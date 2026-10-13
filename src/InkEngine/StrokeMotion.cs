using System.Numerics;
using System.Runtime.CompilerServices;

namespace InkEngine;

/// <summary>
/// **笔迹运动模型对照台**（2026-10-03，第一轮"细笔抖动到底归谁"实验的总开关）。
///
/// 八个模式，一个模式一个来源，不许混合（用户定的移植纪律）：
///   · <see cref="StrokeMotionMode.Raw"/>     M0：什么都不做（现状 `--nosmooth` 的观感）
///   · <see cref="StrokeMotionMode.Catmull"/> M1：现状——过点 centripetal Catmull-Rom + 角点保护
///   · <see cref="StrokeMotionMode.Sliding"/> M2：google/ink 滑动时间窗（1:1，见 GoogleInkSlidingWindow.cs）
///   · <see cref="StrokeMotionMode.Spring"/>  M3：google/ink-stroke-modeler 弹簧模型（1:1，见 InkModel.cs）
///   · <see cref="StrokeMotionMode.OneEuro"/> M4：1€ 滤波（Casiez 2012，速度自适应低通）
///   · <see cref="StrokeMotionMode.Mean"/>    M5：固定窗口算术平均（Xournal++/GIMP 那条思路，自实现）
///   · <see cref="StrokeMotionMode.Mean2"/>   M6：距离窗平均 ＋ 过点曲线 ＋ 收笔追赶（2026-10-04 定稿默认）
///   · <see cref="StrokeMotionMode.Gauss"/>   M7：Xournal++ VelocityGaussian 速度高斯权重平均
///        ＋收笔二次样条（GPL 上游，只按行为自实现，见 BuildGauss；2026-10-04）
///
/// 渲染期加工，不落盘；M2/M3/M4/M5 活笔也生效（各自结果只增/尾部可改，见各实现注释）。
/// 静态复用输出缓冲（x, y, 压力）——和 <see cref="StrokeSmoothing"/> 同一个理由。
/// </summary>
internal enum StrokeMotionMode
{
    Raw = 0,
    Catmull = 1,
    Sliding = 2,
    Spring = 3,
    OneEuro = 4,
    Mean = 5,
    /// <summary>M5+：距离窗平均 ＋ 过点曲线（角点保护）＋ 收笔追赶（2026-10-04）。</summary>
    Mean2 = 6,
    /// <summary>M7：Xournal++ VelocityGaussian 速度高斯权重平均 ＋ 收笔二次样条（2026-10-04，按行为自实现）。</summary>
    Gauss = 7,
}

internal static class StrokeMotion
{
    /// <summary>
    /// 默认档 = **mean2**（2026-10-04 用户拍板定稿）：距离窗 + 过点曲线 + 收笔追赶。
    /// 其余模式全部保留（`--motion <名字>` 切换，做对照）。
    /// </summary>
    public static StrokeMotionMode Mode = StrokeMotionMode.Mean2;

    /// <summary>模式/参数版本：几何缓存必须算进缓存键。</summary>
    public static int Version { get; private set; }

    public static void SetMode(StrokeMotionMode mode)
    {
        if (Mode != mode)
        {
            Mode = mode;
            Version++;
        }
        // [2026-10-06 修正] 这里原来有一句
        //     StrokeSmoothing.SetEnabled(mode == StrokeMotionMode.Catmull);
        // 理由写的是"避免两条曲线叠加"。但它有两个问题：
        //
        //  ① **和文档矛盾**：StrokeSmoothing 的文件头与 Engine.Run 的注释都写着
        //     "默认开（2026-09-28 用户拍板：默认开也没关系）"；这一句让默认档
        //     （mean2）下 Enabled 恒为 false，等于把"默认开"变成"永远关"。
        //  ② **它管的那条路根本没有"第二条曲线"**：Enabled 只被
        //     `BuildCenterlineCore` / `BuildPressureSegments` 的**非模型路径**读
        //     （被橡皮擦过的笔、回放前缀、建模失败的笔）。那条路没有模型输出，
        //     也就无所谓叠加——把它关掉的结果是**这些笔直接退化成折线**。
        //
        // 所以：Enabled 现在只由 `--nosmooth` / `--smooth` 控制（见 Engine.Run），
        // 不再跟着运动模式走。
    }

    public static void BumpVersion() => Version++;

