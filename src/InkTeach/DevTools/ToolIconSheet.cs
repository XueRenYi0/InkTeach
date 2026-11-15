using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 出图用：**主界面那几个工具图标的候选**（开发期专用，不是产品界面）。
///
/// 缘起（用户 2026-09-26）："白板：感觉不像""激光笔：我喜欢那种'笔射出一道光'的形态。
/// 如果搜不到的话，你可以自己写。"——图标像不像**只有眼睛说了算**，代码里的坐标
/// 判断不了（这一条和 <see cref="LaserIconSheet"/> / <see cref="CaptureIconSheet"/>
/// 那两张当初加出来的理由一模一样）。
///
/// 每行三格，从左到右：
///   · **未选中**——浅底 ＋ 图标色，就是平时在带子里的样子；
///   · **选中**——蓝底 ＋ 白图标，就是这一格亮着的样子（白板那几行还会把
///     板面填成**这块板的颜色**，那正是"选中显示选中什么"这条逻辑的体现）；
///   · **放大一倍（48）**——用**未选中**那一版画：线粗、接缝、"和别的图标撞不撞脸"
///     这些细节在 48 里才看得清；选中态的"分量够不够"在 24 那一格比得出来。
///
/// 三个候选来源各占自己的字段（见 <see cref="Row"/>），没填的那几个自然不画。
/// 换行看图的入口：`--toolicons`。
/// </summary>
internal sealed class ToolIconSheet : IOverlayUi
{
    private IUiHost _host;
    private RectF _bounds;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    /// <summary>
    /// 一行候选。三个来源装在一起，谁填了就用谁：
    ///   · <see cref="Icon"/> / <see cref="IconAlt"/> —— 上游 Fluent（或 Material）图标名，
    ///     前者画"未选中"，后者画"选中"（不填就两个状态用同一张，和 Fluent 那批 regular/filled 一个意思）；
    ///   · <see cref="LaserOff"/> / <see cref="LaserOn"/> —— 自绘激光的档号（见 IconAtlas.DrawLaser），
    ///     前者是未选中那格、后者是选中那格；只填一个的话，两个状态都用它（选中那格线会加粗）；
    ///   · <see cref="Board"/> / <see cref="BoardFill"/> —— 自绘白板的款式（见 IconAtlas.DrawBoard）＋
    ///     选中那格板面填的颜色。
    /// </summary>
    private sealed class Row
    {
        public string Label;
        public string Icon;                 // 未选中那格画的图标名
        public string IconAlt;              // 选中那格画的图标名（不填就和 Icon 一样）
        public string IconBig;              // 放大那格画的图标名（默认跟 Icon 一样）
        public int LaserOff = -1;
        public int LaserOn = -1;
        public int Board = -1;
        public Color4? BoardFill;
    }

