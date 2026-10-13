using System;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v2：把「色带 ＋ 按钮 ＋ 粗细」融合成一块三带面板。
/// 色带当腰线、粗细当踢脚线 —— 装饰件同时是功能件。
/// 两张稿：形态与比例、交互与边界。
/// </summary>
internal static partial class Program
{
    const double BandFull = 28;    // 色带满高
    const double BandRail = 10;    // 腰线（细的那种）
    const double Groove = 16;      // 底部凹槽带（粗细条住这里）
    const double FuseRadius = 18;  // 融合面板的圆角（不跟着高度变成胶囊）

    static double FuseHeight(int level, bool hot = false)
    {
        double band = level >= 3 ? (hot ? BandFull : BandRail) : BandFull;
        return band + BarH + Groove;
    }

    // =====================================================================
    //  形态与比例
    // =====================================================================

    static double DrawFuseFormSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "色带 ＋ 按钮 ＋ 粗细 · 融合成一块「三带」面板", 40, y, 30, TitleBrush, bold: true);
        Text(c, "你的想法：色带当腰线、粗细当踢脚线，装饰件同时是功能件。这张稿把它拆成四个「常驻程度」，从最张扬到最克制。",
             40, y + 44, 14, BodyBrush);
        Text(c, "结论先给：能做，而且可能比上一轮的「点它才弹出来」更好 —— 前提是默认态必须收敛，否则色带会比板书还抢眼。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        double w = BarOfWidth(Tools.Length, GroupEnds, -1);

        // ① 两种顺序
        y = Section(c, y, "① 两种顺序（1:1，都画成贴在屏幕下边的样子）");
        Text(c, "A 色带在上：腰线 ＋ 按钮 ＋ 踢脚线（推荐）", 60, y + 2, 13, new SolidColorBrush(Accent), bold: true);
        FusedPanel(c, 60 + 34, y + 18, w, 2, hot: false);
        Edge(c, 60, y + 130, 700, "屏幕下边");
        Text(c, "色带 28 ／ 按钮 56 ／ 凹槽 16 ＝ 总高 100，离屏幕边 12。", 60, y + 146, 12, NoteBrush);

        Text(c, "B 色带在下：粗细 ＋ 按钮 ＋ 色带", 800, y + 2, 13, BodyBrush, bold: true);
        FusedPanel(c, 800 + 34, y + 18, w, 2, hot: false, flip: true);
        Edge(c, 800, y + 130, 700, "屏幕下边");
        Text(c, "顺序换一下而已，代码里就是画布上换两行。最后应该上机各用一天再定。", 800, y + 146, 12, NoteBrush);

        y += 182;

        // ② 四个常驻程度
        y = Section(c, y, "② 四个「常驻程度」：从最张扬到最克制（1:1）");
        var ladder = new (int Level, string Title, string Note)[]
        {
            (1, "L1 满饱和常驻", "9 段满饱和色平铺整条。最像色带，但它会成为全屏最抢眼的东西 —— 抢板书。"),
            (2, "L2 降饱和常驻", "每段混 45% 白，只有选中的那一段满饱和。收敛多了，但仍然一眼就看到「有彩色」。"),
            (3, "L3 细腰线（9 段）", "★推荐：平时只有 10 高、整条低饱和；选中的那段 14 高、满饱和 —— 像一道装饰线，还点得到。"),
            (4, "L4 细腰线（只亮当前色）", "★最保守：平时只有一段当前色，其余是一道浅槽；点一下整条长开成 9 段。"),
        };
        double ly = y + 6;
        foreach (var it in ladder)
        {
            double h = FuseHeight(it.Level);
            Text(c, it.Title, 60, ly + 14, 13,
                 it.Level >= 3 ? new SolidColorBrush(Accent) : BodyBrush, bold: it.Level >= 3);
            double th = Paragraph(c, it.Note, 60, ly + 36, 640, 12, NoteBrush);
            Text(c, $"总高 {h:F0}", 60, ly + 36 + th + 6, 12, NoteBrush);
            FusedPanel(c, 760, ly + 6, w, it.Level, hot: false);
            ly += Math.Max(h + 12, 36 + th + 30) + 26;
        }

        y = ly + 2;

        // ③ 抢戏测试
        y = Section(c, y, "③ 抢戏测试：同一张真实感 PPT（有图文、有红色批注）上，谁在抢戏");
        Slide(c, 60, y + 8, 900, 150);
        FusedPanel(c, 214, y + 8 + 150 - FuseHeight(1) - 8, w, 1, hot: false);
        Caption(c, "L1：色带比板书还抢眼，视线先落在工具条上", 60, y + 166, 900);

        Slide(c, 60, y + 208, 900, 150);
        FusedPanel(c, 214, y + 208 + 150 - FuseHeight(3) - 8, w, 3, hot: false);
        Caption(c, "L3：同一位置，工具条退成一道装饰线，注意力还在板书上", 60, y + 366, 900);

        Text(c, "这一关是整套想法里最关键的：批注软件的工具条不该是屏幕上最好看的东西，",
             1010, y + 24, 12.5, BodyBrush);
        Text(c, "它应该是「看着精致、但视线上不抢」的那种好。L1 做不到，L3/L4 能做到。",
             1010, y + 44, 12.5, BodyBrush);
        Text(c, "底层原因：我们是盖在别人的 PPT 上的，画面主体永远是内容而不是界面。",
             1010, y + 78, 12.5, NoteBrush);
        Text(c, "所以「装饰」对我们来说只能是细线、浅色、低对比 —— 一旦它比内容还亮，",
             1010, y + 98, 12.5, NoteBrush);
        Text(c, "用户的第一反应就是「挡我看东西了」，然后干脆把工具条关掉。",
             1010, y + 118, 12.5, NoteBrush);

        y += 404;

        // ④ 比例
        y = Section(c, y, "④ 比例：三条带不能等分，也不能对称");
        Text(c, "等分（1:1:1）", 60, y + 12, 12.5, BodyBrush);
        RatioBar(c, 300, y + 6, 700, new[] { 33.3, 33.4, 33.3 }, new[] { "色带", "按钮", "凹槽" });
        Text(c, "看着像被切了三刀，三条带互相争位置 —— 室内腰线也不会钉在墙的正中间。",
             300, y + 46, 12, NoteBrush);

        Text(c, "我们的比例（28 : 56 : 16）", 60, y + 78, 12.5, new SolidColorBrush(Accent), bold: true);
        RatioBar(c, 300, y + 72, 700, new double[] { 28, 56, 16 }, new[] { "色带 28", "按钮 56", "凹槽 16" });
        Text(c, "中间带最厚（按钮是主体），两侧薄且不相等（28 对 16）—— 有节奏，不呆板。",
             300, y + 112, 12, NoteBrush);
        Text(c, "凹槽做得最薄是故意的：它是踢脚线，本来就该几乎看不见；而 32 高的命中区可以向上溢出到按钮带的内边距里（那里本来就是空的）。",
             300, y + 132, 12, NoteBrush);

        y += 168;

        // ⑤ 结论
        var box = new Rect(40, y, SheetW - 80, 158);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "⑤ 可行性判断：能做，但要同时满足三个前提", 60, y + 16, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. 默认态必须收敛：上 L3 或 L4。L1/L2 那种满饱和度常驻，在真实 PPT 上一定抢戏（第 ③ 段）。",
            "2. 色带与按钮之间留 6 像素缓冲，并且只在「按下与抬起落在同一段」时才真的换色 —— 防误触。",
            "3. 设置里给一个「显示装饰带」开关，关掉就退回 56 高的单条（老用户的逃生口）。",
            "",
            "三条都满足，这块面板就比上一轮的「点笔才弹托盘」更好：颜色常驻、一步到位，平时还在起装饰作用。",
            "满足不了（比如上机试下来还是抢戏），就退回上一轮的两带托盘，损失不大。",
        };
        double cy = y + 46;
        foreach (string line in concl)
        {
            Text(c, line, 60, cy, 13, BodyBrush);
            cy += 22;
        }
        return y + 172;
    }

    // =====================================================================
    //  交互与边界
    // =====================================================================

    static double DrawFuseUxSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "三带面板 · 交互与边界（设计稿 v2）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "融合之后要解决的问题：怎么不误触、粗细怎么藏、收起来露哪一条、贴左右两边怎么办。",
             40, y + 44, 14, BodyBrush);
        y += 90;

        double w = BarOfWidth(Tools.Length, GroupEnds, -1);

        // ① 色带交互
        y = Section(c, y, "① 色带：一步换色，但要防误触");
        FusedPanel(c, 60, y + 30, w, 3, hot: false);
        c.DrawRectangle(new SolidColorBrush(C(0xF2, 0x9E, 0x2E, 0x33)), null,
                        new Rect(60, y + 30 + BandRail, w, 6));
        Text(c, "6px 缓冲带", 60 + w + 12, y + 30 + BandRail - 4, 12, NoteBrush);
        Text(c, "按下与抬起都落在同一段才算换色；从色带滑到按钮上再松开，不算。",
             60, y + 124, 12.5, BodyBrush);
        Text(c, "缓冲带里不响应任何东西 —— 它是「手滑一下」的容错，不是可点区域。",
             60, y + 144, 12.5, NoteBrush);
        Text(c, "沿色带横拖 = 连续试色（松手才提交，一次拖动一步撤销）。",
             60, y + 170, 12.5, NoteBrush);
        y += 200;

        // ② 粗细凹槽
        y = Section(c, y, "② 粗细凹槽：平时是踢脚线，碰到才成形");
        double gy = y + 40;
        FusedPanel(c, 60, gy, w, 3, hot: false);
        Caption(c, "常态：4px 凹槽（当前笔色 35%），看着就是一道收边", 40, gy + FuseHeight(3) + 10, w + 40);
        FusedPanel(c, 820, gy, w, 3, hot: true, hotSlider: true);
        Caption(c, "悬停 / 触摸：凹槽长到 6px，16 的拖块浮出，右侧笔尖预览同步变粗", 800, gy + FuseHeight(3, true) + 10, w + 40);

        double ty = gy + FuseHeight(3, true) + 48;
        Text(c, "命中区可以「溢出」：凹槽带只有 16 高，但命中区做 32 —— 多出的 8 向上伸进按钮带的下内边距、8 伸到面板外。",
             60, ty, 12.5, BodyBrush);
        Text(c, "因为那两处本来就是空的（按钮带上下各 8 的内边距、面板下方 12 的留白），所以不抢任何按钮的点击。",
             60, ty + 20, 12.5, NoteBrush);
        Text(c, "WCAG 2.5.8 要求目标 ≥ 24×24；这条「借空位」的算法就是把它做到 32 而不加高面板的办法。",
             60, ty + 40, 12.5, NoteBrush);
        y = ty + 68;

        // ③ 收起贴边露哪一条
        y = Section(c, y, "③ 收起来贴边时，露出来的那 8 像素 = 色带");
        double hy = y + 34;
        FusedPanel(c, 214, hy - 8, w, 3, hot: false, clipTop: 8);
        Edge(c, 60, hy, 700, "屏幕下边");
        Text(c, "面板收起贴边、只露上沿 8 像素 —— 露出的正好是那条色带：", 60, hy + 30, 12.5, BodyBrush);
        Text(c, "屏幕边上留一道彩色细线，既是「工具在这儿」的提示，本身又是装饰。", 60, hy + 50, 12.5, BodyBrush);
        Text(c, "（这是「装饰即功能」最漂亮的一处：换作单条方案，露出来的只是白色边缘，看不出是什么。）",
             60, hy + 70, 12.5, NoteBrush);
        Text(c, "替代做法：整块淡出成半透明幽灵，同样露出色带。", 1000, hy + 30, 12.5, NoteBrush);
        Text(c, "两种都不贵，上机比一比哪个更容易被找到。", 1000, hy + 50, 12.5, NoteBrush);
        y = hy + 104;

        // ④ 左右停靠
        y = Section(c, y, "④ 贴左 / 右边缘时：三带要不要变成三列");
        Text(c, "做法一：整体转 90°（三列，色带仍在朝内的一侧）", 60, y + 6, 12.5, new SolidColorBrush(Accent), bold: true);
        VerticalFuse(c, 90, y + 30);
        Text(c, "色带 28 宽、按钮 56 宽、凹槽 16 宽；圆角与内边距同步旋转。", 210, y + 70, 12.5, BodyBrush);
        Text(c, "好处：观感与上下停靠完全一致。", 210, y + 92, 12.5, NoteBrush);
        Text(c, "代价：竖直方向要放 592 高，在 900 逻辑高的屏上占掉三分之二，", 210, y + 112, 12.5, NoteBrush);
        Text(c, "　　　得靠收纳把按钮减到 8 项以内。", 210, y + 132, 12.5, NoteBrush);

        Text(c, "做法二：左右停靠时不显示装饰带，退回单列", 800, y + 6, 12.5, BodyBrush, bold: true);
        VerticalBar(c, 830, y + 30);
        Text(c, "只保留按钮列（＋一条 3px 的当前色细线），面板宽 56。", 910, y + 70, 12.5, BodyBrush);
        Text(c, "好处：占地最小，左右停靠本来就是省地方的用法。", 910, y + 92, 12.5, NoteBrush);
        Text(c, "代价：两种停靠的观感不一致（一个有三带、一个没有）。", 910, y + 112, 12.5, NoteBrush);

        y += 500;

        // ⑤ 展开动画
        y = Section(c, y, "⑤ 展开动画：球 → 三带面板，一口气长出来（200ms）");
        double ay = y + 46;
        Ball(c, 84, ay, 48, false, false, PenRed);
        Caption(c, "收起：球", 40, ay + 36, 88);
        Text(c, "→", 170, ay - 10, 22, NoteBrush);
        FusedPanel(c, 250, ay - FuseHeight(3) / 2, 320, 3, hot: false, ellipseEnd: true, buttons: 0);
        Caption(c, "中段：横向拉长，色带先出现", 180, ay + 62, 460);
        Text(c, "→", 590, ay - 10, 22, NoteBrush);
        FusedPanel(c, 660, ay - FuseHeight(3) / 2, w, 3, hot: false);
        Caption(c, "到位：592 × 82", 640, ay + 62, 640);
        Text(c, "变形只做两件事：宽度 48→592、高度 48→82；圆角一路从 24 降到 18，色带与凹槽跟着一起长出来。",
             60, ay + 96, 12.5, BodyBrush);
        Text(c, "不做的：不旋转、不淡入淡出、不逐个按钮弹跳 —— 工具条是为了快，超过 250ms 就成了表演。",
             60, ay + 116, 12.5, NoteBrush);
        y = ay + 146;

        // ⑥ 代价
        y = Section(c, y, "⑥ 代价账（和上一轮的两带托盘、以及现状比）");
        string[] cost =
        {
            "单条（现状）56 高：592 × 56 ＝ 3.3 万像素²",
            "L3 常态 82 高：592 × 82 ＝ 4.9 万像素²（＋46%）　← 推荐",
            "L1/L2 常态 100 高：592 × 100 ＝ 5.9 万像素²（＋79%）",
            "",
            "换算成屏幕占比：1440 × 900 的逻辑屏上，L3 占 3.7%，L1 占 4.6%。",
            "半透明填充本身几乎不要钱（第一轮算过：13 万像素/帧是微秒级），真正的代价是「多挡住的板书面积」。",
            "所以选 L3/L4 而不是 L1/L2，主要不是性能账，是「别挡内容」这笔账。",
        };
        double cyy = y + 14;
        foreach (string line in cost)
        {
            Text(c, line, 60, cyy, 12.5, BodyBrush);
            cyy += 22;
        }
        return cyy + 12;
    }

    // =====================================================================
    //  元件
    // =====================================================================

    /// <summary>
    /// 三带面板。level：1 满饱和 / 2 降饱和 / 3 细腰线(9 段) / 4 细腰线(只亮当前色)。
    /// flip = 色带放下面；hot = 色带展开成满高；hotSlider = 拖块浮出；
    /// buttons = 画几个按钮（0 = 只画壳，给动画中帧用）；clipTop = 只露上沿几像素。
    /// </summary>
    static double FusedPanel(DrawingContext c, double x, double y, double w, int level, bool hot,
                             bool flip = false, bool hotSlider = false, bool ellipseEnd = false,
                             double clipTop = 0, int buttons = -1)
    {
        double band = level >= 3 ? (hot ? BandFull : BandRail) : BandFull;
        double total = band + BarH + Groove;
        double r = ellipseEnd ? 24 : FuseRadius;

        Panel(c, x, y, w, total, r, false, PanelAlpha, true);

        c.PushClip(new RectangleGeometry(new Rect(x, y, w, total), r, r));
        double bandY = flip ? y + total - band : y;
        DrawBand(c, x, bandY, w, band, level, hot);
        if (band > 12)
        {
            double lineY = flip ? bandY : bandY + band;
            c.DrawLine(new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x12)), 1),
                       new Point(x, lineY), new Point(x + w, lineY));
        }
        c.Pop();

        double rowY = flip ? y + Groove : y + band;
        double gy = flip ? y : y + band + BarH;
        DrawGroove(c, x, gy, w, Groove, hotSlider);
        FusedButtons(c, x, rowY, buttons < 0 ? Tools.Length : buttons, hotSlider ? 5 : 5);

        if (clipTop > 0)
            c.DrawRectangle(Brushes.White, null, new Rect(x - 2, y + clipTop, w + 4, total));
        return total;
    }

    /// <summary>9 段色带：1 满饱和、2 降饱和、3 细腰线、4 只亮当前色那一段。</summary>
    static void DrawBand(DrawingContext c, double x, double y, double w, double h, int level, bool hot)
    {
        double seg = w / 9;
        const int sel = 5;
        for (int i = 0; i < 9; i++)
        {
            bool isSel = i == sel;
            double sx = x + i * seg, sy = y, sh = h;
            if (level >= 3 && !hot && isSel) { sy = y - 2; sh = h + 4; }

            Brush b;
            if (level == 1) b = new SolidColorBrush(Palette[i]);
            else if (level == 2) b = new SolidColorBrush(Mix(Palette[i], Colors.White, 0.45));
            else if (level == 4 && !isSel) b = new SolidColorBrush(C(0x00, 0x00, 0x00, 0x0D));
            else b = new SolidColorBrush(Mix(Palette[i], Colors.White, hot ? 0.15 : 0.45));

            c.DrawRectangle(b, null, new Rect(sx, sy, seg + 0.5, sh));
            if (i > 0 && level != 4)
                c.DrawRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x66)), null, new Rect(sx, sy, 1, sh));
        }

        if (level >= 3)
        {
            double sx = x + sel * seg;
            double sy = hot ? y : y - 2, sh = hot ? h : h + 4;
            c.DrawRectangle(new SolidColorBrush(Palette[sel]), null, new Rect(sx, sy, seg, sh));
            c.DrawRectangle(null, new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x30)), 1),
                            new Rect(sx + 0.5, sy + 0.5, seg - 1, sh - 1));
        }
    }

    static void DrawGroove(DrawingContext c, double x, double y, double w, double h, bool hot)
    {
        double cy = y + h / 2, inset = 16, trackH = hot ? 6 : 4;
        c.DrawRoundedRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x0F)), null,
                               new Rect(x + inset, cy - trackH / 2, w - inset * 2, trackH), trackH / 2, trackH / 2);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF2, 0x2E, 0x2E, hot ? (byte)0x8C : (byte)0x59)), null,
                               new Rect(x + inset, cy - trackH / 2, (w - inset * 2) * 0.45, trackH), trackH / 2, trackH / 2);
        if (!hot) return;
        c.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(C(0x30, 0x34, 0x3C)), 1),
                      new Point(x + inset + (w - inset * 2) * 0.45, cy), 8, 8);
        c.DrawEllipse(new SolidColorBrush(C(0x1C, 0x1F, 0x26)), null, new Point(x + w - inset - 10, cy), 6, 6);
    }

    /// <summary>面板里的按钮行（外框由三带面板提供）。</summary>
    static void FusedButtons(DrawingContext c, double x, double yTop, int count, int active)
    {
        if (count <= 0) return;
        double cx = x + Pad + Btn / 2;
        c.DrawEllipse(new SolidColorBrush(C(0xEC, 0xEE, 0xF2)), null,
                      new Point(cx, yTop + BarH / 2), Btn / 2 - 2, Btn / 2 - 2);
        Icon(c, "chevronDown", cx, yTop + BarH / 2, IconSize * 0.85, new SolidColorBrush(Ink));
        cx += Btn / 2 + SepGap * 2 + 1;

        for (int i = 0; i < count; i++)
        {
            TileRaw(c, cx + Btn / 2, yTop + BarH / 2, Btn, IconSize, Tools[i].Icon, false,
                    active: i == active, hovered: false, pressed: false, disabled: false);
            cx += Btn;
            if (i < count - 1)
            {
                if (Array.IndexOf(GroupEnds, i) >= 0)
                {
                    c.DrawRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x1E)), null,
                                    new Rect(cx + SepGap, yTop + BarH / 2 - 10, 1, 20));
                    cx += SepGap * 2 + 1;
                }
                else cx += Gap;
            }
        }
    }

    /// <summary>竖直停靠：三带转 90°（三列）。</summary>
    static void VerticalFuse(DrawingContext c, double x, double y)
    {
        double w = BandFull + BarH + Groove;
        double h = 9 * 44 + 16;
        Panel(c, x, y, w, h, FuseRadius, false, PanelAlpha, true);
        c.PushClip(new RectangleGeometry(new Rect(x, y, w, h), FuseRadius, FuseRadius));
        double seg = h / 9;
        for (int i = 0; i < 9; i++)
            c.DrawRectangle(new SolidColorBrush(Mix(Palette[i], Colors.White, 0.45)), null,
                            new Rect(x, y + i * seg, BandFull, seg + 0.5));
        c.Pop();
        c.PushClip(new RectangleGeometry(new Rect(x, y, w, h), FuseRadius, FuseRadius));
        c.DrawRectangle(new SolidColorBrush(Palette[5]), null, new Rect(x - 2, y + 5 * seg, BandFull + 4, seg + 0.5));
        c.Pop();

        double bx = x + BandFull + BarH / 2;
        for (int i = 0; i < 9; i++)
            TileRaw(c, bx, y + 8 + 20 + i * (40 + 4), 40, IconSize, Tools[i % Tools.Length].Icon, false,
                    active: i == 1, hovered: false, pressed: false, disabled: false);
        double gx = x + BandFull + BarH + Groove / 2;
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF2, 0x2E, 0x2E, 0x59)), null,
                               new Rect(gx - 2, y + 16, 4, h - 32), 2, 2);
    }

    /// <summary>左右停靠的备选：退回单列。</summary>
    static void VerticalBar(DrawingContext c, double x, double y)
    {
        double w = 56, h = 9 * 44 + 16;
        Panel(c, x, y, w, h, 28, false, PanelAlpha, true);
        double bx = x + w / 2;
        for (int i = 0; i < 9; i++)
            TileRaw(c, bx, y + 8 + 20 + i * (40 + 4), 40, IconSize, Tools[i % Tools.Length].Icon, false,
                    active: i == 1, hovered: false, pressed: false, disabled: false);
        c.DrawRoundedRectangle(new SolidColorBrush(Palette[5]), null, new Rect(x + 2, y + 28, 3, h - 56), 1.5, 1.5);
    }

    /// <summary>模拟一张真实 PPT：标题、正文条、柱状图、红色手写批注。</summary>
    static void Slide(DrawingContext c, double x, double y, double w, double h)
    {
        c.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(C(0xDA, 0xDE, 0xE4)), 1),
                               new Rect(x, y, w, h), 6, 6);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0x1F, 0x2A, 0x3A)), null, new Rect(x + 24, y + 20, 260, 14), 4, 4);
        for (int i = 0; i < 3; i++)
            c.DrawRoundedRectangle(new SolidColorBrush(C(0xE3, 0xE7, 0xEC)), null,
                                   new Rect(x + 24, y + 50 + i * 18, 300 - i * 40, 9), 4, 4);
        for (int i = 0; i < 4; i++)
            c.DrawRoundedRectangle(new SolidColorBrush(i % 2 == 0 ? Palette[5] : Palette[3]), null,
                                   new Rect(x + 420 + i * 70, y + h - 34 - (20 + i * 18), 42, 20 + i * 18), 3, 3);
        Scribble(c, x + 60, y + 96, 260, C(0xE0, 0x2B, 0x2B), 3);
        Scribble(c, x + 380, y + 118, 300, C(0xE0, 0x2B, 0x2B), 3);
        c.DrawEllipse(null, new Pen(new SolidColorBrush(C(0x21, 0x73, 0xE6)), 3),
                      new Point(x + 620, y + h - 48), 46, 26);
    }

    static void Scribble(DrawingContext c, double x, double y, double w, Color color, double thickness)
    {
        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(new Point(x, y), false, false);
            gc.BezierTo(new Point(x + w * 0.22, y - 20), new Point(x + w * 0.34, y + 16), new Point(x + w * 0.52, y - 4), true, false);
            gc.BezierTo(new Point(x + w * 0.72, y - 24), new Point(x + w * 0.82, y + 12), new Point(x + w, y - 8), true, false);
        }
        g.Freeze();
        c.DrawGeometry(null, new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, g);
    }

    static void Edge(DrawingContext c, double x, double y, double w, string label)
    {
        c.DrawRectangle(new SolidColorBrush(C(0x30, 0x34, 0x3C)), null, new Rect(x, y, w, 2));
        Text(c, label, x + w - 90, y + 6, 12, NoteBrush);
    }

    /// <summary>会自动折行的一段说明，返回它占的高度。</summary>
    static double Paragraph(DrawingContext c, string s, double x, double y, double width, double size, Brush b)
    {
        var ft = Fmt(s, size, b);
        ft.MaxTextWidth = width;
        c.DrawText(ft, new Point(x, y));
        return ft.Height;
    }

    static void RatioBar(DrawingContext c, double x, double y, double w, double[] parts, string[] labels)
    {
        double total = 0;
        foreach (double p in parts) total += p;
        double cx = x;
        for (int i = 0; i < parts.Length; i++)
        {
            double bw = w * parts[i] / total;
            Color col = i == 0 ? C(0xF2, 0x2E, 0x2E) : i == 1 ? C(0xF3, 0xF5, 0xF8) : C(0x5A, 0x5E, 0x66);
            c.DrawRectangle(new SolidColorBrush(col), new Pen(new SolidColorBrush(C(0xD0, 0xD4, 0xDA)), 1),
                            new Rect(cx, y, bw, 30));
            var ft = Fmt(labels[i], 11.5, i == 1 ? BodyBrush : Brushes.White);
            ft.MaxTextWidth = bw;
            ft.TextAlignment = TextAlignment.Center;
            c.DrawText(ft, new Point(cx, y + 8));
            cx += bw;
        }
    }

    static Color Mix(Color a, Color b, double t)
    {
        return Color.FromArgb(a.A,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
