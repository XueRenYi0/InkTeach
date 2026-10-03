using System.Numerics;

namespace InkEngine;

/// <summary>选中框上的手柄。顺序按钟表方向排，方便按下标算"对面那个"。</summary>
internal enum SelHandle
{
    None = 0,
    TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left,
    /// <summary>旋转手柄，在上边中点外侧。</summary>
    Rotate,
    /// <summary>
    /// 图形的**第一个端点**手柄（只有直线 / 箭头会用到）。
    ///
    /// 为什么另开两个值、而不是复用 TopLeft/BottomRight：端点手柄的语义是
    /// "改这一条图形的定义元素"（改几何），而不是"按包围盒缩放"（改变换）。
    /// 两者动作完全不同（见 SetStrokeGeometryAction 的注释），共用一个值迟早会串。
    /// 值排在最后，原来那几个的数字一个都没动。
    /// </summary>
    EndpointA,
    /// <summary>图形的第二个端点手柄，见 <see cref="EndpointA"/>。</summary>
    EndpointB,
    /// <summary>
    /// **三个顶点**手柄（三角形 / 平行四边形，2026-09-19 第二批第②步）。
    ///
    /// 为什么不用现成的八向柄顶替：那八个的语义是"按包围盒的哪条边/哪个角缩放"，
    /// 而这三个是"这一个**顶点**"——拖动语义完全不同（拖顶点改几何、拖角改变换），
    /// 共用一个值迟早会串（和 EndpointA 那一条是同一个理由）。
    /// 值排在最后，前面那些数字一个都没动。
    /// </summary>
    VertexA, VertexB, VertexC,
    /// <summary>
    /// **第四个顶点**手柄（坐标系 / 数轴，2026-09-19 第三批）。
    ///
    /// 这两个新种类的定义元素是**四个点**（外框两角 / 原点 / 单位长度点，见
    /// <see cref="StrokeKind.Coordinate"/>），所以顶点柄多出一格。
    /// 值排在最后，前面那些数字一个都没动。
    /// </summary>
    VertexD,
    /// <summary>
    /// **焦点三角形的顶点 P**（椭圆（带焦点），2026-09-22）。
    ///
    /// 单独占一格、不复用顶点柄那几格：它**不是 `Points` 里的控制点**——P 是"参数角"
    /// 算出来的（见 <see cref="Stroke.ConicEllipsePointLocal"/>），拖动语义也不同
    /// （拖它 = 改那个角，椭圆本身一动不动）。混进顶点柄会让"顶点手柄 ↔ 控制点下标"
    /// 那条约定撒谎。
    ///
    /// 值排在最后，前面那些数字一个都没动。
    /// </summary>
    FocusPoint,
}

/// <summary>
/// 图形的**定义元素手柄**（2026-09-19 第二批加）。
///
/// 与 <see cref="SelHandle"/> 的分工：SelHandle 是"通用框上的哪个位置"（八向 + 旋转），
/// 这里是"哪一个定义元素"。椭圆的四个轴端点在参数上语义不同（左右只改 a、上下只改 b），
/// 用通用框的 Left/Top 表达会读不清楚；三角形 / 平行四边形的三个顶点同理。
/// 两者之间的映射只写在
/// <see cref="SelectionHandles.SelHandleOf"/> / <see cref="SelectionHandles.HandleOf"/> 里。
/// </summary>
internal enum ShapeHandle
{
    None = 0,
    /// <summary>直线 / 箭头的起点、**圆的圆心**、椭圆的中心。（拖它 = 整体平移）</summary>
    Anchor,
    /// <summary>直线 / 箭头的终点、**圆的圆周点**。（拖它 = 改这个定义元素）</summary>
    Rim,
    /// <summary>
    /// **横向 / 纵向的参数手柄**（用户 2026-09-20 精简："**一个量只留一个把手**"）。
    ///
    /// 原来椭圆有四个（左右 + 上下），可左右两个**都只改 a**、上下两个**都只改 b**——
    /// 同一个量配两个把手，抓取目标多了一个、信息量一个没多。现在各留一个：
    ///   · <see cref="AxisRight"/> = 椭圆的**右端点**（只改 a）、抛物线的**通径端点**（只改 p）；
    ///   · <see cref="AxisTop"/>   = 椭圆的**上端点**（只改 b）、双曲线的**渐近线角点**（只改斜率）、
    ///     正弦 / 余弦的**谷点**（横改周期、竖改振幅）。
    /// 抓不到左边就把右边那个往左拖过中心（拖过头会被最小值卡住，等于一路缩到底），
    /// 所以删掉的两个不是"少了功能"，是少了两个重复的抓取目标。
    /// </summary>
    AxisRight, AxisTop,
    /// <summary>
    /// 三角形 / 平行四边形的三个顶点（0/1/2 = `Points` 里的下标）。
    ///
    /// 拖哪个只动哪个；平行四边形的**第四个角不给手柄**（用户定：它是算出来的，
    /// `第2 + 第3 − 第1`，拖它没有意义，见 计划-图形工具.md 9.5）。
    /// 下标从 Vertex0 连续排，所以 `h - Vertex0` 就是点序号（见 <see cref="SelectionHandles.VertexIndex"/>）。
    ///
    /// 坐标系 / 数轴用的是**同一族**里的第四格 <see cref="Vertex3"/>：
    /// 它们的定义元素是四个点，本身没有"推导出来的点"这回事。
    /// </summary>
    Vertex0, Vertex1, Vertex2,
    /// <summary>第四个顶点（只有坐标系 / 数轴有，见 <see cref="SelectionHandles.VertexIndex"/>）。</summary>
    Vertex3,
    /// <summary>
    /// **焦点三角形的顶点 P**（只有椭圆（带焦点）有，2026-09-22）。
    ///
    /// 位置由参数角算出来（见 <see cref="SelectionHandles.ShapeHandleLocal"/>），
    /// 所以它**不在** `Points` 里，也没有"第几个点"这回事——这就是它不复用 Vertex 那几格的原因。
    /// </summary>
    FocusPoint,
}

/// <summary>
/// 操作条上九格的**语义**（顺序即排布，见 <see cref="SelectionHandles.BarButtonCount"/>）。
///
/// 用枚举而不是裸下标：渲染（画哪个图标）、命中（点到哪一格）、动作（那一格干什么）
/// 三处必须对上，写死数字迟早错位一格——而且这条在自检里是要逐个核对的。
/// </summary>
internal enum SelBarButton
{
    /// <summary>收起工具条（参考实现里条首那个 ✕）：收起后只剩一个小圆钮，点它展开。</summary>
    Collapse = 0,
    /// <summary>颜色 / 粗细面板。</summary>
    Color = 1,
    /// <summary>锁定 / 解锁（锁定的对象能选中但拖不动，见 调研 第四节）。</summary>
    Lock = 2,
    /// <summary>层级小面板（置顶 / 置底）。</summary>
    Layer = 3,
    /// <summary>导出（另存为）。</summary>
    Export = 4,
    /// <summary>
    /// **存入图库**（用户 2026-09-22 要的"图像收藏"）：把选中的对象存进图形库，
    /// 之后从图形面板最后那一段「图库」里取出来复用。见 <see cref="ShapeLibrary"/>。
    /// </summary>
    Library = 5,
    /// <summary>复制拖拽模式（点一下进入/退出）。</summary>
    Copy = 6,
    FlipH = 7,
    FlipV = 8,
    Delete = 9,
}

/// <summary>
/// **吸到了什么**（一族"特殊形状"吸附，规格 9.6）。
///
/// 为什么要一个专门的枚举、而不是只报个 bool：三条硬要求里有一条是
/// "**吸住时要说得出吸到了什么**"——胶囊上要写"等边"/"正方形"/"菱形"这些字。
/// 枚举值同时也是"哪一种约束"的标识，自检直接按它断言。
/// 文案在 <see cref="SelectionHandles.ShapeSnapLabel"/>（只有那一份，画与脏区共用）。
/// </summary>
internal enum ShapeSnapKind
{
    /// <summary>没吸住（或这一拖根本不适用吸附）。</summary>
    None = 0,
    /// <summary>三角形：两腰等长（被拖的点落在另两点的中垂线上）。</summary>
    Isosceles,
    /// <summary>三角形：三边相等。</summary>
    Equilateral,
    /// <summary>三角形：某个内角恰好 90°。</summary>
    RightAngle,
    /// <summary>矩形：两边相等（正方形）。</summary>
    Square,
    /// <summary>椭圆：a = b（正圆）。</summary>
    Circle,
    /// <summary>平行四边形：邻边相等（菱形）。</summary>
    Rhombus,
    /// <summary>平行四边形：邻边垂直（矩形）。</summary>
    Rectangle,
    /// <summary>
    /// 棱柱：侧棱**竖直**（＝直棱柱）。用户 2026-09-20："直棱柱有一个轻微吸附"。
    /// 和前几档不一样，它不发生在"拖顶点"上，而是**画棱柱第 2 笔**的时候
    ///（见 Engine.ApplyStepGeometry），所以它由 `Engine.StepSnap` 报出来——
    /// 但**走的是同一颗胶囊、同一套语言**（"吸到了什么"）。
    /// </summary>
    RightPrism,
    /// <summary>
    /// 棱锥：顶点**在底心正上方**（＝直棱锥）。和 <see cref="RightPrism"/> 是**同一件事、
    /// 同一个判据、同一个容差**，只是"顶上那个东西"是顶点而不是一个面——
    /// 分成两档纯粹是为了胶囊上写的字对（老师说出口的是"直棱锥"）。
    /// </summary>
    RightPyramid,
    /// <summary>棱台：上底中心**在底心正上方**（＝直棱台）。同上，只是名字不同。</summary>
    RightFrustum,
}

/// <summary>
/// 浮动面板的种类。同一时刻只开一个。
/// </summary>
internal enum SelPanel
{
    None = 0,
    /// <summary>颜色 / 粗细 / 线型（挂在"颜色"那一格下）。</summary>
    Ink = 1,
    /// <summary>层级（挂在"层级"那一格下）。</summary>
    Layer = 2,
    // 注：**没有"导出"面板了**。曾经有过一种（两格：PNG 透明底 / JPG 白底），
    // 是为了绕开"选不到 jpg"；那个 bug 的真因（覆盖层每秒抢层）修掉之后，
    // 用户 2026-09-17 说"这两个图标没用了"，于是删掉——点导出直接弹系统对话框，
    // 格式在它的类型栏里选（见 ExportFormats）。
}

/// <summary>
/// 选中框的坐标系 = 一个矩形 + 一个"框坐标 → 画布坐标"的变换。
///
/// **一律轴对齐**（用户 2026-09-16 定的）：不管选了一条还是多条，框都是
/// "这些对象的**墨迹**在画布坐标下的并集"，也就是屏幕上那个正矩形。
///
/// 早先的做法是"单选跟对象转（用对象自己的坐标系）、多选轴对齐"，理由是
/// Figma / PowerPoint / Illustrator 都那样。改成**一律轴对齐**的取舍：
///   · 得到：斜着的对象，框和手柄也永远正着——8 个手柄在哪就是哪，
///     旋转柄永远在框的正上方，一眼能读；
///   · 代价：斜着的对象框会**虚胖**（45° 的长条会变成一个大正方形），
///     旋转中框每帧重新贴合（会"呼吸"），缩放/翻转按屏幕的轴走；
///   · 反面那一档（跟对象转）的代价是：斜着的时候手柄跟着歪，投影上不好认，
///     而且"这条到底往哪个方向拉是拉长"要看对象自己的朝向——多数人猜不到。
///
/// 数学没变：手柄位置在框坐标里算，再变换到画布；命中判定把指针反变换回框坐标。
/// `ToCanvas` 现在恒为单位矩阵，但它保留着——单条和多条走的是**同一条**公式
/// （框 = 并集），将来真要加"整组的朝向"时，只需要让它不再是单位阵。
/// **开销是 O(1)**：一个矩形、一个 3×2 矩阵，跟选区里有几个对象、画面上有
/// 多少批注都没关系。
/// </summary>
internal struct SelectionFrame
{
    /// <summary>框坐标下的矩形。</summary>
    public RectF Local;
    /// <summary>框坐标 → 画布坐标。</summary>
    public Matrix3x2 ToCanvas;

    public bool IsEmpty => Local.IsEmpty;

    public Vector2 ToCanvasPoint(Vector2 p) => Vector2.Transform(p, ToCanvas);

    /// <summary>把画布坐标的点变回框坐标（命中判定用）。</summary>
    public Vector2 ToLocalPoint(Vector2 p)
    {
        if (ToCanvas.IsIdentity) return p;
        return Matrix3x2.Invert(ToCanvas, out var inv) ? Vector2.Transform(p, inv) : p;
    }

    /// <summary>
    /// 框在画布上占的轴对齐矩形。脏区、操作条定位用它——
    /// 框本身可能是斜的，但"它占了屏幕上哪一块"永远是个正矩形。
    /// </summary>
    public RectF CanvasAabb
    {
        get
        {
            if (Local.IsEmpty) return RectF.Empty;
            if (ToCanvas.IsIdentity) return Local;
            var p0 = ToCanvasPoint(new Vector2(Local.MinX, Local.MinY));
            var p1 = ToCanvasPoint(new Vector2(Local.MaxX, Local.MinY));
            var p2 = ToCanvasPoint(new Vector2(Local.MaxX, Local.MaxY));
            var p3 = ToCanvasPoint(new Vector2(Local.MinX, Local.MaxY));
            var r = RectF.Empty;
            r.Add(p0.X, p0.Y); r.Add(p1.X, p1.Y); r.Add(p2.X, p2.Y); r.Add(p3.X, p3.Y);
            return r;
        }
    }
}

/// <summary>
/// 选中框的手柄布局，以及"拖动某个手柄 → 变换矩阵"的换算。
///
/// **这一层只有数学，没有一行绘制。** 分开的好处有两个：
///
///   ① 换观感不用动它。手柄画多大、什么颜色、要不要显示四边中点，
///      是界面层的事；这里只回答"手柄在哪、点到哪个、拖出来是什么矩阵"。
///   ② 能单独做自检（`--handletest`）。变换矩阵错了是那种"看起来能动、
///      但缩放之后再撤销回不到原样"的问题，肉眼很难发现。
///
/// 三条手感上的决定，都写在对应常量的注释里：
///   · 命中区是视觉尺寸的两倍（投影上写字手是抖的）；
///   · 旋转手柄单独放在上边中点外侧，不和四角缩放抢位置；
///   · **拖过头不翻转，而是夹住**（翻转只能从操作条按按钮，避免误触）。
/// </summary>
internal static class SelectionHandles
{
    /// <summary>
    /// 手柄的视觉直径（**逻辑**像素）。
    ///
    /// 注意单位：这是"看起来多大"，与 DPI 无关。在 2 倍屏上会画成 28 物理像素。
    /// 第一版我按物理像素实现（7 逻辑 = 14 物理），在这台机器上量出来只有
    /// 7 逻辑像素，比设计意图小了一半，投影上会看不清——尺寸一律用逻辑像素。
    /// </summary>
    public const float VisualSizeLogical = 14f;

    /// <summary>
    /// 旋转手柄的直径（逻辑像素）。
    ///
    /// 比普通手柄（14）大一圈：它里面要装一个图标，14 的话图标只剩几个像素，
    /// 投影上就糊成一个点。大一圈也让"这是个按钮"更好认。
    /// </summary>
    public const float RotateGripLogical = 20f;

    /// <summary>旋转手柄里那个图标的尺寸（逻辑像素）。</summary>
    public const float RotateGlyphLogical = 13f;

    /// <summary>
    /// 手柄的命中半径（逻辑像素）。**是视觉尺寸的两倍**：
    /// 投影上写字手是抖的，要求点准一个小方块不现实。
    /// </summary>
    public const float HitRadiusLogical = 14f;

    /// <summary>
    /// 这一次该把通用手柄画多大（画布单位，含 DPI）。
    ///
    /// **大对象拿满 <see cref="VisualSizeLogical"/>；小对象视觉柄跟着缩、命中半径不缩**：
    ///   · 判据 = 框**短边**的 40%，夹在 [<see cref="VisualSizeLogical"/> 的一半, 满尺寸]；
    ///     所以短边 < 35 逻辑像素才开始缩，短边 17.5 逻辑像素时到下限 7。
    ///   · 命中半径**不跟着缩**（还是 <see cref="HitRadiusLogical"/> 28×28）：
    ///     投影上"看得小了点"只是观感，"点不中"才是事故——命中区大一点永远没错。
    ///
    /// 为什么需要它（2026-10-05 用户："8 个点/特殊点有点大；大图形无所谓，
    /// 图形太小了还这么大不合适"）：14 逻辑的方块在 2 倍屏上是 28 物理像素，
    /// 小图形（几十像素）四角一放就把内容盖住了。
    /// 边中点柄更早的让位在 <see cref="ThinEdges"/>（绘制/命中同一把尺子）。
    /// 参考（思路，不抄）：Figma 在小选区时先去掉边中点、只留四角；白板类工具在
    /// 对象小于手柄时会把手柄缩一档——共同点是**先保命中、再谈好看**。
    /// </summary>
    public static float VisualHandleSize(in RectF box, float dpiScale)
    {
        float full = VisualSizeLogical * dpiScale;
        float min = full * 0.5f;
        float shortSide = MathF.Min(box.MaxX - box.MinX, box.MaxY - box.MinY);
        return Math.Clamp(shortSide * 0.4f, min, full);
    }

