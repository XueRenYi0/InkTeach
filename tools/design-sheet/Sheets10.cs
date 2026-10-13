using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v10：选中框在**移动 / 旋转**时的画法对照。
///
/// 这张稿要回答的是"拖起来看不清楚、像卡住"这件事从哪儿来，以及三个方案各长什么样：
///   · 现在：框、8 个手柄、旋转柄、操作条**全都跟着动**；而操作条贴到屏幕下边会
///     **停在原地不动**（引擎 `SelectionHandles.BarRect` 的"给下限、不翻面"），
///     内容继续走 —— 就是最像 bug 的那一幕；
///   · 方案 A：拖动 / 旋转期间把"此刻点不中"的装饰收起来，只留框 + 光晕；
///   · 方案 B：被拖的内容从内容层里摘出来、画在最上层 —— 它自己就"浮"起来了；
///   · 方案 C（可选档）：其余内容压暗一档，落点看得更清。
///
/// 面板里的尺寸一律是引擎的**逻辑像素 1:1**，抄的时候不用换算：
/// 手柄 14、旋转柄 20、光晕 6.5、描边 2.5、操作条 200×34（4×46 + 2×5 + 3×2）、
/// 操作条离框 14、离屏幕下边不小于 12、旋转柄在上边中点外侧 30。
/// </summary>
internal static partial class Program
{
    const double SelPanelW = 700;
    const double SelPanelH = 300;

    // ---- 引擎里那几个数的复刻（改引擎就要改这里，所以只写一遍）----
    const double SelHandle = 14;          // SelectionHandles.VisualSizeLogical
    const double SelGripD = 20;           // RotateGripLogical
    const double SelGripOffset = 30;      // RotateOffsetLogical
    const double SelGlow = 6.5;           // DrawSelection 里的光晕宽度
    const double SelStroke = 2.5;         // DrawSelection 里的描边宽度
    const double SelBarW = 200;           // BarButtonCount(4) × 46 + 2 × 5 + 3 × 2
    const double SelBarH = 34;            // BarHeightLogical
    const double SelBarGap = 14;          // BarOffsetLogical
    const double SelBarBottom = 12;       // BarMinBottomMarginLogical

    static readonly Color SelAccent = C(0x00, 0x78, 0xD4);
    static readonly Color SelInk = C(0x1B, 0x1B, 0x1F);
    static readonly Color SelRed = C(0xF2, 0x2E, 0x2E);
    static readonly Color SelFaint = C(0x8A, 0x90, 0x99);

    enum SelShot
    {
        NowMoveBottom,   // 现在 · 移动中（选区贴屏幕下边：条停住不动）
        NowRotate,       // 现在 · 旋转中
        PlanAMove,       // 方案 A · 移动中（收装饰）
        PlanARotate,     // 方案 A · 旋转中（收装饰 + 留度数）
        PlanBFloat,      // 方案 B · 拖动中（选区浮起来：内容画在最上层）
        PlanCSpot,       // 方案 C · 可选（其余压暗一档）
    }

