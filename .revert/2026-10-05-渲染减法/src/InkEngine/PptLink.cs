using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace InkEngine;

// =====================================================================================
//  PPT 放映联动：连接层
//
//  参考出处（用户 2026-09-26 定的"站在巨人的肩膀上"最高原则，先查后写）：
//    · **Inkeys / 智绘教**（GPLv3，https://github.com/Alan-CRL/Inkeys，本机
//      `画布测试\Inkeys-20260713a\智绘教\PptCOM\PptCOM.cs`）：本文件的连接骨架
//      ——ROT（运行对象表）扫描、多 PPT 实例按优先级挑、WPS 的 ProgID 特判、
//      "忙"重试、COM 对象逐个释放，全部照它改写；它的形态是"独立 COM 服务进程 +
//      共享内存指针"，我们这里是**进程内模块**（省掉进程间那层，逻辑不变）。
//    · **Ink Canvas Ultra**（GPLv3，`MW_PPT.cs`）：进程名探测（没开 PPT 就别去碰
//      COM）、读失败一律静默——避免"没装 Office 的机器上每秒抛一个 COMException"。
//    · **InkCanvasForClass 社区版**（GPLv3，GitHub 上能找到的最新实现，`PPTManager`
//      / `PPTROTConnectionHelper`）：把"连接"抽成接口、换实现不改调用方（策略模式）
//      ——我们用它的接口形状，自检时注入假源。
//
//  与它们的最大不同（我们的适配，写在 Ppt.cs 里）：**页的所有者永远是我们**。
//  这里只回答"现在是不是在放映、第几页、那一页的 SlideID 是多少"，绝不碰文档。
// =====================================================================================

/// <summary>
/// PPT 放映状态的一份快照。**纯托管值**（不含 COM 对象），可以安全地跨线程传——
/// 这是刻意的：COM 对象有线程亲和性，跨线程用会抛 RPC_E_WRONG_THREAD
/// （Ultra 的注释里记着这个坑，我们不让 COM 对象离开轮询线程）。
/// </summary>
internal readonly struct PptSnapshot
{
    /// <summary>连上了 PowerPoint / WPS（有打开的演示文稿）。</summary>
    public bool Connected { get; init; }
    /// <summary>正在放映（全屏放映窗口存在）。</summary>
    public bool Showing { get; init; }
    /// <summary>当前是第几页（1 起）。</summary>
    public int Slide { get; init; }
    /// <summary>一共几页。</summary>
    public int Total { get; init; }
    /// <summary>当前页的 SlideID（PowerPoint 给每张幻灯片的一个稳定编号）。</summary>
    public int SlideId { get; init; }
    /// <summary>演示文稿的稳定标识（名字 + 完整路径的哈希），用来当存盘的目录名。</summary>
    public string Key { get; init; }

    /// <summary>两个快照在"对我们的状态机有意义"的维度上相等吗。</summary>
    public bool SameAs(in PptSnapshot o) =>
        Connected == o.Connected && Showing == o.Showing && Slide == o.Slide &&
        Total == o.Total && SlideId == o.SlideId && Key == o.Key;
}

/// <summary>
/// 页码源：只回答"放映状态是什么"，外加三个操作 PPT 的命令。
/// **自检注入假源**（<see cref="PptFakeSource"/>），产品用 COM 源
/// （<see cref="PptComSource"/>）——测量用的通路和产品用的通路是同一条。
/// </summary>
internal interface IPptSource
{
    PptSnapshot Poll();
    void Next();
    void Prev();
    /// <summary>跳到第 <paramref name="slide"/> 页（1 起）——底部那条进度条松手时用。</summary>
    void GotoSlide(int slide);
    void ExitShow();
}

