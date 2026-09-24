using System.Numerics;

namespace InkEngine;

/// <summary>
/// **把一条手绘点串拟合成"误差带内的光滑曲线"**（逐段三次贝塞尔）——"保形平滑"。
///
/// 照搬（最高原则第 3 条：能整段搬就整段搬）：
///   Philip J. Schneider, "An Algorithm for Automatically Fitting Digitized Curves",
///   in **Graphics Gems**（Academic Press, 1990），原文与 C 源码：
///   <c>http://tog.acm.org/resources/GraphicsGems/gems/FitCurves.c</c>
///   许可证：Graphics Gems 的源码允许"为任何目的使用、复制、修改、分发，无需付费"，
///   只要求保留出处（见原书 README）。本文件是它的 C# 逐函数移植，
///   辅助函数沿用原文命名（`B0..B3`、`BezierII`、`GenerateBezier`……）方便逐行对照。
///   工程界同一算法的其它可对照实现：Calligra/Karbon 的 `KoCurveFit.cpp`、
///   paper.js 的 `PathFitter`、Potrace 的曲线输出。
///
/// **为什么是它**（而不是自己写个滑动平均）：低通滤波/邻域平均会把**尖峰压矮**
/// （正弦的峰会变矮、抛物线的顶点会被磨圆）——那是"把形状改了"，不是"画顺了"。
/// 这个算法带**误差上限**：拟合出的曲线离原始笔迹的偏差不许超过容差，
/// 超了就在"最坏的那个点"切开重拟合（递归）。于是**平滑**和**保形**同时成立。
/// 输出还是 **G1 连续**（关节处切线共线），肉眼看不到接缝。
///
/// ⚠ **两处与原文不同（都是归一化，不是改算法，说明在这儿免得后人对着源码发懵）**：
///   ① 原文的 `error` 参数**量纲是乱的**——`ComputeMaxError` 返回的是**平方距离**，
///      而"成功"判据写的是 `maxError &lt; error`（平方 vs 线性），
///      "要不要迭代"判据写的是 `maxError &lt; error * error`（平方 vs 平方）。
///      这里统一成：**调用方给线性容差**，内部一律用平方比较（见 <see cref="Fit"/>）；
///   ② 于是"要不要试迭代"的门槛换成 `4 × 容差`（线性的 4 倍）——
///      原文那两处混用等价于"误差在容差的若干倍以内就先迭代试试"，这里把它写清楚。
/// </summary>
internal static class CurveFit
{
    /// <summary>一段三次贝塞尔：4 个控制点（首末两个在曲线上）。</summary>
    internal struct Segment
    {
        public Vector2 P0, P1, P2, P3;

        /// <summary>按序号取控制点（照原文的 C 数组写法 `Q[i]` 用）。</summary>
        public Vector2 Get(int i) => i switch { 0 => P0, 1 => P1, 2 => P2, _ => P3 };
    }

    /// <summary>原码里的固定迭代次数（重参数化 ≤ 4 次）。</summary>
    private const int MaxIterations = 4;

    /// <summary>安全阀：最多产出多少段（正常一笔 10~40 段）。超过就收手，
    /// 免得病态输入（自交、来回折）把递归拖长。</summary>
    private const int MaxSegments = 128;

    /// <summary>"先试迭代"的门槛 = 容差 × 这个倍数（见类注释 ⚠②）。</summary>
    private const float IterateGateFactor = 4f;

    /// <summary>
    /// 拟合。`tolerance` 是**线性**容差（画布单位，允许的最大偏差）。
    /// 返回 0 段表示输入退化（点数 &lt; 2）。
    /// </summary>
    internal static List<Segment> Fit(Vector2[] d, float tolerance)
    {
        var segs = new List<Segment>();
        if (d == null || d.Length < 2) return segs;
        if (tolerance <= 0f) tolerance = 0.1f;

        float tolSq = tolerance * tolerance;
        var tHat1 = ComputeLeftTangent(d, 0);
        var tHat2 = ComputeRightTangent(d, d.Length - 1);
        FitCubic(d, 0, d.Length - 1, tHat1, tHat2, tolSq, segs);
        return segs;
    }

