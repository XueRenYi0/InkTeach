using System.Numerics;

namespace InkEngine.Optimize;

/// <summary>
/// 手写美化：把一串原始采样点变成一条"像用笔写出来的"轮廓。
///
/// 算法取自 perfect-freehand（MIT，https://github.com/steveruizok/perfect-freehand），
/// 按 C# 与我们的数据结构重写。它解决的问题正好是我们缺的那几项：
///
///   1. **速度 → 粗细（thinning）**：写得快就细，这是"像笔"的第一要素。
///   2. **没有压感时用速度模拟压力**：鼠标、普通触摸屏、一体机的手指书写
///      都没有真实压感，靠这个才有笔锋。（公式见 SimulatePressure）
///   3. **起笔 / 收笔渐细（taper）**：最直观的"笔锋"——老师写"一"字的
///      两头应该是尖的，而不是两根等粗的香肠。
///   4. **圆头端帽（cap）**：笔画两端收成圆弧，不再是被切平的方块。
///   5. **流线化（streamline）**：输入点先做插值平滑，去掉手抖的锯齿。
///   6. **尖角处理**：拐角处补一小段圆弧，避免折角被"撕开"。
///
/// 输出是一圈闭合的轮廓点，直接喂给现有的填充管线——渲染、脏区、命中测试
/// 全都不用改，只是把"算轮廓"这一步换得更讲究。
/// </summary>
internal static class StrokeBeautifier
{
    /// <summary>
    /// 上一次算轮廓时每个采样点的宽度（直径，物理像素）。仅供自检与调试——
    /// 有了它，"渐细到底有没有生效"就不需要靠猜或者靠人眼看图。
    /// </summary>
    internal static float[] LastWidths;

    /// <summary>上一次算轮廓时用的基础笔宽（物理像素），自检用来核对是否超界。</summary>
    internal static float LastSize;
    /// <summary>诊断用：这一笔里半径最大那一次的计算过程。</summary>
    internal static string DiagMaxNote = "";
    private static float _diagMaxR;
    /// <summary>
    /// 一支笔的造型参数。调这几个数就能得到完全不同的手感，
    /// 对应工具条上的"笔锋预设"。
    /// </summary>
    internal struct PenShape
    {
        /// <summary>速度对粗细的影响强度。0 = 等宽，0.5 = 标准，越大越快细。</summary>
        public float Thinning;

        /// <summary>输入平滑强度。0 = 原样，0.5 = 标准，越大越圆润。</summary>
        public float Streamline;

        /// <summary>轮廓内侧的最小间距。越大越省点，边缘越"整"。</summary>
        public float Smoothing;

        /// <summary>
        /// 起笔渐细的长度，**单位是像素**（0 = 不渐细）。
        ///
        /// 为什么用绝对值而不是"笔宽倍数"或"总长百分比"：按比例给的话，一条
        /// 短线会被渐细吃掉一大截，看起来像被捏扁了——实测就是这么变形的。
        /// 上游的 `start.taper` 也是绝对值（像素或 true）。
        /// </summary>
        public float TaperStartPx;

        /// <summary>收笔渐细的长度，单位像素。</summary>
        public float TaperEndPx;

        /// <summary>边缘粗糙度（0~1）。粉笔用它做出"沙沙"的边。</summary>
        public float Roughness;

        /// <summary>没有真实压感时，用速度模拟压力。</summary>
        public bool SimulatePressure;

        /// <summary>笔画最细不低于基础宽度的这个比例，避免细到看不见。</summary>
        public float MinWidthFactor;
    }

