using System.Numerics;

namespace InkEngine;

/// <summary>
/// 图形的**顶点编辑**：手柄画在哪、点到哪个、拖动之后形状怎么变。
/// 和 <see cref="SelectionHandles"/> 一样，这一层只有数学——一行绘制都没有。
///
/// ## 为什么不和选中框手柄合成一套
///
/// 两套手柄在屏幕上会**重叠**：矩形的四个顶点正好压在选中框的四个角上。
/// 合在一起的结果是"想改形状，结果把图形拉大了"。所以主流软件都把它们分成
/// 两个模式（PowerPoint 的"编辑顶点"、Figma 的双击进入矢量编辑）：
///
///   · **选中模式**（默认）：八个缩放手柄 + 旋转手柄，改的是**外框**；
///   · **顶点模式**（双击形状 / Enter 进入）：顶点手柄，改的是**形状本身**，
///     缩放手柄收起，旋转手柄保留（"改完形状再转一下"是很自然的下一步）。
///
/// ## 命中优先于外框
///
/// 顶点模式下手柄少而精，所以命中判定**先顶点后外框**；两个都够不到就是
/// 框内拖动（整体移动）。顺序反过来的话，顶点手柄会被角手柄吃掉。
/// </summary>
internal static class VertexHandles
{
    /// <summary>顶点手柄的视觉半径（**逻辑**像素，绘制时乘 DPI）。</summary>
    public const float VisualRadiusLogical = 5.5f;

    /// <summary>
    /// 命中半径（逻辑像素）。比视觉大一圈：投影上写字手是抖的，
    /// 而且顶点手柄本来就比角手柄小（角手柄 14 逻辑像素，它 11）。
    /// </summary>
    public const float HitRadiusLogical = 11f;

    /// <summary>这个对象能不能进顶点编辑。</summary>
    public static bool Supports(Stroke s)
        => s != null && s.Kind switch
        {
            StrokeKind.Line or StrokeKind.Arrow or StrokeKind.Rectangle
                or StrokeKind.Ellipse or StrokeKind.Circle
                or StrokeKind.Triangle or StrokeKind.Parallelogram => true,
            _ => false,
        };

    /// <summary>当前选区能不能进顶点编辑（只支持单选一个图形）。</summary>
    public static bool CanEdit(IReadOnlyList<Stroke> sel)
        => sel != null && sel.Count == 1 && Supports(sel[0]);

    /// <summary>
    /// 这个图形**只认顶点**：不显示八个缩放手柄，直接给顶点手柄。
    ///
    /// 判据是"外框拉伸对它有没有意义"：
    ///   · **直线 / 箭头**：拉伸只是把两个端点同时挪动，和"拖端点"是同一件事，
    ///     反而多出八个会误点的把手（用户报的第 2 条）；
    ///   · **三角形 / 平行四边形**：拉伸会让它变成"不规则的三角形 / 不再是平行四边形"，
    ///     所以这两种图形的形状天生就该由顶点说了算（用户报的第 3 条）。
    ///
    /// 矩形 / 椭圆 / 圆保留外框拉伸：拉大拉小是刚需，而且形状不会因此失真
    /// （矩形还是矩形、圆还是圆）。
    /// </summary>
    public static bool PrefersVertices(Stroke s)
        => s != null && s.Kind is StrokeKind.Line or StrokeKind.Arrow
                        or StrokeKind.Triangle or StrokeKind.Parallelogram;

    /// <summary>顶点在**画布坐标**里的位置。</summary>
    public static Vector2 CanvasPosition(Stroke s, int vertexIndex)
    {
        var v = ShapeGeometry.Vertices(s);
        if (vertexIndex < 0 || vertexIndex >= v.Length) return default;
        return Vector2.Transform(v[vertexIndex], s.Transform);
    }

    public static int Count(Stroke s) => ShapeGeometry.Vertices(s).Length;

    /// <summary>
    /// 指针压到哪个顶点上了。返回 -1 = 没压到。
    ///
    /// 命中在**画布坐标**里算（不是把指针变换回局部坐标再量）：手柄画在屏幕上
    /// 是固定大小的，判定就得用屏幕尺度——对象缩到 10% 时，局部坐标里的
    /// "11 像素"在屏幕上只剩 1 像素，那种手柄谁都点不中。
    /// </summary>
    public static int HitTest(Stroke s, float canvasX, float canvasY, float dpiScale)
    {
        if (!Supports(s)) return -1;
        float r = HitRadiusLogical * dpiScale;
        float r2 = r * r;
        var v = ShapeGeometry.Vertices(s);
        for (int i = 0; i < v.Length; i++)
        {
            var p = Vector2.Transform(v[i], s.Transform);
            float dx = p.X - canvasX, dy = p.Y - canvasY;
            if (dx * dx + dy * dy <= r2) return i;
        }
        return -1;
    }

    /// <summary>
    /// 把第 <paramref name="vertexIndex"/> 个顶点拖到画布坐标 (x,y)。
    /// 返回 true = 形状真的变了（调用方据此标脏区）。
    ///
    /// 指针要先反变换回**对象自己的坐标系**：对象转着的时候，"把角拖到这里"
    /// 只有在它自己的坐标系里才有意义（否则一转，矩形就不再是矩形了）。
    /// </summary>
    public static bool DragTo(Stroke s, int vertexIndex, float canvasX, float canvasY)
    {
        if (!Supports(s)) return false;

        var p = new Vector2(canvasX, canvasY);
        if (!s.Transform.IsIdentity)
        {
            if (!Matrix3x2.Invert(s.Transform, out var inv)) return false;
            p = Vector2.Transform(p, inv);
        }
        return ShapeGeometry.MoveVertex(s, vertexIndex, p);
    }
}
