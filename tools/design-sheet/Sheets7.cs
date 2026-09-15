using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v7：两件事 —— ① 激光笔图标候选（含 Material Symbols 的专名图标与自绘方案）；
/// ② 「更多」抽屉（照希沃那类白板的工具箱思路）长什么样、可行性如何。
/// </summary>
internal static partial class Program
{
    static double DrawLaserMoreSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "激光笔图标候选 ＋「更多」抽屉", 40, y, 30, TitleBrush, bold: true);
        Text(c, "① 激光笔：上游 Fluent 没有这个专名 —— 我们从别的开源库找了一个专名图标，另外自己按 Fluent 网格画了两个。",
             40, y + 44, 14, BodyBrush);
        Text(c, "② 更多：把「设置」换成一个入口，点开是一个工具箱抽屉，后期往里加不常用功能与学科工具。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ① 激光笔候选
        y = Section(c, y, "① 激光笔图标：9 个候选（左边 1:1 实际大小，右边 3×）");
        (string Who, string Name, string Note, bool Rec, Action<DrawingContext, double, double, double, Brush> Draw)[] laser =
        {
            ("Material Symbols（专名）", "stylus_laser_pointer", "Google 的专名图标：笔 ＋ 落点 ＋ 光束。Apache-2.0，可直接用。", true,
                (c, cx, cy, sz, b) => Icon(c, "msStylusLaser", cx, cy, sz, b)),
            ("Material Symbols（fill）", "stylus_laser_pointer-fill", "同一枚的实心变体 —— 选中态可以用它。", true,
                (c, cx, cy, sz, b) => Icon(c, "msStylusLaserFill", cx, cy, sz, b)),
            ("自绘（Fluent 线宽）", "笔 ＋ 光束 ＋ 落点", "照 Excalidraw 那个思路画的：一支笔、一道细光束、一个落点。线宽与 Fluent 对齐。", true,
                (c, cx, cy, sz, b) => LaserPenBeam(c, cx, cy, sz, b)),
            ("自绘", "光束锥", "光点 ＋ 一束斜射出去的光（锥体半透明，小尺寸下有体量）。", false,
                (c, cx, cy, sz, b) => LaserBeamCone(c, cx, cy, sz, b)),
            ("Fluent 近义", "Flash 闪电", "上游现成、20 像素下最清楚；语义是「闪光」。同类软件常拿它当激光笔。", false,
                (c, cx, cy, sz, b) => Icon(c, "laserFlash", cx, cy, sz, b)),
            ("Fluent 近义", "Record 圆点", "就是「光点」本身；单独用容易被当成「录制」。", false,
                (c, cx, cy, sz, b) => Icon(c, "laserRecord", cx, cy, sz, b)),
            ("Fluent 近义", "Target 靶心", "像瞄准 / 定位，辨识度高，语义偏「目标」。", false,
                (c, cx, cy, sz, b) => Icon(c, "laserTarget", cx, cy, sz, b)),
            ("自绘", "光点 ＋ 短射线", "像「亮度 / 发光」，容易和显示亮度混。", false,
                (c, cx, cy, sz, b) => LaserRays(c, cx, cy, sz, b)),
            ("自绘（第一版）", "光点 ＋ 三道弧", "像 Wi-Fi 信号 —— 就是你说丑的那个。", false,
                (c, cx, cy, sz, b) => LaserArcs(c, cx, cy, sz, b)),
        };
        double ly = y + 8;
        foreach (var it in laser)
        {
            Text(c, it.Who, 60, ly + 10, 11.5, NoteBrush);
            Text(c, it.Name, 60, ly + 28, 13, it.Rec ? new SolidColorBrush(Accent) : BodyBrush, it.Rec);
            var brush = new SolidColorBrush(it.Rec ? Accent : Ink);
            IconFrame(c, 320, ly + 26, 34);
            it.Draw(c, 320, ly + 26, 20, brush);
            IconFrame(c, 400, ly + 26, 76);
            it.Draw(c, 400, ly + 26, 60, brush);
            Text(c, "1:1", 306, ly + 44, 11, NoteBrush);
            Text(c, "3×", 386, ly + 66, 11, NoteBrush);
            Paragraph(c, it.Note, 460, ly + 16, 1000, 12, it.Rec ? new SolidColorBrush(Accent) : NoteBrush);
            ly += 82;
        }
        y = ly + 6;

        // ② 更多抽屉
        y = Section(c, y, "② 「更多」抽屉：主条上换成一个入口，点开是一格一格的工具箱");
        ToolbarForSketch(c, 60, y + 10);
        DrawerSketch(c, 60, y + 108, 640, 300);
        double nx = 760;
        string[] notes =
        {
            "**和希沃那类白板一个思路**：侧边／端头留一个入口，点开是分组网格 ——",
            "后期不常用的功能、以及学科工具（直尺、量角器、元素周期表、田字格……）都往这里放。",
            "",
            "**它和上带是两件事，不冲突**：",
            "· **上带** = 当前工具的参数（模式 / 颜色 / 大小）—— 跟着工具走，临时；",
            "· **更多** = 全局的入口集合（不常用工具 / 学科工具 / 设置）—— 常驻一个入口，点开才有。",
            "",
            "**可行性结论：完全可行，而且不用动引擎** —— 它只是「多一个界面面板」：",
            "抽屉里的每一项，最终都落到已经存在的引擎能力上（换工具、换模式、开开关）。",
            "唯一要新增的是「往抽屉里放什么、谁钉在主条上」这份**配置数据**（可以落进 settings.json）。",
        };
        double ny = y + 16;
        foreach (string line in notes) { Text(c, line, nx, ny, 12.5, BodyBrush); ny += 21; }
        y = Math.Max(y + 108 + 300, ny) + 24;

        // ③ 图标换不换
        y = Section(c, y, "③ 「更多」这个入口用什么图标：省略号 还是 宫格");
        (string Name, string Icon, string Note)[] moreIcons =
        {
            ("More Horizontal（…）", "more", "Windows 11 里「还有更多」的标准写法。最不容易误解，也最省地方。"),
            ("Apps / Grid（宫格）", "apps", "像「工具箱」：一格格工具。语义更像希沃那个入口，但要和「图形」图标区分开。"),
            ("Grid（网格）", "grid", "同上，线更细；和 Apps 二选一即可。"),
        };
        double my2 = y + 12;
        foreach (var m in moreIcons)
        {
            Text(c, m.Name, 60, my2 + 10, 13, BodyBrush, true);
            IconFrame(c, 320, my2 + 18, 40);
            Icon(c, m.Icon, 320, my2 + 18, 20, new SolidColorBrush(Ink));
            IconFrame(c, 410, my2 + 18, 84);
            Icon(c, m.Icon, 410, my2 + 18, 60, new SolidColorBrush(Ink));
            Paragraph(c, m.Note, 470, my2 + 8, 980, 12.5, NoteBrush);
            my2 += 76;
        }
        y = my2 + 4;

        var box = new Rect(40, y, SheetW - 80, 118);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "④ 建议", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "激光笔：**首选 Material Symbols 的 stylus_laser_pointer**（它是唯一一个「专名」图标，语义不会认错），",
            "　　　　想保持「一个界面一个库」就**用第二个自绘版**（照 Excalidraw 的思路、按 Fluent 线宽重画）；",
            "　　　　选中态用它的 fill 变体。要不要跨库借这一枚，你定 —— 我会把来源与许可证写进 THIRD-PARTY-NOTICES。",
            "「更多」入口：**建议用省略号（…）**；宫格更像「工具箱」但会和主条上的「图形」抢语义，",
            "　　　　　如果后期学科工具真的多起来，再换成宫格也不迟（换图标是一行代码）。",
        };
        double cy = y + 42;
        foreach (string line in concl) { Text(c, line, 60, cy, 12.5, BodyBrush); cy += 20; }
        return cy + 9;
    }

    // ---- 画小东西 --------------------------------------------------------

    /// <summary>把主条简化画一遍（任务栏档＋瘦身档），并在最右端放"更多"入口。</summary>
    static void ToolbarForSketch(DrawingContext c, double x, double y)
    {
        double w = 592, h = 52;
        Panel(c, x, y, w, h, 18, false, PanelAlpha, true);
        c.DrawRectangle(new SolidColorBrush(PenRed), null, new Rect(x + 1, y + 1, w - 2, 4));
        double bx = x + 8 + 20;
        c.DrawEllipse(new SolidColorBrush(C(0xEC, 0xEE, 0xF2)), null, new Point(bx, y + 26), 18, 18);
        Icon(c, "chevronDown", bx, y + 26, 16, new SolidColorBrush(Ink));
        string[] icons = { "mouse", "pen", "highlighter", "laserFlash", "eraser", "select", "shapes", "capture", "undo", "redo", "more" };
        double cx = x + 8 + 40 + 17 + 20;
        for (int i = 0; i < icons.Length; i++)
        {
            bool more = i == icons.Length - 1;
            if (more) cx += 13;   // 分隔线
            if (i == 1) c.DrawRoundedRectangle(new SolidColorBrush(Accent), null, new Rect(cx - 20, y + 6, 40, 40), 9, 9);
            var fg = i == 1 ? Brushes.White : (more ? Brushes.White : new SolidColorBrush(Ink));
            if (more) c.DrawRoundedRectangle(new SolidColorBrush(Accent), null, new Rect(cx - 20, y + 6, 40, 40), 9, 9);
            Icon(c, i == 3 ? "laserFlash" : icons[i], cx, y + 26, 24, fg);
            if (Array.IndexOf(new[] { 0, 4, 7 }, i) >= 0) { c.DrawRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x1E)), null, new Rect(cx + 20 + 8, y + 16, 1, 20)); cx += 17; }
            else cx += 40 + 4;
        }
        Text(c, "主条：最后的齿轮换成「更多」（这里画成选中态）", x, y + h + 6, 12, NoteBrush);
    }

    /// <summary>抽屉：分组网格 + 每格右上角图钉。</summary>
    static void DrawerSketch(DrawingContext c, double x, double y, double w, double h)
    {
        Panel(c, x, y, w, h, 16, false, PanelAlpha, true);
        Text(c, "更多工具", x + 16, y + 12, 14, TitleBrush, true);
        Text(c, "编辑", x + w - 58, y + 12, 12, new SolidColorBrush(Accent));

        (string Group, (string Icon, string Name)[] Items)[] groups =
        {
            ("不常用", new[] { ("laserFlash", "激光笔"), ("board", "白板"), ("color", "取色"), ("delete", "清空") }),
            ("学科工具（占位）", new[] { ("shapes", "直尺"), ("circle", "量角器"), ("grid", "田字格"), ("apps", "更多…") }),
            ("设置", new[] { ("settings", "偏好") }),
        };
        double gy = y + 40;
        foreach (var g in groups)
        {
            Text(c, g.Group, x + 16, gy, 11.5, NoteBrush);
            gy += 18;
            double gx = x + 16;
            foreach (var it in g.Items)
            {
                c.DrawRoundedRectangle(new SolidColorBrush(C(0xF3, 0xF5, 0xF8)), null, new Rect(gx, gy, 64, 56), 8, 8);
                Icon(c, it.Icon, gx + 32, gy + 22, 22, new SolidColorBrush(Ink));
                Caption(c, it.Name, gx - 6, gy + 38, 76);
                Icon(c, "pin", gx + 54, gy + 12, 11, new SolidColorBrush(C(0x9A, 0xA0, 0xAA)));
                gx += 72;
            }
            gy += 72;
        }
        Text(c, "每格右上角的图钉 = 点一下钉到主条上（钉上去的就常驻了）", x + 16, y + h - 24, 11.5, NoteBrush);
    }

    // 自绘的三个激光候选（和假面板里那份保持一致）
    static void LaserPenBeam(DrawingContext c, double cx, double cy, double size, Brush brush)
    {
        double k = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(k, k));
        var body = new Pen(brush, 3.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var thin = new Pen(brush, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(body, new Point(17.0, 4.6), new Point(12.4, 9.2));
        c.DrawLine(thin, new Point(11.6, 10.0), new Point(9.4, 12.2));
        c.DrawEllipse(brush, null, new Point(6.6, 15.0), 2.1, 2.1);
        c.Pop();
        c.Pop();
    }

    static void LaserBeamCone(DrawingContext c, double cx, double cy, double size, Brush brush)
    {
        double k = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(k, k));
        var pen = new Pen(brush, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var dot = new Point(6, 18);
        var cone = new StreamGeometry();
        using (var g = cone.Open())
        {
            g.BeginFigure(dot, true, true);
            g.LineTo(new Point(20.5, 3.5), true, false);
            g.LineTo(new Point(21.5, 13.5), true, false);
        }
        cone.Freeze();
        var col = (brush as SolidColorBrush)?.Color ?? Colors.Black;
        c.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x4D, col.R, col.G, col.B)), null, cone);
        c.DrawLine(pen, new Point(7.6, 16.4), new Point(20.5, 3.5));
        c.DrawLine(pen, new Point(8.4, 18.4), new Point(21.5, 13.5));
        c.DrawEllipse(brush, null, dot, 2.7, 2.7);
        c.Pop();
        c.Pop();
    }
}
