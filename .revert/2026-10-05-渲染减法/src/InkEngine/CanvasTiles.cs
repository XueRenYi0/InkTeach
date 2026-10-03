using System.Diagnostics;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InkEngine;

/// <summary>
/// 画布空间的**分块光栅缓存**（tile cache）。
///
/// 这是内容层的最终形态，取代了"一张恰好一屏、绑死在屏幕坐标上的位图"。
///
/// ## 为什么必须分块
///
/// 老做法的病根是**缓存跟着屏幕走**：位图的第 0 行永远等于屏幕的第 0 行，
/// 于是相机一动，整张位图的含义就全变了，只能整层重画。代价和"文档里有多少
/// 笔"成正比——实测一屏量级约 20ms、累积到一万笔 82~154ms，滚一格卡一下。
/// 更糟的是坐标换算散落在整条管线里（脏区、裁剪、快路径各一处），漏一处就出
/// 残影或"写了看不见"，这类 bug 实测踩过四次。
///
/// 分块把这两件事一次解决：
///
///   1. **块在画布空间**。第 (0,0) 块永远是画布上的同一块地方，和窗口、相机
///      无关。滚动只是"换个位置把块贴上去"，**一个像素都不用重画**。
///   2. **一行坐标换算**。块内的变换只有"减块原点"，相机只出现在"合成到后
///      缓冲"那一步。忘记换算从"随机出 bug"变成"根本无处可忘"。
///
/// ## 代价与边界
///
/// - 每块 <see cref="TileSize"/>² × 4 字节显存（256 → 256KB）。
/// - 常驻块数有上限（<see cref="BudgetTiles"/>），超了按最近使用淘汰。
///   这是**有界**内存：不像老做法那样随书写无限涨。
/// - 块边界必须和画布像素**严格对齐**（块原点取整数倍），并且合成时
///   整张图用同一个偏移。否则相邻块的抗锯齿边缘会各画一半，出现"接缝"。
///   这里的做法是：块原点恒为 TileSize 的整数倍，块内变换是整数平移，
///   于是同一段几何在任何一块里的像素覆盖都一样，接缝不存在。
///
/// 同行做法见 计划-底层性能与功能.md 第二十九、三十节（Xournal++ 按页缓存
/// 并按视口窗口淘汰、Rnote 相机 + 视口裁剪）。
/// </summary>
internal sealed class CanvasTileCache : IDisposable
{
    /// <summary>
    /// 分块边长（画布像素）。
    ///
    /// 取 256 有两个理由：一是和 <see cref="SpatialGrid"/> 的格子同宽，一块
    /// 正好落到整数个格子上，查询不会多扫；二是滚轮一格 144 像素，块越小，
    /// "一帧里新露出来的块"就越碎，单帧波峰越低。
    /// </summary>
    /// <summary>
    /// 分块边长（画布像素）。**可调**：用 --tilesize 覆盖，做参数对照用。
    /// 256 和 512 的取舍是"滚动时的单帧波峰"对"大范围重画的开销"：
    /// 块越大，贴图次数越少（大范围重画越快），但一次新露出来的块也越大。
    /// </summary>
    public static int TileSize = 256;

    /// <summary>每块的字节数。</summary>
    public static int TileBytes => TileSize * TileSize * 4;

    /// <summary>
    /// 常驻分块上限。**0 = 自动：可见块数 + 32**。
    ///
    /// 为什么这么定：可见块是**必须**的（一屏 2880×1800 ≈ 96 块 ≈ 24MB，
    /// 和老做法那张整屏位图同量级）；多留 32 块（8MB）专门给"往回滚"——
    /// 往回滚 600 多像素以内的块还在，直接命中，不用重画。再多留就是白占内存了：
    /// 教室机只有 4GB，而往回滚更远的那一下重画一小条也只要几毫秒。
    ///
    /// 这个数是**上界**，不是占用：一块只在真正被看到时才创建。
    /// </summary>
    public int BudgetTiles;

    /// <summary>
    /// 留给"往回滚"的额外显存（字节）。按字节而不是按块数定，这样换块大小
    /// 时这块预算不会跟着变。
    /// </summary>
    public const int ScrollBackBytes = 8 * 1024 * 1024;

    /// <summary>留给"往回滚"的额外块数。</summary>
    public static int ScrollBackMargin => Math.Max(8, ScrollBackBytes / TileBytes);

    /// <summary>一块内容层。持有者：<see cref="CanvasTileCache"/>。</summary>
    internal sealed class Tile
    {
        public int Tx, Ty;
        public ID3D11Texture2D Texture;
        public ID2D1Bitmap1 Target;      // 往这里画
        public ID2D1Bitmap1 Source;      // 从这里贴（同一张纹理的另一个视图）
        /// <summary>true = 内容变了或被淘汰过，下次可见时要重新光栅化。</summary>
        public bool Dirty = true;
        /// <summary>最后一次"被看到"的帧号，用于淘汰。</summary>
        public long LastFrame;

