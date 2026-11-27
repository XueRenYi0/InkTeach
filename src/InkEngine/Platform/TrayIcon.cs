// [职责] 系统托盘图标（非课件时工具栏设为「仅 PPT 显示」时的兜底入口：临时呼出/收起、恢复常驻、打开设置、退出）。
// [测试] --traytest（只验状态机，不进托盘）

using System;
using System.Runtime.InteropServices;
using InkLocalization;

namespace InkEngine;

/// <summary>
/// **系统托盘图标**：在「仅 PPT 显示」桌面零痕迹状态下常驻系统托盘，
/// 提供左键单击临时呼出/收起工具栏，以及右键菜单（显示/隐藏工具栏、恢复常驻工具栏、打开设置、退出）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action _onToggleBar;
    private readonly Action _onExitPptOnly;
    private readonly Action _onOpenSettings;
    private readonly Action _onQuit;
    private readonly Func<bool> _isBarShown;
    private readonly IntPtr _hIcon;
    private readonly bool _ownsIcon;
    private Native.NOTIFYICONDATA _data;
    private bool _visible;
    private double _lastClickMs;

    /// <summary>自检用：现在图标在不在。</summary>
    public bool VisibleForTest => _visible;

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

    public TrayIcon(IntPtr ownerHwnd, Action onToggleBar, Action onExitPptOnly,
                    Action onOpenSettings, Action onQuit, Func<bool> isBarShown)
    {
        _hwnd = ownerHwnd;
        _onToggleBar = onToggleBar;
        _onExitPptOnly = onExitPptOnly;
        _onOpenSettings = onOpenSettings;
        _onQuit = onQuit;
        _isBarShown = isBarShown;
        _hIcon = LoadAppIcon(out _ownsIcon);
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

        _data.uVersion = 4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref _data);
        _visible = true;
    }

    /// <summary>explorer 重启了（任务栏重生）：若当前应显示则重新加回托盘。</summary>
    public void HandleTaskbarCreated()
    {
        if (!_visible) return;
        _visible = false;
        Add();
    }

    /// <summary>窗口过程把托盘消息转进来。返回 true = 这条消息归托盘。</summary>
    public bool HandleMessage(IntPtr wParam, IntPtr lParam)
    {
        int code = (int)(lParam.ToInt64() & 0xFFFF);
        if (code is Native.NIN_SELECT or Native.NIN_KEYSELECT)
        {
            // 双击会连发两条 NIN_SELECT：500ms 内只认一次，避免点一下触发一开一关。
            double now = Environment.TickCount64;
            if (now - _lastClickMs < 500) return true;
            _lastClickMs = now;
            _onToggleBar?.Invoke();
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
            bool shown = _isBarShown?.Invoke() ?? false;
            Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)1,
                shown ? Loc.Tr("隐藏工具栏") : Loc.Tr("显示工具栏"));
            Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)2, Loc.Tr("恢复常驻工具栏"));
            Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)3, Loc.Tr("打开设置"));
            Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)4, Loc.Tr("退出"));
            Native.GetCursorPos(out var pt);
            Native.SetForegroundWindow(_hwnd);
            int cmd = Native.TrackPopupMenu(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            Native.PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (cmd == 1) _onToggleBar?.Invoke();
            else if (cmd == 2) _onExitPptOnly?.Invoke();
            else if (cmd == 3) _onOpenSettings?.Invoke();
            else if (cmd == 4) _onQuit?.Invoke();
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
    /// 图标：优先用 exe 里嵌的小图标，取不到退回共享系统默认图标（共享图标不销毁）。
    /// </summary>
    private static IntPtr LoadAppIcon(out bool ownsIcon)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
            {
                Native.ExtractIconExW(exe, 0, out IntPtr large, out IntPtr small, 1);
                if (large != IntPtr.Zero) Native.DestroyIcon(large);
                if (small != IntPtr.Zero)
                {
                    ownsIcon = true;
                    return small;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[托盘] 取 exe 图标失败（用系统默认）：" + ex.Message);
        }
        ownsIcon = false;
        return Native.LoadIconW(IntPtr.Zero, Native.IDI_APPLICATION);
    }

    public void Dispose()
    {
        Remove();
        if (_ownsIcon && _hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
    }
}
