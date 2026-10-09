// =====================================================================================
//  PPT 直映：把老师的 PPT 交给系统里装好的 Office / WPS 直接放起来（2026-10-09）
//
//  背景与实测（计划文档 §十二 "实测收口"）：
//    · Office：`POWERPNT.EXE /S "文件"` 官方命令行 → 直接进全屏放映（冷启动 / 已开着
//      两种情形都实测过，编辑窗全程不出现）；
//    · WPS   ：没有放映命令行（`wpp.exe /S` 无效、右键也没有"放映"动词）；官方 COM
//      自动化 `KWPP.Application` → `Presentations.Open(路径, 只读, 不新建, **不带窗口**)`
//      → `SlideShowSettings.Run()` → 直接进放映，连编辑界面都不创建（实测过）；
//    · 选哪家 = 看 .pptx 的**默认关联**（和老师平时双击一致）。不按"速度/省资源"排名：
//      渲染本来就在 Office/WPS 里跑，我们两条路都只是"喊一嗓子"的成本；
//    · 两家都不是 / 启动失败 → 退回"普通打开 + 提示按 F5"，绝不挡路（规矩三：失败当没有）。
//
//  与 Ppt.cs 的分工：这里只管**把放映启动起来**；起来之后的"检测在放映 / 按页批注 /
//  翻页 / 退场"全部由现成的 PptLink + Ppt.cs 接管（零新逻辑，端到端 `--pptprobe` 已验）。
//
//  ⚠ AOT：全程原始接口——advapi32 读注册表、ComLate 迟绑定、CreateProcess——零反射。
// =====================================================================================

using System.Diagnostics;

namespace InkEngine;

/// <summary>PPT 的启动路由（纯枚举，自检直接测）。</summary>
internal enum PptRoute { None, Office, Wps }

internal static class PptLaunch
{
    /// <summary>"打开文档…"过滤器 / 扩展名判断共用的一份口径（见 ExportFileDialog.DocumentFilter）。</summary>
    internal const string FilterSpec = "*.pptx;*.ppt;*.ppsx";

    internal static bool IsPpt(string path)
    {
        var ext = System.IO.Path.GetExtension(path ?? "");
        return ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ppt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ppsx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按默认关联的 ProgId 判走哪家（纯函数）。</summary>
    internal static PptRoute RouteFor(string progId)
    {
        if (string.IsNullOrEmpty(progId)) return PptRoute.None;
        if (progId.StartsWith("WPP", StringComparison.OrdinalIgnoreCase)
            || progId.StartsWith("KWPP", StringComparison.OrdinalIgnoreCase)
            || progId.StartsWith("Kingsoft", StringComparison.OrdinalIgnoreCase))
            return PptRoute.Wps;
        if (progId.StartsWith("PowerPoint", StringComparison.OrdinalIgnoreCase))
            return PptRoute.Office;
        return PptRoute.None;
    }

    /// <summary>这个文件的默认关联 ProgId：先用户选择（UserChoice），再 HKCR 兜底。读不到 = null。</summary>
    internal static string ProgIdOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
        if (ext.Length == 0) return null;
        if (Native.ReadString(Native.HKEY_CURRENT_USER,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice",
                "ProgId", out var id, out _) && !string.IsNullOrEmpty(id))
            return id;
        if (Native.ReadString(Native.HKEY_CLASSES_ROOT, ext, null, out var cls, out _) && !string.IsNullOrEmpty(cls))
            return cls;
        return null;
    }

    /// <summary>
    /// 启动放映。返回**给状态栏的一句话**（成功 / 兜底都说清楚）。
    /// 调用方负责先"借前台"（见 InkEngine.LaunchPptShow 的注释——那一步决定放映窗
    /// 能不能直接盖到最前）。
    /// </summary>
    internal static string Run(string file)
    {
        var name = System.IO.Path.GetFileName(file);
        switch (RouteFor(ProgIdOf(file)))
        {
            case PptRoute.Wps:
                if (TryRunWps(file)) return $"已让 WPS 直接放映：{name}（Esc 退出）";
                ShellOpen(file);
                return "WPS 没能直接放映（已普通打开）——按 F5 开始放映";
            case PptRoute.Office:
                if (TryRunOffice(file)) return $"已让 PowerPoint 直接放映：{name}（Esc 退出）";
                ShellOpen(file);
                return "PowerPoint 没能直接放映（已普通打开）——按 F5 开始放映";
            default:
                ShellOpen(file);
                return "已打开 PPT（这台机不是 Office/WPS 直映；需要时按放映键）";
        }
    }

