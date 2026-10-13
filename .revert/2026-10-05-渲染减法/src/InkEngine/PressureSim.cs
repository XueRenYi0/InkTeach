using System.Runtime.CompilerServices;

namespace InkEngine;

/// <summary>
/// 无压感笔迹的**模拟压力 ＋ 起收笔锥化**（2026-10-05 实验，`--simpressure` / `--pfpressure` / `--simtaper`）。
///
/// 压力有两个来源，一个档只用其一：
///
/// **`--simpressure` = Xournal++ 的 `pressureGuessing`**（GPL-2.0+ 上游，**只参考思路自实现**；
/// 上游文件：`src/core/gui/inputdevices/PenInputHandler.cpp` 的
/// `inferPressureValue` / `filterPressure`）：
///   · 只对"设备没有有效压力"的笔迹生效（鼠标 / 触摸 / 关 Windows Ink 的笔）；
///   · 逐点因果：反速度 = (时标差 ÷ 10) ÷ (距离 + 0.001)，时标差单位 ms、除 10 变厘秒；
///   · p = π/2 + atan(反速度 × 3.14 − 1.3)；
///   · 平滑：p = min(p, 2)/5 + 上次 p × 4/5（EMA，权重 1/5，起笔 last=0）；
///   · 静止特判（两点重合）：p = sqrt(dt/10) − 0.1（dt 是厘秒，dt/10 即秒）；
///   · 输出 (p × 1.1 + 0.8) / 2，再 max(下限 0.05, × 倍率 1.0)
///     （上游 minimumPressure / pressureMultiplier 的默认值）。
///
/// **`--pfpressure` = perfect-freehand 的 `simulatePressure`**（MIT 上游，已署名；
/// ClassIn 那种"等速写到末尾不衰减、末尾甩速变细"的口径）：
/// `sp = min(1, 距离 ÷ 笔宽)`、目标 `rp = min(1, 1 − sp)`、每点移动 `sp × 0.275`、起笔 0.25；
/// 距离用 streamline 低通后的位置（`t = 0.15 + (1 − streamline) × 0.85`）算，只影响测速。
///
/// 外壳适配（Xournal++ 档，其余一字不改）：
///   ① 距离换算到上游的 72dpi 页面单位：d72 = distPx × 72 / (96 × DpiScale)——
///      这样 100% / 200% 屏上同一物理速度得到同一手感（上游页面单位是 1/72 英寸）；
///   ② 时标差为负（设备时标回退）钳成 0；上游用无符号整数会回绕成一个巨值；
///   ③ 时间不可用（全 0 / 全同，自检语料）→ 整条回退 0.5（等于等宽），不给假压力；
///   ④ **量纲映射**：上游的输出是"线宽倍数"语义（运动中稳态约 0.761~1.5，静止更粗），
///      我们的压力域是 0..1（0.5 = 档位宽度）。把上游**运动区间**线性压进
///      [0.5 − <see cref="Depth"/>, 0.5 + <see cref="Depth"/>]：
///      默认 Depth=0.20 → 0.30~0.70 → **0.6×~1.4× 档位宽度**（与真笔的常用区间一致），
///      差异不会被 0.10/2.0 两端的夹子吃掉；静止特判的更大值被夹在上端。
///   ⑤ pf 档的映射：`p_ours = 0.5 − thinning × (0.5 − p)`，与上游半径公式
///      `radius = size × (0.5 − thinning × (0.5 − p))` 在 `size = 线宽` 时一致（见 ComputeBasePf）。
///
/// **起收笔锥化**（`--simtaper`）单源 = perfect-freehand 的 `start/end taper`（MIT，tldraw 在用）：
/// 半径乘 `min(起点缓动, 终点缓动)`，`t = 距端点路径长 ÷ 锥长`，起点缓动 `t(2−t)`、
/// 终点缓动 `1−(1−t)³`；活笔时当前笔尖就是锥的末端（继续写会"长回去"）。
/// 可单独开（等宽 ＋ 笔锋），也可与上面的模拟压力组合。
///
/// 只做渲染期加工：不进 <see cref="Stroke.Points"/>、不进存档；导出 / 回放按同一算法重算，
/// 结果一致；命中 / 撤销 / 橡皮一概看不见它。
/// </summary>
internal static class PressureSim
{
    /// <summary>`--simpressure`：总开关（**默认关**，实验）。</summary>
    public static bool Enabled;
    /// <summary>压力下限（上游 `minimumPressure` 默认 0.05）。</summary>
    public static float Minimum = 0.05f;
    /// <summary>压力倍率（上游 `pressureMultiplier` 默认 1.0）。</summary>
    public static float Multiplier = 1.0f;
    /// <summary>
    /// 模拟压力的**深度**（围绕我们 0.5 的对称范围，见文件头适配④）：
    /// 0.20 → 0.6×~1.4× 档位宽度。`--simpressdepth N` 可调（0.05~0.45）。
    /// </summary>
    public static float Depth = 0.20f;
    /// <summary>
    /// 起笔锥化长度（画布像素，0 = 关；`--simtaper A[,B]`）。
    /// 单源：perfect-freehand 的 `start.taper`（MIT，见 THIRD-PARTY-NOTICES）。
    /// </summary>
    public static float TaperStartPx;
    /// <summary>收笔锥化长度（画布像素，0 = 关；语义同 <see cref="TaperStartPx"/>）。</summary>
    public static float TaperEndPx;
    /// <summary>有没有任何一端在锥化（渲染开关 / 缓存版本判断用）。</summary>
    public static bool HasTaper => TaperStartPx > 0f || TaperEndPx > 0f;
    /// <summary>屏幕 DPI 缩放（物理 DPI ÷ 96），由引擎在窗口建好后写入。</summary>
    public static double DpiScale = 1.0;

