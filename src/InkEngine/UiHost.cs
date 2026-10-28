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
    private RectF _workArea;
    private readonly float _dpiScale;

    /// <summary>UI 之外的代码要求重画界面时，先记在这里，由主线程消费。</summary>
    private Pending _pending;

    public UiHost(InkEngine engine, RectF screen, float dpiScale, bool isReattach = false)
    {
        _engine = engine;
        _screen = screen;
        // 工作区一开始就从引擎取一次（后面的刷新走 UpdateWorkArea）：
        // 界面的"默认位置"要它，而它在 Attach → Layout 那一刻就要用上。
        // ⚠ **必须与 Screen 同一套单位**（界面逻辑 = 物理 ÷ DPI ÷ UiScale，见
        //   `UiLogicalWorkArea`）——2026-10-16 修：原来取的是只除 DPI 的
        //   `LogicalPrimaryWorkArea`，界面缩放 ≠1 时"居中"整体偏移（用户报
        //   "界面大小调过之后，重启就不居中"：运行中换档会走 SetUiScaleFromUi 重刷
        //   工作区，所以只有**重启后的第一份**是错的）。
        _workArea = engine.UiLogicalWorkArea;
        _dpiScale = dpiScale;
        IsReattach = isReattach;
        Commands = this;
    }

    public IEngineCommands Commands { get; }

    public UiState State => _engine.SnapshotState();

    public RectF Screen => _screen;

    /// <summary>
    /// **主屏工作区**（逻辑像素，屏幕减掉任务栏）。界面算默认位置用它——
    /// 见 <see cref="InkEngine.PrimaryWorkArea"/> 的注释。
    /// </summary>
    public RectF WorkArea => _workArea;

    /// <summary>
    /// 屏幕范围是**逻辑像素**（界面自己的坐标空间），屏幕尺寸/DPI 变化时由
    /// 引擎刷新。早先给的是物理像素，2 倍屏上悬浮条会被算到屏幕外。
    /// </summary>
    internal void UpdateScreen(RectF logicalScreen) => _screen = logicalScreen;

    /// <summary>工作区同理（逻辑像素）。和 <see cref="UpdateScreen"/> 一起由引擎刷。</summary>
    internal void UpdateWorkArea(RectF logicalWorkArea) => _workArea = logicalWorkArea;

    public float DpiScale => _dpiScale;

    /// <summary>当前界面缩放档（见 <see cref="UiScalePresets"/>；设置页显示用）。</summary>
    public float UiScale => _engine.UiScale;

    /// <summary>
    /// true 表示这是"换回同一个界面"，不是首次接入。界面据此决定要不要
    /// 重新采纳引擎给的屏幕尺寸——重新采纳会把界面摆到按另一套坐标算的位置上。
    /// </summary>
    public bool IsReattach { get; }

    /// <summary>本次进程由 `--startup` 拉起时，界面首次 Attach 进入后台待命形态。</summary>
    public bool StartupMode => _engine.StartupMode;

    public IDWriteFactory TextFactory => Gfx.WriteFactory;

    public ID2D1Factory1 PathFactory => Gfx.D2DFactory;

    public double NowMs => _engine.NowMs;

    public int DocumentVersion => _engine.Doc.Version;

    public void InvalidateUi() => _engine.InvalidateUi();

    // ---- 界面自己的偏好（引擎只存不解释，落在 settings.json 的 ui 段）----

    public string GetPref(string key) => _engine.GetUiPref(key);
    public string GetClipboardText() => ClipboardText.Get();
    public void SetPref(string key, string value) => _engine.SetUiPref(key, value);

    /// <summary>某个动作当前的键位文本（悬停提示用；唯一起源见 KeyBindings）。</summary>
    public string KeyText(KeyAction action) => _engine.KeyTextFor(action);

    /// <summary>界面把自己的浮层主题推给引擎（见 IOverlayUi / IUiHost 的说明）。</summary>
    public void SetFloatingTheme(UiTheme theme) => _engine.SetFloatingThemeFromUi(theme);

    // ---- IEngineCommands -------------------------------------------------

    public void SetTool(Tool tool) => _engine.SetToolFromUi(tool);
    public void SetUiScale(float scale) => _engine.SetUiScaleFromUi(scale);
    public void SetEraserPreferred() => _engine.SetEraserPreferredFromUi();
    public void CycleParabolaAxis() => _engine.CycleParabolaAxis();
    public void CycleLineDash() => _engine.CycleLineDash();
    public void CycleSolidSides() => _engine.CycleSolidSides();
    public void CycleHyperbolaAsymptotes() => _engine.CycleHyperbolaAsymptotes();
    public void ToggleLibraryPanel() => _engine.ToggleLibraryPanel();
    public void SetLibraryCategory(string id) => _engine.SetLibraryCategory(id);
    public void CreateLibraryCategory() => _engine.CreateLibraryCategory();
    public void DeleteLibraryCategory(string id) => _engine.DeleteLibraryCategory(id);
    public int EmptyLibraryTrash() => _engine.EmptyLibraryTrash();
    public void MoveLibraryEntry(string categoryId) => _engine.MoveLibraryEntry(categoryId);
    public void DeleteLibrarySelection() => _engine.DeleteLibrarySelection();
    public void UpdateLibraryEntryFromSelection() => _engine.UpdateLibraryEntryFromSelection();
    public void CycleEllipseFocusTriangle() => _engine.CycleEllipseFocusTriangle();
    public void SetColor(Color4 color) => _engine.SetColorFromUi(color);
    public void SelectCustomPenColor() => _engine.SelectCustomPenColorFromUi();
    public void SetCustomPenColor(Color4 color) => _engine.SetCustomPenColorFromUi(color);
    public void SelectPenSwatch(int slot) => _engine.SelectPenSwatchFromUi(slot);
    public void SetPenSwatchColor(int slot, Color4 color, bool rememberRecent)
        => _engine.SetPenSwatchColorFromUi(slot, color, rememberRecent);
    public void SelectHighlighterSwatch(int slot) => _engine.SelectHighlighterSwatchFromUi(slot);
    public void SetHighlighterSwatchColor(int slot, Color4 color, bool rememberRecent, bool normalize)
        => _engine.SetHighlighterSwatchColorFromUi(slot, color, rememberRecent, normalize);
    public void SetHighlighterOpacity(int slot, int level)
        => _engine.SetHighlighterOpacityFromUi(slot, level);
    public void BeginScreenColorPick() => _engine.BeginScreenColorPickFromUi();
    public void BeginScreenColorPickAt(int slot) => _engine.BeginScreenColorPickFromUi(slot);
    public void SetWidth(float logicalPx) => _engine.SetWidthFromUi(logicalPx);
    public void SetDash(StrokeDash dash) => _engine.SetDashFromUi(dash);
    public void Undo() => _engine.UndoFromUi();
    public void Redo() => _engine.RedoFromUi();
    public void Clear() => _engine.ClearCurrentPageFromUi();
    public void SetPassThrough(bool on) => _engine.SetPassThroughFromUi(on, toPen: !on);
    public void SetKeyboardMode(bool on) => _engine.SetKeyboardMode(on);
    public void SetAutoStart(bool on) => _engine.SetAutoStartFromUi(on);
    public void RefreshAutoStart() => _engine.RefreshAutoStartFromUi();
    public void SetBoard(bool on) => _engine.SetBoardFromUi(on);
    public void SetBoardColor(Color4 color) => _engine.SetBoardColorFromUi(color);
    public void SetSelectMode(SelectMode mode) => _engine.SetSelectModeFromUi(mode);
    public void SelectAll() => _engine.SelectAllFromUi();
    public void FlipPage(bool down) => _engine.FlipPageFromUi(down);

    public void SetCoordGridDefault(bool on) => _engine.SetCoordGridDefaultFromUi(on);
    public void SetDwellShape(bool on) => _engine.SetDwellShapeFromUi(on);
    public void SetPressure(bool on) => _engine.SetPressureFromUi(on);
    public void SetFineStroke(bool on) => _engine.SetFineStrokeFromUi(on);
    /// <summary>「设置 → 书写 → 墨迹预测」：自绘预测尾总开关（默认关，2026-10-16 起）。</summary>
    public void SetPredict(bool on) => _engine.SetPredictFromUi(on);
    /// <summary>触摸手势总开关（默认开；关掉只剩单指书写）。</summary>
    public void SetTouchGestures(bool on) => _engine.SetTouchGesturesFromUi(on);
    public void SetPrivacyConsent(bool on) => _engine.SetPrivacyConsentFromUi(on);
    public void BackupAllData() => _engine.BackupAllDataFromUi();
    public void RestoreFromBackup() => _engine.RestoreAllDataFromUi();
    public void SetUiOverlay(bool open) => _engine.SetUiOverlayFromUi(open);
    public int ToggleCoordGrid() => _engine.ToggleSelectionGrid();

    public void SetBoardPattern(int pattern, float stepLogical)
        => _engine.SetBoardPatternFromUi(pattern, stepLogical);

    public void SetCaptureHideInk(bool hideInk) => _engine.SetCaptureHideInkFromUi(hideInk);
    public void EnterCapture() => _engine.BeginCaptureModeFromUi();

    public void SetBoardOpacity(float opacity) => _engine.SetBoardOpacityFromUi(opacity);

    /// <summary>「设置 → 导出日志…」：运行日志打包 zip（2026-10-16）。</summary>
    public void ExportLogs() => _engine.ExportLogsFromUi();

    /// <summary>「设置 → 上传日志…」：日志一键传云端（2026-10-17）。</summary>
    public void UploadLogs() => _engine.UploadLogsFromUi();

    public void Paste() => _engine.PasteFromClipboard();
    public void Restart() => _engine.RestartFromUi();
    public void Quit() => _engine.QuitFromUi();
    public void SaveInkFile() => _engine.SaveInkFileFromUi();
    public void OpenInkFile() => _engine.OpenInkFileFromUi();
    /// <summary>「历史板书」：起始目录落在历史目录里的"打开墨迹"。</summary>
    public void OpenInkHistory() => _engine.OpenInkHistoryFromUi();
    public void SaveBoardImage() => _engine.SaveBoardImageFromUi();
    public void SaveBoardAny() => _engine.SaveBoardAnyFromUi();
    public void OpenDocument() => _engine.OpenDocumentFromUi();
    public void OpenBoardAny() => _engine.OpenBoardAnyFromUi();
    public void CloseDocument() => _engine.CloseDocumentFromUi();
    public void StartReplay() => _engine.StartReplayFromUi();
    public void StopReplay() => _engine.StopReplayFromUi();
    public void OpenTimerCard() => _engine.OpenTimerCardFromUi();
    public void OpenRollCard() => _engine.OpenRollCardFromUi();
    public void OpenRollOne() => _engine.OpenRollOneFromUi();
    public void CheckUpdate() => _engine.CheckUpdateFromUi();
    public void ApplyUpdate() => _engine.ApplyUpdateFromUi();

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

        if (p.Clear) _engine.ClearCurrentPageFromUi();
        if (p.Quit) _engine.QuitFromUi();
    }
}
