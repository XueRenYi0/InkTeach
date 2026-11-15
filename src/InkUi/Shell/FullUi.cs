// [职责] 工具条/球/上带/色带的主体布局与绘制。
// [测试] --paneltest

using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using InkEngine;
using InkLocalization;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 完整界面：收起态是**一个球**，点开是一条**按钮带**；球就是带子最左那一格。
///
/// 这是第一版，刻意只做"按钮 ＋ 命中测试"（色带、粗细滑块、「更多」抽屉都还没有）：
/// 先把**坐标 / DPI / 脏区 / 输入拦截**这四件最容易出错的事验对，
/// 后面搬色带和抽屉都只是堆代码（见 计划-底层对接界面.md 阶段 2）。
///
/// 它只认引擎的三个接口：画自己、解释点击、改状态走命令。
/// </summary>
public sealed partial class FullUi : IOverlayUi
{
    // ---- 性能归因开关（开发期）--------------------------------------------
    //
    // 面板每帧到底贵在哪，**不能猜**：把这几块分别关掉、同一份内容各量一遍，
    // 才知道该优化谁（实测：完整展开态比不挂界面贵 2.44 毫秒，超过 1.5 的红线）。
    internal static bool PerfSkipShadow;
    internal static bool PerfSkipChrome;        // 凹槽 ＋ 顶部内高光
    internal static bool PerfSkipSwatchDetail;  // 色片的内高光与选中环
    internal static bool PerfSkipIcons;

    // ---- 按钮表 -------------------------------------------------------------
    //
    // index 0 是"收起格"（就是那个球缩到带子里），1..N 是工具。
    // 分组用 GroupEnd 标出来：组间的间隔要明显大于组内，眼睛才分得开块。
    private static readonly (string Icon, string Filled, string Tip)[] Cells =
    {
        ("pen",       "penFilled",       "收起"),
        ("cursorArrow", "cursorArrowFilled", "鼠标（穿透点击）"),
        ("board",     "board",           "白板"),
        ("pen",       "penFilled",       "笔"),
        ("highlighter","highlighterFilled","荧光笔"),
        ("laser",     "laserFilled",     "激光笔"),
        ("eraser",    "eraserFilled",    "橡皮擦"),
        ("text",      "text",            "文本（点画布输入）"),
        ("select",    "selectFilled",    "选择"),
        ("cut",       "cutFilled",       "截屏"),
        ("shapes",    "shapesFilled",    "图形"),
        ("star",      "starFilled",      "收藏"),
        ("undo",      "undoFilled",      "后撤"),
        ("redo",      "redoFilled",      "重做"),
        ("more",      "moreFilled",      "更多"),
    };

    /// <summary>每一组的最后一格（下标）。组之间画一条分隔线。</summary>
    private static readonly int[] GroupEnds = { 0, 2, 7, 9, 13, 14 };

    /// <summary>
    /// 格子的**稳定身份**（下标会因排序变化，身份不会）。
    /// 持久化"取消了哪几格"只存这些 key；老配置里的数字按下标顺序翻译。
    /// </summary>
    private static readonly string[] CellKeys =
    {
        "ball", "cursor", "board", "pen", "highlighter", "laser", "eraser",
        "text", "select", "capture", "shapes", "star", "undo", "redo", "more",
    };

    /// <summary>v2 落盘顺序（收藏追加在最右）。老配置的数字只在这个顺序里解释。</summary>
    private static readonly string[] LegacyCellKeysV2 =
    {
        "ball", "cursor", "board", "pen", "highlighter", "laser", "eraser",
        "select", "shapes", "capture", "undo", "redo", "more", "star",
    };

    // ---- 界面档位 -----------------------------------------------------------
    //
    // 三档（用户定的默认是"完整档"）：
    //   · 极简 = 一条**短胶囊**，固定五格（球 + 鼠标 + 画笔 + 橡皮 + 更多）；
    //   · 自定义 = 老师自己钉的那份集合（默认＝全部；最少可到球 + 鼠标 + 更多）；
    //   · 完整 = 全部十五格。
    // 规则一条：**极简与自定义都必须是完整档顺序的子序列**——同一个工具在哪一档里
    // 都在同一个相对位置，来回切档不用重新找。

    /// <summary>档位。</summary>
    private enum Profile { Mini = 0, Custom = 1, Full = 2 }

    /// <summary>
    /// 主条格表的版本。**每加一个新格就 +1**；老「完整版」靠它转「自定义」，
    /// 不把新格硬塞进老用户的工具条（用户 2026-10-25 定的硬规则）。
    /// v3 只重排（收藏挪到截屏后），不新增格子；老配置按稳定 key 迁移钉住状态。
    /// </summary>
    private const int CellsVersion = 5;

    /// <summary>每一格从哪个版本开始存在（和 <see cref="CellKeys"/> 对齐）。</summary>
    private static readonly int[] CellSinceVersion =
    {
        0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 2, 0, 0, 0,
    };

    // 极简固定 5 格：球 + 鼠标 + 画笔 + 橡皮 + 更多。
    // 白板 / 撤销仍在完整档和自定义里，只是不在极简主栏显示。
    private static readonly int[] MiniCells = { 1, 3, 6, 14 };

    private Profile _profile = Profile.Full;

    /// <summary>自定义档钉了哪些格（下标对齐 Cells；0 号"收起格"永远在，不参与钉）。</summary>
    private readonly bool[] _pinned = CreateAllPinned();

    // 自定义硬地板只保留鼠标和「更多」：球永远在，所以最少 3 格。
    // 画笔 / 橡皮 / 白板 / 后撤都开放取消；极简仍是固定 5 格，不受这里影响。
    private static bool CanUnpin(int cell) => cell is not (1 or 14);

    private static bool[] CreateAllPinned()
    {
        var a = new bool[Cells.Length];
        for (int i = 1; i < a.Length; i++) a[i] = true;
        return a;
    }

    /// <summary>
    /// 这一档实际显示的格子（含 0 号收起格），**始终按完整档的顺序排列**。
    /// 布局、命中、绘制三处都只认这个列表——不这么统一，切档时总有一处忘了跟着变。
    /// </summary>
    private int[] VisibleCells()
    {
        var list = new List<int> { 0 };
        switch (_profile)
        {
            case Profile.Mini:
                list.AddRange(MiniCells);
                break;
            case Profile.Custom:
                for (int i = 1; i < Cells.Length; i++) if (_pinned[i]) list.Add(i);
                break;
            default:
                for (int i = 1; i < Cells.Length; i++) list.Add(i);
                break;
        }
        return list.ToArray();
    }

    /// <summary>
    /// 上带的**宽度档**。主条每个工具格固定 40 宽，所以可见格数就是带宽的单调代理；
    /// 用格数分档比用绝对像素阈值稳——同样 8 格，因为跨组缝不同会差十几像素，
    /// 用像素阈值会在“加一个图标”时来回跳档，颜色数也跟着闪。
    ///
    ///   3 格（自定义最少）= 3 色、3 档橡皮、紧凑粗细按钮、无虚线；
    ///   4～7 格          = 4 色、5 档笔/3 档橡皮；4 格用粗细按钮，5～7 格用滑条；
    ///   8～9 格          = 6 色、7 档粗细、方形虚线；
    ///   10～11 格        = 8 色、10 档粗细、方形虚线；
    ///   12～15 格（完整）= 全部 8 色、全部粗细档、长方形虚线、完整标签。
    ///
    /// 色片**按可用宽度铺满**；低于约 28px 时先减色片，不继续缩。
    ///
    /// ⚠ 笔色表实际只有 8 色（<see cref="Tokens.Palette"/>），完整档就是 8 色；
    ///    不为了凑“12 色”硬造四个没有名字的颜色。
    /// </summary>
    private enum BandDensity { Minimal = 0, Compact = 1, Regular = 2, Full = 3 }

    private BandDensity Density
    {
        get
        {
            int n = VisibleCount();
            if (n <= 7) return BandDensity.Minimal;
            if (n <= 9) return BandDensity.Compact;
            if (n <= 11) return BandDensity.Regular;
            return BandDensity.Full;
        }
    }

    /// <summary>只数可见格数，不建列表——绘制时每帧会问很多次，别在这里制造垃圾。</summary>
    private int VisibleCount()
    {
        if (_profile == Profile.Mini) return MiniCells.Length + 1;
        if (_profile == Profile.Full) return Cells.Length;
        int n = 1;                                  // 0 号收起格永远在
        for (int i = 1; i < _pinned.Length; i++) if (_pinned[i]) n++;
        return n;
    }

    private IUiHost _host;
    private Widgets _widgets;
    private RectF _screen;                 // 逻辑虚拟桌面（Layout 给的）
    /// <summary>
    /// 逻辑**主屏工作区**（屏幕减掉任务栏）。只用在**默认位置**上（见 <see cref="RawAnchor"/>）：
    /// 用户 2026-09-27 要"面板紧贴任务栏上方、两者不重叠"。
    ///
    /// ⚠ 别拿它替代 <see cref="_screen"/>：夹取、贴边隐藏的位移、吸附都还得按**屏幕**算——
    /// 贴边隐藏那条注释写得很清楚（面板不会"藏到任务栏后面"，只有推出屏幕才真的看不见）。
    /// </summary>
    private RectF _work;
    // 拖过之后的位置：**X = 主条左端，Y = 主条上边**。null = 还没拖过（用默认位置）。
    //
    // 这一格 2026-09-17 来回改过一次，最后**回到假面板的规则**，理由记在 RawAnchor：
    //   · 第一版：没拖过按"整条带子居中"算、拖过之后按左上角算 → 同一个手势两种反应；
    //   · 中间试过"锚球心、两边长"→ 球会滑走（点它收起来要重新瞄）；
    //   · 现在：**锚"展开后带子的左端"，而且那条带子默认屏幕居中**。
    //     球停在带子左端不动，展开＝往右长，展开后整条带子正好居中——用户要的就是这个。
    //
    // 这里特意不用 RectF 来表示"没拖过"：`RectF` 的默认值是全 0，而
    // `IsEmpty` 判的是 `MaxX < MinX`——全 0 的矩形**不算空**，
    // 于是"没拖过"会被当成"拖到了 (0,0)"（自检当场抓到过这一条）。
    private Vector2? _anchor;
    private readonly Anim _expand;         // 0 = 球，1 = 带子
    // 这一帧的贴边位移**只算一次**：PrepareFrame 里推进动画后拍一张快照，
    // QueryBounds、命中测试、Render 全读它。三处各算一次的话，动画中的每一帧
    // 都可能读到略微不同的值——画在这、擦在那，就是"原地残影"。
    private Vector2 _frameShift;
    private bool _frameShiftValid;

    private int _hover = -1;               // -2 = 球，0.. = 格子，-1 = 没有
    private int _press = -1;
    private Vector2 _pressPos;
    private Vector2 _dragStartAnchor;
    private bool _dragging;                // 已经超过拖动阈值

    // ---- 悬停提示（Tooltip；2026-10-02）--------------------------------------
    //
    // 行为：鼠标停在同一个东西上 **500ms**（Tokens.TipDelayMs，照 WPF 默认 400 取松一档），
    // 浮出一张小卡片（名称 + 当前键位 + 一句说明）；移开/按下/拖动立刻收起。
    // **2026-10-16 起**：首次/隔久了 1.5s；出过提示后的 **5 秒窗口内**，下一个提示只等 **400ms**
    //（Tokens.TipFastDelayMs/TipBetweenWindowMs，WPF 的 BetweenShowDelay 口径）；
    // 原「功能提示」开关撤除、改常开。
    //
    // 目标编号**沿用 `HoverAt` 那一套**（-2 球 / 0..13 格 / 100+ 色片 / 200+ 分段 /
    // 300 滑条 / 400 动作按钮 / 500 虚实线）——同一份编号既管高亮也管提示，
    // 不另写一套"哪块算哪块"的映射（那迟早会和高亮漂移）。
    //
    // 触屏/手写笔**没有悬停事件**（仓库老规矩：图形工具的"悬停预览"就是因此改成
    // "按住拖"的），所以提示只当增强，面板上的常显文字一个字都不动。
    private const int TipNoTarget = int.MinValue;
    private int _tipTarget = TipNoTarget;     // 现在停在哪（没有 = 不出提示）
    private double _tipSinceMs;               // 什么时候停上去的（过了延迟才显示）
    private double _lastTipShownAtMs = double.NegativeInfinity;  // 上次真出提示的时刻（"刚看过"窗口）
    private bool _tipShown;                   // 已经过了延迟、正在显示（静态，不再烧帧）
    private readonly Anim _tipFade;           // 0→1 淡入

