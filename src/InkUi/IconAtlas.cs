using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 图标绘制。路径数据来自 `Icons.g.cs`（`tools/gen-ui-icons.ps1` 从 Fluent UI
/// System Icons 生成，MIT），几何由引擎的 <see cref="SvgPath"/> 解析并按 d 缓存。
///
/// 三条规矩：
///
///   1. **绝不每帧重建几何**。WPF 里每帧 `Geometry.Parse` 没感觉，D2D 里是要命的；
///      缓存放在 <see cref="SvgPath"/> 里（按 d 字符串），这里只负责算变换。
///   2. 几何按 24×24 的原始坐标存，绘制时用变换缩放到目标尺寸——
///      同一份几何在任意尺寸/DPI 下复用。
///   3. 名字拼错**当场抛**（生成的 `Get` 就是这么写的），不画一个空图标了事：
///      空图标在界面上太难被发现。
/// </summary>
internal static class IconAtlas
{
    private static ID2D1StrokeStyle _round;
    private static ID2D1Factory1 _factory;
    private static ID2D1PathGeometry _cone;

    /// <summary>
    /// 建一次"圆头圆角"的描边样式（自绘图标要用）。由界面在 Attach 时调一次。
    /// 上游图标都是填充路径，不需要它；我们自己画的那几个（激光笔）是**线条**，
    /// 线头不圆的话，那个"笔＋光束"会像三根火柴棍。
    /// </summary>
    public static void Init(ID2D1Factory1 factory)
    {
        if (factory == null) return;
        _factory = factory;
        if (_round != null) return;
        _round = factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
    }

