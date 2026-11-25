// [职责] 图形绘制期的几何吸附锚点：已有图形的顶点 / 圆心 / 中点 / 原点。
// [测试] --unittest ShapeAnchorSnap

using System.Numerics;

namespace InkEngine;

/// <summary>吸附到的**是哪一种锚点**（只用于视觉反馈与近等距时的优先级）。</summary>
internal enum ShapeAnchorKind
{
    /// <summary>没吸到。</summary>
    None = 0,
    /// <summary>顶点 / 端点 / 焦点 / 立体图形的顶点。</summary>
    Vertex,
    /// <summary>圆心 / 中心 / 底心 / 顶心。</summary>
    Center,
    /// <summary>线段中点（最弱的一种：近等距时让位给顶点与中心）。</summary>
    Midpoint,
    /// <summary>坐标系 / 数轴 / 空间坐标系的原点。</summary>
    Origin,
}

/// <summary>
/// 画图形时"吸到已有图形的关键几何点"的**纯静态**实现（阶段 2，2026-11-25）。
///
/// 三条设计约束：
///   · **状态不挂在引擎上**（ADR-005 D3）：吸住的那个点放这里，引擎只管问、不管存；
///   · **零全表扫描**：候选走文档的空间网格（<see cref="InkDocument.QueryGrid"/>），
///     只看指针附近那一两个格子；
///   · **`Alt` = 完全自由**：开关在调用方（`Engine.ShapeTools`），这里只做几何。
///
/// 自检默认**不吸附**（<see cref="AllowInSelfCheck"/>）：旧 `--shapetest` 在已有图形附近
/// 画线时断言的是精确坐标，默认吸上去会把它弄红。要测吸附时由专项自检自己打开这一档。
/// </summary>
internal static class ShapeAnchorSnap
{
    /// <summary>现在是否吸在一个锚点上（渲染与脏区都读这一个开关）。</summary>
    internal static bool Active;

    /// <summary>吸住的那个点（**画布坐标**）。</summary>
    internal static Vector2 Point;

    /// <summary>吸住的是哪一种锚点。</summary>
    internal static ShapeAnchorKind Kind;

    /// <summary>自检里要不要也吸附（默认关，见类型注释）。</summary>
    internal static bool AllowInSelfCheck;

    /// <summary>近等距判定的容差（**画布像素**）：差这么一点就算"一样近"，按种类优先级挑。</summary>
    private const float TieEpsilon = 0.75f;

    /// <summary>网格查询的输出缓冲（静态复用，绘制期每帧都调）。</summary>
    private static readonly List<Stroke> s_hits = new();

    /// <summary>单个对象的锚点缓冲（同样复用，避免每帧为每个候选分配）。</summary>
    private static readonly List<(Vector2 Pos, ShapeAnchorKind Kind)> s_anchors = new();

    /// <summary>抬笔 / 取消 / 切工具时立即清空（不清就会在屏幕上留一个环）。</summary>
    internal static void Clear()
    {
        Active = false;
        Point = default;
        Kind = ShapeAnchorKind.None;
    }

    /// <summary>
    /// 把"指针这个位置"吸到 `radiusCanvas` 内最近的锚点。
    /// 吸住时顺便把 <see cref="Active"/> / <see cref="Point"/> / <see cref="Kind"/> 写成本次结果；
    /// 没吸住（或调用方不该吸附）时清空状态并返回 false。
    /// </summary>
    internal static bool TrySnap(InkDocument doc, Stroke exclude, Vector2 raw,
                                 float radiusCanvas, out Vector2 snapped, out ShapeAnchorKind kind)
    {
        snapped = raw;
        kind = ShapeAnchorKind.None;
        if (doc == null || !(radiusCanvas > 0f))
        {
            Clear();
            return false;
        }

        var rect = RectF.Empty;
        rect.Add(raw.X - radiusCanvas, raw.Y - radiusCanvas);
        rect.Add(raw.X + radiusCanvas, raw.Y + radiusCanvas);

        doc.QueryGrid(rect, s_hits);
        float best = radiusCanvas;
        int bestRank = 0;
        bool found = false;
        for (int i = 0; i < s_hits.Count; i++)
        {
            var s = s_hits[i];
            if (ReferenceEquals(s, exclude) || s.HiddenForPairing) continue;
            s_anchors.Clear();
            CollectAnchors(s, s_anchors);
            for (int k = 0; k < s_anchors.Count; k++)
            {
                var (pos, kk) = s_anchors[k];
                float d = Vector2.Distance(pos, raw);
                if (d > radiusCanvas) continue;
                int rank = RankOf(kk);
                // 近等距时**不让中点抢走顶点/圆心/原点**（老师连两个顶点时更在意顶点）。
                bool better = !found
                              || d < best - TieEpsilon
                              || (d <= best + TieEpsilon && rank > bestRank);
                if (!better) continue;
                found = true;
                best = d;
                bestRank = rank;
                snapped = pos;
                kind = kk;
            }
        }

        if (!found)
        {
            Clear();
            return false;
        }
        Active = true;
        Point = snapped;
        Kind = kind;
        return true;
    }