    // ---- 工具带自定义色取色板（2026-10-23）--------------------------------
    private bool _penPickerOpen;
    /// <summary>取色板是在改荧光笔（true）还是画笔（false）。</summary>
    private bool _pickerHighlighter;
    /// <summary>取色板是在改普通文本框的文字颜色（true）——此时没有色位，直接写 TextColor。</summary>
    private bool _pickerText;
    /// <summary>取色板正在改哪个色位（0..7 画笔 / 0..4 荧光笔）。</summary>
    private int _penPickerSlot = PenLastSlot;
    /// <summary>色值输入框：正在编辑 / 输入缓冲 / 进入时全选 / 进来前的键盘模式。</summary>
    private bool _hexEditing;
    private string _hexBuffer = "";
    private bool _hexSelectAll;
    private bool _hexKeyboardWasOn;
    /// <summary>荧光笔浓度滑条正在拖。</summary>
    private bool _penPickOpacityDragging;
    /// <summary>吸管取完/取消后，把取色板放回来（从取色板里点的吸管）。</summary>
    private bool _pickerReturnAfterPick;
    private bool _pickerReturnArmed;
    private int _pickerReturnSlot;
    private bool _pickerReturnHighlighter;
    private float _penPickH = 0f, _penPickS = 0f, _penPickV = 1f;
    private bool _penPickDragging;
    private bool _penPickOnHue;
    /// <summary>
    /// 色板打开时，某一次按下把它关掉了：这一次**抬起也要吃掉**。
    /// 不吃的话，抬起会落回普通工具条路径（`_press == -1` 时虽然多半什么都不做，
    /// 但任何一次状态漂移都会变成"关掉又重开"的那一下闪烁）。
    /// </summary>
    private bool _penPickerDismissUp;
    private ID2D1Bitmap _penPickSvBmp;
    private ID2D1Bitmap _penPickHueBmp;
    private float _penPickBmpHue = -1f;
    private int _penPickSvPix, _penPickHuePix;

    // 触摸/笔长按（2026-10-02 第二轮）：手指按住不动 600ms 出提示，**松手这一次不执行**。
    // 和悬停的关系见《调研-悬停提示-Tooltip.md》10.3.1："停在它上面=问它，没真点=没发生"。
    // 鼠标**永不进入这条**（鼠标按住=拖动）；笔接触和手指一样算（有悬停的笔平时走悬停）。
    private double _tipHoldStart = double.NegativeInfinity;   // 长按计时起点
    private int _tipHoldTarget = TipNoTarget;                 // 按住的提示目标
    private bool _tipHoldFired;                               // 已弹提示：这次按下作废
    private double _tipLingerUntil;                           // 松手后提示停到什么时候（0=不停）
    private uint _pressId;                                    // 按下的指针 id（挡别的指针搅局）

    /// <summary>
    /// 上带（设置条）现在显示谁的设置。默认是"笔"。
    /// 上带跟着**当前工具**走：键盘换工具时靠 <see cref="OnStateChanged"/> 里那条
    /// 同步规则把它掰回来，不然会出现"拿着橡皮、上带还是色板"。
    /// </summary>
    private int _bandCell = 3;
    /// <summary>上一次看到的收藏面板开合（边沿：打开时把上带收回色线，关闭时恢复正常）。</summary>
    private bool _lastLibraryOpen;
    /// <summary>上一次看到的工具。用它判断"工具真的换了"，而不是"带子对不对"。</summary>
    private Tool _lastTool = Tool.Pen;
    private bool _lastTextBoxEditing;

    /// <summary>
    /// 上一次看到的"在放映吗"。用它认**进放映的那一刻**（边沿），
    /// 见 <see cref="OnStateChanged"/> 里那一段。
    /// </summary>
    private bool _lastPpt;

    /// <summary>上一次看到的穿透状态。用它认**进穿透的那一刻**（边沿）——
    /// 进穿透时把上带收回那条 10 像素色线，穿透期间不许再张，见 <see cref="OnStateChanged"/>。</summary>
    private bool _lastPass;

    private bool _sliderDragging;

    // ---- 笔的虚实线切换（用户 2026-09-19 第 2 件）--------------------------
    /// <summary>
    /// 色带条上那个**虚实线切换**的换挡动画（0 → 1 = "新线型淡入"）。
    ///
    /// 为什么给它一个动画：这一格画的是**一小段线**（实线/虚线/点线的样子），
    /// 直接换会像闪一下屏、而且看不出"刚才按到了没有"——46×26 的小格子里，
    /// 颜色与形状都是一次性跳变的。做法：旧线型淡出、新线型淡入，
    /// 同时强调色描边闪一下；时长复用 <see cref="Tokens.RailMs"/>（和色带开合同一套）。
    /// </summary>
    private readonly Anim _dashFade;
    /// <summary>换挡动画里"从哪一档来"（淡出的那一条线画的是它）。</summary>
    private StrokeDash _dashFadeFrom = StrokeDash.Solid;

    /// <summary>深色主题：手动开关（用户定的），底色/图标/描边整套换。</summary>
    private bool _dark;

    /// <summary>贴边隐藏：默认关（用户定的）。开了以后贴边时只露 8 像素的头。</summary>
    private bool _hideEnabled;
    private readonly Anim _peek;           // 0 = 只剩露头，1 = 完全显示
    private readonly Anim _rail;           // 0 = 平时那条 10 像素色线，1 = 完整设置条
    /// <summary>色带常开（「设置 → 外观 → 色带常开」，2026-10-05）：一直摊着，不参与悬停收放。</summary>
    private bool _railPinned;
    /// <summary>触摸把设置条"带出来"后的保持截止（触摸没有悬停，不能靠指针位置维持展开）。</summary>
    private double _railTouchHoldUntilMs = double.NegativeInfinity;
    private bool _railHover;

    /// <summary>悬停意图的两个时刻：进热区 120ms 才展开、离开 220ms 才收回——路过不算数。</summary>
    private double _railEnterAtMs = double.NegativeInfinity;
    private double _railExitAtMs = double.NegativeInfinity;
    /// <summary>写字与色带的边界（2026-10-09 二轮硬规则）：上一帧是否在写字（边沿检测）。</summary>
    private bool _railWasDrawing;
    /// <summary>最近一笔的抬手时刻："余温"用它（抬手后 <see cref="Tokens.RailRestMs"/> 内不许开始张开）。</summary>
    private double _railStrokeEndMs = double.NegativeInfinity;
    /// <summary>写字后**必须让指针离开判定区一次**、再停回来，才允许张开（防"写完还在附近就弹卡片"）。</summary>
    private bool _railRequireLeave;
    /// <summary>最近一次"指针位置"更新的时刻（PointerMove/PointerDown 里刷）——
    /// 判定"指针真的离开过"要用它：**没有新位置就别当作离开**（落笔期间引擎不转发悬停，
    /// railHover 会是旧值；拿旧值清 requireLeave 会让规则形同虚设）。</summary>
    private double _railHoverStamp = double.NegativeInfinity;
    private bool _hoverInside;             // 指针在"看得见的那一块"里
    private double _leftAtMs = double.NegativeInfinity;
    /// <summary>触摸/笔唤出贴边面板后的"停留截止"：松手后别 0.7s 就收（手指还要再点工具）。
    /// 只对"按在面板上"的那一次触摸生效；鼠标那套不变。</summary>
    private double _peekHoldUntilMs = double.NegativeInfinity;
    /// <summary>
    /// "可以开始自动收起来了"的开关：指针碰过面板一次之后才置真。
    /// 没碰过之前一律保持完整显示——启动时不许一上来就收成屏幕底边那条露头（见 UpdatePeek）。
    /// </summary>
    private bool _peekArmed;

    /// <summary>设置子页里的行（启动器的底栏不在这张表里）。外观 / 书写 / 墨迹三列并排；
    /// `AutoStart` 排在表尾，但由 <see cref="MoreRowRect"/> 画在外观列最后一格。</summary>
    private enum Row { DarkTheme, AutoHide, RailPin, UiSize, Language, DwellShape, Pressure, Predict, TouchMode, ClearOnArrow, Privacy, RestoreInk, PptAutoSave, HistoryDays, BackupAll, RestoreAll, AutoStart, AiApi }

    /// <summary>
    /// 行表：**绘制 / 命中 / 执行 / 自检都读这一份**（本仓"同一份名单写两处必漏一处"的老毛病）。
    /// `Hint` 是标签下面那行 11px 小灰字，只有需要解释的行才给。
    /// </summary>
    private static readonly (Row Kind, string Label, bool Dangerous, bool Gray, string Hint)[] Rows =
    {
        (Row.DarkTheme, "深色主题", false, false, ""),
        (Row.AutoHide, "贴边隐藏", false, false, ""),
        // 色带常开（2026-10-05 用户实测点名）：设置条一直摊着。触摸屏上那条 10 像素的色线很难点中，
        // 触摸点工具格也会自动带出来（自适应，见 UpdateRail 的 _railTouchHoldUntilMs）。
        (Row.RailPin, "色带常开", false, false, "设置条一直摊开（触摸屏不用去碰那条色线）"),
        // 悬停提示：原来这里有一行「功能提示」开关（2026-10-02 加，默认开）——
        // **2026-10-16 用户定：开关撤除、改常开**（逻辑变成"第一次慢、之后快"，
        // 见 Tokens.TipFastDelayMs / 引擎 Tooltip.cs）。
        // ⚠ 提示的**范围仍是收窄的**：只给图标-only / 带快捷键 / 隐藏手势，别的
        //（色带、文字段、点一下就见结果的）都不配——判据见《调研-悬停提示-Tooltip.md》10.8。
        // 界面缩放（2026-10-10 用户拍板"做 A：档位式全局缩放"）：五档 0.8/0.9/1/1.15/1.3，
        // 点一下换一档、到顶回头。**缩的是界面自己**（工具带 / 色带 / 抽屉 / 设置页 / 图库面板）；
        // 笔宽、画布内容这些"纸上的尺"不跟着动。
        (Row.UiSize, "界面大小", false, false, "工具带和面板的大小；点一下换一档（0.8～1.3，五档）"),
        (Row.Language, "语言", false, false, "界面语言；切换后重启软件生效"),
        // 停顿成型（2026-09-23 第二十批，见 计划-图形工具.md §四十二）：
        // 手写一笔停住 400ms → 把它变成规整图形。**默认开**（用户定的：
        // "因为是停顿变，所以默认开"），所以这一行的开关初始就是「开」。
        // 关掉 = 以后画的那些不再参与；已经变出来的图形不受影响（那是撤销的事）。
        (Row.DwellShape, "停顿变图形", false, false, ""),
        // 压感粗细（2026-10-01，批次 0.2）：默认开；关掉 = 整块板等宽，
        // 手写板的流畅 / 预测不受影响（渲染期开关，文档里的压力数据不动）。
        (Row.Pressure, "压感粗细", false, false, "关掉后所有笔迹等宽（手写板照样流畅）"),
        // 墨迹预测（2026-10-09 回归；**2026-10-16 用户定：默认关**——"以后的软件默认不开预测"；
        // 唯一算法 = 线性预测（Chromium 同族），只剩这一个开关）。
        // 链路：行表 → ActivateRow → IsOn → SavePrefs（"开过"才写 "1"）。
        (Row.Predict, "墨迹预测", false, false, "开了让墨尾更跟手一点；默认关，想跟手就在这里打开"),
        // 「箭头清屏」（2026-11-12 用户定）：只在**桌面批注**生效——
        // 白板能翻屏、PPT/PDF 一页一存，都不需要；默认关。
        (Row.ClearOnArrow, "箭头清屏", false, false,
         "只在桌面批注时生效：点箭头进入穿透前，先清空桌面上的批注（可 Ctrl+Z 撤销）"),
        // 触摸模式（2026-10-24）：红外屏杆子/手指同源，面积橡皮取消后给老师一个
        // “只写字、完全不要手势”的保险档；默认仍是手势模式。
        (Row.TouchMode, "触摸模式", false, false,
         "手势 = 双指翻页/缩放 + 三指擦 + 长按选择；单指 = 只允许一个触点写字，完全无手势"),
        // 匿名设备统计（2026-11-14）：默认开启、不弹模态框；设置里可关，关闭即删卡。
        (Row.Privacy, "匿名设备统计", false, false,
         "只上传版本、语言、国家/地区、机型和性能；不含 IP、位置、账号、课件或屏幕内容；关闭后删除本机统计卡"),
        // AI 识别（2026-11-07）：设置里唯一的外网服务入口；配置写 %APPDATA%\InkTeach\ai.json。
        (Row.AiApi, "AI 识别 API…", false, false,
         "配置板书识别用的服务地址 / API Key / 模型；可添加多个服务商，选中一个作为当前使用"),
        // 墨迹三条偏好（原本在「墨迹」页，2026-10-02 启动器改版后搬进设置子页）。
        (Row.RestoreInk, "自动恢复上次板书", false, false, "下次启动接上这次的板书"),
        // ⚠ 名称 2026-10-10 改：这个开关**不只管 PPT**——文档（PDF/图片）批注的按页
        //   自动保存 / 装载也走它（DocImport 三处判 PptAutoSaveOn）。叫"课件"最贴：
        //   PPT 和文档都是老师的课件（"文档"在我们界面语汇里专指 PDF/图片那类）。
        (Row.PptAutoSave, "课件墨迹自动保存", false, false, "PPT 放映 / 文档打开时，批注按页自动保存、下次自动装载"),
        (Row.HistoryDays, "历史清理", false, false, "过期 PPT 缓存 / 打开前备份 / 历史板书，启动时清掉"),
        // 全局备份（2026-10-25）：只放这里，不在「更多」首页重复。
        // 备份 = 收藏 / 历史板书 / 设置 / 课件批注；不含 PDF / PPT 原文件。
        (Row.BackupAll, "备份全部数据…", false, false,
         "收藏 / 历史板书 / 设置 / 课件批注打包成 .zip；不含 PDF / PPT 原文件"),
        (Row.RestoreAll, "从备份恢复…", false, false,
         "选备份包；恢复后重启软件生效，旧数据留在 .before-restore"),
        // 开机自启：放在「外观」列最后一格（MoreRowRect 里按列排，不按 Rows 顺序画）。
        // 默认关；打开后只写 HKCU Run，不需要管理员，也不碰任务计划/服务。
        (Row.AutoStart, "开机自动启动", false, false, "登录 Windows 后在后台待命：点小球再开始批注"),
        // 板书历史本身没有开关（默认留、默认不显示）；上限是"总量 1 GB、份数不限"，
        // 见 BoardHistory。老师想清空就删 `%LOCALAPPDATA%\InkTeach\History`，或把这一行设成 7 天。
        // ⚠ 「精细笔迹」开关 2026-10-09 晚撤掉（默认开）；「触摸模式」2026-10-24
        //   恢复成“手势 / 单指书写”两档（见 Row.TouchMode），不是旧的布尔总开关。
    };

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    /// <summary>形变期间整片内容淡入用的图层（每帧现建现销，不缓存：理由见 BeginFade）。</summary>
    private ID2D1Layer _fadeLayer;

