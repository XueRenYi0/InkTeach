using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v9：截屏图标的备选。和激光笔那轮同一个做法 —— 先把上游的近义图标摆出来看。
/// </summary>
internal static partial class Program
{
    static double DrawShotIconSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "截屏（截图）图标 · 备选", 40, y, 30, TitleBrush, bold: true);
        Text(c, "上游 Fluent 里有好几枚近义图标。左边 1:1 实际大小，右边 3×；下面一行是评价。",
             40, y + 44, 14, BodyBrush);
        Text(c, "结论：**保留 Screenshot（它是这个动作的专名）**；嫌它「方框+圆点」太抽象就用 Camera；Crop 的框感最强但语义是「裁剪」。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        (string Name, string IconName, string Note, bool Rec)[] cands =
        {
            ("Screenshot（现用）", "capture", "Fluent 给「截屏」这个动作的专名：一个取景框 ＋ 一个圆点（快门）。语义最准。", true),
            ("Screenshot Record", "shotRecord", "取景框 ＋ 录制点，偏「屏幕录制」。", false),
            ("Camera 相机", "camera", "最直观的「拍一张」。很多软件的截图就是相机图标。", true),
            ("Crop 裁剪框", "crop", "框感最强，但语义是「把已有图裁一下」，不是「抓屏」。", false),
            ("Scan 扫描", "scan", "扫描边框：像「扫描仪/识别」，偏文档。", false),
            ("Scan Object", "scanObject", "扫描一个对象 —— 更像「识别」。", false),
            ("Window 窗口", "windowIco", "表示「窗口」，是一个目标而不是动作。", false),
            ("Desktop 桌面", "desktop", "表示「显示器/桌面」，同样偏目标。", false),
            ("Full Screen 全屏", "maximize", "表示「全屏」 —— 那是截屏的一种模式，不是截屏本身。", false),
            ("Image 图像", "image", "表示「图片」（结果），不是动作。", false),
        };

        double ly = y + 8;
        foreach (var it in cands)
        {
            Text(c, it.Name, 60, ly + 22, 13, it.Rec ? new SolidColorBrush(Accent) : BodyBrush, it.Rec);
            var brush = new SolidColorBrush(it.Rec ? Accent : Ink);
            IconFrame(c, 320, ly + 24, 34);
            Icon(c, it.IconName, 320, ly + 24, 20, brush);
            IconFrame(c, 400, ly + 24, 76);
            Icon(c, it.IconName, 400, ly + 24, 60, brush);
            Text(c, "1:1", 306, ly + 42, 11, NoteBrush);
            Text(c, "3×", 386, ly + 64, 11, NoteBrush);
            Paragraph(c, it.Note, 460, ly + 14, 1000, 12, it.Rec ? new SolidColorBrush(Accent) : NoteBrush);
            ly += 76;
        }
        y = ly + 8;

        var box = new Rect(40, y, SheetW - 80, 120);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "怎么选", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. **建议保留 Screenshot**：它是这个动作的专名，而且和其它工具的「框」类图标（选择=虚线框、图形=方形）不撞。",
            "2. 备选 **Camera**：最直观，但和「拍照」这个词一样，会让人以为拍的是摄像头画面 —— 在投影环境下有人会误解。",
            "3. **不要用 Crop**：它表示「把已有图裁小」，和「抓一块屏幕」是两件事；我们已经有「截屏」这个词在提示上了。",
            "4. 截屏的两个模式（直接截 / 隐藏批注截）**不靠图标区分** —— 和橡皮一样，模式在那条上带里。",
        };
        double cy = y + 42;
        foreach (string line in concl) { Text(c, line, 60, cy, 12.5, BodyBrush); cy += 20; }
        return y + 134;
    }
}
