using System;
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
    // 编辑底：**白板 = 当前板色**；文档/图片上退而求其次，用一块很淡的近白
    //（原生 RichEdit 给不了真透明，见 RefreshTextBoxEditWindow 的注释）。
    private static uint _textBoxEditBackColor = 0x00FFFFFF;
    private static IntPtr _textBoxEditBgBrush;
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
    // 双击进入编辑**不在按下时立即发生**：等松手时看有没有拖动。
    // 否则"选中后再按住拖"会被当成双击，直接进编辑、拖不动。
    private bool _pendingTextEdit;
    private Stroke _pendingTextEditStroke;
    private float _pendingTextEditX, _pendingTextEditY;
    private bool _textBoxEditWasNew;
    private bool _textBoxEditSuppressChange;
    private string _textBoxEditOriginalText = "";
    private int _textBoxEditCaretIndex = -1;
    private float _textBoxEditOriginalW, _textBoxEditOriginalH;
    private bool _textBoxEditOriginalAuto, _textBoxEditOriginalAutoWidth;
    private List<Color4> _textBoxEditOriginalLineColors = new();
    private List<TextBoxColorSpan> _textBoxEditOriginalColorSpans = new();
    private bool _textBoxEditColorTouched;
    /// <summary>RichEdit 的字符位置是否按“标准化后的 \n”计数（false = 按 \r\n）。</summary>
    private bool _textBoxEditCpNormalized;
    private RectF _textBoxEditOriginalBounds;
    private Vector2 _textBoxEditOriginalOrigin;
    private Matrix3x2 _textBoxEditOriginalXf = Matrix3x2.Identity;
    // 2026-11-01 按 ClassIn 的常用档定：默认 **18**，不再用 15。
    private float _textFontSize = 18f;
    private Color4 _textColor = InkPalette.PenBand[0].Color;
    private bool _textPrefsLoaded;
    private bool _textColorExplicit;

    /// <summary>
    /// 文字色带的 4 个色位：红 / 黑 / 白 / 自定义。和笔的 8 个色位是**两套独立状态**，
    /// 改文字色不会改笔色，反之亦然。窄带只显示前 3 个（见 FullUi.TextSwatchCount）。
    /// </summary>
    private readonly Color4[] _textSwatchColors =
    {
        InkPalette.PenBand[1].Color,   // 红
        InkPalette.PenBand[0].Color,   // 黑
        InkPalette.PenBand[7].Color,   // 白
        InkPalette.PenBand[2].Color,   // 自定义（默认蓝）
    };

    /// <summary>色带要显示的 4 个色位（快照用；界面只取前 N 个）。</summary>
    internal Color4[] TextSwatchColors => _textSwatchColors;

    /// <summary>取色板这次要改的色位；-1 = 按当前颜色反找。</summary>
    private int _textPickerSlot = -1;

    internal void BeginTextColorPickFromUi(int slot)
    {
        if (slot >= 0 && slot < _textSwatchColors.Length) _textPickerSlot = slot;
    }

    internal bool TextBoxEditing => _textBoxEditing;
    internal int TextBoxEditCaretForTest => _textBoxEditCaretIndex;

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

    /// <summary>
    /// 文字色带永远表示**下一次输入**的默认字号；选中文字的字号在文字操作条上改。
    /// （2026-11-01 用户拍板，照 ClassIn 的"文本工具面板 vs 选中操作条"分工。）
    /// </summary>
    /// <summary>选中对象里的第一个文本框（普通文字和 AI 题卡都算；操作条字号用）。</summary>
    private TextBoxData SelectedFontTextBox
    {
        get
        {
            foreach (var s in Doc.Selected)
                if (s.Kind == StrokeKind.TextBox && s.TextBox != null) return s.TextBox;
            return null;
        }
    }

    internal float TextToolFontSize
    {
        get
        {
            EnsureTextPrefs();
            return _textFontSize;
        }
    }

    /// <summary>文字色带永远表示**下一次输入**的默认颜色。</summary>
    internal Color4 TextToolColor
    {
        get
        {
            EnsureTextPrefs();
            return _textColorExplicit ? _textColor : DefaultTextColor();
        }
    }

    private void EnsureTextPrefs()
    {
        if (_textPrefsLoaded) return;
        _textPrefsLoaded = true;
        if (float.TryParse(GetUiPref("textFont"), out float fs))
        {
            _textFontSize = Math.Clamp(fs, 8f, 72f);
            // 老配置里的 15 是旧默认值，统一迁到新默认 18；其他不在六档里的值
            // 吸到最近的常用档，免得色带显示一个菜单里选不中的字号。
            if (MathF.Abs(_textFontSize - 15f) < 0.1f)
                _textFontSize = 18f;
            else
                _textFontSize = NearestTextFontSize(_textFontSize);
            SetUiPref("textFont", _textFontSize.ToString(CultureInfo.InvariantCulture));
        }

        bool hadSwatches = false;
        string savedSwatches = GetUiPref("textSwatches");
        if (!string.IsNullOrEmpty(savedSwatches))
        {
            var parts = savedSwatches.Split(';');
            for (int i = 0; i < _textSwatchColors.Length && i < parts.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(parts[i])) continue;
                _textSwatchColors[i] = ParseColorPref(parts[i], _textSwatchColors[i]);
                hadSwatches = true;
            }
        }

        string saved = GetUiPref("textColor");
        if (!string.IsNullOrEmpty(saved))
        {
            _textColor = ParseColorPref(saved, _textColor);
            _textColorExplicit = GetUiPref("textColorExplicit") == "1";
        }

        // 老配置迁移：以前只有一个"最近文字色"，没有色位表。
        // 如果它不等于红/黑/白，就放进第 4 格，色带才亮得起来。
        if (!hadSwatches && _textColorExplicit && FindTextSwatchSlot(_textColor) < 0)
            _textSwatchColors[3] = _textColor;
    }

    private static float NearestTextFontSize(float value)
    {
        float best = InkPalette.TextFontSizes[0], bestD = float.MaxValue;
        foreach (float v in InkPalette.TextFontSizes)
        {
            float d = MathF.Abs(v - value);
            if (d < bestD) { bestD = d; best = v; }
        }
        return best;
    }

    /// <summary>当前色在 4 个色位里的下标；不比任何一格接近时返回 -1。</summary>
    private int FindTextSwatchSlot(Color4 color)
    {
        for (int i = 0; i < _textSwatchColors.Length; i++)
            if (SameColor(_textSwatchColors[i], color)) return i;
        return -1;
    }

    private static bool SameColor(Color4 a, Color4 b)
        => MathF.Abs(a.R - b.R) < 0.01f
        && MathF.Abs(a.G - b.G) < 0.01f
        && MathF.Abs(a.B - b.B) < 0.01f;

    private void SaveTextSwatchPrefs()
    {
        var parts = new string[_textSwatchColors.Length];
        for (int i = 0; i < parts.Length; i++) parts[i] = FormatColorPref(_textSwatchColors[i]);
        SetUiPref("textSwatches", string.Join(";", parts));
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

    /// <summary>色带 A−/A+：只改**下一次输入**的默认字号，不碰选中的文字。</summary>
    internal void AdjustTextFontFromUi(float delta)
    {
        EnsureTextPrefs();
        _textFontSize = Math.Clamp(_textFontSize + delta, 8f, 72f);
        SetUiPref("textFont", _textFontSize.ToString(CultureInfo.InvariantCulture));
        NotifyUiStateChanged();
    }

    /// <summary>色带字号菜单：只改**下一次输入**的默认字号。</summary>
    internal void SetTextFontFromUi(float size)
    {
        EnsureTextPrefs();
        size = Math.Clamp(size, 8f, 72f);
        if (MathF.Abs(_textFontSize - size) < 0.01f) { NotifyUiStateChanged(); return; }
        _textFontSize = size;
        SetUiPref("textFont", _textFontSize.ToString(CultureInfo.InvariantCulture));
        NotifyUiStateChanged();
    }

    /// <summary>选中文字色带的第 <paramref name="slot"/> 格：只改默认色，不动作选中文字。</summary>
    internal void SelectTextSwatchFromUi(int slot)
    {
        EnsureTextPrefs();
        if (slot < 0 || slot >= _textSwatchColors.Length) return;
        _textColor = _textSwatchColors[slot];
        _textColorExplicit = true;
        SetUiPref("textColor", FormatColorPref(_textColor));
        SetUiPref("textColorExplicit", "1");
        SetUiPref("textSwatchSelected", slot.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine($"文字默认色：色位 {slot + 1}");
        if (_textBoxEditing) SetTextBoxEditColorFromUi(_textColor);
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 文字取色板提交：写回**当前色所在的那一格**（找不到就写第 4 格），
    /// 并把它设为下一次输入的默认色。只改默认值，不碰选中的文字。
    /// </summary>
    internal void SetTextColorFromUi(Color4 color)
    {
        EnsureTextPrefs();
        // 取色板是从哪一格打开的，就写回哪一格；没有记录才按当前色反找。
        int slot = _textPickerSlot;
        if (slot < 0 || slot >= _textSwatchColors.Length)
            slot = FindTextSwatchSlot(_textColor);
        if (slot < 0) slot = _textSwatchColors.Length - 1;
        _textSwatchColors[slot] = color;
        _textColor = color;
        _textColorExplicit = true;
        SetUiPref("textColor", FormatColorPref(color));
        SetUiPref("textColorExplicit", "1");
        SetUiPref("textSwatchSelected", slot.ToString(CultureInfo.InvariantCulture));
        SaveTextSwatchPrefs();
        if (_textBoxEditing) SetTextBoxEditColorFromUi(color);
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 文字操作条的字号下拉：改**当前选中的文本框**（不是下一次输入的默认值）。
    /// 走 <see cref="AdjustTextBoxFont"/> 那条路，自动高度和一步撤销都由它保证。
    /// </summary>
    internal void SetSelectedTextFontFromUi(float size)
    {
        // AI 题卡也有字号（题干/答案按同一个 FontSize 排版），只是颜色固定。
        var box = SelectedFontTextBox;
        if (box == null) { NotifyUiStateChanged(); return; }
        float delta = Math.Clamp(size, 8f, 72f) - box.FontSize;
        if (MathF.Abs(delta) < 0.01f) { NotifyUiStateChanged(); return; }
        AdjustTextBoxFont(delta);
        if (_textBoxEditing) RefreshTextBoxEditWindow(box);
        NotifyUiStateChanged();
    }

    /// <summary>文字操作条「字体」：换整个文本框的字体档（雅黑/楷体/宋体/黑体）。</summary>
    internal void SetSelectedTextFaceFromUi(int index)
    {
        if (index < 0 || index >= TextBoxFaces.All.Length) return;
        ApplySelectedTextStyle(TextBoxFaces.All[index].Face, null);
    }

    /// <summary>文字操作条「行距」：换整个文本框的行距倍数。</summary>
    internal void SetSelectedTextSpacingFromUi(float spacing)
        => ApplySelectedTextStyle(null, spacing);

    private void ApplySelectedTextStyle(TextBoxFace? face, float? spacing)
    {
        if (Doc.Selected.Count != 1) { NotifyUiStateChanged(); return; }
        var s = Doc.Selected[0];
        var box = s.TextBox;
        if (s.Kind != StrokeKind.TextBox || box == null) { NotifyUiStateChanged(); return; }

        var oldFace = box.Face;
        float oldSpacing = box.LineSpacing;
        var newFace = face ?? oldFace;
        float newSpacing = TextBoxFaces.ClampSpacing(spacing ?? oldSpacing);
        if (newFace == oldFace && MathF.Abs(newSpacing - oldSpacing) < 0.001f)
        {
            NotifyUiStateChanged();
            return;
        }

        var old = s.PaddedBounds;
        var origin = s.Points.Count > 0 ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero;
        float oldW = box.Width, oldH = box.Height, oldFont = box.FontSize;
        bool oldAuto = box.AutoHeight;

        box.Face = newFace;
        box.LineSpacing = newSpacing;
        box.InvalidateLayout();
        s.SyncTextBoxBounds();
        ClampTextBoxIntoView(s);
        Doc.RefreshStrokeGrid(s);
        Doc.Dirty.Add(old);
        Doc.Dirty.Add(s.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
        NotifyUiStateChanged();

        var act = new TextBoxStyleAction(s, origin, oldW, oldH, oldFont, oldAuto,
                                         oldFace, oldSpacing, old);
        Doc.CommitInteractive(act);
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
    /// 文本工具的按下：先找文本框（文本优先于墨迹），点已有文字 = **直接编辑**，
    /// 光标落在点击处；点空白 = 在点击处新建并编辑。
    /// 点到其它对象返回 false，让调用方转成选择工具的点选。
    /// </summary>
    internal bool TextToolPointerDown(float screenX, float screenY)
    {
        // 新的一下按下：上一次未兑现的双击作废（真正的双击会在下面重新 arm）。
        _pendingTextEdit = false;
        _pendingTextEditStroke = null;

        // 先让**选中文字的操作条 / 菜单 / 手柄**接住：它们可能落在文本框外面。
        // 不先判断的话，文本工具下点操作条会被当成"点空白"，又新建一个文本框。
        if (SelectionBarShown && Doc.Selected.Count > 0 && TextBoxChromeAt(screenX, screenY))
        {
            _ = TryBeginSelectionGesture(screenX, screenY - ViewOffsetY, shift: false, alt: false);
            _dirty = true;
            return true;
        }

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
            Doc.SelectOnly(new[] { hit });
            // 第一次点 = 选中（可拖动/改宽），双击才进入编辑。
            // **双击延迟到松手**：第二下按下去先交给选择手势，拖了就是移动，
            // 没拖才在松手时进编辑——这样"选中后马上按住拖"不会被双击吃掉。
            ArmTextBoxEditOnDoubleClick(hit, screenX, screenY);
            _ = TryBeginSelectionGesture(screenX, screenY - ViewOffsetY, shift: false, alt: false);
            _dirty = true;
            NotifyUiStateChanged();
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

    /// <summary>
    /// 指针是不是落在**选中文字的家具**上：操作条 / 收起圆钮 / 面板 / 取色板 / 手柄。
    /// 文本工具下这些都可能伸到文本框外面；不先接住就会被当成"点空白又新建"。
    /// </summary>
    private bool TextBoxChromeAt(float screenX, float screenY)
    {
        float x = screenX;
        float y = screenY - ViewOffsetY;
        if (BarDrawnCollapsed)
            return SelectionDotCanvasRect.Contains(x, y);

        if (SelectionBarCanvasRect.Contains(x, y)) return true;
        if (CustomColorOpen
            && SelectionHandles.PickContains(x, y, LiveSelectionFrame.CanvasAabb, UiUnit,
                                             FurnitureViewport, SelectionHandles.SwatchCount,
                                             SingleTextBoxSelection))
            return true;
        if (SelPanelOpen != SelPanel.None
            && SelectionHandles.PanelContains(x, y, LiveSelectionFrame.CanvasAabb, UiUnit,
                                              FurnitureViewport, SelPanelOpen,
                                              SelectionHandles.SwatchCount, SingleTextBoxSelection))
            return true;

        var frame = SelectionHandles.FrameOf(Doc.Selected);
        return SelectionHandles.HitTest(x, y, Doc.Selected, frame, UiUnit) != SelHandle.None;
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
    /// 把文本框的**纯平移**从 `Transform` 并进 `Points[0]`，并把 Transform 归零。
    ///
    /// 为什么：通用移动手势走的是 `Transform`，而文本框的改宽/输入/边缘夹取都按
    /// `Points[0]` 当画布坐标算。移动过的框再编辑时两边坐标系不一致，宽度会跳、
    /// 位置会漂。文本框不旋转、不缩放（字号走 FontSize），所以纯平移可以安全烘焙。
    /// 带旋转/缩放的组变换不动它，保持原样。
    /// </summary>
    internal static void BakeTextBoxTranslation(Stroke s)
    {
        if (s?.TextBox == null || s.Points.Count == 0) return;
        var t = s.Transform;
        bool pureTranslation = MathF.Abs(t.M11 - 1f) < 1e-4f
                            && MathF.Abs(t.M22 - 1f) < 1e-4f
                            && MathF.Abs(t.M12) < 1e-4f
                            && MathF.Abs(t.M21) < 1e-4f
                            && (MathF.Abs(t.M31) > 0.01f || MathF.Abs(t.M32) > 0.01f);
        if (!pureTranslation) return;

        var p0 = s.Points[0];
        var origin = new Vector2(p0.X + t.M31, p0.Y + t.M32);
        s.Transform = Matrix3x2.Identity;
        s.SetTextBoxRect(origin, s.TextBox.Width, s.TextBox.Height);
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
        float margin = 16f * u;
        // 排版的内边距是左右各 18×Unit；最小宽 = 两边内边距 + 一个字。
        // 不再固定 80，否则前两个字都挤在最小宽里，看起来像“输入一个字不变宽”。
        float minW = MathF.Max(24f * u, 36f * u + _textFontSize * u);
        float w = minW;                         // 初始只够一个字，之后逐字向右长
        float h = 60f * u;
        float canvasBottom = screen.MaxY - ViewOffsetY;
        float canvasRight = screen.MaxX - margin;

        // **落点就是左上角**（ClassIn 的输入起点）：不再先把框往左挪 420。
        // 右边不够时只把初始宽度收到最小可排版宽度；后续输入到边界会自动换行/整体左让，
        // 见 OnTextBoxEditChanged。
        float x = Math.Clamp(canvasX, screen.MinX + margin,
                             MathF.Max(screen.MinX + margin, canvasRight - minW));
        w = MathF.Max(minW, MathF.Min(w, canvasRight - x));

        // 纵向**落点就是框顶**，只保证第一行在屏幕内；继续往下写时由画布自动上滚
        // （KeepTextBoxEditInView），不预先把这个框往上顶三行。
        float lineH = _textFontSize * u * 1.55f;
        float minY = screen.MinY - ViewOffsetY + margin;
        float maxY = MathF.Max(minY, canvasBottom - margin - lineH);
        float y = Math.Clamp(canvasY, minY, maxY);

        var data = TextBoxData.CreatePlain("", u, w, h);
        data.FontSize = _textFontSize;
        // CreatePlain 有 120 的最小宽；这里按落点可用宽覆盖，右侧才不会被硬撑出屏幕。
        data.Width = MathF.Max(minW, w);
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
        s.SetTextBoxRect(new Vector2(x, y), data.Width, data.Height);
        ClampTextBoxIntoView(s);
        Doc.AddStroke(s);
        return s;
    }

    /// <summary>
    /// 识别到文本框用：在指定矩形里建一个普通文本框，墨色/字体/行距/逐行色都由调用方给。
    /// **先设样式再排版**——公式图是按墨色烘的，晚设颜色会出现“文字红、公式黑”。
    /// </summary>
    private Stroke CreatePlainTextBoxAt(string text, RectF target, Color4 ink,
                                        TextBoxFace face, float lineSpacing, float fontSizeLogical,
                                        IReadOnlyList<Color4> lineColors)
    {
        EnsureTextPrefs();
        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.75f, DpiScale);

        var data = TextBoxData.CreatePlain(text ?? "", u,
                                           MathF.Max(120f * u, target.MaxX - target.MinX), 60f * u);
        data.FontSize = Math.Clamp(fontSizeLogical, 8f, 72f);
        data.Face = face;
        data.LineSpacing = TextBoxFaces.ClampSpacing(lineSpacing);
        data.Ink = ink;
        data.Paper = true;
        data.AutoWidth = false;
        data.AutoHeight = true;
        if (lineColors != null)
            foreach (var c in lineColors) data.LineColors.Add(c);

        var s = new Stroke
        {
            Tool = Tool.Marquee,
            Kind = StrokeKind.TextBox,
            TextBox = data,
            Color = ink,
            Width = 0f,
        };

        float maxH = MathF.Max(120f * u, (screen.MaxY - screen.MinY) * 0.92f);
        data.FitToContent(data.Width, maxH, fitWidth: false);

        // 整页/多栏这种“框比原始版面高很多”的情况：按目标高度反推缩小字号，
        // 让结果别变成一条几米长的纸。最多两轮，避免和换行互相追。
        float targetH = MathF.Max(120f * u, target.MaxY - target.MinY);
        for (int pass = 0; pass < 2 && data.Height > targetH * 1.25f; pass++)
        {
            float k = Math.Clamp(targetH * 1.15f / MathF.Max(1f, data.Height), 0.45f, 1f);
            float next = MathF.Max(8f, data.FontSize * k);
            if (MathF.Abs(next - data.FontSize) < 0.5f) break;
            data.FontSize = next;
            data.InvalidateLayout();
            data.FitToContent(data.Width, maxH, fitWidth: false);
        }

        s.SetTextBoxRect(new Vector2(target.MinX, target.MinY), data.Width, data.Height);
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
        data.FontSize = _textFontSize;
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
            float maxW = MathF.Max(80f * u, screen.MaxX - screen.MinX - 32f * u);
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

    /// <summary>
    /// 双击普通文本框：只**记下**"松手时若不是拖动就进编辑"，不立刻进。
    /// AI 题卡暂不进入文字编辑。
    /// </summary>
    private void ArmTextBoxEditOnDoubleClick(Stroke s, float screenX, float screenY)
    {
        if (s?.TextBox == null || s.TextBox.Role != TextBoxRole.Normal || s.Locked) return;
        double now = NowMs;
        bool dbl = ReferenceEquals(_lastTextBoxClickStroke, s) && now - _lastTextBoxClickMs < 450;
        _lastTextBoxClickStroke = s;
        _lastTextBoxClickMs = now;
        if (!dbl) return;
        _pendingTextEdit = true;
        _pendingTextEditStroke = s;
        _pendingTextEditX = screenX;
        _pendingTextEditY = screenY;
    }

    /// <summary>松手时兑现双击：没拖动过才进入编辑。</summary>
    private void EnterPendingTextBoxEdit()
    {
        if (!_pendingTextEdit) return;
        var s = _pendingTextEditStroke;
        float x = _pendingTextEditX, y = _pendingTextEditY;
        _pendingTextEdit = false;
        _pendingTextEditStroke = null;
        if (s == null || !Doc.Selected.Contains(s) || s.Locked) return;
        BeginTextBoxEdit(s, caretScreenX: x, caretScreenY: y);
    }

    internal void BeginTextBoxEdit(Stroke s, bool isNew = false,
                                   float? caretScreenX = null, float? caretScreenY = null)
    {
        if (s?.TextBox == null || s.TextBox.Role != TextBoxRole.Normal) return;
        BakeTextBoxTranslation(s);
        EnsureTextBoxEditWindow();
        if (_textBoxEditHwnd == IntPtr.Zero) return;

        _textBoxEditStroke = s;
        _textBoxEditWasNew = isNew;
        _textBoxEditOriginalText = s.TextBox.Text ?? "";
        _textBoxEditOriginalLineColors = new List<Color4>(s.TextBox.LineColors);
        _textBoxEditOriginalColorSpans = CloneSpans(s.TextBox.ColorSpans);
        _textBoxEditColorTouched = false;
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
        _textBoxEditCpNormalized = DetectRichEditCpMode();
        UpdateTextBoxEditColors(s);
        var font = EnsureTextBoxEditFont(s.TextBox.FontSize * s.TextBox.Unit);
        Native.SendMessage(_textBoxEditHwnd, Native.WM_SETFONT, font, new IntPtr(1));
        ApplyRichEditFormat(s);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETLIMITTEXT, new IntPtr(20000), IntPtr.Zero);
        // 光标放末尾，**不要全选**：双击已经存在的文本框后随手打字，
        // 全选会让第一个字把整段内容替换掉（老师最怕的误操作）。
        int len = Native.GetWindowTextLengthW(_textBoxEditHwnd);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, new IntPtr(len), new IntPtr(len));

        RefreshTextBoxEditWindow(s.TextBox);
        if (caretScreenX.HasValue && caretScreenY.HasValue)
            PlaceTextBoxCaretAtScreenPoint(s, caretScreenX.Value, caretScreenY.Value);
        else
        {
            int len2 = Native.GetWindowTextLengthW(_textBoxEditHwnd);
            Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, new IntPtr(len2), new IntPtr(len2));
            _textBoxEditCaretIndex = len2;
        }
        KeepTextBoxEditInView(s);
        FocusTextBoxEditWindow();
        _dirty = true;
        NotifyUiStateChanged();
    }

    private void ScrollCanvasForTextEdit(IntPtr wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        if (delta == 0) return;
        ScrollCanvasBy(delta / 120f * 72f);
        if (_textBoxEditStroke?.TextBox is { } box) RefreshTextBoxEditWindow(box);
        _dirty = true;
    }

    /// <summary>
    /// 光标跑到屏幕底部/工具条下面时，把画布往上让一点。
    /// 只动视野，不动文本框内部——普通文本框永远没有内部滚动。
    /// 注意：**按光标位置让**，不是按框底让；否则编辑一个比屏幕还高的老框时，
    /// 点上面一行也会被强行滚到底部。
    /// </summary>
    private void KeepTextBoxEditInView(Stroke s)
    {
        if (!_textBoxEditing || s?.TextBox == null || _windows.Count == 0) return;
        if (!TryGetTextBoxEditCaretPoint(out int caretX, out int caretY)) return;
        _ = caretX;

        var screen = ScreenRectPhysical();
        var b = s.PaddedBounds;
        int hostY = (int)MathF.Round(b.MinY + 2f + ViewOffsetY);
        float screenCaretY = hostY + caretY;
        float safeTop = screen.MinY + 72f * DpiScale;
        float safeBottom = screen.MaxY - 120f * DpiScale;

        float dy = 0f;
        if (screenCaretY > safeBottom) dy = safeBottom - screenCaretY;
        else if (screenCaretY < safeTop) dy = safeTop - screenCaretY;
        if (MathF.Abs(dy) < 0.5f) return;

        ViewOffsetY += dy;
        ClampViewOffset();
        _dirty = true;
        RefreshTextBoxEditWindow(s.TextBox);
    }

    /// <summary>取编辑窗里光标在客户区的位置（RichEdit 用 POINTL* 语法）。</summary>
    private bool TryGetTextBoxEditCaretPoint(out int x, out int y)
    {
        x = y = 0;
        if (_textBoxEditHwnd == IntPtr.Zero) return false;
        int len = Native.GetWindowTextLengthW(_textBoxEditHwnd);
        int index = _textBoxEditCaretIndex;
        if (_richEditReady)
        {
            var range = new Native.CHARRANGE();
            var pinRange = System.Runtime.InteropServices.GCHandle.Alloc(
                range, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                Native.SendMessage(_textBoxEditHwnd, (uint)Native.EM_EXGETSEL_RICH,
                                   IntPtr.Zero, pinRange.AddrOfPinnedObject());
                range = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.CHARRANGE>(
                    pinRange.AddrOfPinnedObject());
                index = range.cpMax;
            }
            finally { pinRange.Free(); }
        }
        else
        {
            long sel = Native.SendMessage(_textBoxEditHwnd, Native.EM_GETSEL,
                                          IntPtr.Zero, IntPtr.Zero).ToInt64();
            index = (int)((sel >> 16) & 0xFFFF);
        }
        index = Math.Clamp(index, 0, Math.Max(0, len));
        _textBoxEditCaretIndex = index;

        if (_richEditReady)
        {
            var pt = new Native.POINT();
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(
                pt, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                Native.SendMessage(_textBoxEditHwnd, (uint)Native.EM_POSFROMCHAR_RICH,
                                   pin.AddrOfPinnedObject(), new IntPtr(index));
                pt = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.POINT>(
                    pin.AddrOfPinnedObject());
                x = pt.X; y = pt.Y;
                return true;
            }
            finally { pin.Free(); }
        }

        IntPtr pos = Native.SendMessage(_textBoxEditHwnd, Native.EM_POSFROMCHAR,
                                        new IntPtr(index), IntPtr.Zero);
        long v = pos.ToInt64();
        x = (short)(v & 0xFFFF);
        y = (short)((v >> 16) & 0xFFFF);
        return true;
    }

    /// <summary>
    /// 把光标放到“点下去的那一行/那个字”附近，而不是永远末尾。
    /// 编辑窗已经铺在卡片上，直接把屏幕坐标换成 EDIT 客户区坐标，
    /// 交给 RichEdit 自己算字符索引——它的换行/字宽就是最终排版，最准。
    /// </summary>
    private void PlaceTextBoxCaretAtScreenPoint(Stroke s, float screenX, float screenY)
    {
        if (_textBoxEditHwnd == IntPtr.Zero || s?.TextBox == null) return;
        var b = s.PaddedBounds;
        int hostX = (int)MathF.Round(b.MinX + 2f);
        int hostY = (int)MathF.Round(b.MinY + 2f + ViewOffsetY);
        int clientX = (int)MathF.Round(screenX) - hostX;
        int clientY = (int)MathF.Round(screenY) - hostY;
        clientX = Math.Clamp(clientX, 0, Math.Max(0, (int)MathF.Round(b.MaxX - b.MinX) - 4));
        clientY = Math.Clamp(clientY, 0, Math.Max(0, (int)MathF.Round(b.MaxY - b.MinY) - 4));
        uint scrollCaret = (uint)(_richEditReady ? Native.EM_SCROLLCARET_RICH : Native.EM_SCROLLCARET);
        IntPtr at;
        if (_richEditReady)
        {
            // RichEdit：lParam 是 POINTL*，不是 MAKELPARAM（传错会被当成指针解引用，直接崩）。
            var pt = new Native.POINT { X = clientX, Y = clientY };
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(
                pt, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                at = Native.SendMessage(_textBoxEditHwnd, (uint)Native.EM_CHARFROMPOS_RICH,
                                        IntPtr.Zero, pin.AddrOfPinnedObject());
            }
            finally { pin.Free(); }
        }
        else
        {
            IntPtr lParam = new(((long)clientY << 16) | ((long)clientX & 0xFFFF));
            at = Native.SendMessage(_textBoxEditHwnd, Native.EM_CHARFROMPOS, IntPtr.Zero, lParam);
        }
        int index = (int)(at.ToInt64() & 0xFFFF);
        int len = Native.GetWindowTextLengthW(_textBoxEditHwnd);
        index = Math.Clamp(index, 0, len);
        _textBoxEditCaretIndex = index;
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, new IntPtr(index), new IntPtr(index));
        Native.SendMessage(_textBoxEditHwnd, scrollCaret, IntPtr.Zero, IntPtr.Zero);
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
        UpdateTextBoxEditColors(s);
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
        // 宽度自动长的时候，RichEdit 偶尔会把内部视图横向滚到光标处，
        // 于是"字在长、前面的字却看不见"。这里只把 X 钉回 0，Y 保留
        //（长文本编辑时视口仍跟着光标上下走）。
        ResetRichEditScroll();
        Native.InvalidateRect(_textBoxEditHwnd, IntPtr.Zero, true);
        Native.UpdateWindow(_textBoxEditHwnd);
    }

    /// <summary>
    /// 把 RichEdit 的内部滚动钉回左上角。
    ///
    /// 为什么需要：文本框是"宽高跟着字长"的，窗口变宽/变高/左移时 RichEdit 仍可能
    /// 保留"滚到光标"的偏移，表现为前面的字/上一行被推出可视区。高度既然自动，
    /// 内容本来就该完整显示，编辑窗不需要内部滚动；屏幕装不下时滚的是画布。
    /// 纯 EDIT 没有这组消息。
    /// </summary>
    private void ResetRichEditScroll()
    {
        if (!_richEditReady || _textBoxEditHwnd == IntPtr.Zero) return;
        var pt = new Native.POINT();
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(
            pt, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            Native.SendMessage(_textBoxEditHwnd, (uint)Native.EM_GETSCROLLPOS,
                               IntPtr.Zero, pin.AddrOfPinnedObject());
            pt = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.POINT>(
                pin.AddrOfPinnedObject());
            if (pt.X == 0 && pt.Y == 0) return;
            pt.X = 0;
            pt.Y = 0;
            System.Runtime.InteropServices.Marshal.StructureToPtr(
                pt, pin.AddrOfPinnedObject(), false);
            Native.SendMessage(_textBoxEditHwnd, (uint)Native.EM_SETSCROLLPOS,
                               IntPtr.Zero, pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }
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
        if (box == null || box.Role != TextBoxRole.Normal) return;

        string text = ReadTextBoxEditText();
        if (string.Equals(box.Text, text, StringComparison.Ordinal)) return;

        var old = s.PaddedBounds;
        Vector2 origin = s.Points.Count > 0
            ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero;
        box.Text = text;
        box.InvalidateLayout();

        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.5f, box.Unit);
        float margin = 16f * u;
        // 宽度上限 = **整条屏幕可用宽**（不是"从落点到右沿"，也不另设 800 上限）：
        // 到屏幕边才换行；右沿放不下时把整框往左让，绝不让字长到屏幕外。
        float maxW = MathF.Max(80f * u, screen.MaxX - screen.MinX - margin * 2f);
        if (box.AutoWidth)
        {
            float maxH = MathF.Max(60f * u,
                                   screen.MaxY - ViewOffsetY - origin.Y - 16f * u);
            box.GrowToContent(maxW, maxH);
        }
        else
        {
            // 宽固定之后，输入仍然要**实时更新高度**：否则编辑窗会停在旧高度，
            // 后面的字只能在 RichEdit 内部滚，和“普通文本框没有内部滚动”冲突。
            // 固定宽本身也不许超过屏幕（老存档/换屏幕尺寸的兜底）。
            if (box.Width > maxW)
            {
                box.Width = maxW;
                box.InvalidateLayout();
            }
            box.AutoHeight = true;
            _ = box.EnsureLayout();
        }

        // 横向夹回屏幕：先右沿、再左沿（框比屏幕还宽时 GrowToContent 已经收到上限）。
        float maxOriginX = screen.MaxX - margin - box.Width;
        float minOriginX = screen.MinX + margin;
        origin.X = Math.Clamp(origin.X, MathF.Min(minOriginX, maxOriginX), maxOriginX);
        s.SetTextBoxRect(origin, box.Width, box.Height);
        Doc.RefreshStrokeGrid(s);
        Doc.Dirty.Add(old);
        Doc.Dirty.Add(s.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
        RefreshTextBoxEditWindow(box);
        KeepTextBoxEditInView(s);
    }

    private string ReadTextBoxEditText()
    {
        if (_textBoxEditHwnd == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(32768);
        Native.GetWindowTextW(_textBoxEditHwnd, sb, sb.Capacity);
        return sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>自检：发一个字符消息（模拟真实键盘逐字输入，走 RichEdit 的输入路径）。</summary>
    internal void TypeTextBoxEditCharForTest(char c)
    {
        if (!_textBoxEditing || _textBoxEditHwnd == IntPtr.Zero) return;
        Native.SendMessageW(_textBoxEditHwnd, (uint)Native.WM_CHAR,
                            new IntPtr(c), new IntPtr(1));
        // 不再手动调 OnTextBoxEditChanged：这里要验证 WndProc 自己会边打边长。
    }

    /// <summary>自检：在光标处**逐字追加**（模拟真实键盘输入，不经过输入法）。</summary>
    internal void AppendTextBoxEditTextForTest(string text)
    {
        if (!_textBoxEditing || _textBoxEditHwnd == IntPtr.Zero) return;
        int len = Native.GetWindowTextLengthW(_textBoxEditHwnd);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL,
                           new IntPtr(len), new IntPtr(len));
        Native.SendMessageString(_textBoxEditHwnd, Native.EM_REPLACESEL, new IntPtr(1), text ?? "");
        OnTextBoxEditChanged();
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
            // 自检模拟"继续在末尾输入"：光标放到末尾，截图/滚动才是真实状态。
            int end = Native.GetWindowTextLengthW(_textBoxEditHwnd);
            Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL,
                               new IntPtr(end), new IntPtr(end));
        }
    }

    /// <summary>自检：模拟“点了一下底部色带”导致编辑窗失焦（不应结束编辑）。</summary>
    internal void BlurTextBoxEditForTest()
    {
        if (_textBoxEditing && _windows.Count > 0)
            Native.SetFocus(_windows[0].Hwnd);
    }

    /// <summary>自检：在编辑窗里选中一段（start/end 是标准化文本下标）。</summary>
    internal void SetTextBoxEditSelectionForTest(int start, int end)
    {
        if (!_textBoxEditing || _textBoxEditHwnd == IntPtr.Zero) return;
        var (raw, norm) = ReadRawAndNorm(_textBoxEditHwnd);
        int a = Math.Clamp(start, 0, norm.Length);
        int b = Math.Clamp(end, a, norm.Length);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL,
                           new IntPtr(NormToCp(a, raw)), new IntPtr(NormToCp(b, raw)));
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
            if (s?.TextBox != null
                && (_textBoxEditColorTouched || s.TextBox.ColorSpans.Count > 0))
                CaptureRichEditColors(s.TextBox, text);
        }
        bool wasNew = _textBoxEditWasNew;
        bool blank = accept && s?.TextBox is { Role: TextBoxRole.Normal } && IsBlankText(text);
        _textBoxEditStroke = null;
        _textBoxEditWasNew = false;
        HideTextBoxEdit();

        // 空文本：普通文本框不应该留下一个看不见却能选中的空框。
        //   · 新建后没输入：直接丢，不留撤销记录（和以前一样）；
        //   · 老框被清空：先恢复编辑前的文字/几何，再走“删除”撤销——
        //     这样 Ctrl+Z 回来的是清空前的原文，而不是一个空框。
        if (blank && s != null)
        {
            var old = s.PaddedBounds;
            if (wasNew)
            {
                Doc.RemoveStroke(s);
                Doc.DropLastAddStrokeIf(s);
                Doc.Selected.Remove(s);
                Doc.Dirty.Add(old);
            }
            else
            {
                var box = s.TextBox;
                box.Text = before;
                box.LineColors.Clear();
                box.LineColors.AddRange(_textBoxEditOriginalLineColors);
                box.ColorSpans.Clear();
                foreach (var span in _textBoxEditOriginalColorSpans)
                    box.ColorSpans.Add(new TextBoxColorSpan
                    {
                        Start = span.Start, Length = span.Length, Color = span.Color,
                    });
                box.Width = _textBoxEditOriginalW;
                box.Height = _textBoxEditOriginalH;
                box.AutoHeight = _textBoxEditOriginalAuto;
                box.AutoWidth = _textBoxEditOriginalAutoWidth;
                box.InvalidateLayout();
                s.Transform = _textBoxEditOriginalXf;
                s.SetTextBoxRect(_textBoxEditOriginalOrigin, box.Width, box.Height);
                s.SyncTextBoxBounds();
                Doc.RefreshStrokeGrid(s);
                Doc.Dirty.Add(old);
                Doc.Dirty.Add(_textBoxEditOriginalBounds);
                Doc.Dirty.Add(s.PaddedBounds);
                Doc.RemoveStrokeWithUndo(s);
            }
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
            // 普通文本框：宽度只在“自动宽”时按内容收一次；之后宽度听用户的。
            // 高度永远自动——内容多高就多高，不再冻结、不再转内部滚动。
            if (accept && s.TextBox.Role == TextBoxRole.Normal)
            {
                var screen = ScreenRectPhysical();
                float u = MathF.Max(0.5f, s.TextBox.Unit);
                float margin = 16f * u;
                float maxW = MathF.Max(80f * u, screen.MaxX - screen.MinX - margin * 2f);
                float maxH = MathF.Max(80f * u, MathF.Min(
                    (screen.MaxY - screen.MinY) * 0.9f,
                    screen.MaxY - ViewOffsetY - oldOrigin.Y - margin));
                s.TextBox.FitToContent(maxW, maxH, fitWidth: s.TextBox.AutoWidth);
                // **左沿不动、只往右长**：不要按"变宽/变窄保持中心"去挪 p0，
                // 否则提交那一下框会往左跳半个宽度，看起来就不是"右边长"了。
                // 提交时再横向夹一次：老框/粘贴长文本都不能超出屏幕左右沿。
                if (s.Points.Count > 0)
                {
                    var p0 = s.Points[0];
                    float minX = screen.MinX + margin;
                    float maxX = screen.MaxX - margin - s.TextBox.Width;
                    float nx = Math.Clamp(p0.X, MathF.Min(minX, maxX), maxX);
                    if (MathF.Abs(nx - p0.X) > 0.5f)
                        s.SetPoint(0, new Vector2(nx, p0.Y));
                }
            }
            s.SyncTextBoxBounds();
            ClampTextBoxIntoView(s);
            Doc.RefreshStrokeGrid(s);
            if (accept && s.TextBox.Role == TextBoxRole.Normal)
            {
                // **不要在这里把 AutoWidth 关掉**：关掉的话"点一下外面再回来编辑"
                // 就变成固定宽，用户会看到"输入汉字长度不变"。只有手动拖过左右竖线/
                // 四角缩放（TextBoxSelection 那边）才把 AutoWidth 置 false。
                s.TextBox.AutoHeight = true;
            }
            Doc.Dirty.Add(oldBounds);
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            if (!string.Equals(before, text, StringComparison.Ordinal)
                || !SameColors(_textBoxEditOriginalLineColors, s.TextBox.LineColors)
                || !SameSpans(_textBoxEditOriginalColorSpans, s.TextBox.ColorSpans))
            {
                var newOrigin = s.Points.Count > 0
                    ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero;
                Doc.CommitInteractive(new TextBoxTextAction(
                    s, before, text, oldBounds, s.PaddedBounds,
                    oldW, oldH, oldAuto, _textBoxEditOriginalAutoWidth, oldOrigin, oldXf,
                    s.TextBox.Width, s.TextBox.Height, s.TextBox.AutoHeight,
                    s.TextBox.AutoWidth, newOrigin, s.Transform,
                    _textBoxEditOriginalLineColors, _textBoxEditOriginalColorSpans,
                    s.TextBox.LineColors, s.TextBox.ColorSpans));
            }
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>空白 = 连一个可见字符都没有（含全角空格、零宽字符）。</summary>
    internal static bool IsBlankText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (char c in text)
        {
            if (c == '\u200B' || c == '\uFEFF') continue;
            if (!char.IsWhiteSpace(c)) return false;
        }
        return true;
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
        // **RichEdit 还有一份自己的 options**（和窗口 style 分开）：只清 GWL_STYLE 不够。
        // 不清 ECO_AUTOHSCROLL，每打一个字它会把文本**向右滚 10 个字符**，
        // 表现就是"前一个字被滚走、只看得到末尾"——正是 ES_AUTOHSCROLL 的文档行为。
        if (_richEditReady)
        {
            Native.SendMessage(_textBoxEditHwnd, (uint)Native.EM_SETOPTIONS,
                new IntPtr(Native.ECOOP_AND), new IntPtr(~Native.ECO_AUTOHSCROLL));
        }
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
                IntPtr brush = _textBoxEditBgBrush != IntPtr.Zero
                    ? _textBoxEditBgBrush
                    : Native.GetStockObject(0 /* WHITE_BRUSH */);
                Native.FillRect(wParam, ref rc, brush);
                return new IntPtr(1);
            }
            case Native.WM_CTLCOLOREDIT:
                Native.SetBkColor(wParam, _textBoxEditBackColor);
                Native.SetTextColor(wParam, _textBoxEditTextColor);
                return _textBoxEditBgBrush != IntPtr.Zero
                    ? _textBoxEditBgBrush
                    : Native.GetStockObject(0 /* WHITE_BRUSH */);
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

    /// <summary>先交给 RichEdit 默认处理，再按新文本立即重排（边打边长）。</summary>
    private IntPtr PassToEditThenRefresh(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr r = Native.CallWindowProc(_textBoxEditOldProc, hWnd, msg, wParam, lParam);
        if (_textBoxEditing) OnTextBoxEditChanged();
        return r;
    }

    private IntPtr TextBoxEditWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
            {
                // 顶层 EDIT 默认不刷底色，会透出下面的 PPT/浏览器；编辑时铺白底。
                Native.GetClientRect(hWnd, out var rc);
                IntPtr brush = _textBoxEditBgBrush != IntPtr.Zero
                    ? _textBoxEditBgBrush
                    : Native.GetStockObject(0 /* WHITE_BRUSH */);
                Native.FillRect(wParam, ref rc, brush);
                return new IntPtr(1);
            }
            case Native.WM_KEYDOWN:
            {
                int vk = (int)wParam.ToInt64();
                bool ctrl = (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0;
                if (vk == 0x0D && ctrl) { EndTextBoxEditFromUi(true); return IntPtr.Zero; }
                // Esc = 结束输入（提交），不是丢掉刚打的字；要反悔用 Ctrl+Z。
                if (vk == 0x1B) { EndTextBoxEditFromUi(true); return IntPtr.Zero; }
                // 退格 / Delete / 回车 / Tab / Ctrl+Z：默认处理完要立即重排，
                // 不能等下一次 EN_CHANGE（某些输入法路径下它来得晚）。
                if (vk is 0x08 or 0x2E or 0x0D or 0x09 || (ctrl && vk == 0x5A))
                    return PassToEditThenRefresh(hWnd, msg, wParam, lParam);
                break;
            }
            case Native.WM_CHAR:
            case Native.WM_IME_CHAR:
            case Native.WM_IME_COMPOSITION:
            case Native.WM_IME_ENDCOMPOSITION:
            case Native.WM_CUT:
            case Native.WM_CLEAR:
            case Native.WM_UNDO:
                // **真实键盘/输入法上屏的每个字都走这里**：先让 RichEdit 处理，
                // 再立刻按新文本重排。只靠 EN_CHANGE 在中文 IME 下并不可靠，
                // 表现就是"打字时不变宽，点走以后才变"。
                return PassToEditThenRefresh(hWnd, msg, wParam, lParam);
            case Native.WM_PASTE:
                if (PastePlainText(hWnd))
                {
                    if (_textBoxEditing) OnTextBoxEditChanged();
                    return IntPtr.Zero;
                }
                break;
            case Native.WM_MOUSEWHEEL:
                // 编辑时滚轮滚的是**画布**，不是文本框内部：和 Excalidraw 一样，
                // 普通文本框没有第二个滚动条。滚完把编辑窗跟着卡片重新铺一次。
                if (_textBoxEditing)
                {
                    ScrollCanvasForTextEdit(wParam);
                    return IntPtr.Zero;
                }
                break;
            case Native.WM_KILLFOCUS:
                // 点底部文字色带 / 面板只丢焦点，不结束编辑；点画布外面由 Engine 判断提交。
                // （以前这里直接 End，导致“编辑中选一段改色”永远点不到色带。）
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

    /// <summary>
    /// 编辑窗的两块底色：文字色 + 背景色。
    ///
    /// 背景规则（2026-11-01 用户拍板）：
    ///   · **白板页**：直接用当前板色（白/绿/黑），编辑框看起来就是"同一个底"；
    ///   · **文档 / 图片上**：原生 RichEdit 不能真透明，先用一块比纯白柔和的近白；
    ///     文字本身是浅色（白字）时改用中性灰，否则白字会看不见。
    /// 完全透明要换掉 RichEdit 做自绘编辑器，本批不做。
    /// </summary>
    private void UpdateTextBoxEditColors(Stroke s)
    {
        if (s?.TextBox == null) return;
        var c = s.Color;
        _textBoxEditTextColor = ((uint)(c.R * 255f) & 0xFF)
                              | (((uint)(c.G * 255f) & 0xFF) << 8)
                              | (((uint)(c.B * 255f) & 0xFF) << 16);

        float textLuma = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        bool lightText = textLuma > 0.65f;
        Color4 bg;
        if (Doc.PageKey == InkDocument.BoardPageKey)
        {
            float boardLuma = 0.299f * BoardColor.R + 0.587f * BoardColor.G
                            + 0.114f * BoardColor.B;
            // 板色优先；只有"字和板几乎同色"（白字白板、黑字黑板）才临时换一块
            // 高对比底，否则编辑时连字都看不见。
            if (MathF.Abs(textLuma - boardLuma) < 0.28f)
                bg = textLuma > 0.5f
                    ? new Color4(0.32f, 0.34f, 0.36f, 1f)
                    : new Color4(0.95f, 0.95f, 0.94f, 1f);
            else
                bg = BoardColor;
        }
        else
        {
            bg = lightText
                ? new Color4(0.44f, 0.46f, 0.49f, 1f)
                : new Color4(0.95f, 0.95f, 0.94f, 1f);
        }

        uint cref = ((uint)(bg.R * 255f) & 0xFF)
                  | (((uint)(bg.G * 255f) & 0xFF) << 8)
                  | (((uint)(bg.B * 255f) & 0xFF) << 16);
        if (cref == _textBoxEditBackColor && _textBoxEditBgBrush != IntPtr.Zero) return;
        _textBoxEditBackColor = cref;
        if (_textBoxEditBgBrush != IntPtr.Zero) Native.DeleteObject(_textBoxEditBgBrush);
        _textBoxEditBgBrush = Native.CreateSolidBrush(cref);
    }

    private void ApplyRichEditFormat(Stroke s)
    {        if (!_richEditReady || s?.TextBox == null) return;
        float fontPx = MathF.Max(6f, s.TextBox.FontSize * MathF.Max(0.5f, s.TextBox.Unit));
        float dpi = MathF.Max(1f, DpiScale);
        int fontTwips = Math.Max(80, (int)MathF.Round(fontPx * 15f / dpi));

        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETBKGNDCOLOR,
                           IntPtr.Zero, new IntPtr(_textBoxEditBackColor));

        var cf = new Native.CHARFORMAT2W
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CHARFORMAT2W>(),
            dwMask = Native.CFM_FACE | Native.CFM_SIZE | Native.CFM_COLOR | Native.CFM_CHARSET,
            yHeight = fontTwips,
            crTextColor = (int)_textBoxEditTextColor,
            bCharSet = 1,
            szFaceName = TextBoxFaces.DirectWrite(s.TextBox.Face),
        };
        Native.SendMessageCharFormat(_textBoxEditHwnd, Native.EM_SETCHARFORMAT,
                                     new IntPtr(Native.SCF_ALL), ref cf);

        // 行距和显示端一致：用文本框自己的 LineSpacing（不再固定 1.55）。
        int lineTwips = Math.Max(80, (int)MathF.Round(
            fontPx * TextBoxFaces.ClampSpacing(s.TextBox.LineSpacing) * 15f / dpi));
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

        // 逐行/逐字原色：识别结果在编辑窗里也保持红蓝，不能一进去变单色。
        ApplyAllColorsToRichEdit(s.TextBox);

        int len = Native.GetWindowTextLengthW(_textBoxEditHwnd);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, new IntPtr(len), new IntPtr(len));
    }

    private static List<TextBoxColorSpan> CloneSpans(IEnumerable<TextBoxColorSpan> src)
    {
        var list = new List<TextBoxColorSpan>();
        if (src == null) return list;
        foreach (var s in src)
            list.Add(new TextBoxColorSpan { Start = s.Start, Length = s.Length, Color = s.Color });
        return list;
    }

    private static bool SameColors(IReadOnlyList<Color4> a, IReadOnlyList<Color4> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (MathF.Abs(a[i].R - b[i].R) > 0.004f
                || MathF.Abs(a[i].G - b[i].G) > 0.004f
                || MathF.Abs(a[i].B - b[i].B) > 0.004f) return false;
        return true;
    }

    private static bool SameSpans(IReadOnlyList<TextBoxColorSpan> a, IReadOnlyList<TextBoxColorSpan> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Start != b[i].Start || a[i].Length != b[i].Length
                || MathF.Abs(a[i].Color.R - b[i].Color.R) > 0.004f
                || MathF.Abs(a[i].Color.G - b[i].Color.G) > 0.004f
                || MathF.Abs(a[i].Color.B - b[i].Color.B) > 0.004f) return false;
        return true;
    }

    private static (string Raw, string Norm) ReadRawAndNorm(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(32768);
        Native.GetWindowTextW(hwnd, sb, sb.Capacity);
        string raw = sb.ToString();
        return (raw, raw.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    /// <summary>RichEdit 的 cp 到底按 \r\n 还是按 \n 计（不同版本不一样，实测一次记住）。</summary>
    private bool DetectRichEditCpMode()
    {
        var (raw, norm) = ReadRawAndNorm(_textBoxEditHwnd);
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
        var cr = new Native.CHARRANGE();
        Native.SendMessageCharRange(_textBoxEditHwnd, Native.EM_EXGETSEL_RICH, IntPtr.Zero, ref cr);
        if (cr.cpMax == norm.Length) return true;
        if (cr.cpMax == raw.Length) return false;
        return norm.Length <= raw.Length;
    }

    private int NormToCp(int normIndex, string raw)
    {
        if (_textBoxEditCpNormalized) return Math.Max(0, normIndex);
        int ni = 0, ri = 0;
        while (ri < raw.Length && ni < normIndex)
        {
            if (raw[ri] == '\r' && ri + 1 < raw.Length && raw[ri + 1] == '\n') { ri += 2; ni++; }
            else { ri++; ni++; }
        }
        return ri;
    }

    private int CpToNorm(int cp, string raw)
    {
        if (_textBoxEditCpNormalized) return Math.Max(0, cp);
        int ni = 0, ri = 0;
        while (ri < cp && ri < raw.Length)
        {
            if (raw[ri] == '\r' && ri + 1 < raw.Length && raw[ri + 1] == '\n') { ri += 2; ni++; }
            else { ri++; ni++; }
        }
        return ni;
    }

    private static int ColorRef(Color4 c)
        => ((int)(c.R * 255f) & 0xFF)
           | (((int)(c.G * 255f) & 0xFF) << 8)
           | (((int)(c.B * 255f) & 0xFF) << 16);

    private static Color4 FromColorRef(int cref)
        => new Color4(((cref >> 0) & 0xFF) / 255f,
                      ((cref >> 8) & 0xFF) / 255f,
                      ((cref >> 16) & 0xFF) / 255f, 1f);

    private void ApplyRichEditColorAll(Color4 color)
    {
        if (_textBoxEditHwnd == IntPtr.Zero) return;
        var cf = new Native.CHARFORMAT2W
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CHARFORMAT2W>(),
            dwMask = Native.CFM_COLOR,
            crTextColor = ColorRef(color),
        };
        Native.SendMessageCharFormat(_textBoxEditHwnd, Native.EM_SETCHARFORMAT,
                                     new IntPtr(Native.SCF_ALL), ref cf);
        Native.InvalidateRect(_textBoxEditHwnd, IntPtr.Zero, true);
    }

    private void ApplyRichEditColorRange(int cpMin, int cpMax, Color4 color)
    {
        if (_textBoxEditHwnd == IntPtr.Zero || cpMax <= cpMin) return;
        Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL,
                           new IntPtr(cpMin), new IntPtr(cpMax));
        var cf = new Native.CHARFORMAT2W
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CHARFORMAT2W>(),
            dwMask = Native.CFM_COLOR,
            crTextColor = ColorRef(color),
        };
        Native.SendMessageCharFormat(_textBoxEditHwnd, Native.EM_SETCHARFORMAT,
                                     new IntPtr(Native.SCF_SELECTION), ref cf);
    }

    /// <summary>把逐行色 + 逐字 span 一起刷到 RichEdit；识别结果进编辑窗也保持红蓝。</summary>
    private void ApplyAllColorsToRichEdit(TextBoxData box)
    {
        if (_textBoxEditHwnd == IntPtr.Zero || box == null) return;
        var (raw, _) = ReadRawAndNorm(_textBoxEditHwnd);
        int offset = 0;
        string[] hard = (box.Text ?? "").Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < hard.Length; i++)
        {
            if (i < box.LineColors.Count && hard[i].Length > 0)
            {
                int a = NormToCp(offset, raw), b = NormToCp(offset + hard[i].Length, raw);
                ApplyRichEditColorRange(a, b, box.LineColors[i]);
            }
            offset += hard[i].Length + 1;
        }
        foreach (var span in box.ColorSpans)
        {
            int a = NormToCp(span.Start, raw);
            int b = NormToCp(span.Start + span.Length, raw);
            ApplyRichEditColorRange(a, b, span.Color);
        }
    }

    private (int Min, int Max) RichEditSelection()
    {
        var cr = new Native.CHARRANGE();
        if (_textBoxEditHwnd != IntPtr.Zero)
            Native.SendMessageCharRange(_textBoxEditHwnd, Native.EM_EXGETSEL_RICH, IntPtr.Zero, ref cr);
        return (cr.cpMin, cr.cpMax);
    }

    internal bool TextBoxEditHasSelection
    {
        get
        {
            var (a, b) = RichEditSelection();
            return b > a;
        }
    }

    /// <summary>
    /// 编辑中改色：有选中 → 只改选中的字；没选中 → 整框改色。
    /// 选中改色立即写进 RichEdit（实时），提交时再从 RichEdit 抓回逐字 span。
    /// </summary>
    internal void SetTextBoxEditColorFromUi(Color4 color)
    {
        var s = _textBoxEditStroke;
        var box = s?.TextBox;
        if (s == null || box == null) return;
        _textBoxEditColorTouched = true;

        var (cpMin, cpMax) = RichEditSelection();
        if (cpMax > cpMin)
        {
            ApplyRichEditColorRange(cpMin, cpMax, color);
            var (raw, _) = ReadRawAndNorm(_textBoxEditHwnd);
            int a = CpToNorm(cpMin, raw), b = CpToNorm(cpMax, raw);
            if (b > a) box.SetColorSpan(a, b - a, color);
            box.InvalidateLayout();
            s.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(s);
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            _dirty = true;
            NotifyUiStateChanged();
            FocusTextBoxEditWindow();
            return;
        }

        // 没有选中 = 整框改色：逐行/逐字色都清掉。
        var old = s.PaddedBounds;
        s.Color = color;
        box.Ink = color;
        box.ClearCustomColors();
        box.InvalidateLayout();
        s.SyncTextBoxBounds();
        Doc.RefreshStrokeGrid(s);
        Doc.Dirty.Add(old);
        Doc.Dirty.Add(s.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
        UpdateTextBoxEditColors(s);
        ApplyRichEditColorAll(color);
        NotifyUiStateChanged();
        FocusTextBoxEditWindow();
    }

    /// <summary>
    /// 提交时从 RichEdit 抓每个字的颜色，重建 span。
    /// 这样用户选一段改色、又接着增删字之后，颜色仍跟着字走。
    /// </summary>
    private void CaptureRichEditColors(TextBoxData box, string text)
    {
        if (_textBoxEditHwnd == IntPtr.Zero || box == null) return;
        var (raw, _) = ReadRawAndNorm(_textBoxEditHwnd);
        box.ColorSpans.Clear();

        int hardLine = 0;
        Color4 baseColor = box.InkForLine(0);
        var spans = new List<TextBoxColorSpan>();
        int runStart = -1, runLen = 0;
        Color4 runColor = baseColor;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                hardLine++;
                baseColor = box.InkForLine(hardLine);
            }
            int a = NormToCp(i, raw);
            int b = NormToCp(i + 1, raw);
            if (b <= a) b = a + 1;
            Native.SendMessage(_textBoxEditHwnd, Native.EM_SETSEL,
                               new IntPtr(a), new IntPtr(b));
            var cf = new Native.CHARFORMAT2W
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CHARFORMAT2W>(),
                dwMask = Native.CFM_COLOR,
            };
            Native.SendMessageCharFormat(_textBoxEditHwnd, Native.EM_GETCHARFORMAT,
                                         new IntPtr(Native.SCF_SELECTION), ref cf);
            Color4 c = FromColorRef(cf.crTextColor);
            bool sameBase = MathF.Abs(c.R - baseColor.R) < 0.02f
                            && MathF.Abs(c.G - baseColor.G) < 0.02f
                            && MathF.Abs(c.B - baseColor.B) < 0.02f;

            if (sameBase)
            {
                Flush();
                continue;
            }
            if (runStart < 0)
            {
                runStart = i; runLen = 1; runColor = c;
            }
            else if (MathF.Abs(c.R - runColor.R) < 0.02f
                     && MathF.Abs(c.G - runColor.G) < 0.02f
                     && MathF.Abs(c.B - runColor.B) < 0.02f)
            {
                runLen++;
            }
            else
            {
                Flush();
                runStart = i; runLen = 1; runColor = c;
            }
        }
        Flush();

        foreach (var span in spans) box.ColorSpans.Add(span);

        void Flush()
        {
            if (runStart < 0 || runLen <= 0) return;
            spans.Add(new TextBoxColorSpan { Start = runStart, Length = runLen, Color = runColor });
            runStart = -1; runLen = 0;
        }
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
    private readonly List<Color4> _beforeLineColors, _afterLineColors;
    private readonly List<TextBoxColorSpan> _beforeSpans, _afterSpans;

    public TextBoxTextAction(Stroke s, string before, string after,
                             RectF beforeBounds, RectF afterBounds,
                             float beforeW, float beforeH, bool beforeAuto, bool beforeAutoWidth,
                             Vector2 beforeOrigin, Matrix3x2 beforeXf,
                             float afterW, float afterH, bool afterAuto, bool afterAutoWidth,
                             Vector2 afterOrigin, Matrix3x2 afterXf,
                             IReadOnlyList<Color4> beforeLineColors,
                             IReadOnlyList<TextBoxColorSpan> beforeSpans,
                             IReadOnlyList<Color4> afterLineColors,
                             IReadOnlyList<TextBoxColorSpan> afterSpans)
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
        _beforeLineColors = new List<Color4>(beforeLineColors ?? Array.Empty<Color4>());
        _afterLineColors = new List<Color4>(afterLineColors ?? Array.Empty<Color4>());
        _beforeSpans = CloneSpans(beforeSpans);
        _afterSpans = CloneSpans(afterSpans);
    }

    private static List<TextBoxColorSpan> CloneSpans(IEnumerable<TextBoxColorSpan> src)
    {
        var list = new List<TextBoxColorSpan>();
        if (src == null) return list;
        foreach (var s in src)
            list.Add(new TextBoxColorSpan { Start = s.Start, Length = s.Length, Color = s.Color });
        return list;
    }

    public override int HeldStrokes => 1;
    public override RectF AffectedBefore => _beforeBounds;
    public override RectF AffectedAfter => _afterBounds;

    public override void Undo(InkDocument doc)
        => Apply(doc, _before, _beforeW, _beforeH, _beforeAuto, _beforeAutoWidth,
                 _beforeOrigin, _beforeXf, _beforeLineColors, _beforeSpans);
    public override void Redo(InkDocument doc)
        => Apply(doc, _after, _afterW, _afterH, _afterAuto, _afterAutoWidth,
                 _afterOrigin, _afterXf, _afterLineColors, _afterSpans);

    private void Apply(InkDocument doc, string text, float w, float h, bool auto, bool autoWidth,
                       Vector2 origin, Matrix3x2 xf,
                       IReadOnlyList<Color4> lineColors, IReadOnlyList<TextBoxColorSpan> spans)
    {
        var box = _stroke.TextBox;
        if (box == null) return;
        var old = _stroke.PaddedBounds;
        box.Text = text;
        box.LineColors.Clear();
        if (lineColors != null) box.LineColors.AddRange(lineColors);
        box.ColorSpans.Clear();
        if (spans != null)
            foreach (var span in spans)
                box.ColorSpans.Add(new TextBoxColorSpan
                {
                    Start = span.Start, Length = span.Length, Color = span.Color,
                });
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
