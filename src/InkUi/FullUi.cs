using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
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
public sealed class FullUi : IOverlayUi
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
        ("select",    "selectFilled",    "选择"),
        ("shapes",    "shapesFilled",    "图形"),
        ("capture",   "captureFilled",   "截屏"),
        ("undo",      "undoFilled",      "后撤"),
        ("redo",      "redoFilled",      "重做"),
        ("more",      "moreFilled",      "更多"),
    };

    /// <summary>每一组的最后一格（下标）。组之间画一条分隔线。</summary>
    private static readonly int[] GroupEnds = { 0, 2, 6, 9, 12 };

    // ---- 界面档位 -----------------------------------------------------------
    //
    // 三档（用户定的默认是"完整档"）：
    //   · 极简 = 一条**短胶囊**，只留最常用的六格；
    //   · 自定义 = 老师自己钉的那份集合（默认＝全部，等于完整档，改过之后才不一样）；
    //   · 完整 = 全部十二格。
    // 规则一条：**极简与自定义都必须是完整档顺序的子序列**——同一个工具在哪一档里
    // 都在同一个相对位置，来回切档不用重新找。

    /// <summary>档位。</summary>
    private enum Profile { Mini = 0, Custom = 1, Full = 2 }

    /// <summary>
    /// 极简档留哪几格。**照抄假面板的结论**（鼠标/白板/笔/橡皮/后撤/更多）：
    /// 补"鼠标"是因为看 PPT 时要能点下层，补"后撤"是因为误画一笔要有救。
    /// </summary>
    private static readonly int[] MiniCells = { 1, 2, 3, 6, 10, 12 };

    private Profile _profile = Profile.Full;

    /// <summary>自定义档钉了哪些格（下标对齐 Cells；0 号"收起格"永远在，不参与钉）。</summary>
    private readonly bool[] _pinned = CreateAllPinned();

    /// <summary>笔、橡皮、「更多」是**安全项**：取消钉住之后就没法用了，所以不许取消。</summary>
    private static bool CanUnpin(int cell) => cell is not (3 or 6 or 12);

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

    private int _hover = -1;               // -2 = 球，0.. = 格子，-1 = 没有
    private int _press = -1;
    private Vector2 _pressPos;
    private Vector2 _dragStartAnchor;
    private bool _dragging;                // 已经超过拖动阈值

    // ---- 悬停提示（Tooltip；2026-10-02）--------------------------------------
    //
    // 行为：鼠标停在同一个东西上 **500ms**（Tokens.TipDelayMs，照 WPF 默认 400 取松一档），
    // 浮出一张小卡片（名称 + 当前键位 + 一句说明）；移开/按下/拖动立刻收起。
    //
    // 目标编号**沿用 `HoverAt` 那一套**（-2 球 / 0..12 格 / 100+ 色片 / 200+ 分段 /
    // 300 滑条 / 400 动作按钮 / 500 虚实线）——同一份编号既管高亮也管提示，
    // 不另写一套"哪块算哪块"的映射（那迟早会和高亮漂移）。
    //
    // 触屏/手写笔**没有悬停事件**（仓库老规矩：图形工具的"悬停预览"就是因此改成
    // "按住拖"的），所以提示只当增强，面板上的常显文字一个字都不动。
    private bool _tipEnabled = true;          // 开关在「更多 → 设置 → 外观」，默认开
    private const int TipNoTarget = int.MinValue;
    private int _tipTarget = TipNoTarget;     // 现在停在哪（没有 = 不出提示）
    private double _tipSinceMs;               // 什么时候停上去的（过了延迟才显示）
    private bool _tipShown;                   // 已经过了延迟、正在显示（静态，不再烧帧）
    private readonly Anim _tipFade;           // 0→1 淡入

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

    /// <summary>上一次看到的工具。用它判断"工具真的换了"，而不是"带子对不对"。</summary>
    private Tool _lastTool = Tool.Pen;

    /// <summary>
    /// 上一次看到的"在放映吗"。用它认**进放映的那一刻**（边沿），
    /// 见 <see cref="OnStateChanged"/> 里那一段。
    /// </summary>
    private bool _lastPpt;

    /// <summary>上一次看到的穿透状态。用它认**进穿透的那一刻**（边沿）——
    /// 进穿透时把上带收回那条 6 像素色线，穿透期间不许再张，见 <see cref="OnStateChanged"/>。</summary>
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
    private readonly Anim _rail;           // 0 = 平时那条 6 像素色线，1 = 完整设置条
    /// <summary>色带常开（「设置 → 外观 → 色带常开」，2026-10-05）：一直摊着，不参与悬停收放。</summary>
    private bool _railPinned;
    /// <summary>触摸把设置条"带出来"后的保持截止（触摸没有悬停，不能靠指针位置维持展开）。</summary>
    private double _railTouchHoldUntilMs = double.NegativeInfinity;
    private bool _railHover;

    /// <summary>悬停意图的两个时刻：进热区 120ms 才展开、离开 220ms 才收回——路过不算数。</summary>
    private double _railEnterAtMs = double.NegativeInfinity;
    private double _railExitAtMs = double.NegativeInfinity;
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

    /// <summary>设置子页里的行（启动器的底栏不在这张表里）。**顺序按两栏里的布局走**：
    /// 左列 外观（3）＋ 书写（3）；右列 墨迹（3）——见 <see cref="MoreRowRect"/>。</summary>
    private enum Row { DarkTheme, AutoHide, RailPin, Tooltip, DwellShape, Pressure, FineStroke, RestoreInk, PptAutoSave, HistoryDays, TouchGestures }

    /// <summary>
    /// 行表：**绘制 / 命中 / 执行 / 自检都读这一份**（本仓"同一份名单写两处必漏一处"的老毛病）。
    /// `Hint` 是标签下面那行 11px 小灰字，只有需要解释的行才给。
    /// </summary>
    private static readonly (Row Kind, string Label, bool Dangerous, bool Gray, string Hint)[] Rows =
    {
        (Row.DarkTheme, "深色主题", false, false, ""),
        (Row.AutoHide, "贴边隐藏", false, false, ""),
        // 色带常开（2026-10-05 用户实测点名）：设置条一直摊着。触摸屏上那条 6 像素的色线很难点中，
        // 触摸点工具格也会自动带出来（自适应，见 UpdateRail 的 _railTouchHoldUntilMs）。
        (Row.RailPin, "色带常开", false, false, "设置条一直摊开（触摸屏不用去碰那条色线）"),
        // 悬停提示（2026-10-02）：鼠标停住半秒、手指长按，浮出"名称 + 快捷键 + 说明"。
        // 默认开；触屏没有悬停，所以长按是它在触摸上的等价物（见 Tooltip 那一节）。
        // **范围是收窄过的**：只给图标-only / 带快捷键 / 隐藏手势，别的（色带、文字段、
        // 点一下就见结果的）都不配——判据见《调研-悬停提示-Tooltip.md》10.8。
        (Row.Tooltip, "功能提示", false, false, "鼠标 / 笔悬停、手指长按，显示名称与快捷键"),
        // 停顿成型（2026-09-23 第二十批，见 计划-图形工具.md §四十二）：
        // 手写一笔停住 400ms → 把它变成规整图形。**默认开**（用户定的：
        // "因为是停顿变，所以默认开"），所以这一行的开关初始就是「开」。
        // 关掉 = 以后画的那些不再参与；已经变出来的图形不受影响（那是撤销的事）。
        (Row.DwellShape, "停顿变图形", false, false, ""),
        // 压感粗细（2026-10-01，批次 0.2）：默认开；关掉 = 整块板等宽，
        // 手写板的流畅 / 预测不受影响（渲染期开关，文档里的压力数据不动）。
        (Row.Pressure, "压感粗细", false, false, "关掉后所有笔迹等宽（手写板照样流畅）"),
        // 精细笔迹（2026-10-07）：管的是「原始输入补点」。
        // 系统会把来不及投递的移动合并成一条消息，只取最新那一个等于把采样率砍半
        // （真机实测：指针消息 61Hz，设备实际报了 190Hz）。这一行就是"要不要把中间点捞回来"。
        // **默认开**（用户定的：开不开 ink 要有一样的手写体验）；低配机怕性能不够可以一键关。
        (Row.FineStroke, "精细笔迹", false, false, "关掉后写快时线条略粗糙（省一点性能，低配机可关）"),
        // [停用 2026-10-05] 墨迹预测（老预测系统，见 `已停用-渲染实验.md`）：
        // (Row.Predict, "墨迹预测", false, false, "开了更跟手一点，可能有轻微拖影"),
        // 墨迹三条偏好（原本在「墨迹」页，2026-10-02 启动器改版后搬进设置子页）。
        (Row.RestoreInk, "自动恢复上次板书", false, false, "下次启动接上这次的板书"),
        (Row.PptAutoSave, "PPT 墨迹默认自动保存", false, false, "放映时长按菜单仍可临时覆盖"),
        (Row.HistoryDays, "历史清理", false, false, "过期 PPT 缓存与备份，启动时清掉"),
        // 触摸手势**总开关**（2026-10-05，用户点名要的"保险丝"）：关掉只剩单指书写——
        // 双指手势 / 三指擦 / 长按选择 / 单指漫游全部停用（闸门在 TouchGestures.Enabled，
        // 见那里每条判定）。**默认开**；学校大屏万一遇到手势 bug，老师在这里一键退回。
        (Row.TouchGestures, "触摸手势", false, false, "关掉只剩单指书写（双指 / 三指 / 长按全停用）"),
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
            // 悬停提示：还在等 500ms 延迟、或淡入没走完，都要继续给帧——
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
        _widgets = new Widgets(host);
        IconAtlas.Init(host.PathFactory);
        _expand.Bind(host);        // 时钟必须接真的那个（见 Anim.Bind 的注释）
        _peek.Bind(host);
        _rail.Bind(host);
        _dashFade.Bind(host);
        _more.Bind(host);
        _moreH.Bind(host);
        _tipFade.Bind(host);
        _moreH.Jump(MoreTargetH());
        _lastTool = host.State.Tool;
        // **启动即展开**（用户 2026-09-27 定："第一次打开以后，默认就展开"）。
        //
        // 原来是 `Jump(0f)`——启动是一颗球，得先点一下才展开。用户上手后发现"每次开机
        // 都要先点那一下"是多余的：工具的入口本来就该摆在那儿。
        // 收起仍然能用（点一下条自己就收成球），只是**不再是默认态**。
        _expand.Jump(1f);
        _peek.Jump(1f);
        _rail.Jump(0f);
        _dashFade.Jump(1f);
        _lastPpt = host.State.PptMode;
        _lastPass = host.State.PassThrough;
        LoadPrefs();
        Layout(host.Screen, host.DpiScale);
        PushFloatingTheme();       // 浮层（操作条/小面板/旋转读数）跟着走同一套令牌
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
        // 色带常开（2026-10-05）：设置条一直摊着——触摸屏上不用去碰那条 6 像素的色线。
        _railPinned = _host.GetPref("railPin") == "1";
        // 悬停提示：**默认开**，只写"关过的"那一份（和 dwellShape / pressure 同一条规矩）。
        // 引擎自己画的浮层（操作条 / PPT 条）读不到界面偏好，这里推一次开关过去。
        _tipEnabled = _host.GetPref("tooltip") != "0";
        _host.Commands.SetTooltips(_tipEnabled);

        string prof = _host.GetPref("profile");
        _profile = prof == "mini" ? Profile.Mini
                 : prof == "custom" ? Profile.Custom
                 : Profile.Full;

        for (int i = 1; i < _pinned.Length; i++) _pinned[i] = true;
        foreach (var s in (_host.GetPref("unpinned") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(s, out int cell) && cell > 0 && cell < Cells.Length && CanUnpin(cell))
                _pinned[cell] = false;

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
        // 精细笔迹：**默认开**，同样只写"关了"这一种情况（引擎启动时自己读它）。
        _host.SetPref("finestroke", st.FineStrokeOn ? null : "0");
        // 触摸手势总开关：**默认开**，同样只写"关了"这一种情况。
        _host.SetPref("touch.gestures", st.TouchGesturesOn ? null : "0");
        // [停用 2026-10-05] 墨迹预测：默认关，只写"开了"这一种情况（引擎启动时自己读它）。
        // _host.SetPref("predict", st.PredictOn ? "1" : null);
        // 悬停提示：**默认开**，只写"关了"这一种情况。
        _host.SetPref("tooltip", _tipEnabled ? null : "0");

        var off = new List<string>();
        for (int i = 1; i < _pinned.Length; i++) if (!_pinned[i]) off.Add(i.ToString());
        _host.SetPref("unpinned", off.Count == 0 ? null : string.Join(",", off));
        // 色带常开：默认关，只写"开了"这一种情况。
        _host.SetPref("railPin", _railPinned ? "1" : null);
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
        return QueryBounds();
    }

    public RectF QueryBounds()
    {
        // 「更多」面板打开（含开合动画）时：我们占**整块屏幕**——这是全屏模态的根
        //（命中测试、裁剪、接输入小窗都按这份矩形走，理由见「更多」面板那一节）。
        // 关掉（动画也跑完）立刻回到主条那一条，一帧都不多占。
        if (_moreOpen || _more.Running) return _screen;

        // 占用矩形必须跟着"实际画出来的东西"走：贴边隐藏时它就只剩露头那一条，
        // 输入小窗也跟着缩——这样指针扫过露头才算"碰到面板"，其余位置照旧穿透/画线。
        var u = UnionRect();
        // **粗细预览会画到面板外面**（带子那一侧的上/下方）：可见范围也得跟出去，
        // 否则引擎会把超出 QueryBounds 的部分裁掉（引擎是按这份矩形做裁剪的），
        // 表现就是"预览没画出来"。它只在拖滑条/悬停滑条时出现，平时这份矩形不变。
        if (SizePreviewVisible && _host != null) u = Union(u, SizePreviewRect(_host.State));
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

    /// <summary>上带这一刻的高度：平时 6 像素的色线，碰到了长成 34 像素的设置条。</summary>
    private float BandHeightFull() =>
        Tokens.BandLine + (BandHeightLogical() - Tokens.BandLine) * _rail.Value;

    /// <summary>
    /// 这一格的上带**完全张开**时有多高（逻辑像素）。
    ///
    /// 从 2026-09-20 起它**按格子算**、不是一个常量：图形那一格是**三行**
    /// （第一行 8 个高频图形、第二行 6 个曲线、第三行 7 个立体），所以它比别的格子高两整行。
    /// 带子朝屏幕中心那一侧长（见 <see cref="BandRect"/>），
    /// 所以变高是往上/往下长，不会把主条顶走。
    ///
    /// ⚠ **"带子多高"只能问这一处**（2026-09-20 第十六批收的口子）：以前有四处各写一遍
    /// `Tokens.BandHeight`（判定区 / 夹取 / 总高 / 弹出面板定位），而那一格从第十三批起就是多行，
    /// 于是判定区只盖住**最下面那一行** —— 用户 2026-09-20 报的正是这个：
    /// "**鼠标移动到第一行的任何图形位置，色带会收起来**"（指针一挪到上面那行就被判成
    /// "离开面板"，220 毫秒后带子收回去）。四处都改问这个函数之后，
    /// 加行/加图形都不用再来补一遍。
    /// </summary>
    private float BandHeightLogical()
        => _bandCell == ShapeCell && ShapeBandRows > 1
            ? Tokens.BandHeight
              + (ShapeBandRows - 1) * (Tokens.SegmentHeight + ShapeRowGap)
            : Tokens.BandHeight;

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
    /// 6 像素色线**，不是消失——面板高度不变、贴边隐藏露出来的还是它；
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
    // 笔/荧光笔 = 12 色片 ＋ 粗细；激光 = 光点大小；橡皮 = 整笔/面积 ＋ 大小；
    // 选择 = 矩形/套索；白板 = 三种板色；图形 = 整块图形面板（见 ShapeRows）。
    // 没有设置项的（后撤/重做/更多/截屏）就不长上带——不做一排空按钮。

    private float BandCenterY() => (BandRect().MinY + BandRect().MaxY) * 0.5f;
    private float BandContentLeft() => BandRect().MinX + BarInset();

    private RectF SwatchRect(int i)
    {
        // **色片按可用宽度平分，铺满整条**（照假面板：cw = (avail - gap*(n-1)) / n）。
        // 早先我写的是固定 26 宽 ＋ 6 缝，结果右边空出一大块，跟假面板一比就露馅了。
        var band = BandRect();
        int n = SwatchCount;
        float gap = 4f;
        float avail = band.MaxX - band.MinX - BarInset() * 2f
                    - (BandHasSlider ? SliderTrackW + 14f : 0f)    // 给右端的粗细滑条让位
                    - (BandHasDashToggle ? DashToggleW + DashToggleGap : 0f)   // 给虚实线那一格让位
                    - ActionReserve;                                // 给最右端的动作按钮让位
        float w = (avail - gap * (n - 1)) / n;
        float h = SwatchHeight();
        float x = band.MinX + BarInset() + i * (w + gap);
        float y = BandCenterY() - h * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>上带左右的内边距（照假面板的 inset = 16）。</summary>
    private static float BarInset() => 16f;

    /// <summary>色片的高：最多 26，带子矮的时候按比例缩（假面板：min(26, band * 0.8)）。</summary>
    private float SwatchHeight() => MathF.Min(Tokens.Swatch, BandHeightFull() * 0.8f);

    /// <summary>虚实线那一格的宽（逻辑像素）：46 = 里面还画得下一小段线 + 左右各留 8。</summary>
    private const float DashToggleW = 46f;
    /// <summary>它和左边色片、右边滑条之间的缝。</summary>
    private const float DashToggleGap = 8f;

    /// <summary>
    /// **虚实线切换那一格**：从滑条左边往左让出"一格 + 一道缝"算出来。
    ///
    /// 为什么锚在滑条上、不锚在色片上：滑条是**贴着面板右沿**摆的（位置由 BarInset 定死），
    /// 所以从它往左量出来的这一格**不会随色片个数变**——极简档 4 个色片、完整档 12 个，
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

    /// <summary>粗细滑条的轨道宽（逻辑像素）。</summary>
    private const float SliderTrackW = 132f;

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

    private bool BandHasSwatches => _bandCell is 3 or 4;
    /// <summary>
    /// 这一格的设置条上要不要那个**虚实线切换**（夹在色片和粗细滑条之间，
    /// 用户 2026-09-19 定的位置："加在'调按钮大小'和'颜色带'中间"）。
    ///
    /// **只有完整档的笔有**，两条理由：
    ///   · 荧光笔 / 激光笔的轨迹画成虚线没有意义（见 Engine.PenDash），图形的线型历来
    ///     是"选中之后在操作条面板里改"——所以只有第 3 格；
    ///   · 极简档那条带子只有 ~333 宽，4 个色片 + 粗细滑条已经吃掉 155，再塞一格
    ///     就把色片压到 30 像素以下——而"色片挤到看不清"等于这个入口没有
    ///     （自检里"每个色片 ≥30 宽"那条就是这个意思）。极简档想要虚线就切回完整档。
    /// </summary>
    private bool BandHasDashToggle => _bandCell == 3 && _profile != Profile.Mini;
    /// <summary>
    /// 哪几格的设置条右边有滑条。
    ///
    /// 白板那一格**看布局**：它控制的是"板面不透明度"（用户 2026-09-17："增加一个透明度的
    /// 拖动功能，这样可以批注的时候隐约看见下面的题目"），但紧凑布局（极简档那条带子只有
    /// 315 宽）里"板色 + 底纹 + 间距"已经占满，塞不下 146 宽的滑条——所以退到紧凑布局时
    /// 滑条一起去掉（用户 2026-09-27 选的口径：极简档"翻页不要，保留黑白色，底纹 间距"）。
    /// </summary>
    private bool BandHasSlider => _bandCell == 2 ? !BoardCompactBand : _bandCell is 3 or 4 or 5 or 6;
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
        2 => BoardBand.Length, 6 => 2, 7 => 2, 8 => ShapeBandSegments, 9 => 2, _ => 0,
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
    private const int ShapeCell = 8;

    /// <summary>
    /// 图形那一格**最后那一段**：「图库」（我的图形，2026-09-22 加，用户要的"图像收藏"）。
    ///
    /// 它**不是图形工具、是动作**（点开图库面板），所以**不进 <see cref="ShapeRows"/>**：
    /// 那张表是"段 ↔ 工具"的唯一来源，塞一个非工具进去，`ShapeToolAt`、段高亮、
    /// 以及 `--shapebandtest` 那条"每个有入口的图形都要真拖一笔"全都会跟着错。
    /// 它单独占**第四行**一段（放在所有图形之后 = 不影响任何现有段号，肌肉记忆不动）。
    ///
    /// 纪律：**加图形只动 `ShapeRows`；加动作才动这里**。
    /// </summary>
    private static int LibrarySegment => ShapeSegmentCount;

    /// <summary>「图库」那一段在第几行（工具表之后紧挨着的一行）。</summary>
    private static int LibraryBandRow => ShapeRows.Length;

    /// <summary>
    /// 上带图形那一格一共几行：**工具表那几行 ＋ 最后那段「图库」自己一行**。
    /// 布局（`SegmentRect` 的行高/居中）和带子总高（`BandHeightLogical`）都问它——
    /// 少算一行的话，最后那一段会**画到带子外面去**（落在主条上），看着"点不动"。
    /// </summary>
    private static int ShapeBandRows => ShapeRows.Length + 1;

    /// <summary>图形那一格一共几段（工具段 ＋ 最后那段「图库」）。命中与绘制都用它。</summary>
    private static int ShapeBandSegments => ShapeSegmentCount + 1;

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

    /// <summary>第 `row` 行有几段（决定本行的段宽）。「图库」那一行只有一段。</summary>
    private static int ShapeRowCount(int row)
    {
        if (row == LibraryBandRow) return 1;
        return row >= 0 && row < ShapeRows.Length ? ShapeRows[row].Length : 0;
    }

    /// <summary>
    /// 第 `i` 段（在图形那一格里，**各行从上到下、行内从左到右拉平编号**）
    /// 在第几行。越界回第 0 行。
    /// </summary>
    private static int ShapeSegmentRow(int i)
    {
        if (i < 0) return 0;
        if (i >= ShapeSegmentCount) return LibraryBandRow;   // 最后那一段「图库」自己一行
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

    /// <summary>这个工具的粗细范围。**界面管范围，引擎管钳位**——引擎那边是 0.5～64。</summary>
    private (float Min, float Max) WidthRange(Tool tool) => tool switch
    {
        // 两边的数字要和引擎里各工具的档位对得上（引擎那边是
        // HighlighterWidthPresets 8/18/32、LaserWidthPresets **4/8/14/22**、
        // WidthPresets **1**/3/6/10/16/24、EraserRadiusPresets 12/22/34、
        // PixelEraserWidthPresets 46/93/150）。
        // 界面拿不到引擎的 internal 常量（那是**故意**的：界面只认公开契约），
        // 所以两边各留一份数字，靠自检卡住：--paneltest 会把滑条拖到两端，
        // 断言引擎里那个值真的走到了范围的端点。
        Tool.Highlighter => (8f, 64f),
        Tool.Laser => (4f, 24f),           // 左端 ＝ 最细那一档 4（2026-09-27 晚补的；默认档是 8）
        Tool.Eraser => (8f, 48f),          // 整笔橡皮改的是**落点半径**
        Tool.PixelEraser => (30f, 160f),   // 面积橡皮改的是**那一块的横边**（高 = 横边 × 1.618）
        _ => (1f, 40f),                    // 画笔：左端 = 1（2026-09-27 从 1.5 降下来）
    };

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

    // ---- 上带右端的"动作"按钮（照假面板）------------------------------------
    //
    // 假面板里：**清空**挂在橡皮那条的右端（擦一点 / 擦一块 / 全擦掉，语义是一路的），
    // 而且**按住 0.8 秒才算数**——清空本身可撤销，但代价大，防误触；
    // **全选**挂在选择那条的右端，点一下就执行。
    // 这两条我们一直缺（计划 10.1 的第 1 条），2026-09-17 用户点名要。
    //
    // **关闭白板**（2026-09-27 用户定的）挂白板那条的右端，点一下就执行。
    // 它是"白板格点第一下不再关板"补上的那一半：关板从此**只有这一个手动入口**
    //（另一条是"开穿透会自动关板"的互斥，那是自动的、不是入口）。
    //
    // 位置：上带**最右端**。粗细滑条也在右端，所以橡皮那条是
    // `[整笔擦][面积擦] …… [粗细滑条][清空]`——动作永远贴在最外沿。
    private enum BandAction { None = 0, Clear, SelectAll, CloseBoard, PasteImage }

    private BandAction ActionOf(int bandCell) => bandCell switch
    {
        2 => BandAction.CloseBoard,   // 白板那条：最右端一个"关闭白板"
        6 => BandAction.Clear,        // 清空 ≈ "全擦掉"，和两种橡皮排一条
        7 => BandAction.SelectAll,    // 全选 ≈ "把要操作的东西一次选上"，归选择这条
        9 => BandAction.PasteImage,   // 截图那条：把剪贴板里的东西粘进来（8.3.0 从分段里拆出来）
        _ => BandAction.None,
    };
    private BandAction CurAction => ActionOf(_bandCell);

    private const float ActionW = 92f;
    /// <summary>
    /// **窄的那个动作按钮**（白板的"关闭白板"用它，只有图标、没有文字）。
    ///
    /// 为什么单独给一个宽度：白板那条带子本来就满——8 段（翻页器 ＋ 三色 ＋ 底纹 ＋ 间距）
    /// 要 420，加上 146 的透明度滑条只剩 25 的余量；再塞一个 92 宽的动作按钮，
    /// 段会被挤到 89% 缩放（`SegmentRect` 里的等比缩）。"关闭"这件事一个 ✕ 就够，
    /// 46 宽刚好（图标 18 ＋ 左右各 14），段就不用缩那么狠。
    /// </summary>
    private const float ActionWNarrow = 46f;

    private static float ActionWidthOf(BandAction a) =>
        a == BandAction.CloseBoard ? ActionWNarrow : ActionW;

    private const double ClearHoldMs = 800;

    private RectF ActionRect()
    {
        var band = BandRect();
        float right = band.MaxX - BarInset();
        float cy = (band.MinY + band.MaxY) * 0.5f;
        float w = ActionWidthOf(CurAction);
        return new RectF
        {
            MinX = right - w, MinY = cy - Tokens.SegmentHeight * 0.5f,
            MaxX = right, MaxY = cy + Tokens.SegmentHeight * 0.5f,
        };
    }

    /// <summary>动作按钮要占的横向空间（色片 / 分段 / 滑条都得让位）。</summary>
    private float ActionReserve =>
        CurAction == BandAction.None ? 0f : ActionWidthOf(CurAction) + 10f;

    /// <summary>清空的按住计时（-inf = 没在按）与"刚按完闪一下"的时刻。</summary>
    private double _actionHoldFrom = double.NegativeInfinity;
    private double _actionFlashUntil = double.NegativeInfinity;
    private bool ActionHolding => !double.IsNegativeInfinity(_actionHoldFrom);

    /// <summary>
    /// **双击选择格 = 全选**（用户 2026-09-27 定）。照的是 InkClass 那个"经典交互"：
    /// 它那边是 500ms 内双击选择图标 = 全选（`MW_FloatBar.cs` 的 500ms 双击检测）。
    ///
    /// ⚠ **双击能跟"单击换档"共存，全靠"只有两档"**：连点两下正好转两格、回到原档，
    ///   所以双击的净效果就是"全选，模式没动"（见 `Activate` 里 case 7 那一支）。
    ///   哪天选择方式加到**三档**，这条就不成立了（连点两下会净退一格）——
    ///   那时候要么去掉双击全选，要么加判定延迟（那时单击换档会迟钝，不划算）。
    /// </summary>
    private const double SelectDoubleClickMs = 500;
    private double _lastSelCellClickMs = double.NegativeInfinity;
    /// <summary>
    /// 双击的**第一下之前**那一档选择方式。
    /// 第二下用它把档位抵回去（第一下若已经切过档，这里正好抵掉）——
    /// 这样"从别的工具双击进来"和"已经在选择工具上双击"两种情况的结果一致：
    /// **全选 + 模式回到双击前**。
    /// </summary>
    private SelectMode _selModeBeforeDoubleClick = SelectMode.Rect;

    /// <summary>按住进度 0..1（画那个从左往右的填充）。</summary>
    private float HoldProgress()
    {
        if (!ActionHolding || _host == null) return 0f;
        return (float)Math.Clamp((_host.NowMs - _actionHoldFrom) / ClearHoldMs, 0.0, 1.0);
    }

    /// <summary>
    /// 每帧推进一次"按住清空"。**必须在每帧调**（和面板动画同一套节奏）：
    /// 只在 PointerUp 里判时间的话，老师按住不放、松手前那一下永远不会触发。
    /// </summary>
    private void UpdateBandAction()
    {
        if (!ActionHolding || _host == null) return;
        if (_host.NowMs - _actionHoldFrom < ClearHoldMs) return;
        _actionHoldFrom = double.NegativeInfinity;
        _actionFlashUntil = _host.NowMs + 260;
        _host.Commands.Clear();
        Invalidate();
    }

    /// <summary>
    /// 动作按钮：图标 ＋ 文字；清空那条按住时从左边往右填进度。
    ///
    /// 白板那条的「关闭白板」只有**一个 ✕**（窄版，没有文字，见 <see cref="ActionWNarrow"/>），
    /// 而且**板已经关着时整块压暗、点了也不动**——那时候关板没有意义
    ///（压暗的写法沿用"到顶时上一屏"那一套：同色降到 30% 不透明度）。
    /// </summary>
    private void DrawBandAction(ID2D1DeviceContext ctx)
    {
        var a = CurAction;
        if (a == BandAction.None || !RailOpen) return;
        var r = ActionRect();
        bool clear = a == BandAction.Clear;
        bool closeBoard = a == BandAction.CloseBoard;
        bool paste = a == BandAction.PasteImage;
        bool dim = closeBoard && (_host == null || !_host.State.Board);
        var ink = dim ? new Color4(InkCol.R, InkCol.G, InkCol.B, 0.30f) : InkCol;
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f, 7f);
        var tint = clear ? new Color4(0.90f, 0.35f, 0.25f, 1f) : Tokens.Accent;

        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(tint.R, tint.G, tint.B, (dim ? 0.03f : 0.08f))));
        float t = HoldProgress();
        if (t > 0.002f)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY,
                                     r.MinX + (r.MaxX - r.MinX) * t, r.MaxY), 7f, 7f),
                Brush(ctx, new Color4(tint.R, tint.G, tint.B, 0.45f)));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        // 窄版（关闭白板）：图标居中占满，没有文字那一栏
        if (closeBoard)
        {
            IconAtlas.DrawCentered(ctx, "dismiss", r, 16f, Brush(ctx, ink));
            if (_host != null && _host.NowMs < _actionFlashUntil)
                ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 2f);
            return;
        }

        var iconBox = new RectF { MinX = r.MinX + 6f, MinY = r.MinY, MaxX = r.MinX + 28f, MaxY = r.MaxY };
        IconAtlas.DrawCentered(ctx, paste ? "image" : clear ? "broom" : "selectAll", iconBox, 16f, Brush(ctx, ink));
        var labelBox = new RectF { MinX = r.MinX + 28f, MinY = r.MinY, MaxX = r.MaxX - 6f, MaxY = r.MaxY };
        _widgets.Text(ctx, paste ? "粘贴图片" : clear ? "清空" : "全选", labelBox, 12.5f, Brush(ctx, InkCol));

        if (_host != null && _host.NowMs < _actionFlashUntil)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 2f);
    }

    // ---- 粗细预览：数字 ＋ **真实大小** --------------------------------------
    //
    // 用户 2026-09-17 的三句话：
    //   ①"笔和激光笔调节大小的时候可不可以显示笔号"（要一个读数）；
    //   ②"调节大小的时候那个点和实际大小是不是应该一样大，但是太大了装不下，
    //      我又不希望改动界面怎么办"；
    //   ③"面积橡皮擦的大小调整的时候也能预览实际大小"。
    //
    // 矛盾在于：滑条右端那个预览点最多只能占带子高（34 像素），而笔能到 40、
    // 整笔橡皮的圈能到 96、面积橡皮的块高能到 259 —— 装不下。
    //
    // 解法：**预览画到带子外面去**（带子在上就往上画，在下就往下画），
    // 并且把它算进 `QueryBounds()`——引擎是按那份矩形裁剪界面画的，
    // 不这么做画出去的部分会被裁掉。面板尺寸、布局一个像素都不用改，
    // 而且它只在"拖滑条 / 指针停在滑条上"时出现，平时那份矩形一点都不变。

    private bool SizePreviewVisible =>
        BandOpen() && BandHasSlider && (_sliderDragging || _hover == 300);

    /// <summary>真实落点的宽高（逻辑像素）。**和引擎里那套落点同源**（都读 st.Width）。</summary>
    private (float W, float H) TrueSize(in UiState st)
    {
        // 白板那一格：预览的不是"落点"，而是**板色在这个不透明度下的样子**（一块小色片）
        if (_bandCell == 2) return (56f, 34f);
        return st.Tool switch
        {
            Tool.Eraser => (st.Width * 2f, st.Width * 2f),      // 引擎给的是半径 → 画出来是直径
            Tool.PixelEraser => (st.Width, st.Width * 1.618f),  // 黄金比例矩形（和落点一模一样）
            _ => (st.Width, st.Width),                          // 笔 / 荧光笔 / 激光 = 线宽
        };
    }

    private RectF SizePreviewRect(in UiState st)
    {
        var (w, h) = TrueSize(st);
        var band = BandRect();
        var (left, right) = SliderTrackRange();
        float kx = left + (right - left) * SliderT(st);
        float bw = MathF.Max(w, 58f) + 20f;      // 形状 + 左右留白
        float bh = h + 36f;                      // 上：形状；下：数字
        float top = BandAbove() ? band.MinY - 8f - bh : band.MaxY + 8f;
        // 横向夹在屏幕里：滑钮在最左/最右时，气泡会被推到屏幕外看不见
        float x0 = kx - bw * 0.5f, x1 = kx + bw * 0.5f;
        float lo = _screen.MinX + 4f, hi = _screen.MaxX - 4f;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }
        return new RectF
        {
            MinX = x0, MinY = top,
            MaxX = x1, MaxY = top + bh,
        };
    }

    /// <summary>读数。全是**逻辑像素**，和引擎里那个值同源（面积橡皮报"宽×高"）。</summary>
    private string SizeLabel(in UiState st)
    {
        if (_bandCell == 2) return $"{st.BoardOpacity * 100f:F0}%";
        return st.Tool switch
        {
            Tool.PixelEraser => $"{st.Width:F0}×{st.Width * 1.618f:F0}",
            Tool.Eraser => $"{st.Width * 2f:F0}",
            _ => $"{st.Width:F0}",
        };
    }

    private void DrawSizePreview(ID2D1DeviceContext ctx, in UiState st)
    {
        if (!SizePreviewVisible) return;

        var box = SizePreviewRect(st);
        var (w, h) = TrueSize(st);
        var bg = new RoundedRectangle(new Vortice.RawRectF(box.MinX, box.MinY, box.MaxX, box.MaxY), 10f, 10f);
        ctx.FillRoundedRectangle(bg, Brush(ctx, _dark ? Tokens.PanelDark : Tokens.PanelLight));
        ctx.DrawRoundedRectangle(bg, Brush(ctx, BorderCol), 1f);

        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = box.MinY + 6f + h * 0.5f;
        DrawTrueSizeShape(ctx, st, new Vector2(cx, cy), w, h);

        var textBox = new RectF
        {
            MinX = box.MinX + 4f, MinY = box.MinY + 6f + h,
            MaxX = box.MaxX - 4f, MaxY = box.MaxY - 3f,
        };
        _widgets.Text(ctx, SizeLabel(st), textBox, 12f, Brush(ctx, InkCol));
    }

    /// <summary>按真实尺寸画一个"这一笔/这一块有多大"的样子。配色和落点光标一致。</summary>
    private void DrawTrueSizeShape(ID2D1DeviceContext ctx, in UiState st, Vector2 c, float w, float h)
    {
        var white = new Color4(1f, 1f, 1f, 0.85f);
        var dark = new Color4(0.22f, 0.28f, 0.38f, 0.9f);

        // 白板那一格：一块**板色在这个不透明度下的色片**——所见即所得
        if (_bandCell == 2)
        {
            var r = new Vortice.RawRectF(c.X - w * 0.5f, c.Y - h * 0.5f, c.X + w * 0.5f, c.Y + h * 0.5f);
            var b = st.BoardColor;
            ctx.FillRoundedRectangle(new RoundedRectangle(r, 6f, 6f),
                                     Brush(ctx, new Color4(b.R, b.G, b.B, b.A * st.BoardOpacity)));
            ctx.DrawRoundedRectangle(new RoundedRectangle(r, 6f, 6f), Brush(ctx, dark), 1.4f);
            return;
        }
        switch (st.Tool)
        {
            case Tool.Eraser:                       // 和整笔橡皮的落点圆环同一套
                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f),
                                Brush(ctx, new Color4(0.35f, 0.55f, 0.95f, 0.10f)));
                ctx.DrawEllipse(new Ellipse(c, w * 0.5f + 0.9f, w * 0.5f + 0.9f), Brush(ctx, white), 1.8f);
                ctx.DrawEllipse(new Ellipse(c, w * 0.5f, w * 0.5f), Brush(ctx, dark), 1.8f);
                break;

            case Tool.PixelEraser:                  // 和面积橡皮的落点矩形同一套
            {
                var r = new Vortice.RawRectF(c.X - w * 0.5f, c.Y - h * 0.5f, c.X + w * 0.5f, c.Y + h * 0.5f);
                ctx.FillRectangle(r, Brush(ctx, new Color4(0.35f, 0.55f, 0.95f, 0.22f)));
                ctx.DrawRectangle(r, Brush(ctx, dark), 1.8f);
                break;
            }

            case Tool.Laser:                        // 和激光落点同一个红点
                ctx.FillEllipse(new Ellipse(c, w * 0.5f + 1f, w * 0.5f + 1f), Brush(ctx, white));
                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f),
                                Brush(ctx, new Color4(1f, 0.16f, 0.16f, 0.95f)));
                break;

            case Tool.Highlighter:
            {
                // **圆盘**，和屏幕上的落点一模一样（`Overlay.DrawHighlighterDisc`：
                // 半径 = 半个笔宽，填充用荧光笔本色，外面套白／深两层描边）。
                //
                // 我第一版在这儿画了"一根那么粗的短条"，理由是"荧光笔画出来就是一大条"——
                // 那是**想当然**：点一下的落点就是个圆盘，短条反而和真实落点对不上，
                // 用户一眼就看出来了（"荧光笔的预览怎么不是圆的"）。
                // 规矩只有一条：**预览 = 那个工具在屏幕上的落点**。
                float r = w * 0.5f;
                ctx.FillEllipse(new Ellipse(c, r, r),
                                Brush(ctx, new Color4(st.HighlighterColor.R, st.HighlighterColor.G,
                                                      st.HighlighterColor.B, 0.22f)));
                ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f), Brush(ctx, white), 1.5f);
                ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f),
                                Brush(ctx, new Color4(0.22f, 0.28f, 0.38f, 0.55f)), 1.5f);
                break;
            }

            default:                                // 笔：一个实心圆 = 这一笔有多粗（用**当前笔色**）
                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f), Brush(ctx, st.Color));
                break;
        }
    }

    private int HitSwatch(float x, float y)
    {
        if (!BandHasSwatches) return -1;
        if (!RailOpen) return -1;      // 还是一条色线时不吃点击（指针一靠近它就会张开）
        for (int i = 0; i < SwatchCount; i++)
            if (SwatchRect(i).Contains(x, y)) return i;
        return -1;
    }

    private int HitSegment(float x, float y)
    {
        int n = BandSegmentCount;
        if (!RailOpen) return -1;
        for (int i = 0; i < n; i++)
        {
            // 白板那一格中间那块「第 N 屏」是**只读**的（不是按钮）：它夹在两个翻页箭头
            // 中间、和它们贴在一起组成一个"翻页器"，点它什么都不该发生。
            if (_bandCell == 2 && BoardSegAt(i).Kind == BoardSegKind.PageLabel) continue;
            if (SegmentRect(i, n).Contains(x, y)) return i;
        }
        return -1;
    }

    /// <summary>色线 / 设置条该不该张开。判据：指针碰到它、或者正在拖滑条。</summary>
    private void UpdateRail()
    {
        if (!BandVisible())
        {
            _rail.To(0f, 0);
            _railHover = false;
            return;
        }

        // 穿透：上带**永远保持收起的那条 6 像素色线**，不许张成设置条
        //（2026-10-02 用户口径：点穿透只是把它"收起来"，不是整条消失——面板高度不变，
        //  贴边隐藏露出来的还是它；但也不再像别的格子那样张着设置条）。
        // 进穿透那一下已经在 OnStateChanged 里启动折叠动画；这里兜住"之后又被谁强行张开"
        //（测试钩子、以及悬停意图残留），并让那两个计时器失效。
        if (_host.State.PassThrough)
        {
            _railHover = false;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            if (_rail.Value >= 0.5f) _rail.To(0f, Tokens.RailMs);
            return;
        }

        // 正在拖滑条：一直开着，不参与悬停那套计时
        // （手滑到轨道外面一点点不该让设置条收掉——拖到一半收掉是最气人的一种）
        if (_sliderDragging)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 色带常开（2026-10-05 用户点名的开关）：一直摊着，不参与悬停那套收放。
        if (_railPinned)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        double now = _host.NowMs;

        // 触摸没有悬停：手指点在**面板任意处**（点工具格也算）就把设置条张开，并保持一段
        // （松手后 2.5s 内不收）——不然触摸用户只能去点那条 6 像素的色线，很难点中
        // （2026-10-05 用户实测："点击图标色带不会展开，必须点色带位置"，触摸屏上太麻烦）。
        if (now < _railTouchHoldUntilMs)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        if (_railHover)
        {
            _railExitAtMs = double.NegativeInfinity;
            if (_rail.Value >= 0.5f) { _railEnterAtMs = double.NegativeInfinity; return; }
            if (double.IsNegativeInfinity(_railEnterAtMs)) _railEnterAtMs = now;
            else if (now - _railEnterAtMs >= Tokens.RailShowDelayMs) _rail.To(1f, Tokens.RailMs);
            return;
        }

        _railEnterAtMs = double.NegativeInfinity;
        if (_rail.Value <= 0.001f) { _railExitAtMs = double.NegativeInfinity; return; }
        if (double.IsNegativeInfinity(_railExitAtMs)) _railExitAtMs = now;
        else if (now - _railExitAtMs >= Tokens.RailHideDelayMs) _rail.To(0f, Tokens.RailMs);
    }

    /// <summary>
    /// 色线的"碰到"判定区：**按张开后的高度**算。
    ///
    /// 不这么做的话会来回抖：线一张开，它的矩形就往上长了 28 像素，
    /// 指针（还停在原来那条线的位置）立刻落到判定区外面 → 又收回去 → 再张开……
    /// </summary>
    private RectF RailZone()
    {
        var bar = BarRect();
        // ⚠ 这里以前是 `Tokens.BandHeight`（**一行**的高），图形那一格改成多行之后就短了一截：
        // 指针挪到上面那几行会被判成"离开面板"、带子当场收（用户 2026-09-20 报的 bug）。
        float h = BandBlockFull() + Tokens.RailHoverPad * 2f;
        return BandAbove()
            ? new RectF
            {
                MinX = bar.MinX, MinY = bar.MinY - BandGap - h,
                MaxX = bar.MaxX, MaxY = bar.MinY - BandGap + Tokens.RailHoverPad,
            }
            : new RectF
            {
                MinX = bar.MinX, MinY = bar.MaxY + BandGap - Tokens.RailHoverPad,
                MaxX = bar.MaxX, MaxY = bar.MaxY + BandGap + h,
            };
    }

    /// <summary>
    /// "焦点在悬浮框上"的判定区 = **主条 ∪ 设置条**（用户 2026-09-17 定的语义）。
    ///
    /// 以前只有**设置条自己那一条**算，于是老师想把鼠标从主条挪到色片上有两段路：
    /// 先碰到色线、等 120 毫秒张开、再点工具钉住，钉住了才敢把指针挪上去。
    /// 现在整块面板都算"焦点在面板上"：**指针在面板上它就张开，指针离开就收成色线**，
    /// 中间不用再点一次、也不会走两步就收回去。
    ///
    /// 注意判据用的是**主条 ∪ 色带**而不是"整个 UnionRect"：贴边隐藏时 UnionRect
    /// 会被夹到只剩 8 像素的露头，用它当判定区会让"贴边的面板"在指针没靠近时也展开。
    /// </summary>
    private RectF RailHoverZone()
    {
        var bar = BarRect();
        var rail = RailZone();
        return new RectF
        {
            MinX = MathF.Min(bar.MinX, rail.MinX),
            MinY = MathF.Min(bar.MinY, rail.MinY),
            MaxX = MathF.Max(bar.MaxX, rail.MaxX),
            MaxY = MathF.Max(bar.MaxY, rail.MaxY),
        };
    }

    /// <summary>
    /// 这一档显示哪几个色片（返回**色片表**里的下标）。
    ///
    /// 三张映射各有各的用法（表本身在 <see cref="Tokens"/> 里）：
    ///   · **荧光笔**（`HlSwatchIdx`）：用 `Tokens.HighlighterPalette` 那张亮色表，
    ///     5 个全给——2026-09-27 用户定的"独立一张亮色表"；
    ///   · **极简档的笔**只给 4 个：短胶囊（359 宽）里塞 12 个色片、再减掉滑条占的那 146，
    ///     每个只剩 11 像素宽——点都点不准（用户 2026-09-17："极简模式的色带展开栏里面的
    ///     内容排布有点问题"）。4 个的话每个 42 像素，和完整档一个手感。
    ///     颜色照假面板定的：**红 / 黑 / 蓝 / 白**（讲课时最常用的四支）；
    ///   · **完整档的笔**：12 个全给。
    ///
    /// ⚠ 极简档里**没有荧光笔那一格**（见 `MiniCells`），所以那里不会用到荧光笔表；
    ///    但映射还是给全了——哪天极简档加了荧光笔格，这里不用再补。
    /// ⚠ `MiniSwatchIdx` 里那几个数是**新色表的下标**：2026-09-27 换表时颜色位次动过
    ///   （绿和蓝对调、灰/青/粉/棕拿掉），所以这一行跟着改成了 { 1 红, 0 黑, 2 蓝, 7 白 }。
    /// </summary>
    private static readonly int[] MiniSwatchIdx = { 1, 0, 2, 7 };      // 红 / 黑 / 蓝 / 白
    private static readonly int[] FullSwatchIdx = CreateAllIdx(Tokens.Palette.Length);
    private static readonly int[] HlSwatchIdx = CreateAllIdx(Tokens.HighlighterPalette.Length);

    private static int[] CreateAllIdx(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    /// <summary>这一刻用的是荧光笔那张表吗（色片表 / 切色循环都看它）。</summary>
    private bool UsingHighlighterSwatches => _host != null && _host.State.Tool == Tool.Highlighter;

    /// <summary>这一刻生效的色片表（笔那张 / 荧光笔那张）。</summary>
    private (string Name, Color4 Color)[] SwatchTable =>
        UsingHighlighterSwatches ? Tokens.HighlighterPalette : Tokens.Palette;

    private int[] SwatchIdx => UsingHighlighterSwatches
        ? HlSwatchIdx
        : (_profile == Profile.Mini ? MiniSwatchIdx : FullSwatchIdx);
    private int SwatchCount => SwatchIdx.Length;
    private Color4 SwatchColor(int i) => SwatchTable[SwatchIdx[i]].Color;

    private void ActivateSwatch(int i) => _host.Commands.SetColor(SwatchColor(i));

    /// <summary>
    /// **已经是它了，再点一下 = 换下一个颜色**（用户 2026-09-27 定的）。
    ///
    /// 老师用着笔想换个色，原来是"把指针挪到色带上、瞄准某个色片"（还得先等色带张开）；
    /// 现在直接在工具格上连点就行——**手不用离开那一格**。荧光笔同理。
    ///
    /// 三条规矩：
    ///   · 循环范围 = **色带上看得见的那排色片**（`SwatchIdx`）：所见即所得，不会切到一个
    ///     色带上没有的颜色；色片的高亮圈就是"我现在是哪个色"的指示器；
    ///   · 顺序 = 色片表顺序（常用的排前面，见 `Tokens.Palette` 的注释）；
    ///   · 当前色不在表里（比如从选中面板改过色）→ 落到**第 1 个**。
    ///
    /// ⚠ **只在"色带本来就在这一格"时才切色**（调用处的判据）：老师从图形面板点回笔那一格，
    ///   意思是"把笔拿回来"，那时候悄悄换个颜色是最气人的（同图形那一格"不动工具"的规矩）。
    /// </summary>
    private void CycleColor()
    {
        int n = SwatchCount;
        if (n == 0) return;
        var cur = _host.State.PaletteBase;
        int at = -1;
        for (int i = 0; i < n; i++)
            if (SameColor(SwatchColor(i), cur)) { at = i; break; }
        _host.Commands.SetColor(SwatchColor((at + 1) % n));
        Invalidate();
    }

    /// <summary>
    /// 两个颜色算不算同一个（阈值 0.02）。
    /// **色片高亮和切色找位置共用这一把尺子**——各写一份的话迟早不一致：
    /// 会出现"高亮看着在这个色上，点一下却从下一个色开始切"这种事。
    /// </summary>
    private static bool SameColor(in Color4 a, in Color4 b) =>
        MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;

    /// <summary>
    // [2026-10-05 用户定] `CycleSelectMode`（点选择格/再按 Ctrl+M 切矩形↔套索）已删除：
    // 爱用矩形的一直用矩形、爱用套索的一直用套索；子类型在面板上带那两段里选（见 Activate case 7）。

    /// <summary>
    /// 点了一下虚实线那一格：**三档轮流**（实线 → 虚线 → 点线 → 实线）。
    ///
    /// 和橡皮那一格"点一下换一次"是同一个约定——上带里摆不下下拉框，**点就是切**；
    /// 三档而不是两档，是因为引擎那边线型本来就是三档（见 StrokeDash），
    /// 少给一档等于把"点线"藏起来。
    ///
    /// 顺手起一次换挡动画（见 <see cref="_dashFade"/>）：**旧档要先当场记下来**，
    /// 不然命令一发、状态就变了，动画开始之后再也问不出"从哪一档来的"。
    /// </summary>
    private void CycleDash()
    {
        var cur = _host.State.Dash;
        var next = cur switch
        {
            StrokeDash.Solid => StrokeDash.Dashed,
            StrokeDash.Dashed => StrokeDash.Dotted,
            _ => StrokeDash.Solid,
        };
        _dashFadeFrom = cur;
        _dashFade.Jump(0f);
        _dashFade.To(1f, Tokens.RailMs);
        _host.Commands.SetDash(next);
        Invalidate();
    }

    private void ActivateSegment(int i)
    {
        // 色带上的动静也算"离开了那一格"：双击序列断掉（同 `Activate` 里那一句）。
        _lastSelCellClickMs = double.NegativeInfinity;
        switch (_bandCell)
        {
            case 2:                       // 白板：[‹] 第 N 屏 [›] │ [白][绿][黑] │ [底纹][间距]
            {
                // 这一格**只管白板的事**：翻页永远是白板的"上一屏 / 下一屏"。
                //
                // ⚠ 2026-09-26 改回来过一次：中间曾让它"放映时变成 PPT 的上一页/下一页"，
                // 用户的结论是**不要**——原话"取消掉白板区的翻页，白板区不需要 ppt 翻页"。
                // 语义上也是对的：这一格是"这块板怎么摆"，不该管幻灯片翻到第几张。
                // PPT 的翻页入口只有**底部那两条**（`PptBar`）＋ PPT 自己的遥控器/键盘。
                //
                // 段序**不写死下标**，一律问 `BoardSegAt`（段表只有 BoardBand 那一份；
                // 2026-09-27 重排之前这里写的是 `i == 0` / `i == 4`，段序一动就会静默点错）。
                var seg = BoardSegAt(i);
                switch (seg.Kind)
                {
                    case BoardSegKind.PageUp: _host.Commands.FlipPage(false); break;
                    case BoardSegKind.PageDown: _host.Commands.FlipPage(true); break;
                    case BoardSegKind.PageLabel: break;    // 只读的一块，点它什么都不做

                    // 板色 / 底纹 / 间距：都是"要用板"的意思，所以都顺手把板打开
                    //（底纹只在板面上画得出来，不打开就等于改了看不见——这一条原来是靠
                    //  "白板没开就把那两行压暗"来守的，搬进色带之后改成"顺手打开"更好用）。
                    case BoardSegKind.Color:
                        _host.Commands.SetBoardColor(InkPalette.BoardPresets[seg.Idx].Color);
                        _host.Commands.SetBoard(true);
                        break;
                    case BoardSegKind.Pattern:
                    {
                        var st = _host.State;
                        int next = (st.BoardPattern + 1) % PatternNames.Length;
                        _host.Commands.SetBoardPattern(next, st.BoardPatternStep);
                        _host.Commands.SetBoard(true);
                        SavePrefs();
                        Invalidate();
                        break;
                    }
                    case BoardSegKind.Step:
                    {
                        var st = _host.State;
                        int idx = Array.FindIndex(PatternSteps, v => MathF.Abs(v - st.BoardPatternStep) < 0.5f);
                        float next = PatternSteps[(idx + 1 + PatternSteps.Length) % PatternSteps.Length];
                        _host.Commands.SetBoardPattern(st.BoardPattern, next);
                        _host.Commands.SetBoard(true);
                        SavePrefs();
                        Invalidate();
                        break;
                    }
                }
                break;
            }
            case 6:                       // 整笔擦 / 面积擦 —— 引擎里是**两个工具**
                _host.Commands.SetTool(i == 0 ? Tool.Eraser : Tool.PixelEraser);
                break;
            case 7:                       // 矩形框选 / 自由套索
                _host.Commands.SetSelectMode(i == 0 ? SelectMode.Rect : SelectMode.Lasso);
                break;
            case 8:                       // 图形那一格：**两行**拉平编号，顺序见两张表
                if (i >= 0 && i < ShapeBandSegments)
                {
                    // 最后那一段是**动作**：点开图库面板（"我的图形"，2026-09-22）。
                    // 它不进 ShapeRows（那张表是工具表），所以这里**必须先判**。
                    if (i >= ShapeSegmentCount) { _host.Commands.ToggleLibraryPanel(); break; }

                    var picked = ShapeToolAt(i);
                    // **同一个图形格再点一次 = 换一档**。只对三种图形这样，别的照旧"再点=选中它"：
                    //   · 抛物线：换开口方向（用户 2026-09-20 定："选中框的抛物线按钮取消，
                    //     我不打算从这个转开口" → 朝向改到**画之前**定）；
                    //   · 直线：换线型（用户 2026-09-20 定："点击直线的图标，它会变成虚线，
                    //     再点击变成点虚线，再点击又变成直线……这样就省了好几个空间格"）；
                    //   · 棱柱 / 棱锥 / 棱台：换底面几边形（用户 2026-09-20 定："我想想能不能做成
                    //     像直线切换那样切换三四五六"）。**三格各记各的档**（换谁只动谁），
                    //     判据只有 `ShapeSpec.HasSideCount` 一处——加棱锥 / 棱台时这里差点漏掉。
                    if (picked == Tool.Parabola && _host.State.Tool == Tool.Parabola)
                        _host.Commands.CycleParabolaAxis();
                    else if (picked == Tool.Line && _host.State.Tool == Tool.Line)
                        _host.Commands.CycleLineDash();
                    // 双曲线：换"画不画渐近线"（2026-09-22，用户："增加两挡，有渐近线和无渐近线
                    // ……图标就按照有渐近线和无渐近线"）。
                    else if (picked == Tool.Hyperbola && _host.State.Tool == Tool.Hyperbola)
                        _host.Commands.CycleHyperbolaAsymptotes();
                    // 椭圆（带焦点）：换"画不画焦点三角形"（2026-09-22，用户："椭圆也有两档"）。
                    else if (picked == Tool.ConicEllipse && _host.State.Tool == Tool.ConicEllipse)
                        _host.Commands.CycleEllipseFocusTriangle();
                    // **坐标系：换"要不要网格"**（用户 2026-09-24："我打算把它挪到图形里面的那个
                    // 坐标系……点一下切换成网格，点一下网格没了"）。
                    // 原来这一档在「更多」抽屉里（"坐标系网格"那一行），挪到这一格之后
                    // **和抛物线/直线/双曲线一样是"同一个格再点一次换一档"**——一致，而且
                    // 画之前手指就在这一格上，不用再去抽屉里找。
                    // 落盘只看"改的是不是新画的默认值"（返回值 0 = 是；改已画的对象不算偏好）。
                    else if (picked == Tool.Coordinate && _host.State.Tool == Tool.Coordinate)
                    {
                        if (_host.Commands.ToggleCoordGrid() == 0) SavePrefs();
                    }
                    else if (ShapeSpec.HasSideCount(picked) && _host.State.Tool == picked)
                        _host.Commands.CycleSolidSides();
                    else
                        _host.Commands.SetTool(picked);
                }
                break;
            case 9:                       // 截图：[截图][隐藏窗口截图]（"粘贴图片"已挪到右端的动作按钮）
                // 名字照微信那套（8.3.1）；**点哪一段就用哪种截法进屋**——
                // 段本身是"入口"：`EnterCapture` 一进去就整屏灰下来（详见 IEngineCommands）。
                // 以前点格子就只把工具选中、什么都不发生，"仿微信仿得不像"就是这条。
                _host.Commands.SetCaptureHideInk(i == 1);
                _host.Commands.EnterCapture();
                break;
        }
    }

    // ---- 档位与钉住（「更多」面板「工具条」那一组在用）--------------------

    private int ProfileIndex() => (int)_profile;

    /// <summary>主条 ＋ 上带。</summary>
    private RectF PanelRect()
    {
        var bar = BarRect();
        var band = BandRect();
        if (band.MaxY - band.MinY < 2f) return bar;
        return new RectF
        {
            MinX = bar.MinX, MinY = Math.Min(bar.MinY, band.MinY),
            MaxX = bar.MaxX, MaxY = Math.Max(bar.MaxY, band.MaxY),
        };
    }

    private bool IsToggleRow(int i)
        => Rows[i].Kind is Row.DarkTheme or Row.AutoHide or Row.RailPin or Row.Tooltip or Row.DwellShape
           or Row.Pressure or Row.FineStroke or Row.RestoreInk or Row.PptAutoSave or Row.TouchGestures;

    /// <summary>
    /// **自检用**：这一行到底有没有画开关。
    ///
    /// 为什么专门开一个出口：2026-10-07 加「精细笔迹」那一行时，
    /// 我把行、位置、状态、点击、落盘全接好了，**唯独漏了把它加进 <see cref="IsToggleRow"/>**
    /// —— 于是**标签画出来了、开关没画**，用户看到的是"这一行怎么没有开关"。
    ///
    /// 而当时的自检只做"点一下 → 状态翻转"，**点击判定看的是整行矩形**，
    /// 所以开关画没画它都能过 —— **测试没盖住真正错的地方**。
    /// 现在把"有没有开关"变成一条可断言的事实：以后加开关行漏掉这一处，自检就会红。
    /// </summary>
    internal bool IsToggleRowForTest(int i) => IsToggleRow(i);

    /// <summary>自检用：按标签问"这一行有没有开关"（找不到标签返回 false）。</summary>
    internal bool IsToggleRowByLabelForTest(string label)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Label == label) return IsToggleRow(i);
        return false;
    }

    /// <summary>
    /// 这一行现在是不是压暗（点了没反应）。
    /// 目前只有一种来源：表里写死的（检查更新还没做）。
    ///
    /// ⚠ 2026-09-27 之前还有一条"白板没开时底纹两行压暗"——那两行已经搬进白板色带
    /// （见 <see cref="BoardSegKind"/>），所以这条特例跟着删了。
    /// </summary>
    private bool IsGrayRow(int i) => Rows[i].Gray;

    /// <summary>底纹三档的名字（0/1/2），和引擎那边的取值一一对应。</summary>
    private static readonly string[] PatternNames = { "无", "方格", "横线" };
    /// <summary>
    /// 底纹间距的档位（逻辑像素）。**五档**（2026-09-27 用户："间距可以档位，但需要多几档"）：
    /// 20 最细（密集格子当坐标纸）、40 是默认（＝引擎默认值，出厂就在正中间）、96 最粗（当横线纸）。
    /// ⚠ 引擎那边夹在 8～240 之间（`Engine.SetBoardPatternFromUi`），这几个值都在里面。
    /// ⚠ **最多六档**：档位点是竖排的（点距 5、段高只有 26），第七个就漏出段外了。
    /// </summary>
    private static readonly float[] PatternSteps = { 20f, 30f, 40f, 64f, 96f };

    private static string PatternName(int p) =>
        PatternNames[Math.Clamp(p, 0, PatternNames.Length - 1)];

    private void ActivateRow(int i)
    {
        switch (Rows[i].Kind)
        {
            case Row.DarkTheme:
                _dark = !_dark;
                SavePrefs();
                PushFloatingTheme();       // 浮层（操作条/小面板）也得跟着换
                break;
            case Row.AutoHide:
                _hideEnabled = !_hideEnabled;
                _peek.Jump(1f);          // 刚打开时先给个完整的，别一开就缩起来
                SavePrefs();
                break;

            // 色带常开（2026-10-05）：打开时立刻把它摊开；关掉后交回悬停那套（触摸仍有自适应）。
            case Row.RailPin:
                _railPinned = !_railPinned;
                if (_railPinned) _rail.To(1f, Tokens.RailMs);
                SavePrefs();
                break;

            // 悬停提示（2026-10-02）：只影响界面自己，落盘走 "tooltip" 那一项。
            // 关掉时顺手把已经显示的提示清掉（不然它要等下一次移开才消失）。
            case Row.Tooltip:
                _tipEnabled = !_tipEnabled;
                if (!_tipEnabled) HideTip();
                _host.Commands.SetTooltips(_tipEnabled);   // 引擎自己画的浮层跟着开关
                SavePrefs();
                break;

            // 停顿成型：翻转开关 → 推给引擎 → 落盘（**只写"关过的"那一份**：
            // 配置里没有这一项就是默认开，以后默认值改了老配置不会把新默认顶掉）。
            case Row.DwellShape:
                _host.Commands.SetDwellShape(!(_host.State.DwellShapeOn));
                SavePrefs();
                break;

            // 压感粗细（2026-10-01）：同一条规矩——引擎是权威，界面翻转后落盘。
            // 关掉是**渲染期**的：整块板立刻等宽，文档里的压力数据不动。
            case Row.Pressure:
                _host.Commands.SetPressure(!_host.State.PressureOn);
                SavePrefs();
                break;

            // 精细笔迹（2026-10-07）：原始输入补点的总开关。引擎是权威，界面翻转后落盘。
            case Row.FineStroke:
                _host.Commands.SetFineStroke(!_host.State.FineStrokeOn);
                SavePrefs();
                break;

            // 触摸手势总开关（2026-10-05）：关掉只剩单指书写（双指/三指/长按/漫游全停用）。
            // 引擎是权威（渲染/手势都在它那边），界面翻转后落盘到 "touch.gestures"。
            case Row.TouchGestures:
                _host.Commands.SetTouchGestures(!_host.State.TouchGesturesOn);
                SavePrefs();
                break;

            // [停用 2026-10-05] 墨迹预测（老预测系统）：
            // case Row.Predict:
            //     _host.Commands.SetPredict(!_host.State.PredictOn);
            //     SavePrefs();
            //     break;

            // 墨迹三条偏好（原来在「墨迹」页）：只写 `ui.*`，
            // 默认值不落盘（restoreInk 默认关只写 "1"、pptAutoSave 默认开只写 "0"、
            // historyDays 默认永久写成 null = 删项）。
            case Row.RestoreInk:
                _host.SetPref("restoreInk", _host.GetPref("restoreInk") == "1" ? null : "1");
                break;
            case Row.PptAutoSave:
                _host.SetPref("pptAutoSave", _host.GetPref("pptAutoSave") == "0" ? null : "0");
                break;
            case Row.HistoryDays:
                _host.SetPref("historyDays", _host.GetPref("historyDays") switch
                {
                    "90" => "30", "30" => "7", "7" => null, _ => "90",
                });
                break;
        }
        Invalidate();
    }

    /// <summary>切档。切完要检查"当前工具还在不在这一档里"——不在就落到笔。</summary>
    private void SetProfile(Profile p)
    {
        _profile = p;
        if (PosOf(CellForTool(_host.State.Tool)) < 0)
            _host.Commands.SetTool(Tool.Pen);
        _hover = -1;
        _press = -1;
        SavePrefs();
        Invalidate();
    }

    /// <summary>钉住 / 取消钉住。笔、橡皮、「更多」是安全项（取消了就没法用），不许动。</summary>
    private void TogglePin(int cell)
    {
        // 下标越界在这里直接挡住：命中编号一旦串段（历史上真发生过一次），
        // 这里是最后一道防线——界面组件不该因为一个下标把小命丢给引擎的异常阶梯。
        if (cell <= 0 || cell >= Cells.Length) return;
        if (!CanUnpin(cell)) return;
        _pinned[cell] = !_pinned[cell];
        SetProfile(Profile.Custom);
    }

    private bool _moreOpen;
    private readonly Anim _more;          // 0 = 关、1 = 全开（兼遮罩透明度）
    private readonly Anim _moreH;         // 面板高度：换页时动画到目标高（不跳）
    private MorePage _morePage = MorePage.Home;
    private int _moreHover = MoreHitNone;
    private int _morePress = -1;
    private string _hubHint = "";         // 启动器状态行的临时提示（点置灰格/预留格时写）

    /// <summary>面板两页：启动器（主页）/ 设置子页。**没有页签**——底栏与返回箭头导航。</summary>
    private enum MorePage { Home = 0, Settings = 1 }

    // 尺度（逻辑像素）。tile 76、底栏同款；面板宽 = min(640, 55% 工作宽)。
    // 规矩见《规范-功能卡.md》§二/§3.4。
    private const float MorePad = 20f;
    private const float MoreHeaderH = 44f;
    private const float MoreGroupGap = 14f;
    private const float MoreGroupHeadH = 26f;
    private const float MoreTile = 76f;
    private const float MoreTileGap = 10f;
    private const float MoreStatusH = 20f;
    private const float MoreProfileH = 34f;
    private const float MoreChipH = 40f;
    private const float MoreChipGap = 8f;
    private const int MoreChipCols = 6;
    private const float MoreRowH = 48f;
    /// <summary>
    /// 设置页左列两组各几行。**加行时三处一起改**：这里的数字、`Rows` 表、`MoreRowRect`。
    /// 2026-10-02 加「悬停提示」那一行时就是这么改的（原来这两个数写死在
    /// `SetWriteHeadRect` 和 `MoreLowerH` 里，两处各写一遍迟早漏一处）。
    /// </summary>
    private const int LookRowCount = 4;    // 外观：深色主题 / 贴边隐藏 / 色带常开 / 悬停提示
    private const int WriteRowCount = 4;   // 书写：停顿变图形 / 压感粗细 / 精细笔迹 / 触摸手势总开关（墨迹预测行已停用）
    private const float MoreColumnGap = 16f;
    private const float MoreWriteGap = 8f;
    private const float MoreSwitchW = 44f;
    private const float MoreSwitchH = 26f;
    private const float MoreCloseSize = 44f;
    private const float MoreProfileGap = 8f;
    private const float MoreChipTopGap = 10f;

    // 命中编号：一整块用**一个整数**编码；绘制/命中/执行都读同一个号。
    private const int MoreHitNone = -1;
    private const int MoreHitInside = 0;      // 面板空白：吃掉，但不关面板
    private const int MoreHitClose = 1;
    private const int MoreHitBack = 2;        // 设置子页返回启动器
    private const int MoreHitProfile = 20;    // 20..22（档位）
    private const int MoreHitChip = 30;       // 30+cell（钉住宫格；只有自定义档显示）
    private const int MoreHitRow = 60;        // 60+Rows 下标（设置子页的行）
    private const int MoreHitTile = 140;      // 140+启动器格子（0..5：计时/点名/分组 保存/打开/回放）
    private const int MoreHitBottom = 150;    // 150+底栏（0..3：设置/检查更新/重启/退出）

    /// <summary>面板宽：**min(640, 55% 工作宽)**（2026-10-02 拍板的分辨率规范化 B）。</summary>
    private float MoreWidth()
    {
        float workW = _work.MaxX - _work.MinX;
        return Math.Clamp(workW * 0.55f, 380f, 640f);
    }

    private float MoreTargetH() => _morePage == MorePage.Home ? MoreHomeH() : MoreSettingsH();

    /// <summary>启动器高：两组（课堂/墨迹）各一行 tile ＋ 状态行 ＋ 底栏一行。</summary>
    private float MoreHomeH()
        => MorePad * 2 + MoreHeaderH + 8
           + (MoreGroupHeadH + MoreTile + MoreGroupGap) * 2
           + MoreStatusH + 8 + MoreTile;

    /// <summary>设置子页高：工具条组 ＋ 下半两栏（左 外观＋书写；右 墨迹）。</summary>
    private float MoreSettingsH()
        => MorePad * 2 + MoreHeaderH + 8
           + MoreGroupHeadH + MoreProfileH + MoreChipTopGap
           + (ChipsShown ? 2 * MoreChipH + MoreChipGap : 42f)
           + MoreGroupGap + MoreLowerH;

    private static float MoreLowerH
        => MoreGroupHeadH + LookRowCount * MoreRowH + MoreWriteGap
           + MoreGroupHeadH + WriteRowCount * MoreRowH;

    /// <summary>钉住宫格**只在自定义档**显示（用户 2026-10-02："老是占地方"）。</summary>
    private bool ChipsShown => _profile == Profile.Custom;

    /// <summary>面板矩形：工作区正中、夹进屏幕；高度用 `_moreH` 动画（换页不跳）。</summary>
    private RectF MoreRect()
    {
        float h = MathF.Min(_moreH.Value <= 0f ? MoreTargetH() : _moreH.Value,
                            MathF.Max(200f, (_work.MaxY - _work.MinY) - 16f));
        float w = MathF.Min(MoreWidth(), MathF.Max(240f, (_work.MaxX - _work.MinX) - 16f));
        float cx = (_work.MinX + _work.MaxX) * 0.5f;
        float cy = (_work.MinY + _work.MaxY) * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };
        const float m = 8f;
        if (r.MinX < _screen.MinX + m) { r.MaxX += _screen.MinX + m - r.MinX; r.MinX = _screen.MinX + m; }
        if (r.MaxX > _screen.MaxX - m) { r.MinX -= r.MaxX - (_screen.MaxX - m); r.MaxX = _screen.MaxX - m; }
        if (r.MinY < _screen.MinY + m) { r.MaxY += _screen.MinY + m - r.MinY; r.MinY = _screen.MinY + m; }
        if (r.MaxY > _screen.MaxY - m) { r.MinY -= r.MaxY - (_screen.MaxY - m); r.MaxY = _screen.MaxY - m; }
        return r;
    }

    private float MoreContentW() => MoreRect().MaxX - MoreRect().MinX - MorePad * 2f;
    private float MoreLeftX() => MoreRect().MinX + MorePad;

    private RectF MoreHeaderRect() => new()
    {
        MinX = MoreLeftX(), MinY = MoreRect().MinY + MorePad,
        MaxX = MoreLeftX() + MoreContentW(), MaxY = MoreRect().MinY + MorePad + MoreHeaderH,
    };

    private RectF MoreCloseRect() => new()
    {
        MinX = MoreHeaderRect().MaxX - MoreCloseSize, MinY = MoreHeaderRect().MinY,
        MaxX = MoreHeaderRect().MaxX, MaxY = MoreHeaderRect().MinY + MoreCloseSize,
    };

    private RectF MoreBackRect() => new()
    {
        MinX = MoreHeaderRect().MinX, MinY = MoreHeaderRect().MinY,
        MaxX = MoreHeaderRect().MinX + MoreCloseSize, MaxY = MoreHeaderRect().MinY + MoreCloseSize,
    };

    // ---- 启动器几何 ----------------------------------------------------------

    /// <summary>第 0 组（课堂）的组头 y。</summary>
    private float HubSec0Y() => MoreRect().MinY + MorePad + MoreHeaderH + 8f;

    private int HubRows(int section)
    {
        int count = HubCount(section);
        return Math.Max(1, (count + TileCols() - 1) / TileCols());
    }

    private int HubCount(int section) => HubCounts[Math.Clamp(section, 0, HubCounts.Length - 1)];

    /// <summary>
    /// 每个分组的格子数：课堂 3 个（计时 / 点名 / 随机一人）；墨迹 **4 个**
    /// （保存墨迹 / 打开墨迹 / 墨迹回放 / **保存图片**，2026-10-02 加）。
    /// 格子编号（`HubTileCode`）从这里算出来，**不写死 3**——加格子不会再串段。
    /// </summary>
    private static readonly int[] HubCounts = { 3, 4 };

    /// <summary>第 section 组的组头矩形。</summary>
    private RectF HubHeadRect(int section)
    {
        float y = HubSec0Y();
        for (int s = 0; s < section; s++)
            y += MoreGroupHeadH + HubRows(s) * (MoreTile + MoreTileGap) + MoreGroupGap;
        return new RectF { MinX = MoreLeftX(), MinY = y, MaxX = MoreLeftX() + MoreContentW(), MaxY = y + MoreGroupHeadH };
    }

    private RectF HubTileRect(int section, int index)
    {
        var head = HubHeadRect(section);
        int cols = TileCols();
        int col = index % cols, row = index / cols;
        float x = MoreLeftX() + col * (MoreTile + MoreTileGap);
        float y = head.MaxY + row * (MoreTile + MoreTileGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + MoreTile, MaxY = y + MoreTile };
    }

    /// <summary>第 section 组第 index 个格子的全局编号（从 <see cref="HubCounts"/> 累加，
    /// 加一组/加一格都不用改这里）。</summary>
    private int HubTileCode(int section, int index)
    {
        int code = 0;
        for (int s = 0; s < section && s < HubCounts.Length; s++) code += HubCounts[s];
        return code + index;
    }

    private int TileCols()
        => Math.Clamp((int)((MoreContentW() + MoreTileGap) / (MoreTile + MoreTileGap)), 3, 6);

    private RectF HubStatusRect() => new()
    {
        MinX = MoreLeftX(), MaxY = HubBottomRect(0).MinY - 8f,
        MaxX = MoreLeftX() + MoreContentW(), MinY = HubBottomRect(0).MinY - 8f - MoreStatusH,
    };

    private RectF HubBottomRect(int i)
    {
        var head = HubHeadRect(1);
        float y = head.MaxY + HubRows(1) * (MoreTile + MoreTileGap) + MoreGroupGap + MoreStatusH + 8f;
        float x = MoreLeftX() + i * (MoreTile + MoreTileGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + MoreTile, MaxY = y + MoreTile };
    }

    // ---- 设置子页几何 --------------------------------------------------------

    private float SetToolHeadY() => MoreRect().MinY + MorePad + MoreHeaderH + 8f;

    private RectF MoreProfileRect(int i)
    {
        float w = (MoreContentW() - MoreProfileGap * 2f) / 3f;
        float x = MoreLeftX() + i * (w + MoreProfileGap);
        float y = SetToolHeadY() + MoreGroupHeadH;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + MoreProfileH };
    }

    private RectF MoreChipRect(int cell)
    {
        int idx = cell - 1;
        float w = (MoreContentW() - (MoreChipCols - 1) * MoreChipGap) / MoreChipCols;
        int col = idx % MoreChipCols, row = idx / MoreChipCols;
        float y = MoreProfileRect(0).MaxY + MoreChipTopGap + row * (MoreChipH + MoreChipGap);
        float x = MoreLeftX() + col * (w + MoreChipGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + MoreChipH };
    }

    private float SetLowerTop()
        => MoreProfileRect(0).MaxY + MoreChipTopGap
           + (ChipsShown ? 2 * MoreChipH + MoreChipGap : 42f) + MoreGroupGap;

    private float SetColW() => MathF.Max(120f, (MoreContentW() - MoreColumnGap) * 0.5f);
    private float SetRightX() => MoreLeftX() + SetColW() + MoreColumnGap;

    private RectF SetLookHeadRect() => new()
    {
        MinX = MoreLeftX(), MinY = SetLowerTop(),
        MaxX = MoreLeftX() + SetColW(), MaxY = SetLowerTop() + MoreGroupHeadH,
    };

    private RectF SetWriteHeadRect()
    {
        float y = SetLookHeadRect().MaxY + LookRowCount * MoreRowH + MoreWriteGap;
        return new RectF { MinX = MoreLeftX(), MinY = y, MaxX = MoreLeftX() + SetColW(), MaxY = y + MoreGroupHeadH };
    }

    private RectF SetInkHeadRect() => new()
    {
        MinX = SetRightX(), MinY = SetLowerTop(),
        MaxX = SetRightX() + SetColW(), MaxY = SetLowerTop() + MoreGroupHeadH,
    };

    /// <summary>
    /// Rows 在设置页两栏里的位置：左列 外观（3）＋ 书写（2）；右列 墨迹（3）。
    ///
    /// 2026-10-02 加「悬停提示」那一行时，行数**从常量来**（`LookRowCount` /
    /// `WriteRowCount`），不再在 `SetWriteHeadRect` 里写死 `2 * MoreRowH`——
    /// 加行忘改一处，后面的组头就会压到上一行上（仓库在抽屉时代踩过"写死下标"的坑）。
    /// </summary>
    private RectF MoreRowRect(int i) => Rows[i].Kind switch
    {
        Row.DarkTheme => SetColRow(SetColKind.Look, 0),
        Row.AutoHide => SetColRow(SetColKind.Look, 1),
        Row.RailPin => SetColRow(SetColKind.Look, 2),
        Row.Tooltip => SetColRow(SetColKind.Look, 3),
        Row.DwellShape => SetColRow(SetColKind.Write, 0),
        Row.Pressure => SetColRow(SetColKind.Write, 1),
        Row.FineStroke => SetColRow(SetColKind.Write, 2),
        Row.TouchGestures => SetColRow(SetColKind.Write, 3),
        // [删除 2026-10-05] Row.Predict => SetColRow(SetColKind.Write, 2),（墨迹预测行）
        Row.RestoreInk => SetColRow(SetColKind.Ink, 0),
        Row.PptAutoSave => SetColRow(SetColKind.Ink, 1),
        _ => SetColRow(SetColKind.Ink, 2),
    };

    /// <summary>设置页左列的三段：外观 / 书写 / 右列墨迹。</summary>
    private enum SetColKind { Look, Write, Ink }

    private RectF SetColRow(SetColKind col, int idx)
    {
        float x = col == SetColKind.Ink ? SetRightX() : MoreLeftX();
        float headBottom = col switch
        {
            SetColKind.Look => SetLookHeadRect().MaxY,
            SetColKind.Write => SetWriteHeadRect().MaxY,
            _ => SetInkHeadRect().MaxY,
        };
        float y = headBottom + idx * MoreRowH;
        return new RectF { MinX = x, MinY = y, MaxX = x + SetColW(), MaxY = y + MoreRowH };
    }

    private RectF MoreSwitchRect(int i) => new()
    {
        MinX = MoreRowRect(i).MaxX - MoreSwitchW - 4f,
        MinY = (MoreRowRect(i).MinY + MoreRowRect(i).MaxY) * 0.5f - MoreSwitchH * 0.5f,
        MaxX = MoreRowRect(i).MaxX - 4f,
        MaxY = (MoreRowRect(i).MinY + MoreRowRect(i).MaxY) * 0.5f + MoreSwitchH * 0.5f,
    };

    /// <summary>历史清理那一行右侧的值。</summary>
    private string HistoryDaysName(string pref) => pref switch
    {
        "90" => "90 天", "30" => "30 天", "7" => "7 天", _ => "永久",
    };

    // ---- 命中 ----------------------------------------------------------------

    private int MoreHitAt(float x, float y)
    {
        if (!_moreOpen) return MoreHitNone;
        if (!MoreRect().Contains(x, y)) return MoreHitNone;
        if (MoreCloseRect().Contains(x, y)) return MoreHitClose;
        if (_morePage == MorePage.Settings && MoreBackRect().Contains(x, y)) return MoreHitBack;

        if (_morePage == MorePage.Home)
        {
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < HubCount(s); i++)
                    if (HubTileRect(s, i).Contains(x, y)) return MoreHitTile + HubTileCode(s, i);
            for (int i = 0; i < 4; i++)
                if (HubBottomRect(i).Contains(x, y)) return MoreHitBottom + i;
            return MoreHitInside;
        }

        for (int i = 0; i < 3; i++)
            if (MoreProfileRect(i).Contains(x, y)) return MoreHitProfile + i;
        if (ChipsShown)
            for (int cell = 1; cell < Cells.Length; cell++)
                if (MoreChipRect(cell).Contains(x, y)) return MoreHitChip + cell;
        for (int i = 0; i < Rows.Length; i++)
            if (MoreRowRect(i).Contains(x, y)) return MoreHitRow + i;
        return MoreHitInside;
    }

    // ---- 开/关与换页 ---------------------------------------------------------

    /// <summary>打开面板（入口只有主条「…」那一格）。</summary>
    private void OpenMore()
    {
        _railHover = false;
        _rail.To(0f, Tokens.RailMs);

        _moreOpen = true;
        _morePage = MorePage.Home;          // 每次打开都回启动器（肌肉记忆：底栏永远同一处）
        _moreHover = MoreHitNone;
        _morePress = -1;
        _hubHint = "";
        _more.Jump(0f);
        _more.To(1f, Tokens.MoreOpenMs);
        _moreH.Jump(MoreTargetH());
        Invalidate();
    }

    /// <summary>关闭面板。动画期间 QueryBounds 仍报整屏（遮罩要淡出去）。</summary>
    private void CloseMore()
    {
        if (!_moreOpen) return;
        _moreOpen = false;
        _moreHover = MoreHitNone;
        _morePress = -1;
        HideTip();              // 面板里的提示跟着收（触摸长按候选也作废）
        CancelTipHold();
        _more.To(0f, Tokens.MoreCloseMs);
        Invalidate();
    }

    private void UpdateMoreHeight()
    {
        float target = MoreTargetH();
        if (MathF.Abs(_moreH.Value - target) > 0.5f) _moreH.To(target, 167);
    }

    /// <summary>画面板：遮罩（全屏）＋ 卡片 ＋ 按页切内容。**最后画**，压住一切。</summary>
    private void DrawMorePanel(ID2D1DeviceContext ctx)
    {
        float a = Math.Clamp(_more.Value, 0f, 1f);
        if (a <= 0.003f) return;
        UpdateMoreHeight();

        var scrim = _dark ? Tokens.ScrimDark : Tokens.ScrimLight;
        ctx.FillRectangle(
            new Vortice.RawRectF(_screen.MinX, _screen.MinY, _screen.MaxX, _screen.MaxY),
            Brush(ctx, new Color4(scrim.R, scrim.G, scrim.B, QA(scrim.A * a))));

        var r = MoreRect();
        var saved = ctx.Transform;
        float k = 0.97f + 0.03f * a;
        var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
        ctx.Transform = Matrix3x2.CreateTranslation(-c.X, -c.Y)
                      * Matrix3x2.CreateScale(k, k)
                      * Matrix3x2.CreateTranslation(c.X, c.Y)
                      * saved;
        try { DrawMorePanelCore(ctx); }
        finally { ctx.Transform = saved; }
    }

    private void DrawMorePanelCore(ID2D1DeviceContext ctx)
    {
        var r = MoreRect();
        var card = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 20f, 20f);
        ctx.FillRoundedRectangle(card, Brush(ctx, _dark ? Tokens.PanelDark : Tokens.PanelLight));
        ctx.DrawRoundedRectangle(card, Brush(ctx, BorderCol), 1f);

        // 标题（启动器「更多」/ 子页「设置」）＋ 返回 ＋ 关闭
        string title = _morePage == MorePage.Home ? "更多" : "设置";
        _widgets.Text(ctx, title,
                      new RectF { MinX = MoreHeaderRect().MinX + (_morePage == MorePage.Home ? 0f : MoreCloseSize + 8f),
                                  MinY = MoreHeaderRect().MinY, MaxX = MoreHeaderRect().MaxX, MaxY = MoreHeaderRect().MaxY },
                      17f, Brush(ctx, InkCol), center: false);
        if (_morePage == MorePage.Settings)
        {
            _widgets.Text(ctx, "‹", MoreBackRect(), 24f,
                          Brush(ctx, _moreHover == MoreHitBack ? InkCol : MutedCol));
        }
        _widgets.Text(ctx, "✕", MoreCloseRect(), 15f,
                      Brush(ctx, _moreHover == MoreHitClose ? InkCol : MutedCol));

        if (_morePage == MorePage.Home) DrawMoreHome(ctx);
        else DrawMoreSettings(ctx);
    }

    // ---- 启动器绘制 ----------------------------------------------------------

    /// <summary>启动器格子总数（各分组相加；「加格子忘了别处」用这一条兜住）。</summary>
    private static int HubTileTotal
    {
        get
        {
            int n = 0;
            foreach (int c in HubCounts) n += c;
            return n;
        }
    }

    /// <summary>
    /// 启动器格子的表：`Hint` **不再画在格子里**（2026-10-02 第二批：搬进悬停/长按提示），
    /// 但它是提示的**唯一文案来源**——绘制、命中、提示都读这一份。
    /// 格子编号：课堂 0..2（计时 / 点名 / 随机一人）、墨迹 3..6（保存墨迹 / 打开墨迹 /
    /// 墨迹回放 / 保存图片）。
    /// </summary>
    private (string Label, string Hint, bool Enabled, bool Danger) HubTileInfo(int code) => code switch
    {
        0 => ("计时器", "倒计时 / 正计时 / 秒表；卡片可拖动、双击放大", true, false),
        1 => ("点名", "全名单，抽过的不重复；范围可设", true, false),
        2 => ("随机一人", "自动抽一个，1.5 秒自动关", true, false),
        3 => ("保存墨迹", "把整份板书存成 .inkb 文件", _host != null && !_host.State.PptMode && _host.State.StrokeCount > 0, false),
        4 => ("打开墨迹", "打开一份 .inkb，替换当前板书（先备份）", _host != null && !_host.State.PptMode, false),
        5 => ("墨迹回放", "把这一屏的板书重演一遍（只读，不动板书）", _host != null && (_host.State.ReplayActive || _host.State.StrokeCount > 0), false),
        6 => ("保存图片", "把整块板书存成图片（png / jpg，好发微信）", _host != null && !_host.State.PptMode && _host.State.StrokeCount > 0, false),
        _ => ("", "", false, false),
    };

    /// <summary>
    /// 底栏固定四格的唯一一份表（2026-10-02 第二批：小字搬进提示，格子上只留标题）。
    /// 绘制、命中、提示都读它。
    /// </summary>
    private static readonly (string Label, string Hint, bool Danger)[] MoreBottomTiles =
    {
        ("设置", "外观 / 书写 / 墨迹 / 工具条，都在里面", false),
        ("检查更新", "有新版本会提示，也可以直接应用更新", false),
        ("重启软件", "像电脑重启：不恢复本次板书（自动存档还在）", false),
        ("退出", "关掉批注（会先把键位落盘）", true),
    };

    /// <summary>画一个启动器格子：**只有标题**（小字 2026-10-02 搬进了提示）、
    /// 悬停/长按由 FullUi 的提示系统补。</summary>
    private void DrawHubTile(ID2D1DeviceContext ctx, in RectF t, string label, bool enabled, bool danger, int hitCode)
    {
        var rr = new RoundedRectangle(new Vortice.RawRectF(t.MinX, t.MinY, t.MaxX, t.MaxY), 12f, 12f);
        if (_moreHover == hitCode) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
        Color4 ink = !enabled ? new Color4(InkCol.R, InkCol.G, InkCol.B, 0.35f)
                   : danger ? new Color4(0.85f, 0.22f, 0.22f, 1f)
                   : InkCol;
        _widgets.Text(ctx, label, t, 14f, Brush(ctx, ink));
    }

    private void DrawMoreHome(ID2D1DeviceContext ctx)
    {
        string[] heads = { "课堂", "墨迹" };
        for (int s = 0; s < 2; s++)
        {
            DrawMoreGroupHead(ctx, HubHeadRect(s), heads[s]);
            for (int i = 0; i < HubCount(s); i++)
            {
                int code = HubTileCode(s, i);
                var info = HubTileInfo(code);
                DrawHubTile(ctx, HubTileRect(s, i), info.Label, info.Enabled, info.Danger,
                            MoreHitTile + code);
            }
        }

        // 状态行：命令反馈（保存/打开/更新）——产品里不弹窗，结果都落在这儿。
        // 没有要说的就**空着**（2026-10-02 第二批：原来那句固定的"低频功能都收在这儿……"
        // 撤掉了，面板更清爽；信息都在提示里）。
        var st = _host.State;
        string status = !string.IsNullOrEmpty(_hubHint) ? _hubHint
                      : !string.IsNullOrEmpty(st.InkStatus) ? st.InkStatus
                      : st.UpdateStage != UpdateStage.Idle && !string.IsNullOrEmpty(st.UpdateText) ? st.UpdateText
                      : "";
        var sr = HubStatusRect();
        if (status.Length != 0)
            _widgets.Text(ctx, status, sr, 11.5f, Brush(ctx, MutedCol), center: false);

        // 底栏固定四格（表在上面，绘制/命中/提示共用）
        for (int i = 0; i < MoreBottomTiles.Length; i++)
            DrawHubTile(ctx, HubBottomRect(i), MoreBottomTiles[i].Label, true, MoreBottomTiles[i].Danger,
                        MoreHitBottom + i);
    }

    private void DrawMoreGroupHead(ID2D1DeviceContext ctx, RectF r, string label)
        => _widgets.Text(ctx, label, r, 12.5f, Brush(ctx, MutedCol), center: false);

    // ---- 设置子页绘制 --------------------------------------------------------

    private void DrawMoreSettings(ID2D1DeviceContext ctx)
    {
        DrawMoreGroupHead(ctx, new RectF { MinX = MoreLeftX(), MinY = SetToolHeadY(),
                                           MaxX = MoreLeftX() + MoreContentW(), MaxY = SetToolHeadY() + MoreGroupHeadH },
                          "工具条");
        for (int i = 0; i < 3; i++)
        {
            var s = MoreProfileRect(i);
            bool active = ProfileIndex() == i;
            var rr = new RoundedRectangle(new Vortice.RawRectF(s.MinX, s.MinY, s.MaxX, s.MaxY), 8f, 8f);
            if (active) ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
            else if (_moreHover == MoreHitProfile + i) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);
            _widgets.Text(ctx, ProfileIndex() == i ? ProfileName(i) : ProfileName(i), s, 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
        }
        if (ChipsShown)
        {
            for (int cell = 1; cell < Cells.Length; cell++)
            {
                var c = MoreChipRect(cell);
                bool pinned = _pinned[cell];
                var rr = new RoundedRectangle(new Vortice.RawRectF(c.MinX, c.MinY, c.MaxX, c.MaxY), 8f, 8f);
                if (_moreHover == MoreHitChip + cell) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, pinned ? Tokens.Accent : BorderCol), 1f);
                DrawCellIcon(ctx, cell, c, Tokens.Icon,
                             new Color4(InkCol.R, InkCol.G, InkCol.B, pinned ? 1f : 0.35f),
                             pinned, _host.State);
                if (!pinned)
                    _widgets.Text(ctx, "+", new RectF { MinX = c.MaxX - 18f, MinY = c.MinY + 2f, MaxX = c.MaxX - 3f, MaxY = c.MinY + 18f },
                                  12f, Brush(ctx, MutedCol));
            }
        }
        else
        {
            _widgets.Text(ctx, "挑工具：切到「自定义」档，这里就能钉 / 取消钉",
                          new RectF { MinX = MoreLeftX(), MinY = MoreProfileRect(0).MaxY + MoreChipTopGap,
                                      MaxX = MoreLeftX() + MoreContentW(),
                                      MaxY = MoreProfileRect(0).MaxY + MoreChipTopGap + 32f },
                          11.5f, Brush(ctx, MutedCol), center: false);
        }

        DrawMoreGroupHead(ctx, SetLookHeadRect(), "外观");
        DrawMoreGroupHead(ctx, SetWriteHeadRect(), "书写");
        DrawMoreGroupHead(ctx, SetInkHeadRect(), "墨迹");
        for (int i = 0; i < Rows.Length; i++) DrawMoreRow(ctx, i);
    }

    private void DrawMoreRow(ID2D1DeviceContext ctx, int i)
    {
        var r = MoreRowRect(i);
        if (_moreHover == MoreHitRow + i)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f),
                Brush(ctx, HoverCol));

        Color4 ink = Rows[i].Dangerous ? new Color4(0.85f, 0.22f, 0.22f, 1f) : InkCol;
        var label = new RectF
        {
            MinX = r.MinX + 4f, MinY = r.MinY,
            MaxX = r.MaxX - (IsToggleRow(i) ? MoreSwitchW + 10f : Rows[i].Kind == Row.HistoryDays ? 72f : 4f),
            MaxY = r.MaxY,
        };
        // 2026-10-02 第二批：行下那行 11px 小灰字（Hint）**不再画**——搬进悬停/长按提示；
        // 行上只留标签，整行一条线，清爽。Hint 仍是提示的文案来源（Rows 表那一份）。
        _widgets.Text(ctx, Rows[i].Label, label, 13f, Brush(ctx, ink), center: false);
        if (IsToggleRow(i)) DrawSwitch(ctx, MoreSwitchRect(i), IsOn(i));
        else if (Rows[i].Kind == Row.HistoryDays)
            _widgets.Text(ctx, HistoryDaysName(_host.GetPref("historyDays")),
                          new RectF { MinX = r.MaxX - 72f, MinY = r.MinY, MaxX = r.MaxX - 4f, MaxY = r.MaxY },
                          12.5f, Brush(ctx, InkCol));
    }

    // ---- 执行 ----------------------------------------------------------------

    private void ActivateHub(int code)
    {
        switch (code)
        {
            case 0: CloseMore(); _host.Commands.OpenTimerCard(); break;
            case 1: CloseMore(); _host.Commands.OpenRollCard(); break;
            case 2: CloseMore(); _host.Commands.OpenRollOne(); break;
            case 3: CloseMore(); _host.Commands.SaveInkFile(); break;
            case 4: CloseMore(); _host.Commands.OpenInkFile(); break;
            case 5:
                CloseMore();
                if (_host.State.ReplayActive) _host.Commands.StopReplay();
                else _host.Commands.StartReplay();
                break;
            case 6:                                   // 保存图片（2026-10-02）
                CloseMore();
                _host.Commands.SaveBoardImage();
                break;
        }
        Invalidate();
    }

    private void ActivateBottom(int i)
    {
        switch (i)
        {
            case 0: _morePage = MorePage.Settings; _moreHover = MoreHitNone; Invalidate(); break;
            case 1:
                var stage = _host.State.UpdateStage;
                if (stage == UpdateStage.Available) _host.Commands.ApplyUpdate();
                else if (stage != UpdateStage.Downloading && stage != UpdateStage.Ready)
                    _host.Commands.CheckUpdate();
                Invalidate();
                break;
            case 2: _host.Commands.Restart(); break;   // 像电脑重启：不写会话暂存，板书不接回来（引擎语义见 RestartFromUi）
            case 3: _host.Commands.Quit(); break;
        }
    }


    // ---- 贴边隐藏 -----------------------------------------------------------

    /// <summary>整块（主条 ＋ 上带）**未平移**的矩形。</summary>
    private RectF UnionRect() => PanelRect();

    private static RectF Union(in RectF a, in RectF b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX), MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX), MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    /// <summary>
    /// 贴边隐藏的位移：**只有"贴底"才藏**（用户 2026-09-30 定，两轮收紧后的现行口径）。
    ///
    /// 现行规则（2026-09-30 下午）：**面板底边离屏幕底边 ≤ `DockHideDistance`(10)** 才藏；
    /// 其他边（左右、上）一律不藏；连**工作区底（任务栏上沿）也不算**——
    /// 默认位置离屏幕底 52（任务栏 48 + 离任务栏 4），不触发；要藏就得把它拖到屏幕最底下。
    ///
    /// 沿革：最早四条边都判（收起态四边藏、展开态上下藏）→ 上午改成"只认底边、离工作区底 ≤40"
    /// → 下午用户点名"改成到屏幕底边 ≤10，其他不变"。
    ///
    /// 位移量照样按**屏幕**底边算：覆盖层是全屏置顶的，不推出屏幕就藏不掉
    ///（露头必须落在屏幕最底那 8 像素上，这也是用户要的"保持现状"）。
    ///
    /// 收不收由 `_peek` 那个状态机管（见 <see cref="UpdatePeek"/>）；这里只回答
    /// "该不该移动、往哪移"。
    /// </summary>
    private Vector2 Shift()
    {
        // 面板开着（或开合动画中）一律不平移：模态面板必须纹丝不动，
        // 而且它此刻占的就是整块屏幕——跟着贴边位移会立刻被夹回，看着会跳。
        if (_moreOpen || _more.Running) return Vector2.Zero;
        if (!_hideEnabled) return Vector2.Zero;
        float t = 1f - _peek.Value;
        if (t <= 0.001f) return Vector2.Zero;

        var u = UnionRect();
        float h = u.MaxY - u.MinY;
        // 判定：面板底边离**屏幕**底边还有多少（负数 = 已经压过屏幕底，也算贴底）。
        float db = _screen.MaxY - u.MaxY;
        if (db > Tokens.DockHideDistance) return Vector2.Zero;      // 不够近：不藏

        // 位移量按**屏幕**底边（见上面那段论证）。
        float sb = _screen.MaxY - u.MaxY;
        return new Vector2(0, (h - Tokens.DockPeek + sb) * t);
    }

    /// <summary>
    /// 每帧更新"该不该收起来"。写得像个小状态机，因为规则就三条：
    /// 写字中不许动、指针在里面/正按着/「更多」面板开着不许收、刚离开要等一会儿（防误触）。
    /// </summary>
    /// <summary>
    /// 贴边隐藏："真的在等收合"才继续要帧。
    ///
    /// ⚠ **为什么不能直接用 `! _hoverInside && _peek.Value > 0`**（2026-10-07 修）：
    /// 那条在"**面板根本不会收**"的时候也成立，于是**永远要帧**——
    /// 最典型的一种：开了贴边隐藏但指针**还没碰过面板**（`_peekArmed == false`，
    /// 见 `UpdatePeek` 开头，此时面板按设计保持全开、`_peek.Value == 1`）。
    /// 实测后果：**待机 45.5 fps 持续渲染、单核 4~5%，永不停止**——
    /// 关掉这个开关或删掉设置就是 0 fps。笔记本上就是一直耗电、一直发热。
    ///
    /// 现在只有"armed 之后、且当前不处于 keepOpen、且没在写字"才算"在等收合"：
    /// 三种情况都会收敛到 0 —— 收（`_peek.Value → 0`）、或 keepOpen（`_peekPendingCollapse`
    /// 立刻变假）、或没 arm（同样为假）。
    /// </summary>
    private bool _peekPendingCollapse;

    private void UpdatePeek()
    {
        if (!_hideEnabled) { _peek.To(1f, 0); _peekPendingCollapse = false; return; }

        // **启动之后先不藏**（用户 2026-09-17："在贴边隐藏的情况下，刚启动软件的时候不要隐藏"）。
        //
        // 理由很实在：`_leftAtMs` 初值是负无穷，所以第一帧就满足"离开够久了"——
        // 一开机面板立刻收成屏幕底边那条 8 像素的露头（而且露头正好压在任务栏上），
        // 老师根本找不到它。现在改成：**先露着**，等指针碰过面板一次（`_peekArmed`）
        // 才允许"离开就收"——那时候他已经知道东西在哪儿了。
        if (!_peekArmed) { _peek.To(1f, 0); _peekPendingCollapse = false; return; }

        // **写字中：什么都不做**（原样返回），不是"强制展开"。
        //
        // 这条规则的本意一直是"写字的时候不许收"——老师写到屏幕边上，工具条不能自己缩回去。
        // 但它原来和下面 `keepOpen` 用的是同一句 `_peek.To(1f)`，于是"不许收"变成了"必须展开"：
        // **藏好的露头一落笔就被拽出来**（用户 2026-09-18 报的"贴边隐藏以后我一写它就取消贴边了"）。
        // 现在单独拎出来**原样返回**：本来只剩露头就继续露头，本来就开着就继续开着。
        if (_host.State.IsDrawing)
        {
            // 写字这段时间不算"离开"，写完还要等满 700 毫秒才允许收（防误触那条规则照旧）。
            _leftAtMs = _host.NowMs;
            _peekPendingCollapse = false;
            return;
        }

        bool keepOpen = _hoverInside || _press != -1 || _sliderDragging || _moreOpen
                        || _host.NowMs < _peekHoldUntilMs;
        _peekPendingCollapse = !keepOpen && _peek.Value > 0f;   // 只有这一档才需要继续要帧
        if (keepOpen)
        {
            _leftAtMs = _host.NowMs;
            _peek.To(1f, Tokens.SnapMs);
            return;
        }
        if (_host.NowMs - _leftAtMs < 700) return;               // 刚离开：再等等（防误触）
        _peek.To(0f, Tokens.SnapMs);
    }

    /// <summary>
    /// 贴边隐藏的"指针还在面板上吗"判定（**防抖迟滞**，2026-10-05 修"贴边翻页时闪跳"）：
    ///
    ///   · 完全收起时：只认露头那一条（指针扫过"面板本来的位置"不会凭空召唤它）；
    ///   · 展开/收起/动画期间：判定区取 **目标展开后的完整面板 ∪ 当前可见范围**。
    ///
    /// 为什么不能用"当前动画中的矩形"（原来就是）：面板一边长、判定区一边跟着跑，
    /// 指针会被"甩出"判定区 → 收起 → 露头又回到指针下面 → 再展开……一帧一帧地跳。
    /// 这和 Windows 任务栏自动隐藏的迟滞是同一个道理：**展开后的地盘先算进来**，
    /// 隐藏再慢一步（见 Tokens.RailHideDelayMs 与本类的 700ms 防误触）。
    /// </summary>
    private bool HoverInsideForPeek(float x, float y)
    {
        if (_peek.Value > 0.01f)
        {
            var full = UnionRect();
            float pad = Tokens.RailHoverPad;
            if (x >= full.MinX - pad && x <= full.MaxX + pad
                && y >= full.MinY - pad && y <= full.MaxY + pad) return true;
        }
        return QueryBounds().Contains(x, y);
    }

    // ---- 输入 ---------------------------------------------------------------

    public bool PointerDown(in UiPointerEvent e)
    {
        var p = Local(e);
        _pressId = e.PointerId;                // 这一按是谁按的（长按期间挡别的指针）
        _press = -1;
        _dragging = false;
        HideTip();                         // 按下 = 新动作开始，提示先收
        CancelTipHold();                   // 上一次的长按候选也作废（新按下重新计时）
        // **"按下了"不等于"按在面板上"**。引擎会把**每一次**按下都转给界面
        // （界面有权决定吃不吃），所以这里必须自己判一次位置。
        // 无条件置 true 的后果（用户 2026-09-18 报的"贴边隐藏以后我一写它就取消贴边了"）：
        // 在画布上落笔 → 这里置 true、随后返回 false（这一笔归画布）→ 但 true 留了下来，
        // 而**写字期间引擎不转发 PointerMove**（那一笔已经归画布了），没人去把它改回来 →
        // `UpdatePeek` 一直以为"指针还在面板上" → 把藏好的露头重新拽出来。
        _hoverInside = HoverInsideForPeek(e.X, e.Y);
        _peekArmed = true;                 // 碰过了 → 之后允许"离开就收"
        _leftAtMs = _host.NowMs;
        _pressPos = p;
        // 拖动记的是**左上角**（和 RawAnchor 同一套语义：锚"带子的左端"）
        _dragStartAnchor = Anchor();

        // **按下也算"焦点在面板上"**：手写笔和触摸没有悬停那一段，
        // 只在 PointerMove 里更新 _railHover 的话，老师用笔点面板时设置条根本不会张开
        // （鼠标能张开、笔不能——这类"只在一种设备上坏"的 bug 最难查）。
        _railHover = !_host.State.PassThrough && BandVisible() && RailHoverZone().Contains(p.X, p.Y);
        bool touchLike = e.FromTouch || e.FromPen;   // 手指/笔接触（鼠标不参与长按）

        // 贴边隐藏的**触屏节奏**（2026-10-05）：手指/笔把面板按出来后，给一段"够得着"的
        // 停留时间（松手后别 0.7s 就收——手指还得再点一下工具；2500ms 是"看一眼点得中"的量级）。
        // 顺手把**设置条**也带出来：触摸没有悬停，点工具格 = 想看这个工具的设置条。
        if (touchLike && _hoverInside)
        {
            _peekHoldUntilMs = _host.NowMs + 2500;
            _railTouchHoldUntilMs = _host.NowMs + 2500;
        }

        // 「更多」面板：全屏模态，先于一切其它命中（它盖住整块屏幕）。
        // 点面板外 = 关闭，而且这一下**不落墨**（消费掉；这也是自检要钉的一条）。
        if (_moreOpen)
        {
            int hit = MoreHitAt(p.X, p.Y);
            if (hit == MoreHitNone || hit == MoreHitClose) { CloseMore(); return true; }
            if (hit == MoreHitBack) { _morePage = MorePage.Home; Invalidate(); return true; }
            if (hit >= MoreHitProfile && hit < MoreHitProfile + 3)
            { SetProfile((Profile)(hit - MoreHitProfile)); return true; }
            // 区间必须**按表的长度收口**：宫格 30+1..30+12、行 60+0..60+8、启动器格子 140+0..140+5。
            // 写宽了就会串段——历史上宫格写成 +32 吃过行的 60/61（TogglePin 越界抛异常，自检当场红）。
            if (hit >= MoreHitChip && hit <= MoreHitChip + Cells.Length - 1)
            { TogglePin(hit - MoreHitChip); return true; }
            // 行/格子按下时**存原始命中码**，抬起时比对同一个码（同一套手感）。
            // 触摸/笔按住不动 = 长按候选（到点出提示、松手不执行）；鼠标照旧。
            bool pressRow = false;
            if (hit >= MoreHitRow && hit <= MoreHitRow + Rows.Length - 1)
            { _morePress = hit; pressRow = true; }
            if (hit >= MoreHitTile && hit <= MoreHitTile + HubTileTotal - 1)
            { _morePress = hit; pressRow = true; }
            if (hit >= MoreHitBottom && hit <= MoreHitBottom + MoreBottomTiles.Length - 1)
            { _morePress = hit; pressRow = true; }
            if (pressRow)
            {
                if (touchLike && TipContent(1000 + hit).Title != null) ArmTipHold(1000 + hit);
                Invalidate();
                return true;
            }
            return true;                    // 面板里的空白：吃掉，但不关
        }

        if (_expand.Value < 0.5f)
        {
            if (!BallRect().Contains(p.X, p.Y)) return false;
            _press = -2;                    // 球
            return true;
        }

        // ---- 触摸/笔长按（2026-10-02 第二轮）----
        //
        // 鼠标**不走这里**（鼠标按住 = 拖动/划滑条，语义一个字不变）。这一块只服务
        // "按下即生效、必须改成'按住候选、抬起生效'才能长按"的几个：虚实线格、
        // 图形段的 22 格、关闭白板 ✕。别的元素各自有安排：
        //   · 工具格本来就在抬起时执行（见 PointerUp），在下面 idx 分支武装；
        //   · 清空 = 按住 0.8 秒清空、滑条 = 拖动、PPT 页码格 = 长按菜单 → **排除**；
        //   · 色片 / 文字段没有提示（收窄清单）→ 不武装，按下即生效照旧。
        if (touchLike && BandOpen())
        {
            int holdTarget = -1;
            if (HitDashToggle(p.X, p.Y)) holdTarget = 500;
            else if (_bandCell == ShapeCell)
            {
                int sg = HitSegment(p.X, p.Y);
                if (sg >= 0) holdTarget = 200 + sg;
            }
            else if (CurAction == BandAction.CloseBoard && ActionRect().Contains(p.X, p.Y))
                holdTarget = 400;
            if (holdTarget >= 0 && TipContent(holdTarget).Title != null)
            {
                ArmTipHold(holdTarget);
                return true;      // 抬起才执行；中途长按出提示则这次不执行
            }
        }

        // **滑条要排在工具格前面**：它在面板最下沿，和工具格的矩形是重叠的。
        // 排在后面的话，按最下沿那一条会被当成"点了某个工具"（假面板里 groove 也是先判的）。
        // 动作按钮（清空/全选）排在最前面：它贴在上带最外沿，和谁都挨着。
        if (BandOpen() && CurAction != BandAction.None
            && ActionRect().Contains(p.X, p.Y))
        {
            if (CurAction == BandAction.Clear)
            {
                // 清空：**按住才算数**（0.8 秒），松手即取消。进度由 UpdateBandAction 每帧推进。
                _actionHoldFrom = _host.NowMs;
                _press = 2000;
            }
            else if (CurAction == BandAction.CloseBoard)
            {
                // 关闭白板：**点一下就执行**（和全选一样）——关板是可逆的（再点白板格就开回来），
                // 不像清空那样代价大，所以不必按住。
                // ⚠ 板已经关着时这一下什么都不做（按钮也是压暗的，见 DrawBandAction）。
                if (_host.State.Board)
                {
                    _host.Commands.SetBoard(false);
                    _actionFlashUntil = _host.NowMs + 260;
                }
            }
            else if (CurAction == BandAction.PasteImage)
            {
                // 粘贴图片：点一下就执行——就是批注内的 Ctrl+V。
                // 它以前是截图那格的第三段（分段=模式，它=动作，混着永远不亮）；
                // 8.3.0 拆到这里之后，触摸屏/手写板没键盘的老师照样够得着。
                _host.Commands.Paste();
                _actionFlashUntil = _host.NowMs + 260;
            }
            else
            {
                _host.Commands.SelectAll();          // 全选：点一下就执行
                _actionFlashUntil = _host.NowMs + 260;
            }
            Invalidate();
            return true;
        }

        if (BandOpen() && BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(p.X, p.Y))
        {
            _sliderDragging = true;
            DragSlider(p.X);
            return true;
        }

        int idx = HitCell(p.X, p.Y);
        if (idx >= 0)
        {
            _press = idx;
            // 工具格：抬起执行；触摸/笔按住不动 = 长按候选（鼠标不武装）
            if (touchLike && TipContent(idx).Title != null) ArmTipHold(idx);
            return true;
        }

        // 上带：色片 / 分段（滑条已经在上面判过了）
        if (BandOpen())
        {
            // 虚实线那一格：**按下即生效**（和色片同一个手感——点一下就该看见结果，
            // 不用等抬手；抬手那一下还要给"拖动面板"让路）。
            if (HitDashToggle(p.X, p.Y)) { CycleDash(); return true; }
            int sw = HitSwatch(p.X, p.Y);
            if (sw >= 0) { ActivateSwatch(sw); return true; }
            int sg = HitSegment(p.X, p.Y);
            if (sg >= 0) { ActivateSegment(sg); return true; }
        }

        return false;                       // 带子/上带里的空白（两端内边距）：不吃，引擎按"地盘"吞掉
    }

    /// <summary>屏幕坐标 → "没有平移过的"布局坐标（贴边隐藏会整体平移一次）。</summary>
    private Vector2 Local(in UiPointerEvent e)
    {
        var s = Shift();
        return new Vector2(e.X - s.X, e.Y - s.Y);
    }

    public bool PointerMove(in UiPointerEvent e)
    {
        // 触摸长按计时 / 提示停留期间：**别的指针**（停着的鼠标、笔悬停）的移动不许搅局。
        // 引擎会把窗口收到的所有移动都转给界面（UiCapturing 分支），少了这一道，
        // 鼠标随手动一下就被当成"手指滑走了"（2026-10-02 自检实测 569px 假移动）。
        if ((_tipHoldStart > double.NegativeInfinity || _tipHoldFired)
            && e.PointerId != _pressId)
            return true;
        _hoverInside = HoverInsideForPeek(e.X, e.Y);
        if (_hoverInside) _peekArmed = true;   // 指针进过面板 → 之后允许"离开就收"
        _leftAtMs = _host.NowMs;
        var p = Local(e);
        _railHover = !_host.State.PassThrough && BandVisible() && RailHoverZone().Contains(p.X, p.Y);

        // 「更多」面板开着时，指针只喂给面板：更新悬停、别再碰主条的悬停/拖动状态
        if (_moreOpen)
        {
            int hit = MoreHitAt(p.X, p.Y);
            if (hit != _moreHover) { _moreHover = hit; Invalidate(); }
            // 面板里的提示：鼠标悬停出、手指/笔长按也出（2026-10-02 第二批）
            SetTipTarget(MoreTipTargetAt(hit));
            // 长按候选：在面板上滑走（超过拖动阈值）就取消
            if (_tipHoldStart > double.NegativeInfinity
                && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
                CancelTipHold();
            return true;
        }

        if (_sliderDragging)
        {
            HideTip();                 // 拖滑条时"粗细预览"那张卡在画，别叠提示
            DragSlider(p.X);
            return true;
        }

        if (_press != -1)
        {
            // 触摸/笔长按已经弹过提示：这一次按下作废——不拖动、也不执行（等松手）。
            if (_tipHoldFired) return true;
            // **动作按钮不参与拖动**。
            //
            // 用户实测报的 bug："按住清空的时候，手一抖就把整个面板拖走了"——
            // 面板一走，按钮就不在指针下面了，看着就是"清空没反应"。
            // 只有点在**主条上**（球或者工具格）才算"抓住面板"。
            // （「更多」面板的行有它自己的 `_morePress`，走不到这里——它是全屏模态。）
            bool draggable = _press != 2000 && _press < 1000;
            if (draggable && !_dragging && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
            {
                _dragging = true;
                CancelTipHold();        // 移动就是拖动：长按候选作废（主流消歧：移动=拖、不动=长按）
            }
            if (_dragging)
            {
                _anchor = _dragStartAnchor + (p - _pressPos);
                Invalidate();               // 位置一变就要重画；引擎那边每帧都会加进脏区
            }

            // 按住清空的时候指针滑出按钮 = 算了（各家按钮都是这个约定）。
            // 判定区给 8 像素余量，免得手抖一两像素就取消。
            if (_press == 2000)
            {
                var hit = ActionRect();
                if (p.X < hit.MinX - 8f || p.X > hit.MaxX + 8f
                    || p.Y < hit.MinY - 8f || p.Y > hit.MaxY + 8f)
                {
                    _actionHoldFrom = double.NegativeInfinity;
                    _press = -1;
                    Invalidate();
                }
            }
            return true;
        }

        // 触摸长按已经弹过提示（段/线型/关板那条路）：按住期间移动什么都不做。
        if (_tipHoldFired) return true;
        // 触摸长按还在计时，但手指已经滑走：这次按下不执行、也不弹提示（重新按）。
        if (_tipHoldStart > double.NegativeInfinity
            && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
        {
            CancelTipHold();
            Invalidate();
            return true;
        }

        int hover = _expand.Value < 0.5f
            ? (BallRect().Contains(p.X, p.Y) ? -2 : -1)
            : HoverAt(p.X, p.Y);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        SetTipTarget(hover);
        return hover != -1;
    }

    public void PointerLeave()
    {
        // 触摸长按：手指还按着的时候，"离开"多半是小窗/主窗切换的假动作——
        // 按住计时 / 已经弹出的提示**不在这里收**（松手或停留到期自会收）。
        if (_tipHoldStart == double.NegativeInfinity && !_tipHoldFired)
        {
            HideTip();
            CancelTipHold();
        }
        // 面板开着时指针只会"离开整块屏幕"（占用 = 全屏）：把面板的悬停/按下也清掉
        if (_moreOpen)
        {
            _moreHover = MoreHitNone;
            _morePress = -1;
            Invalidate();
        }
        if (!_hoverInside) return;
        _hoverInside = false;
        _railHover = false;              // 指针离开面板 = 焦点不在了，设置条该收（走 ExitDelay）
        _leftAtMs = _host?.NowMs ?? 0;
        Invalidate();
    }

    /// <summary>
    /// 这一刻指针落在什么上面。上带里的东西用偏移的编号：
    /// 100＋色片下标、200＋分段下标、300 = 滑条。**画与命中同源**，
    /// 不这么编号的话"悬停态"和"命中"会各写一份判断，然后慢慢不一致。
    /// </summary>
    private int HoverAt(float x, float y)
    {
        int idx = HitCell(x, y);
        if (idx >= 0) return idx;
        if (!BandOpen()) return -1;
        // 动作按钮（清空/全选）：编号 400，和色片 100、分段 200、滑条 300 排成一套
        if (CurAction != BandAction.None && RailOpen && ActionRect().Contains(x, y)) return 400;
        if (BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(x, y)) return 300;
        if (HitDashToggle(x, y)) return 500;                       // 虚实线那一格
        int sw = HitSwatch(x, y);
        if (sw >= 0) return 100 + sw;
        int sg = HitSegment(x, y);
        if (sg >= 0) return 200 + sg;
        return -1;
    }

    public bool PointerUp(in UiPointerEvent e)
    {
        // 触摸长按/提示停留期间，只有**按下的那根手指**的抬起才算数（别的指针抬起忽略）。
        if ((_tipHoldStart > double.NegativeInfinity || _tipHoldFired)
            && e.PointerId != _pressId)
            return true;
        // 「更多」面板：按下和抬起落在**同一行**才算一次执行（和抽屉同一套手感）
        if (_moreOpen)
        {
            // 触摸/笔长按已经弹过提示：这一次松手**不执行**（提示停一会儿自己收）——
            // 和主条上那条规则完全一致（Windows/Material 惯例：长按是"看"不是"点"）。
            if (_tipHoldFired)
            {
                CancelTipHold();
                _tipLingerUntil = _host.NowMs + Tokens.TipLingerMs;
                _morePress = -1;
                Invalidate();
                return true;
            }
            int hit = MoreHitAt(e.X, e.Y);
            int pressed = _morePress;
            _morePress = -1;
            if (pressed >= MoreHitRow && pressed <= MoreHitRow + Rows.Length - 1 && hit == pressed)
                ActivateRow(pressed - MoreHitRow);
            else if (pressed >= MoreHitTile && pressed <= MoreHitTile + HubTileTotal - 1 && hit == pressed)
                ActivateHub(pressed - MoreHitTile);
            else if (pressed >= MoreHitBottom && pressed <= MoreHitBottom + 3 && hit == pressed)
                ActivateBottom(pressed - MoreHitBottom);
            Invalidate();
            return true;
        }
        if (_morePress >= 0) { _morePress = -1; return true; }   // 面板刚关掉的那一下：已消化

        if (_sliderDragging)
        {
            _sliderDragging = false;
            return true;
        }

        // ---- 触摸/笔长按的收尾（2026-10-02 第二轮）----
        if (_tipHoldFired)
        {
            // 长按已经弹过提示：这一次松手**不执行任何功能**（Windows/Material 惯例：
            // 长按是"看"不是"点"）；提示继续停留 TipLingerMs 后自动收。
            CancelTipHold();
            _tipLingerUntil = _host.NowMs + Tokens.TipLingerMs;
            _press = -1;
            _dragging = false;
            _sliderDragging = false;
            Invalidate();
            return true;
        }
        if (_press == -1 && _tipHoldStart > double.NegativeInfinity)
        {
            // "按下即生效"的那几格，触摸/笔改成**抬起才生效**（中途长按出提示则不生效）：
            // 虚实线格、图形段、关闭白板 ✕。
            int target = _tipHoldTarget;
            CancelTipHold();
            if (target == 500) CycleDash();
            else if (target == 400)
            {
                if (_host.State.Board)
                {
                    _host.Commands.SetBoard(false);
                    _actionFlashUntil = _host.NowMs + 260;
                }
            }
            else if (target >= 200 && target < 200 + BandSegmentCount)
                ActivateSegment(target - 200);
            Invalidate();
            return true;
        }
        CancelTipHold();        // 工具格那条路：候选作废，原样走抬起执行

        int idx = _press;
        bool dragged = _dragging;
        _press = -1;
        _dragging = false;
        if (idx == -1) return false;

        if (dragged)
        {
            // **松手不吸附**（用户 2026-09-30："我（说的）吸附是比如拖到任务栏下、它自动靠底边，
            // 这种的不用了"）：拖到哪儿就停在哪儿。
            // 拖动过程本身已经把它夹在屏幕内（每次读 `Anchor()` 都过 `Clamp`），
            // 所以"拖出去找不回来"这条底线仍然在；只是不再自动贴边。
            // 贴边**隐藏**是另一回事：只认底边、离底边够近才触发（见 Shift）。
            return true;
        }

        // 按住清空：松手即取消（够 0.8 秒的那一次已经在 UpdateBandAction 里执行过了）
        if (idx == 2000)
        {
            _actionHoldFrom = double.NegativeInfinity;
            Invalidate();
            return true;
        }
        if (idx == -2) { Toggle(); return true; }        // 点球：展开
        if (idx == 0) { Toggle(); return true; }         // 点带子最左那格：收起
        Activate(idx);
        return true;
    }

    private int HitCell(float x, float y)
    {
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++)
            if (CellRect(k).Contains(x, y)) return vis[k];      // 返还完整档的下标
        return -1;
    }

    private void Toggle()
    {
        bool collapse = _expand.Value > 0.5f;
        // 系统关掉动画时直接跳终态（教室里老机器上很常见）
        _expand.To(collapse ? 0f : 1f, collapse ? Tokens.CollapseMs : Tokens.ExpandMs);

        // **收起时必须把临时状态清干净**：面板、悬停、按下的格、在拖的滑条。
        // 不清的话，缩回一个球之后那些状态还挂在那儿，下次展开时会自己冒出来
        //（假面板当年就是栽在这条上："点更多→点收起→再展开，抽屉自己冒出来"）。
        if (collapse)
        {
            // 面板理论上打不开（它要主条展开才有入口），但状态机收口时一并清掉，
            // 免得以后哪天加了别的入口，缩球时面板还挂在那儿。
            _moreOpen = false;
            _more.Jump(0f);
            _moreHover = MoreHitNone;
            _morePress = -1;
            _hover = -1;
            _press = -1;
            _dragging = false;
            _sliderDragging = false;
            CancelTipHold();
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
        }
        Invalidate();
    }

    /// <summary>
    /// 点某一格。**只走命令通道**：界面不许直接改引擎状态（撤销栈、空间索引、
    /// 脏区都靠引擎维护，越权就会破坏这些不变量）。
    /// </summary>
    private void Activate(int idx)
    {
        var cmd = _host.Commands;
        var st = _host.State;

        // **点之前**上带停在哪一格。笔 / 荧光笔那一格要用它判"这一下是切色、还是只是把设置条拿过来"
        //（`_bandCell` 在下面 `HasBand` 那一块里会被改成 idx，改完就问不出"原来在哪"了）。
        int prevBand = _bandCell;
        // **点之前是不是在穿透**：穿透下点笔 / 荧光笔格 = "我要回来写字"，这一次**不许顺手换色**
        //（和键盘 Ctrl+P 同一条：穿透先退出、第二步才谈换色。2026-10-05 用户报的 bug）。
        bool wasPassThrough = st.PassThrough;

        // **点了别的格子 = 选择格那次"双击"序列到此为止**。
        // 不这么做的话，"选择格 →（200ms）笔格 →（200ms）选择格"会被算成对选择格的双击，
        // 于是老师只是在两个格子之间来回看一眼，就被全选了。
        if (idx != 7) _lastSelCellClickMs = double.NegativeInfinity;

        // 点工具格时，上带跟着换成这个工具的设置（"上带＝这个按钮的设置条"），
        // 并且**钉住展开**——不钉的话指针一移开就收了，选项来不及选。
        //
        // 再点一次**同一个**工具 = 收起它自己的设置条（假面板的用法，也是各家通例）。
        if (HasBand(idx))
        {
            // 设置条显示**这一格的设置**：点白板就看板色＋翻页，点橡皮就看整笔擦/面积擦。
            //
            // 这里以前还有一套"点一次钉住、再点同一个工具收起"的状态（照假面板抄的）。
            // 2026-09-17 用户定了新的语义：**张不张开只看焦点在不在面板上**
            // （见 RailHoverZone）。于是"再点一次收起"这一支必须删掉——
            // 指针还停在面板上，收下去会立刻又张开，是两个规则打架。
            // 指针一离开面板它自己就收（走 220 毫秒的退出延迟），不用老师再点一次。
            _bandCell = idx;
            _railEnterAtMs = double.NegativeInfinity;   // 已经在面板上了，不用再等开门那 120 毫秒
        }

        switch (idx)
        {
            case 1: cmd.SetPassThrough(!st.PassThrough); break;
            // 白板那一格：**点三下是一个来回**（用户 2026-09-27 第三次定的）。
            //
            //   ① 板关着               → 开板（顺手把设置条拿过来）
            //   ② 板开着、色带在别处     → **只把色带拿过来，板一动不动**
            //   ③ 板开着、色带也在这一格 → **关板**
            //   ④ 再点一下 → 又回到 ①（开板），往后就这三下循环
            //
            // 为什么要改（用户原话："那个白板我试了几遍，感觉单独弄一个开关还是不习惯"）：
            // 上一版把"关板"挪去了设置条最右端的 ✕，格子上怎么点都关不掉——用起来
            // 反而别扭。现在这一个格子自己就是那个开关，✕ 留着当"一眼看得见的关板键"。
            //
            // 同时**去掉了"再点一下换下一个板色"**（用户："单击切换上面的白板颜色，
            // 我感觉不需要了"）：换板色色带上就摆着三格（白/绿/黑），一点就到；
            // 而这一个格子到底是"开"还是"关"才是大家点它的本意。
            //
            // ② 那条判据必须带 `prevBand == 2`：老师从图形面板点回白板那一格，
            // 意思是"把板拿回来用"，那时候不该顺手把它关掉（同笔 / 荧光笔 / 图形那几格）。
            case 2:
                if (!st.Board) cmd.SetBoard(true);             // ① 开板
                else if (prevBand == 2) cmd.SetBoard(false);   // ③ 关板
                break;                                         // ② 只把色带拿过来，板不动
            // 笔 / 荧光笔那两格：**已经是它、而且色带本来就在这一格 → 换下一个颜色**
            //（用户 2026-09-27 定的"已经是它了，点击切换颜色"）。
            //
            // ⚠ 判据必须带上前一提"色带本来就在这一格"：老师从图形面板点回笔那一格，
            //   意思是"把笔拿回来"，那时候不能悄悄把颜色也换了；而老师一直在用笔时
            //   色带本来就停在笔那一格，连点就是连着换色——正好是想要的手感。
            //
            // ⚠ **切色那条路上也要走一次 `SetTool`**：引擎的 `SwitchTool` 里还兼着
            //   "顺手把穿透关掉"（穿透和工具互斥，见 Engine.SwitchTool）。漏了它就会出现
            //   "点笔格换了色、但还在穿透"——自检里"点工具格＝顺手关掉穿透"那两条当场就红。
            //   `SetTool` 是幂等的（工具没变时只做清理），重复调没有副作用。
            case 3:
                cmd.SetTool(Tool.Pen);
                if (prevBand == 3 && !wasPassThrough) CycleColor();
                break;
            case 4:
                cmd.SetTool(Tool.Highlighter);
                if (prevBand == 4 && !wasPassThrough) CycleColor();
                break;
            case 5: cmd.SetTool(Tool.Laser); break;
            case 6:
                // [2026-10-05 用户定] 橡皮子类型不再"点一下换一次"：点这一格 = 进橡皮
                //（上一次用整笔就整笔、用面积就面积）；整笔/面积去上带那两段里选。
                cmd.SetEraserPreferred();
                break;
            case 7:
                // 选择那一格：**不再"再点一下换档"**（矩形/套索去上带那两段里选，2026-10-05）。
                //
                // **双击 = 全选**（500ms 内两击，照 InkClass 那个经典交互）。
                // 没有"再点换档"之后，两击的净效果就是"选中工具 + 全选"——比原来更直白。
                if (_host.NowMs - _lastSelCellClickMs < SelectDoubleClickMs)
                {
                    _lastSelCellClickMs = double.NegativeInfinity;   // 这一次序列到此为止
                    cmd.SetTool(Tool.Marquee);
                    cmd.SetSelectMode(_selModeBeforeDoubleClick);
                    cmd.SelectAll();
                    // 闪一下：闪的是色带上那个「全选」按钮（和点它自己一样，
                    // 见调研-界面-上下文设置条.md 里"点一下 → 全部进选中框 → 按钮闪一下"）。
                    _actionFlashUntil = _host.NowMs + 260;
                    break;
                }
                _lastSelCellClickMs = _host.NowMs;
                _selModeBeforeDoubleClick = st.SelectMode;           // 记下"双击前"的档
                cmd.SetTool(Tool.Marquee);
                break;
            case 8:
                // 七种图形之后**不能再"两档对切"**了（以前是直线 ↔ 矩形）。
                //
                // 现在的规则：已经是图形工具就**不动工具**——用户只是想看上带（指针就在
                // 面板上，下一手点哪一段都行）；偷偷换成矩形是最气人的，画到一半的
                // 三角形会被换掉。不是图形工具才给一个默认种类（矩形，沿用老行为）。
                if (!HasShapeEntry(st.Tool)) cmd.SetTool(Tool.Rectangle);
                break;
            case 9: cmd.SetTool(Tool.Capture); break;
            case 10: cmd.Undo(); break;
            case 11: cmd.Redo(); break;
            case 12:
                // 「更多」：打开屏幕中央的面板（2026-10-01 起；抽屉从产品路径退场）
                OpenMore();
                break;
        }
        Invalidate();
    }

    /// <summary>哪些格子有上带。没有的（后撤/重做/更多/截屏）点了不长出一条空带子。</summary>
    private static bool HasBand(int cell) => cell is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9;

    /// <summary>当前工具对应的格子——键盘换工具时用它把上带掰回来。</summary>
    private static int CellForTool(Tool t)
    {
        // 七种图形共用第 8 格（哪一种是哪一段由上带里的高亮标出来）。
        // 判据走 HasShapeEntry 而不是再列一遍图形名字：加一种图形只改 ShapeRows。
        if (HasShapeEntry(t)) return 8;
        return t switch
        {
            Tool.Highlighter => 4,
            Tool.Laser => 5,
            Tool.Eraser or Tool.PixelEraser => 6,
            Tool.Marquee => 7,
            Tool.Capture => 9,
            _ => 3,
        };
    }

    public void OnStateChanged(in UiState state)
    {
        // 回放一开始：把「更多」面板收掉。它是**全屏模态**，开着会挡住控制条和画布；
        // 而引擎那边"点界面 = 先退出回放"——不收掉的话，老师想关面板那一刻回放就没了。
        if (state.ReplayActive && _moreOpen) CloseMore();

        // **工具变了就跟着换上带**（键盘热键是老师更常用的那条路）。
        // 判据是"工具真的换了"，不是"当前带子对不对"：点白板之后带子显示的是板色，
        // 那时工具没变、带子也不该被掰走；而一旦换成别的工具，带子必须跟上，
        // 不然会出现"手里是橡皮、上带还是色板"。
        if (state.Tool != _lastTool)
        {
            _lastTool = state.Tool;
            int cell = CellForTool(state.Tool);
            if (HasBand(cell)) _bandCell = cell;
        }

        // ---- 穿透开的那一刻：上带收回那条 6 像素色线（2026-10-02 用户口径）----
        //
        // 用户原话（第二次澄清）："点击穿透以后，色带是横起来的（收起来）。不是说我点了个
        // 穿透，色带就完全没有了。我说的色带消失，就是把它折叠起来，而不是像其他一样，
        // 点过来以后还是展开的。"——所以：**面板高度不变**（贴边隐藏露出来的还是那条线），
        // 只是把设置条收回去、而且穿透期间不许再张开（没有设置可放）。
        // **不动 `_bandCell`**：退出后带子还是回到"刚才那个工具"那一格——老师回到刚才的活。
        //
        // "指针还停在面板上"也必须收：老师刚点的就是穿透格，不清悬停意图的话，120 毫秒后
        // 旧设置条又自己弹开（自检里钉着它）。
        if (state.PassThrough != _lastPass)
        {
            _lastPass = state.PassThrough;
            if (state.PassThrough)
            {
                _railHover = false;
                _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
                _rail.To(0f, Tokens.RailMs);     // 设置条 → 那条 6 像素色线
                Invalidate();
            }
        }

        // ---- 进放映的那一刻：**展开 + 回到默认位置**（用户 2026-09-27 定）----
        //
        // 用户原话："PPT 开始播放以后，把它打开放到居中靠底部。"
        // 他要的是"一放片，笔就自己出来"——老师上课的第一件事往往是拿起笔，
        // 不该先去找那颗球、或者去把上次拖到角落的面板拽回来。
        //
        // ⚠ **只在边沿做一次**（`!state.PptMode` 之外的那一下）：这一段要是写成
        // "放映中就归位"，那么他放映中把面板拖到一边、翻一页（状态变化 → 又走这里）
        // 就会被**拽回底部居中**——那是比"不归位"更烦人的行为。
        // ⚠ **退出放映不还原**（用户 2026-09-27 选的）：面板保持展开、位置不动。
        // 理由是他刚用完笔，回桌面还要接着写板书，再收起来等于多一步。
        if (state.PptMode && !_lastPpt)
        {
            _anchor = null;                       // 回默认位置（工作区底边居中）
            _peek.Jump(1f);                       // 贴边隐藏开着的话，也先完整露出来
            _leftAtMs = _host.NowMs;              // 这次"露面"重新计离开时间
            if (_expand.Value < 0.5f) _expand.To(1f, Tokens.ExpandMs);
            else _expand.Jump(1f);
        }
        _lastPpt = state.PptMode;

        Invalidate();
    }

    // ---- 绘制 ---------------------------------------------------------------

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_host == null) return;
        UpdatePeek();                    // 每帧问一次"该不该收起来"（贴边隐藏）
        UpdateRail();                    // 色线该不该长成设置条
        UpdateBandAction();              // "按住清空"够 0.8 秒没有（每帧推进）
        UpdateTip();                     // 悬停提示"到点没有"（每帧推进）
        RenderShifted(ctx);
    }

    private void RenderShifted(ID2D1DeviceContext ctx)
    {
        var saved = ctx.Transform;
        var shift = Shift();
        if (shift != Vector2.Zero)
            ctx.Transform = Matrix3x2.CreateTranslation(shift) * saved;
        try
        {
            RenderCore(ctx);
            DrawTip(ctx);               // 悬停提示画在最上面（主条/带子/「更多」面板之上）
        }
        finally
        {
            ctx.Transform = saved;
        }
    }

    private void RenderCore(ID2D1DeviceContext ctx)
    {
        float e = _expand.Value;
        var bar = BarRect();
        var panel = UnionRect();

        // **整块面板一张卡片**（主条 ＋ 色带共用一个圆角）：圆角随展开从 24 收到 18，
        // 和假面板一样（它写的是 L.Radius = 24 - 6 * e）。
        float radius = Tokens.PillRadius(Tokens.BarHeight) - 6f * e;
        // **收起态贴边隐藏时，球本身那张卡片不画**：画了的话它的圆和投影会一起露出来，
        // 把手下面还压着一团（露出 8 像素时看得见那一片弧——正是要收拾的那个观感）。
        // 这一条必须在这里判：下面那个 `e < 0.5` 分支里再 return 已经晚了。
        if (!(e < 0.5f && PeekTabShown)) DrawCard(ctx, panel, radius);

        var st = _host.State;

        // 贴边隐藏沉下去之后（露头已经只剩十来个像素），**不再露球的那一小片弧**，
        // 改成一条直的把手（见 Tokens.PeekTab：那一片弧看着像残影）。
        // 换的时机见 PeekTabShown：再早换，球里的色环和笔图标会提前消失。
        if (e < 0.5f && PeekTabShown)
        {
            DrawPeekTab(ctx);
            return;
        }

        // ---- 内容是**两段式**的：形状先长，内容再交叉换 ----
        //
        // 原来是在 e = 0.5 上硬切（球在 e<0.5 画、13 格在 e≥0.5 画）。出图当场看出两个毛病
        // （`--panelshow <图> --expand 0.15` / `0.5`）：
        //   ① 色环和笔图标是按**面板的一半宽**算的，面板一长它们就被撑成一整个大图标，
        //      而且还跟着面板中心往右跑；
        //   ② 硬切那一下 13 格"啪"地出现，而且**没被面板裁住**——右边几个工具跑到面板外面。
        // 现在：形状照旧按 e 长（200ms、ease-out），
        //   球的内容：e 0.05→0.45 淡出；  13 格：e 0.45→0.80 淡入。
        // 中间那段两者同时在，位置也是同一个（球那一格），读起来就是"球摊开成格子"。
        float ballA = 1f - Smooth01((e - 0.05f) / 0.40f);
        float cellsA = Smooth01((e - 0.45f) / 0.35f);

        if (ballA > 0.004f) DrawBallContent(ctx, st, e, ballA);

        // 色带那条**凹槽**：把色带那一块裁出来、填一层淡淡的暗色（照假面板：
        // 裁进面板的圆角形状，顶部两个角自然跟着圆）。
        var band = BandRect();
        if (cellsA > 0.004f)
        {
            // 形变期间**把内容裁进面板**：面板还没长到全宽时，右边那几格会数到面板外面去。
            // 平时（e≈1）一次多余的裁剪都不做。
            bool clip = e < 0.999f;
            if (clip)
                ctx.PushAxisAlignedClip(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY),
                                        AntialiasMode.Aliased);
            // 淡入整层做（不透明度图层），而不是把 alpha 一路传进每个绘制函数：
            // 要淡的东西有几十处（13 格的图标、组分隔线、色片、滑条…），传 alpha 得改十几处签名。
            bool layered = cellsA < 0.996f && BeginFade(ctx, cellsA);
            DrawExpandedContent(ctx, st, panel, band, radius);
            if (layered) EndFade(ctx);
            if (clip) ctx.PopAxisAlignedClip();
        }

        // 面板上/下沿那道 1 像素的"收边"。**两个主题走相反的方向**（理由见 Tokens）：
        //   深色 → 顶沿一道极淡的白高光（读"接光"）；
        //   浅色 → 底沿一道极淡的暗线（读"卷边"）。白高光画在白面板上等于没画。
        //
        // 画法是"裁出一条 1.5 像素高的横带、带子里描一遍圆角矩形"：这样线到两端
        // 自然顺着圆角收进去。老代码是在带子里填一条**直**矩形，两端会戳出圆角外
        // 十几像素（浅色那条是白高光，落在亮背景上就是一条看得见的飞边）。
        if (!PerfSkipChrome && band.MaxY - band.MinY > 20f)
        {
            bool top = _dark;
            float y = top ? panel.MinY + 1f : panel.MaxY - 1f;
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(panel.MinX, y - 0.75f, panel.MaxX, y + 0.75f),
                                    AntialiasMode.Aliased);
            var edge = new Vortice.RawRectF(panel.MinX + 0.5f, panel.MinY + 0.5f,
                                            panel.MaxX - 0.5f, panel.MaxY - 0.5f);
            ctx.DrawRoundedRectangle(new RoundedRectangle(edge, radius - 0.5f, radius - 0.5f),
                                     Brush(ctx, top ? Tokens.TopSheenDark : Tokens.EdgeLightBottom), 1f);
            ctx.PopAxisAlignedClip();

            // 色带那条凹槽**朝主条的那一侧**再压一道 1 像素暗线（只有浅色）。
            // 凹槽的填充已经降到黑 6%，光靠它自己只是一条淡灰带；这一道线才是
            // "这道槽是刻进面板的"的凭据（Windows 的口径：浅色用描边定形）。
            // 位置跟着带子在主条的哪一侧走：面板贴底时带子在上面，线就落在带子下沿。
            //
            // 这条线**可以是一条直横线**（不像上面那条收边要顺着圆角走）：它在面板中间，
            // 而上面那个 `> 20` 的门槛保证了它离面板那条边至少 20 像素，
            // 同一刻的圆角也只有 20 出头——两端伸出去的不到半个像素，量不出来。
            if (!_dark)
            {
                float wy = BandAbove() ? band.MaxY - 0.5f : band.MinY + 0.5f;
                ctx.FillRectangle(new Vortice.RawRectF(panel.MinX + 1f, wy - 0.5f,
                                                       panel.MaxX - 1f, wy + 0.5f),
                                  Brush(ctx, Tokens.WellEdgeLight));
            }
        }

        // 这里原来还画一条"踢脚线"（`DrawGroove`：面板下沿 6 像素处、笔色 25% 的 2 像素横线）。
        // 2026-09-18 删了：它当年的任务是"让面板下沿永远有东西，看着是块完整的板子"，
        // 而现在下沿有**投影 ＋ 描边 ＋ 底沿内阴影**三条，这句话不用它承担了；
        // 留下的坏处更实在——颜色跟着笔走（红笔时它是整块白面板上最响的东西，像根进度条），
        // 而它想表达的"现在拿的是哪支笔"，球里那道色环已经说了、还更准。
        // 粗细预览**最后画**：它可能伸到面板外面，压在上面的东西得过它一层
        DrawSizePreview(ctx, st);
        // 「更多」面板**比一切都后**：遮罩要压住主条和粗细预览，面板再压住遮罩。
        // 它打开时 QueryBounds 已经是整块屏幕，不会被引擎裁掉。
        if (_moreOpen || _more.Running) DrawMorePanel(ctx);
    }

    // ---- 颜色（深色主题只是一整套换过来，形状一个都不动）------------------

    /// <summary>
    /// 展开态那一片内容：凹槽 ＋ 工具格 ＋ 设置条。抽出来是为了能在形变期间**整层淡入**
    /// （用一个不透明度图层包一次就够，不必把 alpha 传进十几个绘制函数）。
    /// </summary>
    private void DrawExpandedContent(ID2D1DeviceContext ctx, in UiState st,
                                     in RectF panel, in RectF band, float radius)
    {
        if (!PerfSkipChrome && band.MaxY - band.MinY >= 2f)
        {
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(band.MinX, band.MinY, band.MaxX, band.MaxY),
                                    AntialiasMode.Aliased);
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY), radius, radius),
                Brush(ctx, _dark ? Tokens.TroughDark : Tokens.TroughLight));
            ctx.PopAxisAlignedClip();
        }

        // 球缩进最左一格，右边是这一档的工具格（极简档就只有六格）
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++) DrawCell(ctx, k, st);

        if (BandVisible()) DrawBand(ctx, st);
    }

    /// <summary>
    /// 收起态球里面的东西：一圈当前笔色 ＋ 一个笔图标。
    ///
    /// 两处**必须按球算、不能按面板算**——老代码是按"面板的一半宽"算的，面板一长
    /// 图标就被撑成一整个大图标（出图 `--expand 0.15` 一眼就看见）：
    ///   · 大小 = 球的半径 × 系数（照假面板的数字）；
    ///   · 位置 = **球那一格的中心**。面板是往右长的，而球在展开态是第 0 格，
    ///     第 0 格的中心比球的中心靠右 4 像素（BarPad 8 ＋ 按钮 40 的一半 − 球半径 24），
    ///     所以按 e 插过去——顺手把"球缩进最左一格"这件事也演了出来。
    /// </summary>
    private void DrawBallContent(ID2D1DeviceContext ctx, in UiState st, float e, float alpha)
    {
        var bar = BarRect();
        float ballR = Tokens.Ball * 0.5f;
        float cx = bar.MinX + ballR + (Tokens.BarPad + Tokens.Button * 0.5f - ballR) * e;
        var c = new Vector2(cx, (bar.MinY + bar.MaxY) * 0.5f);
        float a = QA(alpha);

        // 数字照抄假面板：圆内那圈半径 = 0.76 × 球的半径、线宽 2.5，
        // 中间那个笔图标 = 0.70 × 球的半径。**两个比例都在 Tokens 里**（BallRing / BallIcon），
        // 改一个数的几何关系写在那儿——产品原来把 0.70 写成 1.40，笔正好顶到色圈内沿。
        var ink = st.PaletteBase;
        ctx.DrawEllipse(new Ellipse(c, ballR * Tokens.BallRing, ballR * Tokens.BallRing),
                        Brush(ctx, new Color4(ink.R, ink.G, ink.B, a)), 2.5f);

        var iconBox = new RectF
        {
            MinX = c.X - ballR, MinY = c.Y - ballR, MaxX = c.X + ballR, MaxY = c.Y + ballR,
        };
        var iconInk = InkCol;
        IconAtlas.DrawCentered(ctx, "pen", iconBox, ballR * Tokens.BallIcon,
                               Brush(ctx, new Color4(iconInk.R, iconInk.G, iconInk.B, iconInk.A * a)));
    }

    /// <summary>
    /// 开一个不透明度图层（形变期间整片内容淡入用）。**建不出来就返回 false**，
    /// 让调用方照常画——不能因为一个观感把这一帧丢掉（界面连抛三次会被引擎整体停用）。
    /// 图层**每帧现建现销**，不缓存：设备丢了之后手里就是过期的 COM 对象，
    /// 而这条路只在形变那 200 毫秒里走，代价可以忽略（安静时一次都不走）。
    /// </summary>
    private bool BeginFade(ID2D1DeviceContext ctx, float opacity)
    {
        try { _fadeLayer = ctx.CreateLayer(null); }
        catch { _fadeLayer = null; }
        if (_fadeLayer == null) return false;

        var p = new LayerParameters1
        {
            // 内容盒给"无限大"：给小了 D2D 会照它裁，内容就缺一块
            ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
            Opacity = QA(opacity),
        };
        ctx.PushLayer(ref p, _fadeLayer);
        return true;
    }

    private void EndFade(ID2D1DeviceContext ctx)
    {
        ctx.PopLayer();
        _fadeLayer?.Dispose();
        _fadeLayer = null;
    }

    /// <summary>
    /// 把 0..1 之外的截掉，里面用 smoothstep（两端速度为 0）——交叉淡入的两头才不起棱。
    /// </summary>
    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// 把透明度量化到 1/32 一档。**画刷是按颜色缓存的（建了就留着）**，逐帧喂连续值
    /// 会往缓存里塞几百个画刷；量化之后最多多 32 个，而 1/32 的台阶看不出来。
    /// </summary>
    private static float QA(float a) => MathF.Round(Math.Clamp(a, 0f, 1f) * 32f) / 32f;


    private Color4 PanelFill => _dark ? Tokens.PanelDark : Tokens.PanelLight;
    private Color4 BorderCol => _dark ? Tokens.BorderDark : Tokens.BorderLight;
    private Color4 InkCol => _dark ? Tokens.InkDark : Tokens.InkLight;
    private Color4 MutedCol => _dark ? Tokens.InkMutedDark : Tokens.InkMutedLight;
    private Color4 HoverCol => _dark ? Tokens.HoverDark : Tokens.HoverLight;

    /// <summary>
    /// 一张"卡片"：投影（逐层往外胀）＋ 底 ＋ 1px 描边（没有这道边，圆角会糊进背景里）。
    ///
    /// 投影有几层、每层胀多少、多深，全在 `Tokens.ShadowLight` / `ShadowDark` 里，
    /// 这里只负责"照单子一层层画"。**从最大的一层往最小的一层画**：大的先铺、小的后盖，
    /// 靠近面板的地方叠得最厚，往外逐渐变薄——落差就是这么来的。
    /// </summary>
    private void DrawCard(ID2D1DeviceContext ctx, RectF r, float radius)
    {
        if (!PerfSkipShadow)
        {
            var layers = _dark ? Tokens.ShadowDark : Tokens.ShadowLight;
            for (int i = layers.Length - 1; i >= 0; i--)
            {
                var s = layers[i];
                // 四边一起往外胀，圆角也加上同样的量：这样每一层都和面板**同心**。
                // 只胀矩形、不加大圆角的话，四个转角会缺一块（尖角戳出来）。
                var layerRect = new Vortice.RawRectF(r.MinX - s.Inflate, r.MinY - s.Inflate + s.Dy,
                                                     r.MaxX + s.Inflate, r.MaxY + s.Inflate + s.Dy);
                float layerRad = radius + s.Inflate;
                ctx.FillRoundedRectangle(new RoundedRectangle(layerRect, layerRad, layerRad),
                                         Brush(ctx, s.Color));
            }
        }

        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, PanelFill));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
    }

    /// <summary>
    /// 贴边隐藏时露出来的那条**把手**：一条直的药丸（收起态专用，见 Tokens.PeekTab）。
    ///
    /// 位置**直接取引擎算给我们的占用矩形**（<see cref="QueryBounds"/>）——那正是屏幕上
    /// 真正露出来的那一条，已经含了平移和"夹在屏幕内"。好处是**看得见的**和**点得到的**
    /// 天然是同一个地方，不会出现"看见一条却点不到"。
    ///
    /// 贴着左右边时露出来的那条是**竖的**，所以长边要按方向选。
    /// </summary>
    private void DrawPeekTab(ID2D1DeviceContext ctx)
    {
        var vis = QueryBounds();
        if (vis.IsEmpty) return;

        // 渲染时外面套着一层"贴边平移"的变换（见 RenderShifted），这里先减掉，
        // 画出来的位置才和占用矩形对得上。
        var s = Shift();
        var sliver = new RectF
        {
            MinX = vis.MinX - s.X, MinY = vis.MinY - s.Y,
            MaxX = vis.MaxX - s.X, MaxY = vis.MaxY - s.Y,
        };

        bool horiz = (sliver.MaxX - sliver.MinX) >= (sliver.MaxY - sliver.MinY);
        float cx = (sliver.MinX + sliver.MaxX) * 0.5f;
        float cy = (sliver.MinY + sliver.MaxY) * 0.5f;
        // 长边 = 把手长度（但不比露头那一条本身更长）；短边 = 露头有多厚（就填满它）
        float half = MathF.Min(Tokens.PeekTab,
                               horiz ? sliver.MaxX - sliver.MinX : sliver.MaxY - sliver.MinY) * 0.5f;
        float halfT = (horiz ? sliver.MaxY - sliver.MinY : sliver.MaxX - sliver.MinX) * 0.5f;

        var box = horiz
            ? new Vortice.RawRectF(cx - half, cy - halfT, cx + half, cy + halfT)
            : new Vortice.RawRectF(cx - halfT, cy - half, cx + halfT, cy + half);
        float rad = MathF.Min(halfT, half);
        var shape = new RoundedRectangle(box, rad, rad);

        // **颜色 = 当前笔色**，和展开态露出来的那条色线一模一样（用户 2026-09-18：
        // "展开条贴边以后有颜色的，这个小圆球贴边没颜色"）。
        // 它顺手把"手里是哪支笔"这件事也说了——露头时球里的色环是看不见的。
        var ink = _host.State.PaletteBase;
        ctx.FillRoundedRectangle(shape, Brush(ctx, new Color4(ink.R, ink.G, ink.B, 1f)));

        // 近白的时候补一道暗边：白笔的把手落在浅色桌面上会**什么都看不见**。
        // 这条规矩和色线（`DrawBandLine`）是同一条——那边早就这么干了。
        // 深色主题不用管：白在暗底上本来就看得见。
        if (!_dark && ink.R > 0.9f && ink.G > 0.9f && ink.B > 0.9f)
            ctx.DrawRoundedRectangle(shape, Brush(ctx, new Color4(0f, 0f, 0f, 0.2f)), 1f);
    }

    /// <summary>画上带的内容。每一项都对应引擎里真实存在的能力，摆不出来的就不摆。</summary>
    private void DrawBand(ID2D1DeviceContext ctx, in UiState st)
    {
        // 平时就是一条 **6 像素的色线，整条用当前笔色**（照假面板：不分段、没有文字），
        // 碰到才长成完整的设置条。中间那一段是"线淡出、控件淡入"的过渡
        // ——假面板的公式：t = (带高 - 12) / 14，0 = 还是一条线，1 = 完全是控件。
        float t = Math.Clamp((BandHeightFull() - 12f) / 14f, 0f, 1f);
        if (t < 0.999f) DrawBandLine(ctx, st, 1f - t);
        if (t <= 0.001f) return;

        if (!RailOpen) return;

        if (BandHasSwatches)
        {
            for (int i = 0; i < SwatchCount; i++)
            {
                var r = SwatchRect(i);
                var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
                var rr = new RoundedRectangle(box, 7f, 7f);
                ctx.FillRoundedRectangle(rr, Brush(ctx, SwatchColor(i)));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

                // 一道内高光：色片才有"实体感"（假面板里写着"颜值上最便宜的一笔"）
                if (!PerfSkipSwatchDetail)
                {
                    var hl = new Vortice.RawRectF(r.MinX + 2, r.MinY + 1, r.MaxX - 2, r.MinY + 1 + (r.MaxY - r.MinY) * 0.26f);
                    ctx.FillRoundedRectangle(new RoundedRectangle(hl, 3f, 3f),
                        Brush(ctx, new Color4(1f, 1f, 1f, _dark ? 0.13f : 0.35f)));
                }

                if (!PerfSkipSwatchDetail && IsSwatchActive(st, i))
                {
                    // 选中的色块要有**第二重标记**（只靠颜色区分不符合无障碍要求）：
                    // 往里缩 2 像素再描一圈（照假面板的 1.6 线宽）。
                    var inner = new Vortice.RawRectF(r.MinX + 2, r.MinY + 2, r.MaxX - 2, r.MaxY - 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 5f, 5f), Brush(ctx, InkCol), 1.6f);
                }
                else if (_hover == 100 + i)
                {
                    var outer = new Vortice.RawRectF(r.MinX - 2, r.MinY - 2, r.MaxX + 2, r.MaxY + 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(outer, 9f, 9f), Brush(ctx, InkCol), 1.5f);
                }
            }
        }

        DrawDashToggle(ctx, st);

        int n = BandSegmentCount;
        for (int i = 0; i < n; i++) DrawSegment(ctx, i, n, st);

        if (BandHasSlider) DrawBandSlider(ctx, st);
        DrawBandAction(ctx);

        // ⚠ 「第 N 屏」原来是**单画在带子右边**的一段文字（用的是写死的 `SegmentRect(4, 5)`），
        // 2026-09-27 改成翻页器中间那一段（见 <see cref="BoardSegKind.PageLabel"/>）——
        // 那块就是原来那个入口，只是搬到了两个箭头中间，成了"翻页器"的一部分。
    }

    /// <summary>
    /// 设置条右端的**粗细滑条**：底轨 ＋ 已选段（用当前颜色）＋ 滑钮 ＋ 右端一个
    /// 跟着变大的笔尖预览（粗细一眼看得见，比数字直观）。
    ///
    /// 滑钮**一直画**、不再"只有拖动/悬停才浮出来"：设置条本来就只在
    /// "焦点在面板上"时才出现（见 RailHoverZone），等于永远处在悬停态。
    /// 只在拖动时才出现的话，老师会以为那是个静态的分隔符。
    /// </summary>
    private void DrawBandSlider(ID2D1DeviceContext ctx, in UiState st)
    {
        var box = SliderRect();
        var (left, right) = SliderTrackRange();
        float cy = (box.MinY + box.MaxY) * 0.5f;
        float t = SliderT(st);
        var ink = st.PaletteBase;

        float h = _sliderDragging ? 6f : 5f;
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(left, cy - h * 0.5f, right, cy + h * 0.5f),
                                 h * 0.5f, h * 0.5f),
            Brush(ctx, _dark ? Tokens.TrackDark : Tokens.TrackLight));

        float kx = left + (right - left) * t;
        if (kx - left > 0.5f)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(left, cy - h * 0.5f, kx, cy + h * 0.5f),
                                     h * 0.5f, h * 0.5f),
                Brush(ctx, new Color4(ink.R, ink.G, ink.B, _sliderDragging ? 0.85f : 0.55f)));

        ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 7f, 7f), Brush(ctx, Tokens.AccentInk));
        ctx.DrawEllipse(new Ellipse(new Vector2(kx, cy), 7f, 7f),
                        Brush(ctx, new Color4(0.19f, 0.20f, 0.24f, 1f)), 1f);

        // 右端那个"笔尖预览"：**白板那一格不画**。
        // 它画的是"这一笔有多粗"（半径跟着滑条位置变），而白板这一格拖的是**板面不透明度**，
        // 跟笔宽没有半点关系——它还会拿**当前笔色**（所以在白板上是颗红点）。
        // 用户 2026-09-27 就是在图上看见它才问的："调整透明的的滑块似乎后面跟着一个小圆点？"
        // 拖滑条 / 悬停时本来就有气泡在显示板色和百分比（DrawSizePreview），信息不缺。
        if (_bandCell != 2)
        {
            float pr = Math.Clamp(2f + t * 6.5f, 2f, 8.5f);
            ctx.FillEllipse(new Ellipse(new Vector2(box.MaxX - SliderPreviewW * 0.5f - 2f, cy), pr, pr),
                            Brush(ctx, ink));
        }
    }

    /// <summary>
    /// 虚实线切换那一格。**画的是"下一笔会长什么样"**：一小段按当前线型画出来的线
    /// （和选中面板里那三格同一个语言——老师看的是线本身，不是文字）。
    ///
    /// 样本线的节长比例照抄引擎那两条 D2D dash 图案（虚线"节 3 缝 2"、点线"节≈0 缝 2"，
    /// 单位都是笔宽，见 <c>Gfx</c>），但**不能直接用那条描边样式**：
    /// 它的节长按**真实笔宽**算，46×26 的小格子里会碎成一串看不清的点。
    /// 这里按这一格自己的样本线宽算节长，看着才是"缩小的虚线"。
    /// </summary>
    private void DrawDashToggle(ID2D1DeviceContext ctx, in UiState st)
    {
        if (!BandHasDashToggle || !RailOpen) return;
        var r = DashToggleRect();
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);

        if (_hover == 500) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        // 样本线用**当前笔色**：这一格顺手把"下一笔是什么颜色"也说了一遍。
        float lx0 = r.MinX + 9f, lx1 = r.MaxX - 9f;
        float ly = (r.MinY + r.MaxY) * 0.5f;
        float v = _dashFade.Value;                    // 0 = 全是旧档，1 = 全是新档
        if (v < 0.999f) DrawDashSample(ctx, _dashFadeFrom, lx0, lx1, ly, st.PaletteBase, 1f - v);
        DrawDashSample(ctx, st.Dash, lx0, lx1, ly, st.PaletteBase, v);

        // 换挡那一瞬间描一道强调色：三档的样本线在 46 像素里差别不大，
        // 光看"线变了"不容易确认"我刚才按到了没有"。
        if (v < 0.999f)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, Alpha(Tokens.Accent, 1f - v)), 1.6f);
    }

    /// <summary>
    /// 按线型画一小段样本。<paramref name="alpha"/> ≤ 0.01 就整段不画（换挡动画的淡出那半程走这条）。
    /// </summary>
    private void DrawDashSample(ID2D1DeviceContext ctx, StrokeDash dash, float x0, float x1, float y,
                                in Color4 ink, float alpha)
    {
        if (alpha <= 0.01f) return;
        var brush = Brush(ctx, Alpha(ink, alpha));
        const float w = 2.4f;                  // 样本线宽（和选中面板那三格的 2.6 一个量级）
        switch (dash)
        {
            case StrokeDash.Dashed:
                // 节 3w、缝 2w —— 和 Gfx 里那对数字同一个比例
                for (float x = x0; x < x1; x += w * 5f)
                    ctx.DrawLine(new Vector2(x, y),
                                 new Vector2(MathF.Min(x + w * 3f, x1), y), brush, w);
                break;
            case StrokeDash.Dotted:
                // 缝 2w、每个点是一个"直径 = w"的圆（引擎那边靠圆头 dash cap 鼓出来，同一个样子）
                for (float x = x0 + w * 0.5f; x < x1; x += w * 2f)
                    ctx.FillEllipse(new Ellipse(new Vector2(x, y), w * 0.5f, w * 0.5f), brush);
                break;
            default:
                ctx.DrawLine(new Vector2(x0, y), new Vector2(x1, y), brush, w);
                break;
        }
    }

    /// <summary>
    /// 换个透明度（换挡动画要淡入淡出用）。
    ///
    /// **透明度按 1/32 量化**：<see cref="Brush"/> 那个缓存是按颜色查的，
    /// 每帧一个新透明度就是一"把"新笔刷（167 毫秒 × 每帧两次 ≈ 几十把）。
    /// 量化之后一趟动画最多用 33 个值——肉眼看不出差别，缓存也不再被动画撑大。
    /// </summary>
    private static Color4 Alpha(in Color4 c, float a)
        => new(c.R, c.G, c.B, Math.Clamp(MathF.Round(a * 32f) / 32f, 0f, 1f));

    /// <summary>这一片色片是不是"当前色"（比较尺度见 <see cref="SameColor"/>，和切色共用一把）。</summary>
    private bool IsSwatchActive(in UiState st, int i) => SameColor(SwatchColor(i), st.PaletteBase);

    /// <summary>
    /// 平时那条色线：**整条用当前笔色**，不分段、没有文字（照假面板）。
    /// `alpha` 是过渡用的——它长成设置条的过程中，这条线淡出。
    ///
    /// 一处细节：**白笔在浅底上会看不见**，所以给白线补一道极淡的描边兜底
    /// （假面板里专门为这一种情况写了这个分支）。
    /// </summary>
    private void DrawBandLine(ID2D1DeviceContext ctx, in UiState st, float alpha)
    {
        var r = BandRect();
        if (r.MaxY - r.MinY < 0.5f) return;

        var panel = UnionRect();
        float radius = Tokens.PillRadius(Tokens.BarHeight) - 6f * _expand.Value;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY), radius, radius);
        var line = st.PaletteBase;

        // **贴着面板顶边的圆角走**（照假面板）：整条线的两端跟着面板的圆角收进去，
        // 而不是在面板里缩进一条独立的小线——那样看着像"贴了张纸条"。
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
                                AntialiasMode.Aliased);
        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(line.R, line.G, line.B, alpha)));
        ctx.PopAxisAlignedClip();

        bool nearWhite = line.R > 0.9f && line.G > 0.9f && line.B > 0.9f;
        if (nearWhite && !_dark)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, new Color4(0f, 0f, 0f, 0.2f * alpha)), 1f);
    }

    /// <summary>
    /// 画白板色带上的一段（段表见 <see cref="BoardBand"/>）。六种段各有各的画法：
    ///   · **上一屏 / 下一屏**：圆角小格 ＋ 上下箭头；到顶了"上一屏"压暗（反馈在这里给）；
    ///   · **第 N 屏**：**光写字，不画框、不响应悬停**——它夹在两个箭头中间，和它们是一体的；
    ///   · **板色**：直接把那块板的颜色铺出来（颜色本身就是内容，写字反而多余），
    ///     当前板色描一圈品牌色 ＋ 里面再套一圈白（第二重标记，不靠颜色单独区分）；
    ///   · **底纹 / 间距**：文字写"现在在哪一档" ＋ 右边一竖列**档位点**。
    ///
    /// 底纹 / 间距这两格是**循环档**（点一下换下一档），这套手感是它们原来在「更多」抽屉里
    /// 就有的，搬过来没改。档位点是 2026-09-20 在图形那一格上定的规矩：
    /// **只换文字不给点的话，"这一格还能点"是隐形的**。
    /// </summary>
    private void DrawBoardSegment(ID2D1DeviceContext ctx, int i, in RectF r, in UiState st)
    {
        var seg = BoardSegAt(i);
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, 8f, 8f);
        bool hover = _hover == 200 + i;

        switch (seg.Kind)
        {
            case BoardSegKind.PageUp:
            case BoardSegKind.PageDown:
            {
                // 这条判据**与放映状态无关**——这一格只管白板（见 ActivateSegment 的说明）。
                bool enabled = seg.Kind == BoardSegKind.PageDown || _host.State.CanFlipPageUp;
                var fg = enabled ? InkCol : new Color4(InkCol.R, InkCol.G, InkCol.B, 0.30f);
                if (hover && enabled) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
                IconAtlas.DrawCentered(ctx,
                    seg.Kind == BoardSegKind.PageUp ? "chevronUp" : "chevronDown",
                    r, 16f, Brush(ctx, fg));
                return;
            }

            case BoardSegKind.PageLabel:
                _widgets.Text(ctx, $"第 {st.ScreenIndex} 屏", r, 12.5f, Brush(ctx, InkCol));
                return;

            case BoardSegKind.Color:
            {
                ctx.FillRoundedRectangle(rr, Brush(ctx, InkPalette.BoardPresets[seg.Idx].Color));
                bool active = BoardColorIs(st, seg.Idx);
                ctx.DrawRoundedRectangle(rr, active ? Brush(ctx, Tokens.Accent) : Brush(ctx, BorderCol),
                                         active ? 2f : 1f);
                if (active)
                {
                    var inner = new Vortice.RawRectF(r.MinX + 2, r.MinY + 2, r.MaxX - 2, r.MaxY - 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 6f, 6f),
                                             Brush(ctx, Tokens.AccentInk), 1.5f);
                }
                return;
            }

            default:                       // 底纹 / 间距
            {
                if (hover) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

                // 右边留一条给竖排的档位点（宽度和图形那一格同一个 12，见 DrawSegment）
                const float PipStripW = 12f;
                var textBox = new RectF
                {
                    MinX = r.MinX, MinY = r.MinY, MaxX = r.MaxX - PipStripW, MaxY = r.MaxY,
                };
                string label = seg.Kind == BoardSegKind.Pattern
                    ? PatternName(st.BoardPattern)
                    : $"{st.BoardPatternStep:F0}";
                _widgets.Text(ctx, label, textBox, 12.5f, Brush(ctx, InkCol));

                var (count, cur) = BoardPips(seg.Kind, st);
                DrawPips(ctx, r.MaxX - PipStripW * 0.5f, (r.MinY + r.MaxY) * 0.5f, cur, count, InkCol);
                return;
            }
        }
    }

    private void DrawSegment(ID2D1DeviceContext ctx, int i, int count, in UiState st)
    {
        var r = SegmentRect(i, count);
        bool active = IsSegmentActive(st, i);
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, 8f, 8f);

        // 白板那一格：段表说了算（见 BoardBand / DrawBoardSegment）
        if (_bandCell == 2) { DrawBoardSegment(ctx, i, r, st); return; }

        if (active)
        {
            ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
        }
        else if (_hover == 200 + i || _press == 200 + i)
        {
            ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        }
        ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);

        string label = SegmentLabel(i);
        if (label.Length > 0)
        {
            _widgets.Text(ctx, label, r, (r.MaxX - r.MinX) < 62f ? 11f : 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
            return;
        }

        var segInk = active ? Tokens.AccentInk : InkCol;
        // **有档位的那几格**（点它一下换一档，见 ActivateSegment 的 case 8）：
        //   · 「直线」= 3 档线型（实 / 虚 / 点）；
        //   · 「抛物线」= 2 档（上下 / 左右）；
        //   · 「双曲线」= 2 档（有 / 无渐近线）、「椭圆」= 2 档（有 / 无焦点三角形）；
        //   · 「坐标系」= 2 档（带网格 / 不带网格，2026-09-24 从「更多」抽屉挪进来的）；
        //   · 「棱柱 / 棱锥 / 棱台」= 4 档边数（三 / 四 / 五 / 六）。
        // 图标照旧画当前那一档，**右边再加一竖列档位点**——大而浓的那个是当前档。
        // 用户 2026-09-20 定：只换图标的话，老师"不知道这一格还能点"（可选的状态是隐形的）。
        // 抛物线那格是**同一天稍后补上的**（用户："抛物线页增加小圆点"）——
        // 它从加进来那天起就是"再点一次换一档"，却一直没给点，是漏的。
        //
        // ⚠ **三格各有各的档**（`st.SidesOf(tool)`）：第一版三格共用一个数，结果是
        // "点棱锥那一格，棱柱、棱台的点跟着一起动"——用户上手就报了这个。
        // 档位点画的是**这一格自己的档**，所以这里必须问 `SidesOf(segTool)`，
        // 而不是随手读一个 `st.PrismSides`。
        //
        // **点从"图标下面"挪到了"图标右边"**（还是 2026-09-20，用户看出来的）：
        // 图形段是"宽 × 26"的长方形，而图标只占 18 —— 横排放在下面的时候，为了挤出
        // 那 7 像素高，图标得**压到 16 并整体上移 3.5**；竖着放到右边之后那一列点
        // 只占约 4 宽，图标就能回到 18 并留在正中。
        //
        // **"这一格有几档、现在是第几档"只有 `PipsOf` 那一处**（绘制与自检共用）：
        // 在这里再列一遍工具名，加一种图形就会漏一处。
        // 最后那一段是**动作**不是图形：画书架图标（自绘，见 `IconAtlas.DrawLibrary`），
        // 没有档位点、也不参与"哪个图形选中了"的高亮。
        if (i >= ShapeSegmentCount)
        {
            IconAtlas.DrawCentered(ctx, "library", r, 18f, Brush(ctx, segInk));
            return;
        }

        var segTool = ShapeToolAt(i);
        var (pipCount, pipCur) = PipsOf(segTool, st);
        if (pipCount > 0)
        {
            // 右边让出这么宽的一条给竖排的点（点距/半径在 `DrawPips` 里，最浓的那个半径 2，
            //  所以这一列实际占 ~4 宽、居中在这条带的中间）。
            const float PipStripW = 12f;
            var iconBox = new RectF
            {
                MinX = r.MinX, MinY = r.MinY, MaxX = r.MaxX - PipStripW, MaxY = r.MaxY,
            };
            IconAtlas.DrawCentered(ctx, ShapeIcon(segTool), iconBox, 18f, Brush(ctx, segInk));
            DrawPips(ctx, r.MaxX - PipStripW * 0.5f, (r.MinY + r.MaxY) * 0.5f, pipCur, pipCount, segInk);
            return;
        }

        IconAtlas.DrawCentered(ctx, ShapeIcon(segTool), r, 18f, Brush(ctx, segInk));
    }

    /// <summary>
    /// 这一格右边该点**几个档位点**、第几个是当前档（`Count = 0` ＝ 这一格没有档位，
    /// 绘制那边就照原样画大图标）。**判据只有这一处**——绘制和自检都问它：
    ///   · 「直线」= 3 档线型，当前档就是 <c>LineDash</c>；
    ///   · 「抛物线」= **2 档**（上下 / 左右），当前档按 <see cref="ParabolaAxisIndex"/> 折算
    ///     ——注意 `CurveAxis` 本身是四个值（上下左右各有两向），面板这一格只管"哪一对"；
    ///   · 「棱柱 / 棱锥 / 棱台」= 4 档边数，**三格各记各的**（见 `UiState.SidesOf`）；
    ///   · 别的图形 0（它们还没有第二档）。
    /// 档位范围**来自引擎**（`st.SolidMin/MaxSides`）——`Stroke` 是引擎内部类型，
    /// 界面看不到它，也不该在这里写死一份 3/6。
    /// </summary>
    private static (int Count, int Current) PipsOf(Tool tool, in UiState st)
    {
        if (tool == Tool.Line) return (3, (int)st.LineDash);
        if (tool == Tool.Parabola) return (2, ParabolaAxisIndex(st.ParabolaAxis));
        // **双曲线**（2026-09-22）：2 档 = 有 / 无渐近线。
        // 顺序按"播放先后"排：第 1 档 = 有渐近线（默认）、第 2 档 = 无。
        if (tool == Tool.Hyperbola) return (2, st.HyperbolaAsymptotes ? 0 : 1);
        // **椭圆（带焦点）**（2026-09-22）：2 档 = 有 / 无焦点三角形（默认有）。
        if (tool == Tool.ConicEllipse) return (2, st.EllipseFocusTriangle ? 0 : 1);
        // **坐标系**（2026-09-24）：2 档 = 带网格 / 不带网格。
        // 用户那天原话："我打算把它挪到图形里面的那个坐标系……点一下切换成网格，点一下网格没了"，
        // 紧接着又说"它档位之间是有切换按钮的，你可以参照一下其他那个切换逻辑"——
        // 说的就是这个**档位点**：只换图标不给点的话，"这一格还能点"是隐形的
        //（用户 2026-09-20 就为抛物线补过一次，见上面那段）。
        // 顺序按"播放先后"排：第 1 档 = 不带网格（默认，就是画出来没格子的那个）、第 2 档 = 带网格。
        if (tool == Tool.Coordinate) return (2, st.CoordGridDefault ? 1 : 0);
        if (ShapeSpec.HasSideCount(tool))
            return (st.SolidMaxSides - st.SolidMinSides + 1,
                    Math.Clamp(st.SidesOf(tool), st.SolidMinSides, st.SolidMaxSides) - st.SolidMinSides);
        return (0, 0);
    }

    /// <summary>
    /// 画一**竖列档位点**：告诉老师"这一格有几档、现在是第几档"（从上往下 = 第 1 档 → 第 n 档）。
    ///
    /// 当前档用**大 + 浓**两个差别，其余的小一半、透明度 35%：
    /// 用"大小"而不是只用颜色，是因为这一段可能是**选中态**（整个格子铺着品牌色、
    /// 前景是白的），那时候用颜色区分就完全失效了。
    ///
    /// 点距 5、半径 2 / 1.5：4 个点竖排连起来占 19 高，段高 26 里塞得下（上下各余 3.5），
    /// 横着只占 4 宽——这就是"点挪到图标右边"能省出来给图标的那点地方（见 `DrawSegment`）。
    /// </summary>
    private void DrawPips(ID2D1DeviceContext ctx, float cx, float cy,
                          int cur, int count, Color4 fg)
    {
        const float gap = 5f;
        float y0 = cy - (count - 1) * gap * 0.5f;
        for (int k = 0; k < count; k++)
        {
            bool on = k == cur;
            var c = on ? fg : new Color4(fg.R, fg.G, fg.B, 0.35f);
            float rad = on ? 2f : 1.5f;
            ctx.FillEllipse(new Ellipse(new Vector2(cx, y0 + k * gap), rad, rad), Brush(ctx, c));
        }
    }

    /// <summary>
    /// 图形那一排**用图标不用文字**（照假面板）：图形的轮廓比"直线/矩形/椭圆"几个字
    /// 一眼得多，而且不用为几个字去量宽度（七段之后每段只有 ~79 像素，写"平行四边形"
    /// 四个字根本放不下）。
    /// </summary>
    private string ShapeIcon(int i) => ShapeIcon(ShapeToolAt(i));

    /// <summary>
    /// **图形种类 → 图标名**（带上状态的那一份）：目前几处跟状态有关——
    /// 抛物线要**转成当前开口方向**（见 <see cref="ParabolaIconName"/>）、
    /// 直线要**换成当前线型**（见 <see cref="LineIconName"/>）、
    /// 棱柱 / 棱锥 / 棱台要**换成当前档的边数**（见 <see cref="SolidIconName"/>）、
    /// 坐标系要**换成"带不带网格"**（2026-09-24，见下面那一行）。
    ///
    /// 为什么非跟状态不可：这几格"点第二下换一档"，图标不跟着换的话，
    /// 老师看不出那一下到底有没有生效（三处都是用户 2026-09-20 定的）。
    /// </summary>
    private string ShapeIcon(Tool t) => t switch
    {
        Tool.Parabola => ParabolaIconName(_host.State.ParabolaAxis),
        Tool.Line => LineIconName(_host.State.LineDash),
        // 双曲线：有 / 无渐近线两张（2026-09-22）——那一格"再点一次换一档"，
        // 图标不跟着换的话，老师看不出这一笔到底会不会带那两条虚线。
        Tool.Hyperbola => _host.State.HyperbolaAsymptotes ? "hyperbola" : "hyperbolaNoAsym",
        // 椭圆（带焦点）：有 / 无焦点三角形两张（2026-09-22），理由同上。
        Tool.ConicEllipse => _host.State.EllipseFocusTriangle ? "ovalFocusTri" : "ovalFocus",
        // 坐标系：带网格 / 不带网格两张（2026-09-24）——这一档从「更多」抽屉挪到了这一格
        //（用户："点一下切换成网格，点一下网格没了"），图标同样必须跟着换，否则
        // "这一笔画出来带不带格子"看不出来。显示的是**"以后新画的那些"**那一档
        //（已经画在板上的各存各的，见 Stroke.Grid）。
        Tool.Coordinate => _host.State.CoordGridDefault ? "axesGrid" : "axes",
        _ when ShapeSpec.HasSideCount(t) => SolidIconName(t, _host.State.SidesOf(t)),
        _ => ShapeIconFor(t),
    };

    /// <summary>
    /// 立体图形那一族的图标名：`prism3` ～ `frustum6`（前缀按工具、后缀按当前档边数）。
    ///
    /// 和直线 / 抛物线同一条理由：那几格"再点一次换一档"，图标不跟着换就看不出来。
    /// ⚠ **18 像素下"五"和"六"、以及棱柱 / 棱台**可能不太分得开，所以那一格右边
    /// 还有 **4 个档位点**兜底（见 `DrawPips`）——数点比数边可靠。
    /// </summary>
    private static string SolidIconName(Tool t, int sides)
    {
        string stem = t switch
        {
            Tool.Pyramid => "pyramid",
            Tool.Frustum => "frustum",
            _ => "prism",                              // 棱柱（也是兜底）
        };
        return stem + Math.Clamp(sides, 3, 6);
    }

    /// <summary>
    /// 抛物线的图标名按**当前档位**换（`parabola` = 上下抛物 / `parabolaRight` = 左右抛物，
    /// 见 <see cref="IconAtlas.Draw"/>）。
    ///
    /// 为什么非得变：用户 2026-09-20 把"上下还是左右"从选中框挪到了**画之前**定
    /// （那格已经选中抛物线时再点一次，见 `ActivateSegment` 的 case 8）。
    /// 图标不跟着换的话，"点第二下到底有没有生效"就没法看出来。
    ///
    /// 只有两个名字：**具体朝哪边由画的时候那一拖定**（用户 2026-09-20 更晚的口径：
    /// "感觉不对，还是参考他的逻辑"；InkClass 也是两个按钮 `case 20/21`）。
    /// </summary>
    private static string ParabolaIconName(CurveAxis axis)
        => ParabolaAxisIndex(axis) == 1 ? "parabolaRight" : "parabola";

    /// <summary>
    /// 抛物线那一格**两档**里的第几档（0 = 上下抛物、1 = 左右抛物）。
    ///
    /// 为什么要有它：`CurveAxis` 是**四个值**（上下左右各一个方向，具体朝哪边由画的时候
    /// 那一拖定），而面板这一格只管"**哪一对**"。所以"图标画哪张"和"档位点点第几个"
    /// 都得先把四个值折成两档——**这个折算只有这一处**，两处各写一遍迟早对不上
    /// （表现是"图标换了、点没跟着动"）。
    /// </summary>
    private static int ParabolaAxisIndex(CurveAxis axis)
        => axis is CurveAxis.OpenRight or CurveAxis.OpenLeft ? 1 : 0;

    /// <summary>
    /// 直线的图标名按**当前线型**换（`line` / `lineDash` / `lineDot`，见 <see cref="IconAtlas.Draw"/>）。
    ///
    /// 和抛物线那条同一个理由：那一格"再点一次换一档"（用户 2026-09-20 定：
    /// "点击直线的图标，它会变成虚线，再点击变成点虚线，再点击又变成直线……省了好几个空间格"），
    /// 图标不跟着换的话，老师点完看不出下一笔会是实线还是虚线。
    ///
    /// 三张都是**自绘**的（`IconAtlas` 里同一根线只换画法）——上游图标库里一个虚线专名都没有
    /// （连 `lineWeight` 都只有实线那一张），而且三张必须**一眼看出是同一根线的三档**，
    /// 凑上游的三张图反而会花。
    /// </summary>
    private static string LineIconName(StrokeDash dash) => dash switch
    {
        StrokeDash.Dashed => "lineDash",
        StrokeDash.Dotted => "lineDot",
        _ => "line",                               // 实线（也是兜底）
    };

    /// <summary>
    /// **图形种类 → 图标名**。上带那几段和主条"图形"那一格**共用这一份**：
    /// 主条上显示的必须就是当前种类的形状，两处各写一份迟早对不上
    /// （表现是"上带里点了三角形，主条那格还是矩形"）。
    ///
    /// 名字对不上的那几个是自绘的（`oval` / `parallelogram` / `axes` / `numberline`、
    /// 2026-09-20 加的四种曲线 `parabola` / `hyperbola` / `sine` / `cosine`，
    /// 以及直线的三档线型 `line` / `lineDash` / `lineDot`），
    /// 见 <see cref="IconAtlas.Draw"/>：上游图标库里没有这些专名。
    /// </summary>
    private static string ShapeIconFor(Tool t) => t switch
    {
        Tool.Rectangle => "square",
        Tool.Ellipse => "oval",                    // 自绘：Fluent 只有正圆
        Tool.Circle => "circle",
        Tool.Triangle => "triangle",
        Tool.Parallelogram => "parallelogram",     // 自绘：Fluent 没有这个专名
        // 坐标系 / 数轴也是自绘的（见 IconAtlas.DrawAxes / DrawNumberLine）：
        // 上游图标库里没有"两条轴"和"一条带刻度的轴"这两个专名。
        Tool.Coordinate => "axes",
        Tool.NumberLine => "numberline",
        // 四种曲线：同样自绘（见 IconAtlas.DrawParabola / DrawHyperbola / DrawWave）。
        // 图标画的是"理想样子"（开口向上的抛物线 / a = b 的双曲线 / 一个周期）。
        // **抛物线那一张会跟着当前开口方向转**——它不是这一个名字的事：
        // `ShapeIcon(Tool)` 会替它换成 `parabolaRight` / `parabolaDown` / `parabolaLeft`，
        // 所以这里给的是"默认（开口向上）"那一档，也是名字不对时的兜底。
        Tool.Parabola => "parabola",
        Tool.Hyperbola => "hyperbola",
        // 椭圆（带焦点）（2026-09-22）：自绘（见 IconAtlas.DrawOvalFocus）——椭圆 ＋ 两焦点
        // ＋（默认那一档）焦点三角形。具体哪一张由 `ShapeIcon(Tool)` 按当前档换
        //（见那里的注释），这里是**默认（有焦点三角形）**那张，也是认不出来时的兜底。
        Tool.ConicEllipse => "ovalFocusTri",
        Tool.Sine => "sine",
        Tool.Cosine => "cosine",
        // 波浪线（第十六批）：自绘（见 IconAtlas.DrawWaveLine）——**好几个周期**的正弦波，
        // 和「正弦」那一张（一个周期）一眼能分开。
        Tool.Wave => "wave",
        // 正切（第十五批）：自绘（见 IconAtlas.DrawTangent）——一支曲线 ＋ 两条渐近线。
        Tool.Tangent => "tangent",
        // 立体图形（2026-09-20 第五批）：同样自绘（见 IconAtlas.DrawCylinder / DrawCone 等）。
        Tool.Cylinder => "cylinder",
        Tool.Cone => "cone",
        // 圆台（第十三批）：自绘的第三张旋转体图标（见 IconAtlas.DrawConeFrustum）。
        Tool.ConeFrustum => "conefrustum",
        // 球（第十四批）：自绘（见 IconAtlas.DrawSphere）——轮廓圆 ＋ 赤道椭圆。
        Tool.Sphere => "sphere",
        // ⚠ 长方体 / 四面体 2026-09-20 第十二批**撤了面板入口**（见 ShapeRows 里立体那行），
        // 但它们的两张图标留着——主条那一格在"选中的是旧板书里的一个长方体"时还要画它。
        Tool.Cuboid => "cuboid",
        Tool.Tetrahedron => "tetrahedron",
        // 棱柱 / 棱锥 / 棱台：具体哪一张由 `ShapeIcon(Tool)` 按当前档换（见 SolidIconName）。
        // 这里是**默认（四棱）**那张，也是认不出来的兜底。
        Tool.Prism => "prism4",
        Tool.Pyramid => "pyramid4",
        Tool.Frustum => "frustum4",
        Tool.Arrow => "arrowRight",
        // 直线：自绘三张（实线 / 虚线 / 点线，见 IconAtlas）。这里给的是**实线**那一张，
        // 具体哪一张由 `ShapeIcon(Tool)` 按当前线型换（见 LineIconName）。
        Tool.Line => "line",
        _ => "lineWeight",                         // 认不出来的兜底
    };

    private string SegmentLabel(int i) => _bandCell switch
    {
        6 => i == 0 ? "整笔擦" : "面积擦",
        7 => i == 0 ? "矩形" : "套索",
        // 截图：名字照微信那套（8.3.1）——"截图" = 屏幕原样（连我们的板书一起），
        // "隐藏窗口截图" = 先把我们这层藏起来，拍到的只有下层内容。
        9 => i == 0 ? "截图" : "隐藏窗口截图",
        8 => "",                                   // 图形：画图标（见 ShapeIcon）
        _ => "",
    };

    private bool IsSegmentActive(in UiState st, int i) => _bandCell switch
    {
        2 => BoardSegAt(i).Kind == BoardSegKind.Color && BoardColorIs(st, BoardSegAt(i).Idx),
        6 => i == 0 ? st.Tool == Tool.Eraser : st.Tool == Tool.PixelEraser,
        7 => i == 0 ? st.SelectMode == SelectMode.Rect : st.SelectMode == SelectMode.Lasso,
        8 => i >= 0 && i < ShapeSegmentCount && st.Tool == ShapeToolAt(i),
        9 => i == (st.CaptureHideInk ? 1 : 0),     // 截图：当前是哪一种截法就高亮哪一段
        _ => false,
    };

    private static bool BoardColorIs(in UiState st, int i)
    {
        var c = InkPalette.BoardPresets[i].Color;
        return MathF.Abs(c.R - st.BoardColor.R) < 0.02f
            && MathF.Abs(c.G - st.BoardColor.G) < 0.02f
            && MathF.Abs(c.B - st.BoardColor.B) < 0.02f;
    }

    private bool IsOn(int i) => Rows[i].Kind switch
    {
        Row.DarkTheme => _dark,
        // 色带常开（2026-10-05）：同贴边隐藏，界面自己的偏好。
        Row.RailPin => _railPinned,
        // 悬停提示（界面自己的偏好，默认开；详见字段区那一段）
        Row.Tooltip => _tipEnabled,
        // 停顿成型：**默认开**，所以配置里没有这一项时显示的就是"开"
        //（见 LoadPrefs 里那一行：只有读到 "0" 才关）。
        Row.DwellShape => _host == null || _host.State.DwellShapeOn,
        // 压感粗细：状态在**引擎**（渲染期开关），界面只是显示它
        Row.Pressure => _host == null || _host.State.PressureOn,
        // 精细笔迹：状态也在引擎（默认开），界面只是显示它
        Row.FineStroke => _host == null || _host.State.FineStrokeOn,
        // 触摸手势总开关：状态也在引擎（默认开），界面只是显示它
        Row.TouchGestures => _host == null || _host.State.TouchGesturesOn,
        // [停用 2026-10-05] 墨迹预测：同压感，状态在引擎（默认关）
        // Row.Predict => _host == null || _host.State.PredictOn,
        // 墨迹两条开关：读偏好（restoreInk 默认关、pptAutoSave 默认开）。
        Row.RestoreInk => _host.GetPref("restoreInk") == "1",
        Row.PptAutoSave => _host.GetPref("pptAutoSave") != "0",
        _ => _hideEnabled,
    };

    private static string ProfileName(int i) => i switch { 0 => "极简", 1 => "自定义", _ => "完整" };

    /// <summary>开关：打开的用强调色，关的是浅底 + 描边；滑钮在右/左。</summary>
    private void DrawSwitch(ID2D1DeviceContext ctx, RectF r, bool on)
    {
        float radius = (r.MaxY - r.MinY) * 0.5f;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, on ? Tokens.Accent : HoverCol));
        if (!on) ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        float k = 14f;
        float cx = on ? r.MaxX - k * 0.5f - 3f : r.MinX + k * 0.5f + 3f;
        var c = new Vector2(cx, (r.MinY + r.MaxY) * 0.5f);
        ctx.FillEllipse(new Ellipse(c, k * 0.5f, k * 0.5f), Brush(ctx, Tokens.AccentInk));
    }

    /// <summary>画第 pos 个显示出来的格子（pos 是"当前档位里的序号"，i 是完整档下标）。</summary>
    private void DrawCell(ID2D1DeviceContext ctx, int pos, in UiState st)
    {
        var vis = VisibleCells();
        int i = vis[pos];
        var r = CellRect(pos);
        bool active = IsActive(i, st);
        bool hover = _hover == i || _press == i;

        if (i > 0)
        {
            // 分隔线画在"组变了"的地方——按**显示出来的邻居**判，不是按完整档的下标
            if (pos + 1 < vis.Length && GroupOf(i) != GroupOf(vis[pos + 1]))
            {
                float x = r.MaxX + Tokens.GroupDivider * 0.5f;
                ctx.DrawLine(new Vector2(x, r.MinY + 10), new Vector2(x, r.MaxY - 10),
                             Brush(ctx, BorderCol), 1f);
            }

            if (active)
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, Tokens.Accent));
            }
            else if (hover && !CellUnavailable(i, st))
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, HoverCol));
            }
        }

        if (i == 0)
        {
            // 收起格：一个小球 + 当前笔色那圈（和收起态那个球是同一个东西，只是变小）
            var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
            float d = 32f;
            var ring = new Ellipse(c, d * 0.5f, d * 0.5f);
            ctx.DrawEllipse(ring, Brush(ctx, st.PaletteBase), 2.5f);
            IconAtlas.DrawCentered(ctx, "pen", r, 16f, Brush(ctx, InkCol));
            return;
        }

        // **图形那一格（8）的图标**：**这一格亮着（手里就是某种图形）才跟着当前种类走**；
        // 没亮的时候画 Fluent 那个通用的"两个图形叠在一起"（`Cells[8].Icon`）。
        //
        // 为什么加了后半个条件（用户 2026-09-22 定）：这一格原来**任何时候**都画当前种类，
        // 于是"上次用过矩形"之后，手里明明是笔，图标还画着一个矩形——没选中就是通用的
        // 图形入口，画成某一种具体的图形，不熟悉的人会以为"这一格就是矩形"。
        // 选中之后跟着种类变的那半条照旧（2026-09-19 定的理由）：七种图形挤在一格里，
        // "shapes" 那个通用图标什么也没说——手里是三角形还是平行四边形，只能打开上带才知道。
        // 所以现在是**当前那一种**（和上带里高亮的那一段同一张图，共用 ShapeIconFor）。
        //
        // 代价（明确接受）：七种图形**都没有 filled 变体**（Fluent 表里没有生成），
        // 所以选中态不再像别的格那样变实心，而是"同一个轮廓 + 强调色底 + 白图标"
        // ——和上带里选中的那一段是同一种画法。
        var ink = active ? Tokens.AccentInk : InkCol;
        // **撤销/重做栈空 → 压暗**（用户 2026-09-17："撤销重做灰度"）。
        // 引擎早就把 UndoDepth / RedoDepth 递给界面了，只是界面一直没用。
        // 压暗而不是藏起来：位置固定、老师不用去找；点它也没事（引擎那边是空操作）。
        if (CellUnavailable(i, st)) ink = new Color4(ink.R, ink.G, ink.B, 0.30f);
        if (PerfSkipIcons) return;
        DrawCellIcon(ctx, i, r, Tokens.Icon, ink, active, st);
    }

    /// <summary>
    /// **画某一格的图标**——主条（<see cref="DrawCell"/>）和「更多」面板里那排钉住宫格
    /// **共用这一处**。
    ///
    /// 为什么必须收成一处：用户 2026-09-26 特意提醒"更多里面有一个设置，那里面的图标也要同步起来"。
    /// 当年两处各写了一遍（弹出面板那句只认 `Cells[cell].Icon`），于是白板那一格在带子上是自绘的板、
    /// 在面板里还是 Fluent 那个"窗口布局"；橡皮 / 选择这一轮换了图标之后也会立刻再犯一次。
    /// 仓库里"同一个名单写两处、改一处必漏一处"已经栽过好几次（见 架构-分层与规则.md 五-7），
    /// 所以这不是"顺手合并"，是修那个毛病本身。
    ///
    /// <paramref name="active"/>：这一格亮不亮（面板里恒为 false——那里的格都不是选中态）。
    /// </summary>
    private void DrawCellIcon(ID2D1DeviceContext ctx, int cell, RectF r, float size,
                              in Color4 ink, bool active, in UiState st)
    {
        var brush = Brush(ctx, ink);
        switch (cell)
        {
            // 白板：板开着的时候板面填成**这块板的颜色**（见 IconAtlas.DrawBoard）
            case 2:
                IconAtlas.DrawBoard(ctx, r, size, brush, active ? st.BoardColor : null);
                return;
            // 激光笔：那两张（见 IconAtlas.DrawLaserCell）
            case 5:
                IconAtlas.DrawLaserCell(ctx, r, size, brush, active);
                return;
            default:
                IconAtlas.DrawCentered(ctx, CellIconName(cell, st), r, size, brush);
                return;
        }
    }

    /// <summary>
    /// **这一格画哪个图标名**——"格子 → 图标"的**唯一一处判据**：
    /// 绘制（<see cref="DrawCellIcon"/>）和自检（<see cref="CellIconForTest"/>）都问它，
    /// 免得又出现"自检说画的是 A、屏幕上其实是 B"（那条 ⚠ 见 CellIconForTest）。
    /// 白板 / 激光那两格要额外参数（板色、两态），不在这张表里，由 DrawCellIcon 直接调绘制。
    ///
    /// 2026-09-26 用户挑的这一轮：
    ///   · **橡皮（6）**：未选中＝`eraserPure`（**Radix 那张纯橡皮**，MIT，15 网格；
    ///     用户 2026-09-26 从候选里挑的 B2——理由是这一族里它路径中**一根横线都没有**）；
    ///     选中＝跟着当前是哪种橡皮换：
    ///     整笔擦＝`eraser`（Fluent 原版，带横线）、面积擦＝`eraserMedium`（橡皮＋一个大圈）；
    ///   · **选择（7）**：未选中＝`lucideSelectPointer`（Lucide 虚线框＋指针）；
    ///     选中＝框选 `mdiSelection`（MDI 四角括号＋断续边）/ 套索 `lasso`（Fluent）。
    /// </summary>
    private string CellIconName(int cell, in UiState st) => cell switch
    {
        // 图形那一格：亮着（手里就是某种图形）才跟着当前种类走，没亮画通用的那张（见 DrawCell 那段）
        8 => IsActive(8, st) ? ShapeIcon(st.Tool) : Cells[8].Icon,
        6 => IsActive(6, st)
                ? (st.Tool == Tool.PixelEraser ? "eraserMedium" : "eraser")
                : "eraserPure",
        7 => IsActive(7, st)
                ? (st.SelectMode == SelectMode.Lasso ? "lasso" : "mdiSelection")
                : "lucideSelectPointer",
        _ => IsActive(cell, st) ? Cells[cell].Filled : Cells[cell].Icon,
    };

    /// <summary>
    /// 这一格现在算不算"选中的"。
    ///
    /// **穿透和工具是互斥的**（用户 2026-09-17 定）：穿透开着的时候点击落到下层程序上，
    /// 画布根本收不到笔。所以这时候工具格一律不高亮——不然会出现"鼠标"和"笔"同时亮着，
    /// 老师以为在写字、写出来一个字都没有。
    ///
    /// 白板（2）是例外：它表示的是"板开着"这个事实，和能不能写字无关，穿透时照常显示。
    /// 引擎那边配合着改了：**换工具会自动关掉穿透**（`InkEngine.SwitchTool`），
    /// 所以点一下工具格就能立刻写字，不用先去点"鼠标"把它关掉。
    /// </summary>
    private bool IsActive(int i, in UiState st)
    {
        if (i == 1) return st.PassThrough;
        if (i == 2) return st.Board;
        if (st.PassThrough) return false;
        return i switch
        {
            3 => st.Tool == Tool.Pen,
            4 => st.Tool == Tool.Highlighter,
            5 => st.Tool == Tool.Laser,
            6 => st.Tool is Tool.Eraser or Tool.PixelEraser,
            7 => st.Tool == Tool.Marquee,
            8 => HasShapeEntry(st.Tool),
            9 => st.Tool == Tool.Capture,
            _ => false,
        };
    }

    /// <summary>
    /// 这一格现在是不是"不可用"（暂时压暗的那种）。
    ///
    /// 现在只有一种：**撤销/重做栈空**。用户 2026-09-17 要的"灰度"。
    /// 判据直接用引擎给的 UndoDepth / RedoDepth——界面不去猜"上一次操作是不是能撤销"，
    /// 那种猜法迟早和引擎对不上。
    /// </summary>
    private static bool CellUnavailable(int i, in UiState st) =>
        (i == 10 && st.UndoDepth == 0) || (i == 11 && st.RedoDepth == 0);

    /// <summary>
    /// 画刷按颜色缓存。**不能每帧重建**（性能账里点过名：几何与画刷都要缓存）。
    /// 注意：设备丢失（Gfx 重建）之后这份缓存要重建——届时应重新 SetUiFactory 挂一遍界面。
    /// </summary>
    private ID2D1SolidColorBrush Brush(ID2D1DeviceContext ctx, Color4 c)
    {
        uint key = ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16)
                 | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = ctx.CreateSolidColorBrush(c, null);
        _brushes[key] = b;
        return b;
    }

    private void Invalidate()
    {
        _host?.InvalidateUi();
    }

    // ---- 悬停提示（Tooltip；2026-10-02）--------------------------------------
    //
    // 设计规格与调研见《调研-悬停提示-Tooltip.md》：
    //   · **停留 500ms 才出**（WPF `ToolTipService.InitialShowDelay` 默认 400ms，
    //     取松一档——扫过一排格子不闪）；移开 / 按下 / 拖动立刻收；
    //   · 内容 = **名称 + 当前键位 + 一句说明**；键位从引擎查（`IUiHost.KeyText`），
    //     界面不抄第二份，用户改了 settings.json 提示跟着变；
    //   · 画在整块面板的旁边（默认上方 8px，顶到屏幕就翻到下方），**不跟手、不遮指针**；
    //   · **触屏没有悬停**，所以它只是增强：面板上的常显文字一个字都不动。

    /// <summary>
    /// 这一刻"该不该出现提示"（**悬停那条路**；触摸长按有自己的开关，见 UpdateTip）。
    /// 悬停要求：开关开着、有目标、有文案、没在按/拖、也没有触摸长按正在计时。
    /// </summary>
    private bool TipWanted
        => _tipEnabled && _tipTarget != TipNoTarget
           && (_tipHoldFired
               || (_press == -1 && !_dragging && !_sliderDragging
                   && _tipHoldStart == double.NegativeInfinity))
           && TipContent(_tipTarget).Title != null;

    /// <summary>
    /// 手指/笔按住不动 = 长按候选（鼠标永不进这里）。到 600ms 由 <see cref="UpdateTip"/> 点亮。
    /// 只给"有提示文案、且长按没有被别的功能占用"的元素武装：清空（按住清空）、滑条、
    /// PPT 页码格（长按菜单）都不会走到这儿（调用点已经排除）。
    /// </summary>
    private void ArmTipHold(int target)
    {
        _tipHoldStart = _host.NowMs;
        _tipHoldTarget = target;
        _tipHoldFired = false;
        _tipLingerUntil = 0;
    }

    /// <summary>撤销长按候选（移动超阈值、移开、开关关掉、新按下都走它）。</summary>
    private void CancelTipHold()
    {
        _tipHoldStart = double.NegativeInfinity;
        _tipHoldTarget = TipNoTarget;
        _tipHoldFired = false;
    }

    /// <summary>「更多」面板里这一刻的提示目标（空白/无 = TipNoTarget）。
    /// 编号 = 1000 + 命中码，和 `MoreHitAt` 一一对应（画、命中、提示同源）。</summary>
    private static int MoreTipTargetAt(int hit)
        => hit == MoreHitNone || hit == MoreHitInside ? TipNoTarget : 1000 + hit;

    /// <summary>指针停到某个目标上（target 用 `HoverAt` 那套编号；没有就传 TipNoTarget）。</summary>
    private void SetTipTarget(int target)
    {
        if (target == TipNoTarget) { HideTip(); return; }
        if (target == _tipTarget) return;
        _tipTarget = target;
        _tipSinceMs = _host?.NowMs ?? 0;
        _tipShown = false;
        _tipFade.Jump(0f);
        Invalidate();   // "等 500ms"从这一刻起算，要一帧帧跟到点
    }

    /// <summary>收起提示（移开、按下、拖动、关开关都用它）。</summary>
    private void HideTip()
    {
        bool hadLinger = _tipLingerUntil > 0;
        _tipLingerUntil = 0;
        if (_tipTarget == TipNoTarget && !_tipShown && _tipFade.Value <= 0f)
        {
            if (hadLinger) Invalidate();
            return;
        }
        _tipTarget = TipNoTarget;
        _tipShown = false;
        _tipFade.Jump(0f);
        Invalidate();
    }

    /// <summary>
    /// 每帧推进提示状态（Render 开头调一次，和 UpdatePeek / UpdateRail 同一个位置）。
    /// 三条路：① 触摸/笔长按到点 → 直接点亮；② 停留期到点 → 收；
    /// ③ 悬停 500ms 延迟（原来的那条）。
    /// </summary>
    private void UpdateTip()
    {
        // ① 长按到点：这一次按下就算"作废"了（松手不执行，见 PointerUp）
        if (_tipHoldStart > double.NegativeInfinity && !_tipHoldFired
            && _host.NowMs - _tipHoldStart >= Tokens.TipHoldMs)
        {
            _tipHoldFired = true;
            SetTipTarget(_tipHoldTarget);
            _tipShown = true;
            _tipFade.To(1f, Tokens.TipFadeMs);
            Invalidate();
        }

        // ② 松手后的停留到期 → 收（触摸长按专用；悬停提示是移开即收）
        if (_tipShown && _tipLingerUntil > 0 && _host.NowMs >= _tipLingerUntil)
        {
            HideTip();
            return;
        }

        if (!TipWanted)
        {
            if (_tipShown || _tipFade.Value > 0f)
            {
                _tipShown = false;
                _tipFade.Jump(0f);
            }
            return;
        }
        if (!_tipShown && _host.NowMs - _tipSinceMs >= Tokens.TipDelayMs)
        {
            _tipShown = true;
            _tipFade.To(1f, Tokens.TipFadeMs);
        }
    }

    /// <summary>某个动作当前的键位文本（查引擎的键位表；没有绑定就空字符串）。</summary>
    private string Key(KeyAction a) => _host?.KeyText(a) ?? "";

    /// <summary>
    /// 提示文案：**名称 + 键位 + 一句说明**。`Title == null` = 这个东西不出提示。
    ///
    /// **范围是收窄过的**（2026-10-02 第二轮，见《调研-悬停提示-Tooltip.md》10.2/10.8）：
    /// 只有"图标-only / 带快捷键 / 隐藏手势 / 认不出来"的才配；已经写上字、点一下当场
    /// 见结果的一律不配——触摸长按会吞掉那一次点击，零信息的提示不值这个代价。判据：
    /// **这条提示有没有带来新信息**。
    ///
    /// 名字取"唯一来源"：图形名读 `ToolNames.Of`（引擎和界面共用），键位读
    /// `IUiHost.KeyText`——界面里不写死任何一个键。
    /// </summary>
    private (string Title, string Key, string Note) TipContent(int id)
    {
        if (!_tipEnabled) return default;

        // ---- 「更多」面板（编号 = 1000 + 命中码；2026-10-02 第二批）----------------
        // 面板是触摸主场，所以这些提示**鼠标悬停和手指长按都能出**。
        // 启动器格子和设置行原来那行小灰字已从画面撤掉，这里就是它们的去处。
        if (id >= 1000)
        {
            int hit = id - 1000;
            if (hit >= MoreHitTile && hit <= MoreHitTile + HubTileTotal - 1)
            {
                var (label, hint, _, _) = HubTileInfo(hit - MoreHitTile);
                return hint.Length == 0 ? default : (label, "", hint);
            }
            if (hit >= MoreHitBottom && hit < MoreHitBottom + MoreBottomTiles.Length)
            {
                var t = MoreBottomTiles[hit - MoreHitBottom];
                return (t.Label, "", t.Hint);
            }
            if (hit >= MoreHitRow && hit < MoreHitRow + Rows.Length)
            {
                int i = hit - MoreHitRow;
                return (Rows[i].Label, "", Rows[i].Hint);
            }
            if (hit >= MoreHitChip && hit < MoreHitChip + Cells.Length)
            {
                int cell = hit - MoreHitChip;
                return (Cells[cell].Tip, "",
                        _pinned[cell] ? "已钉在工具条上（点一下取消）" : "点一下钉到工具条上");
            }
            if (hit >= MoreHitProfile && hit < MoreHitProfile + 3)
                return (ProfileName(hit - MoreHitProfile), "", "切到这一档");
            if (hit == MoreHitBack) return ("返回", "", "回到启动器主页");
            if (hit == MoreHitClose) return ("关闭", "", "点面板外也能关");
            return default;
        }

        if (id >= 0 && id < Cells.Length)
        {
            var c = Cells[id];
            return id switch
            {
                // 0 号收起格、球（-2）：自解释，不配（收窄）
                1 => (c.Tip, Key(KeyAction.TogglePassThrough), "打开后点击落到底下的程序"),
                2 => (c.Tip, Key(KeyAction.ToggleBoard), $"{Key(KeyAction.FlipPageUp)} / {Key(KeyAction.FlipPageDown)} 翻屏"),
                3 => (c.Tip, Key(KeyAction.ToolPen), "已经是笔 → 再按换颜色"),
                4 => (c.Tip, Key(KeyAction.ToolHighlighter), ""),
                5 => (c.Tip, Key(KeyAction.ToolLaser), ""),
                6 => (c.Tip, Key(KeyAction.ToolEraser), "再按切「整笔 / 面积」"),
                7 => (c.Tip, Key(KeyAction.ToolMarquee), "再按切「矩形 / 套索」，双击 = 全选"),
                8 => (c.Tip, "", "22 种图形都在上带里挑"),
                9 => (c.Tip, Key(KeyAction.ToolCapture), ""),
                10 => (c.Tip, Key(KeyAction.Undo), ""),
                11 => (c.Tip, Key(KeyAction.Redo), ""),
                12 => (c.Tip, "", "课堂工具、墨迹、设置都在这儿"),
                _ => default,
            };
        }

        // 色片（100+）：颜色一眼就懂，不配（收窄；名字表还留着，画的时候要用）

        if (id >= 200 && id < 200 + BandSegmentCount) return SegmentTip(id - 200);

        if (id == 400) return CurAction switch
        {
            BandAction.Clear => ("清空整页", Key(KeyAction.Clear), "按住 0.8 秒才清，可撤销"),
            // 全选 / 粘贴图片：段上已经写了字、点一下就知道，不配（收窄）
            BandAction.CloseBoard => ("关闭白板", Key(KeyAction.ToggleBoard), "只关白板，墨迹留着"),
            _ => default,
        };

        if (id == 500) return ("线型", "", "实线 / 虚线 / 点线，点一下换");
        return default;      // 滑条（300）不出第二张卡，见上文
    }

    private (string Title, string Key, string Note) SegmentTip(int i)
    {
        switch (_bandCell)
        {
            case 2:
            {
                // 白板：只留翻页两个（有键）；页码读数、板色、底纹、间距都"点一下就知道"，不配（收窄）
                var seg = BoardSegAt(i);
                return seg.Kind switch
                {
                    BoardSegKind.PageUp => ("上一屏", Key(KeyAction.FlipPageUp), ""),
                    BoardSegKind.PageDown => ("下一屏", Key(KeyAction.FlipPageDown), ""),
                    _ => default,
                };
            }
            // 橡皮 / 框选 / 截图的分段上已经写着"整笔擦 / 面积擦 / 矩形 / 套索 / 截图…"，
            // 不配（收窄）；只有图形那 22 段是纯图标，全配。
            case 8:
                if (i >= ShapeSegmentCount) return ("图库", "", "攒下的图形，点开挑一个");
                return (ToolNames.Of(ShapeToolAt(i)), "", "");
        }
        return default;
    }

    /// <summary>某个提示目标贴着哪块画（本地布局坐标；没有就空矩形）。</summary>
    private RectF TipAnchor(int id)
    {
        if (id == -2) return BallRect();
        if (id >= 0 && id < Cells.Length)
        {
            int pos = PosOf(id);
            return pos < 0 ? RectF.Empty : CellRect(pos);
        }
        if (id >= 100 && id < 100 + SwatchCount) return SwatchRect(id - 100);
        if (id >= 200 && id < 200 + BandSegmentCount) return SegmentRect(id - 200, BandSegmentCount);
        if (id == 400 && CurAction != BandAction.None) return ActionRect();
        if (id == 500) return DashToggleRect();
        return RectF.Empty;
    }

    /// <summary>
    /// 提示卡这一刻的矩形（本地布局坐标；空 = 没得画）。
    /// 横向夹在屏幕里；纵向上**躲开整块面板**（带子在主条上方时，卡画在带子上面，
    /// 不压住色片），上方放不下就翻到面板下方。
    /// </summary>
    private RectF TipBox()
    {
        var (title, key, note) = TipContent(_tipTarget);
        if (title == null) return RectF.Empty;
        var anchor = TipAnchor(_tipTarget);
        if (anchor.IsEmpty) return RectF.Empty;

        float titleOnly = _widgets.Measure(title, Tokens.TipTitleSize);
        float titleW = titleOnly + (key.Length == 0 ? 0f
                                  : 12f + _widgets.Measure(key, Tokens.TipTitleSize));
        float noteW = note.Length == 0 ? 0f : _widgets.Measure(note, Tokens.TipNoteSize);
        float w = MathF.Max(titleW, noteW) + Tokens.TipPadX * 2f;
        float h = note.Length == 0
            ? Tokens.TipTitleSize + Tokens.TipPadY * 2f + 4f
            : Tokens.TipTitleSize + 2f + Tokens.TipNoteSize + Tokens.TipPadY * 2f + 4f;

        float cx = (anchor.MinX + anchor.MaxX) * 0.5f;
        float x0 = cx - w * 0.5f, x1 = cx + w * 0.5f;
        float lo = _screen.MinX + 4f, hi = _screen.MaxX - 4f;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }

        var panel = PanelRect();
        float gap = Tokens.TipGap + (1f - _tipFade.Value) * 3f;    // 淡入时上浮 3px
        float top = panel.MinY - gap - h;
        if (top < _screen.MinY + 4f) top = panel.MaxY + gap;
        return new RectF { MinX = x0, MinY = top, MaxX = x1, MaxY = top + h };
    }

    /// <summary>画提示卡。永远最后画（压在主条/带子/更多面板之上）。</summary>
    private void DrawTip(ID2D1DeviceContext ctx)
    {
        if (_tipFade.Value <= 0.001f) return;
        var (title, key, note) = TipContent(_tipTarget);
        if (title == null) return;
        var box = TipBox();
        if (box.IsEmpty) return;

        bool layered = BeginFade(ctx, _tipFade.Value);
        DrawCard(ctx, box, 8f);

        var line1 = new RectF
        {
            MinX = box.MinX + Tokens.TipPadX, MinY = box.MinY + Tokens.TipPadY,
            MaxX = box.MaxX - Tokens.TipPadX, MaxY = box.MinY + Tokens.TipPadY + Tokens.TipTitleSize + 2f,
        };
        _widgets.Text(ctx, title, line1, Tokens.TipTitleSize, Brush(ctx, InkCol), center: false);
        if (key.Length != 0)
        {
            float titleOnly = _widgets.Measure(title, Tokens.TipTitleSize);
            _widgets.Text(ctx, key,
                          new RectF { MinX = line1.MinX + titleOnly + 12f, MinY = line1.MinY,
                                      MaxX = line1.MaxX, MaxY = line1.MaxY },
                          Tokens.TipTitleSize, Brush(ctx, MutedCol), center: false);
        }
        if (note.Length != 0)
            _widgets.Text(ctx, note,
                          new RectF { MinX = line1.MinX, MinY = line1.MaxY,
                                      MaxX = line1.MaxX, MaxY = box.MaxY - Tokens.TipPadY },
                          Tokens.TipNoteSize, Brush(ctx, MutedCol), center: false);
        if (layered) EndFade(ctx);
    }

    // ---- 自检钩子（开发期用；产品代码不碰）--------------------------------

    /// <summary>
    /// 自检用：某一格（按**完整档的下标**）的逻辑矩形。
    /// 注意参数是"格子的编号"不是"第几个"——档位一换，显示的格子数就变了，
    /// 按序号取会跑到界外（自检第一版就是这么点空了整整一条用例）。
    /// </summary>
    internal RectF CellRectForTest(int cell) => CellRect(PosOf(cell));

    /// <summary>自检用：上带这一刻的矩形（没长出来就是空；穿透里是那条 6 像素色线，不为空）。</summary>
    internal RectF BandRectForTest => BandVisible() ? BandRect() : RectF.Empty;

    /// <summary>自检用：设置条张开到什么程度（0 = 平时那条色线，1 = 完整设置条）。
    /// "穿透只收成线、不许张开"这条自检靠它——判的是折叠动画真的走完了。</summary>
    internal float RailValueForTest => _rail.Value;

    /// <summary>自检用：主条（不含上带）的矩形。</summary>
    internal RectF BarRectForTest => BarRect();

    /// <summary>自检用：第 i 个色片的矩形。</summary>
    internal RectF SwatchRectForTest(int i) => SwatchRect(i);

    /// <summary>
    /// 自检用：这一格的设置条上**有没有**那个虚实线切换（笔 + 完整档才有，见 <see cref="BandHasDashToggle"/>）。
    /// 极简档那一条断言靠它——"挤不下所以不给"这件事必须验得出来，不然下一轮加宽了会静默变样。
    /// </summary>
    internal bool DashToggleVisibleForTest => BandHasDashToggle;

    /// <summary>自检用：虚实线那一格的矩形（点它要按物理像素）。</summary>
    internal RectF DashToggleRectForTest => DashToggleRect();

    /// <summary>自检用：换挡动画是不是还在跑（换挡完了要停，空闲帧要回 0）。</summary>
    internal bool DashFadeRunningForTest => _dashFade.Running;

    /// <summary>自检用：上带里第 i 个分段的矩形。</summary>
    internal RectF SegmentRectForTest(int i) => SegmentRect(i, BandSegmentCount);

    /// <summary>
    /// 自检用：上带现在有几段。
    /// 自检要断言"图形那格是七段"，但**段数只能从绘制/命中用的这一份真值里读**：
    /// <see cref="SegmentRectForTest"/> 不校验下标（越界的下标照样算得出一个矩形），
    /// 光靠它数不出上界。
    /// </summary>
    internal int BandSegmentCountForTest => BandSegmentCount;

    /// <summary>
    /// 自检用：白板色带上"某一种段"排第几个（找不到 -1）。名字：翻页那两个是 `PageUp` /
    /// `PageDown`、页码是 `PageLabel`、底纹 `Pattern`、间距 `Step`；板色带下标，`Color0/1/2`
    /// （0/1/2 ＝ 白/绿/黑）。
    ///
    /// 为什么要这么一个入口：**自检不许写死段下标**。段序是会被重排的——2026-09-27 这一轮
    /// 就把 `[上一屏][白][绿][黑][下一屏]` 重排成了 `[‹] 第 N 屏 [›] [白][绿][黑] [底纹][间距]`，
    /// 而写死下标的自检**改错了是静默的**：点到了别的段，红的却是那一条断言（这条教训
    /// 在抽屉那几行上吃过一次，见 `RowRectByLabelForTest` 的注释）。
    /// </summary>
    internal int BoardSegIndexOfForTest(string name)
    {
        if (_bandCell != 2) return -1;
        var segs = BoardBand;
        for (int i = 0; i < segs.Length; i++)
            if (BoardSegName(segs[i]) == name) return i;
        return -1;
    }

    /// <summary>自检用：白板色带这一刻走的是**紧凑布局**（极简档那种，没有翻页和滑条）。</summary>
    internal bool BoardCompactBandForTest => BoardCompactBand;

    private static string BoardSegName((BoardSegKind Kind, int Idx) s) =>
        s.Kind == BoardSegKind.Color ? $"Color{s.Idx}" : s.Kind.ToString();

    /// <summary>
    /// 自检用：某一格现在画的是哪个图标名。
    /// 图形那一格（8）的图标**跟着当前种类变**，所以它得问一次状态；
    /// 其余的格子图标是写死在 Cells 表里的，直接给。
    /// ⚠ 图形这一格的判据和绘制必须**同一条**（"亮着才画当前种类，没亮画通用图标"）：
    /// 自检要是无条件问 ShapeIcon，就会出现"自检说画的是三角形、屏幕上其实是通用图标"
    /// ——这正是这条自检要盯的东西（见 ShapeBandTest 的 D 段）。
    /// </summary>
    internal string CellIconForTest(int cell)
    {
        var st = _host.State;
        // 白板那一格：板开着时画的是**自绘的板 ＋ 这块板的颜色**，名字写成 "board:白板" 这样
        // ——盘里没有这个名字的图标，它只是给自检一个"现在画的是哪一块板"的判据
        //（真画法是 IconAtlas.DrawBoard，见 DrawCell）。
        if (cell == 2)
            return st.Board ? "board:" + BoardColorName(st.BoardColor) : "board";
        // 激光笔那一格：**判据直接问 IconAtlas**（`LaserIconNameForTest`）——换一支图标时
        // 只要改那边一处，这里跟着变，不会出现"自检说 Material、屏幕上其实是自绘的那支"。
        if (cell == 5)
            return IconAtlas.LaserIconNameForTest(IsActive(5, st));
        // 其余格子（含橡皮 / 选择那两格的"选中显示选中什么"）：**和绘制问同一个函数**
        //（`CellIconName`）。两处各写一遍的话，自检就成了"测另一个东西"，见上面那条 ⚠。
        return CellIconName(cell, st);
    }

    /// <summary>自检用：现在这块板是白的 / 绿的 / 黑的（按板色和三个预设比，见 InkPalette.BoardPresets）。</summary>
    private static string BoardColorName(in Color4 c)
    {
        foreach (var (name, preset) in InkPalette.BoardPresets)
            if (MathF.Abs(preset.R - c.R) < 0.02f && MathF.Abs(preset.G - c.G) < 0.02f
                && MathF.Abs(preset.B - c.B) < 0.02f) return name;
        return "自定义色";
    }

    /// <summary>
    /// 自检用：图形那一格**第 i 段**的图标名。
    /// 用来验"抛物线那一段的图标跟着当前开口方向转"（见 <see cref="ParabolaIconName"/>）——
    /// 图标不转的话，老师看不出"再点一次"到底有没有生效。
    /// </summary>
    internal string ShapeIconNameForTest(int i) => ShapeIcon(i);

    /// <summary>
    /// 自检用：图形那一格**第 i 段**右边有几个档位点、第几个是当前档（见 <see cref="PipsOf"/>）。
    /// 拿它验"哪几格该有档位点、各有几档"——用户 2026-09-20 问"抛物线那格怎么没有小圆点"
    /// 就是这类漏了才发现的，所以这条要有断言卡住。
    /// </summary>
    internal (int Count, int Current) ShapePipsForTest(int i)
        => PipsOf(ShapeToolAt(i), _host.State);

    /// <summary>自检用：这个工具在图形面板里有没有入口（见 <see cref="HasShapeEntry"/>）。</summary>
    internal static bool HasShapeEntryForTest(Tool t) => HasShapeEntry(t);

    /// <summary>自检用：图形那一格排了**几行**（自检要靠它把各段分回行里，去验"行不重叠"）。</summary>
    internal static int ShapeRowCountForTest => ShapeBandRows;

    /// <summary>自检用：第 `row` 行**几段**（自检按"行"分组时要知道每行的段数）。</summary>
    internal static int ShapeRowLengthForTest(int row) => ShapeRowCount(row);

    /// <summary>
    /// 自检用：这个工具在图形那一格里是**第几段**（两行拉平编号；没入口就 −1）。
    ///
    /// **为什么要有它**：自检里写死段号会随"加一种图形 / 撤一种图形"**静默失效**
    /// ——这次加棱锥 / 棱台时第二行往下挪了两格，写死的 16 就不声不响点了别的段。
    /// 按工具名问一句就跟着表走，挪多少次都不怕（仓库教训里那一条）。
    /// </summary>
    internal static int ShapeSegmentIndexForTest(Tool t)
    {
        int seen = 0;
        foreach (var row in ShapeRows)
        {
            int j = Array.IndexOf(row, t);
            if (j >= 0) return seen + j;
            seen += row.Length;
        }
        return -1;
    }

    /// <summary>自检用：滑条的矩形。</summary>
    internal RectF SliderRectForTest => SliderRect();

    /// <summary>
    /// 自检用：滑条轨道的左右端（逻辑坐标）。自检要拖到**两端**去验
    /// "这个工具的粗细真的走到了范围的端点"，所以必须拿到和绘制同一份的两个端点。
    /// </summary>
    internal (float Left, float Right) SliderTrackRangeForTest => SliderTrackRange();

    /// <summary>自检用：「更多」面板 / 启动器格子 / 底栏 / 设置行 / 档位 / 钉住宫格的矩形。</summary>
    internal bool MoreOpenForTest => _moreOpen;
    internal RectF MoreRectForTest => MoreRect();
    internal int MorePageForTest => (int)_morePage;
    internal RectF MoreCloseRectForTest => MoreCloseRect();
    internal RectF MoreBackRectForTest => MoreBackRect();
    internal RectF MoreRowRectForTest(int i) => MoreRowRect(i);
    internal RectF MoreProfileRectForTest(int i) => MoreProfileRect(i);
    internal RectF MoreChipRectForTest(int cell) => MoreChipRect(cell);
    /// <summary>自检用：启动器格子的矩形（按**全局编号**，各分组自动展开；越界返回空）。</summary>
    internal RectF HubTileRectForTest(int code)
    {
        int seen = 0;
        for (int s = 0; s < HubCounts.Length; s++)
        {
            if (code < seen + HubCounts[s]) return HubTileRect(s, code - seen);
            seen += HubCounts[s];
        }
        return RectF.Empty;
    }
    internal RectF HubBottomRectForTest(int i) => HubBottomRect(i);
    /// <summary>自检用：某个坐标这一刻的命中码（面板里"点得中吗"的探针）。</summary>
    internal int MoreHitForTest(float x, float y) => MoreHitAt(x, y);

    /// <summary>自检用：行表里有几行（不写死数字——插一行/删一行自检要自己跟上）。</summary>
    internal int MoreRowCountForTest => Rows.Length;

    /// <summary>自检/出图用：打开「更多」面板（产品里只能点主条「…」那一格）。</summary>
    internal void OpenMoreForTest() { OpenMore(); _more.Jump(1f); _moreH.Jump(MoreTargetH()); }

    /// <summary>自检用：关掉「更多」面板（产品里是点面板外 / 点 ✕）。</summary>
    internal void CloseMoreForTest() { CloseMore(); _more.Jump(0f); }

    /// <summary>自检/出图用：切到启动器（0）/ 设置子页（1）。</summary>
    internal void SetMorePageForTest(int i)
    {
        _morePage = (MorePage)Math.Clamp(i, 0, 1);
        _moreHover = MoreHitNone;
        _moreH.Jump(MoreTargetH());
        Invalidate();
    }

    /// <summary>自检/出图用：直接切深色主题（**不落盘**，只改这一刻的显示）。</summary>
    internal void SetDarkForTest(bool on)
    {
        _dark = on;
        PushFloatingTheme();     // 浮层（操作条/小面板）也得跟着换——和点那行开关同一条路
        Invalidate();
    }

    /// <summary>
    /// 自检用：**按行标签**找那一行的矩形（找不到返回空矩形）。
    ///
    /// 行现在长在「更多」面板里（2026-10-01 起）；这个钩子跟着走，标签表的"唯一一份"不变。
    ///
    /// 为什么不让自检写行下标：行是会被插来插去的——2026-09-17 插了底纹两行、
    /// 2026-09-19 把灰着的「学科工具」换成了坐标系 / 数轴 / 坐标系网格三行，
    /// 2026-10-01 又整个搬进了中央面板。每插一次，写死下标的自检就要去改一处引用，
    /// 而且**改错了是静默的**：点到了别的行，红色的却是那一条断言（"点重启没反应"）。
    /// 按标签找，以后插行就不会再碰到自检。
    /// </summary>
    internal RectF RowRectByLabelForTest(string labelPart)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Label.Contains(labelPart, StringComparison.Ordinal)) return MoreRowRect(i);
        return RectF.Empty;
    }

    /// <summary>自检用：第 i 行的矩形（现在指「更多」面板里的行——产品里行只在这儿）。</summary>
    internal RectF RowRectForTest(int i) => MoreRowRect(i);

    /// <summary>自检用：深色主题与贴边隐藏的开关状态。</summary>
    internal bool DarkForTest => _dark;
    internal bool HideEnabledForTest => _hideEnabled;

    /// <summary>自检/出图用：直接切到某一档（产品里在「更多 → 设置 → 工具条」里点）。</summary>
    internal void SetProfileForTest(int i) => SetProfile((Profile)i);

    /// <summary>自检/出图用：把色线张开成设置条（产品里是鼠标碰到它）。</summary>
    internal void OpenRailForTest() { _railHover = true; _rail.Jump(1f); }

    /// <summary>出图用：把上带掰到某一格（等价于点它一下，但不执行那一格的动作）。</summary>
    internal void SelectBandCellForTest(int cell)
    {
        _bandCell = cell;
        _railHover = true;      // 出图时假装"焦点就在面板上"
        _rail.Jump(1f);
    }

    /// <summary>自检用：上带这一刻停在哪一格（"现在是谁的设置条"）。</summary>
    internal int BandCellForTest => _bandCell;

    /// <summary>出图用：把"粗细预览"摆出来（产品里是拖滑条、或指针停在滑条上时出现）。</summary>
    internal void ShowSizePreviewForTest()
    {
        _hover = 300;
        _sliderDragging = true;
        _railHover = true;
        _rail.Jump(1f);
    }

    /// <summary>自检用：上带右端那个动作按钮（清空/全选）的矩形 + 按住状态。</summary>
    internal RectF ActionRectForTest => CurAction == BandAction.None ? RectF.Empty : ActionRect();
    internal bool ActionHoldingForTest => ActionHolding;
    internal float HoldProgressForTest => HoldProgress();

    /// <summary>自检用：这一格现在压暗没有（撤销/重做栈空）。</summary>
    internal bool CellUnavailableForTest(int cell) => CellUnavailable(cell, _host.State);

    /// <summary>自检用：这一刻色片有几个（极简档应该是 4）。</summary>
    internal int SwatchCountForTest => SwatchCount;

    /// <summary>自检用："允许自动收起"的开关（启动时应该是 false）。</summary>
    internal bool PeekArmedForTest => _peekArmed;

    // ---- 自检用：悬停提示 ----------------------------------------------------

    /// <summary>提示开关（「更多 → 设置 → 外观」那一行；默认开）。</summary>
    internal bool TipEnabledForTest => _tipEnabled;
    /// <summary>提示这一刻真的画出来了没有（过了 500ms 延迟 + 淡入中有值）。</summary>
    internal bool TipVisibleForTest => _tipShown && _tipFade.Value > 0.01f;
    /// <summary>任意目标的提示文案（名称 / 键位 / 说明；Title 为 null = 不出提示）。</summary>
    internal (string Title, string Key, string Note) TipContentForTest(int id) => TipContent(id);
    /// <summary>自检用：触摸/笔长按已经弹过提示没有（弹过 = 这一次松手不执行）。</summary>
    internal bool TipHoldFiredForTest => _tipHoldFired;
    /// <summary>自检用：触摸/笔长按正在计时（还没到点）。</summary>
    internal bool TipHoldRunningForTest => _tipHoldStart > double.NegativeInfinity && !_tipHoldFired;
    /// <summary>提示卡的矩形（屏幕坐标；没显示就是空矩形）。</summary>
    internal RectF TipRectForTest
    {
        get
        {
            if (!TipVisibleForTest) return RectF.Empty;
            var r = TipBox();
            if (r.IsEmpty) return RectF.Empty;
            var s = Shift();
            return new RectF
            {
                MinX = r.MinX + s.X, MinY = r.MinY + s.Y,
                MaxX = r.MaxX + s.X, MaxY = r.MaxY + s.Y,
            };
        }
    }
    /// <summary>自检用：直接开关提示（不落盘；生产路径是点设置里那一行）。</summary>
    internal void SetTipEnabledForTest(bool on)
    {
        _tipEnabled = on;
        if (!on) HideTip();
    }
    /// <summary>自检/出图用：直接把某个目标的提示摆出来（产品里靠真悬停等 500ms，出图等不起）。</summary>
    internal void ShowTipForTest(int id)
    {
        SetTipTarget(id);
        _tipShown = true;
        _tipFade.Jump(1f);
        Invalidate();
    }

    /// <summary>自检用：粗细预览这一刻的矩形（屏幕坐标；没显示就是空矩形）。</summary>
    internal RectF SizePreviewRectForTest
    {
        get
        {
            if (!SizePreviewVisible || _host == null) return RectF.Empty;
            var r = SizePreviewRect(_host.State);
            var s = Shift();
            return new RectF
            {
                MinX = r.MinX + s.X, MinY = r.MinY + s.Y,
                MaxX = r.MaxX + s.X, MaxY = r.MaxY + s.Y,
            };
        }
    }

    /// <summary>自检用：这一档显示几格 / 现在是第几档 / 某一格钉着没有。</summary>
    internal int VisibleCountForTest => VisibleCells().Length;
    internal int ProfileForTest => (int)_profile;
    internal bool PinnedForTest(int cell) => _pinned[cell];

    /// <summary>自检用：档位条第 i 段、钉住栏第 cell 格的矩形（与产品同一处：更多面板里）。</summary>
    internal RectF ProfileRectForTest(int i) => MoreProfileRect(i);
    internal RectF ChipRectForTest(int cell) => MoreChipRect(cell);

    /// <summary>自检用：现在算"展开"吗。</summary>
    internal bool ExpandedForTest => Expanded;

    /// <summary>自检用：界面看到的屏幕（核对它和 IUiHost.Screen 是不是同一个）。</summary>
    internal RectF ScreenForTest => _screen;

    /// <summary>自检用：界面看到的**工作区**（默认位置按它算，见 <see cref="RawAnchor"/>）。</summary>
    internal RectF WorkAreaForTest => _work;

    /// <summary>
    /// 自检用：直接指定"拖过的位置"（`null` = 回默认位置），不经过真鼠标拖动。
    /// 用来验"进放映时归位"——那条要看的是**归位前**已经拖到别处这个前提，
    /// 而不是拖动手势本身（手势另有专测）。
    /// </summary>
    internal void SetAnchorForTest(Vector2? anchor)
    {
        _anchor = anchor;
        Invalidate();
    }

    /// <summary>自检用：贴边隐藏的进度（1 = 完全显示，0 = 只剩露头）。</summary>
    internal float PeekForTest => _peek.Value;

    /// <summary>
    /// 自检/出图用：把"球 → 带子"的展开进度一把按到某个值（0 = 球，1 = 完全展开）。
    /// 中间态平时只看得到 200 毫秒，想核对这一段长什么样就只能这么钉住它。
    /// </summary>
    internal void SetExpandForTest(float v) => _expand.Jump(v);

    /// <summary>
    /// 自检/出图用：把贴边隐藏一把按到某个进度（顺带允许"离开就收"）。
    /// 平时这条路要"指针离开过 700 毫秒"，出图时等不起。
    /// </summary>
    internal void ForcePeekForTest(float v)
    {
        _peekArmed = true;
        _peek.Jump(v);
    }

    /// <summary>自检用：色线张开没有（false = 平时那条 6 像素的线）。</summary>
    internal bool RailOpenForTest => RailOpen;

    /// <summary>
    /// 自检用：焦点还在不在面板上（设置条该不该开着看它）。
    /// 旧的 `RailPinnedForTest` 随"点一次钉住"一起删掉了——那套状态已经不存在。
    /// </summary>
    internal bool RailHoverForTest => _railHover;

    /// <summary>自检用：某一格这一刻算不算"选中的"（穿透与工具互斥那条靠它看）。</summary>
    internal bool CellActiveForTest(int cell) => IsActive(cell, _host.State);

    /// <summary>
    /// 自检用：**这一刻档位下、完全展开后的带子宽**。
    /// 默认位置（"展开后那条带子居中"）按它算，自检要按同一个数反推球该在哪儿。
    /// </summary>
    internal float ExpandedWidthForTest => ExpandedWidth();

    /// <summary>自检用：上带这一刻多高（6 = 色线，34 = 完整设置条）。</summary>
    internal float BandHeightForTest => BandRect().MaxY - BandRect().MinY;
}