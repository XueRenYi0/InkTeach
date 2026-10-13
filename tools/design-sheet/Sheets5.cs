using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v5：高度调研 —— 我们 80/108 高不高。基准：900 逻辑像素高的屏。
/// 参照：Windows 平台常见工具条高度、gInk / ppInk 截图实量。
/// </summary>
internal static partial class Program
{
    static double DrawHeightSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "工具条高度调研 · 我们这条算不算高", 40, y, 30, TitleBrush, bold: true);
        Text(c, "基准：900 逻辑像素高的屏幕（2880×1800 @192 DPI 换算过来）。长度按 1:1 画，旁边标占屏高比例。",
             40, y + 44, 14, BodyBrush);
        Text(c, "结论：**高度这次确实偏高** —— 平时 80 已经等于 gInk 那个大块头，展开 108 更是平台标准的两倍。但可以瘦到 60 / 84。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ① 谁多高
        y = Section(c, y, "① 谁多高（逻辑像素；屏幕高按 900 算）");
        (string Who, double H, string Note, int Kind)[] bars =
        {
            ("WinUI 标准按钮 / 常用工具条", 32, "32 —— Windows 里一个按钮的标准高度", 0),
            ("系统截图工具条（估）", 48, "48 —— 和任务栏差不多高", 0),
            ("平台工具条的常见区间", 56, "40～56；56 已经算「厚」的那一档", 0),
            ("ppInk（量自截图）", 76, "60～93 之间，量不准（图上叠了别的窗口）", 0),
            ("gInk（量自截图）", 88, "88 —— 就是「大按钮流派」，图标本身近 50 高", 0),
            ("我们 · 平时", 80, "6 色线 ＋ 56 按钮带 ＋ 18 滑条带", 1),
            ("我们 · 展开", 108, "34 色板 ＋ 56 按钮带 ＋ 18 滑条带", 2),
            ("我们 · 瘦身档（建议）", 60, "4 色线 ＋ 56 按钮带（滑条嵌进下沿，不单独占高度）", 3),
            ("我们 · 瘦身档展开", 84, "28 色板 ＋ 56 按钮带", 3),
        };
        double ry = y + 10;
        foreach (var b in bars)
        {
            Color col = b.Kind switch
            {
                1 => C(0xE0, 0x7A, 0x2B),
                2 => C(0xD1, 0x3A, 0x3A),
                3 => Accent,
                _ => C(0x8A, 0x8F, 0x98),
            };
            bool ours = b.Kind > 0;
            Text(c, b.Who, 60, ry + 4, 13, ours ? new SolidColorBrush(col) : BodyBrush, bold: ours);
            // 条子的"长度"按高度画（0.9 倍），但每行固定高度 —— 否则 108 那行会压到下一行
            double bw = Math.Max(16, b.H * 0.9);
            const double bh = 20;
            c.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, col.R, col.G, col.B)), null,
                                   new Rect(360, ry, bw, bh), 9, 9);
            Text(c, $"{b.H:F0}", 360 + bw + 10, ry + 4, 12.5, new SolidColorBrush(col), ours);
            Text(c, $"{b.H / 900 * 100:F1}% 屏高", 470, ry + 4, 12, NoteBrush);
            Text(c, b.Note, 560, ry + 4, 12, NoteBrush);
            ry += 32;
        }
        y = ry + 8;

        // ② 那 108 是怎么堆出来的
        y = Section(c, y, "② 我们这 108 是怎么堆出来的（左边现状，右边瘦身档）");
        StackBar(c, 120, y + 10, "现状：平时 80 ／ 展开 108", new (string, double, Color)[]
        {
            ("色线 6 / 色板 34", 34, C(0xF2, 0x2E, 0x2E)),
            ("按钮带 56", 56, C(0x21, 0x73, 0xE6)),
            ("滑条带 18", 18, C(0x5A, 0x5E, 0x66)),
        });
        StackBar(c, 760, y + 10, "瘦身档：平时 60 ／ 展开 84", new (string, double, Color)[]
        {
            ("色线 4 / 色板 28", 28, C(0xF2, 0x2E, 0x2E)),
            ("按钮带 56（滑条嵌进下沿）", 56, C(0x21, 0x73, 0xE6)),
        });

        double by = y + 322;
        Text(c, "怎么瘦的：① 滑条不再单独占一行，嵌进按钮带下沿那道 8 像素的内边距里（命中区向外借 12 像素，仍然是 32 高）；",
             120, by, 12.5, BodyBrush);
        Text(c, "② 色线从 6 收到 4、色板从 34 收到 28（色片 26 → 24）；③ 按钮和图标一律不动 —— 那是眼睛的成本，不能省。",
             120, by + 20, 12.5, BodyBrush);
        y = by + 46;

        // ③ 三档
        y = Section(c, y, "③ 三档方案（按钮 44 / 图标 24 都不动）");
        string[] head = { "档", "平时高", "展开高", "动了什么", "代价" };
        double[] cw = { 150, 90, 90, 460, 420 };
        string[][] rows =
        {
            new[] { "现状", "80", "108", "—", "你说的「高」，主要就在这 18 的滑条带和 34 的色板" },
            new[] { "瘦身档（建议）", "60", "84", "滑条嵌进按钮带下沿；色线 6→4；色板 34→28", "滑条和按钮的视觉间距变小，要留一道 1px 分隔" },
            new[] { "极限档", "56", "80", "再把按钮带内边距 6→4（按钮带 52）", "按钮上下只剩 4 像素，悬停底会贴到边" },
            new[] { "砍图标档（不建议）", "52", "76", "按钮 44→40、图标 24→20", "退回到你已经否掉的那一版" },
        };
        double cx0 = 60; double ryy0 = y + 10;
        for (int i = 0; i < head.Length; i++) { Text(c, head[i], cx0, ryy0, 13, TitleBrush, bold: true); cx0 += cw[i]; }
        Line(c, 60, ryy0 + 24, 60 + cw[0] + cw[1] + cw[2] + cw[3] + cw[4], ryy0 + 24, C(0xD8, 0xDC, 0xE2));
        for (int r = 0; r < rows.Length; r++)
        {
            double yy = ryy0 + 36 + r * 30;
            bool rec = r == 1;
            if (rec) c.DrawRoundedRectangle(new SolidColorBrush(C(0xEC, 0xF3, 0xFC)), null,
                                            new Rect(60, yy - 5, cw[0] + cw[1] + cw[2] + cw[3] + cw[4], 28), 6, 6);
            double cxx = 60;
            for (int i = 0; i < rows[r].Length; i++)
            {
                Text(c, rows[r][i], cxx, yy, 12.5, rec ? new SolidColorBrush(Accent) : BodyBrush, rec && i == 0);
                cxx += cw[i];
            }
        }
        y = ryy0 + 36 + rows.Length * 30 + 14;

        // ④ 结论
        var box = new Rect(40, y, SheetW - 80, 132);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "④ 结论", 60, y + 16, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "高是**真的偏高**：平台工具条 40～56，我们平时 80、展开 108。gInk 能到 88，但它的图标本身近 50 高 —— 那是另一个流派。",
            "**平时那个高度才是最要紧的**（展开只在碰色带时出现）：建议先瘦到「平时 60」＝ 平台区间的上沿。",
            "做法就一句：**别让滑条单独占一行**。把它嵌进按钮带下沿的内边距里，省 18 像素，而且观感上更像「面板的一道收边」。",
        };
        double cy = y + 46;
        foreach (string line in concl) { Text(c, line, 60, cy, 13, BodyBrush); cy += 22; }
        return y + 146;
    }

    static void StackBar(DrawingContext c, double x, double y, string title, (string Label, double H, Color Col)[] parts)
    {
        Text(c, title, x, y, 13.5, TitleBrush, bold: true);
        double total = 0;
        foreach (var p in parts) total += p.H;
        double yy = y + 26;
        foreach (var p in parts)
        {
            double h = p.H * 2.2;                       // 放大画清楚
            c.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE6, p.Col.R, p.Col.G, p.Col.B)), null,
                            new Rect(x, yy, 300, h));
            Text(c, p.Label, x + 310, yy + h / 2 - 9, 12.5, BodyBrush);
            yy += h;
        }
        c.DrawRectangle(null, new Pen(new SolidColorBrush(C(0x5A, 0x5E, 0x66)), 1), new Rect(x, y + 26, 300, total * 2.2));
        Text(c, $"合计 {total:F0}", x + 310, yy + 2, 12.5, new SolidColorBrush(Accent), true);
    }
}
