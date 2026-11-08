namespace InkEngine;

public partial class InkEngine
{
    private OverlayLayerManager _overlayLayers;

    /// <summary>
    /// 浮层注册表：library/replay/timer/roll/ppt 的按下、捕获、移动、松手都由
    /// OverlayLayerManager 路由（A4.8 起旧捕获分支与影子路由已清理）；绘制仍留在 Overlay。
    /// </summary>
    internal OverlayLayerManager OverlayLayers
    {
        get
        {
            EnsureOverlayLayers();
            return _overlayLayers;
        }
    }

    private void EnsureOverlayLayers()
    {
        if (_overlayLayers != null) return;
        var m = new OverlayLayerManager();

        m.Register(new LegacyOverlayLayer(
            "library", 800,
            visible: () => LibraryPanelVisibleNow,
            bounds: LibraryLayerBounds,
            hit: LibraryLayerHit,
            down: (x, y, id, type) => LibraryPanelVisibleNow && LibraryPointerDown(x, y, type, id),
            move: (x, y, id) =>
            {
                if (!LibraryPanelVisibleNow) return false;
                if (LibraryPanelPointerMove(x, y, id)) return true;
                return LibraryPanelDragMove(x, y);
            },
            up: (x, y, id) =>
            {
                bool handled = LibraryPanelVisibleNow && LibraryPanelPointerUp(x, y, id);
                if (_libraryDrag != 0)
                {
                    LibraryPanelDragEnd();
                    Native.ReleaseCapture();
                    handled = true;
                }
                return handled;
            },
            dismiss: CloseLibraryPanel,
            dismissPolicy: OverlayDismissPolicy.Escape,
            blocksInputBelow: false));

        m.Register(new LegacyOverlayLayer(
            "replay", 700,
            visible: () => ReplayActive,
            bounds: ReplayBarRect,
            hit: (x, y) => ReplayActive && ReplayBarRect().Contains(x, y),
            down: (x, y, id, type) => ReplayActive && ReplayPointerDown(x, y),
            move: (x, y, id) =>
            {
                if (!ReplayActive) return false;
                ReplayPointerMove(x, y);
                return ReplayScrubbing;
            },
            up: (x, y, id) =>
            {
                if (!ReplayActive) return false;
                ReplayPointerUp(x, y);
                return true;
            },
            dismiss: StopReplayFromUi,
            dismissPolicy: OverlayDismissPolicy.Escape));

        m.Register(new LegacyOverlayLayer(
            "timer", 600,
            visible: () => TimerCardOpen,
            bounds: TimerCardRect,
            hit: (x, y) => TimerCardOpen && TimerCardRect().Contains(x, y),
            down: (x, y, id, type) => TimerCardOpen && TimerCardPointerDown(x, y),
            move: (x, y, id) => TimerCardOpen && TimerCardPointerMove(x, y),
            up: (x, y, id) =>
            {
                if (!TimerCardOpen) return false;
                TimerCardPointerUp(x, y);
                return true;
            },
            dismiss: StopTimerFromUi,
            blocksInputBelow: false));

        m.Register(new LegacyOverlayLayer(
            "roll", 590,
            visible: () => RollCardOpen,
            bounds: RollCardRect,
            hit: (x, y) => RollCardOpen && RollCardRect().Contains(x, y),
            down: (x, y, id, type) => RollCardOpen && RollCardPointerDown(x, y),
            move: (x, y, id) => RollCardOpen && RollCardPointerMove(x, y),
            up: (x, y, id) =>
            {
                if (!RollCardOpen) return false;
                RollCardPointerUp(x, y);
                return true;
            },
            dismiss: CloseRollCardFromUi,
            dismissPolicy: OverlayDismissPolicy.Escape,
            blocksInputBelow: false));

        m.Register(new LegacyOverlayLayer(
            "ppt", 500,
            visible: () => !PptBarRect().IsEmpty,
            bounds: PptBarRect,
            hit: PptBarContains,
            down: (x, y, id, type) => PptBarPointerDown(x, y),
            move: (x, y, id) => PptBarPointerMove(x, y),
            up: (x, y, id) =>
            {
                PptBarPointerUp(x, y);
                return true;
            },
            dismiss: ExitPptMode,
            dismissPolicy: OverlayDismissPolicy.Escape,
            blocksInputBelow: false));

        // P5.1：截图取景。整屏覆盖，但只接管“取消/确认/调整”这类已被截图吃掉的按下；
        // 没吃掉时 `blocksInputBelow=false`，落回 Tool.Capture 旧分支起新框。
        m.Register(new LegacyOverlayLayer(
            "capture", 850,
            visible: () => CaptureActive,
            bounds: () => ViewportCanvas,
            hit: (x, y) => CaptureActive,
            down: (x, y, id, type) => CaptureActive && CapturePointerDown(x, y),
            move: (x, y, id) =>
            {
                if (!CaptureActive) return false;
                if (CaptureAdjusting) CaptureAdjustMove(x, y);
                else ExtendCaptureTo(x, y);
                return true;
            },
            up: (x, y, id) => CaptureActive,
            dismiss: CancelCapture,
            dismissPolicy: OverlayDismissPolicy.Escape,
            blocksInputBelow: false));

        // P5.0：选择操作条（含收起圆钮 / 挂着的面板 / AI 菜单）。
        // 只切“按下”：命中后调用现有选择手势分流；条本身不需要指针捕获。
        m.Register(new LegacyOverlayLayer(
            "selection", 400,
            visible: () => SelectionBarShown,
            bounds: () => BarDrawnCollapsed ? SelectionDotCanvasRect : SelectionBarCanvasRect,
            hit: SelectionLayerHit,
            down: (x, y, id, type) => SelectionBarShown && SelectionBarPointerDown(x, y),
            wantsCapture: false,
            blocksInputBelow: false));

        _overlayLayers = m;
    }

