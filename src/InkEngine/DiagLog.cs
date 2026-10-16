// 运行日志（2026-10-16 用户定）：常开、轻量、能导出。
//
// 用户的原话："增加一个日志功能……后期日志上传到网上分析、或者导出到 U 盘，
// 都可以快速锁定客户问题，但是日志不能影响性能。"
//
// 做法与三条纪律：
//   ① **写入永不阻塞**：`Console.WriteLine` 只做"加锁进队列"，真正的落盘在后台线程
//      每 0.5 秒搬一次。全仓 1300 多条诊断输出**一条都不用改**——这里用
//      `Console.SetOut` 把它们接住；自检/命令行模式 tee 一份、原样照打；
//   ② **限流 + 轮转 + 封顶**：每秒最多 400 行（超了只记"丢了 N 行"）、单文件 2 MB、
//      留 4 份（磁盘 ≤ 8 MB，导出 U 盘一秒完）。哪怕哪天有人在热路径里写日志，
//      也只会"少记"，绝不会"拖慢"；
//   ③ **IO 失败就闭嘴**：日志把软件搞崩/卡住是荒唐的，所有 IO 异常一律吞掉。
//
// 内容：系统/版本/键位表（启动时本来就打）+ 状态切换 + 文件操作 + 异常……
// 都落在 `%LOCALAPPDATA%\InkTeach\Logs`；「更多 → 设置 → 导出日志…」打包成 zip 给支持。

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace InkEngine;

internal static class DiagLog
{
    // ---- 自检的口子（同 Recovery / BoardHistory 那套套路）----

    /// <summary>自检用：把日志目录指到临时目录，别往用户真正的日志里灌。</summary>
    public static string DirOverride;

    /// <summary>单文件轮转阈值（0 = 默认 2 MB；自检调小来验轮转）。</summary>
    public static int SizeCapOverride;

    /// <summary>每秒限流行数（0 = 默认 400；压测用）。</summary>
    public static int MaxPerSecOverride;

    public static string Dir => DirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkTeach", "Logs");

    public static string CurrentPath => Path.Combine(Dir, "inkteach.log");

    /// <summary>导出/打包时的"环境"摘取口（屏幕 / 缩放这些建了窗口才算得准，
    /// 所以只挂一个惰性取值函数，导出那一刻才调用）。</summary>
    public static Func<string> EnvExtra;

    // ---- 状态 ----
    static readonly object _lk = new();           // 行队列（快，几纳秒一把锁）
    static readonly object _io = new();           // 文件写入（慢，只在后台线程 / 导出时碰）
    static readonly StringBuilder _cur = new();   // 还没换行的半行
    static readonly List<string> _pending = new();
    static bool _installed;
    static StreamWriter _w;
    static long _bytes;
    static int _secCount, _secDropped;
    static long _secStartMs;

    /// <summary>限流一共丢了多少行（自检读）。</summary>
    public static long DroppedTotal { get; private set; }
    /// <summary>真正进过队列的行数（自检读）。</summary>
    public static long WrittenTotal { get; private set; }

    static int MaxPerSec => MaxPerSecOverride > 0 ? MaxPerSecOverride : 400;
    static long SizeCap => SizeCapOverride > 0 ? SizeCapOverride : 2_000_000;

    /// <summary>最多留几份（当前 + .1/.2/.3）。</summary>
    const int KeepFiles = 4;

