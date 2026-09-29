using System.Runtime.InteropServices;

namespace InkEngine;

internal static class Native
{
    // ---- window messages -------------------------------------------------
    public const int WM_DESTROY = 0x0002;
    public const int WM_SIZE = 0x0005;
    public const int WM_PAINT = 0x000F;
    public const int WM_CLOSE = 0x0010;
    public const int WM_ERASEBKGND = 0x0014;
    public const int WM_SETCURSOR = 0x0020;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_TIMER = 0x0113;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_MOUSELEAVE = 0x02A3;
    public const int WM_POINTERUPDATE = 0x0245;
    public const int WM_POINTERDOWN = 0x0246;
    public const int WM_POINTERUP = 0x0247;
    public const int WM_POINTERENTER = 0x0249;
    public const int WM_POINTERLEAVE = 0x024A;
    public const int WM_POINTERCAPTURECHANGED = 0x024C;
    public const int WM_DPICHANGED = 0x02E0;

    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
    public const int MA_NOACTIVATE = 3;

    // ---- window styles ---------------------------------------------------
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_HIDE = 0;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // ---- hotkey modifiers ------------------------------------------------
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- pointer ---------------------------------------------------------
    // POINTER_INPUT_TYPE: 1 is PT_POINTER (unspecified), mouse is 4.
    public const uint PT_POINTER = 1;
    public const uint PT_TOUCH = 2;
    public const uint PT_PEN = 3;
    public const uint PT_MOUSE = 4;
    public const uint PT_TOUCHPAD = 5;
    public const uint PEN_FLAG_INVERTED = 0x00000002;
    public const uint PEN_FLAG_ERASER = 0x00000004;

    // ---- 光标 -------------------------------------------------------------
    // IDC_* 都是 MAKEINTRESOURCE 的序号，用 LoadCursor(NULL, 序号) 取。
    // 语义见微软的 "About Cursors"：这些名字什么时候用是平台约定，不是随便挑的。
    public const int IDC_ARROW = 32512;      // Normal select
    public const int IDC_IBEAM = 32513;      // Text select
    public const int IDC_WAIT = 32514;       // Busy：只有真卡住才该出现
    public const int IDC_CROSS = 32515;      // Precision select
    public const int IDC_UPARROW = 32516;    // Alternate select
    // 笔（"a pen cursor"）：微软的附加光标，WinUser.h 里**没有**给它名字，序号就是 32631。
    // InkClass / Ink Canvas 的 Cursor="Pen"（WPF）拿到的就是它。实测（tmp/penprobe）：
    // 32×32、热点在笔尖 (0,0)、白笔身黑描边、固定配色（不跟墨色/笔宽）。
    // ⚠ 产品现在不加载它了（2026-09-27 换成自绘"彩笔"，跟墨色/笔宽，见 Cursors.RenderPen）
    // ——这里留着当**出处**：姿态、热点、比例都是照它量的，也留着做 A/B。
    public const int IDC_PEN = 32631;
    public const int IDC_SIZENWSE = 32642;   // Diagonal resize 1
    public const int IDC_SIZENESW = 32643;   // Diagonal resize 2
    public const int IDC_SIZEWE = 32644;     // Horizontal resize
    public const int IDC_SIZENS = 32645;     // Vertical resize
    public const int IDC_SIZEALL = 32646;    // Move
    public const int IDC_NO = 32648;         // Unavailable
    public const int IDC_HAND = 32649;       // Link select：只能表示链接
    public const int IDC_APPSTARTING = 32650;// Working in background

    public const int SM_CXCURSOR = 13;
    public const int SM_CYCURSOR = 14;

    public const uint TME_LEAVE = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;          // false = 光标
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetCursor();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    /// <summary>读一个光标的详情（热点 + 两个位图）。⚠ 两个位图是**副本**，用完要 DeleteObject。
    /// 自检读"斜笔的热点在不在笔尖"用（见 Cursors.HotspotOf）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO iconInfo);