    static double DrawSelDragSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "选中框 · 移动 / 旋转时的画法", 40, y, 30, TitleBrush, bold: true);
        Text(c, "上排是“现在”的画法，中、下排是三个方案。灰 = 没被选中的板书与下层画面；红 = 被选中的那一块；蓝 = 选中框。",
             40, y + 44, 14, BodyBrush);
        Text(c, "面板里的尺寸是引擎的逻辑像素 1:1（手柄 14 / 旋转柄 20 / 光晕 6.5 / 描边 2.5 / 操作条 200×34 / 条离框 14 / 离屏幕下边 12）。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        double xL = 40, xR = 40 + SelPanelW + 60;

        SelPanel(c, xL, y, SelShot.NowMoveBottom, "现在 · 移动中（选区贴着屏幕下边）",
            "操作条在引擎里是“给下限、不翻面”：下边到了离屏幕 12px 就停住。内容继续往下走、条留在原地 —— 框和条当场分家，投影上看着就像卡住了。");
        SelPanel(c, xR, y, SelShot.NowRotate, "现在 · 旋转中",
            "8 个缩放手柄、旋转柄、上边那条连线、操作条全跟着框一起转；度数标签贴在旋转柄外侧（吸住时整块变强调色）。画面里除了被转的那块，还有四件家具在动。");
        y += SelPanelH + 84;

        SelPanel(c, xL, y, SelShot.PlanAMove, "方案 A · 移动中：把“点不中”的东西收起来",
            "鼠标已经被拖拽接管，手柄和操作条此刻“根本点不中” —— 留着就是“看得见点不到”。只留框 + 光晕，画面上只有一条线跟着内容走。");
        SelPanel(c, xR, y, SelShot.PlanARotate, "方案 A · 旋转中：留柄与度数",
            "旋转柄标着“我抓的是这个”，度数标签给的是读数（图为没吸住的 -43°，白底；吸住时变强调色），这两个留；8 个手柄与操作条收起来。");
        y += SelPanelH + 84;

        SelPanel(c, xL, y, SelShot.PlanBFloat, "方案 B · 拖动中：被拖的那块“浮”起来",
            "这一档是实现方式带来的：拖动期间把选区从内容层里摘出来、画在最上层，于是它自动比周围亮一档、带一层很淡的投影。副作用是拖动中 z 序暂时在最上（松手回归）。");
        SelPanel(c, xR, y, SelShot.PlanCSpot, "方案 C（可选档）· 其余压暗一档",
            "整屏压一层很淡的黑（12%~18%），被拖的那块保持原色。“压暗”只能给一点点：我们是盖在别人 PPT 上的一层，盖多了就变成“换了张纸”。默认建议关。");
        y += SelPanelH + 92;

        var box = new Rect(40, y, SheetW - 80, 150);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "这三排各自解决什么", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "A 解决“看着乱、像卡住”：拖动/旋转期间本体只剩一条框（外加旋转时的柄与度数）。便宜，一天之内能上，判据是“拖动中手柄与操作条的像素不在屏幕上、松手回来”。",
            "B 解决“最坏一帧”：现在拖动每帧都要把“走过的面积”里的所有笔迹重画一遍（散布全屏的一小撮实测 19ms/帧）；B 之后每帧只重画选区自己那一块，顺带得到“浮起来”的层次。",
            "C 只是观感档位，且因为我们盖在 PPT 上、压暗会连下层画面一起压，建议默认关；真要“只留选区”的口味，宁可用它的极端版（隐藏其余）当设置项，不进默认路径。",
        };
        double cy = y + 44;
        foreach (string line in concl) { cy += Paragraph(c, line, 60, cy, SheetW - 120, 12.5, BodyBrush) + 3; }
        return y + 166;
    }

    // =====================================================================
    //  一张面板：场景 + 标题 + 说明
    // =====================================================================

    static void SelPanel(DrawingContext c, double x, double y, SelShot shot, string title, string note)
    {
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1),
                               new Rect(x, y, SelPanelW, SelPanelH), 8, 8);
        c.PushClip(new RectangleGeometry(new Rect(x, y, SelPanelW, SelPanelH), 8, 8));
        SelScene(c, x, y, shot);
        c.Pop();

        double ty = y + SelPanelH + 12;
        Text(c, title, x, ty, 15, TitleBrush, bold: true);
        Paragraph(c, note, x, ty + 24, SelPanelW, 12.5, NoteBrush);
    }

    // =====================================================================
    //  场景：一层假 PPT + 别人的墨 + 被选中的那一块 + 选中框
    // =====================================================================

    static void SelScene(DrawingContext c, double x, double y, SelShot shot)
    {
        bool rotate = shot == SelShot.NowRotate || shot == SelShot.PlanARotate;
        bool chrome = shot == SelShot.NowMoveBottom || shot == SelShot.NowRotate;
        bool scrim = shot == SelShot.PlanCSpot;
        bool lift = shot == SelShot.PlanBFloat || shot == SelShot.PlanCSpot;

        // 1) 下层画面（讲 PPT 的人真正在看的东西）
        SelSlide(c, x, y);
        // 2) 没被选中的板书
        SelOtherInk(c, x, y);
        // 3) 可选档：把"其余"压暗一档（连着下层画面一起，这是我们的物理限制）
        if (scrim)
            c.DrawRectangle(new SolidColorBrush(C(0x08, 0x0A, 0x0E, 0x30)), null,
                            new Rect(x, y, SelPanelW, SelPanelH));

        // 4) 被选中的那一块
        var b = shot == SelShot.NowMoveBottom
            ? new Rect(x + 190, y + 116, 320, 152)     // 贴屏幕下边：条会停住
            : rotate ? new Rect(x + 200, y + 78, 300, 148)
                     : new Rect(x + 190, y + 62, 320, 156);

        if (rotate)
            c.PushTransform(new RotateTransform(-12, b.X + b.Width / 2, b.Y + b.Height / 2));

        if (lift) SelLift(c, b);
        SelContent(c, b);
        SelFrame(c, b);
        if (chrome) SelHandles(c, b);
        if (chrome || rotate) SelGrip(c, b);
        if (rotate) SelReadout(c, b, chrome ? "90°" : "-43°", snapped: chrome);
        if (rotate) c.Pop();

        // 5) 操作条：注意它是**轴对齐**的，跟着框在画布上的轴对齐范围走，不跟着转
        if (chrome)
            SelBar(c, SelAabb(b, rotate), x, y, shot == SelShot.NowMoveBottom);
    }

    /// <summary>假 PPT：一张干净的浅色画面，用来当"下层内容"。</summary>
    static void SelSlide(DrawingContext c, double x, double y)
    {
        c.DrawRectangle(new SolidColorBrush(C(0xF4, 0xF6, 0xF9)), null,
                        new Rect(x, y, SelPanelW, SelPanelH));
        c.DrawRectangle(new SolidColorBrush(C(0x21, 0x73, 0xE6)), null, new Rect(x, y, SelPanelW, 6));
        Text(c, "第 3 章 · 函数的单调性", x + 28, y + 26, 17, new SolidColorBrush(C(0x24, 0x2A, 0x33)), bold: true);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xDD, 0xE2, 0xEA)), null,
                               new Rect(x + 28, y + 56, 250, 9), 4, 4);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xE6, 0xEA, 0xF0)), null,
                               new Rect(x + 28, y + 74, 180, 9), 4, 4);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xE6, 0xEA, 0xF0)), null,
                               new Rect(x + 430, y + 210, 240, 9), 4, 4);
    }

    /// <summary>别人的墨：一块没被选中的板书（灰、不参与拖动）。</summary>
    static void SelOtherInk(DrawingContext c, double x, double y)
    {
        // 荧光笔划的一道
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xE0, 0x3A, 0x88)), null,
                               new Rect(x + 30, y + 96, 300, 20), 10, 10);
        // 一行手写
        var pen = new Pen(new SolidColorBrush(C(0x2A, 0x2E, 0x36)), 3.2)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x + 34, y + 200), false, false);
            g.BezierTo(new Point(x + 70, y + 240), new Point(x + 120, y + 160), new Point(x + 168, y + 208), true, false);
            g.BezierTo(new Point(x + 200, y + 238), new Point(x + 240, y + 176), new Point(x + 288, y + 214), true, false);
        }
        geo.Freeze();
        c.DrawGeometry(null, pen, geo);

        // 右下角一道淡淡的备注
        var pen2 = new Pen(new SolidColorBrush(C(0x9A, 0xA0, 0xA8)), 2.4)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geo2 = new StreamGeometry();
        using (var g = geo2.Open())
        {
            g.BeginFigure(new Point(x + 470, y + 262), false, false);
            g.BezierTo(new Point(x + 520, y + 244), new Point(x + 580, y + 286), new Point(x + 650, y + 258), true, false);
        }
        geo2.Freeze();
        c.DrawGeometry(null, pen2, geo2);
    }

    /// <summary>被选中的那一块：画得比周围"重"一点（红），一眼能分出谁是主角。</summary>
    static void SelContent(DrawingContext c, Rect b)
    {
        var pen = new Pen(new SolidColorBrush(SelRed), 4.4)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            double l = b.X + b.Width * 0.14, r = b.X + b.Width * 0.90;
            double t = b.Y + b.Height * 0.30, bo = b.Y + b.Height * 0.74;
            g.BeginFigure(new Point(l, bo), false, false);
            g.BezierTo(new Point(l + b.Width * 0.18, t - b.Height * 0.06),
                       new Point(r - b.Width * 0.20, bo + b.Height * 0.08), new Point(r, t), true, false);
        }
        geo.Freeze();
        c.DrawGeometry(null, pen, geo);

        // 一块被圈起来的框（学生常画的"重点框"）
        c.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(SelRed), 3.0),
                               new Rect(b.X + b.Width * 0.16, b.Y + b.Height * 0.12, b.Width * 0.62, b.Height * 0.34), 5, 5);
    }

    /// <summary>方案 B/C 的那层"浮起来"：一层很淡的投影，不进内容层。</summary>
    static void SelLift(DrawingContext c, Rect b)
    {
        for (int i = 4; i >= 1; i--)
            c.DrawRoundedRectangle(new SolidColorBrush(C(0x0A, 0x0C, 0x10, (byte)(0x09 + i * 7))), null,
                                   new Rect(b.X - 3, b.Y - 2 + i * 1.7, b.Width + 6, b.Height + 6), 10, 10);
    }

    /// <summary>选中框：光晕 + 描边（和 Overlay.DrawSelection 同规格）。</summary>
    static void SelFrame(DrawingContext c, Rect b)
    {
        c.DrawRectangle(null, new Pen(new SolidColorBrush(C(SelAccent.R, SelAccent.G, SelAccent.B, 0x2A)), SelGlow), b);
        c.DrawRectangle(null, new Pen(new SolidColorBrush(SelAccent), SelStroke), b);
    }

    /// <summary>八个缩放手柄：白底 + 蓝边（14 逻辑像素）。</summary>
    static void SelHandles(DrawingContext c, Rect b)
    {
        double h = SelHandle, r = h * 0.28;
        Point[] pts =
        {
            new Point(b.Left, b.Top), new Point(b.Left + b.Width / 2, b.Top), new Point(b.Right, b.Top),
            new Point(b.Right, b.Top + b.Height / 2), new Point(b.Right, b.Bottom),
            new Point(b.Left + b.Width / 2, b.Bottom), new Point(b.Left, b.Bottom),
            new Point(b.Left, b.Top + b.Height / 2),
        };
        foreach (var p in pts)
            c.DrawRoundedRectangle(new SolidColorBrush(Colors.White),
                                   new Pen(new SolidColorBrush(SelAccent), 1.8),
                                   new Rect(p.X - h / 2, p.Y - h / 2, h, h), r, r);
    }

    /// <summary>旋转柄：上边中点外侧 30，白圆 + 蓝边 + 官方图标 + 一条连线。</summary>
    static void SelGrip(DrawingContext c, Rect b)
    {
        var top = new Point(b.Left + b.Width / 2, b.Top);
        var g = new Point(top.X, top.Y - SelGripOffset);
        c.DrawLine(new Pen(new SolidColorBrush(SelAccent), 1.4), top, g);
        c.DrawEllipse(new SolidColorBrush(Colors.White), new Pen(new SolidColorBrush(SelAccent), 1.6),
                      g, SelGripD / 2, SelGripD / 2);
        SelRotateGlyph(c, g, SelGripD * 0.34, new SolidColorBrush(SelAccent), 1.5);
    }

    /// <summary>度数标签：白底＝自己转到的，强调色＝被吸住的（和引擎同一套语言）。</summary>
    static void SelReadout(DrawingContext c, Rect b, string text, bool snapped)
    {
        double w = 62, h = 30;
        var g = new Point(b.Left + b.Width / 2, b.Top - SelGripOffset);
        var box = new Rect(g.X - w / 2, g.Y - h - 12, w, h);
        var fill = snapped ? new SolidColorBrush(SelAccent) : new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0xF0));
        var pen = snapped ? new SolidColorBrush(Colors.White) : new SolidColorBrush(SelAccent);
        c.DrawRoundedRectangle(fill, new Pen(pen, 1.5), box, h / 2, h / 2);
        var ft = Fmt(text, 15, snapped ? Brushes.White : new SolidColorBrush(C(0x1A, 0x1F, 0x28)), bold: true);
        ft.TextAlignment = TextAlignment.Center;
        c.DrawText(ft, new Point(box.X, box.Y + 6));
    }

    /// <summary>
    /// 操作条（复制 / 删除 / 左右翻转 / 上下翻转）。**轴对齐**，贴在框的轴对齐范围下方 14；
    /// 下边到了离屏幕下边 12 就停住 —— <paramref name="stuck"/> = 画"停住"那一幕。
    /// </summary>
    static void SelBar(DrawingContext c, Rect aabb, double px, double py, bool stuck)
    {
        double x = aabb.Left + aabb.Width / 2 - SelBarW / 2;
        double y = Math.Min(aabb.Bottom + SelBarGap, py + SelPanelH - SelBarBottom - SelBarH);
        x = Math.Max(px + 8, Math.Min(x, px + SelPanelW - 8 - SelBarW));

        Panel(c, x, y, SelBarW, SelBarH, 8, false, 0xF5, true);

        var ink = new SolidColorBrush(C(0x24, 0x26, 0x2B));
        var sepc = C(0xE2, 0xE5, 0xEA);
        for (int i = 0; i < 4; i++)
        {
            double bx = x + 5 + i * 48;
            var key = new Point(bx + 23, y + SelBarH / 2);
            switch (i)
            {
                case 0: SelCopyGlyph(c, key, 20, ink); break;
                case 1: Icon(c, "delete", key.X, key.Y, 19, ink); break;
                case 2: SelFlipGlyph(c, key, 20, ink, true); break;
                case 3: SelFlipGlyph(c, key, 20, ink, false); break;
            }
            if (i < 3)
                c.DrawLine(new Pen(new SolidColorBrush(sepc), 1),
                           new Point(bx + 48, y + 8), new Point(bx + 48, y + SelBarH - 8));
        }

        if (!stuck) return;
        // 把"条停住、框继续走"点出来：一条虚线引到右边的说明
        var from = new Point(x + SelBarW, y + SelBarH / 2);
        var to = new Point(x + SelBarW + 86, y + SelBarH / 2);
        var dash = new Pen(new SolidColorBrush(C(0xE0, 0x62, 0x2A)), 1.2) { DashStyle = DashStyles.Dash };
        c.DrawLine(dash, from, to);
        Text(c, "条停在这儿不动了", to.X + 6, to.Y - 8, 12,
             new SolidColorBrush(C(0xD0, 0x52, 0x1C)), bold: true);
    }

    /// <summary>框在面板里的轴对齐范围（旋转那两幅要自己算，操作条本来就按这个摆）。</summary>
    static Rect SelAabb(Rect b, bool rotate)
    {
        if (!rotate) return b;
        double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        double deg = -12 * Math.PI / 180.0, cos = Math.Cos(deg), sin = Math.Sin(deg);
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, bo = double.MinValue;
        foreach (var p in new[]
        {
            new Point(b.Left, b.Top), new Point(b.Right, b.Top),
            new Point(b.Right, b.Bottom), new Point(b.Left, b.Bottom),
        })
        {
            double dx = p.X - cx, dy = p.Y - cy;
            double nx = cx + dx * cos - dy * sin, ny = cy + dx * sin + dy * cos;
            l = Math.Min(l, nx); r = Math.Max(r, nx); t = Math.Min(t, ny); bo = Math.Max(bo, ny);
        }
        return new Rect(l, t, r - l, bo - t);
    }

    // ---- 操作条上的四个图标（引擎用的是 Fluent 的路径数据，出图工具里手画等价形状）----

    static void SelCopyGlyph(DrawingContext c, Point center, double s, Brush brush)
    {
        var pen = new Pen(brush, 1.5);
        c.DrawRoundedRectangle(null, pen, new Rect(center.X - s * 0.46, center.Y - s * 0.30, s * 0.62, s * 0.72), 2.5, 2.5);
        c.DrawRoundedRectangle(null, pen, new Rect(center.X - s * 0.12, center.Y - s * 0.46, s * 0.62, s * 0.72), 2.5, 2.5);
    }

    static void SelFlipGlyph(DrawingContext c, Point center, double s, Brush brush, bool horizontal)
    {
        var pen = new Pen(brush, 1.4);
        double k = s * 0.44;
        Point[] solid = horizontal
            ? new[] { new Point(center.X - k, center.Y - k), new Point(center.X + k * 0.72, center.Y), new Point(center.X - k, center.Y + k) }
            : new[] { new Point(center.X - k, center.Y - k), new Point(center.X, center.Y + k * 0.72), new Point(center.X + k, center.Y - k) };
        Point[] outline = horizontal
            ? new[] { new Point(center.X + k, center.Y - k), new Point(center.X - k * 0.72, center.Y), new Point(center.X + k, center.Y + k) }
            : new[] { new Point(center.X - k, center.Y + k), new Point(center.X, center.Y - k * 0.72), new Point(center.X + k, center.Y + k) };
        c.DrawGeometry(brush, null, SelPoly(solid));
        c.DrawGeometry(null, pen, SelPoly(outline));
    }

    static void SelRotateGlyph(DrawingContext c, Point center, double r, Brush brush, double w)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(center.X + r * 0.75, center.Y - r * 0.66), false, false);
            g.ArcTo(new Point(center.X - r * 0.75, center.Y - r * 0.66), new Size(r, r), 0, false,
                    SweepDirection.Counterclockwise, true, false);
            g.BeginFigure(new Point(center.X + r * 1.05, center.Y - r * 0.18), true, true);
            g.LineTo(new Point(center.X + r * 0.34, center.Y - r * 0.92), true, false);
            g.LineTo(new Point(center.X + r * 0.16, center.Y - r * 0.10), true, false);
        }
        geo.Freeze();
        c.DrawGeometry(null, new Pen(brush, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }

    static Geometry SelPoly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }
}
