// [职责] 选中操作条的状态与动作：颜色/粗细/线型/锁定/层级/取色器/菜单。
// [测试] --seltest

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;


public partial class InkEngine
{
    // =====================================================================
    //  操作条（九格）+ 浮动面板 + 提示条：状态与动作
    // =====================================================================

    /// <summary>选区正在"闪一下"吗（复制成功的反馈）。</summary>
    internal bool SelFlashing => NowMs < SelFlashUntilMs;

    /// <summary>
    /// 改选中对象的颜色。**荧光笔保留半透明**（`InkPalette.ToHighlighter`），
    /// **图像对象跳过**（它没有"颜色"这个概念），其余按同一色值统改。
    /// 返回真正改掉了几条（0 = 一条都没改，调用方据此给提示）。
    /// </summary>
    internal int SetSelectionColor(Color4 baseColor)
    {
        // 编辑中且编辑窗里选中了一段文字：只改选中的字，不动整框。
        if (_textBoxEditing && _textBoxEditHwnd != IntPtr.Zero && TextBoxEditHasSelection)
        {
            SetTextBoxEditColorFromUi(baseColor);
            return 1;
        }

        var targets = new List<Stroke>();
        var colors = new List<Color4>();
        int changed = 0;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            // 荧光笔与普通墨迹共用同一个"基色"，但荧光笔要转成半透明——
            // 直接按基色刷会把荧光笔刷成实心（那种"越改越糟"的效果）。
            // 规则只有一份：`InkPalette.ForStroke`（自定义取色板也走它）。
            var c = InkPalette.ForStroke(baseColor, s);
            if (s.Color.Equals(c)) continue;
            targets.Add(s);
            colors.Add(c);
            changed++;
        }
        if (changed == 0) return 0;
        Doc.ApplyColors(targets, colors.ToArray());   // 一条动作 = 一步撤销
        // 正在编辑的那个文本框改色后，编辑窗的文字/背景也要立刻跟着走。
        if (_textBoxEditing && _textBoxEditStroke != null && targets.Contains(_textBoxEditStroke))
        {
            UpdateTextBoxEditColors(_textBoxEditStroke);
            ApplyRichEditFormat(_textBoxEditStroke);
            Native.InvalidateRect(_textBoxEditHwnd, IntPtr.Zero, true);
        }
        // 屏幕反馈按用户要求**只留"选中框上看得见的变化"**（颜色环、框的粗细），
        // 不再弹提示条；需要留痕的写控制台（这一版一直如此）。
        Console.WriteLine($"改颜色：{changed} 个对象");
        return changed;
    }

    /// <summary>
    /// 改选中对象的粗细（**逻辑**像素档位，内部乘 DPI 换成物理宽度）。
    /// 图像对象跳过。返回改了几条。
    /// </summary>
    internal int SetSelectionWidth(float logicalWidth)
    {
        float w = logicalWidth * DpiScale;
        var targets = new List<Stroke>();
        int changed = 0;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (MathF.Abs(s.Width - w) < 0.01f) continue;
            targets.Add(s);
            changed++;
        }
        if (changed == 0) return 0;
        Doc.ApplyWidths(targets, w);
        Console.WriteLine($"改粗细：{changed} 个对象 → {logicalWidth:F1} 逻辑像素");
        return changed;
    }

    /// <summary>
    /// 改选中对象的线型（见 <see cref="StrokeDash"/>）。返回改了几条。
    ///
    /// **只跳过图像**——它根本没有"描边"这回事。
    ///
    /// 自由笔迹**也给改**（用户 2026-09-19 改的口径）：原来这里跳过 Freehand，
    /// 依据是"手写虚线没有意义"，于是老师选中一条笔迹再点虚线**一点反应都没有**
    /// （面板那一行还不高亮，因为高亮读的是"第一条非图像对象的线型"）。
    /// 用户报的"选中以后虚线面板还没有实现"就是它。
    /// 渲染那边**本来就支持**（`Gfx.StyleFor(s.Dash)` 在 DrawStrokeCore 一处统一生效），
    /// 所以这条限制一撤，"虚线笔迹"从画到存到导出全都通。
    /// </summary>
    internal int SetSelectionDash(StrokeDash dash)
    {
        var targets = new List<Stroke>();
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (s.Dash == dash) continue;          // 已经是这一档，不用记一步空撤销
            targets.Add(s);
        }
        if (targets.Count == 0) return 0;
        Doc.ApplyDash(targets, dash);              // 一条动作 = 一步撤销
        Console.WriteLine($"改线型：{targets.Count} 个对象 → {DashName(dash)}");
        return targets.Count;
    }

    /// <summary>
    /// 线型的中文名（控制台痕迹用）。**只此一份**——散在各处又是一份"同一件事的两份名单"。
    /// </summary>
    internal static string DashName(StrokeDash d) => d switch
    {
        StrokeDash.Dashed => "虚线",
        StrokeDash.Dotted => "点线",
        _ => "实线",
    };

    /// <summary>
    /// 界面色带条上那个**虚实线切换**被点了一下（三档轮流，见 FullUi 的 DrawDashToggle）。
    ///
    /// 它改的是**以后新画的笔迹**用哪种线型，不碰已经画好的（那些要选中之后再改，
    /// 走 <see cref="SetSelectionDash"/>）——和颜色/粗细的"画之前选、画之后改"两条路一样。
    /// </summary>
    internal void SetDashFromUi(StrokeDash dash)
    {
        if (PenDash == dash) return;
        PenDash = dash;
        Console.WriteLine($"笔的线型：{DashName(dash)}（下一笔开始生效）");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 锁定 / 解锁选中对象（用户 2026-09-16 定的语义：**锁定后能选中、但拖不动**）。
    /// 全部未锁 → 全锁；全部已锁 → 全解；混合 → 全锁。
    /// </summary>
    internal int ToggleSelectionLock()
    {
        if (Doc.Selected.Count == 0) return 0;
        bool anyUnlocked = false;
        foreach (var s in Doc.Selected) if (!s.Locked) { anyUnlocked = true; break; }
        bool lockTo = anyUnlocked;                 // 有没锁的就全锁上，否则全解

        var targets = new List<Stroke>();
        foreach (var s in Doc.Selected) if (s.Locked != lockTo) targets.Add(s);
        int n = targets.Count;
        if (n > 0) Doc.ApplyLock(targets, lockTo);
        if (n > 0)
            Console.WriteLine(lockTo ? $"已锁定 {n} 个对象" : $"已解锁 {n} 个对象");
        return n;
    }

    /// <summary>层级：把选中的整体置顶 / 置底（用户 2026-09-16 定：只做这两个）。</summary>
    internal bool ReorderSelection(bool toFront)
    {
        if (Doc.Selected.Count == 0) return false;
        if (Doc.Selected.Count >= Doc.Strokes.Count)        // 全选时置顶/置底没有意义
        {
            Console.WriteLine("层级：整页都选中了，没有层级可调");
            return false;
        }
        Doc.ReorderSelected(toFront);
        Console.WriteLine(toFront ? "层级：已置顶" : "层级：已置底");
        return true;
    }

    /// <summary>
    /// 点了浮动面板上的东西。返回 true = 这一次按下被面板消费掉了。
    /// 面板的几何全部来自 <see cref="SelectionHandles"/>（渲染与命中同源）。
    /// </summary>
    private bool HandlePanelClick(float x, float y)
    {
        var aabb = LiveSelectionFrame.CanvasAabb;
        float dpi = FurnitureDpi;      // 面板几何：屏幕恒定（见 InkEngine.FurnitureDpi）
        int sc = SelectionHandles.SwatchCount;
        var part = SelectionHandles.PanelPartAt(x, y, aabb, dpi, FurnitureViewport, SelPanelOpen, sc,
            SingleTextBoxSelection);

        switch (part)
        {
            case SelectionHandles.PanelPart.Slider:
                BeginWidthSliderDrag(x, aabb);
                return true;

            // 「更多…」：打开全部系统字体下拉（2026-11-11）。
            case SelectionHandles.PanelPart.FaceMore:
                OpenFontList();
                return true;

            // 线型三格（实线 / 虚线 / 点线）。**格序就是 StrokeDash 的取值**，
            // 所以"第 i 格"能直接转成线型，不用再维护一张对照表（见 StyleCellRect）。
            case SelectionHandles.PanelPart.StyleSolid:
                SetSelectionDash(StrokeDash.Solid);
                return true;

            case SelectionHandles.PanelPart.StyleDashed:
                SetSelectionDash(StrokeDash.Dashed);
                return true;

            case SelectionHandles.PanelPart.StyleDotted:
                SetSelectionDash(StrokeDash.Dotted);
                return true;

            case SelectionHandles.PanelPart.LayerFront:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: true);
                return true;

            case SelectionHandles.PanelPart.LayerBack:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: false);
                return true;

            // 导出格式：0 = PNG（透明底）、1 = JPG（白底）。
        }

        if (part >= SelectionHandles.PanelPart.BackgroundBase)
        {
            int i = part - SelectionHandles.PanelPart.BackgroundBase;
            if (i >= 0 && i < TextBoxBackground.PresetCount)
                SetSelectedTextBackgroundFromUi(i);
            return true;
        }

        if (part >= SelectionHandles.PanelPart.SpacingBase)
        {
            int i = part - SelectionHandles.PanelPart.SpacingBase;
            if (i >= 0 && i < TextBoxFaces.LineSpacings.Length)
                SetSelectedTextSpacingFromUi(TextBoxFaces.LineSpacings[i]);
            return true;
        }

        if (part >= SelectionHandles.PanelPart.FaceBase)
        {
            int i = part - SelectionHandles.PanelPart.FaceBase;
            if (i >= 0 && i < TextBoxFaces.All.Length)
                SetSelectedTextFaceFromUi(i);
            return true;
        }

        if (part >= SelectionHandles.PanelPart.FontBase)
        {
            int i = part - SelectionHandles.PanelPart.FontBase;
            if (i >= 0 && i < InkPalette.TextFontSizes.Length)
            {
                SetSelectedTextFontFromUi(InkPalette.TextFontSizes[i]);
                SelPanelOpen = SelPanel.None;
            }
            return true;
        }

        if (part >= SelectionHandles.PanelPart.SwatchBase)
        {
            int i = part - SelectionHandles.PanelPart.SwatchBase;
            var swatches = InkPalette.SelectionSwatches;
            if (i < 0 || i >= swatches.Length) return true;
            if (i == swatches.Length - 1)      // 末格 = 自定义取色（8.2.0：内置 HSV 小色板）
            {
                if (CustomColorOpen) CloseCustomColor();       // 再点一下 = 收起
                else OpenCustomColor();
                return true;
            }
            CloseCustomColor();                // 点了常规色片 = 不玩自定义了（否则色板会挂在那儿）
            SetSelectionColor(swatches[i].Color);
            return true;
        }
        return false;                          // 面板的空白处：什么都不做
    }

    /// <summary>
    /// 点了**自定义取色板**上的东西（它挂在墨迹面板旁边，优先级比面板本身高）。
    /// 返回 true = 这一次按下被色板消费掉了（包括卡片里没控件的空白处——
    /// 那不算"点在外面"，不触发取消）。
    /// </summary>
    private bool HandlePickClick(float x, float y)
    {
        var aabb = LiveSelectionFrame.CanvasAabb;
        var part = SelectionHandles.PickPartAt(x, y, aabb, FurnitureDpi, FurnitureViewport,
                                               SelectionHandles.SwatchCount, SingleTextBoxSelection);
        switch (part)
        {
            case SelectionHandles.PickPart.Hue:
                BeginColorPickerDrag(x, y, aabb, onHue: true);
                return true;
            case SelectionHandles.PickPart.Sv:
                BeginColorPickerDrag(x, y, aabb, onHue: false);
                return true;
            case SelectionHandles.PickPart.Inside:
                return true;                   // 卡片空白：吃掉这一下，但什么都不改
        }
        return false;
    }

    /// <summary>颜色面板里"粗细滑条"当前的档位表：全荧光笔就用荧光笔那三档。</summary>
    private float[] WidthStepsForSelection()
    {
        bool anyPen = false, anyHighlighter = false;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (s.Tool == Tool.Highlighter) anyHighlighter = true; else anyPen = true;
        }
        return (anyHighlighter && !anyPen) ? HighlighterWidthPresets : WidthPresets;
    }

    /// <summary>
    /// 颜色面板里那条粗细滑条的**取值范围**（连续滑条的两端，逻辑像素）。
    /// = 该档位表的首尾：全荧光笔 8..32、其余 1..24。**滑条夹在它里面**——
    /// 荧光笔不许被拖成实心细线（用户 2026-09-27 定的"独立一张亮色表"那套）。
    /// </summary>
    internal (float Min, float Max) WidthRangeForSelection()
    {
        var steps = WidthStepsForSelection();
        return (steps[0], steps[^1]);
    }

    /// <summary>选区在滑条上的位置（0..1；取第一条非图像对象；空选区/范围退化 = 0）。</summary>
    internal float SliderTofSelection()
    {
        var (min, max) = WidthRangeForSelection();
        if (max <= min) return 0f;
        return Math.Clamp((SliderValueOfSelection() - min) / (max - min), 0f, 1f);
    }

    /// <summary>选区第一条非图像对象的宽度（逻辑像素；没有 = 0）。</summary>
    internal float SliderValueOfSelection()
    {
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            return s.Width / DpiScale;
        }
        return 0f;
    }

    /// <summary>选区里的档位表长度（滑条那排参考刻度有几个点）。</summary>
    internal int SliderStepCount() => WidthStepsForSelection().Length;

    /// <summary>
    /// 按下粗细滑条：**连续拖动**开始（8.2.0 起；2026-10-16 起值**整数吸附**、不再有小数）。
    /// 按下这一下就立刻生效（和旧手感一致：点哪儿就是哪儿），拖动中反复改、
    /// 松手才进撤销栈（<see cref="EndWidthSliderDrag"/>）。
    /// </summary>
    private void BeginWidthSliderDrag(float x, in RectF aabb)
    {
        _propDragTargets.Clear();
        foreach (var s in Doc.Selected) if (!s.IsImage) _propDragTargets.Add(s);
        if (_propDragTargets.Count == 0) return;

        _sliderDragging = true;
        _widthDragValue = SliderValueAtX(x, aabb);

        // 荧光笔选区的滑条只给 8..32（见 WidthRangeForSelection）——
        // 从外面（比如更粗的旧数据）拖进来也不会越界。
        _widthDragStartValue = SliderValueOfSelection();
        _widthDragAction = new SetStrokePropAction(_propDragTargets, _widthDragValue * DpiScale);
        _widthDragAction.Redo(Doc);

        if (MathF.Abs(_widthDragValue - _widthDragStartValue) > 0.01f)
            Console.WriteLine($"拖粗细：{_widthDragStartValue:F1} → {_widthDragValue:F1} 逻辑像素");
        _dirty = true;
    }

    /// <summary>拖动中：把滑条拖到 x 处（连续值，夹在范围的端点上）。</summary>
    private void DragWidthSliderTo(float x)
    {
        if (_widthDragAction == null) return;
        float v = SliderValueAtX(x, LiveSelectionFrame.CanvasAabb);
        if (MathF.Abs(v - _widthDragValue) < 0.05f) return;   // 没变不重画（每帧几十次 Redo 是白给）
        _widthDragValue = v;
        _widthDragAction.RetargetWidth(v * DpiScale);
        _widthDragAction.Redo(Doc);
        _dirty = true;
    }

    /// <summary>松手：一次拖拽 = 一步撤销（值真的变了才记）。</summary>
    private void EndWidthSliderDrag()
    {
        _sliderDragging = false;
        var act = _widthDragAction;
        _widthDragAction = null;
        _propDragTargets.Clear();
        if (act != null && act.HasChange)
        {
            Doc.CommitInteractive(act);
            Console.WriteLine($"改粗细：{act.TargetCount} 个对象 → {_widthDragValue:F1} 逻辑像素");
        }
        _dirty = true;
    }

    /// <summary>x → 滑条上的值（逻辑像素，**整数吸附**——2026-10-16 用户："粗细不要小数点"），
    /// 夹在 <see cref="WidthRangeForSelection"/> 里。</summary>
    private float SliderValueAtX(float x, in RectF aabb)
    {
        var (min, max) = WidthRangeForSelection();
        float t = SelectionHandles.SliderTAt(x, aabb, FurnitureDpi, FurnitureViewport, SelectionHandles.SwatchCount);
        return MathF.Round(min + (max - min) * t);
    }

    // ---- 自定义取色板（8.2.0：色相条 + 饱和度/明度方块 + 当前色预览）--------

    /// <summary>
    /// 点开自定义取色板（色板最后一格）：**起点 = 当前选中墨迹的颜色**（换算成 HSV），
    /// 这样打开时预览色和墨迹一致，不会"一开就跳色"。全选图像时退回手里那支笔的色。
    /// </summary>
    private void OpenCustomColor()
    {
        CustomColorOpen = true;
        Color4 c = Tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            c = s.Color;
            break;
        }
        (_pickH, _pickS, _pickV) = Hsv.FromRgb(c);
        _dirty = true;
    }

    /// <summary>
    /// 关掉自定义取色板。**拖动中被打断的话，把还没提交的那次改动撤回来**
    ///（用户定的"点面板外 = 取消，保持原色"）。
    /// </summary>
    private void CloseCustomColor()
    {
        if (!CustomColorOpen) return;
        CustomColorOpen = false;
        _colorDragging = false;
        if (_colorDragAction != null)
        {
            _colorDragAction.Undo(Doc);
            _colorDragAction = null;
        }
        _dirty = true;
    }

    /// <summary>按下取色板：开始一次"实时预览"的拖拽（松手在板内 = 应用，板外 = 取消）。</summary>
    private void BeginColorPickerDrag(float x, float y, in RectF aabb, bool onHue)
    {
        UpdatePickAtPoint(x, y, aabb, onHue);
        _colorDragging = true;
        _colorDragOnHue = onHue;

        _propDragTargets.Clear();
        foreach (var s in Doc.Selected) if (!s.IsImage) _propDragTargets.Add(s);
        if (_propDragTargets.Count == 0) { _colorDragAction = null; return; }

        var baseColor = Hsv.ToRgb(_pickH, _pickS, _pickV);
        var colors = new Color4[_propDragTargets.Count];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = InkPalette.ForStroke(baseColor, _propDragTargets[i]);
        _colorDragAction = new SetStrokePropAction(_propDragTargets, colors);
        _colorDragAction.Redo(Doc);
        _dirty = true;
    }

    /// <summary>指针位置 → 取色板里的 HSV（拖哪块更新哪块）。</summary>
    private void UpdatePickAtPoint(float x, float y, in RectF aabb, bool hue)
    {
        int sc = SelectionHandles.SwatchCount;
        bool textBar = SingleTextBoxSelection;
        if (hue)
        {
            var r = SelectionHandles.PickHueRect(aabb, FurnitureDpi, FurnitureViewport, sc, textBar);
            if (r.MaxY > r.MinY) _pickH = Math.Clamp((y - r.MinY) / (r.MaxY - r.MinY), 0f, 1f);
        }
        else
        {
            var r = SelectionHandles.PickSvRect(aabb, FurnitureDpi, FurnitureViewport, sc, textBar);
            if (r.MaxX > r.MinX) _pickS = Math.Clamp((x - r.MinX) / (r.MaxX - r.MinX), 0f, 1f);
            if (r.MaxY > r.MinY) _pickV = Math.Clamp((y - r.MinY) / (r.MaxY - r.MinY), 0f, 1f);
        }
    }

    /// <summary>拖动取色板：实时把新颜色作用到选中对象上（还没进撤销栈）。</summary>
    private void DragColorPickerTo(float x, float y)
    {
        if (!_colorDragging) return;
        UpdatePickAtPoint(x, y, LiveSelectionFrame.CanvasAabb, _colorDragOnHue);
        if (_colorDragAction != null)
        {
            _colorDragAction.RetargetColor(Hsv.ToRgb(_pickH, _pickS, _pickV));
            _colorDragAction.Redo(Doc);
        }
        _dirty = true;
    }

    /// <summary>
    /// 松手：指针**在色板里** = 这一次选取算数（一步撤销）；**在外面** = 取消
    ///（把预览撤回去，保持原色）。用户 2026-09-30 定的口径："点面板外 = 取消（保持原色）"。
    /// </summary>
    private void EndColorPickerDrag(float x, float y)
    {
        if (!_colorDragging) return;
        _colorDragging = false;
        var act = _colorDragAction;
        _colorDragAction = null;
        if (act == null) { _dirty = true; return; }

        bool inside = SelectionHandles.PickContains(x, y, LiveSelectionFrame.CanvasAabb, FurnitureDpi,
                                                    FurnitureViewport, SelectionHandles.SwatchCount,
                                                    SingleTextBoxSelection);
        if (inside && act.HasChange)
        {
            Doc.CommitInteractive(act);
            Console.WriteLine($"自定义取色：{act.TargetCount} 个对象 → "
                              + $"RGB({_pickH:F2}, {_pickS:F2}, {_pickV:F2})");
        }
        else
        {
            act.Undo(Doc);                     // 取消 / 没变化：预览撤回去，不记撤销
        }
        _propDragTargets.Clear();
        _dirty = true;
    }


    /// <summary>
    /// 操作条上鼠标悬停的是哪一格（-1 = 没在条上）。**只在没按住时算**：
    /// 拖动中指针早就离开按钮了，重算只会让高亮乱跳。
    ///
    /// 判据走 <see cref="SelectionBarShown"/>（和绘制、光标同一条）：那一块不在的时候
    /// 悬停高亮也不该亮。收起态（用户自己收的 / 画完自动选中那个框）只高亮那颗圆钮。
    /// </summary>
    /// <summary>单选一个文本框吗（决定用文字条还是通用条）。</summary>
    internal bool SingleTextBoxSelection
        => Doc.Selected.Count == 1 && Doc.Selected[0].Kind == StrokeKind.TextBox;

    /// <summary>
    /// 当前选中对象该用哪种选中条（2026-11-08）：
    /// 图片 → 5 格（保存代替颜色，无 AI）；图形 → 5 格（颜色，无 AI）；
    /// 文本 → 6 格（字号）；其余（墨迹/多选/混合）→ 6 格（颜色 + AI）。
    /// </summary>
    internal SelBarSurface BarSurfaceNow
    {
        get
        {
            int n = Doc.Selected.Count;
            if (n == 0) return SelBarSurface.Normal;
            if (n == 1 && Doc.Selected[0].Kind == StrokeKind.TextBox) return SelBarSurface.Text;

            bool allImages = true, allShapes = true;
            foreach (var s in Doc.Selected)
            {
                if (!s.IsImage) allImages = false;
                if (!Stroke.IsShapeKind(s.Kind)) allShapes = false;
            }
            if (allImages) return SelBarSurface.Image;
            if (allShapes) return SelBarSurface.Shape;
            return SelBarSurface.Normal;
        }
    }

    /// <summary>
    /// 当前选中对象的操作条矩形。**所有对象统一"给下限、不翻面"**；
    /// 文本框只是少三格（七格），位置规则和其他对象一模一样。
    /// 画（Overlay）和命中（这里）必须同一份。
    /// </summary>
    private RectF SelectionBarRect(in RectF aabb)
    {
        _ = aabb;                       // 位置用画布缓存：滚动时条跟着对象走
        return SelectionBarCanvasRect;
    }

    /// <summary>
    /// 打开选中条的弹层。**方案 A：两个入口、一个弹层**——
    /// 打开任意一个都先把另一个关掉，保证同一时间只有一个。
    /// </summary>
    internal void OpenSelectionMenu(bool ai)
    {
        SelAiMenuOpen = ai;
        SelMoreMenuOpen = !ai;
        SelMenuHover = -1;
        SelBarHover = -1;
        _dirty = true;
    }

    /// <summary>关掉当前弹层（没开是空操作）。</summary>
    internal void CloseSelectionMenu()
    {
        if (!SelAiMenuOpen && !SelMoreMenuOpen) return;
        SelAiMenuOpen = false;
        SelMoreMenuOpen = false;
        SelMenuHover = -1;
        _dirty = true;
    }

    /// <summary>每帧兜底：选中被清空（撤销/删除/点空白）时，弹层必须跟着关。</summary>
    internal void StepSelectionMenu()
    {
        if (Doc.Selected.Count == 0) CloseSelectionMenu();
        StepFontList();
    }

    /// <summary>当前弹层的行数；0 = 没有弹层。AI 1 行，`⋯` 随对象类型 2/6 行。</summary>
    internal int SelectionMenuRowsNow
        => SelAiMenuOpen ? AiMenuRows
         : SelMoreMenuOpen ? SelectionHandles.MoreBarOrder(BarSurfaceNow).Length
         : 0;

    /// <summary>
    /// AI 菜单这次给哪几项（2026-11-10）：
    ///   · 图片 → 「识别成题卡 / 识别作图」（图片只有一个识别入口，不再拆“识别文本”）；
    ///   · 手写 → 「识别文本 / 识别作图」；
    ///   · 文本框 / 矢量图 → 只有「识别作图」。
    /// </summary>
    internal enum AiMenuKind { PlotOnly, Hand, Image }

    internal AiMenuKind AiMenuKindNow
    {
        get
        {
            if (Doc.Selected.Count == 0) return AiMenuKind.PlotOnly;
            bool image = false;
            foreach (var s in Doc.Selected)
            {
                if (s == null) continue;
                if (s.Kind == StrokeKind.Freehand && s.Tool != Tool.Highlighter)
                    return AiMenuKind.Hand;
                if (s.Kind == StrokeKind.Image && s.Image != null) image = true;
            }
            return image ? AiMenuKind.Image : AiMenuKind.PlotOnly;
        }
    }

    internal int AiMenuRows
        => Doc.Selected.Count == 0 ? 0
         : AiMenuKindNow == AiMenuKind.PlotOnly ? 1 : 2;

    /// <summary>AI 菜单才有状态行（识别进度/结果）；`⋯` 没有。</summary>
    internal bool SelectionMenuHasStatusNow
        => SelAiMenuOpen && HandOcrStatus.Length > 0;

    /// <summary>
    /// 当前弹层的矩形（画布坐标）。绘制、命中、脏区**都读这一份**；
    /// 和 `SelectionBarCanvasRect` 同一个锚点，下方放不下自动翻到上方。
    /// </summary>
    internal RectF SelectionMenuRectNow()
    {
        int rows = SelectionMenuRowsNow;
        if (rows <= 0 || BarDrawnCollapsed) return RectF.Empty;
        return SelectionHandles.BarMenuRectIn(
            SelectionBarCanvasRect, FurnitureDpi, FurnitureViewport, rows,
            SelectionMenuHasStatusNow,
            SelAiMenuOpen ? SelectionHandles.BarMenuWidthLogical
                          : SelectionHandles.BarMenuNarrowWidthLogical,
            centerX: true);
    }

    private void UpdateBarHover(float x, float y)
    {
        if (Doc.Selected.Count == 0) CloseSelectionMenu();
        int hover = -1;
        if (!_drawing && SelectionBarShown)
        {
            if (BarDrawnCollapsed)
            {
                var dot = SelectionDotCanvasRect;
                hover = dot.Contains(x, y) ? (int)SelBarButton.Collapse : -1;
            }
            else hover = SelectionHandles.BarButtonAtIn(x, y, SelectionBarCanvasRect, FurnitureDpi,
                SelectionHandles.BarButtonCountFor(BarSurfaceNow));
        }

        int menuRow = -1;
        var menu = SelectionMenuRectNow();
        if (!_drawing && !menu.IsEmpty)
        {
            menuRow = SelectionHandles.BarMenuRowAt(x, y, menu, FurnitureDpi, SelectionMenuRowsNow);
            if (menuRow < 0) menuRow = -1;   // -2 = 点在菜单边距/状态行，不算行
        }
        // 字体下拉：悬停行高亮。
        if (FontListOpen)
        {
            int lr = SelectionHandles.FontListRowAt(x, y, FontListRectNow(), FurnitureDpi, FontListCapacityNow, FontListScrollPx);
            if (lr != FontListHover) { FontListHover = lr; _dirty = true; }
        }
        if (SelMenuHover != menuRow) { SelMenuHover = menuRow; _dirty = true; }
        if (menuRow >= 0) hover = -1;

        if (hover == SelBarHover) return;
        SelBarHover = hover;
        _dirty = true;
    }

    /// <summary>
    /// 手势开始：把这一批对象从**内容层**里摘出去（方案 B）。
    ///
    /// 三步，代价 O(文档条数)，而且一个手势只做一次：
    ///   ① 收成集合（内容层重画时判定 O(1)）；
    ///   ② 按文档顺序排一份（画的时候要 z 序，集合的遍历顺序不是 z 序）；
    ///   ③ 把它们**按下那一刻**压过的块标脏一次 —— 重画那些块时跳过这一批，
    ///      于是"它们原来待的地方"在内容层里当场就干净了；之后的整个手势期间
    ///      内容层一帧都不用再动（相机滚动也照旧成立，因为块活在画布空间）。
    /// </summary>
    private void DetachForDrag()
    {
        _detachSet.Clear();
        _detachOrdered.Clear();
        foreach (var s in _dragTargets) _detachSet.Add(s);
        if (_detachSet.Count > 0)
        {
            foreach (var s in Doc.Strokes)
                if (_detachSet.Contains(s)) _detachOrdered.Add(s);
            foreach (var s in _dragTargets) Doc.InvalidateContent(s.PaddedBounds);
        }
        _dragPrevBounds = PreviewBounds(Matrix3x2.Identity);
    }

    /// <summary>手势结束：这一批回到内容层（下一次重画就带上它们了）。</summary>
    private void ReattachAfterDrag()
    {
        _detachSet.Clear();
        _detachOrdered.Clear();
        _dragPrevBounds = RectF.Empty;
        _selDragMoved = false;
    }

}