    // ---- 静态底缓存：**想清楚了再做，现在没做** -----------------------------
    //
    // 实测（--panelperf，同一份内容 200 笔）：完整展开态比不挂界面贵约 1.5～2.6 ms/帧，
    // 而归因显示阴影只占 0.37、凹槽 0.16、色片细节 0.34、图标 0.32 ——
    // 剩下的钱花在**每帧几十次小绘制调用本身**（不是填充面积）。
    // 教科书解法是"把静态底画进一张位图，每帧只贴一次"（引擎里的性能面板就是这么干的：
    // 4.3 ms → 0.05 ms）。但**这条路有个前提**：刷缓存必须在"这一帧开始画"之前做。
    //
    // 我试过在 Render() 里刷（也就是在引擎 BeginDraw 里面切换渲染目标再 BeginDraw），
    // Direct2D 不允许这么做 —— 现象是**整块面板直接变空白**（出图当场看出来的；
    // 几何与命中测试的自检全绿，因为那部分没坏）。
    // 所以它需要引擎在 `RenderFrame` 里、BeginDraw 之前开一个"界面离屏准备"的钩子
    // （引擎自己的 PrepareHud 就挂在那儿）。**先记账，不改**（见计划 §10.2）。

    public FullUi()
    {
        _expand = new Anim(0f);
        _peek = new Anim(1f);
        _rail = new Anim(0f);
        _dashFade = new Anim(1f);      // 1 = 换挡动画已经结束（平时就是新档的样子）
        _more = new Anim(0f);          // 「更多」面板：0 = 关、1 = 全开（兼作遮罩透明度）
        _moreH = new Anim(0f);         // 面板高度：换页时动画（0 = 还没量过，按目标算）
        _tipFade = new Anim(0f);       // 悬停提示的淡入
    }

    public string Name => "完整界面";
    public bool Visible => true;
    public bool IsAnimating
    {
        get
        {
            // 贴边隐藏那条"离开一会儿才收"是靠**继续要帧**实现的：
            // 没有帧就没有时机去收（引擎只在有脏区或界面说要动时才渲染）。
            if (_peek.Running) return true;
            if (_rail.Running) return true;
            if (_dashFade.Running) return true;      // 换挡那一下要把淡入淡出画完
            if (_more.Running) return true;          // 「更多」面板开合（遮罩淡入淡出跟着它）
            if (_moreH.Running) return true;         // 面板换页时的高度动画
            if (_ai.Running) return true;            // AI 面板开合
            if (_settingsFlingActive) return true;   // 设置页双指惯性
            if (_apiFetching) return true;           // AI 服务页：等模型列表返回
            // 按住清空、或者刚按完那一下的闪光：都要继续给帧，否则进度条不走、
            // 也永远到不了 0.8 秒那个点（"按住不放"这条全靠帧在推进）。
            if (ActionHolding) return true;
            if (_host != null && _host.NowMs < _actionFlashUntil) return true;
            // 色带正在"等悬停意图"时也得给帧：不然 120 毫秒到了没人去展开它，
            // 或者 220 毫秒到了没人去收它。展开/收起一旦完成，这两个条件立刻为假 → 空闲回到 0 帧。
            if (BandVisible()
                && ((_railHover && _rail.Value < 0.5f) || (!_railHover && _rail.Value > 0f)))
                return true;
            if (_hideEnabled && _peekPendingCollapse && _peek.Value > 0f) return true;
            // 悬停提示：还在等延迟（1.5s / 快路径 400ms）、或淡入没走完，都要继续给帧——
            // 不给帧的话"到点了"没人去画它。显示完之后它是静态的，不再烧帧。
            if (TipWanted && (!_tipShown || _tipFade.Running)) return true;
            // 触摸/笔长按：还在计时（到点要点亮），或提示停留期没到点（到点要收）。
            if (_tipHoldStart > double.NegativeInfinity && !_tipHoldFired) return true;
            if (_tipShown && _tipLingerUntil > 0 && _host.NowMs < _tipLingerUntil) return true;
            return _expand.Running;
        }
    }

    public void Attach(IUiHost host)
    {
        _host = host;
        EnsureUiActions();
        EnsurePageHost();
        _widgets = new Widgets(host);
        IconAtlas.Init(host.PathFactory);
        _expand.Bind(host);        // 时钟必须接真的那个（见 Anim.Bind 的注释）
        _peek.Bind(host);
        _rail.Bind(host);
        _dashFade.Bind(host);
        _more.Bind(host);
        _moreH.Bind(host);
        _ai.Bind(host);
        _tipFade.Bind(host);
        _moreH.Jump(MoreTargetH());
        _lastTool = host.State.Tool;        // **手动启动即展开**（用户 2026-09-27 定："第一次打开以后，默认就展开"）。
        //
        // 开机自启是另一回事：那时老师和学生还没开始用，工具条展开会挡桌面，
        // 所以后台待命态收成小球 + 穿透。判据同时看 StartupMode 和当前穿透，
        // 用户激活之后（穿透关掉）界面重建也不会再无故收起。
        bool startupPassive = host.StartupMode && host.State.PassThrough;
        _expand.Jump(startupPassive ? 0f : 1f);
        _peek.Jump(1f);
        _rail.Jump(0f);
        _dashFade.Jump(1f);
        _lastPpt = host.State.PptMode;
        _lastPass = host.State.PassThrough;
        _lastLibraryOpen = host.State.LibraryOpen;
        LoadPrefs();
        Layout(host.Screen, host.DpiScale);
        PushFloatingTheme();       // 浮层（操作条/小面板/旋转读数）跟着走同一套令牌
    }

    /// <summary>
    /// 第二个进程“又启动了一次”或后台待命被叫醒：把小球展开成完整工具条。
    /// 退出穿透由引擎先做（它会顺带把键盘/前台要回来），这里只负责外观。
    /// </summary>
    public void Activate()
    {
        _peek.Jump(1f);
        _expand.To(1f, Tokens.ExpandMs);
        Invalidate();
    }

    /// <summary>
    /// 把**同一套令牌**推给引擎，让选中操作条、颜色/层级/导出面板、旋转读数也用这套
    /// 颜色与投影。
    ///
    /// 为什么要有这一步：那些东西画在**引擎**的浮层上（它们跟着选区走，不属于工具条），
    /// 而颜色/投影的数字**只应该有一份**。以前引擎里另写了一份 `UiTheme.Default`，
    /// 于是浅色主题改完之后，工具条有了冷灰描边和五层投影，操作条还是蓝灰描边、
    /// 一层投影都没有——用户一眼就看出来"不是一套"。
    ///
    /// 引擎不解释偏好：谁该用深色是界面的事，界面换主题就推一次。
    /// </summary>
    private void PushFloatingTheme()
    {
        var layers = _dark ? Tokens.ShadowDark : Tokens.ShadowLight;
        var ramp = new UiShadowLayer[layers.Length];
        for (int i = 0; i < layers.Length; i++)
            ramp[i] = new UiShadowLayer(layers[i].Inflate, layers[i].Dy, layers[i].Color);

        _host.SetFloatingTheme(new UiTheme(
            panel: _dark ? Tokens.PanelDark : Tokens.PanelLight,
            panelBorder: _dark ? Tokens.BorderDark : Tokens.BorderLight,
            text: _dark ? Tokens.InkDark : Tokens.InkLight,
            textMuted: _dark ? Tokens.InkMutedDark : Tokens.InkMutedLight,
            hover: _dark ? Tokens.HoverDark : Tokens.HoverLight,
            activeBg: Tokens.Accent,
            activeText: Tokens.AccentInk,
            cornerRadius: Tokens.FloatingCorner,
            // 底沿那道"卷边"只有浅色用：深色靠"提亮表面"读层次，给它一条暗边只会显脏
            edgeBottom: _dark ? new Color4(0f, 0f, 0f, 0f) : Tokens.EdgeLightBottom,
            shadow: ramp));
    }

    // ---- 界面自己的偏好（存哪儿、怎么存由引擎负责，这里只管键的含义）------
    //
    // 四样东西能记住：深色主题、贴边隐藏、档位、钉住（取消了哪几格）。
    // **面板位置不记**——用户定的"每次启动都在固定位置"。
    // 只写"和默认不一样"的项：默认档位（完整）、默认全钉住、默认浅色不隐藏都不进配置文件
    // （以后默认值改了，老配置不会把新默认顶掉）。