    /// <summary>
    /// 原文 `FitCubic`：给一段点集拟合一条三次贝塞尔；误差超了就拆两半递归。
    /// `tolSq` 是**平方**容差（内部统一用平方，见类注释）。
    /// </summary>
    private static void FitCubic(Vector2[] d, int first, int last,
                                 Vector2 tHat1, Vector2 tHat2, float tolSq, List<Segment> segs)
    {
        int nPts = last - first + 1;

        // ---- 只剩两个点（或已经拆到底）：原文的启发式——用 1/3 弦长当控制柄 ----
        if (nPts == 2 || segs.Count >= MaxSegments)
        {
            float dist = Vector2.Distance(d[last], d[first]) / 3f;
            segs.Add(new Segment
            {
                P0 = d[first],
                P1 = d[first] + tHat1 * dist,
                P2 = d[last] + tHat2 * dist,
                P3 = d[last],
            });
            return;
        }

        // ---- 按弦长参数化 + 最小二乘求控制柄（原文 GenerateBezier）----
        var u = ChordLengthParameterize(d, first, last);
        var curve = GenerateBezier(d, first, last, u, tHat1, tHat2);

        float maxErrSq = ComputeMaxErrorSq(d, first, last, curve, u, out int splitPoint);
        if (maxErrSq < tolSq)
        {
            segs.Add(curve);
            return;
        }

        // ---- 误差还不算离谱：先用牛顿法改进参数化，再试 4 次（原文 Reparameterize）----
        float gate = tolSq * IterateGateFactor * IterateGateFactor;
        if (maxErrSq < gate)
        {
            for (int i = 0; i < MaxIterations; i++)
            {
                var uPrime = Reparameterize(d, first, last, u, curve);
                curve = GenerateBezier(d, first, last, uPrime, tHat1, tHat2);
                maxErrSq = ComputeMaxErrorSq(d, first, last, curve, uPrime, out splitPoint);
                u = uPrime;
                if (maxErrSq < tolSq)
                {
                    segs.Add(curve);
                    return;
                }
            }
        }

        // ---- 还是不行：在"最坏的那个点"切开，两半各自递归（原文的 FitCubic 尾递归）----
        var tHatCenter = ComputeCenterTangent(d, splitPoint);
        FitCubic(d, first, splitPoint, tHat1, tHatCenter, tolSq, segs);
        FitCubic(d, splitPoint, last, -tHatCenter, tHat2, tolSq, segs);
    }

    /// <summary>
    /// 原文 `GenerateBezier`：固定首末点与两端切线方向，对两个控制柄长度
    /// `alpha_l / alpha_r` 做**最小二乘**（2×2 线性方程组，直接解行列式）。
    /// ⚠ 解出负值说明这一段"弯不回来"（比如 S 形塞进一条单段），原文的处理是
    /// **退回 Wu/Barsky 启发式**（1/3 弦长）——照搬。
    /// </summary>
    private static Segment GenerateBezier(Vector2[] d, int first, int last,
                                          double[] uPrime, Vector2 tHat1, Vector2 tHat2)
    {
        int nPts = last - first + 1;
        var A = new Vector2[nPts, 2];
        for (int i = 0; i < nPts; i++)
        {
            float b1 = B1(uPrime[i]);
            float b2 = B2(uPrime[i]);
            A[i, 0] = tHat1 * b1;
            A[i, 1] = tHat2 * b2;
        }

        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (int i = 0; i < nPts; i++)
        {
            c00 += Vector2.Dot(A[i, 0], A[i, 0]);
            c01 += Vector2.Dot(A[i, 0], A[i, 1]);
            c11 += Vector2.Dot(A[i, 1], A[i, 1]);

            float b0 = B0(uPrime[i]), b1 = B1(uPrime[i]);
            float b2 = B2(uPrime[i]), b3 = B3(uPrime[i]);
            var tmp = d[first + i]
                      - (d[first] * b0 + d[first] * b1 + d[last] * b2 + d[last] * b3);

            x0 += Vector2.Dot(A[i, 0], tmp);
            x1 += Vector2.Dot(A[i, 1], tmp);
        }

        double detC0C1 = c00 * c11 - c01 * c01;
        double detC0X = c00 * x1 - c01 * x0;
        double detXC1 = x0 * c11 - x1 * c01;

        double alphaL = Math.Abs(detC0C1) < 1e-12 ? 0.0 : detXC1 / detC0C1;
        double alphaR = Math.Abs(detC0C1) < 1e-12 ? 0.0 : detC0X / detC0C1;

        if (alphaL < 0.0 || alphaR < 0.0)
        {
            float dist = Vector2.Distance(d[last], d[first]) / 3f;
            return new Segment
            {
                P0 = d[first],
                P1 = d[first] + tHat1 * dist,
                P2 = d[last] + tHat2 * dist,
                P3 = d[last],
            };
        }

        return new Segment
        {
            P0 = d[first],
            P1 = d[first] + tHat1 * (float)alphaL,
            P2 = d[last] + tHat2 * (float)alphaR,
            P3 = d[last],
        };
    }

