using System.Runtime.InteropServices;
using System.Text;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>文字输入模式。RichText 先不进接口，画布文本框仍走 TextBoxEditing。</summary>
public enum TextInputMode
{
    SingleLine = 0,
    MultiLine = 1,
}

/// <summary>文字宿主的视觉参数（界面逻辑像素）。</summary>
public sealed class TextInputVisual
{
    public Color4 Background { get; init; } = new(1f, 1f, 1f, 1f);
    public Color4 Text { get; init; } = new(0f, 0f, 0f, 1f);
    public float FontSize { get; init; } = 14f;
    public float Padding { get; init; } = 8f;
}

/// <summary>
/// 一次文字输入请求。页面只给“矩形 + 初值 + 校验 + 提交/取消”，
/// IME/光标/选区/剪贴板/撤销交给原生 EDIT。
/// </summary>
public sealed class TextInputRequest
{
    public string Id { get; init; } = "";
    public RectF Rect { get; set; }
    public string InitialText { get; init; } = "";
    public TextInputMode Mode { get; init; } = TextInputMode.SingleLine;
    public int MaxLength { get; init; } = 512;
    public bool Password { get; init; }

    /// <summary>false = 只创建/定位宿主，不抢前台（自检用）。</summary>
    public bool Activate { get; init; } = true;

    /// <summary>false = 宿主保持隐藏，只用于自检/预加载；默认显示。</summary>
    public bool Visible { get; init; } = true;

    /// <summary>失焦时提交（默认）；false = 失焦取消。</summary>
    public bool CommitOnFocusLost { get; init; } = true;

    public TextInputVisual Visual { get; init; } = new();

    /// <summary>返回非空字符串 = 校验失败，保持编辑并调 OnError。</summary>
    public Func<string, string> Validate { get; init; }
    public Action<string> OnError { get; init; }
    public Action<string> OnCommit { get; init; }
    public Action OnCancel { get; init; }

    /// <summary>Tab 提交后通知页面移动焦点；bool = Shift。</summary>
    public Action<bool> OnTab { get; init; }
}

/// <summary>一个窗口一个文字输入宿主；页面通过 IUiHost.TextInput 使用。</summary>
public interface ITextInputService
{
    bool IsEditing { get; }
    string EditingId { get; }

    void Begin(TextInputRequest request);
    void UpdateRect(RectF rect);
    void Commit();
    void Cancel();
}

public partial class InkEngine
{
    private TextInputService _textInput;

    internal ITextInputService TextInput => _textInput ??= new TextInputService(this);

    /// <summary>自检用：当前 EDIT 的格式化矩形（物理像素；Top/Bottom/Height）。</summary>
    internal (int Top, int Bottom, int Height) TextInputRectForTest
        => (_textInput as TextInputService)?.TextRectForTest ?? (0, 0, 0);

    /// <summary>
    /// 原生文字宿主：无边框 EDIT，支持单行/多行/密码、IME、选区、复制粘贴、撤销。
    /// 第一版用 windowed EDIT；真透明以后评估 windowless RichEdit/TSF（见调研 §3.12）。
    /// </summary>
    internal sealed class TextInputService : ITextInputService, IDisposable
    {
        private readonly InkEngine _engine;
        private IntPtr _hwnd;
        private IntPtr _hostHwnd;
        private IntPtr _oldProc;
        private IntPtr _hostOldProc;
        private Native.WndProcDelegate _wndProc;
        private Native.WndProcDelegate _hostWndProc;
        private IntPtr _font;
        private IntPtr _brush;
        private uint _brushColor;
        private float _fontPx;
        private bool _editing;
        private bool _hiding;
        private bool _mainNoActivate;
        private TextInputRequest _request;
        private bool _multiLine;
        private int _clientW, _clientH;
        private int _padPx;

        public TextInputService(InkEngine engine) => _engine = engine;

        public bool IsEditing => _editing;
        public string EditingId => _editing ? _request?.Id ?? "" : "";

        public void Begin(TextInputRequest request)
        {
            if (request == null) return;
            if (_editing) Commit();

            _request = request;
            _editing = true;
            _hiding = false;

            EnsureWindow(request.Mode);
            if (_hwnd == IntPtr.Zero)
            {
                _editing = false;
                _request = null;
                return;
            }

            ApplyVisual(request.Visual);
            Native.SetWindowTextW(_hwnd, SanitizeSingleLine(request.InitialText));
            Native.SendMessage(_hwnd, Native.EM_SETLIMITTEXT,
                new IntPtr(Math.Max(1, request.MaxLength)), IntPtr.Zero);
            Native.SendMessage(_hwnd, 0x00CC /*EM_SETPASSWORDCHAR*/,
                new IntPtr(request.Password ? '*' : 0), IntPtr.Zero);

            int len = Native.GetWindowTextLengthW(_hwnd);
            Native.SendMessage(_hwnd, Native.EM_SETSEL, new IntPtr(len), new IntPtr(len));
            Position(request.Rect);

            if (request.Visible)
            {
                Native.ShowWindow(_hostHwnd, Native.SW_SHOW);
                if (request.Activate) FocusHost();
            }
        }

