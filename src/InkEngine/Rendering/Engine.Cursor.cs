// [职责] 光标形状与滚动条交互。
// [测试] --cursortest

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
    //  指针形状（光标）
    //
    //  一条规则：**每次指针移动都要能回答"这个位置、这个工具、这个设备，
    //  用户接下来能做什么"**。回答不了就退到箭头，绝不留空——留空就是
    //  系统兜底，也就是这次修的"永远转圈"。
    //
    //  优先级（上面的压下面的）：
    //    1. 穿透模式   → 不插手，交给下层窗口
    //    2. 触摸       → 不显示指针
    //    3. 拖拽中     → 用按下那一刻的语义（此时系统不再发 WM_SETCURSOR）
    //    4. 滚动条 / 选中框手柄 / 操作条
    //    5. 工具       → 画布上的形状
    // =====================================================================

    internal enum ToolCursorShape
    {
        None,
        /// <summary>圆环：橡皮、手写笔悬停。直接表达"落点范围内会怎样"。</summary>
        Ring,
        /// <summary>
        /// 矩形：像素橡皮。它的落点**就是一个矩形**（竖着的黄金比例），
        /// 所以光标不该画成圆——画成圆等于告诉用户一个错的形状。
        /// </summary>
        Rect,
        /// <summary>
        /// 实心圆盘：荧光笔。落点处就是一个直径 = 笔宽的圆——单击一下留下的
        /// 墨点也是它，所以"光标"和"点下去会得到什么"是同一个形状。
        /// </summary>
        Disc,
        /// <summary>实心点：激光笔。激光表达的是"我说的是这里"，不是"多宽"。</summary>
        Dot,
    }

    private IntPtr _cursorApplied;
    private int _cursorSizePx, _cursorSizeDpi;

    /// <summary>
    /// 系统指针的物理像素尺寸（跟随 DPI 与用户的"指针大小"辅助功能设置）。
    /// 本机 200% 缩放下是 64。自绘光标按它生成，才不会在高分屏上糊掉。
    /// </summary>
    internal int CursorSizePx
    {
        get
        {
            int dpi = Math.Max(96, (int)MathF.Round(96f * DpiScale));
            if (_cursorSizePx == 0 || dpi != _cursorSizeDpi)
            {
                int v = Native.GetSystemMetricsForDpi(Native.SM_CXCURSOR, (uint)Math.Min(dpi, 768));
                _cursorSizePx = v > 0 ? v : (int)(32 * DpiScale);
                _cursorSizeDpi = dpi;
            }
            return _cursorSizePx;
        }
    }

    /// <summary>当前该显示什么光标。纯函数（不碰系统），所以能拿来做自检。</summary>
    internal CursorKind ComputeCursorKind()
    {
        // 吸管：让系统箭头留在屏幕上（老师要知道鼠标在哪）；放大镜固定在左下角，
        // 不再画全屏十字线，也不会因为指针外圈留下残影。
        if (ColorPickActive) return CursorKind.Default;
        // 已拿起收藏、指针在画布上：给箭头（不是画笔），配合虚影表示“这一下是落下”。
        if (LibraryGhostVisibleNow) return CursorKind.Default;
        // 指针停在**界面块**上（接输入小窗 / 图库面板这类自绘界面）：给箭头。
        // 界面上的按钮不该顶着一个笔尖圈/橡皮圈——那一圈是"落点反馈"，
        // 只对画布有意义。
        if (!_drawing && (_uiHover || PointerOnDrawnChrome())) return CursorKind.Default;

        // 回放中一律箭头：画布那一下只是"暂停/继续"，不该顶着笔尖圈/橡皮圈。
        if (_replay != null) return CursorKind.Default;

        if (PassThrough) return CursorKind.Leave;                 // 谁来接管由系统决定
        if (LastPointerType == Native.PT_TOUCH) return CursorKind.Hidden;

        // 文档「手掌」（2026-11-15）：文档里手掌开着 = 拖页 → 系统手型（排在界面/穿透之后）。
        if (DocPanMode && DocView.IsOpen) return CursorKind.Hand;

        if (_drawing)
        {
            // 拖拽中不重新做命中测试：指针早就离开手柄了，重测会让光标在半路变回去。
            // **不再问"是不是框选工具"**：图形工具下也可能正在拖那个画完自动出现的框
            //（见 `OnPointerDown` 里 `AutoSelectionPress` 那一段）。
            if (SelDragging)
                return _dragIsMove ? CursorKind.Move : HandleCursor(_dragHandle);

            // **按在操作条 / 面板 / 圆钮上的那一下：保持箭头**（用户 2026-09-27 报的
            // "悬浮过去是鼠标，点一下它又变成十字了"）。
            //
            // 根因：`OnPointerDown` 在"这一下归谁"分流**之前**就设了 `_drawing = true`
            // 并调了一次 ApplyCursor（见那里的注释），于是按下的那一帧光标落到
            // ToolCursorKind——框选工具下就是十字；等分流走完（点中按钮）已经晚了，
            // 光标那一下的跳变用户看得见。
            //
            // 判据**复用 SelectionCursor**（"一块是界面就是界面"的唯一判据，见它的注释）：
            // 它给 Default 的地方（条 / 面板 / 圆钮）就是界面操作，光标别动——不另列名单。
            // 放过两种情形：框选拖动中（MarqueeActive：框经过条的上方时也该保持十字）
            // 和手柄 / 框内拖动（那种按下当帧就进了 SelDragging，走上面那条）。
            if (!MarqueeActive && SelectionBarShown
                && SelectionCursor(PointerX, PointerY) == CursorKind.Default)
                return CursorKind.Default;

            return ToolCursorKind;
        }

        // 滚动条：不换光标。手型按规范只能表示链接；四向箭头又会被读成"移动/拉伸"。
        // "能拖"这件事交给滑块自己的悬停反馈（变粗、变深）去说。
        if (ScrollBarHover) return CursorKind.Default;

        // 选中框那一套：框选工具下整个框都算；**图形工具下只有框的家具 / 图形自己那条墨算**
        //（别处一按是接着画一笔，光标就该是画的十字——判据在 SelectionInteractiveAt）。
        if (SelectionInteractiveAt(PointerX, PointerY))
        {
            var k = SelectionCursor(PointerX, PointerY);
            if (k.HasValue) return k.Value;      // null = 这一带没有特殊语义，交给工具
        }

        return ToolCursorKind;
    }

    /// <summary>
    /// 指针正落在**引擎自绘的界面块**（图库面板 / PPT 条那一族）上吗。
    ///
    /// 为什么单列成一条判据（2026-09-27 修）：这些浮层都不在"接输入小窗"里，它们自己的
    /// 悬停分支只调 ApplyCursor、**没有任何地方更新"指针在界面上"这个状态**，于是同一块
    /// 面板上的光标取决于来路（从工具条过来是箭头、从画布直入是工具光标）。
    /// 修法不是"顺手把 _uiHover 也置上"（那是接输入小窗的状态，混着用早晚再出错），
    /// 而是把这个几何判据**收成一条**：光标（<see cref="ComputeCursorKind"/>）和
    /// 落点反馈（<see cref="DrawnCursor"/>）都问它——"一块是界面就是界面"，名单只写一处
    /// （教训见 架构-分层与规则.md 五-7）。
    ///
    /// 名单：图库面板（画布坐标）+ PPT 条/长按菜单/页号面板（**物理屏幕坐标**，
    /// 借 `PptBarContains` 那份"穿透豁免与命中"的现成判据——它们本来就是"一块"）。
    /// </summary>
    private bool PointerOnDrawnChrome()
    {
        if (LibraryPanelVisibleNow && LibraryLayout.Contains(LibraryPanelRectNow(), PointerX, PointerY))
            return true;
        var menu = SelectionMenuRectNow();
        if (!menu.IsEmpty && menu.Contains(PointerX, PointerY)) return true;
        // 画布坐标 → 屏幕坐标走相机（阶段 1 = `y + ViewOffsetY`、x 不变）。
        float sx = CanvasToScreenX(PointerX), sy = CanvasToScreenY(PointerY);
        return PptBarContains(sx, sy) || PageEdgeContains(sx, sy) || TimerCardContains(sx, sy)
            || RollCardContains(sx, sy) || (Tool == Tool.Marquee && TextBoxContains(sx, sy));
    }

    /// <summary>
    /// **三支笔（笔 / 荧光笔 / 激光）一律用系统斜笔光标**（硬件光标、零延迟跟随），
    /// 不画自绘落点（笔尖环 / 荧光笔圆盘 / 激光实心点）。
    ///
    /// 2026-10-16：**这是默认**（用户真机 A/B 后拍板"把系统的笔光标当软件内光标"，转正）。
    /// 缘起：手写板被 Windows 报成"屏内笔"时，笔模式走"藏光标＋画环"、和另外两支笔不统一；
    /// 用户试过统一档后定为默认。`--nosyscursor` 退回自绘落点（对照 / 备选）。
    /// 橡皮不在内——它的圆环/方框是"会擦掉多大一块"，是功能不是装饰。
    /// </summary>
    internal static bool SysCursorAll = true;

    /// <summary>
    /// 画布上的工具光标（不含滚动条、操作条、手柄）。
    ///
    /// 问的是 <see cref="EffectiveTool"/>：**笔倒过来拿（笔尾橡皮）时，光标要是橡皮的**
    /// （用户 2026-09-27 报的"笔尾悬停没有指示"）。倒持时上面那行 Tool.Pen 的分支根本走不到，
    /// 直接落到 Tool.Eraser —— 系统光标藏起来、落点由自绘圆环表达（半径见 CursorOuterRadius）。
    /// </summary>
    private CursorKind ToolCursorKind => EffectiveTool switch
    {
        // 笔：**能给斜笔就给斜笔**（鼠标 / 手写板 / 认不出设备的笔）——2026-09-27 加的，
        // 用户点名要 InkClass 那种斜笔：热点在笔尖、不挡视线，且一路跟着落点走
        // （手写板写字时尤其需要，见 PenDeviceOnScreen 的注释）。
        // 用的是**系统那支 IDC_PEN**（自绘彩笔版试过、被用户否掉，见 CursorKind.Pen 的注释）。
        // 默认（SysCursorAll）**不管出身**，一律斜笔；`--nosyscursor` 退回时才分：
        // "笔尖就压在屏幕上"的触屏笔藏起来（笔尖即落点，环由自绘表达）。
        Tool.Pen => (SysCursorAll || PenShowsCursor) ? CursorKind.Pen : CursorKind.Hidden,
        // 荧光笔 / 激光：默认（2026-10-16 用户拍板）**系统斜笔**（硬件光标零延迟）；
        // `--nosyscursor` 退回"藏系统光标 + 自绘落点"（圆盘 / 实心点，带粗细信息）。
        Tool.Highlighter => SysCursorAll ? CursorKind.Pen : CursorKind.Hidden,
        Tool.Laser => SysCursorAll ? CursorKind.Pen : CursorKind.Hidden,
        // 落点由自绘圆环 / 矩形表达；开了 EraserKeepsSystemCursor 就两个都显示（A/B 用）。
        Tool.Eraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        Tool.PixelEraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        // 截图（8.3.1，照微信）：**系统十字全程跟着**，不再自绘取景框角括号；
        // 取景期间另画两条全屏准线（在 Overlay 里，见 DrawCaptureOverlay）。
        // 调整阶段压在手柄上换成系统的缩放箭头——这是 Windows 截图工具的做法，认起来最快。
        Tool.Capture => CaptureCursorKind(),
        // 框选 / 图形仍然用十字准星："从这儿拖到那儿"的通用语言。
        Tool.Text => CursorKind.IBeam,
        Tool.Marquee or Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Arrow
            => CursorKind.Cross,
        // 椭圆（带焦点）：和椭圆一样是"从这儿拖到那儿"的一拖，给十字准星。
        Tool.ConicEllipse => CursorKind.Cross,
        _ => CursorKind.Default,
    };

    /// <summary>
    /// 截图模式下的系统光标（8.3.1）：
    ///   · 待机 / 拖框 = **十字**（微信同款；"从哪儿开始"永远看得见）；
    ///   · 调整阶段压在手柄上 = 对应的**缩放箭头**（和选中框手柄同一套语言）；
    ///   · 压在「✓ 完成 / ✕ 取消 / 右上角 ✕」上 = 普通箭头；
    ///   · 抓屏那一瞬（`CaptureFrameHidden`）由 `ApplyCursor` 那边照旧藏。
    /// </summary>
    private CursorKind CaptureCursorKind()
    {
        if (!CaptureActive) return CursorKind.Cross;          // 只点了格子：先给十字
        if (CaptureAdjusting)
        {
            switch (CaptureHandleAt(PointerX, PointerY))
            {
                case CaptureDrag.L or CaptureDrag.R: return CursorKind.ResizeWE;
                case CaptureDrag.T or CaptureDrag.B: return CursorKind.ResizeNS;
                case CaptureDrag.TL or CaptureDrag.BR: return CursorKind.ResizeNWSE;
                case CaptureDrag.TR or CaptureDrag.BL: return CursorKind.ResizeNESW;
            }
            if (CaptureButtonHit(PointerX, PointerY)) return CursorKind.Default;
        }
        else if (CaptureCancelHit(PointerX, PointerY)) return CursorKind.Default;
        return CursorKind.Cross;
    }

    /// <summary>
    /// 选中框上的光标（含操作条、八个手柄、旋转手柄、框内拖动）。
    /// 返回 null = 指针不在选中框这一带，调用方继续往下问工具。
    ///
    /// 用可空值而不是"返回 Default 表示没有"：Default（箭头）本身也是一个
    /// **明确答案**（比如操作条按钮上就该是箭头），两者混在一起写，
    /// 就会出现"按钮上反而露出手柄光标"这种错。
    /// </summary>
    private CursorKind? SelectionCursor(float canvasX, float canvasY)
    {
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = FurnitureDpi;      // 手柄 / 条 / 面板：屏幕恒定（见 InkEngine.FurnitureDpi）
        var aabb = frame.CanvasAabb;

        // **操作条是"一块"，不只是几个按钮**（用户 2026-09-17 报的："操作对应图标的时候
        // 一会是十字光标，一会是箭头图标"）。
        //
        // 原来只认按钮本体（`BarButtonAt`），按钮之间的分隔线、两头的留白都不算，
        // 于是那些位置上就退回工具光标——框选工具是十字，鼠标在条上横着滑过去就是
        // 箭头／十字／箭头／十字。条在视觉上是一整块白色胶囊，指针落在它上面就该是箭头。
        // 收起态那颗圆钮、以及挂在条下面的小面板（颜色/粗细、层级、导出）同理。
        //
        // ⚠ 这一块由 `SelectionBarShown` 把守（图形工具下只有那个自动选中的框挂着一颗圆钮，
        // 见那里的注释）；画整条还是画圆钮由 `BarDrawnCollapsed` 定。
        if (SelectionBarShown)
        {
            if (BarDrawnCollapsed)
            {
                if (SelectionDotCanvasRect.Contains(canvasX, canvasY))
                    return CursorKind.Default;
            }
            else
            {
                if (CustomColorOpen
                    && SelectionHandles.PickContains(canvasX, canvasY, aabb, dpi, FurnitureViewport,
                                                     SelectionHandles.SwatchCount,
                                                     SingleTextBoxSelection))
                    return CursorKind.Default;
                if (SelectionBarCanvasRect.Contains(canvasX, canvasY))
                    return CursorKind.Default;
                if (SelPanelOpen != SelPanel.None
                    && SelectionHandles.PanelContains(canvasX, canvasY, aabb, dpi, FurnitureViewport,
                                                      SelPanelOpen, SelectionHandles.SwatchCount,
                                                      SingleTextBoxSelection))
                    return CursorKind.Default;
                // AI / `⋯` 弹层也是界面：整块给箭头，不能露出画布工具光标
                //（2026-11-08 用户："这些界面不管点不点都该是鼠标"）。
                var menu = SelectionMenuRectNow();
                if (!menu.IsEmpty && menu.Contains(canvasX, canvasY))
                    return CursorKind.Default;
                // 字体下拉也是界面。
                if (FontListOpen && FontListRectNow().Contains(canvasX, canvasY))
                    return CursorKind.Default;
            }
        }

        var h = SelectionHandles.HitTest(canvasX, canvasY, Doc.Selected, frame, dpi);
        if (h != SelHandle.None) return HandleCursor(h);

        return SelectionHandles.InsideUiFrame(frame, new Vector2(canvasX, canvasY), dpi)
            ? CursorKind.Move : null;
    }

    private static CursorKind HandleCursor(SelHandle h) => h switch
    {
        SelHandle.TopLeft or SelHandle.BottomRight => CursorKind.ResizeNWSE,
        SelHandle.TopRight or SelHandle.BottomLeft => CursorKind.ResizeNESW,
        SelHandle.Left or SelHandle.Right => CursorKind.ResizeWE,
        SelHandle.Top or SelHandle.Bottom => CursorKind.ResizeNS,
        SelHandle.Rotate => CursorKind.Rotate,
        // 端点手柄给十字准星：它是"精确取一个点"，和拉伸（四向箭头）是两种意思。
        SelHandle.EndpointA or SelHandle.EndpointB => CursorKind.Cross,
        // 焦点三角形的顶点 P 同理：它也是"精确取一个点"（同 EndpointA 那条理由）。
        SelHandle.FocusPoint => CursorKind.Cross,
        _ => CursorKind.Default,
    };

    /// <summary>
    /// 把当前该有的光标设上。
    ///
    /// ⚠ **"同一个句柄不重复设"只是一句优化，不能当事实用**（2026-09-27 修）：
    /// 有好几处我们**把光标交出去了**或**让别人改掉了它**——
    ///   · 穿透模式（`CursorKind.Leave`）：指针归下层窗口，它自己会换成箭头；
    ///   · 改窗口扩展样式（`ApplyPassThroughStyle` 的 SWP_FRAMECHANGED）：
    ///     系统会顺手把光标恢复成类光标（箭头）。
    /// 这些时刻之后，`_cursorApplied` 记的还是"我们设过的那一个"，而屏幕上是箭头——
    /// 于是后面所有"设成同一个"的请求全被这句优化跳过，**光标就卡在箭头上不动了**。
    /// 用户 2026-09-27 报的正是这个："切到穿透再切回画笔，还是三角形；
    /// 切荧光笔更明显——箭头和自绘圆盘一起出现；多切几次有时候又能切回来"。
    /// 修法：凡是"放手"或"别人可能改过"的地方，都把 `_cursorApplied` 作废。
    /// </summary>
    internal void ApplyCursor(bool force = false)
    {
        var kind = ComputeCursorKind();
        if (kind == CursorKind.Leave)
        {
            // 不插手：指针归下层窗口。**顺手把缓存作废**——从现在起屏幕上是什么光标
            // 已经不由我们决定，下一次要设的时候必须真设（见上面那段）。
            _cursorApplied = IntPtr.Zero;
            return;
        }
        var h = Cursors.HandleFor(kind, CursorSizePx);
        if (h == IntPtr.Zero) return;
        if (!force && h == _cursorApplied) return;
        Native.SetCursor(h);
        _cursorApplied = h;
    }

    /// <summary>自检用：我们最后一次真的设下去的光标（IntPtr.Zero = 已作废 / 还没设过）。</summary>
    internal IntPtr CursorAppliedForTest => _cursorApplied;

    /// <summary>
    /// 自己要画落点反馈（橡皮圆环、笔尖环、荧光笔圆盘）时，必须把系统光标藏起来，
    /// 否则就是"箭头 + 圆环"叠在一起。触摸不画：手指没有悬停，画了会留在屏幕上。
    /// </summary>
    internal ToolCursorShape DrawnCursor
    {
        get
        {
            // CaptureFrameHidden = "正在抓屏的那一瞬"：取景框和**落点光标**都不画。
            // 光标是琥珀色的，留着它就会被"直接截取"拍进图里（自检抓到过：图片最外一圈
            // 多出 15 个琥珀像素——拖框正好收在角上，光标就压在那一角）。
            //
            // 触摸默认不画（手指没有悬停，画了会留在屏幕上）——**但橡皮例外**：
            // 手指按着擦的时候没有落点反馈就是"看不见橡皮擦"（2026-10-05 用户实测），
            // 所以擦除进行中把鼠标那套落点原样搬过来（形状/尺寸/颜色走同一份代码），
            // 松手（_drawing 落）立刻消失。多指擦会话（TouchMode.Erase）走会话专用反馈，
            // 不在这一条里（它按每个触点画，脏区由 ComputeTransientBounds 管）。
            bool touchEraserCursor = LastPointerType == Native.PT_TOUCH
                                     && _drawing && _touchMode == TouchMode.Write
                                     && (EffectiveTool == Tool.Eraser || EffectiveTool == Tool.PixelEraser);
            // 文档「手掌」：拖页时手里没有笔——不给落点反馈（否则橡皮会顶着一个圈在拖页）。
            if (PassThrough || !PointerInside || CaptureFrameHidden
                || (DocPanMode && DocView.IsOpen)
                || (LastPointerType == Native.PT_TOUCH && !touchEraserCursor))
                return ToolCursorShape.None;

            // 指针停在界面块上（接输入小窗 / 图库面板）：这一圈落点反馈该消失。
            // 不判这一条的话，指针移到面板上之后，覆盖层收不到任何指针消息，
            // 上一帧的圆环会**留在屏幕上不动**——看起来就像卡住了。
            if (_uiHover || PointerOnDrawnChrome()) return ToolCursorShape.None;

            // 规则一句话：**落点离屏幕远的（鼠标 / 手写板）一路画**；
            // **笔尖就压在屏幕上的**（触摸屏自带笔），一落笔就不该再跟一个圈
            // （会把手写的位置挡住，而且笔尖和圈的中心差一两像素时看着像错位）。
            //
            // ⚠ 2026-09-27 修过一次（用户报"手写板写字时光标消失、不流畅"）：
            // 以前这句是"只要 PT_PEN 就算笔尖在屏幕上"——**手写板的笔尖在板子上、
            // 根本不在屏幕里**，于是写字全程屏幕上什么都没有。现在按设备出身分：
            // 只有触摸屏自带的笔（PenDeviceOnScreen）才享受"落笔不画"，
            // 手写板与鼠标一样一路画（区别只是手写板给系统斜笔、不用自绘，见工具分支）。
            bool penTip = LastPointerType == Native.PT_PEN && PenDeviceOnScreen;

            // 问 EffectiveTool：**笔倒过来拿就是橡皮的落点反馈**（半径 = 橡皮半径，
            // 见 CursorOuterRadius）——不然笔尾悬停时画的是"笔尖有多粗"的环，
            // 与它真实会擦掉的那一块对不上（用户 2026-09-27 报的）。
            switch (EffectiveTool)
            {
            case Tool.Eraser:
                // 橡皮反过来：它表达的是"这一块会被擦掉"，擦除中更要看得到。
                return ToolCursorShape.Ring;
            case Tool.PixelEraser:
                // 同理：矩形要一直看得见，擦除中更要说清"这一块正在被擦"。
                return ToolCursorShape.Rect;
            case Tool.Capture:
                // 截图（8.3.1）：**不画自绘落点**——系统十字全程跟着（见 CaptureCursorKind），
                // 屏幕上另画两条全屏准线（在 Overlay.DrawCaptureOverlay 里）。
                // 8.3.0 那个 24×24 角括号被用户点名"比较难用"，整批退场。
                return ToolCursorShape.None;
                case Tool.Pen:
                    // 已经在显示斜笔（鼠标 / 手写板 / 默认档下的所有笔）：落点由它一路表达，
                    // **不再叠自绘环**——"笔上再套一个圈"就是这段开头说的那种叠影。
                    if (SysCursorAll || PenShowsCursor) return ToolCursorShape.None;
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Ring;
                case Tool.Highlighter:
                    if (SysCursorAll) return ToolCursorShape.None;   // 默认：斜笔一路表达落点
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Disc;
                case Tool.Laser:
                    if (SysCursorAll) return ToolCursorShape.None;   // 默认：斜笔一路表达落点
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Dot;
                default:
                    return ToolCursorShape.None;
            }
        }
    }

    /// <summary>自绘落点反馈的半径（像素）：脏区按它扩，不然快速移动会留残影。</summary>
    internal float DrawnCursorRadius
    {
        get
        {
            switch (DrawnCursor)
            {
                case ToolCursorShape.Ring:
                    return CursorOuterRadius * 1.35f + 10f;
                case ToolCursorShape.Disc:
                    // 圆盘：脏区按半径算，再留出描边和抗锯齿的边。
                    return MathF.Max(HighlighterWidthLogical * DpiScale * 0.5f, 2f) + 12f;
                case ToolCursorShape.Rect:
                    // 矩形：按**半对角线**扩，四个角才不会在快速移动时留残影。
                    // 用落点框（拖动中会随速度变大）——按基准算的话，放大的那一圈会留残影。
                    return MathF.Sqrt(PixelEraserCursorHalfWidthPx * PixelEraserCursorHalfWidthPx
                                    + PixelEraserCursorHalfHeightPx * PixelEraserCursorHalfHeightPx) + 12f;
                case ToolCursorShape.Dot:
                    return CursorDotRadius * 1.5f + 10f;
                default:
                    return 0f;
            }
        }
    }

    /// <summary>
    /// 笔/激光落点反馈的**真实**半径（像素）：就是"这一笔有多粗"的一半。
    /// 细笔（1.5 逻辑像素）不能被"看得见的下限"撑掉——那会让人误判笔宽，
    /// 所以下限由外层那个固定尺寸的圈负责（见 Overlay.DrawRingCursor）。
    /// </summary>
    internal float CursorRingTrueRadius => CurrentToolWidthLogical * 0.5f * DpiScale;

    /// <summary>
    /// 激光落点的核心半径（像素）。默认值沿用旧版"芯 5 像素"的观感：
    /// 0.625 这个系数就是让默认 4 逻辑像素 × 2 倍屏 = 5 像素。
    /// 轨迹本身在 `Overlay.DrawLaser` 里按 `WidthLogical` 直接算红带直径；
    /// 这里只给落点圆点用，不再有外围发光半径。
    /// </summary>
    internal float LaserCorePx => MathF.Max(2f, LaserWidthLogical * DpiScale * 0.625f);

    /// <summary>
    /// 激光落点的半径（像素）。取"轨迹头部的粗细"（细激光有个可见下限），
    /// 这样点和它拖出来的尾巴是同一个尺寸，看起来是一支笔而不是"点上加条线"。
    /// </summary>
    internal float CursorDotRadius
        => MathF.Max(LaserCorePx, Cursors.DotMinRadiusLogical * DpiScale);

    /// <summary>
    /// 落点反馈**外圈**的半径（像素）。细笔的真实半径只有 1.5 像素，
    /// 单画那个圈等于没画，所以外面再套一个固定尺寸的圈：
    ///   内圈 = 真实笔宽（1:1，"我写出来就这么粗"）
    ///   外圈 = 最小可见尺寸（"落点在这儿"）
    /// 橡皮本来就只表达范围，只有一个圈。
    /// </summary>
    internal float CursorOuterRadius => EffectiveTool == Tool.Eraser
        ? EraserRadius
        : MathF.Max(CursorRingTrueRadius, Cursors.RingMinRadiusLogical * DpiScale);

    /// <summary>
    /// 订一次"指针离开窗口"的通知。覆盖层以前完全没订过，所以 PointerInside
    /// 永远停在 true——手移开之后落点环会留在屏幕上。
    /// </summary>
    private void TrackPointerLeave(IntPtr hWnd)
    {
        var tme = new Native.TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
            dwFlags = Native.TME_LEAVE,
            hwndTrack = hWnd,
        };
        Native.TrackMouseEvent(ref tme);
    }

    // =====================================================================
    //  滚动条（拖动 + 悬停）
    //
    //  以前只有滚轮能滚：滚动条是**画出来的、拖不动的东西**。光标这一题的
    //  前提是"滑块真的能拖"，所以在这里补上。形状仍然是箭头（理由见调研文档
    //  5.3：手型只能表示链接，四向箭头会被读成缩放/移动）。
    // =====================================================================

    /// <summary>命中宽度（逻辑像素）。滑块只有 4 像素宽，按 4 像素判定等于点不中。</summary>
    private const float ScrollBarGrabLogical = 16f;

    /// <summary>上下夹住相机偏移（滚轮和拖滚动条共用）。</summary>
    internal void ClampViewOffset()
    {
        ViewOffsetY = ClampOffset(ViewOffsetY);
        ViewOffsetX = ClampOffsetX(ViewOffsetX);
    }

    /// <summary>把一个相机偏移夹进合法范围（翻页要"先算目标再决定动不动"，所以抽出来）。</summary>
    private float ClampOffset(float y)
    {
        // 画布底（含"视口下方一屏空白"）× ViewScale 才是屏幕位置；100% 时 = 老式子。
        float lowest = _virtualY + _virtualH - CanvasExtent.MaxY * ViewScaleSafe;
        if (y > 0f) y = 0f;
        if (y < lowest) y = lowest;
        return y;
    }

    // ---- 整屏翻页（"一屏 = 一页"）-----------------------------------------
    //
    // 用户 2026-09-16 选的方案（见 调研-白板翻页.md）：**还是同一张连续画布**，
    // 只是相机**按整屏跳**——上一屏/下一屏各走一个视口高，翻完屏幕上不留半行字。
    //
    // 为什么不做"每页一套笔迹"（InkClass 那种）：它的"页"主要是为了跟 PPT 对齐，
    // 我们暂时不接 PPT；而且它为此付出了"坐标物化 + 按页记偏移"的代价（它的注释写着
    // 切回原页会"位置对不上、滚上去的内容再也滚不回来"）。我们只动相机，没有这些问题。

    private double _camFrom, _camTo, _camStartMs;
    private bool _camAnimating;
    private double _camAnimMs = CamAnimMs;
    private const double CamAnimMs = 167;      // 和界面同一套时长（167ms 是 Windows 的 Direct Entrance）

    /// <summary>
    /// 翻一屏。<paramref name="down"/> = 往下翻（内容上移、偏移变负，和滚轮同一套符号）。
    /// 返回是否真的翻了（已经在顶/底就不动）。
    /// </summary>
    internal bool FlipPage(bool down)
    {
        // 文本编辑中翻页：先把正在编辑的文本框提交，再切相机/切页。
        // 不这么做的话，编辑宿主还挂在上/下一页的文本框上，翻过去以后继续输入会
        // 写到一个“文档里已经不在眼前”的框里，看起来就是 PDF 翻页出问题。
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        if (!BoardOn && DocView.IsOpen) DismissDocResume("翻页");
        float want;
        int docTarget = -1;
        if (BoardOn)
        {
            // 白板有自己的屏页相机：翻页只动白板，不碰底下 PDF / PPT 的页。
            // 箭头按**页顶**走，不按“当前偏移 ± 一屏”走：自由滚到半页时，
            // 上一屏先回本页页顶，再上一屏才退到上一页。
            int cur = Math.Max(1, ScreenIndex);
            float curTop = _virtualY - (cur - 1) * PageHeightCanvas * ViewScaleSafe;
            bool atTop = Math.Abs(ViewOffsetY - curTop) <= PageHeightCanvas * ViewScaleSafe * 0.10f;
            int target = down ? cur + 1 : (atTop ? cur - 1 : cur);
            target = Math.Max(1, target);
            want = ClampOffset(_virtualY - (target - 1) * PageHeightCanvas * ViewScaleSafe);
        }
        else if (DocView.IsOpen)
        {
            // 文档模式：翻页 = **跳到上/下一页的页顶**（"一页一翻"，10-03 文档定的交互）。
            // 相机只有一个数：把目标页顶摆到视口顶 → ViewOffsetY = _virtualY - 页顶。
            var vp = ViewportCanvas;
            int cur = DocView.CurrentIndex(vp.MinY, vp.MaxY);
            if (cur < 0) cur = DocView.IndexAt(vp.MinY + 1f);      // 正好停在页缝里：按顶边算
            int target;
            if (cur < 0)
            {
                target = 0;
            }
            else if (down)
            {
                target = cur + 1;                                   // 往下：直接下一页页顶
            }
            else
            {
                // 往上：先回本页页顶；已经在页顶（差不到 1/10 屏）才去上一页
                float intoPage = vp.MinY - DocView.TopOf(cur);
                target = intoPage > PageHeightCanvas * 0.12f ? cur : cur - 1;
            }
            target = Math.Clamp(target, 0, DocView.Count - 1);
            docTarget = target;
            want = ClampOffset(_virtualY - DocView.TopOf(target) * ViewScaleSafe);
        }
        else
        {
            want = ClampOffset(down ? ViewOffsetY - _virtualH : ViewOffsetY + _virtualH);
        }
        if (Math.Abs(want - ViewOffsetY) < 1f) return false;

        // PDF：低配 1 秒过渡，给扫描版页留渲染时间；目标页先钉住、后台先渲。
        if (docTarget >= 0)
        {
            DocView.PinPage(docTarget);
            _camAnimMs = DocMemory.FlipMs;
        }
        else _camAnimMs = CamAnimMs;

        if (!ClientAreaAnimationOn)
        {
            ViewOffsetY = want;               // 系统关了动画就直接跳终态（老机器上是常事）
            _camAnimating = false;
            _camAnimMs = CamAnimMs;
            if (!BoardOn) DocView.UnpinPage();   // 白板的冻结页不能被翻页动画解钉
            _dirty = true; if (DocView.IsOpen) NoteDocResumeChange();
            return true;
        }
        _camFrom = ViewOffsetY; _camTo = want; _camStartMs = NowMs; _camAnimating = true;
        _dirty = true; if (DocView.IsOpen) NoteDocResumeChange();
        return true;
    }

    /// <summary>
    /// 翻页动画：167ms、快出缓停（和面板的展开同一套曲线）。
    ///
    /// **每帧推进一次**，由主循环调。自检的 `SettleFrames` 也调同一份——
    /// 自检是手动抽帧、不走主循环，少这一句就会量出"翻页返回 true 但相机
    /// 一直停在起点"（`--pagetest` 第一版就是这么自己把自己骗了一次）。
    /// </summary>
    internal void StepCameraAnim()
    {
        if (!_camAnimating) return;
        double t = (NowMs - _camStartMs) / Math.Max(1.0, _camAnimMs);
        if (t >= 1.0)
        {
            ViewOffsetY = (float)_camTo;
            _camAnimating = false;
            _camAnimMs = CamAnimMs;
            if (!BoardOn) DocView.UnpinPage();   // 白板的冻结页不能被翻页动画解钉
        }
        else
        {
            float k = 1f - MathF.Pow(1f - (float)t, 3f);
            ViewOffsetY = (float)(_camFrom + (_camTo - _camFrom) * k);
        }
        _dirty = true; if (!BoardOn && DocView.IsOpen) NoteDocResumeChange();
    }

    /// <summary>当前在第几屏（1 起）。相机偏移除以视口高 + 1。</summary>
    internal int ScreenIndex => (int)Math.Round((_virtualY - ViewOffsetY) / Math.Max(1f, _virtualH)) + 1;

    /// <summary>
    /// 白板自己的页数：内容最远到第几屏，至少当前屏；空白板至少 1 页。
    /// 白板是连续画布，翻到新的一屏再写会自动把总页数长出来。
    /// </summary>
    internal int BoardPageCount
    {
        get
        {
            float h = Math.Max(1f, PageHeightCanvas);
            int last = 1;
            foreach (var s in Doc.Strokes)
            {
                var b = s.PaddedBounds;
                if (b.IsEmpty) continue;
                int p = (int)MathF.Ceiling((b.MaxY - _virtualY) / h);
                if (p > last) last = p;
            }
            return Math.Max(ScreenIndex, last);
        }
    }

    /// <summary>还能不能往上翻（到顶了就不行；往下永远可以——画布下面永远多一屏）。</summary>
    internal bool CanFlipPageUp => ViewOffsetY < -1f;

    /// <summary>
    /// "一页"在**画布坐标**里的高 = 一个视口高；第一页的顶 = 虚拟桌面顶。
    /// 页界线（白板模式下画的那条淡线）按这两个值算，所以它**固定在画布上**、
    /// 不随相机动——能被烘进分块缓存，平时零成本。
    /// </summary>
    internal float PageHeightCanvas => _virtualH / ViewScaleSafe;
    internal float PageTopCanvas => _virtualY;
    /// <summary>底纹的横向锚点（第一页的左边界）——和页界线共用同一个原点。</summary>
    internal float PageLeftCanvas => _virtualX;

    /// <summary>
    /// 系统的"在 Windows 中显示动画"开关（`SPI_GETCLIENTAREAANIMATION`）。
    /// 关掉时所有动效直接跳终态——教室老机器上常关，设计文稿里也把这条列为硬规范。
    /// 取不到就当作"开着"（跟系统默认一致）。
    /// </summary>
    private bool? _animOn;
    internal bool ClientAreaAnimationOn
    {
        get
        {
            if (_animOn == null)
            {
                int v = 1;
                try
                {
                    if (!Native.SystemParametersInfo(Native.SPI_GETCLIENTAREAANIMATION, 0, ref v, 0)) v = 1;
                }
                catch { v = 1; }
                _animOn = v != 0;
            }
            return _animOn.Value;
        }
    }

    private bool TryBeginScrollBarDrag(float screenX, float screenY)
    {
        var w = WindowAt(screenX, screenY);
        if (w == null || !w.TryScrollBar(this, out var sb)) return false;
        if (!sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f)) return false;

        // 点在轨道上（滑块以外）：先把滑块中心挪到指针处，再当成"滑块内拖动"。
        // macOS / Figma 都是这个手感：点哪儿滚到哪儿，比"翻一页"更符合直觉。
        if (screenY < sb.ThumbTop || screenY > sb.ThumbTop + sb.ThumbLen)
        {
            float want = Math.Clamp(screenY - sb.ThumbLen * 0.5f, sb.Top, sb.Top + sb.MaxTravel);
            ApplyScrollBarThumbTop(w, sb, want);
            w.TryScrollBar(this, out sb);
        }

        ScrollBarDragging = true;
        ScrollBarHover = true;
        ScrollBarActiveAtMs = NowMs;
        _scrollWindow = w;
        _scrollGrabDy = screenY - sb.ThumbTop;
        return true;
    }

    private void UpdateScrollBarDrag(float screenY)
    {
        if (_scrollWindow == null) return;
        if (!_scrollWindow.TryScrollBar(this, out var sb)) return;
        float want = Math.Clamp(screenY - _scrollGrabDy, sb.Top, sb.Top + sb.MaxTravel);
        ApplyScrollBarThumbTop(_scrollWindow, sb, want);
    }

    private void EndScrollBarDrag()
    {
        ScrollBarDragging = false;
        _scrollWindow = null;
    }

    /// <summary>滑块顶边 → 相机偏移。绘制时那段换算的逆运算（见 Overlay.DrawScrollBar）。</summary>
    private void ApplyScrollBarThumbTop(OverlayWindow w, in OverlayWindow.ScrollBarLayout sb, float thumbTop)
    {
        if (sb.MaxTravel <= 0.001f) return;
        float frac = Math.Clamp((thumbTop - sb.Top) / sb.MaxTravel, 0f, 1f);
        float viewTop = sb.ExtentMinY + frac * (sb.ExtentH - w.Height);
        ViewOffsetY = w.OriginY - viewTop * (w.ViewScale > 0f ? w.ViewScale : 1f);
        ClampViewOffset();
        ScrollBarActiveAtMs = NowMs;
        _dirty = true;
    }

    /// <summary>指针是否停在滚动条上。停在上面就不该淡出，所以每次移动和每次心跳都要问。</summary>
    internal void UpdateScrollBarHover(float screenX, float screenY)
    {
        if (!ScrollBarEnabled)
        {
            // 停用后把残留的悬停状态清干净（光标 / 绘制都读它）。
            if (ScrollBarHover) { ScrollBarHover = false; _dirty = true; }
            return;
        }
        bool hover = false;
        if (!PassThrough && !ScrollBarDragging)
        {
            var w = WindowAt(screenX, screenY);
            if (w != null && w.TryScrollBar(this, out var sb))
                hover = sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f);
        }

        if (hover != ScrollBarHover)
        {
            ScrollBarHover = hover;
            _dirty = true;                      // 滑块要从 4px 变到 9px
        }
        if (hover || ScrollBarDragging) ScrollBarActiveAtMs = NowMs;
    }

    /// <summary>
    /// 心跳（250ms 一次）里按鼠标位置刷新滚动条悬停。没有这一步，指针停在滑块上
    /// 不动时没人通知，滑块会自己淡出——用户会觉得"这玩意儿点不中"。
    /// </summary>
    private void UpdateScrollBarHoverFromCursor()
    {
        if (PassThrough || ScrollBarDragging) return;
        if (!Native.GetCursorPos(out var p)) return;
        UpdateScrollBarHover(p.X, p.Y);
    }

    /// <summary>找出包含某个屏幕坐标的覆盖窗口（多屏时用）。</summary>
    private OverlayWindow WindowAt(float screenX, float screenY)
    {
        foreach (var w in _windows)
        {
            if (screenX >= w.OriginX && screenX < w.OriginX + w.Width &&
                screenY >= w.OriginY && screenY < w.OriginY + w.Height)
                return w;
        }
        return _windows.Count > 0 ? _windows[0] : null;
    }

}
