
using System;
using System.Collections.Generic;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>计时窗口上点到了哪一块（2026-10-05 重设计：独立窗口 + 本项目自己的版式）。</summary>
internal enum TimerZone
{
    None = 0,
    Body,        // 空白：拖动窗口
    Value,       // 数字/环：倒计时待机=进"改时长"；跑/暂停=暂停/继续；到点=重开
    Start,       // 开始 / 暂停 / 继续 / 到点重开
    Reset,       // 重置回待机
    Minimize,    // 收成 320×150 小时钟（点数字还原）
    Fullscreen,  // 全屏
    Close,       // 停止并关窗（InkClass 同款语义）
    Tab0, Tab1, Tab2,
    Ok,          // 改时长态的 ✓
    Restore,     // 最小化态点数字：还原
}

/// <summary>点名窗口上点到了哪一块。</summary>
internal enum RollZone
{
    None = 0,
    Body,
    CountMinus, CountPlus,
    NumMinus, NumPlus,     // 无名单时的学号范围 1–N
    Draw,        // 抽奖
    PoolReset,   // 不重复池「重置」

    Reload,      // 重读名单
    Close,
}

/// <summary>
/// 课堂窗的共同缩放：设计稿（逻辑像素）→ 物理像素。
/// 屏幕放不下时按 0.94 个工作区等比缩小；放得下就 1:1（再乘 DPI）。
/// </summary>
internal static class WinScale
{
    public static float Unit(in RectF screen, float dpi, float dw, float dh)
    {
        float availW = (screen.MaxX - screen.MinX) * 0.94f;
        float availH = (screen.MaxY - screen.MinY) * 0.94f;
        float needW = dw * dpi, needH = dh * dpi;
        float k = 1f;
        if (needW > availW) k = MathF.Min(k, availW / needW);
        if (needH > availH) k = MathF.Min(k, availH / needH);
        return k * dpi;
    }

    /// <summary>按中心摆窗并夹进工作区（cx/cy 为物理坐标；NaN = 居中）。</summary>
    public static RectF PlaceCentered(in RectF screen, float w, float h, float cx, float cy, float dpi)
    {
        float left = float.IsNaN(cx) ? (screen.MinX + screen.MaxX - w) * 0.5f : cx - w * 0.5f;
        float top = float.IsNaN(cy) ? (screen.MinY + screen.MaxY - h) * 0.5f : cy - h * 0.5f;
        float m = 8f * dpi;
        left = Math.Clamp(left, screen.MinX + m, MathF.Max(screen.MinX + m, screen.MaxX - m - w));
        top = Math.Clamp(top, screen.MinY + m, MathF.Max(screen.MinY + m, screen.MaxY - m - h));
        return new RectF { MinX = left, MinY = top, MaxX = left + w, MaxY = top + h };
    }
}

/// <summary>
/// 计时窗几何（**2026-10-05 重设计**，理由见下面那段"为什么重画"）。
///
/// 设计稿 1100×700、卡片内缩 60（卡片 980×580）；改时长态、最小化 320×150、全屏。
/// 全部乘 u（= DpiScale × 适配缩放）得到物理像素。
///
/// ## 为什么重画（这一段是这次改动的全部理由，别删）
///
/// 原来的版式是**照着 InkClass 的 CountdownTimerWindow 1:1 复刻**的：顶部三个
/// 药丸页签居中、下面一个大圆环、底部一排**圆形**按钮（蓝色开始 / 白色重置 /
/// 白色收起 / 白色全屏 / **红色圆形关闭**）、浅蓝面板 + 蓝描边。功能是对的，
/// 但那套版式本身是别人的识别特征——别人一眼就认得出"这是照着谁做的"。
///
/// 这一版换成**本项目自己的语言**（和主工具带 /「更多」面板同一套）：
///   · 卡片：InkUi.Tokens 那套面板底 + 1px 描边 + 18 圆角 + 八层投影，**并且支持深色**；
///   · 模式选择：左边**贴边**的分段控件（凹槽 + 选中药丸），和工具带上
///     「整笔 ⇄ 面积」那一档同源，不再是居中的三个独立药丸；
///   · 窗口钮（收起 / 全屏 / 关闭）搬到**右上角**，方块幽灵钮，不再是底部一排圆钮；
///   · 主按钮**带文字**（开始 / 暂停 / 重置）——投影上远处要能读出来，光靠图标不够；
///   · 到点时数字与环一起变红（原来只有一个红色小药丸，隔远看不见）。
///
/// ⚠ **对外的方法名一个都没改**（`CardRect` / `TabRect` / `BtnRect` / `ValueRect` /
/// `StepRect` / `OkRect` / `ZoneAt` …）：自检与出图都按名字调它们。改版式不该顺手
/// 改 API——那会让"界面变了"和"自检失灵"两件事混在一起分不清。
/// </summary>
internal static class TimerWin
{
    public const float DW = 1100f, DH = 700f, Inset = 60f;
    public const float MinDW = 320f, MinDH = 150f;
    private const float CardDW = DW - Inset * 2f;   // 980
    private const float CardDH = DH - Inset * 2f;   // 580

    // ---- 版式常量（相对卡片左上角；数字后面那行是"凭什么这么定"）--------------
    /// <summary>卡片内边距 32：和主工具带那套"4 的倍数"节奏一致。</summary>
    public const float Pad = 32f;
    /// <summary>顶栏（分段 + 窗口钮）那一行。</summary>
    private const float TopY = 28f, TopH = 40f;
    private const float SegW = 108f, SegH = 30f, SegGap = 4f;
    /// <summary>窗口钮 34 方块、间隔 10。</summary>
    private const float WinD = 34f, WinGap = 10f;
    private const float RingY = 300f, RingR0 = 168f, RingTh0 = 10f;
    /// <summary>环下面那行说明（预计几点到 / 时间到）。**故意不再套药丸**：
    /// 药丸是 InkClass 的签名形状，去掉以后这一行才像本项目的东西。</summary>
    private const float CapY = 484f, CapH = 30f, CapW = 360f;
    private const float BarY = 506f, BarH = 52f;
    private const float StartW = 176f, ResetW = 108f, BarGap = 16f;
    private const float StepW = 100f, StepH = 40f, StepGapX = 12f;
    private const float StepColW = 212f, StepColGap = 32f;
    private const float OkW = 148f, OkH = 48f;

    public static float Unit(in RectF screen, float dpi) => WinScale.Unit(screen, dpi, DW, DH);

    public static RectF WindowRect(in RectF screen, float dpi, float cx, float cy, bool minimized, bool fullscreen)
    {
        if (fullscreen) return screen;
        float u = Unit(screen, dpi);
        float w = (minimized ? MinDW : DW) * u;
        float h = (minimized ? MinDH : DH) * u;
        return WinScale.PlaceCentered(screen, w, h, cx, cy, dpi);
    }

    /// <summary>fullscreen 时卡片 = 整屏（不再内缩，真全屏）；最小化时卡片 = 小窗。</summary>
    public static RectF CardRect(in RectF win, float u, bool minimized, bool fullscreen = false)
    {
        if (minimized || fullscreen) return win;
        float ins = Inset * u;
        return new RectF { MinX = win.MinX + ins, MinY = win.MinY + ins, MaxX = win.MaxX - ins, MaxY = win.MaxY - ins };
    }

    /// <summary>全屏时按卡片大小把布局整体放大（普通态恒为 1）。</summary>
    public static float Layout(in RectF card, float u)
    {
        float scale = MathF.Min((card.MaxX - card.MinX) / (CardDW * u), (card.MaxY - card.MinY) / (CardDH * u));
        return u * MathF.Max(1f, scale);
    }

    public static float CX(in RectF card) => (card.MinX + card.MaxX) * 0.5f;

    private static RectF Circle(float cx, float cy, float d) => new()
    {
        MinX = cx - d * 0.5f, MinY = cy - d * 0.5f, MaxX = cx + d * 0.5f, MaxY = cy + d * 0.5f,
    };

    // ---- 顶栏 ---------------------------------------------------------------

    /// <summary>三段模式控件：贴左边，不再居中（居中那三个药丸是原来最像"照搬"的地方）。</summary>
    public static RectF TabRect(in RectF card, float u, int i)
    {
        float x = card.MinX + (Pad + i * (SegW + SegGap)) * u;
        float y = card.MinY + (TopY + (TopH - SegH) * 0.5f) * u;
        return new RectF { MinX = x, MinY = y, MaxX = x + SegW * u, MaxY = y + SegH * u };
    }

    /// <summary>分段控件外面那道**凹槽**（三段整体内缩 4）——和工具带上那档同一套画法。</summary>
    public static RectF SegTroughRect(in RectF card, float u)
    {
        var a = TabRect(card, u, 0);
        var b = TabRect(card, u, 2);
        return new RectF { MinX = a.MinX - 4f * u, MinY = card.MinY + TopY * u,
                           MaxX = b.MaxX + 4f * u, MaxY = card.MinY + (TopY + TopH) * u };
    }

    // ---- 主区（环 + 数字 + 说明）---------------------------------------------

    public static float RingCY(in RectF card, float u) => card.MinY + RingY * u;
    public static float RingR(float u) => RingR0 * u;
    public static float RingTh(float u) => RingTh0 * u;

    /// <summary>环+大字所在区域（“点数字/环”热区）。</summary>
    public static RectF ValueRect(in RectF card, float u)
    {
        float c = CX(card), cy = RingCY(card, u), r = RingR(u) + 26f * u;
        return new RectF { MinX = c - r, MinY = cy - r, MaxX = c + r, MaxY = cy + r };
    }

    /// <summary>环下面那行说明（预计到点时刻 / 时间到）。名字沿用旧的 <c>PillRect</c>，
    /// 但**画法已经改成纯文字**，不再有药丸底。</summary>
    public static RectF PillRect(in RectF card, float u)
        => new() { MinX = CX(card) - CapW * u * 0.5f, MinY = card.MinY + CapY * u,
                   MaxX = CX(card) + CapW * u * 0.5f, MaxY = card.MinY + (CapY + CapH) * u };