    // ---- perfect-freehand 口径的压力模型（`--pfpressure`，2026-10-05）--------
    /// <summary>压力模型：false = Xournal++ pressureGuessing；true = perfect-freehand simulatePressure。</summary>
    public static bool UsePf;
    /// <summary>pf：压力→粗细强度（上游 `thinning` 默认 0.5）。</summary>
    public static float PfThinning = 0.5f;
    /// <summary>pf：位置低通强度（**只用于测速**，上游 `streamline` 默认 0.5）。</summary>
    public static float PfStreamline = 0.5f;
    /// <summary>上游常量：每点压力变化率 `RATE_OF_PRESSURE_CHANGE`。</summary>
    private const double PfRate = 0.275;
    /// <summary>上游常量：起笔压力 `DEFAULT_FIRST_PRESSURE`（防"胖开头"）。</summary>
    private const double PfFirstPressure = 0.25;

    // ---- 速度门控的末尾收尖（`--flicktip L[,vMin]`，2026-10-05）-------------
    /// <summary>`--flicktip`：只在**末尾速度**超过门槛时才把最后一段收尖（默认关）。</summary>
    public static bool FlickTip;
    /// <summary>收尖长度（画布像素；`--flicktip L`）。</summary>
    public static float FlickTipPx;
    /// <summary>触发门槛（px/ms；默认 0.5 = 500px/s 的甩速）。</summary>
    public static float FlickTipMinSpeed = 0.5f;

    /// <summary>参数 / 开关版本：笔画缓存键要带上它（对照实验中途拨开关用）。</summary>
    public static int Version { get; private set; }
    public static void BumpVersion() => Version++;

    private sealed class Cache
    {
        public int Revision = -1;
        public int Version = -1;
        public float[] P;
    }

    private static readonly ConditionalWeakTable<Stroke, Cache> s_cache = new();

