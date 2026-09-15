using System;
using System.IO;
using System.Text;

namespace UiMock;

/// <summary>
/// 假面板的日志。为什么要它：双击运行时控制台一关，出问题的现场就没了；
/// 用户报"刚才操作的时候出问题了"，没有日志就只能靠猜。
///
/// 落在 exe 旁边（`ui-mock.log`），追加写、每行带时间与毫秒。
/// 记录三类东西：① 每一次状态变化（谁改的、改成什么）；② 每一次异常（含堆栈）；
/// ③ 关键几何（面板宽高、抽屉开合），这样"看起来不对"时能对上时间线。
/// </summary>
internal static class Log
{
    static readonly object Gate = new object();
    static string _path;
    static readonly DateTime T0 = DateTime.Now;

    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            try
            {
                // exe 旁边；写不进去就退到临时目录（别因为日志把程序搞崩）
                string dir = AppContext.BaseDirectory;
                _path = System.IO.Path.Combine(dir, "ui-mock.log");
                using var _ = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            }
            catch
            {
                _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ui-mock.log");
            }
            return _path;
        }
    }

    public static void Write(string what)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  (+{(DateTime.Now - T0).TotalSeconds,7:F2}s)  {what}";
        try
        {
            lock (Gate) File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 日志写不进去不影响用 */ }
        Console.WriteLine("  · " + what);
    }

    public static void Exception(string where, Exception ex)
    {
        Write($"[异常] {where}: {ex.GetType().Name}: {ex.Message}");
        try
        {
            lock (Gate) File.AppendAllText(Path, ex.ToString() + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>开一次新会话：先写一行分隔，方便在日志里找"这次"。</summary>
    public static void Session(string args)
    {
        Write("======== 启动 " + (args.Length == 0 ? "（无参数）" : args) + " ========");
    }
}
