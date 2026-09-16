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

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    public FullUi()
    {
        _expand = new Anim(0f);
    }

    public string Name => "完整界面";
    public bool Visible => true;
    public bool IsAnimating => _expand.Running;

    public void Attach(IUiHost host)
    {
        _host = host;
        _widgets = new Widgets(host);
        _expand.Bind(host);        // 时钟必须接真的那个（见 Anim.Bind 的注释）
        _lastTool = host.State.Tool;
        _expand.Jump(0f);
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
        var bar = BarRect();
        var band = BandRect();
        // 上带还没长出来的那几帧只算主条（占用矩形必须跟着实际画出来的东西走，
        // 不然命中测试和输入小窗都会比画面大一圈）。
        if (band.MaxY - band.MinY < 2f) return bar;
        return new RectF { MinX = bar.MinX, MinY = bar.MinY, MaxX = bar.MaxX, MaxY = band.MaxY };
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
        float h = Tokens.BandHeight * BandProgress();
        return new RectF
        {
            MinX = bar.MinX, MinY = bar.MaxY + BandGap,
            MaxX = bar.MaxX, MaxY = bar.MaxY + BandGap + h,
        };
    }

    private const float BandGap = 4f;
    private float BandProgress() => _expand.Value;

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
        // 用**总高**（主条 ＋ 上带）：默认位置是"贴着下边"，上带长出来时面板往上长，
        // 而不是往下顶出屏幕（顶出屏幕的话输入小窗也会跟着跑到屏幕外）。
        float h = TotalHeight();
        Vector2 a = _anchor ?? new Vector2(
            _screen.MinX + (_screen.MaxX - _screen.MinX - w) * 0.5f,
            _screen.MaxY - Tokens.EdgeMargin - h);
        return Clamp(a, w, h);
    }

    /// <summary>夹在可见区域内（一期就夹在单块屏里，跨屏怎么画还没验证过）。</summary>
    private Vector2 Clamp(Vector2 a, float w, float h)
    {
        float minX = _screen.MinX + Tokens.DockGap;
        float maxX = _screen.MaxX - Tokens.DockGap - w;
        float minY = _screen.MinY + Tokens.DockGap;
        float maxY = _screen.MaxY - Tokens.DockGap - h;
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;
        return new Vector2(Math.Clamp(a.X, minX, maxX), Math.Clamp(a.Y, minY, maxY));
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
        for (int i = 0; i < Tokens.Palette.Length; i++)
            if (SwatchRect(i).Contains(x, y)) return i;
        return -1;
    }

    private int HitSegment(float x, float y)
    {
        int n = BandSegmentCount;
        for (int i = 0; i < n; i++)
            if (SegmentRect(i, n).Contains(x, y)) return i;
        return -1;
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

    // ---- 输入 ---------------------------------------------------------------

    public bool PointerDown(in UiPointerEvent e)
    {
        _press = -1;
        _dragging = false;
        _pressPos = new Vector2(e.X, e.Y);
        _dragStartAnchor = Anchor();

        if (_expand.Value < 0.5f)
        {
            if (!BallRect().Contains(e.X, e.Y)) return false;
            _press = -2;                    // 球
            return true;
        }

        int idx = HitCell(e.X, e.Y);
        if (idx >= 0)
        {
            _press = idx;
            return true;
        }

        // 上带：滑条优先（它的可拖区域比视觉大一圈，会和色片/分段挨着）
        if (BandVisible())
        {
            if (BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(e.X, e.Y))
            {
                _sliderDragging = true;
                DragSlider(e.X);
                return true;
            }
            int sw = HitSwatch(e.X, e.Y);
            if (sw >= 0) { ActivateSwatch(sw); return true; }
            int sg = HitSegment(e.X, e.Y);
            if (sg >= 0) { ActivateSegment(sg); return true; }
        }

        return false;                       // 带子/上带里的空白（两端内边距）：不吃，引擎按"地盘"吞掉
    }

    public bool PointerMove(in UiPointerEvent e)
    {
        var p = new Vector2(e.X, e.Y);

        if (_sliderDragging)
        {
            DragSlider(e.X);
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

        int hover = _expand.Value < 0.5f
            ? (BallRect().Contains(e.X, e.Y) ? -2 : -1)
            : HoverAt(e.X, e.Y);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        return hover != -1;
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
            case 12: break;                                  // 「更多」抽屉：下一版
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
            IconAtlas.DrawCentered(ctx, "pen", bar, Tokens.Icon, Brush(ctx, Tokens.InkLight));
            return;
        }

        // 上带单独一张方片（圆角 12，不做胶囊：两块叠一起用大圆角会出现"两段弧"）
        var band = BandRect();
        if (band.MaxY - band.MinY >= 2f) DrawCard(ctx, band, Tokens.BandRadius);

        // 展开态：球缩进最左一格，右边是 12 个工具格。
        DrawCell(ctx, 0, st, e);
        for (int i = 1; i < Cells.Length; i++) DrawCell(ctx, i, st, e);

        if (BandVisible()) DrawBand(ctx, st);
    }

    /// <summary>一张"卡片"：两层投影 ＋ 底 ＋ 1px 描边（没有这道边，圆角会糊进背景里）。</summary>
    private void DrawCard(ID2D1DeviceContext ctx, RectF r, float radius)
    {
        var sh1 = new Vortice.RawRectF(r.MinX, r.MinY + 1, r.MaxX, r.MaxY + 1);
        ctx.FillRoundedRectangle(new RoundedRectangle(sh1, radius, radius), Brush(ctx, Tokens.Shadow1));
        var sh2 = new Vortice.RawRectF(r.MinX, r.MinY + 3, r.MaxX, r.MaxY + 3);
        ctx.FillRoundedRectangle(new RoundedRectangle(sh2, radius, radius), Brush(ctx, Tokens.Shadow2));

        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.PanelLight));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.BorderLight), 1f);
    }

    /// <summary>画上带的内容。每一项都对应引擎里真实存在的能力，摆不出来的就不摆。</summary>
    private void DrawBand(ID2D1DeviceContext ctx, in UiState st)
    {
        if (BandHasSwatches)
        {
            for (int i = 0; i < Tokens.Palette.Length; i++)
            {
                var r = SwatchRect(i);
                var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
                var rr = new RoundedRectangle(box, 7f, 7f);
                ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Palette[i].Color));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.BorderLight), 1f);

                if (IsSwatchActive(st, i))
                {
                    // 选中的色块要有**第二重标记**（只靠颜色区分不符合无障碍要求）：
                    // 外圈深描边 ＋ 内圈白环。
                    var outer = new Vortice.RawRectF(r.MinX - 2, r.MinY - 2, r.MaxX + 2, r.MaxY + 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(outer, 9f, 9f), Brush(ctx, Tokens.InkLight), 1.5f);
                    var inner = new Vortice.RawRectF(r.MinX + 1, r.MinY + 1, r.MaxX - 1, r.MaxY - 1);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 6f, 6f), Brush(ctx, new Color4(1f, 1f, 1f, 0.9f)), 2f);
                }
                else if (_hover == 100 + i)
                {
                    var outer = new Vortice.RawRectF(r.MinX - 2, r.MinY - 2, r.MaxX + 2, r.MaxY + 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(outer, 9f, 9f), Brush(ctx, Tokens.InkLight), 1.5f);
                }
            }
        }

        if (BandHasSlider)
        {
            var r = SliderRect();
            _widgets.Slider(ctx, r, SliderT(st),
                            Brush(ctx, Tokens.HoverLight), Brush(ctx, Tokens.Accent), Brush(ctx, Tokens.Accent));
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
            ctx.DrawRoundedRectangle(rr, active ? Brush(ctx, Tokens.Accent) : Brush(ctx, Tokens.BorderLight),
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
            ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.HoverLight));
        }
        ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : Tokens.BorderLight), 1f);

        string label = SegmentLabel(i);
        _widgets.Text(ctx, label, r, 12.5f,
                      Brush(ctx, active ? Tokens.AccentInk : Tokens.InkLight));
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
                             Brush(ctx, Tokens.BorderLight), 1f);
            }

            if (active)
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, Tokens.Accent));
            }
            else if (hover)
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, Tokens.HoverLight));
            }
        }

        if (i == 0)
        {
            // 收起格：一个小球 + 当前笔色那圈（和收起态那个球是同一个东西，只是变小）
            var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
            float d = 32f;
            var ring = new Ellipse(c, d * 0.5f, d * 0.5f);
            ctx.DrawEllipse(ring, Brush(ctx, st.PaletteBase), 2.5f);
            IconAtlas.DrawCentered(ctx, "pen", r, 16f, Brush(ctx, Tokens.InkLight));
            return;
        }

        var icon = active ? Cells[i].Filled : Cells[i].Icon;
        var ink = active ? Tokens.AccentInk : Tokens.InkLight;
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

    /// <summary>自检用：现在算"展开"吗。</summary>
    internal bool ExpandedForTest => _expand.Value > 0.5f;

    /// <summary>自检用：把展开动画一步到位（不等 200 ms）。</summary>
    internal void SnapForTest() => _expand.Jump(_expand.Value > 0.5f ? 0f : 1f);

    /// <summary>自检用：界面看到的屏幕（核对它和 IUiHost.Screen 是不是同一个）。</summary>
    internal RectF ScreenForTest => _screen;
}
