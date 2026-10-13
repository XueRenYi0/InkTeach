using System.Numerics;
using System.Runtime.CompilerServices;

namespace InkEngine;

/// <summary>
/// **ink-stroke-modeler 的引擎接入层**（2026-10-03，第一轮单源对照实验）。
///
/// 纪律（和用户约定的"一个模式 = 一个来源"）：
///   · 算法本体在 <see cref="InkStrokeModeler.cs"/>（1:1 移植，Apache-2.0），这里只做
///     "把一笔 <see cref="Stroke"/> 的采样点翻译成上游的输入流"这件事；
///   · **只做渲染期加工**：不碰 <see cref="Stroke.Points"/>、不进存档、不改命中/撤销；
///   · 默认关（<see cref="Enabled"/> = false）。`--inkmodel` 打开，做 A/B 对照。
///
/// 为什么是"整笔重放 + 前缀缓存"：
///   · 上游的结果是**只增不改**的（README 明说 F(前缀) 是 F(全串) 的子集），所以活笔
///     每帧只喂新增的点、把结果列表留着继续画，不会出现"已经画过的墨又变了"；
///   · 落笔那一刻补一条 Up（触发收笔追赶），之后的几何就是最终形态。
///
/// 缓冲是**静态复用**的（和 <see cref="StrokeSmoothing"/> 同一个理由：渲染单线程非重入）：
/// 谁以后把渲染搬多线程，这里必须改成每线程一份。
/// </summary>
internal static class InkModel
{
    /// <summary>开关（`--inkmodel`）。默认关。</summary>
    public static bool Enabled;

    /// <summary>
    /// 设置版本号。几何缓存必须把它算进缓存键——不然运行时切换开关，
    /// 已经缓存的笔迹不会重画（`--smoothshow` 出图时踩过同款坑）。
    /// </summary>
    public static int Version { get; private set; }
    public static void SetEnabled(bool on)
    {
        if (Enabled == on) return;
        Enabled = on;
        Version++;
    }
    public static void BumpVersion() => Version++;

    /// <summary>模型参数（默认就是上游 README 的示例值；`--inkm*` 可调）。</summary>
    public static InkStrokeModelParams Params = InkStrokeModelParams.Recommended();

    /// <summary>
    /// 合成笔迹（自检/出图里手搓的 Stroke，点的时间戳全是 0）没有可用时标时，
    /// 按这个速度给每个点编一个时间（画布像素/秒）。真实笔迹永远不会走到这条。
    /// </summary>
    public static double SyntheticSpeedPxPerSec = 500;

    /// <summary>是否已经失败并退回（每笔只报一次，避免刷屏）。</summary>
    public static string LastNote = "";

    // ---- 静态复用输出缓冲（x, y, 压力）-------------------------------------
    private static Vector3[] _buffer = new Vector3[1024];
    /// <summary>最近一次 <see cref="Build"/> 的输出点数（0 = 不可用）。</summary>
    public static int ModeledCount { get; private set; }
    public static Vector3 ModeledAt(int i) => _buffer[i];

    // ---- 每笔的状态缓存（模型器 + 输出）-------------------------------------
    private sealed class Cache
    {
        public InkStrokeModel Modeler;
        public readonly List<InkResult> Results = new();
        public int FedCount;
        public bool FedUp;
        public bool Failed;
        public int Version = -1;
        public double[] SyntheticTimes;
        // 时间源：每笔一次定性、永不再改（见 EnsureTimes 的注释）。
        public bool TimeSourceDecided;
        public bool UseSyntheticTimes;
        public double LastFedTime = double.NegativeInfinity;
        // 上一条真喂进去的输入（用于过滤"完全重复的点"——上游对重复输入会直接报错）。
        public bool HasLastFed;
        public float LastFedX, LastFedY, LastFedP;
        public double LastFedT;
    }

    private static readonly ConditionalWeakTable<Stroke, Cache> s_cache = new();