    /// <summary>旋转手柄离上边的距离（逻辑像素）。</summary>
    public const float RotateOffsetLogical = 30f;

    /// <summary>按住 Shift 旋转时的对齐增量（度）。</summary>
    public const float RotationSnapDegrees = 15f;

    /// <summary>
    /// 不按修饰键时的**软吸附**网格（度）。靠近网格线才吸，其余角度自由。
    ///
    /// 网格取 90°（0 / 90 / 180 / 270）：投影上画示意图最常做的就是"摆正"，
    /// 而 45° 这种角度老师要么用笔画，要么宁可自己转——吸 45° 反而碍事。
    /// 想要中间角度：Shift 是 15° 硬网格，Alt 是完全自由。
    /// </summary>
    public const float RotationSoftSnapDegrees = 90f;

    /// <summary>
    /// 软吸附的容差（度）。吸住时度数标签会变色，用户知道"是吸上的不是转到的"，
    /// 所以容差可以给得舒服一点（±3°：投影上写字手抖个两三度很正常）。
    /// </summary>
    public const float RotationSoftSnapToleranceDegrees = 3f;

    /// <summary>
    /// 缩放下限。**刻意不为 0**：矩阵一旦退化（行列式为 0）就求不出逆矩阵，
    /// 撤销那一步会静默失效，对象就卡死在缩小状态里回不来了。
    /// </summary>
    public const float MinScale = 0.02f;

    /// <summary>把一个手柄换算成它在包围盒上的相对位置（0~1）。</summary>
    private static (float u, float v) Uv(SelHandle h) => h switch
    {
        SelHandle.TopLeft => (0f, 0f),
        SelHandle.Top => (0.5f, 0f),
        SelHandle.TopRight => (1f, 0f),
        SelHandle.Right => (1f, 0.5f),
        SelHandle.BottomRight => (1f, 1f),
        SelHandle.Bottom => (0.5f, 1f),
        SelHandle.BottomLeft => (0f, 1f),
        SelHandle.Left => (0f, 0.5f),
        SelHandle.Rotate => (0.5f, 0f),      // 位置另加上偏移
        _ => (0.5f, 0.5f),
    };

    /// <summary>手柄在画布坐标里的位置。</summary>
    public static Vector2 Position(SelHandle h, in RectF b, float dpiScale)
    {
        var (u, v) = Uv(h);
        float x = b.MinX + (b.MaxX - b.MinX) * u;
        float y = b.MinY + (b.MaxY - b.MinY) * v;
        if (h == SelHandle.Rotate) y -= RotateOffsetLogical * dpiScale;
        return new Vector2(x, y);
    }

    /// <summary>手柄在**画布坐标**里的位置。</summary>
    public static Vector2 CanvasPosition(SelHandle h, in SelectionFrame f, float dpiScale)
        => f.ToCanvasPoint(Position(h, f.Local, dpiScale));

    // =====================================================================
    //  直线 / 箭头：两个端点手柄
    //
    //  依据是"手柄 = 图形的定义元素"（见 调研-图形工具.md 2.4）：直线由**两个点**
    //  定义，所以只给两个端点手柄 + 一个旋转柄。8 个缩放柄对它不只是多余，还是**错的**：
    //  左右拉伸 2 倍会把 (0,0)→(100,50) 变成 (0,0)→(200,50)，倾斜角从 26.57° 变成 14.04°
    //  ——用户只是想"把它变长"，线的角度却变了。
    // =====================================================================

    /// <summary>
    /// 选区是不是"单选一条**有端点语义**的图形"（直线 / 箭头）。是的话用它替代通用 8 手柄。
    ///
    /// 只认单选：多选要的是"整组缩放"，那还得靠通用框（见 调研 2.4 的最后一行）。
    /// 图像 / 自由笔迹 / 矩形 / 椭圆也没有端点语义（矩形有四个角、椭圆有半轴，是下一轮的事）。
    /// </summary>
    public static bool EndpointEditable(IReadOnlyList<Stroke> sel, out Stroke stroke)
    {
        stroke = null;
        if (sel == null || sel.Count != 1) return false;
        var s = sel[0];
        if (s == null || s.IsImage || s.Points.Count < 2) return false;
        if (s.Kind != StrokeKind.Line && s.Kind != StrokeKind.Arrow) return false;
        stroke = s;
        return true;
    }

    /// <summary>端点手柄的槽位（0 = 第一个控制点、1 = 最后一个）→ Points 里的下标。</summary>
    public static int EndpointIndex(Stroke s, int slot) => slot <= 0 ? 0 : s.Points.Count - 1;

    // =====================================================================
    //  图形的**定义元素**手柄（直线 / 箭头 / 圆 / 椭圆）
    //
    //  为什么在 SelHandle 之上再分一层：SelHandle 说的是"通用框上的哪个位置"
    //  （八个方向 + 旋转），而这里说的是"这是哪个定义元素"。
    //  椭圆的四个轴端点在**参数**上是不同语义的（左右只改 a、上下只改 b），
    //  拿通用框的 Left/Top 去表达，读代码的人分不清"这是框的左边中点还是长轴左端"。
    //  两层的映射只写在 SelHandleOf / HandleOf 这两个函数里。
    //  见 计划-图形工具.md 9.1（"手柄 = 图形的定义元素 ＋ 一个旋转柄"）。
    // =====================================================================

    /// <summary>
    /// 选区是不是"单选一个由定义元素驱动的图形"（直线 / 箭头 / 圆 / 椭圆 / 三角形 / 平行四边形）。
    ///
    /// 只认单选：多选要的是"整组缩放"，那还得靠通用框。
    /// **矩形保留八个通用柄**（用户定：它是唯一"拉了还是矩形"的图形），
    /// 图像 / 自由笔迹没有"定义元素"这回事。
    /// </summary>
    public static bool ShapeEditable(IReadOnlyList<Stroke> sel, out Stroke stroke)
    {
        stroke = null;
        if (sel == null || sel.Count != 1) return false;
        var s = sel[0];
        if (s == null || s.IsImage || s.Points.Count < 2) return false;
        // 名单只有一份（见 Stroke.IsShapeKind）——原来这里手写了十三项，
        // 和 Model 里那份、以及引擎 `IsShapeTool` 那份三处并行，加图形要改三遍。
        if (!Stroke.IsShapeKind(s.Kind)) return false;
        stroke = s;
        return true;
    }