        public void UpdateRect(RectF rect)
        {
            if (!_editing || _request == null) return;
            _request.Rect = rect;
            Position(rect);
        }

        /// <summary>自检用：直接改当前 EDIT 的文本；不触发提交。</summary>
        internal void SetTextForTest(string text)
        {
            if (_editing && _hwnd != IntPtr.Zero)
                Native.SetWindowTextW(_hwnd, SanitizeSingleLine(text));
        }

        /// <summary>自检用：EDIT 的格式化矩形 + 客户区高度（物理像素）。</summary>
        internal (int Top, int Bottom, int Height) TextRectForTest
        {
            get
            {
                if (_hwnd == IntPtr.Zero) return (0, 0, 0);
                var rc = new Native.RECT();
                Native.SendMessageRect(_hwnd, Native.EM_GETRECT, IntPtr.Zero, ref rc);
                return (rc.Top, rc.Bottom, _clientH);
            }
        }

        public void Commit()
        {
            if (!_editing || _request == null) return;
            TextInputRequest req = _request;
            string text = ReadText();

            if (req.Validate != null)
            {
                string error = req.Validate(text);
                if (!string.IsNullOrEmpty(error))
                {
                    req.OnError?.Invoke(error);
                    return;       // 保持编辑，不丢用户输入
                }
            }

            HideCore();
            req.OnCommit?.Invoke(text);
        }

        public void Cancel()
        {
            if (!_editing || _request == null) return;
            TextInputRequest req = _request;
            HideCore();
            req.OnCancel?.Invoke();
        }

        public void Dispose()
        {
            if (_hostHwnd != IntPtr.Zero)
            {
                bool oldHiding = _hiding;
                _hiding = true;
                Native.DestroyWindow(_hostHwnd);   // 会连同 EDIT 子窗一起销毁
                _hiding = oldHiding;
            }
            _hwnd = IntPtr.Zero;
            _hostHwnd = IntPtr.Zero;
            _editing = false;
            _request = null;
            if (_font != IntPtr.Zero) Native.DeleteObject(_font);
            _font = IntPtr.Zero;
            if (_brush != IntPtr.Zero) Native.DeleteObject(_brush);
            _brush = IntPtr.Zero;
        }

        private void EnsureWindow(TextInputMode mode)
        {
            bool wantMulti = mode == TextInputMode.MultiLine;
            if (_hostHwnd != IntPtr.Zero)
            {
                // EDIT 的 ES_MULTILINE 不能运行时切换；模式变了就重建。
                if (_multiLine == wantMulti) return;
                // DestroyWindow 会发 WM_KILLFOCUS；先挡住，否则会把新请求当成“失焦提交”。
                bool oldHiding = _hiding;
                _hiding = true;
                Native.DestroyWindow(_hostHwnd);
                _hwnd = IntPtr.Zero;
                _hostHwnd = IntPtr.Zero;
                _hiding = oldHiding;
            }

            _multiLine = wantMulti;
            // 单行也带 ES_MULTILINE：只有多行 EDIT 才能用 EM_SETRECT 做垂直居中。
            // 单行会拦截 Enter、并把粘贴里的换行压成空格。
            long style = 0x0004L /*ES_MULTILINE*/;
            if (_multiLine)
                style |= 0x0010L /*ES_WANTRETURN*/ | 0x00200000L /*WS_VSCROLL*/;
            else
                style |= 0x0080L /*ES_AUTOHSCROLL*/;

            _hostWndProc = HostWndProc;
            _hostHwnd = Native.CreateWindowEx(
                Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW,
                "STATIC", "InkTeachTextInputHost", 0x80000000L /*WS_POPUP*/,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _engine._hInstance, IntPtr.Zero);
            if (_hostHwnd == IntPtr.Zero)
            {
                Console.WriteLine("文字输入容器创建失败: " + Marshal.GetLastWin32Error());
                return;
            }
            _hostOldProc = Native.SetWindowLongPtr(
                _hostHwnd, Native.GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_hostWndProc));

