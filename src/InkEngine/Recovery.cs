using System.Globalization;

namespace InkEngine;

/// <summary>
/// 界面出问题时的"不留死局"机制：先把板书暂存下来，必要时重启自己。
///
/// 为什么必须有它：教室的大屏 + 手写板机器上**很可能根本没有键盘**，
/// 界面就是唯一的出口。界面一旦不可用，老师既关不掉、也重启不了这个软件，
/// 屏幕上还盖着一层东西——那是死局，比"某个功能坏了"严重得多。
///
/// 两件事分开放，各有各的理由：
///
///   1. **会话暂存**：重启前把当前文档写进临时文件，重启时读回来。
///      "重启"的前提是**不丢板书**——否则重启比界面停用更糟（一节课的笔记没了）。
///   2. **重启计数**：一个时间窗内数重启次数，超过上限就不再重启。
///      一个必现的界面 bug 会变成"无限重启"，那比停用更糟（屏幕一直在闪、什么都干不了）。
///
/// 这两个文件都在临时目录，不放程序目录（教室机上程序目录常常是只读的）。
/// </summary>
internal static class Recovery
{
    /// <summary>板书暂存文件。只在"要重启"那一刻写，启动时读一次就删。</summary>
    public static string SessionPath =>
        Path.Combine(Path.GetTempPath(), "inkteach-session.ink");

    private static string StatePath =>
        Path.Combine(Path.GetTempPath(), "inkteach-ui-restarts.txt");

    /// <summary>同一个窗口内允许重启几次。第 4 次就停下来——那说明是必现的 bug。</summary>
    public const int MaxRestartsInWindow = 3;

    /// <summary>重启计数的窗口。超过这个间隔就当作"上一次早过去了"，重新数。</summary>
    private const double RestartWindowMs = 5 * 60 * 1000;

    /// <summary>暂存当前板书。写不进去不算致命（顶多这次重启丢东西），但要留痕。</summary>
    public static void SaveSession(byte[] blob)
    {
        try { File.WriteAllBytes(SessionPath, blob); }
        catch (Exception ex) { Console.WriteLine("板书暂存失败（重启会丢这一节课的内容）：" + ex.Message); }
    }

    /// <summary>
    /// 取出上次留下的板书。**读走就删**（只恢复一次），太旧的当陈年残留丢掉——
    /// 不然老师明天打开软件，屏幕上会突然冒出昨天那半屏字。
    /// </summary>
    public static byte[] TakeSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var written = File.GetLastWriteTimeUtc(SessionPath);
            var bytes = File.ReadAllBytes(SessionPath);
            File.Delete(SessionPath);
            if (DateTime.UtcNow - written > TimeSpan.FromHours(12)) return null;
            return bytes;
        }
        catch (Exception ex)
        {
            Console.WriteLine("读会话暂存失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>记一次"因为界面出问题而重启"，返回这个窗口内的累计次数。</summary>
    public static int NoteRestart()
    {
        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;
        double last = 0;
        int count = 0;
        try
        {
            if (File.Exists(StatePath))
            {
                var parts = File.ReadAllText(StatePath).Split(',');
                if (parts.Length == 2)
                {
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out last);
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
                }
            }
        }
        catch { }

        if (now - last > RestartWindowMs) count = 0;
        count++;

        try
        {
            File.WriteAllText(StatePath,
                now.ToString("F0", CultureInfo.InvariantCulture) + "," +
                count.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
        return count;
    }

    /// <summary>
    /// 跑稳了就把计数清掉。什么时候算"稳"由调用方定（见 Engine 主循环里那一分钟）。
    /// 不这么做的话，老师上午碰到两次、下午碰到两次，第四次就会莫名其妙不许重启。
    /// </summary>
    public static void ClearRestartCount()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); }
        catch { }
    }
}
