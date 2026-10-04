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

    /// <summary>
    /// 圆（2026-09-19 第二批）：按下 = **圆心**、拖出去 = 半径。
    ///
    /// 和 <see cref="Ellipse"/> 是两个工具，不是"一个工具的两档"：椭圆的定义元素是
    /// **中心 + 两条半轴**（要能分别拉长拉短），圆的定义元素是**圆心 + 半径**（拉哪边都一样）。
    /// 用户看到的也是两件事："画个正圆"和"画个椭圆"。
    /// </summary>
    Circle = 11,

    /// <summary>
    /// 三角形（2026-09-19 第二批第②步）：和其余图形同一套"**一按一拖**"——
    /// 按下拖出一个外框，松手得到"底边水平、左右对称"的三角形（见 <see cref="StrokeKind.Triangle"/>）。
    /// </summary>
    Triangle = 12,

    /// <summary>
    /// 平行四边形（2026-09-19 第二批第②步）：一按一拖出来的底边水平、上边右移 1/4 宽的
    /// 平行四边形（见 <see cref="StrokeKind.Parallelogram"/>）。
    /// </summary>
    Parallelogram = 13,

    /// <summary>
    /// 坐标系（2026-09-19 第三批）：一按一拖定**外框**，得到一个十字轴
    /// （x 轴向右、y 轴向上，正方向端带箭头）＋ 等距刻度线（见 <see cref="StrokeKind.Coordinate"/>）。
    ///
    /// 为什么老师要它：每讲一次函数就要画一次，而且是"画完还得标刻度"的活。
    /// 刻度**数字**这一轮不做（用户 2026-09-19 定："高中数学坐标系画的时候很少会用到这个刻度，
    /// 到时候也会简单标一下"）——所以就没有"往内容层写字"这套地基的负担。
    /// </summary>
    Coordinate = 14,

    /// <summary>
    /// 数轴（2026-09-19 第三批）：一按一拖得到一条**水平**直线（右端带箭头）＋ 等距刻度线。
    /// 初高中的不等式、区间、集合都从数轴开始画（见 <see cref="StrokeKind.NumberLine"/>）。
    /// </summary>
    NumberLine = 15,

    /// <summary>
    /// 抛物线（2026-09-20 第四批）：按下 = **顶点**，拖出去 = **开口控制点**
    /// （一次定下"半宽"和"深度"）。
    ///
    /// **四种开口（上 / 下 / 左 / 右）不是四个工具**，而是对象自己的
    /// <see cref="Stroke.CurveAxis"/> 那一档（见 <see cref="CurveAxis"/> 的注释）：
    /// 四种开口共用的定义元素、手柄、命中、存档全都一模一样，差别只在
    /// "谁是自变量"这一件事上——那就是一个属性，不是一种新图形。
    /// </summary>
    Parabola = 16,

    /// <summary>
    /// 双曲线（2026-09-20 第四批）：按下 = **中心**，拖出去 = **外角点**
    /// （`a = |Δx|`、`b = |Δy|`，和椭圆同一套存法）。
    ///
    /// 方向（实轴沿 x / 沿 y）同样由 <see cref="Stroke.CurveAxis"/> 决定。
    /// **注意方向换了之后实半轴 / 虚半轴的语义会互换**，手柄读数和命名必须跟着换
    /// （见 <see cref="Stroke.HyperbolaALocal"/>）。
    /// </summary>
    Hyperbola = 17,

    /// <summary>
    /// 正弦（2026-09-20 第四批）：**一个周期**的正弦图象。
    ///
    /// 按下 = **图象起点**（第一个零点，也就是老师画的那条 y 轴所在的位置），
    /// 往右拖 = 一个周期宽 ＋ 振幅。他想要两三个周期，就并排画两三条——
    /// 这个对象画出来**就是一个周期**，这正是课本上要的那个形状。
    /// </summary>
    Sine = 18,

    /// <summary>
    /// 余弦（2026-09-20 第四批）：同正弦，只是**起点在峰顶**（`f(0) = 1`）——
    /// 老师把 y 轴画在哪个位置，那个位置就是峰。
    /// </summary>
    Cosine = 19,

    /// <summary>
    /// **圆柱**（2026-09-20 第五批：照 InkClass 的 `case 6` 搬过来）：
    /// 按下 → 拖出**外接矩形**（左右 = 直径、上下 = 母线长），松手就成。
    /// 顶面画整圈、底面只画看得见的下半圈，被挡住的上半圈走**虚线**（辅助几何槽）。
    /// </summary>
    Cylinder = 20,

    /// <summary>
    /// **圆锥**（照 InkClass 的 `case 7`）：同样一次拖出外接矩形，
    /// 顶点 = 矩形**上边中点**，底面椭圆只画下半圈（上半圈虚线），两条母线连到顶点。
    /// </summary>
    Cone = 21,

    /// <summary>
    /// **长方体**（2026-09-20 第五批：照 InkClass 的 `case 9`，**两笔**）：
    /// 第 1 笔拖出**正面矩形**，第 2 笔拖出**深度**（往后上方 45° 退）。
    /// 三个控制点 = 正面矩形两角 ＋ **背面右下角**（它同时把深度记下来，见 `SetCuboidDepth`）。
    /// 背面被挡住的三条棱（背面下横、斜左下、背面左竖）走**虚线**（辅助几何槽）。
    ///
    /// ⚠ **2026-09-20 第十二批：面板入口撤掉了**（用户："四面体／长方体那两格似乎可以
    /// 删除掉了，没用了"）。撤的是**入口**，不是画法：这个工具值、`StrokeKind.Cuboid`
    /// 和整条画法都留着——旧板书里那些长方体要能打开、能选中、能删
    ///（和 2026-09-19 撤「数轴」入口同一条规矩，见 计划-图形工具.md 11.2）。
    /// </summary>
    Cuboid = 22,

    /// <summary>
    /// **四面体**（照 InkClass 的 `case 26`，**两笔**）：第 1 笔拖出**底面三角形**
    ///（底边水平、顶点居中向上），第 2 笔拖出**顶点**。四个控制点 = 底面三点 ＋ 顶点。
    /// 他的画法里**没有虚线棱**（六条棱全实线），我们照旧。
    ///
    /// ⚠ 面板入口同样是 2026-09-20 第十二批撤掉的（和长方体一起）；画法与存档都留着。
    /// 它其实是个**固定的三棱锥**，现在有了「棱锥」那一格（3/4/5/6 ＋ 直/斜），
    /// 想要四面体就画一个三棱锥。
    /// </summary>
    Tetrahedron = 23,

    /// <summary>
    /// **棱柱**（2026-09-20 第十一批，用户提的）：3/4/5/6 棱柱 ＋ 直/斜通吃。
    ///
    /// 画法是**立着**的（和长方体**不是**一个朝向，两者并存，见 计划-图形工具.md §32）：
    ///   · 第 1 笔拖一个框 = **底面投影后的外接框**，底面是内接其中的**正 n 边形**；
    ///   · 第 2 笔拖到**顶面中心**该在的地方（照四面体"拖到哪就是哪"）：
    ///     往上拖 = **直棱柱**（那一笔会被轻微吸附吸到竖直），拖歪 = **斜棱柱**。
    /// 三个控制点 = 底面外接框两角 ＋ **顶面中心**（侧棱向量 = 顶心 − 底心）。
    /// 几边形由 <see cref="Stroke.PrismSides"/> 决定（画之前在图形面板那一格选）。
    /// </summary>
    Prism = 24,

    /// <summary>
    /// **棱锥**（2026-09-20 第十二批，用户提的）：和棱柱**同一套手感**，只是"顶上那个东西"
    /// 从一个面变成一个点——第 1 笔拖**底面外接框**、第 2 笔拖**顶点**该在的地方。
    /// 往上拖 = **直棱锥**（那一笔会被轻微吸附吸到竖直），拖歪 = 斜棱锥。
    /// 三个控制点 = 底面外接框两角 ＋ 顶点；几边形同样由 <see cref="Stroke.PrismSides"/> 定。
    ///
    /// 几何上它和棱柱／棱台是**一族**（底面正 n 边形 ＋ 顶面），差别只在顶面怎么来，
    /// 见 <see cref="Stroke.PrismFamilyEdges"/>。
    /// </summary>
    Pyramid = 25,

    /// <summary>
    /// **棱台**（2026-09-20 第十二批）：棱锥**截掉上面一小截**剩下的那部分——
    /// 上下两个面是**同形的正 n 边形**、下面的比上面大。
    ///
    /// 画法**和棱柱完全一样、也是两笔**（用户定的："上底按固定比例缩，两笔画完"）：
    /// 第 1 笔拖**下底**外接框、第 2 笔拖**上底中心**该在的地方。上底多大不靠拖——
    /// 按固定比例缩出来（<see cref="ShapeSpec.FrustumTopScale"/>），所以少了第三笔。
    ///
    /// ⚠ 因此**拖得越高、看起来越"尖"**是错觉：上底大小由比例定，只跟着下底走。
    /// </summary>
    Frustum = 26,

    /// <summary>
    /// **圆台**（2026-09-20 第十三批，用户："再加一个圆台"）：<see cref="Cone"/> 截掉上面一截。
    ///
    /// 画法和 <see cref="Cylinder"/> / <see cref="Cone"/> **一模一样**：
    /// **拖一个外接矩形，一笔画完**（左右 = 下底直径、上下 = 总高）。上底多大不靠拖——
    /// 按固定比例缩（<see cref="ShapeSpec.FrustumTopScale"/>，和「棱台」同一个数）。
    ///
    /// 所以它是个**一笔**图形（不在 `Engine.PlanOf` 那张多笔表里），
    /// 也没有档位点（和圆柱 / 圆锥一样）。
    /// </summary>
    ConeFrustum = 27,

    /// <summary>
    /// **球**（2026-09-20 第十四批，用户："再加加入球"）：和圆柱 / 圆锥 / 圆台同族，
    /// **拖一个外接矩形、一笔画完**（半径取矩形里内切的正圆，见 `Stroke.SphereLocal`）。
    ///
    /// 它比别的立体多两笔"内部线"：赤道那个椭圆（下半圈实线、上半圈虚线）——
    /// 不然画出来的球和「圆」长得一样（详见 <see cref="StrokeKind.Sphere"/> 那段）。
    /// </summary>
    Sphere = 28,

    /// <summary>
    /// **正切 y = tan x**（2026-09-20 第十五批，用户："可以画正切"）：和正弦 / 余弦放在
    /// 同一行、同一族，但**画法是一笔**（它没有"周期"和"振幅"两个可以分开拖的量）。
    ///
    /// 按下 = 原点（这一支的中心，也是图象和 x 轴的交点）；拖出去 = **以它为中心的框**：
    ///   · 横向 = **半支长** → 左右两条渐近线正好落在框的左右两边；
    ///   · 纵向 = **可视半高** → 曲线冲到那儿就截断（框外的部分不画）。
    /// 横竖**共用一个单位**（见 <see cref="Stroke.TangentUnitLocal"/>），所以它**拖不变形**。
    ///
    /// 一笔图形 → 不在 `Engine.PlanOf` 那张多笔表里；也没有档位点。详见
    /// <see cref="StrokeKind.Tangent"/> 那段。
    /// </summary>
    Tangent = 29,

    /// <summary>
    /// **波浪线**（2026-09-20 第十六批，用户："还有一个另外的很多周期的波浪的弦函数线"）：
    /// 多周期正弦波，**一笔**（和正弦 / 余弦同族，手感也同族）。
    ///
    /// 和「正弦」那一格的分工（用户定的，两格都在曲线那一行）：
    ///   · **正弦** = 框宽就是**一个周期**（讲"一个周期的图象"用）；
    ///   · **波浪线** = 横向拖的是**要画多长**、周期由振幅定
    ///     （讲周期性、多个周期一起看用）。
    /// 详见 <see cref="StrokeKind.Wave"/>。
    /// </summary>
    Wave = 30,

    /// <summary>
    /// **椭圆（带焦点）**（2026-09-22，用户："还有在第二行增加椭圆，要带焦点"）：
    /// 放在**第二行（曲线那一行）**的一格，画出来是一个**圆锥曲线意义上的椭圆**——
    /// 椭圆本身 ＋ **两个焦点**；另有一档"有 / 无焦点三角形"（三角形第三个顶点在椭圆上，
    /// 可以在椭圆上拖着走，用来讲 `|PF₁| + |PF₂| = 2a`）。
    ///
    /// 和第一行那个 <see cref="Tool.Ellipse"/> **两个工具并存**（用户 2026-09-22 拍板：
    /// "新增一格，两格并存"）：那一个是"画个椭圆图形"（只要形状、没有焦点），
    /// 这一个是为了讲焦点 / 焦点三角形的题。
    ///
    /// 定义元素和 <see cref="StrokeKind.Ellipse"/> **完全一样**（中心 ＋ 外角点，
    /// 于是 a = |dx|、b = |dy|），所以画法、两个半轴手柄、紧框**全都共用那一份**
    /// —— 多出来的只有一个"P 在椭圆上的角度"和一个"画不画焦点三角形"的开关。
    /// </summary>
    ConicEllipse = 31,
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

    /// <summary>
    /// 圆（2026-09-19 加）。
    ///
    /// 和直线**同构**：`Points` = **圆心 ＋ 圆周点**（就 2 个点）。
    /// 于是存档 / 变换 / 剪贴板 / 撤销 / "改几何"那条动作全部白拿——这也是规格里
    /// 选这个存法的理由（见 计划-图形工具.md 9.2）。
    /// 半径 = 这两点的距离；圆心 = 第一个点。
    /// </summary>
    Circle = 6,

    /// <summary>
    /// 三角形（2026-09-19 第二批第②步）。
    ///
    /// `Points` = **三个顶点**，顺序固定为 **上中 / 下左 / 下右**（见 计划-图形工具.md 9.4）：
    /// 画出来是"底边水平、左右对称"的形状，之后拖顶点精调成任意三角形。
    /// 三个点都是**真的顶点**（不是"外框的两个对角点"那种参数），所以紧框、轮廓折线、
    /// 顶点手柄三处直接读它们，不需要再做任何换算。
    /// </summary>
    Triangle = 7,

    /// <summary>
    /// 平行四边形（2026-09-19 第二批第②步）。
    ///
    /// `Points` = **三个顶点**（底左 / 底右 / 顶左），**第四个 = `第2 + 第3 − 第1`**
    /// ——自动推导、**不存**（用户定，见 计划-图形工具.md 9.5）。
    /// 于是"永远不可能是歪的四边形"这件事是**存法本身**保证的，不是拖动时去校正的；
    /// 代价是第四个角没有手柄（它是算出来的，拖它没有意义）。
    /// </summary>
    Parallelogram = 8,

    /// <summary>
    /// 坐标系（2026-09-19 第三批）。
    ///
    /// `Points` = **三个定义元素**：
    ///   · `[0]` **外框的一角**
    ///   · `[1]` **外框的对角**
    ///   · `[2]` **原点 O**
    ///
    /// **为什么外框要占两个点、不能"以原点为中心推"**：推的话拖原点就会把整个框带着走，
    /// 而老师最常用的动作恰恰是"框画完之后把原点挪到左下角，只留第一象限"。
    /// 范围和原点两样东西互相独立，所以各占一个定义元素。
    ///
    /// 画出来：x 轴过 O 水平贯穿外框、y 轴过 O 竖直贯穿外框，正方向端各一个箭头。
    /// **没有刻度**（用户 2026-09-19 定："数轴和坐标系上面的刻度太多了……不需要刻度"）
    /// ——早先那一版有刻度、还多一个"单位长度点"，刻度撤掉之后那个点就没有任何
    /// 可见作用了（拖了看不出变化），于是**一起撤掉**：画都不画的东西不该还占个手柄。
    /// 仍然保留的是那个**可选网格**（默认关，见 <see cref="Stroke.Grid"/>），
    /// 它的间距**现算**（外框短边 ÷ 4，见 <see cref="AxisGridStepLocal"/>），不再存。
    ///
    /// **不给旋转柄**（把坐标系转歪了不是老师要的东西，见 RotateHandleVisible）。
    /// </summary>
    Coordinate = 9,

    /// <summary>
    /// 数轴（2026-09-19 第三批）。
    ///
    /// `Points` = **两个定义元素**：`[0]` 左端、`[1]` 右端，而且**两点 y 恒相等**
    /// （拖端点只改 x，见 <see cref="SetAxisBox"/>）——数轴歪了就不是数轴了。
    ///
    /// 画出来：一条水平线 ＋ 右端一个箭头。**没有刻度**（用户 2026-09-19 定）。
    /// 早先那一版是四个点（左右端 + 零点 + 单位长度点）＋ 等距刻度，刻度撤掉之后
    /// "零点"和"单位长度点"就都没有可见作用了，于是缩回两个点。
    ///
    /// 于是它和"箭头工具"的差别只剩一条，但正是要害的那一条：**永远水平**。
    /// 不等式的解集、区间这些场景里，老师要的就是"一条不会画歪的轴"。
    /// </summary>
    NumberLine = 10,

    /// <summary>
    /// 抛物线（2026-09-20 第四批）。
    ///
    /// `Points` = **两个定义元素**：
    ///   · `[0]` **顶点 V**（"开口那个尖"，也是位置的锚点）；
    ///   · `[1]` **开口控制点 C**（它落在曲线的一个端点上，一次定下"半宽"和"深度"）。
    ///
    /// **朝上 / 朝下 / 朝左 / 朝右**由 <see cref="Stroke.CurveAxis"/> 决定，不是四个种类
    /// （理由见 <see cref="CurveAxis"/> 的注释）。两种开口共用的东西：
    ///   · 定义元素永远只有这两个点，`C` 一律被夹进"它该在的那个象限"（见 SetCurveBox），
    ///     所以几何里可以直接用带符号的差值，不需要到处判方向；
    ///   · 紧框 = "顶点 ↔ 控制点"那个矩形（开口朝上时它正好是曲线的最小外接）。
    /// </summary>
    Parabola = 11,

    /// <summary>
    /// 双曲线（2026-09-20 第四批）。
    ///
    /// `Points` = **两个定义元素**，和椭圆**完全同构**：
    ///   · `[0]` **中心 O**；
    ///   · `[1]` **外角点 E**，`a = |E.x − O.x|`、`b = |E.y − O.y|`（都夹成非负，见 SetCurveBox）。
    ///
    /// 实轴沿 x / 沿 y 由 <see cref="Stroke.CurveAxis"/> 决定：
    ///   · <see cref="CurveAxis.TransverseX"/>：`x²/a² − y²/b² = 1`，两支左右张开，顶点在 `(±a, 0)`；
    ///   · <see cref="CurveAxis.TransverseY"/>：`y²/a² − x²/b² = 1`，两支上下张开，顶点在 `(0, ±a)`。
    ///
    /// **两个朝向里 `a` / `b` 的角色会互换**（一个永远是"实半轴"、另一个是"虚半轴"）：
    ///   · 几何只认"**x 方向的半宽 = a**、**y 方向的半高 = b**"这一套坐标事实，不认虚实；
    ///   · "谁叫实半轴"只在**读数文案**里体现（见 Engine 的 UpdateVertexReadout）——
    ///     几何与命名各归各的，是这里最容易出错的地方（自检里专门钉了这条）。
    ///
    /// 画出来的两支**有截断**：`|t| ≤ asinh 2`，即每支画到 `|y| = 2b`（横向）或
    /// `|x| = 2b`（纵向）为止——双曲线是无限延伸的，不截断就没法存包围盒、也没法估脏区。
    /// **渐近线这一批不画**（要"一条几何两种线型"，属渲染地基，见 计划-图形工具.md 11.4）。
    /// </summary>
    Hyperbola = 12,

    /// <summary>
    /// 正弦 / 余弦的**一个周期**（2026-09-20 第四批，两个种类共用一套画法）。
    ///
    /// `Points` = **三个定义元素**：
    ///   · `[0]` **起点 P0**（余弦是峰顶、正弦是零点——也就是老师画的那条竖线所在处）；
    ///   · `[1]` **周期末端 P1**（一个周期之后回到同一条水平线上，所以 `P1.y == P0.y`）；
    ///   · `[2]` **极值点 P2**：正弦取**峰**、余弦取**谷**。
    ///
    /// 三个点各管一个量，所以三个手柄的语义是单值的：
    ///   · 拖 P0 = 整条平移；拖 P1 = **只改周期**；拖 P2 = **只改振幅**。
    ///
    /// 为什么不像椭圆那样"中心 ＋ 外角点"（2026-09-20 用户改的口径）：
    /// 老师是**从 y 轴起笔**画这条曲线的，中心法会让曲线先往左伸出一截、
    /// 起笔的位置也不是曲线的起点，画出来"不是他按下的那个样子"。
    /// </summary>
    Sine = 13,

    /// <summary>余弦：同 <see cref="Sine"/>，只是起点在峰顶（`f(0) = 1`，见那里的注释）。</summary>
    Cosine = 14,

    /// <summary>
    /// **圆柱**（2026-09-20 第五批）：两个控制点 = **外接矩形的两个角**（拖到哪就是哪）。
    ///
    /// 和椭圆一样是"外框定形"的一族，但它的几何是**三个图元**：
    ///   ① 顶面椭圆（整圈，实线）；② 底面椭圆的下半圈（实线）；③ 两条母线（实线）。
    /// 底面被挡住的上半圈走**虚线**——那就是辅助几何槽（`Geometry2`）的第二个用户
    ///（第一个是双曲线的渐近线）：一个对象两种线，见 计划-图形工具.md §11.4。
    /// </summary>
    Cylinder = 15,

    /// <summary>圆锥：同 <see cref="Cylinder"/> 的一族（外接矩形定形），只是没有顶面、顶点在上边中点。</summary>
    Cone = 16,

    /// <summary>
    /// **长方体**（2026-09-20 第五批）：三个控制点 = 正面矩形两角 ＋ 背面右下角
    ///（见 <see cref="SetCuboidDepth"/>——存这个角而不是"指针原样"，是为了让
    /// **控制点外接正好就是画出来的范围**，包围盒不用另算一份）。
    ///
    /// 两笔：正面矩形 → 深度。背面被挡住的三条棱是虚线（辅助几何槽）。
    /// ⚠ 面板入口 2026-09-20 第十二批撤掉了（画法与存档都留着，见 `Tool.Cuboid`）。
    /// </summary>
    Cuboid = 17,

    /// <summary>四面体：四个控制点 = 底面三角形三点 ＋ 顶点；六条棱全实线（照 InkClass）。
    /// ⚠ 面板入口 2026-09-20 第十二批撤掉了（想要它就画一个**三棱锥**）。</summary>
    Tetrahedron = 18,

    /// <summary>
    /// **棱柱**（2026-09-20）：三个控制点 = 底面外接框两角 ＋ **顶面中心**
    ///（侧棱向量 = 顶心 − 底心）。3~6 边形由 <see cref="Stroke.PrismSides"/> 这一档决定。
    /// 被挡住的底边与侧棱走辅助几何槽（细虚线，判据见 `Stroke.PrismFamilyEdges`）。
    /// </summary>
    Prism = 19,

    /// <summary>
    /// **棱锥**：控制点和 <see cref="Prism"/> **一模一样**（底面外接框两角 ＋ 顶点），
    /// 唯一的差别是"顶上那 n 个点**全都落在顶点上**"——顶面退化成一个点，于是没有顶面那一圈。
    /// 被挡住的棱走辅助几何槽（判据和棱柱共用，见 `Stroke.PrismFamilyEdges`）。
    /// </summary>
    Pyramid = 20,

    /// <summary>
    /// **棱台**：控制点也和 <see cref="Prism"/> 一样（底面外接框两角 ＋ 上底中心），
    /// 差别是顶面**从顶点按固定比例缩回来**（不是平移过去，所以侧棱会"收"）。
    /// 上下两面都是 n 边形，所以底面一圈、顶面一圈、n 条侧棱都在。
    /// </summary>
    Frustum = 21,

    /// <summary>
    /// **圆台**（2026-09-20 第十三批，用户："再加一个圆台"）：圆锥**截掉上面一截**剩下的部分——
    /// 上下两个面都是圆、下面的比上面大。
    ///
    /// 和 <see cref="Cylinder"/> / <see cref="Cone"/> 同一族（都是一次拖出**外接矩形**、
    /// 一笔画完），所以它**没有多笔**、也没进 `Engine.PlanOf` 那张表。
    /// 上下底各是一个椭圆，画法与实虚也**和圆柱 / 圆锥共用一份**（见 <see cref="SolidPieces"/>）：
    /// 上底整圈实线 ＋ 下底下半圈实线 ＋ 两条母线（收进去），**被挡住的是下底的上半圈**（虚线）——
    /// 这些和圆柱完全一样，唯一的差别是"上底小一圈"。
    ///
    /// ⚠ 值**只能追加在末尾**：`Kind` 是要写进存档的（见 InkSerializer），
    /// 插在中间会把老文件里所有后面的种类改成别的意思。
    /// </summary>
    ConeFrustum = 22,

    /// <summary>
    /// **球**（2026-09-20 第十四批，用户："再加加入球"）：和圆柱 / 圆锥 / 圆台同一族——
    /// **拖一个外接矩形、一笔画完**。
    ///
    /// 三件事和别的立体**不一样**，都是"球"这个东西本身的约束：
    ///   · 投影永远是**圆**（不像别的立体分 rx / ry）→ 取矩形里能内切的**正圆**
    ///     （半径 = min(半宽, 半高)，见 <see cref="SphereLocal"/>）：拖成方的就是标准球，
    ///     拖长了也不会画出一个"扁球"；
    ///   · 只画轮廓会**和「圆」一模一样**，所以必须把**赤道**那个椭圆画出来
    ///     （见 <see cref="SphereEquatorLocal"/>），它才是"这是个球"的唯一线索；
    ///   · 赤道的**近侧（下半圈）是实线、远侧（上半圈）是虚线**——近的那半圈露在球面上、
    ///     远的那半圈被球自己挡住。和圆柱底圈那条判据**同一个口径**（"屏幕上方 = 远处"）。
    ///
    /// 值同样只能追加在末尾。
    /// </summary>
    Sphere = 23,

    /// <summary>
    /// **正切 y = tan x**（2026-09-20 第十五批，用户："可以画正切"）：画**一支**。
    ///
    /// 一支的定义域是 `(−π/2, π/2)`、两端冲到无穷，所以画法单独定了一套
    ///（见 <see cref="SetTangentBox"/>）：**按下 = 原点**（这一支的中心，
    /// 也就是图象与 x 轴相交的那个点），**拖出 = 以原点为中心的框**：
    ///   · **横向 = 半支长** → 两条**渐近线**就落在框的左右边上；
    ///   · **纵向 = 可视半高** → 曲线冲到框的上下边就**截断**（课本就这么画）。
    ///
    /// ⚠ 和正弦 / 余弦**不一样的地方**：正切的横竖**共用一个单位**
    ///（π/2 个单位 = 半支长，见 <see cref="TangentUnitLocal"/>），所以它**不会被拖变形**——
    /// "纵向拖多高"只是决定"看得见多少"（显示范围是可以挑的），
    /// 而正弦 / 余弦的"周期 : 振幅"是数学事实，才需要一个固定比例。
    ///
    /// 值同样只能追加在末尾。
    /// </summary>
    Tangent = 24,

    /// <summary>
    /// **波浪线**（2026-09-20 第十六批，用户："还有一个另外的很多周期的波浪的弦函数线"）：
    /// 就是**多周期的正弦波**——和 <see cref="Sine"/> 是同一条曲线，差别只在**这一拖管什么**：
    ///   · 正弦：框宽 = **一个周期**（拖多宽就是一个周期，用来讲"一个周期的图象"）；
    ///   · 波浪线：框宽 = **要画多长**（拖多长画多长，可以好几个周期），
    ///     周期由**振幅**定（`WavePeriodPerAmplitude`，见 <see cref="Stroke.WavePeriodLocal"/>）。
    ///
    /// **为什么要分成两种**（用户 2026-09-20 定的）：课堂上这两件事是分开讲的——
    /// "正弦函数的图象（一个周期）"和"周期性的波浪线（很多个周期）"，
    /// 各占面板一格比"一格两种含义、靠拖多长暗中决定"更好懂（也更好选）。
    ///
    /// 值同样只能追加在末尾。
    /// </summary>
    Wave = 25,

    /// <summary>
    /// **椭圆（带焦点）**（2026-09-22，见 <see cref="Tool.ConicEllipse"/>）。
    ///
    /// `Points` = **两个定义元素**，和 <see cref="Ellipse"/> **完全一样**：
    ///   · `[0]` **中心 O**；`[1]` **外角点 E**，`a = |E.x − O.x|`、`b = |E.y − O.y|`。
    ///
    /// 多出来的两件事都**不是"点"**，所以没有占 `Points`（占的话，紧框 / 存档 /
    /// 变换那一套全得为它多写一份"这条点不算定义元素"的规矩）：
    ///   · <see cref="Stroke.FocusTriangle"/> = 画不画焦点三角形（对象自己的档，进存档）；
    ///   · <see cref="Stroke.FocusPointU"/> = 焦点三角形第三个顶点 P **在椭圆上的参数角**
    ///     （P 由它现算，见 <see cref="Stroke.ConicEllipsePointLocal"/>）——
    ///     存角不存点，于是"拖半轴把它拉扁"之后 P **自动还在椭圆上**（不必再校正一次）。
    ///
    /// 两个焦点由长短轴现算（`c = √(a² − b²)`，见 <see cref="Stroke.ConicEllipseFociLocal"/>），
    /// 不存：它们是椭圆的**函数**，存下来就会跟半轴对不上（老毛病："同一个量存两份"）。
    ///
    /// 值同样只能追加在末尾。
    /// </summary>
    ConicEllipse = 26,
}

/// <summary>
/// **曲线的朝向**（2026-09-20 第四批：抛物线的四种开口 / 双曲线的两个方向）。
///
/// 为什么是一个**属性**，不是四个 `Kind`、也不是四个工具：
///   · 同一族的四种开口共用全部东西——定义元素、手柄、命中、存档、几何构建，
///     差别只有"谁是自变量"这一件事，而这正好是属性的形状；
///   · 它**必须跟着对象走**（进存档）：存到文件、复制粘贴、发给别人之后朝向都不该变
///     （和 <see cref="StrokeDash"/>、<see cref="Stroke.Grid"/> 是同一条理由：
///     对象要自包含，全局开关换个机器就全变样了）；
///   · 四个 `Kind` 的话，加一种开口就要重走一遍"存档升版 ＋ 手柄名单 ＋ 图标"，
///     而它们其实是同一件事的四档。
///
/// 取值就是存档里的字节（见 InkSerializer v11）。读端**只认下面这几个值**，
/// 读到别的值一律退到 `OpenUp`——一个坏字节不该让整条曲线画成说不清的东西。
/// </summary>
public enum CurveAxis
{
    /// <summary>抛物线：**开口向上**（`y = ax²`，a &gt; 0 那一支，屏幕上是个"∪"）。默认值。</summary>
    OpenUp = 0,
    /// <summary>抛物线：**开口向下**（"∩"）。</summary>
    OpenDown = 1,
    /// <summary>抛物线：**开口向右**（课本里的 `y² = 2px`，p &gt; 0 就是它）。</summary>
    OpenRight = 2,
    /// <summary>抛物线：**开口向左**（`y² = −2px`）。</summary>
    OpenLeft = 3,

    /// <summary>双曲线：**实轴沿 x**（`x²/a² − y²/b² = 1`），两支左右张开。默认值。</summary>
    TransverseX = 4,
    /// <summary>双曲线：**实轴沿 y**（`y²/a² − x²/b² = 1`），两支上下张开。</summary>
    TransverseY = 5,
}

/// <summary>
/// 线型（2026-09-19 加，用户定：**做成属性，不做成工具**）。
///
/// **为什么不做成工具**（不像 InkClass 那样"虚线直线""虚线圆"各占一个图标）：
/// InkClass 里一切都是笔迹、**画完就改不了**，线型必须在落笔前选好，所以只能一种线型一个图标；
/// 我们的图形是**对象**，线型事后能改，一个工具就够——上带仍然 7 段，不多占格子。
///
/// **范围**：一开始只是"图形"（直线/箭头/矩形/圆/椭圆/三角形/平行四边形/坐标系），
/// 2026-09-19 之后**自由笔迹也有**：用户要求"笔的色带条上要有虚实线切换"，
/// 并且报"选中一条笔迹再点虚线毫无反应"（那正是引擎里 `Freehand` 被跳过造成的）。
/// 于是"画之前选"（色带条上的按钮 → <c>Engine.PenDash</c>）和"画之后改"
/// （操作条面板 → <c>SetSelectionDash</c>）两条路对笔迹都通了。
///
/// 取值就是存档里的字节（见 InkSerializer v8），**不要改已有的 0/1/2 编号**。
/// </summary>
public enum StrokeDash
{
    /// <summary>实线。也是 ≤ v7 老存档的取值（老文件读进来一律实线）。</summary>
    Solid = 0,
    /// <summary>虚线（辅助线、渐近线、延长线最常用）。</summary>
    Dashed = 1,
    /// <summary>点线（比虚线更轻，画"参考用"的线）。</summary>
    Dotted = 2,
}

/// <summary>
/// **压力 → 笔宽** 的映射（2026-09-20 加）。**全引擎唯一一份**：
/// 渲染（逐点半径）、湿墨轨迹（逐点半径）、紧框 / 命中 / 橡皮（按最粗处）都问它。
///
/// 口径**对齐 WPF / Windows Ink**，而且是**从实测图上反推**出来的——
/// 用户在同一个程序的同一支笔下画了三条线（鼠标 / 轻轻描 / 重重压），我们逐像素量的：
///
/// ```
/// 宽度 = 档位宽度 × clamp(Max × 压力, Min, Max)      // 默认 Max = 2.0、Min = 0.10、线性
/// ```
///
/// | 线 | 实测宽度 | 相对鼠标 | 反推压力 |
/// |---|---|---|---|
/// | 鼠标（无压感） | 18.0 px | 1.00× | **0.50** |
/// | 笔·轻轻描 | 6.0 px | **0.33×** | ≈ 0.17 |
/// | 笔·重重压 | 31.0 px | **1.72×** | ≈ 0.86 |
/// | （满压外推） | ≈36 px | **≈2.00×** | 1.00 |
///
/// 三条推理依据：
///   ① **鼠标那条落在压力 0.5 处**：WPF 官方文档对 `StylusPoint.PressureFactor` 写得很死
///      ——"**The default value is 0.5**"，无压感设备（鼠标）就用这个中值。
///      所以"鼠标画的那条线"是**一半压力**的参照物，**不是满压**；
///   ② 于是 **满压 ≈ 2 × 鼠标宽度**：重压那条实测就是鼠标的 1.72 倍（对应压力 0.86），
///      反推 p = 1 时是 2.0 倍——**上限必须大于 1**，否则"重压比鼠标粗"这件事根本画不出来；
///   ③ **没有 50% 地板**：上一张图里重写的两笔从 30 px 一路**收成尖**，
///      0.5 的地板（= 半个标称宽）会把它截在 18 px 上，收不出尖。
///
/// **被排除的口径**：一开始抄的是老的 Tablet PC（`Microsoft.Ink`）文档那句
/// "最大压力 = 150%、最小 = 50%"。那是**另一套栈**：上限 1.5 倍 < 实测的 1.72 倍，
/// 而且 p = 0.5 处是"最粗"的，解释不了"鼠标那条比重压细"。
/// 抄文档没错——**错在抄错了是哪一套栈**：`System.Windows.Ink`（WPF）
/// 不是 `Microsoft.Ink`（Tablet PC / `InkCollector` 那一套）。
///
/// 三个可调项（都从命令行来，见 Engine 的 `--pressrange` / `--nopressure`）：
///   · <see cref="Min"/> / <see cref="Max"/>：动态范围（两个都取 1 = 压感不改变粗细）；
///   · <see cref="Gamma"/>：曲线（1 = 线性；&gt;1 把轻压区间压扁 = 更"压得住"）。
///
/// 处理顺序照调研的结论：**阈值 → 曲线 → 平滑**。平滑不在这一层做——它是"按点序列"的事，
/// 放在渲染取点那一步（<c>Overlay.DrawPressureInk</c>），这样**存档里留的是原始压力**，
/// 重开之后再画也是同一个样子。
/// </summary>
internal static class PressureWidth
{
    /// <summary>
    /// **地板**（档位的 10%）。这一条是**我们加的**、不是学谁：真到 0 压力时 D2D 会
    /// 渲成一片几乎透明的灰（等于画不出墨），留 10% 既看不出区别、又保证"轻描也有墨"。
    /// </summary>
    public static float Min = 0.10f;
    /// <summary>
    /// **满压时的宽度倍数 = 2.0**：从那张三线图反推出来的（重压实测 1.72 倍 ↔ 压力 0.86）。
    ///
    /// 于是 **p = 0.5 时正好 = 1.0 倍 = 档位宽度**（也就是"无压感那支笔"的粗细）——
    /// 这也是**档位语义没变**的原因：档位仍然是"正常用力写出来的那条线有多粗"。
    /// </summary>
    public static float Max = 2.0f;
    /// <summary>压力曲线指数（1 = 线性，和 WPF 一致；&gt;1 把轻压区间压扁）。</summary>
    public static float Gamma = 1f;
    /// <summary>压感要不要参与渲染（`--nopressure` 关掉，用来做"有/无"对照）。</summary>
    public static bool Enabled = true;

    /// <summary>
    /// 压力（0..1）→ 宽度倍数：**正比于压力**（WPF 的口径）= `Max × p`，再夹到 [Min, Max]。
    ///
    /// 两个特征点：**p = 0.5（无压感设备的默认值）→ 1.0 倍 = 档位宽度**；
    /// **p = 1 → 2 倍**（"比鼠标那条粗一倍"，就是用户在 WPF 里看到的观感）。
    /// </summary>
    public static float Factor(float p)
    {
        float t = Math.Clamp(p, 0f, 1f);
        if (Gamma != 1f) t = MathF.Pow(t, Gamma);
        return Math.Clamp(Max * t, Min, Max);
    }

    /// <summary>某一点的**半宽**（画布 / 局部像素，和标称宽度同一个尺度）。</summary>
    public static float HalfWidth(float nominalWidth, float p)
        => nominalWidth * 0.5f * Factor(p);
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

    /// <summary>
    /// 线型（见 <see cref="StrokeDash"/>）。默认实线。
    ///
    /// **刻意不进 <see cref="InvalidateMetrics"/> 的重算范围**：线型只改"怎么描这条边"，
    /// 不改形状本身，所以包围盒（<see cref="PaddedBounds"/>）一分都不变——
    /// 改线型只需要"旧样子标脏 + 新样子标脏"，几何和缓存都不用重建。
    /// 同理，**命中测试也照样按实线算**（见 <see cref="HitTest"/> 里的注释）。
    /// </summary>
    public StrokeDash Dash = StrokeDash.Solid;

    /// <summary>
    /// **坐标系要不要网格**（只有 <see cref="StrokeKind.Coordinate"/> 用得上，别的种类恒 false）。
    ///
    /// 为什么做成"跟着对象走"而不是"一个全局开关"：对象是**自包含**的——
    /// 存到文件、复制粘贴、发给别人，看到的都得是同一个样子；全局开关的话，
    /// 换台机器（或者改一次设置）整个板书上的坐标系就全变样了。
    /// 画的时候取当时的默认值（见 Engine 的坐标系网格开关），之后选中还能单独改。
    ///
    /// 注意它**改的是几何**（多了那些网格线），所以改它必须让几何缓存失效
    /// （见 SetStrokePropAction：Grid 走 InvalidateMetrics，线型不走）。
    /// </summary>
    public bool Grid;

    /// <summary>
    /// 网格线的**粗细系数**（乘在对象自己的 <see cref="Width"/> 上）。
    ///
    /// 用户 2026-09-24："把那个网格的线**变细变淡**一些"——网格是**底子**，
    /// 不该和两条轴抢眼：一条黑 3 像素的格线铺满整屏，比题目本身还显眼。
    ///
    /// 为什么非要单独一段几何：**一个几何只能有一种描边**（粗细 + 颜色 + 线型是一起给的），
    /// 所以"轴线一种、网格另一种"只能拆成两段——落点见 <see cref="BuildAuxGeometry"/>
    /// 与 `Overlay.DrawStroke` 的辅助几何那一段（同一个槽本来在装渐近线）。
    /// 熔墨（<see cref="MeltToInkParts"/>）也要按这两个系数还回去，不然"擦一下网格变粗"。
    /// </summary>
    internal const float AxisGridWidthFactor = 0.5f;

    /// <summary>网格线的**浓淡系数**（乘在 alpha 上）。理由同 <see cref="AxisGridWidthFactor"/>。</summary>
    internal const float AxisGridAlpha = 0.45f;

    /// <summary>把颜色按比例调淡（只动 alpha）。网格线用它。</summary>
    internal static Color4 Fade(Color4 c, float f) => new Color4(c.R, c.G, c.B, c.A * f);

    /// <summary>
    /// **曲线的朝向**（只有 <see cref="StrokeKind.Parabola"/> / <see cref="StrokeKind.Hyperbola"/>
    /// 用得上，别的种类恒为 <see cref="CurveAxis.OpenUp"/>）。
    ///
    /// 和 <see cref="Grid"/> 完全是同一类东西、也是同一条规矩：
    ///   · 它是**对象自己的样子**（不是全局开关）——存到文件、复制粘贴、发给别人都不该变；
    ///   · 它**改的是几何**（换一种开口，曲线走的完全是另一条路），所以改它必须让几何缓存
    ///     失效（走 <see cref="InvalidateMetrics"/>，和 Grid 一样）；
    ///   · 进存档（v11），老文件读进来是 0（= 开口向上 / 实轴沿 x），正是它们最可能的样子。
    /// </summary>
    public CurveAxis CurveAxis = CurveAxis.OpenUp;

    public readonly List<InkPoint> Points = new();

    /// <summary>
    /// 这一笔**有没有真实压感**（设备报压力、且点处于接触状态）。
    ///
    /// 为什么是"整笔的属性"而不是"看某个点的压力值"：`pressure == 0` 有两种含义
    /// （设备压根不报 / 真的写到 0），所以这件事只能由采集层按 `penMask` 判定
    /// （见 <see cref="PenSample.HasPressure"/> 的注释）。
    ///
    /// 它决定渲染走哪条路：**有压感 + 实线 → D2D 原生变宽墨迹**（见
    /// <c>Overlay.DrawPressureInk</c>）；否则走原来那条等宽描边（和 2026-09-14 之后一样）。
    ///
    /// **进存档（v10）**：不进的话"存盘再打开，压感笔迹就变等宽了"——那是这个功能最刺眼的
    /// 一种 bug（当场看是好的，重开就没了）。老文件读进来是 false（老文件本来也没存压感）。
    /// </summary>
    public bool HasPressure;

    /// <summary>
    /// **正在写的那一笔：按原始折线画**（曲线只用在落笔之后，2026-09-28 用户实测后定的）。
    ///
    /// 为什么要有这个开关：过点曲线（centripetal Catmull-Rom）每来一个新采样点，
    /// 都要**回头重算最后一段**——切线要用到那个新点。于是笔尖后面 ~2 段（几十像素）
    /// 的墨每帧都在微微挪动，屏幕上看就是"一直在闪"（实测：折线在笔尖后 24px 以外
    /// 完全静止，曲线一直动到 46px）。**这是样条画法的固有性质，不是 bug**；
    /// 想要"画的时候一个像素都不许自己动"，就只能让活笔走折线。
    ///
    /// 代价：落笔那一刻形状会换成曲线。密采样（Windows Ink 的笔）下差别是亚像素级、
    /// 看不出来；鼠标/快画这种稀采样才看得出来——而那正是曲线真正有用的地方。
    /// </summary>
    internal bool RawWhileLive;


    /// <summary>
    /// **锁定**（2026-09-16 加，用户定的语义是"能选中、但拖不动"）：
    ///   · 照样能被点选 / 框选 / 套索 / 全选选中，框和手柄照画；
    ///   · 但拖动 / 缩放 / 旋转 / 翻转**跳过它**（选区里锁定的留在原地）；
    ///   · 两种橡皮、`Delete` 也跳过（橡皮和删除改的是内容，不是位置）；
    ///   · 改颜色 / 改粗细 / 复制 / 导出**不受影响**（那是样式与内容带走）。
    /// 见 调研-选中框-反馈-导出-层级-属性.md 第四节。
    /// </summary>
    public bool Locked;

    public ID2D1Geometry Geometry;

    // [删除 2026-10-05] `RenderTail`（预测渲染尾）、`PredictedTip`（预测笔尖）、
    // `_tailStamp/_builtTailStamp`（它们的几何缓存章）与两个 Set 方法：随老预测系统移除。
    // 原文见 `.revert/2026-10-05-渲染减法/`。

    /// <summary>
    /// 建几何时的"曲线化版本"。`--smoothshow` 出图 / 开关一拨时，`Revision` 没变，
    /// 缓存会把旧折线还回来，所以必须进缓存键。
    /// </summary>
    private int _builtSmoothVer = -1;
    /// <summary>建几何时这一笔是不是"正在写"。和曲线化版本同理，必须进缓存键：
    /// 落笔那一刻 `RawWhileLive` 由真变假，几何要跟着从折线换成曲线。</summary>
    private bool _builtRawLive;

    /// <summary>
    /// 建几何时的"墨迹模型版本"（<see cref="InkModel"/>）：模式/参数一拨，
    /// `Revision` 没变，缓存会把旧几何还回来。
    /// </summary>
    private int _builtInkModelVer = -1;

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
                // 线型也算"样式"，跟着继承（2026-09-19：自由笔迹有线型之后，
                // 虚线笔迹被拆开的两截必须还是虚线，否则"拆完样子变了"）。
                Dash = Dash,
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
    /// 熔成墨的时候的**一条折线**：点列 ＋ "它是不是辅助线"。
    ///
    /// 辅助线（双曲线的渐近线、立体图形被挡住的那半圈 / 那几条棱）在屏幕上恒定是
    /// **细虚线**（见 <see cref="BuildAuxGeometry"/> 与 Overlay 里那条 0.6 倍粗细），
    /// 所以熔成墨也得带着这个身份——不然就是用户 2026-09-20 反馈的
    /// "擦一下**虚线变实线**"。
    /// </summary>
    public sealed class InkPiece
    {
        public readonly List<Vector2> Pts;
        public readonly bool Aux;

        /// <summary>
        /// **坐标系网格线**（用户 2026-09-24："网格的线变细变淡"）。
        /// 它和辅助线一样"不参与命中"（见 <see cref="ShapeOutline"/>：轮廓里只列两条轴线），
        /// 但画法不同——网格是**细一半、淡一半的实线**（辅助线是细虚线），
        /// 系数见 <see cref="AxisGridWidthFactor"/> / <see cref="AxisGridAlpha"/>。
        /// </summary>
        public readonly bool Grid;

        public InkPiece(List<Vector2> pts, bool aux) : this(pts, aux, false) { }

        public InkPiece(List<Vector2> pts, bool aux, bool grid)
        {
            Pts = pts; Aux = aux; Grid = grid;
        }
    }

    /// <summary>
    /// 把**图形**熔成墨（**一组**笔迹，不是一个）：每一笔取它自己的一段折线，
    /// 坐标换算到画布、变换归一。
    ///
    /// 为什么：用户 2026-09-15 定"橡皮擦中图形，断开了也要单独算"——图形是参数化对象
    /// （两个端点 / 三个控制点），不先变成点列就没法"擦掉中间、剩下两截"。
    /// 熔完就走和墨迹一模一样的那条路（区间擦除那套）。
    ///
    /// **为什么必须是一组**（2026-09-20 用户反馈"擦一下多出来线 / 虚线变实线"）：
    /// 原来是把轮廓（含抬笔标记）**忽略标记压成一条**再熔，于是
    ///   · 段与段之间被连起来 → 双曲线两支之间、长方体各条棱之间凭空多出线；
    ///   · 辅助线（渐近线、被挡的半圈）根本不在轮廓里 → 虚线消失，或（圆柱底面）
    ///     以实线画回来。
    /// 现在按"画出来的每一笔"分开熔（见 <see cref="InkPieces"/>），外形与擦之前逐笔一致。
    ///
    /// 代价（明确接受）：熔完它**不再是图形**——没有顶点手柄、不能改形状参数。
    /// 这一步靠撤销回退（原图形整个进撤销栈，见 EraseIntervalsAction.Item.UndoOriginal）。
    /// </summary>
    public List<Stroke> MeltToInkParts()
    {
        var list = new List<Stroke>();
        foreach (var piece in InkPieces())
        {
            if (piece.Pts.Count < 2) continue;
            var m = new Stroke
            {
                Tool = Tool, Kind = StrokeKind.Freehand,
                // 网格线熔成墨之后也**得是细的淡的**（见 AxisGridWidthFactor / AxisGridAlpha）：
                // 不然"擦一下坐标系，网格变粗变黑"——和辅助线那条是同一类毛病。
                Color = piece.Grid ? Fade(Color, AxisGridAlpha) : Color,
                // 辅助线在屏幕上就是 0.6 倍粗细的细虚线（Overlay.DrawStroke 那个 Max(1, Width*0.6)）。
                Width = piece.Aux ? MathF.Max(1f, Width * 0.6f)
                      : piece.Grid ? MathF.Max(1f, Width * AxisGridWidthFactor)
                      : Width,
                // 线型跟过来：虚线图形熔成笔迹之后还得是虚线（见 SplitIntoRuns 那条同一个理由）。
                Dash = piece.Aux ? StrokeDash.Dashed : Dash,
            };
            foreach (var p in piece.Pts)
            {
                var q = Transform.IsIdentity ? p : Vector2.Transform(p, Transform);
                m.AddPoint(q.X, q.Y, 1f, 0);
            }
            list.Add(m);
        }
        return list;
    }

    /// <summary>
    /// 这个对象**画在屏幕上的每一笔**（局部坐标，逐段）。
    ///
    /// 它和 <see cref="ShapeOutline"/> 的分工**不能互相顶替**：
    ///   · `ShapeOutline` 只服务"碰到没有"（命中、套索、像素橡皮的粗筛）。为了命中方便，
    ///     它**故意**画得比屏幕上多、少——圆柱底面列整圈（实线半圈与虚线半圈都在里面），
    ///     坐标系只列两条轴线（网格、箭头都不列）；
    ///   · 这一份是"擦之前屏幕上有几笔，熔完就得有几笔"，所以实 / 虚分开、箭头网格不能漏。
    ///     （图形工具计划 §25：漏一处就是"擦一下少几笔"。）
    /// </summary>
    public List<InkPiece> InkPieces()
    {
        var list = new List<InkPiece>();

        switch (Kind)
        {
            // ---- 圆柱 / 圆锥 / 圆台 / 球：可见的几笔 ＋ 被挡住的那半圈（细虚线）----
            case StrokeKind.Cylinder:
            case StrokeKind.Cone:
            case StrokeKind.ConeFrustum:
            case StrokeKind.Sphere:
                if (Points.Count < 2) break;
                list.AddRange(SolidPieces(hidden: false));
                list.AddRange(SolidPieces(hidden: true));
                break;

            // ---- 长方体：九条看得见的棱 ＋ 三条被挡住的棱（细虚线）----
            case StrokeKind.Cuboid:
                if (Points.Count < 3) break;
                list.AddRange(CuboidEdges(hidden: false));
                list.AddRange(CuboidEdges(hidden: true));
                break;

            // ---- 棱柱 / 棱锥 / 棱台：看得见的棱 ＋ 被挡住的（细虚线）——实虚由通用判据分
            //      （见 PrismFamilyEdges）。三兄弟共用这一支。
            case StrokeKind.Prism:
            case StrokeKind.Pyramid:
            case StrokeKind.Frustum:
                if (Points.Count < 2) break;
                list.AddRange(PrismFamilyEdges(hidden: false));
                list.AddRange(PrismFamilyEdges(hidden: true));
                break;

            // ---- 四面体：六条棱，全实线 ----
            case StrokeKind.Tetrahedron:
                if (Points.Count < 3) break;
                list.AddRange(TetraEdges());
                break;

            // ---- 坐标系 / 数轴：轴线 ＋ 箭头（＋ 网格）----
            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
                if (Points.Count < 2) break;
                list.AddRange(AxisPieces());
                break;

            // ---- 正切：一支（一条开曲线）＋ 两条竖直渐近线（细虚线）----
            //      实虚和双曲线同一个排法：曲线走主几何、渐近线走辅助几何。
            case StrokeKind.Tangent:
                if (Points.Count < 2) break;
                foreach (var part in SplitOutlineParts(ShapeOutline()))
                    list.Add(new InkPiece(part, false));
                for (int side = -1; side <= 1; side += 2)
                {
                    var (from, to) = TangentAsymptoteLocal(side);
                    list.Add(new InkPiece(new List<Vector2> { from, to }, true));
                }
                break;

            // ---- 双曲线：两支（中间有抬笔）＋ 两条渐近线（细虚线）----
            case StrokeKind.Hyperbola:
                if (Points.Count < 2) break;
                foreach (var part in SplitOutlineParts(ShapeOutline()))
                    list.Add(new InkPiece(part, false));
                if (ShowAsymptotes)
                {
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var (from, to) = HyperbolaAsymptoteLocal(side);
                        list.Add(new InkPiece(new List<Vector2> { from, to }, true));
                    }
                }
                break;

            // ---- 其余（直线 / 箭头 / 矩形 / 椭圆 / 圆 / 三角形 / 平行四边形 /
            //      抛物线 / 正弦 / 余弦 / 波浪线）：轮廓本来就是"画出来的那一条"，
            //      按抬笔分段即可 ----
            default:
                foreach (var part in SplitOutlineParts(ShapeOutline()))
                    list.Add(new InkPiece(part, false));
                break;
        }
        return list;
    }

    /// <summary>
    /// 把一份轮廓折线按**抬笔标记**（<see cref="OutlineBreak"/>）切成几段。
    /// 抬笔标记本身不进结果（它的坐标是 NaN，写进点列会把整条笔迹的几何变成 NaN——
    /// 那一笔画不出来、点不中，包围盒也会被污染）。
    /// </summary>
    public static List<List<Vector2>> SplitOutlineParts(List<Vector2> outline)
    {
        var parts = new List<List<Vector2>>();
        var cur = new List<Vector2>();
        foreach (var p in outline)
        {
            if (IsOutlineBreak(p))
            {
                if (cur.Count > 0) { parts.Add(cur); cur = new List<Vector2>(); }
                continue;
            }
            cur.Add(p);
        }
        if (cur.Count > 0) parts.Add(cur);
        return parts;
    }

    /// <summary>椭圆上一段弧的采样点列（`u0 → u1`）：**立体图形的实线几何、虚线半圈、
    /// 以及熔墨三处共用这一份点列**（渲染与熔出来的逐点相同）。</summary>
    private static List<Vector2> ArcPoints(Vector2 c, float rx, float ry, float u0, float u1)
    {
        int n = Math.Clamp((int)(MathF.Max(rx, ry) / 4f), 12, 48);
        var pts = new List<Vector2>(n + 1);
        for (int i = 0; i <= n; i++)
            pts.Add(EllipseArcPoint(c, rx, ry, u0 + (u1 - u0) * i / n));
        return pts;
    }

    /// <summary>把几段折线拼成一个 D2D 几何：**每段一条 figure**（互不相连）。
    /// 渲染与熔共用同一份点列，见 <see cref="InkPieces"/>。</summary>
    private static ID2D1PathGeometry FromPieces(ID2D1Factory1 factory, List<InkPiece> pieces)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        foreach (var pc in pieces)
        {
            if (pc.Pts.Count < 2) continue;
            sink.BeginFigure(pc.Pts[0], FigureBegin.Hollow);
            for (int i = 1; i < pc.Pts.Count; i++) sink.AddLine(pc.Pts[i]);
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
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

    /// <summary>
    /// 三角形 / 平行四边形的**画法**：由"拖出来的外框"（按下的点 + 当前指针，
    /// 两个对角点的**顺序随意**，内部先归一化）一次算出三个控制点。
    ///
    /// 每帧都要调（拖动预览），所以既不分配、也不走 <see cref="SetPoints"/>
    /// ——那个要造一个数组。规则就是规格 9.4 / 9.5 那两句：
    ///   · **三角形**：上中 ＋ 下左 ＋ 下右（底边水平、左右对称）；
    ///   · **平行四边形**：底边水平、上边右移 1/4 宽。右移取"底边两端各让出 1/4"
    ///     （而不是整条上边往右挪出框外），于是**外框正好是它的包围盒**，
    ///     松手那一瞬的紧框和用户拖出来的那个框重合，不会"松手就长大一截"。
    /// </summary>
    public void SetShapeBox(float x0, float y0, float x1, float y1)
    {
        // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到三个（控制点数固定）。
        while (Points.Count < 3) AddPoint(x0, y0, 1f, 0);

        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        if (Kind == StrokeKind.Triangle)
        {
            SetPoint(0, new Vector2((minX + maxX) * 0.5f, minY));   // 上中
            SetPoint(1, new Vector2(minX, maxY));                   // 下左
            SetPoint(2, new Vector2(maxX, maxY));                   // 下右
        }
        else
        {
            float shift = (maxX - minX) * 0.25f;                    // 上边右移 1/4 宽
            SetPoint(0, new Vector2(minX, maxY));                   // 底左
            SetPoint(1, new Vector2(maxX - shift, maxY));           // 底右
            SetPoint(2, new Vector2(minX + shift, minY));           // 顶左
        }
    }

    /// <summary>
    /// **抛物线的第一步**（用户 2026-09-20 改的口径："先设定开口，再用两点画出来"）：
    /// 点一下 = **顶点**，定下来就不再动。
    ///
    /// 朝向**不在这里定**——它由操作条那一格"开口方向"先选好（存 <see cref="CurveAxis"/> 的
    /// OpenUp / OpenDown / OpenRight / OpenLeft），和"大小"彻底分开。
    /// 这就是这一版要解决的事：原来"方向"和"大小"挤在同一个拖动里（靠 ±12° 死区赌你要开哪个口），
    /// 两个参数互相拽、都不好调。
    ///
    /// 起手先给一条"最小张口"的曲线（经过点放在 `u = 1` 那个点上，反解出来正好是下限），
    /// 免得松手前屏幕上什么都没有。
    /// </summary>
    public void SetParabolaVertex(float x, float y)
    {
        // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到两个（控制点数固定）。
        while (Points.Count < 2) AddPoint(x, y, 1f, 0);

        var v = new Vector2(x, y);
        const float p = ParabolaMinP;
        var (dir, perp) = ParabolaBasisLocal();
        SetPoint(0, v);
        SetPoint(1, v + perp * p + dir * (p * 0.5f));      // u = 1 处的曲线点（通径端点）
    }

    /// <summary>
    /// **抛物线的第二步**：把"**曲线要经过的那个点**"写进模型，`p` 由它反解
    /// （见 <see cref="ParabolaPThroughPoint"/>）。**朝向不动**——这正是"先定开口、再两点画"。
    /// </summary>
    public void SetParabolaThroughPoint(float x, float y)
    {
        if (Points.Count < 2) return;
        SetPoint(1, new Vector2(x, y));
    }

    /// <summary>
    /// 换朝向（操作条那格"开口方向"）：把"经过点"**跟着新朝向转一下**
    /// ——保持它在轴上的分量 `s`、横跨分量 `t` 不变，于是 **`p` 原样保住**。
    /// 用户看到的是"整条曲线转了个方向"，而不是"换一下朝向曲线就瘪了"。
    /// </summary>
    public void SetParabolaAxis(CurveAxis axis)
    {
        if (Kind != StrokeKind.Parabola || Points.Count < 2) return;
        var cur = EffectiveAxis;
        var next = NormalizeAxis(Kind, axis);
        CurveAxis = next;                  // 顺手把字段写干净（坏值不留在里面，存档会原样写出去）
        if (next == cur) return;           // 同一个方向就没什么要重组的

        var v = CurvePointLocal(0);
        var d = CurvePointLocal(1) - v;
        var (oldDir, oldPerp) = ParabolaBasis(cur);
        float s = Vector2.Dot(d, oldDir), t = Vector2.Dot(d, oldPerp);

        var (newDir, newPerp) = ParabolaBasis(next);
        SetPoint(1, v + newDir * s + newPerp * t);
    }

    /// <summary>
    /// **双曲线的第一步**（照抄 InkClass 的第一笔，见 `MW_ShapeDrawing.cs:1203-1211`）：
    /// 按下 = **中心（原点）**，拖出去 = **渐近线**。
    ///
    /// 这一步定下的是**整个渐近线框**：中心 ＋ 角点 `E`，于是 `A = |dx|`、`B = |dy|`，
    /// 而渐近线就画到 `±(A, B)` —— **正好是你拖到的地方**（见
    /// <see cref="HyperbolaAsymptoteLocal"/>）。用户 2026-09-20 定的口径：
    /// "渐近线画好以后大小完全不动，长度也不动"。
    ///
    /// 这里**不打对折**（原来存的是 `|dx|/2`）：那时渐近线要画到 `2A、2B` 才够长，
    /// 所以"拖多少 → 存一半"；现在渐近线自己就是框、曲线另有大小
    ///（见 <see cref="HyperbolaCurveALocal"/>），"拖到哪就画到哪"才是直的。
    ///
    /// 朝向这里只是**占位**，第二步拖动时会按"点落在渐近线哪一侧"重定。
    /// </summary>
    public void SetHyperbolaFromAsymptote(float x0, float y0, float x1, float y1, float minSize)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);

        var o = new Vector2(x0, y0);
        CurveAxis = CurveAxis.TransverseX;                     // 占位朝向，第二步重定
        float a = MathF.Max(minSize, MathF.Abs(x1 - x0));
        float b = MathF.Max(minSize, MathF.Abs(y1 - y0));
        SetPoint(0, o);
        SetPoint(1, new Vector2(o.X + a, o.Y + b));
    }

    /// <summary>
    /// 由"曲线经过的那个点"`d`（相对中心）定**实轴朝向**：
    ///   · `d` 落在渐近线**更横**的一侧（`|dy| &lt; m·|dx|`）→ **焦点在 x 轴**（左右双曲线）；
    ///   · 否则（更竖）→ **焦点在 y 轴**（上下双曲线）。
    ///
    /// 判据用乘法（等价于 InkClass 的 `|dy/dx| &lt; k`，但不用除、不会碰除零）。
    /// **曲线的大小不在这里算**——那是 <see cref="HyperbolaCurveAThroughPoint"/> 的事。
    /// 拆开是因为 2026-09-20 那一版把"定朝向"和"定大小"混在一个返回值里，
    /// 而大小又要写回第二个点（渐近线框），于是"定曲线"顺手把渐近线也改了。
    /// </summary>
    public static CurveAxis HyperbolaAxisThroughPoint(Vector2 d, float m)
    {
        if (!(m > 1e-4f)) m = 1f;                       // 斜率兜底：第一步没拖动时按 45°
        return MathF.Abs(d.Y) < m * MathF.Abs(d.X)
            ? CurveAxis.TransverseX : CurveAxis.TransverseY;
    }

    /// <summary>
    /// **双曲线的第二步**（用户 2026-09-20 定的口径："渐近线画好以后大小完全不动，
    /// 长度也不动，在通过第三个点生成双曲线"）：
    ///
    /// 渐近线（<see cref="Points"/>[1]）**一个字都不改**，只把"**曲线经过的那个点**"
    /// 记进第三个控制点；曲线的实/虚半轴由它反解（见 <see cref="HyperbolaCurveALocal"/>）。
    /// 朝向仍在这一步定（<see cref="HyperbolaAxisThroughPoint"/>）。
    ///
    /// **和上一版的区别（这是这次改动的全部意义）**：上一版把两个半轴反解出来**写回第二个点**，
    /// 而渐近线是从第二个点派生的 —— 于是"定曲线大小"这一步**顺手把渐近线也改了**，
    /// 用户的原话是"我画图的时候感觉很不适应"。
    /// 现在渐近线（第二个点）不动、曲线大小走第三个点，两件事彻底分开。
    /// </summary>
    public void SetHyperbolaThroughPoint(float x, float y)
    {
        if (Points.Count < 2) return;

        var o = CurvePointLocal(0);
        // 渐近线斜率 = **B / A**（两种朝向下都成立：渐近线方向恒为 (±A, ±B)。
        // 注意它不是 Imag/Real —— 那个比值在实轴沿 y 时会翻过来）。
        float m = HyperbolaBLocal() / MathF.Max(1e-4f, HyperbolaALocal());
        CurveAxis = HyperbolaAxisThroughPoint(new Vector2(x - o.X, y - o.Y), m);

        while (Points.Count < 3) AddPoint(x, y, 1f, 0);
        SetPoint(2, new Vector2(x, y));
    }

    /// <summary>
    /// **正弦 / 余弦的画法**：按下 = **起点**（曲线的起笔处，也就是老师画的那条
    /// y 轴所在的位置），拖出去 = **终点**。一次拖出**两件事**：
    ///   · **横向 = 要画多长**（可以不足一个周期，也可以三四个周期）；
    ///   · **纵向 = 曲线多高**（正弦从轴拖到峰、余弦从峰拖到谷，见 <see cref="WaveAmplitudeLocal"/>）。
    ///
    /// ⚠ 2026-09-20 **改过一次**：原来横向是"**一个周期**"，纵向是振幅——两个量互相独立，
    /// 于是拖一个又宽又矮的框会把波形**拉变形**（画出来不成正弦）；而且一张图上要画好几个
    /// 周期只能重复画好几条。现在横向是"**长度**"，**周期由振幅定**（见
    /// <see cref="WavePeriodLocal"/>）：波形比例恒定，横向拖多长就画多长（用户："画正余弦
    /// **很多个周期**的波浪线"）。
    ///
    /// 两个点都只由这一式算出，所以"拖动预览"和"松手提交"永远一致（不会松手跳一下）。
    /// 横向恒向右展开；`dy` **带符号**，往哪边拖，曲线就先往哪边走。
    /// </summary>
    public void SetWaveBox(float x0, float y0, float x1, float y1, float minSize)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);

        float length = MathF.Max(minSize, MathF.Abs(x1 - x0));             // 要画多长
        float dy = y1 - y0;
        if (MathF.Abs(dy) < minSize) dy = dy < 0f ? -minSize : minSize;   // 高度也有下限
        SetPoint(0, new Vector2(x0, y0));                                  // 起点
        SetPoint(1, new Vector2(x0 + length, y0 + dy));                    // 长度 ＋ 高度
    }

    /// <summary>
    /// 平行四边形的**第四个顶点**（局部坐标）：`第2 + 第3 − 第1`。
    ///
    /// **不存**（用户定，见 9.5）：存下来就有"存的和算的对不上"的可能，
    /// 而只要每次现推，"永远是平行四边形"就是存法本身的性质。
    /// 这一个式子有四个用户（渲染、轮廓折线、紧框、脏区），所以只写在这里一份。
    /// </summary>
    public Vector2 ParallelogramFourthLocal()
        => ParallelogramFourth(new Vector2(Points[0].X, Points[0].Y),
                               new Vector2(Points[1].X, Points[1].Y),
                               new Vector2(Points[2].X, Points[2].Y));

    /// <summary>同上，但三个点由调用方给（拖动预览要按**临时几何**算，见 PolygonInkBounds）。</summary>
    public static Vector2 ParallelogramFourth(Vector2 p0, Vector2 p1, Vector2 p2)
        => new(p1.X + p2.X - p0.X, p1.Y + p2.Y - p0.Y);

    // =====================================================================
    //  坐标系 / 数轴（2026-09-19 第三批）
    //
    //  两个新种类**共用同一套定义**（四个点：外框两角 / 原点 / 单位长度点），
    //  差别只在"外框怎么理解"和"哪几端带箭头"。共用是刻意的：刻度线、单位长度、
    //  手柄、紧框、存档这些事两边一模一样，各写一份迟早会分叉。
    //  存法与理由见 StrokeKind.Coordinate 的注释。
    // =====================================================================

    /// <summary>
    /// 网格那一格的**最小边长**（局部坐标）：防"外框小得离谱时网格密成一片实心"。
    /// </summary>
    public const float AxisMinGridStepLocal = 6f;

    /// <summary>
    /// **网格那一格的边长**（局部坐标）——2026-09-24 用户要的"拖那个格点改方格大小"。
    ///
    /// `0` = **自动**：短边 ÷ 4（老行为，见 <see cref="AxisGridStepLocal"/>）。
    /// 所以老板书打开还是原来那个格子密度，**不用迁移**（存档 v24 新加的那一位读到 0 就是这个意思）。
    ///
    /// **为什么这一次可以存**：2026-09-19 立过一条"单位长度**现算，不存**"——
    /// 理由是那时那个点**画都不画**，留着它就是个"看不见却点得到"的死元素
    /// （仓库里那条"画都不画的东西也不该点得到"）。现在它**画出来了**
    ///（第一象限第一个格子的外角一颗空心点），拖它屏幕上的格子当场变大变小：
    /// 有了可付的可见效果，就该存。
    /// </summary>
    public float AxisGridStep;

    /// <summary>格距的**上限**：再大整个外框里就只剩轴线了（夹住手柄，别拖出个"没有格子的网格"）。</summary>
    public static float AxisMaxGridStepLocal(Vector2 frameA, Vector2 frameB)
        => MathF.Max(AxisMinGridStepLocal,
                     MathF.Min(MathF.Abs(frameB.X - frameA.X), MathF.Abs(frameB.Y - frameA.Y)));

    /// <summary>外框归一化之后的四个边界（局部坐标）。两个点顺序随意，这里统一成 min/max。</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) AxisFrameLocal()
    {
        float ax = Points[0].X, ay = Points[0].Y;
        float bx = Points.Count > 1 ? Points[1].X : ax;
        float by = Points.Count > 1 ? Points[1].Y : ay;
        return (MathF.Min(ax, bx), MathF.Min(ay, by), MathF.Max(ax, bx), MathF.Max(ay, by));
    }

    /// <summary>
    /// **原点 O**（局部坐标，坐标系才有；数轴返回 Zero，别处不读它）。
    /// </summary>
    public Vector2 AxisOriginLocal()
        => Points.Count > 2 ? new Vector2(Points[2].X, Points[2].Y) : Vector2.Zero;

    /// <summary>
    /// **格距手柄的位置**（局部坐标）= 第一象限第一个格子的外角 = 原点 + (格距, −格距)。
    ///
    /// 屏幕 y 向下，所以"第一象限"是**右上** = y 取 −格距（同 <see cref="ShapeHandleLocal"/>
    /// 里 AxisTop 那条注释）。
    /// 它只在 <see cref="Grid"/> 为真时作为手柄存在（没画格子就没有"第一个格子"，
    /// 也就没有"格子多大"这件事可说——见 SelectionHandles.ShapeHandlesOf）。
    /// </summary>
    public Vector2 AxisGridStepHandleLocal()
    {
        float step = AxisGridStepLocal();
        var o = AxisOriginLocal();
        return new Vector2(o.X + step, o.Y - step);
    }

    /// <summary>
    /// **网格那一格有多大**（局部坐标）。只服务坐标系那个可选网格。
    ///
    /// 优先用**存下来的那个值**（<see cref="AxisGridStep"/>，2026-09-24 起：用户拖过那颗
    /// 格点手柄就存下来）——夹在 [最小, 外框短边] 之间，所以把外框拉小之后格子会跟着变小，
    /// 不会出现"格子比外框还大、一个格子都看不见"。
    ///
    /// 没拖过（存的是 0）就**现算**：规则沿用刻度时代那档密度 = **短边 ÷ 4**，
    /// 所以开网格时看着还是课本上那个格子。
    /// </summary>
    public float AxisGridStepLocal()
    {
        var p0 = new Vector2(Points[0].X, Points[0].Y);
        var p1 = Points.Count > 1 ? new Vector2(Points[1].X, Points[1].Y) : p0;
        if (AxisGridStep > 0f)
            return Math.Clamp(AxisGridStep, AxisMinGridStepLocal, AxisMaxGridStepLocal(p0, p1));
        var (minX, minY, maxX, maxY) = AxisFrameLocal();
        return MathF.Max(AxisMinGridStepLocal, MathF.Min(maxX - minX, maxY - minY) / 4f);
    }

    /// <summary>箭头头部的长度（和 <see cref="ArrowHeadPoints"/> 同一口径，跟着笔宽走）。</summary>
    private static float AxisArrowHeadLen(float width) => MathF.Max(width * 5f, 12f);

    /// <summary>
    /// 坐标系 / 数轴的**画法**：由"按下的点 + 当前指针"一次算出全部定义元素。
    ///
    /// 和 <see cref="SetShapeBox"/> 同一个套路（拖动期每帧都要调，所以不分配、不走 SetPoints）：
    ///   · **坐标系**：**三个点** —— 外框一角 / 对角 / **原点 = 按下点**，
    ///     外框以原点为**中心对称展开**（拖出去的那两截就是半宽半高）；
    ///   · **数轴**：**两个点** —— 左端 / 右端，y 一律取**按下点**的 y
    ///     （往斜上方拖也还是水平线，见 StrokeKind.NumberLine：数轴歪了就不是数轴了）。
    ///
    /// 2026-09-19 用户定"两个种类都不要刻度"之后，这里从四个点缩到三个 / 两个：
    /// 少掉的正是"单位长度点"和数轴的"零点"——它们唯一的作用就是给刻度定间距。
    ///
    /// 同一天用户又要求**纠正坐标系的起手**（原话："应该是先确定原点，原点确定以后，
    /// 再把它展开"）：旧版把按下点与指针当成"外框的两个角"、原点硬写在框中心，
    /// 于是"我先按下的那个位置"**永远不是原点**，画完还得再拖一次原点。
    /// 现在按下那一刻原点就定了，拖出去只是让它长大。
    /// **定义元素仍然是三个**，所以手柄 / 紧框 / 命中 / 存档 / 撤销全都不用动。
    /// </summary>
    public void SetAxisBox(float x0, float y0, float x1, float y1)
    {
        if (Kind == StrokeKind.NumberLine)
        {
            // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到两个（控制点数固定）。
            while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
            float lx = MathF.Min(x0, x1), rx = MathF.Max(x0, x1);
            SetPoint(0, new Vector2(lx, y0));          // 左端
            SetPoint(1, new Vector2(rx, y0));          // 右端（箭头在这头）
            return;
        }

        // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到三个（控制点数固定）。
        while (Points.Count < 3) AddPoint(x0, y0, 1f, 0);
        // 半宽半高 = 指针离原点多远（取绝对值，往哪个方向拖都长一样大）。
        float hw = MathF.Abs(x1 - x0), hh = MathF.Abs(y1 - y0);
        SetPoint(0, new Vector2(x0 - hw, y0 - hh));    // 外框一角
        SetPoint(1, new Vector2(x0 + hw, y0 + hh));    // 外框对角
        SetPoint(2, new Vector2(x0, y0));              // 原点 = 按下点（拖到哪都不动）
    }

    /// <summary>
    /// 坐标系 / 数轴的**画布空间墨迹框** = 定义点的外接 ＋ 半笔宽。
    ///
    /// 它以前要多算一截"刻度半长"（刻度是唯一伸出外框的东西）；**刻度取消之后那一截
    /// 就该跟着取消**——框虚胖就是每帧白重画，而这一条是脏区、命中粗筛、导出裁切的依据。
    /// 箭头仍然不用额外算：它的两个翅膀尖在尖端**后面**（见 <see cref="ArrowHeadPoints"/>），
    /// 伸不到外框之外。
    ///
    /// <paramref name="extra"/> 与另外几处同一个用途：拖动预览要再叠一层实时矩阵。
    /// </summary>
    private static RectF AxisInkBoundsOf(ReadOnlySpan<Vector2> pts, in Matrix3x2 transform,
                                         in Matrix3x2 extra, float width)
    {
        var m = transform * extra;
        var r = RectF.Empty;
        for (int i = 0; i < pts.Length; i++)
        {
            var q = Vector2.Transform(pts[i], m);
            r.Add(q.X, q.Y);
        }
        if (r.IsEmpty) return r;
        return r.Inflate(width * 0.5f);
    }

    /// <summary>同上，读模型里的定义点（静止态用）。**定义点最多三个**，所以给三格的 span 就够。</summary>
    public RectF AxisInkBounds(in Matrix3x2 extra)
    {
        int n = Math.Min(Points.Count, 3);
        Span<Vector2> pts = stackalloc Vector2[3];
        for (int i = 0; i < n; i++) pts[i] = new Vector2(Points[i].X, Points[i].Y);
        return AxisInkBoundsOf(pts[..n], Transform, extra, Width);
    }

    // =====================================================================
    //  四种曲线（2026-09-20 第四批）：抛物线 / 双曲线 / 正弦 / 余弦
    //
    //  这一段只有三件事，但它们必须**严格同源**（仓库的老教训：
    //  "同一个名单写在多处 = 加一项必漏一处"）：
    //    ① 画法算式（ParabolaPointAt / HyperbolaPoint / WavePointAt）；
    //    ② 曲线自己的那个矩形（CurveBoxOf）—— 紧框、脏区、命中粗筛、导出裁切全用它；
    //    ③ 采样段数（CurveSegments）。
    //
    //  **一条硬规矩**：这四种曲线的紧框**绝不能**用"控制点的外接"。
    //  双曲线的两支伸得比"中心 ↔ 外角点"远得多（两个方向都要伸到 2 倍），
    //  正弦起点那一侧的半个周期也不在控制点的外接里——而这个框是空间索引的依据，
    //  小掉的后果是"曲线上有些地方点不中"（椭圆当年踩过一模一样的坑，见
    //  WorldInkBounds 里那段注释：紧框算成 256×16）。
    // =====================================================================

    /// <summary>第 `i` 个定义元素（局部坐标）。下标越界返回原点，不抛异常。</summary>
    public Vector2 CurvePointLocal(int i)
        => i >= 0 && i < Points.Count ? new Vector2(Points[i].X, Points[i].Y) : Vector2.Zero;

    /// <summary>
    /// 这种图形**至少**要几个定义元素才算完整。
    ///
    /// 目前只有双曲线需要它（给 3）：它的第三个点 ——"**曲线经过的那个点**"（2026-09-20 加的）
    /// 是**可缺的**（刚起手、或迁移前的老对象只有两个点），而"拖曲线上的那个点"这个手柄
    /// 照样得能按下去；按下去就要往第三格写，格数不够就是**数组越界**
    ///（自检里当场崩过一次，见 Engine.BeginVertexDrag）。
    /// 别的一律 2（它们本来就只有两个定义元素）。
    /// </summary>
    public static int MinCurvePoints(StrokeKind kind) => kind switch
    {
        StrokeKind.Hyperbola => 3,       // 中心 ＋ 渐近线角点 ＋ 曲线经过的点
        StrokeKind.Cuboid => 3,          // 正面矩形两角 ＋ 背面右下角（深度）
        StrokeKind.Tetrahedron => 4,     // 底面三点 ＋ 顶点
        StrokeKind.Prism => 3,           // 底面外接框两角 ＋ 顶面中心（侧棱向量）
        // 棱锥 / 棱台和棱柱**控制点完全一样**（底面外接框两角 ＋ 顶上那个中心）：
        // 差别只在"顶上那个中心算出来的面长什么样"（见 PrismTopLocal），所以只多两行。
        StrokeKind.Pyramid => 3,
        StrokeKind.Frustum => 3,
        _ => 2,
    };

    /// <summary>
    /// 抛物线**开口方向的单位向量**（屏幕坐标：y 向下，所以"向上"是 `(0,−1)`）。
    ///
    /// 用户 2026-09-20 改的口径（"先设定开口向左，然后用两点画出来"）：**方向从"拖动角度"
    /// 里拿出来，变成四个正方向里选一个**（存进 <see cref="CurveAxis"/> 的 OpenUp / Down / Left / Right）。
    ///
    /// 为什么这么改：原来方向和大小**挤在同一个拖动里**（靠 ±12° 死区赌你要开哪个口），
    /// 两个参数互相拽，用户的原话是"这两个参数很不好调整"。拆开之后
    /// **方向用选（离散四个）、大小用拖（连续）**，各自都简单；而且老师在黑板上画的抛物线
    /// 本来就只有正的四种（不再支持"斜的抛物线"——那本来就是上一版为了"自由"加的，
    /// 实际没人用，反而让"想画正的"这件事变得要小心翼翼）。
    ///
    /// 给到四个 Open* 之外的值（坏字节等）一律当**开口向上**（也是枚举的默认值 0）。
    /// </summary>
    public static Vector2 ParabolaDir(CurveAxis axis) => axis switch
    {
        CurveAxis.OpenDown => new Vector2(0f, 1f),
        CurveAxis.OpenRight => new Vector2(1f, 0f),
        CurveAxis.OpenLeft => new Vector2(-1f, 0f),
        _ => new Vector2(0f, -1f),              // OpenUp（兼兜底）
    };

    /// <summary>
    /// 抛物线的**两个单位方向**：`dir` 朝开口外、`perp` 横跨（`dir` 转 90°）。
    /// 几何、手柄、紧框全走它。
    /// </summary>
    public static (Vector2 dir, Vector2 perp) ParabolaBasis(CurveAxis axis)
    {
        var dir = ParabolaDir(axis);
        return (dir, new Vector2(-dir.Y, dir.X));
    }

    /// <summary>
    /// **抛物线开口朝哪边**：由**这一拖的符号**定（照 InkClass 的 `case 20/21`）。
    ///
    /// `pair` 只是"哪一对"：`OpenUp` = **上下抛物**（他的 `y = ax²`）、
    /// `OpenRight` = **左右抛物**（他的 `y² = ax`）——图形面板那一格点一次就在这两档之间换。
    /// 具体朝上还是朝下、朝左还是朝右，看 `q` 落在顶点哪一侧：
    ///
    ///   · 上下档：`q` 在顶点**上方** → 开口向上（屏幕 y 向下，所以比的是 `q.Y < v.Y`）；
    ///   · 左右档：`q` 在顶点**左侧** → 开口向左。
    ///
    /// **为什么方向要跟着拖动走**（用户 2026-09-20："感觉不对，还是参考他的逻辑"）：
    /// 方向由面板选死的时候，"面板选着向上、手却往下拖"会反解出负的沿轴分量，
    /// `p` 直接掉到下限 —— 曲线当场缩成一条细针。
    /// 方向交给这一拖，怎么拖都画得出来；面板只需要回答"上下还是左右"这件推不出来的事。
    /// </summary>
    public static CurveAxis ParabolaAxisOfDrag(Vector2 v, Vector2 q, CurveAxis pair)
    {
        if (pair == CurveAxis.OpenRight || pair == CurveAxis.OpenLeft)      // 左右抛物
            return q.X < v.X ? CurveAxis.OpenLeft : CurveAxis.OpenRight;
        return q.Y < v.Y ? CurveAxis.OpenUp : CurveAxis.OpenDown;           // 上下抛物
    }

    /// <summary>
    /// **焦准距 `p`**（课本里 `y² = 2px` 的那个），抛物线的**唯一**形状参数：
    /// 顶点 ＋ 方向 ＋ `p` 就把整条曲线定死了。焦点在 `dir·(p/2)`、准线过 `−dir·(p/2)`、通径长 `2p`。
    ///
    /// 它由"**曲线要经过的那个点**"`q` 反解（用户的口径："另一个是抛物线的末端点"）——
    /// 把 `q − v` 拆到 `(dir, perp)` 上得 `(s, t)`，而曲线在参数 `u` 处的点是
    /// `v + perp·(u·p) + dir·(u²p/2)`，要求它等于 `q`：
    ///
    ///     u·p = t   →   u = t/p
    ///     u²·p/2 = s   →   t²/(2p) = s   →   **p = t² / (2s)**
    ///
    /// 和双曲线第二步（`a² = dx² − dy²/k²`）是同一类"拿曲线上的一个点反解参数"。
    ///
    /// 两个分量都取**绝对值**：方向是由这一拖的符号定的（见 <see cref="ParabolaAxisOfDrag"/>），
    /// 所以沿轴的分量本该是正的；这里再兜一层，免得"方向没跟上"时反解出负数。
    /// 拖得**几乎平行于轴**（`s ≈ 0`）时张口会趋近无限，所以给 `s` 一个下限 ——
    /// 画出来是一条很扁的抛物线（而不是"没反应"或"一条直线"）。
    /// `t ≈ 0`（点落在对称轴上）没有信息定张口，给下限 `minP`。
    /// </summary>
    public static float ParabolaPThroughPoint(Vector2 v, Vector2 q, CurveAxis axis, float minP)
    {
        var (dir, perp) = ParabolaBasis(axis);
        var d = q - v;
        float s = MathF.Abs(Vector2.Dot(d, dir));      // 沿开口方向的分量
        float t = MathF.Abs(Vector2.Dot(d, perp));     // 横跨方向的分量
        s = MathF.Max(s, minP * 0.5f);
        if (t < 1e-3f) return minP;
        return MathF.Max(minP, t * t / (2f * s));
    }

    /// <summary>
    /// 反解出的 `p` 的**下限**（画布单位）：`p` 太小整条曲线就缩成一个点。
    /// **不乘 DpiScale**——这是几何下限、不是交互容差；画布缩放时它跟着缩放，正是想要的。
    /// </summary>
    public const float ParabolaMinP = 8f;

    /// <summary>实例版（读数 / 紧框 / 手柄 / 渲染都用它）。</summary>
    public float ParabolaPLocal()
        => ParabolaPThroughPoint(CurvePointLocal(0), CurvePointLocal(1), EffectiveAxis, ParabolaMinP);

    /// <summary>实例版的轴方向（单位向量）。</summary>
    public Vector2 ParabolaDirLocal() => ParabolaDir(EffectiveAxis);

    /// <summary>实例版的两个单位方向（单位向量）。</summary>
    public (Vector2 dir, Vector2 perp) ParabolaBasisLocal() => ParabolaBasis(EffectiveAxis);

    /// <summary>
    /// 抛物线在参数 `t` 上的点（**以顶点为原点、轴为 x′** 的那个局部系）：
    /// `x′ = t²·p/2`、`y′ = t·p` —— 这就是 `y′² = 2p·x′` 的现成参数化。
    ///
    /// `t = 0` 是顶点、`t = ±1` 正好落在**通径端点**上（`x′ = p/2、y′ = ±p` ✓ 通径长 2p）；
    /// 画到哪为止见 <see cref="ParabolaSpanOf"/>（**画到"经过点"那儿**，照 InkClass）。
    /// </summary>
    public static Vector2 ParabolaPointAt(Vector2 v, Vector2 q, CurveAxis axis, float t)
    {
        float p = ParabolaPThroughPoint(v, q, axis, ParabolaMinP);
        var (dir, perp) = ParabolaBasis(axis);
        return v + perp * (t * p) + dir * (t * t * p * 0.5f);
    }

    /// <summary>实例版：按模型里的顶点 / 经过点 / 朝向算（渲染与轮廓折线走它）。</summary>
    public Vector2 ParabolaPointAt(float t)
        => ParabolaPointAt(CurvePointLocal(0), CurvePointLocal(1), EffectiveAxis, t);

    /// <summary>
    /// 抛物线**画到 `t` 的哪里为止**（照 InkClass 的 `case 20`：`for (i = 0; i &lt;= |dx|)`）：
    ///
    /// 参数 `u` 处的横跨偏移是 `u·p`，而"曲线经过的那个点"正是老师拖到的位置，
    /// 所以让它落在端点：`u = |t_经过点| / p`。于是曲线**正好停在你拖到的那个点**，
    /// 左右（或上下）对称地铺开 —— 这就是他那个"拖到哪、画到哪"的手感。
    ///
    /// 兜一个下限 `ParabolaMinSpan`：鼠标只是点了一下（几乎没拖）时不至于缩成一个点。
    /// </summary>
    public static float ParabolaSpanOf(Vector2 v, Vector2 q, CurveAxis axis, float minP)
    {
        float p = ParabolaPThroughPoint(v, q, axis, minP);
        var (_, perp) = ParabolaBasis(axis);
        float t = MathF.Abs(Vector2.Dot(q - v, perp));      // 那个点的横跨偏移
        return MathF.Max(ParabolaMinSpan, t / p);
    }

    /// <summary>实例版（渲染 / 轮廓折线 / 紧框都用它）。</summary>
    public float ParabolaSpanLocal()
        => ParabolaSpanOf(CurvePointLocal(0), CurvePointLocal(1), EffectiveAxis, ParabolaMinP);

    /// <summary>抛物线画出来至少铺这么宽（见 <see cref="ParabolaSpanOf"/>）。</summary>
    public const float ParabolaMinSpan = 0.35f;

    /// <summary>
    /// 折线里的**抬笔标记**（双曲线两支之间那一下）。
    ///
    /// <see cref="ShapeOutline"/> 返回的是一条 `List&lt;Vector2&gt;`，而它的三个消费者
    /// （橡皮判交 / 套索判圈 / 打散成笔迹）都是**按相邻两点连线段**读的——
    /// 双曲线有两支，中间直接接过去就凭空多一条横穿包围盒的线。
    /// 用一个"NaN 点"当抬笔，是改动最小的做法：消费者只要跳过 NaN 段即可
    /// （它们本来就各自有一处循环）。
    /// </summary>
    public static readonly Vector2 OutlineBreak = new(float.NaN, float.NaN);

    /// <summary>这个轮廓点是不是"抬笔标记"（见 <see cref="OutlineBreak"/>）。</summary>
    public static bool IsOutlineBreak(Vector2 p) => float.IsNaN(p.X);

    /// <summary>
    /// 按种类**归一之后**的朝向。
    ///
    /// **抛物线和双曲线都需要它**（另外两条曲线没有朝向可言）：
    ///   · 抛物线 = **四个正方向**里选一个（OpenUp / Down / Left / Right，见 <see cref="ParabolaDir"/>）
    ///     —— 它是**选出来的**（图形面板那一格，两点里没有这个信息），只能读字段；
    ///   · 双曲线 = **实轴沿 x 还是沿 y**（TransverseX / TransverseY）
    ///     —— 它是**推出来的**：第三个点（"曲线经过的那个点"）落在渐近线的哪一侧就是哪个
    ///     （见 <see cref="HyperbolaAxisThroughPoint"/>）。
    ///
    /// 双曲线为什么**现推**、不读字段（2026-09-20 定）：朝向和形状必须永远一致。
    /// 读字段的话，拖渐近线角点把斜率扳过对角线之后，那条曲线会卡在
    /// "朝向说左右、点却落在上下那一侧"—— 半轴反解出一个负数 → 曲线**当场缩成一个点**。
    /// 现推就天然一致：点在哪一侧就是哪个朝向；越过对角线的那一刻，两个朝向给出的 `a`
    /// 都趋近于最小，顺滑地翻过去、不会跳。
    ///
    /// 字段本身照样**要写、要存**：只有两个点（刚起手 / 迁移前的老对象）时没有第三个点可推，
    /// 那时它就是兜底；`NormalizeAxis` 还要管"坏字节 / 老文件"落到合法值上。
    /// </summary>
    public CurveAxis EffectiveAxis
    {
        get
        {
            // 双曲线且已经有"曲线经过的那个点" → 现推（理由见上面那段注释）。
            if (Kind == StrokeKind.Hyperbola && Points.Count >= 3)
                return HyperbolaAxisThroughPoint(CurvePointLocal(2) - CurvePointLocal(0),
                                                 HyperbolaBLocal() / MathF.Max(1e-4f, HyperbolaALocal()));
            return NormalizeAxis(Kind, CurveAxis);
        }
    }

    /// <summary>见 <see cref="EffectiveAxis"/>（静态版：读端拿到 kind 之后就地归一）。</summary>
    public static CurveAxis NormalizeAxis(StrokeKind kind, CurveAxis axis)
    {
        if (kind == StrokeKind.Hyperbola)
            return axis == CurveAxis.TransverseY ? CurveAxis.TransverseY : CurveAxis.TransverseX;
        if (kind == StrokeKind.Parabola)
            // 四个正方向之外的（含其它族的值、坏字节）一律当**开口向上**。
            return axis is CurveAxis.OpenDown or CurveAxis.OpenRight or CurveAxis.OpenLeft
                ? axis : CurveAxis.OpenUp;
        return axis;
    }

    /// <summary>
    /// **渐近线框的横向半宽**（`A`，= 角点到中心的 x 距离，恒非负）。
    ///
    /// ⚠ 双曲线**有两套"半轴"，别混**（2026-09-20 拆开，这是"渐近线不再乱动"的关键）：
    ///
    ///   ① **渐近线框** —— `HyperbolaALocal()` / `HyperbolaBLocal()`：
    ///      第二步拖出来的那个矩形（`±A × ±B`），**两条虚线渐近线就是它的对角线**。
    ///      用户定的口径是"渐近线画好以后大小完全不动，长度也不动"——
    ///      所以锁定之后这两个值**一个都不许改**（曲线怎么变都跟它们无关）。
    ///      `A` / `B` 是 **x 方向半宽 / y 方向半高**，不是"谁是实半轴"。
    ///
    ///   ② **曲线自己的半轴** —— `HyperbolaCurveALocal()` / `HyperbolaCurveBLocal()`：
    ///      第三步由"曲线经过的那个点"反解（见 <see cref="HyperbolaCurveAThroughPoint"/>），
    ///      比例恒等于 `B/A`（这样曲线才以那两条虚线为渐近线）。
    ///      **几何 / 截断 / 紧框 / 采样只认这一套。**
    ///
    /// "谁是实半轴"是**第三件事**，随实轴朝向互换（见 <see cref="HyperbolaRealLocal"/>），
    /// 名字只出现在读数文案里。
    /// </summary>
    public float HyperbolaALocal() => HyperbolaAOf(CurvePointLocal(0), CurvePointLocal(1));

    /// <summary>渐近线框的**纵向半高**（`B`，见 <see cref="HyperbolaALocal"/> 那段）。</summary>
    public float HyperbolaBLocal() => HyperbolaBOf(CurvePointLocal(0), CurvePointLocal(1));

    /// <summary>上面两个量的静态核心（拖手柄时要用临时几何算读数，理由同 <see cref="ParabolaHalfSpanOf"/>）。</summary>
    public static float HyperbolaAOf(Vector2 o, Vector2 e) => MathF.Abs(e.X - o.X);

    /// <summary>见 <see cref="HyperbolaAOf"/>。</summary>
    public static float HyperbolaBOf(Vector2 o, Vector2 e) => MathF.Abs(e.Y - o.Y);

    /// <summary>双曲线的两个**轴方向**（单位向量）：`real` = 实轴、`imag` = 虚轴。</summary>
    public static (Vector2 real, Vector2 imag) HyperbolaBasis(CurveAxis axis)
        => axis == CurveAxis.TransverseY
            ? (new Vector2(0f, 1f), new Vector2(1f, 0f))
            : (new Vector2(1f, 0f), new Vector2(0f, 1f));

    /// <summary>
    /// 实轴方向 / 虚轴方向的**分量**（局部坐标，恒非负）：
    /// 拖手柄时把指针位置拆到两个轴上用它（"拖顶点只改 a、拖渐近线点只改斜率"）。
    /// </summary>
    public static (float along, float across) HyperbolaComponents(Vector2 o, Vector2 p, CurveAxis axis)
    {
        var (real, imag) = HyperbolaBasis(axis);
        var d = p - o;
        return (MathF.Abs(Vector2.Dot(d, real)), MathF.Abs(Vector2.Dot(d, imag)));
    }

    /// <summary>
    /// **实半轴**（**曲线自己的**，不是渐近线框的 `A`/`B`）：随实轴朝向互换 ——
    /// 实轴沿 x → 就是曲线的 x 方向半宽 `a`；沿 y → 是曲线的 y 方向半高 `b`。
    /// 只用在**读数文案**和"顶点在哪"上（几何那边一律用 <see cref="HyperbolaCurveALocal"/>）。
    /// </summary>
    public float HyperbolaRealLocal()
        => EffectiveAxis == CurveAxis.TransverseX ? HyperbolaCurveALocal() : HyperbolaCurveBLocal();

    /// <summary>虚半轴（详见 <see cref="HyperbolaRealLocal"/> 那段）。</summary>
    public float HyperbolaImagLocal()
        => EffectiveAxis == CurveAxis.TransverseX ? HyperbolaCurveBLocal() : HyperbolaCurveALocal();

    /// <summary>
    /// **曲线自己的 x 方向半宽系数**（`a`，**不是**渐近线框那个 `A`）：
    /// 由第三个定义元素 —— "**曲线经过的那个点**" —— 反解（见
    /// <see cref="HyperbolaCurveAThroughPoint"/>）。
    ///
    /// 还没定过大小（只有两个控制点）时给 `A/2`（= 曲线画到 `2a = A`，正好铺满框）。
    /// 这种情况**不该发生**（读存档时会补齐第三个点），但兜底不能返回 0。
    /// </summary>
    public float HyperbolaCurveALocal()
        => Points.Count >= 3
            ? HyperbolaCurveAThroughPoint(CurvePointLocal(0), CurvePointLocal(2),
                                          HyperbolaALocal(), HyperbolaBLocal(), EffectiveAxis)
            : MathF.Max(HyperbolaMinA, HyperbolaALocal() * 0.5f);

    /// <summary>曲线的 y 方向半高系数（= `a × B/A`，见 <see cref="HyperbolaCurveALocal"/>）。</summary>
    public float HyperbolaCurveBLocal()
    {
        float a = HyperbolaCurveALocal();
        float A = HyperbolaALocal();
        return a * (A > 1e-4f ? HyperbolaBLocal() / A : 1f);
    }

    /// <summary>
    /// 由"**曲线经过的那个点**"反解曲线的 x 方向半宽系数 `a`。
    ///
    /// 把点拆成 x / y 分量 `(px, py)`，配上"曲线必须以那两条虚线为渐近线"这条锁
    /// （`b/a = B/A`），标准式两边各是一个朝向的式子：
    ///   · **实轴沿 x**：`x²/a² − y²/b² = 1` → `a² = px² − py²·(A/B)²`；
    ///   · **实轴沿 y**：`y²/b² − x²/a² = 1` → `a² = py²·(A/B)² − px²`。
    ///
    /// ⚠ **两个朝向的符号是反的**，这是最容易漏的一处：只写前一个式子的话，
    /// 上下双曲线永远解出一个负数 → `a` 被下限兜住 → 曲线缩成一个点。
    /// 朝向由 <see cref="EffectiveAxis"/> 给（它也是从"这一个点"推出来的，
    /// 见 <see cref="HyperbolaAxisThroughPoint"/>），所以两边永远配套。
    ///
    /// **只夹下限**、不再夹"不超过 `A/2`"（2026-09-20 第二次改）：
    /// 用户的口径是"**根据最后一个点确定双曲线**"—— 点在哪，曲线就必须过哪。
    /// 超出框怎么办？交给**画多长**那头管（见 <see cref="HyperbolaTMaxOf"/>：
    /// 出框就停笔），而不是把曲线的形状掐掉 ——
    /// 掐掉的话，用户点在远处会看到"点了没反应"，那正是他说"很不适应"的那一类手感。
    /// 点落在两支之间（`a² ≤ 0`）时给下限 —— 曲线缩到最小但**不消失**
    /// （"看不见却占着一条对象"是仓库里的老忌）。
    /// </summary>
    public static float HyperbolaCurveAThroughPoint(Vector2 o, Vector2 p, float A, float B, CurveAxis axis)
    {
        if (A < 1e-3f || B < 1e-3f) return HyperbolaMinA;
        float dx = p.X - o.X, dy = p.Y - o.Y;
        float k2 = (A * A) / (B * B);                       // (A/B)²
        float a2 = axis == CurveAxis.TransverseY
            ? dy * dy * k2 - dx * dx                     // 上下：实轴沿 y
            : dx * dx - dy * dy * k2;                    // 左右：实轴沿 x
        float a = a2 > 0f ? MathF.Sqrt(a2) : 0f;
        return MathF.Max(a, HyperbolaMinA);
    }

    /// <summary>
    /// 曲线半轴 `a` 的**下限**（画布单位）：太小整条曲线就缩成一个点。
    /// **不乘 DpiScale**——这是几何下限、不是交互容差（和 <see cref="ParabolaMinP"/> 同一个理由）。
    /// </summary>
    public const float HyperbolaMinA = 8f;

    /// <summary>
    /// **顶点**（实轴两端，落在曲线上）：`side` = ±1 选哪一端。
    /// 用的是**曲线的**实半轴（不是渐近线框的 A/B）。
    /// </summary>
    public Vector2 HyperbolaVertexLocal(int side)
    {
        var o = CurvePointLocal(0);
        var (real, _) = HyperbolaBasis(EffectiveAxis);
        return o + real * (HyperbolaRealLocal() * side);
    }

    /// <summary>
    /// **渐近线框的角点**（就是第二个定义元素本身）：拖它 = 改**渐近线框**
    ///（斜率与长度一起变），曲线的大小不受它影响（曲线有自己的第三个点）。
    /// </summary>
    public Vector2 HyperbolaCornerLocal() => CurvePointLocal(1);

    /// <summary>
    /// **曲线上的那个点**（第三个定义元素 = 第二步拖出来的"经过点"）。
    /// 拖它 = 曲线跟着经过新位置（改的是**曲线的**半轴，**渐近线一个字都不动**）。
    ///
    /// 还没有第三个点的老对象（迁移前）退化成顶点——不该发生（读存档时会补齐，
    /// 见 InkSerializer 的 v14 迁移），但绝不能返回"原点"那种看着正常的错值。
    /// </summary>
    public Vector2 HyperbolaCurvePointLocal()
        => Points.Count >= 3 ? CurvePointLocal(2) : HyperbolaVertexLocal(1);

    /// <summary>
    /// 双曲线的**两条渐近线**（`side` = ±1 选哪一条）：返回它在这条对象范围内的**两端点**。
    ///
    /// 端点 = 中心 ±(A, B)，也就是**第二步拖出来的那个角**（见 <see cref="HyperbolaCornerLocal"/>）
    /// —— "拖到哪就画到哪"，而且**锁定之后再也不会变**（用户 2026-09-20 定的：
    /// "渐近线画好以后大小完全不动，长度也不动"）。
    ///
    /// 这两个端点正是**紧框的四个角**（紧框 = 渐近线框，见 CurveBoxOf 的双曲线那一档），
    /// 所以画渐近线**不会把紧框撑大**；曲线本体又"出框就停笔"
    /// （见 <see cref="HyperbolaTMaxOf"/>），屏幕上就是"曲线严丝合缝地待在两条虚线张开的范围里"。
    /// 这一条很重要：紧框是脏区 / 命中粗筛 / 导出裁切的依据，撑大了就是每帧白重画一大片。
    ///
    /// ⚠ 端点**不能**拿 `HyperbolaBasis` 拆成"实轴方向 A、虚轴方向 B"：
    /// `A` / `B` 恒是 **x / y 方向的半宽半高**（见 <see cref="HyperbolaALocal"/> 那段），
    /// 而渐近线的方向本来就是"横向 A、纵向 B"—— 拿基向量一转，上下双曲线的渐近线
    /// 就会画到框外面去（自检里"渐近线的端点就是紧框的角"那条就是这么红的）。
    /// </summary>
    public (Vector2 from, Vector2 to) HyperbolaAsymptoteLocal(int side)
    {
        var o = CurvePointLocal(0);
        // 方向恒为 (±A, ±B)：两种朝向同一个式子（A / B 本身就是按 x / y 定义的量）。
        var dir = new Vector2(HyperbolaALocal(), side * HyperbolaBLocal());
        return (o - dir, o + dir);
    }

    /// <summary>双曲线要不要画那两条虚线渐近线（对象自己的属性，进存档，默认画）。</summary>
    public bool ShowAsymptotes = true;

    /// <summary>
    /// 改"画不画那两条虚线渐近线"，**并且让几何缓存失效**。
    ///
    /// ⚠ **光给字段赋值是不够的**（2026-09-22 用户报的就是这件事："我选择的不带渐近线的，
    /// 但是画完以后还有渐近线？"）：渐近线走的是**辅助几何槽**，那个槽按 `Revision` 缓存
    /// （见 <see cref="BuildAuxGeometry"/>）。而画的过程中**恒为"画"**（那是画法的向导，
    /// 见 `Engine.BeginShapeAt`），松手那一刻才按面板那一档收口——如果只是把字段改成 false，
    /// 屏幕上那份"带渐近线"的缓存几何**原样留着**，于是看着像"这一档根本没生效"
    ///（下一次编辑把它重建出来才会消失，更让人摸不着头脑）。
    ///
    /// ⚠ 教训（记在 计划-图形工具.md §39.5）：**改一个进几何的字段，就得让它对应的缓存失效**；
    /// 而且只查字段的自检**照不出这个 bug**（第一版就是这么漏的）——现在两处自检都改成
    /// **数屏幕像素**（有那一档那块必须有墨、无那一档必须一个墨点都没有）。
    /// </summary>
    /// <summary>
    /// **配对期暂时别画**（用户 2026-09-25 定："识别的那一刻，第一笔就该消失"）。
    ///
    /// 只影响**画**：`Overlay.DrawStroke` 开头一句就返回 —— 文档、撤销、存档都不动，
    /// 那一笔还躺在原处，提交时 `DwellShapeAction` 靠它的引用把它记进撤销栈。
    ///
    /// ⚠ **提交时必须清掉**（`Engine` 里那段）：不清的话，Ctrl+Z 把这一笔放回来时
    /// 它还是隐藏的 —— 屏幕上"撤销之后什么都没回来"，而且**再也变不回来** ✗
    /// 这也是自检里专门钉的一条（见 `--dwelltest` 的"两笔"那组）。
    ///
    /// ⚠ **不存档**：它是纯运行期的临时状态（和 `ShowAsymptotes` 那种"用户设定"不是一类）。
    /// </summary>
    internal bool HiddenForPairing;

    public void SetShowAsymptotes(bool on)
    {
        if (ShowAsymptotes == on) return;
        ShowAsymptotes = on;
        Revision++;                     // 辅助几何（那两条虚线）重建的触发点
    }

    /// <summary>
    /// **椭圆（带焦点）要不要画焦点三角形**（对象自己的档，进存档，默认画）。
    ///
    /// 两档的含义（用户 2026-09-22 定的口径）：**两个焦点永远画**（"要带焦点"），
    /// 这一档只管**连不连** F₁P、F₂P 那两条边——
    ///   · `true`  ＝ 椭圆 ＋ 两焦点 ＋ 焦点三角形（P 可拖，讲 `|PF₁| + |PF₂| = 2a` 用）；
    ///   · `false` ＝ 椭圆 ＋ 两焦点（只有点，没有三角形）。
    ///
    /// 只对 <see cref="StrokeKind.ConicEllipse"/> 有意义（别的种类恒 true，照旧不读它）。
    /// </summary>
    public bool FocusTriangle = true;

    /// <summary>
    /// 画焦点那两个**小圆点**的半径（局部坐标，逻辑像素）。
    ///
    /// 3 是试出来的：再小（1.5）就只是一个 5 像素的小疙瘩、在板书上找不着；
    /// 再大（6）就盖住了它附近那段椭圆弧，看着像个洞。描边用的是这一笔自己的宽度，
    /// 所以小圆点画出来是"一个略粗的实心点"（内圈只剩 1~2 像素）。
    /// </summary>
    public const float FocusDotRadius = 3f;

    /// <summary>
    /// 焦点三角形第三个顶点 P **在椭圆上的参数角**（弧度，见 <see cref="ConicEllipsePointLocal"/>）：
    /// `P = O + (a·cos u, b·sin u)`。
    ///
    /// **`NaN` = "还没定过"**，这时按 <see cref="DefaultFocusPointU"/> 取**短半轴那一端**
    /// （见那里的注释）。用 NaN 当"自动"这个语义是仓库里已有的约定
    /// （<see cref="OutlineBreak"/> 就是这么标"这里抬笔"的），好处是**不用再加一个
    /// "用户动过没有"的布尔位**——那个位一旦加进来，就又多一处"复制 / 存档 / 撤销
    /// 忘记带上它"的地方。
    /// </summary>
    public float FocusPointU = float.NaN;

    /// <summary>
    /// **P 还没被拖过时的默认位置**：椭圆**左上方**那个点（参数角 −120°）。
    ///
    /// 这个数是**公开规则层**的常量（<see cref="ShapeSpec.FocusPointDefaultU"/>），
    /// 转发一下只是为了"读起来贴着 P"——**画布和图标读的是同一个数**，
    /// 所以图标上那个焦点三角形和真画出来的一模一样。
    /// 为什么选左上方（而不是"正上方"）、为什么是常数（不随 a / b 变），见那边的注释。
    /// </summary>
    public const float DefaultFocusPointU = ShapeSpec.FocusPointDefaultU;

    /// <summary>P 的参数角：没定过（NaN）就取默认那个（左上方，见 <see cref="DefaultFocusPointU"/>）。</summary>
    public float FocusPointAngleLocal
        => float.IsNaN(FocusPointU) ? DefaultFocusPointU : FocusPointU;

    /// <summary>
    /// 焦点三角形第三个顶点 **P**（局部坐标）：按参数角算出来的椭圆上的一个点
    /// （`P = O + (a·cos u, b·sin u)`）。
    ///
    /// **存角不存点**的收益就在这里：a / b 被拖手柄改掉之后 P **自动还在椭圆上**，
    /// 不需要任何一处"改完几何记得把 P 拉回椭圆"的补丁（那种补丁必定有人漏）。
    /// </summary>
    public Vector2 ConicEllipsePointLocal()
    {
        var c = ShapeCenterLocal;
        float u = FocusPointAngleLocal;
        return new Vector2(c.X + MathF.Cos(u) * SemiAxisALocal,
                           c.Y + MathF.Sin(u) * SemiAxisBLocal);
    }

    /// <summary>
    /// **两个焦点**（局部坐标，`F1` 在长轴的负方向那头）：`c = √(a² − b²)`，
    /// 长轴是哪一条由 a / b 谁大决定。
    ///
    /// 退化（正圆，a == b）时 `c = 0`，两个焦点都落在中心——照画（两个点叠在一起），
    /// 不做"正圆不画焦点"那种特例：老师画圆的时候本来就不该用这个工具，
    /// 而半路上把它拖成圆的，看到"焦点收进圆心"正是他想要的那个事实。
    /// </summary>
    public (Vector2 F1, Vector2 F2) ConicEllipseFociLocal()
    {
        var c = ShapeCenterLocal;
        float a = SemiAxisALocal, b = SemiAxisBLocal;
        float cc = a * a - b * b;
        if (cc <= 0f) return (c, c);                 // 圆（或更扁的反向）：焦点收在中心
        float f = MathF.Sqrt(cc);
        return a >= b ? (new Vector2(c.X - f, c.Y), new Vector2(c.X + f, c.Y))
                      : (new Vector2(c.X, c.Y - f), new Vector2(c.X, c.Y + f));
    }

    /// <summary>
    /// 把 P **拖到哪儿**（画布上拖那个圆点时调）：把那个位置折成参数角存起来
    /// （`u = atan2(Δy/b, Δx/a)`）。
    ///
    /// 折算这一步就是"P 永远在椭圆上"的保证：拖到椭圆里面 / 外面都无所谓，
    /// 落到屏幕上的永远是椭圆上离那个方向最近的一点。
    /// </summary>
    public void SetConicEllipsePointFromLocal(float x, float y)
        => SetConicEllipsePointAngle(ConicEllipseAngleOf(new Vector2(x, y)));

    /// <summary>直接写 P 的参数角（拖 P 松手时走这条，见 <see cref="ConicEllipseAngleOf"/>）。</summary>
    public void SetConicEllipsePointAngle(float u)
    {
        FocusPointU = u;
        Revision++;                                  // 几何变了：缓存失效、脏区重算（同 SetPoint）
    }

    /// <summary>
    /// 一个**局部坐标**的点折成 P 的参数角（`atan2(Δy/b, Δx/a)`）。
    ///
    /// 拖 P 的**预览**和**落笔**都调它——两处各写一份 atan2 的话，
    /// 屏幕上拖到的地方和松手之后停的地方会差一点点（"松手跳一下"）。
    /// </summary>
    public float ConicEllipseAngleOf(Vector2 local)
    {
        var c = ShapeCenterLocal;
        float a = MathF.Max(1e-3f, SemiAxisALocal), b = MathF.Max(1e-3f, SemiAxisBLocal);
        return MathF.Atan2((local.Y - c.Y) / b, (local.X - c.X) / a);
    }

    /// <summary>换一下"画不画焦点三角形"（面板那一格再点一次，只作用于**下一笔**）。</summary>
    public void SetFocusTriangle(bool on)
    {
        if (FocusTriangle == on) return;
        FocusTriangle = on;
        Revision++;
    }

    /// <summary>
    /// 双曲线**画到哪为止**（照 InkClass 的 `case 24`：`for (i = a; i &lt;= |dx|)`）：
    /// **画到"曲线经过的那个点"那条轴为止** —— 老师拖到哪，曲线就在那儿收笔。
    ///
    /// 参数 `t` 处的实轴坐标是 `r·cosh t`（`r` = 实半轴），而"经过点"的实轴坐标是 `along`，
    /// 所以让它在端点上：`t = acosh(along / r)`。因为 `r ≤ along` 恒成立
    ///（`a` 就是从这个点反解出来的），`acosh` 的参数一定 ≥ 1，不会无解。
    ///
    /// 这比原来那条"出框就停笔 / 最多画到 2a"更直白：**拖到哪、曲线就画到哪**，
    /// 也和他一模一样（原来那条会让曲线在离指针还有一截的地方就停住，用户说"感觉不对"）。
    ///
    /// ⚠ 于是曲线**可能伸到渐近线框外面**（他本来就没有框这个概念）——
    /// 紧框那头已经改成"框 ∪ 曲线的实际范围"（见 <see cref="CurveBoxOf"/>）。
    /// 兜一个下限 `HyperbolaMinT`：点正好落在实轴上时 `along = r`、`t = 0`，
    /// 不兜的话曲线缩成一个点（"看不见却占着一条对象"是仓库里的老忌）。
    /// </summary>
    public static float HyperbolaTMaxOf(Vector2 o, Vector2 q, CurveAxis axis, float realSemi)
    {
        var (real, _) = HyperbolaBasis(axis);
        float along = MathF.Abs(Vector2.Dot(q - o, real));     // 那个点沿实轴的坐标
        float t = realSemi > 1e-4f ? MathF.Acosh(MathF.Max(1f, along / realSemi)) : 0f;
        return MathF.Max(t, HyperbolaMinT);
    }

    /// <summary>实例版（画曲线、采样折线、紧框都用它，见 <see cref="HyperbolaTMaxOf"/>）。</summary>
    public float HyperbolaTMaxLocal()
        => HyperbolaTMaxOf(CurvePointLocal(0), HyperbolaCurvePointLocal(),
                           EffectiveAxis, HyperbolaRealLocal());

    /// <summary>双曲线画出来至少有这么一段参数（见 <see cref="HyperbolaTMaxOf"/>）。</summary>
    public const float HyperbolaMinT = 0.25f;

    /// <summary>
    /// 双曲线的一支上、参数 `t ∈ [−T, T]` 处的点。
    ///
    /// `branch = 0` 是"第一个分支"、`1` 是另一个（横向时是右支 / 左支，纵向时是上支 / 下支）。
    /// 参数方程用双曲函数：`x = ±a·cosh t、y = b·sinh t` —— 这正是 `x²/a² − y²/b² = 1`
    /// 的现成参数化，`cosh² − sinh² = 1` 就是那个恒等式。
    /// 屏幕 y 向下，所以"上支"是 `y` 变小。
    /// </summary>
    public static Vector2 HyperbolaPoint(Vector2 o, float a, float b, CurveAxis axis, int branch, float t)
    {
        float ch = MathF.Cosh(t), sh = MathF.Sinh(t);
        float side = branch == 0 ? 1f : -1f;
        // `a` / `b` 一律是**x / y 方向的半宽半高**（见 HyperbolaALocal 那段注释），
        // 所以两种朝向下"谁是实轴"体现在：实轴方向用 cosh 铺开、另一个方向用 sinh。
        return axis == CurveAxis.TransverseY
            ? new Vector2(o.X + a * sh, o.Y - side * b * ch)     // 实轴沿 y：上支 / 下支
            : new Vector2(o.X + side * a * ch, o.Y + b * sh);    // 实轴沿 x：右支 / 左支
    }

    /// <summary>正弦 / 余弦的**起点**（局部坐标）：余弦是峰顶、正弦是（第一个）零点。</summary>
    public Vector2 WaveStartLocal() => CurvePointLocal(0);

    /// <summary>
    /// **终点**：`(起点.x ＋ 一个周期, 起点.y ＋ dy)`。它一次定下**周期**和**振幅**
    /// （用户 2026-09-20 定："画的时候就根据起点为原点，还有终点控制周期和振幅"）。
    ///
    /// `dy` **带符号**，符号的含义是"曲线先往哪边去"：
    ///   · 正弦：`dy < 0`（往上拖）= 先上后下，就是课本的 `y = sin x`；
    ///   · 余弦：`dy > 0`（往下拖）= 从峰顶往下，就是课本的 `y = cos x`。
    /// 带符号之后"拖出来的框"和"曲线实际占的地方"一致——上一版取绝对值，
    /// 往下拖时曲线还是往上跑，手感是别扭的。
    /// </summary>
    public Vector2 WaveEndLocal() => CurvePointLocal(1);

    /// <summary>终点相对起点的**纵向偏移**（带符号，见 <see cref="WaveEndLocal"/>）。</summary>
    public static float WaveDyOf(Vector2 start, Vector2 end) => end.Y - start.Y;

    /// <summary>实例版（读数与画法都用它）。</summary>
    public float WaveDyLocal() => WaveDyOf(WaveStartLocal(), WaveEndLocal());

    /// <summary>
    /// 波形**一个周期的长度 ÷ 振幅**（＝"这条波浪画出来有多密"）。
    ///
    /// **用户 2026-09-20 定：一个周期 = 一个振幅**（原话："这个波浪线的比例我想了想
    /// 设置成振幅 = 一个周期"）。所以取 **1**：振幅拖多高，一个周期就有多宽 ——
    /// 拖 300 宽、振幅 100 就是**三个整周期**，数周期不用拿尺子量。
    ///
    /// ⚠ 这条只对**波浪线**（`StrokeKind.Wave`）生效；正弦 / 余弦走"框宽 = 一个周期"，
    /// 和这个常数无关（见 <see cref="WavePeriodLocal"/>）。
    ///
    /// 沿革：这条常数换过两次含义 —— 最早是"框宽 = 一个周期"（多周期得重复画好几条），
    /// 第十五批改成 `4`（周期由振幅定、画多长拖多长），第十六批用户定成 `1`。
    /// 原来的 `4` 是照"屏幕上别太扁"估的（数学上 `y = sin x` 是 `2π ≈ 6.283`：
    /// 用户早在第一版就说过"高度 1 和周期 2π 那么图形很扁"）。
    ///
    /// ⚠ **将来做"曲线挂坐标系 / 按格吸附"时要回到 2π**：那时"1 个单位"由格数定，
    /// 比例就不该由我们定（否则图象和坐标系对不上）。
    /// </summary>
    public const float WavePeriodPerAmplitude = 1f;

    /// <summary>
    /// **一个周期的长度**（像素）。**两种波浪的算法不一样**（2026-09-20 第十六批分的工）：
    ///   · **正弦 / 余弦**（`Sine` / `Cosine`）：**框宽就是一个周期** ——
    ///     `周期 = 框宽`。拖多宽就是"画一个多大周期的图象"，讲课时最直白；
    ///   · **波浪线**（`Wave`）：**周期由振幅定** —— `周期 = WavePeriodPerAmplitude × 振幅`，
    ///     框宽是"**要画多长**"（可以好几个周期）。
    ///
    /// 为什么波浪线要"周期由振幅定"：用户 2026-09-20 那句"**在画正余弦很多个周期的波浪线**"
    /// —— 旧定义里"框宽"和"框高"**互相独立**，拖一个又宽又矮的框会把波形**拉变形**
    ///（画出来的根本不是正弦的样子）。让周期跟着振幅走，**波形比例就恒定了**，
    /// 横向那一拖只剩下一个含义：画多长。
    ///
    /// ★ **2026-09-26：波浪线的周期可以"自带一个"了**（见 <see cref="WavePeriodOf"/>）。
    /// 图形工具那条路一个字节没改（它不写第三个点 → 还是上面这条 T = A）；
    /// 只有**停顿识别**那条路会写 —— 因为老师手画的波，周期和振幅**本来就没关系**
    ///（手画的三个波 T/A 通常是 3~6），锁死 T = A 就只能认"我们自己工具画出来那种比例"。
    /// </summary>
    public float WavePeriodLocal()
        => WavePeriodOf(Kind, WaveStartLocal(), WaveEndLocal(), CurvePointLocal(2));

    /// <summary>
    /// **一个周期的宽度**（三种波共用的一份算式，p0/p1/p2 = 起点 / 终点 / 第三个点）。
    ///
    /// 第三个点**可缺**（`CurvePointLocal(2)` 在点数不够时给 `Vector2.Zero`）：
    ///   · **有**它 → **`周期 = |p2.x − p0.x|`** —— 周期是**对象自己带的一个量**，
    ///     和振幅**互不相干**（识别那条路写的就是它）；
    ///   · **没有**它 → 落回老规矩（波浪线 `T = 振幅`、正弦 / 余弦 `T = 框宽`）。
    ///
    /// ⚠ 为什么用 `Vector2.Zero` 当"没有"的哨兵：和双曲线第三个点**同一个约定**
    ///（见 `CurveBoxOf` 里那句"第三个点可缺"，那边也是这么判的）。
    /// 不会误判：识别写进去的是 `p0 + (周期, 0)`，而周期 ≥ 1 像素，
    /// 只有"p0 正好在原点且周期为 0"才会撞上 —— 那个状态不存在。
    ///
    /// ⚠ **只写这一份**：`WavePeriodLocal`（实例、渲染/手柄/读数走它）和
    /// `CurveBoxOf`（静态、紧框走它）都调它 —— 以前是两份手抄的同一句话，
    /// 加这个自由度时正好收敛掉（"同一个名单写两处必漏一处"是本仓库的老账）。
    /// </summary>
    public static float WavePeriodOf(StrokeKind kind, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        if (kind != StrokeKind.Wave) return MathF.Abs(p1.X - p0.X);    // 正弦 / 余弦：框宽就是周期
        if (p2 != Vector2.Zero) return MathF.Max(1e-3f, MathF.Abs(p2.X - p0.X));
        return WavePeriodPerAmplitude * WaveAmplitudeOf(p0, p1, kind); // 老规矩：周期 = 振幅
    }

    /// <summary>
    /// **给波浪线写"一个周期的宽度"**（第三个定义元素，2026-09-26 加）。
    ///
    /// 只有**停顿识别**那条路调它（`ShapeRecognize.ApplyAxis`）：老师手画的多周期波，
    /// 周期和振幅没有固定比例，锁死 `T = 振幅` 会把波形**压扁或拉长** ——
    /// 那正是"变出来和画的不一样 = 比不变更糟"。
    ///
    /// 图形的**第三个点只用到 x**（周期 = `|p2.x − p0.x|`），y 写成起点的 y 就行
    ///（`CurveBoxOf` / `WaveTracedPointAt` 都不读它的 y）。
    /// ⚠ 它**不是手柄**：四种曲线走通用框（见 `Selection.HasShapeHandles`），
    /// 所以识别出来的周期**暂时拖不动**——想改得再补一个手柄。
    /// </summary>
    public void SetWavePeriod(float period)
    {
        while (Points.Count < 3) AddPoint(Points[0].X, Points[0].Y, 1f, 0);
        SetPoint(2, new Vector2(Points[0].X + MathF.Max(1f, period), Points[0].Y));
    }

    /// <summary>**框宽**（＝横向拖了多远）。正弦 / 余弦里它就是"一个周期"，波浪线里是"要画多长"。</summary>
    public float WaveLengthLocal() => MathF.Abs(WaveEndLocal().X - WaveStartLocal().X);

    /// <summary>
    /// 画出来一共**几个周期**。
    ///   · 正弦 / 余弦：**恒为 1**（框宽就是一个周期）；
    ///   · 波浪线：`要画多长 ÷ 周期`，**可以不是整数**（拖到哪儿画到哪儿，最后一段是半截）。
    /// 渲染的采样密度也按它算（见 <see cref="WavePointAt"/> 的调用方）。
    /// </summary>
    public float WaveCyclesLocal()
    {
        float t = WavePeriodLocal();
        return t > 1e-3f ? WaveLengthLocal() / t : 1f;
    }

    /// <summary>
    /// 振幅 A（恒非负）。**余弦和另外两种的换算不一样**，这是本族唯一一处不对称：
    ///   · 正弦 / **波浪线**：起点在轴上，峰到轴的距离就是 A → `A = |dy|`；
    ///   · 余弦：起点在峰顶，半个周期后到谷，谷离峰顶 `2A` → `A = |dy| / 2`。
    ///
    /// 它俩共同点是"**拖出来的终点都落在曲线的极值点上**"（正弦拖到峰的高度、
    /// 余弦从峰拖到谷），所以"拖到哪儿"和"看着多高"是一致的，所见即所得。
    /// </summary>
    public float WaveAmplitudeLocal() => WaveAmplitudeOf(WaveStartLocal(), WaveEndLocal(), Kind);

    /// <summary>振幅的静态核心（同一个式子只写一份）。</summary>
    public static float WaveAmplitudeOf(Vector2 start, Vector2 end, StrokeKind kind)
        => kind == StrokeKind.Cosine
            ? MathF.Abs(WaveDyOf(start, end)) * 0.5f
            : MathF.Abs(WaveDyOf(start, end));

    /// <summary>
    /// 波形上 `u` 处的点（`u` 的**单位是周期**：`u = 0` 起点、`u = 1` 一个周期之后）。
    /// 画出来的那一段是 `u ∈ [0, <see cref="WaveCyclesLocal"/>]`（可以不是整数）。
    ///
    ///   · **正弦**：`y = 起点.y + dy·sin(2πu)`（`dy < 0` 就是先上后下 ✓）；
    ///   · **余弦**：`y = 起点.y + dy·(1 − cos(2πu))/2`（起点在峰顶、中间到谷 ✓）。
    /// 两式在 u = 0 处分别给出"轴"和"峰"，正是"从起点（y 轴）开始画"。
    ///
    /// **横向一律往右画**（长度取框宽的绝对值）：左右拖动只影响"画多长"，
    /// 不影响朝向（朝向由**上下**拖的符号定）——和抛物线"开口朝哪边由这一拖定"是两回事。
    /// </summary>
    public static float WaveYAt(Vector2 start, Vector2 end, StrokeKind kind, float u)
    {
        float dy = WaveDyOf(start, end);
        double phase = u * MathF.Tau;
        return kind == StrokeKind.Cosine
            ? start.Y + (float)(dy * (1.0 - Math.Cos(phase)) * 0.5)
            : start.Y + (float)(dy * Math.Sin(phase));
    }

    /// <summary>实例版：按模型里的两个定义元素算（渲染与轮廓折线走它）。</summary>
    public Vector2 WavePointAt(float u)
        => new(WaveStartLocal().X + WavePeriodLocal() * u,
              WaveYAt(WaveStartLocal(), WaveEndLocal(), Kind, u));

    /// <summary>
    /// **画出来的那一段**上 `t ∈ [0, 1]` 处的点（`t = 0` 起点、`t = 1` 终点）。
    ///
    /// 渲染 / 轮廓折线 / 紧框采样都走它——它们要的是"整条曲线从这头到那头"，
    /// 而不是"第几个周期"。**换算只有这一处**（把"按周期数"折成"按比例"）。
    /// </summary>
    public Vector2 WaveTracedPointAt(float t) => WavePointAt(WaveCyclesLocal() * t);

    /// <summary>波形那一段的**采样段数**：按总长算，并且保证每个周期至少 24 段（多个周期才够滑）。</summary>
    private int WaveSegmentsLocal()
        => Math.Clamp((int)MathF.Ceiling(WaveLengthLocal() / 16f * MathF.Max(1f, WaveCyclesLocal())),
                      24, 512);


    /// <summary>
    /// 峰 / 谷这两个**极值点**（用户说的"统一根据最大最小点来控制图形"）。
    ///
    /// 它们是**算出来的、不存**——存的是起点和终点两个定义元素，
    /// 和"平行四边形的第四点现推"是同一条规矩：只存定义，其余现推。
    /// 位置直接取曲线上那个周期数（正弦峰 1/4、谷 3/4；余弦峰在起点、
    /// 谷在 1/2），所以**一定落在曲线上**，不会悬空。
    ///
    /// ⚠ 2026-09-20：`u` 现在要**夹在画出来的范围里**（`WaveCyclesLocal`）——
    /// 画多长由拖动定，**可以不足四分之一个周期**（那时峰还没到就收笔了），
    /// 不夹的话这两个点会落在曲线外面。
    /// </summary>
    private Vector2 WaveExtremumAt(bool crest)
    {
        var start = WaveStartLocal();
        var end = WaveEndLocal();
        bool cos = Kind == StrokeKind.Cosine;
        float u = cos ? (crest ? 1f : 0.5f) : (crest ? 0.25f : 0.75f);
        u = MathF.Min(u, WaveCyclesLocal());
        return new Vector2(start.X + WavePeriodLocal() * u, WaveYAt(start, end, Kind, u));
    }

    /// <summary>**峰点**（在曲线上）：正弦是 1/4 处那个；余弦是周期末端那个（起点那个就是起点本身）。</summary>
    public Vector2 WaveCrestLocal() => WaveExtremumAt(crest: true);

    /// <summary>**谷点**（在曲线上）：正弦是 3/4 处那个；余弦是 1/2 处那个。</summary>
    public Vector2 WaveTroughLocal() => WaveExtremumAt(crest: false);

    // =====================================================================
    //  **正切 y = tan x**（2026-09-20 第十五批）
    //
    //  一支（定义域 (−π/2, π/2)）画成"以原点为中心的一个框"：
    //    按下 = **原点**（这一支的中心、图象与 x 轴相交处），拖出 = 那个框的一角。
    //    横向 = **半支长**（渐近线就在 ±这个距离上）、纵向 = **可视半高**（曲线冲到那儿截断）。
    //
    //  为什么它和正弦 / 余弦的画法不一样：
    //    · 正弦 / 余弦的形状比例（周期 : 振幅 = 2π : 1）是**数学事实**，只能有一个自由度，
    //      所以纵向拖的是"振幅"、横向拖的是"长度"，`ShapeSpec` 之外那个比例常数负责固定形状；
    //    · 正切**值域无界**，"画到多高"是个**显示选择**（课本一般画到 ±3 左右），
    //      所以纵向那一拖本来就该是"看得见多少"。
    //    · 而它的**形状**由"横竖同一个单位"钉死（π/2 个单位 = 半支长）→
    //      **不会被拖变形**，所以不需要"周期 : 振幅"那种比例常数；
    //      只有一个**下限比例**（`TangentMinAspect`）兜着"别画成一条斜线"（见那一处注释）。
    // =====================================================================

    /// <summary>正切那**一支的中心**（＝按下那个点：图象与 x 轴的交点，也是原点）。</summary>
    public Vector2 TangentOriginLocal() => CurvePointLocal(0);

    /// <summary>拖出去的那个点（它和原点一起定下"半支长"和"可视半高"）。</summary>
    public Vector2 TangentEndLocal() => CurvePointLocal(1);

    /// <summary>**半支长**：渐近线离原点这么远（两条渐近线在 `原点 ± 半支长`）。</summary>
    public float TangentHalfSpanLocal()
        => MathF.Max(0.5f, MathF.Abs(TangentEndLocal().X - TangentOriginLocal().X));

    /// <summary>
    /// **可视半高**（像素）：曲线冲到离原点这么高就截断（＝框的上下边）。
    ///
    /// ⚠ **有一个下限比例**（<see cref="TangentMinAspect"/>）：横竖既然共用一个单位，
    /// "看得见多高"就直接决定"两头离渐近线还有多远"——框太矮的时候曲线只画到一半就截断了，
    /// 看着是**一条斜线**、不像正切（用户 2026-09-20 上手就发现了：他要"很窄很高"才贴着渐近线，
    /// 而那本来不该靠手感去拖）。所以这里把高度**顶到"贴着渐近线"的那个最小比例**：
    /// 随手拖个方框，画出来也是课本那个样子；拖得更高仍然照你的来（越高越贴）。
    /// </summary>
    public float TangentHalfHeightLocal()
    {
        float dragged = MathF.Max(0.5f, MathF.Abs(TangentEndLocal().Y - TangentOriginLocal().Y));
        return MathF.Max(dragged, TangentMinAspect * TangentHalfSpanLocal());
    }

    /// <summary>
    /// 正切的**最小高宽比**（可视半高 ÷ 半支长）＝ **3**。
    ///
    /// 横竖共用一个单位（见 <see cref="TangentUnitLocal"/>），所以这个比例等价于
    /// "曲线画到 `θ = arctan(3·π/2) ≈ 1.36 rad ≈ 78°`"，也就是**画到离渐近线只差 13% 的地方**
    /// —— 课本上的正切正是这个"两头几乎竖直、贴着渐近线"的样子（再往下拖就该截断了）。
    /// 用户 2026-09-20 的原话："很窄很高的时候会贴着渐近线，说明这个比例的设置比较重要"：
    /// 把这条**做进引擎**，就不用老师靠手感去拖高拖窄了。
    /// </summary>
    public const float TangentMinAspect = 3f;

    /// <summary>
    /// **一个单位有多长**（像素）＝ 半支长 ÷ (π/2)。
    ///
    /// 因为 `x = ±π/2` 正好是渐近线，而渐近线又落在框的左右边上 ——
    /// 于是"横向拖多宽"就把 x 方向的**单位**定死了；正切图象要求**横竖同尺度**
    ///（`y = tan x` 这条曲线本身），所以 y 方向的单位**同一个**。
    /// </summary>
    public float TangentUnitLocal() => TangentHalfSpanLocal() / (MathF.PI * 0.5f);

    /// <summary>画得到的最大参数：`|tan θ| ≤ 可视半高 / 单位` 处（再往外就冲出框了）。</summary>
    public float TangentThetaMaxLocal()
    {
        float u = TangentUnitLocal();
        return MathF.Min(MathF.PI * 0.5f * 0.999f, MathF.Atan(TangentHalfHeightLocal() / u));
    }

    /// <summary>
    /// 正切一支上、参数 `θ ∈ (−π/2, π/2)` 处的点（局部坐标）。
    /// 屏幕 y 向下，所以 `tan` 越大越**往上**（`y` 越小）——曲线从左下冲到右上，正是课本的样子。
    /// </summary>
    public Vector2 TangentPointAtTheta(float theta)
    {
        var o = TangentOriginLocal();
        float u = TangentUnitLocal();
        return new Vector2(o.X + theta * u, o.Y - MathF.Tan(theta) * u);
    }

    /// <summary>
    /// **画出来的那一支**上 `t ∈ [0, 1]` 处的点（`t = 0` 左下、`t = 1` 右上）。
    /// 渲染 / 轮廓折线共用它（和正弦那条 `WaveTracedPointAt` 同一个套路）。
    /// </summary>
    public Vector2 TangentTracedPointAt(float t)
    {
        float m = TangentThetaMaxLocal();
        return TangentPointAtTheta(-m + 2f * m * t);
    }

    /// <summary>
    /// 正切的**两条渐近线**（`side = −1` 左边、`+1` 右边）：都从框的上下边穿过，
    /// 所以端点就是框的上下角。走**辅助几何槽**画成细虚线（和双曲线的渐近线同一种画法）。
    /// </summary>
    public (Vector2 From, Vector2 To) TangentAsymptoteLocal(int side)
    {
        var o = TangentOriginLocal();
        float x = o.X + side * TangentHalfSpanLocal();
        float h = TangentHalfHeightLocal();
        return (new Vector2(x, o.Y - h), new Vector2(x, o.Y + h));
    }

    /// <summary>
    /// **正切的画法**：按下 = 原点（这一支的中心），拖出 = 以它为中心的框的一角
    ///（横向 = 半支长、纵向 = 可视半高）。两个方向都取绝对值：
    /// 这一支**永远是"左下 → 右上"**，往哪个方向拖都是同一支（它没有"开口朝哪边"这回事）。
    ///
    /// ⚠ 存下来的是**你拖的那个值**，"看得见多高"另有下限（见 `TangentHalfHeightLocal`）——
    /// 读的时候才顶上去，所以**画、紧框、渐近线三者用的是同一个数**。
    /// </summary>
    public void SetTangentBox(float x0, float y0, float x1, float y1, float minSize)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        // 半支长 / 半高都有下限（拖得太小会退化成一个点那么高，没法看）
        float hx = MathF.Max(minSize, MathF.Abs(x1 - x0));
        float hy = MathF.Max(minSize, MathF.Abs(y1 - y0));
        SetPoint(0, new Vector2(x0, y0));                        // 原点
        SetPoint(1, new Vector2(x0 + hx, y0 + hy));              // 框的一角
    }

    /// <summary>
    /// 正切那一支的**采样段数**：两端越靠近渐近线越陡（`tan` 在那儿变化极快），
    /// 所以按"半支长 **或可视半高**的像素"给密度（高度被下限顶高之后，尾巴也变长了，
    /// 只看半支长会让那一段看着是折线）。
    /// </summary>
    private int TangentSegmentsLocal()
        => Math.Clamp((int)MathF.Ceiling(
               MathF.Max(TangentHalfSpanLocal(), TangentHalfHeightLocal()) / 4f), 24, 512);

    /// <summary>
    /// 曲线自己的那个矩形（**局部坐标**）。**全引擎唯一一份**，紧框 / 包围盒 / 脏区都问它。
    ///
    /// 四档的算式：
    ///   · 抛物线：顶点 ↔ 控制点那个矩形（开口朝上时它正好是曲线的最小外接；
    ///     朝左 / 朝右时控制点管的是"深度 ＋ 半高"，同样是那个矩形）；
    ///   · 双曲线：**`±A × ±B`，就是渐近线的那个矩形**（A / B = x / y 方向的半宽半高）——
    ///     两个朝向同一个式子，见下面那一档；
    ///   · 正弦：`一个周期宽 × ±A`；余弦：`一个周期宽 × (起点 → 谷底 2A)`；
    ///     波浪线：`画多长 × 按周期数算到的峰 / 谷`（拖不足一个周期时峰还没到，框跟着收）。
    ///
    /// `axis`（曲线朝向）：**这一档现在谁都没用**——双曲线原来按实/虚分流、各带一个 `√5`，
    /// 改成"渐近线框"之后两个朝向是同一个式子。参数留在签名里是因为它属于"曲线框"这一层的
    /// 一般化描述（调用方按同一套签名传参，删了要在好几处签名里绕一圈，得不偿失）。
    /// </summary>
    public static RectF CurveBoxOf(StrokeKind kind, CurveAxis axis, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        switch (kind)
        {
            case StrokeKind.Parabola:
            {
                // 抛物线：**顶点 ＋ 经过点 ＋ 朝向**定形 —— 朝向给轴、`p` 由经过点反解
                //（见 ParabolaPThroughPoint）。画出来的范围是 `|t| ≤ ParabolaSpanOf`，于是
                //   横跨方向：±(Span·p)，沿轴方向：0 … Span²·p/2
                // 取"顶点 + 两个端点"这三个点的外接就够（O(1)，不用逐点采样——
                // 紧框每帧都要问，逐点算就白费了）。
                float p = ParabolaPThroughPoint(p0, p1, axis, ParabolaMinP);
                var (dir, perp) = ParabolaBasis(axis);
                float t = ParabolaSpanOf(p0, p1, axis, ParabolaMinP);
                var tip = dir * (t * t * p * 0.5f);
                var span = perp * (t * p);
                var r = RectF.Empty;
                r.Add(p0.X, p0.Y);
                r.Add(p0.X + tip.X + span.X, p0.Y + tip.Y + span.Y);
                r.Add(p0.X + tip.X - span.X, p0.Y + tip.Y - span.Y);
                return r;
            }

            case StrokeKind.Hyperbola:
            {
                // 紧框 = **渐近线框 ∪ 曲线实际画到的范围**（A / B 是 x / y 方向的半宽半高）。
                //
                // 这一条把三件事一次对齐：
                //   · 渐近线的两个端点**就是** `±(A, B)`（见 HyperbolaAsymptoteLocal）→ 端点在框角上；
                //   · 曲线"**画到"经过点"那儿为止**"（见 HyperbolaTMaxOf，照 InkClass）——
                //     那个点通常落在框里，于是整条曲线在框内；**拖到框外时曲线也跟着出去**，
                //     所以紧框要取**两者的并集**（下面那几行就是干这个的）。
                //     不能只取 `±A × ±B`：曲线伸出去的那一截会被裁掉（脏区 / 导出都跟着错）。
                //   · 于是紧框既不会被渐近线白白撑大、也不会比曲线小。
                // 朝向**不影响**"框"这一半：A、B 本来就是"x / y 方向的量"，虚实互换不影响外接矩形。
                float halfX = MathF.Abs(p1.X - p0.X);
                float halfY = MathF.Abs(p1.Y - p0.Y);
                // 第三个点**可缺**（刚起手 / 三道静态调用点会传 Zero 进来）：那时曲线根本没画
                //（`HyperAsymptotePreviewOnly` 只画虚线），紧框就只是渐近线框本身。
                if (p2 != Vector2.Zero)
                {
                    float a = HyperbolaCurveAThroughPoint(p0, p2, halfX, halfY, axis);
                    float b = a * (halfX > 1e-4f ? halfY / halfX : 1f);
                    float r = axis == CurveAxis.TransverseY ? b : a;              // 实半轴
                    float sh = MathF.Sinh(HyperbolaTMaxOf(p0, p2, axis, r));
                    float dx = MathF.Abs(p2.X - p0.X), dy = MathF.Abs(p2.Y - p0.Y);
                    // 实轴那一侧的伸展**正好停在那个点的坐标上**（画到那儿为止），另一侧是 `半轴 × sinh`
                    halfX = axis == CurveAxis.TransverseY ? MathF.Max(halfX, a * sh) : MathF.Max(halfX, dx);
                    halfY = axis == CurveAxis.TransverseY ? MathF.Max(halfY, dy) : MathF.Max(halfY, b * sh);
                }
                return new RectF
                {
                    MinX = p0.X - halfX, MinY = p0.Y - halfY,
                    MaxX = p0.X + halfX, MaxY = p0.Y + halfY,
                };
            }

            case StrokeKind.Tangent:
            {
                // 正切一支：**正好就是那个框**（两条渐近线在左右边上、曲线截断在上下边上）——
                // 所以紧框 = 框本身，一句就够，而且**精确**（见 TangentAsymptoteLocal）。
                //
                // ⚠ 高度那一维必须**和 `TangentHalfHeightLocal` 用同一份算式**（含下限比例）：
                // 少算这一条，被顶高的那一截就落在紧框外面 —— 脏区少算 → 曲线尾巴留旧像素。
                float hx = MathF.Max(0.5f, MathF.Abs(p1.X - p0.X));
                float hy = MathF.Max(MathF.Abs(p1.Y - p0.Y), TangentMinAspect * hx);
                return new RectF
                {
                    MinX = p0.X - hx, MinY = p0.Y - hy,
                    MaxX = p0.X + hx, MaxY = p0.Y + hy,
                };
            }

            default:
            {
                // 正弦 / 余弦 / **波浪线**：**按解析式给纵向极值**。
                //
                // 2026-09-20 改过两轮：先是从"一个周期宽 × 各自的纵向范围"改成
                // "框宽 = 画多长"（那时多周期是正弦自己干的），第十六批又把这两件事**分给两种图形**：
                //   · 正弦 / 余弦：**框宽就是一个周期** → 周期数恒为 1（峰和谷都在里面）；
                //   · 波浪线：**周期由振幅定** → 周期数可以是小数（峰还没到就收笔）也可以是好几个。
                // 所以下面这套"按周期数算纵向极值"的算式对两种都成立，是同一份。
                //
                // ⚠ **必须精确，不能采样**：采样写的话极值点大多落在两个采样点之间，
                // 框会比曲线**小一点点** —— 那点差值正好是脏区少算的部分（曲线末梢会留旧像素）。
                // 分两种情况讨论虽然啰嗦，但每个都是教科书上的初等结论。
                float len = MathF.Abs(p1.X - p0.X);
                // **周期走共用的那一份算式**（见 `WavePeriodOf`）—— 波浪线的周期现在
                // **可以自带**（第三个定义元素，识别那条路写的），所以这里不能再用
                // "周期 = 振幅"那一句手抄；没有第三个点时它自己就落回老规矩。
                float t = WavePeriodOf(kind, p0, p1, p2);
                float cycles = t > 1e-3f ? len / t : 1f;
                float dy = WaveDyOf(p0, p1);
                float lo, hi;                       // 纵向占到的比例（乘 dy 就是位移）
                if (kind == StrokeKind.Cosine)
                {
                    // `f(u) = (1 − cos 2πu)/2`：u=0 处为 0，之后**单调升**到 u=1 处的 1
                    //（所以"谷"不一定出现：只有一个周期以上才落到 1）。
                    lo = 0f;
                    hi = cycles >= 1f ? 1f : (1f - MathF.Cos(MathF.Tau * cycles)) * 0.5f;
                }
                else
                {
                    // 正弦：峰在 1/4 处、谷在 3/4 处 —— **画到哪儿就认到哪儿**。
                    hi = cycles >= 0.25f ? 1f : MathF.Sin(MathF.Tau * cycles);
                    lo = cycles >= 0.75f ? -1f : 0f;
                }
                float yA = p0.Y + dy * lo, yB = p0.Y + dy * hi;
                return new RectF
                {
                    MinX = p0.X, MinY = MathF.Min(yA, yB),
                    MaxX = p0.X + t * cycles, MaxY = MathF.Max(yA, yB),
                };
            }
        }
    }

    /// <summary>实例版：读模型里的定义元素（静止态走它）。</summary>
    public RectF CurveBoxLocal()
        => CurveBoxOf(Kind, EffectiveAxis, CurvePointLocal(0), CurvePointLocal(1), CurvePointLocal(2));

    /// <summary>
    /// 四种曲线的**画布空间墨迹框** = 曲线矩形过变换取外接 ＋ 半笔宽。
    ///
    /// 和 <see cref="ParametricInkBoundsOf"/> 同一个套路（先把矩形整体过变换再取外接）：
    /// 只有这样，转过 30° 的曲线框才不会虚胖一大圈（那是脏区白重画的经典来源）。
    /// </summary>
    private static RectF CurveInkBoundsOf(StrokeKind kind, CurveAxis axis, in Matrix3x2 transform,
                                          in Matrix3x2 extra, Vector2 p0, Vector2 p1, Vector2 p2,
                                          float width)
        => TransformRect(CurveBoxOf(kind, axis, p0, p1, p2), transform * extra)
           .Inflate(width * 0.5f);

    /// <summary>
    /// 曲线的折线近似**段数**：按尺寸定（每段弦长 ≈ 16 像素），夹在 [24, 128]。
    /// 和 <see cref="EllipseSegments"/> 同一个口径——不然会出现"圆很顺、抛物线是折的"。
    /// </summary>
    private static int CurveSegments(float extent)
        => Math.Clamp((int)MathF.Ceiling(extent / 16f), 24, 128);

    /// <summary>
    /// 一组**局部坐标点**在给定变换下占的画布范围（= <see cref="PaddedBounds"/> 的算法，
    /// 但用的是**传进来的点**，不碰对象）。
    ///
    /// 需要的场景只有一个：**改几何的动作要在动手之前**算出"改完之后占哪块"，
    /// 好把新位置的脏区标出来（见 SetStrokeGeometryAction）。先改后算的话，
    /// 旧位置就再也问不出来了。
    ///
    /// **必须按"这个图形是怎么定义的"分流，和 <see cref="PaddedBounds"/> /
    /// <see cref="PreviewInkBounds"/> 一张表**（这三个是同一件事的三个入口）：
    ///   · 圆 / 椭圆 → 参数化外接（圆心 ± 半轴）。它们的两个控制点是**圆心 + 圆周点**，
    ///     那两个点的外接只是形体的一角——拿它当脏区，2026-09-19 实测过一次：
    ///     拖椭圆的轴端点松手后**左边一整块不画**（脏区窄了一列分块，那一列从
    ///     "起手把形体摘出内容层"之后就再没被重画过）。
    ///   · 三角形 / 平行四边形 → **每个顶点各自过变换再取外接**：转过的图形不能
    ///     "先取局部外接再整体转"，那样框明显虚胖。平行四边形的第四个顶点不在点表里，
    ///     少了它，脏区会漏掉整个右上角（那条边挪过去之后原地就留下一条擦不掉的残影）。
    ///   · 直线 / 箭头 / 矩形 / 图像 / 自由笔迹 → 照旧"点的外接过变换"。
    ///     它们不需要另开一条：端点的外接就是形体的外接（箭头的翅膀尖虽然伸到轴外
    ///     ~22 像素，但那个距离远小于块边长 256，"漏掉的那一小条"永远和端点带同处一块，
    ///     所以看不出缺块——2026-09-19 用像素探针验过）。
    ///   · **曲线**（抛物线 / 双曲线 / 正弦 / 余弦）→ 曲线自己的矩形（见
    ///     <see cref="CurveBoxOf"/>）。它们**必须**单独一档：双曲线的两支比"中心 ↔ 外角点"
    ///     远得多，正弦起点那一侧的半个周期也不在控制点的外接里。
    ///
    /// <paramref name="axis"/> 只有曲线用得上（抛物线开哪个口 / 双曲线哪条是实轴）：
    /// 朝向是"对象的样子"（见 <see cref="Stroke.CurveAxis"/>），所以**静态这一份也得知道它**，
    /// 否则"改几何前先算新位置占哪块"（这只在动手前算，对象还是旧的）就会算错。
    /// </summary>
    public static RectF PaddedBoundsOf(IReadOnlyList<Vector2> local, in Matrix3x2 transform, float width,
                                       StrokeKind kind = StrokeKind.Freehand,
                                       CurveAxis axis = CurveAxis.OpenUp)
    {
        if (local == null || local.Count == 0) return RectF.Empty;

        // 四种曲线（抛物线 / 双曲线 / 正弦 / 余弦）：走**曲线自己的那个矩形**
        // （不能是"控制点的外接"：双曲线会小掉一大圈、正弦会漏掉起点那一侧）。
        if (IsCurveKind(kind) && local.Count >= 2)
        {
            var q2 = local.Count >= 3 ? local[2] : Vector2.Zero;
            // 朝向先按种类归一（见 Stroke.NormalizeAxis）：调用方给的可能还是"另一族"的值
            // （比如 `new Stroke{Kind=Hyperbola}` 的字段默认是 0），不归一会把框算歪。
            return CurveInkBoundsOf(kind, NormalizeAxis(kind, axis), transform, Matrix3x2.Identity,
                                    local[0], local[1], q2, width).Inflate(2f);
        }

        // 圆 / 椭圆（含带焦点的那种）：走参数化外接（里面已经含了半个笔宽，见 ParametricInkBoundsOf）。
        if (local.Count >= 2 && (kind == StrokeKind.Circle || IsSemiAxisEllipse(kind)))
            return ParametricInkBoundsOf(kind, transform, Matrix3x2.Identity,
                                         local[0], local[1], width).Inflate(2f);

        // 三角形 / 平行四边形：顶点各自过变换（含平行四边形现推的第四个顶点）。
        if (kind is StrokeKind.Triangle or StrokeKind.Parallelogram && local.Count >= 3)
            return PolygonInkBoundsOf(kind, transform, Matrix3x2.Identity,
                                      local[0], local[1], local[2], width).Inflate(2f);

        // 坐标系 / 数轴：定义点的外接 + 半笔宽（**没有刻度了，所以不用再往外多算一截**）。
        if (kind is StrokeKind.Coordinate or StrokeKind.NumberLine && local.Count >= 2)
        {
            int n = Math.Min(local.Count, 3);
            Span<Vector2> pts = stackalloc Vector2[3];
            for (int i = 0; i < n; i++) pts[i] = local[i];
            return AxisInkBoundsOf(pts[..n], transform, Matrix3x2.Identity, width).Inflate(2f);
        }

        var b = RectF.Empty;
        for (int i = 0; i < local.Count; i++) b.Add(local[i].X, local[i].Y);
        if (b.IsEmpty) return b;
        return TransformRect(b, transform).Inflate(width * 0.5f + 2f);
    }

    /// <summary>Bumped whenever the shape of this item changes. The cached GPU
    /// geometry is only trusted while it matches, which is what makes "add a
    /// point, redraw" work while a stroke is still being drawn.</summary>
    public int Revision { get; private set; }
    private int _builtRevision = -1;

    /// <summary>
    /// **辅助几何**的缓存（目前只有双曲线的两条虚线渐近线，见
    /// <see cref="BuildAuxGeometry"/>）。它是"一个对象、两段几何、两种线型"的落点：
    /// 主几何的线型/颜色是这个对象自己的（老师可能把它设成虚线了），
    /// 而渐近线**恒定是细虚线**，所以只能两段几何、两次描边。
    ///
    /// 它**不参与命中**（和坐标系网格同一条口径：辅助线点不中），
    /// 也**不进紧框**（画到 `±2a, ±2b` 为止，正好落在曲线自己的框里，见
    /// <see cref="HyperbolaAsymptoteLocal"/>）。
    /// </summary>
    public ID2D1Geometry Geometry2;
    private int _builtRevision2 = -1;

    /// <summary>
    /// 这个对象的**线宽跟不跟着缩放**（用户 2026-09-13 拍板、2026-09-20 落地）：
    ///
    ///   · **图形**（直线 / 矩形 / 椭圆 / 三角形 / 四种曲线 / 立体图形 / 坐标系…）→ **不跟着缩放**：
    ///     线宽是"这个图元自己的属性"，拉伸只是改形状。横着拉一个矩形，四条边还是原来那么粗
    ///     （否则横边粗、竖边不变，看着像书法笔）；
    ///   · **手写墨迹**（自由笔迹 / 荧光笔）与**图像** → **跟着缩放**（像图片一样）：
    ///     放大一段板书，笔迹当然要跟着变粗，不然就"糊"在放大的字里了。
    ///
    /// 落点在描边那一步：图形的几何**带变换**画（<see cref="BuildCanvasGeometry"/>），
    /// 于是描边发生在画布空间、宽度恒等于 <see cref="Width"/>。
    /// </summary>
    public bool KeepsWidth => Kind != StrokeKind.Freehand && Kind != StrokeKind.Image;

    /// <summary>
    /// **画布空间**几何（"线宽不变"那条路，见 <see cref="KeepsWidth"/>）。
    ///
    /// 为什么不能在描边前把 `Transform` 塞进 D2D 上下文：那样描边发生在**局部空间**，
    /// 变换作用在描边**之后** —— 线宽被一起缩放（非等比拉伸时"竖线比横线粗"）。
    /// 把变换折进几何里，描边就发生在画布空间，宽度就恒等于 `Width`、虚线的长短也不变。
    ///
    /// <paramref name="extra"/> 是拖动预览那种"额外再叠一层实时矩阵"的场合（见
    /// Overlay.DrawDragPreview）——注意它是**画布空间里再乘**，顺序在 `Transform` 之后。
    ///
    /// 缓存：局部几何按 <see cref="Revision"/>，这一层再记住**上次的矩阵**——
    /// 拖动时每帧矩阵都在变，所以每帧重建一次；一个图形几百个点，代价可忽略。
    /// 「手写墨迹」不走这条路（<see cref="KeepsWidth"/> 为 false 时直接返回局部几何），
    /// 所以"一次框选一万条笔迹去拉伸"不会在这里每帧重建一万次。
    /// </summary>
    public ID2D1Geometry BuildCanvasGeometry(ID2D1Factory1 factory, bool aux = false,
                                             Matrix3x2? extra = null)
    {
        var src = aux ? BuildAuxGeometry(factory) : BuildGeometry(factory);
        if (src == null) return null;
        if (!KeepsWidth) return src;

        var m = Transform * (extra ?? Matrix3x2.Identity);
        if (_canvasGeoRevision != Revision || _canvasGeoMatrix != m)
        {
            if (_canvasGeo != null) { _canvasGeo.Dispose(); _canvasGeo = null; LiveGeometries--; }
            if (_canvasGeoAux != null) { _canvasGeoAux.Dispose(); _canvasGeoAux = null; LiveGeometries--; }
            _canvasGeoRevision = Revision;
            _canvasGeoMatrix = m;
        }

        var slot = aux ? _canvasGeoAux : _canvasGeo;
        if (slot == null)
        {
            if (m.IsIdentity) return src;          // 单位变换：不用白包一层
            slot = factory.CreateTransformedGeometry(src, m);
            LiveGeometries++;
            if (aux) _canvasGeoAux = slot; else _canvasGeo = slot;
        }
        return slot;
    }

    private ID2D1Geometry _canvasGeo;
    private ID2D1Geometry _canvasGeoAux;
    private Matrix3x2 _canvasGeoMatrix = Matrix3x2.Identity;
    private int _canvasGeoRevision = -1;

    /// <summary>
    /// 颜色 / 粗细这类"不改几何、但改了墨迹范围"的属性变过之后调用。
    ///
    /// 为什么需要：<see cref="InkBounds"/> 按 <see cref="Revision"/> 缓存，
    /// 而 `Color` / `Width` 是裸字段——直接改它们缓存不会失效，于是**改完粗细，
    /// 选中框还按旧的半宽算**（框和墨对不上）。这里顺手也让几何缓存失效，
    /// 多一次重建，一次性代价可以接受。
    /// </summary>
    public void InvalidateMetrics() => Revision++;

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
        // 辅助几何（双曲线的虚线渐近线）也是缓存，同样要放——理由和下面那张位图一样：
        // 漏一处，"删掉之后内存不降"就是必然的。
        Geometry2?.Dispose();
        Geometry2 = null;
        // "线宽不变"那条路的画布空间几何（见 BuildCanvasGeometry）：同样是缓存，同样要放。
        if (_canvasGeo != null) { _canvasGeo.Dispose(); _canvasGeo = null; LiveGeometries--; }
        if (_canvasGeoAux != null) { _canvasGeoAux.Dispose(); _canvasGeoAux = null; LiveGeometries--; }
        _canvasGeoRevision = -1;
        // 图像对象还挂着一张 D2D 位图（可能很大：一张 800×600 的截图约 2MB）。
        // 漏掉这一句，"擦掉截图之后内存不降"就是必然的。释放之后再画会按需重建。
        Image?.Release();
        _builtRevision = -1;
        _builtRevision2 = -1;
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
            Dash = Dash,          // 线型也是"这一条的样子"，复制要跟着走
            Grid = Grid,          // 坐标系网格同理（它是对象自己的样子，不是全局设置）
            CurveAxis = CurveAxis,// 曲线朝向同理（双曲线哪条是实轴；抛物线现在是现推的）
            ShowAsymptotes = ShowAsymptotes,   // 双曲线画不画那两条虚线渐近线
            FocusTriangle = FocusTriangle,     // 椭圆（带焦点）画不画焦点三角形
            FocusPointU = FocusPointU,         // 焦点三角形的顶点 P 在椭圆上哪个位置（NaN = 还没定过）
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
    /// 参数收 <see cref="IReadOnlyList{T}"/>：调用方手里常常只有"只读的一组点"
    /// （例如浮动层画临时几何），没必要为了传参再复制一个数组。
    /// </summary>
    public void SetPoints(IReadOnlyList<Vector2> pts)
    {
        Points.Clear();
        for (int i = 0; i < pts.Count; i++)
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
    internal void RecomputeBounds()
    {
        Bounds = RectF.Empty;

        // 四种新曲线**不能直接用"控制点的外接"**当包围盒：
        //   · 双曲线的两支伸得比"中心 ↔ 外角点"远得多（两个方向都要伸到 2 倍），
        //     照控制点算出来的框会小掉一大圈——而这个框是空间索引（命中粗筛）的依据，
        //     小掉的后果是"曲线上有些地方点不中"；
        //   · 正弦 / 余弦的三个控制点里有一个是极值点，但**起点那一侧的半个周期**
        //     本来就不在控制点的外接里（起点在轴上，曲线却先往上跑）。
        // 所以这几档走"曲线自己的那个矩形"（和 <see cref="CurveBoxLocal"/> 同一份算式）。
        if (IsCurveKind(Kind) && Points.Count >= 2)
        {
            Bounds = CurveBoxLocal();
            return;
        }

        // **长方体**同理：它的第三条棱伸到"正面 ∪ 背面"，
        // 而背面左上 / 右下两个角里只有一个是控制点（第三个点存的是背面右下角），
        // 照控制点算出来的框会**少了背面那一角**（命中粗筛在那儿就点不中）。
        if (Kind == StrokeKind.Cuboid && Points.Count >= 2)
        {
            var (x0, y0, x1, y1) = CuboidFrontLocal();
            float d = CuboidDepthLocal();
            Bounds.Add(x0, y0);
            Bounds.Add(x1, y1);
            Bounds.Add(x0 + d, y0 - d);
            Bounds.Add(x1 + d, y1 - d);
            return;
        }

        // **球**：包围盒是**那个圆的外接方形**（圆心 ± 半径），不是拖出来的矩形——
        // 拖长了（400×200）的时候圆是内切的，照矩形算框会左右各空出一截。
        if (Kind == StrokeKind.Sphere && Points.Count >= 2)
        {
            var (sc, sr) = SphereLocal();
            Bounds.Add(sc.X - sr, sc.Y - sr);
            Bounds.Add(sc.X + sr, sc.Y + sr);
            return;
        }

        // **棱柱 / 棱锥 / 棱台**：包围盒取**真正的那些顶点**（底面 n 个 ∪ 顶面 n 个），
        // 不是"底面外接框 ∪ 框+侧棱"——后者会**偏大**：底面的正 n 边形并不填满外接框
        //（六棱柱的顶端只到 0.866·ry），于是选中框的上沿会空出一条缝
        //（用户 2026-09-20："外接矩形上面会漏一块"）。逐点算既准又不受错切影响。
        // ⚠ 顶面那一份要用 `PrismTopLocal()` 现算，不能拿"底面 ＋ 侧棱向量"——
        //   棱锥（缩成一点）和棱台（缩小一份）的顶面都不在那儿。
        if (IsPrismFamily(Kind) && Points.Count >= 2)
        {
            foreach (var v in PrismBaseLocal()) Bounds.Add(v.X, v.Y);
            if (Points.Count >= 3)
                foreach (var v in PrismTopLocal()) Bounds.Add(v.X, v.Y);
            return;
        }

        foreach (var p in Points) Bounds.Add(p.X, p.Y);
    }

    /// <summary>
    /// 脏区与命中测试用的外扩包围盒（**画布坐标**）。
    /// 在墨迹范围基础上再留 2 像素余量——抗锯齿的边缘会跑出精确包围盒一点点，
    /// 漏了就会在屏幕上留一条发丝一样的残影。
    ///
    /// **两种口径**（和 <see cref="WorldInkBounds"/> 对齐，别让它俩走岔）：
    ///   · **直线 / 箭头**：`墨迹框 + 2`。它们的墨迹框已经含了半个笔宽（圆头线帽的半径），
    ///     旧写法 `WorldBounds.Inflate(半宽 + 2)` 里的 `WorldBounds` 是"局部中心线框转过去
    ///     再取外接"，对转过的直线同样虚胖——脏区会白重画一大片、命中也会连带变松。
    ///   · **其余种类**：照旧 `WorldBounds.Inflate(半宽 + 2)`，**一个数都不改**。
    ///
    /// 外扩量 = 半个笔宽（+2 像素余量）。**2026-09-14 起这个数是精确的**：
    /// 墨迹现在是"中心线 + 等宽描边 + 圆头圆角"，离中心线最远就是半个笔宽，
    /// 而端帽/拐角都是半径 = 半宽 的圆弧，不会再多伸出去。
    /// （以前自己拼轮廓、内角要补到两条内边的交点，最远能到好几倍半宽，
    /// 那时这个系数必须留得很大——现在就按事实来。）
    /// </summary>
    public RectF PaddedBounds => IsParametricShape
        ? WorldInkBounds.Inflate(2f)
        : WorldBounds.Inflate(MaxHalfWidth + 2f);

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

            // **长方体 / 棱柱一族 / 球**是仅有的几个例外：它们的墨迹伸到"控制点的外接"之外
            //（球反过来——是**控制在画出来的东西之外**，所以照控制点算框会虚胖）——
            //   · 长方体：背面左上 / 右下两个角里只有一个是控制点；
            //   · 棱柱一族：第三个控制点是**顶面中心**，而顶面那一圈是从"底面 ＋ 顶上那个中心"
            //     算出来的，**整个在控制点外面**（照控制点算出来的框，上沿会少掉一大块：
            //     用户 2026-09-20 说的"外接矩形上面会漏一块"）；
            //   · 球：拖长了的时候圆是内切的，控制点（矩形）比圆大。
            // 这几族的 `Bounds` 都已经按**真实几何**算过了（见 RecomputeBounds）——直接用"它 ＋ 半笔宽"。
            if ((Kind == StrokeKind.Cuboid || Kind == StrokeKind.Sphere || IsPrismFamily(Kind))
                && Points.Count >= 2)
            {
                float hw0 = Width * 0.5f;
                var rr = Bounds;
                rr.MinX -= hw0; rr.MinY -= hw0; rr.MaxX += hw0; rr.MaxY += hw0;
                _inkBounds = rr;
                _maxHalfWidth = hw0;
                _inkBoundsRevision = Revision;
                return rr;
            }

            var r = RectF.Empty;
            // **初值必须是 0，不能是"标称半宽"**：有压感的笔迹可能比标称**细**
            // （满压才等于标称），初值取标称的话这些笔迹会一律报标称值——
            // 症状是脏区偏大、细笔迹的命中范围偏宽（2026-09-20 `--pressuretest` 抓到的：
            // "轻写那条按它自己的最粗处算" 那一条报出 6.00 vs 6.00）。
            float maxHalf = 0f;
            for (int i = 0; i < Points.Count; i++)
            {
                // 有压感的笔迹**逐点各算各的半宽**（宽的地方要算进去，否则紧框框不住墨）。
                // 图像对象 Width = 0、也没有压感，所以这里算出来就是它自己的矩形。
                float hw = HalfWidthAt(i);
                if (hw > maxHalf) maxHalf = hw;
                r.Add(Points[i].X - hw, Points[i].Y - hw);
                r.Add(Points[i].X + hw, Points[i].Y + hw);
            }
            _inkBounds = r;
            _maxHalfWidth = maxHalf > 0f ? maxHalf : Width * 0.5f;   // 没有点时退回标称
            _inkBoundsRevision = Revision;
            return r;
        }
    }

    /// <summary>
    /// 第 <paramref name="i"/> 个采样点处的**半宽**（局部像素）。
    ///
    /// 没压感（或压感关掉）时恒等于 <c>Width / 2</c>——也就是 2026-09-14 以来那条等宽口径，
    /// 所以"没有压感的设备"走的还是原来那条路，一个像素都不变。
    /// </summary>
    public float HalfWidthAt(int i)
    {
        float hw = Width * 0.5f;
        if (!HasPressure || !PressureWidth.Enabled) return hw;
        if (i < 0 || i >= Points.Count) return hw;
        return PressureWidth.HalfWidth(Width, Points[i].P);
    }

    private float _maxHalfWidth = -1f;

    /// <summary>
    /// 这一笔**最粗那一处**的半宽（局部像素）——紧框、命中、两种橡皮都用它。
    ///
    /// 为什么不继续用 <c>Width / 2</c>：重压的地方比标称宽 50%，按标称算就会
    /// "看得见却点不中"（老师的原话会是"这一笔选不上"），脏区也会漏掉最粗的那一圈。
    /// 和 <see cref="InkBounds"/> 共用同一档缓存，每帧问它不心疼。
    /// </summary>
    public float MaxHalfWidth
    {
        get
        {
            _ = InkBounds;                      // 顺带把 _maxHalfWidth 算出来（同一趟循环）
            return _maxHalfWidth >= 0f ? _maxHalfWidth : Width * 0.5f;
        }
    }

    /// <summary>
    /// 是不是"由**两个端点**定义、中间是空的"那两种图形（直线 / 箭头）。
    ///
    /// 这一个判断现在管着"墨迹范围"那条特殊口径（见 <see cref="LineLikeInkBounds"/>）：
    /// 直线/箭头的局部 AABB 四个角**根本不在线上**，所以"先把框转过去再取外接矩形"会虚胖。
    /// </summary>
    internal bool IsLineLike => Kind is StrokeKind.Line or StrokeKind.Arrow;

    /// <summary>
    /// 是不是"由**参数**定义的图形"（端点 / 圆心半径 / 中心半轴）。
    ///
    /// 这一类对象的局部 AABB **不是**它自己的边界（直线斜着时角不在线上、圆的 AABB
    /// 四角在圆外、三角形的外接框有一半是空的），所以墨迹框一律按参数算（见
    /// <see cref="LineLikeInkBounds(Vector2, Vector2, in Matrix3x2)"/>、
    /// <see cref="ParametricInkBounds"/> 与 <see cref="PolygonInkBounds(Vector2, Vector2, Vector2, in Matrix3x2)"/>），
    /// **不能**用"把局部框整体转过去再取外接"那一套（2026-09-18/19 两轮踩过）。
    /// 自由笔迹 / 矩形 / 图像的局部 AABB 就是它们自己的边界，走老口径。
    /// </summary>
    internal bool IsParametricShape => IsShapeKind(Kind);

    /// <summary>
    /// 这几种是**参数化曲线**（抛物线 / 双曲线 / 正弦 / 余弦 / 波浪线 / 正切）：它们的墨迹框**不能**
    /// 用"控制点的外接"来算（理由见 <see cref="CurveBoxOf"/>）。
    ///
    /// **2026-09-20 收敛**：这句话原来在**六个地方各手写了一遍**（紧框 / 重算包围盒 /
    /// 世界墨迹框 / 预览墨迹框 / 是不是参数化图形 / 能不能编辑），加第五种曲线就得同时改六处，
    /// 漏一处就是"框算小了、曲线上有些地方点不中"（椭圆当年就这么错过）。现在只有这一份。
    /// </summary>
    public static bool IsCurveKind(StrokeKind kind)
        => kind is StrokeKind.Parabola or StrokeKind.Hyperbola
                or StrokeKind.Sine or StrokeKind.Cosine or StrokeKind.Wave
                or StrokeKind.Tangent;

    /// <summary>
    /// 这几种是**图形**（相对于自由笔迹 / 图像）：用"定义元素"描述、选中后能拖手柄改参数。
    ///
    /// **2026-09-20 收敛**：同一份名单原来写了三遍——这份（按 Kind）、
    /// <see cref="SelectionHandles.ShapeEditable"/>（按 Kind，现在转发到这里）、
    /// 以及引擎里的 `IsShapeTool`（**按 Tool**，判的是"这个工具画出的是图形还是自由笔迹"，
    /// 维度不同，那份留着，两边靠自检卡一致）。
    ///
    /// 立体图形（圆柱 / 圆锥）也算：它们同样是"两个控制点定形"，选中的框、移动、旋转、
    /// 存档都走图形那一套（`--shapebandtest` 的"名单一致"那条会卡住）。
    /// </summary>
    public static bool IsShapeKind(StrokeKind kind)
        => kind is StrokeKind.Line or StrokeKind.Arrow or StrokeKind.Circle or StrokeKind.Ellipse
                or StrokeKind.ConicEllipse
                or StrokeKind.Triangle or StrokeKind.Parallelogram
                or StrokeKind.Coordinate or StrokeKind.NumberLine
                or StrokeKind.Cylinder or StrokeKind.Cone or StrokeKind.ConeFrustum
                or StrokeKind.Sphere
                or StrokeKind.Cuboid or StrokeKind.Tetrahedron
                or StrokeKind.Prism or StrokeKind.Pyramid or StrokeKind.Frustum
           || IsCurveKind(kind);

    /// <summary>
    /// **"中心 ＋ 两个半轴"定义的那两种椭圆**（<see cref="StrokeKind.Ellipse"/> 与
    /// <see cref="StrokeKind.ConicEllipse"/>）——几何、紧框、两个半轴手柄、存档
    /// **全部共用一份**，差别只有"带不带焦点"。
    ///
    /// **为什么要有这一个判据函数**：仓库里"圆 / 椭圆"出现过的地方有六七处
    /// （紧框两处、轮廓折线、几何构建、手柄、姿态角读数……），加一种椭圆要是逐处去补
    /// `or StrokeKind.ConicEllipse`，那就是"同一个名单写多处"的老毛病——
    /// 漏一处的表现是"新椭圆画得出来，但点不中 / 紧框不对"（这种最难查）。
    /// 所以凡是"按这一族分支"的地方都问它。
    /// </summary>
    public static bool IsSemiAxisEllipse(StrokeKind kind)
        => kind is StrokeKind.Ellipse or StrokeKind.ConicEllipse;

    /// <summary>
    /// **棱柱 / 棱锥 / 棱台这一族**吗：底面都是那个正 n 边形、控制点都是"底面外接框两角 ＋
    /// 顶上那个中心"，所以几何、实虚判据、包围盒、档位、直／斜吸附**全是同一套**
    /// （见 <see cref="PrismBaseLocal"/> / <see cref="PrismTopLocal"/> / <see cref="PrismFamilyEdges"/>）。
    ///
    /// **为什么要有这一个判据函数**：这一族凡是"按种类分支"的地方都该问它，不该各写一份名单——
    /// 写三处就得改三处，漏一处就是"加了一种图形、某一处还按老样子算"（仓库里为这类事
    /// 栽过好几次，见 计划-图形工具.md 的教训那几条）。
    /// </summary>
    public static bool IsPrismFamily(StrokeKind kind)
        => kind is StrokeKind.Prism or StrokeKind.Pyramid or StrokeKind.Frustum;

    /// <summary>
    /// **旋转体那一族**吗（圆柱 / 圆锥 / 圆台 / 球）：四个**都是一次拖出外接矩形、一笔画完**，
    /// 几何全由那两个角派生（见 <see cref="SolidRectLocal"/>），而且被挡住的部分都是
    /// "远侧那一圈/半圈"（见 <see cref="SolidPieces"/>）。
    ///
    /// **为什么要有这个判据**：2026-09-20 加圆台和球时，"圆柱 / 圆锥"这两个名字
    /// 又散在了好几处（`Engine.UpdateShapePreview` 的逐帧写法、各处的 case 组）——
    /// **加一种旋转体就要挨个补**。凡是"按这一族分支"的地方都该问它
    /// （和 <see cref="IsPrismFamily"/> 是同一条规矩：名单只写一处）。
    /// </summary>
    public static bool IsRevolutionSolid(StrokeKind kind)
        => kind is StrokeKind.Cylinder or StrokeKind.Cone
                or StrokeKind.ConeFrustum or StrokeKind.Sphere;

    /// <summary>圆的**圆心** / 椭圆的**中心**（局部坐标，= 第一个控制点）。</summary>
    public Vector2 ShapeCenterLocal
    {
        get
        {
            var (c, _) = Endpoints();
            return new Vector2(c.X, c.Y);
        }
    }

    /// <summary>圆的**半径**（局部坐标）：圆心到圆周点的距离。</summary>
    public float CircleRadiusLocal
    {
        get
        {
            var (c, r) = Endpoints();
            return MathF.Sqrt((r.X - c.X) * (r.X - c.X) + (r.Y - c.Y) * (r.Y - c.Y));
        }
    }

    /// <summary>椭圆的**横半轴 a**（局部坐标，恒非负）：中心到外角点的横向距离。</summary>
    public float SemiAxisALocal
    {
        get
        {
            var (c, e) = Endpoints();
            return MathF.Abs(e.X - c.X);
        }
    }

    /// <summary>椭圆的**纵半轴 b**（局部坐标，恒非负）。</summary>
    public float SemiAxisBLocal
    {
        get
        {
            var (c, e) = Endpoints();
            return MathF.Abs(e.Y - c.Y);
        }
    }


    /// <summary>
    /// 墨迹包围盒的**画布坐标**版本。
    ///
    /// **两种口径**，按对象是怎么定义的分：
    ///   · **直线 / 箭头**：两个端点（含箭头翅膀尖）变换到画布空间后取外接矩形，再外扩半笔宽。
    ///     它们"由两个点定义"，局部 AABB 的四个角不在线上——旧口径
    ///     （`TransformRect(InkBounds, Transform)` = 框角整体转过去再取外接）对一条
    ///     转过 30° 的直线会把框撑到**接近两倍宽**（实测 962 vs 线自己 490，见 --shapetooltest）。
    ///   · **其余种类**（椭圆 / 圆 / 三角形 / 平行四边形 / 矩形 / 图像 / 自由笔迹）：
    ///     照旧"把局部墨迹框过一遍变换"——但**参数化图形**（前三类）走的也是自己那条
    ///     按定义算的路（见下面几行），只有矩形 / 图像 / 自由笔迹的局部 AABB
    ///     就是它们自己的边界，用这一条才是对的。
    /// </summary>
    public RectF WorldInkBounds
    {
        get
        {
            // **参数化图形一律自己算**，连"变换是单位阵"这一档也不例外：
            // 圆的墨迹圈是"圆心 ± r"，而它的两个控制点（圆心 + 圆周点）的 AABB 只是一条
            // 从圆心伸出去的细条（2026-09-19 出图核对时踩到：紧框算成 256×16）。
            // 这三条都是 O(1)，短路省不了什么。
            if (IsLineLike) return LineLikeInkBounds(Matrix3x2.Identity);
            if (Kind == StrokeKind.Circle || IsSemiAxisEllipse(Kind))
                return ParametricInkBounds(ShapeCenterLocal, RimLocalPoint(), Matrix3x2.Identity);
            if (Kind is StrokeKind.Triangle or StrokeKind.Parallelogram && Points.Count >= 3)
                return PolygonInkBounds(new Vector2(Points[0].X, Points[0].Y),
                                        new Vector2(Points[1].X, Points[1].Y),
                                        new Vector2(Points[2].X, Points[2].Y),
                                        Matrix3x2.Identity);
            // 坐标系 / 数轴：四个定义元素的外接 + **刻度半长**（刻度是唯一伸出外框的东西）。
            if (Kind is StrokeKind.Coordinate or StrokeKind.NumberLine && Points.Count >= 4)
                return AxisInkBounds(Matrix3x2.Identity);

            // 四种曲线：**曲线自己的那个矩形** ＋ 半笔宽。
            // 这一条**不能省**（省了就落到最后那条"点的外接过变换"上）：
            // 双曲线的两支会伸出"中心 ↔ 外角点"很远，正弦会漏掉起点那一侧的半个周期，
            // 而紧框是脏区 / 命中粗筛 / 导出裁切的依据（见 CurveBoxOf 的注释）。
            if (IsCurveKind(Kind) && Points.Count >= 2)
                return CurveInkBoundsOf(Kind, EffectiveAxis, Transform, Matrix3x2.Identity,
                                        CurvePointLocal(0), CurvePointLocal(1), CurvePointLocal(2), Width);

            var r = InkBounds;
            if (r.IsEmpty) return r;
            if (Transform.IsIdentity) return r;
            return TransformRect(r, Transform);
        }
    }

    /// <summary>第二个控制点（局部坐标）：直线的终点 / 圆的圆周点 / 椭圆的外角点。</summary>
    public Vector2 RimLocalPoint()
    {
        var (_, b) = Endpoints();
        return new Vector2(b.X, b.Y);
    }

    /// <summary>
    /// 圆 / 椭圆的**画布空间墨迹框**（参数化外接 + 半笔宽）。
    ///
    /// 为什么不能沿用"把局部框整体转过去再取外接"：椭圆局部是 `(a·cos t, b·sin t)`，
    /// 它的 AABB 是"中心 ±(a,b)"那个矩形；把**那个矩形**转 30° 再取外接，
    /// 得到的框比椭圆本身大一圈（和直线那轮的 962 vs 490 是同一个毛病）。
    ///
    /// 正确的外接（对任意仿射变换都成立，含镜像 / 缩放）：
    ///   x 的极值 = √((a·M11)² + (b·M21)²)、y 的极值 = √((a·M12)² + (b·M22)²)
    /// —— 因为 `A·cos t + B·sin t` 的振幅就是 `√(A² + B²)`。
    /// 旋转 θ 时代进去正是规格 9.3 写的 `√((a·cosθ)² + (b·sinθ)²)` / `√((a·sinθ)² + (b·cosθ)²)`：
    /// 是同一个东西，这样写连镜像、缩放都不用特判。
    ///
    /// 圆就是 `a = b = r` 的特例，所以两者共用一个函数（圆的紧框 = `圆心 ± r`）。
    /// <paramref name="extra"/> 与直线那边同一个用途：拖动预览要叠一层实时矩阵。
    /// </summary>
    public RectF ParametricInkBounds(Vector2 centerLocal, Vector2 rimLocal, in Matrix3x2 extra)
        => ParametricInkBoundsOf(Kind, Transform, extra, centerLocal, rimLocal, Width);

    /// <summary>
    /// 上面那条式子的**静态核心**（不读实例字段：种类 / 变换 / 笔宽都由调用方给）。
    ///
    /// 抽出来的理由：改几何的动作要在**动手之前**算"改完占哪块"（见
    /// <see cref="PaddedBoundsOf"/>），而那时手里只有一组点，没有对象。
    /// 同一个式子写两份的下场，2026-09-19 已经实测过一次：脏区那份漏掉了圆 / 椭圆的
    /// 参数化那一档，于是"拖轴端点松手之后形体缺一块"。
    /// </summary>
    private static RectF ParametricInkBoundsOf(StrokeKind kind, in Matrix3x2 transform, in Matrix3x2 extra,
                                               Vector2 centerLocal, Vector2 rimLocal, float width)
    {
        var m = transform * extra;
        float dx = rimLocal.X - centerLocal.X, dy = rimLocal.Y - centerLocal.Y;
        float a = kind == StrokeKind.Circle
            ? MathF.Sqrt(dx * dx + dy * dy)      // 圆：a = b = 半径
            : MathF.Abs(dx);
        float b = kind == StrokeKind.Circle
            ? a
            : MathF.Abs(dy);

        float hx = MathF.Sqrt((a * m.M11) * (a * m.M11) + (b * m.M21) * (b * m.M21));
        float hy = MathF.Sqrt((a * m.M12) * (a * m.M12) + (b * m.M22) * (b * m.M22));
        var c = Vector2.Transform(centerLocal, m);
        var r2 = new RectF
        {
            MinX = c.X - hx, MinY = c.Y - hy,
            MaxX = c.X + hx, MaxY = c.Y + hy,
        };
        return r2.Inflate(width * 0.5f);
    }

    /// <summary>
    /// 三角形 / 平行四边形的**画布空间墨迹框**：**各个顶点**过变换后取外接 ＋ 半笔宽。
    ///
    /// 为什么不能沿用"把局部框整体转过去再取外接"（H 段那条老账）：三角形的局部 AABB
    /// 有一半是空的（斜边那一侧的角根本不在三角形上），转过去再取外接会明显虚胖，
    /// 而这个框是**脏区、命中粗筛、导出裁切**的依据——虚胖就是每帧白重画。
    /// 平行四边形的第四个顶点不在控制点表里，这里现推（<see cref="ParallelogramFourth"/>）。
    ///
    /// <paramref name="extra"/> 与另外两处同一个用途：拖动预览要再叠一层实时矩阵。
    /// </summary>
    public RectF PolygonInkBounds(Vector2 p0, Vector2 p1, Vector2 p2, in Matrix3x2 extra)
        => PolygonInkBoundsOf(Kind, Transform, extra, p0, p1, p2, Width);

    /// <summary>
    /// 上面那条式子的**静态核心**，理由和 <see cref="ParametricInkBoundsOf"/> 一样：
    /// 改几何的动作要在动手之前按"一组点"算包围盒（见 <see cref="PaddedBoundsOf"/>）。
    /// </summary>
    private static RectF PolygonInkBoundsOf(StrokeKind kind, in Matrix3x2 transform, in Matrix3x2 extra,
                                            Vector2 p0, Vector2 p1, Vector2 p2, float width)
    {
        var m = transform * extra;
        var r = RectF.Empty;
        var q0 = Vector2.Transform(p0, m); r.Add(q0.X, q0.Y);
        var q1 = Vector2.Transform(p1, m); r.Add(q1.X, q1.Y);
        var q2 = Vector2.Transform(p2, m); r.Add(q2.X, q2.Y);
        if (kind == StrokeKind.Parallelogram)
        {
            var q3 = Vector2.Transform(ParallelogramFourth(p0, p1, p2), m);
            r.Add(q3.X, q3.Y);
        }
        return r.Inflate(width * 0.5f);
    }

    /// <summary>
    /// 用**临时控制点**算墨迹紧框——手势期（模型还没动）的选中框与脏区都按它算。
    ///
    /// 存在的理由：<see cref="WorldInkBounds"/> 读的是模型里的点，而拖动预览那几个点
    /// 只在 <see cref="Engine"/> 的预览数组里（见 计划-图形工具.md 8.1①）。
    /// 口径必须和静止态**一模一样**，否则拖动中每边差几个像素、松手那一瞬框会跳一下。
    /// 三档按对象是"怎么定义的"分流，和 <see cref="WorldInkBounds"/> 那张表一一对应。
    /// </summary>
    public RectF PreviewInkBounds(IReadOnlyList<Vector2> local, in Matrix3x2 extra)
    {
        if (local == null || local.Count < 2) return RectF.Empty;
        if (IsLineLike) return LineLikeInkBounds(local[0], local[^1], extra);
        if (Kind == StrokeKind.Circle || IsSemiAxisEllipse(Kind))
            return ParametricInkBounds(local[0], local[1], extra);
        // 曲线：除了点表还要知道"朝向"，所以转给专门那个入口（<see cref="CurvePreviewInkBounds"/>），
        // 口径和静止态、脏区那两份**完全同源**。
        if (IsCurveKind(Kind))
            return CurvePreviewInkBounds(local, extra);
        if (Kind is StrokeKind.Triangle or StrokeKind.Parallelogram && local.Count >= 3)
            return PolygonInkBounds(local[0], local[1], local[2], extra);
        // 坐标系 / 数轴：和静止态同一条式子（定义点的外接 + 半笔宽），口径不能差一分。
        if (Kind is StrokeKind.Coordinate or StrokeKind.NumberLine && local.Count >= 2)
        {
            int n = Math.Min(local.Count, 3);
            Span<Vector2> pts = stackalloc Vector2[3];
            for (int i = 0; i < n; i++) pts[i] = local[i];
            return AxisInkBoundsOf(pts[..n], Transform, extra, Width);
        }

        var b = RectF.Empty;
        for (int i = 0; i < local.Count; i++) b.Add(local[i].X, local[i].Y);
        // 半宽按**最粗处**算（有压感的那一笔，重压的地方比标称宽 50%）。
        return b.IsEmpty ? b : TransformRect(b, Transform * extra).Inflate(MaxHalfWidth);
    }

    /// <summary>
    /// 曲线（抛物线 / 双曲线 / 正弦 / 余弦）的**临时几何**包围盒：
    /// 拖手柄 / 拖控制点时模型还没动，框必须按预览点算，口径和静止态一字不差。
    ///
    /// 单独一个入口、不塞进 <see cref="PreviewInkBounds"/>，是因为那条路按"点表"分流，
    /// 而曲线这里要多传一个 <see cref="CurveAxis"/>（朝向）——两者签名对不上。
    /// </summary>
    public RectF CurvePreviewInkBounds(IReadOnlyList<Vector2> local, in Matrix3x2 extra)
    {
        if (local == null || local.Count < 2) return RectF.Empty;
        var q2 = local.Count >= 3 ? local[2] : Vector2.Zero;
        return CurveInkBoundsOf(Kind, EffectiveAxis, Transform, extra, local[0], local[1], q2, Width);
    }

    /// <summary>
    /// 直线 / 箭头的**画布空间墨迹框**：端点（+ 箭头翅膀尖）过变换之后取 min/max，
    /// 再外扩 `Width * 0.5`（圆头线帽的半径——这是"墨迹"该有的口径）。
    ///
    /// <paramref name="extra"/> 是"再叠一层"的矩阵：拖动预览（移动/旋转手势）里，
    /// 屏幕上的姿态是"对象自己的变换 × 实时矩阵"，框必须按**同一个式子**算才贴得住
    /// （否则一条转 30° 的直线，框还是会按"把紧框整体转过去再取外接"撑成两倍宽）。
    ///
    /// 三个细节：
    ///   · 端点是取 `Points` 的**首尾两个**（不写死下标，形状万一多几个点也成立）；
    ///   · **箭头**要把头部的两个翅膀尖也算进来：它们不在两个端点之间，只按端点算会让
    ///     翅膀落在框外，而框又是脏区/命中/导出裁切的依据（漏了就是残影）；
    ///   · 外扩在**画布空间**做（不是先外扩再过变换）：描边宽度不随变换缩放，
    ///     这也是命中测试那边的既有近似（见 HitTestExact 的注释）。
    /// </summary>
    public RectF LineLikeInkBounds(in Matrix3x2 extra)
    {
        var (a, b) = Endpoints();
        return LineLikeInkBounds(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), extra);
    }

    /// <summary>
    /// 同上，但端点由调用方给（**局部坐标**）。
    ///
    /// 存在的理由只有一个：**拖端点的手势期模型还没动**，框必须按"临时几何"的两个端点算。
    /// 三种情况的框（静止 / 拖动预览 / 拖端点预览）因此共用同一个式子，也就不会出现
    /// "拖的时候每边多 2 像素、松手缩一下"那种口径不一致（2026-09-19）。
    /// 箭头按**传进来的端点**重算头部：预览改了端点，屏幕上那两条翅膀也跟着变，
    /// 框要是还按模型里的旧翅膀算，就会和画出来的箭头对不上。
    /// </summary>
    public RectF LineLikeInkBounds(Vector2 a, Vector2 b, in Matrix3x2 extra)
    {
        var m = Transform * extra;
        var r = RectF.Empty;
        void Add(Vector2 p) => r.Add(p.X, p.Y);
        Add(Vector2.Transform(a, m));
        Add(Vector2.Transform(b, m));
        if (Kind == StrokeKind.Arrow)
        {
            var (_, wingA, wingB) = ArrowHeadPoints(a, b);
            Add(Vector2.Transform(wingA, m));
            Add(Vector2.Transform(wingB, m));
        }
        return r.Inflate(Width * 0.5f);
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
    ///
    /// **线型（虚线/点线）刻意不参与这里**（下面照样传 `Gfx.Round`）：
    /// 虚线中间是**空的**，真按虚线去判，老师点在一段空白上就会"点不中"——
    /// 而他心里点的就是"那条虚线"。所以命中一律按**实线**算，虚线整条都能点中。
    /// </summary>
    public bool HitTestExact(float canvasX, float canvasY, float tolerance = 0f)
    {
        // **"线宽不变"的对象**（图形，见 KeepsWidth）：几何本身就带变换（画布空间），
        // 所以查询点**不用**反变换，线宽也就是画布单位里的 Width —— 和屏幕上看到的
        // 一致。不这么走的话，拉伸过的图形会出现"看得见却点不中"（局部空间里量的线宽
        // 和画出来的不是一回事）。
        bool canvasSpace = KeepsWidth;
        var geo = canvasSpace ? BuildCanvasGeometry(Gfx.D2DFactory) : BuildGeometry(Gfx.D2DFactory);
        if (geo == null) return false;

        // 几何存在局部坐标里，把查询点反变换回去再测——等价于"带着变换去测"，
        // 但只用最简单的重载，少一层踩坑的机会。
        Vector2 p = new(canvasX, canvasY);
        if (!canvasSpace && !Transform.IsIdentity)
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
        // 线宽用**最粗处**（<see cref="MaxHalfWidth"/> × 2）：有压感的笔迹重压处比标称宽 50%，
        // 按标称算就会出现"看得见却点不中"。
        return geo.StrokeContainsPoint(p, MathF.Max(1f, MaxHalfWidth * 2f) + tolerance * 2f, Gfx.Round);
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
    /// 圆 / 椭圆的折线近似段数：**按尺寸定**（每段弦长 ≈ 16 像素），夹在 [24, 128]。
    ///
    /// 固定 32 段对大圆不够圆（半径 400 时一段弦长 78 像素，肉眼就是折线），对小圆又是浪费。
    /// 这条折线只服务"橡皮 / 框选 / 套索判交"，渲染那边走 D2D 原生几何，不受它影响。
    /// </summary>
    private static int EllipseSegments(float radius)
        => Math.Clamp((int)MathF.Ceiling(radius * MathF.Tau / 16f), 24, 128);

    /// <summary>
    /// 图形的**轮廓折线**（局部坐标，首尾相接，自己闭合）。
    ///
    /// 图形不是"点列"，它的轮廓由**定义元素**推出来（见下面一排 Build*）。
    /// 渲染和命中测试都交给 Direct2D，但**"这一笔和一块矩形有没有碰上"不能用包围盒回答**
    /// ——一个画得很大的圆，它的外框矩形中间是空的，用外框判就会"橡皮从圆心里
    /// 划过，整个圆没了"。所以这里按同一套规则把它展开成折线，逐段判交。
    ///
    /// 规则必须和 Build* 保持一致：改了一边就要改另一边（本函数只服务像素橡皮 /
    /// 套索，所以用折线近似；圆和椭圆的段数按尺寸定，见 <see cref="EllipseSegments"/>，
    /// 误差远小于一个笔宽）。
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
            case StrokeKind.ConicEllipse:
            {
                // 中心 ＋ 半轴（2026-09-19 改；以前是"外框两个对角点"）
                var c = ShapeCenterLocal;
                float rx = MathF.Max(0.5f, SemiAxisALocal);
                float ry = MathF.Max(0.5f, SemiAxisBLocal);
                int n = EllipseSegments(MathF.Max(rx, ry));
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n * MathF.Tau;
                    list.Add(new Vector2(c.X + MathF.Cos(t) * rx, c.Y + MathF.Sin(t) * ry));
                }

                // **带焦点的那种**（2026-09-22）多出三样，都得列进这份折线：
                // 两个焦点 ＋（有档时）焦点三角形的两条边。理由是这一份折线的用途——
                // 像素橡皮 / 套索判"碰到没有"（见 ShapeTouchesRect）：
                // 漏掉谁的后果就是"这两条边明明画着、rub 过去擦不掉"，
                // 而焦点坐在椭圆**里面**，不从这儿单独列一笔，橡皮根本够不着它。
                if (Kind != StrokeKind.ConicEllipse) break;

                var (f1, f2) = ConicEllipseFociLocal();
                if (FocusTriangle)
                {
                    var p = ConicEllipsePointLocal();
                    list.Add(OutlineBreak);        // 抬笔：F1→P、F2→P 是两笔，不能接成一条来回线
                    list.Add(f1);
                    list.Add(p);
                    list.Add(OutlineBreak);
                    list.Add(f2);
                    list.Add(p);
                }
                // 两个焦点：屏幕上是个**小圆点**，折线这里用一小段近似
                // （只要"橡皮 / 套索够得着"，形状不参与渲染，见 BuildConicEllipse）。
                list.Add(OutlineBreak);
                list.Add(f1);
                list.Add(new Vector2(f1.X + FocusDotRadius, f1.Y));
                list.Add(OutlineBreak);
                list.Add(f2);
                list.Add(new Vector2(f2.X + FocusDotRadius, f2.Y));
                break;
            }

            case StrokeKind.Circle:
            {
                var c = ShapeCenterLocal;
                float r = MathF.Max(0.5f, CircleRadiusLocal);
                int n = EllipseSegments(r);
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n * MathF.Tau;
                    list.Add(new Vector2(c.X + MathF.Cos(t) * r, c.Y + MathF.Sin(t) * r));
                }
                break;
            }

            case StrokeKind.Arrow:
            {
                var (_, wingA, wingB) = ArrowHeadPoints();
                list.Add(pa);
                list.Add(pb);
                list.Add(wingA);
                list.Add(pb);
                list.Add(wingB);
                break;
            }

            case StrokeKind.Triangle:
            {
                // 三个顶点直接就是轮廓：上中 → 下左 → 下右 → 回到上中（闭合）。
                if (Points.Count < 3) break;
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                list.Add(new Vector2(Points[1].X, Points[1].Y));
                list.Add(new Vector2(Points[2].X, Points[2].Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Parallelogram:
            {
                // 底左 → 底右 → **推导出来的顶右** → 顶左 → 回到底左。
                if (Points.Count < 3) break;
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                list.Add(new Vector2(Points[1].X, Points[1].Y));
                list.Add(ParallelogramFourthLocal());
                list.Add(new Vector2(Points[2].X, Points[2].Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
            {
                // 坐标系 / 数轴：折线这里只列**轴线**（坐标系两条、数轴一条）。
                // 网格刻意不列：这个折线只服务像素橡皮与套索的"碰到没有"，
                // "碰到一根网格线"本来就不该算碰到这个坐标系——
                // 真正决定用户能不能点中它的是 HitTestExact（那条走的是完整几何）。
                if (Points.Count < 2) break;
                var (minX, minY, maxX, maxY) = AxisFrameLocal();
                var o = AxisOriginLocal();
                if (Kind == StrokeKind.NumberLine)
                {
                    list.Add(new Vector2(minX, Points[0].Y));
                    list.Add(new Vector2(maxX, Points[0].Y));
                    break;
                }
                list.Add(new Vector2(minX, o.Y));
                list.Add(new Vector2(maxX, o.Y));
                list.Add(new Vector2(o.X, minY));
                list.Add(new Vector2(o.X, maxY));
                break;
            }

            case StrokeKind.Parabola:
            {
                // 抛物线：一条开折线，t 从 −Span 走到 +Span（**和 BuildParabola 同一份算式**），
                // Span 由"经过点"定（画到老师拖到的那个点为止，见 ParabolaSpanOf）。
                if (Points.Count < 2) break;
                float span = ParabolaSpanLocal();
                var box = CurveBoxLocal();
                int n = CurveSegments(MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY));
                for (int i = 0; i <= n; i++)
                    list.Add(ParabolaPointAt(-span + 2f * span * i / n));
                break;
            }

            case StrokeKind.Hyperbola:
            {
                // 双曲线：**两支**。这里有一个必须处理的坑——
                // 这份折线是被"逐段判交 / 逐点判圈"消费的（见 ShapeTouchesRect、
                // AppendRepresentativePoints），**两支之间直接接过去就会凭空多出一条
                // 横穿整个包围盒的长线段**（它正好过中心）：橡皮从曲线中间那段空白
                // 划一下，整条双曲线就被删掉了——正是当年"橡皮从圆心里划过、整个圆没了"
                // 那个 bug 的翻版。
                // 破法：两支之间放一个**抬笔标记**（见 <see cref="OutlineBreak"/>），
                // 消费那三处各自跳过它。
                if (Points.Count < 2) break;
                var o = CurvePointLocal(0);
                // 名字带 h 前缀：本方法开头已经有 `var (a, b) = Endpoints()`（直线的两个端点）。
                // **用曲线的半轴**（不是渐近线框的 A / B —— 那个只画虚线用）。
                float ha = HyperbolaCurveALocal(), hb = HyperbolaCurveBLocal();
                float hT = HyperbolaTMaxLocal();
                var hbox = CurveBoxLocal();
                int hn = CurveSegments(MathF.Max(hbox.MaxX - hbox.MinX, hbox.MaxY - hbox.MinY));
                for (int branch = 0; branch < 2; branch++)
                {
                    if (branch > 0) list.Add(OutlineBreak);       // 抬笔：两支之间不连线
                    for (int i = 0; i <= hn; i++)
                        list.Add(HyperbolaPoint(o, ha, hb, EffectiveAxis, branch,
                                                -hT + 2f * hT * i / hn));
                }
                break;
            }

            case StrokeKind.Cylinder:
            case StrokeKind.Cone:
            case StrokeKind.ConeFrustum:
            case StrokeKind.Sphere:
            {
                // 立体图形：轮廓 = **底面一圈 ＋（圆柱 / 圆台）顶面一圈 /（圆锥）两条母线**
                // ／（球）**赤道那一圈**。拿"外接矩形"四边当轮廓不行：椭圆弧与矩形之间
                // 那块是空的，橡皮从那儿划过会把整个立体删掉（和双曲线"两支之间不能连线"同类）。
                // 两段之间放**抬笔标记**，免得连出一条横穿包围盒的假线。
                if (Points.Count < 2) break;
                if (Kind == StrokeKind.Sphere)
                {
                    // 球的轮廓就是"轮廓整圆 ＋ 赤道整圈"两段（赤道的上下两半**都要列进来**：
                    // 上半虽然画成虚线，但橡皮得够得着它——轮廓只要求"覆盖到画出来的每一笔"）。
                    var (sc, sr) = SphereLocal();
                    var (_, eqRx, eqRy) = SphereEquatorLocal();
                    int sn = Math.Clamp((int)(sr / 4f), 12, 48);
                    for (int i = 0; i <= sn; i++)
                        list.Add(EllipseArcPoint(sc, sr, sr, i / (float)sn));
                    list.Add(OutlineBreak);
                    for (int i = 0; i <= sn; i++)
                        list.Add(EllipseArcPoint(sc, eqRx, eqRy, i / (float)sn));
                    break;
                }
                var (cx, topCy, botCy, rx, ry) = SolidEllipsesLocal();
                int n = Math.Clamp((int)(MathF.Max(rx, ry) / 4f), 12, 48);
                for (int i = 0; i <= n; i++)
                    list.Add(EllipseArcPoint(new Vector2(cx, botCy), rx, ry, i / (float)n));
                list.Add(OutlineBreak);
                if (Kind == StrokeKind.Cylinder)
                {
                    for (int i = 0; i <= n; i++)
                        list.Add(EllipseArcPoint(new Vector2(cx, topCy), rx, ry, i / (float)n));
                    // **两条母线也要列进来**（2026-09-20 补）：橡皮的粗筛就是按这份轮廓判
                    // "够不够得着"，少了它们，橡皮落在这两条母线上会"够不着这个圆柱"——
                    // 明明画着线却擦不掉（圆锥那两条一直都在，圆柱这两条是漏的）。
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx - rx, topCy));
                    list.Add(new Vector2(cx - rx, botCy));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx + rx, topCy));
                    list.Add(new Vector2(cx + rx, botCy));
                }
                else if (Kind == StrokeKind.ConeFrustum)
                {
                    // 圆台：上底那一圈换成**小一圈**的椭圆，两条母线跟着**收进去**
                    //（母线一样要列——理由同上，少了它们橡皮够不着这两条线）。
                    var (tc, trx, try_) = ConeFrustumTopLocal();
                    for (int i = 0; i <= n; i++)
                        list.Add(EllipseArcPoint(tc, trx, try_, i / (float)n));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx - rx, botCy));
                    list.Add(new Vector2(tc.X - trx, tc.Y));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx + rx, botCy));
                    list.Add(new Vector2(tc.X + trx, tc.Y));
                }
                else
                {
                    var apex = ConeApexLocal();
                    list.Add(new Vector2(cx - rx, botCy));
                    list.Add(apex);
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx + rx, botCy));
                    list.Add(apex);
                }
                break;
            }

            case StrokeKind.Cuboid:
            case StrokeKind.Tetrahedron:
            case StrokeKind.Prism:
            case StrokeKind.Pyramid:
            case StrokeKind.Frustum:
            {
                // 立体多面体：轮廓 = 它的各条棱（**分段**列，不许连出假线——
                // 拿"外接矩形"当轮廓会让橡皮擦到空白处就把整个立体删掉）。
                if (Points.Count < 3) break;
                if (IsPrismFamily(Kind))
                {
                    // 棱柱一族：底面一圈 ＋（棱锥没有）顶面一圈 ＋ n 条侧棱，**每段之间抬笔**。
                    // 和长方体那支同一个口径：轮廓只要求"覆盖到画出来的每一笔"，
                    // 不分实 / 虚（实虚是渲染与熔墨的事，见 PrismFamilyEdges）。
                    var bp = PrismBaseLocal();
                    var tp = PrismTopLocal();
                    int np = bp.Length;
                    for (int k = 0; k <= np; k++) list.Add(bp[k % np]);   // 底面闭合一圈
                    list.Add(OutlineBreak);
                    if (Kind != StrokeKind.Pyramid)                       // 棱锥的顶面只是一个点
                    {
                        for (int k = 0; k <= np; k++) list.Add(tp[k % np]);   // 顶面闭合一圈
                    }
                    for (int k = 0; k < np; k++)
                    {
                        list.Add(OutlineBreak);
                        list.Add(bp[k]);
                        list.Add(tp[k]);
                    }
                    list.Add(OutlineBreak);
                    break;
                }
                if (Kind == StrokeKind.Cuboid)
                {
                    var (x0, y0, x1, y1) = CuboidFrontLocal();
                    float d = CuboidDepthLocal();
                    // 正面一圈 + 背面一圈（各自闭合，两圈之间抬笔）
                    list.Add(new Vector2(x0, y0));
                    list.Add(new Vector2(x1, y0));
                    list.Add(new Vector2(x1, y1));
                    list.Add(new Vector2(x0, y1));
                    list.Add(new Vector2(x0, y0));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(x0 + d, y0 - d));
                    list.Add(new Vector2(x1 + d, y0 - d));
                    list.Add(new Vector2(x1 + d, y1 - d));
                    list.Add(new Vector2(x0 + d, y1 - d));
                    list.Add(new Vector2(x0 + d, y0 - d));
                    list.Add(OutlineBreak);
                    // 四条斜棱（每条一段，互相之间抬笔）
                    var corners = new[]
                    {
                        (new Vector2(x0, y0), new Vector2(x0 + d, y0 - d)),
                        (new Vector2(x1, y0), new Vector2(x1 + d, y0 - d)),
                        (new Vector2(x0, y1), new Vector2(x0 + d, y1 - d)),
                        (new Vector2(x1, y1), new Vector2(x1 + d, y1 - d)),
                    };
                    foreach (var (m, n) in corners)
                    {
                        list.Add(m);
                        list.Add(n);
                        list.Add(OutlineBreak);
                    }
                    list.RemoveAt(list.Count - 1);      // 末尾那个抬笔没人接，去掉（免得消费者读到孤零零的 NaN）
                }
                else
                {
                    var p0 = CurvePointLocal(0);
                    var p1 = CurvePointLocal(1);
                    var p2 = CurvePointLocal(2);
                    var apex = TetraApexLocal();
                    var edges = new[]
                    {
                        (p0, p1), (p1, p2), (p2, p0),      // 底面三条边
                        (p0, apex), (p1, apex), (p2, apex) // 三条棱
                    };
                    foreach (var (m, n) in edges)
                    {
                        list.Add(m);
                        list.Add(n);
                        list.Add(OutlineBreak);
                    }
                    list.RemoveAt(list.Count - 1);      // 同上：去掉末尾那个抬笔
                }
                break;
            }

            case StrokeKind.Sine:
            case StrokeKind.Cosine:
            case StrokeKind.Wave:
            {
                // 正弦 / 余弦 / **波浪线**：一条开折线，t 从 0 走到 1
                //（**和 BuildWave 同一份算式**）。
                // 注意 `WaveTracedPointAt` 而不是 `WavePointAt`：前者按"整条曲线"的比例走、
                // 对三种都对；后者按"周期数"走（正弦恒等于前者，波浪线可能是 2.7 个周期，
                // 见 WaveCyclesLocal）。
                if (Points.Count < 2) break;
                int n = WaveSegmentsLocal();
                for (int i = 0; i <= n; i++) list.Add(WaveTracedPointAt(i / (float)n));
                break;
            }

            case StrokeKind.Tangent:
            {
                // 正切：一条开折线，t 从 0 走到 1（左下的截断点 → 右上的截断点；
                // **和 BuildTangent 同一份算式**，所以橡皮的轮廓和画出来的一模一样）。
                if (Points.Count < 2) break;
                int nt = TangentSegmentsLocal();
                for (int i = 0; i <= nt; i++) list.Add(TangentTracedPointAt(i / (float)nt));
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

    /// <summary>
    /// 这一条对象的**辅助几何**（目前只有双曲线的两条虚线渐近线，见
    /// <see cref="HyperbolaAsymptoteLocal"/>）。
    ///
    /// 为什么要单独一个槽：主几何的线型是这个对象自己的（老师可能已经把曲线
    /// 设成虚线了），而渐近线**恒定是细虚线**——"一个对象两种线"落在这里。
    ///
    /// 它**不参与命中**（和坐标系网格同一条口径：辅助线点不中），也在曲线紧框之内。
    /// 缓存规则和主几何逐字一样（按 <see cref="Revision"/>）。
    /// </summary>
    public ID2D1Geometry BuildAuxGeometry(ID2D1Factory1 factory)
    {
        if (Geometry2 != null && _builtRevision2 == Revision) return Geometry2;
        if (Geometry2 != null) { Geometry2.Dispose(); Geometry2 = null; }
        if (Points.Count < 2)
        {
            _builtRevision2 = Revision;
            return null;
        }

        // **坐标系的网格**（2026-09-24，用户："把那个网格的线变细变淡一些"）：
        // 它走这个槽，是因为"一个对象、两种线"这件事这个槽已经在装了（原来是渐近线）。
        // 描边风格在 Overlay.DrawStroke 里按"是不是网格"分：网格 **细一半、淡一半、实线**，
        // 辅助线照旧 0.6 倍、虚线。坐标系没有网格时这个槽是空的（返回 null）。
        if (Kind == StrokeKind.Coordinate && Grid)
        {
            Geometry2 = BuildAxes(factory, gridOnly: true);
            _builtRevision2 = Revision;
            return Geometry2;
        }

        // **立体图形被挡住的那几笔**（圆柱 / 圆锥 / 圆台的下底上半圈、**球的赤道远侧半圈**、
        // 长方体被挡的三条棱）：恒定细虚线。
        if (Kind is StrokeKind.Cylinder or StrokeKind.Cone or StrokeKind.ConeFrustum
            or StrokeKind.Sphere)
        {
            Geometry2 = BuildSolidHidden(factory);
            _builtRevision2 = Revision;
            return Geometry2;
        }
        if (Kind == StrokeKind.Cuboid)
        {
            Geometry2 = BuildCuboid(factory, hidden: true);
            _builtRevision2 = Revision;
            return Geometry2;
        }
        // **棱柱 / 棱锥 / 棱台**被挡住的那些棱（细虚线）——三兄弟共用（见 PrismFamilyEdges）。
        if (IsPrismFamily(Kind))
        {
            Geometry2 = BuildPrism(factory, hidden: true);
            _builtRevision2 = Revision;
            return Geometry2;
        }
        // **正切的两条竖直渐近线**（x = ±π/2）——恒定细虚线，和双曲线那两条同一种画法。
        // 它**没有开关**：那两条线是"这一支画到哪儿为止"的边界，不是可选的辅助线。
        if (Kind == StrokeKind.Tangent)
        {
            Geometry2 = BuildTangentAsymptotes(factory);
            _builtRevision2 = Revision;
            return Geometry2;
        }

        if (Kind != StrokeKind.Hyperbola || !ShowAsymptotes)
        {
            _builtRevision2 = Revision;
            return null;
        }

        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            for (int side = -1; side <= 1; side += 2)
            {
                var (from, to) = HyperbolaAsymptoteLocal(side);
                sink.BeginFigure(from, FigureBegin.Hollow);
                sink.AddLine(to);
                sink.EndFigure(FigureEnd.Open);
            }
            sink.Close();
        }
        Geometry2 = geo;
        _builtRevision2 = Revision;
        return Geometry2;
    }

    /// <summary>
    /// **正切**几何：一支（`t` 从 0 走到 1 ＝ 左下的截断点 → 右上的截断点）。
    /// 形状由"横竖同一个单位"钉死，所以它**不会被拖变形**（见 TangentUnitLocal）。
    /// </summary>
    private ID2D1PathGeometry BuildTangent(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        int n = TangentSegmentsLocal();

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(TangentTracedPointAt(0f), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
            sink.AddLine(TangentTracedPointAt(i / (float)n));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>正切的**两条渐近线**（细虚线）——走辅助几何槽，和双曲线那两条同一种画法。</summary>
    private ID2D1PathGeometry BuildTangentAsymptotes(ID2D1Factory1 factory)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        for (int side = -1; side <= 1; side += 2)
        {
            var (from, to) = TangentAsymptoteLocal(side);
            sink.BeginFigure(from, FigureBegin.Hollow);
            sink.AddLine(to);
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    public ID2D1Geometry BuildGeometry(ID2D1Factory1 factory)
    {
        // 缓存键 = 几何版本（Revision）＋ 曲线化版本 ＋ 活笔/模型版本；
        // 只比 Revision 的话，开关一拨会把旧几何还回来。
        if (Geometry != null && _builtRevision == Revision
            && _builtSmoothVer == StrokeSmoothing.Version && _builtRawLive == RawWhileLive
            && _builtInkModelVer == StrokeMotion.Version)
            return Geometry;
        if (Points.Count == 0) return null;

        if (Geometry != null) { Geometry.Dispose(); Geometry = null; LiveGeometries--; }

        Geometry = Kind switch
        {
            StrokeKind.Rectangle => BuildRectangle(factory),
            StrokeKind.Ellipse => BuildEllipse(factory),
            // 椭圆（带焦点）：椭圆 ＋ 两焦点 ＋（有档时）焦点三角形，见 BuildConicEllipse
            StrokeKind.ConicEllipse => BuildConicEllipse(factory),
            StrokeKind.Circle => BuildCircle(factory),
            StrokeKind.Arrow => BuildArrow(factory),
            StrokeKind.Line => BuildLine(factory),
            StrokeKind.Triangle => BuildPolygon(factory),
            StrokeKind.Parallelogram => BuildPolygon(factory),
            StrokeKind.Coordinate => BuildAxes(factory),
            StrokeKind.NumberLine => BuildAxes(factory),
            StrokeKind.Parabola => BuildParabola(factory),
            StrokeKind.Hyperbola => BuildHyperbola(factory),
            StrokeKind.Sine => BuildWave(factory),
            StrokeKind.Cosine => BuildWave(factory),
            StrokeKind.Wave => BuildWave(factory),
            StrokeKind.Tangent => BuildTangent(factory),
            StrokeKind.Cylinder => BuildSolid(factory),
            StrokeKind.Cone => BuildSolid(factory),
            StrokeKind.ConeFrustum => BuildSolid(factory),
            StrokeKind.Sphere => BuildSolid(factory),
            StrokeKind.Cuboid => BuildCuboid(factory, hidden: false),
            StrokeKind.Tetrahedron => BuildTetra(factory),
            StrokeKind.Prism => BuildPrism(factory, hidden: false),
            StrokeKind.Pyramid => BuildPrism(factory, hidden: false),
            StrokeKind.Frustum => BuildPrism(factory, hidden: false),
            StrokeKind.Image => BuildImageRect(factory),
            // 自由笔迹：只给**中心线**，描边（宽度、端帽、拐角）交给 D2D。
            // 单点例外——那是一个圆点，几何直接建成圆（渲染那边会填充它）。
            _ => Points.Count == 1 ? BuildDot(factory) : BuildCenterline(factory),
        };
        if (Geometry != null) LiveGeometries++;
        _builtRevision = Revision;
        _builtSmoothVer = StrokeSmoothing.Version;
        _builtRawLive = RawWhileLive;
        _builtInkModelVer = StrokeMotion.Version;
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

    /// <summary>
    /// 多边形几何（**闭合折线**）：三角形 / 平行四边形共用这一份。
    ///
    /// 和矩形同一个写法（`Hollow` 起点 ＋ `Closed` 收尾），于是命中判定天然就是
    /// **只认描边、不认内部**——这正是规格要的（和矩形/椭圆一致，见 HitTestExact）。
    /// 平行四边形的第四个顶点**现推**（<see cref="ParallelogramFourthLocal"/>），
    /// 所以"永远是平行四边形"是几何本身的性质，不是拖动时校正出来的。
    /// </summary>
    private ID2D1PathGeometry BuildPolygon(ID2D1Factory1 factory)
    {
        // 控制点不足三个：不该发生（画法与存档都保证三个），真发生了也别抛异常，
        // 退回一条线就行——一条画错位置的线，比整个渲染循环崩掉好得多。
        if (Points.Count < 3) return BuildLine(factory);

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(Points[0].X, Points[0].Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(Points[1].X, Points[1].Y));
        if (Kind == StrokeKind.Parallelogram) sink.AddLine(ParallelogramFourthLocal());
        sink.AddLine(new Vector2(Points[2].X, Points[2].Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 坐标系 / 数轴的几何：**轴线 ＋ 箭头**（坐标系还可能有那个可选网格）。
    ///
    /// **没有刻度线**（用户 2026-09-19 定："数轴和坐标系上面的刻度太多了……不需要刻度"）。
    /// 刻度曾经在这里画过一版，撤掉之后两个种类都清爽了：
    ///   · 坐标系 = 十字轴 + 两个箭头；开网格时再多一组贯穿外框的格线；
    ///   · 数轴 = 一条水平线 + 一个右箭头。
    /// 代价是"单位长度点"和数轴的"零点"这两个定义元素随之退场（见 SetAxisBox）。
    ///
    /// 一条 path 里放**多个 figure**：描边、命中、虚线都按整条处理，正是我们要的
    /// ——虚线时每一段（每条轴、每根网格线）各自起落，不会跨过拐角连成一长条。
    ///
    /// 箭头画成**一个 V（三段折线）**，不填充三角形：和"箭头工具"同一个做法
    /// （见 <see cref="ArrowHeadPoints"/>），任何笔宽下都匀称；填充的话细笔会糊成一个点。
    /// 头长按**笔宽**算而不是按轴长算——轴可以拉得很长，头跟着长就成了怪东西。
    /// </summary>
    private ID2D1PathGeometry BuildAxes(ID2D1Factory1 factory, bool gridOnly = false)
    {
        // 控制点不足两个：不该发生（画法与存档都保证），真发生了退回一条线，
        // 别让整个渲染循环崩掉（和 BuildPolygon 那条护栏同一个理由）。
        if (Points.Count < 2) return gridOnly ? null : BuildLine(factory);

        // **轴线与网格分成两段几何**：一个几何只能有一种描边，而网格要细一半、淡一半
        //（用户 2026-09-24）。这里按 Grid 位分拣，别的地方都当"一个对象"看它。
        var pieces = new List<InkPiece>();
        foreach (var p in AxisPieces())
            if (p.Grid == gridOnly) pieces.Add(p);
        return pieces.Count == 0 ? null : FromPieces(factory, pieces);
    }

    /// <summary>
    /// 坐标系 / 数轴的**逐笔点列**：轴线 ＋ 箭头（坐标系还有网格）。
    ///
    /// **渲染与熔墨共用这一份**（<see cref="BuildAxes"/> 与 <see cref="InkPieces"/>）：
    /// 以前轴线、箭头、网格分别写在三个地方，熔墨只认得轴线那一份，
    /// 于是"擦一下坐标系，网格和箭头就没了"（用户 2026-09-20 反馈的那类变形）。
    /// </summary>
    private List<InkPiece> AxisPieces()
    {
        var list = new List<InkPiece>();
        var (minX, minY, maxX, maxY) = AxisFrameLocal();
        float head = AxisArrowHeadLen(Width);

        // ---- 数轴：一条水平线 + 右端一个箭头 ----
        if (Kind == StrokeKind.NumberLine)
        {
            float ly = Points[0].Y;                    // 两个端点 y 恒相等（见 SetAxisBox）
            list.Add(new InkPiece(new List<Vector2> { new(minX, ly), new(maxX, ly) }, false));
            list.Add(new InkPiece(AxisArrowPoints(new Vector2(maxX, ly), new Vector2(1f, 0f), head), false));
            return list;
        }

        // ---- 坐标系：十字轴 + 两个箭头 ----
        var o = AxisOriginLocal();
        list.Add(new InkPiece(new List<Vector2> { new(minX, o.Y), new(maxX, o.Y) }, false));
        // 屏幕坐标 y 向下，所以"向上"是往 minY 那头走。
        list.Add(new InkPiece(new List<Vector2> { new(o.X, maxY), new(o.X, minY) }, false));
        list.Add(new InkPiece(AxisArrowPoints(new Vector2(maxX, o.Y), new Vector2(1f, 0f), head), false));
        list.Add(new InkPiece(AxisArrowPoints(new Vector2(o.X, minY), new Vector2(0f, -1f), head), false));

        if (Grid)
        {
            // 网格线单独一段身份（见 InkPiece.Grid）：**细一半、淡一半的实线**，
            // 而且不参与命中（ShapeOutline 里只列两条轴线）。
            foreach (var (a, b) in AxisGridSegments(minX, minY, maxX, maxY, o))
                list.Add(new InkPiece(new List<Vector2> { a, b }, false, grid: true));
        }
        return list;
    }

    /// <summary>
    /// 坐标系**可选网格**的每一条线（起终点）。规则只有这一份：
    /// <see cref="BuildAxes"/> 与熔墨都从这里取，别各写一遍。
    ///
    /// 间距 = <see cref="AxisGridStepLocal"/>（外框短边 ÷ 4，**现算不存**）。
    /// 上限定 512 条 / 方向：外框可以被拉到上万像素，不设上限的话一条病态的对象
    /// 能造出几万个 figure，一帧就把渲染拖死（和刻度时代那条上限同一个理由）。
    /// 注意网格线**不画在原点那一格上**——那两条正是轴本身，重画一遍只会加深一遍颜色。
    /// </summary>
    private List<(Vector2 a, Vector2 b)> AxisGridSegments(float minX, float minY, float maxX, float maxY,
                                                          Vector2 origin)
    {
        const int MaxLines = 512;
        var list = new List<(Vector2, Vector2)>();
        float step = AxisGridStepLocal();

        for (int k = 1; k <= MaxLines; k++)
        {
            float xr = origin.X + k * step, xl = origin.X - k * step;
            if (xr > maxX + 0.01f && xl < minX - 0.01f) break;
            if (xr <= maxX + 0.01f) list.Add((new Vector2(xr, minY), new Vector2(xr, maxY)));
            if (xl >= minX - 0.01f) list.Add((new Vector2(xl, minY), new Vector2(xl, maxY)));
        }
        for (int k = 1; k <= MaxLines; k++)
        {
            float yd = origin.Y + k * step, yu = origin.Y - k * step;
            if (yd > maxY + 0.01f && yu < minY - 0.01f) break;
            if (yd <= maxY + 0.01f) list.Add((new Vector2(minX, yd), new Vector2(maxX, yd)));
            if (yu >= minY - 0.01f) list.Add((new Vector2(minX, yu), new Vector2(maxX, yu)));
        }
        return list;
    }

    /// <summary>
    /// 一个箭头（坐标轴 / 数轴末端那种）：从尖端往回画一个 **V**
    ///（<c>翅膀根 → 尖端 → 另一侧翅膀根</c>）。
    ///
    /// 比例照抄 <see cref="ArrowHeadPoints"/>（翅膀尖在尖端后方 `head` 处、左右各张 `0.45 × head`），
    /// 这样坐标系上的箭头和"箭头工具"画出来的那支看着是一家人。
    /// </summary>
    private static List<Vector2> AxisArrowPoints(Vector2 tip, Vector2 dir, float head)
    {
        var root = tip - dir * head;
        float spread = head * 0.45f;
        var n = new Vector2(-dir.Y, dir.X);               // 垂直于箭头方向
        return new List<Vector2> { root + n * spread, tip, root - n * spread };
    }

    /// <summary>
    /// **抛物线**几何：把 <see cref="ParabolaPointAt"/> 采成一串折线点（**开折线**，不闭合）。
    ///
    /// 控制点不足两个时退回一条线：不该发生（画法与存档都保证两个），真发生了也别抛异常
    /// ——一条画错位置的线，比整个渲染循环崩掉好得多（和 BuildPolygon 那条护栏同一个理由）。
    /// </summary>
    private ID2D1PathGeometry BuildParabola(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        var box = CurveBoxLocal();
        int n = CurveSegments(MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY));

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        // t 从 −Span 走到 +Span：一个端点 → 顶点 → 另一个端点
        //（Span 见 ParabolaSpanOf：**画到"经过点"那儿为止**，和轮廓折线用同一个值）。
        float span = ParabolaSpanLocal();
        sink.BeginFigure(ParabolaPointAt(-span), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
            sink.AddLine(ParabolaPointAt(-span + 2f * span * i / n));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// **双曲线**几何：两支各一个 figure（`t` 从 `−T` 走到 `+T`）。
    ///
    /// 两支放**同一条 path**里，于是描边、虚线、命中、包围盒都按"一条对象"处理
    /// （和坐标系的多 figure 一个做法，见 <see cref="BuildAxes"/>）——
    /// 如果拆成两个对象，"选中/拖动/撤销/存档"全都要多一层"组"的概念。
    /// </summary>
    private ID2D1PathGeometry BuildHyperbola(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        var o = CurvePointLocal(0);
        // **曲线的半轴**（不是渐近线框的 A / B —— 那两个只用来画虚线渐近线，见 BuildAuxGeometry）。
        float a = HyperbolaCurveALocal(), b = HyperbolaCurveBLocal();
        var box = CurveBoxLocal();
        int n = CurveSegments(MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY));
        float tMax = HyperbolaTMaxLocal();

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        for (int branch = 0; branch < 2; branch++)
        {
            sink.BeginFigure(HyperbolaPoint(o, a, b, EffectiveAxis, branch, -tMax), FigureBegin.Hollow);
            for (int i = 1; i <= n; i++)
                sink.AddLine(HyperbolaPoint(o, a, b, EffectiveAxis, branch, -tMax + 2f * tMax * i / n));
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    /// <summary>
    /// **正弦 / 余弦 / 波浪线**几何：`t` 从 0 走到 1 —— 也就是**从起点画到终点**
    ///（正弦 / 余弦：框宽 = 一个周期；波浪线：框宽 = 画多长、周期由振幅定，
    /// 见 <see cref="WaveCyclesLocal"/>；三个种类共用这一份算式，
    /// 差别只在 <see cref="WaveYAt"/> 里那一行 sin / cos）。
    ///
    /// 两端不闭合、也没有拐回头的部分（它是一条"图象"，不是封闭图形）。
    /// </summary>
    private ID2D1PathGeometry BuildWave(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        int n = WaveSegmentsLocal();

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(WaveTracedPointAt(0f), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
            sink.AddLine(WaveTracedPointAt(i / (float)n));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 椭圆几何（**中心 ＋ 两条半轴**）。
    ///
    /// 2026-09-19 改：以前是"两个对角点"定外框（`BuildEllipse` 从两点算中心与半轴）。
    /// 现在 `Points[0]` 就是中心、`Points[1]` 是外角点（`a = |dx|`、`b = |dy|`），
    /// 所以这里直接读两个半轴——**画法和手柄用的是同一个定义**，
    /// 不会出现"画出来是按外框、手柄按半轴"这种两套账。
    /// </summary>
    private ID2D1Geometry BuildEllipse(ID2D1Factory1 factory)
    {
        return factory.CreateEllipseGeometry(
            new Ellipse(ShapeCenterLocal,
                        MathF.Max(0.5f, SemiAxisALocal),
                        MathF.Max(0.5f, SemiAxisBLocal)));
    }

    /// <summary>圆几何（D2D 原生椭圆几何，两个半径相同）。</summary>
    private ID2D1Geometry BuildCircle(ID2D1Factory1 factory)
    {
        float r = MathF.Max(0.5f, CircleRadiusLocal);
        return factory.CreateEllipseGeometry(new Ellipse(ShapeCenterLocal, r, r));
    }

    /// <summary>
    /// **椭圆（带焦点）**的几何（2026-09-22）：椭圆本体 ＋ 两个焦点（小圆点）
    /// ＋（有档时）焦点三角形的两条边 F₁P / F₂P。
    ///
    /// **为什么不用 `ID2D1EllipseGeometry`**：这里要的是"圆 ＋ 两条边 ＋ 两个点"**四段几何的并**，
    /// 只有路径几何能装；而 `ID2D1GeometrySink` 在 Vortice 里没有 `AddEllipse`，
    /// 所以椭圆本体按**折线**画（`EllipseSegments` 份，和 `ShapeOutline` 同一份口径、
    /// 同一个段数函数——两处差一段就是"橡皮擦得掉、D2D 命中差一点"这种说不清的小账）。
    ///
    /// **为什么焦点和三角形走主几何、不走虚线那个辅助槽**（`Geometry2`）：
    /// 辅助槽是"细虚线的辅助线"（渐近线、被挡住的棱）专用的；而焦点三角形是这道题的
    /// **主体**——它得是实线、和椭圆同一个颜色同一个粗细，放辅助槽里就成了一条虚线。
    /// </summary>
    private ID2D1PathGeometry BuildConicEllipse(ID2D1Factory1 factory)
    {
        var c = ShapeCenterLocal;
        float rx = MathF.Max(0.5f, SemiAxisALocal);
        float ry = MathF.Max(0.5f, SemiAxisBLocal);

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();

        // ① 椭圆本体：一圈折线（首尾相接 → 闭合）
        int n = EllipseSegments(MathF.Max(rx, ry));
        sink.BeginFigure(new Vector2(c.X + rx, c.Y), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
        {
            float t = i / (float)n * MathF.Tau;
            sink.AddLine(new Vector2(c.X + MathF.Cos(t) * rx, c.Y + MathF.Sin(t) * ry));
        }
        sink.EndFigure(FigureEnd.Closed);

        var (f1, f2) = ConicEllipseFociLocal();

        // ② 焦点三角形的两条边：**一笔画 F₁ → P → F₂**（两条边共用中间的 P，
        //    写成一条折线就是"画出来的那个三角形去掉底边"）。
        if (FocusTriangle)
        {
            var p = ConicEllipsePointLocal();
            sink.BeginFigure(f1, FigureBegin.Hollow);
            sink.AddLine(p);
            sink.AddLine(f2);
            sink.EndFigure(FigureEnd.Open);
        }

        // ③ 两个焦点：小圆点（描边＝这一笔的宽度，见 FocusDotRadius 的注释）。
        //    同样按折线画一圈（8 段足够小到看不出棱角）。
        //    **两档都画**：用户 2026-09-22 定的"无焦点三角形"只是不连那两条边，焦点还在。
        AddDot(sink, f1);
        AddDot(sink, f2);

        sink.Close();
        return geo;

        // 小工具：画一个"小圆点"（8 段折线的小圆）。
        static void AddDot(ID2D1GeometrySink sink, Vector2 at)
        {
            const int seg = 8;
            sink.BeginFigure(new Vector2(at.X + FocusDotRadius, at.Y), FigureBegin.Hollow);
            for (int i = 1; i <= seg; i++)
            {
                float t = i / (float)seg * MathF.Tau;
                sink.AddLine(new Vector2(at.X + MathF.Cos(t) * FocusDotRadius,
                                         at.Y + MathF.Sin(t) * FocusDotRadius));
            }
            sink.EndFigure(FigureEnd.Closed);
        }
    }

    // =====================================================================
    //  立体图形（2026-09-20 第五批：照 InkClass 的 case 6/7 搬过来）
    //
    //  两个控制点 = 画出来的东西的**外接矩形**（拖到哪就是哪），
    //  椭圆都由它派生：`rx = 宽/2`、`ry = rx / 2.646`（扁率照抄他的常数）。
    //  这样"控制点的外接"**正好就是**真实范围（模型里默认那条包围盒算法就够用），
    //  不用像曲线那样再单独算一份。
    // =====================================================================

    /// <summary>立体图形里椭圆的扁率（短轴 / 长轴）：照 InkClass 的 `2.646`。
    /// **只有一份**在 <see cref="ShapeSpec.SolidEllipseRatio"/>（图标层看不到 `Stroke`，
    /// 而图标上那个椭圆得和画布同一个俯角）——这里只是转发，老代码照旧读这个名。</summary>
    public const float SolidEllipseRatio = ShapeSpec.SolidEllipseRatio;

    /// <summary>外接矩形（局部坐标，已归一成 左上 / 右下）。</summary>
    public (float X0, float Y0, float X1, float Y1) SolidRectLocal()
    {
        var a = CurvePointLocal(0);
        var b = Points.Count >= 2 ? CurvePointLocal(1) : a;
        return (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    /// <summary>写外接矩形（画的时候每一帧都调它；归一成 左上 / 右下，和坐标系那套一致）。</summary>
    public void SetSolidBox(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        SetPoint(0, new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)));
        SetPoint(1, new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)));
    }

    /// <summary>
    /// 上下两个**椭圆**的位置与半径（局部坐标）：`(cx, 上圆心 y, 下圆心 y, rx, ry)`。
    ///
    /// 椭圆的圆心**从矩形边往里缩一个 `ry`**，于是椭圆正好与矩形上下边相切
    ///（画出来的东西刚好占满拖出来的那个矩形）。InkClass 是把圆心放在矩形边上的
    ///（椭圆会往外冒半圈），我们往里收一下：范围好算、紧框不会漏，看着完全一样。
    /// </summary>
    public (float Cx, float TopCy, float BottomCy, float Rx, float Ry) SolidEllipsesLocal()
    {
        var (x0, y0, x1, y1) = SolidRectLocal();
        float rx = MathF.Max(0.5f, (x1 - x0) * 0.5f);
        float ry = MathF.Max(0.5f, rx * SolidEllipseRatio);
        float cx = (x0 + x1) * 0.5f;
        return (cx, y0 + ry, y1 - ry, rx, ry);
    }

    /// <summary>
    /// **球**在画面上的那个圆（圆心 ＋ 半径，局部坐标）。
    ///
    /// 球投影下来**永远是圆**，所以这里不像别的立体那样分 rx / ry，而是取外接矩形里
    /// **能内切的那个正圆**：半径 = `min(半宽, 半高)`、圆心 = 矩形中心。
    /// 拖成方的就是标准球；拖长了（比如 400×200）也**不会**画出一个"扁球"，
    /// 而是画一个内切的圆——"球一定是圆的"这件事由算式保证，不靠用户手稳。
    ///
    /// ⚠ 因此它的**真实范围 = 圆心 ± 半径**，不是那个外接矩形（矩形拖长了就有富余）——
    /// 所以 `RecomputeBounds` 与 `InkBounds` 都得有球这一支（照棱柱一族那套）。
    /// </summary>
    public (Vector2 Center, float R) SphereLocal()
    {
        var (x0, y0, x1, y1) = SolidRectLocal();
        float r = MathF.Max(0.5f, MathF.Min(x1 - x0, y1 - y0) * 0.5f);
        return (new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f), r);
    }

    /// <summary>
    /// **球的赤道**那个椭圆（圆心 = 球心、两个半轴）：
    /// 长半轴 = 球的半径（赤道的左右两端正好落在轮廓圆上那两个点），
    /// 短半轴 = 半径 × <see cref="SolidEllipseRatio"/>。
    ///
    /// ⚠ 扁率**和圆柱 / 圆锥 / 圆台用同一个数**：那是"从上往下看"的俯角，
    /// 一个画面里所有圆都该用同一个俯角，不然球看起来比圆柱"躺得更平"。
    /// </summary>
    public (Vector2 Center, float Rx, float Ry) SphereEquatorLocal()
    {
        var (c, r) = SphereLocal();
        return (c, r, MathF.Max(0.5f, r * SolidEllipseRatio));
    }

    /// <summary>圆锥的顶点（局部坐标）= 外接矩形的**上边中点**（照 InkClass）。</summary>
    public Vector2 ConeApexLocal()
    {
        var (x0, y0, x1, _) = SolidRectLocal();
        return new Vector2((x0 + x1) * 0.5f, y0);
    }

    /// <summary>
    /// **圆台的上底**那个椭圆（圆心 ＋ 两个半轴，局部坐标）——它和 <see cref="Cylinder"/> /
    /// <see cref="Cone"/> 唯一的差别就在这一处。
    ///
    /// 上底 = 下底**同形缩一个固定比例**（<see cref="ShapeSpec.FrustumTopScale"/>，
    /// 和「棱台」用的是**同一个数**——两个台体在面板上挨着，看着该是同一个收法）：
    ///   · 两个半轴**都乘那个比例**（圆还是圆，只是小了）；
    ///   · 圆心**从上边往里缩 `ry × 比例`**（不是缩 `ry`）——这样上底椭圆正好与矩形上边
    ///     **相切**，画出来的东西刚好占满拖出来的那个矩形（和圆柱 / 圆锥同一条老规矩：
    ///     范围好算、紧框不会漏、看着完全一样）。
    /// </summary>
    public (Vector2 Center, float Rx, float Ry) ConeFrustumTopLocal()
    {
        var (cx, _, _, rx, ry) = SolidEllipsesLocal();
        var (_, y0, _, _) = SolidRectLocal();
        float k = ShapeSpec.FrustumTopScale;
        return (new Vector2(cx, y0 + ry * k), rx * k, ry * k);
    }

    // ---- 长方体（两笔：正面矩形 → 深度）---------------------------------

    /// <summary>正面矩形（局部坐标，已归一成 左上 / 右下）。</summary>
    public (float X0, float Y0, float X1, float Y1) CuboidFrontLocal()
    {
        var a = CurvePointLocal(0);
        var b = Points.Count >= 2 ? CurvePointLocal(1) : a;
        return (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    /// <summary>长方体的**最小深度**：拖得太浅就不像一个立体了。</summary>
    public const float CuboidMinDepth = 12f;

    /// <summary>**第 1 笔**：正面矩形（归一成 左上 / 右下）。</summary>
    public void SetCuboidFront(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        SetPoint(0, new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)));
        SetPoint(1, new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)));
    }

    /// <summary>
    /// **第 2 笔**：拖出**深度**。口径照 InkClass：`d = |正面矩形上边 − 指针 y|`，
    /// 方向**恒定往后上方 45°**（他原话："就是懒不想做反向的"——我们也先不做）。
    ///
    /// 存进第三个控制点的是**背面右下角** `(右 + d, 下 − d)`，而不是指针原样：
    /// 那个角正好是画出来的东西最外的一个角，于是"控制点外接"就是真实范围
    ///（包围盒、命中粗筛、导出裁切都直接对）。
    /// </summary>
    public void SetCuboidDepth(float x, float y)
    {
        var (_, y0, x1, y1) = CuboidFrontLocal();
        float d = MathF.Max(CuboidMinDepth, MathF.Abs(y0 - y));
        while (Points.Count < 3) AddPoint(x, y, 1f, 0);
        SetPoint(2, new Vector2(x1 + d, y1 - d));
    }

    /// <summary>深度（由背面右下角反推：`d = 正面下边 − 那个角的 y`）。</summary>
    public float CuboidDepthLocal()
    {
        if (Points.Count < 3) return 0f;
        var (_, _, _, y1) = CuboidFrontLocal();
        return MathF.Max(0f, y1 - CurvePointLocal(2).Y);
    }

    // ---- 四面体（两笔：底面三角形 → 顶点）-------------------------------

    /// <summary>**第 1 笔**：底面三角形（照他：底边水平、顶点居中向上）。</summary>
    public void SetTetraBase(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 3) AddPoint(x0, y0, 1f, 0);
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        SetPoint(0, new Vector2(minX, maxY));                       // 底左
        SetPoint(1, new Vector2(maxX, maxY));                       // 底右
        SetPoint(2, new Vector2((minX + maxX) * 0.5f, minY));       // 底顶（居中）
    }

    /// <summary>**第 2 笔**：顶点（拖到哪就是哪）。</summary>
    public void SetTetraApex(float x, float y)
    {
        while (Points.Count < 4) AddPoint(x, y, 1f, 0);
        SetPoint(3, new Vector2(x, y));
    }

    /// <summary>顶点（局部坐标）：第 2 笔拖到的地方；还没拖过时退化成底面那个顶。</summary>
    public Vector2 TetraApexLocal()
        => Points.Count >= 4 ? CurvePointLocal(3) : CurvePointLocal(2);

    // ---- 棱柱 / 棱锥 / 棱台（两笔：底面正 n 边形 → 顶上那个中心）-----------------
    //
    // 用户 2026-09-20 提出，见 计划-图形工具.md §32 / §34。三个"为什么这么定"：
    //   · **立着画**（底面水平俯视压扁 ＋ 侧棱竖直）：课本上正三/五/六棱柱都是这个样子。
    //     长方体是另一个朝向（正面矩形 ＋ 纵深 45°），两者**并存**、各有各的用处。
    //   · **几边形是可选的档位**（3~6），和直线的线型同构：画之前在图形面板那一格选，
    //     画的那一刻写进对象（见 Stroke.PrismSides）。
    //   · **第 2 笔拖到哪就是哪**（照四面体的顶点）：所以"往上拖 = 直棱柱 / 直棱锥 / 直棱台、
    //     拖歪 = 斜的"是**同一个动作的自然结果**，不需要再分两个工具。
    //
    // 三兄弟**控制点完全一样**（底面外接框两角 ＋ 顶上那个中心），差别只在顶面怎么来：
    // 平移一份（棱柱）/ 缩成一点（棱锥）/ 缩小一份（棱台）——见 PrismTopLocal。

    /// <summary>
    /// **底面几边形**（3~6）。和 <see cref="CurveAxis"/>（抛物线朝向）同一个地位：
    /// **画之前选好**，画的那一刻写进对象，之后它就是这条对象自己的属性——
    /// 面板再换档也不会回头改已经画好的（见 Engine.CycleSolidSides）。
    ///
    /// 存成一个**可读的整数**而不是四个枚举值：3/4/5/6 本来就是这条对象唯一想知道的事，
    /// 拆成 `Prism3/Prism4/...` 反而要在四处做映射。
    /// </summary>
    public int PrismSides = 4;

    /// <summary>档位的上下限（<see cref="PrismSides"/> 读出来先夹一道，坏值也不至于画出怪东西）。</summary>
    public const int MinPrismSides = 3;
    public const int MaxPrismSides = 6;

    /// <summary>不开棱柱工具时（别的图形 / 反序列化兜底）用的默认档：**四棱柱**。</summary>
    public const int DefaultPrismSides = 4;

    /// <summary>这一档的边数（夹过上下限的）。**所有几何都读它**，不要直接读字段。</summary>
    public int PrismSidesClamped => Math.Clamp(PrismSides, MinPrismSides, MaxPrismSides);

    /// <summary>底面外接框（局部坐标，已归一成 左上 / 右下）——`Points[0]` 与 `Points[1]`。</summary>
    public (float X0, float Y0, float X1, float Y1) PrismBaseBoxLocal()
    {
        var a = CurvePointLocal(0);
        var b = Points.Count >= 2 ? CurvePointLocal(1) : a;
        return (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    /// <summary>底面中心（局部坐标）= 外接框的中心。也是**侧棱向量的起点**。</summary>
    public Vector2 PrismBaseCenterLocal()
    {
        var (x0, y0, x1, y1) = PrismBaseBoxLocal();
        return new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
    }

    /// <summary>
    /// 顶面中心（局部坐标）= **第 2 笔拖到的地方**；还没拖过时退化成底心
    ///（这时侧棱向量是零，画出来是一条扁的底边，不会崩）。
    /// </summary>
    public Vector2 PrismApexLocal()
        => Points.Count >= 3 ? CurvePointLocal(2) : PrismBaseCenterLocal();

    /// <summary>
    /// **侧棱向量**（顶心 − 底心）。零向量 = 还没拖第二笔。
    /// 它就是"这一步拖出来的方向"：竖直 = 直棱柱，歪 = 斜棱柱。
    /// </summary>
    public Vector2 PrismLateralLocal() => PrismApexLocal() - PrismBaseCenterLocal();

    /// <summary>
    /// 底面的 `n` 个顶点（局部坐标）：底面上是一个**正** n 边形，落到画面上两步——
    /// **压扁**（俯视）＋ **错切**（远侧往右挪一点，斜二测）。
    /// 两条都在 <see cref="ShapeSpec.PrismBasePointOffset"/> 那一份实现里（画布与图标共用）。
    ///
    /// 结果长什么样（都是课本那个样子）：
    ///   · **三 / 五棱柱**：正前方是一条边（帐篷形）；
    ///   · **四棱柱**：底面是**前边水平、后边也水平**的平行四边形（＝斜二测的正方形）；
    ///   · **六棱柱**：左右两个尖点。
    ///
    /// 起始角见 <see cref="ShapeSpec.PrismBaseOffsetDegrees"/>。顶点在底面参数意义下是
    /// **正** n 边形，所以"正三棱柱 / 正六棱柱"是白拿的。
    /// </summary>
    public Vector2[] PrismBaseLocal()
    {
        var (x0, y0, x1, y1) = PrismBaseBoxLocal();
        float rx = MathF.Max(0.5f, (x1 - x0) * 0.5f);
        float ry = MathF.Max(0.5f, (y1 - y0) * 0.5f);
        var c = PrismBaseCenterLocal();
        int n = PrismSidesClamped;
        float offset = ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f;
        var pts = new Vector2[n];
        for (int k = 0; k < n; k++)
        {
            // 屏幕 y 向下，所以参数角 90° 落在**下方**（= "正前方"）。
            float a = offset + k * MathF.Tau / n;
            pts[k] = c + ShapeSpec.PrismBasePointOffset(MathF.Cos(a), MathF.Sin(a), rx, ry);
        }
        return pts;
    }

    /// <summary>
    /// **顶面**的那 `n` 个点（局部坐标）——棱柱／棱锥／棱台**一家人**在这里分岔：
    ///   · **棱柱**：底面整体**平移**一个侧棱向量（顶面和底面全等）；
    ///   · **棱锥**：`n` 个点**全都落在顶点上**（顶面退化成一个点，所以没有顶面那一圈）；
    ///   · **棱台**：从顶点**按固定比例缩回来**（顶面和底面同形、小一圈，所以侧棱会"收"）。
    ///
    /// ⚠ 三支都算完再返回，调用方不用再判一次种类——"侧棱 = 底点 → 顶面同号那个点"
    /// 这条规则对三兄弟**都成立**（棱锥时那些点全部等于顶点，正好就是"底点 → 顶点"）。
    ///
    /// internal 而不是 private：自检要直接数它（"棱锥的 n 个顶面点全等吗"、
    /// "棱台的上底是不是按比例缩的"——这两条从画出来的棱上反推很别扭）。
    /// </summary>
    internal Vector2[] PrismTopLocal()
    {
        var b = PrismBaseLocal();
        int n = b.Length;
        var top = new Vector2[n];

        if (Kind == StrokeKind.Pyramid)
        {
            var apex = PrismApexLocal();
            for (int k = 0; k < n; k++) top[k] = apex;
            return top;
        }

        if (Kind == StrokeKind.Frustum)
        {
            // 上底 = 顶点 ＋（下底的点 − 底心）× 比例：同形缩小的那一份（见 ShapeSpec.FrustumTopScale）。
            var c = PrismBaseCenterLocal();
            var apex = PrismApexLocal();
            float k0 = ShapeSpec.FrustumTopScale;
            for (int k = 0; k < n; k++) top[k] = apex + (b[k] - c) * k0;
            return top;
        }

        var lat = PrismLateralLocal();                     // 棱柱：平移（也是默认那一支）
        for (int k = 0; k < n; k++) top[k] = b[k] + lat;
        return top;
    }

    /// <summary>**第 1 笔**：底面外接框（归一成 左上 / 右下，和长方体那一笔同一套）。</summary>
    public void SetPrismBase(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        SetPoint(0, new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)));
        SetPoint(1, new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)));
    }

    /// <summary>
    /// **第 2 笔**：顶面中心（拖到哪就是哪）。侧棱向量由它和底心相减得到。
    /// 和长方体的第 2 笔**不同**：那边只吃竖直分量、方向恒定 45°；这边**方向也吃**，
    /// 因为"直还是斜"就是由它定的（用户要的就是这个）。
    /// </summary>
    public void SetPrismApex(float x, float y)
    {
        while (Points.Count < 3) AddPoint(x, y, 1f, 0);
        SetPoint(2, new Vector2(x, y));
    }

    /// <summary>
    /// 把"顶面中心"**吸到竖直**（直棱柱）——只改 x，y 一个字不动。
    /// 由引擎在拖第二笔时先探一次（见 Engine 的 `ApplyStep`），
    /// 所以落进模型的就已经是吸过的位置了（和"拖顶点吸附"同一个口径：吸完才写模型）。
    /// </summary>
    public void SnapPrismApexVertical()
    {
        if (Points.Count < 3) return;
        var p = CurvePointLocal(2);
        SetPoint(2, new Vector2(PrismBaseCenterLocal().X, p.Y));
    }

    /// <summary>
    /// **棱柱／棱锥／棱台这一族的棱**（`hidden = false` 给看得见的、`true` 给被挡住的）。
    ///
    /// 三兄弟共用这一份：底面都是那个正 n 边形，区别只在"顶面"是什么
    /// （棱柱＝平移一份、棱锥＝缩成一点、棱台＝缩小一份，见 <see cref="PrismTopLocal"/>）。
    /// 画出三条线族：
    ///   · **底面的边** n 条；**顶面的边** n 条（棱锥没有——顶面只有一点）；
    ///   · **侧棱** n 条（= 底面的点 → 顶面同号那个点）。
    ///
    /// 实 / 虚的判据**只有一条、而且是通用的**（计划-图形工具.md §32.4）——
    /// 因为底面是"俯视压扁 ＋ 错切"画的，**"远近"就等于屏幕上的上下**：
    ///   · **底面某条边**：中点在底心**下方** → 近侧、看得见；否则被挡住（虚线）；
    ///   · **某条侧棱**：它相邻的两个侧面**只要有一个看得见** → 这条棱看得见；
    ///   · **顶面**：全部看得见（俯视角度下顶面恒可见）。
    ///
    /// 这条判据对**斜棱柱 / 斜棱锥 / 斜棱台**同样成立：侧面的朝向只由底面那条边定，
    /// 和上面歪不歪无关。
    /// 长方体现在还是硬编码三条虚线（它是另一个朝向的画法，见 CuboidEdges），**两边不共用**——
    /// 硬编码那三条是照 InkClass 抄的，换成通用判据会改变长方体的既有外观（那是不许动的）。
    ///
    /// 渲染与熔墨共用这一份（见 <see cref="InkPieces"/>）。自检也数它（`--prismtest` 数
    /// "几条实线几条虚线"），所以是 internal 而不是 private。
    /// </summary>
    internal List<InkPiece> PrismFamilyEdges(bool hidden)
    {
        var list = new List<InkPiece>();
        if (Points.Count < 2) return list;

        var b = PrismBaseLocal();
        var lat = PrismLateralLocal();
        int n = b.Length;

        // **还没拖第二笔**（侧棱还是零）：只画底面那一圈、全实线——
        // 这是第 1 笔的预览（拖出来是个扁的正 n 边形，正好告诉用户"底面画好了"）。
        // 不给它走下面那套判据：那时"顶面"和"底面"重合，虚实分出来的线会叠在一起。
        // （棱锥／棱台同样走这一支：顶点还没定，画出来就该只是那个底面。）
        if (lat.LengthSquared() < 1e-6f)
        {
            if (!hidden)
            {
                var ring = new List<Vector2>(n + 1);
                for (int k = 0; k <= n; k++) ring.Add(b[k % n]);
                list.Add(new InkPiece(ring, false));
            }
            return list;
        }

        var top = PrismTopLocal();
        // 顶面那一圈要不要画：棱锥的顶面退化成一个点，画"面"就成了一个点上的圈（看着像个墨疙瘩）。
        bool hasTopRing = Kind != StrokeKind.Pyramid;

        // 侧面 k（= 底边 k）看得见吗：判据在 `ShapeSpec.PrismFaceVisible`
        //（"外法向朝向观察者"，**错切要算进去**——那是斜二测的四棱柱"右面可见"的来源）。
        var (bx0, by0, bx1, by1) = PrismBaseBoxLocal();
        float rx = MathF.Max(0.5f, (bx1 - bx0) * 0.5f);
        float ry = MathF.Max(0.5f, (by1 - by0) * 0.5f);
        float offset = ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f;
        var faceVisible = new bool[n];
        for (int k = 0; k < n; k++)
        {
            int nx = (k + 1) % n;
            float a0 = offset + k * MathF.Tau / n, a1 = offset + nx * MathF.Tau / n;
            faceVisible[k] = ShapeSpec.PrismFaceVisible(
                (MathF.Cos(a0) + MathF.Cos(a1)) * 0.5f,
                (MathF.Sin(a0) + MathF.Sin(a1)) * 0.5f, rx, ry);
        }

        void Add(Vector2 p, Vector2 q, bool hid)
        {
            if (hid != hidden) return;      // 这一趟只要这一类
            list.Add(new InkPiece(new List<Vector2> { p, q }, hid));
        }

        for (int k = 0; k < n; k++)
        {
            int nx = (k + 1) % n;
            Add(b[k], b[nx], !faceVisible[k]);                  // 底面的边
            if (hasTopRing) Add(top[k], top[nx], false);        // 顶面的边：恒实线
            // 侧棱 k：相邻的两个侧面（k−1 与 k）有一个看得见，这条棱就在轮廓上。
            // 棱锥这里 top[k] 就是顶点，所以自动成了"底点 → 顶点"。
            Add(b[k], top[k], !(faceVisible[k] || faceVisible[(k + n - 1) % n]));
        }
        return list;
    }

    private ID2D1PathGeometry BuildPrism(ID2D1Factory1 factory, bool hidden)
    {
        if (Points.Count < 2) return hidden ? null : BuildLine(factory);
        var pieces = PrismFamilyEdges(hidden);
        return pieces.Count == 0 ? null : FromPieces(factory, pieces);
    }

    /// <summary>椭圆上 `u` 处的点（`u ∈ [0,1)`：0 = 右、0.25 = 下、0.5 = 左、0.75 = 上；屏幕 y 向下）。</summary>
    private static Vector2 EllipseArcPoint(Vector2 c, float rx, float ry, float u)
    {
        float a = u * MathF.Tau;
        return new Vector2(c.X + rx * MathF.Cos(a), c.Y + ry * MathF.Sin(a));
    }

    /// <summary>
    /// 立体图形的**逐笔点列**（`hidden = false` 给看得见的、`true` 给被挡住的）：
    ///   · **圆柱**：顶面整圈 ＋ 底面下半圈 ＋ 两条母线；被挡住的是底面上半圈；
    ///   · **圆锥**：底面下半圈 ＋ 两条母线（顶点 = 上边中点）；被挡住的是底面上半圈；
    ///   · **圆台**：上底整圈（小一圈）＋ 底面下半圈 ＋ 两条**收进去**的母线；被挡住的同样；
    ///   · **球**：轮廓**整圆** ＋ 赤道椭圆的**下半圈**；被挡住的是赤道的**上半圈**
    ///     （远侧那半圈被球自己挡着）。
    ///
    /// **渲染与熔墨共用这一份**（<see cref="BuildSolid"/> / <see cref="BuildSolidHidden"/> /
    /// <see cref="InkPieces"/>）——两边各写一套就会"画出来的和熔出来的不一样"，
    /// 那正是用户 2026-09-20 反馈的那类问题（计划-图形工具.md §25）。
    /// </summary>
    private List<InkPiece> SolidPieces(bool hidden)
    {
        var list = new List<InkPiece>();

        // **球先单独走**：它的圆不是从"上下两个椭圆"派生的（见 SphereLocal 那段），
        // 所以不能落进下面那套 `SolidEllipsesLocal` 的算法里。
        if (Kind == StrokeKind.Sphere)
        {
            var (sc, sr) = SphereLocal();
            var (_, eqRx, eqRy) = SphereEquatorLocal();
            if (hidden)
            {
                list.Add(new InkPiece(ArcPoints(sc, eqRx, eqRy, 0.5f, 1f), true));   // 赤道远侧
            }
            else
            {
                list.Add(new InkPiece(ArcPoints(sc, sr, sr, 0f, 1f), false));        // 轮廓整圆
                list.Add(new InkPiece(ArcPoints(sc, eqRx, eqRy, 0f, 0.5f), false));  // 赤道近侧
            }
            return list;
        }

        var (cx, topCy, botCy, rx, ry) = SolidEllipsesLocal();
        var bot = new Vector2(cx, botCy);

        if (hidden)
        {
            // 底面**上半圈**（`u` 从 0.5 走到 1，走的是 −y 那一侧 = 屏幕上方）：
            // 从上面看下去它是被实体挡住的，课本上就画虚线。
            // **三种旋转体共用这一条**——圆台也一样（被挡住的永远只有下底的上半圈）。
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0.5f, 1f), true));
            return list;
        }

        if (Kind == StrokeKind.Cylinder)
        {
            // 顶面整圈：`u` 从 0 走到 1（闭合）
            list.Add(new InkPiece(ArcPoints(new Vector2(cx, topCy), rx, ry, 0f, 1f), false));
            // 底面**下半圈**（u: 0 → 0.5 走的是 +y 那一侧 = 屏幕上看得见的下半圈）
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0f, 0.5f), false));
            // 两条母线：底面左右端点 → 顶面左右端点
            list.Add(new InkPiece(new List<Vector2> { new(cx - rx, topCy), new(cx - rx, botCy) }, false));
            list.Add(new InkPiece(new List<Vector2> { new(cx + rx, topCy), new(cx + rx, botCy) }, false));
        }
        else if (Kind == StrokeKind.ConeFrustum)
        {
            // 上底整圈：**小一圈的那个椭圆**（圆心 / 半轴的算式只有一处，见 ConeFrustumTopLocal）
            var (tc, trx, try_) = ConeFrustumTopLocal();
            list.Add(new InkPiece(ArcPoints(tc, trx, try_, 0f, 1f), false));
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0f, 0.5f), false));
            // 两条母线：底面左右端点 → **上底**左右端点（所以它们是往中间收的斜线）
            list.Add(new InkPiece(new List<Vector2> { new(cx - rx, botCy), new(tc.X - trx, tc.Y) }, false));
            list.Add(new InkPiece(new List<Vector2> { new(cx + rx, botCy), new(tc.X + trx, tc.Y) }, false));
        }
        else
        {
            var apex = ConeApexLocal();
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0f, 0.5f), false));
            // 两条母线：底面左右端点 → 顶点
            list.Add(new InkPiece(new List<Vector2> { new(cx - rx, botCy), apex }, false));
            list.Add(new InkPiece(new List<Vector2> { new(cx + rx, botCy), apex }, false));
        }
        return list;
    }

    /// <summary>立体图形的**实线几何**（看得见的那几笔）——点列在 <see cref="SolidPieces"/>。</summary>
    private ID2D1PathGeometry BuildSolid(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);
        return FromPieces(factory, SolidPieces(hidden: false));
    }

    /// <summary>
    /// 立体图形**被挡住的那半圈**（虚线，走辅助几何槽 —— 和双曲线的渐近线共用一个槽）。
    /// 点列在 <see cref="SolidPieces"/>（`hidden: true`）。
    /// </summary>
    private ID2D1PathGeometry BuildSolidHidden(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return null;
        return FromPieces(factory, SolidPieces(hidden: true));
    }

    /// <summary>
    /// **长方体的棱**（`hidden = false` 画看得见的、`true` 画被挡住的）。
    ///
    /// 八个顶点由"正面矩形 ＋ 深度 d"派生（深度方向恒定**右上 45°**，照 InkClass）。
    /// 实 / 虚的分配也照他：正面四条边 ＋ 背面**上横 / 右竖** ＋ **左上 / 右上 / 右下**三条斜棱是实线；
    /// 背面**下横**、**左下斜棱**、背面**左竖**是虚线（从正面看过去被挡住）。
    ///
    /// 渲染与熔墨共用这一份（见 <see cref="InkPieces"/>）。
    /// </summary>
    private List<InkPiece> CuboidEdges(bool hidden)
    {
        var list = new List<InkPiece>();
        var (x0, y0, x1, y1) = CuboidFrontLocal();
        float d = CuboidDepthLocal();

        void Edge(Vector2 a, Vector2 b) => list.Add(new InkPiece(new List<Vector2> { a, b }, false));
        void Hidden(Vector2 a, Vector2 b) => list.Add(new InkPiece(new List<Vector2> { a, b }, true));

        if (!hidden)
        {
            // 正面矩形（第 1 笔画的四条边）
            Edge(new(x0, y0), new(x1, y0));
            Edge(new(x1, y0), new(x1, y1));
            Edge(new(x1, y1), new(x0, y1));
            Edge(new(x0, y1), new(x0, y0));
            // 背面：上横、右竖
            Edge(new(x0 + d, y0 - d), new(x1 + d, y0 - d));
            Edge(new(x1 + d, y0 - d), new(x1 + d, y1 - d));
            // 斜棱：左上、右上、右下
            Edge(new(x0, y0), new(x0 + d, y0 - d));
            Edge(new(x1, y0), new(x1 + d, y0 - d));
            Edge(new(x1, y1), new(x1 + d, y1 - d));
        }
        else
        {
            // 被挡住的：背面下横、左下斜棱、背面左竖
            Hidden(new(x0 + d, y1 - d), new(x1 + d, y1 - d));
            Hidden(new(x0, y1), new(x0 + d, y1 - d));
            Hidden(new(x0 + d, y0 - d), new(x0 + d, y1 - d));
        }
        return list;
    }

    private ID2D1PathGeometry BuildCuboid(ID2D1Factory1 factory, bool hidden)
    {
        if (Points.Count < 2) return BuildLine(factory);
        return FromPieces(factory, CuboidEdges(hidden));
    }

    /// <summary>
    /// **四面体**的棱：底面三角形三条边 ＋ 顶点到底面三顶点的三条棱，**全是实线**（照 InkClass）。
    /// 渲染与熔墨共用这一份。
    /// </summary>
    private List<InkPiece> TetraEdges()
    {
        var list = new List<InkPiece>();
        var p0 = CurvePointLocal(0);
        var p1 = CurvePointLocal(1);
        var p2 = CurvePointLocal(2);
        var apex = TetraApexLocal();
        void Edge(Vector2 a, Vector2 b) => list.Add(new InkPiece(new List<Vector2> { a, b }, false));
        Edge(p0, p1);       // 底面三条边
        Edge(p1, p2);
        Edge(p2, p0);
        Edge(p0, apex);     // 三条棱
        Edge(p1, apex);
        Edge(p2, apex);
        return list;
    }

    private ID2D1PathGeometry BuildTetra(ID2D1Factory1 factory)
    {
        if (Points.Count < 3) return BuildLine(factory);
        return FromPieces(factory, TetraEdges());
    }

    private ID2D1PathGeometry BuildArrow(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var (_, wingA, wingB) = ArrowHeadPoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.EndFigure(FigureEnd.Open);
        sink.BeginFigure(wingA, FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.AddLine(wingB);
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 箭头头部的三个关键点（**局部坐标**）：轴线上的"根部"和两个翅膀尖。
    ///
    /// 抽出来是因为它有三个用户，**必须永远一致**：
    ///   ① <see cref="BuildArrow"/>——真正画出来的那条折线；
    ///   ② <see cref="ShapeOutline"/>——橡皮/框选/套索用的轮廓折线；
    ///   ③ 墨迹范围（<see cref="LineLikeInkBounds(Vector2, Vector2, in Matrix3x2)"/>）——箭头那两个
    ///      翅膀尖**不在**"两个端点之间"，只按端点算，翅膀就会落在框外，而框又是脏区/命中/
    ///      导出裁切的依据：脏区漏掉那一块屏幕上会留残影（这几条账见 PaddedBounds 的注释）。
    /// 三处各写一份公式是迟早要咬人的（头部长短、张开比例都在这几行里）。
    ///
    /// 端点由调用方给（而不是自己读 `Points`）：拖端点的手势里要按**预览端点**算，
    /// 这样画出来的箭头、框、脏区才是同一个箭头。
    /// </summary>
    private (Vector2 root, Vector2 wingA, Vector2 wingB) ArrowHeadPoints(Vector2 a, Vector2 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        float head = Math.Clamp(len * 0.28f, 10f, 48f);
        var root = new Vector2(b.X - dx * head, b.Y - dy * head);
        float nx = -dy, ny = dx;
        float spread = head * 0.45f;
        return (root,
                new Vector2(root.X + nx * spread, root.Y + ny * spread),
                new Vector2(root.X - nx * spread, root.Y - ny * spread));
    }

    /// <summary>箭头头部几何，端点取模型自己的首尾两点（画箭头 / 算轮廓用）。</summary>
    private (Vector2 root, Vector2 wingA, Vector2 wingB) ArrowHeadPoints()
    {
        var (a, b) = Endpoints();
        return ArrowHeadPoints(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
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
        => BuildCenterlineCore(factory, float.MaxValue);

    /// <summary>
    /// 回放用：只画**前 maxParam**（点序号，0..Points.Count-1，可带小数）那一段中心线。
    /// **不进几何缓存**（每帧都在变，缓存只会不停重建）——调用方画完负责 Dispose。
    /// </summary>
    internal ID2D1PathGeometry BuildCenterlinePrefix(ID2D1Factory1 factory, float maxParam)
        => BuildCenterlineCore(factory, maxParam);

    private ID2D1PathGeometry BuildCenterlineCore(ID2D1Factory1 factory, float maxParam)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();

        bool clipped = maxParam < Points.Count - 1 - 1e-4f;

        // **每条剩下的段一个 figure，但它们在同一条几何里**——这一点是关键：
        // 一次 DrawGeometry 只混合一次，所以半透明荧光笔即使自相重叠也不会变深。
        // 拆成两个对象（两个 DrawGeometry）就会混合两次（实测差 0 → 56）。
        foreach (var (a, b0) in RemainingRuns())
        {
            if (clipped && a >= maxParam - 1e-6f) break;   // 这一段整个在前缀之后：不画
            float b = clipped ? MathF.Min(b0, maxParam) : b0;

            // **墨迹模型（实验，`--motion`）**：整条用选中的运动模型输出当中心线。
            // 只对"没被橡皮擦过、也不是回放前缀"的整笔生效；擦除/回放照样走旧路
            //（擦除区间是按原始点切的，建模点和参数序号对不上——见 StrokeMotion 的注释）。
            // 上游输出点密度足够（≥180Hz），直接当折线描边即可（弦高误差远小于 1px）。
            if (!clipped && Erased.Count == 0 && StrokeMotion.Build(this))
            {
                // mean2 的曲线层固定为**过点曲线**（拟合档已随停用清理，2026-10-05）。
                bool drew = AppendSmoothedModeledRun(sink);
                if (!drew)
                {
                    int mn = StrokeMotion.Count;
                    var p0 = StrokeMotion.At(0);
                    sink.BeginFigure(new Vector2(p0.X, p0.Y), FigureBegin.Hollow);
                    for (int k = 1; k < mn; k++)
                    {
                        var p = StrokeMotion.At(k);
                        sink.AddLine(new Vector2(p.X, p.Y));
                    }
                    // 活笔笔尖镜像（A2）：只进渲染几何、不进 `cache.Out` 的临时延伸。
                    var tip = StrokeMotion.TipOverlay;
                    for (int k = 0; k < tip.Count; k++)
                        sink.AddLine(new Vector2(tip[k].X, tip[k].Y));
                }
            }
            else
            {
                // 曲线化（`--smooth`）：把这一段 run 的采样点喂给曲线器，输出一串三次贝塞尔。
                // **点还是原来那些点**——曲线严格过每一个采样点，直角由角点保护保住；
                // 不生效时（开关关着 / 段数不够）原样退回下面的折线路径。
                bool smoothed = StrokeSmoothing.Enabled && !RawWhileLive && AppendSmoothedRun(sink, a, b);
                if (!smoothed)
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
                }
            }
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    /// <summary>
    /// mean2：把建模输出喂进过点曲线，直接写成三次贝塞尔。
    /// 活笔时再接上**笔尖镜像**（A2，`StrokeMotion.TipOverlay`）——与收笔追赶同序同值，
    /// 保证活笔末帧＝成稿首帧；镜像不进模型输出，开关它成稿逐点不变。
    /// 返回 false = 段数不够，调用方退回直线折线。
    /// </summary>
    private static bool AppendSmoothedModeledRun(ID2D1GeometrySink sink)
    {
        StrokeSmoothing.Begin();
        int n = StrokeMotion.Count;
        for (int i = 0; i < n; i++)
        {
            var p = StrokeMotion.At(i);
            StrokeSmoothing.Add(p.X, p.Y, p.Z);
        }
        var tip = StrokeMotion.TipOverlay;
        for (int i = 0; i < tip.Count; i++)
            StrokeSmoothing.Add(tip[i].X, tip[i].Y, tip[i].Z);
        int m = StrokeSmoothing.Finish();
        if (m <= 0) return false;

        var segs = StrokeSmoothing.Out;
        sink.BeginFigure(segs[0].P0, FigureBegin.Hollow);
        for (int k = 0; k < m; k++)
            sink.AddBezier(new BezierSegment(segs[k].C1, segs[k].C2, segs[k].P1));
        return true;
    }

    /// <summary>
    /// 曲线化的那一支：把 run `[a, b]` 的采样点（含两端可能被橡皮切出来的插值点）
    /// 喂给 <see cref="StrokeSmoothing"/>，写成三次贝塞尔。
    /// 返回 false = 段数不够、什么都没写（调用方退回折线，所以这里**不许先动 sink**）。
    /// </summary>
    private bool AppendSmoothedRun(ID2D1GeometrySink sink, float a, float b)
    {
        StrokeSmoothing.Begin();

        var start = PointAtParam(a);
        StrokeSmoothing.Add(start.X, start.Y, PressureAtParam(a));
        int i0 = (int)MathF.Floor(a) + 1;
        int i1 = (int)MathF.Floor(b);
        for (int i = i0; i <= i1 && i < Points.Count; i++)
            StrokeSmoothing.Add(Points[i].X, Points[i].Y, Points[i].P);
        if (MathF.Abs(b - MathF.Round(b)) > 1e-6f)
        {
            var end = PointAtParam(b);
            StrokeSmoothing.Add(end.X, end.Y, PressureAtParam(b));
        }

        int n = StrokeSmoothing.Finish();
        if (n <= 0) return false;

        var segs = StrokeSmoothing.Out;
        sink.BeginFigure(segs[0].P0, FigureBegin.Hollow);
        for (int k = 0; k < n; k++)
            sink.AddBezier(new BezierSegment(segs[k].C1, segs[k].C2, segs[k].P1));
        return true;
    }

    /// <summary>
    /// 单点笔迹 = 一个圆点。**不能靠描边**：零长度的线描出来什么都没有，
    /// 所以直接给一个半径为半个笔宽的圆，渲染那一侧会填充它。
    ///
    /// 有压感时半径**取这一点的压力**：轻轻一点是一个小点、按重了是一个大点
    /// （和真笔一样；等宽的写法会让"点一下"永远是一个固定大小的圆）。
    /// </summary>
    private ID2D1Geometry BuildDot(ID2D1Factory1 factory)
    {
        float rad = MathF.Max(1f, HasPressure && PressureWidth.Enabled
            ? PressureWidth.HalfWidth(Width, Points[0].P)
            : Width * 0.5f);
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
    /// 这条动作**自己往 `doc.Dirty` 里加矩形**，不要 `Commit` / `Undo` / `Redo`
    /// 再用 <see cref="AffectedUnion"/> 补一次。默认 false。
    ///
    /// 现在只有"改几何"（拖直线端点）那一条为真：它要加的是"旧位、新位**各一个**矩形"，
    /// 而不是两者的**并集**——对一条横跨屏幕的直线，并集几乎等于整屏（≈96 块），
    /// 而旧、新各一个各只压十几块，差约 8 倍（见 计划-图形工具.md 8.1②）。
    /// </summary>
    public virtual bool SelfManagesDirty => false;

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
/// **停顿成型**：把用户手画的那一笔换成规整图形，**一步撤销、撤了回到手绘原迹**
///（见 计划-图形工具.md §四十二）。
///
/// 为什么不用 <see cref="AddStrokesAction"/>：那条路撤销之后是"图形没了、什么都不剩"——
/// 用户手画的那一笔就永远找不回来了。而"停顿变"是**默认开**的功能，变的又不是用户明确
/// 要的形状（他只是在写字 / 画草图），所以必须留一条"这是我看错了"的退路：
/// 按一次 Ctrl+Z 回到**自己画的那一笔**，而不是回到空白。
/// （InkClass 的"替换型历史"就是干这个的，见 §42.1。）
///
/// 注意手绘原迹**从来没进过文档**（停顿是在笔还按着的时候就触发、当场把它换掉的），
/// 所以它只活在这条记录里 —— `HeldStrokes` 因此是 2。
/// </summary>
internal sealed class DwellShapeAction : EditAction
{
    public Stroke Shape;      // 变出来的图形（已经在文档里）
    public Stroke Ink;        // 手绘原迹
    public int Index;         // 提交时的层序：撤销要把原迹放回**同一个位置**

    /// <summary>**两笔成型**时的**第一笔原迹**（用户 2026-09-25 定的"画两支就是双曲线"）。
    /// 非 null 时：撤销要**一步把两笔都放回去**（"一步回两笔手绘"），重做要**一步把两笔都收走**。
    /// 为什么不开一个新动作类：这条路和"一笔 → 一个图形"**是同一条路**，
    /// 只多了一笔原迹 —— 新开一个类等于把同一段记账抄一遍（同一个坑这个仓库踩过好几次）。
    /// </summary>
    public Stroke Ink2;
    public int Index2;

    public override int HeldStrokes => Ink2 == null ? 2 : 3;

    public override void Undo(InkDocument doc)
    {
        doc.RemoveStroke(Shape);
        // 先放第二笔、再放第一笔 —— 和提交时"先收第一笔"的顺序**反着来**，
        // 这样两笔回到文档里仍是原来那个相对次序。
        if (Ink2 != null) doc.InsertStroke(Index2, Ink2);
        doc.InsertStroke(Index, Ink);
    }

    public override void Redo(InkDocument doc)
    {
        doc.RemoveStroke(Ink);
        if (Ink2 != null) doc.RemoveStroke(Ink2);
        doc.InsertStroke(Index, Shape);
    }

    // 两个方向都要重绘（旧位置擦、新位置画），所以前后**都**算上：
    // 少了任何一半，屏幕上都会留一条"擦不掉的旧墨"或"看不见的新墨"。
    public override RectF AffectedBefore =>
        Ink2 == null ? EditRegion.Of(new[] { Ink }) : EditRegion.Of(new[] { Ink, Ink2 });
    public override RectF AffectedAfter => EditRegion.Of(new[] { Shape });
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

        /// <summary>
        /// 图形熔成墨时**熔出来的那一整组**（<see cref="S"/> 只是其中的第一笔，只为让
        /// 调用方能用 <c>Strokes.IndexOf</c> 找到位置）。null = 这条记录不是"熔图形"来的。
        ///
        /// 为什么要一整组：一个图形可能对应屏幕上好几笔（长方体 12 条棱、圆柱 5 笔、
        /// 双曲线两支＋两条渐近线），熔出来必须逐笔对应才不会"虚线变实线 / 凭空多出线"
        /// （见 <see cref="Stroke.MeltToInkParts"/>）。
        /// </summary>
        public List<Stroke> Melted;
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
/// <summary>
/// 给一批对象改**一个属性**（颜色 / 粗细 / 锁定）。三条共用一个动作：
/// 都是"记下旧值 → 改 → 撤销时写回"，差别只在写哪个字段。
///
/// 脏区按"改前 ∪ 改后"取并集（粗细变了墨会往外长一圈），和 TransformObjectsAction
/// 同一个模式；改完置 StructureChangedSinceRender——内容层的像素真的变了，
/// 不能走"只补画新笔画"那条快路径。
/// </summary>
internal sealed class SetStrokePropAction : EditAction
{
    public enum Prop { Color, Width, Lock, Dash, Grid }

    private readonly Stroke[] _targets;
    private readonly Prop _prop;
    private readonly Color4[] _oldColor;
    private readonly Color4[] _newColor;      // 每条各自的"新颜色"（荧光笔要转半透明，见下）
    private readonly float[] _oldWidth;
    private readonly bool[] _oldLock;
    private readonly bool[] _oldGrid;
    private readonly StrokeDash[] _oldDash;
    private float _width;                     // 拖动中会被 RetargetWidth 改（8.2.0）
    private readonly bool _lock;
    private readonly bool _grid;
    private readonly StrokeDash _dash;
    private readonly RectF _before;

    /// <summary>
    /// 改颜色。**新颜色按条给**（不是统一一个值）：荧光笔要保留半透明
    /// （`InkPalette.ToHighlighter`），普通墨迹用不透明的基色——同一批里两种笔混着选时，
    /// 一个色值套不下去。
    /// </summary>
    public SetStrokePropAction(IReadOnlyList<Stroke> targets, Color4[] newColors)
        : this(targets)
    {
        _prop = Prop.Color;
        _oldColor = new Color4[_targets.Length];
        _newColor = new Color4[_targets.Length];
        for (int i = 0; i < _targets.Length; i++)
        {
            _oldColor[i] = _targets[i].Color;
            _newColor[i] = i < newColors.Length ? newColors[i] : newColors[^1];
        }
    }

    public SetStrokePropAction(IReadOnlyList<Stroke> targets, float width)
        : this(targets) { _prop = Prop.Width; _width = width; _oldWidth = new float[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldWidth[i] = _targets[i].Width; }

    public SetStrokePropAction(IReadOnlyList<Stroke> targets, bool locked)
        : this(targets) { _prop = Prop.Lock; _lock = locked; _oldLock = new bool[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldLock[i] = _targets[i].Locked; }

    /// <summary>
    /// 改线型（<see cref="StrokeDash"/>）。
    /// **按条记旧值**：一批里可能实线/虚线混着选，撤销时要各自回到各自的原样。
    /// </summary>
    public SetStrokePropAction(IReadOnlyList<Stroke> targets, StrokeDash dash)
        : this(targets) { _prop = Prop.Dash; _dash = dash; _oldDash = new StrokeDash[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldDash[i] = _targets[i].Dash; }

    /// <summary>
    /// 改**坐标系网格**开关（见 <see cref="Stroke.Grid"/>）。
    ///
    /// **为什么要多传一个 <paramref name="tag"/>**：C# 的重载只看参数类型，
    /// 而"锁定"那条也是 `(targets, bool)`——两个语义撞在同一个签名上，编译器分不出来。
    /// 传一个哑参数逼调用方把意图写清楚，免得"点了一下网格，结果对象被锁上了"。
    /// </summary>
    public SetStrokePropAction(IReadOnlyList<Stroke> targets, bool grid, Prop tag)
        : this(targets) { _prop = tag; _grid = grid; _oldGrid = new bool[_targets.Length];
                          for (int i = 0; i < _targets.Length; i++) _oldGrid[i] = _targets[i].Grid; }

    private SetStrokePropAction(IReadOnlyList<Stroke> targets)
    {
        _targets = new Stroke[targets.Count];
        for (int i = 0; i < targets.Count; i++) _targets[i] = targets[i];
        _before = EditRegion.Of(_targets);
    }

    public override RectF AffectedBefore => _before;
    public override RectF AffectedAfter => EditRegion.Of(_targets);

    /// <summary>这一批里有几个对象（控制台留痕 / 自检用）。</summary>
    public int TargetCount => _targets.Length;

    /// <summary>
    /// **拖动中更新目标粗细**（8.2.0 面板滑条：同一个动作反复 `Redo`、松手才提交——
    /// 「一次拖拽 = 一步撤销」）。用同一个动作对象，才能保证撤销回到**按下那一刻**的值。
    /// </summary>
    public void RetargetWidth(float width) => _width = width;

    /// <summary>
    /// **拖动中更新目标颜色**（自定义取色板用，同 <see cref="RetargetWidth"/> 那一套）。
    /// **按条重算**：荧光笔要保留半透明（见 `InkPalette.ForStroke`），不能一个色值套下去。
    /// </summary>
    public void RetargetColor(Color4 baseColor)
    {
        for (int i = 0; i < _targets.Length; i++)
            _newColor[i] = InkPalette.ForStroke(baseColor, _targets[i]);
    }

    /// <summary>
    /// 这个动作**真的会改变什么吗**（拖拽松手时用它决定要不要进撤销栈：
    /// 按下去没动、或者选的就是同一个值，不该留一步空撤销）。
    /// </summary>
    public bool HasChange
    {
        get
        {
            switch (_prop)
            {
                case Prop.Width:
                    foreach (float old in _oldWidth)
                        if (MathF.Abs(old - _width) > 0.001f) return true;
                    return false;
                case Prop.Color:
                    for (int i = 0; i < _targets.Length; i++)
                        if (!_oldColor[i].Equals(_newColor[i])) return true;
                    return false;
                default:
                    return true;
            }
        }
    }

    public override void Redo(InkDocument doc) => Apply(doc, old: false);
    public override void Undo(InkDocument doc) => Apply(doc, old: true);

    private void Apply(InkDocument doc, bool old)
    {
        for (int i = 0; i < _targets.Length; i++)
        {
            var s = _targets[i];
            doc.Dirty.Add(s.PaddedBounds);                 // 旧样子要擦
            switch (_prop)
            {
                case Prop.Color: s.Color = old ? _oldColor[i] : _newColor[i]; break;
                case Prop.Width: s.Width = old ? _oldWidth[i] : _width; break;
                case Prop.Lock:  s.Locked = old ? _oldLock[i] : _lock; break;
                case Prop.Dash:  s.Dash = old ? _oldDash[i] : _dash; break;
                case Prop.Grid:  s.Grid = old ? _oldGrid[i] : _grid; break;
            }
            // 颜色/粗细会改墨迹范围（半宽），必须让 InkBounds 的缓存失效；
            // **网格也会**：它改的是形状本身（多画一堆线），几何得重建；
            // 锁定不改外观、线型也不改形状（只改"怎么描这条边"），这两样不用重算。
            if (_prop is Prop.Color or Prop.Width or Prop.Grid) s.InvalidateMetrics();
            doc.Dirty.Add(s.PaddedBounds);                 // 新样子要画
        }
        doc.StructureChangedSinceRender = true;
        doc.Version++;
    }
}

/// <summary>
/// 层级：把选中的这一批整体移到**最上**（置顶）或**最下**（置底），内部相对顺序不变。
/// 只做这两个动作，不做"上移/下移一格"（见 调研-选中框-反馈-导出-层级-属性.md 第三节）。
/// 撤销 = 按原下标插回去。
/// </summary>
internal sealed class ReorderStrokesAction : EditAction
{
    private readonly Stroke[] _targets;
    private readonly bool _toFront;
    private readonly List<(int index, Stroke s)> _old = new();
    private readonly RectF _bounds;

    public ReorderStrokesAction(IReadOnlyList<Stroke> targets, bool toFront)
    {
        _toFront = toFront;
        _targets = new Stroke[targets.Count];
        for (int i = 0; i < targets.Count; i++) _targets[i] = targets[i];
        _bounds = EditRegion.Of(_targets);
    }

    public override RectF AffectedBefore => _bounds;
    public override RectF AffectedAfter => _bounds;

    public override void Redo(InkDocument doc)
    {
        _old.Clear();
        foreach (var s in _targets) _old.Add((doc.Strokes.IndexOf(s), s));
        _old.Sort((a, b) => a.index.CompareTo(b.index));

        foreach (var it in _old) doc.Strokes.Remove(it.s);
        if (_toFront) { foreach (var it in _old) doc.Strokes.Add(it.s); }
        else { for (int i = _old.Count - 1; i >= 0; i--) doc.Strokes.Insert(0, _old[i].s); }
        Touch(doc);
    }

    public override void Undo(InkDocument doc)
    {
        foreach (var it in _old) doc.Strokes.Remove(it.s);
        foreach (var it in _old)                       // 从小到大插，下标才不会互相挤
            doc.Strokes.Insert(Math.Min(it.index, doc.Strokes.Count), it.s);
        Touch(doc);
    }

    private void Touch(InkDocument doc)
    {
        // 顺序变了：内容层"只差几条新笔画"的前提不成立，碰到的块要整块重画。
        doc.StructureChangedSinceRender = true;
        doc.Version++;
        doc.Dirty.Add(_bounds);                        // 重叠的那些像素才可能变
    }
}

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
/// **改几何**：把一条图形的控制点整批换掉（这一轮只服务"拖直线 / 箭头的端点"）。
///
/// 和 <see cref="TransformObjectsAction"/> 是**两条不许混的路**（见 硬约束）：
///   · 那边只改 `Transform` 矩阵，几何一个点都不动 → GPU 几何缓存不用失效；
///   · 这边改的是几何本身 → `Revision` 跟着变，Direct2D 几何缓存**必须**重建，
///     空间索引也要重摆（端点挪走了，它占的格子变了）。
///
/// 三个实现上的要点（都在 计划-图形工具.md 8.1）：
///   ① 拖动期不碰模型，只有**松手**才构造并应用这条动作 → 一次拖拽 = 一步撤销；
///   ② 脏区加"旧位、新位**各一个**矩形"，不用并集（见 SelfManagesDirty 的注释）；
///   ③ 端点存的是**局部坐标**，所以外面提交之前要先过 Transform⁻¹（见 Selection.ToLocalPoint）。
/// </summary>
internal sealed class SetStrokeGeometryAction : EditAction
{
    private readonly Stroke _target;
    private readonly Vector2[] _newPoints;
    private readonly Vector2[] _oldPoints;
    /// <summary>焦点三角形顶点 P 的参数角（旧 / 新）。`null` = 这次改几何与它无关。</summary>
    private readonly float? _oldFocusU;
    private readonly float? _newFocusU;
    /// <summary>坐标系网格的格距（旧 / 新）。`null` = 这次改几何与它无关（见构造函数的注释）。</summary>
    private readonly float? _oldGridStep;
    private readonly float? _newGridStep;
    private readonly RectF _before;
    private readonly RectF _after;

    /// <summary>脏区自己管：要两个矩形，不要并集（见 EditAction.SelfManagesDirty）。</summary>
    public override bool SelfManagesDirty => true;

    /// <param name="newFocusU">
    /// 拖**焦点三角形的顶点 P** 时那个新的参数角；别的改几何动作传 `null`（默认）。
    /// **为什么用可空的 float、不拿 `NaN` 当"没有"**：`NaN` 本身是 P 的一个**合法值**
    ///（= "还没拖过、按短半轴那一端自动摆"），两种含义挤在一个数里，
    /// 撤销那一路就会把"该恢复成 NaN"误判成"这次不关它的事"——
    /// 表现是"拖完 P 按撤销，椭圆回去了、P 还停在新位置"（自检当场抓到过）。
    /// </param>
    /// <param name="newGridStep">
    /// 拖**坐标系的格距手柄**时那个新的格距（局部坐标）；别的改几何动作传 `null`（默认）。
    /// 理由和 P 一模一样：**`0` 是格距的一个合法值**（= 自动，见 <see cref="Stroke.AxisGridStep"/>），
    /// 拿它当"没有"就会"撤销回不到自动那一档"。
    /// </param>
    public SetStrokeGeometryAction(Stroke target, IReadOnlyList<Vector2> newPoints,
                                   float? newFocusU = null, float? newGridStep = null)
    {
        _target = target;
        _newPoints = new Vector2[newPoints.Count];
        for (int i = 0; i < newPoints.Count; i++) _newPoints[i] = newPoints[i];
        _oldPoints = LocalPoints(target);
        _newFocusU = newFocusU;
        // 旧值**永远要记**（哪怕是个 NaN）：撤销时要原样写回去。
        _oldFocusU = newFocusU.HasValue ? target.FocusPointU : null;
        _newGridStep = newGridStep;
        _oldGridStep = newGridStep.HasValue ? target.AxisGridStep : null;
        // **两个包围盒都在动手之前算**：改完之后旧位置就再也问不出来了。
        // 平行四边形的第四个顶点不在点表里，靠 kind 让它现推（脏区不能漏它）；
        // 曲线还要多传一个**朝向**（抛物线开哪个口 / 双曲线哪条是实轴）——
        // 朝向变了也是"改几何"，那条路同样要从这里拿框。
        _before = Stroke.PaddedBoundsOf(_oldPoints, target.Transform, target.Width, target.Kind, target.CurveAxis);
        _after = Stroke.PaddedBoundsOf(_newPoints, target.Transform, target.Width, target.Kind, target.CurveAxis);
    }

    public override RectF AffectedBefore => _before;
    public override RectF AffectedAfter => _after;

    public override void Undo(InkDocument doc) => Apply(doc, _oldPoints, _oldFocusU, _oldGridStep);
    public override void Redo(InkDocument doc) => Apply(doc, _newPoints, _newFocusU, _newGridStep);

    private void Apply(InkDocument doc, Vector2[] pts, float? focusU, float? gridStep)
    {
        doc.ApplyGeometryCore(_target, pts);
        // P 的位置：这一拖带了它就写它（**该是 NaN 就写 NaN**，见构造函数的注释）；没带就一个字不动。
        // **框不用重算**：P 在椭圆上、两个焦点在椭圆里，所以"P 动"不会把紧框撑出去
        // （`_before` / `_after` 那份已然覆盖了整条椭圆）。
        if (focusU.HasValue) _target.SetConicEllipsePointAngle(focusU.Value);
        // 格距：同一个套路（`0` = 自动，是个合法值，所以"带没带"只能看可空）。
        // **框也不用重算**：网格线一律夹在外框里（见 AxisGridSegments），格距再大也撑不出框。
        if (gridStep.HasValue) _target.AxisGridStep = gridStep.Value;
        doc.Dirty.Add(_before);                 // 旧位要擦
        doc.Dirty.Add(_after);                  // 新位要画（**分开两个矩形**，见类注释 ②）
    }

    /// <summary>把对象当前的控制点抄成局部坐标数组（撤销要原样放回去）。</summary>
    private static Vector2[] LocalPoints(Stroke s)
    {
        var pts = new Vector2[s.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(s.Points[i].X, s.Points[i].Y);
        return pts;
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// **一页的内容槽**：这一页的对象、空间索引、撤销栈、计数。
///
/// 为什么要有它（用户 2026-09-26 拍板"PPT 模式参考 InkClass 原味——完全隔离"）：
/// 参考的是它们的**语义**（每页一套笔迹、切页看不到别页、清空只清本页），
/// 但实现换掉——InkClass 切页要"清空 + 逐条重放撤销历史"，ICC 要换 MemoryStream
/// 再反序列化；我们是**换一个槽的引用**：
///   · 对象、网格、撤销栈、计数全在槽里，谁都不搬；
///   · 切页 = 换引用 + 标脏，**O(1)**（真正的代价只剩"整层重铺"那一次绘制）。
/// 这也是为什么 `_undo` / `_grid` 在下面写成了转发属性：调用处一个字都不用改。
/// </summary>
internal sealed class PageSlot
{
    public readonly List<Stroke> Strokes = new();
    public readonly List<EditAction> Undo = new();
    public readonly List<EditAction> Redo = new();
    public readonly SpatialGrid Grid = new();
    public long TotalPoints;
    public int TotalIntervals;
}

internal sealed class InkDocument
{
    // ---- 页（每页一套内容）------------------------------------------------
    //
    // 0 号页 = **桌面批注页**（没有 PPT 时唯一的那一页，也是退出放映后回来的地方）。
    // > 0    = PPT 的某一页，键取 PowerPoint 的 **SlideID**（参考 Ink Canvas Ultra：
    //          页码会随插入/删除页漂移，SlideID 不会）。
    //
    // 页槽只增不减（第一批：内存换简单）；上限与清理见 `--ppttest` 的待办。

    private PageSlot _cur;
    private readonly Dictionary<int, PageSlot> _pages = new();
    private int _pageKey;

    public InkDocument()
    {
        _cur = new PageSlot();
        _pages[0] = _cur;
    }

    /// <summary>当前页的键：0 = 桌面批注页；&gt; 0 = PPT 那一页的 SlideID。</summary>
    public int PageKey => _pageKey;

    public List<Stroke> Strokes => _cur.Strokes;
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

    // ---- 页（每页一套内容）--------------------------------------------------

    /// <summary>
    /// 切到另一页（不存在就新建一个空页）。返回是否真的换了页。
    ///
    /// 切页 = **换槽的引用**：对象、空间索引、撤销栈、点数计数都在槽里，一样都不搬，
    /// 所以是 O(1)。真正的开销只有调用方那一次"整层重铺"（分块缓存要作废）。
    ///
    /// 跨页要清的三样（它们不属于任何一页）：
    ///   · 选中集合（别让"上一页的选中"挂到这一页的对象上）；
    ///   · 本帧新增的笔画暂存（渲染快路径用的，换页后前提不成立）；
    ///   · 脏区改为"整层"（旧页的缓存像素必须全部作废，否则会在新页的屏上露出来）。
    /// </summary>
    internal bool SwitchPage(int key)
    {
        if (key == _pageKey) return false;
        _cur = GetOrCreateSlot(key);
        _pageKey = key;

        Selected.Clear();
        AppendedSinceRender.Clear();
        Dirty.MarkFull();
        StructureChangedSinceRender = true;
        Version++;
        return true;
    }

    private PageSlot GetOrCreateSlot(int key)
    {
        if (_pages.TryGetValue(key, out var slot)) return slot;
        slot = new PageSlot();
        _pages[key] = slot;
        return slot;
    }

    /// <summary>所有页的键（含 0 号桌面页）。PPT 模式退出时按它逐页写盘。</summary>
    internal List<int> PageKeys()
    {
        var keys = new List<int>(_pages.Count);
        foreach (var kv in _pages) keys.Add(kv.Key);
        return keys;
    }

    /// <summary>某一页的对象表（写盘用；调用方只读，不要改）。没有这一页就是 null。</summary>
    internal List<Stroke> StrokesOf(int key)
        => _pages.TryGetValue(key, out var s) ? s.Strokes : null;

    /// <summary>
    /// 往指定页槽装一页的内容（PPT 模式从磁盘读回某一页时用）。
    ///
    /// **绕开 <see cref="AppendStroke"/>**：那个只服务"当前页"，还会记历史、标脏区；
    /// 这是一次批量装载（和 <see cref="ReplaceAll"/> 同类，不产生撤销动作），
    /// 所以计数、网格、几何释放在这里自己管一遍。
    /// 装完如果这一页正好是当前页，要整层作废重画。
    /// </summary>
    internal void LoadPageContent(int key, List<Stroke> strokes, int maxId)
    {
        var slot = GetOrCreateSlot(key);
        foreach (var s in slot.Strokes) s.Release();
        slot.Strokes.Clear();
        slot.Grid.Clear();

        long points = 0;
        int intervals = 0;
        foreach (var s in strokes)
        {
            if (s.Id == 0) s.Id = NextId();
            slot.Strokes.Add(s);
            slot.Grid.Insert(s);
            points += s.Points.Count;
            intervals += s.Erased.Count;
        }
        slot.TotalPoints = points;
        slot.TotalIntervals = intervals;
        slot.Undo.Clear();
        slot.Redo.Clear();
        ReserveIdsUpTo(maxId);

        if (key == _pageKey)
        {
            Dirty.MarkFull();
            StructureChangedSinceRender = true;
            Version++;
        }
    }

    /// <summary>
    /// 丢掉所有页、回到"只有 0 号页"（加载一份新文件时用：那是一份新文档，
    /// 不该带着上一份的 PPT 页）。**旧槽里的对象要逐个 Release**——它们缓存着
    /// Direct2D 几何，直接丢字典会泄漏 GPU 侧内存（同 RemoveStroke 那条注释）。
    /// </summary>
    internal void ResetToSinglePage()
    {
        foreach (var kv in _pages)
            foreach (var s in kv.Value.Strokes) s.Release();

        _cur = new PageSlot();
        _pages.Clear();
        _pages[0] = _cur;
        _pageKey = 0;

        Selected.Clear();
        AppendedSinceRender.Clear();
        Dirty.MarkFull();
        StructureChangedSinceRender = true;
        Version++;
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

    // 撤销栈与重做栈**也是每页一套**（参考 InkClass 的 TimeMachineHistories[页]：
    // 切页后 Ctrl+Z 撤的是本页自己的动作，不会撤出"看不见的东西"）。
    // 写成转发属性而不是字段，是为了让下面所有 `_undo.Xxx()` 的调用一个字都不用改。
    private List<EditAction> _undo => _cur.Undo;
    private List<EditAction> _redo => _cur.Redo;

    /// <summary>Bumped on every change so windows know to repaint.</summary>
    public int Version;

    /// <summary>当前页的点数总数（切页跟着换页槽走）。</summary>
    public long TotalPoints { get => _cur.TotalPoints; set => _cur.TotalPoints = value; }

    /// <summary>
    /// 全文档的**擦除区间总段数**（像素橡皮擦了多少段）。
    ///
    /// 维护成 O(1) 的计数而不是每次遍历统计：手测台要按秒采样它（见 EraserTelemetry），
    /// 遍历一万笔只为了显示一个数，纯属浪费。增删改三处跟着维护。
    /// </summary>
    public int TotalIntervals { get => _cur.TotalIntervals; set => _cur.TotalIntervals = value; }
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

    /// <summary>空间索引**每页一个**（切页跟着换，也是"切页 O(1)"的一半）。</summary>
    private SpatialGrid _grid => _cur.Grid;
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
        // （自己管脏区的那一条除外，它在 Redo/Undo 里加的是"两个矩形"而不是并集。）
        if (!action.SelfManagesDirty) Dirty.Add(action.AffectedUnion);
    }

    /// <summary>
    /// **交互式拖动**的提交口（8.2.0：面板粗细滑条 / 自定义取色板）。
    ///
    /// 拖动期间那个动作已经反复 `Redo` 过（屏幕上是实时的），这里只是把它**挂上撤销栈**：
    /// 于是"一次拖拽 = 一步撤销"，而不是每移动一格记一步。
    /// 松手时传进来的动作如果其实没改变什么（`HasChange` = false），调用方**不要**提交它。
    /// </summary>
    internal void CommitInteractive(EditAction action) => Commit(action);

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
    /// 改一条图形的**几何**（控制点），并把空间索引重摆。
    ///
    /// 为什么必须先 `Remove` 再 `Insert`：索引是按"它占哪块"记的，端点挪走之后
    /// 原来的格子里会留下一个幽灵条目（和 <see cref="ApplyTransformCore"/> 同一条理由）。
    ///
    /// **调用方负责脏区**：这条只负责让模型和索引跟上，加哪个矩形由
    /// <see cref="SetStrokeGeometryAction"/> 决定（它是"两个矩形、不是并集"）。
    /// </summary>
    internal void ApplyGeometryCore(Stroke s, Vector2[] localPoints)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        // 一次换掉全部控制点：Revision++ → 几何缓存失效、Bounds 重算（见 Stroke.SetPoints）。
        s.SetPoints(localPoints);
        _grid.Insert(s);
        Version++;
    }

    /// <summary>
    /// 对一条图形提交一次**改几何**（拖端点松手时走这里）。一步撤销。
    ///
    /// 和 <see cref="ApplyTransform"/> 一样是"效果先应用、再记账"：
    /// 拖动期间模型一个字没动，全部位移只在浮动层的预览里（见 计划-图形工具.md 8.1①）。
    /// </summary>
    /// <param name="newFocusU">
    /// 拖**焦点三角形的顶点 P** 时那个新的参数角（别的动作传 `null`，默认）——
    /// P 不在控制点表里，所以它得单独当一路参数传进来（见 <see cref="ShapeHandle.FocusPoint"/>）。
    /// </param>
    public bool ApplyGeometry(Stroke s, IReadOnlyList<Vector2> newLocalPoints,
                              float? newFocusU = null, float? newGridStep = null)
    {
        if (s == null || newLocalPoints == null || newLocalPoints.Count == 0) return false;
        var act = new SetStrokeGeometryAction(s, newLocalPoints, newFocusU, newGridStep);
        act.Redo(this);
        Commit(act);
        return true;
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
        // 加载是一份新文档的开始：**只留 0 号页**（上一份的 PPT 页连同键一起丢掉）。
        // 它换的是全新的空槽，所以这里不再需要 ClearStrokes（旧对象的几何
        // 已经由 ResetToSinglePage 逐个 Release 掉了）。
        ResetToSinglePage();
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
    /// **停顿成型**的提交：图形进文档，手绘原迹留给撤销栈（一步撤销能回手绘）。
    /// 细节与理由见 <see cref="DwellShapeAction"/>。
    /// </summary>
    public void AddDwellShape(Stroke shape, Stroke ink)
    {
        var act = new DwellShapeAction { Shape = shape, Ink = ink, Index = Strokes.Count };
        AppendStroke(shape);
        Commit(act);
    }

    /// <summary>
    /// **两笔成型**的提交（用户 2026-09-25 定的"**画两支就是双曲线**"）：
    /// 图形进文档，**两笔**手绘原迹都留给撤销栈 —— 按一次 Ctrl+Z 回到**两笔手绘**。
    ///
    /// ⚠ **调用方要先把第一笔从文档里拿掉**（`RemoveStroke(ink2)`）：这个方法只管
    /// "把图形放进去 + 记好账"，和上面那条一笔的约定完全一样。
    /// `index2` 传**第一笔被拿掉之前在文档里的下标**。
    /// </summary>
    public void AddDwellShape(Stroke shape, Stroke ink, Stroke ink2, int index2)
    {
        var act = new DwellShapeAction
        {
            Shape = shape, Ink = ink, Index = Strokes.Count,
            Ink2 = ink2, Index2 = index2,
        };
        AppendStroke(shape);
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
        var affected = a.SelfManagesDirty ? RectF.Empty : a.AffectedUnion;
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
        // 同 Undo：重做同样要把那块重画（自己管脏区的那条已经在 Redo 里加过了）。
        if (!a.SelfManagesDirty) Dirty.Add(a.AffectedUnion);
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
            // **锁定的也不碰**（2026-09-16）：橡皮改的是内容，不是位置——
            // "锁了还能被擦掉"等于没锁。图像那条先例的同一个形状。
            if (s.Locked) continue;
            if (s.IsShape)
            {
                // 图形的轮廓不是两个端点之间的线段，近似会偏，交给 Direct2D 精确算。
                if (!s.HitTestExact(x, y, radius)) continue;
            }
            else
            {
                // 自由笔迹：墨就是中心线两侧各半个笔宽，点到中心线的距离已经够准，走快的那条。
                // 半宽用**最粗处**：有压感的笔迹重压的地方比标称宽 50%（否则那一截擦不掉）。
                float reach = radius + s.MaxHalfWidth;
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
            split.Items.Add(item);

            // 熔过图形的：一条原对象换来**一整组**墨（见 MeltToInkParts），每一笔再各自按
            // "剩下的段"分开；没熔过的（笔迹）就是它自己一条。
            if (it.Melted != null)
            {
                foreach (var src in it.Melted) item.Parts.AddRange(src.SplitIntoRuns());
                foreach (var src in it.Melted) RemoveStroke(src, it.Paint);
            }
            else
            {
                item.Parts.AddRange(s.SplitIntoRuns());     // 剩下的每一段 = 一个独立对象
                RemoveStroke(s, it.Paint);
            }
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
    /// 图形（直线 / 矩形 / 椭圆 / 箭头 / 曲线 / 立体）碰到 **熔成墨再按区间切**：
    /// 切成碎线段没有意义、也没法再拖它的顶点，但"擦一下整个图形消失"更不合直觉，
    /// 所以先熔成一组笔迹（外形逐笔不变，见 <see cref="Stroke.MeltToInkParts"/>）、
    /// 再走和墨迹一模一样的区间擦除。图像对象**不碰**：那是老师截来的内容，要删它
    /// 应该用框选 + Delete——橡皮擦掉半张截图不是任何人想要的。
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
            if (s.Locked) continue;        // 同整笔橡皮：锁定的不擦（见 Stroke.Locked）
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

        // **图形：先熔成墨（一整组！）再切**（用户 2026-09-15 定："橡皮擦中图形，断开了
        // 也要单独算"；2026-09-20 又定"擦完外形一个字不能变"——见 MeltToInkParts）。
        // 熔完在文档里就地替换成一整组笔迹，下面的区间擦除逻辑完全不用为图形另开一条路；
        // 撤销靠 item.UndoOriginal 记着原图形、item.Melted 记着熔出来的这一组。
        if (s.Kind != StrokeKind.Freehand)
        {
            int at = Strokes.IndexOf(s);
            if (at < 0) { if (isNew) act.Drop(item); return; }

            // ① 先在**还没进文档**的这一组副本上算好每一笔要擦掉的区间。
            //    一整组都没被切到就什么都不做：既不熔（熔了没切到 = 白白把图形降级成墨），
            //    也不打掉它的几何缓存。
            var pieces = s.MeltToInkParts();
            var tables = new List<List<(float a, float b)>>(pieces.Count);
            bool any = false;
            for (int k = 0; k < pieces.Count; k++)
            {
                var tbl = new List<(float a, float b)>();
                if (pieces[k].Points.Count >= 2)
                {
                    _intervalScratch.Clear();
                    if (ErasedIntervals(pieces[k], rect, _intervalScratch))
                    {
                        tbl.AddRange(_intervalScratch);
                        any = true;
                    }
                }
                tables.Add(tbl);
            }
            if (!any) { if (isNew) act.Drop(item); return; }

            // ② 真的切到了：把图形换成一整组墨，并把每一笔算好的区间表落上去。
            RemoveStroke(s, paint);
            for (int k = 0; k < pieces.Count; k++) InsertStroke(at + k, pieces[k], paint);
            item.UndoOriginal = s;
            item.Melted = pieces;
            item.S = pieces[0];
            item.After = tables[0];
            for (int k = 0; k < pieces.Count; k++)
                if (tables[k].Count > 0) SetErased(pieces[k], tables[k], paint);
            affected++;
            return;
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

    /// <summary>
    /// 图形和这块矩形碰上了没有（**轮廓 ＋ 辅助线**两趟，不按外框——
    /// 见 <see cref="Stroke.ShapeOutline"/> 与 <see cref="Stroke.InkPieces"/>）。
    ///
    /// ⚠ **2026-09-21 补上第二趟（辅助线）**，修的是用户报的
    /// "**双曲线的渐近线好像擦不掉**"：轮廓里**没有**渐近线（它只在辅助槽里），
    /// 所以橡皮从虚线上掠过时判定"没碰上"，那一刀**什么也没擦**（曲线能擦、虚线纹丝不动）。
    /// 被挡的那几条棱（立体图形）、正切的两条渐近线同理，现在都能擦到了。
    ///
    /// **为什么不干脆按 `InkPieces` 全量判**：坐标系 / 数轴的**网格**也在 pieces 里，
    /// 而网格是**有意做成"不可擦"**的装饰（见 `--shapetest` 那条"落在网格线上 → 不碰它"）
    /// —— 全量判会让"擦一下网格"把整个坐标系熔成一堆笔迹。
    /// 网格那一笔的 `Aux` 是 false，所以"轮廓 ＋ 辅助线"这个判据正好把它挡在外面。
    /// </summary>
    private static bool ShapeTouchesRect(Stroke s, in RectF rect)
    {
        // 往外扩半个笔宽（+1 余量）再判交：笔身擦到就算碰到。①②两趟共用这个扩过的框。
        var r = rect.Inflate(MathF.Max(1f, s.Width) * 0.5f + 1f);
        // ① 轮廓（曲线 / 边本身）。**跳过抬笔标记**（双曲线两支之间那一下）：
        //    这一条不能省 —— NaN 喂进 SegmentHitsRect 会"比较全为 false"、
        //    最后 return true，变成"任意一擦就整条删掉"。
        var pts = s.ShapeOutline();
        for (int i = 1; i < pts.Count; i++)
        {
            if (Stroke.IsOutlineBreak(pts[i - 1]) || Stroke.IsOutlineBreak(pts[i])) continue;
            var a = pts[i - 1];
            var b = pts[i];
            if (Hit(s, a, b, r)) return true;
        }
        // ② **辅助线**（渐近线 / 被挡住的棱）：它们**不在轮廓里**，所以得单独走一遍 ——
        //    这正是"双曲线的渐近线擦不掉"的修法。网格那一笔的 Aux 是 false，不会被误判
        //    （网格有意做成不可擦，见上面那条注释）。
        foreach (var piece in s.InkPieces())
        {
            if (!piece.Aux) continue;
            for (int k = 1; k < piece.Pts.Count; k++)
                if (Hit(s, piece.Pts[k - 1], piece.Pts[k], r)) return true;
        }
        return false;

        // 小工具：把一段（局部坐标）过变换再判交。
        static bool Hit(Stroke s, Vector2 a, Vector2 b, in RectF r)
        {
            if (!s.Transform.IsIdentity)
            {
                a = Vector2.Transform(a, s.Transform);
                b = Vector2.Transform(b, s.Transform);
            }
            return SegmentHitsRect(a, b, r);
        }
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
            if (s.Locked) continue;            // 锁定的不删（见 Stroke.Locked）
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

    // ---- 属性编辑（颜色 / 粗细 / 锁定）与层级 -------------------------------

    /// <summary>
    /// 给一批对象改颜色（一步撤销）。<paramref name="newColors"/> 与 targets 一一对应
    /// （荧光笔那一条要用半透明版本，见 SetStrokePropAction）。
    /// </summary>
    public bool ApplyColors(IReadOnlyList<Stroke> targets, Color4[] newColors)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, newColors);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>给一批对象改粗细（**物理**像素，调用方自己乘 DPI）。一步撤销。</summary>
    public bool ApplyWidths(IReadOnlyList<Stroke> targets, float width)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, width);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>给一批对象加锁 / 解锁（一步撤销）。见 Stroke.Locked 的语义。</summary>
    public bool ApplyLock(IReadOnlyList<Stroke> targets, bool locked)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, locked);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>
    /// 给一批对象改线型（一步撤销）。见 <see cref="StrokeDash"/>。
    /// 它是**样式**不是形状：包围盒、几何缓存、命中判定都不受影响。
    /// </summary>
    public bool ApplyDash(IReadOnlyList<Stroke> targets, StrokeDash dash)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, dash);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>给一批**坐标系**改"要不要网格"（一步撤销）。见 <see cref="Stroke.Grid"/>。
    /// 和线型不同：网格**改的是几何**（多画那些线），所以缓存要重建。</summary>
    public bool ApplyGrid(IReadOnlyList<Stroke> targets, bool on)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, on, SetStrokePropAction.Prop.Grid);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>层级：把选中的整体置顶 / 置底（一步撤销）。内部相对顺序不变。</summary>
    public bool ReorderSelected(bool toFront)
    {
        if (Selected.Count == 0 || Selected.Count >= Strokes.Count) return false;
        var act = new ReorderStrokesAction(Selected, toFront);
        act.Redo(this);
        Commit(act);
        return true;
    }

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
                ? s.DistanceToCanvas(x, y) <= s.MaxHalfWidth + tolerance    // 有压感时按最粗处（否则重压处点不中）
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

/// <summary>
/// 激光笔的轨迹（**可以同时有好几条**）。
///
/// 用户 2026-09-27 定的行为，照 ClassIn 那个叫「**拖拽激光笔**」的工具
/// （ClassIn 帮助中心把它和普通的「激光笔」并列成两个工具）：
///   · **手写期间整条一直留着**——以前是 600ms 的滑动窗口，写着写着开头就没了；
///   · **松手之后先停留 2 秒**（<see cref="HoldMs"/>），再**整条一起淡出**（<see cref="FadeMs"/>）；
///   · 抬手再写一条时，**上一条还在淡出，不会被新的顶掉**（所以这里是个集合）。
///
/// 业界对照：Drawboard 的 Laser Pointer 把这两种做成同一工具的两个模式
/// （Trail ↔ Point），并把"松手后才开始淡出"叫 Timing = After complete；
/// InkCanvas-Ultra 的 `MW_LaserPointer.cs` 是"停留 1.2 秒 + 淡出 0.6 秒"，
/// 这里按用户定的 2 秒。
///
/// ⚠ 它**不是笔迹**（这一条一直没变）：不进文档、橡皮擦不掉、不撤销、不存档。
/// </summary>
internal sealed class LaserTrail
{
    /// <summary>松手后停留多久才开始淡出（用户 2026-09-27 定的 **2 秒**）。</summary>
    public const double HoldMs = 2000;
    /// <summary>淡出用多久（整条一起淡）。</summary>
    public const double FadeMs = 600;

    /// <summary>
    /// 采样门槛（**平方**距离，画布像素）。
    ///
    /// 数值参考 InkCanvas-Ultra 的 `LaserSampleThreshold = 6.25`（＝ 2.5 像素的平方，
    /// 见它的 `MW_LaserPointer.cs:39`）。为什么现在必须设：以前靠 600ms 窗口顺带把点数限住了，
    /// 现在**整条一直留着**，不设门槛的话一条长轨迹能攒到几千个点，
    /// 而每一帧都要绕着它重算三次轮廓（三层发光，见 Overlay.DrawLaser）。
    /// </summary>
    private const float SampleThresholdSq = 6.25f;

    /// <summary>一条轨迹：一串采样点 ＋ "淡到哪儿了"。</summary>
    internal sealed class Stroke
    {
        public readonly List<InkPoint> Points = new();
        /// <summary>
        /// 这一条的粗细（逻辑像素，写下那一刻的激光宽）。
        /// **必须每条自己记一份**：它是"写下时"的属性——共用当前值的话，
        /// 老师画完一条、抬手把粗细调大、再画下一条，**上一条会跟着一起变胖**。
        /// </summary>
        public float WidthLogical = 4f;
        /// <summary>淡出进度 0..1（1 ＝ 已经淡完、会被丢掉）。</summary>
        public float Fade;
    }

    private readonly List<Stroke> _strokes = new();

    /// <summary>正在写的那条（没有就 null）。</summary>
    public Stroke Writing { get; private set; }

    /// <summary>
    /// **最后一条抬手的时刻**；`-inf` ＝ 还有笔在写（或者这一批还没开始抬手）。
    ///
    /// ⚠ 计时是**整批共用一个**，不是每条各算各的——这正是用户 2026-09-27 报的那条：
    /// "我第一笔写完写第二笔……只要它还在写，第一笔就不会消失；等最后写完以后，
    /// 它们才会一起消失"。每条各算的话，第二笔还在写的时候第一笔就已经到点淡掉了。
    /// </summary>
    private double _releasedAtMs = double.NegativeInfinity;

    /// <summary>这一刻要画的所有轨迹（正在写的 ＋ 还在停留/淡出的）。</summary>
    public IReadOnlyList<Stroke> Strokes => _strokes;

    /// <summary>有没有轨迹要画。**画不画、要不要继续出帧都问它**（见 `NeedsFrame`）——
    /// 停留那 2 秒里画面其实没变，但必须继续出帧，否则淡出的那一刻没人去推进。
    /// </summary>
    public bool Visible => _strokes.Count > 0;

    /// <summary>这一刻所有轨迹里最粗的那一条（脏区要按它往外扩）。</summary>
    public float MaxWidthLogical
    {
        get
        {
            float w = 0f;
            foreach (var s in _strokes) if (s.WidthLogical > w) w = s.WidthLogical;
            return w;
        }
    }

    /// <summary>按下：**新起一条**（还在淡出的那些原样留着，不能被顶掉）。</summary>
    public void Begin(float x, float y, double now, float widthLogical)
    {
        // 有新笔在写 → 这一批**重新算作"还没抬手"**：
        // 计时器打回 -inf，正在停留/淡出的那几条也跟着一起"续命"
        //（用户定的：只要还在写，前面那些就不许消失）。
        _releasedAtMs = double.NegativeInfinity;
        Writing = new Stroke { WidthLogical = widthLogical };
        Writing.Points.Add(new InkPoint { X = x, Y = y, P = 1, T = now });
        _strokes.Add(Writing);
    }

    /// <summary>移动：往正在写的那条上追加（太密的点不要，见 <see cref="SampleThresholdSq"/>）。</summary>
    public void Add(float x, float y, double now)
    {
        var s = Writing;
        if (s == null || s.Points.Count == 0) return;
        var last = s.Points[s.Points.Count - 1];
        float dx = x - last.X, dy = y - last.Y;
        if (dx * dx + dy * dy < SampleThresholdSq) return;
        s.Points.Add(new InkPoint { X = x, Y = y, P = 1, T = now });
    }

    /// <summary>
    /// 抬手：**整批开始计时**（不是给这一条单独计时）。
    /// 抬手后先停留 <see cref="HoldMs"/> 再一起淡出；中途再落笔会由 <see cref="Begin"/>
    /// 把计时器打回 -inf，等于"这笔还没写完，先别淡"。
    /// </summary>
    public void Release(double now)
    {
        // ⚠ 没有正在写的就直接返回：`EndStroke` / `SwitchTool` 是**所有工具**收笔都会走的，
        //   在这里无条件写计时器的话，老师用画笔画一条、抬手，就会顺手给
        //   还在淡出的激光"续命"（激光从此老是不消失）。
        if (Writing == null) return;
        _releasedAtMs = now;
        Writing = null;
    }

    /// <summary>
    /// 每帧推进：算淡出进度、丢掉已经淡完的。
    /// **必须每帧调**（和面板动画同一套节奏）：只在抬手那一刻算的话，
    /// 停留结束之后没人去改 `Fade`，那条轨迹会永远挂在屏幕上。
    /// </summary>
    public void Prune(double now)
    {
        // 还有笔在写（或者这一批压根没抬手过）→ 全部原样留着，一点都别淡。
        if (double.IsNegativeInfinity(_releasedAtMs))
        {
            for (int i = 0; i < _strokes.Count; i++) _strokes[i].Fade = 0f;
            return;
        }

        // 整批共用同一个"抬手到现在"的时间差 → 所有条的 Fade 一模一样，
        // 所以它们总是**一起**淡完、一起被丢掉（用户要的正是这个）。
        double since = now - _releasedAtMs;
        float fade = since <= HoldMs
            ? 0f                                                    // 停留期：一点都没淡
            : (float)Math.Clamp((since - HoldMs) / FadeMs, 0.0, 1.0);

        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            _strokes[i].Fade = fade;
            if (fade >= 1f) _strokes.RemoveAt(i);
        }
    }

    public void Clear()
    {
        _strokes.Clear();
        Writing = null;
        _releasedAtMs = double.NegativeInfinity;
    }
}
