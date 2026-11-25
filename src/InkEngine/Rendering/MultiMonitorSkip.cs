// [职责] 多屏按窗跳帧：这一帧某扇覆盖窗口要不要 RenderFrame+Present 的纯判定、帧级事实收集、每窗一帧记忆。
// [测试] --unittest MultiMonitorSkip

using System;
using System.Runtime.CompilerServices;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 一帧里"这扇窗口要不要画"的全部输入。**纯数据**——引擎只负责把事实填进来，
/// 判定本身在 <see cref="WindowSkipDecision"/>，单元测试能直接钉住（<c>--unittest MultiMonitorSkip</c>）。
/// </summary>
internal record struct WindowFrameInputs
{
    /// <summary>这一帧一共几个覆盖窗口（单屏恒为 1：永远画）。</summary>
    public int WindowCount;

    /// <summary>自检模式：单帧截图必须拍到完整画面，**任何跳帧都不许发生**。</summary>
    public bool SelfCheckMode;

    /// <summary>
    /// 这一帧界面（工具栏 / 弹层）会不会落在这扇窗里。界面每帧都在动（悬停、动画），
    /// 落在哪扇屏上，哪扇屏就必须照常画。
    /// </summary>
    public bool HasUiInWindow;

    /// <summary>文档整层作废（<c>Dirty.Full</c>）：所有窗口都得重铺。</summary>
    public bool HasFullDirty;

    /// <summary>这扇窗上一帧的分帧预算没烘完，还有可见脏块欠着。</summary>
    public bool HasPendingTiles;

    /// <summary>这扇窗还欠着几帧整屏重画（相机 / 倍率 / 底色变化的收尾）。</summary>
    public int FullFramesLeft;

    /// <summary>相机 / 分块倍率 / 底色 / 垫白 / 页让位 / 冻结页 之一和上一帧对不上。</summary>
    public bool CameraOrThemeChanged;

    /// <summary>文档页层（PDF / 图片）有新脏区、整层作废，或高清补丁有变化——**每窗各自**烘进分块。</summary>
    public bool DocPagesChanged;

    /// <summary>指针这一刻落在这一窗的可见画布上。</summary>
    public bool PointerOnWindow;

    /// <summary>指针上一帧落在这一窗上（刚移走的那一帧还要把光标擦干净）。</summary>
    public bool PointerWasOnWindow;

    /// <summary>正在写的那一笔（湿墨）的包围盒和这一窗的可见画布相交。</summary>
    public bool ActiveStrokeIntersects;

    /// <summary>本帧文档改动（脏区 ＋ 新笔）的并集和这一窗的可见画布相交。</summary>
    public bool DocDeltaIntersects;

    /// <summary>有全局浮层（截图 / 吸管 / 卡片 / 回放条 / 选中 / 呼出盘 / 激光…）：任何窗口都可能要动。</summary>
    public bool HasGlobalOverlay;

    /// <summary>这扇窗挂着一笔"等 Present 之后撤轨迹"的收尾（`--syswet` 路径）：跳帧会让它永远挂着。</summary>
    public bool HasPendingTrailEnd;

    /// <summary>
    /// **上一帧**这扇窗画过浮动层（光标 / 湿墨 / 预测尾 / 滚动条）。指针一移走，
    /// 还得再画一帧把那些像素擦掉，之后才允许跳过——否则副屏上会留一个"死光标"。
    /// </summary>
    public bool HadDynamicOverlayLastFrame;

    /// <summary>
    /// 这扇窗的后缓冲里还躺着"上一/上上帧画过、但这一帧不再画"的像素（浮动层矩形或界面矩形）。
    /// 双缓冲决定了一条残留要两帧才擦干净，所以它为真时**必须继续画**。
    /// </summary>
    public bool HasStalePixels;

    /// <summary>**这一帧**这扇窗会不会画浮动层（渲染之后由引擎写回记忆）。</summary>
    public bool HasDynamicOverlayNow;
}

