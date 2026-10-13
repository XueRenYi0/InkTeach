using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 课堂窗（计时/点名）的绘制：1:1 复刻 InkClass 的浅色面板版式
/// （CountdownTimerWindow / RandWindow，GPL-3.0）。
/// 与 Overlay.cs 同属 OverlayWindow（partial）；配色原值：
/// 面板 #F0F3F9、描边 #0066BF、关闭 #E32A34、次级白钮 #FBFBFD、
/// 药丸 #E8EAF0、数字待机 #5B5D5F、灰罩 #BFBFBF。
/// </summary>
internal sealed partial class OverlayWindow
{
    // ── InkClass 课堂窗配色（源码原值） ────────────────────────────────
    static readonly Color4 WinPanel = new(0xF0 / 255f, 0xF3 / 255f, 0xF9 / 255f, 1f);
    static readonly Color4 WinBorder = new(0x00 / 255f, 0x66 / 255f, 0xBF / 255f, 1f);
    static readonly Color4 WinAccent = new(0x00 / 255f, 0x66 / 255f, 0xBF / 255f, 1f);
    static readonly Color4 WinDanger = new(0xE3 / 255f, 0x2A / 255f, 0x34 / 255f, 1f);
    static readonly Color4 WinLightBtn = new(0xFB / 255f, 0xFB / 255f, 0xFD / 255f, 1f);
    static readonly Color4 WinDisableBg = new(0xF3 / 255f, 0xF5 / 255f, 0xF9 / 255f, 1f);
    static readonly Color4 WinDisableInk = new(0x9D / 255f, 0x9D / 255f, 0x9E / 255f, 1f);
    static readonly Color4 WinPillBg = new(0xE8 / 255f, 0xEA / 255f, 0xF0 / 255f, 1f);
    static readonly Color4 WinDigitIdle = new(0x5B / 255f, 0x5D / 255f, 0x5F / 255f, 1f);
    static readonly Color4 WinCover = new(0xBF / 255f, 0xBF / 255f, 0xBF / 255f, 1f);
    static readonly Color4 WinMuted = new(0x7C / 255f, 0x82 / 255f, 0x8C / 255f, 1f);
    static readonly Color4 WinInk = new(0x1A / 255f, 0x1D / 255f, 0x22 / 255f, 1f);
    static readonly Color4 WinTrack = new(0xDF / 255f, 0xE4 / 255f, 0xEC / 255f, 1f);
    static readonly Color4 WinLine = new(0xD8 / 255f, 0xDD / 255f, 0xE6 / 255f, 1f);
    static readonly Color4 WinWhite = new(1f, 1f, 1f, 1f);
    static readonly Color4 WinShadow = new(0f, 0f, 0f, 0.16f);

    private readonly Dictionary<(int Px, bool Bold), IDWriteTextFormat> _winFormats = new();

    // =====================================================================
    //  计时窗
    // =====================================================================

