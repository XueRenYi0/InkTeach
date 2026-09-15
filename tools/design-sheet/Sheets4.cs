using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v4：宽度调研 —— 我们这条 640 到底算不算宽。用 1440 逻辑像素的屏幕做基准，
/// 把同行的实测占比画在同一条尺子上（数据来源见文档第九节）。
/// </summary>
internal static partial class Program
{
    static double DrawWidthSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "工具条宽度调研 · 我们这条算不算宽", 40, y, 30, TitleBrush, bold: true);
        Text(c, "基准：1440 逻辑像素宽的屏幕（就是 2880×1800 @192 DPI 换算过来的那块屏）。",
             40, y + 44, 14, BodyBrush);
        Text(c, "结论先给：按同行标准我们**不算宽，反而更窄**；\"显宽\"另有原因（第三节）。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ① 一条尺子上比
        y = Section(c, y, "① 同样一条工具条，各家占屏宽多少（都换算到 1440）");
        double rulerW = 1200;
        (string Who, double Ratio, string Note, bool Ours)[] bars =
        {
            ("ppInk（30+ 项）",        0.49, "1920 宽截图里约 945 像素；按钮很小（每项约 31）", false),
            ("gInk（10 项）",          0.74, "960 宽截图里约 710 像素；按钮很大（每项约 90，含 2 倍缩放）", false),
            ("系统截图工具（约 6 项）", 0.29, "估算，仅供量级参考", false),
            ("我们（11 项，44/24）",    0.44, "640 逻辑像素", true),
            ("我们 · 收纳成 8 项",      0.34, "496", true),
            ("我们 · 收纳成 7 项",      0.30, "435", true),
        };

        double ry = y + 10;
        foreach (var b in bars)
        {
            bool ours = b.Ours;
            var col = ours ? (b.Ratio < 0.4 ? Accent : C(0xE0, 0x7A, 0x2B)) : C(0x5A, 0x5E, 0x66);
            Text(c, b.Who, 60, ry + 4, 13, ours ? new SolidColorBrush(col) : BodyBrush, bold: ours);
            double bw = rulerW * b.Ratio;
            c.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, col.R, col.G, col.B)), null,
                                   new Rect(300, ry, bw, 26), 13, 13);
            Text(c, $"{b.Ratio * 100:F0}%", 300 + bw + 10, ry + 5, 12.5, new SolidColorBrush(col), ours);
            Text(c, b.Note, 500, ry + 5, 12, NoteBrush);
            ry += 40;
        }
        y = ry + 4;

        // ② 减项账
        y = Section(c, y, "② 每少放一项，省多少（44 按钮 / 4 间隙 / 组间 17）");
        (string Cfg, string Width, string Pct, string How)[] rows =
        {
            ("11 项（现在）", "640", "44%", "全部工具都摆在外面"),
            ("10 项", "592", "41%", "把「设置」收进更多"),
            ("9 项", "529", "37%", "再把「截屏」也收进去"),
            ("8 项", "496", "34%", "再把「重做」收进去（重做还能用 Ctrl+Y）"),
            ("7 项", "435", "30%", "再把「激光笔」收进去"),
            ("6 项 ＋「更多」", "448", "31%", "收成 6 项，但多一格「更多」（第 1 轮已经设计过）"),
        };
        double[] cw = { 200, 110, 90, 700 };
        string[] head = { "配置", "宽度", "占屏", "少了什么" };
        double cx = 60;
        for (int i = 0; i < head.Length; i++) { Text(c, head[i], cx, y + 4, 13, TitleBrush, bold: true); cx += cw[i]; }
        Line(c, 60, y + 28, 60 + cw[0] + cw[1] + cw[2] + cw[3], y + 28, C(0xD8, 0xDC, 0xE2));
        for (int r = 0; r < rows.Length; r++)
        {
            double ryy = y + 40 + r * 28;
            bool rec = r == 3;
            if (rec) c.DrawRoundedRectangle(new SolidColorBrush(C(0xEC, 0xF3, 0xFC)), null,
                                            new Rect(60, ryy - 5, cw[0] + cw[1] + cw[2] + cw[3], 26), 6, 6);
            cx = 60;
            string[] cells = { rows[r].Cfg, rows[r].Width, rows[r].Pct, rows[r].How };
            for (int i = 0; i < cells.Length; i++)
            {
                Text(c, cells[i], cx, ryy, 12.5, rec ? new SolidColorBrush(Accent) : BodyBrush, rec && i == 0);
                cx += cw[i];
            }
        }
        y += 40 + rows.Length * 28 + 10;

        // ③ 为什么「显宽」
        y = Section(c, y, "③ 那为什么你还是觉得宽？三个原因都不在「宽度」上");
        string[] reasons =
        {
            "1. 同行是**贴着屏幕边**的一条，我们是**浮在屏幕里**的一块 —— 同样宽，浮着的那个一定更「占地方」。",
            "2. 同行高 40～56，我们高 108（三带：色线 ＋ 按钮 ＋ 滑条）—— 面积是宽 × 高，我们「方」得多。",
            "3. 我们的按钮 44、图标 24（你上一轮亲自定的「太小了」），同行很多是 31～40 的小按钮。",
            "",
            "所以真正管用的三招是：**① 用收纳把它变短；② 贴边停靠（同行的做法）；③ 微调内边距与组间**。",
            "把图标改小、或者砍掉色线/滑条，都不是好办法 —— 那是在砍功能换面积。",
        };
        double y3 = y + 12;
        foreach (string line in reasons) { Text(c, line, 60, y3, 13, BodyBrush); y3 += 24; }
        y = y3 + 10;

        // ④ 结论
        var box = new Rect(40, y, SheetW - 80, 130);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "④ 结论", 60, y + 16, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "按同行标准，我们 44% 是**偏窄的**（gInk 74%、ppInk 49%）：这条长度对「11 个工具摆一行」来说是正常的。",
            "但「宽」的感觉是真的，来源是「浮着 ＋ 更高 ＋ 按钮更大」。所以**先做收纳、再做贴边**，不要砍图标。",
            "建议的默认值：**收纳到 8 项（496 / 34%）**，并且允许用户自己继续减到 6～7 项（430～450 / 30%）。",
        };
        double cy2 = y + 46;
        foreach (string line in concl) { Text(c, line, 60, cy2, 13, BodyBrush); cy2 += 22; }
        return y + 144;
    }
}
