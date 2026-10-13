using System;
using System.Diagnostics;   // Stopwatch
using System.Runtime;       // GCSettings / GCLatencyMode 都在这个命名空间里

namespace InkEngine;

/// <summary>
/// 书写期间的 GC"低延迟档"（低配机防卡顿用）。
///
/// 为什么需要它：.NET 的垃圾回收要**暂停所有线程**才能干活，这个暂停就叫"延迟"。
/// 默认模式（Interactive）允许"前台第 2 代回收"——那是所有回收里最重的一种：
/// 它要扫整个堆，还可能压缩内存，**一次几十毫秒**。如果这一下正好落在书写过程中，
/// 用户看到的就是"笔突然卡了一下"，而且这种**偶发长帧**比"平均慢 2 毫秒"难受得多。
///
/// 微软文档里给的解法就是这个 <see cref="GCLatencyMode.SustainedLowLatency"/>：
/// 它"禁止前台第 2 代回收，只做第 0、1 代和后台第 2 代"，而且"可以使用更长时间"
/// （对比 LowLatency 只能短时间用）。代价是堆会变大、碎片会多一点 —— 也就是
/// **拿内存换延迟**，所以下面只在小段时间里开。
///
/// 用法：一笔按下时 <see cref="Enter"/>，主循环每帧 <see cref="Tick"/>；
/// 最后一笔结束满 <see cref="HoldMs"/> 毫秒后自动退回默认档。
/// 一笔接一笔写的时候，结束时间一直在往后推，所以整节课都待在低延迟档里。
///
/// 两个必须知道的坑（文档里写明的）：
///   1) 这个设置**依赖后台垃圾回收**；如果后台 GC 被关掉了（配置成 Batch 模式），
///      它会失败。所以这里设完要**读回来确认**，而不是"设了就当成了"。
///   2) 低延迟期间第 2 代回收被抑制，**除非系统发来低内存通知**——也就是说
///      在内存紧张的机器上它会被"打断"。所以低配上还得同时管住内存占用
///      （图像张数、缓存上限），光开这一条不够。
/// </summary>
internal static class GcLatency
{
    /// <summary>总开关。<c>--nogclatency</c> 关掉，用来做"开 / 不开"的对照测量。</summary>
    internal static bool Enabled = true;

    /// <summary>最后一笔结束之后再保持多久（毫秒）。写长板书时这个窗口一直在被推后。</summary>
    internal static double HoldMs = 5000;

    /// <summary>现在是不是真的处于低延迟档（诊断用；不是"我们想让它开"，是"设置读回来是它"）。</summary>
    internal static bool On { get; private set; }

    /// <summary>设置失败的原因（只记第一次）。为 null 表示没失败过。</summary>
    internal static string FailNote { get; private set; }

    /// <summary>进去过几次、退回来几次（诊断用：能看出这个机制到底有没有在工作）。</summary>
    internal static int EnterCount, ExitCount;

    static double _untilMs;
    static GCLatencyMode _orig;      // 原本是什么档，退回来时**原样还回去**
    static bool _origSaved;

    // 自带一个秒表，**不借用引擎的时钟**。
    // 借用的话就得把 NowMs 传进来，而 NowMs 只在主循环那一处刷新；
    // 自检走的是"抽消息＋渲染"那条路，传进来的会是过期的值，"超时退回"就永远验不到
    //（RenderAll 里 MaybeAutoSave 那段注释记过同一个坑）。
    static readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <summary>一笔开始了（按下时调）。顺带把"保持到什么时候"往后推。</summary>
    internal static void Enter()
    {
        _untilMs = _sw.Elapsed.TotalMilliseconds + HoldMs;
        if (On || !Enabled || FailNote != null) return;

        try
        {
            if (!_origSaved) { _orig = GCSettings.LatencyMode; _origSaved = true; }
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            // **读回来确认**：失败的时候 setter 不一定抛异常，也可能只是没生效。
            // （本项目的规矩：不许把"我设了"当成"它生效了"。）
            On = GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency;
            if (!On) FailNote = "设了没生效（后台 GC 可能被关掉了）";
            else EnterCount++;
        }
        catch (Exception ex)
        {
            FailNote = ex.GetType().Name;
            On = false;
        }
    }

    /// <summary>
    /// 每渲染一帧调一次。`busy` = 现在有手势在进行（正在写、正在拖滚动条/框选，即 `_drawing`）。
    ///
    /// **书写期间必须一直保持**，这是自检当场抓出来的：一开始只在"按下"那一刻刷新窗口，
    /// 结果一笔写得比窗口长（自检把窗口调成 150 ms，笔画一拖就超了）就会在**笔画中途**
    /// 退回默认档——也就是"写得越久越容易卡"，正好和目的相反。
    /// 现在只要手势还在进行就一直往后推，停手之后才开始倒计时。
    /// </summary>
    internal static void Tick(bool busy)
    {
        double now = _sw.Elapsed.TotalMilliseconds;
        if (busy) { _untilMs = now + HoldMs; return; }
        if (!On || now < _untilMs) return;
        try
        {
            GCSettings.LatencyMode = _origSaved ? _orig : GCLatencyMode.Interactive;
            On = false;
            ExitCount++;
        }
        catch { /* 退不回去也不该影响写字，静默 */ }
    }

    /// <summary>诊断用的一句话。</summary>
    internal static string Describe()
        => !Enabled ? "关（--nogclatency）"
         : FailNote != null ? $"不可用（{FailNote}）"
         : On ? $"生效中（进 {EnterCount} 次 / 退 {ExitCount} 次）"
         : $"开，待命（进 {EnterCount} 次 / 退 {ExitCount} 次）";
}
