// =====================================================================================
//  PPT 放映联动：真实 COM 源（AOT 安全版）
//
//  2026-10-05 从 PptLink.cs 拆出来，为 NativeAOT 改造：
//    · 去掉 `dynamic`（运行时绑定需要动态代码生成）
//    · 去掉 System.Runtime.InteropServices.ComTypes（内置 COM 的 ROT/moniker 接口）
//    · 去掉 Type.InvokeMember / Marshal.IsComObject / ReleaseComObject
//  全部换成 PptComLate.cs 的原始 vtable 函数指针 + IDispatch 迟绑定。
//
//  行为契约与原版一致：进程名探测 → ROT 扫描挑实例 → 读放映状态；
//  任何调用失败一律静默（没装 Office 的机器上零症状）。
//
//  2026-10-09 真机实测（WPS 放映）：根因在"WPS **无窗**打开"（WithWindow=false）——
//  无窗文稿遇前台切换（如从穿透切回批注）后，连 `ActivePresentation` 都读成空、
//  且本进程内重连也救不回（验证工具/脚本进程退出才恢复）。**根治 = 启动改用有窗
//  打开**（见 PptLaunch）。这里留两层兜底防再犯：①ROT 同优先级优先 WPS 本体
//  （WPS 会把自己的自动化同时注册成 KWPP 与 PowerPoint 兼容两个条目，见 PreferTie）；
//  ②读到空但放映窗还在时自动重连（TryReconnect，平时不该触发）。
// =====================================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InkEngine;

internal sealed class PptComSource : IPptSource
{
    private IntPtr _app;                      // IDispatch*（Application），引用归我们
    private string _appProgId = "";           // 记一下是哪家的（PowerPoint / WPS）
    private long _nextProcessProbeMs;         // 下一次允许"枚举全机进程"的时刻
    private long _nextHealMs;                 // 下一次允许"连接自愈重连"的时刻（见 TryHealConnection）

    /// <summary>`--pptdebug`：把 ROT 扫描每一步打出来（排查连接问题用，平时关）。</summary>
    internal static bool Debug;
    private static void D(string s) { if (Debug) Console.WriteLine("[ppt] " + s); }

    // PowerPoint / WPS 演示 的 ProgID（参考 Inkeys：KWPP/WPP 这些 WPS 别名要一起试）。
    private static readonly string[] ProgIds =
    {
        "PowerPoint.Application",
        "KWPP.Application",
        "Wpp.Application",
        "WPP.Application",
    };

    /// <summary>"没开 PPT 就别碰 COM"——没有目标进程时 ROT 扫描要么抛要么白扫一圈。</summary>
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
        ComLate.EnsureCom();
        if (_app == IntPtr.Zero)
        {
            // 频率纪律和原版一样：全机进程枚举 2 秒最多一次。
            long now = Environment.TickCount64;
            if (now < _nextProcessProbeMs) return default;
            _nextProcessProbeMs = now + 2000;
            if (!AnyProcessRunning()) return default;
        }

