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
    //  选择手势（框选工具 = 选择工具）
    // =====================================================================

    // ---- 拖动期收装饰（方案 A）与拖动预览（方案 B）------------------------

    /// <summary>
    /// 拖动 / 旋转手势里"装饰该收起来"吗（方案 A）。
    ///
    /// 三条判据：
    ///   · 正在拖（<see cref="SelDragging"/>）；
    ///   · **真的动过**（超过 <see cref="ClickToleranceLogical"/>）——单击一下不该闪；
    ///   · 是**移动或旋转** —— 缩放手势不收：拖某个手柄时另外几个手柄是有用的参照，
    ///     而光标也已经说明抓的是哪一个。
    ///
    /// 为什么可以收：按下那一刻指针就被 SetCapture 接管了，手柄与操作条
    /// **此刻根本点不中**，画着就是"看得见、点不到"。
    /// 注意：**脏区照旧算**（见 Overlay.ComputeTransientBounds），靠两帧回溯把
    /// 消失的那一块擦干净。
    ///
    /// 2026-09-18 补：**拖端点也收**。这时候手柄停在模型里（旧位置），
    /// 而临时几何已经跟着指针走了——留着那个柄会指着一条线上根本没有的点。
    /// 收起来之后画面里只剩"临时几何 + 倾斜角读数"，恰好是此刻唯一有用的两件东西。
    /// </summary>
    internal bool SelChromeCollapsed
        => SelDragging && _selDragMoved
        && (_dragIsMove || _dragHandle == SelHandle.Rotate || _vertexDragging);

    /// <summary>拖动预览在不在：手势进行中，而且真的有一批对象被摘出来了。</summary>
    internal bool DragPreviewActive => SelDragging && !_vertexDragging && _detachOrdered.Count > 0;

    /// <summary>
    /// 拖动时给当前对象画中心十字的条件：
    /// **单选的图形/图片、真的整体移动过**。缩放手柄、旋转、手写墨迹、多选都不画。
    /// </summary>
    internal bool SelectionCenterCrossVisible
        => !PassThrough && SelDragging && _selDragMoved && _dragIsMove && !_vertexDragging
           && Doc.Selected.Count == 1 && Doc.Selected[0].IsShape;

    /// <summary>拖动预览要画的那一批（按文档顺序，用 <see cref="DragPreviewMatrix"/> 变换）。</summary>
    internal IReadOnlyList<Stroke> DragPreviewStrokes => _detachOrdered;

    /// <summary>
    /// 拖动预览的实时变换（画布坐标），乘在对象**按下那一刻**的变换之上。
    ///
    /// 内容层那条老路会把对象变换成"按下时的变换 × 这个矩阵"，这里左乘的正是它，
    /// 所以两种画法应当像素一致（自检里有一条专门盯这件事）。
    /// </summary>
    internal Matrix3x2 DragPreviewMatrix => _selDragMatrix;

    /// <summary>这一条现在被摘出内容层了吗（内容层重画一块时要跳过它）。</summary>
    internal bool IsContentDetached(Stroke s) => _detachSet.Count > 0 && _detachSet.Contains(s);

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
        float dpi = UiUnit;
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
        var part = SelectionHandles.PickPartAt(x, y, aabb, UiUnit, FurnitureViewport,
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
        float t = SelectionHandles.SliderTAt(x, aabb, UiUnit, FurnitureViewport, SelectionHandles.SwatchCount);
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
            var r = SelectionHandles.PickHueRect(aabb, UiUnit, FurnitureViewport, sc, textBar);
            if (r.MaxY > r.MinY) _pickH = Math.Clamp((y - r.MinY) / (r.MaxY - r.MinY), 0f, 1f);
        }
        else
        {
            var r = SelectionHandles.PickSvRect(aabb, UiUnit, FurnitureViewport, sc, textBar);
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

        bool inside = SelectionHandles.PickContains(x, y, LiveSelectionFrame.CanvasAabb, UiUnit,
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
            SelectionBarCanvasRect, UiUnit, FurnitureViewport, rows,
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
            else hover = SelectionHandles.BarButtonAtIn(x, y, SelectionBarCanvasRect, UiUnit,
                SelectionHandles.BarButtonCountFor(BarSurfaceNow));
        }

        int menuRow = -1;
        var menu = SelectionMenuRectNow();
        if (!_drawing && !menu.IsEmpty)
        {
            menuRow = SelectionHandles.BarMenuRowAt(x, y, menu, UiUnit, SelectionMenuRowsNow);
            if (menuRow < 0) menuRow = -1;   // -2 = 点在菜单边距/状态行，不算行
        }
        // 字体下拉：悬停行高亮。
        if (FontListOpen)
        {
            int lr = SelectionHandles.FontListRowAt(x, y, FontListRectNow(), UiUnit, FontListCapacityNow, FontListScrollPx);
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

    // ---------------------------------------------------------------------
    //  图形端点编辑：起手 / 每帧 / 提交
    // ---------------------------------------------------------------------

    /// <summary>
    /// 定义元素手柄按下：起一次"改几何"的手势。
    ///
    /// 复用 <see cref="DetachForDrag"/> 那套：**只在这一刻**把这条图形压过的块标脏一次、
    /// 并且让内容层在重画时跳过它——于是它原来待的地方当场就干净了，
    /// 之后整个手势期内容层一帧都不用再动（自检里"内容层一帧不重画"盯的就是这条）。
    /// </summary>
    private void BeginVertexDrag(Stroke s, ShapeHandle h, float x, float y)
    {
        _vertexDragging = true;
        _vertexTarget = s;
        _vertexHandle = h;
        // 格数**不能只看"现在有几个点"**：双曲线的第三个点（"曲线经过的那个点"）是**可缺的**
        // （刚起手、或迁移前的老对象就只有两个），而"拖曲线上的那个点"这个手柄照样要能按下——
        // 按下就得往第三格写，格数不够就是**数组越界**（自检里当场崩过一次）。
        // 提交时会把这第三格写进模型（ApplyGeometry），对象也就从两个点补成三个。
        _vertexPreviewLocal = new Vector2[Math.Max(s.Points.Count, Stroke.MinCurvePoints(s.Kind))];
        _vertexPrevBounds = RectF.Empty;
        _vertexPreviewCanvas = SelectionHandles.ShapeHandleCanvasPosition(s, h);
        // "这一拖不是拖格距"（0 = 没在拖；格距自己不许是 0，见那个字段的注释）。
        _vertexPreviewGridStep = 0f;
        // 预览初始就是原样（这个时候还没动，画面不该有任何变化）。
        WriteVertexLocalPoints(_vertexPreviewCanvas, snapping: false);
        UpdateVertexReadout();
        _vertexSnapped = false;
        _shapeSnap = ShapeSnapKind.None;
        _shapeSnapText = "";
        _shapeSnapAnchor = _vertexPreviewCanvas;
        // 坐标系轴端吸附网格时用的"起手格距"：拖动过程中格距可能跟着外框变（自动档），
        // 用起手那一刻的值才能吸得稳；真吸上了就把这一档钉住（见 WriteVertexLocalPoints）。
        _vertexAxisGridStep = s.Kind == StrokeKind.Coordinate && s.Grid
            ? s.AxisGridStepLocal() : 0f;
        _vertexPinGridStep = 0f;

        _dragHandle = SelectionHandles.SelHandleOf(h);
        _dragIsMove = false;
        _dragStartPoint = new Vector2(x, y);
        _dragTargets = new[] { s };
        _selDragMatrix = Matrix3x2.Identity;
        _selDragMoved = false;
        DetachForDrag();

        SelDragging = true;
        _dirty = true;
    }

    /// <summary>
    /// 拖动中：**模型一个字不改**——只在
    /// <see cref="_vertexPreviewLocal"/> / <see cref="_vertexPreviewCanvas"/> 里攒出
    /// "临时几何"，由浮动层每帧画出来（<see cref="OverlayWindow"/> 的 DrawVertexPreview）。
    ///
    /// 坐标系有两层，错一层元素就会飞：指针和手柄在**画布坐标**，
    /// 而定义元素存在对象的**局部坐标**里，中间隔着 `Transform`（旋转过的图形必须过
    /// `Transform⁻¹` 才能改点）。
    ///
    /// 吸附有**两套**，各管各的：
    ///   · 直线 / 箭头 → **方向**吸附（绕另一个端点转、保持长度）；
    ///   · 椭圆 / 三角形 / 平行四边形 → **特殊形状**吸附（正圆 / 等腰 / 等边 / 直角 /
    ///     菱形 / 矩形），在 <see cref="WriteVertexLocalPoints"/> 里做，
    ///     语义是"把被拖的那个点修正到恰好满足约束的位置"（规格 9.6）。
    /// 两套都可能同时"吸住"，所以胶囊上的字由 <see cref="ShapeSnapKind"/> 与
    /// α 读数分别负责，互不覆盖。
    /// </summary>
    private void UpdateVertexDrag(float x, float y)
    {
        var s = _vertexTarget;
        if (s == null) return;

        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        var canvasPoint = new Vector2(x, y);
        bool snapped = false;
        if (s.Kind is StrokeKind.Line or StrokeKind.Arrow)
        {
            // "对面那个端点"：拖哪一头就绕另一头转，才符合"我刚拖的这头动、那头不动"。
            var fixedHandle = _vertexHandle == ShapeHandle.Anchor ? ShapeHandle.Rim : ShapeHandle.Anchor;
            var fixedPoint = SelectionHandles.ShapeHandleCanvasPosition(s, fixedHandle);
            canvasPoint = SelectionHandles.SnapEndPoint(fixedPoint, canvasPoint, shift, alt, out snapped);
        }

        // 脏区只标"被拖的那个元素**走过的那一小段**"：旧、新两个小矩形分开加
        // （上一帧 ∪ 这一帧，和拖动预览同一套账）。整条线的包围盒横跨屏幕时几乎是整屏，
        // 而这里动的其实只有一个元素——按整条算等于每帧白重画一大片。
        if (!_vertexPrevBounds.IsEmpty) Doc.Dirty.Add(_vertexPrevBounds);
        var prevCanvas = _vertexPreviewCanvas;
        _vertexPreviewCanvas = canvasPoint;
        _vertexSnapped = snapped;
        _shapeSnapAnchor = canvasPoint;         // 吸住时胶囊贴在被拖的那个元素上
        WriteVertexLocalPoints(canvasPoint);    // 里面顺手判"特殊形状吸附"（见 9.6）
        _vertexPrevBounds = EndpointDirtyRect(prevCanvas, canvasPoint, s.Width * 0.5f + 2f);
        if (!_vertexPrevBounds.IsEmpty) Doc.Dirty.Add(_vertexPrevBounds);

        UpdateVertexReadout();

        if (!_selDragMoved
            && Vector2.Distance(new Vector2(x, y), _dragStartPoint) > ClickToleranceLogical * DpiScale)
            _selDragMoved = true;
        _dirty = true;
    }

    /// <summary>
    /// 松手：把攒了一路的临时几何**提交成一步撤销**（改的是几何，不是变换）。
    ///
    /// 一点没移动就不提交——按一下端点手柄本来会多出一条"原样"的撤销记录。
    /// </summary>
    private void CommitVertexDrag()
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        bool moved = _selDragMoved;
        // 这一拖是不是"拖焦点三角形的顶点 P"（见 _vertexPreviewFocusU）——
        // 取值要在**清字段之前**，而且只有真的动过才算数。
        float focusU = _vertexPreviewFocusU;
        bool focusDrag = moved && s != null && !float.IsNaN(focusU);
        // 同一个道理：这一拖是不是"拖坐标系的格距手柄"（见 _vertexPreviewGridStep）。
        float gridStep = _vertexPreviewGridStep;
        bool gridDrag = moved && s != null && gridStep > 0f;
        // 轴端吸到网格时要把起手那一档格距钉住（见 WriteVertexLocalPoints）。
        float pinGridStep = _vertexPinGridStep;
        bool pinGrid = moved && s != null && pinGridStep > 0f && !gridDrag;

        _vertexDragging = false;
        _vertexTarget = null;
        _vertexPreviewLocal = null;
        _vertexPreviewFocusU = float.NaN;       // 手势结束：预览字段归位
        _vertexPreviewGridStep = 0f;
        _vertexPrevBounds = RectF.Empty;
        _dragTargets = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _selDragMoved = false;
        _shapeSnap = ShapeSnapKind.None;        // 手势结束：胶囊跟着消失
        _shapeSnapText = "";
        _vertexAxisGridStep = 0f;
        _vertexPinGridStep = 0f;

        // 拖 P：**点表一个字没变**（P 不是控制点），只有那个参数角要写
        //（撤销要不要跟着回，见 SetStrokeGeometryAction 的 newFocusU）。
        // ⚠ 传的是 `float?`：只有"真的在拖 P"才带值，别的改几何动作传 null（一个字都不动 P）。
        if (focusDrag) Doc.ApplyGeometry(s, pts, focusU);
        // 拖格距：同一个套路——点表一个字没变（格距不是控制点），只有那一个数要写。
        else if (gridDrag) Doc.ApplyGeometry(s, pts, null, gridStep);
        // 拖轴端且吸到了网格：点表和格距一起写（格距钉住，网格线才不会跑掉）。
        else if (pinGrid) Doc.ApplyGeometry(s, pts, null, pinGridStep);
        else if (moved && s != null && pts != null) Doc.ApplyGeometry(s, pts);
        // 一点没动（只是点了一下端点手柄松手）：**也必须把它压过的那块重画一次**——
        // 起手那一下它已经从内容层摘出去了，不重画的话这一块就一直是"没有这条线"，
        // 屏幕上的线会当场消失（松手后模型其实什么都没变）。
        // 走 InvalidateContent 而不是 Dirty.Add：内容层只在**版本号变了**时才消费脏区
        // （见那个函数的注释），少抬一次版本号就等于白标。
        else if (s != null) Doc.InvalidateContent(s.PaddedBounds);

        // 回到内容层：提交那一步已经把旧位与新位都标脏了，所以这一帧的重画一定会
        // 带上它（而不是继续画预览）。
        ReattachAfterDrag();
        _dirty = true;
    }

    /// <summary>
    /// 把"被拖的定义元素落在画布坐标 <paramref name="canvasPoint"/>"写进预览的局部点。
    ///
    /// 这里是**全部拖动语义**所在：同一个函数要伺候六种对象
    /// （直线/箭头改端点、圆改半径、圆心平移、椭圆改一条半轴、三角形/平行四边形改顶点），
    /// 所以按 `(Kind, 手柄)` 分派，而不是"替换第 N 个点"。
    ///
    /// 三条硬规矩：
    ///   · 指针在**画布坐标**、点存在**局部坐标**，必须过 `Transform⁻¹`（旋转过的对象尤其）；
    ///   · 拖圆心/中心时**两个点一起平移**——半径 / 半轴因此逐位不变
    ///     （这也是"拖圆心 = 平移"的判据）；
    ///   · **特殊形状吸附**（规格 9.6）只在这条路上做：它改的是"被拖的那个点"，
    ///     而不是形状的种类。`Alt` 传进去就变成完全自由。
    /// </summary>
    private void WriteVertexLocalPoints(Vector2 canvasPoint, bool snapping = true)
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        // 拷的是"模型里**现在有的**点"，而 `pts` 可能**比它长**（双曲线那个可缺的第三个点，
        // 见 BeginVertexDrag）——长出来的那几格先留零向量，由下面的分支按需填。
        for (int i = 0; i < pts.Length; i++)
            pts[i] = i < s.Points.Count ? new Vector2(s.Points[i].X, s.Points[i].Y) : Vector2.Zero;

        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        float lenTol = SelectionHandles.ShapeSnapLengthToleranceLogical * DpiScale;
        _shapeSnap = ShapeSnapKind.None;        // 每帧重判：不吸的帧必须回到"没有"
        _shapeSnapText = "";
        _vertexPinGridStep = 0f;

        // **坐标系原点吸附**（2026-11-03）：画布中心 / 四条边，横竖各判各的。
        // 在**画布坐标**里吸，过了这一关才逆变换回局部坐标——否则旋转 / 缩放过的
        // 坐标系会吸到一个和屏幕上看到的完全无关的位置。
        if (snapping && !alt && s.Kind == StrokeKind.Coordinate
            && _vertexHandle == ShapeHandle.Vertex2)
        {
            canvasPoint = SnapCoordinateOrigin(s, canvasPoint, out _shapeSnap, out string snapText);
            _shapeSnapText = snapText;
            _shapeSnapAnchor = canvasPoint;
        }
        else if (snapping && !alt && s.Kind == StrokeKind.SpaceCoordinate
                 && _vertexHandle == ShapeHandle.SpaceOrigin)
        {
            canvasPoint = SnapSpaceOrigin(canvasPoint, out _shapeSnap, out string snapText);
            _shapeSnapText = snapText;
            _shapeSnapAnchor = canvasPoint;
        }

        var local = SelectionHandles.ToLocalPoint(canvasPoint, s.Transform);
        var c = s.ShapeCenterLocal;
        float minAxis = ShapeMinAxisLogical * DpiScale;

        switch (s.Kind)
        {
            case StrokeKind.Trapezoid:
            {
                float minW = ShapeMinAxisLogical * DpiScale;
                float minH = ShapeMinAxisLogical * DpiScale;
                if (pts.Length < 4) break;

                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0 || idx >= 4) break;
                if (idx <= 1)
                {
                    // 下底：两个底点的 y 一起走，保证底边水平。
                    float topY = MathF.Min(pts[2].Y, pts[3].Y);
                    pts[0].Y = pts[1].Y = MathF.Max(local.Y, topY + minH);
                    pts[idx].X = local.X;
                    // 拖过另一个底点：自动交换左右身份，仍是梯形，不交叉。
                    if (pts[0].X > pts[1].X)
                    {
                        (pts[0], pts[1]) = (pts[1], pts[0]);
                        _vertexHandle = _vertexHandle == ShapeHandle.Vertex0
                            ? ShapeHandle.Vertex1 : ShapeHandle.Vertex0;
                        idx = SelectionHandles.VertexIndex(_vertexHandle);
                    }
                    if (pts[1].X - pts[0].X < minW)
                    {
                        if (idx == 0) pts[1].X = pts[0].X + minW;
                        else pts[0].X = pts[1].X - minW;
                    }                }
                else
                {
                    // 上底：两个顶点的 y 一起走，且不能压到下底。
                    float bottomY = MathF.Max(pts[0].Y, pts[1].Y);
                    pts[2].Y = pts[3].Y = MathF.Min(local.Y, bottomY - minH);
                    pts[idx].X = local.X;
                    if (pts[2].X < pts[3].X)
                    {
                        (pts[2], pts[3]) = (pts[3], pts[2]);
                        _vertexHandle = _vertexHandle == ShapeHandle.Vertex2
                            ? ShapeHandle.Vertex3 : ShapeHandle.Vertex2;
                        idx = SelectionHandles.VertexIndex(_vertexHandle);
                    }
                    if (pts[2].X - pts[3].X < minW)
                    {
                        if (idx == 2) pts[3].X = pts[2].X - minW;
                        else pts[2].X = pts[3].X + minW;
                    }
                }
                if (!alt)
                {
                    float mb = (pts[0].X + pts[1].X) * 0.5f;
                    float mt = (pts[2].X + pts[3].X) * 0.5f;
                    float wb = pts[1].X - pts[0].X;
                    float wt = pts[2].X - pts[3].X;
                    if (MathF.Abs(wt - wb) <= lenTol)
                    {
                        float half = wb * 0.5f;
                        if (MathF.Abs(mb - mt) <= lenTol)
                        {
                            float mid = (mb + mt) * 0.5f;
                            pts[2].X = mid + half; pts[3].X = mid - half;
                            _shapeSnap = ShapeSnapKind.Rectangle;
                        }
                        else
                        {
                            pts[2].X = mt + half; pts[3].X = mt - half;
                            _shapeSnap = ShapeSnapKind.ParallelogramTrapezoid;
                        }
                    }
                    else if (MathF.Abs(mb - mt) <= lenTol)
                    {
                        float d = mb - mt;
                        pts[2].X += d; pts[3].X += d;
                        _shapeSnap = ShapeSnapKind.Isosceles;
                    }
                    else
                    {
                        int ti = SelectionHandles.VertexIndex(_vertexHandle);
                        bool right = (ti == 2 && MathF.Abs(pts[2].X - pts[1].X) <= lenTol)
                                  || (ti == 3 && MathF.Abs(pts[3].X - pts[0].X) <= lenTol)
                                  || (ti == 1 && MathF.Abs(pts[1].X - pts[2].X) <= lenTol)
                                  || (ti == 0 && MathF.Abs(pts[0].X - pts[3].X) <= lenTol);
                        if (right)
                        {
                            if (ti == 2) pts[2].X = pts[1].X;
                            else if (ti == 3) pts[3].X = pts[0].X;
                            else if (ti == 1) pts[1].X = pts[2].X;
                            else pts[0].X = pts[3].X;
                            _shapeSnap = ShapeSnapKind.RightAngle;
                        }
                    }
                }
                break;
            }

            case StrokeKind.Sector:
            {
                if (pts.Length < 3) break;
                var center = new Vector2(pts[0].X, pts[0].Y);
                float a0 = MathF.Atan2(pts[1].Y - center.Y, pts[1].X - center.X);
                float sweep = s.SectorSweepDegrees();
                if (_vertexHandle == ShapeHandle.Rim)
                {
                    float rOld = Vector2.Distance(center, new Vector2(pts[1].X, pts[1].Y));
                    float r = shift ? MathF.Max(minAxis, rOld)
                                    : MathF.Max(minAxis, Vector2.Distance(center, local));
                    if (!shift)
                        a0 = MathF.Atan2(local.Y - center.Y, local.X - center.X);
                    pts[1] = center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r;
                    pts[2] = center + new Vector2(MathF.Cos(a0 + sweep * MathF.PI / 180f),
                                                  MathF.Sin(a0 + sweep * MathF.PI / 180f)) * r;
                    break;
                }
                if (_vertexHandle == ShapeHandle.FocusPoint)
                {
                    float rOld = Vector2.Distance(center, new Vector2(pts[1].X, pts[1].Y));
                    float r = shift ? MathF.Max(minAxis, rOld)
                                    : MathF.Max(minAxis, Vector2.Distance(center, local));
                    float a1 = MathF.Atan2(local.Y - center.Y, local.X - center.X);
                    float d = (a1 - a0) * 180f / MathF.PI;
                    while (d < 0f) d += 360f;
                    if (!alt)
                    {
                        float snapped = MathF.Round(d / 15f) * 15f;
                        if (MathF.Abs(snapped - d) <= 3f) d = snapped;
                    }
                    sweep = Math.Clamp(d, 5f, 359f);
                    pts[1] = center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r;
                    pts[2] = center + new Vector2(MathF.Cos(a0 + sweep * MathF.PI / 180f),
                                                  MathF.Sin(a0 + sweep * MathF.PI / 180f)) * r;
                }
                break;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
            {
                // 顶点：**拖哪个只动哪个**，第四个角（平行四边形）永远是算出来的。
                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0 || idx >= pts.Length) break;
                pts[idx] = SelectionHandles.SnapPolygonVertex(
                    s.Kind, idx, pts[0], pts[1], pts[2], local, lenTol, alt, out _shapeSnap);
                break;
            }

            case StrokeKind.SpaceCoordinate:
            {
                // 2026-11-03：四个手柄各管一件事。O = 整组平移（否则轴会歪，
                // 就不再是"直角坐标系"）；三个轴端只改自己那一截的长度。
                if (pts.Length < 4) break;
                var o = new Vector2(pts[0].X, pts[0].Y);
                if (_vertexHandle == ShapeHandle.SpaceOrigin)
                {
                    var delta = local - o;
                    for (int i = 0; i < 4; i++) pts[i] += delta;
                    break;
                }
                if (snapping && _vertexHandle is ShapeHandle.SpaceX or ShapeHandle.SpaceY)
                {
                    var snapped = SnapSpaceAxis(s, _vertexHandle, o, local, shift, alt,
                                                out ShapeSnapKind sk, out string st);
                    if (sk != ShapeSnapKind.None)
                    {
                        local = snapped;
                        _shapeSnap = sk;
                        _shapeSnapText = st;
                        _shapeSnapAnchor = Vector2.Transform(local, s.Transform);
                    }
                }
                if (_vertexHandle == ShapeHandle.SpaceX)
                {
                    // x 轴端**自由拖**：位置同时决定长度和角度（有些题的投影夹角不是 60°）。
                    // 只夹最小长度，方向完全跟指针；Z 仍保持向上。
                    var d = local - o;
                    float len = d.Length();
                    var dir = len > 1e-3f ? d / len : new Vector2(1f, 0f);
                    len = MathF.Max(minAxis, len);
                    pts[1] = o + dir * len;
                    break;
                }
                if (_vertexHandle == ShapeHandle.SpaceZ)
                {
                    // z 永远向上（用户："Z 轴一般都是会向上"）：往下拖只缩到最小。
                    float len = MathF.Max(minAxis, o.Y - local.Y);
                    pts[3] = o + new Vector2(0f, -len);
                    break;
                }
                if (_vertexHandle == ShapeHandle.SpaceY)
                {
                    // y 轴端同样**自由拖**：长度和夹角都能调；z 不动。
                    var d = local - o;
                    float len = d.Length();
                    var dir = len > 1e-3f ? d / len : Stroke.SpaceAxisYDirection;
                    len = MathF.Max(minAxis, len);
                    pts[2] = o + dir * len;
                    break;
                }
                break;
            }

            case StrokeKind.Coordinate:
            {
                if (pts.Length < 3) break;
                var o = new Vector2(pts[2].X, pts[2].Y);
                float minLen = ShapeMinAxisLogical * DpiScale;

                // ---- 四个轴端：**只改自己那一截**（2026-11-03）----
                // 0 = 外框的 min 那一侧，1 = 外框的 max 那一侧；原点、另外三个端点都不动。
                if (_vertexHandle is ShapeHandle.AxisRight or ShapeHandle.AxisLeft
                                    or ShapeHandle.AxisTop or ShapeHandle.AxisBottom)
                {
                    bool horizontal = _vertexHandle is ShapeHandle.AxisRight or ShapeHandle.AxisLeft;
                    bool positive = _vertexHandle is ShapeHandle.AxisRight or ShapeHandle.AxisBottom;
                    var (minX, minY, maxX, maxY) = s.AxisFrameLocal();
                    float value = horizontal ? local.X : local.Y;

                    // 画布边吸附（和原点同一套 8 逻辑像素；边在原点外侧才接受）。
                    if (snapping && !alt)
                    {
                        float tol = CoordinateSnapRadiusLogical * DpiScale;
                        var vp = ViewportCanvas;
                        float edgeCanvas = horizontal
                            ? (positive ? vp.MaxX : vp.MinX)
                            : (positive ? vp.MaxY : vp.MinY);
                        // 画布边 → 局部坐标的同一个轴（坐标系不给旋转柄；这里只是把
                        // 多选整体缩放过的情形也一并覆盖）。
                        var edgeLocal = SelectionHandles.ToLocalPoint(
                            horizontal ? new Vector2(edgeCanvas, canvasPoint.Y)
                                       : new Vector2(canvasPoint.X, edgeCanvas), s.Transform);
                        float edge = horizontal ? edgeLocal.X : edgeLocal.Y;
                        bool edgeUsable = positive ? edge >= (horizontal ? o.X : o.Y) + minLen
                                                   : edge <= (horizontal ? o.X : o.Y) - minLen;
                        if (edgeUsable && MathF.Abs(value - edge) <= tol)
                        {
                            value = edge;
                            _shapeSnap = ShapeSnapKind.CanvasEdge;
                            _shapeSnapText = "画布边";
                        }
                        else if (s.Grid && _vertexAxisGridStep > 0f)
                        {
                            // 自己的网格：长度吸到格距的整数倍（1 格起）。
                            float scale = MathF.Sqrt(MathF.Abs(
                                s.Transform.M11 * s.Transform.M22 - s.Transform.M12 * s.Transform.M21));
                            if (scale < 1e-3f) scale = 1f;
                            float originOnAxis = horizontal ? o.X : o.Y;
                            float len = MathF.Abs(value - originOnAxis);
                            float k = MathF.Round(len / _vertexAxisGridStep);
                            float snapped = k * _vertexAxisGridStep;
                            if (k >= 1f && MathF.Abs(len - snapped) <= tol / scale)
                            {
                                value = originOnAxis + (positive ? snapped : -snapped);
                                _shapeSnap = ShapeSnapKind.GridStep;
                                _shapeSnapText = "网格";
                                // 自动格距会随外框短边变；吸上了就把当前这一档钉住，
                                // 否则松手后网格线又跑开，读数就成了假的。
                                _vertexPinGridStep = _vertexAxisGridStep;
                            }
                        }
                    }

                    // 夹最小值：轴端不许越过原点到另一侧（否则"正 / 负半轴"就翻面了）。
                    if (horizontal)
                    {
                        value = positive ? MathF.Max(value, o.X + minLen)
                                         : MathF.Min(value, o.X - minLen);
                        if (positive) maxX = value; else minX = value;
                    }
                    else
                    {
                        value = positive ? MathF.Max(value, o.Y + minLen)
                                         : MathF.Min(value, o.Y - minLen);
                        if (positive) maxY = value; else minY = value;
                    }
                    // 外框统一写回 min/max 两个角（顺序不承载语义，见 AxisFrameLocal）。
                    pts[0] = new Vector2(minX, minY);
                    pts[1] = new Vector2(maxX, maxY);
                    break;
                }

                // ---- 格距手柄（第 6 颗，开网格才有）----
                // 它**不是控制点**，而且**一个点都不动**——外框、原点都保持原样，改的只有格距。
                if (_vertexHandle == ShapeHandle.Vertex3)
                {
                    // 手柄落在"原点 + (格距, −格距)"上，所以两个方向的偏移量都等于格距；
                    // 取平均（= 打到那条对角线上的投影）而不是单看某一轴：拖动方向偏一点也不会跳。
                    var d = local - o;
                    float want = (MathF.Abs(d.X) + MathF.Abs(d.Y)) * 0.5f;
                    _vertexPreviewGridStep = Math.Clamp(want,
                        Stroke.AxisMinGridStepLocal,
                        Stroke.AxisMaxGridStepLocal(pts[0], pts[1]));
                    break;
                }

                if (_vertexHandle == ShapeHandle.Vertex2)
                {
                    // **原点：在框里自由挪**（2026-11-03 用户纠正："原本他的原点是可以拖动
                    // 位置的，比如刚开始在最中心，可以拖到左下角"）。
                    // 夹在框内是必须的：跑到框外，两条轴就都不在框里交叉了，
                    // 画出来是个说不清的东西（而贴在框角上正是想要的那种用法）。
                    // 特殊位置（框中心 / 四角 / 画布中心 / 画布边）的吸附在
                    // SnapCoordinateOrigin 里已经做完了，这里只负责夹取。
                    float fx0 = MathF.Min(pts[0].X, pts[1].X), fx1 = MathF.Max(pts[0].X, pts[1].X);
                    float fy0 = MathF.Min(pts[0].Y, pts[1].Y), fy1 = MathF.Max(pts[0].Y, pts[1].Y);
                    pts[2] = new Vector2(Math.Clamp(local.X, fx0, fx1),
                                         Math.Clamp(local.Y, fy0, fy1));
                    break;
                }
                break;
            }

            case StrokeKind.NumberLine:
            {
                // **数轴的两个端点**：只改 x，y 一律保持 —— 往斜上方拖也还是水平线
                // （数轴歪了就不是数轴了）。同时不许互相越过：越过去"左端/右端"
                // 这两个名字就撒谎了，手柄也会互换位置。
                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0 || idx >= pts.Length) break;
                float minLen = ShapeMinAxisLogical * DpiScale;
                float x = local.X;
                if (idx == 0) x = MathF.Min(x, pts[1].X - minLen);
                else x = MathF.Max(x, pts[0].X + minLen);
                pts[idx] = new Vector2(x, pts[0].Y);
                break;
            }

            case StrokeKind.Circle when _vertexHandle == ShapeHandle.Rim:
            {
                // 圆的圆周点：**只改半径，圆心钉住**。半径在局部坐标里量（两点距离），
                // 方向照指针走；太小就夹到最小值（不然圆退化成一点，看不见还点不中）。
                var dir = local - c;
                float r = dir.Length();
                if (r < minAxis)
                    local = c + (r > 1e-3f ? dir / r : new Vector2(1f, 0f)) * minAxis;
                pts[1] = local;
                break;
            }

            case StrokeKind.Circle when _vertexHandle == ShapeHandle.Anchor:
            {
                // 圆心：**整条平移**（半径逐位不变）。
                // 正常路径上拖它走的是"整体拖动"（改变换矩阵，见 TryBeginSelectionGesture），
                // 这里只是兜底——万一哪天真从这条路进来，语义也必须是"平移"而不是"改一个点"。
                //
                // `when` 必须写在这里：`case A: case B when 条件:` 的条件**只管 B**，
                // A 会无条件命中（这一条在下面 sine/cosine 那段也专门提醒过）。
                var d = local - c;
                pts[0] = c + d;
                pts[1] = s.RimLocalPoint() + d;
                break;
            }

            case StrokeKind.Ellipse when _vertexHandle == ShapeHandle.AxisRight:
            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.AxisRight:
            {
                // 右端点：**只改 a**，b 保持（这就是"每次只动那一条"）。
                // 往左拖过中心也照样缩（负数被下面的 minAxis 卡住），
                // 所以"想缩左边"不需要另一个手柄——抓右端点一路往左拖就行。
                // ⚠ 两个 case 都带 `when`：`case A: case B when 条件:` 的条件**只管 B**（老账）。
                float a = MathF.Max(minAxis, local.X - c.X);
                // 正圆吸附（规格 9.6）：|a − b| 在容差内就取成 b —— 于是 a、b 逐位相等。
                a = SelectionHandles.SnapEllipseAxis(a, s.SemiAxisBLocal, lenTol, alt, out bool snapA);
                if (snapA) _shapeSnap = ShapeSnapKind.Circle;
                pts[1] = new Vector2(c.X + a, c.Y + s.SemiAxisBLocal);
                break;
            }

            case StrokeKind.Ellipse when _vertexHandle == ShapeHandle.AxisTop:
            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.AxisTop:
            {
                // 上端点：**只改 b**，a 保持（同理，往下拖过中心也能缩）。
                float b = MathF.Max(minAxis, c.Y - local.Y);
                // 正圆吸附（规格 9.6）：|b − a| 在容差内就取成 a。
                b = SelectionHandles.SnapEllipseAxis(b, s.SemiAxisALocal, lenTol, alt, out bool snapB);
                if (snapB) _shapeSnap = ShapeSnapKind.Circle;
                pts[1] = new Vector2(c.X + s.SemiAxisALocal, c.Y + b);
                break;
            }

            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.FocusPoint:
            {
                // **焦点三角形的顶点 P**：它**不在 pts 里**（P 是参数角算出来的，
                // 不是控制点，见 ShapeHandle.FocusPoint），所以这里一个点都不写，
                // 只把"拖到哪儿"折成参数角记到预览字段里——浮动层拿它画预览，
                // 松手由 CommitVertexDrag 写进对象（那一步才进撤销栈）。
                _vertexPreviewFocusU = s.ConicEllipseAngleOf(local);
                break;
            }

            // 四种曲线（抛物线 / 双曲线 / 正弦 / 余弦）的特殊点手柄 2026-09-20 全砍了
            //（用户："通通按常规操作，给操作柄和旋转"），所以这里也没有它们的写回分支：
            // 那些"拖这个点 → 只改 p / 只改 a / 只改周期＋振幅"的算式一起删掉了。
            // 曲线现在走**通用框**（纯变换：缩放 / 旋转 / 平移），模型层一个字都不用改。
            default:
                // 直线 / 箭头：改哪一头就是哪一头（首点或末点）。
                pts[_vertexHandle == ShapeHandle.Anchor ? 0 : pts.Length - 1] = local;
                break;
        }
    }

    /// <summary>
    /// 坐标系**原点**的吸附：外框自己的中心 / 四边（＝四角）＋ 画布中心 / 四条边；
    /// 横竖各判各的（可以只吸 x 不吸 y）。
    ///
    /// 为什么先吸**外框**：老师最常用的动作是"原点拖到框的左下角，只留第一象限"。
    /// 外框角离得最近、又正是那个位置，先判它；画布中心 / 边是第二层，
    /// 用来把整个坐标系摆到屏幕的固定位置上。
    /// 为什么允许分轴吸：只吸 x 或只吸 y 也是常见摆法（贴左边、垂直居中），
    /// 两轴绑成一个点反而难摆。
    ///
    /// 半径 = <see cref="CoordinateSnapRadiusLogical"/>；`Alt` 在调用处已经排除。
    /// 返回的是**画布坐标**，文案给胶囊用（为空 = 没吸住）。
    /// </summary>
    private Vector2 SnapCoordinateOrigin(Stroke s, Vector2 p,
                                         out ShapeSnapKind kind, out string text)
    {
        kind = ShapeSnapKind.None;
        text = "";
        var vp = ViewportCanvas;
        float tol = CoordinateSnapRadiusLogical * DpiScale;
        float cx = (vp.MinX + vp.MaxX) * 0.5f;
        float cy = (vp.MinY + vp.MaxY) * 0.5f;
        float x = p.X, y = p.Y;
        string sx = "", sy = "";
        bool hitX = false, hitY = false;
        bool frameX = false, frameY = false;
        bool centerX = false, centerY = false;
        float bestX = tol, bestY = tol;

        // 外框在**画布坐标**里的中心 / 四条边（坐标系本身不可旋转，但可能被整体缩放）。
        var (minX, minY, maxX, maxY) = s.AxisFrameLocal();
        float fcx = (minX + maxX) * 0.5f, fcy = (minY + maxY) * 0.5f;
        var fCenter = Vector2.Transform(new Vector2(fcx, fcy), s.Transform);
        var fMin = Vector2.Transform(new Vector2(minX, minY), s.Transform);
        var fMax = Vector2.Transform(new Vector2(maxX, maxY), s.Transform);

        void TakeX(float target, string label, bool isFrame, bool isCenter)
        {
            float d = MathF.Abs(p.X - target);
            if (d <= tol && (!hitX || d < bestX))
            {
                x = target; sx = label; bestX = d; hitX = true;
                frameX = isFrame; centerX = isCenter;
            }
        }
        void TakeY(float target, string label, bool isFrame, bool isCenter)
        {
            float d = MathF.Abs(p.Y - target);
            if (d <= tol && (!hitY || d < bestY))
            {
                y = target; sy = label; bestY = d; hitY = true;
                frameY = isFrame; centerY = isCenter;
            }
        }

        // 第一层：外框中心 / 四条边（＝四个角）。
        TakeX(fCenter.X, "框内水平居中", true, true);
        TakeX(fMin.X, "框左边缘", true, false);
        TakeX(fMax.X, "框右边缘", true, false);
        TakeY(fCenter.Y, "框内垂直居中", true, true);
        TakeY(fMin.Y, "框上边缘", true, false);
        TakeY(fMax.Y, "框下边缘", true, false);

        // 第二层：画布中心 / 四条边。
        TakeX(cx, "画布水平居中", false, true);
        TakeX(vp.MinX, "画布左边缘", false, false);
        TakeX(vp.MaxX, "画布右边缘", false, false);
        TakeY(cy, "画布垂直居中", false, true);
        TakeY(vp.MinY, "画布上边缘", false, false);
        TakeY(vp.MaxY, "画布下边缘", false, false);

        if (!hitX && !hitY) return p;

        string Join() => sx.Length > 0 && sy.Length > 0 ? sx + " + " + sy
                        : sx.Length > 0 ? sx : sy;

        if (frameX && frameY)
        {
            kind = ShapeSnapKind.FrameAnchor;
            if (centerX && centerY) text = "框中心";
            else if (sx == "框左边缘" && sy == "框下边缘") text = "左下角";
            else if (sx == "框右边缘" && sy == "框下边缘") text = "右下角";
            else if (sx == "框左边缘" && sy == "框上边缘") text = "左上角";
            else if (sx == "框右边缘" && sy == "框上边缘") text = "右上角";
            else text = Join();
        }
        else if (frameX || frameY)
        {
            kind = ShapeSnapKind.FrameAnchor;
            text = Join();
        }
        else if (centerX && centerY)
        {
            kind = ShapeSnapKind.CanvasCenter;
            text = "画布中心";
        }
        else
        {
            kind = ShapeSnapKind.CanvasEdge;
            text = Join();
        }
        return new Vector2(x, y);
    }

    /// <summary>
    /// 空间坐标系**轴端**的角度吸附（2026-11-03）：
    ///   · **单根轴**吸到特殊角（<see cref="SelectionHandles.SpecialInclinationDegrees"/>：
    ///     0/30/45/60/90/120/135/150；`Shift` 是 15° 硬档，`Alt` 关闭）；
    ///   · **两轴夹角**也吸到同一组特殊角（拖 X 时 Y 固定，拖 Y 时 X 固定）。
    /// 两个候选都算出来，取**离指针更近**的那个；平手时优先“夹角”，因为数学题更关心夹角。
    /// 返回**局部坐标**；文案由这里一起给出，胶囊上直接写吸到了什么。
    /// </summary>
    private Vector2 SnapSpaceAxis(Stroke s, ShapeHandle handle, Vector2 o, Vector2 p,
                                  bool shift, bool alt,
                                  out ShapeSnapKind kind, out string text)
    {
        kind = ShapeSnapKind.None;
        text = "";
        if (alt) return p;
        float len = Vector2.Distance(o, p);
        if (len < 1e-3f) return p;

        float pointerAng = MathF.Atan2(p.Y - o.Y, p.X - o.X);
        float AngleOf(Vector2 q) => MathF.Atan2(q.Y - o.Y, q.X - o.X);
        float DeltaTo(Vector2 q)
        {
            float d = SelectionHandles.NormalizeDegrees((AngleOf(q) - pointerAng) * 180f / MathF.PI);
            return MathF.Min(d, 360f - d);
        }

        // 候选一：单根轴吸到特殊角（SnapEndPoint 已经处理 Shift / 软吸附 / 方向侧）。
        var absPt = SelectionHandles.SnapEndPoint(o, p, shift, alt, out bool absSnap);
        float absDelta = absSnap ? DeltaTo(absPt) : float.MaxValue;

        // 候选二：两轴夹角吸到特殊角（x–y 取 0~180 的较小角）。
        var other = handle == ShapeHandle.SpaceX
            ? new Vector2(s.Points[2].X, s.Points[2].Y)
            : new Vector2(s.Points[1].X, s.Points[1].Y);
        var ov = other - o;
        bool relSnap = false;
        float relDeg = 0f;
        Vector2 relPt = p;
        float relDelta = float.MaxValue;
        if (ov.LengthSquared() > 1e-6f)
        {
            float raw = (pointerAng - AngleOf(other)) * 180f / MathF.PI;
            raw = SelectionHandles.NormalizeDegrees(raw);
            if (raw > 180f) raw -= 360f;               // -180..180
            float absRaw = MathF.Abs(raw);
            if (absRaw > 0.5f && absRaw < 179.5f)
            {
                float snapped = SelectionHandles.SnapFoldedDegrees(
                    absRaw, SelectionHandles.SpecialInclinationDegrees, shift, alt, out relSnap);
                if (relSnap && snapped > 0.5f && snapped < 179.5f)
                {
                    float sign = raw < 0f ? -1f : 1f;
                    float a = AngleOf(other) + sign * snapped * MathF.PI / 180f;
                    relPt = o + new Vector2(MathF.Cos(a), MathF.Sin(a)) * len;
                    relDeg = snapped;
                    relDelta = DeltaTo(relPt);
                }
            }
        }

        bool useRel = relSnap && (!absSnap || relDelta <= absDelta);
        if (!useRel && !absSnap) return p;

        var result = useRel ? relPt : absPt;
        kind = ShapeSnapKind.AxisAngle;
        if (useRel)
            text = $"x–y {relDeg:F0}°";
        else
        {
            // 显示按“数学角”（y 向上）：右 0°、上 90°、左 180°、下 270°。
            float vx = result.X - o.X, vy = result.Y - o.Y;
            float display = SelectionHandles.NormalizeDegrees(
                -MathF.Atan2(vy, vx) * 180f / MathF.PI);
            text = (handle == ShapeHandle.SpaceX ? "x 轴 " : "y 轴 ") + $"{display:F0}°";
        }
        return result;
    }


    /// 8 逻辑像素，横竖分开判，`Alt` 在调用处已经排除。
    /// </summary>
    /// <summary>
    /// 空间坐标系**原点**的吸附：只吸画布中心 / 四条边（没有"自己的框"——
    /// 正半轴坐标系的框是三个轴端现算的，吸框角没有意义）。
    /// 8 逻辑像素，横竖分开判，`Alt` 在调用处已经排除。
    /// </summary>
    private Vector2 SnapSpaceOrigin(Vector2 p, out ShapeSnapKind kind, out string text)
    {
        kind = ShapeSnapKind.None;
        text = "";
        var vp = ViewportCanvas;
        float tol = CoordinateSnapRadiusLogical * DpiScale;
        float cx = (vp.MinX + vp.MaxX) * 0.5f;
        float cy = (vp.MinY + vp.MaxY) * 0.5f;
        float x = p.X, y = p.Y;
        string sx = "", sy = "";
        bool centerX = false, centerY = false;

        if (MathF.Abs(p.X - cx) <= tol) { x = cx; sx = "画布水平居中"; centerX = true; }
        else if (MathF.Abs(p.X - vp.MinX) <= tol) { x = vp.MinX; sx = "画布左边缘"; }
        else if (MathF.Abs(p.X - vp.MaxX) <= tol) { x = vp.MaxX; sx = "画布右边缘"; }

        if (MathF.Abs(p.Y - cy) <= tol) { y = cy; sy = "画布垂直居中"; centerY = true; }
        else if (MathF.Abs(p.Y - vp.MinY) <= tol) { y = vp.MinY; sy = "画布上边缘"; }
        else if (MathF.Abs(p.Y - vp.MaxY) <= tol) { y = vp.MaxY; sy = "画布下边缘"; }

        if (sx.Length == 0 && sy.Length == 0) return p;
        kind = centerX && centerY ? ShapeSnapKind.CanvasCenter : ShapeSnapKind.CanvasEdge;
        text = centerX && centerY ? "画布中心"
             : sx.Length > 0 && sy.Length > 0 ? sx + " + " + sy
             : sx.Length > 0 ? sx : sy;
        return new Vector2(x, y);
    }

    ///
    /// 量的都是**用户看到的那个量**：
    ///   · 直线的 α 按**画布坐标**量（旋转过的线，局部角不等于屏幕角）；
    ///   · 圆的 r 按**画布坐标**量（屏幕上这个圆多大）；
    ///   · 椭圆的 a / b 按**局部坐标**量 —— 那是椭圆自己的半轴
    ///     （它转到哪个方向，长轴都是那么长）。
    /// </summary>
    private void UpdateVertexReadout()
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        if (s == null || pts == null || pts.Length < 2)
        {
            _vertexReadout = VertexReadoutKind.None;
            return;
        }

        var c = Vector2.Transform(pts[0], s.Transform);
        var e = Vector2.Transform(pts[^1], s.Transform);
        switch (s.Kind)
        {
            case StrokeKind.Circle:
            {
                float r = Vector2.Distance(c, e);
                _vertexReadout = VertexReadoutKind.Radius;
                _vertexReadoutValue = r;
                _vertexReadoutSecondary = r * 2f;
                break;
            }

            case StrokeKind.ConicEllipse when _vertexHandle == ShapeHandle.FocusPoint:
            {
                // 拖**焦点三角形的顶点 P**：这一拖没有读数。
                // 理由同三角形顶点那条：P 本身不是老师要看的那个数，
                // 而"|PF₁| + |PF₂| = 2a"是**另一个**读数（不在这一批里，别把 α 报出来充数）。
                _vertexReadout = VertexReadoutKind.None;
                break;
            }

            case StrokeKind.Ellipse:
            case StrokeKind.ConicEllipse:
            {
                bool vertical = _vertexHandle == ShapeHandle.AxisTop;
                var d = pts[^1] - pts[0];
                _vertexReadout = vertical ? VertexReadoutKind.AxisB : VertexReadoutKind.AxisA;
                _vertexReadoutValue = vertical ? MathF.Abs(d.Y) : MathF.Abs(d.X);
                _vertexReadoutSecondary = 0f;
                break;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
            case StrokeKind.Trapezoid:
                // 三角形 / 平行四边形 / 梯形**这一轮没有读数**：它们要显示的是内角 / 夹角，
                // 那是第③轮（9.7）的事；现在改顶点时只有"吸到了什么"那一颗胶囊
                // （见 ShapeSnapKind）。**不要**落到 default 去报一个 α：
                // 三个顶点之间根本没有"倾斜角"这个量。
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            case StrokeKind.Sector:
            {
                if (_vertexHandle == ShapeHandle.Rim)
                {
                    float r = Vector2.Distance(pts[0], pts[1]);
                    _vertexReadout = VertexReadoutKind.Radius;
                    _vertexReadoutValue = r;
                    _vertexReadoutSecondary = r * 2f;
                }
                else
                {
                    float a0 = MathF.Atan2(pts[1].Y - pts[0].Y, pts[1].X - pts[0].X);
                    float a1 = MathF.Atan2(pts[2].Y - pts[0].Y, pts[2].X - pts[0].X);
                    float deg = (a1 - a0) * 180f / MathF.PI;
                    while (deg < 0f) deg += 360f;
                    _vertexReadout = VertexReadoutKind.SectorAngle;
                    _vertexReadoutValue = Math.Clamp(deg, 0f, 359f);
                    _vertexReadoutSecondary = 0f;
                }
                break;
            }

            // 四种曲线的读数（p / 实半轴 / 虚半轴 / 周期＋振幅）2026-09-20 随手柄一起删了：
            // 读数挂在手柄上，手柄没了，那个量就没人拖得动它们了（见 ShapeHandlesOf）。
            case StrokeKind.SpaceCoordinate:
            {
                if (_vertexHandle is ShapeHandle.SpaceX or ShapeHandle.SpaceY)
                {
                    var xv = pts[1] - pts[0];
                    var yv = pts[2] - pts[0];
                    float lx = xv.Length(), ly = yv.Length();
                    if (lx > 1e-3f && ly > 1e-3f)
                    {
                        float cos = Math.Clamp(Vector2.Dot(xv, yv) / (lx * ly), -1f, 1f);
                        _vertexReadout = VertexReadoutKind.SpaceAngle;
                        _vertexReadoutValue = MathF.Acos(cos) * 180f / MathF.PI;
                        _vertexReadoutSecondary = 0f;
                        break;
                    }
                }
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;
            }

            case StrokeKind.Coordinate:
            {
                float fx0 = MathF.Min(pts[0].X, pts[1].X);
                float fx1 = MathF.Max(pts[0].X, pts[1].X);
                float fy0 = MathF.Min(pts[0].Y, pts[1].Y);
                float fy1 = MathF.Max(pts[0].Y, pts[1].Y);
                var o = pts[2];
                switch (_vertexHandle)
                {
                    case ShapeHandle.AxisRight:
                    case ShapeHandle.AxisLeft:
                        _vertexReadout = VertexReadoutKind.AxisXLength;
                        _vertexReadoutValue = _vertexHandle == ShapeHandle.AxisRight
                            ? fx1 - o.X : o.X - fx0;
                        break;
                    case ShapeHandle.AxisTop:
                    case ShapeHandle.AxisBottom:
                        _vertexReadout = VertexReadoutKind.AxisYLength;
                        _vertexReadoutValue = _vertexHandle == ShapeHandle.AxisTop
                            ? o.Y - fy0 : fy1 - o.Y;
                        break;
                    case ShapeHandle.Vertex3:
                        // 格距手柄：预览字段在拖动中才有值；没拖时显示当前格距。
                        _vertexReadout = VertexReadoutKind.GridStep;
                        _vertexReadoutValue = _vertexPreviewGridStep > 0f
                            ? _vertexPreviewGridStep : s.AxisGridStepLocal();
                        break;
                    default:
                        // 原点：这一拖只有"吸到了什么"那颗胶囊，没有长度读数。
                        _vertexReadout = VertexReadoutKind.None;
                        break;
                }
                _vertexReadoutSecondary = 0f;
                break;
            }

            case StrokeKind.NumberLine:
                // 数轴同样**没有角度读数**：两个定义元素之间没有"倾斜角"可言
                // （轴永远是水平的，刻度间距也只是个长度）。
                // 落到 default 去报 α 的话，拖端点会给出一个毫无意义的度数。
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            default:
                _vertexReadout = VertexReadoutKind.Inclination;
                _vertexReadoutValue = SelectionHandles.InclinationDegrees(c, e);
                // 直线 / 箭头再报一个**长度**（2026-10-05 用户：和 α 同一个逻辑）。
                _vertexReadoutSecondary = Vector2.Distance(c, e);
                break;
        }
    }

    /// <summary>
    /// 多边形读数（规格 9.7）：三角形 = **三个内角**，平行四边形 = **它自己那两个夹角**。
    /// 两个都**只在拖顶点时显示**。
    ///
    /// **2026-09-20 用户定**："三角形应该在拖动的时候再显示角度，要不然看起来也乱"——
    /// 在那之前三角形是"一选中就显示三个角"，静止摆着也一直挂着三颗角标。
    /// 现在它和平行四边形**走同一条判据**（平行四边形从 2026-09-19 起就是这样）。
    /// 顺带也没了"整体移动 / 旋转三角形时显示角度"：转一个三角形并不会改变它的内角，
    /// 那几个数在那两件事里都是噪音。
    ///
    /// 两个输出：<paramref name="vertices"/> 是那些角的顶点（**画布坐标**，标签贴在它外侧），
    /// <paramref name="degrees"/> 是角度（度）。返回要显示几个角（0 = 这一帧没有这套读数）。
    ///
    /// 顶点取**这一帧屏幕上那个几何**：拖顶点中模型一个字没改，所以走临时几何
    /// （`_vertexPreviewLocal`）乘上它自己的 `Transform`。
    /// **绘制与脏区都只走这一个函数**：两边各算一份的话，标签会按一个位置擦、按另一个位置画，
    /// 拖动久了屏幕上就留一条擦不掉的边（选中框那一套踩过这个坑）。
    /// </summary>
    internal int FillAngleReadout(Span<Vector2> vertices, Span<float> degrees)
    {
        // **只有"拖顶点中"才出读数**（见上面那段）：不是拖顶点这一件事 → 这一帧没有读数。
        if (!_vertexDragging || _vertexTarget == null
            || _vertexTarget.Kind is not (StrokeKind.Triangle or StrokeKind.Parallelogram)
            || _vertexPreviewLocal == null)
            return 0;

        var s = _vertexTarget;
        int n = s.Kind == StrokeKind.Triangle ? 3 : 4;
        if (vertices.Length < n || degrees.Length < 3) return 0;

        var m = s.Transform;
        for (int i = 0; i < 3; i++) vertices[i] = Vector2.Transform(_vertexPreviewLocal[i], m);
        // 第四个顶点**不在临时点表里**（平行四边形存三个点），现推一个——
        // 少了它，报出来的那个角就少了一条边，角度会算错。
        if (n == 4)
            vertices[3] = Vector2.Transform(Stroke.ParallelogramFourth(
                _vertexPreviewLocal[0], _vertexPreviewLocal[1], _vertexPreviewLocal[2]), m);
        return SelectionHandles.PolygonAngles(s.Kind, vertices[..n], degrees);
    }

    /// <summary>被拖端点"走过的一小段"的脏矩形（两个端点位置取并集，再按半笔宽外扩）。</summary>
    private static RectF EndpointDirtyRect(Vector2 a, Vector2 b, float inflate)
    {
        var r = RectF.Empty;
        r.Add(a.X, a.Y);
        r.Add(b.X, b.Y);
        return r.Inflate(inflate);
    }

    /// <summary>
    /// 这一批对象在矩阵 <paramref name="m"/> 下占的画布范围（轴对齐）。
    ///
    /// 脏区必须是正矩形，而对象和选区框都可能是斜的，所以取四个角变换后的包围盒。
    /// 往外多留 2 逻辑像素：抗锯齿的边缘会跑出精确包围盒一点点，漏了就会在屏幕上
    /// 留一条发丝一样的残影。
    /// </summary>
    private RectF PreviewBounds(in Matrix3x2 m)
    {
        var r = RectF.Empty;
        foreach (var s in _dragTargets)
        {
            var b = s.PaddedBounds;
            if (b.IsEmpty) continue;
            if (m.IsIdentity) { r.Add(b); continue; }
            r.Add(TransformBounds(b, m));
        }
        return r.IsEmpty ? r : r.Inflate(2f * DpiScale);
    }

    /// <summary>把一个轴对齐矩形过一遍矩阵，取四个角变换后的新包围盒。</summary>
    private static RectF TransformBounds(in RectF b, in Matrix3x2 m)
    {
        if (b.IsEmpty) return RectF.Empty;
        if (m.IsIdentity) return b;
        var p0 = Vector2.Transform(new Vector2(b.MinX, b.MinY), m);
        var p1 = Vector2.Transform(new Vector2(b.MaxX, b.MinY), m);
        var p2 = Vector2.Transform(new Vector2(b.MaxX, b.MaxY), m);
        var p3 = Vector2.Transform(new Vector2(b.MinX, b.MaxY), m);
        var r = RectF.Empty;
        r.Add(p0.X, p0.Y); r.Add(p1.X, p1.Y);
        r.Add(p2.X, p2.Y); r.Add(p3.X, p3.Y);
        return r;
    }

    /// <summary>
    /// **这一帧该画的那个选中框**：拖动预览期间要用实时变换，不能再用模型里的。
    ///
    /// 为什么必须分开：方案 B 里模型到松手才动（那样内容层才能一帧都不重画），
    /// 于是 <see cref="SelectionHandles.FrameOf"/> 拿到的是**按下那一刻**的框 ——
    /// 直接拿它画，拖动中框和手柄会留在原地不动、内容和框分家（松手才"啪"地跳回来）。
    ///
    /// 算法和 <see cref="SelectionHandles.FrameOf"/> 是**同一条**：每个对象的**世界**
    /// 包围盒先过一遍实时矩阵，再并成轴对齐的框。所以一条和多条没有分支——
    /// 框永远正着，旋转中每帧重新贴合当前内容（会"呼吸"，这是"框永远正着"的代价，
    /// 见 SelectionFrame 的注释）。
    /// 不在拖动中就原样返回（绝大多数帧走这条，零开销）。
    /// </summary>
    internal SelectionFrame LiveSelectionFrame
    {
        get
        {
            // 拖端点 / 拖顶点：模型到松手才动，所以框必须按**临时几何**算——
            // 照模型算的话框会留在旧位置上，和屏幕上那条线当场分家。
            // （和下面"拖动预览用实时矩阵"是同一件事的两种形态：一个改几何、一个改变换。）
            //
            // 口径必须和静止态**一模一样**（2026-09-19）：走 PreviewInkBounds
            // （端点外接 / 圆的参数化外接 / 顶点外接 + 半笔宽），它和 WorldInkBounds 一张表。
            // 以前这里写死了 LineLikeInkBounds —— 那是**直线专用**的一条式子，
            // 圆/椭圆/三角形拖元素时框会退化成"两个控制点的外接"（圆的紧框一度算成 256×16）。
            // 另：这儿以前用的是 PaddedBoundsOf —— 那是**脏区**的口径（墨迹 + 2 像素），
            // 于是拖动中每边比对象多 2 像素，松手那一瞬框会缩一下。
            if (_vertexDragging && _vertexTarget != null && _vertexPreviewLocal != null)
            {
                var pv = _vertexPreviewLocal;
                var rv = _vertexTarget.PreviewInkBounds(pv, Matrix3x2.Identity);
                return new SelectionFrame { Local = rv, ToCanvas = Matrix3x2.Identity };
            }

            var f = SelectionHandles.FrameOf(Doc.Selected);
            if (!DragPreviewActive || f.IsEmpty) return f;
            var r = RectF.Empty;
            foreach (var s in Doc.Selected)
            {
                // 锁定的对象**不跟着动**（用户 2026-09-16 定的 B 语义），
                // 所以它们那块按"当前位置"算——不然框会跟着它们一起漂。
                if (s.Locked) { r.Add(s.WorldInkBounds); continue; }
                // 直线 / 箭头要按**端点**过（对象变换 × 实时矩阵）算，不能拿"当前墨迹框"再乘矩阵：
                // 后者等于"把一个矩形整体转过去再取外接"，一条转 30° 的直线框会撑到接近两倍宽
                // （2026-09-18 实测：962 vs 线自己 490）。
                r.Add(s.IsLineLike
                    ? s.LineLikeInkBounds(_selDragMatrix)
                    : TransformBounds(s.WorldInkBounds, _selDragMatrix));
            }
            return new SelectionFrame { Local = r, ToCanvas = Matrix3x2.Identity };
        }
    }

    /// <summary>
    /// **画完自动选中那个框**（见 <see cref="EndStroke"/>）：指针落在哪一档。
    ///
    /// **和框选工具下那一套完全一样**，只有"按到框外"这一种情况不同：
    ///   · <see cref="AutoSelZone.Furniture"/> ＝ 操作条那一块（圆钮或整条）/ 面板 /
    ///     手柄 / 旋转柄——交给 <see cref="TryBeginSelectionGesture"/> 那一套分流；
    ///   · <see cref="AutoSelZone.Grab"/> ＝ **框内任意一点**（含图形自己那条墨）
    ///     → 整体拖动，和框选工具下同一条路（按住框里哪儿都能拖）；
    ///   · <see cref="AutoSelZone.None"/> ＝ 框**外**：收起这个框，**这一笔照常画**
    ///     ——用户 2026-09-22 的口径："点击了其他地方，这个选中框就取消"。
    ///
    /// 用户上手之后的原话是"**我现在是只要收缩，其他的都不变**"——所以这一档判据
    /// **不认识 `_autoSelCollapsed`**：收起 / 摊开只影响"条"画成圆钮还是一整条
    ///（见 <see cref="BarDrawnCollapsed"/>），拖动、拉手柄、点条这些行为一个字都不变。
    /// ⚠ 第一版把"框里"判成"接着画一笔"，结果就是用户报的那句
    /// "它好像不能拖动位置，只能拉伸缩放"——**框里那块地方必须能拖**。
    ///
    /// ⚠ **只有这一份判据**：按下时往哪条路走（`OnPointerDown` → <see cref="AutoSelectionPress"/>）
    /// 和光标形状（<see cref="ComputeCursorKind"/>）都问它，两处各写一遍的话就会出现
    /// "光标看着能拖、按下去却在画图"这种一半对一半错的状态
    ///（"同一个名单写在多处必漏一处"的教训见 架构-分层与规则.md 五-7）。
    /// </summary>
    private AutoSelZone AutoSelectionZoneAt(float x, float y)
    {
        float dpi = UiUnit;
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var aabb = frame.CanvasAabb;

        // ① 操作条那一块——**收起来就是那一颗圆钮，摊开了就是一整条 ＋ 面板**。
        //    范围和 SelectionCursor 里那几块**同一个**（"看得见的一块"和"点得到的一块"
        //    必须是同一个）。
        if (BarDrawnCollapsed)
        {
            if (SelectionDotCanvasRect.Contains(x, y))
                return AutoSelZone.Furniture;
        }
        else
        {
            if (SelectionBarCanvasRect.Contains(x, y))
                return AutoSelZone.Furniture;
            if (CustomColorOpen
                && SelectionHandles.PickContains(x, y, aabb, dpi, FurnitureViewport,
                                                 SelectionHandles.SwatchCount, SingleTextBoxSelection))
                return AutoSelZone.Furniture;
            if (SelPanelOpen != SelPanel.None
                && SelectionHandles.PanelContains(x, y, aabb, dpi, FurnitureViewport,
                                                  SelPanelOpen, SelectionHandles.SwatchCount,
                                                  SingleTextBoxSelection))
                return AutoSelZone.Furniture;
            // 字体下拉浮在面板右边：点它不能当成“拖画布/拖对象”。
            if (FontListOpen && FontListRectNow().Contains(x, y))
                return AutoSelZone.Furniture;
        }

        // ② 手柄（八个缩放柄 / 旋转柄 / 定义元素手柄）。**必须排在"整体拖动"那条前面**：
        //    直线的两个端点手柄正好压在框边上，先判框内的话那一按会被当成"拖整条"。
        if (SelectionHandles.HitTest(x, y, Doc.Selected, frame, dpi) != SelHandle.None)
            return AutoSelZone.Furniture;

        // ③ **框内** → 整体拖动（`TryBeginSelectionGesture` 里第 3 / 4 步那一套：
        //    框里空白处是"拖动"、按在某条墨上是"收窄成只选它"）。
        //    判据就是框选工具下那一条（`frame.ToLocalPoint` 落在 `frame.Local` 里），
        //    不另写一份"离轮廓多远算按上了"。
        if (SelectionHandles.InsideUiFrame(frame, new Vector2(x, y), dpi))
            return AutoSelZone.Grab;

        // ④ 框**外**：不算动它——收起这个框，这一笔照常画。
        return AutoSelZone.None;
    }

    /// <summary>图形工具下、那个自动出现的框：这一次按下算不算"在动它"。</summary>
    private bool AutoSelectionPress(float x, float y, bool shift, bool alt)
        => AutoSelectionZoneAt(x, y) != AutoSelZone.None
           && TryBeginSelectionGesture(x, y, shift, alt);

    /// <summary>
    /// 此刻指针所在这一带，"选中框那一套"（手柄 / 框内拖动）是不是可用。
    ///
    /// 框选工具下**整个框**都可用；图形工具下按 <see cref="AutoSelectionZoneAt"/> 那两档走。
    /// 这一条也**只有这一份**——光标都问它。
    /// </summary>
    private bool SelectionInteractiveAt(float canvasX, float canvasY)
        => Doc.Selected.Count > 0
           && !_textBoxEditing
           && (Tool == Tool.Marquee
               // 文本工具下，**已选中的文字框**也要给"移动十字"：未选中的文字仍是 I 字，
               // 选中之后拖它走、双击编辑（2026-11-01 用户照 ClassIn 定的）。
               || (Tool == Tool.Text && SingleTextBoxSelection)
               || ((IsShapeTool(Tool) || _dwellSelected || _touchSelected)
                   && AutoSelectionZoneAt(canvasX, canvasY) != AutoSelZone.None));

    /// <summary>
    /// **操作条那一块（整条 / 圆钮 / 它下面挂的面板）画不画、点不点**。
    ///
    /// 框选工具下当然有；**图形工具下也有**——画完自动选中那个框会挂一**颗圆钮**
    /// （见 <see cref="_autoSelCollapsed"/>）：不画的话老师不知道该去哪儿把它摊开，
    /// 而那只是一颗 26 逻辑像素的圆钮，不像整条九格那样会把"接着画"的地方占掉。
    /// 摊开之后行为**一个字都不变**（只是"条"长得完整了）。
    ///
    /// ⚠ 这是**唯一**判据：绘制（Overlay）/ 按下分流（TryBeginSelectionGesture）/ 悬停高亮
    /// （UpdateBarHover）/ 光标（SelectionCursor）四处都问它，任一处漏了都会变成
    /// "画了但点不到"或者反过来"点得到但看不见"。具体画整条还是画圆钮，问 `BarDrawnCollapsed`。
    /// </summary>
    internal bool SelectionBarShown
        => Doc.Selected.Count > 0
           && !_textBoxEditing
           && (Tool == Tool.Marquee || (Tool == Tool.Text && SingleTextBoxSelection)
               || IsShapeTool(Tool) || _dwellSelected || _touchSelected);

    /// <summary>图形工具下那个自动选中的框：指针落在"家具 / 算动它 / 都不是"哪一档。</summary>
    private enum AutoSelZone
    {
        /// <summary>都不算——按下去是接着画一笔。</summary>
        None,
        /// <summary>框的家具：圆钮 / 操作条 / 面板 / 手柄 / 旋转柄（交给选择手势那一套分流）。</summary>
        Furniture,
        /// <summary>框内任意一点（含图形自己那条墨）：拖它走。</summary>
        Grab,
    }

    /// <summary>
    /// 框选工具按下时的分流。返回 true 表示这次按下已经被选择手势接掉。
    ///
    /// 顺序有讲究（从 1 到 5，**不能换**）：
    ///   1. 操作条按钮——它和手柄、框都紧挨着，放后面就点不中；
    ///   2. 手柄（缩放 / 旋转）；
    ///   3. 在**当前选区框内** → 整体拖动；
    ///   4. **点选**：按在一条墨迹上就选中它（没有选中时也走这条）；
    ///   5. 都没有 → 返回 false，交给外面的框选。
    ///
    /// 第 3 步必须在第 4 步之前：否则"拖动已选中的一块"会被判成"点选其中一条"，
    /// 多选就再也搬不动了。（2026-09-15 实现点选时定的顺序。）
    /// </summary>
    /// <summary>按下参数滑杆：进入"拖参数"手势（一次拖动 = 一步撤销）。</summary>
    private bool TryBeginFigureParamDrag(float x, float y, in SelectionFrame frame)
    {
        if (Doc.Selected.Count != 1) return false;
        var s = Doc.Selected[0];
        if (s.Kind != StrokeKind.Figure || s.Figure == null || s.Locked) return false;
        var keys = FigureParamBar.Keys(s);
        if (keys.Count == 0) return false;
        float ui = UiUnit;
        var panel = FigureParamBar.PanelRect(frame.CanvasAabb, ui, FurnitureViewport, keys.Count);
        int row = FigureParamBar.RowAt(panel, keys.Count, x, y, ui);
        if (row < 0) return false;
        string key = keys[row];
        if (!FigureParamBar.TryGet(s, key, out double value)) return false;

        _figureParamDragging = true;
        _figureParamStroke = s;
        _figureParamKey = key;
        FigureParamBar.Range(value, out _figureParamMin, out _figureParamMax);
        _figureParamOldJson = PlotSpecJson.Write(s.Figure);
        SelDragging = true;
        _dragIsMove = false;
        _dragHandle = SelHandle.None;
        _selDragMoved = true;
        ApplyFigureParamAt(x);
        return true;
    }

    private void ApplyFigureParamAt(float x)
    {
        if (!_figureParamDragging || _figureParamStroke?.Figure == null) return;
        var keys = FigureParamBar.Keys(_figureParamStroke);
        int row = keys.IndexOf(_figureParamKey);
        if (row < 0) return;
        float ui = UiUnit;
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var panel = FigureParamBar.PanelRect(frame.CanvasAabb, ui, FurnitureViewport, keys.Count);
        var track = FigureParamBar.TrackRect(FigureParamBar.RowRect(panel, row, ui), ui);
        float v = FigureParamBar.ValueAt(track, x, _figureParamMin, _figureParamMax);
        FigureParamBar.Set(_figureParamStroke, _figureParamKey, v);
        _figureParamStroke.InvalidateMetrics();
        Doc.Dirty.Add(_figureParamStroke.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
    }

    private void FinishFigureParamDrag()
    {
        var s = _figureParamStroke;
        if (s?.Figure != null)
        {
            string json = PlotSpecJson.Write(s.Figure);
            if (json != _figureParamOldJson)
            {
                var act = new SetFigureParamsAction(s, _figureParamOldJson, json);
                Doc.CommitInteractive(act);
            }
            Doc.Dirty.Add(s.PaddedBounds);
        }
        _figureParamDragging = false;
        _figureParamStroke = null;
        _figureParamKey = "";
        _figureParamOldJson = "";
        _dragIsMove = false;
        _dragHandle = SelHandle.None;
        SelDragging = false;
        _dirty = true;
    }

    private bool TryBeginSelectionGesture(float x, float y, bool shift, bool alt)
    {
        _dragHitStroke = null;
        ResetAlignSnapLatch();
        ClearAlignTargets();
        float dpi = DpiScale;      // 内容命中容差 / 粗细换算（"纸上的尺"，不跟界面档）
        float ui = UiUnit;         // 选中家具（条 / 面板 / 手柄）的尺寸与命中

        bool move = false;
        var h = SelHandle.None;
        var frame = SelectionHandles.FrameOf(Doc.Selected);

        // ⓪.5 交互矢量图：参数滑杆优先于操作条/手柄（它浮在框上方）。
        if (TryBeginFigureParamDrag(x, y, frame)) return true;

        // —— ⓪ 收起态圆钮 / 浮动面板 / 操作条：优先级最高（它们盖在别的东西上面）——
        //
        // 顺序：**面板内部 → 操作条 → 面板外部**。为什么把"条"放在"面板外部"之前：
        // 面板开着时去点"层级"那一格，应该直接换成层级面板，而不是"先关掉再被这一格
        // 又打开"。而点面板和条以外的任何地方，才把面板收起来（并且这一次点击**继续**
        // 往下走，于是"点空白取消选中"的习惯不会因为面板开着就失灵）。
        //
        // ⚠ 整段由 `SelectionBarShown` 把守：只有"框选工具 / 图形工具下那个自动选中的框"
        // 才有点得到的那一块（见那里的注释）——不把守的话，操作条会隔空吃掉
        // "接着画下一个"的那一下。
        if (SelectionBarShown)
        {
            var aabb0 = LiveSelectionFrame.CanvasAabb;

            if (BarDrawnCollapsed)
            {
                // 收起态：只有一个圆钮，点它展开（框和手柄照旧，收起的只是"条"）。
                if (SelectionDotCanvasRect.Contains(x, y))
                {
                    SelBarCollapsed = false;
                    // **画完自动选中那个框**：点开这颗圆钮之后就成"常规那一套"了
                    //（整条操作条出来、框内任意一点都能拖）——用户 2026-09-22 定的就是这一步。
                    _autoSelCollapsed = false;
                    _dirty = true;
                    return true;
                }
            }
            // **自定义取色板优先**：它挂在墨迹面板旁边，可能压着别的控件，而且
            // "点它自己 = 不算点在外面"（点空白不取消，只有点卡片以外才取消）。
            else if (CustomColorOpen
                     && SelectionHandles.PickContains(x, y, aabb0, ui, FurnitureViewport,
                                                      SelectionHandles.SwatchCount,
                                                      SingleTextBoxSelection))
            {
                HandlePickClick(x, y);
                return true;                        // 色板上的点击一律吃掉（包括空白处）
            }
            // 字体下拉「更多…」：列表浮在面板右侧，优先于面板本身命中。
            // 按下只“武装”拖动；抬起时没拖动才应用那一行（鼠标/笔/触摸共用）。
            else if (FontListOpen
                     && !FontListRectNow().IsEmpty
                     && FontListRectNow().Contains(x, y))
            {
                return true;                        // 列表上的按下一律吃掉（含空白）
            }
            else if (SelPanelOpen != SelPanel.None
                     && SelectionHandles.PanelContains(x, y, aabb0, ui, FurnitureViewport,
                                                       SelPanelOpen, SelectionHandles.SwatchCount,
                                                       SingleTextBoxSelection))
            {
                HandlePanelClick(x, y);
                return true;                        // 面板上的点击一律吃掉（包括空白处）
            }
            else if (!SelectionMenuRectNow().IsEmpty
                     && SelectionMenuRectNow().Contains(x, y))
            {
                var menu = SelectionMenuRectNow();
                int row = SelectionHandles.BarMenuRowAt(x, y, menu, ui, SelectionMenuRowsNow);
                if (SelAiMenuOpen)
                {
                    // 选了行就收起弹层：以前 AI 菜单点完不关，投影上看着“没反应”，
                    // 再点一次又只会提示“正在作图”（2026-11-11 用户反馈）。
                    if (row >= 0) CloseSelectionMenu();
                    switch (AiMenuKindNow)
                    {
                        case AiMenuKind.Hand:
                            if (row == 0) RecognizeToTextBoxFromUi();
                            else if (row == 1) RecognizePlotFromUi();
                            break;
                        case AiMenuKind.Image:
                            if (row == 0) RecognizeImageCardFromUi();
                            else if (row == 1) RecognizePlotFromUi();
                            break;
                        default:
                            if (row == 0) RecognizePlotFromUi();
                            break;
                    }
                }
                else if (SelMoreMenuOpen)
                {
                    var order = SelectionHandles.MoreBarOrder(BarSurfaceNow);
                    if (row >= 0 && row < order.Length)
                    {
                        var action = order[row];
                        CloseSelectionMenu();
                        RunBarButton(action, frame, aabb0);
                    }
                }
                return true;
            }
            else
            {
                int btn0 = SelectionHandles.BarButtonAtIn(x, y, SelectionBarRect(aabb0), ui,
                    SelectionHandles.BarButtonCountFor(BarSurfaceNow));
                if (btn0 >= 0) { RunBarAction(btn0, frame, aabb0); return true; }
                if (SelPanelOpen != SelPanel.None || CustomColorOpen)
                {
                    CloseCustomColor();
                    SelPanelOpen = SelPanel.None;
                    _dirty = true;
                }
                CloseSelectionMenu();
            }
        }

        // —— 1~3：有选中时才谈得上（没选中就直接跳到第 4 步的点选）——
        if (Doc.Selected.Count > 0)
        {
            // 操作条定位用框的**画布轴对齐范围**：框本身可能是斜的，但"它占了屏幕上
            // 哪一块"永远是个正矩形，操作条贴在那个矩形的下面才对。
            var aabb = frame.CanvasAabb;

            // （操作条与面板已经在 ⓪ 里处理过了，这里只剩手柄 / 框内拖动 / 点选）
            h = SelectionHandles.HitTest(x, y, Doc.Selected, frame, ui);

            // 定义元素手柄（直线 / 箭头 / 圆 / 椭圆）：**两种语义分派**
            //   · **圆心 / 中心**（Anchor）：拖它 = 整体平移（改变换矩阵、几何一个点不动，
            //     半径 / 半轴因此逐位不变）。
            //   · **端点 / 圆周点 / 轴端点**：走"改几何"那条快路（手势期不碰模型、松手一步撤销）。
            // 锁定语义照旧：锁定的拖不动，落回"框内拖动"让下面那段锁定分支把它吃掉。
            bool anchorMove = false;
            // "按下的是不是定义元素手柄"——顶点那几格**走 IsVertexHandle 一条判据**，
            // 不在这里再列一遍名字（列名字的写法 2026-09-19 漏过一次 VertexD，
            // 症状是"拖单位长度点什么都没发生"，见 SelectionHandles.IsVertexHandle）。
            bool handleLike = h is SelHandle.EndpointA or SelHandle.EndpointB
                                 or SelHandle.Left or SelHandle.Right or SelHandle.Top or SelHandle.Bottom
                              || SelectionHandles.IsVertexHandle(h)
                              // 焦点三角形的顶点 P（2026-09-22）：**它也是一格定义元素手柄**
                              // ——少写这一格，屏幕上会画出一个"看得见、按不动"的圆点。
                              || h == SelHandle.FocusPoint;
            if (handleLike && SelectionHandles.ShapeEditable(Doc.Selected, out var shape))
            {
                var sh = SelectionHandles.HandleOf(shape, h);
                if (SelectionHandles.IsAnchorMove(shape, sh))
                {
                    anchorMove = true;
                    _dragHitStroke = shape;    // 松手若没动 → 收窄成"只选中它"（和点选同一条路）
                    h = SelHandle.None;
                }
                else if (sh != ShapeHandle.None && !shape.Locked)
                {
                    BeginVertexDrag(shape, sh, x, y);
                    return true;
                }
                // **别急着把 h 抹掉**：`Top / Bottom / Left / Right` 这四个名字是**两用**的——
                //   · 对椭圆 / 坐标系 / 数轴：它们是"定义元素"（走上面那条改几何的路）；
                //   · 对**没有定义元素手柄**的对象（矩形 / 墨迹 / 图像 / 四种曲线 / 四个立体图形）：
                //     它们就是**通用框的四边中点缩放柄**。
                // 原来这里无条件 `h = SelHandle.None`，于是那类对象"四角能拉、四边中点只能整体拖"
                //（用户 2026-09-20："那八个点出现了，但是我看着只有对角线能拖动放缩？
                //  我觉得左右拉伸也可以给"）。
                // 判据还是那一条：**它到底有没有定义元素手柄**——有，才说明这个 h 真的"没有"，
                // 该抹掉（"画都不画就别点得到"，见 HitTest 那段）；没有，就留给下面的通用框。
                else if (SelectionHandles.HasShapeHandles(shape))
                    h = SelHandle.None;
            }

            if (h == SelHandle.None)
            {
                if (anchorMove)
                {
                    // 圆心 / 中心：**直接算作整体拖动**，不走下面"指针底下有没有东西"那一关。
                    // 理由是几何事实：圆心离描边一整个半径那么远，而点选只认描边
                    // （见 HitObjectAt）——靠那一段的话"拖圆心"根本拖不动（2026-09-19 自检抓到）。
                    move = true;
                }
                else
                {
                    // 没点在手柄上：看指针是不是落在**操作框**里（整体拖动）。
                    // 小对象那个框撑到最小尺寸（见 SelectionHandles.UiBox），
                    // "看得见的那一圈"里面都能拖——判据和光标、自动选中框同一份。
                    move = SelectionHandles.InsideUiFrame(frame, new Vector2(x, y), ui);

                    // **框里有别的对象压在上面时，不能拖当前选区**：先让位给点选。
                    // 典型场景：文本框上面写了墨迹，当前文本框是选中的，老师点那条墨迹，
                    // 期望是"选中墨迹"，不是"把文本框拖走"。
                    if (move)
                    {
                        var under = Doc.HitObjectAt(x, y, ClickToleranceLogical * dpi);
                        // 2026-11-03：**只在"选中的是文本框 / 题卡"时让位给压在上面的墨迹**。
                        // 形状不能这么判——椭圆的中心、矩形的内部本来就是空的，
                        // 点在那儿仍然应该拖动整个形状（snaptest 里坐标轴的端点正好穿过
                        // 椭圆中心，旧写法会拒绝拖动，表现为"选框完全拖不动"）。
                        bool selectedTextBox = Doc.Selected.Count > 0;
                        foreach (var sel in Doc.Selected)
                            if (sel.Kind != StrokeKind.TextBox) { selectedTextBox = false; break; }
                        if (selectedTextBox && under != null && !Doc.Selected.Contains(under)) move = false;
                        else _dragHitStroke = under;
                    }
                }
            }
        }

        // —— 4：点选（2026-09-15 新增）——
        if (h == SelHandle.None && !move)
        {
            var hit = Doc.SelectAt(x, y, ClickToleranceLogical * dpi,
                                   additive: shift, subtractive: alt);
            if (hit == null) return false;                // 5：空白 → 交给框选

            _dragHitStroke = hit;
            _dirty = true;
            // 加选/减选时**不进拖动**：方便连着点几条攒出一个选择。
            if (shift || alt) return true;

            // 无修饰键：选中它并**直接进入整体拖动**（点住就能拖，和 PPT 一样）。
            frame = SelectionHandles.FrameOf(Doc.Selected);
            move = true;
        }

        // 文本框：8 手柄改的是"框的宽高"，不是缩放内容。走专用手势，
        // 不进下面的 Transform 缩放（否则字和公式会被拉变形）。
        if (h != SelHandle.None && Doc.Selected.Count == 1
            && Doc.Selected[0].Kind == StrokeKind.TextBox)
        {
            BeginTextBoxResize(Doc.Selected[0], h, x, y);
            return true;
        }

        _dragHandle = h;
        _dragIsMove = move;
        _dragFrame = frame;
        _dragStartPoint = new Vector2(x, y);
        _selDragMatrix = Matrix3x2.Identity;
        _selDragMoved = false;
        _dragTargets = Doc.Selected.ToArray();

        // 锁定（用户 2026-09-16 定的 B 语义）：**能选中，但拖不动**。
        // 做法是把锁定的从"这一次手势要动的那批"里摘掉——选区框照旧画（框算的是
        // Doc.Selected），只是它们不跟着动。全锁时提示一句、并且什么都不动。
        {
            int lockedCount = 0;
            foreach (var s in _dragTargets) if (s.Locked) lockedCount++;
            if (lockedCount > 0)
            {
                if (lockedCount == _dragTargets.Length)
                {
                    // 锁定的对象拖不动——这一句只在控制台留痕（屏幕反馈按用户要求去掉了提示条；
                    // 锁图标本身就是状态指示）。
                    Console.WriteLine($"拖动：这 {lockedCount} 个对象已锁定，拖不动");
                    SelDragging = true;                 // 吃掉这次按下：别退化成重新框选
                    _dragTargets = Array.Empty<Stroke>();
                    _dragStartXform = Array.Empty<Matrix3x2>();
                    _dragHandle = SelHandle.None;
                    _dragIsMove = false;
                    _dirty = true;
                    return true;
                }
                var movable = new List<Stroke>(_dragTargets.Length - lockedCount);
                foreach (var s in _dragTargets) if (!s.Locked) movable.Add(s);
                _dragTargets = movable.ToArray();
                Console.WriteLine($"拖动：{lockedCount} 个对象已锁定，不跟着动");
            }
        }

        _dragStartXform = new Matrix3x2[_dragTargets.Length];
        for (int i = 0; i < _dragTargets.Length; i++)
            _dragStartXform[i] = _dragTargets[i].Transform;

        // 对象吸附只服务“单选、整体拖动”；目标框在这里一次性收集。
        PrepareAlignTargets(move && _dragTargets.Length == 1 ? _dragTargets[0] : null);

        // 方案 B：把这一批从**内容层**摘出去，改由浮动层画实时预览。
        // 模型在整个手势里一个字都不改，所以内容层一帧都不用重画。
        DetachForDrag();

        // 旋转：累积角从 0 起，指针的"上一帧位置"就是按下这一点。
        // 必须在这里归零——上一次拖拽攒下来的角度绝不能带进这一次。
        _rotAccumDeg = 0f;
        _rotPrevPoint = frame.ToLocalPoint(_dragStartPoint);

        // 这一次旋转读 α 还是 Δ，**按下这一刻判定一次**（用户 2026-09-18 的四条之一）。
        // 判定复用"单选一条直线/箭头"那个现成判据（终点手柄用的是同一个），
        // 于是"何时给端点手柄、何时给 α 读数"永远是同一个答案——不会两处走岔。
        _rotLineLike = SelectionHandles.EndpointEditable(Doc.Selected, out var rotLine);
        _rotStartInclination = _rotLineLike
            ? SelectionHandles.InclinationDegrees(SelectionHandles.EndpointCanvasPosition(rotLine, 0),
                                                  SelectionHandles.EndpointCanvasPosition(rotLine, 1))
            : 0f;
        SelRotationInclination = _rotStartInclination;
        SelRotationReadsInclination = false;   // 松手前标签不显示，这个标志只在拖动中为真

        // 单选一个图形（矩形 / 椭圆 / 三角形 / 平行四边形）→ 读数走**姿态角**（规格 9.7）。
        // 圆不在内（转了看不出来），图像 / 笔迹改不进来（PoseEditable 只认那四种）。
        // 镜像状态也在这里记一次：手势里它不会变（乘上去的是纯旋转，行列式恒正）。
        _rotPoseLike = SelectionHandles.PoseEditable(Doc.Selected, out var rotPose);
        _rotMirrored = _rotPoseLike && SelectionHandles.IsMirrored(rotPose.Transform);
        _rotStartPose = _rotPoseLike ? SelectionHandles.PoseAngleDegrees(rotPose.Transform) : 0f;
        SelRotationPose = _rotStartPose;
        SelRotationReadsPose = false;          // 同上：只在拖动中为真

        SelDragging = true;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 自检用：直接走一次"选择手势分流"（操作条 / 手柄 / 框内拖动 / 点选都从这儿进）。
    /// 返回 false = 这次按下没被选择手势接掉（调用方会起框选）。
    ///
    /// 为什么把修饰键当参数：点选里的 Shift/Alt 分支要看真键盘，自检没法按着 Shift 跑，
    /// 于是"读键盘"这一步留在调用方（鼠标按下那处），判定函数保持纯逻辑。
    /// </summary>
    internal bool SelectionGestureForTest(float x, float y, bool shift = false, bool alt = false)
        => TryBeginSelectionGesture(x, y, shift, alt);

    /// <summary>自检用：结束一次选择手势（松手）。</summary>
    internal void EndSelectionGestureForTest() => EndSelDrag();

    /// <summary>
    /// 自检用：走一遍"指针移动"里**浮层拖动**那一段（粗细滑条 / 自定义取色板）。
    /// 真机上这段在 `OnPointerMove` 里排在工具 switch 之前（和工具无关）；
    /// 自检没法合成系统指针消息，所以把同一小段逻辑暴露出来（和上面那条同一个套路）。
    /// </summary>
    internal void PanelDragMoveForTest(float x, float y)
    {
        if (_sliderDragging) { DragWidthSliderTo(x); return; }
        if (_colorDragging) DragColorPickerTo(x, y);
    }

    /// <summary>自检用：结束浮层拖动（松手）——滑条提交、取色板按指针在不在板内决定应用/取消。</summary>
    internal void EndPanelDragForTest(float x, float y)
    {
        if (_sliderDragging) EndWidthSliderDrag();
        else if (_colorDragging) EndColorPickerDrag(x, y);
    }

    /// <summary>自检用：自定义取色板开着吗。</summary>
    internal bool CustomColorOpenForTest => CustomColorOpen;

    /// <summary>自检/出图用：按真实入口打开自定义取色板（起点色 = 选中墨迹/手里那支笔）。</summary>
    internal void OpenCustomColorForTest() => OpenCustomColor();

    private const float AlignSnapEnterLogical = 5f;
    private const float AlignSnapReleaseLogical = 8f;
    private const float AlignSnapProximityLogical = 24f;
    private const float AlignSnapPulseMs = 220f;

    /// <summary>清掉这一手势的“吸住了谁”（短亮不跟着清，它自己淡出）。</summary>
    private void ResetAlignSnapLatch()
    {
        _alignSnapLatched = false;
        _alignSnapTarget = null;
        _alignSnapTargetFrame = RectF.Empty;
        _alignSnapTargetCurveFrame = RectF.Empty;
        _alignSnapTargetWave = false;
        _alignSnapTargetCenterline = false;
        _alignSnapTargetAxisCenterOnlyX = false;
        _alignSnapTargetAxisCenterOnlyY = false;
        _alignSnapX = null;
        _alignSnapY = null;
    }

    private void ClearAlignTargets()
    {
        _alignTargets = null;
        _alignTargetFrames = null;
        _alignTargetCurveFrames = null;
        _alignTargetWaves = null;
        _alignTargetCenterlines = null;
        _alignTargetAxisCenterOnlyX = null;
        _alignTargetAxisCenterOnlyY = null;
        _alignSourceCenterline = false;
        _alignSourceWave = false;
        _alignSourceCurveFrame = RectF.Empty;
    }

    /// <summary>
    /// 手势开始时收集一次“图形 + 图片”的目标框。自由笔迹不参与，
    /// 所以几百个汉字的墨迹不会进入每帧的候选循环。
    /// </summary>
    private void PrepareAlignTargets(Stroke selected)
    {
        ClearAlignTargets();
        if (selected == null || !selected.IsShape) return;

        _alignSourceCenterline = IsAxisAlignedTransform(selected.Transform);
        _alignSourceWave = IsWaveLike(selected.Kind);
        _alignSourceCurveFrame = _alignSourceWave ? CurveSnapFrame(selected) : RectF.Empty;
        var targets = new List<Stroke>();
        var frames = new List<RectF>();
        var curveFrames = new List<RectF>();
        var waves = new List<bool>();
        var centerlines = new List<bool>();
        var centerOnlyX = new List<bool>();
        var centerOnlyY = new List<bool>();
        for (int i = 0; i < Doc.Strokes.Count; i++)
        {
            var s = Doc.Strokes[i];
            if (ReferenceEquals(s, selected) || !s.IsShape) continue;
            var b = s.WorldInkBounds;
            if (b.IsEmpty) continue;
            targets.Add(s);
            frames.Add(b);
            bool wave = IsWaveLike(s.Kind);
            waves.Add(wave);
            curveFrames.Add(wave ? CurveSnapFrame(s) : RectF.Empty);
            bool axisAligned = IsAxisAlignedTransform(s.Transform);
            centerlines.Add(axisAligned);

            // 轴类目标只把**真正的轴线**留给吸附。直线 / 箭头横着放时，
            // 它自己的笔宽会让 3×3 框多出“上沿 / 下沿”两条假轴线；数轴同理。
            // 只在这条轴与屏幕轴平行时启用：斜线 / 已经旋转过的图形仍走原来的
            // 3×3，避免把端点当成厚度。坐标系保留 3×3——它的外框（网格边界）
            // 本身也是用户可能要对齐的对象，不能一起砍掉。
            bool horizontal = axisAligned && IsHorizontalAxisTarget(s);
            bool vertical = axisAligned && IsVerticalAxisTarget(s);
            centerOnlyX.Add(axisAligned && s.IsLineLike && vertical);
            centerOnlyY.Add((axisAligned && s.Kind == StrokeKind.NumberLine)
                            || (axisAligned && s.IsLineLike && horizontal));
        }
        _alignTargets = targets.ToArray();
        _alignTargetFrames = frames.ToArray();
        _alignTargetCurveFrames = curveFrames.ToArray();
        _alignTargetWaves = waves.ToArray();
        _alignTargetCenterlines = centerlines.ToArray();
        _alignTargetAxisCenterOnlyX = centerOnlyX.ToArray();
        _alignTargetAxisCenterOnlyY = centerOnlyY.ToArray();
    }

    /// <summary>目标是一条水平直线 / 箭头（按画布里的两个端点判，容差 5%）。</summary>
    private static bool IsHorizontalAxisTarget(Stroke s)
    {
        if (!s.IsLineLike || s.Points.Count < 2) return false;
        var p0 = Vector2.Transform(new Vector2(s.Points[0].X, s.Points[0].Y), s.Transform);
        var p1 = Vector2.Transform(new Vector2(s.Points[^1].X, s.Points[^1].Y), s.Transform);
        float dx = MathF.Abs(p1.X - p0.X), dy = MathF.Abs(p1.Y - p0.Y);
        return dx > 1e-3f && dy <= MathF.Max(1f, dx * 0.05f);
    }

    /// <summary>目标是一条竖直直线 / 箭头（按画布里的两个端点判，容差 5%）。</summary>
    private static bool IsVerticalAxisTarget(Stroke s)
    {
        if (!s.IsLineLike || s.Points.Count < 2) return false;
        var p0 = Vector2.Transform(new Vector2(s.Points[0].X, s.Points[0].Y), s.Transform);
        var p1 = Vector2.Transform(new Vector2(s.Points[^1].X, s.Points[^1].Y), s.Transform);
        float dx = MathF.Abs(p1.X - p0.X), dy = MathF.Abs(p1.Y - p0.Y);
        return dy > 1e-3f && dx <= MathF.Max(1f, dy * 0.05f);
    }

    /// <summary>
    /// 正弦 / 余弦 / 波浪线是“有中心线的开放曲线”。两块这类图形拼接时，
    /// 要用**曲线中心线框**，不能用含笔宽的墨迹外框；否则外沿碰上了，中心线
    /// 还差一个笔宽（用户 2026-10-19 报的两个正弦接缝）。
    /// 这里只开这三类，闭合图形 / 图片仍走墨迹外框。
    /// </summary>
    private static bool IsWaveLike(StrokeKind kind)
        => kind is StrokeKind.Sine or StrokeKind.Cosine or StrokeKind.Wave;

    /// <summary>曲线中心线框（画布坐标，不含笔宽）。</summary>
    private static RectF CurveSnapFrame(Stroke s)
    {
        var b = s.CurveBoxLocal();
        if (b.IsEmpty) return b;
        var m = s.Transform;
        if (m.IsIdentity) return b;

        var r = RectF.Empty;
        void Add(Vector2 p) => r.Add(p.X, p.Y);
        Add(Vector2.Transform(new Vector2(b.MinX, b.MinY), m));
        Add(Vector2.Transform(new Vector2(b.MaxX, b.MinY), m));
        Add(Vector2.Transform(new Vector2(b.MaxX, b.MaxY), m));
        Add(Vector2.Transform(new Vector2(b.MinX, b.MaxY), m));
        return r;
    }

    /// <summary>中心线只对未旋转的图形有意义；旋转过的框不是图形自身的对称轴。</summary>
    private static bool IsAxisAlignedTransform(in Matrix3x2 m)
    {
        float s = MathF.Max(MathF.Abs(m.M11), MathF.Abs(m.M22));
        if (s < 1e-6f) return false;
        return MathF.Abs(m.M12) <= s * 1e-3f && MathF.Abs(m.M21) <= s * 1e-3f;
    }

    private static float AlignCenterX(in RectF r) => (r.MinX + r.MaxX) * 0.5f;
    private static float AlignCenterY(in RectF r) => (r.MinY + r.MaxY) * 0.5f;

    private static float AlignLineX(in RectF r, AlignLineRole role) => role switch
    {
        AlignLineRole.Start => r.MinX,
        AlignLineRole.Center => AlignCenterX(r),
        _ => r.MaxX,
    };

    private static float AlignLineY(in RectF r, AlignLineRole role) => role switch
    {
        AlignLineRole.Start => r.MinY,
        AlignLineRole.Center => AlignCenterY(r),
        _ => r.MaxY,
    };

    /// <summary>两个一维区间之间的空隙（重叠 = 0；分离时是正数）。</summary>
    private static float IntervalGap(float a0, float a1, float b0, float b1)
    {
        if (a1 < b0) return b0 - a1;
        if (b1 < a0) return a0 - b1;
        return 0f;
    }

    /// <summary>
    /// 软相交：两条竖线要比较时，上下范围必须重叠或相距不超过 24px；
    /// 两条横线要比较时，左右范围同理。避免屏幕两角也触发对齐。
    /// </summary>
    private static bool AlignNearX(in RectF a, in RectF b, float proximity)
        => IntervalGap(a.MinX, a.MaxX, b.MinX, b.MaxX) <= proximity;

    private static bool AlignNearY(in RectF a, in RectF b, float proximity)
        => IntervalGap(a.MinY, a.MaxY, b.MinY, b.MaxY) <= proximity;

    /// <summary>中心对中心优先级最高；同边其次；交叉边最后；距离永远优先。</summary>
    private static int AlignPriority(AlignLineRole source, AlignLineRole target)
        => source == AlignLineRole.Center && target == AlignLineRole.Center ? 0
         : source == target ? 1
         : 2;

    private static void ConsiderAlignCandidate(float delta, AlignLineRole sourceRole,
                                               AlignLineRole targetRole, float targetLine,
                                               float tolerance, ref AlignAxisCandidate? best)
    {
        if (MathF.Abs(delta) > tolerance) return;
        if (best.HasValue)
        {
            var old = best.Value;
            float oldD = MathF.Abs(old.Delta), newD = MathF.Abs(delta);
            if (newD > oldD + 1e-4f) return;
            if (MathF.Abs(newD - oldD) <= 1e-4f
                && AlignPriority(sourceRole, targetRole)
                   > AlignPriority(old.SourceRole, old.TargetRole)) return;
        }
        best = new AlignAxisCandidate
        {
            SourceRole = sourceRole,
            TargetRole = targetRole,
            Delta = delta,
            TargetLine = targetLine,
        };
    }

    /// <summary>
    /// 一根轴上的候选：源框三条线 × 目标框三条线（3×3），
    /// 但先用另一轴的软相交门槛把远处的框排掉。只保留最近的一个。
    /// </summary>
    private static AlignAxisCandidate? BestX(in RectF a, in RectF b, float tolerance,
                                             float proximity, bool allowCenterline,
                                             bool targetCenterOnly)
    {
        if (!AlignNearY(a, b, proximity)) return null;
        AlignAxisCandidate? best = null;
        for (int si = 0; si < 3; si++)
        {
            var sourceRole = (AlignLineRole)si;
            if (!allowCenterline && sourceRole == AlignLineRole.Center) continue;
            float sourceLine = AlignLineX(a, sourceRole);
            for (int ti = 0; ti < 3; ti++)
            {
                var targetRole = (AlignLineRole)ti;
                if (targetCenterOnly && targetRole != AlignLineRole.Center) continue;
                if (!allowCenterline && targetRole == AlignLineRole.Center) continue;
                float targetLine = AlignLineX(b, targetRole);
                ConsiderAlignCandidate(targetLine - sourceLine, sourceRole, targetRole,
                                       targetLine, tolerance, ref best);
            }
        }
        return best;
    }

    private static AlignAxisCandidate? BestY(in RectF a, in RectF b, float tolerance,
                                             float proximity, bool allowCenterline,
                                             bool targetCenterOnly)
    {
        if (!AlignNearX(a, b, proximity)) return null;
        AlignAxisCandidate? best = null;
        for (int si = 0; si < 3; si++)
        {
            var sourceRole = (AlignLineRole)si;
            if (!allowCenterline && sourceRole == AlignLineRole.Center) continue;
            float sourceLine = AlignLineY(a, sourceRole);
            for (int ti = 0; ti < 3; ti++)
            {
                var targetRole = (AlignLineRole)ti;
                if (targetCenterOnly && targetRole != AlignLineRole.Center) continue;
                if (!allowCenterline && targetRole == AlignLineRole.Center) continue;
                float targetLine = AlignLineY(b, targetRole);
                ConsiderAlignCandidate(targetLine - sourceLine, sourceRole, targetRole,
                                       targetLine, tolerance, ref best);
            }
        }
        return best;
    }

    /// <summary>按已经吸住的那一对线重算；吸住期间不换线。</summary>
    private static AlignAxisCandidate? CandidateForRoles(AlignLineRole sourceRole,
                                                        AlignLineRole targetRole,
                                                        bool xAxis,
                                                        in RectF a, in RectF b,
                                                        float tolerance, float proximity,
                                                        bool allowCenterline,
                                                        bool targetCenterOnly)
    {
        if (targetCenterOnly && targetRole != AlignLineRole.Center) return null;
        if (!allowCenterline
            && (sourceRole == AlignLineRole.Center || targetRole == AlignLineRole.Center))
            return null;
        if (xAxis)
        {
            if (!AlignNearY(a, b, proximity)) return null;
            float targetLine = AlignLineX(b, targetRole);
            float delta = targetLine - AlignLineX(a, sourceRole);
            if (MathF.Abs(delta) > tolerance) return null;
            return new AlignAxisCandidate
            {
                SourceRole = sourceRole, TargetRole = targetRole,
                Delta = delta, TargetLine = targetLine,
            };
        }
        else
        {
            if (!AlignNearX(a, b, proximity)) return null;
            float targetLine = AlignLineY(b, targetRole);
            float delta = targetLine - AlignLineY(a, sourceRole);
            if (MathF.Abs(delta) > tolerance) return null;
            return new AlignAxisCandidate
            {
                SourceRole = sourceRole, TargetRole = targetRole,
                Delta = delta, TargetLine = targetLine,
            };
        }
    }

    /// <summary>找最近的、在 5px 内有候选的目标框；一个手势只跟一个目标框。</summary>
    private bool TryFindAlignTarget(in RectF a, in RectF aCurve, float tolerance, float proximity,
                                    bool sourceCenterline,
                                    out Stroke target, out RectF targetFrame,
                                    out RectF targetCurveFrame, out bool targetWave,
                                    out bool targetCenterline,
                                    out bool targetCenterOnlyX, out bool targetCenterOnlyY,
                                    out AlignAxisCandidate? x, out AlignAxisCandidate? y)
    {
        target = null;
        targetFrame = RectF.Empty;
        targetCurveFrame = RectF.Empty;
        targetWave = false;
        targetCenterline = false;
        targetCenterOnlyX = false;
        targetCenterOnlyY = false;
        x = null;
        y = null;
        float bestScore = float.MaxValue;
        int bestPriority = int.MaxValue;

        for (int i = 0; i < _alignTargets.Length; i++)
        {
            bool useCurveFrame = _alignSourceWave && _alignTargetWaves[i] && !aCurve.IsEmpty
                                 && !_alignTargetCurveFrames[i].IsEmpty;
            var aa = useCurveFrame ? aCurve : a;
            var bb = useCurveFrame ? _alignTargetCurveFrames[i] : _alignTargetFrames[i];
            bool allowCenterline = sourceCenterline && _alignTargetCenterlines[i];
            bool centerOnlyX = allowCenterline && _alignTargetAxisCenterOnlyX[i];
            bool centerOnlyY = allowCenterline && _alignTargetAxisCenterOnlyY[i];
            var bx = BestX(aa, bb, tolerance, proximity, allowCenterline, centerOnlyX);
            var by = BestY(aa, bb, tolerance, proximity, allowCenterline, centerOnlyY);
            if (!bx.HasValue && !by.HasValue) continue;

            float sxc = bx.HasValue ? MathF.Abs(bx.Value.Delta) : float.MaxValue;
            float syc = by.HasValue ? MathF.Abs(by.Value.Delta) : float.MaxValue;
            float score = MathF.Min(sxc, syc);
            int prio = Math.Min(
                bx.HasValue ? AlignPriority(bx.Value.SourceRole, bx.Value.TargetRole) : int.MaxValue,
                by.HasValue ? AlignPriority(by.Value.SourceRole, by.Value.TargetRole) : int.MaxValue);

            if (score > bestScore + 1e-4f) continue;
            if (MathF.Abs(score - bestScore) <= 1e-4f && prio >= bestPriority) continue;

            bestScore = score;
            bestPriority = prio;
            target = _alignTargets[i];
            targetFrame = _alignTargetFrames[i];
            targetCurveFrame = _alignTargetCurveFrames[i];
            targetWave = useCurveFrame;
            targetCenterline = allowCenterline;
            targetCenterOnlyX = centerOnlyX;
            targetCenterOnlyY = centerOnlyY;
            x = bx;
            y = by;
        }
        return target != null;
    }

    private void BeginAlignPulse(AlignSnapKind kind, Vector2 point, float halfLen)
    {
        _alignPulseKind = kind;
        _alignPulsePoint = point;
        _alignPulseHalfLen = halfLen;
        _alignPulseStartMs = NowMs;
        _alignPulseEndMs = NowMs + AlignSnapPulseMs;
        _dirty = true;
    }

    /// <summary>吸附刚发生 / 某一轴刚吸住时，短亮一次；持续吸住不重复闪。</summary>
    private void MaybeBeginAlignPulse(in RectF a, bool hadX, bool hadY, Stroke oldTarget)
    {
        if (!_alignSnapX.HasValue && !_alignSnapY.HasValue) return;
        bool targetChanged = !ReferenceEquals(oldTarget, _alignSnapTarget);
        bool newX = !hadX && _alignSnapX.HasValue;
        bool newY = !hadY && _alignSnapY.HasValue;
        if (!targetChanged && !newX && !newY) return;

        float dpi = UiUnit;
        bool xMoved = _alignSnapX.HasValue && MathF.Abs(_alignSnapX.Value.Delta) > 0.01f;
        bool yMoved = _alignSnapY.HasValue && MathF.Abs(_alignSnapY.Value.Delta) > 0.01f;

        // 两个方向都真的动了（或本来就对齐）才画点；
        // 其中一个方向只是“本来就已经对齐”（delta≈0），就只画另一个方向的线，
        // 这样两个正弦周期拼接时看到的是竖缝，而不是一个笼统的圆点。
        if (_alignSnapX.HasValue && _alignSnapY.HasValue && xMoved && yMoved)
        {
            var p = new Vector2(_alignSnapX.Value.TargetLine, _alignSnapY.Value.TargetLine);
            BeginAlignPulse(AlignSnapKind.Point, p, 0f);
        }
        else if (xMoved)
        {
            var p = new Vector2(_alignSnapX.Value.TargetLine, AlignCenterY(a));
            BeginAlignPulse(AlignSnapKind.Vertical, p, 44f * dpi);
        }
        else if (yMoved)
        {
            var p = new Vector2(AlignCenterX(a), _alignSnapY.Value.TargetLine);
            BeginAlignPulse(AlignSnapKind.Horizontal, p, 44f * dpi);
        }
        else if (_alignSnapX.HasValue && _alignSnapY.HasValue)
        {
            var p = new Vector2(_alignSnapX.Value.TargetLine, _alignSnapY.Value.TargetLine);
            BeginAlignPulse(AlignSnapKind.Point, p, 0f);
        }
    }

    /// <summary>
    /// 整体移动矩阵：先按指针算纯平移，再在单选时叠一层选中框吸附。
    /// 只改平移量；模型 / 撤销仍然走原来的那一步（见 EndSelDrag）。
    /// </summary>
    private Matrix3x2 MoveMatrixWithAlignSnap(Vector2 cur, bool alt)
    {
        var raw = cur - _dragStartPoint;

        if (_dragTargets == null || _dragTargets.Length != 1 || alt
            || _alignTargets == null || _alignTargets.Length == 0)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }

        var s = _dragTargets[0];
        if (s == null || s.Locked || !s.IsShape)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }

        float dpi = DpiScale;
        float enterTol = AlignSnapEnterLogical * dpi;
        float releaseTol = AlignSnapReleaseLogical * dpi;
        float proximity = AlignSnapProximityLogical * dpi;
        bool movedEnough = Vector2.Distance(cur, _dragStartPoint) > ClickToleranceLogical * dpi;
        if (!movedEnough && !_alignSnapLatched)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }

        var aInk = s.WorldInkBounds;
        if (aInk.IsEmpty)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }
        aInk.MinX += raw.X; aInk.MaxX += raw.X;
        aInk.MinY += raw.Y; aInk.MaxY += raw.Y;

        var aCurve = _alignSourceWave ? _alignSourceCurveFrame : RectF.Empty;
        if (!aCurve.IsEmpty)
        {
            aCurve.MinX += raw.X; aCurve.MaxX += raw.X;
            aCurve.MinY += raw.Y; aCurve.MaxY += raw.Y;
        }
        var a = aInk;

        var oldTarget = _alignSnapTarget;
        bool hadX = _alignSnapX.HasValue;
        bool hadY = _alignSnapY.HasValue;
        AlignAxisCandidate? x = null;
        AlignAxisCandidate? y = null;

        // 已吸住同一个目标：先按释放距离验证；目标被删掉也立刻松开。
        if (_alignSnapLatched)
        {
            bool alive = _alignSnapTarget != null && Doc.Strokes.Contains(_alignSnapTarget);
            if (alive)
            {
                // 吸住期间只沿原来那一对线走：不因为另一条线更近就突然换线。
                bool allowCenterline = _alignSourceCenterline && _alignSnapTargetCenterline;
                bool centerOnlyX = allowCenterline && _alignSnapTargetAxisCenterOnlyX;
                bool centerOnlyY = allowCenterline && _alignSnapTargetAxisCenterOnlyY;
                bool curvePair = _alignSnapTargetWave && _alignSourceWave && !aCurve.IsEmpty
                                 && !_alignSnapTargetCurveFrame.IsEmpty;
                var af = curvePair ? aCurve : aInk;
                var bf = curvePair ? _alignSnapTargetCurveFrame : _alignSnapTargetFrame;
                a = af;
                x = _alignSnapX.HasValue
                    ? CandidateForRoles(_alignSnapX.Value.SourceRole,
                                        _alignSnapX.Value.TargetRole,
                                        xAxis: true, af, bf,
                                        releaseTol, proximity, allowCenterline, centerOnlyX)
                    : null;
                y = _alignSnapY.HasValue
                    ? CandidateForRoles(_alignSnapY.Value.SourceRole,
                                        _alignSnapY.Value.TargetRole,
                                        xAxis: false, af, bf,
                                        releaseTol, proximity, allowCenterline, centerOnlyY)
                    : null;

                // 原来只有一条轴吸住时，另一条轴后来进入范围也可以补上。
                // 仍然只在同一个目标框上补，不换目标。
                if (!x.HasValue) x = BestX(af, bf, enterTol, proximity, allowCenterline, centerOnlyX);
                if (!y.HasValue) y = BestY(af, bf, enterTol, proximity, allowCenterline, centerOnlyY);
            }
            if (!alive || (!x.HasValue && !y.HasValue))
            {
                ResetAlignSnapLatch();
                x = null;
                y = null;
            }
            else
            {
                _alignSnapX = x;
                _alignSnapY = y;
            }
        }

        // 没吸住：找最近目标框（5px 内）。
        if (!_alignSnapLatched
            && TryFindAlignTarget(aInk, aCurve, enterTol, proximity, _alignSourceCenterline,
                                  out var target, out var targetFrame,
                                  out var targetCurveFrame, out var targetWave,
                                  out var targetCenterline,
                                  out var targetCenterOnlyX, out var targetCenterOnlyY,
                                  out x, out y))
        {
            _alignSnapTarget = target;
            _alignSnapTargetFrame = targetFrame;
            _alignSnapTargetCurveFrame = targetCurveFrame;
            _alignSnapTargetWave = targetWave;
            _alignSnapTargetCenterline = targetCenterline;
            _alignSnapTargetAxisCenterOnlyX = targetCenterOnlyX;
            _alignSnapTargetAxisCenterOnlyY = targetCenterOnlyY;
            _alignSnapX = x;
            _alignSnapY = y;
            _alignSnapLatched = true;
            if (targetWave && !aCurve.IsEmpty) a = aCurve;
        }

        if (!_alignSnapLatched) return Matrix3x2.CreateTranslation(raw);

        if (_alignSnapX.HasValue) raw.X += _alignSnapX.Value.Delta;
        if (_alignSnapY.HasValue) raw.Y += _alignSnapY.Value.Delta;

        MaybeBeginAlignPulse(a, hadX, hadY, oldTarget);
        return Matrix3x2.CreateTranslation(raw);
    }

    /// <summary>
    /// 拖动中：每帧都从**按下那一刻的变换**重算，而不是在上一帧结果上继续乘。
    /// 后者会累积浮点误差，拖得越久偏得越多，撤销也回不到原样。
    /// </summary>
    private void UpdateSelDrag(float x, float y)
    {
        // 参数滑杆：每帧只改一个参数并重采样，不碰变换。
        if (_figureParamDragging) { ApplyFigureParamAt(x); return; }

        // 文本框：8 手柄改宽高（不是 Transform 缩放）。
        if (_textBoxResizing) { UpdateTextBoxResize(x, y); return; }

        // 拖端点：另一条路（改几何，模型不动），和"拖对象"分开走。
        if (_vertexDragging) { UpdateVertexDrag(x, y); return; }

        // 缩放 / 旋转不参与对象吸附：只清掉上一次可能留下的“吸住”状态。
        if (!_dragIsMove) ResetAlignSnapLatch();

        var cur = new Vector2(x, y);
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        // Alt = 临时关掉吸附。Shift 是"硬网格 15°"，Alt 是"完全自由"，两个修饰键
        // 各管一头，中间那档（默认的 90° 软吸附）不用按键。
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;

        Matrix3x2 m, localM;
        // 特殊形状吸附（规格 9.6）**只在拖四角时可能出现**：整体移动、旋转一律不吸
        // （那两件事改的是位置和姿态，改不了"这是不是个正方形"）。每帧先清空，
        // 下面那条分支吸住了再填。
        _shapeSnap = ShapeSnapKind.None;
        if (_dragIsMove)
        {
            // 整体移动：指针在画布上走多少，对象就走多少。**不能**在框坐标里算
            // 再共轭回来——框是斜的时候那样会走偏方向。
            // 单选拖动时这里再叠一层选中框吸附；每帧从按下点重算，
            // 所以吸住后再拖开也不会累积误差。
            m = MoveMatrixWithAlignSnap(cur, alt);
        }
        else if (_dragHandle == SelHandle.Rotate)
        {
            // —— 旋转：**一帧一步地累积**（角度不设上限，见 SelRotationDegrees）——
            //
            // 角度在**框坐标**里量（和真正施加的旋转矩阵同一套输入）。
            // 选中框现在一律轴对齐（ToCanvas 是单位阵），所以框坐标 == 画布坐标，
            // 量出来的角度就是屏幕上看到的角度——这也正是"读数按眼睛看的方向"那条规则。
            var c = new Vector2((_dragFrame.Local.MinX + _dragFrame.Local.MaxX) * 0.5f,
                                (_dragFrame.Local.MinY + _dragFrame.Local.MaxY) * 0.5f);
            var p = _dragFrame.ToLocalPoint(cur);
            _rotAccumDeg += SelectionHandles.RotationStepDegrees(c, _rotPrevPoint, p);
            _rotPrevPoint = p;

            float localDeg;
            bool snapped;
            if (_rotLineLike)
            {
                // —— 单选直线 / 箭头：读数与吸附都走 **α₀ + Δ**（用户 2026-09-18 澄清）——
                //
                // 用户原话："按照高中数学这个直线的倾斜角，现在是 45 度，那么我那个旋转手柄
                // 刚开始转的时候就是 45 度。那么逆时针是为正，顺时针为负，它可以无限转下去，
                // 也就说可以转到 360、720、1000 多度，或者说负的 2000 多度……除了初始角度
                // 是要调一下以外，其他和原来的最开始的逻辑是一样的。"
                //
                // 于是这里**不折角、不取模**：读数就是 `按下时的 α₀ + 这一拖累积转过的角 Δ`
                // （Δ 本来就无上限，见 _rotAccumDeg）。45° 的线逆时针转一圈 = 405°、
                // 再转 = 765°；顺时针 400° = −355°。
                // 吸附在**展开值**上做，特殊角按 180° 周期（405.5° 吸到 405°），
                // 所以"要不要吸"和折回那一版完全一样，只是吸完的数仍是展开的。
                float expanded = _rotStartInclination + _rotAccumDeg;
                float snappedDeg = SelectionHandles.SnapExpandedInclinationDegrees(expanded, shift, alt,
                                                                                  out snapped);
                if (snapped)
                {
                    // "要落到那个角还得多转多少度"——把它加进累积角，
                    // 于是**矩阵用的是吸过之后的角**，标签上的数和屏幕上看到的一致。
                    _rotAccumDeg += snappedDeg - expanded;
                }
                localDeg = _rotAccumDeg;
                SelRotationInclination = _rotStartInclination + localDeg;   // 展开值，故意不折
                SelRotationReadsInclination = true;
            }
            else if (_rotPoseLike && _dragTargets.Length == 1)
            {
                // —— 单选一个图形：读数与吸附都走**姿态角**（规格 9.7）——
                //
                // 和直线那一支是同一套账，只是目标角不同（那边是八个特殊角，这边是 0/90）：
                //   "按下时的姿态角 ＋ 累积角" → 在这个**连续**的角上吸 → 把最小修正
                //   加回累积角。必须在连续角上吸：折过的 [0,180) 里 "179.6°" 离 0° 只有 0.4°，
                //   但它们差整整 180°——直接吸会把只偏 0.4° 的三角形**翻过来**。
                //
                // 镜像过的图形"本地转 +1°"在屏幕上是 -1°（行列式为负），所以两个方向都要乘
                // _rotMirrored 的符号；同一个手势里镜像状态不会变（乘上去的是纯旋转）。
                float sgn = _rotMirrored ? -1f : 1f;
                float expanded = _rotStartPose + sgn * _rotAccumDeg;
                float snappedExpanded = SelectionHandles.SnapExpandedPoseDegrees(expanded, shift, alt,
                                                                                out snapped);
                if (snapped)
                {
                    _rotAccumDeg += sgn * (snappedExpanded - expanded);
                    // 吸住时标签直接写"**吸到的那条线**"（0 / 90，或 Shift 网格上的角）：
                    // 从矩阵里解出来的值在浮点噪声下可能印成 `180.0°`（数学上和 0° 是同一条线，
                    // 但用户要的就是"拖到读数 0° 就转正了"）。两者差在 0.001° 以内，
                    // 所以下面那条"矩阵与标签同一个角"的自检照样成立。
                    // 先按 0.001° 抹一遍浮点噪声再折：`-0.0000001` 折进 [0,180) 会变成 179.9999999，
                    // 印出来还是 "180.0°"——把噪声抹掉才拿得到干净的 0.0（远细于一位小数的显示精度）。
                    SelRotationPose = SelectionHandles.FoldDegrees(MathF.Round(snappedExpanded, 3));
                }
                localDeg = _rotAccumDeg;
                SelRotationReadsPose = true;    // 没吸住时数值在下面 m 算出来之后再填
            }
            else
            {
                // 其它情况（多选 / 图像 / 自由笔迹 / 圆）：照旧读 Δ、照旧吸 90°。
                // 吸附作用在**累积角**上：90° / 15° 的整数倍在负角度、超过一圈的角度上照样对得上。
                localDeg = SelectionHandles.SnapRotationDegrees(_rotAccumDeg, shift, alt, out snapped);
                SelRotationReadsInclination = false;
            }

            // 这一段是"框坐标和屏幕反手时，把读数翻回眼睛看到的方向"的通用保险：
            // 镜像过的对象 + 斜框的组合下，本地角度的正负和屏幕是反的。
            // 选中框改成**一律轴对齐**之后 ToCanvas 恒为单位阵，这里恒不触发；
            // 留着是因为公式本来就要覆盖"框有自己的朝向"那一天（见 SelectionFrame 的注释）。
            // 注：它只管 Δ 那个读数；α 是从**画布坐标**量出来的，天生就是眼睛看到的方向。
            SelRotationDegrees = SelectionHandles.IsMirrored(_dragFrame.ToCanvas) ? -localDeg : localDeg;
            SelRotationSnapped = snapped;
            SelRotating = true;

            // 矩阵用**吸附后的那个数**（没镜像时它和标签上的数一模一样）。
            localM = SelectionHandles.RotateMatrix(localDeg, c);
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }
        else
        {
            // 手柄换算在**框坐标**里做（缩放的锚点是"对角那个手柄"，只有在框坐标里
            // 才是"沿着框的两条边"），再共轭回画布坐标：M = F⁻¹ · M_local · F
            //
            // 拖**矩形四角**时先问一句"要不要吸成正方形"（规格 9.6）。顺序是先吸附、
            // 再回退到通用换算：吸住了就用吸附给的那个矩阵，没吸住一个字都不改。
            // 注意它只改 `localM`（框坐标里的矩阵），共轭那一步照旧——这样"转过/镜像过的
            // 框"那种情形（今天框恒轴对齐，但公式留着）不会被绕过。
            if (SelectionHandles.TrySnapSquareCorner(_dragTargets, _dragHandle, _dragFrame,
                                                     cur, DpiScale, alt, out var squareM))
            {
                _shapeSnap = ShapeSnapKind.Square;
                _shapeSnapAnchor = cur;             // 胶囊贴在指针（= 被拖的那个角）上
                localM = squareM;
            }
            else
            {
                localM = SelectionHandles.DragMatrix(_dragHandle, _dragFrame,
                                                     _dragStartPoint, cur, DpiScale, shift, shift, alt);
            }
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }

        // 姿态角读数**从最终矩阵里解出来**：`对象变换 × 预览矩阵` 正是浮动层画那一帧用的式子，
        // 所以标签上的数就是屏幕上那个姿态——"矩阵与标签同一个角"是构造出来的，不是凑出来的
        // （直线那一支当年就是在这里对不上：标签用一个角、矩阵用另一个角）。
        // 吸住那一档已经在上面填成"吸到的那个角"了（0/90 更好看，见那里的注释），跳过。
        if (_rotPoseLike && _dragTargets.Length == 1 && !SelRotationSnapped)
            SelRotationPose = SelectionHandles.PoseAngleDegrees(_dragTargets[0].Transform * m);

        // 方案 B：**不动模型**，实时位移只活在 _selDragMatrix 里，由浮动层画预览。
        //
        // 以前这里是"每帧 SetTransformLive"，它会把渲染版本号抬起来，于是内容层
        // 每一帧都要把"走过的面积"里的所有笔迹重画一遍——实测散布全屏的一小撮
        // 是 19ms/帧，而其中被拖的只有那几条。现在内容层只在**手势开始**被标脏一次
        // （把这一批从块里摘出去那一下），中途一帧都不碰。
        //
        // 脏区 = 上一帧预览的位置 ∪ 这一帧预览的位置：后缓冲里躺着的是两帧前的画面，
        // 而这两块并起来正好覆盖"两帧之间差了哪些像素"（和以前"旧位 ∪ 新位"同一套账）。
        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);
        _selDragMatrix = m;
        _dragPrevBounds = PreviewBounds(m);
        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);

        // 方案 A：真的动过才收装饰（单击一下不该闪）。
        if (!_selDragMoved
            && Vector2.Distance(cur, _dragStartPoint) > ClickToleranceLogical * DpiScale)
            _selDragMoved = true;

        _dirty = true;
    }

    /// <summary>
    /// 松手：先把模型恢复到按下那一刻，再提交**一条**变换命令。
    ///
    /// 这样拖动过程中的实时预览不产生撤销记录，而撤销一步就精确回到拖动前
    /// ——不会出现"拖的时候动了三十次，要按三十次撤销"那种事。
    /// </summary>
    private void EndSelDrag()
    {
        // 参数滑杆：提交一步撤销，结束。
        if (_figureParamDragging) { FinishFigureParamDrag(); return; }

        // 文本框：改宽高手势收尾（模型已经实时改好，这里只清状态）。
        if (_textBoxResizing) { FinishTextBoxResize(); return; }

        SelDragging = false;
        SelRotating = false;            // 度数标签只在拖动中出现
        ResetAlignSnapLatch();          // 吸附只活在这一次拖动里；短亮自己淡出
        ClearAlignTargets();
        _rotAccumDeg = 0f;              // 下一次拖拽从 0 开始数（标签也不显示了）
        SelRotationReadsInclination = false;   // 读数归位：下一次按下时重新判定读 α 还是 Δ
        SelRotationReadsPose = false;          // 姿态角那一档同样归位
        _shapeSnap = ShapeSnapKind.None;       // "正方形"那颗胶囊也只在拖动中出现

        // 拖端点：收尾走"改几何"那条路（一次拖拽 = 一步改几何的撤销）。
        if (_vertexDragging) { CommitVertexDrag(); return; }

        if (_dragTargets == null) return;

        // 一批都没得动（例如选中的全锁着）：只把状态收干净，**不要**提交空动作
        // （提交了就是"点一下多一条空撤销记录"）。
        if (_dragTargets.Length == 0)
        {
            _dragTargets = null;
            _dragStartXform = null;
            _dragHandle = SelHandle.None;
            _dragIsMove = false;
            _dragHitStroke = null;
            _selDragMatrix = Matrix3x2.Identity;
            ReattachAfterDrag();
            _dirty = true;
            return;
        }

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            Doc.SetTransformLive(_dragTargets[i], _dragStartXform[i]);
            Doc.Dirty.Add(_dragTargets[i].PaddedBounds);
        }

        Doc.Selected.Clear();
        foreach (var t in _dragTargets) Doc.Selected.Add(t);
        // **一点没移动就别进撤销栈**：按一下选中框（或者点选一条）原本会多出一条
        // "原样"的撤销记录——点选一多，撤销栈里全是这种空记录。
        if (!_selDragMatrix.IsIdentity)
            Doc.ApplyTransform(_selDragMatrix);

        // "点一下把多选收窄成单选"：按下时命中了一条、而这一次**一点没移动** → 只留它
        // （PPT/Figma 的行为）。移动过就正常整体搬，不收窄。
        if (_dragIsMove && _dragHitStroke != null && _selDragMatrix.IsIdentity
            && Doc.Selected.Count > 1 && Doc.Selected.Contains(_dragHitStroke))
            Doc.SelectOnly(new[] { _dragHitStroke });

        _dragTargets = null;
        _dragStartXform = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _dragHitStroke = null;
        _selDragMatrix = Matrix3x2.Identity;

        // 方案 B：这一批回到内容层（下一次重画就把它们画进块里）。
        // 顺序放在最后：上面那条"提交变换"已经把旧位与新位都标脏了，
        // 所以这一帧的重画一定会带上它们（而不是继续画预览）。
        ReattachAfterDrag();

        _dirty = true;
    }

    /// <summary>
    /// 操作条按钮。下标与 SelectionHandles.BarButtonAt 的返回值和渲染时的
    /// 图标数组一一对应：0 复制 / 1 删除 / 2 左右翻转 / 3 上下翻转 / 4 旋转。
    /// </summary>
    /// <summary>
    /// 图片选中条上的「美化」：去黄 / 提亮 / 纸白 / 轻锐化，一步撤销。
    /// 只换图片对象的像素，不改尺寸、方向、位置；透明图/已经够白的图直接跳过。
    /// </summary>
    private void BeautifySelection()
    {
        if (_beautifyBusy)
        {
            SetInkStatus("正在美化，请稍等…");
            _dirty = true;
            return;
        }
        if (Doc.Selected.Count == 0) return;
        var todo = new List<Stroke>();
        int already = 0, notImage = 0;
        foreach (var s in Doc.Selected)
        {
            if (!s.IsImage || s.Image == null) { notImage++; continue; }
            // 已美化且还留着原图 → 这一次就是“恢复原图”；原图丢了才跳过。
            if (s.Image.Beautified && s.Image.BeautifyOriginal == null) { already++; continue; }
            todo.Add(s);
        }
        if (todo.Count == 0)
        {
            SetInkStatus(notImage > 0 && already == 0
                ? "选中的不是图片"
                : already > 0 ? "这张已经美化过（原图未保留）" : "没有可美化的图片");
            _dirty = true;
            return;
        }

        // 自检要确定性：直接同步跑；产品里放后台，点下去按钮/画布不卡。
        if (SelfCheckMode)
        {
            var results = BuildBeautifyResults(todo, out int skip);
            ApplyBeautifyResults(results, skip);
            return;
        }

        _beautifyBusy = true;
        SetInkStatus(Loc.T("正在美化 {0} 张…", todo.Count));
        _dirty = true;
        NotifyUiStateChanged();
        var items = todo.ToArray();
        Task.Run(() =>
        {
            try
            {
                var results = BuildBeautifyResults(items, out int skip);
                PostUi(() => ApplyBeautifyResults(results, skip));
            }
            catch (Exception ex)
            {
                PostUi(() =>
                {
                    _beautifyBusy = false;
                    SetInkStatus("美化失败：" + ex.Message);
                    _dirty = true;
                    NotifyUiStateChanged();
                });
            }
        });
    }

    private List<(Stroke Stroke, ImageData Old, ImageData New, bool Reverted)> BuildBeautifyResults(
        IReadOnlyList<Stroke> todo, out int skip)
    {
        var results = new List<(Stroke, ImageData, ImageData, bool)>(todo.Count);
        skip = 0;
        foreach (var s in todo)
        {
            var old = s.Image;
            if (old == null) { skip++; continue; }
            if (old.Beautified)
            {
                // 再点一次 = 恢复原图；原图只在本次进程里保留。
                if (old.BeautifyOriginal != null)
                    results.Add((s, old, old.BeautifyOriginal, true));
                else
                    skip++;
                continue;
            }
            var pixels = PhotoBeautify.Process(old.Bgra, old.Width, old.Height);
            if (pixels == null) { skip++; continue; }
            var enhanced = ImageData.AdoptPremultiplied(old.Width, old.Height, pixels);
            if (enhanced == null) { skip++; continue; }
            enhanced.MarkBeautified(old);
            results.Add((s, old, enhanced, false));
        }
        return results;
    }

    private void ApplyBeautifyResults(
        List<(Stroke Stroke, ImageData Old, ImageData New, bool Reverted)> results, int skip)
    {
        _beautifyBusy = false;
        if (_quit)
        {
            foreach (var r in results)
                if (!r.Reverted) r.New.Release();
            return;
        }

        var compound = new CompoundAction();
        int done = 0, back = 0;
        foreach (var r in results)
        {
            // 后台算的时候用户可能把图删了/换了：对不上就丢掉，不要往新文档里硬塞。
            if (!Doc.Strokes.Contains(r.Stroke) || !ReferenceEquals(r.Stroke.Image, r.Old))
            {
                if (!r.Reverted) r.New.Release();   // 恢复原图那份不在这里释放
                skip++;
                continue;
            }
            compound.Steps.Add(new ReplaceImageAction(r.Stroke, r.Old, r.New));
            if (r.Reverted) back++; else done++;
        }

        if (compound.Steps.Count == 0)
        {
            SetInkStatus(skip > 0 ? "这张已经是白纸 / 透明图，不需要再美化" : "没有可美化的图片");
            _dirty = true;
            NotifyUiStateChanged();
            return;
        }

        compound.Redo(Doc);                 // 先把新像素换上去，再进撤销栈
        Doc.CommitInteractive(compound);
        _dirty = true;
        NotifyUiStateChanged();
        string msg = done > 0 && back > 0
            ? $"已美化 {done} 张、恢复原图 {back} 张；Ctrl+Z 可撤销"
            : back > 0
                ? $"已恢复原图 {back} 张；Ctrl+Z 可撤销"
                : $"已美化 {done} 张；再点一次可恢复原图，Ctrl+Z 可撤销";
        if (skip > 0) msg += $"（{skip} 张跳过）";
        SetInkStatus(msg);
        Console.WriteLine($"[图片] 美化 {done} 张，恢复原图 {back} 张，跳过 {skip} 张");
    }

    private void RunBarAction(int index, in SelectionFrame frame, in RectF aabb)
        => RunBarButton(SelectionHandles.BarButtonOf(index, BarSurfaceNow), frame, aabb);

    /// <summary>
    /// 执行一个操作条语义。**渲染、命中、`⋯` 菜单行都走这一份**，
    /// 不再用下标二次映射（2026-11-08 收口：隐藏动作没有下标）。
    /// </summary>
    private void RunBarButton(SelBarButton btn, in SelectionFrame frame, in RectF aabb)
    {
        // 下标 ↔ 语义只在 SelBarButton 里写一遍（渲染、命中、动作三处共用它）。
        bool textBoxOnly = SingleTextBoxSelection;
        if (textBoxOnly)
        {
            var tb = Doc.Selected[0].TextBox;
            if (btn == SelBarButton.Color && tb?.Role == TextBoxRole.AiQuestion)
            {
                SetInkStatus("题卡颜色固定为黑题红解析");
                _dirty = true;
                return;
            }
        }
        if (btn != SelBarButton.Ai && btn != SelBarButton.More) CloseSelectionMenu();
        switch (btn)
        {
            // **收起工具条**（用户 2026-09-16 更正：条首那个 ✕ 不是"取消选择"）。
            // 收起后只剩一个小圆钮，点它展开；框和手柄照旧，收起的只是"条"。
            case SelBarButton.Collapse:
                SelBarCollapsed = true;
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                CloseSelectionMenu();
                break;

            case SelBarButton.Beautify:
                BeautifySelection();
                break;

            case SelBarButton.Ai:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                if (SelAiMenuOpen) CloseSelectionMenu();
                else OpenSelectionMenu(ai: true);
                break;

            case SelBarButton.More:
                CloseCustomColor();
                SelPanelOpen = SelPanel.None;
                if (SelMoreMenuOpen) CloseSelectionMenu();
                else OpenSelectionMenu(ai: false);
                break;

            case SelBarButton.Color:
                // AI 题卡的颜色/粗细固定（题干黑、答案红），点开面板也调不了，直接不响应。
                if (Doc.Selected.Count == 1
                    && Doc.Selected[0].TextBox?.Role == TextBoxRole.AiQuestion)
                    break;
                if (SelPanelOpen == SelPanel.Ink)
                {
                    CloseCustomColor();                         // 关面板 = 色板一起关
                    SelPanelOpen = SelPanel.None;
                }
                else
                {
                    SelPanelOpen = SelPanel.Ink;
                }
                break;

            case SelBarButton.FontSize:
                // 只有文字条有这一格：开/关常用字号下拉（10/14/18/24/48/64）。
                CloseCustomColor();
                SelPanelOpen = SelPanelOpen == SelPanel.FontSize ? SelPanel.None : SelPanel.FontSize;
                break;

            case SelBarButton.Lock:
                ToggleSelectionLock();
                break;

            case SelBarButton.Layer:
                CloseCustomColor();                             // 换面板 = 色板跟着关
                SelPanelOpen = SelPanelOpen == SelPanel.Layer ? SelPanel.None : SelPanel.Layer;
                break;

            // `⋯` 里的置顶/置底：直接执行，不再开二级“层级”弹窗。
            case SelBarButton.Top:
                ReorderSelection(toFront: true);
                break;
            case SelBarButton.Bottom:
                ReorderSelection(toFront: false);
                break;

            case SelBarButton.Export:
                // **直接弹系统"另存为"**，格式在那个对话框的类型栏里选（见 ExportFormats）。
                //
                // 早先前这里先开一层我们自己的两格面板，理由是"系统那个下拉点不开"
                // （用户报的"选不到 jpg"）。那个真因后来查清了：**覆盖层每秒抢一次置顶**，
                // 把对话框的下拉挤掉了（见 ReassertTopmost）。真因修掉之后，
                // 用户 2026-09-17 提的那句就成立了——"那下面这两个图标就没有用了吧，
                // 用户都可以自己保存图片了"：两处选格式反而绕，删掉还少一步。
                ExportSelectionPref();
                break;

            case SelBarButton.Library:
            {
                // **收藏**：把选中的对象存成一个条目（磁盘那点事在 ShapeLibrary 里）。
                // 重复内容不写新文件；图标会从“星标＋”换成“实心星＋对勾”。
                int saved = SaveSelectionToLibrary();
                if (saved > 0)
                    Console.WriteLine($"已收藏（{saved} 个对象）");
                else if (LibraryFeedback == LibraryFeedbackKind.Error)
                    Console.WriteLine("收藏失败（选中为空或者写不进去）");
                break;
            }

            // 复制按钮 = **只再制**（原件保留 + 新副本错位）；2026-11-03 起不再写剪贴板。
            // 复制到剪贴板走「选择」上带右端的「复制」（触摸屏也能用）。
            case SelBarButton.Copy:
                CopySelectionAndDuplicate();
                break;

            // 翻转绕**框自己的轴**。框现在一律轴对齐，所以它就是屏幕的左右/上下翻——
            // 和"框永远正着"同一条规则（见 SelectionFrame 的注释）。
            case SelBarButton.FlipH:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: true)));
                break;
            case SelBarButton.FlipV:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: false)));
                break;

            case SelBarButton.Delete:
            {
                // 编辑中直接删：先把原生 EDIT 收掉，别让它留在屏幕上当"残字"。
                if (_textBoxEditing) EndTextBoxEditFromUi(false);
                int n = Doc.Selected.Count, locked = 0;
                foreach (var s in Doc.Selected) if (s.Locked) locked++;
                Doc.DeleteSelected();
                SelBarHover = -1;
                ClearEngineTooltip();
                Console.WriteLine(locked > 0
                    ? $"删除 {n - locked} 个对象（{locked} 个锁定，没删）"
                    : $"删除 {n} 个对象");
                break;
            }
        }
        Laser.Clear();
        _dirty = true;
    }

    /// <summary>
    /// 把"框坐标下的变换"翻译成"画布坐标下的变换"：<c>F⁻¹ · M · F</c>。
    ///
    /// 框是斜的时候，同一句"沿框的横轴放大两倍"在画布坐标里是个斜的缩放。
    /// 共轭就是在两套坐标之间翻译这件事——不用为"斜着的情况"另写一套公式。
    /// </summary>
    private static Matrix3x2 Conjugate(in Matrix3x2 frame, in Matrix3x2 localM)
    {
        if (frame.IsIdentity) return localM;
        if (!Matrix3x2.Invert(frame, out var inv)) return localM;
        return inv * localM * frame;
    }
}