/// <summary>
/// **多屏按窗跳帧**的纯判定（2026-11-25）。
///
/// 为什么要它：双屏扩展模式下主屏写字时，副屏**一个像素都不会变**，但老代码每帧
/// 无条件对每扇窗 <c>RenderFrame</c> + <c>Present</c>——60~120 次/秒的纯浪费
/// （CPU 提交 + GPU 合成各一份）。这里把"这扇屏这一帧到底有没有事"收成一组布尔事实，
/// **全部为假**才允许跳过：多屏、非自检、无界面、无指针、无湿墨、无脏区、
/// 无相机/底色/页变化、无全局浮层、上一帧也没画过浮动层。
///
/// 每一条"必须画"都是一个真实的残影来源，删任一条都会在副屏上留东西：
///   · 上一帧画过光标/湿墨 → 这一帧必须再画一次（<see cref="WindowFrameInputs.HadDynamicOverlayLastFrame"/>）；
///   · 相机 / 分块倍率 / 底色变了 → 必须画（否则副屏停在旧位置、旧底色上）；
///   · 文档改动的包围盒碰到了这扇屏 → 必须画（跳过会把变更丢掉）。
/// </summary>
internal static class WindowSkipDecision
{
    /// <summary>这扇窗这一帧要不要 <c>RenderFrame</c> + <c>Present</c>。纯函数，同输入同输出。</summary>
    internal static bool ShouldRenderWindow(in WindowFrameInputs f)
    {
        // 单屏 / 自检：行为一个像素都不变（自检是单帧截图，跳帧会拍到半张画面）。
        if (f.WindowCount <= 1) return true;
        if (f.SelfCheckMode) return true;

        if (f.HasUiInWindow) return true;
        if (f.HasFullDirty) return true;
        if (f.HasPendingTiles) return true;
        if (f.FullFramesLeft > 0) return true;
        if (f.CameraOrThemeChanged) return true;
        if (f.DocPagesChanged) return true;
        if (f.HasGlobalOverlay) return true;
        if (f.HasPendingTrailEnd) return true;   // 轨迹收尾要等这一帧 Present

        // 指针：现在在 / 上一帧在（擦旧光标）。
        if (f.PointerOnWindow || f.PointerWasOnWindow) return true;
        if (f.ActiveStrokeIntersects) return true;
        if (f.DocDeltaIntersects) return true;

        // **最关键的防残影**：上一帧画过浮动层，或后缓冲里还躺着旧像素，这一帧哪怕别的
        // 都没变，也要再画一帧把它们擦掉。少了这一句，副屏上会留一个永远不消失的旧光标。
        if (f.HadDynamicOverlayLastFrame) return true;
        if (f.HasStalePixels) return true;

        return false;
    }
}

/// <summary>
/// **帧级事实**（各窗口共用）：一次算好，逐窗只做矩形相交。判定之外的一切都在这里，<see cref="WindowSkipDecision"/> 保持纯函数。
/// </summary>
internal struct SkipScene
{
    public bool Valid;
    public int WindowCount;
    public bool SelfCheckMode;
    public bool HasFullDirty;
    public bool DocPagesChanged;

    /// <summary>整屏级浮层（截图 / 吸管 / 反馈码 / HUD / 轻提示…）：任何窗口都得画。</summary>
    public bool HasGlobalOverlay;

    /// <summary>本帧文档改动（脏区 ＋ 新笔）的并集，画布坐标。</summary>
    public RectF DocDelta;
    public bool HasActiveStroke;
    public RectF ActiveStrokeBounds;

    /// <summary>画布坐标的浮层并集（选中框 / 激光 / 图库面板 / 框选 / 吸附…）。逐窗和可见画布相交。</summary>
    public RectF CanvasOverlay;

    /// <summary>屏幕坐标的浮层并集（页码条 / 回放条 / 计时卡 / 点名卡 / 恢复卡）。逐窗和窗口屏幕矩形相交。</summary>
    public RectF ScreenOverlay;

    /// <summary>界面的**逻辑**矩形（逻辑屏幕坐标，即 UiQueryBoundsNow 的口径）；空 = 界面这一帧不画。</summary>
    public RectF UiLogicalRect;

    public bool PointerInside;
    public float PointerX, PointerY;
}

