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

    private IUiHost _host;
    private Widgets _widgets;
    private RectF _screen;                 // 逻辑虚拟桌面（Layout 给的）
    // 拖过之后的位置（左上角）；**null = 还没拖过**，用默认位置（下边居中）。
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

    /// <summary>深色主题：手动开关（用户定的），底色/图标/描边整套换。</summary>
    private bool _dark;

    /// <summary>贴边隐藏：默认关（用户定的）。开了以后贴边时只露 8 像素的头。</summary>
    private bool _hideEnabled;
    private readonly Anim _peek;           // 0 = 只剩露头，1 = 完全显示
    private readonly Anim _rail;           // 0 = 平时那条 6 像素色线，1 = 完整设置条
    private bool _railHover;
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
    private const float DrawerGap = 8f;
    private const float DrawerSepH = 9f;

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

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
            if (_hideEnabled && !_hoverInside && _peek.Value > 0f) return true;
            return _expand.Running;
        }
    }

    public void Attach(IUiHost host)
    {
        _host = host;
        _widgets = new Widgets(host);
        _expand.Bind(host);        // 时钟必须接真的那个（见 Anim.Bind 的注释）
        _peek.Bind(host);
        _rail.Bind(host);
        _lastTool = host.State.Tool;
        _expand.Jump(0f);
        _peek.Jump(1f);
        _rail.Jump(0f);
        Layout(host.Screen, host.DpiScale);
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

    private const float BandGap = 4f;
    private float BandProgress() => _expand.Value;

    /// <summary>上带这一刻的高度：平时 6 像素的色线，碰到了长成 34 像素的设置条。</summary>
    private float BandHeightFull() =>
        Tokens.BandLine + (Tokens.BandHeight - Tokens.BandLine) * _rail.Value;

    /// <summary>色线 / 设置条：数值够大了才按"设置条"那套画与命中（中间态归短的这边）。</summary>
    private bool RailOpen => _rail.Value >= 0.5f;

    /// <summary>上带这一刻是不是真的画出来了（长出来之前不参与命中）。</summary>
    private bool BandVisible() => BandProgress() > 0.6f;

    /// <summary>带子完全展开时的宽。算出来的，不是写死的（改格数/间距不用手改数字）。</summary>
    private static float ExpandedWidth()
    {
        float w = Tokens.BarPad * 2f + Cells.Length * Tokens.Button;
        int gaps = Cells.Length - 1;
        w += (gaps - GroupEnds.Length + 1) * Tokens.GapInGroup;
        w += (GroupEnds.Length - 1) * Tokens.GroupDivider;
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
    /// 没拖过时的位置：**主条的底边贴屏幕下边**（离边 12）。
    /// 注意这里减的是**主条高**、不是总高——带子朝上长，主条自己不动。
    /// </summary>
    private Vector2 RawAnchor() => _anchor ?? new Vector2(
        _screen.MinX + (_screen.MaxX - _screen.MinX - Width()) * 0.5f,
        _screen.MaxY - Tokens.EdgeMargin - Tokens.BarHeight);

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

    /// <summary>第 i 格的矩形（逻辑坐标）。收起格也在里面（i = 0）。</summary>
    private RectF CellRect(int i)
    {
        var a = Anchor();
        float x = a.X + Tokens.BarPad;
        for (int k = 0; k <= i; k++)
        {
            if (k == i)
                return new RectF { MinX = x, MinY = a.Y, MaxX = x + Tokens.Button, MaxY = a.Y + Height() };
            x += Tokens.Button;
            x += Array.IndexOf(GroupEnds, k) >= 0 ? Tokens.GroupDivider : Tokens.GapInGroup;
        }
        return RectF.Empty;
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
    private float BandContentLeft() => BandRect().MinX + Tokens.BarPad;

    private RectF SwatchRect(int i)
    {
        float x = BandContentLeft() + i * (Tokens.Swatch + Tokens.SwatchGap);
        float y = BandCenterY() - Tokens.Swatch * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + Tokens.Swatch, MaxY = y + Tokens.Swatch };
    }

    private RectF SliderRect()
    {
        var band = BandRect();
        float y = BandCenterY() - (Tokens.SliderKnob + 8f) * 0.5f;
        return new RectF
        {
            MinX = band.MaxX - Tokens.BarPad - Tokens.SliderWidth,
            MinY = y, MaxX = band.MaxX - Tokens.BarPad, MaxY = y + Tokens.SliderKnob + 8f,
        };
    }

    private RectF SegmentRect(int i, int count)
    {
        var band = BandRect();
        float total = band.MaxX - band.MinX - Tokens.BarPad * 2;
        float w = Math.Min(120f, (total - (count - 1) * 6f) / count);
        float x = BandContentLeft() + i * (w + 6f);
        float y = BandCenterY() - Tokens.SegmentHeight * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + Tokens.SegmentHeight };
    }

    private bool BandHasSwatches => _bandCell is 3 or 4;
    private bool BandHasSlider => _bandCell is 3 or 4 or 5 or 6;
    private int BandSegmentCount => _bandCell switch { 2 => 3, 6 => 2, 7 => 2, 8 => 4, _ => 0 };

    /// <summary>这个工具的粗细范围。**界面管范围，引擎管钳位**——引擎那边是 0.5～64。</summary>
    private (float Min, float Max) WidthRange(Tool tool) => tool switch
    {
        Tool.Highlighter => (16f, 64f),
        Tool.Laser => (4f, 24f),
        Tool.Eraser or Tool.PixelEraser => (8f, 64f),
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
        var box = SliderRect();
        float left = box.MinX + Tokens.SliderKnob * 0.5f;
        float right = box.MaxX - Tokens.SliderKnob * 0.5f;
        float t = right <= left ? 0f : Math.Clamp((x - left) / (right - left), 0f, 1f);

        var (min, max) = WidthRange(_host.State.Tool);
        float want = min + (max - min) * t;
        if (MathF.Abs(want - _host.State.Width) < 0.5f) return;   // 没变就不提交
        _host.Commands.SetWidth(want);
    }

    private int HitSwatch(float x, float y)
    {
        if (!BandHasSwatches) return -1;
        // 色线状态（还没张开）：整条线按 12 等分，**点哪一段就是哪个色**
        // ——用户明确说喜欢"不用先展开再点"这一条。
        if (!RailOpen)
        {
            var line = BandRect();
            float left = line.MinX + Tokens.BarPad, right = line.MaxX - Tokens.BarPad;
            if (x < left || x > right) return -1;
            if (y < line.MinY - Tokens.RailHoverPad || y > line.MaxY + Tokens.RailHoverPad) return -1;
            int n = Tokens.Palette.Length;
            return Math.Clamp((int)((x - left) / MathF.Max(1f, (right - left) / n)), 0, n - 1);
        }
        for (int i = 0; i < Tokens.Palette.Length; i++)
            if (SwatchRect(i).Contains(x, y)) return i;
        return -1;
    }

    private int HitSegment(float x, float y)
    {
        int n = BandSegmentCount;
        if (n > 0 && !RailOpen)
        {
            var line = BandRect();
            float left = line.MinX + Tokens.BarPad, right = line.MaxX - Tokens.BarPad;
            if (x < left || x > right) return -1;
            if (y < line.MinY - Tokens.RailHoverPad || y > line.MaxY + Tokens.RailHoverPad) return -1;
            return Math.Clamp((int)((x - left) / MathF.Max(1f, (right - left) / n)), 0, n - 1);
        }
        for (int i = 0; i < n; i++)
            if (SegmentRect(i, n).Contains(x, y)) return i;
        return -1;
    }

    /// <summary>色线 / 设置条该不该张开。判据：指针碰到它、或者正在拖滑条。</summary>
    private void UpdateRail()
    {
        if (!BandVisible()) { _rail.To(0f, 0); _railHover = false; return; }
        _rail.To(_railHover || _sliderDragging ? 1f : 0f, Tokens.RailMs);
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

    private void ActivateSwatch(int i) => _host.Commands.SetColor(Tokens.Palette[i].Color);

    private void ActivateSegment(int i)
    {
        switch (_bandCell)
        {
            case 2:                       // 白板三色：选板色＝要用板，所以顺手把板打开
                _host.Commands.SetBoardColor(InkPalette.BoardPresets[i].Color);
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

    /// <summary>抽屉的高：6 行 ＋ 两条分隔 ＋ 上下内边距。算出来的，加行不用手改数字。</summary>
    private static float DrawerHeight()
    {
        float h = DrawerPad * 2f + Rows.Length * DrawerRowH;
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
        var panel = PanelRect();
        float maxX = panel.MaxX;
        float y = BandAbove() ? panel.MinY - DrawerGap - h : panel.MaxY + DrawerGap;
        return new RectF { MinX = maxX - DrawerW, MinY = y, MaxX = maxX, MaxY = y + h };
    }

    private float RowTop(int i)
    {
        float y = DrawerRect().MinY + DrawerPad;
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
                Invalidate();
                break;
            case Row.AutoHide:
                _hideEnabled = !_hideEnabled;
                _peek.Jump(1f);          // 刚打开时先给个完整的，别一开就缩起来
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
        _dragStartAnchor = Anchor();

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

        int idx = HitCell(p.X, p.Y);
        if (idx >= 0)
        {
            _press = idx;
            return true;
        }

        // 上带：滑条优先（它的可拖区域比视觉大一圈，会和色片/分段挨着）
        if (BandVisible())
        {
            if (BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(p.X, p.Y))
            {
                _sliderDragging = true;
                DragSlider(p.X);
                return true;
            }
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
        _railHover = BandVisible() && RailZone().Contains(p.X, p.Y);

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
        for (int i = 0; i < Cells.Length; i++)
            if (CellRect(i).Contains(x, y)) return i;
        return -1;
    }

    private void Toggle()
    {
        bool collapse = _expand.Value > 0.5f;
        // 系统关掉动画时直接跳终态（教室里老机器上很常见）
        _expand.To(collapse ? 0f : 1f, collapse ? Tokens.CollapseMs : Tokens.ExpandMs);
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

        // 点工具格时，上带跟着换成这个工具的设置（"上带＝这个按钮的设置条"）。
        if (HasBand(idx)) _bandCell = idx;

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
        DrawCard(ctx, bar, Tokens.PillRadius(Tokens.BarHeight));

        var st = _host.State;
        if (e < 0.5f)
        {
            // 收起态：一个球，圆内那圈颜色 = 当前笔色（不用点开就知道手里是哪支笔）
            float d = Tokens.Ball - 8f;
            var c = new Vector2((bar.MinX + bar.MaxX) * 0.5f, (bar.MinY + bar.MaxY) * 0.5f);
            var ring = new Ellipse(c, d * 0.5f, d * 0.5f);
            ctx.DrawEllipse(ring, Brush(ctx, st.PaletteBase), 3f);
            IconAtlas.DrawCentered(ctx, "pen", bar, Tokens.Icon, Brush(ctx, InkCol));
            return;
        }

        // 上带单独一张方片（圆角 12，不做胶囊：两块叠一起用大圆角会出现"两段弧"）
        var band = BandRect();
        if (band.MaxY - band.MinY >= 2f) DrawCard(ctx, band, Tokens.BandRadius);

        // 展开态：球缩进最左一格，右边是 12 个工具格。
        DrawCell(ctx, 0, st, e);
        for (int i = 1; i < Cells.Length; i++) DrawCell(ctx, i, st, e);

        if (BandVisible()) DrawBand(ctx, st);
        if (_drawerOpen) DrawDrawer(ctx);
    }

    // ---- 颜色（深色主题只是一整套换过来，形状一个都不动）------------------

    private Color4 PanelFill => _dark ? Tokens.PanelDark : Tokens.PanelLight;
    private Color4 BorderCol => _dark ? Tokens.BorderDark : Tokens.BorderLight;
    private Color4 InkCol => _dark ? Tokens.InkDark : Tokens.InkLight;
    private Color4 HoverCol => _dark ? Tokens.HoverDark : Tokens.HoverLight;

    /// <summary>一张"卡片"：两层投影 ＋ 底 ＋ 1px 描边（没有这道边，圆角会糊进背景里）。</summary>
    private void DrawCard(ID2D1DeviceContext ctx, RectF r, float radius)
    {
        var sh1 = new Vortice.RawRectF(r.MinX, r.MinY + 1, r.MaxX, r.MaxY + 1);
        ctx.FillRoundedRectangle(new RoundedRectangle(sh1, radius, radius), Brush(ctx, Tokens.Shadow1));
        var sh2 = new Vortice.RawRectF(r.MinX, r.MinY + 3, r.MaxX, r.MaxY + 3);
        ctx.FillRoundedRectangle(new RoundedRectangle(sh2, radius, radius), Brush(ctx, Tokens.Shadow2));

        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, PanelFill));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
    }

    /// <summary>画上带的内容。每一项都对应引擎里真实存在的能力，摆不出来的就不摆。</summary>
    private void DrawBand(ID2D1DeviceContext ctx, in UiState st)
    {
        // 平时就一条 6 像素的色线（可点的 12 段），碰到才长成完整的设置条。
        if (!RailOpen) { DrawBandLine(ctx, st); return; }

        if (BandHasSwatches)
        {
            for (int i = 0; i < Tokens.Palette.Length; i++)
            {
                var r = SwatchRect(i);
                var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
                var rr = new RoundedRectangle(box, 7f, 7f);
                ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Palette[i].Color));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

                if (IsSwatchActive(st, i))
                {
                    // 选中的色块要有**第二重标记**（只靠颜色区分不符合无障碍要求）：
                    // 外圈深描边 ＋ 内圈白环。
                    var outer = new Vortice.RawRectF(r.MinX - 2, r.MinY - 2, r.MaxX + 2, r.MaxY + 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(outer, 9f, 9f), Brush(ctx, InkCol), 1.5f);
                    var inner = new Vortice.RawRectF(r.MinX + 1, r.MinY + 1, r.MaxX - 1, r.MaxY - 1);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 6f, 6f), Brush(ctx, new Color4(1f, 1f, 1f, 0.9f)), 2f);
                }
                else if (_hover == 100 + i)
                {
                    var outer = new Vortice.RawRectF(r.MinX - 2, r.MinY - 2, r.MaxX + 2, r.MaxY + 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(outer, 9f, 9f), Brush(ctx, InkCol), 1.5f);
                }
            }
        }

        if (BandHasSlider)
        {
            var r = SliderRect();
            _widgets.Slider(ctx, r, SliderT(st),
                            Brush(ctx, HoverCol), Brush(ctx, Tokens.Accent), Brush(ctx, Tokens.Accent));
        }

        int n = BandSegmentCount;
        for (int i = 0; i < n; i++) DrawSegment(ctx, i, n, st);
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
    /// 平时那条色线：笔/荧光笔就是 12 段色片压成的一条线（当前色那一段多一道白记号），
    /// 分段类是底槽里按比例高亮当前那一档，只有滑条的（激光）画一个位置点。
    /// </summary>
    private void DrawBandLine(ID2D1DeviceContext ctx, in UiState st)
    {
        var r = BandRect();
        float radius = MathF.Max(2f, (r.MaxY - r.MinY) * 0.5f);
        float left = r.MinX + Tokens.BarPad, right = r.MaxX - Tokens.BarPad;
        var trough = new Vortice.RawRectF(left, r.MinY, right, r.MaxY);
        ctx.FillRoundedRectangle(new RoundedRectangle(trough, radius, radius), Brush(ctx, HoverCol));

        if (BandHasSwatches)
        {
            int n = Tokens.Palette.Length;
            float w = (right - left) / n;
            for (int i = 0; i < n; i++)
            {
                var seg = new Vortice.RawRectF(left + i * w, r.MinY, left + (i + 1) * w, r.MaxY);
                ctx.FillRectangle(seg, Brush(ctx, Tokens.Palette[i].Color));
                if (IsSwatchActive(st, i))
                {
                    float cx = left + (i + 0.5f) * w;
                    ctx.FillRectangle(
                        new Vortice.RawRectF(cx - 1.5f, r.MinY + 0.5f, cx + 1.5f, r.MaxY - 0.5f),
                        Brush(ctx, new Color4(1f, 1f, 1f, 0.95f)));
                }
            }
            return;
        }

        int count = _bandCell == 2 ? InkPalette.BoardPresets.Length : BandSegmentCount;
        if (count > 0)
        {
            int sel = ActiveSegmentIndex(st);
            if (sel < 0) return;
            float w = (right - left) / count;
            var c = _bandCell == 2 ? InkPalette.BoardPresets[sel].Color : Tokens.Accent;
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(left + sel * w, r.MinY,
                                                          left + (sel + 1) * w, r.MaxY), radius, radius),
                Brush(ctx, c));
            return;
        }

        if (BandHasSlider)
        {
            float x = left + (right - left) * SliderT(st);
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(x - 8f, r.MinY, x + 8f, r.MaxY), radius, radius),
                Brush(ctx, Tokens.Accent));
        }
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
            ctx.FillRoundedRectangle(rr, Brush(ctx, InkPalette.BoardPresets[i].Color));
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
        _widgets.Text(ctx, label, r, 12.5f,
                      Brush(ctx, active ? Tokens.AccentInk : InkCol));
    }

    private string SegmentLabel(int i) => _bandCell switch
    {
        6 => i == 0 ? "整笔擦" : "面积擦",
        7 => i == 0 ? "矩形" : "套索",
        8 => i switch { 0 => "直线", 1 => "矩形", 2 => "椭圆", _ => "箭头" },
        _ => "",
    };

    private bool IsSegmentActive(in UiState st, int i) => _bandCell switch
    {
        2 => BoardColorIs(st, i),
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

    private void DrawCell(ID2D1DeviceContext ctx, int i, in UiState st, float e)
    {
        var r = CellRect(i);
        bool active = IsActive(i, st);
        bool hover = _hover == i || _press == i;

        if (i > 0)
        {
            // 分隔线画在组尾那一格的右边
            if (Array.IndexOf(GroupEnds, i) >= 0 && i != Cells.Length - 1)
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
        IconAtlas.DrawCentered(ctx, icon, r, Tokens.Icon, Brush(ctx, ink));
    }

    private bool IsActive(int i, in UiState st) => i switch
    {
        1 => st.PassThrough,
        2 => st.Board,
        3 => st.Tool == Tool.Pen,
        4 => st.Tool == Tool.Highlighter,
        5 => st.Tool == Tool.Laser,
        6 => st.Tool is Tool.Eraser or Tool.PixelEraser,
        7 => st.Tool == Tool.Marquee,
        8 => st.Tool is Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Arrow,
        9 => st.Tool == Tool.Capture,
        _ => false,
    };

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

    private void Invalidate() => _host?.InvalidateUi();

    // ---- 自检钩子（开发期用；产品代码不碰）--------------------------------

    /// <summary>自检用：第 i 格的逻辑矩形（换算成物理坐标再加 DPI 就能点）。</summary>
    internal RectF CellRectForTest(int i) => CellRect(i);

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

    /// <summary>自检用：抽屉开着没有 / 它的矩形 / 第 i 行的矩形。</summary>
    internal bool DrawerOpenForTest => _drawerOpen;
    internal RectF DrawerRectForTest => DrawerRect();
    internal RectF RowRectForTest(int i) => RowRect(i);

    /// <summary>自检用：深色主题与贴边隐藏的开关状态。</summary>
    internal bool DarkForTest => _dark;
    internal bool HideEnabledForTest => _hideEnabled;

    /// <summary>自检/出图用：把抽屉打开（产品里只能点「更多」那一格开）。</summary>
    internal void OpenDrawerForTest() => _drawerOpen = true;

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

    /// <summary>自检用：上带这一刻多高（6 = 色线，34 = 完整设置条）。</summary>
    internal float BandHeightForTest => BandRect().MaxY - BandRect().MinY;
}
