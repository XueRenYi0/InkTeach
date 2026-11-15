// [职责] 图形工具：直线/矩形/椭圆/箭头/曲线绘制。
// [测试] --shapetest

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;

public partial class InkEngine
{
    // =====================================================================
    //  图形工具：直线 / 矩形 / 椭圆 / 箭头
    //
    //  四种都是"拖 A→B"：按下记起点、拖动时**只改终点**、松手提交成一个
    //  Kind 正确的图形（见 计划-图形工具.md 8.2）。几何由两个端点定义，
    //  所以运行时只有两个点——不是"画完再拟合"，也不是先存一串采样点。
    // =====================================================================

    /// <summary>
    /// 拖动距离短于这么多**逻辑像素**就不提交（避免误点造出一个退化的图形）。
    ///
    /// 4 这个数和点选容差 <see cref="ClickToleranceLogical"/> 同一个量级：
    /// 投影上手抖个一两像素很正常，"点一下"不该留下任何东西。
    /// </summary>
    internal const float ShapeMinDragLogical = 4f;

    /// <summary>这几种工具走"拖出来一个图形"那条路（其余工具照旧写自由笔迹）。
    ///
    /// ⚠ **名单里留着长方体 / 四面体**（2026-09-20 第十二批撤的是**面板入口**，
    /// 不是画法）：它们画出来仍然是图形，旧板书里的那些要能选中、能移动、能删。
    /// 也就是"**能画**"比"**有入口**"宽一层——数轴是这个道理的第一例
    /// （见 计划-图形工具.md 11.2 与 FullUi.HasShapeEntry）。</summary>
    internal static bool IsShapeTool(Tool t)
        => t is Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Circle
             or Tool.Triangle or Tool.Parallelogram or Tool.Arrow
             or Tool.Coordinate or Tool.NumberLine
             or Tool.Parabola or Tool.Hyperbola or Tool.Sine or Tool.Cosine
             or Tool.Wave or Tool.Tangent
             // 椭圆（带焦点）（2026-09-22）：和第二行那些曲线同一族——
             // 它也是"一按一拖出一个参数化对象"，不是自由笔迹。
             or Tool.ConicEllipse
             or Tool.Cylinder or Tool.Cone or Tool.Cuboid or Tool.Tetrahedron
             or Tool.Prism or Tool.Pyramid or Tool.Frustum
             or Tool.ConeFrustum or Tool.Sphere
             or Tool.Star or Tool.Trapezoid or Tool.Sector
             or Tool.SpaceCoordinate;

    /// <summary>
    /// **一笔**做什么：把"这一笔拖到的位置"写进半成品的几何。
    ///
    /// 参数含义：`s` = 半成品对象、`origin` = **第 1 笔按下的那个点**（双曲线的中心；
    /// 后面几笔沿用，不在自己那一笔重设）、`p` = 这一笔指针所在的位置、
    /// `minSize` = 几何下限（见 <see cref="ShapeMinAxisLogical"/>，防"零尺寸图形"）。
    /// </summary>
    private delegate void StepApply(Stroke s, Vector2 origin, Vector2 p, float minSize);

    /// <summary>
    /// 一种**多笔图形**的表：**一笔一行**（`Apply` 的长度 = 这种图形要拖几笔）。
    ///
    /// 为什么要收成一张表（用户 2026-09-20 定："照他的规则复刻……用状态机"）：
    /// InkClass 里"现在画到第几笔"这件事**抄了三遍**——双曲线用自己的
    /// `drawMultiStepShapeCurrentStep`、长方体用自己的 `CuboidStrokeCollection`、
    /// 四面体用自己的 `isFirstTouchTetrahedron`，三份各写各的、各在 MouseUp 里各判一次。
    /// 我们收成**一行表**：以后加一种多笔图形 = **表里加一行 ＋ 写它那几笔的算式**，
    /// 推进 / 提交 / 作废 / 触摸那套逻辑一个字都不用动。
    ///
    /// 表里**暂时不写提示文案**：提示是 §18 那件事（用户定"最后做"），
    /// 到时候在这一行旁边加一列就够了。
    /// </summary>
    private sealed class StepPlan
    {
        public StepApply[] Apply = Array.Empty<StepApply>();
    }

