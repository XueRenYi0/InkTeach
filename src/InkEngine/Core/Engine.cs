// [职责] 引擎核心状态、自动存档、板书历史、主循环字段（主文件）。
// [测试] --recoverytest / --unittest

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>框选工具下拖空白处的选择方式（`Ctrl+Alt+9` 切）。见 InkEngine.SelMode。</summary>
/// <summary>
/// 框选工具下的两种选择方式。<b>public</b>：它是界面契约的一部分
/// （界面要能"切成套索"，见 <see cref="IEngineCommands.SetSelectMode"/>）。
/// </summary>
public enum SelectMode
{
    /// <summary>矩形框选：碰到墨就算选中（OneNote 的语义）。</summary>
    Rect = 0,
    /// <summary>自由套索：代表点 80% 落在圈里才算选中（WPF 的语义）。</summary>
    Lasso = 1,
}

internal enum PassThroughMode
{
    /// <summary>WM_NCHITTEST -> HTTRANSPARENT only (safe, but the documented
    /// hand-off only reaches windows owned by the same thread).</summary>
    HitTest = 0,

    /// <summary>Adds WS_EX_TRANSPARENT to the window style on top of HitTest.</summary>
    ExTransparent = 1,

    /// <summary>Adds WS_EX_LAYERED | WS_EX_TRANSPARENT (the classic click-through
    /// recipe). Kept as a probe because it may disturb DirectComposition.</summary>
    LayeredTransparent = 2,
}

/// <summary>
/// 引擎本体。部分实现按职责拆在别的文件里（`Ppt.cs` = PPT 放映联动）。
/// </summary>
public partial class InkEngine
{
    // ---- document / interaction state ------------------------------------
    internal readonly InkDocument Doc = new();
    internal readonly LaserTrail Laser = new();

    /// <summary>
    /// 文档页底层（图片 / PDF 导入后"一叠页"的那一层）。**不是笔迹**：
    /// 不参与存档、导出、回放、撤销（见 <see cref="DocPages"/> 的说明）。
    /// 没打开文档时它是空的——不调用 = 零开销。
    /// </summary>
    internal readonly DocPages DocView = new();

    /// <summary>主循环线程 id（后台线程叫醒用；<see cref="Loop"/> 第一句填）。</summary>
    private volatile uint _mainThreadId;

    /// <summary>启动后清一次旧版备份目录（只跑一次，后台线程）。</summary>
    private bool _oldBackupCleanupStarted;

    /// <summary>后台渲完一页的"叫醒"标记（渲染线程写、主循环读）。</summary>
    private volatile bool _docPageReady;