    /// <summary>
    /// 这个图形有哪些手柄，写进 <paramref name="dst"/>，返回个数（最多 5 个，调用方给 5 个格子）。
    ///
    /// 用"写进缓冲 + 返回个数"而不是返回 List：手柄命中在悬停时**每帧都跑**，
    /// 每帧 new 一个 List 是白给的垃圾（这也是仓库里 SpatialGrid 那套的老规矩）。
    /// 顺序 = 绘制顺序；命中时**倒着**遍历（后画的先中），见 HitTest。
    /// </summary>
    public static int ShapeHandlesOf(Stroke s, Span<ShapeHandle> dst)
    {
        if (s == null) return 0;
        switch (s.Kind)
        {
            case StrokeKind.Line:
            case StrokeKind.Arrow:
            case StrokeKind.Circle:
                if (dst.Length < 2) return 0;
                dst[0] = ShapeHandle.Anchor;
                dst[1] = ShapeHandle.Rim;
                return 2;

            case StrokeKind.Ellipse:
                // **两个**（用户 2026-09-20 精简：原来是五个）：
                //   AxisRight = **右端点**，只改 a（原来左右两个都管 a，留一个）
                //   AxisTop   = **上端点**，只改 b（原来上下两个都管 b，留一个）
                // 中心那个手柄也去掉了：**拖整条就是平移**，再在中心放一个把手
                // 只是多了一个"抓了也没多出功能"的目标。
                if (dst.Length < 2) return 0;
                dst[0] = ShapeHandle.AxisRight;
                dst[1] = ShapeHandle.AxisTop;
                return 2;

            case StrokeKind.ConicEllipse:
            {
                // **带焦点的那种椭圆**（2026-09-22）：两个半轴手柄和上面那个椭圆
                // **一模一样**（定义元素就是同一套），只在"有焦点三角形"那一档多一个 P。
                //
                // P **放在最后**：命中是倒着找的（后画的先中），而 P 就在椭圆上、
                // 和半轴端点可能离得很近——它更"具体"（拖它只动这个点），该优先。
                if (dst.Length < 2) return 0;
                dst[0] = ShapeHandle.AxisRight;
                dst[1] = ShapeHandle.AxisTop;
                if (s.FocusTriangle && dst.Length >= 3)
                {
                    dst[2] = ShapeHandle.FocusPoint;
                    return 3;
                }
                return 2;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
                // **只有三个**：多边形是由顶点定义的，第四个角（平行四边形）是算出来的。
                if (dst.Length < 3 || s.Points.Count < 3) return 0;
                dst[0] = ShapeHandle.Vertex0;
                dst[1] = ShapeHandle.Vertex1;
                dst[2] = ShapeHandle.Vertex2;
                return 3;

            case StrokeKind.Coordinate:
                // **三个**（外框两角 / 原点）：这三个都是真的定义元素，都上手柄。
                if (dst.Length < 3 || s.Points.Count < 3) return 0;
                dst[0] = ShapeHandle.Vertex0;
                dst[1] = ShapeHandle.Vertex1;
                dst[2] = ShapeHandle.Vertex2;
                // **开了网格还有第四颗：格距**（用户 2026-09-24："那个第一个方格上那个格点，
                // 它也做一个空心点，这样的话我就可以通过拖动那个点来改变这个方格的大小"）。
                // 它**不是控制点**——拖它只改 `Stroke.AxisGridStep`（见 Engine 那一支）。
                //
                // ⚠ **只有开网格时才给**：没画格子就没有"第一个格子"这个可见的东西，
                // 那时给一颗"拖了也看不见变化"的手柄，正是 2026-09-19 撤掉
                // "单位长度点"时说的那种"画都不画却点得到"的死元素。
                if (s.Grid && dst.Length >= 4)
                {
                    dst[3] = ShapeHandle.Vertex3;
                    return 4;
                }
                return 3;

            case StrokeKind.NumberLine:
                // **两个**（左端 / 右端）。没有刻度之后，"零点"和"单位长度点"退场了
                // （见 StrokeKind.NumberLine），所以这里是两个手柄而不是四个。
                if (dst.Length < 2 || s.Points.Count < 2) return 0;
                dst[0] = ShapeHandle.Vertex0;
                dst[1] = ShapeHandle.Vertex1;
                return 2;

            // **四种曲线（抛物线 / 双曲线 / 正弦 / 余弦）不给任何特殊点手柄**
            //（用户 2026-09-20 定："抛物线、双曲线、正弦、余弦，把特殊点砍掉，
            //  通通按常规操作 —— 给操作柄和旋转，和正常的一样"）。
            //
            // 它们和矩形 / 立体图形一样走**通用框**（八个缩放柄 ＋ 旋转柄）：
            // 通用框本质是"纯变换操作器"，对任何对象都成立，所以这里返回 0 就行。
            // 具体到"手感"上的两处变化：
            //   · 拉伸变得**等比/自由**：框怎么拉曲线怎么变（不再是"只改 p / 只改 a"）；
            //   · 跟着消失的还有两条读数（姿态角 / 半轴）——那是一起拆的
            //     （见 Engine 的顶点读数那段），因为它挂的正是这些手柄。
            // 抛物线的**朝向**仍然在图形面板那一格切（画/编辑两套口径不变）。
            default:
                return 0;
        }
    }

    /// <summary>顶点手柄 → `Points` 里的下标（0/1/2/3）；不是顶点柄就返回 -1。</summary>
    public static int VertexIndex(ShapeHandle h)
        => h is ShapeHandle.Vertex0 or ShapeHandle.Vertex1 or ShapeHandle.Vertex2 or ShapeHandle.Vertex3
            ? (int)(h - ShapeHandle.Vertex0) : -1;

    /// <summary>
    /// 这个 **SelHandle** 是不是顶点柄（`VertexA` ～ `VertexD`）。
    ///
    /// 归一成一条判据是必须的：引擎问"按下的是不是定义元素手柄"用的是它
    /// （见 Engine 的手势分流）。以前那里写的是一串 `VertexA or VertexB or VertexC`，
    /// 2026-09-19 加第四格 `VertexD` 时**就漏改了**——症状不是报错，而是
    /// "拖单位长度点什么都没发生"（`--axistest` 当场抓到）。列一串名字的地方，
    /// 加一格就漏一处；合成一个函数之后，加格子只需要改这里。
    /// </summary>
    public static bool IsVertexHandle(SelHandle h)
        => h is SelHandle.VertexA or SelHandle.VertexB or SelHandle.VertexC or SelHandle.VertexD;

    /// <summary>定义元素在**局部坐标**里的位置（= 它在 `Points` 里对应的那个定义）。</summary>
    public static Vector2 ShapeHandleLocal(Stroke s, ShapeHandle h)
    {
        // 顶点手柄直接就是控制点本身（三角形 / 平行四边形：定义元素 = 顶点）。
        int vi = VertexIndex(h);
        if (vi >= 0)
        {
            // **坐标系的第 4 颗不是控制点，是"格距"**（2026-09-24）：它落在
            // "第一象限第一个格子的外角"，由原点 ＋ 格距现算（见 AxisGridStepHandleLocal）。
            if (vi == 3 && s.Kind == StrokeKind.Coordinate) return s.AxisGridStepHandleLocal();
            if (vi >= s.Points.Count) return Vector2.Zero;
            return new Vector2(s.Points[vi].X, s.Points[vi].Y);
        }

        var c = s.ShapeCenterLocal;

        // 四种曲线**不再有特殊点手柄**（2026-09-20 第五批），所以这里也没有它们的分支了：
        // 那些"手柄落在曲线旁边哪个位置"的算式（谷点 / 经过点 / 渐近线角点）随手柄一起删掉。
        return h switch
        {
            ShapeHandle.Anchor => c,
            ShapeHandle.Rim => s.RimLocalPoint(),
            // 椭圆只剩"右端点（管 a）＋ 上端点（管 b）"两个，见 ShapeHandlesOf。
            ShapeHandle.AxisRight => c + new Vector2(s.SemiAxisALocal, 0f),
            ShapeHandle.AxisTop => c + new Vector2(0f, -s.SemiAxisBLocal),   // 屏幕 y 向下，"上"是 -y
            // 焦点三角形的顶点 P：由参数角算（不在 Points 里，见 ShapeHandle.FocusPoint）
            ShapeHandle.FocusPoint => s.ConicEllipsePointLocal(),
            _ => c,
        };
    }

    /// <summary>定义元素在**画布坐标**里的位置（手柄画在哪、读数的锚点，都用它）。</summary>
    public static Vector2 ShapeHandleCanvasPosition(Stroke s, ShapeHandle h)
        => Vector2.Transform(ShapeHandleLocal(s, h), s.Transform);

    /// <summary>
    /// 这个图形给不给旋转柄。
    ///
    /// **圆不给**（用户定："圆转了看不出来"）；
    /// **坐标系 / 数轴也不给**（2026-09-19 第三批）：它们的存在意义就是"水平轴 + 竖直轴"，
    /// 转歪了既不是坐标系也不是数轴——而且刻度、箭头、网格全是按"轴对齐"画的，
    /// 给个旋转柄等于把一个画不出来也说不清的状态开放给用户。
    /// 不给就是连命中都不做，而不是"画不出来但点得到"。
    ///
    /// **2026-09-20 第五批：四种曲线改成"常规操作"之后，它们也给旋转柄了**
    ///（用户："通通按常规操作 —— 给操作柄和旋转，和正常的一样"）。
    /// 上一批不给的理由是"转歪了就不像课本上那条曲线"；现在的口径是：图形就是要能像
    /// 图形那样摆弄，转歪了自己转回来（而且通用框的旋转本来就是任何对象都有的能力）。
    /// 抛物线的朝向切换仍在图形面板那一格（不受旋转影响）。
    /// </summary>
    public static bool RotateHandleVisible(Stroke s)
        => !(s.Kind is StrokeKind.Circle or StrokeKind.Coordinate or StrokeKind.NumberLine);

    /// <summary>
    /// **这个对象有没有"定义元素手柄"**——也就是它走**特殊手柄那一套**（直线 / 箭头 / 圆 /
    /// 椭圆 / 三角形 / 平行四边形 / 坐标系 / 数轴），还是走**通用框那一套**（缩放 ＋ 对角拉伸：
    /// 矩形 / 图像 / 自由笔迹 / 多选 / 四种曲线 / 四个立体图形）。
    ///
    /// 全工程只有这一条判据，三处调用点都问它（不要各自写一份）：
    ///   · `Overlay` 第 3 / 4 步——画不画旋转柄的连线中心、画特殊手柄还是通用八手柄；
    ///   · `HitTest(…, sel, …)`——查完特殊手柄要不要继续落到通用框那一关；
    ///   · `Engine` 的手势分流——按下 `Top/Bottom/Left/Right` 时，
    ///     这个 `h` 是"定义元素"还是"通用框的四边中点缩放柄"。
    ///
    /// 2026-09-20 前面写错过两次（判的是"是不是图形"）：四种曲线和四个立体图形
    /// **是图形但没有特殊点**，一次被漏成"一个手柄都不画、也点不中"，
    /// 一次被漏成"四角能拉、四边中点只能整体拖"。
    /// </summary>
    public static bool HasShapeHandles(Stroke s)
    {
        Span<ShapeHandle> buf = stackalloc ShapeHandle[5];
        return ShapeHandlesOf(s, buf) > 0;
    }

    /// <summary>
    /// 这个定义元素手柄拖起来是**整体平移**还是**改几何**。
    ///
    /// 只有"**圆的圆心**"是平移（用户定：拖圆心 = 整个圆平移、半径不变）。
    /// 直线的起点**不是**——直线没有"中心"这个概念，拖它就是把那一头拉走
    /// （这也是 <see cref="ShapeHandle.Anchor"/> 这个名字的含义："这条图形挂靠的那个点"，
    /// 对直线是起点、对圆是圆心）。
    ///
    /// **椭圆的中心 2026-09-20 起不在名单里**：那个手柄本身被精简掉了（拖整条就是平移），
    /// 所以这里也不该再认它——认了就等于给一条已经没有入口的路留后门。
    /// 判据集中在这一个函数里：手势入口（Engine）与写点（Engine）都用它，
    /// 免得"入口按平移处理、写点却按改几何"这种两套账。
    /// </summary>
    public static bool IsAnchorMove(Stroke s, ShapeHandle h)
        => h == ShapeHandle.Anchor && s.Kind is StrokeKind.Circle;

    /// <summary>ShapeHandle → SelHandle（手势状态机统一用 SelHandle 记"抓着哪个"，见 Engine）。</summary>
    public static SelHandle SelHandleOf(ShapeHandle h) => h switch
    {
        ShapeHandle.Anchor => SelHandle.EndpointA,
        ShapeHandle.Rim => SelHandle.EndpointB,
        ShapeHandle.AxisRight => SelHandle.Right,
        ShapeHandle.AxisTop => SelHandle.Top,
        ShapeHandle.Vertex0 => SelHandle.VertexA,
        ShapeHandle.Vertex1 => SelHandle.VertexB,
        ShapeHandle.Vertex2 => SelHandle.VertexC,
        ShapeHandle.Vertex3 => SelHandle.VertexD,
        ShapeHandle.FocusPoint => SelHandle.FocusPoint,
        _ => SelHandle.None,
    };

    /// <summary>
    /// SelHandle → ShapeHandle（命中之后翻译成"抓着哪个定义元素"）。
    /// 椭圆不给"外角点"手柄（用户定：四个轴端点已经把拉伸给全了），所以 EndpointB 对它是 None；
    /// 顶点柄只对"由顶点定义"的那四种图形有意义（三角形 / 平行四边形 / 坐标系 / 数轴），
    /// 别的种类上回 None。
    /// </summary>
    public static ShapeHandle HandleOf(Stroke s, SelHandle h)
    {
        // 三角形 / 平行四边形 / 坐标系 / 数轴：定义元素**就是那些控制点**，别的 SelHandle 一律"没有"。
        // 和"直线不给八向缩放柄留后门"是同一条规矩（见 HitTest 的注释）：
        // 画都不画的东西，也不该点得到、更不该被翻译成一个能改几何的元素
        // （EndpointB 落到 Rim 上，外面那条拖动分支就会去改一个顶点）。
        //
        // 这里**不设上限**：三角形只用到前三个顶点，第四个自然落空——多给一格
        // 比"按种类写死 3 还是 4"少一处会写错的地方。
        if (IsVertexDefined(s))
            return h switch
            {
                SelHandle.VertexA => ShapeHandle.Vertex0,
                SelHandle.VertexB => ShapeHandle.Vertex1,
                SelHandle.VertexC => ShapeHandle.Vertex2,
                SelHandle.VertexD => ShapeHandle.Vertex3,
                _ => ShapeHandle.None,
            };
        return h switch
        {
            SelHandle.EndpointA => HasHandle(s, ShapeHandle.Anchor) ? ShapeHandle.Anchor
                                  : HasHandle(s, ShapeHandle.Rim) ? ShapeHandle.Rim
                                  : ShapeHandle.None,
            // Rim = "第二个定义元素"：直线的终点 / 圆的圆周点 / **抛物线的"曲线上的点"** /
            // **双曲线的顶点**。判据**问 ShapeHandlesOf 自己**，不是另写一份名单——
            // 见下面 HasHandle 的注释（2026-09-20 就是这么漏的）。
            SelHandle.EndpointB => HasHandle(s, ShapeHandle.Rim) ? ShapeHandle.Rim : ShapeHandle.None,
            // 横向 / 纵向的"参数端点"：椭圆、双曲线、正弦 / 余弦各自含义不同
            //（位置见 ShapeHandleLocal）。这两格**是这一族图形唯一的"改参数"入口**；
            // 再往下那两格（Left / Bottom）只有通用框那套（矩形 / 图像 / 笔迹）用得到。
            SelHandle.Right => HasHandle(s, ShapeHandle.AxisRight) ? ShapeHandle.AxisRight : ShapeHandle.None,
            SelHandle.Top => HasHandle(s, ShapeHandle.AxisTop) ? ShapeHandle.AxisTop : ShapeHandle.None,
            // 焦点三角形的顶点 P（只有"椭圆（带焦点）"的"有三角形"那一档会发这一格）：
            // 和上面两格同一条判据——**问 ShapeHandlesOf 自己**，不另写名单。
            SelHandle.FocusPoint => HasHandle(s, ShapeHandle.FocusPoint) ? ShapeHandle.FocusPoint : ShapeHandle.None,
            _ => ShapeHandle.None,
        };
    }

    /// <summary>
    /// 这个对象**实际发没发**某一格手柄——判据是 <see cref="ShapeHandlesOf"/> 自己。
    ///
    /// **2026-09-20 踩过的坑**：这里原来是另一份手写名单（`HasAxisHandles(kind)`），
    /// 注释还写着"四种曲线都留两个参数手柄"，而抛物线 / 正余弦当天已经各精简成**一个**
    /// （`ShapeHandlesOf` 那头的名单是准的）。两份名单一漂移，后果不是"多画一个不存在的把手"
    /// ——画的那头是准的——而是**按住包围盒右边中点（那里根本没画任何东西）拖动，曲线会跟着变**：
    /// `Right` 被翻译成 `AxisRight`，外面那条拖动分支以为中了个真手柄，
    /// 一路走到 `WriteVertexLocalPoints` 的兜底分支去改"曲线上的点"。
    /// 收敛成"问唯一那份名单"之后，这种漂移不可能再发生。
    /// </summary>
    private static bool HasHandle(Stroke s, ShapeHandle want)
    {
        Span<ShapeHandle> buf = stackalloc ShapeHandle[8];
        int n = ShapeHandlesOf(s, buf);
        for (int i = 0; i < n; i++) if (buf[i] == want) return true;
        return false;
    }

    /// <summary>
    /// 是不是"**完全由控制点定义**"的图形：三角形 / 平行四边形（三个点）
    /// 和坐标系 / 数轴（四个点）。
    ///
    /// 和 <see cref="IsPolygon"/> 的区别：这个是"手柄怎么发"的口径
    /// （顶点柄发几格、别的 SelHandle 一律不给），那个是"几何怎么算"的口径
    /// （平行四边形要现推第四个角）。
    /// </summary>
    public static bool IsVertexDefined(Stroke s)
        => s != null && s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram
                                   or StrokeKind.Coordinate or StrokeKind.NumberLine;

    /// <summary>是不是"三个顶点定义"的多边形（三角形 / 平行四边形）。</summary>
    public static bool IsPolygon(Stroke s)
        => s != null && s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram;

    /// <summary>
    /// 端点手柄在**画布坐标**里的位置。
    ///
    /// 这是"两个坐标系"最容易写错的一处：**控制点存在对象的局部坐标里**
    /// （`Stroke.Points`），而手柄、指针都在画布坐标里——中间隔着 `Stroke.Transform`。
    /// </summary>
    public static Vector2 EndpointCanvasPosition(Stroke s, int slot)
    {
        var p = s.Points[EndpointIndex(s, slot)];
        return Vector2.Transform(new Vector2(p.X, p.Y), s.Transform);
    }

    /// <summary>
    /// 把**画布坐标**的点变回对象的局部坐标。
    ///
    /// 拖端点必须过这一步：旋转 / 缩放过的直线，如果把画布坐标直接当局部坐标写回去，
    /// 端点会飞到一个和指针完全无关的地方（自检里"旋转过的直线拖端点之后端点仍落在
    /// 指针位置"那一条就是盯它的）。同一套换算在 SelectionFrame.ToLocalPoint 里
    /// 也有一份——那里是框坐标，这里是对象坐标，维度不同，不是重复。
    /// </summary>
    public static Vector2 ToLocalPoint(Vector2 canvasPoint, in Matrix3x2 transform)
    {
        if (transform.IsIdentity) return canvasPoint;
        return Matrix3x2.Invert(transform, out var inv) ? Vector2.Transform(canvasPoint, inv) : canvasPoint;
    }

    /// <summary>
    /// 按**选区**做手柄命中：直线 / 箭头只认两个端点 + 旋转柄，其余走通用框那一套。
    /// 直线那一条**不给 8 个缩放柄留后门**（连命中都不做）：画都不画了却还能点到，
    /// 就成了"看不见但拖得动"，比"看得见点不到"更难解释。
    ///
    /// **判据是"这个对象有没有定义元素手柄"，不是"它是不是图形"**（2026-09-20 修）：
    /// 四种曲线和四个立体图形**是图形**但一个特殊手柄都没有，原来在这里直接
    /// `return None`，于是它们的八个缩放柄"画不出来、也点不中"——两头空。
    /// 现在这种情况下**继续往下走通用框那一关**（和矩形 / 墨迹 / 图像同一条路）。
    /// 判据换没换的一致性由自检盯着：`--shapetooltest` 会当场拖一次通用框的角，
    /// 而且要求**框真的被拉大了**（不是"整体挪了一下"）。
    /// </summary>
    public static SelHandle HitTest(float canvasX, float canvasY, IReadOnlyList<Stroke> sel,
                                    in SelectionFrame f, float dpiScale)
    {
        if (ShapeEditable(sel, out var s))
        {
            float rad = HitRadiusLogical * dpiScale;
            var p = new Vector2(canvasX, canvasY);
            Span<ShapeHandle> handles = stackalloc ShapeHandle[5];
            int n = ShapeHandlesOf(s, handles);
            // **倒着找**：后画的手柄先命中。椭圆上"中心"和"轴端点"离得可能很近
            // （半轴很小时），后画的轴端点是更具体的那个（拖它只改一条半轴），
            // 让它优先，否则小椭圆上永远只能拖中心。
            for (int i = n - 1; i >= 0; i--)
            {
                if (Vector2.DistanceSquared(p, ShapeHandleCanvasPosition(s, handles[i])) <= rad * rad)
                    return SelHandleOf(handles[i]);
            }
            // 旋转柄照旧（形状是"参数"，旋转是"姿态"，两码事）；圆不给（见 RotateHandleVisible）。
            if (RotateHandleVisible(s)
                && Vector2.DistanceSquared(p, CanvasPosition(SelHandle.Rotate, f, dpiScale)) <= rad * rad)
                return SelHandle.Rotate;
            // **有**特殊手柄 → 到此为止（绝不给通用八手柄留后门，理由见上面那句）；
            // **没有** → 落到下面通用框那一关。
            if (n > 0) return SelHandle.None;
        }
        return HitTest(canvasX, canvasY, f, dpiScale);
    }

    // =====================================================================
    //  倾斜角 α 与画线吸附
    //
    //  α 和旋转读数 Δ 是**两个数**（见 调研-图形工具.md 2.3），必须分开显示：
    //    · α = 这条线**本身**的姿态，范围 [0°,180°)，永远非负 → 画线、拖端点时出现；
    //    · Δ = 我这一次**转了多少**，逆时针为正、不设上限 → 拖旋转柄时出现。
    //  混在一起用户会以为软件自相矛盾（"这条线 135°，我刚把它转了 -20°"）。
    //
    //  角度换算全工程只有这一处权威实现（连同 RotateMatrix / RotationStepDegrees）：
    //  屏幕 y 轴朝下 → 先取一次负号（逆时针为正），新代码一律复用它们，不另写一份。
    // =====================================================================

    /// <summary>
    /// 直线的**倾斜角** α ∈ [0°,180°)：x 轴正向与直线所成的角。
    ///
    /// 屏幕 y 轴朝下，所以先取一次负号（和 RotationStepDegrees 同一个约定），
    /// 再对 180° 取模折回来——**直线没有方向**，从左下到右上和从右上到左下是同一条线，
    /// 读数必须一样。可核对的等式是 m = tan θ（见 调研 2.3）。
    /// </summary>
    public static float InclinationDegrees(Vector2 p0, Vector2 p1)
    {
        float deg = -MathF.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180f / MathF.PI;
        return FoldInclination(deg);
    }

    /// <summary>
    /// 把角度折到 [0°,180°)（180° 与 0° 是同一条水平线，取 0°）。
    ///
    /// 只在本文件内部用：判"离特殊角多远"（<see cref="InclinationDistance"/>）、
    /// 判"还要转多少"（<see cref="InclinationShortestDelta"/>）、以及画线/拖端点那两个
    /// 确实只需要 [0,180) 的读数。**旋转手柄那个读数是展开值，故意不过这里**（用户 2026-09-18）。
    /// </summary>
    private static float FoldInclination(float deg)
    {
        deg %= 180f;
        if (deg < 0f) deg += 180f;
        // 顺手把 -0 归一成 +0：IEEE 的 `-0.0 % 180` 还是 -0.0，而 "F1" 会把它印成
        // "-0.0"——水平的线读数写成 `α = -0.0°` 就是对不上（α 按定义非负）。
        if (deg == 0f) deg = 0f;
        return deg;
    }

    /// <summary>
    /// 两条直线（两个 α，都在 [0°,180°) 里）之间的**角距离**。
    ///
    /// **必须是环形的**：α 折在 [0,180)，0° 与 180°（也就是 -0°）是同一条水平线，
    /// 所以 179.5° 离 0° 只有 0.5°。直接 `|d - t|` 会算成 179.5°，
    /// 结果是"一条几乎水平的线吸不到水平"（2026-09-18 用户点出来的那条）。
    /// </summary>
    public static float InclinationDistance(float a, float b)
    {
        float d = MathF.Abs(FoldInclination(a) - FoldInclination(b));
        return MathF.Min(d, 180f - d);
    }

    /// <summary>
    /// 从 α = <paramref name="fromDeg"/> 走到 α = <paramref name="toDeg"/> **还要转多少度**
    /// （视觉逆时针为正，落在 (-90°, 90°]）。
    ///
    /// 和 <see cref="InclinationDistance"/> 是一对：那边回答"差多少"（非负），
    /// 这边回答"往哪边转"（带符号）。旋转吸附要用它把"吸到的角"换算成"这一拖还得转多少"。
    /// </summary>
    public static float InclinationShortestDelta(float fromDeg, float toDeg)
    {
        float d = (FoldInclination(toDeg) - FoldInclination(fromDeg)) % 180f;
        if (d > 90f) d -= 180f;
        if (d <= -90f) d += 180f;
        return d;
    }

    /// <summary>
    /// 倾斜角读数文案：`α = 45.0°`（一位小数）。
    ///
    /// 和旋转读数刻意长得不一样（那边不带字段名）：两个数同时挂在一条直线上是对的，
    /// 但**必须分得清哪个是哪个**——带 `α = ` 的只可能是 [0°,180°) 那个倾斜角。
    /// </summary>
    public static string FormatInclination(float deg) => "α = " + FormatOneDecimal(deg);

    /// <summary>
    /// **纯数字**的角度文案：`405.0°` / `-135.0°` / `45.0°`（一位小数、不带字段名）。
    ///
    /// 单选直线拖旋转柄时用它（用户 2026-09-18 定）。两个理由：
    ///   · 那个读数是"从这条线原有的倾斜角接着转"的**展开角**，无上下限——
    ///     405° 按数学定义已经不是倾斜角了（倾斜角只在 [0°,180°)），标成 `α = ` 反而错；
    ///   · 用户说"其他和原来的逻辑一样"，而原来那套 Δ 读数就是纯数字。
    /// </summary>
    public static string FormatSignedDegrees(float deg) => FormatOneDecimal(deg);

    /// <summary>
    /// 一位小数 + 度数符号，并把"会印成 -0.0 的值"归一成 +0（不然水平的线会写成 `-0.0°`）。
    /// 上面两个文案函数共用它——**文案的格式只写这一份**。
    ///
    /// 注意必须**先按要印的精度取整再判零**：−0.02° 本身不是 0，但 "F1" 会把它印成 "-0.0"，
    /// 所以直接判 `deg == 0f` 拦不住它。
    /// </summary>
    private static string FormatOneDecimal(float deg)
    {
        float rounded = MathF.Round(deg, 1);
        if (rounded == 0f) rounded = 0f;      // -0.0 == 0f 为真，赋值就得到 +0.0
        return rounded.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "°";
    }

    /// <summary>
    /// **特殊角**（度）：高中数学 [0°,180°) 上那八个"图上读得出来"的角。
    ///
    /// 两个地方共用这一张表（用户 2026-09-18 定）：
    ///   · **画直线/箭头**时的方向吸附；
    ///   · **单选直线/箭头**拖旋转柄时的吸附（读的是 α，不是转过的 Δ，见下面）。
    /// 集合从原来的 {0,30,45,60,90} 补全成八个：120/135/150 也是老师在黑板上
    /// 画示意图会用到的角，画得出来就该吸得到。
    ///
    /// **注意 0° 与 180° 是同一条线**，判距离一律走 <see cref="InclinationDistance"/>（环形）。
    /// </summary>
    public static readonly float[] SpecialInclinationDegrees =
        { 0f, 30f, 45f, 60f, 90f, 120f, 135f, 150f };

    /// <summary>
    /// 特殊角软吸附的容差（度）。**±1°**（用户 2026-09-18 从 ±3° 收紧）。
    ///
    /// 收紧的理由：特殊角有八个、彼此只差 15°，容差 3° 时"26.5° 也被吸到 30°"
    /// ——用户明明想画 26.5°（正好是斜率 0.5 那条），却怎么也画不出来。
    /// 1° 只吸"我就是要这个整数角"的手。
    ///
    /// 这一条**只管特殊角那一套**（画线 + 单选直线旋转）；
    /// 通用旋转（矩形 / 多选 / 自由笔迹只吸 90° 那一档）仍是 ±3°
    /// （见 <see cref="RotationSoftSnapToleranceDegrees"/>）——用户明确要求那一档"保持现在的行为"。
    /// </summary>
    public const float SoftSnapToleranceDegrees = 1f;

    /// <summary>
    /// 对一个**倾斜角**做吸附，三种模式（优先级从高到低，和旋转共用同一套语义）：
    ///   · <paramref name="noSnap"/>（Alt）：完全自由；
    ///   · <paramref name="gridSnap"/>（Shift）：硬网格 15°（和旋转的 Shift 同一个数）；
    ///   · 默认：软吸附到 <see cref="SpecialInclinationDegrees"/> 里最近的一条
    ///     （**环形**距离 ≤ <see cref="SoftSnapToleranceDegrees"/> 才吸）。
    /// 返回值折在 [0°,180°)。
    /// </summary>
    public static float SnapInclinationDegrees(float deg, bool gridSnap, bool noSnap, out bool snapped)
        => SnapFoldedDegrees(deg, SpecialInclinationDegrees, gridSnap, noSnap, out snapped);

    /// <summary>
    /// 对**折在 [0°,180°) 里的角**做吸附——倾斜角与姿态角共用这一份实现，
    /// 差别只在 <paramref name="targets"/>（直线是八个特殊角、图形是 0/90 两条）。
    ///
    /// 三种模式（优先级从高到低，和旋转共用同一套语义）：
    ///   · <paramref name="noSnap"/>（Alt）：完全自由；
    ///   · <paramref name="gridSnap"/>（Shift）：硬网格 15°（和旋转的 Shift 同一个数）；
    ///   · 默认：软吸附到 <paramref name="targets"/> 里最近的一条
    ///     （**环形**距离 ≤ <see cref="SoftSnapToleranceDegrees"/> 才吸）。
    /// 返回值折在 [0°,180°)。
    /// </summary>
    public static float SnapFoldedDegrees(float deg, float[] targets, bool gridSnap, bool noSnap,
                                          out bool snapped)
    {
        snapped = false;
        // 优先级和旋转那条**一模一样**（先看 Shift 的硬网格，再看 Alt 的自由）：
        // 两个修饰键一起按时 Shift 赢——否则同一个手势在两处会有两种行为。
        if (gridSnap)
        {
            deg = MathF.Round(FoldInclination(deg) / RotationSnapDegrees) * RotationSnapDegrees;
            snapped = true;
            // 178° 这一档会 round 成 180°——折回来就是 0°，还是"水平"那一条（同一条线）。
            return FoldInclination(deg);
        }
        if (noSnap) return FoldInclination(deg);

        float folded = FoldInclination(deg);
        float best = folded, bestDist = float.MaxValue;
        foreach (float target in targets)
        {
            float d = InclinationDistance(folded, target);   // 环形：179.5° 离 0° 只有 0.5°
            if (d < bestDist) { bestDist = d; best = target; }
        }
        if (bestDist <= SoftSnapToleranceDegrees) { folded = best; snapped = true; }
        return FoldInclination(folded);
    }

    /// <summary>
    /// 对**展开后的角**（可以超过 180°、也可以是负数）做吸附，**特殊角按 180° 周期**。
    ///
    /// 单选直线拖旋转柄用这一版（用户 2026-09-18 澄清："逆时针为正、顺时针为负，它可以
    /// 无限转下去……除了初始角度要调一下以外，其他和原来的逻辑是一样的"）。
    ///
    /// 为什么不复用上面那一版（它返回折回 [0,180) 的值）：
    ///   · 折回会把"405°"变成"45°"——用户转了两圈，读数突然跳回 45，看起来像丢了圈数；
    ///   · 而"要不要吸"只看**离特殊角多远**，那是个周期量（405.5° 离 405° 只有 0.5°，
    ///     405° 折回来就是 45°，还是那八个角）。
    /// 所以这里的做法是：**折回只用来找最近的角，吸完把差值加回展开值**——
    /// 405.5° 吸到 405.0°、−135.4° 吸到 −135.0°、765.4° 吸到 765.0°。
    /// <paramref name="snapped"/> 与容差（±1°）的语义和上面那一版完全一致。
    /// </summary>
    public static float SnapExpandedInclinationDegrees(float deg, bool gridSnap, bool noSnap,
                                                       out bool snapped)
        => SnapExpandedDegrees(deg, SpecialInclinationDegrees, gridSnap, noSnap, out snapped);

    /// <summary>
    /// 同 <see cref="SnapExpandedInclinationDegrees"/>，但目标角由调用方给——
    /// 直线的倾斜角喂八个特殊角，图形的**姿态角**喂 <see cref="PoseSnapDegrees"/>（0/90）。
    ///
    /// 姿态角为什么也要走"展开值"这一版（它读数本身是折过的）：
    /// 三角形的姿态角折在 [0,180) 时，"179.6°"和"0°"只差 0.4°，但**两者差 180°**——
    /// 拿折过的值去吸，一个本来只偏 0.4° 的三角形会被**翻过来 180°**（尖朝下的变成朝上）。
    /// 所以吸附必须在连续的那个角上做，吸完把**最小**的修正量加回去。
    /// </summary>
    public static float SnapExpandedDegrees(float deg, float[] targets, bool gridSnap, bool noSnap,
                                            out bool snapped)
    {
        snapped = false;
        float folded = FoldInclination(deg);          // 只用于判"离哪个角最近"

        if (gridSnap)
        {
            // Shift 的 15° 硬网格同样按 180° 周期：走到最近的一条网格线上（可以走出去好几圈）
            float target = MathF.Round(folded / RotationSnapDegrees) * RotationSnapDegrees;
            snapped = true;
            return deg + InclinationShortestDelta(folded, target);
        }
        if (noSnap) return deg;                       // Alt：完全自由

        float best = folded, bestDist = float.MaxValue;
        foreach (float target in targets)
        {
            float d = InclinationDistance(folded, target);
            if (d < bestDist) { bestDist = d; best = target; }
        }
        if (bestDist > SoftSnapToleranceDegrees) return deg;
        snapped = true;
        return deg + InclinationShortestDelta(folded, best);
    }

    /// <summary>姿态角的"展开值"吸附（目标 0/90，容差与直线一致 ±1°）；见 <see cref="SnapExpandedDegrees"/>。</summary>
    public static float SnapExpandedPoseDegrees(float deg, bool gridSnap, bool noSnap,
                                                out bool snapped)
        => SnapExpandedDegrees(deg, PoseSnapDegrees, gridSnap, noSnap, out snapped);

    /// <summary>
    /// 把"指针位置"吸附成"**绕 <paramref name="start"/> 转过来、长度不变**"的端点，
    /// 也就是画线时那个"吸附时绕起点转、保持长度"（见 计划-图形工具.md 8.2）。
    ///
    /// 两处细节：
    ///   · 长度取**指针到起点的真实距离**（吸附只改方向，不偷偷改长短——用户拉多长就是多长）；
    ///   · α 是"折过"的（直线没有方向），所以重建方向时要在"θ 和 θ+180°"里挑离当前指针方向
    ///     最近的那一个，否则线会甩到指针的反方向去。
    /// </summary>
    public static Vector2 SnapEndPoint(Vector2 start, Vector2 pointer, bool gridSnap, bool noSnap,
                                       out bool snapped)
    {
        snapped = false;
        float dx = pointer.X - start.X, dy = pointer.Y - start.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return pointer;                    // 零长度：没什么可吸的

        float snappedDeg = SnapInclinationDegrees(InclinationDegrees(start, pointer), gridSnap, noSnap,
                                                  out snapped);
        if (!snapped) return pointer;

        // 屏幕方向角 θ（y 朝下）。α = -θ 折到 [0,180)，所以候选方向是 -α 与 -α+180°。
        float theta = MathF.Atan2(dy, dx);
        float bestTheta = theta, bestDist = float.MaxValue;
        for (int k = 0; k < 2; k++)
        {
            float cand = -snappedDeg * MathF.PI / 180f + k * MathF.PI;
            float d = MathF.Abs(NormalizeDegrees((cand - theta) * 180f / MathF.PI));
            if (d < bestDist) { bestDist = d; bestTheta = cand; }
        }
        return new Vector2(start.X + MathF.Cos(bestTheta) * len, start.Y + MathF.Sin(bestTheta) * len);
    }

    // =====================================================================
    //  一族"特殊形状"吸附（规格 9.6）
    //
    //  一句话：**把被拖的那个点修正到"恰好满足约束"的位置**。
    //  三角形 → 等腰 / 等边 / 直角；矩形 → 正方形；椭圆 → 正圆；
    //  平行四边形 → 菱形 / 矩形。
    //
    //  三条硬要求（不照做就会变成"手柄粘手"，见 计划-图形工具.md 9.6）：
    //    ① **只在拖顶点 / 轴端点 / 角的时候吸**：整体移动、旋转一律不吸
    //       （那两件事改的是位置和姿态，改不了"这是不是等腰三角形"）；
    //    ② **容差要小**：长度 ≤ 2 逻辑像素、角度 ≤ 1°；
    //    ③ **`Alt` 一律自由**，吸住时要说得出"吸到了什么"（胶囊上有字）。
    //
    //  量的都是**对象自己的局部坐标**里的量与角（顶点距离、两条边的夹角）——
    //  那才是这个形状自己的性质。旋转不改变长度和夹角，所以"转过 30° 的三角形"照样吸；
    //  对象被整体缩放过的情形下，容差在屏幕上会跟着放大/缩小，这是已知的近似
    //  （与"描边宽度不随变换缩放"那一条是同一类取舍，见 HitTestExact 的注释）。
    // =====================================================================

    /// <summary>长度容差（**逻辑像素**，规格 9.6：≤ 2）。调用方乘 dpi 换算成画布单位。</summary>
    public const float ShapeSnapLengthToleranceLogical = 2f;

    /// <summary>角度容差（**度**，规格 9.6：≤ 1）。</summary>
    public const float ShapeSnapAngleToleranceDegrees = 1f;

    /// <summary>吸住了什么 → 胶囊上的字。**只有这一份**（画与脏区都读它）。</summary>
    public static string ShapeSnapLabel(ShapeSnapKind k) => k switch
    {
        ShapeSnapKind.Isosceles => "等腰",
        ShapeSnapKind.Equilateral => "等边",
        ShapeSnapKind.RightAngle => "直角",
        ShapeSnapKind.Square => "正方形",
        ShapeSnapKind.Circle => "正圆",
        ShapeSnapKind.Rhombus => "菱形",
        ShapeSnapKind.Rectangle => "矩形",
        ShapeSnapKind.RightPrism => "直棱柱",
        ShapeSnapKind.RightPyramid => "直棱锥",
        ShapeSnapKind.RightFrustum => "直棱台",
        _ => "",
    };

    /// <summary>
    /// 三角形 / 平行四边形的特殊形状吸附：把被拖的第 <paramref name="index"/> 个控制点
    /// 修正到"恰好满足约束"的位置，**其余两个点一个字不动**（这是"拖哪个只动哪个"的延续）。
    ///
    /// 传进来的三个点是**这次拖动的当前值**（<paramref name="proposed"/> 就是被拖那个点的
    /// 新位置）：修正只可能改它，所以输出只有一个点。
    /// <paramref name="lengthTol"/> 是**画布单位**下的容差（调用方按 dpi 换算）。
    /// <paramref name="noSnap"/>（`Alt`）为真时一律返回原样。
    /// </summary>
    public static Vector2 SnapPolygonVertex(StrokeKind kind, int index,
                                            Vector2 p0, Vector2 p1, Vector2 p2,
                                            Vector2 proposed, float lengthTol, bool noSnap,
                                            out ShapeSnapKind snapped)
    {
        snapped = ShapeSnapKind.None;
        if (noSnap || index < 0 || index > 2) return proposed;   // Alt：完全自由

        Span<Vector2> p = stackalloc Vector2[3];
        p[0] = p0; p[1] = p1; p[2] = p2;
        p[index] = proposed;
        if (kind == StrokeKind.Triangle) return SnapTrianglePoints(p, index, lengthTol, out snapped);
        if (kind == StrokeKind.Parallelogram)
            return SnapParallelogramPoints(p, index, lengthTol, out snapped);
        return proposed;
    }

    /// <summary>
    /// 三角形的三个约束，**按优先级从上往下**判（都满足时取上面那条）：
    ///   · **等边**（最强）→ 三边相等；
    ///   · **等腰** → 两腰（被拖的点到另两点的距离）相等；
    ///   · **直角** → **任意一个内角**离 90° 不超过 1°。
    ///
    /// 先判等边再判等腰是因为等边一定满足等腰（两边差 0）：反过来判的话，
    /// 一个本来就等边的三角形会被"修正"成只是等腰（把第三个点拉到中垂线上就不再等边了），
    /// 那是**把好东西改坏了**。
    /// </summary>
    private static Vector2 SnapTrianglePoints(ReadOnlySpan<Vector2> p, int k, float tol,
                                              out ShapeSnapKind snapped)
    {
        snapped = ShapeSnapKind.None;
        var v = p[k];
        var a = p[(k + 1) % 3];
        var b = p[(k + 2) % 3];
        float ab = Vector2.Distance(a, b);
        float va = Vector2.Distance(v, a);
        float vb = Vector2.Distance(v, b);

        // ① 等边：把被拖的点摆到"以另两点为边的正三角形第三个顶点"上
        //    （两个候选取离现在更近的那个，也就是保持它原来在哪一侧）。
        if (MathF.Abs(va - ab) <= tol && MathF.Abs(vb - ab) <= tol)
        {
            snapped = ShapeSnapKind.Equilateral;
            return EquilateralThird(a, b, v);
        }
        // ② 等腰：落在另两点的**中垂线**上（"两腰等长"的全部位置就是它）。
        if (MathF.Abs(va - vb) <= tol)
        {
            snapped = ShapeSnapKind.Isosceles;
            return ProjectOntoPerpBisector(v, a, b);
        }
        // ③ 直角：先看被拖的那个顶点（用户正在调的就是这个角），再看另外两个。
        for (int n = 0; n < 3; n++)
        {
            int j = (k + n) % 3;
            var at = p[j];
            var q = p[(j + 1) % 3] - at;
            var r = p[(j + 2) % 3] - at;
            if (MathF.Abs(AngleBetweenDegrees(q, r) - 90f) > ShapeSnapAngleToleranceDegrees)
                continue;
            snapped = ShapeSnapKind.RightAngle;
            if (j == k)
            {
                // 直角就在被拖的这个顶点上：它必须落在"以另两点为直径的圆"上（泰勒斯定理）。
                return ProjectOntoCircleDia(v, p[(j + 1) % 3], p[(j + 2) % 3]);
            }
            // 直角在另一个（固定的）顶点上：被拖的点必须落在"过那个顶点、垂直于那条固定边"的线上。
            var fixedEnd = (j + 1) % 3 == k ? p[(j + 2) % 3] : p[(j + 1) % 3];
            return ProjectOntoPerpLine(v, at, fixedEnd - at);
        }
        return v;
    }

    /// <summary>
    /// 平行四边形的两个约束，**按优先级从上往下**判：
    ///   · **菱形** → 邻边相等（`|u| = |v|`）；
    ///   · **矩形** → 邻边垂直（`u · v = 0`）。
    ///
    /// 三条边向量按"拖的是哪个点"分化（只有被拖的那个点会动）：
    ///   · 拖**底左**（下标 0）：两条边都挂在它身上 → 条件是"到另两点等距"→ 中垂线；
    ///   · 拖**底右 / 顶左**（1 / 2）：只有一条边变 → 条件是"到 P0 的距离 = 另一条边的长"→ 圆。
    /// 正方形同时满足两条，这时报**菱形**（顺序即优先级）。
    /// </summary>
    private static Vector2 SnapParallelogramPoints(ReadOnlySpan<Vector2> p, int k, float tol,
                                                   out ShapeSnapKind snapped)
    {
        snapped = ShapeSnapKind.None;
        var v = p[k];
        var u = p[1] - p[0];        // 一条邻边（P0 → P1）
        var w = p[2] - p[0];        // 另一条邻边（P0 → P2）
        float lu = u.Length(), lw = w.Length();
        if (lu < 1e-4f || lw < 1e-4f) return v;      // 退化成一点：没什么可吸的

        if (MathF.Abs(lu - lw) <= tol)
        {
            snapped = ShapeSnapKind.Rhombus;
            return k switch
            {
                0 => ProjectOntoPerpBisector(v, p[1], p[2]),
                1 => RadialFrom(v, p[0], lw),      // 目标：离 P0 恰好 lw（= 另一条边的长）
                _ => RadialFrom(v, p[0], lu),
            };
        }
        if (MathF.Abs(AngleBetweenDegrees(u, w) - 90f) <= ShapeSnapAngleToleranceDegrees)
        {
            snapped = ShapeSnapKind.Rectangle;
            return k switch
            {
                0 => ProjectOntoCircleDia(v, p[1], p[2]),
                1 => ProjectOntoPerpLine(v, p[0], w),
                _ => ProjectOntoPerpLine(v, p[0], u),
            };
        }
        return v;
    }

    /// <summary>
    /// 椭圆的**正圆吸附**（规格 9.6）：`|a − b| ≤ 容差` 时，把**被拖的那一条半轴**
    /// 取成另一条的长——于是 a 与 b 逐位相等，是个正圆。
    ///
    /// 为什么改被拖的那一条、而不是两条各让一半：语义是"修正**被拖的那个点**的位置"，
    /// 另一条半轴（和它那个手柄）不该跟着动。
    /// <paramref name="other"/> = 另一条半轴的长；返回修正后的这一条。
    /// </summary>
    public static float SnapEllipseAxis(float current, float other, float lengthTol, bool noSnap,
                                        out bool snapped)
    {
        snapped = false;
        if (noSnap) return current;
        if (MathF.Abs(current - other) > lengthTol) return current;
        snapped = true;
        return other;
    }

    /// <summary>
    /// 拖**矩形四角**时的正方形吸附（规格 9.6）。返回 true = 吸住了，<paramref name="localM"/> 是矩阵。
    ///
    /// 判据量的是**矩形自己的两边长差**（不是"拖出来那一帧的框有多方"）：四角缩放是**等比**的
    /// （见 DragMatrix 的注释），比值在整段拖动里不变，所以"这个矩形离正方形差多少"
    /// 是按下那一刻就定下来的——按形状量才对得上用户看到的那个形状，
    /// 否则拖出去一点就"忽然不吸了"（框上的边长差被同比放大，越过了容差）。
    ///
    /// 修正 = 把缩放因子拆成两个（不再是等比），让结果框的两边**恰好**相等：
    /// `side = (w + h)/2 × s`，再按 `side/w`、`side/h` 分别缩放。因为原本就在容差内，
    /// 修正量 ≤ 1 像素，手感上感觉不到"跳"，但结果是一个**精确**的正方形。
    ///
    /// 只在"单选一个矩形、而且它是**正着的**"时候生效：转过的矩形，选中框是它的外接正矩形
    /// （虚胖的那一条），沿屏幕轴缩放和它自己的边长没有简单关系——宁可不吸，
    /// 也不给一个解释不清的行为。**整体移动 / 旋转不走这里**（规格 9.6 第一条硬要求）。
    /// </summary>
    public static bool TrySnapSquareCorner(IReadOnlyList<Stroke> sel, SelHandle handle,
                                           in SelectionFrame f, Vector2 currentPoint,
                                           float dpiScale, bool noSnap, out Matrix3x2 localM)
    {
        localM = Matrix3x2.Identity;
        if (noSnap || sel == null || sel.Count != 1) return false;         // Alt = 自由；多选不吸
        var s = sel[0];
        if (s == null || s.Kind != StrokeKind.Rectangle) return false;
        if (!IsAxisAligned(s.Transform)) return false;
        if (handle is not (SelHandle.TopLeft or SelHandle.TopRight
                           or SelHandle.BottomLeft or SelHandle.BottomRight)) return false;

        float w = f.Local.MaxX - f.Local.MinX;
        float h = f.Local.MaxY - f.Local.MinY;
        if (w <= 1e-3f || h <= 1e-3f) return false;
        // **容差外一律不吸**（规格 9.6：长度 ≤ 2 逻辑像素）。
        if (MathF.Abs(w - h) > ShapeSnapLengthToleranceLogical * dpiScale) return false;

        var anchor = Position(Opposite(handle), f.Local, dpiScale);
        var corner = Position(handle, f.Local, dpiScale);
        float armX = corner.X - anchor.X, armY = corner.Y - anchor.Y;
        if (MathF.Abs(armX) < 1e-3f || MathF.Abs(armY) < 1e-3f) return false;
        float sx = (currentPoint.X - anchor.X) / armX;
        float sy = (currentPoint.Y - anchor.Y) / armY;
        // 四角 = 等比（取变化大的那一轴），和 DragMatrix 里那条规则同源——两边都改，
        // 所以这里必须自己再算一遍，不能在 DragMatrix 的结果上打补丁。
        float su = MathF.Max(MathF.Abs(sx), MathF.Abs(sy));
        float side = (w + h) * 0.5f * su;
        float fx = MathF.Sign(sx == 0f ? 1f : sx) * side / w;
        float fy = MathF.Sign(sy == 0f ? 1f : sy) * side / h;
        // 会被夹到最小缩放的不算吸住：那种时候对象已经被夹住了，"精确的正方形"没有意义，
        // 而报"吸住了"却是假的。
        if (MathF.Abs(fx) < MinScale || MathF.Abs(fy) < MinScale) return false;

        localM = Matrix3x2.CreateScale(fx, fy, anchor);
        return true;
    }

    /// <summary>
    /// 把 <paramref name="v"/> 修正到"与 <paramref name="a"/>、<paramref name="b"/> 等距"的位置：
    /// 即它在 `ab` 的**中垂线**上的最近点（沿中垂线投影）。
    /// </summary>
    private static Vector2 ProjectOntoPerpBisector(Vector2 v, Vector2 a, Vector2 b)
    {
        var m = (a + b) * 0.5f;
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return v;
        var n = new Vector2(-d.Y, d.X) / len;              // ab 的法线（单位）
        return m + n * Vector2.Dot(v - m, n);
    }

    /// <summary>
    /// 以 `ab` 为边、和 <paramref name="near"/> **同侧**的正三角形第三个顶点。
    /// （边长 = `|ab|`，高 = `|ab|·√3/2`，从 `ab` 的中点沿法线抬高。）
    /// </summary>
    private static Vector2 EquilateralThird(Vector2 a, Vector2 b, Vector2 near)
    {
        var m = (a + b) * 0.5f;
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return near;
        var n = new Vector2(-d.Y, d.X) / len;
        float h = len * 0.8660254f;                        // √3/2
        float side = Vector2.Dot(near - m, n) < 0f ? -1f : 1f;
        return m + n * (h * side);
    }

    /// <summary>
    /// 把 <paramref name="v"/> 修正到"以 `ab` 为**直径**的圆"上的最近点——那是
    /// `∠(a, v, b) = 90°`（泰勒斯定理）的全部位置。
    /// </summary>
    private static Vector2 ProjectOntoCircleDia(Vector2 v, Vector2 a, Vector2 b)
    {
        var m = (a + b) * 0.5f;
        float r = Vector2.Distance(a, b) * 0.5f;
        if (r < 1e-4f) return v;
        var d = v - m;
        float len = d.Length();
        if (len < 1e-4f) return m + new Vector2(r, 0f);    // 正好落在圆心：随便挑一个方向
        return m + d / len * r;
    }

    /// <summary>把 <paramref name="v"/> 修正到"过 <paramref name="p"/>、垂直于 <paramref name="dir"/>"的直线上的最近点。</summary>
    private static Vector2 ProjectOntoPerpLine(Vector2 v, Vector2 p, Vector2 dir)
    {
        float len = dir.Length();
        if (len < 1e-4f) return v;
        var n = new Vector2(-dir.Y, dir.X) / len;
        return p + n * Vector2.Dot(v - p, n);
    }

    /// <summary>把 <paramref name="v"/> 修正到"以 <paramref name="p0"/> 为心、半径 <paramref name="r"/> 的圆"上的最近点。</summary>
    private static Vector2 RadialFrom(Vector2 v, Vector2 p0, float r)
    {
        var d = v - p0;
        float len = d.Length();
        if (len < 1e-4f) return p0 + new Vector2(r, 0f);
        return p0 + d / len * r;
    }

    /// <summary>
    /// 两条边的**内角**（度，落在 [0°,180°]）。
    ///
    /// 这是全工程"两边夹角"的权威实现（和 <see cref="InclinationDegrees"/> 同一层）：
    /// 直角吸附、自检判"内角正好 90°"都走它，别在别处再写一遍 acos。
    /// cos 夹到 [-1,1] 是必须的：`dot/(|u||v|)` 在浮点下会蹦出 1.0000001，
    /// Acos 会返回 NaN——那个 NaN 会一路传进矩阵里。
    /// </summary>
    public static float AngleBetweenDegrees(Vector2 u, Vector2 v)
    {
        float lu = u.Length(), lv = v.Length();
        if (lu < 1e-4f || lv < 1e-4f) return 0f;
        float cos = Math.Clamp(Vector2.Dot(u, v) / (lu * lv), -1f, 1f);
        return MathF.Acos(cos) * 180f / MathF.PI;
    }

    // =====================================================================
    //  读数（规格 9.7）：图形的**姿态角** / 三角形的**内角** / 平行四边形的**夹角**
    //
    //  这三样都在这里成型，理由和上面那两处一样：**角度换算只有这一份**。
    //  三个量量的都是"眼睛在屏幕上看到的那个角"，所以一律按**画布坐标**的点来算
    //  （旋转过的图形，局部坐标里的角早就不是屏幕上的那个角了）。
    // =====================================================================

    /// <summary>
    /// 单选拖旋转柄时读数走"**姿态角**"的那几种图形（规格 9.7）：矩形 / 椭圆 / 三角形 / 平行四边形。
    ///
    /// **圆不在内**（用户定："圆转了看不出来"）；直线 / 箭头也不在内——它们读倾斜角 α
    /// （和姿态角是同一个东西，只是线上叫倾斜角）；图像 / 自由笔迹 / 多选照旧读转过的 Δ。
    /// 和 <see cref="ShapeEditable"/> 一样只认单选，理由相同：多选要的是"整组转"，
    /// 没有"这一个图形的姿态"可言。
    /// </summary>
    public static bool PoseEditable(IReadOnlyList<Stroke> sel, out Stroke stroke)
    {
        stroke = null;
        if (sel == null || sel.Count != 1) return false;
        var s = sel[0];
        if (s == null || s.IsImage) return false;
        if (s.Kind is not (StrokeKind.Rectangle or StrokeKind.Ellipse
                           or StrokeKind.ConicEllipse
                           or StrokeKind.Triangle or StrokeKind.Parallelogram)) return false;
        stroke = s;
        return true;
    }

    /// <summary>
    /// 图形**相对水平**的**姿态角**（度，折在 [0°,180°)）：0° = 正的、90° = 竖的。
    ///
    /// 怎么解出来：图形的局部坐标里它是"正着躺的"（矩形的四条边、椭圆的长轴、三角形的底边
    /// 都平行于局部 x 轴），所以把**局部 x 轴**过一遍变换、量它现在的方向，就是它的姿态。
    /// 折进 [0°,180°) 有两个作用：
    ///   · 图形没有"正反"（转 180° 和没转看起来一样），折掉之后读数才是它真正的姿态；
    ///   · **镜像 / 上下翻转**过的图形行列式为负，`atan2` 给出的符号是反的——折进
    ///     [0°,180°) 之后就与镜像无关了（左右翻转的矩形照样读 0.0°），不必再判一次行列式。
    /// 量方向这一步复用 <see cref="InclinationDegrees"/>——**不另写一份 atan2**。
    /// </summary>
    public static float PoseAngleDegrees(in Matrix3x2 m)
        => InclinationDegrees(Vector2.Transform(Vector2.Zero, m), Vector2.Transform(Vector2.UnitX, m));

    /// <summary>
    /// 姿态角软吸附的目标角：**只有 0° / 90° 两条**（规格 9.7）。
    ///
    /// 比直线的八个特殊角少得多，因为姿态角要回答的问题只有一个——"这个图形摆正了没有"。
    /// 0° = 正着、90° = 竖着，两条都算摆正；容差与直线那一套完全一致（±1°）。
    /// </summary>
    public static readonly float[] PoseSnapDegrees = { 0f, 90f };

    /// <summary>
    /// 对**姿态角**做吸附：`Shift` 15° 硬网格、`Alt` 自由、默认软吸附 0/90（±1°），
    /// 和直线的倾斜角那一套是同一个函数（只是目标角换了一张表）。返回值折在 [0°,180°)。
    /// </summary>
    public static float SnapPoseDegrees(float deg, bool gridSnap, bool noSnap, out bool snapped)
        => SnapFoldedDegrees(deg, PoseSnapDegrees, gridSnap, noSnap, out snapped);

    /// <summary>姿态角读数文案：`姿态 = 0.0°`（和直线的 `α = …` 是同一种长相，只换名字）。</summary>
    public static string FormatPose(float deg) => "姿态 = " + FormatOneDecimal(deg);

    /// <summary>
    /// 把一个角折进 [0°,180°)（里面就是 <see cref="FoldInclination"/>，只是下面那几段要用它）。
    ///
    /// 姿态角读数用：引擎吸住之后拿"吸到的那个展开值"折回来当标签
    /// （0/90，或 Shift 网格上的角）——从矩阵里解出来的值在浮点噪声下可能是 179.9998°，
    /// 数学上和 0° 是同一条线，可屏幕上写 "180.0°" 会让人以为"没转到 0"。
    /// </summary>
    public static float FoldDegrees(float deg) => FoldInclination(deg);

    /// <summary>
    /// 多边形要显示的那些角（度，**画布坐标**下算，规格 9.7）：三角形 = 三个内角、
    /// 平行四边形 = **它自己那两个夹角**（对顶角相等，四个角只有两个不同的值，报一次即可）。
    ///
    /// <paramref name="vertices"/>：三角形给 3 个顶点；平行四边形给 **4 个**
    /// （含那个推导出来的第四个角——它是被报出来的那个角的一条边，少了它角度就是错的）。
    /// <paramref name="degrees"/> 至少 3 个格子。返回报了几个角。
    ///
    /// **每个角各自独立取值**（用户 2026-09-19 定）：以前三角形那三个还被按 0.1° 配平过，
    /// 为的是让屏幕上那行"内角和 = 180.0°"自洽；那一行现在不显示了（用户："乱"），
    /// 配平就一起撤了——留着它反而会让某一个角为了凑数**偏离真实角度 0.1°**。
    /// </summary>
    public static int PolygonAngles(StrokeKind kind, ReadOnlySpan<Vector2> vertices,
                                    Span<float> degrees)
    {
        switch (kind)
        {
            case StrokeKind.Triangle:
            {
                if (vertices.Length < 3 || degrees.Length < 3) return 0;
                for (int i = 0; i < 3; i++)
                    degrees[i] = AngleAtVertexDegrees(vertices, i, (i + 1) % 3, (i + 2) % 3);
                return 3;
            }

            case StrokeKind.Parallelogram:
            {
                if (vertices.Length < 4 || degrees.Length < 2) return 0;
                // 报相邻的那两个顶点就够了：p0 处与 p1 处的角正好互补（同旁内角），
                // 另外两个顶点是它们的对顶角，值一模一样。
                degrees[0] = AngleAtVertexDegrees(vertices, 0, 1, 2);
                degrees[1] = AngleAtVertexDegrees(vertices, 1, 0, 3);
                return 2;
            }

            default:
                return 0;
        }
    }

    /// <summary>顶点 <paramref name="i"/> 处、由 <paramref name="ia"/> / <paramref name="ib"/> 两条边张成的角（度）。</summary>
    private static float AngleAtVertexDegrees(ReadOnlySpan<Vector2> p, int i, int ia, int ib)
        => AngleBetweenDegrees(p[ia] - p[i], p[ib] - p[i]);

    /// <summary>内角 / 夹角的读数文案：`30.0°`（和直线读数胶囊同一位数的小数口径）。</summary>
    public static string FormatAngleDegrees(float deg) => FormatOneDecimal(deg);

    /// <summary>
    /// 这个变换有没有"转过的分量"（`M12` / `M21` 非零 = 旋转或剪切）。
    ///
    /// 只有**正着的**矩形，"沿屏幕轴缩放"才等于"沿它自己两条边缩放"；
    /// 转过的矩形选中框是外接正矩形（虚胖），两者没有简单关系。
    /// 容差用相对量（千分之一）：矩阵在拖动里累积过浮点运算，拿 `== 0` 判会漏掉残留。
    /// </summary>
    private static bool IsAxisAligned(in Matrix3x2 m)
    {
        float s = MathF.Max(MathF.Abs(m.M11), MathF.Abs(m.M22));
        if (s < 1e-6f) return false;
        return MathF.Abs(m.M12) <= s * 1e-3f && MathF.Abs(m.M21) <= s * 1e-3f;
    }

    /// <summary>
    /// 算当前选区的坐标系：**一律轴对齐**——把每个对象变换后的**墨迹**包围盒并起来，
    /// 选一条就是它自己那一个（理由与取舍见 SelectionFrame 的注释）。
    ///
    /// 范围取 **InkBounds（墨迹范围）而不是 Bounds（中心线）**：框是给眼睛看的，
    /// 它必须把屏幕上那一坨墨圈住。用中心线的话，笔越宽框越"缩"到墨里面去——
    /// 64 像素宽的荧光笔选中之后，框的四条边全压在墨上，看着就是错的
    /// （用户实测反馈）。改成墨迹范围之后，框刚好贴着墨的外沿。
    ///
    /// 斜着的对象取的是"它转过之后的墨迹范围"，所以框会比对象本身大一圈（正矩形
    /// 圈斜东西，天生虚胖）。这是"框永远正着"这条决定的直接代价，**不是 bug**。
    /// </summary>
    public static SelectionFrame FrameOf(IReadOnlyList<Stroke> sel)
    {
        if (sel == null || sel.Count == 0)
            return new SelectionFrame { Local = RectF.Empty, ToCanvas = Matrix3x2.Identity };

        // 一条和多条**同一条路**：把每个对象变换后的墨迹包围盒并起来。
        var r = RectF.Empty;
        foreach (var s in sel)
        {
            var b = s.WorldInkBounds;
            if (!b.IsEmpty) r.Add(b);
        }
        return new SelectionFrame { Local = r, ToCanvas = Matrix3x2.Identity };
    }

    /// <summary>
    /// "细长 / 小对象让出边中点柄"的判据——**全工程只有这一份**：命中的两个入口和
    /// 绘制（Overlay 画那八个通用柄）都问它。分家写就会出现"看得见点不到"、
    /// "点得到看不见"，或者小对象上八个方块挤成一团（2026-10-05 用户报的）。
    ///
    /// 返回 (ThinVertical, ThinHorizontal)：竖向太窄 → 让出"上/下"；横向太窄 → 让出"左/右"。
    /// </summary>
    public static (bool ThinVertical, bool ThinHorizontal) ThinEdges(in RectF box, float dpiScale)
    {
        float d = HitRadiusLogical * dpiScale * 2f;
        return ((box.MaxY - box.MinY) < d, (box.MaxX - box.MinX) < d);
    }

    /// <summary>按给定的选区坐标系做手柄命中判定。</summary>
    public static SelHandle HitTest(float canvasX, float canvasY, in SelectionFrame f,
                                    float dpiScale, bool includeEdgeHandles = true)
    {
        float r = HitRadiusLogical * dpiScale;
        var p = new Vector2(canvasX, canvasY);

        // 细长对象让出"边中点"手柄 —— 判据只有 ThinEdges 一份（命中两个入口、
        // 绘制侧共用它；分家就会出现"点不中/拖不动/画一堆"）。
        var (thinVertical, thinHorizontal) = ThinEdges(f.CanvasAabb, dpiScale);

        // 旋转手柄先测：它在框外，不会和四角重叠，但它离上边中点最近，
        // 先测它能避免两个窄命中区互相抢。
        if (Vector2.DistanceSquared(p, CanvasPosition(SelHandle.Rotate, f, dpiScale)) <= r * r)
            return SelHandle.Rotate;

        Span<SelHandle> order = stackalloc SelHandle[]
        {
            SelHandle.TopLeft, SelHandle.TopRight, SelHandle.BottomLeft, SelHandle.BottomRight,
            SelHandle.Top, SelHandle.Bottom, SelHandle.Left, SelHandle.Right,
        };
        foreach (var h in order)
        {
            bool isEdge = h is SelHandle.Top or SelHandle.Bottom or SelHandle.Left or SelHandle.Right;
            if (isEdge && !includeEdgeHandles) continue;
            if (thinVertical && h is SelHandle.Top or SelHandle.Bottom) continue;
            if (thinHorizontal && h is SelHandle.Left or SelHandle.Right) continue;
            if (Vector2.DistanceSquared(p, CanvasPosition(h, f, dpiScale)) <= r * r) return h;
        }
        return SelHandle.None;
    }

    /// <summary>
    /// 拖动换算。返回的是**框坐标**下的矩阵；调用方要把它共轭回画布坐标：
    /// <c>F⁻¹ · M · F</c>（见 InkEngine.UpdateSelDrag）。
    ///
    /// 为什么不在画布坐标里直接算：框是斜的，而缩放的锚点是"对角那个手柄"，
    /// 在斜的坐标系里做轴向缩放，只有在框坐标里才是"沿框的两条边"。
    /// </summary>
    public static Matrix3x2 DragMatrix(SelHandle handle, in SelectionFrame f,
                                       Vector2 startPoint, Vector2 currentPoint,
                                       float dpiScale, bool uniform, bool snapAngle,
                                       bool noSnap = false)
        => DragMatrix(handle, f.Local, f.ToLocalPoint(startPoint), f.ToLocalPoint(currentPoint),
                      dpiScale, uniform, snapAngle, noSnap);

    /// <summary>
    /// 点到哪个手柄上了。返回 <see cref="SelHandle.None"/> 表示没点中手柄
    /// （调用方接着判断是不是"拖动整个选区"）。
    ///
    /// <paramref name="includeEdgeHandles"/> 为 false 时只认四角和旋转手柄——
    /// 那是"极简"观感：画面干净，代价是单轴拉伸要进菜单。
    /// </summary>
    public static SelHandle HitTest(float canvasX, float canvasY, in RectF b,
                                    float dpiScale, bool includeEdgeHandles = true)
    {
        float r = HitRadiusLogical * dpiScale;
        var p = new Vector2(canvasX, canvasY);

        // **细长对象（一行字、一条横线）的特例**：上下（或左右）手柄的命中区比对象本身
        // 还高，会把整个身子盖住——于是"想拖它"变成了"想缩放它"，而且拖不动。
        // 投影上"拖不动"比"缩不了"气人得多，所以这里：对象在某个方向比手柄的命中直径还窄时，
        // **把那个方向的"边中点"手柄让出来**（四角和旋转手柄照旧）。
        // 2026-09-15 由 --seltest 的"复制拖拽"用例暴露：一条 8 逻辑像素宽的横线，
        // 在正中间按下命中的是"上"手柄。
        // 竖向太窄 → 让出"上/下"；横向太窄 → 让出"左/右"（判据见 ThinEdges）。
        var (thinVertical, thinHorizontal) = ThinEdges(b, dpiScale);

        // 旋转手柄先测：它在框外，不会和四角重叠，但它离上边中点的
        // "上"手柄最近，先测它能避免两个窄命中区互相抢。
        if (Vector2.DistanceSquared(p, Position(SelHandle.Rotate, b, dpiScale)) <= r * r)
            return SelHandle.Rotate;

        Span<SelHandle> order = stackalloc SelHandle[]
        {
            SelHandle.TopLeft, SelHandle.TopRight, SelHandle.BottomLeft, SelHandle.BottomRight,
            SelHandle.Top, SelHandle.Bottom, SelHandle.Left, SelHandle.Right,
        };
        foreach (var h in order)
        {
            bool isEdge = h is SelHandle.Top or SelHandle.Bottom or SelHandle.Left or SelHandle.Right;
            if (isEdge && !includeEdgeHandles) continue;
            if (thinVertical && h is SelHandle.Top or SelHandle.Bottom) continue;
            if (thinHorizontal && h is SelHandle.Left or SelHandle.Right) continue;
            if (Vector2.DistanceSquared(p, Position(h, b, dpiScale)) <= r * r) return h;
        }
        return SelHandle.None;
    }

    /// <summary>对面那个手柄——缩放时它是不动的锚点。</summary>
    private static SelHandle Opposite(SelHandle h) => h switch
    {
        SelHandle.TopLeft => SelHandle.BottomRight,
        SelHandle.Top => SelHandle.Bottom,
        SelHandle.TopRight => SelHandle.BottomLeft,
        SelHandle.Right => SelHandle.Left,
        SelHandle.BottomRight => SelHandle.TopLeft,
        SelHandle.Bottom => SelHandle.Top,
        SelHandle.BottomLeft => SelHandle.TopRight,
        SelHandle.Left => SelHandle.Right,
        _ => SelHandle.None,
    };

    /// <summary>
    /// 拖动旋转手柄 → 旋转了多少度，以及这个角度**是不是被吸附出来的**。
    ///
    /// 三种模式，优先级从高到低：
    ///   · <paramref name="noSnap"/>（按住 Alt）：完全自由。给"我就要 43°"的人一条路。
    ///   · <paramref name="gridSnap"/>（按住 Shift）：硬网格 15°，与 Office / Figma 一致。
    ///   · 默认：**软吸附**——离 90° 的整数倍不足 3° 就吸上去，其余角度原样保留。
    ///
    /// 度数是**相对量**（相对按下那一刻）——对象本来可能就转着、多选时每个对象
    /// 角度还各不相同，显示绝对角度只会让人看不懂。
    /// </summary>
    public static float RotationDeltaDegrees(Vector2 center, Vector2 startPoint, Vector2 currentPoint,
                                             bool gridSnap, bool noSnap, out bool snapped)
        => SnapRotationDegrees(RotationStepDegrees(center, startPoint, currentPoint),
                               gridSnap, noSnap, out snapped);

    /// <summary>
    /// 从 <paramref name="fromPoint"/> 转到 <paramref name="toPoint"/>（都相对
    /// <paramref name="center"/>）的**最短角度增量**。
    ///
    /// **符号约定：逆时针为正、顺时针为负**（用户 2026-09-15 定："贴合我们高中数学"，
    /// 不设上限）。屏幕的 y 轴朝下，`atan2` 算出来的正角在屏幕上其实是**顺时针**，
    /// 所以这里取过一次负号——出来的数就是标签上直接显示的那个数。
    ///
    /// 这一层是给**累积**用的（见 InkEngine.UpdateSelDrag）：拖动中每帧取一小步，
    /// 要的是"这一帧往哪边转了多少"，不是"相对起点一共多少"。取"最短"正是为此——
    /// 一帧里真转过半圈以上才会取错方向，60 帧/秒下那要求指针在一帧里绕半圈。
    /// </summary>
    public static float RotationStepDegrees(Vector2 center, Vector2 fromPoint, Vector2 toPoint)
    {
        float a0 = MathF.Atan2(fromPoint.Y - center.Y, fromPoint.X - center.X);
        float a1 = MathF.Atan2(toPoint.Y - center.Y, toPoint.X - center.X);
        return -NormalizeDegrees((a1 - a0) * 180f / MathF.PI);
    }

    /// <summary>
    /// 对**已经攒好的角度**做吸附。**不做归一化**：角度不设上限（用户 2026-09-15 定），
    /// 转两圈就是 720°，倒着转回去就是 -400°。90 / 15 的整数倍在负角度和超过一圈的
    /// 角度上照样对得上，所以吸附规则一个字都不用改。
    /// </summary>
    public static float SnapRotationDegrees(float deg, bool gridSnap, bool noSnap, out bool snapped)
    {
        snapped = false;
        if (gridSnap)
        {
            deg = MathF.Round(deg / RotationSnapDegrees) * RotationSnapDegrees;
            snapped = true;
        }
        else if (!noSnap)
        {
            float nearest = MathF.Round(deg / RotationSoftSnapDegrees) * RotationSoftSnapDegrees;
            if (MathF.Abs(deg - nearest) <= RotationSoftSnapToleranceDegrees)
            {
                deg = nearest;
                snapped = true;
            }
        }
        return deg;
    }

    /// <summary>
    /// 绕 <paramref name="center"/> 转 <paramref name="deg"/> 度（**逆时针为正**）的矩阵。
    ///
    /// 全工程**只有这一处**把"用户看到的度数"翻成屏幕坐标的旋转方向，两个调用点
    /// （引擎拖手柄、<see cref="DragMatrix"/>）都走它——分开写迟早出现
    /// "读数说 +90°、对象却往 -90° 转"这种对不上的事。
    /// </summary>
    public static Matrix3x2 RotateMatrix(float deg, Vector2 center)
        => Matrix3x2.CreateRotation(-deg * MathF.PI / 180f, center);

    /// <summary>
    /// 把角度归一化到 (-180, 180]。用户看到的是 -43°，不是 317°；也不是 -0°。
    ///
    /// 注意：**标签上的数不再过这里**（角度不设上限）。它还留着，是给
    /// <see cref="RotationStepDegrees"/>（每帧增量）和自检里"独立量一遍角度"用的。
    /// </summary>
    public static float NormalizeDegrees(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg <= -180f) deg += 360f;
        return deg;
    }

