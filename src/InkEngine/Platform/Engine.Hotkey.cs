// [职责] 热键处理：WM_HOTKEY → 动作。
// [测试] --keytest

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
    //  Hotkeys
    // =====================================================================

    private void HandleHotkey(int id)
    {
        // id 是"注册顺序"，动作由键位表决定（见 RegisterHotkeys）。
        var action = ActionForHotkeyId(id);
        if (action == KeyAction.None) return;
        RunAction(action);
        // 换工具/换模式之后指针形状立刻要跟着变。
        ApplyCursor();
        _dirty = true;
    }

    /// <summary>
    /// 执行一个动作。**全局热键和批注内快捷键走同一个入口**——否则同一个动作
    /// 会有两份实现，早晚会不一致（"按 Ctrl+Alt+1 换笔换得好好的，按 Ctrl+1 却少清了激光"）。
    /// </summary>
    private void RunAction(KeyAction action)
    {
        // 手测台：事件流水。撤销尤其重要——"擦完马上撤销"就是"这一擦不是我想要的"。
        if (EraserTelemetry != null && action != KeyAction.None)
            EraserTelemetry.Note(KeyMap.Describe(action), NowMs);

        // 呼出盘开着时，别的动作先把它收掉（它自己的"再按一次"不算；Esc 在 HandleKeyDown 里单独处理）。
        if (RadialPaletteActive && action != KeyAction.RadialPalette)
            CancelRadialPalette("其它动作");

        // 连续微调会话：除了微调/缩放本身，其它任何动作都表示"这次手停了"——先封口，
        // 免得下一次微调把增量叠到一条已经翻篇的动作上（停手超时由 StepMicroAdjust 收）。
        if (_microAdjust != null && !IsMicroAdjustAction(action)) _microAdjust = null;

        switch (action)
        {
            // 穿透开关（全局 Ctrl+Alt+Shift+T 这条同一条路）：退出时恢复进穿透前的板态，
            // 见 SetPassThrough 的 restoreBoard。用户主动退出 = 回到画笔（2026-10-24）。
            case KeyAction.TogglePassThrough:
                // 文档模式里用户入口不开穿透（2026-11-15；和主条那格同一条判据）。
                if (!PassThrough && BlockUserPassThroughInDoc()) break;
                // 和点箭头同一条路：进穿透前按设置清桌面（白板/PPT/文档不动）。
                if (!PassThrough) ClearDesktopOnArrowFromUi();
                SetPassThrough(!PassThrough, restoreBoard: true, toPen: PassThrough);
                break;
            // 呼出盘：按住才出来的"标迹菜单"（松手确认，见 OpenRadialPalette）。
            case KeyAction.RadialPalette: OpenRadialPalette(); break;
            // 工具键统一走 ToolKeyPress：**不管是应用内键还是"放映时的临时全局热键"**，
            // 都要有"已经是它 → 换色/换档"这条逻辑（用户 2026-09-30 实测：放映里 Ctrl+P
            // 能切到笔了，但已经是笔时再按不换色——就是因为这条热键路径漏了 ToolKeyPress）。
            case KeyAction.ToolPen: ToolKeyPress(KeyAction.ToolPen); break;
            case KeyAction.ToolHighlighter: ToolKeyPress(KeyAction.ToolHighlighter); break;
            case KeyAction.ToolLaser: ToolKeyPress(KeyAction.ToolLaser); break;
            case KeyAction.ToolEraser: ToolKeyPress(KeyAction.ToolEraser); break;
            case KeyAction.ToolPixelEraser: ToolKeyPress(KeyAction.ToolPixelEraser); break;
            case KeyAction.SplitErased:
            {
                int n = Doc.SplitErasedSelection();
                Console.WriteLine(n > 0
                    ? $"拆开擦断的笔迹：{n} 条 → 各段成为独立对象（可单独搬运/删除）"
                    : "拆开擦断的笔迹：选中的里面没有被擦断的（先用框选选中它）");
                break;
            }
            case KeyAction.ToolCapture: SwitchTool(Tool.Capture); BeginCaptureMode(); break;
            case KeyAction.ToolMarquee: SwitchTool(Tool.Marquee); break;
            // 图形**没有键位动作**（用户 2026-09-19 定：图形通通不要快捷键），
            // 换种类只有面板上带那一条路（`FullUi.ActivateSegment` → `SwitchTool`）。
            case KeyAction.SelectShape: ToggleSelectMode(); break;
            case KeyAction.Undo: Doc.Undo(); Laser.Clear(); break;
            case KeyAction.Redo: Doc.Redo(); break;
            case KeyAction.Copy: CopySelectionToClipboard(); break;
            // 键盘这一条 = 橡皮带那个「清空墨迹」：只清当前页；
            // 整份文档/演示文稿的清空在翻页条菜单（2026-10-25 拆开）。
            case KeyAction.Clear: ClearCurrentPageFromUi(); break;
            case KeyAction.ToggleBoard: SetBoardFromUi(!BoardOn); break;
            case KeyAction.ToggleHud: ShowHud = !ShowHud; break;
            case KeyAction.CycleWidth: CycleWidth(); break;
            case KeyAction.ToggleKeyboardMode: SetKeyboardMode(!KeyboardMode); break;
            case KeyAction.Quit: _quit = true; break;

            case KeyAction.SelectAll: SelectAll(); break;
            case KeyAction.Duplicate: Doc.DuplicateSelected(); break;
            case KeyAction.DeleteSelected:
                if (_textBoxEditing) EndTextBoxEditFromUi(false);
                Doc.DeleteSelected();
                break;
            case KeyAction.CancelSelection: Doc.Selected.Clear(); break;
            case KeyAction.Paste: PasteFromClipboard(); break;
            // 有选中 = 左/右移 1；**没选中：←→ 也是翻页**（2026-10-05 用户定：
            // 四个方向键的语法统一——有选中就微调，没选中就 ←→ 翻页、↑↓ 滚画布）。
            // 放映里没选中时同样走 ←→（代 PPT 翻页），和放映临时全局键那条路一致。
            case KeyAction.NudgeLeft:
                if (Doc.Selected.Count > 0) Nudge(-1f, 0f);
                else if (BoardOn) FlipPageFromUi(false);
                else if (PptMode) PptPrevFromUi();
                else FlipPageFromUi(false);
                break;
            // ↑↓：**有选中 → 微调；没选中 → 上下滚画布**（用户 2026-09-30 定："任何情况下
            // 都能上下滑动画布"，一格 = 屏幕高的 1/10，照 ClassIn 的手感）。
            // **2026-11-23**：放映且**没开白板**时 ↑↓ 也交给 PPT 翻页——翻页笔的上下翻键
            // 实际发 VK ↑/↓，这样无论焦点在覆盖层还是 WPS 都统一（白板仍滚画布）。
            case KeyAction.NudgeUp:
                if (Doc.Selected.Count > 0) Nudge(0f, -1f);
                else if (PptMode && !BoardOn) PptPrevFromUi();
                else ScrollCanvasBy(+ScrollStepLogical);
                break;
            case KeyAction.NudgeRight:
                if (Doc.Selected.Count > 0) Nudge(1f, 0f);
                else if (BoardOn) FlipPageFromUi(true);
                else if (PptMode) PptNextFromUi();
                else FlipPageFromUi(true);
                break;
            case KeyAction.NudgeDown:
                if (Doc.Selected.Count > 0) Nudge(0f, 1f);
                else if (PptMode && !BoardOn) PptNextFromUi();
                else ScrollCanvasBy(-ScrollStepLogical);
                break;
            case KeyAction.NudgeLeftFar: Nudge(-10f, 0f); break;
            case KeyAction.NudgeUpFar: Nudge(0f, -10f); break;
            case KeyAction.NudgeRightFar: Nudge(10f, 0f); break;
            case KeyAction.NudgeDownFar: Nudge(0f, 10f); break;
            case KeyAction.ScaleUp: ScaleSelection(1.1f); break;
            case KeyAction.ScaleDown: ScaleSelection(1f / 1.1f); break;
            // PageUp / PageDown 跟随“当前这张纸”：白板 → 白板屏，PPT → PPT 页，
            // 文档 → 文档页。板开着时**绝不**落到下层的 PPT / 文档；
            // 这样独立白板之后，三个模式也不用记三套翻页键。
            case KeyAction.FlipPageUp:
                if (BoardOn) FlipPageFromUi(false);
                else if (PptMode) PptPrevFromUi();
                else if (DocView.IsOpen) FlipPageFromUi(false);
                break;
            case KeyAction.FlipPageDown:
                if (BoardOn) FlipPageFromUi(true);
                else if (PptMode) PptNextFromUi();
                else if (DocView.IsOpen) FlipPageFromUi(true);
                break;
            // 放映时 ←→（临时全局热键送进来的）：**有选中 → 微调；没选中 → 代 WPS/PPT 翻页**。
            // 键盘在我们手里，不拦的话 WPS 收不到 ←→，表现就是"按了没反应"（用户实测）。
            case KeyAction.PptPrev:
                if (Doc.Selected.Count > 0) Nudge(-1f, 0f);
                else if (BoardOn) FlipPageFromUi(false);
                else PptPrevFromUi();
                break;
            case KeyAction.PptNext:
                if (Doc.Selected.Count > 0) Nudge(1f, 0f);
                else if (BoardOn) FlipPageFromUi(true);
                else PptNextFromUi();
                break;
            // 放映时 ↑↓（临时全局热键送进来的）：和普通模式同一条逻辑——一格 = 屏幕高的 1/10。
            case KeyAction.PanUp:
                if (Doc.Selected.Count > 0) Nudge(0f, -1f); else ScrollCanvasBy(+ScrollStepLogical);
                break;
            case KeyAction.PanDown:
                if (Doc.Selected.Count > 0) Nudge(0f, 1f); else ScrollCanvasBy(-ScrollStepLogical);
                break;
        }
    }

    // ---- 连续微调会话（2026-10-05）-----------------------------------------
    //
    // 问题：方向键按住会自动重复（Windows 连发 KEYDOWN），旧实现每一下都是一条
    // `ApplyTransform` → 撤销栈里瞬间十几条，"移歪了想撤销要按十几次 Ctrl+Z"。
    // 修法：微调 / 缩放**共用一条动作**，后续增量叠上去；松开或停 400ms 就"封口"，
    // 下一次微调另开一条。中途被别的动作改过（鼠标拖动、撤销、清空……）也认得出来——
    // `TryAppendDelta` 拿文档版本号当闸（见 Model.TransformObjectsAction）。
    private TransformObjectsAction _microAdjust;
    private double _microAdjustLastMs = double.NegativeInfinity;
    private const double MicroAdjustMergeMs = 400;

    private static bool IsMicroAdjustAction(KeyAction a) => a is
        KeyAction.NudgeLeft or KeyAction.NudgeUp or KeyAction.NudgeRight or KeyAction.NudgeDown or
        KeyAction.NudgeLeftFar or KeyAction.NudgeUpFar or KeyAction.NudgeRightFar or KeyAction.NudgeDownFar or
        KeyAction.ScaleUp or KeyAction.ScaleDown;

    /// <summary>微调或缩放应用一步；和上一步还能接上（同一批对象、中间没别的改动）就合并。</summary>
    private void MicroAdjust(in Matrix3x2 delta)
    {
        if (Doc.Selected.Count == 0) return;
        if (_microAdjust != null && _microAdjust.SameTargets(Doc.Selected)
            && _microAdjust.TryAppendDelta(Doc, delta))
        {
            _microAdjustLastMs = NowMs;
            _dirty = true;
            return;
        }
        var act = new TransformObjectsAction(Doc.Selected, delta);
        act.Redo(Doc);
        Doc.CommitInteractive(act);
        _microAdjust = act;
        _microAdjustLastMs = NowMs;
        _dirty = true;
    }

    /// <summary>主循环每帧叫：停手超过 400ms → 会话封口（下一次微调另起一条撤销记录）。</summary>
    private void StepMicroAdjust()
    {
        if (_microAdjust != null && NowMs - _microAdjustLastMs > MicroAdjustMergeMs)
            _microAdjust = null;
    }

    /// <summary>
    /// 选中对象的键盘缩放（`Ctrl+=` / `Ctrl+-`）：以**选区框中心**为锚（和双指捏合同一个
    /// 取点，见 TouchGestureMoved 那一段），一步 10%。走 <see cref="MicroAdjust"/>，
    /// 所以连按五下 = 一条撤销。
    /// </summary>
    private void ScaleSelection(float factor)
    {
        if (Doc.Selected.Count == 0)
        {
            Console.WriteLine("缩放选中：现在没有选中对象（先框选）");
            return;
        }
        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        float cx = (aabb.MinX + aabb.MaxX) * 0.5f;
        float cy = (aabb.MinY + aabb.MaxY) * 0.5f;
        // 防退化：缩到只剩几像素就别再缩了（倒不是矩阵会坏，是屏幕上等于消失了）。
        float maxSide = MathF.Max(aabb.MaxX - aabb.MinX, aabb.MaxY - aabb.MinY);
        if (factor < 1f && maxSide * factor < 4f)
        {
            Console.WriteLine("缩放选中：已经缩到最小（再缩就没有了）");
            return;
        }
        MicroAdjust(Matrix3x2.CreateTranslation(-cx, -cy)
                  * Matrix3x2.CreateScale(factor)
                  * Matrix3x2.CreateTranslation(cx, cy));
    }

    /// <summary>
    /// 方向键微调。**连续按（含按住自动重复）合并成一步撤销**（2026-10-05）：
    /// 每一步都走 <see cref="MicroAdjust"/>，同一次"手不停"只留一条撤销记录，
    /// 停手 400ms 或中途做了别的事才封口。
    /// </summary>
    private void Nudge(float dx, float dy) => MicroAdjust(Matrix3x2.CreateTranslation(dx, dy));

    /// <summary>
    /// 切换**当前工具**的粗细。以前这里写死改笔宽，于是"选了荧光笔按切粗细没反应"
    /// ——改的不是它。四种工具各有一张档位表（见 WidthPresets 的注释）。
    /// </summary>
    /// <summary>
    /// 换工具。**用户规则（2026-09-15 定，也是 InkClass / PPT / Figma 的惯例）**：
    /// "选中是临时上下文"——除了框选工具自己，换到任何别的工具都**收起选区**。
    ///
    /// 不这么做会出两个问题：① 选区遮罩会把接下来的第一笔吃掉（老师点完工具画不出来，
    /// 得先点一下空白）；② 用户看着"我已经换工具了，怎么还选着"。
    /// 滚动、激光、键盘编辑键**不算**换上下文（见 计划-选中工具.md 的那张表）。
    /// </summary>
    private void SwitchTool(Tool t)
    {
        // 用户换工具 = 已经决定自己动手：恢复提示关掉（打开文档顺手切回笔那一次除外）。
        if (DocResumeVisible && t != Tool && !_docResumeJustOpened)
            DismissDocResume("换工具");
        // 离开文本工具 = 先把正在编辑的文字收掉（光标/白底不能跟着换工具跑）。
        if (_textBoxEditing && t != Tool.Text) EndTextBoxEditFromUi(true);
        if (t != Tool.Text)
        {
            _lastTextBoxClickStroke = null;
            _lastTextBoxClickMs = double.NegativeInfinity;
        }
        // 老师自己换工具 = 不再自动恢复“插入前的工具”；插入完成后会再置 true。
        _restoreToolAfterLibrarySelection = false;
        // 换工具也把“拿起的收藏卡片”放下，别让虚影跟着跑到新工具里。
        if (LibraryUseSelected != null || _libraryCardPress != 0)
        {
            LibraryUseSelected = null;
            _libraryPending = null;
            _libraryPendingTitle = null;
            _libraryCardPress = 0;
            _libraryCardPressEntry = null;
            _libraryCardPressWasSelected = false;
            Native.ReleaseCapture();
        }
        // 收藏弹窗是常驻浮层，换工具不收（用户 2026-10-26 定）；只在 Esc/关闭/收藏格收。
        // 任何换工具（面板格 / 工具键 / 自检）都要先把呼出盘收掉——盘还在、
        // 工具已经换走，是最容易看出来的状态打架。
        if (RadialPaletteActive) CancelRadialPalette("换工具");
        // 换到截图工具 = 记下"进截图之前用的工具"（8.3.1：Esc 取消时要回到它）。
        // 只记第一次（进来之后 Tool 已经是 Capture，不会再覆盖）。
        if (t == Tool.Capture && Tool != Tool.Capture) _toolBeforeCapture = Tool;
        if (Tool != t && t != Tool.Marquee) ClearSelectionForNewContext();
        // **换到框选工具 = 那个"刚画完"的框从此按常规那一套走**（整条操作条、框里任意一点
        // 都能拖）。理由：`SwitchTool` 对"换到框选"本来就**不清选区**（见上一句），
        // 而"选择"这个工具的名字本身就是在说"我要整理它"——那时还给一颗圆钮、
        // 要老师再点一下才摊开，反而绕（这一路的口径见 `_autoSelCollapsed`）。
        if (t == Tool.Marquee) _autoSelCollapsed = false;
        // **换工具 = 多笔图形作废**（见表 StepPlan，目前只有双曲线两笔）。
        //
        // 两件事不能少：① 状态归零——不清的话，切走再切回来按第一笔，会接着上一轮去改
        // 那条画了一半的曲线，而不是画新的；② **把那条半成品丢掉**——它一直挂在
        // ActiveStroke 上、**不在文档里**，而 ActiveStroke 是无条件参与渲染的
        //（见 Overlay 的 DrawStroke），不丢就会变成"屏幕上留着一条怎么也擦不掉的曲线"
        //（自检里有一条专门数屏幕上那一块的墨）。
        if (_stepPlan != null)
        {
            ActiveStroke = null;
            _dirty = true;
        }
        _stepPlan = null;
        _stepIndex = 0;
        _stepSnap = ShapeSnapKind.None;      // 「直棱柱」那颗胶囊跟着半成品一起作废
        ShapeAnchorSnap.Clear();             // 关键点吸附的小圆环同理（切工具就收）
        // 换工具 = 退出"文档手掌"（2026-11-15：手掌不是工具，点任何工具格 = 我要写字了）。
        // 放在 Tool = t 之前：ExitDocPan 只碰手掌/光标，不看 Tool。
        ExitDocPan("换工具");
        Tool = t;
        // 一次性图形：任何主动换工具都取消它；面板选卡会在 SetShapePresetFromUi 里重新置上。
        _shapeOneShot = false;
        _shapeFromPanel = false;
        if (!IsShapeTool(t)) _lastNonShapeTool = t;
        // **记住"橡皮用的是哪一种形态"**（整笔擦 / 面积擦）：按 Ctrl+E 回来时切回它，
        // 并且下次启动也还在（见 ToolKeyPress 与 Run 里的读取）。
        if (t == Tool.Eraser || t == Tool.PixelEraser)
        {
            _eraserKind = t;
            SetUiPref("eraserKind", t == Tool.PixelEraser ? "pixel" : "whole");
        }

        // **穿透和工具是互斥的**（用户 2026-09-17 定）。
        //
        // 穿透开着的时候点击落到下层程序上，画布根本收不到笔——这时"选中了笔"是个假状态：
        // 按钮亮着、写不出字。所以换工具（点面板也好、按热键也好）等于一句"我要开始用了"，
        // 顺手把穿透关掉。反过来，点面板上那个"鼠标"格是明说要穿透，它单独开。
        // 这条退出**不恢复白板**（restoreBoard: false）：换工具的意思是"我现在就要写"，
        // 画布要保持眼前所见——突然盖回白板反而是惊吓（和"穿透开关"退出区分，见 SetPassThrough）。
        if (PassThrough) SetPassThrough(false);

        // 半路换工具：那条还在写的激光轨迹**当作抬手收尾**（整批开始 2 秒计时）。
        // 不这么做的话这批轨迹永远是"还在写"（计时器停在 -inf），于是既不淡出、
        // 又让 `Laser.Visible` 一直为真 → **每一帧都出一帧**（白烧 CPU）。
        // ⚠ `Release` 自己会判"有没有正在写的"：没有就直接返回，所以这里可以无脑调，
        //   不会把上一批还在淡的激光给"续命"（见 `LaserTrail.Release`）。
        Laser.Release(NowMs);
    }

    /// <summary>收起选区（换工具、以及"与选中无关的新操作"走这里）。</summary>
    private void ClearSelectionForNewContext()
    {
        // 选区没了，面板和它下面挂的自定义取色板也一起收掉——
        // 不收的话"面板关了色板还挂在屏幕上"（见 CloseCustomColor 那段注释）。
        CloseCustomColor();
        SelPanelOpen = SelPanel.None;
        // 选区没了，"画完自动选中那个框还收没收着"也就没有意义了——归零，
        // 免得下一个自动选中的框刚开始就带着上一轮的展开状态（见 `_autoSelCollapsed`）。
        _autoSelCollapsed = false;
        // **停顿成型那一路的"选中"也一起归零**（见 `_dwellSelected`）：它比 `_autoSelCollapsed`
        // 多担一件事——放行"笔下面那个框能不能点"，所以更不能跟着上一轮留到下一轮。
        _dwellSelected = false;
        // 触摸手势选出来的那一路同理（见 `_touchSelected`）。
        _touchSelected = false;
        if (Doc.Selected.Count == 0) return;
        Doc.Selected.Clear();
        _dirty = true;
    }

    internal void CycleWidth()
    {
        switch (Tool)
        {
            case Tool.Highlighter:
                HighlighterWidthIndex = (HighlighterWidthIndex + 1) % HighlighterWidthPresets.Length;
                HighlighterWidthLogical = HighlighterWidthPresets[HighlighterWidthIndex];
                break;
            case Tool.Laser:
                LaserWidthIndex = (LaserWidthIndex + 1) % LaserWidthPresets.Length;
                LaserWidthLogical = LaserWidthPresets[LaserWidthIndex];
                break;
            case Tool.PixelEraser:
                PixelEraserWidthIndex = (PixelEraserWidthIndex + 1) % PixelEraserWidthPresets.Length;
                PixelEraserWidthLogical = PixelEraserWidthPresets[PixelEraserWidthIndex];
                break;
            case Tool.Eraser:
                // 整笔橡皮也有自己的档位。以前它落进 default，按 Ctrl+Alt+6 改的是**笔宽**
                // ——"拿着橡皮调粗细，笔迹变粗了"就是这么来的。
                EraserRadiusIndex = (EraserRadiusIndex + 1) % EraserRadiusPresets.Length;
                EraserRadiusLogical = EraserRadiusPresets[EraserRadiusIndex];
                break;
            default:
                WidthPresetIndex = (WidthPresetIndex + 1) % WidthPresets.Length;
                PenWidthLogical = WidthPresets[WidthPresetIndex];
                break;
        }
        Console.WriteLine($"{Tool} 粗细 -> {CurrentToolWidthLogical} 逻辑像素"
                        + $"（本机实际 {CurrentToolWidthLogical * DpiScale:F0} 物理像素）");
        EraserTelemetry?.Note($"{ToolName(Tool)}粗细 → {CurrentToolWidthLogical:F0} 逻辑像素", NowMs);
        // **记住粗细档**（用户 2026-09-30 定：笔 / 荧光笔 / 激光笔 / 面积擦 分开记）。
        // 2026-10-15 笔键名升到 `w4.pen`（20 档新表）——表的"第几档"语义随换代，
        // 混读会错位；老键只当迁移来源。笔同时写一份"数值"（`wv.pen`）。
        SetUiPref("w4.pen", WidthPresetIndex.ToString());
        SetUiPref("wv.pen", PenWidthLogical.ToString("0.##"));
        SetUiPref("w3.hl", HighlighterWidthIndex.ToString());
        SetUiPref("w3.laser", LaserWidthIndex.ToString());
        SetUiPref("w3.pixel", PixelEraserWidthIndex.ToString());
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 开关穿透。
    ///
    /// `restoreBoard`：退出穿透时，要不要把**进穿透时被顺手关掉的白板**恢复。
    ///   · **穿透开关**退出（面板那一格 / 全局 `Ctrl+Alt+Shift+T`）传 true——
    ///     老师按它的意思是"回到刚才"，所以"板开还是开、关还是关"；
    ///   · **换工具**退出（`SwitchTool` 里的自动关穿透）传 false——那条路的意思是
    ///     "我现在就要写"，画布要保持眼前所见（露出来的下层应用），突然盖回白板反而是惊吓；
    ///     想回板，再点一下白板格就行（用户 2026-09-30 拍板，见《调研-快捷键-焦点与穿透》8.3）。
    /// </summary>
    private void SetPassThrough(bool on, bool restoreBoard = false, bool toPen = false)
    {
        // 进穿透 = 离开"文档模式"的交互（2026-11-15）：手掌先收掉，
        // 免得退回来时是"手里是笔、一动却是拖页"的混合态。
        if (on) ExitDocPan("进穿透");
        // 进穿透先记下板态（要被"开关退出"用来恢复）。
        // 只在"真的从关到开"这一下记——重复调 SetPassThrough(true) 时 BoardOn 已经被关掉了，
        // 再记一次就会把外面的快照覆盖成 false，退出时反而不恢复。
        if (on && !PassThrough)
        {
            _boardBeforePassThrough = BoardOn;
            // 收藏弹窗是引擎自绘的浮动面板，没有独立输入窗；穿透下留着只会
            // “看得见、点不动”，点击还会落到下层 PPT。先藏，退出自动回来。
            HideLibraryForPassThrough();
        }

        // 进穿透 = "键盘/指针都给下层"：开着的呼出盘收掉（和 8.5 工具键不响应同一条语义）。
        if (on && RadialPaletteActive) CancelRadialPalette("进穿透");

        PassThrough = on;
        _dirty = true;                 // 藏 / 恢复收藏弹窗都要立刻重画
        // 穿透 = "键盘让给下层"：放映临时全局键跟着挂/摘（用户 2026-09-30 定，
        // 见 PptHotkeys 那段注释）。不在放映时这一句是空操作。
        SyncPptHotkeys();
        // 穿透打开/关掉时把激光轨迹清掉（原来是把 `Visible` 置假，等价于"立刻全没"）。
        // ⚠ 别只隐藏不清：轨迹会一直留在集合里，`Laser.Visible` 仍为真 →
        //    每一帧都出一帧（白烧 CPU），而且下次一进来它们又冒出来。
        if (!on) Laser.Clear();

        // **穿透和白板互斥**（用户 2026-09-17 问的那条）。两个方向都要挡：
        //   · 开白板 → 关穿透：白板是不透明的一层，穿透是"点击落到下层程序"；
        //     两个一起开着，老师看到的是白板、点到的却是白板下面那个看不见的窗口。
        //   · 开穿透 → 关白板：同上，反过来也一样说不通。
        // 被关掉的白板**开关退出时恢复**（restoreBoard，用户 2026-09-30 拍板）；
        // **换工具退出不恢复**——理由见方法头那两行。
        if (on && BoardOn)
        {
            BoardOn = false;
            Doc.InvalidateAll();
            NotifyUiStateChanged();
        }
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        // 穿透时把指针交还给下层窗口（ApplyCursor 会在穿透模式下自动放手，并作废缓存）；
        // 退出穿透要立刻把属于我们的光标设回来，不必等下一次鼠标移动
        //（这一句是 `force`：改样式刚把光标恢复成箭头，而缓存里的值已经不成立了）。
        ApplyCursor(force: true);

        if (!on)
        {
            // **恢复板态**（只挂"开关退出"）：进穿透前板是开的、现在被我们一起关着 → 开回来。
            // 恢复要跟 SetBoardFromUi 一样整层作废（底色是烘进分块缓存的）。
            if (restoreBoard && _boardBeforePassThrough && !BoardOn)
            {
                BoardOn = true;
                Doc.InvalidateAll();
                NotifyUiStateChanged();
                _dirty = true;
                Console.WriteLine("退出穿透：白板恢复到进穿透之前（开着）");
            }
            _boardBeforePassThrough = false;   // 快照只服务相邻这几次穿透，用完即清

            // **不留"人还在白板槽、板却没开"的混合态**（用户 2026-10-16 报）：
            // 写了板书 → 开穿透 → 经「换工具/呼出盘」这类"我就要写"的路退穿透
            // （restoreBoard=false）→ 板盖不恢复，但人还站在板槽上——屏幕上就是
            // "白板看不见、板槽的墨迹却浮着"，接着写还会**悄悄写进白板槽**。
            // 让过位的板只有两种正当结局：① 穿透开关退出 = 恢复原样（上面那支）；
            // ② 不恢复 = **人退回来处**——和 SetBoardFromUi 关板同一条路（相机按槽记忆）。
            if (!BoardOn && Doc.PageKey == InkDocument.BoardPageKey)
            {
                LeaveBoardSlot();
                Doc.InvalidateAll();
                _dirty = true;
                NotifyUiStateChanged();
                Console.WriteLine("退出穿透：白板未恢复 → 人退回来处（清了'板槽没板'的混合态）");
            }

            // **穿透关掉 = 回到"能批注"的状态 → 必须把键盘/前台要回来**。
            // 用户 2026-09-30 复现的真 bug：穿透开开关关几次之后，Ctrl+P 这些应用内快捷键
            // 就彻底死了——因为穿透期间我们的窗口不是前台（样式里也不让它被激活），
            // 关掉穿透时只恢复了样式、**没人把前台还给我们**，于是按键全被别的窗口收走。
            // `SetKeyboardMode` 里那句 SetForegroundWindow 正是干这个的（幂等，重复调没副作用）。
            // ⚠ 和"退出放映要把前台要回来"是同一类补丁，见 Ppt.ExitPptMode——以后凡是
            //   "从别的状态切回批注态"的地方都要做这一步。
            //
            // 开机自启的后台待命态之前刻意把 KeyboardMode 关了（不抢登录后的前台）；
            // 用户第一次用小球/工具/快捷键退出穿透时，在这里把键盘模式补回来，
            // 并把因为待命而没接的 PPT 联动接上。
            if (StartupMode && !_pptLinkStarted)
            {
                KeyboardMode = true;
                StartPptLink();
            }
            if (_windows.Count > 0) SetKeyboardMode(KeyboardMode);

            // 第一次退出穿透 = 用户开始用了：后台待命的小球展开成完整条。
            // 只做一次，之后用户自己收起就尊重他的选择。
            if (_startupPassive)
            {
                _startupPassive = false;
                if (Ui != null) UiGuard("Activate", () => Ui.Activate());
            }

            // **用户主动退出穿透 = 直接回到画笔**（2026-10-24 用户定）。
            // 以前退出后保留上一次的工具，老师常常接着一写发现自己还在橡皮/荧光笔上；
            // 画笔用得最多，退回来就给笔，省一次点格。
            // 只有"面板穿透格 / 全局 Ctrl+Alt+Shift+T"这两条用户开关路径传 toPen；
            // 换工具、打开文档、教室卡片自动收放这些内部路径不动（各有各的工具语义）。
            if (toPen && Tool != Tool.Pen) SwitchTool(Tool.Pen);
        }

        Console.WriteLine($"pass-through = {on} (mode {PassMode})");
    }


    internal void ApplyPassThroughStyle(OverlayWindow w)
    {
        long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
        const long clear = Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        ex &= ~clear;

        if (PassThrough)
        {
            if (PassMode == PassThroughMode.ExTransparent)
                ex |= Native.WS_EX_TRANSPARENT;
            else if (PassMode == PassThroughMode.LayeredTransparent)
                ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        }

        Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);

        // 改扩展样式会让系统**顺手把光标恢复成类光标（箭头）**——我们那个
        // "已设过就不重设"的缓存这时就成了假事实（见 ApplyCursor 的注释）。
        // 作废它，下一次 ApplyCursor 才会把属于我们的光标真设回去。
        _cursorApplied = IntPtr.Zero;
    }

    /// <summary>
    /// 关窗、注销快捷键、释放设备。子类若要覆写，务必先调用 base。
    /// </summary>
    protected virtual void Shutdown()
    {
        // 托盘图标**必须先删**：NIM_DELETE 需要有效 hwnd（下面那个 foreach 会 Dispose 窗口）。
        _tray?.Dispose();
        _tray = null;

        foreach (var w in _windows)
        {
            Native.KillTimer(w.Hwnd, (IntPtr)1);
            for (int i = 1; i <= _hotkeyActions.Count + 2; i++) Native.UnregisterHotKey(w.Hwnd, i);
            w.Dispose();
        }
        // 只写"改过的"键位和"界面改过的"偏好；都没动过就不碰用户的配置文件。
        FlushSettings();

        // 退出前补一次自动存档（**白板槽**；下一次打开接上）。自检模式不动用户的存档。
        if (BoardFingerprint(Doc.StrokesOf(InkDocument.BoardPageKey)) != _autoSavedBoardFp)
            AutoSaveNow();

        // 文档还开着就退出：阅读位置也补一次（下一会话才能问"上次看到第几页"）。
        if (DocView.IsOpen) SaveDocResumeOnClose();

        // 在途的周期写盘（板书历史 / 自动存档 / PPT 批注）先全部等落完——**顺序不能反**：
        // 反了的话，"后到的旧内容"会把下面这份退出快照盖掉。
        BackgroundSaver.Flush(5000);

        // 板书历史：退出前再补一份（**不受"自动恢复"偏好影响**）。这一份是"关软件那一刻"
        // 的**白板槽**——上面那条"停顿才写"只保证阶段性留存，最后这一段靠它兜底。
        // 退出这份走**同步**（落完才算数）。
        WriteBoardHistory(Doc.StrokesOf(InkDocument.BoardPageKey), sync: true);
        BoardHistory.CloseSession();      // 下一次运行另起一份快照

        if (_uiInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_uiInputHwnd);
            _uiInputHwnd = IntPtr.Zero;
            _uiInputShown = false;
        }
        if (_pptInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_pptInputHwnd);
            _pptInputHwnd = IntPtr.Zero;
            _pptInputShown = false;
        }
        if (_timerInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_timerInputHwnd);
            _timerInputHwnd = IntPtr.Zero;
            _timerInputShown = false;
        }
        if (_rollInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_rollInputHwnd);
            _rollInputHwnd = IntPtr.Zero;
            _rollInputShown = false;
        }

        _windows.Clear();
        s_map.Clear();
        Cursors.DisposeAll();
        Gfx.Shutdown();
        // 手测台：退出时把汇总写出来（逐条数据在每条拖拽结束时就已经落盘了）。
        EraserTelemetry?.Close(Doc, NowMs);
        // D1 亚像素：退出时给一行统计，确认 `--himetric` 到底有没有真的映射上
        // （设备不报 himetric / 拿不到设备矩形时会逐点退回整数像素，不能只看横幅）。
        if (InputPrecision.UseHimetric)
            Console.WriteLine($"输入精度统计: himetric 映射 {InputPrecision.MappedPoints} 点 / "
                              + $"退回整数像素 {InputPrecision.FallbackPoints} 点");
        TouchStats.WriteSummary();
        Loc.DumpMissing();
        Console.WriteLine("shutdown complete");
    }

}
