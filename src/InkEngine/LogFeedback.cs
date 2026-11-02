// 日志与反馈：用户在上传日志前写一句“遇到了什么问题”。
//
// 为什么用原生 EDIT：中文/输入法、候选框、软键盘都交给系统，和 AI 面板、文本框编辑同一条路。
// 这里只做一个**单行**输入（最多 240 字）；日志反馈不需要富文本。
using System.Runtime.InteropServices;

namespace InkEngine;

public partial class InkEngine
{
    /// <summary>用户填的问题描述；上传/导出时写进 zip 的 `问题.txt`。</summary>
    internal string LogNoteText = "";

    /// <summary>最近一次清屏的摘要（自动写进日志上下文，方便定位“清屏没清干净”这类问题）。</summary>
    internal string LastClearSummary = "";

    private IntPtr _logNoteHwnd;
    private Native.WndProcDelegate _logNoteWndProc;
    private IntPtr _logNoteOldProc;
    private bool _logNoteEditing;
    private bool _logNoteHadNoActivate;

    internal bool LogNoteEditing => _logNoteEditing;

    /// <summary>自检用：把文字直接塞进原生输入框，走真实的“读取输入”路径。</summary>
    internal void SetLogNoteTextForTest(string text)
        => Native.SetWindowTextW(_logNoteHwnd, text ?? "");

    internal void BeginLogNoteEditFromUi(float x, float y, float w, float h)
    {
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        HideAiPromptEdit();                 // 两个原生输入框不叠在一起
        EnsureLogNoteWindow();
        if (_logNoteHwnd == IntPtr.Zero) return;

        Native.SetWindowTextW(_logNoteHwnd, LogNoteText ?? "");
        var font = EnsureLibraryRenameFont(UiUnit);
        Native.SendMessage(_logNoteHwnd, Native.WM_SETFONT, font, new IntPtr(1));
        int margin = Math.Max(2, (int)MathF.Round(8f * UiUnit));
        Native.SendMessage(_logNoteHwnd, Native.EM_SETMARGINS,
                           new IntPtr(Native.EC_LEFTMARGIN | Native.EC_RIGHTMARGIN),
                           new IntPtr(margin | (margin << 16)));
        Native.SendMessage(_logNoteHwnd, Native.EM_SETLIMITTEXT, new IntPtr(240), IntPtr.Zero);
        Native.SendMessage(_logNoteHwnd, Native.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));

        int px = (int)MathF.Round(x * UiUnit);
        int py = (int)MathF.Round(y * UiUnit);
        int pw = Math.Max(120, (int)MathF.Round(w * UiUnit));
        int ph = Math.Max(28, (int)MathF.Round(h * UiUnit));
        Native.SetWindowPos(_logNoteHwnd, Native.HWND_TOPMOST, px, py, pw, ph,
                            Native.SWP_SHOWWINDOW);
        FocusLogNoteWindow();
        _logNoteEditing = true;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void EndLogNoteEditFromUi(bool accept)
    {
        if (!_logNoteEditing) return;
        string text = LogNoteText ?? "";
        if (accept && _logNoteHwnd != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(512);
            Native.GetWindowTextW(_logNoteHwnd, sb, sb.Capacity);
            text = sb.ToString().Trim();
            if (text.Length > 240) text = text[..240];
        }
        LogNoteText = text;
        HideLogNoteEdit();
        _dirty = true;
        NotifyUiStateChanged();
    }

    private void HideLogNoteEdit()
    {
        bool was = _logNoteEditing;
        _logNoteEditing = false;
        if (_logNoteHwnd != IntPtr.Zero)
            Native.ShowWindow(_logNoteHwnd, Native.SW_HIDE);
        RestoreLogNoteFocus();
        if (was) { _dirty = true; NotifyUiStateChanged(); }
    }

    private void EnsureLogNoteWindow()
    {
        if (_logNoteHwnd != IntPtr.Zero) return;
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW;
        _logNoteHwnd = Native.CreateWindowEx(exStyle, "EDIT", "InkTeachLogNote",
            0x80000000L /*WS_POPUP*/ | 0x00800000L /*WS_BORDER*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_logNoteHwnd == IntPtr.Zero)
        {
            Console.WriteLine("日志输入框创建失败: " + Marshal.GetLastWin32Error());
            return;
        }
        _logNoteWndProc = LogNoteWndProc;
        _logNoteOldProc = Native.SetWindowLongPtr(
            _logNoteHwnd, Native.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_logNoteWndProc));
    }

    private IntPtr LogNoteWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_KEYDOWN:
            {
                int vk = (int)wParam.ToInt64();
                if (vk == 0x0D) { EndLogNoteEditFromUi(true); return IntPtr.Zero; }
                if (vk == 0x1B) { EndLogNoteEditFromUi(false); return IntPtr.Zero; }
                break;
            }
            case Native.WM_CHAR:
                if ((int)wParam.ToInt64() == 0x0D || (int)wParam.ToInt64() == 0x1B)
                    return IntPtr.Zero;
                break;
            case Native.WM_KILLFOCUS:
                if (_logNoteEditing) EndLogNoteEditFromUi(true);
                return IntPtr.Zero;
            case Native.WM_GETDLGCODE:
                return new IntPtr(Native.DLGC_WANTALLKEYS);
        }
        return Native.CallWindowProc(_logNoteOldProc, hWnd, msg, wParam, lParam);
    }

    private void FocusLogNoteWindow()
    {
        IntPtr main = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        _logNoteHadNoActivate = false;
        if (main != IntPtr.Zero)
        {
            long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_NOACTIVATE) != 0)
            {
                _logNoteHadNoActivate = true;
                Native.SetWindowLongPtr(main, Native.GWL_EXSTYLE,
                    new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
                Native.SetWindowPos(main, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            }
        }
        IntPtr fg = Native.GetForegroundWindow();
        uint fgTid = Native.GetWindowThreadProcessId(fg, out _);
        uint myTid = Native.GetCurrentThreadId();
        if (fgTid != 0 && fgTid != myTid)
        {
            Native.AttachThreadInput(myTid, fgTid, true);
            Native.SetForegroundWindow(_logNoteHwnd);
            Native.SetFocus(_logNoteHwnd);
            Native.AttachThreadInput(myTid, fgTid, false);
        }
        else
        {
            Native.SetForegroundWindow(_logNoteHwnd);
            Native.SetFocus(_logNoteHwnd);
        }
        Native.SetWindowPos(_logNoteHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void RestoreLogNoteFocus()
    {
        bool had = _logNoteHadNoActivate;
        _logNoteHadNoActivate = false;
        if (had && _windows.Count > 0)
        {
            IntPtr main = _windows[0].Hwnd;
            long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(main, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            Native.SetWindowPos(main, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        if (KeyboardMode && _windows.Count > 0) Native.SetFocus(_windows[0].Hwnd);
    }
}