    /// <summary>
    /// 度数标签上的字。四舍五入到整度，**不留 -0°**，并且**不绕回**：
    /// 740° 就写 "740°"，倒着转两圈就写 "-720°"（用户：不设上限）。
    /// 正数不带 "+"，负数自带 "-"。
    /// </summary>
    public static string FormatDegrees(float deg)
    {
        int v = (int)MathF.Round(deg);
        if (v == 0) v = 0;                       // -0.4 四舍五入成 -0 → 0
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "°";
    }

    /// <summary>
    /// 把"拖着某个手柄从 <paramref name="startPoint"/> 到 <paramref name="currentPoint"/>"
    /// 换算成一个变换矩阵（作用在**选区开始拖动时**的坐标上）。
    ///
    /// <paramref name="uniform"/>（Shift）：等比缩放，取两个方向里变化大的那个。
    /// <paramref name="snapAngle"/>（Shift 用于旋转）：对齐到 15 度增量。
    /// </summary>
    public static Matrix3x2 DragMatrix(SelHandle handle, in RectF startBounds,
                                       Vector2 startPoint, Vector2 currentPoint,
                                       float dpiScale, bool uniform, bool snapAngle,
                                       bool noSnap = false)
    {
        if (handle == SelHandle.None) return Matrix3x2.Identity;

        if (handle == SelHandle.Rotate)
        {
            var c = new Vector2((startBounds.MinX + startBounds.MaxX) * 0.5f,
                                (startBounds.MinY + startBounds.MaxY) * 0.5f);
            float deg = RotationDeltaDegrees(c, startPoint, currentPoint, snapAngle, noSnap, out _);
            return RotateMatrix(deg, c);
        }

        // 缩放 / 拉伸：对面那个手柄是**不动的锚点**。
        var anchor = Position(Opposite(handle), startBounds, dpiScale);
        var (u, v) = Uv(handle);

        // 拖动方向上的"起始臂长"。边中点手柄只在单轴上有效。
        float armX = Position(handle, startBounds, dpiScale).X - anchor.X;
        float armY = Position(handle, startBounds, dpiScale).Y - anchor.Y;
        float curX = currentPoint.X - anchor.X;
        float curY = currentPoint.Y - anchor.Y;

        float sx = MathF.Abs(armX) > 1e-3f ? curX / armX : 1f;
        float sy = MathF.Abs(armY) > 1e-3f ? curY / armY : 1f;

        // 边中点：只让它管一个轴，另一个轴保持 1。
        if (u == 0.5f) sx = 1f;
        if (v == 0.5f) sy = 1f;

        // 四角 = **等比缩放**，不管有没有按 Shift。
        //
        // 理由：核心场景是"把写的内容放大/缩小"，不是压扁；而非等比加上
        // "线宽不变"会让笔迹看起来像被压过的图片。单轴拉伸已经由边中点负责了，
        // 四角再来一遍自由缩放在功能上是重复的。
        bool isCorner = u != 0.5f && v != 0.5f;
        if (uniform || isCorner)
        {
            float s = (u == 0.5f) ? MathF.Abs(sy) : (v == 0.5f) ? MathF.Abs(sx)
                    : MathF.Max(MathF.Abs(sx), MathF.Abs(sy));
            if (u != 0.5f) sx = s * MathF.Sign(sx == 0f ? 1f : sx);
            if (v != 0.5f) sy = s * MathF.Sign(sy == 0f ? 1f : sy);
        }

        // **夹住而不是翻转**：拖过头时停在最小尺寸，不越过锚点变成镜像。
        // 镜像要从操作条按按钮——那里是显式操作，不会误触。
        // （同时也保证矩阵不退化，撤销永远能回来。）
        sx = ClampScale(sx);
        sy = ClampScale(sy);

        return Matrix3x2.CreateScale(sx, sy, anchor);
    }

