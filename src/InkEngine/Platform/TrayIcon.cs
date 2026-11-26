// [职责] 系统托盘图标（工具栏被「只在 PPT 显示」藏起来时的兜底入口：打开设置 / 退出）。
// [测试] --traytest（只验状态机，不进托盘）

using System;
using System.Runtime.InteropServices;
using InkLocalization;

namespace InkEngine;

/// <summary>
/// **系统托盘图标**（2026-11-25，自研；用户："工具栏只在 PPT 显示 + 托盘兜底"）。
///
/// 为什么需要它：主窗口是 `WS_EX_TOOLWINDOW`（**不占任务栏**）。「工具栏只在 PPT 显示」
/// 一开，不在放映时屏幕上就没有任何入口能打开设置或退出——老师只能去任务管理器。
/// 托盘是这类"无主窗口常驻小工具"的标准兜底（PowerToys / 输入法都是这么做的）。
///
/// 刻意做成独立类、不碰 Engine 的 partial（ADR-005 D3/D6）：
/// 引擎只跟它说两句话——**要不要显示**（<see cref="Update"/>）和**转进来的消息**
/// （<see cref="HandleMessage"/> / <see cref="HandleTaskbarCreated"/>）。
///
/// 两处 PR 没做、但真机上会踩的坑（自研补上）：
///   1. **explorer 重启**：托盘图标跟着任务栏一起消失，必须收到 `TaskbarCreated`
///      后重新 `NIM_ADD`（见 <see cref="HandleTaskbarCreated"/>）；
///   2. **单击行为**：NOTIFYICON_VERSION_4 下单击 = `NIN_SELECT`，双击 = 两条
///      `NIN_SELECT`；只认一条就不会"点一下弹两下"（下面用时间去重）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action _onOpenSettings;
    private readonly Action _onQuit;
    private readonly IntPtr _hIcon;
    private Native.NOTIFYICONDATA _data;
    private bool _visible;
    private double _lastOpenMs;

    /// <summary>自检用：现在图标在不在。</summary>
    public bool VisibleForTest => _visible;

    // 任务栏"重生"消息号（每个会话现问一次；0 = 还没问过）
    private static uint s_taskbarCreated;

    /// <summary>`TaskbarCreated` 的消息号（给窗口过程比对用；0 = 不是这条消息）。</summary>
    public static uint TaskbarCreatedMessage
    {
        get
        {
            if (s_taskbarCreated == 0)
            {
                try { s_taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated"); }
                catch { s_taskbarCreated = 0; }     // 问不到就算了：不影响其它功能
            }
            return s_taskbarCreated;
        }
    }

    public TrayIcon(IntPtr ownerHwnd, Action onOpenSettings, Action onQuit)
    {
        _hwnd = ownerHwnd;
        _onOpenSettings = onOpenSettings;
        _onQuit = onQuit;
        _hIcon = LoadAppIcon();
        _data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uCallbackMessage = Native.WM_INKTRAY,
            szTip = "InkTeach",
            szInfo = "",
            szInfoTitle = "",
        };
    }

    /// <summary>要不要显示（幂等：状态没变什么都不做；主循环每帧调）。</summary>
    public void Update(bool wanted)
    {
        if (wanted == _visible) return;
        if (!wanted) { Remove(); return; }
        Add();
    }

    /// <summary>真正的 NIM_ADD（首次显示 / explorer 重启后都走这里）。</summary>
    private void Add()
    {
        _data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        _data.hIcon = _hIcon;
        if (!Native.Shell_NotifyIconW(Native.NIM_ADD, ref _data)) return;

        // 版本 4：回调里 lParam 的低 16 位是通知码（NIN_SELECT / WM_CONTEXTMENU），
        // 高 16 位才是鼠标消息——不乱认的话右键会当成"打开设置"。
        _data.uVersion = 4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref _data);
        _visible = true;
    }

    /// <summary>
    /// explorer 重启了（任务栏重生）：托盘里那一格已经没了，**重新加一次**。
    /// 不接这一条的话：老师重启一次资源管理器，兜底入口就永久消失——
    /// 而这时工具栏很可能正被藏着，等于软件"打不开了"。
    /// </summary>
    public void HandleTaskbarCreated()
    {
        if (!_visible) return;          // 本来就不该显示：不主动冒出来
        _visible = false;
        Add();
    }

    /// <summary>窗口过程把托盘消息转进来。返回 true = 这条消息归托盘。</summary>
    public bool HandleMessage(IntPtr wParam, IntPtr lParam)
    {
        // 版本 4 的约定：低 16 位通知码，高 16 位图标 ID。
        int code = (int)(lParam.ToInt64() & 0xFFFF);
        if (code is Native.NIN_SELECT or Native.NIN_KEYSELECT)
        {
            // 单击 / 双击（双击会连发两条 NIN_SELECT）：500ms 内只认一次，
            // 否则"点一下设置、面板被点了两下"（自己把刚开的面板又翻一页）。
            double now = Environment.TickCount64;
            if (now - _lastOpenMs < 500) return true;
            _lastOpenMs = now;
            _onOpenSettings?.Invoke();
            return true;
        }
        if (code == Native.WM_CONTEXTMENU)
        {
            ShowMenu();
            return true;
        }
        return false;
    }

    private void ShowMenu()
    {
        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)1, Loc.Tr("打开设置"));
            Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)2, Loc.Tr("退出"));
            Native.GetCursorPos(out var pt);
            // 菜单前必须把窗口拿到前台：否则点菜单外面它不会消失（Win32 的老规矩）。
            // 覆盖层本来就是置顶 + 不抢焦点，这一下只是"让菜单成为前台"。
            Native.SetForegroundWindow(_hwnd);
            int cmd = Native.TrackPopupMenu(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            if (cmd == 1) _onOpenSettings?.Invoke();
            else if (cmd == 2) _onQuit?.Invoke();
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private void Remove()
    {
        if (!_visible) return;
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref _data);
        _visible = false;
    }

    /// <summary>
    /// 图标：优先用 exe 里嵌的那一张（项目属性 `ApplicationIcon`），取不到退回系统默认。
    /// **必须用小图标**：托盘那一格只有 16×16，拿大图标会被系统缩糊。
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
            {
                Native.ExtractIconExW(exe, 0, out IntPtr large, out IntPtr small, 1);
                if (large != IntPtr.Zero) Native.DestroyIcon(large);   // 只用小的，大的立刻还掉
                if (small != IntPtr.Zero) return small;
            }
        }
        catch (Exception ex)
        {
            // 取图标失败不该影响启动：退回系统默认图标（下面那行）
            Console.WriteLine("[托盘] 取 exe 图标失败（用系统默认）：" + ex.Message);
        }
        return Native.LoadIconW(IntPtr.Zero, Native.IDI_APPLICATION);
    }

    public void Dispose()
    {
        // **必须先删图标**：NIM_DELETE 要一个还活着的 hwnd（引擎关窗口在前）。
        Remove();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
    }
}
