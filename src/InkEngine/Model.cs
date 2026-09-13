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
    /// Fitted cubic Beziers describing the centreline, one Vector2[4] per
    /// segment. When present the ribbon is built from this instead of the raw
    /// polyline, which is what stops the outline looking faceted.
    /// </summary>
    public List<Vector2[]> Centerline;

    /// <summary>Normalised pressure sampled evenly along the stroke, then
    /// smoothed. Driving width from this instead of per-sample pressure removes
    /// the lumpy variation that raw pen pressure produces.</summary>
    public float[] WidthProfile;

    /// <summary>
    /// 美化后的闭合轮廓（虚拟桌面坐标）。由 StrokeBeautifier 在落笔结束时算一次，
    /// 之后每次重画都直接用它，不再重算——这是"写的时候不卡"的前提。
    ///
    /// 它同时承载了：速度→粗细、起收笔渐细、圆头端帽、尖角圆弧、边缘粗糙度。
    /// 渲染管线本身一行没改，只是喂给它的点从"左右各一个"变成了这条轮廓。
    /// </summary>
    public Vector2[] Outline;

    /// <summary>
    /// 这一笔是否走了"手写美化"。false = 精确模式：等宽、不做任何造型，
    /// 画线段和几何图形用。界面上的"手写美化"开关就是在切这个。
    /// </summary>
    public bool Beautified;

    /// <summary>
    /// 诊断用：完全不做任何后处理。笔迹就是原始采样点连出来的等宽带子，
    /// 没有 1€ 滤波、没有抽稀、没有贝塞尔拟合、没有笔锋。
    /// 用来回答"最初的样子是什么""某个观感问题到底出在哪一层"。
    /// </summary>
    /// <remarks>
    /// **当前默认是 true（= 后处理全部关掉）**：这一阶段要先看"原始采样点"
    /// 长什么样，才能判断哪些观感问题其实是自己写的平滑 / 美化带来的。
    /// 命令行加 --smooth 可以重新打开全部后处理。
    /// </remarks>
    public static bool RawInk = true;
    /// <summary>诊断用：算完轮廓后把拐角附近的点打到控制台。</summary>
    public static bool DumpOutline;

    /// <summary>当前使用的笔锋预设。换了预设要重新算轮廓。</summary>
    /// <summary>默认不做手写美化，等宽渲染。见 InkEngine.PenPresetValue 的说明。</summary>
    public PenPreset Preset = PenPreset.Precise;

    /// <summary>
    /// 美化时每个采样点的宽度（直径，物理像素）。仅供自检与调试。
    ///
    /// 存在笔画自己身上，不放在 StrokeBeautifier 的静态字段里——静态字段会被
    /// 下一条笔画覆盖，自检就会读到别人的数据（实测栽过：判定用的数字和
    /// 实际这一笔对不上，来回查了好几轮）。
    /// </summary>
    public float[] BeautifiedWidths;

    /// <summary>
    /// 美化后轮廓能超出名义笔宽多少（倍数）。脏区和命中测试要用。
    ///
    /// 为什么是 1.45：速度模拟出来的压力会超过 1（连续慢写时压力累积），加上圆头
    /// 端帽和粗糙边缘，实测最宽的毛笔档会到名义宽度的约 1.43 倍。
    /// 取小了不是"笔迹看着细"，而是**脏区算小、快速书写留下残影**——
    /// 这类 bug 很显眼又难查，所以这里宁可留足。
    /// </summary>
    public const float OutlineMaxFactor = 1.45f;

    /// <summary>How far the fitted curve strays from the sampled points, in
    /// pixels. A sanity number: too large and the "smoothing" is distortion.</summary>
    public float FitMaxErrorPx;

    public const int ProfileSamples = 48;

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

    /// <summary>
    /// Turns a raw freehand stroke into a fitted curve plus a smoothed width
    /// profile. Called once, when the pen lifts.
    /// </summary>
    public void Beautify(float scale)
    {
        // 诊断开关：把这一笔的**所有**后处理都跳过，直接画原始采样点。
        // 用来回答"最开始是什么样"——没有滤波、没有抽稀、没有拟合、没有笔锋。
        if (RawInk)
        {
            Beautified = false;
            Outline = null;
            Centerline = null;
            WidthProfile = null;
            return;
        }

        if (Kind != StrokeKind.Freehand || Points.Count < 4) return;

        WidthProfile = BuildWidthProfile(Points, ProfileSamples);

        // 手写美化：算一次笔锋轮廓（速度→粗细 + 起收笔渐细 + 圆头端帽）。
        // 有真实压感就用压感，没有就用速度模拟——鼠标和普通触摸屏也有笔锋。
        bool hasPressure = HasRealPressure();
        Outline = StrokeBeautifier.BuildOutline(Points, hasPressure, Width,
                                                StrokeBeautifier.Preset(Preset), scale);
        Beautified = Outline != null;
        if (Outline != null)
        {
            // 把这一笔用的宽度曲线留下来（自检用），来源见 StrokeBeautifier。
            BeautifiedWidths = StrokeBeautifier.LastWidths;
            Revision++;
        }

        if (DumpOutline && Outline != null)
        {
            // 拐角在中心线的中途。打印轮廓里横坐标接近拐角的那一段点，
            // 直接看几何长什么样（洞是不是轮廓自己就缺）。
            var corner = SimplifiedCorner();
            Console.WriteLine($"  [轮廓] 共 {Outline.Length} 点，拐角大约在 ({corner.X:F0},{corner.Y:F0})");
            foreach (var p in Outline)
            {
                if (MathF.Abs(p.X - corner.X) < 60 && MathF.Abs(p.Y - corner.Y) < 60)
                    Console.WriteLine($"    ({p.X - corner.X,7:F1},{p.Y - corner.Y,7:F1})");
            }
        }

        var simplified = Simplify.Rdp(Points, 0.5f * scale);
        if (simplified.Count < 2) return;

        var verts = new List<Vector2>(simplified.Count);
        foreach (var p in simplified) verts.Add(new Vector2(p.X, p.Y));

        // Tolerance: how far the curve may stray from the sampled points.
        // Sub-pixel, so the fit is faithful rather than a reshaping.
        var curves = CurveFit.FitCurve(verts, 0.75f * scale);
        if (curves.Count == 0) return;

        Centerline = curves;
        ReplacePoints(simplified);   // polyline kept for hit testing

        // Measure how faithfully the curve follows the input.
        float worst = 0;
        foreach (var v in verts)
        {
            float best = float.MaxValue;
            foreach (var seg in curves)
            {
                // Sample at ~2 px so the measurement is not limited by the
                // sampling itself; 16 steps over a long segment would read as
                // several pixels of error that is not really there.
                float approx = Vector2.Distance(seg[0], seg[1])
                             + Vector2.Distance(seg[1], seg[2])
                             + Vector2.Distance(seg[2], seg[3]);
                int steps = Math.Clamp((int)MathF.Ceiling(approx / 2f), 8, 160);
                for (int i = 0; i <= steps; i++)
                {
                    float d2 = Vector2.DistanceSquared(CurveFit.Evaluate(seg, i / (float)steps), v);
                    if (d2 < best) best = d2;
                }
            }
            if (best > worst) worst = best;
        }
        FitMaxErrorPx = MathF.Sqrt(worst);
    }

    private static float[] BuildWidthProfile(List<InkPoint> pts, int samples)
    {
        var profile = new float[samples];
        int n = pts.Count;
        if (n == 0) return profile;
        if (n == 1)
        {
            Array.Fill(profile, Math.Clamp(pts[0].P, 0f, 1f));
            return profile;
        }

        var len = new float[n];
        for (int i = 1; i < n; i++)
        {
            float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            len[i] = len[i - 1] + MathF.Sqrt(dx * dx + dy * dy);
        }
        float total = MathF.Max(1e-3f, len[n - 1]);

        int seg = 0;
        for (int i = 0; i < samples; i++)
        {
            float target = total * i / (samples - 1f);
            while (seg < n - 2 && len[seg + 1] < target) seg++;
            float span = MathF.Max(1e-4f, len[seg + 1] - len[seg]);
            float f = Math.Clamp((target - len[seg]) / span, 0f, 1f);
            profile[i] = pts[seg].P + (pts[seg + 1].P - pts[seg].P) * f;
        }

        // Two passes of a 5-tap box filter: enough to make the width read as a
        // smooth taper instead of a wobble.
        for (int pass = 0; pass < 2; pass++)
        {
            var copy = (float[])profile.Clone();
            for (int i = 0; i < samples; i++)
            {
                float sum = 0; int count = 0;
                for (int k = -2; k <= 2; k++)
                {
                    int j = i + k;
                    if (j < 0 || j >= samples) continue;
                    sum += copy[j]; count++;
                }
                profile[i] = sum / count;
            }
        }
        return profile;
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
        Width * MathF.Max(MaxWidthFactor, OutlineMaxFactor) * 0.5f + 2f);

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
    private ID2D1Geometry BuildRibbon(ID2D1Factory1 factory)
    {
        // 美化轮廓优先：它是落笔结束时算好的，直接填充即可，
        // 既省掉每帧重新算宽度，也保证了书写时的形状和落笔后完全一致。
        if (Outline != null && Outline.Length >= 3)
            return BuildGeometryFromOutline(factory);

        // 精确模式（画线段/图形）：等宽带子，不读宽度曲线。
        // 读宽度曲线的话，速度变化会让直线看着歪歪扭扭——那是"手写感"，
        // 但画图和连线时是噪音。
        if (!Beautified && Centerline != null)
            return BuildRibbonFromCenterline(factory, uniform: true);

        if (Centerline != null && WidthProfile != null && WidthProfile.Length >= 2)
            return BuildRibbonFromCenterline(factory);

        // --rawink：原始采样点直接连成等宽带子，不做三点平滑。
        if (RawInk) return BuildRibbonFromPoints(factory, raw: true);

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

    /// <summary>诊断用：找出中心线上转得最急的那个点（也就是"拐角"）。</summary>
    private Vector2 SimplifiedCorner()
    {
        var best = new Vector2(0, 0);
        float worst = 1f;   // 余弦值，越小越急
        for (int i = 1; i < Points.Count - 1; i++)
        {
            var a = new Vector2(Points[i].X - Points[i - 1].X, Points[i].Y - Points[i - 1].Y);
            var b = new Vector2(Points[i + 1].X - Points[i].X, Points[i + 1].Y - Points[i].Y);
            if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f) continue;
            float cos = Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b));
            if (cos < worst)
            {
                worst = cos;
                best = new Vector2(Points[i].X, Points[i].Y);
            }
        }
        return best;
    }

    /// <summary>
    /// Samples the fitted Beziers densely and builds the outline from those
    /// samples, taking width from the smoothed profile by arc length. Dense
    /// sampling is what makes the edge smooth: the outline is only as faceted
    /// as the samples are far apart.
    /// </summary>
    private ID2D1Geometry BuildRibbonFromCenterline(ID2D1Factory1 factory, bool uniform = false)
    {
        const float stepPx = 1.6f;
        var pts = new List<Vector2>(256);

        foreach (var seg in Centerline)
        {
            float approx = Vector2.Distance(seg[0], seg[1])
                         + Vector2.Distance(seg[1], seg[2])
                         + Vector2.Distance(seg[2], seg[3]);
            int steps = Math.Clamp((int)MathF.Ceiling(approx / stepPx), 4, 96);
            int startIndex = pts.Count == 0 ? 0 : 1;
            for (int i = startIndex; i <= steps; i++)
                pts.Add(CurveFit.Evaluate(seg, i / (float)steps));
        }

        int n = pts.Count;
        if (n < 2) return BuildRibbonFromPoints(factory);

        var len = new float[n];
        for (int i = 1; i < n; i++)
            len[i] = len[i - 1] + Vector2.Distance(pts[i], pts[i - 1]);
        float total = MathF.Max(1e-3f, len[n - 1]);

        var outline = new Vector2[n * 2];
        for (int i = 0; i < n; i++)
        {
            int a = i > 0 ? i - 1 : i;
            int b = i < n - 1 ? i + 1 : i;
            Vector2 dir = pts[b] - pts[a];
            if (dir.LengthSquared() < 1e-8f) dir = Vector2.UnitX;
            dir = Vector2.Normalize(dir);
            var nrm = new Vector2(-dir.Y, dir.X);

            float hw;
            if (uniform)
            {
                hw = Width * 0.5f;
            }
            else
            {
                float t = len[i] / total;
                hw = Width * (0.60f + 0.80f * SampleProfile(t)) * 0.5f;
            }

            outline[i] = pts[i] + nrm * hw;
            outline[n * 2 - 1 - i] = pts[i] - nrm * hw;
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

    private float SampleProfile(float t)
    {
        float x = Math.Clamp(t, 0f, 1f) * (WidthProfile.Length - 1);
        int i0 = (int)x;
        int i1 = Math.Min(i0 + 1, WidthProfile.Length - 1);
        float f = x - i0;
        return WidthProfile[i0] + (WidthProfile[i1] - WidthProfile[i0]) * f;
    }

    private ID2D1Geometry BuildRibbonFromPoints(ID2D1Factory1 factory, bool raw = false)
    {
        int n = Points.Count;
        if (n == 1)
        {
            float rad = MathF.Max(1f, HalfWidthAt(0));
            return factory.CreateEllipseGeometry(
                new Ellipse(new Vector2(Points[0].X, Points[0].Y), rad, rad));
        }

        // Light 3-tap smoothing removes hand jitter without softening the line.
        // The two end points are left untouched: smoothing the head would drag
        // the line tip backwards and make the pen feel like it is lagging.
        var px = new float[n];
        var py = new float[n];
        for (int i = 0; i < n; i++)
        {
            // raw 模式：连这点平滑也不做，就是原始点。
            if (raw || i == 0 || i == n - 1)
            {
                px[i] = Points[i].X;
                py[i] = Points[i].Y;
                continue;
            }
            int a = i > 0 ? i - 1 : i;
            int b = i < n - 1 ? i + 1 : i;
            px[i] = (Points[a].X + Points[i].X * 2f + Points[b].X) * 0.25f;
            py[i] = (Points[a].Y + Points[i].Y * 2f + Points[b].Y) * 0.25f;
        }

        var outline = new Vector2[n * 2];
        for (int i = 0; i < n; i++)
        {
            int a = i > 0 ? i - 1 : i;
            int b = i < n - 1 ? i + 1 : i;
            float dx = px[b] - px[a];
            float dy = py[b] - py[a];
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            float nx = -dy, ny = dx;
            float hw = HalfWidthAt(i);
            outline[i] = new Vector2(px[i] + nx * hw, py[i] + ny * hw);
            outline[n * 2 - 1 - i] = new Vector2(px[i] - nx * hw, py[i] - ny * hw);
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