        /// <summary>
        /// 还没画上去的**新笔画**。内容层只增不减时（正在写字），把这几条补画到
        /// 现有画面上就行，不必清空整块重画——重画一块的代价与该块里的笔画条数
        /// 成正比，而"再画一笔"的代价应当只有一笔。
        /// </summary>
        public readonly List<Stroke> Appended = new();
    }

    private readonly Dictionary<long, Tile> _tiles = new();
    private readonly List<Tile> _visible = new();
    private readonly List<KeyValuePair<long, Tile>> _trimScratch = new();
    private ID2D1DeviceContext _ctx;
    private long _frame;

    // ---- 诊断用（性能面板 / 自检）----
    public int Count => _tiles.Count;
    public int VisibleCount => _visible.Count;
    /// <summary>上一帧真正光栅化的块数。</summary>
    public int RasterizedLastFrame { get; private set; }
    /// <summary>上一帧光栅化花的时间（毫秒）。滚动时新露出来的块算在这里。</summary>
    public double RasterMsLastFrame { get; private set; }
    /// <summary>上一帧重画的笔画条数（跨块会重复计）。</summary>
    public int StrokesLastFrame { get; private set; }
    /// <summary>累计光栅化块数（含重复）。</summary>
    public long RasterizedTotal { get; private set; }
    /// <summary>上一帧走"只补画"路径的块数（诊断）。</summary>
    public int AppendedLastFrame { get; private set; }
    /// <summary>累计淘汰块数。</summary>
    public long EvictedTotal { get; private set; }

    /// <summary>这一帧可见的块，按"先创建顺序"排列；合成时遍历它。</summary>
    public List<Tile> Visible => _visible;

    public void Attach(ID2D1DeviceContext ctx) => _ctx = ctx;

    private static long Key(int tx, int ty) => ((long)ty << 32) ^ (uint)tx;

    /// <summary>块在画布坐标里的矩形。原点恒为 TileSize 的整数倍——接缝防线。</summary>
    public static RectF RectOf(int tx, int ty) => new()
    {
        MinX = (float)tx * TileSize, MinY = (float)ty * TileSize,
        MaxX = (float)(tx + 1) * TileSize, MaxY = (float)(ty + 1) * TileSize,
    };

    private static int FirstIdx(float v) => (int)MathF.Floor(v / TileSize);
    private static int LastIdx(float v) => (int)MathF.Ceiling(v / TileSize) - 1;

    /// <summary>
    /// 内容变了：把与这些**画布矩形**相交的块标脏。
    ///
    /// 只标记已经存在的块——不存在的那块本来就是脏的（新建时即脏）。
    /// 注意这里完全不看相机：脏区是画布坐标，块也是画布坐标。
    /// </summary>
    public void MarkDirty(in RectF canvasRect)
    {
        if (canvasRect.IsEmpty) return;
        for (int ty = FirstIdx(canvasRect.MinY); ty <= LastIdx(canvasRect.MaxY); ty++)
            for (int tx = FirstIdx(canvasRect.MinX); tx <= LastIdx(canvasRect.MaxX); tx++)
                if (_tiles.TryGetValue(Key(tx, ty), out var tile))
                {
                    tile.Dirty = true;
                    tile.Appended.Clear();      // 整块都要重画了，补画清单作废
                }
    }

    /// <summary>整层作废（清空、换底色、换分辨率）。纹理留着复用，只置脏。</summary>
    public void MarkAllDirty()
    {
        foreach (var t in _tiles.Values) { t.Dirty = true; t.Appended.Clear(); }
    }

    /// <summary>
    /// 新增一条笔画：只记"补画这一条"，**不清空整块**。
    ///
    /// 只记到**已经存在且不脏**的块上：不存在的块新建时就是脏的、会整块画；
    /// 已经脏的块反正要整块重画，再记一次会画两遍。
    /// </summary>
    public bool MarkAppend(Stroke s)
    {
        var b = s.PaddedBounds;
        if (b.IsEmpty) return false;
        bool any = false;
        for (int ty = FirstIdx(b.MinY); ty <= LastIdx(b.MaxY); ty++)
            for (int tx = FirstIdx(b.MinX); tx <= LastIdx(b.MaxX); tx++)
                if (_tiles.TryGetValue(Key(tx, ty), out var tile) && !tile.Dirty)
                {
                    tile.Appended.Add(s);
                    any = true;
                }
        return any;
    }