    // ---- 参数（命令行可调；默认值分别来自各自上游/文献）----------------------
    /// <summary>M2 滑动窗时长（秒）。google/ink 默认 20ms。</summary>
    public static double SlidingWindowSeconds = 0.020;
    /// <summary>M4 1€ 滤波：最小截止频率（Hz）。Casiez 参考默认 1.0。</summary>
    public static double OneEuroMinCutoff = 1.0;
    /// <summary>M4 1€ 滤波：速度系数。参考默认 0.007（配合 120Hz 量级）。</summary>
    public static double OneEuroBeta = 0.007;
    /// <summary>M4 1€ 滤波：速度信号的截止频率（Hz）。参考默认 1.0。</summary>
    public static double OneEuroDerivativeCutoff = 1.0;
    /// <summary>M5 均值窗：取最近几个采样点求平均（Xournal++ 的 buffersize）。</summary>
    public static int MeanWindow = 4;
    /// <summary>
    /// M5+（mean2）距离窗：只平均"最近这么长一段路径"内的点（画布像素）。
    /// 好处：滞后不再随速度线性增长，而是恒定在约半个窗口；采样率高低也不影响手感。
    /// 默认 12 画布像素（200% 屏上约 6 逻辑像素）——比 20 更跟手，慢写仍有足够多的点可平均。
    /// </summary>
    public static float Mean2WindowPx = 12f;
    // ---- M6g（2026-10-08 新增）：高斯权 + 速度自适应 σ ------------------------
    // 用户硬要求：**正常书写速度下墨不许落在笔尖后面（不许有可见的"固定距离"）**，
    // 同时慢写/细字保留去抖。均匀窗的滞后恒为窗长一半（12px 窗 ≈ 6px）；把窗内权重
    // 改成"到笔尖的路径距离做高斯"后，质心滞后 ≈ 0.8σ（同样平滑跨度下约为均匀窗一半），
    // σ 再随速度收缩，快写进一步贴笔。**默认开**（2026-10-08 真机验收：亚像素 + σ3.0→1.2）；
    // 退回旧行为用 `--mean2guniform`（均匀窗），或 `--mean2gsigma/--mean2gfast` 调 σ。
    public static bool Mean2Gauss = true;
    /// <summary>慢速 σ（画布像素）。默认 3.0（2026-10-08 用户真机验收"跟手且不抖"）。</summary>
    public static float Mean2SigmaSlow = 3f;
    /// <summary>快速 σ（画布像素）。</summary>
    public static float Mean2SigmaFast = 1.2f;
    /// <summary>速度分界（px/ms）：≤ Slow 用 σSlow；≥ Fast 用 σFast；中间线性。</summary>
    public static float Mean2SpeedSlow = 0.4f;
    public static float Mean2SpeedFast = 1.6f;
    /// <summary>笔尖混合上限（0~0.6）：out=(1−λ)·高斯均+λ·原始末点；λ 随速度升到它。默认 0=不混。</summary>
    public static float Mean2TipBlendMax;
    /// <summary>M7 Gauss：Xournal++ `stabilizerSigma` 默认 0.5（行为口径，见 StrokeStabilizer.cpp）。</summary>
    public static double GaussSigma = 0.5;
    /// <summary>M7 Gauss：收笔二次样条，对应 Xournal++ `stabilizerFinalizeStroke`（默认 true）。</summary>
    public static bool GaussFinalize = true;
    // [删除 2026-10-05] `Mean2CurveKind/Mean2CurveMode/CurveModeled`（曲线档选择）、
    // `Mean2TipOverlay`（原始点笔尖叠加）、`PredictTip`（预测笔尖叠加）：随停用/删除清理。
    // mean2 的曲线层固定为过点曲线；恢复见 `已停用-渲染实验.md` + `.revert/`。

    // ---- 静态复用输出缓冲 -------------------------------------------------
    private static Vector3[] _buffer = new Vector3[1024];
    public static int Count { get; private set; }
    public static Vector3 At(int i) => _buffer[i];

    /// <summary>M7 的事件（对应 Xournal++ `VelocityEvent`：位置 + 压力 + 速度）。</summary>
    private struct GaussEvent
    {
        public float X, Y, Z;
        public float Velocity;   // px/ms（与上游同单位）
    }

    // ---- 每笔缓存 ---------------------------------------------------------
    private sealed class MotionCache
    {
        public StrokeMotionMode CachedMode = (StrokeMotionMode)(-1);
        public int Version = -1;
        public double[] Times;
        // 时间源：每笔一次定性、永不再改；真实时间钳成非递减（同 InkModel 的修复）。
        public bool TimeSourceDecided;
        public bool UseSyntheticTimes;
        public double LastFedTime = double.NegativeInfinity;

        // M2 滑动窗
        public GiStrokeInputModel Sliding;
        public int SlidingFed;
        public bool SlidingFinished;
        public readonly List<GiModeledStrokeInput> SlidingOut = new();

        // M4 1€
        public OneEuroAxis OneEuroX, OneEuroY;
        public int OneEuroFed;

        // M5 均值窗
        public readonly List<Vector3> MeanWindow = new();
        public int MeanFed;

        // M5+ 距离窗
        public readonly List<Vector3> Mean2Points = new();
        public readonly List<float> Mean2Cum = new();
        public int Mean2Fed;
        public bool Mean2Ended;
        /// <summary>没有叠加笔尖之前的输出点数（每帧重算笔尖前先回退到这里，避免重复叠加）。</summary>
        public int Mean2BaseCount;
        // M6g（2026-10-08）：窗内每点的时标（秒）+ σ/λ 的一阶平滑状态（与 Mean2Points 同步增删）。
        public readonly List<double> Mean2Times = new();
        public float Mean2SigmaState;
        public float Mean2BlendState = -1f;

        // M7 Xournal++ VelocityGaussian（新→旧；被权重判据截掉的永久丢弃）
        public readonly List<GaussEvent> GaussBuf = new();
        public int GaussFed;
        public bool GaussFinalized;
        public double GaussLastTimestamp;   // ms

        // M4/M5 共用输出
        public readonly List<Vector3> Out = new();
    }

    private static readonly ConditionalWeakTable<Stroke, MotionCache> s_cache = new();
    private static readonly GiStrokeInputBatch s_emptyBatch = new();