    /// <summary>优先级：中点最弱（1），顶点 / 中心 / 原点同档（2）。</summary>
    private static int RankOf(ShapeAnchorKind k)
        => k == ShapeAnchorKind.Midpoint ? 1 : 2;

    /// <summary>
    /// 把一条对象的**几何关键点**（局部坐标 → 画布坐标）追加到 <paramref name="dst"/>。
    ///
    /// 覆盖面与"为什么不给自由笔迹"：
    ///   · 参数化图形给的是**它自己的定义元素**（角点 / 顶点 / 圆心 / 原点），不是采样点；
    ///   · 熔墨产物（<see cref="Stroke.ExactPolyline"/>）只给首尾两端——中间是采样点；
    ///   · 自由笔迹 / 图片 / 文本框 / 矢量图一律不给：它们没有"可对齐的几何点"。
    /// 具体分派见下面四个收集器（按"点的语义"分组，一组一个方法，好读也好改）。
    /// </summary>
    internal static void CollectAnchors(Stroke s, List<(Vector2 Pos, ShapeAnchorKind Kind)> dst)
    {
        if (s == null || dst == null || s.HiddenForPairing) return;
        int n = s.Points.Count;
        if (n == 0) return;

        var t = s.Transform;
        // 熔墨出来的精确折线：首尾是真几何，中间是采样点。
        if (s.ExactPolyline)
        {
            Add(dst, t, Local(s, 0), ShapeAnchorKind.Vertex);
            if (n >= 2) Add(dst, t, Local(s, n - 1), ShapeAnchorKind.Vertex);
            return;
        }

        if (CollectPolylineAnchors(s, dst, t, n)) return;
        if (CollectFrameAnchors(s, dst, t, n)) return;
        if (CollectSolidAnchors(s, dst, t, n)) return;
        // 其余（自由笔迹 / 图片 / 文本框 / 矢量图 / 星形……）没有"可对齐的几何点"，跳过。
    }

    /// <summary>追加一个锚点（局部 → 画布）。</summary>
    private static void Add(List<(Vector2 Pos, ShapeAnchorKind Kind)> dst, Matrix3x2 t,
                            Vector2 local, ShapeAnchorKind kind)
        => dst.Add((Vector2.Transform(local, t), kind));

    /// <summary>第 i 个控制点（局部坐标）。</summary>
    private static Vector2 Local(Stroke s, int i) => new(s.Points[i].X, s.Points[i].Y);

    /// <summary>一条线两个端点那一族：直线 / 箭头（＋中点）。</summary>
    private static bool CollectPolylineAnchors(
        Stroke s, List<(Vector2 Pos, ShapeAnchorKind Kind)> dst, Matrix3x2 t, int n)
    {
        switch (s.Kind)
        {
            case StrokeKind.Line:
            case StrokeKind.Arrow:
                if (n < 2) return true;
                {
                    var a = Local(s, 0);
                    var b = Local(s, n - 1);
                    Add(dst, t, a, ShapeAnchorKind.Vertex);
                    Add(dst, t, b, ShapeAnchorKind.Vertex);
                    Add(dst, t, (a + b) * 0.5f, ShapeAnchorKind.Midpoint);
                }
                return true;
            case StrokeKind.NumberLine:
            case StrokeKind.SpaceCoordinate:
                Add(dst, t, Local(s, 0), ShapeAnchorKind.Origin);
                return true;
            default:
                return false;
        }
    }

    /// <summary>"外框定形"那一族：矩形给四角＋中心，圆 / 椭圆给中心，坐标系给原点。</summary>
    private static bool CollectFrameAnchors(
        Stroke s, List<(Vector2 Pos, ShapeAnchorKind Kind)> dst, Matrix3x2 t, int n)
    {
        switch (s.Kind)
        {
            case StrokeKind.Rectangle:
                if (n < 2) return true;
                {
                    var a = Local(s, 0);
                    var b = Local(s, 1);
                    Add(dst, t, new Vector2(a.X, a.Y), ShapeAnchorKind.Vertex);
                    Add(dst, t, new Vector2(b.X, a.Y), ShapeAnchorKind.Vertex);
                    Add(dst, t, new Vector2(b.X, b.Y), ShapeAnchorKind.Vertex);
                    Add(dst, t, new Vector2(a.X, b.Y), ShapeAnchorKind.Vertex);
                    Add(dst, t, (a + b) * 0.5f, ShapeAnchorKind.Center);
                }
                return true;

            case StrokeKind.Circle:
            case StrokeKind.Ellipse:
                Add(dst, t, s.ShapeCenterLocal, ShapeAnchorKind.Center);
                return true;

            case StrokeKind.ConicEllipse:
                Add(dst, t, s.ShapeCenterLocal, ShapeAnchorKind.Center);
                {
                    var (f1, f2) = s.ConicEllipseFociLocal();
                    Add(dst, t, f1, ShapeAnchorKind.Vertex);
                    Add(dst, t, f2, ShapeAnchorKind.Vertex);
                }
                return true;

            case StrokeKind.Coordinate:
                if (n >= 3) Add(dst, t, s.AxisOriginLocal(), ShapeAnchorKind.Origin);
                return true;

            default:
                return false;
        }
    }