    private static float ClampScale(float s)
    {
        if (float.IsNaN(s)) return 1f;
        return MathF.Max(MinScale, s);
    }

    /// <summary>
    /// 显式的镜像矩阵。**这是"翻转"唯一合法的入口**——拖动永远不会产生它。
    /// 左右翻转 = 绕选区的竖直中轴做 -1 的 X 缩放；上下翻转同理。
    /// </summary>
    public static Matrix3x2 MirrorMatrix(in RectF bounds, bool horizontal)
    {
        var c = new Vector2((bounds.MinX + bounds.MaxX) * 0.5f,
                            (bounds.MinY + bounds.MaxY) * 0.5f);
        return horizontal ? Matrix3x2.CreateScale(-1f, 1f, c)
                          : Matrix3x2.CreateScale(1f, -1f, c);
    }

    /// <summary>整体平移（在选中框内部按下拖动）。</summary>
    public static Matrix3x2 MoveMatrix(Vector2 from, Vector2 to)
        => Matrix3x2.CreateTranslation(to - from);

    /// <summary>
    /// 这个变换是不是**镜像过**的（行列式为负）。
    ///
    /// 早先它管旋转读数的正负：镜框 + 斜框的组合下，同一个拖动在框坐标里量出来是
    /// 顺时针、在屏幕上看着却是逆时针，读数得翻回来。
    /// 选中框改成**一律轴对齐**之后（用户 2026-09-16），框坐标就是屏幕坐标，
    /// 读数天生按眼睛看到的方向走，引擎里那处判断已经恒不触发；
    /// 这个判断仍被自检用着（要报"这一条到底镜像没镜像"），所以留着。
    /// </summary>
    public static bool IsMirrored(in Matrix3x2 m)
        => m.M11 * m.M22 - m.M12 * m.M21 < 0f;

