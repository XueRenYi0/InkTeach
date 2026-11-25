// [职责] 收笔笔锋（速度尖尾）：抬笔前末段快 → 笔画末端渐尖；慢 → 等宽。
// [测试] --tapertest

using System.Runtime.CompilerServices;

namespace InkEngine;

/// <summary>
/// **收笔笔锋（速度尖尾）**：抬笔前末段写得快 → 笔画末端渐尖；慢 → 完全等宽。
/// 仿 ClassIn；单源 = perfect-freehand 的 `end.taper`（MIT，见 THIRD-PARTY-NOTICES.md）。
///
/// 只做**渲染期**加工：不改 <see cref="Stroke.Points"/>、不改 <see cref="InkPoint"/>、
/// 不进存档；导出 / 回放 / 分块烘焙按同一算法重算，结果一致，命中 / 撤销 / 橡皮看不见它。
///
/// 与旧 `--flicktip`（已删除）的区别：尾长**随速度连续变化**（不再是固定 L），
/// 速度门槛也做成软区间（低于 <see cref="VMin"/> 全 1，达到 <see cref="VMax"/> 取满）。
/// 不做整条线速率变宽——中段恒等宽，只有末端那一小段收尖。
/// </summary>
internal static class StrokeTaper
{
    /// <summary>总开关（默认**开**；`--notaper` / 设置项 `taper=0` 关）。</summary>
    public static bool Enabled = true;

    /// <summary>速度窗口（ms）：取最后约 100ms 内的点（至少末 3 段；T 有噪声，用窗口内位移 ÷ 时间）。</summary>
    public static double WindowMs = 100.0;

    /// <summary>收尖速度下限（px/ms）：低于它 → 全 1（完全等宽，不顿出尖）。</summary>
    public static float VMin = 0.5f;

    /// <summary>收尖速度上限（px/ms）：达到/超过它 → t=1，尾长取满 TailFactor×Width。</summary>
    public static float VMax = 2.5f;

    /// <summary>满速时的尾长系数（× <see cref="Stroke.Width"/>；`--taper L,vMin` 可调）。</summary>
    public static float TailFactor = 2.5f;

    /// <summary>尾长上限（× Width）：细笔同速同尾长会变成"针尖"（约 10–12 倍线宽），
    /// 封在 4×Width 后细笔尖尾缩短、粗笔（2.5×Width &lt; 4×Width）不受影响。</summary>
    public static float MaxTailWidths = 4f;

    /// <summary>尾长下限（× Width）：**短笔宽收尾太急像"剁"**——把尾长按线宽下半段留个比例，
    /// 让粗笔的斜切变缓（8px 笔仍是 1.4×，18px 笔从 0.8× 提到 1.4×）。</summary>
    public const float MinTailWidthsRelative = 1.4f;

    /// <summary>eased 超过它才应用保底尾长；否则尾长按 eased×TailFactor×Width 自然缩短。</summary>
    public const float MinTailEase = 0.15f;

    /// <summary>末点系数下限（>0：尖端不至于细成一根发丝 / 被 D2D 渲成透明）。</summary>
    public const float MinFactor = 0.15f;

    /// <summary>**尖端绝对下限**（逻辑像素半宽）：细笔按比例算出来只有零点几像素，
    /// 屏幕上就是"一根针"；这里抬到一个很小的绝对半径（不做大头，否则笔锋就没了）。</summary>
    public const float MinTipHalfPx = 0.6f;
    public const float MinTipHalfWidths = 0.04f;
    /// <summary>尖端系数最多抬到多少：再抬就不是"尖尾"而是"平头"了。</summary>
    public const float TipFloorCap = 0.2f;

    /// <summary>参数 / 开关版本：笔画缓存键带上它（运行时拨开关 / 调参要能立刻重算）。</summary>
    public static int Version { get; private set; }
    public static void BumpVersion() => Version++;

    private sealed class Cache
    {
        public int Revision = -1;
        public int Version = -1;
        public float[] F;       // 逐点系数（可能全 1）
        public bool Uniform;    // F 是否全 1 → Factors 返回 null
    }

    private static readonly ConditionalWeakTable<Stroke, Cache> s_cache = new();

