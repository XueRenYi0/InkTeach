using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DesignSheet;

/// <summary>
/// v16：课堂窗对照稿 —— 计时窗 / 点名窗。
/// 尺寸与配色逐项抄自本机 InkClass（GPL-3.0）
/// CountdownTimerWindow.xaml(.cs) / RandWindow.xaml(.cs)：
///   面板 #F0F3F9、1px 描边 #0066BF、圆角 10、强调 #0066BF、关闭 #E32A34、
///   次级白钮 #FBFBFD、药丸 #E8EAF0、数字待机 #5B5D5F。
/// 新增（2026-10-02 拍板）：三模式页签、到点超时口径、随机一人去重池。
/// </summary>
internal static partial class Program
{
    // ── InkClass 课堂窗配色（源码原值） ────────────────────────────────
    static readonly Color WinPanel      = C(0xF0, 0xF3, 0xF9);
    static readonly Color WinAccent     = C(0x00, 0x66, 0xBF);
    static readonly Color WinDanger     = C(0xE3, 0x2A, 0x34);
    static readonly Color WinLightBtn   = C(0xFB, 0xFB, 0xFD);
    static readonly Color WinDisableBg  = C(0xF3, 0xF5, 0xF9);
    static readonly Color WinDisableInk = C(0x9D, 0x9D, 0x9E);
    static readonly Color WinPill       = C(0xE8, 0xEA, 0xF0);
    static readonly Color WinDigitIdle  = C(0x5B, 0x5D, 0x5F);
    static readonly Color WinCover      = C(0xBF, 0xBF, 0xBF);
    static readonly Color WinMuted      = C(0x7C, 0x82, 0x8C);
    static readonly Color WinLine       = C(0xD8, 0xDD, 0xE6);
    static readonly Color WinInk        = C(0x1A, 0x1D, 0x22);
    static readonly Color BoardBack     = C(0x13, 0x15, 0x1A);