    // =====================================================================
    //  操作条（复制 / 删除 / 左右翻转 / 上下翻转 / 旋转）
    //
    //  布局放在这里而不是渲染层：**命中判定和绘制必须用同一套尺寸**。
    //  分开写迟早会差几个像素，表现就是"看得见按钮却点不中"。放这里
    //  也让它可以被 --handletest 直接验。
    // =====================================================================

    /// <summary>
    /// 操作条按钮数（2026-09-16 从 4 扩到 9；**2026-09-22 从 9 扩到 10**，顺序见
    /// <see cref="SelBarButton"/>）：
    /// 收起 / 颜色 / 锁定 / 层级 / 导出 / **图库** / 复制 / 左右翻转 / 上下翻转 / 删除。
    ///
    /// 三条排布上的理由：
    ///   · **危险动作在最右**（删除），离手远一点；
    ///   · **开关类在左**（收起、颜色、锁定、层级），它们是"点一下看状态"的；
    ///   · **仍然没有"旋转 90°"**：旋转手柄 + Shift 的 15° 吸附已经覆盖任意角度，
    ///     再放一格是冗余（用户 2026-09-15 明确不要）。翻转两格**保留在条上**
    ///     （用户 2026-09-16 定：九格，不把翻转收进子面板）。
    ///
    /// **2026-09-22 加的第十格是「图库」**（用户："墨迹选中的操作栏有一个收藏的图标"）。
    /// 位置挑在**导出旁边**：两者都是"把选中的东西弄到别处去"，而且离删除这一格还隔着
    /// 复制/翻转三格——不会手滑点到删除。整条从 348 宽变 386（38×10），仍然是胶囊。
    ///
    /// **2026-09-20 这天第十格「开口方向」加了又撤、撤了又加，最后**真的撤掉**了**，
    /// 三次的理由都记在这儿（免得以后又翻烧饼）：
    ///   · **第一次撤**（用户："在选中栏调方向感觉不好"）：那时**拖手势也能改方向**，
    ///     条上一格和手势重复，用户不知道该用哪个；
    ///   · **加回**：抛物线改成"先选开口、再用两点画"之后，拖手势不管方向了，
    ///     朝向只剩这一个入口——不加回的话"画完想换开口"只能删了重画；
    ///   · **最终撤掉**（用户 2026-09-20 深夜："选中框的抛物线按钮功能取消哦，
    ///     我不打算从这个转抛物线开口"）：朝向改到**画之前**在图形面板里定
    ///     （那一格已经选中抛物线时**再点一次**换一档，见 Engine.CycleParabolaAxis）。
    ///     选中之后就是不能再改朝向——这是用户的选择，不是缺功能。
    /// </summary>
    public const int BarButtonCount = 10;
    /// <summary>
    /// 操作条的高度 / 每格宽度 / 格间距（逻辑像素）。
    ///
    /// 沿革：九格铺开时 44×34（太厚）→ 2026-09-16 缩到 38×30 → **8.2.1 收成 30×30 方格子**
    /// （用户 2026-09-30 第二轮："选中条还是太松散——选中悬停是长方形不是正方形，所以分散"）。
    /// 30 是条件定出来的：条高就是 30，格子宽取同一个数 → **每格是正方形**；
    /// 图标 18 / 格子 30 = **0.6**，正好和主工具条那条的 24 / 40 同比例。
    /// 悬停 / 激活的底也跟着变成正方形（见 <see cref="BarHoverChip"/>）。
    ///
    /// ⚠ 两端内边距不在这里，用浮层那套 token（<see cref="FloatPadLogical"/> = 6）：
    /// "三处浮层共用一套尺寸"是 8.2.0 定的。
    /// </summary>
    public const float BarHeightLogical = 30f;
    public const float BarButtonWidthLogical = 30f;
    public const float BarGapLogical = 0f;

