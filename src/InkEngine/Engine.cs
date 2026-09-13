using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

internal enum PassThroughMode
{
    /// <summary>WM_NCHITTEST -> HTTRANSPARENT only (safe, but the documented
    /// hand-off only reaches windows owned by the same thread).</summary>
    HitTest = 0,

    /// <summary>Adds WS_EX_TRANSPARENT to the window style on top of HitTest.</summary>
    ExTransparent = 1,

    /// <summary>Adds WS_EX_LAYERED | WS_EX_TRANSPARENT (the classic click-through
    /// recipe). Kept as a probe because it may disturb DirectComposition.</summary>
    LayeredTransparent = 2,
}

public class InkEngine
{
    // ---- document / interaction state ------------------------------------
    internal readonly InkDocument Doc = new();
    internal readonly LaserTrail Laser = new();
    internal Stroke ActiveStroke;
    internal Tool Tool = Tool.Pen;
    /// <summary>Tool sizes are authored in logical pixels and scaled by the
    /// monitor DPI at use. Without this everything looks half-size on a 150%
    /// display, which is exactly how a 18px eraser turns into an unusable dot.</summary>
    internal float DpiScale = 1f;
    internal float EraserRadiusLogical = 22f;
    internal float PenWidthLogical = 3f;
    internal float HighlighterWidthLogical = 18f;
    internal float EraserRadius => EraserRadiusLogical * DpiScale;
    private float _lastEraseX, _lastEraseY;

    /// <summary>Pen width presets, in logical pixels. Cycled with Ctrl+Alt+W
    /// until there is a proper on-screen control for it.</summary>
    internal static readonly float[] WidthPresets = { 1.5f, 3f, 6f, 10f, 16f, 24f };
    internal int WidthPresetIndex = 1;

    private double _lastSampleMs = -1;
    internal bool PointerInside;
    internal float PointerX, PointerY;
    internal bool ShowHud = true;
    internal string HudText = "";
    internal bool MarqueeActive;

    // ---- 选择手势（框选工具 = 选择工具）---------------------------------
    // 四种情形在这里分流：点操作条按钮 / 拖手柄 / 在选中范围里整体拖 / 空白处重新框选。
    internal bool SelDragging;
    internal int SelBarHover = -1;          // 悬停的按钮，-1 = 无（给渲染用）
    /// <summary>
    /// 批注键盘模式。开 = 键盘归批注层（编辑快捷键生效）；
    /// 关 = 覆盖层永不抢焦点，键盘还给下层程序。
    ///
    /// 这是个**明确的取舍**：开着的时候放映中的 PPT 收不到键盘，因为覆盖层
    /// 拿着前台焦点。用户拍板"先不考虑 PPT，先把底层做好"，所以默认开，
    /// 用 Ctrl+Alt+K 切换。
    /// </summary>
    internal bool KeyboardMode = true;
    private SelHandle _dragHandle = SelHandle.None;
    private bool _dragIsMove;
    private SelectionFrame _dragFrame;
    private Vector2 _dragStartPoint;
    private Matrix3x2[] _dragStartXform;
    private Stroke[] _dragTargets;
    private Matrix3x2 _selDragMatrix = Matrix3x2.Identity;
    /// <summary>调试开关：不画自己那一笔，只留系统合成器画出来的委托轨迹。</summary>
    internal bool SuppressActiveStroke;
    internal float MqMinX, MqMinY, MqMaxX, MqMaxY;
    internal bool PassThrough;
    /// <summary>
    /// Windows only honours click-through for a *layered* window, so the
    /// default has to include WS_EX_LAYERED. The other modes are kept as
    /// controls for the automated pass-through test.
    /// </summary>
    internal PassThroughMode PassMode = PassThroughMode.LayeredTransparent;
    internal bool NoContentCache;
    internal double NowMs;

    internal static readonly Color4 PenColor = InkPalette.PenDefault;
    internal static readonly Color4 HighlighterColor = InkPalette.HighlighterDefault;

    // 下面这些标成 internal 而不是 private，是给开发期测试宿主看的：
    // InkProbe 通过 InternalsVisibleTo 继承引擎、直接读内部状态来做自动化
    // 测量。产品界面走 IOverlayUi，不碰这些。
    internal readonly List<OverlayWindow> _windows = new();
    /// <summary>穿透时用来"找回界面"的小圆钮（独立窗口，见 RestorePill）。</summary>
    internal RestorePill Pill;
    internal IntPtr _hInstance;
    internal string _className;
    private Native.WndProcDelegate _wndProc;
    private static readonly Dictionary<IntPtr, OverlayWindow> s_map = new();
    /// <summary>属于"恢复小圆钮"的窗口句柄。它们的消息要单独处理。</summary>
    private static readonly HashSet<IntPtr> s_pillHwnds = new();

    internal bool _quit;
    internal bool _dirty = true;
    private bool _animating;
    internal bool _drawing;
    private uint _activePointer;
    private uint _activePointerType;
    internal int _cntDown, _cntMove, _cntUp, _cntCaptureLost;
    internal string _lastStrokeReport;
    private string _lastSimplifyInfo;
    internal bool argsContainActivate;
    /// <summary>调试用：每帧故意睡这么多毫秒，模拟低配机器上渲染跟不上。</summary>
    internal int _artificialLagMs;
    private long _hotkeysRegistered;
    internal int _virtualX, _virtualY, _virtualW, _virtualH;
    internal double _autoExitAt = double.MaxValue;

    // ---- perf accounting --------------------------------------------------
    internal readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
    internal double _fps;
    private int _frames;
    private double _fpsWindowStart;
    private TimeSpan _lastCpu;
    private double _lastCpuWall;
    private double _cpuPercent;
    internal double _peakWorkingSetMb;

    // ---- 界面接入口 -------------------------------------------------------
    // 引擎只认 IOverlayUi。界面通过 UiHost 改引擎状态，永远不直接碰文档。
    internal UiHost Host;
    internal IOverlayUi Ui;
    internal bool UiInvalidatePending;
    internal Color4 CurrentColor = InkPalette.PenDefault;
    internal Color4 HighlighterCurrent = InkPalette.HighlighterDefault;
    /// <summary>
    /// 默认**不做手写美化**，就是等宽笔迹。
    ///
    /// 用户实测后的结论：手写美化"不是那么必须"，而且关掉之后观感更干净。
    /// 所以这里默认走 Precise（等宽、不模拟速度、无渐细）。
    /// 需要时用 --preset handwriting 之类打开，算法代码保留。
    /// </summary>
    internal PenPreset PenPresetValue = PenPreset.Precise;
    internal bool UiCapturing;

