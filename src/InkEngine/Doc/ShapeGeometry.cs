using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 图形（直线 / 矩形 / 平行四边形 / 椭圆 / 圆 / 三角形 / 箭头）的**几何定义**：
/// 控制点怎么放、几何怎么从控制点长出来、拖动某个顶点之后控制点怎么变。
///
/// ## 为什么单独一个文件
///
/// 自由笔迹和图形是两种东西，混在一起写迟早互相污染：
///
///   · **自由笔迹**是"一串采样点连成的带子"，它的形状就是数据本身，
///     没有"定义"可言（见 Model.cs 的 BuildRibbon）。
///   · **图形**是"几个控制点 + 一条规则"。比如矩形由两个对角控制点定义，
///     四个角是**算出来的**。所以图形可以被"改定义"——这正是顶点拖动
///     （拖一个角、对角不动、形状实时变）能成立的前提。
///
/// 这一层只有几何，不碰文档、不碰渲染、不碰输入。所以它能被
/// `--shapetest` / `--vertextest` 直接单测。
///
/// ## 控制点的约定（这是"大模型也能看懂"的关键）
///
/// 控制点存在 <see cref="Stroke.Points"/> 里，**一律是对象的局部坐标**
/// （对象自己的坐标系，不看 Transform——变换只改矩阵，理由见 Stroke.Transform）。
///
/// | 图形 | 控制点数 | 含义 |
/// |---|---|---|
/// | 直线 / 箭头 | 2 | A、B 两个端点 |
/// | 矩形 / 椭圆 | 2 | 包围盒的两个对角点（局部坐标系里轴对齐）|
/// | 平行四边形 | 3 | A、B、C；第四个顶点 = B + C − A（算出来的）|
/// | 圆 | 2 | 圆心、圆周上一点（半径 = 两点距离）|
/// | 三角形 | 3 | 三个顶点 |
///
/// 局部坐标里"轴对齐"不等于屏幕上轴对齐：对象转 30° 之后，它的矩形
/// 在自己那套坐标里仍然是轴对齐的。这与 Figma / PowerPoint 的图形一致
/// （旋转不改变图形自身的定义）。
/// </summary>
internal static class ShapeGeometry
{
    /// <summary>这个 <see cref="StrokeKind"/> 算不算图形（不是自由笔迹、不是图像）。</summary>
    public static bool IsShape(StrokeKind k)
        => k != StrokeKind.Freehand && k != StrokeKind.Image;

    /// <summary>这个图形需要几个控制点。用来在创建时把点数补齐。</summary>
    public static int ControlPointCount(StrokeKind k) => k switch
    {
        StrokeKind.Parallelogram or StrokeKind.Triangle => 3,
        StrokeKind.Image => 2,
        _ => 2,
    };

    /// <summary>
    /// 新图形落地时的控制点：从"按下点 → 当前点"这个拖动框算出来。
    ///
    /// 一次性把所有控制点都写好（三角形写 3 个、平行四边形写 3 个），
    /// 而不是"先存两个点、以后再补"——后者会让"图形还没抬笔就被橡皮擦碰到"
    /// 这类边界情形读到半截数据。
    /// </summary>
    public static void SetFromDrag(Stroke s, float ax, float ay, float bx, float by,
                                   bool uniform)
    {
        float x0 = MathF.Min(ax, bx), x1 = MathF.Max(ax, bx);
        float y0 = MathF.Min(ay, by), y1 = MathF.Max(ay, by);

        // Shift = 等比：正方形 / 正圆 / 等腰的等边三角形。
        // 判据用"哪条边长"，往大的那边靠——投影上写字手抖，宁可给大一点。
        if (uniform)
        {
            if (s.Kind is StrokeKind.Rectangle or StrokeKind.Ellipse
                        or StrokeKind.Circle or StrokeKind.Triangle
                        or StrokeKind.Parallelogram)
            {
                float side = MathF.Max(x1 - x0, y1 - y0);
                x1 = x0 + side;
                y1 = y0 + side;
            }
            else if (s.Kind is StrokeKind.Line or StrokeKind.Arrow)
            {
                // 直线 / 箭头：吸附到 45° 的整数倍（和 Office 一致）。
                float dx = bx - ax, dy = by - ay;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len > 1e-3f)
                {
                    float step = MathF.PI / 4f;
                    float a = MathF.Round(MathF.Atan2(dy, dx) / step) * step;
                    bx = ax + MathF.Cos(a) * len;
                    by = ay + MathF.Sin(a) * len;
                }
            }
        }