            _hwnd = Native.CreateWindowEx(
                0, "EDIT", "InkTeachTextInput",
                style | 0x40000000L /*WS_CHILD*/ | 0x10000000L /*WS_VISIBLE*/,
                0, 0, 1, 1, _hostHwnd, IntPtr.Zero, _engine._hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Console.WriteLine("文字输入 EDIT 创建失败: " + Marshal.GetLastWin32Error());
                Native.DestroyWindow(_hostHwnd);
                _hostHwnd = IntPtr.Zero;
                return;
            }

            _wndProc = WndProc;
            _oldProc = Native.SetWindowLongPtr(
                _hwnd, Native.GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProc));
        }

        private void ApplyVisual(TextInputVisual v)
        {
            v ??= new TextInputVisual();
            float px = MathF.Max(8f, MathF.Round(v.FontSize * _engine.UiUnit));
            if (_font == IntPtr.Zero || MathF.Abs(_fontPx - px) > 0.5f)
            {
                if (_font != IntPtr.Zero) Native.DeleteObject(_font);
                _font = Native.CreateFontW(
                    -(int)px, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
                _fontPx = px;
            }
            if (_font != IntPtr.Zero)
                Native.SendMessage(_hwnd, Native.WM_SETFONT, _font, new IntPtr(1));

            int pad = (int)MathF.Round(v.Padding * _engine.UiUnit);
            _padPx = pad;
            Native.SendMessage(_hwnd, Native.EM_SETMARGINS,
                new IntPtr(Native.EC_LEFTMARGIN | Native.EC_RIGHTMARGIN),
                new IntPtr(pad | (pad << 16)));

            uint bg = ColorRef(v.Background);
            if (bg != _brushColor || _brush == IntPtr.Zero)
            {
                if (_brush != IntPtr.Zero) Native.DeleteObject(_brush);
                _brush = Native.CreateSolidBrush(bg);
                _brushColor = bg;
            }
            _textColor = ColorRef(v.Text);
            UpdateTextRect();
        }

        private uint _textColor;

        private void Position(RectF logical)
        {
            if (_hostHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero) return;
            float u = _engine.UiUnit;
            int x = (int)MathF.Floor(logical.MinX * u);
            int y = (int)MathF.Floor(logical.MinY * u);
            int w = Math.Max(24, (int)MathF.Ceiling((logical.MaxX - logical.MinX) * u));
            int h = Math.Max(24, (int)MathF.Ceiling((logical.MaxY - logical.MinY) * u));
            _clientW = w; _clientH = h;
            Native.SetWindowPos(_hostHwnd, Native.HWND_TOPMOST, x, y, w, h, 0);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, w, h, Native.SWP_NOZORDER);
            UpdateTextRect();
        }

        /// <summary>
        /// 文字垂直居中：单行 EDIT 默认顶对齐，看起来像“字跑到框上面”。
        /// 单行也建成多行 EDIT，用 EM_SETRECT 把格式化矩形放到中间；
        /// 多行则用 visual padding 做上/下留白。
        /// </summary>
        private void UpdateTextRect()
        {
            if (_hwnd == IntPtr.Zero || _clientW <= 0 || _clientH <= 0) return;
            int top, bottom;
            if (_multiLine)
            {
                top = _padPx;
                bottom = Math.Max(top + 8, _clientH - _padPx);
            }
            else
            {
                int line = Math.Max(1, (int)MathF.Round(_fontPx * 1.4f));
                top = Math.Max(0, (_clientH - line) / 2);
                bottom = Math.Min(_clientH, top + line + 2);
            }
            var rc = new Native.RECT
            {
                Left = 0, Top = top,
                Right = Math.Max(1, _clientW),
                Bottom = Math.Max(top + 1, bottom),
            };
            Native.SendMessageRect(_hwnd, Native.EM_SETRECT, IntPtr.Zero, ref rc);
        }

        private string SanitizeSingleLine(string text)
        {
            text ??= "";
            if (_multiLine) return text;
            return text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }

        private string ReadText()
        {
            if (_hwnd == IntPtr.Zero) return "";
            int len = Native.GetWindowTextLengthW(_hwnd);
            var sb = new StringBuilder(Math.Max(16, len + 1));
            Native.GetWindowTextW(_hwnd, sb, sb.Capacity);
            return SanitizeSingleLine(sb.ToString());
        }

        private void HideCore()
        {
            if (_hiding) return;
            _hiding = true;
            _editing = false;
            _request = null;
            if (_hostHwnd != IntPtr.Zero) Native.ShowWindow(_hostHwnd, Native.SW_HIDE);
            RestoreFocus();
            _hiding = false;
        }

        private void FocusHost()
        {
            if (_hostHwnd == IntPtr.Zero) return;
            IntPtr main = _engine._windows.Count > 0 ? _engine._windows[0].Hwnd : IntPtr.Zero;
            _mainNoActivate = false;
            if (main != IntPtr.Zero)
            {
                long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_NOACTIVATE) != 0)
                {
                    _mainNoActivate = true;
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
                Native.SetForegroundWindow(_hostHwnd);
                Native.SetFocus(_hwnd);
                Native.AttachThreadInput(myTid, fgTid, false);
            }
            else
            {
                Native.SetForegroundWindow(_hostHwnd);
                Native.SetFocus(_hwnd);
            }
            Native.SetWindowPos(_hostHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private void RestoreFocus()
        {
            bool had = _mainNoActivate;
            _mainNoActivate = false;
            if (had && _engine._windows.Count > 0)
            {
                IntPtr main = _engine._windows[0].Hwnd;
                long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(main, Native.GWL_EXSTYLE,
                    new IntPtr(ex | Native.WS_EX_NOACTIVATE));
                Native.SetWindowPos(main, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            if (_engine.KeyboardMode && _engine._windows.Count > 0)
                Native.SetFocus(_engine._windows[0].Hwnd);
        }

        private IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_CTLCOLOREDIT:
                {
                    IntPtr hdc = wParam;
                    Native.SetTextColor(hdc, _textColor);
                    Native.SetBkColor(hdc, _brushColor);
                    return _brush != IntPtr.Zero ? _brush : Native.GetStockObject(0 /*WHITE_BRUSH*/);
                }
                case Native.WM_ERASEBKGND:
                    return new IntPtr(1);
                case Native.WM_SIZE:
                {
                    int w = (int)(lParam.ToInt64() & 0xFFFF);
                    int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    if (_hwnd != IntPtr.Zero)
                        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0,
                            Math.Max(1, w), Math.Max(1, h), Native.SWP_NOZORDER);
                    return IntPtr.Zero;
                }
                case 0x0007 /*WM_SETFOCUS*/:
                    if (_hwnd != IntPtr.Zero) Native.SetFocus(_hwnd);
                    return IntPtr.Zero;
                case Native.WM_CLOSE:
                    Cancel();
                    return IntPtr.Zero;
            }
            return Native.CallWindowProc(_hostOldProc, hWnd, msg, wParam, lParam);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_CTLCOLOREDIT:
                {
                    IntPtr hdc = wParam;
                    Native.SetTextColor(hdc, _textColor);
                    Native.SetBkColor(hdc, _brushColor);
                    return _brush != IntPtr.Zero ? _brush : Native.GetStockObject(0 /*WHITE_BRUSH*/);
                }
                case Native.WM_KEYDOWN:
                {
                    int vk = wParam.ToInt32();
                    if (vk == 0x1B /*Esc*/) { Cancel(); return IntPtr.Zero; }
                    if (vk == 0x09 /*Tab*/)
                    {
                        TextInputRequest req = _request;
                        bool shift = (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;
                        Commit();
                        req?.OnTab?.Invoke(shift);
                        return IntPtr.Zero;
                    }
                    if (vk == 0x0D /*Enter*/)
                    {
                        bool multiline = _multiLine;
                        bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
                        if (multiline && !ctrl) break;     // 多行：Enter 换行；Ctrl+Enter 提交
                        Commit();
                        return IntPtr.Zero;
                    }
                    break;
                }
                case Native.WM_CHAR:
                {
                    int ch = wParam.ToInt32();
                    bool multiline = _multiLine;
                    if (ch == 0x1B) return IntPtr.Zero;
                    if (!multiline && (ch == 0x0D || ch == 0x0A)) return IntPtr.Zero;
                    break;
                }
                case Native.WM_KILLFOCUS:
                    if (!_hiding && _editing)
                    {
                        if (_request?.CommitOnFocusLost != false) Commit();
                        else Cancel();
                    }
                    return IntPtr.Zero;
                case Native.WM_GETDLGCODE:
                    return new IntPtr(Native.DLGC_WANTALLKEYS);
                case Native.WM_CLOSE:
                    Cancel();
                    return IntPtr.Zero;
            }
            return Native.CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
        }

        private static uint ColorRef(Color4 c)
        {
            uint r = (uint)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
            uint g = (uint)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
            uint b = (uint)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
            return r | (g << 8) | (b << 16);
        }
    }
}
