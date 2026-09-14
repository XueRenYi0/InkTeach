using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

public enum Tool
{
    Pen = 0,
    Highlighter = 1,
    Laser = 2,
    /// <summary>笔记橡皮擦：碰到哪一条就整条删掉（精准擦除，见 EraserOps 的表格）。</summary>
    Eraser = 3,
    Marquee = 4,
    Line = 5,
    Rectangle = 6,
    Ellipse = 7,
    Arrow = 8,
    /// <summary>面积橡皮擦：黄金分割比的矩形范围，范围内的墨被切掉。</summary>
    AreaEraser = 9,
    /// <summary>截图：拖一个框，把屏幕那一块抓成图像对象。</summary>
    Capture = 10,
    Parallelogram = 11,
    Circle = 12,
    Triangle = 13,
}

/// <summary>An axis-aligned rectangle in virtual-desktop pixels.</summary>
public struct RectF
{
    public float MinX, MinY, MaxX, MaxY;

    public static RectF Empty => new()
    {
        MinX = float.MaxValue, MinY = float.MaxValue,
        MaxX = float.MinValue, MaxY = float.MinValue,
    };

    public bool IsEmpty => MaxX < MinX;

    public void Add(float x, float y)
    {
        if (x < MinX) MinX = x;
        if (y < MinY) MinY = y;
        if (x > MaxX) MaxX = x;
        if (y > MaxY) MaxY = y;
    }

    public void Add(RectF other)
    {
        if (other.IsEmpty) return;
        if (other.MinX < MinX) MinX = other.MinX;
        if (other.MinY < MinY) MinY = other.MinY;
        if (other.MaxX > MaxX) MaxX = other.MaxX;
        if (other.MaxY > MaxY) MaxY = other.MaxY;
    }

    public bool Intersects(RectF o)
        => !IsEmpty && !o.IsEmpty && MinX <= o.MaxX && MaxX >= o.MinX && MinY <= o.MaxY && MaxY >= o.MinY;

    public RectF Inflate(float d) => new()
    {
        MinX = MinX - d, MinY = MinY - d, MaxX = MaxX + d, MaxY = MaxY + d,
    };
}

/// <summary>
/// The set of screen regions whose pixels are stale. Tracking regions instead
/// of a single "something changed" flag is what makes erasing and undoing
/// cheap: only the area an edit actually touched is cleared and re-rasterised.
/// </summary>
internal sealed class DirtyRegion
{
    /// <summary>
    /// 脏矩形数量的上限。超过就压缩，但**不会压成一个**（见 Coalesce）。
    /// 取 32：拖 20 个对象会产生 40 个旧位/新位矩形，压到 32 就够用了。
    /// </summary>
    private const int MaxRects = 32;
    private readonly List<RectF> _rects = new();

    public bool Full { get; private set; }
    public IReadOnlyList<RectF> Rects => _rects;

    public void Add(RectF r)
    {
        if (Full || r.IsEmpty) return;

        // Merge into any overlapping rectangle, then keep merging until stable,
        // so a long erase drag collapses into a handful of boxes instead of
        // growing without bound.
        for (int i = 0; i < _rects.Count; i++)
        {
            if (!_rects[i].Intersects(r)) continue;
            var merged = _rects[i];
            merged.Add(r);
            _rects.RemoveAt(i);
            r = merged;
            i = -1;
        }

        _rects.Add(r);
        if (_rects.Count > MaxRects)
        {
            // 超上限：把**刚加进来的这一个**并进"并起来最小"的那个已有矩形，
            // 而不是跑一遍全局合并。
            //
            // 为什么：Add 可能每帧被调上万次（拖动一万个对象 = 2 万次旧位/新位）。
            // 全局合并是 O(k³)，每帧上万次就是上亿次运算——直接把帧率打死。
            // 上一版就是这么错的：Coalesce 从 O(k) 改成 O(k³) 之后没能塌缩成
            // 一个矩形，于是超限之后的每次 Add 都触发一遍。这里改成 O(k)。
            MergeIntoCheapest(r);
        }
    }

    /// <summary>
    /// 把一个矩形并进"并起来面积最小"的那个已有矩形。O(k)，k ≤ MaxRects。
    /// 合并而不是丢弃：丢掉的区域就永远不会被重画，屏幕上会留下擦不掉的残影。
    /// </summary>
    private void MergeIntoCheapest(RectF r)
    {
        _rects.RemoveAt(_rects.Count - 1);      // 刚加进来的那个，重新分配

        int best = 0;
        float bestArea = float.MaxValue;
        for (int i = 0; i < _rects.Count; i++)
        {
            var u = _rects[i];
            u.Add(r);
            float area = (u.MaxX - u.MinX) * (u.MaxY - u.MinY);
            if (area < bestArea) { bestArea = area; best = i; }
        }
        var merged = _rects[best];
        merged.Add(r);
        _rects[best] = merged;
    }

    public void MarkFull()
    {
        Full = true;
        _rects.Clear();
    }

    public void Reset()
    {
        Full = false;
        _rects.Clear();
    }

}

internal enum StrokeKind
{
    Freehand = 0,
    Line = 1,
    Rectangle = 2,
    Ellipse = 3,
    Arrow = 4,
    Parallelogram = 5,
    Circle = 6,
    Triangle = 7,
    /// <summary>图像对象（截图 / 粘贴）。像素挂在 <see cref="Stroke.Image"/> 上。</summary>
    Image = 8,
}

internal struct InkPoint
{
    public float X, Y;      // virtual-desktop pixels
    public float P;         // pressure 0..1
    public double T;        // ms timestamp
}

/// <summary>A single drawn item. Freehand strokes are filled ribbons so that
/// pressure can vary the width; shapes are stroked outlines.</summary>
internal sealed class Stroke
{
    public Tool Tool;
    public StrokeKind Kind = StrokeKind.Freehand;
    public Color4 Color;
    public float Width;
    public readonly List<InkPoint> Points = new();

    public ID2D1Geometry Geometry;

    /// <summary>
    /// 图像对象的像素（只有 <see cref="StrokeKind.Image"/> 有）。
    /// **核心只把它当"一块要画上去的图"**，不解释内容、不做解码
    /// （见 ImageData 的注释：来源是 GDI 截图或剪贴板 CF_DIB，都是 BGRA）。
    /// </summary>
    public ImageData Image;

    /// <summary>是不是图像对象。渲染、命中测试、顶点编辑三处都要按它分流。</summary>
    public bool IsImage => Kind == StrokeKind.Image;

    /// <summary>
    /// Direct2D 的"几何实现"（geometry realization）：把几何**细分（三角化）之后**
    /// 的结果缓存成一个设备相关对象。普通 ID2D1PathGeometry 只是数学描述，
    /// 每次 FillGeometry 都要重新细分；realization 把这一步做成一次性的，
    /// 之后每次绘制只是提交已经算好的三角形。
    /// 注意：它是**设备相关**的，多个窗口（多块屏）时需要各存一份。
    /// </summary>
    public ID2D1GeometryRealization Realization;

    public ID2D1GeometryRealization GetRealization(ID2D1DeviceContext1 ctx1, float tolerance)
    {
        // 几何一变（正在书写的那一笔会一直变），缓存就必须作废
        if (Realization != null && _realizationRevision == Revision) return Realization;
        if (ctx1 == null) return null;
        ReleaseRealization();

        // 细分缓存的真实成本 ≈ 每个点 4KB（实测：88 点的笔画约 350KB，
        // 而不是当初估的"每个 18KB"——差了 19 倍，这就是内存居高不下的主因）。
        // 按**点数**记账，超预算就退回"每次重新细分"：宁可慢一点，
        // 也不能让内存无上限增长。
        if (LiveRealizations >= MaxRealizations) return null;
        if (LiveRealizationPoints + Points.Count > RealizationPointsBudget) return null;

        var geo = BuildGeometry(Gfx.D2DFactory);
        if (geo == null) return null;
        try
        {
            Realization = ctx1.CreateFilledGeometryRealization(geo, tolerance);
            if (Realization != null)
            {
                LiveRealizations++;
                LiveRealizationPoints += Points.Count;
            }
        }
        catch { Realization = null; }
        _realizationRevision = Revision;
        return Realization;
    }

    /// <summary>最近一次被画到的帧号（淘汰细分缓存时判断"还热不热"）。</summary>
    public long LastDrawnFrame;

    /// <summary>丢掉细分缓存，保留路径几何。</summary>
    public void ReleaseRealization()
    {
        if (Realization == null) return;
        Realization.Dispose();
        Realization = null;
        LiveRealizations--;
        LiveRealizationPoints -= Points.Count;
    }

    private int _realizationRevision = -1;

    /// <summary>当前存活的细分缓存数量与上限（跨所有笔画）。</summary>
    public static int LiveRealizations;
    public static int MaxRealizations = 4096;