    /// <summary>
    /// 建模这一笔，写进静态缓冲；返回 false = 该模式不用加工（M0/M1）或这一笔不适用。
    /// </summary>
    public static bool Build(Stroke s, StrokeMotionMode? forceMode = null)
    {
        Count = 0;
        var mode = forceMode ?? Mode;
        if (mode is StrokeMotionMode.Raw or StrokeMotionMode.Catmull) return false;
        if (s == null || s.Kind != StrokeKind.Freehand || s.Points.Count < 2) return false;

        var cache = s_cache.GetValue(s, _ => new MotionCache());
        if (cache.CachedMode != mode || cache.Version != Version || cache.Times == null)
        {
            cache.CachedMode = mode;
            cache.Version = Version;
            cache.Sliding = null;
            cache.SlidingFed = 0;
            cache.SlidingFinished = false;
            cache.SlidingOut.Clear();
            cache.OneEuroX = new OneEuroAxis(OneEuroMinCutoff, OneEuroBeta, OneEuroDerivativeCutoff);
            cache.OneEuroY = new OneEuroAxis(OneEuroMinCutoff, OneEuroBeta, OneEuroDerivativeCutoff);
            cache.OneEuroFed = 0;
            cache.MeanWindow.Clear();
            cache.MeanFed = 0;
            cache.Mean2Points.Clear();
            cache.Mean2Cum.Clear();
            cache.Mean2Fed = 0;
            cache.Mean2Ended = false;
            cache.Mean2BaseCount = 0;
            cache.Mean2Times.Clear();
            cache.Mean2SigmaState = 0f;
            cache.Mean2BlendState = -1f;
            cache.GaussBuf.Clear();
            cache.GaussFed = 0;
            cache.GaussFinalized = false;
            cache.GaussLastTimestamp = 0;
            cache.Out.Clear();
            cache.Times = null;
        }

        EnsureTimes(s, cache);

        switch (mode)
        {
            case StrokeMotionMode.Spring:
            {
                if (!InkModel.BuildCore(s)) return false;
                WriteFromInkModel();
                break;
            }
            case StrokeMotionMode.Sliding:
            {
                if (!BuildSliding(s, cache)) return false;
                WriteFromSliding(cache);
                break;
            }
            case StrokeMotionMode.OneEuro:
            {
                if (!BuildOneEuro(s, cache)) return false;
                WriteFromList(cache.Out);
                break;
            }
            case StrokeMotionMode.Mean:
            {
                if (!BuildMean(s, cache)) return false;
                WriteFromList(cache.Out);
                break;
            }
            case StrokeMotionMode.Mean2:
            {
                if (!BuildMean2(s, cache)) return false;
                WriteFromList(cache.Out);
                break;
            }
            case StrokeMotionMode.Gauss:
            {
                if (!BuildGauss(s, cache)) return false;
                WriteFromList(cache.Out);
                break;
            }
            default:
                return false;
        }
        return Count >= 2;
    }

    // ---- M2：google/ink 滑动窗 ---------------------------------------------

    private static bool BuildSliding(Stroke s, MotionCache cache)
    {
        if (cache.Sliding == null)
        {
            cache.Sliding = new GiStrokeInputModel();
            GiSlidingWindowConfig.WindowSize = SlidingWindowSeconds;
            cache.Sliding.StartStroke(GiInputModelKind.SlidingWindow, brushEpsilon: 0.01f);
        }

        var pts = s.Points;
        if (pts.Count > cache.SlidingFed)
        {
            var batch = new GiStrokeInputBatch();
            for (int i = cache.SlidingFed; i < pts.Count; i++)
            {
                batch.Append(GiStrokeInput.Make(
                    new InkVec2(pts[i].X, pts[i].Y), cache.Times[i],
                    s.HasPressure ? pts[i].P : -1f));
            }
            cache.Sliding.ExtendStroke(batch, s_emptyBatch, cache.Times[pts.Count - 1]);
            cache.SlidingFed = pts.Count;
            cache.LastFedTime = cache.Times[pts.Count - 1];
        }

        if (!s.RawWhileLive && !cache.SlidingFinished)
        {
            cache.Sliding.FinishStrokeInputs();
            cache.SlidingFinished = true;
        }

        cache.SlidingOut.Clear();
        cache.SlidingOut.AddRange(cache.Sliding.ModeledInputs);
        return cache.SlidingOut.Count >= 2;
    }

    private static void WriteFromSliding(MotionCache cache)
    {
        var src = cache.SlidingOut;
        EnsureBuffer(src.Count);
        for (int i = 0; i < src.Count; i++)
            _buffer[i] = new Vector3(src[i].Position.X, src[i].Position.Y,
                src[i].Pressure < 0 ? 0.5f : src[i].Pressure);
        Count = src.Count;
    }

    // ---- M4：1€ 滤波 --------------------------------------------------------

    /// <summary>一维 1€ 滤波（Casiez, CHI 2012；参考实现的标准公式）。</summary>
    internal sealed class OneEuroAxis
    {
        private readonly double _minCutoff, _beta, _derivativeCutoff;
        private double _xPrev;
        private double _dxPrev;
        private bool _hasPrev;

        public OneEuroAxis(double minCutoff, double beta, double derivativeCutoff)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _derivativeCutoff = derivativeCutoff;
        }