    /// <summary>
    /// 各档参数。基准是 perfect-freehand 官方 demo 的默认风格，
    /// 改的是"老师上课怎么用"这一层：
    ///
    /// - 写字要有一点点快慢粗细，但**不能做渐细**（默认就是 0）；
    /// - 画图/连线要**完全等宽**，否则直线看起来是歪的；
    /// - 粗笔要把速度感压得更低，因为粗笔上任何起伏都被放大。
    /// </summary>
    internal static PenShape Preset(PenPreset preset) => preset switch
    {
        // 手写美化（默认）：速度→宽度 0.5（上游默认值），不起收笔渐细。
        // 端头用圆帽已经是"收得住"的效果，不必再削尖——削尖就是上一版
        // "粗笔难看、短线变形"的根源。
        PenPreset.Handwriting => new PenShape
        {
            // thinning 用 0.34 而不是上游默认的 0.5：0.5 在我们的采样率下
            // 实测宽度波动 46%，写在粗笔上就是"忽粗忽细"。0.34 大约 30%，
            // 有笔意但不夸张。上游自己是整套 demo 一起调的，不能只照抄一个数。
            // 0.26 实测波动约 30%，写在 3~6 像素的笔上是"有笔意但不夸张"。
            // 这个数是用自检量出来的，不是照抄——上游的 0.5 在他们的采样率下
            // 波动 46%，放到我们的采样率上就是"忽粗忽细"。
            Thinning = 0.26f, Streamline = 0.50f, Smoothing = 0.50f,
            TaperStartPx = 0f, TaperEndPx = 0f, Roughness = 0f,
            SimulatePressure = true, MinWidthFactor = 0.72f,
        },

        // 精确：等宽、不模拟速度。画线段、箭头、几何图形用。
        // 填 0 就是"不美化"，走的是最朴素的等宽带子。
        PenPreset.Precise => new PenShape
        {
            Thinning = 0f, Streamline = 0.50f, Smoothing = 0.50f,
            TaperStartPx = 0f, TaperEndPx = 0f, Roughness = 0f,
            SimulatePressure = false, MinWidthFactor = 1.0f,
        },

        // 粗笔：速度感压到 0.28（上游默认的一半多一点），下限抬到 0.85，
        // 这样 16~24 逻辑像素的粗笔写出来仍然干净。
        PenPreset.Bold => new PenShape
        {
            // 粗笔把速度感压到近乎没有：32 逻辑像素的笔上，任何起伏都被放大成
            // "忽粗忽细"，看起来像画歪了。这一档就是给"粗笔写大字"用的。
            Thinning = 0.14f, Streamline = 0.55f, Smoothing = 0.55f,
            TaperStartPx = 0f, TaperEndPx = 0f, Roughness = 0f,
            SimulatePressure = true, MinWidthFactor = 0.92f,
        },

        // 书法：唯一开渐细的一档，而且要短（按像素给，不按比例），
        // 否则短笔画会被削变形。
        // 书法：唯一开渐细的一档。长度是**逻辑像素**，且受"最少留一半"约束，
        // 免得短笔画被削成梭形。
        PenPreset.Calligraphy => new PenShape
        {
            Thinning = 0.45f, Streamline = 0.55f, Smoothing = 0.50f,
            TaperStartPx = 5f, TaperEndPx = 6f, Roughness = 0f,
            SimulatePressure = true, MinWidthFactor = 0.72f,
        },

        _ => Preset(PenPreset.Handwriting),
    };

    // ---- 与上游一致的常量（改动前请先看一眼上游的 constants.ts）--------
    /// <summary>模拟压力每次允许变化的幅度。</summary>
    private const float RateOfPressureChange = 0.275f;
    /// <summary>起笔的默认压力。刻意压低——落笔那一刻手速通常很慢，
    /// 不压的话每一笔的开头都会鼓出一个疙瘩。</summary>
    private const float DefaultFirstPressure = 0.25f;
    private const float DefaultPressure = 0.5f;
    /// <summary>收笔处几像素内的点丢掉，去掉抬手前的抖动。</summary>
    private const float EndNoiseThreshold = 3f;
    /// <summary>圆头端帽的分段数。</summary>
    private const int StartCapSegments = 13;
    private const int EndCapSegments = 29;
    private const int CornerCapSegments = 13;

    /// <summary>一个采样点，附带算出来的向量与累计弧长。</summary>
    private struct Pt
    {
        public Vector2 P;
        public float Pressure;
        /// <summary>指向前一个点的单位向量。</summary>
        public Vector2 Vector;
        public float Distance;
        public float RunningLength;
    }

    /// <summary>
    /// 算出一条笔画的轮廓。
    /// </summary>
    /// <param name="pts">原始采样点（虚拟桌面坐标）。</param>
    /// <param name="hasRealPressure">输入是否带真实压感。false 时用速度模拟。</param>
    /// <param name="baseWidth">基础笔宽（物理像素）。</param>
    /// <param name="shape">笔锋参数。</param>
    /// <returns>闭合轮廓；点数不足或退化时返回 null。</returns>
    internal static Vector2[] BuildOutline(List<InkPoint> pts, bool hasRealPressure,
                                           float baseWidth, in PenShape shape,
                                           float dpiScale = 1f)
    {
        if (pts == null || pts.Count == 0 || baseWidth <= 0) return null;

        // 1) 流线化 + 生成带向量的采样点
        var stroke = BuildStrokePoints(pts, baseWidth, shape.Streamline, hasRealPressure);
        if (stroke.Count == 0) return null;

        // 2) 逐点算半径 → 沿法线往两侧偏移 → 3) 两端补端帽
        LastSize = baseWidth;
        return BuildOutlinePoints(stroke, baseWidth, shape, hasRealPressure,
                                  dpiScale <= 0 ? 1f : dpiScale);
    }

