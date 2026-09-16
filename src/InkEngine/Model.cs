using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

public enum Tool
{
    Pen = 0,
    Highlighter = 1,
    Laser = 2,
    Eraser = 3,
    Marquee = 4,
    Line = 5,
    Rectangle = 6,
    Ellipse = 7,
    Arrow = 8,
    /// <summary>截图：拖一个框，把屏幕那一块抓成图像对象。</summary>
    Capture = 9,

    /// <summary>
    /// 像素橡皮：擦掉笔迹的**一部分**（一笔被切成两段），和"碰到哪一条就整条删"
    /// 的 <see cref="Eraser"/> 是两种工具，不是同一个工具的两档。
    ///
    /// 落点是**竖着的黄金比例矩形**（高 : 宽 = 1.618，见 Engine.PixelEraser*）：
    /// 实际用法是"从上往下抹一段"（一列板书、一个竖排的字），不是横扫一行。
    /// 同行都有这一档（OneNote 的"笔划橡皮" vs GoodNotes 的"精细橡皮"），
    /// 取舍与调研见 调研-橡皮擦.md。
    /// </summary>
    PixelEraser = 10,
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

    /// <summary>点在不在这个矩形里（点选/操作条这些地方用）。</summary>
    public bool Contains(float x, float y)
        => !IsEmpty && x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

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
    /// <summary>图像对象（截图 / 粘贴）。像素挂在 <see cref="Stroke.Image"/> 上。</summary>
    Image = 5,
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
    /// **被擦掉的参数区间**（参数 = 点序号，可以带小数；空表 = 整条完好）。
    ///
    /// 2026-09-15 从"把笔迹拆成几个新对象"改成"在一条笔迹上记区间"，四个理由：
    ///   ① **自相重叠的墨不会混合两次**：剩下的段还在同一条几何里，一次
    ///      `DrawGeometry` 只混合一次。拆成两个对象就会混合两次——半透明荧光笔
    ///      在自交处会变深（离屏实测：差 0 → 56）；
    ///   ② **对象数不涨**：拆对象时，反复擦同一笔会让对象线性增长
    ///      （一万笔的画面上 25 步切出 3123 个）；
    ///   ③ **身份不变**：`Id` 不换，选中 / 变换 / 复制粘贴的行为完全不变；
    ///   ④ 区间记在**参数**上，与变换无关——拆对象时得把变换烘进画布坐标。
    ///
    /// 代价（明确接受）：几何缓存要按区间重拼（figure 数与"拆出来的段数"同量级）；
    /// 命中测试要跳过被擦的段（见 <see cref="DistanceTo"/>）。
    ///
    /// 约定：**始终有序、不相邻**（<see cref="AddErased"/> 负责合并）。
    /// </summary>
    public readonly List<(float a, float b)> Erased = new();

    /// <summary>最后一个点的参数（参数 = 点序号）。</summary>
    public float LastParam => Points.Count > 1 ? Points.Count - 1 : 0f;

    public bool HasErased => Erased.Count > 0;

