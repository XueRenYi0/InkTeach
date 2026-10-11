// CoreInkProbe v2 —— 决定性实验（NativeAOT 应用接系统墨迹引擎 CoreInkPresenterHost）
//  + 2026-10-12 扩展：测"旁听口"——CoreInkIndependentInputSource 能不能在
//    湿墨绘制之前、在墨迹线程上，把同一份指针流（含压感/时间戳）送给我们。
//
// v2 测试内容：
//   A) 挂 CoreInkIndependentInputSource，订阅全部指针事件并记录字段
//   B) 内置合成笔注入（240~250Hz、带压力、含悬停/起笔/运笔/收笔）——不再依赖外部注入器
//   C) 同窗口 WM_POINTER/WM_MOUSE 计数（验证"笔输入被接管"的程度）
//   D) StrokesCollected 与 IIS 事件并存性
//
// 装配（沿用 v1 已验证的路子）：
//   1) Win32 覆盖层窗口（NOREDIRECTIONBITMAP + TOPMOST + TOOLWINDOW + NOACTIVATE）
//   2) Windows.UI.Composition 合成树，手动 COM QI 调 ICompositorDesktopInterop（vtable 槽 3）
//   3) new CoreInkPresenterHost()，InputDeviceTypes = Pen，PredictionTime=15ms
//   4) 合成笔注入 → 看 IIS 事件 vs WM_POINTER 计数 vs StrokesCollected

using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;
using WinRT;

namespace CoreInkProbe;

internal static unsafe class Program
{
    private static nint _hwnd;
    private static int _strokeCount;
    private static string _log = "";
    private static readonly object _logLock = new();
    private static readonly object _cntLock = new();
    private static readonly Dictionary<string, int> _counts = new();

    // 真笔测试模式（--pentest）：不再合成注入，等真人用笔写；结果落盘到桌面\软件。
    private static bool _pentest;
    private static bool _stopped;
    private static string _resultPath = "";
    private static nint _tipHwnd;

    // Moving 事件的时间线（旁听流的最早/最晚）
    private static int _moveN;
    private static ulong _firstMoveTs, _lastMoveTs;
    private static long _firstMoveTick, _lastMoveTick;
    private static long _injStartTick, _injEndTick;
    private static int _injSamples;

    private static void Log(string line)
    {
        lock (_logLock)
        {
            Console.WriteLine(line);
            _log += line + Environment.NewLine;
        }
    }

