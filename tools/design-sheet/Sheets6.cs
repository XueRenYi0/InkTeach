using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v6：贴边与隐藏 —— 四个方向怎么摆（色带永远朝屏幕中心）、收起来怎么露头、
/// 以及哪几类坑必须提前防。这一页是给"要不要现在做"这个决定用的。
/// </summary>
internal static partial class Program
{
    static double DrawDockSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "贴边与隐藏 · 方向、露头、以及坑在哪", 40, y, 30, TitleBrush, bold: true);
        Text(c, "先把规则画出来（不是先写代码）：贴哪条边、色带朝哪边、收起来露多少、四向还是只做上下。",
             40, y + 44, 14, BodyBrush);
        Text(c, "结论：**规则现在就能定，实现最好等接进引擎** —— 这类 bug 全在窗口行为上，假面板一个都验证不到。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ① 四个方向
        y = Section(c, y, "① 四个方向的停靠：色带永远朝屏幕中心（内容那一侧）");
        DockSample(c, 60, y + 6, "贴下边", DockSide.Bottom);
        DockSample(c, 380, y + 6, "贴上边", DockSide.Top);
        DockSample(c, 700, y + 6, "贴左边", DockSide.Left);
        DockSample(c, 1020, y + 6, "贴右边", DockSide.Right);
        Text(c, "为什么朝中心：① 指针总是从内容那侧过来，色带离手最近；② 展开时只往内容方向长，不会顶出屏幕；",
             60, y + 236, 12.5, BodyBrush);
        Text(c, "③ 贴边收起时，露出来的那一条正好是色带 —— 屏幕边上留一道彩色细线，既是提示又是装饰。",
             60, y + 256, 12.5, BodyBrush);
        y += 286;

        // ② 收起来露多少
        y = Section(c, y, "② 折叠隐藏：必须留一条「看得见的头」");
        (string Title, string Note, int Kind)[] hides =
        {
            ("完全移出屏幕", "鼠标进不来 → 永远唤不出。而且窗口在屏幕外，脏区也算不到。", 0),
            ("留 8 像素（推荐）", "看得见、点得到、悬停能唤出；8 像素在 192 DPI 上是 16 物理像素，够看。", 1),
            ("只留 2 像素", "像系统任务栏自动隐藏那样。好处是几乎不占地方；代价是得用鼠标「蹭」边缘才找得到。", 2),
        };
        double hy = y + 8;
        foreach (var h in hides)
        {
            Text(c, h.Title, 60, hy, 13, h.Kind == 1 ? new SolidColorBrush(Accent) : BodyBrush, h.Kind == 1);
            var screen = new Rect(320, hy - 4, 420, 60);
            c.DrawRectangle(new SolidColorBrush(C(0xEC, 0xEE, 0xF3)), null, screen);
            c.DrawRectangle(new SolidColorBrush(C(0x30, 0x34, 0x3C)), null, new Rect(screen.X, screen.Bottom - 2, screen.Width, 2));
            double vis = h.Kind == 0 ? 0 : h.Kind == 1 ? 8 : 2;
            double w = 300;
            c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0xE6)),
                                   new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x1A)), 1),
                                   new Rect(screen.X + 60, screen.Bottom - vis, w, 52), 10, 10);
            c.DrawRectangle(new SolidColorBrush(C(0xF2, 0x2E, 0x2E)), null,
                            new Rect(screen.X + 60, screen.Bottom - vis, w, Math.Min(4, vis)));
            Text(c, h.Note, 760, hy + 4, 12, NoteBrush);
            hy += 74;
        }
        y = hy + 6;

        // ③ 坑在哪
        y = Section(c, y, "③ 这类功能的坑：四类，全在「窗口行为」上（假面板验证不到）");
        (string Cat, string What, string Guard)[] bugs =
        {
            ("坐标与多屏", "虚拟桌面有负坐标；每屏 DPI 可能不同；面板跨两块屏会被各裁一半；"
                         + "任务栏可能自动隐藏、可能在左右、可能在别的屏", "布局用虚拟桌面矩形 + 每屏 rcWork；跨屏时按当前屏夹住"),
            ("窗口与输入", "任务栏是 topmost 的「特殊窗口」，会盖住我们的覆盖层；指针在屏幕外时窗口收不到悬停；"
                         + "鼠标从副屏扫过边缘会误触发；穿透模式下界面本来就点不到", "避让 rcWork；露头必须留在屏幕内；唤出要 dwell + 方向判断；先修穿透那条"),
            ("渲染与脏区", "界面消失时必须把上一帧的界面区域重画；贴边动画每帧都要重画两块区域（新旧各一）", "已有的 _uiBoundsPrev 机制能覆盖；动画期间必须让引擎持续给帧"),
            ("状态与用户", "藏得太深就找不回；有人就是不想让它自动藏；手势和书写时不能被打断", "默认不自动隐藏；给开关；书写中禁止触发；留「看得见的头」"),
        };
        double by = y + 10;
        double[] cw = { 150, 640, 560 };
        string[] head = { "哪一类", "会出什么事", "怎么防" };
        double bx = 60;
        for (int i = 0; i < head.Length; i++) { Text(c, head[i], bx, by, 13, TitleBrush, bold: true); bx += cw[i]; }
        Line(c, 60, by + 24, 60 + cw[0] + cw[1] + cw[2], by + 24, C(0xD8, 0xDC, 0xE2));
        double ry = by + 36;
        foreach (var b in bugs)
        {
            double h1 = Wrap(c, b.What, 60 + cw[0] + 6, ry, cw[1] - 20, 12.5, BodyBrush);
            double h2 = Wrap(c, b.Guard, 60 + cw[0] + cw[1] + 6, ry, cw[2] - 20, 12.5, NoteBrush);
            Text(c, b.Cat, 60, ry + 2, 12.5, new SolidColorBrush(C(0x3A, 0x3E, 0x46)), true);
            ry += Math.Max(46, Math.Max(h1, h2) + 14);
        }
        y = ry + 8;

        // ④ 分期
        y = Section(c, y, "④ 分期建议：现在做什么、等接引擎再做什么");
        (string When, string What, string Why)[] phase =
        {
            ("现在（假面板里，成本小）", "把规则定下来：贴哪几条边、离边多少、色带朝内、露头多少、吸附阈值、动画时长",
             "这些都是设计决定，不需要真窗口就能定，而且现在定下来能省掉后面的返工"),
            ("现在就能做（假面板里）", "上下贴边的视觉演示 + 松手吸附 + 收起时露一条色线",
             "纯布局与命中，假面板能演示得和真的一样；左右贴边要先把三带转成三列，工作量大，建议往后放"),
            ("等接进引擎再做", "真正的边缘吸附、悬停唤出、任务栏避让、多屏、穿透下的界面点击",
             "这些的 bug 全在窗口行为上（z 序 / 命中 / 脏区 / 多屏），假面板用的是 WPF 假窗口，一个都验不出"),
        };
        double py = y + 10;
        foreach (var p in phase)
        {
            Text(c, p.When, 60, py + 2, 12.5, p.When.StartsWith("等") ? new SolidColorBrush(C(0xD1, 0x3A, 0x3A)) : new SolidColorBrush(Accent), true);
            double h1 = Wrap(c, p.What, 320, py, 520, 12.5, BodyBrush);
            Wrap(c, p.Why, 860, py, 620, 12.5, NoteBrush);
            py += Math.Max(46, h1 + 14);
        }
        y = py + 6;

        var box = new Rect(40, y, SheetW - 80, 108);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "⑤ 回答「现在做合适吗」", 60, y + 14, 15, TitleBrush, bold: true);
        string[] ans =
        {
            "**规则现在定，合适**；**实现等接引擎**，更合适 —— 因为接引擎前还欠两件必修项（穿透下界面点不到、界面驱动不了动画），",
            "那两件不做，贴边和隐藏接上去也是「点不动的界面」。所以顺序是：先补那两件 → 再把界面接进引擎 → 然后做贴边与隐藏。",
            "如果现在就想看到效果：可以在假面板里做「上下贴边 + 露一条色线」，成本半天，但它只解决「长什么样」，不解决「会不会有 bug」。",
        };
        double ay = y + 42;
        foreach (string line in ans) { Text(c, line, 60, ay, 12.5, BodyBrush); ay += 22; }
        return ay + 8;
    }

    enum DockSide { Bottom, Top, Left, Right }

    /// <summary>画一个"屏幕 + 面板"的示意：色带永远在朝屏幕中心的那一侧。</summary>
    static void DockSample(DrawingContext c, double x, double y, string label, DockSide side)
    {
        var screen = new Rect(x, y, 260, 200);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF4, 0xF5, 0xF7)),
                               new Pen(new SolidColorBrush(C(0xDA, 0xDE, 0xE4)), 1), screen, 8, 8);
        // 屏幕中心
        var center = new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
        c.DrawEllipse(new SolidColorBrush(C(0xC8, 0xCD, 0xD6)), null, center, 3, 3);

        double len = 190, thick = 26;
        Rect panel;
        bool horizontal = side is DockSide.Bottom or DockSide.Top;
        if (horizontal)
        {
            double px = center.X - len / 2;
            double py = side == DockSide.Bottom ? screen.Bottom - 12 - thick : screen.Y + 12;
            panel = new Rect(px, py, len, thick);
        }
        else
        {
            double py = center.Y - len / 2;
            double px = side == DockSide.Right ? screen.Right - 12 - thick : screen.X + 12;
            panel = new Rect(px, py, thick, len);
        }
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0xE6)),
                               new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x1A)), 1), panel, 8, 8);
        // 色带：朝中心那一条
        var band = side switch
        {
            DockSide.Bottom => new Rect(panel.X, panel.Y, panel.Width, 5),
            DockSide.Top => new Rect(panel.X, panel.Bottom - 5, panel.Width, 5),
            DockSide.Left => new Rect(panel.Right - 5, panel.Y, 5, panel.Height),
            _ => new Rect(panel.X, panel.Y, 5, panel.Height),
        };
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF2, 0x2E, 0x2E)), null, band, 2.5, 2.5);

        // 从面板指向中心的箭头
        var from = side switch
        {
            DockSide.Bottom => new Point(center.X, panel.Y - 6),
            DockSide.Top => new Point(center.X, panel.Bottom + 6),
            DockSide.Left => new Point(panel.Right + 6, center.Y),
            _ => new Point(panel.X - 6, center.Y),
        };
        var to = side switch
        {
            DockSide.Bottom => new Point(center.X, center.Y - 12),
            DockSide.Top => new Point(center.X, center.Y + 12),
            DockSide.Left => new Point(center.X - 12, center.Y),
            _ => new Point(center.X + 12, center.Y),
        };
        var pen = new Pen(new SolidColorBrush(C(0x9A, 0xA0, 0xAA)), 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        c.DrawLine(pen, from, to);
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            var d = new Vector(to.X - from.X, to.Y - from.Y);
            d.Normalize();
            var n = new Vector(-d.Y, d.X);
            g.BeginFigure(to, true, true);
            g.LineTo(to - d * 9 + n * 4, true, false);
            g.LineTo(to - d * 9 - n * 4, true, false);
        }
        tri.Freeze();
        c.DrawGeometry(new SolidColorBrush(C(0x9A, 0xA0, 0xAA)), null, tri);

        Text(c, label, x, y + 208, 13, TitleBrush, true);
        Text(c, "色带朝中心", x + 62, y + 210, 11.5, new SolidColorBrush(C(0xF2, 0x2E, 0x2E)));
    }

    /// <summary>把一段字折行画出来，返回占的高度（用于风险表）。</summary>
    static double Wrap(DrawingContext c, string text, double x, double y, double width, double size, Brush brush)
    {
        var ft = Fmt(text, size, brush);
        ft.MaxTextWidth = width;
        c.DrawText(ft, new Point(x, y));
        return ft.Height;
    }
}