    // ---- 底部主按钮（带文字）-------------------------------------------------

    public static RectF BtnRect(in RectF card, float u, TimerZone which)
    {
        switch (which)
        {
            case TimerZone.Start:
            {
                float total = (StartW + BarGap + ResetW) * u;
                float x = CX(card) - total * 0.5f;
                float y = card.MinY + BarY * u;
                return new RectF { MinX = x, MinY = y, MaxX = x + StartW * u, MaxY = y + BarH * u };
            }
            case TimerZone.Reset:
            {
                float x = CX(card) + BarGap * u + (StartW - ResetW) * u * 0.5f;
                float y = card.MinY + BarY * u;
                return new RectF { MinX = x, MinY = y, MaxX = x + ResetW * u, MaxY = y + BarH * u };
            }
            case TimerZone.Minimize:
            case TimerZone.Fullscreen:
            case TimerZone.Close:
            {
                // 右上角三个方块钮，从右往左排：关闭 / 全屏 / 收起。
                float d = WinD * u;
                float y = card.MinY + (TopY + (TopH - WinD) * 0.5f) * u;
                float cx = card.MaxX - Pad * u - d * 0.5f;
                int back = which == TimerZone.Close ? 0 : which == TimerZone.Fullscreen ? 1 : 2;
                cx -= back * (WinD + WinGap) * u;
                return new RectF { MinX = cx - d * 0.5f, MinY = y, MaxX = cx + d * 0.5f, MaxY = y + d };
            }
            default: return new RectF();
        }
    }

    // ---- 改时长态 -----------------------------------------------------------

    /// <summary>改时长态的大数字（`HH:MM:SS`）。</summary>
    public static RectF EditValueRect(in RectF card, float u)
        => new() { MinX = CX(card) - 320f * u, MinY = card.MinY + 150f * u,
                   MaxX = CX(card) + 320f * u, MaxY = card.MinY + 270f * u };

    /// <summary>单位标题（时 / 分 / 秒）那一行。</summary>
    public static RectF StepLabelRect(in RectF card, float u, int unit)
    {
        float colW = StepColW * u;
        float total = (colW * 3f + StepColGap * 2f * u);
        float x = CX(card) - total * 0.5f + unit * (colW + StepColGap * u);
        return new RectF { MinX = x, MinY = card.MinY + 292f * u, MaxX = x + colW, MaxY = card.MinY + 318f * u };
    }

    /// <summary>
    /// ± 步进钮：unit 0 时 / 1 分 / 2 秒；pair 0=加（+5 / +1）1=减（−5 / −1）；row 0/1。
    ///
    /// 版式是**三列 2×2 的小格**（每列一个单位，标题在上面）——原来是把 18 个钮
    /// 撒在数字上下两片，投影上根本分不清哪个是哪个单位的。
    ///
    /// ⚠ `row` 是**列**（左右），`pair` 是**行**（上下）。这两个别搞反：搞反了排出来
    /// 是斜着的一串阶梯，看着像坏掉而不是像设计（第一版就踩了这个，看图才看出来）。
    /// </summary>
    public static RectF StepRect(in RectF card, float u, int unit, int pair, int row)
    {
        var lab = StepLabelRect(card, u, unit);
        float w = StepW * u, h = StepH * u;
        float x = lab.MinX + row * (StepW + StepGapX) * u;
        float y = card.MinY + (pair == 0 ? 330f : 378f) * u;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>改时长态的「确定」——整宽药丸，不是原来那个孤零零的小圆 ✓。</summary>
    public static RectF OkRect(in RectF card, float u)
        => new() { MinX = CX(card) - OkW * u * 0.5f, MinY = card.MinY + 500f * u,
                   MaxX = CX(card) + OkW * u * 0.5f, MaxY = card.MinY + (500f + OkH) * u };

    // ---- 最小化态 ------------------------------------------------------------

    public static RectF MinimalDigitsRect(in RectF card, float u)
        => new() { MinX = card.MinX, MinY = card.MinY, MaxX = card.MaxX, MaxY = card.MaxY - 40f * u };

    public static RectF MinimalPillRect(in RectF card, float u)
    {
        float w = 64f * u, h = 14f * u, cx = CX(card);
        return new RectF { MinX = cx - w * 0.5f, MinY = card.MaxY - 28f * u,
                           MaxX = cx + w * 0.5f, MaxY = card.MaxY - 14f * u };
    }

    // ---- 命中 ---------------------------------------------------------------

    public static TimerZone ZoneAt(in RectF card, float u, float x, float y, bool edit, bool countdown)
    {
        if (!card.Contains(x, y)) return TimerZone.None;
        for (int i = 0; i < 3; i++)
            if (TabRect(card, u, i).Contains(x, y)) return TimerZone.Tab0 + i;

        if (edit && countdown)
        {
            // 改时长态：**底排那组按钮这时不画也不响应**（否则「确定」和「开始」抢同一块，
            // 会出现"点确定却把计时开了"）。顶栏的三个窗口钮照常可用。
            if (OkRect(card, u).Contains(x, y)) return TimerZone.Ok;
            if (EditValueRect(card, u).Contains(x, y)) return TimerZone.Value;
            if (BtnRect(card, u, TimerZone.Close).Contains(x, y)) return TimerZone.Close;
            if (BtnRect(card, u, TimerZone.Minimize).Contains(x, y)) return TimerZone.Minimize;
            if (BtnRect(card, u, TimerZone.Fullscreen).Contains(x, y)) return TimerZone.Fullscreen;
            return TimerZone.Body;
        }

        if (ValueRect(card, u).Contains(x, y)) return TimerZone.Value;
        if (BtnRect(card, u, TimerZone.Start).Contains(x, y)) return TimerZone.Start;
        if (BtnRect(card, u, TimerZone.Reset).Contains(x, y)) return TimerZone.Reset;
        if (BtnRect(card, u, TimerZone.Minimize).Contains(x, y)) return TimerZone.Minimize;
        if (BtnRect(card, u, TimerZone.Fullscreen).Contains(x, y)) return TimerZone.Fullscreen;
        if (BtnRect(card, u, TimerZone.Close).Contains(x, y)) return TimerZone.Close;
        return TimerZone.Body;
    }

    // ---- 文本格式（纯函数）---------------------------------------------------

    /// <summary>毫秒 → 文本。倒计时/正计时 `MM:SS`（超一小时 `H:MM:SS`），秒表多 `.cc`。</summary>
    public static string Format(float ms, TimerMode mode)
    {
        long t = (long)MathF.Max(0f, ms);
        long cs = (t / 10) % 100;
        long s = t / 1000;
        long m = s / 60; s %= 60;
        long h = m / 60; m %= 60;
        string main = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        return mode == TimerMode.Stopwatch ? $"{main}.{cs:00}" : main;
    }

    /// <summary>到点后的超时文本（`+00:27`）。</summary>
    public static string FormatOvertime(float ms)
    {
        long t = (long)MathF.Max(0f, ms);
        long s = t / 1000;
        long m = s / 60; s %= 60;
        long h = m / 60; m %= 60;
        return h > 0 ? $"+{h}:{m:00}:{s:00}" : $"+{m:00}:{s:00}";
    }

    public static string ModeName(TimerMode mode) => mode switch
    {
        TimerMode.Countdown => "倒计时",
        TimerMode.CountUp => "正计时",
        _ => "秒表",
    };

    /// <summary>单位标题（时 / 分 / 秒）。</summary>
    public static string UnitName(int unit) => unit switch { 0 => "时", 1 => "分", _ => "秒" };
}

/// <summary>
/// 点名窗几何（**2026-10-05 重设计**，理由同 <see cref="TimerWin"/> 的说明）。
///
/// 设计稿 900×500。原来的版式是照 InkClass 的 RandWindow 1:1 复刻：左边一列大字
/// 名字、右边一竖排控件（− 5 ＋ 在最上、抽奖按钮在中下）。这一版换成上下四段：
/// **结果区（名字卡片按名换行排布）／控制行（人数步进 + 抽奖）／信息行（不重复池）／
/// 底行（名单 + 学号范围）**，并且名字不再是一列纯文字，而是**一个个圆角名牌**——
/// 投影上最后要看的是"抽到了谁"，名牌比一列文字醒目得多。
/// </summary>
internal static class RollWin
{
    public const float DW = 900f, DH = 500f;

    /// <summary>内边距 32，与计时窗同一套节奏（两个窗看起来才像一家人）。</summary>
    public const float Pad = 32f;

    private const float TopY = 24f;                       // 顶栏：只有右上角一个 ✕
    private const float CloseD = 34f;
    private const float AreaY = 92f, AreaH = 216f;        // 名牌区
    private const float CtrlY = 332f, CtrlH = 48f;        // 控制行
    private const float InfoY = 396f, InfoH = 26f;        // 不重复池那一行
    private const float BotH = 44f;                       // 底行（名单 / 学号范围）
    private const float StepD = 48f, CountW = 88f, DrawW = 220f;

    public static float Unit(in RectF screen, float dpi) => WinScale.Unit(screen, dpi, DW, DH);

    public static RectF WindowRect(in RectF screen, float dpi, float cx, float cy)
    {
        float u = Unit(screen, dpi);
        return WinScale.PlaceCentered(screen, DW * u, DH * u, cx, cy, dpi);
    }

    // ---- 名牌区 -------------------------------------------------------------

    public static RectF ResultsRect(in RectF card, float u) => new()
    {
        MinX = card.MinX + Pad * u, MinY = card.MinY + AreaY * u,
        MaxX = card.MaxX - Pad * u, MaxY = card.MinY + (AreaY + AreaH) * u,
    };