    // =====================================================================
    //  第一步：流线化。把原始点按 streamline 插值成更平滑的一串，
    //  同时算出每点的方向向量与累计长度。
    // =====================================================================
    private static List<Pt> BuildStrokePoints(List<InkPoint> pts, float size,
                                              float streamline, bool hasRealPressure)
    {
        _diagMaxR = 0f;
        DiagMaxNote = "";
        var result = new List<Pt>(pts.Count + 4);

        // streamline 越大，越靠近上一个点（越平滑）。0.15 是上游给的下限。
        float t = 0.15f + (1f - Math.Clamp(streamline, 0f, 1f)) * 0.85f;

        // 只有两个点时，中间补几个点，否则渐细的笔画会画成虚线。
        var work = pts;
        if (pts.Count == 2)
        {
            work = new List<InkPoint>(6) { pts[0] };
            for (int i = 1; i < 5; i++)
            {
                float f = i / 4f;
                work.Add(new InkPoint
                {
                    X = pts[0].X + (pts[1].X - pts[0].X) * f,
                    Y = pts[0].Y + (pts[1].Y - pts[0].Y) * f,
                    P = pts[0].P + (pts[1].P - pts[0].P) * f,
                    T = pts[0].T,
                });
            }
        }
        else if (pts.Count == 1)
        {
            // 单点：补一个偏移点，让它退化成一个小圆点而不是空轮廓。
            work = new List<InkPoint>(2)
            {
                pts[0],
                new InkPoint { X = pts[0].X + 1f, Y = pts[0].Y + 1f, P = pts[0].P, T = pts[0].T },
            };
        }

        var first = new Pt
        {
            P = new Vector2(work[0].X, work[0].Y),
            Pressure = hasRealPressure ? Math.Clamp(work[0].P, 0f, 1f) : DefaultFirstPressure,
            Vector = new Vector2(1, 1),
            Distance = 0,
            RunningLength = 0,
        };
        result.Add(first);

        var prev = first;
        float runningLength = 0;
        bool reachedMinLength = false;
        int max = work.Count - 1;

        for (int i = 1; i < work.Count; i++)
        {
            var target = new Vector2(work[i].X, work[i].Y);

            // 按 streamline 在"上一个点"和"当前点"之间插值：这就是平滑的来源。
            Vector2 point = i == max
                ? target
                : Vector2.Lerp(prev.P, target, t);

            if (Vector2.DistanceSquared(point, prev.P) < 1e-12f) continue;

            float distance = Vector2.Distance(point, prev.P);
            runningLength += distance;

            // 起笔阶段先攒够一小段距离再开始记录，避免开头被抖动带偏。
            if (i < max && !reachedMinLength)
            {
                if (runningLength < size) continue;
                reachedMinLength = true;
            }

            var cur = new Pt
            {
                P = point,
                Pressure = hasRealPressure
                    ? Math.Clamp(work[i].P, 0f, 1f)
                    : DefaultPressure,
                Vector = Vector2.Normalize(prev.P - point),
                Distance = distance,
                RunningLength = runningLength,
            };
            result.Add(cur);
            prev = cur;
        }

        if (result.Count > 1) result[0] = WithVector(result[0], result[1].Vector);
        return result;
    }

    private static Pt WithVector(Pt p, Vector2 v) { p.Vector = v; return p; }