    /// <summary>
    /// 这一笔该不该走收尖：笔、自由笔迹、点数 ≥ 4、实线，不是激光 / 荧光笔 / 橡皮
    /// （那些按现有约定忽略压力与笔锋）。**有压感的笔迹也生效**——真笔（Windows Ink
    /// 一定报压感）是主要设备，笔锋要在有压感的中段上再叠末端尖尾。
    /// </summary>
    public static bool Eligible(Stroke s)
        => Enabled && s != null && !s.ExactPolyline && s.Tool == Tool.Pen
           && s.Kind == StrokeKind.Freehand && s.Points.Count >= 4
           && s.Dash == StrokeDash.Solid;     // 虚线/点线走等宽描边，没有"尖端"可言

    /// <summary>
    /// 逐点系数（长度 = <see cref="Stroke.Points"/>.Count）；**null = 全 1**（等宽）。
    /// 按 <see cref="Stroke.Revision"/> + <see cref="Version"/> 缓存。
    /// </summary>
    public static float[] Factors(Stroke s)
    {
        if (!Eligible(s)) return null;
        var cache = s_cache.GetValue(s, _ => new Cache());
        if (cache.F == null || cache.F.Length != s.Points.Count
            || cache.Revision != s.Revision || cache.Version != Version)
        {
            cache.F = Compute(s, -1, 0f);
            cache.Uniform = IsUniform(cache.F);
            cache.Revision = s.Revision;
            cache.Version = Version;
        }
        return cache.Uniform ? null : cache.F;
    }

    /// <summary>
    /// **按前缀末端**算系数（回放前缀用）：把"末端"从整笔末点改成
    /// `cum(lastIdx) + frac × 下一段弧长`，尾长 / 曲线 / 下限都不变。
    /// 这样回放里"正在长"的那笔，尖端随书写推进平滑移动，而不是先等宽、抬笔瞬间整段变尖。
    /// <paramref name="lastIdx"/>&lt;0 = 整笔（末点即末端）。
    /// 不缓存（前缀每帧都在变）；速度门控仍用整笔末段速度（回放语义：这一笔整体快不快）。
    /// </summary>
    public static float[] Factors(Stroke s, int lastIdx, float frac)
    {
        if (!Eligible(s)) return null;
        if (lastIdx < 0) return Factors(s);
        var f = Compute(s, lastIdx, frac);
        return IsUniform(f) ? null : f;
    }

    /// <summary>末段速度（px/ms）；时间不可用（dt≤0 / 全相同）返回 NaN → 全 1。</summary>
    public static float EndSpeed(Stroke s)
    {
        var pts = s.Points;
        int n = pts.Count;
        // 从末点往回扫：累计时间 ≥ WindowMs 即停，但**至少取末 3 段**（点密时窗口内段更多；
        // 点稀时 3 段可能已超过 100ms）。dt≤0 的退化段跳过（不计段数、继续往前）。
        double dist = 0, dtSum = 0;
        int used = 0;
        bool crossedPause = false;
        for (int i = n - 1; i >= 1; i--)
        {
            double dts = pts[i].T - pts[i - 1].T;
            if (dts <= 0) continue;
            // **跨过停顿就不算收笔速度**：写字中途顿笔（停 200ms 再抬笔）之后的那点位移
            // 会被算成"末段飞一样的快"，结果把整笔尾巴削掉一大段——方向刚好反了。
            // 窗口里出现一段 ≥ 半个窗口的间隔，说明这一段不是"连续书写"，直接不加笔锋。
            if (used > 0 && dts >= WindowMs * 0.5) crossedPause = true;
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            dist += Math.Sqrt(dx * dx + dy * dy);
            dtSum += dts;
            used++;
            if (dts >= WindowMs * 0.5) crossedPause = true;
            if (used >= 3 && dtSum >= WindowMs) break;
        }
        if (used == 0 || dtSum <= 0 || crossedPause) return float.NaN;
        return (float)(dist / dtSum);
    }

    /// <summary>尖端系数下限（供自检用）：max(MinFactor, 按基宽抬升的 floorK)，封顶 TipFloorCap。</summary>
    public static float TipFloorFactor(float width)
    {
        float floorK = (float)(Math.Max(MinTipHalfPx, MinTipHalfWidths * width)
                               / Math.Max(1e-3, width * 0.5));
        return Math.Max(MinFactor, Math.Min(floorK, TipFloorCap));
    }