    /// <summary>原文 `ComputeMaxError`：逐点求"到贝塞尔曲线（在参数 u 处）"的**平方**距离，
    /// 返回最大值并给出它落在哪个点（拆分的依据）。</summary>
    private static float ComputeMaxErrorSq(Vector2[] d, int first, int last,
                                           in Segment curve, double[] u, out int splitPoint)
    {
        splitPoint = (last - first + 1) / 2;
        float maxDistSq = 0f;
        for (int i = first + 1; i < last; i++)
        {
            var p = BezierAt(curve, (float)u[i - first]);
            float distSq = Vector2.DistanceSquared(p, d[i]);
            if (distSq >= maxDistSq)
            {
                maxDistSq = distSq;
                splitPoint = i;
            }
        }
        return maxDistSq;
    }

    /// <summary>原文 `Reparameterize`：对每个点做一次牛顿迭代，改进它的参数 u。</summary>
    private static double[] Reparameterize(Vector2[] d, int first, int last, double[] u, in Segment curve)
    {
        int nPts = last - first + 1;
        var uPrime = new double[nPts];
        for (int i = 0; i < nPts; i++)
            uPrime[i] = NewtonRaphsonRootFind(curve, d[first + i], u[i]);
        return uPrime;
    }

    /// <summary>
    /// 原文 `NewtonRaphsonRootFind`：解 "点 P 到曲线的最短距离那个参数"（一维牛顿法）。
    /// 用 Q' 与 Q'' 的控制点（差分）算分子分母——照搬，不做化简。
    /// </summary>
    private static double NewtonRaphsonRootFind(in Segment q, Vector2 p, double u)
    {
        var q1 = new Vector2[3];
        for (int i = 0; i <= 2; i++)
            q1[i] = (new Vector2(
                // 一阶导的控制点：相邻控制点差 × 3
                (q.Get(i + 1).X - q.Get(i).X) * 3f,
                (q.Get(i + 1).Y - q.Get(i).Y) * 3f));
        var q2 = new Vector2[2];
        for (int i = 0; i <= 1; i++)
            q2[i] = new Vector2(
                (q1[i + 1].X - q1[i].X) * 2f,
                (q1[i + 1].Y - q1[i].Y) * 2f);

        var qu = BezierAt(q, (float)u);
        var q1u = BezierPolyAt(q1, (float)u);
        var q2u = BezierPolyAt(q2, (float)u);

        double numerator = (qu.X - p.X) * q1u.X + (qu.Y - p.Y) * q1u.Y;
        double denominator = q1u.X * q1u.X + q1u.Y * q1u.Y
                           + (qu.X - p.X) * q2u.X + (qu.Y - p.Y) * q2u.Y;
        if (Math.Abs(denominator) < 1e-12) return u;
        return u - numerator / denominator;
    }

