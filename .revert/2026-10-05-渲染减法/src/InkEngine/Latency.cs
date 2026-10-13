using System.Diagnostics;
using System.Text;

namespace InkEngine;

/// <summary>
/// 一次"笔尖动 → 像素亮"的分段记录，单位毫秒。
///
/// 之所以要**分段**而不是只记一个总数：每一段的"归属"不同，能改的只有其中几段。
///
///   InputToMsg      硬件/系统 → 我们的消息循环拿到它。归操作系统输入栈 + 我们的
///                   消息队列。**队列这一段是我们的责任**：Present 阻塞时没人抽消息，
///                   笔就得排队，这是自己造成的延时。
///   MsgToPresent    我们处理这条消息 + 画一帧的耗时。归我们的渲染代码。
///   PresentBlock    Present 内部阻塞的时间（等垂直同步）。归呈现方式的选择。
///   PresentToDisplay Present 返回 → DXGI 报告这一帧上屏。归 DWM 合成 + 扫描输出，
///                   除非换成委托墨迹轨迹，否则应用改不了它。
///
/// 最后一段来自 IDXGISwapChain::GetFrameStatistics().SyncQPCTime。窗口化的
/// 合成交换链上这个值**是"最近一次真正上屏的帧"的时刻**，不一定正好是我们
/// 这一帧，所以它是带 ±1 帧不确定度的估计值，代码里用 HasDisplay 标出来，
/// 报告里也单独说明。前四段是精确值。
/// </summary>
public struct LatencySample
{
    public double InputToMsgMs;
    public double MsgToPresentMs;
    public double PresentBlockMs;
    public double PresentToDisplayMs;
    public double TotalMs;
    public bool HasDisplay;
    /// <summary>这一帧合并了几个输入采样点（>1 说明输入比渲染快，是正常的）。</summary>
    public int PointsInFrame;
    public ulong PresentCount;
}

