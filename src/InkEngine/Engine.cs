using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
    /// 界面自己的偏好（深色主题、贴边隐藏、档位、钉住）。引擎**只存不解释**：
    /// 它不知道"极简档"是什么，界面说存什么就存什么。落盘在 settings.json 的 `ui` 段。
    /// </summary>
    internal readonly Dictionary<string, string> UiPrefs = new();
    private bool _uiPrefsDirty;

    /// <summary>自检/基准模式：**不读也不写**用户配置（判据要确定，更不能改用户的设置）。</summary>
    internal bool SelfCheckMode;

    // ---- 自动存档（崩溃恢复）------------------------------------------------
    //
    // 产品里**没有"保存"这个动作**，所以按"持久画布"来做：每 15 秒（板书变了才写）
    // 存一份到 %LOCALAPPDATA%，下次打开自动接上。
    // 为什么值得：一节课的板书丢了是最坏的失败模式；同类开源软件（Xournal++）
    // 正因为"崩溃就丢"被用户提了严重数据丢失的 issue。

    /// <summary>自动存档的间隔（毫秒）。板书没变就不写。</summary>
    private double _autoSaveEveryMs = 15000;
    private double _nextAutoSaveAtMs;
    private long _autoSavedVersion = -1;

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

    /// <summary>自检用：立刻按当前文档写一次自动存档（不看间隔）。</summary>
    internal void AutoSaveNow()
    {
        try { Recovery.SaveAuto(InkSerializer.Save(Doc)); }
        catch (Exception ex) { Console.WriteLine("自动存档失败：" + ex.Message); }
        _autoSavedVersion = Doc.Version;
        AutoSaveCount++;
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
        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;
        if (Doc.Version == _autoSavedVersion) return;

        // PPT 模式：**改成写当前这一页到 PPT 自己的目录**，不写自动存档。
        // 为什么：自动存档是"桌面的那一份板书"，下次启动会被当成桌面批注接回来——
        // 把某一页 PPT 的内容写进去就串了（那是另一条路的数据）。
        // PPT 页本来就有自己的落盘通道（翻页时、退出时各一次），这里补上"放映中途"
        // 那一段（老师在一页上写很久、还没翻页就崩了，内容不至于丢）。
        if (PptMode)
        {
            SaveCurrentPptPage();
            _autoSavedVersion = Doc.Version;
            AutoSaveCount++;
            return;
        }

        AutoSaveNow();
    }
    internal Stroke ActiveStroke;
    internal Tool Tool = Tool.Pen;
    /// <summary>Tool sizes are authored in logical pixels and scaled by the
    /// monitor DPI at use. Without this everything looks half-size on a 150%
    /// display, which is exactly how a 18px eraser turns into an unusable dot.</summary>
    internal float DpiScale = 1f;

    internal float EraserRadiusLogical = 22f;
    /// <summary>
    /// 整笔橡皮的落点半径（逻辑像素）的档位。**以前它根本没有档位**——
    /// 界面上给橡皮画了粗细滑条，可引擎里 `SetWidthFromUi` 把它归到"其它"那一支，
    /// 结果改的是**笔宽**：老师拖橡皮的滑条，笔迹粗细悄悄变了、橡皮一点没变。
    /// 现在四种工具各记各的，橡皮也有自己的三档。
    /// </summary>
    internal static readonly float[] EraserRadiusPresets = { 12f, 22f, 34f };
    internal int EraserRadiusIndex = 1;
    /// <summary>两种橡皮各自的可调范围（逻辑像素）。界面滑条的范围要和这里一致。</summary>
    internal const float EraserRadiusMin = 8f, EraserRadiusMax = 48f;
    internal const float PixelEraserMinWidth = 30f, PixelEraserMaxWidth = 160f;
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
    /// 现在发光比例改成 ClassIn 那种（**宽白芯 ＋ 细红边 ＋ 柔光**，见 `Overlay.DrawLaser`），
    /// 同一档在屏幕上也更"实"。
    /// </summary>
    internal float LaserWidthLogical = 8f;
    /// <summary>
    /// **笔的线型**（用户 2026-09-19 第 2 件：笔的色带条上要能切虚实线）。
    ///
    /// 和 <see cref="PenWidthLogical"/> 同一个地位：**这支笔自己的设置**。
    /// 只作用于新画出来的**自由笔迹**，而且是"只有笔用"——
    /// 荧光笔与激光笔的两条半透明/发光轨迹画成虚线没有意义，图形也不吃它
    /// （图形的线型历来是"选中之后在操作条面板里改"，见 <see cref="SetSelectionDash"/>）。
    ///
    /// 画的时候取当时的值写进对象（见 <see cref="Stroke.Dash"/>），之后各存各的：
    /// 改这个开关**不会**动已经写在板上的东西——和坐标系网格那条一个口径。
    /// </summary>
    internal StrokeDash PenDash = StrokeDash.Solid;
    internal float EraserRadius => EraserRadiusLogical * DpiScale;
    private float _lastEraseX, _lastEraseY;

    /// <summary>
    /// 像素橡皮的落点尺寸（逻辑像素）：**竖着的黄金比例矩形**，高 : 宽 = 1.618。
    ///
    /// 默认宽 93 × 高 150：用户实测的用法是"从上往下抹一段"（一列板书、一个竖排的字），
    /// 不是横扫一行，所以竖边比横边长。Ctrl+Alt+6 在这里的几档之间切（档位同样守黄金比）。
    /// </summary>
    internal const float GoldenRatio = 1.618f;
    internal float PixelEraserWidthLogical = 93f;
    internal static readonly float[] PixelEraserWidthPresets = { 46f, 93f, 150f };
    internal int PixelEraserWidthIndex = 1;
    internal float PixelEraserHeightLogical => PixelEraserWidthLogical * GoldenRatio;
    internal float PixelEraserHalfWidthPx => PixelEraserWidthLogical * 0.5f * DpiScale;
    internal float PixelEraserHalfHeightPx => PixelEraserHeightLogical * 0.5f * DpiScale;

    /// <summary>截图工具的拖动矩形（画布坐标）。</summary>
    internal bool CaptureActive;
    internal float CapMinX, CapMinY, CapMaxX, CapMaxY;

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
    /// </summary>
    internal bool CaptureFrameHidden;

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

    /// <summary>Pen width presets, in logical pixels. Cycled with Ctrl+Alt+W
    /// until there is a proper on-screen control for it.
    /// 最细档 2026-09-27 从 1.5 降到 **1**（用户："画笔的最小笔宽设成 1 可以吗？"——
    /// 1.5 写细字、画坐标轴刻度时还是偏粗）。</summary>
    internal static readonly float[] WidthPresets = { 1f, 3f, 6f, 10f, 16f, 24f };
    internal int WidthPresetIndex = 1;

    /// <summary>
    /// 每种工具各自的粗细档位。**笔和荧光笔的档位不是一回事**：荧光笔是"涂一大条"，
    /// 1 像素这种档位对它没意义；激光更小。共用一张表的结果就是
    /// "选了荧光笔按 Ctrl+Alt+6 没反应"（它改的是笔宽）——实测就是这个 bug。
    /// </summary>
    internal static readonly float[] HighlighterWidthPresets = { 8f, 18f, 32f };
    /// <summary>
    /// 激光的四档。沿革：4/8/14（初版）→ 8/14/22（2026-09-27 白天"默认档粗一点"）
    /// → **4/8/14/22**（2026-09-27 晚：用户说"最小笔宽是 8，我感觉有点宽了"）。
    /// 补一档更细的 4 回来，**默认档仍是 8**（`LaserWidthIndex = 1`）——
    /// "想要更细"和"默认别太细"这两条要求这样同时满足。
    /// </summary>
    internal static readonly float[] LaserWidthPresets = { 4f, 8f, 14f, 22f };
    internal int HighlighterWidthIndex = 1;
    internal int LaserWidthIndex = 1;

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
        if (Native.GetPointerDevice(sourceDevice, out var info))
            onScreen = info.pointerDeviceType == Native.POINTER_DEVICE_TYPE_INTEGRATED_PEN;
        _penOnScreenCache[sourceDevice] = onScreen;
        return onScreen;
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
    /// **复制拖拽模式**：点了操作条的复制按钮之后进入，此时按住选中内容拖动 = **拖出一份副本**
    /// （原件不动），可以连着拖多份；再点按钮 / 点空白 / 换工具退出。
    ///
    /// 语义抄 InkClass（那边已经踩过一轮坑）：点图标**只进模式**，不是"点一下原地克隆一份"
    /// ——后者副本固定偏移 24px、落点不可控，已经废弃。
    /// </summary>
    internal bool CopyDragArmed;

    /// <summary>复制拖拽这一次拖动里"插入副本"那条待提交的动作（松手时和位移合成一步）。</summary>
    private AddStrokesAction _pendingClone;

    /// <summary>
    /// 橡皮的落点反馈之外，**同时显示系统箭头**（默认藏起来，只画我们自己的圈/方块）。
    ///
    /// 留这个开关是为了做 A/B：自绘的方块光标本质是"我们画的一帧"，天生比系统光标晚
    /// 一个刷新周期（16.7 ms）；系统箭头画在硬件光标平面上，几乎没有延迟。用户说
    /// "不跟手"时，把两个都显示出来——箭头跟手、方块形状准，哪种更舒服由用户说了算。
    /// </summary>
    internal bool EraserKeepsSystemCursor;
    internal string HudText = "";
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
    /// 把线压成零长度，`SnapToAxis` 一看没有方向 → 吸成水平 → **一条很短的小横线**
    /// （用户 2026-09-24 报的"竖着画的直线变成很短的一个横直线"；`--dwelltest` H6 钉住：
    /// 修之前那一档漂移 299.51 画布单位，整条 300 的线只剩一个点）。
    /// </summary>
    internal const float DwellDragSlopLogical = 5f;

    /// <summary>停顿成型定型时的**角度吸附容差**（度）：照 InkClass 的 `LineAssistSnapDeg = 4`
    /// （画坐标轴 / 分割线刚需，见 计划-图形工具.md §42.1）。</summary>
    internal const float DwellSnapDeg = 4f;

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

    /// <summary>正在拖颜色面板里的粗细滑条。</summary>
    private bool _sliderDragging;

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
    /// 最后一次"滚动条该露面"的时刻（滚动或悬停）。滚动条按这个时间淡出——
    /// 静止 3 秒后消失（InkClass 实测 1.5 秒太快，用户会找不到它）。
    /// </summary>
    internal double ScrollBarActiveAtMs = double.MinValue;

    /// <summary>指针正停在滚动条上：滑块加粗、不淡出。拖动中也算（滑块不能半路淡掉）。</summary>
    internal bool ScrollBarHover;
    internal bool ScrollBarDragging;
    /// <summary>按下时指针相对滑块顶边的偏移，拖动中保持它不变，滑块才不"跳"。</summary>
    private float _scrollGrabDy;
    private OverlayWindow _scrollWindow;

    /// <summary>屏幕坐标 → 画布坐标（相机）。输入进来第一件事就是过这个。</summary>
    internal void ScreenToCanvas(ref float x, ref float y) => y -= ViewOffsetY;

    /// <summary>当前视口在**画布坐标**里的范围（相机之后）。</summary>
    internal RectF ViewportCanvas => new()
    {
        MinX = _virtualX,
        MinY = _virtualY - ViewOffsetY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY - ViewOffsetY + _virtualH,
    };

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
            r.Add(new RectF
            {
                MinX = vp.MinX, MinY = vp.MaxY,
                MaxX = vp.MaxX, MaxY = vp.MaxY + _virtualH,
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
    /// <summary>调试开关：不画自己那一笔，只留系统合成器画出来的委托轨迹。</summary>
    internal bool SuppressActiveStroke;
    internal float MqMinX, MqMinY, MqMaxX, MqMaxY;
    internal bool PassThrough;
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
    private bool _animating;
    internal bool _drawing;
    private uint _activePointer;
    private uint _activePointerType;

    // ---- 压感采集与笔迹预测（实现见 Input/PenInput.cs、Prediction/InkPredictor.cs）----
    //  这两件事共用同一条时间轴：采样点自带硬件时标，压感保真靠它，外推预测也靠它。
    private readonly PenSampleBuffer _pen = new();
    /// <summary>
    /// 非笔指针（鼠标 / 触摸）的合并点。手写板**没开 Windows Ink** 时设备是以
    /// `PT_MOUSE` 上报的，触摸屏是 `PT_TOUCH`——这两条路以前一条消息只取一个点，
    /// 快写时轨迹被静默抽稀（见 Input/PointerInput.cs）。
    /// </summary>
    private readonly PointerSampleBuffer _ptr = new();
    private readonly InkPredictor _predictor = new();
    private readonly PredictedPoint[] _predBuf = new PredictedPoint[8];
    private readonly Vector2[] _trailReal = new Vector2[PenSampleBuffer.MaxSamples];
    /// <summary>湿墨**逐点半径**（和 _trailReal 一一对应）：有压感时湿墨也得有粗有细，
    /// 否则抬手那一下粗细会跳（见 <see cref="TrailRadius"/>）。</summary>
    private readonly float[] _trailRadii = new float[PenSampleBuffer.MaxSamples];
    private readonly Vector2[] _trailPred = new Vector2[8];
    /// <summary>渲染尾的复用缓冲（画布坐标）。每帧清空重填，不分配。</summary>
    private readonly List<Vector2> _tailScratch = new(8);

    /// <summary>
    /// 预测开关。**默认关**（2026-09-29 用户拍板）。
    ///
    /// 关它的原因（都是实测的，别再"顺手打开"）：
    ///   · 真笔那一条：DWM **不把我们喂的预测点画出来**（用 `--predictms 200 --predictlead 400`
    ///     当探针验过：鼠标那条会窜出去，手写板那条纹丝不动）——所以对笔，它一直是白喂；
    ///   · 鼠标/触摸那一条：预测段由我们画（见 <see cref="UpdateRenderTail"/>），
    ///     而输入是突发的，尾巴会**一出一进**，屏幕上是末端"突突突往外跳"（用户原话）；
    ///   · 收益又測不出来：同一支笔、开与关，"手感分不出来"。
    ///
    /// 配置：`--predict` 打开（做对照用）；开了之后 `--predictms N` 调地平线、
    /// `--predictlead N` 调前带量上限。
    /// </summary>
    internal bool PredictEnabled;
    /// <summary>
    /// 正在写的这一笔**已经交给系统合成器画**了吗（<see cref="FeedInkTrail"/> 真的喂了点）。
    /// 喂过就不再加自己的渲染尾——两边一起补会在笔尖前面重复画出一小截。
    /// </summary>
    internal bool ActiveStrokeOnTrail;
    /// <summary>渲染尾相对最后一个真实点的最远距离（画布像素）。脏区要按它往外扩。</summary>
    internal float PredictedTailLead;
    /// <summary>自画预测尾的累计统计（诊断用）：算过多少次、一共报过多少个点、最大前带量。</summary>
    internal int TailComputes, TailPointsTotal;
    internal float TailLeadMax;
    /// <summary>这一笔有没有出过预测尾、以及最大前带量（`[笔画]` 那一行要用）。</summary>
    private bool _strokeHadTail;
    private float _strokeTailMax;

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

    /// <summary>起仪表：记下这一刻的分配计数和三代 GC 次数。</summary>
    private void BeginStrokeMeasure()
    {
        // GetAllocatedBytesForCurrentThread 只是读一个计数器，很便宜，不会自己触发回收。
        _mAlloc0 = GC.GetAllocatedBytesForCurrentThread();
        _mGc0 = GC.CollectionCount(0);
        _mGc1 = GC.CollectionCount(1);
        _mGc2 = GC.CollectionCount(2);
        // 预测那本账也要按笔分开记（真笔的预测在 DWM 那条路上）
        _mPredCount = PredLeadCount;
        _mPredSum0 = PredLeadSum;
        _strokePredMax = 0f;
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

        StrokePredCount = PredLeadCount - _mPredCount;
        StrokePredLeadAvg = StrokePredCount > 0 ? (PredLeadSum - _mPredSum0) / StrokePredCount : 0;
        StrokePredLeadMax = _strokePredMax;

        StrokesMeasured++;
        AllocKbSum += StrokeAllocBytes / 1024.0;
        if (StrokeAllocBytes > AllocBytesMax) AllocBytesMax = StrokeAllocBytes;
        if (StrokeGc2 > 0) StrokesWithGc2++;
    }

    /// <summary>当前这一笔的渲染尾点数（诊断与自检用；0 = 没有尾）。</summary>
    internal int RenderTailPoints => ActiveStroke?.RenderTail?.Count ?? 0;
    /// <summary>当前预测地平线（毫秒，8~15）。诊断用。</summary>
    internal double PredictHorizonMs => _predictor.HorizonMs;
    /// <summary>前带量的硬上限（像素）。诊断用。</summary>
    internal float PredictLeadCap => _predictor.MaxDistance;
    /// <summary>本笔有没有压感（设备级判断，不是看数值）。</summary>
    internal bool ActiveStrokeHasPressure;
    /// <summary>上一笔的合并率/预测统计（诊断与自检用）。</summary>
    internal int LastCoalescedSamples, LastCoalescedMessages;
    /// <summary>累计统计（--penlive 结束时汇总用）：真笔的压感/倾角/合并情况。</summary>
    internal int PenTotalPoints, PenPressurePoints, PenMessages, PenSamples;
    internal bool PenSawPressureMask, PenSawTiltMask, PenSawRotationMask;
    /// <summary>累计统计：非笔指针（鼠标 / 触摸）读到多少消息、多少合并采样点。</summary>
    internal int PtrTotalPoints, PtrMessages, PtrSamples, PtrCoalescedExtra;
    /// <summary>预测把湿墨往前带了多少（像素）——"说不清有没有用"时就看这个数。</summary>
    internal double PredLeadSum; internal int PredLeadCount; internal float PredLeadMax;
    /// <summary>
    /// 这一笔**喂给委托轨迹（DWM）**的预测段：次数 / 平均前带量 / 最大前带量。
    /// 真笔的预测全在这条路上（由系统合成器画），和"我们自己画的尾"是两回事——
    /// 报告里必须分开写，否则真笔那几笔会显示成"预测尾=无"，看起来像没预测
    ///（2026-09-29 用户就是这么被误导的）。
    /// </summary>
    internal int StrokePredCount;
    internal double StrokePredLeadAvg;
    internal float StrokePredLeadMax;
    int _mPredCount;
    double _mPredSum0;
    float _strokePredMax;
    internal int _cntDown, _cntMove, _cntUp, _cntCaptureLost;
    internal string _lastStrokeReport;
    private long _hotkeysRegistered;

    /// <summary>
    /// 键位表：动作 → 键 + 作用域。**可配置、可查冲突、可落盘**（见 KeyBindings.cs）。
    /// 启动时先读用户配置的覆盖项，退出时把改动写回去。
    /// </summary>
    internal KeyMap Keys = KeyMap.Default();

    internal int _virtualX, _virtualY, _virtualW, _virtualH;

    internal double _autoExitAt = double.MaxValue;

    // ---- perf accounting --------------------------------------------------
    internal readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
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
    internal Color4 BoardColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal double _lastRebuildMs;
    internal double _lastRecordMs;
    internal double _lastPresentMs;
    // 分块缓存的状态（面板与自检读数）
    internal int _tilesUsed, _tilesVisible, _tilesBudget, _tilesRasterized;
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
    private double _accRecord, _accPresent, _accHud, _accStats;
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
    private void StampInput() => _lastInputMs = _clock.Elapsed.TotalMilliseconds;

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

    // =====================================================================
    //  Startup
    // =====================================================================

    internal int Run(string[] args)
    {
        // 设置控制台输出编码。
        // 【为什么必须包 try】：发布版是 GUI 子系统（无控制台），双击启动时进程手里
        // 根本没有控制台句柄，这个 setter 会走 SetConsoleOutputEncoding 抛
        // IOException“句柄无效”，把整个启动流程打断（踩过：双击发布版启动失败）。
        // 有控制台（命令行/带参数自检）时才真正生效，没控制台就跳过——
        // 反正没控制台时所有 Console.WriteLine 本来也是空操作，不影响任何功能。
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Native.EnableMouseInPointer(true);

        Gfx.Init();
        NowMs = _clock.Elapsed.TotalMilliseconds;

        _hInstance = Native.GetModuleHandle(null);
        _className = "InkTeachOverlay_" + Guid.NewGuid().ToString("N");
        if (!RegisterWindowClass())
            return 2;

        string mode = args.Length > 0 ? args[0] : "";
        SelfCheckMode = mode.Length > 0;

        // 用户偏好（深色主题/贴边隐藏/档位/钉住，以及"启动要不要接上上次的板书"）。
        //
        // **必须读在恢复板书之前**：接不接上次的板书由 `ui.restoreInk` 决定
        // （默认不接，见 RestoreAutoSaveIfAny）。
        // 自检/基准模式**不读**——判据要确定，而且不该拿用户的设置去跑自检。
        if (InkSettings.PathOverride == null && args.Contains("--nosettings"))
            InkSettings.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                              "inkteach-nosettings.json");
        if (!SelfCheckMode)
            foreach (var w in InkSettings.LoadUiPrefs(UiPrefs))
                Console.WriteLine("settings: " + w);

        // 上次因为界面出问题重启过？把板书读回来（读走就删，只恢复一次）。
        // 自检/基准模式不掺和：那些模式不该被"上次留下的板书"影响判据。
        if (mode.Length == 0) RestoreSessionIfAny();

        // 上次的自动存档（崩溃/正常退出都留）：**默认不接**，见 RestoreAutoSaveIfAny。
        if (mode.Length == 0) RestoreAutoSaveIfAny();

        // 宿主自己的启动分支（开发期的点击目标、截图工具等）。返回 true
        // 表示这条命令行已经由宿主处理完，引擎不再往下走。产品界面不需要覆写。
        if (PrepareHostStartup(mode, args, out int hostExit))
            return hostExit;

        // PPT 放映联动：**只在产品模式下真的接**（自检/基准模式不接——那要碰 COM，
        // 判据会变得不确定；而且探针 `--pptprobe` 自己 new 一个源去问，
        // 不走这条路）。没装 Office / 没开 PPT 时它什么都不做。
        StartPptLink();

        // --nohud：关掉调试性能面板。它是给开发看的，每帧要花约 1.9 ms
        // （文字排版 + 进程计数），测底层性能时必须排除掉，否则量到的是
        // 测量工具本身而不是渲染引擎。
        if (args.Contains("--nohud")) ShowHud = false;

        // 对照实验用：--nohist 关掉脏区的多帧回溯，应当立刻出现残影，
        // 用来证明残影测试本身是有效的（而不是永远通过）。
        if (args.Contains("--nohist")) OverlayWindow.TransientHistoryFrames = 0;
        // ---- 委托墨迹轨迹（湿墨交给系统合成器画）-----------------------------
        //
        // 默认策略（2026-09-13 实测之后定）：**系统里真有笔设备就默认开**，
        // 没有就关。理由：这条通道对鼠标输入没有意义（实测合成鼠标下它不画），
        // 而对手写笔它是"笔尖跟手"的唯一正解——湿墨由 DWM 画，我们自己的
        // 渲染延时（敲定前实测约 1~3 帧）就不再压在笔尖上。
        //
        // 以前默认关是因为"本机没有笔、验证不了"。这台机器有笔数字化器，
        // 而且引擎只对 PT_PEN 调用它，鼠标路径完全不受影响。
        bool penDevicePresent =
            (Native.GetSystemMetrics(Native.SM_DIGITIZER)
             & (Native.NID_INTEGRATED_PEN | Native.NID_EXTERNAL_PEN)) != 0;
        OverlayWindow.InkTrailEnabled = penDevicePresent && !args.Contains("--noinktrail");
        if (args.Contains("--inktrail")) OverlayWindow.InkTrailEnabled = true;
        if (args.Contains("--noinktrail")) OverlayWindow.InkTrailEnabled = false;

        // ---- 笔迹预测（**默认关**，2026-09-29）--------------------------------
        // 关的理由见 PredictEnabled 那段注释：真笔那条 DWM 不画我们的预测点（白喂），
        // 鼠标/触摸那条自绘尾会"一出一进"（末端突突跳），而收益又测不出来。
        // 想要对照就 `--predict`；开了之后 --predictms 调地平线、--predictlead 调前带量。
        PredictEnabled = args.Contains("--predict");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--predictms" && double.TryParse(args[i + 1], out double pm))
                _predictor.HorizonMs = pm;
        // 前带量的硬上限（像素）。默认 12 px 足够快机器；负载大、延迟高时要放宽才看得清效果。
        // 上界给到 400 而不是 40：这是**真机调手感**的旋钮，"100 到底难不难受"必须能真的调到
        // 100（夹在 40 的话人会以为功能就这样，见 InkPredictor.HardMaxHorizonMs 那段说明）。
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--predictlead" && float.TryParse(args[i + 1], out float pl))
                _predictor.MaxDistance = Math.Clamp(pl, 4f, 400f);
        _predictor.ClampHorizon();

        // ---- 书写期间的 GC 低延迟档 -------------------------------------------
        //
        // 默认**开**：书写期间用 SustainedLowLatency 抑制"前台第 2 代回收"
        //（那是最重的一种回收，一次几十毫秒，落在书写中间就是"笔突然卡一下"）。
        // 详见 GcLatency.cs 的说明与两个坑。
        //
        // `--nogclatency` 关掉它，专门用来做"开 / 不开"的对照测量——
        // 低配机上到底是不是它对卡顿有用，得靠这个开关量，不能凭感觉。
        GcLatency.Enabled = !args.Contains("--nogclatency");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--gchold" && double.TryParse(args[i + 1], out double gh))
                GcLatency.HoldMs = Math.Clamp(gh, 0, 60000);

        // ---- 压感 → 粗细 ------------------------------------------------------
        //
        // 默认**开**：设备报压力就按压力改粗细——和系统墨迹（Tablet PC / OneNote 那一档）
        // 同一个口径：最小压力 50%、最大 150%（见 PressureWidth）。
        //
        // 两个开关都是**给真机调手感用的**，不是给用户平时按的：
        //   · `--nopressure` 关掉，用来做"有/无"对照（差异要当场看得出来才算数）；
        //   · `--pressrange min,max[,gamma]` 现调动态范围与曲线，不用重编。
        PressureWidth.Enabled = !args.Contains("--nopressure");
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--pressrange") continue;
            var parts = args[i + 1].Split(',');
            if (parts.Length >= 2
                && float.TryParse(parts[0], out float pmin)
                && float.TryParse(parts[1], out float pmax)
                && pmax > pmin)
            {
                PressureWidth.Min = Math.Clamp(pmin, 0.05f, 2f);
                PressureWidth.Max = Math.Clamp(pmax, 0.10f, 3f);
                if (parts.Length >= 3 && float.TryParse(parts[2], out float pg) && pg > 0.05f)
                    PressureWidth.Gamma = Math.Clamp(pg, 0.1f, 4f);
            }
        }

        // ---- 中心线曲线化（过点 Catmull-Rom ＋ 角点保护）------------------------
        //
        // **默认开**（2026-09-28 用户拍板："默认开也没关系"）：把"逐点直线段"换成
        // "过每一个采样点的三次贝塞尔"，急转处由角点保护切断切线。
        // **不改存档里的点、不改形状语义**——曲线严格过点，直角/顿笔/尖角原样保留；
        // 正在写的那一笔走折线（`Stroke.RawWhileLive`），落笔那一刻才换成曲线。
        //
        //   --nosmooth          退回折线（做"开 / 关"对照用）
        //   --smoothcorner N    角点阈值（度）。默认 35：转得比它急就保留尖角。
        StrokeSmoothing.SetEnabled(!args.Contains("--nosmooth"));
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--smoothcorner" && float.TryParse(args[i + 1], out float sc))
            {
                StrokeSmoothing.CornerAngleDeg = Math.Clamp(sc, 5f, 90f);
                StrokeSmoothing.BumpVersion();
            }
        Console.WriteLine(StrokeSmoothing.Enabled
            ? $"中心线曲线化: 开（过点曲线 ＋ 角点保护，角点阈值 {StrokeSmoothing.CornerAngleDeg}°；"
              + "活笔走折线，落笔才换曲线）"
            : "中心线曲线化: 关（折线；--nosmooth 的效果）");

        // ---- 呈现节奏 ---------------------------------------------------------
        // 默认改成"等到合成边界再抽输入、立刻 Present(0)"。实测这一项把
        // "Present 返回 → 像素亮"从 3.5 个刷新周期压到 1 个（见
        // reports/延时-实测与优化.md）。--nopace 退回老的 Present(1) 阻塞行为。
        OverlayWindow.VBlankPaced = !args.Contains("--nopace");
        if (OverlayWindow.VBlankPaced) OverlayWindow.LatencyWaitEnabled = true;

        _virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        _virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        _virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        _virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        Console.WriteLine($"virtual desktop: {_virtualW}x{_virtualH} at ({_virtualX},{_virtualY})");

        if (!CreateOverlays())
            return 3;

        // 键位：先读用户配置的覆盖项，再注册全局热键。读坏了不阻塞启动——
        // 用默认键位跑起来，但把问题逐条打出来（静默回退最坑人）。
        foreach (var w in InkSettings.Load(Keys))
            Console.WriteLine("settings: " + w);

        // 自动更新的来源：settings.json 覆盖默认值（默认是空的 = 不检查）。
        // 放这里读，是为了"用户改配置文件不用重新编译"。
        UpdateFeed.Url = InkSettings.LoadUpdateUrl() ?? "";

        // **有更新源 → 初始状态就是"检查更新"**（而不是"未配置源"）。
        // `UpdateText` 的默认值是"未配置更新源"，那是给"真的一个源都没有"准备的；
        // 以前不管有没有源，一打开就显示"检查更新（未配置源）"，看着像坏了
        // （用户 2026-09-29 反馈）。界面那边 `UpdateStage.Idle` 显示的就是"检查更新"。
        if (UpdateFeed.HasAnySource)
        {
            UpdateState = UpdateStage.Idle;
            UpdateText = "";
        }

        // **上次用的颜色**：每个工具各记一个（存的是色带序号，见 SetColorFromUi）。
        // 读不到 / 对不上就用默认色（笔=红、荧光=黄）——那是 InkPalette 里的默认。
        if (int.TryParse(GetUiPref("penColor"), out int penIdx)
            && penIdx >= 0 && penIdx < InkPalette.PenBand.Length)
        {
            CurrentColor = InkPalette.PenBand[penIdx].Color;
            _penColorIdx = penIdx;                     // 连按时从这里往下走
        }
        if (int.TryParse(GetUiPref("hlColor"), out int hlIdx)
            && hlIdx >= 0 && hlIdx < InkPalette.HighlighterBand.Length)
        {
            HighlighterCurrent = InkPalette.ToHighlighter(InkPalette.HighlighterBand[hlIdx].Color);
            _hlColorIdx = hlIdx;
        }
        // **粗细档**（每个工具分开记，用户 2026-09-30 定）：存的是"第几档"，
        // 读回来时把档位和对应的逻辑宽度一起恢复（见 CycleWidth 里的写入）。
        if (int.TryParse(GetUiPref("w.pen"), out int wPen) && wPen >= 0 && wPen < WidthPresets.Length)
        { WidthPresetIndex = wPen; PenWidthLogical = WidthPresets[wPen]; }
        if (int.TryParse(GetUiPref("w.hl"), out int wHl) && wHl >= 0 && wHl < HighlighterWidthPresets.Length)
        { HighlighterWidthIndex = wHl; HighlighterWidthLogical = HighlighterWidthPresets[wHl]; }
        if (int.TryParse(GetUiPref("w.laser"), out int wLaser) && wLaser >= 0 && wLaser < LaserWidthPresets.Length)
        { LaserWidthIndex = wLaser; LaserWidthLogical = LaserWidthPresets[wLaser]; }
        if (int.TryParse(GetUiPref("w.pixel"), out int wPixel) && wPixel >= 0 && wPixel < PixelEraserWidthPresets.Length)
        { PixelEraserWidthIndex = wPixel; PixelEraserWidthLogical = PixelEraserWidthPresets[wPixel]; }
        // 上次用的橡皮形态（整笔擦 / 面积擦）
        if (GetUiPref("eraserKind") == "pixel") _eraserKind = Tool.PixelEraser;
        // 上次用的线型（实线 / 虚线 / 点线）
        if (int.TryParse(GetUiPref("lineDash"), out int dash) && dash >= 0 && dash <= 2)
            LineDash = (StrokeDash)dash;
        // **粗细的"数值"版**：界面拖滑条设的是任意值（不一定落在档位上），
        // 所以除了上面那四行"档位"之外再记一份具体数值，谁后写谁生效。
        if (float.TryParse(GetUiPref("wv.pen"), out float vPen)) PenWidthLogical = vPen;
        if (float.TryParse(GetUiPref("wv.hl"), out float vHl)) HighlighterWidthLogical = vHl;
        if (float.TryParse(GetUiPref("wv.laser"), out float vLaser)) LaserWidthLogical = vLaser;
        if (float.TryParse(GetUiPref("wv.pixel"), out float vPixel)) PixelEraserWidthLogical = vPixel;
        // 上次用的选择方式（矩形 / 套索）
        if (GetUiPref("selMode") == "lasso") SelMode = SelectMode.Lasso;

        // **上一次是自动更新装上来的吗**：换壳脚本会在更新目录里留一个 done.txt。
        // 看到它 = 本次启动就是"更新完的第一次启动"，在界面上明说一句
        // （「更多」抽屉那一行会显示"已更新到 x.y.z"），然后把标记删掉——只说一次。
        // 为什么要这一步：换壳重启之后界面原本什么提示都没有，老师根本不知道成没成。
        try
        {
            string done = Path.Combine(UpdateFeed.UpdateDirFor(UpdateFeed.CurrentVersion), "done.txt");
            if (File.Exists(done))
            {
                File.Delete(done);
                UpdateState = UpdateStage.UpToDate;
                UpdateText = $"已更新到 {UpdateFeed.CurrentVersion}";
                Console.WriteLine($"自动更新：本次启动是换壳更新上来的（{UpdateFeed.CurrentVersion}）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("自动更新：检查 done.txt 失败：" + ex.Message);
        }

        RegisterHotkeys();

        // 自检/基准模式下面板默认关掉，除非显式 --hud。
        //
        // 为什么：面板是个大黑框，画在屏幕左上角**压在一切之上**。而很多自检的
        // 判据恰恰是"数屏幕上的墨"（--coordtest / --inputtest / --erasertest /
        // --ghosttest / --tiletest / --appendtest / --passtest / --rotatetest…），
        // 面板往上一盖，那些用例会**假失败**——面板越大盖得越多，这次把面板
        // 放大到 700×258 逻辑像素就当场踩到了。交互模式仍然默认开着。
        if (mode.Length > 0 && !args.Contains("--hud")) ShowHud = false;

        int dispatch = RunModeDispatch(mode, args);
        if (dispatch >= 0)
            return dispatch;

        // 走到这里说明是**交互模式**：把键位表打出来（连同注册失败的条目）。
        // 测试模式不打印——三十多行会把每条自检的日志淹掉。
        // 以前这个方法**根本没人调用**，所以键位一直只活在 README 里。
        PrintUsageHelp();

        Loop();
        Shutdown();
        return ExitCode;
    }

    /// <summary>
    /// 启动阶段、窗口创建之前的宿主分支。引擎本身没有这样的模式，
    /// 开发期的截图/点击目标工具挂在宿主（InkTeach）里。
    /// </summary>
    protected virtual bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }

    /// <summary>
    /// 读回"因为界面出问题而重启"之前暂存的板书。**读走就删**（只恢复一次），
    /// 太旧的由 <see cref="Recovery.TakeSession"/> 自己丢掉。
    /// </summary>
    private void RestoreSessionIfAny()
    {
        var blob = Recovery.TakeSession();
        if (blob == null) return;

        try
        {
            InkSerializer.LoadInto(Doc, blob);
            Doc.InvalidateAll();
            Console.WriteLine($"已恢复上次界面出问题时暂存的板书：{Doc.Strokes.Count} 笔");
        }
        catch (Exception ex)
        {
            Console.WriteLine("会话恢复失败（那份暂存已丢弃）：" + ex.Message);
        }
    }

    /// <summary>
    /// 启动时接上上次的板书（自动存档）。
    /// **读走不删**：它就是这份板书本身，下次打开还要接着用。
    /// 读坏了只提示、从空白开始——自动存档不该成为"起不来"的理由。
    /// </summary>
    private void RestoreAutoSaveIfAny()
    {
        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;

        // **默认不接上一次的板书**（用户 2026-09-17："退出以后再打开不用恢复墨迹吧……
        // 我觉得默认不恢复墨迹"）。
        //
        // 为什么这个默认是对的：教室的机器是**公用**的，上一节课（甚至上一个班）的板书
        // 一开机又铺满整个屏幕，老师第一件事就得先清空；而且"打开就有别人的东西"
        // 本身就不合适。课与课之间要的是一块干净的白板。
        //
        // 注意和图省事的临时暂存（<see cref="RestoreSessionIfAny"/>）分开：那条只在
        // **软件自己主动重启**（界面上点"重启"、界面出问题自动重建）时才走，
        // 那是"重启不该丢东西"，和"下次打开要不要接上"是两件事，仍然保留。
        //
        // 开关放在界面偏好里（`ui.restoreInk = "1"`），以后在"更多"抽屉里给一行就能改；
        // 现在没有这一项 = 不恢复。
        if (!RestoreInkOnStartup)
        {
            Console.WriteLine("启动：不接上次的板书（默认；要接上就在设置里打开 restoreInk）");
            return;
        }

        var blob = Recovery.LoadAuto();
        if (blob == null) return;

        try
        {
            InkSerializer.LoadInto(Doc, blob);
            Doc.InvalidateAll();
            _autoSavedVersion = Doc.Version;
            Console.WriteLine($"已接上上次的板书：{Doc.Strokes.Count} 笔");
        }
        catch (Exception ex)
        {
            Console.WriteLine("自动存档读不出来（已忽略，从空白开始）：" + ex.Message);
        }
    }

    /// <summary>
    /// 窗口建好、快捷键注册完之后的分支。返回 -1 表示"没有特殊模式，
    /// 正常跑消息循环"；返回 &gt;= 0 表示直接以该值退出。
    /// 产品界面只会拿到 -1；测试模式由 InkTeach 覆写。
    /// </summary>
    protected virtual int RunModeDispatch(string mode, string[] args) => -1;

    /// <summary>
    /// 收尾时给进程的退出码。默认 0；自检发现有 FAIL 时置 1，
    /// 这样脚本/CI 不用去解析控制台文字也能判红绿（和 tools/ui-mock 的自检一个规矩）。
    /// 产品路径永远是 0。
    /// </summary>
    protected int ExitCode;

    private bool RegisterWindowClass()
    {
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
            // 兜底光标。**这一句是"永远转圈"的解药**：hCursor 留空（NULL）时
            // 系统会给一个默认的等待类光标，用户看到的就是一直转圈。
            // 具体形状由 WM_SETCURSOR 按状态覆盖（见 ApplyCursor）。
            hCursor = Cursors.System(Native.IDC_ARROW),
        };
        ushort atom = Native.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private bool CreateOverlays()
    {
        bool ok = true;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var w = new OverlayWindow();
            var err = w.Create(_hInstance, _className, r.Left, r.Top, r.Width, r.Height);
            if (err != null)
            {
                Console.WriteLine($"overlay create failed on monitor {hMon}: {err}");
                ok = false;
                return false;
            }
            s_map[w.Hwnd] = w;
            _windows.Add(w);
            Console.WriteLine($"overlay on monitor {hMon}: {r.Width}x{r.Height} at ({r.Left},{r.Top}) dpi={w.Dpi}");
            Console.WriteLine($"委托墨迹轨迹(InkTrail): {(OverlayWindow.InkTrailEnabled ? "开" : "关")}"
                              + $"（接口{OverlayWindow.InkTrailNote}）");
            // 调参时"我到底调上了没有"必须一眼看得见：这里印的是**生效值**，不是"可用/不可用"。
            // （2026-09-22 用户碰到的两个坑：--predictms 100 被静默夹到 15；--noinktrail 生效了没有
            //   只能靠猜。这两件事都不该靠猜。）
            Console.WriteLine($"笔迹预测: {(PredictEnabled ? "开（--predict）" : "关（默认）")}"
                              + $"，地平线 {PredictHorizonMs:F0} ms（推荐 8~{InkPredictor.MaxHorizonMs:F0}，硬上限 {InkPredictor.HardMaxHorizonMs:F0}）"
                              + $"，前带量上限 {PredictLeadCap:F0} px");
            Console.WriteLine($"预测尾（鼠标/触摸自画的那一截）：{(OverlayWindow.InkTrailEnabled
                ? "真笔那一笔让给系统轨迹，鼠标/触摸仍然画"
                : "真笔也画（委托轨迹已关）")}");
            // 书写期间的 GC 低延迟档：低配上"偶发卡一下"的第一嫌疑就是它没生效。
            // 这里印的是**读回来的实际状态**（见 GcLatency.Describe），不是"我们想让它开"。
            Console.WriteLine($"书写期间 GC 低延迟档: {GcLatency.Describe()}"
                              + $"（SustainedLowLatency，最后一笔后 {GcLatency.HoldMs / 1000:F0} 秒退回）");

            // 系统笔设置：**"长按当右键"最容易在板书时添乱**——笔尖停住不动会被判成长按，
            // 而板书时停顿是常态。这里只**如实打印读到的数值**，不解释哪个取值代表开还是关
            //（那套取值我们没有可靠出处，不能猜；本项目的规矩是不许把猜的写成结论）。
            // 想改就去：设置 → 蓝牙和设备 → 笔和 Windows Ink → 其他笔设置。
            {
                const string penKey = @"Software\Microsoft\Wisp\Pen\SysEventParameters";
                bool hasMode = Native.ReadDword(penKey, "HoldMode", out uint hm, out _);
                bool hasWait = Native.ReadDword(penKey, "WaitTime", out uint wt, out _);
                bool hasHold = Native.ReadDword(penKey, "HoldTime", out uint ht, out _);
                if (hasMode || hasWait || hasHold)
                {
                    var parts = new List<string>();
                    if (hasMode) parts.Add($"HoldMode={hm}");
                    if (hasWait) parts.Add($"长按判定等待 {wt} ms");
                    if (hasHold) parts.Add($"长按保持 {ht} ms");
                    Console.WriteLine("系统笔设置: " + string.Join("、", parts)
                                      + "（这一份是**系统全局**的设置，只列出来看清楚——"
                                      + "我们自己的窗口已经单独关掉了这条手势，见下一行）");
                }
                else
                {
                    Console.WriteLine(@"系统笔设置: 读不到 HKCU\Software\Microsoft\Wisp\Pen\SysEventParameters"
                                      + "（这台机器没有这套笔设置；真笔设备可能由驱动自己管）");
                }
            }
            // **这条是"无论系统怎么设，我们这边都不认它"的验收口**（用户 2026-09-23 要的）：
            // 三条 Window 级关法见 Native.DisableSystemPressAndHold。为什么必须印出来：
            // 它失效时的现象是"笔尖停住弹右键环"，看起来像"停顿成型没识别出来"，
            // 排查方向会从一开始就错。
            Console.WriteLine("长按=右键手势: " + (OverlayWindow.PressAndHoldDisabled
                ? "已在本窗口内关掉（系统里开着也不影响我们）"
                : "⚠ 没关掉（真笔上笔尖停住可能弹右键环，会和停顿成型抢）"));
            Console.WriteLine($"压感→粗细: {(PressureWidth.Enabled
                ? $"开（{PressureWidth.Min:F2}~{PressureWidth.Max:F2} 倍，曲线 gamma {PressureWidth.Gamma:F2}）"
                : "关（--nopressure）")}；变宽通道: {OverlayWindow.InkNote}");
            return true;
        }, IntPtr.Zero);

        if (_windows.Count == 0)
        {
            Console.WriteLine("no monitors found");
            return false;
        }

        // Refresh the HUD ~4x a second so the numbers move even when idle.
        //
        // 顺便每隔 250 ms 重新"置顶"一次（WM_TIMER 里调 ReassertTopmost）。
        // 这个必须够快：**别的置顶窗口会把我们盖住**——实测输入法状态条
        // （wetype 的 StatusBarWnd）、桌面小组件（RocketView）都是 TOPMOST，
        // 谁最后调 SetWindowPos 谁在上面。被盖住的后果不只是"看不见"：
        // 合成/真实点击都会落到那个窗口上，批注一个字都画不出来。
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)1, 250, IntPtr.Zero);
        DpiScale = _windows[0].Dpi / 96f;

        Host?.UpdateScreen(LogicalVirtualScreen);
        Host?.UpdateWorkArea(LogicalPrimaryWorkArea);
        Console.WriteLine($"DPI 缩放 {DpiScale:F2}（逻辑 {_windows[0].Width / DpiScale:F0}x{_windows[0].Height / DpiScale:F0}）");

        // 把键盘模式落到窗口样式上。字段默认是开的，但样式要等窗口建好才能改——
        // 不调这一句，默认值和实际样式就对不上（得按一次 Ctrl+Alt+K 才生效）。
        SetKeyboardMode(KeyboardMode);
        return ok;
    }

    private void RegisterHotkeys()
    {
        // 全局热键**不再是一串写死的 (id, key)**：动作与键位由 Keys 这张表决定，
        // id 只是"注册顺序"，HandleHotkey 拿它反查动作。这样键位可配置、可查冲突，
        // 而引擎里不认识"Ctrl+Alt+P"这种东西。
        IntPtr h = _windows[0].Hwnd;
        _hotkeyActions.Clear();
        Keys.GlobalFailures.Clear();

        foreach (var b in Keys.For(KeyScope.Global))
        {
            int id = _hotkeyActions.Count + 1;
            uint mod = Native.MOD_NOREPEAT;
            if ((b.Chord.Modifiers & KeyChord.ModCtrl) != 0) mod |= Native.MOD_CONTROL;
            if ((b.Chord.Modifiers & KeyChord.ModAlt) != 0) mod |= Native.MOD_ALT;
            if ((b.Chord.Modifiers & KeyChord.ModShift) != 0) mod |= Native.MOD_SHIFT;

            _hotkeyActions.Add(b.Action);
            if (!Native.RegisterHotKey(h, id, mod, b.Chord.Vk))
            {
                // **注册失败要留痕**：教室机上微信 / QQ / 输入法 / 教学软件都会抢键
                // （实测 Ctrl+Alt+W 就被微信截图占了）。以前这里只打一行控制台，
                // 用户根本不知道，只觉得"这个键没反应"。
                int err = Marshal.GetLastWin32Error();
                string msg = $"{b.Chord}（{KeyMap.Describe(b.Action)}）注册失败，错误 {err}"
                           + (err == 1409 ? "：已被别的程序占用" : "");
                Keys.GlobalFailures.Add(msg);
                Console.WriteLine("hotkey " + msg);
            }
            else _hotkeysRegistered++;
        }
        Console.WriteLine($"registered {_hotkeysRegistered}/{Keys.Bindings.Count(b => b.Scope == KeyScope.Global)} global hotkeys");
    }

    // ---- 放映期间的"临时全局热键"（2026-09-30 用户定方案①）-----------------
    //
    // 背景：放映时**前台是 WPS/PPT**，我们的覆盖层收不到键盘（键盘按"焦点"投递；
    // 鼠标才是按"位置"，所以滚轮/点击在放映中照样有效）。而我们的工具键是"应用内
    // 快捷键"，靠焦点 → 放映时全失效 ✗。
    //
    // 办法：**一进放映批注模式，就把这几个键临时注册成全局热键**——系统直接投给我们、
    // 不看焦点；而且键被我们吞掉、**不会传给 WPS**，所以不会有"我们切了工具、PPT 又翻
    // 一页"的双发副作用（隔壁 InkClass 那种"打架"就是它用全局钩子但不吞键造成的）。
    // 退出放映立刻注销：平时一个键都不多占。
    private const int PptHotkeyBase = 81;          // 一小段专用 id（常规热键是 1..N，别撞）
    private static readonly (uint Mod, uint Vk, KeyAction Act)[] PptHotkeys =
    {
        (Native.MOD_CONTROL, 0x50 /*P*/, KeyAction.ToolPen),
        (Native.MOD_CONTROL, 0x49 /*I*/, KeyAction.ToolHighlighter),
        (Native.MOD_CONTROL, 0x4C /*L*/, KeyAction.ToolLaser),
        (Native.MOD_CONTROL, 0x45 /*E*/, KeyAction.ToolEraser),
        (Native.MOD_CONTROL, 0x5A /*Z*/, KeyAction.Undo),
    };
    private bool _pptHotkeysOn;

    /// <summary>进/出放映批注模式时调它（见 Ppt.EnterPptMode / ExitPptMode）。</summary>
    private void RegisterPptHotkeys(bool on)
    {
        if (_pptHotkeysOn == on || _windows.Count == 0) return;
        IntPtr h = _windows[0].Hwnd;
        for (int i = 0; i < PptHotkeys.Length; i++)
        {
            int id = PptHotkeyBase + i;
            var (mod, vk, _) = PptHotkeys[i];
            if (on)
            {
                if (!Native.RegisterHotKey(h, id, mod | Native.MOD_NOREPEAT, vk))
                    Console.WriteLine($"hotkey 放映临时键 #{i} 注册失败，错误 {Marshal.GetLastWin32Error()}");
            }
            else Native.UnregisterHotKey(h, id);
        }
        _pptHotkeysOn = on;
        Console.WriteLine(on ? "放映批注模式：工具键（Ctrl+P/I/L/E/Z）已临时升级为全局热键"
                            : "退出放映：临时全局热键已注销");
    }

    /// <summary>注册顺序 → 动作。按这个顺序 RegisterHotKey，WM_HOTKEY 的 id 就是它。</summary>
    private readonly List<KeyAction> _hotkeyActions = new();

    internal KeyAction ActionForHotkeyId(int id)
        => id >= 1 && id <= _hotkeyActions.Count ? _hotkeyActions[id - 1]
         : id >= PptHotkeyBase && id < PptHotkeyBase + PptHotkeys.Length ? PptHotkeys[id - PptHotkeyBase].Act
         : KeyAction.None;

    /// <summary>自检用：这批全局热键实际注册成功了几个。</summary>
    internal long HotkeysRegistered => _hotkeysRegistered;

    // =====================================================================
    //  Message loop
    // =====================================================================

    internal void Loop()
    {
        while (!_quit)
        {
            // 每帧开头先把渲染尾收掉：指针停住但画面还在刷（动画、界面失效、激光衰减）
            // 的时候，不收就会一直重画上一帧算出来的那一小截预测墨。
            // 紧接着的 DrainMessages 会把这一帧真的到过的点算成新的尾。
            ClearRenderTail();
            DrainMessages();
            PumpUpdate();                 // 自动更新：把后台结果搬过来，该换壳就换壳
            if (_quit) break;

            NowMs = _clock.Elapsed.TotalMilliseconds;
            PumpKeyGestures();            // 工具键的手势：长按判定 + 连按换色的延迟结算

            if (NowMs >= _autoExitAt) break;

            // 跑满一分钟还没出事 → 把"因为界面出问题而重启"的计数清掉。
            // 不这么做的话，上午两次、下午两次，第四次就会莫名其妙不许重启。
            if (!_restartCountCleared && NowMs > 60_000)
            {
                Recovery.ClearRestartCount();
                _restartCountCleared = true;
            }

            Laser.Prune(NowMs);
            StepCameraAnim();                 // 翻页动画（167ms）
            StepPpt();                        // PPT 放映联动（没变化时只读一个 bool，不碰 COM）
            StepPptBar();                     // 底部那两条的长按判定（只有按住那一会儿有活）
            if (NeedsFrame())
            {
                // VBlankPaced：先等到合成边界，**再抽一次消息**，然后画、提交。
                //
                // 顺序是有讲究的。等到边界之后抽，把"等边界期间到达的输入"
                // 也算进这一帧，笔尖就是最新的；反过来（先抽、再等一个周期）
                // 等于让最新的那一笔在队列里多躺一个刷新周期。
                if (OverlayWindow.VBlankPaced)
                {
                    Native.DwmFlush();
                    DrainMessages();
                    _dirty = true;
                }
                // 渲染期间界面可能又提出"我还要一帧"（在 Render 里调 InvalidateUi）。
                // 用序号认出来，别让这一句 _dirty = false 把它抹掉——
                // 抹掉的表现就是"动画或一次性外观变化卡在第一帧"。
                long seqBefore = _uiInvalidateSeq;
                long docVerBefore = Doc.Version;
                RenderAll();
                // 渲染期间**改了文档**（"按住清空"就是在界面的 Render 里够时间的）
                // 也要再要一帧：这一帧贴出去的是改之前的像素。
                // 少了后面这半句，清空之后屏幕上那层墨会一直留着——见 RenderAll 里的注释。
                _dirty = _uiInvalidateSeq != seqBefore || Doc.Version != docVerBefore;
            }
            else
            {
                Native.WaitMessage();
            }
        }
    }

    /// <summary>把消息队列里现有的消息全部处理掉，不阻塞。测试模式复用同一份，
    /// 免得"测量用的循环"和"真正的循环"因为顺序不同而量出两个结论。</summary>
    internal void DrainMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) { _quit = true; break; }
            // PPT 轮询线程的唤醒消息：**不派发**（它没有对应的窗口）——
            // 主循环紧接着的 StepPpt() 会把新快照处理掉。
            if (msg.message == PptWatcher.WakeMessage) continue;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        NowMs = _clock.Elapsed.TotalMilliseconds;
    }

    internal void RenderAll()
    {
        FrameCounter++;
        double frameStart = NowMs;
        double wall0 = _clock.Elapsed.TotalMilliseconds;

        // 自动存档：**放在这里**（每帧都过）而不是主循环里——主循环那条路
        // 只在"真有帧"时才走到，而自检是用"抽消息＋渲染"驱动的，挂在主循环里
        // 自检就永远验不到它（第一版就是这么漏的：改了板书也不写）。
        // 自检模式走临时路径（Recovery.AutoSavePathOverride），不会碰用户的板书。
        MaybeAutoSave();

        // PPT 状态机。**为什么两处都调**（这里 + 主循环 Loop 里）：主循环那条路
        // 只在"真有帧"时才走到，而自检是用"抽消息＋渲染"驱动的（同 MaybeAutoSave
        // 的理由）；反过来，空闲无帧时也要能响应翻页，所以 Loop 里那一句不能省。
        // 两边都是幂等的（TakeDirty 取走就清、SameAs 挡重复）。
        StepPpt();
        StepPptBar();     // 底部那两条的长按判定（理由同上，自检那条路也走它）

        // 书写期间的 GC 低延迟档：超时退回。放在这里**和自动存档同一个理由**——
        // 挂主循环里的话，自检那条路永远验不到"超时能退回"（见 GcLatency.cs）。
        // `_drawing` 传进去：手势还在进行就一直保持，别在一笔的中途退出来。
        GcLatency.Tick(_drawing);

        // The HUD text and the process counters cost an order of magnitude more
        // than drawing the ink does, so they refresh a few times a second rather
        // than every frame. Measured before this change: 2.7 ms + 3.5 ms per
        // frame, against 0.7 ms for the actual rendering.
        var swHud = Stopwatch.StartNew();
        bool statsRan = false;
        if (wall0 >= _nextStatsAt)
        {
            // 面板显示时才需要 4 Hz 刷新；只打日志的话 1 Hz 就够。
            // 进程计数一次要 ~3 ms，没必要每秒跑 4 次。
            _nextStatsAt = wall0 + (ShowHud ? 250 : 1000);
            UpdateStats();
            // 只在真的要显示面板时才拼字符串。BuildHudText 会去查进程内存，
            // 那是整条路径里最贵的一步；面板关掉还在跑就纯属浪费——
            // 实测它让空闲 CPU 从 ~0.3% 涨到 2~3.6%（单核）。
            if (ShowHud) HudText = BuildHudText();
            statsRan = true;
        }
        swHud.Stop();

        // 相机写给各覆盖窗口：渲染的每一处变换都用它（见 OverlayWindow.CanvasToWindow）。
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }

        // 面板的接输入小窗跟着界面这一刻占的地方走（方案 B）。
        // 放在渲染之前：这一帧界面画在哪，输入就该收在哪，两件事同源。
        UpdateUiInputWindow();

        // 记下渲染前的文档版本：**渲染期间界面可能改文档**（"按住清空"就是在界面的
        // Render 回调里够时间的——界面没有别的"每帧回调"可用）。见下面的判断。
        long docVerBeforeRender = Doc.Version;

        foreach (var w in _windows)
            w.RenderFrame(this);
        foreach (var w in _windows)
            w.Present();

        if (LatencyRecording) RecordLatencySample();

        // 界面自己要求的重画（InvalidateUi）已经在这一帧贴完，可以清掉了。
        UiInvalidatePending = false;

        // Every window has now applied this round of changes, so the stale
        // regions can be dropped. Doing it here (rather than inside a window)
        // is what keeps multi-monitor setups correct.
        //
        // **但渲染期间改了文档就不能清**：这一帧贴出去的是改之前的像素，
        // 脏区得留给下一帧。这里踩过一个真 bug（用户报的"橡皮清空没有用、
        // 还把墨迹卡住、连常规橡皮都擦不掉"）：清空是在界面的 Render 里触发的，
        // 引擎渲染完无条件 Reset()，于是**文档已经空了、屏幕上那层墨还留着**——
        // 看着像清空失效，而橡皮也擦不掉（文档里已经没有东西可擦了）。
        if (Doc.Version == docVerBeforeRender)
        {
            Doc.Dirty.Reset();
            Doc.AppendedSinceRender.Clear();
            Doc.StructureChangedSinceRender = false;
        }

        var w0 = _windows[0];
        _lastRebuildMs = w0.LastRebuildMs;
        _lastRecordMs = w0.LastRecordMs;
        _lastPresentMs = w0.LastPresentMs;
        _tilesUsed = w0.LastTileCount;
        _tilesVisible = w0.LastTileVisible;
        _tilesBudget = w0.LastTileBudget;
        _tilesRasterized = w0.LastPatchCount;

        // How long the newest input took to reach the screen. This is our own
        // contribution; the display pipeline adds up to one more scan-out.
        // 只在"这一帧确实处理了新输入"时采样，而且采完就作废。
        //
        // 这里原来有个 bug：_lastInputMs 用完不清零，于是空闲帧（激光动画、
        // 或只是等垂直同步的那一帧）会把同一条旧输入再算一遍，而且越算越大，
        // 把 EMA 一路往上带。面板上"输入到上屏"那个数因此偏大且不收敛。
        if (_lastInputMs > 0)
        {
            double sample = _clock.Elapsed.TotalMilliseconds - _lastInputMs;
            if (sample >= 0 && sample < 200)
                _inputToPresentMs = _inputToPresentMs <= 0
                    ? sample
                    : _inputToPresentMs * 0.85 + sample * 0.15;
            _lastInputMs = -1;
        }

        double wall = _clock.Elapsed.TotalMilliseconds;
        _lastFrameMs = wall - frameStart;
        // 手测台：把这一帧的"重画 + 整帧"耗时喂给采集器（它只在拖拽中累计）。
        if (EraserTelemetry != null)
        {
            EraserTelemetry.Frame(_windows.Count > 0 ? _windows[0].LastPatchMs : 0,
                                  _lastFrameMs, _inputToPresentMs);
            EraserTelemetry.Beat(NowMs, _fps, Doc);
        }

        _frames++;
        if (wall - _fpsWindowStart >= 500)
        {
            _fps = _frames * 1000.0 / (wall - _fpsWindowStart);
            _frames = 0;
            _fpsWindowStart = wall;
        }

        _accRecord += _lastRecordMs;
        _accPresent += _lastPresentMs;
        // 面板自己的代价从窗口那边取（重排 + 贴图），不是这里的"统计"耗时——
        // 混在一起会让"面板很贵"这件事被统计代码盖住。
        _lastHudMs = _windows.Count > 0 ? _windows[0].LastHudMs : 0;
        _accHud += swHud.Elapsed.TotalMilliseconds;
        _accPanel += _lastHudMs;
        if (_windows.Count > 0)
        {
            double redraw = _windows[0].LastHudRedrawMs;
            if (redraw > 0) { _hudRedrawSum += redraw; _hudRedrawCount++; }
            _hudRedrawTotal = _windows[0].HudRedraws;
        }
        if (statsRan) _accStats += _lastStatsMs;
        _accFrames++;

        if (NowMs >= _nextLogAt)
        {
            _nextLogAt = NowMs + 1000;
            int n = Math.Max(1, _accFrames);
            Console.WriteLine(
                $"[{NowMs / 1000,6:F1}s] fps={_fps,5:F1}"
                + $" 记录={_accRecord / n,5:F2} 上屏={_accPresent / n,5:F2}"
                  + $" 面板={_hudRedrawMs,4:F2}ms×{_hudRedrawPerSec:F0}/s 统计={_lastStatsMs,5:F2}"
                  + $" 合计={(_accRecord + _accPresent + _accHud + _accStats) / n,6:F2}ms"
                  + $" 置顶={_lastTopmostMs,5:F2}ms"
                  + $" 脏区={_windows[0].LastPresentRectCount}块/{_windows[0].LastPresentAreaPercent,5:F1}%"
                  + $" ws={_workingSetMb,6:F1}MB");
            _accRecord = _accPresent = _accHud = _accStats = 0;
            _hudAvgMs = _accPanel / n;
            _accPanel = 0;
            double secs = Math.Max(0.001, (NowMs - _hudRateWall) / 1000.0);
            _hudRedrawPerSec = (_hudRedrawTotal - _hudRedrawAtWall) / secs;
            _hudRedrawAtWall = _hudRedrawTotal;
            _hudRateWall = NowMs;
            _hudRedrawMs = _hudRedrawCount > 0 ? _hudRedrawSum / _hudRedrawCount : 0;
            _hudRedrawSum = 0; _hudRedrawCount = 0;
            _accFrames = 0;
        }
    }

    /// <summary>
    /// 显存已用量（MB）。
    ///
    /// 为什么要显示它：**核显的显存是从系统内存里分的**，而它**不计入进程工作集**。
    /// 任务管理器那一列在核显机器上会把 GPU 共享显存算进去，于是出现
    /// "任务管理器 1000MB、面板只有 100MB"——不是面板不准，是只报了一个口径，
    /// 恰好漏掉了最大的一块。
    /// </summary>
    private static double GpuUsedMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return 0;
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return info.CurrentUsage / 1048576.0;
        }
        catch { return 0; }
    }
    private string BuildHudText()
    {
        // 面板的排版原则：**一行一类事**，数字对齐，单位统一。
        // 内存那一行是重点：三个口径并列，并且把"任务管理器里叫哪一列"写出来
        // ——用户拿它跟任务管理器对照时最需要的就是这句话。
        return
            $"帧率 {_fps,6:F1} fps（空闲不重绘）   帧耗时 {_lastFrameMs,5:F2} ms   输入→上屏 {_inputToPresentMs,5:F1} ms\n" +
            $"绘制 {_lastRecordMs,5:F2} ms      上屏 {_lastPresentMs,5:F2} ms      分块光栅 {_lastRebuildMs,5:F2} ms\n" +
            $"面板 {_hudRedrawMs,4:F2} ms/次 ×{_hudRedrawPerSec,3:F0}/秒（摊到每帧 {_hudAvgMs,4:F2} ms）   统计 {_lastStatsMs,5:F2} ms\n" +
            $"内存 {_privateWsMb,6:F1} MB（专用工作集 = 任务管理器的“内存”）\n" +
            $"已提交 {_privateMb,6:F1} MB（=“提交大小”）   工作集 {_workingSetMb,6:F1} MB（含共享）\n" +
            $"共享提交 {_sharedCommitMb,6:F1} MB   显存 {_gpuMb,6:F1} MB   CPU {_cpuPercent,4:F1} %\n" +
            $"笔画 {Doc.Strokes.Count}      点数 {Doc.TotalPoints}\n" +
            $"选中 {Doc.Selected.Count}      工具 {ToolName(Tool)}{SelectModeTag()}{(PassThrough ? "（穿透中）" : "")}      粗细 {CurrentToolWidthLogical,4:F1}      撤销栈 {Doc.UndoDepth}\n" +
            $"分块 {_tilesUsed}/{_tilesBudget}（可见 {_tilesVisible}，本帧光栅 {_tilesRasterized}）      网格 {Doc.GridCells}\n" +
            $"Ctrl+Alt：1笔 2荧光 3激光 4橡皮 7像素橡皮 5框选 9矩形/套索 6粗细 Z撤销 C清空\n" +
            (EraserTelemetry != null
                ? $"橡皮手测台：记录中 · 已记 {EraserTelemetry.DragCount} 条拖拽（退出时写汇总）\n"
                : "") +
            $"其它 Ctrl+Alt：I面板 P穿透 K键盘 Y穿透方式 X退出";
    }

    /// <summary>面板上跟着"工具"显示的当前选择方式（只有框选工具用得上）。</summary>
    private string SelectModeTag()
        => Tool == Tool.Marquee ? (SelMode == SelectMode.Lasso ? "·套索" : "·矩形") : "";

    /// <summary>工具名（遥测 / HUD / 日志用）。</summary>
    private static string ToolName(Tool t) => t switch
    {
        Tool.Pen => "笔",
        Tool.Highlighter => "荧光笔",
        Tool.Laser => "激光笔",
        Tool.Eraser => "橡皮擦",
        Tool.PixelEraser => "像素橡皮",
        Tool.Capture => "截图",
        Tool.Marquee => "框选",
        Tool.Line => "直线",
        Tool.Rectangle => "矩形",
        Tool.Ellipse => "椭圆",
        Tool.Circle => "圆",
        Tool.Triangle => "三角形",
        Tool.Parallelogram => "平行四边形",
        Tool.Arrow => "箭头",
        // 2026-09-20 补：这六个以前落在 `_ => "?"`，HUD 和橡皮日志里显示成问号
        //（加图形时最容易漏的一处，因为漏了不报错、只是显示难看）。
        Tool.Coordinate => "坐标系",
        Tool.NumberLine => "数轴",
        Tool.Parabola => "抛物线",
        Tool.Hyperbola => "双曲线",
        Tool.Sine => "正弦",
        Tool.Cosine => "余弦",
        Tool.Wave => "波浪线",
        Tool.Tangent => "正切",
        Tool.Cylinder => "圆柱",
        Tool.Cone => "圆锥",
        Tool.Cuboid => "长方体",
        Tool.Tetrahedron => "四面体",
        // ⚠ **加图形别忘了这里**（漏了不报错，只是 HUD / 日志里显示成问号）。
        // 2026-09-22 补上一批漏掉的五个（圆台 / 球 / 棱柱 / 棱锥 / 棱台）＋ 本批的椭圆（带焦点）。
        Tool.ConeFrustum => "圆台",
        Tool.Sphere => "球",
        Tool.Prism => "棱柱",
        Tool.Pyramid => "棱锥",
        Tool.Frustum => "棱台",
        Tool.ConicEllipse => "椭圆（带焦点）",
        _ => "?",
    };

    // =====================================================================
    //  Window procedure
    // =====================================================================

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // **系统来问"要不要那条长按手势"**：一律回"不要"。
        //
        // 这是关掉"按住不动 = 右键"的**主路**（另两条在 Native.DisableSystemPressAndHold 里）：
        // 官方文档对这一位的原话就是 "disables press and hold (right-click) gesture"，
        // 也就是它同时干掉手势和右键消息。回在这里而不是某个窗口分支里，
        // 是因为系统可能拿**任意一个**我们的窗口来问（覆盖层、接输入小窗都算）。
        //
        // 顺带一条官方说明：关掉之后左键消息**不再需要等那段"区分长按与单击"的延迟**，
        // 落笔更跟手。
        if (msg == Native.WM_TABLET_QUERYSYSTEMGESTURESTATUS)
            return new IntPtr(Native.TABLET_DISABLE_PRESSANDHOLD);

        // 面板的"接输入小窗"（方案 B）：它只收输入，什么都不画。
        if (_uiInputHwnd != IntPtr.Zero && hWnd == _uiInputHwnd)
            return UiInputWndProc(hWnd, msg, wParam, lParam);

        // PPT 条那一块的"接输入小窗"（同一套方案，只在放映 + 穿透时存在）。
        // 为什么条需要它、而覆盖层那条 NCHITTEST 豁免不够用，见 Ppt.cs 里
        // `_pptInputHwnd` 那一段注释（一句话：穿透用的 WS_EX_TRANSPARENT 让
        // 系统跳过命中测试，豁免根本执行不到）。
        if (_pptInputHwnd != IntPtr.Zero && hWnd == _pptInputHwnd)
            return PptInputWndProc(hWnd, msg, wParam, lParam);

        // 宿主自己的窗口（开发期的点击目标）先处理。产品界面不会用到这一层。
        if (HandleHostWindowMessage(hWnd, msg, wParam, lParam, out var hostResult))
            return hostResult;

        // 滚轮：滚动画布（只改相机偏移，不动对象数据）。见 HandleWheel。
        if (msg == 0x020A /*WM_MOUSEWHEEL*/) return HandleWheel(wParam);
        // 批注键盘模式下的按键。只有这个模式收得到——见 SetKeyboardMode。
        // 工具键（Ctrl+P/I/L/E/M）走手势状态机，需要"松键"和"是不是自动重复"两件事：
        // 自动重复 = lParam bit30（见 ToolKeyDown 的说明）。
        if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
        {
            bool repeat = (lParam.ToInt64() & 0x40000000) != 0;
            if (HandleKeyDown(wParam, repeat)) return IntPtr.Zero;
        }
        if ((msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) && HandleKeyUp(wParam))
            return IntPtr.Zero;

        switch (msg)
        {
            case Native.WM_NCHITTEST:
                // 这两个计数只给自检用：它回答"穿透时系统到底还问不问我"，
                // 这是判定"能不能只靠命中测试做区域穿透"的唯一硬证据。
                _cntNcHitTest++;
                if (!PassThrough)
                {
                    _cntNcHitClient++;
                    return new IntPtr(Native.HTCLIENT);
                }
                // 穿透模式下，**界面自己那一块仍然归界面**。
                //
                // 以前这里一律回 HTTRANSPARENT，于是"开着穿透还想用工具条"这个
                // 最常见的组合直接失效：命中测试在系统那一层就把我们排除了，
                // WM_POINTERDOWN 根本轮不到引擎，悬浮球变成了一个画出来的装饰。
                HitTestPoint(lParam, out float hitX, out float hitY);
                // PPT 条（放映时底部那两条）也算"我的地盘"：**开着穿透时老师照样得能
                // 点翻页 / 拖进度条**——不然那一下会落到下层 PPT 上，被它当成翻页点击。
                bool mine = UiContains(hitX, hitY) || PptBarContains(hitX, hitY);
                if (mine) _cntNcHitClient++;
                return new IntPtr(mine ? Native.HTCLIENT : Native.HTTRANSPARENT);

            // 指针形状。**必须显式回答**：不处理时系统会拿窗口类的光标兜底，
            // 而类光标为 NULL 的表现就是"永远转圈"（这次修的就是它）。
            //
            // 只在"移动且输入未被捕获"时才会收到这条消息——所以拖拽中的光标
            // 得在按下那一刻自己设（见 OnPointerDown），不能等这里。
            case Native.WM_SETCURSOR:
                // 非客户区（边框、缩放角）由系统按自己的规矩来。
                if ((lParam.ToInt64() & 0xFFFF) != Native.HTCLIENT) break;
                // 穿透：面板之外让下层窗口决定；**面板自己那一块要给箭头**
                // （否则"工具条上顶着一个笔尖圈"或者一个转圈光标）。
                if (PassThrough && !_uiHover) break;
                ApplyCursor(force: true);
                return new IntPtr(1);                   // TRUE = 已处理，别再兜底

            case Native.WM_POINTERENTER:
                PointerInside = true;
                TrackPointerLeave(hWnd);
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                // 指针离开窗口：落点反馈（橡皮圆环、笔尖环）必须跟着消失，
                // 否则手一移开，屏幕上就留下一个圈。
                PointerInside = false;
                _dirty = true;
                ApplyCursor();
                // 注意：**这里不能叫 Ui.PointerLeave()**。覆盖层的"离开"在面板接管输入时
                // 恰恰是"指针进了面板"的那一刻（人从画布移到工具条上），
                // 界面收到的会是反的。界面那条离开由接输入小窗发（见 UiInputWndProc）。
                return IntPtr.Zero;

            case Native.WM_MOUSELEAVE:
                PointerInside = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
                OnPointerDown(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUPDATE:
                OnPointerMove(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUP:
                OnPointerUp(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                EndStroke();
                return IntPtr.Zero;

            case Native.WM_HOTKEY:
                HandleHotkey(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;

            case Native.WM_TIMER:
                // **停顿成型那颗 40ms 的定时器**（id = 2）只问一件事："笔停了多久"。
                // 顺手做的置顶那一套（id = 1 那颗 250ms 的）在这里**不重复做**——
                // 不分开的话它会跟着 40ms 的节奏跑，一秒凭空多 25 次置顶。
                if (wParam.ToInt32() == DwellTimerId)
                {
                    TickDwellShape();
                    return IntPtr.Zero;
                }
                // 只有要显示性能面板时才需要周期性重绘；面板关掉还每秒重画 4 次
                // 纯属白烧电。
                if (ShowHud) _dirty = true;
                UpdateScrollBarHoverFromCursor();
                var swTop = Stopwatch.StartNew();
                ReassertTopmost();
                swTop.Stop();
                _lastTopmostMs = swTop.Elapsed.TotalMilliseconds;
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _quit = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 让宿主处理属于它自己创建的窗口的消息。返回 true 表示消息已被消费。
    /// </summary>
    protected virtual bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    private void ReassertTopmost()
    {
        // **弹着系统对话框的时候一律不抬**（用户 2026-09-17 报的"点开一下就收回去、
        // 选不到 jpg"，2026-09-17 探针定位）。
        //
        // 这一抬是每秒一次的定时器干的（WM_TIMER），平时是必要的：全屏覆盖层被别的
        // 程序抢到后面去就"看不见也画不上"。但**弹着"另存为"的时候它是有害的**：
        // 覆盖层被重新抬到置顶，就去跟对话框抢那一层，对话框自己的**下拉列表**
        // （普通弹窗，不在置顶层）随即被盖住 / 被关掉——探针连着拍三张看得很清楚：
        // 点下拉 150 毫秒时列表好好地开着（PNG / JPEG 两条），900 毫秒时已经没了。
        // 那 900 毫秒正好压着一次定时器。
        //
        // 对话框期间由 ExportFileDialog 自己保证"它在最上面、而且一直置顶"
        // （见 CenterAndBringUp），关掉之后 ReturnFocusAfterDialog 再把覆盖层拾回来。
        if (ExportDialogOpen) return;

        foreach (var w in _windows)
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        // 接输入小窗**必须排在最后抬**：两块都是置顶窗口，谁最后 SetWindowPos
        // 谁在上面（见 计划-底层对接界面.md 4.1 的"层序"纪律）。只在这一处抬，
        // 别处不许再抬它，否则会出现"面板偶尔被自己的笔迹层盖住"。
        if (_uiInputShown && _uiInputHwnd != IntPtr.Zero)
            Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 这一刻该不该出一帧：脏了，或者有东西还在动。
    ///
    /// 三个"在动"的来源，各有各的理由：
    ///   · 激光轨迹：它**松手后停留 2 秒再整体淡出**（见 `LaserTrail`），不驱动的话
    ///     停留结束那一刻没人去推进淡出，那道光芒会一直挂在屏幕上；
    ///   · 正在书写：笔尖这条线每帧都在变；
    ///   · **界面自己声明的动画**（<see cref="IOverlayUi.IsAnimating"/>）：
    ///     展开/收起、悬停展开、贴边吸附全靠它，缺了就是"动画停在第一帧"。
    ///
    /// 自检直接调它来数帧——这样"引擎给不给帧"用的是**同一个判据**，
    /// 不会出现"测的是一套、跑的是另一套"。
    /// </summary>
    internal bool NeedsFrame()
    {
        _animating = Laser.Visible || _drawing || SelFlashing
                   || UiIsAnimatingNow || _camAnimating;
        return _dirty || _animating;
    }

    // =====================================================================
    //  Pointer input
    // =====================================================================

    private void OnPointerDown(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntDown++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 相机：屏幕 → 画布

        // 图库面板排在界面之前：它从工具条上沿**往上长**，两块本来不重叠，
        // 但顺序写清楚——面板是自己的浮层，先问它。
        // 按在面板外 = 先把它收起来，然后这一下**照常往下面走**（同颜色/层级面板的口径：
        // 面板外的第一下既收面板、也不耽误画）。`LibraryPointerDown` 内部还会管
        // "面板里的空白"（吃掉，不穿透到画布）。
        if (LibraryPanelOpen)
        {
            if (LibraryPointerDown(x, y)) { ApplyCursor(); return; }
            CloseLibraryPanel();
        }

        // 界面优先：点在悬浮条上就是操作界面，不是画一笔。
        //
        // 两件容易踩的事：
        //  1) 这一段必须**排在穿透判断之前**——不然开着穿透时界面永远收不到按下；
        //  2) 传给界面的是**逻辑屏幕坐标**，不是画布坐标。界面的布局与绘制都在
        //     屏幕坐标里，喂它画布坐标的话相机一滚，命中就整体偏掉一块。
        //
        // 注：面板在实际产品里由"接输入小窗"（方案 B）接管，走的不是这条路；
        // 这里留着是**兜底**——万一那块小窗没建起来，至少非穿透模式下还能用。
        if (UiPointerDown(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted))
        {
            _drawing = false;
            // 界面也要捕获指针：拖出悬浮条、在按钮上滑开都需要继续收到消息。
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 落在界面画的那一块里、但界面没吃这一下：这一下就算了。
        // ① 不落墨——面板底下的墨看不见，面板一挪又冒出来，属于"看不见却被记下来"；
        // ② 不透给下层——界面声明占用的区域（QueryBounds）就是它的地盘，
        //    要放行得让界面自己别把那一块算进去。
        if (UiContains(screenX, screenY))
        {
            _uiHover = true;
            _drawing = false;
            _dirty = true;
            return;
        }

        // 底部那两条 PPT 控件（放映时才在）：**排在界面之后、穿透之前**。
        //   · 界面在它上面（见 RenderFrame 的绘制顺序），所以界面先问；
        //   · 必须在穿透之前——开着穿透时老师照样得能点翻页 / 拖进度条
        //     （NCHITTEST 里给它开了区域豁免，见那里的注释）。
        if (PptBarPointerDown(screenX, screenY))
        {
            _drawing = false;
            _pptCapturing = true;           // 这一次归它：松手时由它收尾（见 OnPointerUp）
            Native.SetCapture(hWnd);        // 长按要持续收到移动（判"拿起后有没有拖走"）
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 界面之外的穿透：交下层窗口，我们不收这一下。
        if (PassThrough) return;

        // 图库里点了一张之后（`_libraryPending` 非空）：**这一次按下就是"把它放下来"**
        // ——和"粘贴对象"同一条路（一步撤销、放完自动选中）。
        // 位置：排在界面之后（点在工具条上不该跑到画布上插东西）、落笔之前
        //（它不是"画一笔"，不能进 _drawing）。
        if (LibraryInsertArmed)
        {
            TryInsertLibraryAt(x, y);
            ApplyCursor();
            return;
        }

        // **同一时刻只跟一条指针**——这是 OnPointerMove/OnPointerUp 里那句
        // `id != _activePointer` 的另一半。已经有指针在手（正在写、正在拖滚动条）时，
        // 后来的按下直接忽略。
        //
        // 这条守卫以前没有，而**只有触摸踩得到**：鼠标和笔一次只有一个指针，
        // 触摸屏上写字时蹭到的第二根手指（或者落屏的掌根）会让下面 `_activePointer = id`
        // 把指针抢走、`ActiveStroke` 被换成新的一笔——而正在写的那一笔**还没进文档**，
        // 于是"写一半的字凭空消失"。它顺带就是最基础的掌心抑制：掌根不再抢笔。
        if (_drawing) return;

        _activePointer = id;
        _activePointerType = ptype;
        PointerX = x; PointerY = y; PointerInside = true;

        // 滚动条次之：它只占右边缘一条窄带，但"拖滑块"比"在那儿画一笔"更特殊。
        if (TryBeginScrollBarDrag(screenX, screenY))
        {
            _drawing = true;
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        _drawing = true;
        Native.SetCapture(hWnd);
        // 书写会话开始：进 GC 低延迟档（见 GcLatency.cs），并起分配/GC 仪表。
        // 一笔接一笔写的时候，低延迟档会一直待着；最后一笔结束满几秒才退回。
        GcLatency.Enter();
        BeginStrokeMeasure();
        // 拖拽中系统不再发 WM_SETCURSOR（输入已被捕获），光标必须在按下这一刻定下来。
        ApplyCursor();

        var tool = inverted ? Tool.Eraser : Tool;
        switch (tool)
        {
            case Tool.Eraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginErase();
                EraserTelemetry?.BeginDrag(Tool.Eraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseAt(x, y, EraserRadius);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.PixelEraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginEraseRect();
                EraserTelemetry?.BeginDrag(Tool.PixelEraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseRectAt(x, y, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.Capture:
                // 和框选同一个手势。**和框选一样存画布坐标**（渲染那一层就
                // 不必为它单独换算一次），抓屏时再换算回屏幕坐标
                // （screen = canvas + 相机偏移，见 EndCapture）。
                BeginCaptureAt(x, y);
                break;

            case Tool.Marquee:
                // 先问选择手势（按钮 / 手柄 / 整体拖动）；都没接才起新的框选。
                // 修饰键在这一刻读一次就传进去（而不是在判定函数里读键盘）——
                // 这样"Shift 加选 / Alt 减选"这条分支能被自检直接驱动。
                if (!TryBeginSelectionGesture(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0))
                {
                    BeginMarqueeAt(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0);
                }
                break;

            case Tool.Laser:
                // **新起一条**（抬手那几条还在淡出，原样留着——照 ClassIn：可以同时有好几条）。
                // 粗细在这里记进这一条（每条自己记，见 `LaserTrail.Stroke.WidthLogical`）。
                Laser.Begin(x, y, NowMs, LaserWidthLogical);
                break;

            // 图形工具：**同一个手势**"按下记起点 → 拖动改终点 → 松手提交"
            // （接一个等于接七个，差别只在 Kind，见 计划-图形工具.md 8.2 / 9.1）。
            //
            // 判据走 <see cref="IsShapeTool"/>，**不再在这里逐个列 case**：
            // 以前这里是一串 `case Tool.Line: …`，和 IsShapeTool 是**同一件事的两份名单**。
            // 2026-09-19 加坐标系 / 数轴时这里就漏改了，症状是"工具切过去了、
            // 画出来的还是一坨自由笔迹"——`--axistest` 第一次跑就把它抓出来了。
            // 合成一份之后，以后再加图形种类不会再漏第二个地方。
            default:
                if (IsShapeTool(tool))
                {
                    // **画完自动选中那个框**（见 EndStroke）：按下先问"这一下是不是在动它"。
                    //   · 在它身上（圆钮 / 操作条 / 手柄 / 旋转柄 / **框内任意一点**）
                    //     → 交给选择手势那一套（拖动、拉手柄、点条，和框选工具下**完全一样**）；
                    //   · 在**框外** → 收起这个框，这一笔照常画。
                    // 用户 2026-09-22 定的口径："点击了其他地方，这个选中框就取消"；
                    // 而"只要收缩，其他的都不变"——所以框里照样能拖（第一版把框里判成
                    // "接着画一笔"，用户上手就是"不能拖动位置，只能拉伸缩放"）。
                    // 判据只有 `AutoSelectionZoneAt` 这一处（光标那边问 `SelectionInteractiveAt`）。
                    if (Doc.Selected.Count > 0)
                    {
                        bool shift = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
                        bool alt = (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0;
                        if (AutoSelectionPress(x, y, shift, alt)) { _dirty = true; return; }
                        ClearSelectionForNewContext();
                    }

                    // **多笔图形**（目前只有双曲线：两笔，见表 StepPlan）。规则照 InkClass：
                    // **一笔 = 按下-拖-松手**，松手推进下一笔（见 _stepIndex 那段注释）。
                    //   ① 第 1 笔按下 → 起半成品（`_stepPlan` / `_stepIndex` 也在那里落）；
                    //   ② 后面几笔按下 → **什么都不新建**，只接着改那个半成品
                    //     （中心沿用第 1 笔那个，InkClass 也是这么干的）。
                    if (_stepPlan != null && _stepIndex > 0)
                    {
                        ApplyStepGeometry(x, y);
                        break;
                    }
                    var plan = PlanOf(tool);
                    if (plan != null)
                    {
                        BeginStepShape(tool, plan, x, y);
                        ApplyStepGeometry(x, y);      // 按下也算一次（见那个函数的注释）
                        break;
                    }
                    BeginShapeAt(tool, x, y);
                    break;
                }

                // **停顿成型刚变出来的那个框**：工具还是笔，但这一下同样要先问"是不是在动它"。
                // 用户 2026-09-23 定的口径和图形工具下**完全一样**：点框里 → 拖动 / 拉手柄；
                // 点框外 → 收起这个框、**这一笔照常画**（只是"只不过点了一下"的话不留墨）。
                //
                // 判据是 `_dwellSelected`（只有停顿变出来的选中框才为真）——所以笔下面
                // 不会凭空多出交互：框选 / 图形工具画完那些选中，在笔下面仍然是"接着画"。
                if (TryDwellSelectionPress(x, y)) { _dirty = true; return; }

                BeginFreehandStrokeAt(id, ptype, x, y, screenX, screenY, pressure);
                break;
        }
        _dirty = true;
    }

    /// <summary>
    /// 笔下面那一下：**如果"停顿变出来的选中框"还在，先问它是不是在动它**
    ///（返回 true = 这一下被选择手势吃掉了，调用方直接收工）。
    ///
    /// ⚠ **只有这一处实现**：`OnPointerDown` 和 `--dwelltest` 都问它。
    /// 判据写两份的话，自检验的其实是"自检自己那一份"，等于没验
    ///（"同一个名单写在多处必漏一处"，见 架构-分层与规则.md 五-7）。
    /// </summary>
    private bool TryDwellSelectionPress(float x, float y)
    {
        if (!_dwellSelected || Doc.Selected.Count == 0) return false;
        bool shiftKey = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
        bool altKey = (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0;
        if (AutoSelectionPress(x, y, shiftKey, altKey)) return true;
        // 框外：收起这个框，这一笔照常画——只是"如果这一下只是个点"，墨不留
        //（见 EndStroke 里那条 `_dismissTapArmed`）。
        ClearSelectionForNewContext();
        _dismissTapArmed = true;
        return false;
    }

    /// <summary>
    /// 起一笔自由笔迹（笔 / 荧光笔 / 激光笔都走这里）。
    ///
    /// 从 `OnPointerDown` 里**抽出来**的理由有两条：
    ///   · 停顿成型的自检要能从"按下"这一步**真跑一遍**（`--dwelltest`），
    ///     而不是在测试里另造一条笔迹对象——那样测的就不是这条路了
    ///     （"我造了个对象、字段是我自己填的"不算测，见 project_memory 的教训）；
    ///   · 起笔的几件事（线型 / 合成器轨迹 / 预测器 / 停顿跟踪）本来就该在一处写完。
    /// </summary>
    private void BeginFreehandStrokeAt(uint id, uint ptype, float x, float y,
                                       float screenX, float screenY, float pressure)
    {
        var tool = Tool;
        float trailW = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                                 : tool == Tool.Laser ? LaserWidthLogical
                                                 : PenWidthLogical) * DpiScale;
        // 这一笔的线型：**只有笔吃那个开关**（见 PenDash），别的工具一律实线。
        var dash = tool == Tool.Pen ? PenDash : StrokeDash.Solid;
        // 只对真笔（PT_PEN）起轨迹：这条通道是给"笔尖跟手"用的，
        // 鼠标/触摸走它没有意义，而且会平白多一条系统画出来的线。
        //
        // **虚线笔迹不起委托墨迹**：那条轨迹由系统合成器画，画不出我们的线型
        // （它只会画一条实线），一笔写完就会"实线突然变虚线"闪一下。
        // 退回落自己画反而是对的——自己画的湿墨本来就是虚线，前后一致。
        if (ptype == Native.PT_PEN && dash == StrokeDash.Solid)
            WindowAt(screenX, screenY)?.BeginInkTrail(
                tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor, trailW * 0.5f);
        ActiveStroke = new Stroke
        {
            Tool = tool,
            Color = tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
            Width = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                              : tool == Tool.Laser ? LaserWidthLogical
                                              : PenWidthLogical) * DpiScale,
            Dash = dash,
            // **正在写的这一笔画折线**（曲线只用在落笔之后）：曲线的最后一段每来一个
            // 新点就要回头重算，笔尖后面那几十像素会一直微微动 —— 用户 2026-09-28
            // 实测的原话是"上面会出残影一直在那闪"。见 Stroke.RawWhileLive。
            RawWhileLive = true,
        };
        // 起笔：预测器从这一刻开始积累；落笔这条消息里可能已经合并了几个采样点，
        // 一起收进来（以前只取最新那一个）。
        _predictor.Reset();
        ActiveStrokeHasPressure = false;
        LastCoalescedSamples = LastCoalescedMessages = 0;
        // 这一笔还没交给系统合成器；渲染尾也先清掉（上一笔可能留了一截）。
        ActiveStrokeOnTrail = false;
        _strokeHadTail = false;
        _strokeTailMax = 0f;
        ClearRenderTail();
        AppendStrokeSamples(id, ptype, x, y, screenX, screenY, pressure);
        // 半径**逐点算**（见 TrailRadius）：有压感的笔，湿墨的粗细必须和干墨一致。
        FeedInkTrail(ptype, TrailRadius(), screenX, screenY);

        // **停顿成型跟着这一笔开始计时**（见 计划-图形工具.md §四十二）。
        // 激光笔不参与：它只是"指一下"，本来就不留墨，把它变出一个图形来没有意义。
        _dwellInk = null;
        _dwellCommitted = false;
        // 录墨迹（`--recink`）：这一笔从零开始攒（见 `FlushInkRecord`）。
        _recordPts = null;
        _recordGuess = "";
        // 上一笔要是还挂着（**配对没走到提交**就复位了，比如中途换了工具），
        // **必须把它放回可见** —— 否则那一笔会**永远隐身** ✗（见 `Stroke.HiddenForPairing`）。
        // ⚠ 走 `SetPairHidden`：它同时把那一笔占的块标脏，否则"放回来了、屏幕上还是不显示"
        //   （配对期那几帧已经把块重画成"没有它"的版本了）。
        SetPairHidden(_twoBranchPrev, false);
        _twoBranchPrev = null;
        _twoBranchIndex = -1;

        if (DwellShapeEnabled && tool != Tool.Laser)
        {
            _dwell.Begin(NowMs, new Vector2(x, y), DwellAssist.DeadZoneLogical * DpiScale);
            StartDwellTimer();
        }
        else _dwell.Reset();
    }

    /// <summary>
    /// 这一帧的指针移动（自由笔迹那一支）。从 `OnPointerMove` 抽出来，
    /// 理由和 <see cref="BeginFreehandStrokeAt"/> 同一个：停顿成型的自检要能**真跑**这一条。
    /// </summary>
    private void ExtendFreehandStroke(uint id, uint ptype, float x, float y,
                                      float screenX, float screenY, float pressure)
    {
        // **停顿成型**：armed 之后这一笔不再堆采样点（它已经变成幽灵图形了），
        // 指针移动一律交给 `ArmedStrokeMove`：**只有直线**会动（拖另一头转向 / 伸缩），
        // 其它图形**只预览、什么都不改**（抬手才定型 + 自动选中，见那个函数）。
        // ⚠ 这里**没有"取消"**：早先那条"非直线拖走就取消、这一帧的点照常进笔迹"是
        //    我们自己定的，早去掉了。
        bool dwellConsumed = _dwell.State == DwellState.Armed
                          && ArmedStrokeMove(x, y, screenX, screenY);
        if (dwellConsumed) return;

        // 指针报什么坐标就存什么坐标：不做平滑、不做抽稀。
        // 但**一条消息里的点要全部收下**——系统会把来不及投递的移动合并
        // （自己实测：注入 140 Hz，应用只收到约 60 条消息，其余在 history 里），
        // 只取最新那一个等于把笔的采样率砍半（见 Input/PenInput.cs）。
        AppendStrokeSamples(id, ptype, x, y, screenX, screenY, pressure);
        FeedInkTrail(ptype, TrailRadius(), screenX, screenY);
        // 停顿跟踪：**死区内的抖动不算"动过"**（见 DwellAssist.DeadZoneLogical）——
        // 少了它，笔尖静止时那点亚像素抖动会把计时一直刷新，
        // "停 600ms"永远攒不满，整条功能看着像没生效。
        _dwell.Sample(NowMs, new Vector2(x, y));
    }

    // ---------------------------------------------------------------------
    //  停顿成型：计时 / 触发 / 按住调整 / 定型（规格见 计划-图形工具.md §四十二）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 开那颗 40ms 的轮询定时器（**只在笔画进行中开**）。
    ///
    /// 为什么非要有它：**笔不动就没有 `WM_POINTERUPDATE`**，而主循环空闲时阻塞在
    /// `WaitMessage()`（见 `Loop`）——不主动醒过来，就永远问不出"停了多久"。
    ///
    /// 为什么单独一颗而不是搭现成那颗 250ms 的：250ms 的粒度下一笔"600ms 的停顿"
    /// 会在 500~750ms 之间随机触发，手感是飘的；40ms 只多醒 25 次/秒，而且**只在写字期间**。
    /// </summary>
    private void StartDwellTimer()
    {
        if (_dwellTimerOn || _windows.Count == 0) return;
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)DwellTimerId, DwellAssist.TickMs, IntPtr.Zero);
        _dwellTimerOn = true;
    }

    private void StopDwellTimer()
    {
        if (!_dwellTimerOn || _windows.Count == 0) return;
        Native.KillTimer(_windows[0].Hwnd, (IntPtr)DwellTimerId);
        _dwellTimerOn = false;
    }

    /// <summary>
    /// **轮询那一刻**：笔停够了就识别一次。这是整条路上唯一的"时间判据"入口
    ///（`Engine.Loop` 里 `NowMs` 已经刷新过了，见那里的顺序）。
    /// </summary>
    private void TickDwellShape()
    {
        if (_dwell.State != DwellState.Tracking) return;
        if (!_dwell.StillEnough(NowMs)) return;

        var ink = ActiveStroke;
        if (ink == null || ink.Points.Count < 3) return;

        var pts = new Vector2[ink.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(ink.Points[i].X, ink.Points[i].Y);
        // **录墨迹**（`--recink`）：识别器看到的就是这一串点，直接留个引用给它存 ✓
        if (InkRecordPath != null) _recordPts = pts;
        // 最短长度那条门槛**在识别器里**（见 ShapeRecognize.Recognize 的 scale 参数）——
        // 这里不再自己乘一遍 DpiScale：同一个数写在两处，迟早会漂（用户 2026-09-23 的教训清单里
        // 第一条就是这个）。
        // ★ **两笔 → 双曲线**（用户 2026-09-25 定的约定："**画两支就是双曲线，画一支就是抛物线**"）。
        //
        // ⚠⚠ **必须排在"认不出图形就返回"那道闸【之前】** —— 这是自检抓出来的致命错位：
        //   单独一支双曲线**本来就不该被认成六种图形里的任何一种**（新约定：单笔不出双曲线，
        //   而它也不是抛物线）→ `Recognize` 返回 `IsNothing`；要是把配对挡在它后面，
        //   **这条路永远走不到**（实测：语料完全对称、一个都不出，日志里连一行配对记录都没有）。
        var pair = TryTwoBranchPair(ink, pts);
        if (!pair.IsNothing)
        {
            if (InkLogEnabled) Console.WriteLine($"[成型] 两笔 → 双曲线：{pair.Rule}");
            if (InkRecordPath != null) _recordGuess = "两笔 → 双曲线：" + pair.Rule;
            ArmDwellShape(ink, pair);
            return;
        }

        var guess = ShapeRecognize.Recognize(pts, DpiScale);
        if (guess.IsNothing)
        {
            // **`--reclog` 诊断**（用户 2026-09-26 报"椭圆和矩形两个都常常什么都不认"那一条）：
            // 真机上"没认出来"原来**一点痕迹都没有**，只能靠合成语料去猜。
            // 这一行把"为什么没认"直接打出来（`Rule` 里每一道闸都带着数字）。
            if (InkLogEnabled)
                Console.WriteLine($"[成型] 没认出来：{guess.Rule}（{pts.Length} 点）");
            if (InkRecordPath != null) _recordGuess = "没认出来：" + guess.Rule;
            // 认不出图形 → **什么都不做，墨迹原样留着**（用户 2026-09-25 定）。
            //
            // 这里原先兜底"保形平滑"（把这一笔换成误差带内的光滑曲线，§43 第一步），
            // 2026-09-25 用户上手之后**取消**了 —— 原话："把'保形平滑'这个功能取消掉吧，
            // 因为它影响我画抛物线"。
            // 原因很实在：平滑**只在"认不出图形"时生效**，而**画抛物线（尤其只画半支的）
            // 经常正好落在这一档** —— 于是老师画的那条曲线被悄悄换成另一条，
            // 看着像"它自己变了形"。而这恰恰违反 §42 那条老规矩。
            //
            // 换成"什么都不做"之后：**认不出 = 良性失败**（墨还是他自己那一笔），
            // 规矩回到"**宁可不变，也不能变出一个对不上的**"。
            return;
        }

        if (InkLogEnabled) Console.WriteLine($"[成型] 认出了 {guess.Kind}：{guess.Rule}");
        if (InkRecordPath != null) _recordGuess = $"认出了 {guess.Kind}：" + guess.Rule;
        ArmDwellShape(ink, guess);
    }

    /// <summary>
    /// **把这一笔写进录制文件**（见 `InkRecordPath`）——`EndStroke` 一进来就调。
    ///
    /// 格式（好读、也好写脚本解析）：
    /// <code>
    /// --- stroke 3  t=812345  guess=没认出来：两笔不像同一个双曲线的两支（…）
    /// 301.25,400.50
    /// 305.10,402.75
    /// …
    /// </code>
    /// 段头一行给出"第几笔 / 什么时候 / 当时认成了什么（或为什么没认）"，
    /// 后面一行一个原始采样点。**连续两笔就是一条双曲线**（我按这个来配对统计）。
    ///
    /// ⚠ 写不进去（路径不对 / 文件被占）**绝不能影响画图** —— 整段包在 try 里，
    ///   失败了只往控制台说一声。
    /// </summary>
    private void FlushInkRecord()
    {
        if (InkRecordPath == null) return;
        // 优先用"识别器看到的那串点"；这一笔没停顿（没触发识别）就用收笔时的手绘点兜底。
        // ⚠ 兜底要**点数够**才算：停顿成型后 `ActiveStroke` 已经被换成图形对象（只剩两三个
        //   定义点），那种不能当墨迹录进去（否则我会拿一堆"两根点"当样本 ✗）。
        var pts = _recordPts;
        if ((pts == null || pts.Length < 8) && ActiveStroke != null && ActiveStroke.Points.Count >= 8)
        {
            pts = new Vector2[ActiveStroke.Points.Count];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new Vector2(ActiveStroke.Points[i].X, ActiveStroke.Points[i].Y);
        }
        try
        {
            using var w = new StreamWriter(InkRecordPath, append: true);
            _recordSeq++;
            if (pts == null || pts.Length < 8)
                w.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  "
                            + $"guess=（这一笔没录到原始点：没停顿、或点数不够）");
            else
            {
                w.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  "
                            + $"guess={(_recordGuess.Length == 0 ? "（没到识别那一步）" : _recordGuess)}");
                foreach (var p in pts) w.WriteLine($"{p.X:F2},{p.Y:F2}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[录墨迹] 写不进去（{InkRecordPath}）：{ex.Message}");
        }
        _recordPts = null;
        _recordGuess = "";
    }

    /// <summary>
    /// **认出图形了**：把正在写的这一笔换成幽灵预览（用户还按着笔）。
    ///
    /// 做法就是**换掉 `ActiveStroke` 指向的对象**（换成一个普通图形对象）：
    ///   · 渲染不用写一行新代码——"正在书写的那一笔"本来就是每帧重画的（见 `DrawStroke`）；
    ///   · 抬手时 `EndStroke` 提交的就是这个对象，**图形从来没进过文档**，
    ///     所以"一步撤销回手绘"靠的是 <see cref="DwellShapeAction"/> 把原迹带在记录里。
    /// </summary>
    private void ArmDwellShape(Stroke ink, in ShapeGuess guess)
    {
        var shape = DwellAssist.BuildShapeStroke(guess, ink);

        // 停手 = 这一笔不再长了：把"正在写"的三条通道全收掉。
        // 不收的话屏幕上会同时留着一条手绘的歪线和一条规整图形（InkClass 那边
        // 为此要切 `EditingMode`，还留下过"两条线""预览残留"一串坑；我们这边
        // 采样和上屏都在引擎手里，所以要收的只有这三处）。
        foreach (var w in _windows) w.EndInkTrail();
        ActiveStrokeOnTrail = false;
        ClearRenderTail();
        _predictor.Reset();

        var anchor = _dwell.Anchor;                       // 笔停住的位置
        double stillMs = _dwell.StillMs(NowMs);           // 复位之前先量

        if (guess.Kind == StrokeKind.Line)
        {
            // ── **直线：唯一的例外，继续当"幽灵"按住**（用户 2026-09-24 定）────────
            // 直线的另一头正是画完最常要调的（转成水平 / 竖直），按住就能拖——比"先松手、
            // 再点它、再拖手柄"少两步。抬手才定型，而且**不选中**（顺手一划，接着写）。
            // 要记两件事：
            //   · 起步死区基准：笔尖还在这儿附近 → 线一个像素都不改（见 DwellDragSlopLogical）；
            //   · 钉住的那一头：**离笔尖远的那一头**。不能拿 `Points[0]`：识别器给的端点是
            //     "拟合方向的投影极值"，哪一头落在那里**不定**，钉错时抖一下就把线压成零长度
            //     （用户 2026-09-24 报的"竖线变成很短的小横线"）。
            _dwellInk = ink;
            ActiveStroke = shape;
            _dwellArmAnchor = anchor;
            _dwellGhostOrigin = null;   // 直线走"拖另一头改形状"那条路，不吃幽灵改大小的基准
            _dwellGhostAnchorIdx = -1;
            _dwellGhostNearIdx = -1;
            if (guess.Def.Length >= 2)
                _dwellLinePin = Vector2.Distance(guess.Def[0], anchor) >= Vector2.Distance(guess.Def[1], anchor)
                              ? guess.Def[0] : guess.Def[1];
            _dwell.Fire();
            _dirty = true;
            Console.WriteLine($"[停顿成型] 停 {stillMs:F0}ms → {guess.Kind}（{guess.Rule}）"
                              + "；按住还能拖另一头转向，松手定型（**不选中**，接着写）");
            return;
        }

        // ── **其它图形：也是幽灵 —— 按住拖到位，松手才定型 ＋ 选中** ──────────────────
        // 用户 2026-09-25 试过 ClassIn 之后改的（上一版 2026-09-24 是"识别到就定型并选中"）：
        // ClassIn 的手感是"图形变出来以后先别松手，直接把它拖到位，松手才选中"——
        // 比"松手 → 再点它 → 再拖"少两步，而画完就想挪位置这件事在板书里非常常见。
        //
        // ⚠ 和 2026-09-24 砍掉的那套幽灵**不是一回事**：砍掉的是"按住改大小 / 转角"，
        //   这里**只平移**（见 ArmedStrokeMove）——定义元素之间的相对关系一个都不动，
        //   而那批"图形消失 / 变形"的病因全出在改几何上。
        //   机制也完全复用直线那条：图形**从来没进过文档**，它就是 `ActiveStroke`；
        //   抬手时 `EndStroke` 提交（`_dwellInk != null` 那条路），并因为"不是直线"
        //   走 `committedShape` → 自动选中。
        _dwellInk = ink;
        ActiveStroke = shape;
        _dwellArmAnchor = anchor;
        var org = new Vector2[shape.Points.Count];
        for (int i = 0; i < org.Length; i++)
            org[i] = new Vector2(shape.Points[i].X, shape.Points[i].Y);
        _dwellGhostOrigin = org;

        // **锚点 / 跟着笔尖走的那一点**：**成型时挑定、拖动全程不变**。
        //   · 锚点 = 离笔尖**最远**的那个定义元素 —— 和直线的 `_dwellLinePin` **同一条判据**
        //     （人收笔就停在"终点"那一带，所以远端才是"起点"）；
        //   · 跟着走的 = 离笔尖**最近**的那一个（= 用户拖出来的那个终点）。
        // ⚠ 两个下标必须在这里定死：拖动中再算的话，指针一移动它们就可能互换
        //   → 图形会"翻来翻去"（同一个病根见 §42.6.2 第三条：直线当初拿 `Points[0]` 当支点）。
        //
        // ★ **双曲线：锚点钉死"中心"**（用户 2026-09-25 定）：
        //     "它在幽灵模式下，应该是**按照中心为锚点**进行放大缩小。"
        //   通用规则（离笔尖最远的那一个）在这里会挑错 —— 双曲线有**三个**定义元素
        //   （中心 / 渐近线角点 / 经过点），笔尖停在支线上时"离笔尖最远的"很可能是
        //   **渐近线角点**，那样缩放就绕那个角点转，图形会整个跑偏 ✗
        //   所以中心一律不动，**跟着笔尖走的那一个在"其余两点"里挑**（离笔尖最近的）。
        if (guess.Kind == StrokeKind.Hyperbola && org.Length >= 3)
        {
            _dwellGhostAnchorIdx = 0;                       // 中心 = `Points[0]`，一律不动
            _dwellGhostNearIdx = 1;
            float nD = float.MaxValue;
            for (int i = 1; i < org.Length; i++)
            {
                float dd = Vector2.Distance(org[i], anchor);
                if (dd < nD) { nD = dd; _dwellGhostNearIdx = i; }
            }
        }
        else
        {
            _dwellGhostAnchorIdx = 0;
            _dwellGhostNearIdx = 1;
            float farD = -1f, nearD = float.MaxValue;
            for (int i = 0; i < org.Length; i++)
            {
                float dd = Vector2.Distance(org[i], anchor);
                if (dd > farD) { farD = dd; _dwellGhostAnchorIdx = i; }
                if (dd < nearD) { nearD = dd; _dwellGhostNearIdx = i; }
            }
        }
        if (org.Length < 2 || _dwellGhostAnchorIdx == _dwellGhostNearIdx)
        {
            // 退化（少于两个点 / 两点重合）→ 不吃幽灵改大小，只预览（和 2026-09-24 那版一样）
            _dwellGhostAnchorIdx = -1;
            _dwellGhostNearIdx = -1;
        }

        _dwell.Fire();
        _dirty = true;
        Console.WriteLine($"[停顿成型] 停 {stillMs:F0}ms → {guess.Kind}（{guess.Rule}）"
                          + "；按住能拖到位，松手定型（**并选中**；Ctrl+Z 可回手绘）");
    }

    /// <summary>
    /// armed 之后的指针移动。返回 true = 这一下被"幽灵预览"吃掉了（不再往笔迹里堆点）。
    ///
    /// **两种图形都吃移动**（用户 2026-09-25 试过 ClassIn 之后定），差别只在"拖的是什么"：
    ///   · **直线**：**离笔尖远的那一头钉住**、拖出去就是**转向 / 伸缩**（照 ClassIn / InkClass 的
    ///     `LineAssistMove`），并在容差内吸到 0/90 —— 画坐标轴就靠这一下。留着它，是因为
    ///     直线是"顺手一划"，它的另一头正是画完最常要调的（转成水平 / 竖直），
    ///     而在选中态里调要多两步（先点它、再拖手柄）。
    ///   · **其它图形**：**改大小**（用户 2026-09-25 上手 ClassIn 之后逐条定的）——
    ///     **锚点不动，拖出去就是改另一个对角 / 半径**：
    ///     圆形"圆心不动、改变圆的大小"；矩形"有一个顶点不动，相当于拖另外一个对角线"；
    ///     而且"**旋转功能好像不能转了，应该就是拖动改大小**"（幽灵期不做旋转）。
    ///     通用规则 = **离笔尖最远的那个定义元素钉住、离笔尖最近的那个跟笔尖走**，
    ///     其余定义元素按**逐分量比例**跟着走（见下面实现）。
    ///     ⚠ 和 2026-09-24 砍掉的那套**不是**同一批代码：那三条"图形消失 / 变形"的病因
    ///     查清了**都不是幽灵态的错**（是 `EndStroke` 两道门槛量错了对象、以及直线锚点
    ///     拿了 `Points[0]` —— 三条现在都已修，见 §42.6.2）；而同节实测的"所有图形都在
    ///     跟着手抖改大小"则由**起步死区**挡着（下面第一段）。
    ///     抬手 → 定型 + 自动选中（见 `EndStroke`）。
    /// </summary>
    private bool ArmedStrokeMove(float x, float y, float screenX, float screenY)
    {
        var s = ActiveStroke;
        if (s == null) return false;

        var p = new Vector2(x, y);
        // **起步死区**（两条路共用）：笔尖还在成型那一刻的位置附近（只有驱动上报的亚像素抖动）
        // → 一个像素都不改。没有它，"按住不动"其实一直在改：直线那边笔尖正好停在支点
        // 那一头时会把线压成零长度（用户 2026-09-24 报的"竖线变成很短的小横线"；
        // `--dwelltest` H6 实测漂移 299.51）；图形这边会看到图形"自己抖着改大小"。
        // ⚠ **这一条是幽灵改大小的前提**（不是可选优化）：§42.6.2 实测过"所有图形当时都在
        //   跟着手抖改大小（1.0~3.4 画布单位）"，没有死区，重新加回来就是把那个毛病一起加回来。
        if (Vector2.Distance(p, _dwellArmAnchor) <= DwellDragSlopLogical * DpiScale)
            return true;      // 这一下照样被"幽灵预览"吃掉，只是不改

        // ── 直线：拖另一头 → 转向 / 伸缩（改形状）────────────────────────────────
        if (s.Kind == StrokeKind.Line)
        {
            if (s.Points.Count < 2) return true;
            var a = _dwellLinePin;
            var (_, b, _) = ShapeRecognize.SnapToAxis(a, p, DwellSnapDeg);
            s.SetPoints(new[] { a, b });
            _shapeInclination = SelectionHandles.InclinationDegrees(a, b);
            _shapeInclinationSnapped = Vector2.Distance(b, p) > 0.01f;
            _shapeAnchor = b;
            return true;
        }

        // ── 其它图形：改大小（锚点不动，拖出去 = 改另一个对角 / 半径）────────────────
        // 用户 2026-09-25 上手 ClassIn 逐条定的规则：
        //   (a) 圆形：**圆心不动**，改变圆的大小；
        //   (b) 矩形：**有一个顶点不动**，拖动相当于拖另外一个对角线；
        //   (c) **旋转功能不能转**，就是拖动改大小。
        // 下面这一小段同时覆盖 (a)(b)(c)，而且**一行几何都不重写**（照 42.9 第 3 条）：
        //   ① 锚点（离笔尖最远那个定义元素）**一个像素都不动** → (a)(b) 的"不动"那半句；
        //   ② "跟着走的那个"（离笔尖最近的定义元素）**直接设成指针** → 对圆就是
        //      "圆周点搬到指针上"，半径自然 = |指针 − 圆心|，圆心没动 —— 正好是 (a)；
        //      对矩形就是"另一个对角 = 指针"，起始角没动 —— 正好是 (b)；
        //   ③ 其余定义元素（三角形 / 平行四边形 / 坐标系那种三个点以上的）按**逐分量比例**
        //      跟着走，比例从"锚点 → 跟着走的那点"这一段算；
        //   ④ 全程只挪点、**不做姿态旋转** → (c)。
        // ⚠ ③ 的除法要防零：某分量在基准里就是 0（比如圆的圆周点正好在正右方）时，
        //   那个分量取 1（不变）。②对"跟着走的那个"是**直接赋值**，不经过除法，所以
        //   圆永远不会因为这个退化。
        if (_dwellGhostOrigin == null || _dwellGhostOrigin.Length < 2
            || _dwellGhostAnchorIdx < 0 || _dwellGhostNearIdx < 0) return true;

        int n = _dwellGhostOrigin.Length;
        var moved = new Vector2[n];
        for (int i = 0; i < n; i++) moved[i] = _dwellGhostOrigin[i];

        var A = _dwellGhostOrigin[_dwellGhostAnchorIdx];
        moved[_dwellGhostAnchorIdx] = A;          // ① 锚点不动（写一遍，意思写在代码里）
        moved[_dwellGhostNearIdx] = p;            // ② 跟着笔尖走的那一点

        var e0 = _dwellGhostOrigin[_dwellGhostNearIdx] - A;
        var e1 = p - A;
        float sx = MathF.Abs(e0.X) > 1e-3f ? e1.X / e0.X : 1f;
        float sy = MathF.Abs(e0.Y) > 1e-3f ? e1.Y / e0.Y : 1f;
        for (int i = 0; i < n; i++)               // ③ 其余点按比例
        {
            if (i == _dwellGhostAnchorIdx || i == _dwellGhostNearIdx) continue;
            var rel = _dwellGhostOrigin[i] - A;
            moved[i] = A + new Vector2(rel.X * sx, rel.Y * sy);
        }
        s.SetPoints(moved);
        return true;
    }

    /// <summary>
    /// **取消选中那一击不留墨**（用户 2026-09-23 定）：这一笔如果是"点一下"
    /// （**笔尖走过的总路程** ≤ <see cref="DwellTapSlopLogical"/> 逻辑像素），
    /// 就不提交、不进撤销栈。
    ///
    /// 为什么单独定这条：点一下空白原本会落**一个点**（`Stroke.IsSinglePoint` 渲染成一个实心圆点），
    /// 而"点一下别处"的意图是"收起那个选中框"，留一个墨点等于**每次取消选中都脏一块屏幕**
    /// （InkClass 也是这么处理的，见它的 `TryDiscardDismissTapStroke`）。
    ///
    /// ⚠ **判据为什么不是"首末两点的位移"**（两个来源都那么写，这里都不能参考）：
    ///   · 参考实现 Ink Canvas 用的是"**抬起点 − 按下点** ≤ 6px"
    ///     （`画布测试/Ink-Canvas-Dev/Ink Canvas/MW_PopupLayers.cs:230` 的
    ///      `DismissTapMaxMovePx`；它只在"按下真的收起了可见面板"时才立这个标记，
    ///      所以踩到的机会少）；
    ///   · 我们早先量的是**笔迹首末两点**。
    ///   两者对"点一下"都对，但对"**一笔画成个闭合圈**"（圆 / 四边形 / 五边形……首末两点
    ///   天生重合，位移 ≈ 0）会把**整笔**判成"点一下" → 用户的墨凭空消失。
    ///   这不是推演：`--dwelltest` 的 L 扫掠当场抓到 6 例（"五边形（认不出）"三档尺寸 ×
    ///   两档乱动 × 带选中，全部消失）。
    ///   换成**路程**就天然分得开：点在原地 ≈ 0，任何真画的一笔都是几十上百像素。
    /// </summary>
    private bool DwellTapLeaveNoInk(Stroke s)
    {
        if (s.Points.Count < 2) return true;
        var pts = new Vector2[s.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(s.Points[i].X, s.Points[i].Y);
        return ShapeRecognize.PathLength(pts) <= DwellTapSlopLogical * DpiScale;
    }

    // ---- 自检入口（`--dwelltest` 用；四条都是"薄封装"，不含任何逻辑）--------
    //
    // 为什么做成四个薄壳而不是一个"大模拟函数"：自检要能在**中间任何一步**断言
    //（比如"停 559ms 时**还没**变"），塞进一个函数里就只能在最后看一眼结果。
    // 时钟是自检**直接推 `NowMs`** 的——不许 sleep（600ms 一次、十几个用例，
    // 自检会从"一眼看完"变成"等十秒"，而且真机上时序不可复现）。

    /// <summary>自检用：按一下（**连"笔下面那个选中框"的分流一起走**，判据就是
    /// <see cref="TryDwellSelectionPress"/> 那一份，和真按下同源）。</summary>
    internal void DwellBeginForTest(float x, float y)
    {
        if (TryDwellSelectionPress(x, y)) return;      // 被选择手势吃掉：这一笔不该起
        BeginFreehandStrokeAt(0, Native.PT_MOUSE, x, y, x, y, 0.5f);
    }

    /// <summary>自检用：喂一个采样点（走真入口 <see cref="ExtendFreehandStroke"/>）。</summary>
    internal void DwellMoveForTest(float x, float y)
    {
        // 和真入口 `OnPointerMove` 一样：**手里没有活笔画就没有"移动"可言**
        // （非直线是"识别到就定型"，那一刻这一笔已经结束、`ActiveStroke` 已放手；
        //  真机的移动落在 `OnPointerMove` 的 `if (ActiveStroke != null)` 外面，什么都不做）。
        // 这里少写这一句的话，自检喂点会直接捅进 `ExtendFreehandStroke` 里的空引用。
        if (ActiveStroke == null) return;
        ExtendFreehandStroke(0, Native.PT_MOUSE, x, y, x, y, 0.5f);
    }

    /// <summary>自检用：假装 40ms 定时器响了一次。</summary>
    internal void DwellTickForTest() => TickDwellShape();

    /// <summary>自检用：抬手（走真入口 <see cref="EndStroke"/>）。</summary>
    internal void DwellEndForTest() => EndStroke();

    /// <summary>自检用：状态机在哪一档。</summary>
    internal DwellState DwellStateForTest => _dwell.State;

    /// <summary>自检用：这一次自动选中是不是"停顿变出来的"。</summary>
    internal bool DwellSelectedForTest => _dwellSelected;

    /// <summary>自检用：开关（默认开的那个）。用完要还原。</summary>
    internal void SetDwellEnabledForTest(bool on) => DwellShapeEnabled = on;

    private void OnPointerMove(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntMove++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 相机：屏幕 → 画布，下游全按画布坐标走
        PointerX = x; PointerY = y; PointerInside = true;

        // 图库面板的悬停（和按下同一条口径：面板是最上面那一层）。
        // 格子亮一下是"这一格点得中"的反馈；整理模式下光标停在红 ✕ 上也是同一套。
        if (LibraryPanelOpen && !_drawing)
        {
            float dpi = DpiScale;
            var panel = LibraryPanelRectNow();
            bool inside = LibraryLayout.Contains(panel, x, y);
            int hov = inside ? LibraryLayout.CellAt(panel, dpi, LibraryEntries.Count, x, y) : -1;
            if (hov != LibraryHover)
            {
                LibraryHover = hov;
                _dirty = true;      // 悬停高亮变了才重画，不是每次移动都重画
            }
            // 进 / 出面板各标一次脏：从画布带进来的落点环（笔 / 橡皮的圈）要擦掉。
            // 光靠脏区的"前两帧临时图元"不够——最后一步移动若没别的原因标脏，
            // 就不会有渲染帧去擦那一圈（屏幕上会留一个圆环印子）。
            if (inside != _onDrawnChrome) { _onDrawnChrome = inside; _dirty = true; }
            if (inside) { ApplyCursor(); return; }     // 面板里：吃掉，别让底下的内容跟着动
        }

        // 界面捕获了指针（例如按下按钮后滑出去），消息全归界面。
        if (UiCapturing)
        {
            UiPointerMove(screenX, screenY, pressure, false, inverted);   // 逻辑屏幕坐标
            _dirty = true;
            return;
        }

        // 界面优先：悬停也要转发。
        //
        // 悬停高亮、"靠近才长大"、"悬停 120 ms 才浮出"全靠这条。以前只有
        // **捕获分支**会转发，而"鼠标停在按钮上"恰恰是没捕获的状态——
        // 于是界面的悬停永远不会亮，而且不报错，只是"感觉不跟手"。
        // 书写中不转发：那一笔已经归画布了，界面这时候不该再动。
        if (!_drawing && UiPointerMove(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted))
        {
            if (!_uiHover) { _uiHover = true; ApplyCursor(); }
            _dirty = true;
            return;
        }
        if (_uiHover) { _uiHover = false; ApplyCursor(); }

        // 底部那两条 PPT 控件（放映时才在）：悬停高亮 / 进度条拖动。
        // 排在穿透之前——穿透时它的悬停与拖动照样要跟手。
        if (!_drawing && PptBarPointerMove(screenX, screenY))
        {
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 穿透模式：我们不收输入，也不该动光标（那是下层窗口的事）。
        if (PassThrough) { _dirty = true; return; }

        // 拖滚动条：和"画一笔"互斥。
        if (ScrollBarDragging)
        {
            UpdateScrollBarDrag(screenY);
            ApplyCursor();
            _dirty = true;
            return;
        }

        if (!_drawing || id != _activePointer)
        {
            // 悬停路径。以前这里直接 return，"没落笔"时引擎完全不知道指针在哪儿，
            // 于是悬停光标、滚动条悬停、落点预览都无从谈起。
            UpdateScrollBarHover(screenX, screenY);
            UpdateBarHover(x, y);                  // 操作条九格的 hover 态（画与命中同源）
            ApplyCursor();
            if (DrawnCursor != ToolCursorShape.None) _dirty = true;
            return;
        }

        var tool = inverted ? Tool.Eraser : Tool;
        // 手测台：数"指针消息"而不是"擦除步"——消息之间的间隔才是跟不跟手。
        if (tool == Tool.Eraser || tool == Tool.PixelEraser) EraserTelemetry?.Move(NowMs);
        switch (tool)
        {
            case Tool.Eraser:
                EraseAlongPath(x, y);
                break;

            case Tool.PixelEraser:
                EraseRectAlongPath(x, y);
                break;

            case Tool.Capture:
                if (CaptureActive) ExtendCaptureTo(x, y);
                break;

            case Tool.Marquee:
                // 拖着颜色面板里的粗细滑条：只吸档位，不进"整体拖动"。
                if (_sliderDragging) SetWidthStepAt(x, LiveSelectionFrame.CanvasAabb);
                else if (SelDragging) UpdateSelDrag(x, y);
                else ExtendMarqueeTo(x, y);
                break;

            case Tool.Laser:
                Laser.Add(x, y, NowMs);
                break;

            // 图形：只更新**控制点**（图形由它的定义元素决定），不往里堆采样点。
            // 预览就走"正在书写的那一笔"那条路（它本来就每帧重画），所以实时。
            //
            // 判据同样走 <see cref="IsShapeTool"/>，**不在这里逐个列 case**——
            // 和 OnPointerDown 那条是同一个理由（那边漏改过一次，两个地方一起修掉）。
            default:
                // 拖**手柄**（改几何）/ 拖整条，排在图形前面：这一类拖动是在**按下那一刻**
                // 被 OnPointerDown 接下的，而那时的工具可能还是图形工具
                //（画完自动选中那个框，见 `AutoSelectionPress` 那一段），也可能拖到一半
                // 被热键换了工具（见 SwitchTool）。两种情况 `ActiveStroke` 都是 null ——
                // 落到下面 UpdateShapePreview 上就什么都不发生（鼠标拖得动、模型一动不动）。
                if (SelDragging)
                {
                    UpdateSelDrag(x, y);
                    break;
                }
                if (ActiveStroke != null && IsShapeTool(tool))
                {
                    UpdateShapePreview(x, y);
                    break;
                }
                if (ActiveStroke != null)
                {
                    ExtendFreehandStroke(id, ptype, x, y, screenX, screenY, pressure);
                }
                break;
        }
        // 拖拽中（已捕获）：系统不会再发 WM_SETCURSOR，每帧自己维护。
        ApplyCursor();
        _dirty = true;
    }

    private void OnPointerUp(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntUp++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);

        // 界面优先收尾，否则会留下"按钮一直按着"的状态。
        if (UiCapturing && ReadPointer(id, out float ux, out float uy, out float upressure,
                                       out bool uinverted, out _))
        {
            UiPointerUp(ux, uy, upressure, false, uinverted);
            // **必须显式放开捕获**：按钮上也走 SetCapture（拖出按钮、在按钮上滑开
            // 都要继续收到消息），而系统**不会**在按键抬起时替我们放开。
            // 忘了这一句的后果不是"按钮卡住"，而是**整台机器的鼠标事件都还挂在
            // 我们窗口上**：穿透、点下层程序、换到别的软件全乱，而且现象离原因很远。
            Native.ReleaseCapture();
            UiCapturing = false;
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (ScrollBarDragging)
        {
            EndScrollBarDrag();
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // PPT 控件条（放映时才在）：**这一次按下是它吃掉的就由它收尾**——
        // 长按（拿起 / 拖走）和短按（弹页号面板）都在这里结束。
        // 必须显式 ReleaseCapture（按下那一刻 SetCapture 过）：忘了这一句，
        // **整台机器的鼠标都还挂在我们窗口上**（见上面那句注释）。
        if (_pptCapturing && ReadPointer(id, out float bx, out float by, out _, out _, out _))
        {
            _pptCapturing = false;
            PptBarPointerUp(bx, by);
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (id != _activePointer) return;
        Native.ReleaseCapture();
        _drawing = false;
        EndStroke();
    }

    /// <summary>
    /// Walks the eraser along the segment the pointer just travelled instead of
    /// only testing the newest position. Without this, a quick flick leaves gaps
    /// between samples and only some of the crossed strokes disappear - which is
    /// what "the eraser feels unresponsive" actually looks like.
    /// </summary>
    private void EraseAlongPath(float x, float y)
    {
        float r = EraserRadius;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, r * 0.5f)), 1, 48);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseAt(ex, ey, r);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    /// <summary>
    /// 像素橡皮沿指针走过的路径扫一遍。理由和整笔橡皮一样：只测最新那一点，快划就会在
    /// 两次采样之间漏掉一整段。步长取**短边的一半**——相邻两个位置至少重叠一半，
    /// 中间不会有缝；上限 64 步，免得一次大跨度拖动把每帧的代价拉爆。
    /// </summary>
    private void EraseRectAlongPath(float x, float y)
    {
        float hw = PixelEraserHalfWidthPx, hh = PixelEraserHalfHeightPx;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, MathF.Min(hw, hh))), 1, 64);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseRectAt(ex, ey, hw, hh);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    // =====================================================================
    //  截图
    //
    //  流程（每一步都有它非这么做不可的理由）：
    //
    //   1. 拖一个框（和框选同一个手势，用户不用学新动作）；
    //   2. **把自己的覆盖层藏起来**再抓屏——不然抓到的就是"批注盖在 PPT 上"
    //      那一坨，等于把老师写的字又贴回去一张；
    //   3. 抓到的是一比一的**物理像素**，按 DPI 折成画布尺寸；
    //   4. 放到**视口左上角**并立刻选中——用户看得见、拖得动、能接着复制/翻转；
    //   5. 同时写进剪贴板：老师要把它贴到别的地方（PPT、微信、Word）是常见动作。
    //  =====================================================================

    /// <summary>
    /// 覆盖层藏起来时用的句柄列表。**多屏也要一起藏**，否则副屏上抓到的还是
    /// 我们自己画的东西（多屏教室很常见）。
    /// </summary>
    private IntPtr[] OverlayHandles()
    {
        var list = new List<IntPtr>(_windows.Count);
        foreach (var w in _windows) list.Add(w.Hwnd);
        // 抓屏时要连"接输入小窗"一起藏：它是 1/255 的一层灰，肉眼看不见，
        // 但拍进图里就是一层脏（截图界面永远不该出现在截图里）。
        // PPT 条那块小窗同理（它也是我们的窗口）。
        if (_uiInputHwnd != IntPtr.Zero) list.Add(_uiInputHwnd);
        if (_pptInputHwnd != IntPtr.Zero) list.Add(_pptInputHwnd);
        return list.ToArray();
    }

    /// <summary>截图落地的位置：**视口左上角**往里缩一点（见 CaptureMargin 的注释）。</summary>
    internal const float CaptureMarginLogical = 24f;

    /// <summary>
    /// "直接截取"抓屏期间：**藏掉取景框、但保留板书**。用 using 包起来，
    /// 异常路径也不会把框永久藏掉（藏着的框=看不见自己画到哪儿，会以为软件坏了）。
    /// </summary>
    private IDisposable HiddenCaptureFrame()
    {
        CaptureFrameHidden = true;
        _dirty = true;
        RenderAll();                 // 先把自己这一帧重画成"没有框"的样子
        Native.DwmFlush();           // 等合成器贴上去，再抓
        System.Threading.Thread.Sleep(60);
        Native.DwmFlush();
        return new CaptureFrameRestore(this);
    }

    private sealed class CaptureFrameRestore : IDisposable
    {
        private readonly InkEngine _e;
        public CaptureFrameRestore(InkEngine e) => _e = e;
        public void Dispose()
        {
            _e.CaptureFrameHidden = false;
            _e._dirty = true;
        }
    }

    private void EndCapture()
    {
        CaptureActive = false;
        _dirty = true;

        // 画布坐标 → 屏幕坐标：相机偏移加回去（ScreenToCanvas 的逆运算）。
        // 相机只有纵向偏移（横向没有滚动），所以 x 不用换算。
        int x = (int)MathF.Round(MathF.Min(CapMinX, CapMaxX));
        int y = (int)MathF.Round(MathF.Min(CapMinY, CapMaxY) + ViewOffsetY);
        int w = (int)MathF.Round(MathF.Abs(CapMaxX - CapMinX));
        int h = (int)MathF.Round(MathF.Abs(CapMaxY - CapMinY));

        // 抓 1 像素宽的框没有意义（多半是"点了一下"），直接当没发生过。
        if (w < 4 || h < 4)
        {
            Console.WriteLine("截图取消：框太小");
            return;
        }

        byte[] pixels;
        if (CaptureHideInk)
        {
            // 隐藏批注截取（默认）：整个覆盖层藏起来，拍到的只有下层内容
            using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                pixels = ScreenCapture.Grab(x, y, w, h);
        }
        else
        {
            // **直接截取**：连板书一起拍，只把取景框藏掉。
            //
            // 框是我们自己画的（浮动层），所以不用藏窗口、也不用等合成器撤窗口——
            // 重画一帧就没了。等一拍再抓，是为了让合成器把这一帧真的贴上去
            // （和 HiddenOverlay 那边同一个道理，只是这里只有一帧的事）。
            using (HiddenCaptureFrame())
                pixels = ScreenCapture.Grab(x, y, w, h);
        }

        // 窗口藏过又显示，后缓冲里的内容是旧的：整层作废重画，
        // 否则屏幕上会留下一块擦不掉的残影。
        Doc.InvalidateAll();

        if (pixels == null)
        {
            Console.WriteLine("截图失败：抓屏返回空");
            return;
        }

        ClipboardImage.SetImage(pixels, w, h);

        var img = ImageData.Adopt(w, h, pixels);
        if (img == null) return;

        // 画布坐标 = 视口左上角 + 一点边距。**边距是必须的**：贴着 (0,0) 的话
        // 选中框的旋转手柄会跑到屏幕外，用户抓不到它。
        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);

        // 落到视口左上角之后可能超出视口（图比屏幕大），把相机拉回去看全它。
        EnsureVisible(placed);
        if (placed != null)
        {
            Doc.SelectOnly(new[] { placed });
            Tool = Tool.Marquee;             // 刚截完图，最可能的下一件事是摆放它
            NotifyUiStateChanged();
        }
        Console.WriteLine($"截图 {w}×{h} 物理像素 → 画布 {w / DpiScale:F0}×{h / DpiScale:F0}，已放到左上角并选中");
    }

    /// <summary>把相机滚到"这个对象看得见"。只做纵向——横向没有滚动这回事。</summary>
    private void EnsureVisible(Stroke s)
    {
        if (s == null) return;
        var vp = ViewportCanvas;
        var b = s.PaddedBounds;
        if (b.MaxY > vp.MaxY) ViewOffsetY += b.MaxY - vp.MaxY;
        else if (b.MinY < vp.MinY) ViewOffsetY += b.MinY - vp.MinY;
        ClampViewOffset();
    }

    /// <summary>
    /// 从剪贴板粘一张图（Ctrl+V）。位置规则和截图一致：**视口左上角**，
    /// 粘完立刻选中。
    /// </summary>
    /// <summary>
    /// 复制选中对象到剪贴板：**对象字节 + 一张图**。
    ///
    /// 对象字节让"粘回来仍是可编辑对象"（跨页、跨窗口、跨程序实例都行）；
    /// 图是给外部程序的兜底（Word / PPT / 微信粘到的是图，至少能用）。
    /// 只放对象不放图也行，但那样"复制一段板书贴到微信"就没反应——两头都要顾。
    /// </summary>
    internal bool CopySelectionToClipboard()
    {
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("复制：没有选中任何东西（先用框选或点选选中）");
            return false;
        }

        byte[] objects = ClipboardInk.Serialize(sel);

        // 同时渲染一张图：范围 = 这批对象的画布范围 + 4 逻辑像素留白
        byte[] dib = null; int w = 0, h = 0;
        var box = EditRegion.Of(sel);
        if (!box.IsEmpty && _windows.Count > 0)
        {
            box = box.Inflate(4f * DpiScale);
            w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            // 防呆：图太大就不放了（对象格式照放）
            if ((long)w * h <= 32_000_000) dib = _windows[0].RenderStrokesToBgra(sel, box, w, h);
            else { w = h = 0; }
        }

        bool ok = ClipboardInk.Set(objects, dib, w, h);
        Console.WriteLine(ok
            ? $"复制 {sel.Count} 个对象（同时放了一张 {w}×{h} 的图：外部程序也粘得上）"
            : "复制失败（剪贴板正被别的程序占着？）");

        // 屏幕上的反馈只有**选区闪一下**（用户反馈的"复制以后看不出来"）。
        // 提示条 2026-09-16 按用户要求去掉了：屏幕上一个字都不留。
        if (ok) SelFlashUntilMs = NowMs + SelFlashMs;
        return ok;
    }

    // ---- 导出（选中 → 透明底 PNG）------------------------------------------
    //
    // 用户 2026-09-17 定：**只导选中的**、**透明底 PNG**、**弹系统"另存为"**，
    // 而且**同时放进剪贴板**（两样都要）。落点与命名按
    // [调研-选中框-反馈-导出-层级-属性.md] 第二节。
    //
    // 屏幕上**不留字**（用户 2026-09-16 明确要求把提示条去掉），只让选区闪一下——
    // 文件对话框本身就是最清楚的反馈。

    /// <summary>
    /// 把选中的内容离屏渲染成一张**透明底** BGRA（和"复制到剪贴板"那条路同一个渲染器）。
    /// 返回 null = 不能导（没选中 / 太大 / 渲染失败），每种情况都有日志。
    /// </summary>
    private byte[] RenderSelectionForExport(out int w, out int h)
    {
        w = h = 0;
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("导出：没有选中任何东西（先用框选或点选选中）");
            return null;
        }
        var box = EditRegion.Of(sel);
        if (box.IsEmpty) return null;
        box = box.Inflate(4f * DpiScale);                     // 和"复制"同一个留白
        w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 32_000_000)
        {
            Console.WriteLine($"导出：选中的内容太大（{w}×{h}）");
            w = h = 0;
            return null;
        }
        if (_windows.Count == 0) { w = h = 0; return null; }

        var bgra = _windows[0].RenderStrokesToBgra(sel, box, w, h);
        if (bgra == null) { Console.WriteLine("导出：离屏渲染失败"); w = h = 0; }
        return bgra;
    }

    /// <summary>
    /// 写 PNG ＋ **同时放进剪贴板**（对象 ＋ 这张图，和 Ctrl+C 同一个剪贴板合同），
    /// 再让选区闪一下。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path)
        => WriteExport(bgra, w, h, path, 1);

    /// <summary>
    /// 写盘。<paramref name="filterIndex"/> = 老师在类型栏选的那一条（决定"透明底 / 白底"），
    /// 编码器按扩展名决定——两者的分工见 `ExportFormats.Encode` 的注释。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path, int filterIndex)
    {
        LastExportPath = null;
        LastExportFilterIndex = 0;
        try
        {
            var bytes = ExportFormats.Encode(path, bgra, w, h, filterIndex, out string tag);
            if (bytes == null) { Console.WriteLine($"导出：{tag} 编码失败"); return false; }
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"导出成功：{path}（{w}×{h}，{tag}，{bytes.Length / 1024.0:F0} KB）");
            LastExportPath = Path.GetFullPath(path);
            LastExportFilterIndex = filterIndex;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"导出失败：{ex.Message}");
            return false;
        }

        // 用户 2026-09-17 更正：**导出不再顺手放进剪贴板**——"复制"那条路已经有了
        // （选中 → Ctrl+C / 操作条上的复制），导出就只管落盘，职责单一、也少一次
        // 剪贴板写入（剪贴板是全局资源，能少碰就少碰）。
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **导出按钮做的事**：离屏渲染 → 弹"另存为" → 写盘。取消 = 什么都不做。
    ///
    /// 格式**就在那个对话框里选**（见 <see cref="ExportFormats"/>：透明底 / 白底 /
    /// 文件大小 / 老软件兼容四种，每种把优势写在类型名里）。我们这边**不再先弹一层
    /// 自己的格式面板**——用户 2026-09-17："那下面这两个图标就没有用了吧，用户都可以
    /// 自己保存图片了"：说得对，两处选格式反而绕，点导出直接出对话框还少一步。
    /// （触摸屏上那个下拉现在也点得开了：真因是覆盖层每秒抢层，见 ReassertTopmost。）
    /// </summary>
    /// <param name="defaultFilterIndex">
    /// 默认选中第几条（1 起）。界面把"上次用的那条"记在偏好里传进来，
    /// 所以常用 JPG 的老师下次不用再选。
    /// </param>
    /// <returns>用户最终选的那一条（1 起）；取消或没导成返回 0。</returns>
    internal int ExportSelection(int defaultFilterIndex = 1)
    {
        ExportAttempts++;
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return 0;

        // 自检模式**绝不弹对话框**：系统对话框会阻塞在那里等消息，
        // 而自检是靠"抽消息 + 渲染"自己推进的——一弹就把整条自检卡死到超时。
        // （自检要验的是"渲染 → 编码 → 写盘 → 剪贴板"这条链，那条由 --iotest 走
        //  ExportSelectionToPathForTest，不经过这里。）
        if (!ExportDialogEnabled)
        {
            Console.WriteLine("导出：自检模式不弹另存为对话框（见 --iotest）");
            return 0;
        }

        // 默认扩展名跟着选的那一条走（第 1 条 PNG 透明底是老师最常要的：
        // "贴到别处不带走白底"）。
        int want = Math.Clamp(defaultFilterIndex, 1, ExportFormats.Count);
        string suggested = $"选中-{DateTime.Now:yyyyMMdd-HHmm}" + ExportFormats.ExtensionFor(want);
        string path = null;
        int chosen = want;
        BorrowFocusForDialog();                 // 覆盖层平时不抢焦点，弹框前临时放开
        ExportDialogOpen = true;                // 弹框期间不许再抬覆盖层，见 ReassertTopmost
        try { path = ExportFileDialog.AskForImage(OwnerHwnd(), suggested, want, out chosen); }
        catch (Exception ex)
        {
            // **弹框这一步出错绝不允许打死软件**。真踩过：.NET 7 起结构体字段不能用
            // StringBuilder，那一版一点"导出"整个进程就没了（异常从 P/Invoke 冒到 Main）。
            // 现在最坏也只是"这次导不出去"，并且把原因说清楚。
            Console.WriteLine($"导出：弹另存为失败（{ex.GetType().Name}: {ex.Message}）");
            return 0;
        }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }

        if (path == null) { Console.WriteLine("导出：取消"); return 0; }
        return WriteExport(bgra, w, h, path, chosen) ? chosen : 0;
    }

    /// <summary>
    /// 导出按钮真正调的那一层：**记住老师上次选的那一条格式**（`ui.exportFormat`），
    /// 下次直接默认它——常用 JPG 的老师不用每次都去下拉里找。
    ///
    /// 这一条偏好由**引擎自己解释**（别的 `ui.*` 都是界面说了算）。理由：操作条
    /// 是引擎那边画和命中测试的（`Selection.cs`），导出按钮压根不经过 `InkUi`，
    /// 所以"记住上次的选择"只能落在这里。键名照旧放在 `ui` 段，不另开一节。
    ///
    /// 注意：自检模式下这条偏好**读不到也写不进**（自检不碰用户配置），
    /// 所以自检里走的是默认第 1 条，判据是确定的。
    /// </summary>
    private void ExportSelectionPref()
    {
        int last = ExportDefaultFilterIndex();
        int chosen = ExportSelection(last);
        if (chosen >= 1) SetUiPref("exportFormat", chosen.ToString());
    }

    /// <summary>
    /// 默认选中第几条格式：读偏好 `ui.exportFormat`，读不出来/越界就回到第 1 条（PNG 透明底）。
    /// 单独抽出来是为了能自检（"记住上次的选择"这条逻辑不依赖对话框）。
    /// </summary>
    internal int ExportDefaultFilterIndex()
        => int.TryParse(GetUiPref("exportFormat"), out int v)
           && v >= 1 && v <= ExportFormats.Count ? v : 1;

    /// <summary>自检用：不弹对话框，直接写到指定路径（其余流程一模一样）。</summary>
    internal bool ExportSelectionToPathForTest(string path, int filterIndex = 1)
    {
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return false;
        return WriteExport(bgra, w, h, path, filterIndex);
    }

    private IntPtr OwnerHwnd() => _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

    /// <summary>
    /// 导出时要不要弹系统的"另存为"。**自检模式必须关掉**（见 <see cref="ExportSelection"/>）：
    /// 系统对话框会阻塞等消息，而自检是自己抽消息推进的，一弹就卡到超时。
    /// 产品里保持 true。
    /// </summary>
    internal bool ExportDialogEnabled = true;

    /// <summary>
    /// "系统对话框正开着"。**只用来按住 <see cref="ReassertTopmost"/>**：
    /// 那一下每秒一次的置顶重抬会去跟对话框抢层，顺手把它的下拉列表关掉
    /// （用户报的"选不到 jpg"就是它）。见 <see cref="ReassertTopmost"/> 的注释。
    /// </summary>
    internal bool ExportDialogOpen;

    /// <summary>
    /// 上一次**真的写出去**的那个文件（全路径；没成功过就是 null），以及它是不是 JPEG。
    ///
    /// `--dialogprobe --save` 靠它确认"对话框 → 路径 → 编码 → 落盘"整条链真的通了，
    /// 而不是只看对话框弹没弹出来。
    /// </summary>
    internal string LastExportPath;
    /// <summary>上一次成功导出用的那一条格式（1 起，见 <see cref="ExportFormats"/>）。</summary>
    internal int LastExportFilterIndex;

    /// <summary>
    /// 试过几次导出（**包括没有选中、被自检挡下、用户取消**）。
    ///
    /// 自检要的"点一下"和"真的弹框"分开：`--edittest` 在自检模式下点导出，
    /// 对话框是关着的，只能靠这个计数器证明那一下真的走到了导出这条路
    /// （而不是"什么都没发生"）。
    /// </summary>
    internal int ExportAttempts;

    // 弹系统对话框期间借一下焦点：把 WS_EX_NOACTIVATE 摘掉、弹完装回去。
    // 和"批注键盘模式"同一个手法（那边是长期摘掉，这里是临时的）。
    private bool _focusBorrowed;

    private void BorrowFocusForDialog()
    {
        if (_focusBorrowed) return;
        _focusBorrowed = true;
        // **同时把"置顶"摘掉**：我们的覆盖层是 WS_EX_TOPMOST + 铺满整屏，
        // 而系统对话框自己的弹层（**文件类型那个下拉列表**）是普通弹窗——
        // 它会被置顶的覆盖层盖住，用户看到的就是"点开一下就收回去、选不到 JPG"
        // （用户 2026-09-17 报的）。弹框期间我们不需要浮在最上面，摘掉就好。
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | 0x0020 /*SWP_FRAMECHANGED*/);
            Native.SetWindowPos(w.Hwnd, new IntPtr(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        if (_windows.Count > 0) Native.SetForegroundWindow(_windows[0].Hwnd);
    }

    private void ReturnFocusAfterDialog()
    {
        if (!_focusBorrowed) return;
        _focusBorrowed = false;
        if (KeyboardMode) return;               // 键盘模式本来就要求能激活，别把它的样式改回去
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            // 把"置顶"装回去（覆盖层平时必须浮在所有程序上面）
            Native.SetWindowPos(w.Hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 粘贴：**优先粘回可编辑对象**——剪贴板里有我们的对象格式就还原成对象，
    /// 没有就退回"当图片粘贴"（原有行为）。落在视口左上角并自动选中，粘完就能拖走。
    /// </summary>
    internal bool PasteFromClipboard()
    {
        if (ClipboardInk.TryGetObjects(out var objs) && objs.Count > 0)
        {
            var vp = ViewportCanvas;
            float m = CaptureMarginLogical * DpiScale;
            var box = EditRegion.Of(objs);
            var move = Matrix3x2.CreateTranslation(vp.MinX + m - box.MinX, vp.MinY + m - box.MinY);
            foreach (var s in objs) s.Transform = s.Transform * move;   // 插入前平移（画布坐标）

            Doc.AddStrokes(objs);          // 一步撤销
            Doc.SelectOnly(objs);
            Tool = Tool.Marquee;
            NotifyUiStateChanged();
            _dirty = true;
            Console.WriteLine($"粘贴 {objs.Count} 个对象（仍是可编辑对象）");
            return true;
        }
        return PasteImageFromClipboard();
    }

    internal bool PasteImageFromClipboard()
    {
        if (!ClipboardImage.TryGetImage(out var pixels, out int w, out int h, out bool hasAlpha))
        {
            Console.WriteLine("粘贴：剪贴板里没有可用的图像（只认 CF_DIB / CF_DIBV5）");
            return false;
        }
        var img = ImageData.Adopt(w, h, pixels, hasAlpha);
        if (img == null) return false;

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);
        EnsureVisible(placed);
        Doc.SelectOnly(new[] { placed });
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
        _dirty = true;
        Console.WriteLine($"粘贴图片 {w}×{h}");
        return true;
    }

    // ==================================================================
    //  图库（"我的图形"）：把选中的对象存起来，之后从图形面板最后那一段取回来
    //
    //  口径与取舍见 计划-图形工具.md §41 和 ShapeLibrary 的注释；这里只写引擎这一侧：
    //    · 保存：选中 → 一个文件（磁盘那点事全在 ShapeLibrary 里）；
    //    · 面板开合 + 重读目录；
    //    · 落笔插入：点一张 → 待插入 → 画布上按下就落在那儿。**和"粘贴对象"同一条路**
    //      （平移对齐落点、进文档一步撤销、插入后自动选中）——不另造一套。
    // ==================================================================

    /// <summary>图库面板开着吗（引擎侧浮动面板，和颜色 / 层级面板同一套画法）。</summary>
    internal bool LibraryPanelOpen;
    /// <summary>面板里的条目。打开面板时读一次，存 / 删之后重读——不做增量更新（条目很少）。</summary>
    internal List<ShapeLibrary.Entry> LibraryEntries = new();
    /// <summary>指针悬停在哪个格子上（-1 = 没在格子上）。</summary>
    internal int LibraryHover = -1;
    /// <summary>
    /// 上一帧指针在不在"自绘界面块"（图库面板）上。只用来在**进出那一刻标脏**：
    /// 从画布带进来的落点环要擦掉（见 <see cref="PointerOnDrawnChrome"/> 的注释）。
    /// </summary>
    private bool _onDrawnChrome;
    /// <summary>
    /// 「整理」模式：每个格子上叠一颗红 ✕，点它就是删。
    /// 为什么要有这个模式（参考实现也有一模一样的一个）：**触摸屏没有右键**，
    /// 而"点格子"本身已经是"插入"，所以删除必须换一种手势。
    /// </summary>
    internal bool LibraryEditMode;
    /// <summary>待插入的内容（非空 = 已经点了格子，下一次在画布上按下就落在那里）。</summary>
    private List<Stroke> _libraryPending;

    /// <summary>是不是"等着落笔插入"（自检 / 光标要看它）。</summary>
    internal bool LibraryInsertArmed => _libraryPending != null;

    /// <summary>把选中的对象存成一个图库条目。返回存了几个（0 = 没存成）。</summary>
    internal int SaveSelectionToLibrary()
    {
        if (Doc.Selected.Count == 0) return 0;
        var items = new List<Stroke>(Doc.Selected);
        if (ShapeLibrary.Save(items) == null) return 0;
        // 反馈只有"闪一下"（和全选同一个 `SelFlashUntilMs`）：这一版没有 toast，
        // 而存进图库这件事**必须有个动静**——不然老师不知道点中了没有。
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return items.Count;
    }

    internal void OpenLibraryPanel()
    {
        ReloadLibrary();
        LibraryPanelOpen = true;
        LibraryEditMode = false;
        _dirty = true;
    }

    internal void CloseLibraryPanel()
    {
        if (!LibraryPanelOpen && !LibraryEditMode) return;
        LibraryPanelOpen = false;
        LibraryEditMode = false;
        LibraryHover = -1;
        _onDrawnChrome = false;   // 面板没了，"在界面上"这条状态跟着清零（下次打开重新触发标脏）
        _dirty = true;
    }

    internal void ToggleLibraryPanel()
    {
        if (LibraryPanelOpen) CloseLibraryPanel();
        else OpenLibraryPanel();
    }

    /// <summary>重读目录（打开面板 / 存完 / 删完都走这里）。</summary>
    internal void ReloadLibrary()
    {
        LibraryEntries = ShapeLibrary.List();
        LibraryHover = -1;
        _dirty = true;
    }

    /// <summary>点了第 i 个格子 = 进"落笔插入"态（面板自己收起来，别挡着要落的地方）。</summary>
    internal bool ArmLibraryInsert(int index)
    {
        if (index < 0 || index >= LibraryEntries.Count) return false;
        _libraryPending = LibraryEntries[index].Strokes;
        CloseLibraryPanel();
        // 手头正在画的东西作废（和粘贴前一样：免得这一次按下又被当成接着画）
        ActiveStroke = null;
        _stepPlan = null;
        _stepIndex = 0;
        Console.WriteLine($"图库：已选中第 {index + 1} 个条目（{_libraryPending.Count} 个对象），到画布上按下就落在那里");
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 把待插入的图库内容落在 (x, y)：**包围盒左上角对齐落点**、原始大小、进文档**一步撤销**、
    /// 插入后**自动选中**——和"粘贴对象"完全同一条路（用户要的就是"点一下就有份能拖的"）。
    /// </summary>
    internal bool TryInsertLibraryAt(float x, float y)
    {
        if (_libraryPending == null) return false;
        var src = _libraryPending;
        _libraryPending = null;

        var objs = new List<Stroke>(src.Count);
        foreach (var s in src) objs.Add(s.Clone());
        foreach (var s in objs) s.Id = 0;          // 身份重新发（同剪贴板那条教训：撞号会指错对象）

        var box = EditRegion.Of(objs);
        var move = Matrix3x2.CreateTranslation(x - box.MinX, y - box.MinY);
        foreach (var s in objs) s.Transform = s.Transform * move;

        Doc.AddStrokes(objs);
        Doc.SelectOnly(objs);
        // **收起来那一态**：和"刚画完一个图形"一致（见 `_autoSelCollapsed`）——
        // 插进来只是先给个轻的框，要整条操作条点一下圆钮。
        _autoSelCollapsed = true;
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        Console.WriteLine($"从图库插入 {objs.Count} 个对象");
        return true;
    }

    /// <summary>图库面板这一刻的矩形（**画布坐标**）。绘制、命中、脏区都问它。</summary>
    internal RectF LibraryPanelRectNow()
    {
        float dpi = DpiScale;
        var ui = UiQueryBoundsNow();                 // 界面那套是**逻辑**像素，要自己乘回 dpi
        var screen = LogicalVirtualScreen;           // 逻辑虚拟桌面
        float uiTop = ui.IsEmpty
            ? screen.MinY + 80f
            : ui.MinY * dpi;                         // 界面块的上沿（工具条在最下，上带在它上面）
        return LibraryLayout.PanelRect(screen.MinX * dpi, screen.MaxX * dpi, uiTop, dpi,
                                       LibraryEntries.Count);
    }

    /// <summary>图库面板上的按下。返回 true = 这一下归面板（不再往下走到画布）。</summary>
    private bool LibraryPointerDown(float x, float y)
    {
        float dpi = DpiScale;
        var panel = LibraryPanelRectNow();
        if (!LibraryLayout.Contains(panel, x, y)) return false;    // 面板外：交给调用方收面板

        if (LibraryLayout.CloseRect(panel, dpi).Contains(x, y)) { CloseLibraryPanel(); return true; }

        if (LibraryLayout.EditRect(panel, dpi).Contains(x, y))
        {
            LibraryEditMode = !LibraryEditMode;
            LibraryHover = -1;
            _dirty = true;
            return true;
        }

        int idx = LibraryLayout.CellAt(panel, dpi, LibraryEntries.Count, x, y);
        if (idx < 0) return true;                 // 面板里的空白：吃掉，别穿透到画布上去

        if (LibraryEditMode)
        {
            var cell = LibraryLayout.CellRect(panel, dpi, idx);
            if (LibraryLayout.BadgeRect(cell, dpi).Contains(x, y))
            {
                ShapeLibrary.Delete(LibraryEntries[idx].Path);
                ReloadLibrary();
            }
            // 整理模式下点格子本体**不插入**（参考实现同款：防止整理时误插一堆）
            return true;
        }

        ArmLibraryInsert(idx);
        return true;
    }

    /// <summary>
    /// 改**配对期隐藏**这一位，并且**顺手让它对应的内容块失效**。
    ///
    /// ## 为什么必须有这个函数的"顺手失效"（这是用户 2026-09-25 报的 bug）
    ///
    /// 症状：画两支的时候，**停顿那一刻第一笔没消失，等抬手才消失**。
    ///
    /// 根因是**只改了字段、没让渲染缓存失效**（正是本仓库记过的一条老教训）：
    /// 内容层不是每帧重画、而是**分块位图缓存**（`CanvasTileCache`），第一笔这一刻
    /// **早就烘进块里**了；而块只在 `Doc.Version` 变化时才重光栅化（`Overlay.SyncTiles`）。
    /// 于是只置字段的话：`Overlay.DrawStroke` 确实会跳过它，但**没有任何一帧重画那一块**，
    /// 屏幕上原样留着旧像素 —— 一直留到抬手提交（那一步 `RemoveStroke` 抬了版本号，
    /// 块被重画），**看起来就是"松手以后才消失"**。
    ///
    /// 所以"藏"和"让那一块重画"必须**同时**发生 —— 这就是这个函数存在的全部理由，
    /// 两个调用点（藏 / 放回来）都要走它，**不要直接给字段赋值**。
    /// </summary>
    private void SetPairHidden(Stroke s, bool on)
    {
        if (s == null || s.HiddenForPairing == on) return;
        s.HiddenForPairing = on;
        // 模型本身一个字没改（这一笔还在文档原处、撤销栈照样靠它），
        // 只是告诉渲染层"这块要重来"（见 Document.InvalidateContent 的注释）。
        Doc.InvalidateContent(s.PaddedBounds);
    }

    /// <summary>
    /// **两笔配对**：这一笔和"上一笔"是不是同一个双曲线的两支（用户 2026-09-25 定的约定）。
    ///
    /// &gt; "**画两支就是双曲线，画一支就是抛物线**。不管它实际上是抛物线还是双曲线，
    /// &gt;  我们只要按照这个来区分。"
    ///
    /// 为什么这一刀比"判形状"干净：形状判断是**连续、有噪声**的（夹角、残差都栽在这儿 ——
    /// 真实手抖下双曲线只剩 25~40%），而"**画了几笔**"是**离散、零噪声**的：老师画两支的时候，
    /// **他自己知道**在画双曲线。所以族的选择**不再交给拟合**，拟合只负责"造一个像的"。
    ///
    /// 认出来 → 返回那个双曲线，并把"上一笔"钉在 <see cref="_twoBranchPrev"/> 上
    /// （抬手提交时**一起收走**，见 `EndStroke`）；认不出 → `IsNothing`，
    /// **这一笔照原来的单笔路走**（绝不抢活）。
    ///
    /// 挑"上一笔"的四条，都很保守 —— 因为**误配的代价是"把两笔变成一个"**：
    ///   ① **文档里最后一笔**（不往前找更早的，那会开始猜）；
    ///   ② 在 **20 秒**窗口内（见 <see cref="TwoBranchWindowMs"/>）；
    ///   ③ 是**手绘的墨**（`Freehand`）—— 已经成型的对象不参与；
    ///   ④ 两边点数都够，最后交给 `TryTwoBranchHyperbola` 判**中心对称**。
    /// </summary>
    private ShapeGuess TryTwoBranchPair(Stroke ink, Vector2[] pts)
    {
        if (Doc.Strokes.Count == 0) return ShapeGuess.None("没有上一笔");
        if (NowMs - _lastInkEndMs > TwoBranchWindowMs) return ShapeGuess.None("上一笔太久");
        var prev = Doc.Strokes[^1];

        // **诊断日志**（用户 2026-09-25 上手"一个也画不出来"）：
        // 一行说清"上一笔什么样、这一笔什么样、卡在哪一道闸"。
        // 真机排查全靠它 —— 否则只能靠猜，而这条路已经猜错好几轮了。
        // 限流：最多每 500ms 一行（这个方法在停顿期间每 40ms 走一次）。
        bool log = NowMs - _pairLogMs > 500;
        if (log) _pairLogMs = NowMs;

        ShapeGuess g;
        // **"第一支"用哪一串点**：正常就是那一笔手绘的墨；
        // 但要是它**刚被停顿成型过**（收笔前手停了一下），对象里只剩 2~3 个定义点，
        // 这里就用**当初那一笔原迹**（引用比较确认它还是文档里最后一笔，见字段注释）。
        // 拿不到就置 null（= 这一笔不能当"第一支"用）。
        var prevInk = prev;
        if (prev.Kind != StrokeKind.Freehand)
            prevInk = prev == _lastDwellShape && _lastDwellInk != null ? _lastDwellInk : null;

        if (prevInk == null)
        {
            // ⚠ 这一条**很容易中**：第一支要是也停顿过（那 400ms 的静止），
            //   它就**已经变成一个对象**了，而对象的 `Points` 只剩定义点、
            //   **拿不回原来那些采样点**，于是配不成对。日志里会打出 `上一笔 Parabola`。
            //   （2026-09-26 起：**刚停顿成型的那一笔**能从 `_lastDwellInk` 拿回原迹，
            //    所以只有"更早的、别处来的对象"才会走到这里。）
            g = ShapeGuess.None("上一笔不是手绘的墨（它已经被成型过了）");
        }
        else if (prevInk.Points.Count < 8 || pts.Length < 8)
        {
            g = ShapeGuess.None("有一笔太短");
        }
        else
        {
            var prevPts = new Vector2[prevInk.Points.Count];
            for (int i = 0; i < prevPts.Length; i++)
                prevPts[i] = new Vector2(prevInk.Points[i].X, prevInk.Points[i].Y);
            g = ShapeRecognize.TryTwoBranchHyperbola(prevPts, pts, DpiScale);
            if (!g.IsNothing)
            {
                _twoBranchPrev = prev;
                _twoBranchIndex = Doc.Strokes.Count - 1;
                // ★ **幽灵期先把第一笔藏起来**（用户 2026-09-25 定：
                //   "识别的那一刻，第一笔就该消失"）—— 原来要等抬手提交才消失。
                //   ⚠ 只是**不画**（`Overlay.DrawStroke` 会跳过），文档一个字没动 ——
                //     撤销栈要靠它还在原处。
                SetPairHidden(prev, true);
            }
        }

        if (log)
            Console.WriteLine($"[两笔配对] {(g.IsNothing ? "否" : "**是双曲线**")}：{g.Rule}"
                              + $"（上一笔 {prev.Kind}·{prevInk?.Points.Count ?? 0} 点"
                              + $"{(prevInk != null && prevInk != prev ? "（用它当初的原迹）" : "")}，"
                              + $"这一笔 {pts.Length} 点）");
        return g;
    }

    private void EndStroke()
    {
        // 激光笔抬手：这条**开始计时**（停留 2 秒后再整体淡出，见 `LaserTrail.HoldMs`）。
        // 放在最前面：下面那几条分支（截屏 / 图形 / 多笔）都和激光笔无关，不必等它们；
        // 而且**每一条收笔路径都要走到**（正常抬手、丢捕获都走 `EndStroke`）——
        // 漏一次的话那条轨迹就永远是"还在写"，既不淡也不会消失。
        Laser.Release(NowMs);

        if (ScrollBarDragging) EndScrollBarDrag();
        foreach (var w in _windows) w.EndInkTrail();
        // **录墨迹**（用户 2026-09-26 提"我手画多少条双曲线给你，你按这些来定制判据"）：
        // 把这一笔的**原始采样点**追加到录制文件。放在最前面 —— 此时 `ActiveStroke`
        // 要么还是原始墨、要么是停顿成型换掉的那个对象（所以真正要用的点见 `_recordPts`）。
        FlushInkRecord();
        // **抬手就关掉停顿那颗定时器**：它只在"有笔在写"的时候有意义（见 StartDwellTimer）。
        StopDwellTimer();
        // 收笔：渲染尾立刻作废（它是"正在写"才有的东西）。**必须在提交进文档之前**清——
        // 不清的话这一条会永远在末尾带着一小截预测出来的墨，存档、导出、下次打开都带着。
        ClearRenderTail();
        ActiveStrokeOnTrail = false;
        Doc.EndErase();
        if (CaptureActive)
        {
            EndCapture();
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        // 记下"**这一笔到此结束**"的时刻 —— 两笔配对（画两支 = 双曲线）要用它算时间窗口
        // （见 `TwoBranchWindowMs`，现在是 20 秒）。
        // ⚠ 放在这里（**截屏那条分支之后**）：`CaptureActive` 那条路也走 `EndStroke`，
        //   截一次屏不该把两笔的窗口冲掉。
        _lastInkEndMs = NowMs;

        if (ActiveStroke != null)
        {
            // **多笔图形**：松手 = **这一笔完成**（照 InkClass：推进就发生在 MouseUp，
            // 见 `MW_ShapeDrawing.cs:1814-1822` 的 `drawMultiStepShapeCurrentStep`）。
            // 推进一步之后再看"还有没有下一笔"。
            //
            // 注意这里**不以"拖了多远"为准**：多笔图形的每一笔定的是**一个几何量**
            //（渐近线框 / 曲线过哪），"在目标位置点一下"和"拖过去"一样有效 ——
            // 所以不拿 ShapeDragLongEnough 去卡它（那是给一笔成形的图形防误点用的）。
            if (_stepPlan != null) _stepIndex++;

            // 松手就把"**这一笔吸到了什么**"收回去（「直棱柱」那颗胶囊只在拖动中出现，
            // 和拖顶点吸附那边同一个口径：松手就没有了）。不清的话它会一直挂在屏幕上。
            _stepSnap = ShapeSnapKind.None;

            // 还有下一笔 → 这一下松手**不算完**：半成品停在 ActiveStroke 里
            //（**不进文档、也不写撤销记录**），等最后一笔松手再统一提交。
            // 这样在用户眼里这几笔是连着的，中途不会先冒出一条"半成品"曲线、再把它改掉，
            // 撤销也不会多出好几条记录（InkClass 同样把两笔塞进同一个提交里，`:1846-1850`）。
            bool stepPending = _stepPlan != null && _stepIndex < _stepPlan.Apply.Length;

            // 图形：拖动太短 = 误点，**不提交**。什么都不留（连撤销记录都不留）——
            // 一个退化的图形（零长度直线、零面积矩形）在画面上看不见，却能被点中、
            // 能被框选，是最难解释的一类杂物。
            //
            // **多笔图形**没有"拖多长"可依（见上），所以改看**定义元素齐没齐**：
            // 双曲线少一个"曲线经过的点"就等于什么都没定下来（`MinCurvePoints` 说它要三个点），
            // 这种半成品不提交。尺寸下限在各自的算式里已经卡死
            //（`SetHyperbolaFromAsymptote`），退化不成"看不见却占着一条对象"。
            bool commit = !stepPending
                       && ActiveStroke.Points.Count > 0
                       && (_stepPlan != null
                           ? ActiveStroke.Points.Count >= Stroke.MinCurvePoints(ActiveStroke.Kind)
                           // 三种来源分三档：
                           //   · 自由笔迹：不卡长度（它本来就是一笔一画）；
                           //   · **停顿变出来的图形**：也不卡长度 —— 识别器已经验收过它
                           //    （总长 ≥ 40 逻辑像素、每条边都贴得住墨，见 ShapeRecognize）；
                           //     而**这条门槛量的是图形、不是墨**（"定义元素首末两点够不够远"），
                           //     对"定义元素天生就短"的图形会误杀，一误杀就是**图形和手绘原迹
                           //     一起被丢掉**（用户 2026-09-24 报的"图形整个会消失掉"就是这一类）。
                           //     ⚠ 今天这一档**够不到**：识别器的"总长 ≥ 40 逻辑像素"把定义元素的
                           //     尺度顶在了这条门槛之上（圆：r ≥ 6.4 逻辑像素 > 4）。留着它是防
                           //     "以后加一个定义元素很小的图形"（比如一个"点"）——那时它会立刻用上，
                           //     而它挡的是**用户的墨凭空消失**，不是省一行代码的事；
                           //   · 图形工具：照旧要"拖够长"（防误点、防画出退化的零面积图形
                           //     ——那种东西看不见却点得中，是最难解释的一类杂物）。
                           : (!IsShapeTool(ActiveStroke.Tool) && _dwellInk == null)
                             || _dwellInk != null
                             || ShapeDragLongEnough(ActiveStroke));

            // **取消选中那一击不留墨**（用户 2026-09-23 定）。
            //
            // 起因：停顿变出来的图形是自动选中的，而"点一下别处"的意图是收起那个框。
            // 那一击原本会落**一个实心圆点**（`Stroke.IsSinglePoint`），于是"取消选中"
            // 这个纯粹的动作会在屏幕上留下墨——**每次取消都脏一块**，板书时特别烦。
            // 判据是"整段位移 ≤ DwellTapSlopLogical"，所以真画一笔绝不会被吃掉。
            //
            // ⚠ **停顿成型那一笔不在这条里**（`_dwellInk == null` 才算）：它既然被识别器
            //   认出来了，就绝不可能是"点一下"（识别器要总长 ≥ 40 逻辑像素）；而这里量的是
            //   **图形**的首末两点 —— 圆的定义元素是（圆心，圆周点），首末两点距离就是**半径**，
            //   半径小于点选容差（8 逻辑像素）的圆会正好落进来，整笔（图形 ＋ 手绘原迹）
            //   被当成"点一下"抹掉。而这条**够得着**：圆的识别门槛是 2πr ≥ 40 → r ≥ 6.4 逻辑
            //   像素，所以 6.4~8 这一段的圆真会被误杀（`--dwelltest` H4 用半径 7.5 逻辑像素的圆
            //   钉住；把那半句删掉它当场红："整笔没了"）。
            if (commit && _dwellInk == null && _dismissTapArmed && !IsShapeTool(ActiveStroke.Tool)
                && _stepPlan == null && DwellTapLeaveNoInk(ActiveStroke))
            {
                commit = false;
                Console.WriteLine("[停顿成型] 这一下只是取消选中，不留墨");
            }

            // 这一次提交的是不是一个**刚成型的图形**（要不要自动选中它，见下面"画完自动选中"）
            Stroke committedShape = null;
            if (commit)
            {
                // **双曲线的渐近线按档收口**（用户 2026-09-22："化的时候是都有渐近线，
                // 但是最终显示没有"）：画的过程中一直是"有"（那是向导，见 BeginShapeAt），
                // 交到文档里的这一刻才按面板那一档决定 —— 于是"无渐近线"那一档
                // 画的时候照样看得到那两条虚线，画完就只剩曲线。
                // ⚠ 必须走 `SetShowAsymptotes`（它会抬 Revision）——直接给字段赋值的话，
                // 屏幕上那份"带渐近线"的辅助几何缓存在那儿不重建，画完照样看得见那两条虚线
                //（用户 2026-09-22 报的就是这个："我选择的不带渐近线的，但是画完以后还有渐近线"）。
                if (ActiveStroke.Kind == StrokeKind.Hyperbola)
                    ActiveStroke.SetShowAsymptotes(HyperbolaAsymptotes);
                if (_dwellInk != null)
                {
                    // 停顿成型：这一笔不再是"正在写"（曲线化恢复生效）。即使定型成的是
                    // 自由笔迹（`_dwellInk` 那条路），也不该再带着"活笔"的标志。
                    ActiveStroke.RawWhileLive = false;
                    // **停顿成型**：走"替换型"提交——图形进文档，手绘原迹跟着撤销栈走，
                    // 于是按一次 Ctrl+Z 回到**自己画的那一笔**（见 DwellShapeAction）。
                    //
                    // ★ **两笔成型**（`_twoBranchPrev != null`，用户 2026-09-25 定的
                    //   "**画两支就是双曲线**"）：这时**上一笔也要收走** ——
                    //   `RemoveStroke` 是"效果先应用"，记账全由下面那条动作负责
                    //   （它带两笔原迹，所以 **一步 Ctrl+Z 回到两笔手绘**）。
                    if (_twoBranchPrev != null)
                    {
                        var prev = _twoBranchPrev;
                        int prevIdx = _twoBranchIndex;
                        _twoBranchPrev = null;
                        _twoBranchIndex = -1;
                        // ⚠ **必须先把隐藏位清掉**：不清的话 Ctrl+Z 把这一笔放回来时
                        //   它还是隐身的 —— 屏幕上"撤销之后什么都没回来"，而且再也变不回来 ✗
                        //   （走 `SetPairHidden` 而不是直接赋值：它会把"那一笔原来待的块"
                        //     一起标脏，见那个函数的注释）
                        SetPairHidden(prev, false);
                        Doc.RemoveStroke(prev);
                        Doc.AddDwellShape(ActiveStroke, _dwellInk, prev, prevIdx);
                    }
                    else
                    {
                        Doc.AddDwellShape(ActiveStroke, _dwellInk);
                        // **把"这一支的原迹"留一个引用**（见 `_lastDwellShape` 那段注释）：
                        // 画双曲线的第一支时手要是停了一下，它就先变成了一个**抛物线对象**，
                        // 而对象里拿不回采样点 → 两笔配对就断了。只认**抛物线**这一种：
                        // 那是"画一支双曲线被误判成抛物线"的真实情形；别的种类（圆 / 椭圆…）
                        // 不在这里放行 —— 它们的形状和"一支双曲线"差太远，误配的代价更大。
                        if (ActiveStroke.Kind == StrokeKind.Parabola)
                        {
                            _lastDwellShape = ActiveStroke;
                            _lastDwellInk = _dwellInk;
                        }
                    }
                    _dwellCommitted = true;
                    // **直线抬手后不选中**（用户 2026-09-23 定）：直线是"顺手一划"，画完要立刻接着
                    // 写下一笔——弹出一个选中框 + 操作条反而挡路；按住期间已经能调（转向/伸缩），
                    // 抬手之后想再调就再点它一下（和 GoodNotes"松手后点一下才选中"同一个手感）。
                    // ⚠ 这一条例外**只在这条路上**：图形工具画的直线照旧自动选中（那是既有设计）。
                    if (ActiveStroke.Kind != StrokeKind.Line) committedShape = ActiveStroke;
                    Console.WriteLine($"[停顿成型] 定型 {ActiveStroke.Kind}"
                                      + (committedShape != null ? "（已选中；Ctrl+Z 可回手绘）" : "（未选中；Ctrl+Z 可回手绘）"));
                }
                else
                {
                    // 落笔：从这一刻起曲线化生效（几何缓存键里带着这个标志，会自己重画）
                    ActiveStroke.RawWhileLive = false;
                    Doc.AddStroke(ActiveStroke);
                    // 真提交进文档了才算"成型"——下面那一步要拿它做自动选中。
                    if (IsShapeTool(ActiveStroke.Tool)) committedShape = ActiveStroke;
                }
                // **先结账再打印**：这一段（含 AddStroke 的快照/缓存收拾）才是分配最集中的地方，
                // 晚一步收仪表，印出来的就是上一笔的数字。
                EndStrokeMeasure();
                _lastStrokeReport =
                    $"采集到 {ActiveStroke.Points.Count} 个点"
                    + $"，收到 按下{_cntDown} 移动{_cntMove} 抬起{_cntUp} 丢失捕获{_cntCaptureLost}"
                    + $"，设备={PointerTypeName(_activePointerType)}"
                    + $"，压感={(ActiveStrokeHasPressure ? "有" : "无")}"
                    + $"，合并({LastCoalescedMessages} 条消息 → {LastCoalescedSamples} 个采样点)"
                    // 预测器与预测尾：调参时这两项是**唯一能证明"到底生效没有"的东西**
                    // （速度低于 MinSpeed 时预测器会主动不出点，光看屏幕分不清是"没生效"还是"没必要"）。
                    + $"，预测器={_predictor.Count} 点/末速度 {_predictor.Speed:F3} px/ms"
                    + $"，预测尾={(_strokeHadTail ? $"自绘有（最多 {_strokeTailMax:F1} px）" : "自绘无")}"
                    + (StrokePredCount > 0
                        ? $"，喂DWM {StrokePredCount} 段（平均 {StrokePredLeadAvg:F1} / 最大 {StrokePredLeadMax:F1} px）"
                        : "")
                    // 分配与 GC：低配机排查"偶发卡顿"的**唯一依据**。
                    // 第 2 代那一位出现在书写期间，就说明这一笔画到一半被全堆回收打断过。
                    + $"，分配 {StrokeAllocBytes / 1024.0:F1} KB/GC {StrokeGc0}/{StrokeGc1}/{StrokeGc2}";
                Console.WriteLine("[笔画] " + _lastStrokeReport);
                if (StrokeGc2 > 0)
                    Console.WriteLine($"  ⚠ 这一笔期间发生了 {StrokeGc2} 次第 2 代 GC"
                                      + "（低配机上这就是一次可见的卡顿，值得查是哪里在分配）");
            }
            else if (!stepPending && IsShapeTool(ActiveStroke.Tool) && _stepPlan == null)
            {
                Console.WriteLine($"[图形] 拖动太短（< {ShapeMinDragLogical:F0} 逻辑像素），没提交");
            }
            // 多笔式没走完：**连 ActiveStroke 都不清**——它还要接着给下一笔当预览用；
            // 表和"画到第几笔"也跟着留在原地（下一笔按下时要用）。
            if (!stepPending)
            {
                ActiveStroke = null;
                _stepPlan = null;
                _stepIndex = 0;
            }

            // **画完自动选中**（用户 2026-09-22 定）：图形一成型就把它的选中框亮出来，
            // 老师可以立刻拖它、拖顶点改形状——不用先去点"框选"再回头点它一下。
            // （截图和粘贴早就是"落下就选中"，这条把图形也拉齐了。）
            //
            // **工具不换**（这一点和截图 / 粘贴不同，它们换成了框选，因为"接着画下一个"
            // 对它们不存在）：手里还是原来那个图形工具，所以"这一下算动它还是算接着画"
            // 的分流落在 `OnPointerDown`（`AutoSelectionPress`）。
            //
            // 而且刚画完是**收起来**的形态（一颗圆钮），点一下它才展开成常规那一套
            // ——理由见 `_autoSelCollapsed`（用户上手之后定的：一整条摊在图形下面又重又挡）。
            if (committedShape != null)
            {
                Doc.SelectOnly(new[] { committedShape });
                _autoSelCollapsed = true;
                _dirty = true;
            }
            // **停顿变出来的那个框，工具还是笔** —— 所以还要记一笔"这一次选中
            // 是停顿给的"，否则那个框会"画得出来、点不着"（见 `_dwellSelected`）。
            // 直线上这一位是 false（它抬手不选中，见上面）：没有选中框，也就没有"点不着"的问题。
            _dwellSelected = committedShape != null && _dwellCommitted;
        }
        // 停顿成型这一笔的账在这里结清：状态机复位、原迹引用放手
        //（原迹已经交给撤销栈了，见 DwellShapeAction —— 这里放手不会丢东西）。
        _dwell.Reset();
        _dwellInk = null;
        _dismissTapArmed = false;
        // 拖动**手柄**（改几何）的收尾：**和当前工具无关**。
        //
        // 挂在下面"选择工具"那条分支里是不够的：手柄拖动属于**选中**那一套，
        // 而松手时手上的工具可能已经不是选择工具了（拖到一半按热键换了工具，
        // 见 SwitchTool——它只作废多笔图形的半成品，不动手柄拖动）。
        // 那种情况下这一拖会**永远不提交**：屏幕上拖得好好的，松手一看 a 一点没变
        //（自检当场抓出来过）。
        //
        // ⚠ 判据是 `SelDragging` 而**不是**"是不是框选工具"：画完自动选中那个框
        //（见上面 `AutoSelectionPress` 那段）是在**图形工具**下起手拖的，
        // 按工具判的话这一拖同样永远不提交。
        if (SelDragging) EndSelDrag();
        else if (Tool == Tool.Marquee)
        {
            if (_sliderDragging) _sliderDragging = false;      // 滑条松手：只是停，不用收尾
            else ApplyMarquee();
        }
        EndStrokeMeasure();      // 兜底：没收过的分支（取消、切换工具等）也把总账结掉
        _drawing = false;
        _dirty = true;
        _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
        // 手测台：一次拖拽收尾。**必须在 EndErase 之后**——撤销栈深度要算上刚提交的那一步。
        EraserTelemetry?.EndDrag(Doc, Doc.UndoDepth, NowMs);
    }

    private static string PointerTypeName(uint t) => t switch
    {
        Native.PT_MOUSE => "鼠标",
        Native.PT_TOUCH => "触摸",
        Native.PT_PEN => "笔",
        _ => "未知",
    };

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
        /// <summary>
        /// 取景框（四个角括号 + 中心小十字）：截图工具。
        ///
        /// 为什么单独给它一个：截图 / 框选 / 图形原来**共用一个系统十字**
        /// （`ToolCursorKind` 里那一行 `Marquee or Capture or Line or ...`），
        /// 老师分不出"我现在是要截图还是要选框"——用户 2026-09-17 当场点了这一条。
        /// 取景框的角括号和"拖出来一个框"是同一个意思，一眼就分得开。
        /// </summary>
        Frame,
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
        // 指针停在**界面块**上（接输入小窗 / 图库面板这类自绘界面）：给箭头。
        // 界面上的按钮不该顶着一个笔尖圈/橡皮圈——那一圈是"落点反馈"，
        // 只对画布有意义。
        if (!_drawing && (_uiHover || PointerOnDrawnChrome())) return CursorKind.Default;

        if (PassThrough) return CursorKind.Leave;                 // 谁来接管由系统决定
        if (LastPointerType == Native.PT_TOUCH) return CursorKind.Hidden;

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
        if (LibraryPanelOpen && LibraryLayout.Contains(LibraryPanelRectNow(), PointerX, PointerY))
            return true;
        // 画布坐标 → 屏幕坐标只差一个垂直滚动量（ScreenToCanvas 就是 `y -= ViewOffsetY`），
        // x 没有滚动、直接用。
        return PptBarContains(PointerX, PointerY + ViewOffsetY);
    }

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
        // 只有"笔尖就压在屏幕上"的触屏笔才藏起来（笔尖即落点，环由自绘表达）。
        Tool.Pen => PenShowsCursor ? CursorKind.Pen : CursorKind.Hidden,
        Tool.Highlighter => CursorKind.Hidden,      // 落点由自绘的宽度圆盘表达
        Tool.Laser => CursorKind.Hidden,            // 落点由自绘的实心点表达
        // 落点由自绘圆环 / 矩形表达；开了 EraserKeepsSystemCursor 就两个都显示（A/B 用）。
        Tool.Eraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        Tool.PixelEraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        // 截图有**自己的**落点形状（取景框角括号，见 ToolCursorShape.Frame），
        // 所以系统光标藏起来——不然就是"十字 + 角括号"叠在一起。
        Tool.Capture => CursorKind.Hidden,
        // 框选 / 图形仍然用十字准星："从这儿拖到那儿"的通用语言。
        Tool.Marquee or Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Arrow
            => CursorKind.Cross,
        // 椭圆（带焦点）：和椭圆一样是"从这儿拖到那儿"的一拖，给十字准星。
        Tool.ConicEllipse => CursorKind.Cross,
        _ => CursorKind.Default,
    };

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
        float dpi = DpiScale;
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
                if (SelectionHandles.BarCollapsedRect(aabb, dpi, ViewportCanvas).Contains(canvasX, canvasY))
                    return CursorKind.Default;
            }
            else
            {
                if (SelectionHandles.BarRect(aabb, dpi, ViewportCanvas).Contains(canvasX, canvasY))
                    return CursorKind.Default;
                if (SelPanelOpen != SelPanel.None
                    && SelectionHandles.PanelContains(canvasX, canvasY, aabb, dpi, ViewportCanvas,
                                                      SelPanelOpen, SelectionHandles.SwatchCount))
                    return CursorKind.Default;
            }
        }

        var h = SelectionHandles.HitTest(canvasX, canvasY, Doc.Selected, frame, dpi);
        if (h != SelHandle.None) return HandleCursor(h);

        var lp = frame.ToLocalPoint(new Vector2(canvasX, canvasY));
        bool inside = lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
                   && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY;
        return inside ? CursorKind.Move : null;
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
            if (PassThrough || !PointerInside || LastPointerType == Native.PT_TOUCH
                || CaptureFrameHidden)
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
                // 截图：取景框角括号。**正在拖的时候不画**——那时取景框本身就是反馈，
                // 再叠一个跟着走的角括号只是噪音（用户 2026-09-17："光标配合也感觉不好"）。
                return CaptureActive ? ToolCursorShape.None : ToolCursorShape.Frame;
                case Tool.Pen:
                    // 已经在显示斜笔（鼠标 / 手写板，自绘彩笔）：落点由它一路表达，
                    // **不再叠自绘环**——"笔上再套一个圈"就是这段开头说的那种叠影。
                    if (PenShowsCursor) return ToolCursorShape.None;
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Ring;
                case Tool.Highlighter:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Disc;
                case Tool.Laser:
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
                    return MathF.Sqrt(PixelEraserHalfWidthPx * PixelEraserHalfWidthPx
                                    + PixelEraserHalfHeightPx * PixelEraserHalfHeightPx) + 12f;
                case ToolCursorShape.Dot:
                    return CursorDotRadius * 1.5f + 10f;
                case ToolCursorShape.Frame:
                    // 取景框：半对角线（角括号撑在四角）再留一点
                    return 12f * DpiScale * 1.4143f + 10f;
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
    /// 激光轨迹的粗细（像素）。**默认值和以前一模一样**：以前轨迹写死
    /// "芯 5 像素 + 外发光 26 像素"，和任何宽度设置都无关；现在按激光自己的
    /// 粗细缩放（0.625 这个系数就是让默认 4 逻辑像素 × 2 倍屏 = 5 像素，
    /// 外观不变），这样切粗细对激光才是有意义的。
    /// </summary>
    internal float LaserCorePx => MathF.Max(2f, LaserWidthLogical * DpiScale * 0.625f);
    internal float LaserGlowPx => LaserCorePx * 5.2f;

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
    }

    /// <summary>把一个相机偏移夹进合法范围（翻页要"先算目标再决定动不动"，所以抽出来）。</summary>
    private float ClampOffset(float y)
    {
        var extent = CanvasExtent;
        float lowest = _virtualH - extent.MaxY;
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
    private const double CamAnimMs = 167;      // 和界面同一套时长（167ms 是 Windows 的 Direct Entrance）

    /// <summary>
    /// 翻一屏。<paramref name="down"/> = 往下翻（内容上移、偏移变负，和滚轮同一套符号）。
    /// 返回是否真的翻了（已经在顶/底就不动）。
    /// </summary>
    internal bool FlipPage(bool down)
    {
        float want = ClampOffset(down ? ViewOffsetY - _virtualH : ViewOffsetY + _virtualH);
        if (Math.Abs(want - ViewOffsetY) < 1f) return false;

        if (!ClientAreaAnimationOn)
        {
            ViewOffsetY = want;               // 系统关了动画就直接跳终态（老机器上是常事）
            _dirty = true;
            return true;
        }
        _camFrom = ViewOffsetY; _camTo = want; _camStartMs = NowMs; _camAnimating = true;
        _dirty = true;
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
        double t = (NowMs - _camStartMs) / CamAnimMs;
        if (t >= 1.0) { ViewOffsetY = (float)_camTo; _camAnimating = false; }
        else
        {
            float k = 1f - MathF.Pow(1f - (float)t, 3f);
            ViewOffsetY = (float)(_camFrom + (_camTo - _camFrom) * k);
        }
        _dirty = true;
    }

    /// <summary>当前在第几屏（1 起）。相机偏移除以视口高 + 1。</summary>
    internal int ScreenIndex => (int)Math.Round(-ViewOffsetY / Math.Max(1f, _virtualH)) + 1;

    /// <summary>还能不能往上翻（到顶了就不行；往下永远可以——画布下面永远多一屏）。</summary>
    internal bool CanFlipPageUp => ViewOffsetY < -1f;

    /// <summary>
    /// "一页"在**画布坐标**里的高 = 一个视口高；第一页的顶 = 虚拟桌面顶。
    /// 页界线（白板模式下画的那条淡线）按这两个值算，所以它**固定在画布上**、
    /// 不随相机动——能被烘进分块缓存，平时零成本。
    /// </summary>
    internal float PageHeightCanvas => _virtualH;
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
        ViewOffsetY = w.OriginY - viewTop;
        ClampViewOffset();
        ScrollBarActiveAtMs = NowMs;
        _dirty = true;
    }

    /// <summary>指针是否停在滚动条上。停在上面就不该淡出，所以每次移动和每次心跳都要问。</summary>
    internal void UpdateScrollBarHover(float screenX, float screenY)
    {
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

    // =====================================================================
    //  图形工具：直线 / 矩形 / 椭圆 / 箭头
    //
    //  四种都是"拖 A→B"：按下记起点、拖动时**只改终点**、松手提交成一个
    //  Kind 正确的图形（见 计划-图形工具.md 8.2）。几何由两个端点定义，
    //  所以运行时只有两个点——不是"画完再拟合"，也不是先存一串采样点。
    // =====================================================================

    /// <summary>
    /// 拖动距离短于这么多**逻辑像素**就不提交（避免误点造出一个退化的图形）。
    ///
    /// 4 这个数和点选容差 <see cref="ClickToleranceLogical"/> 同一个量级：
    /// 投影上手抖个一两像素很正常，"点一下"不该留下任何东西。
    /// </summary>
    internal const float ShapeMinDragLogical = 4f;

    /// <summary>这几种工具走"拖出来一个图形"那条路（其余工具照旧写自由笔迹）。
    ///
    /// ⚠ **名单里留着长方体 / 四面体**（2026-09-20 第十二批撤的是**面板入口**，
    /// 不是画法）：它们画出来仍然是图形，旧板书里的那些要能选中、能移动、能删。
    /// 也就是"**能画**"比"**有入口**"宽一层——数轴是这个道理的第一例
    /// （见 计划-图形工具.md 11.2 与 FullUi.HasShapeEntry）。</summary>
    internal static bool IsShapeTool(Tool t)
        => t is Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Circle
             or Tool.Triangle or Tool.Parallelogram or Tool.Arrow
             or Tool.Coordinate or Tool.NumberLine
             or Tool.Parabola or Tool.Hyperbola or Tool.Sine or Tool.Cosine
             or Tool.Wave or Tool.Tangent
             // 椭圆（带焦点）（2026-09-22）：和第二行那些曲线同一族——
             // 它也是"一按一拖出一个参数化对象"，不是自由笔迹。
             or Tool.ConicEllipse
             or Tool.Cylinder or Tool.Cone or Tool.Cuboid or Tool.Tetrahedron
             or Tool.Prism or Tool.Pyramid or Tool.Frustum
             or Tool.ConeFrustum or Tool.Sphere;

    /// <summary>
    /// **一笔**做什么：把"这一笔拖到的位置"写进半成品的几何。
    ///
    /// 参数含义：`s` = 半成品对象、`origin` = **第 1 笔按下的那个点**（双曲线的中心；
    /// 后面几笔沿用，不在自己那一笔重设）、`p` = 这一笔指针所在的位置、
    /// `minSize` = 几何下限（见 <see cref="ShapeMinAxisLogical"/>，防"零尺寸图形"）。
    /// </summary>
    private delegate void StepApply(Stroke s, Vector2 origin, Vector2 p, float minSize);

    /// <summary>
    /// 一种**多笔图形**的表：**一笔一行**（`Apply` 的长度 = 这种图形要拖几笔）。
    ///
    /// 为什么要收成一张表（用户 2026-09-20 定："照他的规则复刻……用状态机"）：
    /// InkClass 里"现在画到第几笔"这件事**抄了三遍**——双曲线用自己的
    /// `drawMultiStepShapeCurrentStep`、长方体用自己的 `CuboidStrokeCollection`、
    /// 四面体用自己的 `isFirstTouchTetrahedron`，三份各写各的、各在 MouseUp 里各判一次。
    /// 我们收成**一行表**：以后加一种多笔图形 = **表里加一行 ＋ 写它那几笔的算式**，
    /// 推进 / 提交 / 作废 / 触摸那套逻辑一个字都不用动。
    ///
    /// 表里**暂时不写提示文案**：提示是 §18 那件事（用户定"最后做"），
    /// 到时候在这一行旁边加一列就够了。
    /// </summary>
    private sealed class StepPlan
    {
        public StepApply[] Apply = Array.Empty<StepApply>();
    }

    /// <summary>
    /// **双曲线：两笔**（照 InkClass 的 `drawingShapeMode = 24/25`，`MW_ShapeDrawing.cs:1051-1152`）：
    ///
    ///   第 1 笔：**从中心拖出渐近线**（`A = |dx|、B = |dy|`，"拖到哪就是哪"）；
    ///   第 2 笔：**拖到哪、曲线就经过哪**（中心沿用第 1 笔那个）。
    ///
    /// 算式各只有一份、都在模型层（<see cref="Stroke.SetHyperbolaFromAsymptote"/> /
    /// <see cref="Stroke.SetHyperbolaThroughPoint"/>），这张表只负责"第几笔调哪一个"。
    /// </summary>
    private static readonly StepPlan HyperbolaPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, min) => s.SetHyperbolaFromAsymptote(o.X, o.Y, p.X, p.Y, min),
            static (s, o, p, _) => s.SetHyperbolaThroughPoint(p.X, p.Y),
        },
    };

    /// <summary>
    /// **长方体：两笔**（照 InkClass 的 `case 9`）——第 1 笔正面矩形、第 2 笔深度。
    /// </summary>
    private static readonly StepPlan CuboidPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetCuboidFront(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetCuboidDepth(p.X, p.Y),
        },
    };

    /// <summary>**四面体：两笔**（照他的 `case 26`）——第 1 笔底面三角形、第 2 笔顶点。</summary>
    private static readonly StepPlan TetrahedronPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetTetraBase(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetTetraApex(p.X, p.Y),
        },
    };

    /// <summary>
    /// **棱柱 / 棱锥 / 棱台：两笔**（2026-09-20 用户提的，见 计划-图形工具.md §32 / §34）——
    /// 第 1 笔拖出**底面外接框**（内接正 n 边形）、第 2 笔拖到**顶上那个中心**
    ///（棱柱 = 顶面中心、棱锥 = 顶点、棱台 = 上底中心）。
    ///
    /// ⚠ **三兄弟共用这一张表、一个字都不用改**——因为它们的控制点**完全一样**
    ///（底面外接框两角 ＋ 顶上那个中心），差别只在模型里"顶上那个中心算出来的面
    /// 长什么样"（`Stroke.PrismTopLocal`）。这就是"一个动作定三种立体"的由来。
    ///
    /// 和四面体**同一套口径**（第 2 笔"拖到哪就是哪"），所以"往上拖 = 直棱柱 / 直棱锥 /
    /// 直棱台、拖歪 = 斜的"是同一个动作的自然结果；"直"那一档的**轻微吸附**在
    /// `ApplyStepGeometry` 里补（那一步不在表里，因为它要读/换档位之外的状态）。
    /// </summary>
    private static readonly StepPlan PrismPlan = new()
    {
        Apply = new StepApply[]
        {
            static (s, o, p, _) => s.SetPrismBase(o.X, o.Y, p.X, p.Y),
            static (s, o, p, _) => s.SetPrismApex(p.X, p.Y),
        },
    };

    /// <summary>
    /// 这种工具是不是**多笔**的；是的话它的表在哪（见 <see cref="StepPlan"/>）。
    /// **判据只有这一处**：按下（起半成品 / 接着改）与松手（推进还是提交）都问它，
    /// 各写一份名单就是"加一种图形必漏一处"的老毛病。
    /// </summary>
    private static StepPlan PlanOf(Tool t) => t switch
    {
        Tool.Hyperbola => HyperbolaPlan,
        Tool.Cuboid => CuboidPlan,
        Tool.Tetrahedron => TetrahedronPlan,
        // 棱柱 / 棱锥 / 棱台**共用一张表**（控制点完全一样，见 PrismPlan 的注释）——
        // 所以"加一种立体"这件事在这里是**零改动**。
        Tool.Prism or Tool.Pyramid or Tool.Frustum => PrismPlan,
        _ => null,
    };

    /// <summary>
    /// 这种图形**要拖几笔**（`PlanOf` 那张表的行数；一笔画完的给 1）。
    ///
    /// **为什么要从表里算、而不是让自检自己列一份"哪些是多笔"**：
    /// 自检里那份名单一旦跟不上（这一轮加棱锥 / 棱台时就漏了），结果不是"红"，
    /// 而是**自检按一笔去拖、多笔图形根本画不出来**——然后断言里那句
    /// `s != null && s.Kind == d.kind` 才红，报出来的却是"没画出来"，
    /// 看着像功能坏了、其实是自检写漏了。从表里算就永远同步：
    /// **加一种多笔图形 = 表里加一行，自检自动跟着拖笔。**
    /// </summary>
    internal static int ShapeStepCount(Tool t)
    {
        var plan = PlanOf(t);
        return plan == null || plan.Apply.Length < 1 ? 1 : plan.Apply.Length;
    }

    /// <summary>
    /// **多笔图形的第 1 笔**按下：起一条**还没进文档**的半成品，并记下"第一个点"
    ///（双曲线的**中心**）—— 后面几笔都围着它算。InkClass 也是这个口径：
    /// 第二笔刻意**不重设** `iniP`（`NeedUpdateIniP()`，`:1971-1977`）。
    /// </summary>
    private void BeginStepShape(Tool tool, StepPlan plan, float x, float y)
    {
        BeginShapeAt(tool, x, y);
        _stepPlan = plan;
        _stepIndex = 0;
        _stepOrigin = new Vector2(x, y);
    }

    /// <summary>
    /// 把"**当前这一笔**、指针在 `(x, y)`"写进半成品的几何（查表 → 调模型那一份算式）。
    ///
    /// **按下和拖动都调它**：按下也调，是为了"在目标位置点一下、没怎么拖"也算数
    ///（老师很可能就直接点在要经过的地方）。
    /// </summary>
    private bool ApplyStepGeometry(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || _stepPlan == null) return false;
        if (_stepIndex < 0 || _stepIndex >= _stepPlan.Apply.Length) return false;
        _stepPlan.Apply[_stepIndex](s, _stepOrigin, new Vector2(x, y),
                                    ShapeMinAxisLogical * DpiScale);

        // **棱柱第 2 笔的"直棱柱"轻微吸附**（用户 2026-09-20："直棱柱有一个轻微吸附"）——
        // 写在**这一步之后**：先按指针老老实实落一次，再看要不要把它扳直。
        //
        // 为什么放在这里而不是放进 `PrismPlan` 那张表：表里的每一行是**纯算式**
        //（`(s,o,p,min) => …`，不读引擎状态、不出状态），而吸附要**把结果报给界面**
        //（出「直棱柱」那颗胶囊，见 `_stepSnap`），还要有一份容差常量。留在这里两边都干净。
        //
        // 判据用**长度**（"顶心和底心的横向差"小于容差），和"正圆"那条吸附同一个口径：
        // "轻微吸附"就该是"手抖一点点也算直"，用角度在柱子很高时会变得很难吸住。
        //
        // **棱柱 / 棱锥 / 棱台共用这一条**（问 `Stroke.IsPrismFamily`，不各写一份名单）：
        // 三兄弟的"直"是同一件事——**顶上那个中心在底心正上方**。
        // 报给界面的胶囊名字按种类分（直棱柱 / 直棱锥 / 直棱台，见 UprightSnapOf）。
        _stepSnap = ShapeSnapKind.None;
        if (_stepIndex == 1 && Stroke.IsPrismFamily(s.Kind))
        {
            float dx = MathF.Abs(s.PrismApexLocal().X - s.PrismBaseCenterLocal().X);
            if (dx <= PrismUprightToleranceLogical * DpiScale)
            {
                s.SnapPrismApexVertical();
                _stepSnap = UprightSnapOf(s.Kind);
            }
        }
        // 胶囊挂在"**吸完之后**那个顶面中心"上（画布坐标）——绘制与脏区都读这一个数
        //（理由同 VertexPreviewCanvasPoint：两边各算一份就会差一帧、留残影）。
        StepSnapAnchor = Vector2.Transform(s.PrismApexLocal(), s.Transform);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// "直棱柱"吸附的**横向容差**（逻辑像素）：顶心和底心的横坐标差在这个范围内就扳直。
    /// 12 是照"手抖一两像素 + 投影上的一点偏"定的；比点选容差（4）宽，
    /// 因为这是个"轻微吸附"，不是"精确判定"。
    /// </summary>
    internal const float PrismUprightToleranceLogical = 12f;

    /// <summary>
    /// 吸到竖直时该报哪一颗胶囊：**名字按种类分**（直棱柱 / 直棱锥 / 直棱台），
    /// 因为老师说出口的是"这是个直棱锥"，不是笼统的"吸住了"。
    ///
    /// 吸附的**判据**三兄弟共用（都在 <see cref="ApplyStepGeometry"/> 里那一处），
    /// 这里只是把"吸到了什么"翻成人话（标签本体在 `SelectionHandles.ShapeSnapLabel`）。
    /// </summary>
    private static ShapeSnapKind UprightSnapOf(StrokeKind k) => k switch
    {
        StrokeKind.Pyramid => ShapeSnapKind.RightPyramid,
        StrokeKind.Frustum => ShapeSnapKind.RightFrustum,
        _ => ShapeSnapKind.RightPrism,
    };

    /// <summary>
    /// **多笔图形当前那一笔吸到了什么**（现在只有棱柱的「直棱柱」一档）。
    /// 和 <see cref="ShapeSnapKind"/> 同一个枚举、同一颗胶囊——用户看到的语言要一致。
    /// 松手 / 收笔时必须清掉（见 `EndStroke`），否则那颗胶囊会留在屏幕上。
    /// </summary>
    internal ShapeSnapKind StepSnap => _stepSnap;
    private ShapeSnapKind _stepSnap = ShapeSnapKind.None;

    /// <summary>「直棱柱」那颗胶囊挂在哪（**画布坐标** = 吸完之后的顶面中心）。
    /// 绘制与脏区都读它，不许各算一份（同 <see cref="VertexPreviewCanvasPoint"/>）。</summary>
    internal Vector2 StepSnapAnchor;


    /// <summary>
    /// **多笔图形的第 1 笔**里"屏幕上先只出现一半"的那一个：双曲线还没定"曲线经过的点"时，
    /// **只画渐近线、先不画曲线**——照 InkClass（第一笔只画两条虚线渐近线，
    /// `MW_ShapeDrawing.cs:1059-1068`，曲线是第二笔才出现的）。
    ///
    /// 判据直接看**模型有没有第三个点**，不另设状态位：没有那个点，曲线本来就没定义
    ///（`HyperbolaCurveALocal` 只能给一个兜底大小），画出来是假的。
    /// 渲染那一头在 Overlay 里读它（见 `DrawStrokeCore` 开头的说明）。
    /// </summary>
    internal bool HyperAsymptotePreviewOnly
        => ActiveStroke?.Kind == StrokeKind.Hyperbola && ActiveStroke.Points.Count < 3;

    /// <summary>图形工具 → 它画出来的种类。**和 <see cref="IsShapeTool"/> 同一份名单**，
    /// 加图形时两处都要动（自检 `--shapebandtest` 的"名单一致"那条会卡住）。</summary>
    internal static StrokeKind KindOfShapeTool(Tool t) => t switch
    {
        Tool.Line => StrokeKind.Line,
        Tool.Rectangle => StrokeKind.Rectangle,
        Tool.Ellipse => StrokeKind.Ellipse,
        Tool.ConicEllipse => StrokeKind.ConicEllipse,
        Tool.Circle => StrokeKind.Circle,
        Tool.Triangle => StrokeKind.Triangle,
        Tool.Parallelogram => StrokeKind.Parallelogram,
        Tool.Coordinate => StrokeKind.Coordinate,
        Tool.NumberLine => StrokeKind.NumberLine,
        Tool.Parabola => StrokeKind.Parabola,
        Tool.Hyperbola => StrokeKind.Hyperbola,
        Tool.Sine => StrokeKind.Sine,
        Tool.Cosine => StrokeKind.Cosine,
        Tool.Wave => StrokeKind.Wave,
        Tool.Tangent => StrokeKind.Tangent,
        Tool.Cylinder => StrokeKind.Cylinder,
        Tool.Cone => StrokeKind.Cone,
        Tool.Cuboid => StrokeKind.Cuboid,
        Tool.Tetrahedron => StrokeKind.Tetrahedron,
        Tool.Prism => StrokeKind.Prism,
        Tool.Pyramid => StrokeKind.Pyramid,
        Tool.Frustum => StrokeKind.Frustum,
        Tool.ConeFrustum => StrokeKind.ConeFrustum,
        Tool.Sphere => StrokeKind.Sphere,
        _ => StrokeKind.Arrow,
    };

    /// <summary>
    /// 抛物线**一笔**（照 InkClass 的 `case 20/21`：顶点 → 末端点，一次拖完）：
    ///
    ///   · 顶点 = **按下那一刻**那个点（见 <see cref="BeginShapeAt"/>）；
    ///   · **开口朝哪边**由这一拖的符号定（见 <see cref="Stroke.ParabolaAxisOfDrag"/>）
    ///     —— 面板那一格只回答"**上下还是左右**"这件推不出来的事（`ParabolaAxis`）；
    ///   · `p`（张口）与"**画到哪**"都由这一拖定：曲线**正好停在你拖到的那个点**上
    ///     （`p = t²/(2s)` 反解；画出范围见 `Stroke.ParabolaSpanOf`）。
    ///
    /// 用户 2026-09-20 的口径（原话）："现在这个双曲线和抛物线的感觉不对，还是参考他的逻辑吧"
    /// —— 上一版"方向由面板选死"的问题是：面板选着向上、手却往下拖时反解出负数，
    /// 曲线会当场缩成一条细针（`p` 掉到下限）。方向跟着拖动走就再也不会出这种事。
    /// </summary>
    private bool UpdateParabolaPreview(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || s.Kind != StrokeKind.Parabola) return false;
        var v = new Vector2(s.Points[0].X, s.Points[0].Y);
        var q = new Vector2(x, y);
        // **方向先定**：`SetParabolaVertex` 要用它来铺那个占位点（见模型里那段注释）。
        s.CurveAxis = Stroke.ParabolaAxisOfDrag(v, q, ParabolaAxis);
        s.SetParabolaVertex(v.X, v.Y);
        s.SetParabolaThroughPoint(q.X, q.Y);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **抛物线工具当前的"哪一对"**（用户 2026-09-20 定：画**之前**定好，不在选中框里改——
    /// 他原话是"选中框的抛物线按钮功能取消，我不打算从这个转抛物线开口"）。
    ///
    /// 只两档、也正是 InkClass 的那两个按钮：
    ///   · `OpenUp` = **上下抛物**（他的 `y = ax²`，`case 20`）；
    ///   · `OpenRight` = **左右抛物**（他的 `y² = ax`，`case 21`）。
    ///
    /// **具体朝哪边不在这里**——由画的时候那一拖的符号定（见 `Stroke.ParabolaAxisOfDrag`）。
    /// 2026-09-20 晚用户看过之后定："感觉不对，还是参考他的逻辑"：
    /// 方向由面板选死时，"选着向上、手却往下拖"会让曲线缩成一条细针。
    /// 面板只回答推不出来的那件事（上下还是左右），连续量（朝哪边、多大、多长）全交给手。
    ///
    /// 它只在两处出现：图形面板里**再点一次那一格**换一档（见 FullUi.ActivateSegment 与
    /// <see cref="CycleParabolaAxis"/>），以及画的时候写进对象（见 <see cref="BeginShapeAt"/>）。
    ///
    /// 存成**引擎字段**、不存偏好文件：一次课里连画几条同向的抛物线是常态，
    /// 留着上一次那个方向比每次回"上下"顺手；但也犯不上跨进程记着，所以不进偏好。
    /// </summary>
    public CurveAxis ParabolaAxis { get; private set; } = CurveAxis.OpenUp;

    /// <summary>
    /// 换下一档"哪一对"：**上下 → 左右 → 上下**（见 <see cref="ParabolaAxis"/>）。
    /// 只影响**下一笔**画出来的抛物线，不碰已经画好的对象。
    /// </summary>
    public void CycleParabolaAxis()
    {
        ParabolaAxis = ParabolaAxis == CurveAxis.OpenUp ? CurveAxis.OpenRight : CurveAxis.OpenUp;
        // 面板上那一格的图标要跟着换，所以推一次状态（同时标脏，HUD 之类也读它）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **直线那一格当前的线型**（实线 / 虚线 / 点线）。用户 2026-09-20 定：
    /// "点击直线的图标，它会变成虚线，再点击变成点虚线，再点击又变成直线……
    /// **这样就省了好几个空间格**"。
    ///
    /// 为什么只给直线、不给全部图形：那一格点第二下**要有新含义**才敢这么用，
    /// 而图形面板里现在只有"抛物线换开口方向"占着这个动作（见 `FullUi.ActivateSegment`）。
    /// 直线是画得最多的一种，虚实线又是老师最常用的两种（辅助线、延长线），
    /// 所以先给它；别的图形以后真需要，照这个模子加即可。
    ///
    /// **它是"下一笔用哪种"，不是"板上那些直线现在是什么"**——已经画好的各存各的
    /// （见 <see cref="Stroke.Dash"/>），要改它们走选中后的操作条面板（`SetSelectionDash`）。
    /// 和 `PenDash` 那条口径一样，区别只是"哪支工具吃它"。
    ///
    /// 存成**引擎字段**、不存偏好文件（和 <see cref="ParabolaAxis"/> 同一条理由）：
    /// 一次课里连画几条虚线是常态，留着上一档比每次回实线顺手。
    /// </summary>
    public StrokeDash LineDash { get; private set; } = StrokeDash.Solid;

    /// <summary>
    /// 换下一档直线线型：**实线 → 虚线 → 点线 → 实线**（见 <see cref="LineDash"/>）。
    /// 只影响**下一笔**画出来的直线，不碰已经画好的对象。
    /// </summary>
    public void CycleLineDash()
    {
        // 三档一轮：Solid(0) → Dashed(1) → Dotted(2) → Solid。
        // 用取模而不是列举，是为了以后要加第四档（比如点划线）时只改这里一句话。
        LineDash = (StrokeDash)(((int)LineDash + 1) % 3);
        // **记住线型**（实线 / 虚线 / 点线，用户 2026-09-30 定：重启回来还是它）
        SetUiPref("lineDash", ((int)LineDash).ToString());
        // 面板上那一格的图标要跟着换，所以推一次状态（和抛物线换朝向同一套）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **立体那一格当前的档**：底面几边形（3~6）。用户 2026-09-20 定：
    /// "我想想能不能做成像直线切换那样切换三四五六"——于是它和 <see cref="LineDash"/>
    /// **完全同构**：面板上那一格**再点一次换一档**（配 4 个档位点），
    /// 画的那一刻写进对象（见 `BeginShapeAt`）。
    ///
    /// 和 <see cref="LineDash"/> 一样只管"**下一笔**"：已经画好的棱柱各存各的
    ///（见 <see cref="Stroke.PrismSides"/>），面板换档不会回头改它们。
    /// 存成引擎字段、不存偏好文件（和抛物线朝向、直线线型同一条理由）：
    /// 一次课里连画几个同一种棱柱是常态，留着上一档比每次回到默认顺手。
    ///
    /// ⚠ **棱柱 / 棱锥 / 棱台各记各的档，不是一个共享的档**——用户 2026-09-20 上手就发现
    /// "切一个另外两个也动"，那是 bug：一个人完全可能"四棱柱配三棱锥"，
    /// 三格共用一个数反而没法表达。所以这里是**三个字段**，问谁要问
    /// <see cref="SidesFor"/>（**唯一判据**，别在外面各写一份 switch）。
    /// </summary>
    private int _sidesPrism = Stroke.DefaultPrismSides;
    private int _sidesPyramid = Stroke.DefaultPrismSides;
    private int _sidesFrustum = Stroke.DefaultPrismSides;

    /// <summary>
    /// **某个立体工具当前那一档**（底面几边形，3~6）。不是这一族的工具给默认档
    ///（调用方也就不会拿它去画什么）。
    /// </summary>
    public int SidesFor(Tool tool) => tool switch
    {
        Tool.Pyramid => _sidesPyramid,
        Tool.Frustum => _sidesFrustum,
        Tool.Prism => _sidesPrism,
        _ => Stroke.DefaultPrismSides,
    };

    /// <summary>
    /// 换**当前工具**那一档：**3 → 4 → 5 → 6 → 3**（见 <see cref="SidesFor"/>）。
    /// 只影响**下一笔**画出来的那一个，既不碰已经画好的对象，也**不动另外两格**
    ///（"棱柱换档 → 棱锥的档跟着走"是用户 2026-09-20 报的 bug）。
    /// </summary>
    public void CycleSolidSides()
    {
        var t = this.Tool;
        if (!ShapeSpec.HasSideCount(t)) return;          // 不是那一族就什么也不做
        // 四档一轮，**按边数升序**（用户定：三/四/五/六）——升序比"按常用度"更好记，
        // 而且档位点从上往下读出来就是 3/4/5/6，不用额外记顺序。
        int cur = SidesFor(t);
        int next = cur >= Stroke.MaxPrismSides ? Stroke.MinPrismSides : cur + 1;
        switch (t)
        {
            case Tool.Pyramid: _sidesPyramid = next; break;
            case Tool.Frustum: _sidesFrustum = next; break;
            default: _sidesPrism = next; break;
        }
        // 面板上那一格的图标要跟着换，所以推一次状态（和直线换线型同一套）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **双曲线那一格当前的档**：画不画那两条虚线渐近线（用户 2026-09-22 提的
    /// "增加两挡，有渐近线和无渐近线"）。
    ///
    /// 和 <see cref="LineDash"/> / <see cref="CycleSolidSides"/> **完全同构**：
    /// 面板上那一格**再点一次换一档**（配 2 个档位点、图标跟着换），
    /// 画的那一刻写进对象（见 <see cref="BeginShapeAt"/> 与 <see cref="EndStroke"/>）。
    ///
    /// ⚠ **画的时候一律画渐近线**（用户 2026-09-22 的原话："化的时候是都有渐近线，
    /// 但是最终显示没有"）——那两条虚线是**画法的向导**（第一步就是"从中心拖出渐近线框"，
    /// 见 <see cref="HyperbolaPlan"/>），边画边看是必须的；它按档**收口在松手那一刻**
    ///（见 <see cref="EndStroke"/> 里的提交那一段）。
    ///
    /// 存成引擎字段、不存偏好文件（和抛物线朝向、直线线型、立体边数同一条理由）：
    /// 一节课里连画几条同一种双曲线是常态，留着上一档比每次回默认顺手。
    /// </summary>
    public bool HyperbolaAsymptotes { get; private set; } = true;

    /// <summary>换下一档"双曲线画不画渐近线"：**有 → 无 → 有**（见 <see cref="HyperbolaAsymptotes"/>）。</summary>
    public void CycleHyperbolaAsymptotes()
    {
        HyperbolaAsymptotes = !HyperbolaAsymptotes;
        // 面板上那一格的图标要跟着换，所以推一次状态（和换棱柱边数同一套）。
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **椭圆（带焦点）那一格当前的档**：画不画焦点三角形（用户 2026-09-22 提的
    /// "两档：有焦点三角形和没有焦点三角形"；**两个焦点两档都画**，见
    /// <see cref="Stroke.FocusTriangle"/>）。
    ///
    /// 和 <see cref="HyperbolaAsymptotes"/> 同一套：面板那一格再点一次换档、
    /// 画的那一刻写进对象、只管"下一笔"（已经画好的各存各的）。
    /// 默认**有**：这一格的存在意义就是讲焦点三角形（只要一个椭圆的话用第一行那个椭圆）。
    /// </summary>
    public bool EllipseFocusTriangle { get; private set; } = true;

    /// <summary>换下一档"椭圆画不画焦点三角形"：**有 → 无 → 有**（见 <see cref="EllipseFocusTriangle"/>）。</summary>
    public void CycleEllipseFocusTriangle()
    {
        EllipseFocusTriangle = !EllipseFocusTriangle;
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 图形工具的起手：造一条**只有起点**的图形，拖动期由
    /// <see cref="UpdateShapePreview"/> 改控制点，松手由 <see cref="EndStroke"/> 提交。
    ///
    /// 刻意**不喂预测器、也不起委托墨迹**：那是"笔尖跟手"用的，
    /// 而图形跟着指针走的是**吸附后的控制点**——两套画在屏幕上会变成两条不一样的线。
    /// </summary>
    private void BeginShapeAt(Tool tool, float x, float y)
    {
        var kind = KindOfShapeTool(tool);
        ActiveStroke = new Stroke
        {
            Tool = tool,
            Kind = kind,
            Color = CurrentColor,
            Width = PenWidthLogical * DpiScale,
            // 坐标系要不要网格 = **画的那一刻那个开关的状态**（见 CoordGridDefault）。
            // 存到对象自己身上，之后单独改它不影响别的坐标系（见 Stroke.Grid 的注释）。
            Grid = kind == StrokeKind.Coordinate && CoordGridDefault,
            // 抛物线的**开口方向是画之前选好的**（见 ParabolaAxis）：画的那一刻写进对象，
            // 之后它就是这条曲线自己的属性，和工具当前那档再无关系。
            CurveAxis = kind == StrokeKind.Parabola ? ParabolaAxis : CurveAxis.OpenUp,
            // 直线的**线型也是画之前选好的**（见 LineDash）：同样在画的那一刻写进对象。
            // 别的图形一律实线——它们的线型历来是"选中之后在操作条面板里改"。
            Dash = kind == StrokeKind.Line ? LineDash : StrokeDash.Solid,
            // 立体那一族的**底面几边形**同样是画之前选好的（见 SidesFor）：
            // 画的那一刻写进对象，之后面板再换档也不回头改它。
            // ⚠ 三兄弟都要走这一句——2026-09-20 第一版写成 `kind == StrokeKind.Prism`，
            // 结果**棱锥 / 棱台永远画成四棱**（用户上手一句就抓出来了）。
            PrismSides = Stroke.IsPrismFamily(kind) ? SidesFor(tool) : Stroke.DefaultPrismSides,
            // 椭圆（带焦点）的**焦点三角形开关**同样是画之前选好的（见 EllipseFocusTriangle）：
            // 画的那一刻写进对象，之后面板再换档也不回头改它。
            FocusTriangle = kind != StrokeKind.ConicEllipse || EllipseFocusTriangle,
            // 双曲线的渐近线**画的时候一律画**（那是画法的向导，见 HyperbolaAsymptotes）
            // ——按档收口在松手那一刻（见 EndStroke 的提交那一段）。
            ShowAsymptotes = true,
        };
        ActiveStroke.AddPoint(x, y, 1f, NowMs);
        // 抛物线的**顶点 = 按下那个点**：它现在是**一笔画完**的（照 InkClass 的 `case 20/21`：
        // 顶点 → 末端点，一次拖完），所以顶点在这里就落定，拖动只负责"曲线过哪、开多大"
        //（见 UpdateParabolaPreview）。多笔图形的第一个点不在这里定（它们各有各的算式）。
        if (kind == StrokeKind.Parabola) ActiveStroke.SetParabolaVertex(x, y);
        // 读数状态从这一刻重新开始：不清的话，上一次画线吸住的那个强调色会漏到
        // 这一次的第一帧（还没收到移动消息，α 也还没算）。
        _shapeInclination = 0f;
        _shapeInclinationSnapped = false;
        _shapeAnchor = new Vector2(x, y);
        // 三角形 / 平行四边形是"外框 → 三个顶点"，拖动期每一帧都要拿**按下那一刻**的
        // 那个角去算外框——它不在控制点表里（控制点已经被推成三个顶点了）。
        _shapeBoxOrigin = new Vector2(x, y);
        _predictor.Reset();
        ActiveStrokeHasPressure = false;
        LastCoalescedSamples = LastCoalescedMessages = 0;
    }

    /// <summary>
    /// 拖动中更新图形的控制点。
    ///
    /// 吸附只对**有方向的两个**（直线 / 箭头）做：矩形和椭圆是"两个对角点 / 中心+外角点"
    /// 定义的，没有"倾斜角"这回事，绕起点转只会把用户拉出来的框改小（见 计划-图形工具.md 8.2
    /// 那一行只写了"画线吸附"）。Shift = 15° 硬网格、Alt = 完全自由，和旋转同一套语义。
    /// 三角形 / 平行四边形没有"倾斜角"可吸，它们的"特殊形状吸附"发生在**拖顶点**时
    /// （见 计划-图形工具.md 9.6：画的时候不吸，改的时候才吸）。
    /// </summary>
    private void UpdateShapePreview(float x, float y)
    {
        var s = ActiveStroke;
        if (s == null || s.Points.Count == 0) return;

        var start = new Vector2(s.Points[0].X, s.Points[0].Y);
        var end = new Vector2(x, y);

        // 三角形 / 平行四边形：外框（按下点 ＋ 指针）→ **三个控制点**一次算出来。
        if (s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram)
        {
            s.SetShapeBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 坐标系 / 数轴：同样是"外框 → 一次算出全部控制点"，只是控制点是四个。
        // 拖动期就必须写成**最终那一份定义**，否则预览和松手的结果会差一下
        // （和三角形那条同一个理由：两套算法 = 松手就跳）。
        // 数轴在这里顺手把 y 钉在按下点那一行上（往斜上方拖也还是水平线）。
        if (s.Kind is StrokeKind.Coordinate or StrokeKind.NumberLine)
        {
            s.SetAxisBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // **多笔图形**：这一笔的几何交给表里那一行（见表 StepPlan）。
        // **只有按住拖动才更新**——InkClass 就是这样（多步图形的几何只在 `MouseTouchMove` 里更新，
        // 指针不按键时半成品一动不动）。这也是"触摸屏也能用"的根子：手指没有悬停，
        // 而每一步本来就是"按住拖-松手"，笔 / 鼠标 / 手指走的是**同一条路**。
        if (_stepPlan != null)
        {
            ApplyStepGeometry(x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 抛物线走**一笔**（照 InkClass 的 `case 20/21`）：顶点在按下那一刻就定下了
        //（见 BeginShapeAt），这一拖只定"曲线经过哪个点、开多大"。
        if (s.Kind == StrokeKind.Parabola)
        {
            UpdateParabolaPreview(x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 立体图形（**旋转体那一族**：圆柱 / 圆锥 / 圆台 / 球）：**外接矩形 → 一次算出全部几何**
        //（和坐标系那条同一个套路：拖动期就写成最终那一份定义，否则预览和松手的结果会差一下）。
        //
        // ⚠ 判据走 `Stroke.IsRevolutionSolid`，**不再写"圆柱 or 圆锥"**：
        // 2026-09-20 加圆台时就漏了这一处（当时靠"没走到这儿也能画对"蒙过去了——
        // 兜底那条 `SetEnd` 写进去的角点虽然没归一，但 `SolidRectLocal` 自己会 min/max，
        // 结果一样）。加球时把它一并收成判据函数：**加一种旋转体不用再来补这一行**。
        if (Stroke.IsRevolutionSolid(s.Kind))
        {
            s.SetSolidBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 正弦 / 余弦 / 波浪线：按下 = **起点**（"从 y 轴开始画"），拖出去 = **终点**。
        // 正弦 / 余弦：这一拖定下**一个周期**和**振幅**（框宽就是一个周期）；
        // 波浪线：这一拖定下**画多长**和**振幅**（周期由振幅定，见 Stroke.WavePeriodLocal）。
        // 三者的算式同一份，差别只在 `WavePeriodLocal` 那一行。
        if (s.Kind is StrokeKind.Sine or StrokeKind.Cosine or StrokeKind.Wave)
        {
            s.SetWaveBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y, ShapeMinAxisLogical * DpiScale);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        // 正切：按下 = **原点**（这一支的中心），拖出去 = **以它为中心的框**——
        // 横向是半支长（渐近线正好落框边）、纵向是可视半高（超出就截断）。
        // 它和正弦/余弦**不一样**：正弦是把"起点"拖到终点（起手点留在原地），
        // 正切是**中心不动、四周一起长**。所以调用的是 `SetTangentBox` 不是 `SetWaveBox`。
        if (s.Kind == StrokeKind.Tangent)
        {
            s.SetTangentBox(_shapeBoxOrigin.X, _shapeBoxOrigin.Y, x, y, ShapeMinAxisLogical * DpiScale);
            _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
            return;
        }

        if (s.Kind is StrokeKind.Line or StrokeKind.Arrow)
        {
            bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
            bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
            end = SelectionHandles.SnapEndPoint(start, end, shift, alt, out bool snapped);
            // 画线过程中也把 α 报给浮动层（用户 2026-09-18 要的：边画边看这条线是多少度）。
            // α 由**权威函数**算，这里只是把它和"吸没吸住"一起存下来，供渲染与脏区用。
            _shapeInclination = SelectionHandles.InclinationDegrees(start, end);
            _shapeInclinationSnapped = snapped;
        }
        s.SetEnd(end.X, end.Y);
        _shapeAnchor = new Vector2(s.Points[^1].X, s.Points[^1].Y);
    }

    /// <summary>
    /// 正在画一条**有倾斜角**的图形（直线 / 箭头）。
    ///
    /// 矩形和椭圆是两个对角点定义的，没有"倾斜角"这回事，所以不给读数
    /// （画线读数只对直线/箭头，见 计划-图形工具.md 8.2）。
    /// </summary>
    internal bool ShapeInclinationActive
        => ActiveStroke != null && ActiveStroke.Kind is StrokeKind.Line or StrokeKind.Arrow;

    /// <summary>画线中那条线**当前结果**的 α（[0°,180°)）；不在画线时是 0。</summary>
    internal float ShapeInclinationDegrees => _shapeInclination;

    /// <summary>画线中的 α 是"吸"出来的吗（特殊角 / Shift 网格）——标签按它变色。</summary>
    internal bool ShapeInclinationSnapped => _shapeInclinationSnapped;

    /// <summary>画线中那个会被拖动的端点（画布坐标）：读数标签贴它外侧。</summary>
    internal Vector2 ShapeInclinationAnchor => _shapeAnchor;

    private float _shapeInclination;
    private bool _shapeInclinationSnapped;
    private Vector2 _shapeAnchor;
    /// <summary>
    /// 图形起手时按下的那个点（画布坐标）。只有三角形 / 平行四边形用得到：
    /// 它们是"外框 → 三个顶点"，而外框的两个角里有一个（按下的那个）**不在控制点表里**，
    /// 必须在起手那一刻记下来，否则拖动中做不出"外框"这个中间量。
    /// </summary>
    private Vector2 _shapeBoxOrigin;

    /// <summary>
    /// 这次拖拽够不够长（不够就当没画，什么也不留）。
    /// 判据用**画布坐标下的实际距离**，而不是"收没收到过移动消息"——
    /// 手抖一下也会收到移动消息。
    /// </summary>
    private bool ShapeDragLongEnough(Stroke s)
    {
        if (s.Points.Count < 2) return false;
        // 坐标系要单独算：它的**原点是按下点**（见 Stroke.SetAxisBox），
        // 而"拖出去的那一下" = 原点 ↔ 外框角的距离（外框角 = 原点 ± 拖动量，所以正好等于拖动量）。
        // 拿外框对角去判的话（= √2 × 拖动量）会把"只拖了 3 像素"也当成够长。
        if (s.Kind == StrokeKind.Coordinate && s.Points.Count >= 3)
        {
            var o = new Vector2(s.Points[2].X, s.Points[2].Y);
            var c = new Vector2(s.Points[0].X, s.Points[0].Y);
            return Vector2.Distance(o, c) >= ShapeMinDragLogical * DpiScale;
        }
        var a = new Vector2(s.Points[0].X, s.Points[0].Y);
        var b = new Vector2(s.Points[^1].X, s.Points[^1].Y);
        return Vector2.Distance(a, b) >= ShapeMinDragLogical * DpiScale;
    }

    /// <summary>
    /// 把这条指针消息里的**全部**采样点追加进当前笔画。
    ///
    /// 真笔走合并点（`GetPointerPenInfoHistory`，见 <c>Input/PenInput.cs</c>）：
    /// 系统会把来不及投递的移动合并进一条消息，只取最新那一个等于把采样率砍半。
    /// 非笔设备、或读不到合并点时退回"一个消息一个点"的老路（行为与以前完全一致）。
    ///
    /// 坐标：笔画存**画布**坐标；预测器喂**屏幕**坐标（湿墨轨迹也是屏幕空间）。
    /// </summary>
    private void AppendStrokeSamples(uint id, uint ptype, float curCanvasX, float curCanvasY,
                                     float screenX, float screenY, float curPressure)
    {
        if (ActiveStroke == null) return;

        if (ptype == Native.PT_PEN && _pen.Read(id, NowMs, _lastInputMsgQpc) > 0)
        {
            LastCoalescedMessages++;
            LastCoalescedSamples += _pen.Count;
            PenMessages++;
            PenSamples += _pen.Count;
            PenSawPressureMask |= _pen.AnyPressure;
            PenSawTiltMask |= _pen.HasTilt;
            PenSawRotationMask |= _pen.Last.Rotation != 0;
            for (int i = 0; i < _pen.Count; i++)
            {
                var s = _pen[i];
                float cx = s.X, cy = s.Y;
                ScreenToCanvas(ref cx, ref cy);
                ActiveStroke.AddPoint(cx, cy, s.Pressure, s.TimeMs);
                _predictor.Add(s.X, s.Y, s.TimeMs);
                PenTotalPoints++;
                if (s.HasPressure) PenPressurePoints++;
            }
            ActiveStrokeHasPressure |= _pen.AnyPressure;
            // 压感是**整笔的属性**（见 Stroke.HasPressure）：这一笔只要有一条消息报过有效压力，
            // 它就从此刻起按压感渲染。**当场写进对象**（而不是渲染时再问一次），
            // 因为湿墨、干墨、紧框、存档读的都是这一个标志。
            //
            // **压感只作用于「笔」这一支**（2026-09-20 对齐 WPF 时定的，也是官方示例的做法：
            // 荧光笔的 `DrawingAttributes.IgnorePressure = true`）：
            //   · 荧光笔是一支"平头马克笔"，粗细随压力变会让划出来的带子忽宽忽窄（满压还是 2 倍宽）；
            //   · 激光笔只是指一下，没有"笔迹粗细"这回事。
            if (ActiveStrokeHasPressure && ActiveStroke.Tool == Tool.Pen) ActiveStroke.HasPressure = true;
            UpdateRenderTail();
            return;
        }

        // ---- 鼠标 / 触摸：同一个道理，读 POINTER_INFO 的合并点 --------------------
        //
        // 这条以前不存在，代价在两类设备上同时体现：手写板**没开 Windows Ink** 时
        // 以 PT_MOUSE 上报、触摸屏是 PT_TOUCH，它们一条消息只取最新那一个点，
        // 快写时轨迹被静默抽稀，而且预测器拿不到足够密的速度估计。
        if (ptype != Native.PT_PEN && _ptr.Read(id, NowMs, _lastInputMsgQpc) > 0)
        {
            LastCoalescedMessages++;
            LastCoalescedSamples += _ptr.Count;
            PtrMessages++;
            PtrSamples += _ptr.Count;
            // HistoryCount 是"系统本来说有几条消息"，减掉我们真的收下的那几条，
            // 就是被合并掉的中间点（合并率就是它除以 HistoryCount）。
            PtrCoalescedExtra += Math.Max(0, _ptr.HistoryCount - 1);
            for (int i = 0; i < _ptr.Count; i++)
            {
                var s = _ptr[i];
                float cx = s.X, cy = s.Y;
                ScreenToCanvas(ref cx, ref cy);
                ActiveStroke.AddPoint(cx, cy, s.Pressure, s.TimeMs);
                _predictor.Add(s.X, s.Y, s.TimeMs);
                PtrTotalPoints++;
            }
            // 非笔设备没有 penMask，也就永远不会给这一笔打上 HasPressure——
            // 这正是 WPF / 微软白板里"鼠标画的那条线是等宽"的来源。
            UpdateRenderTail();
            return;
        }

        // ---- 读不到合并点：退回"一个消息一个点"的老路（行为与以前完全一致）------
        ActiveStroke.AddPoint(curCanvasX, curCanvasY, curPressure, NowMs);
        _predictor.Add(screenX, screenY, NowMs);
        UpdateRenderTail();
    }

    /// <summary>
    /// 算出"正在写的那一笔"的**渲染尾**（预测段），写进 <see cref="Stroke.RenderTail"/>。
    ///
    /// 为什么要在我们自己画的那一笔上补：委托墨迹轨迹只对真笔开，鼠标 / 触摸没有任何
    /// 低延时通道——它们的墨完全由我们画，于是墨的末端永远落在上一帧的位置。
    /// 把预测段接上，末端就回到"现在"（模型见 Prediction/InkPredictor.cs）。
    ///
    /// 四条前提，缺一条就不加尾：
    ///   · 预测开着；
    ///   · 这一笔**没有**交给系统合成器画（交给它了就不能重复补，见 ActiveStrokeOnTrail）；
    ///   · 自由笔迹 ＋ 笔 / 荧光笔 ＋ 实线（图形由控制点定义，没有"末端滞后"这回事；
    ///     虚线接尾会让 dash 图案从接缝处重新开始，看着是断的）；
    ///   · 预测器真的给出了点（刚起笔、慢写、急转弯时它会主动不给，见 Predict）。
    ///
    /// 预测在**屏幕**空间算（和委托轨迹同一条），画的时候换回**画布**空间——
    /// 滚动之后尾巴才会跟着笔迹走。
    /// </summary>
    private void UpdateRenderTail()
    {
        var s = ActiveStroke;
        if (s == null) return;

        bool eligible = PredictEnabled && !ActiveStrokeOnTrail
            && s.Kind == StrokeKind.Freehand
            && (s.Tool == Tool.Pen || s.Tool == Tool.Highlighter)
            && s.Dash == StrokeDash.Solid;

        if (!eligible || s.Points.Count == 0) { ClearRenderTail(); return; }

        int n = _predictor.Predict(_predBuf);
        if (n == 0) { ClearRenderTail(); return; }

        _tailScratch.Clear();
        for (int i = 0; i < n; i++)
        {
            float cx = _predBuf[i].X, cy = _predBuf[i].Y;
            ScreenToCanvas(ref cx, ref cy);
            _tailScratch.Add(new Vector2(cx, cy));
        }
        var last = s.Points[^1];
        PredictedTailLead = Vector2.Distance(new Vector2(last.X, last.Y), _tailScratch[^1]);
        s.SetRenderTail(_tailScratch);
        // 统计：这一笔到底有没有尾、最长多长。**必须能打印出来**——不然"手写板上看不出来"
        // 这种事只能靠猜（2026-09-22 用户实测：鼠标甩得很难受、手写板毫无反应）。
        _strokeHadTail = true;
        if (PredictedTailLead > _strokeTailMax) _strokeTailMax = PredictedTailLead;
        TailComputes++;
        TailPointsTotal += n;
        if (PredictedTailLead > TailLeadMax) TailLeadMax = PredictedTailLead;
    }

    /// <summary>
    /// 把渲染尾收掉：起笔、收笔、以及"这一帧指针根本没动"的时候都要收。
    /// 不收的后果是笔停住时笔尖前面一直挂着一小截预测出来的墨。
    /// </summary>
    private void ClearRenderTail()
    {
        ActiveStroke?.SetRenderTail(null);
        PredictedTailLead = 0;
    }

    /// <summary>
    /// 湿墨该用多粗的半径：**和干墨同一个映射**（见 <see cref="PressureWidth"/>）。
    ///
    /// 不做这一步的后果很扎眼：抬手那一瞬间，湿墨（固定半径）会跳成干墨（有粗有细）——
    /// 正在写的"一"和落笔后的"一"粗细不是一条线。
    /// </summary>
    private float TrailRadius()
    {
        var s = ActiveStroke;
        if (s == null) return 0f;
        if (!s.HasPressure || !PressureWidth.Enabled || s.Points.Count == 0) return s.Width * 0.5f;
        return PressureWidth.HalfWidth(s.Width, s.Points[^1].P);
    }

    /// <summary>
    /// 湿墨：把这一条消息里的真实点（屏幕坐标）连同**预测点**一起交给系统合成器。
    ///
    /// ⚠ **实测（2026-09-29）：DWM 不把我们喂的预测点画出来。**
    /// 判据用最大档 `--predictms 200 --predictlead 400` 做探针：同一时刻鼠标那条
    /// （预测尾由我们自己画）会窜出去一大截，**手写板那条毫无变化**。
    /// 也就是说真笔的"跟手"完全来自委托轨迹本身，预测这几段目前是**白喂**。
    /// 先留着（几次 COM 调用，代价可以忽略；万一以后系统版本开始认了就直接生效），
    /// 但**别把"笔的预测"算进效果账里**——笔那条路的效果全在弧线和宽度上。
    /// 报告里 `喂DWM N 段（平均 X / 最大 Y px）` 这一项只在排查"到底喂没喂"时看。
    /// </summary>
    /// 预测只作用于湿墨——它画的是"正在写的这一笔"的最后一小段，真实点一到就被覆盖，
    /// 不进存档、也不会变成一条真的笔画。这是"不甩墨"的第一道保险。
    /// </summary>
    private void FeedInkTrail(uint ptype, float radius, float screenX, float screenY)
    {
        if (ptype != Native.PT_PEN || !OverlayWindow.InkTrailEnabled) return;
        var win = WindowAt(screenX, screenY);
        if (win == null) return;

        int realCount = 0;
        if (_pen.Count > 0)
        {
            for (int i = 0; i < _pen.Count && realCount < _trailReal.Length; i++)
            {
                _trailReal[realCount] = new Vector2(_pen[i].X, _pen[i].Y);
                // 逐点半径 = 这一点的压力走**和干墨同一个映射**（见 PressureWidth）。
                _trailRadii[realCount] = PressureRadiusOf(_pen[i].Pressure);
                realCount++;
            }
        }
        if (realCount == 0)
        {
            _trailReal[realCount] = new Vector2(screenX, screenY);
            _trailRadii[realCount] = radius;
            realCount++;
        }

        int predCount = 0;
        if (PredictEnabled)
        {
            int n = _predictor.Predict(_predBuf);
            for (int i = 0; i < n && predCount < _trailPred.Length; i++)
                _trailPred[predCount++] = new Vector2(_predBuf[i].X, _predBuf[i].Y);
        }

        if (predCount > 0)
        {
            float lead = Vector2.Distance(_trailReal[realCount - 1], _trailPred[predCount - 1]);
            PredLeadSum += lead;
            PredLeadCount++;
            if (lead > PredLeadMax) PredLeadMax = lead;
            if (lead > _strokePredMax) _strokePredMax = lead;      // 这一笔自己的最大值
        }

        win.AddInkTrailPoints(_trailReal, realCount, _trailPred, predCount, radius, _trailRadii);

        // 系统合成器接手了这一笔的湿墨 → 把我们自己那份"渲染尾"收掉。
        // 不收就是两边一起补，笔尖前面会出现重复的一小截。
        ActiveStrokeOnTrail = true;
        ClearRenderTail();
    }

    /// <summary>一个压力值 → 湿墨半径（和干墨同一映射、同一单位）。</summary>
    private float PressureRadiusOf(float p)
    {
        var s = ActiveStroke;
        if (s == null) return 0f;
        if (!s.HasPressure || !PressureWidth.Enabled) return s.Width * 0.5f;
        return PressureWidth.HalfWidth(s.Width, p);
    }

    private void ApplyMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;

        // 选择方式是套索时走另一条判据（80% 在内 + 贴边无限延伸），见 ApplyLassoSelection。
        if (SelMode == SelectMode.Lasso) { ApplyLassoSelection(); return; }

        float l = MqMinX, t = MqMinY, r = MqMaxX, b = MqMaxY;
        if (r - l < 4 || b - t < 4)
        {
            // 这是一次**点击**，不是拖框。单击空白处就该取消选中。
            // 之前这里直接 return、什么都不做，用户得点两下才取消，很别扭。
            Doc.Selected.Clear();
            _dirty = true;
            return;
        }

        Doc.ApplyMarquee(new RectF { MinX = l, MinY = t, MaxX = r, MaxY = b });
        // **不在这里自动拆开**。原来这里调了 SplitErasedSelection()，被否掉了：
        // 拆成两个对象会让半透明荧光笔在"两截互相穿过"的地方**混合两次、颜色变深**——
        // 那等于"我什么都没擦，只是框选了一下，墨却变了"。原则是**擦除只是像素没了**：
        // 除了被擦掉的那一块，其他像素在任何操作前后都该一模一样。
        // 想单独摆弄某一截时用显式动作（Ctrl+Alt+8 拆开擦断的笔迹），见 RunAction。
        Console.WriteLine($"marquee selected {Doc.Selected.Count} strokes");
        _dirty = true;
    }

    /// <summary>
    /// 切换框选工具下"拖空白处"的方式：矩形框 ↔ 自由套索。
    ///
    /// **只切方式，不换工具**——用户按这个键的时候手上可能还拿着笔（正在写），
    /// 换工具会顺手把选中清掉（见 SwitchTool），那是他没要的副作用。
    /// 方式不落盘：和工具尺寸一样活在内存里，重开就是默认的矩形。
    /// </summary>
    private void ToggleSelectMode()
    {
        SetSelectMode(SelMode == SelectMode.Lasso ? SelectMode.Rect : SelectMode.Lasso);
    }

    /// <summary>
    /// 切到指定的选择方式。键盘那一路（Ctrl+Alt+9）和界面那一路
    /// （<see cref="SetSelectModeFromUi"/>）**都走这里**，免得两条路各写一套规则。
    /// </summary>
    private void SetSelectMode(SelectMode mode)
    {
        SelMode = mode;
        // **记住选择方式**（矩形 / 套索，用户 2026-09-30 定）：键盘和界面两条路都走这里，
        // 所以只在这一处写就够了；启动时读回来（见 Run 里那句 selMode）。
        SetUiPref("selMode", mode == SelectMode.Lasso ? "lasso" : "rect");
        // 半路切就把没画完的圈丢掉，免得下一次按下接在旧路径后面。
        LassoPath.Clear();
        MarqueeActive = false;
        Console.WriteLine(mode == SelectMode.Lasso
            ? "选择方式：自由套索（圈住 80% 就算选中；圈到屏幕边＝当作无限延伸）"
            : "选择方式：矩形框（碰到墨就算选中）");
        if (Tool != Tool.Marquee)
            Console.WriteLine("  （现在不是框选工具：按 Ctrl+Alt+5 换到框选才用得上）");
    }

    /// <summary>自检用：切一次选择方式（等同于按一下 Ctrl+Alt+9）。</summary>
    internal void ToggleSelectModeForTest() => ToggleSelectMode();

    /// <summary>
    /// 框选工具按下时起一个新手势。**抽出来是为了自检能走到同一段代码**
    /// （见 <see cref="MarqueeDragForTest"/>）：合成鼠标在锁屏 / 被别的程序占着捕获时
    /// 进不来，那几条自检只能记跳过——而这段接线恰恰是最容易写错的地方。
    /// 修饰键由调用方读键盘后传进来，判定函数保持纯逻辑。
    /// </summary>
    private void BeginMarqueeAt(float x, float y, bool shift, bool alt)
    {
        MarqueeActive = true;
        _mqAnchorX = x; _mqAnchorY = y;
        MqMinX = MqMaxX = x; MqMinY = MqMaxY = y;
        // 套索：路径从按下这一点开始；修饰键也在这一刻记下来（松手时用它决定加选/减选）。
        LassoPath.Clear();
        LassoLive = new Vector2(x, y);
        if (SelMode == SelectMode.Lasso) LassoPath.Add(LassoLive);
        _lassoAdditive = shift;
        _lassoSubtractive = alt;
    }

    /// <summary>
    /// 拖框中一路移动。
    ///
    /// 矩形：**锚点 ↔ 当前点**（四个方向都能拉，往回走框跟着缩回去）。
    /// 套索：抽稀后收进路径，同时记下指针当前位置给预览用（见 <see cref="LassoLive"/>）。
    /// </summary>
    private void ExtendMarqueeTo(float x, float y)
    {
        if (SelMode != SelectMode.Lasso)
        {
            // 矩形框 = 从锚点拉到当前点的那一个矩形。**不是**"扫过的最大范围"。
            MqMinX = MathF.Min(_mqAnchorX, x); MqMaxX = MathF.Max(_mqAnchorX, x);
            MqMinY = MathF.Min(_mqAnchorY, y); MqMaxY = MathF.Max(_mqAnchorY, y);
            return;
        }

        // 套索：间距 < 3 逻辑像素的点对判据没有贡献，只会把多边形从几十个顶点撑到几千个，
        // 而每条笔迹的"点在不在圈里"都要乘这个顶点数。
        LassoLive = new Vector2(x, y);
        if (LassoPath.Count == 0) { LassoPath.Add(LassoLive); }
        else
        {
            var last = LassoPath[LassoPath.Count - 1];
            if (Vector2.Distance(last, LassoLive) >= LassoStepLogical * DpiScale)
                LassoPath.Add(LassoLive);
        }

        // 脏区**只增不减**（和矩形那条路刻意不同）：已经画到屏幕上的那一段线，
        // 不能因为指针往回移就不重画——不重画就擦不掉，屏幕上会留下一截旧线。
        MqMinX = MathF.Min(MqMinX, x); MqMaxX = MathF.Max(MqMaxX, x);
        MqMinY = MathF.Min(MqMinY, y); MqMaxY = MathF.Max(MqMaxY, y);
    }

    /// <summary>截图取景框按下（和框选同一套锚点算法）。</summary>
    private void BeginCaptureAt(float x, float y)
    {
        CaptureActive = true;
        _capAnchorX = x; _capAnchorY = y;
        CapMinX = CapMaxX = x; CapMinY = CapMaxY = y;
    }

    /// <summary>截图取景框拖动：锚点 ↔ 当前点（往回拖要跟着缩，否则就是"不跟手"）。</summary>
    private void ExtendCaptureTo(float x, float y)
    {
        CapMinX = MathF.Min(_capAnchorX, x); CapMaxX = MathF.Max(_capAnchorX, x);
        CapMinY = MathF.Min(_capAnchorY, y); CapMaxY = MathF.Max(_capAnchorY, y);
    }

    /// <summary>
    /// 自检用：只驱动截图取景框的"按下 → 拖"，**不真的抓屏**（抓屏要藏窗口，
    /// 还会把这一帧的测试环境弄乱）。取景框和框选框用的是同一套锚点算法。
    /// </summary>
    internal void CaptureFrameDragForTest(float ax, float ay, float x1, float y1, float x2 = float.NaN, float y2 = float.NaN)
    {
        BeginCaptureAt(ax, ay);
        ExtendCaptureTo(x1, y1);
        if (!float.IsNaN(x2)) ExtendCaptureTo(x2, y2);
    }

    /// <summary>
    /// 自检用：把一条路径按"按下 → 一路移动 → 松手"**真实跑一遍**——走的是和鼠标
    /// 完全相同的三个入口（<see cref="BeginMarqueeAt"/> / <see cref="ExtendMarqueeTo"/> /
    /// <see cref="ApplyMarquee"/>），只是不经过操作系统的消息队列。
    /// </summary>
    internal void MarqueeDragForTest(IReadOnlyList<Vector2> path,
                                     bool shift = false, bool alt = false)
    {
        if (path == null || path.Count == 0) return;
        BeginMarqueeAt(path[0].X, path[0].Y, shift, alt);
        for (int i = 1; i < path.Count; i++) ExtendMarqueeTo(path[i].X, path[i].Y);
        ApplyMarquee();               // 松手：按 SelMode 走矩形或套索
    }

    /// <summary>
    /// 套索松手。判据在 <see cref="InkDocument.ApplyLasso"/>（80% 在内 + 贴边无限延伸），
    /// 这里只管三件事：路径太短就当**单击空白**（取消选中，和框选一致）、
    /// 修饰键加减选、清掉路径。
    /// </summary>
    private void ApplyLassoSelection()
    {
        if (LassoPath.Count < 3 || (MqMaxX - MqMinX) < 4f || (MqMaxY - MqMinY) < 4f)
        {
            // 点一下空白就想"什么都别选"——和框选那条同样的处理（见 ApplyMarquee）。
            Doc.Selected.Clear();
            CopyDragArmed = false;
            LassoPath.Clear();
            _dirty = true;
            return;
        }

        int n = Doc.ApplyLasso(LassoPath, ViewportCanvas, LassoEdgeSnapLogical * DpiScale,
                               additive: _lassoAdditive, subtractive: _lassoSubtractive);
        Console.WriteLine(_lassoSubtractive
            ? $"lasso removed {n} strokes（剩 {Doc.Selected.Count} 条）"
            : $"lasso selected {n} strokes（圈 {LassoPath.Count} 个点"
              + (_lassoAdditive ? $"，Shift 加选，共 {Doc.Selected.Count} 条）" : "）"));
        LassoPath.Clear();
        _dirty = true;
    }

    /// <summary>
    /// "圈到屏幕边"的判定容差（逻辑像素）：指针离可见区域边线这么近就当作贴边，
    /// 那一段按无限延伸处理（见 <see cref="InkDocument.ApplyLasso"/>）。
    ///
    /// 取 4：指针能被拖到窗口外（有捕获），所以贴边基本都能满足；取太大又会让
    /// "离边还有一点点"的圈也偷偷延伸出去，把屏幕外的墨一起圈进来。
    /// </summary>
    internal const float LassoEdgeSnapLogical = 4f;

    /// <summary>
    /// 读一条指针消息，顺便把两个时标记下来（延时探针用）：
    ///   pi.PerformanceCount —— 系统给的硬件/驱动时标（QPC 计数）
    ///   现在                —— 我们真正拿到它的时刻
    /// 两者之差 = 输入栈 + 我们自己的消息队列。**这一段里只有队列是我们的责任**，
    /// 而队列拖延的直接原因就是 Present(1) 把线程卡在垂直同步里。
    /// </summary>
    private bool ReadPointer(uint id, out float x, out float y, out float pressure,
                             out bool inverted, out uint pointerType)
    {
        x = y = 0; pressure = 0.5f; inverted = false; pointerType = 0;
        if (!Native.GetPointerInfo(id, out var pi)) return false;
        _lastInputPerfQpc = pi.PerformanceCount;
        _lastInputMsgQpc = Qpc.Now;
        if (LatencyRecording && _prevInputMsgQpc != 0)
        {
            double gap = Qpc.Ms(_prevInputMsgQpc, _lastInputMsgQpc);
            if (gap > 0 && gap < 500) Latency.AddInputGap(gap);
        }
        _prevInputMsgQpc = _lastInputMsgQpc;
        _framePoints++;
        x = pi.ptPixelLocationX;
        y = pi.ptPixelLocationY;
        pointerType = pi.pointerType;
        // 笔的"出身"（笔尖在不在屏幕上）：**手写板和触屏笔的落点规则不同**，这条必须
        // 每条笔消息都确认一遍（换设备/换笔会变），结果按设备句柄缓存、只查一次。
        // 鼠标/触摸不查（它们的路不需要这个信息）。
        if (pi.pointerType == Native.PT_PEN)
            PenDeviceOnScreen = QueryPenOnScreen(pi.sourceDevice);
        if (pi.pointerType == Native.PT_PEN && Native.GetPointerPenInfo(id, out var pen))
        {
            pressure = pen.pressure / 1024f;
            if (pressure <= 0.01f) pressure = 0.5f;
            inverted = (pen.penFlags & (Native.PEN_FLAG_INVERTED | Native.PEN_FLAG_ERASER)) != 0;
        }
        // 留给光标那一族用（EffectiveTool）：**每条消息都写**，鼠标 / 触摸自然落回 false。
        // 光标的形状必须在"悬停"（还没落笔）时就对，所以不能等 OnPointerDown 里那个局部变量。
        LastPointerInverted = inverted;
        return true;
    }

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
        switch (action)
        {
            case KeyAction.TogglePassThrough: SetPassThrough(!PassThrough); break;
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
            case KeyAction.ToolCapture: SwitchTool(Tool.Capture); break;
            case KeyAction.ToolMarquee: SwitchTool(Tool.Marquee); break;
            // 图形**没有键位动作**（用户 2026-09-19 定：图形通通不要快捷键），
            // 换种类只有面板上带那一条路（`FullUi.ActivateSegment` → `SwitchTool`）。
            case KeyAction.SelectShape: ToggleSelectMode(); break;
            case KeyAction.Undo: Doc.Undo(); Laser.Clear(); break;
            case KeyAction.Redo: Doc.Redo(); break;
            case KeyAction.Copy: CopySelectionToClipboard(); break;
            case KeyAction.Clear: Doc.Clear(); Laser.Clear(); break;
            case KeyAction.ToggleHud: ShowHud = !ShowHud; break;
            case KeyAction.CycleWidth: CycleWidth(); break;
            case KeyAction.ToggleKeyboardMode: SetKeyboardMode(!KeyboardMode); break;
            case KeyAction.Quit: _quit = true; break;

            case KeyAction.SelectAll: SelectAll(); break;
            case KeyAction.Duplicate: Doc.DuplicateSelected(); break;
            case KeyAction.DeleteSelected: Doc.DeleteSelected(); break;
            case KeyAction.CancelSelection: Doc.Selected.Clear(); break;
            case KeyAction.Paste: PasteFromClipboard(); break;
            case KeyAction.NudgeLeft: Nudge(-1f, 0f); break;
            case KeyAction.NudgeUp: Nudge(0f, -1f); break;
            case KeyAction.NudgeRight: Nudge(1f, 0f); break;
            case KeyAction.NudgeDown: Nudge(0f, 1f); break;
            case KeyAction.NudgeLeftFar: Nudge(-10f, 0f); break;
            case KeyAction.NudgeUpFar: Nudge(0f, -10f); break;
            case KeyAction.NudgeRightFar: Nudge(10f, 0f); break;
            case KeyAction.NudgeDownFar: Nudge(0f, 10f); break;
            case KeyAction.FlipPageUp: FlipPageFromUi(false); break;
            case KeyAction.FlipPageDown: FlipPageFromUi(true); break;
        }
    }

    /// <summary>
    /// 方向键微调。**已知待改**：按住方向键会重复触发，每次都是一条撤销记录；
    /// 要接"连续微调合并成一步"，得等编辑命令支持合并。
    /// </summary>
    private void Nudge(float dx, float dy)
    {
        Doc.ApplyTransform(Matrix3x2.CreateTranslation(dx, dy));
        _dirty = true;
    }

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
        Tool = t;
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
        CopyDragArmed = false;
        // 选区没了，"画完自动选中那个框还收没收着"也就没有意义了——归零，
        // 免得下一个自动选中的框刚开始就带着上一轮的展开状态（见 `_autoSelCollapsed`）。
        _autoSelCollapsed = false;
        // **停顿成型那一路的"选中"也一起归零**（见 `_dwellSelected`）：它比 `_autoSelCollapsed`
        // 多担一件事——放行"笔下面那个框能不能点"，所以更不能跟着上一轮留到下一轮。
        _dwellSelected = false;
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
        // **记住粗细档**（用户 2026-09-30 定：笔 / 荧光笔 / 激光笔 / 面积擦 分开记）
        SetUiPref("w.pen", WidthPresetIndex.ToString());
        SetUiPref("w.hl", HighlighterWidthIndex.ToString());
        SetUiPref("w.laser", LaserWidthIndex.ToString());
        SetUiPref("w.pixel", PixelEraserWidthIndex.ToString());
        NotifyUiStateChanged();
    }

    private void SetPassThrough(bool on)
    {
        PassThrough = on;
        // 穿透打开/关掉时把激光轨迹清掉（原来是把 `Visible` 置假，等价于"立刻全没"）。
        // ⚠ 别只隐藏不清：轨迹会一直留在集合里，`Laser.Visible` 仍为真 →
        //    每一帧都出一帧（白烧 CPU），而且下次一进来它们又冒出来。
        if (!on) Laser.Clear();

        // **穿透和白板互斥**（用户 2026-09-17 问的那条）。两个方向都要挡：
        //   · 开白板 → 关穿透：白板是不透明的一层，穿透是"点击落到下层程序"；
        //     两个一起开着，老师看到的是白板、点到的却是白板下面那个看不见的窗口。
        //   · 开穿透 → 关白板：同上，反过来也一样说不通。
        // 关掉的那一方**不自动回来**（和"关板不自动开穿透"一致）：老师再点一下就行，
        // 而"悄悄替你恢复"才是难查的那类行为。
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

        // **穿透关掉 = 回到"能批注"的状态 → 必须把键盘/前台要回来**。
        // 用户 2026-09-30 复现的真 bug：穿透开开关关几次之后，Ctrl+P 这些应用内快捷键
        // 就彻底死了——因为穿透期间我们的窗口不是前台（样式里也不让它被激活），
        // 关掉穿透时只恢复了样式、**没人把前台还给我们**，于是按键全被别的窗口收走。
        // `SetKeyboardMode` 里那句 SetForegroundWindow 正是干这个的（幂等，重复调没副作用）。
        // ⚠ 和"退出放映要把前台要回来"是同一类补丁，见 Ppt.ExitPptMode——以后凡是
        //   "从别的状态切回批注态"的地方都要做这一步。
        if (!on && _windows.Count > 0) SetKeyboardMode(KeyboardMode);

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
        foreach (var w in _windows)
        {
            Native.KillTimer(w.Hwnd, (IntPtr)1);
            for (int i = 1; i <= _hotkeyActions.Count + 2; i++) Native.UnregisterHotKey(w.Hwnd, i);
            w.Dispose();
        }
        // 只写"改过的"键位和"界面改过的"偏好；都没动过就不碰用户的配置文件。
        FlushSettings();

        // 退出前补一次自动存档（下一次打开接上）。自检模式不动用户的存档。
        if (Doc.Version != _autoSavedVersion) AutoSaveNow();

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

        _windows.Clear();
        s_map.Clear();
        Cursors.DisposeAll();
        Gfx.Shutdown();
        // 手测台：退出时把汇总写出来（逐条数据在每条拖拽结束时就已经落盘了）。
        EraserTelemetry?.Close(Doc, NowMs);
        Console.WriteLine("shutdown complete");
    }

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
        // 界面看到的屏幕坐标是逻辑像素：它按自己的逻辑尺寸布局，引擎负责按 DPI 放大。
        Host = new UiHost(this, LogicalVirtualScreen, DpiScale, keepScreen);
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
        Color = Tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
        PaletteBase = Tool == Tool.Highlighter
            ? new Color4(HighlighterCurrent.R, HighlighterCurrent.G, HighlighterCurrent.B, 1f)
            : CurrentColor,
        Width = CurrentToolWidthLogical,
        // 按工具分开给：只给一个 Width 的话，滑块在切工具时会跳（三个工具各记各的宽度）。
        PenWidth = PenWidthLogical,
        HighlighterWidth = HighlighterWidthLogical,
        HighlighterColor = HighlighterCurrent,
        LaserWidth = LaserWidthLogical,
        Dash = PenDash,
        PassThrough = PassThrough,
        Board = BoardOn,
        BoardColor = BoardColor,
        BoardPattern = BoardPattern,
        BoardPatternStep = BoardPatternStepLogical,
        BoardOpacity = BoardOpacity,
        CaptureHideInk = CaptureHideInk,
        SelectMode = SelMode,
        CoordGridDefault = CoordGridDefault,
        DwellShapeOn = DwellShapeEnabled,
        ScreenIndex = ScreenIndex,
        CanFlipPageUp = CanFlipPageUp,
        IsDrawing = _drawing,
        UndoDepth = Doc.UndoDepth,
        RedoDepth = Doc.RedoDepth,
        StrokeCount = Doc.Strokes.Count,
        UpdateStage = UpdateState,
        UpdateText = UpdateText,
    };

    private void NotifyUiStateChanged()
    {
        if (Ui == null) return;
        var snapshot = SnapshotState();
        UiGuard("OnStateChanged", () => Ui.OnStateChanged(snapshot));
    }

    internal void SetToolFromUi(Tool tool)
    {
        SwitchTool(tool);
        EraserTelemetry?.Note($"界面换工具 → {ToolName(tool)}", NowMs);
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

    /// <summary>自检用：拖动中移动指针（选择手势）。</summary>
    internal void UpdateSelectionGestureForTest(float x, float y) => UpdateSelDrag(x, y);

    internal void SetColorFromUi(Color4 color)
    {
        // 荧光笔用同一组基色，只是降低不透明度——老师看到的色相是一致的。
        if (Tool == Tool.Highlighter)
            HighlighterCurrent = InkPalette.ToHighlighter(color);
        else
            CurrentColor = color;
        // **记住这个颜色**（用户 2026-09-30 定：每个工具各记一个色，重启回来还是它）。
        // 存的是**色带里的序号**而不是 RGB：色带以后微调颜色值，存档也不用跟着迁。
        var band = Tool == Tool.Highlighter ? InkPalette.HighlighterBand : InkPalette.PenBand;
        for (int i = 0; i < band.Length; i++)
            if (CloseColor(band[i].Color, color))
            {
                if (Tool == Tool.Highlighter) _hlColorIdx = i; else _penColorIdx = i;
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
                HighlighterWidthLogical = v;
                SetUiPref("wv.hl", v.ToString("0.##"));
                int hi = Array.IndexOf(HighlighterWidthPresets, v);
                if (hi >= 0) HighlighterWidthIndex = hi;
                break;

            case Tool.Laser:
                LaserWidthLogical = v;
                int li = Array.IndexOf(LaserWidthPresets, v);
                if (li >= 0) LaserWidthIndex = li;
                break;

            case Tool.PixelEraser:
                // 像素橡皮改的是**那一块橡皮的横边**（高 = 横边 × 黄金比）。
                // 它的范围比笔宽大得多（一块橡皮 30～160 逻辑像素），
                // 所以这里单独夹一次，不跟笔共用那个 64 的上限。
                PixelEraserWidthLogical = Math.Clamp(v, PixelEraserMinWidth, PixelEraserMaxWidth);
                int pi = Array.IndexOf(PixelEraserWidthPresets, PixelEraserWidthLogical);
                if (pi >= 0) PixelEraserWidthIndex = pi;
                break;

            case Tool.Eraser:
                // 整笔橡皮改的是**落点半径**（碰到哪儿就删哪一条）
                EraserRadiusLogical = Math.Clamp(v, EraserRadiusMin, EraserRadiusMax);
                int ei = Array.IndexOf(EraserRadiusPresets, EraserRadiusLogical);
                if (ei >= 0) EraserRadiusIndex = ei;
                break;

            default:
                PenWidthLogical = v;
                SetUiPref("wv.pen", v.ToString("0.##"));
                int idx = Array.IndexOf(WidthPresets, v);
                if (idx >= 0) WidthPresetIndex = idx;
                break;
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void UndoFromUi()
    {
        Doc.Undo();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void RedoFromUi()
    {
        Doc.Redo();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void ClearFromUi()
    {
        Doc.Clear();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
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
    // =====================================================================
    //  自动更新（2026-09-29）
    // =====================================================================
    //
    // 分工：**网络全在后台线程**，主线程只做两件事——把结果变成状态文字、
    // 以及（下完之后）拉起换壳脚本然后退出自己。理由：主循环还扛着渲染和输入，
    // 让它在十几秒的下载里卡住是不能接受的。
    //
    // 状态字段由**主线程**写（界面在读），后台线程只往 `_updResult` / `_updZipPath`
    // 里放结果、再把 `_updPost` / `_updApplyPosted` 立起来。主循环每帧 `PumpUpdate()` 收一次。

    /// <summary>自动更新的状态（界面那一行显示什么、点了做什么，都看它）。</summary>
    internal UpdateStage UpdateState = UpdateStage.NotConfigured;
    /// <summary>自动更新的一行状态文字。</summary>
    internal string UpdateText = "未配置更新源";

    private float UpdateProgress;                       // 0..1（下载中，只给界面看）
    private string _updVersion = "", _updNotes = "", _updZipUrl = "", _updSha = "";
    private volatile bool _updPost;                     // 后台：检查结果放好了
    private volatile bool _updApplyPosted;              // 后台：下载结束了
    private volatile bool _updBusy;                     // 有后台任务在跑（别叠加）
    /// <summary>
    /// 自检/验收用：查到新版本就**自动继续下载安装**（不用等人再点一下）。
    /// 产品里永远是 false（用户点两下：一下查、一下装）。
    /// </summary>
    internal bool AutoApplyUpdate;
    /// <summary>
    /// 自检/验收用：`--updatecheck` 只查不装时，**查完就自己退出**
    /// （产品里没有这个开关——用户点一下查、再点一下装，界面当然要留着）。
    /// </summary>
    internal bool AutoCheckOnly;
    private readonly object _updLock = new();
    private (UpdateFeed.Manifest m, string err) _updResult;
    private string _updUsedUrl = "";
    private string _updZipPath = "", _updError = "";
    private long _updGot, _updTotal;                    // 下载进度（后台写、主线程读）

    /// <summary>「检查更新」被点了一下（见 <see cref="IEngineCommands.CheckUpdate"/>）。</summary>
    internal void CheckUpdateFromUi()
    {
        if (!UpdateFeed.HasAnySource)
        {
            // **没配更新源是正常状态，不是错误**（教室机器可以指到局域网共享；
            // 一条都没有时点「检查更新」只改一行状态文字）
            UpdateState = UpdateStage.NotConfigured;
            UpdateText = "未配置更新源";
            NotifyUiStateChanged();
            Console.WriteLine("自动更新：没有可用的更新源（settings.json 的 update.url，或 UpdateFeed.Sources）");
            return;
        }
        if (_updBusy) return;                           // 已经在查 / 在下，别叠加

        UpdateState = UpdateStage.Checking;
        UpdateText = "检查中…";
        NotifyUiStateChanged();

        Console.WriteLine($"自动更新：检查（当前 {UpdateFeed.CurrentVersion}；"
                          + (UpdateFeed.Url.Length > 0 ? "用户配置源" : $"候选 {UpdateFeed.Sources.Length} 条，按序试")
                          + (UpdateFeed.Url.Length > 0 ? "" : "；加速站直连、GitHub 那条走系统代理") + "）");
        _updBusy = true;
        var th = new System.Threading.Thread(() =>
        {
            var m = UpdateFeed.FetchBest(UpdateFeed.CurrentVersion, out string used, out string err);
            lock (_updLock)
            {
                _updResult = (m, err);
                _updUsedUrl = used;
            }
            _updPost = true;
        })
        { IsBackground = true, Name = "InkTeach-Update-Check" };
        th.Start();
    }

    /// <summary>已经查到新版本了，再点一下：**下载 → 校验 → 换壳重启**。</summary>
    internal void ApplyUpdateFromUi()
    {
        if (_updBusy || UpdateState != UpdateStage.Available || _updZipUrl.Length == 0) return;

        UpdateState = UpdateStage.Downloading;
        UpdateProgress = 0f;
        UpdateText = "下载中 0%";
        NotifyUiStateChanged();

        string url = _updZipUrl, sha = _updSha, ver = _updVersion;
        Console.WriteLine($"自动更新：开始下载 {ver} → {url}");
        _updBusy = true;
        var th = new System.Threading.Thread(() =>
        {
            string dir = UpdateFeed.UpdateDirFor(ver);
            string zip = Path.Combine(dir, $"InkTeach-{UpdateFeed.SafeVer(ver)}-win-x64.zip");
            bool ok = UpdateFeed.Download(url, zip, sha,
                (got, total) =>
                {
                    System.Threading.Interlocked.Exchange(ref _updGot, got);
                    System.Threading.Interlocked.Exchange(ref _updTotal, total);
                }, out string err);
            lock (_updLock)
            {
                _updZipPath = ok ? zip : "";
                _updError = err;
            }
            _updApplyPosted = true;
        })
        { IsBackground = true, Name = "InkTeach-Update-Download" };
        th.Start();
    }

    /// <summary>主循环每帧叫一次：把后台结果搬成状态；下完了就拉换壳脚本并退出自己。**只在主线程跑。**</summary>
    private void PumpUpdate()
    {
        if (UpdateState == UpdateStage.Downloading)
        {
            long got = System.Threading.Interlocked.Read(ref _updGot);
            long total = System.Threading.Interlocked.Read(ref _updTotal);
            float p = total > 0 ? Math.Clamp(got / (float)total, 0f, 1f) : 0f;
            if (p - UpdateProgress > 0.01f)
            {
                UpdateProgress = p;
                UpdateText = $"下载中 {p * 100:F0}%";
                NotifyUiStateChanged();
            }
        }

        if (_updPost)
        {
            _updPost = false;
            _updBusy = false;
            UpdateFeed.Manifest m;
            string err;
            lock (_updLock) (m, err) = _updResult;

            bool needApply = false;
            if (m == null)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "检查失败，再点重试";
                Console.WriteLine("自动更新：检查失败：" + err);
            }
            else if (UpdateFeed.CompareVersions(m.Version, UpdateFeed.CurrentVersion) <= 0)
            {
                UpdateState = UpdateStage.UpToDate;
                UpdateText = "已是最新";
                Console.WriteLine($"自动更新：已是最新（{UpdateFeed.CurrentVersion}；源 {UpdateFeed.HostOf(_updUsedUrl)}）");
            }
            else if (m.Url.Length == 0 || m.Sha256.Length == 0)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "清单不完整";
                Console.WriteLine($"自动更新：清单里 {m.Version} 缺 url 或 sha256，拒绝");
            }
            else
            {
                _updVersion = m.Version;
                _updNotes = m.Notes;
                _updZipUrl = m.Url;
                _updSha = m.Sha256;
                UpdateState = UpdateStage.Available;
                UpdateText = $"有新版本 {m.Version}";
                Console.WriteLine($"自动更新：发现 {m.Version}（当前 {UpdateFeed.CurrentVersion}；"
                                  + $"源 {UpdateFeed.HostOf(_updUsedUrl)}）"
                                  + (m.Notes.Length > 0 ? "：" + Shorten(m.Notes) : ""));
                needApply = AutoApplyUpdate;
            }
            NotifyUiStateChanged();

            // `--updatecheck`（验收用）：**查完就退**，别把界面挂在屏幕上。
            // 产品里没有这个开关（用户点一下查、再点一下装，界面当然要留着）。
            if (AutoCheckOnly) { _quit = true; return; }
            if (needApply) ApplyUpdateFromUi();
        }

        if (_updApplyPosted)
        {
            _updApplyPosted = false;
            _updBusy = false;
            string zip, err;
            lock (_updLock) { zip = _updZipPath; err = _updError; }

            if (zip.Length == 0)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "下载失败，再点重试";
                Console.WriteLine("自动更新：下载失败：" + err + "（软件保持原样）");
                NotifyUiStateChanged();
                return;
            }

            // 换壳：脚本**等我们退出之后**才动文件（见 UpdateFeed.LaunchSwap）。
            // 拉不起来就留在原地——"更新没装成、软件也没了"是最坏的结果。
            string exe = Environment.ProcessPath;
            string appDir = Path.GetDirectoryName(exe);
            try
            {
                string script = UpdateFeed.WriteSwapScript(Path.GetDirectoryName(zip));
                if (UpdateFeed.LaunchSwap(script, appDir, zip, exe, out string lerr))
                {
                    UpdateState = UpdateStage.Ready;
                    UpdateText = "正在重启…";
                    Console.WriteLine($"自动更新：{_updVersion} 已下载并校验，换壳脚本已拉起，本进程退出");
                    NotifyUiStateChanged();
                    _quit = true;                       // 退出 → 脚本接手：改名旧目录、解压、重启
                }
                else
                {
                    UpdateState = UpdateStage.Failed;
                    UpdateText = "换壳失败（保持原样）";
                    Console.WriteLine("自动更新：换壳启动失败：" + lerr);
                    NotifyUiStateChanged();
                }
            }
            catch (Exception ex)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "换壳失败（保持原样）";
                Console.WriteLine("自动更新：换壳失败：" + ex.Message);
                NotifyUiStateChanged();
            }
        }
    }

    /// <summary>状态文字要能塞进面板那一行，长的截掉（换行会让行高乱掉）。</summary>
    private static string Shorten(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= 60 ? s : s[..60] + "…";
    }

    internal void RestartFromUi()
    {
        Recovery.DiscardSession();   // 保证新进程是空白：不写，而且删掉上次的残留
        AutoSaveNow();               // 盘上留一份（不受"下次打开要不要接上"那个偏好影响）

        if (!RestartSelf("界面上的重启"))
        {
            Console.WriteLine("重启没成功，继续用（板书还在）");
            return;
        }
        RestartRequested = true;
        _quit = true;
    }

    internal void SetPassThroughFromUi(bool on)
    {
        SetPassThrough(on);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 开关白板。底色一变，整个内容层都要重画——缓存里那张图是按旧底色画的。
    /// </summary>
    internal void SetBoardFromUi(bool on)
    {
        if (BoardOn == on) return;
        BoardOn = on;
        // 开白板就顺手关掉穿透（另一边在 SetPassThrough 里，两个方向都挡，理由见那儿）
        if (on) SetPassThrough(false);
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 装界面，**并留下"再造一个"的办法**。产品代码用这一条，不要用 <see cref="SetUi"/>：
    /// 界面出问题时引擎要能自己把界面重建回来（教室大屏很可能没有键盘，
    /// 界面是唯一的出口，见 <see cref="Recovery"/> 的注释）。
    ///
    /// 传进来的工厂必须每次都能造一个**全新的**界面（不要复用同一个实例——
    /// 那个实例正是刚被判定"状态坏了"的那一个）。
    /// </summary>
    public void SetUiFactory(Func<IOverlayUi> factory)
    {
        _uiFactory = factory;
        SetUi(factory != null ? factory() : null);
    }

    /// <summary>界面切选择方式（矩形框 / 自由套索）。</summary>
    internal void SetSelectModeFromUi(SelectMode mode)
    {
        if (SelMode == mode) return;
        SetSelectMode(mode);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 界面换板色。底色一变整个内容层都要重画——缓存里那张图是按旧底色画的
    /// （和 <see cref="SetBoardFromUi"/> 同理）。
    /// </summary>
    internal void SetBoardColorFromUi(Color4 color)
    {
        if (BoardColor.Equals(color)) return;
        BoardColor = color;
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>界面上的"全选"。<see cref="SelectAll"/> 自己会把工具切成框选，免得用户以为没生效。</summary>
    internal void SelectAllFromUi()
    {
        SelectAll();
        _dirty = true;
    }

    /// <summary>界面上的"上一屏 / 下一屏"（整屏翻页）。</summary>
    internal void FlipPageFromUi(bool down)
    {
        if (!FlipPage(down)) return;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 指针按下的第一站：先问界面。返回 true 表示这次输入归界面（比如按到了
    /// 悬浮条上的按钮），引擎不再把它变成笔画。
    /// </summary>
    private bool UiPointerDown(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (!UiVisibleNow) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        if (!UiGuard("PointerDown", () => Ui.PointerDown(e), false)) return false;
        UiCapturing = true;
        return true;
    }

    private bool UiPointerMove(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (!UiVisibleNow) return false;

        // 只有在界面已经捕获输入或指针落在界面矩形内时才转发，避免没必要的调用。
        if (!UiCapturing && !UiContains(x, y)) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        return UiGuard("PointerMove", () => Ui.PointerMove(e), false);
    }

    private bool UiPointerUp(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (!UiVisibleNow || !UiCapturing) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        bool consumed = UiGuard("PointerUp", () => Ui.PointerUp(e), false);
        UiCapturing = false;
        return consumed;
    }

    private bool UiContains(float x, float y)
    {
        x /= DpiScale; y /= DpiScale;   // 物理 → 逻辑
        foreach (var w in _windows)
            if (w.UiBoundsLogicalContains(x, y)) return true;
        return false;
    }

    /// <summary>
    /// 取 WM_NCHITTEST 的 lParam 里的屏幕坐标。
    ///
    /// 它是两个**有符号** 16 位拼起来的**物理**像素坐标。必须按有符号取：
    /// 副屏在主屏左边/上边时坐标是负的，按无符号取会得到 6 万多，
    /// 贴边、命中测试全错——而单屏 + 正坐标的机器上完全看不出来。
    /// </summary>
    private static void HitTestPoint(IntPtr lParam, out float x, out float y)
    {
        long v = lParam.ToInt64();
        x = (short)(v & 0xFFFF);
        y = (short)((v >> 16) & 0xFFFF);
    }

    // ---- 面板的"接输入小窗"（方案 B）--------------------------------------

    // ---- 界面回调的统一入口：**都是防弹的** ------------------------------
    //
    // 为什么值得包这一层：界面里的一个 bug 不该让"板书"这个主功能跟着不可用。
    // 以前的保护只有绘制那一段（`Overlay.DrawUi` 里的 try/catch 打印），
    // 而 `PointerDown/Move/Up`、`OnStateChanged`、`QueryBounds`、`IsAnimating`
    // 全是裸调——界面一抛，异常就冒到消息循环里，老师当场什么都画不出来。
    //
    // 规矩：**3 秒内 3 次**异常就把界面停用（换成 NullUi，并藏掉接输入小窗），
    // 笔迹照常。
    //
    // 为什么是"一个时间窗内 3 次"而不是"连续 3 次调用"：界面每帧都会被调好几次
    // （Visible / QueryBounds / Render / PointerMove…），只要别处的调用成功就把
    // 计数清零的话，一条只在 PointerDown 里踩的 bug 永远攒不到 3 次。
    // 反过来"一次就停用"又太狠——偶发一次不该让工具条消失一整节课。
    private int _uiFaults;
    private double _uiLastFaultMs = double.NegativeInfinity;

    /// <summary>界面工厂：有它引擎才能"自己把界面重建一个回来"。</summary>
    private Func<IOverlayUi> _uiFactory;

    /// <summary>最近一分钟里重建过几次界面。重建也失败就得往上走一级（重启软件）。</summary>
    private int _uiRebuilds;
    private double _uiRebuildWindowStartMs = double.NegativeInfinity;

    /// <summary>自检用：这一次"重启软件"有没有真的被发起。</summary>
    internal bool RestartRequested { get; private set; }

    /// <summary>重启计数清过一次就够了（跑满一分钟算"稳住了"）。</summary>
    private bool _restartCountCleared;

    /// <summary>
    /// 界面的异常次数与处置。**三级阶梯**，一级比一级重，但都不留死局：
    ///
    ///   ① 重建界面（引擎自己 new 一个回来）—— 板书不动，工具条重新挂一遍；
    ///   ② 重启软件 —— 重启前把板书暂存下来，重启后读回来，**什么都不丢**；
    ///   ③ 都不行就退回无界面 —— 至少还能写，而且是必现 bug 时的唯一出路
    ///      （否则就是无限重启，屏幕一直在闪，比停用更糟）。
    ///
    /// 为什么必须往"重启"走：教室的大屏 + 手写板机器上很可能没有键盘，
    /// 界面就是唯一的出口；界面没了又没法重启，就是死局（用户 2026-09-16 提的）。
    /// </summary>
    private void NoteUiFault(string what, Exception ex)
    {
        if (NowMs - _uiLastFaultMs > 3000) _uiFaults = 0;   // 隔久了当新的一轮
        _uiLastFaultMs = NowMs;
        _uiFaults++;
        if (_uiFaults < 3)
        {
            Console.WriteLine($"界面异常（{what}，第 {_uiFaults} 次）：{ex.Message}");
            return;
        }

        Console.WriteLine($"界面 3 秒内抛了 {_uiFaults} 次异常（最后一次在 {what}）：{ex.Message}");

        // ① 还能重建就先重建：不丢板书，通常也够用（界面多半是状态被搞坏了）。
        if (_uiFactory != null && UiRebuildAllowed())
        {
            RebuildUi(what);
            return;
        }

        // ② 重建也救不回来 → 重启软件（先把板书存下来）。
        if (TryRestartForUiProblem(what)) return;

        // ③ 最后的兜底：退回无界面。必现的 bug 走到这里，至少还能写。
        DisableUi(what);
    }

    private void RebuildUi(string what)
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) { _uiRebuilds = 0; _uiRebuildWindowStartMs = NowMs; }
        _uiRebuilds++;
        _uiFaults = 0;

        Console.WriteLine($"  → 重建界面（这个界面是第 {_uiRebuilds} 次重建）—— 板书不受影响");
        try
        {
            SetUi(_uiFactory());
        }
        catch (Exception ex)
        {
            Console.WriteLine("界面重建失败：" + ex.Message);
            SetUi(null);
        }
    }

    /// <summary>一分钟内最多重建两次；再多说明重建不管用，该往上走一级。</summary>
    private bool UiRebuildAllowed()
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) return true;
        return _uiRebuilds < 2;
    }

    /// <summary>
    /// 因为界面出问题而重启软件。返回 true = 已经开始重启（调用方别再往下走）。
    /// **先存板书再重启**：重启的前提是不丢东西。
    /// </summary>
    private bool TryRestartForUiProblem(string what)
    {
        int n = Recovery.NoteRestart();
        if (n > Recovery.MaxRestartsInWindow)
        {
            Console.WriteLine($"  → 这个窗口内已经重启过 {n - 1} 次，不再重启（多半是必现的问题）");
            return false;
        }

        try { Recovery.SaveSession(InkSerializer.Save(Doc)); } catch (Exception ex) { Console.WriteLine("板书暂存失败：" + ex.Message); }
        Console.WriteLine($"  → 重启软件（第 {n} 次），板书已暂存，重启后自动读回来");

        if (!RestartSelf(what)) return false;
        RestartRequested = true;
        _quit = true;           // 主循环退出 → Shutdown → 进程结束，新进程接手
        return true;
    }

    /// <summary>
    /// 真的把软件重新拉起来。**宿主可以覆写**（自检里只记一笔，不真拉进程）。
    /// 返回 false = 没拉起来，那就别退出——退回无界面继续跑总比"退出后什么都没有"强。
    /// </summary>
    protected virtual bool RestartSelf(string why)
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("拉起新进程失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>三级都走不通时的兜底：停用界面，笔迹照常。</summary>
    private void DisableUi(string what)
    {
        Console.WriteLine($"  → 界面已停用（{what}）。笔迹不受影响；修好之后重开软件就回来了。");
        Ui = new NullUi();
        UiCapturing = false;
        Native.ReleaseCapture();
        _uiHover = false;
        if (_uiInputHwnd != IntPtr.Zero && _uiInputShown)
        {
            Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
            _uiInputShown = false;
        }
        _dirty = true;
    }

    private T UiGuard<T>(string what, Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception ex) { NoteUiFault(what, ex); return fallback; }
    }

    private void UiGuard(string what, Action call)
    {
        try { call(); }
        catch (Exception ex) { NoteUiFault(what, ex); }
    }

    internal bool UiVisibleNow => Ui != null && UiGuard("Visible", () => Ui.Visible, false);
    internal RectF UiQueryBoundsNow() =>
        Ui == null ? RectF.Empty : UiGuard("QueryBounds", () => Ui.QueryBounds(), RectF.Empty);
    /// <summary>界面声明"我会画到占用矩形外面一圈"（投影/浮出预览），见 IOverlayUi.PaintMargin。</summary>
    internal float UiPaintMarginNow =>
        Ui == null ? 0f : UiGuard("PaintMargin", () => Ui.PaintMargin, 0f);
    internal bool UiIsAnimatingNow => Ui != null && UiGuard("IsAnimating", () => Ui.IsAnimating, false);
    internal RectF UiLayoutNow(RectF screen, float dpiScale) =>
        Ui == null ? RectF.Empty : UiGuard("Layout", () => Ui.Layout(screen, dpiScale), RectF.Empty);
    internal void UiRenderNow(ID2D1DeviceContext ctx, UiTheme theme) =>
        UiGuard("Render", () => Ui.Render(ctx, theme));

    /// <summary>
    /// 每次渲染前调用一次：让接输入小窗跟着界面这一刻占的地方走。
    /// 界面隐藏或没占地方时把它藏起来——藏起来就等于"不存在于输入里"。
    /// </summary>
    private void UpdateUiInputWindow()
    {
        RectF logical = UiVisibleNow ? UiQueryBoundsNow() : RectF.Empty;

        if (logical.IsEmpty)
        {
            if (_uiInputShown && _uiInputHwnd != IntPtr.Zero)
            {
                Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
                _uiInputShown = false;
                _uiHover = false;
            }
            return;
        }

        EnsureUiInputWindow();
        if (_uiInputHwnd == IntPtr.Zero) return;

        // 逻辑 → 物理只在这里做一次（界面的坐标系永远只有逻辑那一套）。
        var phys = new RectF
        {
            MinX = logical.MinX * DpiScale, MinY = logical.MinY * DpiScale,
            MaxX = logical.MaxX * DpiScale, MaxY = logical.MaxY * DpiScale,
        };

        if (_uiInputShown && phys.Equals(_uiInputRect)) return;

        Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST,
            (int)MathF.Floor(phys.MinX), (int)MathF.Floor(phys.MinY),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxX - phys.MinX)),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxY - phys.MinY)),
            Native.SWP_NOACTIVATE | (_uiInputShown ? 0 : Native.SWP_SHOWWINDOW));

        _uiInputRect = phys;
        _uiInputShown = true;
    }

    private void EnsureUiInputWindow()
    {
        if (_uiInputHwnd != IntPtr.Zero) return;

        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _uiInputHwnd = Native.CreateWindowEx(exStyle, _className, "InkTeachUiInput",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_uiInputHwnd == IntPtr.Zero)
        {
            Console.WriteLine("界面接输入小窗创建失败: " + Marshal.GetLastWin32Error()
                              + "（面板在穿透模式下会点不动）");
            return;
        }

        // 1/255 的不透明度：肉眼看不见，但对命中测试来说它**实实在在地在这**。
        Native.SetLayeredWindowAttributes(_uiInputHwnd, 0, 1, Native.LWA_ALPHA);

        // 这个小窗也算"我们的窗口"：系统可能拿它来问长按手势（消息那条见 WndProc），
        // 窗口属性的那两条要在这里补一次（手势按窗口算，漏一个窗口就漏一块地方）。
        Native.DisableSystemPressAndHold(_uiInputHwnd);
    }

    /// <summary>
    /// 接输入小窗的消息。它不画任何东西，只负责把指针事件翻成界面事件。
    /// 坐标一律是**物理屏幕坐标**——<c>UiPointer*</c> 那三个包装负责除以 DPI，
    /// 跟覆盖层那条路完全同一套口径。
    /// </summary>
    private IntPtr UiInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // 这块就是面板本身，不需要再判断"落在面板里的哪个位置"。
            case Native.WM_NCHITTEST:
                _cntNcHitTest++; _cntNcHitClient++;
                return new IntPtr(Native.HTCLIENT);

            // 点面板不许把下层程序的焦点抢走（老师点一下按钮，PPT 还是前台）。
            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            // 光标：和覆盖层那条路**问同一份判据**（指针在面板上 = 箭头）。
            // 不答这一条的话系统会拿**类光标**兜底（NULL → 默认箭头）：面板上看着没问题，
            // 但"面板 ↔ 画布"来回走时，我们那个"已设过就不重设"的缓存会被这一次
            // 兜底悄悄作废（见 ApplyCursor 的注释）。
            case Native.WM_SETCURSOR:
                ApplyCursor(force: true);
                return new IntPtr(1);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERENTER:
                _uiHover = true; _dirty = true; ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                _uiHover = false; _dirty = true; ApplyCursor();
                // 界面那条"人走了"由这里发：方案 B 下，指针离开面板＝离开这块接输入小窗。
                UiGuard("PointerLeave", () => Ui.PointerLeave());
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntDown++;
                LastPointerType = ptype;
                _uiHover = true;

                if (UiPointerDown(sx, sy, pressure, ptype == Native.PT_PEN, inverted))
                {
                    // 界面也要捕获：在按钮上滑开、拖出面板，都要继续收到消息。
                    Native.SetCapture(hWnd);
                }
                // 界面没吃这一口也**不落墨、不透给下层**：QueryBounds 声明的就是
                // 界面的地盘（面板底下的墨看不见，将来面板一挪又冒出来）。
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntMove++;
                LastPointerType = ptype;
                _uiHover = true;
                UiPointerMove(sx, sy, pressure, ptype == Native.PT_PEN, inverted);
                _dirty = true;
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (ReadPointer(id, out float sx, out float sy, out float pressure,
                                out bool inverted, out _))
                {
                    StampInput(); _cntUp++;
                    UiPointerUp(sx, sy, pressure, false, inverted);
                }
                Native.ReleaseCapture();
                UiCapturing = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                UiCapturing = false;
                _uiHover = false;
                _dirty = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>控制台用法说明。产品界面不会走这里。</summary>
    protected virtual void PrintUsageHelp()
    {
        Console.WriteLine();
        Console.WriteLine("InkTeach 批注原型已启动。键位（可配置，见 " + InkSettings.FilePath + "）：");
        Console.Write(Keys.ToText());
        if (Keys.GlobalFailures.Count > 0)
        {
            Console.WriteLine("  ！下面这些键没注册上（被别的程序占了？换一个键）：");
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    " + f);
        }
        Console.WriteLine();
    }

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
        var targets = new List<Stroke>();
        var colors = new List<Color4>();
        int changed = 0;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            // 荧光笔与普通墨迹共用同一个"基色"，但荧光笔要转成半透明——
            // 直接按基色刷会把荧光笔刷成实心（那种"越改越糟"的效果）。
            var c = s.Color.A < 0.99f ? InkPalette.ToHighlighter(baseColor) : baseColor;
            if (s.Color.Equals(c)) continue;
            targets.Add(s);
            colors.Add(c);
            changed++;
        }
        if (changed == 0) return 0;
        Doc.ApplyColors(targets, colors.ToArray());   // 一条动作 = 一步撤销
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
        float dpi = DpiScale;
        int sc = SelectionHandles.SwatchCount;
        var part = SelectionHandles.PanelPartAt(x, y, aabb, dpi, ViewportCanvas, SelPanelOpen, sc);

        switch (part)
        {
            case SelectionHandles.PanelPart.Slider:
                _sliderDragging = true;
                SetWidthStepAt(x, aabb);
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
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: true);
                return true;

            case SelectionHandles.PanelPart.LayerBack:
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: false);
                return true;

            // 导出格式：0 = PNG（透明底）、1 = JPG（白底）。
        }

        if (part >= SelectionHandles.PanelPart.SwatchBase)
        {
            int i = part - SelectionHandles.PanelPart.SwatchBase;
            var swatches = InkPalette.SelectionSwatches;
            if (i < 0 || i >= swatches.Length) return true;
            if (i == swatches.Length - 1)      // 末格 = 自定义取色（本轮占位）
            {
                Console.WriteLine("自定义取色：下一批接系统取色器");
                return true;
            }
            SetSelectionColor(swatches[i].Color);
            return true;
        }
        return false;                          // 面板的空白处：什么都不做
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

    /// <summary>把滑条拖到 x 处 → 吸附到最近的档位并应用。</summary>
    private void SetWidthStepAt(float x, in RectF aabb)
    {
        var steps = WidthStepsForSelection();
        int i = SelectionHandles.SliderNearestStep(x, steps.Length, aabb, DpiScale, ViewportCanvas,
                                                   SelectionHandles.SwatchCount);
        SetSelectionWidth(steps[i]);
    }

    /// <summary>当前选区在滑条上对应的档位（取第一条非图像对象）。</summary>
    internal int SliderStepOfSelection(in RectF aabb)
    {
        var steps = WidthStepsForSelection();
        float wLogical = 0f;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            wLogical = s.Width / DpiScale;
            break;
        }
        int best = 0; float bestD = float.MaxValue;
        for (int i = 0; i < steps.Length; i++)
        {
            float d = MathF.Abs(steps[i] - wLogical);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>选区里的档位表长度（渲染滑条刻度、命中同源用）。</summary>
    internal int SliderStepCount() => WidthStepsForSelection().Length;

    /// <summary>
    /// 操作条上鼠标悬停的是哪一格（-1 = 没在条上）。**只在没按住时算**：
    /// 拖动中指针早就离开按钮了，重算只会让高亮乱跳。
    ///
    /// 判据走 <see cref="SelectionBarShown"/>（和绘制、光标同一条）：那一块不在的时候
    /// 悬停高亮也不该亮。收起态（用户自己收的 / 画完自动选中那个框）只高亮那颗圆钮。
    /// </summary>
    private void UpdateBarHover(float x, float y)
    {
        int hover = -1;
        if (!_drawing && SelectionBarShown)
        {
            var aabb = LiveSelectionFrame.CanvasAabb;
            if (BarDrawnCollapsed)
            {
                var dot = SelectionHandles.BarCollapsedRect(aabb, DpiScale, ViewportCanvas);
                hover = dot.Contains(x, y) ? (int)SelBarButton.Collapse : -1;
            }
            else hover = SelectionHandles.BarButtonAt(x, y, aabb, DpiScale, ViewportCanvas);
        }
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
        WriteVertexLocalPoints(_vertexPreviewCanvas);
        UpdateVertexReadout();
        _vertexSnapped = false;
        _shapeSnap = ShapeSnapKind.None;
        _shapeSnapAnchor = _vertexPreviewCanvas;

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

        // 拖 P：**点表一个字没变**（P 不是控制点），只有那个参数角要写
        //（撤销要不要跟着回，见 SetStrokeGeometryAction 的 newFocusU）。
        // ⚠ 传的是 `float?`：只有"真的在拖 P"才带值，别的改几何动作传 null（一个字都不动 P）。
        if (focusDrag) Doc.ApplyGeometry(s, pts, focusU);
        // 拖格距：同一个套路——点表一个字没变（格距不是控制点），只有那一个数要写。
        else if (gridDrag) Doc.ApplyGeometry(s, pts, null, gridStep);
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
    private void WriteVertexLocalPoints(Vector2 canvasPoint)
    {
        var s = _vertexTarget;
        var pts = _vertexPreviewLocal;
        // 拷的是"模型里**现在有的**点"，而 `pts` 可能**比它长**（双曲线那个可缺的第三个点，
        // 见 BeginVertexDrag）——长出来的那几格先留零向量，由下面的分支按需填。
        for (int i = 0; i < pts.Length; i++)
            pts[i] = i < s.Points.Count ? new Vector2(s.Points[i].X, s.Points[i].Y) : Vector2.Zero;

        var local = SelectionHandles.ToLocalPoint(canvasPoint, s.Transform);
        var c = s.ShapeCenterLocal;
        float minAxis = ShapeMinAxisLogical * DpiScale;
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
        float lenTol = SelectionHandles.ShapeSnapLengthToleranceLogical * DpiScale;
        _shapeSnap = ShapeSnapKind.None;        // 每帧重判：不吸的帧必须回到"没有"

        switch (s.Kind)
        {
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

            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
            {
                // 定义元素**各拖各的**——这正是"它们都是真的定义元素"那件事的兑现。
                // 这里没有"特殊形状吸附"：坐标系 / 数轴没有等腰、直角那一类的约束，
                // `_shapeSnap` 就一直是 None（上面已经重置过）。
                int idx = SelectionHandles.VertexIndex(_vertexHandle);
                if (idx < 0) break;

                // **第 4 颗 = 格距手柄**（用户 2026-09-24："拖动那个点来改变这个方格的大小"）：
                // 它**不是控制点**，所以这一支要判在"点数"那道闸**之前**（坐标系只有三个点），
                // 而且**一个点都不动**——外框、原点都保持原样，改的只有格距。
                if (idx == 3 && s.Kind == StrokeKind.Coordinate)
                {
                    // 手柄落在"原点 + (格距, −格距)"上，所以两个方向的偏移量都等于格距；
                    // 取平均（= 打到那条对角线上的投影）而不是单看某一轴：拖动方向偏一点也不会跳。
                    var d = local - pts[2];
                    float want = (MathF.Abs(d.X) + MathF.Abs(d.Y)) * 0.5f;
                    _vertexPreviewGridStep = Math.Clamp(want,
                        Stroke.AxisMinGridStepLocal,
                        Stroke.AxisMaxGridStepLocal(pts[0], pts[1]));
                    break;
                }

                if (idx >= pts.Length) break;
                float minLen = ShapeMinAxisLogical * DpiScale;

                if (s.Kind == StrokeKind.NumberLine)
                {
                    // **数轴的两个端点**：只改 x，y 一律保持 —— 往斜上方拖也还是水平线
                    // （数轴歪了就不是数轴了）。同时不许互相越过：越过去"左端/右端"
                    // 这两个名字就撒谎了，手柄也会互换位置。
                    float x = local.X;
                    if (idx == 0) x = MathF.Min(x, pts[1].X - minLen);
                    else x = MathF.Max(x, pts[0].X + minLen);
                    pts[idx] = new Vector2(x, pts[0].Y);
                    break;
                }

                if (idx == 2)
                {
                    // **原点**：在外框里随便挪（老师最常用的动作就是"框画完了，
                    // 把原点拖到左下角，只留第一象限"）。
                    // 夹在外框内是必须的：跑到框外，两条轴就都不在框里交叉了，
                    // 画出来是个说不清的东西（而贴在框角上正是想要的那种用法）。
                    float fx0 = MathF.Min(pts[0].X, pts[1].X), fx1 = MathF.Max(pts[0].X, pts[1].X);
                    float fy0 = MathF.Min(pts[0].Y, pts[1].Y), fy1 = MathF.Max(pts[0].Y, pts[1].Y);
                    pts[2] = new Vector2(Math.Clamp(local.X, fx0, fx1), Math.Clamp(local.Y, fy0, fy1));
                }
                else
                {
                    pts[idx] = local;                 // 外框任意一角都能拖（改范围）
                }
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
    /// 刷新"拖动中的读数"：直线是倾斜角 α、圆是 `r`（附带直径 d = 2r）、椭圆是 `a` 或 `b`。
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
                // 三角形 / 平行四边形**这一轮没有读数**：它们要显示的是内角 / 夹角，
                // 那是第③轮（9.7）的事；现在改顶点时只有"吸到了什么"那一颗胶囊
                // （见 ShapeSnapKind）。**不要**落到 default 去报一个 α：
                // 三个顶点之间根本没有"倾斜角"这个量。
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            // 四种曲线的读数（p / 实半轴 / 虚半轴 / 周期＋振幅）2026-09-20 随手柄一起删了：
            // 读数挂在手柄上，手柄没了，那个量就没人拖得动它们了（见 ShapeHandlesOf）。
            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
                // 坐标系 / 数轴同样**没有角度读数**：四个定义元素之间没有"倾斜角"可言
                // （轴永远是水平的 / 竖直的，刻度间距也只是个长度）。
                // 落到 default 去报 α 的话，拖框角会给出一个毫无意义的度数。
                _vertexReadout = VertexReadoutKind.None;
                _vertexReadoutValue = 0f;
                _vertexReadoutSecondary = 0f;
                break;

            default:
                _vertexReadout = VertexReadoutKind.Inclination;
                _vertexReadoutValue = SelectionHandles.InclinationDegrees(c, e);
                _vertexReadoutSecondary = 0f;
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
        float dpi = DpiScale;
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var aabb = frame.CanvasAabb;

        // ① 操作条那一块——**收起来就是那一颗圆钮，摊开了就是一整条 ＋ 面板**。
        //    范围和 SelectionCursor 里那几块**同一个**（"看得见的一块"和"点得到的一块"
        //    必须是同一个）。
        if (BarDrawnCollapsed)
        {
            if (SelectionHandles.BarCollapsedRect(aabb, dpi, ViewportCanvas).Contains(x, y))
                return AutoSelZone.Furniture;
        }
        else
        {
            if (SelectionHandles.BarRect(aabb, dpi, ViewportCanvas).Contains(x, y))
                return AutoSelZone.Furniture;
            if (SelPanelOpen != SelPanel.None
                && SelectionHandles.PanelContains(x, y, aabb, dpi, ViewportCanvas,
                                                  SelPanelOpen, SelectionHandles.SwatchCount))
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
        var lp = frame.ToLocalPoint(new Vector2(x, y));
        if (lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
            && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY)
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
           && (Tool == Tool.Marquee
               || ((IsShapeTool(Tool) || _dwellSelected)
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
        => Doc.Selected.Count > 0 && (Tool == Tool.Marquee || IsShapeTool(Tool) || _dwellSelected);

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
    private bool TryBeginSelectionGesture(float x, float y, bool shift, bool alt)
    {
        _dragHitStroke = null;
        _pendingClone = null;
        float dpi = DpiScale;

        bool move = false;
        var h = SelHandle.None;
        var frame = SelectionHandles.FrameOf(Doc.Selected);

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
                var dot = SelectionHandles.BarCollapsedRect(aabb0, dpi, ViewportCanvas);
                if (dot.Contains(x, y))
                {
                    SelBarCollapsed = false;
                    // **画完自动选中那个框**：点开这颗圆钮之后就成"常规那一套"了
                    //（整条操作条出来、框内任意一点都能拖）——用户 2026-09-22 定的就是这一步。
                    _autoSelCollapsed = false;
                    _dirty = true;
                    return true;
                }
            }
            else if (SelPanelOpen != SelPanel.None
                     && SelectionHandles.PanelContains(x, y, aabb0, dpi, ViewportCanvas,
                                                       SelPanelOpen, SelectionHandles.SwatchCount))
            {
                HandlePanelClick(x, y);
                return true;                        // 面板上的点击一律吃掉（包括空白处）
            }
            else
            {
                int btn0 = SelectionHandles.BarButtonAt(x, y, aabb0, dpi, ViewportCanvas);
                if (btn0 >= 0) { RunBarAction(btn0, frame, aabb0); return true; }
                if (SelPanelOpen != SelPanel.None) { SelPanelOpen = SelPanel.None; _dirty = true; }
            }
        }

        // —— 1~3：有选中时才谈得上（没选中就直接跳到第 4 步的点选）——
        if (Doc.Selected.Count > 0)
        {
            // 操作条定位用框的**画布轴对齐范围**：框本身可能是斜的，但"它占了屏幕上
            // 哪一块"永远是个正矩形，操作条贴在那个矩形的下面才对。
            var aabb = frame.CanvasAabb;

            // （操作条与面板已经在 ⓪ 里处理过了，这里只剩手柄 / 框内拖动 / 点选）
            h = SelectionHandles.HitTest(x, y, Doc.Selected, frame, dpi);

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
                    // 没点在手柄上：把指针变回框坐标，看是不是落在框里（整体拖动）。
                    var lp = frame.ToLocalPoint(new Vector2(x, y));
                    move = lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
                        && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY;

                    // 顺手记下"指针底下是哪一条"：松手时若一点没移动，就把多选**收窄成只选它**
                    // （PPT/Figma 的行为）。落在框内空白处 → 记不到东西 → 松手不改选择。
                    if (move) _dragHitStroke = Doc.HitObjectAt(x, y, ClickToleranceLogical * dpi);
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

        // —— 4.5：复制拖拽模式：按住选中内容拖动 = **拖出一份副本**（原件不动）——
        // 克隆放在这里（按下那一刻），副本与原件完全重合，随后这一次拖动移动的就是副本；
        // 松手时把"插入副本 + 移动副本"合成一步撤销（见 EndSelDrag）。
        if (move && CopyDragArmed)
        {
            _pendingClone = Doc.CloneSelectedInPlace();
            if (_pendingClone.Strokes.Count > 0)
            {
                frame = SelectionHandles.FrameOf(Doc.Selected);   // 副本与原件重合，框不变
                _dirty = true;
            }
            else _pendingClone = null;
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
    /// 拖动中：每帧都从**按下那一刻的变换**重算，而不是在上一帧结果上继续乘。
    /// 后者会累积浮点误差，拖得越久偏得越多，撤销也回不到原样。
    /// </summary>
    private void UpdateSelDrag(float x, float y)
    {
        // 拖端点：另一条路（改几何，模型不动），和"拖对象"分开走。
        if (_vertexDragging) { UpdateVertexDrag(x, y); return; }

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
            m = Matrix3x2.CreateTranslation(cur - _dragStartPoint);
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
        SelDragging = false;
        SelRotating = false;            // 度数标签只在拖动中出现
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
            _pendingClone = null;
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
        if (_pendingClone != null)
        {
            // 拖出副本：**插入副本 + 移动副本 = 一步撤销**（拖错了按一次就全回去）。
            if (_selDragMatrix.IsIdentity)
            {
                Doc.CommitCompound(_pendingClone);
            }
            else
            {
                var moveAct = new TransformObjectsAction(Doc.Selected, _selDragMatrix);
                moveAct.Redo(Doc);
                Doc.CommitCompound(_pendingClone, moveAct);
            }
            _pendingClone = null;
        }
        else if (!_selDragMatrix.IsIdentity)
        {
            Doc.ApplyTransform(_selDragMatrix);
        }

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
        _pendingClone = null;
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
    private void RunBarAction(int index, in SelectionFrame frame, in RectF aabb)
    {
        // 下标 ↔ 语义只在 SelBarButton 里写一遍（渲染、命中、动作三处共用它）。
        var btn = (SelBarButton)index;
        switch (btn)
        {
            // **收起工具条**（用户 2026-09-16 更正：条首那个 ✕ 不是"取消选择"）。
            // 收起后只剩一个小圆钮，点它展开；框和手柄照旧，收起的只是"条"。
            case SelBarButton.Collapse:
                SelBarCollapsed = true;
                SelPanelOpen = SelPanel.None;
                CopyDragArmed = false;
                break;

            case SelBarButton.Color:
                SelPanelOpen = SelPanelOpen == SelPanel.Ink ? SelPanel.None : SelPanel.Ink;
                break;

            case SelBarButton.Lock:
                ToggleSelectionLock();
                break;

            case SelBarButton.Layer:
                SelPanelOpen = SelPanelOpen == SelPanel.Layer ? SelPanel.None : SelPanel.Layer;
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
                // **存入图库**：把选中的对象存成一个条目（磁盘那点事在 ShapeLibrary 里）。
                // 反馈只有"闪一下"——这一版没有 toast；存进去之后从图形面板最后那一段
                // 「图库」里能看见（`--librarytest` 就是照这条链路量的）。
                int saved = SaveSelectionToLibrary();
                Console.WriteLine(saved > 0
                    ? $"已存入图库（{saved} 个对象）"
                    : "存入图库失败（选中为空或者写不进去）");
                break;
            }

            // **进入/退出"复制拖拽模式"**，不是"点一下原地克隆一份"。
            // 抄 InkClass 的结论：点击即克隆那版"副本固定偏移 24px、落点不可控"，已废弃；
            // 现在点图标只进模式（图标高亮 + 框变虚线），之后按住选中内容拖 = 拖出副本。
            case SelBarButton.Copy:
                CopyDragArmed = !CopyDragArmed;
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
                int n = Doc.Selected.Count, locked = 0;
                foreach (var s in Doc.Selected) if (s.Locked) locked++;
                Doc.DeleteSelected();
                Console.WriteLine(locked > 0
                    ? $"删除 {n - locked} 个对象（{locked} 个锁定，没删）"
                    : $"删除 {n} 个对象");
                break;
            }
        }
        Laser.Clear();
        // 删除之后选区可能空了；模式跟着选区走（InkClass 同款：选区没了就退出）。
        if (Doc.Selected.Count == 0) CopyDragArmed = false;
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
    // =====================================================================
    //  批注键盘模式
    // =====================================================================

    /// <summary>
    /// 开关批注键盘模式。
    ///
    /// 开：去掉 WS_EX_NOACTIVATE 并把窗口提到前台，键盘归批注层，编辑类
    ///     快捷键（Ctrl+Z / Ctrl+D / Delete / 方向键…）才有地方落地。
    /// 关：加回 WS_EX_NOACTIVATE，覆盖层回到"永不抢焦点"，键盘还给下层程序
    ///     ——那时候只有全局热键（Ctrl+Alt+…）可用。
    ///
    /// 取舍说清楚：开着的时候，放映中的 PPT 收不到键盘。
    /// </summary>
    internal void SetKeyboardMode(bool on)
    {
        KeyboardMode = on;
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            if (on) ex &= ~Native.WS_EX_NOACTIVATE;
            else ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            if (on) Native.SetForegroundWindow(w.Hwnd);
        }
        Console.WriteLine($"批注键盘模式 = {on}"
            + (on ? "（键盘归批注层；此时下层程序收不到键盘）" : "（键盘还给下层程序）"));
        _dirty = true;
    }

    /// <summary>
    /// 批注键盘模式下的按键。**只在 <see cref="KeyboardMode"/> 打开时收到。**
    ///
    /// 键位一律照 Windows 的通用习惯，不自己发明：
    ///   Ctrl+Z 撤销 / Ctrl+Y 重做 / Ctrl+A 全选 / Delete 删除 / Esc 取消选择
    ///   Ctrl+D 复制一份（系统剪贴板还没接，先用 D）
    ///   方向键移动 1 像素，按住 Shift 是 10 像素
    ///
    /// 已知待改：方向键按住会重复触发，每次都是一条撤销记录。要接"连续微调
    /// 合并成一步"，得等编辑命令支持合并（撤销栈里相邻同类动作合并）。
    /// </summary>
    // ================= 工具键的"手势"状态机 =================
    //
    // 手写板的笔上只有两个按钮，所以工具键要"一键多用"（用户 2026-09-29 定）：
    //   · 单击             = 切到它；**已经是它** → 连按换色/换档（和点面板那一格一个规矩）
    //   · 快速双击(≤350ms) = 主工具往后轮一格：笔 → 荧光笔 → 激光笔 → 橡皮 → 选中 → 笔
    //   · 按住(≥0.6s)      = 回第一个颜色 / 第一档
    //
    // 为什么要"延迟 250ms 结算连按换色"：双击和"同键连按"只能靠时间分开。
    // 延迟只落在**最后一击**上：连按时下一击一到就立刻结算，手感是连续的。
    private const double ToolKeyDoubleMs = 350;   // 两击间隔 ≤ 它 = 双击
    private const double ToolKeyHoldMs = 600;     // 按住 ≥ 它 = 长按
    // 连按动作（换色/换档）在**松手后**再等这么久才结算。
    // ⚠ 必须 > ToolKeyDoubleMs：双击是"第二次按下落在上次松手后 350ms 内"，
    //   结算若比它早，双击就会被"先换了色、再切工具"（2026-09-29 自己踩过）。
    private const double ToolKeyRepeatMs = 400;

    private sealed class KeyGesture
    {
        public double DownAt = -1;      // 本次按下时刻；-1 = 没按着
        public double UpAt = -1;        // 上次松开时刻；-1 = 不参与双击判定（刚长按完）
        public bool HoldFired;          // 本次按住已经触发过长按
        public bool WantsRepeat;        // 这一击落在"已经在这个工具上" → 松手后要换色/换档
        public double PendingAt = -1;   // 待结算的"连按动作"时刻
        public KeyAction PendingWhat;
    }

    private readonly Dictionary<KeyAction, KeyGesture> _gestures = new();

    private KeyGesture Gest(KeyAction a)
    {
        if (!_gestures.TryGetValue(a, out var g)) _gestures[a] = g = new KeyGesture();
        return g;
    }

    /// <summary>这些键走手势状态机（单击/双击/长按），其余键照旧一按一动。</summary>
    private static bool IsToolKey(KeyAction a) => a
        is KeyAction.ToolPen or KeyAction.ToolHighlighter or KeyAction.ToolLaser
        or KeyAction.ToolEraser or KeyAction.ToolPixelEraser or KeyAction.ToolMarquee;

    /// <summary>主工具的轮换顺序（双击往后一格）。</summary>
    private static readonly Tool[] MainToolCycle =
        { Tool.Pen, Tool.Highlighter, Tool.Laser, Tool.Eraser, Tool.Marquee };

    private static Tool ToolOf(KeyAction a) => a switch
    {
        KeyAction.ToolPen => Tool.Pen,
        KeyAction.ToolHighlighter => Tool.Highlighter,
        KeyAction.ToolLaser => Tool.Laser,
        KeyAction.ToolEraser => Tool.Eraser,
        KeyAction.ToolPixelEraser => Tool.PixelEraser,
        _ => Tool.Marquee,
    };

    /// <summary>双击：主工具往后轮一格（图形/截图这些不在循环里 → 从笔开始）。</summary>
    private void NextMainTool()
    {
        int i = Array.IndexOf(MainToolCycle, Tool);
        var next = MainToolCycle[(i + 1) % MainToolCycle.Length];
        SwitchTool(next);
        Console.WriteLine($"工具键双击 → {ToolName(next)}");
    }

    private bool ToolKeyDown(KeyAction a, double now)
    {
        var g = Gest(a);
        if (g.DownAt >= 0) return true;                            // 自动重复的按下：忽略，等松手
        bool dbl = g.UpAt >= 0 && now - g.UpAt <= ToolKeyDoubleMs;
        g.DownAt = now;
        g.HoldFired = false;
        g.WantsRepeat = false;
        if (dbl)
        {
            CancelPending(g);
            g.UpAt = -1;                                            // 双击后不再连着判"第三击"
            NextMainTool();
            return true;
        }
        var target = ToolOf(a);
        if (Tool != target) { SwitchTool(target); return true; }     // 单击：立即切（要快）
        // 已经在这个工具上 → 松手后 400ms 执行"连按动作"（等一等看是不是双击）
        g.WantsRepeat = true;
        return true;
    }

    private bool ToolKeyUp(KeyAction a, double now)
    {
        var g = Gest(a);
        if (g.DownAt < 0) return false;
        double held = now - g.DownAt;
        g.DownAt = -1;
        if (held >= ToolKeyHoldMs || g.HoldFired) { g.UpAt = -1; g.WantsRepeat = false; return true; }
        g.UpAt = now;                                               // 短按：记下来给双击判定用
        if (g.WantsRepeat)
        {
            g.WantsRepeat = false;
            g.PendingAt = now + ToolKeyRepeatMs;
            g.PendingWhat = a;
        }
        return true;
    }

    /// <summary>每帧一次：长按判定 + 连按动作的延迟结算。</summary>
    private void PumpKeyGestures()
    {
        double now = NowMs;
        foreach (var (a, g) in _gestures)
        {
            if (g.DownAt >= 0 && !g.HoldFired && now - g.DownAt >= ToolKeyHoldMs)
            {
                g.HoldFired = true;
                CancelPending(g);
                ResetToolToFirst(a);
                continue;
            }
            if (g.PendingAt >= 0 && now >= g.PendingAt)
            {
                var what = g.PendingWhat;
                CancelPending(g);
                DoToolKeyRepeat(what);
            }
        }
    }

    private static void CancelPending(KeyGesture g) => g.PendingAt = -1;

    /// <summary>连按要干的事：笔/荧光笔换色，橡皮切整笔↔面积，选中切矩形↔套索。</summary>
    private void DoToolKeyRepeat(KeyAction a)
    {
        switch (a)
        {
            case KeyAction.ToolPen: CycleBandColor(highlighter: false); break;
            case KeyAction.ToolHighlighter: CycleBandColor(highlighter: true); break;
            case KeyAction.ToolEraser:
            case KeyAction.ToolPixelEraser:
                SwitchTool(Tool == Tool.PixelEraser ? Tool.Eraser : Tool.PixelEraser);
                Console.WriteLine($"橡皮连按 → {(Tool == Tool.PixelEraser ? "面积擦" : "整笔擦")}");
                break;
            case KeyAction.ToolMarquee: ToggleSelectMode(); break;
        }
    }

    /// <summary>长按：回第一个颜色 / 第一档。</summary>
    private void ResetToolToFirst(KeyAction a)
    {
        switch (a)
        {
            case KeyAction.ToolPen:
                SetColorFromUi(InkPalette.PenBand[0].Color);
                Console.WriteLine($"长按 → 笔回到「{InkPalette.PenBand[0].Name}」");
                break;
            case KeyAction.ToolHighlighter:
                SetColorFromUi(InkPalette.HighlighterBand[0].Color);
                Console.WriteLine($"长按 → 荧光笔回到「{InkPalette.HighlighterBand[0].Name}」");
                break;
            case KeyAction.ToolEraser:
            case KeyAction.ToolPixelEraser:
                SwitchTool(Tool.Eraser);
                Console.WriteLine("长按 → 橡皮回到「整笔擦」");
                break;
            case KeyAction.ToolMarquee:
                if (SelMode == SelectMode.Lasso) ToggleSelectMode();
                Console.WriteLine("长按 → 框选回到「矩形框」");
                break;
        }
    }

    /// <summary>
    /// 连按换色：在色带里往后走一格（转圈）。
    ///
    /// ⚠ **用记下来的序号走，不靠"按颜色值找当前位置"**——2026-09-30 修的真 bug：
    /// 以前每按一下都拿当前颜色去色带里匹配，容差 0.10 把「深蓝 / 藏青」认成同一个，
    /// 于是在 墨绿→酒红→藏青→(被当成深蓝)→墨绿 之间来回蹦，用户实测"只有后面三种在切"。
    /// 现在序号只在 SetColorFromUi（别人改了色）时同步，循环本身永远 +1。
    /// </summary>
    private void CycleBandColor(bool highlighter)
    {
        var band = highlighter ? InkPalette.HighlighterBand : InkPalette.PenBand;
        int idx = highlighter ? _hlColorIdx : _penColorIdx;
        int next = ((idx % band.Length) + 1) % band.Length;
        if (highlighter) _hlColorIdx = next; else _penColorIdx = next;
        SetColorFromUi(band[next].Color);      // 荧光笔的透明度由 SetColorFromUi 自己加
        Console.WriteLine($"连按 → {band[next].Name}");
    }

    /// <summary>当前笔色 / 荧光色在色带里的序号（默认 红 / 荧光黄）。</summary>
    private int _penColorIdx = 1;
    private int _hlColorIdx;

    private static bool CloseColor(Color4 a, Color4 b) =>
        Math.Abs(a.R - b.R) < 0.10f && Math.Abs(a.G - b.G) < 0.10f && Math.Abs(a.B - b.B) < 0.10f;

    /// <summary>上次用的橡皮形态（整笔擦 / 面积擦）——按 Ctrl+E 时切回它，见 SetUiPref("eraserKind")。</summary>
    private Tool _eraserKind = Tool.Eraser;

    /// <summary>
    /// 工具键的**单击**逻辑（2026-09-30 收口：双击/长按那套手势全部取消，只留单击）：
    ///   · 不是这个工具 → 切过去
    ///   · 已经是它    → 换一个：笔/荧光笔换颜色、橡皮切整笔⇄面积、选择切矩形⇄套索
    /// </summary>
    private void ToolKeyPress(KeyAction a)
    {
        var target = ToolOf(a);
        // 橡皮：回到"上次用的那一种形态"（整笔/面积），不是永远回整笔擦
        if (target == Tool.Eraser) target = _eraserKind;
        if (Tool != target) { SwitchTool(target); return; }
        DoToolKeyRepeat(a);
    }

    private bool HandleKeyDown(IntPtr wParam, bool isRepeat)
    {
        // 键位表驱动：按"当前修饰键状态 + 主键"拼成一个和弦，去批注内作用域里查。
        // 查不到就**不吞这个键**（返回 false），交给系统/下层程序——吞掉所有按键
        // 会让批注键盘模式下连输入法都用不了。
        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;

        var chord = new KeyChord(mods, (uint)wParam.ToInt32());
        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null) return false;

        if (IsToolKey(hit.Action))
        {
            // **自动重复的按下吞掉**：按住不放时 Windows 会连发 KEYDOWN，而"已经是它 →
            // 换色"这条会被连发带着一路狂转（所以工具键只看第一次按下，松手才算一次）。
            if (!isRepeat) ToolKeyPress(hit.Action);
            _dirty = true;
            return true;
        }
        RunAction(hit.Action);
        _dirty = true;
        return true;
    }

    /// <summary>松键：只服务工具键的手势（长按/双击判定），其余键不看松键。</summary>
    private bool HandleKeyUp(IntPtr wParam)
    {
        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;
        var chord = new KeyChord(mods, (uint)wParam.ToInt32());
        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null || !IsToolKey(hit.Action)) return false;
        ToolKeyUp(hit.Action, NowMs);
        return true;
    }

    /// <summary>
    /// 全选。顺便切到选择工具——不切的话手柄和操作条不会出现，
    /// 用户会以为"全选没生效"。
    /// </summary>
    internal void SelectAll()
    {
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 滚轮滚动画布。**只改相机偏移 ViewOffsetY，一个对象的数据都不动**——
    /// 这是滚动能做到 O(1) 的前提。绝不能像 InkClass 那样把平移烘焙进点坐标
    /// （那是 O(对象数)/次，一万笔滚一格要重写十万个点）。
    ///
    /// 只做纵向。往下滚 = 内容上移 = 偏移变负。
    ///
    /// 代价：**这里一个像素都不重画**。内容层是画布空间的分块缓存
    /// （CanvasTiles.cs），滚动只是换个位置把已经画好的块贴上去；只有在
    /// 新露出**没画过**的块时才会光栅化那一小块。所以滚一格的代价与文档
    /// 里有多少笔无关——从"整层重画"（一万笔 82~154ms，滚一格卡一下）
    /// 变成"贴图 + 偶尔补一两块"。这也正是 Win32 ScrollWindowEx 和浏览器
    /// 合成器滚动图层用的手法。
    /// </summary>
    internal IntPtr HandleWheel(IntPtr wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        // 往下滚时 delta = -120，要先变负（内容上移）——所以这里是 +=。
        // 写成 -= 会把符号翻过来：滚轮往下 = 视图往上，正好抵掉，
        // 再被"不许滚过顶部"夹回 0，表现就是**完全滚不动**。
        ViewOffsetY += delta / 120f * 72f * DpiScale;   // 一格 = 72 逻辑像素
        // 上下都夹住画布范围（内容边界 ∪ 一屏）。
        // 不夹的话会滚进无尽的空白，而且比例滚动条拿不到有意义的范围。
        // 下边界是"视口底边贴住内容底边"——接着写，内容长出去，范围自己长出来，
        // 所以不会把人卡在底边。
        ClampViewOffset();

        ScrollBarActiveAtMs = NowMs;                     // 滚动时让滚动条露面
        // 注意：**不调 Doc.InvalidateAll()**。那会把整个文档标脏、让所有分块
        // 重画——正是这次要拔掉的病根。分块缓存自己会发现相机变了，只做重合成。
        _dirty = true;
        return IntPtr.Zero;
    }}
