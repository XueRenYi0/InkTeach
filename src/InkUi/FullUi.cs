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
        _expand.Bind(host);        // 时钟必须接真的那个（见 Anim.Bind 的注释）
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
        var a = Anchor();
        float w = Width();
        float h = Height();
        return new RectF { MinX = a.X, MinY = a.Y, MaxX = a.X + w, MaxY = a.Y + h };
    }

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
        float h = Height();
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
        if (idx < 0) return false;          // 带子里的空白（两端内边距）：不吃
        _press = idx;
        return true;
    }

    public bool PointerMove(in UiPointerEvent e)
    {
        var p = new Vector2(e.X, e.Y);

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
            : HitCell(e.X, e.Y);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        return hover != -1;
    }

    public bool PointerUp(in UiPointerEvent e)
    {
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

    public void OnStateChanged(in UiState state) => Invalidate();

    // ---- 绘制 ---------------------------------------------------------------

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_host == null) return;
        var b = QueryBounds();
        if (b.IsEmpty) return;

        float e = _expand.Value;
        var box = new Vortice.RawRectF(b.MinX, b.MinY, b.MaxX, b.MaxY);
        float radius = Tokens.PillRadius(Height());
        var rr = new RoundedRectangle(box, radius, radius);

        // 投影：同形状往下叠两层、逐层变淡（真模糊贵一个量级，而且系统会自己降级）。
        var sh1 = new Vortice.RawRectF(b.MinX, b.MinY + 1, b.MaxX, b.MaxY + 1);
        ctx.FillRoundedRectangle(new RoundedRectangle(sh1, radius, radius), Brush(ctx, Tokens.Shadow1));
        var sh2 = new Vortice.RawRectF(b.MinX, b.MinY + 3, b.MaxX, b.MaxY + 3);
        ctx.FillRoundedRectangle(new RoundedRectangle(sh2, radius, radius), Brush(ctx, Tokens.Shadow2));

        // 面板底 + 1px 描边（没有这道边，圆角会糊进背景里）。
        ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.PanelLight));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.BorderLight), 1f);

        var st = _host.State;
        if (e < 0.5f)
        {
            // 收起态：一个球，圆内那圈颜色 = 当前笔色（不用点开就知道手里是哪支笔）
            float d = Tokens.Ball - 8f;
            var c = new Vector2((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f);
            var ring = new Ellipse(c, d * 0.5f, d * 0.5f);
            ctx.DrawEllipse(ring, Brush(ctx, st.PaletteBase), 3f);
            IconAtlas.DrawCentered(ctx, "pen", b, Tokens.Icon, Brush(ctx, Tokens.InkLight));
            return;
        }

        // 展开态：球缩进最左一格，右边是 12 个工具格（按 e 淡入）。
        DrawCell(ctx, 0, st, e);
        for (int i = 1; i < Cells.Length; i++) DrawCell(ctx, i, st, e);
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

    /// <summary>自检用：现在算"展开"吗。</summary>
    internal bool ExpandedForTest => _expand.Value > 0.5f;

    /// <summary>自检用：把展开动画一步到位（不等 200 ms）。</summary>
    internal void SnapForTest() => _expand.Jump(_expand.Value > 0.5f ? 0f : 1f);

    /// <summary>自检用：界面看到的屏幕（核对它和 IUiHost.Screen 是不是同一个）。</summary>
    internal RectF ScreenForTest => _screen;
}