    /// <summary>
    /// 细分缓存的总点数预算（近似等于内存：每点约 4KB）。
    /// 12000 点 ≈ 48MB，大约够"当前这一屏"的笔画用——缓存的意义本来就在
    /// "刚画过的还会再画"，不在"全都留着"。
    /// </summary>
    public static long RealizationPointsBudget = 6_000;
    public static long LiveRealizationPoints;

    /// <summary>测量用：&gt;0 时直接顶掉"按对象数自动算"的预算。</summary>
    public static int MaxRealizationsOverride;

    /// <summary>
    /// 几何画完之后是否保留。
    ///
    /// **false = 画完立刻释放**（重建一条几何只要 3µs，见 reports/inkprobe-report.txt：
    /// build geometry 34.5ms / 10000 条）。这是给 --memab 做 A/B 用的开关，
    /// 回答一个之前没答对的问题：「每条笔画常驻一份几何，到底占不占显存？」
    ///
    /// 它和"过几帧再淘汰"有本质区别：那是**反复建销**，会撞上 D2D 分配器的棘轮
    /// （释放过的块被留着复用，常驻反而更高，这一条我们踩过）。这里是**一次性**
    /// 释放，之后只有那块被重画时才重建。
    /// </summary>
    public static bool KeepGeometry = true;

    /// <summary>
    /// 画完之后缓存放哪一档。默认 <see cref="RetentionMode.KeepRealization"/>：
    /// 细分缓存留着（重建贵、只有 18KB），路径几何丢掉（重建便宜、常驻却要几百 KB）。
    /// 实测 720 笔：全留 393MB / 只留细分 145MB，而每帧记录耗时没有差别。
    /// </summary>
    public enum RetentionMode
    {
        /// <summary>全留（老行为，只用于对照测量）。</summary>
        KeepAll,
        /// <summary>只留细分缓存。默认。</summary>
        KeepRealization,
        /// <summary>全丢（最快的回收，但也丢掉细分缓存）。</summary>
        KeepNone,
    }

    public static RetentionMode Retention = RetentionMode.KeepRealization;
    /// <summary>
    /// 几何包围盒，**局部坐标**（对象自己的坐标系，不看 Transform）。
    ///
    /// 要"这个对象在画布上占多大地方"用 <see cref="WorldBounds"/>。
    /// 这两个概念分开是刻意的：变换一变，局部包围盒不该跟着变。
    /// </summary>
    public RectF Bounds = RectF.Empty;

    /// <summary>
    /// 稳定身份，**只增不减、永不复用**。撤销 / 多选 / 复制 / 序列化都靠它：
    /// 用引用相等做不了（复制会产生新对象），用列表下标也不行（删除会错位）。
    /// 0 表示"还没分配"，由文档在入册时给。
    /// </summary>
    public int Id;

    /// <summary>
    /// 局部坐标 → 画布坐标。**移动 / 缩放 / 旋转 / 镜像都只改这个矩阵，
    /// 几何一个点都不动。**
    ///
    /// 这是这一层最重要的约定，它带来三件事：
    ///   ① 变换是 O(1)，跟笔画有多少个点无关；
    ///   ② GPU 几何缓存不用失效（缓存的是局部坐标下的几何）；
    ///   ③ "放大再缩小"能精确回到原样，不会累积浮点损失。
    ///
    /// 反过来做（把变换烘焙进点坐标）会自动继承 InkClass 的两个毛病：
    /// 每次变换都要重写全部点、而且缩放两次之后形状就回不去了。
    ///
    /// 默认单位矩阵：几何直接写在画布坐标里（现存数据就是这个状态）。
    /// </summary>
    public Matrix3x2 Transform = Matrix3x2.Identity;

    private RectF _worldBounds = RectF.Empty;
    private int _worldBoundsRevision = -1;
    private Matrix3x2 _worldBoundsTransform = Matrix3x2.Identity;

    /// <summary>
    /// 画布坐标下的包围盒 = 局部包围盒经 <see cref="Transform"/> 变换之后。
    ///
    /// **脏区、命中测试、空间索引、框选一律用它**，不要用 <see cref="Bounds"/>。
    ///
    /// 两个容易写错的地方：
    ///   ① 非等比缩放 / 旋转 / 镜像之后**不能只变换两个角点**——那得到的是
    ///      "以对角线为边的矩形"，会偏小，脏区就会留下残影。这里变换四个角取并集。
    ///   ② 它有缓存。拖动时每帧都会问它，不能每次都算。
    /// </summary>
    public RectF WorldBounds
    {
        get
        {
            if (_worldBoundsRevision == Revision && _worldBoundsTransform.Equals(Transform))
                return _worldBounds;

            _worldBounds = TransformRect(Bounds, Transform);
            _worldBoundsRevision = Revision;
            _worldBoundsTransform = Transform;
            return _worldBounds;
        }
    }

    /// <summary>把矩形按矩阵变换后取四个角的并集（旋转 / 镜像 / 非等比都正确）。</summary>
    private static RectF TransformRect(RectF r, in Matrix3x2 m)
    {
        if (r.IsEmpty) return RectF.Empty;
        if (m.IsIdentity) return r;

        var p0 = Vector2.Transform(new Vector2(r.MinX, r.MinY), m);
        var p1 = Vector2.Transform(new Vector2(r.MaxX, r.MinY), m);
        var p2 = Vector2.Transform(new Vector2(r.MaxX, r.MaxY), m);
        var p3 = Vector2.Transform(new Vector2(r.MinX, r.MaxY), m);

        var box = RectF.Empty;
        box.Add(p0.X, p0.Y);
        box.Add(p1.X, p1.Y);
        box.Add(p2.X, p2.Y);
        box.Add(p3.X, p3.Y);
        return box;
    }

    /// <summary>
    /// 优化器算好的闭合轮廓（虚拟桌面坐标）。**核心不产生它，只在有值时使用。**
    ///
    /// 有它就按填充多边形画，笔迹的形状完全由优化器决定（速度→粗细、起收笔
    /// 渐细、圆头端帽、拐角圆弧，全都体现在这一圈点里）。为 null 时核心画
    /// 最朴素的样子：原始采样点连成的等宽带子。
    /// </summary>
    public Vector2[] Outline;

    /// <summary>
    /// 优化器写入的逐点宽度（直径，物理像素）。**核心自己不读**，只给自检与
    /// 调试用。存在笔画自己身上而不是优化器的静态字段里——静态字段会被下一条
    /// 笔画覆盖，自检就会读到别人的数据（实测栽过，来回查了好几轮）。
    /// </summary>
    public float[] BeautifiedWidths;

    /// <summary>
    /// 笔迹实际可能超出名义笔宽多少（倍数），脏区与命中测试要用。
    ///
    /// 默认 1.4：压感把宽度放大到 0.6 + 0.8×P 的上限。优化器装上之后会把它
    /// 调大一些（圆头端帽、粗糙边缘会让轮廓再往外扩），由优化器自己设置。
    ///
    /// 取小了不是"笔迹看着细"，而是**脏区算小、快速书写留下残影**——
    /// 这类 bug 很显眼又难查，所以宁可留足。
    /// </summary>
    public float BoundsInflateFactor = MaxWidthFactor;

    /// <summary>
    /// 当前使用的笔锋预设。**核心只负责记住它**，怎么解释这个预设是优化器的事
    /// （核心连 PenPreset 的具体风格都不认识）。没装优化器时它是无意义的。
    /// </summary>
    public PenPreset Preset = PenPreset.Precise;

    /// <summary>Bumped whenever the shape of this item changes. The cached GPU
    /// geometry is only trusted while it matches, which is what makes "add a
    /// point, redraw" work while a stroke is still being drawn.</summary>
    public int Revision { get; private set; }
    private int _builtRevision = -1;

    /// <summary>Scratch field used by the spatial index to avoid returning the
    /// same stroke twice for one query without allocating a set.</summary>
    public int QueryStamp;

    public static long ReleasedGeometries;

    /// <summary>
    /// 当前**存活**的几何对象数（诊断用）。
    ///
    /// 删了东西内存不降时，第一个要问的就是"几何释放了没有"。只有
    /// ReleasedGeometries（累计释放）看不出这个——累计值是单调涨的，
    /// 释放了 100 条又新建了 100 条，它照样涨。
    /// </summary>
    public static long LiveGeometries;

    /// <summary>Releases the cached Direct2D geometry.</summary>
    public void Release()
    {
        if (Geometry != null)
        {
            ReleasedGeometries++;
            Geometry.Dispose();
            Geometry = null;
            LiveGeometries--;
        }
        ReleaseRealization();
        // 图像对象还挂着一张 D2D 位图（可能很大：一张 800×600 的截图约 2MB）。
        // 漏掉这一句，"擦掉截图之后内存不降"就是必然的。释放之后再画会按需重建。
        Image?.Release();
        _builtRevision = -1;
    }