    /// <summary>
    /// 白板模式：给整块画布铺一层不透明的底色，遮住桌面和别的程序。
    /// 关掉就是原本的"透明批注"——直接写在别人的 PPT、网页上面。
    /// </summary>
    internal bool BoardOn;
    internal Color4 BoardColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal double _lastRebuildMs;
    internal double _lastRecordMs;
    internal double _lastPresentMs;
    private double _inputToPresentMs;
    private double _lastInputMs = -1;
    internal double _nextLogAt;
    private double _accRecord, _accPresent, _accHud, _accStats;
    private int _accFrames;
    private double _nextStatsAt;
    private double _lastStatsMs;
    private double _workingSetMb;
    private double _lastTopmostMs;

    /// <summary>Everything that talks to the OS for reporting: process times,
    /// working set, CPU share. Runs a few times a second, never per frame.</summary>
    private void UpdateStats()
    {
        var sw = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        double wall = _clock.Elapsed.TotalMilliseconds;
        double cpuDelta = (proc.TotalProcessorTime - _lastCpu).TotalMilliseconds;
        double wallDelta = wall - _lastCpuWall;
        if (wallDelta > 250)
        {
            _cpuPercent = cpuDelta / wallDelta / Environment.ProcessorCount * 100.0;
            _lastCpu = proc.TotalProcessorTime;
            _lastCpuWall = wall;
        }

        _workingSetMb = proc.WorkingSet64 / 1048576.0;
        if (_workingSetMb > _peakWorkingSetMb) _peakWorkingSetMb = _workingSetMb;
        sw.Stop();
        _lastStatsMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Stamped whenever a pointer message is handled, so the loop can
    /// report how long input took to reach the screen.</summary>
    private void StampInput() => _lastInputMs = _clock.Elapsed.TotalMilliseconds;

    internal string LastCaptureReport = "";

    // =====================================================================
    //  Startup
    // =====================================================================

    internal int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Native.EnableMouseInPointer(true);

        Gfx.Init();
        NowMs = _clock.Elapsed.TotalMilliseconds;

        _hInstance = Native.GetModuleHandle(null);
        _className = "InkProbeOverlay_" + Guid.NewGuid().ToString("N");
        if (!RegisterWindowClass())
            return 2;

        string mode = args.Length > 0 ? args[0] : "";

        // 宿主自己的启动分支（开发期的点击目标、截图工具等）。返回 true
        // 表示这条命令行已经由宿主处理完，引擎不再往下走。产品界面不需要覆写。
        if (PrepareHostStartup(mode, args, out int hostExit))
            return hostExit;

        // --nohud：关掉调试性能面板。它是给开发看的，每帧要花约 1.9 ms
        // （文字排版 + 进程计数），测底层性能时必须排除掉，否则量到的是
        // 测量工具本身而不是渲染引擎。
        if (args.Contains("--nohud")) ShowHud = false;

        // 对照实验用：--nohist 关掉脏区的多帧回溯，应当立刻出现残影，
        // 用来证明残影测试本身是有效的（而不是永远通过）。
        if (args.Contains("--nohist")) OverlayWindow.TransientHistoryFrames = 0;
        if (args.Contains("--inktrail")) OverlayWindow.InkTrailEnabled = true;
        if (args.Contains("--norealize")) OverlayWindow.RealizationEnabled = false;
        if (args.Contains("--latencywait")) OverlayWindow.LatencyWaitEnabled = true;
        // 笔迹优化不由引擎决定：核心不认识"平滑""笔锋"这些概念，装不装优化器
        // 由宿主说了算（见 InkOptimizer.cs，以及 InkProbe 里的 --smooth 开关）。
        argsContainActivate = args.Contains("--activate");
        int lagIdx = Array.IndexOf(args, "--laggy");
        if (lagIdx >= 0 && lagIdx + 1 < args.Length && int.TryParse(args[lagIdx + 1], out int lagMs))
            _artificialLagMs = lagMs;

        _virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        _virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        _virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        _virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        Console.WriteLine($"virtual desktop: {_virtualW}x{_virtualH} at ({_virtualX},{_virtualY})");

        if (!CreateOverlays())
            return 3;

        RegisterHotkeys();
        int dispatch = RunModeDispatch(mode, args);
        if (dispatch >= 0)
            return dispatch;

        Loop();
        Shutdown();
        return 0;
    }