/// <summary>
/// 把"帧级事实 ＋ 这扇窗的上一帧记忆"拼成 <see cref="WindowFrameInputs"/>，并在渲染之后写回记忆。
/// 判定本身不在这里（见 <see cref="WindowSkipDecision"/>），所以测试可以只验判定、也可以走完整状态迁移。
/// </summary>
internal static class WindowSkipScheduler
{
    /// <summary>每帧开头收一次帧级事实。单屏 / 自检时 <see cref="SkipScene.Valid"/> 为 false，调用方走老路径。</summary>
    internal static SkipScene BuildScene(InkEngine app)
    {
        int n = app._windows.Count;
        var s = new SkipScene
        {
            WindowCount = n,
            SelfCheckMode = app.SelfCheckMode,
            PointerInside = app.PointerInside,
            PointerX = app.PointerX,
            PointerY = app.PointerY,
        };
        if (n <= 1 || app.SelfCheckMode) return s;   // Valid 保持 false：调用方原样全画

        var doc = app.RenderDoc;
        s.Valid = true;
        s.HasFullDirty = doc.Dirty.Full;

        // 文档改动 = 脏区 ∪ 新笔（两者都是画布坐标）。作废整层时不用算子集。
        if (!s.HasFullDirty)
        {
            var delta = RectF.Empty;
            foreach (var r in doc.Dirty.Rects) delta.Add(r);
            foreach (var st in doc.AppendedSinceRender) delta.Add(st.PaddedBounds);
            s.DocDelta = delta;
        }

        s.HasActiveStroke = app.ActiveStroke != null;
        if (s.HasActiveStroke) s.ActiveStrokeBounds = app.ActiveStroke.PaddedBounds;

        // 页层 / 高清补丁：这两样都是"每窗各自烘进分块"，只有渲染的那扇窗会消费它们的脏区，
        // 所以只要有变化就**所有窗口都画**（否则副屏那一张永远停在旧页 / 旧补丁上）。
        var pages = app.DocView;
        bool pagesPending = pages.FullDirty || pages.DirtyRects.Count > 0
                            || pages.Version != SkipFrameState.Host.PagesVersionBaseline;
        bool patchPending = app.DocPatch is { } patch
                            && (patch.WantsFrame || patch.DirtyRects.Count > 0);
        s.DocPagesChanged = pagesPending || patchPending;

        s.HasGlobalOverlay = HasScreenWideOverlay(app);
        s.CanvasOverlay = CanvasOverlayBounds(app);
        s.ScreenOverlay = ScreenOverlayBounds(app);
        s.UiLogicalRect = UiLogicalRect(app);
        return s;
    }

    /// <summary>
    /// **整屏级**浮层：绘制时本身铺满整个窗口，或者 HUD 这种每扇窗各自的固定块。
    /// 这些出现在哪扇屏上都一样，干脆所有窗口都画（都不常见，成本可控）。
    /// </summary>
    private static bool HasScreenWideOverlay(InkEngine app)
    {
        if (app.CaptureActive || app.ColorPickActive || app.FeedbackQrVisible) return true;
        if (app.CaptureTransparentFrame || app.NoContentCache) return true;   // 抓屏 / 调试路径：每扇窗都要走
        if (app.ShowHud || app.EraserHud || app.TouchHud) return true;
        if (app.ToastVisible) return true;              // 轻提示画在窗口坐标里（每扇窗都有）
        if (app.TooltipShown || app.TooltipPending) return true;   // 提示卡贴着浮层，按屏算太细
        if (app.TextBoxEditing) return true;
        // 呼出盘 / 图库面板 / 框选 / 吸附提示 / 滚动条这些都是"跟着指针/时间跑"的，且
        // 几何上可能跨屏；不全画就得逐窗算它们的包围盒，收益不值那个风险。
        if (app.RadialPaletteActive || app.LibraryPanelVisibleNow || app.LibraryGhostVisibleNow) return true;
        if (app.LibraryFeedbackActive || app.MarqueeActive || app.AlignSnapPulseActive) return true;
        if (app.TouchEraseSessionActive) return true;
        if (app.Laser.Visible || app.Laser.NeedsClearFrame) return true;
        if (app.PredictTailActive) return true;
        if (app.DragPreviewActive) return true;
        // 回放：正在"长"的那条前缀画在画布坐标里，可能落在那扇不动的屏上；
        // 同时 TickReplay 是每窗各调一次的，跳帧会让两扇屏演到不同的位置。
        if (app.ReplayActive) return true;
        return false;
    }

