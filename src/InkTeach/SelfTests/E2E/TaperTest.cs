// [职责] 收笔笔锋（速度尖尾）E2E：速度门控 / 尾长 / 上屏宽窄 / 开关往返。
// [测试] --tapertest

using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    private int _taperPass, _taperFail;

    private void TaperCheck(string name, bool ok, string detail)
    {
        if (ok) _taperPass++; else _taperFail++;
        Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
    }

    /// <summary>构造一条匀速无压感直线：每段 step px、dt = step / speed，末段速度恒 = speed。</summary>
    private static Stroke TaperMakeLine(float speed, float step, int segments, float width)
    {
        var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = width };
        double t = 0;
        for (int i = 0; i <= segments; i++)
        {
            s.AddPoint(i * step, 0f, 0.5f, t);
            t += step / speed;
        }
        return s;
    }

    /// <summary>系数 &lt; 1 的那一段弧长（尾长），用于比较"尾长随速度变化"。</summary>
    private static float TaperArc(Stroke s)
    {
        var f = StrokeTaper.Factors(s);
        if (f == null) return 0f;
        var pts = s.Points;
        float arc = 0f;
        for (int i = pts.Count - 1; i >= 1; i--)
        {
            float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            arc += MathF.Sqrt(dx * dx + dy * dy);
            if (f[i - 1] >= 0.999f) break;
        }
        return arc;
    }

    /// <summary>
    /// 收笔笔锋（速度尖尾）自检（`--tapertest`）。
    /// 纯函数部分不需要真笔/屏幕；屏幕部分用鼠标与合成笔各画快/慢一组，量末端 vs 中段宽度。
    /// </summary>
    private void TaperTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 收笔笔锋自检（速度尖尾）===");
        Console.WriteLine($"  变宽通道: {OverlayWindow.InkNote}");
        _taperPass = 0;
        _taperFail = 0;

        TaperCheckGating();
        TaperCheckShape();
        TaperCheckScreen();

        Console.WriteLine();
        Console.WriteLine($"  合计 {_taperPass + _taperFail} 项：通过 {_taperPass}，失败 {_taperFail}");
        if (_taperFail > 0) ExitCode = 1;
        _quit = true;
    }

    /// <summary>① 时间不可用 ② 慢速等宽 ③ 快速收尖 ⑥ 跨停顿 ⑧ 慢速尾长更短。</summary>
    private void TaperCheckGating()
    {
        const float W = 6f;

        // ① T 全同 → 不加笔锋（时间不可用 → 全 1）
        {
            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = W };
            for (int i = 0; i <= 8; i++) s.AddPoint(i * 10f, 0f, 0.5f, 1000.0);
            TaperCheck("T 全同：不加笔锋（系数全 1）", StrokeTaper.Factors(s) == null,
                       $"Factors = {(StrokeTaper.Factors(s) == null ? "null（全 1）" : "非空")}");
        }

        // ② 慢速直线 → 系数全 1
        {
            var slow = TaperMakeLine(0.2f, 4f, 30, W);   // 0.2 px/ms < VMin
            TaperCheck("慢速直线：系数全 1", StrokeTaper.Factors(slow) == null,
                       $"EndSpeed={StrokeTaper.EndSpeed(slow):F2} px/ms（VMin={StrokeTaper.VMin}）");
        }

        // ③ 快速直线 → 末点≈尖端下限、中段=1、单调不增
        {
            var fast = TaperMakeLine(3f, 10f, 20, W);    // 3 px/ms > VMax，长 200px
            var f = StrokeTaper.Factors(fast);
            bool mid = true;
            for (int i = 5; i <= 15; i++) if (f[i] < 0.999f) mid = false;
            bool mono = true;
            for (int i = 1; i < f.Length; i++) if (f[i] > f[i - 1] + 1e-5f) mono = false;
            float tipFloor = StrokeTaper.TipFloorFactor(W);
            TaperCheck("快速直线：末点≈尖端下限 且中段=1、单调不增",
                       f != null && MathF.Abs(f[^1] - tipFloor) < 1e-4f && mid && mono,
                       $"末点 {f[^1]:F3}（下限 {tipFloor:F3}），中段=1:{mid}，单调:{mono}");
        }

        // ⑥ 跨停顿不算收笔速度：中段停 300ms 再快速移动 → 末段"飞一样快"不算数，不加笔锋
        {
            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = W };
            double t = 0;
            for (int i = 0; i <= 10; i++) { s.AddPoint(i * 4f, 0f, 0.5f, t); t += 6.7; }
            t += 300.0;
            for (int i = 1; i <= 3; i++) { s.AddPoint(40f + i * 20f, 0f, 0.5f, t); t += 6.7; }
            float sp = StrokeTaper.EndSpeed(s);
            TaperCheck("跨停顿不算收笔速度（EndSpeed=NaN / 全 1）",
                       float.IsNaN(sp) && StrokeTaper.Factors(s) == null,
                       $"EndSpeed={(float.IsNaN(sp) ? "NaN" : sp.ToString("F2"))}，Factors={(StrokeTaper.Factors(s) == null ? "null" : "非空")}");
        }

        // ⑧ 慢速尾长缩短：刚过 VMin（0.6 px/ms）时尾长明显小于满速尾长
        {
            var nearMin = TaperMakeLine(0.6f, 2f, 60, W);   // 0.6 px/ms：不触发保底
            var full = TaperMakeLine(3f, 2f, 60, W);        // 满速
            float aNear = TaperArc(nearMin), aFull = TaperArc(full);
            TaperCheck("慢速尾长缩短：刚过 VMin < 满速",
                       aNear > 0 && aFull > aNear * 2f,
                       $"0.6px/ms 尾长 {aNear:F0}px < 满速尾长 {aFull:F0}px");
        }
    }

    /// <summary>④ 尾长随速度单调 ⑤ 开关门控 ⑦ 前缀与整笔一致。</summary>
    private void TaperCheckShape()
    {
        const float W = 6f;

        // ④ 尾长随速度单调（慢速短、快速长）
        {
            var v1 = TaperMakeLine(0.8f, 4f, 40, W);    // 0.8 px/ms：t 低、尾短
            var v2 = TaperMakeLine(3f, 4f, 40, W);      // 3 px/ms：满速、尾长
            float a1 = TaperArc(v1), a2 = TaperArc(v2);
            TaperCheck("尾长随速度单调：慢速 < 快速", a1 > 0 && a2 > a1,
                       $"慢速尾长 {a1:F0}px < 快速尾长 {a2:F0}px");
        }

        // ⑤ 开关关 → Eligible 被 Enabled 门控
        {
            var fast = TaperMakeLine(3f, 10f, 20, W);
            bool onBefore = StrokeTaper.Enabled;
            StrokeTaper.Enabled = false;
            bool off = StrokeTaper.Eligible(fast);
            StrokeTaper.Enabled = true;
            bool on = StrokeTaper.Eligible(fast);
            StrokeTaper.Enabled = onBefore;
            TaperCheck("开关：关→Eligible=false，开→true", !off && on, $"关={off}，开={on}");
        }

        // ⑦ 前缀与整笔一致：prefix=1.0（lastIdx=末点、frac=0）与整笔末点系数相等
        {
            var fast = TaperMakeLine(3f, 10f, 20, W);
            var whole = StrokeTaper.Factors(fast);
            var prefix = StrokeTaper.Factors(fast, fast.Points.Count - 1, 0f);
            TaperCheck("前缀（prefix=1.0）与整笔末点系数一致",
                       whole != null && prefix != null
                       && MathF.Abs(whole[^1] - prefix[^1]) < 1e-5f
                       && MathF.Abs(prefix[^1] - StrokeTaper.TipFloorFactor(W)) < 1e-4f,
                       $"整笔末点 {whole[^1]:F3}，前缀末点 {prefix[^1]:F3}（下限 {StrokeTaper.TipFloorFactor(W):F3}）");
        }
    }

    /// <summary>屏幕部分：鼠标快/慢与合成笔快/慢各一组，量末端 vs 中段竖直宽度。</summary>
    private void TaperCheckScreen()
    {
        if (!OverlayWindow.InkAvailable)
        {
            Console.WriteLine("  SKIP: 变宽通道不可用，屏幕部分没法验");
            return;
        }

        const float W = 6f;
        bool oldBoard = BoardOn, oldTrail = OverlayWindow.InkTrailEnabled;
        float oldPenWidth = PenWidthLogical;
        var oldColor = CurrentColor;
        BoardOn = true;
        OverlayWindow.InkTrailEnabled = false;
        PenWidthLogical = W;
        SetColorFromUi(new Color4(1f, 0f, 0f, 1f));
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        SettleFrames(300);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        const float halfLen = 260f;

        int slowMid = TaperMousePair(cx, cy, halfLen);
        TaperPenPair(cx, cy, halfLen, slowMid);
        TaperSaveShot(cx, cy, halfLen);
        TaperSwitchRoundTrip();

        BoardOn = oldBoard;
        OverlayWindow.InkTrailEnabled = oldTrail;
        PenWidthLogical = oldPenWidth;
        SetColorFromUi(oldColor);
    }

    /// <summary>合成鼠标（无压感）画快/慢两条线，返回慢线中段宽度（= 档位宽）。</summary>
    private int TaperMousePair(float cx, float cy, float halfLen)
    {
        void MouseLine(float y, int steps, float stepPx, int settleMs)
        {
            float startX = cx - halfLen;
            SendMouse((int)startX, (int)y, 0);
            SendMouse((int)startX, (int)y, Native.MOUSEEVENTF_LEFTDOWN);
            for (int i = 1; i <= steps; i++)
            {
                SendMouse((int)(startX + i * stepPx), (int)y, 0);
                SettleFrames(settleMs);
            }
            SendMouse((int)(startX + steps * stepPx), (int)y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);
        }

        float yFast = cy - 140f, ySlow = cy + 140f;
        MouseLine(yFast, 26, 20f, 2);   // 快：末段 > VMin
        MouseLine(ySlow, 65, 8f, 30);   // 慢：末段 < VMin

        int fastMid = TaperWidth(cx, yFast), fastTip = TaperWidth(cx + halfLen - 2f, yFast);
        int fastTip2 = TaperWidth(cx + halfLen - 14f, yFast);
        int slowMid = TaperWidth(cx, ySlow), slowTip = TaperWidth(cx + halfLen - 2f, ySlow);

        TaperCheck("快写：末端明显比中段窄",
                   fastMid > 0 && fastTip < fastMid * 0.5f,
                   $"中段 {fastMid}px → 末端 {fastTip}px");
        TaperCheck("快写：末端明显比末端往回 12px 窄",
                   fastTip2 > 0 && fastTip < fastTip2 * 0.75f,
                   $"末端 {fastTip}px < 末端-12px {fastTip2}px");
        TaperCheck("慢写：末端 ≈ 中段（等宽）",
                   slowMid > 0 && slowTip >= slowMid * 0.7f,
                   $"中段 {slowMid}px → 末端 {slowTip}px");
        return slowMid;
    }

    /// <summary>合成笔（满压恒定）：快线末端收尖、中段 = 满压 2×档位；慢线末端≈中段。</summary>
    private void TaperPenPair(float cx, float cy, float halfLen, int slowMid)
    {
        if (!EnsureSyntheticPen())
        {
            Console.WriteLine("  SKIP: 拿不到合成笔设备，有压感笔迹部分没法验");
            return;
        }

        void PenLine(float y, int steps, float stepPx, int settleMs)
        {
            float startX = cx - halfLen;
            SendPenPoint(startX, y, 1024, contact: true, first: true);
            for (int i = 1; i <= steps; i++)
            {
                SendPenPoint(startX + i * stepPx, y, 1024, contact: true, first: false);
                SettleFrames(settleMs);
            }
            SendPenPoint(startX + steps * stepPx, y, 1024, contact: false, first: false);
            SettleFrames(200);
        }

        float yPenFast = cy - 60f, yPenSlow = cy + 60f;
        PenLine(yPenFast, 26, 20f, 2);   // 快
        PenLine(yPenSlow, 65, 8f, 30);   // 慢

        int penFastMid = TaperWidth(cx, yPenFast);
        int penFastTip = TaperWidth(cx + halfLen - 2f, yPenFast);
        int penSlowMid = TaperWidth(cx, yPenSlow);
        int penSlowTip = TaperWidth(cx + halfLen - 2f, yPenSlow);

        TaperCheck("有压感快线：中段 = 满压 2×档位宽",
                   penFastMid > 0 && slowMid > 0
                   && penFastMid >= slowMid * 1.7f && penFastMid <= slowMid * 2.3f,
                   $"满压中段 {penFastMid}px vs 档位 {slowMid}px");
        TaperCheck("有压感快线：末端明显比中段窄",
                   penFastTip < penFastMid * 0.5f,
                   $"中段 {penFastMid}px → 末端 {penFastTip}px");
        TaperCheck("有压感慢线：末端 ≈ 中段（等宽）",
                   penSlowMid > 0 && penSlowTip >= penSlowMid * 0.7f,
                   $"中段 {penSlowMid}px → 末端 {penSlowTip}px");
    }

    /// <summary>某点上下 60px 内的红色像素数（窄区域竖直厚度）。</summary>
    private static int TaperWidth(float x, float y) => ScreenProbe.CountRed((int)x - 1, (int)y - 30, 3, 60);

    /// <summary>快/慢对比图（四条线），供肉眼验收；不算断言项。</summary>
    private void TaperSaveShot(float cx, float cy, float halfLen)
    {
        int sx = (int)(cx - halfLen) - 20, sy = (int)(cy - 170);
        int sw = (int)(halfLen * 2) + 40, sh = 340;
        bool saved = ScreenProbe.SaveBmp("reports/笔锋-快慢对比.bmp", sx, sy, sw, sh);
        string pngNote = "";
        if (saved)
        {
            try
            {
                using var bmp = System.Drawing.Image.FromFile(
                    Path.GetFullPath("reports/笔锋-快慢对比.bmp"));
                bmp.Save("reports/笔锋-快慢对比.png", System.Drawing.Imaging.ImageFormat.Png);
                pngNote = "；PNG 已存";
            }
            catch { pngNote = "；PNG 转换失败"; }
        }
        Console.WriteLine($"  [图] reports/笔锋-快慢对比.bmp {(saved ? "已保存" : "保存失败")}（区域 ({sx},{sy}) {sw}×{sh}）{pngNote}");
    }

    /// <summary>开关往返：文档笔画点数据一个字节不动；压感关掉时笔锋仍生效。</summary>
    private void TaperSwitchRoundTrip()
    {
        var snap = Doc.Strokes.Select(s => s.Points.ToArray()).ToList();
        var oldPressure = PressureWidth.Enabled;
        int drawsByTaperOnly = 0;
        try
        {
            PressureWidth.Enabled = false;
            Doc.InvalidateAll();
            SettleFrames(200);
            int before = OverlayWindow.PressureInkDraws;
            Doc.InvalidateAll();
            SettleFrames(200);
            drawsByTaperOnly = OverlayWindow.PressureInkDraws - before;
        }
        finally { PressureWidth.Enabled = oldPressure; }
        TaperCheck("压感关掉后笔锋仍生效（开关互相独立）",
                   drawsByTaperOnly > 0,
                   $"重画时变宽通道绘制 {drawsByTaperOnly} 次（应为 >0）");
        SettleFrames(200);

        SetTaperFromUi(false);
        SettleFrames(200);
        SetTaperFromUi(true);
        SettleFrames(200);
        bool same = snap.Count == Doc.Strokes.Count;
        for (int i = 0; same && i < snap.Count; i++)
        {
            var a = snap[i];
            var b = Doc.Strokes[i].Points;
            if (a.Length != b.Count) { same = false; break; }
            for (int j = 0; j < a.Length; j++)
                if (a[j].X != b[j].X || a[j].Y != b[j].Y || a[j].P != b[j].P || a[j].T != b[j].T)
                { same = false; break; }
        }
        TaperCheck("开关往返：文档笔画点数据不变",
                   same && Doc.Strokes.Count >= 2,
                   $"笔画 {Doc.Strokes.Count} 条，点数据 {(same ? "一致" : "变了")}");
    }
}