    /// <summary>
    /// 把还没画的补画清单作废，改成整块重画。
    /// **结构一变（删、移、撤销、清空）就必须调用**：那些块现在的内容已经
    /// 不是"只差几条新笔画"了，继续往旧画面上补笔会留下早就该消失的墨。
    /// </summary>
    public void FlushAppendsAsDirty()
    {
        foreach (var t in _tiles.Values)
            if (t.Appended.Count > 0) { t.Appended.Clear(); t.Dirty = true; }
    }

    /// <summary>
    /// 同步这一帧的可见块：需要光栅化的（新露出 / 被标脏的）就地光栅化，
    /// 然后按预算淘汰看不见的旧块。
    /// </summary>
    /// <param name="visibleCanvas">当前视口在画布坐标里的范围。</param>
    /// <param name="paint">画一块：参数是要画的块目标、画布矩形、以及"只补画这些笔画"
    /// （null = 整块重画），返回画了几条笔画。</param>
    public void Sync(in RectF visibleCanvas, Func<ID2D1Bitmap1, RectF, List<Stroke>, int> paint)
    {
        _frame++;
        _visible.Clear();
        RasterizedLastFrame = 0;
        AppendedLastFrame = 0;
        RasterMsLastFrame = 0;
        StrokesLastFrame = 0;

        if (visibleCanvas.IsEmpty) return;

        int x0 = FirstIdx(visibleCanvas.MinX), x1 = LastIdx(visibleCanvas.MaxX);
        int y0 = FirstIdx(visibleCanvas.MinY), y1 = LastIdx(visibleCanvas.MaxY);

        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                long k = Key(tx, ty);
                if (!_tiles.TryGetValue(k, out var tile))
                {
                    tile = Create(tx, ty);
                    _tiles[k] = tile;
                }
                tile.LastFrame = _frame;

                if (tile.Dirty)
                {
                    var sw = Stopwatch.StartNew();
                    StrokesLastFrame += paint(tile.Target, RectOf(tx, ty), null);
                    sw.Stop();
                    RasterMsLastFrame += sw.Elapsed.TotalMilliseconds;
                    tile.Dirty = false;
                    tile.Appended.Clear();
                    RasterizedLastFrame++;
                    RasterizedTotal++;
                }
                else if (tile.Appended.Count > 0)
                {
                    // 只补画新笔画：不清空，代价与"新笔画条数"成正比，而不是与块内总数成正比。
                    var sw = Stopwatch.StartNew();
                    StrokesLastFrame += paint(tile.Target, RectOf(tx, ty), tile.Appended);
                    sw.Stop();
                    RasterMsLastFrame += sw.Elapsed.TotalMilliseconds;
                    tile.Appended.Clear();
                    AppendedLastFrame++;
                    RasterizedLastFrame++;
                    RasterizedTotal++;
                }
                _visible.Add(tile);
            }
        }

        Trim();
    }

    /// <summary>
    /// 淘汰看不见的旧块。
    ///
    /// **绝不淘汰这一帧可见的块**——那是正在显示的东西。按最后使用帧号从旧到新
    /// 丢，直到回到预算内。
    /// </summary>
    private void Trim()
    {
        int budget = BudgetTiles > 0
            ? Math.Max(BudgetTiles, _visible.Count + ScrollBackMargin)
            : _visible.Count + ScrollBackMargin;
        if (_tiles.Count <= budget) return;

        _trimScratch.Clear();
        foreach (var kv in _tiles)
            if (kv.Value.LastFrame != _frame) _trimScratch.Add(kv);
        _trimScratch.Sort((a, b) => a.Value.LastFrame.CompareTo(b.Value.LastFrame));

        foreach (var kv in _trimScratch)
        {
            if (_tiles.Count <= budget) break;
            Release(kv.Value);
            _tiles.Remove(kv.Key);
            EvictedTotal++;
        }
    }

    private Tile Create(int tx, int ty)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)TileSize,
            Height = (uint)TileSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        var tile = new Tile { Tx = tx, Ty = ty, Texture = Gfx.Device.CreateTexture2D(desc) };
        var pf = new Vortice.DCommon.PixelFormat(
            Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using (var surface = tile.Texture.QueryInterface<IDXGISurface>())
        {
            // 一张纹理两个视图：作为渲染目标的那张不能同时当绘制源
            // （Direct2D 的规定，老内容层也是这么做的）。
            tile.Target = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
            tile.Source = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.None));
        }
        return tile;
    }

    private static void Release(Tile t)
    {
        t.Source?.Dispose(); t.Target?.Dispose(); t.Texture?.Dispose();
        t.Source = null; t.Target = null; t.Texture = null;
    }

    /// <summary>全部释放（换分辨率/DPI、设备重建时用）。</summary>
    public void Reset()
    {
        foreach (var t in _tiles.Values) Release(t);
        _tiles.Clear();
        _visible.Clear();
    }

    public void Dispose() => Reset();
}