    /// <summary>
    /// **画布坐标**的浮层并集（选中框 / 选中操作条 / 跳动的高亮）。逐窗和可见画布相交判断——
    /// 选中框在主屏上时，副屏不用为它重画。
    /// </summary>
    private static RectF CanvasOverlayBounds(InkEngine app)
    {
        var r = RectF.Empty;
        // 关键点吸附的小圆环（2026-11-25 阶段 2）：**画布坐标、跟着指针**，逐窗判相交即可。
        // （"关掉那一帧还要再擦一次"由 HadDynamicOverlayLastFrame 兜，见 HasDynamicOverlay。）
        if (ShapeAnchorSnap.Active)
        {
            float d = 10f * app.FurnitureDpi;
            var p = ShapeAnchorSnap.Point;
            r.Add(p.X - d, p.Y - d);
            r.Add(p.X + d, p.Y + d);
        }
        if (app.Doc.Selected.Count == 0) return r;
        var aabb = app.LiveSelectionFrame.CanvasAabb;
        if (aabb.IsEmpty) return r;
        // 外扩量取"手柄 ＋ 操作条 ＋ 面板 ＋ 投影"的量级：宁可多画一扇窗，也别把操作条切掉一半。
        float pad = 360f * app.UiUnit;
        r.Add(aabb.Inflate(pad));      // 并进去，不是覆盖（否则吸附环被丢掉）
        return r;
    }

    /// <summary>**屏幕坐标**的浮层并集（页码条 / 回放条 / 计时卡 / 点名卡 / 文档恢复卡）。</summary>
    private static RectF ScreenOverlayBounds(InkEngine app)
    {
        var r = RectF.Empty;
        if (app.PageBarVisible) r.Add(app.PptBarRect());
        if (app.ReplayActive) r.Add(app.ReplayBarRect());
        if (app.TimerCardOpen) r.Add(app.TimerCardRect());
        if (app.RollCardOpen) r.Add(app.RollCardRect());
        if (app.DocResumeVisible && app.DocView.IsOpen) r.Add(app.DocResumeCardRect());
        return r;
    }

    /// <summary>界面这一帧的逻辑矩形（`UiQueryBoundsNow` 口径）；空 = 界面不画。</summary>
    private static RectF UiLogicalRect(InkEngine app)
        => app.UiVisibleNow ? app.UiQueryBoundsNow() : RectF.Empty;

    /// <summary>填这扇窗这一帧的输入（读上一帧记忆，不改）。</summary>
    internal static WindowFrameInputs Build(OverlayWindow w, InkEngine app, in SkipScene scene)
    {
        var mem = WindowRenderMemory.For(w);
        var vis = w.VisibleCanvasRect;
        bool globalOverlay = scene.HasGlobalOverlay
            || (!scene.CanvasOverlay.IsEmpty && scene.CanvasOverlay.Intersects(vis))
            || ScreenTouches(w, scene.ScreenOverlay);
        // 界面的逻辑矩形 → 这扇窗的物理像素（和 `PrepareUi` 同一条换算：Dpi/96 × UiScale）。
        RectF uiPhys = RectF.Empty;
        if (!scene.UiLogicalRect.IsEmpty)
        {
            float k = w.Dpi / 96f * app.UiScale;
            uiPhys = new RectF
            {
                MinX = scene.UiLogicalRect.MinX * k, MinY = scene.UiLogicalRect.MinY * k,
                MaxX = scene.UiLogicalRect.MaxX * k, MaxY = scene.UiLogicalRect.MaxY * k,
            };
        }
        var f = new WindowFrameInputs
        {
            WindowCount = scene.WindowCount,
            SelfCheckMode = scene.SelfCheckMode,
            HasUiInWindow = ScreenTouches(w, uiPhys),
            HasFullDirty = scene.HasFullDirty,
            HasPendingTiles = w.HasPendingDirtyTiles,
            FullFramesLeft = w.FullFramesLeftForSkip,
            CameraOrThemeChanged = CameraOrThemeChanged(w, app, mem),
            DocPagesChanged = scene.DocPagesChanged,
            HasGlobalOverlay = globalOverlay,
            HasPendingTrailEnd = w.TrailEndPending,
            PointerOnWindow = scene.PointerInside && vis.Contains(scene.PointerX, scene.PointerY),
            PointerWasOnWindow = mem.PointerWasOnWindow,
            ActiveStrokeIntersects = scene.HasActiveStroke && scene.ActiveStrokeBounds.Intersects(vis),
            DocDeltaIntersects = !scene.HasFullDirty && scene.DocDelta.Intersects(vis),
            HadDynamicOverlayLastFrame = mem.HadDynamicOverlayLastFrame,
            HasStalePixels = w.HasStaleOverlayRects || w.HasStaleUiPixels,
            HasDynamicOverlayNow = HasDynamicOverlay(w, app, scene, vis),
        };
        return f;
    }