    /// <summary>
    /// 把一批名字排成**一行行圆角名牌**（左对齐、按名换行）。
    ///
    /// 字号是**试出来的**：从 26 一档一档往下试，取第一个"全部装得下"的档位。
    /// 为什么不用固定字号——名单 5 个人和 60 个人都得在**同一块**区域里排完，
    /// 固定字号要么空一大片、要么溢出（60 人 × 22px 至少要 8 行，216 高放不下）。
    ///
    /// 宽度按字符估：CJK 一个字约 1 em，拉丁/数字约 0.55 em。名字表来自
    /// <c>Names.txt</c>，绝大多数是中文，这个精度足够；宁可略估宽一点，
    /// 也不要把名牌挤出区域。
    /// </summary>
    public static List<(RectF R, string Name)> Chips(in RectF area, IReadOnlyList<string> names, float u)
    {
        var outp = new List<(RectF, string)>();
        if (names == null || names.Count == 0) return outp;
        float gap = 10f * u;
        float aw = area.MaxX - area.MinX, ah = area.MaxY - area.MinY;

        foreach (float sizeLog in new[] { 26f, 22f, 19f, 16f, 14f, 12f, 11f })
        {
            float size = sizeLog * u;
            float ch = size + 16f * u;
            float x = area.MinX, y = area.MinY;
            int fit = 0;
            bool fits = true;
            foreach (var n in names)
            {
                float cw = ChipWidth(n, size) + 26f * u;
                if (x > area.MinX && x + cw > area.MaxX) { x = area.MinX; y += ch + gap; }
                if (y + ch > area.MaxY) { fits = false; break; }
                x += cw + gap;
                fit++;
            }
            if (!fits) continue;
            if (fit < names.Count) continue;      // 名字放不下（理论上不会，兜底）

            x = area.MinX; y = area.MinY;
            foreach (var n in names)
            {
                float cw = ChipWidth(n, size) + 26f * u;
                if (x > area.MinX && x + cw > area.MaxX) { x = area.MinX; y += ch + gap; }
                outp.Add((new RectF { MinX = x, MinY = y, MaxX = x + cw, MaxY = y + ch }, n));
                x += cw + gap;
            }
            // **整块名牌在结果区里垂直居中**：5 个人和 40 个人看起来都像"摆在那儿"，
            // 而不是"从顶上往下堆"——后者在只有几个名字时会显得头重脚轻。
            CenterVertically(outp, area);
            return outp;
        }

        // 极端兜底：全部按最小档平铺，超出区域的部分**不画**（宁可少几个，也不能压到控件上）。
        float s0 = 11f * u, ch0 = s0 + 16f * u;
        float x0 = area.MinX, y0 = area.MinY;
        foreach (var n in names)
        {
            float cw = ChipWidth(n, s0) + 26f * u;
            if (x0 > area.MinX && x0 + cw > area.MaxX) { x0 = area.MinX; y0 += ch0 + gap; }
            if (y0 + ch0 > area.MaxY) break;
            outp.Add((new RectF { MinX = x0, MinY = y0, MaxX = x0 + cw, MaxY = y0 + ch0 }, n));
            x0 += cw + gap;
        }
        CenterVertically(outp, area);
        return outp;
    }

    /// <summary>把整块名牌在结果区里垂直居中（原地改 List 里的矩形）。</summary>
    private static void CenterVertically(List<(RectF R, string Name)> chips, in RectF area)
    {
        if (chips.Count == 0) return;
        float top = chips[0].R.MinY, bottom = chips[0].R.MaxY;
        foreach (var c in chips)
        {
            top = MathF.Min(top, c.R.MinY);
            bottom = MathF.Max(bottom, c.R.MaxY);
        }
        float dy = (area.MinY + area.MaxY - (top + bottom)) * 0.5f;
        if (MathF.Abs(dy) < 0.5f) return;
        for (int i = 0; i < chips.Count; i++)
        {
            var r = chips[i].R;
            chips[i] = (new RectF { MinX = r.MinX, MinY = r.MinY + dy,
                                     MaxX = r.MaxX, MaxY = r.MaxY + dy }, chips[i].Name);
        }
    }

    /// <summary>名牌宽度（不含两侧留白）：CJK 约 1 em，其余约 0.55 em。</summary>
    public static float ChipWidth(string name, float size)
    {
        float w = 0f;
        foreach (char c in name) w += c >= 0x2E80 ? size : size * 0.55f;
        return w;
    }

    /// <summary>名牌里的字号（和 <see cref="Chips"/> 同一套试档逻辑，抽出来给绘制用）。</summary>
    public static float ChipFontSize(in RectF area, int n, float u)
    {
        float gap = 10f * u;
        float aw = area.MaxX - area.MinX, ah = area.MaxY - area.MinY;
        foreach (float s in new[] { 26f, 22f, 19f, 16f, 14f, 12f, 11f })
        {
            float size = s * u, ch = size + 16f * u;
            // 用最坏情况估：每个名字都按最长算，宁可字号小一档也不溢出。
            float cw = 4f * size + 26f * u;                    // 四字名字：最常见
            int perRow = Math.Max(1, (int)((aw + gap) / (cw + gap)));
            int rows = (n + perRow - 1) / perRow;
            if (rows * (ch + gap) - gap <= ah) return size;
        }
        return 11f * u;
    }

    // ---- 控制行（人数步进 + 抽奖）-------------------------------------------

    private static RectF At(in RectF card, float u, float xLog, float yLog, float wLog, float hLog)
        => new() { MinX = card.MinX + xLog * u, MinY = card.MinY + yLog * u,
                   MaxX = card.MinX + (xLog + wLog) * u, MaxY = card.MinY + (yLog + hLog) * u };

    public static RectF MinusRect(in RectF card, float u) => At(card, u, Pad, CtrlY, StepD, StepD);
    public static RectF CountRect(in RectF card, float u) => At(card, u, Pad + StepD + 12f, CtrlY, CountW, StepD);
    public static RectF PlusRect(in RectF card, float u)
        => At(card, u, Pad + StepD + 12f + CountW + 12f, CtrlY, StepD, StepD);

    public static RectF DrawRect(in RectF card, float u)
    {
        float w = DrawW * u;
        float x = card.MaxX - Pad * u - w;
        float y = card.MinY + (CtrlY + (CtrlH - StepD) * 0.5f) * u;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + StepD * u };
    }

    // ---- 信息行（不重复池）---------------------------------------------------

    public static RectF PoolTextRect(in RectF card, float u) => At(card, u, Pad, InfoY, 480f, InfoH);

    public static RectF PoolResetRect(in RectF card, float u)
    {
        float w = 96f * u;
        return new RectF { MinX = card.MaxX - Pad * u - w, MinY = card.MinY + InfoY * u,
                           MaxX = card.MaxX - Pad * u, MaxY = card.MinY + (InfoY + InfoH) * u };
    }

    // ---- 顶栏 / 底行 ---------------------------------------------------------

    public static RectF CloseRect(in RectF card, float u)
    {
        float d = CloseD * u;
        return new RectF { MinX = card.MaxX - Pad * u - d, MinY = card.MinY + TopY * u,
                           MaxX = card.MaxX - Pad * u, MaxY = card.MinY + TopY * u + d };
    }

    private static float BotY(in RectF card, float u) => card.MaxY - Pad * u - BotH * u;

    public static RectF NamesRect(in RectF card, float u) => At(card, u, Pad, DH - Pad - BotH, 244f, BotH);

    public static RectF ReloadRect(in RectF card, float u)
    {
        float d = 36f * u;
        var n = NamesRect(card, u);
        float x = n.MaxX + 12f * u;
        float y = n.MinY + (BotH * u - d) * 0.5f;
        return new RectF { MinX = x, MinY = y, MaxX = x + d, MaxY = y + d };
    }

    // ---- 无名单时的学号范围 ---------------------------------------------------

    private const float NumD = 34f;

    public static RectF NumPlusRect(in RectF card, float u)
    {
        float d = NumD * u;
        return new RectF { MinX = card.MaxX - Pad * u - d, MinY = BotY(card, u) + (BotH * u - d) * 0.5f,
                           MaxX = card.MaxX - Pad * u, MaxY = BotY(card, u) + (BotH * u - d) * 0.5f + d };
    }

    public static RectF NumMinusRect(in RectF card, float u)
    {
        var p = NumPlusRect(card, u);
        float w = p.MaxX - p.MinX;
        return new RectF { MinX = p.MinX - 12f * u - w, MinY = p.MinY, MaxX = p.MinX - 12f * u, MaxY = p.MaxY };
    }

    public static RectF NumTextRect(in RectF card, float u)
    {
        var m = NumMinusRect(card, u);
        float w = 130f * u;
        return new RectF { MinX = m.MinX - 14f * u - w, MinY = BotY(card, u),
                           MaxX = m.MinX - 14f * u, MaxY = BotY(card, u) + BotH * u };
    }

    public static RectF NumLabelRect(in RectF card, float u)
    {
        var t = NumTextRect(card, u);
        return new RectF { MinX = t.MinX, MinY = t.MinY - 26f * u, MaxX = t.MaxX, MaxY = t.MinY - 4f * u };
    }

    // ---- 命中 ---------------------------------------------------------------

    public static RollZone ZoneAt(in RectF card, float u, float x, float y)
    {
        if (!card.Contains(x, y)) return RollZone.None;
        if (CloseRect(card, u).Contains(x, y)) return RollZone.Close;
        if (MinusRect(card, u).Contains(x, y)) return RollZone.CountMinus;
        if (PlusRect(card, u).Contains(x, y)) return RollZone.CountPlus;
        if (NumMinusRect(card, u).Contains(x, y)) return RollZone.NumMinus;
        if (NumPlusRect(card, u).Contains(x, y)) return RollZone.NumPlus;
        if (DrawRect(card, u).Contains(x, y)) return RollZone.Draw;
        if (PoolResetRect(card, u).Contains(x, y)) return RollZone.PoolReset;
        if (ReloadRect(card, u).Contains(x, y)) return RollZone.Reload;
        return RollZone.Body;
    }
}

