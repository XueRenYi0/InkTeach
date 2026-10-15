// InkDesktopHost.cs —— InkDesktopHost（桌面墨迹宿主，Win10 1511+）互操作 + 线程封装。
//
// 为什么有它：系统托管湿墨原来走 CoreInkPresenterHost——**Win10 上实测坏的**（教室机症状：
// 手写板/出墨不可用）；InkDesktopHost 是同一件事的"桌面正门"（微软 2026 年的 WinUI 3
// InkCanvas 内部用的也是它），Win10 1511+ 全系可用。本机双端探针实测（2026-10-14/15）：
//   Win11 26300 真机与 Win10 19045（VM）全绿——SetRootVisual hr=0、出墨（红像素 3829）、
//   UWP 面（样式/输入过滤/干笔数据）全通。探针工程留档：_scratch/InkDesktopProbe。
//
// 关键接法（官方 microsoft-ui-xaml/controls/dev/InkCanvas/InkPresenter.cpp 逐条对照）：
//   ① CoCreateInstance(CLSID_InkDesktopHost) 必须在**主线程**执行
//      （实测：在墨迹线程里 CoCreate 直接被拒）；
//   ② CreateInkPresenter / SetRootVisual / SetCommitRequestHandler / SetSize 等
//      全部经 QueueWorkItem 投到**墨迹线程**执行——直呼 = RPC_E_WRONG_THREAD(0x8001010E)
//      （另一台电脑 2026-10-12 的探针就是直呼 CreateAndInitializeInkPresenter 踩这个坑，
//       判了"不可用"；正确姿势见官方源码 + 本机探针）；
//   ③ SetRootVisual 传**原生指针**（IDCompositionVisual* + IDCompositionDevice*）：
//      Vortice 是 SharpGen 包装（不是 RCW），指针在 NativePointer 属性上；用
//      Marshal.GetIUnknownForObject 会拿到 CCW 代理 → E_NOINTERFACE(0x80004002)。
//
// 分工：本文件只管"宿主对象 + 墨迹线程封送 + presenter 双面 + commit 回调"；
// "怎么用 presenter"（旁听喂模型、交接、输入策略）全部留在 SystemWetHost 一字不动。
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.UI.Input.Inking;

namespace InkEngine;

internal sealed class InkDesktopHostImpl
{
    // ---- GUID（inkpresenterdesktop.h 官方 SDK 逐字对照）----
    static readonly Guid CLSID_InkDesktopHost = new("062584a6-f830-4bdc-a4d2-0a10ab062b1d");
    static readonly Guid IID_IInkDesktopHost = new("4ce7d875-a981-4140-a1ff-ad93258e8d59");
    static readonly Guid IID_IInkPresenterDesktop = new("73f3c0d9-2e8b-48f3-895e-20cbd27b723b");

    // ---- COM 面（顺序 = 探针实测跑通的 vtable）----