    private static void Bump(string kind)
    {
        lock (_cntLock)
        {
            _counts.TryGetValue(kind, out int n);
            _counts[kind] = n + 1;
        }
    }

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
            Dump();
            return 1;
        }
    }

    private static void Dump()
    {
        try
        {
            lock (_logLock)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "coreink-probe.log"), _log);
            }
        }
        catch
        {
            // 忽略。
        }
    }

    private static int Run()
    {
        SetProcessDpiAwarenessContext(-4);
        var cli = Environment.GetCommandLineArgs();
        _targetSelf = cli.Contains("--target");
        bool activate = cli.Contains("--activate");
        _pentest = cli.Contains("--pentest");
        GetWorkArea(out var work);
        Log("OS " + Environment.OSVersion.VersionString);
        Log($"workarea {work.Left},{work.Top} {work.Right - work.Left}x{work.Bottom - work.Top}");

        if (!CreateAppWindow(work, activate))
        {
            Log("window creation failed");
            return 2;
        }

        Log("window ok 0x" + _hwnd.ToString("X"));
        Log("EnableMouseInPointer=" + EnableMouseInPointer(true));
        Log("targetSelf=" + _targetSelf + " activate=" + activate);
        if (activate)
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

        if (_pentest)
        {
            CreateTipWindow(work);
            RegisterHotKey(_hwnd, 1, 0x7 /*CTRL|ALT|SHIFT*/, 0x51 /*Q*/);
            _resultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "软件", "真笔探针结果.txt");
            Log("PENTEST setup: tip=0x" + _tipHwnd.ToString("X") + " result=" + _resultPath);
        }

        bool mainThread = cli.Contains("--mainthread");
        DispatcherQueueController dqc = null;
        if (mainThread)
        {
            Log("mainthread: 用 CoreMessaging 在主线程建 DispatcherQueue（枚举组合）...");
            foreach (int tt in new[] { 2, 1 })
            {
                bool got = false;
                foreach (int apt in new[] { 0, 1, 2 })
                {
                    var opt = new DispatcherQueueOptions { Size = 12, ThreadType = (uint)tt, ApartmentType = (uint)apt };
                    int hr = CreateDispatcherQueueController(opt, out nint ctl);
                    Log($"  tt={tt} apt={apt} hr=0x{hr:X8} ctl=0x{ctl:X}");
                    if (hr >= 0 && ctl != 0)
                    {
                        got = true;
                        break;
                    }
                }

                if (got)
                {
                    break;
                }
            }
        }
        else
        {
            dqc = DispatcherQueueController.CreateOnDedicatedThread();
            Log("DispatcherQueueController(dedicated) ok=" + (dqc is not null));
        }

        bool inkReady = false;
        void SetupInk()
        {
            var compositor = new Compositor();
            Log("compositor ok");

            var target = CreateDesktopWindowTarget(compositor, _hwnd, true);
            Log("desktop window target ok=" + (target is not null));

            var root = compositor.CreateContainerVisual();
            target!.Root = root;

            var host = new CoreInkPresenterHost();
            var presenter = host.InkPresenter;
            presenter.InputDeviceTypes = _pentest ? CoreInputDeviceTypes.Pen
                                                  : CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;   // v3：对齐 09-14 成功配置

            var attrs = presenter.CopyDefaultDrawingAttributes();
            attrs.Color = Windows.UI.Color.FromArgb(255, 230, 30, 90);
            try
            {
                attrs.ModelerAttributes.PredictionTime = TimeSpan.FromMilliseconds(15);
                Log("PredictionTime=15ms ok");
            }
            catch (Exception ex)
            {
                Log("PredictionTime set failed: " + ex.Message);
            }

            presenter.UpdateDefaultDrawingAttributes(attrs);

            presenter.StrokesCollected += (_, args) =>
            {
                _strokeCount += args.Strokes.Count;
                Log($"[{Environment.TickCount64}] StrokesCollected! total={_strokeCount}");
                if (_pentest)
                {
                    foreach (var s in args.Strokes)
                    {
                        try { Log($"  stroke points={s.GetInkPoints().Count}"); } catch { }
                    }
                }
            };

            // === v2 核心：旁听口 ===
            try
            {
                var iis = CoreInkIndependentInputSource.Create(presenter);
                iis.PointerEntering += (_, a) => OnPointer("Entering", a);
                iis.PointerHovering += (_, a) => OnPointer("Hovering", a);
                iis.PointerPressing += (_, a) => OnPointer("Pressing", a);
                iis.PointerMoving += (_, a) => OnPointer("Moving", a);
                iis.PointerReleasing += (_, a) => OnPointer("Releasing", a);
                iis.PointerExiting += (_, a) => OnPointer("Exiting", a);
                iis.PointerLost += (_, a) => OnPointer("Lost", a);
                Log("CoreInkIndependentInputSource.Create ok + events subscribed");
            }
            catch (Exception ex)
            {
                Log("IIS create/subscribe FAILED: " + ex.Message);
            }

            if (_pentest)
            {
                var band = compositor.CreateSpriteVisual();
                band.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(44, 80, 160, 255));
                float scale = MathF.Max(1f, GetDpiForWindow(_hwnd) / 96f);
                band.Offset = new System.Numerics.Vector3(0, (work.Bottom - work.Top) / scale / 2f - 160f, 0);
                band.Size = new System.Numerics.Vector2((work.Right - work.Left) / scale, 320);
                root.Children.InsertAtTop(band);
            }

            var inkVisual = compositor.CreateContainerVisual();
            root.Children.InsertAtTop(inkVisual);
            host.RootVisual = inkVisual;

            try
            {
                presenter.IsInputEnabled = true;
                Log("IsInputEnabled=" + presenter.IsInputEnabled);
            }
            catch (Exception ex)
            {
                Log("IsInputEnabled set failed: " + ex.Message);
            }

            inkReady = true;
        }

        if (mainThread)
        {
            try
            {
                SetupInk();
            }
            catch (Exception ex)
            {
                Log("setup (main thread) failed: " + ex);
            }
        }
        else if (dqc is not null)
        {
            using var done = new ManualResetEventSlim(false);
            dqc.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    SetupInk();
                }
                catch (Exception ex)
                {
                    Log("setup on dedicated thread failed: " + ex);
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait(6000);
        }
        else
        {
            try
            {
                SetupInk();
            }
            catch (Exception ex)
            {
                Log("setup failed: " + ex);
            }
        }

        Log("ink host ready=" + inkReady);
        bool fg = SetForegroundWindow(_hwnd);
        Log($"SetForegroundWindow={fg} fgHwnd=0x{GetForegroundWindow():X} ours=0x{_hwnd:X}");

        Thread.Sleep(300);
        long end;
        if (_pentest)
        {
            long secs = 90;
            var cli2 = Environment.GetCommandLineArgs();
            int iSec = Array.IndexOf(cli2, "--secs");
            if (iSec >= 0 && iSec + 1 < cli2.Length && long.TryParse(cli2[iSec + 1], out long s2)) secs = s2;
            Log($"PENTEST 就绪：请在屏幕中间的浅蓝色带里写 3~5 笔（红墨=系统画上了）；Ctrl+Alt+Shift+Q 结束；{secs}s 自动结束");
            end = Environment.TickCount64 + secs * 1000;
        }
        else
        {
            bool mouseMode = Environment.GetCommandLineArgs().Contains("--mouse");
            var inj = new Thread(() => { if (mouseMode) InjectMouseDrag(); else InjectStroke(); }) { IsBackground = true };
            inj.Start();
            end = Environment.TickCount64 + 9000;   // 注入 ~2.2s + 尾巴
        }

        while (!_stopped && Environment.TickCount64 < end)
        {
            while (PeekMessageW(out var msg, 0, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            Thread.Sleep(5);
        }

        string summary = "IIS counts: " + DumpCounts();
        Log($"RESULT strokes={_strokeCount} wndMsgs pointer={_msgPointer} mouse={_msgMouse}");
        Log(summary);
        Log($"IIS moving: n={_moveN} firstTs={_firstMoveTs}µs tick={_firstMoveTick} lastTs={_lastMoveTs}µs tick={_lastMoveTick}");
        Log($"inject: samples={_injSamples} startTick={_injStartTick} endTick={_injEndTick} dur={(_injEndTick - _injStartTick)}ms");
        Dump();

        if (_pentest)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_resultPath)!);
                lock (_logLock) { File.WriteAllText(_resultPath, _log, new System.Text.UTF8Encoding(false)); }
                Log("结果已写入 " + _resultPath);
            }
            catch (Exception ex) { Log("写结果失败: " + ex.Message); }

            if (!Environment.GetCommandLineArgs().Contains("--nomsg"))
            {
                string verdict = _strokeCount > 0 ? "✓ 有笔迹（系统接管了输入）" : "✗ 没收到笔迹（输入没进 presenter）";
                MessageBoxW(0,
                    "测试结束：" + verdict + "\r\n\r\n"
                    + "窗口消息 pointer=" + _msgPointer + " mouse=" + _msgMouse + "\r\n"
                    + "笔迹数=" + _strokeCount + "\r\n\r\n"
                    + "结果文件：\r\n" + _resultPath + "\r\n\r\n"
                    + "请回忆并告诉我：写字的时候，屏幕中间有没有出现红色墨迹？",
                    "CoreInkProbe 真笔测试", 0x40 | 0x1000);
            }
        }

        return _strokeCount > 0 ? 0 : 3;
    }

    private static string DumpCounts()
    {
        lock (_cntLock)
        {
            return string.Join(" ", _counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private static void OnPointer(string kind, PointerEventArgs a)
    {
        Bump(kind);

        if (kind == "Hovering" || kind == "Entering" || kind == "Exiting")
        {
            return;                      // 这三种只计数（太密）
        }

        string line;
        try
        {
            var pt = a.CurrentPoint;
            int inter = -1;
            try
            {
                var ip = a.GetIntermediatePoints();
                inter = ip?.Count ?? 0;
            }
            catch
            {
                inter = -2;              // 接口不可用
            }

            if (kind == "Moving")
            {
                lock (_cntLock)
                {
                    _moveN++;
                    if (_moveN == 1) { _firstMoveTs = pt.Timestamp; _firstMoveTick = Environment.TickCount64; }
                    _lastMoveTs = pt.Timestamp;
                    _lastMoveTick = Environment.TickCount64;
                }
            }

            bool detail = kind != "Moving" || _moveN <= 6 || (_moveN % 50 == 0);
            if (!detail)
            {
                return;
            }

            line = $"[{Environment.TickCount64}] {kind}#{_counts[kind]} ts={pt.Timestamp}µs pos=({pt.Position.X:F1},{pt.Position.Y:F1}) raw=({pt.RawPosition.X:F0},{pt.RawPosition.Y:F0}) press={pt.Properties.Pressure:F3} inter={inter} barrel={pt.Properties.IsBarrelButtonPressed} dev={pt.PointerDevice.PointerDeviceType}";
        }
        catch (Exception ex)
        {
            line = $"[{Environment.TickCount64}] {kind} detail failed: {ex.Message}";
        }

        Log(line);
    }

    // ---------------- 合成笔注入（照抄 InkTeach 自检里的已验证写法）----------------

    private static void SendPenPoint(nint dev, float x, float y, uint pressure, bool contact, bool first)
    {
        uint flags = POINTER_FLAG_INRANGE | POINTER_FLAG_CONFIDENCE;
        if (contact) flags |= POINTER_FLAG_INCONTACT;
        if (first) flags |= POINTER_FLAG_NEW | POINTER_FLAG_PRIMARY | POINTER_FLAG_FIRSTBUTTON;

        var one = new POINTER_TYPE_INFO();
        one.type = PT_PEN;
        one.pen.pointerInfo.pointerType = PT_PEN;
        one.pen.pointerInfo.pointerFlags = flags;
        one.pen.pointerInfo.ptPixelLocationX = (int)x;
        one.pen.pointerInfo.ptPixelLocationY = (int)y;
        // hwndTarget：默认留空走正常路由；--target 时填自己窗口（对照用）。
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

    private static int _injectFails;
    private static bool _targetSelf;

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

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);

    private static void MouseEvent(uint flags, uint dx, uint dy) => mouse_event(flags, dx, dy, 0, 0);

    private static void InjectStroke()
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

            for (int i = 0; i < 5; i++)                       // 悬停进场
            {
                SendPenPoint(dev, x0, y0, 0, contact: false, first: false);
                Thread.Sleep(4);
            }

            _injStartTick = Environment.TickCount64;
            int total = 0;

            SendPenPoint(dev, x0, y0, 300, contact: true, first: true);
            total++;
            Thread.Sleep(4);

            const int n1 = 250;                               // 第一段：向右 600px（≈1 秒）
            for (int i = 1; i <= n1; i++)
            {
                float x = x0 + 600f * i / n1;
                uint p = (uint)(300 + Math.Min(1f, i / 40f) * 500);
                SendPenPoint(dev, x, y0, p, contact: true, first: false);
                total++;
                Thread.Sleep(4);
            }

            const int n2 = 180;                               // 第二段：向上 400px（90° 转弯）
            for (int i = 1; i <= n2; i++)
            {
                float y = y0 - 400f * i / n2;
                SendPenPoint(dev, x0 + 600, y, 800, contact: true, first: false);
                total++;
                Thread.Sleep(4);
            }

            SendPenPoint(dev, x0 + 600, y0 - 400, 0, contact: false, first: false);   // 收笔
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

    // ---------------- 装配（v1 原样）----------------

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

    // ---------------- Win32 ----------------

    private static bool CreateAppWindow(RECT work, bool activate)
    {
        var hInstance = GetModuleHandleW(null);
        var className = "CoreInkProbeWnd";
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

        long exStyle = 0x00200000 /*NOREDIRECTIONBITMAP*/ | 0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/;
        if (!activate) exStyle |= 0x08000000 /*NOACTIVATE*/;
        _hwnd = CreateWindowExW(
            exStyle,
            className,
            "CoreInkProbe",
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
            case 0x0246: // WM_POINTERDOWN
            case 0x0247: // WM_POINTERUP
                _msgPointer++;
                return 0;

            case 0x0200: // WM_MOUSEMOVE
            case 0x0201: // WM_LBUTTONDOWN
                _msgMouse++;
                return 0;

            case 0x0312: // WM_HOTKEY
                _stopped = true;
                return 0;

            case 0x0084: // WM_NCHITTEST
                return 1; // HTCLIENT
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static int _msgPointer;
    private static int _msgMouse;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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

    // ---------------- 真笔测试：提示窗（GDI 小条，画在最顶）----------------

    private static string _tipText = "真笔测试：请在屏幕中间的浅蓝色带里写 3~5 笔（若出现红色墨迹＝系统接管成功）；写完按 Ctrl+Alt+Shift+Q 结束（90 秒自动结束）";
    private static nint _tipBrush;

    private static void CreateTipWindow(RECT work)
    {
        var hInstance = GetModuleHandleW(null);
        var className = "CoreInkProbeTip";
        var classNamePtr = Marshal.StringToHGlobalUni(className);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&TipWndProc,
            hInstance = hInstance,
            lpszClassName = classNamePtr,
        };
        RegisterClassExW(ref wc);

        int w = 1200, h = 84;
        int x = (work.Left + work.Right) / 2 - w / 2;
        int y = work.Bottom - h - 50;
        _tipHwnd = CreateWindowExW(
            0x8 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/,
            className,
            "tip",
            0x80000000 /*POPUP*/ | 0x10000000 /*VISIBLE*/,
            x,
            y,
            w,
            h,
            0,
            0,
            hInstance,
            0);
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
            DrawTextW(hdc, _tipText, -1, ref rc, 0x25 /*CENTER|VCENTER|SINGLELINE*/);
            EndPaint(hwnd, ref ps);
            return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
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

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // ---------------- 合成笔结构体（照抄 InkTeach/Native.cs）----------------

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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref RECT data, uint winIni);

    private static void GetWorkArea(out RECT rect)
    {
        rect = default;
        SystemParametersInfoW(0x0030, 0, ref rect, 0);
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(nint device, nint pointerInfo, uint count);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public uint Size;
        public uint ThreadType;
        public uint ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
