using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 引擎侧对 <see cref="IOverlayUi"/> 的实现：持有界面、把界面的请求翻译成
/// 引擎操作，并保证界面永远改不到文档内部。
///
/// 界面可以有多套，但这个宿主只有一个——换界面就是换一个 IOverlayUi 实例。
/// </summary>
internal sealed class UiHost : IUiHost, IEngineCommands
{
    private sealed class Pending
    {
        public bool Quit;
        public bool Clear;
    }

    private readonly InkEngine _engine;
    private RectF _screen;
    private readonly float _dpiScale;

    /// <summary>UI 之外的代码要求重画界面时，先记在这里，由主线程消费。</summary>
    private Pending _pending;

    public UiHost(InkEngine engine, RectF screen, float dpiScale, bool isReattach = false)
    {
        _engine = engine;
        _screen = screen;
        _dpiScale = dpiScale;
        IsReattach = isReattach;
        Commands = this;
    }

    public IEngineCommands Commands { get; }

    public UiState State => _engine.SnapshotState();

    public RectF Screen => _screen;

    /// <summary>
    /// 屏幕范围是**逻辑像素**（界面自己的坐标空间），屏幕尺寸/DPI 变化时由
    /// 引擎刷新。早先给的是物理像素，2 倍屏上悬浮条会被算到屏幕外。
    /// </summary>
    internal void UpdateScreen(RectF logicalScreen) => _screen = logicalScreen;

    public float DpiScale => _dpiScale;

    /// <summary>
    /// true 表示这是"换回同一个界面"，不是首次接入。界面据此决定要不要
    /// 重新采纳引擎给的屏幕尺寸——重新采纳会把界面摆到按另一套坐标算的位置上。
    /// </summary>
    public bool IsReattach { get; }

    public IDWriteFactory TextFactory => Gfx.WriteFactory;

    public ID2D1Factory1 PathFactory => Gfx.D2DFactory;

    public double NowMs => _engine.NowMs;

    public int DocumentVersion => _engine.Doc.Version;

    public void InvalidateUi() => _engine.InvalidateUi();

    // ---- 界面自己的偏好（引擎只存不解释，落在 settings.json 的 ui 段）----

    public string GetPref(string key) => _engine.GetUiPref(key);
    public void SetPref(string key, string value) => _engine.SetUiPref(key, value);

    // ---- IEngineCommands -------------------------------------------------

    public void SetTool(Tool tool) => _engine.SetToolFromUi(tool);
    public void SetColor(Color4 color) => _engine.SetColorFromUi(color);
    public void SetWidth(float logicalPx) => _engine.SetWidthFromUi(logicalPx);
    public void Undo() => _engine.UndoFromUi();
    public void Redo() => _engine.RedoFromUi();
    public void Clear() => _engine.ClearFromUi();
    public void SetPassThrough(bool on) => _engine.SetPassThroughFromUi(on);
    public void SetBoard(bool on) => _engine.SetBoardFromUi(on);
    public void SetBoardColor(Color4 color) => _engine.SetBoardColorFromUi(color);
    public void SetSelectMode(SelectMode mode) => _engine.SetSelectModeFromUi(mode);
    public void SelectAll() => _engine.SelectAllFromUi();
    public void FlipPage(bool down) => _engine.FlipPageFromUi(down);

    public void SetBoardPattern(int pattern, float stepLogical)
        => _engine.SetBoardPatternFromUi(pattern, stepLogical);

    public void SetCaptureHideInk(bool hideInk) => _engine.SetCaptureHideInkFromUi(hideInk);
    public void Restart() => _engine.RestartFromUi();
    public void Quit() => _engine.QuitFromUi();

    /// <summary>
    /// 把跨线程请求合并成"退出"和"清空"两个标志，主线程在下一帧开头消费。
    /// 这样界面可以从任何线程调命令，不会踩到引擎的单线程状态。
    /// </summary>
    internal void PostQuit()
    {
        var p = (_pending ??= new Pending());
        p.Quit = true;
    }

    internal void PostClear()
    {
        var p = (_pending ??= new Pending());
        p.Clear = true;
    }

    internal void FlushPending()
    {
        var p = _pending;
        if (p == null) return;
        _pending = null;

        if (p.Clear) _engine.ClearFromUi();
        if (p.Quit) _engine.QuitFromUi();
    }
}