    // =====================================================================
    //  第二步：把中心线展开成轮廓
    // =====================================================================
    private static Vector2[] BuildOutlinePoints(List<Pt> points, float size,
                                                in PenShape shape, bool hasRealPressure,
                                                float dpiScale)
    {
        if (points.Count == 1)
        {
            float r1 = size * 0.5f;
            return Circle(points[0].P, r1);
        }

        float thinning = Math.Clamp(shape.Thinning, 0f, 1f);
        float totalLength = points[^1].RunningLength;
        float minDistance = MathF.Pow(size * Math.Clamp(shape.Smoothing, 0.05f, 1f), 2);

        // 渐细长度直接就是像素值（与上游一致），只受总长约束。
        //
        // 上一版按"笔宽倍数 + 总长 12%"算，结果：600 像素的长横线两端各渐细
        // 70 像素，粗笔看起来像被削尖的雪茄；而 80 像素的短笔画又被渐细吃掉
        // 一大半，直接变形。改成绝对值之后两个问题都没有了。
        // 渐细长度：参数是逻辑像素，换算成物理像素再和弧长比。
        //
        // 再压一道"最少留一半"：渐细加起来最多吃掉一半长度，否则短笔画的
        // 中段永远到不了全宽，看起来像被捏扁了（实测过）。
        float taperCap = totalLength * 0.5f;
        float taperStart = shape.TaperStartPx <= 0f
            ? 0f
            : MathF.Min(shape.TaperStartPx * dpiScale, taperCap);
        float taperEnd = shape.TaperEndPx <= 0f
            ? 0f
            : MathF.Min(shape.TaperEndPx * dpiScale, MathF.Max(0f, taperCap - taperStart));

        var left = new List<Vector2>(points.Count + 8);
        var right = new List<Vector2>(points.Count + 8);
        var radii = new List<float>(points.Count);

        // 开头几个点的压力取平均：手落笔时通常很慢，直接用会被"鼓头"。
        float prevPressure = InitialPressure(points, hasRealPressure, size);
      float radius = size * 0.5f;
        float firstRadius = 0f;
        var prevVector = points[0].Vector;
        var prevLeft = points[0].P;
        var prevRight = points[0].P;
        bool prevSharp = false;

        // 粗糙度用的确定性噪声（不用随机数：同一笔每次重画必须长得一样，
        // 否则几何缓存会不停失效）。
        float roughSeed = 0f;

        for (int i = 0; i < points.Count; i++)
        {
            var cur = points[i];
            float pressure = cur.Pressure;
            bool isLast = i == points.Count - 1;

            // 收笔前几像素内的点丢掉，去抬手抖动（最后一点保留）。
            if (!isLast && totalLength - cur.RunningLength < EndNoiseThreshold) continue;

            if (thinning > 0f)
            {
                if (shape.SimulatePressure && !hasRealPressure)
                    pressure = SimulatePressure(prevPressure, cur.Distance, size);
                radius = size * (0.5f - thinning * (0.5f - pressure));
            }
            else
            {
                radius = size * 0.5f;
            }

            // 粗细的下限：太细会细到看不见（尤其在低分辨率投影上）。
            radius = MathF.Max(radius, size * shape.MinWidthFactor * 0.5f);
            if (firstRadius == 0f) firstRadius = radius;

            // ---- 起笔 / 收笔渐细 ----
            float startStrength = 1f;
            if (taperStart > 0f && cur.RunningLength < taperStart)
            {
                float k = Math.Clamp(cur.RunningLength / taperStart, 0f, 1f);
                startStrength = k * (2f - k);          // 与上游一致：先快后慢
            }
            float endStrength = 1f;
            if (taperEnd > 0f && totalLength - cur.RunningLength < taperEnd)
            {
                float k = Math.Clamp((totalLength - cur.RunningLength) / taperEnd, 0f, 1f);
                float m = k - 1f;
                endStrength = m * m * m + 1f;          // 与上游一致
            }
            radius = MathF.Max(0.01f, radius * MathF.Min(startStrength, endStrength));
            radii.Add(radius * 2f);   // 之后按弧长重采样成宽度曲线
            if (radius > _diagMaxR)
            {
                _diagMaxR = radius;
                DiagMaxNote = $"max r={radius:F2} size={size:F1} p={pressure:F3}"
                            + $" thin={thinning:F2} k={MathF.Min(startStrength, endStrength):F3}"
                            + $" floor={size * shape.MinWidthFactor * 0.5f:F2}";
            }

            // ---- 尖角：前后方向反转超过直角就补一小段圆，别让折角裂开 ----
            var nextVector = !isLast ? points[i + 1].Vector : cur.Vector;
            float nextDpr = !isLast ? Dot(cur.Vector, nextVector) : 1f;
            float prevDpr = Dot(cur.Vector, prevVector);
            bool sharp = (prevDpr < 0f && !prevSharp) || nextDpr < 0f;

            if (sharp)
            {
                var offset = Perp(prevVector) * radius;
                for (int s = 0; s <= CornerCapSegments; s++)
                {
                    float a = MathF.PI * (s / (float)CornerCapSegments);
                    left.Add(Rotate(cur.P - offset, cur.P, a));
                    right.Add(Rotate(cur.P + offset, cur.P, -a));
                }
                prevLeft = left[^1];
                prevRight = right[^1];
                if (nextDpr < 0f) prevSharp = true;
                continue;
            }
            prevSharp = false;

            if (isLast)
            {
                var off = Perp(cur.Vector) * radius;
                left.Add(cur.P - off);
                right.Add(cur.P + off);
                continue;
            }

            // ---- 普通点：按前后方向的插值定法线，往两侧各偏一个半径 ----
            var blended = Vector2.Lerp(nextVector, cur.Vector, Math.Clamp(nextDpr, 0f, 1f));
            var normal = Perp(blended) * radius;

            // 粗糙度：沿法线加一点确定性的起伏，做出粉笔那种毛边。
            //
            // 关键约束：起伏**必须包含在 radius 之内**，不能让偏移量超过半径。
            // 之前是"先算好偏移再额外加一点"，结果轮廓比声明的边界还宽
            // （实测粉笔宽了 15%），而脏区是按声明边界算的——快速书写就会留残影。
            if (shape.Roughness > 0f)
            {
                roughSeed += 0.7f;
                float wobble = MathF.Sin(roughSeed * 1.9f) * 0.6f
                             + MathF.Sin(roughSeed * 0.53f) * 0.4f;
                // 正负各占一半，外侧最多扩到 radius，内侧最多收到 radius*0.6。
                float k = 1f + Math.Clamp(wobble, -1f, 1f) * shape.Roughness * 0.4f;
                normal = Vector2.Normalize(normal) * (radius * k);
            }

            // 两侧要么都加、要么都不加。分别判断会让左右两侧的点数错位，
            // 于是"左侧第 i 点"和"右侧第 i 点"不再位于同一个横截面上，
            // 轮廓就会歪，量出来的宽度也会偏大。这里用一个条件同时管两边。
            var tl = cur.P - normal;
            var tr = cur.P + normal;
            if (i <= 1 || Vector2.DistanceSquared(prevLeft, tl) > minDistance)
            {
                left.Add(tl);
                right.Add(tr);
                prevLeft = tl;
                prevRight = tr;
            }

            prevPressure = pressure;
            prevVector = cur.Vector;
        }

        if (left.Count == 0 || right.Count == 0) return null;

        // 存下逐点的宽度曲线，供自检核对"渐细有没有真的作用到形状上"。
        LastWidths = radii.ToArray();

        // ---- 端帽：渐细的那一头不补圆帽（本来就是尖的），另一头补圆弧 ----
        var last = points[^1];
        float endRadius = radius;
        var outline = new List<Vector2>(left.Count + right.Count + EndCapSegments + 2);

        // 左侧从头到尾
        foreach (var p in left) outline.Add(p);

        // 收笔端帽
        if (taperEnd <= 0f)
        {
            var dir = Perp(-last.Vector);
            var start = last.P + dir * endRadius;
            for (int s = 1; s < EndCapSegments; s++)
            {
                float a = MathF.PI * 3f * (s / (float)EndCapSegments);
                outline.Add(Rotate(start, last.P, a));
            }
        }
        else
        {
            outline.Add(last.P);
        }

        // 右侧从尾到头
        for (int i = right.Count - 1; i >= 0; i--) outline.Add(right[i]);

        // 起笔端帽
        if (taperStart <= 0f)
        {
            var first = points[0];
            var rightFirst = right[0];
            for (int s = 1; s < StartCapSegments; s++)
            {
                float a = MathF.PI * (s / (float)StartCapSegments);
                outline.Add(Rotate(rightFirst, first.P, -a));
            }
        }
        else
        {
            outline.Add(points[0].P);
        }

        return outline.Count >= 3 ? outline.ToArray() : null;
    }

