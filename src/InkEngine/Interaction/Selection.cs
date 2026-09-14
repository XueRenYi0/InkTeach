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
/// 选中框的坐标系 = 一个矩形 + 一个"框坐标 → 画布坐标"的变换。
///
/// **单选**：用对象自己的坐标系（局部包围盒 + 它的变换），所以框和手柄跟着对象
/// 一起转。这是主流软件的行为（Figma / PowerPoint / Illustrator 都是）。
/// **多选**：用画布坐标下的轴对齐包围盒。多个对象朝向不同时，硬凑一个"整体朝向"
/// 只会让人看不懂——所以这里刻意不做。
///
/// 这么分的好处：两种情形的数学是同一套。手柄位置在框坐标里算，再变换到画布；
/// 命中判定把指针反变换回框坐标。轴对齐只是"变换为单位矩阵"的特例。
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

    /// <summary>旋转度数标签的尺寸与离手柄的间距（逻辑像素）。</summary>
    public const float ReadoutWidthLogical = 64f;
    public const float ReadoutHeightLogical = 30f;
    public const float ReadoutGapLogical = 9f;

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
    /// <summary>
    /// 旋转度数标签的矩形（**画布坐标**）。
    ///
    /// 放在这一层（而不是渲染层）的理由和手柄位置一样：画它、把它算进脏区、
    /// 以及**自检里量它**，三处必须是同一套几何。少一处对齐，屏幕上就留一块
    /// 擦不掉的残影，或者自检量到隔壁去（这一条刚踩过：自检的量法飘到桌面上，
    /// 把蓝壁纸数成了强调色）。
    ///
    /// 位置贴在旋转手柄外侧（跟着手柄转），并夹在给定的可见范围内——
    /// 选区贴到屏幕边上时，标签不会跑到屏幕外。
    /// </summary>
    public static RectF ReadoutRect(in SelectionFrame frame, float dpi, in RectF visibleCanvas)
    {
        var rot = CanvasPosition(SelHandle.Rotate, frame, dpi);
        float w = ReadoutWidthLogical * dpi, h = ReadoutHeightLogical * dpi;
        float gap = (RotateGripLogical * 0.5f + ReadoutGapLogical) * dpi;

        float cx = rot.X, cy = rot.Y - gap - h * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };

        float pad = 4f * dpi;
        if (r.MinX < visibleCanvas.MinX + pad)
        {
            r.MaxX += visibleCanvas.MinX + pad - r.MinX;
            r.MinX = visibleCanvas.MinX + pad;
        }
        if (r.MaxX > visibleCanvas.MaxX - pad)
        {
            r.MinX -= r.MaxX - (visibleCanvas.MaxX - pad);
            r.MaxX = visibleCanvas.MaxX - pad;
        }
        if (r.MinY < visibleCanvas.MinY + pad)
        {
            r.MaxY += visibleCanvas.MinY + pad - r.MinY;
            r.MinY = visibleCanvas.MinY + pad;
        }
        if (r.MaxY > visibleCanvas.MaxY - pad)
        {
            r.MinY -= r.MaxY - (visibleCanvas.MaxY - pad);
            r.MaxY = visibleCanvas.MaxY - pad;
        }
        return r;
    }

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
    /// 算当前选区的坐标系。单选跟对象转，多选轴对齐（理由见 SelectionFrame 的注释）。
    /// </summary>
    public static SelectionFrame FrameOf(IReadOnlyList<Stroke> sel)
    {
        if (sel == null || sel.Count == 0)
            return new SelectionFrame { Local = RectF.Empty, ToCanvas = Matrix3x2.Identity };

        if (sel.Count == 1)
        {
            var s = sel[0];
            return new SelectionFrame { Local = s.Bounds, ToCanvas = s.Transform };
        }

        // 多选：把每个对象变换后的包围盒并起来，作为轴对齐的框。
        var r = RectF.Empty;
        foreach (var s in sel)
        {
            var one = new SelectionFrame { Local = s.Bounds, ToCanvas = s.Transform };
            r.Add(one.CanvasAabb);
        }
        return new SelectionFrame { Local = r, ToCanvas = Matrix3x2.Identity };
    }

    /// <summary>按给定的选区坐标系做手柄命中判定。</summary>
    public static SelHandle HitTest(float canvasX, float canvasY, in SelectionFrame f,
                                    float dpiScale, bool includeEdgeHandles = true)
    {
        float r = HitRadiusLogical * dpiScale;
        var p = new Vector2(canvasX, canvasY);

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
    /// 返回的是**带符号的相对量**（相对按下那一刻），正 = 顺时针，值域 (-180, 180]。
    ///
    /// 为什么这里保留符号、而给用户看的读数却用 [0,360)（见 <see cref="NormalizeDegrees"/>）：
    /// "转了多少"是有方向的，少了符号就没法把"顺时针 90°"和"逆时针 270°"分开，
    /// 而两者对**接下来的吸附**不是一回事。给用户看的那个数在调用方合成
    /// （起始绝对角 + 这一拖的增量），再统一归一到 0~360。
    /// </summary>
    public static float RotationDeltaDegrees(Vector2 center, Vector2 startPoint, Vector2 currentPoint,
                                             bool gridSnap, bool noSnap, out bool snapped)
    {
        float a0 = MathF.Atan2(startPoint.Y - center.Y, startPoint.X - center.X);
        float a1 = MathF.Atan2(currentPoint.Y - center.Y, currentPoint.X - center.X);
        float delta = a1 - a0;

        snapped = false;
        if (gridSnap)
        {
            float step = RotationSnapDegrees * MathF.PI / 180f;
            delta = MathF.Round(delta / step) * step;
            snapped = true;
        }
        else if (!noSnap)
        {
            float step = RotationSoftSnapDegrees * MathF.PI / 180f;
            float nearest = MathF.Round(delta / step) * step;
            float tol = RotationSoftSnapToleranceDegrees * MathF.PI / 180f;
            if (MathF.Abs(delta - nearest) <= tol)
            {
                delta = nearest;
                snapped = true;
            }
        }
        return WrapSignedDegrees(delta * 180f / MathF.PI);
    }

    /// <summary>归一化到 (-180, 180]：数学上要符号的地方用它。</summary>
    public static float WrapSignedDegrees(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg <= -180f) deg += 360f;
        return deg;
    }

    /// <summary>
    /// 把角度归一化到 **[0, 360)**。给用户看的度数一律走这里。
    ///
    /// 为什么不是 (-180, 180]：投影上读数出现在手柄旁边，"‑43°" 里的减号很小、
    /// 又和手柄的连线挨在一起，老师和学生都会先看到"43"再愣一下。整套读数改成
    /// 0~360 之后没有负号，读法唯一（Office / Figma 的角度输入框也是这个值域）。
    ///
    /// 两条边界写在实现里：
    ///   · **360° 归一到 0°**——"转了一整圈"和"没转"在几何上是同一件事，
    ///     读两个数只会让人怀疑是不是转坏了；
    ///   · **不出现 -0°**（-0.2° 四舍五入到 -0 时写成 0）。
    /// </summary>
    public static float NormalizeDegrees(float deg)
    {
        deg %= 360f;
        if (deg < 0f) deg += 360f;
        if (deg >= 360f) deg -= 360f;
        return deg;
    }

    /// <summary>
    /// 一个变换矩阵的**绝对朝向**（度，[0,360)）。第一列向量（M11, M12）就是
    /// 对象局部 X 轴在画布上的方向，所以它的极角就是"这个对象转了多少度"。
    ///
    /// 单选时读数用它：用户拖旋转手柄时看到的是**对象现在多少度**
    /// （和 Office 的旋转读数一致），而不是"比按下那一刻多了多少度"。
    /// 多选时框是轴对齐的（见 SelectionFrame 的注释），这时它恒为 0，
    /// 读数会退回"这一拖转了多少"——恰好是那种情形下唯一有意义的数。
    /// </summary>
    public static float RotationOf(in Matrix3x2 m)
        => NormalizeDegrees(MathF.Atan2(m.M12, m.M11) * 180f / MathF.PI);

    /// <summary>度数标签上的字。四舍五入到整度，并且**不留 -0°**。</summary>
    public static string FormatDegrees(float deg)
    {
        int v = (int)MathF.Round(NormalizeDegrees(deg));
        v %= 360;
        if (v < 0) v += 360;                     // -0.4 四舍五入成 -0 → 0
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
            return Matrix3x2.CreateRotation(deg * MathF.PI / 180f, c);
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

    // =====================================================================
    //  操作条（复制 / 删除 / 左右翻转 / 上下翻转 / 旋转）
    //
    //  布局放在这里而不是渲染层：**命中判定和绘制必须用同一套尺寸**。
    //  分开写迟早会差几个像素，表现就是"看得见按钮却点不中"。放这里
    //  也让它可以被 --handletest 直接验。
    // =====================================================================

    /// <summary>
    /// 操作条按钮数。**没有"旋转 90°"**：旋转手柄 + Shift 的 15° 吸附已经覆盖了
    /// 任意角度（包括精确 90°），再放一个按钮是冗余，还占宽度、增加误点。
    /// </summary>
    public const int BarButtonCount = 4;
    public const float BarHeightLogical = 34f;
    public const float BarButtonWidthLogical = 46f;
    public const float BarPaddingLogical = 5f;
    public const float BarGapLogical = 2f;
    /// <summary>选中框下边到操作条的距离（逻辑像素）。</summary>
    public const float BarOffsetLogical = 14f;
    /// <summary>图标框边长（逻辑像素）。Fluent 图标自带内边距，所以比字形大一点。</summary>
    public const float BarIconBoxLogical = 22f;

    /// <summary>操作条在画布坐标里的矩形。</summary>
    public static RectF BarRect(in RectF sel, float dpi)
    {
        float btnW = BarButtonWidthLogical * dpi;
        float h = BarHeightLogical * dpi;
        float pad = BarPaddingLogical * dpi;
        float gap = BarGapLogical * dpi;
        float w = pad * 2 + BarButtonCount * btnW + (BarButtonCount - 1) * gap;

        float x = (sel.MinX + sel.MaxX) * 0.5f - w * 0.5f;
        float y = sel.MaxY + BarOffsetLogical * dpi;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>第 i 个按钮的矩形（i 从 0 起）。</summary>
    public static RectF BarButtonRect(int i, in RectF sel, float dpi)
    {
        var bar = BarRect(sel, dpi);
        float btnW = BarButtonWidthLogical * dpi;
        float pad = BarPaddingLogical * dpi;
        float gap = BarGapLogical * dpi;
        float x = bar.MinX + pad + i * (btnW + gap);
        return new RectF { MinX = x, MinY = bar.MinY, MaxX = x + btnW, MaxY = bar.MaxY };
    }

    /// <summary>点到哪个按钮上了。返回 -1 表示没点到操作条。</summary>
    public static int BarButtonAt(float x, float y, in RectF sel, float dpi)
    {
        var bar = BarRect(sel, dpi);
        if (x < bar.MinX || x > bar.MaxX || y < bar.MinY || y > bar.MaxY) return -1;
        for (int i = 0; i < BarButtonCount; i++)
        {
            var b = BarButtonRect(i, sel, dpi);
            if (x >= b.MinX && x <= b.MaxX) return i;
        }
        return -1;
    }
}