/// <summary>
/// 轮询线程：**所有 COM 调用都在这一条线程上**（包括 Next/Prev/Exit 命令——
/// 它们从主线程入队、由这里执行）。为什么：COM 对象有公寓（Apartment）亲和性，
/// 让同一条后台线程包办"读状态"和"发命令"，就不存在跨线程 marshalling 的问题。
///
/// 状态一变就 `PostThreadMessage` 唤醒主循环（消息泵本来就在主线程队列上），
/// 主循环 `StepPpt()` 读快照、切页。**轮询间隔 200ms**：翻页的延迟上限就是它。
/// </summary>
internal sealed class PptWatcher
{
    /// <summary>自定义线程消息：PPT 状态变了（主循环在 DrainMessages 里认它）。</summary>
    public const uint WakeMessage = 0x8000 + 0x51;     // WM_APP + 0x51

    private readonly IPptSource _src;
    private readonly uint _wakeThreadId;
    private readonly int _intervalMs;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<(byte cmd, int arg)> _cmds = new();   // 0=下一页 1=上一页 2=结束放映 3=跳到第 n 页
    private readonly object _sync = new();
    private PptSnapshot _snap;
    private bool _dirty;
    private volatile bool _stop;

    /// <summary>这轮轮询真的跑了几次（自检用：证明线程活着、不是"设了就忘了"）。</summary>
    public int PollCount;

    public PptWatcher(IPptSource src, uint wakeThreadId, int intervalMs = 200)
    {
        _src = src;
        _wakeThreadId = wakeThreadId;
        _intervalMs = intervalMs;
        _thread = new Thread(Run) { IsBackground = true, Name = "PptWatch" };
    }

    public void Start() => _thread.Start();
    public void Stop() { _stop = true; _wake.Set(); }

    public PptSnapshot Snapshot() { lock (_sync) return _snap; }

    /// <summary>有新快照吗（取走就清）。主循环每帧问一次，**没变化时一个锁都不拿**。</summary>
    public bool TakeDirty() { lock (_sync) { if (!_dirty) return false; _dirty = false; return true; } }

    /// <summary>主线程排一条命令，由轮询线程执行（见类注释）。</summary>
    public void Post(byte cmd, int arg = 0) { _cmds.Enqueue((cmd, arg)); _wake.Set(); }

    private void Run()
    {
        while (!_stop)
        {
            // ① 先执行命令（按钮点下去最多等一个醒来周期，而不是一个轮询周期）
            while (_cmds.TryDequeue(out var c))
            {
                try
                {
                    if (c.cmd == 0) _src.Next();
                    else if (c.cmd == 1) _src.Prev();
                    else if (c.cmd == 2) _src.ExitShow();
                    else if (c.cmd == 3) _src.GotoSlide(c.arg);
                }
                catch { /* 命令失败不许把轮询线程带走 */ }
            }

            // ② 读一次状态；变了就存快照 + 唤醒主循环
            try
            {
                var s = _src.Poll();
                PollCount++;
                bool changed;
                lock (_sync)
                {
                    changed = !s.SameAs(_snap);
                    _snap = s;
                    if (changed) _dirty = true;
                }
                if (changed && _wakeThreadId != 0)
                    Native.PostThreadMessage(_wakeThreadId, WakeMessage, IntPtr.Zero, IntPtr.Zero);
            }
            catch { /* 读失败当"没连上"处理：下一次轮询再试 */ }

            _wake.WaitOne(_intervalMs);
        }
    }
}

/// <summary>
/// 自检用的假源：状态由测试直接写，命令只改自己的页码，不碰任何外部程序。
/// `--ppttest` 走的就是这条（我这边没有 PowerPoint 环境，产品那条路要用户在
/// 装了 PPT / WPS 的机器上用 `--pptprobe` 确认）。
/// </summary>
internal sealed class PptFakeSource : IPptSource
{
    public bool Connected = true;
    public bool Showing;
    public int Slide = 1;
    public int Total = 3;
    public int SlideId = 256;
    public string Key = "假演示文稿";

    /// <summary>自检用：翻页/跳页命令真的被调到了几次（证明按钮那条路通了）。</summary>
    public int NextCalls, PrevCalls, ExitCalls, GotoCalls;
    /// <summary>自检用：最近一次跳页跳到了第几页。</summary>
    public int GotoTarget;