    /// <summary>
    /// 启动阶段、窗口创建之前的宿主分支。引擎本身没有这样的模式，
    /// 开发期的截图/点击目标工具挂在宿主（InkProbe）里。
    /// </summary>
    protected virtual bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }

    /// <summary>
    /// 窗口建好、快捷键注册完之后的分支。返回 -1 表示"没有特殊模式，
    /// 正常跑消息循环"；返回 &gt;= 0 表示直接以该值退出。
    /// 产品界面只会拿到 -1；测试模式由 InkProbe 覆写。
    /// </summary>
    protected virtual int RunModeDispatch(string mode, string[] args) => -1;

    private bool RegisterWindowClass()
    {
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
        };
        ushort atom = Native.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private bool CreateOverlays()
    {
        bool ok = true;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var w = new OverlayWindow();
            var err = w.Create(_hInstance, _className, r.Left, r.Top, r.Width, r.Height);
            if (err != null)
            {
                Console.WriteLine($"overlay create failed on monitor {hMon}: {err}");
                ok = false;
                return false;
            }
            s_map[w.Hwnd] = w;
            _windows.Add(w);
            Console.WriteLine($"overlay on monitor {hMon}: {r.Width}x{r.Height} at ({r.Left},{r.Top}) dpi={w.Dpi}");
            Console.WriteLine($"委托墨迹轨迹(InkTrail): {OverlayWindow.InkTrailNote}");
            return true;
        }, IntPtr.Zero);

        if (_windows.Count == 0)
        {
            Console.WriteLine("no monitors found");
            return false;
        }

        // Refresh the HUD ~4x a second so the numbers move even when idle.
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)1, 250, IntPtr.Zero);
        DpiScale = _windows[0].Dpi / 96f;

        // 找回界面用的小圆钮：单独一个窗口，因为它不能带穿透样式
        // （带了就收不到点击，也就没法"点回来"）。平时隐藏，不占地方。
        Pill = new RestorePill();
        var pillErr = Pill.Create(_hInstance, _className, _windows[0].Dpi);
        if (pillErr != null)
        {
            Console.WriteLine("恢复小圆钮创建失败: " + pillErr);
            Pill = null;
        }
        else
        {
            s_pillHwnds.Add(Pill.Hwnd);   // 它的消息由 WndProc 单独分支处理
        }

        Host?.UpdateScreen(LogicalVirtualScreen);
        Console.WriteLine($"DPI 缩放 {DpiScale:F2}（逻辑 {_windows[0].Width / DpiScale:F0}x{_windows[0].Height / DpiScale:F0}）");

        // 把键盘模式落到窗口样式上。字段默认是开的，但样式要等窗口建好才能改——
        // 不调这一句，默认值和实际样式就对不上（得按一次 Ctrl+Alt+K 才生效）。
        SetKeyboardMode(KeyboardMode);
        return ok;
    }

    private void RegisterHotkeys()
    {
        IntPtr h = _windows[0].Hwnd;
        uint mod = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;

        (int id, char key, string desc)[] spec =
        {
            (1,  'P', "穿透模式开关"),
            (2,  '1', "笔"),
            (3,  '2', "荧光笔"),
            (4,  '3', "激光笔"),
            (5,  '4', "橡皮擦"),
            (6,  '5', "框选"),
            (7,  'Z', "撤销"),
            (8,  'C', "清空"),
            (9,  'I', "性能面板"),
            (10, 'B', "性能基准测试"),
            (11, 'M', "显存/内存探测"),
            (12, 'Y', "切换穿透实现方式"),
            (13, 'X', "退出"),
            // 原来用 W，但 Ctrl+Alt+W 被微信占用（微信截图），实测本机注册失败
            // （错误 1409 = 热键已被注册）。换成 6：跟 1~5 挨着，好记，
            // 而且 Ctrl+Alt+6 各家都没占。
            (14, '6', "切换笔迹粗细"),
            (15, 'K', "批注键盘模式开关"),
        };

        foreach (var (id, key, desc) in spec)
        {
            if (Native.RegisterHotKey(h, id, mod, key))
                _hotkeysRegistered++;
            else
                Console.WriteLine($"hotkey Ctrl+Alt+{key} ({desc}) failed: {Marshal.GetLastWin32Error()}");
        }
        Console.WriteLine($"registered {_hotkeysRegistered}/{spec.Length} global hotkeys");
    }

    // =====================================================================
    //  Message loop
    // =====================================================================

    internal void Loop()
    {
        while (!_quit)
        {
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                if (msg.message == 0x0012 /*WM_QUIT*/) { _quit = true; break; }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            if (_quit) break;

            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= _autoExitAt) break;

            Laser.Prune(NowMs);
            _animating = Laser.ActiveAt(NowMs) || _drawing;

            if (_dirty || _animating)
            {
                RenderAll();
                _dirty = false;
            }
            else
            {
                Native.WaitMessage();
            }
        }
    }

    internal void RenderAll()
    {
        double frameStart = NowMs;
        double wall0 = _clock.Elapsed.TotalMilliseconds;

        // The HUD text and the process counters cost an order of magnitude more
        // than drawing the ink does, so they refresh a few times a second rather
        // than every frame. Measured before this change: 2.7 ms + 3.5 ms per
        // frame, against 0.7 ms for the actual rendering.
        var swHud = Stopwatch.StartNew();
        bool statsRan = false;
        if (wall0 >= _nextStatsAt)
        {
            // 面板显示时才需要 4 Hz 刷新；只打日志的话 1 Hz 就够。
            // 进程计数一次要 ~3 ms，没必要每秒跑 4 次。
            _nextStatsAt = wall0 + (ShowHud ? 250 : 1000);
            UpdateStats();
            // 只在真的要显示面板时才拼字符串。BuildHudText 会去查进程内存，
            // 那是整条路径里最贵的一步；面板关掉还在跑就纯属浪费——
            // 实测它让空闲 CPU 从 ~0.3% 涨到 2~3.6%（单核）。
            if (ShowHud) HudText = BuildHudText();
            statsRan = true;
        }
        swHud.Stop();

        foreach (var w in _windows)
            w.RenderFrame(this);
        foreach (var w in _windows)
            w.Present();

        // 界面自己要求的重画（InvalidateUi）已经在这一帧贴完，可以清掉了。
        UiInvalidatePending = false;

        // 穿透中小圆钮要跟着界面报的位置走（界面可能还在调整自己的占位）。
        if (PassThrough) UpdatePassThroughPill();

        // 调试：模拟"渲染跟不上"的低配机器，用来验证委托墨迹轨迹会不会补位。
        if (_artificialLagMs > 0) System.Threading.Thread.Sleep(_artificialLagMs);

        // Every window has now applied this round of changes, so the stale
        // regions can be dropped. Doing it here (rather than inside a window)
        // is what keeps multi-monitor setups correct.
        Doc.Dirty.Reset();

        var w0 = _windows[0];
        _lastRebuildMs = w0.LastRebuildMs;
        _lastRecordMs = w0.LastRecordMs;
        _lastPresentMs = w0.LastPresentMs;

        // How long the newest input took to reach the screen. This is our own
        // contribution; the display pipeline adds up to one more scan-out.
        if (_lastInputMs > 0)
        {
            double sample = _clock.Elapsed.TotalMilliseconds - _lastInputMs;
            if (sample >= 0 && sample < 200)
                _inputToPresentMs = _inputToPresentMs <= 0
                    ? sample
                    : _inputToPresentMs * 0.85 + sample * 0.15;
        }

        double wall = _clock.Elapsed.TotalMilliseconds;
        _lastFrameMs = wall - frameStart;

        _frames++;
        if (wall - _fpsWindowStart >= 500)
        {
            _fps = _frames * 1000.0 / (wall - _fpsWindowStart);
            _frames = 0;
            _fpsWindowStart = wall;
        }

        _accRecord += _lastRecordMs;
        _accPresent += _lastPresentMs;
        _accHud += swHud.Elapsed.TotalMilliseconds;
        if (statsRan) _accStats += _lastStatsMs;
        _accFrames++;

        if (NowMs >= _nextLogAt)
        {
            _nextLogAt = NowMs + 1000;
            int n = Math.Max(1, _accFrames);
            Console.WriteLine(
                $"[{NowMs / 1000,6:F1}s] fps={_fps,5:F1}"
                + $" 记录={_accRecord / n,5:F2} 上屏={_accPresent / n,5:F2}"
                  + $" HUD={_accHud / n,5:F2} 统计={_accStats / n,5:F2}"
                  + $" 合计={(_accRecord + _accPresent + _accHud + _accStats) / n,6:F2}ms"
                  + $" 置顶={_lastTopmostMs,5:F2}ms"
                  + $" 脏区={_windows[0].LastPresentRectCount}块/{_windows[0].LastPresentAreaPercent,5:F1}%"
                  + $" ws={_workingSetMb,6:F1}MB");
            _accRecord = _accPresent = _accHud = _accStats = 0;
            _accFrames = 0;
        }
    }

    private string BuildHudText()
    {
        return
            $"帧率 {_fps,5:F1} fps     帧耗时 {_lastFrameMs,5:F2} ms     输入到上屏 {_inputToPresentMs,5:F1} ms\n" +
            $"绘制 {_lastRecordMs,5:F2} ms     上屏 {_lastPresentMs,5:F2} ms     整层重建 {_lastRebuildMs,6:F1} ms\n" +
            $"工作集 {_workingSetMb,5:F1} MB     CPU {_cpuPercent,4:F1} %     峰值 {_peakWorkingSetMb,5:F1} MB\n" +
            $"笔画 {Doc.Strokes.Count,6}     点数 {Doc.TotalPoints,8}     选中 {Doc.Selected.Count}     工具：{ToolName(Tool)}{(PassThrough ? "   [穿透中]" : "")}\n" +
            $"笔迹粗细 {PenWidthLogical,4:F1} 逻辑像素    网格单元 {Doc.GridCells}\n" +
            $"快捷键都加 Ctrl+Alt：1笔 2荧光 3激光 4橡皮 5框选 / W换粗细 / Z撤销 / C清空 / P穿透 / X退出";
    }

    private static string ToolName(Tool t) => t switch
    {
        Tool.Pen => "笔",
        Tool.Highlighter => "荧光笔",
        Tool.Laser => "激光笔",
        Tool.Eraser => "橡皮擦",
        Tool.Marquee => "框选",
        Tool.Line => "直线",
        Tool.Rectangle => "矩形",
        Tool.Ellipse => "圆",
        Tool.Arrow => "箭头",
        _ => "?",
    };

    // =====================================================================
    //  Window procedure
    // =====================================================================

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 宿主自己的窗口（开发期的点击目标）先处理。产品界面不会用到这一层。
        if (HandleHostWindowMessage(hWnd, msg, wParam, lParam, out var hostResult))
            return hostResult;

        // 批注键盘模式下的按键。只有这个模式收得到——见 SetKeyboardMode。
        if ((msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN) && HandleKeyDown(wParam))
            return IntPtr.Zero;

        // 恢复小圆钮：它独立于覆盖层，是穿透时唯一还能接收点击的东西。
        if (s_pillHwnds.Contains(hWnd))
        {
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                case Native.WM_POINTERDOWN:
                    // 点它就退出穿透，把工具条还回来。
                    SetPassThrough(false);
                    return IntPtr.Zero;

                case Native.WM_MOUSEMOVE:
                    Pill?.SetHovered(true);
                    return IntPtr.Zero;

                case Native.WM_MOUSELEAVE:
                    Pill?.SetHovered(false);
                    return IntPtr.Zero;

                case Native.WM_NCHITTEST:
                    return new IntPtr(Native.HTCLIENT);

                case Native.WM_MOUSEACTIVATE:
                    return new IntPtr(Native.MA_NOACTIVATE);

                default:
                    return Native.DefWindowProc(hWnd, msg, wParam, lParam);
            }
        }

        switch (msg)
        {
            case Native.WM_NCHITTEST:
                if (PassThrough)
                    return new IntPtr(Native.HTTRANSPARENT);
                return new IntPtr(Native.HTCLIENT);

            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
                OnPointerDown(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUPDATE:
                OnPointerMove(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUP:
                OnPointerUp(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                EndStroke();
                return IntPtr.Zero;

            case Native.WM_HOTKEY:
                HandleHotkey(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;

            case Native.WM_TIMER:
                // 只有要显示性能面板时才需要周期性重绘；面板关掉还每秒重画 4 次
                // 纯属白烧电。
                if (ShowHud) _dirty = true;
                UpdatePillHover();
                var swTop = Stopwatch.StartNew();
                ReassertTopmost();
                swTop.Stop();
                _lastTopmostMs = swTop.Elapsed.TotalMilliseconds;
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _quit = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 让宿主处理属于它自己创建的窗口的消息。返回 true 表示消息已被消费。
    /// </summary>
    protected virtual bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    private void ReassertTopmost()
    {
        foreach (var w in _windows)
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // =====================================================================
    //  Pointer input
    // =====================================================================

    private void OnPointerDown(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntDown++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (PassThrough) return;
        if (!ReadPointer(id, out float x, out float y, out float pressure, out bool inverted, out uint ptype)) return;

        _activePointer = id;
        _activePointerType = ptype;
        PointerX = x; PointerY = y; PointerInside = true;

        // 界面优先：点在悬浮条上就是操作界面，不是画一笔。
        if (UiPointerDown(x, y, pressure, ptype == Native.PT_PEN, inverted))
        {
            _drawing = false;
            // 界面也要捕获指针：拖出悬浮条、在按钮上滑开都需要继续收到消息。
            Native.SetCapture(hWnd);
            _dirty = true;
            return;
        }

        _drawing = true;
        Native.SetCapture(hWnd);

        var tool = inverted ? Tool.Eraser : Tool;
        switch (tool)
        {
            case Tool.Eraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginErase();
                Doc.EraseAt(x, y, EraserRadius);
                break;

            case Tool.Marquee:
                // 先问选择手势（按钮 / 手柄 / 整体拖动）；都没接才起新的框选。
                if (!TryBeginSelectionGesture(x, y))
                {
                    MarqueeActive = true;
                    MqMinX = MqMaxX = x; MqMinY = MqMaxY = y;
                }
                break;

            case Tool.Laser:
                Laser.Clear();
                Laser.Visible = true;
                Laser.Add(x, y, NowMs);
                break;

            default:
                _lastSampleMs = NowMs;
                // 起笔打底交给优化器；没装优化器就什么都不做。
                InkOptimizers.Current?.BeginStroke(x, y);
                float trailW = (tool == Tool.Highlighter ? HighlighterWidthLogical : PenWidthLogical) * DpiScale;
                WindowAt(x, y)?.BeginInkTrail(
                    tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor, trailW * 0.5f);
                ActiveStroke = new Stroke
                {
                    Tool = tool,
                    Color = tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
                    Width = (tool == Tool.Highlighter ? HighlighterWidthLogical : PenWidthLogical) * DpiScale,
                    Preset = PenPresetValue,
                };
                ActiveStroke.AddPoint(x, y, pressure, NowMs);
                break;
        }
        _dirty = true;
    }

    private void OnPointerMove(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntMove++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float x, out float y, out float pressure, out bool inverted, out _)) return;
        PointerX = x; PointerY = y; PointerInside = true;

        // 界面捕获了指针（例如按下按钮后滑出去），消息全归界面。
        if (UiCapturing)
        {
            UiPointerMove(x, y, pressure, false, inverted);
            _dirty = true;
            return;
        }

        if (PassThrough || !_drawing || id != _activePointer) { _dirty = true; return; }

        var tool = inverted ? Tool.Eraser : Tool;
        switch (tool)
        {
            case Tool.Eraser:
                EraseAlongPath(x, y);
                break;

            case Tool.Marquee:
                if (SelDragging) UpdateSelDrag(x, y);
                else
                {
                    MqMinX = MathF.Min(MqMinX, x); MqMaxX = MathF.Max(MqMaxX, x);
                    MqMinY = MathF.Min(MqMinY, y); MqMaxY = MathF.Max(MqMaxY, y);
                }
                break;

            case Tool.Laser:
                Laser.Add(x, y, NowMs);
                break;

            default:
                if (ActiveStroke != null)
                {
                    // 平滑和抽稀都是优化器的事。没装优化器时原样收下：
                    // 指针报什么坐标，就存什么坐标。
                    double dt = Math.Max(1e-3, (NowMs - _lastSampleMs) / 1000.0);
                    _lastSampleMs = NowMs;
                    float fx = x, fy = y;
                    var opt = InkOptimizers.Current;
                    opt?.Smooth(ref fx, ref fy, dt);

                    WindowAt(x, y)?.AddInkTrailPoint(fx, fy, ActiveStroke.Width * 0.5f);

                    var last = ActiveStroke.Points[^1];
                    float dx = fx - last.X, dy = fy - last.Y;
                    float minStep = (opt?.SampleStepLogicalPx ?? 0f) * DpiScale;
                    if (dx * dx + dy * dy >= minStep * minStep)
                        ActiveStroke.AddPoint(fx, fy, pressure, NowMs);
                }
                break;
        }
        _dirty = true;
    }

    private void OnPointerUp(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntUp++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);

        // 界面优先收尾，否则会留下"按钮一直按着"的状态。
        if (UiCapturing && ReadPointer(id, out float ux, out float uy, out float upressure,
                                       out bool uinverted, out _))
        {
            UiPointerUp(ux, uy, upressure, false, uinverted);
            _drawing = false;
            _dirty = true;
            return;
        }

        if (id != _activePointer) return;
        Native.ReleaseCapture();
        _drawing = false;
        EndStroke();
    }

    /// <summary>
    /// Walks the eraser along the segment the pointer just travelled instead of
    /// only testing the newest position. Without this, a quick flick leaves gaps
    /// between samples and only some of the crossed strokes disappear - which is
    /// what "the eraser feels unresponsive" actually looks like.
    /// </summary>
    private void EraseAlongPath(float x, float y)
    {
        float r = EraserRadius;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, r * 0.5f)), 1, 48);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Doc.EraseAt(_lastEraseX + dx * t, _lastEraseY + dy * t, r);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    private void EndStroke()
    {
        foreach (var w in _windows) w.EndInkTrail();
        Doc.EndErase();
        if (ActiveStroke != null)
        {
            if (ActiveStroke.Points.Count > 0)
            {
                // 抬笔后的处理（平滑、抽稀、拟合、笔锋）全部交给优化器。
                // 没装优化器时，存下来的就是原始采样点，引擎直接画它。
                InkOptimizers.Current?.EndStroke(ActiveStroke, DpiScale);
                _lastSimplifyInfo = InkOptimizers.Current?.LastReport;
                Doc.AddStroke(ActiveStroke);
                _lastStrokeReport =
                    $"采集到 {ActiveStroke.Points.Count} 个点"
                    + $"，收到 按下{_cntDown} 移动{_cntMove} 抬起{_cntUp} 丢失捕获{_cntCaptureLost}"
                    + $"，设备={PointerTypeName(_activePointerType)}"
                    + (string.IsNullOrEmpty(_lastSimplifyInfo) ? "" : $"，抽稀 {_lastSimplifyInfo}");
                Console.WriteLine("[笔画] " + _lastStrokeReport);
                _lastSimplifyInfo = null;
            }
            ActiveStroke = null;
        }
        if (Tool == Tool.Marquee)
        {
            if (SelDragging) EndSelDrag();
            else ApplyMarquee();
        }
        _drawing = false;
        _dirty = true;
        _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
    }

    private static string PointerTypeName(uint t) => t switch
    {
        Native.PT_MOUSE => "鼠标",
        Native.PT_TOUCH => "触摸",
        Native.PT_PEN => "笔",
        _ => "未知",
    };

    /// <summary>找出包含某个屏幕坐标的覆盖窗口（多屏时用）。</summary>
    private OverlayWindow WindowAt(float screenX, float screenY)
    {
        foreach (var w in _windows)
        {
            if (screenX >= w.OriginX && screenX < w.OriginX + w.Width &&
                screenY >= w.OriginY && screenY < w.OriginY + w.Height)
                return w;
        }
        return _windows.Count > 0 ? _windows[0] : null;
    }

    private void ApplyMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;

        float l = MqMinX, t = MqMinY, r = MqMaxX, b = MqMaxY;
        if (r - l < 4 || b - t < 4)
        {
            // 这是一次**点击**，不是拖框。单击空白处就该取消选中。
            // 之前这里直接 return、什么都不做，用户得点两下才取消，很别扭。
            Doc.Selected.Clear();
            _dirty = true;
            return;
        }

            Doc.ApplyMarquee(new RectF { MinX = l, MinY = t, MaxX = r, MaxY = b });
        Console.WriteLine($"marquee selected {Doc.Selected.Count} strokes");
        _dirty = true;
    }

    private static bool ReadPointer(uint id, out float x, out float y, out float pressure,
                                    out bool inverted, out uint pointerType)
    {
        x = y = 0; pressure = 0.5f; inverted = false; pointerType = 0;
        if (!Native.GetPointerInfo(id, out var pi)) return false;
        x = pi.ptPixelLocationX;
        y = pi.ptPixelLocationY;
        pointerType = pi.pointerType;
        if (pi.pointerType == Native.PT_PEN && Native.GetPointerPenInfo(id, out var pen))
        {
            pressure = pen.pressure / 1024f;
            if (pressure <= 0.01f) pressure = 0.5f;
            inverted = (pen.penFlags & (Native.PEN_FLAG_INVERTED | Native.PEN_FLAG_ERASER)) != 0;
        }
        return true;
    }

    // =====================================================================
    //  Hotkeys
    // =====================================================================

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            case 1: SetPassThrough(!PassThrough); break;
            case 2: Tool = Tool.Pen; break;
            case 3: Tool = Tool.Highlighter; break;
            case 4: Tool = Tool.Laser; break;
            case 5: Tool = Tool.Eraser; break;
            case 6: Tool = Tool.Marquee; break;
            case 7: Doc.Undo(); break;
            case 8: Doc.Clear(); Laser.Clear(); break;
            case 9: ShowHud = !ShowHud; break;
            // 10/11 是开发期的基准与内存探测，本身不属于引擎，交给宿主覆写。
            case 10:
            case 11:
                HandleHostHotkey(id);
                break;
            case 12: CyclePassThroughMode(); break;
            case 13: _quit = true; break;
            case 14:
                WidthPresetIndex = (WidthPresetIndex + 1) % WidthPresets.Length;
                PenWidthLogical = WidthPresets[WidthPresetIndex];
                Console.WriteLine($"笔迹粗细 -> {PenWidthLogical} 逻辑像素"
                                + $"（本机实际 {PenWidthLogical * DpiScale:F0} 物理像素）");
                break;
            case 15:
                SetKeyboardMode(!KeyboardMode);
                break;
        }
        _dirty = true;
    }

    /// <summary>快捷键里属于宿主（开发工具）的那几个。产品界面用不到。</summary>
    protected virtual void HandleHostHotkey(int id) { }

    private void SetPassThrough(bool on)
    {
        PassThrough = on;
        if (!on) Laser.Visible = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        UpdatePassThroughPill();
        Console.WriteLine($"pass-through = {on} (mode {PassMode})");
    }

    /// <summary>
    /// 穿透时把"找回界面"的小圆钮摆出来。位置取界面自己报的占位矩形右端——
    /// 也就是工具条原来的位置，老师一眼就能看到它在那儿。
    /// </summary>
    private void UpdatePassThroughPill()
    {
        if (Pill == null) return;

        if (!PassThrough)
        {
            Pill.Hide();
            _pillShown = RectF.Empty;
            return;
        }

        var bounds = Ui?.QueryBounds() ?? RectF.Empty;
        // IsEmpty 只认"求过并集且为空"的矩形；默认全 0 的矩形要单独排除，
        // 否则会拿它去开一个坐标错乱的窗口。
        if (bounds.IsEmpty || (bounds.MinX == 0 && bounds.MinY == 0
                               && bounds.MaxX == 0 && bounds.MaxY == 0))
        {
            // 界面没有报位置（比如纯引擎模式）：退回到屏幕右下角。
            float size = 44f;
            bounds = new RectF
            {
                MinX = LogicalVirtualScreen.MaxX - size - 16f,
                MinY = LogicalVirtualScreen.MaxY - size - 90f,
                MaxX = LogicalVirtualScreen.MaxX - 16f,
                MaxY = LogicalVirtualScreen.MaxY - 90f,
            };
        }

        // 界面可能把提示条也算进了占位矩形，这里只要右下角那一小块。
        float s = 44f;
        var pill = new RectF
        {
            MinX = bounds.MaxX - s,
            MinY = bounds.MaxY - s,
            MaxX = bounds.MaxX,
            MaxY = bounds.MaxY,
        };

        // 只有位置或可见性真的变了才动窗口。
        // 之前每帧都调一次 Show（里面是 SetWindowPos + SWP_SHOWWINDOW），
        // 每帧重新"显示"一次窗口会不断打断鼠标消息，点击因此迟迟送不到
        // ——实测就是这个原因导致点小圆钮没反应。
        if (Pill.Visible && SameRect(_pillShown, pill)) return;
        _pillShown = pill;
        Pill.Show(pill);
    }

    private static bool SameRect(RectF a, RectF b)
        => MathF.Abs(a.MinX - b.MinX) < 0.5f && MathF.Abs(a.MinY - b.MinY) < 0.5f
        && MathF.Abs(a.MaxX - b.MaxX) < 0.5f && MathF.Abs(a.MaxY - b.MaxY) < 0.5f;

    /// <summary>上一次摆出的圆钮矩形，用来避免每帧重复显示窗口。</summary>
    private RectF _pillShown = RectF.Empty;

    /// <summary>
    /// 光标靠近小圆钮时把它提亮。走的是已有的 250ms 定时器，
    /// 不用再为它单独装一个鼠标钩子——那个钮本来就很小，250ms 足够灵敏。
    /// </summary>
    private void UpdatePillHover()
    {
        if (Pill == null || !Pill.Visible) return;
        if (!Native.GetCursorPos(out var p)) return;

        bool inside = p.X >= Pill.OriginX && p.X < Pill.OriginX + Pill.Width
                   && p.Y >= Pill.OriginY && p.Y < Pill.OriginY + Pill.Height;
        if (inside != _pillHover)
        {
            _pillHover = inside;
            Pill.SetHovered(inside);
        }
    }

    private bool _pillHover;

    private void CyclePassThroughMode()
    {
        PassMode = (PassThroughMode)(((int)PassMode + 1) % 3);
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        Console.WriteLine($"pass-through implementation = {PassMode}");
    }

    internal void ApplyPassThroughStyle(OverlayWindow w)
    {
        long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
        const long clear = Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        ex &= ~clear;

        if (PassThrough)
        {
            if (PassMode == PassThroughMode.ExTransparent)
                ex |= Native.WS_EX_TRANSPARENT;
            else if (PassMode == PassThroughMode.LayeredTransparent)
                ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        }

        Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
    }

    /// <summary>
    /// 关窗、注销快捷键、释放设备。子类若要覆写，务必先调用 base。
    /// </summary>
    protected virtual void Shutdown()
    {
        foreach (var w in _windows)
        {
            Native.KillTimer(w.Hwnd, (IntPtr)1);
            for (int i = 1; i <= 13; i++) Native.UnregisterHotKey(w.Hwnd, i);
            w.Dispose();
        }
        _windows.Clear();
        s_map.Clear();
        s_pillHwnds.Clear();
        Pill?.Dispose();
        Pill = null;
        Gfx.Shutdown();
        Console.WriteLine("shutdown complete");
    }

    // =====================================================================
    //  界面接入（引擎边界）
    //
    //  引擎负责：笔画/文档/输入/渲染/工具状态。
    //  界面负责：画自己、解释点击。
    //  两边唯一的通道就是 IOverlayUi / IUiHost，引擎里没有任何具体界面代码。
    // =====================================================================

    /// <summary>
    /// 换界面。传 null 或 <see cref="NullUi"/> 就是无界面（纯笔迹）。
    /// 界面只在覆盖层建好之后接入；运行中换界面也是安全的。
    /// </summary>
    /// <param name="keepScreen">
    /// 传 true 表示沿用当前这个界面已经算好的屏幕尺寸。自检里"临时换掉再换回来"
    /// 会用到处，避免界面按引擎内部的窗口尺寸重新布局（那把界面摆到别处去了）。
    /// 正式产品代码用默认值即可。
    /// </param>
    public void SetUi(IOverlayUi ui, bool keepScreen = false)
    {
        Ui = ui ?? new NullUi();
        // 界面看到的屏幕坐标是逻辑像素：它按自己的逻辑尺寸布局，引擎负责按 DPI 放大。
        Host = new UiHost(this, LogicalVirtualScreen, DpiScale, keepScreen);
        Ui.Attach(Host);
        InvalidateUi();
        NotifyUiStateChanged();
    }

    /// <summary>当前接入的界面（诊断用）。</summary>
    public IOverlayUi CurrentUi => Ui;

    /// <summary>虚拟桌面范围，界面据此决定悬浮条放在哪。</summary>
    public RectF VirtualScreen => new()
    {
        MinX = _virtualX,
        MinY = _virtualY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY + _virtualH,
    };

    /// <summary>虚拟桌面的**逻辑**范围（除以 DPI 缩放），界面布局用这个。</summary>
    public RectF LogicalVirtualScreen => new()
    {
        MinX = _virtualX / DpiScale,
        MinY = _virtualY / DpiScale,
        MaxX = (_virtualX + _virtualW) / DpiScale,
        MaxY = (_virtualY + _virtualH) / DpiScale,
    };

    /// <summary>
    /// 界面声明"外观变了，请重画我的缓存"。可以由界面的任意线程调用，
    /// 主线程在下一帧消费。**不要每帧调**，那等于每帧重画整个界面。
    /// </summary>
    public void InvalidateUi()
    {
        UiInvalidatePending = true;
        _dirty = true;
    }

    internal UiState SnapshotState() => new(
        Tool,
        Tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
        Tool == Tool.Highlighter
            ? new Color4(HighlighterCurrent.R, HighlighterCurrent.G, HighlighterCurrent.B, 1f)
            : CurrentColor,
        Tool == Tool.Highlighter ? HighlighterWidthLogical : PenWidthLogical,
        PenPresetValue,
        PassThrough, BoardOn, Doc.UndoDepth, Doc.RedoDepth, Doc.Strokes.Count);

    private void NotifyUiStateChanged()
    {
        if (Ui == null) return;
        Ui.OnStateChanged(SnapshotState());
    }

    internal void SetToolFromUi(Tool tool)
    {
        Tool = tool;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetColorFromUi(Color4 color)
    {
        // 荧光笔用同一组基色，只是降低不透明度——老师看到的色相是一致的。
        if (Tool == Tool.Highlighter)
            HighlighterCurrent = InkPalette.ToHighlighter(color);
        else
            CurrentColor = color;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetWidthFromUi(float logicalPx)
    {
        float v = Math.Clamp(logicalPx, 0.5f, 64f);
        if (Tool == Tool.Highlighter) HighlighterWidthLogical = v;
        else PenWidthLogical = v;
        int idx = Array.IndexOf(WidthPresets, v);
        if (idx >= 0) WidthPresetIndex = idx;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetPresetFromUi(PenPreset preset)
    {
        PenPresetValue = preset;
        // 已经画好的笔画保持原样：它们是"当时的笔"写出来的，历史不该被改写。
        // 新的一笔会用新的预设。
        NotifyUiStateChanged();
    }

    internal void UndoFromUi()
    {
        Doc.Undo();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void RedoFromUi()
    {
        Doc.Redo();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void ClearFromUi()
    {
        Doc.Clear();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void QuitFromUi() => _quit = true;

    internal void SetPassThroughFromUi(bool on)
    {
        SetPassThrough(on);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 开关白板。底色一变，整个内容层都要重画——缓存里那张图是按旧底色画的。
    /// </summary>
    internal void SetBoardFromUi(bool on)
    {
        if (BoardOn == on) return;
        BoardOn = on;
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 指针按下的第一站：先问界面。返回 true 表示这次输入归界面（比如按到了
    /// 悬浮条上的按钮），引擎不再把它变成笔画。
    /// </summary>
    private bool UiPointerDown(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (Ui == null || !Ui.Visible) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        if (!Ui.PointerDown(e)) return false;
        UiCapturing = true;
        return true;
    }

    private bool UiPointerMove(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (Ui == null || !Ui.Visible) return false;

        // 只有在界面已经捕获输入或指针落在界面矩形内时才转发，避免没必要的调用。
        if (!UiCapturing && !UiContains(x, y)) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        return Ui.PointerMove(e);
    }

    private bool UiPointerUp(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (Ui == null || !Ui.Visible || !UiCapturing) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        bool consumed = Ui.PointerUp(e);
        UiCapturing = false;
        return consumed;
    }

    private bool UiContains(float x, float y)
    {
        x /= DpiScale; y /= DpiScale;   // 物理 → 逻辑
        foreach (var w in _windows)
            if (w.UiBoundsLogicalContains(x, y)) return true;
        return false;
    }

    /// <summary>控制台用法说明。产品界面不会走这里。</summary>
    protected virtual void PrintUsageHelp()
    {
        Console.WriteLine();
        Console.WriteLine("InkProbe 批注原型已启动。全局快捷键（Ctrl+Alt+...）：");
        Console.WriteLine("  P 穿透模式开关      1 笔        2 荧光笔     3 激光笔");
        Console.WriteLine("  4 橡皮擦            5 框选      Z 撤销       C 清空");
        Console.WriteLine("  I 性能面板          B 性能基准  M 内存探测   Y 切换穿透实现");
        Console.WriteLine("  X 退出");
        Console.WriteLine();
    }

    // =====================================================================
    //  选择手势（框选工具 = 选择工具）
    // =====================================================================

    /// <summary>
    /// 框选工具按下时的分流。返回 true 表示这次按下已经被选择手势接掉。
    ///
    /// 顺序有讲究：**先按钮、再手柄、最后才判断"整体拖动"**。反过来写的话，
    /// 按钮和手柄都紧挨着选中框，会被"在框内拖动"抢走，表现就是点不中。
    /// </summary>
    private bool TryBeginSelectionGesture(float x, float y)
    {
        if (Doc.Selected.Count == 0) return false;

        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;

        // 操作条定位用框的**画布轴对齐范围**：框本身可能是斜的，但"它占了屏幕上
        // 哪一块"永远是个正矩形，操作条贴在那个矩形的下面才对。
        var aabb = frame.CanvasAabb;
        int btn = SelectionHandles.BarButtonAt(x, y, aabb, dpi);
        if (btn >= 0) { RunBarAction(btn, frame, aabb); return true; }

        var h = SelectionHandles.HitTest(x, y, frame, dpi);
        bool move = false;
        if (h == SelHandle.None)
        {
            // 没点在手柄上：把指针变回框坐标，看是不是落在框里（整体拖动）。
            var lp = frame.ToLocalPoint(new Vector2(x, y));
            move = lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
                && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY;
            if (!move) return false;                      // 落在空白处：交给框选
        }

        _dragHandle = h;
        _dragIsMove = move;
        _dragFrame = frame;
        _dragStartPoint = new Vector2(x, y);
        _selDragMatrix = Matrix3x2.Identity;

        _dragTargets = Doc.Selected.ToArray();
        _dragStartXform = new Matrix3x2[_dragTargets.Length];
        for (int i = 0; i < _dragTargets.Length; i++)
            _dragStartXform[i] = _dragTargets[i].Transform;

        SelDragging = true;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 拖动中：每帧都从**按下那一刻的变换**重算，而不是在上一帧结果上继续乘。
    /// 后者会累积浮点误差，拖得越久偏得越多，撤销也回不到原样。
    /// </summary>
    private void UpdateSelDrag(float x, float y)
    {
        var cur = new Vector2(x, y);
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;

        Matrix3x2 m;
        if (_dragIsMove)
        {
            // 整体移动：指针在画布上走多少，对象就走多少。**不能**在框坐标里算
            // 再共轭回来——框是斜的时候那样会走偏方向。
            m = Matrix3x2.CreateTranslation(cur - _dragStartPoint);
        }
        else
        {
            // 手柄换算在**框坐标**里做（缩放的锚点是"对角那个手柄"，只有在框坐标里
            // 才是"沿着框的两条边"），再共轭回画布坐标：M = F⁻¹ · M_local · F
            var localM = SelectionHandles.DragMatrix(_dragHandle, _dragFrame,
                                                     _dragStartPoint, cur, DpiScale, shift, shift);
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            var s = _dragTargets[i];
            Doc.Dirty.Add(s.PaddedBounds);                 // 旧位置要擦
            Doc.SetTransformLive(s, _dragStartXform[i] * m);
            Doc.Dirty.Add(s.PaddedBounds);                 // 新位置要画
        }
        _selDragMatrix = m;
        _dirty = true;
    }

    /// <summary>
    /// 松手：先把模型恢复到按下那一刻，再提交**一条**变换命令。
    ///
    /// 这样拖动过程中的实时预览不产生撤销记录，而撤销一步就精确回到拖动前
    /// ——不会出现"拖的时候动了三十次，要按三十次撤销"那种事。
    /// </summary>
    private void EndSelDrag()
    {
        SelDragging = false;
        if (_dragTargets == null) return;

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            Doc.SetTransformLive(_dragTargets[i], _dragStartXform[i]);
            Doc.Dirty.Add(_dragTargets[i].PaddedBounds);
        }

        Doc.Selected.Clear();
        foreach (var t in _dragTargets) Doc.Selected.Add(t);
        Doc.ApplyTransform(_selDragMatrix);

        _dragTargets = null;
        _dragStartXform = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _selDragMatrix = Matrix3x2.Identity;
        _dirty = true;
    }

    /// <summary>
    /// 操作条按钮。下标与 SelectionHandles.BarButtonAt 的返回值和渲染时的
    /// 图标数组一一对应：0 复制 / 1 删除 / 2 左右翻转 / 3 上下翻转 / 4 旋转。
    /// </summary>
    private void RunBarAction(int index, in SelectionFrame frame, in RectF aabb)
    {
        switch (index)
        {
            case 0: Doc.DuplicateSelected(); break;
            case 1: Doc.DeleteSelected(); break;

            // 翻转绕**框自己的轴**：斜着的对象应该在自己那套坐标里翻，
            // 而不是绕屏幕的竖直线翻——后者看起来像被转到别处去了。
            case 2: Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                        SelectionHandles.MirrorMatrix(frame.Local, horizontal: true))); break;
            case 3: Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                        SelectionHandles.MirrorMatrix(frame.Local, horizontal: false))); break;

        }
        Laser.Clear();
        _dirty = true;
    }

    /// <summary>
    /// 把"框坐标下的变换"翻译成"画布坐标下的变换"：<c>F⁻¹ · M · F</c>。
    ///
    /// 框是斜的时候，同一句"沿框的横轴放大两倍"在画布坐标里是个斜的缩放。
    /// 共轭就是在两套坐标之间翻译这件事——不用为"斜着的情况"另写一套公式。
    /// </summary>
    private static Matrix3x2 Conjugate(in Matrix3x2 frame, in Matrix3x2 localM)
    {
        if (frame.IsIdentity) return localM;
        if (!Matrix3x2.Invert(frame, out var inv)) return localM;
        return inv * localM * frame;
    }
    // =====================================================================
    //  批注键盘模式
    // =====================================================================

    /// <summary>
    /// 开关批注键盘模式。
    ///
    /// 开：去掉 WS_EX_NOACTIVATE 并把窗口提到前台，键盘归批注层，编辑类
    ///     快捷键（Ctrl+Z / Ctrl+D / Delete / 方向键…）才有地方落地。
    /// 关：加回 WS_EX_NOACTIVATE，覆盖层回到"永不抢焦点"，键盘还给下层程序
    ///     ——那时候只有全局热键（Ctrl+Alt+…）可用。
    ///
    /// 取舍说清楚：开着的时候，放映中的 PPT 收不到键盘。
    /// </summary>
    private void SetKeyboardMode(bool on)
    {
        KeyboardMode = on;
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            if (on) ex &= ~Native.WS_EX_NOACTIVATE;
            else ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            if (on) Native.SetForegroundWindow(w.Hwnd);
        }
        Console.WriteLine($"批注键盘模式 = {on}"
            + (on ? "（键盘归批注层；此时下层程序收不到键盘）" : "（键盘还给下层程序）"));
        _dirty = true;
    }

    /// <summary>
    /// 批注键盘模式下的按键。**只在 <see cref="KeyboardMode"/> 打开时收到。**
    ///
    /// 键位一律照 Windows 的通用习惯，不自己发明：
    ///   Ctrl+Z 撤销 / Ctrl+Y 重做 / Ctrl+A 全选 / Delete 删除 / Esc 取消选择
    ///   Ctrl+D 复制一份（系统剪贴板还没接，先用 D）
    ///   方向键移动 1 像素，按住 Shift 是 10 像素
    ///
    /// 已知待改：方向键按住会重复触发，每次都是一条撤销记录。要接"连续微调
    /// 合并成一步"，得等编辑命令支持合并（撤销栈里相邻同类动作合并）。
    /// </summary>
    private bool HandleKeyDown(IntPtr wParam)
    {
        int vk = wParam.ToInt32();
        bool ctrl = (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0;
        bool shift = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;

        switch (vk)
        {
            case 0x5A: if (!ctrl) return false; Doc.Undo(); Laser.Clear(); break;   // Z
            case 0x59: if (!ctrl) return false; Doc.Redo(); break;                  // Y
            case 0x41: if (!ctrl) return false; SelectAll(); break;                 // A
            case 0x44: if (!ctrl) return false; Doc.DuplicateSelected(); break;     // D
            case 0x2E: Doc.DeleteSelected(); break;                                 // Delete
            case 0x1B: Doc.Selected.Clear(); break;                                 // Esc
            case 0x25: case 0x26: case 0x27: case 0x28:                             // 方向键
            {
                float step = shift ? 10f : 1f;
                float dx = vk == 0x25 ? -step : vk == 0x27 ? step : 0f;
                float dy = vk == 0x26 ? -step : vk == 0x28 ? step : 0f;
                Doc.ApplyTransform(Matrix3x2.CreateTranslation(dx, dy));
                break;
            }
            default: return false;
        }
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 全选。顺便切到选择工具——不切的话手柄和操作条不会出现，
    /// 用户会以为"全选没生效"。
    /// </summary>
    private void SelectAll()
    {
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
    }
}