    /// <summary>每次运行装一次（在 Main 里"接父进程控制台"之后调——tee 包的要是最终的 stdout）。</summary>
    public static void Install()
    {
        lock (_lk)
        {
            if (_installed) return;
            _installed = true;
        }

        try
        {
            Console.SetOut(new Tee(Console.Out));
            Console.SetError(new Tee(Console.Error));
        }
        catch { }

        WriteHeader();

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    AppendRaw("===== 未处理异常（进程即将退出）=====\n" + e.ExceptionObject);
                    FlushNow();      // 崩溃前同步落盘：这批比平时任何一条都值得留下
                }
                catch { }
            };
            // 崩溃最怕"最后 0.5 秒还在队列里"——退出时同步搬一次。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { FlushNow(); } catch { } };
        }
        catch { }

        new Thread(Pump) { IsBackground = true, Name = "DiagLog" }.Start();
    }

    /// <summary>把日志换到别的目录（自检用；调用后新行落新目录）。</summary>
    public static void Retarget(string dir)
    {
        FlushNow();
        lock (_io) { CloseWriter(); }
        DirOverride = dir;
    }

    /// <summary>强制把队列搬进文件（测试 / 导出 / 崩溃前用；平时不用管，后台线程每 0.5s 一次）。</summary>
    public static void FlushNow()
    {
        List<string> batch = null;
        lock (_lk)
        {
            if (_pending.Count > 0)
            {
                batch = new List<string>(_pending);
                _pending.Clear();
            }
        }
        if (batch == null) return;

        lock (_io)
        {
            try
            {
                EnsureWriter();
                foreach (var ln in batch)
                {
                    _w.WriteLine(ln);
                    _bytes += Encoding.UTF8.GetByteCount(ln) + 2;
                }
                _w.Flush();
            }
            catch
            {
                CloseWriter();          // IO 坏了就闭嘴（下轮再试开）
            }
            if (_bytes >= SizeCap) RotateLocked();
        }
    }

    // ---- 导出（「更多 → 设置 → 导出日志…」走这里）--------------------------------

    /// <summary>把日志（+环境摘要 + 上次崩溃记录）打包成 zip。返回实际路径。</summary>
    public static string ExportZip(string destPath)
    {
        FlushNow();
        string stage = Path.Combine(Path.GetTempPath(),
            "inkteach-log-" + Environment.ProcessId + "-" + DateTime.Now.ToString("HHmmss"));
        try
        {
            Directory.CreateDirectory(stage);
            try
            {
                // ⚠ 当前日志文件被写线程**开着**（FileShare.ReadWrite）——不能用 File.Copy
                //（它按 FileShare.Read 开源，和写句柄的"允许写"冲突 → IO 异常；而这里
                //  恰恰是"出问题想看日志"的时刻，绝不能悄悄失败）。统一走共享读拷贝。
                foreach (var f in Directory.GetFiles(Dir, "inkteach*.log"))
                    CopyShared(f, Path.Combine(stage, Path.GetFileName(f)));
            }
            catch { }
            File.WriteAllText(Path.Combine(stage, "环境.txt"), BuildSummary(), new UTF8Encoding(false));
            try
            {
                string crash = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InkTeach", "last-crash.txt");
                if (File.Exists(crash)) CopyShared(crash, Path.Combine(stage, "last-crash.txt"));
            }
            catch { }

            if (File.Exists(destPath)) File.Delete(destPath);
            ZipFile.CreateFromDirectory(stage, destPath);
            return destPath;
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
        }
    }

    /// <summary>带"读写共享"的手工拷贝（源可能正被写线程开着）。</summary>
    static void CopyShared(string src, string dst)
    {
        using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var outFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.CopyTo(outFs);
    }

    static string BuildSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("InkTeach 运行日志摘要（导出时生成，供排查问题用）");
        sb.AppendLine();
        sb.AppendLine($"版本：{ProductInfo.Version}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            sb.AppendLine($"本次运行：{p.StartTime:yyyy-MM-dd HH:mm:ss} 起，已运行 {(DateTime.Now - p.StartTime).TotalMinutes:F1} 分钟");
        }
        catch { }
        sb.AppendLine($"系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
        sb.AppendLine($".NET：{Environment.Version}；进程 {RuntimeInformation.ProcessArchitecture}，{(Environment.Is64BitProcess ? "64" : "32")} 位");
        sb.AppendLine($"机器：{Environment.MachineName}");
        try
        {
            var x = EnvExtra?.Invoke();
            if (!string.IsNullOrEmpty(x)) sb.AppendLine($"屏幕：{x}");
        }
        catch { }
        sb.AppendLine();
        sb.AppendLine("日志文件：");
        try
        {
            foreach (var f in Directory.GetFiles(Dir, "inkteach*.log"))
                sb.AppendLine($"  {Path.GetFileName(f)}  {new FileInfo(f).Length / 1024.0:F0} KB");
        }
        catch { }
        return sb.ToString();
    }

    // ---- 内部：行装配 / 限流 / 落盘 ---------------------------------------------

    /// <summary>后台搬运线程：每 0.5 秒把队列落一次盘（所有阻塞都在这边）。</summary>
    static void Pump()
    {
        while (true)
        {
            try { Thread.Sleep(500); } catch { }
            try { FlushNow(); } catch { }
        }
    }

    static void WriteHeader()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"====== InkTeach {ProductInfo.Version} 启动 · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ======");
            try { sb.AppendLine($"系统 {RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）· 进程 {(Environment.Is64BitProcess ? "64" : "32")} 位 · {Environment.MachineName}"); } catch { }
            File.AppendAllText(CurrentPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>不限流直插（头部 / 崩溃专用）。</summary>
    static void AppendRaw(string text)
    {
        lock (_lk)
        {
            foreach (var ln in text.Split('\n'))
                _pending.Add("[" + Stamp() + "] " + ln.TrimEnd('\r'));
        }
    }

    static void Feed(char c)
    {
        if (c == '\n') { EndLine(); return; }
        if (c == '\r') return;
        lock (_lk)
        {
            _cur.Append(c);
            if (_cur.Length >= 8000) EmitLocked();
        }
    }

    static void Feed(ReadOnlySpan<char> s)
    {
        while (true)
        {
            int nl = s.IndexOf('\n');
            if (nl < 0) break;
            lock (_lk)
            {
                _cur.Append(s[..nl]);
                EmitLocked();
            }
            s = s[(nl + 1)..];
        }
        if (!s.IsEmpty)
        {
            lock (_lk)
            {
                _cur.Append(s);
                if (_cur.Length >= 8000) EmitLocked();
            }
        }
    }

    static void EndLine() { lock (_lk) EmitLocked(); }

    /// <summary>把半行缓冲收尾、限流、带时间戳进队列。**持 `_lk` 调用**。</summary>
    static void EmitLocked()
    {
        string line = _cur.ToString();
        _cur.Clear();
        line = line.TrimEnd('\r', ' ');
        if (line.Length == 0) return;              // 空行不记（日志里没信息量）

        long now = Environment.TickCount64;
        if (now - _secStartMs >= 1000)
        {
            _secStartMs = now;
            _secCount = 0;
            if (_secDropped > 0)
            {
                _pending.Add($"[{Stamp()}] （日志限流：上一秒丢了 {_secDropped} 行）");
                DroppedTotal += _secDropped;
                _secDropped = 0;
            }
        }
        if (++_secCount > MaxPerSec) { _secDropped++; return; }

        _pending.Add("[" + Stamp() + "] " + line);
        WrittenTotal++;
    }

    static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    static void EnsureWriter()
    {
        if (_w != null) return;
        Directory.CreateDirectory(Dir);
        var fs = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _w = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
        try { _bytes = fs.Length; } catch { _bytes = 0; }
    }

    static void CloseWriter()
    {
        try { _w?.Dispose(); } catch { }
        _w = null;
    }

    /// <summary>轮转：inkteach.log → .1 → .2 → .3（丢最旧的）。**持 `_io` 调用**。</summary>
    static void RotateLocked()
    {
        CloseWriter();
        try
        {
            string oldest = Path.Combine(Dir, $"inkteach.{KeepFiles - 1}.log");
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = KeepFiles - 2; i >= 1; i--)
            {
                string src = Path.Combine(Dir, $"inkteach.{i}.log");
                if (File.Exists(src)) File.Move(src, Path.Combine(Dir, $"inkteach.{i + 1}.log"), true);
            }
            if (File.Exists(CurrentPath)) File.Move(CurrentPath, Path.Combine(Dir, "inkteach.1.log"), true);
        }
        catch { }
        _bytes = 0;
    }

    // ---- tee：接住 Console 输出（原样转发 + 记一份）--------------------------------

    sealed class Tee : TextWriter
    {
        readonly TextWriter _inner;
        public Tee(TextWriter inner) { _inner = inner; }
        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char c)
        {
            try { _inner.Write(c); } catch { }
            DiagLog.Feed(c);
        }

        public override void Write(string s)
        {
            try { _inner.Write(s); } catch { }
            if (!string.IsNullOrEmpty(s)) DiagLog.Feed(s.AsSpan());
        }

        public override void WriteLine(string s)
        {
            try { _inner.WriteLine(s); } catch { }
            if (!string.IsNullOrEmpty(s)) DiagLog.Feed(s.AsSpan());
            DiagLog.EndLine();
        }

        public override void WriteLine()
        {
            try { _inner.WriteLine(); } catch { }
            DiagLog.EndLine();
        }

        public override void Flush()
        {
            try { _inner.Flush(); } catch { }
        }
    }
}

public partial class InkEngine
{
    /// <summary>
    /// 「设置 → 导出日志…」（2026-10-16 用户定）：把运行日志（+环境摘要 +上次崩溃记录）
    /// 打成一个 zip。存 U 盘、发微信、上传都能用——排查教室机器问题时先要这个。
    /// 自检模式不弹对话框（同其它导出链路）。
    /// </summary>
    internal void ExportLogsFromUi()
    {
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return; }

        string name = $"InkTeach-{ProductInfo.Version}-日志-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        IntPtr owner = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        string dest = ExportFileDialog.AskForLogZip(owner, name, null);
        if (dest == null) return;      // 取消

        try
        {
            DiagLog.ExportZip(dest);
            SetInkStatus($"日志已导出：{dest}");
            Console.WriteLine($"[日志] 已导出 {dest}");
        }
        catch (Exception ex)
        {
            SetInkStatus("日志导出失败：" + ex.Message);
            Console.WriteLine("[日志] 导出失败：" + ex);
        }
    }
}