    [DllImport("user32.dll")]
    public static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    // ---- dpi -------------------------------------------------------------
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // ---- structs ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
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
    public struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX, tiltY;
    }

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    // ---- user32 ----------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(long dwExStyle, string lpClassName, string lpWindowName,
        long dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    public static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint removeMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern bool WaitMessage();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// 分层窗口的整体透明度。面板的"接输入小窗"用它把自己压到 1/255：
    /// 肉眼不可见，但**在**——既不能用 alpha=0（等于点不到自己），
    /// 也不能把窗口挪出屏幕（那样同样收不到输入）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// 把自己挂到**父进程的控制台**上（没有就失败）。
    ///
    /// 为什么要它：产品发布时工程改成 WinExe（双击不能弹出黑框），但**自检和出图几乎
    /// 全是从命令行跑的**，那些 `Console.WriteLine` 不能一起丢掉。两者兼顾的做法就是
    /// "有命令行参数时试着接父控制台"：从终端跑 → 接上、输出照旧；
    /// 从资源管理器双击（无参数）→ 接不上、也就不会有黑框。
    /// 见 <c>InkTeach.Program.Main</c>。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    /// <summary><see cref="AttachConsole"/> 的参数：挂到父进程上。</summary>
    public const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    /// 读系统设置。自检/翻页用它问"在 Windows 中显示动画"这一条
    /// （`SPI_GETCLIENTAREAANIMATION`）：关掉时动效直接跳终态。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    /// <summary>
    /// 读系统设置（这是另一个入参形态）：<c>SPI_GETWORKAREA</c> 要的是一个 <see cref="RECT"/>，
    /// 它给的是**主屏的工作区**——屏幕减掉任务栏之后剩下的那块。
    ///
    /// 为什么要它（用户 2026-09-27）：悬浮条的默认位置要"落在任务栏**上方**、两者不重叠"。
    /// 按整个屏幕算的话，贴底那一条会压住任务栏（我们的覆盖层是置顶的，任务栏挡不住它，
    /// 而且面板矩形是"我们的地盘"、会吃掉那一块的点击）。工作区这个数天然把任务栏扣掉了，
    /// 任务栏在底部/左侧/顶部都成立。
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    public static extern bool SystemParametersInfoRect(uint uiAction, uint uiParam,
                                                       ref RECT pvParam, uint fWinIni);

    public const uint SPI_GETWORKAREA = 0x0030;

    public const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // ---- 系统手势：在本窗口内关掉"按住不动 = 右键" ------------------------
    //
    // 为什么需要它：我们要做"笔按住不动 400ms → 把这一笔变成规整图形"（见
    // 计划-图形工具.md 的停顿成型那一批）。而 Windows 自己就有一条同款手势——
    // 笔/手指按住不动 = 右键（带一圈反馈动画）。两条撞在一起的结果是：
    // 用户按得越稳，系统越可能先弹右键环，我们的"停顿"根本轮不到。
    //
    // 关掉的是**我们窗口内**的这一条，不改系统全局设置（用户自己开的那个开关
    // 只影响别的程序）。三条路都走一遍，理由见各自的注释：
    //   · `WM_TABLET_QUERYSYSTEMGESTURESTATUS` 回 `TABLET_DISABLE_PRESSANDHOLD`
    //     —— 官方文档原话就写着它 "disables press and hold (right-click) gesture"，
    //     是**同时关手势和右键消息**的那一条（其余两条只保证关反馈/关手势）；
    //   · `SetProp(MicrosoftTabletPenServiceProperty, 1)` —— 官方 how-to 的等价写法；
    //   · `SetGestureConfig` 把全部手势挡掉 —— Raymond Chen 给的 Win7+ 路径。
    //
    // ⚠ 拿不准的一条（**不许当结论**）：`SetWindowFeedbackSetting(FEEDBACK_*_PRESSANDHOLD)`
    // 名字和文档都只说"visual feedback（反馈/动画）"，**它到底阻不阻止右键消息，官方没说**。
    // 所以这里**不用它**——要靠"关反馈"来解决问题的话，等于赌一条没有出处的结论。
    public const uint WM_TABLET_DEFBASE = 0x02C0;
    public const uint WM_TABLET_QUERYSYSTEMGESTURESTATUS = WM_TABLET_DEFBASE + 12;   // 0x02CC

    /// <summary>关掉"按住不动 → 右键"这条手势。官方文档（Tpcshrd.h）原话：
    /// "disables press and hold (right-click) gesture"，也就是我们窗口内不再产生右键。</summary>
    public const int TABLET_DISABLE_PRESSANDHOLD = 0x0001;

    /// <summary>关掉抬笔那一下的水波反馈（顺带关，和按压无关但同属"别在板书时闪一下"）。</summary>
    public const int TABLET_DISABLE_PENTAPFEEDBACK = 0x0008;

    /// <summary>关掉笔筒按钮那一圈反馈。</summary>
    public const int TABLET_DISABLE_PENBARRELFEEDBACK = 0x0010;

    /// <summary>SetGestureConfig 的一条配置：`dwID = 0` 时 `dwBlock = GC_ALLGESTURES` 挡掉全部手势。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GESTURECONFIG
    {
        public uint dwID;
        public uint dwWant;
        public uint dwBlock;
    }

    public const uint GC_ALLGESTURES = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetGestureConfig(IntPtr hwnd, uint dwReserved, uint cIDs,
                                               ref GESTURECONFIG pGestureConfig, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    /// <summary>
    /// 在本窗口内关掉系统的"按住不动 = 右键"手势（见上面那一段常量注释）。
    ///
    /// 三条路一起走、任一条成功就算成功：单条路在不同驱动/系统版本上偶有失效的报告，
    /// 而三条都是幂等且互不冲突的。返回"至少一条成功"，调用方把它印进启动横幅
    /// ——**这个开关到底生效没有，必须一眼看得见**（不然"停顿没反应"这件事
    /// 会被记成"识别不准"，排查方向从一开始就错了）。
    /// </summary>
    public static bool DisableSystemPressAndHold(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        bool ok = false;

        // ① 窗口属性：官方 how-to 的写法（"只要能拿到窗口句柄，就能关掉长按"）。
        try
        {
            int flags = TABLET_DISABLE_PRESSANDHOLD
                      | TABLET_DISABLE_PENTAPFEEDBACK
                      | TABLET_DISABLE_PENBARRELFEEDBACK;
            if (SetProp(hwnd, "MicrosoftTabletPenServiceProperty", new IntPtr(flags))) ok = true;
        }
        catch { }

        // ② 挡掉全部系统手势（Win7+）。我们本来就不吃 WM_GESTURE —— 笔迹、翻页、
        //    滚动条全是自己按 WM_POINTER 算的，挡掉不存在"误伤自己"的问题。
        try
        {
            var cfg = new GESTURECONFIG { dwID = 0, dwWant = 0, dwBlock = GC_ALLGESTURES };
            if (SetGestureConfig(hwnd, 0, 1, ref cfg, (uint)Marshal.SizeOf<GESTURECONFIG>())) ok = true;
        }
        catch { }

        // ③ 消息那条在 WndProc 里回（见 Engine.WndProc 的 WM_TABLET_QUERYSYSTEMGESTURESTATUS）——
        //    它是系统主动来问的，答在那边才生效，这里没有对应调用。
        return ok;
    }

    // ---- pointer input ---------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnableMouseInPointer(bool enable);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO pointerInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerPenInfoHistory(uint pointerId, ref uint entriesCount, [Out] POINTER_PEN_INFO[] penInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerInfoHistory(uint pointerId, ref uint entriesCount, [Out] POINTER_INFO[] pointerInfo);

    // ---- kernel32 --------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, bool alertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll")]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 内存计数器的**扩展版 2**（Windows 11 / SDK 22000 起）。
    ///
    /// 关键在最后两个字段：
    ///   <c>PrivateWorkingSetSize</c> = **专用工作集**，也就是任务管理器"内存"
    ///     那一列显示的数（实测与 PerfCounter `\Process\Working Set - Private`、
    ///     NtQuerySystemInformation 的 WorkingSetPrivateSize 三者完全一致）；
    ///   <c>SharedCommitUsage</c> = 共享提交，解释"工作集里有多少是别人的"。
    ///
    /// 老系统上这两个字段会被留 0（系统按 cb 截断拷贝），调用方据此回退。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage, PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetProcessMemoryInfo(IntPtr hProcess,
                                                   ref PROCESS_MEMORY_COUNTERS_EX2 counters,
                                                   uint cb);

    /// <summary>进程时间（CPU 用量）。比 Process.Refresh 便宜两个数量级。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr hProcess,
                                              out long creation, out long exit,
                                              out long kernel, out long user);

    // ---- keyboard/mouse state (used for modifier checks) -----------------
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>往窗口队列塞一条消息。自检里用它模拟"批注键盘模式下按了一个键"。</summary>
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 线程消息（PPT 轮询线程 → 主线程的唤醒）---------------------------
    //
    // 为什么不给窗口 PostMessage：**不需要 hwnd**。主循环的消息泵本来就是
    // "当前线程的队列"（`PeekMessage(hWnd: IntPtr.Zero, ...)`），线程消息
    // 直接落在同一个队列里，引擎在 DrainMessages 里认一个自定义消息号就够了。

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 子窗口排查（导出对话框探针用）----------------------------------

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder s, int n);

    // 注：`MONITORINFO` / `GetMonitorInfo` 上面已经有了（多屏那块用的），这里不再重复定义。
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    /// <summary>`MONITOR_DEFAULTTONEAREST`：窗口不在任何显示器上时给最近的那块。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // ---- painting --------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public unsafe fixed byte rgbReserved[32];
    }

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    // ---- screen capture (verification only) ------------------------------

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>SM_DIGITIZER：系统有没有数字化器（触摸 / 笔）。返回值按位解释。</summary>
    public const int SM_DIGITIZER = 94;
    /// <summary>SM_DIGITIZER 的位：内置笔 / 外接笔。有这两位之一，才值得开委托墨迹轨迹。</summary>
    public const int NID_INTEGRATED_PEN = 0x04;
    public const int NID_EXTERNAL_PEN = 0x08;

    public const uint SRCCOPY = 0x00CC0020;
    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>前台窗口。PPT 联动用它判"哪个放映窗口在前面"（多个 PPT 实例时挑对那个）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 最简单的消息框。**只在"没有控制台"的场合用**（WinExe 双击启动）：
    /// 崩溃时那句话没人看得到，至少得让用户看见一眼、知道去哪找详情。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public const uint MB_OK = 0x00000000;
    public const uint MB_ICONERROR = 0x00000010;

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    // =====================================================================
    //  剪贴板（截图落到剪贴板、粘贴图片）
    //
    //  只用 GDI 那套经典格式（CF_DIB / CF_DIBV5 / CF_BITMAP），**不注册
    //  自定义格式、不引 WIC**：
    //    · CF_DIB 是 1993 年就有的格式，任何程序都认（微信、QQ、Office、
    //      画图、浏览器……），比"只写 PNG 格式"通用得多；
    //    · 读的时候优先 CF_DIBV5（它带显式的 alpha 通道和 sRGB 信息），
    //      退回 CF_DIB（alpha 是垃圾，按不透明处理，见 ImageData.Adopt）。
    // =====================================================================

    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_DIBV5 = 17;
    public const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// 注册一个自定义剪贴板格式（返回它的格式号，同一进程内同一个名字只注册一次）。
    /// 我们用 "InkTeach.InkObjects" 放**我们自己的对象字节**——粘回来仍是可编辑对象，
    /// 而不是一张图（见 ClipboardInk）。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetPriorityClipboardFormat(uint[] paFormatPriorityList, int cFormats);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    // =====================================================================
    //  DWM：合成配速
    // =====================================================================

    /// <summary>
    /// 阻塞到 DWM 完成下一次合成为止。返回 0 表示成功。
    ///
    /// 为什么需要它：Present(1) 是把线程**卡在垂直同步里**，这段时间消息队列
    /// 没人抽，笔再快也得排队。改成"先 DwmFlush 等到合成边界、抽完消息、
    /// 立刻 Present(0)"，输入就不用等了。
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    public const uint QS_ALLINPUT = 0x04FF;

    /// <summary>
    /// 阻塞到"有消息"或超时，二选一先到先返回。
    ///
    /// 用它而不是 Thread.Sleep(1)：Sleep 的精度受系统计时器粒度限制，默认
    /// 一粒就是 15.6ms——足够错过一整个刷新周期。实测配速模式下把 Sleep(1)
    /// 用在空闲分支上，帧数从 60 掉到 47，就是这一粒造成的。
    /// 有消息就立刻返回，所以连续书写时它不引入任何等待。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(
        uint count, IntPtr handles, uint timeoutMs, uint wakeMask, uint flags);

    // =====================================================================
    //  合成笔输入（仅开发期实验用）
    // =====================================================================
    // 目的：验证"委托墨迹轨迹"（IDCompositionDelegatedInkTrail）这条通道到底
    // 认不认合成输入。它此前在本机验证不了，是因为只有合成鼠标可用，而系统
    // 很可能只对 PT_PEN 启用这条通道。CreateSyntheticPointerDevice(PT_PEN)
    // 造出来的消息走的是和真笔同一条 WM_POINTER 路径（pointerType = PT_PEN、
    // 带压感），所以能把这条链路跑通。

    public const uint POINTER_FLAG_NEW = 0x00000001;
    public const uint POINTER_FLAG_INRANGE = 0x00000002;
    public const uint POINTER_FLAG_INCONTACT = 0x00000004;
    public const uint POINTER_FLAG_FIRSTBUTTON = 0x00000010;
    public const uint POINTER_FLAG_PRIMARY = 0x00002000;
    public const uint POINTER_FLAG_CONFIDENCE = 0x00004000;

    public const uint PEN_MASK_PRESSURE = 0x00000001;
    public const uint PEN_MASK_ROTATION = 0x00000002;
    public const uint PEN_MASK_TILT_X = 0x00000004;
    public const uint PEN_MASK_TILT_Y = 0x00000008;

    /// <summary>POINTER_FEEDBACK_DEFAULT。合成设备必须给一个反馈模式。</summary>
    public const uint POINTER_FEEDBACK_DEFAULT = 1;

    /// <summary>
    /// POINTER_TYPE_INFO：真实定义里中间是一个 union，最大成员是
    /// POINTER_TOUCH_INFO（144 字节），我们只用 pen 分支（120 字节）。
    /// 差的 24 字节必须显式补出来，否则 API 按 union 的真实大小读写会越界
    /// （第一次写这个探针时就是这么崩的：0xC0000374 堆损坏）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TYPE_INFO
    {
        public uint type;
        public POINTER_PEN_INFO pen;
        public unsafe fixed byte unionPad[24];
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool InjectSyntheticPointerInput(
        IntPtr device, POINTER_TYPE_INFO[] pointerInfo, uint count);

    // ---- 注册表（只读，用来诊断"系统笔设置"）------------------------------
    //
    // 为什么用 P/Invoke 而不是 Microsoft.Win32.Registry：那个类型在 .NET Core 上要额外引包，
    // 而这个项目没有引；advapi32 的这几个函数一直是系统自带的，零依赖。
    //
    // 用途只有一个：把**系统级的笔延迟来源**打印出来。最典型的是"长按当右键"——
    // 笔尖停住不动会被判成长按，而板书时停顿是常态。
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint samDesired,
                                           out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved,
                                              out uint type, byte[] data, ref uint cbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(IntPtr hKey);

    public static readonly IntPtr HKEY_CURRENT_USER = new IntPtr(unchecked((int)0x80000001));
    public const uint KEY_READ = 0x00020019;
    public const uint REG_DWORD = 4;
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;

    /// <summary>
    /// 读一个 HKCU 下的 DWORD。读不到就把原因放在 <paramref name="err"/> 里返回 false
    /// （键不存在 / 值不存在 / 读失败）——**不猜、不用默认值顶替**。
    /// </summary>
    public static bool ReadDword(string subKey, string valueName, out uint value, out string err)
    {
        value = 0;
        err = null;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, subKey, 0, KEY_READ, out IntPtr hk) != ERROR_SUCCESS)
        {
            err = "键不存在";
            return false;
        }
        try
        {
            uint type = 0, cb = 4;
            var buf = new byte[4];
            int rc = RegQueryValueExW(hk, valueName, IntPtr.Zero, out type, buf, ref cb);
            if (rc != ERROR_SUCCESS)
            {
                err = rc == ERROR_FILE_NOT_FOUND ? "值不存在" : $"读失败 rc={rc}";
                return false;
            }
            if (type != REG_DWORD)
            {
                err = $"类型不是 DWORD（{type}）";
                return false;
            }
            value = BitConverter.ToUInt32(buf, 0);
            return true;
        }
        finally { RegCloseKey(hk); }
    }

    [DllImport("user32.dll")]
    public static extern void DestroySyntheticPointerDevice(IntPtr device);

    public const int POINTER_DEVICE_PRODUCT_STRING_MAX = 520;

    /// <summary>
    /// POINTER_DEVICE_INFO。注意这**不是**指针消息里的 POINTER_INFO——
    /// 第一次写的时候按"displayOrientation + device + type + ..."猜布局，
    /// 结果 GetPointerDevices 把托管堆写坏直接崩进程。字段顺序和大小必须照抄
    /// 官方定义（尾部那 520 个 wchar 是最容易漏的）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_DEVICE_INFO
    {
        public uint displayOrientation;
        public IntPtr device;
        public uint pointerDeviceType;
        public IntPtr monitor;
        public uint startingCursorId;
        public ushort maxActiveContacts;
        public unsafe fixed ushort productString[POINTER_DEVICE_PRODUCT_STRING_MAX];
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerDevices(ref uint deviceCount, [Out] POINTER_DEVICE_INFO[] devices);

    // POINTER_DEVICE_TYPE：设备的"出身"。**这块板子/这支笔的笔尖是不是就在屏幕上**，
    // 就靠它区分（见 Engine.PenDeviceOnScreen）：
    //   INTEGRATED_PEN = 触摸屏自带的笔（写字时笔尖压着屏幕，落点就是笔尖）；
    //   EXTERNAL_PEN   = 外接手写板/数位屏（笔尖在板子上，屏幕上必须给光标指示）。
    public const uint POINTER_DEVICE_TYPE_INTEGRATED_PEN = 0x00000001;
    public const uint POINTER_DEVICE_TYPE_EXTERNAL_PEN = 0x00000002;
    public const uint POINTER_DEVICE_TYPE_INTEGRATED_TOUCH = 0x00000003;
    public const uint POINTER_DEVICE_TYPE_EXTERNAL_TOUCH = 0x00000004;

    /// <summary>按设备句柄（POINTER_INFO.sourceDevice）查这一个设备的详情。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerDevice(IntPtr device, out POINTER_DEVICE_INFO pointerDevice);

    // ---- synthetic input (used by the automated input-path test) ----------

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ---- 合成键盘输入（自检用：验证全局热键真的能"按"出来）------------------
    //
    // Windows 的 INPUT 是一个**联合体**：前 4 字节是 type，后面按 type 解释成
    // 鼠标 / 键盘 / 硬件事件。上面那个 INPUT 是按鼠标那一支写的（自检一直在用），
    // 这里再给键盘写一支**同布局的**结构，而不是把 INPUT 改成 Explicit 联合：
    // 联合成员的偏移在 x64 是 8、在 x86 是 4，写成 Explicit 就得按位数分两套，
    // 代价比分两份结构大。SendInput 只认那块内存的前 4 字节（type）和 cbSize，
    // 所以传键盘这一支的结构体一样能进去。

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT_KBD
    {
        public uint type;
        public KEYBDINPUT ki;
        /// <summary>
        /// 补到和 <see cref="INPUT"/> 一样大（这个工程是 x64 的，INPUT = 40 字节）。
        ///
        /// **不是凑数**：SendInput 要求 cbSize 就是 INPUT 的大小（不是"这一支联合体成员"的大小），
        /// 而且它是按 cbSize 当步长遍历数组的——少这几个字节，一次发多个事件就会错位，
        /// 函数直接返回 0（一个事件都没进系统）。第一次写这版就是这样，自检里"注入事件 0/6"，
        /// 而工具一个没换。
        /// </summary>
        private ulong _pad;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT_KBD[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