    public PptSnapshot Poll() => new()
    {
        Connected = Connected, Showing = Showing, Slide = Slide, Total = Total,
        SlideId = SlideId, Key = Key,
    };

    public void Next() { NextCalls++; if (Slide < Total) { Slide++; SlideId += 1; } }
    public void Prev() { PrevCalls++; if (Slide > 1) { Slide--; SlideId -= 1; } }
    public void ExitShow() { ExitCalls++; Showing = false; }

    public void GotoSlide(int slide)
    {
        GotoCalls++;
        GotoTarget = slide;
        int want = Math.Clamp(slide, 1, Math.Max(1, Total));
        SlideId += want - Slide;      // 保持"SlideId 跟着页码走"（和 Next/Prev 同一条规则）
        Slide = want;
    }
}

/// <summary>
/// 真实的 COM 源：进程名探测 → ROT 扫描找 PowerPoint/WPS 实例 → 读放映状态。
/// 全部调用失败一律静默（返回"没连上"），**没装 Office 的机器上不该有任何症状**。
/// </summary>
internal sealed class PptComSource : IPptSource
{
    private object _app;                      // PowerPoint.Application（dynamic 用）
    private string _appProgId = "";           // 记一下是哪家的（PowerPoint / WPS）
    private long _nextProcessProbeMs;         // 下一次允许"枚举全机进程"的时刻

    // PowerPoint / WPS 演示 的 ProgID。**参考 Inkeys**：它连 KWPP/WPP 这些
    // WPS 的别名一起试，不然国产办公套件里这个功能就是死的。
    private static readonly string[] ProgIds =
    {
        "PowerPoint.Application",
        "KWPP.Application",
        "Wpp.Application",
        "WPP.Application",
    };

    /// <summary>"没开 PPT 就别碰 COM"——参考 Ultra：GetActiveObject/ROT 在
    /// 没有目标进程时要么抛异常要么白扫一圈，两者都贵。</summary>
    private static bool AnyProcessRunning()
    {
        try
        {
            if (Process.GetProcessesByName("POWERPNT").Length > 0) return true;
            if (Process.GetProcessesByName("wpp").Length > 0) return true;
        }
        catch { }
        return false;
    }

    public PptSnapshot Poll()
    {
        if (_app == null)
        {
            // **注意这一层的频率**：`Process.GetProcessesByName` 要枚举全机进程，
            // 而轮询线程 5 次/秒地跑它、只为回答"老师这会儿开 PPT 了吗"，
            // 在没装 Office 的机器上是白烧 CPU。2 秒探一次足够——老师从打开 PPT
            // 到按 F5 中间本来就有好几秒；真在放映时走的是下面那条（不经过这里）。
            long now = Environment.TickCount64;
            if (now < _nextProcessProbeMs) return default;
            _nextProcessProbeMs = now + 2000;

            if (!AnyProcessRunning())
            {
                _app = null;
                return default;
            }
        }

        try
        {
            var app = EnsureApp();
            if (app == null) return default;

            dynamic dApp = app;
            object pres;
            try { pres = dApp.ActivePresentation; } catch { return default; }
            if (pres == null) return default;

            dynamic dPres = pres;
            string name = "";
            string full = "";
            try { name = (string)dPres.Name; } catch { }
            try { full = (string)dPres.FullName; } catch { }

            int total = 0;
            try { total = (int)dPres.Slides.Count; } catch { }

            var snap = new PptSnapshot
            {
                Connected = true,
                Key = MakeKey(name, full),
                Total = total,
            };

            // 放映窗口存在 = 正在全屏放映（参考：SlideShowWindow 属性在非放映时抛异常/为 null）
            object ssw = null;
            try { ssw = dPres.SlideShowWindow; } catch { }
            if (ssw == null) return snap;

            dynamic dSsw = ssw;
            int slide = 0, slideId = 0;
            try { slide = (int)dSsw.View.CurrentShowPosition; } catch { }
            try { slideId = (int)dSsw.View.Slide.SlideID; } catch { }

            return new PptSnapshot
            {
                Connected = true, Showing = true, Slide = slide, Total = total,
                SlideId = slideId, Key = snap.Key,
            };
        }
        catch
        {
            return default;
        }
    }