    /// <summary>
    /// 这一笔该不该走这条增强管道：**没有真实压力** + 笔 + 自由笔迹 + 至少两点，
    /// 且**至少开了一项**（模拟压力 `--simpressure` 或起收笔锥化 `--simtaper`）。
    /// 高亮笔 / 激光笔按现有约定忽略压力，不参与。
    /// </summary>
    public static bool Eligible(Stroke s)
        => s != null && s.Tool == Tool.Pen && s.Kind == StrokeKind.Freehand && s.Points.Count >= 2
           && (FlickTip || ((Enabled || HasTaper) && !s.HasPressure));

    /// <summary>取整条的模拟压力数组（按 Revision 缓存）；不适用时返回 null。</summary>
    public static float[] TryArray(Stroke s)
    {
        if (!Eligible(s)) return null;
        var cache = s_cache.GetValue(s, _ => new Cache());
        if (cache.P == null || cache.P.Length != s.Points.Count
            || cache.Revision != s.Revision || cache.Version != Version)
        {
            cache.P = Compute(s);
            cache.Revision = s.Revision;
            cache.Version = Version;
        }
        return cache.P;
    }

    /// <summary>第 i 点用于渲染的**压力**：真实优先，其次模拟，最后占位 0.5。
    /// 例外：`--flicktip` 开着时**覆盖真实压感**（笔也走"等宽＋末尾收尖"，ClassIn 口径）。</summary>
    public static float PointPressure(Stroke s, float[] sim, int i)
    {
        if (s.HasPressure && !FlickTip) return s.Points[i].P;
        return sim != null && i >= 0 && i < sim.Length ? sim[i] : 0.5f;
    }

    /// <summary>同上，但每次自己去缓存里取（命中判定 / 紧框这类零散调用用）。</summary>
    public static float PointPressureAt(Stroke s, int i)
    {
        if (s.HasPressure && !FlickTip) return s.Points[i].P;
        var sim = TryArray(s);
        return sim != null && i >= 0 && i < sim.Length ? sim[i] : 0.5f;
    }

    /// <summary>模拟压力或"未知"（-1，google/ink 的压力缺省口径；sliding 那条路用）。</summary>
    public static float PointPressureOrUnknown(Stroke s, float[] sim, int i)
    {
        if (s.HasPressure && !FlickTip) return s.Points[i].P;
        return sim != null && i >= 0 && i < sim.Length ? sim[i] : -1f;
    }

    /// <summary>同上，每次自己去缓存里取（零散调用用）。</summary>
    public static float PointPressureOrUnknownAt(Stroke s, int i)
        => PointPressureOrUnknown(s, TryArray(s), i);

    /// <summary>整条数组：基础压力（Xournal++）＋ 起收笔锥化（perfect-freehand），按 Revision 缓存。</summary>
    private static float[] Compute(Stroke s)
    {
        var pts = s.Points;
        int n = pts.Count;
        var outp = new float[n];

        if (UsePf) ComputeBasePf(s, outp);
        else if (Enabled) ComputeBase(s, outp);
        else for (int i = 0; i < n; i++) outp[i] = 0.5f;   // 只开锥化时：等宽打底

        ApplyTaper(pts, outp);
        ApplyFlickTip(pts, outp);
        return outp;
    }

