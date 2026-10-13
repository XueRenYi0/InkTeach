using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v3：两件"收尾"的事 —— 激光笔图标换哪个，以及白块/黑块在浅底深底上怎么才看得见。
/// </summary>
internal static partial class Program
{
    static double DrawIconSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "图标与色块 · 方案对比（设计稿 v3）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "你点名的两件事：激光笔图标不好看；白块在浅底、黑块在深底上分不出来。",
             40, y + 44, 14, BodyBrush);
        Text(c, "左边的数都是 24 网格绘到 20 —— 和工具条里的真实尺寸一致。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ① 激光笔候选
        y = Section(c, y, "① 激光笔：上游没有这个专名，只有近义图标和「自己拼」两条路");
        (string Title, string Note, bool Rec, Action<DrawingContext, double, double, double, Brush> Draw)[] laser =
        {
            ("① Flash 闪电", "上游现成、小尺寸最好认。语义是「闪光/快充」，但同类软件常拿它当激光笔。", false,
                (c, cx, cy, sz, b) => Icon(c, "laserFlash", cx, cy, sz, b)),
            ("② Flashlight 手电筒", "有「发光」的意思，但更像手电，不像一支笔。", false,
                (c, cx, cy, sz, b) => Icon(c, "laserFlashlight", cx, cy, sz, b)),
            ("③ Record 圆点", "就是「光点」本身，语义最直白；风险是容易被当成「录制」。", false,
                (c, cx, cy, sz, b) => Icon(c, "laserRecord", cx, cy, sz, b)),
            ("④ Target 靶心", "像瞄准/定位，辨识度高；语义偏「目标」而不是「激光」。", false,
                (c, cx, cy, sz, b) => Icon(c, "laserTarget", cx, cy, sz, b)),
            ("⑤ 光束锥（自制）", "一个光点 + 一束斜射出去的光 —— 语义最准，也不撞其它图标。", true,
                (c, cx, cy, sz, b) => LaserBeam(c, cx, cy, sz, b)),
            ("⑥ 光点＋短射线", "像「亮度/发光」，容易和「显示亮度」混。", false,
                (c, cx, cy, sz, b) => LaserRays(c, cx, cy, sz, b)),
            ("⑦ 光点＋三道弧", "像 Wi-Fi 信号 —— 就是现在假面板里那个，建议换掉。", false,
                (c, cx, cy, sz, b) => LaserArcs(c, cx, cy, sz, b)),
        };

        double ly = y + 8;
        foreach (var it in laser)
        {
            Text(c, it.Title, 60, ly + 32, 13, it.Rec ? new SolidColorBrush(Accent) : BodyBrush, bold: it.Rec);
            var brush = new SolidColorBrush(it.Rec ? Accent : Ink);
            it.Draw(c, 330, ly + 40, 20, brush);         // 1:1
            IconFrame(c, 330, ly + 40, 40);
            it.Draw(c, 430, ly + 40, 60, brush);         // 3×
            IconFrame(c, 430, ly + 40, 80);
            Text(c, "1:1", 312, ly + 56, 11, NoteBrush);
            Text(c, "3×", 412, ly + 76, 11, NoteBrush);
            Paragraph(c, it.Note, 500, ly + 30, 900, 12, it.Rec ? new SolidColorBrush(Accent) : NoteBrush);
            ly += 84;
        }

        y = ly + 6;

        // ② 其余图标复核
        y = Section(c, y, "② 其余 11 项复核：只有「截屏」还值得商量，别的都可以留下");
        Text(c, "每格里：上排 regular ＝ 常态，下排 filled ＝ 选中（这就是 Windows 11 自己的惯例：选中换填充版，不是加个框）。",
             60, y + 2, 12.5, BodyBrush);
        (string Icon, string Filled, string Name)[] tools =
        {
            ("mouse", "mouseFilled", "鼠标"), ("pen", "penFilled", "笔"), ("highlighter", "highlighterFilled", "荧光笔"),
            ("laserFlash", "laserFilled", "激光笔"), ("eraser", "eraserFilled", "橡皮擦"),
            ("select", "selectFilled", "选择"), ("shapes", "shapesFilled", "图形"), ("capture", "captureFilled", "截屏"),
            ("undo", "undoFilled", "后撤"), ("redo", "redoFilled", "重做"), ("settings", "settingsFilled", "设置"),
        };
        double tx = 60;
        foreach (var t in tools)
        {
            Panel(c, tx, y + 26, 56, 92, 12, false, PanelAlpha, true);
            Icon(c, t.Icon, tx + 28, y + 50, 20, new SolidColorBrush(Ink));
            Icon(c, t.Filled, tx + 28, y + 82, 20, new SolidColorBrush(Ink));
            Caption(c, t.Name, tx - 6, y + 122, 68);
            tx += 66;
        }

        Text(c, "截屏 = Screenshot（方框 + 圆点）确实偏抽象，但换成 Crop/相机，语义就更偏「裁剪/拍照」了。",
             60, y + 152, 12.5, NoteBrush);
        Text(c, "建议：先按现状做，等你上手看过再定 —— 图标换名字是一行代码的事。",
             60, y + 172, 12.5, NoteBrush);
        y += 200;

        // ③ 色块可见性
        y = Section(c, y, "③ 白块在浅底、黑块在深底：三档处理");
        Text(c, "浅底（面板是白 80%）", 60, y + 4, 13, TitleBrush, bold: true);
        Text(c, "深底（面板是 #202020 80%）", 800, y + 4, 13, TitleBrush, bold: true);

        string[] labels =
        {
            "A 不处理：白块直接融进面板，只剩一个空格（就是你看到的问题）",
            "B 只给每块加描边：能看见格子，但块本身和底还是一体的",
            "C 极值色退一档/提一档 + 描边 + 底下垫槽（推荐）",
        };
        for (int i = 0; i < 3; i++)
        {
            double ry = y + 30 + i * 86;
            Text(c, labels[i], 60, ry, 12.5, i == 2 ? new SolidColorBrush(Accent) : BodyBrush, bold: i == 2);
            RailSample(c, 60, ry + 30, 640, 28, i, dark: false);
            RailSample(c, 760, ry + 30, 640, 28, i, dark: true);
        }
        y += 30 + 3 * 86 + 6;

        // ④ 结论
        var box = new Rect(40, y, SheetW - 80, 130);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "④ 这一版定了什么", 60, y + 16, 15, TitleBrush, bold: true);
        string[] done =
        {
            "色块规则：浅底不用纯白、深底不用纯黑 —— 换成「退一档/提一档 + 描边 + 垫槽」，三种手段一起上才稳。",
            "注意区分两件事：色块**显示**的颜色会随主题微调，但真正画出去的**笔色**不变（白笔就是纯白）。",
            "激光笔：推荐 ⑤ 光束锥（自制，线宽和 Fluent 对齐）；想省事就用 ① Flash。假面板里按 L 可以逐个换着看。",
            "其余图标不动。常态 regular、选中 filled 已经在假面板里实现了。",
        };
        double cy = y + 46;
        foreach (string line in done)
        {
            Text(c, line, 60, cy, 13, BodyBrush);
            cy += 22;
        }
        return y + 144;
    }

    // ---- 激光笔候选的画法 -------------------------------------------------

    static void LaserBeam(DrawingContext c, double cx, double cy, double size, Brush b)
    {
        double s = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(s, s));
        var pen = new Pen(b, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var dot = new Point(6, 18);
        // 先铺一层半透明的锥（小尺寸下有"体量"才看得清），再压两条边界和一个光点
        var cone = new StreamGeometry();
        using (var g = cone.Open())
        {
            g.BeginFigure(dot, true, true);
            g.LineTo(new Point(20.5, 3.5), true, false);
            g.LineTo(new Point(21.5, 13.5), true, false);
        }
        cone.Freeze();
        c.DrawGeometry(new SolidColorBrush(Blend(b, 0x4D)), null, cone);
        c.DrawLine(pen, new Point(7.6, 16.4), new Point(20.5, 3.5));
        c.DrawLine(pen, new Point(8.4, 18.4), new Point(21.5, 13.5));
        c.DrawEllipse(b, null, dot, 2.7, 2.7);
        c.Pop();
        c.Pop();
    }

    static Color Blend(Brush b, byte alpha)
    {
        var col = (b as SolidColorBrush)?.Color ?? Colors.Black;
        return Color.FromArgb(alpha, col.R, col.G, col.B);
    }

    static void LaserRays(DrawingContext c, double cx, double cy, double size, Brush b)
    {
        double s = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(s, s));
        var pen = new Pen(b, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var dot = new Point(12, 12);
        c.DrawEllipse(b, null, dot, 3.0, 3.0);
        foreach (double a in new[] { -90.0, -40, 10, 55, 125, 180, 235 })
        {
            double r = a * Math.PI / 180.0;
            c.DrawLine(pen,
                new Point(dot.X + Math.Cos(r) * 6.0, dot.Y + Math.Sin(r) * 6.0),
                new Point(dot.X + Math.Cos(r) * 9.2, dot.Y + Math.Sin(r) * 9.2));
        }
        c.Pop();
        c.Pop();
    }

    static void LaserArcs(DrawingContext c, double cx, double cy, double size, Brush b)
    {
        double s = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(s, s));
        var pen = new Pen(b, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var dot = new Point(6.5, 17.5);
        c.DrawEllipse(b, null, dot, 2.6, 2.6);
        foreach (double rad in new[] { 7.0, 11.0, 15.0 })
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(Arc(dot, rad, -96), false, false);
                gc.ArcTo(Arc(dot, rad, -18), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            c.DrawGeometry(null, pen, g);
        }
        c.Pop();
        c.Pop();
    }

    static Point Arc(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180.0;
        return new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
    }

    static void IconFrame(DrawingContext c, double cx, double cy, double box)
        => c.DrawRectangle(null, new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1),
                           new Rect(cx - box / 2, cy - box / 2, box, box));

    /// <summary>一段 9 色腰线的样品：treatment 0 不处理 / 1 加描边 / 2 退提一档＋槽。</summary>
    static void RailSample(DrawingContext c, double x, double y, double w, double h, int treatment, bool dark)
    {
        Panel(c, x - 10, y - 10, w + 20, h + 20, 12, dark, PanelAlpha, true);
        Color panelFill = dark ? C(0x20, 0x20, 0x22) : C(0xFF, 0xFF, 0xFF);
        if (treatment == 2)
            c.DrawRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x1F) : C(0x00, 0x00, 0x00, 0x1A)),
                            null, new Rect(x, y, w, h));

        double seg = w / 9;
        for (int i = 0; i < 9; i++)
        {
            bool extreme = (i == 8 && !dark) || (i == 7 && dark);
            Color display = extreme
                ? (dark ? C(0x3C, 0x41, 0x4A) : C(0xF7, 0xF8, 0xFA))
                : Palette[i];
            double mute = treatment == 0 ? 0.20 : treatment == 1 ? 0.20 : extreme ? 0.06 : 0.20;
            Color fill = Mix(display, panelFill, mute);
            if (treatment == 0 && extreme && !dark) fill = C(0xFF, 0xFF, 0xFF);

            Pen edge = treatment >= 1
                ? new Pen(new SolidColorBrush(extreme
                        ? (dark ? C(0xFF, 0xFF, 0xFF, 0x66) : C(0x00, 0x00, 0x00, 0x40))
                        : C(0x00, 0x00, 0x00, 0x1A)), 1)
                : null;
            c.DrawRectangle(new SolidColorBrush(fill), edge, new Rect(x + i * seg, y, seg + 0.5, h));
        }
    }
}
