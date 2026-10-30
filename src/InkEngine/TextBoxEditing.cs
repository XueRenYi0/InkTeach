using System.Numerics;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 普通文本框的插入与编辑。
///
/// 编辑用系统原生多行 EDIT：中文/输入法/选区/复制粘贴都由 Windows 处理，
/// 不自绘光标、不处理 WM_CHAR。Ctrl+Enter 结束，Esc 取消；点别处失焦也按结束处理。
/// </summary>
public partial class InkEngine
{
    private IntPtr _textBoxEditHwnd;
    private IntPtr _textBoxEditHost;
    private static Native.WndProcDelegate _textBoxEditHostProc;
    private Native.WndProcDelegate _textBoxEditWndProc;
    private IntPtr _textBoxEditOldProc;
    private bool _textBoxEditHadNoActivate;
    private bool _textBoxEditing;
    private Stroke _textBoxEditStroke;
    private IntPtr _textBoxEditFont;
    private float _textBoxEditFontPx;
    private double _lastTextBoxClickMs = double.NegativeInfinity;
    private Stroke _lastTextBoxClickStroke;

    internal bool TextBoxEditing => _textBoxEditing;

    internal void InsertTextBoxFromUi()
    {
        HideAiPromptEdit();
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        else HideTextBoxEdit();
        var s = CreatePlainTextBox("");
        Doc.SelectOnly(new[] { s });
        BeginTextBoxEdit(s);
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>自检/摆样：插入一个已有文字的普通文本框，不进入编辑。</summary>
    internal void InsertPlainTextBoxForTest(string text)
    {
        var s = CreatePlainTextBox(text ?? "");
        Doc.SelectOnly(new[] { s });
        _dirty = true;
        NotifyUiStateChanged();
    }

    private Stroke CreatePlainTextBox(string text)
    {
        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.75f, DpiScale);
        float w = MathF.Min(420f * u, (screen.MaxX - screen.MinX) * 0.55f);
        float h = 120f * u;
        float x = (screen.MinX + screen.MaxX) * 0.5f - w * 0.5f;
        float y = (screen.MinY + screen.MaxY) * 0.5f - ViewOffsetY - h * 0.5f;

        var data = TextBoxData.CreatePlain(text, u, w, h);
        var s = new Stroke
        {
            Tool = Tool,
            Kind = StrokeKind.TextBox,
            TextBox = data,
            Color = new Color4(0.12f, 0.14f, 0.18f, 1f),
            Width = 0f,
        };
        data.Ink = s.Color;
        // 先排版，再入文档：AddStroke 的脏区和空间索引都按**最终包围盒**登记。
        // 以前先 Add 再长高，低处的字不在脏区里，删除后会残留最后几行（用户实测）。
        _ = data.EnsureLayout();
        s.SetTextBoxRect(new Vector2(x, y), w, data.Height);
        ClampTextBoxIntoView(s);
        Doc.AddStroke(s);
        return s;
    }

    /// <summary>双击普通文本框进入编辑；AI 题卡暂不进入文字编辑。</summary>
    private void MaybeEditTextBoxOnDoubleClick(Stroke s)
    {
        if (s?.TextBox == null || s.TextBox.Role != TextBoxRole.Normal) return;
        double now = NowMs;
        bool dbl = ReferenceEquals(_lastTextBoxClickStroke, s) && now - _lastTextBoxClickMs < 450;
        _lastTextBoxClickStroke = s;
        _lastTextBoxClickMs = now;
        if (dbl) BeginTextBoxEdit(s);
    }