    private void LoadPrefs()
    {
        _dark = _host.GetPref("dark") == "1";
        _hideEnabled = _host.GetPref("hide") == "1";
        // 色带常开（2026-10-05）：设置条一直摊着——触摸屏上不用去碰那条 10 像素的色线。
        _railPinned = _host.GetPref("railPin") == "1";
        // 悬停提示：**常开**（原「功能提示」设置 2026-10-16 撤除，用户定）。

        string prof = _host.GetPref("profile");
        _profile = prof == "mini" ? Profile.Mini
                 : prof == "custom" ? Profile.Custom
                 : Profile.Full;

        for (int i = 1; i < _pinned.Length; i++) _pinned[i] = true;
        // 「取消了哪几格」按**稳定 key** 存；老配置里的数字按 v2 顺序翻译。
        // 重排（收藏挪到截屏后）不能把老用户的"取消钉住"挪到别的格上。
        foreach (var token in (_host.GetPref("unpinned") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = null;
            if (int.TryParse(token, out int legacyCell)
                && legacyCell > 0 && legacyCell < LegacyCellKeysV2.Length)
                key = LegacyCellKeysV2[legacyCell];
            else
                key = token.Trim();
            int cell = Array.IndexOf(CellKeys, key);
            if (cell > 0 && cell < Cells.Length && CanUnpin(cell))
                _pinned[cell] = false;
        }

        // **新增格子的迁移**（2026-10-25 硬规则）：老用户不硬塞新格，
        // 内部转「自定义」、旧格全钉、新格不钉，工具条外观一个像素不变。
        // 用 `CellSinceVersion` 按身份判断，不再假设"新格一定在最后"。
        int savedVersion = int.TryParse(_host.GetPref("cellsVersion"), out int cellsVer) ? cellsVer : 1;
        if (savedVersion < CellsVersion)
        {
            for (int i = 1; i < Cells.Length; i++)
            {
                if (CellSinceVersion[i] <= savedVersion) continue;
                if (_host.State.IsFirstRun)
                {
                    _pinned[i] = true;
                }
                else
                {
                    _pinned[i] = false;
                    if (_profile == Profile.Full) _profile = Profile.Custom;
                }
            }
            SavePrefs();
        }

        // 白板底纹是**引擎状态**（画进分块缓存的），界面这边只是它的"存储器"：
        // 启动时把上次的档位推给引擎一次。数值不合法就当默认。
        // ⚠ 间距要**吸附到最近的档位**：档位表 2026-09-27 换过（原 24/40/64 → 现 20/30/40/64/96），
        //   老配置里存的 24 不在新表里，不吸附的话档位点会指错一格。
        int pat = int.TryParse(_host.GetPref("boardPattern"), out int p) ? p : 0;
        float step = float.TryParse(_host.GetPref("boardStep"), out float stepPref) ? stepPref : 40f;
        _host.Commands.SetBoardPattern(pat, SnapPatternStep(step));
        float op = float.TryParse(_host.GetPref("boardOpacity"), out float opPref) ? opPref : BoardOpacityMax;
        _host.Commands.SetBoardOpacity(op);

        // 坐标系网格也是**引擎状态**（它决定新画的坐标系带不带格），同一套做法：
        // 启动时推一次。默认**关**（见 Engine.CoordGridDefault 的注释）。
        _host.Commands.SetCoordGridDefault(_host.GetPref("coordGrid") == "1");

        // 停顿成型同样是引擎状态，而且**默认开**（用户 2026-09-23 定）。
        // 所以这里读的是"关过的"那一份：只有明确写着 "0" 才关，没有这一项就是开。
        _host.Commands.SetDwellShape(_host.GetPref("dwellShape") != "0");

        // **默认档位 = 完整**（用户 2026-10-05 定："刚开始的新软件要完整的图标那种"）。
        // 以前这里在没有"profile"偏好时按逻辑屏宽自动落极简（<1300 → 极简）——在 200% 缩放的
        // 高分屏上第一眼就少一半图标（"像缺了图"）。现在**第一眼永远是完整档**；
        // 想要极简的老师在「更多 → 档位」切一次（SavePrefs 会写 "profile"），以后永远听他的。
        // （上面读 "profile" 那一段已经把 mini / custom 读回来了；没有这一项就是完整。）
        // 课堂工具（计时/点名的预设与偏好）在引擎侧，界面不再存副本——见 Classroom.cs。
    }

    private void SavePrefs()
    {
        _host.SetPref("dark", _dark ? "1" : null);
        _host.SetPref("hide", _hideEnabled ? "1" : null);
        _host.SetPref("profile", _profile == Profile.Full ? null
                               : _profile == Profile.Mini ? "mini" : "custom");
        // 白板底纹：默认（无 / 40）不写，只写改过的
        var st = _host.State;
        _host.SetPref("boardPattern", st.BoardPattern == 0 ? null : st.BoardPattern.ToString());
        _host.SetPref("boardStep", MathF.Abs(st.BoardPatternStep - 40f) < 0.5f
                                   ? null : st.BoardPatternStep.ToString("F0"));
        _host.SetPref("boardOpacity", st.BoardOpacity >= BoardOpacityMax - 0.005f
                                      ? null : st.BoardOpacity.ToString("F2"));
        // 坐标系网格：默认关，只写"开了"这一种情况。
        _host.SetPref("coordGrid", st.CoordGridDefault ? "1" : null);
        // 停顿成型：**默认开**，所以只写"关了"这一种情况（写成 "0"）。
        _host.SetPref("dwellShape", st.DwellShapeOn ? null : "0");
        // 压感粗细：**默认开**，同样只写"关了"这一种情况（引擎启动时自己读它）。
        _host.SetPref("pressure", st.PressureOn ? null : "0");
        // （精细笔迹 / 触摸手势的偏好写入 2026-10-09 晚随开关一起撤——两项都常开，没有可写的状态。）
        // 墨迹预测：**默认关**（2026-10-16 用户定："以后的软件默认不开预测"），
        // 只写"开过"的那一种情况（写 "1"）。
        _host.SetPref("predict2", st.PredictTailOn ? "1" : null);
        // 悬停提示：**常开**（2026-10-16 起；原来的"关过才写 0"随开关撤除）。

        var off = new List<string>();
        for (int i = 1; i < _pinned.Length; i++) if (!_pinned[i]) off.Add(CellKeys[i]);
        _host.SetPref("unpinned", off.Count == 0 ? null : string.Join(",", off));
        // 色带常开：默认关，只写"开了"这一种情况。
        _host.SetPref("railPin", _railPinned ? "1" : null);
        // 主条格表版本：每次加新格都要写，老用户的迁移靠它只跑一次。
        _host.SetPref("cellsVersion", CellsVersion.ToString());
    }

    // ---- 布局 ---------------------------------------------------------------

    public RectF Layout(RectF screen, float dpiScale)
    {
        _screen = screen;
        // 工作区在**每次布局**时重取一次（屏幕/DPI 变了会走到这里，Layout 因此是天然的刷新点）。
        // 读不到或给了空矩形就退回屏幕——**绝不留下一个 0×0 的"工作区"**，
        // 那会让默认位置把面板夹到屏幕左上角。
        var w = _host?.WorkArea ?? default;
        _work = (w.MaxX > w.MinX && w.MaxY > w.MinY) ? w : screen;
        _pageHost?.SetWorkArea(_work);
        ClampSettingsScroll();
        _frameShiftValid = false;          // 屏幕/DPI 变了：旧的位移快照作废
        return QueryBounds();
    }

    /// <summary>
    /// 引擎每帧在 QueryBounds **之前**调一次：把动画/贴边隐藏/提示推进到这一帧，
    /// 然后给这一帧拍一张位移快照。
    ///
    /// 这些更新原来在 <see cref="Render"/> 开头。Render 只在一部分路径上被调用
    /// （引擎先算脏区再画），于是"位置在 Render 里变、脏区按变之前算"——
    /// 贴边隐藏 + 色板那一组残影就是从这里来的。
    /// </summary>
    public void PrepareFrame()
    {
        if (_host == null) return;
        // 全屏层（更多 / 说明）开合：收藏弹窗先藏，关掉自动回来。
        // 色带 / 取色器只是底部临时展开，**不影响收藏弹窗**。
        _host.Commands.SetUiOverlay(_moreOpen || _aiOpen || _more.Running || _ai.Running);
        StepSettingsFling();             // 设置页双指纵滑的惯性（和白板同一套衰减）
        StepApiFetch();                  // AI 服务页：模型列表异步返回后回到 UI 线程
        FinishMoreCloseIfDone();         // 关闭动画结束后再清 PageHost（避免闪回 Home）
        UpdatePeek();                    // 贴边隐藏该不该收
        UpdateRail();                    // 色线该不该长成设置条
        UpdateBandAction();              // "按住清空"够 0.8 秒没有
        UpdateTip();                     // 悬停提示"到点没有"
        _frameShift = ComputeShift();
        _frameShiftValid = true;
    }

    public RectF QueryBounds()
    {
        // 「更多」面板打开（含开合动画）时：我们占**整块屏幕**——这是全屏模态的根
        //（命中测试、裁剪、接输入小窗都按这份矩形走，理由见「更多」面板那一节）。
        // 关掉（动画也跑完）立刻回到主条那一条，一帧都不多占。
        if (_moreOpen || _more.Running) return _screen;
        // AI 面板也是全屏模态（遮罩 + 居中卡片），占用整屏。
        if (_aiOpen || _ai.Running) return _screen;

        // 占用矩形必须跟着"实际画出来的东西"走：贴边隐藏时它就只剩露头那一条，
        // 输入小窗也跟着缩——这样指针扫过露头才算"碰到面板"，其余位置照旧穿透/画线。
        var u = BaseBoundsLocal();
        // **粗细预览会画到面板外面**（带子那一侧的上/下方）：可见范围也得跟出去，
        // 否则引擎会把超出 QueryBounds 的部分裁掉（引擎是按这份矩形做裁剪的），
        // 表现就是"预览没画出来"。它只在拖滑条/悬停滑条时出现，平时这份矩形不变。
        if (SizePreviewVisible && _host != null) u = Union(u, SizePreviewRect(_host.State));
        if (_penPickerOpen) u = Union(u, PenPickerRect());
        if (_textFontMenuOpen) u = Union(u, TextFontMenuRect());
        if (_aiMenuOpen) u = Union(u, AiMenuRect());
        return FinishBounds(u);
    }

    /// <summary>
    /// **主条本身**的占用矩形（不含下拉/动作菜单/粗细预览这些临时弹层）。
    /// 收藏面板定位只认它：否则一开菜单，面板会跟着弹层往上跳（见 IOverlayUi.ChromeBounds）。
    /// </summary>
    public RectF ChromeBounds => FinishBounds(BaseBoundsLocal());

    public RectF BarBounds => FinishBounds(BarRect());

    private RectF BaseBoundsLocal() => UnionRect();

    /// <summary>把本地矩形平移到屏幕坐标、夹进屏幕，并按露头状态收短。</summary>
    private RectF FinishBounds(RectF u)
    {
        var s = Shift();
        u = new RectF
        {
            MinX = u.MinX + s.X, MinY = u.MinY + s.Y,
            MaxX = u.MaxX + s.X, MaxY = u.MaxY + s.Y,
        };
        // 露头必须留在屏幕内（绝不把窗口挪出屏幕来实现隐藏——那样就再也收不到输入了）
        u.MinX = Math.Max(u.MinX, _screen.MinX);
        u.MinY = Math.Max(u.MinY, _screen.MinY);
        u.MaxX = Math.Min(u.MaxX, _screen.MaxX);
        u.MaxY = Math.Min(u.MaxY, _screen.MaxY);

        // 收起态贴边隐藏：露出来的那一条**画成一条短把手**（见 DrawPeekTab），
        // 所以"点得到的地方"也得跟着收短——不然把手两边各留一截看不见、
        // 却会吃掉点击的空地（"看得见的地方就点得到"，这句要两头都成立）。
        if (PeekTabShown && _expand.Value < 0.5f)
        {
            bool horiz = (u.MaxX - u.MinX) >= (u.MaxY - u.MinY);
            float len = MathF.Min(Tokens.PeekTab, horiz ? u.MaxX - u.MinX : u.MaxY - u.MinY);
            if (horiz)
            {
                float c = (u.MinX + u.MaxX) * 0.5f;
                u.MinX = c - len * 0.5f; u.MaxX = c + len * 0.5f;
            }
            else
            {
                float c = (u.MinY + u.MaxY) * 0.5f;
                u.MinY = c - len * 0.5f; u.MaxY = c + len * 0.5f;
            }
        }
        return u;
    }

    /// <summary>
    /// 贴边隐藏是不是已经沉到"该改用把手"的地步了。
    ///
    /// 判据是**露出来多厚**：露头 = 8 ＋ 40 × peek（40 = 球的直径 − 露头），
    /// 露出不到 12 像素就换。换早了不行——球里的色环和笔图标会提前消失，
    /// 那正是用户点过名的"跳"。
    /// </summary>
    /// <summary>
    /// 贴边隐藏"这一刻真的在起作用"吗：开关开着，而且面板**够得到屏幕底边**
    ///（判据和 <see cref="Shift"/> 完全同一份：**只有底边才藏**，左右/上边一律不藏）。
    ///
    /// ⚠ 这一条 2026-10-01 补：以前 `PeekTabShown` 只看 `_peek` 收没收，于是把球拖到
    /// **左/右边缘**（那里 `Shift` 恒为 0、根本藏不动）时，球会被画成那条"露头把手"
    /// ——一个**纯笔色的实心圆**，而且永远"吸不进去"（用户报的正是这个）。
    /// </summary>
    private bool DockHideEngaged =>
        _hideEnabled && (_screen.MaxY - UnionRect().MaxY) <= Tokens.DockHideDistance;

    private bool PeekTabShown =>
        DockHideEngaged && Tokens.DockPeek + (Tokens.Ball - Tokens.DockPeek) * _peek.Value <= 12f;

    /// <summary>
    /// 现在是"展开的条"还是"球"。0.5 这条线全工程共用（贴边隐藏要不要收、内容画哪一套）。
    /// </summary>
    private bool Expanded => _expand.Value > 0.5f;

    /// <summary>
    /// 画到占用矩形**外面**的那一圈（投影），告诉引擎别把它裁掉。
    /// 只影响裁剪与脏区，**不参与命中测试**——所以面板旁边照样能画线。
    /// </summary>
    /// <summary>
    /// 界面会画到占用矩形外面的那一圈 = 投影最远胀到多少；
    /// **悬停提示有可能要更大**：它画在面板外面（上方或下方），
    /// 所以"停在一个有提示的东西上"期间临时把余量放大（`Tokens.TipPaintMargin`）。
    /// 只影响裁剪与脏区，不参与命中测试——不会出现"提示旁边点不动"。
    /// </summary>
    public float PaintMargin
        => TipWanted || _tipFade.Value > 0f ? Tokens.TipPaintMargin : Tokens.PaintMargin;

    private RectF BarRect()
    {
        var a = Anchor();
        return new RectF { MinX = a.X, MinY = a.Y, MaxX = a.X + Width(), MaxY = a.Y + Tokens.BarHeight };
    }

    /// <summary>上带：贴在主条下面 4 像素。高度随展开动画长出来（不做第二套动画）。</summary>
    private RectF BandRect()
    {
        var bar = BarRect();
        float h = BandHeightFull() * BandProgress();
        // 带子**朝屏幕中心那一侧**长：
        //   · 面板贴底（默认位置）→ 带子在上面：底边锚定不动，你刚点的按钮位置也不动
        //     （这是假面板当年的做法，代码注释写着"贴底时下面没有空间"）；
        //   · 面板拖到上半屏 → 带子在下面，朝内容长。
        // 一开始我写成"永远在主条下面"，那是**不经意的偏差**：面板底边锚定 + 带子在下面
        // = 展开时按钮带整体上跳 38 像素，正是设计文稿里点过名的"切工具会跳"。
        if (BandAbove())
            return new RectF
            {
                MinX = bar.MinX, MinY = bar.MinY - BandGap - h,
                MaxX = bar.MaxX, MaxY = bar.MinY - BandGap,
            };
        return new RectF
        {
            MinX = bar.MinX, MinY = bar.MaxY + BandGap,
            MaxX = bar.MaxX, MaxY = bar.MaxY + BandGap + h,
        };
    }

    /// <summary>
    /// 色带和主条之间**不留缝**：假面板里它们是**同一块面板**（一个圆角包住两行），
    /// 我原来做成两张分开的卡片 ＋ 4 像素缝，看着就是"两个叠起来的药丸"，
    /// 不如它整块（用户 2026-09-16 提的"色带不如假面板美观"，主要就是这一条）。
    /// </summary>
    private const float BandGap = 0f;
    private float BandProgress() => _expand.Value;

    /// <summary>上带这一刻的高度：平时 10 像素的色线，碰到了长成 34 像素的设置条。</summary>
    private float BandHeightFull() =>
        Tokens.BandLine + (BandHeightLogical() - Tokens.BandLine) * _rail.Value;

    /// <summary>
    /// 这一格的上带**完全张开**时有多高（逻辑像素）。
    ///
    /// 2026-11-02：图形改成独立面板后，所有上带统一 34，不再有“图形那一格三行”的特例。
    /// 带子仍朝屏幕中心那一侧长（见 <see cref="BandRect"/>），不会把主条顶走。
    /// </summary>
    private float BandHeightLogical() => Tokens.BandHeight;

    /// <summary>
    /// 上带**完全张开**时那一整块占多高（含它与主条之间的缝）——
    /// 夹取屏幕、判定区都问它（见 <see cref="BandHeightLogical"/> 那条注释）。
    /// </summary>
    private float BandBlockFull() => BandGap + BandHeightLogical();

    /// <summary>上带**这一刻**占多高（跟着"展开"那一档动画长/收）。</summary>
    private float BandBlockNow() => BandBlockFull() * BandProgress();

    /// <summary>色线 / 设置条：数值够大了才按"设置条"那套画与命中（中间态归短的这边）。</summary>
    private bool RailOpen => _rail.Value >= 0.5f;

    /// <summary>上带这一刻是不是真的画出来了（长出来之前不参与命中）。</summary>
    private bool BandVisible() => BandProgress() > 0.6f;

    /// <summary>
    /// 上带这一刻是不是**张开成了设置条**：张开完成、而且不在穿透里。
    ///
    /// 穿透里永远不成立（2026-10-02 用户口径）：点穿透后色带只是**收回平时那条
    /// 10 像素色线**，不是消失——面板高度不变、贴边隐藏露出来的还是它；
    /// 但绝不像别的格子那样张着设置条（穿透没有设置可放）。
    /// 绘制那几处也跟着它让路：折叠动画进行到一半时旧内容就已经点不到了。
    /// </summary>
    private bool BandOpen() => BandVisible() && RailOpen && !_host.State.PassThrough;

    /// <summary>这一格属于第几组（分隔线画在"组变了"的两个相邻格之间）。</summary>
    private static int GroupOf(int cell)
    {
        for (int i = 0; i < GroupEnds.Length; i++) if (cell <= GroupEnds[i]) return i;
        return GroupEnds.Length - 1;
    }

    /// <summary>带子完全展开时的宽。**按这一刻显示的格子算**，改档位/格数都不用动数字。</summary>
    private float ExpandedWidth()
    {
        var vis = VisibleCells();
        float w = Tokens.BarPad * 2f;
        for (int k = 0; k < vis.Length; k++)
        {
            w += Tokens.Button;
            if (k + 1 < vis.Length)
                w += GroupOf(vis[k]) == GroupOf(vis[k + 1])
                    ? Tokens.GapInGroup : Tokens.GroupDivider;
        }
        return w;
    }

    private float Height() => Tokens.BarHeight;

    /// <summary>整个面板现在有多高（主条 ＋ 上带）。默认位置按它算，所以是往上长。</summary>
    private float TotalHeight() => Tokens.BarHeight + BandBlockNow();

    private float Width()
    {
        float e = _expand.Value;
        return Tokens.Ball + (ExpandedWidth() - Tokens.Ball) * e;
    }

    /// <summary>
    /// 左上角。没拖过就贴在**下边居中**（离边 12）；拖过就用拖到的位置。
    /// 拖动只改位置、不改尺寸——两套动画互相拉扯是最难查的一类怪相。
    /// </summary>
    private Vector2 Anchor()
    {
        float w = Width();
        float h = TotalHeight();
        var a = RawAnchor();
        return Clamp(a, w, h);
    }

    /// <summary>
    /// 主条左上角。**锚的是"展开后那条带子的左端"**——球就长在这个位置上，
    /// 所以开合之间**球一动不动**（点它展开、再点它收起，不用重新瞄；费茨定律）。
    ///
    /// 没拖过 → 那条**展开后的带子贴着工作区底边居中**（`x = 工作区中心 - 展开宽度/2`），
    /// 于是收起时球停在屏幕中心**偏左**（差半个带子宽），点开以后整条带子正好居中。
    /// **这就是假面板当年的做法**（`MockWindow.ApplyLayout`：
    /// `_anchorLeft = wa.Left + (wa.Width - BarContentWidth)/2`，
    /// 注释写着"锚的是面板自己的左下角，所以抽屉展开时窗口往左上长、面板本身不动"）。
    ///
    /// **为什么用工作区而不是屏幕**（用户 2026-09-27）：他说的是"贴着任务栏上方一点点、
    /// 两者不重叠"。工作区就是"屏幕减掉任务栏"那块，按它算出来的位置天然满足这一条；
    /// 按屏幕底算的话，面板会**压在任务栏上**（覆盖层置顶，任务栏挡不住它，而且面板矩形
    /// 是"我们的地盘"、那一块的任务栏也就点不到了）。
    /// 任务栏在底部/左侧/顶部，工作区都会跟着让开，不用为它单写分支。
    ///
    /// 为什么不是"球居中、往两边长"（中间试过一版）：那样球会随着宽度滑走，
    /// 收起时要重新找它。为什么不是"球居中、只往右长"：展开后整条带子会偏到右边去。
    ///
    /// 贴到右边怎么办：球拖到右边缘时，往右长会顶出屏幕，由 <see cref="Clamp"/>
    /// 把**整条带子夹回屏幕内**（看着就是"贴住右边、往左铺开"）。
    ///
    /// 纵向：主条的上边为准（带子长在上面，贴底时按钮不会跳）。
    /// </summary>
    private Vector2 RawAnchor()
    {
        float top = _anchor?.Y ?? (_work.MaxY - Tokens.EdgeMargin - Tokens.BarHeight);
        // 没拖过：按**展开后的宽度**居中（不是当前宽度）——这样收起态和展开态
        // 左右两端都不会跳，只在"整条带子"这一级对齐。
        float x = _anchor?.X
                ?? _work.MinX + (_work.MaxX - _work.MinX - ExpandedWidth()) * 0.5f;
        return new Vector2(x, top);
    }

    /// <summary>
    /// 带子长在哪一侧：**朝屏幕中心**。面板在下半屏就朝上长（贴底时下面本来也没空间），
    /// 拖到上半屏就朝下长。判据用"主条中心 vs 屏幕中心"，不管面板怎么拖都成立。
    /// </summary>
    private bool BandAbove()
    {
        var a = RawAnchor();
        float center = a.Y + Tokens.BarHeight * 0.5f;
        return center >= (_screen.MinY + _screen.MaxY) * 0.5f;
    }

    /// <summary>夹在可见区域内（一期就夹在单块屏里，跨屏怎么画还没验证过）。</summary>
    private Vector2 Clamp(Vector2 a, float w, float h)
    {
        // 夹取要按**整个面板**（主条 ＋ 带子）算：a 是主条左上角，
        // 带子在上面时整块的顶边在 a.Y 之上。带子那一块的高度**问 `BandBlockNow`**、
        // 不写字面量：图形那一格是三行，写一行高会让整块被夹进屏幕里一格（见那处的注释）。
        float bandH = BandBlockNow();
        float top = BandAbove() ? a.Y - bandH : a.Y;
        float bottom = top + h;

        float x = Math.Clamp(a.X, _screen.MinX + Tokens.DockGap,
                                    _screen.MaxX - Tokens.DockGap - w);
        float y = a.Y;
        if (top < _screen.MinY + Tokens.DockGap) y += _screen.MinY + Tokens.DockGap - top;
        if (bottom > _screen.MaxY - Tokens.DockGap) y -= bottom - (_screen.MaxY - Tokens.DockGap);
        return new Vector2(x, y);
    }

    /// <summary>
    /// 第 pos 个**显示出来**的格子的矩形（逻辑坐标，pos 是"当前档位里的序号"）。
    /// 切档换的是这份列表，绘制与命中都走它，所以不会出现"画一套、点另一套"。
    /// </summary>
    private RectF CellRect(int pos)
    {
        var vis = VisibleCells();
        if (pos < 0 || pos >= vis.Length) return RectF.Empty;

        var a = Anchor();
        float x = a.X + Tokens.BarPad;
        for (int k = 0; k <= pos; k++)
        {
            if (k == pos)
                return new RectF { MinX = x, MinY = a.Y, MaxX = x + Tokens.Button, MaxY = a.Y + Height() };
            x += Tokens.Button;
            x += GroupOf(vis[k]) == GroupOf(vis[k + 1]) ? Tokens.GapInGroup : Tokens.GroupDivider;
        }
        return RectF.Empty;
    }

    /// <summary>某一格（按完整档的下标）在当前位置里的序号；不在这一档就是 -1。</summary>
    private int PosOf(int cell)
    {
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++) if (vis[k] == cell) return k;
        return -1;
    }

