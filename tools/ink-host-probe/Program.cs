// InkHostProbe v1 —— 独立探针：裸 Win32 接"系统托管湿墨 + 旁听"两条候选路线
// 规格来源：调研/方案-Win10与Win11系统托管湿墨及旁听接入.md §5（阶段 0，唯一值得做的下一轮实验）
//
// 目的：在**不改产品代码**的前提下，用同一套计数口径回答三个事实问题——
//   ① Presenter/宿主能否收到真实输入（真笔 PT_PEN 优先；合成笔仅作预演）
//   ② 屏幕上周否真的出现"系统画的湿墨"（逐帧像素计数，不靠目测）
//   ③ 同一笔的点能否被旁听出来（CoreWetStrokeUpdateSource 的 NewInkPoints）
//
// route inkdesktop（路线 1，官方 Win32 宿主）：
//   经典 DirectComposition 设备 + CreateTargetForHwnd(我们的覆盖层 HWND) + 根视觉
//   → CoCreateInstance(CLSID_InkDesktopHost {062584a6-f830-4bdc-a4d2-0a10ab062b1d})
//   → CreateAndInitializeInkPresenter(rootVisual, w, h, IID_IInkPresenterDesktop, ...)
//   → 尝试 QI WinRT InkPresenter 面（IID a69b70e2-887b-458f-b173-4fe4438930a3）
//     成功则：IsInputEnabled=true、InputDeviceTypes、订阅 StrokesCollected、挂 CoreWet 旁听
//   → 注入 → 计数（窗口消息 / presenter 事件 / CoreWet 点 / 屏幕墨色像素）
//
// route corehost（路线 2，2026-09-14 曾一次成功、此后复现不了）：
//   DispatcherQueue + Windows.UI.Composition + DesktopWindowTarget（同一个覆盖层 HWND）
//   → CoreInkPresenterHost → presenter → RootVisual
//   → StrokesCollected + CoreWet 旁听（--iis 另加 CoreInkIndependentInputSource 对照）
//   → 同上计数
//
// 输入模式（--input）：
//   synthetic（默认）合成笔 CreateSyntheticPointerDevice(PT_PEN) 注入
//   mouse              合成鼠标拖拽（SetCursorPos + mouse_event；EnableMouseInPointer 开）
//   pentest            真笔人工：中间横带书写 3~5 笔，Ctrl+Alt+Shift+Q 提前结束；
//                      期间以 250ms 采样整窗像素，结束时报告（结果另存桌面\软件 供人查看）
//
// 其他开关：--activate --target --mainthread --dqc --transparent --nohost --iis --noface
//           --penmouse --secs N --pentestsecs N --out PATH --nomsg
//
// 纪律（与方案一致）：本探针只新增 tools/ink-host-probe/，不碰 src/；报告写入 reports/。
// 未知参数打印帮助并退出（不静默忽略）。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;
using WinRT;

namespace InkHostProbe;

internal static unsafe class Program
{
    private const string ProbeVersion = "ink-host-probe v1 (2026-10-12)";

    // ---------------- 选项 ----------------

    private static string _route = "corehost";      // corehost | inkdesktop
    private static string _input = "synthetic";     // synthetic | mouse | pentest
    private static bool _mainThread;
    private static bool _dqc;
    private static bool _transparent;
    private static bool _noHost;
    private static bool _iis;
    private static bool _noFace;
    private static bool _penMouse;
    private static bool _activate;
    private static bool _targetSelf;
    private static bool _nomsg;
    private static long _secs = 9;
    private static long _pentestSecs = 120;
    private static string? _outPath;

    // ---------------- 日志 ----------------

    private static readonly object _logLock = new();
    private static readonly StringBuilder _log = new();

    private static void Log(string line)
    {
        lock (_logLock)
        {
            Console.WriteLine(line);
            _log.Append(line).Append('\n');
        }
    }

    private static string LogText()
    {
        lock (_logLock)
        {
            return _log.ToString();
        }
    }

    // ---------------- 计数 ----------------

    private static readonly object _cntLock = new();
    private static readonly Dictionary<string, int> _counts = new();
    private static readonly Dictionary<uint, int> _pointerIdCounts = new();
    private static readonly HashSet<string> _pointerTypesSeen = new();

    private static void Bump(string kind)
    {
        lock (_cntLock)
        {
            _counts.TryGetValue(kind, out int n);
            _counts[kind] = n + 1;
        }
    }

    private static int Get(string kind)
    {
        lock (_cntLock)
        {
            _counts.TryGetValue(kind, out int n);
            return n;
        }
    }