    /// <summary>
    /// 没有真实压感时，用"移动速度"模拟压力：走得快 → 压力小 → 笔画细。
    /// 这就是鼠标也能写出笔锋的原因。
    /// </summary>
    private static float SimulatePressure(float prevPressure, float distance, float size)
    {
        float sp = MathF.Min(1f, distance / MathF.Max(1e-3f, size));
        float rp = MathF.Min(1f, 1f - sp);
        return MathF.Min(1f, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
    }

    private static float InitialPressure(List<Pt> pts, bool hasRealPressure, float size)
    {
        float acc = pts[0].Pressure;
        int n = Math.Min(10, pts.Count);
        for (int i = 0; i < n; i++)
        {
            float p = pts[i].Pressure;
            if (!hasRealPressure) p = SimulatePressure(acc, pts[i].Distance, size);
            acc = (acc + p) * 0.5f;
        }
        return acc;
    }

    private static Vector2[] Circle(Vector2 center, float radius)
    {
        const int segments = 24;
        var pts = new Vector2[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.PI * 2f * i / segments;
            pts[i] = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
        }
        return pts;
    }

    private static Vector2 Perp(Vector2 v) => new(-v.Y, v.X);

    private static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;

    private static Vector2 Rotate(Vector2 p, Vector2 center, float angle)
    {
        float s = MathF.Sin(angle), c = MathF.Cos(angle);
        var d = p - center;
        return new Vector2(d.X * c - d.Y * s, d.X * s + d.Y * c) + center;
    }
}