    /// <summary>收起态那个球（和带子第一格同一个位置，来回都不用重新瞄准）。</summary>
    private RectF BallRect()
    {
        var a = Anchor();
        return new RectF { MinX = a.X, MinY = a.Y, MaxX = a.X + Tokens.Ball, MaxY = a.Y + Tokens.Ball };
    }

    // ---- 上带（当前工具的设置条）-------------------------------------------
    //
    // 一条规则贯穿到底：**上带显示"现在这个按钮的设置"**。
    // 笔/荧光笔 = 色片（按宽度档 4/6/8 个）＋ 粗细；激光 = 光点大小；橡皮 = 整笔/面积 ＋ 大小；
    // 选择 = 矩形/套索；白板 = 三种板色；图形 = 整块图形面板（见 ShapeRows）。
    // 没有设置项的（后撤/重做/更多/截屏）就不长上带——不做一排空按钮。

    private float BandCenterY() => (BandRect().MinY + BandRect().MaxY) * 0.5f;
    private float BandContentLeft() => BandRect().MinX + BarInset();

    private RectF SwatchRect(int i)
    {
        // **按可用宽度铺满，不再固定方块**。5 格 4 个色片约 33×26，
        // 4 格用粗细按钮，3 格减到 3 个色片；色片之间只留 4 的缝，
        // 看起来是一条连续的色带，而不是四个孤零零的小方块。
        // 极简档曾经试过 26×26 方块，留下的空隙太大，用户 2026-10-20 否掉了。
        var band = BandRect();
        int n = SwatchCount;
        float avail = band.MaxX - band.MinX - BarInset() * 2f
                    - (BandHasSlider ? SliderTrackW + 14f : 0f)    // 给右端的粗细滑条让位
                    - (BandHasWidthButton ? WidthButtonW + WidthButtonGap : 0f) // 4 格以下的粗细按钮
                    - (BandHasDashToggle ? DashToggleW + DashToggleGap : 0f)   // 给虚实线那一格让位
                    - TextSizeReserve                              // 文本：右端 A− / 字号 / A+
                    - ActionReserve;                                // 给最右端的动作按钮让位
        float h = SwatchHeight();
        float gap = 4f;
        float w = MathF.Max(18f, (avail - gap * (n - 1)) / n);
        float x = band.MinX + BarInset() + i * (w + gap);
        float y = BandCenterY() - h * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>上带左右的内边距（照假面板的 inset = 16）。</summary>
    private static float BarInset() => 16f;

    /// <summary>色片的高：最多 26，带子矮的时候按比例缩（假面板：min(26, band * 0.8)）。</summary>
    private float SwatchHeight() => MathF.Min(Tokens.Swatch, BandHeightFull() * 0.8f);

    /// <summary>
    /// 虚实线那一格的宽（逻辑像素）：完整档 46 = 一小段线 + 左右各留 8；
    /// 8～11 格收成 28×26 的方形（只画一小段样本线），极简档直接不出现。
    /// </summary>
    private float DashToggleW => Density == BandDensity.Full ? 46f : 28f;
    /// <summary>它和左边色片、右边滑条之间的缝。</summary>
    private const float DashToggleGap = 8f;

    /// <summary>
    /// **虚实线切换那一格**：从滑条左边往左让出"一格 + 一道缝"算出来。
    ///
    /// 为什么锚在滑条上、不锚在色片上：滑条是**贴着面板右沿**摆的（位置由 BarInset 定死），
    /// 所以从它往左量出来的这一格**不会随色片个数变**——极简档 4 个色片、完整档 8 个，
    /// 它都不会跳。色片那边再让开同一对常量（见 <see cref="SwatchRect"/>），两边同源。
    /// </summary>
    private RectF DashToggleRect()
    {
        var band = BandRect();
        float cy = (band.MinY + band.MaxY) * 0.5f;
        float right = SliderRect().MinX - DashToggleGap;
        return new RectF
        {
            MinX = right - DashToggleW, MinY = cy - Tokens.SegmentHeight * 0.5f,
            MaxX = right, MaxY = cy + Tokens.SegmentHeight * 0.5f,
        };
    }

    /// <summary>指针在不在那一格上（**画与命中同源**，和别的格子一个规矩）。</summary>
    private bool HitDashToggle(float x, float y)
        => BandHasDashToggle && RailOpen && DashToggleRect().Contains(x, y);

    private bool HitWidthButton(float x, float y)
        => BandHasWidthButton && RailOpen && WidthButtonRect().Contains(x, y);

    /// <summary>
    /// 粗细滑条的轨道宽（逻辑像素）。**先缩间距/滑条，再减颜色**：
    /// 极简 110、8～9 格 118、10 格以上 132。滑条短了但档位吸附还在，
    /// 不会因为省宽度把滑条挤没。
    /// </summary>
    private float SliderTrackW => Density switch
    {
        // 5 格极简先缩滑条；6–7 格仍有 110 的空间。
        BandDensity.Minimal => VisibleCount() <= 5 ? 64f : 110f,
        BandDensity.Compact => 118f,
        _ => 132f,
    };

    /// <summary>
    /// 4 格自定义时，橡皮带让位给“面积擦 / 整笔擦 / 清空”三样：
    /// 连 30px 的粗细按钮也不画，避免把两个模式段压到 20 多像素
    /// （2026-10-25 用户定：4 格砍掉滑条，保清空）。
    /// </summary>
    private bool EraserSizeHidden => _bandCell == 6 && VisibleCount() <= 4;

    /// <summary>
    /// 4 格及以下不再塞滑条，改成一颗“粗细”按钮：点一下循环当前工具的有效档位。
    /// 3 格时色片也降到 3 个，保证每个仍有可点的宽度。
    /// </summary>
    private bool BandHasWidthButton =>
        !EraserSizeHidden && (_bandCell is 3 or 4 or 5 or 6) && VisibleCount() <= 4;

    private const float WidthButtonW = 30f;
    private const float WidthButtonGap = 8f;

    private RectF WidthButtonRect()
    {
        var band = BandRect();
        float h = SwatchHeight();
        float right = band.MaxX - BarInset() - ActionReserve;
        return new RectF
        {
            MinX = right - WidthButtonW,
            MinY = (band.MinY + band.MaxY) * 0.5f - h * 0.5f,
            MaxX = right,
            MaxY = (band.MinY + band.MaxY) * 0.5f + h * 0.5f,
        };
    }

    /// <summary>
    /// 粗细滑条：**长在设置条里面、靠右端**（用户 2026-09-17："笔、荧光笔的调节大小
    /// 还是放到色带上来"）。
    ///
    /// 它原来在**面板最下沿那一整条**上（照假面板的 groove）。放在那儿有两个问题：
    ///   ① 和工具格的下半截重叠，命中顺序得靠"滑条先判"这种技巧兜着；
    ///   ② 它离"设置"这件事太远——老师看到的是"面板底下有条槽"，不知道它是干什么的。
    /// 挪进设置条之后，色片（颜色）和滑条（粗细）在同一行里，就是"这个工具的设置"。
    ///
    /// 命中区上下各给 9 像素余量（手指比鼠标难瞄）。
    /// </summary>
    private RectF SliderRect()
    {
        var band = BandRect();
        float right = band.MaxX - BarInset() - ActionReserve;   // 最右端留给动作按钮
        float cy = (band.MinY + band.MaxY) * 0.5f;
        return new RectF
        {
            MinX = right - SliderTrackW, MinY = cy - 9f,
            MaxX = right, MaxY = cy + 9f,
        };
    }

    private RectF SegmentRect(int i, int count)
    {
        var band = BandRect();
        // 和色片一样：右端有滑条的时候要给滑条让位，否则分段会和滑条叠在一起
        float total = band.MaxX - band.MinX - BarInset() * 2
                    - (BandHasSlider ? SliderTrackW + 14f : 0f)
                    - (BandHasWidthButton ? WidthButtonW + WidthButtonGap : 0f)
                    - ActionReserve;
        // 白板那一格：**按内容定宽的几段**（见 BoardBand），放不下就整体等比缩。
        // 原来这里是"5 段固定 70 宽"，结果极简档那条带子只有 315 宽，5×70 直接把
        // 色带撑爆（滑条压在黑板上、页码被挤出面板）——2026-09-27 重排时修掉。
        if (_bandCell == 2)
        {
            var segs = BoardBand;
            // ⚠ 也要给最右端的动作按钮（关闭白板那个 ✕）让位——漏了这一项的话，
            //    段会铺到按钮底下去（2026-09-27 加关闭按钮时补的）。
            float avail = band.MaxX - band.MinX - BarInset() * 2
                        - (BandHasSlider ? SliderTrackW + 14f : 0f)
                        - ActionReserve;
            float need = BoardBandWidth(segs);
            float k = need <= 0f || avail >= need ? 1f : avail / need;   // 放不下等比缩，绝不越界
            float sx = BandContentLeft();
            for (int j = 0; j < i && j < segs.Length; j++)
                sx += (BoardSegW(segs[j].Kind) + BoardSegGap(segs[j].Kind, segs[j + 1].Kind)) * k;
            float bw = (i < segs.Length ? BoardSegW(segs[i].Kind) : 0f) * k;
            float by = BandCenterY() - Tokens.SegmentHeight * 0.5f;
            return new RectF { MinX = sx, MinY = by, MaxX = sx + bw, MaxY = by + Tokens.SegmentHeight };
        }
        // **图形那一格是好几行**（2026-09-20 第十三批起是 3 行，见 `ShapeRows`）。
        // 行/列从"这一段排第几"推出来（`ShapeSegmentRow` / `ShapeSegmentCol`），
        // 每行的段宽各自按"可用宽度 ÷ **本行**段数"算——所以 4 段的曲线那行反而更宽。
        // 整块**以带子中线为中心**上下摊开（N=2 时就是老写法"各让出半个行间距"，
        // 所以那一版的位置一个像素没动），行间距和段间距是同一个 6。
        if (_bandCell == ShapeCell)
        {
            int row = ShapeSegmentRow(i);
            int col = ShapeSegmentCol(i);
            int cols = ShapeRowCount(row);
            int rows = ShapeBandRows;
            float sw = Math.Min(120f, (total - (cols - 1) * 6f) / cols);
            float sx = BandContentLeft() + col * (sw + 6f);
            float rowsTop = BandCenterY()
                          - (rows * Tokens.SegmentHeight + (rows - 1) * ShapeRowGap) * 0.5f;
            float sy = rowsTop + row * (Tokens.SegmentHeight + ShapeRowGap);
            return new RectF { MinX = sx, MinY = sy, MaxX = sx + sw, MaxY = sy + Tokens.SegmentHeight };
        }
        float w = Math.Min(120f, (total - (count - 1) * 6f) / count);
        float x = BandContentLeft() + i * (w + 6f);
        float y = BandCenterY() - Tokens.SegmentHeight * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + Tokens.SegmentHeight };
    }