    /// <summary>
    /// **双曲线：两笔**（照 InkClass 的 `drawingShapeMode = 24/25`，`MW_ShapeDrawing.cs:1051-1152`）：
    ///
    ///   第 1 笔：**从中心拖出渐近线**（`A = |dx|、B = |dy|`，"拖到哪就是哪"）；
    ///   第 2 笔：**拖到哪、曲线就经过哪**（中心沿用第 1 笔那个）。
    ///
    /// 算式各只有一份、都在模型层（<see cref="Stroke.SetHyperbolaFromAsymptote"/> /
    /// <see cref="Stroke.SetHyperbolaThroughPoint"/>），这张表只负责"第几笔调哪一个"。
    /// </summary>
    private static readonly StepPlan HyperbolaPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, min) => s.SetHyperbolaFromAsymptote(o.X, o.Y, p.X, p.Y, min),
            static (s, o, p, _) => s.SetHyperbolaThroughPoint(p.X, p.Y),
        },
    };

    /// <summary>
    /// **长方体：两笔**（照 InkClass 的 `case 9`）——第 1 笔正面矩形、第 2 笔深度。
    /// </summary>
    private static readonly StepPlan CuboidPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetCuboidFront(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetCuboidDepth(p.X, p.Y),
        },
    };

    /// <summary>**四面体：两笔**（照他的 `case 26`）——第 1 笔底面三角形、第 2 笔顶点。</summary>
    private static readonly StepPlan TetrahedronPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetTetraBase(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetTetraApex(p.X, p.Y),
        },
    };

    /// <summary>
    /// **棱柱 / 棱锥 / 棱台：两笔**（2026-09-20 用户提的，见 计划-图形工具.md §32 / §34）——
    /// 第 1 笔拖出**底面外接框**（内接正 n 边形）、第 2 笔拖到**顶上那个中心**
    ///（棱柱 = 顶面中心、棱锥 = 顶点、棱台 = 上底中心）。
    ///
    /// ⚠ **三兄弟共用这一张表、一个字都不用改**——因为它们的控制点**完全一样**
    ///（底面外接框两角 ＋ 顶上那个中心），差别只在模型里"顶上那个中心算出来的面
    /// 长什么样"（`Stroke.PrismTopLocal`）。这就是"一个动作定三种立体"的由来。
    ///
    /// 和四面体**同一套口径**（第 2 笔"拖到哪就是哪"），所以"往上拖 = 直棱柱 / 直棱锥 /
    /// 直棱台、拖歪 = 斜的"是同一个动作的自然结果；"直"那一档的**轻微吸附**在
    /// `ApplyStepGeometry` 里补（那一步不在表里，因为它要读/换档位之外的状态）。
    /// </summary>
    private static readonly StepPlan PrismPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetPrismBase(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetPrismApex(p.X, p.Y),
        },
    };

    /// <summary>
    /// 这种工具是不是**多笔**的；是的话它的表在哪（见 <see cref="StepPlan"/>）。
    /// **判据只有这一处**：按下（起半成品 / 接着改）与松手（推进还是提交）都问它，
    /// 各写一份名单就是"加一种图形必漏一处"的老毛病。
    /// </summary>
    private static StepPlan PlanOf(Tool t) => t switch
    {
        Tool.Hyperbola => HyperbolaPlan,
        Tool.Cuboid => CuboidPlan,
        Tool.Tetrahedron => TetrahedronPlan,
        // 棱柱 / 棱锥 / 棱台**共用一张表**（控制点完全一样，见 PrismPlan 的注释）——
        // 所以"加一种立体"这件事在这里是**零改动**。
        Tool.Prism or Tool.Pyramid or Tool.Frustum => PrismPlan,
        _ => null,
    };

    /// <summary>
    /// 这种图形**要拖几笔**（`PlanOf` 那张表的行数；一笔画完的给 1）。
    ///
    /// **为什么要从表里算、而不是让自检自己列一份"哪些是多笔"**：
    /// 自检里那份名单一旦跟不上（这一轮加棱锥 / 棱台时就漏了），结果不是"红"，
    /// 而是**自检按一笔去拖、多笔图形根本画不出来**——然后断言里那句
    /// `s != null && s.Kind == d.kind` 才红，报出来的却是"没画出来"，
    /// 看着像功能坏了、其实是自检写漏了。从表里算就永远同步：
    /// **加一种多笔图形 = 表里加一行，自检自动跟着拖笔。**
    /// </summary>
    internal static int ShapeStepCount(Tool t)
    {
        var plan = PlanOf(t);
        return plan == null || plan.Apply.Length < 1 ? 1 : plan.Apply.Length;
    }

    /// <summary>
    /// **多笔图形的第 1 笔**按下：起一条**还没进文档**的半成品，并记下"第一个点"
    ///（双曲线的**中心**）—— 后面几笔都围着它算。InkClass 也是这个口径：
    /// 第二笔刻意**不重设** `iniP`（`NeedUpdateIniP()`，`:1971-1977`）。
    /// </summary>
    private void BeginStepShape(Tool tool, StepPlan plan, float x, float y)
    {
        BeginShapeAt(tool, x, y);
        _stepPlan = plan;
        _stepIndex = 0;
        _stepOrigin = new Vector2(x, y);
    }

    /// <summary>
    /// 把"**当前这一笔**、指针在 `(x, y)`"写进半成品的几何（查表 → 调模型那一份算式）。
    ///
    /// **按下和拖动都调它**：按下也调，是为了"在目标位置点一下、没怎么拖"也算数
    ///（老师很可能就直接点在要经过的地方）。
    /// </summary>
    private bool ApplyStepGeometry(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || _stepPlan == null) return false;
        if (_stepIndex < 0 || _stepIndex >= _stepPlan.Apply.Length) return false;
        _stepPlan.Apply[_stepIndex](s, _stepOrigin, new Vector2(x, y),
                                    ShapeMinAxisLogical * DpiScale);

        // **棱柱第 2 笔的"直棱柱"轻微吸附**（用户 2026-09-20："直棱柱有一个轻微吸附"）——
        // 写在**这一步之后**：先按指针老老实实落一次，再看要不要把它扳直。
        //
        // 为什么放在这里而不是放进 `PrismPlan` 那张表：表里的每一行是**纯算式**
        //（`(s,o,p,min) => …`，不读引擎状态、不出状态），而吸附要**把结果报给界面**
        //（出「直棱柱」那颗胶囊，见 `_stepSnap`），还要有一份容差常量。留在这里两边都干净。
        //
        // 判据用**长度**（"顶心和底心的横向差"小于容差），和"正圆"那条吸附同一个口径：
        // "轻微吸附"就该是"手抖一点点也算直"，用角度在柱子很高时会变得很难吸住。
        //
        // **棱柱 / 棱锥 / 棱台共用这一条**（问 `Stroke.IsPrismFamily`，不各写一份名单）：
        // 三兄弟的"直"是同一件事——**顶上那个中心在底心正上方**。
        // 报给界面的胶囊名字按种类分（直棱柱 / 直棱锥 / 直棱台，见 UprightSnapOf）。
        _stepSnap = ShapeSnapKind.None;
        if (_stepIndex == 1 && Stroke.IsPrismFamily(s.Kind))
        {
            float dx = MathF.Abs(s.PrismApexLocal().X - s.PrismBaseCenterLocal().X);
            if (dx <= PrismUprightToleranceLogical * DpiScale)
            {
                s.SnapPrismApexVertical();
                _stepSnap = UprightSnapOf(s.Kind);
            }
        }
        // 胶囊挂在"**吸完之后**那个顶面中心"上（画布坐标）——绘制与脏区都读这一个数
        //（理由同 VertexPreviewCanvasPoint：两边各算一份就会差一帧、留残影）。
        StepSnapAnchor = Vector2.Transform(s.PrismApexLocal(), s.Transform);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// "直棱柱"吸附的**横向容差**（逻辑像素）：顶心和底心的横坐标差在这个范围内就扳直。
    /// 12 是照"手抖一两像素 + 投影上的一点偏"定的；比点选容差（4）宽，
    /// 因为这是个"轻微吸附"，不是"精确判定"。
    /// </summary>
    internal const float PrismUprightToleranceLogical = 12f;

    /// <summary>
    /// 吸到竖直时该报哪一颗胶囊：**名字按种类分**（直棱柱 / 直棱锥 / 直棱台），
    /// 因为老师说出口的是"这是个直棱锥"，不是笼统的"吸住了"。
    ///
    /// 吸附的**判据**三兄弟共用（都在 <see cref="ApplyStepGeometry"/> 里那一处），
    /// 这里只是把"吸到了什么"翻成人话（标签本体在 `SelectionHandles.ShapeSnapLabel`）。
    /// </summary>
    private static ShapeSnapKind UprightSnapOf(StrokeKind k) => k switch
    {
        StrokeKind.Pyramid => ShapeSnapKind.RightPyramid,
        StrokeKind.Frustum => ShapeSnapKind.RightFrustum,
        _ => ShapeSnapKind.RightPrism,
    };

    /// <summary>
    /// **多笔图形当前那一笔吸到了什么**（现在只有棱柱的「直棱柱」一档）。
    /// 和 <see cref="ShapeSnapKind"/> 同一个枚举、同一颗胶囊——用户看到的语言要一致。
    /// 松手 / 收笔时必须清掉（见 `EndStroke`），否则那颗胶囊会留在屏幕上。
    /// </summary>
    internal ShapeSnapKind StepSnap => _stepSnap;
    private ShapeSnapKind _stepSnap = ShapeSnapKind.None;

    /// <summary>「直棱柱」那颗胶囊挂在哪（**画布坐标** = 吸完之后的顶面中心）。
    /// 绘制与脏区都读它，不许各算一份（同 <see cref="VertexPreviewCanvasPoint"/>）。</summary>
    internal Vector2 StepSnapAnchor;


    /// <summary>
    /// **多笔图形的第 1 笔**里"屏幕上先只出现一半"的那一个：双曲线还没定"曲线经过的点"时，
    /// **只画渐近线、先不画曲线**——照 InkClass（第一笔只画两条虚线渐近线，
    /// `MW_ShapeDrawing.cs:1059-1068`，曲线是第二笔才出现的）。
    ///
    /// 判据直接看**模型有没有第三个点**，不另设状态位：没有那个点，曲线本来就没定义
    ///（`HyperbolaCurveALocal` 只能给一个兜底大小），画出来是假的。
    /// 渲染那一头在 Overlay 里读它（见 `DrawStrokeCore` 开头的说明）。
    /// </summary>
    internal bool HyperAsymptotePreviewOnly
        => ActiveStroke?.Kind == StrokeKind.Hyperbola && ActiveStroke.Points.Count < 3;

    /// <summary>图形工具 → 它画出来的种类。**和 <see cref="IsShapeTool"/> 同一份名单**，
    /// 加图形时两处都要动（自检 `--shapebandtest` 的"名单一致"那条会卡住）。</summary>
    internal static StrokeKind KindOfShapeTool(Tool t) => t switch
    {
        Tool.Line => StrokeKind.Line,
        Tool.Rectangle => StrokeKind.Rectangle,
        Tool.Ellipse => StrokeKind.Ellipse,
        Tool.ConicEllipse => StrokeKind.ConicEllipse,
        Tool.Circle => StrokeKind.Circle,
        Tool.Triangle => StrokeKind.Triangle,
        Tool.Parallelogram => StrokeKind.Parallelogram,
        Tool.Coordinate => StrokeKind.Coordinate,
        Tool.NumberLine => StrokeKind.NumberLine,
        Tool.Parabola => StrokeKind.Parabola,
        Tool.Hyperbola => StrokeKind.Hyperbola,
        Tool.Sine => StrokeKind.Sine,
        Tool.Cosine => StrokeKind.Cosine,
        Tool.Wave => StrokeKind.Wave,
        Tool.Tangent => StrokeKind.Tangent,
        Tool.Cylinder => StrokeKind.Cylinder,
        Tool.Cone => StrokeKind.Cone,
        Tool.Cuboid => StrokeKind.Cuboid,
        Tool.Tetrahedron => StrokeKind.Tetrahedron,
        Tool.Prism => StrokeKind.Prism,
        Tool.Pyramid => StrokeKind.Pyramid,
        Tool.Frustum => StrokeKind.Frustum,
        Tool.ConeFrustum => StrokeKind.ConeFrustum,
        Tool.Sphere => StrokeKind.Sphere,
        Tool.Star => StrokeKind.Star,
        Tool.Trapezoid => StrokeKind.Trapezoid,
        Tool.Sector => StrokeKind.Sector,
        Tool.SpaceCoordinate => StrokeKind.SpaceCoordinate,
        _ => StrokeKind.Arrow,
    };

    /// <summary>
    /// 抛物线**一笔**（照 InkClass 的 `case 20/21`：顶点 → 末端点，一次拖完）：
    ///
    ///   · 顶点 = **按下那一刻**那个点（见 <see cref="BeginShapeAt"/>）；
    ///   · **开口朝哪边**由这一拖的符号定（见 <see cref="Stroke.ParabolaAxisOfDrag"/>）
    ///     —— 面板那一格只回答"**上下还是左右**"这件推不出来的事（`ParabolaAxis`）；
    ///   · `p`（张口）与"**画到哪**"都由这一拖定：曲线**正好停在你拖到的那个点**上
    ///     （`p = t²/(2s)` 反解；画出范围见 `Stroke.ParabolaSpanOf`）。
    ///
    /// 用户 2026-09-20 的口径（原话）："现在这个双曲线和抛物线的感觉不对，还是参考他的逻辑吧"
    /// —— 上一版"方向由面板选死"的问题是：面板选着向上、手却往下拖时反解出负数，
    /// 曲线会当场缩成一条细针（`p` 掉到下限）。方向跟着拖动走就再也不会出这种事。
    /// </summary>
    private bool UpdateParabolaPreview(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || s.Kind != StrokeKind.Parabola) return false;
        var v = new Vector2(s.Points[0].X, s.Points[0].Y);
        var q = new Vector2(x, y);
        // **方向先定**：`SetParabolaVertex` 要用它来铺那个占位点（见模型里那段注释）。
        s.CurveAxis = Stroke.ParabolaAxisOfDrag(v, q, ParabolaAxis);
        s.SetParabolaVertex(v.X, v.Y);
        s.SetParabolaThroughPoint(q.X, q.Y);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **抛物线工具当前的"哪一对"**（用户 2026-09-20 定：画**之前**定好，不在选中框里改——
    /// 他原话是"选中框的抛物线按钮功能取消，我不打算从这个转抛物线开口"）。
    ///
    /// 只两档、也正是 InkClass 的那两个按钮：
    ///   · `OpenUp` = **上下抛物**（他的 `y = ax²`，`case 20`）；
    ///   · `OpenRight` = **左右抛物**（他的 `y² = ax`，`case 21`）。
    ///
    /// **具体朝哪边不在这里**——由画的时候那一拖的符号定（见 `Stroke.ParabolaAxisOfDrag`）。
    /// 2026-09-20 晚用户看过之后定："感觉不对，还是参考他的逻辑"：
    /// 方向由面板选死时，"选着向上、手却往下拖"会让曲线缩成一条细针。
    /// 面板只回答推不出来的那件事（上下还是左右），连续量（朝哪边、多大、多长）全交给手。
    ///
    /// 它只在两处出现：图形面板里**再点一次那一格**换一档（见 FullUi.ActivateSegment 与
    /// <see cref="CycleParabolaAxis"/>），以及画的时候写进对象（见 <see cref="BeginShapeAt"/>）。
    ///
    /// 存成**引擎字段**、不存偏好文件：一次课里连画几条同向的抛物线是常态，
    /// 留着上一次那个方向比每次回"上下"顺手；但也犯不上跨进程记着，所以不进偏好。
    /// </summary>
    public CurveAxis ParabolaAxis { get; private set; } = CurveAxis.OpenUp;

    /// <summary>
    /// 换下一档"哪一对"：**上下 → 左右 → 上下**（见 <see cref="ParabolaAxis"/>）。
    /// 只影响**下一笔**画出来的抛物线，不碰已经画好的对象。
    /// </summary>
    public void CycleParabolaAxis()
    {
        ParabolaAxis = ParabolaAxis == CurveAxis.OpenUp ? CurveAxis.OpenRight : CurveAxis.OpenUp;
        // 面板上那一格的图标要跟着换，所以推一次状态（同时标脏，HUD 之类也读它）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **直线那一格当前的线型**（实线 / 虚线 / 点线）。用户 2026-09-20 定：
    /// "点击直线的图标，它会变成虚线，再点击变成点虚线，再点击又变成直线……
    /// **这样就省了好几个空间格**"。
    ///
    /// 为什么只给直线、不给全部图形：那一格点第二下**要有新含义**才敢这么用，
    /// 而图形面板里现在只有"抛物线换开口方向"占着这个动作（见 `FullUi.ActivateSegment`）。
    /// 直线是画得最多的一种，虚实线又是老师最常用的两种（辅助线、延长线），
    /// 所以先给它；别的图形以后真需要，照这个模子加即可。
    ///
    /// **它是"下一笔用哪种"，不是"板上那些直线现在是什么"**——已经画好的各存各的
    /// （见 <see cref="Stroke.Dash"/>），要改它们走选中后的操作条面板（`SetSelectionDash`）。
    /// 和 `PenDash` 那条口径一样，区别只是"哪支工具吃它"。
    ///
    /// 存成**引擎字段**、不存偏好文件（和 <see cref="ParabolaAxis"/> 同一条理由）：
    /// 一次课里连画几条虚线是常态，留着上一档比每次回实线顺手。
    /// </summary>
    public StrokeDash LineDash { get; private set; } = StrokeDash.Solid;

    /// <summary>
    /// 「箭头清屏」（2026-11-12 用户定）：开启后，点工具栏箭头**进入穿透**前，
    /// 先清空**桌面批注**当前这一屏（一步撤销）。只在桌面模式生效——
    /// 白板能翻屏、PPT/PDF 一页一存，都不需要这个动作。
    /// </summary>
    internal bool ClearOnArrow;

    /// <summary>此刻点箭头会不会真的清屏（设置开着 + 在桌面批注，不在白板/PPT/文档/回放）。</summary>
    internal bool ClearOnArrowActiveNow
        => ClearOnArrow && !BoardOn && !PptMode && !DocView.IsOpen && _replay == null;

    internal void SetClearOnArrowFromUi(bool on)
    {
        if (ClearOnArrow == on) return;
        ClearOnArrow = on;
        SetUiPref("clearOnArrow", on ? "1" : null);
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>点箭头时调用：满足条件才清桌面这一屏；不满足时什么都不做（照常穿透）。</summary>
    internal void ClearDesktopOnArrowFromUi()
    {
        if (!ClearOnArrowActiveNow) return;
        if (Doc.Strokes.Count == 0) return;
        ClearCurrentPageFromUi();
    }

    /// <summary>
    /// 换下一档直线线型：**实线 → 虚线 → 点线 → 实线**（见 <see cref="LineDash"/>）。
    /// 只影响**下一笔**画出来的直线，不碰已经画好的对象。
    /// </summary>
    public void CycleLineDash()
    {
        // 三档一轮：Solid(0) → Dashed(1) → Dotted(2) → Solid。
        // 用取模而不是列举，是为了以后要加第四档（比如点划线）时只改这里一句话。
        LineDash = (StrokeDash)(((int)LineDash + 1) % 3);
        // **记住线型**（实线 / 虚线 / 点线，用户 2026-09-30 定：重启回来还是它）
        SetUiPref("lineDash", ((int)LineDash).ToString());
        // 面板上那一格的图标要跟着换，所以推一次状态（和抛物线换朝向同一套）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **立体那一格当前的档**：底面几边形（3~6）。用户 2026-09-20 定：
    /// "我想想能不能做成像直线切换那样切换三四五六"——于是它和 <see cref="LineDash"/>
    /// **完全同构**：面板上那一格**再点一次换一档**（配 4 个档位点），
    /// 画的那一刻写进对象（见 `BeginShapeAt`）。
    ///
    /// 和 <see cref="LineDash"/> 一样只管"**下一笔**"：已经画好的棱柱各存各的
    ///（见 <see cref="Stroke.PrismSides"/>），面板换档不会回头改它们。
    /// 存成引擎字段、不存偏好文件（和抛物线朝向、直线线型同一条理由）：
    /// 一次课里连画几个同一种棱柱是常态，留着上一档比每次回到默认顺手。
    ///
    /// ⚠ **棱柱 / 棱锥 / 棱台各记各的档，不是一个共享的档**——用户 2026-09-20 上手就发现
    /// "切一个另外两个也动"，那是 bug：一个人完全可能"四棱柱配三棱锥"，
    /// 三格共用一个数反而没法表达。所以这里是**三个字段**，问谁要问
    /// <see cref="SidesFor"/>（**唯一判据**，别在外面各写一份 switch）。
    /// </summary>
    private int _sidesPrism = Stroke.DefaultPrismSides;
    private int _sidesPyramid = Stroke.DefaultPrismSides;
    private int _sidesFrustum = Stroke.DefaultPrismSides;

    /// <summary>
    /// **某个立体工具当前那一档**（底面几边形，3~6）。不是这一族的工具给默认档
    ///（调用方也就不会拿它去画什么）。
    /// </summary>
    public int SidesFor(Tool tool) => tool switch
    {
        Tool.Pyramid => _sidesPyramid,
        Tool.Frustum => _sidesFrustum,
        Tool.Prism => _sidesPrism,
        _ => Stroke.DefaultPrismSides,
    };

    /// <summary>
    /// 换**当前工具**那一档：**3 → 4 → 5 → 6 → 3**（见 <see cref="SidesFor"/>）。
    /// 只影响**下一笔**画出来的那一个，既不碰已经画好的对象，也**不动另外两格**
    ///（"棱柱换档 → 棱锥的档跟着走"是用户 2026-09-20 报的 bug）。
    /// </summary>
    public void CycleSolidSides()
    {
        var t = this.Tool;
        if (!ShapeSpec.HasSideCount(t)) return;          // 不是那一族就什么也不做
        // 四档一轮，**按边数升序**（用户定：三/四/五/六）——升序比"按常用度"更好记，
        // 而且档位点从上往下读出来就是 3/4/5/6，不用额外记顺序。
        int cur = SidesFor(t);
        int next = cur >= Stroke.MaxPrismSides ? Stroke.MinPrismSides : cur + 1;
        switch (t)
        {
            case Tool.Pyramid: _sidesPyramid = next; break;
            case Tool.Frustum: _sidesFrustum = next; break;
            default: _sidesPrism = next; break;
        }
        // 面板上那一格的图标要跟着换，所以推一次状态（和直线换线型同一套）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **双曲线那一格当前的档**：画不画那两条虚线渐近线（用户 2026-09-22 提的
    /// "增加两挡，有渐近线和无渐近线"）。
    ///
    /// 和 <see cref="LineDash"/> / <see cref="CycleSolidSides"/> **完全同构**：
    /// 面板上那一格**再点一次换一档**（配 2 个档位点、图标跟着换），
    /// 画的那一刻写进对象（见 <see cref="BeginShapeAt"/> 与 <see cref="EndStroke"/>）。
    ///
    /// ⚠ **画的时候一律画渐近线**（用户 2026-09-22 的原话："化的时候是都有渐近线，
    /// 但是最终显示没有"）——那两条虚线是**画法的向导**（第一步就是"从中心拖出渐近线框"，
    /// 见 <see cref="HyperbolaPlan"/>），边画边看是必须的；它按档**收口在松手那一刻**
    ///（见 <see cref="EndStroke"/> 里的提交那一段）。
    ///
    /// 存成引擎字段、不存偏好文件（和抛物线朝向、直线线型、立体边数同一条理由）：
    /// 一节课里连画几条同一种双曲线是常态，留着上一档比每次回默认顺手。
    /// </summary>
    public bool HyperbolaAsymptotes { get; private set; } = true;

    /// <summary>换下一档"双曲线画不画渐近线"：**有 → 无 → 有**（见 <see cref="HyperbolaAsymptotes"/>）。</summary>
    public void CycleHyperbolaAsymptotes()
    {
        HyperbolaAsymptotes = !HyperbolaAsymptotes;
        // 面板上那一格的图标要跟着换，所以推一次状态（和换棱柱边数同一套）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **椭圆（带焦点）那一格当前的档**：画不画焦点三角形（用户 2026-09-22 提的
    /// "两档：有焦点三角形和没有焦点三角形"；**两个焦点两档都画**，见
    /// <see cref="Stroke.FocusTriangle"/>）。
    ///
    /// 和 <see cref="HyperbolaAsymptotes"/> 同一套：面板那一格再点一次换档、
    /// 画的那一刻写进对象、只管"下一笔"（已经画好的各存各的）。
    /// 默认**有**：这一格的存在意义就是讲焦点三角形（只要一个椭圆的话用第一行那个椭圆）。
    /// </summary>
    public bool EllipseFocusTriangle { get; private set; } = true;

    /// <summary>换下一档"椭圆画不画焦点三角形"：**有 → 无 → 有**（见 <see cref="EllipseFocusTriangle"/>）。</summary>
    public void CycleEllipseFocusTriangle()
    {
        EllipseFocusTriangle = !EllipseFocusTriangle;
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 图形工具的起手：造一条**只有起点**的图形，拖动期由
    /// <see cref="UpdateShapePreview"/> 改控制点，松手由 <see cref="EndStroke"/> 提交。
    ///
    /// 刻意**不喂预测器、也不起委托墨迹**：那是"笔尖跟手"用的，
    /// 而图形跟着指针走的是**吸附后的控制点**——两套画在屏幕上会变成两条不一样的线。
    /// </summary>
    private void BeginShapeAt(Tool tool, float x, float y)
    {
        var kind = KindOfShapeTool(tool);
        ResetPredictTail();   // B4：图形工具不预测，但把上一笔的尾巴清掉
        ActiveStroke = new Stroke
        {
            Tool = tool,
            Kind = kind,
            Color = CurrentColor,
            Width = PenWidthLogical * DpiScale,
            // 坐标系要不要网格 = **画的那一刻那个开关的状态**（见 CoordGridDefault）。
            // 存到对象自己身上，之后单独改它不影响别的坐标系（见 Stroke.Grid 的注释）。
            Grid = kind == StrokeKind.Coordinate && CoordGridDefault,
            // 抛物线的**开口方向是画之前选好的**（见 ParabolaAxis）：画的那一刻写进对象，
            // 之后它就是这条曲线自己的属性，和工具当前那档再无关系。
            CurveAxis = kind == StrokeKind.Parabola ? ParabolaAxis : CurveAxis.OpenUp,
            // 直线的**线型也是画之前选好的**（见 LineDash）：同样在画的那一刻写进对象。
            // 别的图形一律实线——它们的线型历来是"选中之后在操作条面板里改"。
            Dash = kind == StrokeKind.Line ? LineDash : StrokeDash.Solid,
            // 立体那一族的**底面几边形**同样是画之前选好的（见 SidesFor）：
            // 画的那一刻写进对象，之后面板再换档也不回头改它。
            // ⚠ 三兄弟都要走这一句——2026-09-20 第一版写成 `kind == StrokeKind.Prism`，
            // 结果**棱锥 / 棱台永远画成四棱**（用户上手一句就抓出来了）。
            PrismSides = Stroke.IsPrismFamily(kind) ? SidesFor(tool) : Stroke.DefaultPrismSides,
            // 椭圆（带焦点）的**焦点三角形开关**同样是画之前选好的（见 EllipseFocusTriangle）：
            // 画的那一刻写进对象，之后面板再换档也不回头改它。
            FocusTriangle = kind != StrokeKind.ConicEllipse || EllipseFocusTriangle,
            // 双曲线的渐近线**画的时候一律画**（那是画法的向导，见 HyperbolaAsymptotes）
            // ——按档收口在松手那一刻（见 EndStroke 的提交那一段）。
            ShowAsymptotes = true,
        };
        ActiveStroke.AddPoint(x, y, 1f, NowMs);
        // 抛物线的**顶点 = 按下那个点**：它现在是**一笔画完**的（照 InkClass 的 `case 20/21`：
        // 顶点 → 末端点，一次拖完），所以顶点在这里就落定，拖动只负责"曲线过哪、开多大"
        //（见 UpdateParabolaPreview）。多笔图形的第一个点不在这里定（它们各有各的算式）。
        if (kind == StrokeKind.Parabola) ActiveStroke.SetParabolaVertex(x, y);
        // 读数状态从这一刻重新开始：不清的话，上一次画线吸住的那个强调色会漏到
        // 这一次的第一帧（还没收到移动消息，α 也还没算）。
        _shapeInclination = 0f;
        _shapeLength = 0f;
        _shapeInclinationSnapped = false;
        _shapeAnchor = new Vector2(x, y);
        // 三角形 / 平行四边形是"外框 → 三个顶点"，拖动期每一帧都要拿**按下那一刻**的
        // 那个角去算外框——它不在控制点表里（控制点已经被推成三个顶点了）。
        _shapeBoxOrigin = new Vector2(x, y);
        ActiveStrokeHasPressure = false;
        LastCoalescedSamples = LastCoalescedMessages = 0;
    }

    /// <summary>
    /// 拖动中更新图形的控制点。
    ///
    /// 吸附只对**有方向的两个**（直线 / 箭头）做：矩形和椭圆是"两个对角点 / 中心+外角点"
    /// 定义的，没有"倾斜角"这回事，绕起点转只会把用户拉出来的框改小（见 计划-图形工具.md 8.2
    /// 那一行只写了"画线吸附"）。Shift = 15° 硬网格、Alt = 完全自由，和旋转同一套语义。
    /// 三角形 / 平行四边形没有"倾斜角"可吸，它们的"特殊形状吸附"发生在**拖顶点**时
    /// （见 计划-图形工具.md 9.6：画的时候不吸，改的时候才吸）。
    /// </summary>
    private void UpdateShapePreview(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || s.Points.Count == 0) return;

        var start = new Vector2(s.Points[0].X, s.Points[0].Y);
        var end = new Vector2(x, y);

        // 五角星 / 梯形 / 扇形：各自一条直接的"定义元素 → 几何"路径。
        if (s.Kind == StrokeKind.Star)
        {
            s.SetStarFromCenter(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(x, y);
            return;
        }
        if (s.Kind == StrokeKind.Trapezoid)
        {
            s.SetTrapezoidBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(x, y);
            return;
        }
        if (s.Kind == StrokeKind.Sector)
        {
            s.SetSectorFromCenter(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y, 90f);
            _shapeAnchor = new Vector2(x, y);
            return;
        }

        // 三角形 / 平行四边形：外框（按下点 ＋ 指针）→ **三个控制点**一次算出来。
        if (s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram)
        {
            s.SetShapeBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 空间直角坐标系：按下 = 原点，拖出去只定 x/z 两截；y 按斜二测自动半长。
        if (s.Kind == StrokeKind.SpaceCoordinate)
        {
            s.SetSpaceAxisBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 坐标系 / 数轴：同样是"外框 → 一次算出全部控制点"，只是控制点是四个。
        // 拖动期就必须写成**最终那一份定义**，否则预览和松手的结果会差一下
        // （和三角形那条同一个理由：两套算法 = 松手就跳）。
        // 数轴在这里顺手把 y 钉在按下点那一行上（往斜上方拖也还是水平线）。
        if (s.Kind is StrokeKind.Coordinate or StrokeKind.NumberLine)
        {
            s.SetAxisBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // **多笔图形**：这一笔的几何交给表里那一行（见表 StepPlan）。
        // **只有按住拖动才更新**——InkClass 就是这样（多步图形的几何只在 `MouseTouchMove` 里更新，
        // 指针不按键时半成品一动不动）。这也是"触摸屏也能用"的根子：手指没有悬停，
        // 而每一步本来就是"按住拖-松手"，笔 / 鼠标 / 手指走的是**同一条路**。
        if (_stepPlan != null)
        {
            ApplyStepGeometry(x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 抛物线走**一笔**（照 InkClass 的 `case 20/21`）：顶点在按下那一刻就定下了
        //（见 BeginShapeAt），这一拖只定"曲线经过哪个点、开多大"。
        if (s.Kind == StrokeKind.Parabola)
        {
            UpdateParabolaPreview(x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 立体图形（**旋转体那一族**：圆柱 / 圆锥 / 圆台 / 球）：**外接矩形 → 一次算出全部几何**
        //（和坐标系那条同一个套路：拖动期就写成最终那一份定义，否则预览和松手的结果会差一下）。
        //
        // ⚠ 判据走 `Stroke.IsRevolutionSolid`，**不再写"圆柱 or 圆锥"**：
        // 2026-09-20 加圆台时就漏了这一处（当时靠"没走到这儿也能画对"蒙过去了——
        // 兜底那条 `SetEnd` 写进去的角点虽然没归一，但 `SolidRectLocal` 自己会 min/max，
        // 结果一样）。加球时把它一并收成判据函数：**加一种旋转体不用再来补这一行**。
        if (Stroke.IsRevolutionSolid(s.Kind))
        {
            s.SetSolidBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 正弦 / 余弦 / 波浪线：按下 = **起点**（"从 y 轴开始画"），拖出去 = **终点**。
        // 正弦 / 余弦：这一拖定下**一个周期**和**振幅**（框宽就是一个周期）；
        // 波浪线：这一拖定下**画多长**和**振幅**（周期由振幅定，见 Stroke.WavePeriodLocal）。
        // 三者的算式同一份，差别只在 `WavePeriodLocal` 那一行。
        if (s.Kind is StrokeKind.Sine or StrokeKind.Cosine or StrokeKind.Wave)
        {
            s.SetWaveBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y, ShapeMinAxisLogical * DpiScale);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 正切：按下 = **原点**（这一支的中心），拖出去 = **以它为中心的框**——
        // 横向是半支长（渐近线正好落框边）、纵向是可视半高（超出就截断）。
        // 它和正弦/余弦**不一样**：正弦是把"起点"拖到终点（起手点留在原地），
        // 正切是**中心不动、四周一起长**。所以调用的是 `SetTangentBox` 不是 `SetWaveBox`。
        if (s.Kind == StrokeKind.Tangent)
        {
            s.SetTangentBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y, ShapeMinAxisLogical * DpiScale);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        if (s.Kind is StrokeKind.Line or StrokeKind.Arrow)
        {
            bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
            bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
            end = SelectionHandles.SnapEndPoint(start, end, shift, alt, out bool snapped);
            // 画线过程中也把 α 报给浮动层（用户 2026-09-18 要的：边画边看这条线是多少度）。
            // α 由**权威函数**算，这里只是把它和"吸没吸住"一起存下来，供渲染与脏区用。
            _shapeInclination = SelectionHandles.InclinationDegrees(start, end);
            _shapeLength = Vector2.Distance(start, end);
            _shapeInclinationSnapped = snapped;
        }
        s.SetEnd(end.X, end.Y);
        _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
    }

    /// <summary>
    /// 正在画一条**有倾斜角**的图形（直线 / 箭头）。
    ///
    /// 矩形和椭圆是两个对角点定义的，没有"倾斜角"这回事，所以不给读数
    /// （画线读数只对直线/箭头，见 计划-图形工具.md 8.2）。
    /// </summary>
    internal bool ShapeInclinationActive
        => ActiveStroke != null && ActiveStroke.Kind is StrokeKind.Line or StrokeKind.Arrow;

    /// <summary>画线中那条线**当前结果**的 α（[0°,180°)）；不在画线时是 0。</summary>
    internal float ShapeInclinationDegrees => _shapeInclination;

    /// <summary>画线中那条线**当前结果**的长度（画布像素）。和 α 同源、同一时刻更新，
    /// 标签把两个数一起报（2026-10-05 用户："再给直线增加一个长度，和度数是同一个逻辑"）。</summary>
    internal float ShapeLength => _shapeLength;

    /// <summary>画线中的 α 是"吸"出来的吗（特殊角 / Shift 网格）——标签按它变色。</summary>
    internal bool ShapeInclinationSnapped => _shapeInclinationSnapped;

    /// <summary>画线中那个会被拖动的端点（画布坐标）：读数标签贴它外侧。</summary>
    internal Vector2 ShapeInclinationAnchor => _shapeAnchor;

    private float _shapeInclination;
    private float _shapeLength;
    private bool _shapeInclinationSnapped;
    private Vector2 _shapeAnchor;
    /// <summary>
    /// 图形起手时按下的那个点（画布坐标）。只有三角形 / 平行四边形用得到：
    /// 它们是"外框 → 三个顶点"，而外框的两个角里有一个（按下的那个）**不在控制点表里**，
    /// 必须在起手那一刻记下来，否则拖动中做不出"外框"这个中间量。
    /// </summary>
    private Vector2 _shapeBoxOrigin;

    /// <summary>
    /// 这次拖拽够不够长（不够就当没画，什么也不留）。
    /// 判据用**画布坐标下的实际距离**，而不是"收没收到过移动消息"——
    /// 手抖一下也会收到移动消息。
    /// </summary>
    private bool ShapeDragLongEnough(Stroke s)
    {
        if (s.Points.Count < 2) return false;
        // 星形 / 梯形 / 扇形：首末点不代表大小（星形首末是相邻顶点），改量真实包围盒。
        if (s.Kind is StrokeKind.Star or StrokeKind.Trapezoid or StrokeKind.Sector)
        {
            var bb = s.InkBounds;
            float size = MathF.Max(bb.MaxX - bb.MinX, bb.MaxY - bb.MinY);
            return size >= ShapeMinDragLogical * DpiScale;
        }
        // 坐标系要单独算：它的**原点是按下点**（见 Stroke.SetAxisBox），
        // 而"拖出去的那一下" = 原点 ↔ 外框角的距离（外框角 = 原点 ± 拖动量，所以正好等于拖动量）。
        // 拿外框对角去判的话（= √2 × 拖动量）会把"只拖了 3 像素"也当成够长。
        if (s.Kind == StrokeKind.Coordinate && s.Points.Count >= 3)
        {
            var o = new Vector2(s.Points[2].X, s.Points[2].Y);
            var c = new Vector2(s.Points[0].X, s.Points[0].Y);
            return Vector2.Distance(o, c) >= ShapeMinDragLogical * DpiScale;
        }
        var a = new Vector2(s.Points[0].X, s.Points[0].Y);
        var b = new Vector2(s.Points[^1].X, s.Points[^1].Y);
        return Vector2.Distance(a, b) >= ShapeMinDragLogical * DpiScale;
    }

    /// <summary>
    /// 把这条指针消息里的**全部**采样点追加进当前笔画。
    ///
    /// 真笔走合并点（`GetPointerPenInfoHistory`，见 <c>Input/PenInput.cs</c>）：
    /// 系统会把来不及投递的移动合并进一条消息，只取最新那一个等于把采样率砍半。
    /// 非笔设备、或读不到合并点时退回"一个消息一个点"的老路（行为与以前完全一致）。
    ///
    /// 坐标：笔画存**画布**坐标；预测器喂**屏幕**坐标（湿墨轨迹也是屏幕空间）。
    /// </summary>
    /// <summary>
    /// **这一笔走的是哪条路** —— 用大白话写出来。
    ///
    /// 为什么要有它（2026-10-07 用户的原话）："**我怎么知道什么时候是走了 ink？
    /// 什么时候又是鼠标模式补点？**"
    ///
    /// 以前这些信息散在两处（`设备=鼠标` / `[raw 补点 +N]`），得自己对读才知道 ——
    /// **那就等于没告诉用户**。这里合成一句人话，一眼看明白。
    /// （原还有第三处 `[wintab …]`；Wintab 支路 2026-10-16 整条移除。）
    /// </summary>
    private string DescribeInputPath(uint ptype)
    {
        if (ptype == Native.PT_PEN)
            return "开 ink（Windows Ink 笔）→ 压力 1024 级";
        if (ptype == Native.PT_TOUCH)
            return "触摸";
        return _rawPointsAdded > 0
            ? "关 ink → raw 补点（等宽，无压力）"
            : "鼠标（等宽）";
    }

    private void AppendStrokeSamples(uint id, uint ptype, float curCanvasX, float curCanvasY,
                                     float screenX, float screenY, float curPressure)
    {
        if (ActiveStroke == null) return;

        // `--rawinput`：这一条指针消息给出的是**绝对位置** → 把它设成锚点、累计清零。
        // 这是"误差不漂"的关键：raw 报的位移只在两条指针消息之间累加，
        // 每来一条指针消息就重新对准一次真值。
        ResetRawAnchor(screenX, screenY);

        // 顺便结算"有没有中间点可捞"：把这一段的原始报数并进累计，再看比值。
        // 阈值 1.3 是留了余量的——1:1 设备的实测比值就是 1.0，有合并的实测 2.3~4。
        _ptrMsgsThisStroke++;
        _rawTotalSincePtr += _rawSinceLastPtr;
        _rawSinceLastPtr = 0;
        if (_ptrMsgsThisStroke >= 3)
            _rawHasIntermediates = _rawTotalSincePtr >= _ptrMsgsThisStroke * 1.3;

        if (ptype == Native.PT_PEN && _pen.Read(id, NowMs, _lastInputMsgQpc) > 0)
        {
            LastCoalescedMessages++;
            LastCoalescedSamples += _pen.Count;
            PenMessages++;
            PenSamples += _pen.Count;
            PenSawPressureMask |= _pen.AnyPressure;
            PenSawTiltMask |= _pen.HasTilt;
            PenSawRotationMask |= _pen.Last.Rotation != 0;
            for (int i = 0; i < _pen.Count; i++)
            {
                var s = _pen[i];
                float cx = s.X, cy = s.Y;
                ScreenToCanvas(ref cx, ref cy);
                // ⚠ **真笔这条也要净化**（2026-10-07）：用户报的是"开不开 ink 都脏"，
                // 而开 ink 走的就是这一条。板子报的位置毛刺是**同一个来源**，
                // 不会因为走了 Windows Ink 就自己变干净。
                AddPointCleaned(cx, cy, s.Pressure, s.TimeMs);
                PenTotalPoints++;
                if (s.HasPressure) PenPressurePoints++;
            }
            ActiveStrokeHasPressure |= _pen.AnyPressure;
            // 压感是**整笔的属性**（见 Stroke.HasPressure）：这一笔只要有一条消息报过有效压力，
            // 它就从此刻起按压感渲染。**当场写进对象**（而不是渲染时再问一次），
            // 因为湿墨、干墨、紧框、存档读的都是这一个标志。
            //
            // **压感只作用于「笔」这一支**（2026-09-20 对齐 WPF 时定的，也是官方示例的做法：
            // 荧光笔的 `DrawingAttributes.IgnorePressure = true`）：
            //   · 荧光笔是一支"平头马克笔"，粗细随压力变会让划出来的带子忽宽忽窄（满压还是 2 倍宽）；
            //   · 激光笔只是指一下，没有"笔迹粗细"这回事。
            if (ActiveStrokeHasPressure && ActiveStroke.Tool == Tool.Pen) ActiveStroke.HasPressure = true;
            return;
        }

        // ---- 鼠标 / 触摸：同一个道理，读 POINTER_INFO 的合并点 --------------------
        //
        // 这条以前不存在，代价在两类设备上同时体现：手写板**没开 Windows Ink** 时
        // 以 PT_MOUSE 上报、触摸屏是 PT_TOUCH，它们一条消息只取最新那一个点，
        // 快写时轨迹被静默抽稀，而且预测器拿不到足够密的速度估计。
        if (ptype != Native.PT_PEN && _ptr.Read(id, NowMs, _lastInputMsgQpc) > 0)
        {
            LastCoalescedMessages++;
            LastCoalescedSamples += _ptr.Count;
            PtrMessages++;
            PtrSamples += _ptr.Count;
            // HistoryCount 是"系统本来说有几条消息"，减掉我们真的收下的那几条，
            // 就是被合并掉的中间点（合并率就是它除以 HistoryCount）。
            PtrCoalescedExtra += Math.Max(0, _ptr.HistoryCount - 1);

            int nPtr = _ptr.Count;
            for (int i = 0; i < nPtr; i++)
            {
                var s = _ptr[i];
                float cx = s.X, cy = s.Y;
                ScreenToCanvas(ref cx, ref cy);
                AddPointCleaned(cx, cy, s.Pressure, s.TimeMs);
                PtrTotalPoints++;
            }
            // 非笔设备没有 penMask，也就永远不会给这一笔打上 HasPressure——
            // 这正是 WPF / 微软白板里"鼠标画的那条线是等宽"的来源。
            return;
        }

        // ---- 读不到合并点：退回"一个消息一个点"的老路（行为与以前完全一致）------
        AddPointCleaned(curCanvasX, curCanvasY, curPressure, NowMs);
    }

    /// <summary>
    /// 处理一条原始输入（`--rawprobe` / `--rawinput`）。
    ///
    /// `--rawprobe`：只数条数（用来量"设备到底报了多少"，见 `[笔画]` 那一栏）。
    /// `--rawinput`：在"正在写一笔"时，把 raw 的相对位移换算成绝对位置**补成采样点**。
    ///
    /// 门槛卡得很保守——只要有一条不满足就**只计数、不加点**，绝不影响既有行为：
    ///   · 必须是鼠标类型的报（笔的原始报是 digitizer 类、厂商私有格式，不走这里）；
    ///   · 必须正在写一笔，且这一笔是**自由笔迹**（图形/橡皮/框选/截图/激光都不碰）；
    ///   · 必须有**有效的绝对锚点**（没锚点就没法把相对位移变绝对——宁可不加也不猜）。
    /// </summary>
    private void HandleRawInput(IntPtr lParam)
    {
        _rawReportsThisStroke++;
        _rawSinceLastPtr++;
        if (!RawInputCapture || ActiveStroke == null) { _rawSkipNoStroke++; return; }

        uint size = (uint)Marshal.SizeOf<Native.RAWINPUT>();
        uint header = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, out var ri, ref size, header) == uint.MaxValue)
        { _rawSkipApi++; return; }
        if (ri.header.dwType != Native.RIM_TYPEMOUSE) { _rawSkipType++; return; }

        int dx = ri.mouse.lLastX, dy = ri.mouse.lLastY;

        // 只有"自由笔迹 + 手写工具"才补点：和 OnPointerMove 那条分流保持同一张口径
        if (ActiveStroke.Kind != StrokeKind.Freehand) { _rawSkipKind++; return; }
        var tool = ActiveStroke.Tool;
        if (tool != Tool.Pen && tool != Tool.Highlighter) { _rawSkipKind++; return; }
        if (SelDragging || _dwell.State == DwellState.Armed) { _rawSkipGate++; return; }
        // **只有"确实有中间点可捞"才补**（见 _rawHasIntermediates 那段注释）：
        // 1:1 的设备上补进去的全是重复点（零长段），纯噪声。
        if (!_rawHasIntermediates) { _rawSkipNoInter++; return; }
        // 两种报都**必须先有绝对锚点**（来自指针消息）才敢用——没锚点就没法判断
        // "这一报是真的在附近，还是换算错了"，宁可不加也不猜。
        if (!_rawAnchorValid) { _rawSkipAnchor++; return; }

        float sx, sy;
        bool isAbs = (ri.mouse.usFlags & Native.MOUSE_MOVE_ABSOLUTE) != 0;
        if (isAbs)
        {
            // **绝对报**（2026-10-07 补，实测手写板在鼠标模式上报的就是这种，
            // 连 SendInput 注入的也是——原来那句"遇到绝对就跳过"把**每一条**都跳掉了）。
            //
            // ⚠ **绝对报也要取"差值"再累加，不能拿绝对值当位置直接用**——第一版就是直接
            // 用，结果被测出来：绝对值的归一化映射有微小偏差 → 补进去的点有偏移 →
            // **细线的抗锯齿被摊开、覆盖率掉到阈值以下**（`--widthtest` 最细那档
            // 从合格掉到 0.69）。而且本机合成输入是 1:1（原始报和指针消息是同一批位置），
            // 直接当位置用等于**往笔迹里塞噪声**。
            //
            // 取差值就对了：
            //   · 1:1 时差值 = 指针消息那条位移 → 累加出来正好是指针点 → 被最小距离过滤掉 → 零噪声；
            //   · 有合并时（真机 2.3:1）差值 = 沿路径的中间位移 → 累加出来是真正的插值点。
            // 而且它**每次指针消息都被重新对准**（锚点），映射偏一点也不漂。
            if (!_rawAbsValid) { _rawAbsX = dx; _rawAbsY = dy; _rawAbsValid = true; _rawSkipAbs++; return; }
            float pw = _primaryW / 65535f, ph = _primaryH / 65535f;
            float mx = (dx - _rawAbsX) * pw;
            float my = (dy - _rawAbsY) * ph;
            _rawAbsX = dx; _rawAbsY = dy;
            // 多屏/虚拟桌面时归一化基准不是主屏，差值会被放大或缩小——用一个宽松但有效的
            // 上限挡掉病态值（正常的报单步位移不会超过半个屏）。
            if (MathF.Abs(mx) > _primaryW * 0.5f || MathF.Abs(my) > _primaryH * 0.5f) { _rawSkipAbs++; return; }
            _rawAccumX += mx;
            _rawAccumY += my;
            sx = _rawAnchorX + _rawAccumX;
            sy = _rawAnchorY + _rawAccumY;
        }
        else
        {
            if (dx == 0 && dy == 0) { _rawSkipZero++; return; }   // 不动时也会发空报，丢掉
            _rawAccumX += dx;
            _rawAccumY += dy;
            sx = _rawAnchorX + _rawAccumX;
            sy = _rawAnchorY + _rawAccumY;
        }

        float cx = sx, cy = sy;
        ScreenToCanvas(ref cx, ref cy);

        // ① 每笔上限（第二道保险）
        if (_rawPointsAdded >= RawMaxPerStroke) { _rawSkipCap++; return; }

        // ② 最小距离：把零长段挡在门外。鼠标 raw 可以到 1000Hz，不移开的话
        //    相邻两点差不到 1 像素——几何那边明确讲过零长段会多出退化段。
        if (ActiveStroke.Points.Count > 0)
        {
            var last = ActiveStroke.Points[^1];
            float ddx = cx - last.X, ddy = cy - last.Y;
            if (ddx * ddx + ddy * ddy < RawMinDistPx * RawMinDistPx) { _rawSkipNear++; return; }
        }

        // 压力**沿用这一笔最后一个点**（第一个点还没有邻居时才用 0.5 这个中点）。
        //
        // 教训（两次都栽在"拿常数代表有状态的事"上）：写死 0.5 → 真笔的 raw 补点被拉粗；
        // 写死 0 → 有压感的笔画上全是细口子（看起来"脏"）。沿用到目前为止的值才对：
        //   · 真笔：最后一个点的压力就是真实压力 → raw 点无缝接上 ✓
        //   · 真鼠标：`HasPressure` 始终为假，这个值根本不参与渲染 → 等宽不变 ✓
        float rawP = ActiveStroke.Points.Count > 0 ? ActiveStroke.Points[^1].P : 0.5f;
        AddPointCleaned(cx, cy, rawP, NowMs);
        _rawPointsAdded++;
    }

    private void ResetRawAnchor(float screenX, float screenY)
    {
        _rawAnchorX = screenX; _rawAnchorY = screenY;
        _rawAccumX = _rawAccumY = 0;
        _rawAnchorValid = true;
    }

    /// <summary>
    /// 按笔开关原始输入（**空闲时必须关**，见 <see cref="OverlayWindow.SetRawInput"/> 那段注释：
    /// 不关的话空闲会持续收到鼠标轮询空报，主循环被顶成 46fps 空转、单核 26%）。
    /// </summary>
    private void SetRawCapture(bool on)
    {
        if (!RawProbeEnabled && !RawInputCapture) return;
        if (on == _rawRegistered) return;
        if (_windows.Count == 0) return;
        _rawRegistered = on && _windows[0].SetRawInput(true);
        if (!on) _windows[0].SetRawInput(false);
    }

    private bool _rawRegistered;

    /// <summary>上一笔结束时进程级轨迹喂点/结账数（`[笔画]` 里报"这一笔"的差值）。</summary>
    private long _trailFedSeen, _trailRemovedSeen;

    // 随老预测系统整条链移除。算法与接线原文见 `.revert/2026-10-05-渲染减法/`。

    /// <summary>
    /// 湿墨该用多粗的半径：**和干墨同一个映射**（见 <see cref="PressureWidth"/>）。
    ///
    /// 不做这一步的后果很扎眼：抬手那一瞬间，湿墨（固定半径）会跳成干墨（有粗有细）——
    /// 正在写的"一"和落笔后的"一"粗细不是一条线。
    /// </summary>
    private float TrailRadius()
    {
        var s = ActiveStroke;
        if (s == null) return 0f;
        if (!s.HasPressure || !PressureWidth.Enabled || s.Points.Count == 0) return s.Width * 0.5f;
        return PressureWidth.HalfWidth(s.Width, s.Points[^1].P);
    }

    /// <summary>
    /// 湿墨：把这一条消息里的真实点（屏幕坐标）连同**预测点**一起交给系统合成器。
    ///
    /// ⚠ **实测（2026-09-29）：DWM 不把我们喂的预测点画出来。**
    /// 判据用最大档 `--predictms 200 --predictlead 400` 做探针：同一时刻鼠标那条
    /// （预测尾由我们自己画）会窜出去一大截，**手写板那条毫无变化**。
    /// 也就是说真笔的"跟手"完全来自委托轨迹本身，预测这几段目前是**白喂**。
    /// 先留着（几次 COM 调用，代价可以忽略；万一以后系统版本开始认了就直接生效），
    /// 但**别把"笔的预测"算进效果账里**——笔那条路的效果全在弧线和宽度上。
    /// 报告里 `喂DWM N 段（平均 X / 最大 Y px）` 这一项只在排查"到底喂没喂"时看。
    /// </summary>
    /// 预测只作用于湿墨——它画的是"正在写的这一笔"的最后一小段，真实点一到就被覆盖，
    /// 不进存档、也不会变成一条真的笔画。这是"不甩墨"的第一道保险。
    /// </summary>
    private void FeedInkTrail(uint ptype, float radius, float screenX, float screenY)
    {
        if (!OverlayWindow.InkTrailEnabled) return;
        var win = WindowAt(screenX, screenY);
        if (win == null) return;

        // 采样缓冲**按输入类型选**：笔在 `_pen`、鼠标/触摸在 `_ptr`（两边都是屏幕像素）。
        // ⚠ 以前这里写死读 `_pen`——放开鼠标/触摸之后，那样会把**上一条笔消息的旧点**
        // 再喂一遍，轨迹会画在错误的位置/时间段上。
        int realCount = 0;
        if (ptype == Native.PT_PEN)
        {
            for (int i = 0; i < _pen.Count && realCount < _trailReal.Length; i++)
            {
                _trailReal[realCount] = new Vector2(_pen[i].X, _pen[i].Y);
                // 逐点半径 = 这一点的压力走**和干墨同一个映射**（见 PressureWidth）。
                _trailRadii[realCount] = PressureRadiusOf(_pen[i].Pressure);
                realCount++;
            }
        }
        else
        {
            for (int i = 0; i < _ptr.Count && realCount < _trailReal.Length; i++)
            {
                _trailReal[realCount] = new Vector2(_ptr[i].X, _ptr[i].Y);
                // 非笔没有真实压力（`PointerSample` 恒 0.5 占位）→ 半径就是当前笔宽的一半，
                // 和干墨一致（`radius` 由调用方给，见 TrailRadius）。
                _trailRadii[realCount] = radius;
                realCount++;
            }
        }
        if (realCount == 0)
        {
            _trailReal[realCount] = new Vector2(screenX, screenY);
            _trailRadii[realCount] = radius;
            realCount++;
        }

        // [试验E，2026-10-11 试过、已撤] 曾在这里把"再走一步"的预测点喂进 predicted 数组
        //（`--trailpredict`）：鼠标/触摸快速书写时甩尾明显（用户真机拍板），整条撤掉。
        win.AddInkTrailPoints(_trailReal, realCount, null, 0, radius, _trailRadii);
    }

    /// <summary>一个压力值 → 湿墨半径（和干墨同一映射、同一单位）。</summary>
    private float PressureRadiusOf(float p)
    {
        var s = ActiveStroke;
        if (s == null) return 0f;
        if (!s.HasPressure || !PressureWidth.Enabled) return s.Width * 0.5f;
        return PressureWidth.HalfWidth(s.Width, p);
    }

    private void ApplyMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;

        // 选择方式是套索时走另一条判据（80% 在内 + 贴边无限延伸），见 ApplyLassoSelection。
        if (SelMode == SelectMode.Lasso) { ApplyLassoSelection(); return; }

        float l = MqMinX, t = MqMinY, r = MqMaxX, b = MqMaxY;
        if (r - l < 4 || b - t < 4)
        {
            // 这是一次**点击**，不是拖框。单击空白处就该取消选中。
            // 之前这里直接 return、什么都不做，用户得点两下才取消，很别扭。
            Doc.Selected.Clear();
            _dirty = true;
            return;
        }

        Doc.ApplyMarquee(new RectF { MinX = l, MinY = t, MaxX = r, MaxY = b });
        // **不在这里自动拆开**。原来这里调了 SplitErasedSelection()，被否掉了：
        // 拆成两个对象会让半透明荧光笔在"两截互相穿过"的地方**混合两次、颜色变深**——
        // 那等于"我什么都没擦，只是框选了一下，墨却变了"。原则是**擦除只是像素没了**：
        // 除了被擦掉的那一块，其他像素在任何操作前后都该一模一样。
        // 想单独摆弄某一截时用显式动作（Ctrl+Alt+8 拆开擦断的笔迹），见 RunAction。
        Console.WriteLine($"marquee selected {Doc.Selected.Count} strokes");
        _dirty = true;
    }

    /// <summary>
    /// 切换框选工具下"拖空白处"的方式：矩形框 ↔ 自由套索。
    ///
    /// **只切方式，不换工具**——用户按这个键的时候手上可能还拿着笔（正在写），
    /// 换工具会顺手把选中清掉（见 SwitchTool），那是他没要的副作用。
    /// 方式不落盘：和工具尺寸一样活在内存里，重开就是默认的矩形。
    /// </summary>
    private void ToggleSelectMode()
    {
        SetSelectMode(SelMode == SelectMode.Lasso ? SelectMode.Rect : SelectMode.Lasso);
    }

    /// <summary>
    /// 切到指定的选择方式。键盘那一路（Ctrl+Alt+9）和界面那一路
    /// （<see cref="SetSelectModeFromUi"/>）**都走这里**，免得两条路各写一套规则。
    /// </summary>
    private void SetSelectMode(SelectMode mode)
    {
        SelMode = mode;
        // **记住选择方式**（矩形 / 套索，用户 2026-09-30 定）：键盘和界面两条路都走这里，
        // 所以只在这一处写就够了；启动时读回来（见 Run 里那句 selMode）。
        SetUiPref("selMode", mode == SelectMode.Lasso ? "lasso" : "rect");
        // 半路切就把没画完的圈丢掉，免得下一次按下接在旧路径后面。
        LassoPath.Clear();
        MarqueeActive = false;
        Console.WriteLine(mode == SelectMode.Lasso
            ? "选择方式：自由套索（圈住 80% 就算选中；圈到屏幕边＝当作无限延伸）"
            : "选择方式：矩形框（碰到墨就算选中）");
        if (Tool != Tool.Marquee)
            Console.WriteLine("  （现在不是框选工具：按 Ctrl+Alt+5 换到框选才用得上）");
    }

    /// <summary>自检用：切一次选择方式（等同于按一下 Ctrl+Alt+9）。</summary>
    internal void ToggleSelectModeForTest() => ToggleSelectMode();

    /// <summary>
    /// 框选工具按下时起一个新手势。**抽出来是为了自检能走到同一段代码**
    /// （见 <see cref="MarqueeDragForTest"/>）：合成鼠标在锁屏 / 被别的程序占着捕获时
    /// 进不来，那几条自检只能记跳过——而这段接线恰恰是最容易写错的地方。
    /// 修饰键由调用方读键盘后传进来，判定函数保持纯逻辑。
    /// </summary>
    private void BeginMarqueeAt(float x, float y, bool shift, bool alt)
    {
        MarqueeActive = true;
        _mqAnchorX = x; _mqAnchorY = y;
        MqMinX = MqMaxX = x; MqMinY = MqMaxY = y;
        // 套索：路径从按下这一点开始；修饰键也在这一刻记下来（松手时用它决定加选/减选）。
        LassoPath.Clear();
        LassoLive = new Vector2(x, y);
        if (SelMode == SelectMode.Lasso) LassoPath.Add(LassoLive);
        _lassoAdditive = shift;
        _lassoSubtractive = alt;
    }

    /// <summary>
    /// 拖框中一路移动。
    ///
    /// 矩形：**锚点 ↔ 当前点**（四个方向都能拉，往回走框跟着缩回去）。
    /// 套索：抽稀后收进路径，同时记下指针当前位置给预览用（见 <see cref="LassoLive"/>）。
    /// </summary>
    private void ExtendMarqueeTo(float x, float y)
    {
        if (SelMode != SelectMode.Lasso)
        {
            // 矩形框 = 从锚点拉到当前点的那一个矩形。**不是**"扫过的最大范围"。
            MqMinX = MathF.Min(_mqAnchorX, x); MqMaxX = MathF.Max(_mqAnchorX, x);
            MqMinY = MathF.Min(_mqAnchorY, y); MqMaxY = MathF.Max(_mqAnchorY, y);
            return;
        }

        // 套索：间距 < 3 逻辑像素的点对判据没有贡献，只会把多边形从几十个顶点撑到几千个，
        // 而每条笔迹的"点在不在圈里"都要乘这个顶点数。
        LassoLive = new Vector2(x, y);
        if (LassoPath.Count == 0) { LassoPath.Add(LassoLive); }
        else
        {
            var last = LassoPath[LassoPath.Count - 1];
            if (Vector2.Distance(last, LassoLive) >= LassoStepLogical * DpiScale)
                LassoPath.Add(LassoLive);
        }

        // 脏区**只增不减**（和矩形那条路刻意不同）：已经画到屏幕上的那一段线，
        // 不能因为指针往回移就不重画——不重画就擦不掉，屏幕上会留下一截旧线。
        MqMinX = MathF.Min(MqMinX, x); MqMaxX = MathF.Max(MqMaxX, x);
        MqMinY = MathF.Min(MqMinY, y); MqMaxY = MathF.Max(MqMaxY, y);
    }

    /// <summary>
    /// **进入截图取景**（8.3.1，照微信的节奏）：一进来就
    ///   · 抓"冻结帧"（整屏；隐藏窗口截图那张是**藏起我们整个覆盖层**抓的）；
    ///   · 整屏铺遮罩（全灰，还没有框）+ 十字光标 + 全屏准线 + 顶部提示 + 右上「✕ 取消」；
    ///   · 工具切到截图（面板那一格亮着；界面在取景期间由引擎收起）。
    ///
    /// 入口三处：点模式段（`EnterCapture`）、键盘 `Ctrl+Shift+A`、以及在"截图工具已选中"时
    /// 直接在画布上按下（<see cref="BeginCaptureAt"/> 会兜底调它）。
    /// `freeze=false` 只给自检用（不真的抓屏）。
    /// </summary>
    internal void BeginCaptureMode(bool freeze = true)
    {
        if (CaptureActive) return;
        if (Tool != Tool.Capture) SwitchTool(Tool.Capture);   // 记录 _toolBeforeCapture 在里面
        CaptureActive = true;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capturePressHandled = false;
        _capLastClickMs = double.NegativeInfinity;

        // 指针取**系统光标当前位置**：从面板上点进来时，覆盖层收不到那几条移动消息，
        // PointerX/Y 还停在旧位置——准线和"从哪儿起框"就会晚一拍才跟上。
        if (Native.GetCursorPos(out var p))
        {
            PointerX = ScreenToCanvasX(p.X);
            PointerY = ScreenToCanvasY(p.Y);   // 屏幕 → 画布（相机）
        }
        // 还没有框：四个点都放在指针上（"框太小"的判据自然成立 → 遮罩整块灰、不挖洞）
        CapMinX = CapMaxX = PointerX;
        CapMinY = CapMaxY = PointerY;

        if (freeze) BeginCaptureFreeze();
        _dirty = true;
        ApplyCursor();
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 截图取景框按下（和框选同一套锚点算法）。
    ///
    /// 8.3.1 起：如果还没进取景（只点了「截屏」格、还没点模式段），**按下的这一下就是入口**——
    /// 先调 <see cref="BeginCaptureMode"/>（抓冻结帧、铺遮罩），再落锚点。
    /// </summary>
    private void BeginCaptureAt(float x, float y, bool freeze = true)
    {
        if (!CaptureActive) BeginCaptureMode(freeze);
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capAnchorX = x; _capAnchorY = y;
        CapMinX = CapMaxX = x; CapMinY = CapMaxY = y;
    }

    /// <summary>
    /// 换另一种截法（取景中按 `Ctrl+Shift+A`）：重新冻结一次。
    /// 「截图」←→「隐藏窗口截图」两种模式的**可见差别**全在这一抓里（藏不藏我们）。
    /// </summary>
    private void ToggleCaptureMode()
    {
        CaptureHideInk = !CaptureHideInk;
        if (CaptureActive)
        {
            ReleaseCaptureFrozen();
            BeginCaptureFreeze();
            CaptureAdjusting = false;
            _capDrag = CaptureDrag.None;
            CapMinX = CapMaxX = PointerX;
            CapMinY = CapMaxY = PointerY;
            _dirty = true;
        }
        NotifyUiStateChanged();
        Console.WriteLine($"截图模式：{(CaptureHideInk ? "隐藏窗口截图（只拍下层）" : "截图（连批注一起）")}");
    }

    /// <summary>
    /// 按下时抓"冻结帧"：**整块虚拟桌面**，抓到内存里。
    ///
    /// 两种模式抓的东西不一样（和松手时那份口径完全一致）：
    ///   · **隐藏界面**：把整个覆盖层藏起来再抓（拍到的只有下层内容）——
    ///     那 ~60ms 的"闪"从"松手时"挪到了"刚按下时"（老师刚发起操作，预期就是切模式）；
    ///   · **直接截取**：只藏我们自己的**遮罩/取景框/光标**，板书一起拍进去。
    ///     用 `CaptureFrameHidden` 重画一帧就走（不用藏窗口、不用等合成器撤窗口）。
    ///
    /// 抓失败（返回 null）就保持 `CaptureFrozen = false`，松手时退回老路径现抓——
    /// 截图不能因为"冻结没抓到"整个失效。
    /// </summary>
    private void BeginCaptureFreeze()
    {
        ReleaseCaptureFrozen();
        var vs = VirtualScreen;
        int x = (int)MathF.Round(vs.MinX), y = (int)MathF.Round(vs.MinY);
        int w = (int)MathF.Round(vs.MaxX - vs.MinX), h = (int)MathF.Round(vs.MaxY - vs.MinY);
        if (w <= 0 || h <= 0) return;

        byte[] px;
        if (CaptureHideInk)
        {
            // 吸管：**不藏窗口**，只把覆盖层清成透明一帧，再 BitBlt。
            // 老路（HiddenOverlay）会让屏幕明显闪一下；透明帧对 DWM 来说是"这层没东西"，
            // 抓到的仍然是桌面，但窗口不用隐藏/恢复。
            if (ColorPickActive)
            {
                CaptureTransparentFrame = true;
                _dirty = true;
                RenderAll();
                Native.DwmFlush();
                px = ScreenCapture.Grab(x, y, w, h);
                CaptureTransparentFrame = false;
                _dirty = true;
                Doc.InvalidateAll();
                if (px == null)
                {
                    // 透明帧没抓到：退回老路，保证功能不失效。
                    using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                        px = ScreenCapture.Grab(x, y, w, h);
                }
            }
            else
            {
                // 截图（藏墨）：藏整个覆盖层（含接输入小窗）→ 抓 → 恢复。恢复之后把内容层整层作废：
                // 窗口藏过又显示，后缓冲里躺的是旧画面（这条老注释里记过）。
                using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                    px = ScreenCapture.Grab(x, y, w, h);
                Doc.InvalidateAll();
            }
        }
        else
        {
            // 只藏我们自己的东西：重画一帧 → 等合成器贴上去 → 抓 → 恢复。
            CaptureFrameHidden = true;
            _dirty = true;
            RenderAll();
            Native.DwmFlush();
            px = ScreenCapture.Grab(x, y, w, h);
            CaptureFrameHidden = false;
            _dirty = true;
        }

        // **把前台/键盘要回来**（用户 2026-09-30 报的"截完图笔的快捷键没有用了"）：
        // 上面那一下藏窗会把前台让给别人，恢复显示用的是 SW_SHOWNOACTIVATE、不会自己回来。
        // 不补这一句，不只"截完图"的快捷键是死的——**取景中的 Esc/Enter 也收不到**
        //（它们同样靠我们的窗口收键）。详见 RegainKeyboardFocus 那段。
        RegainKeyboardFocus();

        if (px == null)
        {
            Console.WriteLine("截图：冻结帧没抓到，退回松手时抓屏");
            return;
        }
        CaptureFrozenBgra = px;
        CaptureFrozenX = x; CaptureFrozenY = y;
        CaptureFrozenW = w; CaptureFrozenH = h;
        CaptureFrozen = true;
        // 抓屏期间那条"接输入小窗"必须立刻收掉；HiddenOverlay 恢复窗口时可能把它
        // 又显示出来，下一帧再收就晚了——截图确认键正好压在工具条那一块时会被它吃掉。
        UpdateUiInputWindow();
    }

    /// <summary>放掉冻结帧的内存（确认/取消/重新截图时都走它）。</summary>
    private void ReleaseCaptureFrozen()
    {
        CaptureFrozenBgra = null;
        CaptureFrozen = false;
        CaptureFrozenW = CaptureFrozenH = 0;
    }

    /// <summary>当前取景框在**屏幕**（物理像素）里的矩形。</summary>
    internal RectF CaptureFrameScreen()
    {
        float x0 = MathF.Min(CapMinX, CapMaxX), y0 = MathF.Min(CapMinY, CapMaxY);
        float x1 = MathF.Max(CapMinX, CapMaxX), y1 = MathF.Max(CapMinY, CapMaxY);
        return new RectF
        {
            MinX = CanvasToScreenX(x0), MinY = CanvasToScreenY(y0),
            MaxX = CanvasToScreenX(x1), MaxY = CanvasToScreenY(y1),
        };
    }

    /// <summary>从冻结帧里裁出指定屏幕矩形（物理像素）。越界自动夹取（框永远在屏内，兜底而已）。</summary>
    private byte[] CropCaptureFrozen(int x, int y, int w, int h)
    {
        if (!CaptureFrozen || CaptureFrozenBgra == null || w <= 0 || h <= 0) return null;
        var dst = new byte[(long)w * h * 4 <= int.MaxValue ? w * h * 4 : 0];
        if (dst.Length == 0) return null;
        for (int row = 0; row < h; row++)
        {
            int sy = y + row - CaptureFrozenY;
            if (sy < 0 || sy >= CaptureFrozenH) continue;
            int sx = x - CaptureFrozenX;
            int n = w, so = 0;
            if (sx < 0) { n += sx; so = -sx; sx = 0; }
            if (sx + n > CaptureFrozenW) n = CaptureFrozenW - sx;
            if (n <= 0) continue;
            int soB = so * 4 + row * w * 4;
            int siB = (sy * CaptureFrozenW + sx) * 4;
            Array.Copy(CaptureFrozenBgra, siB, dst, soB, n * 4);
        }
        return dst;
    }

    /// <summary>
    /// 截图取景框拖动：锚点 ↔ 当前点（往回拖要跟着缩，否则就是"不跟手"）。
    /// 拖出屏幕也没有意义，夹在屏幕里（和框选那种"贴边=无限延伸"不同：截图就是要看见的那一块）。
    /// </summary>
    private void ExtendCaptureTo(float x, float y)
    {
        var vs = VirtualScreen;
        float sx = Math.Clamp(x, ScreenToCanvasX(vs.MinX), ScreenToCanvasX(vs.MaxX));
        float sy = Math.Clamp(y, ScreenToCanvasY(vs.MinY), ScreenToCanvasY(vs.MaxY));
        CapMinX = MathF.Min(_capAnchorX, sx); CapMaxX = MathF.Max(_capAnchorX, sx);
        CapMinY = MathF.Min(_capAnchorY, sy); CapMaxY = MathF.Max(_capAnchorY, sy);
    }

    /// <summary>
    /// 自检用：只驱动截图取景框的"按下 → 拖"，**不真的抓屏**（抓屏要藏窗口，
    /// 还会把这一帧的测试环境弄乱），也**不换工具**（换工具会污染后面的用例）。
    /// 取景框和框选框用的是同一套锚点算法。
    /// </summary>
    internal void CaptureFrameDragForTest(float ax, float ay, float x1, float y1, float x2 = float.NaN, float y2 = float.NaN)
    {
        CaptureActive = true;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capAnchorX = ax; _capAnchorY = ay;
        CapMinX = CapMaxX = ax; CapMinY = CapMaxY = ay;
        ExtendCaptureTo(x1, y1);
        if (!float.IsNaN(x2)) ExtendCaptureTo(x2, y2);
    }

    /// <summary>
    /// 自检用：把一条路径按"按下 → 一路移动 → 松手"**真实跑一遍**——走的是和鼠标
    /// 完全相同的三个入口（<see cref="BeginMarqueeAt"/> / <see cref="ExtendMarqueeTo"/> /
    /// <see cref="ApplyMarquee"/>），只是不经过操作系统的消息队列。
    /// </summary>
    internal void MarqueeDragForTest(IReadOnlyList<Vector2> path,
                                     bool shift = false, bool alt = false)
    {
        if (path == null || path.Count == 0) return;
        BeginMarqueeAt(path[0].X, path[0].Y, shift, alt);
        for (int i = 1; i < path.Count; i++) ExtendMarqueeTo(path[i].X, path[i].Y);
        ApplyMarquee();               // 松手：按 SelMode 走矩形或套索
    }

    /// <summary>
    /// 套索松手。判据在 <see cref="InkDocument.ApplyLasso"/>（80% 在内 + 贴边无限延伸），
    /// 这里只管三件事：路径太短就当**单击空白**（取消选中，和框选一致）、
    /// 修饰键加减选、清掉路径。
    /// </summary>
    private void ApplyLassoSelection()
    {
        if (LassoPath.Count < 3 || (MqMaxX - MqMinX) < 4f || (MqMaxY - MqMinY) < 4f)
        {
            // 点一下空白就想"什么都别选"——和框选那条同样的处理（见 ApplyMarquee）。
            Doc.Selected.Clear();
            LassoPath.Clear();
            _dirty = true;
            return;
        }

        int n = Doc.ApplyLasso(LassoPath, ViewportCanvas, LassoEdgeSnapLogical * DpiScale,
                               additive: _lassoAdditive, subtractive: _lassoSubtractive);
        Console.WriteLine(_lassoSubtractive
            ? $"lasso removed {n} strokes（剩 {Doc.Selected.Count} 条）"
            : $"lasso selected {n} strokes（圈 {LassoPath.Count} 个点"
              + (_lassoAdditive ? $"，Shift 加选，共 {Doc.Selected.Count} 条）" : "）"));
        LassoPath.Clear();
        _dirty = true;
    }

    /// <summary>
    /// "圈到屏幕边"的判定容差（逻辑像素）：指针离可见区域边线这么近就当作贴边，
    /// 那一段按无限延伸处理（见 <see cref="InkDocument.ApplyLasso"/>）。
    ///
    /// 取 4：指针能被拖到窗口外（有捕获），所以贴边基本都能满足；取太大又会让
    /// "离边还有一点点"的圈也偷偷延伸出去，把屏幕外的墨一起圈进来。
    /// </summary>
    internal const float LassoEdgeSnapLogical = 4f;

    /// <summary>
    /// 读一条指针消息，顺便把两个时标记下来（延时探针用）：
    ///   pi.PerformanceCount —— 系统给的硬件/驱动时标（QPC 计数）
    ///   现在                —— 我们真正拿到它的时刻
    /// 两者之差 = 输入栈 + 我们自己的消息队列。**这一段里只有队列是我们的责任**，
    /// 而队列拖延的直接原因就是 Present(1) 把线程卡在垂直同步里。
    /// </summary>
    /// <summary>
    /// 触摸触点的**接触面积**（矩形长边，物理像素）。拿不到 / 这块屏不报 → 0。
    /// 2026-10-24 起只用于 TouchStats 采样，不再触发手掌橡皮；同时把矩形宽高
    /// 带出去，方便以后重新设计面积规则。
    /// </summary>
    private static float ReadTouchSize(uint id, out int contactW, out int contactH)
    {
        contactW = contactH = 0;
        // 用**显式缓冲区**（160 字节）而不是 `out POINTER_TOUCH_INFO`：后者走的是
        // 运行时按托管结构体大小分配的栈槽，合成指针那条路上实测会 AV；
        // 显式给足缓冲区 + `PtrToStructure` 最稳（这里是纯读，没有写回）。
        IntPtr buf = Marshal.AllocHGlobal(160);
        try
        {
            if (!Native.GetPointerTouchInfo(id, buf)) return 0f;
            var ti = Marshal.PtrToStructure<Native.POINTER_TOUCH_INFO>(buf);
            int w = ti.rcContact.Width, h = ti.rcContact.Height;
            if (w <= 0 && h <= 0) { w = ti.rcContactRaw.Width; h = ti.rcContactRaw.Height; }
            contactW = Math.Max(0, w);
            contactH = Math.Max(0, h);
            return MathF.Max(w, h);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool ReadPointer(uint id, out float x, out float y, out float pressure,
                             out bool inverted, out uint pointerType)
    {        x = y = 0; pressure = 0.5f; inverted = false; pointerType = 0;
        if (!Native.GetPointerInfo(id, out var pi)) return false;
        _lastInputPerfQpc = pi.PerformanceCount;
        _lastInputMsgQpc = Qpc.Now;
        if (LatencyRecording && _prevInputMsgQpc != 0)
        {
            double gap = Qpc.Ms(_prevInputMsgQpc, _lastInputMsgQpc);
            if (gap > 0 && gap < 500) Latency.AddInputGap(gap);
        }
        _prevInputMsgQpc = _lastInputMsgQpc;
        _framePoints++;
        x = pi.ptPixelLocationX;
        y = pi.ptPixelLocationY;
        pointerType = pi.pointerType;
        // 笔的"出身"（笔尖在不在屏幕上）：**手写板和触屏笔的落点规则不同**，这条必须
        // 每条笔消息都确认一遍（换设备/换笔会变），结果按设备句柄缓存、只查一次。
        // 鼠标/触摸不查（它们的路不需要这个信息）。
        if (pi.pointerType == Native.PT_PEN)
            PenDeviceOnScreen = QueryPenOnScreen(pi.sourceDevice);
        if (pi.pointerType == Native.PT_PEN && Native.GetPointerPenInfo(id, out var pen))
        {
            pressure = pen.pressure / 1024f;
            if (pressure <= 0.01f) pressure = 0.5f;
            inverted = (pen.penFlags & (Native.PEN_FLAG_INVERTED | Native.PEN_FLAG_ERASER)) != 0;
        }
        // 留给光标那一族用（EffectiveTool）：**每条消息都写**，鼠标 / 触摸自然落回 false。
        // 光标的形状必须在"悬停"（还没落笔）时就对，所以不能等 OnPointerDown 里那个局部变量。
        LastPointerInverted = inverted;
        return true;
    }

}