    /// <summary>整条系数数组（可能全 1）；速度低于 VMin → 全 1。</summary>
    private static float[] Compute(Stroke s, int lastIdx, float frac)
    {
        var pts = s.Points;
        int n = pts.Count;
        var outp = new float[n];
        for (int i = 0; i < n; i++) outp[i] = 1f;

        double speed = EndSpeed(s);
        if (!(speed > VMin)) return outp;   // 慢速 / 时间不可用：等宽

        double t = Math.Clamp((speed - VMin) / (VMax - VMin), 0.0, 1.0);
        double eased = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);   // ease-out-cubic
        double tail = eased * TailFactor * s.Width;
        // 保底：速度明显越过门槛时，尾长至少留 MinTailWidthsRelative×Width——
        // 粗笔按 0.8× 收尾会显得"剁"，留 1.4× 让斜切变缓（8px 笔本来就已超这个比例）。
        if (eased > MinTailEase) tail = Math.Max(tail, MinTailWidthsRelative * s.Width);
        tail = Math.Min(tail, MaxTailWidths * s.Width);            // 按线宽封顶，防细笔变针
        if (tail <= 0) return outp;

        // 逐点累计弧长（cum = 到第 i 点的路径长）。
        var cum = new double[n];
        cum[0] = 0;
        for (int i = 1; i < n; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        // "末端"：整笔 = 末点；前缀 = lastIdx + frac×下一段（超出末点就钳回末点）。
        double endArc;
        if (lastIdx >= 0)
        {
            int li = Math.Clamp(lastIdx, 0, n - 1);
            double seg = li + 1 < n ? cum[li + 1] - cum[li] : 0.0;
            endArc = Math.Min(cum[n - 1], cum[li] + seg * Math.Clamp(frac, 0.0, 1.0));
        }
        else
        {
            endArc = cum[n - 1];
        }
        if (endArc <= 0.001) return outp;                          // 原地抖动的点堆：不锥

        for (int i = 0; i < n; i++)
        {
            double remain = endArc - cum[i];                       // 距末端的弧长
            if (remain < 0) remain = 0;
            if (remain >= tail) { outp[i] = 1f; continue; }
            double u = remain / tail;                              // 0 = 末端（最尖）
            outp[i] = (float)TaperNumber(u);
            if (outp[i] < MinFactor) outp[i] = MinFactor;
        }

        // 细笔的"针尖"：系数 0.15 乘到 8px 的笔上只剩 0.6px 半宽，屏幕上就是一根针。
        // 按**基宽**把末段系数抬到"至少 MinTipHalfPx 的最小半径"，即
        //   k >= max(MinTipHalfPx, MinTipHalfWidths × Width) / (Width × 0.5)
        // —— 只抬末段（其中已经 < 1 的那些点），中段仍是 1。
        float floorK = Math.Min(TipFloorFactor(s.Width), 1f);
        for (int i = 0; i < n; i++)
            if (outp[i] < floorK) outp[i] = Math.Min(floorK, 1f);
        return outp;
    }

    /// <summary>尾部系数曲线（按 u；0 = 末端、1 = 锥形起点）。</summary>
    private static double TaperNumber(double u)
    {
        // 两侧微凹的笔锋轮廓：宽度 ∝ u^0.62（指数明显 < 1 → 先缓后急，靠末端才快速收到尖；
        // 0.85 在 18px 粗线上被像素量化抹平，看不出凹）。
        // 尾部起点仍用一段 Hermite 把"等宽段 → 收细段"的硬折角抹平（u=1 处斜率 0）。
        const double Knee = 0.9;
        const double P = 0.62;
        if (u <= Knee) return Math.Pow(u, P);
        double k0 = Math.Pow(Knee, P);
        double m0 = P * Math.Pow(Knee, P - 1) * (1 - Knee);    // 换算到 t 的斜率
        double t = (u - Knee) / (1 - Knee);
        double t2 = t * t, t3 = t2 * t;
        return k0 * (2 * t3 - 3 * t2 + 1) + m0 * (t3 - 2 * t2 + t) - 2 * t3 + 3 * t2;
    }

    private static bool IsUniform(float[] f)
    {
        if (f == null) return true;
        for (int i = 0; i < f.Length; i++)
            if (f[i] < 0.999f) return false;
        return true;
    }
}