    internal void BeginTextBoxEdit(Stroke s)
    {
        if (s?.TextBox == null || s.TextBox.Role != TextBoxRole.Normal) return;
        EnsureTextBoxEditWindow();
        if (_textBoxEditHwnd == IntPtr.Zero) return;

        _textBoxEditStroke = s;
        _textBoxEditing = true;
        Native.SetWindowTextW(_textBoxEditHwnd, s.TextBox.Text ?? "");
        var font = EnsureTextBoxEditFont(s.TextBox.FontSize * s.TextBox.Unit);
        Native.SendMessage(_textBoxEditHwnd, Native.WM_SETFONT, font, new IntPtr(1));
        int margin = Math.Max(1, (int)MathF.Round(2f * s.TextBox.Unit));
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETMARGINS,
                           new IntPtr(Native.EC_LEFTMARGIN | Native.EC_RIGHTMARGIN),
                           new IntPtr(margin | (margin << 16)));
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETLIMITTEXT, new IntPtr(20000), IntPtr.Zero);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));

        var b = s.PaddedBounds;
        float unit = MathF.Max(0.5f, s.TextBox.Unit);
        float pad = 18f * unit;
        // 编辑窗按**内容区**摆：留出和渲染一致的 18 逻辑像素内边距；
        // 高度至少 320 逻辑像素，粘长文不用在一个 120 高的小框里滚。
        float editH = MathF.Max(s.TextBox.Height, 320f * unit);
        var screen = ScreenRectPhysical();
        editH = MathF.Min(editH, (screen.MaxY - screen.MinY) * 0.65f);
        int x = (int)MathF.Round(b.MinX + 2f + pad);
        int y = (int)MathF.Round(b.MinY + 2f + pad + ViewOffsetY);
        int w = Math.Max(80, (int)MathF.Round(b.MaxX - b.MinX - 4f - pad * 2f));
        int h = Math.Max(40, (int)MathF.Round(editH - pad * 2f));
        int maxBottom = (int)MathF.Round(screen.MaxY - 16f * unit);
        if (y + h > maxBottom)
        {
            // 框贴着屏幕底：编辑窗优先翻到框上方；再不行才压缩高度。
            int above = (int)MathF.Round(b.MinY - h - 8f * unit);
            int top = (int)MathF.Round(screen.MinY + 16f * unit);
            if (above >= top) y = above;
            else y = Math.Max(top, maxBottom - h);
        }
        if (y + h > maxBottom) h = Math.Max(40, maxBottom - y);
        Native.SetWindowPos(_textBoxEditHost, Native.HWND_TOPMOST, x, y, w, h,
                            Native.SWP_SHOWWINDOW);
        Native.SetWindowPos(_textBoxEditHwnd, IntPtr.Zero, 0, 0, w, h,
                            Native.SWP_SHOWWINDOW);
        FocusTextBoxEditWindow();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>自检：直接改原生 EDIT 里的文字（模拟粘贴），不经过键盘。</summary>
    internal void SetTextBoxEditTextForTest(string text)
    {
        if (_textBoxEditing && _textBoxEditHwnd != IntPtr.Zero)
            Native.SetWindowTextW(_textBoxEditHwnd, text ?? "");
    }

    internal void EndTextBoxEditFromUi(bool accept)
    {
        if (!_textBoxEditing) return;
        var s = _textBoxEditStroke;
        string before = s?.TextBox?.Text ?? "";
        string text = before;
        if (accept && _textBoxEditHwnd != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(32768);
            Native.GetWindowTextW(_textBoxEditHwnd, sb, sb.Capacity);
            text = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        }
        _textBoxEditStroke = null;
        HideTextBoxEdit();
        if (s != null && s.TextBox != null)
        {
            var oldBounds = s.PaddedBounds;
            s.TextBox.Text = text;
            s.TextBox.InvalidateLayout();
            s.SyncTextBoxBounds();
            ClampTextBoxIntoView(s);
            Doc.RefreshStrokeGrid(s);
            Doc.Dirty.Add(oldBounds);
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            if (!string.Equals(before, text, StringComparison.Ordinal))
                Doc.CommitInteractive(new TextBoxTextAction(s, before, text, oldBounds, s.PaddedBounds));
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    private void HideTextBoxEdit()
    {
        bool was = _textBoxEditing;
        _textBoxEditing = false;
        if (_textBoxEditHost != IntPtr.Zero)
            Native.ShowWindow(_textBoxEditHost, Native.SW_HIDE);
        if (_textBoxEditHwnd != IntPtr.Zero)
            Native.ShowWindow(_textBoxEditHwnd, Native.SW_HIDE);
        RestoreTextBoxEditFocus();
        if (was) { _dirty = true; NotifyUiStateChanged(); }
    }

    private void EnsureTextBoxEditWindow()
    {
        if (_textBoxEditHwnd != IntPtr.Zero) return;
        RegisterTextBoxEditHost();
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW;
        long hostStyle = 0x80000000L /* WS_POPUP */ | 0x02000000L /* WS_CLIPCHILDREN */;
        _textBoxEditHost = Native.CreateWindowEx(exStyle, "InkTeachEditHost", "InkTeachTextBoxHost",
            hostStyle, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_textBoxEditHost == IntPtr.Zero) return;

        // WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN
        long style = 0x40000000L | 0x10000000L | 0x00200000L | 0x0004L | 0x0040L | 0x1000L;
        _textBoxEditHwnd = Native.CreateWindowEx(0, "EDIT", "InkTeachTextBox",
            style, 0, 0, 1, 1, _textBoxEditHost, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_textBoxEditHwnd == IntPtr.Zero) return;
        _textBoxEditWndProc = TextBoxEditWndProc;
        _textBoxEditOldProc = Native.SetWindowLongPtr(
            _textBoxEditHwnd, Native.GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_textBoxEditWndProc));
    }

    private void RegisterTextBoxEditHost()
    {
        if (_textBoxEditHostProc != null) return;
        _textBoxEditHostProc = TextBoxEditHostProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_textBoxEditHostProc),
            hInstance = _hInstance,
            hCursor = Native.LoadCursor(IntPtr.Zero, new IntPtr(Native.IDC_ARROW)),
            hbrBackground = Native.GetStockObject(0 /* WHITE_BRUSH */),
            lpszClassName = "InkTeachEditHost",
        };
        Native.RegisterClassEx(ref wc);
    }

    private static IntPtr TextBoxEditHostProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
            {
                Native.GetClientRect(hWnd, out var rc);
                Native.FillRect(wParam, ref rc, Native.GetStockObject(0 /* WHITE_BRUSH */));
                return new IntPtr(1);
            }
            case Native.WM_CTLCOLOREDIT:
                Native.SetBkColor(wParam, 0x00FFFFFF);
                Native.SetTextColor(wParam, 0x00000000);
                return Native.GetStockObject(0 /* WHITE_BRUSH */);
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr TextBoxEditWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
            {
                // 顶层 EDIT 默认不刷底色，会透出下面的 PPT/浏览器；编辑时铺白底。
                Native.GetClientRect(hWnd, out var rc);
                Native.FillRect(wParam, ref rc, Native.GetStockObject(0 /* WHITE_BRUSH */));
                return new IntPtr(1);
            }
            case Native.WM_KEYDOWN:
            {
                int vk = (int)wParam.ToInt64();
                bool ctrl = (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0;
                if (vk == 0x0D && ctrl) { EndTextBoxEditFromUi(true); return IntPtr.Zero; }
                if (vk == 0x1B) { EndTextBoxEditFromUi(false); return IntPtr.Zero; }
                break;
            }
            case Native.WM_KILLFOCUS:
                if (_textBoxEditing) EndTextBoxEditFromUi(true);
                return IntPtr.Zero;
            case Native.WM_GETDLGCODE:
                return new IntPtr(Native.DLGC_WANTALLKEYS);
        }
        return Native.CallWindowProc(_textBoxEditOldProc, hWnd, msg, wParam, lParam);
    }

    private IntPtr EnsureTextBoxEditFont(float px)
    {
        float v = Math.Clamp(px, 12f, 96f);
        if (_textBoxEditFont != IntPtr.Zero && MathF.Abs(_textBoxEditFontPx - v) < 0.5f)
            return _textBoxEditFont;
        if (_textBoxEditFont != IntPtr.Zero) Native.DeleteObject(_textBoxEditFont);
        _textBoxEditFont = Native.CreateFontW(-(int)MathF.Round(v), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        _textBoxEditFontPx = v;
        return _textBoxEditFont != IntPtr.Zero ? _textBoxEditFont : Native.GetStockObject(Native.DEFAULT_GUI_FONT);
    }

    private void FocusTextBoxEditWindow()
    {
        IntPtr main = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        _textBoxEditHadNoActivate = false;
        if (main != IntPtr.Zero)
        {
            long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_NOACTIVATE) != 0)
            {
                _textBoxEditHadNoActivate = true;
                Native.SetWindowLongPtr(main, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
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
            Native.SetForegroundWindow(_textBoxEditHost);
            Native.SetFocus(_textBoxEditHwnd);
            Native.AttachThreadInput(myTid, fgTid, false);
        }
        else
        {
            Native.SetForegroundWindow(_textBoxEditHost);
            Native.SetFocus(_textBoxEditHwnd);
        }
        Native.SetWindowPos(_textBoxEditHost, Native.HWND_TOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void RestoreTextBoxEditFocus()
    {
        bool had = _textBoxEditHadNoActivate;
        _textBoxEditHadNoActivate = false;
        if (had && _windows.Count > 0)
        {
            IntPtr main = _windows[0].Hwnd;
            long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(main, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            Native.SetWindowPos(main, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        if (KeyboardMode && _windows.Count > 0) Native.SetFocus(_windows[0].Hwnd);
    }
}

/// <summary>
/// 文本框改文字的一步撤销。只换 <see cref="TextBoxData.Text"/> 并重排，
/// 不碰宽高/字号（那些由 <see cref="TextBoxGeometryAction"/> 管）。
/// </summary>
internal sealed class TextBoxTextAction : EditAction
{
    private readonly Stroke _stroke;
    private readonly string _before, _after;
    private readonly RectF _beforeBounds, _afterBounds;

    public TextBoxTextAction(Stroke s, string before, string after,
                             RectF beforeBounds, RectF afterBounds)
    {
        _stroke = s;
        _before = before;
        _after = after;
        _beforeBounds = beforeBounds;
        _afterBounds = afterBounds;
    }

    public override int HeldStrokes => 1;
    public override RectF AffectedBefore => _beforeBounds;
    public override RectF AffectedAfter => _afterBounds;

    public override void Undo(InkDocument doc) => Apply(doc, _before);
    public override void Redo(InkDocument doc) => Apply(doc, _after);

    private void Apply(InkDocument doc, string text)
    {
        var box = _stroke.TextBox;
        if (box == null) return;
        var old = _stroke.PaddedBounds;
        box.Text = text;
        box.InvalidateLayout();
        _stroke.SyncTextBoxBounds();
        doc.RefreshStrokeGrid(_stroke);
        doc.Dirty.Add(old);
        doc.Dirty.Add(_stroke.PaddedBounds);
        doc.StructureChangedSinceRender = true;
        doc.Version++;
    }
}