/// <summary>一段耗时的分布。分位数用最近秩法（nearest-rank），小样本下不会骗人。</summary>
public readonly struct Dist
{
    public readonly int N;
    public readonly double Mean, P50, P90, P95, P99, Min, Max, Std;

    public Dist(IReadOnlyList<double> xs)
    {
        N = xs.Count;
        if (N == 0) { Mean = P50 = P90 = P95 = P99 = Min = Max = Std = double.NaN; return; }
        var sorted = new double[N];
        for (int i = 0; i < N; i++) sorted[i] = xs[i];
        Array.Sort(sorted);
        double sum = 0;
        for (int i = 0; i < N; i++) sum += sorted[i];
        Mean = sum / N;
        double var = 0;
        for (int i = 0; i < N; i++) { double d = sorted[i] - Mean; var += d * d; }
        Std = Math.Sqrt(var / N);
        Min = sorted[0];
        Max = sorted[N - 1];
        P50 = At(sorted, 0.50);
        P90 = At(sorted, 0.90);
        P95 = At(sorted, 0.95);
        P99 = At(sorted, 0.99);
    }

    private static double At(double[] sorted, double q)
    {
        int idx = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    public override string ToString()
        => N == 0 ? "(无样本)"
         : $"均值 {Mean,6:F2}  中位 {P50,6:F2}  P90 {P90,6:F2}  P95 {P95,6:F2}  P99 {P99,6:F2}  最大 {Max,6:F2}  标准差 {Std,5:F2}";
}

/// <summary>
/// 延时采样器。宿主在测延时的模式下打开它，引擎每画完一帧就记一条。
/// 只做记录和统计，不参与渲染——它自己在热路径上的开销必须可以忽略，
/// 所以 Add 里只做一次 List.Add，所有计算都延迟到 Report 时。
/// </summary>
public sealed class LatencyRecorder
{
    private readonly List<LatencySample> _samples = new();

    public int Count => _samples.Count;
    public IReadOnlyList<LatencySample> Samples => _samples;
    public string Scenario = "";

    /// <summary>采样点之间的墙钟间隔（毫秒），用来判断"输入到不到得了"。</summary>
    private readonly List<double> _inputGaps = new();
    public IReadOnlyList<double> InputGaps => _inputGaps;

    public void Clear()
    {
        _samples.Clear();
        _inputGaps.Clear();
    }

    public void Add(in LatencySample s) => _samples.Add(s);
    public void AddInputGap(double ms) => _inputGaps.Add(ms);

    public Dist InputToMsg => new(Col(s => s.InputToMsgMs));
    public Dist MsgToPresent => new(Col(s => s.MsgToPresentMs));
    public Dist PresentBlock => new(Col(s => s.PresentBlockMs));
    public Dist PresentToDisplay => new(Col(s => s.PresentToDisplayMs, onlyWithDisplay: true));
    public Dist Total => new(Col(s => s.TotalMs));

    private List<double> Col(Func<LatencySample, double> f, bool onlyWithDisplay = false)
    {
        var list = new List<double>(_samples.Count);
        foreach (var s in _samples)
        {
            if (onlyWithDisplay && !s.HasDisplay) continue;
            double v = f(s);
            if (!double.IsNaN(v)) list.Add(v);
        }
        return list;
    }

    /// <summary>
    /// 稳定性：把样本切成前后两半比均值（看有没有"越写越慢"），
    /// 以及超过阈值（默认 2 个 60Hz 刷新周期）的比例。
    /// </summary>
    public string StabilityReport(double spikeThresholdMs = 33.4)
    {
        if (_samples.Count < 8) return "样本太少，判不了稳定性。";
        int half = _samples.Count / 2;
        double a = 0, b = 0;
        for (int i = 0; i < half; i++) a += _samples[i].TotalMs;
        for (int i = half; i < _samples.Count; i++) b += _samples[i].TotalMs;
        a /= half; b /= (_samples.Count - half);

        int spikes = 0;
        foreach (var s in _samples) if (s.TotalMs > spikeThresholdMs) spikes++;

        var sb = new StringBuilder();
        sb.AppendLine($"  前半段均值 {a:F2} ms，后半段均值 {b:F2} ms，漂移 {(b - a):+0.00;-0.00;0.00} ms"
                      + (Math.Abs(b - a) < 2 ? "（无劣化趋势）" : "（有趋势，需要查）"));
        sb.AppendLine($"  超过 {spikeThresholdMs:F1} ms 的帧：{spikes} / {_samples.Count}"
                      + $"（{spikes * 100.0 / _samples.Count:F1}%）");
        if (_inputGaps.Count > 2)
        {
            double sum = 0, min = double.MaxValue, max = 0;
            foreach (var g in _inputGaps) { sum += g; min = Math.Min(min, g); max = Math.Max(max, g); }
            sb.AppendLine($"  输入采样间隔：均值 {sum / _inputGaps.Count:F2} ms，"
                          + $"最短 {min:F2}，最长 {max:F2}"
                          + $"（≈{1000.0 / Math.Max(0.001, sum / _inputGaps.Count):F0} Hz）");
        }
        return sb.ToString();
    }

    public string Report(bool includeDisplay = true)
    {
        if (_samples.Count == 0) return $"[{Scenario}] 没有采到样本。";
        var sb = new StringBuilder();
        sb.AppendLine($"  [{Scenario}] 样本 {_samples.Count} 帧");
        sb.AppendLine($"    笔尖采样 → 收到消息：{InputToMsg}");
        sb.AppendLine($"    笔尖采样 → 调 Present：{MsgToPresent}");
        sb.AppendLine($"    Present 内部阻塞  ：{PresentBlock}");
        if (includeDisplay)
        {
            var d = PresentToDisplay;
            sb.AppendLine(d.N > 0
                ? $"    Present → 上屏    ：{d}  (DXGI，含 ±1 帧不确定度)"
                : "    Present → 上屏    ：不可得（此交换链不支持 GetFrameStatistics）");
        }
        sb.AppendLine($"    合计（笔尖采样 → Present 返回）：{Total}");
        var e2e = new List<double>();
        foreach (var s in _samples)
            if (s.HasDisplay && !double.IsNaN(s.InputToMsgMs))
                e2e.Add(s.InputToMsgMs + s.MsgToPresentMs + s.PresentBlockMs + s.PresentToDisplayMs);
        if (e2e.Count > 0)
            sb.AppendLine($"    端到端（硬件时标 → 上屏）：{new Dist(e2e)}");
        sb.Append(StabilityReport());
        return sb.ToString();
    }

    public void WriteCsv(string path, string scenario)
    {
        bool exists = File.Exists(path);
        var sb = new StringBuilder();
        if (!exists)
            sb.AppendLine("scenario,index,input_to_msg_ms,msg_to_present_ms,present_block_ms,"
                          + "present_to_display_ms,total_ms,has_display,points_in_frame,present_count");
        for (int i = 0; i < _samples.Count; i++)
        {
            var s = _samples[i];
            sb.AppendLine($"{scenario},{i},{s.InputToMsgMs:F3},{s.MsgToPresentMs:F3},"
                          + $"{s.PresentBlockMs:F3},{s.PresentToDisplayMs:F3},{s.TotalMs:F3},"
                          + $"{(s.HasDisplay ? 1 : 0)},{s.PointsInFrame},{s.PresentCount}");
        }
        if (exists) File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        else File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>QPC 计数 ↔ 毫秒。全进程一把尺子，避免各处 Stopwatch 频率换算不一致。</summary>
public static class Qpc
{
    public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
    public static ulong Now => (ulong)Stopwatch.GetTimestamp();
    public static double Ms(ulong from, ulong to) => (to - from) * TicksToMs;
    public static double MsSince(ulong from) => (Now - from) * TicksToMs;
}
