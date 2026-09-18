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
    /// <summary>复制拖拽模式（点一下进入/退出）。</summary>
    Copy = 5,
    FlipH = 6,
    FlipV = 7,
    Delete = 8,
}

/// <summary>浮动面板的种类。同一时刻只开一个。</summary>
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
    ///
    /// 直线那一条**不给 8 个缩放柄留后门**（连命中都不做）：画都不画了却还能点到，
    /// 就成了"看不见但拖得动"，比"看得见点不到"更难解释。
    /// </summary>
    public static SelHandle HitTest(float canvasX, float canvasY, IReadOnlyList<Stroke> sel,
                                    in SelectionFrame f, float dpiScale)
    {
        if (EndpointEditable(sel, out var s))
        {
            float rad = HitRadiusLogical * dpiScale;
            var p = new Vector2(canvasX, canvasY);
            if (Vector2.DistanceSquared(p, EndpointCanvasPosition(s, 0)) <= rad * rad)
                return SelHandle.EndpointA;
            if (Vector2.DistanceSquared(p, EndpointCanvasPosition(s, 1)) <= rad * rad)
                return SelHandle.EndpointB;
            // 旋转柄照旧（形状是"参数"，旋转是"姿态"，两码事）。
            if (Vector2.DistanceSquared(p, CanvasPosition(SelHandle.Rotate, f, dpiScale)) <= rad * rad)
                return SelHandle.Rotate;
            return SelHandle.None;
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
        foreach (float target in SpecialInclinationDegrees)
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
        foreach (float target in SpecialInclinationDegrees)
        {
            float d = InclinationDistance(folded, target);
            if (d < bestDist) { bestDist = d; best = target; }
        }
        if (bestDist > SoftSnapToleranceDegrees) return deg;
        snapped = true;
        return deg + InclinationShortestDelta(folded, best);
    }

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

    /// <summary>按给定的选区坐标系做手柄命中判定。</summary>
    public static SelHandle HitTest(float canvasX, float canvasY, in SelectionFrame f,
                                    float dpiScale, bool includeEdgeHandles = true)
    {
        float r = HitRadiusLogical * dpiScale;
        var p = new Vector2(canvasX, canvasY);

        // 细长对象让出"边中点"手柄 —— 理由和下面 RectF 版一模一样（两处都要改，
        // 这不是复制代码，是同一个判据的两个入口；漏改一处就会出现"某条路径点不中/拖不动"）。
        var box = f.CanvasAabb;
        bool thinVertical = (box.MaxY - box.MinY) < r * 2f;
        bool thinHorizontal = (box.MaxX - box.MinX) < r * 2f;

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
        float boxW = b.MaxX - b.MinX, boxH = b.MaxY - b.MinY;
        bool thinVertical = boxH < r * 2f;      // 竖向太窄 → 让出"上/下"
        bool thinHorizontal = boxW < r * 2f;    // 横向太窄 → 让出"左/右"

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
    /// 操作条按钮数（2026-09-16 从 4 扩到 9，顺序见 <see cref="SelBarButton"/>）：
    /// 收起 / 颜色 / 锁定 / 层级 / 导出 / 复制 / 左右翻转 / 上下翻转 / 删除。
    ///
    /// 三条排布上的理由：
    ///   · **危险动作在最右**（删除），离手远一点；
    ///   · **开关类在左**（收起、颜色、锁定、层级），它们是"点一下看状态"的；
    ///   · **仍然没有"旋转 90°"**：旋转手柄 + Shift 的 15° 吸附已经覆盖任意角度，
    ///     再放一格是冗余（用户 2026-09-15 明确不要）。翻转两格**保留在条上**
    ///     （用户 2026-09-16 定：九格，不把翻转收进子面板）。
    /// </summary>
    public const int BarButtonCount = 9;
    /// <summary>
    /// 操作条的高度 / 每格宽度 / 内边距 / 格间距（逻辑像素）。
    ///
    /// **2026-09-16 缩小过一轮**：九格铺开之后整条 440×34 太"厚"、太占屏幕
    /// （用户："工具条太大，没有之前美观"）。现在 38×30、内边距 3、格间距 0
    /// ——整条 348×30，比原来窄 20%、矮 12%，而且圆角取高度一半做成**胶囊**，
    /// 视觉上比"厚矩形"轻一档。
    /// 38 这个数不是随手取的：Windows 11 任务栏按钮 40 逻辑像素是"看得清又点得中"的
    /// 那个量级（见 调研-界面-高度.md），再小投影上就吃力了。
    /// </summary>
    public const float BarHeightLogical = 30f;
    public const float BarButtonWidthLogical = 38f;
    /// <summary>两端内边距。给 6：胶囊的两端是圆的，图标贴太近会像"要掉出来"。</summary>
    public const float BarPaddingLogical = 6f;
    public const float BarGapLogical = 0f;
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

    // ---- 浮动面板（颜色/粗细、层级）的尺寸（逻辑像素）----
    public const float PanelPaddingLogical = 10f;
    /// <summary>色片边长。</summary>
    public const float SwatchSizeLogical = 26f;
    public const float SwatchGapLogical = 7f;
    /// <summary>色板列数（4 列：中性一行、暖一行、冷一行 + 末格自定义）。</summary>
    public const int SwatchColumns = 4;
    /// <summary>面板里"滑条行""线型行"的高度。</summary>
    public const float PanelRowLogical = 34f;
    /// <summary>面板与它上面那条（操作条）的距离。</summary>
    public const float PanelGapLogical = 10f;
    /// <summary>层级面板每一格的边长（两格并排）。</summary>
    public const float LayerCellLogical = 40f;

    /// <summary>
    /// 小面板里**两格之间留的缝**（逻辑像素）。
    ///
    /// 一开始是不留缝的（`x = 起点 + i * 格宽`），画出来两格**圆角贴在一起**，
    /// 交界处出现一个"掐进去"的缺口，看着像没画好。留 8 像素之后是两块分开的按钮，
    /// 顺便也符合"相邻的可点区域别共用一条边"这个老规矩（点歪一点不会点错一个）。
    /// </summary>
    public const float PanelCellGapLogical = 8f;

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
        float gridH = rows * SwatchSizeLogical + MathF.Max(0, rows - 1) * SwatchGapLogical;
        return PanelPaddingLogical * 2 + PanelRowLogical * 2 + gridH;
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
        float w = PanelPaddingLogical * 2
                + SwatchColumns * SwatchSizeLogical + (SwatchColumns - 1) * SwatchGapLogical;
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
        float w = (cellLogical * cells + PanelCellGapLogical * (cells - 1)
                 + PanelPaddingLogical * 2) * dpi;
        float h = (cellHeightLogical + PanelPaddingLogical * 2) * dpi;
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
        float pad = PanelPaddingLogical * dpi, cell = LayerCellLogical * dpi;
        float x = p.MinX + pad + i * (cell + PanelCellGapLogical * dpi);
        return new RectF { MinX = x, MinY = p.MinY + pad, MaxX = x + cell, MaxY = p.MinY + pad + cell };
    }

    /// <summary>颜色面板里第 i 个色片。</summary>
    public static RectF SwatchRect(int i, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var p = PanelRect(sel, dpi, visible, swatchCount);
        float pad = PanelPaddingLogical * dpi;
        float size = SwatchSizeLogical * dpi, gap = SwatchGapLogical * dpi;
        float gridTop = p.MaxY - pad - ((swatchCount + SwatchColumns - 1) / SwatchColumns) * size
                      - (((swatchCount + SwatchColumns - 1) / SwatchColumns) - 1) * gap;
        int col = i % SwatchColumns, row = i / SwatchColumns;
        float x = p.MinX + pad + col * (size + gap);
        float y = gridTop + row * (size + gap);
        return new RectF { MinX = x, MinY = y, MaxX = x + size, MaxY = y + size };
    }

    /// <summary>颜色面板里"粗细滑条"那一行的矩形。</summary>
    public static RectF SliderRect(in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var p = PanelRect(sel, dpi, visible, swatchCount);
        float pad = PanelPaddingLogical * dpi;
        return new RectF
        {
            MinX = p.MinX + pad, MinY = p.MinY + pad,
            MaxX = p.MaxX - pad, MaxY = p.MinY + pad + PanelRowLogical * dpi,
        };
    }

    /// <summary>颜色面板里"线型"那一行的第 i 格（0 = 实线，1 = 虚线）。</summary>
    public static RectF StyleCellRect(int i, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var p = PanelRect(sel, dpi, visible, swatchCount);
        float pad = PanelPaddingLogical * dpi;
        float rowTop = p.MinY + pad + PanelRowLogical * dpi;
        float half = (p.MaxX - p.MinX - pad * 2 - 8 * dpi) * 0.5f;
        float x = p.MinX + pad + i * (half + 8 * dpi);
        return new RectF { MinX = x, MinY = rowTop, MaxX = x + half, MaxY = rowTop + PanelRowLogical * dpi };
    }

    /// <summary>点到面板的哪个部分了（渲染和命中同源）。</summary>
    internal enum PanelPart
    {
        None = 0,
        Slider,
        StyleSolid,
        StyleDashed,
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
            for (int i = 0; i < 2; i++)
                if (StyleCellRect(i, sel, dpi, visible, swatchCount).Contains(x, y))
                    return i == 0 ? PanelPart.StyleSolid : PanelPart.StyleDashed;
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

    /// <summary>滑条上第 i 档（共 n 档）的圆钮中心 X。</summary>
    public static float SliderStepX(int i, int n, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        var r = SliderRect(sel, dpi, visible, swatchCount);
        float inset = 10f * dpi;                       // 两端留白，圆钮不贴边
        float a = r.MinX + inset, b = r.MaxX - inset;
        if (n <= 1) return (a + b) * 0.5f;
        return a + (b - a) * i / (n - 1);
    }

    /// <summary>离 (x,y) 最近的档位下标（滑条拖动时吸附用）。</summary>
    public static int SliderNearestStep(float x, int n, in RectF sel, float dpi, in RectF visible, int swatchCount)
    {
        int best = 0; float bestD = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float sx = SliderStepX(i, n, sel, dpi, visible, swatchCount);
            float d = MathF.Abs(sx - x);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

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
        float pad = BarPaddingLogical * dpi;
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
        float pad = BarPaddingLogical * dpi;
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