    /// <summary>三段的标题 ＋ 行（分段的理由：这三件事在图上要一眼分得开）。</summary>
    private static readonly (string Title, Row[] Rows)[] Sections =
    {
        ("一、白板 —— 用户：\"白板：感觉不像\"（原来用的是 Fluent Board：圆角方框劈成四块，像窗口布局）", new[]
        {
            new Row { Label = "Fluent Board（原来用的）", Icon = "board" },
            new Row { Label = "① 板 ＋ 两条短腿", Board = 1 },
            new Row { Label = "② 板 ＋ 长腿 ＋ 两行板书（2026-09-26 早些时候用过）", Board = 2 },
            new Row { Label = "③ 板 ＋ 长腿 ＋ 托盘", Board = 3 },
            new Row { Label = "④ 板 ＋ 长腿（没有板书）", Board = 4 },
            new Row { Label = "★ ⑤ 板 ＋ 正在写的马克笔（现在用它；笔身 2026-09-27 由 2.8 压到 2.0）", Board = 5 },
            new Row { Label = "⑥ 板 ＋ 两行板书（不画腿，新加：板里不再有那支像橡皮的笔）", Board = 6 },
        }),

        ("二、白板的选中态 —— 板开着的时候，图标就是**这块板的颜色**（\"选中显示选中什么\"）", new[]
        {
            new Row { Label = "板色 = 白板", Board = InkUi.IconAtlas.BoardDefault, BoardFill = InkPalette.BoardPresets[0].Color },
            new Row { Label = "板色 = 绿板", Board = InkUi.IconAtlas.BoardDefault, BoardFill = InkPalette.BoardPresets[1].Color },
            new Row { Label = "板色 = 黑板", Board = InkUi.IconAtlas.BoardDefault, BoardFill = InkPalette.BoardPresets[2].Color },
        }),

        ("三、激光笔 —— 用户：\"笔的形状要比较明显，然后要射出来一道细线，激光朝左下、笔朝上\"", new[]
        {
            new Row { Label = "原来用的（未选中细线版 ／ 选中加重版）", LaserOff = 5, LaserOn = 2 },
            // 2026-09-26 第二批：照用户给的参考图（真激光笔：笔身 ＋ 金属圈 ＋ 按钮）画的三档。
            new Row { Label = "① 笔形＋细光：未选中线描／选中实心", LaserOff = 12, LaserOn = 12 },
            new Row { Label = "② 笔形＋细光＋按钮（笔身上一颗点）", LaserOff = 13, LaserOn = 13 },
            new Row { Label = "★ ③ 笔形＋细光：两态都实心（照参考图，2026-09-26 用它）", LaserOff = 14, LaserOn = 14 },
            // Material Symbols 的专名（Apache-2.0，见 Icons.g.cs 里的 msStylusLaser）——
            // 上一轮搜到的那个，留着当对照（用户这一轮的结论是"笔要更像笔、光要细"）。
            new Row { Label = "④ Material stylus_laser_pointer（上一轮搜到的，作对照）", Icon = "msStylusLaser", IconAlt = "msStylusLaserFill" },
        }),

        ("四、另外那几格也照\"选中显示选中什么\"这条逻辑（和图形那一格一样）", new[]
        {
            // 选择那一格（2026-09-26 加）：未选中＝通用的选择框；选中之后按当前档换。
            new Row { Label = "选择：未选中 ／ 选中＝矩形框选", Icon = "select", IconAlt = "selectFilled" },
            new Row { Label = "选择：选中＝套索（那一档换套索那张）", Icon = "select", IconAlt = "lasso", IconBig = "lasso" },
            // 橡皮那一格早就这样了（整笔擦／面积擦各一张，见 IconAtlas.DrawEraser）——放这儿当参照。
            new Row { Label = "橡皮（早就是这样，作参照）", Icon = "eraserFilled", IconAlt = "eraserFilled" },
        }),

        ("五、激光笔（2026-09-27）—— 用户：\"ClassIn 那个就是一个笔头加了一个发光提示……" +
         "我们是不是也可以仿照他这种，在正常的笔头上加一个发光的，但风格要是我们现在这种\"", new[]
        {
            // ★ 第五轮定稿：用户从上游那批"带笔的"图标里挑的（见 PenSheet / `--penshow`）——
            //   **Fluent 自家的 `Laser Tool`（20 网格），原样用、什么都不加**。
            new Row { Label = "★ 现在用它：Fluent 自家的 Laser Tool（原样，不加杠）", Icon = "laserTool", IconAlt = "laserToolFilled" },
            new Row { Label = "A′（2026-09-26 那版）：大笔 ＋ 两道虚线 ＋ 点", LaserOff = 16, LaserOn = 16 },
            new Row { Label = "方案 A（第二轮）：笔头 ＋ 亮点 ＋ 三道射线", LaserOff = 17, LaserOn = 17 },
            new Row { Label = "方案 A′（四道射线，扇得开一点）", LaserOff = 18, LaserOn = 18 },
            new Row { Label = "方案 B（第二轮）：笔头 ＋ 亮点 ＋ 一圈光晕（整圈）", LaserOff = 19, LaserOn = 19 },
            new Row { Label = "方案 C（第三轮）：只有一颗笔头 ＋ 三道光（照 ClassIn）", LaserOff = 20, LaserOn = 20 },
            // 第四轮（2026-09-27）：用户自己点名的形态——**钢笔尖 ＋ 三道杠**，四档只差尺寸
            new Row { Label = "方案 D①（1.00 倍）：钢笔尖 ＋ 三道杠，整组占满 15.4 格", LaserOff = 21, LaserOn = 21 },
            new Row { Label = "方案 D②（0.92 倍）", LaserOff = 22, LaserOn = 22 },
            new Row { Label = "方案 D③（0.84 倍）", LaserOff = 23, LaserOn = 23 },
            new Row { Label = "方案 D④（0.76 倍，最小）", LaserOff = 24, LaserOn = 24 },
        }),
    };