    /// <summary>图形那一格两行之间的间距（也是段与段之间的 6，见 <see cref="SegmentRect"/>）。</summary>
    private const float ShapeRowGap = 6f;

    // ---- 白板那一格的色带（2026-09-27 重排）--------------------------------
    //
    // 这一格原来只有 `[上一屏][白][绿][黑][下一屏]` ＋ 右边一块"第 N 屏"文字，
    // 段宽**写死 70**。用户 2026-09-27 提了两件事，外加顺带查出来的一件老 bug：
    //   ① "上下翻页和页码应该挨着？" —— 原来是上一屏在最左、下一屏在第 4 段之后，
    //      中间被三个板色劈开，页码孤零零贴在"下一屏"右边
    //      → 改成 `[‹] 第 N 屏 [›]` **三件挨着**，摆在带子最左；
    //   ② "调整透明的的滑块似乎后面跟着一个小圆点？" —— 那个点是**笔宽预览点**，
    //      拿的还是当前笔色（所以在白板上是一颗红点），这一格它没有意义
    //      → 白板不画它（见 DrawBandSlider）；
    //   ③ 写死的 5×70 在**极简档**（带子只有 315 宽）会把色带撑爆：滑条压在黑板上、
    //      页码被挤出面板（现场就是出图 `reports/_b-mini.png`）
    //      → 段宽改成"按内容定宽 ＋ 放不下就等比缩"，并且放不下时退到紧凑布局。
    //
    // 同时把「更多」抽屉里的「白板底纹 / 底纹间距」搬了过来（用户："我现在要把更多里面的
    // 白板底纹，底纹间距这些设置移动到白板的展开色带里面"）。它们本来就是**循环档**
    // （点一下换下一档），搬过来保持这个手感，另外补上"一共几档、现在第几档"的档位点。
    // ⚠ 只搬**界面入口**：引擎接口 `SetBoardPattern` 和偏好键 `boardPattern/boardStep`
    //   一个没动，所以**存档格式不变**。

    /// <summary>白板色带上的一段是干什么的。**名单只有这一份**：布局 / 绘制 / 命中 / 激活都问它。</summary>
    private enum BoardSegKind { PageUp, PageLabel, PageDown, Color, Pattern, Step }

    /// <summary>
    /// 白板色带的段表。`Idx` 只有 <see cref="BoardSegKind.Color"/> 用得上
    /// （0/1/2 ＝ 白/绿/黑，就是 `InkPalette.BoardPresets` 的下标）。
    ///
    /// 两份布局是**同一套逻辑的两种裁剪**，不是两套代码：
    ///   · 完整：翻页器 ＋ 板色 ＋ 底纹 ＋ 间距（带子够宽时用这份）；
    ///   · 紧凑：板色 ＋ 底纹 ＋ 间距（极简档用——用户 2026-09-27 定的原话：
    ///     "翻页不要，保留黑白色，底纹 间距"）。
    /// </summary>
    private static readonly (BoardSegKind Kind, int Idx)[] BoardBandFull =
    {
        (BoardSegKind.PageUp, 0), (BoardSegKind.PageLabel, 0), (BoardSegKind.PageDown, 0),
        (BoardSegKind.Color, 0), (BoardSegKind.Color, 1), (BoardSegKind.Color, 2),
        (BoardSegKind.Pattern, 0), (BoardSegKind.Step, 0),
    };
    private static readonly (BoardSegKind Kind, int Idx)[] BoardBandCompact =
    {
        (BoardSegKind.Color, 0), (BoardSegKind.Color, 1), (BoardSegKind.Color, 2),
        (BoardSegKind.Pattern, 0), (BoardSegKind.Step, 0),
    };

    /// <summary>
    /// 每一段多宽（逻辑像素）。**按内容给**、不平摊：翻页那两个箭头只要一格窄的，
    /// "第 N 屏"和"底纹 / 间距"要放得下两三个字。
    /// </summary>
    private static float BoardSegW(BoardSegKind k) => k switch
    {
        BoardSegKind.PageUp or BoardSegKind.PageDown => 34f,
        BoardSegKind.PageLabel => 58f,
        BoardSegKind.Color => 44f,        // 用户 2026-09-27："你把颜色缩短，颜色不用那么宽"
        _ => 58f,                          // 底纹 / 间距
    };

    /// <summary>
    /// 段与段之间的缝。**翻页器那三件贴紧（2）**，其余 6；组与组之间让开 12
    /// ——"第 N 屏"夹在两个箭头中间不贴紧的话，看着还是三块东西，不是"一个翻页器"。
    /// </summary>
    private static float BoardSegGap(BoardSegKind a, BoardSegKind b) => (a, b) switch
    {
        (BoardSegKind.PageUp, BoardSegKind.PageLabel) => 2f,
        (BoardSegKind.PageLabel, BoardSegKind.PageDown) => 2f,
        (BoardSegKind.PageDown, BoardSegKind.Color) => 12f,
        (BoardSegKind.Color, BoardSegKind.Pattern) => 12f,
        _ => 6f,
    };

    /// <summary>这一份段表一共要占多宽（含缝）。</summary>
    private static float BoardBandWidth((BoardSegKind Kind, int Idx)[] segs)
    {
        float w = 0;
        for (int i = 0; i < segs.Length; i++)
        {
            w += BoardSegW(segs[i].Kind);
            if (i + 1 < segs.Length) w += BoardSegGap(segs[i].Kind, segs[i + 1].Kind);
        }
        return w;
    }

    /// <summary>
    /// 完整布局**最少要占这么多比例的可用宽度**才还留着它，否则退到紧凑布局。
    ///
    /// 为什么不写成"放不下才退"（也就是比例 = 1.0）：完整档 623 宽时可用 445，而完整布局
    /// 要 420——只有 25 的余量。自定义档**只要取消钉住一格**（少 ~44 宽）就会掉到 420 以下，
    /// 那一格就会突然从"有翻页器、有透明度滑条"跳成"都没有"，看着像个 bug。
    /// 让它在 85% 以上都还走完整布局（差的那点靠 `SegmentRect` 里的等比缩补上），
    /// 就平滑多了；再窄下去文字该碰边了，那时候退紧凑才是对的。
    /// </summary>
    private const float BoardBandMinSqueeze = 0.85f;