    /// <summary>
    /// 悬停 / 激活那块**正方形底**从格子边缘往里缩多少（逻辑像素）。
    ///
    /// 8.2.1 之前它是"四周各缩 3"，于是 38×30 的格子里画出来是 **32×24 的长方形**——
    /// 用户一眼就看出问题（"悬停是长方形不是正方形，所以分散"）。
    /// 现在：底 = min(格宽, 格高) − 2×2 = **26×26 正方形**，居中；格与格之间只隔 4 像素，
    /// 高亮连成"一排小方块"，比原来那种扁长高亮收紧得多。
    /// **画和自检都读这一份**（见 <see cref="BarHoverChip"/>）。
    /// </summary>
    public const float BarHoverInsetLogical = 2f;

    /// <summary>
    /// 操作条第 <paramref name="i"/> 格"悬停 / 激活"那块正方形底。
    /// 尺寸 = `min(格宽,格高) − 2×inset`，居中放在格子里——**画与自检同源**
    /// （见 <see cref="BarHoverInsetLogical"/> 那段：这个形状是用户点名要改的，别再各算一份）。
    /// </summary>
    public static RectF BarHoverChip(int i, in RectF sel, float dpi, in RectF visible)
    {
        var btn = BarButtonRect(i, sel, dpi, visible);
        float side = MathF.Min(btn.MaxX - btn.MinX, btn.MaxY - btn.MinY) - BarHoverInsetLogical * 2f * dpi;
        side = MathF.Max(1f, side);
        float cx = (btn.MinX + btn.MaxX) * 0.5f, cy = (btn.MinY + btn.MaxY) * 0.5f;
        return new RectF { MinX = cx - side * 0.5f, MinY = cy - side * 0.5f,
                           MaxX = cx + side * 0.5f, MaxY = cy + side * 0.5f };
    }
    /// <summary>选中框下边到操作条的距离（逻辑像素）。</summary>
    public const float BarOffsetLogical = 14f;
    /// <summary>
    /// 操作条下边缘与"可见区域下边"的最小距离（逻辑像素）——即**给条一个下限**。
    ///
    /// 为什么是"给下限"而不是"翻到选区上方"（用户 2026-09-15 定的）：
    /// 翻上去会盖住选区上方的板书，而且条的位置会跳来跳去；给下限更稳——条永远在框
    /// 下方，到了离屏幕下边这么近就**停住**，宁可压住一点选中内容（条画在浮动层上，
    /// 永远可见、可点）。
    /// </summary>
    public const float BarMinBottomMarginLogical = 12f;
    /// <summary>两侧留白：条不贴屏幕边（逻辑像素）。</summary>
    public const float BarScreenPaddingLogical = 8f;
    /// <summary>图标框边长（逻辑像素）。Fluent 图标自带内边距，所以比字形大一点。</summary>
    /// <summary>图标框边长（逻辑像素）。从 22 收到 18：图标跟着条一起缩小，
    /// 但**不能再小**——投影上 16 以下就开始糊。</summary>
    public const float BarIconBoxLogical = 18f;

    /// <summary>
    /// 收起态那个小圆钮的直径（逻辑像素）。
    ///
    /// 用户 2026-09-16 更正："条首那个 ✕ 不是取消选择，是**收起工具条**"——
    /// 点它整条收起来，只剩这么一个小圆，点它再展开（参考实现也是这个形状）。
    /// 定 44：和条高（34）一个量级、比手柄大一圈，投影上点得中。
    /// </summary>
    /// <summary>
    /// 收起态小圆球的直径。44 → 36 → **26**（用户："收起以后的小圆球太大，不美观"）。
    ///
    /// 为什么敢给这么小：它只干一件事——"点我展开"，而且指针此刻就在附近
    /// （老师刚点过条上的收起）。与其说它是个按钮，不如说是个**标记**；
    /// 26 逻辑像素在投影上仍然看得清，和工具条那边 40 的最小可点尺寸是两种东西。
    /// </summary>
    public const float BarCollapsedDotLogical = 26f;

    /// <summary>收起态圆钮与选中框下边的距离（逻辑像素，和展开态一致，位置不跳）。</summary>
    public const float BarCollapsedOffsetLogical = 14f;

    // ---- 浮层尺寸 token（8.2.0：三处浮层共用这一套）------------------------
    //
    // 用户 2026-09-30："有点松散，不精致" → 只把**尺寸**统一到这一套，不动交互。
    // 为什么这么定（都不新开数）：外框内边距沿用操作条原来那个 6（比面板原来的 10 紧一档）；
    // 格间距取 4，和主工具条的 `GapInGroup` 同一个节奏；圆角走界面推上来的
    // `UiTheme.CornerRadius`（= InkUi.Tokens.FloatingCorner 10），不在这里再写一份。
    // 分组缝沿用各自已有的数（操作条格缝 0、面板与条之间 10），没有新开。

    /// <summary>
    /// 浮层外框的内边距（逻辑像素）：操作条两端、墨迹/层级面板四边、取色板四边。
    /// 6 = 原有操作条的那个数；面板原来 10，8.2.0 收到 6（"对齐主工具条的密度"）。
    /// </summary>
    public const float FloatPadLogical = 6f;

    /// <summary>
    /// 浮层里的格间距（逻辑像素）：面板里相邻色片 / 线型格 / 层级格 / 行与行之间。
    /// 4 = 和主工具条组内缝（`Tokens.GapInGroup`）同一个节奏；面板原来 7/8，收到 4。
    /// </summary>
    public const float FloatGapLogical = 4f;

    /// <summary>色片边长。26 不动：三处浮层收的是"缝"，不是控件本身。</summary>
    public const float SwatchSizeLogical = 26f;
    /// <summary>色板列数（4 列：中性一行、暖一行、冷一行 + 末格自定义）。</summary>
    public const int SwatchColumns = 4;
    /// <summary>
    /// 面板里那两行的高度（逻辑像素）：**滑条行 30、线型行 26**。
    ///
    /// 原来两行都是 34；8.2.1 用户第二眼："那三个（实/虚/点）线占位太多"——
    /// 线型行收到 26、滑条行收到 30，面板总高 196 → **184**。
    /// 滑条行比线型行多 4：它上方要留"拖动中的数值"那行字的位置（见
    /// `DrawInkPanel` 里画数值那段）。
    /// </summary>
    public const float SliderRowLogical = 30f;
    public const float StyleRowLogical = 26f;
    /// <summary>面板与它上面那条（操作条）的距离。</summary>
    public const float PanelGapLogical = 10f;
    /// <summary>层级面板每一格的边长（两格并排）。</summary>
    public const float LayerCellLogical = 40f;

    /// <summary>滑条右端留给"笔尖预览点"的宽度（逻辑像素）。和主条那条滑条同一个语言：
    /// 右端一颗跟着值变大的点——粗细一眼看得见，不用读数字。</summary>
    public const float SliderTailLogical = 30f;
    /// <summary>滑条两端的缩进：滑钮（半径 7）不许贴着面板边。</summary>
    public const float SliderInsetLogical = 8f;

    // ---- 自定义取色板（8.2.0，色相条 + 饱和度/明度方块）的尺寸（逻辑像素）----

    /// <summary>饱和度/明度方块的边长。104 够用：它是最主要的那块取色面。</summary>
    public const float PickSvLogical = 104f;

    /// <summary>竖直色相条的宽。16 在投影上也点得中、拖得住。</summary>
    public const float PickHueLogical = 16f;

    /// <summary>底部"当前色预览"那一行的高（和色片 26 同一个量级）。</summary>
    public const float PickPreviewLogical = 26f;

    /// <summary>
    /// 自定义取色板里那三个可点部分。
    ///
    /// 单独一枚枚举（不复用 <see cref="PanelPart"/>）：取色板是**另一张卡片**，
    /// 它和墨迹面板的格子没有任何语义重叠，混进一张表只会让"点到哪儿了"更难读。
    /// </summary>
    internal enum PickPart
    {
        None = 0,
        /// <summary>饱和度（横）／明度（纵）方块。</summary>
        Sv,
        /// <summary>竖直色相条。</summary>
        Hue,
        /// <summary>卡片里除了上面两块的空白处（点了不做事，但**不算"点在外面"**）。</summary>
        Inside,
    }