    public void Next() => InvokeView("Next");
    public void Prev() => InvokeView("Previous");
    public void ExitShow() => InvokeView("Exit");

    /// <summary>跳到第 n 页（底部那条进度条松手时用；参考 Inkeys 的
    /// `pptSlideShowWindow.View.GotoSlide` 那条调用）。越界由 PowerPoint 自己夹——
    /// 它会抛，我们照例吞掉（"在第 1 页按上一页"是同一类情况）。</summary>
    public void GotoSlide(int slide)
    {
        try
        {
            if (_app == null || slide < 1) return;
            dynamic dApp = _app;
            object ssw = null;
            try { ssw = dApp.ActivePresentation.SlideShowWindow; } catch { }
            if (ssw == null) return;
            ((dynamic)ssw).View.GotoSlide(slide);
        }
        catch { }
    }

    /// <summary>控制放映视图：Next / Previous / Exit（参考 InkClass 的 BtnPPTSlidesUp/Down，
    /// 只是把"吞掉异常"的理由写明白：在第 1 页按上一页、放映刚结束，这些都会抛，
    /// 让它们冒出去会打断轮询线程）。</summary>
    private void InvokeView(string what)
    {
        try
        {
            if (_app == null) return;
            dynamic dApp = _app;
            object ssw = null;
            try { ssw = dApp.ActivePresentation.SlideShowWindow; } catch { }
            if (ssw == null) return;
            dynamic dView = ((dynamic)ssw).View;
            if (what == "Next") dView.Next();
            else if (what == "Previous") dView.Previous();
            else dView.Exit();
        }
        catch { }
    }

    /// <summary>拿到 PowerPoint.Application（缓存；缓存的对象失效时重新扫一遍）。</summary>
    private object EnsureApp()
    {
        if (_app != null)
        {
            // 探活：随便读一个便宜属性（参考 Ultra 的 TryPingPptApplication）
            try { dynamic d = _app; _ = d.Name; return _app; }
            catch { _app = null; }
        }

        _app = RotFindApplication(out _appProgId);
        return _app;
    }

    // -------------------------------------------------------------------------------
    //  ROT（运行对象表）扫描
    //
    //  为什么必须自己扫：.NET Core / .NET 8 **没有** `Marshal.GetActiveObject`
    //  （那是 .NET Framework 的 API），想在托管代码里"接到一个已经开着的 COM 实例"
    //  只有两条路——① 直接 P/Invoke ROT；② 走 WPS 的私有接口。这里参考 Inkeys
    //  的 ①（它当年也是因为这个才手写的）。
    //
    //  扫出来可能有多个实例（老师开了两个 PPT），**优先级**参考 Inkeys：
    //    有 ActivePresentation = 1 ＜ 有 SlideShowWindow = 2 ＜ 放映窗口是前台 = 3。
    // -------------------------------------------------------------------------------

    private static object RotFindApplication(out string progId)
    {
        progId = "";
        object best = null;
        int bestPriority = 0;