    /// <summary>
    /// 把这一笔建模结果写进静态缓冲。返回 false = 这一笔不走新路（调用方退回旧路径）。
    ///
    /// 不支持的场合（都明确退回，不静默画错）：
    ///   · 不是自由笔迹 / 点数 &lt; 2；
    ///   · 被像素橡皮擦过（擦除区间是按**原始点**切段的，建模点对不上参数）；
    ///   · 自身建模抛错（时间倒流、输入间距过大等）——记一次 <see cref="LastNote"/>。
    /// </summary>
    public static bool Build(Stroke s)
    {
        if (!Enabled) return false;
        return BuildCore(s);
    }

    /// <summary>不考虑 <see cref="Enabled"/> 开关的建模入口（给 <see cref="StrokeMotion"/> 当 M3 后端）。</summary>
    internal static bool BuildCore(Stroke s)
    {
        ModeledCount = 0;
        if (s == null || s.Kind != StrokeKind.Freehand) return false;
        if (s.Points.Count < 2 || s.Erased.Count > 0) return false;

        var cache = s_cache.GetValue(s, _ => new Cache());
        if (cache.Failed) return false;

        if (cache.Modeler == null || cache.Version != Version)
        {
            cache.Modeler = new InkStrokeModel();
            cache.Modeler.Reset(Params);
            cache.Results.Clear();
            cache.FedCount = 0;
            cache.FedUp = false;
            cache.Version = Version;
            cache.SyntheticTimes = null;
        }

        var pts = s.Points;
        try
        {
            EnsureTimes(s, cache);
            if (cache.FedCount == 0)
            {
                Feed(cache, s, pts[0], InkEventType.Down, 0);
                cache.FedCount = 1;
            }
            for (int i = cache.FedCount; i < pts.Count; i++)
                Feed(cache, s, pts[i], InkEventType.Move, i);
            cache.FedCount = pts.Count;

            // 落笔（不再"正在写"）之后补一条 Up：触发收笔追赶，这是上游语义的一部分。
            if (!s.RawWhileLive && !cache.FedUp)
            {
                Feed(cache, s, pts[^1], InkEventType.Up, pts.Count - 1);
                cache.FedUp = true;
            }
        }
        catch (Exception ex)
        {
            cache.Failed = true;
            LastNote = "ink-stroke-modeler 失败，已退回旧路径：" + ex.Message;
            Console.WriteLine(LastNote);
            return false;
        }

        var results = cache.Results;
        if (results.Count < 2) return false;

        if (_buffer.Length < results.Count) Array.Resize(ref _buffer, results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            _buffer[i] = new Vector3(r.Position.X, r.Position.Y, r.Pressure < 0 ? 0.5f : r.Pressure);
        }
        ModeledCount = results.Count;
        Builds++;
        if (Builds == 1)
            Console.WriteLine($"[InkModel] 首次建模成功：{results.Count} 点输出（活笔增量喂点 + 前缀缓存）");
        return true;
    }

    /// <summary>累计成功建模的笔数（诊断：确认 `--inkmodel` 真的走了新路而不是静默退回）。</summary>
    public static long Builds;

    private static void Feed(Cache cache, Stroke s, InkPoint p, InkEventType type, int index)
    {
        float pressure = s.HasPressure ? p.P : PressureSim.PointPressureOrUnknownAt(s, index);
        double time = cache.SyntheticTimes[index];

        // **重复点过滤**：上游把"和上一条完全相同的输入"视为非法（README 要求调用方
        // 先过滤）。你们的采样在慢速/合成输入下会连着报同坐标同时刻——原样喂进去
        // 每一笔都会整条失败。只跳过完全相同的点，不做任何其它处理。
        if (cache.HasLastFed && type == InkEventType.Move &&
            p.X == cache.LastFedX && p.Y == cache.LastFedY &&
            time == cache.LastFedT && pressure == cache.LastFedP)
            return;

        cache.HasLastFed = true;
        cache.LastFedX = p.X;
        cache.LastFedY = p.Y;
        cache.LastFedT = time;
        cache.LastFedP = pressure;
        cache.LastFedTime = time;

        var input = InkInput.Make(
            type, new InkVec2(p.X, p.Y), time,
            // 没有真实压感的笔迹给 -1（上游语义"未知"），别拿 0.5 冒充真实压力。
            pressure: pressure);
        cache.Modeler.Update(input, cache.Results);
    }