    /// <summary>
    /// 自定义取色板的外框：挂在墨迹面板旁边（右边放不下翻到左边），**底边对齐**——
    /// 离"自定义"那一格（色板末格）最近，眼睛不用跨半张面板找。
    /// </summary>
    public static RectF CustomPanelRect(in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        float w = (FloatPadLogical * 2 + PickSvLogical + FloatGapLogical + PickHueLogical) * dpi;
        float h = (FloatPadLogical * 2 + PickSvLogical + FloatGapLogical + PickPreviewLogical) * dpi;
        var ink = PanelRect(sel, dpi, visible, swatchCount);
        float gap = PanelGapLogical * dpi;
        float x = ink.MaxX + gap;
        float y = ink.MaxY - h;

        if (!visible.IsEmpty)
        {
            float margin = BarScreenPaddingLogical * dpi;
            float left = visible.MinX + margin;
            float right = MathF.Max(left, visible.MaxX - margin - w);
            if (x > right) x = ink.MinX - gap - w;             // 右边放不下 → 翻到左边
            x = Math.Clamp(x, left, right);
            float top = visible.MinY + margin;
            float bottom = MathF.Max(top, visible.MaxY - margin - h);
            y = Math.Clamp(y, top, bottom);
        }
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>取色板里饱和度/明度方块的矩形（画与命中同源）。</summary>
    public static RectF PickSvRect(in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var p = CustomPanelRect(sel, dpi, visible, swatchCount);
        float pad = FloatPadLogical * dpi;
        return new RectF
        {
            MinX = p.MinX + pad, MinY = p.MinY + pad,
            MaxX = p.MinX + pad + PickSvLogical * dpi, MaxY = p.MinY + pad + PickSvLogical * dpi,
        };
    }

    /// <summary>取色板里竖直色相条的矩形。</summary>
    public static RectF PickHueRect(in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var sv = PickSvRect(sel, dpi, visible, swatchCount);
        float gap = FloatGapLogical * dpi;
        return new RectF
        {
            MinX = sv.MaxX + gap, MinY = sv.MinY,
            MaxX = sv.MaxX + gap + PickHueLogical * dpi, MaxY = sv.MaxY,
        };
    }

    /// <summary>取色板底部"当前色预览"那一格（含原色／新色两块色片）。</summary>
    public static RectF PickPreviewRect(in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var sv = PickSvRect(sel, dpi, visible, swatchCount);
        float gap = FloatGapLogical * dpi;
        return new RectF
        {
            MinX = sv.MinX, MinY = sv.MaxY + gap,
            MaxX = PickHueRect(sel, dpi, visible, swatchCount).MaxX, MaxY = sv.MaxY + gap + PickPreviewLogical * dpi,
        };
    }

    /// <summary>点到取色板的哪一块了（<see cref="PickPart.Inside"/> = 卡片里、控件外）。</summary>
    public static PickPart PickPartAt(float x, float y, in RectF sel, float dpi, in RectF visible,
                                      int swatchCount)
    {
        if (!CustomPanelRect(sel, dpi, visible, swatchCount).Contains(x, y)) return PickPart.None;
        if (PickSvRect(sel, dpi, visible, swatchCount).Contains(x, y)) return PickPart.Sv;
        if (PickHueRect(sel, dpi, visible, swatchCount).Contains(x, y)) return PickPart.Hue;
        return PickPart.Inside;
    }

    /// <summary>点在不在取色板这张卡片里（"点外面 = 取消"那条判据用它）。</summary>
    public static bool PickContains(float x, float y, in RectF sel, float dpi, in RectF visible,
                                    int swatchCount)
        => CustomPanelRect(sel, dpi, visible, swatchCount).Contains(x, y);

    /// <summary>色板里有几个色片（引擎侧的色板表长度）。</summary>
    public static int SwatchCount => InkPalette.SelectionSwatches.Length;

    /// <summary>
    /// 收起态圆钮的矩形：**横向跟着框居中、纵向在框下方同一个位置**——
    /// 展开/收起时圆钮和条的横纵位置一致，切换不会"跳"。
    /// </summary>
    public static RectF BarCollapsedRect(in RectF sel, float dpi, in RectF visible)
    {
        float d = BarCollapsedDotLogical * dpi;
        float x = (sel.MinX + sel.MaxX) * 0.5f - d * 0.5f;
        float y = sel.MaxY + BarCollapsedOffsetLogical * dpi;

        if (!visible.IsEmpty)
        {
            float margin = BarScreenPaddingLogical * dpi;
            x = Math.Clamp(x, visible.MinX + margin, MathF.Max(visible.MinX + margin, visible.MaxX - margin - d));
            float top = visible.MinY + margin;
            float bottomMost = visible.MaxY - BarMinBottomMarginLogical * dpi - d;
            y = MathF.Min(y, MathF.Max(top, bottomMost));
            if (y < top) y = top;
        }
        return new RectF { MinX = x, MinY = y, MaxX = x + d, MaxY = y + d };
    }

    /// <summary>面板的高度：滑条行 + 线型行 + 色板若干行 + 内边距（画与命中同源）。</summary>
    public static float PanelHeightLogical(int swatchCount)
    {
        int rows = (swatchCount + SwatchColumns - 1) / SwatchColumns;
        float gridH = rows * SwatchSizeLogical + MathF.Max(0, rows - 1) * FloatGapLogical;
        return FloatPadLogical * 2 + SliderRowLogical + StyleRowLogical + gridH;
    }

    /// <summary>
    /// 浮动面板的矩形。默认挂在**操作条正下方**（参考实现就是这样）；
    /// 下方放不下就翻到**选中框上方**；再放不下就夹在可见区域里。
    ///
    /// 注意它比条高一截（240 逻辑像素量级），比条更容易出屏——所以夹取规则比 BarRect
    /// 多一条"翻面"，不能照抄条形那套"给下限、不翻面"。
    /// </summary>
    public static RectF PanelRect(in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        float w = FloatPadLogical * 2
                + SwatchColumns * SwatchSizeLogical + (SwatchColumns - 1) * FloatGapLogical;
        float h = PanelHeightLogical(swatchCount) * dpi;
        w *= dpi;

        var bar = BarRect(sel, dpi, visible);
        float x = bar.MinX;                                       // 左对齐条（参考图）
        float y = bar.MaxY + PanelGapLogical * dpi;

        if (!visible.IsEmpty)
        {
            float margin = BarScreenPaddingLogical * dpi;
            float left = visible.MinX + margin;
            float right = MathF.Max(left, visible.MaxX - margin - w);
            x = Math.Clamp(x, left, right);

            float top = visible.MinY + margin;
            float bottom = visible.MaxY - margin;
            if (y + h > bottom)                                   // 下方放不下：翻到框上方
            {
                float above = sel.MinY - PanelGapLogical * dpi - h;
                y = above >= top ? above : MathF.Max(top, bottom - h);
            }
            y = Math.Clamp(y, top, MathF.Max(top, bottom - h));
        }
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>层级小面板的矩形（两格并排，贴在"层级"那一格的下面）。</summary>
    public static RectF LayerPanelRect(in RectF sel, float dpi, in RectF visible)
        => PanelBelow((int)SelBarButton.Layer, LayerCellLogical, LayerCellLogical, 2, sel, dpi, visible);

    /// <summary>
    /// "挂在操作条某个按钮下面的小面板"——层级和导出共用这一份几何：
    /// 水平对准按钮中心、垂直贴在条下方；下方放不下就翻到选区上方；最后夹进可见区。
    /// </summary>
    private static RectF PanelBelow(int button, float cellLogical, float cellHeightLogical, int cells,
                                    in RectF sel, float dpi, in RectF visible)
    {
        float w = (cellLogical * cells + FloatGapLogical * (cells - 1)
                 + FloatPadLogical * 2) * dpi;
        float h = (cellHeightLogical + FloatPadLogical * 2) * dpi;
        var bar = BarRect(sel, dpi, visible);
        var btn = BarButtonRect(button, sel, dpi, visible);

        float x = btn.MinX + (btn.MaxX - btn.MinX) * 0.5f - w * 0.5f;
        float y = bar.MaxY + PanelGapLogical * dpi;

        if (!visible.IsEmpty)
        {
            float margin = BarScreenPaddingLogical * dpi;
            x = Math.Clamp(x, visible.MinX + margin, MathF.Max(visible.MinX + margin, visible.MaxX - margin - w));
            float top = visible.MinY + margin;
            float bottom = visible.MaxY - margin;
            if (y + h > bottom)
            {
                float above = sel.MinY - PanelGapLogical * dpi - h;
                y = above >= top ? above : MathF.Max(top, bottom - h);
            }
            y = Math.Clamp(y, top, MathF.Max(top, bottom - h));
        }
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>层级面板里第 i 格（0 = 置顶，1 = 置底）。</summary>
    public static RectF LayerCellRect(int i, in RectF sel, float dpi, in RectF visible)
    {
        var p = LayerPanelRect(sel, dpi, visible);
        float pad = FloatPadLogical * dpi, cell = LayerCellLogical * dpi;
        float x = p.MinX + pad + i * (cell + FloatGapLogical * dpi);
        return new RectF { MinX = x, MinY = p.MinY + pad, MaxX = x + cell, MaxY = p.MinY + pad + cell };
    }

    /// <summary>颜色面板里第 i 个色片。</summary>
    public static RectF SwatchRect(int i, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var p = PanelRect(sel, dpi, visible, swatchCount);
        float pad = FloatPadLogical * dpi;
        float size = SwatchSizeLogical * dpi, gap = FloatGapLogical * dpi;
        float gridTop = p.MaxY - pad - ((swatchCount + SwatchColumns - 1) / SwatchColumns) * size
                      - (((swatchCount + SwatchColumns - 1) / SwatchColumns) - 1) * gap;
        int col = i % SwatchColumns, row = i / SwatchColumns;
        float x = p.MinX + pad + col * (size + gap);
        float y = gridTop + row * (size + gap);
        return new RectF { MinX = x, MinY = y, MaxX = x + size, MaxY = y + size };
    }

    /// <summary>颜色面板里"粗细滑条"那一行的矩形（行高见 <see cref="SliderRowLogical"/>）。</summary>
    public static RectF SliderRect(in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var p = PanelRect(sel, dpi, visible, swatchCount);
        float pad = FloatPadLogical * dpi;
        return new RectF
        {
            MinX = p.MinX + pad, MinY = p.MinY + pad,
            MaxX = p.MaxX - pad, MaxY = p.MinY + pad + SliderRowLogical * dpi,
        };
    }

    /// <summary>
    /// 颜色面板里"线型"那一行的第 i 格：
    /// **0 = 实线、1 = 虚线、2 = 点线**——顺序刻意与 <see cref="StrokeDash"/> 的取值一致，
    /// 这样"第 i 格"和"第 i 号线型"不用再做一次对照表（少一张表就少一个漏改的地方）。
    ///
    /// 这一行原来是 2 格（实线/虚线）的占位；2026-09-19 接底层时扩成 3 格。
    /// 格宽按"面板内宽 ÷ 格数"平分，以后再加档位只改 <see cref="StyleCellCount"/>。
    /// </summary>
    public static RectF StyleCellRect(int i, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var p = PanelRect(sel, dpi, visible, swatchCount);
        float pad = FloatPadLogical * dpi;
        float gap = FloatGapLogical * dpi;
        float rowTop = p.MinY + pad + SliderRowLogical * dpi;
        float cellW = (p.MaxX - p.MinX - pad * 2 - gap * (StyleCellCount - 1)) / StyleCellCount;
        float x = p.MinX + pad + i * (cellW + gap);
        return new RectF { MinX = x, MinY = rowTop, MaxX = x + cellW, MaxY = rowTop + StyleRowLogical * dpi };
    }

    /// <summary>线型那一行有几格。**画与命中都读它**（见 <see cref="StyleCellRect"/>）。</summary>
    public const int StyleCellCount = 3;

    /// <summary>
    /// 线型那一行现在该高亮哪一档 = **选中对象里第一条非图像对象的线型**
    /// （和色板那条"取第一条的颜色"是同一个口径：多选时以第一条为准，改的时候整批一起改）。
    ///
    /// **画（浮动面板）与自检都读它**：以前这段"取第一条"的循环只写在绘制那一处，
    /// 自检要断言"面板认得出现在是实线"就只能自己再抄一遍——那等于用另一份口径
    /// 去验另一份口径，抄错了还看不出来。
    /// </summary>
    public static StrokeDash DashOfSelection(IReadOnlyList<Stroke> sel)
    {
        for (int i = 0; i < sel.Count; i++)
        {
            var s = sel[i];
            if (s.IsImage) continue;              // 图像没有"线型"这个概念
            return s.Dash;
        }
        return StrokeDash.Solid;                  // 没选中（或全是图像）：按实线高亮
    }

    /// <summary>点到面板的哪个部分了（渲染和命中同源）。</summary>
    internal enum PanelPart
    {
        None = 0,
        Slider,
        StyleSolid,
        StyleDashed,
        StyleDotted,
        SwatchBase,     // + i
        LayerFront,
        LayerBack,
    }

    /// <summary>
    /// 面板命中。**返回 None 也可能是"点在面板的空白处"**——调用方用
    /// <see cref="PanelContains"/> 区分"点在外面（该收起面板）"和"点在面板上（别收起）"。
    /// </summary>
    public static PanelPart PanelPartAt(float x, float y, in RectF sel, float dpi, in RectF visible,
                                        SelPanel panel, int swatchCount)
    {
        if (panel == SelPanel.Ink)
        {
            var slider = SliderRect(sel, dpi, visible, swatchCount);
            if (slider.Contains(x, y)) return PanelPart.Slider;
            for (int i = 0; i < StyleCellCount; i++)
            {
                if (!StyleCellRect(i, sel, dpi, visible, swatchCount).Contains(x, y)) continue;
                return i switch
                {
                    1 => PanelPart.StyleDashed,
                    2 => PanelPart.StyleDotted,
                    _ => PanelPart.StyleSolid,
                };
            }
            for (int i = 0; i < swatchCount; i++)
                if (SwatchRect(i, sel, dpi, visible, swatchCount).Contains(x, y))
                    return PanelPart.SwatchBase + i;
        }
        else if (panel == SelPanel.Layer)
        {
            if (LayerCellRect(0, sel, dpi, visible).Contains(x, y)) return PanelPart.LayerFront;
            if (LayerCellRect(1, sel, dpi, visible).Contains(x, y)) return PanelPart.LayerBack;
        }
        return PanelPart.None;
    }

    /// <summary>点在不在（当前这个）面板的卡片里面。</summary>
    public static bool PanelContains(float x, float y, in RectF sel, float dpi, in RectF visible,
                                     SelPanel panel, int swatchCount)
    {
        if (panel == SelPanel.Ink) return PanelRect(sel, dpi, visible, swatchCount).Contains(x, y);
        if (panel == SelPanel.Layer) return LayerPanelRect(sel, dpi, visible).Contains(x, y);
        return false;
    }

    /// <summary>
    /// 滑条**轨道**的左右端（逻辑像素）。**画与拖共用这一份**——各算一份的话，
    /// 迟早会出现"看着在中间、点出来偏一截"（和滚动条那条老规矩一样）。
    ///
    /// 右端让出 <see cref="SliderTailLogical"/>：那是"笔尖预览点"的地盘
    /// （和主条那条滑条同一个语言，见 `DrawBandSlider`）。
    /// </summary>
    public static (float Left, float Right) SliderTrackRange(in RectF sel, float dpi, in RectF visible,
                                                              int swatchCount)
    {
        var r = SliderRect(sel, dpi, visible, swatchCount);
        float a = r.MinX + SliderInsetLogical * dpi;
        float b = r.MaxX - SliderTailLogical * dpi;
        return (a, MathF.Max(a, b));
    }

    /// <summary>0..1 → 滑条上的 x（连续；不再吸档位）。</summary>
    public static float SliderXOfT(float t, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var (a, b) = SliderTrackRange(sel, dpi, visible, swatchCount);
        return a + (b - a) * Math.Clamp(t, 0f, 1f);
    }

    /// <summary>x → 0..1（连续；越界夹住）。</summary>
    public static float SliderTAt(float x, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var (a, b) = SliderTrackRange(sel, dpi, visible, swatchCount);
        if (b <= a) return 0f;
        return Math.Clamp((x - a) / (b - a), 0f, 1f);
    }

    /// <summary>
    /// 档位点在滑条上的 x（**只作为参考刻度**：滑条 8.2.0 起是连续的，
    /// 那排小点还画——它们说的是"以前那几个档位在这儿"，老师凭肌肉记忆找得到地方）。
    /// </summary>
    public static float SliderStepX(int i, int n, in RectF sel, float dpi, in RectF visible, int swatchCount)
        => SliderXOfT(n <= 1 ? 0f : (float)i / (n - 1), sel, dpi, visible, swatchCount);

    /// <summary>
    /// 操作条在画布坐标里的矩形。<paramref name="visible"/> 是当前可见的画布范围
    /// （`InkEngine.ViewportCanvas`）。
    ///
    /// **渲染和命中必须调这一个函数**（分开写迟早差几个像素，表现就是"看得见点不中"）。
    /// 夹取规则只有两条：横向夹在可见区域内；纵向上边不越顶、**下边不低于下限**。
    /// </summary>
    public static RectF BarRect(in RectF sel, float dpi, in RectF visible)
    {
        float btnW = BarButtonWidthLogical * dpi;
        float h = BarHeightLogical * dpi;
        float pad = FloatPadLogical * dpi;
        float gap = BarGapLogical * dpi;
        float w = pad * 2 + BarButtonCount * btnW + (BarButtonCount - 1) * gap;

        float x = (sel.MinX + sel.MaxX) * 0.5f - w * 0.5f;
        float y = sel.MaxY + BarOffsetLogical * dpi;

        if (!visible.IsEmpty)
        {
            float margin = BarScreenPaddingLogical * dpi;
            float left = visible.MinX + margin;
            float right = MathF.Max(left, visible.MaxX - margin - w);
            x = Math.Clamp(x, left, right);

            // 上边不越顶；**下边不低于下限**（用户定的"给下限，不翻面"）。
            float top = visible.MinY + margin;
            float bottomMost = visible.MaxY - BarMinBottomMarginLogical * dpi - h;
            y = MathF.Min(y, MathF.Max(top, bottomMost));
            if (y < top) y = top;
        }
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>第 i 个按钮的矩形（i 从 0 起）。</summary>
    public static RectF BarButtonRect(int i, in RectF sel, float dpi, in RectF visible)
    {
        var bar = BarRect(sel, dpi, visible);
        float btnW = BarButtonWidthLogical * dpi;
        float pad = FloatPadLogical * dpi;
        float gap = BarGapLogical * dpi;
        float x = bar.MinX + pad + i * (btnW + gap);
        return new RectF { MinX = x, MinY = bar.MinY, MaxX = x + btnW, MaxY = bar.MaxY };
    }

    /// <summary>点到哪个按钮上了。返回 -1 表示没点到操作条。</summary>
    public static int BarButtonAt(float x, float y, in RectF sel, float dpi, in RectF visible)
    {
        var bar = BarRect(sel, dpi, visible);
        if (x < bar.MinX || x > bar.MaxX || y < bar.MinY || y > bar.MaxY) return -1;
        for (int i = 0; i < BarButtonCount; i++)
        {
            var b = BarButtonRect(i, sel, dpi, visible);
            if (x >= b.MinX && x <= b.MaxX) return i;
        }
        return -1;
    }
}