    // ---- 各条路线 ---------------------------------------------------------------------

    private static bool TryRunOffice(string file)
    {
        var exe = FindPowerPoint();
        if (exe == null) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "/S \"" + file + "\"",
                UseShellExecute = false,
            });
            return true;
        }
        catch { return false; }
    }

    private static string FindPowerPoint()
    {
        foreach (var sub in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\POWERPNT.EXE",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\POWERPNT.EXE",
        })
        {
            if (Native.ReadString(Native.HKEY_LOCAL_MACHINE, sub, null, out var p, out _)
                && !string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
                return p;
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = System.IO.Path.Combine(root, @"Microsoft Office\root\Office16\POWERPNT.EXE");
            if (System.IO.File.Exists(p)) return p;
        }
        return null;
    }

    // ⚠ COM 引用**故意不释放**：WPS 的"无窗文稿"没有窗口兜底——这边若把应用/文稿的
    // 引用全放掉，WPS 可能判定"没人要了"，把文稿甚至正在放的放映一起收掉。留到本进程
    // 结束（=下课关软件）由系统统一释放；不写盘、不留设置。换第二份 PPT 时旧的也不放
    // （一次课多留一两个引用，可忽略）。
    private static IntPtr _wpsApp;
    private static IntPtr _wpsPres;

    private static bool TryRunWps(string file)
    {
        try
        {
            if (!ComLate.CreateFromProgId("KWPP.Application", out var app) || app == IntPtr.Zero)
                return false;
            var presentations = ComLate.GetObject(app, "Presentations");
            if (presentations == IntPtr.Zero) { ComLate.Release(app); return false; }

            var pres = ComLate.OpenPresentation(presentations, file, readOnly: true, untitled: false, withWindow: false);
            ComLate.Release(presentations);
            if (pres == IntPtr.Zero) { ComLate.Release(app); return false; }

            var settings = ComLate.GetObject(pres, "SlideShowSettings");
            if (settings == IntPtr.Zero) { ComLate.Release(pres); ComLate.Release(app); return false; }
            bool ok = ComLate.Invoke0(settings, "Run");
            ComLate.Release(settings);
            if (!ok) { ComLate.Release(pres); ComLate.Release(app); return false; }

            _wpsApp = app;          // 不释放，见上
            _wpsPres = pres;
            return true;
        }
        catch { return false; }
    }

    private static void ShellOpen(string file)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
        }
        catch { /* 规矩三：失败当没有 */ }
    }

    // ---- 放映窗（"上线保障"用）--------------------------------------------------------

    /// <summary>
    /// 找"正在放映"的窗口：PowerPoint 的 `screenClass`，或标题带"幻灯片放映 / Slide Show"
    /// 的（WPS / 各播放器）。找到返回窗口句柄（顺带给出进程号），没有返回 0。
    /// </summary>
    internal static IntPtr FindShowWindow(out uint pid)
    {
        IntPtr found = IntPtr.Zero;
        uint foundPid = 0;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            var cls = new System.Text.StringBuilder(64);
            Native.GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() != "screenClass")
            {
                var title = new System.Text.StringBuilder(256);
                Native.GetWindowTextW(h, title, title.Capacity);
                var t = title.ToString();
                if (t.IndexOf("幻灯片放映", StringComparison.Ordinal) < 0
                    && t.IndexOf("Slide Show", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;
            }
            found = h;
            Native.GetWindowThreadProcessId(h, out foundPid);
            return false;
        }, IntPtr.Zero);
        pid = foundPid;
        return found;
    }

    /// <summary>把窗口拉到最前（一次性；调用方应刚"借过前台"才有资格设前台）。</summary>
    internal static bool TrySetForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var fg = Native.GetForegroundWindow();
            uint dummy;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out dummy);
            uint me = Native.GetCurrentThreadId();
            bool attached = fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            bool ok = Native.SetForegroundWindow(hwnd);
            if (attached) Native.AttachThreadInput(me, fgThread, false);
            return ok;
        }
        catch { return false; }
    }
}
