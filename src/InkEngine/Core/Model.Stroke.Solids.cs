// [职责] 立体图形（圆柱/圆锥/球/棱柱等）的几何构建。
// [测试] --shapetest

using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

internal sealed partial class Stroke
{
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
    private ID2D1Geometry BuildCenterline(ID2D1Factory1 factory)
        => BuildCenterlineCore(factory, float.MaxValue);

    /// <summary>
    /// 回放用：只画**前 maxParam**（点序号，0..Points.Count-1，可带小数）那一段中心线。
    /// **不进几何缓存**（每帧都在变，缓存只会不停重建）——调用方画完负责 Dispose。
    /// </summary>
    internal ID2D1Geometry BuildCenterlinePrefix(ID2D1Factory1 factory, float maxParam)
        => BuildCenterlineCore(factory, maxParam);

    private ID2D1Geometry BuildCenterlineCore(ID2D1Factory1 factory, float maxParam)
    {
        // 书写中的超长单笔：稳定前段复用缓存几何，只重建笔尖附近一截。
        // 返回 null = 条件不满足，下面照原路一次性整条重建。
        var live = TryBuildLiveChunked(factory, maxParam);
        if (live != null) return live;

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
                // `--nosmooth` 时连模型输出也不上曲线——这样它才是一个真正的
                // "折线对照组"（否则模型路径照样出曲线，开关等于没有）。
                bool drew = StrokeSmoothing.Enabled && AppendSmoothedModeledRun(sink);
                GeomTrace(this, "A", "模型", drew);
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
                }
            }
            else
            {
                // 曲线化（`--smooth`）：把这一段 run 的采样点喂给曲线器，输出一串三次贝塞尔。
                // **点还是原来那些点**——曲线严格过每一个采样点，直角由角点保护保住；
                // 不生效时（开关关着 / 段数不够）原样退回下面的折线路径。
                bool smoothed = StrokeSmoothing.Enabled && !RawWhileLive && AppendSmoothedRun(sink, a, b);
                GeomTrace(this, "A", "原始", smoothed,
                          $"clipped={clipped} erased={Erased.Count} model={StrokeMotion.Build(this)} live={RawWhileLive} enabled={StrokeSmoothing.Enabled}");
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
    /// `--geomtrace` 诊断：把"这一笔的几何走了哪条路"打一行出来（每笔一次）。
    /// 关掉时零开销（一个 bool 判断）。
    /// </summary>
    internal static void GeomTrace(Stroke s, string channel, string srcKind, bool curved, string why = "")
    {
        if (!global::InkEngine.InkEngine.GeomTraceOn) return;
        // **一笔会建两次几何**：正在写的时候一次（RawWhileLive=true）、落笔之后一次。
        // 而且**两条上屏通道可能都建**（A 总是先建、B 随后试），所以按
        // (阶段 × 通道) 去重，四个组合各打一行。少一个组合就少一条线索。
        int bit = (s.RawWhileLive ? 0 : 2) + (channel == "B" ? 1 : 0);
        if ((s.GeomTraceMask & (1 << bit)) != 0) return;
        s.GeomTraceMask |= 1 << bit;
        string stage = s.RawWhileLive ? "活笔" : "落笔后";
        float span = 0f;
        for (int i = 1; i < s.Points.Count; i++)
            span += Vector2.Distance(new Vector2(s.Points[i - 1].X, s.Points[i - 1].Y),
                                     new Vector2(s.Points[i].X, s.Points[i].Y));
        float spacing = s.Points.Count > 1 ? span / (s.Points.Count - 1) : 0f;
        string line = $"  [几何路径] {stage} {channel}-{srcKind}+{(curved ? "曲线" : "折线")}"
                      + $" | 点 {s.Points.Count} 均距 {spacing:F1}px 总长 {span:F0}px"
                      + $" | 压感={s.HasPressure} 活笔={s.RawWhileLive} 曲线开关={StrokeSmoothing.Enabled}"
                      + (why.Length > 0 ? " | " + why : "");
        Console.WriteLine(line);
        // 同时落一份文件：真机上只要把文件发过来，不用截控制台。
        try
        {
            if (s_traceWriter == null)
            {
                string p = Path.Combine(Path.GetTempPath(), "inkteach-geomtrace.txt");
                s_traceWriter = new StreamWriter(p, append: false) { AutoFlush = true };
                Console.WriteLine($"  [几何路径] 轨迹文件：{p}");
            }
            s_traceWriter.WriteLine(line);
        }
        catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }
    }

    private static StreamWriter s_traceWriter;

    /// <summary>
    /// mean2：把建模输出喂进过点曲线，直接写成三次贝塞尔。
    /// 返回 false = 段数不够，调用方退回直线折线。
    /// </summary>
    private static bool AppendSmoothedModeledRun(ID2D1GeometrySink sink)
    {
        int m = PrepareSmoothedModeledRun();
        if (m <= 0) return false;

        var segs = StrokeSmoothing.Out;
        sink.BeginFigure(segs[0].P0, FigureBegin.Hollow);
        for (int k = 0; k < m; k++)
            sink.AddBezier(new BezierSegment(segs[k].C1, segs[k].C2, segs[k].P1));
        return true;
    }

    /// <summary>
    /// 把运动模型的输出喂进过点曲线，**只算段不建几何**（返回段数；0 = 段数不够）。
    /// 书写中启用分段缓存时也要先算这一遍——两种用法共用同一份结果，
    /// 保证缓存段 = 原路会画的段，一个控制点都不差。
    /// </summary>
    private static int PrepareSmoothedModeledRun()
    {
        StrokeSmoothing.Begin();
        int n = StrokeMotion.Count;
        for (int i = 0; i < n; i++)
        {
            var p = StrokeMotion.At(i);
            StrokeSmoothing.Add(p.X, p.Y, p.Z);
        }
        return StrokeSmoothing.Finish();
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
        float rad = DotRadius();
        return factory.CreateEllipseGeometry(
            new Ellipse(new Vector2(Points[0].X, Points[0].Y), rad, rad));
    }
}
