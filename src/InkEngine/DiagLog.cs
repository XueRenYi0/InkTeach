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

using Microsoft.Win32;
using System.IO.Compression;
using System.Net.Http;
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
            try { sb.AppendLine($"机器 {MachineModel} · {CpuName} · RAM {RamGb}G"); } catch { }
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
    /// <summary>日志收集仓坐标（建好后把令牌回填到下面；空 = 未配置、按钮提示改用导出）。</summary>
    internal const string LogUploadOwner = "XueRenYI0";
    internal const string LogUploadRepo = "InkTeach-Logs";
    internal const string LogUploadTag = "logs";

    /// <summary>
    /// 上传专用令牌（2026-10-17 用户建：仅"项目"组，其余全禁止）——
    /// **异或 + Base64 掩码存储**，不再明文（防 grep / 防一眼看穿）。
    /// ⚠ 老实说：客户端必须能自己解开，所以这不是"真加密"——真正的防线是
    ///   "专用令牌 + 出事就换"（见 调研\日志上传-方案.md）。
    /// </summary>
    internal const string LogUploadTokenMasked = "Li0YJzEHASZMNj4hVUMCBFUkFzljHRJb";
    internal static string LogUploadToken => Unmask(LogUploadTokenMasked);
    internal static string Unmask(string s)
    {
        var b = Convert.FromBase64String(s);
        var k = Encoding.UTF8.GetBytes("InkTeach-Log-2026");
        for (int i = 0; i < b.Length; i++) b[i] ^= k[i % k.Length];
        return Encoding.UTF8.GetString(b);
    }

    // ---- 本机信息 / 性能快照（2026-10-17 用户定）-------------------------------
    //
    // 用户要两件事：
    //   ① 知道"装了多少台、什么型号、还在不在用"——见下面的**设备卡**（自动上传）；
    //   ② 能分析"卡顿"——每 5 分钟一条性能快照 + 单帧长卡（≥150ms）立刻记一条。
    // 两样都极便宜：型号/CPU/内存读注册表和一次 P/Invoke（进程内缓存），
    // 快照只是把引擎已有的 fps/帧耗时/内存数抄进日志。

    /// <summary>设备卡的安装 ID（引擎启动时从偏好注入；匿名 GUID，不含个人信息）。</summary>
    public static string DeviceId;
    /// <summary>首次运行日期（yyyy-MM-dd，本地偏好）。</summary>
    public static string InstallFirstDay;

    static string _model, _cpu;
    static int _ramGb;

    /// <summary>机器型号（"HONOR HGF-WX6-V" 这种；读 BIOS 注册表，一次缓存）。</summary>
    public static string MachineModel
    {
        get { if (_model == null) ReadHardware(); return _model; }
    }
    public static string CpuName
    {
        get { if (_cpu == null) ReadHardware(); return _cpu; }
    }
    public static int RamGb
    {
        get { if (_cpu == null) ReadHardware(); return _ramGb; }
    }

    static void ReadHardware()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string mk = k?.GetValue("SystemManufacturer") as string;
            string md = k?.GetValue("SystemProductName") as string;
            _model = ((mk ?? "").Trim() + " " + (md ?? "").Trim()).Trim();
        }
        catch { }
        if (string.IsNullOrEmpty(_model)) _model = "未知";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _cpu = ((k?.GetValue("ProcessorNameString") as string) ?? "").Trim();
        }
        catch { }
        if (string.IsNullOrEmpty(_cpu)) _cpu = "未知";
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m)) _ramGb = (int)Math.Round(m.ullTotalPhys / 1024.0 / 1024.0 / 1024.0);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---- 性能快照（写进日志，排查"卡顿"用）-------------------------------------

    static long _perfWinStartMs = -1;
    static long _perfFrames;
    static double _perfSumFrame, _perfMaxFrame, _perfMaxPresent;
    static int _perfGc2Last;
    static long _slowLogAtMs;

    /// <summary>慢帧日志里的"当时在干什么"（引擎只在变化时写，几纳秒）。</summary>
    public static string DocOpenNote = "桌面";

    /// <summary>
    /// 每帧调一次（渲染线程，几十纳秒的量级）：累计窗口统计；
    /// **单帧 ≥150ms** 立刻记一条（12 秒内至多一条），**每 5 分钟**一条窗口快照。
    /// 卡顿排查最值钱的就是"哪一秒、卡了多久、当时内存多少"。
    /// </summary>
    public static void NoteFrame(double nowMs, double fps, double frameMs, double presentMs, double workingSetMb)
    {
        if (_perfWinStartMs < 0) { _perfWinStartMs = (long)nowMs; _perfGc2Last = GC.CollectionCount(2); }
        _perfFrames++;
        _perfSumFrame += frameMs;
        if (frameMs > _perfMaxFrame) _perfMaxFrame = frameMs;
        if (presentMs > _perfMaxPresent) _perfMaxPresent = presentMs;

        if (frameMs >= 150 && nowMs - _slowLogAtMs > 12_000)
        {
            _slowLogAtMs = (long)nowMs;
            Console.WriteLine($"[perf] 慢帧 {(long)frameMs}ms（fps≈{fps:F0}，ws={workingSetMb:F0}MB，{DocOpenNote}）");
        }

        if (nowMs - _perfWinStartMs >= 300_000)
        {
            int gc2 = GC.CollectionCount(2);
            Console.WriteLine($"[perf] 5 分钟：平均帧 {_perfSumFrame / Math.Max(1, _perfFrames):F1}ms"
                + $" 最差帧 {(long)_perfMaxFrame}ms 最差上屏 {_perfMaxPresent:F1}ms"
                + $" ws={workingSetMb:F0}MB gc2+={gc2 - _perfGc2Last}");
            _perfGc2Last = gc2;
            _perfWinStartMs = (long)nowMs;
            _perfFrames = 0;
            _perfSumFrame = _perfMaxFrame = _perfMaxPresent = 0;
        }
    }

    // ---- 设备卡（台数 / 型号 / 活跃统计；老师零操作）---------------------------

    static volatile bool _cardStarted, _cardDone, _cardOk;

    /// <summary>后台传一次设备卡（每天最多一次，由引擎的主循环触发）。</summary>
    public static void StartDeviceCardUpload(string version, string screen)
    {
        if (_cardStarted) return;
        _cardStarted = true;
        _cardDone = _cardOk = false;
        var th = new Thread(() => DeviceCardWorker(version, screen)) { IsBackground = true, Name = "DeviceCard" };
        th.Start();
    }

    /// <summary>主循环轮询：传完了没有（true = 有结果，ok = 成功）。</summary>
    public static bool PollDeviceCard(out bool ok)
    {
        ok = _cardOk;
        return _cardDone;
    }

    static void DeviceCardWorker(string version, string screen)
    {
        bool ok = false;
        try
        {
            using var http = NewLogHttp(120);
            string api = $"https://api.gitcode.com/api/v5/repos/{LogUploadOwner}/{LogUploadRepo}";
            string name = "devices/" + DeviceId + ".json";
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildCardJson(version, screen)));

            // 已有旧卡 → 要 sha 才能更新（每天覆盖同一份，**仓库不会越长越大**）
            string sha = null;
            string getUrl = $"{api}/contents/{name}?access_token={Uri.EscapeDataString(LogUploadToken)}";
            var getResp = http.GetAsync(getUrl).GetAwaiter().GetResult();
            if (getResp.IsSuccessStatusCode)
                sha = JsonStringField(getResp.Content.ReadAsStringAsync().GetAwaiter().GetResult(), "sha");

            string body = "{\"message\":\"device card\",\"content\":\"" + b64 + "\",\"branch\":\"main\""
                        + (sha != null ? ",\"sha\":\"" + sha + "\"" : "") + "}";
            using var req = new HttpRequestMessage(sha == null ? HttpMethod.Post : HttpMethod.Put,
                $"{api}/contents/{name}?access_token={Uri.EscapeDataString(LogUploadToken)}");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = http.SendAsync(req).GetAwaiter().GetResult();
            ok = resp.IsSuccessStatusCode;
        }
        catch { ok = false; }
        _cardOk = ok;
        _cardDone = true;
    }

    static string BuildCardJson(string version, string screen)
    {
        string J(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
        return "{\"id\":\"" + DeviceId + "\""
             + ",\"ver\":\"" + J(version) + "\""
             + ",\"os\":\"" + J(RuntimeInformation.OSDescription) + "\""
             + ",\"build\":" + Environment.OSVersion.Version.Build
             + ",\"model\":\"" + J(MachineModel) + "\""
             + ",\"cpu\":\"" + J(CpuName) + "\""
             + ",\"ramGb\":" + RamGb
             + ",\"screen\":\"" + J(screen) + "\""
             + ",\"first\":\"" + J(InstallFirstDay) + "\""
             + ",\"last\":\"" + DateTime.Now.ToString("yyyy-MM-dd") + "\""
             + "}";
    }

    static HttpClient NewLogHttp(int seconds)
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        http.Timeout = TimeSpan.FromSeconds(seconds);
        try { http.DefaultRequestHeaders.UserAgent.ParseAdd("InkTeach-log/" + ProductInfo.Version); } catch { }
        return http;
    }

    static string JsonStringField(string json, string key)
    {
        int k = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        int colon = json.IndexOf(':', k + key.Length + 2);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }


    // ---- 异步上传（2026-10-17）：点「日志」时界面绝不能被网络卡住 -------------------

    static volatile int _upState;      // 0 空闲 / 1 上传中 / 2 成功 / 3 失败
    static volatile string _upMsg, _upLeaf;

    public static bool UploadBusy => _upState == 1;

    public static void StartZipUpload(string zipPath, string leaf)
    {
        if (_upState == 1) return;
        _upState = 1;
        _upMsg = null;
        _upLeaf = leaf;
        var th = new Thread(() =>
        {
            try { InkEngine.UploadZipToGitCode(zipPath, leaf); _upState = 2; }
            catch (Exception ex) { _upMsg = ex.Message; _upState = 3; }
        }) { IsBackground = true, Name = "LogUpload" };
        th.Start();
    }

    /// <summary>主循环轮询：0 = 没消息、1 = 还在传、2 = 成功、3 = 失败（msg 有效）。取走即清。</summary>
    public static int PollUpload(out string msg, out string leaf)
    {
        int s = _upState;
        if (s != 2 && s != 3) { msg = null; leaf = null; return s; }
        msg = _upMsg;
        leaf = _upLeaf;
        _upState = 0;
        return s;
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

    // ---- 日志上云（2026-10-17 用户定）------------------------------------------
    //
    // 目标：老师机器卡了 →「设置 → 上传日志…」→ 日志 zip 到云端 → 我们取下来直接分析。
    // 方案：GitCode 上的一个**日志收集仓**（免费、国内直连、接口现成——和
    //       `tools\gitcode-release.ps1` 用的是同一条"发行版附件"链路）。
    //
    // ⚠ 没有服务器，令牌只能藏在客户端里（能从 exe 里抠出来）：所以
    //   ① 这个令牌**只给日志收集仓用**（绝不复用发版令牌）；
    //   ② 泄漏的最坏后果 = 有人往那个仓写垃圾（读不到别的东西、也删不掉我们的仓库）——
    //      发现异常换个令牌发个小版本即可。取日志见 `tools\fetch-logs.ps1`，
    //      完整方案与建仓步骤见 `调研\日志上传-方案.md`。

    /// <summary>「日志」入口（2026-10-17 用户定，**合并成一个**）：先试上传（后台线程）；
    /// 没网 / 没配令牌 / 接口错 → **自动转成「导出日志」**（弹另存为），老师总能把 zip 拿出来。
    /// ⚠ 上传走后台：教室网络差时**界面不会冻住**（结果由 <see cref="StepLogUpload"/> 轮询）。</summary>
    internal void UploadLogsFromUi()
    {
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不上传"); return; }
        if (DiagLog.UploadBusy) { SetInkStatus("正在上传日志…（请稍等）"); return; }

        if (string.IsNullOrEmpty(DiagLog.LogUploadToken))
        {
            // 没配置 = 直接导出（老师侧永远有一条能走的路）
            SetInkStatus("上传未配置——改为导出，请把 zip 发回来");
            ExportLogsFromUi();
            return;
        }

        string zip = Path.Combine(Path.GetTempPath(),
            $"inkteach-upload-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            DiagLog.ExportZip(zip);      // 本地打包（≤8MB）很快，同步没问题
        }
        catch (Exception ex)
        {
            SetInkStatus("打包失败：" + ex.Message);
            Console.WriteLine("[日志] 打包失败：" + ex);
            return;
        }
        _logUploadZip = zip;
        string leaf = $"log-{DateTime.Now:yyyyMMdd-HHmmss}-{LogSafeName(Environment.MachineName)}-{Guid.NewGuid().ToString("N")[..6]}.zip";
        DiagLog.StartZipUpload(zip, leaf);
        SetInkStatus("正在上传日志…");
        Console.WriteLine("[日志] 开始上传…（后台线程，界面不卡）");
    }

    string _logUploadZip;

    /// <summary>上传结果轮询（Loop / RenderAll 两处都调）：成功报编号；失败自动转导出。</summary>
    internal void StepLogUpload()
    {
        int st = DiagLog.PollUpload(out string msg, out string leaf);
        if (st != 2 && st != 3) return;
        if (st == 2)
        {
            SetInkStatus($"日志已上传：{leaf}（把这个编号告诉开发）");
            Console.WriteLine($"[日志] 已上传 {leaf}");
        }
        else
        {
            SetInkStatus("上传失败：" + msg + "——已改为导出");
            Console.WriteLine("[日志] 上传失败，转导出：" + msg);
            ExportLogsFromUi();
        }
        try { if (_logUploadZip != null) File.Delete(_logUploadZip); } catch { }
        _logUploadZip = null;
    }

    /// <summary>设备卡自动上传（台数 / 型号统计；2026-10-17）。每天最多一次、启动 25 秒后才试。</summary>
    bool _cardTried, _cardDoneHandled;
    internal void MaybeDeviceCardUpload()
    {
        if (!ExportDialogEnabled) return;                 // 自检 / 测试模式不自动传
        bool cardOk = false;
        if (!_cardDoneHandled && DiagLog.PollDeviceCard(out cardOk))
        {
            _cardDoneHandled = true;
            if (cardOk)
            {
                SetUiPref("log.cardDay", DateTime.Now.ToString("yyyy-MM-dd"));
                Console.WriteLine("[日志] 设备卡已上传（今日台数统计）");
            }
            else
            {
                Console.WriteLine("[日志] 设备卡上传失败（下个会话再试）");
            }
            return;
        }
        if (_cardTried) return;
        if (NowMs < 25_000) return;                        // 别抢启动那几秒
        if (GetUiPref("log.cardDay") == DateTime.Now.ToString("yyyy-MM-dd")) return;
        if (string.IsNullOrEmpty(DiagLog.DeviceId)) return;
        _cardTried = true;
        Console.WriteLine("[日志] 设备卡开始上传…");
        DiagLog.StartDeviceCardUpload(ProductInfo.Version, $"{_virtualW}x{_virtualH}");
    }

    /// <summary>文件名安全字符（**只留 ASCII**：它要进 URL 路径；机器名可能带中文）。</summary>
    static string LogSafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
        return sb.Length == 0 ? "machine" : sb.ToString();
    }

    /// <summary>
    /// 把 zip 传成日志收集仓里的一个**文件**（`logs/<名字>`，contents 接口）。
    ///
    /// ⚠ 为什么不用"发行版附件"（发版脚本那条路）：**私有仓的附件下载会 403**
    /// （各种鉴权姿势都试过，见 调研\日志上传-方案.md 的"踩坑记录"）——而仓库
    /// 文件这条链（api.gitcode.com + access_token）一路都通：读、写、列表全过。
    /// 新建用 **POST**（PUT 是"更新已存在的文件"，会向你要 sha）。
    /// </summary>
    internal static void UploadZipToGitCode(string zipPath, string assetName)
    {
        // GitCode 是国内直连的：**显式不走系统代理**（同 UpdateFeed 那条规矩——
        // 半死不活的"给 GitHub 用的代理"会把上传卡到超时）。
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        http.Timeout = TimeSpan.FromSeconds(120);
        try { http.DefaultRequestHeaders.UserAgent.ParseAdd("InkTeach-log/" + ProductInfo.Version); } catch { }

        string api = $"https://api.gitcode.com/api/v5/repos/{DiagLog.LogUploadOwner}/{DiagLog.LogUploadRepo}";
        string b64 = Convert.ToBase64String(File.ReadAllBytes(zipPath));
        // 手工拼 JSON：base64 和文件名里没有需要转义的字符（名字已 ASCII 化）
        string body = "{\"message\":\"log " + assetName + "\",\"content\":\"" + b64 + "\",\"branch\":\"main\"}";
        string q = $"{api}/contents/logs/{Uri.EscapeDataString(assetName)}" +
                   $"?access_token={Uri.EscapeDataString(DiagLog.LogUploadToken)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, q);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = http.SendAsync(req).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
        {
            string t = "";
            try { t = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
            throw new Exception($"http={(int)resp.StatusCode}"
                + (t.Length > 0 ? "；" + t.Substring(0, Math.Min(160, t.Length)) : ""));
        }
    }
}
