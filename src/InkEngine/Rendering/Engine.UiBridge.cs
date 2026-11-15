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
    //  界面接入（引擎边界）
    //
    //  引擎负责：笔画/文档/输入/渲染/工具状态。
    //  界面负责：画自己、解释点击。
    //  两边唯一的通道就是 IOverlayUi / IUiHost，引擎里没有任何具体界面代码。
    // =====================================================================

    /// <summary>
    /// 换界面。传 null 或 <see cref="NullUi"/> 就是无界面（纯笔迹）。
    /// 界面只在覆盖层建好之后接入；运行中换界面也是安全的。
    /// </summary>
    /// <param name="keepScreen">
    /// 传 true 表示沿用当前这个界面已经算好的屏幕尺寸。自检里"临时换掉再换回来"
    /// 会用到处，避免界面按引擎内部的窗口尺寸重新布局（那把界面摆到别处去了）。
    /// 正式产品代码用默认值即可。
    /// </param>
    public void SetUi(IOverlayUi ui, bool keepScreen = false)
    {
        Ui = ui ?? new NullUi();
        // 换了界面就得让各窗口重新布局：覆盖窗口缓存着"上次给 Layout 的屏幕"，
        // 不清的话新界面永远等不到 Layout，QueryBounds 会一直返回空（看不见也点不到）。
        foreach (var w in _windows) w.InvalidateUiLayout();
        _uiFaults = 0;
        _uiHover = false;
        // 界面看到的屏幕坐标是逻辑像素：它按自己的逻辑尺寸布局，引擎负责按 DPI×界面缩放 放大。
        Host = new UiHost(this, UiLogicalScreen, DpiScale, keepScreen);
        Ui.Attach(Host);
        InvalidateUi();
        NotifyUiStateChanged();
    }

    /// <summary>当前接入的界面（诊断用）。</summary>
    public IOverlayUi CurrentUi => Ui;

    /// <summary>虚拟桌面范围，界面据此决定悬浮条放在哪。</summary>
    public RectF VirtualScreen => new()
    {
        MinX = _virtualX,
        MinY = _virtualY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY + _virtualH,
    };

    /// <summary>虚拟桌面的**逻辑**范围（除以 DPI 缩放），界面布局用这个。</summary>
    public RectF LogicalVirtualScreen => new()
    {
        MinX = _virtualX / DpiScale,
        MinY = _virtualY / DpiScale,
        MaxX = (_virtualX + _virtualW) / DpiScale,
        MaxY = (_virtualY + _virtualH) / DpiScale,
    };

    /// <summary>
    /// 界面自己的**逻辑屏幕**：逻辑桌面再除以 UiScale。
    /// 界面放大后 1 个"界面逻辑单位"占更多物理像素，它能摆的地方自然变小——
    /// 这是"只乘一个 UiScale"之外必须同时做的一件事，不然 1.3 倍下工具条会被推到屏幕外。
    /// </summary>
    public RectF UiLogicalScreen => new()
    {
        MinX = LogicalVirtualScreen.MinX / UiScale,
        MinY = LogicalVirtualScreen.MinY / UiScale,
        MaxX = LogicalVirtualScreen.MaxX / UiScale,
        MaxY = LogicalVirtualScreen.MaxY / UiScale,
    };

    /// <summary>主屏工作区的"界面逻辑"版（口径同 <see cref="UiLogicalScreen"/>）。</summary>
    internal RectF UiLogicalWorkArea
    {
        get
        {
            var w = LogicalPrimaryWorkArea;
            return new RectF
            {
                MinX = w.MinX / UiScale, MinY = w.MinY / UiScale,
                MaxX = w.MaxX / UiScale, MaxY = w.MaxY / UiScale,
            };
        }
    }

    /// <summary>
    /// 设置页「界面大小」换档（吸附后立刻生效；落盘由界面负责，见 FullUi.ActivateRow）。
    /// 界面尺寸体系一变：各窗口的布局缓存作废、宿主拿新的"逻辑屏幕"重算、整帧重画。
    /// </summary>
    internal void SetUiScaleFromUi(float scale)
    {
        scale = UiScalePresets.Snap(scale);
        if (MathF.Abs(scale - UiScale) < 0.001f) return;
        UiScale = scale;
        foreach (var w in _windows) w.InvalidateUiLayout();
        Host?.UpdateScreen(UiLogicalScreen);
        Host?.UpdateWorkArea(UiLogicalWorkArea);
        InvalidateUi();
        _dirty = true;
    }

    /// <summary>
    /// **主屏的工作区**（物理像素）：屏幕减掉任务栏之后剩下的那块矩形。
    ///
    /// 为什么界面需要它（用户 2026-09-27）：悬浮条的**默认位置**要"紧贴任务栏上方、
    /// 两者不重叠"。按整个屏幕算做不到——覆盖层是置顶的，任务栏挡不住它，于是贴底那一条
    /// 会**压在任务栏上**（而面板矩形是"我们的地盘"，那一块的点击也一并被吃掉）。
    /// 工作区这个数天然把任务栏扣掉了，任务栏在底/左/上/右都成立。
    ///
    /// 读不到（老系统 / 合成环境）就退回虚拟桌面——**宁可按"没有任务栏"算，也不能给空矩形**：
    /// 给了空矩形，界面会把它当"屏幕是 0×0"从而把面板夹到左上角。
    ///
    /// ⚠ 多屏时给的是**主屏**（不是"面板当前所在那块屏"）：用户 2026-09-27 选的。
    /// 位置本来就不记盘，所以"每次都回主屏"是可预测的那一档。
    /// </summary>
    public RectF PrimaryWorkArea
    {
        get
        {
            var r = default(Native.RECT);
            if (Native.SystemParametersInfoRect(Native.SPI_GETWORKAREA, 0, ref r, 0)
                && r.Width > 0 && r.Height > 0)
                return new RectF { MinX = r.Left, MinY = r.Top, MaxX = r.Right, MaxY = r.Bottom };
            return VirtualScreen;
        }
    }

    /// <summary>主屏工作区的**逻辑**范围（除以 DPI），界面算"默认位置"用它。</summary>
    public RectF LogicalPrimaryWorkArea
    {
        get
        {
            var w = PrimaryWorkArea;
            return new RectF
            {
                MinX = w.MinX / DpiScale, MinY = w.MinY / DpiScale,
                MaxX = w.MaxX / DpiScale, MaxY = w.MaxY / DpiScale,
            };
        }
    }

    /// <summary>
    /// 界面声明"外观变了，请重画我的缓存"。可以由界面的任意线程调用，
    /// 主线程在下一帧消费。**不要每帧调**，那等于每帧重画整个界面。
    /// </summary>
    public void InvalidateUi()
    {
        UiInvalidatePending = true;
        _uiInvalidateSeq++;
        _dirty = true;
    }

    /// <summary>
    /// 界面的重画请求流水号。渲染循环拿它判"渲染期间界面是不是又提了请求"，
    /// 从而**不抹掉**那一帧请求（见 Loop 里那段的注释）。
    /// </summary>
    private long _uiInvalidateSeq;

    internal UiState SnapshotState() => new()
    {
        Tool = Tool,
        // 放映中：界面的"进放映就把面板展开 + 归位"靠它（只在边沿用一次，见 UiState.PptMode）。
        PptMode = PptMode,
        ParabolaAxis = ParabolaAxis,      // 界面拿它把图形面板那一格的图标转成当前朝向
        LineDash = LineDash,              // 界面拿它把「直线」那一格的图标换成当前线型
        PrismSides = _sidesPrism,         // 界面拿它把「棱柱」那一格的图标换成当前档（＋档位点）
        PyramidSides = _sidesPyramid,     // 棱锥 / 棱台各记各的档（三格的档位点互不影响）
        FrustumSides = _sidesFrustum,
        SolidMinSides = Stroke.MinPrismSides,   // 档位范围也推上去（界面才知道点几个点）
        SolidMaxSides = Stroke.MaxPrismSides,
        // 图形面板里那两格"再点一次换一档"的当前档（界面拿它画图标 + 档位点）：
        // 双曲线的渐近线（有 / 无）、椭圆（带焦点）的焦点三角形（有 / 无）。
        HyperbolaAsymptotes = HyperbolaAsymptotes,
        EllipseFocusTriangle = EllipseFocusTriangle,
        Color = Tool == Tool.Highlighter ? HighlighterCurrent
              : Tool == Tool.Text ? TextToolColor : CurrentColor,
        PaletteBase = Tool == Tool.Highlighter
            ? new Color4(HighlighterCurrent.R, HighlighterCurrent.G, HighlighterCurrent.B, 1f)
            : Tool == Tool.Text ? TextToolColor : CurrentColor,
        CustomPenColor = CustomPenColor,
        CustomPenSelected = CustomPenSelected,
        PenSwatchColors = (Color4[])PenSwatchColors.Clone(),
        PenSwatchSelected = PenSwatchSelected,
        RecentPenColors = _recentPenColors.ToArray(),
        Width = CurrentToolWidthLogical,
        // 按工具分开给：只给一个 Width 的话，滑块在切工具时会跳（三个工具各记各的宽度）。
        PenWidth = PenWidthLogical,
        HighlighterWidth = HighlighterWidthLogical,
        HighlighterColor = HighlighterCurrent,
        HighlighterSwatchColors = (Color4[])HighlighterSwatchColors.Clone(),
        HighlighterSwatchOpacity = (int[])HighlighterSwatchOpacity.Clone(),
        HighlighterSwatchSelected = HighlighterSwatchSelected,
        RecentHighlighterColors = _recentHighlighterColors.ToArray(),
        LaserWidth = LaserWidthLogical,
        TextFontSize = TextToolFontSize,
        TextColor = TextToolColor,
        TextSwatchColors = (Color4[])TextSwatchColors.Clone(),
        TextBoxEditing = _textBoxEditing,
        Dash = PenDash,
        PassThrough = PassThrough,
        KeyboardMode = KeyboardMode,
        ColorPickActive = ColorPickActive,
        AutoStartOn = AutoStartOn,
        Board = BoardOn,
        BoardColor = BoardColor,
        BoardPattern = BoardPattern,
        BoardPatternStep = BoardPatternStepLogical,
        BoardOpacity = BoardOpacity,
        CaptureHideInk = CaptureHideInk,
        SelectMode = SelMode,
        CoordGridDefault = CoordGridDefault,
        DwellShapeOn = DwellShapeEnabled,
        PressureOn = PressureWidth.Enabled,      // 界面拿它显示「设置 → 书写 → 压感粗细」那个开关
        FineStrokeOn = RawInputCapture,          // 「设置 → 书写 → 精细笔迹」（原始输入补点）总开关
        PredictTailOn = PredictTailEnabled,      // 「设置 → 书写 → 墨迹预测」（自绘预测尾）总开关（默认关，2026-10-16 起）
        WetMode = WetMode,                       // 「设置 → 墨迹 → 湿墨档位」（0..3；界面显示当前档）
        HistoryCount = BoardHistory.Count,       // 「更多 → 墨迹 → 历史板书」那一格的亮/灰
        TouchGesturesOn = _touch.Enabled,        // 触摸模式：true=手势；false=单指书写（默认手势）
        PrivacyConsentOn = PrivacyConsentGranted, // 设备与性能统计的同意状态
        // [删除 2026-10-05] PredictOn（墨迹预测）：随老预测系统移除。
        ScreenIndex = ScreenIndex,
        CanFlipPageUp = CanFlipPageUp,
        IsDrawing = _drawing,
        UndoDepth = Doc.UndoDepth,
        RedoDepth = Doc.RedoDepth,
        StrokeCount = Doc.Strokes.Count,
        SelectionCount = Doc.Selected.Count,
        UpdateStage = UpdateState,
        UpdateText = UpdateText,
        InkStatus = InkStatus,                  // 界面「墨迹」页的状态行
        DocOpen = DocView.IsOpen,
        DocInfo = DocView.IsOpen ? $"{DocView.Title} · {DocView.Count} 页" : "",
        BoardOn = BoardOn,
        ClearOnArrow = ClearOnArrow,
        ClearOnArrowActive = ClearOnArrowActiveNow,
        ReplayActive = ReplayActive,
        ReplayPlaying = ReplayPlaying,
        ReplaySpeed = ReplaySpeed,
        // 「课堂」页：计时器状态 + 点名名单（点名全在界面层做，引擎只读盘/推状态）
        TimerActive = TimerActive,
        TimerPaused = TimerPaused,
        TimerFinished = TimerFinished,
        TimerMode = TimerKind,
        TimerValueMs = TimerValueMs,
        TimerCardOpen = TimerCardOpen,
        TimerSettingsOpen = TimerSettingsOpen,
        TimerExpanded = TimerExpanded,
        RollCardOpen = RollCardOpen,
        RollSettingsOpen = RollSettingsOpen,
        IsFirstRun = !InkSettings.ExistedAtLoad,
        LibraryOpen = LibraryPanelOpen,
        LibraryShapesTab = LibraryPanelOpen && LibraryTab == LibraryTab.Shapes,
        LibraryCategoryId = LibraryCategoryId,
        LibraryCategories = _libraryCategoryInfos,
        LibrarySelectionCount = LibrarySelected.Count,
        Names = Names,
        AiPrompt = AiPromptText,
        AiPromptEditing = _aiPromptEditing,
        AiBusy = _handOcrBusy,
        AiStatus = _handOcrStatus,
        AiApiConfigured = AiApiConfigured,
        AiApiModel = AiApiModel,
        LogNote = LogNoteText,
        LogNoteEditing = _logNoteEditing,
        LogUploadConsentOn = LogUploadConsentGranted,
    };

    private void NotifyUiStateChanged()
    {
        if (Ui == null) return;
        var snapshot = SnapshotState();
        UiGuard("OnStateChanged", () => Ui.OnStateChanged(snapshot));
    }

    internal void SetToolFromUi(Tool tool)
    {
        if (tool == Tool.Text) EnsureTextPrefs();
        if (_textBoxEditing && tool != Tool.Text) EndTextBoxEditFromUi(true);
        ExitReplayForEdit("换工具");
        SwitchTool(tool);
        EraserTelemetry?.Note($"界面换工具 → {ToolName(tool)}", NowMs);
        ApplyCursor();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>面板点橡皮格：切回**上次用的那一种橡皮形态**（整笔/面积），顺手关穿透。
    /// [2026-10-05 用户定] 橡皮子类型不再由"再点一次"切换，面板这一格固定进橡皮、类型看上带。</summary>
    internal void SetEraserPreferredFromUi()
    {
        ExitReplayForEdit("换工具");
        SwitchTool(_eraserKind);
        ApplyCursor();
        _dirty = true;
        NotifyUiStateChanged();
    }

    // ---- 自检钩子（测试要驱动"换工具/操作条按钮/拖动中移动"这些私有路径）----

    /// <summary>自检用：执行一个键位动作（换工具、撤销……都从同一个入口进）。</summary>
    internal void RunActionForTest(KeyAction a) => RunAction(a);

    /// <summary>自检用：点操作条第 index 个按钮。</summary>
    internal void RunBarActionForTest(int index)
        => RunBarAction(index, SelectionHandles.FrameOf(Doc.Selected),
                        SelectionHandles.FrameOf(Doc.Selected).CanvasAabb);

    /// <summary>自检用：按语义执行一个动作（含已收进 `⋯` 的动作）。</summary>
    internal void RunBarButtonForTest(SelBarButton button)
        => RunBarButton(button, SelectionHandles.FrameOf(Doc.Selected),
                        SelectionHandles.FrameOf(Doc.Selected).CanvasAabb);

    /// <summary>自检用：打开 AI 或 `⋯` 弹层（验证互斥/几何）。</summary>
    internal void OpenSelectionMenuForTest(bool ai) => OpenSelectionMenu(ai);

    /// <summary>自检用：拖动中移动指针（选择手势）。</summary>
    internal void UpdateSelectionGestureForTest(float x, float y) => UpdateSelDrag(x, y);

    internal void SetColorFromUi(Color4 color)
    {
        // 荧光笔用同一组基色，只是降低不透明度——老师看到的色相是一致的。
        if (Tool == Tool.Highlighter)
        {
            float alpha = HighlighterCurrent.A > 0.02f ? HighlighterCurrent.A : 0.32f;
            var baseColor = InkPalette.HighlighterBase(color);
            HighlighterCurrent = InkPalette.ToHighlighter(baseColor, alpha);
            HighlighterSwatchSelected = -1;
            SetUiPref("hlSwatchSelected", "-1");
            SetUiPref("hlColorValue", FormatColorPref(baseColor));
            SetUiPref("hlColorOpacity", alpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            CurrentColor = color;
            PenSwatchSelected = -1;
            SetUiPref("penSwatchSelected", "-1");
            SetUiPref("penColorValue", FormatColorPref(color));
            SetUiPref("penCustomSelected", "0");
        }
        // **记住这个颜色**（用户 2026-09-30 定：每个工具各记一个色，重启回来还是它）。
        // 存的是**色带里的序号**而不是 RGB：色带以后微调颜色值，存档也不用跟着迁。
        var band = Tool == Tool.Highlighter ? InkPalette.HighlighterBand : InkPalette.PenBand;
        for (int i = 0; i < band.Length; i++)
            if (CloseColor(band[i].Color, color))
            {
                SetUiPref(Tool == Tool.Highlighter ? "hlColor" : "penColor", i.ToString());
                break;
            }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **新画的坐标系要不要网格**（用户在「更多」抽屉里开关，见 FullUi 的"坐标系网格"那一行）。
    ///
    /// 它只是"画的时候取哪个值"：每个坐标系自己存了一份（见 <see cref="Stroke.Grid"/>），
    /// 所以改这个开关**不会**动已经画在板上的坐标系——那些要选中之后单独改
    /// （<see cref="ToggleSelectionGrid"/>）。理由见 Stroke.Grid 的注释：
    /// 对象要自包含，不能让它长大以后还受一个全局开关摆布。
    ///
    /// 默认**关**：黑板上的坐标系本来就是光秃秃一个十字，格子是要的时候才要。
    /// </summary>
    internal bool CoordGridDefault;

    /// <summary>
    /// 「更多」抽屉里那一行"坐标系网格"被点了一下。
    ///
    /// **选中了坐标系就改它们，没选中就改"以后新画的默认值"**：
    /// 老师的预期是"我点这一下是冲着眼前这个东西来的"，而抽屉里没有"操作谁"的概念，
    /// 于是用"当前有没有选中坐标系"来分派——一次点击只干一件事。
    /// 返回改了几个对象（0 = 改的是新画的默认值）。
    /// </summary>
    internal int ToggleSelectionGrid()
    {
        var targets = new List<Stroke>();
        foreach (var s in Doc.Selected)
            if (s.Kind == StrokeKind.Coordinate) targets.Add(s);   // 数轴没有网格

        if (targets.Count == 0)
        {
            CoordGridDefault = !CoordGridDefault;
            Console.WriteLine($"坐标系网格（新画的默认值）：{(CoordGridDefault ? "开" : "关")}");
            _dirty = true;
            NotifyUiStateChanged();
            return 0;
        }

        // 一批里"有格 / 没格"混着时，按"只要还有没格的，就全给开上"来定
        // ——和锁定那一条同一个口径（有没锁的就全锁上）。
        bool anyOff = false;
        foreach (var s in targets) if (!s.Grid) { anyOff = true; break; }
        Doc.ApplyGrid(targets, anyOff);
        Console.WriteLine($"坐标系网格：{targets.Count} 个对象 → {(anyOff ? "开" : "关")}");
        _dirty = true;
        return targets.Count;
    }

    /// <summary>界面启动时把"坐标系网格"的偏好推过来（见 FullUi.LoadPrefs）。</summary>
    internal void SetCoordGridDefaultFromUi(bool on)
    {
        if (CoordGridDefault == on) return;
        CoordGridDefault = on;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 「更多」抽屉里那一行"停顿成型"被点了一下 / 界面启动时把偏好推过来
    ///（见 FullUi.LoadPrefs）。规格见 计划-图形工具.md §四十二。
    ///
    /// 它只管"以后画的那些参不参与"，**不动已经画在板上的任何东西**——
    /// 变出来的图形是普通对象，开关关掉不会把它们变回手绘（撤销才是那条路）。
    /// </summary>
    internal void SetDwellShapeFromUi(bool on)
    {
        if (DwellShapeEnabled == on) return;
        DwellShapeEnabled = on;
        Console.WriteLine($"停顿成型：{(on ? "开（停一下就把手绘变图形）" : "关")}");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 「更多 → 设置 → 书写 → 压感粗细」被点了一下（2026-10-01）。
    /// 语义见 <see cref="IEngineCommands.SetPressure"/>：**渲染期**开关，文档一个字节不动。
    ///
    /// ⚠ 必须 `Doc.InvalidateAll()`：笔迹是按块**烘进内容层缓存**的，只标一个脏区的话
    /// 屏幕上还是旧粗细（"开关点了没反应"最典型的一种）。整层作废最贵也就重铺一屏。
    /// </summary>
    internal void SetPressureFromUi(bool on)
    {
        if (PressureWidth.Enabled == on) return;
        PressureWidth.Enabled = on;
        Doc.InvalidateAll();
        _dirty = true;
        Console.WriteLine($"压感粗细：{(on ? "开（按压力改粗细）" : "关（所有笔迹等宽，手写板照样流畅）")}");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 触摸模式（「更多 → 设置 → 书写 → 触摸模式」，2026-10-24 恢复为两档）。
    /// true = 手势模式；false = 单指书写（双指手势 / ≥3 指擦 / 长按选择 / 轮盘全部停用）。
    /// 默认手势；单指档落盘 `touch.mode=single`，手势档不写项。
    /// </summary>
    internal void SetTouchGesturesFromUi(bool on)
    {
        bool changed = _touch.Enabled != on;
        _touch.Enabled = on;
        SetUiPref("touch.mode", on ? null : "single");
        if (!on && changed) TouchAbort();
        Console.WriteLine($"触摸模式：{(on ? "手势（双指 / 三指擦 / 长按选择）" : "单指书写（无手势）")}");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 隐私/设备统计版本。2026-11-14：从“首次弹窗同意”改为“默认开启 + 非阻塞提示”，
    /// 采集范围收窄为设备/性能/语言/系统国家地区；不取 IP、省/市、账号、板书。
    /// 以后范围有实质变化，改这个版本号会再提示一次（非阻塞，不弹模态框）。
    /// </summary>
    internal const int PrivacyVersion = 2;

    /// <summary>
    /// 欧盟/欧洲经济区/英国/瑞士：默认关闭，用户可在设置里主动开启。
    /// 这些地区 ePrivacy/GDPR 对终端设备非必要遥测更倾向 opt-in；
    /// 中国等其他地区默认开启，但一样可在设置里关闭。
    /// </summary>
    private static readonly HashSet<string> EuLikeRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR",
        "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK",
        "SI", "ES", "SE", "IS", "LI", "NO", "GB", "CH",
    };

    private static bool IsEuLikeRegion()
    {
        try { return EuLikeRegions.Contains(RegionInfo.CurrentRegion.TwoLetterISORegionName); }
        catch { return false; }
    }

    /// <summary>
    /// 设备与性能统计开关。2026-11-14 起默认开启；
    /// 旧版明确拒绝（denied）继续尊重；欧盟/英国/瑞士默认关闭。
    /// </summary>
    internal bool PrivacyConsentGranted
    {
        get
        {
            string consent = GetUiPref("privacy.consent");
            if (consent == "granted") return true;
            if (consent == "denied") return false;
            return !IsEuLikeRegion();
        }
    }

    private bool PrivacyNotified => GetUiPref("privacy.ver") == PrivacyVersion.ToString();

    /// <summary>
    /// 首次/升级后只提示一次（非阻塞 Toast），不弹模态框。
    /// 旧版明确拒绝的保持关闭；欧盟/英国/瑞士默认关闭；其余默认开启。
    /// </summary>
    private void EnsurePrivacyNotice()
    {
        if (!ExportDialogEnabled) return;
        if (PrivacyNotified) return;

        string consent = GetUiPref("privacy.consent");
        bool explicitDenied = consent == "denied";
        bool defaultOn = !IsEuLikeRegion();

        SetUiPref("privacy.ver", PrivacyVersion.ToString());
        SetUiPref("privacy.at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        if (explicitDenied)
        {
            PrivacyStore.Save(PrivacyVersion, false, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("[隐私] 匿名设备统计：保持关闭（旧版已拒绝）");
            return;
        }

        if (consent != "granted" && !defaultOn)
        {
            SetUiPref("privacy.consent", "denied");
            PrivacyStore.Save(PrivacyVersion, false, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            ShowToast(Loc.T("匿名设备统计默认关闭（欧盟/英国/瑞士）；设置 → 关于可开"), 6000);
            Console.WriteLine("[隐私] 匿名设备统计：默认关闭（欧盟/英国/瑞士）");
            NotifyUiStateChanged();
            return;
        }

        SetUiPref("privacy.consent", "granted");
        PrivacyStore.Save(PrivacyVersion, true, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        ShowToast(Loc.T("匿名设备统计已开启；仅设备/性能，不含 IP/位置，设置 → 关于可关"), 6000);
        Console.WriteLine("[隐私] 匿名设备统计：默认开启（已提示）");
        NotifyUiStateChanged();
    }

    /// <summary>设置页开关。关闭时停止后续上传并尝试删除旧设备卡；打开时直接生效，不再弹模态框。</summary>
    internal void SetPrivacyConsentFromUi(bool on)
    {
        SetUiPref("privacy.consent", on ? "granted" : "denied");
        SetUiPref("privacy.ver", PrivacyVersion.ToString());
        SetUiPref("privacy.at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        PrivacyStore.Save(PrivacyVersion, on, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (!on) DiagLog.StartDeviceCardDelete();
        Console.WriteLine("[隐私] 匿名设备统计：" + (on ? "已开启" : "已关闭"));
        NotifyUiStateChanged();
    }

    /// <summary>命令行上有没有 `--nopressure`（给对照实验用，它优先于用户偏好）。</summary>
    private bool _noPressureArg;

    /// <summary>压感粗细的偏好键（只写"关过"的那一份）。</summary>
    private const string PressurePrefKey = "pressure";

    /// <summary>精简笔迹（原始输入补点）的偏好键（同样只写"关过"的那一份）。</summary>
    private const string FineStrokePrefKey = "finestroke";

    /// <summary>「墨迹预测」（自绘预测尾）的偏好键（**默认关**，只写"开过"的那一份）。</summary>
    private const string PredictPrefKey = "predict2";

    /// <summary>命令行上有没有 `--nopredict2` / `--predict2`（对照实验用，优先于用户偏好）。</summary>
    private bool _noPredictArg, _forcePredictArg;

    /// <summary>命令行上有没有 `--norawinput`（给对照实验用，它优先于用户偏好）。</summary>
    private bool _noRawInputArg;

    /// <summary>
    /// 「更多 → 设置 → 书写 → 精细笔迹」被点了一下（2026-10-07）。
    ///
    /// 关掉 = 不再按原始报率补中间点：**省一点性能**（低配机可关），
    /// 代价是写快时线条的细节少一些。**只影响以后写的**，已经画好的一个字节不动。
    /// 正在写的那一笔会**立刻注销**原始输入（见 <see cref="SetRawCapture"/>：
    /// 空闲时必须注销，否则会退回"空闲也收鼠标空报"那个 46fps 的老问题）。
    /// </summary>
    internal void SetFineStrokeFromUi(bool on)
    {
        if (RawInputCapture == on) return;
        RawInputCapture = on;
        if (!on) SetRawCapture(false);
        Console.WriteLine($"精细笔迹：{(on ? "开（按设备的原始报率补中间点）" : "关（不再补点，省一点性能）")}");
        NotifyUiStateChanged();
    }

    /// <summary>启动时应用"精细笔迹"（自检要单独调一次，理由同压感）。
    /// 开关已撤（常开）——这里只是重新断言"常开"，仍只有 `--norawinput` 能关。</summary>
    internal void ApplyFineStrokePrefForTest()
        => RawInputCapture = !_noRawInputArg;

    /// <summary>
    /// 「更多 → 设置 → 书写 → 墨迹预测」被点了一下（2026-10-09）——**B4 自绘预测尾的总开关**。
    ///
    /// 关掉 = 不再画预测尾（笔迹末端更老实）；开/关都顺手复位预测器（关→立刻撤尾；
    /// 开→从干净状态起步，等下一笔重新喂点）。只影响以后画的，已落笔的墨一个字节不动。
    /// </summary>
    internal void SetPredictFromUi(bool on)
    {
        if (PredictTailEnabled == on) return;
        PredictTailEnabled = on;
        ResetPredictTail();
        Console.WriteLine($"墨迹预测：{(on ? "开（自绘预测尾）" : "关（不画预测尾）")}");
        NotifyUiStateChanged();
    }

    /// <summary>启动时应用"墨迹预测"偏好（自检要单独调一次，理由同压感 / 精细笔迹）。</summary>
    internal void ApplyPredictPrefForTest() => ApplyPredictTailSetting();

    /// <summary>**默认关**（2026-10-16 用户定："以后的软件默认不开预测"）；命令行为对照实验优先
    /// （`--nopredict2` 强关 / `--predict2` 强开），否则听偏好（偏好写过 "1" 才开）。</summary>
    private void ApplyPredictTailSetting()
        => PredictTailEnabled = !_noPredictArg
           && (_forcePredictArg || GetUiPref(PredictPrefKey) == "1");

    /// <summary>
    /// 自检用：把"压感粗细"的偏好**重新应用一次**——模拟"重开软件"里读偏好那一步。
    /// 自检模式启动时根本不读盘（见 LoadUiPrefs 那段），所以偏好往返必须靠这一条补上。
    /// </summary>
    internal void ApplyPressurePrefForTest()
        => PressureWidth.Enabled = !_noPressureArg && GetUiPref(PressurePrefKey) != "0";



    /// <summary>
    /// 滑条/点档传进来的目标粗细（逻辑像素）。**2026-10-09 晚起：一律吸附到"最近的一档"**——
    /// 滑条拖动不再产生中间值（1 号和 2 号之间没有 1.3 这种），拿到的就是档表里的数。
    /// 档表见 WidthPresets / HighlighterWidthPresets / LaserWidthPresets /
    /// EraserRadiusPresets / PixelEraserWidthPresets。
    /// </summary>
    internal void SetWidthFromUi(float logicalPx)
    {
        // 外层的 0.5～64 只是"别把明显离谱的值放进来"的兜底；**真正的范围按工具算**。
        // 这个 64 曾经把面积橡皮卡住过：它的横边要能到 160（一块大橡皮），
        // 被外层夹在 64 之后，"拖到最右"只能到 64（--paneltest 当场抓到）。
        float v = Math.Clamp(logicalPx, 0.5f, PixelEraserMaxWidth);

        // **按当前工具路由**。以前只有"荧光笔 / 激光 / 其它"三支，
        // 两种橡皮全落进"其它"→ 改的是笔宽（界面上的橡皮滑条是个摆设）。
        switch (Tool)
        {
            case Tool.Highlighter:
            {
                int i = NearestIndex(HighlighterWidthPresets, v);
                HighlighterWidthIndex = i;
                HighlighterWidthLogical = HighlighterWidthPresets[i];
                SetUiPref("w3.hl", i.ToString());
                SetUiPref("wv.hl", HighlighterWidthLogical.ToString("0.##"));
                break;
            }

            case Tool.Laser:
            {
                int i = NearestIndex(LaserWidthPresets, v);
                LaserWidthIndex = i;
                LaserWidthLogical = LaserWidthPresets[i];
                SetUiPref("w3.laser", i.ToString());
                SetUiPref("wv.laser", LaserWidthLogical.ToString("0.##"));
                break;
            }

            case Tool.PixelEraser:
            {
                // 像素橡皮改的是**那一块橡皮的横边**（高 = 横边 × 黄金比）。
                float cv = Math.Clamp(v, PixelEraserMinWidth, PixelEraserMaxWidth);
                int i = NearestIndex(PixelEraserWidthPresets, cv);
                PixelEraserWidthIndex = i;
                PixelEraserWidthLogical = PixelEraserWidthPresets[i];
                SetUiPref("w3.pixel", i.ToString());
                SetUiPref("wv.pixel", PixelEraserWidthLogical.ToString("0.##"));
                break;
            }

            case Tool.Eraser:
            {
                // 整笔橡皮改的是**落点半径**（碰到哪儿就删哪一条）；固定 5 档、吸附。
                float cv = Math.Clamp(v, EraserRadiusMin, EraserRadiusMax);
                int i = NearestIndex(EraserRadiusPresets, cv);
                EraserRadiusIndex = i;
                EraserRadiusLogical = EraserRadiusPresets[i];
                break;
            }

            default:
            {
                int i = NearestIndex(WidthPresets, v);
                WidthPresetIndex = i;
                PenWidthLogical = WidthPresets[i];
                SetUiPref("w4.pen", i.ToString());
                SetUiPref("wv.pen", PenWidthLogical.ToString("0.##"));
                break;
            }
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>离 v 最近的那个档值（档表都非空）。</summary>
    private static float Nearest(float[] table, float v)
    {
        float best = table[0], bestD = float.MaxValue;
        foreach (var x in table)
        {
            float d = MathF.Abs(x - v);
            if (d < bestD) { bestD = d; best = x; }
        }
        return best;
    }

    private static int NearestIndex(float[] table, float v) => Array.IndexOf(table, Nearest(table, v));

    /// <summary>迁移老配置的一个档位下标：老表取值 → 吸附进新表（见 LoadUiPrefs 的调用处）。</summary>
    private bool MigrateWidthPref(string key, float[] oldTable, float[] newTable, ref int index, ref float value)
    {
        if (!int.TryParse(GetUiPref(key), out int oldIdx) || oldIdx < 0 || oldIdx >= oldTable.Length) return false;
        value = Nearest(newTable, oldTable[oldIdx]);
        index = NearestIndex(newTable, value);
        return true;
    }

    internal void UndoFromUi()
    {
        ExitReplayForEdit("撤销");
        Doc.Undo();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void RedoFromUi()
    {
        ExitReplayForEdit("重做");
        Doc.Redo();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **「清除全部墨迹」**（2026-10-14 用户定：一个动作、三个场景、一个名字）：
    ///   · **PPT**：这份演示文稿**所有页**的批注（含盘上缓存；不动桌面板书、不动"播到第几页"）；
    ///   · **文档（PDF/图片）**：这份文档**所有页**的批注（含盘上缓存；不动桌面板书）；
    ///   · **白板 / 桌面（当前这一槽）**：整张画布（含翻过的屏）——只清当前所在的那一槽，另一槽不动。
    /// 三个场景都**可撤销**（一步回到清空前）。2026-10-25 起**只剩翻页条菜单**这个入口：
    /// 橡皮带的「清空墨迹」只清当前页（见 <see cref="ClearCurrentPageFromUi"/>）。
    /// </summary>
    internal void ClearAllInkFromUi()
    {
        ExitReplayForEdit("清除全部墨迹");

        // **白板优先**：白板盖在文档 / PPT 上时，清空仍然只能清白板槽。
        // 以前这里先看 PptMode / DocView，结果白板清屏会清到底下的文档或 PPT。
        if (BoardOn)
        {
            int n = Doc.Strokes.Count;
            Doc.Clear();
            // 清完整槽后回第 1 屏；否则会停在一个已经没有内容的空屏上。
            ViewOffsetY = 0f;
            _pageScroll[InkDocument.BoardPageKey] = 0f;
            _camAnimating = false;
            _dirty = true;
            Console.WriteLine(n > 0
                ? $"[白板] 已清空整个白板槽（{n} 个对象；可撤销，文档/PPT 不受影响）"
                : "[白板] 清空白板：本来就没有墨迹");
        }
        else if (PptMode)
        {
            ClearAllPptMarks();          // 所有页 + 连盘删；可撤销（见 ClearSlidesAction）
        }
        else if (DocView.IsOpen)
        {
            ClearDocMarks();             // 整份文档 + 连盘删；Doc.Clear() 本来就可撤销
        }
        else
        {
            int n = Doc.Strokes.Count;
            Doc.Clear();
            Console.WriteLine(n > 0
                ? $"[板书] 已清除全部墨迹（{n} 个对象；可撤销）"
                : "[板书] 清除全部墨迹：本来就没有墨迹");
        }
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 「清空墨迹」：只清**当前页 / 当前幻灯片 / 当前这一屏**。
    ///
    /// 和 <see cref="ClearAllInkFromUi"/> 的分工（2026-10-25 用户定）：
    ///   · 橡皮带里的按钮、`Ctrl+Shift+C` → 这里，只清眼前这一页；
    ///   · 翻页条菜单的「清除全部墨迹」→ 那份，整份文档 / 演示文稿 / 整块板书。
    ///
    /// 三个场景都一步撤销。文档和 PPT 的盘上缓存只动当前页：
    ///   · PPT 用 <see cref="PptStore.DeletePage"/>，不碰别的幻灯片；
    ///   · 文档是一份文件，清完当前页后把剩下的页**立即写回**（`force`），
    ///     不能整份删，否则别的页也没了。
    /// </summary>
    internal void ClearCurrentPageFromUi()
    {
        ExitReplayForEdit("清空墨迹");

        int n = 0;
        if (BoardOn)
        {
            n = Doc.ClearRect(ClearScreenRect());
        }
        else if (PptMode)
        {
            int key = Doc.PageKey;
            n = Doc.Strokes.Count;
            if (n > 0)
            {
                Doc.Clear();
                // 0 号页是桌面批注，不属于演示文稿，不删盘。
                if (key != 0 && _pptKey.Length > 0) PptStore.DeletePage(_pptKey, key);
            }
        }
        else if (DocView.IsOpen)
        {
            var vp = ViewportCanvas;
            int idx = DocView.CurrentIndex(vp.MinY, vp.MaxY);
            if (idx < 0) idx = DocView.FirstAtOrAfter(vp.MinY);
            if (idx >= DocView.Count) idx = DocView.Count - 1;
            var page = DocView.At(idx);
            if (page != null)
            {
                n = Doc.ClearRect(page.Rect);
                if (n > 0) SaveDocInk(force: true);
            }
        }
        else
        {
            // 桌面批注：当前这一屏，和没有白板时的“屏页”一致。
            n = Doc.ClearRect(ClearScreenRect());
        }

        LastClearSummary = $"{DateTime.Now:HH:mm:ss} 清空 {n} 个对象"
            + $"（屏 {ScreenIndex}，视口 y {ViewOffsetY:F0}）";
        Laser.Clear();
        _dirty = true;
        SetInkStatus(n > 0
            ? $"已清空当前页（{n} 个对象），Ctrl+Z 可撤销"
            : "当前页没有墨迹");
        Console.WriteLine(n > 0
            ? $"[清空] 当前页/屏：{n} 个对象（可撤销）"
            : "[清空] 当前页/屏：本来就没有墨迹");
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 当前“屏页”在画布上的矩形：白板/桌面按 <see cref="ScreenIndex"/> 取一屏。
    /// 文档和 PPT 不用它（各有自己的页矩形 / 当前页）。
    /// </summary>
    private RectF CurrentScreenRect()
    {
        int idx = Math.Max(1, ScreenIndex);
        float top = _virtualY + (idx - 1) * PageHeightCanvas;
        return new RectF
        {
            MinX = _virtualX,
            MinY = top,
            MaxX = _virtualX + _virtualW,
            MaxY = top + PageHeightCanvas,
        };
    }

    /// <summary>
    /// 「清墨迹」真正要清的范围：**当前屏页 ∪ 当前视口**。
    ///
    /// 为什么不能只清 <see cref="CurrentScreenRect"/>：
    /// 白板允许停在两屏之间（滚到一半），屏幕最下方露出来的是下一屏的一条。
    /// 这时只清“当前屏”，那条墨会被留在屏幕底下——用户看到的就是
    /// “清屏没清干净，底部还有一条”，只能再拿橡皮补一下。
    /// 2026-11-02 用户日志里 10:49–10:53 画了 17 笔、清屏只清了 13 个对象，
    /// 和这个边界对得上。
    ///
    /// 取并集只多清**屏幕上真的看得见**的那一条；屏幕外的下一屏内容不动。
    /// </summary>
    private RectF ClearScreenRect()
    {
        var page = CurrentScreenRect();
        var view = ViewportCanvas;
        return new RectF
        {
            MinX = MathF.Min(page.MinX, view.MinX),
            MinY = MathF.Min(page.MinY, view.MinY),
            MaxX = MathF.Max(page.MaxX, view.MaxX),
            MaxY = MathF.Max(page.MaxY, view.MaxY),
        };
    }

    internal void QuitFromUi() => _quit = true;

    // ---- 浮层主题（选中操作条 / 小面板用它画）-----------------------------

    /// <summary>
    /// 浮层主题。界面 `Attach` 时推上来（<see cref="IUiHost.SetFloatingTheme"/>），
    /// 没界面挂上来时用 <see cref="UiTheme.Default"/> 兜底。
    ///
    /// 它和界面自己那颗工具条共用一套数字（都出自 `InkUi.Tokens`），
    /// 所以"操作条和工具条是一家"这句话在代码上也有了着落，而不是靠两处各写一份。
    /// </summary>
    internal UiTheme FloatingTheme = UiTheme.Default;

    /// <summary>界面把浮层主题推上来了。主题换了要立刻重画（浮层在这一帧就变样）。</summary>
    internal void SetFloatingThemeFromUi(UiTheme theme)
    {
        FloatingTheme = theme;
        _dirty = true;
    }

    // ---- 界面偏好的存取（引擎只当仓库，不解释内容）----------------------

    /// <summary>界面来问一条偏好。没有就返回 null（界面自己知道默认值）。</summary>
    internal string GetUiPref(string key)
        => key != null && UiPrefs.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// 界面记一条偏好。传 null 表示"回到默认"，那就把这一项**删掉**——
    /// 配置文件里只留和默认不一样的项，以后默认值改了，老配置不会把新默认顶掉。
    /// </summary>
    internal void SetUiPref(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (value == null)
        {
            if (UiPrefs.Remove(key)) _uiPrefsDirty = true;
            return;
        }
        if (UiPrefs.TryGetValue(key, out var old) && old == value) return;
        UiPrefs[key] = value;
        _uiPrefsDirty = true;
    }

    /// <summary>把键位与界面偏好写盘（只在真的改过时才写）。自检模式一律不写。</summary>
    internal void FlushSettings()
    {
        if (SelfCheckMode) return;
        // 从备份恢复之后：内存里还是旧设置，这一轮**不许再写盘**，
        // 否则老师随手改一个开关就会把刚恢复的整份设置覆盖回去。重启后新设置才生效。
        if (_settingsRestored) return;
        if (!Keys.Dirty && !_uiPrefsDirty) return;
        InkSettings.Save(Keys, UiPrefs, UpdateFeed.Url);
        _uiPrefsDirty = false;
    }

    /// <summary>自检用：**无视自检模式的禁令**，立刻把设置写盘（用来验证"记得住"这条链子）。</summary>
    internal void SaveSettingsForTest() => InkSettings.Save(Keys, UiPrefs, UpdateFeed.Url);

    /// <summary>
    /// 自检用：把内存里的界面偏好清空、**从文件重读**。
    /// 不这么做的话，"重开界面读回了偏好"其实读的是内存里那份，等于没验到落盘。
    /// </summary>
    internal void ReloadUiPrefsForTest()
    {
        UiPrefs.Clear();
        foreach (var w in InkSettings.LoadUiPrefs(UiPrefs))
            Console.WriteLine("settings: " + w);
    }

    /// <summary>
    /// 界面上那个"重启"：给老师一个"感觉不对就重开一次"的出口
    /// （教室大屏 + 手写板的机器上可能没有键盘，界面是唯一入口）。
    ///
    /// **语义 = 像电脑重启：板书不接回来**（用户 2026-09-22 定）。走的是真拉新进程 + 自己退出，
    /// 所以内存（含驱动内部缓冲那约 100 MB）全部还给系统；新进程起来是一块**干净白板**。
    ///
    /// 和"界面崩了自己重建"那条路（<see cref="RestartNow"/>）**刻意不同**，别把两处一起改：
    ///   · 这里：老师**主动**点的，他要的就是"重来一次"；
    ///   · 那里：老师没要求、App 自己决定重启，**丢了板书是事故**——所以那边照旧
    ///     写会话暂存、启动时读回来。
    ///
    /// 两个文件的分工别搞混（见 Recovery）：
    ///   · 会话暂存（TEMP，读走就删）="重启不丢东西"，这里**明确不写**，还要把残留删掉；
    ///   · 自动存档（LOCALAPPDATA，读走不删）="盘上留一份"，这里照写——
    ///     屏幕上是干净白板，但那份板书还在盘上，需要时能找回来（体感像电脑重启：
    ///     桌面是干净的，硬盘上的文件还在）。
    /// </summary>
}