        try
        {
            var app = EnsureApp();
            if (app == IntPtr.Zero && TryReconnect()) app = _app;   // ROT 全读空：直接重建连接
            if (app == IntPtr.Zero) return default;
            D("Poll: Application 已连接");

            var pres = ComLate.GetObject(app, "ActivePresentation");
            if (pres == IntPtr.Zero && TryReconnect())
            {
                // WPS 兼容连接的"前台一抢就永久读空"：重连一次就好了（见 TryReconnect）。
                app = _app;
                pres = ComLate.GetObject(app, "ActivePresentation");
            }
            if (pres == IntPtr.Zero) return default;
            D("Poll: ActivePresentation 拿到了");
            try
            {
                ComLate.TryGetString(pres, "Name", out string name);
                ComLate.TryGetString(pres, "FullName", out string full);

                int total = 0;
                var slides = ComLate.GetObject(pres, "Slides");
                if (slides != IntPtr.Zero)
                {
                    ComLate.TryGetInt(slides, "Count", out total);
                    ComLate.Release(slides);
                }

                var snap = new PptSnapshot
                {
                    Connected = true,
                    Key = MakeKey(name, full),
                    Total = total,
                };

                // 放映窗口存在 = 正在全屏放映（非放映时属性为空）。
                var ssw = ComLate.GetObject(pres, "SlideShowWindow");
                if (ssw == IntPtr.Zero) return snap;
                try
                {
                    int slide = 0, slideId = 0;
                    var view = ComLate.GetObject(ssw, "View");
                    if (view != IntPtr.Zero)
                    {
                        ComLate.TryGetInt(view, "CurrentShowPosition", out slide);
                        var slideObj = ComLate.GetObject(view, "Slide");
                        if (slideObj != IntPtr.Zero)
                        {
                            ComLate.TryGetInt(slideObj, "SlideID", out slideId);
                            ComLate.Release(slideObj);
                        }
                        ComLate.Release(view);
                    }
                    return new PptSnapshot
                    {
                        Connected = true, Showing = true, Slide = slide, Total = total,
                        SlideId = slideId, Key = snap.Key,
                    };
                }
                finally { ComLate.Release(ssw); }
            }
            finally { ComLate.Release(pres); }
        }
        catch
        {
            return default;
        }
    }

    public void Next() => InvokeView("Next");
    public void Prev() => InvokeView("Previous");
    public void ExitShow() => InvokeView("Exit");

    /// <summary>跳到第 n 页（进度条松手时用）。越界由 PowerPoint 自己抛，我们吞掉。</summary>
    public void GotoSlide(int slide)
    {
        ComLate.EnsureCom();
        try
        {
            if (_app == IntPtr.Zero || slide < 1) return;
            var pres = ComLate.GetObject(_app, "ActivePresentation");
            if (pres == IntPtr.Zero) return;
            try
            {
                var ssw = ComLate.GetObject(pres, "SlideShowWindow");
                if (ssw == IntPtr.Zero) return;
                try
                {
                    var view = ComLate.GetObject(ssw, "View");
                    if (view == IntPtr.Zero) return;
                    try { ComLate.Invoke1Int(view, "GotoSlide", slide); }
                    finally { ComLate.Release(view); }
                }
                finally { ComLate.Release(ssw); }
            }
            finally { ComLate.Release(pres); }
        }
        catch { }
    }

    /// <summary>控制放映视图：Next / Previous / Exit（抛错不带走轮询线程）。</summary>
    private void InvokeView(string what)
    {
        ComLate.EnsureCom();
        try
        {
            if (_app == IntPtr.Zero) { D($"InvokeView({what}): app 空"); return; }
            var pres = ComLate.GetObject(_app, "ActivePresentation");
            if (pres == IntPtr.Zero) { D($"InvokeView({what}): pres 空"); return; }
            try
            {
                var ssw = ComLate.GetObject(pres, "SlideShowWindow");
                if (ssw == IntPtr.Zero) { D($"InvokeView({what}): ssw 空"); return; }
                try
                {
                    var view = ComLate.GetObject(ssw, "View");
                    if (view == IntPtr.Zero) { D($"InvokeView({what}): view 空"); return; }
                    try { D($"InvokeView({what}): 调用"); ComLate.Invoke0(view, what); }
                    finally { ComLate.Release(view); }
                }
                finally { ComLate.Release(ssw); }
            }
            finally { ComLate.Release(pres); }
        }
        catch { }
    }

    /// <summary>拿到 PowerPoint.Application（缓存；缓存失效时重新扫 ROT）。</summary>
    private IntPtr EnsureApp()
    {
        if (_app != IntPtr.Zero)
        {
            // 探活：随便读一个便宜属性。
            if (ComLate.TryGetString(_app, "Name", out _)) return _app;
            ComLate.Release(_app);
            _app = IntPtr.Zero;
        }

        _app = RotFindApplication(out _appProgId);
        return _app;
    }

    // =====================================================================
    //  连接自愈（2026-10-09 真机实测所加，见文件头）
    //
    //  症状：WPS 无窗放映期间前台被抢走一次（如从穿透切回批注），老连接上
    //  `ActivePresentation` 永久读空——放映还在、条却不回来；且此时**新连接也被
    //  读空**，直到把启动时抱着的 WPS 引用放掉才恢复（实测）。
    //
    //  所以重连做三件事：①放掉 PptLaunch 抱着的引用；②扔了旧连接；
    //  ③按 KWPP 优先重建（按"进程在跑"过滤，不给纯 Office 机器白拉 WPS）。
    //  只在"放映窗还在屏幕上"时做（真没在放就别白建对象），1 秒最多一次；
    //  **每次尝试都会重来**（就算失败也允许下一次再试——断线期恰恰要反复敲）。
    // =====================================================================
    private bool TryReconnect()
    {
        long now = Environment.TickCount64;
        if (now < _nextHealMs) return false;
        _nextHealMs = now + 1000;

        if (PptLaunch.FindShowWindow(out _) == IntPtr.Zero) return false;   // 放映窗都不在 = 真没在放
        D("连接失效但放映窗还在：重连");

        PptLaunch.ReleaseHeldRefs();
        ComLate.Release(_app);
        _app = IntPtr.Zero;
        _appProgId = "";

        if (ProcRunning("wpp") && TryAttach("KWPP.Application")) return true;
        if (ProcRunning("POWERPNT") && TryAttach("PowerPoint.Application")) return true;
        return false;
    }

    /// <summary>重连一家：建对象 + 试读 ActivePresentation；读得到才认（引用留在 `_app`）。</summary>
    private bool TryAttach(string progId)
    {
        if (!ComLate.CreateFromProgId(progId, out var a) || a == IntPtr.Zero) return false;
        var chk = ComLate.GetObject(a, "ActivePresentation");
        if (chk != IntPtr.Zero)
        {
            ComLate.Release(chk);
            _app = a;
            _appProgId = progId;
            D($"重连成功：{progId}");
            return true;
        }
        ComLate.Release(a);
        return false;
    }

    private static bool ProcRunning(string name)
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// ROT 同优先级的 tie-break：**优先 WPS 本体（KWPP/WPP）**——别选
    /// PowerPoint 兼容劫持那个，它在前台被抢一次后会永久读空（见文件头）。
    /// </summary>
    private static bool PreferTie(string newPid, string curPid)
    {
        if (string.IsNullOrEmpty(newPid) || string.IsNullOrEmpty(curPid)) return false;
        static bool IsWpsNative(string p) =>
            p.StartsWith("KWPP", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("WPP", StringComparison.OrdinalIgnoreCase);
        return IsWpsNative(newPid) && !IsWpsNative(curPid);
    }

    // -------------------------------------------------------------------------------
    //  ROT 扫描（和原版同一套优先级：ActivePresentation=1 ＜ 有 SlideShowWindow=2
    //  ＜ 放映窗口是前台=3）。所有接口指针手工 Release。
    // -------------------------------------------------------------------------------

    private static unsafe IntPtr RotFindApplication(out string progId)
    {
        progId = "";
        IntPtr best = IntPtr.Zero;
        int bestPriority = 0;

        if (ComLate.GetRot(out var rot) != 0 || rot == IntPtr.Zero) return IntPtr.Zero;
        D($"GetRot ok rot={rot != IntPtr.Zero}");
        try
        {
            if (ComLate.RotEnumRunning(rot, out var penum) != 0 || penum == IntPtr.Zero) return IntPtr.Zero;
            D($"EnumRunning ok enum={penum != IntPtr.Zero}");
            try
            {
                IntPtr moniker = IntPtr.Zero;
                while (ComLate.EnumNext(penum, &moniker, out uint fetched) == 0 && fetched == 1)
                {
                    IntPtr bindCtx = IntPtr.Zero, com = IntPtr.Zero;
                    try
                    {
                        if (ComLate.CreateBindCtx(out bindCtx) != 0) { D("  CreateBindCtx 失败"); continue; }
                        string display = "";
                        int dhr = ComLate.GetDisplayName(moniker, bindCtx, out var psz);
                        if (dhr == 0 && psz != IntPtr.Zero)
                        {
                            display = Marshal.PtrToStringUni(psz) ?? "";
                            Marshal.FreeCoTaskMem(psz);
                        }
                        D($"  GetDisplayName hr=0x{dhr:X8} display=\"{display}\"");
                        if (display.Length == 0) continue;

                        bool isApp = IsAppMoniker(display, out string pid);
                        bool isFile = LooksLikePresentation(display);
                        D($"  moniker \"{display}\" app={isApp} file={isFile}");
                        if (!isApp && !isFile) continue;

                        if (ComLate.RotGetObject(rot, moniker, out com) != 0 || com == IntPtr.Zero) continue;

                        IntPtr app;
                        var disp = ComLate.QiDispatch(com);      // ROT 里给的是 IUnknown
                        if (disp == IntPtr.Zero) { D("    QI IDispatch 失败"); continue; }
                        D("    取到对象 + QI IDispatch");
                        if (isApp)
                        {
                            app = disp;
                        }
                        else
                        {
                            // 文件 moniker：从 Presentation 反查 Application。
                            app = ComLate.GetObject(disp, "Application");
                            ComLate.Release(disp);
                        }
                        if (app == IntPtr.Zero) continue;

                        int priority = PriorityOf(app);
                        D($"    优先级={priority}");
                        if (priority > bestPriority || (priority == bestPriority && PreferTie(pid, progId)))
                        {
                            if (best != IntPtr.Zero) ComLate.Release(best);
                            best = app;
                            bestPriority = priority;
                            progId = pid ?? "";
                        }
                        else
                        {
                            ComLate.Release(app);
                        }
                    }
                    catch { }
                    finally
                    {
                        if (com != IntPtr.Zero) ComLate.Release(com);
                        if (bindCtx != IntPtr.Zero) ComLate.Release(bindCtx);
                        if (moniker != IntPtr.Zero) ComLate.Release(moniker);
                        moniker = IntPtr.Zero;
                    }
                }
            }
            finally { ComLate.Release(penum); }
        }
        finally { ComLate.Release(rot); }
        D($"扫描结束: bestPriority={bestPriority} progId=\"{progId}\"");
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
    private static int PriorityOf(IntPtr app)
    {
        try
        {
            var pres = ComLate.GetObject(app, "ActivePresentation");
            if (pres == IntPtr.Zero) return 0;
            try
            {
                var ssw = ComLate.GetObject(pres, "SlideShowWindow");
                if (ssw == IntPtr.Zero) return 1;
                try
                {
                    if (ComLate.TryGetInt(ssw, "HWND", out int hwnd) && hwnd != 0
                        && Native.GetForegroundWindow() == new IntPtr(hwnd))
                        return 3;
                    return 2;
                }
                finally { ComLate.Release(ssw); }
            }
            finally { ComLate.Release(pres); }
        }
        catch { return 0; }
    }

    /// <summary>
    /// 演示文稿的稳定标识 = 名字 + 完整路径的哈希（不带页数：页数会变，插一页就换目录）。
    /// </summary>
    private static string MakeKey(string name, string full)
    {
        string basis = string.IsNullOrEmpty(full) ? name : full;
        uint h = 2166136261u;
        foreach (char c in basis) { h ^= (byte)c; h *= 16777619u; }   // FNV-1a，够用就行
        return $"{name}_{h:x8}";
    }

    // ---- 只留一个纯 Win32 的 P/Invoke（CLSID 查询是 blittable 的，AOT 直接支持）----

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);
}