    /// <summary>
    /// 后台线程（文档页渲染）渲完一页 → 置标记 + 一条线程消息，把主循环从 `WaitMessage()`
    /// 里踢醒。**必须叫醒**：空闲时引擎不给帧，不叫就是"页渲好了、屏幕上还空着"。
    /// （和 PPT 轮询线程唤醒主循环同一套做法，见 PptWatcher。）
    /// </summary>
    private void WakeForDocPage()
    {
        _docPageReady = true;
        try
        {
            if (_mainThreadId != 0)
                Native.PostThreadMessage(_mainThreadId, PptWatcher.WakeMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* 有意忽略：读取失败走默认值（ADR-005 D5） */ }
    }

    /// <summary>主循环每拍：后台来的页到了就置脏，下一拍走渲染（在 SyncDocPages 里采纳）。</summary>
    private void StepDocPageReady()
    {
        if (!_docPageReady) return;
        _docPageReady = false;
        _dirty = true;
    }

    /// <summary>
    /// 界面自己的偏好（深色主题、贴边隐藏、档位、钉住）。引擎**只存不解释**：
    /// 它不知道"极简档"是什么，界面说存什么就存什么。落盘在 settings.json 的 `ui` 段。
    /// </summary>
    internal readonly Dictionary<string, string> UiPrefs = new();
    private bool _uiPrefsDirty;

    /// <summary>自检/基准模式：**不读也不写**用户配置（判据要确定，更不能改用户的设置）。</summary>
    /// <summary>
    /// 这个参数**是开关、不是自检 mode** 吗？是的话不该把程序带进自检模式。
    ///
    /// 只列"用户可能会单独敲、而且确实不是 mode"的那些。
    /// 真敲错 mode 名（比如 `--inputtset`）仍按老规矩进自检模式 —— 那正是自检模式存在的意义。
    /// </summary>
    private static bool IsSwitchNotMode(string a) => a switch
    {
        "--norawinput" or "--nopressure" or "--rawprobe" or "--notrend"
            or "--himetric" or "--nohimetric" or "--notouch" or "--syswet" or "--ownwet" or "--strokefile"
            or "--inktrail" or "--noinktrail" or "--trailonly" or "--trailcommit"
            or "--hud" or "--nohud" or "--fullpresent" or "--warp"
            or "--pdflowmem" or "--pdfhighmem"
            or "--recink" or "--recinkp" or "--reclive"
            or "--doc" or "--startup"
            or "--nosmooth" or "--printersafe"
            or "--mean2gauss" or "--mean2guniform" or "--mean2gsigma" or "--mean2gfast"
            or "--mean2glo" or "--mean2ghi" or "--mean2gtip"
            or "--predict2" or "--nopredict2" or "--syscursor" or "--nosyscursor" => true,
        _ => false,
    };

    internal bool SelfCheckMode;

    /// <summary>
    /// 本次进程由开机自启拉起（命令行带 `--startup`）。产品启动态：
    /// 穿透先开着、工具条收成小球、不抢焦点；手动双击仍然是画笔 + 展开。
    /// </summary>
    internal bool StartupMode;

    /// <summary>第一次退出穿透时还要把后台待命的小球展开（只做一次）。</summary>
    private bool _startupPassive;

    /// <summary>开机自启当前真实生效（HKCU Run 有值且未被任务管理器禁用）。</summary>
    internal bool AutoStartOn;

    /// <summary>启动时拿到的那个参数（`args[0]`）。只给"自检模式"那行诊断用。</summary>
    private string _modeName = "";

    /// <summary>那行诊断只打一次。</summary>
    private bool _selfCheckLogged;

    // ---- 自动存档（崩溃恢复）------------------------------------------------
    //
    // 产品里**没有"保存"这个动作**，所以按"持久画布"来做：每 15 秒（板书变了才写）
    // 存一份到 %LOCALAPPDATA%，下次打开自动接上。
    // 为什么值得：一节课的板书丢了是最坏的失败模式；同类开源软件（Xournal++）
    // 正因为"崩溃就丢"被用户提了严重数据丢失的 issue。

    /// <summary>自动存档的间隔（毫秒）。板书没变就不写。</summary>
    private double _autoSaveEveryMs = 15000;
    private double _nextAutoSaveAtMs;
    private long _autoSavedVersion = -1;       // PPT 页的"没变不写"（放映分支还在用）
    private long _autoSavedBoardFp = -1;       // 白板槽的指纹（自动存档"没变不写"的判据）

    /// <summary>自检用：这一场跑下来自动存档写了多少次（验证"没变就不写"）。</summary>
    internal int AutoSaveCount { get; private set; }

    /// <summary>自检用：把自动存档间隔调短（不然得等 15 秒才验得到节流）。</summary>
    internal void SetAutoSaveIntervalForTest(double ms)
    {
        _autoSaveEveryMs = ms;
        _nextAutoSaveAtMs = 0;
    }

    /// <summary>自检用：走一遍"启动时接上上次板书"这条真路径（含坏文件容错）。</summary>
    internal void RestoreAutoSaveForTest() => RestoreAutoSaveIfAny();

    /// <summary>
    /// **启动时要不要接上上次的板书**（`ui` 段里的 `restoreInk`）。**默认关**。
    ///
    /// 用户 2026-09-17："退出以后再打开不用恢复墨迹吧……后期可以设置，但是我觉得
    /// 默认不恢复墨迹。" 理由写在 <see cref="RestoreAutoSaveIfAny"/> 里：教室机器是
    /// 公用的，一开机就铺满上一节课的板书不合理。
    ///
    /// 它同时管**写**：不读的东西不必写（见 <see cref="MaybeAutoSave"/>）。
    /// </summary>
    internal bool RestoreInkOnStartup => GetUiPref("restoreInk") == "1";

    /// <summary>自动存档 / 会话暂存的素材：**只包含白板槽**（桌面批注不落盘，2026-10-16 用户定）。</summary>
    private List<Stroke> BoardForSave()
        => Doc.StrokesOf(InkDocument.BoardPageKey) ?? new List<Stroke>();

    /// <summary>自检/退出用：立刻把**白板槽**写一份自动存档（不看间隔）。</summary>
    internal void AutoSaveNow()
    {
        try { Recovery.SaveAuto(InkSerializer.SaveStrokes(BoardForSave())); }
        catch (Exception ex) { Console.WriteLine("自动存档失败：" + ex.Message); }
        _autoSavedBoardFp = BoardFingerprint(Doc.StrokesOf(InkDocument.BoardPageKey));
        AutoSaveCount++;
    }

    // ---- 板书历史（**白板槽**的快照；见 BoardHistory，2026-10-14 加、10-16 改槽）----
    //
    // 和"自动存档"的分工：自动存档只有**一份**、而且**受"自动恢复上次板书"偏好管辖**
    //（默认关着 = 连写都不写）；板书历史**不看那个偏好**——它是"老师回头能找回自己写过的东西"，
    // 留存是留存、显示是显示（默认都不显示）。上限与轮转在 BoardHistory 里。
    /// <summary>最小间隔：两次快照至少隔这么久（停顿门槛之外的兜底，防"写两笔停一下"频繁写盘）。</summary>
    private const double HistoryMinGapMs = 10000;
    /// <summary>抬笔后至少静止这么久才允许写快照（写字期间一次都不碰盘）。</summary>
    private const double HistoryIdleMs = 1500;

    private double _nextHistoryAtMs = HistoryMinGapMs;
    private long _historyFingerprint = -1;
    private bool _historyLogged;
    /// <summary>最后一次抬笔的时刻（"手停了没有"的判据）。</summary>
    private double _lastStrokeEndMs = double.NegativeInfinity;

    /// <summary>自检用：立刻写一份板书历史（不看间隔与指纹；同步落盘——测试要立刻数文件）。</summary>
    internal bool SnapshotBoardHistoryForTest()
        => WriteBoardHistory(Doc.StrokesOf(InkDocument.BoardPageKey), sync: true);

    /// <summary>
    /// 每帧过一趟：**手停下来之后**（抬笔 ≥1.5 秒、且距上次 ≥10 秒）、而且板书画布真的变了
    /// （按指纹判）才写一份。挂在 RenderAll 里（和自动存档同一处），自检那条路也照样验得到。
    ///
    /// ⚠ **为什么不"每 15 秒无条件写"**（用户 2026-10-14 提的卡顿风险，实测口径）：
    /// 一份快照 ≈ 0.1~3 MB（不含图片），序列化 + 落盘在 UI 线程上约 5~20ms——
    /// 正好吃掉一帧（16ms 预算）。落在写字中间就是一次可见的卡顿。
    /// 所以：**写字期间一次都不写**；停手说话的那一下才写（那时掉一帧没人看得出来）。
    /// 崩溃前"一直没停过手"的那一小段靠退出/重启时那一份兜底。
    /// </summary>
    private void MaybeHistorySnapshot()
    {
        if (NowMs < _nextHistoryAtMs) return;
        if (_drawing || ActiveStroke != null) return;
        if (NowMs - _lastStrokeEndMs < HistoryIdleMs) return;
        _nextHistoryAtMs = NowMs + HistoryMinGapMs;
        var board = Doc.StrokesOf(InkDocument.BoardPageKey);   // 白板槽（桌面批注不进历史，2026-10-16 用户定）
        long fp = BoardFingerprint(board);
        if (fp == _historyFingerprint) return;
        if (WriteBoardHistory(board, sync: false)) _historyFingerprint = fp;
    }

    /// <summary>
    /// 板上内容的指纹（条数 + 总点数）。只用来判"值不值得再写一份"，不追求精确——
    /// 增删一笔、擦掉一段都会变；真判错了最多多写一次（覆盖写，代价有界）。
    /// </summary>
    private static long BoardFingerprint(List<Stroke> board)
    {
        if (board == null || board.Count == 0) return 0;
        long fp = board.Count;
        foreach (var s in board) fp = fp * 131 + s.Points.Count;
        return fp;
    }

    /// <summary>
    /// 把**白板槽**写成一份历史快照（2026-10-16 起白板独立；桌面批注不进历史）。
    /// **图片一律不要**：截图那种对象整条跳过（用户 2026-10-14 定）。
    /// 空板书且这一轮还没写过 → 不留空文件（免得开一下软件就多一份空的）。
    /// `sync`（2026-10-15）：周期快照走**后台写**（false）；退出前那份必须落完（true）。
    /// </summary>
    private bool WriteBoardHistory(List<Stroke> board, bool sync)
    {
        var list = new List<Stroke>();
        if (board != null)
            foreach (var s in board)
                if (!s.IsImage) list.Add(s);

        if (list.Count == 0 && !BoardHistory.HasSession) return false;
        try
        {
            var blob = InkSerializer.SaveStrokes(list);
            string path = sync ? BoardHistory.Save(blob) : BoardHistory.SaveDeferred(blob);
            if (path == null) return false;
            if (!_historyLogged && list.Count > 0)
            {
                _historyLogged = true;
                Console.WriteLine($"[板书历史] 开始在后台留快照（{list.Count} 个对象；"
                                  + "「更多 → 墨迹 → 历史板书」可找回）");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("板书历史失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 到了间隔、而且板书真的变了，就写一次。
    /// 判据用文档版本号（`Doc.Version`）——它在每次增删改时都会加一，
    /// 比"每 15 秒无条件写一遍"省得多（一万笔的全量序列化不是白给的）。
    ///
    /// **偏好关着就一次都不写**：默认档不读那个文件（见 <see cref="RestoreAutoSaveIfAny"/>），
    /// 写一个没人读的文件只是白白序列化 + 在用户的盘上留一份板书。
    /// </summary>
    private void MaybeAutoSave()
    {
        if (!RestoreInkOnStartup) return;
        if (NowMs < _nextAutoSaveAtMs) return;

        // 2026-10-15（低配机"书写偶尔迟缓"）：**只挑书写间隙写**——到点时手还在写、
        // 或刚停笔（<1.5s）就先不写（不重置间隔，下一帧继续判，手一停就会补上）。
        // 在写的过程中序列化整份板书 = 一次可感知的顿挫；这是"随板书大小线性变长"的
        // 少数几处主线程工作之一。序列化仍要碰 Doc（只能主线程），**写盘走后台上**
        //（见 AutoSaveDeferred / BackgroundSaver）。
        if (_drawing || ActiveStroke != null) return;
        if (NowMs - _lastStrokeEndMs < HistoryIdleMs) return;

        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;

        // PPT 模式：**改成写当前这一页到 PPT 自己的目录**，不写自动存档。
        // 为什么：自动存档是"白板那一份"，把某一页 PPT 的内容写进去就串了
        //（那是另一条路的数据）。PPT 页本来就有自己的落盘通道（翻页时、退出时各一次），
        // 这里补上"放映中途"那一段（老师在一页上写很久、还没翻页就崩了，内容不至于丢）。
        if (PptMode)
        {
            if (Doc.Version == _autoSavedVersion) return;
            SaveCurrentPptPage(deferred: true);
            _autoSavedVersion = Doc.Version;
            AutoSaveCount++;
            return;
        }

        // 非放映：**自动存档 = 白板槽**（桌面批注不落盘——2026-10-16 用户定）。
        // 指纹判"变没变"：在桌面 / 文档页写字而白板槽没动的，一次都不写
        //（以前按全文档版本号判，在文档页写两笔也会顺手把白板存档重写一遍）。
        long fp = BoardFingerprint(Doc.StrokesOf(InkDocument.BoardPageKey));
        if (fp == _autoSavedBoardFp) return;
        AutoSaveDeferred();
        _autoSavedBoardFp = fp;
    }

    /// <summary>周期自动存档（2026-10-15 加）：序列化在主线程（碰 Doc），**写盘走后台上**。
    /// 退出 / 重启那两处仍走同步的 <see cref="AutoSaveNow"/>（必须落完才算数）。
    /// 2026-10-16 起**只存白板槽**。</summary>
    private void AutoSaveDeferred()
    {
        try { Recovery.SaveAutoDeferred(InkSerializer.SaveStrokes(BoardForSave())); }
        catch (Exception ex) { Console.WriteLine("自动存档失败：" + ex.Message); }
        _autoSavedBoardFp = BoardFingerprint(Doc.StrokesOf(InkDocument.BoardPageKey));
        AutoSaveCount++;
    }

    // ---- 历史清理（墨迹 B）：启动后**首个空闲帧**做一次 ----------------------
    //
    // 为什么不在启动那一刻做：老师按下图标到窗口出现之间的时间要尽量短；
    // 扫我们自己的两个小目录虽然是毫秒级，也不值得占在"开机"那一下。
    // 为什么挂在 WM_TIMER（250ms 那口钟）上：它本来就在滴答，借一次就够了。
    private bool _historySwept = true;          // 自检/无窗口模式默认"已扫过" = 永远不扫
    private double _historySweepAtMs = double.MaxValue;

    /// <summary>自检用：直接按给定保留期扫一次（0 = 永久，一份都不删）。</summary>
    internal (int Files, int Dirs) SweepHistoryForTest(int days)
        => days <= 0 ? (0, 0) : InkHistory.Sweep(days);

    private void MaybeSweepHistory()
    {
        if (_historySwept || NowMs < _historySweepAtMs) return;
        _historySwept = true;
        int days = InkHistory.RetentionDays(GetUiPref("historyDays"));
        if (days <= 0) return;                  // 永久：一份都不删
        var (files, dirs) = InkHistory.Sweep(days);
        if (files > 0 || dirs > 0)
            Console.WriteLine($"[墨迹] 历史清理（保留 {days} 天）：删了 {files} 个文件、{dirs} 个空目录");
    }

    internal Stroke ActiveStroke;

    /// <summary>
    /// `--syswet`：这一笔的湿墨由系统轨迹负责（渲染时不再自绘当前笔画）。
    /// 用**引用相等**认笔画，收笔/换笔都不会串。
    /// </summary>
    private Stroke _systemWetStroke;
    /// <summary>Overlay 渲染判据：这一笔是不是"由系统轨迹画湿墨"。</summary>
    internal bool IsSystemWetStroke(Stroke s) => s != null && ReferenceEquals(s, _systemWetStroke);
    internal Tool Tool = Tool.Pen;
    /// <summary>Tool sizes are authored in logical pixels and scaled by the
    /// monitor DPI at use. Without this everything looks half-size on a 150%
    /// display, which is exactly how a 18px eraser turns into an unusable dot.</summary>
    internal float DpiScale = 1f;
    /// <summary>界面缩放档（见 <see cref="UiScalePresets"/>；1 = 标准）。物理 = 逻辑 × DpiScale × UiScale。</summary>
    internal float UiScale = 1f;
    /// <summary>界面：一个逻辑单位 = 多少物理像素。**界面相关的换算一律用它**，别再用裸 DpiScale。</summary>
    internal float UiUnit => DpiScale * UiScale;

    /// <summary>
    /// **选中家具（手柄 / 操作条 / 面板 / 读数标签）的画布单位** = <see cref="UiUnit"/> ÷ 相机缩放。
    ///
    /// 为什么要有它：这些家具的位置是画布坐标（跟着墨迹走），但**大小必须恒定在屏幕上**——
    /// 文档放大到 200% 时，手柄还应该是 10 逻辑像素，不是 20。画布单位里补一个 ÷ViewScale
    /// 正好抵消相机那一层放大，于是"位置随内容、大小随屏幕"。
    ///
    /// **只有这一份**：绘制、命中、拖动换算（`SelectionHandles.UiBox` 的最小操作框、
    /// 最小尺寸下限）全都问它，任何一处用裸 <see cref="UiUnit"/> 都会在缩放下分家
    /// （画在一个地方、点不到那个地方）。100% 时它 == UiUnit，行为一个像素都不变。
    /// </summary>
    internal float FurnitureDpi => UiUnit / ViewScaleSafe;

    internal float EraserRadiusLogical = 22f;
    /// <summary>
    /// 整笔橡皮的落点半径（逻辑像素）的档位。**以前它根本没有档位**——
    /// 界面上给橡皮画了粗细滑条，可引擎里 `SetWidthFromUi` 把它归到"其它"那一支，
    /// 结果改的是**笔宽**：老师拖橡皮的滑条，笔迹粗细悄悄变了、橡皮一点没变。
    /// 现在四种工具各记各的。**2026-10-09 晚用户定：固定档位、滑动吸附在档上**
    /// （不像笔那样铺开很多档——橡皮就几个大小，拖到哪都落在最近的档）。
    /// **2026-11-03**：两种橡皮各加了一个"精细档"（整笔 6 / 像素 14），放在最前面。
    /// </summary>
    internal static readonly float[] EraserRadiusPresets = { 6f, 12f, 17f, 22f, 28f, 34f };
    internal int EraserRadiusIndex = 3;   // 默认还是 22（2026-11-03 前面加了一档 6）
    /// <summary>两种橡皮各自的可调范围（逻辑像素）。界面滑条的范围要和这里一致。
    /// 2026-11-03：各加了一个"精细档"，下限跟着降（像素橡皮 30 → 12）。</summary>
    internal const float EraserRadiusMin = 4f, EraserRadiusMax = 48f;
    internal const float PixelEraserMinWidth = 12f, PixelEraserMaxWidth = 160f;
    internal float PenWidthLogical = 3f;
    internal float HighlighterWidthLogical = 18f;
    /// <summary>
    /// 激光笔的粗细。以前激光借的是**笔宽**（`Engine.cs` 里那条
    /// `tool == Highlighter ? Highlighter : Pen`），于是"切粗细"对激光没意义、
    /// 落点的点大小也没法跟笔迹对上。现在四种工具各记各的。
    ///
    /// ⚠ 默认档 2026-09-27 从 4 改粗到 8（用户："可以把默认档改粗一点"）。
    /// 为什么当时显得细：那条轨迹是"**细白芯 ＋ 一层淡红晕**"，
    /// 白芯只占 0.32 个直径 → 4 的直径下白芯才 1.3 像素，肉眼基本只剩一点红雾。
    /// 现在比例改成 ClassIn 那种（**宽白芯 ＋ 实红边**，2026-10-18 起撤掉外围柔光，
    /// 见 `Overlay.DrawLaser`），同一档在屏幕上也更"实"、且不再有一圈圈光晕。
    /// </summary>
    internal float LaserWidthLogical = 8f;
    /// <summary>
    /// **笔的线型**（用户 2026-09-19 第 2 件：笔的色带条上要能切虚实线）。
    ///
    /// 和 <see cref="PenWidthLogical"/> 同一个地位：**这支笔自己的设置**。
    /// 只作用于新画出来的**自由笔迹**，而且是"只有笔用"——
    /// 荧光笔的半透明轨迹与激光笔的指示轨迹画成虚线没有意义，图形也不吃它
    /// （图形的线型历来是"选中之后在操作条面板里改"，见 <see cref="SetSelectionDash"/>）。
    ///
    /// 画的时候取当时的值写进对象（见 <see cref="Stroke.Dash"/>），之后各存各的：
    /// 改这个开关**不会**动已经写在板上的东西——和坐标系网格那条一个口径。
    /// </summary>
    internal StrokeDash PenDash = StrokeDash.Solid;
    internal float EraserRadius => EraserRadiusLogical * DpiScale;
    private float _lastEraseX, _lastEraseY;

    // ---- 动态橡皮（8.3.4 起；8.3.6 换形状；8.3.7 定窗口与门槛）--------------
    //
    // 面积擦的尺寸跟着**移动速度**走：**静止/慢 = 你调的那个基准（不缩）**、快 = 最多 2.5 倍。
    // 形状照隔壁 Inkeys「笔速橡皮」定（他们那条本来就是给触屏设备的）：
    //   他们：`speed≤20 → max(25, speed×2.33+13.33)`、`speed>20 → min(200, 3.0×speed)`
    //         → 有**下限 25px**（慢到底不再小）、有**上限 200px**、中间一段线性斜坡；
    //   我们：`factor = clamp(1.0 + (v − 0.80) × 1.0, 1.0, 2.5)`（v 单位物理像素/毫秒）
    //         → 0.80 以下恒 **1.0（基准，一点不缩）**、以上线性涨、2.3 到顶。
    //         **8.3.8**：下限从 0.7 提到 1.0（用户："点击一下不动的时候，橡皮会缩小吗？我调的
    //         初始值应该就是默认大小，点击以后不缩小"）——按住不动时手指/鼠标的**微小抖动**
    //         也会被算成"速度"，于是慢慢滑到 0.7 倍。现在**静止/慢速就是基准**，不缩。
    //
    // 8.3.4 的两个毛病（用户报"慢速下忽大忽小"）8.3.6 都治了：
    //   ① 速度原来用"这一次消息的 dist ÷ dt"——慢速时一次只走一两像素、除以很小的 dt，
    //      估出来的速度天然抖 → 现在**窗口累计**（攒够 6 像素或 30ms 才算一次）；
    //   ② 平滑原来 α=0.35（≈25ms 时间常数，太灵）→ 现在按**时间常数**平滑（涨 120ms /
    //      收 450ms + 最少一步 0.01）——这就是 Inkeys 那个"每步只走差距的 1/50、至少 0.1px"
    //      的同源做法（他们按消息数算、我们按时间算，帧率无关）。
    //   另加**回差**（0.8 涨 / 0.6 回）：速度在门槛附近晃时尺寸不会来回切。
    //
    // **8.3.7（用户报"常规速度下还是忽大忽小"）**：问题不在曲线的数，而是：
    //   ① 窗口按**像素**攒（"攒够 6px"）——常规速度下几毫秒就攒满，等于没有窗口；
    //   ② 门槛 0.35 **比常规擦字速度还低**——等于"一擦就变尺寸"。
    //   现在：窗口**按时间攒满 100ms**、门槛提到 **0.80**（常规速度落在死区里，尺寸纹丝不动）、
    //   平滑再放慢（涨 220 / 收 450），并加 `--eraserhud` 读数——门槛按真机读数定，不靠猜。
    //
    // ⚠ **只作用于面积擦**。"整笔擦"的"大小"是**命中半径**（碰到哪条删哪条），
    //   让半径随速度变 = "点到哪条全看手速"，不可预期，所以整笔擦恒定。
    // ✅ **不分设备**：笔 / 鼠标 / 手指走的是同一段擦除代码，所以手指在触摸屏上抹面积擦照样有。
    // 后门：`--eraserfixed` 关掉动态（不进界面）。
    internal bool DynamicEraser = true;

    /// <summary>最慢/静止时的系数下限 = **1.0（就是基准）**：你调的那个大小就是默认大小，
    /// 按住不动、慢慢抹都不缩（8.3.8 从 0.7 提上来；Inkeys 那边是个 25px 地板，但我们的
    /// 基准是用户自己调的滑条，"静止 = 你调的值"才符合直觉）。</summary>
    internal const float EraserFactorMin = 1.0f;
    /// <summary>最快时的系数上限（基准的 2.5 倍；Inkeys 的对应物是 200px 封顶）。</summary>
    internal const float EraserFactorMax = 2.5f;
    /// <summary>开始涨的门槛（物理像素/毫秒）——**必须高于"常规抹"的速度**（8.3.7 实测口径：
    /// 常规擦字 ≈0.4~0.7、刻意快扫 ≥1.5）。门槛以下恒下限 = "常规速度永远同一尺寸"。</summary>
    internal const float EraserSpeedKnee = 0.8f;
    /// <summary>回差下沿：掉到这以下才回下限（0.6~0.8 之间保持，防门槛附近来回切）。</summary>
    internal const float EraserSpeedBack = 0.6f;
    /// <summary>斜坡斜率：`factor = 下限 + (v − 门槛) × 斜率`（0.8 → 2.3 px/ms 之间涨到顶）。</summary>
    internal const float EraserSpeedSlope = 1.0f;
    /// <summary>到顶的速度（px/ms）：`门槛 + (上限 − 下限) ÷ 斜率`。给读数/文档用。</summary>
    internal static float EraserSpeedTop => EraserSpeedKnee + (EraserFactorMax - EraserFactorMin) / EraserSpeedSlope;
    /// <summary>平滑时间常数（毫秒）：涨得比收得快一点（跟手），但都远慢于手抖的频率。</summary>
    internal const float EraserGrowTauMs = 220f, EraserShrinkTauMs = 450f;
    /// <summary>每步最少挪动的系数（Inkeys 那 0.1px 保底步长的同源做法，保证收得回来）。</summary>
    private const float EraserMinStep = 0.01f;
    /// <summary>
    /// 速度通道（抄 MyPaint 的 **Fine/Gross Speed**：fine 跟手、gross "changes very slowly"）。
    /// 目标速度取两者的**较大值**：快扫时 fine 立刻起作用；慢下来时 gross 还停在旧速度上，
    /// **尺寸不会一慢就塌**——这就是"停住再轻动一下突然变小"的一半解法（另一半是下面的缓释）。
    /// 每个通道"攒够时间或距离就结算一次"。
    /// </summary>
    internal const float EraserFineWindowMs = 40f, EraserFineWindowPx = 12f;
    internal const float EraserGrossWindowMs = 350f, EraserGrossWindowPx = 120f;
    /// <summary>指针停住之后先**保持**多久才开始缓释（免得扫到一半停一下、尺寸就缩）。</summary>
    internal const float EraserIdleHoldMs = 150f;
    /// <summary>"正在减速"时的保持时间（更短 → 提前开始收）。判据 = `fine &lt; gross × 0.7`：
    /// 两个滤波量的差就是**加速度的符号**，噪声被压了两遍——用户想要"加速度"的稳定代理。</summary>
    internal const float EraserDecelHoldMs = 60f;

    private float _eraseSpeedEma;        // 当前用的速度 = max(fine, gross)（物理像素/毫秒）
    private double _eraseLastMs;         // 上一次速度采样时刻
    private float _eraseFine, _eraseGross;                       // 两条速度通道（px/ms）
    private float _eraseFineDist, _eraseFineMs;                  // fine 窗口累计（40ms / 12px）
    private float _eraseGrossDist, _eraseGrossMs;                // gross 窗口累计（350ms / 120px）
    private float _eraseDynFactor = 1f;  // 当前尺寸系数（落笔 = 1）
    private float _eraseTarget = 1f;     // 平滑的目标（回差状态也存这儿）
    private double _eraseHoldBaseMs;     // 上一次**移动**的时刻（缓释的保持期从这里算）
    private bool _eraseDecaying;         // 已经进入"停住缓释"阶段（每帧推；一动就退出）
    private float _eraseTestIdle;        // 自检用：累计的空闲时间

    /// <summary>橡皮诊断浮层（`--eraserhud`）：实时显示 速度 / 目标 / 当前系数 / 尺寸。
    /// 它是**调参工具**：真机上擦几下，读出"常规速度是多少 px/ms"，门槛就按那个数定。</summary>
    internal bool EraserHud;
    internal string EraserHudText = "";
    private double _eraserHudNextMs;

    /// <summary>
    /// 面积擦**正在拖**吗。框只在拖动中跟着速度变；**悬停时显示基准框**——因为落笔第一下
    /// 用的就是基准（速度还没有），所以"悬停看见的 = 按下去第一下擦掉的"，所见即所得。
    /// </summary>
    internal bool PixelEraseDragging;

    /// <summary>
    /// 落点框 = **真正会被擦掉的那一块**（物理像素半宽/半高）。擦除（`EraseRectAlongPath`）
    /// 和 Overlay 画的那个框读的是**同一份**——不然就会出现"看见的框"和"擦掉的范围"对不上。
    /// 拖动中 = 基准 × 速度系数；没在拖 = 基准。
    /// </summary>
    internal float PixelEraserCursorHalfWidthPx
        => PixelEraserHalfWidthPx * (PixelEraseDragging ? _eraseDynFactor : 1f);
    internal float PixelEraserCursorHalfHeightPx
        => PixelEraserHalfHeightPx * (PixelEraseDragging ? _eraseDynFactor : 1f);

    /// <summary>
    /// 像素橡皮的落点尺寸（逻辑像素）：**竖着的黄金比例矩形**，高 : 宽 = 1.618。
    ///
    /// 默认宽 93 × 高 150：用户实测的用法是"从上往下抹一段"（一列板书、一个竖排的字），
    /// 不是横扫一行，所以竖边比横边长。Ctrl+Alt+6 在这里的几档之间切（档位同样守黄金比）。
    /// </summary>
    internal const float GoldenRatio = 1.618f;
    internal float PixelEraserWidthLogical = 93f;
    /// <summary>像素橡皮六档（2026-10-09 晚用户定：固定档位、滑动吸附；
    /// 2026-11-03 前面加了一档 14 用来切细线）。默认 93（下标 3）。</summary>
    internal static readonly float[] PixelEraserWidthPresets = { 14f, 46f, 70f, 93f, 122f, 150f };
    internal int PixelEraserWidthIndex = 3;   // 默认还是 93（前面加了一档 14）
    internal float PixelEraserHeightLogical => PixelEraserWidthLogical * GoldenRatio;
    internal float PixelEraserHalfWidthPx => PixelEraserWidthLogical * 0.5f * DpiScale;
    internal float PixelEraserHalfHeightPx => PixelEraserHeightLogical * 0.5f * DpiScale;

    /// <summary>截图工具的拖动矩形（画布坐标）。</summary>
    internal bool CaptureActive;
    internal float CapMinX, CapMinY, CapMaxX, CapMaxY;

    /// <summary>
    /// **松开之后、确认之前**的"调整"阶段（8.3.0 加的）。
    ///
    /// 以前松手 = 立刻抓屏落图，框歪了只能删掉重截。现在松手先留框：
    /// 可以拖边/拖角/拖整体，`Enter` 或点「✓ 完成」才落图，`Esc`/右键/点框外取消
    ///（微信 / QQ / Snipaste 都是这个手感）。
    /// </summary>
    internal bool CaptureAdjusting;

    /// <summary>
    /// **冻结帧**（8.3.0）：按下截图的那一瞬间，先把**整块虚拟桌面**抓下来存着，
    /// 松手/确认时只是**裁剪**它。
    ///
    /// 为什么这么改（用户 2026-09-30 选的方案）：旧流程是松手时藏窗口（60ms）再抓屏，
    /// 老师能看到"批注整层闪一下"；而且松手就定局、没法调整。冻结之后：
    ///   · 抓取只发生在**按下那一刻**一次（`隐藏界面` 模式的那 60ms 闪在刚按下时，之后全程不闪）；
    ///   · 松手只裁剪内存，屏幕不动 → 调整阶段可以随便拖；
    ///   · 洞里画的是**冻结帧本身**（所见即所得：拍到的就是看到的）。
    /// 代价：拍到的画面 = 按下那一刻（PPT 动画会停在按下时）；内存瞬态 ~20MB（一块整屏）。
    /// </summary>
    internal byte[] CaptureFrozenBgra;          // 物理像素、BGRA、自上而下、整块虚拟桌面
    internal int CaptureFrozenX, CaptureFrozenY, CaptureFrozenW, CaptureFrozenH;
    internal bool CaptureFrozen;                // 冻结帧在手上吗（抓失败 → false，退回松手时抓）

    internal enum CaptureDrag { None, Move, L, R, T, B, TL, TR, BL, BR }
    private CaptureDrag _capDrag = CaptureDrag.None;
    private float _capDragX, _capDragY;         // 按下时的指针（画布）
    private float _capD0MinX, _capD0MinY, _capD0MaxX, _capD0MaxY;   // 按下时的框
    /// <summary>调整阶段那一次按下已经被截图吃掉（抬手时别再走框选那套）。</summary>
    private bool _capturePressHandled;

    /// <summary>
    /// 进截图模式**之前**用的工具（8.3.1）：Esc / 右键 /「✕」取消时回到它，
    /// 而不是把老师留在"截图工具上空等"的状态里。在 `SwitchTool(→Capture)` 时记一次。
    /// </summary>
    private Tool _toolBeforeCapture = Tool.Pen;

    /// <summary>调整阶段"双击框内 = 完成"的判据（微信的手感）：上次按下的时刻/位置。</summary>
    private double _capLastClickMs = double.NegativeInfinity;
    private float _capLastClickX, _capLastClickY;

    /// <summary>框的最小边长（逻辑像素）：比这还小当成误点/取消。</summary>
    internal const float CaptureMinSizeLogical = 10f;

    /// <summary>调整阶段的 8 个手柄命中半径（逻辑像素）。</summary>
    internal const float CaptureHandleHitLogical = 14f;

    /// <summary>「✓ 完成 / ✕ 取消」两颗按钮的尺寸与间距（逻辑像素）。</summary>
    internal const float CaptureBtnWLogical = 96f, CaptureBtnHLogical = 30f, CaptureBtnGapLogical = 8f;

    /// <summary>
    /// 截图模式（用户 2026-09-17 要的"对接"，参考 InkClass 的两项菜单）：
    ///   · **true = 隐藏界面截取**（默认，也是原来的行为）：抓之前把整个覆盖层藏起来，
    ///     拍到的只有下层内容——**批注、白板、面板一起藏**，一概不入镜。
    ///     （用户 2026-09-17 更正过叫法：原来写"隐藏批注截取"，
    ///      但它其实连白板和面板一起藏，所以界面上把这档叫**"隐藏界面"**。）
    ///   · **false = 直接截取**：**连板书一起拍**（老师想把"PPT + 我写的批注"一起给别人，
    ///     或者把自己写的解题过程做成一张图）。
    ///
    /// 直接截取时**取景框本身**还是要藏掉（不然那个琥珀色框会拍进图里）——
    /// 见 <see cref="CaptureFrameHidden"/>。
    /// </summary>
    internal bool CaptureHideInk = true;

    /// <summary>
    /// 这一帧不画"我们自己盖在画面上的东西"——取景框**和落点光标**（只在"直接截取"
    /// 抓屏的那一瞬为真）。光标也得藏：它是琥珀色的，留着就会被拍进图里。
    /// 8.3.0 起连**遮罩/准线**一起不画（冻结帧就抓在这一瞬，一丁点我们自己的东西都不能进图）。
    /// </summary>
    internal bool CaptureFrameHidden;

    /// <summary>
    /// 抓屏专用：这一帧把整个覆盖层清成**全透明**（不画内容/界面/光标），
    /// 让 BitBlt 直接看到桌面。用它替代"把窗口藏起来再显示"可去掉吸管/截图的闪屏。
    /// </summary>
    internal bool CaptureTransparentFrame;

    // ---- 白板底纹（方格 / 横线）--------------------------------------------
    //
    // 用户 2026-09-17 要的："给白板增加网格和横线功能，可以参[考]inkclass 的实现"。
    // InkClass 那边（`MW_WhiteboardPattern.cs`）的做法与取舍：
    //   · 三档：**无 / 方格 / 横线**；
    //   · **不是笔迹**——"橡皮擦不掉、撤销不涉及、选择选不中、换页共用"；
    //   · 线细 0.5 逻辑像素（辅助参考线，纤细不抢视觉）；线色随板面明暗自适应
    //     （浅板：灰 60%；深板：白 20%）；
    //   · 间距可调（它给的是 16～240 的滑块）。
    //
    // 我们这里更进一步：底纹和板色一起**画进分块缓存**（见 Overlay.DrawBoardPattern），
    // 所以滚动、翻页、写字都不花额外的钱；代价只有"换底纹/换板色时整层重铺一次"，
    // 和换板色本来就是同一件事。

    /// <summary>白板底纹：0 = 无，1 = 方格，2 = 横线。</summary>
    internal int BoardPattern;

    /// <summary>
    /// **白板的不透明度**（用户 2026-09-17："增加一个透明度的拖动功能，这样可以批注的时候
    /// 隐约看见下面的题目，但是书写有干净"）。
    ///
    /// 1 = 实心板面（现在这样）；调小 → 板面半透明，下面的 PPT / 题目隐约透出来。
    /// **关键是"书写干净"**：半透明只作用于**底色**——底色是画进分块缓存的第一层，
    /// 笔迹画在它上面、本身不透明，所以字照样是实的（自检里专门验这一条）。
    /// </summary>
    internal float BoardOpacity = 1f;
    /// <summary>可调范围：0.35 再低就看不清自己写的字了，1.0 = 实心。</summary>
    internal const float BoardOpacityMin = 0.35f, BoardOpacityMax = 1f;

    internal void SetBoardOpacityFromUi(float v)
    {
        v = Math.Clamp(v, BoardOpacityMin, BoardOpacityMax);
        if (MathF.Abs(BoardOpacity - v) < 0.005f) return;
        BoardOpacity = v;
        // 和换板色 / 换底纹一样：底色是画进分块缓存的，所以所有块都过期了
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }
    /// <summary>底纹间距（逻辑像素）。InkClass 的范围是 16～240，我们按逻辑像素存。</summary>
    internal float BoardPatternStepLogical = 40f;

    /// <summary>底纹间距（画布像素 = 物理像素）。</summary>
    internal float BoardPatternStepPx => Math.Clamp(BoardPatternStepLogical, 8f, 240f) * DpiScale;

    internal void SetBoardPatternFromUi(int pattern, float stepLogical)
    {
        pattern = Math.Clamp(pattern, 0, 2);
        stepLogical = Math.Clamp(stepLogical, 8f, 240f);
        if (BoardPattern == pattern && MathF.Abs(BoardPatternStepLogical - stepLogical) < 0.01f) return;
        BoardPattern = pattern;
        BoardPatternStepLogical = stepLogical;
        // 和换板色一样：底纹是**画进分块缓存**的，所以所有块都过期了
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetCaptureHideInkFromUi(bool hideInk)
    {
        if (CaptureHideInk == hideInk) return;
        CaptureHideInk = hideInk;
        NotifyUiStateChanged();
    }

    /// <summary>引擎入口：进入截图取景（面板上点模式段走它，见 IEngineCommands.EnterCapture）。</summary>
    internal void BeginCaptureModeFromUi()
    {
        ExitReplayForEdit("截图");
        BeginCaptureMode();
    }

    /// <summary>
    /// **屏幕吸管**（工具带自定义色用）：冻结当前桌面，等用户点一下；点击后写入自定义槽。
    ///
    /// 复用截图链路：<see cref="BeginCaptureFreeze"/> 会先藏起整个覆盖层、等合成器撤掉，
    /// 再抓虚拟桌面到 `CaptureFrozenBgra`。之后只在这张冻结图上取像素，所以不会取到
    /// 我们自己的工具栏、墨迹或取色板；也不需要每次移动都隐藏窗口。
    /// </summary>
    internal void BeginScreenColorPickFromUi(int targetSlot = -1)
    {
        if (ColorPickActive) return;
        if (CaptureActive) CancelCapture();
        int targetCount = Tool == Tool.Highlighter
            ? HighlighterSwatchColors.Length : PenSwatchColors.Length;
        ColorPickTargetSlot = targetSlot >= 0 && targetSlot < targetCount ? targetSlot : -1;

        if (Native.GetCursorPos(out var p))
        {
            PointerX = p.X;
            PointerY = ScreenToCanvasY(p.Y);
            PointerInside = true;
        }
        ColorPickHoverValid = false;
        ColorPickActive = true;
        // 吸管取的是**底下真正的内容**：不能把老师自己的墨迹拍进去挡住要取的颜色。
        // 复用截图偏好会把“隐藏墨迹”这个开关带进来，这里临时强制为 true，抓完恢复。
        bool oldHideInk = CaptureHideInk;
        CaptureHideInk = true;
        BeginCaptureFreeze();
        CaptureHideInk = oldHideInk;
        if (!CaptureFrozen)
        {
            ColorPickActive = false;
            Console.WriteLine("吸管：抓屏失败，取消");
            _dirty = true;
            NotifyUiStateChanged();
            return;
        }
        UpdateColorPickHover(PointerX, PointerY);
        _dirty = true;
        ApplyCursor();
        NotifyUiStateChanged();
        // 透明帧抓完之后立刻把冻结帧铺上，别让桌面在"抓完 → 下一帧"之间多露一帧。
        RenderAll();
        Console.WriteLine("吸管：点屏幕取色，Esc 取消");
    }

    /// <summary>吸管当前屏幕点对应的冻结帧颜色；超出冻结帧返回 null。</summary>
    internal Color4? SampleFrozenColorAt(float canvasX, float canvasY)
    {
        if (!CaptureFrozen || CaptureFrozenBgra == null) return null;
        int sx = (int)MathF.Round(CanvasToScreenX(canvasX));
        int sy = (int)MathF.Round(CanvasToScreenY(canvasY));
        int ix = sx - CaptureFrozenX, iy = sy - CaptureFrozenY;
        if (ix < 0 || iy < 0 || ix >= CaptureFrozenW || iy >= CaptureFrozenH) return null;
        int o = (iy * CaptureFrozenW + ix) * 4;
        if (o < 0 || o + 3 >= CaptureFrozenBgra.Length) return null;
        return new Color4(CaptureFrozenBgra[o + 2] / 255f,
                          CaptureFrozenBgra[o + 1] / 255f,
                          CaptureFrozenBgra[o] / 255f, 1f);
    }

    private void UpdateColorPickHover(float x, float y)
    {
        var c = SampleFrozenColorAt(x, y);
        ColorPickHoverValid = c.HasValue;
        if (c.HasValue) ColorPickHover = c.Value;
    }

    internal string ColorPickHexText()
    {
        var c = ColorPickHover;
        int r = Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
        int b = Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    internal void CopyColorPickHex()
    {
        if (!ColorPickActive || !ColorPickHoverValid) return;
        if (ClipboardText.Set(ColorPickHexText()))
        {
            ColorPickCopiedUntilMs = NowMs + 1000;
            _dirty = true;
        }
    }

    private void CommitColorPickAt(float x, float y)
    {
        var c = SampleFrozenColorAt(x, y);
        bool highlighter = Tool == Tool.Highlighter;
        int count = highlighter ? HighlighterSwatchColors.Length : PenSwatchColors.Length;
        int selected = highlighter ? HighlighterSwatchSelected : PenSwatchSelected;
        int slot = ColorPickTargetSlot >= 0
            ? ColorPickTargetSlot
            : selected >= 0 ? selected : count - 1;
        ColorPickTargetSlot = -1;
        ColorPickActive = false;
        ReleaseCaptureFrozen();
        if (c.HasValue)
        {
            if (highlighter)
                SetHighlighterSwatchColorFromUi(slot, c.Value, rememberRecent: true, normalize: true);
            else
                SetPenSwatchColorFromUi(slot, c.Value, rememberRecent: true);
        }
        _dirty = true;
        ApplyCursor();
        RegainKeyboardFocus();
        NotifyUiStateChanged();
        Console.WriteLine(c.HasValue ? "吸管：已取色" : "吸管：点在屏幕外，取消");
    }

    internal void CancelColorPick()
    {
        if (!ColorPickActive) return;
        ColorPickActive = false;
        ColorPickTargetSlot = -1;
        ReleaseCaptureFrozen();
        _dirty = true;
        ApplyCursor();
        RegainKeyboardFocus();
        NotifyUiStateChanged();
        Console.WriteLine("吸管：已取消");
    }

    /// <summary>Pen width presets, in logical pixels. Cycled with Ctrl+Alt+W
    /// until there is a proper on-screen control for it.
    /// 最细档 2026-09-27 从 1.5 降到 **1**（用户："画笔的最小笔宽设成 1 可以吗？"——
    /// 1.5 写细字、画坐标轴刻度时还是偏粗）。
    /// **2026-10-15 用户定：画笔 = 1~20 整整 20 档**（原话"1~20 这 20 个档位就可以了"）——
    /// 每档差 1 个逻辑像素；原 1.5/2.5/3.5 中点档与 40 超粗档一并撤掉。
    /// 默认档仍是 3（现下标 2）。滑条拖动**吸附到最近档**（不再有中间值、也不画点）。
    /// 界面那份对应表在 `FullUi.PenWidthGrades`，靠 --paneltest 逐项比对卡住。</summary>
    internal static readonly float[] WidthPresets =
    {
        1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f,
        11f, 12f, 13f, 14f, 15f, 16f, 17f, 18f, 19f, 20f,
    };
    /// <summary>上一代档表（2026-10-09 晚 ~ 2026-10-15，15 档）：只为**迁移老配置**而留。
    /// 老配置 `w3.pen` 存的是那张表的"第几档"——直接按新表读会错位，先过这张表取回
    /// 数值、再吸附进新表（1.5/2.5/3.5 吸附到相邻整数档；40 吸附到 20）。</summary>
    internal static readonly float[] Legacy15WidthPresets = { 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 20f, 40f };
    /// <summary>上一代档位表（2026-10-09 白天，9 档）：只为**迁移老配置**而留。
    /// 老配置 `w2.pen` 存的是那张表的"第几档"——直接按新表读会错位，
    /// 先过这张表取回数值、再吸附进新表（16 / 24 不在新表，吸附到最近的 20）。</summary>
    internal static readonly float[] Legacy9WidthPresets = { 1f, 2f, 3f, 4f, 6f, 8f, 10f, 16f, 24f };
    /// <summary>更老的一代（2026-09-27 ~ 2026-10-09 白天前，6 档）：迁移 `w.pen` 用。
    /// 老配置 `w.pen` 存的是"第几档"，直接按新表读会把 6 读成 2——先过这张表取回数值，
    /// 再吸附回新表（老表每个值都在新表里，吸附无损）。</summary>
    internal static readonly float[] LegacyWidthPresets = { 1f, 3f, 6f, 10f, 16f, 24f };
    internal int WidthPresetIndex = 2;            // = 3f（默认粗细；新 20 档表的下标）

    /// <summary>
    /// 每种工具各自的粗细档位。**笔和荧光笔的档位不是一回事**：荧光笔是"涂一大条"，
    /// 1 像素这种档位对它没意义；激光更小。共用一张表的结果就是
    /// "选了荧光笔按 Ctrl+Alt+6 没反应"（它改的是笔宽）——实测就是这个 bug。
    /// </summary>
    /// <summary>荧光笔档位。**2026-10-16 用户定：8–88、步 4（21 档）**——大屏上盖一行
    /// 大字的场景，旧表最粗 32 偏细。沿革：8/18/32 → 8/13/18/25/32（10-09 晚）→ 现在这张。
    /// 默认档 = 下标 2 = **16**（旧默认 18 离它最近）。</summary>
    internal static readonly float[] HighlighterWidthPresets =
        { 8f, 12f, 16f, 20f, 24f, 28f, 32f, 36f, 40f, 44f, 48f, 52f, 56f, 60f, 64f, 68f, 72f, 76f, 80f, 84f, 88f };
    /// <summary>
    /// 激光的档位。**2026-10-16 用户定：2–24、步 2（12 档）**（原提"2–40 步 2"，
    /// 评估后把上限收到 24——40 逻辑像素 ≈1 厘米宽的红带，更像马克笔还挡内容）。
    /// 沿革：4/8/14 → 8/14/22 → 4/8/14/22 → 4/6/8/11/14/18/22（10-09 晚）→ 现在这张。
    /// 默认档 = 下标 3 = **8**（和旧默认一致）。
    /// </summary>
    internal static readonly float[] LaserWidthPresets =
        { 2f, 4f, 6f, 8f, 10f, 12f, 14f, 16f, 18f, 20f, 22f, 24f };
    internal int HighlighterWidthIndex = 2;
    internal int LaserWidthIndex = 3;

    /// <summary>当前工具的粗细（逻辑像素）。落点反馈、界面状态都读它。</summary>
    internal float CurrentToolWidthLogical => Tool switch
    {
        Tool.Highlighter => HighlighterWidthLogical,
        Tool.Laser => LaserWidthLogical,
        Tool.PixelEraser => PixelEraserWidthLogical,
        Tool.Eraser => EraserRadiusLogical,
        _ => PenWidthLogical,
    };

    internal bool PointerInside;
    internal float PointerX, PointerY;
    /// <summary>
    /// 最后一次指针消息来自什么设备（鼠标 / 笔 / 触摸）。**悬停的移动也要更新**——
    /// 指针形状直接取决于它，见 <see cref="ComputeCursorKind"/>。
    /// </summary>
    internal uint LastPointerType = Native.PT_MOUSE;

    /// <summary>
    /// 当前这支笔的**笔尖是不是就在屏幕上**（触摸屏自带的笔 = true；外接手写板 = false）。
    ///
    /// 为什么要分这一刀（2026-09-27 用户报的 bug）："手写笔落笔后不画落点、把系统光标也
    /// 藏起来"这条规则的依据是"**笔尖本身就是落点**"。可手写板的笔尖在**板子上**、
    /// 根本不在屏幕里——写字时屏幕上什么都没有，用户的原话是"感觉不太流畅、有点奇怪"。
    /// 所以这条规则**只对触摸屏自带的笔成立**；鼠标、手写板（以及认不出设备的笔）
    /// 一律给"斜笔"光标，让它一路跟着落点走。
    ///
    /// 设备句柄 → 结论只查一次（同一支笔不会一会儿在屏上一会儿在板子上），缓存住；
    /// 查不到（老驱动 / 合成设备）按 false 走 = "给光标"——**宁可多给，不能没有**。
    /// </summary>
    internal bool PenDeviceOnScreen;

    /// <summary>设备句柄 → "笔尖在屏幕上吗"的缓存（见 <see cref="PenDeviceOnScreen"/>）。</summary>
    private readonly Dictionary<IntPtr, bool> _penOnScreenCache = new();

    /// <summary>查一次"这个设备句柄的笔尖在不在屏幕上"，结果进缓存。</summary>
    private bool QueryPenOnScreen(IntPtr sourceDevice)
    {
        if (sourceDevice == IntPtr.Zero) return false;
        if (_penOnScreenCache.TryGetValue(sourceDevice, out bool known)) return known;
        bool onScreen = false;
        bool ok = Native.GetPointerDevice(sourceDevice, out var info);
        if (ok) onScreen = info.pointerDeviceType == Native.POINTER_DEVICE_TYPE_INTEGRATED_PEN;
        _penOnScreenCache[sourceDevice] = onScreen;
        LogPenDevice(sourceDevice, ok, ref info, onScreen);   // 每台设备一行（reports\pen-devices.txt）
        return onScreen;
    }

    /// <summary>
    /// 每见到一台**新笔设备**，把 Windows 报的"出身"原样追加一行到 `reports\pen-devices.txt`。
    ///
    /// 为什么值得记（2026-10-16 用户报"手写板不开驱动时只有画画笔模式不统一"）：笔的光标
    /// 要不要藏，全靠 `GetPointerDevice` 报的 pointerDeviceType——**有手写板会被报成"屏内笔"**，
    /// 于是笔工具藏了系统光标、画了自绘圆环（用户看到的"圆圈＋十字"）。这种事光看现象
    /// 猜不出设备、字段、结论，得让文件说话。每台设备每进程只记一次（走上面的缓存）；
    /// 纯诊断，任何异常静默吞掉——绝不能因为它影响输入。
    /// </summary>
    private static void LogPenDevice(IntPtr handle, bool queryOk, ref Native.POINTER_DEVICE_INFO info,
                                     bool onScreen)
    {
        try
        {
            bool rectsOk = Native.GetPointerDeviceRects(handle, out _, out _);
            var sb = new System.Text.StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append("  句柄=0x").Append(handle.ToString("X"));
            sb.Append("  查询=").Append(queryOk ? "成功" : "失败");
            sb.Append("  类型=").Append(info.pointerDeviceType)
              .Append('(').Append(DeviceTypeName(info.pointerDeviceType)).Append(')');
            sb.Append("  monitor=0x").Append(info.monitor.ToString("X"));
            sb.Append("  矩形映射=").Append(rectsOk ? "成功" : "失败");
            sb.Append("  最大触点数=").Append(info.maxActiveContacts);
            sb.Append("  判定=").Append(onScreen ? "屏内笔（藏光标＋画环）" : "板子/外接（给斜笔）");
            string prod = ProductStringPath(ref info);
            // **合成设备（自检注入的虚拟笔）不记**：CreateSyntheticPointerDevice 造出来的设备
            // 路径长这样「\??\Microsoft HID RID\…」——它们不是用户的板子，记下来只会污染这份
            // "给真机看"的文件（2026-10-16：pressuretest 跑一下就混进来一行）。
            if (prod.Contains("Microsoft HID RID", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrEmpty(prod)) sb.Append("  产品=").Append(prod);
            System.IO.Directory.CreateDirectory("reports");
            System.IO.File.AppendAllText(System.IO.Path.Combine("reports", "pen-devices.txt"),
                                         sb.ToString() + Environment.NewLine);
        }
        catch { /* 诊断而已，不能影响输入 */ }
    }

    private static string DeviceTypeName(uint type) => type switch
    {
        Native.POINTER_DEVICE_TYPE_INTEGRATED_PEN => "屏内笔",
        Native.POINTER_DEVICE_TYPE_EXTERNAL_PEN => "外接笔",
        Native.POINTER_DEVICE_TYPE_INTEGRATED_TOUCH => "屏内触摸",
        Native.POINTER_DEVICE_TYPE_EXTERNAL_TOUCH => "外接触摸",
        _ => "未知",
    };

    /// <summary>
    /// 读 POINTER_DEVICE_INFO 尾巴上的产品路径（固定长度 wchar[520]）。
    /// **长度自己数**，不信驱动会乖乖补 '\0'——真遇到没补的，`new string(char*)` 会越界读到崩。
    /// </summary>
    private static unsafe string ProductStringPath(ref Native.POINTER_DEVICE_INFO info)
    {
        var local = info;                          // 先拷一份：栈上局部变量的定长缓冲**直接当指针用**
        ushort* p = local.productString;
        int len = 0;
        while (len < Native.POINTER_DEVICE_PRODUCT_STRING_MAX && p[len] != 0) len++;
        return new string((char*)p, 0, len);
    }

    /// <summary>
    /// 笔工具此刻**该不该给斜笔光标**（而不是藏起来交给自绘落点）。
    /// 规则：只有"笔尖就在屏幕上"的那支笔才藏（笔尖即落点）；鼠标、手写板、认不出的笔
    /// 都给斜笔（系统 IDC_PEN）。触摸轮不到它（<see cref="ComputeCursorKind"/> 开头就返回了）。
    /// </summary>
    internal bool PenShowsCursor
        => LastPointerType != Native.PT_PEN || !PenDeviceOnScreen;

    /// <summary>
    /// 最后一条指针消息里**笔是不是倒过来拿的**（笔尾橡皮，PEN_FLAG_INVERTED / ERASER）。
    ///
    /// 为什么要留这个状态（用户 2026-09-27 报的）：倒持的笔**画的时候**早就按橡皮走了
    /// （`OnPointerDown` / `OnPointerMove` 里那句 `inverted ? Tool.Eraser : Tool`），
    /// 唯独**光标没跟着变**——笔尾悬停在画布上时，屏幕上还是斜笔 / 笔尖环，
    /// 老师看不出"这一头是橡皮"，也看不出"会擦掉多大一块"。
    ///
    /// 生命周期跟着 <see cref="LastPointerType"/> 走：读指针消息时一起更新（见
    /// <see cref="ReadPointer"/>）；鼠标 / 触摸永远读不到倒持（它们没有笔尾），
    /// 于是每条消息都会把它复位成 false。
    /// </summary>
    internal bool LastPointerInverted;

    /// <summary>
    /// 此刻**实际在用的工具**：笔倒过来拿时就是橡皮，其余情况就是 <see cref="Tool"/>。
    ///
    /// 光标这一族（<see cref="ToolCursorKind"/> / <see cref="DrawnCursor"/> /
    /// <see cref="CursorOuterRadius"/>）必须问它而不是直接问 Tool——**判据只写一处**，
    /// 不然"倒持"这个判断就会散到好几处（本项目为这种散落栽过四次，
    /// 见 架构-分层与规则.md 五-7）。
    /// </summary>
    internal Tool EffectiveTool => LastPointerInverted ? Tool.Eraser : Tool;

    /// <summary>
    /// 左上角那个黑色性能面板（FPS/延时）。
    ///
    /// **默认关**（用户 2026-09-30：普通用户不该看到它，取消默认开）。
    /// 要看数字时用命令行 `--hud` 开（自检/基准那批模式另有一套规则：
    /// 它们默认也不开，除非显式 `--hud`——面板会盖住屏幕上的墨，数墨的用例会假失败）。
    /// 快捷键已经没有了（原 Ctrl+I 已删）。
    /// </summary>
    internal bool ShowHud = false;

    /// <summary>
    /// 橡皮的实测采集（"手测台" --eraserlab）。**平时是 null**，所有上报点都是 `?.`，
    /// 正常使用一分钱开销都不多花。见 <see cref="EraserTelemetry"/>。
    /// </summary>
    internal EraserTelemetry EraserTelemetry;

    /// <summary>
    /// 橡皮的落点反馈之外，**同时显示系统箭头**（默认藏起来，只画我们自己的圈/方块）。
    ///
    /// 留这个开关是为了做 A/B：自绘的方块光标本质是"我们画的一帧"，天生比系统光标晚
    /// 一个刷新周期（16.7 ms）；系统箭头画在硬件光标平面上，几乎没有延迟。用户说
    /// "不跟手"时，把两个都显示出来——箭头跟手、方块形状准，哪种更舒服由用户说了算。
    /// </summary>
    internal bool EraserKeepsSystemCursor;
    internal string HudText = "";

    /// <summary>
    /// **触点诊断**（8.3.3）：给学校大屏量"同时报几个触点 / 报不报接触面积"的小浮层。
    ///
    /// 为什么要它：手势方案（见 调研-触摸手势-学校大屏.md）能不能落地，全看这块屏
    /// ①一次能不能报 ≥2 个触点、②接触面积报不报——而这两件事**只有真机才能量**
    ///（无触摸屏的机器上合成注入不可靠：隔壁 InkClass 实测 0 命中，文档写明"不要承诺本机注入"）。
    /// 默认关、`--touchhud` 开、限频刷新（照他们的硬约束 ≥200ms）、不落盘。
    /// </summary>
    internal bool TouchHud;
    internal string TouchHudText = "";
    internal int TouchHudMax;                    // 见过的最大同时触点数
    internal bool TouchHudSawArea;               // 见过非零接触面积吗
    internal int TouchHudNow => _touchDiagIds.Count;
    private readonly HashSet<uint> _touchDiagIds = new();
    private float _touchDiagMaxW, _touchDiagMaxH;
    private double _touchDiagNextMs;
    private string _touchDiagLast = "—";
    internal bool MarqueeActive;

    /// <summary>
    /// 框选工具下拖空白处时的**选择方式**：矩形框 ↔ 自由套索（`Ctrl+Alt+9` 切）。
    ///
    /// 为什么不给界面按钮：工具条上已经挤了六种工具，而"选择方式"是个**低频设置**
    /// （一节课可能一次都不切）。一个键 + 面板上一行字（见 BuildHudText）足够，
    /// 还省掉一次"图标到底画哪个"的审美争论。**不落盘**（和工具尺寸一样活在内存里）。
    ///
    /// 两种手势的判据**故意不一样**（见 InkDocument.ApplyMarquee / ApplyLasso）：
    /// 拖矩形＝"我框住这一片"（碰到就算），画一圈＝"我把这一条圈起来了"（80% 在内）。
    /// </summary>
    internal SelectMode SelMode = SelectMode.Rect;

    /// <summary>套索拖出来的路径（**画布坐标**，间距 ≥ <see cref="LassoStepLogical"/>）。</summary>
    internal readonly List<Vector2> LassoPath = new();

    /// <summary>
    /// 指针**此刻**的位置（画布坐标）。套索的路径是抽稀过的（间距 ≥3 逻辑像素），
    /// 只画路径的话线头会比指针慢半拍（200% 缩放下差 6 个物理像素，看得出来）。
    /// 所以预览画"路径 + 这一个点"，手感才是线头咬着指针走。
    /// </summary>
    internal Vector2 LassoLive;

    /// <summary>套索路径的采样间距（逻辑像素）：太密没用，还会把点数撑到几百。</summary>
    internal const float LassoStepLogical = 3f;

    /// <summary>
    /// 框选矩形 / 截图取景框的**锚点**（按下那一点，画布坐标）。
    ///
    /// 矩形必须按"锚点 ↔ 当前点"来算，**不能**在每次移动时累积 min/max：
    /// 累积的话指针往回走框不会跟着缩，屏幕上看到的是"扫过的最大范围"，
    /// 手感就是"不跟手"（用户 2026-09-15 实测反馈："人家是选中起始点以后，
    /// 鼠标不管怎么变，它都是一个可变的矩形，但是我们这个似乎不是"）。
    /// </summary>
    private float _mqAnchorX, _mqAnchorY;

    /// <summary>截图取景框的锚点（同上，两处用的是同一套算法）。</summary>
    private float _capAnchorX, _capAnchorY;

    /// <summary>按下那一刻的修饰键（套索的 Shift 加选 / Alt 减选）。</summary>
    private bool _lassoAdditive, _lassoSubtractive;

    // ---- 选择手势（框选工具 = 选择工具）---------------------------------
    // 四种情形在这里分流：点操作条按钮 / 拖手柄 / 在选中范围里整体拖 / 空白处重新框选。
    internal bool SelDragging;
    internal int SelBarHover = -1;          // 悬停的按钮，-1 = 无（给渲染用）

    /// <summary>
    /// 操作条**收起**了吗（用户 2026-09-16 更正：条首那个 ✕ 不是"取消选择"，是"收起"）。
    /// 收起后整条不画，只在框下方留一个小圆钮，点它展开——投影时不挡板书。
    /// 注意收起的是**条**，框与手柄照旧（参考实现也是这样）。
    /// </summary>
    internal bool SelBarCollapsed;

    /// <summary>选中操作条末尾的 AI 下拉是否打开（2026-11-06）。</summary>
    /// <summary>AI 菜单开着吗。和 <see cref="SelMoreMenuOpen"/> **互斥**（方案 A）。</summary>
    internal bool SelAiMenuOpen;
    /// <summary>`⋯` 菜单开着吗。和 <see cref="SelAiMenuOpen"/> 互斥。</summary>
    internal bool SelMoreMenuOpen;
    /// <summary>当前弹层的悬停行（AI/`⋯` 共用；同一时间只开一个）。</summary>
    internal int SelMenuHover = -1;

    // 操作条 / 收起圆钮的**画布坐标缓存**：只在文档、选区、界面单位、可见区尺寸
    // 发生变化时重算；**画布滚动不重算**。这样滚动时条跟着对象一起走、滚出视野就消失，
    // 而不是一直粘在屏幕下沿（ClassIn 的行为，用户 2026-11-01 确认合理）。
    private RectF _barCanvasCache, _dotCanvasCache;
    private int _barCacheVersion = int.MinValue;
    private float _barCacheUnit;
    private float _barCacheViewW, _barCacheViewH;
    private Stroke[] _barCacheSelection = Array.Empty<Stroke>();

    /// <summary>当前操作条的画布矩形（滚动时跟着对象走）。</summary>
    internal RectF SelectionBarCanvasRect
    {
        get { EnsureSelectionChromeCache(); return _barCanvasCache; }
    }

    /// <summary>收起态圆钮的画布矩形（同上）。</summary>
    internal RectF SelectionDotCanvasRect
    {
        get { EnsureSelectionChromeCache(); return _dotCanvasCache; }
    }

    private void EnsureSelectionChromeCache()
    {
        var sel = Doc.Selected;
        var vp = FurnitureViewport;
        float vw = vp.IsEmpty ? 0f : vp.MaxX - vp.MinX;
        float vh = vp.IsEmpty ? 0f : vp.MaxY - vp.MinY;
        bool sameSel = _barCacheSelection.Length == sel.Count;
        if (sameSel)
            for (int i = 0; i < sel.Count; i++)
                if (!ReferenceEquals(_barCacheSelection[i], sel[i])) { sameSel = false; break; }
        // 单位用家具尺度（含相机缩放）：文档放大时条也要保持屏幕大小，
        // 倍率一变缓存就得重算（比较的就是这个值，不用另加字段）。
        float unit = FurnitureDpi;
        bool stale = _barCacheVersion != Doc.Version
                  || MathF.Abs(_barCacheUnit - unit) > 1e-4f
                  || MathF.Abs(_barCacheViewW - vw) > 0.5f
                  || MathF.Abs(_barCacheViewH - vh) > 0.5f
                  || !sameSel;
        if (!stale) return;

        var aabb = LiveSelectionFrame.CanvasAabb;
        int count = SelectionHandles.BarButtonCountFor(BarSurfaceNow);
        _barCanvasCache = SelectionHandles.BarRect(aabb, unit, vp, count);
        _dotCanvasCache = SelectionHandles.BarCollapsedRect(aabb, unit, vp);
        _barCacheVersion = Doc.Version;
        _barCacheUnit = unit;
        _barCacheViewW = vw;
        _barCacheViewH = vh;
        _barCacheSelection = sel.ToArray();
    }

    /// <summary>
    /// **画完自动选中那个框**现在把操作条"收起来"画（只挂一颗圆钮 ＋ 框 ＋ 手柄 ＋ 旋转柄）。
    ///
    /// 用户 2026-09-22 上手之后定的口径，原话两句：
    /// "刚选中以后，使用收起来的那个形态，然后操作了这个状态栏以后，就按照常规逻辑走吧"、
    /// "**我现在是只要收缩，其他的都不变**"。
    ///
    /// 所以要分清这个标志**管什么、不管什么**：
    ///   · **管**：操作条那一块画成"一颗圆钮"还是"一整条九格"（见 <see cref="BarDrawnCollapsed"/>）。
    ///     刚画完摊开一整条，观感太重、那张卡片又正好压在图形下面；点一下圆钮就摊开。
    ///   · **不管**：拖动 / 拉手柄 / 旋转 / 点条 —— 这些**和框选工具下一个样**
    ///    （`AutoSelectionZoneAt` 压根不读这个标志；第一版读了，结果就是用户报的
    ///     "不能拖动位置，只能拉伸缩放"）。
    ///
    /// 它和用户自己那个"收起"偏好（<see cref="SelBarCollapsed"/>）是**两路**：
    /// 点开圆钮时两个一起清（不然会"点了圆钮还是圆钮"）；这个标志本身不写回偏好。
    /// </summary>
    private bool _autoSelCollapsed;

    /// <summary>画完自动选中那个框现在是不是"收起来"的形态（见 <see cref="_autoSelCollapsed"/>）。</summary>
    internal bool AutoSelCollapsed => _autoSelCollapsed;

    // =====================================================================
    //  停顿成型（手写一笔停一下 → 变成图形）。规格与实测数据见 计划-图形工具.md §四十二。
    // =====================================================================

    /// <summary>开关（**默认开**，用户 2026-09-23 定的："因为是停顿变，所以默认开"）。
    /// 偏好只写"关过的"那一份（照 `InkSettings` 的规矩：和默认一样就不写）。</summary>
    internal bool DwellShapeEnabled = true;

    /// <summary>停顿状态机本体（"停了多久"那件事全在它里面，见 <see cref="DwellAssist"/>）。</summary>
    private readonly DwellAssist _dwell = new();

    /// <summary>armed 时被换下来的**手绘原迹**（撤销要把它放回文档，见 `DwellShapeAction`）。</summary>
    private Stroke _dwellInk;

    /// <summary>这一笔提交的是不是"停顿变出来的图形"（决定：自动选中 ＋ 撤销走替换）。</summary>
    private bool _dwellCommitted;

    /// <summary>轮询定时器开着没有（只在笔画进行中开，见 <see cref="DwellAssist.TickMs"/>）。</summary>
    private bool _dwellTimerOn;

    /// <summary>定时器 id：1 是"置顶"那颗（别人的），2 是停顿成型这颗。</summary>
    private const int DwellTimerId = 2;

    /// <summary>
    /// **这一次的自动选中是"停顿变出来的"**（而不是图形工具画完选的）。
    ///
    /// 为什么要单有一个标志：图形工具下那个自动选中的框，是按"当前工具是不是图形工具"
    /// 来放行的（见 `SelectionBarShown` / `SelectionInteractiveAt`）。而停顿成型**工具还是笔**
    /// （用户手里那支笔没变），所以只按工具判的话，那个框会"画得出来、点不着"。
    ///
    /// 它只在这一个来源下为真：框选 / 图形工具画完都不会置它，**所以笔下面不会凭空多出交互**。
    /// 清它的地方也只有一处（`ClearSelectionForNewContext`），和 `_autoSelCollapsed` 同一处。
    /// </summary>
    private bool _dwellSelected;

    /// <summary>刚把"停顿变出来的选中框"收起来的那一下——**如果只是点一下，不许留墨**
    ///（用户 2026-09-23 定的："点别处取消选中，而且也不会留墨迹"）。
    /// 见 <see cref="DwellTapLeaveNoInk"/>。</summary>
    private bool _dismissTapArmed;

    /// <summary>"取消选中那一击"的容差（逻辑像素）：整段位移不超过它就算"点一下，不是画"。
    /// 取 8：鼠标点一下通常只飘 1~2 px，手写笔更小；真画一笔不会只有 8 px。</summary>
    internal const float DwellTapSlopLogical = 8f;

    // ---- armed（幽灵期）"按住拖动"的两件事（**只给直线用**，见 ArmedStrokeMove）--------

    /// <summary>
    /// 成型那一刻的**笔尖位置**（直线的"起步死区"基准）。
    /// 用途见 <see cref="DwellDragSlopLogical"/>：笔尖还在这儿附近时，线一个像素都不改。
    /// </summary>
    private Vector2 _dwellArmAnchor;

    /// <summary>直线：**钉住的那一头**（成型时定下来，拖动全程不变）。
    /// 为什么不能直接拿 `Points[0]`：识别器给的端点是"拟合方向的投影极值"，
    /// 哪一头落到 `Points[0]` 是不定的（见 <see cref="ShapeRecognize.TryLine"/>）。</summary>
    private Vector2 _dwellLinePin;

    /// <summary>**上一笔"收笔"的时刻**（引擎时钟，毫秒）。两笔配对要用它算时间窗口 ——
    /// 用户 2026-09-25 定 5 秒，2026-09-26 **放开到 20 秒**（见 <see cref="TwoBranchWindowMs"/>）。
    /// 写在 `EndStroke` 里（截屏那条分支之后）：截一次屏不该把两笔的窗口冲掉。</summary>
    private double _lastInkEndMs = double.MinValue;

    /// <summary>**两笔成型**时"第一笔"那个对象（用户 2026-09-25："画两支就是双曲线"）。
    /// 非 null = 抬手提交时要把**上一笔也收走**（见 `EndStroke` 里那段）。
    /// 认出来的那一刻钉住；`_dwellInk` 清哪儿它就清哪儿（取消、复位都要跟）。</summary>
    private Stroke _twoBranchPrev;
    private int _twoBranchIndex = -1;

    /// <summary>配对诊断日志的**限流**时刻（`TryTwoBranchPair` 每 40ms 走一次，不限流会刷屏）。</summary>
    private double _pairLogMs = double.MinValue;

    /// <summary>
    /// **最后一次"停顿成型"出来的对象**，以及**它当初那一笔原迹**（用户 2026-09-26 报
    /// "画几次才成一次"之后加的）。
    ///
    /// 为什么需要：画双曲线的**第一支**时，如果收笔前手停了一下（400ms），这一支就**先被
    /// 停顿时成了一个抛物线对象** —— 而对象的 `Points` 只剩 2~3 个定义点，**拿不回原来
    /// 那些采样点**，于是两笔配对**永远配不上**，用户看到的就是"这次不行、再画一次说不定行"
    /// （要不要停那一下是随机的）。对象本身已经在文档里了，撤销栈也记着它的原迹 ——
    /// 这里只是**在引擎里留一个引用**，配对时拿它当"第一支"用。
    ///
    /// ⚠ 只在**引擎**里留，不进模型、不进存档：它是个纯粹的运行期便利。
    /// ⚠ 用它之前必须确认 `prev == _lastDwellShape`（引用比较）—— 用户中间画了别的、
    ///   或撤销掉了，`Doc.Strokes[^1]` 就不再是它了，自动失效。
    /// </summary>
    private Stroke _lastDwellShape;
    private Stroke _lastDwellInk;

    /// <summary>
    /// **识别诊断日志开关**（2026-09-26 加，给"真机上为什么没认出来"留的口子）。
    ///
    /// 由来：用户报"椭圆和矩形两个都**常常什么都不认**"，而**真机上"没认出来"一点痕迹都没有**
    /// （认不出 = 静默什么都不做，见 §42）—— 只能靠合成语料去猜，猜了两轮都没复现。
    /// 开了它，停顿定型那一刻会往控制台打一行：认成了什么（带 `Rule`）、或者**为什么没认**
    /// （`Rule` 里每一道闸都带着数字，比如"矩形框贴不住（最远 18 > 容差 16）"）。
    ///
    /// 用法：`InkTeach.exe --reclog`（终端里跑），或环境变量 `INKTEACH_RECLOG=1`
    /// （后者对所有自检模式也生效，`--dwelltest` 就能把日志打出来）。
    /// ⚠ 默认为 false：正常开窗跑**不刷屏**。
    /// </summary>
    internal static bool InkLogEnabled;

    /// <summary>
    /// **墨迹录制**（用户 2026-09-26 提："**我手画多少条双曲线给你，你根据这些双曲线来定制
    /// 一个方案**"）。非 null 时，每一笔收笔都把**原始采样点**追加到这个文件里。
    ///
    /// 用法：`InkTeach.exe --recink reports/我的双曲线.txt`，然后照常画。
    /// **识别失败的那几笔才是最值钱的样本**，所以看到没变出来不用管，照画完抬手就行。
    ///
    /// ⚠ 记的是**识别器看到的那串点**（`TickDwellShape` 里那份 `pts`，坐标口径完全一致）——
    ///   不是成型后的对象：对象里只剩两三个定义点，**原始的采样点拿不回来了**。
    /// ⚠ 一笔里**没停顿**（没触发识别）时，用收笔那一刻的 `ActiveStroke` 兜底（点数够才算）。
    /// </summary>
    internal static string InkRecordPath;

    /// <summary>`--recinkp`：录墨迹时**多录压力和来源**（见 `FlushInkRecord` 那段注释）。
    /// 不加这个开关时，录制格式与以前逐字一致（`--inkfile` 那个读取器不受影响）。</summary>
    internal static bool InkRecordWithPressure;

    /// <summary>这一笔里哪些点是 **raw 补点**加的（下标）。只录来源时才记。</summary>
    private readonly System.Collections.Generic.List<int> _rawAddedIdx = new();

    // ===================== `--clean N`：入笔前的净化 =====================
    //
    // **为什么要有它（2026-10-07，用户录了真机笔迹之后）**：
    // 把用户真实写的 22 笔录下来量，发现两件事：
    //   ① **位置有毛刺**：抖动 RMS 3.7px、峰值偏离 31.7px、方向反转 12~23 次/笔
    //      —— 而点距只有 4~10px，所以是"每 4 个点就来回拐一下"的那种细毛刺。
    //      app 原来的距离窗（12px ≈ 3 个点）对它**不够**：3 点平均只把 ±2px 压到 ±1.2px。
    //   ② **压力是台阶**：28 个点里压力只变 8 次 → 宽度一跳一跳。
    //
    // 加大 `--mean2win` 也能压毛刺，但那是**距离窗**：窗口越大滞后越大（≈窗一半），
    // 换来的跟手变差很明显。而这两种毛病都是"短程"的 —— 用**固定点数的滑动平均**
    // 更划算：同样压噪声，滞后只有 N/2 个**点**（≈ 十几毫秒）。
    //
    // 默认 **0 = 完全不动**（一个字节不改）。等用户试出合适的档再考虑设默认。

    // ---- 湿墨档位（2026-10-13 新增；2026-10-15 精简为两档：应用湿墨层撤除）------------
    //
    // 档位是**在原来的路径之上叠加**，不是替换：
    //   0 = 原路径：不喂系统轨迹——湿墨只有主交换链自绘（**默认**，保证"关档 = 和以前逐字一样"）；
    //   1 = + 系统委托轨迹（IDCompositionDelegatedInkTrail 只补"还没上屏的那一小截"）：
    //       **有就用、没有就自动退回软件自绘**（2026-10-16 起**只在 Win11 24H2+ 启用**——
    //       低于它整条不探测、全走自绘；Win10 本就没有这条通道）。
    // 两档里**主交换链自绘都照旧**（档位只加不减），所以任何一档都不会比原路径更差。
    // （2026-10-15 用户定：删掉原档 2「应用湿墨层（FastWet）」——真机看有锯齿、松手才平滑，
    //   也感觉不到优势；原档 3「全开」随 FastWet 一起消失，语义并入 1。）
    //
    // 切换方式：`--wetmode N`（记住到设置里）或运行时 **Ctrl+Alt+Shift+W** 循环。
    internal static int WetMode;

    /// <summary>湿墨档位的用户偏好键（命令行/热键写，没有界面）。</summary>
    private const string WetModePrefKey = "wetmode";

    /// <summary>命令行要求的档位（推迟到 Loop() 才写盘——构造函数里 SelfCheckMode 还不可信）。</summary>
    private string _pendingWetModePref = "";

    internal static string WetModeName(int m) => m switch
    {
        1 => "系统委托轨迹（有就用、没有退回自绘）",
        _ => "原路径（不喂系统轨迹）",
    };

    /// <summary>把当前档位落到系统轨迹开关上，并把"被关掉那条通道"的在场湿墨收掉。</summary>
    private void ApplyWetMode()
    {
        bool trail = WetMode == 1;
        OverlayWindow.InkTrailEnabled = trail;
        foreach (var w in _windows)
            if (!trail) w.EndInkTrail();
        _dirty = true;
    }

    /// <summary>`--clean N`：入笔前对位置与压力做 N 点滑动平均（0/1 = 关）。</summary>
    internal static int CleanWindow;

    private readonly System.Collections.Generic.List<(float X, float Y, float P)> _cleanBuf = new();

    /// <summary>
    /// 加一个点，必要时先净化。
    /// ⚠ 滞后 = 窗口内的点数（最多 N 个点）。N 别开太大：写到 5 就有点"笔尖拖着一截"了。
    /// </summary>
    private void AddPointCleaned(float x, float y, float p, double t)
    {
        if (ActiveStroke == null) return;
        InputBlackBox.Sample(t, x, y, p);   // 黑匣子：记录喂进预测器的原始点（回放靠它 1:1 复现）
        // 喂预测器。**用点自己的时间 `t`**（2026-10-16 修，用户同意）：
        //   · 笔/指针消息路径：`t` = 每点自带硬件时标（QPC）换算的相对时间——最新一点
        //     锚在 nowMs、批内其余点按真实间隔回推（见 PenSampleBuffer / PointerSampleBuffer）；
        //   · raw 补点 / 回放 / 合成输入：`t` 就是 NowMs（时标不可用时同样退化成 NowMs）。
        // 以前统一用 NowMs：**同一轮消息泵里收到的一批点会拿到同一个时间戳**（dt=0 被跳过），
        // 速度只能"跨批"估计——批内最后几步（往往正是拐弯那几笔）进不了速度，
        // 在批量投递的机器上就表现为"拐弯甩尾"。用 `t` 后批内间隔真实，方向立刻正确。
        // （两边的值在同一把引擎时钟上：t 是拿 NowMs 往回扣 QPC 差值，不是另一套时基。）
        if (PredictTailEnabled) _predictor.Add(x, y, t);
        if (CleanWindow <= 1) { ActiveStroke.AddPoint(x, y, p, t); return; }

        _cleanBuf.Add((x, y, p));
        if (_cleanBuf.Count > CleanWindow) _cleanBuf.RemoveAt(0);
        float ax = 0f, ay = 0f, ap = 0f;
        for (int i = 0; i < _cleanBuf.Count; i++)
        { ax += _cleanBuf[i].X; ay += _cleanBuf[i].Y; ap += _cleanBuf[i].P; }
        int n = _cleanBuf.Count;
        ActiveStroke.AddPoint(ax / n, ay / n, ap / n, t);
    }

    // ---- 自绘预测尾（2026-10-15 接入：线性预测，Chromium 同族）------------------
    // 旧账（"乱跳"的正身，写在 App.TailJumpTest 的注释里）：鼠标/兼容模式输入是突发的——
    // 来一阵 → 尾巴甩出去；间隔一超 40ms → 速度清零 → 尾巴整个消失。修法 = 长度限速平滑。
    // 纪律：只画显示（独立几何，不进文档/存档/命中）、每帧重建、慢写/反向/断笔由
    // InkPredictorLinear 内建把关；开关：设置「墨迹预测」行 / `--predict2`（**默认关**，2026-10-16 用户定）。
    // 2026-10-15 用户拍板：**取消毫秒调节**（「预测时长」行与 `--predlatency` 撤除）、
    // **替换 v6**（用户在 Win10 机器实测 v6 甩尾明显）——只剩这个线性预测 + 一个开关。
    // v6 备份 `.revert/2026-10-15-预测线性接入/`、说明 `调研/已停用-预测算法收口.md`、
    // 对照与 60Hz 结论见 `调研/调研-线性预测-Chromium.md`。
    internal static bool PredictTailEnabled;

    // 版本号显示见 Update.cs 末尾的 `ProductInfo`（界面工程用；它拿不到内部的 UpdateFeed）。
    // 唯一预测器（地平线固定 21.3ms = 实测端到端延迟；没有时长档）。
    private readonly InkPredictorLinear _predictor = new();
    // 预测点暂存（复用；**不是每帧 new**——v6 一次最多给 ≤6 个点，缓冲给足 64）。
    private readonly PredictedPoint[] _predBuf = new PredictedPoint[64];
    // 委托墨迹活跃时的预测缩放（2026-10-12 委托接入后）：湿墨已把"墨跟笔"做掉，
    // 预测应退场——默认缩到 0.35（留一丝对冲收笔交接）；`--wetpred 0` 全关 / `1` 回旧行为。
    private bool _trailActiveThisStroke;
    private float _wetPredScale = 0.35f;
    private float _tailLen;                               // 显示尾长（画布像素，限速平滑）
    private float _tailWidthState;                        // 尾宽平滑状态（0=未定）
    private readonly List<Vector2> _tailOffsets = new();  // 预测偏移（相对原始末点，画布坐标）
    private readonly List<Vector2> _tailCanvas = new();   // 供 Overlay：锚点 + 预测点（画布坐标）
    private float _tailWidth = 3f;

    /// <summary>Overlay 每帧读：预测尾现在该不该画（点数 ≥2 才画）。</summary>
    internal bool PredictTailActive =>
        PredictTailEnabled && ActiveStroke != null && _tailCanvas.Count >= 2;
    internal IReadOnlyList<Vector2> PredictTailPoints => _tailCanvas;
    internal float PredictTailWidth => _tailWidth;
    /// <summary>诊断（`--tailjumptest` / 黑匣子回放用）：喂点数 / 尾点数 / 当前尾长。</summary>
    internal string PredictTailDebug =>
        $"feed={_predictor.Count} canvas={_tailCanvas.Count} len={_tailLen:F1}";

    private void ResetPredictTail()
    {
        _predictor.Reset();
        _trailActiveThisStroke = false;
        _tailLen = 0f;
        _tailWidthState = 0f;
        _tailOffsets.Clear();
        _tailCanvas.Clear();
    }

    /// <summary>
    /// 每帧一次（渲染前）：算出这一帧要画多长的预测尾。
    /// **长度限速在"像素"上做**（治"跳跃的笔头"）：速度估计的帧间尖峰（批量投递、
    /// 压力变化）不再直接乘进偏移——尾巴长度按 伸展 ~2.2px/帧、回收 ~1.2px/帧
    /// （90Hz 下 ≈200/110 px/s）逼近目标，方向照旧。看得见的效果是"墨顺滑地跟上去/收回来"。
    /// （internal：自检的 `SettleFrames` 帧驱动器也要调它——它不走主循环。）
    /// </summary>
    internal void UpdatePredictTail()
    {
        _tailCanvas.Clear();
        if (!PredictTailEnabled || ActiveStroke == null || ActiveStroke.Points.Count < 2)
        {
            _tailLen = 0f;
            _tailOffsets.Clear();
            return;
        }

        float frameMs = _windows.Count > 0 && _windows[0].RefreshPeriodMs > 1.0
            ? (float)_windows[0].RefreshPeriodMs
            : 1000f / 90f;

        var pred = _predBuf;                      // 复用缓冲
        int n = _predictor.Predict(pred);         // 唯一预测器（2026-10-15 起：线性）
        if (n > 0)
        {
            var raw = ActiveStroke.Points[^1];
            _tailOffsets.Clear();
            for (int i = 0; i < n; i++)
                _tailOffsets.Add(new Vector2(pred[i].X - raw.X, pred[i].Y - raw.Y));
            // 2026-10-12：本笔有系统委托轨迹（湿墨在画）→ 预测退场（×_wetPredScale）。
            if (_trailActiveThisStroke && _wetPredScale < 0.999f)
                for (int i = 0; i < _tailOffsets.Count; i++)
                    _tailOffsets[i] = _tailOffsets[i] * _wetPredScale;
        }

        // ---- 长度限速（在"像素"上做，治"跳跃的笔头"）----
        // 速率按**时间**定义（px/ms）：60Hz 学校屏与 90Hz 本机观感不漂移（帧数不同、时间相同）。
        // 三段分开：正常伸出平滑 / 正常回缩短 / **预测失效时快收**——后者治
        // "急转处旧方向的尾留在屏幕上慢慢缩"（用户 2026-10-08 学校机："写快时急转偶尔跳一下"）。
        float tipLenRaw = 0f;
        if (n > 0 && _tailOffsets.Count > 0)
        {
            var o = _tailOffsets[^1];
            tipLenRaw = MathF.Sqrt(o.X * o.X + o.Y * o.Y);
        }
        // 2026-10-12：**DL 档的速度自适应限速**——快写时收放都要更快。
        // 离线对照（_ml-predict 总表 §八）：固定限速在快直线上吃掉 ~17px 滞后（13→30），
        // 自适应收回 ~80%（30→16.6）且乱甩仍跟得住。B4 档保持原常数（它当时是按原始
        // 突发输入调的，不动）。
        float extRate = 0.20f, retRate = 0.11f, fastRate = 0.72f;
        float rate = n <= 0
            ? fastRate * frameMs                                       // 失效：快收（≈8px/帧@90Hz）
            : (tipLenRaw > _tailLen ? extRate : retRate) * frameMs;    // 正常：伸 2.2 / 缩 1.2（@90Hz）
        _tailLen += Math.Clamp(tipLenRaw - _tailLen, -rate, rate);
        if (_tailLen < 0.4f) _tailLen = 0f;
        if (_tailLen <= 0f || _tailOffsets.Count == 0) return;

        // 锚点优先用"正在显示的平滑输出末点"（接头处连续，不会和笔身错开）；
        // 建模不成功（catmull/raw）时退回原始末点。
        Vector2 anchor;
        float anchorP = -1f;
        if (StrokeMotion.Build(ActiveStroke) && StrokeMotion.Count > 0)
        {
            var m = StrokeMotion.At(StrokeMotion.Count - 1);
            anchor = new Vector2(m.X, m.Y);
            anchorP = m.Z;
        }
        else
        {
            var r = ActiveStroke.Points[^1];
            anchor = new Vector2(r.X, r.Y);
        }

        // 尾宽：**与笔身末点同一来源**——压感取"建模输出末点"的压力，而不是原始末点
        //（原始压力可能是起笔那一下的尖峰，会让尾巴比笔身胖出一圈，看起来就是"笔头冒个圆"；
        // 2026-10-08 用户报的"起笔先出个圆"就是它＋短尾的观感）。宽度再做一阶平滑防闪。
        float pForWidth = anchorP >= 0f ? anchorP : ActiveStroke.Points[^1].P;
        float wTarget = ActiveStroke.HasPressure
            ? MathF.Max(1f, PressureWidth.HalfWidth(ActiveStroke.Width, pForWidth) * 2f)
            : MathF.Max(1f, ActiveStroke.Width);
        _tailWidthState = _tailWidthState <= 0f ? wTarget : _tailWidthState + (wTarget - _tailWidthState) * 0.3f;
        _tailWidth = _tailWidthState;

        // 太短的尾不画：圆端帽下"又短又粗"的一截看起来就是个圆点/圆头（同一条反馈）。
        if (_tailLen < 2f) return;

        var tipOff = _tailOffsets[^1];
        float tipOffLen = MathF.Max(1e-3f, MathF.Sqrt(tipOff.X * tipOff.X + tipOff.Y * tipOff.Y));
        float scale = _tailLen / tipOffLen;
        _tailCanvas.Add(anchor);
        for (int i = 0; i < _tailOffsets.Count; i++)
            _tailCanvas.Add(anchor + _tailOffsets[i] * scale);
    }


    /// <summary>录制缓冲：**识别器这一笔看到的那串点**（`TickDwellShape` 每次攒一份）。
    /// 只存引用、不复制 —— 没开录制时一个字节都不花。</summary>
    private Vector2[] _recordPts;

    /// <summary>这一笔认成了什么 / 为什么没认（写进录制文件的段头，方便按"失败的样本"归类）。</summary>
    private string _recordGuess = "";

    /// <summary>录制文件里的笔序号（从 1 开始）。</summary>
    private int _recordSeq;

    /// <summary>**两笔配对的时间窗口**（毫秒）。原来是用户 2026-09-25 定的 **5 秒**，
    /// **2026-09-26 放宽到 20 秒**（用户报"画几次才成一次"之后核对的）。
    ///
    /// ⚠ 5 秒为什么不够：这个窗口量的是"**上一笔收笔那一刻**"到"**这一笔停顿**"之间的时间，
    /// 也就是"**画第二支花了多久 ＋ 停顿**"—— 一笔画得慢一点（想一下、比一下两支对不对称）
    /// 就超了。实测这对语料里的时间分布没有意义（自检是推时钟的），但从"画一支要几秒"
    /// 这个量级看，5 秒**必然**经常不够。
    ///
    /// ⚠ 放宽**没有引入新的误配风险**：挑"上一笔"的第一条就是"**文档里最后一笔**"，
    /// 所以中间只要画了别的，`prev` 就已经换人了；这个窗口只是"别跟很久以前那一笔硬凑"
    /// 的一道兜底。真要误配，挡住它的是那条**中心对称**校验（自检里"两笔不相干
    /// （同一边画两遍）→ 不许变成双曲线"盯着它）。</summary>
    internal const double TwoBranchWindowMs = 20000;

    /// <summary>其它图形：**识别那一刻的定义元素点**（幽灵改大小的基准，见 ArmedStrokeMove）。
    /// 存"基准"而不是"累计位移"：一律从基准算**绝对值** —— 累计会跟着亚像素抖动漂，
    /// 直线那边的起步死区（<see cref="DwellDragSlopLogical"/>）就是为了这件事。
    /// 直线不吃这一位（它拖另一头改形状）。</summary>
    private Vector2[] _dwellGhostOrigin;

    /// <summary>幽灵期**不动的那个定义元素**的下标（锚点）。判据 = **离笔尖最远**的那一个
    /// —— 和直线的 <see cref="_dwellLinePin"/> 是**同一条判据**（用户 2026-09-25 定：
    /// "圆形圆心不动、矩形有一个顶点不动"；人收笔的位置就在"终点"那一带，所以远端才是起点）。
    /// 成型时挑定、拖动全程不变。−1 = 这一笔不吃幽灵改大小。</summary>
    private int _dwellGhostAnchorIdx = -1;

    /// <summary>幽灵期**跟着笔尖走的那个定义元素**的下标。判据 = **离笔尖最近**的那一个
    /// （就是"用户拖出来的那个终点"）。和锚点一起在成型时挑定。</summary>
    private int _dwellGhostNearIdx = -1;

    /// <summary>
    /// armed 之后"按住拖动"的**起步死区**（逻辑像素）：笔尖在这个范围内动，线一个像素都不改。
    ///
    /// 为什么非有不可（和 `DwellAssist.DeadZoneLogical` 同一个道理，但后果更凶）：
    /// 笔尖"静止"按在屏幕上时，驱动仍在上报亚像素抖动。没有死区的话，那段抖动会被当成
    /// "用户在拖"——**直线最惨**：笔尖就停在"跟着笔尖走的那一头"，抖 1 个画布单位就够
    /// 把线压成零长度（`SnapEndPoint` 对零长度原样返回）→ **整条线只剩一个点**
    /// （用户 2026-09-24 报的"竖着画的直线变成很短的一个横直线"；`--dwelltest` H6 钉住：
    /// 修之前那一档漂移 299.51 画布单位，整条 300 的线只剩一个点）。
    /// </summary>
    internal const float DwellDragSlopLogical = 5f;

    /// <summary>
    /// 操作条那一块现在画成**收起来那一颗圆钮**吗（而不是一整条九格）。
    /// 两种来源：用户自己收的（<see cref="SelBarCollapsed"/>）、**画完自动选中那个框**
    /// （见 <see cref="_autoSelCollapsed"/>）。
    ///
    /// ⚠ 绘制 / 命中 / 光标**都必须问这一条**：只判 `SelBarCollapsed` 的话，
    /// 自动选中那个框会"画的是圆钮、点的是一整条"，两边对不上
    ///（"同一个名单写在多处必漏一处"，见 架构-分层与规则.md 五-7）。
    /// </summary>
    internal bool BarDrawnCollapsed => SelBarCollapsed || _autoSelCollapsed;

    /// <summary>当前开着的浮动面板（同一时刻只开一个）。见 <see cref="SelPanel"/>。</summary>
    internal SelPanel SelPanelOpen = SelPanel.None;

    /// <summary>
    /// **自定义取色板开着吗**（8.2.0 加，`SelectionSwatches` 最后一格点开的那个小色板）。
    ///
    /// 它是墨迹面板的**子面板**：面板关 / 换面板 / 收条 / 选区没了，它都跟着关
    ///（见 CloseCustomColor），统一收在一处，免得出现"面板关了色板还挂在屏幕上"。
    /// </summary>
    internal bool CustomColorOpen;

    /// <summary>
    /// 正在拖颜色面板里的粗细滑条。
    ///
    /// 8.2.0 起它是**连续滑条**（不再是"点一下就吸到档位"）：拖动期间动作直接作用在
    /// 选中对象上（`_widthDragAction` 反复 Redo，屏幕实时变），松手才挂上撤销栈
    /// ——「一次拖拽 = 一步撤销」。
    /// </summary>
    private bool _sliderDragging;

    /// <summary>粗细滑条这一次拖拽对应的动作（拖动中反复改目标值；松手提交或丢弃）。</summary>
    private SetStrokePropAction _widthDragAction;
    /// <summary>按下那一刻滑条的值（松手要不要提交的判据之一）。</summary>
    private float _widthDragStartValue;
    /// <summary>拖拽中滑条的值（逻辑像素；画数值和判"没变就不重画"都用它）。</summary>
    private float _widthDragValue;

    /// <summary>拖动中的粗细值（逻辑像素）——绘制那行数值、自检读值都走它。</summary>
    internal float WidthSliderDragValue => _widthDragValue;
    /// <summary>鼠标正按在粗细滑条上吗（绘制"拖动中显示数值"那条判据）。</summary>
    internal bool WidthSliderDragging => _sliderDragging;
    /// <summary>取色板里当前选的 HSV（0..1）——绘制和自检都读它。</summary>
    internal (float H, float S, float V) PickHsv => (_pickH, _pickS, _pickV);

    /// <summary>粗细滑条这一次拖拽的目标（按下那一刻的非图像选中对象）。</summary>
    private readonly List<Stroke> _propDragTargets = new();

    /// <summary>
    /// **自定义取色板**（8.2.0）的三件状态：
    ///   · `_pickH/_pickS/_pickV` = 色板里当前选的 HSV（0..1）；
    ///   · `_colorDragging` / `_colorDragOnHue` = 正在拖色相条还是饱和度/明度方块；
    ///   · `_colorDragAction` = 这一次拖拽的属性动作（同名一套：松开在色板里 = 应用，
    ///     松开在外面 = 撤销并丢弃 —— 见 <see cref="EndColorPickerDrag"/>）。
    /// </summary>
    private float _pickH, _pickS = 1f, _pickV = 1f;
    private bool _colorDragging;
    private bool _colorDragOnHue;
    private SetStrokePropAction _colorDragAction;

    /// <summary>复制成功之后选区"闪一下"的到期时刻（0.25 秒，见 Overlay.DrawSelection）。</summary>
    internal double SelFlashUntilMs;

    /// <summary>复制成功时选区闪一下的时长（毫秒）。</summary>
    public const double SelFlashMs = 250;
    /// <summary>
    /// 批注键盘模式。开 = 键盘归批注层（编辑快捷键生效）；
    /// 关 = 覆盖层永不抢焦点，键盘还给下层程序。
    ///
    /// 这是个**明确的取舍**：开着的时候放映中的 PPT 收不到键盘，因为覆盖层
    /// 拿着前台焦点。用户拍板"先不考虑 PPT，先把底层做好"，所以默认开，
    /// 用 Ctrl+Alt+K 切换。
    /// </summary>
    internal bool KeyboardMode = true;

    /// <summary>
    /// 相机：画布坐标 → 屏幕坐标的纵向偏移。**滚动只改这一个数**，
    /// 不动任何对象数据——这是滚动能做到 O(1) 的前提。
    ///
    /// 往下滚 = 内容上移 = 这个值变负。只做纵向：板书是纵向累积的，
    /// 横向没有用武之地，还省掉双指横扫与"画横线"的手势冲突。
    /// </summary>
    internal float ViewOffsetY;

    /// <summary>
    /// 相机：画布坐标 → 屏幕坐标的横向偏移。滚动仍然只动 Y；
    /// 这个数留给缩放后的横向平移（阶段 1 恒为 0，行为与以前完全一致）。
    /// </summary>
    internal float ViewOffsetX;

    /// <summary>
    /// 相机缩放（画布 → 屏幕）。**阶段 1 恒为 1f**：只铺管线，不启用缩放。
    ///
    /// 约定（唯一一套，全仓库必须走下面四个 helper）：
    ///   screen = canvas * ViewScale + ViewOffset；
    ///   canvas = (screen − ViewOffset) / ViewScale。
    /// </summary>
    internal float ViewScale = 1f;

    /// <summary>屏幕 X → 画布 X（相机）。</summary>
    internal float ScreenToCanvasX(float sx) => ViewScale > 0f ? (sx - ViewOffsetX) / ViewScale : sx;

    /// <summary>屏幕 Y → 画布 Y（相机）。</summary>
    internal float ScreenToCanvasY(float sy) => ViewScale > 0f ? (sy - ViewOffsetY) / ViewScale : sy;

    /// <summary>画布 X → 屏幕 X（相机）。</summary>
    internal float CanvasToScreenX(float cx) => cx * ViewScale + ViewOffsetX;

    /// <summary>画布 Y → 屏幕 Y（相机）。</summary>
    internal float CanvasToScreenY(float cy) => cy * ViewScale + ViewOffsetY;

    /// <summary>算"一屏在画布里的高度/宽度"时用的除数；非法缩放值按 1 处理。</summary>
    internal float ViewScaleSafe => ViewScale > 0f ? ViewScale : 1f;

    /// <summary>
    /// 横向相机偏移的夹取（阶段 1 恒为 0；缩放后左右拖页用）。
    /// 和纵向的 `ClampOffset`（见 Engine.Cursor.cs）同一套判据。
    /// </summary>
    private float ClampOffsetX(float x)
    {
        // 100% 时横向相机必须严格为 0：这是"阶段 1 逐像素不变"的硬保证。
        // 100% 以下（文档缩到 50~75%）允许横向摆正——否则缩小后页会贴死在左边。
        if (MathF.Abs(ViewScale - 1f) <= 0.0005f) return 0f;
        var extent = CanvasExtent;
        // 左右各夹到"内容边贴住视口边"：x 的正方向 = 内容右移。
        float minX = _virtualX + _virtualW - extent.MaxX * ViewScale;   // 内容右边缘贴视口右边缘
        float maxX = _virtualX - extent.MinX * ViewScale;               // 内容左边缘贴视口左边缘
        if (maxX < minX) { float t = minX; minX = maxX; maxX = t; }
        return Math.Clamp(x, minX, maxX);
    }

    // =====================================================================
    //  文档缩放（v1：只缩文档，50/75/100/125/150/200）
    //
    //  参考：Acrobat / 浏览器 PDF 阅读器的"以视口中心缩放"——缩放前后
    //  **中心压着的那个画布点不动**，用户不会"点一下放大就找不到刚才看的地方"。
    //  只缩文档（白板 / 桌面批注不缩）：页和墨一起缩，页位图按新倍率重渲。
    // =====================================================================

    /// <summary>文档缩放档（1 = 100%）。只在文档开着时有效，关文档 / 打开新文档回到 1。</summary>
    internal float DocZoom = 1f;

    /// <summary>固定档位的下限（`−` / `+` / 百分比档位只走 50~200）。</summary>
    internal const float DocZoomMin = 0.5f;
    internal const float DocZoomMax = 2f;
    /// <summary>
    /// **"适合整页"的硬下限**（2026-11-16）：A4 竖页在 16:9 屏上要缩到 ~40% 才装得下。
    /// 只有 <see cref="FitDocPageToView"/> 用这个下限；`−` / `+` 和百分比档位仍然 50~200
    /// （否则用户点一次"适合整页"就再也回不到 50% 这一档的语义了）。
    /// </summary>
    internal const float DocZoomFitMin = 0.4f;

    /// <summary>缩放档位（v1 只有这六档；不做 300%）。</summary>
    internal static readonly float[] DocZoomSteps = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };

    /// <summary>
    /// 文档缩放（界面 / 自检共用）。**只有文档开着才有意义**，其它情况是空操作。
    ///
    /// 相机换算：屏幕中心压着的画布点 (cx,cy) 在缩放后仍压在同一处：
    ///   offset = centerScreen − c × scale。
    /// 然后按 X/Y 夹取（夹取可能让中心点偏一点：页比视口窄时"不越过页边"优先）。
    /// </summary>
    internal void SetDocZoomFromUi(float zoom)
    {
        SetDocZoomCore(zoom, DocZoomMin,
            _virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.5f);
    }

    /// <summary>
    /// 缩放核心：<paramref name="minZoom"/> = 这一次允许的下限（"适合整页"传
    /// <see cref="DocZoomFitMin"/>，其余传 <see cref="DocZoomMin"/>）；
    /// <paramref name="anchorScreenX/Y"/> = 缩放前后压住不动的那个屏幕点
    /// （界面按钮 = 视口中心；Ctrl+滚轮 = 指针位置）。
    /// </summary>
    private void SetDocZoomCore(float zoom, float minZoom, float anchorScreenX, float anchorScreenY)
    {
        if (!DocView.IsOpen) return;
        if (float.IsNaN(zoom) || float.IsInfinity(zoom)) return;
        zoom = Math.Clamp(zoom, minZoom, DocZoomMax);
        if (MathF.Abs(zoom - ViewScale) < 0.0005f) { DocZoom = zoom; return; }
        DismissDocResume("缩放");              // 缩放 = 换观察位置，提示先收（不跳）

        float cx = ScreenToCanvasX(anchorScreenX);
        float cy = ScreenToCanvasY(anchorScreenY);

        _camAnimating = false;                 // 缩放和翻页动画不能同时抢相机
        ViewScale = zoom;
        ViewOffsetX = anchorScreenX - cx * zoom;
        ViewOffsetY = anchorScreenY - cy * zoom;
        CenterDocPageIfFits(zoom);             // ≤100% 且整页装得下 → 横向摆正到视口中心
        ClampViewOffset();

        DocZoom = zoom;
        DocView.SetViewScale(ViewScale);       // 页位图按新倍率重渲（≤100% 不重渲）
        _dirty = true;
        Console.WriteLine($"[文档] 缩放 {MathF.Round(zoom * 100f):F0}%"
            + $"（页位图 {DocView.PageRenderScale:F2}x / 相机 {ViewScale:F2}x）");
        NoteDocResumeChange();
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **缩到 100% 及以下时把页横向摆正**（2026-11-16 用户要求）：
    /// 只要"页宽 × 倍率 ≤ 视口宽"（整页装得下），就让页中心压着视口中心：
    ///   `ViewOffsetX = 视口中心 X − 页中心 X × ViewScale`
    /// 然后照旧夹进合法范围。这样从"200% 拖到左边"缩回 100% / 50% 时页不会贴死在左边。
    ///
    /// **100% 是例外**：那条路必须逐像素保持原样（`ViewOffsetX` 严格 0、页锚在主屏中心），
    /// 所以这里不在 100% 时算"居中"，交给 <see cref="ClampOffsetX"/> 归零。
    /// &gt;100% 不碰：页比视口宽，横向自由拖 + 夹取（现状）。
    /// </summary>
    private void CenterDocPageIfFits(float zoom)
    {
        if (!DocView.IsOpen || DocView.Count <= 0) return;
        if (zoom > 1.0001f || MathF.Abs(zoom - 1f) <= 0.0005f) return;
        int i = Math.Clamp(BarPageNow - 1, 0, DocView.Count - 1);
        var p = DocView.At(i);
        if (p == null) return;
        float pageW = (p.Rect.MaxX - p.Rect.MinX) * zoom;
        if (pageW > _virtualW + 0.5f) return;              // 页仍比视口宽：保持自由横拖
        float pageCx = (p.Rect.MinX + p.Rect.MaxX) * 0.5f;
        ViewOffsetX = _virtualX + _virtualW * 0.5f - pageCx * zoom;
        ViewOffsetX = ClampOffsetX(ViewOffsetX);
    }

    /// <summary>放大一档（到头就停在 200%）。</summary>
    internal void DocZoomIn() => SetDocZoomFromUi(NextDocZoomStep(+1));

    /// <summary>缩小一档（到 50% 就停；"适合整页"缩到 40% 时这一下不动）。</summary>
    internal void DocZoomOut()
    {
        if (DocZoom <= DocZoomMin + 0.0005f) return;
        SetDocZoomFromUi(NextDocZoomStep(-1));
    }

    private float NextDocZoomStep(int dir)
    {
        if (dir > 0)
        {
            foreach (var z in DocZoomSteps)
                if (z > DocZoom + 0.0005f) return z;
            return DocZoomMax;
        }
        for (int i = DocZoomSteps.Length - 1; i >= 0; i--)
            if (DocZoomSteps[i] < DocZoom - 0.0005f) return DocZoomSteps[i];
        // 已经低于 50%（只可能是"适合整页"留下的 40%）：不再往 50% **放大**。
        return MathF.Min(DocZoom, DocZoomMin);
    }

    /// <summary>
    /// **"适合整页"现算的目标倍率**（把当前页整页装进视口）。
    /// 参考 Acrobat / Edge 的 Fit Page：`min(视口宽/页宽, 视口高/页高)`。
    /// A4 竖页在 16:9 屏上 ≈ 0.40，所以这里是**唯一**允许低于 50% 的地方。
    /// </summary>
    internal float DocFitPageZoom()
    {
        if (!DocView.IsOpen || DocView.Count <= 0) return DocZoom;
        int i = Math.Clamp(BarPageNow - 1, 0, DocView.Count - 1);
        var r = DocView.At(i).Rect;
        float w = MathF.Max(1f, r.MaxX - r.MinX);
        float h = MathF.Max(1f, r.MaxY - r.MinY);
        float z = MathF.Min(_virtualW / w, _virtualH / h);
        return Math.Clamp(z, DocZoomFitMin, 1f);
    }

    /// <summary>执行"适合整页"（围绕视口中心缩放，保证整页都看得见）。</summary>
    internal void FitDocPageToView()
    {
        if (!DocView.IsOpen) return;
        SetDocZoomCore(DocFitPageZoom(), DocZoomFitMin,
            _virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.5f);
    }

    /// <summary>
    /// Ctrl + 滚轮缩放（只缩文档；**围绕指针**，指针压着的画布点不动）。
    /// 返回 true = 这一下被缩放吃掉，不再走普通滚轮。
    /// 普通滚轮（没有 Ctrl）照旧滚画布——见 HandleWheel。
    /// </summary>
    internal bool DocZoomWheelFromUi(int delta, float screenX, float screenY)
    {
        if (!DocView.IsOpen || PptMode || BoardOn || delta == 0) return false;
        float target = NextDocZoomStep(delta > 0 ? +1 : -1);
        if (MathF.Abs(target - DocZoom) < 0.0005f) return true;   // 已到头：吃掉，不改相机
        SetDocZoomCore(target, DocZoomMin, screenX, screenY);
        return true;
    }

    /// <summary>
    /// 最后一次"滚动条该露面"的时刻（滚动或悬停）。滚动条按这个时间淡出——
    /// 静止 3 秒后消失（InkClass 实测 1.5 秒太快，用户会找不到它）。
    /// </summary>
    internal double ScrollBarActiveAtMs = double.MinValue;

    /// <summary>指针正停在滚动条上：滑块加粗、不淡出。拖动中也算（滑块不能半路淡掉）。</summary>
    internal bool ScrollBarHover;
    internal bool ScrollBarDragging;

    /// <summary>
    /// 滚动条总开关。**2026-10-09 用户拍板：停用。**
    /// 理由（用户归纳）：PDF / PPT / 桌面批注都用不到它；白板本来就看得到"第几屏"；
    /// 右缘 16px 抓取带会吃掉"写到边缘"的起笔（笔也一样）；触摸拖它还有过
    /// "幽灵手指"卡死后续输入的 bug（见 OnPointerUp 里的防御注释）。
    /// 代码 / 几何 / 自检全部保留，只是不画、不接管；
    /// `--scrollshow` 对照模式会临时打开它（见 App.ScrollShowcase）。
    /// </summary>
    internal static bool ScrollBarEnabled = false;

    /// <summary>Overlay（别处类型）绘制用：静态字段在那里名字不好写，给它一个实例读法。</summary>
    internal bool ScrollBarVisible => ScrollBarEnabled;
    /// <summary>按下时指针相对滑块顶边的偏移，拖动中保持它不变，滑块才不"跳"。</summary>
    private float _scrollGrabDy;
    private OverlayWindow _scrollWindow;

    /// <summary>屏幕坐标 → 画布坐标（相机）。输入进来第一件事就是过这个。</summary>
    internal void ScreenToCanvas(ref float x, ref float y)
    {
        x = ScreenToCanvasX(x);
        y = ScreenToCanvasY(y);
    }

    /// <summary>当前视口在**画布坐标**里的范围（相机之后）。</summary>
    internal RectF ViewportCanvas => new()
    {
        MinX = ScreenToCanvasX(_virtualX),
        MinY = ScreenToCanvasY(_virtualY),
        MaxX = ScreenToCanvasX(_virtualX + _virtualW),
        MaxY = ScreenToCanvasY(_virtualY + _virtualH),
    };

    /// <summary>
    /// **选中家具**（操作条 / 收起圆钮 / 颜色·层级·取色面板）这一刻可用的画布范围：
    /// 和 <see cref="ViewportCanvas"/> 同一套坐标，但把**主工具条和色带**那块让出去。
    ///
    /// 为什么必须让：UI 是后画的，主工具条压在上面；操作条原来只按屏幕底边夹，
    /// 选区靠下时整条会钻到工具条底下——看得见框、找不到按钮。条放大到 40 以后
    /// 这件事更容易发生，所以 2026-10-25 把它提到和"界面块"同一套判据。
    ///
    /// 判据只看界面在**屏幕哪一半**：
    ///   · 贴底（默认）→ 家具的下边停在工具条上沿再留 8；`BarRect` 自己的 16 下限再叠一层
    ///     （实际留 24，给投影和描边让位）；
    ///   · 拖到上半屏 → 家具的上边让到工具条下沿再留 8（带子在工具条下方）。
    /// 界面没挂 / 尺寸退化时原样返回——绝不因为算不出界面就把家具夹到屏幕外。
    /// </summary>
    internal RectF FurnitureViewport
    {
        get
        {
            var v = ViewportCanvas;
            var ui = UiQueryBoundsNow();
            if (ui.IsEmpty) return v;

            float unit = UiUnit;
            float uiTop = ui.MinY * unit;
            float uiBottom = ui.MaxY * unit;
            float screenMidY = (VirtualScreen.MinY + VirtualScreen.MaxY) * 0.5f;

            // `ViewportCanvas` 是**画布坐标**（屏幕 Y 减了相机偏移），界面的上下沿是
            // **屏幕坐标**——滚过画布以后两者差一个 ViewOffsetY。不换算的话，
            // "避开工具条"会在滚动后整体偏一截（条要么钻下去、要么被推得太高）。
            float uiTopCanvas = ScreenToCanvasY(uiTop);
            float uiBottomCanvas = ScreenToCanvasY(uiBottom);

            if ((uiTop + uiBottom) * 0.5f >= screenMidY)
            {
                // 工具条在下半屏：下边不许低于它的上沿。
                float limit = uiTopCanvas - 8f * unit;
                if (limit > v.MinY) v.MaxY = MathF.Min(v.MaxY, limit);
            }
            else
            {
                // 工具条在上半屏：上边不许高于它的下沿。
                float limit = uiBottomCanvas + 8f * unit;
                if (limit < v.MaxY) v.MinY = MathF.Max(v.MinY, limit);
            }

            if (v.MaxX <= v.MinX || v.MaxY <= v.MinY) return ViewportCanvas;
            return v;
        }
    }

    /// <summary>
    /// 画布范围 = 内容边界 ∪ 当前视口 ∪ **视口下方一屏空白**。
    ///
    /// 最后那一屏是必须的：只取"内容 ∪ 视口"的话，**空文档的画布恰好一屏**，
    /// 下边界就是 0，往下滚立刻被夹回去——而滚不动就写不到下面去，写不到下面
    /// 内容就不长，内容不长范围就不长……**死锁**。
    /// （实测踩过：用户一打开软件，滚轮完全没反应，就是这个。）
    ///
    /// 留一屏空白等于"永远有一张白纸在下面"，像记事本一样。
    /// </summary>
    internal RectF CanvasExtent
    {
        get
        {
            var vp = ViewportCanvas;
            var r = Doc.Extent(vp);
            // 文档页层也要算进画布范围，否则相机到最后一页之前就被夹住（滚不下去）。
            // **白板是独立纸**：板开着时不能再把下面 PDF / 图片的页数算进来，
            // 否则白板会凭空多出很多空白屏，页码也会被底层文档带偏。
            if (!BoardOn && DocView.IsOpen) r.Add(DocView.Extent());
            // "视口下方一屏空白"是**屏幕**一屏：画布单位要除以 ViewScale。
            // 100% 时就是 _virtualH（老行为逐像素不变）；放大时空白跟着变短，
            // 缩小时变长——否则缩放后"滚到底"的位置会差一整屏。
            float oneScreen = _virtualH / ViewScaleSafe;
            r.Add(new RectF
            {
                MinX = vp.MinX, MinY = vp.MaxY,
                MaxX = vp.MaxX, MaxY = vp.MaxY + oneScreen,
            });
            return r;
        }
    }
    // 这两个给自检看（--cursortest 要构造"拖拽中"的状态）。
    internal SelHandle _dragHandle = SelHandle.None;
    internal bool _dragIsMove;
    /// <summary>
    /// 这一次拖动的"点选起手笔迹"（按下时命中的那一条）。松手时若**一点没移动**，
    /// 就把多选收窄成只选它——PPT/Figma 的行为。见 EndSelDrag。
    /// </summary>
    internal Stroke _dragHitStroke;

    // ---- 交互矢量图的参数滑杆（v2.1） ----
    private bool _figureParamDragging;
    private Stroke _figureParamStroke;
    private string _figureParamKey = "";
    private float _figureParamMin, _figureParamMax;
    private string _figureParamOldJson = "";
    internal bool FigureParamDragging => _figureParamDragging;
    internal Stroke FigureParamStroke => _figureParamStroke;
    internal string FigureParamKey => _figureParamKey;
    internal float FigureParamMin => _figureParamMin;
    internal float FigureParamMax => _figureParamMax;

    /// <summary>
    /// 点选的命中容差（逻辑像素）。细笔只有 1.5 逻辑像素宽，严格按墨判等于点不中；
    /// 4 像素和操作条手柄的容差同一量级。见 <see cref="InkDocument.HitObjectAt"/>。
    /// </summary>
    internal const float ClickToleranceLogical = 4f;
    private SelectionFrame _dragFrame;

    /// <summary>
    /// 这一次手势**真的动过**吗（指针离按下那一点超过了点击容差）。
    ///
    /// 只用来决定"装饰要不要收起来"（见 <see cref="SelChromeCollapsed"/>）：
    /// 单击一下不该让手柄和操作条闪一下。
    /// </summary>
    private bool _selDragMoved;

    /// <summary>
    /// 拖动预览（见 <see cref="DragPreviewActive"/>）里**从内容层摘出去**的那一批，
    /// 判定用（内容层重画一块时 O(1) 问"这条要不要跳过"）。见 <see cref="IsContentDetached"/>。
    /// </summary>
    private readonly HashSet<Stroke> _detachSet = new();

    /// <summary>
    /// 同一批，按**文档顺序**排一遍。画的时候用它——
    /// 一个集合的遍历顺序不是 z 序，多选里荧光笔压着字那种情况就会翻来翻去。
    /// </summary>
    private readonly List<Stroke> _detachOrdered = new();

    /// <summary>
    /// 上一帧拖动预览占的画布范围。
    ///
    /// 脏区要写成"上一帧 ∪ 这一帧"：后缓冲里躺着的是两帧前的画面，而这两块并起来
    /// 正好覆盖两帧之间的差集（这和以前"旧位置 ∪ 新位置"是同一套账，见
    /// <c>Overlay.UpdateFrameDirty</c> 的注释）。
    /// </summary>
    private RectF _dragPrevBounds;

    // =====================================================================
    //  图形定义元素编辑（直线 / 箭头 / 圆 / 椭圆）：和"拖对象"同一条快路
    //
    //  手势期**模型一个字不改**，只在浮动层按"临时几何（种类 + 改动后的定义元素）"画，
    //  松手才提交一步改几何的动作（见 计划-图形工具.md 8.1①）。
    //  每帧 SetPoint + 标脏那条路会把受影响的块整块重光栅化——实测 19ms/帧。
    // =====================================================================

    /// <summary>正在拖定义元素改几何（读数标签靠它决定显不显示）。</summary>
    internal bool VertexDragging => _vertexDragging;

    /// <summary>预览要画的那一条（模型此刻没动，它还是旧几何）。</summary>
    internal Stroke VertexPreviewStroke => _vertexDragging ? _vertexTarget : null;

    /// <summary>预览用的**局部坐标**控制点（把拖动中那个定义元素换掉之后的一组点）。</summary>
    internal IReadOnlyList<Vector2> VertexPreviewPoints => _vertexPreviewLocal;

    /// <summary>
    /// 拖**焦点三角形的顶点 P** 时，这一帧算出来的参数角；`NaN` = 这一拖不是拖 P。
    ///
    /// 为什么要单独走一个字段、不塞进 <see cref="VertexPreviewPoints"/>：
    /// **P 根本不是控制点**——它是"参数角"算出来的（见 <see cref="Stroke.ConicEllipsePointLocal"/>），
    /// 塞进点表就等于承认"P 是第 3 个定义元素"，那紧框 / 存档 / 变换都得为它多写一套规矩。
    /// 所以预览只多带**这一个数**，浮动层拿它画预览（见 Overlay 的 DrawVertexPreview）。
    /// </summary>
    internal float VertexPreviewFocusU => _vertexPreviewFocusU;
    private float _vertexPreviewFocusU = float.NaN;

    /// <summary>
    /// 拖**坐标系的格距手柄**（第四颗）时，这一帧算出来的格距；`0` = 这一拖不是拖它。
    ///
    /// 和 `FocusPointU` 同一个道理：**格距不是控制点**（坐标系永远只有三个点，
    /// 见 <see cref="StrokeKind.Coordinate"/>），所以预览只多带这一个数，
    /// 浮动层拿它画预览（见 Overlay 的 DrawVertexPreview 里那个 `g.AxisGridStep`）。
    /// 用手势开始时那个 `0` 当"没在拖"的记号，是因为格距本身**不许是 0**
    ///（0 = 自动，见 <see cref="Stroke.AxisGridStep"/> 的注释）。
    /// </summary>
    internal float VertexPreviewGridStep => _vertexPreviewGridStep;
    private float _vertexPreviewGridStep;

    /// <summary>拖动中的那个元素在**画布坐标**里的位置（读数标签贴在它外侧）。</summary>
    internal Vector2 VertexPreviewCanvasPoint => _vertexPreviewCanvas;

    /// <summary>读数该显示哪一种量（直线是 α、圆是 r/d、椭圆是 a 或 b）。</summary>
    internal VertexReadoutKind VertexReadout => _vertexReadout;

    /// <summary>读数的主数值（α / r / a / b）；单位由 <see cref="VertexReadout"/> 决定。</summary>
    internal float VertexReadoutValue => _vertexReadoutValue;

    /// <summary>读数的次要值（只有圆用：直径 d = 2r）；其它情况是 0。</summary>
    internal float VertexReadoutSecondary => _vertexReadoutSecondary;

    /// <summary>读数是不是"吸"出来的（软吸附 / Shift 网格）——标签按它变色。</summary>
    internal bool VertexInclinationSnapped => _vertexSnapped;

    /// <summary>
    /// 拖顶点 / 轴端点 / 四角时**吸到了什么**（规格 9.6）。`None` = 这一帧没吸住
    /// （也可能这一拖根本不适用吸附——整体移动、旋转就永远是 `None`）。
    /// 胶囊上的字由它决定（<see cref="SelectionHandles.ShapeSnapLabel"/>）。
    /// </summary>
    internal ShapeSnapKind ShapeSnapKind => _shapeSnap;

    /// <summary>吸住时那颗胶囊贴哪儿（画布坐标）：拖元素 = 被拖的那个元素、拖四角 = 指针。</summary>
    internal Vector2 ShapeSnapAnchor => _shapeSnapAnchor;

    /// <summary>
    /// 吸附胶囊的**具体文案**（坐标系原点 / 轴端用）：比如"左边缘 + 垂直居中"。
    /// 为空时回落到 <see cref="SelectionHandles.ShapeSnapLabel"/>。
    /// </summary>
    internal string ShapeSnapText => _shapeSnapText;

    private bool _vertexDragging;

    /// <summary>
    /// **多笔图形**已经拖完几笔（0 = 还没开始）。画法与"一共有几笔"都在
    /// <see cref="PlanOf"/> 那张表里（目前只有双曲线：**两笔**）。
    ///
    /// 规则是照 **InkClass** 的（用户 2026-09-20 定："按照他的这个规则复刻"）——
    /// 它的多笔图形就是"**一笔 = 按下-拖-松手**"，松手推进下一笔
    ///（`MW_ShapeDrawing.cs` 的 `drawMultiStepShapeCurrentStep` ＋ `inkCanvas_MouseUp`）：
    ///
    ///   · **双曲线两笔**（`drawingShapeMode = 24/25`，`MW_ShapeDrawing.cs:1051-1152`）：
    ///     ① **从中心拖出渐近线**（拖到哪、`A / B` 就是多少）→ 松手**锁住**，此后不再变；
    ///     ② **再拖一下**：拖到哪、**曲线就经过哪**（中心沿用第 1 笔那个，
    ///        InkClass 的 `NeedUpdateIniP()` 在第二笔刻意**不重设 `iniP`**，`:1971-1977`）。
    ///
    /// 中途几笔松手**都不算完**：半成品留在 `ActiveStroke`（**不进文档、不写撤销记录**），
    /// 最后那一笔松手才统一提交 —— 所以两笔合起来**只有一条撤销记录**（InkClass 也是
    /// 这个口径：`:1846-1850` 把渐近线笔画和曲线笔画塞进同一个提交里）。
    /// </summary>
    private int _stepIndex;

    /// <summary>
    /// 正在画的这个半成品用的是哪张表（null = 不是多笔图形）。它和 <see cref="_stepIndex"/>
    /// 一起回答"**还有没有下一笔**"，所以换工具 / 认输时两件都要清（见 <see cref="SwitchTool"/>）。
    /// </summary>
    private StepPlan _stepPlan;

    /// <summary>
    /// 多步图形的**第一个点**（双曲线的中心 / 抛物线的顶点，画布坐标）：后面几步都围着它转。
    /// </summary>
    private Vector2 _stepOrigin;
    private Stroke _vertexTarget;
    /// <summary>被拖的那个**定义元素**（圆是圆心/圆周点、椭圆是中心/四个轴端点、多边形是顶点）。</summary>
    private ShapeHandle _vertexHandle;
    private Vector2[] _vertexPreviewLocal;
    private Vector2 _vertexPreviewCanvas;
    private VertexReadoutKind _vertexReadout;
    private float _vertexReadoutValue;
    private float _vertexReadoutSecondary;
    private bool _vertexSnapped;
    /// <summary>这一帧的"特殊形状吸附"结果（见 <see cref="ShapeSnapKind"/>）。</summary>
    private ShapeSnapKind _shapeSnap;
    private Vector2 _shapeSnapAnchor;
    /// <summary>坐标系吸附时的具体文案（为空 = 用枚举的通用文案）。</summary>
    private string _shapeSnapText = "";
    /// <summary>这次拖坐标系轴端时，起手那一刻的格距（用来判"长度是不是整数倍格距"）。</summary>
    private float _vertexAxisGridStep;
    /// <summary>轴端吸到网格时要把格距钉住；提交时一起写进对象（0 = 不钉）。</summary>
    private float _vertexPinGridStep;
    /// <summary>上一帧"被拖的那个元素"附近的脏矩形（这一帧要连它一起标）。</summary>
    private RectF _vertexPrevBounds;

    /// <summary>
    /// 拖定义元素时的最小尺寸（**逻辑像素**）：圆的半径、椭圆的半轴都不许小于它。
    ///
    /// 理由和"画图时拖动距离短于 4 逻辑像素就不提交"是同一个：退化成线段 / 点的图形
    /// 在屏幕上看不见，却还占着一条对象、还能被框选到——是最难解释的一类杂物。
    /// 用户对椭圆的要求原话是"a、b 各要有最小值（不许退化成线段）"。
    /// </summary>
    internal const float ShapeMinAxisLogical = 4f;

    /// <summary>
    /// 坐标系原点 / 轴端的吸附半径（**逻辑像素**）：8 逻辑像素，和"用户能感到吸住"
    /// 的常规手感一致（`Alt` 临时关闭）。
    /// </summary>
    internal const float CoordinateSnapRadiusLogical = 8f;

    /// <summary>拖定义元素时，那个读数**是什么量**（决定胶囊上写 α / r / a / b）。</summary>
    internal enum VertexReadoutKind
    {
        None = 0,
        /// <summary>直线的倾斜角 α（[0°,180°)）。</summary>
        Inclination,
        /// <summary>圆的半径 r（胶囊上顺带写直径 d = 2r）。</summary>
        Radius,
        /// <summary>椭圆的横半轴 a。</summary>
        AxisA,
        /// <summary>椭圆的纵半轴 b。</summary>
        AxisB,
        /// <summary>
        /// 抛物线：课本里那个 **p**（`y² = 2px` / `x² = 2py` 里的 p）。
        ///
        /// 为什么报 p 而不是"半宽多少像素"：焦点 `(p/2, 0)`、准线 `x = −p/2` 全从它来，
        /// 而且**四种开口同一个式子**（p = 半跨² ÷ (2 × 深度)）——不需要按方向分文案。
        /// </summary>
        ParabolaP,
        /// <summary>双曲线：**实半轴**（曲线自己的那一半，实轴沿 y 时报的是纵向那个）。</summary>
        HyperbolaReal,
        /// <summary>双曲线：**虚半轴**（曲线自己的另一半；**不是**渐近线框的 A / B——那两个只画虚线）。</summary>
        HyperbolaImag,
        /// <summary>正弦 / 余弦：**周期 T**（一个周期有多宽；secondary 是振幅 A）。</summary>
        WavePeriod,
        /// <summary>扇形：圆心角（度）。</summary>
        SectorAngle,
        /// <summary>坐标系：+x / −x 半轴的长度。</summary>
        AxisXLength,
        /// <summary>坐标系：+y / −y 半轴的长度。</summary>
        AxisYLength,
        /// <summary>坐标系：网格格距。</summary>
        GridStep,
        /// <summary>空间坐标系：x 轴与 y 轴的平面夹角。</summary>
        SpaceAngle,
    }

    /// <summary>正在拖旋转手柄——度数标签靠它决定显不显示。</summary>
    internal bool SelRotating;
    /// <summary>
    /// 当前这一拖一共转了多少度（**逆时针为正、顺时针为负**，相对按下那一刻）。
    ///
    /// **不设上限**（用户 2026-09-15 定："贴合我们高中数学"）：转两圈就是 720°，
    /// 倒着转就是负数，超过 ±180° 也**不绕回**——所以它不是"起点到当前点的夹角"，
    /// 而是每帧一小步累加出来的（见 <see cref="_rotAccumDeg"/>）。
    /// </summary>
    internal float SelRotationDegrees;
    /// <summary>这个角度是"吸"出来的（90° 或 Shift 15° 网格），标签要变色提示。</summary>
    internal bool SelRotationSnapped;

    /// <summary>
    /// **单选一条直线 / 箭头**拖旋转柄时的读数：`按下时的倾斜角 α₀ + 这一拖累积转过的角 Δ`。
    ///
    /// **无上限、也不折角**（用户 2026-09-18 澄清："……逆时针是为正，顺时针为负，它可以无限
    /// 转下去，也就说可以转到 360、720、1000 多度，或者说负的 2000 多度，这都是可以的。
    /// 也就说它是除了初始角度是要调一下以外，其他和原来的最开始的逻辑是一样的。"）。
    /// 一条 α₀ = 45° 的线：逆时针转一圈 = `405°`、再转 = `765°`；顺时针 400° = `−355°`。
    /// 所以它**不是**倾斜角（倾斜角只在 [0°,180°)），标签上也就写成纯数字
    /// （见 SelectionHandles.FormatSignedDegrees）。
    ///
    /// 只在"单选直线/箭头"这一条分支里有意义，由 <see cref="SelRotationReadsInclination"/> 决定
    /// 标签读谁；其它情况（多选 / 图像 / 自由笔迹 / 圆）照旧读 Δ，
    /// 单选一个图形（矩形 / 椭圆 / 三角形 / 平行四边形）读的是姿态角（见 <see cref="SelRotationPose"/>）。
    /// </summary>
    internal float SelRotationInclination;
    /// <summary>这一次旋转的读数是不是 α₀ + Δ（真 = 单选直线/箭头；假 = 照旧读 Δ）。</summary>
    internal bool SelRotationReadsInclination;

    /// <summary>
    /// **单选一个图形**（矩形 / 椭圆 / 三角形 / 平行四边形）拖旋转柄时的读数：这个图形
    /// **相对水平的姿态角**（度，折在 [0°,180°)；0° = 正的、90° = 竖的）。
    ///
    /// 和直线的那个读数有两处**故意不一样**（规格 9.7）：
    ///   · **折在 [0°,180°)**：姿态角只是"这个图形朝哪边躺着"，没有圈数可言
    ///     （直线那个是"从 α₀ 接着转了多少"，用户 2026-09-18 明确要不设上限，两者用途不同）；
    ///   · 它**每帧从最终矩阵里解出来**（见 UpdateSelDrag 末尾），不是"按下时的角 + 累积角"
    ///     一路累加出来的——于是"矩阵与标签是同一个角"是**构造出来的**性质：镜像 / 上下翻转
    ///     过的图形（行列式为负，本地转 +1° 在屏幕上是 -1°）也不会对不上。
    ///     累积角照样在管吸附：吸住了就把差额加回去（和直线那一支是同一套账）。
    /// </summary>
    internal float SelRotationPose;
    /// <summary>这一次旋转的读数是不是姿态角（真 = 单选一个图形；见 <see cref="SelectionHandles.PoseEditable"/>）。</summary>
    internal bool SelRotationReadsPose;

    /// <summary>这次旋转是不是"单选直线/箭头"那一档（按下那一刻判定一次，手势里不再变）。</summary>
    private bool _rotLineLike;
    /// <summary>这次旋转是不是"单选一个图形"那一档（读姿态角；同样按下时判定一次）。</summary>
    private bool _rotPoseLike;
    /// <summary>被转的图形是不是**镜像过**的（行列式为负）：修正要反着加回累积角（见 UpdateSelDrag）。</summary>
    private bool _rotMirrored;
    /// <summary>
    /// 按下那一刻这个**图形**的姿态角（折在 [0°,180°)）——它对应直线那一支的 α₀：
    /// 吸附要在"它 + 累积角"这个**连续**的角上做（折过的值直接吸会把三角形翻 180°，见
    /// SelectionHandles.SnapExpandedDegrees 的注释）。
    /// </summary>
    private float _rotStartPose;
    /// <summary>
    /// 按下那一刻这条线的倾斜角 α₀（折在 [0°,180)）——旋转读数就是从它开始"接着变"的。
    /// 初始角用折过的值是有意的（用户："初始角度是要调一下"）：一条线没有方向，
    /// 按下时读到 45° 而不是 405°，之后才按用户转的方向累加。
    /// </summary>
    private float _rotStartInclination;

    /// <summary>
    /// 本次旋转的**累积角**（度，逆时针为正）。每帧用
    /// <see cref="SelectionHandles.RotationStepDegrees"/> 取一小步加上去。
    ///
    /// 为什么不直接量"起点 → 当前点"：那个量天生只有 ±180° 的分辨率，
    /// 从起点直接甩到 200° 的位置，量出来是 -160°（最短路径），"转了一圈"
    /// 这件事就丢了。一帧一步地累积才记得住转了几圈。
    /// </summary>
    private float _rotAccumDeg;
    /// <summary>上一帧指针在**框坐标**里的位置（算这一帧转了多少用）。</summary>
    private Vector2 _rotPrevPoint;

    private Vector2 _dragStartPoint;
    private Matrix3x2[] _dragStartXform;
    private Stroke[] _dragTargets;
    private Matrix3x2 _selDragMatrix = Matrix3x2.Identity;

    // ---- 对象吸附（2026-10-18）--------------------------------------------
    //
    // 最终口径：**只看单选对象的内容选中框**，不认锚点。
    //   · 参与对象：图形 + 图片（`IsShape`，自由笔迹不参与）；
    //   · 同边对齐：左/左、右/右、上/上、下/下；
    //   · 对边拼接：右/左、下/上（左右或上下刚好贴住）；
    //   · 中心线：横中线对横中线、竖中线对竖中线；
    //   · 中心：两条中心线同时对上。
    //
    // 手势期间只存“这一帧吸住了什么”；模型仍然不动，最终仍由 `_selDragMatrix`
    // 走原来的那一步提交。这样吸附不会多出撤销记录，也不影响非拖动路径。
    internal enum AlignSnapKind
    {
        None = 0,
        Point,       // 点提示：两条中心线同时对上 / 中心对中心
        Horizontal,  // 横线提示：只改 y
        Vertical,    // 竖线提示：只改 x
    }

    /// <summary>一个框在某一轴上的三条线：左/中/右，或上/中/下。</summary>
    private enum AlignLineRole
    {
        Start = 0,
        Center = 1,
        End = 2,
    }

    private struct AlignAxisCandidate
    {
        public AlignLineRole SourceRole;
        public AlignLineRole TargetRole;
        public float Delta;        // 这一轴要补的平移量
        public float TargetLine;   // 画布坐标里的目标线（x 或 y）
    }

    private bool _alignSnapLatched;
    private Stroke _alignSnapTarget;
    private RectF _alignSnapTargetFrame;
    private RectF _alignSnapTargetCurveFrame;
    private bool _alignSnapTargetWave;
    private bool _alignSnapTargetCenterline;
    private bool _alignSnapTargetAxisCenterOnlyX;
    private bool _alignSnapTargetAxisCenterOnlyY;
    private AlignAxisCandidate? _alignSnapX;
    private AlignAxisCandidate? _alignSnapY;

    // 手势开始时一次性收集“图形 + 图片”目标框；自由笔迹不参与。
    // 这样每帧只遍历少量图形/图片，不会去扫几百个汉字的墨迹框。
    private Stroke[] _alignTargets;
    private RectF[] _alignTargetFrames;
    private RectF[] _alignTargetCurveFrames;
    private bool[] _alignTargetWaves;
    private bool[] _alignTargetCenterlines;
    /// <summary>
    /// 轴类目标（直线 / 箭头 / 数轴）在**厚度方向**只认中心线。
    /// 直线本身有笔宽，3×3 框会把上沿、下沿也当成两条可吸附的线；对数学里的
    /// X 轴来说，那只是笔迹边缘，真正要的是轴线中心（用户 2026-10-19）。
    /// </summary>
    private bool[] _alignTargetAxisCenterOnlyX;
    private bool[] _alignTargetAxisCenterOnlyY;
    private bool _alignSourceCenterline;
    private bool _alignSourceWave;
    private RectF _alignSourceCurveFrame;

    // 短亮提示：吸附“刚吸住”的那一下闪一次，不写字、不常驻。
    private AlignSnapKind _alignPulseKind;
    private Vector2 _alignPulsePoint;
    private float _alignPulseHalfLen;
    private double _alignPulseStartMs;
    private double _alignPulseEndMs;

    /// <summary>这帧的吸附短亮还该不该出帧（比可见时间多留一点，最后一帧负责擦干净）。</summary>
    internal bool AlignSnapPulseActive
        => _alignPulseEndMs > 0 && NowMs < _alignPulseEndMs + 40.0;

    /// <summary>短亮本体还在不在可见期。</summary>
    internal bool AlignSnapPulseVisible
        => _alignPulseEndMs > 0 && NowMs < _alignPulseEndMs;

    /// <summary>短亮的透明度（从 1 线性淡出）。</summary>
    internal float AlignSnapPulseAlpha
    {
        get
        {
            double span = Math.Max(1.0, _alignPulseEndMs - _alignPulseStartMs);
            double t = (NowMs - _alignPulseStartMs) / span;
            return (float)Math.Clamp(1.0 - t, 0.0, 1.0);
        }
    }

    internal AlignSnapKind AlignSnapPulseKind => _alignPulseKind;
    internal Vector2 AlignSnapPulsePoint => _alignPulsePoint;
    internal float AlignSnapPulseHalfLen => _alignPulseHalfLen;
    /// <summary>调试开关：不画自己那一笔，只留系统合成器画出来的委托轨迹。</summary>
    internal bool SuppressActiveStroke;
    internal float MqMinX, MqMinY, MqMaxX, MqMaxY;
    internal bool PassThrough;
    /// <summary>
    /// 进穿透前的白板状态——"穿透开关"退出时按它恢复（见 <see cref="SetPassThrough"/> 的
    /// `restoreBoard`；用户 2026-09-30 拍板："白板开还是开、关还是关"）。
    /// 换工具退出穿透**不**恢复（那是"我就要写"，见 SwitchTool 与 SetPassThrough 的注释）。
    /// </summary>
    private bool _boardBeforePassThrough;
    /// <summary>
    /// Windows only honours click-through for a *layered* window, so the
    /// default has to include WS_EX_LAYERED. The other modes are kept as
    /// controls for the automated pass-through test.
    /// </summary>
    internal PassThroughMode PassMode = PassThroughMode.LayeredTransparent;
    internal bool NoContentCache;
    internal double NowMs;

    internal static readonly Color4 PenColor = InkPalette.PenDefault;
    internal static readonly Color4 HighlighterColor = InkPalette.HighlighterDefault;

    // 下面这些标成 internal 而不是 private，是给开发期测试宿主看的：
    // InkTeach 通过 InternalsVisibleTo 继承引擎、直接读内部状态来做自动化
    // 测量。产品界面走 IOverlayUi，不碰这些。
    internal readonly List<OverlayWindow> _windows = new();
    internal IntPtr _hInstance;
    internal string _className;
    private Native.WndProcDelegate _wndProc;
    private static readonly Dictionary<IntPtr, OverlayWindow> s_map = new();
    internal bool _quit;
    internal bool _dirty = true;
    internal bool _drawing;

    /// <summary>这一帧为什么出（位标志）。只在 NeedsFrame 里填；1Hz 日志/慢帧日志读。</summary>
    [Flags]
    private enum FrameDrive
    {
        None = 0,
        Dirty = 1 << 0,
        Laser = 1 << 1,
        Drawing = 1 << 2,
        Selection = 1 << 3,
        UiAnim = 1 << 4,
        Camera = 1 << 5,
        Replay = 1 << 6,
        Timer = 1 << 7,
        Roll = 1 << 8,
        Radial = 1 << 9,
        Tooltip = 1 << 10,
        Trail = 1 << 11,
        Align = 1 << 12,
        TouchFling = 1 << 13,
    }
    private FrameDrive _frameDrive;
    /// <summary>最近一次"用户动作"（按下/按键）的时间：用来识别"没人操作却一直出帧"。</summary>
    private double _lastActivityMs;
    private double _idleWarnAtMs;
    private uint _activePointer;
    private uint _activePointerType;

    // ---- 压感采集与笔迹预测（实现见 Input/PenInput.cs、Prediction/InkPredictorLinear.cs）----
    //  这两件事共用同一条时间轴：采样点自带硬件时标，压感保真靠它，外推预测也靠它。
    private readonly PenSampleBuffer _pen = new();
    /// <summary>
    /// 非笔指针（鼠标 / 触摸）的合并点。手写板**没开 Windows Ink** 时设备是以
    /// `PT_MOUSE` 上报的，触摸屏是 `PT_TOUCH`——这两条路以前一条消息只取一个点，
    /// 快写时轨迹被静默抽稀（见 Input/PointerInput.cs）。
    /// </summary>
    private readonly PointerSampleBuffer _ptr = new();
    private readonly Vector2[] _trailReal = new Vector2[PenSampleBuffer.MaxSamples];
    /// <summary>湿墨**逐点半径**（和 _trailReal 一一对应）：有压感时湿墨也得有粗有细，
    /// 否则抬手那一下粗细会跳（见 <see cref="TrailRadius"/>）。</summary>
    private readonly float[] _trailRadii = new float[PenSampleBuffer.MaxSamples];

    // [试验E，2026-10-11 试过、已撤] 往系统轨迹的 predicted 数组喂"再走一步"的预测点
    //（`--trailpredict`）：鼠标/触摸快速书写时**甩尾明显**（用户真机拍板），整条撤掉。
    // 结论：这条通道的预测点不能用来顶采样间隙——屏幕上的"尾巴"一甩比采样旧一点更难看。

    // [删除 2026-10-05] 老预测系统整条链（`_predictor`/`_predBuf`/`_trailPred`/`_tailScratch`、
    // `PredictEnabled`/`_predictArg`/`PredictPrefKey`、渲染尾与预测尾统计等）：用户决定不接预测。
    // [2026-10-15 收口] 预测三档（经典 B4 / Google / DL）退役、不进编译；唯一实现 = v6
    // （备份 `.revert/2026-10-15-预测收口/`，说明 `调研/已停用-预测算法收口.md`）。

    // ---- 书写期间的分配 / GC 仪表（低配机排查用）--------------------------
    //
    // 低配机上"卡"的第一来源不是平均帧时间，而是**偶发长帧**，其中最凶的一种就是
    // GC 的前台第 2 代回收（要扫整个堆、还可能压缩内存，一次几十毫秒）。
    // 所以这两件事必须能打印出来，否则优化只能靠猜：
    //   · 写一笔到底分配了多少字节（分配越多，越容易触发回收）；
    //   · 这一笔期间真的发生了几次 GC、是哪一代（**第 2 代出现在书写期间 = 危险信号**）。
    /// <summary>这一笔期间本线程的托管分配字节数。</summary>
    internal long StrokeAllocBytes;
    /// <summary>这一笔期间完成的 GC 次数（第 0 / 1 / 2 代）。</summary>
    internal int StrokeGc0, StrokeGc1, StrokeGc2;
    /// <summary>累计统计（`--penlive` 汇总那一行要用）。</summary>
    internal int StrokesMeasured, StrokesWithGc2;
    internal long AllocBytesMax;
    internal double AllocKbSum;

    long _mAlloc0;
    int _mGc0, _mGc1, _mGc2;
    bool _measureDone;
    /// <summary>
    /// 这一笔按下时的时刻。形状识别 / 图形工具生成的点 `T` 可能是 0（不是真实采样时间），
    /// 不能拿 `Points[0].T` 算历时——否则日志会把一笔 2 秒的图形写成“28 分钟”。
    /// </summary>
    double _measureStartMs;

    /// <summary>起仪表：记下这一刻的分配计数和三代 GC 次数。</summary>
    private void BeginStrokeMeasure()
    {
        // GetAllocatedBytesForCurrentThread 只是读一个计数器，很便宜，不会自己触发回收。
        _mAlloc0 = GC.GetAllocatedBytesForCurrentThread();
        _mGc0 = GC.CollectionCount(0);
        _mGc1 = GC.CollectionCount(1);
        _mGc2 = GC.CollectionCount(2);
        _measureStartMs = NowMs;
        _measureDone = false;
    }

    /// <summary>收仪表：算差额，累进总账。重复调用只算第一次（收笔有几条分支）。</summary>
    private void EndStrokeMeasure()
    {
        if (_measureDone) return;
        _measureDone = true;
        StrokeAllocBytes = GC.GetAllocatedBytesForCurrentThread() - _mAlloc0;
        StrokeGc0 = GC.CollectionCount(0) - _mGc0;
        StrokeGc1 = GC.CollectionCount(1) - _mGc1;
        StrokeGc2 = GC.CollectionCount(2) - _mGc2;

        StrokesMeasured++;
        AllocKbSum += StrokeAllocBytes / 1024.0;
        if (StrokeAllocBytes > AllocBytesMax) AllocBytesMax = StrokeAllocBytes;
        if (StrokeGc2 > 0) StrokesWithGc2++;
    }

    /// <summary>本笔有没有压感（设备级判断，不是看数值）。</summary>
    internal bool ActiveStrokeHasPressure;
    /// <summary>上一笔的合并率/预测统计（诊断与自检用）。</summary>
    internal int LastCoalescedSamples, LastCoalescedMessages;
    /// <summary>累计统计（--penlive 结束时汇总用）：真笔的压感/倾角/合并情况。</summary>
    internal int PenTotalPoints, PenPressurePoints, PenMessages, PenSamples;
    internal bool PenSawPressureMask, PenSawTiltMask, PenSawRotationMask;
    /// <summary>累计统计：非笔指针（鼠标 / 触摸）读到多少消息、多少合并采样点。</summary>
    internal int PtrTotalPoints, PtrMessages, PtrSamples, PtrCoalescedExtra;
    // [删除 2026-10-05] 预测前带量 / 喂 DWM 段数统计（老预测系统）。
    internal int _cntDown, _cntMove, _cntUp, _cntCaptureLost;
    internal string _lastStrokeReport;

    /// <summary>`--strokefile` 的落盘路径（**空串 = 不写**；这项目没开 nullable，不用 `?`）。</summary>
    private string _strokeFilePath = "";
    private long _hotkeysRegistered;

    /// <summary>
    /// 键位表：动作 → 键 + 作用域。**可配置、可查冲突、可落盘**（见 KeyBindings.cs）。
    /// 启动时先读用户配置的覆盖项，退出时把改动写回去。
    /// </summary>
    internal KeyMap Keys = KeyMap.Default();

    /// <summary>某个动作当前的键位文本（界面悬停提示用；键位表的唯一起源见 KeyBindings.cs）。</summary>
    internal string KeyTextFor(KeyAction action) => Keys.KeyText(action);

    internal int _virtualX, _virtualY, _virtualW, _virtualH;

    internal double _autoExitAt = double.MaxValue;

    // ---- perf accounting --------------------------------------------------
    internal readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
    /// <summary>上一帧 RenderAll 的实际墙钟耗时（含分块光栅、界面准备、上屏等待）。诊断/自检用。</summary>
    internal double LastFrameMs => _lastFrameMs;
    internal double _fps;
    private int _frames;
    private double _fpsWindowStart;
    private TimeSpan _lastCpu;
    private double _lastCpuWall;
    private double _cpuPercent;
    internal double _peakWorkingSetMb;

    // ---- 界面接入口 -------------------------------------------------------
    // 引擎只认 IOverlayUi。界面通过 UiHost 改引擎状态，永远不直接碰文档。
    internal UiHost Host;
    internal IOverlayUi Ui;
    internal bool UiInvalidatePending;
    internal Color4 CurrentColor = InkPalette.PenDefault;
    internal Color4 HighlighterCurrent = InkPalette.HighlighterDefault;

    // ---- 工具带色位 / 最近颜色（2026-10-23 起；2026-10-28 扩成 8 个可改色位）----
    /// <summary>
    /// 笔的 8 个色位。默认 = <see cref="InkPalette.PenBand"/> 的 8 色；
    /// 每个色位都可以被用户换色（不再分“固定色 / 自定义槽”两种身份）。
    /// </summary>
    internal Color4[] PenSwatchColors = Array.ConvertAll(InkPalette.PenBand, p => p.Color);
    /// <summary>当前笔色来自第几个色位；-1 = 颜色不是从色带选的（选中面板等外部改色）。</summary>
    internal int PenSwatchSelected = -1;
    /// <summary>屏幕吸管这次要把颜色写回哪个色位；-1 = 按当前选中/最后一格兜底。</summary>
    internal int ColorPickTargetSlot = -1;
    /// <summary>兼容旧接口：最后一格就是原来的“自定义槽”。</summary>
    internal Color4 CustomPenColor
    {
        get => PenSwatchColors[^1];
        set => PenSwatchColors[^1] = value;
    }
    /// <summary>兼容旧接口：最后一格是否当前选中。</summary>
    internal bool CustomPenSelected
    {
        get => PenSwatchSelected == PenSwatchColors.Length - 1;
        set
        {
            if (value) PenSwatchSelected = PenSwatchColors.Length - 1;
            else if (PenSwatchSelected == PenSwatchColors.Length - 1) PenSwatchSelected = -1;
        }
    }
    private const int RecentPenColorMax = 8;
    private readonly List<Color4> _recentPenColors = new();

    // ---- 荧光笔的 5 个色位 / 5 档浓度（2026-10-28）----------------------------
    /// <summary>5 个色位的基色（不透明）；实际画的时候按对应档位加 alpha。</summary>
    internal Color4[] HighlighterSwatchColors = Array.ConvertAll(
        InkPalette.HighlighterBand,
        p => new Color4(p.Color.R, p.Color.G, p.Color.B, 1f));
    /// <summary>5 个色位各自的透明度档位下标（0..4）；默认都是第 3 档 0.32。</summary>
    internal int[] HighlighterSwatchOpacity = { 2, 2, 2, 2, 2 };
    /// <summary>当前荧光色来自第几个色位；-1 = 外部改色。</summary>
    internal int HighlighterSwatchSelected = -1;
    private const int RecentHighlighterColorMax = 8;
    private readonly List<Color4> _recentHighlighterColors = new();

    /// <summary>屏幕吸管模式：冻结帧已经抓好，等用户点一下屏幕。</summary>
    internal bool ColorPickActive;
    /// <summary>吸管当前指向的颜色（画预览 / 提交用）。</summary>
    internal Color4 ColorPickHover = new(1f, 1f, 1f, 1f);
    internal bool ColorPickHoverValid;
    /// <summary>吸管“已复制”提示的截止时刻。</summary>
    internal double ColorPickCopiedUntilMs = double.NegativeInfinity;
    internal bool UiCapturing;

    /// <summary>自检用：系统问了我们几次命中测试、其中几次回答"这块是我的"。</summary>
    internal int _cntNcHitTest, _cntNcHitClient;

    // ==== 面板的"接输入小窗"（方案 B，见 计划-底层对接界面.md 4.1）=================
    //
    // 为什么需要它：穿透必须靠 WS_EX_TRANSPARENT（跨进程才点得到下层），
    // 而那个样式让窗口**在系统那一层就被排除在输入之外**——实测 WM_NCHITTEST
    // 一次都不会被调用。所以"面板可点"和"点击穿透"在同一个窗口里是互斥的。
    //
    // 解法：画面照旧画在覆盖层上（不新增渲染路径、不抢层序），另外开一块
    // **只收输入、不画东西**的小窗，正好盖住面板矩形。系统按窗口分发输入，
    // 于是"穿透"和"面板可点"天然同时成立。
    private IntPtr _uiInputHwnd;
    private bool _uiInputShown;
    // 界面当前按住的指针（触摸可以同时有两根；不能用一个 bool 把第一根抬手后的第二根丢掉）。
    private readonly HashSet<uint> _uiPointers = new();
    private RectF _uiInputRect;                 // 物理像素

    /// <summary>
    /// 指针这一刻是不是停在界面自己那一块上。
    /// 两个地方要用：悬停时光标要给箭头（不是笔尖/橡皮圈）；穿透模式下
    /// WM_SETCURSOR 要区分"面板之外让下层决定"与"面板之上我们自己给箭头"。
    /// （internal 是为了自检能构造"从画布直入 / 从工具条进入"两种来路，见 --cursortest。）
    /// </summary>
    internal bool _uiHover;

    /// <summary>
    /// 白板模式：给整块画布铺一层不透明的底色，遮住桌面和别的程序。
    /// 关掉就是原本的"透明批注"——直接写在别人的 PPT、网页上面。
    /// </summary>
    internal bool BoardOn;

    /// <summary>
    /// 打开白板时冻结在底层的文档页（0 起；-1 = 没有文档底座）。
    /// 白板翻页只动白板相机，页图按打开那一刻的文档相机画，不再被白板滚动带回第 1 页。
    /// </summary>
    internal int BoardFrozenDocPage = -1;
    internal float BoardFrozenDocViewOffsetY;

    /// <summary>半透明白板 + 打开时冻结的文档页是否应该参与绘制。</summary>
    internal bool BoardFrozenDocActive =>
        BoardOn && DocView.IsOpen && BoardFrozenDocPage >= 0 && BoardOpacity < 0.999f;

    /// <summary>
    /// **文档的"纸"**（2026-10-09 用户定的"两张纸"模型）：文档开着时自动铺的一层白底
    /// （页缝/页边不露桌面）。它**独立于白板**——白板是"再来一张纸"，盖在文档上面；
    /// 两张纸的开关互不影响（见 Overlay.BoardBrush / SyncDocPages）。
    /// </summary>
    internal Color4 DocPaperColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal Color4 BoardColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal double _lastRebuildMs;
    internal double _lastRecordMs;
    internal double _lastPresentMs;
    internal double _lastPresentWaitMs;
    // 分块缓存的状态（面板与自检读数）
    internal int _tilesUsed, _tilesVisible, _tilesBudget, _tilesRasterized;
    /// <summary>最近一拍预取了几块（HUD 用；见主循环的空闲预取）。</summary>
    internal int _prefetchLast;
    private double _inputToPresentMs;
    private double _lastInputMs = -1;

    // ---- 延时探针（见 Latency.cs）---------------------------------------
    /// <summary>打开后每画完一帧就记一条延时样本。只有测量模式会打开。</summary>
    internal bool LatencyRecording;
    internal readonly LatencyRecorder Latency = new();
    /// <summary>最近一条指针消息自带的硬件时标（QPC 计数，可能是 0 = 不可信）。</summary>
    private ulong _lastInputPerfQpc;
    /// <summary>最近一条指针消息被我们拿到的时刻（QPC）。</summary>
    private ulong _lastInputMsgQpc;
    /// <summary>已经记过样的那条消息，避免同一帧重复记。</summary>
    private ulong _lastSampledMsgQpc;
    /// <summary>上一条指针消息到达的时刻，用来量实际输入采样间隔。</summary>
    private ulong _prevInputMsgQpc;
    /// <summary>这一帧收进了几个指针采样点。</summary>
    private int _framePoints;
    /// <summary>累计渲染帧数。测量模式用它算真实帧率（样本条数 ≠ 帧数）。</summary>
    internal long FrameCounter;
    internal double _nextLogAt;
    private double _accRecord, _accPresent;
    private double _accFrame, _accRaster;
    private int _accFrames;
    private double _nextStatsAt;
    private double _lastStatsMs;
    /// <summary>上一帧画性能面板花了多久。面板自己的代价也要看得见，否则它会掩盖问题。</summary>
    private double _lastHudMs;
    private double _hudAvgMs;        // 面板代价的窗口均值（单帧会被"重排那一帧"带偏）
    private double _accPanel;
    private double _hudRedrawMs, _hudRedrawSum, _hudRedrawPerSec;
    private double _hudRateWall;
    private long _hudRedrawCount, _hudRedrawTotal, _hudRedrawAtWall;
    private double _workingSetMb;
    private double _privateMb;
    private double _privateWsMb;      // 专用工作集 = 任务管理器"内存"列
    private double _sharedCommitMb;   // 共享提交（工作集里有多少是别的进程也有的）
    private double _gpuMb;
    private double _lastTopmostMs;

    /// <summary>Everything that talks to the OS for reporting: process times,
    /// working set, CPU share. Runs a few times a second, never per frame.</summary>
    private void UpdateStats()
    {
        var sw = Stopwatch.StartNew();

        double wall = _clock.Elapsed.TotalMilliseconds;
        // 一次调用拿齐内存三个口径；CPU 用 GetProcessTimes。
        // 以前走 Process.GetCurrentProcess()+Refresh()，那条路要几百微秒到几毫秒
        // （统计那一栏 3~4 ms 的大头就是它）——面板自己的开销不该跟被观测的东西一个量级。
        var c = new Native.PROCESS_MEMORY_COUNTERS_EX2();
        c.cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>();
        bool memOk = Native.GetProcessMemoryInfo(Native.GetCurrentProcess(), ref c, c.cb);
        if (memOk)
        {
            _workingSetMb = c.WorkingSetSize.ToUInt64() / 1048576.0;
            _privateMb = c.PrivateUsage.ToUInt64() / 1048576.0;
            double pws = c.PrivateWorkingSetSize.ToUInt64() / 1048576.0;
            // 老系统上 EX2 的后两个字段是 0：那时退回"工作集"，不编一个假数出来。
            _privateWsMb = pws > 0 ? pws : _workingSetMb;
            _sharedCommitMb = c.SharedCommitUsage / 1048576.0;
        }

        if (Native.GetProcessTimes(Native.GetCurrentProcess(), out _, out _, out long kernel, out long user))
        {
            var cpu = TimeSpan.FromTicks(kernel + user);
            double cpuDelta = (cpu - _lastCpu).TotalMilliseconds;
            double wallDelta = wall - _lastCpuWall;
            if (wallDelta > 250)
            {
                _cpuPercent = cpuDelta / wallDelta / Environment.ProcessorCount * 100.0;
                _lastCpu = cpu;
                _lastCpuWall = wall;
            }
        }

        _gpuMb = GpuUsedMb();
        if (_workingSetMb > _peakWorkingSetMb) _peakWorkingSetMb = _workingSetMb;
        sw.Stop();
        _lastStatsMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Stamped whenever a pointer message is handled, so the loop can
    /// report how long input took to reach the screen.</summary>
    private void StampInput() => _lastInputMs = _lastActivityMs = _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// 把"这一帧覆盖的那条最新输入"记成一条延时样本。调用点在每个窗口都
    /// Present 之后——只有到那时候才知道这一帧实际什么时候被交出去。
    ///
    /// 只记**新**输入：同一帧里收到多条指针消息时以最后一条为准（它才是
    /// 屏幕上那一笔的笔尖位置），已经记过的就不再记。
    /// </summary>
    private void RecordLatencySample()
    {
        if (_windows.Count == 0) return;
        if (_lastInputMsgQpc == 0 || _lastInputMsgQpc == _lastSampledMsgQpc) return;
        var w0 = _windows[0];
        if (w0.LastPresentEndQpc < _lastInputMsgQpc) return;   // 这一帧还没把输入交出去

        _lastSampledMsgQpc = _lastInputMsgQpc;

        // 计时起点优先用**采样点自己的时标**，而不是"我们读到它的时刻"。
        //
        // 原因是消息会被系统合并：注入 140 Hz 的输入，应用实际只收到约 60 条
        // WM_POINTERUPDATE（其余被合并进 historyCount）。用"读到时刻"当起点，
        // 会把"系统什么时候肯把消息给我们"算进应用延时里，量出来一半的帧
        // 是 1.5 ms、另一半是 16 ms——那是投递节奏，不是渲染慢。
        // 用采样点时标当起点，才是"笔尖动 → 这一帧交出去"的干净口径。
        bool perfOk = HardwareStampUsable(_lastInputPerfQpc, _lastInputMsgQpc);
        ulong origin = perfOk ? _lastInputPerfQpc : _lastInputMsgQpc;

        var s = new LatencySample
        {
            InputToMsgMs = perfOk ? Qpc.Ms(_lastInputPerfQpc, _lastInputMsgQpc) : double.NaN,
            MsgToPresentMs = Qpc.Ms(origin, w0.LastPresentStartQpc),
            PresentBlockMs = Qpc.Ms(w0.LastPresentStartQpc, w0.LastPresentEndQpc),
            PointsInFrame = _framePoints,
            PresentCount = w0.LastPresentCount,
        };
        // 合计从"采样点产生"算起（拿不到采样点时标就退回"读到消息"那一刻）。
        s.TotalMs = s.MsgToPresentMs + s.PresentBlockMs;

        // 上屏时刻：窗口化合成交换链上，GetFrameStatistics 给的是"最近一次真正
        // 上屏的帧"。它可能比我们这一帧早（负值）——那种情况不算，标记为不可得。
        if (w0.LastFrameStatsOk && w0.LastDisplayQpc > w0.LastPresentEndQpc)
        {
            s.PresentToDisplayMs = Qpc.Ms(w0.LastPresentEndQpc, w0.LastDisplayQpc);
            s.HasDisplay = s.PresentToDisplayMs < w0.RefreshPeriodMs * 4;
        }

        Latency.Add(s);
        _framePoints = 0;
    }

    /// <summary>测量模式在每个场景开始前调用：把探针的游标清干净，避免上一场景的
    /// 残留时标把第一条样本算成"等了三百毫秒"。</summary>
    internal void ResetLatencyProbe()
    {
        _lastInputMsgQpc = 0;
        _lastSampledMsgQpc = 0;
        _prevInputMsgQpc = 0;
        _framePoints = 0;
    }

    /// <summary>
    /// 判断 POINTER_INFO.PerformanceCount 能不能当 QPC 用。
    ///
    /// 真实手写笔上它是硬件的 QPC 时标，值得采信；但**合成输入（SendInput /
    /// 注入）上它可能是 0 或别的计数器**，直接减会得到荒谬的数。所以先做
    /// 两条检查：非零、且不晚于我们收到它的时刻、且差值在 2 秒以内。
    /// 不通过就写 NaN，报告里那一列会是"不可得"，不会拿假数当结论。
    /// </summary>
    private static bool HardwareStampUsable(ulong stamp, ulong msgQpc)
        => stamp != 0 && stamp <= msgQpc && (msgQpc - stamp) * Qpc.TicksToMs < 2000.0;

    internal string LastCaptureReport = "";

}
