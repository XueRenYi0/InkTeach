// 对象吸附专项自检（2026-10-18）：
// 只验证几何选择结果，不依赖鼠标轨迹的像素级回放。
// 覆盖：3×3 线组合、软相交、中心、抛物线顶点、左对中、远距离不吸、正弦拼缝。

using System.Numerics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    private void SnapTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 对象吸附（选中框 3×3 / 软相交）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        bool Near(float a, float b, float eps = 0.8f) => MathF.Abs(a - b) <= eps;
        Vector2 FrameCenter(Stroke s)
        {
            var b = s.WorldInkBounds;
            return new Vector2((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f);
        }
        void ResetDoc()
        {
            Doc.Clear();
            Doc.ClearHistory();
            Doc.Selected.Clear();
        }
        Stroke AddCoordinate(float ox, float oy)
        {
            var c = new Stroke
            {
                Tool = Tool.Coordinate,
                Kind = StrokeKind.Coordinate,
                Width = 4f,
            };
            c.SetAxisBox(ox, oy, ox + 600f, oy + 600f);
            Doc.AddStroke(c);
            return c;
        }
        Stroke AddRect(float cx, float cy, float halfW, float halfH)
        {
            var r = new Stroke
            {
                Tool = Tool.Rectangle,
                Kind = StrokeKind.Rectangle,
                Width = 4f,
            };
            r.AddPoint(cx - halfW, cy - halfH, 1f, 0);
            r.AddPoint(cx + halfW, cy + halfH, 1f, 0);
            Doc.AddStroke(r);
            return r;
        }
        Stroke AddEllipse(float cx, float cy, float a, float b)
        {
            var e = new Stroke
            {
                Tool = Tool.Ellipse,
                Kind = StrokeKind.Ellipse,
                Width = 4f,
            };
            e.AddPoint(cx, cy, 1f, 0);
            e.AddPoint(cx + a, cy + b, 1f, 0);
            Doc.AddStroke(e);
            return e;
        }
        Stroke AddParabola(float vx, float vy)
        {
            var p = new Stroke
            {
                Tool = Tool.Parabola,
                Kind = StrokeKind.Parabola,
                Width = 4f,
                CurveAxis = CurveAxis.OpenUp,
            };
            p.SetParabolaVertex(vx, vy);
            p.SetParabolaThroughPoint(vx + 200f, vy - 400f);
            Doc.AddStroke(p);
            return p;
        }
        Stroke AddSine(float x, float y, float width)
        {
            var s = new Stroke
            {
                Tool = Tool.Sine,
                Kind = StrokeKind.Sine,
                Width = 4f,
            };
            s.SetWaveBox(x, y, x + width, y - 100f, 4f);
            Doc.AddStroke(s);
            return s;
        }
        Stroke AddLine(float x0, float y0, float x1, float y1, float width)
        {
            var s = new Stroke
            {
                Tool = Tool.Line,
                Kind = StrokeKind.Line,
                Width = width,
            };
            s.AddPoint(x0, y0, 1f, 0);
            s.AddPoint(x1, y1, 1f, 0);
            Doc.AddStroke(s);
            return s;
        }
        Vector2 WaveStartWorld(Stroke s)
            => Vector2.Transform(s.WaveTracedPointAt(0f), s.Transform);
        Vector2 WaveEndWorld(Stroke s)
            => Vector2.Transform(s.WaveTracedPointAt(1f), s.Transform);
        bool Drag(Stroke s, Vector2 from, Vector2 to)
        {
            Doc.SelectOnly(new[] { s });
            bool took = SelectionGestureForTest(from.X, from.Y);
            if (!took) return false;
            UpdateSelectionGestureForTest(to.X, to.Y);
            EndSelectionGestureForTest();
            return true;
        }

        float dpi = DpiScale;

        // ① 椭圆中心 → 坐标系原点（点数：中心对中心）
        ResetDoc();
        AddCoordinate(1000f, 1000f);
        var ellipse = AddEllipse(1600f, 1000f, 120f, 80f);
        var eStart = FrameCenter(ellipse);
        var eEnd = eStart + new Vector2(1000f + 3f * dpi - eStart.X,
                                        1000f + 2f * dpi - eStart.Y);
        bool eTook = Drag(ellipse, eStart, eEnd);
        var ec = FrameCenter(ellipse);
        Check("椭圆中心对原点", eTook && Near(ec.X, 1000f) && Near(ec.Y, 1000f),
              $"中心 ({ec.X:F1},{ec.Y:F1})");

        // ② 抛物线顶点 → 原点（下边中点 → 横中线 + 竖中线 → 竖中线）
        ResetDoc();
        AddCoordinate(1000f, 1000f);
        var parabola = AddParabola(1600f, 1000f);
        var pv = Vector2.Transform(parabola.ShapeCenterLocal, parabola.Transform);
        var pStart = FrameCenter(parabola);
        var pEnd = pStart + new Vector2(1000f + 3f * dpi - pv.X,
                                        1000f + 2f * dpi - pv.Y);
        bool pTook = Drag(parabola, pStart, pEnd);
        var pvAfter = Vector2.Transform(parabola.ShapeCenterLocal, parabola.Transform);
        var pFrame = parabola.WorldInkBounds;
        // 选中框口径：框底（顶点处的墨迹外沿）贴横中线，顶点中心线会高出半个笔宽。
        Check("抛物线底边对横中线（框口径）",
              pTook && Near(pvAfter.X, 1000f) && Near(pFrame.MaxY, 1000f),
              $"顶点 x {pvAfter.X:F1}，框底 {pFrame.MaxY:F1}，顶点 y {pvAfter.Y:F1}");

        // ③ 左边缘 → 目标竖中线（3×3 的交叉角色）
        ResetDoc();
        AddCoordinate(1000f, 1000f);
        var rect = AddRect(1600f, 1000f, 100f, 100f);
        var rStart = FrameCenter(rect);
        float rHalf = (rect.WorldInkBounds.MaxX - rect.WorldInkBounds.MinX) * 0.5f;
        float wantCenterX = 1000f + rHalf + 3f * dpi;
        var rEnd = rStart + new Vector2(wantCenterX - rStart.X, 0f);
        bool rTook = Drag(rect, rStart, rEnd);
        var rb = rect.WorldInkBounds;
        Check("左边缘对目标竖中线", rTook && Near(rb.MinX, 1000f),
              $"左边 {rb.MinX:F1}");

        // ④ 软相交：上下差很远时，左边对左边不吸
        ResetDoc();
        var farTarget = AddRect(1000f, 300f, 100f, 100f);
        var farSource = AddRect(1000f, 1300f, 100f, 100f);
        var fStart = FrameCenter(farSource);
        var fEnd = fStart + new Vector2(6f * dpi, 0f);
        bool fTook = Drag(farSource, fStart, fEnd);
        var fb = farSource.WorldInkBounds;
        Check("远距离不触发左对齐", fTook && !Near(fb.MinX, farTarget.WorldInkBounds.MinX),
              $"源左边 {fb.MinX:F1}，目标左边 {farTarget.WorldInkBounds.MinX:F1}");

        // ⑤ 两个正弦周期：右边对左边拼上。
        // 拼缝看的是**曲线端点中心线**，不是含笔宽的外框；否则外沿相碰、
        // 中心线还差一个笔宽，看起来就是一个小台阶（用户 2026-10-19）。
        ResetDoc();
        var sineA = AddSine(1000f, 1000f, 300f);
        var sineB = AddSine(1400f, 1000f, 300f);
        var sStart = FrameCenter(sineB);
        float gapWant = 3f * dpi;
        float moveX = sineA.WorldInkBounds.MaxX - sineB.WorldInkBounds.MinX
                      - sineB.Width + gapWant;
        var sEnd = sStart + new Vector2(moveX, 0f);
        bool sTook = Drag(sineB, sStart, sEnd);
        var sAEnd = WaveEndWorld(sineA);
        var sBStart = WaveStartWorld(sineB);
        Check("两个正弦周期拼缝（中心线）",
              sTook && Near(sAEnd.X, sBStart.X) && Near(sAEnd.Y, sBStart.Y),
              $"接缝 A({sAEnd.X:F1},{sAEnd.Y:F1}) / B({sBStart.X:F1},{sBStart.Y:F1})");

        // ⑥ 细线 / 轴类目标只认中心线：正弦中线应落到 X 轴中心，
        //    而不是被笔宽造成的上沿 / 下沿先吸走（用户 2026-10-19 报的场景）。
        ResetDoc();
        AddLine(900f, 1000f, 1400f, 1000f, 8f * dpi);
        var axisSine = AddSine(1000f, 1000f + 4f * dpi - 10f, 300f);
        var aStart = FrameCenter(axisSine);
        // 拖 10px 后：中线离轴心 4*dpi，离笔迹下沿 0——旧逻辑会吸下沿。
        bool aTook = Drag(axisSine, aStart, aStart + new Vector2(0f, 10f));
        var ac = FrameCenter(axisSine);
        Check("正弦中线对横轴中心", aTook && Near(ac.Y, 1000f),
              $"中线 y {ac.Y:F1}（轴心 1000.0）");

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? $"  PASS: 对象吸附（{pass} 项）"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");

        ResetDoc();
        _quit = true;
    }
}
