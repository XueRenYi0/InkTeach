// [职责] 正切曲线的几何构建。
// [测试] --shapetest

using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

internal sealed partial class Stroke
{
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
        if (PreviewTangentHalfHeight > 0.5f)
            return MathF.Max(0.5f, PreviewTangentHalfHeight);
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

        // 空间坐标系：四个点 ＋ 箭头翅膀（翅膀会伸出点框）。
        if (kind == StrokeKind.SpaceCoordinate && local.Count >= 4)
        {
            Span<Vector2> pts = stackalloc Vector2[4];
            for (int i = 0; i < 4; i++) pts[i] = local[i];
            return SpaceAxisInkBoundsOf(pts, transform, Matrix3x2.Identity, width).Inflate(2f);
        }

        // 坐标系 / 数轴：定义点的外接 + 半笔宽。
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
    public bool KeepsWidth => Kind != StrokeKind.Freehand && Kind != StrokeKind.Image
                              && Kind != StrokeKind.TextBox && Kind != StrokeKind.Figure;

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

    /// <summary>
    /// 空间索引登记过的格子键。**移除时按这份快照走**——对象的包围盒
    /// 可能在 Insert 之后变过（文本框长高、改宽），按当前包围盒 Remove
    /// 会漏掉旧格子，留下"框选还能选中、再点删除没反应"的幽灵。
    /// </summary>
    internal long[] GridKeys;

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
        // 书写中留下的分块几何（见 Model.Stroke.LiveChunks.cs）：同样是缓存，必须一起放。
        DisposeLiveChunks();
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
            TextBox = TextBox?.Clone(),   // 题卡内容也是"这一条的样子"，复制要独立一份
            // 交互矢量图：JSON 深拷贝一份，复制/收藏后改参数不会串到原对象。
            Figure = Figure == null ? null : PlotSpecJson.Read(PlotSpecJson.Write(Figure)),
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