    /// <summary>
    /// 加一段被擦掉的参数区间（自动裁剪到 [0, LastParam] 并与相邻区间合并）。
    /// 返回是否真的改变了什么。
    /// </summary>
    public bool AddErased(float a, float b)
    {
        float lo = MathF.Min(a, b), hi = MathF.Max(a, b);
        lo = Math.Clamp(lo, 0f, LastParam);
        hi = Math.Clamp(hi, 0f, LastParam);
        if (MergeInterval(Erased, lo, hi))
        {
            Revision++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 把一段区间并进一张"有序、互不相邻"的区间表（就地改）。返回是否真的变了。
    ///
    /// 抽成静态是为了让像素橡皮能先在**副本**上算好结果再决定要不要动对象——
    /// 直接改原对象的话，"其实没变化"也会把几何缓存打掉、把块标记成要重画。
    /// </summary>
    public static bool MergeInterval(List<(float a, float b)> list, float lo, float hi)
    {
        // 零长度区间 = 什么都没擦掉（参数的 1 就是相邻两个采样点的间隔）。
        if (hi - lo < 1e-3f) return false;

        // 合并判定的余量：参数 1 = 相邻两个采样点的间隔，1e-3 换到屏幕上是千分之一个
        // 采样间距——远小于一个像素。
        const float eps = 1e-3f;

        int from = 0;
        while (from < list.Count && list[from].b < lo - eps) from++;

        float nlo = lo, nhi = hi;
        int to = from;                       // [from, to) 是要被吃掉的旧区间
        while (to < list.Count && list[to].a <= nhi + eps)
        {
            nlo = MathF.Min(nlo, list[to].a);
            nhi = MathF.Max(nhi, list[to].b);
            to++;
        }

        // 被一个已有区间完整包住 → 状态没变。
        if (to - from == 1 && list[from].a == nlo && list[from].b == nhi) return false;

        list.RemoveRange(from, to - from);
        list.Insert(from, (nlo, nhi));
        return true;
    }

    /// <summary>给一张区间表算"还剩下哪些连续段"（<see cref="RemainingRuns"/> 的静态版）。</summary>
    public static List<(float a, float b)> RemainingRunsOf(
        List<(float a, float b)> list, float lastParam)
    {
        var runs = new List<(float a, float b)>();
        float cursor = 0f;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].a > cursor + 1e-4f) runs.Add((cursor, list[i].a));
            cursor = MathF.Max(cursor, list[i].b);
        }
        if (cursor < lastParam - 1e-4f) runs.Add((cursor, lastParam));
        return runs;
    }

    /// <summary>整表替换（撤销 / 重做 / 反序列化用）。</summary>
    public void SetErased(List<(float a, float b)> src)
    {
        Erased.Clear();
        if (src != null) Erased.AddRange(src);
        Revision++;
    }

    /// <summary>参数 t 是否落在被擦掉的区间里。</summary>
    public bool IsParamErased(float t)
    {
        for (int i = 0; i < Erased.Count; i++)
            if (t >= Erased[i].a && t <= Erased[i].b) return true;
        return false;
    }

    /// <summary>
    /// 还剩下的连续段（参数区间），按顺序。空 = 整条都被擦没了。
    /// 渲染按它拼几何，撤销 / 命中测试也读它。
    /// </summary>
    public List<(float a, float b)> RemainingRuns()
    {
        var runs = new List<(float a, float b)>();
        // 单点笔迹（一个圆点）：参数只有一个 0。它没有"长度"可分，所以要么完整
        // 保留、要么整条被擦掉（后者由像素橡皮那边直接判定并删除，见 EraseRectAt）。
        if (Points.Count <= 1)
        {
            if (Erased.Count == 0) runs.Add((0f, 0f));
            return runs;
        }
        return RemainingRunsOf(Erased, LastParam);
    }

    /// <summary>参数 t 处的坐标（在两个采样点之间线性插值）。</summary>
    public Vector2 PointAtParam(float t)
    {
        int n = Points.Count;
        if (n == 0) return default;
        if (n == 1) return new Vector2(Points[0].X, Points[0].Y);
        float c = Math.Clamp(t, 0f, n - 1);
        int i = (int)MathF.Floor(c);
        if (i >= n - 1) return new Vector2(Points[n - 1].X, Points[n - 1].Y);
        float f = c - i;
        return new Vector2(
            Points[i].X + (Points[i + 1].X - Points[i].X) * f,
            Points[i].Y + (Points[i + 1].Y - Points[i].Y) * f);
    }

    /// <summary>参数 t 处的压力（拆段时给新段的端点用，别把压感丢掉）。</summary>
    public float PressureAtParam(float t)
    {
        int n = Points.Count;
        if (n == 0) return 0.5f;
        if (n == 1) return Points[0].P;
        float c = Math.Clamp(t, 0f, n - 1);
        int i = (int)MathF.Floor(c);
        if (i >= n - 1) return Points[n - 1].P;
        float f = c - i;
        return Points[i].P + (Points[i + 1].P - Points[i].P) * f;
    }

    /// <summary>参数 t 处的时间戳（同上）。</summary>
    public double TimeAtParam(float t)
    {
        int n = Points.Count;
        if (n == 0) return 0;
        if (n == 1) return Points[0].T;
        float c = Math.Clamp(t, 0f, n - 1);
        int i = (int)MathF.Floor(c);
        if (i >= n - 1) return Points[n - 1].T;
        float f = c - i;
        return Points[i].T + (Points[i + 1].T - Points[i].T) * f;
    }

    /// <summary>
    /// 把这一条按**剩下的段**拆成几个新对象（局部坐标 + 原变换，样式继承）。
    /// 只有"用户要单独摆弄某一段"时才调用，见 <see cref="InkDocument.SplitErasedSelection"/>。
    /// </summary>
    public List<Stroke> SplitIntoRuns()
    {
        var parts = new List<Stroke>();
        foreach (var (a, b) in RemainingRuns())
        {
            var p = new Stroke
            {
                Tool = Tool, Kind = StrokeKind.Freehand,
                Color = Color, Width = Width, Transform = Transform,
            };
            var start = PointAtParam(a);
            p.AddPoint(start.X, start.Y, PressureAtParam(a), TimeAtParam(a));
            for (int i = 1; i < Points.Count; i++)
            {
                if (i < a - 1e-6f) continue;
                if (i > b + 1e-6f) break;
                p.AddPoint(Points[i].X, Points[i].Y, Points[i].P, Points[i].T);
            }
            var end = PointAtParam(b);
            p.AddPoint(end.X, end.Y, PressureAtParam(b), TimeAtParam(b));
            parts.Add(p);
        }
        return parts;
    }

    /// <summary>
    /// 把**图形**（直线 / 矩形 / 椭圆 / 箭头）熔成自由笔迹：按它自己的轮廓折线取点，
    /// 坐标换算到画布、变换归一。
    ///
    /// 为什么：用户 2026-09-15 定"**橡皮擦中图形，断开了也要单独算**"——图形是参数化对象
    /// （两个端点），不先变成点列就没法"擦掉中间、剩下两截"。熔完就走和墨迹一模一样的那条路。
    ///
    /// 代价（明确接受）：熔完它**不再是图形**——没有顶点手柄、不能改形状参数。
    /// 这一步靠撤销回退（原图形整个进撤销栈）。
    ///
    /// 轮廓顺序（见 <see cref="ShapeOutline"/>）：直线 2 点、矩形 5 点、椭圆 33 点；
    /// 箭头是 [尾, 尖, 上翼, 尖, 下翼]——中间那两段是**沿着已经画过的翼走回去**，
    /// 不会凭空多出墨（不透明笔下看不出双画，箭头本来就是用笔画的）。
    /// </summary>
    public Stroke MeltToFreehand()
    {
        var m = new Stroke
        {
            Tool = Tool, Kind = StrokeKind.Freehand,
            Color = Color, Width = Width,
        };
        foreach (var p in ShapeOutline())
        {
            var q = Transform.IsIdentity ? p : Vector2.Transform(p, Transform);
            m.AddPoint(q.X, q.Y, 1f, 0);
        }
        return m;
    }

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
        // 图像对象还挂着一张 D2D 位图（可能很大：一张 800×600 的截图约 2MB）。
        // 漏掉这一句，"擦掉截图之后内存不降"就是必然的。释放之后再画会按需重建。
        Image?.Release();
        _builtRevision = -1;
    }

    /// <summary>
    /// 深拷贝（复制 / 粘贴用）。**Id 留 0**，由文档入册时分配——
    /// 复制出来的必须是新身份，否则撤销和多选会指向错的对象。
    /// </summary>
    public Stroke Clone()
    {
        var c = new Stroke
        {
            Tool = Tool,
            Kind = Kind,
            Color = Color,
            Width = Width,
            Transform = Transform,
        };
        // 用 AddPoint 加：它会顺便把 Bounds 和 Revision 收拾好。
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            c.AddPoint(p.X, p.Y, p.P, p.T);
        }
        // 图像像素**共享不复制**：一张截图几兆字节，复制一份纯属浪费；
        // 而且像素是不可变的（没有任何代码会改它），共享没有风险。
        c.Image = Image;
        // 擦除区间要跟着复制：不然"复制一份"会把已经擦掉的部分又画回来。
        c.Erased.AddRange(Erased);
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

    /// <summary>包围盒重算。它同时是脏区、命中测试和空间索引的依据，改点之后必须重算。</summary>
    private void RecomputeBounds()
    {
        Bounds = RectF.Empty;
        foreach (var p in Points) Bounds.Add(p.X, p.Y);
    }

    /// <summary>
    /// 脏区与命中测试用的外扩包围盒（**画布坐标**）。
    /// 在 WorldBounds 基础上再按笔宽外扩——笔迹是画在线两侧的，
    /// 只算中心线包围盒会漏掉边缘，快速书写就留残影。
    ///
    /// 外扩量 = 半个笔宽（+2 像素余量）。**2026-09-14 起这个数是精确的**：
    /// 墨迹现在是"中心线 + 等宽描边 + 圆头圆角"，离中心线最远就是半个笔宽，
    /// 而端帽/拐角都是半径 = 半宽 的圆弧，不会再多伸出去。
    /// （以前自己拼轮廓、内角要补到两条内边的交点，最远能到好几倍半宽，
    /// 那时这个系数必须留得很大——现在就按事实来。）
    /// </summary>
    public RectF PaddedBounds => WorldBounds.Inflate(
        Width * 0.5f + 2f);

    private RectF _inkBounds = RectF.Empty;
    private int _inkBoundsRevision = -1;

    /// <summary>
    /// **墨迹**的包围盒（局部坐标）：中心线往外扩半个笔宽。
    ///
    /// 和 <see cref="Bounds"/>（中心线）是两件事。要"这一笔在屏幕上占了多大一块"，
    /// 必须用它——中心线包围盒对宽笔来说小得离谱：64 像素宽的荧光笔，
    /// 屏幕上是一条 64 像素宽的带子，而中心线包围盒只是一个点或一条细线。
    /// **选中框就吃这个数**（以前用中心线，宽笔选中之后框压在墨里面，看着就不对）。
    ///
    /// 按 Revision 缓存：选区每帧都要问它，不能每次都遍历点。
    /// </summary>
    public RectF InkBounds
    {
        get
        {
            if (_inkBoundsRevision == Revision) return _inkBounds;

            var r = RectF.Empty;
            float hw = Width * 0.5f;        // 图像对象 Width = 0，就是它自己的矩形
            for (int i = 0; i < Points.Count; i++)
            {
                r.Add(Points[i].X - hw, Points[i].Y - hw);
                r.Add(Points[i].X + hw, Points[i].Y + hw);
            }
            _inkBounds = r;
            _inkBoundsRevision = Revision;
            return r;
        }
    }

    /// <summary>
    /// 墨迹包围盒的**画布坐标**版本（变换四个角取并集，理由同 <see cref="WorldBounds"/>）。
    /// 多选时的选区框按它并起来。
    /// </summary>
    public RectF WorldInkBounds
    {
        get
        {
            var r = InkBounds;
            if (r.IsEmpty) return r;
            if (Transform.IsIdentity) return r;
            return TransformRect(r, Transform);
        }
    }

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

        // 图形和笔迹现在都是"中心线 + 描边"（笔迹交 D2D 原生描边画，图形本来也是），
        // 所以命中判定统一走描边：线宽算进去，容差靠把线临时加粗来实现——
        // 于是"橡皮圆碰到这条线"就等价于"点落在加粗了 2r 的线上"，不用自己算距离。
        //
        // 注：非等比变换下"线宽不变"还没实现（见计划文档 7.1），这里按局部线宽判定。
        return geo.StrokeContainsPoint(p, MathF.Max(1f, Width) + tolerance * 2f, Gfx.Round);
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
            float d2 = DistToSegmentSq(x, y, Points[i - 1].X, Points[i - 1].Y,
                                      Points[i].X, Points[i].Y, out float t);
            // **被擦掉的那一段不算墨**：拆对象的时候缺口是真的（那一段对象都没了），
            // 改成区间表之后缺口只是"不画"，所以命中测试必须自己跳过——否则拿橡皮
            // 去点一个明明已经擦空的缺口，会把整条笔迹删掉。
            if (IsParamErased((i - 1) + t)) continue;
            if (d2 < best) best = d2;
        }

        // 图形（矩形/椭圆/箭头）不走这里：它们的轮廓不是"两个端点之间的线段"，
        // 近似会偏，命中判定统一走 HitTestExact（见 EraseAt）。
        return MathF.Sqrt(best);
    }

    private static float DistToSegmentSq(float px, float py, float ax, float ay, float bx, float by)
        => DistToSegmentSq(px, py, ax, ay, bx, by, out _);

    /// <summary>点到线段的距离平方；顺带给出最近点的参数 t（0..1），命中测试要用。</summary>
    private static float DistToSegmentSq(float px, float py, float ax, float ay,
                                         float bx, float by, out float t)
    {
        float vx = bx - ax, vy = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = vx * vx + vy * vy;
        t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - vx * t, dy = wy - vy * t;
        return dx * dx + dy * dy;
    }

    /// <summary>与矩形是否相交（画布坐标）。粗筛用，只看世界包围盒。</summary>
    public bool IntersectsRect(RectF r) => WorldBounds.Intersects(r);

    public bool IsShape => Kind != StrokeKind.Freehand;

    /// <summary>
    /// 笔迹的墨是"中心线 + 圆头圆角描边"，**由 Direct2D 自己算轮廓**
    /// （`DrawGeometry` + `Gfx.Round`），我们不再自己拼轮廓。
    ///
    /// 2026-09-14：用户实测后拍板——D2D 的观感比我们自己拼的轮廓好，
    /// 于是**把我们那一套整个删掉**（等宽带子、半圆端帽、外侧圆弧、内角补角、
    /// 压感→宽度映射，以及为它服务的几何细分缓存）。
    ///
    /// 代价（明确接受）：**一条笔画只能有一个宽度**——压感不再影响粗细。
    /// 采样点上仍然记着压力值（存档、将来要用还拿得到），只是渲染不吃它。
    /// </summary>
    public bool IsSinglePoint => Kind == StrokeKind.Freehand && Points.Count == 1;

    /// <summary>
    /// 套索判据用的**代表点集**（画布坐标），追加到 <paramref name="dst"/>。
    ///
    /// 三种对象各取"它自己那个形状"，不能一律拿 Points 顶上：
    ///   · **自由笔迹**：采样点。**跳过被像素橡皮擦掉的那一段**——擦掉的墨不算墨，
    ///     一条被擦掉中间一段的长横线上，缺口里的点会把"圈住左边半截"判成"圈住整条"；
    ///   · **图形**：<see cref="ShapeOutline"/> 的轮廓折线。**不能用两个端点**——
    ///     矩形 / 椭圆的端点是斜对角，拿它当代表点，等于"圈住外框的左上角"就算
    ///     "圈住了整个矩形"；
    ///   · **图像**：四个角。它是"填满的一块"，四角就是它的边界。
    ///
    /// 全部经 <see cref="Transform"/> 变换到画布坐标：套索是在屏幕上圈的，
    /// 判据必须在同一套坐标里（旋转过的对象尤其明显）。
    /// </summary>
    public void AppendRepresentativePoints(List<Vector2> dst)
    {
        if (Points.Count == 0) return;
        bool ident = Transform.IsIdentity;

        if (IsImage)
        {
            var b = Bounds;
            dst.Add(ToCanvas(b.MinX, b.MinY, ident));
            dst.Add(ToCanvas(b.MaxX, b.MinY, ident));
            dst.Add(ToCanvas(b.MaxX, b.MaxY, ident));
            dst.Add(ToCanvas(b.MinX, b.MaxY, ident));
            return;
        }

        if (Kind != StrokeKind.Freehand)
        {
            foreach (var p in ShapeOutline()) dst.Add(ident ? p : Vector2.Transform(p, Transform));
            return;
        }

        // 自由笔迹：按参数跳擦除区间。参数 = 点序号（见 Stroke.Erased）。
        for (int i = 0; i < Points.Count; i++)
        {
            if (Erased.Count > 0 && IsParamErased(i)) continue;
            var q = Points[i];
            dst.Add(ToCanvas(q.X, q.Y, ident));
        }
    }

    private Vector2 ToCanvas(float x, float y, bool ident)
        => ident ? new Vector2(x, y) : Vector2.Transform(new Vector2(x, y), Transform);

    /// <summary>
    /// 图形的**轮廓折线**（局部坐标，首尾相接，自己闭合）。
    ///
    /// 图形不是"点列"，它的轮廓由两个端点推出来（见下面一排 Build*）。渲染和
    /// 命中测试都交给 Direct2D，但**"这一笔和一块矩形有没有碰上"不能用包围盒回答**
    /// ——一个画得很大的圆，它的外框矩形中间是空的，用外框判就会"橡皮从圆心里
    /// 划过，整个圆没了"。所以这里按同一套规则把它展开成折线，逐段判交。
    ///
    /// 规则必须和 Build* 保持一致：改了一边就要改另一边（本函数只服务像素橡皮，
    /// 所以用折线近似——圆 32 段，误差远小于一个笔宽）。
    /// </summary>
    public List<Vector2> ShapeOutline()
    {
        var list = new List<Vector2>();
        if (Points.Count == 0) return list;
        var (a, b) = Endpoints();
        var pa = new Vector2(a.X, a.Y);
        var pb = new Vector2(b.X, b.Y);

        switch (Kind)
        {
            case StrokeKind.Line:
                list.Add(pa);
                list.Add(pb);
                break;

            case StrokeKind.Rectangle:
                list.Add(new Vector2(pa.X, pa.Y));
                list.Add(new Vector2(pb.X, pa.Y));
                list.Add(new Vector2(pb.X, pb.Y));
                list.Add(new Vector2(pa.X, pb.Y));
                list.Add(new Vector2(pa.X, pa.Y));
                break;

            case StrokeKind.Ellipse:
            {
                float rx = MathF.Max(0.5f, MathF.Abs(pb.X - pa.X) * 0.5f);
                float ry = MathF.Max(0.5f, MathF.Abs(pb.Y - pa.Y) * 0.5f);
                float cx = (pa.X + pb.X) * 0.5f, cy = (pa.Y + pb.Y) * 0.5f;
                const int N = 32;
                for (int i = 0; i <= N; i++)
                {
                    float t = i / (float)N * MathF.Tau;
                    list.Add(new Vector2(cx + MathF.Cos(t) * rx, cy + MathF.Sin(t) * ry));
                }
                break;
            }

            case StrokeKind.Arrow:
            {
                float dx = pb.X - pa.X, dy = pb.Y - pa.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-3f) { dx = 1; dy = 0; len = 1; }
                dx /= len; dy /= len;
                float head = Math.Clamp(len * 0.28f, 10f, 48f);
                float hx = pb.X - dx * head, hy = pb.Y - dy * head;
                float nx = -dy, ny = dx;
                float spread = head * 0.45f;
                list.Add(pa);
                list.Add(pb);
                list.Add(new Vector2(hx + nx * spread, hy + ny * spread));
                list.Add(pb);
                list.Add(new Vector2(hx - nx * spread, hy - ny * spread));
                break;
            }

            default:
                // 图像对象：轮廓就是它的矩形（但橡皮不碰图像，见 EraseRectAt）。
                list.Add(new Vector2(pa.X, pa.Y));
                list.Add(new Vector2(pb.X, pa.Y));
                list.Add(new Vector2(pb.X, pb.Y));
                list.Add(new Vector2(pa.X, pb.Y));
                list.Add(new Vector2(pa.X, pa.Y));
                break;
        }
        return list;
    }

    public ID2D1Geometry BuildGeometry(ID2D1Factory1 factory)
    {
        if (Geometry != null && _builtRevision == Revision) return Geometry;
        if (Points.Count == 0) return null;

        if (Geometry != null) { Geometry.Dispose(); Geometry = null; LiveGeometries--; }

        Geometry = Kind switch
        {
            StrokeKind.Rectangle => BuildRectangle(factory),
            StrokeKind.Ellipse => BuildEllipse(factory),
            StrokeKind.Arrow => BuildArrow(factory),
            StrokeKind.Line => BuildLine(factory),
            StrokeKind.Image => BuildImageRect(factory),
            // 自由笔迹：只给**中心线**，描边（宽度、端帽、拐角）交给 D2D。
            // 单点例外——那是一个圆点，几何直接建成圆（渲染那边会填充它）。
            _ => Points.Count == 1 ? BuildDot(factory) : BuildCenterline(factory),
        };
        if (Geometry != null) LiveGeometries++;
        _builtRevision = Revision;
        return Geometry;
    }

    private (InkPoint a, InkPoint b) Endpoints()
        => (Points[0], Points.Count > 1 ? Points[^1] : Points[0]);

    private ID2D1PathGeometry BuildLine(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    private ID2D1PathGeometry BuildRectangle(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, a.Y));
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.AddLine(new Vector2(a.X, b.Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private ID2D1Geometry BuildEllipse(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        float rx = MathF.Max(0.5f, MathF.Abs(b.X - a.X) * 0.5f);
        float ry = MathF.Max(0.5f, MathF.Abs(b.Y - a.Y) * 0.5f);
        return factory.CreateEllipseGeometry(
            new Ellipse(new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f), rx, ry));
    }

    private ID2D1PathGeometry BuildArrow(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        float head = Math.Clamp(len * 0.28f, 10f, 48f);
        float hx = b.X - dx * head, hy = b.Y - dy * head;
        float nx = -dy, ny = dx;
        float spread = head * 0.45f;

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.EndFigure(FigureEnd.Open);
        sink.BeginFigure(new Vector2(hx + nx * spread, hy + ny * spread), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.AddLine(new Vector2(hx - nx * spread, hy - ny * spread));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>图像对象的矩形几何（命中测试与裁剪用，不做描边）。</summary>
    private ID2D1PathGeometry BuildImageRect(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Filled);
        sink.AddLine(new Vector2(b.X, a.Y));
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.AddLine(new Vector2(a.X, b.Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 中心线（开放折线）。**这就是笔迹的几何**：宽度、圆头端帽、拐角全部由
    /// Direct2D 按描边样式自己算（见 Overlay 里的 `DrawGeometry(..., Gfx.Round)`）。
    ///
    /// 2026-09-14：以前这里画的是"我们自己拼的轮廓"（等宽带子 + 半圆端帽 +
    /// 外侧圆弧 + 内角补角 + 压感宽度），用户实测后认为 D2D 的观感更好，
    /// 于是整套删掉。
    /// </summary>
    private ID2D1PathGeometry BuildCenterline(ID2D1Factory1 factory)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();

        // **每条剩下的段一个 figure，但它们在同一条几何里**——这一点是关键：
        // 一次 DrawGeometry 只混合一次，所以半透明荧光笔即使自相重叠也不会变深。
        // 拆成两个对象（两个 DrawGeometry）就会混合两次（实测差 0 → 56）。
        foreach (var (a, b) in RemainingRuns())
        {
            sink.BeginFigure(PointAtParam(a), FigureBegin.Hollow);
            for (int i = 1; i < Points.Count; i++)
            {
                if (i < a - 1e-6f) continue;
                if (i > b + 1e-6f) break;
                sink.AddLine(new Vector2(Points[i].X, Points[i].Y));
            }
            // 终点只在"切出来的插值点"时才补。**必须是这个条件**：如果这一段的终点正好落在
            // 某个采样点上，上面的循环已经把它加进去了，再补一次就给几何多出一个零长段——
            // 没被擦过的笔迹（a=0、b=末尾）必须和"没有区间表"时**逐点一致**，
            // 否则等于凭空改了笔迹几何。
            if (MathF.Abs(b - MathF.Round(b)) > 1e-6f) sink.AddLine(PointAtParam(b));
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 单点笔迹 = 一个圆点。**不能靠描边**：零长度的线描出来什么都没有，
    /// 所以直接给一个半径为半个笔宽的圆，渲染那一侧会填充它。
    /// </summary>
    private ID2D1Geometry BuildDot(ID2D1Factory1 factory)
    {
        float rad = MathF.Max(1f, Width * 0.5f);
        return factory.CreateEllipseGeometry(
            new Ellipse(new Vector2(Points[0].X, Points[0].Y), rad, rad));
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

/// <summary>
/// 把几步合成**一步撤销**。现在只有一个用处：**拖出副本**（插入副本 + 移动副本）——
/// 用户拖错了一下，按一次撤销就该回到"什么都没发生"。
///
/// 约定和别的动作一样：**效果已经应用过了**，这里只负责记账；撤销时反着放回去。
/// </summary>
internal sealed class CompoundAction : EditAction
{
    public readonly List<EditAction> Steps = new();

    public override int HeldStrokes
    {
        get { int n = 0; foreach (var a in Steps) n += a.HeldStrokes; return n; }
    }

    public override RectF AffectedBefore
    {
        get { var r = RectF.Empty; foreach (var a in Steps) r.Add(a.AffectedBefore); return r; }
    }

    public override RectF AffectedAfter
    {
        get { var r = RectF.Empty; foreach (var a in Steps) r.Add(a.AffectedAfter); return r; }
    }

    public override void Undo(InkDocument doc)
    {
        for (int i = Steps.Count - 1; i >= 0; i--) Steps[i].Undo(doc);
    }

    public override void Redo(InkDocument doc)
    {
        foreach (var a in Steps) a.Redo(doc);
    }
}

internal sealed class RemoveStrokesAction : EditAction
{
    public readonly List<(int index, Stroke stroke)> Items = new();
    public override int HeldStrokes => Items.Count;
    public override void Undo(InkDocument doc) { foreach (var it in Items) doc.InsertStroke(it.index, it.stroke); }
    public override void Redo(InkDocument doc) { foreach (var it in Items) doc.RemoveStroke(it.stroke); }
    public override RectF AffectedBefore => EditRegion.Of(Items.Select(it => it.stroke));
}

/// <summary>
/// 像素橡皮：往笔画的**擦除区间表**里加区间（见 <see cref="Stroke.Erased"/>）。
///
/// 一条记录 = 一条**原本就存在**的笔画，两种情况：
///   · 还在（只是被擦掉了几段）→ 撤销就是把区间表换回 Before、重做换回 After；
///   · 整条被擦没了（图形、或细笔整条落在块里）→ 从文档里拿走，撤销时按 Anchor 放回去。
///
/// 一次拖拽 = 一条记录（不管擦到几条、擦了几刀），所以"擦一次 = 一步撤销"照旧。
///
/// 和上一版（把笔迹拆成几个新对象）比，这里少了一大堆麻烦：对象不动，就没有
/// "碎片又被人擦到要并回去""撤销时把上一版碎片放回来"这些事情，`Id` 也保持不变。
/// </summary>
internal sealed class EraseIntervalsAction : EditAction
{
    internal sealed class Item
    {
        public Stroke S;
        /// <summary>这一笔**原本**的擦除区间表（撤销时换回来）。</summary>
        public List<(float a, float b)> Before = new();
        /// <summary>这一笔**擦完之后**的擦除区间表（重做时换回来）。</summary>
        public List<(float a, float b)> After = new();
        /// <summary>整条被擦没了 → 从文档里拿走（撤销要放回去），Anchor 是它的位置。</summary>
        public bool Removed;
        public int Anchor;
        /// <summary>这一次真正变了的那一块（撤销/重做也用它当脏区，别整条重画）。</summary>
        public RectF Paint = RectF.Empty;

        /// <summary>
        /// 撤销时要还原成什么。默认就是 <see cref="S"/>；**图形被熔成笔迹时记的是原来那个图形**
        /// （S 已经换成熔出来的笔迹了）。
        /// </summary>
        public Stroke UndoOriginal;
    }

    public readonly List<Item> Items = new();

    /// <summary>这一批真正需要重画的范围（画布坐标，由 EraseRectAt 累加）。</summary>
    public RectF Paint = RectF.Empty;

    public Item FindItem(Stroke s)
    {
        for (int i = 0; i < Items.Count; i++)
            if (ReferenceEquals(Items[i].S, s)) return Items[i];
        return null;
    }

    /// <summary>一次拖拽里同一笔被擦了好几刀：并到同一条记录上（Before 保持最初那份）。</summary>
    public Item Touch(Stroke s)
    {
        var it = FindItem(s);
        if (it != null) return it;
        it = new Item { S = s, Before = new List<(float a, float b)>(s.Erased) };
        Items.Add(it);
        return it;
    }

    /// <summary>
    /// 算完发现"这一刀其实什么也没改变"（粗筛进来的、或者这一段早就被擦过了）：
    /// 把**刚刚新建**的这条记录撤掉，别让撤销栈里多出一步空操作。
    /// 调用方负责只在"这条记录是这一刀才建的"时候调——已经改过的那种不能丢。
    /// </summary>
    public void Drop(Item it) => Items.Remove(it);

    public override int HeldStrokes => Items.Count;

    /// <summary>
    /// 只报"真正变化的那一块"。基类默认拿动过的对象的包围盒并集当脏区，那太粗了：
    /// 一条笔迹被擦掉中间一小段，框外的墨一点没变，按整条标脏会把这条墨跨过的每一块
    /// 都拖去重画（实测一万笔时每步重画 11.3 块，而橡皮只盖住 2~4 块）。
    /// </summary>
    public override RectF AffectedBefore => Paint;
    public override RectF AffectedAfter => Paint;

    public override void Undo(InkDocument doc) => Apply(doc, restored: true);
    public override void Redo(InkDocument doc) => Apply(doc, restored: false);

    private void Apply(InkDocument doc, bool restored)
    {
        // ① 区间表换回去（撤销）/ 换回来（重做）。
        foreach (var it in Items)
            doc.SetErased(it.S, restored ? it.Before : it.After, it.Paint);

        // ② 整条被擦没的那些：撤销时放回去、重做时再拿走。
        //
        // 位置用 Anchor——"排在这一笔前面的、没被本批拿走的笔画有几条"。下标在拖动过程中
        // 一直在变（每拿走一条，后面的就少 1），只有这个数不变，所以按它插回去不会错位。
        _removed.Clear();
        foreach (var it in Items) if (it.Removed) _removed.Add(it);
        if (_removed.Count == 0) return;

        _removed.Sort((x, y) => x.Anchor.CompareTo(y.Anchor));
        if (restored)
        {
            int inserted = 0;
            foreach (var it in _removed)
                doc.InsertStroke(it.Anchor + inserted++, it.S, it.Paint);
        }
        else
        {
            foreach (var it in _removed) doc.RemoveStroke(it.S, it.Paint);
        }
    }

    private readonly List<Item> _removed = new();
}

/// <summary>
/// "把擦断的笔迹拆成独立对象"这一步的撤销记录：一条原笔迹 → 它的每一段各成一个对象。
///
/// 只在**用户选中了它**的时候才发生（见 <see cref="InkDocument.SplitErasedSelection"/>）。
/// 平时像素橡皮只往区间表里加区间，不拆——那样自交不变深、对象数不涨、身份也不变。
/// </summary>
internal sealed class SplitErasedAction : EditAction
{
    internal sealed class Item
    {
        /// <summary>原笔迹在列表里的下标（**拖拽前那一套坐标系**，见 InkDocument.EndErase）。</summary>
        public int Index;
        /// <summary>擦之前的那一条（整条进撤销栈，回退时原样回来）。</summary>
        public Stroke Original;
        /// <summary>它**原本**的擦除区间表（老存档里可能就有区间；回退时要一并还原）。</summary>
        public List<(float a, float b)> Before = new();
        /// <summary>擦剩下的几段，各自是独立对象。**空 = 整条被擦没了**。</summary>
        public readonly List<Stroke> Parts = new();
    }

    public readonly List<Item> Items = new();

    /// <summary>
    /// 这一次真正变化的那一块（画布坐标）。给了就用它当脏区，不给就退回"动过的对象的
    /// 包围盒并集"（保守但很粗，见 EraseIntervalsAction 的注释）。
    /// </summary>
    public RectF Paint = RectF.Empty;

    public override int HeldStrokes
    {
        get { int n = 0; foreach (var it in Items) n += 1 + it.Parts.Count; return n; }
    }

    public override RectF AffectedBefore => Paint.IsEmpty ? EditRegion.Of(Items.Select(it => it.Original)) : Paint;
    public override RectF AffectedAfter => Paint.IsEmpty ? EditRegion.Of(Items.SelectMany(it => it.Parts)) : Paint;

    public override void Undo(InkDocument doc) => Apply(doc, restored: true);
    public override void Redo(InkDocument doc) => Apply(doc, restored: false);

    private void Apply(InkDocument doc, bool restored)
    {
        // 先把本批涉及的都拿出来（按引用删，不看下标）。
        foreach (var it in Items)
        {
            doc.RemoveStroke(it.Original);
            for (int i = 0; i < it.Parts.Count; i++) doc.RemoveStroke(it.Parts[i]);
        }

        // 再按下标升序放回去。插入点 = 原下标 - 已经处理过的条数 + 前面已经放回去的笔画数
        // （登记时用的是同一套下标：拆分那一步是**按下标降序**逐条处理的，前面的处理不会
        // 动到后面还没处理的下标）。
        var order = Items.OrderBy(it => it.Index).ToList();
        int handled = 0, placed = 0;
        foreach (var it in order)
        {
            int pos = it.Index - handled + placed;
            if (restored)
            {
                doc.InsertStroke(pos, it.Original);
                // 老存档里的区间要跟着还原（新擦的笔迹 Before 是空的）。
                doc.SetErased(it.Original, it.Before, Paint.IsEmpty ? null : Paint);
                placed += 1;
            }
            else
            {
                for (int i = 0; i < it.Parts.Count; i++) doc.InsertStroke(pos + i, it.Parts[i]);
                placed += it.Parts.Count;
            }
            handled++;
        }
    }
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

    /// <summary>
    /// 全文档的**擦除区间总段数**（像素橡皮擦了多少段）。
    ///
    /// 维护成 O(1) 的计数而不是每次遍历统计：手测台要按秒采样它（见 EraserTelemetry），
    /// 遍历一万笔只为了显示一个数，纯属浪费。增删改三处跟着维护。
    /// </summary>
    public int TotalIntervals;
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
    /// <summary>像素橡皮的候选集合（HashSet 是为了"扫一遍笔画列表时 O(1) 判断在不在候选里"）。</summary>
    private readonly HashSet<Stroke> _candidateSet = new();
    /// <summary>像素橡皮命中的 (下标, 前面的干净笔画数, 笔画)。复用，避免拖动时每步分配。</summary>
    private readonly List<(int index, int logical, Stroke s)> _hitScratch = new();
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
        TotalIntervals += s.Erased.Count;
        _grid.Insert(s);
        Dirty.Add(s.PaddedBounds);
        Version++;
    }

    public void InsertStroke(int index, Stroke s) => InsertStroke(index, s, null);

    /// <summary>
    /// 插一条笔画，并**只**把 <paramref name="dirty"/> 标成脏区（null = 按常规标整条的范围）。
    ///
    /// 只有像素橡皮走带脏区的那一版，理由很直接：它把一个对象换成两段，**框外的墨
    /// 一点没变**（同一批点、同一个变换折算后的画布坐标、同样的颜色和宽度），
    /// 所以缓存里框外那些块仍然是有效的。按"整条的范围"标脏会把这条墨跨过的每一块
    /// 都拖去重画——实测一万笔时每步重画 11 块，而橡皮明明只盖住 2 块。
    /// </summary>
    public void InsertStroke(int index, Stroke s, RectF? dirty)
    {
        // 插到中间（撤销"删除"走这里）：顺序变了，块必须整块重画。
        StructureChangedSinceRender = true;
        if (s.Id == 0) s.Id = NextId();
        Strokes.Insert(Math.Clamp(index, 0, Strokes.Count), s);
        TotalPoints += s.Points.Count;
        TotalIntervals += s.Erased.Count;
        _grid.Insert(s);
        Dirty.Add(dirty ?? s.PaddedBounds);
        Version++;
    }

    public void RemoveStroke(Stroke s) => RemoveStroke(s, null);

    /// <summary>删一条笔画。脏区同 <see cref="InsertStroke(int, Stroke, RectF?)"/>（像素橡皮专用）。</summary>
    public void RemoveStroke(Stroke s, RectF? dirty)
    {
        if (!Strokes.Remove(s)) return;
        // **对象从文档里消失了，就不能还留在选中集合里**（否则选中框会挂着一个不存在的东西，
        // 拖它还会给它做变换）。擦除、删除、撤销"新增"都会走到这里——集中一处兜底。
        Selected.Remove(s);
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        // 关键：笔画被移除时必须释放缓存的 Direct2D 几何，否则每擦一次、
        // 每撤销一次都会泄漏一个几何对象（连同它占的 GPU 侧细分数据）。
        // 撤销/重做会重建几何，代价很小；不释放的话一节课能涨到 GB 级。
        s.Release();
        TotalPoints -= s.Points.Count;
        TotalIntervals -= s.Erased.Count;
        Dirty.Add(dirty ?? s.PaddedBounds);
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
        TotalIntervals = 0;
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
    /// 复制拖拽用：把当前选中**原地克隆一份**插进文档，**不进撤销栈**——调用方会在松手时
    /// 把"插入副本 + 拖动副本"合成一步（见 <see cref="CommitCompound"/>）。
    ///
    /// 为什么是"原地重合"而不是像 Ctrl+D 那样偏移 24 像素：拖出副本的手感就是
    /// "从原件上拖出来一份"，起点必须重合（抄 InkClass 的结论：固定偏移的落点不可控）。
    /// </summary>
    public AddStrokesAction CloneSelectedInPlace()
    {
        var act = new AddStrokesAction();
        if (Selected.Count == 0) return act;

        // 护栏和 Ctrl+D 同一套（见 DuplicateSelected 的注释）
        foreach (var s in Selected)
        {
            if (Strokes.Count + act.Strokes.Count + 1 > MaxObjects)
            {
                LastRejectReason = $"批注数量将达到上限 {MaxObjects}，这一份副本没复制。";
                Console.WriteLine("[拒绝] " + LastRejectReason);
                break;
            }
            var c = s.Clone();
            act.Strokes.Add(c);
            AppendStroke(c);        // 这一步给它分配 Id
        }
        if (act.Strokes.Count > 0)
        {
            Selected.Clear();
            Selected.AddRange(act.Strokes);
        }
        return act;
    }

    /// <summary>
    /// 把几步**合成一步**提交。调用方保证这些效果**已经应用过**了
    /// （副本已插入、变换在拖动中逐帧设过），这里只记账。
    /// </summary>
    public void CommitCompound(params EditAction[] steps)
    {
        var c = new CompoundAction();
        foreach (var a in steps) if (a != null) c.Steps.Add(a);
        if (c.Steps.Count > 0) Commit(c);
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
    /// 告诉渲染层"这些块要整块重来"，但**模型本身一个字没改**。
    ///
    /// 用途是拖动预览（见 <c>InkEngine._dragTargets</c> 与方案 B）：手势开始的这一刻，
    /// 被拖的那一批对象要从内容层里**摘出去**——它们原来压过的地方必须重画一次
    /// （这次不带它们）。之后整个手势期间这些块都有效，一帧都不用再碰。
    ///
    /// 为什么需要这个专用入口：块标脏只在"版本号变了"那一支里做
    /// （见 <c>Overlay.SyncTiles</c>），所以"只标脏、不动模型"这件事必须
    /// 同时把版本号往上抬一格，否则这一帧的标脏会被丢掉。
    /// </summary>
    public void InvalidateContent(in RectF r)
    {
        Dirty.Add(r);
        StructureChangedSinceRender = true;
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

    /// <summary>一次插入多条（粘贴用），**算一步撤销**。</summary>
    public void AddStrokes(IReadOnlyList<Stroke> items)
    {
        if (items == null || items.Count == 0) return;
        var act = new AddStrokesAction();
        for (int i = 0; i < items.Count; i++)
        {
            act.Strokes.Add(items[i]);
            AppendStroke(items[i]);
        }
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

        // 撤销也要把自己动过的那块标脏——**和 Commit 用同一句话**，理由一样：
        // 命令自己报告"我动了哪块区域"，漏标就是屏幕上留着撤销前的画面
        // （改变换那类命令只 bump 版本号、不碰 Dirty，原来在这里断了链：
        //  模型回去了、块却还是旧图，自检里"撤销后原位置要有墨、拖过去的地方要干净"
        //  两条当场抓到）。
        //
        // **必须在改之前采样**：AffectedAfter 是"对象**现在**占哪块"（TransformObjectsAction
        // 是现算的），改完再算就变成"原位置"，于是"拖过去的那个位置"永远没被标脏、
        // 在屏幕上留一块幽灵（实测：撤销后新旧两处都有墨）。
        var affected = a.AffectedUnion;
        a.Undo(this);
        Dirty.Add(affected);
        _redo.Add(a);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var a = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        a.Redo(this);
        Dirty.Add(a.AffectedUnion);      // 同 Undo：重做同样要把那块重画
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
        bool standalone = _eraseBatch == null;
        var act = _eraseBatch ?? new RemoveStrokesAction();
        int added = 0;

        var probe = new RectF { MinX = x - radius, MinY = y - radius, MaxX = x + radius, MaxY = y + radius };
        _grid.Query(probe, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        // Copy first: removing strokes mutates the grid we just queried.
        var candidates = _queryScratch.ToArray();
        foreach (var s in candidates)
        {
            // **图像对象不碰**：两种橡皮一致（像素橡皮那边也跳过）。
            // 图像是"老师截来的内容"，不是笔画——橡皮擦掉半张截图不是任何人想要的；
            // 要删它用框选 + Delete。改之前这里对图像会走 IsShape 分支整条删掉，
            // 两种橡皮行为不一致（实测见 调研-图形与选中框.md 第五节）。
            if (s.IsImage) continue;
            if (s.IsShape)
            {
                // 图形的轮廓不是两个端点之间的线段，近似会偏，交给 Direct2D 精确算。
                if (!s.HitTestExact(x, y, radius)) continue;
            }
            else
            {
                // 自由笔迹：墨就是中心线两侧各半个笔宽，点到中心线的距离已经够准，走快的那条。
                float reach = radius + s.Width * 0.5f;
                if (s.DistanceToCanvas(x, y) > reach) continue;
            }
            int index = Strokes.IndexOf(s);
            if (index < 0) continue;
            act.Items.Add((index, s));
            added++;
            RemoveStroke(s);
        }
        if (standalone && act.Items.Count > 0) Commit(act);
        return added;
    }

    private RemoveStrokesAction _eraseBatch;
    private EraseIntervalsAction _intervalBatch;

    /// <summary>像素橡皮算出来的参数区间（复用一个表，拖动时不要每步分配）。</summary>
    private readonly List<(float a, float b)> _intervalScratch = new();

    public void BeginErase() => _eraseBatch = new RemoveStrokesAction();

    /// <summary>像素橡皮：开始一次拖拽。整段拖拽只算**一步**撤销。</summary>
    public void BeginEraseRect() => _intervalBatch = new EraseIntervalsAction();

    public void EndErase()
    {
        if (_eraseBatch != null && _eraseBatch.Items.Count > 0) Commit(_eraseBatch);
        _eraseBatch = null;
        if (_intervalBatch != null && _intervalBatch.Items.Count > 0)
        {
            // 拖拽中用区间表（便宜、不 churn 对象），**松手时才落成独立对象**。
            MaterializeIntervalBatch(_intervalBatch);
            _intervalBatch = null;
        }
    }

    /// <summary>
    /// 像素橡皮的拖拽收尾：**把"擦除区间"落成"独立对象"**。
    ///
    /// 用户 2026-09-15 定的语义："橡皮擦中墨迹或者图形，如果断开了结构，选中分开以后单独算"
    /// ——所以擦完就是**两截各自独立的笔迹**，能分别选中、分别搬、分别删。
    /// 拖拽过程中先用区间表是图便宜（一次拖拽里同一笔可能被擦十几刀，每刀都新建对象会
    /// 又慢又乱），松手一次性结账。
    ///
    /// 三步都不能省：
    ///   ① 把"整条被擦没"的**临时放回列表**——Anchor 记的是"排在它前面的、没被本批拿走的
    ///      画笔有几条"，按它升序插回去，列表就恢复成**拖拽前**的样子；
    ///   ② 这时每个原对象的 `IndexOf` 就是拖拽前那一套下标（撤销记录要用同一套坐标系）；
    ///   ③ 从后往前把每个原对象换成它的碎片（擦光的换成空），下标不会互相干扰。
    /// </summary>
    private void MaterializeIntervalBatch(EraseIntervalsAction act)
    {
        // ① 把被擦光的临时放回去（只为算下标，马上又会被换掉）
        var gone = new List<EraseIntervalsAction.Item>();
        foreach (var it in act.Items) if (it.Removed) gone.Add(it);
        gone.Sort((a, b) => a.Anchor.CompareTo(b.Anchor));
        int inserted = 0;
        foreach (var it in gone) InsertStroke(it.Anchor + inserted++, it.S);

        // ② 记下"拖拽前"的下标；③ 从后往前替换成碎片
        var live = new List<(int index, EraseIntervalsAction.Item it)>();
        foreach (var it in act.Items)
        {
            int idx = Strokes.IndexOf(it.S);
            if (idx >= 0) live.Add((idx, it));
        }
        live.Sort((a, b) => b.index.CompareTo(a.index));

        var split = new SplitErasedAction { Paint = act.Paint };
        foreach (var (index, it) in live)
        {
            var s = it.S;
            var item = new SplitErasedAction.Item
            {
                Index = index,
                Original = it.UndoOriginal ?? s,   // 熔过图形的：还的是原图形
                Before = it.Before,
            };
            item.Parts.AddRange(s.SplitIntoRuns());     // 剩下的每一段 = 一个独立对象
            split.Items.Add(item);

            RemoveStroke(s, it.Paint);
            for (int k = 0; k < item.Parts.Count; k++) InsertStroke(index + k, item.Parts[k], it.Paint);
        }
        if (split.Items.Count > 0) Commit(split);
    }

    /// <summary>
    /// 换掉一条笔画的擦除区间表（像素橡皮 / 撤销 / 重做都走这里）。
    ///
    /// 为什么要包一层：改区间**必须**同时做三件事——几何缓存失效（<c>Revision</c> 会涨，
    /// 见 Stroke.SetErased）、内容层的块标成要重画、脏区记上。少一件就是"数据改了屏幕
    /// 不动"或者"重画范围不对留残影"。
    ///
    /// <paramref name="paint"/> 是"这次真正变了的那一块"；不给就退回整条的包围盒
    /// （保守但很粗，见 EraseIntervalsAction.AffectedBefore 的注释）。
    /// </summary>
    public void SetErased(Stroke s, List<(float a, float b)> intervals, RectF? paint = null)
    {
        TotalIntervals += (intervals?.Count ?? 0) - s.Erased.Count;
        s.SetErased(intervals);
        StructureChangedSinceRender = true;
        Dirty.Add(paint ?? s.PaddedBounds);
        Version++;
    }

    /// <summary>
    /// 像素橡皮：把以 (cx,cy) 为中心、半宽 halfW、半高 halfH 的矩形范围内的墨擦掉。
    /// 返回**受影响的笔画条数**（被切开的、被整条删的，都算一条）。
    ///
    /// 和 <see cref="EraseAt"/>（整笔橡皮）的区别只有一条：自由笔迹**被切成两段**，
    /// 而不是碰到就整条消失。
    ///
    /// 切的时候按**墨迹轮廓**算——矩形往外扩半个笔宽，再和中心线求交。只按中心线
    /// 切的话，圆头端帽会戳进框里半个笔尖，"框里干干净净"就不成立（实测看得出来）。
    ///
    /// 图形（直线 / 矩形 / 椭圆 / 箭头）碰到**整条删**：切成碎线段没有意义，用户也
    /// 没法再拖它的顶点。图像对象**不碰**：那是老师截来的内容，要删它应该用框选 +
    /// Delete——橡皮擦掉半张截图不是任何人想要的。
    /// </summary>
    public int EraseRectAt(float cx, float cy, float halfW, float halfH)
    {
        bool standalone = _intervalBatch == null;
        var act = _intervalBatch ?? new EraseIntervalsAction();

        var rect = new RectF
        {
            MinX = cx - halfW, MinY = cy - halfH,
            MaxX = cx + halfW, MaxY = cy + halfH,
        };

        // 粗筛走空间网格：它按格子回答，会多给几个候选，所以下面还要各自精确判一次。
        _grid.Query(rect, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        // 粗筛：几何上真的碰到的才有资格。
        _candidateSet.Clear();
        for (int i = 0; i < _queryScratch.Count; i++)
        {
            var s = _queryScratch[i];
            if (s.IsImage) continue;
            if (!s.PaddedBounds.Intersects(rect)) continue;
            if (s.Kind != StrokeKind.Freehand && !ShapeTouchesRect(s, rect)) continue;
            _candidateSet.Add(s);
        }
        if (_candidateSet.Count == 0) return 0;

        // **一次线性扫描**同时算出两件事：候选在列表里的下标（只有"整条被擦没"才用得上）、
        // 以及它前面有几条"没被本批拿走的笔画"（Anchor）。
        //
        // 为什么不逐个 IndexOf + 逐个往前数：在一块 93×150 的地方，候选项可以有一两百条，
        // 每条都 O(笔画数) 扫一遍，一万笔的画面上就是"每步几百万次比较"——实测那正是
        // 一步 22ms 里的大头。扫一遍是 O(笔画数)，和候选多少无关。
        _hitScratch.Clear();
        int unaffected = 0;
        for (int i = 0; i < Strokes.Count; i++)
        {
            var t = Strokes[i];
            if (_candidateSet.Contains(t)) _hitScratch.Add((i, unaffected, t));
            unaffected++;
        }
        if (_hitScratch.Count == 0) return 0;

        int affected = 0;
        foreach (var (index, logical, s) in _hitScratch)
            ApplyErase(act, index, logical, s, rect, ref affected);

        // 单次调用（自检、性能测试、以及将来"按一下擦一下"的用法）也要落成独立对象——
        // 和整段拖拽松手时走的是同一个收尾，语义只有一处。
        if (standalone && act.Items.Count > 0) MaterializeIntervalBatch(act);
        return affected;
    }

    /// <summary>
    /// 擦到一笔：算出"被擦掉的参数区间"，并进它的区间表；如果整条都被擦没了，
    /// 就把它从文档里拿走（撤销时按 Anchor 放回来）。
    ///
    /// <paramref name="index"/> 是调用方线性扫描时记下的下标；<paramref name="anchor"/>
    /// 是"排在这一笔前面的、没被本批拿走的笔画有几条"——只有整条被拿走时才用得上。
    /// </summary>
    private void ApplyErase(EraseIntervalsAction act, int index, int anchor,
                            Stroke s, in RectF rect, ref int affected)
    {
        // 这一次真正变了的那一块 = 橡皮矩形 ⊕ 半个笔宽（切口的圆头刚好在这一圈上）
        // + 2 像素抗锯齿余量。框外的墨没变，所以只标这一块当脏区。
        var paint = rect.Inflate(MathF.Max(1f, s.Width) * 0.5f + 2f);
        act.Paint.Add(paint);

        bool isNew = act.FindItem(s) == null;
        var item = act.Touch(s);
        item.Paint.Add(paint);

        // **图形：先熔成笔迹再切**（用户 2026-09-15 定："橡皮擦中图形，断开了也要单独算"）。
        // 熔完在文档里就地替换，下面的擦除逻辑完全不用为图形另开一条路；
        // 撤销靠 item.UndoOriginal 记着原来那个图形。
        if (s.Kind != StrokeKind.Freehand)
        {
            var melted = s.MeltToFreehand();
            int at = Strokes.IndexOf(s);
            if (at < 0) { if (isNew) act.Drop(item); return; }
            RemoveStroke(s, paint);
            InsertStroke(at, melted, paint);
            item.UndoOriginal = s;
            item.S = melted;
            s = melted;
        }

        if (s.Points.Count <= 1)
        {
            // 单点笔迹（一个圆点）：参数只有一个 0，没有长度可分——盖住就整条擦掉。
            if (!DotCovered(s, rect)) { if (isNew) act.Drop(item); return; }
            item.After.Clear();
            SetErased(s, item.After, paint);
            MarkRemoved(act, item, anchor, s, paint);
            affected++;
            return;
        }

        _intervalScratch.Clear();
        if (!ErasedIntervals(s, rect, _intervalScratch))
        {
            if (isNew) act.Drop(item);
            return;
        }

        // 先在副本上算好结果，再决定要不要动对象：没变化就别打掉几何缓存。
        var after = new List<(float a, float b)>(s.Erased);
        foreach (var iv in _intervalScratch) Stroke.MergeInterval(after, iv.a, iv.b);
        if (SameIntervals(after, s.Erased)) { if (isNew) act.Drop(item); return; }

        item.After = after;
        SetErased(s, item.After, paint);
        if (Stroke.RemainingRunsOf(after, s.LastParam).Count == 0)
            MarkRemoved(act, item, anchor, s, paint);
        affected++;
    }

    /// <summary>
    /// 整条被擦没了：从文档里拿走，并记下**放回去的位置**。
    ///
    /// Anchor 要用"没被本批拿走的"笔画来算：本批已经拿走的那几条不在列表里了，
    /// 但它们原本排在前面，所以得把它们补回去（按记录顺序扫一遍）。
    /// 松手时 <see cref="MaterializeIntervalBatch"/> 就靠它把列表恢复成拖拽前的样子。
    /// </summary>
    private void MarkRemoved(EraseIntervalsAction act, EraseIntervalsAction.Item item,
                             int anchor, Stroke s, in RectF paint)
    {
        int fix = 0;
        foreach (var other in act.Items)
            if (other.Removed && other.Anchor <= anchor + fix) fix++;
        item.Removed = true;
        item.Anchor = anchor + fix;
        RemoveStroke(s, paint);
    }

    /// <summary>两张区间表是否逐项相同（用来判断"这一刀其实什么都没改变"）。</summary>
    private static bool SameIntervals(List<(float a, float b)> x, List<(float a, float b)> y)
    {
        if (x.Count != y.Count) return false;
        for (int i = 0; i < x.Count; i++) if (x[i] != y[i]) return false;
        return true;
    }

    /// <summary>单点笔迹（圆点）是否被这块橡皮盖住（画布坐标判定）。</summary>
    private static bool DotCovered(Stroke s, in RectF rect)
    {
        var p = new Vector2(s.Points[0].X, s.Points[0].Y);
        if (!s.Transform.IsIdentity) p = Vector2.Transform(p, s.Transform);
        // 圆点的墨是以 p 为心、半径半个笔宽的圆；"墨被盖住"= 圆心到橡皮的距离 ≤ 半笔宽。
        return DistToRect(p, rect) <= MathF.Max(1f, s.Width) * 0.5f;
    }

    /// <summary>图形和这块矩形碰上了没有（按轮廓判，不按外框——见 Stroke.ShapeOutline）。</summary>
    private static bool ShapeTouchesRect(Stroke s, in RectF rect)
    {
        // 轮廓往外扩半个笔宽（+1 余量）再判交：笔身擦到就算碰到。
        var r = rect.Inflate(MathF.Max(1f, s.Width) * 0.5f + 1f);
        var pts = s.ShapeOutline();
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            if (!s.Transform.IsIdentity)
            {
                a = Vector2.Transform(a, s.Transform);
                b = Vector2.Transform(b, s.Transform);
            }
            if (SegmentHitsRect(a, b, r)) return true;
        }
        return false;
    }

    /// <summary>线段与轴对齐矩形相交（slab 法）。</summary>
    private static bool SegmentHitsRect(Vector2 a, Vector2 b, in RectF r)
    {
        if (PointInRect(a, r) || PointInRect(b, r)) return true;

        float t0 = 0f, t1 = 1f;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        if (!Slab(a.X, dx, r.MinX, r.MaxX, ref t0, ref t1)) return false;
        if (!Slab(a.Y, dy, r.MinY, r.MaxY, ref t0, ref t1)) return false;
        return true;
    }

    private static bool Slab(float origin, float dir, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(dir) < 1e-9f) return origin >= lo && origin <= hi;
        float inv = 1f / dir;
        float ta = (lo - origin) * inv, tb = (hi - origin) * inv;
        if (ta > tb) (ta, tb) = (tb, ta);
        if (ta > t0) t0 = ta;
        if (tb < t1) t1 = tb;
        return t0 <= t1;
    }

    private static bool PointInRect(Vector2 p, in RectF r)
        => p.X >= r.MinX && p.X <= r.MaxX && p.Y >= r.MinY && p.Y <= r.MaxY;

    /// <summary>点到矩形的距离（在框里就是 0）。转角是圆的——这就是"矩形 ⊕ 圆笔头"。</summary>
    private static float DistToRect(Vector2 p, in RectF r)
    {
        float dx = MathF.Max(MathF.Max(r.MinX - p.X, 0f), p.X - r.MaxX);
        float dy = MathF.Max(MathF.Max(r.MinY - p.Y, 0f), p.Y - r.MaxY);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 算出"这一笔被这块橡皮擦掉的**参数区间**"（参数 = 点序号，可以带小数）。
    /// 返回 false = 一点没碰到（调用方原样留着）；true = 至少有一段被擦了（写进 into）。
    ///
    /// 判据和上一版一样：墨在中心线两侧各半个笔宽，所以"墨被盖住"等价于
    /// "中心线上的点到橡皮矩形的距离 ≤ 半笔宽"。进出边界的那一段上二分求切点参数
    /// （距离沿线段是凸的，必收敛；<see cref="EdgeT"/> 与方向无关）。
    ///
    /// 画布坐标只用来**判定**，"擦掉哪一段"用的是笔画自己的参数——所以带变换的笔迹
    /// （被框选移动 / 缩放过）不用再把变换烘进点坐标，区间天生跟着变换走。
    /// </summary>
    private static bool ErasedIntervals(Stroke s, RectF rect, List<(float a, float b)> into)
    {
        int n = s.Points.Count;
        if (n < 2) return false;
        float reach = MathF.Max(1f, s.Width) * 0.5f;

        Vector2 Pt(int i)
        {
            var v = new Vector2(s.Points[i].X, s.Points[i].Y);
            return s.Transform.IsIdentity ? v : Vector2.Transform(v, s.Transform);
        }
        bool In(Vector2 p) => DistToRect(p, rect) <= reach;

        bool touched = false;
        bool inRun = false;
        float runStart = 0f;

        // 起手就在框里（第一段之前没有任何边界要算）
        if (In(Pt(0))) { inRun = true; runStart = 0f; touched = true; }

        // **逐段扫，而且段内要细分**：只看两个端点是不够的——橡皮完全可能整段落
        // 在两个采样点之间（稀疏采样、或者只有两个端点的长直线），只看端点会"擦了个寂寞"。
        // 段内按固定步长细分，跨界处在相邻两个子采样之间二分求切点（精度和整段二分一致）。
        // 代价：密集笔迹（采样点间隔 1~5 像素）每段只多 1~2 次距离计算；
        //       只有"两个端点拉得很开"的笔迹才多算，而那正是原来会漏的那种。
        const float SubStep = 4f;
        for (int i = 0; i + 1 < n; i++)
        {
            Vector2 a = Pt(i), b = Pt(i + 1);
            float len = Vector2.Distance(a, b);
            int sub = Math.Max(1, (int)MathF.Ceiling(len / SubStep));

            float prevT = 0f;
            bool prevIn = In(a);
            for (int k = 1; k <= sub; k++)
            {
                float t = k / (float)sub;
                bool cur = In(Vector2.Lerp(a, b, t));
                if (cur) touched = true;

                if (!inRun && cur)
                {
                    runStart = i + EdgeT(a, b, rect, reach, prevT, t);
                    inRun = true;
                }
                else if (inRun && !cur)
                {
                    into.Add((runStart, i + EdgeT(a, b, rect, reach, prevT, t)));
                    inRun = false;
                }
                prevT = t;
                prevIn = cur;
            }
        }
        if (inRun) into.Add((runStart, n - 1));      // 一直擦到最后一笔
        return touched;
    }

    /// <summary>
    /// 线段上"刚好压在橡皮边界上"那一点的参数 t：在 [<paramref name="lo"/>,
    /// <paramref name="hi"/>] 之间二分，两端一个是框内、一个是框外。
    ///
    /// **方向必须无关**：进框（前一点在外、后点在里面）和出框（反过来）都要用。
    /// 第一版只按"起点在外"写，于是出框那一刀收敛到了框内那个采样点——
    /// 结果是每条的右半边多留了一截墨，切口不在框边而在框里。自检抓到的就是这个。
    /// 框是凸的，距离沿线段先减后增，二分一定收敛（20 次 ≈ 百万分之一，远小于一个像素）。
    /// </summary>
    private static float EdgeT(Vector2 a, Vector2 b, in RectF r, float reach, float lo, float hi)
    {
        bool loInside = DistToRect(Vector2.Lerp(a, b, lo), r) <= reach;
        for (int i = 0; i < 20; i++)
        {
            float mid = (lo + hi) * 0.5f;
            bool midInside = DistToRect(Vector2.Lerp(a, b, mid), r) <= reach;
            if (midInside == loInside) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    /// <summary>
    /// 把**选区里被擦断的**笔迹拆成独立对象（剩下的每一段各成一个）。
    ///
    /// 为什么要有这一步：像素橡皮默认**不拆**（一条笔迹 + 擦除区间表）——那样半透明
    /// 荧光笔自交处不会混合两次、反复擦对象数也不涨、`Id` 也不变。但用户眼睛看到的是
    /// "这里明明断成两截了"，想单独搬动其中一截时，选中整条太反直觉。折中：
    /// **平时不拆，一旦被框选到就拆**——"擦"保持轻量，"单独摆弄某一段"也做得到。
    ///
    /// 拆出来的段继承样式与变换，点仍是**局部坐标**（缩放/旋转过的笔迹拆完也正确）；
    /// 一次框选拆多条的，**算一步撤销**。
    /// </summary>
    /// <returns>拆开了几条（0 = 选中的里面没有被擦断的）</returns>
    public int SplitErasedSelection()
    {
        var act = new SplitErasedAction();

        // **按下标降序**处理：每拆一条都会改变列表长度，从后往前走，前面那些还没处理的
        // 下标才是同一套坐标系（撤销/重做按它插回去才不会错位）。
        var targets = new List<(int index, Stroke s)>();
        foreach (var s in Selected)
        {
            if (!s.HasErased || s.Kind != StrokeKind.Freehand) continue;
            int i = Strokes.IndexOf(s);
            if (i >= 0) targets.Add((i, s));
        }
        if (targets.Count == 0) return 0;
        targets.Sort((a, b) => b.index.CompareTo(a.index));

        var newSelection = new List<Stroke>(Selected);
        foreach (var (index, s) in targets)
        {
            var parts = s.SplitIntoRuns();
            if (parts.Count <= 1) continue;            // 只剩一段（或者全被擦没了），不用拆

            var item = new SplitErasedAction.Item { Index = index, Original = s };
            item.Parts.AddRange(parts);
            act.Items.Add(item);
            newSelection.Remove(s);
            newSelection.AddRange(parts);

            RemoveStroke(s);
            for (int k = 0; k < parts.Count; k++) InsertStroke(index + k, parts[k]);
        }
        if (act.Items.Count == 0) return 0;

        Commit(act);
        Selected.Clear();
        Selected.AddRange(newSelection);
        return act.Items.Count;
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

    /// <summary>
    /// 框选：**框碰到墨就选中那一条**（不是"整条都在框里才选中"）。
    ///
    /// 判据用 <see cref="Stroke.PaddedBounds"/>（中心线外扩到笔身）：笔身擦到框
    /// 就算选中。改成"相交"是被用户实测逼出来的——按"整条都在框里"，屏幕上
    /// 永远选不全：笔迹只要有一头在屏幕外（框拖不到那儿），或者粗笔的笔身压出
    /// 框外一点点，那条就永远选不上，用户看到的就是"我明明全框住了，却没全选中"。
    ///
    /// 代价是：框边碰到一条很长的笔迹会把整条选进来。这和 OneNote 的框选一致，
    /// 也是老师更需要的那个方向（选多了可以点空白重来，选少了会以为软件坏了）。
    /// 判据是"穿过框"，和空间索引给候选用的是同一个框，所以不会漏。
    /// </summary>
    public void ApplyMarquee(RectF r)
    {
        Selected.Clear();
        _grid.Query(r, _queryScratch);
        foreach (var s in _queryScratch)
            if (s.PaddedBounds.Intersects(r)) Selected.Add(s);
    }

    /// <summary>WPF 的 `_percentIntersectForInk`：代表点落进圈里的比例（百分数）。</summary>
    public const float LassoPercentInk = 80f;

    /// <summary>
    /// 套索的判据：**一个对象的代表点里有 80% 以上落在圈里**才算选中。
    ///
    /// 80 这个数是抄微软 WPF 的 `LassoHelper._percentIntersectForInk = 80`——它是
    /// 微软在真实手写板上反复调出来的：太高（100%＝"整条都在圈里"）会像框选那样
    /// 永远选不全（笔头伸出圈外一点点就白圈）；太低会把手滑划过的笔迹一股脑选进来。
    ///
    /// 注意它和**框选**的判据**故意不一样**：框选是"碰到就选"（对应 OneNote），
    /// 套索是"圈住了才算"（对应 WPF 与绝大多数白板）。两种手势的意图本来就不同：
    /// 拖矩形是"我框住这一片"，画一圈是"我把这一条圈起来了"。
    ///
    /// **贴边＝无限延伸**（<paramref name="visible"/> 是当前可见的画布矩形）：
    /// 圈到屏幕边的顶点会被推到"屏幕外很远"（见 ExtendToEdges），于是伸出屏幕的
    /// 那一截也算在圈里。没有这一条，一条横跨屏幕的长笔迹永远选不全——框选当初
    /// 就是为同一个问题才改成"碰到就选"的（见 ApplyMarquee 的注释）。
    ///
    /// 返回**被这一圈判中**的对象数（加减选之前），自检和日志用它。
    /// </summary>
    public int ApplyLasso(IReadOnlyList<Vector2> path, in RectF visible, float edgeSnap,
                          bool additive = false, bool subtractive = false)
    {
        if (path == null || path.Count < 3) return 0;

        // 候选粗筛用**没延伸过**的圈：延伸之后包围盒会到几十万像素外，等于全表扫描。
        // 这不影响正确性——能落在圈里的对象，它的包围盒一定和圈（原始范围）相交；
        // 不相交的，在屏幕上根本看不见（看不见的东西本来也不该被圈进来）。
        var bbox = RectF.Empty;
        foreach (var p in path) bbox.Add(p.X, p.Y);
        bbox = bbox.Inflate(1f);

        _grid.Query(bbox, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        var poly = ExtendToEdges(path, visible, edgeSnap);
        var scratch = new List<Vector2>();
        var hit = new List<Stroke>();
        foreach (var s in _queryScratch)
        {
            scratch.Clear();
            s.AppendRepresentativePoints(scratch);
            if (scratch.Count == 0) continue;

            int inside = 0;
            foreach (var p in scratch)
                if (PointInPolygon(poly, p.X, p.Y)) inside++;

            // 整数比较，避免浮点边界上的 79.99999：inside / Count ≥ 80%
            if (inside * 100 >= scratch.Count * LassoPercentInk) hit.Add(s);
        }

        if (subtractive) { foreach (var s in hit) Selected.Remove(s); }
        else if (additive) { foreach (var s in hit) if (!Selected.Contains(s)) Selected.Add(s); }
        else { Selected.Clear(); Selected.AddRange(hit); }
        return hit.Count;
    }

    /// <summary>"无限远"的替代值：10 万像素。见 ExtendToEdges。</summary>
    private const float EdgeRun = 100_000f;

    /// <summary>
    /// 把"贴着屏幕边"的顶点推到屏幕外很远（EdgeRun），这一步就是
    /// 把"圈到屏幕边"变成"圈到无限远"。
    ///
    /// 为什么推顶点、而不是"把那条边延长成射线"：射线版的数学更干净，但要给多边形
    /// 引入"无穷远边"的概念，后续的奇偶判定、包围盒、预览绘制全都得跟着改。
    /// 推到 10 万像素外，在**可见区域尺度**上和无穷远没有区别（屏幕上最长的一条
    /// 板书也就几千像素），而判据仍然是一个普通的多边形。
    ///
    /// 代价（已知）：被推出去的那个顶点会在屏幕外鼓出一个楔形，圈外、又恰好在
    /// 那个方向上的东西会被多选进来。屏幕上看不出来（那些东西本来就不在可见区域里）。
    /// </summary>
    private static List<Vector2> ExtendToEdges(
        IReadOnlyList<Vector2> path, in RectF visible, float edgeSnap)
    {
        var poly = new List<Vector2>(path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            if (!visible.IsEmpty)
            {
                if (p.X <= visible.MinX + edgeSnap) p.X = visible.MinX - EdgeRun;
                else if (p.X >= visible.MaxX - edgeSnap) p.X = visible.MaxX + EdgeRun;
                if (p.Y <= visible.MinY + edgeSnap) p.Y = visible.MinY - EdgeRun;
                else if (p.Y >= visible.MaxY - edgeSnap) p.Y = visible.MaxY + EdgeRun;
            }
            poly.Add(p);
        }
        return poly;
    }

    /// <summary>
    /// 点在多边形内（射线法 / 奇偶规则）。每点 O(n)，不需要三角化。
    /// </summary>
    public static bool PointInPolygon(IReadOnlyList<Vector2> poly, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            float xi = poly[i].X, yi = poly[i].Y;
            float xj = poly[j].X, yj = poly[j].Y;
            if ((yi > y) == (yj > y)) continue;                 // 这条边不跨过水平线
            float xCross = (xj - xi) * (y - yi) / (yj - yi) + xi;
            if (x < xCross) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 点选：返回 (x,y) 处**最上面**的那个对象（没点到就 null）。
    ///
    /// 判据和橡皮命中同源：自由笔迹看"点到中心线的距离 ≤ 半笔宽 + 容差"
    /// （**被像素橡皮擦掉的那段不算墨**，<see cref="Stroke.DistanceToCanvas"/> 已经跳过）；
    /// 图形 / 图像交给 Direct2D 的描边 / 填充命中。
    ///
    /// 多条叠在一起时取列表里**下标最大**的（最后画的＝最上面）——和 InkClass 一致
    /// （那边是 `hitTestStrokes[^1]`）。
    /// </summary>
    public Stroke HitObjectAt(float x, float y, float tolerance)
    {
        // 粗筛：网格是按"带笔宽的外扩包围盒"索引的，所以点周围一个小矩形就能捞到
        // 所有可能命中的对象（宽笔的笔身伸过来也算）。
        var probe = new RectF
        {
            MinX = x - tolerance, MinY = y - tolerance,
            MaxX = x + tolerance, MaxY = y + tolerance,
        };
        _grid.Query(probe, _queryScratch);
        if (_queryScratch.Count == 0) return null;

        Stroke best = null;
        int bestIndex = -1;
        foreach (var s in _queryScratch)
        {
            if (!s.PaddedBounds.Contains(x, y)) continue;

            bool hit = s.Kind == StrokeKind.Freehand
                ? s.DistanceToCanvas(x, y) <= s.Width * 0.5f + tolerance
                : s.HitTestExact(x, y, tolerance);
            if (!hit) continue;

            int idx = Strokes.IndexOf(s);
            if (idx > bestIndex) { bestIndex = idx; best = s; }
        }
        return best;
    }

    /// <summary>
    /// 点选：把 (x,y) 处的对象选中，返回**被点到的那个**（没点到 = null，调用方继续框选）。
    ///
    /// 修饰键（和主流一致）：<paramref name="additive"/>（Shift）= 加选，已经在选区里就
    /// **移出**（切换）；<paramref name="subtractive"/>（Alt）= 移出。加/减选时不动其它选中，
    /// 方便连着点几条攒出一个选择。
    /// </summary>
    public Stroke SelectAt(float x, float y, float tolerance,
                           bool additive = false, bool subtractive = false)
    {
        var hit = HitObjectAt(x, y, tolerance);
        if (hit == null) return null;

        if (subtractive) Selected.Remove(hit);
        else if (additive) { if (!Selected.Remove(hit)) Selected.Add(hit); }
        else { Selected.Clear(); Selected.Add(hit); }
        return hit;
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
