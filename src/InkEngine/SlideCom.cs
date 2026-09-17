using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// **真的去认 PowerPoint / WPS 的放映**（阶段 2）。
///
/// 三条硬规矩，都写在 调研-对接PPT.md 第三节里：
///   ① **后期绑定**，不引 `Microsoft.Office.Interop.PowerPoint`：
///      编译不需要装 Office，Office 换版本、换 WPS 都不用重编；
///   ② **每一步都包起来**，取不到就返回 false——"没有 PPT"是**正常状态**，
///      不是错误（教室机器上没装 Office 的很常见）；
///   ③ **只读**：这个类绝不翻页、绝不改动别人的演示文稿（翻页那条路在
///      <see cref="Next"/> / <see cref="Previous"/>，只有老师按我们的按钮才会走）。
///
/// 为什么不用 COM 事件（`SlideShowNextSlide` 那些）：见 3.4——老师翻页有三个来源
/// （我们的按钮、键盘、遥控翻页器），轮询一律看得见；挂事件在后期绑定下要走连接点，
/// 而且事件在别的线程回来，InkClass 的注释记着"未捕获的 COM 异常会直接终止整个进程"。
/// </summary>
internal sealed class PowerPointComSource : ISlideSource
{
    public string Name => _appId ?? "（还没接上）";

    /// <summary>
    /// 按顺序试的 ProgID。**微软的在前、WPS 的在后**：
    /// 一台机器上两个都装也有可能，先认微软的（它的 `Slide.SlideID` 一定有，
    /// WPS 的老版本不一定给）。
    /// </summary>
    private static readonly string[] ProgIds =
    {
        "PowerPoint.Application",     // 微软 Office
        "KWPP.Application",           // WPS 演示（新版）
        "wpp.Application",            // WPS 演示（旧版）
    };

    private object _app;
    private string _appId;

    public bool TryGetState(out SlideState state)
    {
        state = default;
        object app = EnsureApp();
        if (app == null) return Fail("没接上应用实例");

        object windows = Prop(app, "SlideShowWindows");
        int n = windows == null ? 0 : ToInt(Prop(windows, "Count"));
        if (windows == null)
        {
            // 读不到集合：分清楚是"它正忙"还是"真的没在放映"（见 IsBusyCode）
            if (_busySeen) { state = new SlideState { Busy = true }; _busySeen = false; return false; }
            return Fail("读 SlideShowWindows 失败");
        }
        object win = n > 0 ? Item(windows, 1) : null;
        if (win == null)
        {
            // 集合索引这条路取不出来时，退到 `ActivePresentation.SlideShowWindow`
            // （两条路的取舍见 ActiveShowWindow 的注释）
            object pres0 = Prop(app, "ActivePresentation");
            win = pres0 == null ? null : Prop(pres0, "SlideShowWindow");
        }
        if (win == null) return false;               // 没在放映（正常状态，不算错）
        object view = Prop(win, "View");
        if (view == null) return Fail("读 SlideShowWindow.View 失败");

        int pos = ToInt(Prop(view, "CurrentShowPosition"));
        if (pos <= 0) return Fail("读 View.CurrentShowPosition 失败");

        object pres = Prop(view, "Presentation");
        if (pres == null) pres = Prop(app, "ActivePresentation");     // 这条实测更稳
        int count = pres != null ? ToInt(Prop(Prop(pres, "Slides"), "Count")) : 0;
        string deck = pres != null ? Prop(pres, "FullName") as string : null;

        // **页身份**：PowerPoint 的 Slide.SlideID。拿不到（老 WPS）就退回页号——
        // 不是最好的答案，但比"认不出来"强；落盘时会记下"这是按页号存的"。
        long slideId = 0;
        object slide = Prop(view, "Slide");
        if (slide != null) slideId = ToLong(Prop(slide, "SlideID"));
        if (slideId == 0) slideId = pos;

        if (string.IsNullOrEmpty(deck)) deck = "(未保存的演示文稿)";
        state = new SlideState
        {
            Showing = true,
            Position = pos,
            Count = count,
            SlideId = slideId,
            DeckKey = deck,
        };
        return true;
    }