        IRunningObjectTable rot = null;
        IEnumMoniker enumMoniker = null;
        try
        {
            if (GetRunningObjectTable(0, out rot) != 0 || rot == null) return null;
            rot.EnumRunning(out enumMoniker);
            if (enumMoniker == null) return null;

            var monikers = new IMoniker[1];
            IntPtr fetched = IntPtr.Zero;
            while (enumMoniker.Next(1, monikers, fetched) == 0)
            {
                IBindCtx ctx = null;
                object com = null;
                try
                {
                    CreateBindCtx(0, out ctx);
                    string display = "";
                    try { monikers[0].GetDisplayName(ctx, null, out display); } catch { }
                    if (string.IsNullOrEmpty(display)) continue;

                    // 只认两类名字：应用 moniker（!{CLSID}）和演示文稿文件（.pptx…）
                    bool isApp = IsAppMoniker(display, out string pid);
                    bool isFile = LooksLikePresentation(display);
                    if (!isApp && !isFile) continue;

                    if (rot.GetObject(monikers[0], out com) != 0 || com == null) continue;

                    object app = null;
                    if (isApp)
                    {
                        app = com;
                        com = null;               // 所有权转移，交给 app 那条路管
                    }
                    else
                    {
                        // 文件 moniker：从 Presentation 反查 Application
                        try
                        {
                            app = com.GetType().InvokeMember(
                                "Application", System.Reflection.BindingFlags.GetProperty,
                                null, com, null);
                        }
                        catch { }
                    }
                    if (app == null) continue;

                    int priority = PriorityOf(app);
                    if (priority > bestPriority)
                    {
                        bestPriority = priority;
                        best = app;
                        progId = pid ?? "";
                    }
                    else if (!ReferenceEquals(app, best))
                    {
                        Release(app);             // 不是最好的就放掉（参考 Inkeys 的释放纪律）
                    }
                }
                catch { }
                finally
                {
                    if (com != null) Release(com);
                    if (ctx != null) Release(ctx);
                }
            }
        }
        catch { }
        finally
        {
            if (enumMoniker != null) Release(enumMoniker);
            if (rot != null) Release(rot);
        }
        return best;
    }

    /// <summary>这个名字是不是"某个 PPT 应用实例"的 moniker（!{CLSID}）。</summary>
    private static bool IsAppMoniker(string display, out string progId)
    {
        progId = null;
        foreach (var id in ProgIds)
        {
            try
            {
                if (CLSIDFromProgID(id, out Guid clsid) != 0 || clsid == Guid.Empty) continue;
                string want = "!" + clsid.ToString("B").ToUpperInvariant();
                if (string.Equals(display, want, StringComparison.OrdinalIgnoreCase))
                {
                    progId = id;
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>显示器名字像不像一个演示文稿文件（参考 Inkeys 的扩展名清单）。</summary>
    private static bool LooksLikePresentation(string display)
    {
        string[] exts = { ".pptx", ".pptm", ".ppt", ".ppsx", ".ppsm", ".pps", ".potx", ".potm", ".pot", ".dps", ".dpt" };
        string lower = display.ToLowerInvariant();
        foreach (var e in exts) if (lower.Contains(e)) return true;
        return false;
    }

    /// <summary>优先级：能读到 ActivePresentation 记 1；有放映窗口记 2；放映窗口在前台记 3。</summary>
    private static int PriorityOf(object app)
    {
        try
        {
            dynamic d = app;
            object pres = null;
            try { pres = d.ActivePresentation; } catch { }
            if (pres == null) return 0;
            int p = 1;

            object ssw = null;
            try { ssw = ((dynamic)pres).SlideShowWindow; } catch { }
            if (ssw == null) return p;
            p = 2;

            try
            {
                dynamic dSsw = ssw;
                int hwndVal = (int)dSsw.HWND;
                if (hwndVal != 0 && Native.GetForegroundWindow() == new IntPtr(hwndVal)) p = 3;
            }
            catch { }
            return p;
        }
        catch { return 0; }
    }

    private static void Release(object com)
    {
        if (com == null) return;
        try { if (Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }

    /// <summary>
    /// 演示文稿的稳定标识 = 名字 + 完整路径的哈希。参考 ICC 的
    /// `{Name}_{Slides.Count}_{FileHash}` 思路，但**不带页数**——
    /// 页数是会变的（插一页就换目录，批注就找不到了，InkClass 的教训）。
    /// </summary>
    private static string MakeKey(string name, string full)
    {
        string basis = string.IsNullOrEmpty(full) ? name : full;
        uint h = 2166136261u;
        foreach (char c in basis) { h ^= (byte)c; h *= 16777619u; }   // FNV-1a，够用就行
        return $"{name}_{h:x8}";
    }

    // ---- ROT / COM 的 P/Invoke（参考 Inkeys PptCOM.cs 的声明）------------------

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);
}