using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace InkEngine;

/// <summary>
/// 无窗口 RichEdit（ITextServices + ITextHost）的 C# 宿主。
///
/// 它本身是一个普通子窗口：外部仍用 HWND + SendMessage 操作，
/// 由这个窗口把消息转发给 ITextServices，并在 WM_PAINT 里
/// 先画背景、再用 TxDraw 画文字。这样 TextBoxEditing 的
/// Begin/Commit/边打边长/选区/颜色 span 等状态机可以原样保留。
/// </summary>
internal sealed unsafe class WindowlessTextControl : IDisposable
{
    private const string ClassName = "InkTeachWindowlessText";
    private const int WM_SETTEXT = 0x000C;
    private const int WM_GETTEXT = 0x000D;
    private const int WM_GETTEXTLENGTH = 0x000E;
    private const int WM_NCCREATE = 0x0081;
    private const int WM_NCDESTROY = 0x0082;
    private const int WM_SYSCHAR = 0x0106;
    private const int WM_GETFONT = 0x0031;
    private const int WM_SETREDRAW = 0x000B;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_IME_SETCONTEXT = 0x0281;
    private const int WM_IME_NOTIFY = 0x0282;
    private const int WM_IME_REQUEST = 0x0288;
    private const int CS_DBLCLKS = 0x0008;
    private const int CS_HREDRAW = 0x0002;
    private const int CS_VREDRAW = 0x0001;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int TXTBACK_TRANSPARENT = 0;
    private const uint TXTBIT_RICHTEXT = 0x00000001;
    private const uint TXTBIT_MULTILINE = 0x00000002;
    private const uint TXTBIT_WORDWRAP = 0x00000400;
    private const uint TXTBIT_CLIENTRECTCHANGE = 0x00100000;
    private const int DVASPECT_CONTENT = 1;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int S_OK = 0;
    private const int EN_CHANGE = 0x0300;

    private static bool _classReady;
    private static Native.WndProcDelegate? _classProc;
    private static IntPtr _hostVtbl;
    private static Guid _iidTextHost;
    private static IntPtr _msftedit;
    public static string LastError { get; private set; } = "";

