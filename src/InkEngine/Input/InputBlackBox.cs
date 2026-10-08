using System.IO;
using System.Text;

namespace InkEngine;

/// <summary>
/// **输入黑匣子**（`--reclive &lt;文件&gt;`）：把真正喂进引擎的输入逐条落盘，
/// 供 `--replayinput` 离线 **1:1 回放**——"预测偶尔跳一下"这类说不清、偶发的问题，
/// 从此不需要用户描述：按时间留一份，回放后变成"第几秒第几帧、跳了几像素"。
///
/// 为什么挂在这三层（少了任何一层回放都会失真）：
///   · **Begin** = `BeginFreehandStrokeAt`（起笔：含 ptype / 压力）；
///   · **Sample** = `AddPointCleaned` 的**入参**（净化前 + 引擎 NowMs）——
///     回放时用同一函数喂回去，净化、曲线建模、预测器全部逐字复现；
///   · **End** = `EndStroke`。
/// 回放不需要屏幕/像素：尾的每个数字（供给率、帧间位移、门触发）都从引擎状态算出来。
///
/// 格式（UTF-8 无 BOM；时间 = 引擎 NowMs 毫秒、InvariantCulture）：
///   #InkTeach input v1
///   #version … turndeg … pred …
///   B &lt;t&gt; &lt;x&gt; &lt;y&gt; &lt;p&gt; &lt;ptype&gt;      ← 起笔
///   S &lt;t&gt; &lt;x&gt; &lt;y&gt; &lt;p&gt;             ← 采样（喂进预测器的原始点）
///   E &lt;t&gt;                        ← 收笔
/// 每笔收笔即 flush（同 `--recink` 的规矩：每笔写完就能取文件）。
/// </summary>
internal static class InputBlackBox
{
    private static StreamWriter _sw;
    private static int _sinceFlush;

    public static bool Enabled => _sw != null;

    public static void Open(string path, string header)
    {
        Close();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _sw = new StreamWriter(path, append: false, new UTF8Encoding(false));
        _sw.WriteLine("#InkTeach input v1");
        _sw.WriteLine("#" + header);
        _sw.Flush();               // 表头立即落盘（进程被强杀也留得住）
    }

    public static void Close()
    {
        try { _sw?.Flush(); _sw?.Dispose(); } catch { }
        _sw = null;
    }

    public static void Begin(double t, float x, float y, float p, uint ptype)
    {
        if (_sw == null) return;
        _sw.WriteLine(FormattableString.Invariant($"B {t:F3} {x:F3} {y:F3} {p:F4} {ptype}"));
        Tick();
    }

    public static void Sample(double t, float x, float y, float p)
    {
        if (_sw == null) return;
        _sw.WriteLine(FormattableString.Invariant($"S {t:F3} {x:F3} {y:F3} {p:F4}"));
        Tick();
    }

    public static void End(double t)
    {
        if (_sw == null) return;
        _sw.WriteLine(FormattableString.Invariant($"E {t:F3}"));
        _sw.Flush();                 // 每笔写完即落盘
        _sinceFlush = 0;
    }

    private static void Tick()
    {
        if (++_sinceFlush >= 256) { _sw.Flush(); _sinceFlush = 0; }
    }
}
