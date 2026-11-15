// =====================================================================================
//  PPT COM 的 AOT 安全内核（2026-10-05）
//
//  背景：NativeAOT **不支持内置 COM**（[ComImport] / RCW / Type.InvokeMember /
//  System.Runtime.InteropServices.ComTypes 那一套），而 PPT/WPS 联动必须走
//  IDispatch。这里用**原始 vtable 函数指针**实现最小可用的两件事：
//
//    · ROT（运行对象表）枚举：IRunningObjectTable / IEnumMoniker / IMoniker / IBindCtx
//    · IDispatch 迟绑定：GetIDsOfNames + Invoke（属性读取、0 参方法、1 个 int 参数）
//
//  只服务 PptComSource，不追求通用；每个方法的 vtable 槽位都标了出处，改的时候
//  对着 objidl.h 数一遍。参数一律走 IntPtr / 栈上 VARIANT，不经过运行时 marshalling。
// =====================================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace InkEngine;

internal static unsafe class ComLate
{
    // ---- VARIANT 类型常数（wtypes.h）----
    public const ushort VT_EMPTY = 0, VT_NULL = 1, VT_I2 = 2, VT_I4 = 3,
                        VT_BSTR = 8, VT_DISPATCH = 9, VT_BOOL = 11, VT_UNKNOWN = 13,
                        VT_UI4 = 19, VT_I8 = 20, VT_INT = 22;