    // =====================================================================
    //  ① 计时窗
    // =====================================================================
    static double DrawTimerWindowSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "计时窗 · 对照稿（InkClass 1:1 复刻 ＋ 三模式）", 40, y, 28, TitleBrush, bold: true);
        y += 42;
        Text(c, "窗 1100×700 居中置顶，卡片内缩 60；底色 #F0F3F9、1px 描边 #0066BF、圆角 10 —— 数值全部照抄 InkClass CountdownTimerWindow。",
             40, y, 13.5, BodyBrush);
        y += 21;
        Text(c, "新增（本次拍板）：顶部三模式页签；内部计时换单调时钟（外观不变）。浅色面板在黑板上天然醒目，不靠半透明。",
             40, y, 13.5, NoteBrush);
        y += 32;

        y = Section(c, y, "① 运行态 —— 倒计时剩余 02:35（三种模式共用这个窗，页签切换）");
        double wx = 60, wy = y + 16, ww = 1100, wh = 700;
        WinBoard(c, wx - 20, wy - 20, ww + 40, wh + 40);

        double cx0 = wx + 60, cy0 = wy + 60, cw = ww - 120, chh = wh - 120;
        WinCard(c, cx0, cy0, cw, chh);
        double ccx = cx0 + cw / 2;

        // 三模式页签（新增）
        double tabW = 96, tabGap = 12, tabTop = cy0 + 20;
        double tabsTotal = tabW * 3 + tabGap * 2, tabX = ccx - tabsTotal / 2;
        WinTab(c, tabX + tabW / 2, tabTop, tabW, "倒计时", true);
        WinTab(c, tabX + tabW + tabGap + tabW / 2, tabTop, tabW, "正计时", false);
        WinTab(c, tabX + 2 * (tabW + tabGap) + tabW / 2, tabTop, tabW, "秒表", false);

        // 环形进度 + 压在上面的大字 + 到点药丸
        double ringCy = cy0 + 280, ringR = 178;
        WinRing(c, ccx, ringCy, ringR, 14, 0.52);
        WinDigits(c, "00:02:35", ccx, ringCy - 52, 66, Brushes.Black);
        WinClockPill(c, ccx, cy0 + 470, "预计 12:30 到点");

        // 底部：开始/暂停、重置、最小化、全屏、关闭
        double btnCy = cy0 + 520;
        WinCircleBtn(c, ccx - 46, btnCy, 60, WinAccent, "pause", Colors.White);
        WinCircleBtn(c, ccx + 46, btnCy, 60, WinLightBtn, "arrowSync", WinInk);
        WinCircleBtn(c, cx0 + cw - 152, btnCy, 44, WinLightBtn, "chevronDown", WinInk);
        WinCircleBtn(c, cx0 + cw - 98, btnCy, 44, WinLightBtn, "maximize", WinInk);
        WinCircleBtn(c, cx0 + cw - 44, btnCy, 44, WinDanger, "dismiss", Colors.White);

        // 右侧照抄清单
        double ax = 1216, any = wy + 4;
        Text(c, "照抄清单", ax, any, 16, TitleBrush, bold: true);
        any += 30;
        string[] notes =
        {
            "尺寸 1100×700，居中置顶",
            "卡片内缩 60 → 面板 980×580",
            "底 #F0F3F9 / 边 #0066BF 1px / 圆角 10",
            "环：剩余比例；大字压在环上",
            "运行黑 #000000；待机 #5B5D5F",
            "药丸 #E8EAF0：预计结束时刻",
            "开始 #0066BF；重置 #FBFBFD",
            "最小化 / 全屏白钮；关闭 #E32A34",
            "页签（新增）：蓝底 = 当前模式",
            "到点：提示音只响一次",
        };
        foreach (var s in notes)
        {
            Text(c, "• " + s, ax, any, 12.5, BodyBrush);
            any += 22;
        }

        // ② 状态对照
        y = wy + wh + 56;
        y = Section(c, y, "② 状态对照（改时长 / 最小化 / 到点超时）");

        // 改时长态
        double i1x = 40, i1y = y + 6, i1w = 560, i1h = 420;
        Text(c, "改时长态（1:1 片段）", i1x, i1y - 24, 13, NoteBrush, bold: true);
        WinCard(c, i1x, i1y, i1w, i1h);
        double i1cx = i1x + i1w / 2;
        WinAdjust(c, i1cx - 78, i1y + 80, i1y + 322);
        WinAdjust(c, i1cx, i1y + 80, i1y + 322);
        WinAdjust(c, i1cx + 78, i1y + 80, i1y + 322);
        WinDigits(c, "00:05:00", i1cx, i1y + 172, 44, Brushes.Black);
        WinCircleBtn(c, i1cx + 150, i1y + 230, 36, WinAccent, "check", Colors.White);
        WinTextC(c, "点数字区进入；时/分/秒各自 ±5 / ±1；点 ✓ 返回", i1cx, i1y + i1h - 44, 12.5, new SolidColorBrush(WinMuted));

        // 最小化态
        double i2x = 640, i2y = y + 6, i2w = 400, i2h = 250;
        Text(c, "最小化态（1:1）", i2x, i2y - 24, 13, NoteBrush, bold: true);
        WinCard(c, i2x, i2y, i2w, i2h);
        WinDigits(c, "00:02:35", i2x + i2w / 2, i2y + 90, 46, Brushes.Black);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0x88, 0x88, 0x88)), null,
            new Rect(i2x + i2w / 2 - 32, i2y + i2h - 30, 64, 14), 7, 7);
        WinTextC(c, "400×250，只显示剩余时间；点大字恢复", i2x + i2w / 2, i2y + i2h + 10, 12.5, new SolidColorBrush(WinMuted));

        // 到点超时态
        double i3x = 1080, i3y = y + 6, i3w = 400, i3h = 250;
        Text(c, "到点超时态（我们口径）", i3x, i3y - 24, 13, NoteBrush, bold: true);
        WinCard(c, i3x, i3y, i3w, i3h);
        WinDangerPill(c, i3x + i3w / 2, i3y + 52, "时间到 · 响铃一次");
        WinDigits(c, "+00:27", i3x + i3w / 2, i3y + 92, 46, Brushes.Black);
        WinTextC(c, "停表后继续走、显示 +00:27（InkClass 原版是停在 00:00）", i3x + i3w / 2, i3y + i3h + 10, 12.5, new SolidColorBrush(WinMuted));

        // 色卡
        y = i1y + i1h + 56;
        Text(c, "配色原值（抄自 InkClass）", 40, y, 14, TitleBrush, bold: true);
        y += 26;
        WinSwatches(c, 40, y,
            ("#F0F3F9", WinPanel), ("#0066BF", WinAccent), ("#E32A34", WinDanger),
            ("#FBFBFD", WinLightBtn), ("#E8EAF0", WinPill), ("#5B5D5F", WinDigitIdle),
            ("#BFBFBF", WinCover), ("#F3F5F9", WinDisableBg), ("#9D9D9E", WinDisableInk));
        y += 62;
        Text(c, "说明：静态稿；动效、暂停补偿、拖动、全屏等行为按 InkClass 原逻辑复刻。出图 tools/design-sheet（1 单位 = 1 逻辑像素）。",
             40, y, 12, NoteBrush);
        return y + 24;
    }

    // =====================================================================
    //  ② 点名窗
    // =====================================================================
    static double DrawRollWindowSheet(DrawingContext c)
    {
        double y = 36;
        Text(c, "点名窗 · 对照稿（InkClass 1:1 复刻 ＋ 不重复池）", 40, y, 28, TitleBrush, bold: true);
        y += 42;
        Text(c, "窗 900×500 居中置顶；同款浅色面板；左 1.8 : 右 1 两栏。底色/描边/圆角与计时窗一致（照抄 RandWindow）。",
             40, y, 13.5, BodyBrush);
        y += 21;
        Text(c, "新增：抽过的不重复（去重池）。结果排布照抄：≤5 一列 / 6–10 两列 / >10 三列；随机一人＝快捷态自动关。",
             40, y, 13.5, NoteBrush);
        y += 32;

        y = Section(c, y, "① 结果态（抽 3 人；右栏可调人数 1..名单人数）");
        double wx = 60, wy = y + 16, ww = 900, wh = 500;
        WinBoard(c, wx - 20, wy - 20, ww + 40, wh + 40);
        WinCard(c, wx, wy, ww, wh);                       // RandWindow Margin=0：卡片=窗口
        double leftW = ww * 1.8 / 2.8;
        double lcx = wx + leftW / 2;
        double rcx = wx + leftW + (ww - leftW) / 2;

        // 左栏结果（3 人 → 一列，字号 70 设计值）
        WinTextC(c, "张伟", lcx, wy + 92, 54, Brushes.Black, true);
        WinTextC(c, "李娜", lcx, wy + 188, 54, Brushes.Black, true);
        WinTextC(c, "王强", lcx, wy + 284, 54, Brushes.Black, true);

        // 右栏：人数 [- 3 +]
        WinCircleBtn(c, rcx - 92, wy + 108, 64, WinLightBtn, "minus", WinInk);
        WinDigits(c, "3", rcx, wy + 80, 44, Brushes.Black);
        WinCircleBtn(c, rcx + 92, wy + 108, 64, WinLightBtn, "plus", WinInk);

        // 抽奖大按钮
        var dr = new Rect(rcx - 100, wy + 190, 200, 64);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0x1C, 0x20, 0x28, 0x24)), null,
            new Rect(dr.X, dr.Y + 3, dr.Width, dr.Height), 10, 10);
        c.DrawRoundedRectangle(new SolidColorBrush(WinAccent), null, dr, 10, 10);
        WinGlyph(c, "person", rcx - 44, wy + 222, 20, Brushes.White);
        WinTextC(c, "抽奖", rcx + 24, wy + 202, 28, Brushes.White, true);

        // 不重复池（新增）：左文右「重置」
        Text(c, "不重复池：12 / 42（抽完自动重置）", wx + leftW + 20, wy + 272, 12, new SolidColorBrush(WinMuted));
        var ftReset = Fmt("重置", 12, new SolidColorBrush(WinAccent), true);
        ftReset.MaxTextWidth = wx + ww - 20 - (wx + leftW + 20);
        ftReset.TextAlignment = TextAlignment.Right;
        c.DrawText(ftReset, new Point(wx + leftW + 20, wy + 272));

        // 底部：名单药丸 + 关闭
        double pillX = wx + leftW + 12, pillY = wy + wh - 60, pillH = 40;
        var ftName = Fmt("名单：42 人", 14, new SolidColorBrush(C(0x33, 0x38, 0x40)));
        double pillW = ftName.Width + 66;
        c.DrawRoundedRectangle(new SolidColorBrush(WinLightBtn), new Pen(new SolidColorBrush(WinLine), 1),
            new Rect(pillX, pillY, pillW, pillH), pillH / 2, pillH / 2);
        WinGlyph(c, "person", pillX + 26, pillY + pillH / 2, 16, new SolidColorBrush(C(0x33, 0x38, 0x40)));
        c.DrawText(ftName, new Point(pillX + 44, pillY + (pillH - ftName.Height) / 2));
        WinCircleBtn(c, wx + ww - 32, pillY + pillH / 2, 40, WinDanger, "dismiss", Colors.White);

        // 右侧：排布规则 + 照抄清单
        double ax = 1010;
        Text(c, "结果排布（照抄）", ax, wy + 4, 16, TitleBrush, bold: true);
        double dx = ax;
        foreach (var it in new[] { (1, "一列", "≤5 人"), (2, "两列", "6–10 人"), (3, "三列", ">10 人") })
        {
            WinColDiagram(c, dx, wy + 34, it.Item1);
            WinTextC(c, it.Item2 + " · " + it.Item3, dx + 60, wy + 126, 12, BodyBrush);
            dx += 168;
        }
        double any = wy + 168;
        string[] notes =
        {
            "抽取：先滚 5 次（每 150ms）再出结果",
            "单次抽取内不重复（照抄）",
            "名单：程序目录 Names.txt（一行一名）",
            "替名：Replace.txt（原名-->新名，可选）",
            "药丸点开可编辑名单；有人名时显示人数",
            "「随机一人」＝自动抽 1 人 + 1.5s 自动关",
            "不重复池（新增）：抽中移出，抽完重置",
            "池行右侧「重置」可手动清空",
        };
        foreach (var s in notes)
        {
            Text(c, "• " + s, ax, any, 12.5, BodyBrush);
            any += 21;
        }

        // ② 快捷态 + 池示意
        y = wy + wh + 56;
        y = Section(c, y, "② 「随机一人」快捷态 ＋ 去重池（新增）");
        double qx = 40, qy = y + 6, qw = 560, qh = 260;
        Text(c, "快捷态（1:1）", qx, qy - 24, 13, NoteBrush, bold: true);
        WinCard(c, qx, qy, qw, qh);
        WinTextC(c, "张伟", qx + 150, qy + 84, 54, Brushes.Black, true);
        Text(c, "1.5 秒后自动关", qx + 330, qy + 78, 14, BodyBrush);
        Text(c, "只抽 1 人，走不重复池", qx + 330, qy + 106, 12.5, new SolidColorBrush(WinMuted));
        Text(c, "点「抽奖」同样入池", qx + 330, qy + 130, 12.5, new SolidColorBrush(WinMuted));

        double px = 660, py = qy + 4;
        Text(c, "去重池：抽中移出，抽完自动重置", px, py, 13.5, BodyBrush, bold: true);
        for (int i = 0; i < 42; i++)
        {
            int row = i / 7, col = i % 7;
            var fill = i < 12 ? WinAccent : C(0xD9, 0xDE, 0xE7);
            c.DrawEllipse(new SolidColorBrush(fill), null, new Point(px + 12 + col * 30, py + 42 + row * 30), 9, 9);
        }
        Text(c, "蓝 = 已抽（12）；灰 = 池中（30）；窗口底部池行右侧可「重置」", px, py + 232, 12.5, new SolidColorBrush(WinMuted));

        // 色卡
        y = qy + qh + 56;
        Text(c, "配色与计时窗同源（抄自 InkClass）", 40, y, 14, TitleBrush, bold: true);
        y += 26;
        WinSwatches(c, 40, y,
            ("#F0F3F9", WinPanel), ("#0066BF", WinAccent), ("#E32A34", WinDanger),
            ("#FBFBFD", WinLightBtn), ("#E8EAF0", WinPill), ("#5B5D5F", WinDigitIdle),
            ("#BFBFBF", WinCover), ("#F3F5F9", WinDisableBg), ("#9D9D9E", WinDisableInk));
        y += 62;
        Text(c, "说明：静态稿；滚动手感、自动关时长、单次不重复等行为按 InkClass 原逻辑复刻；去重池为本次新增。",
             40, y, 12, NoteBrush);
        return y + 24;
    }

    // =====================================================================
    //  Win* 绘制小工具
    // =====================================================================

    /// <summary>模拟黑板/课件底：证明浅色窗的对比度。</summary>
    static void WinBoard(DrawingContext c, double x, double y, double w, double h)
    {
        c.DrawRoundedRectangle(new SolidColorBrush(BoardBack), null, new Rect(x, y, w, h), 14, 14);
        var l1 = new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x12));
        var l2 = new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x0B));
        c.DrawRoundedRectangle(l1, null, new Rect(x + 48, y + 40, w * 0.40, 20), 7, 7);
        c.DrawRoundedRectangle(l2, null, new Rect(x + 48, y + 76, w * 0.56, 13), 6, 6);
        c.DrawRoundedRectangle(l2, null, new Rect(x + 48, y + 102, w * 0.46, 13), 6, 6);
        c.DrawRoundedRectangle(l1, null, new Rect(x + 48, y + 142, w * 0.30, 16), 6, 6);
        c.DrawRoundedRectangle(l2, null, new Rect(x + 48, y + 176, w * 0.52, 13), 6, 6);
    }

    static void WinCard(DrawingContext c, double x, double y, double w, double h)
    {
        c.DrawRoundedRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x38)), null,
            new Rect(x + 3, y + 7, w, h), 12, 12);
        c.DrawRoundedRectangle(new SolidColorBrush(WinPanel),
            new Pen(new SolidColorBrush(WinAccent), 1), new Rect(x, y, w, h), 10, 10);
    }

    /// <summary>环形进度：frac = 剩余比例（照 InkClass 的 CurrentValue 语义）。</summary>
    static void WinRing(DrawingContext c, double cx, double cy, double r, double th, double frac)
    {
        c.DrawEllipse(null, new Pen(new SolidColorBrush(C(0xDF, 0xE4, 0xEC)), th), new Point(cx, cy), r, r);
        if (frac <= 0.001) return;
        double a1 = 360.0 * Math.Min(frac, 1.0);
        if (a1 >= 359.9)
        {
            c.DrawEllipse(null, new Pen(new SolidColorBrush(WinAccent), th), new Point(cx, cy), r, r);
            return;
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            Point P(double deg)
            {
                double rad = (deg - 90.0) * Math.PI / 180.0;
                return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
            }
            g.BeginFigure(P(0), false, false);
            g.ArcTo(P(a1), new Size(r, r), 0, a1 > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        var pen = new Pen(new SolidColorBrush(WinAccent), th)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawGeometry(null, pen, geo);
    }

    static void WinDigits(DrawingContext c, string s, double cx, double top, double size, Brush b, bool bold = true)
    {
        var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                              bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(s, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, tf, size, b, 1.0)
        { MaxTextWidth = 1400, TextAlignment = TextAlignment.Center };
        c.DrawText(ft, new Point(cx - 700, top));
    }

    static void WinTextC(DrawingContext c, string s, double cx, double top, double size, Brush b, bool bold = false)
    {
        var ft = Fmt(s, size, b, bold);
        ft.MaxTextWidth = 1400;
        ft.TextAlignment = TextAlignment.Center;
        c.DrawText(ft, new Point(cx - 700, top));
    }

    static void WinTab(DrawingContext c, double cx, double top, double w, string text, bool active)
    {
        double h = 30;
        var r = new Rect(cx - w / 2, top, w, h);
        if (active)
            c.DrawRoundedRectangle(new SolidColorBrush(WinAccent), null, r, h / 2, h / 2);
        else
            c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x9C)),
                new Pen(new SolidColorBrush(C(0xC9, 0xCF, 0xDB)), 1), r, h / 2, h / 2);
        var ft = Fmt(text, 14, active ? Brushes.White : new SolidColorBrush(WinDigitIdle), active);
        c.DrawText(ft, new Point(cx - ft.Width / 2, top + (h - ft.Height) / 2));
    }

    static void WinCircleBtn(DrawingContext c, double cx, double cy, double d, Color fill, string glyph, Color ink)
    {
        c.DrawEllipse(new SolidColorBrush(C(0x1C, 0x20, 0x28, 0x24)), null, new Point(cx, cy + 1.6), d / 2, d / 2);
        c.DrawEllipse(new SolidColorBrush(fill), null, new Point(cx, cy), d / 2, d / 2);
        WinGlyph(c, glyph, cx, cy, d * 0.52, new SolidColorBrush(ink));
    }

    static void WinGlyph(DrawingContext c, string glyph, double cx, double cy, double s, Brush b)
    {
        switch (glyph)
        {
            case "play":
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(cx - s * 0.32, cy - s * 0.46), true, true);
                    g.LineTo(new Point(cx - s * 0.32, cy + s * 0.46), true, false);
                    g.LineTo(new Point(cx + s * 0.50, cy), true, false);
                }
                geo.Freeze();
                c.DrawGeometry(b, null, geo);
                break;
            }
            case "pause":
            {
                double w = s * 0.24, h = s * 0.92, gap = s * 0.26, rr = w / 2;
                c.DrawRoundedRectangle(b, null, new Rect(cx - gap - w, cy - h / 2, w, h), rr, rr);
                c.DrawRoundedRectangle(b, null, new Rect(cx + gap, cy - h / 2, w, h), rr, rr);
                break;
            }
            case "plus":
            case "minus":
            {
                var pen = new Pen(b, Math.Max(2, s * 0.16)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                c.DrawLine(pen, new Point(cx - s * 0.4, cy), new Point(cx + s * 0.4, cy));
                if (glyph == "plus") c.DrawLine(pen, new Point(cx, cy - s * 0.4), new Point(cx, cy + s * 0.4));
                break;
            }
            case "clock":
            {
                var pen = new Pen(b, Math.Max(1.4, s * 0.12));
                c.DrawEllipse(null, pen, new Point(cx, cy), s * 0.46, s * 0.46);
                c.DrawLine(pen, new Point(cx, cy - s * 0.22), new Point(cx, cy + s * 0.04));
                c.DrawLine(pen, new Point(cx, cy + s * 0.04), new Point(cx + s * 0.20, cy + s * 0.14));
                break;
            }
            case "person":
            {
                c.DrawEllipse(b, null, new Point(cx, cy - s * 0.22), s * 0.22, s * 0.22);
                var pen = new Pen(b, Math.Max(1.6, s * 0.20)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(cx - s * 0.42, cy + s * 0.40), false, false);
                    g.QuadraticBezierTo(new Point(cx, cy - s * 0.02), new Point(cx + s * 0.42, cy + s * 0.40), true, false);
                }
                geo.Freeze();
                c.DrawGeometry(null, pen, geo);
                break;
            }
            default:
                Icon(c, glyph, cx, cy, s, b);   // arrowSync / chevronDown / maximize / dismiss / check
                break;
        }
    }

    /// <summary>± 按钮组：上方 +5/+1、下方 -1/-5（照 InkClass 的时/分/秒调整）。</summary>
    static void WinAdjust(DrawingContext c, double cx, double yTop, double yBottom)
    {
        WinMiniBtn(c, cx, yTop, "+5");
        WinMiniBtn(c, cx, yTop + 30, "+1");
        WinMiniBtn(c, cx, yBottom - 30, "-1");
        WinMiniBtn(c, cx, yBottom, "-5");
    }

    static void WinMiniBtn(DrawingContext c, double cx, double cy, string s)
    {
        double w = 46, h = 26;
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0xE6)),
            new Pen(new SolidColorBrush(WinLine), 1), new Rect(cx - w / 2, cy - h / 2, w, h), 6, 6);
        var ft = Fmt(s, 12.5, new SolidColorBrush(C(0x33, 0x38, 0x40)), true);
        c.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    static void WinClockPill(DrawingContext c, double cx, double cy, string text)
    {
        var ft = Fmt(text, 14, new SolidColorBrush(C(0x33, 0x38, 0x40)));
        double w = ft.Width + 56, h = 30;
        c.DrawRoundedRectangle(new SolidColorBrush(WinPill), null, new Rect(cx - w / 2, cy - h / 2, w, h), h / 2, h / 2);
        WinGlyph(c, "clock", cx - w / 2 + 20, cy, 16, new SolidColorBrush(C(0x33, 0x38, 0x40)));
        c.DrawText(ft, new Point(cx - w / 2 + 34, cy - ft.Height / 2));
    }

    static void WinDangerPill(DrawingContext c, double cx, double cy, string text)
    {
        var ft = Fmt(text, 13, Brushes.White, true);
        double w = ft.Width + 30, h = 26;
        c.DrawRoundedRectangle(new SolidColorBrush(WinDanger), null, new Rect(cx - w / 2, cy - h / 2, w, h), h / 2, h / 2);
        c.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    static void WinColDiagram(DrawingContext c, double x, double y, int cols)
    {
        double w = 120, h = 84;
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFA, 0xFB, 0xFD)),
            new Pen(new SolidColorBrush(WinLine), 1), new Rect(x, y, w, h), 8, 8);
        double inner = (w - 24) / cols;
        for (int i = 0; i < cols; i++)
        {
            double colX = x + 12 + inner * i + inner / 2;
            for (int k = 0; k < 3; k++)
                c.DrawRoundedRectangle(new SolidColorBrush(C(0x3A, 0x3F, 0x48, 0xDD)), null,
                    new Rect(colX - inner * 0.32, y + 14 + k * 22, inner * 0.64, 9), 4.5, 4.5);
        }
    }

    static void WinSwatches(DrawingContext c, double x, double y, params (string label, Color col)[] items)
    {
        double cx = x;
        foreach (var (label, col) in items)
        {
            c.DrawRoundedRectangle(new SolidColorBrush(col),
                new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x26)), 1), new Rect(cx, y, 22, 22), 4, 4);
            Text(c, label, cx - 6, y + 28, 10.5, NoteBrush);
            cx += 104;
        }
    }
}
