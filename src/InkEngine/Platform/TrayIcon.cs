// [职责] 系统托盘图标（工具栏被设置隐藏时的兜底入口：打开设置 / 退出）。
// [测试] --traytest

using System.Runtime.InteropServices;
using InkLocalization;

namespace InkEngine;

/// <summary>
/// 系统托盘图标（2026-11-25，用户："可以加一个托盘图标"）。
///
/// 主窗口是 `WS_EX_TOOLWINDOW`（不占任务栏），所以当「工具栏只在 PPT 显示」把工具条藏起来时，
/// 托盘是唯一还能让老师找到设置/退出的入口。**故意做成独立类、不做 Engine 的 partial**
/// （ADR-005 禁止给 Engine / FullUi / Overlay 的 partial 族新增文件）。
///
/// 只暴露三件事给引擎：构造（窗口句柄 + 两个回调）、<see cref="Update"/>（要不要显示）、
/// <see cref="HandleMessage"/>（窗口过程里转进来的托盘消息）。图标来源与菜单都在这里面，
/// 引擎一行 Win32 都不用写。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action _onOpenSettings;
    private readonly Action _onQuit;
    private readonly IntPtr _hIcon;
    private Native.NOTIFYICONDATA _data;
    private bool _visible;

    /// <summary>自检用：现在图标在不在。</summary>
    public bool VisibleForTest => _visible;

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

    /// <summary>要不要显示（幂等：状态没变什么都不做）。</summary>
    public void Update(bool wanted)
    {
        if (wanted == _visible) return;
        if (!wanted) { Remove(); return; }

        _data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        _data.hIcon = _hIcon;
        if (!Native.Shell_NotifyIconW(Native.NIM_ADD, ref _data)) return;

        // 用 NOTIFYICON_VERSION_4：回调里 lParam 的低 16 位是通知码（NIN_SELECT / WM_CONTEXTMENU）。
        var v = _data;
        v.uVersion = 4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref v);
        _data.uVersion = 4;
        _visible = true;
    }

    /// <summary>窗口过程把托盘消息转进来。返回 true = 这条消息归托盘。</summary>
    public bool HandleMessage(IntPtr wParam, IntPtr lParam)
    {
        int code = (int)(lParam.ToInt64() & 0xFFFF);
        if (code is Native.NIN_SELECT or Native.NIN_KEYSELECT
            or Native.WM_LBUTTONUP or Native.WM_LBUTTONDBLCLK)
        {
            _onOpenSettings?.Invoke();
            return true;
        }
        if (code is Native.WM_CONTEXTMENU or Native.WM_RBUTTONUP)
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
            // 菜单前必须把窗口拿到前台，否则点菜单外面它不会消失（Win32 的老规矩）。
            Native.SetForegroundWindow(_hwnd);
            int cmd = Native.TrackPopupMenu(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON,
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

    /// <summary>图标：优先用 exe 里嵌的那一张（`ApplicationIcon`），拿不到退回系统默认图标。</summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
            {
                Native.ExtractIconExW(exe, 0, out IntPtr large, out IntPtr small, 1);
                if (large != IntPtr.Zero) Native.DestroyIcon(large);   // 托盘只用小图标
                if (small != IntPtr.Zero) return small;
            }
        }
        catch
        {
            // 取图标失败不该影响启动：回退到系统默认图标（下面那行）。
        }
        return Native.LoadIconW(IntPtr.Zero, Native.IDI_APPLICATION);
    }

    public void Dispose()
    {
        Remove();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
    }
}