    /// <summary>多边形 / 立体那一族：各顶点 ＋ 中心（旋转体在 <see cref="CollectRevolutionAnchors"/>）。</summary>
    private static bool CollectSolidAnchors(
        Stroke s, List<(Vector2 Pos, ShapeAnchorKind Kind)> dst, Matrix3x2 t, int n)
    {
        switch (s.Kind)
        {
            case StrokeKind.Triangle:
                for (int i = 0; i < 3 && i < n; i++) Add(dst, t, Local(s, i), ShapeAnchorKind.Vertex);
                return true;

            case StrokeKind.Parallelogram:
                for (int i = 0; i < 3 && i < n; i++) Add(dst, t, Local(s, i), ShapeAnchorKind.Vertex);
                if (n >= 3) Add(dst, t, s.ParallelogramFourthLocal(), ShapeAnchorKind.Vertex);
                return true;

            case StrokeKind.Trapezoid:
                for (int i = 0; i < 4 && i < n; i++) Add(dst, t, Local(s, i), ShapeAnchorKind.Vertex);
                return true;

            case StrokeKind.Sector:
                Add(dst, t, Local(s, 0), ShapeAnchorKind.Center);
                if (n >= 2) Add(dst, t, Local(s, 1), ShapeAnchorKind.Vertex);
                if (n >= 3) Add(dst, t, Local(s, 2), ShapeAnchorKind.Vertex);
                return true;

            case StrokeKind.Cuboid:
                if (n < 2) return true;
                {
                    var (x0, y0, x1, y1) = s.CuboidFrontLocal();
                    Add(dst, t, new Vector2(x0, y0), ShapeAnchorKind.Vertex);
                    Add(dst, t, new Vector2(x1, y0), ShapeAnchorKind.Vertex);
                    Add(dst, t, new Vector2(x1, y1), ShapeAnchorKind.Vertex);
                    Add(dst, t, new Vector2(x0, y1), ShapeAnchorKind.Vertex);
                    Add(dst, t, new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f),
                        ShapeAnchorKind.Center);
                }
                return true;

            case StrokeKind.Tetrahedron:
                for (int i = 0; i < 3 && i < n; i++) Add(dst, t, Local(s, i), ShapeAnchorKind.Vertex);
                if (n >= 4) Add(dst, t, s.TetraApexLocal(), ShapeAnchorKind.Vertex);
                return true;

            default:
                break;
        }

        if (CollectRevolutionAnchors(s, dst, t)) return true;
        // 棱柱 / 棱锥 / 棱台：底面中心 ＋ 顶上那个中心（顶点）。
        if (Stroke.IsPrismFamily(s.Kind))
        {
            Add(dst, t, s.PrismBaseCenterLocal(), ShapeAnchorKind.Center);
            if (n >= 3) Add(dst, t, s.PrismApexLocal(), ShapeAnchorKind.Vertex);
            return true;
        }
        return false;
    }

    /// <summary>旋转体那一族：球给球心，圆柱 / 圆锥 / 圆台给上下底心与顶点。</summary>
    private static bool CollectRevolutionAnchors(
        Stroke s, List<(Vector2 Pos, ShapeAnchorKind Kind)> dst, Matrix3x2 t)
    {
        switch (s.Kind)
        {
            case StrokeKind.Sphere:
                Add(dst, t, s.SphereLocal().Center, ShapeAnchorKind.Center);
                return true;

            case StrokeKind.Cylinder:
                {
                    var (cx, topCy, bottomCy, _, _) = s.SolidEllipsesLocal();
                    Add(dst, t, new Vector2(cx, topCy), ShapeAnchorKind.Center);
                    Add(dst, t, new Vector2(cx, bottomCy), ShapeAnchorKind.Center);
                }
                return true;

            case StrokeKind.Cone:
                {
                    var (cx, _, bottomCy, _, _) = s.SolidEllipsesLocal();
                    Add(dst, t, new Vector2(cx, bottomCy), ShapeAnchorKind.Center);
                    Add(dst, t, s.ConeApexLocal(), ShapeAnchorKind.Vertex);
                }
                return true;

            case StrokeKind.ConeFrustum:
                {
                    var (cx, _, bottomCy, _, _) = s.SolidEllipsesLocal();
                    var (top, _, _) = s.ConeFrustumTopLocal();
                    Add(dst, t, new Vector2(cx, bottomCy), ShapeAnchorKind.Center);
                    Add(dst, t, top, ShapeAnchorKind.Center);
                }
                return true;

            default:
                return false;
        }
    }
}
