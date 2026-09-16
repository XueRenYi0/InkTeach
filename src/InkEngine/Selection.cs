using System.Numerics;

namespace InkEngine;

/// <summary>选中框上的手柄。顺序按钟表方向排，方便按下标算"对面那个"。</summary>
internal enum SelHandle
{
    None = 0,
    TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left,
    /// <summary>旋转手柄，在上边中点外侧。</summary>
    Rotate,
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
    {
        float w = (LayerCellLogical * 2 + PanelPaddingLogical * 2) * dpi;
        float h = (LayerCellLogical + PanelPaddingLogical * 2) * dpi;
        var bar = BarRect(sel, dpi, visible);
        var btn = BarButtonRect((int)SelBarButton.Layer, sel, dpi, visible);

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
        float x = p.MinX + pad + i * cell;
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