        public double Filter(double x, double dt)
        {
            if (!_hasPrev) { _hasPrev = true; _xPrev = x; _dxPrev = 0; return x; }
            if (dt <= 0) return _xPrev;
            double dx = (x - _xPrev) / dt;
            double edx = Alpha(_derivativeCutoff, dt) * dx + (1 - Alpha(_derivativeCutoff, dt)) * _dxPrev;
            double cutoff = _minCutoff + _beta * Math.Abs(edx);
            double xHat = Alpha(cutoff, dt) * x + (1 - Alpha(cutoff, dt)) * _xPrev;
            _xPrev = xHat;
            _dxPrev = edx;
            return xHat;
        }

        private static double Alpha(double cutoff, double dt)
        {
            double tau = 1.0 / (2.0 * Math.PI * cutoff);
            return 1.0 / (1.0 + tau / dt);
        }
    }

    private static bool BuildOneEuro(Stroke s, MotionCache cache)
    {
        var pts = s.Points;
        for (int i = cache.OneEuroFed; i < pts.Count; i++)
        {
            double dt = i == 0 ? 0 : cache.Times[i] - cache.Times[i - 1];
            double fx = cache.OneEuroX.Filter(pts[i].X, dt);
            double fy = cache.OneEuroY.Filter(pts[i].Y, dt);
            cache.Out.Add(new Vector3((float)fx, (float)fy, s.HasPressure ? pts[i].P : 0.5f));
        }
        cache.OneEuroFed = pts.Count;
        if (pts.Count > 0) cache.LastFedTime = cache.Times[pts.Count - 1];
        return cache.Out.Count >= 2;
    }

    // ---- M5：固定窗口算术平均（Xournal++/GIMP 思路的自实现）------------------

    private static bool BuildMean(Stroke s, MotionCache cache)
    {
        var pts = s.Points;
        for (int i = cache.MeanFed; i < pts.Count; i++)
        {
            cache.MeanWindow.Add(new Vector3(pts[i].X, pts[i].Y, s.HasPressure ? pts[i].P : 0.5f));
            if (cache.MeanWindow.Count > Math.Max(1, MeanWindow))
                cache.MeanWindow.RemoveAt(0);

            Vector3 sum = Vector3.Zero;
            foreach (var v in cache.MeanWindow) sum += v;
            cache.Out.Add(sum / cache.MeanWindow.Count);
        }
        cache.MeanFed = pts.Count;
        if (pts.Count > 0) cache.LastFedTime = cache.Times[pts.Count - 1];
        return cache.Out.Count >= 2;
    }

    // ---- M5+（mean2）：距离窗平均 + 收笔追赶 ---------------------------------

    /// <summary>
    /// 距离窗：只保留"最近 <see cref="Mean2WindowPx"/> 像素路径"内的点再取平均。
    ///   · 慢写：窗内点很多 → 强平滑（和 M5 一样甚至更强）；
    ///   · 快写：窗内点少 → 滞后被钳在"半个窗口"，不随速度线性增长；
    ///   · 采样率变化（Windows Ink 开/关）不再改变手感。
    /// 落笔后（!RawWhileLive）追加"半程点 + 真实末点"两步收笔追赶，末端不再短一截。
    /// </summary>
    private static bool BuildMean2(Stroke s, MotionCache cache)
    {
        var pts = s.Points;
        // M6g：高斯权的权尾在 3σ 处已 <0.01；窗裁到 max(原窗, 3σSlow+1) 就够。
        float trimPx = Mean2Gauss ? MathF.Max(Mean2WindowPx, 3f * Mean2SigmaSlow + 1f) : Mean2WindowPx;
        for (int i = cache.Mean2Fed; i < pts.Count; i++)
        {
            var p = new Vector3(pts[i].X, pts[i].Y, s.HasPressure ? pts[i].P : 0.5f);
            float d = cache.Mean2Points.Count == 0
                ? 0f
                : Vector2.Distance(
                    new Vector2(cache.Mean2Points[^1].X, cache.Mean2Points[^1].Y),
                    new Vector2(p.X, p.Y));
            cache.Mean2Points.Add(p);
            cache.Mean2Cum.Add((cache.Mean2Cum.Count == 0 ? 0f : cache.Mean2Cum[^1]) + d);
            cache.Mean2Times.Add(cache.Times != null && i < cache.Times.Length ? cache.Times[i] : 0.0);

            // 从窗口头部裁掉超出距离的点（至少留最后一个）。
            float tail = cache.Mean2Cum[^1];
            int keep = 0;
            while (keep + 1 < cache.Mean2Cum.Count && tail - cache.Mean2Cum[keep] > trimPx)
                keep++;
            if (keep > 0)
            {
                cache.Mean2Points.RemoveRange(0, keep);
                cache.Mean2Cum.RemoveRange(0, keep);
                cache.Mean2Times.RemoveRange(0, keep);
            }

            if (!Mean2Gauss)
            {
                Vector3 sum = Vector3.Zero;
                foreach (var v in cache.Mean2Points) sum += v;
                cache.Out.Add(sum / cache.Mean2Points.Count);
            }
            else
            {
                cache.Out.Add(Mean2GaussWeighted(cache));
            }
        }
        cache.Mean2Fed = pts.Count;
        if (pts.Count > 0) cache.LastFedTime = cache.Times[pts.Count - 1];
        cache.Mean2BaseCount = cache.Out.Count;

        // 收笔追赶：抬笔那一下把滞后补回真实末点（半程 + 末点两步，避免硬折）。
        if (!s.RawWhileLive && !cache.Mean2Ended && cache.Out.Count > 0)
        {
            var lastRaw = new Vector3(pts[^1].X, pts[^1].Y, s.HasPressure ? pts[^1].P : 0.5f);
            var lastOut = cache.Out[^1];
            if (Vector2.Distance(new Vector2(lastOut.X, lastOut.Y),
                                 new Vector2(lastRaw.X, lastRaw.Y)) > 1f)
                cache.Out.Add((lastOut + lastRaw) * 0.5f);
            cache.Out.Add(lastRaw);
            cache.Mean2Ended = true;
        }

        // [删除 2026-10-05] 活笔笔尖叠加（`--predicttip` / `--mean2tip`）：随停用/删除清理。
        return cache.Out.Count >= 2;
    }

    /// <summary>
    /// M6g：窗内"高斯权 + 速度自适应 σ + 可选笔尖混合"的平均（2026-10-08）。
    ///   · 权 w = exp(−(d/σ)²/2)，d = 沿路径到笔尖的距离（笔尖 = 0）——越靠笔尖权越大，
    ///     质心滞后 ≈ 0.8σ；同样 σ 下的"平滑跨度"仍有 2.5σ 量级；
    ///   · σ 按最近一段的实测速度在 [σFast..σSlow] 线性取值，并对 σ 与 λ 做一阶平滑
    ///     （0.25/点），防输出"忽松忽紧"地脉动；
    ///   · 可选 λ（默认 0）：out = (1−λ)·高斯均 + λ·原始末点，λ 随速度升到 TipBlendMax。
    /// d 用**路径距离**（不是欧氏），所以和均匀窗一样"贴着路径走"——圆弧不切角、形状不丢。
    /// </summary>
    private static Vector3 Mean2GaussWeighted(MotionCache cache)
    {
        var pts = cache.Mean2Points;
        var cum = cache.Mean2Cum;
        var times = cache.Mean2Times;
        int n = pts.Count;
        if (n == 1) return pts[0];

        // ---- 速度：最近 ~6 点的路径速度（px/ms）→ σ、λ 的目标值 ----
        float v = float.NaN;
        if (times.Count == n)
        {
            int j = Math.Max(0, n - 6);
            double dtMs = (times[^1] - times[j]) * 1000.0;
            if (dtMs > 0.05) v = (float)((cum[^1] - cum[j]) / dtMs);
        }
        float blendT = 0f;
        float sigmaTarget;
        if (float.IsFinite(v) && v > 0f)
        {
            float t = Math.Clamp((v - Mean2SpeedSlow) / MathF.Max(1e-3f, Mean2SpeedFast - Mean2SpeedSlow), 0f, 1f);
            sigmaTarget = Mean2SigmaSlow + (Mean2SigmaFast - Mean2SigmaSlow) * t;
            blendT = t;
        }
        else
        {
            sigmaTarget = Mean2SigmaSlow;   // 没时标/静止：按慢速（最稳）
        }
        cache.Mean2SigmaState = cache.Mean2SigmaState <= 0f
            ? sigmaTarget
            : cache.Mean2SigmaState + (sigmaTarget - cache.Mean2SigmaState) * 0.25f;
        float sigma = MathF.Max(0.1f, cache.Mean2SigmaState);

        float lambdaTarget = Mean2TipBlendMax <= 0f ? 0f : Mean2TipBlendMax * blendT;
        cache.Mean2BlendState = cache.Mean2BlendState < 0f
            ? lambdaTarget
            : cache.Mean2BlendState + (lambdaTarget - cache.Mean2BlendState) * 0.25f;
        float lambda = Math.Clamp(cache.Mean2BlendState, 0f, 0.6f);

        // ---- 高斯权平均 ----
        float dTip = cum[^1];
        double sw = 0, sx = 0, sy = 0, sz = 0;
        for (int k = 0; k < n; k++)
        {
            float d = dTip - cum[k];
            double w = Math.Exp(-(d / sigma) * (d / sigma) * 0.5);
            if (w < 0.01) continue;         // 权重已可忽略（列表按时间递增，d 递减）
            var p = pts[k];
            sw += w; sx += p.X * w; sy += p.Y * w; sz += p.Z * w;
        }
        if (sw <= 1e-9) return pts[^1];
        var avg = new Vector3((float)(sx / sw), (float)(sy / sw), (float)(sz / sw));

        // ---- 可选：向原始末点混一点（λ 随速度）----
        if (lambda > 0f)
        {
            var tip = pts[^1];
            return avg + (tip - avg) * lambda;
        }
        return avg;
    }

    // ---- M7：Xournal++ VelocityGaussian（速度高斯权重平均 + 收笔二次样条）----
    //
    // 行为口径来自 Xournal++ `src/core/control/tools/StrokeStabilizer.cpp` 的
    // `VelocityGaussian` 与 `Active::finalizeStroke/quadraticSplineTo`（上游 GPL-2.0+；
    // 本仓库只按行为自实现，未复制代码）：
    //   · 每个输入事件记 位置/压力/速度（速度 = 与上一事件的距离 ÷ 两事件时标差 ms；
    //     时标差为 0 记 1——上游原话 "timelaps == 0 → 1"）；
    //   · 从新到旧加权求和：weight = exp(-S²/(2σ²))，S = 比它新的所有事件的**速度和**，
    //     最新一个权重恒为 1；权重 < 0.01 时中断，并把更旧的事件**永久丢弃**；
    //   · 输出加权平均（位置与压力），一进一出；
    //   · 收笔（对应 stabilizerFinalizeStroke，默认 true）：从最后一个输出点向真实末点
    //     接一条二次样条（控制点沿"远离前一个输出点"的方向、长度按上游公式夹在 |BC| 内），
    //     再按上游的递归中点细分展成点（平直判据 1.0001 / 0.3px），最后补上真实末点。
    //
    // 外壳适配（两处，其余一字不改）：
    //   ① 时标为负（设备时标回退）时同样记 1ms——上游用无符号整数会回绕成巨值，
    //      本引擎各模式统一防回退，这里取等效的"忽略这次回退"；
    //   ② 压感平直判据里的 0.1 是上游"压力×线宽"单位，这里按 |Δ压力|×线宽 比较。
    private static bool BuildGauss(Stroke s, MotionCache cache)
    {
        var pts = s.Points;
        for (int i = cache.GaussFed; i < pts.Count; i++)
        {
            float p = s.HasPressure ? pts[i].P : 0.5f;
            double tMs = cache.Times[i] * 1000.0;   // 上游时标单位是 ms

            if (cache.GaussBuf.Count == 0)
            {
                // 对应 recordFirstEvent：第一个事件速度记 0、只入缓冲；
                // 几何上它就是按下点（handler 已经落点），所以我们也直接输出它。
                cache.GaussBuf.Insert(0, new GaussEvent { X = pts[i].X, Y = pts[i].Y, Z = p, Velocity = 0f });
                cache.GaussLastTimestamp = tMs;
                cache.Out.Add(new Vector3(pts[i].X, pts[i].Y, p));
                continue;
            }

            var last = cache.GaussBuf[0];
            double dt = tMs - cache.GaussLastTimestamp;
            if (dt == 0) dt = 1;
            if (dt < 0) dt = 1;                     // 见注释适配①
            float v = (float)(Vector2.Distance(
                new Vector2(last.X, last.Y), new Vector2(pts[i].X, pts[i].Y)) / dt);
            cache.GaussBuf.Insert(0, new GaussEvent { X = pts[i].X, Y = pts[i].Y, Z = p, Velocity = v });
            cache.GaussLastTimestamp = tMs;

            double twoSigmaSq = 2 * GaussSigma * GaussSigma;
            double wsumX = 0, wsumY = 0, wsumZ = 0, wsumW = 0, sumV = 0;
            int k = 0;
            for (; k < cache.GaussBuf.Count; k++)
            {
                double weight = Math.Exp(-(sumV * sumV) / twoSigmaSq);
                if (weight < 0.01) break;
                var g = cache.GaussBuf[k];
                sumV += g.Velocity;
                wsumX += weight * g.X;
                wsumY += weight * g.Y;
                wsumZ += weight * g.Z;
                wsumW += weight;
            }
            if (k < cache.GaussBuf.Count)           // 上游 erase(it, end)：截掉的永久丢弃
                cache.GaussBuf.RemoveRange(k, cache.GaussBuf.Count - k);

            if (wsumW > 0)
                cache.Out.Add(new Vector3(
                    (float)(wsumX / wsumW), (float)(wsumY / wsumW), (float)(wsumZ / wsumW)));
        }

        cache.GaussFed = pts.Count;
        if (pts.Count > 0) cache.LastFedTime = cache.Times[pts.Count - 1];

        // 收笔：对应 Active::finalizeStroke（VelocityGaussian 没有压力再平衡重载，故不写）
        if (!s.RawWhileLive && !cache.GaussFinalized && cache.Out.Count > 0)
        {
            if (GaussFinalize) AppendGaussFinalize(s, cache);
            cache.GaussFinalized = true;
        }
        return cache.Out.Count >= 2;
    }

    /// <summary>M7 收笔：对应 Xournal++ `Active::quadraticSplineTo(getLastEvent())`。</summary>
    private static void AppendGaussFinalize(Stroke s, MotionCache cache)
    {
        var pts = s.Points;
        var B = cache.Out[^1];
        float cx = pts[^1].X, cy = pts[^1].Y;
        float cz = s.HasPressure ? pts[^1].P : B.Z;

        if (cache.Out.Count == 1)
        {
            // 上游 pointCount == 1 → drawEvent(ev)：只有一个输出点时直接补真实末点
            cache.Out.Add(new Vector3(cx, cy, cz));
            return;
        }

        var A = cache.Out[^2];
        float abx = B.X - A.X, aby = B.Y - A.Y;
        float bcx = cx - B.X, bcy = cy - B.Y;
        double normAB = Math.Sqrt(abx * (double)abx + aby * (double)aby);
        double normBC = Math.Sqrt(bcx * (double)bcx + bcy * (double)bcy);
        if (normBC < 1e-12) return;                 // 上游：normBC < eps → 不补
        if (normAB < 1e-12)                         // 上游：normAB < eps → drawEvent(ev)
        {
            cache.Out.Add(new Vector3(cx, cy, cz));
            return;
        }

        double dot = abx * (double)bcx + aby * (double)bcy;
        // 上游：distance = min(|BC|²·|AB| / (2·AB·BC), |BC|)；除零（dot=0）时上游得 inf、被夹成 |BC|
        double distance = dot == 0
            ? normBC
            : Math.Min(Math.Abs(normBC * normBC * normAB / (2 * dot)), normBC);

        bool usePressure = s.HasPressure;           // 对应上游 tool.isPressureSensitive()
        if (usePressure)
        {
            // 上游：coeff = |BC|/2 + distance；B.z = (coeff·A.z + |AB|·C.z) / (|AB| + coeff)
            double coeff = normBC / 2 + distance;
            B.Z = (float)((coeff * A.Z + normAB * cz) / (normAB + coeff));
            cache.Out[^1] = B;                      // 上游 setLastPressure(B.z)
        }

        // 控制点 Q = B.lineTo(A, -distance)：从 B 沿"远离 A"的方向走 distance
        float qx = B.X - (float)(distance / normAB) * abx;
        float qy = B.Y - (float)(distance / normAB) * aby;

        // 二次样条 B→C 升三次：fp = B + 2/3·(Q-B)，sp = C + 2/3·(Q-C)
        float fpx = B.X + (qx - B.X) * 2f / 3f, fpy = B.Y + (qy - B.Y) * 2f / 3f;
        float spx = cx + (qx - cx) * 2f / 3f, spy = cy + (qy - cy) * 2f / 3f;

        // 展点：序列首点是 B（上游 pop_front 掉，因为已经画过），不含终点 C
        var seq = new List<Vector3>(16);
        GaussFlattenSpline(seq, s.Width, usePressure,
            B.X, B.Y, B.Z, fpx, fpy, B.Z, spx, spy, B.Z, cx, cy, cz);
        for (int i = 1; i < seq.Count; i++) cache.Out.Add(seq[i]);
        cache.Out.Add(new Vector3(cx, cy, cz));     // 上游最后 drawSegmentTo(C)
    }

    /// <summary>
    /// M7：把三次贝塞尔按上游 <c>SplineSegment::toPointSequence</c> 展开：
    /// 平直（|B-fp|+|fp-sp|+|sp-C| &lt; 1.0001·|B-C|，或 |B-C| &lt; 0.3px；压感再加
    /// |Δ压力|·线宽 ≤ 0.1）就收，否则在 t=0.5 二分递归。输出的是各段的起点（不含终点 C）。
    /// 压感中间点的 z 用上游公式（对两端的 t=0.5 插值，不是严格几何线性）。
    /// </summary>
    private static void GaussFlattenSpline(List<Vector3> outp, float width, bool usePressure,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float x2, float y2, float z2, float x3, float y3, float z3)
    {
        double l1 = Vector2.Distance(new Vector2(x0, y0), new Vector2(x1, y1));
        double l2 = Vector2.Distance(new Vector2(x1, y1), new Vector2(x2, y2));
        double l3 = Vector2.Distance(new Vector2(x2, y2), new Vector2(x3, y3));
        double l = Vector2.Distance(new Vector2(x0, y0), new Vector2(x3, y3));
        if (l < 0.3 || (l1 + l2 + l3 < 1.0001 * l &&
                        (!usePressure || Math.Abs(z0 - z3) * width <= 0.1)))
        {
            outp.Add(new Vector3(x0, y0, z0));
            return;
        }

        // subdivide(0.5)：de Casteljau
        float b0x = (x0 + x1) * 0.5f, b0y = (y0 + y1) * 0.5f;
        float b1x = (x1 + x2) * 0.5f, b1y = (y1 + y2) * 0.5f;
        float b2x = (x2 + x3) * 0.5f, b2y = (y2 + y3) * 0.5f;
        float c0x = (b0x + b1x) * 0.5f, c0y = (b0y + b1y) * 0.5f;
        float c1x = (b1x + b2x) * 0.5f, c1y = (b1y + b2y) * 0.5f;
        float dx = (c0x + c1x) * 0.5f, dy = (c0y + c1y) * 0.5f;
        float dz = usePressure ? 0.5f * z0 + 0.5f * z3 : z0;   // 上游：t·first.z+(1-t)·second.z

        GaussFlattenSpline(outp, width, usePressure, x0, y0, z0, b0x, b0y, z0, c0x, c0y, z0, dx, dy, dz);
        GaussFlattenSpline(outp, width, usePressure, dx, dy, dz, c1x, c1y, z1, b2x, b2y, z1, x3, y3, z3);
    }

    // ---- 输出缓冲与时间轴 ---------------------------------------------------

    private static void EnsureBuffer(int need)
    {
        if (_buffer.Length < need) Array.Resize(ref _buffer, Math.Max(need, _buffer.Length * 2));
    }

    private static void WriteFromInkModel()
    {
        int n = InkModel.ModeledCount;
        EnsureBuffer(n);
        for (int i = 0; i < n; i++) _buffer[i] = InkModel.ModeledAt(i);
        Count = n;
    }

    private static void WriteFromList(List<Vector3> src)
    {
        EnsureBuffer(src.Count);
        for (int i = 0; i < src.Count; i++) _buffer[i] = src[i];
        Count = src.Count;
    }

    /// <summary>
    /// 时间轴（2026-10-04 修复"活笔变成直线"）：
    ///   · **时间源每笔一次定性、永不再改**：任何一点有非零时间戳 → 真实时间；
    ///     全为零（自检/出图手搓的 Stroke）→ 按弧长合成。
    ///     旧实现的 bug：第一帧 span=0 被误判成合成、第二帧切回真实时间 →
    ///     输入时间倒流 → sliding 窗口把不相邻的点平均到一起，输出飞掉。
    ///   · 真实时间**钳成非递减**（设备时标跨消息可能轻微回退）。
    /// </summary>
    private static void EnsureTimes(Stroke s, MotionCache cache)
    {
        var pts = s.Points;
        if (cache.Times == null || cache.Times.Length < pts.Count)
            cache.Times = new double[Math.Max(pts.Count, 16)];

        if (!cache.TimeSourceDecided)
        {
            if (pts.Count >= 2)
            {
                // 同 InkModel：判据是"时间有没有变化"，全同/回退 → 合成时间。
                bool varying = pts[^1].T - pts[0].T > 0.5;
                if (varying)
                {
                    for (int i = 1; i < pts.Count; i++)
                        if (pts[i].T < pts[i - 1].T) { varying = false; break; }
                }
                cache.UseSyntheticTimes = !varying;
                cache.TimeSourceDecided = true;
            }
            else
            {
                cache.Times[0] = 0;
                return;
            }
        }

        if (cache.UseSyntheticTimes)
        {
            double t = 0;
            cache.Times[0] = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
                t += Math.Sqrt(dx * dx + dy * dy) / Math.Max(1.0, InkModel.SyntheticSpeedPxPerSec);
                cache.Times[i] = t;
            }
        }
        else
        {
            for (int i = 0; i < pts.Count; i++)
            {
                double t = pts[i].T * 0.001;
                if (t < cache.LastFedTime) t = cache.LastFedTime;
                cache.Times[i] = t;
            }
        }
    }

    // ---- 命令行 -------------------------------------------------------------
    //
    // [停用 2026-10-05] 除 `--mean2win` 外的参数都属于已停用的对照档
    // （mean / oneeuro / sliding / gauss / fit / tip）。代码保留，恢复见 `已停用-渲染实验.md`。

    public static void ApplyParamsFromArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                // [停用] case "--motionwin" when int.TryParse(args[i + 1], out var w) && w >= 2:
                //     MeanWindow = Math.Clamp(w, 2, 64); break;
                case "--mean2win" when float.TryParse(args[i + 1], out var m2) && m2 >= 2f:
                    Mean2WindowPx = Math.Clamp(m2, 2f, 200f); break;
                // ---- M6g（2026-10-08）：高斯权 + 速度自适应 ----
                case "--mean2gauss": Mean2Gauss = true; break;
                case "--mean2guniform": Mean2Gauss = false; break;
                case "--mean2gsigma" when float.TryParse(args[i + 1], out var gs0) && gs0 > 0f:
                    Mean2SigmaSlow = Math.Clamp(gs0, 0.5f, 20f); break;
                case "--mean2gfast" when float.TryParse(args[i + 1], out var gf0) && gf0 > 0f:
                    Mean2SigmaFast = Math.Clamp(gf0, 0.3f, 20f); break;
                case "--mean2glo" when float.TryParse(args[i + 1], out var gl0) && gl0 >= 0f:
                    Mean2SpeedSlow = Math.Clamp(gl0, 0f, 10f); break;
                case "--mean2ghi" when float.TryParse(args[i + 1], out var gh0) && gh0 > 0f:
                    Mean2SpeedFast = Math.Clamp(gh0, 0.05f, 20f); break;
                case "--mean2gtip" when float.TryParse(args[i + 1], out var gt0) && gt0 >= 0f:
                    Mean2TipBlendMax = Math.Clamp(gt0, 0f, 0.6f); break;
                // [停用] case "--mean2nocurve":
                //     Mean2CurveMode = Mean2CurveKind.None; break;
                // [停用] case "--mean2fit":
                //     Mean2CurveMode = Mean2CurveKind.Fit; break;
                // [停用] case "--mean2smooth":
                //     Mean2CurveMode = Mean2CurveKind.Smooth; break;
                // [停用] case "--mean2fittol" when float.TryParse(args[i + 1], out var tol) && tol > 0f:
                //     WpfInkFit.TolerancePx = Math.Clamp(tol, 0.05f, 5f); break;
                // [停用] case "--mean2tip":
                //     Mean2TipOverlay = true; break;
                // [停用] case "--mean2notip":
                //     Mean2TipOverlay = false; break;
                // [停用] case "--oneeuro" when TryParsePair(args[i + 1], out var fc, out var beta):
                //     OneEuroMinCutoff = fc; OneEuroBeta = beta; break;
                // [停用] case "--oneeurodc" when double.TryParse(args[i + 1], out var dc) && dc > 0:
                //     OneEuroDerivativeCutoff = dc; break;
                // [停用] case "--slidewin" when double.TryParse(args[i + 1], out var sw) && sw > 0:
                //     SlidingWindowSeconds = Math.Clamp(sw, 0.001, 0.2); break;
                // [停用] case "--gausssigma" when double.TryParse(args[i + 1], out var gs) && gs > 0:
                //     GaussSigma = Math.Clamp(gs, 0.05, 10.0); break;
                // [停用] case "--gaussnofinalize":
                //     GaussFinalize = false; break;
                // [停用] case "--gaussfinalize":
                //     GaussFinalize = true; break;
            }
        }
        BumpVersion();
    }

    private static bool TryParsePair(string text, out double a, out double b)
    {
        a = b = 0;
        var parts = text.Split(':');
        return parts.Length == 2 && double.TryParse(parts[0], out a) && double.TryParse(parts[1], out b);
    }
}