    /// <summary>
    /// perfect-freehand 口径的压力（`simulatePressure.ts` 1:1 行为）：
    ///   `sp = min(1, 距离 ÷ 笔宽)`；目标 `rp = min(1, 1 − sp)`；
    ///   `p = min(1, p + (rp − p) × (sp × 0.275))`；起笔 `p = 0.25`。
    /// 距离用 **streamline 低通后的位置** 算（`getStrokePoints` 的口径：
    /// `t = 0.15 + (1 − streamline) × 0.85`，只平滑测速，不动渲染几何）。
    /// 特点：等速写 → 压力收敛到定值（宽度不变）；末尾大步长甩速 → sp→1、目标→0，
    /// 每点掉 27.5% → 末尾明显变细（ClassIn 那种"甩出去收尖"）。
    /// 映射到我们的压力域：`p_ours = 0.5 − thinning × (0.5 − p)`，
    /// 与上游半径公式 `radius = size × (0.5 − thinning × (0.5 − p))` 在 `size = 线宽` 时一致。
    /// </summary>
    private static void ComputeBasePf(Stroke s, float[] outp)
    {
        var pts = s.Points;
        int n = pts.Count;
        double t = 0.15 + (1.0 - Math.Clamp(PfStreamline, 0f, 1f)) * 0.85;
        double size = Math.Max(0.5, s.Width);

        double sx = pts[0].X, sy = pts[0].Y;   // streamline 后的位置（首点原样）
        double px = sx, py = sy;
        double pressure = PfFirstPressure;
        outp[0] = MapPf(pressure);

        for (int i = 1; i < n; i++)
        {
            sx += (pts[i].X - sx) * t;
            sy += (pts[i].Y - sy) * t;
            double dx = sx - px, dy = sy - py;
            px = sx;
            py = sy;

            double distance = Math.Sqrt(dx * dx + dy * dy);
            double sp = Math.Min(1.0, distance / size);
            double rp = Math.Min(1.0, 1.0 - sp);
            pressure = Math.Min(1.0, pressure + (rp - pressure) * (sp * PfRate));
            outp[i] = MapPf(pressure);
        }
    }

    /// <summary>pf 压力 → 我们的压力域（见 <see cref="ComputeBasePf"/> 的映射说明）。</summary>
    private static float MapPf(double p)
        => (float)Math.Max(Minimum, 0.5 - PfThinning * (0.5 - p));

    /// <summary>按上游 `inferPressureValue` 的顺序重放整条笔迹（离线 / 活笔都是同一份值）。</summary>
    private static void ComputeBase(Stroke s, float[] outp)
    {
        var pts = s.Points;
        int n = pts.Count;

        // 时间可用性：和模型的 EnsureTimes 同一口径（有没有变化 + 非递减）。
        bool varying = n >= 2 && pts[^1].T - pts[0].T > 0.5f;
        if (varying)
        {
            for (int i = 1; i < n; i++)
                if (pts[i].T < pts[i - 1].T) { varying = false; break; }
        }
        if (!varying)
        {
            for (int i = 0; i < n; i++) outp[i] = 0.5f;   // 见文件头适配③
            return;
        }

        double dpi = DpiScale > 0.1 ? DpiScale : 1.0;
        double to72 = 72.0 / (96.0 * dpi);               // 适配①

        double lastPressure = 0.0;                        // 上游 onButtonPress：lastPressure = 0
        for (int i = 0; i < n; i++)
        {
            double dtCs = i == 0 ? 0.0 : (pts[i].T - pts[i - 1].T) / 10.0;
            if (dtCs < 0) dtCs = 0;                       // 适配②

            double distance = 0.0;
            if (i > 0)
            {
                double dx = (pts[i].X - pts[i - 1].X) * to72;
                double dy = (pts[i].Y - pts[i - 1].Y) * to72;
                distance = Math.Sqrt(dx * dx + dy * dy);
            }

            double newPressure;
            if (distance == 0)
            {
                // 静止特判（上游原样，且**不走 EMA**——上游那一行随后被覆盖）
                newPressure = Math.Sqrt(dtCs / 10.0) - 0.1;
            }
            else
            {
                double inverseSpeed = dtCs / (distance + 0.001);
                newPressure = Math.PI / 2.0 + Math.Atan(inverseSpeed * 3.14 - 1.3);
                newPressure = Math.Min(newPressure, 2.0) / 5.0 + lastPressure * 4.0 / 5.0;
            }

            lastPressure = newPressure;
            double filtered = (newPressure * 1.1 + 0.8) / 2.0;

            // 适配④：上游"运动中"的稳态区间（极快 0.761 ~ 最慢 1.5；起笔瞬态 0.345
            // 被夹在下端）→ 我们的压力域 0.5±Depth（默认 0.30~0.70 → 0.6×~1.4× 档位宽度）。
            const double xpFast = 0.761, xpSlow = 1.5;
            double t = (filtered - xpFast) / (xpSlow - xpFast);
            double ours = 0.5 - Depth + Math.Clamp(t, 0.0, 1.0) * (2.0 * Depth);
            outp[i] = (float)Math.Max(Minimum, ours * Multiplier);
        }
    }