    private void DrawTimerCard(InkEngine app)
    {
        if (!app.TimerCardOpen) return;
        float dpi = Dpi / 96f;
        var win = app.TimerCardRect();
        float u = app.TimerUnit();

        // 最小化：320×150，只剩剩余时间 + 底部拖动条（比 InkClass 的 400×250 更紧凑，用户 2026-10-03 定）
        if (app.TimerMinimized)
        {
            WinCardRect(win, dpi, 10f * dpi);
            WinText(app.TimerDisplayText(),
                    new RectF { MinX = win.MinX + 6f * dpi, MinY = win.MinY + 14f * dpi,
                                MaxX = win.MaxX - 6f * dpi, MaxY = win.MaxY - 44f * dpi },
                    52f * dpi, WinInk, true);
            var pill = TimerWin.MinimalPillRect(win, dpi);
            _scratch.Color = new Color4(0.53f, 0.53f, 0.53f, 1f);
            float pr = (pill.MaxY - pill.MinY) * 0.5f;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(pill.MinX, pill.MinY, pill.MaxX, pill.MaxY), pr, pr), _scratch);
            return;
        }

        var card = TimerWin.CardRect(win, u, false, app.TimerExpanded);
        float lu = TimerWin.Layout(card, u);
        WinCardRect(card, lu, app.TimerExpanded ? 0f : 10f * lu);

        // 三模式页签
        for (int i = 0; i < 3; i++)
        {
            var r = TimerWin.TabRect(card, lu, i);
            bool on = (int)app.TimerKind == i;
            float rr = (r.MaxY - r.MinY) * 0.5f;
            var box = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rr, rr);
            if (on)
            {
                _scratch.Color = WinAccent;
                _ctx.FillRoundedRectangle(box, _scratch);
            }
            else
            {
                _scratch.Color = new Color4(1f, 1f, 1f, 0.62f);
                _ctx.FillRoundedRectangle(box, _scratch);
                _scratch.Color = WinLine;
                _ctx.DrawRoundedRectangle(box, _scratch, 1f * lu);
            }
            WinText(TimerWin.ModeName((TimerMode)i), r, 14f * lu, on ? WinWhite : WinDigitIdle, on);
            // 页签小圆点：该模式在跑（亮）/ 暂停（灰）——切走也继续跑（用户 2026-10-03 定）
            if (app.TimerTabRunning(i) || app.TimerTabPaused(i))
            {
                bool runDot = app.TimerTabRunning(i);
                _scratch.Color = runDot
                    ? (on ? WinWhite : WinAccent)
                    : (on ? new Color4(1f, 1f, 1f, 0.65f) : WinMuted);
                _ctx.FillEllipse(new Ellipse(new Vector2(r.MaxX - 11f * lu, r.MinY + 11f * lu), 3.5f * lu, 3.5f * lu), _scratch);
            }
        }

        if (app.TimerSettingsOpen)
        {
            // 改时长态：时/分/秒各自 ±5 / ±1 + ✓
            for (int unit = 0; unit < 3; unit++)
                for (int pair = 0; pair < 2; pair++)
                    for (int row = 0; row < 2; row++)
                    {
                        var r = TimerWin.StepRect(card, lu, unit, pair, row);
                        float rr = 6f * lu;
                        var box = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rr, rr);
                        _scratch.Color = WinWhite;
                        _ctx.FillRoundedRectangle(box, _scratch);
                        _scratch.Color = WinLine;
                        _ctx.DrawRoundedRectangle(box, _scratch, 1f * lu);
                        string label = (pair == 0 ? "+" : "−") + (row == 0 ? "5" : "1");
                        WinText(label, r, 12.5f * lu, WinInk, true);
                    }
            WinText($"{app.TimerSetH:00}:{app.TimerSetM:00}:{app.TimerSetS:00}",
                    TimerWin.EditValueRect(card, lu), 66f * lu, WinInk, true);
            var ok = TimerWin.OkRect(card, lu);
            WinBtn(MidX(ok), MidY(ok), Wid(ok), WinAccent, WinWhite, "check", lu);
        }
        else
        {
            // 环 + 压在上面的大字
            WinRing(TimerWin.CX(card), TimerWin.RingCY(card, lu), TimerWin.RingR(lu), TimerWin.RingTh(lu),
                    app.TimerRingFraction());
            WinText(app.TimerDisplayText(), TimerWin.ValueRect(card, lu), 66f * lu, WinInk, true);

            // 药丸：跑着=预计结束时刻；到点=红「时间到」
            if (app.TimerKind == TimerMode.Countdown && app.TimerActive && !app.TimerFinished)
                WinPill(TimerWin.PillRect(card, lu), app.TimerEndText(), lu, WinPillBg, WinInk, "clock");
            else if (app.TimerKind == TimerMode.Countdown && app.TimerFinished)
                WinPill(TimerWin.PillRect(card, lu), "时间到", lu, WinDanger, WinWhite, null);
        }

        // 底部：开始/暂停、重置、最小化、全屏、关闭
        bool running = app.TimerActive && !app.TimerPaused && !app.TimerFinished;
        var startR = TimerWin.BtnRect(card, lu, TimerZone.Start);
        WinBtn(MidX(startR), MidY(startR), Wid(startR), WinAccent, WinWhite, running ? "pause" : "play", lu);
        var resetR = TimerWin.BtnRect(card, lu, TimerZone.Reset);
        bool resetOn = app.TimerActive || app.TimerFinished;
        WinBtn(MidX(resetR), MidY(resetR), Wid(resetR), resetOn ? WinLightBtn : WinDisableBg,
               resetOn ? WinInk : WinDisableInk, "sync", lu);
        var minR = TimerWin.BtnRect(card, lu, TimerZone.Minimize);
        WinBtn(MidX(minR), MidY(minR), Wid(minR), WinLightBtn, WinInk, "chevdown", lu);
        var fsR = TimerWin.BtnRect(card, lu, TimerZone.Fullscreen);
        WinBtn(MidX(fsR), MidY(fsR), Wid(fsR), WinLightBtn, WinInk, "max", lu);
        var closeR = TimerWin.BtnRect(card, lu, TimerZone.Close);
        WinBtn(MidX(closeR), MidY(closeR), Wid(closeR), WinDanger, WinWhite, "close", lu);
    }

    // =====================================================================
    //  点名窗
    // =====================================================================

    private void DrawRollCard(InkEngine app)
    {
        if (!app.RollCardOpen) return;
        var card = app.RollCardRect();
        float u = app.RollUnit();
        WinCardRect(card, u, 10f * u);

        bool rolling = app.RollingNow;

        // 左栏：滚动预览 / 结果（1/2/3 列） / 待抽
        if (rolling)
        {
            var area = RollWin.ResultsRect(card, u);
            WinText(app.RollFaceNow, area, MathF.Min(64f * u, (area.MaxY - area.MinY) * 0.38f), WinInk, true);
        }
        else if (app.RollResultNow.Length > 0)
        {
            var res = app.RollResultNow;
            int n = res.Length;
            int cols = RollWin.Cols(n);
            int rows = (n + cols - 1) / cols;
            var area = RollWin.ResultsRect(card, u);
            float px = MathF.Min(70f * u, (area.MaxY - area.MinY) / (rows * 1.45f));
            for (int ci = 0; ci < cols; ci++)
            {
                int from = ci * n / cols;
                int to = (ci + 1) * n / cols;
                if (to <= from) continue;
                var sb = new StringBuilder();
                for (int i = from; i < to; i++)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(res[i]);
                }
                WinText(sb.ToString(), RollWin.ColRect(card, u, cols, ci), px, WinInk, true);
            }
        }
        else
        {
            WinText("点「抽奖」开始", RollWin.ResultsRect(card, u), 22f * u, WinMuted, false);
        }

        // 右栏：人数 [-  3  +]
        var minus = RollWin.MinusRect(card, u);
        WinBtn(MidX(minus), MidY(minus), Wid(minus),
               rolling ? WinDisableBg : WinLightBtn, rolling ? WinDisableInk : WinInk, "minus", u);
        var plus = RollWin.PlusRect(card, u);
        WinBtn(MidX(plus), MidY(plus), Wid(plus),
               rolling ? WinDisableBg : WinLightBtn, rolling ? WinDisableInk : WinInk, "plus", u);
        WinText(app.RollCountNow.ToString(), RollWin.CountRect(card, u), 44f * u,
                rolling ? WinDisableInk : WinInk, true);

        // 抽奖大按钮（滚动时灰罩，InkClass 同款）
        var dr = RollWin.DrawRect(card, u);
        var drr = new RoundedRectangle(new Vortice.RawRectF(dr.MinX, dr.MinY, dr.MaxX, dr.MaxY), 10f * u, 10f * u);
        _scratch.Color = rolling ? WinCover : WinAccent;
        _ctx.FillRoundedRectangle(drr, _scratch);
        float bx = MidX(dr);
        WinGlyph("person", bx - 42f * u, MidY(dr), 20f * u, WinWhite, u);
        WinText("抽奖", new RectF { MinX = bx - 10f * u, MinY = dr.MinY, MaxX = dr.MaxX - 14f * u, MaxY = dr.MaxY },
                28f * u, WinWhite, true);

        // 不重复池：已抽 / 总数 + 重置
        WinText($"不重复池：{app.RollDrawnNow} / {app.RollTotalNow}",
                RollWin.PoolTextRect(card, u), 12f * u, WinMuted, false);
        WinText("重置", RollWin.PoolResetRect(card, u), 12f * u, WinAccent, true);

        // 无名单：学号范围 1–N 可调（有名单则显示名单人数，加减不生效）
        if (app.RollHasNames)
        {
            WinText($"名单 {app.Names.Length} 人", RollWin.NumTextRect(card, u), 14f * u, WinMuted, false);
        }
        else
        {
            WinText("学号范围", RollWin.NumLabelRect(card, u), 11f * u, WinMuted, false);
            WinText($"1 – {app.RollMaxNumNow}", RollWin.NumTextRect(card, u), 16f * u, WinInk, true);
            var nm = RollWin.NumMinusRect(card, u);
            WinBtn(MidX(nm), MidY(nm), Wid(nm), WinLightBtn, WinInk, "minus", u);
            var np = RollWin.NumPlusRect(card, u);
            WinBtn(MidX(np), MidY(np), Wid(np), WinLightBtn, WinInk, "plus", u);
        }

        // 底部：名单药丸 + 重读 + 关闭
        var names = RollWin.NamesRect(card, u);
        float nr = (names.MaxY - names.MinY) * 0.5f;
        var nbox = new RoundedRectangle(new Vortice.RawRectF(names.MinX, names.MinY, names.MaxX, names.MaxY), nr, nr);
        _scratch.Color = WinLightBtn;
        _ctx.FillRoundedRectangle(nbox, _scratch);
        _scratch.Color = WinLine;
        _ctx.DrawRoundedRectangle(nbox, _scratch, 1f * u);
        string nt = app.RollHasNames ? $"名单：{app.Names.Length} 人" : "未导入名单";
        WinText(nt, names, 14f * u, WinInk, false);
        var rel = RollWin.ReloadRect(card, u);
        WinBtn(MidX(rel), MidY(rel), Wid(rel), WinLightBtn, WinInk, "sync", u);
        var cl = RollWin.CloseRect(card, u);
        WinBtn(MidX(cl), MidY(cl), Wid(cl), WinDanger, WinWhite, "close", u);
    }

    // =====================================================================
    //  绘制小工具
    // =====================================================================

    private static float MidX(in RectF r) => (r.MinX + r.MaxX) * 0.5f;
    private static float MidY(in RectF r) => (r.MinY + r.MaxY) * 0.5f;
    private static float Wid(in RectF r) => r.MaxX - r.MinX;

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

    /// <summary>浅色卡片：投影 + #F0F3F9 底 + 1px #0066BF 描边 + 圆角。</summary>
    private void WinCardRect(in RectF r, float u, float radius)
    {
        _scratch.Color = WinShadow;
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new Vortice.RawRectF(r.MinX, r.MinY + 6f * u, r.MaxX, r.MaxY + 6f * u), radius, radius), _scratch);
        _scratch.Color = WinPanel;
        var box = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), radius, radius);
        _ctx.FillRoundedRectangle(box, _scratch);
        _scratch.Color = WinBorder;
        _ctx.DrawRoundedRectangle(box, _scratch, 1f * u);
    }

    private void WinBtn(float cx, float cy, float d, Color4 fill, Color4 ink, string glyph, float u)
    {
        _scratch.Color = new Color4(0f, 0f, 0f, 0.10f);
        _ctx.FillEllipse(new Ellipse(new Vector2(cx, cy + 1.5f * u), d * 0.5f, d * 0.5f), _scratch);
        _scratch.Color = fill;
        _ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), d * 0.5f, d * 0.5f), _scratch);
        WinGlyph(glyph, cx, cy, d * 0.52f, ink, u);
    }

    private void WinPill(in RectF r, string text, float u, Color4 fill, Color4 ink, string icon)
    {
        float rr = (r.MaxY - r.MinY) * 0.5f;
        _scratch.Color = fill;
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), rr, rr), _scratch);
        float tx = r.MinX + 6f * u;
        if (icon != null)
        {
            WinGlyph(icon, r.MinX + 22f * u, MidY(r), 16f * u, ink, u);
            tx = r.MinX + 34f * u;
        }
        WinText(text, new RectF { MinX = tx, MinY = r.MinY, MaxX = r.MaxX - 6f * u, MaxY = r.MaxY },
                14f * u, ink, true);
    }

    /// <summary>环形进度：底环 + 从 12 点顺时针的进度弧（用短线段拼，D2D DrawArc 签名不稳）。</summary>
    private void WinRing(float cx, float cy, float r, float th, float frac)
    {
        _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), SetBrush(WinTrack), th);
        if (frac <= 0.001f) return;
        WinArc(cx, cy, r, 0f, 360f * Math.Min(frac, 1f), th, WinAccent);
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
                    new Vortice.RawRectF(cx - gap - w, cy - h * 0.5f, cx - gap, cy + h * 0.5f), rr, rr), _scratch);
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new Vortice.RawRectF(cx + gap, cy - h * 0.5f, cx + gap + w, cy + h * 0.5f), rr, rr), _scratch);
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
                _ctx.DrawLine(new Vector2(cx - s * 0.34f, cy - s * 0.34f), new Vector2(cx + s * 0.34f, cy + s * 0.34f), _scratch, th);
                _ctx.DrawLine(new Vector2(cx + s * 0.34f, cy - s * 0.34f), new Vector2(cx - s * 0.34f, cy + s * 0.34f), _scratch, th);
                break;
            case "check":
                _ctx.DrawLine(new Vector2(cx - s * 0.38f, cy + s * 0.04f), new Vector2(cx - s * 0.10f, cy + s * 0.32f), _scratch, th);
                _ctx.DrawLine(new Vector2(cx - s * 0.10f, cy + s * 0.32f), new Vector2(cx + s * 0.42f, cy - s * 0.30f), _scratch, th);
                break;
        }
    }
}