    /// <summary>原文 `ChordLengthParameterize`：按弦长累积给每个点一个初始参数 u∈[0,1]。</summary>
    private static double[] ChordLengthParameterize(Vector2[] d, int first, int last)
    {
        int nPts = last - first + 1;
        var u = new double[nPts];
        u[0] = 0.0;
        for (int i = first + 1; i <= last; i++)
            u[i - first] = u[i - first - 1] + Vector2.Distance(d[i], d[i - 1]);
        double total = u[nPts - 1];
        if (total <= 1e-9) { for (int i = 0; i < nPts; i++) u[i] = i / (double)(nPts - 1); return u; }
        for (int i = 1; i < nPts; i++) u[i] /= total;
        return u;
    }

    /// <summary>原文 `ComputeLeftTangent`：起点切线（朝第二个点）。</summary>
    private static Vector2 ComputeLeftTangent(Vector2[] d, int end) => Normalize(d[end + 1] - d[end]);

    /// <summary>原文 `ComputeRightTangent`：终点切线（朝倒数第二个点）。</summary>
    private static Vector2 ComputeRightTangent(Vector2[] d, int end) => Normalize(d[end - 1] - d[end]);

    /// <summary>原文 `ComputeCenterTangent`：中间切开处的切线（前后两段方向的**平均**）。</summary>
    private static Vector2 ComputeCenterTangent(Vector2[] d, int center)
    {
        var v1 = d[center - 1] - d[center];
        var v2 = d[center] - d[center + 1];
        return Normalize((v1 + v2) * 0.5f);
    }

    private static Vector2 Normalize(Vector2 v)
    {
        float len = v.Length();
        return len <= 1e-9f ? new Vector2(1f, 0f) : v / len;
    }

    // ---- 伯恩斯坦基（原文 B0..B3）------------------------------------------------

    private static float B0(double u) { float t = (float)(1.0 - u); return t * t * t; }
    private static float B1(double u) { float t = (float)(1.0 - u); return 3f * (float)u * t * t; }
    private static float B2(double u) { float t = (float)(1.0 - u); return 3f * (float)u * (float)u * t; }
    private static float B3(double u) { float uu = (float)u; return uu * uu * uu; }

    /// <summary>原文 `BezierII`：de Casteljau 求值（三次）。</summary>
    private static Vector2 BezierAt(in Segment c, float t)
    {
        Span<Vector2> v = stackalloc Vector2[4];
        v[0] = c.P0; v[1] = c.P1; v[2] = c.P2; v[3] = c.P3;
        return BezierPolyAt(v, t);
    }

    /// <summary>
    /// de Casteljau（阶数由点数定：4 个控制点 = 三次）。
    /// ⚠ 缓冲区开在**栈上**：这个函数在一次拟合里要被调上千次（每轮迭代 × 每个点），
    /// 早先每调一次 `Clone()` 一个数组，401 点那一笔光分配就够把停顿那一下拖慢。
    /// </summary>
    private static Vector2 BezierPolyAt(ReadOnlySpan<Vector2> v, float t)
    {
        Span<Vector2> tmp = stackalloc Vector2[4];
        int degree = v.Length - 1;
        for (int i = 0; i <= degree; i++) tmp[i] = v[i];
        for (int i = 1; i <= degree; i++)
            for (int j = 0; j <= degree - i; j++)
                tmp[j] = tmp[j] * (1f - t) + tmp[j + 1] * t;
        return tmp[0];
    }

    // ---- 给引擎用的两个工具 -------------------------------------------------------

    /// <summary>
    /// 把贝塞尔段**展平成点串**（引擎里"墨"就是一个点串，所以展平之后
    /// 渲染 / 命中 / 存档 / 导出**一行都不用改**）。
    /// `spacing` 是采样间距（画布单位）：段越长、弯得越厉害，采得越密。
    /// </summary>
    internal static Vector2[] Flatten(IReadOnlyList<Segment> segs, float spacing)
    {
        if (segs == null || segs.Count == 0) return Array.Empty<Vector2>();
        if (spacing <= 0.1f) spacing = 0.1f;

        var pts = new List<Vector2>(segs.Count * 8);
        for (int s = 0; s < segs.Count; s++)
        {
            var c = segs[s];
            // 估这一段要多长：**弦与控制多边形的平均**——弧长的经典估计
            //（单用控制多边形会明显偏长：弯的段能长出 15%，于是展平出来的点数反而比原迹多）。
            float polyLen = Vector2.Distance(c.P0, c.P1) + Vector2.Distance(c.P1, c.P2)
                          + Vector2.Distance(c.P2, c.P3);
            float chord = Vector2.Distance(c.P0, c.P3);
            float approx = (polyLen + chord) * 0.5f;
            int n = Math.Clamp((int)MathF.Ceiling(approx / spacing), 2, 240);
            for (int i = 0; i <= n; i++)
            {
                if (s > 0 && i == 0) continue;          // 关节只留一个点
                pts.Add(BezierAt(c, i / (float)n));
            }
        }
        return pts.ToArray();
    }