    private static void Fail(string message)
    {
        LastError = message;
        Console.WriteLine("[B0] " + message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HostObject
    {
        public IntPtr Vtbl;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTL
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZEL
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CHARFORMATW
    {
        public int cbSize;
        public uint dwMask;
        public uint dwEffects;
        public int yHeight;
        public int yOffset;
        public int crTextColor;
        public byte bCharSet;
        public byte bPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szFaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PARAFORMAT
    {
        public int cbSize;
        public uint dwMask;
        public short wNumbering;
        public short wReserved;
        public int dxStartIndent;
        public int dxRightIndent;
        public int dxOffset;
        public short wAlignment;
        public short cTabCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] rgxTabs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCTW
    {
        public IntPtr lpCreateParams;
        public IntPtr hInstance;
        public IntPtr hMenu;
        public IntPtr hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public IntPtr lpszName;
        public IntPtr lpszClass;
        public int dwExStyle;
    }

    private IntPtr _hwnd;
    private IntPtr _hostObj;
    private IntPtr _services;
    private GCHandle _self;
    private IntPtr _charFormat;
    private IntPtr _paraFormat;
    private uint _defaultTextColor;
    private IntPtr _font;
    private byte[] _snapshotBgra;
    private int _snapshotW, _snapshotH;
    private int _refCount = 1;
    private bool _destroyed;

    /// <summary>宿主画背景；参数是目标内存 DC。为空时铺白底。</summary>
    public Action<IntPtr>? PaintBackground { get; set; }
    private int _viewInset;
    /// <summary>文字四周内缩（物理像素）。windowless 路径用 TxGetViewInset 提供，EM_SETRECT 不生效。</summary>
    public int ViewInset
    {
        get => _viewInset;
        set
        {
            if (_viewInset == value) return;
            _viewInset = Math.Max(0, value);
            if (_services != IntPtr.Zero)
            {
                var notify = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)(*(void***)_services)[19];
                notify(_services, 0x00002000 /*TXTBIT_VIEWINSETCHANGE*/, 0x00002000);
            }
            Invalidate();
        }
    }

    public IntPtr Hwnd => _hwnd;
    public bool IsReady => _services != IntPtr.Zero;

    public bool HasImmContext()
    {
        if (_hwnd == IntPtr.Zero) return false;
        IntPtr himc = ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero) return false;
        ImmReleaseContext(_hwnd, himc);
        return true;
    }

    private WindowlessTextControl() { }

    /// <summary>创建子窗口 + 文字服务；失败返回 null（调用方回退到 RichEdit 窗口）。</summary>
    public static WindowlessTextControl? TryCreate(IntPtr parent, IntPtr hInstance,
        int initialInset, int initialFontTwips, uint initialColor)
    {
        EnsureClass();
        if (!_classReady)
        {
            Fail("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            return null;
        }

        var host = new WindowlessTextControl { _viewInset = Math.Max(0, initialInset) };
        host._self = GCHandle.Alloc(host);
        host._hostObj = Marshal.AllocHGlobal(Marshal.SizeOf<HostObject>());
        ((HostObject*)host._hostObj)->Vtbl = _hostVtbl;
        ((HostObject*)host._hostObj)->Context = GCHandle.ToIntPtr(host._self);

        host._charFormat = Marshal.AllocHGlobal(Marshal.SizeOf<CHARFORMATW>());
        host._paraFormat = Marshal.AllocHGlobal(Marshal.SizeOf<PARAFORMAT>());
        host.ResetDefaultFormats(initialFontTwips, initialColor);

        IntPtr hwnd = Native.CreateWindowEx(0, ClassName, "InkTeachWindowlessText",
            WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, parent, IntPtr.Zero, hInstance,
            GCHandle.ToIntPtr(host._self));
        if (hwnd == IntPtr.Zero)
        {
            Fail("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            host.ReleaseUnmanaged();
            return null;
        }
        host._hwnd = hwnd;

        if (!host.InitServices())
        {
            Native.DestroyWindow(hwnd);   // WM_NCDESTROY 会释放 GCHandle
            return null;
        }
        return host;
    }

    private static void EnsureClass()
    {
        BuildVtable();
        if (_classReady) return;
        _classProc = ClassWndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = CS_DBLCLKS | CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_classProc),
            hInstance = GetModuleHandleW(null),
            hCursor = Native.LoadCursor(IntPtr.Zero, new IntPtr(32512) /* IDC_IBEAM */),
            lpszClassName = ClassName,
        };
        _classReady = Native.RegisterClassEx(ref wc) != 0;
    }

    private bool InitServices()
    {
        if (_msftedit == IntPtr.Zero)
            _msftedit = Native.LoadLibraryW("Msftedit.dll");
        if (_msftedit == IntPtr.Zero)
        {
            Fail("LoadLibrary(Msftedit) failed: " + Marshal.GetLastWin32Error());
            return false;
        }

        IntPtr pCreate = GetProcAddress(_msftedit, "CreateTextServices");
        IntPtr pIidHost = GetProcAddress(_msftedit, "IID_ITextHost");
        IntPtr pIidSvc = GetProcAddress(_msftedit, "IID_ITextServices");
        if (pCreate == IntPtr.Zero || pIidHost == IntPtr.Zero || pIidSvc == IntPtr.Zero)
        {
            Fail($"exports missing create={pCreate != IntPtr.Zero} host={pIidHost != IntPtr.Zero} svc={pIidSvc != IntPtr.Zero}");
            return false;
        }
        _iidTextHost = Marshal.PtrToStructure<Guid>(pIidHost);

        var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)pCreate;
        IntPtr unk = IntPtr.Zero;
        int hr = create(IntPtr.Zero, _hostObj, &unk);
        if (hr != S_OK || unk == IntPtr.Zero)
        {
            Fail($"CreateTextServices hr=0x{hr:X8} unk={unk}");
            return false;
        }

        Guid iidSvc = Marshal.PtrToStructure<Guid>(pIidSvc);
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)unk)[0];
        IntPtr svc = IntPtr.Zero;
        hr = qi(unk, &iidSvc, &svc);
        var rel = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)unk)[2];
        rel(unk);
        _services = svc;
        if (hr != S_OK || _services == IntPtr.Zero)
        {
            Fail($"ITextServices QI hr=0x{hr:X8} svc={svc}");
            return false;
        }

        CallInPlaceActivate();
        CallServiceNoArg(11);  // OnTxUIActivate
        return true;
    }

    private static void ReleaseIfNeeded(IntPtr p)
    {
        if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
    }

    private void ReleaseServices()
    {
        if (_services != IntPtr.Zero)
        {
            CallServiceNoArg(12);
            CallServiceNoArg(10);
            var rel = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)_services)[2];
            rel(_services);
            _services = IntPtr.Zero;
        }
        ClearBackgroundSnapshot();
        ReleaseIfNeeded(_charFormat); _charFormat = IntPtr.Zero;
        ReleaseIfNeeded(_paraFormat); _paraFormat = IntPtr.Zero;
        if (_hostObj != IntPtr.Zero) { Marshal.FreeHGlobal(_hostObj); _hostObj = IntPtr.Zero; }
    }

    private void ReleaseUnmanaged()
    {
        ReleaseServices();
        if (_self.IsAllocated) _self.Free();
    }

    public void Dispose()
    {
        if (_destroyed) return;
        _destroyed = true;
        ReleaseUnmanaged();
        PaintBackground = null;
    }

    private void ResetDefaultFormats(int fontTwips, uint color)
    {
        _defaultTextColor = color;
        var cf = new CHARFORMATW
        {
            cbSize = Marshal.SizeOf<CHARFORMATW>(),
            dwMask = Native.CFM_FACE | Native.CFM_SIZE | Native.CFM_COLOR | Native.CFM_CHARSET,
            yHeight = Math.Max(80, fontTwips),
            crTextColor = (int)color,
            bCharSet = 1,
            szFaceName = "Microsoft YaHei UI",
        };
        Marshal.StructureToPtr(cf, _charFormat, false);

        var pf = new PARAFORMAT
        {
            cbSize = Marshal.SizeOf<PARAFORMAT>(),
            dwMask = Native.PFM_LINESPACING,
            rgxTabs = new int[32],
        };
        Marshal.StructureToPtr(pf, _paraFormat, false);
    }

    public void SetDefaultFormats(ref Native.CHARFORMAT2W cf, ref Native.PARAFORMAT2 pf)
    {
        if (_charFormat == IntPtr.Zero || _paraFormat == IntPtr.Zero) return;
        _defaultTextColor = (uint)cf.crTextColor;
        var c = new CHARFORMATW
        {
            cbSize = Marshal.SizeOf<CHARFORMATW>(),
            dwMask = cf.dwMask,
            dwEffects = cf.dwEffects,
            yHeight = cf.yHeight,
            yOffset = cf.yOffset,
            crTextColor = cf.crTextColor,
            bCharSet = cf.bCharSet,
            bPitchAndFamily = cf.bPitchAndFamily,
            szFaceName = cf.szFaceName ?? "Microsoft YaHei UI",
        };
        Marshal.StructureToPtr(c, _charFormat, false);
        if (_services != IntPtr.Zero)
        {
            var notify = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)(*(void***)_services)[19];
            notify(_services, 0x00020000 /*TXTBIT_CHARFORMATCHANGE*/, 0x00020000);
        }

        var p = new PARAFORMAT
        {
            cbSize = Marshal.SizeOf<PARAFORMAT>(),
            dwMask = pf.dwMask,
            wNumbering = pf.wNumbering,
            wReserved = pf.wReserved,
            dxStartIndent = pf.dxStartIndent,
            dxRightIndent = pf.dxRightIndent,
            dxOffset = pf.dxOffset,
            wAlignment = pf.wAlignment,
            cTabCount = Math.Min((short)32, pf.cTabCount),
            rgxTabs = new int[32],
        };
        if (pf.rgxTabs != null)
            Array.Copy(pf.rgxTabs, p.rgxTabs, Math.Min(32, pf.rgxTabs.Length));
        Marshal.StructureToPtr(p, _paraFormat, false);
    }

    public void SetText(string text)
    {
        if (_services == IntPtr.Zero) return;
        IntPtr p = Marshal.StringToHGlobalUni(text ?? "");
        try
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)_services)[14];
            fn(_services, p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public string GetText()
    {
        if (_services == IntPtr.Zero) return "";
        IntPtr bstr = IntPtr.Zero;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)_services)[13];
        fn(_services, &bstr);
        if (bstr == IntPtr.Zero) return "";
        try
        {
            string s = Marshal.PtrToStringBSTR(bstr) ?? "";
            // TxGetText 末尾常带一个段落结束符；窗口版 GetWindowText 不会带。
            // 不裁掉的话，自动宽会把光标放到“下一行”，表现为再怎么打都不变宽。
            if (s.Length > 0 && s[^1] == '\r') s = s[..^1];
            return s;
        }
        finally { Marshal.FreeBSTR(bstr); }
    }

    public int GetTextLength()
        => _services == IntPtr.Zero ? 0 : Send(WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();

    public IntPtr Send(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_services == IntPtr.Zero) return IntPtr.Zero;
        IntPtr result = IntPtr.Zero;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr*, int>)(*(void***)_services)[3];
        fn(_services, msg, wParam, lParam, &result);
        return result;
    }

    public void Draw(IntPtr hdc, int width, int height)
    {
        if (_services == IntPtr.Zero || hdc == IntPtr.Zero) return;
        // 文字排版看 TxDraw 的 bounds；caret 在 TxSetCaretPos 补同一 pad。
        // 两者用同一个内缩，文字和 caret 才会重合。
        int pad = Math.Max(0, Math.Min(ViewInset, Math.Min(width, height) / 3));
        // 底部不再内缩：多行文本框最后一行不会被 bounds 裁掉。
        var rcl = new RECTL { Left = pad, Top = pad, Right = width - pad, Bottom = height };
        var upd = new Native.RECT { Left = pad, Top = pad, Right = width - pad, Bottom = height };
        // 某些 windowless 路径下 RichEdit 不主动设 GDI 文字色；先给 DC 一个
        // 和当前墨色一致的文字色，避免白板白字被画成系统黑。
        Native.SetTextColor(hdc, _defaultTextColor != 0 ? _defaultTextColor : 0x00000000);
        Native.SetBkColor(hdc, 0x00000000);
        SetBkMode(hdc, 1 /*TRANSPARENT*/);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, IntPtr,
            IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, uint, int, int>)
            (*(void***)_services)[4];
        fn(_services, DVASPECT_CONTENT, -1, IntPtr.Zero, IntPtr.Zero, hdc, IntPtr.Zero,
           (IntPtr)(&rcl), IntPtr.Zero, (IntPtr)(&upd), IntPtr.Zero, 0, 0);
    }

    private static void CopyBgraToBitmap(IntPtr hdc, IntPtr bmp, int w, int h, byte[] bgra)
    {
        if (bgra == null || w <= 0 || h <= 0 || bgra.Length < w * h * 4) return;
        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            },
        };
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            SetDIBits(hdc, bmp, 0, (uint)h, handle.AddrOfPinnedObject(),
                ref bi, Native.DIB_RGB_COLORS);
        }
        finally { handle.Free(); }
    }

    private static void StretchBgraToBitmap(IntPtr hdc, int dw, int dh,
        byte[] bgra, int sw, int sh)
    {
        if (bgra == null || sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return;
        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = sw,
                biHeight = -sh,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            },
        };
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            StretchDIBits(hdc, 0, 0, dw, dh, 0, 0, sw, sh,
                handle.AddrOfPinnedObject(), ref bi, Native.DIB_RGB_COLORS, Native.SRCCOPY);
        }
        finally { handle.Free(); }
    }

    /// <summary>把编辑前截下的画布区域（BGRA，自上而下）作为背景。</summary>
    public void SetBackgroundSnapshot(byte[] bgra, int w, int h)
    {
        ClearBackgroundSnapshot();
        if (bgra == null || w <= 0 || h <= 0 || bgra.Length < w * h * 4) return;
        _snapshotBgra = (byte[])bgra.Clone();
        // BitBlt 抓到的 32bpp 图 alpha 一般是 0；背景是屏幕实拍，统一按不透明。
        for (int i = 3; i < _snapshotBgra.Length; i += 4) _snapshotBgra[i] = 255;
        _snapshotW = w;
        _snapshotH = h;
        Invalidate();
    }

    public void ClearBackgroundSnapshot()
    {
        _snapshotBgra = null;
        _snapshotW = _snapshotH = 0;
        Invalidate();
    }

    public void OnSize()
    {
        if (_services == IntPtr.Zero) return;
        // 让 RichEdit 知道 client rect 变了；WM_SIZE 帮助它重排行。
        Native.GetClientRect(_hwnd, out var rc);
        IntPtr lParam = new(((long)rc.Height << 16) | ((long)rc.Width & 0xFFFF));
        Send(0x0005 /*WM_SIZE*/, IntPtr.Zero, lParam);
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)(*(void***)_services)[19];
        fn(_services, TXTBIT_CLIENTRECTCHANGE, TXTBIT_CLIENTRECTCHANGE);
        Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    public void OnHostSetFocus()
    {
        if (_services == IntPtr.Zero) return;
        CallServiceNoArg(11);
        Send(0x0007 /*WM_SETFOCUS*/, IntPtr.Zero, IntPtr.Zero);
        Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    public void OnHostKillFocus()
    {
        if (_services == IntPtr.Zero) return;
        Send(0x0008 /*WM_KILLFOCUS*/, IntPtr.Zero, IntPtr.Zero);
        CallServiceNoArg(12);
    }

    private void CallServiceNoArg(int index)
    {
        if (_services == IntPtr.Zero) return;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)_services)[index];
        fn(_services);
    }

    private void CallInPlaceActivate()
    {
        if (_services == IntPtr.Zero) return;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)_services)[9];
        fn(_services, IntPtr.Zero);
    }

    private void Paint()
    {
        Native.PAINTSTRUCT ps;
        IntPtr hdc = Native.BeginPaint(_hwnd, out ps);
        try
        {
            Native.GetClientRect(_hwnd, out var rc);
            int w = Math.Max(1, rc.Width);
            int h = Math.Max(1, rc.Height);
            IntPtr mem = Native.CreateCompatibleDC(hdc);
            bool useSnapshot = _snapshotBgra != null;
            IntPtr bmp = CreateCompatibleBitmap(hdc, w, h);
            IntPtr old = Native.SelectObject(mem, bmp);
            try
            {
                if (useSnapshot)
                {
                    if (_snapshotW == w && _snapshotH == h)
                        CopyBgraToBitmap(mem, bmp, w, h, _snapshotBgra);
                    else
                        StretchBgraToBitmap(mem, w, h, _snapshotBgra, _snapshotW, _snapshotH);
                }
                else
                {
                    if (PaintBackground != null) PaintBackground(mem);
                    else Native.FillRect(mem, ref rc, Native.GetStockObject(0 /*WHITE_BRUSH*/));
                }
                Draw(mem, w, h);
                Native.BitBlt(hdc, 0, 0, w, h, mem, 0, 0, Native.SRCCOPY);
            }
            finally
            {
                Native.SelectObject(mem, old);
                Native.DeleteObject(bmp);
                Native.DeleteDC(mem);
            }
        }
        finally { Native.EndPaint(_hwnd, ref ps); }
    }

    // ---- window proc ----------------------------------------------------

    private static IntPtr ClassWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        WindowlessTextControl? host = FromWindow(hwnd);
        switch (msg)
        {
            case WM_NCCREATE:
            {
                var cs = Marshal.PtrToStructure<CREATESTRUCTW>(lParam);
                Native.SetWindowLongPtr(hwnd, -21 /*GWLP_USERDATA*/, cs.lpCreateParams);
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
            }
            case WM_NCDESTROY:
            {
                IntPtr gch = Native.GetWindowLongPtr(hwnd, -21);
                Native.SetWindowLongPtr(hwnd, -21, IntPtr.Zero);
                if (host != null)
                {
                    host._destroyed = true;
                    host._hwnd = IntPtr.Zero;
                    host.ReleaseServices();
                    if (host._self.IsAllocated) host._self = default;
                }
                if (gch != IntPtr.Zero) GCHandle.FromIntPtr(gch).Free();
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
            }
            case 0x000F /*WM_PAINT*/:
                host?.Paint();
                return IntPtr.Zero;
            case 0x0014 /*WM_ERASEBKGND*/:
                return new IntPtr(1);
            case 0x0005 /*WM_SIZE*/:
                host?.OnSize();
                return IntPtr.Zero;
            case 0x0007 /*WM_SETFOCUS*/:
                host?.OnHostSetFocus();
                return IntPtr.Zero;
            case 0x0008 /*WM_KILLFOCUS*/:
                host?.OnHostKillFocus();
                return IntPtr.Zero;
            case WM_SETTEXT:
                host?.SetText(Marshal.PtrToStringUni(lParam) ?? "");
                host?.Invalidate();
                return new IntPtr(1);
            case WM_GETTEXT:
            {
                string text = host?.GetText() ?? "";
                int max = wParam.ToInt32();
                if (max > 0 && lParam != IntPtr.Zero)
                {
                    int n = Math.Min(text.Length, max - 1);
                    if (n > 0) Marshal.Copy(text.ToCharArray(), 0, lParam, n);
                    Marshal.WriteInt16(lParam, n * 2, 0);
                    return new IntPtr(n);
                }
                return new IntPtr(text.Length);
            }
            case WM_GETTEXTLENGTH:
                return new IntPtr(host?.GetTextLength() ?? 0);
            case 0x0030 /*WM_SETFONT*/:
                host?._font = wParam;
                host?.Invalidate();
                return IntPtr.Zero;
            case 0x0031 /*WM_GETFONT*/:
                return host?._font ?? IntPtr.Zero;
            case 0x0087 /*WM_GETDLGCODE*/:
                return new IntPtr(0x0004 /*DLGC_WANTALLKEYS*/);
            case 0x0020 /*WM_SETCURSOR*/:
                Native.SetCursor(Native.LoadCursor(IntPtr.Zero, new IntPtr(32512 /*IDC_IBEAM*/)));
                return new IntPtr(1);
            case 0x0113 /*WM_TIMER*/:
                host?.Send(msg, wParam, lParam);
                return IntPtr.Zero;
            case 0x000B /*WM_SETREDRAW*/:
                return IntPtr.Zero;
        }

        if (host != null && IsTextMessage(msg))
        {
            // 鼠标点也在“文字区”坐标里：去掉 TxDraw bounds 的内缩。
            if (msg >= 0x0200 && msg <= 0x020E && host.ViewInset > 0)
            {
                int pad = host.ViewInset;
                int mx = (short)(lParam.ToInt64() & 0xFFFF) - pad;
                int my = (short)((lParam.ToInt64() >> 16) & 0xFFFF) - pad;
                mx = Math.Max(0, mx);
                my = Math.Max(0, my);
                lParam = new IntPtr(((long)my << 16) | ((long)mx & 0xFFFF));
            }
            IntPtr result = host.Send(msg, wParam, lParam);
            host.Invalidate(true);
            return result;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static bool IsTextMessage(uint msg)
    {
        // 标准 EDIT/EM_* 范围 + RichEdit 的 WM_USER 扩展消息 + 键鼠/IME。
        if (msg >= 0x00B0 && msg <= 0x00DF) return true;
        if (msg >= 0x0400 && msg < 0x8000) return true;
        switch (msg)
        {
            case 0x0100: case 0x0101: case 0x0102: case 0x0103:
            case 0x0104: case 0x0105: case 0x0106:
            case 0x0200: case 0x0201: case 0x0202: case 0x0203:
            case 0x0204: case 0x0205: case 0x0206:
            case 0x020A: case 0x0286:
            case 0x010D: case 0x010E: case 0x010F:
            case 0x0281: case 0x0282: case 0x0288:
            case 0x0300: case 0x0301: case 0x0302: case 0x0303: case 0x0304:
                return true;
        }
        return false;
    }

    private void Invalidate(bool erase = false)
    {
        if (_hwnd != IntPtr.Zero) Native.InvalidateRect(_hwnd, IntPtr.Zero, erase);
    }

    private static WindowlessTextControl? FromWindow(IntPtr hwnd)
    {
        IntPtr gch = Native.GetWindowLongPtr(hwnd, -21 /*GWLP_USERDATA*/);
        return gch == IntPtr.Zero ? null : GCHandle.FromIntPtr(gch).Target as WindowlessTextControl;
    }

    // ---- ITextHost vtable ------------------------------------------------

    private static WindowlessTextControl HostFromSelf(IntPtr self)
    {
        var obj = (HostObject*)self;
        return (WindowlessTextControl)GCHandle.FromIntPtr(obj->Context).Target!;
    }

    private static void BuildVtable()
    {
        if (_hostVtbl != IntPtr.Zero) return;
        _hostVtbl = Marshal.AllocHGlobal(IntPtr.Size * 42);
        void Slot(int i, IntPtr p) => Marshal.WriteIntPtr(_hostVtbl, i * IntPtr.Size, p);

        Slot(0, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&QueryInterface);
        Slot(1, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef);
        Slot(2, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release);
        Slot(3, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)&TxGetDC);
        Slot(4, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxReleaseDC);
        Slot(5, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)&TxShowScrollBar);
        Slot(6, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)&TxEnableScrollBar);
        Slot(7, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int, int>)&TxSetScrollRange);
        Slot(8, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int>)&TxSetScrollPos);
        Slot(9, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void>)&TxInvalidateRect);
        Slot(10, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, void>)&TxViewChange);
        Slot(11, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, int>)&TxCreateCaret);
        Slot(12, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&TxShowCaret);
        Slot(13, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)&TxSetCaretPos);
        Slot(14, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&TxSetTimer);
        Slot(15, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, void>)&TxKillTimer);
        Slot(16, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr, IntPtr, IntPtr, IntPtr, uint, void>)&TxScrollWindowEx);
        Slot(17, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, void>)&TxSetCapture);
        Slot(18, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void>)&TxSetFocus);
        Slot(19, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, void>)&TxSetCursor);
        Slot(20, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxScreenToClient);
        Slot(21, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxClientToScreen);
        Slot(22, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxActivate);
        Slot(23, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&TxDeactivate);
        Slot(24, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetClientRect);
        Slot(25, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetViewInset);
        Slot(26, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetCharFormat);
        Slot(27, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetParaFormat);
        Slot(28, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, uint>)&TxGetSysColor);
        Slot(29, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetBackStyle);
        Slot(30, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetMaxLength);
        Slot(31, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetScrollBars);
        Slot(32, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetPasswordChar);
        Slot(33, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetAcceleratorPos);
        Slot(34, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetExtent);
        Slot(35, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&OnTxCharFormatChange);
        Slot(36, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&OnTxParaFormatChange);
        Slot(37, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)&TxGetPropertyBits);
        Slot(38, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)&TxNotify);
        Slot(39, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)&TxImmGetContext);
        Slot(40, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&TxImmReleaseContext);
        Slot(41, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&TxGetSelectionBarWidth);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(IntPtr self, IntPtr riid, IntPtr ppv)
    {
        if (ppv == IntPtr.Zero) return E_POINTER;
        Guid g = riid == IntPtr.Zero ? Guid.Empty : Marshal.PtrToStructure<Guid>(riid);
        if (g == Guid.Empty || g == new Guid("00000000-0000-0000-C000-000000000046") || g == _iidTextHost)
        {
            *(IntPtr*)ppv = self;
            var h = HostFromSelf(self);
            Interlocked.Increment(ref h._refCount);
            return S_OK;
        }
        *(IntPtr*)ppv = IntPtr.Zero;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(IntPtr self)
    {
        var h = HostFromSelf(self);
        return (uint)Interlocked.Increment(ref h._refCount);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Release(IntPtr self)
    {
        var h = HostFromSelf(self);
        return (uint)Interlocked.Decrement(ref h._refCount);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr TxGetDC(IntPtr self) => Native.GetDC(HostFromSelf(self)._hwnd);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxReleaseDC(IntPtr self, IntPtr hdc)
        => Native.ReleaseDC(HostFromSelf(self)._hwnd, hdc);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxShowScrollBar(IntPtr self, int bar, int show) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxEnableScrollBar(IntPtr self, int flags, int arrows) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxSetScrollRange(IntPtr self, int bar, int min, int max, int redraw) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxSetScrollPos(IntPtr self, int bar, int pos, int redraw) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxInvalidateRect(IntPtr self, IntPtr rect, int erase)
    {
        var h = HostFromSelf(self);
        if (h._hwnd != IntPtr.Zero) Native.InvalidateRect(h._hwnd, rect, erase != 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxViewChange(IntPtr self, int update)
    {
        var h = HostFromSelf(self);
        if (h._hwnd == IntPtr.Zero) return;
        if (update != 0) Native.UpdateWindow(h._hwnd);
        else Native.InvalidateRect(h._hwnd, IntPtr.Zero, false);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxCreateCaret(IntPtr self, IntPtr bmp, int w, int h)
        => CreateCaret(HostFromSelf(self)._hwnd, bmp, w, h) ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxShowCaret(IntPtr self, int show)
        => show != 0 ? (ShowCaret(HostFromSelf(self)._hwnd) ? 1 : 0) : (HideCaret(HostFromSelf(self)._hwnd) ? 1 : 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxSetCaretPos(IntPtr self, int x, int y)
    {
        var h = HostFromSelf(self);
        // 文字用 TxDraw bounds 内缩 pad；caret 坐标是 bounds 相对，补上同样的 pad。
        int pad = Math.Max(0, h.ViewInset);
        return SetCaretPos(x + pad, y + pad) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxSetTimer(IntPtr self, uint id, uint ms)
        => Native.SetTimer(HostFromSelf(self)._hwnd, new IntPtr(id), ms, IntPtr.Zero) != IntPtr.Zero ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxKillTimer(IntPtr self, uint id)
        => Native.KillTimer(HostFromSelf(self)._hwnd, new IntPtr(id));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxScrollWindowEx(IntPtr self, int dx, int dy, IntPtr scroll, IntPtr clip,
        IntPtr updateRgn, IntPtr updateRect, uint flags)
    {
        // 普通文本框没有内部滚动；画布滚动由 TextBoxEditing 处理。
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxSetCapture(IntPtr self, int capture)
    {
        if (capture != 0) Native.SetCapture(HostFromSelf(self)._hwnd);
        else Native.ReleaseCapture();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxSetFocus(IntPtr self) => Native.SetFocus(HostFromSelf(self)._hwnd);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxSetCursor(IntPtr self, IntPtr cursor, int text)
    {
        if (cursor != IntPtr.Zero) Native.SetCursor(cursor);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxScreenToClient(IntPtr self, IntPtr pt)
    {
        if (pt == IntPtr.Zero) return 0;
        var p = Marshal.PtrToStructure<Native.POINT>(pt);
        bool ok = ScreenToClient(HostFromSelf(self)._hwnd, ref p);
        if (ok) Marshal.StructureToPtr(p, pt, false);
        return ok ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxClientToScreen(IntPtr self, IntPtr pt)
    {
        if (pt == IntPtr.Zero) return 0;
        var p = Marshal.PtrToStructure<Native.POINT>(pt);
        bool ok = ClientToScreen(HostFromSelf(self)._hwnd, ref p);
        if (ok) Marshal.StructureToPtr(p, pt, false);
        return ok ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxActivate(IntPtr self, IntPtr oldState)
    {
        if (oldState != IntPtr.Zero) *(int*)oldState = 0;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxDeactivate(IntPtr self, int newState) => S_OK;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetClientRect(IntPtr self, IntPtr rect)
    {
        if (rect == IntPtr.Zero) return E_POINTER;
        var h = HostFromSelf(self);
        if (!Native.GetClientRect(h._hwnd, out var r)) return 0;
        Marshal.StructureToPtr(r, rect, false);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetViewInset(IntPtr self, IntPtr rect)
    {
        if (rect == IntPtr.Zero) return E_POINTER;
        // 内缩由 TxDraw bounds + TxSetCaretPos 处理，这里返回 0 避免重复。
        var r = new Native.RECT();
        Marshal.StructureToPtr(r, rect, false);
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetCharFormat(IntPtr self, IntPtr pp)
    {
        if (pp == IntPtr.Zero) return E_POINTER;
        var h = HostFromSelf(self);
        *(IntPtr*)pp = h._charFormat;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetParaFormat(IntPtr self, IntPtr pp)
    {
        if (pp == IntPtr.Zero) return E_POINTER;
        *(IntPtr*)pp = HostFromSelf(self)._paraFormat;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint TxGetSysColor(IntPtr self, int index)
    {
        // COLOR_HIGHLIGHTTEXT = 14。RichEdit 默认用系统“选中文字色”，
        // 浅色主题下是黑字，深色板上白字一选中就突然变黑。统一用白字，
        // 深/浅背景下配系统蓝色高亮都清楚。
        if (index == 14) return 0x00FFFFFF;
        // COLOR_WINDOWTEXT = 8：部分 windowless 路径会拿系统文字色，
        // 而不看 EM_SETCHARFORMAT。跟随当前文本框的墨色（白板白字/纸面黑字）。
        var h = HostFromSelf(self);
        if (index == 8 && h._defaultTextColor != 0) return h._defaultTextColor;
        return (uint)GetSysColor(index);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetBackStyle(IntPtr self, IntPtr style)
    {
        if (style == IntPtr.Zero) return E_POINTER;
        *(int*)style = TXTBACK_TRANSPARENT;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetMaxLength(IntPtr self, IntPtr len)
    {
        if (len == IntPtr.Zero) return E_POINTER;
        *(uint*)len = 20000;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetScrollBars(IntPtr self, IntPtr bars)
    {
        if (bars == IntPtr.Zero) return E_POINTER;
        *(uint*)bars = 0;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetPasswordChar(IntPtr self, IntPtr ch)
    {
        if (ch != IntPtr.Zero) *(char*)ch = '\0';
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetAcceleratorPos(IntPtr self, IntPtr pos)
    {
        if (pos != IntPtr.Zero) *(int*)pos = -1;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetExtent(IntPtr self, IntPtr extent)
    {
        if (extent == IntPtr.Zero) return E_POINTER;
        var h = HostFromSelf(self);
        Native.GetClientRect(h._hwnd, out var rc);
        var sz = new SIZEL
        {
            cx = Math.Max(1, rc.Width) * 2540 / 96,
            cy = Math.Max(1, rc.Height) * 2540 / 96,
        };
        Marshal.StructureToPtr(sz, extent, false);
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnTxCharFormatChange(IntPtr self, IntPtr cf) => S_OK;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnTxParaFormatChange(IntPtr self, IntPtr pf) => S_OK;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetPropertyBits(IntPtr self, uint mask, IntPtr bits)
    {
        if (bits == IntPtr.Zero) return E_POINTER;
        *(uint*)bits = mask & (TXTBIT_RICHTEXT | TXTBIT_MULTILINE | TXTBIT_WORDWRAP);
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxNotify(IntPtr self, uint notify, IntPtr data)
    {
        var h = HostFromSelf(self);
        if (notify == EN_CHANGE && h._hwnd != IntPtr.Zero)
        {
            IntPtr parent = GetParent(h._hwnd);
            if (parent != IntPtr.Zero)
                Native.PostMessage(parent, 0x0111 /*WM_COMMAND*/,
                    new IntPtr((EN_CHANGE << 16) | 1), h._hwnd);
        }
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr TxImmGetContext(IntPtr self) => ImmGetContext(HostFromSelf(self)._hwnd);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TxImmReleaseContext(IntPtr self, IntPtr himc)
    {
        if (himc != IntPtr.Zero) ImmReleaseContext(HostFromSelf(self)._hwnd, himc);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int TxGetSelectionBarWidth(IntPtr self, IntPtr width)
    {
        if (width != IntPtr.Zero) *(int*)width = 0;
        return S_OK;
    }

    // ---- native helpers -------------------------------------------------
    [DllImport("user32.dll")] private static extern bool CreateCaret(IntPtr hWnd, IntPtr hBitmap, int nWidth, int nHeight);
    [DllImport("user32.dll")] private static extern bool ShowCaret(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool HideCaret(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetCaretPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSysColor(int index);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref Native.POINT pt);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref Native.POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern int SetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        IntPtr bits, ref Native.BITMAPINFO bi, uint usage);
    [DllImport("gdi32.dll")] private static extern int StretchDIBits(IntPtr hdc, int xDst, int yDst,
        int wDst, int hDst, int xSrc, int ySrc, int wSrc, int hSrc, IntPtr bits,
        ref Native.BITMAPINFO bi, uint usage, uint rop);

    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr himc);
}
