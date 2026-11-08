namespace InkEngine;

public partial class InkEngine
{
    private OverlayLayerManager _overlayLayers;

    /// <summary>
    /// 浮层注册表。A4.1 只注册适配器（读状态/命中/回调），**尚未切主输入路由**；
    /// 绘制仍是 Overlay 原有代码。
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
            move: (x, y, id) => LibraryPanelVisibleNow && LibraryPanelPointerMove(x, y, id),
            up: (x, y, id) => LibraryPanelVisibleNow && LibraryPanelPointerUp(x, y, id),
            dismiss: CloseLibraryPanel,
            dismissPolicy: OverlayDismissPolicy.Escape));

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
            dismiss: StopTimerFromUi));

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
            dismissPolicy: OverlayDismissPolicy.Escape));

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
            dismissPolicy: OverlayDismissPolicy.Escape));

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

    /// <summary>自检/影子路由：只读探测当前最上层浮层，不触发任何回调。</summary>
    internal string OverlayPeekForTest(float x, float y) => OverlayLayers.PeekHit(x, y)?.Id ?? "";

    internal int OverlayShadowMismatches { get; private set; }
    internal string OverlayShadowLast { get; private set; } = "";
    private int _overlayShadowLogged;

    /// <summary>
    /// A4.2 影子路由：用旧分支顺序算一遍“应该由谁吃”，和 OverlayLayerManager.PeekHit 对比。
    /// 只记录、不改变输入；UI 不在这张表里，旧结果是 ui 时跳过。
    /// </summary>
    internal void ShadowOverlayHit(float screenX, float screenY)
    {
        string oldLayer = OverlayShadowLegacyTop(screenX, screenY);
        if (oldLayer == "ui") return;

        string newLayer = OverlayLayers.PeekHit(screenX, screenY)?.Id ?? "";
        if (string.Equals(oldLayer, newLayer, StringComparison.Ordinal)) return;

        OverlayShadowMismatches++;
        OverlayShadowLast = $"{oldLayer} -> {newLayer} @ {screenX:F0},{screenY:F0}";
        if (_overlayShadowLogged < 8)
        {
            _overlayShadowLogged++;
            Console.WriteLine("[overlay-shadow] " + OverlayShadowLast);
        }
    }

    private string OverlayShadowLegacyTop(float x, float y)
    {
        if (LibraryLayerHit(x, y)) return "library";
        if (ReplayActive && ReplayBarRect().Contains(x, y)) return "replay";
        if (UiContains(x, y)) return "ui";
        if (TimerCardOpen && TimerCardRect().Contains(x, y)) return "timer";
        if (RollCardOpen && RollCardRect().Contains(x, y)) return "roll";
        if (PptBarContains(x, y)) return "ppt";
        return "";
    }
}
