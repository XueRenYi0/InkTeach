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
///      ⚠ **这条路只给"界面出问题、App 自己决定重建"用**（`Engine.RestartNow`）：
///      那种情况下老师没要求重启，**丢了板书是事故**，所以必须不丢。
///      **界面上老师主动点的那个"重启"不走这里**（用户 2026-09-22 定：它就是"像电脑重启"，
///      板书不接回来）——那条改成写下面那份自动存档、并把会话暂存**主动删掉**
///      （`DiscardSession`），见 `Engine.RestartFromUi`。两处的判据正好相反，别一起改。
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

    /// <summary>
    /// **自动存档**（崩溃恢复用）。放 `%LOCALAPPDATA%`——不是 TEMP：
    /// TEMP 会被系统/清理工具删掉，"断电一节课"这种场景恰恰要跨重启活下来。
    ///
    /// 和 <see cref="SessionPath"/> 的分工：那个是"自己主动重启，立刻读回来"；
    /// 这个是"每 15 秒存一次，下次打开接上"（产品里没有"保存"这个动作，
    /// 所以按持久画布来做——同类里 OneNote/Notability 都是这个模型；
    /// Xournal++ 是"退出删掉草稿"，它的用户正在提 issue 想改，
    /// 见 xournalpp/xournalpp#7697 与 #7754）。
    /// </summary>
    /// <summary>自检用：指向临时文件，**别动用户真正的板书**（和设置那边同一个套路）。</summary>
    public static string AutoSavePathOverride;

    public static string AutoSavePath => AutoSavePathOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkTeach", "autosave.ink");

    public static bool AutoSaveExists => File.Exists(AutoSavePath);

    /// <summary>自动存档写盘。写不进去只提示，不影响使用（顶多这次崩溃丢东西）。</summary>
    public static void SaveAuto(byte[] blob)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(AutoSavePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 先写临时文件再换名：**不能直接覆盖**——写一半断电会留下一个坏文件，
            // 下次打开时"恢复"出一堆垃圾（比没有自动存档更糟）。
            string tmp = AutoSavePath + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, AutoSavePath, overwrite: true);
        }
        catch (Exception ex) { Console.WriteLine("自动存档失败：" + ex.Message); }
    }

    /// <summary>读自动存档。**读走不删**：它就是"上次的板书"，下次打开还要用。</summary>
    public static byte[] LoadAuto()
    {
        try
        {
            if (!File.Exists(AutoSavePath)) return null;
            return File.ReadAllBytes(AutoSavePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("读自动存档失败（当作没有，不影响启动）：" + ex.Message);
            return null;
        }
    }

    /// <summary>删掉自动存档（自检用；产品里没有"删除"这条路，清空板书会存成空文档）。</summary>
    public static void DeleteAuto()
    {
        try { if (File.Exists(AutoSavePath)) File.Delete(AutoSavePath); } catch { }
    }

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

    /// <summary>
    /// 丢掉会话暂存（不写了、也不要了）。给"界面上的重启 = 像电脑重启、板书不接回来"那条路用。
    ///
    /// 为什么**光"不写"还不够**：上次崩溃抢救留下的残留（或者上一次重启时新进程没起来）
    /// 会被新进程读回去，那就成了"重启还带出旧板书"。所以这里要主动删掉。
    /// </summary>
    public static void DiscardSession()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { }
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