    /// <summary>
    /// 文本框**实际画出来的**世界矩形（含 Transform）。用 <see cref="TextBoxData.LayoutHeight"/>
    /// 而不是 Points/AABB 的高度：排版长高之后、包围盒还没来得及同步时，删除/重绘
    /// 必须按这张真实卡片擦，否则最后几行会留在屏幕上。
    /// </summary>
    internal RectF TextBoxWorldRect()
    {
        if (Kind != StrokeKind.TextBox || TextBox == null || Points.Count == 0)
            return RectF.Empty;
        var o = new Vector2(Points[0].X, Points[0].Y);
        var r = new RectF
        {
            MinX = o.X, MinY = o.Y,
            MaxX = o.X + TextBox.Width, MaxY = o.Y + TextBox.LayoutHeight,
        };
        if (Transform.IsIdentity) return r;
        var p0 = Vector2.Transform(new Vector2(r.MinX, r.MinY), Transform);
        var p1 = Vector2.Transform(new Vector2(r.MaxX, r.MinY), Transform);
        var p2 = Vector2.Transform(new Vector2(r.MinX, r.MaxY), Transform);
        var p3 = Vector2.Transform(new Vector2(r.MaxX, r.MaxY), Transform);
        return new RectF
        {
            MinX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X)),
            MinY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y)),
            MaxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X)),
            MaxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y)),
        };
    }

    /// <summary>把题卡的包围盒控制点同步成"左上角 + 宽高"。
    /// 宽度/折叠状态一变就调用；只改 `Points` 的两个角，**不动 Transform、不动内容**。
    /// </summary>
    internal void SyncTextBoxBounds()
    {
        if (Kind != StrokeKind.TextBox || TextBox == null) return;
        _ = TextBox.EnsureLayout();      // 折叠/改宽之后先把高度算出来，再同步两个角
        Vector2 o = Points.Count > 0 ? new Vector2(Points[0].X, Points[0].Y) : Vector2.Zero;
        SetTextBoxRect(o, TextBox.Width, TextBox.LayoutHeight);
    }

    /// <summary>
    /// 按"左上角 + 宽高"重设文本框的局部矩形（拖左边/上边改大小时原点也要跟着走）。
    /// 不动 Transform、不动内容；只负责包围盒和几何版本。
    /// </summary>
    internal void SetTextBoxRect(Vector2 origin, float width, float height)
    {
        if (Kind != StrokeKind.TextBox || TextBox == null) return;
        TextBox.Width = MathF.Max(1f, width);
        TextBox.Height = MathF.Max(1f, height);
        Points.Clear();
        Points.Add(new InkPoint { X = origin.X, Y = origin.Y, P = 1f, T = 0 });
        Points.Add(new InkPoint { X = origin.X + TextBox.Width, Y = origin.Y + TextBox.Height, P = 1f, T = 0 });
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

        if (Kind == StrokeKind.Sector && Points.Count >= 3)
        {
            Bounds = RectF.Empty;
            foreach (var p in SectorOutlinePoints()) Bounds.Add(p.X, p.Y);
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

        // 空间坐标系：四个点 ＋ 三个箭头的翅膀（y 斜着，翅膀会伸到点框外面）。
        if (Kind == StrokeKind.SpaceCoordinate && Points.Count >= 4)
        {
            Span<Vector2> sp = stackalloc Vector2[4];
            for (int i = 0; i < 4; i++) sp[i] = new Vector2(Points[i].X, Points[i].Y);
            Bounds = SpaceAxisInkBoundsOf(sp, Matrix3x2.Identity, Matrix3x2.Identity, Width);
            return;
        }

        if (Kind == StrokeKind.TextBox && TextBox != null && Points.Count >= 1)
        {
            var o = new Vector2(Points[0].X, Points[0].Y);
            Bounds.Add(o.X, o.Y);
            Bounds.Add(o.X + TextBox.Width, o.Y + TextBox.LayoutHeight);
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
    public RectF PaddedBounds
    {
        get
        {
            // 文本框按**实际画出来的卡片**（含排版后的高度）算：Points 的包围盒
            // 可能在自动长高后还没同步，按它做脏区/命中会漏掉最后几行。
            if (Kind == StrokeKind.TextBox && TextBox != null)
            {
                var r = TextBoxWorldRect();
                if (!r.IsEmpty) return r.Inflate(2f);
            }
            return IsParametricShape
                ? WorldInkBounds.Inflate(2f)
                : WorldBounds.Inflate(MaxHalfWidth + 2f);
        }
    }

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

            if (Kind == StrokeKind.Sector && Points.Count >= 3)
            {
                float hw0 = Width * 0.5f;
                var rr = RectF.Empty;
                foreach (var p in SectorOutlinePoints()) rr.Add(p.X, p.Y);
                rr.MinX -= hw0; rr.MinY -= hw0; rr.MaxX += hw0; rr.MaxY += hw0;
                _inkBounds = rr;
                _maxHalfWidth = hw0;
                _inkBoundsRevision = Revision;
                return rr;
            }

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

            // 空间坐标系：四个点 ＋ 三个箭头的翅膀（翅膀会伸出点框，见那个函数的注释）。
            if (Kind == StrokeKind.SpaceCoordinate && Points.Count >= 4)
            {
                Span<Vector2> sp = stackalloc Vector2[4];
                for (int i = 0; i < 4; i++) sp[i] = new Vector2(Points[i].X, Points[i].Y);
                var rr = SpaceAxisInkBoundsOf(sp, Transform, Matrix3x2.Identity, Width);
                _inkBounds = rr;
                _maxHalfWidth = Width * 0.5f;
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
                or StrokeKind.Star or StrokeKind.Trapezoid or StrokeKind.Sector
                or StrokeKind.SpaceCoordinate
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
            if (Kind == StrokeKind.SpaceCoordinate && Points.Count >= 4)
            {
                Span<Vector2> sp = stackalloc Vector2[4];
                for (int i = 0; i < 4; i++) sp[i] = new Vector2(Points[i].X, Points[i].Y);
                return SpaceAxisInkBoundsOf(sp, Transform, Matrix3x2.Identity, Width);
            }

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
        // 坐标系 / 数轴 / 空间坐标系：和静止态同一条式子（定义点的外接 + 半笔宽），口径不能差一分。
        if (Kind is StrokeKind.Coordinate or StrokeKind.NumberLine && local.Count >= 2)
        {
            int n = Math.Min(local.Count, 3);
            Span<Vector2> pts = stackalloc Vector2[3];
            for (int i = 0; i < n; i++) pts[i] = local[i];
            return AxisInkBoundsOf(pts[..n], Transform, extra, Width);
        }
        if (Kind == StrokeKind.SpaceCoordinate && local.Count >= 4)
        {
            Span<Vector2> pts = stackalloc Vector2[4];
            for (int i = 0; i < 4; i++) pts[i] = local[i];
            return SpaceAxisInkBoundsOf(pts, Transform, extra, Width);
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
        // AI 题卡是"填满的一块矩形"（和图像同义）：点在卡片里就算命中。
        // 不走几何：卡片没有描边中心线，硬按两点的线去算会出现"看得见却点不中"。
        if (Kind == StrokeKind.TextBox && TextBox != null && Points.Count >= 1)
        {
            var b = TextBoxWorldRect();
            return canvasX >= b.MinX - tolerance && canvasX <= b.MaxX + tolerance
                && canvasY >= b.MinY - tolerance && canvasY <= b.MaxY + tolerance;
        }

        // 交互矢量图：整张卡片都算命中（和 Image 一样），不像几何图形只认描边。
        if (Kind == StrokeKind.Figure && Points.Count >= 2)
        {
            var b = WorldBounds;
            return canvasX >= b.MinX - tolerance && canvasX <= b.MaxX + tolerance
                && canvasY >= b.MinY - tolerance && canvasY <= b.MaxY + tolerance;
        }

        // 字形填充对象：点在**填色区域**里就算命中（even-odd，孔里不算）。
        if (Kind == StrokeKind.Glyph && Points.Count >= 3)
        {
            bool inside = false;
            int i = 0;
            while (i < Points.Count)
            {
                int start = i;
                int end = i + 1;
                while (end < Points.Count && Points[end].P >= 0) end++;
                for (int k = start; k < end; k++)
                {
                    var a = Points[k];
                    var b = Points[k + 1 < end ? k + 1 : start];
                    if ((a.Y > canvasY) != (b.Y > canvasY) &&
                        canvasX < (b.X - a.X) * (canvasY - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
                i = end;
            }
            return inside;
        }

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

    /// <summary>微移笔画（总路径 ≤ <see cref="TinyDotPx"/>）也算"点"。
    /// 2026-10-08（用户 B 案）：轻点时笔尖总会动 1~3px，按普通笔迹描就会描出小折钩、
    /// 形状各异的痕迹；这里统一当"点"画。半径取**整笔最大压力**（起手那几点压力最低，
    /// 取最大才符合"按多重、点多圆"），再压 <see cref="DotFactorCap"/> 上限。
    /// **live 与落地走同一判据** → 松手不再变形、点完起写不再换个样子。</summary>
    public bool RendersAsDot => IsSinglePoint
        || (Kind == StrokeKind.Freehand && Points.Count > 1 && PathLengthUpTo(TinyDotPx) <= TinyDotPx);

    /// <summary>"微移算点"的路径上限（画布像素）。</summary>
    public const float TinyDotPx = 4f;

    /// <summary>圆点宽度的上限（倍）：0.8 = 不再拱成大圆，但仍保留"压力越大点越大"到近满压。</summary>
    public const float DotFactorCap = 0.8f;

    /// <summary>圆点半径（"点/微移笔画"共用；渲染与回放都调它，保证一处口径）。</summary>
    public float DotRadius()
    {
        if (HasPressure && PressureWidth.Enabled)
        {
            float p = 0f;
            foreach (var q in Points) if (q.P > p) p = q.P;
            return MathF.Max(1f, Width * 0.5f * MathF.Min(PressureWidth.Factor(p), DotFactorCap));
        }
        return MathF.Max(1f, Width * 0.5f);      // 无压感（鼠标/触摸）：档位宽度的一半
    }

    /// <summary>从首点起的累计路径长度；一超过 <paramref name="limit"/> 提前返回（长笔不白算）。</summary>
    private float PathLengthUpTo(float limit)
    {
        float len = 0f;
        for (int i = 1; i < Points.Count; i++)
        {
            len += Vector2.Distance(new Vector2(Points[i - 1].X, Points[i - 1].Y),
                                    new Vector2(Points[i].X, Points[i].Y));
            if (len > limit) return len;
        }
        return len;
    }

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

            case StrokeKind.Star:
            {
                if (Points.Count < 3) break;
                foreach (var p in Points) list.Add(new Vector2(p.X, p.Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Trapezoid:
            {
                if (Points.Count < 4) break;
                for (int i = 0; i < 4; i++)
                    list.Add(new Vector2(Points[i].X, Points[i].Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Sector:
            {
                list.AddRange(SectorOutlinePoints());
                break;
            }

            // ---- 空间坐标系：三根从原点出发的轴（抬笔分段，避免连出假线）----
            case StrokeKind.SpaceCoordinate:
            {
                if (Points.Count < 4) break;
                var o = new Vector2(Points[0].X, Points[0].Y);
                list.Add(o); list.Add(new Vector2(Points[1].X, Points[1].Y));
                list.Add(OutlineBreak);
                list.Add(o); list.Add(new Vector2(Points[2].X, Points[2].Y));
                list.Add(OutlineBreak);
                list.Add(o); list.Add(new Vector2(Points[3].X, Points[3].Y));
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
        // AI 题卡由 Overlay 用 DirectWrite 画，没有中心线几何。
        if (Kind == StrokeKind.TextBox) return null;

        // 抬笔：书写中的分段缓存到此为止（下一行起按整条原始点重建，笔锋/曲线化不受影响）。
        if (!RawWhileLive && _liveChunkKind != 0)
        {
            if (Geometry != null) { Geometry.Dispose(); Geometry = null; LiveGeometries--; }
            DisposeLiveChunks();
        }

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
            StrokeKind.Star => BuildStar(factory),
            StrokeKind.Trapezoid => BuildTrapezoid(factory),
            StrokeKind.Sector => BuildSector(factory),
            StrokeKind.Coordinate => BuildAxes(factory),
            StrokeKind.NumberLine => BuildAxes(factory),
            StrokeKind.SpaceCoordinate => BuildAxes(factory),
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
            // 单点与"微移笔画"例外——那是一个圆点，几何直接建成圆（渲染那边会填充它）。
            _ => RendersAsDot ? BuildDot(factory) : BuildCenterline(factory),
        };
        if (Geometry != null) LiveGeometries++;
        _builtRevision = Revision;
        _builtSmoothVer = StrokeSmoothing.Version;
        _builtRawLive = RawWhileLive;
        _builtInkModelVer = StrokeMotion.Version;
        return Geometry;
    }

    // ======================================================================
    //  书写中：超长单笔的分段增量几何缓存（2026-11-25）
    //
    //  为什么：正在写的那一笔每帧 Revision +1，几何缓存必然失效，于是
    //  BuildCenterlineCore 每帧都从第 0 个点重头开 sink、加几百上千段；而其中
    //  **前段一点没变**，变的只有笔尖附近那一小截。
    //
    //  做法（严格只影响"书写中的显示"，抬笔后完全走原路）：
    //    · 前段按固定步长烘成独立 path geometry 留着复用，每帧只重建尾部一截；
    //    · 用 ID2D1GeometryGroup 把"缓存块 + 尾段"装成**一条几何**再 DrawGeometry：
    //      一次混合，块与块衔接处的圆头端帽不会叠出深色/深透明度接缝（荧光笔也一样）；
    //    · 贝塞尔通道只缓存离笔尖 LiveStableMargin 段以外的输出（平滑器的角点/抽稀
    //      窗口最多看 ±3 点），尾段永远现算 → 笔尖跟手性不变；
    //    · 抬笔 / Release 时立即释放全部分块，内容层烘焙仍按原始点一次重建整笔。
    //
    //  红线：**绝不改 Points**、绝不改 StrokeTaper / StrokeSmoothing 的算法与参数——
    //  这里只是把"同一批点/同一批曲线段"分几次建几何。
    //  `--smoothmacropx`（像素宏观窗）打开时不启用：那个判据按全条平均点距取窗口，
    //  前段的角点可能随总长变化，分块缓存不再等价于整条重建。
    // ======================================================================

    /// <summary>一块缓存覆盖的段数（折线 = 段数，贝塞尔 = 输出曲线段数）。</summary>
    internal const int LiveChunkSegments = 128;

    /// <summary>启用分段缓存的最小采样点数（短笔重头建也不慢，不值得多一层）。</summary>
    private const int LiveChunkMinPoints = 160;

    /// <summary>离"当前末端"至少这么多段才认为稳定（平滑器的角点/抽稀窗口最多看 ±3 点）。</summary>
    private const int LiveStableMargin = 8;

    /// <summary>缓存种类：0 无、1 折线、2 平滑贝塞尔。</summary>
    private int _liveChunkKind;
    private ID2D1PathGeometry[] _liveChunks;
    private int _liveChunkCount;
    /// <summary>折线 = 已烘到的点下标；贝塞尔 = 已烘的段数。下一块的起点。</summary>
    private int _liveChunkCursor;
    /// <summary>建缓存时的点数：点被整批换短了（撤销/重设）就必须作废。</summary>
    private int _liveChunkPoints;
    private int _liveChunkSmoothVer;

    private readonly List<ID2D1Geometry> _liveGroupScratch = new();

    /// <summary>
    /// 书写中尝试走"稳定前段缓存 + 尾段重建"。返回 null = 不适用/还没攒够一块，
    /// 调用方照原路整条重建（结果与没有这个优化时一致）。
    /// </summary>
    private ID2D1Geometry TryBuildLiveChunked(ID2D1Factory1 factory, float maxParam)
    {
        if (!RawWhileLive || Erased.Count > 0 || RendersAsDot) return null;
        if (Points.Count < LiveChunkMinPoints) return null;
        // 虚线/点线不能分块：每段几何会重新起一个 dash 相位，接缝处的短划会错位。
        if (Dash != StrokeDash.Solid) return null;
        // 回放前缀/取景裁剪：每帧末端都在动，分块缓存没有意义。
        if (maxParam < Points.Count - 1 - 1e-4f) return null;

        if (StrokeMotion.Build(this))
        {
            // 运动模型输出：曲线化开着走贝塞尔分块；关着是"模型折线"，保持原路（少见配置）。
            if (!StrokeSmoothing.Enabled) return null;
            if (StrokeSmoothing.CornerMacroPx > 0f) return null;
            int m = PrepareSmoothedModeledRun();
            if (m < LiveChunkSegments + LiveStableMargin) return null;
            return BuildBezierChunked(factory, m);
        }

        // 原始折线通道（`--motion raw/catmull`，或点数/几何条件让模型不生效）。
        return BuildPolylineChunked(factory);
    }

    private ID2D1Geometry BuildPolylineChunked(ID2D1Factory1 factory)
    {
        EnsureLiveChunkState(kind: 1);
        int last = Points.Count - 1;

        while (_liveChunkCursor + LiveChunkSegments <= last)
        {
            int from = _liveChunkCursor, to = from + LiveChunkSegments;
            AddLiveChunk(BuildPolylineRange(factory, from, to));
            _liveChunkCursor = to;
        }
        if (_liveChunkCursor == 0) return null;     // 还没攒够一块：交回原路

        var tail = _liveChunkCursor < last ? BuildPolylineRange(factory, _liveChunkCursor, last) : null;
        GeomTrace(this, "A", "原始", false, "live-chunk");
        return MakeLiveGroup(factory, tail);
    }

    private ID2D1PathGeometry BuildPolylineRange(ID2D1Factory1 factory, int from, int to)
    {
        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(LivePoint(from), FigureBegin.Hollow);
            for (int i = from + 1; i <= to; i++) sink.AddLine(LivePoint(i));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        return geo;
    }

    private Vector2 LivePoint(int i) => new(Points[i].X, Points[i].Y);

    private ID2D1Geometry BuildBezierChunked(ID2D1Factory1 factory, int segmentCount)
    {
        EnsureLiveChunkState(kind: 2);
        var segs = StrokeSmoothing.Out;
        // 尾段（含平滑器还没看全邻居的那几段）永远现算，保证笔尖跟手、曲线不断。
        int stable = segmentCount - LiveStableMargin;

        while (_liveChunkCursor + LiveChunkSegments <= stable)
        {
            int from = _liveChunkCursor, to = from + LiveChunkSegments;
            AddLiveChunk(BuildBezierRange(factory, segs, from, to));
            _liveChunkCursor = to;
        }
        if (_liveChunkCursor == 0) return null;     // 还没攒够一块：交回原路

        var tail = BuildBezierRange(factory, segs, _liveChunkCursor, segmentCount);
        GeomTrace(this, "A", "模型", true, "live-chunk");
        return MakeLiveGroup(factory, tail);
    }

    /// <summary>段区间 [from, toExclusive)：fig. 起点 = 第 from 段的 P0（严格落在采样点上）。</summary>
    private static ID2D1PathGeometry BuildBezierRange(ID2D1Factory1 factory,
        StrokeSmoothing.Seg[] segs, int from, int toExclusive)
    {
        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(segs[from].P0, FigureBegin.Hollow);
            for (int k = from; k < toExclusive; k++)
                sink.AddBezier(new BezierSegment(segs[k].C1, segs[k].C2, segs[k].P1));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        return geo;
    }

    private void EnsureLiveChunkState(int kind)
    {
        if (_liveChunkKind != kind || _liveChunkPoints > Points.Count
            || _liveChunkSmoothVer != StrokeSmoothing.Version)
            DisposeLiveChunks();

        _liveChunkKind = kind;
        _liveChunkPoints = Points.Count;
        _liveChunkSmoothVer = StrokeSmoothing.Version;
    }

    private void AddLiveChunk(ID2D1PathGeometry geo)
    {
        if (_liveChunks == null || _liveChunkCount == _liveChunks.Length)
            Array.Resize(ref _liveChunks, Math.Max(4, (_liveChunks?.Length ?? 0) * 2));
        _liveChunks[_liveChunkCount++] = geo;
        LiveGeometries++;
    }

    /// <summary>
    /// 把缓存块 + 尾段装成一条几何（一次 DrawGeometry → 一次混合，接缝不叠色）。
    /// 组持有子几何的引用，尾段的包装器在这里就可以放掉。
    /// </summary>
    private ID2D1Geometry MakeLiveGroup(ID2D1Factory1 factory, ID2D1PathGeometry tail)
    {
        _liveGroupScratch.Clear();
        for (int i = 0; i < _liveChunkCount; i++) _liveGroupScratch.Add(_liveChunks[i]);
        if (tail != null) _liveGroupScratch.Add(tail);

        var arr = _liveGroupScratch.ToArray();
        _liveGroupScratch.Clear();
        var group = factory.CreateGeometryGroup(
            Vortice.Direct2D1.FillMode.Winding, arr, (uint)arr.Length);
        // 组本身由 BuildGeometry 统一计入 LiveGeometries（那里对所有 Geometry 都 +1）。
        tail?.Dispose();        // 组自己持有引用
        return group;
    }

    /// <summary>抬笔/释放：所有分块几何立即放掉（下一帧按整条原始点重建，笔锋/曲线化不受影响）。</summary>
    private void DisposeLiveChunks()
    {
        if (_liveChunks != null)
        {
            for (int i = 0; i < _liveChunkCount; i++)
            {
                _liveChunks[i]?.Dispose();
                LiveGeometries--;
            }
            _liveChunks = null;
        }
        _liveChunkCount = 0;
        _liveChunkCursor = 0;
        _liveChunkKind = 0;
        _liveChunkPoints = 0;
        _liveChunkSmoothVer = 0;
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

    private ID2D1PathGeometry BuildStar(ID2D1Factory1 factory)
    {
        if (Points.Count < 3) return BuildLine(factory);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(Points[0].X, Points[0].Y), FigureBegin.Hollow);
        for (int i = 1; i < Points.Count; i++)
            sink.AddLine(new Vector2(Points[i].X, Points[i].Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private ID2D1PathGeometry BuildTrapezoid(ID2D1Factory1 factory)
    {
        if (Points.Count < 4) return BuildLine(factory);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(Points[0].X, Points[0].Y), FigureBegin.Hollow);
        for (int i = 1; i < 4; i++)
            sink.AddLine(new Vector2(Points[i].X, Points[i].Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private ID2D1PathGeometry BuildSector(ID2D1Factory1 factory)
    {
        var pts = SectorOutlinePoints();
        if (pts.Count < 2) return BuildLine(factory);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(pts[0], FigureBegin.Hollow);
        for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
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
        float head = PreviewArrowHeadLen > 0.5f
            ? PreviewArrowHeadLen : AxisArrowHeadLen(Width);

        // ---- 空间直角坐标系：原点 → 三个正半轴端点，各带一个箭头 ----
        if (Kind == StrokeKind.SpaceCoordinate)
        {
            if (Points.Count < 4) return list;
            var so = new Vector2(Points[0].X, Points[0].Y);
            void Axis(Vector2 end, Vector2 dir)
            {
                list.Add(new InkPiece(new List<Vector2> { so, end }, false));
                list.Add(new InkPiece(AxisArrowPoints(end, dir, head), false));
            }
            var xEnd = new Vector2(Points[1].X, Points[1].Y);
            var xDir = xEnd - so;
            Axis(xEnd, xDir.LengthSquared() > 1e-6f
                ? Vector2.Normalize(xDir) : new Vector2(1f, 0f));
            var yEnd = new Vector2(Points[2].X, Points[2].Y);
            var yDir = yEnd - so;
            Axis(yEnd, yDir.LengthSquared() > 1e-6f
                ? Vector2.Normalize(yDir) : SpaceAxisYDirection);
            Axis(new Vector2(Points[3].X, Points[3].Y), new Vector2(0f, -1f));
            return list;
        }

        var (minX, minY, maxX, maxY) = AxisFrameLocal();

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
        if (PreviewSmooth) n = Math.Max(n, 72);
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
        if (!PreviewFocusDots)
        {
            AddDot(sink, f1);
            AddDot(sink, f2);
        }

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

}
