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
    private const int MaxRects = 12;
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
        if (_rects.Count > MaxRects) Coalesce();
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

    private void Coalesce()
    {
        var u = _rects[0];
        for (int i = 1; i < _rects.Count; i++) u.Add(_rects[i]);
        _rects.Clear();
        _rects.Add(u);
    }
}

internal enum StrokeKind
{
    Freehand = 0,
    Line = 1,
    Rectangle = 2,
    Ellipse = 3,
    Arrow = 4,
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
        if (Realization != null) { Realization.Dispose(); Realization = null; LiveRealizations--; }

        // 细分缓存每个约 18 KB，必须设上限：超了就退回"每次重新细分"，
        // 宁可慢一点也不能让内存无上限增长。
        if (LiveRealizations >= MaxRealizations) return null;

        var geo = BuildGeometry(Gfx.D2DFactory);
        if (geo == null) return null;
        try
        {
            Realization = ctx1.CreateFilledGeometryRealization(geo, tolerance);
            if (Realization != null) LiveRealizations++;
        }
        catch { Realization = null; }
        _realizationRevision = Revision;
        return Realization;
    }

    private int _realizationRevision = -1;

    /// <summary>当前存活的细分缓存数量与上限（跨所有笔画）。</summary>
    public static int LiveRealizations;
    public static int MaxRealizations = 4096;
    public RectF Bounds = RectF.Empty;

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

    /// <summary>Releases the cached Direct2D geometry.</summary>
    public void Release()
    {
        if (Geometry != null) ReleasedGeometries++;
        Geometry?.Dispose();
        Geometry = null;
        if (Realization != null) { Realization.Dispose(); Realization = null; LiveRealizations--; }
        _builtRevision = -1;
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
        Bounds = RectF.Empty;
        foreach (var pt in Points) Bounds.Add(pt.X, pt.Y);
        Revision++;
    }

    /// <summary>Swaps in a simplified point list (used by RDP on completion).</summary>
    public void ReplacePoints(List<InkPoint> points)
    {
        Points.Clear();
        Points.AddRange(points);
        Bounds = RectF.Empty;
        foreach (var p in Points) Bounds.Add(p.X, p.Y);
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
    public RectF PaddedBounds => Bounds.Inflate(
        Width * BoundsInflateFactor * 0.5f + 2f);

    /// <summary>Distance in pixels from a point to this item's outline.</summary>
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

    public bool IntersectsRect(RectF r) => Bounds.Intersects(r);

    public bool ContainedInRect(RectF r)
        => !Bounds.IsEmpty && Bounds.MinX >= r.MinX && Bounds.MaxX <= r.MaxX
        && Bounds.MinY >= r.MinY && Bounds.MaxY <= r.MaxY;

    public bool IsShape => Kind != StrokeKind.Freehand;

    public ID2D1Geometry BuildGeometry(ID2D1Factory1 factory)
    {
        if (Geometry != null && _builtRevision == Revision) return Geometry;
        if (Points.Count == 0) return null;

        Geometry?.Dispose();
        Geometry = null;

        Geometry = Kind switch
        {
            StrokeKind.Rectangle => BuildRectangle(factory),
            StrokeKind.Ellipse => BuildEllipse(factory),
            StrokeKind.Arrow => BuildArrow(factory),
            StrokeKind.Line => BuildLine(factory),
            _ => BuildRibbon(factory),
        };
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
}

internal sealed class AddStrokesAction : EditAction
{
    public readonly List<Stroke> Strokes = new();
    public override void Undo(InkDocument doc) { foreach (var s in Strokes) doc.RemoveStroke(s); }
    public override void Redo(InkDocument doc) { foreach (var s in Strokes) doc.AppendStroke(s); }
}

internal sealed class RemoveStrokesAction : EditAction
{
    public readonly List<(int index, Stroke stroke)> Items = new();
    public override void Undo(InkDocument doc) { foreach (var it in Items) doc.InsertStroke(it.index, it.stroke); }
    public override void Redo(InkDocument doc) { foreach (var it in Items) doc.RemoveStroke(it.stroke); }
}

internal sealed class ClearAction : EditAction
{
    public readonly List<Stroke> Removed = new();
    public override void Undo(InkDocument doc) { foreach (var s in Removed) doc.AppendStroke(s); }
    public override void Redo(InkDocument doc) { doc.ClearStrokes(); }
}

// ---------------------------------------------------------------------------

internal sealed class InkDocument
{
    public readonly List<Stroke> Strokes = new();
    public readonly List<Stroke> Selected = new();
    public readonly DirtyRegion Dirty = new();

    // 撤销栈必须有上限。原来用无上限的 Stack，一节课下来会堆进十万条动作、
    // 每条还持有笔画对象——实测 3 分钟就多占约 80 MB。主流软件的撤销深度
    // 都在 100~200 步，超过就从最旧的开始丢。
    private const int MaxUndoDepth = 200;
    private readonly List<EditAction> _undo = new();
    private readonly List<EditAction> _redo = new();

    /// <summary>Bumped on every change so windows know to repaint.</summary>
    public int Version;

    public long TotalPoints;
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    private readonly SpatialGrid _grid = new();
    private readonly List<Stroke> _queryScratch = new();
    public int GridCells => _grid.CellCount;

    /// <summary>Candidate lookup through the spatial index (for measurement).</summary>
    public int QueryGrid(RectF r, List<Stroke> results) => _grid.Query(r, results);

    /// <summary>
    /// Set when the only pending change is a brand-new stroke that is already on
    /// top of everything else. Then the affected region can simply be drawn into
    /// instead of cleared and re-rasterised - which is the common case, because
    /// it happens every time the user finishes a stroke.
    /// </summary>
    public Stroke PendingAppend;

    // -- mutation primitives (no history; the actions below drive these) ---

    public void AppendStroke(Stroke s)
    {
        bool cleanSlate = !Dirty.Full && Dirty.Rects.Count == 0;
        Strokes.Add(s);
        TotalPoints += s.Points.Count;
        _grid.Insert(s);
        Dirty.Add(s.PaddedBounds);
        PendingAppend = cleanSlate ? s : null;
        Version++;
    }

    public void InsertStroke(int index, Stroke s)
    {
        PendingAppend = null;
        Strokes.Insert(Math.Clamp(index, 0, Strokes.Count), s);
        TotalPoints += s.Points.Count;
        _grid.Insert(s);
        Dirty.Add(s.PaddedBounds);
        Version++;
    }

    public void RemoveStroke(Stroke s)
    {
        if (!Strokes.Remove(s)) return;
        PendingAppend = null;
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
        PendingAppend = null;
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
        if (_undo.Count > MaxUndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
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
        if (_undo.Count > MaxUndoDepth) _undo.RemoveAt(0);
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
            float reach = radius + s.Width * Stroke.MaxWidthFactor * 0.5f;
            if (s.DistanceTo(x, y) > reach) continue;
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

    public void BeginErase() => _eraseBatch = new RemoveStrokesAction();

    public void EndErase()
    {
        if (_eraseBatch != null && _eraseBatch.Items.Count > 0) Commit(_eraseBatch);
        _eraseBatch = null;
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
    {
        foreach (var s in Selected)
        {
            PendingAppend = null;
            _grid.Remove(s);
            Dirty.Add(s.PaddedBounds);       // erase the old position
            for (int i = 0; i < s.Points.Count; i++)
            {
                var p = s.Points[i];
                p.X += dx; p.Y += dy;
                s.Points[i] = p;
            }
            s.Bounds = RectF.Empty;
            foreach (var p in s.Points) s.Bounds.Add(p.X, p.Y);
            s.Release();
            _grid.Insert(s);
            Dirty.Add(s.PaddedBounds);       // repaint the new position
        }
        if (Selected.Count > 0) Version++;
    }

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
        PendingAppend = null;
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