    /// <summary>
    /// 白板这一格用**紧凑**布局吗？
    ///
    /// 判据是**真的量一下放不放得下**（不是写死"极简档"）：完整布局要占"段总宽 ＋ 滑条 ＋
    /// 最右端那个动作按钮（关闭白板 ✕，2026-09-27 加）＋ 两侧内边距"，放不下就退到紧凑布局。
    /// 这样"自定义档里把格子取消钉得只剩几格"也照样不会撑爆——那正是这一格原来坏掉的根因。
    /// ⚠ 量的是 <see cref="ExpandedWidth"/>（展开后的整条宽），不是当前动画中的宽：
    ///    跟着动画走的话，色线张开到一半就会从完整布局跳成紧凑布局，看着像闪了一下。
    /// ⚠ 滑条那一项这里**写死常量、不走 `BandHasSlider`**：那个属性要先问 `BoardCompactBand`，
    ///    反过来问它就是自己咬自己（无限递归）。完整布局下滑条一定在（见 `BandHasSlider`），
    ///    所以写死是对的。动作按钮那边可以直接用 `ActionReserve`——它只看 `_bandCell`，不回环。
    /// </summary>
    private bool BoardCompactBand =>
        ExpandedWidth() - BarInset() * 2f - (SliderTrackW + 14f) - ActionReserve
        < BoardBandWidth(BoardBandFull) * BoardBandMinSqueeze;

    private (BoardSegKind Kind, int Idx)[] BoardBand =>
        BoardCompactBand ? BoardBandCompact : BoardBandFull;

    /// <summary>第 i 段是什么（越界给"只读文字"那种无动作的，省得调用处判空）。</summary>
    private (BoardSegKind Kind, int Idx) BoardSegAt(int i)
    {
        var segs = BoardBand;
        return i >= 0 && i < segs.Length ? segs[i] : (BoardSegKind.PageLabel, 0);
    }

    /// <summary>
    /// 白板那两格（底纹 / 间距）各有几档、现在第几档（给 <see cref="DrawPips"/> 用）。
    /// 和图形那一格的 <see cref="PipsOf"/> 同一个意思，只是这两格不是"工具"、是白板的状态。
    /// </summary>
    private static (int Count, int Current) BoardPips(BoardSegKind kind, in UiState st)
    {
        if (kind == BoardSegKind.Pattern)
            return (PatternNames.Length, Math.Clamp(st.BoardPattern, 0, PatternNames.Length - 1));
        // ⚠ 先把值抄到局部再进 lambda：`in` 参数不许被 lambda 捕获（CS1628）。
        float step = st.BoardPatternStep;
        int idx = Array.FindIndex(PatternSteps, v => MathF.Abs(v - step) < 0.5f);
        return (PatternSteps.Length, idx < 0 ? 0 : idx);
    }