    private RectF LibraryLayerBounds()
    {
        if (!LibraryPanelVisibleNow) return RectF.Empty;
        var r = LibraryPanelRectNow();
        if (LibraryMenu != 0) r.Add(LibraryMenuRect());
        if (LibraryCategoriesOpen) r.Add(LibraryDrawerRectNow());
        return r;
    }

    private bool LibraryLayerHit(float x, float y)
    {
        if (!LibraryPanelVisibleNow) return false;
        if (LibraryLayout.Contains(LibraryPanelRectNow(), x, y)) return true;
        if (LibraryMenu != 0 && LibraryLayout.Contains(LibraryMenuRect(), x, y)) return true;
        if (LibraryCategoriesOpen && LibraryLayout.Contains(LibraryDrawerRectNow(), x, y)) return true;
        return false;
    }

    /// <summary>
    /// Phase 5.0：选择操作条这一块（整条 / 收起圆钮 / 挂着的面板 / AI 菜单）是否命中。
    /// 判据和 <see cref="TryBeginSelectionGesture"/> 的 ⓪ 段一致；只用于浮层路由。
    /// </summary>
    private bool SelectionLayerHit(float x, float y)
    {
        if (!SelectionBarShown) return false;
        if (BarDrawnCollapsed) return SelectionDotCanvasRect.Contains(x, y);

        var aabb = LiveSelectionFrame.CanvasAabb;
        float ui = UiUnit;
        if (SelectionBarCanvasRect.Contains(x, y)) return true;
        if (CustomColorOpen
            && SelectionHandles.PickContains(x, y, aabb, ui, FurnitureViewport,
                                             SelectionHandles.SwatchCount, SingleTextBoxSelection))
            return true;
        if (SelPanelOpen != SelPanel.None
            && SelectionHandles.PanelContains(x, y, aabb, ui, FurnitureViewport,
                                              SelPanelOpen, SelectionHandles.SwatchCount,
                                              SingleTextBoxSelection))
            return true;
        if (SelAiMenuOpen
            && SelectionHandles.BarAiMenuRectIn(SelectionBarCanvasRect, ui, FurnitureViewport,
                   HandOcrStatus.Length > 0).Contains(x, y))
            return true;
        return false;
    }

    /// <summary>Phase 5.0：条上的按下仍走原来的选择手势分流（按钮 / 面板 / 圆钮）。</summary>
    private bool SelectionBarPointerDown(float x, float y)
        => TryBeginSelectionGesture(x, y, shift: false, alt: false);

    internal bool SelectionLayerHitForTest(float x, float y) => SelectionLayerHit(x, y);
}