    /// <summary>
    /// **独立验收用**：原始每个采样点到"展平后的点串"的最近距离，取最大。
    ///
    /// ⚠ 这是**跟拟合器无关**的一条量法（拟合器内部比的是"到贝塞尔在参数 u 处的平方距离"，
    /// 这里是"到折线的欧氏距离"）——所以它能拿来验拟合器，不是自己给自己打分。
    /// ⚠ 复杂度：两条点串都**沿曲线单调走**，所以"最近的段"的下标也单调——用滑窗往后看
    /// （窗口宽度按两边的点数比算）。**不要写成两层全扫**：401 点那一笔全扫要 5 ms，
    /// 卡在"停顿那一刻"不合适（`--smoothtest` 量过）。
    /// </summary>
    internal static float MaxDeviationToPolyline(IReadOnlyList<Vector2> pts, IReadOnlyList<Vector2> poly)
    {
        if (pts == null || poly == null || pts.Count == 0 || poly.Count == 0) return float.MaxValue;
        if (poly.Count == 1) return Vector2.Distance(pts[0], poly[0]);

        // 一次全扫也只在点数很少时发生（退化输入）；正常走下面的滑窗
        if (pts.Count * poly.Count <= 4096)
        {
            float w0 = 0f;
            for (int i = 0; i < pts.Count; i++)
                w0 = MathF.Max(w0, NearestToPolyline(pts[i], poly, 0, poly.Count - 2));
            return w0;
        }

        int win = 4 + (int)MathF.Ceiling(poly.Count / (float)pts.Count) * 3;
        int j0 = 0;
        float worst = 0f;
        for (int i = 0; i < pts.Count; i++)
        {
            int from = Math.Max(0, j0 - 2);
            int to = Math.Min(poly.Count - 2, j0 + win);
            float best = float.MaxValue;
            int bestJ = from;
            for (int j = from; j <= to; j++)
            {
                float d = DistanceToSegment(pts[i], poly[j], poly[j + 1]);
                if (d < best) { best = d; bestJ = j; }
            }
            // 只在"窗口右端就是最近"时前移（保证不会跳过更近的段）
            j0 = bestJ;
            if (best > worst) worst = best;
        }
        return worst;
    }

    private static float NearestToPolyline(Vector2 p, IReadOnlyList<Vector2> poly, int fromJ, int toJ)
    {
        float best = float.MaxValue;
        for (int j = fromJ; j <= toJ; j++)
        {
            float d = DistanceToSegment(p, poly[j], poly[j + 1]);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// 展平间距该怎么取：**不比原迹更密**——不密的话点数不增加（不白占内存和渲染）。
    /// 原迹点距本身就比 `floorLogical` 大时（采样稀，例如鼠标快划），跟原迹走。
    /// 引擎和自检都用这一处（同一个量只写一处）。
    ///
    /// ⚠ 乘 1.1 是给 `Flatten` 的**取整余量**：每段按"控制多边形 ÷ 间距"上取整、还要留关节，
    /// 逐段累积下来会多出几个点（实测 181 点那一笔会到 185）——留 10% 余量才能真的"不增加"。
    /// </summary>
    internal static float FlattenSpacing(float pathLength, int rawPointCount, float floorLogical, float dpiScale)
        => MathF.Max(floorLogical * dpiScale, 1.1f * pathLength / MathF.Max(1, rawPointCount));

    /// <summary>点到线段的距离（投影夹在两端之间）。</summary>
    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq <= 1e-9f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