    private static string CountsDump()
    {
        lock (_cntLock)
        {
            return string.Join(" ", _counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    // ---------------- Presenter / 旁听统计 ----------------

    private static readonly object _inkLock = new();
    private static int _strokeCount;             // StrokesCollected 笔数
    private static int _strokePoints;            // StrokesCollected 点数
    private static int _wetEvents;               // CoreWet 事件总数
    private static int _wetPoints;               // CoreWet 点总数
    private static readonly Dictionary<string, int> _wetEventKinds = new();
    private static int _wetDetailLines;
    private static ulong _wetFirstTs;
    private static ulong _wetLastTs;
    private static int _iisEvents;

    private static void DumpInkCounters(StringBuilder sb)
    {
        lock (_inkLock)
        {
            sb.Append("presenter.strokes=").Append(_strokeCount)
              .Append(" presenter.points=").Append(_strokePoints)
              .Append(" corewet.events=").Append(_wetEvents)
              .Append(" corewet.points=").Append(_wetPoints)
              .Append(" iis.events=").Append(_iisEvents)
              .Append('\n');
            sb.Append("corewet.kinds=").Append(string.Join(" ", _wetEventKinds.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))).Append('\n');
            if (_wetFirstTs != 0 || _wetLastTs != 0)
            {
                sb.Append($"corewet.timestamp first={_wetFirstTs} last={_wetLastTs}\n");
            }
        }
    }

    private static void OnStrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
    {
        int n = 0;
        int pts = 0;
        try
        {
            n = args.Strokes?.Count ?? 0;
            foreach (var s in args.Strokes!)
            {
                try { pts += s.GetInkPoints()?.Count ?? 0; } catch { }
            }
        }
        catch { }
        lock (_inkLock)
        {
            _strokeCount += n;
            _strokePoints += pts;
        }
        Log($"[{Environment.TickCount64}] StrokesCollected strokes+={n} total={_strokeCount} points+={pts}");
    }

    private static void OnWet(string kind, CoreWetStrokeUpdateEventArgs e)
    {
        int n = 0;
        string first = "";
        try
        {
            var list = e.NewInkPoints;
            n = list?.Count ?? 0;
            if (n > 0)
            {
                var p0 = list![0];
                first = $" first=({p0.Position.X:F1},{p0.Position.Y:F1}) press={p0.Pressure:F3} tilt=({p0.TiltX:F0},{p0.TiltY:F0}) ts={p0.Timestamp}";
            }
        }
        catch (Exception ex)
        {
            first = " <points read failed: " + ex.Message + ">";
        }

        lock (_inkLock)
        {
            _wetEvents++;
            _wetPoints += n;
            _wetEventKinds.TryGetValue(kind, out int c);
            _wetEventKinds[kind] = c + 1;
            if (n > 0)
            {
                if (_wetFirstTs == 0)
                {
                    try { _wetFirstTs = e.NewInkPoints![0].Timestamp; } catch { }
                }
                try { _wetLastTs = e.NewInkPoints![n - 1].Timestamp; } catch { }
            }
        }

        bool detail = kind != "Continuing" || _wetDetailLines < 8;
        if (detail)
        {
            _wetDetailLines++;
            int kindCount;
            lock (_inkLock) { _wetEventKinds.TryGetValue(kind, out kindCount); }
            Log($"[{Environment.TickCount64}] CoreWet {kind}#{kindCount} ptr={e.PointerId} disp={e.Disposition} pts={n} tid={Environment.CurrentManagedThreadId}{first}");
        }
    }

    private static void OnIis(string kind, PointerEventArgs a)
    {
        _iisEvents++;
        if (_iisEvents <= 6 || _iisEvents % 50 == 0)
        {
            string line;
            try
            {
                var pt = a.CurrentPoint;
                line = $"[{Environment.TickCount64}] IIS {kind}#{_iisEvents} pos=({pt.Position.X:F0},{pt.Position.Y:F0}) press={pt.Properties.Pressure:F3} dev={pt.PointerDevice.PointerDeviceType}";
            }
            catch (Exception ex)
            {
                line = $"[{Environment.TickCount64}] IIS {kind}#{_iisEvents} detail failed: {ex.Message}";
            }
            Log(line);
        }
    }

    // ---------------- 屏幕像素采样（墨色计数） ----------------

    private sealed class ScreenSampler
    {
        private readonly object _lock = new();
        private readonly List<(long ms, int core, int soft)> _timeline = new();
        private nint _memDc;
        private nint _dib;
        private nint _oldObj;
        private nint _bits;
        private int _w, _h;
        private RECT _rect;
        private bool _ready;
        private int _baseline = -1;
        private int _maxCore;
        private long _firstInkMs = -1;

        public int Baseline { get { lock (_lock) { return _baseline; } } }
        public int MaxCore { get { lock (_lock) { return _maxCore; } } }
        public long FirstInkMs { get { lock (_lock) { return _firstInkMs; } } }
        public int Samples { get { lock (_lock) { return _timeline.Count; } } }

        public bool Init(RECT rect)
        {
            try
            {
                _rect = rect;
                _w = rect.Right - rect.Left;
                _h = rect.Bottom - rect.Top;
                if (_w <= 0 || _h <= 0) { return false; }

                nint screen = GetDC(0);
                _memDc = CreateCompatibleDC(screen);
                ReleaseDC(0, screen);
                if (_memDc == 0) { return false; }

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)sizeof(BITMAPINFOHEADER),
                        biWidth = _w,
                        biHeight = -_h,           // top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0,        // BI_RGB
                    },
                };
                _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, 0, 0);
                if (_dib == 0 || _bits == 0) { return false; }
                _oldObj = SelectObject(_memDc, _dib);
                _ready = true;
                return true;
            }
            catch (Exception ex)
            {
                Log("sampler init failed: " + ex.Message);
                return false;
            }
        }

        public void Sample()
        {
            if (!_ready) { return; }
            try
            {
                nint screen = GetDC(0);
                BitBlt(_memDc, 0, 0, _w, _h, screen, _rect.Left, _rect.Top, 0x00CC0020 /*SRCCOPY*/);
                ReleaseDC(0, screen);

                int core = 0, soft = 0;
                byte* p = (byte*)_bits;
                int stride = _w * 4;
                for (int y = 0; y < _h; y++)
                {
                    byte* row = p + (long)y * stride;
                    for (int x = 0; x < _w; x++)
                    {
                        byte b = row[x * 4 + 0], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        if (r >= 190 && b >= 190 && g <= 120) { core++; }
                        else if (r >= 130 && b >= 130 && g <= 170) { soft++; }
                    }
                }

                long now = Environment.TickCount64;
                lock (_lock)
                {
                    if (_baseline < 0) { _baseline = core; }
                    _timeline.Add((now, core, core + soft));
                    if (core > _maxCore) { _maxCore = core; }
                    if (_firstInkMs < 0 && core >= _baseline + 30) { _firstInkMs = now; }
                }
            }
            catch (Exception ex)
            {
                Log("sample failed: " + ex.Message);
                _ready = false;
            }
        }

        public void DisposeAll()
        {
            try
            {
                if (_memDc != 0) { if (_oldObj != 0) { SelectObject(_memDc, _oldObj); } DeleteDC(_memDc); }
                if (_dib != 0) { DeleteObject(_dib); }
            }
            catch { }
            _ready = false;
        }

        public void Dump(StringBuilder sb)
        {
            lock (_lock)
            {
                sb.Append($"screen.samples={_timeline.Count} baseline_core={_baseline} max_core={_maxCore} first_ink_ms={( _firstInkMs<0?"-":_firstInkMs.ToString())}\n");
                sb.Append("screen.timeline(ms,core,soft): ");
                int printed = 0;
                foreach (var t in _timeline)
                {
                    if (printed++ >= 120) { sb.Append("..."); break; }
                    sb.Append(t.ms % 1000000).Append(':').Append(t.core).Append('/').Append(t.soft).Append(' ');
                }
                sb.Append('\n');
            }
        }
    }

    private static readonly ScreenSampler _sampler = new();

    // ---------------- 主流程 ----------------

    [STAThread]
    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            WriteReport();
            return 1;
        }
    }

    private static int Run()
    {
        if (!ParseArgs())
        {
            return 2;
        }

        SetProcessDpiAwarenessContext(-4);

        Log($"{ProbeVersion}");
        Log($"route={_route} input={_input} mainthread={_mainThread} dqc={_dqc} transparent={_transparent} nohost={_noHost} iis={_iis} noface={_noFace} penmouse={_penMouse} activate={_activate} target={_targetSelf}");

        var os = default(RTL_OSVERSIONINFOEXW);
        os.dwOSVersionInfoSize = (uint)sizeof(RTL_OSVERSIONINFOEXW);
        int rtl = RtlGetVersion(ref os);
        Log($"OS RtlGetVersion hr=0x{rtl:X8} {os.dwMajorVersion}.{os.dwMinorVersion}.{os.dwBuildNumber} v{Environment.OSVersion.VersionString} 64bit={Environment.Is64BitProcess}");

        GetWorkArea(out var work);
        Log($"workarea {work.Left},{work.Top} {work.Right - work.Left}x{work.Bottom - work.Top}");

        if (!CreateAppWindow(work))
        {
            Log("window creation failed");
            return 2;
        }

        uint dpi = GetDpiForWindow(_hwnd);
        Log($"window ok 0x{_hwnd:X} exstyle=0x{_exStyle:X} dpi={dpi}");
        Log("EnableMouseInPointer=" + EnableMouseInPointer(true));

        if (_activate)
        {
            nint fg0 = GetForegroundWindow();
            uint fgTid = GetWindowThreadProcessId(fg0, out _);
            uint myTid = GetCurrentThreadId();
            AttachThreadInput(myTid, fgTid, true);
            bool okFg = SetForegroundWindow(_hwnd);
            nint okAw = SetActiveWindow(_hwnd);
            SetFocus(_hwnd);
            AttachThreadInput(myTid, fgTid, false);
            Log($"activate: SetForegroundWindow={okFg} SetActiveWindow=0x{okAw:X} fgNow=0x{GetForegroundWindow():X} ours=0x{_hwnd:X}");
        }

        GetWindowRect(_hwnd, out var wrect);
        if (!_sampler.Init(wrect))
        {
            Log("sampler unavailable (继续，只有消息计数)");
        }

        bool hostReady;
        if (_route == "inkdesktop")
        {
            hostReady = SetupInkDesktop();
        }
        else
        {
            hostReady = SetupCoreHost();
        }
        Log("host ready=" + hostReady);

        bool fg = SetForegroundWindow(_hwnd);
        Log($"SetForegroundWindow={fg} fgHwnd=0x{GetForegroundWindow():X} ours=0x{_hwnd:X}");

        // 基准像素：注入前静置 300ms，采 3 次
        for (int i = 0; i < 3; i++)
        {
            _sampler.Sample();
            Thread.Sleep(100);
        }

        long start = Environment.TickCount64;
        if (_input == "synthetic")
        {
            var t = new Thread(() =>
            {
                Thread.Sleep(150);
                InjectSyntheticStroke();
            })
            { IsBackground = true };
            t.Start();
        }
        else if (_input == "mouse")
        {
            var t = new Thread(() =>
            {
                Thread.Sleep(150);
                InjectMouseDrag();
            })
            { IsBackground = true };
            t.Start();
        }
        else // pentest
        {
            CreateTipWindow(work);
            RegisterHotKey(_hwnd, 1, 0x7 /*CTRL|ALT|SHIFT*/, 0x51 /*Q*/);
            Log($"PENTEST 就绪：请在屏幕中部横带里用真笔写 3~5 笔；Ctrl+Alt+Shift+Q 结束；{_pentestSecs}s 自动结束");
        }

        long end = start + (_input == "pentest" ? _pentestSecs * 1000 : _secs * 1000);
        long sampleInterval = _input == "pentest" ? 250 : 100;
        long nextSample = start;

        while (!_stop && Environment.TickCount64 < end)
        {
            while (PeekMessageW(out var msg, 0, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            if (Environment.TickCount64 >= nextSample)
            {
                nextSample += sampleInterval;
                _sampler.Sample();
            }

            Thread.Sleep(5);
        }

        // 尾帧：注入结束后再采 1s
        if (_input != "pentest")
        {
            long tailEnd = Environment.TickCount64 + 1000;
            while (Environment.TickCount64 < tailEnd)
            {
                _sampler.Sample();
                Thread.Sleep(100);
            }
        }

        Log("==== 结果 ====");
        Log("wnd " + CountsDump());
        Log("pointer ids: " + PointerIdsDump());
        Log("pointer types seen: " + string.Join(",", _pointerTypesSeen));
        var ink = new StringBuilder();
        DumpInkCounters(ink);
        foreach (var line in ink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) { Log(line); }
        var scr = new StringBuilder();
        _sampler.Dump(scr);
        foreach (var line in scr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) { Log(line); }
        Log(Verdict());

        string path = WriteReport();
        Log("REPORT: " + path);

        if (_input == "pentest" && !_nomsg)
        {
            string verdict = Get("wm_pointer_down") > 0 || _strokeCount > 0 || _wetPoints > 0 || _sampler.MaxCore > _sampler.Baseline + 30
                ? "✓ 窗口/宿主有动静（详见报告）" : "✗ 什么都没收到";
            MessageBoxW(0,
                "测试结束：" + verdict + "\r\n\r\n"
                + "窗口消息（指针 下/移/上）：" + Get("wm_pointer_down") + "/" + Get("wm_pointer_update") + "/" + Get("wm_pointer_up") + "\r\n"
                + "StrokesCollected 笔数=" + _strokeCount + "  CoreWet 点=" + _wetPoints + "\r\n"
                + "屏幕墨色像素 峰值=" + _sampler.MaxCore + "（基线 " + _sampler.Baseline + "）\r\n\r\n"
                + "报告：\r\n" + path + "\r\n\r\n"
                + "请回忆：写字时屏幕上有系统画的墨迹吗？（报告里也有数字）",
                "InkHostProbe 真笔测试", 0x40 | 0x1000);
        }

        return Get("wm_pointer_down") > 0 || _strokeCount > 0 || _wetPoints > 0 ? 0 : 3;
    }

    private static string PointerIdsDump()
    {
        lock (_cntLock)
        {
            return string.Join(" ", _pointerIdCounts.OrderBy(kv => kv.Key).Select(kv => $"0x{kv.Key:X}:{kv.Value}"));
        }
    }

    private static string Verdict()
    {
        bool presenterInput = _strokeCount > 0 || _wetPoints > 0;
        bool coreWetPoints = _wetPoints > 0;
        bool windowGotInput = Get("wm_pointer_down") + Get("wm_pointer_update") + Get("wm_pointer_up") > 0;
        bool screenInk = _sampler.MaxCore >= _sampler.Baseline + 30;
        string v = "VERDICT:";
        v += presenterInput ? " presenter-received-input=YES" : " presenter-received-input=NO";
        v += coreWetPoints ? " corewet-points=YES" : " corewet-points=NO";
        v += windowGotInput ? " window-got-pointer=YES" : " window-got-pointer=NO";
        v += screenInk ? " screen-ink=YES" : " screen-ink=NO";
        return v;
    }

    private static bool ParseArgs()
    {
        var cli = Environment.GetCommandLineArgs();
        for (int i = 1; i < cli.Length; i++)
        {
            string a = cli[i];
            switch (a)
            {
                case "--route":
                    if (++i >= cli.Length) { return Help("--route 缺参数"); }
                    _route = cli[i];
                    if (_route != "corehost" && _route != "inkdesktop") { return Help("route 只能是 corehost | inkdesktop"); }
                    break;
                case "--input":
                    if (++i >= cli.Length) { return Help("--input 缺参数"); }
                    _input = cli[i];
                    if (_input != "synthetic" && _input != "mouse" && _input != "pentest") { return Help("input 只能是 synthetic | mouse | pentest"); }
                    break;
                case "--mainthread": _mainThread = true; break;
                case "--dqc": _dqc = true; break;
                case "--transparent": _transparent = true; break;
                case "--nohost": _noHost = true; break;
                case "--iis": _iis = true; break;
                case "--noface": _noFace = true; break;
                case "--penmouse": _penMouse = true; break;
                case "--activate": _activate = true; break;
                case "--target": _targetSelf = true; break;
                case "--nomsg": _nomsg = true; break;
                case "--secs":
                    if (++i >= cli.Length || !long.TryParse(cli[i], out _secs)) { return Help("--secs 需要数字"); }
                    break;
                case "--pentestsecs":
                    if (++i >= cli.Length || !long.TryParse(cli[i], out _pentestSecs)) { return Help("--pentestsecs 需要数字"); }
                    break;
                case "--out":
                    if (++i >= cli.Length) { return Help("--out 缺参数"); }
                    _outPath = cli[i];
                    break;
                default:
                    return Help("未知参数: " + a);
            }
        }
        return true;
    }

    private static bool Help(string why)
    {
        Console.WriteLine("InkHostProbe —— " + why);
        Console.WriteLine();
        Console.WriteLine("用法：InkHostProbe --route corehost|inkdesktop [--input synthetic|mouse|pentest] [选项]");
        Console.WriteLine("  --route corehost     CoreInkPresenterHost + CoreWet 旁听（默认）");
        Console.WriteLine("  --route inkdesktop   IInkDesktopHost + IInkPresenterDesktop（QI WinRT 面）");
        Console.WriteLine("  --input synthetic    合成笔注入（默认）");
        Console.WriteLine("  --input mouse        合成鼠标拖拽");
        Console.WriteLine("  --input pentest      真笔人工书写（Ctrl+Alt+Shift+Q 提前结束）");
        Console.WriteLine("  --mainthread         corehost：在主线程装配（默认专用 DispatcherQueue 线程）");
        Console.WriteLine("  --dqc                inkdesktop：尝试先在主线程建 DispatcherQueue");
        Console.WriteLine("  --transparent        窗口加 WS_EX_TRANSPARENT");
        Console.WriteLine("  --nohost             只建窗口与注入（对照，不建任何宿主）");
        Console.WriteLine("  --iis                corehost：另挂 CoreInkIndependentInputSource 对照计数");
        Console.WriteLine("  --noface             inkdesktop：不尝试 QI WinRT InkPresenter 面");
        Console.WriteLine("  --penmouse           主持的 InputDeviceTypes 加 Mouse");
        Console.WriteLine("  --activate           抢前台（AttachThreadInput 完整版）");
        Console.WriteLine("  --target             合成指针 hwndTarget 直投本窗口");
        Console.WriteLine("  --secs N             合成/鼠标模式总时长（默认 9）");
        Console.WriteLine("  --pentestsecs N      真笔模式时长（默认 120）");
        Console.WriteLine("  --out PATH           报告输出路径");
        Console.WriteLine("  --nomsg              真笔模式结束不弹窗");
        return false;
    }

    // ---------------- route corehost ----------------

    private static DispatcherQueueController? _dqcKeeper;

    private static bool SetupCoreHost()
    {
        if (_noHost)
        {
            Log("--nohost：跳过宿主创建（对照）");
            return false;
        }

        bool ready = false;
        void SetupInk()
        {
            var compositor = new Compositor();
            Log("compositor ok");

            var target = CreateDesktopWindowTarget(compositor, _hwnd, true);
            Log("desktop window target ok=" + (target is not null));

            var root = compositor.CreateContainerVisual();
            target!.Root = root;

            if (_noHost)
            {
                ready = true;
                return;
            }

            var host = new CoreInkPresenterHost();
            var presenter = host.InkPresenter;
            presenter.InputDeviceTypes = InputDeviceTypesFor();

            var attrs = presenter.CopyDefaultDrawingAttributes();
            attrs.Color = InkColor;
            try { attrs.Size = new Windows.Foundation.Size(3, 3); } catch { }
            try
            {
                attrs.ModelerAttributes.PredictionTime = TimeSpan.FromMilliseconds(15);
                Log("PredictionTime=15ms ok");
            }
            catch (Exception ex) { Log("PredictionTime set failed: " + ex.Message); }
            presenter.UpdateDefaultDrawingAttributes(attrs);

            presenter.StrokesCollected += (s, a) => OnStrokesCollected(s, a);

            // 旁听口：CoreWetStrokeUpdateSource（方案 §5.2 的 5 号计数）
            try
            {
                var wet = CoreWetStrokeUpdateSource.Create(presenter);
                wet.WetStrokeStarting += (s, e) => OnWet("Starting", e);
                wet.WetStrokeContinuing += (s, e) => OnWet("Continuing", e);
                wet.WetStrokeStopping += (s, e) => OnWet("Stopping", e);
                wet.WetStrokeCompleted += (s, e) => OnWet("Completed", e);
                wet.WetStrokeCanceled += (s, e) => OnWet("Canceled", e);
                Log("CoreWetStrokeUpdateSource.Create ok + 5 events subscribed");
            }
            catch (Exception ex)
            {
                Log("CoreWet create FAILED: " + ex.Message);
            }

            if (_iis)
            {
                try
                {
                    var iis = CoreInkIndependentInputSource.Create(presenter);
                    iis.PointerEntering += (s, a) => OnIis("Entering", a);
                    iis.PointerHovering += (s, a) => OnIis("Hovering", a);
                    iis.PointerPressing += (s, a) => OnIis("Pressing", a);
                    iis.PointerMoving += (s, a) => OnIis("Moving", a);
                    iis.PointerReleasing += (s, a) => OnIis("Releasing", a);
                    iis.PointerExiting += (s, a) => OnIis("Exiting", a);
                    iis.PointerLost += (s, a) => OnIis("Lost", a);
                    Log("CoreInkIndependentInputSource.Create ok + events subscribed");
                }
                catch (Exception ex) { Log("IIS create/subscribe FAILED: " + ex.Message); }
            }

            var inkVisual = compositor.CreateContainerVisual();
            root.Children.InsertAtTop(inkVisual);
            host.RootVisual = inkVisual;

            try
            {
                presenter.IsInputEnabled = true;
                Log("IsInputEnabled readback=" + presenter.IsInputEnabled);
            }
            catch (Exception ex) { Log("IsInputEnabled set failed: " + ex.Message); }

            Log("presenter.InputDeviceTypes=" + SafeInputDeviceTypes(presenter));
            ready = true;
        }

        if (_mainThread)
        {
            Log("mainthread：用 CoreMessaging 在主线程建 DispatcherQueue（枚举组合）...");
            foreach (int tt in new[] { 2, 1 })
            {
                bool got = false;
                foreach (int apt in new[] { 0, 1, 2 })
                {
                    var opt = new DispatcherQueueOptions { Size = 12, ThreadType = (uint)tt, ApartmentType = (uint)apt };
                    int hr = CreateDispatcherQueueController(opt, out nint ctl);
                    Log($"  tt={tt} apt={apt} hr=0x{hr:X8} ctl=0x{ctl:X}");
                    if (hr >= 0 && ctl != 0) { got = true; break; }
                }
                if (got) { break; }
            }
            try { SetupInk(); }
            catch (Exception ex) { Log("setup (main thread) failed: " + ex); }
        }
        else
        {
            _dqcKeeper = DispatcherQueueController.CreateOnDedicatedThread();
            Log("DispatcherQueueController(dedicated) ok=" + (_dqcKeeper is not null));
            using var done = new ManualResetEventSlim(false);
            _dqcKeeper!.DispatcherQueue.TryEnqueue(() =>
            {
                try { SetupInk(); }
                catch (Exception ex) { Log("setup on dedicated thread failed: " + ex); }
                finally { done.Set(); }
            });
            done.Wait(6000);
        }

        return ready;
    }

    private static CoreInputDeviceTypes InputDeviceTypesFor()
    {
        var t = CoreInputDeviceTypes.Pen;
        if (_penMouse || _input == "mouse") { t |= CoreInputDeviceTypes.Mouse; }
        return t;
    }

    private static string SafeInputDeviceTypes(InkPresenter p)
    {
        try { return p.InputDeviceTypes.ToString(); } catch (Exception ex) { return "<read failed: " + ex.Message + ">"; }
    }

    // ---------------- route inkdesktop ----------------

    private static readonly Guid IID_IDCompositionDesktopDevice = new("5F4633FE-1E08-4CB8-8C75-CE24333F5602");
    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    private static readonly Guid CLSID_InkDesktopHost = new("062584a6-f830-4bdc-a4d2-0a10ab062b1d");
    private static readonly Guid IID_IInkDesktopHost = new("4ce7d875-a981-4140-a1ff-ad93258e8d59");
    private static readonly Guid IID_IInkPresenterDesktop = new("73f3c0d9-2e8b-48f3-895e-20cbd27b723b");
    private static readonly Guid IID_IInkPresenter = new("a69b70e2-887b-458f-b173-4fe4438930a3");

    private static bool SetupInkDesktop()
    {
        int coHr = CoInitializeEx(0, 0x2 /*COINIT_APARTMENTTHREADED*/);
        Log($"CoInitializeEx(STA) hr=0x{coHr:X8}");
        if (coHr == unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE
        {
            Log("  （线程已是其它套间；继续）");
        }

        if (_dqc)
        {
            foreach (int tt in new[] { 2, 1 })
            {
                bool got = false;
                foreach (int apt in new[] { 0, 1, 2 })
                {
                    var opt = new DispatcherQueueOptions { Size = 12, ThreadType = (uint)tt, ApartmentType = (uint)apt };
                    int hr = CreateDispatcherQueueController(opt, out nint ctl);
                    Log($"  dqc tt={tt} apt={apt} hr=0x{hr:X8} ctl=0x{ctl:X}");
                    if (hr >= 0 && ctl != 0) { got = true; break; }
                }
                if (got) { break; }
            }
        }

        // 1) 经典 DComp 设备（null rendering device）
        nint device = 0;
        int hrDev = DCompositionCreateDevice2(0, in IID_IDCompositionDesktopDevice, out device);
        Log($"DCompositionCreateDevice2(desktop) hr=0x{hrDev:X8} ptr=0x{device:X}");
        if (hrDev < 0 || device == 0)
        {
            nint dev1 = 0;
            int hr1 = DCompositionCreateDevice(0, in IID_IDCompositionDevice, out dev1);
            Log($"  fallback DCompositionCreateDevice hr=0x{hr1:X8} ptr=0x{dev1:X}");
            if (hr1 >= 0 && dev1 != 0)
            {
                int qiHr = QI(dev1, in IID_IDCompositionDesktopDevice, out device);
                Log($"  QI IDCompositionDesktopDevice hr=0x{qiHr:X8} ptr=0x{device:X}");
                Marshal.Release(dev1);
            }
        }
        if (device == 0)
        {
            Log("DComp 设备创建失败，route inkdesktop 无法继续");
            return false;
        }

        // 2) target + root visual
        nint visual = 0;
        var createVisual = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(device, 6);
        int hrVis = createVisual(device, &visual);
        Log($"IDCompositionDevice2::CreateVisual hr=0x{hrVis:X8} ptr=0x{visual:X}");

        nint target = 0;
        if (!_noHost)
        {
            var createTarget = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Vt(device, 24);
            int hrTgt = createTarget(device, _hwnd, 1, &target);
            Log($"IDCompositionDesktopDevice::CreateTargetForHwnd hr=0x{hrTgt:X8} ptr=0x{target:X}");
            if (hrTgt >= 0 && target != 0 && visual != 0)
            {
                var setRoot = (delegate* unmanaged[Stdcall]<nint, nint, int>)Vt(target, 3);
                int hrRoot = setRoot(target, visual);
                Log($"IDCompositionTarget::SetRoot hr=0x{hrRoot:X8}");
            }

            var commit = (delegate* unmanaged[Stdcall]<nint, int>)Vt(device, 3);
            int hrCommit = commit(device);
            Log($"IDCompositionDevice2::Commit hr=0x{hrCommit:X8}");
        }

        if (_noHost)
        {
            Log("--nohost：跳过 InkDesktopHost 创建（对照）");
            return visual != 0;
        }

        // 3) InkDesktopHost
        nint host = 0;
        int hrHost = CoCreateInstance(in CLSID_InkDesktopHost, 0, 0x17 /*CLSCTX_ALL*/, in IID_IInkDesktopHost, out host);
        Log($"CoCreateInstance(CLSID_InkDesktopHost) hr=0x{hrHost:X8} ptr=0x{host:X}");
        if (hrHost < 0 || host == 0)
        {
            Log("InkDesktopHost 创建失败");
            return false;
        }

        float scale = GetDpiForWindow(_hwnd) / 96f;
        GetWindowRect(_hwnd, out var wr);
        float wDip = (wr.Right - wr.Left) / scale;
        float hDip = (wr.Bottom - wr.Top) / scale;

        nint presenterDesktop = 0;
        Guid riid = IID_IInkPresenterDesktop;
        var createAndInit = (delegate* unmanaged[Stdcall]<nint, nint, float, float, Guid*, nint*, int>)Vt(host, 5);
        int hrCreate = createAndInit(host, visual, wDip, hDip, &riid, &presenterDesktop);
        Log($"CreateAndInitializeInkPresenter({wDip:F0}x{hDip:F0} dip) hr=0x{hrCreate:X8} ptr=0x{presenterDesktop:X}");
        if (hrCreate < 0 || presenterDesktop == 0)
        {
            return false;
        }

        // IInkPresenterDesktop 面：GetSize / SetRootVisual（设备一起给）
        var getSize = (delegate* unmanaged[Stdcall]<nint, float*, float*, int>)Vt(presenterDesktop, 5);
        float gw = 0, gh = 0;
        int hrSize = getSize(presenterDesktop, &gw, &gh);
        Log($"IInkPresenterDesktop::GetSize hr=0x{hrSize:X8} => {gw}x{gh} dip");

        var setRootVisual = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Vt(presenterDesktop, 3);
        int hrSetRoot = setRootVisual(presenterDesktop, visual, device);
        Log($"IInkPresenterDesktop::SetRootVisual hr=0x{hrSetRoot:X8}");

        var commit2 = (delegate* unmanaged[Stdcall]<nint, int>)Vt(device, 3);
        Log($"Commit(after setroot) hr=0x{commit2(device):X8}");

        // 4) 尝试取得 WinRT InkPresenter 面（方案 §2.3 B 的关键未知）
        if (!_noFace)
        {
            int hrFace = QI(presenterDesktop, in IID_IInkPresenter, out nint face);
            Log($"QI IInkPresenter hr=0x{hrFace:X8} ptr=0x{face:X}");
            if (hrFace >= 0 && face != 0)
            {
                try
                {
                    var presenter = MarshalInspectable<InkPresenter>.FromAbi(face);
                    Log("WinRT InkPresenter 面获取成功: " + presenter.GetType().FullName);
                    WirePresenter(presenter);
                }
                catch (Exception ex)
                {
                    Log("WinRT 面包裹失败（QI 成功但 CsWinRT 不认）: " + ex.Message);
                }
            }
            else
            {
                Log("WinRT InkPresenter 面不可用（QI 失败）——本 route 无法配置输入/无法旁听");
            }
        }
        else
        {
            Log("--noface：跳过 QI WinRT InkPresenter 面");
        }

        return true;
    }

    private static void WirePresenter(InkPresenter presenter)
    {
        try { presenter.InputDeviceTypes = InputDeviceTypesFor(); } catch (Exception ex) { Log("set InputDeviceTypes failed: " + ex.Message); }
        try { presenter.IsInputEnabled = true; } catch (Exception ex) { Log("set IsInputEnabled failed: " + ex.Message); }
        try { Log("face InputDeviceTypes=" + presenter.InputDeviceTypes + " IsInputEnabled=" + presenter.IsInputEnabled); } catch { }

        try
        {
            var attrs = presenter.CopyDefaultDrawingAttributes();
            attrs.Color = InkColor;
            presenter.UpdateDefaultDrawingAttributes(attrs);
            Log("face drawing attrs set (magenta)");
        }
        catch (Exception ex) { Log("set drawing attrs failed: " + ex.Message); }

        try
        {
            presenter.StrokesCollected += (s, a) => OnStrokesCollected(s, a);
            Log("face StrokesCollected subscribed");
        }
        catch (Exception ex) { Log("subscribe StrokesCollected failed: " + ex.Message); }

        try
        {
            var wet = CoreWetStrokeUpdateSource.Create(presenter);
            wet.WetStrokeStarting += (s, e) => OnWet("Starting", e);
            wet.WetStrokeContinuing += (s, e) => OnWet("Continuing", e);
            wet.WetStrokeStopping += (s, e) => OnWet("Stopping", e);
            wet.WetStrokeCompleted += (s, e) => OnWet("Completed", e);
            wet.WetStrokeCanceled += (s, e) => OnWet("Canceled", e);
            Log("face CoreWetStrokeUpdateSource.Create ok + 5 events subscribed");
        }
        catch (Exception ex) { Log("face CoreWet create FAILED: " + ex.Message); }
    }

    // ---------------- 合成注入（照抄 coreink-probe v2 的已验证写法） ----------------

    private static int _injectFails;
    private static int _injSamples;
    private static long _injStartTick, _injEndTick;

    private static void SendPenPoint(nint dev, float x, float y, uint pressure, bool contact, bool first)
    {
        uint flags = POINTER_FLAG_INRANGE | POINTER_FLAG_CONFIDENCE;
        if (contact) { flags |= POINTER_FLAG_INCONTACT; }
        if (first) { flags |= POINTER_FLAG_NEW | POINTER_FLAG_PRIMARY | POINTER_FLAG_FIRSTBUTTON; }

        var one = new POINTER_TYPE_INFO();
        one.type = PT_PEN;
        one.pen.pointerInfo.pointerType = PT_PEN;
        one.pen.pointerInfo.pointerFlags = flags;
        one.pen.pointerInfo.ptPixelLocationX = (int)x;
        one.pen.pointerInfo.ptPixelLocationY = (int)y;
        one.pen.pointerInfo.hwndTarget = _targetSelf ? _hwnd : 0;
        one.pen.penFlags = 0;
        one.pen.penMask = PEN_MASK_PRESSURE;
        one.pen.pressure = pressure;

        nint buf = Marshal.AllocHGlobal(Marshal.SizeOf<POINTER_TYPE_INFO>() + 16);
        try
        {
            Marshal.StructureToPtr(one, buf, false);
            if (!InjectSyntheticPointerInput(dev, buf, 1) && _injectFails++ < 3)
            {
                Log("InjectSyntheticPointerInput failed err=" + Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void InjectSyntheticStroke()
    {
        TimeBeginPeriod(1);
        try
        {
            nint dev = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
            Log("CreateSyntheticPointerDevice(PT_PEN) ptr=0x" + dev.ToString("X"));
            if (dev == 0)
            {
                Log("synthetic pen device FAILED");
                return;
            }

            GetWorkArea(out var work);
            float cx = (work.Left + work.Right) / 2f;
            float cy = (work.Top + work.Bottom) / 2f;
            float x0 = cx - 300, y0 = cy + 120;

            for (int i = 0; i < 5; i++)
            {
                SendPenPoint(dev, x0, y0, 0, contact: false, first: false);
                Thread.Sleep(4);
            }

            _injStartTick = Environment.TickCount64;
            int total = 0;

            SendPenPoint(dev, x0, y0, 300, contact: true, first: true);
            total++;
            Thread.Sleep(4);

            const int n1 = 250;
            for (int i = 1; i <= n1; i++)
            {
                float x = x0 + 600f * i / n1;
                uint p = (uint)(300 + Math.Min(1f, i / 40f) * 500);
                SendPenPoint(dev, x, y0, p, contact: true, first: false);
                total++;
                Thread.Sleep(4);
            }

            const int n2 = 180;
            for (int i = 1; i <= n2; i++)
            {
                float y = y0 - 400f * i / n2;
                SendPenPoint(dev, x0 + 600, y, 800, contact: true, first: false);
                total++;
                Thread.Sleep(4);
            }

            SendPenPoint(dev, x0 + 600, y0 - 400, 0, contact: false, first: false);
            total++;
            _injEndTick = Environment.TickCount64;
            _injSamples = total;
            Log($"inject done: samples={total} dur={_injEndTick - _injStartTick}ms");
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private static void InjectMouseDrag()
    {
        TimeBeginPeriod(1);
        try
        {
            GetWorkArea(out var work);
            float cx = (work.Left + work.Right) / 2f;
            float cy = (work.Top + work.Bottom) / 2f;
            float x0 = cx - 250, y0 = cy + 60;

            SetCursorPos((int)x0, (int)y0);
            Thread.Sleep(60);
            _injStartTick = Environment.TickCount64;
            int total = 0;

            MouseEvent(MOUSEEVENTF_LEFTDOWN, 0, 0);
            total++;
            const int n = 220;
            for (int i = 1; i <= n; i++)
            {
                SetCursorPos((int)(x0 + 500f * i / n), (int)y0);
                total++;
                Thread.Sleep(5);
            }

            MouseEvent(MOUSEEVENTF_LEFTUP, 0, 0);
            total++;
            _injEndTick = Environment.TickCount64;
            _injSamples = total;
            Log($"inject mouse drag done: samples={total} dur={_injEndTick - _injStartTick}ms");
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    // ---------------- 报告 ----------------

    private static string WriteReport()
    {
        try
        {
            string name = $"ink-host-probe-{_route}-{_input}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            string path = _outPath ?? ResolveReportPath(name);
            var sb = new StringBuilder();
            sb.Append("InkHostProbe 报告\n");
            sb.Append("生成: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("probe: ").Append(ProbeVersion).Append('\n');
            sb.Append("本文件由 tools/ink-host-probe 自动生成；同口径说明见探针源码头部注释。\n");
            sb.Append("说明：screen.max_core 为窗口区域「墨色核心像素」峰值（颜色判定 r>=190,b>=190,g<=120，墨色=纯品红）；baseline 为注入前基线。\n");
            sb.Append('\n');
            sb.Append(LogText());
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            Log("写报告失败: " + ex.Message);
            return "(写入失败)";
        }
    }

    private static string ResolveReportPath(string name)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "reports")) && Directory.Exists(Path.Combine(dir, "tools")))
            {
                return Path.Combine(dir, "reports", name);
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(Environment.CurrentDirectory, name);
    }

    // ---------------- Win32 基础设施 ----------------

    private static nint _hwnd;
    private static long _exStyle;
    private static volatile bool _stop;

    private static bool CreateAppWindow(RECT work)
    {
        var hInstance = GetModuleHandleW(null);
        var className = "InkHostProbeWnd";
        var classNamePtr = Marshal.StringToHGlobalUni(className);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = hInstance,
            hCursor = LoadCursorW(0, 32512),
            lpszClassName = classNamePtr,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            return false;
        }

        _exStyle = 0x00200000 /*NOREDIRECTIONBITMAP*/ | 0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/;
        if (_transparent) { _exStyle |= 0x20 /*TRANSPARENT*/; }
        _hwnd = CreateWindowExW(
            _exStyle,
            className,
            "InkHostProbe",
            0x80000000 /*POPUP*/ | 0x10000000 /*VISIBLE*/,
            work.Left,
            work.Top,
            work.Right - work.Left,
            work.Bottom - work.Top,
            0,
            0,
            hInstance,
            0);
        return _hwnd != 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x0245: // WM_POINTERUPDATE
                Bump("wm_pointer_update");
                TrackPointer(wParam);
                return 0;
            case 0x0246: // WM_POINTERDOWN
                Bump("wm_pointer_down");
                TrackPointer(wParam);
                return 0;
            case 0x0247: // WM_POINTERUP
                Bump("wm_pointer_up");
                TrackPointer(wParam);
                return 0;
            case 0x0249: // WM_POINTERENTER
                Bump("wm_pointer_enter");
                return 0;
            case 0x024A: // WM_POINTERLEAVE
                Bump("wm_pointer_leave");
                return 0;
            case 0x024C: // WM_POINTERCAPTURECHANGED
                Bump("wm_pointer_capture_changed");
                return 0;
            case 0x0200: // WM_MOUSEMOVE
                Bump("wm_mouse_move");
                return 0;
            case 0x0201: // WM_LBUTTONDOWN
                Bump("wm_lbutton_down");
                return 0;
            case 0x0202: // WM_LBUTTONUP
                Bump("wm_lbutton_up");
                return 0;
            case 0x0312: // WM_HOTKEY
                _stop = true;
                return 0;
            case 0x0084: // WM_NCHITTEST
                return 1; // HTCLIENT
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void TrackPointer(nint pointerId)
    {
        uint id = (uint)pointerId;
        lock (_cntLock)
        {
            _pointerIdCounts.TryGetValue(id, out int c);
            _pointerIdCounts[id] = c + 1;
        }
        if (GetPointerType(id, out uint type))
        {
            _pointerTypesSeen.Add(type switch { 1 => "touch", 2 => "pen", 3 => "mouse", _ => "type" + type });
        }
    }

    // ---------------- 真笔提示窗（GDI，最顶层，不参与书写区） ----------------

    private static nint _tipHwnd;
    private static nint _tipBrush;

    private static void CreateTipWindow(RECT work)
    {
        var hInstance = GetModuleHandleW(null);
        var className = "InkHostProbeTip";
        var classNamePtr = Marshal.StringToHGlobalUni(className);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&TipWndProc,
            hInstance = hInstance,
            lpszClassName = classNamePtr,
        };
        RegisterClassExW(ref wc);

        int w = 1400, h = 72;
        int x = (work.Left + work.Right) / 2 - w / 2;
        int y = work.Bottom - h - 40;
        _tipHwnd = CreateWindowExW(
            0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/,
            className,
            "tip",
            0x80000000 /*POPUP*/ | 0x10000000 /*VISIBLE*/,
            x, y, w, h, 0, 0, hInstance, 0);
        _tipBrush = CreateSolidBrush(0x00202020);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static nint TipWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x000F) // WM_PAINT
        {
            var hdc = BeginPaint(hwnd, out var ps);
            GetClientRect(hwnd, out var rc);
            FillRect(hdc, ref rc, _tipBrush);
            SetBkMode(hdc, 1);
            SetTextColor(hdc, 0x00FFFFFF);
            string text = $"InkHostProbe 真笔测试（{_route}）：请在屏幕中部横带里写 3~5 笔；Ctrl+Alt+Shift+Q 结束（{_pentestSecs}s 自动结束）";
            DrawTextW(hdc, text, -1, ref rc, 0x25 /*CENTER|VCENTER|SINGLELINE*/);
            EndPaint(hwnd, ref ps);
            return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static Windows.UI.Color InkColor => Windows.UI.Color.FromArgb(255, 255, 0, 255);

    private static DesktopWindowTarget? CreateDesktopWindowTarget(Compositor compositor, nint hwnd, bool topmost)
    {
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
        nint abi = MarshalInspectable<Compositor>.FromManaged(compositor);
        nint interop = QueryInterface(abi, in iid);
        Log("ICompositorDesktopInterop QI ok 0x" + interop.ToString("X"));

        nint targetPtr = 0;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)(*(nint*)(*(nint*)interop + 3 * 8));
        int hr = create(interop, hwnd, topmost ? 1 : 0, &targetPtr);
        Log($"CreateDesktopWindowTarget hr=0x{hr:X8} ptr=0x{targetPtr:X}");
        if (hr < 0 || targetPtr == 0)
        {
            throw new COMException("CreateDesktopWindowTarget failed", hr);
        }

        return MarshalInspectable<DesktopWindowTarget>.FromAbi(targetPtr);
    }

    private static nint QueryInterface(nint pUnk, in Guid iid)
    {
        var qi = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint*)(*(nint*)pUnk));
        nint result = 0;
        var g = iid;
        int hr = qi(pUnk, &g, &result);
        if (hr < 0 || result == 0)
        {
            throw new COMException("QueryInterface failed", hr);
        }

        return result;
    }

    private static int QI(nint pUnk, in Guid iid, out nint result)
    {
        result = 0;
        if (pUnk == 0) { return unchecked((int)0x80004003); }
        var qi = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint*)(*(nint*)pUnk));
        var g = iid;
        nint r = 0;
        int hr = qi(pUnk, &g, &r);
        result = r;
        return hr;
    }

    private static nint Vt(nint p, int slot) => *(nint*)(*(nint*)p + slot * sizeof(nint));

    // ---------------- P/Invoke ----------------

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern int SystemParametersInfoW(uint action, uint param, ref RECT data, uint winIni);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);

    private static void MouseEvent(uint flags, uint dx, uint dy) => mouse_event(flags, dx, dy, 0, 0);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(long exStyle, string className, string windowName, long style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint instance, int cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool EnableMouseInPointer(bool enable);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RECT rc);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT rc, nint brush);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint hdc, string text, int count, ref RECT rc, uint format);

    // 屏幕采样
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO bmi, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int x, int y, int w, int h, nint hdcSrc, int sx, int sy, uint rop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(nint device, nint pointerInfo, uint count);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW info);

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice(nint dxgiDevice, in Guid iid, out nint device);

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice2(nint renderingDevice, in Guid iid, out nint device);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint clsctx, in Guid iid, out nint ppv);

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    // ---------------- 结构体 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public unsafe fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public uint Size;
        public uint ThreadType;
        public uint ApartmentType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RTL_OSVERSIONINFOEXW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        public unsafe fixed char szCSDVersion[128];
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public unsafe fixed uint bmiColors[3];
    }

    // 合成笔结构体（照抄 coreink-probe / InkTeach Native.cs）
    private const uint PT_PEN = 3;
    private const uint POINTER_FLAG_NEW = 0x00000001;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_FIRSTBUTTON = 0x00000010;
    private const uint POINTER_FLAG_PRIMARY = 0x00002000;
    private const uint POINTER_FLAG_CONFIDENCE = 0x00004000;
    private const uint PEN_MASK_PRESSURE = 0x00000001;
    private const uint POINTER_FEEDBACK_DEFAULT = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public nint sourceDevice;
        public nint hwndTarget;
        public int ptPixelLocationX, ptPixelLocationY;
        public int ptHimetricLocationX, ptHimetricLocationY;
        public int ptPixelLocationRawX, ptPixelLocationRawY;
        public int ptHimetricLocationRawX, ptHimetricLocationRawY;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX, tiltY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TYPE_INFO
    {
        public uint type;
        public POINTER_PEN_INFO pen;
        public unsafe fixed byte unionPad[24];
    }

    private static void GetWorkArea(out RECT rect)
    {
        rect = default;
        SystemParametersInfoW(0x0030, 0, ref rect, 0);
    }
}
