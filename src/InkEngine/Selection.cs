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
    /// <summary>手柄的视觉直径（逻辑像素）。</summary>
    public const float VisualSizeLogical = 7f;

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
    /// 把"拖着某个手柄从 <paramref name="startPoint"/> 到 <paramref name="currentPoint"/>"
    /// 换算成一个变换矩阵（作用在**选区开始拖动时**的坐标上）。
    ///
    /// <paramref name="uniform"/>（Shift）：等比缩放，取两个方向里变化大的那个。
    /// <paramref name="snapAngle"/>（Shift 用于旋转）：对齐到 15 度增量。
    /// </summary>
    public static Matrix3x2 DragMatrix(SelHandle handle, in RectF startBounds,
                                       Vector2 startPoint, Vector2 currentPoint,
                                       float dpiScale, bool uniform, bool snapAngle)
    {
        if (handle == SelHandle.None) return Matrix3x2.Identity;

        if (handle == SelHandle.Rotate)
        {
            var c = new Vector2((startBounds.MinX + startBounds.MaxX) * 0.5f,
                                (startBounds.MinY + startBounds.MaxY) * 0.5f);
            float a0 = MathF.Atan2(startPoint.Y - c.Y, startPoint.X - c.X);
            float a1 = MathF.Atan2(currentPoint.Y - c.Y, currentPoint.X - c.X);
            float delta = a1 - a0;
            if (snapAngle)
            {
                float step = RotationSnapDegrees * MathF.PI / 180f;
                delta = MathF.Round(delta / step) * step;
            }
            return Matrix3x2.CreateRotation(delta, c);
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

        if (uniform)
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
}