    /// <summary>
    /// 时间轴准备（2026-10-04 修复"活笔变成直线"）：
    ///   · **时间源每笔一次定性、永不再改**：任何一点有非零时间戳 → 用真实时间；
    ///     全为零（自检/出图手搓的 Stroke）→ 按弧长合成。
    ///     之前的 bug：第一帧只有一个时间点（span=0）被误判成合成，第二帧又切回真实
    ///     时间 → 模型器收到的输入时间倒流，窗口积分把不相邻的点平均到一起，输出飞掉。
    ///   · 真实时间**钳成非递减**（设备时标跨消息可能轻微回退；上游要求输入时间非递减）。
    /// </summary>
    private static void EnsureTimes(Stroke s, Cache cache)
    {
        var pts = s.Points;
        if (cache.SyntheticTimes == null || cache.SyntheticTimes.Length < pts.Count)
            cache.SyntheticTimes = new double[Math.Max(pts.Count, 16)];

        if (!cache.TimeSourceDecided)
        {
            if (pts.Count >= 2)
            {
                // 判据是"时间有没有变化"，不是"是不是非零"：
                //   · 全部时间戳相同（自检注入 / 合并点 / QPC 不可用）→ 合成时间；
                //   · 跨点有变化且非递减 → 真实时间；
                //   · 出现回退 → 也用合成时间（避免喂进模型器的时间倒流）。
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
                // 只有一个点：先按 0 喂，下一帧拿到第二个点再定性。
                // 若最终判为真实时间，后续时间戳（引擎时钟，>0）仍然非递减——不会倒流。
                cache.SyntheticTimes[0] = 0;
                return;
            }
        }

        if (cache.UseSyntheticTimes)
        {
            double t = 0;
            cache.SyntheticTimes[0] = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
                t += Math.Sqrt(dx * dx + dy * dy) / Math.Max(1.0, SyntheticSpeedPxPerSec);
                cache.SyntheticTimes[i] = t;
            }
        }
        else
        {
            for (int i = 0; i < pts.Count; i++)
            {
                double t = pts[i].T * 0.001;
                if (t < cache.LastFedTime) t = cache.LastFedTime;   // 非递减
                cache.SyntheticTimes[i] = t;
            }
        }
    }

    // ---- 供命令行调参（`--inkm*`，只影响新的一笔/重画）------------------------
    public static void ApplyParamsFromArgs(string[] args)
    {
        Params = InkStrokeModelParams.Recommended();
        double? wobbleScale = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--inkmtimeout" when double.TryParse(args[i + 1], out var v1) && v1 >= 0:
                    Params.WobbleSmootherParams.Timeout = v1; break;
                case "--inkmspeedscale" when double.TryParse(args[i + 1], out var v2) && v2 > 0:
                    wobbleScale = v2; break;
                case "--inkmspring" when float.TryParse(args[i + 1], out var v3) && v3 > 0:
                    Params.PositionModelerParams.SpringMassConstant = v3; break;
                case "--inkmdrag" when float.TryParse(args[i + 1], out var v4) && v4 >= 0:
                    Params.PositionModelerParams.DragConstant = v4; break;
                case "--inkmrate" when double.TryParse(args[i + 1], out var v5) && v5 > 0:
                    Params.SamplingParams.MinOutputRate = v5; break;
            }
        }
        // 速度阈值是"位置单位/秒"：本引擎的位置单位是画布物理像素。上游 README 那套
        // 默认值只在"几乎停住"时生效（那是 wobble 平滑的本来目的：治停笔时的量化抖动）。
        // 需要放大时用 --inkmspeedscale。
        if (wobbleScale.HasValue)
        {
            Params.WobbleSmootherParams.SpeedFloor *= (float)wobbleScale.Value;
            Params.WobbleSmootherParams.SpeedCeiling *= (float)wobbleScale.Value;
        }
        BumpVersion();
    }
}
