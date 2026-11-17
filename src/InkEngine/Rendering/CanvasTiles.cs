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
/// - 每块 `(TileSize × RenderScale)`² × 4 字节显存（100% 时 256 → 256KB；
///   200% 时 512 → 1MB，但可见块数少 4 倍，可见显存 ≈ 屏幕像素数）。
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

    /// <summary>
    /// 块纹理的**超采样倍率**（= 相机缩放；见 <see cref="SetRenderScale"/>）。
    ///
    /// 画布矩形永远是 <see cref="TileSize"/>²（块网格、脏区、笔画筛选全按它算），
    /// 只有**纹理**跟着这个倍率变大：200% 时一张纹理 512²，一块画布地方在屏幕上
    /// 正好一格屏幕像素，墨迹和页图都是"按屏幕像素密度"烘的，不会糊。
    ///
    /// 反过来缩到 50% 时纹理是 128²：可见范围变成 4 倍画布面积，但每块纹理只有
    /// 1/4 面积，**可见块的显存 ≈ 屏幕像素数**，不会因为"看得更多"而爆。
    /// </summary>
    public float RenderScale { get; private set; } = 1f;

    /// <summary>一块纹理**内容区**的边长（像素）= round(TileSize × RenderScale)。预算 / 诊断口径按它算。</summary>
    public int TextureSize => Math.Max(1, (int)MathF.Round(TileSize * RenderScale));

    /// <summary>
    /// 块纹理四周多烘的一圈**边像素**（gutter / border texel，Skia / Chromium raster tile 的同款做法；
    /// 2026-11-16 残影评审 P2 / T-a）。
    ///
    /// 为什么要：缩放手势中（相机倍率 ≠ 块纹理倍率，见 CompositeTiles）按旧倍率 Linear 拉伸，
    /// 双线性采样在块边上要读"块外面"那一列——没有 gutter 时读到的是纹理外的透明，屏幕上就是
    /// 一张淡淡的网格（停手 120ms 换倍率后消失，用户会说成"有脏影"）。现在块外那一圈也按真实内容
    /// 烘进来（光栅化矩形外扩 <see cref="GutterCanvas"/>），采样读到的就是邻块同一处的像素。
    ///
    /// **不渗色的前提**：邻块的改动也得把这一圈重烘——所以 MarkDirty / MarkAppend 都按外扩后的矩形
    /// 找块。1:1 时合成走 NearestNeighbor、只取内容区，gutter 一个像素都不上屏。
    /// </summary>
    public const int GutterTexels = 2;

    /// <summary>gutter 折算成画布像素（= GutterTexels / RenderScale）。</summary>
    public float GutterCanvas => GutterTexels / RenderScale;

    /// <summary>真正分配的纹理边长 = 内容区 + 两侧 gutter。</summary>
    public int AllocTextureSize => TextureSize + 2 * GutterTexels;

    /// <summary>
    /// 每块纹理的字节数（跟着 <see cref="RenderScale"/> 走，预算按它算）。
    /// **口径是内容区**，不含 gutter（实占多 ≈3%@100%、≈1.6%@200%）——预算 / 自检的数字保持原样。
    /// </summary>
    public int TileBytes => TextureSize * TextureSize * 4;

    /// <summary>
    /// 换超采样倍率：**一次换、整层重烘**（旧纹理尺寸对不上，留着就是花屏）。
    ///
    /// 只在倍率真的变了的时候动手——相机每帧都会把 ViewScale 递进来，这里必须
    /// 是幂等的，否则等于每帧重建整层缓存（那比不缓存还慢）。
    /// </summary>
    public void SetRenderScale(float scale)
    {
        if (!(scale > 0f)) scale = 1f;
        if (MathF.Abs(scale - RenderScale) < 0.0005f) return;
        RenderScale = scale;
        Reset();          // 纹理尺寸变了：旧纹理一个都不能留（内容/显存一起放）
        // Reset 只是把 C# 这边的引用放了；D2D 上下文里可能还挂着上一批纹理的批处理引用，
        // 不冲一次的话显存要等下一帧 EndDraw 才真的掉下来。Flush 只是"把攒着的命令交出去"，
        // 不阻塞 GPU（ID2D1DeviceContext::Flush 的语义）。
        try { _ctx?.Flush(out _, out _); }
        catch { /* 有意忽略：Flush 失败只影响显存回收时点，不影响画面 */ }
    }

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

    /// <summary>留给"往回滚"的额外块数（按当前倍率的块大小算）。</summary>
    public int ScrollBackMargin => Math.Max(8, ScrollBackBytes / TileBytes);

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
    /// <summary>上一帧光栅化花的时间（毫秒，含新块纹理创建）。滚动时新露出来的块算在这里。</summary>
    public double RasterMsLastFrame { get; private set; }
    /// <summary>上一帧“只补画新笔画”那条快路径花的时间（毫秒）。</summary>
    public double AppendedMsLastFrame { get; private set; }
    /// <summary>上一帧重画的笔画条数（跨块会重复计）。</summary>
    public int StrokesLastFrame { get; private set; }
    /// <summary>累计光栅化块数（含重复）。</summary>
    public long RasterizedTotal { get; private set; }
    /// <summary>上一帧走"只补画"路径的块数（诊断）。</summary>
    public int AppendedLastFrame { get; private set; }
    /// <summary>累计淘汰块数。</summary>
    public long EvictedTotal { get; private set; }

    // ---- 视口外一圈的预取（诊断 / 调度）------------------------------------
    /// <summary>
    /// 视口外一圈的**预取**（对齐上游：Xournal++ 预载前后页、Rnote 视口 0.4 余量预渲染）。
    ///
    /// 为什么需要：新露出来的块现在是**当帧同步光栅**的——密集页快速翻屏时一帧要烘十几块
    /// （每块可能几百条笔画），弱机上就是一次可见的卡顿。预取把这件事摊到"停手/空闲"的
    /// 那几拍里：每拍最多烘 <see cref="PrefetchPerFrame"/> 块，等真滚到那里时块已经热了，
    /// 那一帧只做合成（实测 `--prefetchtest`：再滚一格 `LastPatchCount == 0`）。
    ///
    /// 边界：只烘**视口外扩一圈**（含四角），不是全画布；预算里给这圈留了位置（见 BudgetNow）。
    /// 关掉它（`--noprefetch`）行为回到纯同步光栅。
    /// </summary>
    public static bool PrefetchEnabled = true;

    /// <summary>每拍最多烘几块。取 2：一拍最多加约 0.4~2ms，摊在 4ms 的节拍里不显眼。</summary>
    public static int PrefetchPerFrame = 2;

    /// <summary>上一拍预取了几块 / 累计 / 上一拍耗时 / 环里还差多少。</summary>
    public int LastPrefetch { get; private set; }
    public long PrefetchTotal { get; private set; }
    public double PrefetchMsLastFrame { get; private set; }
    public bool PrefetchPending { get; private set; }

    /// <summary>
    /// 预取环的**字节上限**：16MB。环是按"视口外一圈"的块数算的
    /// （<see cref="RingFullTiles"/> = 2×列 + 2×行 + 4），块数和倍率的平方成正比，
    /// 200% 时一圈就是 100% 时的 1/4 块数、但每块 4 倍字节——不夹住的话环会跟着
    /// 倍率一起涨。**可见块和回滚余量不在这里夹**（它们是画面本身）。
    /// </summary>
    public const int RingBytes = 16 * 1024 * 1024;

    /// <summary>按当前 <see cref="TileBytes"/> 折算，环最多允许几块。</summary>
    public int RingCapTiles => Math.Max(0, RingBytes / Math.Max(1, TileBytes));

    /// <summary>视口外一圈完整块数（上 + 下 + 左 + 右 + 四角）。</summary>
    public int RingFullTiles => 2 * _visCols + 2 * _visRows + 4;

    /// <summary>
    /// 真正参与预取的环块数 = min(完整环, 字节上限)。
    /// 100% 时一般在 40~60 块（&lt; 16MB，不夹）；200% 时被夹到 16 块。
    /// </summary>
    public int RingTiles => PrefetchEnabled ? Math.Min(RingFullTiles, RingCapTiles) : 0;

    /// <summary>环占的字节数（诊断/自检口径；上界就是 <see cref="RingBytes"/>）。</summary>
    public int RingBytesNow => RingTiles * TileBytes;

    /// <summary>
    /// 本帧的常驻预算（块数）：可见 ＋ 回滚余量 ＋ 预取环。
    /// 预取的块要能在原地等到"被滚到"，所以预算必须把环算进去，否则刚烘好的块会被 Trim 淘汰。
    /// 手动设了 <see cref="BudgetTiles"/> 时它仍然是**下限**（和原来同一条口径）。
    /// </summary>
    public int BudgetNow => Math.Max(BudgetTiles, _visible.Count + ScrollBackMargin + RingTiles);

    /// <summary>这一帧可见的块，按"先创建顺序"排列；合成时遍历它。</summary>
    public List<Tile> Visible => _visible;

    /// <summary>本帧可见块的行列数（预算里的"环"按它算）。</summary>
    private int _visCols, _visRows;

    /// <summary>
    /// 环的块坐标（**按字节上限截断**）：先上下两条、再左右两条，凑够
    /// <see cref="RingTiles"/> 就停。预取和"还需不需要预取"必须走同一份清单，
    /// 否则两边对不上就会出现"永远欠着、永远不烘"。
    /// </summary>
    private readonly List<(int Tx, int Ty)> _ringScratch = new();

    private void BuildRing(int x0, int x1, int y0, int y1)
    {
        _ringScratch.Clear();
        int limit = RingTiles;
        if (limit <= 0) return;

        void Add(int tx, int ty)
        {
            if (_ringScratch.Count < limit) _ringScratch.Add((tx, ty));
        }

        for (int tx = x0 - 1; tx <= x1 + 1; tx++) Add(tx, y0 - 1);   // 上
        for (int tx = x0 - 1; tx <= x1 + 1; tx++) Add(tx, y1 + 1);   // 下
        for (int ty = y0; ty <= y1; ty++) Add(x0 - 1, ty);           // 左
        for (int ty = y0; ty <= y1; ty++) Add(x1 + 1, ty);           // 右
    }

    public void Attach(ID2D1DeviceContext ctx) => _ctx = ctx;

    private static long Key(int tx, int ty) => ((long)ty << 32) ^ (uint)tx;

    /// <summary>块在画布坐标里的矩形。原点恒为 TileSize 的整数倍——接缝防线。</summary>
    public static RectF RectOf(int tx, int ty) => new()
    {
        MinX = (float)tx * TileSize, MinY = (float)ty * TileSize,
        MaxX = (float)(tx + 1) * TileSize, MaxY = (float)(ty + 1) * TileSize,
    };

    /// <summary>
    /// 光栅化一块时交给 paint 的画布矩形 = <see cref="RectOf"/> 外扩一圈 gutter。
    /// 纹理像素 (0,0) 对应这个矩形的左上角，块本身的左上角落在纹理像素 (G, G)。
    /// </summary>
    public RectF PaintRectOf(int tx, int ty) => RectOf(tx, ty).Inflate(GutterCanvas);

    private static int FirstIdx(float v) => (int)MathF.Floor(v / TileSize);
    private static int LastIdx(float v) => (int)MathF.Ceiling(v / TileSize) - 1;

    /// <summary>
    /// 内容变了：把与这些**画布矩形**相交的块标脏。
    ///
    /// 只标记已经存在的块——不存在的那块本来就是脏的（新建时即脏）。
    /// 注意这里完全不看相机：脏区是画布坐标，块也是画布坐标。
    /// </summary>
    public void MarkDirty(in RectF changed)
    {
        if (changed.IsEmpty) return;
        // 外扩 gutter：改动落在邻块边上时，邻块纹理里那一圈 gutter 也要重烘（否则缩放中 Linear 采样渗旧色）
        var canvasRect = changed.Inflate(GutterCanvas);
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
        b = b.Inflate(GutterCanvas);         // 同 MarkDirty：邻块的 gutter 里也有这一笔
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
        AppendedMsLastFrame = 0;
        StrokesLastFrame = 0;

        if (visibleCanvas.IsEmpty) return;

        int x0 = FirstIdx(visibleCanvas.MinX), x1 = LastIdx(visibleCanvas.MaxX);
        int y0 = FirstIdx(visibleCanvas.MinY), y1 = LastIdx(visibleCanvas.MaxY);

        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                long k = Key(tx, ty);
                double createMs = 0;
                if (!_tiles.TryGetValue(k, out var tile))
                {
                    var swc = Stopwatch.StartNew();
                    tile = Create(tx, ty);
                    swc.Stop();
                    createMs = swc.Elapsed.TotalMilliseconds;
                    _tiles[k] = tile;
                }
                tile.LastFrame = _frame;

                if (tile.Dirty)
                {
                    var sw = Stopwatch.StartNew();
                    StrokesLastFrame += paint(tile.Target, PaintRectOf(tx, ty), null);
                    sw.Stop();
                    RasterMsLastFrame += sw.Elapsed.TotalMilliseconds + createMs;
                    tile.Dirty = false;
                    tile.Appended.Clear();
                    RasterizedLastFrame++;
                    RasterizedTotal++;
                }
                else if (tile.Appended.Count > 0)
                {
                    // 只补画新笔画：不清空，代价与"新笔画条数"成正比，而不是与块内总数成正比。
                    var sw = Stopwatch.StartNew();
                    StrokesLastFrame += paint(tile.Target, PaintRectOf(tx, ty), tile.Appended);
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    RasterMsLastFrame += ms;
                    AppendedMsLastFrame += ms;
                    tile.Appended.Clear();
                    AppendedLastFrame++;
                    RasterizedLastFrame++;
                    RasterizedTotal++;
                }
                _visible.Add(tile);
            }
        }

        // 记录可见块的行列数：预算里的"预取环"按它算（见 BudgetNow / RingTiles）。
        _visCols = Math.Max(0, x1 - x0 + 1);
        _visRows = Math.Max(0, y1 - y0 + 1);

        Trim();
    }

    /// <summary>
    /// 视口外一圈里还有没有"欠着的块"（不存在或脏）。只做扫描，不光栅。
    /// 引擎用它决定要不要排空闲预取拍。
    /// </summary>
    public bool PrefetchNeeded(in RectF visibleCanvas)
    {
        if (!PrefetchEnabled || PrefetchPerFrame <= 0 || visibleCanvas.IsEmpty) return false;

        int x0 = FirstIdx(visibleCanvas.MinX), x1 = LastIdx(visibleCanvas.MaxX);
        int y0 = FirstIdx(visibleCanvas.MinY), y1 = LastIdx(visibleCanvas.MaxY);
        BuildRing(x0, x1, y0, y1);
        foreach (var (tx, ty) in _ringScratch)
            if (!_tiles.TryGetValue(Key(tx, ty), out var t) || t.Dirty) return true;
        return false;
    }

    /// <summary>
    /// 预取一拍：只烘**视口外一圈**里欠着的块，最多 <see cref="PrefetchPerFrame"/> 块。
    /// 不碰可见块、不合成、不上屏（上游的预渲染思路，见 <see cref="PrefetchEnabled"/>）。
    /// 返回 true = 环里还有活（引擎再排下一拍）。
    /// </summary>
    public bool Prefetch(in RectF visibleCanvas, Func<ID2D1Bitmap1, RectF, List<Stroke>, int> paint)
    {
        LastPrefetch = 0;
        PrefetchMsLastFrame = 0;
        PrefetchPending = false;
        if (!PrefetchNeeded(visibleCanvas)) return false;

        int x0 = FirstIdx(visibleCanvas.MinX), x1 = LastIdx(visibleCanvas.MaxX);
        int y0 = FirstIdx(visibleCanvas.MinY), y1 = LastIdx(visibleCanvas.MaxY);
        BuildRing(x0, x1, y0, y1);

        foreach (var (tx, ty) in _ringScratch)
        {
            long k = Key(tx, ty);
            if (_tiles.TryGetValue(k, out var tile) && !tile.Dirty) continue;

            if (LastPrefetch >= PrefetchPerFrame) { PrefetchPending = true; continue; }

            double createMs = 0;
            if (tile == null)
            {
                var swc = Stopwatch.StartNew();
                tile = Create(tx, ty);
                swc.Stop();
                createMs = swc.Elapsed.TotalMilliseconds;
                _tiles[k] = tile;
            }
            var sw = Stopwatch.StartNew();
            paint(tile.Target, PaintRectOf(tx, ty), null);     // 整块重画；新块/脏块都不会有补画清单
            sw.Stop();
            PrefetchMsLastFrame += sw.Elapsed.TotalMilliseconds + createMs;
            tile.Dirty = false;
            tile.Appended.Clear();
            tile.LastFrame = _frame;                      // 刚烘的算新，Trim 不会先淘汰它
            LastPrefetch++;
            PrefetchTotal++;
        }
        return PrefetchPending;
    }

    /// <summary>
    /// 淘汰看不见的旧块。
    ///
    /// **绝不淘汰这一帧可见的块**——那是正在显示的东西。按最后使用帧号从旧到新
    /// 丢，直到回到预算内。预算把预取环也算进去了（见 <see cref="BudgetNow"/>）。
    /// </summary>
    private void Trim()
    {
        int budget = BudgetNow;
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
            Width = (uint)AllocTextureSize,
            Height = (uint)AllocTextureSize,
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