    /// <summary>
    /// 把盘上读回来的间距**吸附到最近的档位**。
    /// 档位表是换过的（原来是 24/40/64，2026-09-27 改成 20/30/40/64/96），
    /// 老配置里存的 24 不在新表里——不吸附的话档位点会指到第 1 档上，等于显示一个谎。
    /// </summary>
    private static float SnapPatternStep(float v)
    {
        float best = PatternSteps[0], bestD = float.MaxValue;
        foreach (var s in PatternSteps)
        {
            float d = MathF.Abs(s - v);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    private bool BandHasSwatches => _bandCell is 3 or 4 or 7;

    /// <summary>文本那一格：色片 = 下一次输入的文字颜色；右边是字号下拉。</summary>
    private bool BandHasTextSize => _bandCell == 7 && _host?.State.TextBoxEditing != true;
    // 文本带在 4~5 格的自定义主条里会很窄：字号按钮收小、色片只留 3 个，
    // 否则字号按钮和色片会叠在一起。
    private bool TextSizeTight => VisibleCount() <= 5;
    private float TextSizeBtnW => TextSizeTight ? 46f : 56f;
    private float TextSizeReserve => BandHasTextSize ? TextSizeBtnW + 10f : 0f;

    private RectF TextSizeButtonRect()
    {
        var band = BandRect();
        float right = band.MaxX - BarInset() - ActionReserve;
        float cy = (band.MinY + band.MaxY) * 0.5f;
        float h = SwatchHeight();
        return new RectF
        {
            MinX = right - TextSizeBtnW, MinY = cy - h * 0.5f,
            MaxX = right, MaxY = cy + h * 0.5f,
        };
    }

    // ---- 字号菜单（点按钮弹出）-------------------------------------------
    private bool _textFontMenuOpen;
    // 和文字操作条的下拉读同一张表（ClassIn 的六档），见 InkPalette.TextFontSizes。
    private static float[] TextFontSizes => InkPalette.TextFontSizes;
    private const int TextFontCols = 3;
    private const float TextFontCellW = 46f;
    private const float TextFontCellH = 28f;
    private const float TextFontGap = 4f;
    private const float TextFontPad = 8f;
    private const float TextFontHeaderH = 20f;
    private static int TextFontRows => (TextFontSizes.Length + TextFontCols - 1) / TextFontCols;
    private static float TextFontMenuW => TextFontPad * 2 + TextFontCols * TextFontCellW
                                        + (TextFontCols - 1) * TextFontGap;
    private static float TextFontMenuH => TextFontPad * 2 + TextFontHeaderH
                                        + TextFontRows * TextFontCellH
                                        + (TextFontRows - 1) * TextFontGap;

    private RectF TextFontMenuRect()
    {
        var band = BandRect();
        var btn = TextSizeButtonRect();
        var shift = Shift();
        float minX = _screen.MinX - shift.X, maxX = _screen.MaxX - shift.X;
        float minY = _screen.MinY - shift.Y, maxY = _screen.MaxY - shift.Y;
        float x = (btn.MinX + btn.MaxX - TextFontMenuW) * 0.5f;
        float y = BandAbove()
            ? band.MinY - PenPickGap - TextFontMenuH
            : band.MaxY + PenPickGap;
        x = Math.Clamp(x, minX + 8f, MathF.Max(minX + 8f, maxX - 8f - TextFontMenuW));
        y = Math.Clamp(y, minY + 8f, MathF.Max(minY + 8f, maxY - 8f - TextFontMenuH));
        return new RectF { MinX = x, MinY = y, MaxX = x + TextFontMenuW, MaxY = y + TextFontMenuH };
    }

    private RectF TextFontCellRect(int i)
    {
        var p = TextFontMenuRect();
        int col = i % TextFontCols, row = i / TextFontCols;
        float x = p.MinX + TextFontPad + col * (TextFontCellW + TextFontGap);
        float y = p.MinY + TextFontPad + TextFontHeaderH + row * (TextFontCellH + TextFontGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + TextFontCellW, MaxY = y + TextFontCellH };
    }

    private int HitTextFontCell(float x, float y)
    {
        for (int i = 0; i < TextFontSizes.Length; i++)
            if (TextFontCellRect(i).Contains(x, y)) return i;
        return -1;
    }

    private void OpenTextFontMenu()
    {
        if (_penPickerOpen) ClosePenColorPicker();
        _textFontMenuOpen = true;
        _railHover = true;
        Invalidate();
    }

    private void CloseTextFontMenu()
    {
        if (!_textFontMenuOpen) return;
        _textFontMenuOpen = false;
        _leftAtMs = _host.NowMs;
        _frameShiftValid = false;
        Invalidate();
    }
    /// <summary>
    /// 这一格的设置条上要不要那个**虚实线切换**（夹在色片和粗细滑条之间，
    /// 用户 2026-09-19 定的位置："加在'调按钮大小'和'颜色带'中间"）。
    ///
    /// 只有笔（第 3 格）有；荧光笔 / 激光笔画虚线没有意义（见 Engine.PenDash）。
    /// 显示档随带宽走：3–7 格没有（极简色片/粗细按钮已经很满），
    /// 8～11 格是 28×26 方形，12 格以上才恢复 46 宽的长方形。
    /// 这样不是“少一个功能”，而是把这一档最不常用的开关让位给颜色/滑条。
    /// </summary>
    private bool BandHasDashToggle => _bandCell == 3 && Density != BandDensity.Minimal;
    /// <summary>
    /// 哪几格的设置条右边有滑条。
    ///
    /// 白板那一格**看布局**：它控制的是"板面不透明度"（用户 2026-09-17："增加一个透明度的
    /// 拖动功能，这样可以批注的时候隐约看见下面的题目"），但紧凑布局（极简档那条带子只有
    /// 315 宽）里"板色 + 底纹 + 间距"已经占满，塞不下 146 宽的滑条——所以退到紧凑布局时
    /// 滑条一起去掉（用户 2026-09-27 选的口径：极简档"翻页不要，保留黑白色，底纹 间距"）。
    /// </summary>
    private bool BandHasSlider => !EraserSizeHidden && !BandHasWidthButton
        && (_bandCell == 2 ? !BoardCompactBand : _bandCell is 3 or 4 or 5 or 6);
    /// <summary>
    /// 上带里有几段。白板那一格取自 <see cref="BoardBand"/>（完整 8 段 / 紧凑 5 段，
    /// **不写死数字**——2026-09-27 重排时就是靠这条把"5 段写死"的旧账还掉的）。
    /// 截图那一格是 3 段：**[直接截取][隐藏界面][粘贴图片]**——前两段照 InkClass 的两项菜单，
    /// 第三段是用户 2026-09-17 要的："粘贴功能，因为其他地方使用复制功能可以到剪贴板，
    /// 但如果是触摸屏或者手写板可能没有键盘"（等于把 `Ctrl+V` 搬到屏幕上）。
    ///
    /// 图形那一格是**整块图形面板**（18 段、三行）：段数和顺序都取自
    /// <see cref="ShapeRows"/>，不再写死数字——段宽是按"可用宽度 ÷ **本行**段数"算的
    /// （见 <see cref="SegmentRect"/>），加图形不用再动布局，也不会挤成一团
    /// （挤不下会有 `--shapebandtest` 里"每段 ≥ 60 宽"那条兜着）。
    /// </summary>
    private int BandSegmentCount => _bandCell switch
    {
        2 => BoardBand.Length, 6 => 2, 8 => 2, 9 => 2, _ => 0,
    };

    /// <summary>
    /// **图形那一格的段表**（一行一个数组，从上到下、从左到右）——这是整个面板里
    /// **唯一一份**"点哪一段切什么工具 / 哪段高亮 / 画哪张图标"的来源
    /// （`ShapeToolAt` / `ActivateSegment` / `IsSegmentActive` / `ShapeIconFor` 都读它）。
    /// 各写一份的话，加一种图形就会漏掉一处——这条教训仓库里吃过好几次。
    ///
    /// **三行**（2026-09-20 第十三批定的，见 计划-图形工具.md §35）：
    ///   ① **高频图形 8 个**（用户 2026-09-19 那批，**位置一个都不许动**——肌肉记忆）；
    ///   ② **4 种曲线**（2026-09-20 第五批："图形框里加第二列，教师实际使用时高频的只要一行"）；
    ///   ③ **6 个立体图形**（第五批搬来圆柱 / 圆锥，第十一/十二批加棱柱 / 棱锥 / 棱台，
    ///      第十三批加圆台）。
    ///
    /// **为什么从"两行"改成"三行"**：第三行凑到 6 个之后，第二行会有 10 段，
    /// 每段宽度掉到 **55 逻辑像素**——而"每段 ≥ 60"是量出来的门槛（图标 18 ＋ 四周留白），
    /// `--shapebandtest` 里那条断言当场就红了。三行之后每行 8 / 6 / 7 段，
    /// 每段 70 / 96 / 81，都还在门槛之上；而且读法更顺：**曲线一行、旋转体一行、棱柱体一行**。
    /// 代价是开带时面板高一整行（只在指针停在面板上时，画的时候不受影响）。
    ///
    /// 第一行那八个的**相对次序一个没动**（直线 < 矩形 < 椭圆 < 箭头 是老师最早的肌肉记忆，
    /// 2026-09-19 又接了坐标系），这条规矩从第一版起就没破过。
    /// </summary>
    private static readonly Tool[][] ShapeRows =
    {
        // ① 高频图形（第一行，位置冻结）
        new[]
        {
            Tool.Line, Tool.Rectangle, Tool.Ellipse, Tool.Circle,
            Tool.Triangle, Tool.Parallelogram, Tool.Arrow,
            Tool.Coordinate,     // 2026-09-19 第二批接在末尾（只撤了它后面的"数轴"那一段）
        },
        // ② 曲线（第二行）：**椭圆（带焦点）/ 双曲线 / 抛物线** / 正弦 / 余弦 / 波浪线 / 正切。
        //
        // ⚠ **2026-09-22 用户重排了这一行的头三格**（原话："椭圆排在第二行的最前面，
        //   然后是……图标前三个是椭圆，双曲线，抛物线"）——也就是把「椭圆（带焦点）」放到行首、
        //   抛物线挪到第三。第一行那八个的**位置仍然冻结**（那条规矩没破，用户只说了第二行）。
        new[]
        {
            Tool.ConicEllipse, Tool.Hyperbola, Tool.Parabola,
            Tool.Sine, Tool.Cosine, Tool.Wave, Tool.Tangent,
        },
        // ③ 立体（第三行）：**旋转体 4 ＋ 棱柱体 3**。
        //   · 旋转体：一次拖出**外接矩形**、一笔画完、被挡住的是"远侧那一圈/半圈"
        //     （圆柱 / 圆锥 / 圆台 / **球**；球多画一个赤道椭圆，不然它和「圆」长得一样）；
        //   · 棱柱体：**两笔**（底面外接框 → 顶上那个中心）、3/4/5/6 档、"直"那档有轻微吸附。
        // ⚠ **球紧跟着圆台**（用户 2026-09-20："把球放在旋转体后面"）——旋转体那一组
        //   "柱 / 锥 / 台 / 球"连着排，后面才是棱柱体那一组。
        // ⚠ **长方体 / 四面体不在这张表里了**（2026-09-20 第十二批撤的**入口**）：
        // 四棱柱（直）就是长方体、三棱锥就是四面体，被上面那几段覆盖了。
        // 撤的是入口，不是画法——`Tool.Cuboid` / `StrokeKind.Cuboid` 与整条画法都留着，
        // 旧板书里的长方体照样能打开、能选中、能删（同 2026-09-19 撤「数轴」的规矩，见 11.2）。
        new[]
        {
            Tool.Cylinder, Tool.Cone, Tool.ConeFrustum, Tool.Sphere,
            Tool.Prism, Tool.Pyramid, Tool.Frustum,
        },
    };

    /// <summary>图形那一格在上带里的下标（三行都在这一个格子里）。</summary>
    private const int ShapeCell = 10;

    /// <summary>
    /// 上带图形那一格一共几行。布局（`SegmentRect` 的行高/居中）和带子总高
    /// （`BandHeightLogical`）都问它。
    /// </summary>
    private static int ShapeBandRows => ShapeRows.Length;

    /// <summary>图形那一格一共几段（各行加起来）——命中与绘制的循环都用它。</summary>
    private static int ShapeSegmentCount
    {
        get
        {
            int sum = 0;
            foreach (var row in ShapeRows) sum += row.Length;
            return sum;
        }
    }

    /// <summary>第 `row` 行有几段（决定本行的段宽）。</summary>
    private static int ShapeRowCount(int row)
    {
        if (row < 0 || row >= ShapeRows.Length) return 0;
        return ShapeRows[row].Length;
    }

    /// <summary>
    /// 第 `i` 段（在图形那一格里，**各行从上到下、行内从左到右拉平编号**）
    /// 在第几行。越界回第 0 行。
    /// </summary>
    private static int ShapeSegmentRow(int i)
    {
        if (i < 0) return 0;
        int seen = 0;
        for (int r = 0; r < ShapeRows.Length; r++)
        {
            seen += ShapeRows[r].Length;
            if (i < seen) return r;
        }
        return ShapeRows.Length - 1;
    }

    /// <summary>第 `i` 段在**它那一行里**排第几（画/命中的 x 用它）。</summary>
    private static int ShapeSegmentCol(int i)
    {
        int row = ShapeSegmentRow(i);
        int before = 0;
        for (int r = 0; r < row; r++) before += ShapeRows[r].Length;
        return i - before;
    }

    /// <summary>
    /// 第 `i` 段对应哪个工具。越界回第一段——宁可画错一个图标，也不让下标越界。
    /// </summary>
    private static Tool ShapeToolAt(int i)
    {
        // 最后那一段是**动作**（图库），没有工具：调用处都先判过 `i >= ShapeSegmentCount`，
        // 走到这儿说明有谁漏判了——回第一个工具，宁可画错一个图标也不越界。
        if (i >= ShapeSegmentCount) return ShapeRows[0][0];
        int row = ShapeSegmentRow(i);
        int col = ShapeSegmentCol(i);
        var cells = ShapeRows[row];
        return col >= 0 && col < cells.Length ? cells[col] : ShapeRows[0][0];
    }

    /// <summary>
    /// 这个工具**在图形面板里有没有入口**（点主条那一格时用它判断"要不要偷偷换工具"）。
    ///
    /// **名字说明白点**：`Engine` 里也有一个 `IsShapeTool`，判的是"**这个工具画出来的是
    /// 图形还是自由笔迹**"（含数轴——它没入口但画法还在，老存档里那些数轴要能选中、能删）。
    /// 两个名字撞着、语义不同，2026-09-20 顺手把这个改成 `HasShapeEntry`：
    /// **有入口** ⊂ **能画**，差的就是数轴那一个。
    ///
    /// 判据是**那张段表**（`ShapeRows`，一行一个数组）——它是"点哪一段切什么
    /// 工具 / 哪段高亮 / 画哪张图标"的唯一来源，所以这里扫一下就够，
    /// 加图形只改表（见 2026-09-19 那一轮：这里曾经是"上带七段 **或** 坐标系/数轴那一段"，
    /// 两者合流之后收敛回一句）。
    /// </summary>
    private static bool HasShapeEntry(Tool t)
    {
        foreach (var row in ShapeRows)
            if (Array.IndexOf(row, t) >= 0) return true;
        return false;
    }

    /// <summary>这个工具的粗细范围。**界面管范围，引擎管钳位**。
    ///
    /// **2026-10-09 晚起：范围 = 该工具档表的首尾**（滑条两端正好是最细/最粗那一档——
    /// 拖动吸附之后，范围外到不了、也不该到）。两张数字要对上引擎的档表，
    /// 靠 --paneltest 卡住（见 WidthGrades）。
    /// </summary>
    private (float Min, float Max) WidthRange(Tool tool) => tool switch
    {
        Tool.Highlighter => (8f, 88f),     // = HighlighterWidthGrades 首尾
        Tool.Laser => (2f, 24f),           // = LaserWidthGrades 首尾
        Tool.Eraser => (6f, 34f),          // 整笔橡皮的**落点半径** = EraserWidthGrades 首尾
        Tool.PixelEraser => (14f, 150f),   // 像素橡皮的**横边**（高 = 横边 × 1.618）＝ 首尾
        _ => (1f, 20f),                    // 画笔 = PenWidthGrades 首尾
    };

    /// <summary>
    /// 各工具的**粗细档位**（逻辑像素）。**2026-10-09 晚用户改法**：
    /// 滑条上**不再画点**；拖动**吸附到最近一档**（拿到手的一定是表里的数）。
    ///
    /// 和引擎里那五张表各留一份，靠 --paneltest 逐项比对卡住（同 WidthRange 的老规矩）；
    /// 界面只认公开契约、拿不到引擎 internal 是故意的。
    /// **笔表 2026-10-15 换代：1~20 每档差 1**（用户定；原 15 档的 .5 中点与 40 撤掉）：
    ///   荧光笔 8/13/18/25/32；激光 4/6/8/11/14/18/22；
    ///   整笔橡皮 6/12/17/22/28/34（6 档）；像素橡皮 14/46/70/93/122/150（6 档）。
    ///   2026-11-03：两种橡皮各加了一个"精细档"（6 / 14），用来把一条细线切开一个小口。
    /// </summary>
    private static readonly float[] PenWidthGrades =
    {
        1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f,
        11f, 12f, 13f, 14f, 15f, 16f, 17f, 18f, 19f, 20f,
    };
    // 2026-10-16 换代（与引擎表逐项一致，--paneltest ⑥.1 卡住）：
    // 荧光笔 8–88 步 4（21 档）；激光 2–24 步 2（12 档）。
    private static readonly float[] HighlighterWidthGrades =
        { 8f, 12f, 16f, 20f, 24f, 28f, 32f, 36f, 40f, 44f, 48f, 52f, 56f, 60f, 64f, 68f, 72f, 76f, 80f, 84f, 88f };
    private static readonly float[] LaserWidthGrades =
        { 2f, 4f, 6f, 8f, 10f, 12f, 14f, 16f, 18f, 20f, 22f, 24f };
    private static readonly float[] EraserWidthGrades = { 6f, 12f, 17f, 22f, 28f, 34f };
    private static readonly float[] PixelEraserWidthGrades = { 14f, 46f, 70f, 93f, 122f, 150f };

    private float[] WidthGrades(Tool tool) => tool switch
    {
        Tool.Highlighter => HighlighterWidthGrades,
        Tool.Laser => LaserWidthGrades,
        Tool.Eraser => EraserWidthGrades,
        Tool.PixelEraser => PixelEraserWidthGrades,
        _ => PenWidthGrades,
    };

    /// <summary>
    /// **滑条实际吸附的档位**：极简 5 档、8～9 格 7 档、10～11 格 10 档、完整全档。
    /// 首尾一定保留（最细到最粗），中间按等距抽样；抽出来的值都来自原档表，
    /// 所以引擎按全档表吸附时也会落在同一个值上。
    ///
    /// 只影响“拖滑条一次能停几个位置”，不改变实际笔宽范围；
    /// 键盘 Ctrl+Alt+6 的循环仍走引擎全档表，那是另一条入口。
    /// </summary>
    private float[] ActiveWidthGrades(Tool tool)
    {
        var all = WidthGrades(tool);
        // 极简/小宽度模式：橡皮只留 3 个可点档位；笔仍保留 5 个。
        int want = tool is Tool.Eraser or Tool.PixelEraser && VisibleCount() <= 7
            ? 3
            : Density switch
            {
                BandDensity.Minimal => 5,
                BandDensity.Compact => 7,
                BandDensity.Regular => 10,
                _ => all.Length,
            };
        if (want >= all.Length) return all;

        var picked = new float[want];
        for (int i = 0; i < want; i++)
        {
            int idx = (int)MathF.Round(i * (all.Length - 1f) / (want - 1f));
            picked[i] = all[idx];
        }
        return picked;
    }

    private static int NearestGradeIndex(float[] grades, float value)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < grades.Length; i++)
        {
            float d = MathF.Abs(grades[i] - value);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private float SliderT(in UiState st)
    {
        // 白板那一格：滑条 = **板面不透明度**（不是粗细）。
        // 左边的数字要和引擎里的 BoardOpacityMin/Max 一致（界面拿不到引擎的 internal 常量，
        // 靠 --paneltest 把滑条拖到两端来卡住这两边）。
        if (_bandCell == 2)
            return Math.Clamp((st.BoardOpacity - BoardOpacityMin) / (BoardOpacityMax - BoardOpacityMin), 0f, 1f);
        var (min, max) = WidthRange(st.Tool);
        if (max <= min) return 0f;
        return Math.Clamp((st.Width - min) / (max - min), 0f, 1f);
    }

    /// <summary>板面透明度的可调范围（要和引擎里那两个常量一致）。</summary>
    private const float BoardOpacityMin = 0.35f, BoardOpacityMax = 1f;

    /// <summary>拖滑条：只在**值真的变了**的时候提交（每帧几十次 SetWidth 会连带动画与重画）。</summary>
    /// <summary>紧凑粗细按钮：点一下切到当前工具的下一个有效档位。</summary>
    private void CycleWidthGrade()
    {
        var tool = _host.State.Tool;
        var grades = ActiveWidthGrades(tool);
        if (grades.Length == 0) return;
        int at = NearestGradeIndex(grades, _host.State.Width);
        float next = grades[(at + 1) % grades.Length];
        if (MathF.Abs(next - _host.State.Width) < 0.01f) return;
        _host.Commands.SetWidth(next);
        Invalidate();
    }

    private void DragSlider(float x)
    {
        var (left, right) = SliderTrackRange();
        float t = right <= left ? 0f : Math.Clamp((x - left) / (right - left), 0f, 1f);

        if (_bandCell == 2)
        {
            float wantOpacity = BoardOpacityMin + (BoardOpacityMax - BoardOpacityMin) * t;
            if (MathF.Abs(wantOpacity - _host.State.BoardOpacity) < 0.005f) return;
            _host.Commands.SetBoardOpacity(wantOpacity);
            return;
        }
        var (min, max) = WidthRange(_host.State.Tool);
        float want = min + (max - min) * t;
        // 窄档只吸附到这一档可见的 5/7/10 个位置；完整档仍是全档表。
        var grades = ActiveWidthGrades(_host.State.Tool);
        want = grades[NearestGradeIndex(grades, want)];
        if (MathF.Abs(want - _host.State.Width) < 0.5f) return;   // 没变就不提交
        _host.Commands.SetWidth(want);
    }

    /// <summary>滑条右端留给"粗细预览点"的宽度。</summary>
    private const float SliderPreviewW = 18f;

    /// <summary>
    /// 滑条**轨道**的左右端（滑钮圆心能走到哪儿）。
    /// 画和拖共用这一份——各算一份的话，迟早会出现"看着在中间、点出来偏一截"。
    /// </summary>
    private (float Left, float Right) SliderTrackRange()
    {
        var box = SliderRect();
        float left = box.MinX + Tokens.SliderKnob * 0.5f;
        float right = box.MaxX - SliderPreviewW - Tokens.SliderKnob * 0.5f;
        return (left, right);
    }

}
