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
        ("mouse",     "mouseFilled",     "鼠标（穿透点击）"),
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

    /// <summary>
    /// 上带（设置条）现在显示谁的设置。默认是"笔"。
    /// 上带跟着**当前工具**走：键盘换工具时靠 <see cref="OnStateChanged"/> 里那条
    /// 同步规则把它掰回来，不然会出现"拿着橡皮、上带还是色板"。
    /// </summary>
    private int _bandCell = 3;

    /// <summary>上一次看到的工具。用它判断"工具真的换了"，而不是"带子对不对"。</summary>
    private Tool _lastTool = Tool.Pen;

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

    // ---- 「更多」抽屉 ------------------------------------------------------
    private bool _drawerOpen;
    private int _drawerHover = -1;
    private int _profileHover = -1;
    private int _chipHover = -1;

    /// <summary>深色主题：手动开关（用户定的），底色/图标/描边整套换。</summary>
    private bool _dark;

    /// <summary>贴边隐藏：默认关（用户定的）。开了以后贴边时只露 8 像素的头。</summary>
    private bool _hideEnabled;
    private readonly Anim _peek;           // 0 = 只剩露头，1 = 完全显示
    private readonly Anim _rail;           // 0 = 平时那条 6 像素色线，1 = 完整设置条
    private bool _railHover;

    /// <summary>悬停意图的两个时刻：进热区 120ms 才展开、离开 220ms 才收回——路过不算数。</summary>
    private double _railEnterAtMs = double.NegativeInfinity;
    private double _railExitAtMs = double.NegativeInfinity;
    private bool _hoverInside;             // 指针在"看得见的那一块"里
    private double _leftAtMs = double.NegativeInfinity;
    /// <summary>
    /// "可以开始自动收起来了"的开关：指针碰过面板一次之后才置真。
    /// 没碰过之前一律保持完整显示——启动时不许一上来就收成屏幕底边那条露头（见 UpdatePeek）。
    /// </summary>
    private bool _peekArmed;

    /// <summary>「更多」抽屉里的行。</summary>
    private enum Row { DarkTheme, AutoHide, BoardPattern, BoardStep, CoordGrid, Restart, Quit, CheckUpdate }

    private static readonly (Row Kind, string Label, bool Dangerous, bool Gray)[] Rows =
    {
        (Row.DarkTheme, "深色主题", false, false),
        (Row.AutoHide, "贴边隐藏", false, false),
        // 白板底纹（用户 2026-09-17 要的，参考 InkClass 的"无/方格/横线 + 间距"）。
        // 点一下换下一档，标签上直接写当前是哪一档。**白板没开时压暗**——
        // 底纹只画在板面上，板子没开就改了也看不见（InkClass 也是这么守的）。
        (Row.BoardPattern, "白板底纹", false, false),
        (Row.BoardStep, "底纹间距", false, false),
        // 坐标系网格（2026-09-19 第三批）。
        //
        // 坐标系 / 数轴这两个**画图种类**已经进了上带（见 ShapeBandOrder），
        // 抽屉里只留这个"设置"。原先这里有一行灰着的「学科工具」占位，
        // 2026-09-19 一度被三行真东西（坐标系/数轴/网格）替掉，随后用户要求
        // "所有的图形都从图形框那个入口进"，于是两个工具挪走上带、这一行留下。
        (Row.CoordGrid, "坐标系网格", false, false),
        (Row.Restart, "重启软件", false, false),
        (Row.Quit, "退出", true, false),
        (Row.CheckUpdate, "检查更新", false, true),
    };

    /// <summary>
    /// 第 1、3、4 行后面画分隔线（画的时候跳过的位置）。
    /// 三刀切出四组：主题/贴边 · 底纹 · 坐标系网格 · 系统。
    /// </summary>
    private static bool IsSeparatorAfter(int row) => row is 1 or 3 or 4;

    private const float DrawerW = 260f;
    private const float DrawerRowH = 40f;
    private const float DrawerPad = 12f;
    /// <summary>
    /// 抽屉离面板的空隙。比假面板的 8 大一些（用户要的"再往上一些"）：
    /// 拉开一点，抽屉和工具条才像两块东西，而不是糊在一起。
    /// </summary>
    private const float DrawerGap = 16f;
    private const float DrawerSepH = 9f;
    private const float ProfileH = 32f;        // 顶部那排"极简 / 自定义 / 完整"
    private const float GridChipH = 36f;       // 钉住那一栏里每个工具格
    private const float GridGap = 6f;

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
            // 按住清空、或者刚按完那一下的闪光：都要继续给帧，否则进度条不走、
            // 也永远到不了 0.8 秒那个点（"按住不放"这条全靠帧在推进）。
            if (ActionHolding) return true;
            if (_host != null && _host.NowMs < _actionFlashUntil) return true;
            // 色带正在"等悬停意图"时也得给帧：不然 120 毫秒到了没人去展开它，
            // 或者 220 毫秒到了没人去收它。展开/收起一旦完成，这两个条件立刻为假 → 空闲回到 0 帧。
            if (BandVisible()
                && ((_railHover && _rail.Value < 0.5f) || (!_railHover && _rail.Value > 0f)))
                return true;
            if (_hideEnabled && !_hoverInside && _peek.Value > 0f) return true;
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
        _lastTool = host.State.Tool;
        _expand.Jump(0f);
        _peek.Jump(1f);
        _rail.Jump(0f);
        _dashFade.Jump(1f);
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
        int pat = int.TryParse(_host.GetPref("boardPattern"), out int p) ? p : 0;
        float step = float.TryParse(_host.GetPref("boardStep"), out float stepPref) ? stepPref : 40f;
        _host.Commands.SetBoardPattern(pat, step);
        float op = float.TryParse(_host.GetPref("boardOpacity"), out float opPref) ? opPref : BoardOpacityMax;
        _host.Commands.SetBoardOpacity(op);

        // 坐标系网格也是**引擎状态**（它决定新画的坐标系带不带格），同一套做法：
        // 启动时推一次。默认**关**（见 Engine.CoordGridDefault 的注释）。
        _host.Commands.SetCoordGridDefault(_host.GetPref("coordGrid") == "1");
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

        var off = new List<string>();
        for (int i = 1; i < _pinned.Length; i++) if (!_pinned[i]) off.Add(i.ToString());
        _host.SetPref("unpinned", off.Count == 0 ? null : string.Join(",", off));
    }

    // ---- 布局 ---------------------------------------------------------------

    public RectF Layout(RectF screen, float dpiScale)
    {
        _screen = screen;
        return QueryBounds();
    }

    public RectF QueryBounds()
    {
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
    private bool PeekTabShown =>
        _hideEnabled && Tokens.DockPeek + (Tokens.Ball - Tokens.DockPeek) * _peek.Value <= 12f;

    /// <summary>
    /// 现在是"展开的条"还是"球"。0.5 这条线全工程共用（贴边隐藏要不要收、内容画哪一套）。
    /// </summary>
    private bool Expanded => _expand.Value > 0.5f;

    /// <summary>
    /// 画到占用矩形**外面**的那一圈（投影），告诉引擎别把它裁掉。
    /// 只影响裁剪与脏区，**不参与命中测试**——所以面板旁边照样能画线。
    /// </summary>
    public float PaintMargin => Tokens.PaintMargin;

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
    /// 从 2026-09-20 起它**按格子算**、不是一个常量：图形那一格是**两行**
    /// （第一行 8 个高频图形、第二行 4 种曲线），所以它比别的格子高一整行
    /// （段高 ＋ 行间距）。带子朝屏幕中心那一侧长（见 <see cref="BandRect"/>），
    /// 所以变高是往上/往下长，不会把主条顶走。
    /// </summary>
    private float BandHeightLogical()
        => _bandCell == ShapeCell && ShapeBandOrder2.Length > 0
            ? Tokens.BandHeight + Tokens.SegmentHeight + ShapeRowGap
            : Tokens.BandHeight;

    /// <summary>色线 / 设置条：数值够大了才按"设置条"那套画与命中（中间态归短的这边）。</summary>
    private bool RailOpen => _rail.Value >= 0.5f;

    /// <summary>上带这一刻是不是真的画出来了（长出来之前不参与命中）。</summary>
    private bool BandVisible() => BandProgress() > 0.6f;

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
    private float TotalHeight() => Tokens.BarHeight + (BandGap + Tokens.BandHeight) * BandProgress();

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
    /// 没拖过 → 那条**展开后的带子屏幕下方居中**（`x = 中心 - 展开宽度/2`），
    /// 于是收起时球停在屏幕中心**偏左**（差半个带子宽），点开以后整条带子正好居中。
    /// **这就是假面板当年的做法**（`MockWindow.ApplyLayout`：
    /// `_anchorLeft = wa.Left + (wa.Width - BarContentWidth)/2`，
    /// 注释写着"锚的是面板自己的左下角，所以抽屉展开时窗口往左上长、面板本身不动"）。
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
        float top = _anchor?.Y ?? (_screen.MaxY - Tokens.EdgeMargin - Tokens.BarHeight);
        // 没拖过：按**展开后的宽度**居中（不是当前宽度）——这样收起态和展开态
        // 左右两端都不会跳，只在"整条带子"这一级对齐。
        float x = _anchor?.X
                ?? _screen.MinX + (_screen.MaxX - _screen.MinX - ExpandedWidth()) * 0.5f;
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
        // 带子在上面时整块的顶边在 a.Y 之上。
        float bandH = (BandGap + Tokens.BandHeight) * BandProgress();
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
    // 选择 = 矩形/套索；白板 = 三种板色；图形 = 七种图形（见 ShapeBandOrder）。
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
        // 白板那一格：5 段固定宽（70），右边留出来给"第 N 屏"
        if (_bandCell == 2)
        {
            float bw = 70f;
            float bx = BandContentLeft() + i * (bw + 6f);
            float by = BandCenterY() - Tokens.SegmentHeight * 0.5f;
            return new RectF { MinX = bx, MinY = by, MaxX = bx + bw, MaxY = by + Tokens.SegmentHeight };
        }
        // **图形那一格是两行**（2026-09-20 第五批：第一行 8 个高频图形、第二行 4 种曲线）。
        // 行/列从"这一段排第几"推出来（见 ShapeToolAt / ShapeSegmentRow 那两张表），
        // 两行的段宽各自按"可用宽度 ÷ 本行段数"算——所以第二行只有 4 段，反而更宽。
        // 两行以带子中线为界上下分（各让出半个行间距），和段间距 6 是同一套网格。
        if (_bandCell == ShapeCell)
        {
            int row = ShapeSegmentRow(i);
            int cols = ShapeRowCount(row);
            int col = row == 0 ? i : i - ShapeBandOrder.Length;
            float sw = Math.Min(120f, (total - (cols - 1) * 6f) / cols);
            float sx = BandContentLeft() + col * (sw + 6f);
            float sy = row == 0
                ? BandCenterY() - Tokens.SegmentHeight - ShapeRowGap * 0.5f
                : BandCenterY() + ShapeRowGap * 0.5f;
            return new RectF { MinX = sx, MinY = sy, MaxX = sx + sw, MaxY = sy + Tokens.SegmentHeight };
        }
        float w = Math.Min(120f, (total - (count - 1) * 6f) / count);
        float x = BandContentLeft() + i * (w + 6f);
        float y = BandCenterY() - Tokens.SegmentHeight * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + Tokens.SegmentHeight };
    }

    /// <summary>图形那一格两行之间的间距（也是段与段之间的 6，见 <see cref="SegmentRect"/>）。</summary>
    private const float ShapeRowGap = 6f;

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
    /// 哪几格的设置条右边有滑条。**白板那一格也有**——它控制的是"板面不透明度"
    /// （用户 2026-09-17："增加一个透明度的拖动功能，这样可以批注的时候隐约看见下面的题目"）。
    /// </summary>
    private bool BandHasSlider => _bandCell is 2 or 3 or 4 or 5 or 6;
    /// <summary>
    /// 上带里有几段。白板那一格是 5 段：**[上一屏] [白][绿][黑] [下一屏]**
    /// ——翻屏和板色是同一类事（都属于"这块板怎么摆"），放一行最顺手。
    /// 截图那一格是 3 段：**[直接截取][隐藏界面][粘贴图片]**——前两段照 InkClass 的两项菜单，
    /// 第三段是用户 2026-09-17 要的："粘贴功能，因为其他地方使用复制功能可以到剪贴板，
    /// 但如果是触摸屏或者手写板可能没有键盘"（等于把 `Ctrl+V` 搬到屏幕上）。
    ///
    /// 图形那一格是 **7 段**（2026-09-19 补上第二批三种之后）：段数和顺序都取自
    /// <see cref="ShapeBandOrder"/>，不再写死数字——段宽是按"可用宽度 ÷ 段数"算的
    /// （见 <see cref="SegmentRect"/>），加图形不用再动布局，也不会挤成一团。
    /// </summary>
    private int BandSegmentCount => _bandCell switch
    {
        2 => 5, 6 => 2, 7 => 2, 8 => ShapeSegmentCount, 9 => 3, _ => 0,
    };

    /// <summary>
    /// **图形种类在上带里的顺序**（从左到右）：
    /// 直线 → 矩形 → 椭圆 → 圆 → 三角形 → 平行四边形 → 箭头 → 坐标系。
    ///
    /// 为什么把三种新的插在"椭圆"后面、把箭头挪到最后：
    ///   · 矩形 / 椭圆 / 圆 是"一按一拖、拖出来的那个框就是它"的同一类（都只有一个中心，
    ///     四个角由外框定），**圆紧挨着椭圆**最顺——两个都是"中心 + 半径"的东西，
    ///     分家反而要多找一眼；
    ///   · 三角形 / 平行四边形 虽然也是拖一个外框，但形状是**按固定规则从框里归一出来的**
    ///     （三角形底边水平、左右对称；平行四边形上边固定右移 1/4 宽），排在上一类后面；
    ///   · 箭头是"拖一条线"那一类，和直线一头一尾，所以被挤到最后。
    /// 原有的四段**相对次序一个没动**（直线 < 矩形 < 椭圆 < 箭头），只是箭头挪到了末尾
    /// ——上带那四段的位置语义早就在老师的肌肉记忆里了，不重排。
    ///
    /// 2026-09-19 第二批加坐标系 / 数轴时，用户看过之后要求**它们也走这里**
    /// （原话："我希望所有的图形都放到我们现成的面板上，也就是图形框里面，
    /// 从那个地方入口，不要放到'更多'里面"）。于是接了**末尾**两段：
    ///   · 接在末尾 = 前面七段的编号一个不变（`--shapebandtest` 里那些段号一个不用改）；
    ///   · 坐标系在数轴前面（大件在前，也和后加的两个热键 F / N 的顺序一致）；
    ///   · 它们和前面七段**不混排**：这两个是"一节课画一次"的学科件，
    ///     紧挨着自成一组，比插在矩形和椭圆之间更好找。
    ///
    /// **2026-09-19 稍后又撤掉了末尾那一段「数轴」**（用户："把快捷栏最后一个图标删掉，
    /// 我感觉用不到——图形里面有一个坐标系，只有向右箭头的那个坐标系"）。
    /// 于是上带 **8 段**。**只撤入口**：`Tool.NumberLine` / `StrokeKind.NumberLine`
    /// 都留着——存档里存的是一条字节，删了就是"打开旧板书少一条"（见 计划-图形工具.md 11.2）。
    /// 撤掉之后"每段 ≥ 60 逻辑像素"那条自检反而更宽松了。
    ///
    /// 这张表是**唯一来源**：画哪段（<see cref="ShapeIcon"/>）、点哪段切什么工具
    /// （<see cref="ActivateSegment"/>）、哪段高亮（<see cref="IsSegmentActive"/>）、
    /// 主条那一格画什么图标（<see cref="ShapeIconFor"/>）都读它——
    /// 各写一份的话，加一种图形就会漏掉一处。
    /// </summary>
    private static readonly Tool[] ShapeBandOrder =
    {
        Tool.Line, Tool.Rectangle, Tool.Ellipse, Tool.Circle,
        Tool.Triangle, Tool.Parallelogram, Tool.Arrow,
        Tool.Coordinate,
    };

    /// <summary>
    /// **第二行**的图形（2026-09-20 第五批，用户定："图形框里加第二列，
    /// 教师实际使用时高频的只有一行就行，到时候再调"）：
    /// 抛物线 → 双曲线 → 正弦 → 余弦。
    ///
    /// 三件事照着用户那句话定：
    ///   · **第一行一个都不动**——那八个是肌肉记忆（`--shapebandtest` 里那些按段号点击的
    ///     断言，也正因此一个都不用改）；
    ///   · 第二行放"一节课画一两次"的曲线，所以**只有 4 段**，每段反而比第一行宽；
    ///   · 两行的段数写在**这两张表**里，段宽照旧"可用宽度 ÷ 本行段数"算
    ///     （见 <see cref="SegmentRect"/>），所以以后往第二行加图形不用动布局。
    ///
    /// 它是"点哪一段切什么工具 / 哪段高亮 / 画哪张图标"的**唯一来源**（和第一行一样）：
    /// 三处各写一份的话，加一种图形就会漏掉一处（这条教训仓库里吃过三次）。
    /// </summary>
    private static readonly Tool[] ShapeBandOrder2 =
    {
        Tool.Parabola, Tool.Hyperbola, Tool.Sine, Tool.Cosine,
        // 2026-09-20 第五批：立体图形（照 InkClass 的 case 6/7/9/26 搬过来）。
        // 第二行从 4 段长到 8 段（和第一行一样宽）——段宽是"可用宽度 ÷ 本行段数"算出来的，
        // 加段不用动布局。
        Tool.Cylinder, Tool.Cone,
        // 2026-09-20 第十一批：棱柱（3/4/5/6 棱柱 ＋ 直/斜，用户提的，见 计划-图形工具.md §32）。
        // 第二行因此从 8 段长到 9 段——段宽是"可用宽度 ÷ 本行段数"算出来的，加段不用动布局。
        Tool.Prism,
        // 2026-09-20 第十二批：棱锥 / 棱台（用户提的，见 §34）。它们和棱柱**同一族**
        //（底面正 n 边形 ＋ 顶上一个中心、一样两笔、一样 3/4/5/6 档、一样有 "直" 吸附），
        // 所以紧挨着棱柱排（"三兄弟挨着"最好找）。
        Tool.Pyramid, Tool.Frustum,
        // ⚠ **长方体 / 四面体从这一行撤掉了**（同一天，用户："那两格似乎可以删除掉了，没用了"）：
        // 四棱柱（直）就是长方体、三棱锥就是四面体，它们被上面那几段覆盖了。
        // 撤的是**入口**，不是画法——`Tool.Cuboid` / `StrokeKind.Cuboid` 与整条画法都留着，
        // 旧板书里的长方体照样能打开、能选中、能删（同 2026-09-19 撤「数轴」的规矩，见 11.2）。
        // 第二行**还是 9 段**（去掉 2 段、加上 2 段）——所以段宽一点没变。
    };

    /// <summary>图形那一格在上带里的下标（两行都在这一个格子里）。</summary>
    private const int ShapeCell = 8;

    /// <summary>图形那一格一共几段（两行加起来）——命中与绘制的循环都用它。</summary>
    private static int ShapeSegmentCount => ShapeBandOrder.Length + ShapeBandOrder2.Length;

    /// <summary>
    /// 第 `i` 段（在图形那一格里，**两行拉平编号**：前 8 个是第一行、接着 4 个是第二行）
    /// 对应哪个工具。越界回第一段——宁可画错一个图标，也不让下标越界。
    /// </summary>
    private static Tool ShapeToolAt(int i)
    {
        if (i < 0) return ShapeBandOrder[0];
        if (i < ShapeBandOrder.Length) return ShapeBandOrder[i];
        int j = i - ShapeBandOrder.Length;
        return j < ShapeBandOrder2.Length ? ShapeBandOrder2[j] : ShapeBandOrder[0];
    }

    /// <summary>这一段在第几行（0 = 第一行高频图形、1 = 第二行曲线）。</summary>
    private static int ShapeSegmentRow(int i) => i < ShapeBandOrder.Length ? 0 : 1;

    /// <summary>这一行有几段（决定段宽）。</summary>
    private static int ShapeRowCount(int row) => row == 0 ? ShapeBandOrder.Length : ShapeBandOrder2.Length;

    /// <summary>
    /// 这个工具**在图形面板里有没有入口**（点主条那一格时用它判断"要不要偷偷换工具"）。
    ///
    /// **名字说明白点**：`Engine` 里也有一个 `IsShapeTool`，判的是"**这个工具画出来的是
    /// 图形还是自由笔迹**"（含数轴——它没入口但画法还在，老存档里那些数轴要能选中、能删）。
    /// 两个名字撞着、语义不同，2026-09-20 顺手把这个改成 `HasShapeEntry`：
    /// **有入口** ⊂ **能画**，差的就是数轴那一个。
    ///
    /// 判据是**那两张段表**（`ShapeBandOrder` / `ShapeBandOrder2`）——它们是"点哪一段切什么
    /// 工具 / 哪段高亮 / 画哪张图标"的唯一来源，所以这里 IndexOf 一下就够，
    /// 加图形只改表（见 2026-09-19 那一轮：这里曾经是"上带七段 **或** 坐标系/数轴那一段"，
    /// 两者合流之后收敛回一句）。
    /// </summary>
    private static bool HasShapeEntry(Tool t)
        => Array.IndexOf(ShapeBandOrder, t) >= 0 || Array.IndexOf(ShapeBandOrder2, t) >= 0;

    /// <summary>这个工具的粗细范围。**界面管范围，引擎管钳位**——引擎那边是 0.5～64。</summary>
    private (float Min, float Max) WidthRange(Tool tool) => tool switch
    {
        // 两边的数字要和引擎里各工具的档位对得上（引擎那边是
        // HighlighterWidthPresets 8/18/32、LaserWidthPresets 4/8/14、
        // EraserRadiusPresets 12/22/34、PixelEraserWidthPresets 46/93/150）。
        // 界面拿不到引擎的 internal 常量（那是**故意**的：界面只认公开契约），
        // 所以两边各留一份数字，靠自检卡住：--paneltest 会把滑条拖到两端，
        // 断言引擎里那个值真的走到了范围的端点。
        Tool.Highlighter => (8f, 64f),
        Tool.Laser => (4f, 24f),
        Tool.Eraser => (8f, 48f),          // 整笔橡皮改的是**落点半径**
        Tool.PixelEraser => (30f, 160f),   // 面积橡皮改的是**那一块的横边**（高 = 横边 × 1.618）
        _ => (1.5f, 40f),
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
    // 位置：上带**最右端**。粗细滑条也在右端，所以橡皮那条是
    // `[整笔擦][面积擦] …… [粗细滑条][清空]`——动作永远贴在最外沿。
    private enum BandAction { None = 0, Clear, SelectAll }

    private BandAction ActionOf(int bandCell) => bandCell switch
    {
        6 => BandAction.Clear,        // 清空 ≈ "全擦掉"，和两种橡皮排一条
        7 => BandAction.SelectAll,    // 全选 ≈ "把要操作的东西一次选上"，归选择这条
        _ => BandAction.None,
    };
    private BandAction CurAction => ActionOf(_bandCell);

    private const float ActionW = 92f;
    private const double ClearHoldMs = 800;

    private RectF ActionRect()
    {
        var band = BandRect();
        float right = band.MaxX - BarInset();
        float cy = (band.MinY + band.MaxY) * 0.5f;
        return new RectF
        {
            MinX = right - ActionW, MinY = cy - Tokens.SegmentHeight * 0.5f,
            MaxX = right, MaxY = cy + Tokens.SegmentHeight * 0.5f,
        };
    }

    /// <summary>动作按钮要占的横向空间（色片 / 分段 / 滑条都得让位）。</summary>
    private float ActionReserve => CurAction == BandAction.None ? 0f : ActionW + 10f;

    /// <summary>清空的按住计时（-inf = 没在按）与"刚按完闪一下"的时刻。</summary>
    private double _actionHoldFrom = double.NegativeInfinity;
    private double _actionFlashUntil = double.NegativeInfinity;
    private bool ActionHolding => !double.IsNegativeInfinity(_actionHoldFrom);

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

    /// <summary>动作按钮：图标 ＋ 文字；清空那条按住时从左边往右填进度。</summary>
    private void DrawBandAction(ID2D1DeviceContext ctx)
    {
        var a = CurAction;
        if (a == BandAction.None || !RailOpen) return;
        var r = ActionRect();
        bool clear = a == BandAction.Clear;
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f, 7f);
        var tint = clear ? new Color4(0.90f, 0.35f, 0.25f, 1f) : Tokens.Accent;

        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(tint.R, tint.G, tint.B, 0.08f)));
        float t = HoldProgress();
        if (t > 0.002f)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY,
                                     r.MinX + (r.MaxX - r.MinX) * t, r.MaxY), 7f, 7f),
                Brush(ctx, new Color4(tint.R, tint.G, tint.B, 0.45f)));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        var iconBox = new RectF { MinX = r.MinX + 6f, MinY = r.MinY, MaxX = r.MinX + 28f, MaxY = r.MaxY };
        IconAtlas.DrawCentered(ctx, clear ? "broom" : "selectAll", iconBox, 16f, Brush(ctx, InkCol));
        var labelBox = new RectF { MinX = r.MinX + 28f, MinY = r.MinY, MaxX = r.MaxX - 6f, MaxY = r.MaxY };
        _widgets.Text(ctx, clear ? "清空" : "全选", labelBox, 12.5f, Brush(ctx, InkCol));

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
        BandVisible() && BandHasSlider && (_sliderDragging || _hover == 300);

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
            if (SegmentRect(i, n).Contains(x, y)) return i;
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

        // 正在拖滑条：一直开着，不参与悬停那套计时
        // （手滑到轨道外面一点点不该让设置条收掉——拖到一半收掉是最气人的一种）
        if (_sliderDragging)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 抽屉开着的时候设置条让位（假面板同一条：这两个抢的是同一块地方）。
        // 少了这一条会有个很别扭的画面：抽屉还开着，指针在主条上一动，
        // 设置条就从抽屉底下冒出来一截（抽屉离主条只有 16 像素，设置条有 34 高）。
        if (_drawerOpen)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _railHover = false;
            _rail.To(0f, Tokens.RailMs);
            return;
        }

        double now = _host.NowMs;
        if (_railHover)
        {
            _railExitAtMs = double.NegativeInfinity;
            if (_rail.Value >= 0.5f) { _railEnterAtMs = double.NegativeInfinity; return; }
            if (double.IsNegativeInfinity(_railEnterAtMs)) _railEnterAtMs = now;
            else if (now - _railEnterAtMs >= 120) _rail.To(1f, Tokens.RailMs);
            return;
        }

        _railEnterAtMs = double.NegativeInfinity;
        if (_rail.Value <= 0.001f) { _railExitAtMs = double.NegativeInfinity; return; }
        if (double.IsNegativeInfinity(_railExitAtMs)) _railExitAtMs = now;
        else if (now - _railExitAtMs >= 220) _rail.To(0f, Tokens.RailMs);
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
        float h = Tokens.BandHeight + Tokens.RailHoverPad * 2f;
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
    /// 这一档显示哪几个色片（返回 <see cref="Tokens.Palette"/> 里的下标）。
    ///
    /// **极简档只给 4 个**：短胶囊（359 宽）里塞 12 个色片、再减掉滑条占的那 146，
    /// 每个只剩 11 像素宽——点都点不准（用户 2026-09-17："极简模式的色带展开栏里面的
    /// 内容排布有点问题"）。4 个的话每个 42 像素，和完整档一个手感。
    /// 颜色照假面板定的：**红 / 黑 / 蓝 / 白**（讲课时最常用的四支）。
    /// </summary>
    private static readonly int[] MiniSwatchIdx = { 3, 0, 8, 2 };
    private static readonly int[] FullSwatchIdx = CreateFullSwatchIdx();

    private static int[] CreateFullSwatchIdx()
    {
        var a = new int[Tokens.Palette.Length];
        for (int i = 0; i < a.Length; i++) a[i] = i;
        return a;
    }

    private int[] SwatchIdx => _profile == Profile.Mini ? MiniSwatchIdx : FullSwatchIdx;
    private int SwatchCount => SwatchIdx.Length;
    private Color4 SwatchColor(int i) => Tokens.Palette[SwatchIdx[i]].Color;

    private void ActivateSwatch(int i) => _host.Commands.SetColor(SwatchColor(i));

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
        switch (_bandCell)
        {
            case 2:                       // 白板：[上一屏] [白][绿][黑] [下一屏]
                if (i == 0) { _host.Commands.FlipPage(false); break; }        // 上一屏
                if (i == 4) { _host.Commands.FlipPage(true); break; }         // 下一屏
                // 三色：选板色＝要用板，所以顺手把板打开
                _host.Commands.SetBoardColor(InkPalette.BoardPresets[i - 1].Color);
                _host.Commands.SetBoard(true);
                break;
            case 6:                       // 整笔擦 / 面积擦 —— 引擎里是**两个工具**
                _host.Commands.SetTool(i == 0 ? Tool.Eraser : Tool.PixelEraser);
                break;
            case 7:                       // 矩形框选 / 自由套索
                _host.Commands.SetSelectMode(i == 0 ? SelectMode.Rect : SelectMode.Lasso);
                break;
            case 8:                       // 图形那一格：**两行**拉平编号，顺序见两张表
                if (i >= 0 && i < ShapeSegmentCount)
                {
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
                    else if (ShapeSpec.HasSideCount(picked) && _host.State.Tool == picked)
                        _host.Commands.CycleSolidSides();
                    else
                        _host.Commands.SetTool(picked);
                }
                break;
            case 9:                       // 截图：[直接截取][隐藏界面][粘贴图片]
                // 照 InkClass 的两项菜单：默认"隐藏界面"（只拍下层内容），
                // "直接截取"连板书一起拍。第三段是**动作**：把剪贴板里的东西粘进来
                // （没有键盘的触摸屏 / 手写板也能用，等于把 Ctrl+V 搬到了屏幕上）。
                if (i == 2) { _host.Commands.Paste(); break; }
                _host.Commands.SetCaptureHideInk(i == 1);
                break;
        }
    }

    // ---- 「更多」抽屉 ------------------------------------------------------

    /// <summary>抽屉顶部的档位条（极简 / 自定义 / 完整）。</summary>
    private RectF ProfileRect(int i)
    {
        var d = DrawerRect();
        float w = (d.MaxX - d.MinX - DrawerPad * 2 - 2 * 8f) / 3f;
        float x = d.MinX + DrawerPad + i * (w + 8f);
        return new RectF { MinX = x, MinY = d.MinY + DrawerPad, MaxX = x + w, MaxY = d.MinY + DrawerPad + ProfileH };
    }

    private int ProfileIndex() => (int)_profile;

    /// <summary>"钉住"那一栏：12 个工具格，排 4 列。点一下切换钉住/取消。</summary>
    private float GridTop() => DrawerRect().MinY + DrawerPad + ProfileH + 12f;

    private RectF ChipRect(int cell)
    {
        var d = DrawerRect();
        int idx = cell - 1;                        // 0..11（0 号收起格不参与钉）
        float w = (d.MaxX - d.MinX - DrawerPad * 2 - 3 * GridGap) / 4f;
        int col = idx % 4, row = idx / 4;
        float x = d.MinX + DrawerPad + col * (w + GridGap);
        float y = GridTop() + row * (GridChipH + GridGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + GridChipH };
    }

    private int HitChip(float x, float y)
    {
        if (!_drawerOpen) return -1;
        for (int cell = 1; cell < Cells.Length; cell++)
            if (ChipRect(cell).Contains(x, y)) return cell;
        return -1;
    }

    private int HitProfile(float x, float y)
    {
        if (!_drawerOpen) return -1;
        for (int i = 0; i < 3; i++) if (ProfileRect(i).Contains(x, y)) return i;
        return -1;
    }

    /// <summary>抽屉的高：档位条 ＋ 钉住那一栏 ＋ 6 行 ＋ 两条分隔 ＋ 上下内边距。算出来的。</summary>
    private static float DrawerHeight()
    {
        float h = DrawerPad * 2f + ProfileH + 12f + 3f * (GridChipH + GridGap) + 10f;
        h += Rows.Length * DrawerRowH;
        for (int i = 0; i < Rows.Length; i++) if (IsSeparatorAfter(i)) h += DrawerSepH;
        return h;
    }

    /// <summary>主条 ＋ 上带（不含抽屉）。</summary>
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

    /// <summary>
    /// 抽屉：**右对齐、放在内容那一侧**（和上带同一个方向）。
    /// 右对齐是因为它是由最右那格「更多」打开的——弹出的东西应该出现在手指附近。
    /// </summary>
    private RectF DrawerRect()
    {
        float h = DrawerHeight();
        // 位置以**色带完全展开**时的面板顶为参照，不用"这一刻"的面板顶：
        // 色带在 6↔34 之间长短变化，抽屉要是跟着它走，鼠标一碰到色带抽屉就往上跳一下
        // —— 那正是用户说的"色带展开以后有起伏"。
        var panel = PanelRectFullBand();
        float maxX = panel.MaxX;
        float y = BandAbove() ? panel.MinY - DrawerGap - h : panel.MaxY + DrawerGap;
        return new RectF { MinX = maxX - DrawerW, MinY = y, MaxX = maxX, MaxY = y + h };
    }

    /// <summary>如果按"色带完全展开"来算，面板会占哪一块（只给抽屉定位用）。</summary>
    private RectF PanelRectFullBand()
    {
        var bar = BarRect();
        float h = Tokens.BandHeight + BandGap;
        return BandAbove()
            ? new RectF { MinX = bar.MinX, MinY = bar.MinY - h, MaxX = bar.MaxX, MaxY = bar.MaxY }
            : new RectF { MinX = bar.MinX, MinY = bar.MinY, MaxX = bar.MaxX, MaxY = bar.MaxY + h };
    }

    private float RowTop(int i)
    {
        // 行在**档位条 ＋ 钉住栏**的下面
        float y = GridTop() + 3f * (GridChipH + GridGap) + 10f;
        for (int k = 0; k < i; k++)
        {
            y += DrawerRowH;
            if (IsSeparatorAfter(k)) y += DrawerSepH;
        }
        return y;
    }

    private RectF RowRect(int i)
    {
        var d = DrawerRect();
        float y = RowTop(i);
        return new RectF { MinX = d.MinX + DrawerPad, MinY = y, MaxX = d.MaxX - DrawerPad, MaxY = y + DrawerRowH };
    }

    /// <summary>开关的矩形（行右侧那个小胶囊）。</summary>
    private RectF SwitchRect(int i)
    {
        var r = RowRect(i);
        float w = 36f, h = 20f;
        float cy = (r.MinY + r.MaxY) * 0.5f;
        return new RectF { MinX = r.MaxX - w, MinY = cy - h * 0.5f, MaxX = r.MaxX, MaxY = cy + h * 0.5f };
    }

    private bool IsToggleRow(int i) => Rows[i].Kind is Row.DarkTheme or Row.AutoHide or Row.CoordGrid;

    /// <summary>
    /// 这一行现在是不是压暗（点了没反应）。两种来源：
    ///   · 表里写死的（检查更新 / 学科工具还没做）；
    ///   · **白板没开时的底纹两行**——底纹只画在板面上，板子没开就改了也看不见，
    ///     所以压暗（InkClass 也是这么守的：板面收起时右键不弹那个菜单）。
    /// </summary>
    private bool IsGrayRow(int i) =>
        Rows[i].Gray
        || (Rows[i].Kind is Row.BoardPattern or Row.BoardStep && _host != null && !_host.State.Board);

    /// <summary>底纹三档的名字（0/1/2），和引擎那边的取值一一对应。</summary>
    private static readonly string[] PatternNames = { "无", "方格", "横线" };
    /// <summary>底纹间距的三档（逻辑像素）。细格写字、中格常用、粗格当横线纸。</summary>
    private static readonly float[] PatternSteps = { 24f, 40f, 64f };

    private static string PatternName(int p) =>
        PatternNames[Math.Clamp(p, 0, PatternNames.Length - 1)];

    /// <summary>行标签：底纹两行要把"当前是哪一档"写出来（它们不是开关，是循环档）。</summary>
    private string RowLabel(int i)
    {
        if (_host == null) return Rows[i].Label;
        var st = _host.State;
        return Rows[i].Kind switch
        {
            Row.BoardPattern => $"白板底纹：{PatternName(st.BoardPattern)}",
            Row.BoardStep => $"底纹间距：{st.BoardPatternStep:F0}",
            _ => Rows[i].Label,
        };
    }

    private int HitRow(float x, float y)
    {
        if (!_drawerOpen) return -1;
        var d = DrawerRect();
        if (!d.Contains(x, y)) return -1;
        for (int i = 0; i < Rows.Length; i++)
            if (RowRect(i).Contains(x, y) && !IsGrayRow(i)) return i;
        return -1;
    }

    private void ActivateRow(int i)
    {
        switch (Rows[i].Kind)
        {
            case Row.DarkTheme:
                _dark = !_dark;
                SavePrefs();
                PushFloatingTheme();       // 浮层（操作条/小面板）也得跟着换
                Invalidate();
                break;
            case Row.AutoHide:
                _hideEnabled = !_hideEnabled;
                _peek.Jump(1f);          // 刚打开时先给个完整的，别一开就缩起来
                SavePrefs();
                Invalidate();
                break;

            // 底纹两行：**循环档位**（点一下换下一档），改完顺手落盘。
            case Row.BoardPattern:
            {
                var st = _host.State;
                int next = (st.BoardPattern + 1) % PatternNames.Length;
                _host.Commands.SetBoardPattern(next, st.BoardPatternStep);
                SavePrefs();
                Invalidate();
                break;
            }
            case Row.BoardStep:
            {
                var st = _host.State;
                int idx = Array.FindIndex(PatternSteps, v => MathF.Abs(v - st.BoardPatternStep) < 0.5f);
                float next = PatternSteps[(idx + 1 + PatternSteps.Length) % PatternSteps.Length];
                _host.Commands.SetBoardPattern(st.BoardPattern, next);
                SavePrefs();
                Invalidate();
                break;
            }
            // 坐标系网格：**选中了坐标系就改它们，没选中就翻"新画的默认值"**
            // （分派规则见 Engine.ToggleSelectionGrid）。落盘**只在改默认值时**做——
            // 改对象是一次编辑动作，不该顺手把偏好也改了。
            case Row.CoordGrid:
                if (_host.Commands.ToggleCoordGrid() == 0) SavePrefs();
                Invalidate();
                break;

            case Row.Restart:
                _host.Commands.Restart();     // 引擎会先暂存板书再重启
                break;
            case Row.Quit:
                _host.Commands.Quit();
                break;
        }
    }

    /// <summary>切档。切完要检查"当前工具还在不在这一档里"——不在就落到笔。</summary>
    private void SetProfile(Profile p)
    {
        _profile = p;
        if (PosOf(CellForTool(_host.State.Tool)) < 0)
            _host.Commands.SetTool(Tool.Pen);
        _drawerHover = -1;
        _hover = -1;
        _press = -1;
        SavePrefs();
        Invalidate();
    }

    /// <summary>钉住 / 取消钉住。笔、橡皮、「更多」是安全项（取消了就没法用），不许动。</summary>
    private void TogglePin(int cell)
    {
        if (!CanUnpin(cell)) return;
        _pinned[cell] = !_pinned[cell];
        SetProfile(Profile.Custom);
    }

    // ---- 贴边隐藏 -----------------------------------------------------------

    /// <summary>整块（主条 ＋ 上带 ＋ 抽屉）**未平移**的矩形。</summary>
    private RectF UnionRect()
    {
        var p = PanelRect();
        if (!_drawerOpen) return p;
        var d = DrawerRect();
        return new RectF
        {
            MinX = Math.Min(p.MinX, d.MinX), MinY = Math.Min(p.MinY, d.MinY),
            MaxX = Math.Max(p.MaxX, d.MaxX), MaxY = Math.Max(p.MaxY, d.MaxY),
        };
    }

    private static RectF Union(in RectF a, in RectF b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX), MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX), MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    /// <summary>
    /// 贴边隐藏的位移：往贴着的那条边挪，最后只剩 `DockPeek` 那么宽露在外面。
    /// 几何全部按"没挪"算，只有最后一步整体平移——这样命中、绘制、占用矩形三处
    /// 不会各写一份坐标换算（那是这类 bug 的老窝）。
    /// </summary>
    private Vector2 Shift()
    {
        if (!_hideEnabled) return Vector2.Zero;
        float t = 1f - _peek.Value;
        if (t <= 0.001f) return Vector2.Zero;

        var u = UnionRect();
        float w = u.MaxX - u.MinX, h = u.MaxY - u.MinY;
        float dl = u.MinX - _screen.MinX, dr = _screen.MaxX - u.MaxX;
        float dt = u.MinY - _screen.MinY, db = _screen.MaxY - u.MaxY;
        float best = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        if (best > Tokens.SnapDistance) return Vector2.Zero;      // 没贴边就不藏

        // **方向**按工作区挑（面板停在哪儿），**位移量**按**屏幕**边算。
        //
        // 为什么位移量不能按工作区：面板不会"藏到任务栏后面"——我们的覆盖层是全屏置顶的，
        // 任务栏挡不住它。只有把面板推出**屏幕**，它才真的看不见。按工作区算的话，
        // 露头会变成 8 ＋（任务栏那段高度）＝几十像素（自检当场量到过 56）。
        float sl = u.MinX - _screen.MinX, sr = _screen.MaxX - u.MaxX;
        float st = u.MinY - _screen.MinY, sb = _screen.MaxY - u.MaxY;

        // **左右两条边不藏"展开态的条"**（用户 2026-09-18 定的规则，见
        // 调研-界面-贴边与隐藏.md 附三）。理由：面板是横的，把它缩进侧边等于**侧着塞进边里**
        // ——贴左边时露出来的其实是它的**右端**（最后一格和动作按钮区），跟"球"没有任何关系；
        // 而球是 48×48 的正方形，塞进哪条边都是同一个姿态。
        // 所以：**收起态四边都能藏，展开态只在上下藏**。左右仍然保留"吸附停靠"（那是拖动的事，
        // 在 Snap 里，不在这），只是不再往里缩。
        if ((best == dl || best == dr) && Expanded) return Vector2.Zero;

        if (best == dl) return new Vector2(-(w - Tokens.DockPeek + sl) * t, 0);
        if (best == dr) return new Vector2((w - Tokens.DockPeek + sr) * t, 0);
        if (best == dt) return new Vector2(0, -(h - Tokens.DockPeek + st) * t);
        return new Vector2(0, (h - Tokens.DockPeek + sb) * t);
    }

    /// <summary>
    /// 每帧更新"该不该收起来"。写得像个小状态机，因为规则就三条：
    /// 写字中不许动、指针在里面/正按着/抽屉开着不许收、刚离开要等一会儿（防误触）。
    /// </summary>
    private void UpdatePeek()
    {
        if (!_hideEnabled) { _peek.To(1f, 0); return; }

        // **启动之后先不藏**（用户 2026-09-17："在贴边隐藏的情况下，刚启动软件的时候不要隐藏"）。
        //
        // 理由很实在：`_leftAtMs` 初值是负无穷，所以第一帧就满足"离开够久了"——
        // 一开机面板立刻收成屏幕底边那条 8 像素的露头（而且露头正好压在任务栏上），
        // 老师根本找不到它。现在改成：**先露着**，等指针碰过面板一次（`_peekArmed`）
        // 才允许"离开就收"——那时候他已经知道东西在哪儿了。
        if (!_peekArmed) { _peek.To(1f, 0); return; }

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
            return;
        }

        bool keepOpen = _hoverInside || _press != -1 || _sliderDragging || _drawerOpen;
        if (keepOpen)
        {
            _leftAtMs = _host.NowMs;
            _peek.To(1f, Tokens.SnapMs);
            return;
        }
        if (_host.NowMs - _leftAtMs < 700) return;               // 刚离开：再等等（防误触）
        _peek.To(0f, Tokens.SnapMs);
    }

    // ---- 输入 ---------------------------------------------------------------

    public bool PointerDown(in UiPointerEvent e)
    {
        var p = Local(e);
        _press = -1;
        _dragging = false;
        // **"按下了"不等于"按在面板上"**。引擎会把**每一次**按下都转给界面
        // （界面有权决定吃不吃），所以这里必须自己判一次位置。
        // 无条件置 true 的后果（用户 2026-09-18 报的"贴边隐藏以后我一写它就取消贴边了"）：
        // 在画布上落笔 → 这里置 true、随后返回 false（这一笔归画布）→ 但 true 留了下来，
        // 而**写字期间引擎不转发 PointerMove**（那一笔已经归画布了），没人去把它改回来 →
        // `UpdatePeek` 一直以为"指针还在面板上" → 把藏好的露头重新拽出来。
        _hoverInside = QueryBounds().Contains(e.X, e.Y);
        _peekArmed = true;                 // 碰过了 → 之后允许"离开就收"
        _leftAtMs = _host.NowMs;
        _pressPos = p;
        // 拖动记的是**左上角**（和 RawAnchor 同一套语义：锚"带子的左端"）
        _dragStartAnchor = Anchor();

        // **按下也算"焦点在面板上"**：手写笔和触摸没有悬停那一段，
        // 只在 PointerMove 里更新 _railHover 的话，老师用笔点面板时设置条根本不会张开
        // （鼠标能张开、笔不能——这类"只在一种设备上坏"的 bug 最难查）。
        _railHover = BandVisible() && RailHoverZone().Contains(p.X, p.Y);

        if (_expand.Value < 0.5f)
        {
            if (!BallRect().Contains(p.X, p.Y)) return false;
            _press = -2;                    // 球
            return true;
        }

        // 抽屉优先。它长在面板外面（上方），物理上和主条不重叠，
        // 但顺序写清楚，省得以后挪位置时踩雷。
        int row = HitRow(p.X, p.Y);
        if (row >= 0) { _press = 1000 + row; return true; }
        int prof = HitProfile(p.X, p.Y);
        if (prof >= 0) { SetProfile((Profile)prof); return true; }
        int chip = HitChip(p.X, p.Y);
        if (chip >= 0) { TogglePin(chip); return true; }

        // **滑条要排在工具格前面**：它在面板最下沿，和工具格的矩形是重叠的。
        // 排在后面的话，按最下沿那一条会被当成"点了某个工具"（假面板里 groove 也是先判的）。
        // 动作按钮（清空/全选）排在最前面：它贴在上带最外沿，和谁都挨着。
        if (BandVisible() && RailOpen && CurAction != BandAction.None
            && ActionRect().Contains(p.X, p.Y))
        {
            if (CurAction == BandAction.Clear)
            {
                // 清空：**按住才算数**（0.8 秒），松手即取消。进度由 UpdateBandAction 每帧推进。
                _actionHoldFrom = _host.NowMs;
                _press = 2000;
            }
            else
            {
                _host.Commands.SelectAll();          // 全选：点一下就执行
                _actionFlashUntil = _host.NowMs + 260;
            }
            Invalidate();
            return true;
        }

        if (BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(p.X, p.Y))
        {
            _sliderDragging = true;
            DragSlider(p.X);
            return true;
        }

        int idx = HitCell(p.X, p.Y);
        if (idx >= 0)
        {
            _press = idx;
            return true;
        }

        // 上带：色片 / 分段（滑条已经在上面判过了）
        if (BandVisible())
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
        _hoverInside = QueryBounds().Contains(e.X, e.Y);
        if (_hoverInside) _peekArmed = true;   // 指针进过面板 → 之后允许"离开就收"
        _leftAtMs = _host.NowMs;
        var p = Local(e);
        _railHover = BandVisible() && RailHoverZone().Contains(p.X, p.Y);

        if (_sliderDragging)
        {
            DragSlider(p.X);
            return true;
        }

        if (_press != -1)
        {
            // **动作按钮和抽屉里的行不参与拖动**。
            //
            // 用户实测报的 bug："按住清空的时候，手一抖就把整个面板拖走了"——
            // 面板一走，按钮就不在指针下面了，看着就是"清空没反应"。
            // 只有点在**主条上**（球或者工具格）才算"抓住面板"。
            bool draggable = _press != 2000 && _press < 1000;
            if (draggable && !_dragging && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
                _dragging = true;
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

        int row = HitRow(p.X, p.Y);
        if (row != _drawerHover)
        {
            _drawerHover = row;
            Invalidate();
        }
        int prof = HitProfile(p.X, p.Y);
        if (prof != _profileHover) { _profileHover = prof; Invalidate(); }
        int chip = HitChip(p.X, p.Y);
        if (chip != _chipHover) { _chipHover = chip; Invalidate(); }

        int hover = _expand.Value < 0.5f
            ? (BallRect().Contains(p.X, p.Y) ? -2 : -1)
            : HoverAt(p.X, p.Y);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        return hover != -1 || row >= 0;
    }

    public void PointerLeave()
    {
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
        if (!BandVisible()) return -1;
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
        if (_sliderDragging)
        {
            _sliderDragging = false;
            return true;
        }
        int idx = _press;
        bool dragged = _dragging;
        _press = -1;
        _dragging = false;
        if (idx == -1) return false;

        if (dragged)
        {
            SnapNearEdge();
            return true;
        }

        // 按住清空：松手即取消（够 0.8 秒的那一次已经在 UpdateBandAction 里执行过了）
        if (idx == 2000)
        {
            _actionHoldFrom = double.NegativeInfinity;
            Invalidate();
            return true;
        }
        if (idx >= 1000) { ActivateRow(idx - 1000); return true; }   // 抽屉里的行
        if (idx == -2) { Toggle(); return true; }        // 点球：展开
        if (idx == 0) { Toggle(); return true; }         // 点带子最左那格：收起
        Activate(idx);
        return true;
    }

    /// <summary>松手时离最近的边够近就贴过去。</summary>
    private void SnapNearEdge()
    {
        var a = Anchor();
        float w = Width(), h = Height();
        float left = a.X - _screen.MinX;
        float right = _screen.MaxX - (a.X + w);
        float top = a.Y - _screen.MinY;
        float bottom = _screen.MaxY - (a.Y + h);

        float best = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        if (best > Tokens.SnapDistance) return;          // 不够近：不吸附（拖到哪儿就哪儿）

        var want = a;
        if (best == left) want.X = _screen.MinX + Tokens.DockGap;
        else if (best == right) want.X = _screen.MaxX - Tokens.DockGap - w;
        else if (best == top) want.Y = _screen.MinY + Tokens.DockGap;
        else want.Y = _screen.MaxY - Tokens.DockGap - h;

        _anchor = want;
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

        // **收起时必须把临时状态清干净**：抽屉、钉住的悬停、按下的格、在拖的滑条。
        // 不清的话，缩回一个球之后抽屉还挂在那儿（占用矩形也算着它），
        // 下次展开时那些状态还会自己冒出来。假面板当年就是栽在这条上：
        // "点更多→点收起→再展开，抽屉自己冒出来"。
        if (collapse)
        {
            _drawerOpen = false;
            _drawerHover = -1;
            _profileHover = -1;
            _chipHover = -1;
            _hover = -1;
            _press = -1;
            _dragging = false;
            _sliderDragging = false;
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
            case 2: cmd.SetBoard(!st.Board); break;
            case 3: cmd.SetTool(Tool.Pen); break;
            case 4: cmd.SetTool(Tool.Highlighter); break;
            case 5: cmd.SetTool(Tool.Laser); break;
            case 6:
                // 引擎里"整笔擦/面积擦"是**两个工具**，不是一个工具的两档；
                // 第一版就点一下换一次（真正的两档要等上带做出来）。
                cmd.SetTool(st.Tool == Tool.PixelEraser ? Tool.Eraser : Tool.PixelEraser);
                break;
            case 7: cmd.SetTool(Tool.Marquee); break;
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
            case 12:                                         // 「更多」：开合抽屉
                _drawerOpen = !_drawerOpen;
                _drawerHover = -1;
                // 抽屉和色带抢同一块地方：开抽屉就把色带收掉（假面板同一条）
                if (_drawerOpen) { _railHover = false; _rail.To(0f, Tokens.RailMs); }
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
        // 判据走 HasShapeEntry 而不是再列一遍七个名字：加一种图形只改 ShapeBandOrder。
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
        Invalidate();
    }

    // ---- 绘制 ---------------------------------------------------------------

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_host == null) return;
        UpdatePeek();                    // 每帧问一次"该不该收起来"（贴边隐藏）
        UpdateRail();                    // 色线该不该长成设置条
        UpdateBandAction();              // "按住清空"够 0.8 秒没有（每帧推进）
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
        if (_drawerOpen) DrawDrawer(ctx);
        // 粗细预览**最后画**：它可能伸到面板外面，压在上面的东西得过它一层
        DrawSizePreview(ctx, st);
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

        // 白板那一格右边显示"第 N 屏"——老师要有一点位置感（"我在第几屏"）
        if (_bandCell == 2)
        {
            var panel = UnionRect();
            var box = new RectF
            {
                MinX = SegmentRect(4, 5).MaxX + 10f, MinY = BandRect().MinY,
                MaxX = panel.MaxX - BarInset(), MaxY = BandRect().MaxY,
            };
            _widgets.Text(ctx, $"第 {st.ScreenIndex} 屏", box, 12.5f, Brush(ctx, InkCol), center: false);
        }
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

        float pr = Math.Clamp(2f + t * 6.5f, 2f, 8.5f);
        ctx.FillEllipse(new Ellipse(new Vector2(box.MaxX - SliderPreviewW * 0.5f - 2f, cy), pr, pr),
                        Brush(ctx, ink));
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

    private bool IsSwatchActive(in UiState st, int i)
    {
        var c = SwatchColor(i);
        var p = st.PaletteBase;
        return MathF.Abs(c.R - p.R) < 0.02f && MathF.Abs(c.G - p.G) < 0.02f
            && MathF.Abs(c.B - p.B) < 0.02f;
    }

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

    private void DrawSegment(ID2D1DeviceContext ctx, int i, int count, in UiState st)
    {
        var r = SegmentRect(i, count);
        bool active = IsSegmentActive(st, i);
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, 8f, 8f);

        // 白板那三格直接画板色（颜色本身就是内容，写字反而多余）
        if (_bandCell == 2)
        {
            // 两端是"上一屏 / 下一屏"：到顶了"上一屏"压暗（点不动，反馈在这里给）
            if (i == 0 || i == 4)
            {
                bool enabled = i == 4 || _host.State.CanFlipPageUp;
                var fg = enabled ? InkCol : new Color4(InkCol.R, InkCol.G, InkCol.B, 0.30f);
                if (_hover == 200 + i) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
                IconAtlas.DrawCentered(ctx, i == 0 ? "chevronUp" : "chevronDown", r, 16f, Brush(ctx, fg));
                return;
            }

            int bi = i - 1;                       // 1..3 → 白/绿/黑
            ctx.FillRoundedRectangle(rr, Brush(ctx, InkPalette.BoardPresets[bi].Color));
            ctx.DrawRoundedRectangle(rr, active ? Brush(ctx, Tokens.Accent) : Brush(ctx, BorderCol),
                                     active ? 2f : 1f);
            if (active)
            {
                var inner = new Vortice.RawRectF(r.MinX + 2, r.MinY + 2, r.MaxX - 2, r.MaxY - 2);
                ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 6f, 6f), Brush(ctx, Tokens.AccentInk), 1.5f);
            }
            return;
        }

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
        // **有档位的那两段**（点它一下换一档，见 ActivateSegment 的 case 8）：
        //   · 「直线」= 3 档线型（实 / 虚 / 点）；
        //   · 「棱柱 / 棱锥 / 棱台」= 4 档边数（三 / 四 / 五 / 六）。
        // 图标照旧画当前那一档，**右边再加一竖列档位点**——大而浓的那个是当前档。
        // 用户 2026-09-20 定：只换图标的话，老师"不知道这一格还能点"（可选的状态是隐形的）。
        //
        // ⚠ **三格各有各的档**（`st.SidesOf(tool)`）：第一版三格共用一个数，结果是
        // "点棱锥那一格，棱柱、棱台的点跟着一起动"——用户上手就报了这个。
        // 档位点画的是**这一格自己的档**，所以这里必须问 `SidesOf(segTool)`，
        // 而不是随手读一个 `st.PrismSides`。
        //
        // **点从"图标下面"挪到了"图标右边"**（还是 2026-09-20，用户看出来的）：
        // 图形段是"宽 × 26"的长方形（第二行 9 段时每格约 80 宽），而图标只占 18 ——
        // 左右各有约 30 的空白。横排放在下面的时候，为了挤出那 7 像素高，
        // 图标得**压到 16 并整体上移 3.5**；竖着放到右边之后那一列点只占约 7 宽，
        // 图标就能回到 18 并留在正中。只有这几格有档位，别的段照旧。
        //
        // **档数与当前档都从这一处算**（不在绘制里再列一遍工具名）：
        // `ShapeSpec.HasSideCount(tool)` 判"是不是那一族"，这里判"它有几档、现在是第几档"。
        // 档位范围**来自引擎**（`st.SolidMin/MaxSides`）——`Stroke` 是引擎内部类型，
        // 界面看不到它，也不该在这里写死一份 3/6。
        var segTool = ShapeToolAt(i);
        int pipCount = segTool == Tool.Line ? 3
            : ShapeSpec.HasSideCount(segTool) ? st.SolidMaxSides - st.SolidMinSides + 1
            : 0;
        if (pipCount > 0)
        {
            int pipCur = segTool == Tool.Line
                ? (int)st.LineDash
                : Math.Clamp(st.SidesOf(segTool), st.SolidMinSides, st.SolidMaxSides) - st.SolidMinSides;
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
    /// **图形种类 → 图标名**（带上状态的那一份）：目前两处跟状态有关——
    /// 抛物线要**转成当前开口方向**（见 <see cref="ParabolaIconName"/>）、
    /// 直线要**换成当前线型**（见 <see cref="LineIconName"/>），
    /// 棱柱 / 棱锥 / 棱台要**换成当前档的边数**（见 <see cref="SolidIconName"/>）。
    ///
    /// 为什么非跟状态不可：这几格"点第二下换一档"，图标不跟着换的话，
    /// 老师看不出那一下到底有没有生效（三处都是用户 2026-09-20 定的）。
    /// </summary>
    private string ShapeIcon(Tool t) => t switch
    {
        Tool.Parabola => ParabolaIconName(_host.State.ParabolaAxis),
        Tool.Line => LineIconName(_host.State.LineDash),
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
    /// "感觉不对，还是照搬他的逻辑"；InkClass 也是两个按钮 `case 20/21`）。
    /// </summary>
    private static string ParabolaIconName(CurveAxis axis) => axis switch
    {
        CurveAxis.OpenRight or CurveAxis.OpenLeft => "parabolaRight",
        _ => "parabola",                           // 上下抛物（也是兜底）
    };

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
        Tool.Sine => "sine",
        Tool.Cosine => "cosine",
        // 立体图形（2026-09-20 第五批）：同样自绘（见 IconAtlas.DrawCylinder / DrawCone 等）。
        Tool.Cylinder => "cylinder",
        Tool.Cone => "cone",
        // ⚠ 长方体 / 四面体 2026-09-20 第十二批**撤了面板入口**（见 ShapeBandOrder2 那段），
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
        9 => i switch { 0 => "直接截取", 1 => "隐藏界面", _ => "粘贴图片" },
        8 => "",                                   // 图形：画图标（见 ShapeIcon）
        _ => "",
    };

    private bool IsSegmentActive(in UiState st, int i) => _bandCell switch
    {
        2 => i >= 1 && i <= 3 && BoardColorIs(st, i - 1),
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

    /// <summary>
    /// 画「更多」抽屉：左边文字、右边开关；灰项（还没做的功能）压暗并且点不动。
    /// 危险动作（退出）用红字——不挨着常用动作放，这是设计里定过的规矩。
    /// </summary>
    private void DrawDrawer(ID2D1DeviceContext ctx)
    {
        var d = DrawerRect();
        DrawCard(ctx, d, Tokens.BandRadius);

        // 档位条：极简 / 自定义 / 完整
        for (int i = 0; i < 3; i++)
        {
            var r = ProfileRect(i);
            bool active = ProfileIndex() == i;
            var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);
            if (active) ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
            else if (_profileHover == i) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);
            _widgets.Text(ctx, ProfileName(i), r, 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
        }

        // 钉住那一栏：点一下切换钉住/取消（笔、橡皮、更多是安全项，点不动）
        for (int cell = 1; cell < Cells.Length; cell++)
        {
            var r = ChipRect(cell);
            bool pinned = _pinned[cell];
            var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);
            if (pinned)
            {
                ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 1f);
            }
            else
            {
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
            }
            var ink = pinned ? InkCol : new Color4(InkCol.R, InkCol.G, InkCol.B, 0.35f);
            if (cell == 5) IconAtlas.DrawLaser(ctx, r, 18f, Brush(ctx, ink));
            else IconAtlas.DrawCentered(ctx, Cells[cell].Icon, r, 18f, Brush(ctx, ink));
            if (!CanUnpin(cell))
            {
                // 安全项：右上角一个小点，意思是"这个取消不掉"
                ctx.FillEllipse(new Ellipse(new Vector2(r.MaxX - 5f, r.MinY + 5f), 2f, 2f),
                                Brush(ctx, new Color4(InkCol.R, InkCol.G, InkCol.B, 0.45f)));
            }
        }

        for (int i = 0; i < Rows.Length; i++)
        {
            var r = RowRect(i);
            bool gray = IsGrayRow(i);
            bool hover = !gray && _drawerHover == i;

            if (hover)
            {
                var hb = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
                ctx.FillRoundedRectangle(new RoundedRectangle(hb, 8f, 8f), Brush(ctx, HoverCol));
            }

            Color4 ink = gray ? new Color4(InkCol.R, InkCol.G, InkCol.B, 0.35f)
                       : Rows[i].Dangerous ? new Color4(0.85f, 0.22f, 0.22f, 1f)
                       : InkCol;
            var label = new RectF
            {
                MinX = r.MinX + 4, MinY = r.MinY,
                MaxX = r.MaxX - (IsToggleRow(i) ? 48f : 4f), MaxY = r.MaxY,
            };
            _widgets.Text(ctx, RowLabel(i), label, 13f, Brush(ctx, ink), center: false);

            if (IsToggleRow(i)) DrawSwitch(ctx, SwitchRect(i), IsOn(i));

            if (IsSeparatorAfter(i))
            {
                float y = r.MaxY + DrawerSepH * 0.5f;
                ctx.DrawLine(new Vector2(d.MinX + DrawerPad, y),
                             new Vector2(d.MaxX - DrawerPad, y), Brush(ctx, BorderCol), 1f);
            }
        }
    }

    private bool IsOn(int i) => Rows[i].Kind switch
    {
        Row.DarkTheme => _dark,
        // 坐标系网格这一行显示的是**"以后新画的那些"要不要格**——
        // 已经画在板上的坐标系各存各的（见 Stroke.Grid），
        // 所以选中一个坐标系再点这一下时，改的是它，这个开关的位置不动。
        Row.CoordGrid => _host != null && _host.State.CoordGridDefault,
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

        // **图形那一格（8）的图标跟着当前种类走**（2026-09-19）：
        // 七种图形挤在一格里之后，"shapes" 那个"两个图形叠在一起"的通用图标
        // 什么也没说——手里是三角形还是平行四边形，只能打开上带才知道。
        // 现在画的就是当前那一种（和上带里高亮的那一段同一张图，共用 ShapeIconFor）。
        //
        // 代价（明确接受）：七种图形**都没有 filled 变体**（Fluent 表里没有生成），
        // 所以选中态不再像别的格那样变实心，而是"同一个轮廓 + 强调色底 + 白图标"
        // ——和上带里选中的那一段是同一种画法。
        var icon = i == 8 ? ShapeIcon(st.Tool) : active ? Cells[i].Filled : Cells[i].Icon;
        var ink = active ? Tokens.AccentInk : InkCol;
        // **撤销/重做栈空 → 压暗**（用户 2026-09-17："撤销重做灰度"）。
        // 引擎早就把 UndoDepth / RedoDepth 递给界面了，只是界面一直没用。
        // 压暗而不是藏起来：位置固定、老师不用去找；点它也没事（引擎那边是空操作）。
        if (CellUnavailable(i, st)) ink = new Color4(ink.R, ink.G, ink.B, 0.30f);
        // 激光笔是**自绘**的（笔＋光束＋落点）：Fluent 里没有这个专名，
        // 用闪电之类的近义图标，老师看不出这是激光笔（假面板比过九个候选，选的是这个）。
        if (PerfSkipIcons) return;
        // 激光笔：**未选中用线条版、选中用实心版**（和 Fluent 那批 regular／filled
        // 同一套规矩——混着的表现就是"一排里只有它是实心的，像被填了色"）。
        if (i == 5)
            IconAtlas.DrawLaser(ctx, r, Tokens.Icon, Brush(ctx, ink), null,
                                active ? IconAtlas.LaserDefault : IconAtlas.LaserOutline);
        // 两种橡皮也是**自绘**的，而且**图标跟着当前是哪种橡皮变**：
        // 整笔擦＝橡皮压着一条线；面积擦＝竖着的黄金比例矩形＋十字（和落点光标同形）。
        // 一个按钮管两个工具，图标不跟着变的话，"现在到底在擦整条还是擦一块"只能看文字。
        else if (i == 6)
            IconAtlas.DrawEraser(ctx, r, Tokens.Icon, Brush(ctx, ink),
                                 area: st.Tool == Tool.PixelEraser, outline: !active);
        else IconAtlas.DrawCentered(ctx, icon, r, Tokens.Icon, Brush(ctx, ink));
    }

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

    // ---- 自检钩子（开发期用；产品代码不碰）--------------------------------

    /// <summary>
    /// 自检用：某一格（按**完整档的下标**）的逻辑矩形。
    /// 注意参数是"格子的编号"不是"第几个"——档位一换，显示的格子数就变了，
    /// 按序号取会跑到界外（自检第一版就是这么点空了整整一条用例）。
    /// </summary>
    internal RectF CellRectForTest(int cell) => CellRect(PosOf(cell));

    /// <summary>自检用：上带这一刻的矩形（没长出来就是空）。</summary>
    internal RectF BandRectForTest => BandVisible() ? BandRect() : RectF.Empty;

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
    /// 自检用：某一格现在画的是哪个图标名。
    /// 图形那一格（8）的图标**跟着当前种类变**，所以它得问一次状态；
    /// 其余的格子图标是写死在 Cells 表里的，直接给。
    /// </summary>
    internal string CellIconForTest(int cell)
        => cell == 8 ? ShapeIcon(_host.State.Tool) : Cells[cell].Icon;

    /// <summary>
    /// 自检用：图形那一格**第 i 段**的图标名。
    /// 用来验"抛物线那一段的图标跟着当前开口方向转"（见 <see cref="ParabolaIconName"/>）——
    /// 图标不转的话，老师看不出"再点一次"到底有没有生效。
    /// </summary>
    internal string ShapeIconNameForTest(int i) => ShapeIcon(i);

    /// <summary>自检用：这个工具在图形面板里有没有入口（见 <see cref="HasShapeEntry"/>）。</summary>
    internal static bool HasShapeEntryForTest(Tool t) => HasShapeEntry(t);

    /// <summary>
    /// 自检用：这个工具在图形那一格里是**第几段**（两行拉平编号；没入口就 −1）。
    ///
    /// **为什么要有它**：自检里写死段号会随"加一种图形 / 撤一种图形"**静默失效**
    /// ——这次加棱锥 / 棱台时第二行往下挪了两格，写死的 16 就不声不响点了别的段。
    /// 按工具名问一句就跟着表走，挪多少次都不怕（仓库教训里那一条）。
    /// </summary>
    internal static int ShapeSegmentIndexForTest(Tool t)
    {
        int i = Array.IndexOf(ShapeBandOrder, t);
        if (i >= 0) return i;
        int j = Array.IndexOf(ShapeBandOrder2, t);
        return j >= 0 ? ShapeBandOrder.Length + j : -1;
    }

    /// <summary>自检用：滑条的矩形。</summary>
    internal RectF SliderRectForTest => SliderRect();

    /// <summary>
    /// 自检用：滑条轨道的左右端（逻辑坐标）。自检要拖到**两端**去验
    /// "这个工具的粗细真的走到了范围的端点"，所以必须拿到和绘制同一份的两个端点。
    /// </summary>
    internal (float Left, float Right) SliderTrackRangeForTest => SliderTrackRange();

    /// <summary>自检用：抽屉开着没有 / 它的矩形 / 第 i 行的矩形。</summary>
    internal bool DrawerOpenForTest => _drawerOpen;
    internal RectF DrawerRectForTest => DrawerRect();
    internal RectF RowRectForTest(int i) => RowRect(i);

    /// <summary>
    /// 自检用：**按行标签**找那一行的矩形（找不到返回空矩形）。
    ///
    /// 为什么不让自检写行下标：抽屉里的行是会被插来插去的——2026-09-17 插了底纹两行、
    /// 2026-09-19 把灰着的「学科工具」换成了坐标系 / 数轴 / 坐标系网格三行。
    /// 每插一次，写死下标的自检就要去改一处引用，而且**改错了是静默的**：
    /// 点到了别的行，红色的却是那一条断言（"点重启没反应"）。
    /// 按标签找，以后插行就不会再碰到自检。
    /// </summary>
    internal RectF RowRectByLabelForTest(string labelPart)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Label.Contains(labelPart, StringComparison.Ordinal)) return RowRect(i);
        return RectF.Empty;
    }

    /// <summary>自检用：深色主题与贴边隐藏的开关状态。</summary>
    internal bool DarkForTest => _dark;
    internal bool HideEnabledForTest => _hideEnabled;

    /// <summary>自检/出图用：把抽屉打开（产品里只能点「更多」那一格开）。</summary>
    internal void OpenDrawerForTest() => _drawerOpen = true;

    /// <summary>自检/出图用：直接切到某一档（产品里在抽屉顶部点）。</summary>
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

    /// <summary>自检用：抽屉里某一行现在压暗没有（底纹两行在"白板没开"时要压暗）。</summary>
    internal bool RowGrayForTest(int row) => IsGrayRow(row);

    /// <summary>自检用：抽屉里某一行现在显示的字（底纹两行会把当前档位写出来）。</summary>
    internal string RowLabelForTest(int row) => RowLabel(row);

    /// <summary>自检用：这一刻色片有几个（极简档应该是 4）。</summary>
    internal int SwatchCountForTest => SwatchCount;

    /// <summary>自检用："允许自动收起"的开关（启动时应该是 false）。</summary>
    internal bool PeekArmedForTest => _peekArmed;

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

    /// <summary>自检用：档位条第 i 段、钉住栏第 cell 格的矩形。</summary>
    internal RectF ProfileRectForTest(int i) => ProfileRect(i);
    internal RectF ChipRectForTest(int cell) => ChipRect(cell);

    /// <summary>自检用：现在算"展开"吗。</summary>
    internal bool ExpandedForTest => Expanded;

    /// <summary>自检用：把展开动画一步到位（不等 200 ms）。</summary>
    internal void SnapForTest() => _expand.Jump(_expand.Value > 0.5f ? 0f : 1f);

    /// <summary>自检用：界面看到的屏幕（核对它和 IUiHost.Screen 是不是同一个）。</summary>
    internal RectF ScreenForTest => _screen;

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