    /// <summary>按 24 网格画的图标，画的左上角是 (x, y)，边长 size。</summary>
    public static void Draw(ID2D1DeviceContext ctx, string name, float x, float y,
                            float size, ID2D1Brush brush)
    {
        var geo = SvgPath.Get(PanelIcons.Get(name));
        if (geo == null) return;

        // 有些上游图标不是 24 网格（Material Symbols 用 960），而且原点可能是负的
        // （viewBox="0 -960 960 960"）。只按宽度缩放、不按原点平移，图标会画到框外——
        // 看着就是"这个图标只有一个小角"（自检出图时当场看到过一次）。
        float box = 24f, ox = 0f, oy = 0f;
        if (PanelIcons.TryGetBox(name, out var vb))
        {
            var parts = vb.Split(' ');
            if (parts.Length >= 4
                && float.TryParse(parts[0], out var vx) && float.TryParse(parts[1], out var vy)
                && float.TryParse(parts[2], out var vw) && vw > 0)
            {
                box = vw; ox = vx; oy = vy;
            }
        }

        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / box)
                      * Matrix3x2.CreateTranslation(-ox, -oy)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;
        ctx.FillGeometry(geo, brush);
        ctx.Transform = saved;
    }

    /// <summary>在方块里居中画一个图标（按钮上用它，省得每处都自己算居中）。</summary>
    public static void DrawCentered(ID2D1DeviceContext ctx, string name, RectF box,
                                    float size, ID2D1Brush brush)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        Draw(ctx, name, cx - size * 0.5f, cy - size * 0.5f, size, brush);
    }

    /// <summary>
    /// **自绘的激光笔图标**：笔 ＋ 光束 ＋ 落点。
    ///
    /// 为什么不用上游图标：Fluent 里没有"激光笔"这个专名（只有 Flash 闪电、
    /// Record 圆点这些近义）；Material Symbols 有专名但要引第二个图标库，
    /// 混库会让线宽和光学尺寸对不上。这三个元素照抄假面板里量好的坐标
    /// （24 网格：笔身 (17,4.6)→(12.4,9.2) 粗 3、光束 (11.6,10)→(9.4,12.2) 细 1.4、
    /// 落点圆心 (6.6,15) 半径 2.1），画出来和界面上其它 Fluent 图标是一套手感。
    /// </summary>
    public static void DrawLaser(ID2D1DeviceContext ctx, RectF box, float size,
                                ID2D1Brush brush, ID2D1Brush softBrush = null,
                                int variant = LaserDefault)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;

        switch (variant)
        {
            case 0:     // 第一版：笔 ＋ 细光束 ＋ 小落点（用户说"有点单薄"）
                ctx.DrawLine(new Vector2(17f, 4.6f), new Vector2(12.4f, 9.2f), brush, 3f, _round);
                ctx.DrawLine(new Vector2(11.6f, 10f), new Vector2(9.4f, 12.2f), brush, 1.4f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(6.6f, 15f), 2.1f, 2.1f), brush);
                break;

            case 1:     // 锥形光束（照假面板那一版）：一片 30% 的锥 ＋ 两条边 ＋ 大一点的落点
                if (Cone() != null) ctx.FillGeometry(Cone(), softBrush ?? brush);
                ctx.DrawLine(new Vector2(7.6f, 16.4f), new Vector2(20.5f, 3.5f), brush, 1.7f, _round);
                ctx.DrawLine(new Vector2(8.4f, 18.4f), new Vector2(21.5f, 13.5f), brush, 1.7f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(6f, 18f), 2.7f, 2.7f), brush);
                break;

            case 2:     // 加重版：笔更粗、光束更粗、落点更大
                ctx.DrawLine(new Vector2(17.5f, 4.2f), new Vector2(12.6f, 9.1f), brush, 3.6f, _round);
                ctx.DrawLine(new Vector2(11.8f, 9.9f), new Vector2(8.8f, 12.9f), brush, 2.4f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(7f, 14.8f), 2.9f, 2.9f), brush);
                break;

            case 3:     // 锥形 ＋ 落点光环（落点外面再套一圈，像"正在打的那一点"）
                ctx.DrawLine(new Vector2(7.6f, 16.4f), new Vector2(20.5f, 3.5f), brush, 1.7f, _round);
                ctx.DrawLine(new Vector2(8.4f, 18.4f), new Vector2(21.5f, 13.5f), brush, 1.7f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(6f, 18f), 2.7f, 2.7f), brush);
                ctx.DrawEllipse(new Ellipse(new Vector2(6f, 18f), 4.3f, 4.3f), brush, 1.1f);
                break;

            default:    // "PowerPoint 那颗红点"：一个实心红点 ＋ 一圈很淡的光晕
                ctx.FillEllipse(new Ellipse(new Vector2(12f, 12f), 7.5f, 7.5f),
                                BrushOf(ctx, new Color4(0.95f, 0.18f, 0.18f, 0.22f)));
                ctx.FillEllipse(new Ellipse(new Vector2(12f, 12f), 3.2f, 3.2f),
                                BrushOf(ctx, new Color4(0.95f, 0.18f, 0.18f, 1f)));
                break;
        }

        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘图标偶尔需要一个**固定颜色**的画刷（比如那颗红点：它不是"图标色"，
    /// 它就是激光本身的颜色）。按颜色缓存，不每帧重建。
    /// </summary>
    private static ID2D1SolidColorBrush BrushOf(ID2D1DeviceContext ctx, Color4 c)
    {
        uint key = ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16)
                 | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);
        if (_fixed.TryGetValue(key, out var b)) return b;
        b = ctx.CreateSolidColorBrush(c, null);
        _fixed[key] = b;
        return b;
    }

    private static readonly Dictionary<uint, ID2D1SolidColorBrush> _fixed = new();

    /// <summary>产品里用哪一个激光笔图标（改这一个数字就能换）。</summary>
    public const int LaserDefault = 2;

    /// <summary>
    /// **自绘的两种橡皮图标**（<paramref name="area"/> = 面积擦 / 像素橡皮）。
    ///
    /// 为什么不用 Fluent 的 Eraser（用户 2026-09-17："笔迹擦除的图标不合理"）：
    /// 它是一块**斜着的圆角方块**，20 像素下读起来像个菱形 / 一片叶子，
    /// 而且整笔擦和面积擦**用的是同一个图标**——两个行为完全不同的工具长得一模一样，
    /// 老师只能靠上面的文字分。
    ///
    /// 现在两个分开画，各自的图形就是它**在屏幕上的样子**：
    ///   · 整笔擦：一块橡皮压在一条线上，线**从橡皮底下钻出来**（碰到哪条就整条没了）；
    ///   · 面积擦：**竖着的黄金比例矩形**＋中心十字＋一层淡填充——和落点光标
    ///     （`Overlay.DrawEraserRectCursor`）是同一个形状，老师一眼对得上。
    ///
    /// 网格 24、线宽 1.8～2.6，和自绘的激光笔同一套手感。
    /// </summary>
    public static void DrawEraser(ID2D1DeviceContext ctx, RectF box, float size,
                                 ID2D1Brush brush, bool area)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;

        if (area)
        {
            // **虚线框**（竖着的黄金比例矩形：9 × 14.6）。
            //
            // 第一版画的是"实线框＋中心十字"，出图一看**像个加号按钮**（12 像素宽的框
            // 在 20 像素的按钮里几乎成了正方形，"＋"又最抢眼）。改成虚线框之后：
            // 虚线是各家通用的"一块区域"的说法，和落点那个矩形是同一个意思，
            // 也不会再和"加号/放大"混。
            float hw = 4.5f, hh = 7.3f;
            const float dl = 2.6f, gp = 2.1f, lw = 1.8f;
            DashedLine(ctx, new Vector2(12f - hw, 12f - hh), new Vector2(12f + hw, 12f - hh), dl, gp, lw, brush);
            DashedLine(ctx, new Vector2(12f + hw, 12f - hh), new Vector2(12f + hw, 12f + hh), dl, gp, lw, brush);
            DashedLine(ctx, new Vector2(12f + hw, 12f + hh), new Vector2(12f - hw, 12f + hh), dl, gp, lw, brush);
            DashedLine(ctx, new Vector2(12f - hw, 12f + hh), new Vector2(12f - hw, 12f - hh), dl, gp, lw, brush);
        }
        else
        {
            // 先画那条**笔画**（横着一条），再用**实心**橡皮块把它的左半截压住——
            // 看起来就是"线从橡皮底下钻出来"，一眼明白"碰到就整条没了"。
            //
            // 橡皮用**实心**（不是描边）：20 像素的按钮里，描边的斜方块会糊成一圈线，
            // 实心的块才读得出来"这是一块橡皮"（出图比过两版）。
            // 线的起点故意留在方块**里面**（y=9.2 时方块占 x∈[10.3,13.4]），
            // 不然会从方块左上角外面露出一小截，像线穿过去了。
            ctx.DrawLine(new Vector2(11.0f, 9.2f), new Vector2(21.2f, 9.2f), brush, 2.6f, _round);
            var body = EraserBody();
            if (body != null) ctx.FillGeometry(body, brush);
        }

        ctx.Transform = saved;
    }

    /// <summary>锥形光束那片半透明填充（建一次）。</summary>
    private static ID2D1PathGeometry Cone()
    {
        if (_cone != null) return _cone;
        if (_factory == null) return null;
        _cone = _factory.CreatePathGeometry();
        using (var sink = _cone.Open())
        {
            sink.BeginFigure(new Vector2(6f, 18f), FigureBegin.Filled);
            sink.AddLine(new Vector2(20.5f, 3.5f));
            sink.AddLine(new Vector2(21.5f, 13.5f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return _cone;
    }

    /// <summary>
    /// 整笔橡皮那块**斜 42° 的实心方块**（24 网格里的一副固定坐标，建一次）。
    /// 和 <see cref="Cone"/> 一样：自绘图标要填一块形状时用它，不每帧重建几何。
    /// </summary>
    private static ID2D1PathGeometry EraserBody()
    {
        if (_eraserBody != null) return _eraserBody;
        if (_factory == null) return null;

        var c = new Vector2(10.2f, 14.0f);
        float r = -42f * MathF.PI / 180f;
        var ax = new Vector2(MathF.Cos(r), MathF.Sin(r));   // 长轴（指向右上）
        var pe = new Vector2(-ax.Y, ax.X);                   // 短轴
        const float hl = 5.6f, hw = 3.5f;
        var p1 = c + ax * hl + pe * hw;
        var p2 = c + ax * hl - pe * hw;
        var p3 = c - ax * hl - pe * hw;
        var p4 = c - ax * hl + pe * hw;

        var g = _factory.CreatePathGeometry();
        using (var sink = g.Open())
        {
            // 画成**两块**，中间留一道 1.1 像素的缝——就是橡皮上那道"用到哪儿"的分界。
            // 用"几何留缝"而不是"再画一条背景色的线"：图标底色可能是面板底、
            // 也可能是选中态的强调色，背景色画不对就成了脏点；留缝是**真的透过去**，
            // 两种底色下都对。
            Quad(sink, ax, pe, hw, -hl, -hl * 0.30f);
            Quad(sink, ax, pe, hw, -hl * 0.10f, hl);
            sink.Close();
        }
        _eraserBody = g;
        return _eraserBody;
    }

    /// <summary>往几何里加一块"长轴从 a 到 b、半宽 hw"的矩形（自绘图标拼形状用）。</summary>
    private static void Quad(ID2D1GeometrySink sink, Vector2 ax, Vector2 pe, float hw, float a, float b)
    {
        var c0 = ax * a; var c1 = ax * b; var w = pe * hw;
        sink.BeginFigure(c0 + w, FigureBegin.Filled);
        sink.AddLine(c1 + w);
        sink.AddLine(c1 - w);
        sink.AddLine(c0 - w);
        sink.EndFigure(FigureEnd.Closed);
    }

    private static ID2D1PathGeometry _eraserBody;

    /// <summary>虚线：自绘图标画"一块区域"时用（面积橡皮）。圆头线头，比分段方头好看。</summary>
    private static void DashedLine(ID2D1DeviceContext ctx, Vector2 a, Vector2 b,
                                   float dash, float gap, float w, ID2D1Brush brush)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 0.01f) return;
        d /= len;
        for (float s = 0f; s < len; s += dash + gap)
        {
            float e = MathF.Min(s + dash, len);
            ctx.DrawLine(a + d * s, a + d * e, brush, w, _round);
        }
    }
}
