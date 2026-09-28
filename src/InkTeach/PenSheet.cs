using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 出图用：**上游那一批"带笔的"图标对照**（开发期专用，不是产品界面）。
///
/// 缘起（用户 2026-09-27）："把上面所有斜着的笔拿下来……我看一下有什么合适的笔，
/// 给它加上一个'三道杠'，表示发光的意思，适合做激光笔。"
/// 挑笔这件事**只有眼睛说了算**（和 <see cref="ToolIconSheet"/> 当初加出来的理由一样）。
///
/// 每行一支笔，四格从左到右：
///   · **未选中（24）**——浅底 ＋ 图标色（平时在带子里的样子）；
///   · **选中（24）**——蓝底 ＋ 白图标；
///   · **放大（48）**——看细节、看笔尖朝哪；
///   · **放大 ＋ 三道杠（48）**——"这支笔要是当激光笔用，长这样"（<see cref="InkUi.IconAtlas.DrawGlowBars"/>）。
///
/// ⚠ 第 3、4 格**并排**是关键（第一版只出两张图，翻着看根本比不出来）：
///   同一支笔原样、加杠放在一起，"加了以后还认得出是笔吗 / 杠放得下吗"一眼就看得出来。
///   三道杠的位置对每支笔**完全一样**（以 (7.60,16.40) 当笔尖、朝 135°±30°），
///   这样比的才是"哪支笔留得出地方"，而不是"哪支笔的杠被挪过"。
///
/// 图标数据来自 <see cref="PenIcons"/>（由 `tmp/gen-pen-icons.ps1` 从上游拉下来的 13 支，
/// **产品图标表一个字都没动**）。挑中的那支再挪进 src/InkUi/Icons.g.cs。
/// 入口：`--penshow <路径>`。
/// </summary>
internal sealed class PenSheet : IOverlayUi
{
    private IUiHost _host;
    private RectF _bounds;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    /// <summary>本地名 ＋ 给人看的说明（上游名写在 <see cref="PenIcons"/> 的注释里）。</summary>
    private static readonly (string Key, string Label)[] Pens =
    {
        ("pen",            "Pen —— 我们现在笔工具用的那支"),
        ("penSparkle",     "Pen Sparkle —— 笔 ＋ 一点闪光（上游自带的「发光笔」）"),
        ("laserTool",      "★ Laser Tool —— 上游真有这个名（只有 20 像素版）＝ 已选中，原样用它"),
        ("edit",           "Edit —— 铅笔"),
        ("highlight",      "Highlight —— 我们现在荧光笔用的那支"),
        ("inkStroke",      "Ink Stroke —— 一笔墨迹"),
        ("inkingTool",     "Inking Tool —— 触控笔"),
        ("calligraphyPen", "Calligraphy Pen —— 书法笔"),
        ("paintBrush",     "Paint Brush —— 画笔"),
        ("paintBrushSparkle", "Paint Brush Sparkle —— 画笔 ＋ 一点闪光"),
        ("wand",           "Wand —— 魔杖"),
        ("handDraw",       "Hand Draw —— 手绘（笔 ＋ 一点）"),
        ("drawShape",      "Draw Shape —— 画图形"),
        ("drawText",       "Draw Text —— 写字"),
    };

    private const float ColW = 620f, RowH = 92f, TitleH = 60f;

    private static readonly Color4 InkCol = new(0.11f, 0.12f, 0.15f, 1f);
    private static readonly Color4 CellBg = new(0f, 0f, 0f, 0.07f);
    private static readonly Color4 AccentBg = new(0f, 0.404f, 0.753f, 1f);
    private static readonly Color4 AccentInk = new(1f, 1f, 1f, 1f);

    public string Name => "候选笔对照";
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
        float h = 24f + TitleH + Pens.Length * RowH + 16f;
        float w = ColW + 32f;
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

        var titleFmt = _host.TextFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, 13.5f, "zh-CN");
        var labelFmt = _host.TextFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13f, "zh-CN");

        ctx.DrawText("上游「带笔的」图标 —— 未选中 / 选中 / 放大 / 放大＋三道杠",
            titleFmt, new Rect(_bounds.MinX + 16f, _bounds.MinY + 16f, ColW, 26f), Brush(ctx, InkCol));
        ctx.DrawText("最后两格并排：原样 vs 同一支笔加了「三道杠」（杠的位置每支都一样）",
            labelFmt, new Rect(_bounds.MinX + 16f, _bounds.MinY + 40f, ColW, 20f),
            Brush(ctx, new Color4(0.35f, 0.37f, 0.42f, 1f)));

        float y = _bounds.MinY + 24f + TitleH;
        foreach (var (key, label) in Pens)
        {
            ctx.DrawText(label, labelFmt,
                         new Rect(_bounds.MinX + 16f, y + RowH * 0.5f - 10f, 250f, 20f),
                         Brush(ctx, InkCol));

            DrawCell(ctx, CellRect(272f, y, 48f), 48f, key, selected: false, bars: false);
            DrawCell(ctx, CellRect(328f, y, 48f), 48f, key, selected: true, bars: false);
            DrawCell(ctx, CellRect(392f, y, 84f), 84f, key, selected: false, bars: false);
            DrawCell(ctx, CellRect(488f, y, 84f), 84f, key, selected: false, bars: true);
            y += RowH;
        }
    }

    private RectF CellRect(float x, float y, float side)
    {
        float top = y + (RowH - side) * 0.5f;
        return new RectF
        {
            MinX = _bounds.MinX + x, MinY = top,
            MaxX = _bounds.MinX + x + side, MaxY = top + side,
        };
    }

    /// <summary>画一格：浅底/蓝底 ＋ 这支笔（<paramref name="bars"/> 为真再加三道杠）。</summary>
    private void DrawCell(ID2D1DeviceContext ctx, RectF cell, float size, string key,
                          bool selected, bool bars)
    {
        var cbox = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
        ctx.FillRoundedRectangle(new RoundedRectangle(cbox, 8f, 8f),
                                 Brush(ctx, selected ? AccentBg : CellBg));

        var brush = Brush(ctx, selected ? AccentInk : InkCol);
        // 图标按 24 网格那套尺寸画（比格子小一圈，和产品里一样留出内边距）
        float icon = size * (24f / 32f);
        InkUi.IconAtlas.DrawCandidate(ctx, PenIcons.Get(key), PenIcons.BoxOf(key), cell, icon, brush);
        if (bars) InkUi.IconAtlas.DrawGlowBars(ctx, cell, icon, brush);
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