        var pts = PointsFor(s.Kind, ax, ay, bx, by, x0, y0, x1, y1);
        s.SetPoints(pts);
    }

    private static Vector2[] PointsFor(StrokeKind kind,
                                       float ax, float ay, float bx, float by,
                                       float x0, float y0, float x1, float y1)
    {
        float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
        switch (kind)
        {
            case StrokeKind.Triangle:
                // 等腰三角形：底边在下，顶点在上。三个点都是真控制点，
                // 抬笔之后拖任意一个顶点都能自由改形状。
                return new[]
                {
                    new Vector2(x0, y1), new Vector2(x1, y1), new Vector2(cx, y0),
                };

            case StrokeKind.Parallelogram:
                // A = 左下，B = 右下，C = 右上（往左错开 1/4 宽）。
                // 错开一点，一眼就能和矩形区分开。
                float skew = (x1 - x0) * 0.25f;
                return new[]
                {
                    new Vector2(x0, y1), new Vector2(x1, y1), new Vector2(x1 - skew, y0),
                };

            case StrokeKind.Circle:
                // 圆心 + 圆周上一点。半径取拖动框的一半边长里的**大者**，
                // 这样"拖多大框就出多大圆"，不会因为手抖变成个椭圆。
                float r = MathF.Max(x1 - x0, y1 - y0) * 0.5f;
                return new[] { new Vector2(cx, cy), new Vector2(cx + r, cy) };

            default:
                // 直线 / 箭头 / 矩形 / 椭圆：两个点。前两个用原始按下点与当前点
                // （不是归一化后的角），方向才有意义——箭头指哪边靠它。
                return new[] { new Vector2(ax, ay), new Vector2(bx, by) };
        }
    }

    /// <summary>直线 / 箭头：两个端点（局部坐标）。</summary>
    public static (Vector2 a, Vector2 b) Endpoints(Stroke s)
    {
        var a = new Vector2(s.Points[0].X, s.Points[0].Y);
        var b = s.Points.Count > 1
            ? new Vector2(s.Points[^1].X, s.Points[^1].Y)
            : a;
        return (a, b);
    }

    /// <summary>
    /// 图形在**局部坐标**里的真实范围。
    ///
    /// **必须按图形的真实外形算，不能按控制点算。** 这一条是用 bug 换来的：
    /// 圆的控制点是"圆心 + 圆周上一点"，两点的包围盒只是半径那么一小块正方形，
    /// 于是选中框贴在圆心里、空间索引漏掉圆的左半边、脏区也只盖住四分之一——
    /// 表现就是"圆画出来不对"（框不对、擦不到、滚动时留残影）。
    ///
    /// 包围盒是脏区、命中粗筛、空间索引、选中框四个东西的共同依据，
    /// 算错一处，四个地方一起错。
    /// </summary>
    public static RectF BoundsOf(Stroke s)
    {
        var r = RectF.Empty;
        switch (s.Kind)
        {
            case StrokeKind.Circle:
            {
                var c = new Vector2(s.Points[0].X, s.Points[0].Y);
                var rim = s.Points.Count > 1 ? new Vector2(s.Points[1].X, s.Points[1].Y) : c;
                float rad = MathF.Max(0.5f, Vector2.Distance(c, rim));
                r.Add(c.X - rad, c.Y - rad);
                r.Add(c.X + rad, c.Y + rad);
                return r;
            }
            case StrokeKind.Parallelogram:
            {
                var v = Vertices(s);
                r.Add(v[0].X, v[0].Y); r.Add(v[1].X, v[1].Y); r.Add(v[2].X, v[2].Y);
                var d = v[0] + v[2] - v[1];      // A + C − B（见 BuildParallelogram 的注释）
                r.Add(d.X, d.Y);
                return r;
            }
            default:
                // 其余图形（直线/箭头/矩形/椭圆/三角形）的顶点就是数据本身，
                // 取所有点的并集即可。
                for (int i = 0; i < s.Points.Count; i++) r.Add(s.Points[i].X, s.Points[i].Y);
                return r;
        }
    }

    /// <summary>
    /// 存起来的**控制点**（局部坐标）。顶点编辑要拿它做"改前 / 改后"的
    /// 对比与撤销记录——注意它和 <see cref="Vertices"/> 不是一回事
    /// （矩形画四个手柄，只存两个控制点）。
    /// </summary>
    public static Vector2[] ControlPoints(Stroke s)
    {
        var v = new Vector2[s.Points.Count];
        for (int i = 0; i < v.Length; i++)
            v[i] = new Vector2(s.Points[i].X, s.Points[i].Y);
        return v;
    }

    // =====================================================================
    //  顶点（可拖的控制点）
    // =====================================================================

    /// <summary>
    /// 顶点编辑时**画出来的那些小手柄**的位置（局部坐标），以及"拖它等于改哪个控制点"。
    ///
    /// 两个概念分开是刻意的：**画出来的顶点**和**存起来的控制点**不是一一对应。
    /// 矩形的控制点只有两个（对角），但要画四个角——拖任意一个角，对角不动，
    /// 矩形跟着变。平行四边形反过来：存三个点，画三个，第四个是算出来的
    /// （不画手柄，因为它不是自由量：平行四边形的第四个顶点必须由另外三个决定，
    /// 给它一个能拖的手柄只会让形状"不再是平行四边形"）。
    /// </summary>
    public static Vector2[] Vertices(Stroke s) => s.Kind switch
    {
        StrokeKind.Rectangle or StrokeKind.Ellipse => BoundsCorners(s),
        StrokeKind.Circle => new[]
        {
            new Vector2(s.Points[0].X, s.Points[0].Y),
            new Vector2(s.Points[1].X, s.Points[1].Y),
        },
        StrokeKind.Triangle => new[]
        {
            new Vector2(s.Points[0].X, s.Points[0].Y),
            new Vector2(s.Points[1].X, s.Points[1].Y),
            new Vector2(s.Points[^1].X, s.Points[^1].Y),
        },
        StrokeKind.Parallelogram => new[]
        {
            new Vector2(s.Points[0].X, s.Points[0].Y),
            new Vector2(s.Points[1].X, s.Points[1].Y),
            new Vector2(s.Points[2].X, s.Points[2].Y),
        },
        StrokeKind.Line or StrokeKind.Arrow => new[]
        {
            new Vector2(s.Points[0].X, s.Points[0].Y),
            new Vector2(s.Points[^1].X, s.Points[^1].Y),
        },
        _ => Array.Empty<Vector2>(),
    };

    /// <summary>矩形的四个角（局部坐标，轴对齐）。</summary>
    private static Vector2[] BoundsCorners(Stroke s)
    {
        var b = s.Bounds;
        return new[]
        {
            new Vector2(b.MinX, b.MinY), new Vector2(b.MaxX, b.MinY),
            new Vector2(b.MaxX, b.MaxY), new Vector2(b.MinX, b.MaxY),
        };
    }

    /// <summary>
    /// 把第 <paramref name="vertexIndex"/> 个**画出来的顶点**拖到局部坐标
    /// <paramref name="p"/>，返回 true 表示形状真的变了。
    ///
    /// 每个图形在这里回答"拖这个顶点意味着什么"：矩形是"移动这个角、
    /// 对角不动"，圆是"改半径"，三角形是"移动这一个顶点"。
    /// </summary>
    public static bool MoveVertex(Stroke s, int vertexIndex, Vector2 p)
    {
        switch (s.Kind)
        {
            case StrokeKind.Line or StrokeKind.Arrow:
                if (vertexIndex == 0) s.SetPoint(0, p);
                else if (vertexIndex == 1) s.SetPoint(s.Points.Count - 1, p);
                else return false;
                return true;

            case StrokeKind.Rectangle or StrokeKind.Ellipse:
            {
                // 对角不动：另外两个角跟着走，形状永远是"局部轴对齐"的矩形。
                var corners = BoundsCorners(s);
                if (vertexIndex < 0 || vertexIndex >= 4) return false;
                var opposite = corners[(vertexIndex + 2) % 4];
                float x0 = MathF.Min(opposite.X, p.X), x1 = MathF.Max(opposite.X, p.X);
                float y0 = MathF.Min(opposite.Y, p.Y), y1 = MathF.Max(opposite.Y, p.Y);
                s.SetPoint(0, new Vector2(x0, y0));
                s.SetPoint(s.Points.Count - 1, new Vector2(x1, y1));
                return true;
            }

            case StrokeKind.Circle:
            {
                var c = new Vector2(s.Points[0].X, s.Points[0].Y);
                if (vertexIndex == 0)
                {
                    // 拖圆心 = 整圆平移，半径不变（改大小是拖圆周上那个点）。
                    var rim = new Vector2(s.Points[1].X, s.Points[1].Y);
                    var r = rim - c;
                    s.SetPoint(0, p);
                    s.SetPoint(1, p + r);
                }
                else if (vertexIndex == 1)
                {
                    var d = p - c;
                    if (d.Length() < 1f) return false;      // 半径塌成 0 就废了
                    s.SetPoint(1, p);
                }
                else return false;
                return true;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
                if (vertexIndex < 0 || vertexIndex >= s.Points.Count) return false;
                s.SetPoint(vertexIndex, p);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 命中测试用：顶点手柄的半径（**局部坐标**）。
    ///
    /// 为什么按局部坐标给：对象被放大 3 倍时，局部 1 像素等于屏幕 3 像素，
    /// 手柄会跟着变大——那是对的（手柄贴在对象上，看起来就该一起大）。
    /// 界面上再按"屏幕最小可见尺寸"兜底，避免缩得很小时点不中。
    /// </summary>
    public const float VertexHitRadiusLocal = 9f;

    // =====================================================================
    //  几何：控制点 → Direct2D 几何
    // =====================================================================

    /// <summary>按控制点构建描边几何（图形一律是"描边的中心线"，线宽在绘制时给）。</summary>
    public static ID2D1Geometry Build(Stroke s, ID2D1Factory1 factory)
    {
        if (s.Points.Count == 0) return null;
        return s.Kind switch
        {
            StrokeKind.Line => BuildLine(s, factory),
            StrokeKind.Rectangle => BuildRectangle(s, factory),
            StrokeKind.Ellipse => BuildEllipse(s, factory),
            StrokeKind.Circle => BuildCircle(s, factory),
            StrokeKind.Arrow => BuildArrow(s, factory),
            StrokeKind.Triangle => BuildTriangle(s, factory),
            StrokeKind.Parallelogram => BuildParallelogram(s, factory),
            StrokeKind.Image => BuildImageRect(s, factory),
            _ => null,
        };
    }

    private static ID2D1PathGeometry OpenPath(ID2D1Factory1 factory, Vector2 start)
    {
        var geo = factory.CreatePathGeometry();
        var sink = geo.Open();
        sink.BeginFigure(start, FigureBegin.Hollow);
        return geo;                 // 注：sink 的生命周期跟着 geo，见下面每个方法的用法
    }

    private static ID2D1PathGeometry BuildLine(Stroke s, ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints(s);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(a, FigureBegin.Hollow);
        sink.AddLine(b);
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    private static ID2D1PathGeometry BuildRectangle(Stroke s, ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints(s);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(a, FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, a.Y));
        sink.AddLine(b);
        sink.AddLine(new Vector2(a.X, b.Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private static ID2D1Geometry BuildEllipse(Stroke s, ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints(s);
        float rx = MathF.Max(0.5f, MathF.Abs(b.X - a.X) * 0.5f);
        float ry = MathF.Max(0.5f, MathF.Abs(b.Y - a.Y) * 0.5f);
        return factory.CreateEllipseGeometry(
            new Ellipse(new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f), rx, ry));
    }

    private static ID2D1Geometry BuildCircle(Stroke s, ID2D1Factory1 factory)
    {
        var c = new Vector2(s.Points[0].X, s.Points[0].Y);
        var rim = s.Points.Count > 1
            ? new Vector2(s.Points[1].X, s.Points[1].Y)
            : c;
        float r = MathF.Max(0.5f, Vector2.Distance(c, rim));
        return factory.CreateEllipseGeometry(new Ellipse(c, r, r));
    }

    private static ID2D1PathGeometry BuildArrow(Stroke s, ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints(s);
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        // 箭头头部按**链路长度**取，并夹在 10~48：太短了看不见，太长了
        // 一根箭头变成一堆三角。这两个上下限是投影上试出来的。
        float head = Math.Clamp(len * 0.28f, 10f, 48f);
        float hx = b.X - dx * head, hy = b.Y - dy * head;
        float nx = -dy, ny = dx;
        float spread = head * 0.45f;

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(a, FigureBegin.Hollow);
        sink.AddLine(b);
        sink.EndFigure(FigureEnd.Open);
        sink.BeginFigure(new Vector2(hx + nx * spread, hy + ny * spread), FigureBegin.Hollow);
        sink.AddLine(b);
        sink.AddLine(new Vector2(hx - nx * spread, hy - ny * spread));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    private static ID2D1PathGeometry BuildTriangle(Stroke s, ID2D1Factory1 factory)
    {
        var v = Vertices(s);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(v[0], FigureBegin.Hollow);
        sink.AddLine(v[1]);
        sink.AddLine(v[2]);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private static ID2D1PathGeometry BuildParallelogram(Stroke s, ID2D1Factory1 factory)
    {
        var v = Vertices(s);
        // 第四个顶点：四个顶点按 A→B→C→D 首尾相接时满足 **A + C = B + D**，
        // 所以 D = A + C − B。
        //
        // 这里曾经写成 D = B + C − A（把下标记串了），画出来是一个**自交的沙漏**，
        // 包围盒也跟着偏出去一大截——是自检里"包围盒 = 真实占位"那一条抓出来的。
        var d = v[0] + v[2] - v[1];
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(v[0], FigureBegin.Hollow);
        sink.AddLine(v[1]);
        sink.AddLine(v[2]);
        sink.AddLine(d);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>图像用的矩形几何（命中测试与裁剪用，不做描边）。</summary>
    private static ID2D1PathGeometry BuildImageRect(Stroke s, ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints(s);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(a, FigureBegin.Filled);
        sink.AddLine(new Vector2(b.X, a.Y));
        sink.AddLine(b);
        sink.AddLine(new Vector2(a.X, b.Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }
}