    /// <summary>渲染 / 跳帧之后把"这一帧的事实"记成下一帧的"上一帧"（相机基准也在这里推进）。</summary>
    internal static void Commit(OverlayWindow w, InkEngine app, in SkipScene scene, bool rendered)
    {
        var mem = WindowRenderMemory.For(w);
        // 只有真的画了才算"画过浮动层"：跳过的那一帧屏幕上什么都没动。
        mem.HadDynamicOverlayLastFrame = rendered
            && HasDynamicOverlay(w, app, scene, w.VisibleCanvasRect);
        mem.PointerWasOnWindow = app.PointerInside
                                 && w.VisibleCanvasRect.Contains(app.PointerX, app.PointerY);
        // 相机 / 底色基准：只在这扇窗真的画过之后才推进——跳过的帧它的分块没按新相机合成过。
        if (!rendered) return;
        mem.LastViewOffsetX = w.ViewOffsetX;
        mem.LastViewOffsetY = w.ViewOffsetY;
        mem.LastViewScale = w.ViewScale;
        mem.LastBoardOn = app.BoardOn;
        mem.LastBoardColor = app.BoardColor;
        mem.LastPaper = !app.BoardOn && app.DocView.IsOpen && !app.PassThrough;
        mem.LastDocHidden = app.PassThrough || (app.BoardOn && app.BoardOpacity >= 0.999f);
        mem.LastFrozenDoc = app.BoardFrozenDocActive;
        mem.HasBaseline = true;
    }

    /// <summary>这扇窗会不会画"跟着指针 / 时间跑"的东西（画过就必须下一帧再擦一次）。</summary>
    private static bool HasDynamicOverlay(OverlayWindow w, InkEngine app, in SkipScene scene, in RectF vis)
    {
        bool pointerHere = scene.PointerInside && vis.Contains(scene.PointerX, scene.PointerY);
        if (pointerHere && app.DrawnCursor != InkEngine.ToolCursorShape.None) return true;
        if (pointerHere && app.PredictTailActive) return true;
        // 关键点吸附的小圆环跟着指针每帧换位置 → 画过就必须再擦一帧（同光标圈）。
        // 按**这一扇屏**判：环在别的屏上时，这扇窗不用陪着重画。
        if (ShapeAnchorSnap.Active
            && vis.Contains(ShapeAnchorSnap.Point.X, ShapeAnchorSnap.Point.Y)) return true;
        if (app.TouchEraseSessionActive) return true;
        if (scene.HasActiveStroke && scene.ActiveStrokeBounds.Intersects(vis)) return true;
        // 滚动条：悬停 / 拖动 / 3.5 秒淡出窗口内都在变（淡出结束才停）。
        if ((app.ScrollBarHover || app.ScrollBarDragging
             || (app.NowMs - app.ScrollBarActiveAtMs) / 1000.0 <= 3.5)
            && w.HasScrollBarForSkip(app)) return true;
        return false;
    }

    /// <summary>屏幕矩形 <paramref name="screenRect"/>（虚拟桌面坐标）碰不碰这扇窗的物理范围。</summary>
    private static bool ScreenTouches(OverlayWindow w, in RectF screenRect)
    {
        if (screenRect.IsEmpty) return false;
        return screenRect.MinX < w.OriginX + w.Width && screenRect.MaxX > w.OriginX
            && screenRect.MinY < w.OriginY + w.Height && screenRect.MaxY > w.OriginY;
    }

