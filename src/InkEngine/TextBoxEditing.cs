using System.Globalization;
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
    private static uint _textBoxEditTextColor;
    private static bool _textBoxEditLightText;
    private static IntPtr _msftedit;
    private static bool _richEditReady;
    private static InkEngine _textBoxEditOwner;
    private Native.WndProcDelegate _textBoxEditWndProc;
    private IntPtr _textBoxEditOldProc;
    private bool _textBoxEditHadNoActivate;
    private bool _textBoxEditing;
    private Stroke _textBoxEditStroke;
    private IntPtr _textBoxEditFont;
    private float _textBoxEditFontPx;
    private double _lastTextBoxClickMs = double.NegativeInfinity;
    private Stroke _lastTextBoxClickStroke;
    private bool _textBoxEditWasNew;
    private bool _textBoxEditSuppressChange;
    private string _textBoxEditOriginalText = "";
    private float _textBoxEditOriginalW, _textBoxEditOriginalH;
    private bool _textBoxEditOriginalAuto, _textBoxEditOriginalAutoWidth;
    private RectF _textBoxEditOriginalBounds;
    private Vector2 _textBoxEditOriginalOrigin;
    private Matrix3x2 _textBoxEditOriginalXf = Matrix3x2.Identity;
    private float _textFontSize = 15f;
    private Color4 _textColor = InkPalette.PenBand[0].Color;
    private bool _textPrefsLoaded;
    private bool _textColorExplicit;

    internal bool TextBoxEditing => _textBoxEditing;

    /// <summary>文本工具当前该显示的字号/颜色：选中了普通文本框就用它的，否则用默认值。</summary>
    private TextBoxData SelectedPlainTextBox
    {
        get
        {
            foreach (var s in Doc.Selected)
                if (s.Kind == StrokeKind.TextBox && s.TextBox is { Role: TextBoxRole.Normal } b)
                    return b;
            return null;
        }
    }

    internal float TextToolFontSize => SelectedPlainTextBox?.FontSize ?? _textFontSize;
    internal Color4 TextToolColor
    {
        get
        {
            var b = SelectedPlainTextBox;
            if (b != null) return b.Ink;
            EnsureTextPrefs();
            return _textColorExplicit ? _textColor : DefaultTextColor();
        }
    }

    private void EnsureTextPrefs()
    {
        if (_textPrefsLoaded) return;
        _textPrefsLoaded = true;
        if (float.TryParse(GetUiPref("textFont"), out float fs))
            _textFontSize = Math.Clamp(fs, 8f, 72f);
        string saved = GetUiPref("textColor");
        if (!string.IsNullOrEmpty(saved))
        {
            _textColor = ParseColorPref(saved, _textColor);
            _textColorExplicit = GetUiPref("textColorExplicit") == "1";
        }
    }

    /// <summary>新建文本的默认颜色：没主动选过色时，浅板深字、深板白字。</summary>
    private Color4 DefaultTextColor()
    {
        EnsureTextPrefs();
        if (_textColorExplicit) return _textColor;
        float luma = 0.299f * BoardColor.R + 0.587f * BoardColor.G + 0.114f * BoardColor.B;
        return BoardOn && luma < 0.55f
            ? InkPalette.PenBand[7].Color
            : InkPalette.PenBand[0].Color;
    }

    internal void AdjustTextFontFromUi(float delta)
    {
        EnsureTextPrefs();
        var box = SelectedPlainTextBox;
        if (box == null)
        {
            _textFontSize = Math.Clamp(_textFontSize + delta, 8f, 72f);
            SetUiPref("textFont", _textFontSize.ToString(CultureInfo.InvariantCulture));
            NotifyUiStateChanged();
            return;
        }
        float before = box.FontSize;
        AdjustTextBoxFont(delta);
        if (MathF.Abs(box.FontSize - before) > 0.01f)
        {
            _textFontSize = box.FontSize;
            SetUiPref("textFont", _textFontSize.ToString(CultureInfo.InvariantCulture));
        }
        if (_textBoxEditing) RefreshTextBoxEditWindow(box);
        NotifyUiStateChanged();
    }

    internal void SetTextColorFromUi(Color4 color)
    {
        EnsureTextPrefs();
        _textColor = color;
        _textColorExplicit = true;
        SetUiPref("textColor", $"{color.R:F3},{color.G:F3},{color.B:F3}");
        SetUiPref("textColorExplicit", "1");
        bool changed = false;
        foreach (var s in Doc.Selected)
        {
            if (s.Kind != StrokeKind.TextBox || s.TextBox is not { Role: TextBoxRole.Normal }) continue;
            changed = true;
        }
        if (changed) SetSelectionColor(color);
        if (_textBoxEditing && _textBoxEditStroke?.TextBox is { Role: TextBoxRole.Normal } eb)
        {
            _textBoxEditTextColor = ((uint)(eb.Ink.R * 255f) & 0xFF)
                                  | (((uint)(eb.Ink.G * 255f) & 0xFF) << 8)
                                  | (((uint)(eb.Ink.B * 255f) & 0xFF) << 16);
            _textBoxEditLightText = 0.299f * eb.Ink.R + 0.587f * eb.Ink.G + 0.114f * eb.Ink.B > 0.65f;
            ApplyRichEditFormat(_textBoxEditStroke);
            Native.InvalidateRect(_textBoxEditHwnd, IntPtr.Zero, true);
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void CommitTextEditFromUi()
    {
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        else NotifyUiStateChanged();
    }

    internal void InsertTextBoxFromUi()
    {
        HideAiPromptEdit();
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        else HideTextBoxEdit();
        // 文本是"插入动作"，不是模式：插完切回选择，方便马上拖动/改宽高。
        if (Tool != Tool.Marquee) SetToolFromUi(Tool.Marquee);
        var s = CreatePlainTextBox("");
        Doc.SelectOnly(new[] { s });
        BeginTextBoxEdit(s, isNew: true);
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 文本工具的按下：先找文本框（文本优先于墨迹），点已有文字 = 选中/再点编辑；
    /// 点空白 = 在点击处新建并编辑。点到其它对象返回 false，让调用方转成选择工具的点选。
    /// </summary>
    internal bool TextToolPointerDown(float screenX, float screenY)
    {
        var hit = TextBoxAt(screenX, screenY);
        if (hit?.TextBox != null)
        {
            if (hit.TextBox.Role == TextBoxRole.AiQuestion)
            {
                // AI 题卡不是普通文字：交给选择工具，别在它上面再叠一个文本框。
                SetToolFromUi(Tool.Marquee);
                Doc.SelectOnly(new[] { hit });
                _dirty = true;
                NotifyUiStateChanged();
                return true;
            }
            bool selected = Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], hit);
            if (!selected)
            {
                Doc.SelectOnly(new[] { hit });
                _dirty = true;
                NotifyUiStateChanged();
                return true;
            }
            BeginTextBoxEdit(hit);
            return true;
        }

        float cx = screenX, cy = screenY - ViewOffsetY;
        var other = Doc.HitObjectAt(cx, cy, ClickToleranceLogical * DpiScale);
        if (other != null)
        {
            // 文本工具下点到墨迹/图形：退回选择工具，让标准点选接住这一下。
            SetToolFromUi(Tool.Marquee);
            return false;
        }

        var s = CreatePlainTextBoxAt(cx, cy);
        Doc.SelectOnly(new[] { s });
        BeginTextBoxEdit(s, isNew: true);
        _dirty = true;
        NotifyUiStateChanged();
        return true;
    }

    /// <summary>自检/摆样：插入一个已有文字的普通文本框，不进入编辑。</summary>
    internal void InsertPlainTextBoxForTest(string text)
    {
        var s = CreatePlainTextBox(text ?? "");
        Doc.SelectOnly(new[] { s });
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 文本工具：在点击处新建一个一行高、较窄的普通文本框（入文档，
    /// 由调用方选中并进入编辑）。位置用**画布坐标**。
    /// </summary>
    private Stroke CreatePlainTextBoxAt(float canvasX, float canvasY)
    {
        EnsureTextPrefs();
        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.75f, DpiScale);
        float w = MathF.Max(80f * u, 3f * _textFontSize * u);
        float h = 60f * u;
        float margin = 16f * u;
        float canvasBottom = screen.MaxY - ViewOffsetY;
        float canvasRight = screen.MaxX;

        // 点击点靠近右/下边缘时，先给“至少能长几行”的空间；不然一点就只剩十几像素。
        float roomW = MathF.Min(420f * u, (screen.MaxX - screen.MinX) * 0.45f);
        float x = MathF.Min(canvasX,
            MathF.Max(screen.MinX + margin, canvasRight - margin - roomW));
        float lineH = _textFontSize * u * 1.55f;
        float roomH = 3f * lineH + 36f * u;
        float y = MathF.Min(canvasY,
            MathF.Max(screen.MinY - ViewOffsetY + margin, canvasBottom - margin - roomH));

        var data = TextBoxData.CreatePlain("", u, w, h);
        data.FontSize = _textFontSize;
        data.AutoWidth = true;
        data.AutoHeight = true;
        var color = DefaultTextColor();
        var s = new Stroke
        {
            Tool = Tool.Marquee,
            Kind = StrokeKind.TextBox,
            TextBox = data,
            Color = color,
            Width = 0f,
        };
        data.Ink = color;
        s.SetTextBoxRect(new Vector2(x, y), w, data.Height);
        ClampTextBoxIntoView(s);
        Doc.AddStroke(s);
        return s;
    }

    private Stroke CreatePlainTextBox(string text)
    {
        EnsureTextPrefs();
        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.75f, DpiScale);
        float w = MathF.Min(420f * u, (screen.MaxX - screen.MinX) * 0.55f);
        float h = 60f * u;
        float x = (screen.MinX + screen.MaxX) * 0.5f - w * 0.5f;
        float y = (screen.MinY + screen.MaxY) * 0.5f - ViewOffsetY - h * 0.5f;

        var data = TextBoxData.CreatePlain(text, u, w, h);
        data.AutoWidth = true;
        var s = new Stroke
        {
            Tool = Tool.Marquee,
            Kind = StrokeKind.TextBox,
            TextBox = data,
            Color = DefaultTextColor(),
            Width = 0f,
        };
        data.Ink = s.Color;

        // 第一次按内容定宽高：宽 = 文本自然宽（夹在最长 token 和屏幕 90% 之间），
        // 高 = 内容高。空框保持默认宽 + 最小高度，等第一次粘贴/输入再自适应。
        if (!string.IsNullOrWhiteSpace(text))
        {
            float maxW = MathF.Max(80f * u, (screen.MaxX - screen.MinX) * 0.9f);
            float maxH = MathF.Max(80f * u, (screen.MaxY - screen.MinY) * 0.9f);
            data.FitToContent(maxW, maxH, fitWidth: true);
            x = (screen.MinX + screen.MaxX) * 0.5f - data.Width * 0.5f;
            y = (screen.MinY + screen.MaxY) * 0.5f - ViewOffsetY - data.Height * 0.5f;
            s.SetTextBoxRect(new Vector2(x, y), data.Width, data.Height);
        }
        else
        {
            _ = data.EnsureLayout();
            s.SetTextBoxRect(new Vector2(x, y), w, data.Height);
        }

        // 先排版，再入文档：AddStroke 的脏区和空间索引都按**最终包围盒**登记。
        // 以前先 Add 再长高，低处的字不在脏区里，删除后会残留最后几行（用户实测）。
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

    internal void BeginTextBoxEdit(Stroke s, bool isNew = false)
    {
        if (s?.TextBox == null || s.TextBox.Role != TextBoxRole.Normal) return;
        EnsureTextBoxEditWindow();
        if (_textBoxEditHwnd == IntPtr.Zero) return;

        _textBoxEditStroke = s;
        _textBoxEditWasNew = isNew;
        _textBoxEditOriginalText = s.TextBox.Text ?? "";
        _textBoxEditOriginalW = s.TextBox.Width;
        _textBoxEditOriginalH = s.TextBox.Height;
        _textBoxEditOriginalAuto = s.TextBox.AutoHeight;
        _textBoxEditOriginalAutoWidth = s.TextBox.AutoWidth;
        _textBoxEditOriginalBounds = s.PaddedBounds;
        _textBoxEditOriginalOrigin = s.Points.Count > 0
            ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero;
        _textBoxEditOriginalXf = s.Transform;
        _textBoxEditing = true;
        // 原生编辑窗总是从顶部显示：把内容也回到顶部，编辑和显示同一位置。
        if (s.TextBox.ScrollY > 0.5f)
        {
            s.TextBox.ScrollY = 0f;
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
        }
        _textBoxEditSuppressChange = true;
        Native.SetWindowTextW(_textBoxEditHwnd, s.TextBox.Text ?? "");
        _textBoxEditSuppressChange = false;
        _textBoxEditTextColor = ((uint)(s.Color.R * 255f) & 0xFF)
                              | (((uint)(s.Color.G * 255f) & 0xFF) << 8)
                              | (((uint)(s.Color.B * 255f) & 0xFF) << 16);
        _textBoxEditLightText = 0.299f * s.Color.R + 0.587f * s.Color.G + 0.114f * s.Color.B > 0.65f;
        var font = EnsureTextBoxEditFont(s.TextBox.FontSize * s.TextBox.Unit);
        Native.SendMessage(_textBoxEditHwnd, Native.WM_SETFONT, font, new IntPtr(1));
        ApplyRichEditFormat(s);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETLIMITTEXT, new IntPtr(20000), IntPtr.Zero);
        // 光标放末尾，**不要全选**：双击已经存在的文本框后随手打字，
        // 全选会让第一个字把整段内容替换掉（老师最怕的误操作）。
        int len = Native.GetWindowTextLengthW(_textBoxEditHwnd);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, new IntPtr(len), new IntPtr(len));

        RefreshTextBoxEditWindow(s.TextBox);
        FocusTextBoxEditWindow();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 把原生编辑窗重新铺到卡片上（改字号、输入长高、改颜色后都调）。
    /// 白底 = **蓝框里的卡片本身**：宿主按卡片矩形，EDIT 子窗铺满宿主，
    /// 再用 EM_SETRECT 把文字缩进 18 逻辑像素——白底和蓝框严格同尺寸。
    /// </summary>
    private void RefreshTextBoxEditWindow(TextBoxData box)
    {
        if (!_textBoxEditing || _textBoxEditStroke == null || box == null) return;
        var s = _textBoxEditStroke;
        s.SyncTextBoxBounds();
        var b = s.PaddedBounds;
        float unit = MathF.Max(0.5f, box.Unit);
        float pad = 18f * unit;
        int x = (int)MathF.Round(b.MinX + 2f);
        int y = (int)MathF.Round(b.MinY + 2f + ViewOffsetY);
        int w = Math.Max(60, (int)MathF.Round(b.MaxX - b.MinX - 4f));
        int h = Math.Max(40, (int)MathF.Round(b.MaxY - b.MinY - 4f));
        Native.SetWindowPos(_textBoxEditHost, Native.HWND_TOPMOST, x, y, w, h,
                            Native.SWP_SHOWWINDOW);
        Native.SetWindowPos(_textBoxEditHwnd, IntPtr.Zero, 0, 0, w, h,
                            Native.SWP_SHOWWINDOW);
        var textRect = new Native.RECT
        {
            Left = (int)MathF.Round(pad),
            Top = (int)MathF.Round(pad),
            Right = Math.Max((int)MathF.Round(pad) + 40, w - (int)MathF.Round(pad)),
            Bottom = Math.Max((int)MathF.Round(pad) + 20, h - (int)MathF.Round(pad)),
        };
        Native.SendMessageRect(_textBoxEditHwnd, Native.EM_SETRECT, IntPtr.Zero, ref textRect);
        Native.InvalidateRect(_textBoxEditHwnd, IntPtr.Zero, true);
        Native.UpdateWindow(_textBoxEditHwnd);
    }

    /// <summary>
    /// 输入中的实时增长：第一次输入时宽度跟着文字长，到屏幕右沿后换行、高度继续长；
    /// 之后（AutoWidth=false）不再跟着长，保持用户看到的尺寸。
    /// </summary>
    private void OnTextBoxEditChanged()
    {
        if (!_textBoxEditing || _textBoxEditSuppressChange) return;
        var s = _textBoxEditStroke;
        var box = s?.TextBox;
        if (box == null || box.Role != TextBoxRole.Normal || !box.AutoWidth) return;

        string text = ReadTextBoxEditText();
        if (string.Equals(box.Text, text, StringComparison.Ordinal)) return;

        var old = s.PaddedBounds;
        Vector2 origin = s.Points.Count > 0
            ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero;
        box.Text = text;
        box.InvalidateLayout();

        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.5f, box.Unit);
        float maxW = MathF.Min((screen.MaxX - screen.MinX) * 0.9f,
                               screen.MaxX - origin.X - 16f * u);
        float maxH = MathF.Max(60f * u,
                               screen.MaxY - ViewOffsetY - origin.Y - 16f * u);
        box.GrowToContent(MathF.Max(120f * u, maxW), maxH);
        s.SetTextBoxRect(origin, box.Width, box.Height);
        Doc.RefreshStrokeGrid(s);
        Doc.Dirty.Add(old);
        Doc.Dirty.Add(s.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
        RefreshTextBoxEditWindow(box);
    }

    private string ReadTextBoxEditText()
    {
        if (_textBoxEditHwnd == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(32768);
        Native.GetWindowTextW(_textBoxEditHwnd, sb, sb.Capacity);
        return sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>自检：直接改原生 EDIT 里的文字（模拟粘贴），不经过键盘。</summary>
    internal void SetTextBoxEditTextForTest(string text)
    {
        if (_textBoxEditing && _textBoxEditHwnd != IntPtr.Zero)
        {
            _textBoxEditSuppressChange = true;
            Native.SetWindowTextW(_textBoxEditHwnd, text ?? "");
            _textBoxEditSuppressChange = false;
            OnTextBoxEditChanged();
        }
    }

    internal void EndTextBoxEditFromUi(bool accept)
    {
        if (!_textBoxEditing) return;
        var s = _textBoxEditStroke;
        // 输入中会把文字实时写回 TextBox.Text（为了边打边长）；撤销用的“原文”
        // 必须从 Begin 时单独存，不能从当前值读——否则中间改过之后这里会以为没变化。
        string before = _textBoxEditOriginalText;
        string text = before;
        if (accept && _textBoxEditHwnd != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(32768);
            Native.GetWindowTextW(_textBoxEditHwnd, sb, sb.Capacity);
            text = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        }
        bool wasNew = _textBoxEditWasNew;
        bool emptyNew = accept && wasNew && string.IsNullOrWhiteSpace(text);
        _textBoxEditStroke = null;
        _textBoxEditWasNew = false;
        HideTextBoxEdit();
        if (emptyNew && s != null)
        {
            // 新建后没输入就结束：不要留下一个透明、看不见却能选中的空框。
            var old = s.PaddedBounds;
            Doc.RemoveStroke(s);
            Doc.DropLastAddStrokeIf(s);
            Doc.Selected.Remove(s);
            Doc.Dirty.Add(old);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            _dirty = true;
            NotifyUiStateChanged();
            return;
        }
        if (s != null && s.TextBox != null)
        {
            var oldBounds = _textBoxEditOriginalBounds;
            float oldW = _textBoxEditOriginalW, oldH = _textBoxEditOriginalH;
            bool oldAuto = _textBoxEditOriginalAuto;
            var oldOrigin = _textBoxEditOriginalOrigin;
            var oldXf = _textBoxEditOriginalXf;
            s.TextBox.Text = text;
            s.TextBox.InvalidateLayout();
            // 第一次收编辑 = 最后一次自适应：宽按文本自然宽、高按内容；
            // 之后 AutoHeight=false，完全由用户拖。
            if (accept && s.TextBox.Role == TextBoxRole.Normal && s.TextBox.AutoHeight)
            {
                var screen = ScreenRectPhysical();
                float u = MathF.Max(0.5f, s.TextBox.Unit);
                float maxW = MathF.Max(80f * u, MathF.Min(
                    (screen.MaxX - screen.MinX) * 0.9f,
                    screen.MaxX - oldOrigin.X - 16f * u));
                float maxH = MathF.Max(80f * u, MathF.Min(
                    (screen.MaxY - screen.MinY) * 0.9f,
                    screen.MaxY - ViewOffsetY - oldOrigin.Y - 16f * u));
                s.TextBox.FitToContent(maxW, maxH, fitWidth: !string.IsNullOrWhiteSpace(text));
                // 变宽/变窄都保持原来的中心，不要只往右长。
                // ???????????????????????????????
                // ????????????????????
                if (!wasNew && s.Points.Count > 0 && MathF.Abs(s.TextBox.Width - oldW) > 0.5f)
                {
                    var p0 = s.Points[0];
                    s.SetPoint(0, new Vector2(p0.X - (s.TextBox.Width - oldW) * 0.5f, p0.Y));
                }
            }
            s.SyncTextBoxBounds();
            ClampTextBoxIntoView(s);
            Doc.RefreshStrokeGrid(s);
            if (accept && s.TextBox.Role == TextBoxRole.Normal)
            {
                s.TextBox.AutoHeight = false;
                s.TextBox.AutoWidth = false;
            }
            Doc.Dirty.Add(oldBounds);
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            if (!string.Equals(before, text, StringComparison.Ordinal))
            {
                var newOrigin = s.Points.Count > 0
                    ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero;
                Doc.CommitInteractive(new TextBoxTextAction(
                    s, before, text, oldBounds, s.PaddedBounds,
                    oldW, oldH, oldAuto, _textBoxEditOriginalAutoWidth, oldOrigin, oldXf,
                    s.TextBox.Width, s.TextBox.Height, s.TextBox.AutoHeight,
                    s.TextBox.AutoWidth, newOrigin, s.Transform));
            }
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

        if (_msftedit == IntPtr.Zero)
        {
            _msftedit = Native.LoadLibraryW("Msftedit.dll");
            _richEditReady = _msftedit != IntPtr.Zero;
        }

        // WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN
        // ??????????? 17px ????????????????????
        long style = 0x40000000L | 0x10000000L | 0x0004L | 0x0040L | 0x1000L;
        string cls = _richEditReady ? "RICHEDIT50W" : "EDIT";
        _textBoxEditHwnd = Native.CreateWindowEx(0, cls, "InkTeachTextBox",
            style, 0, 0, 1, 1, _textBoxEditHost, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_textBoxEditHwnd == IntPtr.Zero && _richEditReady)
        {
            _richEditReady = false;
            _textBoxEditHwnd = Native.CreateWindowEx(0, "EDIT", "InkTeachTextBox",
                style, 0, 0, 1, 1, _textBoxEditHost, IntPtr.Zero, _hInstance, IntPtr.Zero);
        }
        if (_textBoxEditHwnd == IntPtr.Zero) return;
        // 明确关掉横向滚动：RichEdit 默认若不关，长行会一直往右长、不换行，
        // 和下面文本框的排版（到边就折行）对不上。
        long editStyle = Native.GetWindowLongPtr(_textBoxEditHwnd, -16 /* GWL_STYLE */).ToInt64();
        editStyle &= ~0x80L;                    // ES_AUTOHSCROLL
        Native.SetWindowLongPtr(_textBoxEditHwnd, -16 /* GWL_STYLE */, new IntPtr(editStyle));
        _textBoxEditOwner = this;
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
                Native.FillRect(wParam, ref rc,
                    Native.GetStockObject(_textBoxEditLightText ? 2 /* GRAY_BRUSH */ : 0 /* WHITE_BRUSH */));
                return new IntPtr(1);
            }
            case Native.WM_CTLCOLOREDIT:
                Native.SetBkColor(wParam, _textBoxEditLightText ? 0x00808080u : 0x00FFFFFFu);
                Native.SetTextColor(wParam, _textBoxEditTextColor);
                return Native.GetStockObject(_textBoxEditLightText ? 2 /* GRAY_BRUSH */ : 0 /* WHITE_BRUSH */);
            case 0x0111 /* WM_COMMAND */:
                // RichEdit 的文字变化通知发给父窗口；这里转回引擎做“边打边长”。
                if (_textBoxEditOwner != null
                    && lParam == _textBoxEditOwner._textBoxEditHwnd
                    && ((int)(wParam.ToInt64() >> 16) & 0xFFFF) == 0x0300 /* EN_CHANGE */)
                {
                    _textBoxEditOwner.OnTextBoxEditChanged();
                    return IntPtr.Zero;
                }
                break;
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
                Native.FillRect(wParam, ref rc,
                    Native.GetStockObject(_textBoxEditLightText ? 2 /* GRAY_BRUSH */ : 0 /* WHITE_BRUSH */));
                return new IntPtr(1);
            }
            case Native.WM_KEYDOWN:
            {
                int vk = (int)wParam.ToInt64();
                bool ctrl = (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0;
                if (vk == 0x0D && ctrl) { EndTextBoxEditFromUi(true); return IntPtr.Zero; }
                // Esc = 结束输入（提交），不是丢掉刚打的字；要反悔用 Ctrl+Z。
                if (vk == 0x1B) { EndTextBoxEditFromUi(true); return IntPtr.Zero; }
                break;
            }
            case 0x0302 /* WM_PASTE */:
                if (PastePlainText(hWnd)) return IntPtr.Zero;
                break;
            case Native.WM_KILLFOCUS:
                if (_textBoxEditing) EndTextBoxEditFromUi(true);
                return IntPtr.Zero;
            case Native.WM_GETDLGCODE:
                return new IntPtr(Native.DLGC_WANTALLKEYS);
        }
        return Native.CallWindowProc(_textBoxEditOldProc, hWnd, msg, wParam, lParam);
    }

    private static bool PastePlainText(IntPtr hWnd)
    {
        if (!Native.OpenClipboard(hWnd)) return false;
        try
        {
            IntPtr h = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return false;
            IntPtr p = Native.GlobalLock(h);
            if (p == IntPtr.Zero) return false;
            string text;
            try
            {
                text = System.Runtime.InteropServices.Marshal.PtrToStringUni(p) ?? "";
            }
            finally
            {
                Native.GlobalUnlock(h);
            }
            if (text.Length == 0) return true;
            // 只取纯文本：避免从浏览器/Word 粘进来改掉字体和行距。
            Native.SendMessageString(hWnd, Native.EM_REPLACESEL, new IntPtr(1), text);
            Native.InvalidateRect(hWnd, IntPtr.Zero, true);
            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private void ApplyRichEditFormat(Stroke s)
    {
        if (!_richEditReady || s?.TextBox == null) return;
        float fontPx = MathF.Max(6f, s.TextBox.FontSize * MathF.Max(0.5f, s.TextBox.Unit));
        float dpi = MathF.Max(1f, DpiScale);
        int fontTwips = Math.Max(80, (int)MathF.Round(fontPx * 15f / dpi));

        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETBKGNDCOLOR,
                           IntPtr.Zero, new IntPtr(_textBoxEditLightText ? 0x00808080 : 0x00FFFFFF));

        var cf = new Native.CHARFORMAT2W
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CHARFORMAT2W>(),
            dwMask = Native.CFM_FACE | Native.CFM_SIZE | Native.CFM_COLOR | Native.CFM_CHARSET,
            yHeight = fontTwips,
            crTextColor = (int)_textBoxEditTextColor,
            bCharSet = 1,
            szFaceName = "Microsoft YaHei UI",
        };
        Native.SendMessageCharFormat(_textBoxEditHwnd, Native.EM_SETCHARFORMAT,
                                     new IntPtr(Native.SCF_ALL), ref cf);

        // 行距和显示端一致：1.55 × 字号。
        // ⚠ RichEdit 的“多倍行距”是按它自己的默认行高乘的，不是按字号；
        // 之前写 155（rule=5）= 7.75 行，长文本只看得见第一行。
        // 这里改成 **固定值**（rule=3），twips 直接按 1.55×字号 算，和排版同源。
        int lineTwips = Math.Max(80, (int)MathF.Round(fontPx * 1.55f * 15f / dpi));
        var pf = new Native.PARAFORMAT2
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.PARAFORMAT2>(),
            dwMask = Native.PFM_LINESPACING,
            dyLineSpacing = lineTwips,
            bLineSpacingRule = 3,
            rgxTabs = new int[32],
        };
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
        Native.SendMessageParaFormat(_textBoxEditHwnd, Native.EM_SETPARAFORMAT, IntPtr.Zero, ref pf);

        int len = Native.GetWindowTextLengthW(_textBoxEditHwnd);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, new IntPtr(len), new IntPtr(len));
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
    private readonly float _beforeW, _beforeH, _afterW, _afterH;
    private readonly bool _beforeAuto, _afterAuto;
    private readonly bool _beforeAutoWidth, _afterAutoWidth;
    private readonly Vector2 _beforeOrigin, _afterOrigin;
    private readonly Matrix3x2 _beforeXf, _afterXf;

    public TextBoxTextAction(Stroke s, string before, string after,
                             RectF beforeBounds, RectF afterBounds,
                             float beforeW, float beforeH, bool beforeAuto, bool beforeAutoWidth,
                             Vector2 beforeOrigin, Matrix3x2 beforeXf,
                             float afterW, float afterH, bool afterAuto, bool afterAutoWidth,
                             Vector2 afterOrigin, Matrix3x2 afterXf)
    {
        _stroke = s;
        _before = before;
        _after = after;
        _beforeBounds = beforeBounds;
        _afterBounds = afterBounds;
        _beforeW = beforeW; _beforeH = beforeH; _beforeAuto = beforeAuto;
        _beforeAutoWidth = beforeAutoWidth;
        _beforeOrigin = beforeOrigin; _beforeXf = beforeXf;
        _afterW = afterW; _afterH = afterH; _afterAuto = afterAuto;
        _afterAutoWidth = afterAutoWidth;
        _afterOrigin = afterOrigin; _afterXf = afterXf;
    }

    public override int HeldStrokes => 1;
    public override RectF AffectedBefore => _beforeBounds;
    public override RectF AffectedAfter => _afterBounds;

    public override void Undo(InkDocument doc)
        => Apply(doc, _before, _beforeW, _beforeH, _beforeAuto, _beforeAutoWidth, _beforeOrigin, _beforeXf);
    public override void Redo(InkDocument doc)
        => Apply(doc, _after, _afterW, _afterH, _afterAuto, _afterAutoWidth, _afterOrigin, _afterXf);

    private void Apply(InkDocument doc, string text, float w, float h, bool auto, bool autoWidth,
                       Vector2 origin, Matrix3x2 xf)
    {
        var box = _stroke.TextBox;
        if (box == null) return;
        var old = _stroke.PaddedBounds;
        box.Text = text;
        box.Width = MathF.Max(1f, w);
        box.Height = MathF.Max(1f, h);
        box.AutoHeight = auto;
        box.AutoWidth = autoWidth;
        box.InvalidateLayout();
        _stroke.Transform = xf;
        _stroke.SetTextBoxRect(origin, box.Width, box.Height);
        _stroke.SyncTextBoxBounds();
        doc.RefreshStrokeGrid(_stroke);
        doc.Dirty.Add(old);
        doc.Dirty.Add(_stroke.PaddedBounds);
        doc.StructureChangedSinceRender = true;
        doc.Version++;
    }
}
