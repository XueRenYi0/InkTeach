using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v1 的三张图稿：「更多」与收纳、色带三方案、面板位置与粗细。
/// 都在回答同一类问题 —— 怎么把颜色和粗细塞进一个已经很克制的工具条里。
/// 这三张稿只讨论形态，不动引擎。
/// </summary>
internal static partial class Program
{
    // =====================================================================
    //  「更多」与收纳
    // =====================================================================

    static double DrawMoreSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "批注工具条 · 「更多」与收纳 设计稿 v1", 40, y, 30, TitleBrush, bold: true);
        Text(c, "主条只放常用的，其余收进「更多」。收纳是手动做的 —— 为什么不自动，见第 ⑤ 段（有历史教训）。",
             40, y + 44, 14, BodyBrush);
        Text(c, "逻辑像素 1:1。这张稿只讲收纳这一件事，色带与粗细在另外两张上。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ① 主条：默认 vs 收纳后
        y = Section(c, y, "① 主条：默认 vs 收纳后（都是 1:1）");
        var subset = new (string Icon, string Name)[]
        {
            Tools[0], Tools[1], Tools[2], Tools[4], Tools[8], Tools[9],
        }; // 鼠标 笔 荧光笔 橡皮 后撤 重做
        var groupsB = new[] { 0, 3 };
        double wide = BarOfWidth(Tools.Length, GroupEnds, -1);
        double narrow = BarOfWidth(subset.Length, groupsB, 6);

        Text(c, $"A 默认：{Tools.Length} 项，宽 {wide:F0}", 60, y + 2, 13, BodyBrush, bold: true);
        BarOf(c, 60, y + 24, Tools, GroupEnds, false, 1, -1);

        Text(c, $"B 收纳后：{subset.Length} 项 ＋「更多」，宽 {narrow:F0} —— 省 {wide - narrow:F0} 像素（{(1 - narrow / wide) * 100:F0}%）",
             60, y + 100, 13, BodyBrush, bold: true);
        BarOf(c, 60, y + 122, subset, groupsB, false, 1, -1, moreCount: 6);
        Text(c, "「更多」那一格，在池里还有东西时带一个小圆点；池空了圆点就消失。",
             60, y + 122 + BarH + 10, 12, NoteBrush);

        // ② 抽屉
        double dx = 760, dy = y + 2;
        Text(c, "② 点「更多」：一张抽屉（不是对话框）", dx, dy, 13, BodyBrush, bold: true);
        Drawer(c, dx, dy + 24);
        double dny = dy + 24 + 228 + 12;
        Text(c, "3 行 × 4 列，每格 64×64（图标 24 ＋ 名称 11px）；右上角的图钉 = 点一下加入主条。",
             dx, dny, 12, NoteBrush);
        Text(c, "它和主条是同一套视觉：同样的白 80% 底、同样的描边、同样的圆角语言。",
             dx, dny + 20, 12, NoteBrush);
        Text(c, "（反面教材：ppInk 的调色板是个 WinForms 大对话框 —— 一开就是另一个年代。）",
             dx, dny + 40, 12, NoteBrush);

        double after = Math.Max(y + 122 + BarH + 46, dny + 64);

        // ③ 编辑态
        y = Section(c, after + 10, "③ 编辑态：主条上的项可以移出，也可以从抽屉放回");
        BarEdit(c, 60, y + 28, subset, groupsB);
        Text(c, "虚线框 = 可以移出；「笔 / 橡皮 / 后撤」是安全项，不给虚线框，也移不出去。",
             60, y + 28 + BarH + 42, 12.5, BodyBrush);
        Text(c, "移出与放回都是点击（点虚线框上的 −，点抽屉里的图钉），不要求拖放 —— 触摸屏也能用。",
             60, y + 28 + BarH + 62, 12.5, NoteBrush);
        Text(c, "「拖动换位」留到 v2：它只在有人想把笔挪到最左边时才有意义，不是收纳的必要条件。",
             60, y + 28 + BarH + 82, 12.5, NoteBrush);

        // ④ 池 ↔ 条
        y = Section(c, y + 200, "④ 关系：主条只是「工具池」的一个子集");
        Pool(c, 60, y + 16, 12, "工具池（12 项，一个都没少）");
        Arrow(c, 300, y + 92);
        MiniBar(c, 360, y + 68, subset, "主条（6 项，用户自己挑的）");
        Text(c, "被收起来的项没有消失，只是搬了家 —— 抽屉里按「最近用过」排序，三个月没碰的自己沉到底部。",
             60, y + 162, 12.5, NoteBrush);

        y += 196;

        // ⑤ 规矩
        var box = new Rect(40, y, SheetW - 80, 152);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "⑤ 三条硬规矩", 60, y + 16, 15, TitleBrush, bold: true);
        string[] rules =
        {
            "1. 手动收纳，不自动隐藏。Office 2000 的「自适应菜单」就是把不常用的命令从视野里藏起来，",
            "　 结果用户抱怨「命令会自己跑」—— 这是界面史上被反复引用的反面案例，别重演。",
            "2. 抽屉里可以按「最近用过」排序（换位置），但不许「因为不常用就消失」（换存在）。",
            "3. 安全项（笔 / 橡皮 / 后撤）不可移出：用户不该有办法把自己锁在门外。",
        };
        double ry = y + 46;
        foreach (string line in rules)
        {
            Text(c, line, 60, ry, 13, BodyBrush);
            ry += 24;
        }
        return y + 166;
    }

    // =====================================================================
    //  色带三方案
    // =====================================================================

    static double DrawColorSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "笔的颜色怎么选 · 三方案对比（设计稿 v1）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "问题：一整条连续色带点哪算哪（爽，但颜色不固定），还是 9 个色块（稳，但像色板）。",
             40, y + 44, 14, BodyBrush);
        Text(c, "结论先给：长条这个形状是对的，连续那个手感是错的 —— 推荐方案 C（连续 ＋ 9 刻度 ＋ 吸附）。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        y = Section(c, y, "① 三个方案：1:1（左）＋ 放大看细节（右）");
        double w = BarOfWidth(Tools.Length, GroupEnds, -1);

        StripA(c, 220, y + 10, w, 28, 0);
        Text(c, "A 整条连续", 60, y + 18, 13, BodyBrush, bold: true);
        ZoomStrip(c, 860, y + 4, 560, 40, 0);
        Text(c, "一条从红到紫的色相渐变。看着最顺，但它只有色相：取不到黑、白、灰，",
             220, y + 48, 12.5, BodyBrush);
        Text(c, "而且「上次那个红」只能靠记住位置 —— 同一节课里两个人点不出同一个颜色。",
             220, y + 68, 12.5, NoteBrush);

        StripA(c, 220, y + 122, w, 28, 1);
        Text(c, "B 九段色块", 60, y + 130, 13, BodyBrush, bold: true);
        ZoomStrip(c, 860, y + 116, 560, 40, 1);
        Text(c, "每段 64×28 的圆角方块（间隙 2）。命中面积是 26 圆点的 2.6 倍，一次点中，可复现。",
             220, y + 160, 12.5, BodyBrush);
        Text(c, "代价：看着像色板，不如 A 那一条整体；9 段的边界会把条子切碎。",
             220, y + 180, 12.5, NoteBrush);

        StripA(c, 220, y + 234, w, 28, 2);
        Text(c, "C 连续＋吸附", 60, y + 242, 13, new SolidColorBrush(Accent), bold: true);
        ZoomStrip(c, 860, y + 228, 560, 40, 2);
        Text(c, "★ 推荐：视觉上是一条连续的长条（段与段之间 8px 柔和过渡），行为上是 9 段 ——",
             220, y + 272, 12.5, new SolidColorBrush(Accent));
        Text(c, "点或拖到哪里都吸附到最近那一段，并高亮那一段（1px 刻度线 ＋ 上方小三角）。",
             220, y + 292, 12.5, BodyBrush);

        y += 336;

        // ② 对照表
        y = Section(c, y, "② 为什么推荐 C：四个只看数字就能定的维度");
        string[] head = { "方案", "可复现", "黑白灰", "命中面积", "色弱可辨", "上手" };
        string[][] rows =
        {
            new[] { "A 连续", "差（靠记位置）", "无", "592×28", "差", "快但选不准" },
            new[] { "B 九段", "好（第 N 格）", "有", "64×28", "好", "最快" },
            new[] { "C 连续＋吸附", "好（吸到刻度）", "有", "64×28＋容差", "好", "快且准" },
        };
        double tx = 60, tw = SheetW - 120;
        double[] colw = { 170, 200, 100, 220, 120, 160 };

        double cy = y + 10;
        double cx = tx;
        for (int i = 0; i < head.Length; i++)
        {
            Text(c, head[i], cx + 8, cy, 13, TitleBrush, bold: true);
            cx += colw[i];
        }
        Line(c, tx, cy + 24, tx + tw, cy + 24, C(0xD8, 0xDC, 0xE2));
        for (int r = 0; r < 3; r++)
        {
            double ry = cy + 34 + r * 30;
            if (r == 2)
                c.DrawRoundedRectangle(new SolidColorBrush(C(0xEC, 0xF3, 0xFC)), null,
                                       new Rect(tx, ry - 6, tw, 28), 6, 6);
            cx = tx;
            for (int i = 0; i < head.Length; i++)
            {
                bool rec = r == 2;
                Text(c, rows[r][i], cx + 8, ry, 12.5,
                     rec ? new SolidColorBrush(Accent) : BodyBrush, bold: rec && i == 0);
                cx += colw[i];
            }
        }

        y += 150;

        // ③ 结论
        var box = new Rect(40, y, SheetW - 80, 134);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "③ 落地时要注意的四件事", 60, y + 16, 15, TitleBrush, bold: true);
        string[] notes =
        {
            "1. 白笔必须有：投影上白笔是刚需，而纯色相带取不到白 —— 这是「9 色」不能砍的直接理由。",
            "2. 段宽 64、条高 28：手指和笔都好点（WCAG 2.5.8 的目标下限是 24×24，我们远超）。",
            "3. 吸附容差 = 半段宽（约 32 像素）：点在两段交界处也不纠结，往哪边偏就算哪一边。",
            "4. 如果觉得 8px 的柔和过渡画起来麻烦：直接上方案 B，它是零风险解，观感差距很小。",
        };
        double ny = y + 46;
        foreach (string line in notes)
        {
            Text(c, line, 60, ny, 13, BodyBrush);
            ny += 22;
        }
        return y + 148;
    }

    // =====================================================================
    //  面板位置与粗细
    // =====================================================================

    static double DrawLayoutSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "笔的面板放哪、粗细怎么调（设计稿 v1）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "三个问题一起答：① 色带与粗细合成一块面板；② 面板放上面还是下面；③ 那根细线怎么藏、怎么出来。",
             40, y + 44, 14, BodyBrush);
        y += 90;

        // ① 三种面板
        y = Section(c, y, "① 面板怎么和主条长在一起（1:1）");
        double w = BarOfWidth(Tools.Length, GroupEnds, -1);

        Text(c, "A 一体托盘（推荐）", 60, y + 4, 13, new SolidColorBrush(Accent), bold: true);
        Bar(c, 60, y + 26, dark: false, active: 1, hover: -1);
        Tray(c, 60, y + 26 + BarH + 8, w);
        Text(c, "与主条同宽、圆角 16（不是 56÷2＝28）：一眼是主条下面推出来的一层，不是两块东西叠着。",
             60, y + 26 + BarH + 8 + 88 + 10, 12.5, BodyBrush);

        Text(c, "B 两块独立胶囊（不推荐）", 700, y + 4, 13, BodyBrush, bold: true);
        Bar(c, 700, y + 26, dark: false, active: 1, hover: -1);
        Strip(c, 700, y + 26 + BarH + 8, w, 44, false);
        Strip(c, 700, y + 26 + BarH + 8 + 52, w, 44, false);
        Text(c, "两条胶囊之间会出现「两段弧 ＋ 一条缝」，这就是上一张稿提醒过的问题：",
             700, y + 26 + BarH + 8 + 110, 12.5, NoteBrush);
        Text(c, "圆角取高度一半只适合单独一条；叠起来就显得碎。",
             700, y + 26 + BarH + 8 + 130, 12.5, NoteBrush);

        double afterA = y + 26 + BarH + 8 + 88 + 40;

        Text(c, "C 只贴着「笔」的一小段（不推荐）", 60, afterA + 4, 13, BodyBrush, bold: true);
        Bar(c, 60, afterA + 26, dark: false, active: 1, hover: -1);
        Tray(c, 60 + 96, afterA + 26 + BarH + 8, 360, compact: true);
        Text(c, "看着轻，但色带被压到 360 宽（每段只剩 40，手指开始点不准）；",
             700, afterA + 30, 12.5, NoteBrush);
        Text(c, "而且它贴的是「笔」的位置 —— 笔挪到右边，整块面板也跟着跑。",
             700, afterA + 50, 12.5, NoteBrush);

        y = afterA + 176;

        // ② 上还是下
        y = Section(c, y, "② 放上面还是下面：跟着主条走，规则只有一条");
        Text(c, "主条在屏幕中部 → 面板往下方展开（默认）", 60, y + 6, 13, BodyBrush, bold: true);
        Bar(c, 60, y + 30, dark: false, active: 1, hover: -1, maxButtons: 6);
        Tray(c, 60, y + 30 + BarH + 8, BarOfWidth(6, new[] { 0, 4 }, -1), compact: true);

        Text(c, "主条贴到屏幕下边 → 面板自动翻到上方（阈值：下方剩余 ＜ 面板高 ＋ 12）",
             760, y + 6, 13, BodyBrush, bold: true);
        c.DrawRectangle(new SolidColorBrush(C(0xEC, 0xEE, 0xF3)), null, new Rect(760, y + 30, 700, 158));
        c.DrawRectangle(new SolidColorBrush(C(0x30, 0x34, 0x3C)), null, new Rect(760, y + 186, 700, 2));
        Text(c, "屏幕下边（面板翻到上方）", 760 + 520, y + 164, 12, NoteBrush);
        Tray(c, 780, y + 40, BarOfWidth(6, new[] { 0, 4 }, -1), compact: true);
        Bar(c, 780, y + 122, dark: false, active: 1, hover: -1, maxButtons: 6);

        Text(c, "唯一要守的规则：离主条最近的那一条，永远是最常用的那个（颜色）；不管面板在上还是在下。",
             60, y + 208, 13, BodyBrush);
        y += 240;

        // ③ 粗细三态
        y = Section(c, y, "③ 粗细滑条：平时极细，碰到才成形（1:1）");
        double sw = BarOfWidth(6, new[] { 0, 4 }, -1);
        double sly = y + 30;
        Panel(c, 60, sly - 14, sw, 54, 16, false, PanelAlpha, true);
        Slider(c, 76, sly + 6, sw - 32, 0);
        Caption(c, "常态：4px 细线，用当前笔色、35% 透明度；下面垫 32 高的隐形命中区", 40, sly + 36, sw + 40);

        double sx2 = 60 + sw + 60;
        Panel(c, sx2, sly - 14, sw, 54, 16, false, PanelAlpha, true);
        Slider(c, sx2 + 16, sly + 6, sw - 32, 1);
        Caption(c, "悬停 / 触摸：轨道长到 6px，拖块出现，右侧笔尖预览同步变粗", sx2 - 20, sly + 36, sw + 40);

        double zy = sly + 116;
        Panel(c, 60, zy - 20, 420, 96, 16, false, PanelAlpha, true);
        Slider(c, 80, zy + 6, 320, 2);
        Caption(c, "拖动中：拖块变大、笔尖预览跟着变；松手才提交（一次拖动 ＝ 一步粗细变化）", 60, zy + 60, 420);

        Text(c, "为什么「细线」不能只是好看：4px 远低于 WCAG 2.5.8 的 24×24 目标下限，",
             540, zy - 10, 12.5, BodyBrush);
        Text(c, "所以细线下面必须垫一个 32 高的隐形命中区，鼠标和笔才能一点就中。",
             540, zy + 10, 12.5, BodyBrush);
        Text(c, "另外 WCAG 1.4.13 要求「悬停才出现」的内容必须可悬停、可关闭、持续存在 ——",
             540, zy + 38, 12.5, NoteBrush);
        Text(c, "所以那根细线自己也必须能点开，不能只有悬停这一条路。",
             540, zy + 58, 12.5, NoteBrush);

        y = zy + 96;

        // ④ 触摸
        y = Section(c, y, "④ 触摸屏没有悬停：这条路径必须写死成「点一下」");
        double hy = y + 30;
        Panel(c, 60, hy - 14, 400, 54, 16, false, PanelAlpha, true);
        Slider(c, 76, hy + 6, 368, 0);
        Icon(c, "mouse", 260, hy + 6, 24, new SolidColorBrush(Ink));
        Caption(c, "① 手指或笔点一下那根细线", 60, hy + 40, 400);
        Text(c, "→", 480, hy - 4, 24, NoteBrush);
        Panel(c, 520, hy - 14, 400, 54, 16, false, PanelAlpha, true);
        Slider(c, 536, hy + 6, 368, 1);
        Caption(c, "② 轨道立刻长开、拖块出现，手指直接拖", 520, hy + 40, 400);
        Text(c, "③ 点面板外面收起（不要求悬停）", 960, hy + 4, 12.5, BodyBrush);
        y = hy + 74;

        // ⑤ 极简环形
        y = Section(c, y, "⑤ 极简环形版怎么调色、怎么调粗细");
        double rcx = 240, rcy = y + 130;
        ColorRing(c, rcx, rcy, 84, 118, 5);
        Ball(c, rcx, rcy, 40, false, false, Palette[5]);

        double tcx = 560;
        Strip(c, tcx, y + 108, 170, 48, false);
        Ball(c, tcx - 26, y + 132, 40, false, false, PenRed);
        WidthDots(c, tcx + 34, y + 132);
        Caption(c, "粗细不做滑块，做 3 档圆点（细 / 中 / 粗）", tcx - 40, y + 168, 250);

        double nx = 880, nyy = y + 22;
        foreach (string line in new[]
        {
            "色：环形用 9 个楔形，不是 9 个圆点 —— 外半径 118、内半径 84，",
            "　　每段命中区约 60×34，比直径 26 的圆点大得多，手指也点得中；",
            "　　中心那个圆显示当前色（就是收起态那个球的画法，一眼认得出）。",
            "",
            "粗细：环形里塞一根滑块会破坏对称，所以改成笔外侧 3 个圆点，点一下就换档；",
            "　　　要更细的调节就切回完整界面 —— 极简版的目标本来就是「手写和擦除」。",
            "",
            "这也是「两套界面共用一套数据」的例子：色还是那 9 色，只是画成了环。",
        })
        {
            Text(c, line, nx, nyy, 12.5, BodyBrush);
            nyy += 22;
        }

        return Math.Max(rcy + 140, Math.Max(nyy, y + 200)) + 10;
    }

    // =====================================================================
    //  元件（只在这三张稿里用）
    // =====================================================================

    static double BarOfWidth(int count, int[] groups, int moreCount, double scale = 1)
    {
        double w = Pad * 2 + Btn + (SepGap * 2 + 1);
        for (int i = 0; i < count; i++)
        {
            w += Btn;
            if (i < count - 1)
                w += Array.IndexOf(groups, i) >= 0 ? SepGap * 2 + 1 : Gap;
        }
        if (moreCount >= 0) w += SepGap * 2 + 1 + Btn;
        return w * scale;
    }

    /// <summary>和 Bar() 同一个画法，但项目与分组由调用方给（收纳后的主条是子集）。</summary>
    static BarLayout BarOf(DrawingContext c, double x, double y, (string Icon, string Name)[] items, int[] groups,
                           bool dark, int active, int hover, byte alpha = PanelAlpha, double scale = 1,
                           int maxItems = 99, int moreCount = -1)
    {
        var layout = new BarLayout();
        double h = BarH * scale, pad = Pad * scale, btn = Btn * scale,
               gap = Gap * scale, sep = SepGap * scale;

        int shown = Math.Min(items.Length, maxItems);
        double w = BarOfWidth(shown, groups, moreCount, scale);
        layout.Width = w;

        Strip(c, x, y, w, h, dark, alpha);

        double cx = x + pad + btn / 2;
        c.DrawEllipse(new SolidColorBrush(dark ? C(0x3A, 0x3A, 0x3E) : C(0xEC, 0xEE, 0xF2)),
                      null, new Point(cx, y + h / 2), btn / 2 - 2 * scale, btn / 2 - 2 * scale);
        Icon(c, "chevronDown", cx, y + h / 2, IconSize * scale * 0.85, dark ? Brushes.White : new SolidColorBrush(Ink));
        layout.Centers.Add(cx);
        cx += btn / 2 + sep * 2 + 1;

        for (int i = 0; i < shown; i++)
        {
            TileRaw(c, cx + btn / 2, y + h / 2, btn, IconSize * scale, items[i].Icon, dark,
                    active: i == active, hovered: i == hover, pressed: false, disabled: false);
            layout.Centers.Add(cx + btn / 2);
            cx += btn;
            if (i < shown - 1)
            {
                if (Array.IndexOf(groups, i) >= 0)
                {
                    c.DrawRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x28) : C(0x00, 0x00, 0x00, 0x1E)),
                                    null, new Rect(cx + sep, y + h / 2 - 10 * scale, scale, 20 * scale));
                    cx += sep * 2 + 1;
                }
                else cx += gap;
            }
        }

        if (moreCount >= 0)
        {
            c.DrawRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x28) : C(0x00, 0x00, 0x00, 0x1E)),
                            null, new Rect(cx + sep, y + h / 2 - 10 * scale, scale, 20 * scale));
            cx += sep * 2 + 1;
            TileRaw(c, cx + btn / 2, y + h / 2, btn, IconSize * scale, "more", dark,
                    active: false, hovered: false, pressed: false, disabled: false);
            if (moreCount > 0)
                c.DrawEllipse(new SolidColorBrush(Accent), null,
                              new Point(cx + btn - 8 * scale, y + 8 * scale), 3.5 * scale, 3.5 * scale);
            layout.Centers.Add(cx + btn / 2);
        }
        return layout;
    }

    /// <summary>编辑态：可移出的项加虚线框与减号角标。</summary>
    static void BarEdit(DrawingContext c, double x, double y, (string Icon, string Name)[] items, int[] groups)
    {
        var lay = BarOf(c, x, y, items, groups, false, -1, -1);
        var dash = new Pen(new SolidColorBrush(Accent), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        for (int i = 0; i < items.Length; i++)
        {
            string n = items[i].Name;
            if (n is "笔" or "橡皮" or "橡皮擦" or "后撤") continue;   // 安全项，不给虚线框
            double cx = lay.Centers[i + 1];
            c.DrawRoundedRectangle(null, dash, new Rect(cx - 20, y + 8, 40, 40), 8, 8);
            c.DrawEllipse(new SolidColorBrush(Accent), new Pen(Brushes.White, 1), new Point(cx + 14, y + 14), 6.5, 6.5);
            c.DrawLine(new Pen(Brushes.White, 1.6), new Point(cx + 10.5, y + 14), new Point(cx + 17.5, y + 14));
        }
    }

    /// <summary>「更多」抽屉：3 行 × 4 列。</summary>
    static void Drawer(DrawingContext c, double x, double y)
    {
        const double cell = 64, gap = 6, padx = 12;
        double w = padx * 2 + cell * 4 + gap * 3, h = 12 + 28 + cell * 3 + gap * 2 + 12;
        Panel(c, x, y, w, h, 16, false, PanelAlpha, true);
        Text(c, "更多工具", x + padx, y + 10, 13, TitleBrush, bold: true);
        Text(c, "编辑", x + w - padx - 28, y + 10, 12, new SolidColorBrush(Accent));

        (string Icon, string Name)[] pool =
        {
            ("laser", "激光笔"), ("lasso", "选择"), ("capture", "截屏"), ("board", "白板"),
            ("lineWeight", "直线"), ("square", "矩形"), ("circle", "椭圆"), ("triangle", "三角形"),
            ("arrowRight", "箭头"), ("color", "取色"), ("delete", "清空"), ("settings", "设置"),
        };
        for (int i = 0; i < pool.Length; i++)
        {
            int col = i % 4, row = i / 4;
            double cxx = x + padx + col * (cell + gap), cyy = y + 12 + 28 + row * (cell + gap);
            c.DrawRoundedRectangle(new SolidColorBrush(C(0xF3, 0xF5, 0xF8)), null, new Rect(cxx, cyy, cell, cell), 8, 8);
            Icon(c, pool[i].Icon, cxx + cell / 2, cyy + 24, 24, new SolidColorBrush(Ink));
            Caption(c, pool[i].Name, cxx - 8, cyy + 42, cell + 16);
            Icon(c, "pin", cxx + cell - 12, cyy + 12, 12, new SolidColorBrush(C(0x9A, 0xA0, 0xAA)));
        }
    }

    static void Pool(DrawingContext c, double x, double y, int count, string label)
    {
        const double cell = 30, gap = 6;
        double w = 6 * cell + 5 * gap;
        Panel(c, x, y, w + 24, 24 + 2 * cell + gap + 24, 16, false, PanelAlpha, true);
        Text(c, label, x + 12, y + 8, 12, NoteBrush);
        string[] icons = { "mouse", "pen", "highlighter", "laser", "eraser", "lasso",
                           "shapes", "capture", "undo", "redo", "settings", "board" };
        for (int i = 0; i < count && i < icons.Length; i++)
        {
            int col = i % 6, row = i / 6;
            double cxx = x + 12 + col * (cell + gap), cyy = y + 28 + row * (cell + gap);
            c.DrawRoundedRectangle(new SolidColorBrush(C(0xF3, 0xF5, 0xF8)), null, new Rect(cxx, cyy, cell, cell), 6, 6);
            Icon(c, icons[i], cxx + cell / 2, cyy + cell / 2, 16, new SolidColorBrush(Ink));
        }
    }

    static void MiniBar(DrawingContext c, double x, double y, (string Icon, string Name)[] items, string label)
    {
        double w = items.Length * 34 + (items.Length - 1) * 4 + 16;
        Strip(c, x, y, w, 42, false);
        for (int i = 0; i < items.Length; i++)
            TileRaw(c, x + 8 + 17 + i * 38, y + 21, 34, 18, items[i].Icon, false,
                    active: i == 1, hovered: false, pressed: false, disabled: false);
        Text(c, label, x, y + 50, 12, NoteBrush);
    }

    static void Arrow(DrawingContext c, double x, double y)
    {
        var pen = new Pen(new SolidColorBrush(C(0x9A, 0xA0, 0xAA)), 1.5);
        c.DrawLine(pen, new Point(x, y), new Point(x + 40, y));
        c.DrawLine(pen, new Point(x + 32, y - 6), new Point(x + 40, y));
        c.DrawLine(pen, new Point(x + 32, y + 6), new Point(x + 40, y));
    }

    /// <summary>带显式圆角的半透明面板（Strip 只能画胶囊）。</summary>
    static void Panel(DrawingContext c, double x, double y, double w, double h, double radius,
                      bool dark, byte alpha, bool shadow)
    {
        Color fill = dark ? C(0x22, 0x22, 0x24, alpha) : C(0xFF, 0xFF, 0xFF, alpha);
        Color border = dark ? C(0xFF, 0xFF, 0xFF, 0x1F) : C(0x00, 0x00, 0x00, 0x1A);
        if (shadow)
        {
            for (int i = 3; i >= 1; i--)
                c.DrawRoundedRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, (byte)(0x04 + i * 3))), null,
                                       new Rect(x + 0.5, y + 0.5 + i * 1.6, w - 1, h - 1), radius, radius);
        }
        c.DrawRoundedRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(border), 1),
                               new Rect(x + 0.5, y + 0.5, w - 1, h - 1), radius, radius);
    }

    /// <summary>一块托盘：上半色带、下半粗细（compact = 只画色带）。</summary>
    static void Tray(DrawingContext c, double x, double y, double w, bool compact = false)
    {
        double h = compact ? 74 : 88;
        Panel(c, x, y, w, h, 16, false, PanelAlpha, true);
        double sw = w - 32;
        StripA(c, x + 16, y + 12, sw, 28, 2);
        double sy = y + 12 + 28 + 12;
        if (!compact)
        {
            c.DrawRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x14)), null, new Rect(x + 16, sy - 6, sw, 1));
            Slider(c, x + 16, sy + 8, sw, 0);
        }
        else Slider(c, x + 16, sy + 2, sw, 0);
    }

    /// <summary>色带的三种画法：0 连续、1 九段、2 连续＋刻度吸附。</summary>
    static void StripA(DrawingContext c, double x, double y, double w, double h, int kind)
    {
        if (kind == 1)
        {
            double seg = (w - 8 * 2) / 9;
            for (int i = 0; i < 9; i++)
            {
                double sx = x + i * (seg + 2);
                // 白块在浅色底上会「消失」，必须自己带一圈描边 —— 这是真问题，不是装饰
                var edge = i == 8 ? new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x33)), 1) : null;
                c.DrawRoundedRectangle(new SolidColorBrush(Palette[i]), edge, new Rect(sx, y, seg, h), 6, 6);
            }
            double bsx = x + 5 * (seg + 2);
            c.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(C(0x2A, 0x2E, 0x36)), 1.5),
                                   new Rect(bsx, y, seg, h), 6, 6);
            c.DrawEllipse(null, new Pen(Brushes.White, 2), new Point(bsx + seg / 2, y + h / 2), 8, 8);
            return;
        }

        if (kind == 0)
        {
            // 纯色相渐变：只走红→紫，正好演示"取不到黑白"
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i <= 6; i++)
                b.GradientStops.Add(new GradientStop(Palette[i], i / 6.0));
            c.DrawRoundedRectangle(b, new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x1A)), 1),
                                   new Rect(x, y, w, h), h / 2, h / 2);
            double px = x + w * 0.42;
            c.DrawEllipse(null, new Pen(new SolidColorBrush(C(0x2A, 0x2E, 0x36)), 1.5), new Point(px, y + h / 2), 12, 12);
            c.DrawEllipse(null, new Pen(Brushes.White, 2), new Point(px, y + h / 2), 8, 8);
            return;
        }

        // kind == 2：连续 ＋ 9 刻度 ＋ 吸附
        var ramp = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < 9; i++)
        {
            double a = (double)i / 9, z = (double)(i + 1) / 9;
            ramp.GradientStops.Add(new GradientStop(Palette[i], a + 0.006));
            ramp.GradientStops.Add(new GradientStop(Palette[i], z - 0.006));
        }
        ramp.GradientStops.Insert(0, new GradientStop(Palette[0], 0));
        ramp.GradientStops.Add(new GradientStop(Palette[8], 1));
        c.DrawRoundedRectangle(ramp, new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x1A)), 1),
                               new Rect(x, y, w, h), h / 2, h / 2);

        double segw = w / 9;
        for (int i = 1; i < 9; i++)
            c.DrawRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x55)), null,
                            new Rect(x + i * segw, y + 3, 1, h - 6));

        double sel = x + 5.5 * segw;
        c.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(C(0x2A, 0x2E, 0x36)), 1.5),
                               new Rect(x + 5 * segw + 2, y + 2, segw - 4, h - 4), 8, 8);
        c.DrawEllipse(null, new Pen(Brushes.White, 2), new Point(sel, y + h / 2), 8, 8);

        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(sel, y - 3), true, true);
            g.LineTo(new Point(sel - 6, y - 11), true, false);
            g.LineTo(new Point(sel + 6, y - 11), true, false);
        }
        tri.Freeze();
        c.DrawGeometry(new SolidColorBrush(C(0x2A, 0x2E, 0x36)), null, tri);
    }

    /// <summary>把一段色带放大看细节。</summary>
    static void ZoomStrip(DrawingContext c, double x, double y, double w, double h, int kind)
    {
        Panel(c, x - 10, y - 10, w + 20, h + 44, 10, false, PanelAlpha, true);
        StripA(c, x, y, w, h, kind);
        string note = kind switch
        {
            0 => "放大：平滑渐变，但没有任何刻度 —— 上一次那个红在哪？",
            1 => "放大：段边界干净，一眼数得出第几个；缺点是条子被切成 9 块。",
            _ => "放大：8px 柔和过渡 ＋ 1px 白色刻度线；选中的那段有深色描边和小三角。",
        };
        Text(c, note, x, y + h + 8, 12, NoteBrush);
    }

    /// <summary>粗细滑条：state 0 常态、1 悬停/触摸、2 拖动中。</summary>
    static void Slider(DrawingContext c, double x, double y, double w, int state)
    {
        double trackH = state == 0 ? 4 : 6;
        double cy = y + 10;
        Color trackColor = state == 0 ? C(0x1C, 0x1F, 0x26, 0x59) : C(0x1C, 0x1F, 0x26, 0x99);
        c.DrawRoundedRectangle(new SolidColorBrush(trackColor), null,
                               new Rect(x, cy - trackH / 2, w, trackH), trackH / 2, trackH / 2);

        double knob = state == 2 ? 0.62 : 0.45;
        c.DrawRoundedRectangle(new SolidColorBrush(Accent), null,
                               new Rect(x, cy - trackH / 2, w * knob, trackH), trackH / 2, trackH / 2);
        if (state >= 1)
        {
            c.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(C(0x30, 0x34, 0x3C)), 1),
                          new Point(x + w * knob, cy), 8, 8);
            double r = state == 2 ? 9 : 6;
            c.DrawEllipse(new SolidColorBrush(C(0x1C, 0x1F, 0x26)), null, new Point(x + w - 10, cy), r, r);
        }
    }

    /// <summary>环形色板：9 个楔形。</summary>
    static void ColorRing(DrawingContext c, double cx, double cy, double rIn, double rOut, int selected)
    {
        for (int i = 0; i < 9; i++)
        {
            double a0 = -90 + i * 40 + 1, a1 = -90 + (i + 1) * 40 - 1;
            Wedge(c, cx, cy, rIn, rOut, a0, a1, new SolidColorBrush(Palette[i]));
            if (i == selected)
                c.DrawEllipse(Brushes.White, null, Polar(cx, cy, rOut - 12, (a0 + a1) / 2), 3.5, 3.5);
        }
    }

    static void WidthDots(DrawingContext c, double cx, double cy)
    {
        double[] rs = { 3.5, 5.5, 8 };
        for (int i = 0; i < 3; i++)
        {
            double x = cx + (i - 1) * 26;
            if (i == 2) c.DrawEllipse(new SolidColorBrush(C(0xE8, 0xEE, 0xF7)), null, new Point(x, cy), 16, 16);
            c.DrawEllipse(new SolidColorBrush(C(0x1C, 0x1F, 0x26)), null, new Point(x, cy), rs[i], rs[i]);
        }
    }

    static void Wedge(DrawingContext c, double cx, double cy, double rIn, double rOut, double a0, double a1, Brush b)
    {
        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(Polar(cx, cy, rIn, a0), true, true);
            gc.LineTo(Polar(cx, cy, rOut, a0), true, false);
            gc.ArcTo(Polar(cx, cy, rOut, a1), new Size(rOut, rOut), 0, false, SweepDirection.Clockwise, true, false);
            gc.LineTo(Polar(cx, cy, rIn, a1), true, false);
            gc.ArcTo(Polar(cx, cy, rIn, a0), new Size(rIn, rIn), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        c.DrawGeometry(b, null, g);
    }

    static Point Polar(double cx, double cy, double r, double deg)
    {
        double a = deg * Math.PI / 180.0;
        return new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
    }
}