    /// <summary>
    /// 相机 / 分块倍率 / 底色 / 垫白 / 页让位 / 冻结页 有没有和上一帧不一样。
    /// 和 <c>SyncTiles</c> 自己的 <c>_last*</c> 判据同口径（那边负责真正重铺，这边只负责"这帧要不要画"）。
    /// </summary>
    private static bool CameraOrThemeChanged(OverlayWindow w, InkEngine app, WindowRenderMemory mem)
    {
        if (!mem.HasBaseline) return true;      // 首帧 / 新建窗口：一定画一次并把基准立起来
        if (!(MathF.Abs(w.ViewOffsetX - mem.LastViewOffsetX) <= 0.001f)) return true;
        if (!(MathF.Abs(w.ViewOffsetY - mem.LastViewOffsetY) <= 0.001f)) return true;
        if (!(MathF.Abs(w.ViewScale - mem.LastViewScale) <= 0.0005f)) return true;
        if (w.TileScalePending) return true;    // 停手后"换分块倍率"是一个待办，要有人去应用
        if (app.BoardOn != mem.LastBoardOn) return true;
        if (!app.BoardColor.Equals(mem.LastBoardColor)) return true;
        if ((!app.BoardOn && app.DocView.IsOpen && !app.PassThrough) != mem.LastPaper) return true;
        if ((app.PassThrough || (app.BoardOn && app.BoardOpacity >= 0.999f)) != mem.LastDocHidden) return true;
        if (app.BoardFrozenDocActive != mem.LastFrozenDoc) return true;
        return false;
    }
}

/// <summary>
/// 跳帧判定的**进程级基准**（页层版本水位）。单例放在类自己身上，
/// **不给 Engine 新增私有字段**（ADR-005 D3）。
///
/// 为什么需要：<see cref="DocPages.Version"/> 会在渲染途中（后台页就绪 / 直通生成）上涨，
/// 而消费它的是"这一次真的渲染了的那扇窗"。用「帧首版本」做基准，就能保证
/// **下一帧所有窗口都重画一次**，把新页图铺到副屏上——晚一帧，但一帧都不漏。
/// </summary>
internal sealed class SkipFrameState
{
    /// <summary>主循环共用的实例。</summary>
    internal static readonly SkipFrameState Host = new();

    /// <summary>上一帧帧首的页层版本（-1 = 还没见过）。</summary>
    internal int PagesVersionBaseline = -1;

    /// <summary>换文档 / 关文档：把基准作废，下一帧一律全画。</summary>
    internal void Invalidate() => PagesVersionBaseline = -1;
}

/// <summary>
/// 单扇覆盖窗口的"上一帧"记忆（跳帧判定的唯一状态）。
/// 挂在 <see cref="ConditionalWeakTable{TKey,TValue}"/> 上按窗口索引：窗口释放了记忆跟着回收，
/// 不占 Engine / Overlay 的私有字段预算（ADR-005 D3）。
/// </summary>
internal sealed class WindowRenderMemory
{
    private static readonly ConditionalWeakTable<OverlayWindow, WindowRenderMemory> s_map = new();

    /// <summary>上一帧这扇窗画过浮动层（光标 / 湿墨 / 预测尾 / 滚动条）——见判定里的防残影那一条。</summary>
    internal bool HadDynamicOverlayLastFrame;

    /// <summary>上一帧指针在这扇窗上。</summary>
    internal bool PointerWasOnWindow;

    /// <summary>相机 / 底色基准立起来过没有（首帧比较要一律判"变了"）。</summary>
    internal bool HasBaseline;
    internal float LastViewOffsetX, LastViewOffsetY;
    internal float LastViewScale = 1f;
    internal bool LastBoardOn;
    internal Color4 LastBoardColor;
    internal bool LastPaper;
    internal bool LastDocHidden;
    internal bool LastFrozenDoc;

    /// <summary>取这扇窗的记忆（没有就建一份）。</summary>
    internal static WindowRenderMemory For(OverlayWindow w)
        => s_map.GetValue(w, _ => new WindowRenderMemory());
}