/// <summary>
/// 课堂工具（计时器 ＋ 点名）——2026-10-02 起按 InkClass 的用法改成
/// **点入口 → 弹出独立居中窗口**（浅色面板 + 蓝边 + 圆钮 + 环形进度 / 三列结果）。
/// 保留本项目的安全模型：捕获只在按下那一刻立起、拖动的唯一前提是 `*Capturing`、
/// 穿透时各配一块"接输入小窗"、✕ = 停止并关窗、计时用单调时钟。
/// </summary>
public partial class InkEngine
{
    // =====================================================================
    //  计时器
    // =====================================================================

    /// <summary>一个模式（倒计时/正计时/秒表）自己的运行状态——切页签只换显示，不打断。</summary>
    private struct TimerRun
    {
        public bool Active, Paused, Finished;
        public double Accum, Anchor;
        public float Value, Overtime;
        public long ShownUnit;
    }

    private readonly TimerRun[] _timerRuns = new TimerRun[3];

    private TimerMode _timerKind = TimerMode.Countdown;
    private bool _timerActive, _timerPaused, _timerFinished;
    private bool _timerCardOpen;         // 窗口开着
    private bool _timerEdit;             // 改时长态（仅倒计时）
    private bool _timerMinimized;        // 收成 320×150 小时钟
    private bool _timerFullscreen;
    private int _timerSetH = 0, _timerSetM = 5, _timerSetS = 0;   // 倒计时设定
    private bool _timerSoundOn = true;   // 到点提示音（默认开；窗口上不再放开关）
    private float _timerDurationMs = 300_000f;
    private float _timerValueMs;         // 倒计时=剩余（>=0）；正/秒=已过
    private float _timerOvertimeMs;      // 到点后的超时（显示 +00:27）
    private double _timerAccumMs;
    private double _timerAnchorMs;
    private long _timerShownUnit = long.MinValue;
    private long _timerNotifiedSec = long.MinValue;
    private double _timerPressAtMs = double.NegativeInfinity;
    private float _timerPressX, _timerPressY;
    private float _timerGrabDX, _timerGrabDY;
    private bool _timerDragging, _timerRestoreArmed;
    private bool _timerCapturing;
    private int _timerBeepCount;
    private float _timerCX = float.NaN, _timerCY = float.NaN;     // 窗口中心（物理）
    private bool _timerPosLoaded;
    private double _timerLastBodyClickMs = double.NegativeInfinity;
    private float _timerLastBodyClickX, _timerLastBodyClickY;

    // 课堂窗开着时自动切穿透（不然记事本 / 下层课件被浮层挡住）；
    // 关掉最后一个课堂窗时恢复原状态。
    private bool _classroomPassThroughForced, _classroomPassThroughBefore;

    internal TimerMode TimerKind => _timerKind;
    internal bool TimerActive => _timerActive;
    internal bool TimerPaused => _timerPaused;
    internal bool TimerFinished => _timerFinished;
    internal bool TimerExpanded => _timerFullscreen;      // 兼容旧字段名：现在是"全屏"
    internal bool TimerMinimized => _timerMinimized;
    internal bool TimerCardOpen => _timerCardOpen;
    internal bool TimerSettingsOpen => _timerEdit;        // 兼容旧字段名：现在是"改时长"
    internal bool TimerSoundOn => _timerSoundOn;
    internal float TimerValueMs => _timerValueMs;
    internal float TimerOvertime => _timerOvertimeMs;
    internal int TimerSetH => _timerSetH;
    internal int TimerSetM => _timerSetM;
    internal int TimerSetS => _timerSetS;

    /// <summary>要连续帧：**任一模式**在跑就画（切走的也在跑、也要到点；用户 2026-10-03 定）。</summary>
    internal bool TimerWantsFrame
    {
        get
        {
            if (!_timerCardOpen) return false;
            for (int i = 0; i < 3; i++)
            {
                var r = GetRun((TimerMode)i);
                if (r.Active && !r.Paused) return true;
            }
            return false;
        }
    }

    /// <summary>窗口这一刻的矩形（最小化/全屏都从这一处算）。</summary>
    internal RectF TimerCardRect()
    {
        var screen = ScreenRectPhysical();
        LoadTimerPosIfNeeded();
        return TimerWin.WindowRect(screen, DpiScale, _timerCX, _timerCY, _timerMinimized, _timerFullscreen);
    }

    /// <summary>计时窗的物理缩放单位（Overlay 画字/按钮要用同一个）。</summary>
    internal float TimerUnit() => TimerWin.Unit(ScreenRectPhysical(), DpiScale);

    internal bool TimerCardContains(float x, float y)
        => _timerCardOpen && TimerCardRect().Contains(x, y);

    /// <summary>窗口上显示的主数字：倒计时待机=设定时长；到点=超时 `+00:27`；其余=计数。</summary>
    internal string TimerDisplayText()
    {
        if (_timerKind == TimerMode.Countdown)
        {
            if (!_timerActive) return TimerWin.Format(_timerDurationMs, TimerMode.Countdown);
            if (_timerFinished) return TimerWin.FormatOvertime(_timerOvertimeMs);
            return TimerWin.Format(_timerValueMs, TimerMode.Countdown);
        }
        return TimerWin.Format(_timerValueMs, _timerKind);
    }

    /// <summary>环的填充比例：倒计时=剩余占比；正计时/秒表=当前一分钟内的进度。</summary>
    internal float TimerRingFraction()
    {
        if (_timerKind == TimerMode.Countdown)
        {
            if (!_timerActive) return 1f;
            if (_timerFinished) return 0f;
            return _timerDurationMs > 0f ? Math.Clamp(_timerValueMs / _timerDurationMs, 0f, 1f) : 0f;
        }
        if (!_timerActive) return 0f;
        double elapsed = TimerElapsedMs();
        return (float)((elapsed % 60000.0) / 60000.0);
    }

    /// <summary>预计结束时刻文本（仅倒计时跑着时显示；InkClass 同款药丸）。</summary>
    internal string TimerEndText()
    {
        var t = DateTime.Now + TimeSpan.FromMilliseconds(Math.Max(0f, _timerValueMs));
        return "预计 " + t.ToString("HH:mm") + " 到点";
    }

    private TimerRun CaptureRun() => new()
    {
        Active = _timerActive, Paused = _timerPaused, Finished = _timerFinished,
        Accum = _timerAccumMs, Anchor = _timerAnchorMs,
        Value = _timerValueMs, Overtime = _timerOvertimeMs, ShownUnit = _timerShownUnit,
    };

    private void ApplyRun(in TimerRun r)
    {
        _timerActive = r.Active; _timerPaused = r.Paused; _timerFinished = r.Finished;
        _timerAccumMs = r.Accum; _timerAnchorMs = r.Anchor;
        _timerValueMs = r.Value; _timerOvertimeMs = r.Overtime; _timerShownUnit = r.ShownUnit;
    }

    private TimerRun GetRun(TimerMode m) => m == _timerKind ? CaptureRun() : _timerRuns[(int)m];
    private void SetRun(TimerMode m, in TimerRun r) { if (m == _timerKind) ApplyRun(r); else _timerRuns[(int)m] = r; }
    private void ParkVisibleRun() => _timerRuns[(int)_timerKind] = CaptureRun();
    private void LoadRun(TimerMode m) { _timerKind = m; ApplyRun(_timerRuns[(int)m]); }
    private static double RunElapsed(in TimerRun r, double now)
        => r.Accum + (r.Active && !r.Paused ? now - r.Anchor : 0);

    /// <summary>把某个模式的数值按当前时间推一遍（切页签装载后也用它）。</summary>
    private void RefreshRun(TimerMode m)
    {
        var r = GetRun(m);
        double elapsed = RunElapsed(r, NowMs);
        if (m != TimerMode.Countdown) { r.Value = (float)elapsed; r.Overtime = 0f; }
        else if (elapsed <= _timerDurationMs) { r.Value = (float)(_timerDurationMs - elapsed); r.Overtime = 0f; }
        else { r.Value = 0f; r.Overtime = (float)(elapsed - _timerDurationMs); }
        SetRun(m, r);
    }

    /// <summary>页签小圆点：这个模式在跑（亮）/ 暂停（灰）。</summary>
    internal bool TimerTabRunning(int i) { var r = GetRun((TimerMode)i); return r.Active && !r.Paused; }
    internal bool TimerTabPaused(int i) { var r = GetRun((TimerMode)i); return r.Active && r.Paused; }

    private double TimerElapsedMs()
        => _timerAccumMs + (_timerActive && !_timerPaused ? NowMs - _timerAnchorMs : 0);

    private void RefreshTimerValue()
    {
        double elapsed = TimerElapsedMs();
        if (_timerKind != TimerMode.Countdown)
        {
            _timerValueMs = (float)elapsed;
            _timerOvertimeMs = 0f;
            return;
        }
        if (elapsed <= _timerDurationMs)
        {
            _timerValueMs = (float)(_timerDurationMs - elapsed);
            _timerOvertimeMs = 0f;
        }
        else
        {
            _timerValueMs = 0f;
            _timerOvertimeMs = (float)(elapsed - _timerDurationMs);   // 到点后继续正计时（超时）
        }
    }

    private void SyncTimerDurationFromSet()
        => _timerDurationMs = (_timerSetH * 3600f + _timerSetM * 60f + _timerSetS) * 1000f;

    private long TimerDisplayUnit()
        => (long)((_timerFinished && _timerKind == TimerMode.Countdown ? _timerOvertimeMs : _timerValueMs)
                  / (_timerKind == TimerMode.Stopwatch ? 10f : 1000f));