    // ---- IDispatch 的 wFlags（oaidl.h）----
    private const ushort DISPATCH_METHOD = 1, DISPATCH_PROPERTYGET = 2;

    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IntPtr prot);
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IntPtr ppbc);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(IntPtr pvarg);

    private const uint COINIT_MULTITHREADED = 0;
    [ThreadStatic] private static bool _comReady;

    /// <summary>`--pptdebug`：把 IDispatch 调用的 HRESULT 打出来。</summary>
    internal static bool Debug;
    private static void D(string s) { if (Debug) Console.WriteLine("[com] " + s); }

    /// <summary>Office 自动化一律用 en-US 解析名字：跟本地化文化走时，方法名可能解析不到。</summary>
    private const uint LCID_EN_US = 0x0409;

    /// <summary>
    /// **裸 vtable 路线的必修课**：原来 .NET 内置 COM 会在创建 RCW 时自动给线程做
    /// CoInitializeEx；现在自己调 IDispatch，线程必须先初始化，否则 CreateBindCtx
    /// 这类调用直接返回 CO_E_NOTINITIALIZED（0x800401F0），表现是"ROT 扫出 0 个"。
    /// MTA 即可：轮询线程没有消息泵，STA 反而要泵消息；已初始化过（S_FALSE）或
    /// 已被别的模式初始化（RPC_E_CHANGED_MODE）都算可用。
    /// </summary>
    public static void EnsureCom()
    {
        if (_comReady) return;
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        if (hr == 0 /*S_OK*/ || hr == 1 /*S_FALSE*/ || hr == unchecked((int)0x80010106) /*RPC_E_CHANGED_MODE*/)
            _comReady = true;
    }

    /// <summary>x64 的 VARIANTARG：头 8 字节 + 联合体 8 字节（DECIMAL 撑到 16）+ 对齐 = 24。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int lVal;
        [FieldOffset(8)] public short bVal;
        [FieldOffset(8)] public long llVal;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dispparams
    {
        public IntPtr rgvarg;
        public IntPtr rgdispidNamedArgs;
        public uint cArgs;
        public uint cNamedArgs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IntPtr* Vtbl(IntPtr p) => *(IntPtr**)p;

    /// <summary>IUnknown::Release（槽位 2）。</summary>
    public static void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(p)[2])(p); } catch { }
    }

    /// <summary>IUnknown::QueryInterface（槽位 0）。</summary>
    private static int QueryInterface(IntPtr p, in Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        if (p == IntPtr.Zero) return unchecked((int)0x80004003);
        Guid g = iid; IntPtr tmp;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(p)[0])(p, &g, &tmp);
        if (hr == 0) ppv = tmp;
        return hr;
    }

    /// <summary>QI 出 IDispatch；失败返回 0。</summary>
    public static IntPtr QiDispatch(IntPtr punk)
        => QueryInterface(punk, IID_IDispatch, out var p) == 0 ? p : IntPtr.Zero;

    // ---- ROT（ole32）----

    public static int GetRot(out IntPtr rot) => GetRunningObjectTable(0, out rot);
    public static int CreateBindCtx(out IntPtr ctx) => CreateBindCtx(0, out ctx);

    /// <summary>IRunningObjectTable::EnumRunning（槽位 9）。</summary>
    public static int RotEnumRunning(IntPtr rot, out IntPtr penum)
    {
        penum = IntPtr.Zero; IntPtr tmp;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(rot)[9])(rot, &tmp);
        if (hr == 0) penum = tmp;
        return hr;
    }

    /// <summary>IRunningObjectTable::GetObject（槽位 6）。</summary>
    public static int RotGetObject(IntPtr rot, IntPtr moniker, out IntPtr obj)
    {
        obj = IntPtr.Zero; IntPtr tmp;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(rot)[6])(rot, moniker, &tmp);
        if (hr == 0) obj = tmp;
        return hr;
    }

    /// <summary>IEnumMoniker::Next（槽位 3，一次只要一个）。</summary>
    public static int EnumNext(IntPtr penum, IntPtr* rgelt, out uint fetched)
    {
        fetched = 0; uint tmp;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)Vtbl(penum)[3])(penum, 1, rgelt, &tmp);
        if (hr == 0) fetched = tmp;
        return hr;
    }

    /// <summary>IMoniker::GetDisplayName（槽位 20）。vtable = IUnknown(3) + IPersistStream(5)
    /// + IMoniker 自身方法（BindToObject=8 … RelativePathTo=19、GetDisplayName=20）。
    /// 返回的 LPOLESTR 由 CoTaskMemFree 释放。</summary>
    public static int GetDisplayName(IntPtr moniker, IntPtr bindCtx, out IntPtr psz)
    {
        psz = IntPtr.Zero; IntPtr tmp;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Vtbl(moniker)[20])(
            moniker, bindCtx, IntPtr.Zero, &tmp);
        if (hr == 0) psz = tmp;
        return hr;
    }

    // ---- IDispatch（oleaut32 的语义，走对象自己的 vtable）----

    /// <summary>IDispatch::GetIDsOfNames（槽位 5）。LCID 用当前文化，和原来的 dynamic 一致。</summary>
    private static int GetId(IntPtr disp, string name, out int id)
    {
        id = -1;
        fixed (char* p = name)
        {
            IntPtr* names = stackalloc IntPtr[1];
            names[0] = (IntPtr)p;
            Guid iid = Guid.Empty; int tmp;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint, uint, int*, int>)Vtbl(disp)[5])(
                disp, &iid, names, 1, LCID_EN_US, &tmp);
            if (hr == 0) { id = tmp; D($"GetIDsOfNames(\"{name}\") id={id}"); }
            else D($"GetIDsOfNames(\"{name}\") hr=0x{hr:X8}");
            return hr;
        }
    }

    /// <summary>IDispatch::Invoke（槽位 6）。</summary>
    private static int Invoke(IntPtr disp, int id, ushort flags, IntPtr args, uint nArgs, out Variant result)
    {
        var dp = new Dispparams { rgvarg = args, cArgs = nArgs };
        Guid iid = Guid.Empty; Variant v = default;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, uint, ushort, Dispparams*, Variant*, IntPtr, IntPtr, int>)Vtbl(disp)[6])(
            disp, id, &iid, LCID_EN_US, flags, &dp, &v, IntPtr.Zero, IntPtr.Zero);
        result = v;
        return hr;
    }

    private static void Clear(ref Variant v)
    {
        try { VariantClear((IntPtr)Unsafe.AsPointer(ref v)); } catch { }
    }

    /// <summary>读一个整数属性；拿不到返回 false。</summary>
    public static bool TryGetInt(IntPtr obj, string name, out int value)
    {
        value = 0;
        if (obj == IntPtr.Zero || GetId(obj, name, out int id) != 0) return false;
        if (Invoke(obj, id, DISPATCH_PROPERTYGET, IntPtr.Zero, 0, out var v) != 0) return false;
        switch (v.vt)
        {
            case VT_I4: case VT_INT: value = v.lVal; return true;
            case VT_I2: value = v.bVal; return true;
            case VT_UI4: value = (int)(uint)v.lVal; return true;
            case VT_I8: value = (int)v.llVal; return true;
            case VT_BOOL: value = v.bVal != 0 ? 1 : 0; return true;
            default: Clear(ref v); return false;
        }
    }

    /// <summary>读一个字符串属性（BSTR）；拿不到返回 false。</summary>
    public static bool TryGetString(IntPtr obj, string name, out string value)
    {
        value = "";
        if (obj == IntPtr.Zero || GetId(obj, name, out int id) != 0) return false;
        if (Invoke(obj, id, DISPATCH_PROPERTYGET, IntPtr.Zero, 0, out var v) != 0) return false;
        if (v.vt != VT_BSTR || v.ptr == IntPtr.Zero) { Clear(ref v); return false; }
        value = Marshal.PtrToStringUni(v.ptr) ?? "";
        Marshal.FreeBSTR(v.ptr);
        return true;
    }

    /// <summary>读一个对象属性（VT_DISPATCH/VT_UNKNOWN）。**返回的引用归调用者，须 Release。**</summary>
    public static IntPtr GetObject(IntPtr obj, string name)
    {
        if (obj == IntPtr.Zero || GetId(obj, name, out int id) != 0) return IntPtr.Zero;
        if (Invoke(obj, id, DISPATCH_PROPERTYGET, IntPtr.Zero, 0, out var v) != 0) return IntPtr.Zero;
        if (v.vt == VT_DISPATCH || v.vt == VT_UNKNOWN) return v.ptr;   // 所有权转移，不清 Variant
        Clear(ref v);
        return IntPtr.Zero;
    }

    /// <summary>调一个 0 参方法（Next / Previous / Exit）。</summary>
    public static bool Invoke0(IntPtr obj, string name)
    {
        if (obj == IntPtr.Zero || GetId(obj, name, out int id) != 0) return false;
        int hr = Invoke(obj, id, DISPATCH_METHOD, IntPtr.Zero, 0, out var v);
        D($"Invoke0(\"{name}\") hr=0x{hr:X8} vt={v.vt}");
        Clear(ref v);
        if (hr != 0) return false;
        return true;
    }

    /// <summary>调一个带 1 个 int 参数的方法（GotoSlide）。</summary>
    public static bool Invoke1Int(IntPtr obj, string name, int arg)
    {
        if (obj == IntPtr.Zero || GetId(obj, name, out int id) != 0) return false;
        Variant a = default; a.vt = VT_I4; a.lVal = arg;
        int hr = Invoke(obj, id, DISPATCH_METHOD, (IntPtr)(&a), 1, out var v);
        D($"Invoke1(\"{name}\", {arg}) hr=0x{hr:X8} vt={v.vt}");
        Clear(ref v);
        if (hr != 0) return false;
        return true;
    }

    // ---- 建实例 + 带 BSTR 的多参调用（2026-10-09，PPT 直映用）------------------------
    //
    // 原来的用法是"ROT 里找个已经开着的 PPT 连上去读状态"；直映要反过来：
    // **从零把 WPS 的自动化服务器叫起来**（CoCreateInstance）再下命令。

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    private const uint CLSCTX_LOCAL_SERVER = 4;

    /// <summary>按 ProgId 起一个**进程外**自动化实例，直接要 IDispatch。失败一律静默返回 false。</summary>
    public static bool CreateFromProgId(string progId, out IntPtr disp)
    {
        disp = IntPtr.Zero;
        EnsureCom();
        if (CLSIDFromProgID(progId, out var clsid) != 0)
        {
            D($"CLSIDFromProgID(\"{progId}\") 失败");
            return false;
        }
        var iid = IID_IDispatch;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out var p);
        D($"CoCreateInstance(\"{progId}\") hr=0x{hr:X8}");
        if (hr != 0) return false;
        disp = p;
        return true;
    }

    private static Variant BoolVar(bool b)
    {
        Variant v = default;
        v.vt = VT_BOOL;
        v.bVal = b ? (short)-1 : (short)0;
        return v;
    }

    /// <summary>
    /// 调 `Presentations.Open(File, ReadOnly, Untitled, WithWindow)`——4 个参数全给
    /// （WithWindow 现固定给"是"：WPS 的"无窗打开"会被前台切换弄坏，见 PptLaunch 头注释）。
    ///
    /// ⚠ IDispatch 的参数按**倒序**排进 rgvarg（第 4 个参数排最前），别排反。
    /// 成功返回文稿的 IDispatch*（**引用归调用者**，须 Release）；失败返回 0。
    /// </summary>
    public static IntPtr OpenPresentation(IntPtr presentations, string file, bool readOnly, bool untitled, bool withWindow)
    {
        if (presentations == IntPtr.Zero || GetId(presentations, "Open", out int id) != 0) return IntPtr.Zero;
        IntPtr bstr = Marshal.StringToBSTR(file);
        try
        {
            Variant* args = stackalloc Variant[4];
            args[0] = BoolVar(withWindow);          // 第 4 个参数
            args[1] = BoolVar(untitled);            // 第 3 个
            args[2] = BoolVar(readOnly);            // 第 2 个
            args[3] = default;
            args[3].vt = VT_BSTR;                   // 第 1 个
            args[3].ptr = bstr;
            int hr = Invoke(presentations, id, DISPATCH_METHOD, (IntPtr)args, 4, out var v);
            D($"Open(\"{System.IO.Path.GetFileName(file)}\") hr=0x{hr:X8} vt={v.vt}");
            if (hr == 0 && (v.vt == VT_DISPATCH || v.vt == VT_UNKNOWN)) return v.ptr;   // 所有权转移
            Clear(ref v);
            return IntPtr.Zero;
        }
        finally { Marshal.FreeBSTR(bstr); }
    }
}