    /// <summary>
    /// 起收笔锥化（perfect-freehand 的 `start/end taper` 口径，MIT）：
    /// 半径乘 `min(起点系数, 终点系数)`；`t = 距端点路径长 ÷ 锥长`，
    /// 起点缓动 `t(2−t)`、终点缓动 `1−(1−t)³`（都是渐入，不硬折）。
    /// 只开锥化不开模拟压力时 = "等宽字 + 笔锋"；活笔时当前笔尖就是锥的末端（pf 原样）。
    /// </summary>
    private static void ApplyTaper(List<InkPoint> pts, float[] outp)
    {
        if (!HasTaper) return;
        int n = pts.Count;

        double total = 0;
        for (int i = 1; i < n; i++)
            total += Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X)
                               + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));
        if (total <= 0.001) return;   // 原地抖动的点堆：不锥，免得整条变细线

        double run = 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
                run += Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X)
                                 + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));

            double kStart = 1, kEnd = 1;
            if (TaperStartPx > 0f)
            {
                double t = Math.Clamp(run / TaperStartPx, 0.0, 1.0);
                kStart = t * (2 - t);
            }
            if (TaperEndPx > 0f)
            {
                double t = Math.Clamp((total - run) / TaperEndPx, 0.0, 1.0);
                double u = 1 - t;
                kEnd = 1 - u * u * u;
            }
            outp[i] = (float)Math.Max(Minimum, outp[i] * Math.Min(kStart, kEnd));
        }
    }

    /// <summary>
    /// **速度门控的末尾收尖**（`--flicktip L[,vMin]`）：
    /// 锥形/缓动用 perfect-freehand 的 end taper（`1−(1−t)³`，t = 距末端的路径长 ÷ L），
    /// 但**只在末尾速度 ≥ vMin 时才启用**——等速/慢速写到末尾宽度不变，快速甩出去才收尖。
    ///
    /// 末尾速度 = 最后至多 3 段的总距离 ÷ 总时差（px/ms；时差 ≤ 0 的段跳过）。
    /// 门控不是上游行为：pf 的 taper 是固定长度、不看速度；ClassIn 闭源。这是按用户
    /// 明确需求（"只有末尾甩速才收尖"）在 pf 锥形之上加的独立组合档，见计划文档。
    /// </summary>
    private static void ApplyFlickTip(List<InkPoint> pts, float[] outp)
    {
        if (!FlickTip || FlickTipPx <= 0f || pts.Count < 4) return;
        int n = pts.Count;

        // 末尾速度（最后至多 3 段）
        double dist = 0, dtMs = 0;
        int used = 0;
        for (int i = n - 1; i >= 1 && used < 3; i--)
        {
            double dt = pts[i].T - pts[i - 1].T;
            if (dt <= 0) continue;
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            dist += Math.Sqrt(dx * dx + dy * dy);
            dtMs += dt;
            used++;
        }
        if (used == 0 || dtMs <= 0) return;
        double speed = dist / dtMs;
        if (speed < FlickTipMinSpeed) return;      // 等速/慢速收笔：不收尖

        // 末尾路径长
        double total = 0;
        for (int i = 1; i < n; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        if (total <= 0.001) return;

        double run = 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
                run += Math.Sqrt(dx * dx + dy * dy);
            }
            double t = Math.Clamp((total - run) / FlickTipPx, 0.0, 1.0);
            double u = 1 - t;
            outp[i] = (float)Math.Max(Minimum, outp[i] * (1 - u * u * u));
        }
    }
}
