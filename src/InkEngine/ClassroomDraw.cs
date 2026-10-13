using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 课堂窗（计时 / 点名）的绘制。**2026-10-05 重设计**：原来是 InkClass
/// CountdownTimerWindow / RandWindow 的 1:1 复刻（浅蓝面板 + 蓝描边 + 一排圆形
/// 按钮 + 红色圆钮关闭），那套版式本身就是别人的识别特征。现在改成和主工具带 /
/// 「更多」面板同一套语言，并且**支持深色主题**（原来只有浅色一档）。
///
/// 颜色的**唯一来源是界面推上来的 <see cref="UiTheme"/>**（`InkUi.Tokens` 那套），
/// 引擎自己不再存一份写死的浅色值——多存一份就等于多一处会漂移的真相。
/// 与 Overlay.cs 同属 OverlayWindow（partial）。
/// </summary>
internal sealed partial class OverlayWindow
{
    /// <summary>课堂窗这一笔要的颜色，全部由 <see cref="UiTheme"/> 现场算出来。</summary>
    private readonly record struct WinPal(
        Color4 Panel, Color4 Border, Color4 Ink, Color4 Muted, Color4 Soft,
        Color4 Accent, Color4 AccentInk, Color4 Track, Color4 Danger, Color4 DangerSoft,
        Color4 RingArc, float Corner);

    /// <summary>
    /// 按当前主题取色。两个"自己决定的"值在这里说明理由：
    ///
    /// · **RingArc（环的进度色）＝ 当前笔色**——这是本项目自己的一个想法：课堂窗
    ///   跟着你手里的笔走。⚠ 但**笔色可能是白**（黑板上写白字），白环画在浅色面板上
    ///   等于没有，所以要过一道**对比度闸**：用 WCAG 的相对亮度对比度，
    ///   **低于 3:1 就退回强调色**。
    ///   为什么用对比度而不是"亮度差"：亮度差是拿绝对值比，红笔配深色面板的差只有
    ///   0.17（看着明明很清楚），第一版就误判成"太暗"把红环换成了蓝环，看图才发现。
    ///   这条闸不能省——"跟着笔色走"好看，"看不见时间"是事故。
    /// · **Danger 不跟主题走**：红是"到点 / 关闭"的固定语义，两套主题里取同一个值，
    ///   免得深浅两档的"红"不是同一个红。
    /// </summary>
    private static WinPal PalFor(UiTheme t, Color4 penColor)
    {
        var danger = new Color4(0.86f, 0.22f, 0.25f, 1f);
        var accent = t.ActiveBg;
        var arc = Contrast(penColor, t.Panel) >= 3.0f ? penColor : accent;
        return new WinPal(
            Panel: t.Panel, Border: t.PanelBorder, Ink: t.Text, Muted: t.TextMuted,
            Soft: t.Hover, Accent: accent, AccentInk: t.ActiveText, Track: t.Hover,
            Danger: danger, DangerSoft: new Color4(danger.R, danger.G, danger.B, 0.12f),
            RingArc: arc, Corner: t.CornerRadius);
    }

    /// <summary>WCAG 相对亮度对比度（1:1 ～ 21:1）；alpha 一律当不透明看。</summary>
    private static float Contrast(Color4 a, Color4 b)
    {
        float la = RelLum(a), lb = RelLum(b);
        return (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    }

    private static float RelLum(Color4 c)
        => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);

    private static float Lin(float v)
        => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>主题给的投影层（绘制入口缓存一次，别每帧现算）。</summary>
    private UiShadowLayer[] _winShadow = Array.Empty<UiShadowLayer>();

    private readonly Dictionary<(int Px, bool Bold), IDWriteTextFormat> _winFormats = new();

    // =====================================================================
    //  通用小工具（卡片 / 按钮 / 名牌 / 环）
    // =====================================================================