    /// <summary>
    /// 只丢路径几何，**保留细分缓存**（geometry realization）。
    ///
    /// 为什么要分开：这两样东西的性价比差一个数量级。
    ///   · 路径几何只是"数学描述"，重建一次约 3.4µs（一万条约 34ms），
    ///     但它挂在笔画上常驻时，实测**每条要占 300KB 上下**（连同驱动
    ///     为它维护的内部数据）——720 笔就多出 250MB。
    ///   · 细分缓存是"三角化之后的结果"，重建贵（这才是缓存的意义），
    ///     但只有 18KB 左右，而且本来就有条数上限。
    ///
    /// 所以最优解是"留着细分缓存、丢掉路径几何"：慢的那部分已经缓存住了，
    /// 贵的那部分（常驻内存）释放掉。
    /// </summary>
    public void ReleaseGeometry()
    {
        if (Geometry == null) return;
        ReleasedGeometries++;
        Geometry.Dispose();
        Geometry = null;
        LiveGeometries--;
    }

    /// <summary>
    /// 深拷贝（复制 / 粘贴用）。**Id 留 0**，由文档入册时分配——
    /// 复制出来的必须是新身份，否则撤销和多选会指向错的对象。
    ///
    /// 轮廓（Outline）是共享的，不复制：它是优化器算完就不再改的只读数组，
    /// 一条笔画几百个点，复制一份只是白白吃内存。
    /// </summary>
    public Stroke Clone()
    {
        var c = new Stroke
        {
            Tool = Tool,
            Kind = Kind,
            Color = Color,
            Width = Width,
            Preset = Preset,
            Transform = Transform,
            BoundsInflateFactor = BoundsInflateFactor,
        };
        // 用 AddPoint 加：它会顺便把 Bounds 和 Revision 收拾好。
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            c.AddPoint(p.X, p.Y, p.P, p.T);
        }
        c.Outline = Outline;
        c.BeautifiedWidths = BeautifiedWidths;
        // 图像像素**共享不复制**：一张截图几兆字节，复制一份纯属浪费；
        // 而且像素是不可变的（没有任何代码会改它），共享没有风险。
        c.Image = Image;
        return c;
    }

    public void AddPoint(float x, float y, float p, double t)
    {
        Points.Add(new InkPoint { X = x, Y = y, P = p, T = t });
        Bounds.Add(x, y);
        Revision++;
    }

    /// <summary>Shapes are defined by their first and last point only.</summary>
    public void SetEnd(float x, float y)
    {
        if (Points.Count == 0) { AddPoint(x, y, 1f, 0); return; }
        if (Points.Count == 1) AddPoint(x, y, 1f, 0);
        else
        {
            var p = Points[1];
            p.X = x; p.Y = y;
            Points[1] = p;
        }
        RecomputeBounds();
        Revision++;
    }

    /// <summary>
    /// 整批换掉控制点（**图形的定义**从这里改）。
    ///
    /// 和 <see cref="ReplacePoints"/> 的分开点是语义：那个是"抽稀"（自由笔迹的
    /// 采样点变少），这个是"改形状的定义"（矩形换个对角）。代码一样，
    /// 但调用点的意图完全不同，分两个名字读起来才不会误解。
    /// </summary>
    public void SetPoints(Vector2[] pts)
    {
        Points.Clear();
        for (int i = 0; i < pts.Length; i++)
            Points.Add(new InkPoint { X = pts[i].X, Y = pts[i].Y, P = 1f, T = 0 });
        RecomputeBounds();
        Revision++;
    }

    /// <summary>只挪一个控制点（顶点拖动走这里，**不动其它点、不动变换**）。</summary>
    public void SetPoint(int index, Vector2 p)
    {
        if (index < 0 || index >= Points.Count) return;
        var v = Points[index];
        v.X = p.X; v.Y = p.Y;
        Points[index] = v;
        RecomputeBounds();
        Revision++;
    }

    /// <summary>
    /// 包围盒重算。它同时是脏区、命中测试和空间索引的依据，改点之后必须重算。
    ///
    /// **图形不能"取控制点的并集"**：圆的控制点是圆心 + 圆周一点，那样的并集只是
    /// 半径那么小块方框。图形的真实范围交给 <see cref="ShapeGeometry.BoundsOf"/>。
    /// </summary>
    private void RecomputeBounds()
    {
        if (Kind != StrokeKind.Freehand && Kind != StrokeKind.Image)
        {
            Bounds = ShapeGeometry.BoundsOf(this);
            return;
        }
        Bounds = RectF.Empty;
        foreach (var p in Points) Bounds.Add(p.X, p.Y);
    }

    /// <summary>Swaps in a simplified point list (used by RDP on completion).</summary>
    public void ReplacePoints(List<InkPoint> points)
    {
        Points.Clear();
        Points.AddRange(points);
        RecomputeBounds();
        Revision++;
    }

    public float HalfWidthAt(int index)
    {
        float w = Width;
        if (Kind == StrokeKind.Freehand && Points.Count > 1)
            w = Width * (0.60f + 0.80f * Math.Clamp(Points[index].P, 0f, 1f));
        return w * 0.5f;
    }

    /// <summary>Largest width multiplier the pressure curve can produce
    /// (0.60 + 0.80 * P at P = 1). Anything that reasons about how far a stroke
    /// can paint - dirty regions above all - has to use this, not the nominal
    /// half width, or the stroke paints outside its own bounds.</summary>
    public const float MaxWidthFactor = 1.4f;

    /// <summary>
    /// 脏区与命中测试用的外扩边界。美化后的轮廓可能比"中心线 ± 压力最大半宽"
    /// 再超出一点（起收笔的圆帽、粗糙边缘），所以取两者里更大的那个系数。
    /// 取小了会在快速书写时留下残影——这是最容易被忽略、又最显眼的 bug。
    /// </summary>
    /// <summary>
    /// 脏区与命中测试用的外扩包围盒（**画布坐标**）。
    /// 在 WorldBounds 基础上再按笔宽外扩——笔迹是画在线两侧的，
    /// 只算中心线包围盒会漏掉边缘，快速书写就留残影。
    /// </summary>
    public RectF PaddedBounds => WorldBounds.Inflate(
        Width * BoundsInflateFactor * 0.5f + 2f);

    /// <summary>Distance in pixels from a point to this item's outline.</summary>
    /// <summary>
    /// 画布坐标下的"点到这一笔有多远"。内部先反变换回局部坐标再量。
    ///
    /// 反变换之后距离的尺度会跟着变（对象被放大时局部距离看起来变小），
    /// 所以这里按对象的平均缩放折算回画布尺度。**这是个近似**；
    /// 精确判定用 HitTestExact（让 Direct2D 带着变换直接算）。
    /// </summary>
    public float DistanceToCanvas(float x, float y)
    {
        if (Transform.IsIdentity) return DistanceTo(x, y);

        Matrix3x2.Invert(Transform, out var inv);
        var p = Vector2.Transform(new Vector2(x, y), inv);
        return DistanceTo(p.X, p.Y) * AverageScale(Transform);
    }

    /// <summary>矩阵的平均缩放倍数（|行列式| 开方），用来在局部 / 画布尺度之间折算。</summary>
    private static float AverageScale(in Matrix3x2 m)
    {
        float det = m.M11 * m.M22 - m.M12 * m.M21;
        float s = MathF.Sqrt(MathF.Abs(det));
        return s > 1e-6f ? s : 1f;
    }

    /// <summary>
    /// 精确命中测试（**画布坐标**），<paramref name="tolerance"/> 是容差半径。
    ///
    /// 和 <see cref="DistanceToCanvas"/> 的分工：
    ///
    ///   · **自由笔迹**：带子本来就是"中心线两侧各半个笔宽"，所以"点到中心线的
    ///     距离"已经等价于精确判定，用那个更快，不必走这里。
    ///   · **图形**（直线 / 矩形 / 椭圆 / 箭头）：真实轮廓不是两个端点之间的
    ///     线段，DistanceTo 里只能用归一化半径之类的近似，缩放或旋转之后明显偏。
    ///     这里让 Direct2D 照着**画出来时用的同一条描边**去算，是精确的。
    ///
    /// 代价：一次几何构建（有缓存）+ 一次 COM 调用。所以只对**粗筛之后的
    /// 少量候选**调用，不要拿它去遍历整个文档。
    /// </summary>
    public bool HitTestExact(float canvasX, float canvasY, float tolerance = 0f)
    {
        var geo = BuildGeometry(Gfx.D2DFactory);
        if (geo == null) return false;

        // 几何存在局部坐标里，把查询点反变换回去再测——等价于"带着变换去测"，
        // 但只用最简单的重载，少一层踩坑的机会。
        Vector2 p = new(canvasX, canvasY);
        if (!Transform.IsIdentity)
        {
            if (!Matrix3x2.Invert(Transform, out var inv)) return false;
            p = Vector2.Transform(p, inv);
        }

        // 图像是"填满的一块矩形"：点在矩形里就算命中，容差往四周扩一点。
        if (IsImage)
        {
            if (geo.FillContainsPoint(p)) return true;
            return tolerance > 0f && geo.StrokeContainsPoint(p, tolerance * 2f, Gfx.Round);
        }

        if (IsShape)
        {
            // 图形是「描边的中心线」：线宽要算进去；容差靠把线临时加粗来实现——
            // 于是"橡皮圆碰到这条线"就等价于"点落在加粗了 2r 的线上"，不用自己算距离。
            //
            // 注：非等比变换下"线宽不变"还没实现（见计划文档 7.1），这里按局部线宽判定。
            return geo.StrokeContainsPoint(p, MathF.Max(1f, Width) + tolerance * 2f, Gfx.Round);
        }

        // 自由笔迹是填充的带子：落在里面就算命中；
        // 容差则等价于"落在轮廓边界附近 tolerance 之内"。
        if (geo.FillContainsPoint(p)) return true;
        return tolerance > 0f && geo.StrokeContainsPoint(p, tolerance * 2f, Gfx.Round);
    }

    public float DistanceTo(float x, float y)
    {
        if (Points.Count == 0) return float.MaxValue;
        if (Points.Count == 1)
        {
            float dx0 = Points[0].X - x, dy0 = Points[0].Y - y;
            return MathF.Sqrt(dx0 * dx0 + dy0 * dy0);
        }

        // Distance to the *segments*, not just to the sample points. Measuring
        // to vertices only makes the eraser feel dead between samples, which is
        // exactly where a fast stroke has the fewest of them.
        float best = float.MaxValue;
        for (int i = 1; i < Points.Count; i++)
        {
            float d2 = DistToSegmentSq(x, y, Points[i - 1].X, Points[i - 1].Y, Points[i].X, Points[i].Y);
            if (d2 < best) best = d2;
        }

        // Shapes only store two corners, so their outline is not the segment
        // between them. Add the real outline distance.
        if (Kind == StrokeKind.Rectangle || Kind == StrokeKind.Ellipse)
        {
            var r = Bounds;
            if (Kind == StrokeKind.Rectangle)
            {
                best = MathF.Min(best, DistToSegmentSq(x, y, r.MinX, r.MinY, r.MaxX, r.MinY));
                best = MathF.Min(best, DistToSegmentSq(x, y, r.MaxX, r.MinY, r.MaxX, r.MaxY));
                best = MathF.Min(best, DistToSegmentSq(x, y, r.MaxX, r.MaxY, r.MinX, r.MaxY));
                best = MathF.Min(best, DistToSegmentSq(x, y, r.MinX, r.MaxY, r.MinX, r.MinY));
            }
            else
            {
                // Normalised radial distance is a good enough stand-in for the
                // ellipse outline when deciding "did the eraser touch it".
                float cx = (r.MinX + r.MaxX) * 0.5f, cy = (r.MinY + r.MaxY) * 0.5f;
                float rx = MathF.Max(1f, (r.MaxX - r.MinX) * 0.5f);
                float ry = MathF.Max(1f, (r.MaxY - r.MinY) * 0.5f);
                float nx = (x - cx) / rx, ny = (y - cy) / ry;
                float k = MathF.Sqrt(nx * nx + ny * ny) - 1f;
                float approx = k * MathF.Min(rx, ry);
                best = MathF.Min(best, approx * approx);
            }
        }
        return MathF.Sqrt(best);
    }

    private static float DistToSegmentSq(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax, vy = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - vx * t, dy = wy - vy * t;
        return dx * dx + dy * dy;
    }

    /// <summary>与矩形是否相交（画布坐标）。粗筛用，只看世界包围盒。</summary>
    public bool IntersectsRect(RectF r) => WorldBounds.Intersects(r);

    /// <summary>是否整个落在矩形里（画布坐标）。框选用。</summary>
    public bool ContainedInRect(RectF r)
        => !WorldBounds.IsEmpty && WorldBounds.MinX >= r.MinX && WorldBounds.MaxX <= r.MaxX
        && WorldBounds.MinY >= r.MinY && WorldBounds.MaxY <= r.MaxY;

    public bool IsShape => Kind != StrokeKind.Freehand;

    public ID2D1Geometry BuildGeometry(ID2D1Factory1 factory)
    {
        if (Geometry != null && _builtRevision == Revision) return Geometry;
        if (Points.Count == 0) return null;

        if (Geometry != null) { Geometry.Dispose(); Geometry = null; LiveGeometries--; }

        // 自由笔迹的几何在这里（它是"采样点连成带子"，属于笔迹本身）；
        // 图形的几何在 ShapeGeometry —— 那边是"控制点 + 一条规则"，
        // 顶点拖动要改的正是那条规则，两者放一起改起来才不会互相踩。
        Geometry = Kind == StrokeKind.Freehand
            ? BuildRibbon(factory)
            : ShapeGeometry.Build(this, factory);
        if (Geometry != null) LiveGeometries++;
        _builtRevision = Revision;
        return Geometry;
    }

    /// <summary>
    /// Freehand strokes become a filled "ribbon": every sample point is offset
    /// along its normal by half the pressure-scaled width, both sides are joined
    /// into one closed polygon and filled with the winding rule. That is what
    /// allows per-point width, and it also stops a translucent highlighter from
    /// double-darkening where the stroke crosses over itself.
    /// </summary>
    /// <summary>
    /// 这一笔到底画成什么形状。**核心只有二选一**：
    ///
    ///   ① 优化器给了闭合轮廓 → 直接按多边形填充，形状完全由优化器决定；
    ///   ② 没有 → 原始采样点连成的等宽带子，也就是最朴素的样子。
    ///
    /// 核心不产生轮廓，也不做平滑或拟合。想改变观感，请装优化器
    /// （见 InkOptimizer.cs），而不是往这里加算法。
    /// </summary>
    private ID2D1Geometry BuildRibbon(ID2D1Factory1 factory)
    {
        if (Outline != null && Outline.Length >= 3)
            return BuildGeometryFromOutline(factory);

        return BuildRibbonFromPoints(factory);
    }

    /// <summary>
    /// 把美化轮廓直接变成填充几何。填充规则用 Winding：
    /// 这样轮廓自交（比如写连笔时的回环）不会被挖空。
    /// </summary>
    private ID2D1Geometry BuildGeometryFromOutline(ID2D1Factory1 factory)
    {
        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.SetFillMode(FillMode.Winding);
            sink.BeginFigure(Outline[0], FigureBegin.Filled);
            for (int i = 1; i < Outline.Length; i++) sink.AddLine(Outline[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

    /// <summary>
    /// 输入是否带真实压感。判据：压力值有没有真的变化过。
    /// 鼠标和多数触摸屏上报的恒定值（0.5 或 1）会被认成"没有压感"，
    /// 从而改用速度模拟——这正是我们想要的分支。
    /// </summary>
    private bool HasRealPressure()
    {
        if (Points.Count < 3) return false;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in Points)
        {
            if (p.P < min) min = p.P;
            if (p.P > max) max = p.P;
        }
        return max - min > 0.02f;
    }

    /// <summary>
    /// 最朴素的画法：把采样点连成一条等宽带子，宽度由压感决定
    /// （<see cref="HalfWidthAt"/>）。没有优化器时走的就是这一条。
    ///
    /// **这里不做任何平滑**：指针报什么坐标就用什么坐标。底层性能测试要的
    /// 就是这个——量到的数字里不含我们自己加的滤波、抽稀或拟合。
    ///
    /// 已知观感问题：相邻两个采样点几乎重合时（鼠标刚按下的那一瞬间经常
    /// 连报好几个相同坐标），下面的方向会被强行设成水平，轮廓随之在这里
    /// 冒出一个尖角。这是"起笔处有毛边"最可能的来源，属**底层渲染**的
    /// 问题，修在这一点即可，不需要开优化器。
    /// </summary>
    private ID2D1Geometry BuildRibbonFromPoints(ID2D1Factory1 factory)
    {
        int n = Points.Count;
        if (n == 1)
        {
            float rad = MathF.Max(1f, HalfWidthAt(0));
            return factory.CreateEllipseGeometry(
                new Ellipse(new Vector2(Points[0].X, Points[0].Y), rad, rad));
        }

        var outline = new Vector2[n * 2];
        for (int i = 0; i < n; i++)
        {
            int a = i > 0 ? i - 1 : i;
            int b = i < n - 1 ? i + 1 : i;
            float dx = Points[b].X - Points[a].X;
            float dy = Points[b].Y - Points[a].Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            float nx = -dy, ny = dx;
            float hw = HalfWidthAt(i);
            outline[i] = new Vector2(Points[i].X + nx * hw, Points[i].Y + ny * hw);
            outline[n * 2 - 1 - i] = new Vector2(Points[i].X - nx * hw, Points[i].Y - ny * hw);
        }

        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.SetFillMode(FillMode.Winding);
            sink.BeginFigure(outline[0], FigureBegin.Filled);
            for (int i = 1; i < outline.Length; i++) sink.AddLine(outline[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }
}

// ---------------------------------------------------------------------------
//  Undo / redo
// ---------------------------------------------------------------------------

internal abstract class EditAction
{
    public abstract void Undo(InkDocument doc);
    public abstract void Redo(InkDocument doc);

    /// <summary>这次改动**之前**对象占了哪块（画布坐标）。</summary>
    public virtual RectF AffectedBefore => RectF.Empty;

    /// <summary>改动**之后**占哪块。</summary>
    public virtual RectF AffectedAfter => RectF.Empty;

    /// <summary>要重绘的区域 = 两者并集。旧位置要擦、新位置要画，缺一个就留残影。</summary>
    public RectF AffectedUnion
    {
        get { var r = AffectedBefore; r.Add(AffectedAfter); return r; }
    }

    /// <summary>
    /// 这条动作**引用着多少条笔画**。撤销栈的内存成本主要就在这里：
    /// "删掉/清空"之后笔画并没有消失，只是从文档挪到了撤销栈里——撤销能还原，
    /// 代价就是这些笔画必须一直活着。
    /// </summary>
    public virtual int HeldStrokes => 0;
}

/// <summary>一组对象占的总区域。加入、移除、改位置都只是"方向不同"，算法一样。</summary>
internal static class EditRegion
{
    public static RectF Of(IEnumerable<Stroke> strokes)
    {
        var r = RectF.Empty;
        foreach (var s in strokes) r.Add(s.PaddedBounds);
        return r;
    }
}

internal sealed class AddStrokesAction : EditAction
{
    public readonly List<Stroke> Strokes = new();
    public override int HeldStrokes => Strokes.Count;
    public override void Undo(InkDocument doc) { foreach (var s in Strokes) doc.RemoveStroke(s); }
    public override void Redo(InkDocument doc) { foreach (var s in Strokes) doc.AppendStroke(s); }
    public override RectF AffectedAfter => EditRegion.Of(Strokes);
}

internal sealed class RemoveStrokesAction : EditAction
{
    public readonly List<(int index, Stroke stroke)> Items = new();
    public override int HeldStrokes => Items.Count;
    public override void Undo(InkDocument doc) { foreach (var it in Items) doc.InsertStroke(it.index, it.stroke); }
    public override void Redo(InkDocument doc) { foreach (var it in Items) doc.RemoveStroke(it.stroke); }
    public override RectF AffectedBefore => EditRegion.Of(Items.Select(it => it.stroke));
}

internal sealed class ClearAction : EditAction
{
    public readonly List<Stroke> Removed = new();
    public override int HeldStrokes => Removed.Count;
    public override void Undo(InkDocument doc) { foreach (var s in Removed) doc.AppendStroke(s); }
    public override void Redo(InkDocument doc) { doc.ClearStrokes(); }
    public override RectF AffectedBefore => EditRegion.Of(Removed);
}

/// <summary>
/// 把几条动作合成**一步撤销**。
///
/// 为什么需要它：一次面积橡皮的拖动可能同时"删掉两个图形 + 把三条笔迹切成
/// 七段"，那是三种不同性质的动作。用户眼里这是"我擦了一下"，就该一次 Ctrl+Z
/// 回到原样。撤销按**逆序**执行（后做的先撤），否则"先插后删"会撤在错误的
/// 文档状态上。
/// </summary>
internal sealed class CompositeAction : EditAction
{
    public readonly List<EditAction> Parts = new();

    public override int HeldStrokes
    {
        get { int n = 0; foreach (var p in Parts) n += p.HeldStrokes; return n; }
    }

    public override RectF AffectedBefore
    {
        get { var r = RectF.Empty; foreach (var p in Parts) r.Add(p.AffectedBefore); return r; }
    }

    public override RectF AffectedAfter
    {
        get { var r = RectF.Empty; foreach (var p in Parts) r.Add(p.AffectedAfter); return r; }
    }

    public override void Undo(InkDocument doc)
    {
        for (int i = Parts.Count - 1; i >= 0; i--) Parts[i].Undo(doc);
    }

    public override void Redo(InkDocument doc)
    {
        foreach (var p in Parts) p.Redo(doc);
    }
}

/// <summary>
/// 把"一条笔画"换成"若干碎片"（面积橡皮切段，见 EraserOps.CutByRect）。
///
/// parts 为空 = 整条被擦掉；非空 = 切成了几段。两种情形同一条动作，
/// 因为对用户来说都是"这一下把这一块擦干净了"。
/// </summary>
internal sealed class ReplaceStrokesAction : EditAction
{
    public readonly List<(int index, Stroke original, List<Stroke> pieces)> Items = new();

    public override int HeldStrokes
    {
        get
        {
            int n = 0;
            foreach (var it in Items) n += 1 + (it.pieces?.Count ?? 0);
            return n;
        }
    }

    public override RectF AffectedBefore => EditRegion.Of(Items.Select(it => it.original));

    public override RectF AffectedAfter
    {
        get
        {
            var r = RectF.Empty;
            foreach (var it in Items)
                if (it.pieces != null) foreach (var p in it.pieces) r.Add(p.PaddedBounds);
            return r;
        }
    }

    public override void Redo(InkDocument doc)
    {
        foreach (var it in Items) doc.RemoveStroke(it.original);
        foreach (var it in Items)
            if (it.pieces != null) foreach (var p in it.pieces) doc.AppendStroke(p);
    }

    public override void Undo(InkDocument doc)
    {
        foreach (var it in Items)
            if (it.pieces != null) foreach (var p in it.pieces) doc.RemoveStroke(p);
        // 按下标升序插回去：下标是在"这一下之前"的文档上记的，
        // 从前往后插才能让每条都回到自己原来的位置。
        foreach (var it in Items.OrderBy(i => i.index)) doc.InsertStroke(it.index, it.original);
    }
}

/// <summary>
/// 对一组对象施加同一个变换。移动 / 缩放 / 旋转 / 镜像**都是它**，不是四套代码。
///
/// 只改矩阵、不碰几何，所以：
///   · 代价是 O(对象数)，**与每一笔有多少个点无关**；
///   · 撤销就是乘逆矩阵；
///   · 几何缓存完全不用失效（缓存的是局部坐标下的几何）。
///
/// 逆矩阵现算而不存一份：省内存，而且"改变换"本来就是可逆运算，不需要额外状态。
/// </summary>
internal sealed class TransformObjectsAction : EditAction
{
    private readonly Stroke[] _targets;
    private readonly Matrix3x2 _delta;
    private readonly RectF _before;

    public TransformObjectsAction(IReadOnlyList<Stroke> targets, Matrix3x2 delta)
    {
        _targets = new Stroke[targets.Count];
        for (int i = 0; i < targets.Count; i++) _targets[i] = targets[i];
        _delta = delta;
        _before = EditRegion.Of(_targets);
    }

    public override RectF AffectedBefore => _before;
    public override RectF AffectedAfter => EditRegion.Of(_targets);

    public override void Redo(InkDocument doc) => Shift(doc, _delta);

    public override void Undo(InkDocument doc)
    {
        // 退化矩阵（比如缩放成 0）不可逆，那就什么都不做，别把对象搞成 NaN。
        if (!Matrix3x2.Invert(_delta, out var inv)) return;
        Shift(doc, inv);
    }

    private void Shift(InkDocument doc, in Matrix3x2 m)
    {
        foreach (var s in _targets) doc.ApplyTransformCore(s, m);
        doc.Version++;
    }
}

/// <summary>
/// 改图形的**形状定义**（顶点拖动）。
///
/// 和 <see cref="TransformObjectsAction"/> 的区别值得写下来：
///   · 变换动作只改矩阵，**几何一个点都不动**（缩放、旋转、移动都是它）；
///   · 这个动作改的是控制点，也就是"这个矩形到底是哪四个角"。
///
/// 两者都能撤销，但撤销的方式完全不同：前者乘逆矩阵，后者把控制点写回去。
/// 混成一个的话，撤销顶点拖动时会把"对象当前的角度/大小"一起抹掉。
///
/// 和 <see cref="Stroke.SetPoints"/> 配套：它内部会重算包围盒与版本号，
/// 所以内容层分块会跟着重画，不会留下旧形状的残影。
/// </summary>
internal sealed class EditShapeAction : EditAction
{
    private readonly Stroke _target;
    private readonly Vector2[] _before;
    private readonly Vector2[] _after;
    private readonly RectF _beforeRegion;

    public EditShapeAction(Stroke target, Vector2[] before, Vector2[] after)
    {
        _target = target;
        _before = before;
        _after = after;
        _beforeRegion = target.PaddedBounds;
    }

    public override RectF AffectedBefore => _beforeRegion;
    public override RectF AffectedAfter => _target.PaddedBounds;

    public override void Redo(InkDocument doc) => Apply(doc, _after);
    public override void Undo(InkDocument doc) => Apply(doc, _before);

    private void Apply(InkDocument doc, Vector2[] points)
    {
        doc.BeginLiveEdit(_target);
        _target.SetPoints(points);
        doc.EndLiveEdit(_target);
        doc.Dirty.Add(_target.PaddedBounds);
    }
}

// ---------------------------------------------------------------------------

internal sealed class InkDocument
{
    public readonly List<Stroke> Strokes = new();
    public readonly List<Stroke> Selected = new();
    public readonly DirtyRegion Dirty = new();

    /// <summary>
    /// 自上一帧渲染之后**新增**的笔画。
    ///
    /// 内容层缓存（CanvasTiles）用它走"只补画新笔画"的快路径：正在写字时
    /// 文档只增不减，往已经画好的块上补一笔就行，不必清空整块重画——重画一块的
    /// 代价与该块里的笔画总数成正比，而"再写一笔"的代价应当只有一笔。
    /// 由渲染循环在每个窗口都渲染完之后清空（和 <see cref="Dirty"/> 同一处）。
    /// </summary>
    public readonly List<Stroke> AppendedSinceRender = new();

    /// <summary>
    /// 自上一帧之后发生过**结构性**改动：删除、插入、移动、清空、整层作废。
    /// 一旦为真，"只补画"的前提（块内容只差几条新笔画）就不成立了，
    /// 相关块必须整块重画。
    /// </summary>
    public bool StructureChangedSinceRender;

    /// <summary>
    /// 画布块序列（纵向排列）。现在固定一块——冻结截图、白板、翻页都是它的不同内容，
    /// 见计划文档第十二节。序列化按"多块"写，所以以后加页不用改格式。
    /// </summary>
    public readonly List<CanvasBlock> Blocks = new() { CanvasBlock.Default };

    /// <summary>
    /// 对象身份分配器。**只增不减、永不复用**。
    /// 反序列化时要把用过的最大值写回来，否则新对象会和老对象撞 id，
    /// 撤销和多选就会指向错的对象。
    /// </summary>
    private int _nextId = 1;
    public int NextId() => _nextId++;

    /// <summary>反序列化之后调用：把 id 水位抬到至少这么高。</summary>
    public void ReserveIdsUpTo(int maxUsed)
    {
        if (maxUsed >= _nextId) _nextId = maxUsed + 1;
    }

    /// <summary>
    /// 画布范围 = **内容边界 ∪ 一屏**，随书写自动增长。
    ///
    /// "∪ 一屏"是刻意的：没有内容的空白也必须能写（老师总得有个地方起笔），
    /// 所以画布永远至少有一屏；内容长出去了，画布跟着长。
    ///
    /// 它是**比例滚动条的前提**——没有总高度就没有比例可算，滑块无从画起。
    ///
    /// 代价：每次调用 O(笔画数)（约 0.05ms/万笔），滚轮时算一遍可以接受。
    /// </summary>
    public RectF Extent(in RectF viewport)
    {
        var r = RectF.Empty;
        foreach (var s in Strokes) r.Add(s.PaddedBounds);
        if (r.IsEmpty) return viewport;   // 空文档：画布就是一屏
        r.Add(viewport);                  // ∪ 一屏
        return r;
    }

    // 撤销栈必须有上限。原来用无上限的 Stack，一节课下来会堆进十万条动作、
    // 每条还持有笔画对象——实测 3 分钟就多占约 80 MB。主流软件的撤销深度
    // 都在 100~200 步，超过就从最旧的开始丢。
    private const int MaxUndoDepth = 200;

    /// <summary>
    /// 撤销栈里最多挂多少条笔画。
    ///
    /// 只管步数是不够的：一步"清空"就能把一万条笔画挂在栈里等着还原，
    /// 步数还是 1。所以再加一道**条数**上限，超了从最老的开始丢。
    ///
    /// 实测（--memlife）：一万条笔画挂在栈里约 5MB 点数据；几何在删除时就
    /// 释放了，不会跟着栈走。所以这道上限不是为了省那点内存，而是防止
    /// "清空一万条 + 继续写"这类操作把内存顶上去——上限之内随便撤销，
    /// 超出的部分才丢。
    /// </summary>
    public const int MaxUndoStrokes = 30_000;
    private readonly List<EditAction> _undo = new();
    private readonly List<EditAction> _redo = new();

    /// <summary>Bumped on every change so windows know to repaint.</summary>
    public int Version;

    public long TotalPoints;
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    /// <summary>
    /// 撤销/重做栈里引用着的笔画总数。
    ///
    /// 这是"删了东西内存不降"的头号嫌疑：被删的笔画还在撤销栈里等着被还原。
    /// 步数有上限（<see cref="MaxUndoDepth"/>），所以真正要盯的是**条数**——
    /// 一步"清空"就能把一万条全挂在那儿。
    /// </summary>
    public int HistoryStrokes
    {
        get
        {
            int n = 0;
            foreach (var a in _undo) n += a.HeldStrokes;
            foreach (var a in _redo) n += a.HeldStrokes;
            return n;
        }
    }

    private readonly SpatialGrid _grid = new();
    private readonly List<Stroke> _queryScratch = new();
    public int GridCells => _grid.CellCount;

    /// <summary>Candidate lookup through the spatial index (for measurement).</summary>
    public int QueryGrid(RectF r, List<Stroke> results) => _grid.Query(r, results);

    // -- mutation primitives (no history; the actions below drive these) ---

    public void AppendStroke(Stroke s)
    {
        if (s.Id == 0) s.Id = NextId();
        Strokes.Add(s);
        AppendedSinceRender.Add(s);
        TotalPoints += s.Points.Count;
        _grid.Insert(s);
        Dirty.Add(s.PaddedBounds);
        Version++;
    }

    public void InsertStroke(int index, Stroke s)
    {
        // 插到中间（撤销"删除"走这里）：顺序变了，块必须整块重画。
        StructureChangedSinceRender = true;
        if (s.Id == 0) s.Id = NextId();
        Strokes.Insert(Math.Clamp(index, 0, Strokes.Count), s);
        TotalPoints += s.Points.Count;
        _grid.Insert(s);
        Dirty.Add(s.PaddedBounds);
        Version++;
    }

    public void RemoveStroke(Stroke s)
    {
        if (!Strokes.Remove(s)) return;
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        // 关键：笔画被移除时必须释放缓存的 Direct2D 几何，否则每擦一次、
        // 每撤销一次都会泄漏一个几何对象（连同它占的 GPU 侧细分数据）。
        // 撤销/重做会重建几何，代价很小；不释放的话一节课能涨到 GB 级。
        s.Release();
        TotalPoints -= s.Points.Count;
        Dirty.Add(s.PaddedBounds);
        Version++;
    }

    public void ClearStrokes()
    {
        StructureChangedSinceRender = true;
        foreach (var s in Strokes) s.Release();
        _grid.Clear();
        Strokes.Clear();
        Selected.Clear();
        TotalPoints = 0;
        Dirty.MarkFull();
        Version++;
    }

    // -- user operations ---------------------------------------------------

    private void Commit(EditAction action)
    {
        _undo.Add(action);
        TrimUndo();
        _redo.Clear();

        // 命令自己报告"我动了哪块区域"，脏区在这里统一合并。
        // 这样功能代码就不必各自记得去标脏——漏标是留残影的头号原因。
        Dirty.Add(action.AffectedUnion);
    }

    /// <summary>
    /// 撤销栈两道闸：**步数**（不超过 <see cref="MaxUndoDepth"/>）和
    /// **条数**（不超过 <see cref="MaxUndoStrokes"/>）。任一条超了就从最老的丢。
    ///
    /// 为什么要有条数这道：一步"清空"能挂着一万条笔画。步数只有 1，
    /// 但内存是实打实的。
    /// </summary>
    private void TrimUndo()
    {
        if (_undo.Count > MaxUndoDepth)
            _undo.RemoveRange(0, _undo.Count - MaxUndoDepth);

        int held = 0;
        foreach (var a in _undo) held += a.HeldStrokes;
        if (held <= MaxUndoStrokes) return;

        int drop = 0;
        while (held > MaxUndoStrokes && drop < _undo.Count - 1)
        {
            held -= _undo[drop].HeldStrokes;
            drop++;
        }
        if (drop > 0) _undo.RemoveRange(0, drop);
    }

    /// <summary>
    /// 改一个对象的变换，并把空间索引跟着挪。
    ///
    /// **索引必须在改之前移除、改之后插入**：改完再移除就找不到它原来占的格子了，
    /// 那些格子里会永远留着一个幽灵条目。
    /// </summary>
    internal void ApplyTransformCore(Stroke s, in Matrix3x2 m)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        s.Transform = s.Transform * m;
        _grid.Insert(s);
    }

    /// <summary>
    /// 实时改形状的**开始**：先把对象从空间索引里摘出来。
    ///
    /// 顺序不能反：索引记的是对象的包围盒，等顶点拖完再摘就找不到它原来占的
    /// 那几格了，那些格子里会留下幽灵条目——表现是"擦不到已经移走的东西"，
    /// 或者在看不见的地方其实一直有东西。
    /// </summary>
    internal void BeginLiveEdit(Stroke s)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);
    }

    /// <summary>实时改形状的**结束**：按新形状重新入索引，并通知渲染层。</summary>
    internal void EndLiveEdit(Stroke s)
    {
        _grid.Insert(s);
        Version++;
    }

    /// <summary>
    /// 顶点拖动结束：把这一拖记成**一步撤销**。
    /// 拖动过程中的实时预览不进撤销栈（和拖动选中框的节奏一致）。
    /// </summary>
    public void CommitShapeEdit(Stroke s, Vector2[] before, Vector2[] after)
    {
        var act = new EditShapeAction(s, before, after);
        act.Redo(this);            // 形状此刻已经是新值，Redo 只负责把索引/脏区对齐
        Commit(act);
    }

    /// <summary>
    /// 对当前选中的对象施加一个变换。移动 / 缩放 / 旋转 / 镜像都走这里。
    /// 返回 false 表示没有选中任何东西。
    /// </summary>
    public bool ApplyTransform(Matrix3x2 delta)
    {
        if (Selected.Count == 0) return false;
        var act = new TransformObjectsAction(Selected, delta);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>
    /// 复制选中的对象：克隆、换新身份、整体偏移一点，**并把选中切到副本**。
    /// 选中切到副本是主流软件的行为，也符合直觉：复制完接着拖，拖的该是副本。
    /// </summary>
    public bool DuplicateSelected(float dx = 24f, float dy = 24f)
    {
        if (Selected.Count == 0) return false;

        // 防呆护栏：Ctrl+A + Ctrl+D 是**指数增长**（选全部→复制→再全选→再复制，
        // 每按一次翻一倍）。按十次就是 1024 倍，二十次是一百万条——任何画布
        // 程序都扛不住，区别只在于"崩"还是"优雅地拒绝"。
        //
        // 到上限就**明确拒绝并说清楚**，而不是让程序卡到没响应。
        // 老师说"我按了几下就卡死了"的时候，至少能看懂发生了什么。
        // 两道护栏：
        //   ① 单次复制新增太多——"全选 + 复制"连按就是在走这条路。一次复制
        //      5000 条以上几乎一定是误操作，而且代价是实打实的：
        //      每条要重新光栅化、要进空间索引、要占点数据。
        //   ② 总数上限——见 MaxObjects。
        if (Selected.Count > MaxDuplicatePerOperation)
        {
            LastRejectReason = $"一次复制 {Selected.Count} 条超过上限 {MaxDuplicatePerOperation}。"
                             + "如果确实要复制这么多，请分几次选。";
            Console.WriteLine("[拒绝] " + LastRejectReason);
            return false;
        }

        if (Strokes.Count + Selected.Count > MaxObjects)
        {
            LastRejectReason = $"批注数量将达到 {Strokes.Count + Selected.Count}，"
                             + $"超过上限 {MaxObjects}。请先清空或删掉一些。";
            Console.WriteLine("[拒绝] " + LastRejectReason);
            return false;
        }

        var act = new AddStrokesAction();
        var copies = new List<Stroke>(Selected.Count);
        foreach (var s in Selected)
        {
            var c = s.Clone();
            c.Transform = c.Transform * Matrix3x2.CreateTranslation(dx, dy);
            copies.Add(c);
            act.Strokes.Add(c);
            AppendStroke(c);       // 这一步给它分配 Id
        }
        Commit(act);

        Selected.Clear();
        Selected.AddRange(copies);
        return true;
    }

    /// <summary>
    /// 对象数量上限。
    ///
    /// 原来取 10 万（按"每条约 10KB"估的）——实测每条约 16~20KB，
    /// 10 万条就是 **2GB**，还没算内容层缓存。教室里那台机器会直接卡死。
    ///
    /// 现在取 2 万：
    ///   · 够用：连续板书 20 分钟约 7200 笔，一节 40 分钟的课约 1.5 万笔；
    ///   · 安全：堆在同一个位置的极端情况（Ctrl+A + Ctrl+D）约 400MB 就停住，
    ///     而不是一路涨到 GB 级。
    /// 到上限是**明确拒绝并说明原因**，不是让程序卡到没响应。
    /// </summary>
    public const int MaxObjects = 20_000;

    /// <summary>
    /// 单次复制最多新增多少条。
    ///
    /// 实测：堆在同一个位置的 8192 条，渲染一帧要 162ms、占用 272MB；
    /// 16384 条是 289ms / 397MB（用真实笔迹还要更高）。而正常一屏板书大约
    /// 400 条，所以 5000 这个量级既拦得住"全选+复制连按"，又不挡正常复制。
    /// </summary>
    public const int MaxDuplicatePerOperation = 5_000;

    /// <summary>上一次被拒绝的原因（给界面显示用）。没有就是 null。</summary>
    public string LastRejectReason;

    /// <summary>
    /// 拖动预览：把变换**直接设成某个值**（不是乘增量）。
    ///
    /// 为什么不乘增量：拖动中每帧都从"按下那一刻的原始变换"重算，而不是在
    /// 上一帧结果上继续乘——后者会累积浮点误差，拖得越久偏得越多，松手后
    /// 撤销也回不到原样。这个过程**不产生撤销记录**；松手时才提交一条命令。
    /// 但空间索引必须每帧跟着挪，否则拖完擦除会擦不到。
    /// </summary>
    internal void SetTransformLive(Stroke s, in Matrix3x2 m)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        s.Transform = m;
        _grid.Insert(s);

        // **必须 bump 版本号**：渲染层就是靠它判断"内容层该不该修补"的
        // （SyncTiles 里 `_tilesVersion != doc.Version` 那一句）。
        //
        // 当初漏了这一句，把"不产生撤销记录"和"文档没变"混成了一件事，
        // 结果拖动时蓝框跟着走、墨迹纹丝不动，松手才整层重画跳过去。
        // 版本号管的是"外观变了没有"，跟撤销栈无关。
        //
        // 代价是每帧把脏区碰到的**分块**重画一次（不是整层重建），
        // 所以跟画面里有多少笔无关。
        Version++;
    }

    /// <summary>
    /// 用读出来的内容整体替换文档（加载 / 粘贴）。
    /// **这是唯一一个不产生撤销动作的批量改动**——加载是一份新文档的开始；
    /// 粘贴要进撤销栈的话，由调用方自己包成一条动作。
    /// </summary>
    internal void ReplaceAll(List<CanvasBlock> blocks, List<Stroke> strokes, int maxId)
    {
        ClearStrokes();
        Blocks.Clear();
        Blocks.AddRange(blocks);
        if (Blocks.Count == 0) Blocks.Add(CanvasBlock.Default);
        foreach (var s in strokes) AppendStroke(s);
        ReserveIdsUpTo(maxId);
        ClearHistory();
        Dirty.MarkFull();
        Version++;
    }

    /// <summary>Drops the undo history without touching the strokes. Used by the
    /// synthetic benchmark, which must not record 10k undo entries.</summary>
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void AddStroke(Stroke s)
    {
        var act = new AddStrokesAction();
        act.Strokes.Add(s);
        AppendStroke(s);
        Commit(act);
    }

    /// <summary>
    /// 造一个图像对象（截图落盘、粘贴图片走这里）。
    ///
    /// 两个坐标约定：
    ///   · **局部坐标**是 (0,0)-(W,H) 像素，图像自己的像素坐标；
    ///   · 摆到画布上靠 <see cref="Stroke.Transform"/>（缩放 + 平移），
    ///     和笔迹完全一样的机制——所以之后缩放、旋转、翻转都不需要另写代码。
    ///
    /// <paramref name="scale"/> 是"物理像素 → 画布像素"的换算（等于 1/DPI）。
    /// 截屏抓到的是一比一的物理像素，在 200% 缩放的屏上直接摆上去会大一倍。
    /// </summary>
    public Stroke AddImage(ImageData img, float canvasX, float canvasY, float scale)
    {
        if (img == null) return null;
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = img,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(img.Width, img.Height),
        });
        s.Transform = Matrix3x2.CreateScale(scale)
                    * Matrix3x2.CreateTranslation(canvasX, canvasY);
        AddStroke(s);
        return s;
    }

    /// <summary>把选区换成指定的这一批对象（截图之后自动选中新对象用）。</summary>
    public void SelectOnly(IEnumerable<Stroke> items)
    {
        Selected.Clear();
        foreach (var s in items) Selected.Add(s);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var a = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        a.Undo(this);
        _redo.Add(a);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var a = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        a.Redo(this);
        _undo.Add(a);
        TrimUndo();
        return true;
    }

    public void Clear()
    {
        if (Strokes.Count == 0) return;
        var act = new ClearAction();
        act.Removed.AddRange(Strokes);
        ClearStrokes();
        Commit(act);
    }

    public int EraseAt(float x, float y, float radius)
    {
        // One drag = one undo step. Collecting the removals into a single action
        // also stops a fast drag from flooding the undo stack. A call made
        // outside BeginErase/EndErase is committed straight away.
        var batch = EraseBatch();
        int added = 0;

        var probe = new RectF { MinX = x - radius, MinY = y - radius, MaxX = x + radius, MaxY = y + radius };
        _grid.Query(probe, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        var act = new RemoveStrokesAction();
        // Copy first: removing strokes mutates the grid we just queried.
        var candidates = _queryScratch.ToArray();
        foreach (var s in candidates)
        {
            if (s.IsShape)
            {
                // 图形的轮廓不是两个端点之间的线段，近似会偏，交给 Direct2D 精确算。
                if (!s.HitTestExact(x, y, radius)) continue;
            }
            else
            {
                // 自由笔迹：带子就是中心线两侧半个笔宽，点到中心线的距离已经够准，走快的那条。
                float reach = radius + s.Width * Stroke.MaxWidthFactor * 0.5f;
                if (s.DistanceToCanvas(x, y) > reach) continue;
            }
            int index = Strokes.IndexOf(s);
            if (index < 0) continue;
            act.Items.Add((index, s));
            added++;
            RemoveStroke(s);
        }
        if (act.Items.Count > 0) batch.Parts.Add(act);
        FlushIfAuto();
        return added;
    }

    /// <summary>
    /// **笔记橡皮擦**：擦掉 (x,y) 处**最上面那一条**笔画，返回擦掉几条（0 或 1）。
    ///
    /// 只擦最上面一条是"精准"的核心：字的交叉点上笔尖同时压着两三笔，
    /// 全删的话一擦就是一大块。想一次擦一片，那是面积橡皮
    /// （<see cref="EraseAreaRect"/>）的活。
    ///
    /// 实现上有个小机关：空间索引给的是**无序**候选，而"最上面"要看文档顺序。
    /// 这里拿查询的 stamp 从文档尾部往前扫，**撞到第一个命中的就收工**——
    /// 不分配集合，而且从尾部扫意味着通常几步就命中。
    /// </summary>
    public int EraseStrokeAt(float x, float y, float radius)
    {
        var batch = EraseBatch();
        var probe = new RectF { MinX = x - radius, MinY = y - radius, MaxX = x + radius, MaxY = y + radius };
        _grid.Query(probe, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        int stamp = _grid.CurrentStamp;
        for (int i = Strokes.Count - 1; i >= 0; i--)
        {
            var s = Strokes[i];
            if (s.QueryStamp != stamp) continue;              // 不在候选里
            if (!EraserOps.HitsPoint(s, x, y, radius)) continue;

            var act = new RemoveStrokesAction();
            act.Items.Add((i, s));
            RemoveStroke(s);
            batch.Parts.Add(act);
            FlushIfAuto();
            return 1;
        }
        FlushIfAuto();
        return 0;
    }

    /// <summary>
    /// **面积橡皮擦**：矩形范围内的墨被切掉，返回被改动的对象数。
    ///
    /// 两种对象两种处理，这是刻意的（也是这一版最需要用户确认的取舍）：
    ///   · **自由笔迹**切段——切出来的碎片仍是普通笔画，可以再选中、移动、撤销；
    ///   · **图形 / 图像**整对象删除——"半个矩形"没有意义，硬切只会留下一条
    ///     开口的折线（图形的顶点定义会当场失效）。
    /// </summary>
    public int EraseAreaRect(RectF rect)
    {
        if (rect.IsEmpty) return 0;
        var batch = EraseBatch();

        _grid.Query(rect, _queryScratch);
        if (_queryScratch.Count == 0) return 0;
        var candidates = _queryScratch.ToArray();

        var act = new ReplaceStrokesAction();
        foreach (var s in candidates)
        {
            int index = Strokes.IndexOf(s);
            if (index < 0) continue;

            if (s.IsImage)
            {
                if (!EraserOps.ImageOverlapsRect(s, rect)) continue;
                act.Items.Add((index, s, null));
                RemoveStroke(s);
            }
            else if (s.IsShape)
            {
                if (!EraserOps.ShapeTouchesRect(s, rect)) continue;
                act.Items.Add((index, s, null));
                RemoveStroke(s);
            }
            else
            {
                // **按墨的实际宽度膨胀之后再切**（不是按中心线切）。
                //
                // 这一步是"看起来像像素橡皮"的关键。只按中心线切的话：
                // 中心线在框外、笔身却压在框里的粗笔画一点都不会少——
                // 用户看到的就是"框里还有墨，而且形状变怪了"（实测报的 bug）。
                // 膨胀半个笔宽之后，**凡是与框相交的墨都会被切断**，
                // 框内一定干净；代价是框外一个笔宽内也会跟着掉，
                // 这也正好让切口看起来是"整条擦断"，而不是被啃出一个歪口子。
                var cut = rect.Inflate(s.Width * Stroke.MaxWidthFactor * 0.5f);
                if (!EraserOps.CutByRect(s, cut, _pieceScratch)) continue;
                var pieces = new List<Stroke>(_pieceScratch);
                act.Items.Add((index, s, pieces));
                RemoveStroke(s);
                foreach (var p in pieces) AppendStroke(p);
            }
        }
        if (act.Items.Count > 0) batch.Parts.Add(act);
        FlushIfAuto();
        return act.Items.Count;
    }

    private readonly List<Stroke> _pieceScratch = new();
    private CompositeAction _eraseBatch;

    /// <summary>
    /// 取当前这一"擦"的批次。**一次按下到抬起 = 一步撤销**：拖动中产生的
    /// 几十次擦除全挂在同一个 CompositeAction 上，松手才提交。
    /// 没有 BeginErase 就直接调用时，返回一个临时批次并立刻提交（见 EndErase 的调用方）。
    /// </summary>
    private CompositeAction EraseBatch()
    {
        if (_eraseBatch != null) return _eraseBatch;
        _eraseBatch = new CompositeAction();
        _eraseBatchAutoCommit = true;
        return _eraseBatch;
    }

    /// <summary>
    /// 没有 BeginErase / EndErase 包着就直接调擦除（自检、脚本、将来的批量操作
    /// 走这条）时，立刻提交这一步，别让动作挂在临时批次里永远不进撤销栈。
    /// </summary>
    private void FlushIfAuto()
    {
        if (!_eraseBatchAutoCommit) return;
        if (_eraseBatch != null && _eraseBatch.Parts.Count > 0) Commit(_eraseBatch);
        _eraseBatch = null;
        _eraseBatchAutoCommit = false;
    }

    private bool _eraseBatchAutoCommit;

    public void BeginErase()
    {
        _eraseBatch = new CompositeAction();
        _eraseBatchAutoCommit = false;
    }

    public void EndErase()
    {
        if (_eraseBatch != null && _eraseBatch.Parts.Count > 0) Commit(_eraseBatch);
        _eraseBatch = null;
        _eraseBatchAutoCommit = false;
    }

    public void DeleteSelected()
    {
        if (Selected.Count == 0) return;
        var act = new RemoveStrokesAction();
        foreach (var s in Selected)
        {
            int idx = Strokes.IndexOf(s);
            if (idx >= 0) act.Items.Add((idx, s));
        }
        foreach (var it in act.Items) RemoveStroke(it.stroke);
        Selected.Clear();
        if (act.Items.Count > 0) Commit(act);
    }

    /// <summary>Moves the current selection by a delta (drag-to-move).</summary>
    public void MoveSelected(float dx, float dy)
        => ApplyTransform(Matrix3x2.CreateTranslation(dx, dy));

    public void ApplyMarquee(RectF r)
    {
        Selected.Clear();
        _grid.Query(r, _queryScratch);
        foreach (var s in _queryScratch)
            if (s.ContainedInRect(r)) Selected.Add(s);
    }

    /// <summary>Marks the whole content layer stale (cheap to say, expensive to
    /// repaint, so only used when a change really touches the entire surface).</summary>
    public void InvalidateAll()
    {
        StructureChangedSinceRender = true;
        Dirty.MarkFull();
        Version++;
    }
}

/// <summary>Laser pointer trail: a ring of timestamped points that fade out.</summary>
internal sealed class LaserTrail
{
    public const double LifetimeMs = 600;

    private readonly List<InkPoint> _pts = new();
    public double LastAddMs;
    public bool Visible;

    public IReadOnlyList<InkPoint> Points => _pts;

    public void Add(float x, float y, double now)
    {
        _pts.Add(new InkPoint { X = x, Y = y, P = 1, T = now });
        LastAddMs = now;
        if (_pts.Count > 4096) _pts.RemoveRange(0, 1024);
    }

    public void Prune(double now)
    {
        int drop = 0;
        while (drop < _pts.Count && now - _pts[drop].T > LifetimeMs) drop++;
        if (drop > 0) _pts.RemoveRange(0, drop);
    }

    public bool ActiveAt(double now) => Visible && _pts.Count > 1 && (now - LastAddMs) < LifetimeMs * 1.5;

    public void Clear() => _pts.Clear();
}
