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
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_LBUTTONDOWN = 0x0201;
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