    /// <summary>
    /// 卡片：面板底 + 1px 描边 + 圆角，下面垫主题给的投影层
    /// （<c>UiTheme.Shadow</c>，和「更多」面板同一个来源）。
    /// </summary>
    private void WinCard(in RectF r, float u, in WinPal p)
    {
        float rad = p.Corner * u;
        foreach (var s in _winShadow)
        {
            _scratch.Color = s.Color;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new RawRectF(r.MinX - s.Inflate * u, r.MinY + s.Dy * u - s.Inflate * u,
                             r.MaxX + s.Inflate * u, r.MaxY + s.Dy * u + s.Inflate * u), rad, rad), _scratch);
        }
        _scratch.Color = p.Panel;
        var box = new RoundedRectangle(new RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rad, rad);
        _ctx.FillRoundedRectangle(box, _scratch);
        _scratch.Color = p.Border;
        _ctx.DrawRoundedRectangle(box, _scratch, MathF.Max(1f, u));
    }

    /// <summary>圆角方钮（幽灵底）。窗口钮、步进钮、名片底都用它。</summary>
    private void WinGhostBtn(in RectF r, float u, in WinPal p, float radius = 10f)
    {
        float rad = MathF.Min(radius * u, (r.MaxY - r.MinY) * 0.5f);
        _scratch.Color = p.Soft;
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rad, rad), _scratch);
    }

    /// <summary>药丸主按钮（图标 + 文字）。计时窗的开始/暂停/重置、点名的抽奖都用它。</summary>
    private void WinPillBtn(in RectF r, float u, in WinPal p, string glyph, string label,
                             Color4 fill, Color4 ink)
    {
        float rad = (r.MaxY - r.MinY) * 0.5f;
        _scratch.Color = fill;
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rad, rad), _scratch);
        float cx = MidX(r), cy = MidY(r);
        if (glyph != null && label != null)
        {
            WinGlyph(glyph, cx - 34f * u, cy, 18f * u, ink, u);
            WinText(label, new RectF { MinX = cx - 34f * u, MinY = r.MinY, MaxX = r.MaxX, MaxY = r.MaxY },
                    17f * u, ink, true);
        }
        else if (glyph != null) WinGlyph(glyph, cx, cy, 18f * u, ink, u);
        else if (label != null) WinText(label, r, 17f * u, ink, true);
    }

    /// <summary>一个名字名牌。抽中的名字用强调色底。</summary>
    private void WinChip(in RectF r, float size, string name, in WinPal p)
    {
        float rad = (r.MaxY - r.MinY) * 0.5f;
        _scratch.Color = p.Accent;
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rad, rad), _scratch);
        WinText(name, r, size, p.AccentInk, true);
    }

    /// <summary>一行"图标 + 文字"（环下面那行说明用）。</summary>
    private void WinIconText(string glyph, in RectF r, float u, string text, Color4 ink)
    {
        float w = (text.Length * 13f + 30f) * u;
        float x = MidX(r) - w * 0.5f;
        WinGlyph(glyph, x + 8f * u, MidY(r), 14f * u, ink, u);
        WinText(text, new RectF { MinX = x + 20f * u, MinY = r.MinY, MaxX = x + w, MaxY = r.MaxY },
                14f * u, ink, false);
    }

    // =====================================================================
    //  计时窗
    // =====================================================================

    private void DrawTimerCard(InkEngine app)
    {
        if (!app.TimerCardOpen) return;
        var pal = PalFor(app.FloatingTheme, app.CurrentColor);
        _winShadow = app.FloatingTheme.Shadow ?? Array.Empty<UiShadowLayer>();

        float dpi = Dpi / 96f;
        var win = app.TimerCardRect();
        float u = app.TimerUnit();

        // 最小化：320×150，只剩剩余时间 + 底部拖动条（用户 2026-10-03 定）
        if (app.TimerMinimized)
        {
            WinCard(win, dpi, pal);
            WinText(app.TimerDisplayText(),
                    new RectF { MinX = win.MinX + 6f * dpi, MinY = win.MinY + 14f * dpi,
                                MaxX = win.MaxX - 6f * dpi, MaxY = win.MaxY - 44f * dpi },
                    52f * dpi, pal.Ink, true);
            var pill = TimerWin.MinimalPillRect(win, dpi);
            _scratch.Color = pal.Muted;
            float pr = (pill.MaxY - pill.MinY) * 0.5f;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new RawRectF(pill.MinX, pill.MinY, pill.MaxX, pill.MaxY), pr, pr), _scratch);
            return;
        }

        var card = TimerWin.CardRect(win, u, false, app.TimerExpanded);
        float lu = TimerWin.Layout(card, u);
        WinCard(card, lu, pal);

        // ---- 顶栏：左边分段（凹槽 + 选中药丸）----
        var trough = TimerWin.SegTroughRect(card, lu);
        float trr = (trough.MaxY - trough.MinY) * 0.5f;
        _scratch.Color = pal.Soft;
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new RawRectF(trough.MinX, trough.MinY, trough.MaxX, trough.MaxY), trr, trr), _scratch);
        for (int i = 0; i < 3; i++)
        {
            var r = TimerWin.TabRect(card, lu, i);
            bool on = (int)app.TimerKind == i;
            if (on)
            {
                float rr = (r.MaxY - r.MinY) * 0.5f;
                _scratch.Color = pal.Accent;
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rr, rr), _scratch);
            }
            WinText(TimerWin.ModeName((TimerMode)i), r, 14f * lu, on ? pal.AccentInk : pal.Muted, on);
            // 页签小圆点：该模式在跑（亮）/ 暂停（灰）——切走也继续跑（用户 2026-10-03 定）
            if (app.TimerTabRunning(i) || app.TimerTabPaused(i))
            {
                bool runDot = app.TimerTabRunning(i);
                _scratch.Color = runDot
                    ? (on ? pal.AccentInk : pal.Accent)
                    : (on ? pal.AccentInk : pal.Muted);
                _ctx.FillEllipse(new Ellipse(new Vector2(r.MaxX - 10f * lu, r.MinY + 10f * lu),
                                             3.5f * lu, 3.5f * lu), _scratch);
            }
        }

        // ---- 顶栏：右上角三个窗口钮（收起 / 全屏 / 关闭）----
        foreach (var (zone, glyph) in new[]
        {
            (TimerZone.Minimize, "chevdown"), (TimerZone.Fullscreen, "max"), (TimerZone.Close, "close"),
        })
        {
            var r = TimerWin.BtnRect(card, lu, zone);
            WinGhostBtn(r, lu, pal, 8f);
            if (zone == TimerZone.Close)
            {
                // 关闭钮只加一层很淡的红底 + 红字：认得出来，又不会在投影上砸出一个红疙瘩。
                _scratch.Color = pal.DangerSoft;
                float rr = 8f * lu;
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rr, rr), _scratch);
                WinGlyph(glyph, MidX(r), MidY(r), 15f * lu, pal.Danger, lu);
            }
            else WinGlyph(glyph, MidX(r), MidY(r), 15f * lu, pal.Muted, lu);
        }

        bool finished = app.TimerFinished;
        var bigInk = finished ? pal.Danger : pal.Ink;

        if (app.TimerSettingsOpen)
        {
            // 改时长态：三列（时 / 分 / 秒），每列 2×2 的 ±5 / ±1，最后一条整宽「确定」。
            for (int unit = 0; unit < 3; unit++)
            {
                WinText(TimerWin.UnitName(unit), TimerWin.StepLabelRect(card, lu, unit),
                        14f * lu, pal.Muted, false);
                for (int pair = 0; pair < 2; pair++)
                    for (int row = 0; row < 2; row++)
                    {
                        var r = TimerWin.StepRect(card, lu, unit, pair, row);
                        WinGhostBtn(r, lu, pal, 8f);
                        string label = (pair == 0 ? "+" : "−") + (row == 0 ? "5" : "1");
                        WinText(label, r, 15f * lu, pal.Ink, true);
                    }
            }
            WinText($"{app.TimerSetH:00}:{app.TimerSetM:00}:{app.TimerSetS:00}",
                    TimerWin.EditValueRect(card, lu), 72f * lu, bigInk, true);
            WinPillBtn(TimerWin.OkRect(card, lu), lu, pal, "check", "确定", pal.Accent, pal.AccentInk);
        }
        else
        {
            // 环 + 压在上面的大字
            WinRing(TimerWin.CX(card), TimerWin.RingCY(card, lu), TimerWin.RingR(lu),
                    TimerWin.RingTh(lu), app.TimerRingFraction(),
                    finished ? pal.Danger : pal.RingArc, pal.Track);
            WinText(app.TimerDisplayText(), TimerWin.ValueRect(card, lu), 76f * lu, bigInk, true);

            // 环下面一行**纯文字**（原来是个灰药丸——那是 InkClass 的签名形状）
            if (app.TimerKind == TimerMode.Countdown && app.TimerActive && !finished)
                WinIconText("clock", TimerWin.PillRect(card, lu), lu, app.TimerEndText(), pal.Muted);
            else if (app.TimerKind == TimerMode.Countdown && finished)
                WinText("时间到", TimerWin.PillRect(card, lu), 17f * lu, pal.Danger, true);
            else if (app.TimerKind == TimerMode.Stopwatch)
                WinText("秒表", TimerWin.PillRect(card, lu), 15f * lu, pal.Muted, false);
            else if (app.TimerKind == TimerMode.CountUp && app.TimerActive)
                WinIconText("clock", TimerWin.PillRect(card, lu), lu, "正在计时", pal.Muted);

            // ---- 底排：开始 / 暂停 ＋ 重置（带文字，投影上远处也读得出来）----
            bool running = app.TimerActive && !app.TimerPaused && !finished;
            WinPillBtn(TimerWin.BtnRect(card, lu, TimerZone.Start), lu, pal,
                       running ? "pause" : "play",
                       running ? "暂停" : finished ? "再来一次" : app.TimerActive ? "继续" : "开始",
                       pal.Accent, pal.AccentInk);
            bool resetOn = app.TimerActive || finished;
            WinPillBtn(TimerWin.BtnRect(card, lu, TimerZone.Reset), lu, pal, "sync", "重置",
                       pal.Soft, resetOn ? pal.Ink : pal.Muted);
        }
    }

    // =====================================================================
    //  点名窗
    // =====================================================================

    private void DrawRollCard(InkEngine app)
    {
        if (!app.RollCardOpen) return;
        var pal = PalFor(app.FloatingTheme, app.CurrentColor);
        _winShadow = app.FloatingTheme.Shadow ?? Array.Empty<UiShadowLayer>();

        var card = app.RollCardRect();
        float u = app.RollUnit();
        WinCard(card, u, pal);

        bool rolling = app.RollingNow;

        // ---- 顶栏：右上角一个 ✕ ----
        var close = RollWin.CloseRect(card, u);
        WinGhostBtn(close, u, pal, 8f);
        WinGlyph("close", MidX(close), MidY(close), 15f * u, pal.Muted, u);

        // ---- 名牌区 ----
        var area = RollWin.ResultsRect(card, u);
        if (rolling)
        {
            // 滚动中：正在滚的那个名字**单独放大**、在结果区里居中——最后一眼要看的就是它。
            float fs = MathF.Min(76f * u, (area.MaxY - area.MinY) * 0.52f);
            float h = fs + 30f * u;
            var box = new RectF { MinX = area.MinX, MinY = (area.MinY + area.MaxY - h) * 0.5f,
                                  MaxX = area.MaxX, MaxY = (area.MinY + area.MaxY - h) * 0.5f + h };
            WinText(app.RollFaceNow, box, fs, pal.Accent, true);
        }
        else if (app.RollResultNow.Length > 0)
        {
            var chips = RollWin.Chips(area, app.RollResultNow, u);
            float fs = RollWin.ChipFontSize(area, app.RollResultNow.Length, u);
            foreach (var c in chips) WinChip(c.R, fs, c.Name, pal);
        }
        else
        {
            WinText("按「抽奖」开始", area, 20f * u, pal.Muted, false);
        }

        // ---- 控制行：人数步进（左）＋ 抽奖（右）----
        var minus = RollWin.MinusRect(card, u);
        WinGhostBtn(minus, u, pal, 10f);
        WinGlyph("minus", MidX(minus), MidY(minus), 16f * u, rolling ? pal.Muted : pal.Ink, u);
        var plus = RollWin.PlusRect(card, u);
        WinGhostBtn(plus, u, pal, 10f);
        WinGlyph("plus", MidX(plus), MidY(plus), 16f * u, rolling ? pal.Muted : pal.Ink, u);
        WinText(app.RollCountNow.ToString(), RollWin.CountRect(card, u), 34f * u,
                rolling ? pal.Muted : pal.Ink, true);

        WinPillBtn(RollWin.DrawRect(card, u), u, pal, rolling ? null : "person",
                   rolling ? "滚动中…" : "抽奖",
                   rolling ? pal.Soft : pal.Accent, rolling ? pal.Muted : pal.AccentInk);

        // ---- 信息行：不重复池 ----
        WinText($"不重复池：{app.RollDrawnNow} / {app.RollTotalNow}",
                RollWin.PoolTextRect(card, u), 14f * u, pal.Muted, false);
        WinText("重置", RollWin.PoolResetRect(card, u), 14f * u, pal.Accent, true);

        // ---- 底行：名单 / 学号范围 ----
        var names = RollWin.NamesRect(card, u);
        float nr = (names.MaxY - names.MinY) * 0.5f;
        _scratch.Color = pal.Soft;
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new RawRectF(names.MinX, names.MinY, names.MaxX, names.MaxY), nr, nr), _scratch);
        WinText(app.RollHasNames ? $"名单：{app.Names.Length} 人" : "未导入名单",
                names, 14f * u, pal.Ink, false);
        var rel = RollWin.ReloadRect(card, u);
        WinGhostBtn(rel, u, pal, 8f);
        WinGlyph("sync", MidX(rel), MidY(rel), 15f * u, pal.Muted, u);

        if (!app.RollHasNames)
        {
            // 没有名单文件时，底行右侧给"学号范围 1–N"（有名单则加减本来也不生效，不画）
            WinText("学号范围", RollWin.NumLabelRect(card, u), 12f * u, pal.Muted, false);
            WinText($"1 – {app.RollMaxNumNow}", RollWin.NumTextRect(card, u), 20f * u, pal.Ink, true);
            var nm = RollWin.NumMinusRect(card, u);
            WinGhostBtn(nm, u, pal, 8f);
            WinGlyph("minus", MidX(nm), MidY(nm), 13f * u, pal.Ink, u);
            var np = RollWin.NumPlusRect(card, u);
            WinGhostBtn(np, u, pal, 8f);
            WinGlyph("plus", MidX(np), MidY(np), 13f * u, pal.Ink, u);
        }
    }

    // =====================================================================
    //  绘制小工具
    // =====================================================================

    private static float MidX(in RectF r) => (r.MinX + r.MaxX) * 0.5f;
    private static float MidY(in RectF r) => (r.MinY + r.MaxY) * 0.5f;

    private IDWriteTextFormat WinFmt(float px, bool bold = false)
    {
        int kpx = Math.Max(8, (int)MathF.Round(px));
        var key = (Px: kpx, Bold: bold);
        if (!_winFormats.TryGetValue(key, out var f))
        {
            f = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                bold ? FontWeight.Bold : FontWeight.SemiBold,
                FontStyle.Normal, FontStretch.Normal, kpx, "zh-CN");
            f.TextAlignment = TextAlignment.Center;
            f.ParagraphAlignment = ParagraphAlignment.Center;
            _winFormats[key] = f;
        }
        return f;
    }

    private void WinText(string s, in RectF r, float px, Color4 col, bool bold = false)
    {
        _scratch.Color = col;
        _ctx.DrawText(s, WinFmt(px, bold), new Rect(r.MinX, r.MinY, r.MaxX - r.MinX, r.MaxY - r.MinY), _scratch);
    }

    /// <summary>环形进度：底环 + 从 12 点顺时针的进度弧（用短线段拼，D2D DrawArc 签名不稳）。</summary>
    private void WinRing(float cx, float cy, float r, float th, float frac, Color4 arc, Color4 track)
    {
        _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), SetBrush(track), th);
        if (frac <= 0.001f) return;
        WinArc(cx, cy, r, 0f, 360f * Math.Min(frac, 1f), th, arc);
    }

    private void WinArc(float cx, float cy, float r, float a0, float a1, float th, Color4 col)
    {
        _scratch.Color = col;
        int steps = Math.Max(2, (int)(MathF.Abs(a1 - a0) / 4f));
        float span = a1 - a0;
        float dir = span >= 0 ? 1f : -1f;
        // 相邻线段端点各外扩半个线宽对应的角度：不重叠的话，外沿会露出一个个"刻度豁口"。
        float over = r > 1f ? (th * 0.5f / r) * 180f / MathF.PI * 1.2f : 0f;
        Vector2 Pt(float deg)
        {
            float rad = (deg - 90f) * MathF.PI / 180f;
            return new Vector2(cx + r * MathF.Cos(rad), cy + r * MathF.Sin(rad));
        }
        for (int i = 0; i < steps; i++)
        {
            float s0 = a0 + span * i / steps - over * dir;
            float s1 = a0 + span * (i + 1) / steps + over * dir;
            _ctx.DrawLine(Pt(s0), Pt(s1), _scratch, th);
        }
        _ctx.FillEllipse(new Ellipse(Pt(a0), th * 0.5f, th * 0.5f), _scratch);
        _ctx.FillEllipse(new Ellipse(Pt(a1), th * 0.5f, th * 0.5f), _scratch);
    }

    private void WinGlyph(string glyph, float cx, float cy, float s, Color4 ink, float u)
    {
        _scratch.Color = ink;
        float th = MathF.Max(1.2f, s * 0.13f);
        switch (glyph)
        {
            case "play":
            {
                using var path = Gfx.D2DFactory.CreatePathGeometry();
                using (var sink = path.Open())
                {
                    sink.BeginFigure(new Vector2(cx - s * 0.32f, cy - s * 0.46f), FigureBegin.Filled);
                    sink.AddLine(new Vector2(cx - s * 0.32f, cy + s * 0.46f));
                    sink.AddLine(new Vector2(cx + s * 0.50f, cy));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }
                _ctx.FillGeometry(path, _scratch);
                break;
            }
            case "pause":
            {
                float w = s * 0.24f, h = s * 0.92f, gap = s * 0.26f, rr = w * 0.5f;
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new RawRectF(cx - gap - w, cy - h * 0.5f, cx - gap, cy + h * 0.5f), rr, rr), _scratch);
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new RawRectF(cx + gap, cy - h * 0.5f, cx + gap + w, cy + h * 0.5f), rr, rr), _scratch);
                break;
            }
            case "plus":
            case "minus":
                _ctx.DrawLine(new Vector2(cx - s * 0.40f, cy), new Vector2(cx + s * 0.40f, cy), _scratch, th);
                if (glyph == "plus")
                    _ctx.DrawLine(new Vector2(cx, cy - s * 0.40f), new Vector2(cx, cy + s * 0.40f), _scratch, th);
                break;
            case "clock":
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), s * 0.44f, s * 0.44f), _scratch, th);
                _ctx.DrawLine(new Vector2(cx, cy - s * 0.20f), new Vector2(cx, cy + s * 0.03f), _scratch, th * 0.9f);
                _ctx.DrawLine(new Vector2(cx, cy + s * 0.03f), new Vector2(cx + s * 0.19f, cy + s * 0.13f), _scratch, th * 0.9f);
                break;
            case "person":
                _ctx.FillEllipse(new Ellipse(new Vector2(cx, cy - s * 0.22f), s * 0.22f, s * 0.22f), _scratch);
                WinArc(cx, cy - s * 0.02f, s * 0.50f, 150f, 210f, MathF.Max(1.4f, s * 0.18f), ink);
                break;
            case "sync":
            {
                WinArc(cx, cy, s * 0.42f, -55f, 235f, MathF.Max(1.6f, s * 0.15f), ink);
                float rad = (235f - 90f) * MathF.PI / 180f;
                var tip = new Vector2(cx + s * 0.42f * MathF.Cos(rad), cy + s * 0.42f * MathF.Sin(rad));
                _ctx.FillEllipse(new Ellipse(tip, s * 0.10f, s * 0.10f), _scratch);
                break;
            }
            case "chevdown":
                _ctx.DrawLine(new Vector2(cx - s * 0.38f, cy - s * 0.14f), new Vector2(cx, cy + s * 0.24f), _scratch, th);
                _ctx.DrawLine(new Vector2(cx, cy + s * 0.24f), new Vector2(cx + s * 0.38f, cy - s * 0.14f), _scratch, th);
                break;
            case "max":
            {
                float e = s * 0.42f, ln = s * 0.20f;
                _ctx.DrawLine(new Vector2(cx - e, cy - e + ln), new Vector2(cx - e, cy - e), _scratch, th);
                _ctx.DrawLine(new Vector2(cx - e, cy - e), new Vector2(cx - e + ln, cy - e), _scratch, th);
                _ctx.DrawLine(new Vector2(cx + e - ln, cy - e), new Vector2(cx + e, cy - e), _scratch, th);
                _ctx.DrawLine(new Vector2(cx + e, cy - e), new Vector2(cx + e, cy - e + ln), _scratch, th);
                _ctx.DrawLine(new Vector2(cx + e, cy + e - ln), new Vector2(cx + e, cy + e), _scratch, th);
                _ctx.DrawLine(new Vector2(cx + e, cy + e), new Vector2(cx + e - ln, cy + e), _scratch, th);
                _ctx.DrawLine(new Vector2(cx - e + ln, cy + e), new Vector2(cx - e, cy + e), _scratch, th);
                _ctx.DrawLine(new Vector2(cx - e, cy + e), new Vector2(cx - e, cy + e - ln), _scratch, th);
                break;
            }
            case "close":
            {
                float e = s * 0.32f;
                _ctx.DrawLine(new Vector2(cx - e, cy - e), new Vector2(cx + e, cy + e), _scratch, th);
                _ctx.DrawLine(new Vector2(cx - e, cy + e), new Vector2(cx + e, cy - e), _scratch, th);
                break;
            }
            case "check":
            {
                _ctx.DrawLine(new Vector2(cx - s * 0.36f, cy + s * 0.02f),
                              new Vector2(cx - s * 0.10f, cy + s * 0.28f), _scratch, th);
                _ctx.DrawLine(new Vector2(cx - s * 0.10f, cy + s * 0.28f),
                              new Vector2(cx + s * 0.38f, cy - s * 0.28f), _scratch, th);
                break;
            }
        }
    }
}