    /// <summary>启动器点「计时器」：开窗、回到待机态（倒计时保留上次设定）。</summary>
    internal void OpenTimerCardFromUi()
    {
        _timerCardOpen = true;
        for (int i = 0; i < 3; i++) _timerRuns[i] = default;   // 开窗 = 三个模式都归零
        _timerActive = false;
        _timerPaused = false;
        _timerFinished = false;
        _timerAccumMs = 0;
        _timerEdit = false;
        _timerMinimized = false;
        _timerFullscreen = false;
        _timerRestoreArmed = false;
        _timerDragging = false;
        _timerShownUnit = long.MinValue;
        ClassroomWantPassThrough(true);
        RefreshTimerValue();
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"计时器：打开窗口（{TimerWin.ModeName(_timerKind)}）");
    }

    internal void StartTimerFromUi(TimerMode mode, float seconds)
    {
        if (mode != _timerKind) { RefreshRun(_timerKind); ParkVisibleRun(); LoadRun(mode); }
        if (mode == TimerMode.Countdown)
        {
            int total = Math.Max(1, (int)MathF.Round(seconds));
            _timerSetH = total / 3600;
            _timerSetM = (total % 3600) / 60;
            _timerSetS = total % 60;
            SyncTimerDurationFromSet();
        }
        _timerAccumMs = 0;
        _timerAnchorMs = NowMs;
        _timerActive = true;
        _timerPaused = false;
        _timerFinished = false;
        _timerCardOpen = true;
        _timerEdit = false;
        _timerShownUnit = long.MinValue;
        RefreshTimerValue();
        SaveTimerPrefs();
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"计时器：{TimerWin.ModeName(mode)} 开始"
                          + (mode == TimerMode.Countdown ? $"（{_timerDurationMs / 1000f:F0} 秒）" : ""));
    }

    /// <summary>用当前设定开始（倒计时全零时兜底成 1 分钟，同 InkClass）。</summary>
    private void StartTimerFromCurrentSet()
    {
        if (_timerKind == TimerMode.Countdown)
        {
            if (_timerSetH == 0 && _timerSetM == 0 && _timerSetS == 0)
            {
                _timerSetM = 1;
                SyncTimerDurationFromSet();
            }
            StartTimerFromUi(TimerMode.Countdown, _timerDurationMs / 1000f);
        }
        else
        {
            StartTimerFromUi(_timerKind, 0f);
        }
    }

    internal void PauseTimerFromUi()
    {
        if (!_timerActive || _timerPaused || _timerFinished) return;
        _timerAccumMs += NowMs - _timerAnchorMs;
        _timerPaused = true;
        RefreshTimerValue();
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine("计时器：暂停");
    }

    internal void ResumeTimerFromUi()
    {
        if (!_timerActive) return;
        if (_timerFinished) { StartTimerFromCurrentSet(); return; }
        if (!_timerPaused) return;
        _timerPaused = false;
        _timerAnchorMs = NowMs;
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine("计时器：继续");
    }

    /// <summary>重置：停表、回待机（倒计时保留设定；正/秒归零）。</summary>
    private void ResetTimerFromUi()
    {
        _timerActive = false;
        _timerPaused = false;
        _timerFinished = false;
        _timerAccumMs = 0;
        _timerShownUnit = long.MinValue;
        RefreshTimerValue();
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine("计时器：重置回待机");
    }

    /// <summary>✕：停止并关窗（规范里唯一的 ✕ 语义；InkClass 也是关窗即停表）。</summary>
    internal void StopTimerFromUi()
    {
        bool was = _timerActive || _timerCardOpen;
        for (int i = 0; i < 3; i++) _timerRuns[i] = default;   // ✕ = 三模式全部停止
        _timerActive = false;
        _timerPaused = false;
        _timerFinished = false;
        _timerAccumMs = 0;
        _timerCardOpen = false;
        _timerEdit = false;
        _timerMinimized = false;
        _timerFullscreen = false;
        _timerDragging = false;
        _timerRestoreArmed = false;
        // `_timerCapturing` 不在这里清：它是"这一次按下归窗口"的凭据，
        // 必须等抬指那一下由引擎统一放开（提前清 = 漏掉 ReleaseCapture，整机鼠标挂住）。
        _timerPressAtMs = double.NegativeInfinity;
        HideTimerInputWindow();
        ClassroomWantPassThrough(false);
        _dirty = true;
        NotifyUiStateChanged();
        if (was) Console.WriteLine("计时器：停止并关窗");
    }

    /// <summary>改时长 ±（unit 0 时 / 1 分 / 2 秒）。</summary>
    private void AdjustTimerSet(int unit, int delta)
    {
        if (!_timerEdit || _timerKind != TimerMode.Countdown) return;
        if (unit == 0) _timerSetH = Math.Clamp(_timerSetH + delta, 0, 99);
        else if (unit == 1) _timerSetM = Math.Clamp(_timerSetM + delta, 0, 59);
        else _timerSetS = Math.Clamp(_timerSetS + delta, 0, 59);
        SyncTimerDurationFromSet();
        RefreshTimerValue();
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"计时器：设定 {_timerSetH:00}:{_timerSetM:00}:{_timerSetS:00}");
    }

    private void SaveTimerPrefs()
    {
        SetUiPref("timerKind", _timerKind == TimerMode.Countdown ? null : ((int)_timerKind).ToString());
        SetUiPref("timerH", _timerSetH == 0 ? null : _timerSetH.ToString());
        SetUiPref("timerM", _timerSetM == 5 ? null : _timerSetM.ToString());
        SetUiPref("timerS", _timerSetS == 0 ? null : _timerSetS.ToString());
        SetUiPref("timerSound", _timerSoundOn ? null : "0");
    }

    private void LoadTimerPrefs()
    {
        if (int.TryParse(GetUiPref("timerKind"), out int k) && k is >= 0 and <= 2) _timerKind = (TimerMode)k;
        if (int.TryParse(GetUiPref("timerH"), out int h)) _timerSetH = Math.Clamp(h, 0, 99);
        if (int.TryParse(GetUiPref("timerM"), out int m)) _timerSetM = Math.Clamp(m, 0, 59);
        if (int.TryParse(GetUiPref("timerS"), out int s)) _timerSetS = Math.Clamp(s, 0, 59);
        _timerSoundOn = GetUiPref("timerSound") != "0";
        SyncTimerDurationFromSet();
    }

    private void PlayTimerBeep()
    {
        if (!_timerSoundOn) return;
        _timerBeepCount++;
        try { Native.MessageBeep(0x00000040 /*MB_ICONASTERISK*/); } catch { }
    }

    /// <summary>每帧推一次（Loop 与 RenderAll 各一处，理由同 StepPptBar）。</summary>
    internal void StepTimerCard()
    {
        if (_timerCardOpen)
        {
            double now = NowMs;
            for (int i = 0; i < 3; i++)
            {
                var m = (TimerMode)i;
                var r = GetRun(m);
                if (!r.Active || r.Paused) continue;
                double elapsed = RunElapsed(r, now);
                if (m == TimerMode.Countdown)
                {
                    if (elapsed <= _timerDurationMs)
                    {
                        r.Value = (float)(_timerDurationMs - elapsed);
                        r.Overtime = 0f;
                    }
                    else
                    {
                        r.Value = 0f;
                        r.Overtime = (float)(elapsed - _timerDurationMs);
                        if (!r.Finished)
                        {
                            r.Finished = true;
                            PlayTimerBeep();
                            NotifyUiStateChanged();
                            Console.WriteLine($"计时器：[{TimerWin.ModeName(m)}] 到点（继续显示超时）");
                        }
                    }
                }
                else
                {
                    r.Value = (float)elapsed;
                    r.Overtime = 0f;
                }
                SetRun(m, r);
                if (m == _timerKind)
                {
                    long unit = TimerDisplayUnit();
                    if (unit != _timerShownUnit) { _timerShownUnit = unit; _dirty = true; }
                    long sec = (long)(now / 1000.0);
                    if (sec != _timerNotifiedSec) { _timerNotifiedSec = sec; NotifyUiStateChanged(); }
                }
            }
        }
        SyncTimerInputWindow();
    }

    // ---- 计时窗指针 ----------------------------------------------------------

    private void OnTimerValueClick()
    {
        if (_timerKind == TimerMode.Countdown && !_timerActive)
        {
            _timerEdit = !_timerEdit;               // 待机：点数字进/出"改时长"
        }
        else if (_timerActive)
        {
            if (_timerPaused) ResumeTimerFromUi();
            else PauseTimerFromUi();
            // 到点后 ResumeTimerFromUi 会按"从头重开"处理（同旧卡）。
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    private void OnTimerStartClick()
    {
        if (!_timerActive) { StartTimerFromCurrentSet(); return; }
        if (_timerPaused) { ResumeTimerFromUi(); return; }
        if (_timerFinished) { StartTimerFromCurrentSet(); return; }   // 到点重开
        PauseTimerFromUi();
    }

    /// <summary>返回 true = 这一下归窗口。按下时调用方负责 SetCapture。</summary>
    internal bool TimerCardPointerDown(float x, float y)
    {
        if (!_timerCardOpen) return false;
        var screen = ScreenRectPhysical();
        float dpi = DpiScale;
        var win = TimerWin.WindowRect(screen, dpi, _timerCX, _timerCY, _timerMinimized, _timerFullscreen);
        if (!win.Contains(x, y)) return false;

        if (_timerMinimized)
        {
            _timerGrabDX = x - win.MinX;
            _timerGrabDY = y - win.MinY;
            _timerPressX = x; _timerPressY = y;
            _timerPressAtMs = NowMs;
            _timerDragging = false;
            _timerRestoreArmed = TimerWin.MinimalDigitsRect(win, dpi).Contains(x, y);
            return true;
        }

        float u = TimerWin.Unit(screen, dpi);
        var card = TimerWin.CardRect(win, u, false, _timerFullscreen);
        float lu = TimerWin.Layout(card, u);

        if (_timerEdit && _timerKind == TimerMode.Countdown)
        {
            for (int unit = 0; unit < 3; unit++)
                for (int pair = 0; pair < 2; pair++)
                    for (int row = 0; row < 2; row++)
                        if (TimerWin.StepRect(card, lu, unit, pair, row).Contains(x, y))
                        {
                            AdjustTimerSet(unit, (pair == 0 ? +1 : -1) * (row == 0 ? 5 : 1));
                            return true;
                        }
        }

        var zone = TimerWin.ZoneAt(card, lu, x, y, _timerEdit, _timerKind == TimerMode.Countdown);
        switch (zone)
        {
            case TimerZone.Close: StopTimerFromUi(); return true;
            case TimerZone.Minimize:
                _timerMinimized = true;
                _timerFullscreen = false;
                _timerEdit = false;
                _dirty = true; NotifyUiStateChanged();
                return true;
            case TimerZone.Fullscreen:
                _timerFullscreen = !_timerFullscreen;
                _timerEdit = false;
                _dirty = true; NotifyUiStateChanged();
                return true;
            case TimerZone.Tab0: case TimerZone.Tab1: case TimerZone.Tab2:
            {
                var target = (TimerMode)(zone - TimerZone.Tab0);
                if (target != _timerKind)
                {
                    // 切页签 = 把当前模式"停靠"进数组（跑着的继续跑），再装载目标模式。
                    // 用户 2026-10-03：允许切、切走继续跑，页签上用小圆点表示在跑。
                    RefreshRun(_timerKind);
                    ParkVisibleRun();
                    LoadRun(target);
                    _timerEdit = false;
                    RefreshRun(target);
                    _timerShownUnit = long.MinValue;
                    SaveTimerPrefs();
                    _dirty = true;
                    NotifyUiStateChanged();
                    Console.WriteLine($"计时器：切到 {TimerWin.ModeName(target)}"
                        + (TimerTabRunning((int)target) ? "（继续跑）" : ""));
                }
                return true;
            }
            case TimerZone.Ok:
                _timerEdit = false;
                _dirty = true; NotifyUiStateChanged();
                return true;
            case TimerZone.Value: OnTimerValueClick(); return true;
            case TimerZone.Start: OnTimerStartClick(); return true;
            case TimerZone.Reset: ResetTimerFromUi(); return true;
        }

        // Body：记按下（双击 = 全屏开关；拖动在 Move 里按"非全屏 + 真的按着"放行）
        _timerGrabDX = x - win.MinX;
        _timerGrabDY = y - win.MinY;
        _timerPressX = x; _timerPressY = y;
        _timerPressAtMs = NowMs;
        _timerDragging = false;
        return true;
    }

    internal bool TimerCardPointerMove(float x, float y)
    {
        if (!_timerCardOpen) return false;
        // **只有"真的按着"才进入拖动**（`_timerCapturing` 由引擎在按下那一刻立起、抬指清掉）。
        if (_timerCapturing && !_timerFullscreen)
        {
            if (_timerPressAtMs >= 0 && !_timerDragging)
            {
                float lim = 4f * DpiScale;
                if (MathF.Abs(x - _timerPressX) > lim || MathF.Abs(y - _timerPressY) > lim)
                {
                    _timerDragging = true;
                    _timerRestoreArmed = false;      // 从数字上拖走 = 改变主意，不还原
                }
            }
            if (_timerDragging)
            {
                var screen = ScreenRectPhysical();
                float dpi = DpiScale;
                var win = TimerWin.WindowRect(screen, dpi, _timerCX, _timerCY, _timerMinimized, _timerFullscreen);
                float w = win.MaxX - win.MinX, h = win.MaxY - win.MinY;
                float m = 8f * dpi;
                float left = Math.Clamp(x - _timerGrabDX, screen.MinX + m,
                                        MathF.Max(screen.MinX + m, screen.MaxX - m - w));
                float top = Math.Clamp(y - _timerGrabDY, screen.MinY + m,
                                       MathF.Max(screen.MinY + m, screen.MaxY - m - h));
                _timerCX = left + w * 0.5f;
                _timerCY = top + h * 0.5f;
                _dirty = true;
                return true;
            }
        }
        return TimerCardContains(x, y);
    }

    internal void TimerCardPointerUp(float x, float y)
    {
        if (!_timerCardOpen) return;
        if (_timerDragging)
        {
            _timerDragging = false;
            _timerPressAtMs = double.NegativeInfinity;
            SaveTimerPos();
            _dirty = true;
            return;
        }
        if (_timerRestoreArmed)
        {
            _timerRestoreArmed = false;
            _timerPressAtMs = double.NegativeInfinity;
            _timerMinimized = false;                 // 点数字还原（InkClass 同款）
            _dirty = true;
            NotifyUiStateChanged();
            return;
        }
        if (_timerPressAtMs >= 0)
        {
            _timerPressAtMs = double.NegativeInfinity;
            if (!_timerMinimized)
            {
                // 双击空白 = 全屏开关（用户 2026-10-02：全屏加个"点哪里"的入口）
                double t = NowMs;
                float tol = 12f * DpiScale;
                if (t - _timerLastBodyClickMs < 350
                    && MathF.Abs(x - _timerLastBodyClickX) < tol
                    && MathF.Abs(y - _timerLastBodyClickY) < tol)
                {
                    _timerLastBodyClickMs = double.NegativeInfinity;
                    _timerFullscreen = !_timerFullscreen;
                    NotifyUiStateChanged();
                    Console.WriteLine(_timerFullscreen ? "计时器：全屏" : "计时器：退出全屏");
                }
                else
                {
                    _timerLastBodyClickMs = t;
                    _timerLastBodyClickX = x;
                    _timerLastBodyClickY = y;
                }
            }
            _dirty = true;
        }
    }

    private void LoadTimerPosIfNeeded()
    {
        if (_timerPosLoaded) return;
        _timerPosLoaded = true;
        var p = GetUiPref("timerPos");
        if (string.IsNullOrEmpty(p)) return;
        var parts = p.Split(',');
        if (parts.Length != 2) return;
        if (!float.TryParse(parts[0], out float lx) || !float.TryParse(parts[1], out float ly)) return;
        var screen = ScreenRectPhysical();
        _timerCX = screen.MinX + lx * DpiScale;
        _timerCY = screen.MinY + ly * DpiScale;
    }

    private void SaveTimerPos()
    {
        var screen = ScreenRectPhysical();
        SetUiPref("timerPos", $"{(_timerCX - screen.MinX) / DpiScale:F0},"
                            + $"{(_timerCY - screen.MinY) / DpiScale:F0}");
    }

    // ---- 计时窗接输入小窗（穿透时）------------------------------------------

    private IntPtr _timerInputHwnd;
    private RectF _timerInputRect;
    private bool _timerInputShown;
    internal bool TimerInputWindowShown => _timerInputShown;
    internal void TimerInputWindowRect(out RectF rect) => rect = _timerInputRect;

    private void SyncTimerInputWindow()
    {
        if (!_timerCardOpen || !PassThrough) { HideTimerInputWindow(); return; }
        EnsureTimerInputWindow();
        if (_timerInputHwnd == IntPtr.Zero) return;
        var box = TimerCardRect();
        if (_timerInputShown && box.Equals(_timerInputRect)) return;
        Native.SetWindowPos(_timerInputHwnd, Native.HWND_TOPMOST,
            (int)MathF.Floor(box.MinX), (int)MathF.Floor(box.MinY),
            Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX)),
            Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY)),
            Native.SWP_NOACTIVATE | (_timerInputShown ? 0 : Native.SWP_SHOWWINDOW));
        _timerInputRect = box;
        _timerInputShown = true;
    }

    private void HideTimerInputWindow()
    {
        if (_timerInputHwnd != IntPtr.Zero && _timerInputShown)
            Native.ShowWindow(_timerInputHwnd, Native.SW_HIDE);
        _timerInputShown = false;
        // ⚠ 这里**不动 `_timerCapturing`**：捕获的放开只认"抬指"那一下。
    }

    private void EnsureTimerInputWindow()
    {
        if (_timerInputHwnd != IntPtr.Zero) return;
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _timerInputHwnd = Native.CreateWindowEx(exStyle, _className, "InkTeachTimerInput",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_timerInputHwnd == IntPtr.Zero)
        {
            Console.WriteLine("计时器接输入小窗创建失败: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return;
        }
        Native.SetLayeredWindowAttributes(_timerInputHwnd, 0, 1, Native.LWA_ALPHA);
        Native.DisableSystemPressAndHold(_timerInputHwnd);
    }

    private IntPtr TimerInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        void SetPointerCanvas(float sx, float sy)
        {
            float cx = sx, cy = sy;
            ScreenToCanvas(ref cx, ref cy);
            PointerX = cx; PointerY = cy; PointerInside = true;
        }
        switch (msg)
        {
            case Native.WM_NCHITTEST: _cntNcHitTest++; _cntNcHitClient++; return new IntPtr(Native.HTCLIENT);
            case Native.WM_MOUSEACTIVATE: return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_SETCURSOR: ApplyCursor(force: true); return new IntPtr(1);
            case Native.WM_ERASEBKGND: return new IntPtr(1);
            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps); Native.EndPaint(hWnd, ref ps); return IntPtr.Zero;
            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype)) return IntPtr.Zero;
                StampInput(); _cntDown++; LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                if (TimerCardPointerDown(sx, sy))
                {
                    _drawing = false;
                    _timerCapturing = true;
                    Native.SetCapture(hWnd);
                }
                _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }
            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype)) return IntPtr.Zero;
                StampInput(); _cntMove++; LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                TimerCardPointerMove(sx, sy);
                _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }
            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (ReadPointer(id, out float sx, out float sy, out _, out _, out _))
                {
                    StampInput(); _cntUp++;
                    SetPointerCanvas(sx, sy);
                    if (_timerCapturing) { _timerCapturing = false; TimerCardPointerUp(sx, sy); }
                }
                Native.ReleaseCapture();
                _drawing = false; _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }
            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++; _timerCapturing = false; _timerDragging = false; _dirty = true;
                return IntPtr.Zero;
            default: return IntPtr.Zero;
        }
    }

    // =====================================================================
    //  点名
    // =====================================================================

    private bool _rollCardOpen;
    private bool _rollQuick;                     // 「随机一人」快捷态：抽完 1.5 秒自动关
    private int _rollCount = 1;
    private int _rollMaxNum = 60;                // 无名单时：学号 1..N

    private readonly List<int> _rollPool = new();
    private int _rollPoolTotal = -1;
    private string[] _rollResult = Array.Empty<string>();
    private string _rollFace = "";
    private bool _rolling;
    private int _rollDrawnCount;
    private double _rollStopAtMs, _rollNextTickMs, _rollCloseAtMs;
    private Random _rollRng = Random.Shared;
    private float _rollCX = float.NaN, _rollCY = float.NaN;
    private bool _rollPosLoaded;
    private bool _rollDragging, _rollCapturing;
    private double _rollPressAtMs = double.NegativeInfinity;
    private float _rollPressX, _rollPressY, _rollGrabDX, _rollGrabDY;
    private IntPtr _rollInputHwnd;
    private RectF _rollInputRect;
    private bool _rollInputShown;

    internal bool RollCardOpen => _rollCardOpen;
    /// <summary>兼容旧字段名：待抽态（没在滚、也还没出结果）。</summary>
    internal bool RollSettingsOpen => !_rolling && _rollResult.Length == 0;
    internal bool RollingNow => _rolling;
    internal bool RollQuick => _rollQuick;
    internal string RollFaceNow => _rollFace;
    internal string[] RollResultNow => _rollResult;
    internal int RollPoolNow => _rollPool.Count;
    internal int RollDrawnNow => _rollDrawnCount;
    internal int RollCountNow => _rollCount;
    internal int RollTotalNow => RollCandidates().Length;
    internal int RollMaxNumNow => _rollMaxNum;
    internal bool RollHasNames => Names.Length > 0;

    internal bool RollWantsFrame => _rollCardOpen && (_rolling || _rollCloseAtMs > 0);

    internal RectF RollCardRect()
    {
        var screen = ScreenRectPhysical();
        LoadRollPosIfNeeded();
        return RollWin.WindowRect(screen, DpiScale, _rollCX, _rollCY);
    }

    /// <summary>点名窗的物理缩放单位（Overlay 画字/按钮要用同一个）。</summary>
    internal float RollUnit() => RollWin.Unit(ScreenRectPhysical(), DpiScale);

    internal bool RollCardContains(float x, float y)
        => _rollCardOpen && RollCardRect().Contains(x, y);

    internal void OpenRollCardFromUi()
    {
        _rollQuick = false;
        _rollCardOpen = true;
        _rolling = false;
        _rollResult = Array.Empty<string>();
        _rollFace = "";
        _rollCloseAtMs = 0;
        ReloadNamesFromUi();       // 开窗即重读：记事本改完再开窗就能用
        ClassroomWantPassThrough(true);
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine("点名：打开窗口");
    }

    /// <summary>「随机一人」：自动抽 1 人、出结果 1.5 秒后自动关（InkClass 同款）。</summary>
    internal void OpenRollOneFromUi()
    {
        _rollQuick = true;
        _rollCardOpen = true;
        _rolling = false;
        _rollResult = Array.Empty<string>();
        _rollFace = "";
        _rollCloseAtMs = 0;
        ReloadNamesFromUi();
        _rollCount = 1;
        ClassroomWantPassThrough(true);
        StartRollFromUi();
    }

    /// <summary>✕：停止滚动并关窗。</summary>
    internal void CloseRollCardFromUi()
    {
        bool was = _rollCardOpen;
        _rollCardOpen = false;
        _rolling = false;
        _rollQuick = false;
        _rollCloseAtMs = 0;
        _rollDragging = false;
        // `_rollCapturing` 同计时窗：等抬指统一放开。
        _rollPressAtMs = double.NegativeInfinity;
        HideRollInputWindow();
        ClassroomWantPassThrough(false);
        _dirty = true;
        NotifyUiStateChanged();
        if (was) Console.WriteLine("点名：关闭窗口");
    }

    /// <summary>抽奖（或再抽一次）：先滚 5 次×150ms，再定格（InkClass 原节奏）。</summary>
    internal void StartRollFromUi()
    {
        if (_rolling) return;
        var all = RollCandidates();
        if (all.Length == 0) return;
        _rollCount = Math.Clamp(_rollCount, 1, all.Length);
        EnsureRollPool(all.Length);
        if (_rollPool.Count == 0) { FillRollPool(all.Length); _rollDrawnCount = 0; }
        _rolling = true;
        _rollResult = Array.Empty<string>();
        _rollFace = RollPreviewText();
        _rollStopAtMs = NowMs + 5 * 150;
        _rollNextTickMs = NowMs + 150;
        _rollCloseAtMs = 0;
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"点名：一次 {_rollCount} 人"
                          + (RollHasNames ? $"（名单 {Names.Length} 人）" : "（1–60 编号）"));
    }

    /// <summary>「重置」：清空不重复池（结果保留）。</summary>
    private void ResetRollPoolFromUi()
    {
        _rollPool.Clear();
        _rollDrawnCount = 0;
        _rollPoolTotal = -1;
        EnsureRollPool(RollCandidates().Length);
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine("点名：重置不重复池");
    }

    private string[] RollCandidates()
    {
        if (Names.Length > 0) return Names;
        var list = new List<string>(_rollMaxNum);
        for (int v = 1; v <= _rollMaxNum; v++) list.Add(v.ToString());
        return list.ToArray();
    }

    private void EnsureRollPool(int total)
    {
        // 池子空了 = 上一轮抽完；这里补满（"抽完自动重置"），并把"已抽"归零。
        if (_rollPoolTotal == total && _rollPool.Count > 0) return;
        _rollPoolTotal = total;
        FillRollPool(total);
        _rollDrawnCount = 0;
    }

    private void FillRollPool(int total)
    {
        _rollPool.Clear();
        for (int i = 0; i < total; i++) _rollPool.Add(i);
    }

    /// <summary>滚动中显示的预览：本次人数个随机（预览内不重复，不动池子）。</summary>
    private string RollPreviewText()
    {
        var all = RollCandidates();
        if (all.Length == 0) return "—";
        int n = Math.Clamp(_rollCount, 1, all.Length);
        var picks = new List<string>(n);
        var used = new HashSet<int>();
        while (picks.Count < n)
        {
            int idx = _rollRng.Next(all.Length);
            if (all.Length > 1 && used.Contains(idx)) continue;
            used.Add(idx);
            picks.Add(all[idx]);
        }
        return string.Join("  ", picks);
    }

    private void FinishRoll()
    {
        _rolling = false;
        var all = RollCandidates();
        if (all.Length == 0) { _rollResult = Array.Empty<string>(); return; }
        int n = Math.Clamp(_rollCount, 1, all.Length);
        var res = new List<string>(n);
        for (int k = 0; k < n; k++)
        {
            if (_rollPool.Count == 0) { FillRollPool(all.Length); _rollDrawnCount = 0; }   // 抽完自动重置
            int j = _rollRng.Next(_rollPool.Count);
            int idx = _rollPool[j];
            _rollPool.RemoveAt(j);
            res.Add(all[idx]);
        }
        _rollResult = res.ToArray();
        _rollDrawnCount += res.Count;
        Console.WriteLine("点名：" + string.Join("、", _rollResult));
    }

    internal void StepRollCard()
    {
        if (_rollCardOpen && _rolling)
        {
            double now = NowMs;
            if (now >= _rollStopAtMs)
            {
                FinishRoll();
                _dirty = true;
                NotifyUiStateChanged();
                if (_rollQuick) _rollCloseAtMs = now + 1500;
            }
            else if (now >= _rollNextTickMs)
            {
                _rollNextTickMs += 150;
                _rollFace = RollPreviewText();
                _dirty = true;
            }
        }
        if (_rollCardOpen && _rollCloseAtMs > 0 && NowMs >= _rollCloseAtMs)
            CloseRollCardFromUi();
        SyncRollInputWindow();
    }

    private void AdjustRollCount(int delta)
    {
        int total = RollCandidates().Length;
        _rollCount = Math.Clamp(_rollCount + delta, 1, Math.Max(1, total));
        SaveRollPrefs();
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"点名：一次 {_rollCount} 人");
    }

    private void SaveRollPrefs()
    {
        SetUiPref("rollCount", _rollCount == 1 ? null : _rollCount.ToString());
        SetUiPref("rollMaxNum", _rollMaxNum == 60 ? null : _rollMaxNum.ToString());
    }

    private void LoadRollPrefs()
    {
        if (int.TryParse(GetUiPref("rollCount"), out int c)) _rollCount = Math.Max(1, c);
        if (int.TryParse(GetUiPref("rollMaxNum"), out int m)) _rollMaxNum = Math.Clamp(m, 1, 200);
    }


    /// <summary>无名单时的学号上限（1..200）。改了清结果/重填池，避免旧号码残留。</summary>
    private void AdjustRollMaxNum(int delta)
    {
        if (Names.Length > 0) return;
        _rollMaxNum = Math.Clamp(_rollMaxNum + delta, 1, 200);
        _rollCount = Math.Clamp(_rollCount, 1, Math.Max(1, _rollMaxNum));
        _rollPoolTotal = -1;
        EnsureRollPool(RollCandidates().Length);
        _rollResult = Array.Empty<string>();
        _rollFace = "";
        SaveRollPrefs();
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"点名：学号范围 1–{_rollMaxNum}");
    }

    internal bool RollCardPointerDown(float x, float y)
    {
        if (!_rollCardOpen) return false;
        var screen = ScreenRectPhysical();
        float dpi = DpiScale;
        var win = RollWin.WindowRect(screen, dpi, _rollCX, _rollCY);
        if (!win.Contains(x, y)) return false;
        float u = RollWin.Unit(screen, dpi);

        switch (RollWin.ZoneAt(win, u, x, y))
        {
            case RollZone.Close: CloseRollCardFromUi(); return true;
            case RollZone.CountMinus: AdjustRollCount(-1); return true;
            case RollZone.CountPlus: AdjustRollCount(+1); return true;
            case RollZone.NumMinus: AdjustRollMaxNum(-1); return true;
            case RollZone.NumPlus: AdjustRollMaxNum(+1); return true;
            case RollZone.Draw: StartRollFromUi(); return true;
            case RollZone.PoolReset: ResetRollPoolFromUi(); return true;

            case RollZone.Reload: ReloadNamesFromUi(); return true;
        }

        _rollGrabDX = x - win.MinX;
        _rollGrabDY = y - win.MinY;
        _rollPressX = x; _rollPressY = y;
        _rollPressAtMs = NowMs;
        _rollDragging = false;
        return true;
    }

    internal bool RollCardPointerMove(float x, float y)
    {
        if (!_rollCardOpen) return false;
        // 同计时窗：**只有真的按着才拖动**。
        if (_rollCapturing)
        {
            if (_rollPressAtMs >= 0 && !_rollDragging)
            {
                float lim = 4f * DpiScale;
                if (MathF.Abs(x - _rollPressX) > lim || MathF.Abs(y - _rollPressY) > lim)
                    _rollDragging = true;
            }
            if (_rollDragging)
            {
                var screen = ScreenRectPhysical();
                float dpi = DpiScale;
                var win = RollWin.WindowRect(screen, dpi, _rollCX, _rollCY);
                float w = win.MaxX - win.MinX, h = win.MaxY - win.MinY;
                float m = 8f * dpi;
                float left = Math.Clamp(x - _rollGrabDX, screen.MinX + m,
                                        MathF.Max(screen.MinX + m, screen.MaxX - m - w));
                float top = Math.Clamp(y - _rollGrabDY, screen.MinY + m,
                                       MathF.Max(screen.MinY + m, screen.MaxY - m - h));
                _rollCX = left + w * 0.5f;
                _rollCY = top + h * 0.5f;
                _dirty = true;
                return true;
            }
        }
        return RollCardContains(x, y);
    }

    internal void RollCardPointerUp(float x, float y)
    {
        if (!_rollCardOpen) return;
        if (_rollDragging)
        {
            _rollDragging = false;
            _rollPressAtMs = double.NegativeInfinity;
            SaveRollPos();
            _dirty = true;
            return;
        }
        if (_rollPressAtMs >= 0)
        {
            _rollPressAtMs = double.NegativeInfinity;
            _dirty = true;
        }
    }

    private void LoadRollPosIfNeeded()
    {
        if (_rollPosLoaded) return;
        _rollPosLoaded = true;
        var p = GetUiPref("rollPos");
        if (string.IsNullOrEmpty(p)) return;
        var parts = p.Split(',');
        if (parts.Length != 2) return;
        if (!float.TryParse(parts[0], out float lx) || !float.TryParse(parts[1], out float ly)) return;
        var screen = ScreenRectPhysical();
        _rollCX = screen.MinX + lx * DpiScale;
        _rollCY = screen.MinY + ly * DpiScale;
    }

    private void SaveRollPos()
    {
        var screen = ScreenRectPhysical();
        SetUiPref("rollPos", $"{(_rollCX - screen.MinX) / DpiScale:F0},"
                           + $"{(_rollCY - screen.MinY) / DpiScale:F0}");
    }

    internal bool RollInputWindowShown => _rollInputShown;
    internal void RollInputWindowRect(out RectF rect) => rect = _rollInputRect;

    private void SyncRollInputWindow()
    {
        if (!_rollCardOpen || !PassThrough) { HideRollInputWindow(); return; }
        EnsureRollInputWindow();
        if (_rollInputHwnd == IntPtr.Zero) return;
        var box = RollCardRect();
        if (_rollInputShown && box.Equals(_rollInputRect)) return;
        Native.SetWindowPos(_rollInputHwnd, Native.HWND_TOPMOST,
            (int)MathF.Floor(box.MinX), (int)MathF.Floor(box.MinY),
            Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX)),
            Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY)),
            Native.SWP_NOACTIVATE | (_rollInputShown ? 0 : Native.SWP_SHOWWINDOW));
        _rollInputRect = box;
        _rollInputShown = true;
    }

    private void HideRollInputWindow()
    {
        if (_rollInputHwnd != IntPtr.Zero && _rollInputShown)
            Native.ShowWindow(_rollInputHwnd, Native.SW_HIDE);
        _rollInputShown = false;
        // 同计时窗：捕获只认抬指那一下清（不许在这里提前清）。
    }

    private void EnsureRollInputWindow()
    {
        if (_rollInputHwnd != IntPtr.Zero) return;
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _rollInputHwnd = Native.CreateWindowEx(exStyle, _className, "InkTeachRollInput",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_rollInputHwnd == IntPtr.Zero)
        {
            Console.WriteLine("点名接输入小窗创建失败: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return;
        }
        Native.SetLayeredWindowAttributes(_rollInputHwnd, 0, 1, Native.LWA_ALPHA);
        Native.DisableSystemPressAndHold(_rollInputHwnd);
    }

    private IntPtr RollInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        void SetPointerCanvas(float sx, float sy)
        {
            float cx = sx, cy = sy;
            ScreenToCanvas(ref cx, ref cy);
            PointerX = cx; PointerY = cy; PointerInside = true;
        }
        switch (msg)
        {
            case Native.WM_NCHITTEST: _cntNcHitTest++; _cntNcHitClient++; return new IntPtr(Native.HTCLIENT);
            case Native.WM_MOUSEACTIVATE: return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_SETCURSOR: ApplyCursor(force: true); return new IntPtr(1);
            case Native.WM_ERASEBKGND: return new IntPtr(1);
            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps); Native.EndPaint(hWnd, ref ps); return IntPtr.Zero;
            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype)) return IntPtr.Zero;
                StampInput(); _cntDown++; LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                if (RollCardPointerDown(sx, sy))
                {
                    _drawing = false;
                    _rollCapturing = true;
                    Native.SetCapture(hWnd);
                }
                _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }
            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype)) return IntPtr.Zero;
                StampInput(); _cntMove++; LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                RollCardPointerMove(sx, sy);
                _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }
            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (ReadPointer(id, out float sx, out float sy, out _, out _, out _))
                {
                    StampInput(); _cntUp++;
                    SetPointerCanvas(sx, sy);
                    if (_rollCapturing) { _rollCapturing = false; RollCardPointerUp(sx, sy); }
                }
                Native.ReleaseCapture();
                _drawing = false; _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }
            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++; _rollCapturing = false; _rollDragging = false; _dirty = true;
                return IntPtr.Zero;
            default: return IntPtr.Zero;
        }
    }

    // ---- 点名名单（Names.txt）-----------------------------------------------

    /// <summary>课堂窗开着时自动切穿透（不然记事本 / 下层课件被浮层挡住）；关掉最后一个恢复。</summary>
    private void ClassroomWantPassThrough(bool on)
    {
        if (on)
        {
            if (_classroomPassThroughForced) return;
            _classroomPassThroughForced = true;
            _classroomPassThroughBefore = PassThrough;
            if (!PassThrough) SetPassThroughFromUi(true);
        }
        else
        {
            if (!_classroomPassThroughForced) return;
            if (_timerCardOpen || _rollCardOpen) return;      // 另一个课堂窗还开着
            _classroomPassThroughForced = false;
            if (!_classroomPassThroughBefore) SetPassThroughFromUi(false);
        }
    }

    internal string[] Names { get; private set; } = Array.Empty<string>();
    internal static string NamesPathOverride;
    internal static string NamesFilePath => NamesPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkTeach", "Names.txt");

    private void LoadNames()
    {
        try
        {
            if (!File.Exists(NamesFilePath)) { Names = Array.Empty<string>(); return; }
            var list = new List<string>();
            foreach (var raw in File.ReadAllLines(NamesFilePath))
            {
                var s = raw.Trim();
                if (s.Length == 0 || s.StartsWith('#')) continue;
                list.Add(s);
            }
            Names = list.ToArray();
        }
        catch (Exception ex)
        {
            Names = Array.Empty<string>();
            Console.WriteLine("点名名单读取失败：" + ex.Message);
        }
    }

    internal void ReloadNamesFromUi()
    {
        LoadNames();
        var all = RollCandidates();
        _rollCount = Math.Clamp(_rollCount, 1, Math.Max(1, all.Length));
        EnsureRollPool(all.Length);
        Console.WriteLine($"点名名单：{Names.Length} 个名字（{NamesFilePath}）");
        NotifyUiStateChanged();
    }

    /// <summary>启动时读课堂偏好（计时设定/提示音/人数）。</summary>
    internal void LoadClassroomPrefs()
    {
        LoadTimerPrefs();
        LoadRollPrefs();
        LoadNames();
    }

    // ---- 自检钩子（开发期用）------------------------------------------------

    internal void TimerTickForTest() => StepTimerCard();
    internal void RollTickForTest() => StepRollCard();
    internal void TimerPointerDownForTest(float x, float y) => TimerCardPointerDown(x, y);
    internal void TimerPointerMoveForTest(float x, float y) => TimerCardPointerMove(x, y);
    internal void TimerPointerUpForTest(float x, float y) => TimerCardPointerUp(x, y);
    internal void RollPointerDownForTest(float x, float y) => RollCardPointerDown(x, y);
    internal void RollPointerMoveForTest(float x, float y) => RollCardPointerMove(x, y);
    internal void RollPointerUpForTest(float x, float y) => RollCardPointerUp(x, y);
    internal int TimerBeepCountForTest => _timerBeepCount;
    internal float TimerOvertimeForTest => _timerOvertimeMs;
    internal void SetRollSeedForTest(int seed) => _rollRng = new Random(seed);
    internal void SetRollMaxForTest(int v)
    {
        if (Names.Length > 0) return;
        _rollMaxNum = Math.Clamp(v, 1, 200);
        _rollCount = Math.Clamp(_rollCount, 1, _rollMaxNum);
        _rollPoolTotal = -1;
        EnsureRollPool(RollCandidates().Length);
        _dirty = true;
    }
    internal void FinishRollNowForTest() { if (_rolling) { FinishRoll(); _dirty = true; NotifyUiStateChanged(); } }
    internal void OpenTimerCardForTest() => OpenTimerCardFromUi();
    internal void OpenRollCardForTest() => OpenRollCardFromUi();
    internal void OpenRollOneForTest() => OpenRollOneFromUi();
}
