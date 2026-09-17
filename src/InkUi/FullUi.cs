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

    /// <summary>「更多」抽屉里的行。</summary>
    private enum Row { DarkTheme, AutoHide, Restart, Quit, CheckUpdate, SubjectTools }

    private static readonly (Row Kind, string Label, bool Dangerous, bool Gray)[] Rows =
    {
        (Row.DarkTheme, "深色主题", false, false),
        (Row.AutoHide, "贴边隐藏", false, false),
        (Row.Restart, "重启软件", false, false),
        (Row.Quit, "退出", true, false),
        (Row.CheckUpdate, "检查更新", false, true),
        (Row.SubjectTools, "学科工具", false, true),
    };

    /// <summary>第 1、3 行是分隔线（画的时候跳过的位置）。</summary>
    private static bool IsSeparatorAfter(int row) => row == 1 || row == 3;

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
        _lastTool = host.State.Tool;
        _expand.Jump(0f);
        _peek.Jump(1f);
        _rail.Jump(0f);
        LoadPrefs();
        Layout(host.Screen, host.DpiScale);
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
    }

    private void SavePrefs()
    {
        _host.SetPref("dark", _dark ? "1" : null);
        _host.SetPref("hide", _hideEnabled ? "1" : null);
        _host.SetPref("profile", _profile == Profile.Full ? null
                               : _profile == Profile.Mini ? "mini" : "custom");

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
        return u;
    }

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
        Tokens.BandLine + (Tokens.BandHeight - Tokens.BandLine) * _rail.Value;

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
    // 选择 = 矩形/套索；白板 = 三种板色；图形 = 四种图形。
    // 没有设置项的（后撤/重做/更多/截屏）就不长上带——不做一排空按钮。

    private float BandCenterY() => (BandRect().MinY + BandRect().MaxY) * 0.5f;
    private float BandContentLeft() => BandRect().MinX + BarInset();

    private RectF SwatchRect(int i)
    {
        // **色片按可用宽度平分，铺满整条**（照假面板：cw = (avail - gap*(n-1)) / n）。
        // 早先我写的是固定 26 宽 ＋ 6 缝，结果右边空出一大块，跟假面板一比就露馅了。
        var band = BandRect();
        int n = Tokens.Palette.Length;
        float gap = 4f;
        float avail = band.MaxX - band.MinX - BarInset() * 2f
                    - (BandHasSlider ? SliderTrackW + 14f : 0f);   // 给右端的粗细滑条让位
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
        float right = band.MaxX - BarInset();
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
        // 白板那一格：5 段固定宽（70），右边留出来给"第 N 屏"
        if (_bandCell == 2)
        {
            float bw = 70f;
            float bx = BandContentLeft() + i * (bw + 6f);
            float by = BandCenterY() - Tokens.SegmentHeight * 0.5f;
            return new RectF { MinX = bx, MinY = by, MaxX = bx + bw, MaxY = by + Tokens.SegmentHeight };
        }
        // 和色片一样：右端有滑条的时候要给滑条让位，否则分段会和滑条叠在一起
        float total = band.MaxX - band.MinX - BarInset() * 2
                    - (BandHasSlider ? SliderTrackW + 14f : 0f);
        float w = Math.Min(120f, (total - (count - 1) * 6f) / count);
        float x = BandContentLeft() + i * (w + 6f);
        float y = BandCenterY() - Tokens.SegmentHeight * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + Tokens.SegmentHeight };
    }

    private bool BandHasSwatches => _bandCell is 3 or 4;
    private bool BandHasSlider => _bandCell is 3 or 4 or 5 or 6;
    /// <summary>
    /// 上带里有几段。白板那一格是 5 段：**[上一屏] [白][绿][黑] [下一屏]**
    /// ——翻屏和板色是同一类事（都属于"这块板怎么摆"），放一行最顺手。
    /// </summary>
    private int BandSegmentCount => _bandCell switch { 2 => 5, 6 => 2, 7 => 2, 8 => 4, _ => 0 };

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
        var (min, max) = WidthRange(st.Tool);
        if (max <= min) return 0f;
        return Math.Clamp((st.Width - min) / (max - min), 0f, 1f);
    }

    /// <summary>拖滑条：只在**值真的变了**的时候提交（每帧几十次 SetWidth 会连带动画与重画）。</summary>
    private void DragSlider(float x)
    {
        var (left, right) = SliderTrackRange();
        float t = right <= left ? 0f : Math.Clamp((x - left) / (right - left), 0f, 1f);

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
    private static (float W, float H) TrueSize(in UiState st) => st.Tool switch
    {
        Tool.Eraser => (st.Width * 2f, st.Width * 2f),      // 引擎给的是半径 → 画出来是直径
        Tool.PixelEraser => (st.Width, st.Width * 1.618f),  // 黄金比例矩形（和落点一模一样）
        _ => (st.Width, st.Width),                          // 笔 / 荧光笔 / 激光 = 线宽
    };

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
    private static string SizeLabel(in UiState st) => st.Tool switch
    {
        Tool.PixelEraser => $"{st.Width:F0}×{st.Width * 1.618f:F0}",
        Tool.Eraser => $"{st.Width * 2f:F0}",
        _ => $"{st.Width:F0}",
    };

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

            case Tool.Highlighter:                  // 荧光笔是"涂一大条"：画一根那么粗的短条
            {
                float len = MathF.Max(40f, w * 1.4f);
                var r = new Vortice.RawRectF(c.X - len * 0.5f, c.Y - h * 0.5f,
                                             c.X + len * 0.5f, c.Y + h * 0.5f);
                ctx.FillRoundedRectangle(new RoundedRectangle(r, h * 0.5f, h * 0.5f),
                                         Brush(ctx, st.HighlighterColor));
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
        for (int i = 0; i < Tokens.Palette.Length; i++)
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

    private void ActivateSwatch(int i) => _host.Commands.SetColor(Tokens.Palette[i].Color);

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
            case 8:                       // 四种图形（三角与平行四边形底层还没做）
                _host.Commands.SetTool(i switch
                {
                    0 => Tool.Line, 1 => Tool.Rectangle, 2 => Tool.Ellipse, _ => Tool.Arrow,
                });
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

    private bool IsToggleRow(int i) => Rows[i].Kind is Row.DarkTheme or Row.AutoHide;
    private bool IsGrayRow(int i) => Rows[i].Gray;

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
                Invalidate();
                break;
            case Row.AutoHide:
                _hideEnabled = !_hideEnabled;
                _peek.Jump(1f);          // 刚打开时先给个完整的，别一开就缩起来
                SavePrefs();
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

        bool keepOpen = _hoverInside || _press != -1 || _sliderDragging || _drawerOpen
                     || _host.State.IsDrawing;
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
        _hoverInside = true;
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
            if (!_dragging && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
                _dragging = true;
            if (_dragging)
            {
                _anchor = _dragStartAnchor + (p - _pressPos);
                Invalidate();               // 位置一变就要重画；引擎那边每帧都会加进脏区
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
        if (BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(x, y)) return 300;
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
                cmd.SetTool(st.Tool == Tool.Rectangle ? Tool.Line : Tool.Rectangle);
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
    private static bool HasBand(int cell) => cell is 2 or 3 or 4 or 5 or 6 or 7 or 8;

    /// <summary>当前工具对应的格子——键盘换工具时用它把上带掰回来。</summary>
    private static int CellForTool(Tool t) => t switch
    {
        Tool.Highlighter => 4,
        Tool.Laser => 5,
        Tool.Eraser or Tool.PixelEraser => 6,
        Tool.Marquee => 7,
        Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Arrow => 8,
        Tool.Capture => 9,
        _ => 3,
    };

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
        DrawCard(ctx, panel, radius);

        var st = _host.State;
        if (e < 0.5f)
        {
            // 收起态：一个球，圆内那圈颜色 = 当前笔色（不用点开就知道手里是哪支笔）
            var c = new Vector2((bar.MinX + bar.MaxX) * 0.5f, (bar.MinY + bar.MaxY) * 0.5f);
            // 数字照抄假面板：圆内那圈半径 = 0.76 × 球的半径、线宽 2.5，
            // 中间那个笔图标 = 0.7 × 球的半径（比工具格里的图标小一圈，
            // 不然一个 48 的球里塞一个 24 的图标会顶到边上）。
            float half = (bar.MaxX - bar.MinX) * 0.5f;
            var ring = new Ellipse(c, half * 0.76f, half * 0.76f);
            ctx.DrawEllipse(ring, Brush(ctx, st.PaletteBase), 2.5f);
            IconAtlas.DrawCentered(ctx, "pen", bar, half * 1.4f, Brush(ctx, InkCol));
            return;
        }

        // 色带那条**凹槽**：把色带那一块裁出来、填一层淡淡的暗色（照假面板：
        // 裁进面板的圆角形状，顶部两个角自然跟着圆）。
        var band = BandRect();
        if (!PerfSkipChrome && band.MaxY - band.MinY >= 2f)
        {
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(band.MinX, band.MinY, band.MaxX, band.MaxY),
                                    AntialiasMode.Aliased);
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY), radius, radius),
                Brush(ctx, _dark ? Tokens.TroughDark : Tokens.TroughLight));
            ctx.PopAxisAlignedClip();
        }

        // 展开态：球缩进最左一格，右边是这一档的工具格（极简档就只有六格）。
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++) DrawCell(ctx, k, st);

        if (BandVisible()) DrawBand(ctx, st);

        // 面板顶部一道极淡的内高光（假面板原话："Windows 11 的层次感靠它"）
        if (!PerfSkipChrome && band.MaxY - band.MinY > 20f)
        {
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY),
                                    AntialiasMode.Aliased);
            ctx.FillRectangle(
                new Vortice.RawRectF(panel.MinX + 1, panel.MinY + 0.5f, panel.MaxX - 1, panel.MinY + 1.5f),
                Brush(ctx, _dark ? Tokens.TopSheenDark : Tokens.TopSheenLight));
            ctx.PopAxisAlignedClip();
        }

        // 滑条：面板**最下沿那一条**（照假面板：主条下方本来就留了 8 像素余量）
        if (e > 0.55f) DrawGroove(ctx, st);
        if (_drawerOpen) DrawDrawer(ctx);
        // 粗细预览**最后画**：它可能伸到面板外面，压在上面的东西得过它一层
        DrawSizePreview(ctx, st);
    }

    // ---- 颜色（深色主题只是一整套换过来，形状一个都不动）------------------

    private Color4 PanelFill => _dark ? Tokens.PanelDark : Tokens.PanelLight;
    private Color4 BorderCol => _dark ? Tokens.BorderDark : Tokens.BorderLight;
    private Color4 InkCol => _dark ? Tokens.InkDark : Tokens.InkLight;
    private Color4 HoverCol => _dark ? Tokens.HoverDark : Tokens.HoverLight;

    /// <summary>一张"卡片"：两层投影 ＋ 底 ＋ 1px 描边（没有这道边，圆角会糊进背景里）。</summary>
    private void DrawCard(ID2D1DeviceContext ctx, RectF r, float radius)
    {
        if (!PerfSkipShadow)
        {
            var sh1 = new Vortice.RawRectF(r.MinX, r.MinY + 1, r.MaxX, r.MaxY + 1);
            ctx.FillRoundedRectangle(new RoundedRectangle(sh1, radius, radius), Brush(ctx, Tokens.Shadow1));
            var sh2 = new Vortice.RawRectF(r.MinX, r.MinY + 3, r.MaxX, r.MaxY + 3);
            ctx.FillRoundedRectangle(new RoundedRectangle(sh2, radius, radius), Brush(ctx, Tokens.Shadow2));
        }

        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, PanelFill));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
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
            for (int i = 0; i < Tokens.Palette.Length; i++)
            {
                var r = SwatchRect(i);
                var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
                var rr = new RoundedRectangle(box, 7f, 7f);
                ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Palette[i].Color));
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

        int n = BandSegmentCount;
        for (int i = 0; i < n; i++) DrawSegment(ctx, i, n, st);

        if (BandHasSlider) DrawBandSlider(ctx, st);

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
    /// 面板**最下沿那一条**滑条（照假面板的 DrawGroove）：底轨 ＋ 用当前笔色画的进度
    /// ＋ 右端一个跟着变大的笔尖预览。拖动时才浮出白色滑钮。
    ///
    /// 没有滑条的工具（鼠标/选择/图形…）在这里画一条"踢脚线"——
    /// 笔色 25% 的 2 像素线。**它的作用是让面板高度不忽高忽低**，
    /// 顺带让每个工具的下沿都有点东西，不至于是空的。
    /// </summary>
    private void DrawGroove(ID2D1DeviceContext ctx, in UiState st)
    {
        var panel = UnionRect();
        // 2026-09-17：粗细滑条**搬进设置条**里了（见 SliderRect 的注释），
        // 所以面板下沿不再需要"有滑条就画滑条、没滑条画踢脚线"这条分支——
        // 现在**每格都画同一条踢脚线**：笔色 25% 的 2 像素细线。
        // 它的作用只剩一个，但很实在：**让面板下沿永远有东西**，看着是块完整的板子。
        float inset = BarInset();
        float cy = panel.MaxY - 6f;
        float left = panel.MinX + inset;
        float w = panel.MaxX - inset - left;
        var ink = st.PaletteBase;
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(left, cy - 1f, left + w, cy + 1f), 1f, 1f),
            Brush(ctx, new Color4(ink.R, ink.G, ink.B, 0.25f)));
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

    private bool IsSwatchActive(in UiState st, int i)
    {
        var c = Tokens.Palette[i].Color;
        var p = st.PaletteBase;
        return MathF.Abs(c.R - p.R) < 0.02f && MathF.Abs(c.G - p.G) < 0.02f
            && MathF.Abs(c.B - p.B) < 0.02f;
    }

    /// <summary>当前那一档在下标几（色线里靠它标出"你现在在哪一段"）。</summary>
    private int ActiveSegmentIndex(in UiState st)
    {
        switch (_bandCell)
        {
            case 2:
                for (int i = 0; i < InkPalette.BoardPresets.Length; i++)
                    if (BoardColorIs(st, i)) return i;
                return -1;
            case 6:
                return st.Tool == Tool.Eraser ? 0 : st.Tool == Tool.PixelEraser ? 1 : -1;
            case 7:
                return st.SelectMode == SelectMode.Rect ? 0 : 1;
            case 8:
                return st.Tool switch
                {
                    Tool.Line => 0, Tool.Rectangle => 1, Tool.Ellipse => 2, Tool.Arrow => 3, _ => -1,
                };
            default:
                return -1;
        }
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
            _widgets.Text(ctx, label, r, (r.MaxX - r.MinX) < 62f ? 11f : 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
        else
            IconAtlas.DrawCentered(ctx, ShapeIcon(i), r, 18f,
                                   Brush(ctx, active ? Tokens.AccentInk : InkCol));
    }

    /// <summary>
    /// 图形那一排**用图标不用文字**（照假面板）：四种图形的轮廓比"直线/矩形"四个字
    /// 一眼得多，而且不用为四个字去量宽度。
    /// </summary>
    private static string ShapeIcon(int i) => i switch
    {
        0 => "lineWeight", 1 => "square", 2 => "circle", _ => "arrowRight",
    };

    private string SegmentLabel(int i) => _bandCell switch
    {
        6 => i == 0 ? "整笔擦" : "面积擦",
        7 => i == 0 ? "矩形" : "套索",
        8 => "",                                   // 图形：画图标（见 ShapeIcon）
        _ => "",
    };

    private bool IsSegmentActive(in UiState st, int i) => _bandCell switch
    {
        2 => i >= 1 && i <= 3 && BoardColorIs(st, i - 1),
        6 => i == 0 ? st.Tool == Tool.Eraser : st.Tool == Tool.PixelEraser,
        7 => i == 0 ? st.SelectMode == SelectMode.Rect : st.SelectMode == SelectMode.Lasso,
        8 => st.Tool == (i switch
             {
                 0 => Tool.Line, 1 => Tool.Rectangle, 2 => Tool.Ellipse, _ => Tool.Arrow,
             }),
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
            _widgets.Text(ctx, Rows[i].Label, label, 13f, Brush(ctx, ink), center: false);

            if (IsToggleRow(i)) DrawSwitch(ctx, SwitchRect(i), IsOn(i));

            if (IsSeparatorAfter(i))
            {
                float y = r.MaxY + DrawerSepH * 0.5f;
                ctx.DrawLine(new Vector2(d.MinX + DrawerPad, y),
                             new Vector2(d.MaxX - DrawerPad, y), Brush(ctx, BorderCol), 1f);
            }
        }
    }

    private bool IsOn(int i) => Rows[i].Kind == Row.DarkTheme ? _dark : _hideEnabled;

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
            else if (hover)
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

        var icon = active ? Cells[i].Filled : Cells[i].Icon;
        var ink = active ? Tokens.AccentInk : InkCol;
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
            8 => st.Tool is Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Arrow,
            9 => st.Tool == Tool.Capture,
            _ => false,
        };
    }

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

    /// <summary>自检用：上带里第 i 个分段的矩形。</summary>
    internal RectF SegmentRectForTest(int i) => SegmentRect(i, BandSegmentCount);

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
    internal bool ExpandedForTest => _expand.Value > 0.5f;

    /// <summary>自检用：把展开动画一步到位（不等 200 ms）。</summary>
    internal void SnapForTest() => _expand.Jump(_expand.Value > 0.5f ? 0f : 1f);

    /// <summary>自检用：界面看到的屏幕（核对它和 IUiHost.Screen 是不是同一个）。</summary>
    internal RectF ScreenForTest => _screen;

    /// <summary>自检用：贴边隐藏的进度（1 = 完全显示，0 = 只剩露头）。</summary>
    internal float PeekForTest => _peek.Value;

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
