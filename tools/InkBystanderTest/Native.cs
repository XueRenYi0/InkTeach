// Native.cs —— Win32 / GDI / 输入注入 P/Invoke 与截图像素取证
// 本文件只做互操作封装：窗口消息常量、SendInput 鼠标注入、合成笔注入、raw input 注册与解析、
// GDI（BitBlt/GetDIBits 风格的 DIB 段）截图像素统计，以及 ICompositorDesktopInterop 的 vtable 调用。
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace InkBystanderTest
{
    internal static class Native
    {
        // ---------------- 消息与窗口常量 ----------------
        public const int WM_DESTROY = 0x0002;
        public const int WM_PAINT = 0x000F;
        public const int WM_QUIT = 0x0012;
        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_TIMER = 0x0113;
        public const int WM_INPUT = 0x00FF;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_POINTERUPDATE = 0x0245;
        public const int WM_POINTERDOWN = 0x0246;
        public const int WM_POINTERUP = 0x0247;
        public const int WM_POINTERENTER = 0x0249;
        public const int WM_POINTERLEAVE = 0x024A;
        public const int WM_POINTERCAPTURECHANGED = 0x024C;

        public const int PM_REMOVE = 0x0001;
        public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        public const uint WS_VISIBLE = 0x10000000;
        public const int SW_SHOW = 5;
        public const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
        public const int COLOR_WINDOW = 5;
        public const uint IDC_ARROW = 32512;
        public const int HWND_TOPMOST = -1;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // ---------------- SendInput 鼠标 ----------------
        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        // ---------------- 合成笔（CreateSyntheticPointerDevice）----------------
        public const uint PT_PEN = 3;
        public const uint POINTER_FEEDBACK_NONE = 3;
        public const uint POINTER_FLAG_INRANGE = 0x00000002;
        public const uint POINTER_FLAG_INCONTACT = 0x00000004;
        public const uint POINTER_FLAG_PRIMARY = 0x00002000;
        public const uint POINTER_FLAG_DOWN = 0x00010000;
        public const uint POINTER_FLAG_UPDATE = 0x00020000;
        public const uint POINTER_FLAG_UP = 0x00040000;
        public const uint PEN_MASK_PRESSURE = 0x1;

        // ---------------- raw input ----------------
        public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDI_DEVICENAME = 0x20000007;

        // ---------------- GDI ----------------
        public const int SRCCOPY = 0x00CC0020;
        public const int BI_RGB = 0;
        public const int DIB_RGB_COLORS = 0;
        public const int PS_SOLID = 0;

        // ---------------- 结构体 ----------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
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
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        // INPUT 是 union：x64 下偏移 8 处放 mi/ki/hi，整体 40 字节
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
            [FieldOffset(8)] public KEYBDINPUT ki;
            [FieldOffset(8)] public HARDWAREINPUT hi;
        }

        // POINTER_INFO：x64 下 96 字节
        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_INFO
        {
            public uint pointerType, pointerId, frameId, pointerFlags;
            public IntPtr sourceDevice, hwndTarget;
            public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
            public uint dwTime, historyCount;
            public int InputData;
            public uint dwKeyStates;
            public ulong PerformanceCount;
            public int ButtonChangeType;
        }

        // POINTER_PEN_INFO：POINTER_INFO(96) + 6×4 = 120 字节
        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_PEN_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint penFlags, penMask, pressure, rotation;
            public int tiltX, tiltY;
        }

        // POINTER_TYPE_INFO 是 union（含更大的 POINTER_TOUCH_INFO=144），x64 下整体 152 字节
        [StructLayout(LayoutKind.Explicit, Size = 152)]
        public struct POINTER_TYPE_INFO
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

        // ---------------- P/Invoke ----------------
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG lpMsg);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int nExitCode);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
        [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
        [DllImport("user32.dll")] public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, int maxCount, uint mode);
        [DllImport("user32.dll")] public static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreatePen(int iStyle, int cWidth, int color);
        [DllImport("gdi32.dll")] public static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);
        [DllImport("gdi32.dll")] public static extern bool LineTo(IntPtr hdc, int x, int y);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int color);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("kernel32.dll")] public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);
        [DllImport("kernel32.dll")] public static extern bool QueryPerformanceFrequency(out long lpFrequency);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();

        // ---------------- 帧像素统计 ----------------
        public struct FrameStats
        {
            public int RedCount;      // 墨色（红）像素数
            public int NonWhiteCount; // 非背景（非白）像素数
            public int MinX, MinY, MaxX, MaxY; // 红像素 bbox（客户区坐标）
            public bool HasInk => RedCount > 0;
        }

        // 抓取窗口客户区（屏幕坐标取源），统计红墨像素与非白像素；红像素判定对鼠标光标免疫
        public static bool CaptureClientFrame(IntPtr hwnd, out FrameStats stats)
        {
            stats = default;
            if (!GetClientRect(hwnd, out RECT rc)) return false;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) return false;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) return false;

            IntPtr screenDC = GetDC(IntPtr.Zero);
            IntPtr memDC = CreateCompatibleDC(screenDC);
            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // 负值=自上而下
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;
            IntPtr hBmp = CreateDIBSection(screenDC, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (hBmp == IntPtr.Zero)
            {
                DeleteDC(memDC); ReleaseDC(IntPtr.Zero, screenDC); return false;
            }
            IntPtr old = SelectObject(memDC, hBmp);
            BitBlt(memDC, 0, 0, w, h, screenDC, origin.X, origin.Y, SRCCOPY);
            SelectObject(memDC, old);

            unsafe
            {
                byte* p = (byte*)bits;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                int red = 0, nonWhite = 0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = p + (long)y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        if (r > 128 && r - g > 60 && r - b > 60) // 红墨（抗锯齿边缘也算）
                        {
                            red++;
                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                        if (r < 235 || g < 235 || b < 235) nonWhite++;
                    }
                }
                stats.RedCount = red;
                stats.NonWhiteCount = nonWhite;
                stats.MinX = minX; stats.MinY = minY; stats.MaxX = maxX; stats.MaxY = maxY;
            }

            DeleteObject(hBmp);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, screenDC);
            return true;
        }

        // GDI 自绘折线（干墨接管实验用；rgb 为 0x00BBGGRR，红=0x000000FF）
        public static void GdiDrawPolyline(IntPtr hwnd, System.Collections.Generic.IList<POINT> pts, int thickness, int rgb)
        {
            if (pts == null || pts.Count < 2) return;
            IntPtr hdc = GetDC(hwnd);
            IntPtr pen = CreatePen(PS_SOLID, thickness, rgb);
            IntPtr old = SelectObject(hdc, pen);
            MoveToEx(hdc, pts[0].X, pts[0].Y, IntPtr.Zero);
            for (int i = 1; i < pts.Count; i++) LineTo(hdc, pts[i].X, pts[i].Y);
            SelectObject(hdc, old);
            DeleteObject(pen);
            ReleaseDC(hwnd, hdc);
        }

        // GDI 整客户区刷白（擦掉自绘内容，防止污染下一轮截图）
        public static void GdiFillWhite(IntPtr hwnd)
        {
            GetClientRect(hwnd, out RECT rc);
            IntPtr hdc = GetDC(hwnd);
            IntPtr br = CreateSolidBrush(0x00FFFFFF);
            FillRect(hdc, ref rc, br);
            DeleteObject(br);
            ReleaseDC(hwnd, hdc);
        }

        // ---------------- SendInput 鼠标注入 ----------------
        static INPUT MouseInput(uint flags, int dx = 0, int dy = 0)
        {
            return new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags }
            };
        }

        // 绝对坐标移动（虚拟桌面坐标映射，多显示器下也正确）
        public static void SendMouseAbsMove(int screenX, int screenY)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            int dx = (int)(((screenX - vx) * 65535L) / Math.Max(1, vw - 1));
            int dy = (int)(((screenY - vy) * 65535L) / Math.Max(1, vh - 1));
            INPUT[] inputs = { MouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, dx, dy) };
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }

        public static void SendMouseButton(bool down, bool right)
        {
            uint f = right
                ? (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP)
                : (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
            INPUT[] inputs = { MouseInput(f) };
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }

        // ---------------- 合成笔注入（PT_PEN，走系统指针栈，能触发笔通道湿墨渲染）----------------
        static void InjectPen(IntPtr hPen, IntPtr hwnd, int x, int y, uint flags, uint pressure)
        {
            POINTER_TYPE_INFO info = new POINTER_TYPE_INFO();
            info.type = PT_PEN;
            info.penInfo.pointerInfo.pointerType = PT_PEN;
            info.penInfo.pointerInfo.pointerId = 101; // 合成指针 id
            info.penInfo.pointerInfo.frameId = 1;
            info.penInfo.pointerInfo.pointerFlags = flags;
            info.penInfo.pointerInfo.hwndTarget = hwnd;
            info.penInfo.pointerInfo.ptPixelLocation = new POINT { X = x, Y = y };
            info.penInfo.pointerInfo.ptPixelLocationRaw = new POINT { X = x, Y = y };
            info.penInfo.pointerInfo.dwTime = (uint)GetTickCount64();
            info.penInfo.pointerInfo.historyCount = 1;
            info.penInfo.penMask = PEN_MASK_PRESSURE;
            info.penInfo.pressure = pressure;
            POINTER_TYPE_INFO[] arr = { info };
            if (!InjectSyntheticPointerInput(hPen, arr, 1))
                throw new InvalidOperationException("InjectSyntheticPointerInput 失败，hr=0x" + Marshal.GetLastWin32Error().ToString("X8"));
        }

        public static IntPtr CreatePenDevice()
        {
            IntPtr h = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_NONE);
            if (h == IntPtr.Zero) throw new InvalidOperationException("CreateSyntheticPointerDevice 失败，hr=0x" + Marshal.GetLastWin32Error().ToString("X8"));
            return h;
        }

        public static void PenDown(IntPtr hPen, IntPtr hwnd, int x, int y)
            => InjectPen(hPen, hwnd, x, y, POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_PRIMARY, 512);
        public static void PenMove(IntPtr hPen, IntPtr hwnd, int x, int y)
            => InjectPen(hPen, hwnd, x, y, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT, 512);
        public static void PenUp(IntPtr hPen, IntPtr hwnd, int x, int y)
            => InjectPen(hPen, hwnd, x, y, POINTER_FLAG_UP | POINTER_FLAG_INRANGE, 512);

        // ---------------- raw input 设备名 ----------------
        public static string GetRawInputDeviceName(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return "(null)";
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return "(unknown)";
            IntPtr buf = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                uint got = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size);
                return got == uint.MaxValue ? "(query-fail)" : Marshal.PtrToStringUni(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    // ICompositorDesktopInterop（windows.ui.composition.interop.h）——探针实测直接 cast 拿不到，
    // 这里走原生指针 QI + vtable 调用（slot 3 = CreateDesktopWindowTarget）。
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateDesktopWindowTargetDelegate(IntPtr self, IntPtr hwnd, int isTopmost, out IntPtr target);

    internal static class ComposeInterop
    {
        static readonly Guid IID_ICompositorDesktopInterop = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");

        public static Windows.UI.Composition.Desktop.DesktopWindowTarget CreateDesktopWindowTarget(
            Windows.UI.Composition.Compositor compositor, IntPtr hwnd, bool isTopmost)
        {
            // 投影对象 → 原生 IUnknown
            var marshaler = WinRT.MarshalInspectable<Windows.UI.Composition.Compositor>.CreateMarshaler(compositor, true);
            IntPtr pUnk = WinRT.MarshalInspectable<Windows.UI.Composition.Compositor>.GetAbi(marshaler);
            Guid iid = IID_ICompositorDesktopInterop;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out IntPtr pInterop);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(pInterop);
                IntPtr fp = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size); // slot 3
                var fn = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTargetDelegate>(fp);
                hr = fn(pInterop, hwnd, isTopmost ? 1 : 0, out IntPtr pTarget);
                Marshal.ThrowExceptionForHR(hr);
                return Windows.UI.Composition.Desktop.DesktopWindowTarget.FromAbi(pTarget);
            }
            finally
            {
                Marshal.Release(pInterop);
            }
        }
    }
}