    private const float ColW = 540f, RowH = 64f, TitleH = 30f, SectionGap = 10f;

    /// <summary>表里画图标用的三种底色/图标色（和产品界面同源，见 Tokens）。</summary>
    private static readonly Color4 InkCol = new(0.11f, 0.12f, 0.15f, 1f);
    private static readonly Color4 CellBg = new(0f, 0f, 0f, 0.07f);
    private static readonly Color4 AccentBg = new(0f, 0.404f, 0.753f, 1f);
    private static readonly Color4 AccentInk = new(1f, 1f, 1f, 1f);

    public string Name => "工具图标对照";
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
        float h = 24f;
        foreach (var (_, rows) in Sections) h += TitleH + rows.Length * RowH + SectionGap;
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
            FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, 12.5f, "zh-CN");
        var labelFmt = _host.TextFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 12.5f, "zh-CN");

        float y = _bounds.MinY + 16f;
        foreach (var (title, rows) in Sections)
        {
            ctx.DrawText(title, titleFmt,
                         new Rect(_bounds.MinX + 16f, y, ColW, TitleH - 6f), Brush(ctx, InkCol));
            y += TitleH;

            foreach (var row in rows)
            {
                ctx.DrawText(row.Label, labelFmt,
                             new Rect(_bounds.MinX + 16f, y + RowH * 0.5f - 9f, 250f, 20f),
                             Brush(ctx, InkCol));

                // 三格：未选中（浅底）／选中（蓝底）／放大（浅底，48）
                DrawState(ctx, CellRect(280f, y), 24f, row, selected: false);
                DrawState(ctx, CellRect(330f, y), 24f, row, selected: true);
                DrawState(ctx, BigRect(390f, y), 48f, row, selected: false, big: true);

                y += RowH;
            }
            y += SectionGap;
        }
    }

    /// <summary>那一行第 1／2 格的方框（40×40 的正方形，垂直居中在行里）。</summary>
    private RectF CellRect(float x, float y)
    {
        float top = y + (RowH - 40f) * 0.5f;
        return new RectF
        {
            MinX = _bounds.MinX + x, MinY = top,
            MaxX = _bounds.MinX + x + 40f, MaxY = top + 40f,
        };
    }

    /// <summary>那一行第 3 格（放大）的方框：80×80，还是垂直居中。</summary>
    private RectF BigRect(float x, float y)
    {
        float top = y + (RowH - 80f) * 0.5f;
        return new RectF
        {
            MinX = _bounds.MinX + x, MinY = top,
            MaxX = _bounds.MinX + x + 80f, MaxY = top + 80f,
        };
    }

    /// <summary>
    /// 画三格里的某一格。
    /// <paramref name="selected"/> 为真时用**蓝底 ＋ 白图标**（就是带子里"这一格亮着"的样子）。
    /// </summary>
    private void DrawState(ID2D1DeviceContext ctx, RectF cell, float size, Row row, bool selected,
                           bool big = false)
    {
        var cbox = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
        ctx.FillRoundedRectangle(new RoundedRectangle(cbox, 8f, 8f),
                                 Brush(ctx, selected ? AccentBg : CellBg));

        var ink = selected ? AccentInk : InkCol;
        var brush = Brush(ctx, ink);

        if (row.LaserOff >= 0 || row.LaserOn >= 0)
        {
            // 未选中那格画线描版（细一号、落点空心）；选中那格画实心版。
            // 只填了一个档号的行：两个状态用同一档，靠 "outline" 这个开关分粗细。
            int v = selected ? (row.LaserOn >= 0 ? row.LaserOn : row.LaserOff)
                             : (row.LaserOff >= 0 ? row.LaserOff : row.LaserOn);
            InkUi.IconAtlas.DrawLaser(ctx, cell, size, brush, null, v, outline: !selected);
            return;
        }

        if (row.Board >= 0)
        {
            // 选中那格把板面填成这块板的颜色（不填就用白板色）——"选中显示选中什么"。
            Color4? fill = null;
            if (selected) fill = row.BoardFill ?? InkPalette.BoardPresets[0].Color;
            InkUi.IconAtlas.DrawBoard(ctx, cell, size, brush, fill, row.Board);
            return;
        }

        var name = big && row.IconBig != null ? row.IconBig
                 : selected && row.IconAlt != null ? row.IconAlt
                 : row.Icon;
        InkUi.IconAtlas.DrawCentered(ctx, name, cell, size, brush);
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