using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v8：极简界面 —— 收起是球、展开是短胶囊（笔 / 橡皮 / 更多）；
/// 顺便回答「短胶囊还是圆盘」。
/// </summary>
internal static partial class Program
{
    static double DrawMiniSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "极简界面 · 短胶囊还是圆盘", 40, y, 30, TitleBrush, bold: true);
        Text(c, "你的方案：收起一个球 → 展开一条短胶囊（笔 / 橡皮 / 更多）；笔给 4 个色块；下面一行是面积擦和清屏。",
             40, y + 44, 14, BodyBrush);
        Text(c, "我的结论：**方案可行，而且极简版不用第二套系统** —— 它就是「完整界面只钉 3 格」；**先做短胶囊，圆盘留到以后再评估**。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ① 三态
        y = Section(c, y, "① 极简界面的三个状态（1:1，任务栏档＋瘦身档）");
        Text(c, "收起：还是那个球（48）", 60, y + 2, 13, BodyBrush, true);
        Ball(c, 60 + 24, y + 60, 48, false, false, C(0xF2, 0x2E, 0x2E));
        Caption(c, "和完整版一模一样", 40, y + 92, 90);

        Text(c, "展开 · 笔：短胶囊 ＋ 4 个色块", 200, y + 2, 13, BodyBrush, true);
        MiniCapsule(c, 200, y + 24, MiniMode.Pen);
        Caption(c, "胶囊 236 × 52（屏宽的 16%）；下面那一行就是笔的设置条", 200, y + 130, 420);

        Text(c, "展开 · 橡皮：两段 ＋ 清空", 700, y + 2, 13, BodyBrush, true);
        MiniCapsule(c, 700, y + 24, MiniMode.Eraser);
        Caption(c, "你说的「面积擦」就在这一段里；清空挂在这一行的右端（红色、按住 0.8 秒）", 700, y + 130, 420);
        y += 176;

        // ② 更多
        y = Section(c, y, "② 「更多」：和完整版共用同一个抽屉");
        MiniCapsule(c, 60, y + 8, MiniMode.More);
        DrawerSketch(c, 360, y + 8, 380, 306);
        Text(c, "极简版的「更多」不是另做一套 —— 就是完整版那个抽屉：",
             790, y + 20, 12.5, BodyBrush);
        Text(c, "应用（更新/重启/退出）、界面（深色/贴边/装饰带）、学科工具（占位）全都一样。",
             790, y + 40, 12.5, BodyBrush);
        Text(c, "抽屉里钉什么、主条上放什么，是同一份配置 —— 极简版只是「钉得少」。",
             790, y + 70, 12.5, NoteBrush);
        Text(c, "所以极简版要复用的不是「另一套界面」，而是同一份「钉住配置」。",
             790, y + 98, 12.5, NoteBrush);
        y += 330;

        // ③ 短胶囊 vs 圆盘
        y = Section(c, y, "③ 短胶囊 vs 圆盘");
        Text(c, "短胶囊（方案 A，推荐）", 60, y + 4, 13, new SolidColorBrush(Accent), true);
        MiniCapsule(c, 60, y + 26, MiniMode.Pen);
        Caption(c, "236 × 52，屏宽的 16%；一行排得下笔 / 橡皮 / 更多", 60, y + 130, 420);

        Text(c, "圆盘（方案 B，径向）", 540, y - 18, 13, BodyBrush, true);
        RadialSketch(c, 660, y + 116, 74);
        Caption(c, "直径约 168（含外圈）；每项方向固定，靠肌肉记忆点", 540, y + 196, 420);

        (string Item, string Cap, string Rad)[] cmp =
        {
            ("实现成本", "**半天**：复用完整版的布局 / 命中 / 动画，只是钉 3 格", "**一天以上**：要另写角度分区命中，组件都要重画成环形"),
            ("和完整版的关系", "同一套组件、同一份配置", "第二套设计系统（上带、色带、抽屉都得再来一遍）"),
            ("点击精度", "3 个等距按钮，费茨距离几乎一样", "每个方向距离相等，专家用户更快；但要先记住方向"),
            ("4 个色块", "一行排开，一眼扫完", "要排成环形色环，4 个色块在环上反而难扫读"),
            ("占地形状", "一条细长（贴边很合适）", "一块方形（更适合角落）"),
            ("风险", "低 —— 都是已经跑过的代码路径", "中 —— 新布局系统 = 新的命中与动画 bug"),
        };
        double tx = 60, ty = y + 216;
        double[] cw = { 150, 620, 600 };
        for (int i = 0; i < 3; i++)
            Text(c, new[] { "对比项", "短胶囊", "圆盘" }[i], tx + (i == 1 ? cw[0] : i == 2 ? cw[0] + cw[1] : 0), ty, 13, TitleBrush, true);
        Line(c, 60, ty + 24, 60 + cw[0] + cw[1] + cw[2], ty + 24, C(0xD8, 0xDC, 0xE2));
        double ry = ty + 36;
        foreach (var r in cmp)
        {
            double h1 = Wrap(c, r.Cap, 60 + cw[0], ry, cw[1] - 20, 12.5, BodyBrush);
            double h2 = Wrap(c, r.Rad, 60 + cw[0] + cw[1], ry, cw[2] - 20, 12.5, NoteBrush);
            Text(c, r.Item, 60, ry + 2, 12.5, new SolidColorBrush(C(0x3A, 0x3E, 0x46)), true);
            ry += Math.Max(40, Math.Max(h1, h2) + 12);
        }
        y = ry + 10;

        // ④ 建议
        var box = new Rect(40, y, SheetW - 80, 236);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "④ 我的五条建议", 60, y + 14, 15, TitleBrush, bold: true);
        string[] adv =
        {
            "1. **选短胶囊**：极简版的价值是「几乎不占地方 ＋ 手写和擦除就够」，短胶囊正好，而且半天能出来；",
            "　　圆盘留到以后当「可选外观」—— 它要另写一套布局系统，收益（快一点）不值现在这个成本。",
            "2. **下面那一行 = 当前工具的设置条**（和完整版同一条规则）：笔 → 4 色块；橡皮 → 整笔擦 ｜ 面积擦 ＋ 清空。",
            "　　这样你说的「面积擦」和「清屏」不用另占一格，主条就真的只有 3 格 —— 胶囊短到 236。",
            "3. **4 个颜色建议：红 / 黑 / 蓝 / 白**。红是批改、黑是板书、蓝是标注、**白是投影刚需**（深底 PPT 上只有白笔看得见）。",
            "　　如果你更想要黄（强调），把白换成黄就行 —— 但投影场景会缺一支笔。",
            "4. **粗细滑条保留**（平时就是那条极细的线）：极简版最常做的就是「写」和「擦」，粗细仅次于颜色，而且它不额外占高度。",
            "5. **两套界面怎么切**：抽屉里放一个「极简 / 完整」开关 ＋ 一个全局热键；切换时保持屏幕位置和当前工具，别让工具条跳。",
        };
        double ay = y + 42;
        foreach (string line in adv) { Text(c, line, 60, ay, 12.5, BodyBrush); ay += 22; }
        return y + 250;
    }

    enum MiniMode { Pen, Eraser, More }

    /// <summary>极简胶囊：收起格 ＋ 笔 / 橡皮 ＋ 更多，下面一行是当前工具的设置条。</summary>
    static void MiniCapsule(DrawingContext c, double x, double y, MiniMode mode)
    {
        var tools = new (string Icon, string Name)[] { ("pen", "笔"), ("eraser", "橡皮") };
        var groups = new int[0];
        var lay = BarOf(c, x, y, tools, groups, false, mode == MiniMode.Pen ? 0 : 1, -1, moreCount: 0);
        double w = lay.Width, h = BarH;

        // 下面那一行（当前工具的设置条）
        double sy = y + h + 6, sh = 26;
        Panel(c, x, sy, w, sh + 10, 12, false, PanelAlpha, true);
        switch (mode)
        {
            case MiniMode.Pen:
            {
                Color[] four = { C(0xF2, 0x2E, 0x2E), C(0x1C, 0x1F, 0x26), C(0x21, 0x73, 0xE6), C(0xFF, 0xFF, 0xFF) };
                double seg = (w - 24) / 4;
                for (int i = 0; i < 4; i++)
                {
                    var r = new Rect(x + 12 + i * seg + 4, sy + 5, seg - 8, 16);
                    c.DrawRoundedRectangle(new SolidColorBrush(four[i]),
                                           new Pen(new SolidColorBrush(i == 3 ? C(0x00, 0x00, 0x00, 0x33) : C(0x00, 0x00, 0x00, 0x22)), 1),
                                           r, 5, 5);
                    if (i == 0) c.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(C(0x1B, 0x1B, 0x1F)), 1.6),
                                                       new Rect(r.X + 1.5, r.Y + 1.5, r.Width - 3, r.Height - 3), 4, 4);
                }
                break;
            }
            case MiniMode.Eraser:
            {
                double cw2 = (w - 24 - 6 - 92) / 2;
                for (int i = 0; i < 2; i++)
                {
                    var r = new Rect(x + 12 + i * (cw2 + 6), sy + 5, cw2, 16);
                    c.DrawRoundedRectangle(new SolidColorBrush(i == 1 ? Accent : C(0x00, 0x00, 0x00, 0x0A)),
                                           new Pen(new SolidColorBrush(i == 1 ? Colors.Transparent : C(0x00, 0x00, 0x00, 0x1E)), 1), r, 5, 5);
                    var ft = Fmt(i == 0 ? "整笔擦" : "面积擦", 11, i == 1 ? Brushes.White : BodyBrush);
                    ft.MaxTextWidth = cw2; ft.TextAlignment = TextAlignment.Center;
                    c.DrawText(ft, new Point(r.X, r.Y + 1));
                }
                var rk = new Rect(x + w - 12 - 86, sy + 5, 86, 16);
                c.DrawRoundedRectangle(new SolidColorBrush(C(0xE0, 0x2B, 0x2B, 0x1A)),
                                       new Pen(new SolidColorBrush(C(0xE0, 0x2B, 0x2B, 0x66)), 1), rk, 5, 5);
                Icon(c, "broom", rk.X + 14, rk.Y + 8, 13, new SolidColorBrush(C(0xE0, 0x2B, 0x2B, 0xE6)));
                Text(c, "清空", rk.X + 24, rk.Y + 1, 11, new SolidColorBrush(C(0xE0, 0x2B, 0x2B, 0xE6)));
                break;
            }
            default:
            {
                var ft = Fmt("（点「更多」弹出抽屉，和完整版同一个）", 11, NoteBrush);
                ft.MaxTextWidth = w; ft.TextAlignment = TextAlignment.Center;
                c.DrawText(ft, new Point(x, sy + 4));
                break;
            }
        }
    }

    /// <summary>圆盘示意：中间是收起球，外圈几项。</summary>
    static void RadialSketch(DrawingContext c, double cx, double cy, double r)
    {
        var items = new (string Icon, double A)[] { ("pen", -90), ("eraser", 0), ("more", 90), ("mouse", 180) };
        foreach (var it in items)
        {
            double rad = it.A * Math.PI / 180.0;
            double bx = cx + Math.Cos(rad) * r, by = cy + Math.Sin(rad) * r;
            Strip(c, bx - 20, by - 20, 40, 40, false);
            TileRaw(c, bx, by, 40, IconSize, it.Icon, false, active: it.Icon == "pen", hovered: false, pressed: false, disabled: false);
        }
        Ball(c, cx, cy, 44, false, false, C(0xF2, 0x2E, 0x2E));
        // 色环（示意）
        Color[] four = { C(0xF2, 0x2E, 0x2E), C(0x1C, 0x1F, 0x26), C(0x21, 0x73, 0xE6), C(0xFF, 0xFF, 0xFF) };
        for (int i = 0; i < 4; i++)
        {
            double a = (-140 + i * 30) * Math.PI / 180.0;
            double bx = cx + Math.Cos(a) * (r + 26), by = cy + Math.Sin(a) * (r + 26);
            c.DrawEllipse(new SolidColorBrush(four[i]), new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x33)), 1), new Point(bx, by), 11, 11);
        }
    }
}