    /// <summary>
    /// 读不出来时说一句**哪一步**读不出来（只报一次，不刷屏）。
    /// 这一步很值得写：后台绑定出问题时，"没认出来"和"读到一半失败"在日志里长得一样，
    /// 而两者的修法完全不同。
    /// </summary>
    /// <summary>
    /// 这几个是 COM 的**"我正忙，稍后再试"**（不是错误），抄自 Inkeys：
    /// PowerPoint 在换页动画 / 弹对话框 / 保存时会抛它们。当成"放映结束"是误判。
    /// `0x8001010A` RPC_E_SERVERCALL_RETRYLATER、`0x800AC472` VBA_E_IGNORE、
    /// `0x80010001` RPC_E_CALL_REJECTED。
    /// </summary>
    private static bool IsBusyCode(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is COMException c)
            {
                uint hr = unchecked((uint)c.HResult);
                if (hr == 0x8001010A || hr == 0x800AC472 || hr == 0x80010001) return true;
            }
        return false;
    }

    private static bool Fail(string why)
    {
        if (_lastFail != why)
        {
            _lastFail = why;
            Console.WriteLine($"幻灯片来源：{why}"
                            + (LastError == null ? "" : $"（{LastError}）"));
        }
        return false;
    }

    /// <summary>自检/上层用：刚才那次失败是不是"它正忙"。</summary>
    private static bool LastFailWasBusy;

    private static string _lastFail;
    private static string LastError;

    public bool Next() => ViewCommand("Next");
    public bool Previous() => ViewCommand("Previous");

    /// <summary>借它自己的"幻灯片导航"来跳页（照 InkClass 的思路，省一套页选择器）。</summary>
    public bool ShowNativeNavigator()
    {
        object win = ActiveShowWindow();
        if (win == null) return false;
        object nav = Prop(win, "SlideNavigation");
        if (nav == null) return false;
        return SetProp(nav, "Visible", true);
    }

    /// <summary>翻页：**必须自己 try/catch**（后台线程里未捕获的 COM 异常会杀掉进程）。</summary>
    private bool ViewCommand(string method)
    {
        try
        {
            object win = ActiveShowWindow();
            if (win == null) return false;
            Invoke(win, "Activate", null);           // 先把放映窗口激活（InkClass 同款）
            object view = Prop(win, "View");
            if (view == null) return false;
            Invoke(view, method, null);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"放映翻页失败（{method}）：{Short(ex)}");
            return false;
        }
    }

    private object ActiveShowWindow()
    {
        object app = EnsureApp();
        if (app == null) return null;

        // 两条路都试：① 集合 `SlideShowWindows[1]`；② 走 `ActivePresentation.SlideShowWindow`。
        // **为什么要两条**：实测 PowerPoint 上第一条的**索引**取不出来
        // （`Count` 读得到、`Item(1)` 报 DISP_E_MEMBERNOTFOUND），而第二条在放映中一定有值
        // （探针里 `pres.SlideShowWindow` 当场就是"有"）。Office 的集合在不同版本上
        // 认的访问方式不一样，两条都留着最省事。
        object windows = Prop(app, "SlideShowWindows");
        if (windows != null && ToInt(Prop(windows, "Count")) > 0)
        {
            object w = Item(windows, 1);
            if (w != null) return w;
        }
        object pres = Prop(app, "ActivePresentation");
        if (pres == null) return null;
        return Prop(pres, "SlideShowWindow");
    }

    /// <summary>
    /// 拿到"正在运行的那个应用实例"。**自己绝不启动它**（`GetActiveObject` 只认已经在跑的）。
    /// 拿不到就逐个 ProgID 试一遍，并把结果记住——下次直接用它。
    /// </summary>
    private object EnsureApp()
    {
        if (_app != null)
        {
            // 已经抓到的实例可能已经退出了：问一句，问不动就丢掉重来。
            try { _ = Prop(_app, "Name"); return _app; }
            catch { _app = null; _appId = null; }
        }
        foreach (var id in ProgIds)
        {
            try
            {
                _app = RunningObjectTable.Find(id);
                if (_app != null)
                {
                    // **验证一下它真的能读**：ROT 里拿到的裸包装按名字取成员会
                    // `DISP_E_UNKNOWNNAME`（实测：`Name` 读得到、`SlideShowWindows` 读不到）。
                    // 换带类型信息的包装、再读一个只有 Application 才有的成员来确认。
                    object typed = TryTyped(_app, id);
                    if (typed != null) _app = typed;
                    if (Prop(_app, "SlideShowWindows") != null)
                    {
                        _appId = id;
                        Console.WriteLine($"幻灯片来源：接上了 {id}（运行对象表）");
                        return _app;
                    }
                }

                // 退一步：**只在进程已经在跑的时候**才走这条路。
                // Office 是单实例的：对已经在跑的 PowerPoint 调 CreateInstance 拿到的是
                // **同一个实例**（探针实测这条路读得到 `SlideShowWindows`）。
                // 那句"进程已经在跑"是硬护栏——**我们绝不替老师启动 PowerPoint**。
                if (!ProcessRunning(id)) continue;
                Type t = Type.GetTypeFromProgID(id);
                if (t == null) continue;
                _app = Activator.CreateInstance(t);
                if (_app == null || Prop(_app, "SlideShowWindows") == null) { _app = null; continue; }
                _appId = id;
                Console.WriteLine($"幻灯片来源：接上了 {id}（进程已在运行）");
                return _app;
            }
            catch { }                                 // 没装 / 没在跑 → 试下一个
        }
        return null;
    }

    /// <summary>这个应用是不是**已经在跑**（唯一的用途：决定敢不敢碰它的 COM）。</summary>
    private static bool ProcessRunning(string progId)
    {
        string[] names = progId.StartsWith("PowerPoint") ? new[] { "POWERPNT" }
                       : new[] { "wpp", "wps" };
        foreach (var n in names)
        {
            try { if (System.Diagnostics.Process.GetProcessesByName(n).Length > 0) return true; }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// 把 ROT 里拿到的裸 COM 对象换成**带类型信息**的包装（按 ProgID 找 typelib）。
    ///
    /// 为什么需要这一步：`GetType().InvokeMember(名字…)` 在"没有类型信息"的对象上，
    /// 只能靠 IDispatch 的**名字解析**，而 Office 的某些成员在裸包装下解不出来
    /// （实测报 `0x80020006 DISP_E_UNKNOWNNAME`）。换成按 typelib 绑定的包装之后，
    /// 名字→dispid 由类型库给，稳了。
    /// </summary>
    private static object TryTyped(object comObject, string progId)
    {
        try
        {
            Type t = Type.GetTypeFromProgID(progId);
            if (t == null) return null;
            IntPtr unk = Marshal.GetIUnknownForObject(comObject);
            try { return Marshal.GetTypedObjectForIUnknown(unk, t); }
            finally { Marshal.Release(unk); }
        }
        catch { return null; }
    }

    // ---- 后期绑定的三个小工具（Property / Method / Item）------------------
    // 全部吞掉异常：取不到就是"这次没有"，绝不往上传。

    private static object Prop(object o, string name)
    {
        if (o == null) return null;
        try { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
        catch (Exception ex)
        {
            LastError = $"{name}: {Short(ex)}";
            if (IsBusyCode(ex)) _busySeen = true;      // 稍后再试，不是错
            return null;
        }
    }

    private static bool _busySeen;

    private static bool SetProp(object o, string name, object value)
    {
        if (o == null) return false;
        try
        {
            o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });
            return true;
        }
        catch { return false; }
    }

    private static object Invoke(object o, string name, object[] args)
    {
        if (o == null) return null;
        try { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
        catch (Exception ex) { LastError = $"{name}: {Short(ex)}"; return null; }
    }

    /// <summary>COM 集合取第 i 个（1 起）。</summary>
    private static object Item(object collection, int index)
    {
        if (collection == null) return null;
        // COM 集合取元素有**三种写法**，各家实现认的不一样（Office 自己不同版本也不同）：
        // 名字叫 Item / 叫 _Default / 干脆用默认成员（空名字）。挨个试，都失败才认输。
        foreach (var name in new[] { "Item", "_Default", "" })
        {
            try
            {
                var v = collection.GetType().InvokeMember(name, BindingFlags.GetProperty, null,
                                                           collection, new object[] { index });
                if (v != null) return v;
            }
            catch (Exception ex) { LastError = $"{name}({index}): {Short(ex)}"; }
        }
        return null;
    }

    private static int ToInt(object v) { try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; } }
    private static long ToLong(object v) { try { return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }

    private static string Short(Exception ex)
    {
        // COM 调用失败常常包在 TargetInvocationException 里，里面那句才是真正的原因
        if (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
        return ex is COMException c ? $"0x{c.HResult:X8} {ex.Message}" : $"{ex.GetType().Name}: {ex.Message}";
    }
}

/// <summary>
/// **在运行对象表（ROT）里找已经跑着的那个应用实例**。
///
/// 为什么不用 `Marshal.GetActiveObject`：那是 .NET Framework 的 API，
/// **.NET Core / .NET 5+ 把它删了**（我们跑在 .NET 8 上，编译期直接找不到）。
/// 官方没有替代品，所以这里按老办法自己走一遍 ROT：
///   ① 先按 ProgID 解析出名字（Office 会把自己的 ProgID 注册进 ROT）；
///   ② 不行就**枚举 ROT**，按显示名匹配（Office 常常只登记"打开的那个文件名"）。
///
/// 这是"没有 PPT 就是正常状态"的一部分：找不到就返回 null，上层当没放映。
/// </summary>
internal static class RunningObjectTable
{
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int MkParseDisplayName(IBindCtx ctx, string name, ref int eaten, out IMoniker moniker);

    public static object Find(string progId)
    {
        if (GetRunningObjectTable(0, out var rot) != 0 || rot == null) return null;
        if (CreateBindCtx(0, out var ctx) != 0 || ctx == null) return null;

        // ① 按 ProgID 找（PowerPoint / WPS 都会登记）
        try
        {
            int eaten = 0;
            if (MkParseDisplayName(ctx, progId, ref eaten, out var mon) == 0 && mon != null)
            {
                rot.GetObject(mon, out object obj);
                if (obj != null) return obj;
            }
        }
        catch { }

        // ② 枚举一遍，按显示名匹配
        IEnumMoniker e = null;
        try { rot.EnumRunning(out e); } catch { }
        if (e == null) return null;

        bool wantWps = progId.StartsWith("KWPP") || progId.StartsWith("wpp");
        var one = new IMoniker[1];
        while (e.Next(1, one, IntPtr.Zero) == 0)
        {
            var m = one[0];
            if (m == null) continue;
            try
            {
                m.GetDisplayName(ctx, null, out string display);
                if (string.IsNullOrEmpty(display)) continue;
                string d = display.ToLowerInvariant();
                bool hit = wantWps
                    ? d.Contains("wpp") || d.Contains("kwpp") || d.EndsWith(".ppt") || d.EndsWith(".pptx")
                    : d.Contains("powerpoint") || d.EndsWith(".ppt") || d.EndsWith(".pptx");
                if (!hit) continue;
                rot.GetObject(m, out object obj);
                if (obj != null) return obj;
            }
            catch { }
        }
        return null;
    }
}