    [ComImport, Guid("4ce7d875-a981-4140-a1ff-ad93258e8d59"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInkDesktopHost
    {
        [PreserveSig] int QueueWorkItem(IInkHostWorkItem workItem);   // 投到墨迹线程执行
        [PreserveSig] int CreateInkPresenter(ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CreateAndInitializeInkPresenter(IntPtr rootVisual, float width, float height, ref Guid riid, out IntPtr ppv);
    }

    // IInkHostWorkItem（ccda0a9a-…）：在 InkDesktopHost 线程上执行的墨迹操作
    // （官方 InkPresenter.cpp 的 GenericInkCallback 同款用法：presenter 的创建/变更都经它投递）
    [ComImport, Guid("ccda0a9a-1b78-4632-bb96-97800662e26c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInkHostWorkItem
    {
        [PreserveSig] int Invoke();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class GenericInkCallback : IInkHostWorkItem
    {
        public Action Run;
        public int Invoke()
        {
            try { Run?.Invoke(); }
            catch (Exception ex) { Console.WriteLine("[IDH workitem] 异常: " + ex.Message); }
            return 0;
        }
    }

    [ComImport, Guid("73f3c0d9-2e8b-48f3-895e-20cbd27b723b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInkPresenterDesktop
    {
        [PreserveSig] int SetRootVisual(IntPtr rootVisual, IntPtr device);
        [PreserveSig] int SetCommitRequestHandler(IInkCommitRequestHandler handler);
        [PreserveSig] int GetSize(out float width, out float height);
        [PreserveSig] int SetSize(float width, float height);
        [PreserveSig] int OnHighContrastChanged();
    }

    [ComImport, Guid("fabea3fc-b108-45b6-a9fc-8d08fa9f85cf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInkCommitRequestHandler
    {
        [PreserveSig] int OnCommitRequested();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class CommitHandler : IInkCommitRequestHandler
    {
        public Action OnCall;
        public int OnCommitRequested()
        {
            try { OnCall?.Invoke(); } catch { }
            return 0;
        }
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    // ---- 实例状态 ----
    IInkDesktopHost _host;
    CommitHandler _commitKeepAlive;
    int _inkThreadId = -1;
    public bool Failed { get; private set; }

    /// <summary>COM 面（SetRootVisual / SetSize / SetCommitRequestHandler 在这面上）。</summary>
    public IInkPresenterDesktop Desktop { get; private set; }
    /// <summary>UWP 面（绘制属性 / 输入过滤 / 湿墨事件 / 自定义干燥全在这面上——与 CM 同源）。</summary>
    public InkPresenter Presenter { get; private set; }

    /// <summary>墨迹线程 tid（第一条 work item 执行时记录；之后用它判"已在墨迹线程上"）。</summary>
    public int InkThreadId => _inkThreadId;

    /// <summary>主线程调：建宿主对象（CoCreateInstance 必须在主线程——实测）。失败抛。</summary>
    public static InkDesktopHostImpl CreateOnCallingThread()
    {
        var impl = new InkDesktopHostImpl();
        var clsid = CLSID_InkDesktopHost;
        var iid = IID_IInkDesktopHost;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out IntPtr pHost);
        if (hr < 0 || pHost == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005));
        }
        try { impl._host = (IInkDesktopHost)Marshal.GetObjectForIUnknown(pHost); }
        finally { Marshal.Release(pHost); }
        return impl;
    }

    /// <summary>进程级探测：这台机器建不建得起（只 CoCreate，不碰 presenter）。</summary>
    public static bool Probe(out string note)
    {
        try
        {
            var h = CreateOnCallingThread();
            h.Dispose();
            note = "可用（InkDesktopHost，Win10 1511+）";
            return true;
        }
        catch (Exception ex)
        {
            note = "不可用：" + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 在**墨迹线程**上执行一个动作并等待完成（主线程调）。已经在墨迹线程上则直接执行（防自锁）。
    /// 返回：true=已执行；false=投递失败/超时。动作里抛的异常会带回调用方。
    /// </summary>
    public bool Run(Action act, int timeoutMs = 4000)
    {
        if (Failed || _host == null) return false;
        if (_inkThreadId != -1 && (int)Native.GetCurrentThreadId() == _inkThreadId) { act(); return true; }
        var done = new ManualResetEventSlim(false);
        Exception err = null;
        var item = new GenericInkCallback
        {
            Run = () =>
            {
                _inkThreadId = (int)Native.GetCurrentThreadId();
                try { act(); }
                catch (Exception ex) { err = ex; }
                finally { done.Set(); }
            }
        };
        if (_host.QueueWorkItem(item) < 0) return false;
        if (!done.Wait(timeoutMs)) return false;
        if (err != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(err).Throw();
        return true;
    }

    /// <summary>同一个墨迹线程上执行并取回一个值（主线程调）。失败/超时返回 fallback。</summary>
    public T RunT<T>(Func<T> f, T fallback, int timeoutMs = 4000)
    {
        T r = fallback;
        bool ok = Run(() => { r = f(); }, timeoutMs);
        return ok ? r : fallback;
    }

    /// <summary>墨迹线程上：建 presenter（COM 面 + UWP 面）。必须在 Run 里执行。</summary>
    public void CreatePresenterOnInkThread()
    {
        var iid = IID_IInkPresenterDesktop;
        IntPtr pPr = IntPtr.Zero;
        int hr = _host.CreateInkPresenter(ref iid, out pPr);
        if (hr < 0 || pPr == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005));
        }
        // 两个面共用同一个 COM 身份（探针同款）：COM 面走 RCW、UWP 面走 CsWinRT。
        // 引用计数不做精细归还——宿主随进程存活，Dispose 只断外壳。
        Desktop = (IInkPresenterDesktop)Marshal.GetObjectForIUnknown(pPr);
        Presenter = WinRT.MarshalInspectable<InkPresenter>.FromAbi(pPr);
    }

    /// <summary>墨迹线程上：接 commit 回调（系统要求提交时由我们 Commit 合成设备）。</summary>
    public int AttachCommitHandler(Action onCommit)
    {
        _commitKeepAlive = new CommitHandler { OnCall = onCommit };
        return Desktop.SetCommitRequestHandler(_commitKeepAlive);
    }

    public void Dispose()
    {
        Failed = true;
        Desktop = null;
        Presenter = null;
        _commitKeepAlive = null;
        _host = null;
    }
}
