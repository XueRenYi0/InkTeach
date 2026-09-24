using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 出图用：把**图形面板里所有图标**（含每一档的变体）摆在一张图上，每个画三个尺寸
///（24 = 界面里的真实大小、48、96）——开发期专用，不是产品界面。
///
/// 为什么要这张图：图形图标是自绘的、而且**一格多档**（棱柱 / 棱锥 / 棱台 各 4 张、
/// 直线 3 张、抛物线 2 张），"够不够看、和别格撞不撞脸"**只有眼睛说了算**。
/// 2026-09-20 用户说"三棱锥图标看着不对"，靠代码里的坐标判断不出来，
/// 这张表就是那时候加的（照 <see cref="LaserIconSheet"/> 的写法）。
///
/// ⚠ 名单**是手写的**（和面板那张 `ShapeRows` 不是一回事）：这里要的是"**所有档位变体
/// 都摆出来**"，而面板一次只显示当前那一档。**加一种图形要记得往这儿也加一条**
/// （这份名单漏了不会红——它是给眼睛看的，不进自检）。
/// </summary>
internal sealed class ShapeIconSheet : IOverlayUi
{
    private IUiHost _host;
    private RectF _bounds;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    /// <summary>一行一个图标名（就是 `IconAtlas.Draw` 认的那些名字）。</summary>
    private static readonly (string Name, string Label)[] Items =
    {
        // 第一行那八个高频图形
        ("line", "直线（实线）"), ("lineDash", "直线（虚线）"), ("lineDot", "直线（点线）"),
        ("square", "矩形"),
        ("oval", "椭圆"), ("circle", "圆"), ("triangle", "三角形"),
        ("parallelogram", "平行四边形"), ("arrowRight", "箭头"), ("axes", "坐标系"),
        // 坐标系有两档（2026-09-24）：带网格 / 不带网格——那一格"再点一次换一档"。
        ("axesGrid", "坐标系（带网格）"),
        ("numberline", "数轴（撤了入口）"),
        // 曲线：抛物线 / 双曲线 / 正弦 / 余弦 / 波浪线 / **正切**（第十五、十六批）
        // ＋ **椭圆（带焦点）**（2026-09-22）：它和双曲线**各两张**（各有一个"两档"）
        ("parabola", "抛物线（上下）"), ("parabolaRight", "抛物线（左右）"),
        ("hyperbola", "双曲线（有渐近线）"), ("hyperbolaNoAsym", "双曲线（无渐近线）"),
        ("ovalFocusTri", "椭圆·焦点三角形"), ("ovalFocus", "椭圆·只有焦点"),
        ("sine", "正弦"), ("cosine", "余弦"),
        ("wave", "波浪线"), ("tangent", "正切"),
        // 旋转体
        ("cylinder", "圆柱"), ("cone", "圆锥"), ("conefrustum", "圆台"), ("sphere", "球"),
        // 棱柱一族：三 / 四 / 五 / 六（每一档一张）
        ("prism3", "棱柱 3"), ("prism4", "棱柱 4"), ("prism5", "棱柱 5"), ("prism6", "棱柱 6"),
        ("pyramid3", "棱锥 3"), ("pyramid4", "棱锥 4"), ("pyramid5", "棱锥 5"), ("pyramid6", "棱锥 6"),
        ("frustum3", "棱台 3"), ("frustum4", "棱台 4"), ("frustum5", "棱台 5"), ("frustum6", "棱台 6"),
        // 撤了面板入口、但画法还在（旧板书里的那些要照旧显示）
        ("cuboid", "长方体（撤了入口）"), ("tetrahedron", "四面体（撤了入口）"),
    };

    private const int Cols = 3;
    private const float ColW = 300f, RowH = 76f;

    public string Name => "图形图标对照";
    public bool Visible => true;
    public bool IsAnimating => false;

    public void Attach(IUiHost host)
    {
        _host = host;
        InkUi.IconAtlas.Init(host.PathFactory);
        Layout(host.Screen, host.DpiScale);
    }

    public RectF Layout(RectF screen, float dpiScale)
    {
        int rows = (Items.Length + Cols - 1) / Cols;
        float w = Cols * ColW + 32f, h = rows * RowH + 32f;
        _bounds = new RectF
        {
            MinX = screen.MinX + (screen.MaxX - screen.MinX - w) * 0.5f,
            MinY = screen.MinY + 60f, MaxX = 0, MaxY = 0,
        };
        _bounds.MaxX = _bounds.MinX + w;
        _bounds.MaxY = _bounds.MinY + h;
        return _bounds;
    }

    public RectF QueryBounds() => _bounds;

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_host == null || _bounds.IsEmpty) return;

        var box = new Vortice.RawRectF(_bounds.MinX, _bounds.MinY, _bounds.MaxX, _bounds.MaxY);
        ctx.FillRoundedRectangle(new RoundedRectangle(box, 12f, 12f), Brush(ctx, new Color4(1f, 1f, 1f, 0.94f)));
        ctx.DrawRoundedRectangle(new RoundedRectangle(box, 12f, 12f), Brush(ctx, new Color4(0f, 0f, 0f, 0.12f)), 1f);

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);
        var fmt = _host.TextFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 12f, "zh-CN");

        for (int i = 0; i < Items.Length; i++)
        {
            int col = i % Cols, row = i / Cols;
            float x = _bounds.MinX + 16f + col * ColW;
            float y = _bounds.MinY + 16f + row * RowH;
            float cy = y + RowH * 0.5f;

            ctx.DrawText(Items[i].Label, fmt,
                         new Rect(x, cy - 9f, 150f, 18f), Brush(ctx, ink));

            // 三个尺寸从左到右：真实（24）/ 一倍半（36）/ 三倍（72）
            DrawOne(ctx, x + 160f, cy, Items[i].Name, 24f, ink);
            DrawOne(ctx, x + 200f, cy, Items[i].Name, 36f, ink);
            DrawOne(ctx, x + 252f, cy, Items[i].Name, 72f, ink);
        }
    }

    /// <summary>按"中心 + 尺寸"画一张图标（三个尺寸共用这一处）。</summary>
    private void DrawOne(ID2D1DeviceContext ctx, float cx, float cy, string name,
                         float size, Color4 ink)
    {
        var box = new RectF
        {
            MinX = cx - size * 0.5f, MinY = cy - size * 0.5f,
            MaxX = cx + size * 0.5f, MaxY = cy + size * 0.5f,
        };
        InkUi.IconAtlas.DrawCentered(ctx, name, box, size, Brush(ctx, ink));
    }

    private ID2D1SolidColorBrush Brush(ID2D1DeviceContext ctx, Color4 c)
    {
        uint key = ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16)
                 | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = ctx.CreateSolidColorBrush(c, null);
        _brushes[key] = b;
        return b;
    }

    public bool PointerDown(in UiPointerEvent e) => false;
    public bool PointerMove(in UiPointerEvent e) => false;
    public void PointerLeave() { }
    public bool PointerUp(in UiPointerEvent e) => false;
    public void OnStateChanged(in UiState state) { }
}
