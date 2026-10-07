// 本文件由 App.cs 拆出（2026-10-07）：InkMotion 这一组。
// **纯搬家，逻辑一字未改** —— 靠 partial class 共享 App 的私有成员。
// 拆开的目的：产品代码与自检代码互不干扰，人和 AI 读代码时不必互相穿插。

using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{

    private void CurveTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 四种曲线自检（抛物线 / 双曲线 / 正弦 / 余弦）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-38} {detail}");
        }
        bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
        // 方向比较：两个单位向量「几乎相同」（用于"方向没变 / 变到哪去了"这类断言）。
        bool NearDir(Vector2 a, Vector2 b) => Vector2.Distance(a, b) <= 0.01f;

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        float minAxis = ShapeMinAxisLogical * DpiScale;
        var ink = new Color4(1f, 0f, 1f, 1f);            // 品红：屏幕上好数
        Host.Commands.SetColor(ink);

        // 造一条"刚起手"的曲线（只有 BeginShapeAt 铺的那一个占位点），
        // 再走 Set*Box —— 和真机拖动走的是同一条路。
        Stroke NewCurve(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke { Tool = tool, Kind = kind, Color = ink, Width = 4f * DpiScale };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }
        // 点到线段的距离（验"两支之间有没有幽灵线段"用）。
        float DistPointSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return Vector2.Distance(p, a);
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        // ================= ① 画法：真机**一笔拖**画一条抛物线 =================
        // 口径（用户 2026-09-20 照 InkClass 的 `case 20/21`）：**顶点 = 按下那个点**，
        // 一次拖到"曲线要经过的地方"、松手就成；朝向是画之前在图形面板上选好的
        // （这里走默认的"开口向上"）。
        Doc.Clear();
        Doc.ClearHistory();
        SetToolFromUi(Tool.Parabola);
        float px = _virtualX + 900f, py = _virtualY + 900f;

        // 按下（还没拖）：半成品不进文档。
        SendMouse((int)px, (int)py, 0);                            SettleFrames(60);
        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
        Check("抛物线：按下（定顶点）之后**还没提交**（半成品不进文档）",
              Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 条（期望 0）");

        // 拖到顶点右上方 120 × 100：曲线**经过这个点**——
        // 朝上开口时 `s = 100、t = 120` → `p = t²/(2s) = 14400/200 = 72`。
        SendMouse((int)(px + 120f), (int)(py - 100f), 0);   SettleFrames(240);
        Check("抛物线：拖动的时候曲线**经过指针那个点**（p = t²/2s = 72）（±2）",
              ActiveStroke != null && Near(ActiveStroke.ParabolaPLocal(), 72f, 2f),
              $"p {(ActiveStroke == null ? -1f : ActiveStroke.ParabolaPLocal()):F1}（期望 72）");

        // 松手：完成（提交就发生在这一刻）。
        SendMouse((int)(px + 120f), (int)(py - 100f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);

        var pbDraw = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("抛物线：**松手之后**对象才出来（一条抛物线）",
              pbDraw != null && pbDraw.Kind == StrokeKind.Parabola,
              pbDraw == null ? $"对象 {Doc.Strokes.Count} 条（期望 1）" : $"Kind={pbDraw.Kind}");
        if (pbDraw == null) { Console.WriteLine("  （后面几项没法验）"); _quit = true; return; }

        Check("抛物线：两个定义元素（顶点 ＋ 曲线上的那个点）",
              pbDraw.Points.Count == 2, $"控制点 {pbDraw.Points.Count} 个");
        Check("抛物线：**顶点 = 按下那个点**（±2 像素）",
              Near(pbDraw.CurvePointLocal(0).X, px, 2f) && Near(pbDraw.CurvePointLocal(0).Y, py, 2f),
              $"顶点 ({pbDraw.CurvePointLocal(0).X:F0},{pbDraw.CurvePointLocal(0).Y:F0})"
              + $" 期望 ({px:F0},{py:F0})");
        Check("抛物线：**经过点 = 最后点的那个位置**（±2 像素）",
              Near(pbDraw.CurvePointLocal(1).X, px + 120f, 2f)
              && Near(pbDraw.CurvePointLocal(1).Y, py - 100f, 2f),
              $"经过点 ({pbDraw.CurvePointLocal(1).X:F0},{pbDraw.CurvePointLocal(1).Y:F0})"
              + $" 期望 ({px + 120f:F0},{py - 100f:F0})");
        Check("抛物线：朝向 = **开口向上**（默认值，不是从两点推的）",
              pbDraw.EffectiveAxis == CurveAxis.OpenUp && Near(pbDraw.ParabolaDirLocal().Y, -1f, .01f),
              $"{pbDraw.EffectiveAxis} / {pbDraw.ParabolaDirLocal()}");
        Check("抛物线：p = 72（±4）", Near(pbDraw.ParabolaPLocal(), 72f, 4f),
              $"p {pbDraw.ParabolaPLocal():F1}");
        Check("抛物线：一笔拖到底 = **一步撤销**（不是两步）",
              Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");

        // ================= ② 几何算式（直接摆点，不走工具） =================
        Doc.Clear();
        Doc.ClearHistory();

        // ---- 抛物线：顶点 (400,400)、**开口向上**、经过点 (700,250) → p = 300 ----
        // 经过点取的是 `u = 1` 处的曲线点（也就是通径端点）：`s = p/2 = 150、t = p = 300`
        // → 反解 `p = t²/(2s) = 300`。它和上一版"顶点 + 张口点 (400,250)"画出来的是
        // **同一条曲线**，所以下面那些紧框 / 脏区断言一个都不用改。
        var vb = NewCurve(Tool.Parabola, StrokeKind.Parabola, 400f, 400f);
        vb.CurveAxis = CurveAxis.OpenUp;
        vb.SetParabolaVertex(400f, 400f);
        vb.SetParabolaThroughPoint(700f, 250f);
        Check("抛物线：顶点 (400,400)、经过点 (700,250)（±0.5）",
              Near(vb.CurvePointLocal(0).X, 400f, .5f) && Near(vb.CurvePointLocal(0).Y, 400f, .5f)
              && Near(vb.CurvePointLocal(1).X, 700f, .5f) && Near(vb.CurvePointLocal(1).Y, 250f, .5f),
              $"顶点 ({vb.CurvePointLocal(0).X:F1},{vb.CurvePointLocal(0).Y:F1})"
              + $" 经过点 ({vb.CurvePointLocal(1).X:F1},{vb.CurvePointLocal(1).Y:F1})");
        Check("抛物线：朝向 = **选出来的开口向上**（不是从两点推的）",
              vb.EffectiveAxis == CurveAxis.OpenUp
              && Near(vb.ParabolaDirLocal().X, 0f, .01f) && Near(vb.ParabolaDirLocal().Y, -1f, .01f),
              $"{vb.EffectiveAxis} / {vb.ParabolaDirLocal()}");
        Check("抛物线：p 由经过点反解 = t²/(2s) = 300²/300 = 300（±0.5）",
              Near(vb.ParabolaPLocal(), 300f, .5f), $"p {vb.ParabolaPLocal():F1}");
        var vbT0 = vb.ParabolaPointAt(0f);
        var vbT1 = vb.ParabolaPointAt(1f);
        Check("抛物线：t=0 落在顶点、t=±1 落在**通径端点**（= 我们给的那个经过点）上（±0.5）",
              Near(vbT0.X, 400f, .5f) && Near(vbT0.Y, 400f, .5f)
              && Near(vbT1.X, 700f, .5f) && Near(vbT1.Y, 250f, .5f),
              $"t0 ({vbT0.X:F1},{vbT0.Y:F1})  t1 ({vbT1.X:F1},{vbT1.Y:F1}) 期望 (400,400) / (700,250)");
        var vbBox = vb.CurveBoxLocal();
        // 紧框 = **"顶点 ↔ 经过点"那个矩形**（照 InkClass：画到拖到的那个点为止）——
        // 于是那个点**正好落在框角上**：横跨 ±300、沿轴 0…150。
        Check("抛物线：紧框 = **拖动那个矩形**（经过点正好落在框角上）（±0.5）",
              Near(vbBox.MinX, 100f, .5f) && Near(vbBox.MaxX, 700f, .5f)
              && Near(vbBox.MinY, 250f, .5f) && Near(vbBox.MaxY, 400f, .5f),
              $"框 ({vbBox.MinX:F1},{vbBox.MinY:F1})..({vbBox.MaxX:F1},{vbBox.MaxY:F1})"
              + " 期望 (100,250)..(700,400)");
        var vbPad = vb.PaddedBounds;
        Check("抛物线：脏区框 = 紧框 ＋ 半笔宽 ＋ 2（±0.05）",
              Near(vbPad.MinX, vbBox.MinX - vb.Width * .5f - 2f, .05f)
              && Near(vbPad.MaxY, 400f + vb.Width * .5f + 2f, .05f),
              $"({vbPad.MinX:F2},{vbPad.MinY:F2})..({vbPad.MaxX:F2},{vbPad.MaxY:F2})"
              + $" 笔宽 {vb.Width:F1}");

        // ---- 双曲线：中心 (1200,500)，第一步"拖出渐近线框 (320,240)" ----
        //      **拖到哪就是哪**：A = 320、B = 240（2026-09-20 改成不再打对折，
        //      因为渐近线自己就是框、不再需要"画到 2a、2b"那种放大）。
        //      第二步（第三个点）= "**曲线要经过的那个点**"：(1370,560)，相对中心 `dx = 170、dy = 60`
        //      → 曲线的 x 方向半宽 `a = √(170² − 60²·(4/3)²) = √22500 = 150`、
        //      y 方向半高 `b = a × B/A = 112.5`。**这一步渐近线框一个字都不动**（这次改动的核心）。
        var hy = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1200f, 500f);
        hy.SetHyperbolaFromAsymptote(1200f, 500f, 1520f, 740f, minAxis);
        hy.SetHyperbolaThroughPoint(1370f, 560f);
        Check("双曲线：第一步 A = 320、B = 240（**拖到哪就是哪**）（±0.5）",
              Near(hy.HyperbolaALocal(), 320f, .5f) && Near(hy.HyperbolaBLocal(), 240f, .5f),
              $"A {hy.HyperbolaALocal():F1}  B {hy.HyperbolaBLocal():F1}");
        Check("双曲线：渐近线斜率 = B/A = 240/320 = 0.75（就是 InkClass 的 |dy/dx|）（±0.001）",
              Near(hy.HyperbolaBLocal() / hy.HyperbolaALocal(), 0.75f, .001f),
              $"{hy.HyperbolaBLocal() / hy.HyperbolaALocal():F4}（期望 0.7500）");
        // **第二步只写第三个点**：曲线的半轴由它反解，渐近线框（A / B）**原封不动**。
        Check("双曲线：第二步的点反解出曲线的半轴（a = 150、b = 112.5），框不动（±0.5）",
              Near(hy.HyperbolaCurveALocal(), 150f, .5f) && Near(hy.HyperbolaCurveBLocal(), 112.5f, .5f)
              && Near(hy.HyperbolaALocal(), 320f, .5f) && Near(hy.HyperbolaBLocal(), 240f, .5f),
              $"曲线 a {hy.HyperbolaCurveALocal():F1}、b {hy.HyperbolaCurveBLocal():F1}；"
              + $"框 A {hy.HyperbolaALocal():F1}、B {hy.HyperbolaBLocal():F1}");
        var hyBox = hy.CurveBoxLocal();
        // 紧框 = **渐近线的那个矩形**（用户 2026-09-20 定："把双曲线限制在渐近线的矩形框内"）。
        // hy：中心 (1200,500)、A = 320、B = 240 → 框 = ±320 × ±240。
        Check("双曲线：紧框 = ±A × ±B（就是渐近线框）（±0.5）",
              Near(hyBox.MinX, 1200f - 320f, .5f) && Near(hyBox.MaxX, 1200f + 320f, .5f)
              && Near(hyBox.MinY, 500f - 240f, .5f) && Near(hyBox.MaxY, 500f + 240f, .5f),
              $"框 ({hyBox.MinX:F1},{hyBox.MinY:F1})..({hyBox.MaxX:F1},{hyBox.MaxY:F1})"
              + $" 期望 (880,260)..(1520,740)");
        var hyApex = hy.HyperbolaVertexLocal(1);
        Check("双曲线：顶点在 (中心.x ＋ a, 中心.y) = (1350, 500)（±0.5）",
              Near(hyApex.X, 1350f, .5f) && Near(hyApex.Y, 500f, .5f),
              $"({hyApex.X:F1},{hyApex.Y:F1}) 期望 (1350,500)");
        // 两支之间的"幽灵线段"：轮廓折线是被逐段判交消费的（橡皮 / 套索），
        // 中间那条横穿的连线会让"橡皮从两支之间划过 = 整条被删"。
        var hyOutline = hy.ShapeOutline();
        float nearest = float.MaxValue;
        int breaks = 0;
        for (int i = 1; i < hyOutline.Count; i++)
        {
            if (Stroke.IsOutlineBreak(hyOutline[i - 1]))
            {
                breaks++;                 // 抬笔标记（见 Stroke.OutlineBreak）：一条接在它后面
                continue;
            }
            if (Stroke.IsOutlineBreak(hyOutline[i])) continue;      // 同上一条的尾巴
            nearest = MathF.Min(nearest, DistPointSeg(new Vector2(1200f, 500f),
                                                      hyOutline[i - 1], hyOutline[i]));
        }
        Check("双曲线：轮廓里恰好一个**抬笔标记**（两支分开、不连线）", breaks == 1, $"{breaks} 处抬笔");
        Check("双曲线：轮廓里**没有横穿两支之间**的幽灵线段（中心到任一段 ≥ 0.9a）",
              nearest >= 150f * 0.9f, $"最近距离 {nearest:F1}（a = 150，要 ≥ 135）");

        // **曲线整条收在渐近线框里**（用户 2026-09-20 的诉求）：
        // 渐近线框 = ±A × ±B = ±320 × ±240，而曲线"**出框就停笔**"（见 Stroke.HyperbolaTMaxOf）——
        // 先到哪条边就停在哪条边。hy 的 `2a = 300 < A = 320`，所以它**先撞上那条"最多画到 2a"的上限**
        // （`cosh t = 2`，见 HyperbolaDrawT），横向停在 300、纵向停在 `b√3 ≈ 194.9`。
        float hMaxX = 0f, hMaxY = 0f;
        foreach (var q in hyOutline)
        {
            if (Stroke.IsOutlineBreak(q)) continue;
            hMaxX = MathF.Max(hMaxX, MathF.Abs(q.X - 1200f));
            hMaxY = MathF.Max(hMaxY, MathF.Abs(q.Y - 500f));
        }
        Check("双曲线：**整条曲线收在渐近线框内**（|x| ≤ A、|y| ≤ B）（±0.5）",
              hMaxX <= 320f + .5f && hMaxY <= 240f + .5f,
              $"曲线最远 ({hMaxX:F1},{hMaxY:F1})，框 (±320,±240)");
        Check("双曲线：横向**画到拖到的那个点为止**（最远 x = 170，照 InkClass）（±0.5）",
              Near(hMaxX, 170f, .5f), $"{hMaxX:F1}（期望 170）");
        Check("双曲线：另一条轴只到 b·sinh(t)（≈59.8，那个点在那儿）（±0.5）",
              Near(hMaxY, 60f, .5f), $"{hMaxY:F1}（期望 ≈60）");

        // ---- 正弦：起点 (300,1000)；拖出 **480 宽 × 100 高 = 一个周期 + 振幅** ----
        // ⚠ 2026-09-20 第十六批：正弦 / 余弦回到"**框宽就是一个周期**"
        //（用户："正弦和余弦用一个周期的图"）——多周期那件事交给**波浪线**那一格
        //（横向 = 要画多长，见下面 ⑥ 段）。
        //   A = 100（正弦：纵拖 = 振幅）→ **T = 框宽 = 480** → 画 1 个周期。
        var sn = NewCurve(Tool.Sine, StrokeKind.Sine, 300f, 1000f);
        sn.SetWaveBox(300f, 1000f, 780f, 900f, minAxis);
        Check("正弦：起点 = 按下点、终点 = 起点＋(长度, 高度)（±0.5）",
              Near(sn.Points[0].X, 300f, .5f) && Near(sn.Points[0].Y, 1000f, .5f)
              && Near(sn.Points[1].X, 780f, .5f) && Near(sn.Points[1].Y, 900f, .5f),
              $"起点 ({sn.Points[0].X:F1},{sn.Points[0].Y:F1})"
              + $" 终点 ({sn.Points[1].X:F1},{sn.Points[1].Y:F1})");
        Check("正弦：两个定义元素（起点 ＋ 终点，极值点是现推的）",
              sn.Points.Count == 2, $"{sn.Points.Count} 个");
        Check("正弦：A = 100、**T = 框宽 = 480**、正好画 1 个周期（±0.5）",
              Near(sn.WaveAmplitudeLocal(), 100f, .5f)
              && Near(sn.WavePeriodLocal(), 480f, .5f)
              && Near(sn.WaveLengthLocal(), 480f, .5f)
              && Near(sn.WaveCyclesLocal(), 1f, .01f),
              $"A {sn.WaveAmplitudeLocal():F1}  T {sn.WavePeriodLocal():F1}"
              + $"  长 {sn.WaveLengthLocal():F1}  周期数 {sn.WaveCyclesLocal():F2}（期望 1.00）");
        var snQ = sn.WavePointAt(0.25f);
        var snH = sn.WavePointAt(0.5f);
        var snT = sn.WavePointAt(0.75f);
        Check("正弦：u=0 在轴上、u=0.25 到峰、u=0.5 回轴、u=0.75 到谷（±0.5）",
              Near(sn.WavePointAt(0f).Y, 1000f, .5f) && Near(snQ.Y, 900f, .5f)
              && Near(snH.Y, 1000f, .5f) && Near(snT.Y, 1100f, .5f),
              $"0:{sn.WavePointAt(0f).Y:F1} 1/4:{snQ.Y:F1} 1/2:{snH.Y:F1} 3/4:{snT.Y:F1}");
        var snBox = sn.CurveBoxLocal();
        Check("正弦：紧框 = 一个周期宽 × ±A（峰和谷都在里面）（±0.5）",
              Near(snBox.MinX, 300f, .5f) && Near(snBox.MaxX, 780f, .5f)
              && Near(snBox.MinY, 900f, .5f) && Near(snBox.MaxY, 1100f, .5f),
              $"框 ({snBox.MinX:F1},{snBox.MinY:F1})..({snBox.MaxX:F1},{snBox.MaxY:F1})");

        // ---- 余弦：起点 (300,1400)（**峰顶**）；拖出 480 宽 × 200 高 = 一个周期 ----
        // 纵向拖的是"峰 → 谷"＝ 2A（这是余弦和正弦唯一的不对称，见 WaveAmplitudeOf），
        // 横向那一拖同样是**一个周期**。
        var cs = NewCurve(Tool.Cosine, StrokeKind.Cosine, 300f, 1400f);
        cs.SetWaveBox(300f, 1400f, 780f, 1600f, minAxis);   // 纵拖 = 峰→谷 = 2A → A = 100
        Check("余弦：起点是**峰顶**、终点在**谷**那一侧（±0.5）",
              Near(cs.Points[0].Y, 1400f, .5f) && Near(cs.Points[1].Y, 1600f, .5f)
              && Near(cs.WaveTroughLocal().X, 540f, .5f) && Near(cs.WaveTroughLocal().Y, 1600f, .5f),
              $"起点 y {cs.Points[0].Y:F1}  终点 y {cs.Points[1].Y:F1}"
              + $"  谷 ({cs.WaveTroughLocal().X:F1},{cs.WaveTroughLocal().Y:F1})"
              + "（期望 x=540 = 300 ＋ 半个周期 240）");
        Check("余弦：A = 100、T = 框宽 = 480、正好 1 个周期（和正弦同一条算式）（±0.5）",
              Near(cs.WaveAmplitudeLocal(), 100f, .5f) && Near(cs.WavePeriodLocal(), 480f, .5f)
              && Near(cs.WaveCyclesLocal(), 1f, .01f),
              $"A {cs.WaveAmplitudeLocal():F1}  T {cs.WavePeriodLocal():F1}"
              + $"  周期数 {cs.WaveCyclesLocal():F2}（期望 1.00）");
        Check("余弦：u=0 在峰顶、u=0.25 到中线、u=0.5 到谷底（±0.5）",
              Near(cs.WavePointAt(0f).Y, 1400f, .5f) && Near(cs.WavePointAt(0.25f).Y, 1500f, .5f)
              && Near(cs.WavePointAt(0.5f).Y, 1600f, .5f),
              $"0:{cs.WavePointAt(0f).Y:F1} 1/4:{cs.WavePointAt(0.25f).Y:F1} 1/2:{cs.WavePointAt(0.5f).Y:F1}");
        var csBox = cs.CurveBoxLocal();
        Check("余弦：紧框 = 一个周期宽 × (起点 → 谷底 2A)（±0.5）",
              Near(csBox.MinX, 300f, .5f) && Near(csBox.MaxX, 780f, .5f)
              && Near(csBox.MinY, 1400f, .5f) && Near(csBox.MaxY, 1600f, .5f),
              $"框 ({csBox.MinX:F1},{csBox.MinY:F1})..({csBox.MaxX:F1},{csBox.MaxY:F1})");

        // ---- 波浪线（2026-09-20 第十六批，用户："还有一个另外的很多周期的波浪的弦函数线"）----
        // 和正弦是**同一条曲线**，差别只在**这一拖管什么**：正弦的框宽 = 一个周期、
        // 波浪线的框宽 = **要画多长**（周期由振幅定：**`T = 1 × A`** —— 用户 2026-09-20 定的
        // "振幅 = 一个周期"，见 WavePeriodPerAmplitude）。
        // 所以下面这几条就是**"多周期"这件事的正面断言**：
        //   · 拖得宽 → 周期数跟着涨，而 **T 和 A 一个字不变**（波形不会被拉变形）；
        //   · 拖得不足一个周期 → 紧框跟着收（峰还没到，不能撑到 ±A）。
        var wv = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv.SetWaveBox(300f, 1000f, 600f, 900f, minAxis);          // 300 宽、A = 100
        Check("波浪线：A = 100、**T = 1×A = 100**、画 300 宽 → 3 个整周期（±0.5）",
              Near(wv.WaveAmplitudeLocal(), 100f, .5f)
              && Near(wv.WavePeriodLocal(), 100f, .5f)
              && Near(wv.WaveLengthLocal(), 300f, .5f)
              && Near(wv.WaveCyclesLocal(), 3f, .02f),
              $"A {wv.WaveAmplitudeLocal():F1}  T {wv.WavePeriodLocal():F1}"
              + $"  长 {wv.WaveLengthLocal():F1}  周期数 {wv.WaveCyclesLocal():F2}（期望 3.00）");
        // **拖宽一倍**：周期数翻倍，而波形一个字不变（"画多长画多长"这件事的正面断言）。
        var wv2 = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv2.SetWaveBox(300f, 1000f, 900f, 900f, minAxis);        // 同样 A = 100、宽一倍（600）
        Check("波浪线：**同一个高度拖宽一倍（300 → 600）→ 周期数翻倍（3.0 → 6.0）**，"
              + "而 T 和 A 一个字没变（不变形）",
              Near(wv2.WaveCyclesLocal(), 6f, .02f)
              && Near(wv2.WaveAmplitudeLocal(), 100f, .5f)
              && Near(wv2.WavePeriodLocal(), 100f, .5f),
              $"A {wv2.WaveAmplitudeLocal():F1}  T {wv2.WavePeriodLocal():F1}"
              + $"  周期数 {wv2.WaveCyclesLocal():F2}（期望 6.00）");
        Check("波浪线：宽一倍的紧框 = 长度也翻倍、**上下两条线还是 ±A**（波形没被拉扁）",
              Near(wv2.CurveBoxLocal().MinX, 300f, .5f) && Near(wv2.CurveBoxLocal().MaxX, 900f, .5f)
              && Near(wv2.CurveBoxLocal().MinY, 900f, .5f) && Near(wv2.CurveBoxLocal().MaxY, 1100f, .5f),
              $"框 ({wv2.CurveBoxLocal().MinX:F1},{wv2.CurveBoxLocal().MinY:F1})"
              + $"..({wv2.CurveBoxLocal().MaxX:F1},{wv2.CurveBoxLocal().MaxY:F1})");
        // **拖得短**（不足一个周期）也要能画：那时紧框不该撑到 ±A（峰还没到）
        var wv3 = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv3.SetWaveBox(300f, 1000f, 320f, 900f, minAxis);         // 长 20、A = 100 → 0.2 个周期
        Check("波浪线：拖得**不足一个周期**时紧框跟着收（不撑到 ±A，峰根本还没到）（±0.5）",
              Near(wv3.WaveCyclesLocal(), 0.2f, .01f)
              && Near(wv3.CurveBoxLocal().MinY, 1000f - 100f * MathF.Sin(MathF.Tau * 0.2f), .5f)
              && wv3.CurveBoxLocal().MinY > 900.5f,
              $"周期数 {wv3.WaveCyclesLocal():F3}  框上沿 {wv3.CurveBoxLocal().MinY:F1}"
              + $"（期望 {1000f - 100f * MathF.Sin(MathF.Tau * 0.2f):F1}；"
              + "满峰才是 900 —— 这里峰还没到，框跟着收）");
        // 两种波浪**同一个框拖出来的东西不一样**（这是"分两格"的意义所在）：
        // 拿**同一个框**（正弦那一条：480 宽 × 100 高）比 —— 正弦是**一个周期**（T = 480）、
        // 波浪线是 **4.8 个周期**（T = 100 = A）。
        var wv4 = NewCurve(Tool.Wave, StrokeKind.Wave, 300f, 1000f);
        wv4.SetWaveBox(300f, 1000f, 780f, 900f, minAxis);         // 和上面 sn **一模一样的一拖**
        Check("正弦 / 波浪线**同一个框拖出来不是同一条**：T 480（一个周期）vs T 100（4.8 个周期）",
              Near(sn.WavePeriodLocal(), 480f, .5f) && Near(sn.WaveCyclesLocal(), 1f, .01f)
              && Near(wv4.WavePeriodLocal(), 100f, .5f) && Near(wv4.WaveCyclesLocal(), 4.8f, .02f),
              $"正弦 T {sn.WavePeriodLocal():F0}/周期数 {sn.WaveCyclesLocal():F2}、"
              + $"波浪线 T {wv4.WavePeriodLocal():F0}/周期数 {wv4.WaveCyclesLocal():F2}");

        // ---- 正切（2026-09-20 第十五批，用户："可以画正切"）----
        // 和正弦 / 余弦同族，但**一笔**：按下 = 原点（这一支的中心，也是图象与 x 轴的交点），
        // 拖出去 = **以它为中心的框**——横向 = 半支长（两条渐近线正好落在框的左右边）、
        // 纵向 = 可视半高（曲线冲到那儿就截断）。
        //
        // ⚠ 两条最要紧的：
        //   ① **它不该被拖变形**：横竖共用一个单位（`半支长 ↔ π/2`），所以框拖成什么形状，
        //      画出来的都是同一个正切（只是看得见的部分多少不同）；
        //   ② **可视半高有个下限**（`TangentMinAspect = 3`）：横竖既然同一个尺度，
        //      "看得见多高"就直接决定"两头离渐近线还有多远" —— 框太矮时曲线只画到一半就截断，
        //      看着是**一条斜线**（用户 2026-09-20 上手就是这个印象：他得故意拖得又高又窄才像）。
        //      所以引擎把高度顶到"贴着渐近线"的那个最小比例，**随手一拖就是课本的样子**。
        var tg = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg.SetTangentBox(300f, 2400f, 420f, 2540f, minAxis);      // 半支长 120、拖的半高只有 140
        Check("正切：两个定义元素（原点 ＋ 拖出去那个角），是**一笔**图形",
              tg.Points.Count == 2 && ShapeStepCount(Tool.Tangent) == 1,
              $"{tg.Points.Count} 个控制点、{ShapeStepCount(Tool.Tangent)} 笔"
              + "（期望 2 个 / 1 笔）");
        Check("正切：原点 = 按下点；半支长 120；可视半高**被顶到下限 360 = 3 × 120**（拖的是 140）（±0.5）",
              Near(tg.TangentOriginLocal().X, 300f, .5f) && Near(tg.TangentOriginLocal().Y, 2400f, .5f)
              && Near(tg.TangentHalfSpanLocal(), 120f, .5f)
              && Near(tg.TangentHalfHeightLocal(), 3f * 120f, .5f),
              $"原点 ({tg.TangentOriginLocal().X:F1},{tg.TangentOriginLocal().Y:F1})"
              + $"  半支长 {tg.TangentHalfSpanLocal():F1}  可视半高 {tg.TangentHalfHeightLocal():F1}"
              + $"（拖的 {MathF.Abs(tg.TangentEndLocal().Y - tg.TangentOriginLocal().Y):F1}）");
        // 单位 = 半支长 ÷ π/2 —— **这一条就是"横竖同一个尺度"的定义**（不变形的根）
        Check($"正切：横竖**共用一个单位**（半支长 120 ÷ π/2 ≈ {120f / (MathF.PI * .5f):F1}）（±0.5）",
              Near(tg.TangentUnitLocal(), 120f / (MathF.PI * 0.5f), .5f),
              $"{tg.TangentUnitLocal():F2}（期望 {120f / (MathF.PI * 0.5f):F2}）");
        Check("正切：θ=0 过原点、θ=π/4 降**一个单位**（tan 45° = 1）（±0.5）",
              Near(tg.TangentPointAtTheta(0f).X, 300f, .5f)
              && Near(tg.TangentPointAtTheta(0f).Y, 2400f, .5f)
              && Near(tg.TangentPointAtTheta(MathF.PI * .25f).Y, 2400f - tg.TangentUnitLocal(), .5f),
              $"θ=0 → ({tg.TangentPointAtTheta(0f).X:F1},{tg.TangentPointAtTheta(0f).Y:F1})"
              + $"  θ=π/4 → y {tg.TangentPointAtTheta(MathF.PI * .25f).Y:F1}"
              + $"（期望 {2400f - tg.TangentUnitLocal():F1}）");
        // 截断：曲线**冲到框的上下边就收笔**（框外的不画），所以首末两点的 y 正好是 ±半高
        Check("正切：一支的**首末两点正好落在框的上下边**（冲出框就截断）（±0.5）",
              Near(tg.TangentTracedPointAt(0f).Y, 2400f + 360f, .5f)
              && Near(tg.TangentTracedPointAt(1f).Y, 2400f - 360f, .5f)
              && tg.TangentTracedPointAt(0f).X < 300f && tg.TangentTracedPointAt(1f).X > 300f,
              $"端头 ({tg.TangentTracedPointAt(0f).X:F1},{tg.TangentTracedPointAt(0f).Y:F1})"
              + $" 和 ({tg.TangentTracedPointAt(1f).X:F1},{tg.TangentTracedPointAt(1f).Y:F1})");
        // **"贴着渐近线"的正面判据**：曲线两头停在离渐近线 13% 的地方（θ 走到 π/2 的 87%）
        // —— 这一条就是用户那句"很窄很高的时候才贴着渐近线"要做成默认行为的那件事。
        float tgGap = 1f - MathF.Abs(300f - tg.TangentTracedPointAt(1f).X) / tg.TangentHalfSpanLocal();
        Check("正切：两头**贴着渐近线**（只差 13% 的半支长；θ 走到 π/2 的 87%）（±0.02）",
              Near(tgGap, 0.133f, .02f)
              && Near(tg.TangentThetaMaxLocal() / (MathF.PI * .5f), 0.867f, .02f),
              $"离渐近线还差 {tgGap * 100f:F1}%（期望 13.3），"
              + $"θ 走到 π/2 的 {tg.TangentThetaMaxLocal() / (MathF.PI * .5f) * 100f:F1}%");
        // 两条渐近线：x = ±半支长（**正好落在框的左右边**）、上下从 −半高 到 ＋半高
        var (aFrom, aTo) = tg.TangentAsymptoteLocal(1);
        Check("正切：两条渐近线在 x = 原点 ± 半支长（就落在框的左右边上）（±0.5）",
              Near(tg.TangentAsymptoteLocal(-1).From.X, 180f, .5f)
              && Near(aFrom.X, 420f, .5f) && Near(aFrom.Y, 2400f - 360f, .5f)
              && Near(aTo.Y, 2400f + 360f, .5f),
              $"左 x {tg.TangentAsymptoteLocal(-1).From.X:F1}（期望 180）"
              + $"  右 x {aFrom.X:F1}（期望 420）  上下 {aFrom.Y:F1}..{aTo.Y:F1}（期望 2040..2760）");
        // 紧框 = **那个框本身**（不用采样求极值：曲线的范围就是 ±半支长 / ±半高）
        var tgBox = tg.CurveBoxLocal();
        Check("正切：紧框 = 那个框本身 (180,2040)..(420,2760)（±0.5）",
              Near(tgBox.MinX, 180f, .5f) && Near(tgBox.MaxX, 420f, .5f)
              && Near(tgBox.MinY, 2400f - 360f, .5f) && Near(tgBox.MaxY, 2400f + 360f, .5f),
              $"框 ({tgBox.MinX:F1},{tgBox.MinY:F1})..({tgBox.MaxX:F1},{tgBox.MaxY:F1})");
        // == 不变形：**同一个比例放大一倍** → 曲线逐点也放大一倍（形状一个字不变） ==
        // 这条比"看着像"硬得多：它证的是横竖**同一个**单位，也就是说
        // "框拖成什么形状都一样，画出来的还是那个正切"（少了这条，"拖宽就拉平"会溜过去）。
        var tg2 = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg2.SetTangentBox(300f, 2400f, 300f + 240f, 2400f + 280f, minAxis);   // 比例相同、放大一倍
        bool tgScaleOk = true; string tgScaleNote = "";
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            var pa = tg.TangentTracedPointAt(t);
            var pb = tg2.TangentTracedPointAt(t);
            if (!Near(pb.X - 300f, (pa.X - 300f) * 2f, .5f)
                || !Near(pb.Y - 2400f, (pa.Y - 2400f) * 2f, .5f))
            {
                tgScaleOk = false;
                tgScaleNote = $"t={t:F2}：(相对原点) ({pa.X - 300f:F1},{pa.Y - 2400f:F1})"
                            + $" → ({pb.X - 300f:F1},{pb.Y - 2400f:F1})（期望 ×2）";
                break;
            }
        }
        Check("正切：框**按同一比例放大一倍** → 曲线逐点放大一倍（拖不变形）", tgScaleOk,
              tgScaleOk ? "5 个采样点全是 ×2（形状一个字没变）" : tgScaleNote);
        // 拖得**又宽又矮**（480 × 70）：高度照样被顶到 3 倍下限，于是画出来的还是
        // "贴着渐近线的那一支"——**这就是这条下限存在的理由**（不然它是一条斜线）。
        // 注意 θ 的上限和半支长**无关**（`tan θmax = 3·π/2`），所以它和 tg 走到同一个 θ。
        var tg3 = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg3.SetTangentBox(300f, 2400f, 300f + 480f, 2400f + 70f, minAxis);   // 又宽又矮
        Check("正切：**又宽又矮**的框也被顶到同一条下限（θ 上限和 tg 一样、不走成斜线）",
              Near(tg3.CurveBoxLocal().MaxX - tg3.TangentOriginLocal().X, 480f, .5f)
              && Near(tg3.TangentHalfHeightLocal(), 3f * 480f, .5f)
              && Near(tg3.TangentThetaMaxLocal(), tg.TangentThetaMaxLocal(), .01f)
              && tg3.TangentThetaMaxLocal() < MathF.PI * .5f,
              $"半支长 480、拖的半高 70 → 实际半高 {tg3.TangentHalfHeightLocal():F0}、"
              + $"θ 上限 {tg3.TangentThetaMaxLocal():F3}（tg 是 {tg.TangentThetaMaxLocal():F3}）");
        // 反过来说：**拖得比下限更高就照你的来**（越高血压到越近）。
        var tg4 = NewCurve(Tool.Tangent, StrokeKind.Tangent, 300f, 2400f);
        tg4.SetTangentBox(300f, 2400f, 300f + 120f, 2400f + 700f, minAxis);   // 半支长 120、半高 700
        Check("正切：拖得**比下限更高**就照你的来（半高 700 > 360 → θ 上限更大、贴得更近）",
              Near(tg4.TangentHalfHeightLocal(), 700f, .5f)
              && tg4.TangentThetaMaxLocal() > tg.TangentThetaMaxLocal() + 0.05f,
              $"半支长 120、拖的半高 700 → θ 上限 {tg4.TangentThetaMaxLocal():F3} rad"
              + $"（下限那一档是 {tg.TangentThetaMaxLocal():F3}，π/2 是 {MathF.PI * .5f:F3}）");

        // ---- 圆柱 / 圆锥（2026-09-20 第五批：照 InkClass 的 case 6/7）----
        // 两个控制点就是**外接矩形**（拖到哪就是哪），椭圆由它派生：
        // `rx = 宽/2 = 200`、`ry = rx / 2.646 ≈ 75.59`。
        // 圆心从矩形上下边**往里缩一个 ry**，于是椭圆正好与矩形相切（范围好算、紧框不漏）。
        var cy = NewCurve(Tool.Cylinder, StrokeKind.Cylinder, 200f, 500f);
        cy.SetSolidBox(200f, 500f, 600f, 900f);
        var (ccx, cTop, cBot, crx, cry) = cy.SolidEllipsesLocal();
        Check("圆柱：外接矩形 = 拖出来的那个（200,500)..(600,900)（±0.5）",
              Near(cy.SolidRectLocal().X0, 200f, .5f) && Near(cy.SolidRectLocal().Y1, 900f, .5f),
              $"({cy.SolidRectLocal().X0:F0},{cy.SolidRectLocal().Y0:F0})"
              + $"..({cy.SolidRectLocal().X1:F0},{cy.SolidRectLocal().Y1:F0})");
        // 期望值**读引擎那个常量**，不在这里再抄一遍 2.646——写死的常数会随它改而静默失效
        //（仓库里为这类事栽过，见 计划-图形工具.md 的教训那几条）。
        Check($"圆柱：rx = 200、ry = rx × 扁率 ≈ {200f * Stroke.SolidEllipseRatio:F1}（照他的常数）（±0.5）",
              Near(crx, 200f, .5f) && Near(cry, 200f * Stroke.SolidEllipseRatio, .5f),
              $"rx {crx:F1}  ry {cry:F1}（期望 200 / {200f * Stroke.SolidEllipseRatio:F1}）");
        Check("圆柱：上下两个圆心从矩形边**往里缩一个 ry**（相切，不冒出去）（±0.5）",
              Near(ccx, 400f, .5f) && Near(cTop, 500f + cry, .5f) && Near(cBot, 900f - cry, .5f),
              $"cx {ccx:F1}  上圆心 y {cTop:F1}（期望 {500f + cry:F1}）"
              + $"  下圆心 y {cBot:F1}（期望 {900f - cry:F1}）");
        Check("圆柱：紧框 = 外接矩形本身（控制点外接就够，椭圆不会冒出去）（±0.5）",
              Near(cy.Bounds.MinX, 200f, .5f) && Near(cy.Bounds.MaxX, 600f, .5f)
              && Near(cy.Bounds.MinY, 500f, .5f) && Near(cy.Bounds.MaxY, 900f, .5f),
              $"框 ({cy.Bounds.MinX:F0},{cy.Bounds.MinY:F0})..({cy.Bounds.MaxX:F0},{cy.Bounds.MaxY:F0})");
        // 轮廓的**分段结构**：底面一圈 / 顶面一圈 / 两条母线（2026-09-20 补了母线——
        // 橡皮的粗筛按这份轮廓判"够不够得着"，少了母线就"明明画着线却擦不掉"）。
        var cyParts = Stroke.SplitOutlineParts(cy.ShapeOutline());
        Check("圆柱：轮廓分成 4 段（底圈 / 顶圈 / 两条母线），段与段之间靠抬笔标记隔开",
              cyParts.Count == 4 && cyParts[1].Count > 8 && cyParts[2].Count == 2 && cyParts[3].Count == 2,
              $"{cyParts.Count} 段，点数 {string.Join("/", cyParts.ConvertAll(p => p.Count))}"
              + "（期望 4 段：圈/圈/2/2）");

        var co = NewCurve(Tool.Cone, StrokeKind.Cone, 200f, 500f);
        co.SetSolidBox(200f, 500f, 600f, 900f);
        Check("圆锥：顶点 = 外接矩形的**上边中点** (400, 500)（照 InkClass）（±0.5）",
              Near(co.ConeApexLocal().X, 400f, .5f) && Near(co.ConeApexLocal().Y, 500f, .5f),
              $"({co.ConeApexLocal().X:F0},{co.ConeApexLocal().Y:F0}) 期望 (400,500)");

        // ---- 圆台（2026-09-20 第十三批，用户："再加一个圆台"）----
        // 它和圆柱 / 圆锥**共用同一份画法**（`Stroke.SolidPieces`：上底整圈 ＋ 下底下半圈 ＋
        // 两条母线，被挡的是下底上半圈），差别只在**上底小一圈**。所以这一节只钉那一点：
        // 上底的比例 / 相切、母线**往中间收**、轮廓还是 4 段。
        var cf = NewCurve(Tool.ConeFrustum, StrokeKind.ConeFrustum, 200f, 500f);
        cf.SetSolidBox(200f, 500f, 600f, 900f);
        var (fTop, frx, fry) = cf.ConeFrustumTopLocal();
        float fk = ShapeSpec.FrustumTopScale;
        Check($"圆台：上底 = 下底**同形缩 {fk:F2}**（两个半轴都乘，和棱台同一个数）（±0.5）",
              Near(frx, crx * fk, .5f) && Near(fry, cry * fk, .5f),
              $"上底 ({frx:F1}, {fry:F1})（期望 ({crx * fk:F1}, {cry * fk:F1})）");
        Check("圆台：上底与矩形上边**相切**（圆心往里缩的是 ry×比例，不是 ry）（±0.5）",
              Near(fTop.Y - fry, 500f, .5f),
              $"上底上沿 {fTop.Y - fry:F1}（期望 500 = 矩形上边）");
        var cfParts = Stroke.SplitOutlineParts(cf.ShapeOutline());
        Check("圆台：轮廓分 4 段（下底圈 / 上底圈 / 两条母线）——和圆柱同一个结构",
              cfParts.Count == 4 && cfParts[1].Count > 8 && cfParts[2].Count == 2 && cfParts[3].Count == 2,
              $"{cfParts.Count} 段，点数 {string.Join("/", cfParts.ConvertAll(p => p.Count))}"
              + "（期望 4 段：圈/圈/2/2）");
        // 母线**不是竖的**（是竖的就成圆柱了）；而且上端**正好落在上底左右端点**。
        Check("圆台：两条母线**往中间收**，上端落在上底左右端点（不是竖直的）",
              cfParts.Count == 4
              && Near(cfParts[2][0].X, 200f, .5f) && Near(cfParts[2][1].X, fTop.X - frx, .5f)
              && Near(cfParts[3][0].X, 600f, .5f) && Near(cfParts[3][1].X, fTop.X + frx, .5f)
              && cfParts[2][1].X > cfParts[2][0].X && cfParts[3][1].X < cfParts[3][0].X,
              cfParts.Count == 4
                  ? $"左 {cfParts[2][0].X:F0}→{cfParts[2][1].X:F0}、右 {cfParts[3][0].X:F0}→{cfParts[3][1].X:F0}"
                    + $"（期望 200→{fTop.X - frx:F0}、600→{fTop.X + frx:F0}）"
                  : "（轮廓没分成 4 段）");
        Check("圆台：紧框 = 外接矩形本身（上底缩小后不会冒出去）（±0.5）",
              Near(cf.Bounds.MinX, 200f, .5f) && Near(cf.Bounds.MaxX, 600f, .5f)
              && Near(cf.Bounds.MinY, 500f, .5f) && Near(cf.Bounds.MaxY, 900f, .5f),
              $"框 ({cf.Bounds.MinX:F0},{cf.Bounds.MinY:F0})..({cf.Bounds.MaxX:F0},{cf.Bounds.MaxY:F0})");

        // ---- 球（2026-09-20 第十四批，用户："在加入球"）----
        // 它有三件事和别的立体不同，都得钉住（不然"球"会退化成"圆"）：
        //   ① 投影永远是**圆**（取矩形里内切的圆，拖长了也不许画成扁球）；
        //   ② 必须画**赤道**那个椭圆（否则和「圆」那一格长得一样，看不出是个球）；
        //   ③ 赤道的近侧（下半圈）实线、远侧（上半圈）虚线。
        // 特意用**长方形** 400×200 来拖（不是正方形）：这样"内切正圆"这条才会真的被验到。
        var sp = NewCurve(Tool.Sphere, StrokeKind.Sphere, 200f, 500f);
        sp.SetSolidBox(200f, 500f, 600f, 700f);                      // 400×200
        var (spC, spR) = sp.SphereLocal();
        var (spEqC, spEqRx, spEqRy) = sp.SphereEquatorLocal();
        Check("球：投影是**内切正圆**——半径 = min(半宽, 半高) = 100（不是 200）、圆心 = 矩形中心（±0.5）",
              Near(spR, 100f, .5f) && Near(spC.X, 400f, .5f) && Near(spC.Y, 600f, .5f),
              $"半径 {spR:F1}（期望 100）、圆心 ({spC.X:F0},{spC.Y:F0})（期望 (400,600)）");
        Check("球：赤道椭圆 —— 长半轴 = 半径、圆心 = 球心、短半轴 = 半径 × 那个俯角（±0.5）",
              Near(spEqRx, spR, .5f) && Near(spEqRy, spR * Stroke.SolidEllipseRatio, .5f)
              && Near(spEqC.X, spC.X, .5f) && Near(spEqC.Y, spC.Y, .5f),
              $"赤道 ({spEqRx:F1}, {spEqRy:F1})（期望 ({spR:F1}, {spR * Stroke.SolidEllipseRatio:F1})），"
              + $"圆心 ({spEqC.X:F0},{spEqC.Y:F0})");
        Check("球：赤道**不是正圆**（短半轴 < 长半轴 —— 那个「从上往下看」的俯角）",
              spEqRy < spEqRx - 1f, $"短半轴 {spEqRy:F1} vs 长半轴 {spEqRx:F1}");
        var spParts = Stroke.SplitOutlineParts(sp.ShapeOutline());
        Check("球：轮廓分 2 段（外圈 / 赤道圈）——赤道的上下两半都要列进来（橡皮得够得着）",
              spParts.Count == 2 && spParts[0].Count > 8 && spParts[1].Count > 8,
              $"{spParts.Count} 段，点数 {string.Join("/", spParts.ConvertAll(p => p.Count))}"
              + "（期望 2 段：圈/圈）");
        Check("球：紧框 = **那个圆的外接方形**（圆心 ± 半径），不是拖出来的 400×200 矩形（±0.5）",
              Near(sp.Bounds.MinX, 300f, .5f) && Near(sp.Bounds.MaxX, 500f, .5f)
              && Near(sp.Bounds.MinY, 500f, .5f) && Near(sp.Bounds.MaxY, 700f, .5f),
              $"框 ({sp.Bounds.MinX:F0},{sp.Bounds.MinY:F0})..({sp.Bounds.MaxX:F0},{sp.Bounds.MaxY:F0})"
              + "（期望 (300,500)..(500,700)）");
        // 墨迹框（脏区/命中用）必须跟着**真实几何**走，不能是外接矩形——
        // 这和棱柱那次的"外接矩形上面漏一块"是同一类毛病，只是方向相反（这里是虚胖）。
        Check("球：墨迹框也按真实几何算（= 圆的外接方形 ＋ 半个笔宽，不是矩形 ＋ 半个笔宽）",
              Near(sp.WorldInkBounds.MaxY - sp.WorldInkBounds.MinY,
                   (500f - 300f) + sp.Width, 1.5f),
              $"墨迹框高 {sp.WorldInkBounds.MaxY - sp.WorldInkBounds.MinY:F1}"
              + $"（期望 {(500f - 300f) + sp.Width:F1} = 200 + 笔宽 {sp.Width:F1}）");

        // ---- 长方体 / 四面体（2026-09-20 第五批：照 InkClass 的 case 9/26，**两笔**）----
        // 长方体：第 1 笔正面矩形 (200,500)..(600,900)，第 2 笔拖到 y=650 → 深度 d = |500 − 650| = 150。
        var cu = NewCurve(Tool.Cuboid, StrokeKind.Cuboid, 200f, 500f);
        cu.SetCuboidFront(200f, 500f, 600f, 900f);
        cu.SetCuboidDepth(500f, 650f);
        Check("长方体：深度 = |正面上边 − 指针 y| = 150（照他的口径）（±0.5）",
              Near(cu.CuboidDepthLocal(), 150f, .5f), $"{cu.CuboidDepthLocal():F1}（期望 150）");
        Check("长方体：第 3 个控制点 = **背面右下角** (600+150, 900−150) = (750, 750)（±0.5）",
              Near(cu.CurvePointLocal(2).X, 750f, .5f) && Near(cu.CurvePointLocal(2).Y, 750f, .5f),
              $"({cu.CurvePointLocal(2).X:F0},{cu.CurvePointLocal(2).Y:F0}) 期望 (750,750)");
        Check("长方体：三个控制点（正面两角 ＋ 背面右下角）—— 多笔状态机要它凑齐才算画完",
              cu.Points.Count == 3 && Stroke.MinCurvePoints(StrokeKind.Cuboid) == 3,
              $"控制点 {cu.Points.Count} 个、MinCurvePoints = {Stroke.MinCurvePoints(StrokeKind.Cuboid)}");
        Check("长方体：紧框 = 正面 ∪ 背面（控制点外接正好就是画出来的范围）（±0.5）",
              Near(cu.Bounds.MinX, 200f, .5f) && Near(cu.Bounds.MaxX, 750f, .5f)
              && Near(cu.Bounds.MinY, 350f, .5f) && Near(cu.Bounds.MaxY, 900f, .5f),
              $"框 ({cu.Bounds.MinX:F0},{cu.Bounds.MinY:F0})..({cu.Bounds.MaxX:F0},{cu.Bounds.MaxY:F0})"
              + " 期望 (200,350)..(750,900)");

        // 四面体：第 1 笔外接矩形 (200,500)..(600,900) → 底面三角形（底边在下、顶点居中在上），
        // 第 2 笔拖到 (300, 200) = 顶点。
        var te = NewCurve(Tool.Tetrahedron, StrokeKind.Tetrahedron, 200f, 500f);
        te.SetTetraBase(200f, 500f, 600f, 900f);
        te.SetTetraApex(300f, 200f);
        Check("四面体：底面三角形 = 底左(200,900) / 底右(600,900) / 底顶(400,500)（照他）（±0.5）",
              Near(te.CurvePointLocal(0).X, 200f, .5f) && Near(te.CurvePointLocal(0).Y, 900f, .5f)
              && Near(te.CurvePointLocal(1).X, 600f, .5f) && Near(te.CurvePointLocal(1).Y, 900f, .5f)
              && Near(te.CurvePointLocal(2).X, 400f, .5f) && Near(te.CurvePointLocal(2).Y, 500f, .5f),
              $"({te.CurvePointLocal(0).X:F0},{te.CurvePointLocal(0).Y:F0}) / "
              + $"({te.CurvePointLocal(1).X:F0},{te.CurvePointLocal(1).Y:F0}) / "
              + $"({te.CurvePointLocal(2).X:F0},{te.CurvePointLocal(2).Y:F0})");
        Check("四面体：顶点 = 第 2 笔拖到的地方 (300, 200)（±0.5）",
              Near(te.TetraApexLocal().X, 300f, .5f) && Near(te.TetraApexLocal().Y, 200f, .5f),
              $"({te.TetraApexLocal().X:F0},{te.TetraApexLocal().Y:F0}) 期望 (300,200)");
        Check("四面体：四个控制点 ＋ 轮廓 = 六条棱（各段之间抬笔，不连出假线）",
              te.Points.Count == 4 && Stroke.MinCurvePoints(StrokeKind.Tetrahedron) == 4
              && te.ShapeOutline().Count == 6 * 2 + 5,
              $"控制点 {te.Points.Count} 个；轮廓 {te.ShapeOutline().Count} 点"
              + $"（6 段 × 2 点 ＋ 5 个抬笔 = 17）");

        // ================= ③ 手柄：四种曲线走**常规操作**（2026-09-20 第五批）========
        // 用户："抛物线、双曲线、正弦、余弦，把特殊点砍掉；通通按常规操作 ——
        //       给操作柄和旋转，和正常的一样。"
        Span<ShapeHandle> hs = stackalloc ShapeHandle[5];
        int nPb = SelectionHandles.ShapeHandlesOf(vb, hs);
        int nHy = SelectionHandles.ShapeHandlesOf(hy, hs);
        int nSn = SelectionHandles.ShapeHandlesOf(sn, hs);
        int nCs = SelectionHandles.ShapeHandlesOf(cs, hs);
        Check("手柄：四种曲线**一个特殊点都不给**（和矩形 / 立体图形一样，走通用框）",
              nPb == 0 && nHy == 0 && nSn == 0 && nCs == 0,
              $"抛物线 {nPb}、双曲线 {nHy}、正弦 {nSn}、余弦 {nCs}（都该是 0）");
        Check("手柄：四种曲线**都给旋转柄**（＝常规操作：缩放 ＋ 旋转 ＋ 平移）",
              SelectionHandles.RotateHandleVisible(vb) && SelectionHandles.RotateHandleVisible(hy)
              && SelectionHandles.RotateHandleVisible(sn) && SelectionHandles.RotateHandleVisible(cs),
              "抛物线 / 双曲线 / 正弦 / 余弦 都有旋转柄");
        Check("手柄：曲线仍然是**图形**（选中 / 移动 / 存档都走图形那一套）",
              Stroke.IsShapeKind(vb.Kind) && Stroke.IsShapeKind(hy.Kind)
              && Stroke.IsShapeKind(sn.Kind) && Stroke.IsShapeKind(cs.Kind),
              "四种曲线都在 IsShapeKind 里");

        // **通用八手柄真的给到了**（用户 2026-09-20 问"没有特殊点的图形……那八个是不是应该都给"）。
        //
        // 修的是一处**两头空**：Overlay 和 HitTest 原来都判"**是不是图形**"（`ShapeEditable`），
        // 而四种曲线 / 四个立体图形**是图形但没有特殊点** —— 于是既画不出特殊手柄、
        // 又拿不到通用八手柄，**画不出来也点不中**（真机上就只能整体拖，拉不动大小）。
        // 判据换成"**有没有特殊手柄**"之后，它们和矩形走同一条路。
        //
        // 这里对八个对象各点一次"框的右下角"：**点得中**才算给了（画的那一半靠出图看，
        // 拖的那一半由下面 ④ 验——那边还要求框**真的被拉大**，不然"整体挪一下"也能骗过去）。
        {
            var noSpecial = new (Stroke st, string name)[]
            {
                (vb, "抛物线"), (hy, "双曲线"), (sn, "正弦"), (cs, "余弦"),
                (cy, "圆柱"), (co, "圆锥"), (cf, "圆台"), (sp, "球"),
                (cu, "长方体"), (te, "四面体"),
            };
            int hitOk = 0;
            var missed = new List<string>();
            foreach (var (st, nm) in noSpecial)
            {
                var one = new[] { st };
                var f2 = SelectionHandles.FrameOf(one);
                var c2 = SelectionHandles.CanvasPosition(SelHandle.BottomRight, f2, DpiScale);
                if (SelectionHandles.HitTest(c2.X, c2.Y, one, f2, DpiScale) == SelHandle.BottomRight)
                    hitOk++;
                else missed.Add(nm);
            }
            Check("手柄：四种曲线 ＋ 六个立体图形**都点得中通用框的角**（＝八个缩放柄真的给了）",
                  hitOk == noSpecial.Length,
                  $"点得中 {hitOk}/{noSpecial.Length}"
                  + (missed.Count > 0 ? $"，点不中：{string.Join("/", missed)}" : ""));
        }

        // ================= ④ 编辑：曲线走**通用框**（只动变换，不动几何）=================
        // 曲线不再有"拖这个点只改那个量"的入口——模型层那几个函数（SetParabola… /
        // SetHyperbola… / SetWave…）只在**画的时候**用一次（见 §19、§22）。
        // 这里钉住通用框那条路：拖角只改 `Transform`，**局部几何一个点都不动**，
        // 朝向 / p 这些"曲线自己的参数"也一个字不动（这正是"线宽不变"那条路的前提，
        // 见 Stroke.KeepsWidth）。
        Doc.Clear();
        Doc.AddStroke(vb);
        Doc.ClearHistory();
        Doc.SelectOnly(new[] { vb });
        var pbP0 = new Vector2(vb.Points[0].X, vb.Points[0].Y);
        var pbP1 = new Vector2(vb.Points[1].X, vb.Points[1].Y);
        float pbPBefore = vb.ParabolaPLocal();
        var pbAxisBefore = vb.EffectiveAxis;
        var vbFrame = SelectionHandles.FrameOf(Doc.Selected);
        var vbCorner = SelectionHandles.CanvasPosition(SelHandle.BottomRight, vbFrame, DpiScale);
        float vbW0 = vbFrame.CanvasAabb.MaxX - vbFrame.CanvasAabb.MinX;
        float vbH0 = vbFrame.CanvasAabb.MaxY - vbFrame.CanvasAabb.MinY;
        // 先确认"按的是**手柄**，不是框内的空白"——不确认的话，按下会被当成"整体拖动"，
        // 下面那条 `!Transform.IsIdentity` 照样成立（整体挪一下也改变换），**断言等于白写**：
        // 这正是这一条原来漏掉的那半（用户 2026-09-20 问出来的那件事）。
        Check("曲线拖通用框的角：按下去命中**右下角手柄**（不是框内空白 → 不是整体拖动）",
              SelectionHandles.HitTest(vbCorner.X, vbCorner.Y, Doc.Selected, vbFrame, DpiScale)
                  == SelHandle.BottomRight,
              $"{SelectionHandles.HitTest(vbCorner.X, vbCorner.Y, Doc.Selected, vbFrame, DpiScale)}"
              + "（期望 BottomRight）");
        bool vbTook = SelectionGestureForTest(vbCorner.X, vbCorner.Y);
        UpdateSelectionGestureForTest(vbCorner.X + 200f, vbCorner.Y + 120f);
        SettleFrames(40);
        EndSelectionGestureForTest();
        SettleFrames(60);
        Check("曲线拖通用框的角：**只改 Transform，几何点一个都没动**",
              vbTook && !vb.Transform.IsIdentity
              && vb.Points[0].X == pbP0.X && vb.Points[0].Y == pbP0.Y
              && vb.Points[1].X == pbP1.X && vb.Points[1].Y == pbP1.Y,
              $"接住={vbTook}，变换={(vb.Transform.IsIdentity ? "单位（没动）" : "变了")}，"
              + $"两个控制点 {(vb.Points[0].X == pbP0.X && vb.Points[0].Y == pbP0.Y ? "原样" : "被改了！")}");
        // **框真的被拉大了**才算"八个缩放柄能用"：往右下角拖 200×120，
        // 框的宽高该跟着长——"整体挪一下"改的是位置、宽高一点不变，这一条能把它筛掉。
        var vbFrameAfter = SelectionHandles.FrameOf(Doc.Selected);
        float vbW1 = vbFrameAfter.CanvasAabb.MaxX - vbFrameAfter.CanvasAabb.MinX;
        float vbH1 = vbFrameAfter.CanvasAabb.MaxY - vbFrameAfter.CanvasAabb.MinY;
        Check("曲线拖通用框的角：框**真的被拉大了**（不是整体挪了一下）",
              vbW1 > vbW0 + 40f && vbH1 > vbH0 + 20f,
              $"框 {vbW0:F0}×{vbH0:F0} → {vbW1:F0}×{vbH1:F0}");
        Check("曲线拖通用框的角：**朝向 与 p 都不受影响**（它们是曲线自己的参数）",
              vb.EffectiveAxis == pbAxisBefore && Near(vb.ParabolaPLocal(), pbPBefore, .01f),
              $"{vb.EffectiveAxis}，p {vb.ParabolaPLocal():F1}（拖之前 {pbPBefore:F1}）");
        Doc.SelectOnly(Array.Empty<Stroke>());

        // ④b **四边中点也要能拉伸**（用户 2026-09-20："那八个点出现了，但是我看着只有对角线
        //     能拖动放缩？我觉得左右拉伸也可以给"）。
        //
        // 根因：`Top / Bottom / Left / Right` 这四个名字是**两用**的——对椭圆 / 坐标系 / 数轴
        // 它们是"定义元素"，对没有定义元素手柄的对象它们就是通用框的**四边中点缩放柄**。
        // 手势分流那里原来无条件把它们抹成 `None`，于是那类对象"四角能拉、四边中点只能整体拖"。
        // 这里拖**右边中点**：宽该变大、**高一个点不该变**（整体拖动是宽高都不变，
        // 等比缩放是宽高一起变——两头都能把"其实没在缩放"筛出来）。
        Doc.SelectOnly(new[] { vb });        // ④ 尾巴上把选择清空了，框要从"选中它"重新算
        var vbF2 = SelectionHandles.FrameOf(Doc.Selected);
        float vbW2a = vbF2.CanvasAabb.MaxX - vbF2.CanvasAabb.MinX;
        float vbH2a = vbF2.CanvasAabb.MaxY - vbF2.CanvasAabb.MinY;
        var vbMid = SelectionHandles.CanvasPosition(SelHandle.Right, vbF2, DpiScale);
        Check("曲线拖通用框：**右边中点**按下去命中的是缩放柄（不是框内空白）",
              SelectionHandles.HitTest(vbMid.X, vbMid.Y, Doc.Selected, vbF2, DpiScale) == SelHandle.Right,
              $"{SelectionHandles.HitTest(vbMid.X, vbMid.Y, Doc.Selected, vbF2, DpiScale)}（期望 Right）");
        bool vbTookMid = SelectionGestureForTest(vbMid.X, vbMid.Y);
        UpdateSelectionGestureForTest(vbMid.X + 200f, vbMid.Y);
        SettleFrames(40);
        EndSelectionGestureForTest();
        SettleFrames(60);
        var vbF3 = SelectionHandles.FrameOf(Doc.Selected);
        float vbW2b = vbF3.CanvasAabb.MaxX - vbF3.CanvasAabb.MinX;
        float vbH2b = vbF3.CanvasAabb.MaxY - vbF3.CanvasAabb.MinY;
        Check("曲线拖通用框的**右边中点**：横向拉长（宽变大、高基本不变）",
              vbTookMid && MathF.Abs(vbW2b - (vbW2a + 200f)) < 6f && MathF.Abs(vbH2b - vbH2a) < 2f,
              $"接住={vbTookMid}，框 {vbW2a:F0}×{vbH2a:F0} → {vbW2b:F0}×{vbH2b:F0}"
              + $"（期望 宽 {(vbW2a + 200f):F0}、高 {vbH2a:F0}）");
        // 判据自洽：**有**定义元素手柄的（直线）为真、**没有**的（曲线 / 立体图形 / 矩形）为假。
        // 三处调用点（Overlay 画手柄、HitTest 命中、手势分流认不认四边中点）问的都是它，
        // 所以它错了就是三处一起错——一条断言盯住三种成分。
        var vbLine = new Stroke { Tool = Tool.Line, Kind = StrokeKind.Line };
        vbLine.AddPoint(0f, 0f, 1f, 0);
        vbLine.AddPoint(10f, 0f, 1f, 0);
        var vbRect = new Stroke { Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle };
        vbRect.AddPoint(0f, 0f, 1f, 0);
        vbRect.AddPoint(10f, 10f, 1f, 0);
        Check("判据自洽：HasShapeHandles —— 直线 true，曲线 / 立体图形 / 矩形 false",
              SelectionHandles.HasShapeHandles(vbLine)
              && !SelectionHandles.HasShapeHandles(vb)
              && !SelectionHandles.HasShapeHandles(cy)
              && !SelectionHandles.HasShapeHandles(cu)
              && !SelectionHandles.HasShapeHandles(vbRect),
              "直线该 true；四种曲线 / 立体图形 / 矩形都该 false");
        Doc.SelectOnly(Array.Empty<Stroke>());

        // ⑤ 朝向：**选出来的**（不再靠拖动角度推）
        //
        // 这是 2026-09-20 这一版的核心改动：方向从"拖出来的"变成"选出来的"，
        // 于是"方向"和"大小"两个参数彻底分开、各管各的——用户的原话是
        // "开口的 4 种还要同时兼顾大小，这两个参数很不好调整"。
        Doc.Clear();
        var pb2 = NewCurve(Tool.Parabola, StrokeKind.Parabola, 500f, 500f);
        pb2.CurveAxis = CurveAxis.OpenUp;                        // 选：开口向上
        pb2.SetParabolaVertex(500f, 500f);
        pb2.SetParabolaThroughPoint(500f + 240f, 500f - 120f);   // s = 120、t = 240 → p = 240
        Doc.AddStroke(pb2);
        Doc.ClearHistory();
        Doc.SelectOnly(new[] { pb2 });
        Check("朝向：选「开口向上」→ 方向 = (0,−1)；p 由经过点反解 = 240（±1）",
              NearDir(pb2.ParabolaDirLocal(), new Vector2(0f, -1f)) && Near(pb2.ParabolaPLocal(), 240f, 1f),
              $"{pb2.ParabolaDirLocal()}，p {pb2.ParabolaPLocal():F0}");

        // 四个朝向各来一次：方向对得上，而且 **p 一点不变**（换朝向只转方向、不改形状——
        // 实现上是把"经过点"的 (s, t) 分量跟着新基重组，见 Stroke.SetParabolaAxis）。
        var axisCases = new (CurveAxis axis, Vector2 dir)[]
        {
            (CurveAxis.OpenUp, new Vector2(0f, -1f)),
            (CurveAxis.OpenDown, new Vector2(0f, 1f)),
            (CurveAxis.OpenRight, new Vector2(1f, 0f)),
            (CurveAxis.OpenLeft, new Vector2(-1f, 0f)),
        };
        bool axisOk = true, pKept = true;
        foreach (var (ax, dir) in axisCases)
        {
            pb2.SetParabolaAxis(ax);
            if (!NearDir(pb2.ParabolaDirLocal(), dir)) axisOk = false;
            if (!Near(pb2.ParabolaPLocal(), 240f, .5f)) pKept = false;
        }
        Check("朝向：四个方向切一圈 —— 方向都对得上，**p 始终是 240**（换朝向曲线不瘪）",
              axisOk && pKept, $"最后 {pb2.ParabolaDirLocal()}，p {pb2.ParabolaPLocal():F1}");
        // 坏值（比如读到别的族的值）要被归一成"开口向上"，不能带着它去算几何。
        // **在独立对象上试**：直接给 pb2 写个坏字段会把它的几何也带歪
        //（字段说"开口向上"、点位却是按旧朝向摆的），后面几条断言全跟着出错。
        var badAxis = NewCurve(Tool.Parabola, StrokeKind.Parabola, 2000f, 700f);
        badAxis.CurveAxis = CurveAxis.TransverseY;
        badAxis.SetParabolaVertex(2000f, 700f);
        Check("朝向：给到别的族的值 → 归一成**开口向上**（坏字节不许带进几何）",
              badAxis.EffectiveAxis == CurveAxis.OpenUp, $"{badAxis.EffectiveAxis}");
        pb2.SetParabolaAxis(CurveAxis.OpenUp);                   // 转回向上，下面接着用
        Check("朝向：转回向上之后仍然 p = 240（±0.5）",
              NearDir(pb2.ParabolaDirLocal(), new Vector2(0f, -1f)) && Near(pb2.ParabolaPLocal(), 240f, .5f),
              $"{pb2.ParabolaDirLocal()}，p {pb2.ParabolaPLocal():F1}");

        // 曲线参数由**画的那两笔**定，编辑时不再有"拖这个点"的入口（见 ③ / ④）。
        // 这里用一个**独立对象**直接调模型层那一份算式，验"经过点 → p 反解"这条口径没变
        //（画的时候用它，见 §19；拖手柄那条路已经删掉）——不碰 pb2，
        // 免得把下面"p 还是 240"那几条断言带歪。
        var pbParam = NewCurve(Tool.Parabola, StrokeKind.Parabola, 500f, 500f);
        pbParam.CurveAxis = CurveAxis.OpenUp;
        pbParam.SetParabolaVertex(500f, 500f);
        pbParam.SetParabolaThroughPoint(500f + 300f, 500f - 240f);   // s = 240、t = 300
        Check("参数：经过点 (300, −240) → p = t²/2s = 187.5（±2）",
              Near(pbParam.ParabolaPLocal(), 300f * 300f / 480f, 2f),
              $"p {pbParam.ParabolaPLocal():F1}（期望 {300f * 300f / 480f:F1}）");
        Check("参数：换经过点**不动朝向**（朝向是选出来的）",
              NearDir(pbParam.ParabolaDirLocal(), new Vector2(0f, -1f)), $"{pbParam.ParabolaDirLocal()}");

        // 换朝向这件事，2026-09-20 深夜**从操作条挪到了图形面板**（用户："选中框的抛物线按钮
        // 功能取消哦，我不打算从这个转抛物线开口"）：现在条上**没有「开口方向」这一格**，
        // 换朝向走的是 `Engine.CycleParabolaAxis`（画之前定，见 --shapebandtest 里那两条）。
        // ⚠ 数字 10 = 九格 ＋ **2026-09-22 加的第十格「图库」**（见 `SelBarButton` 和
        //   `BarButtonCount` 的注释）。这条早先写死 9，是那天之后**没跟着改**的遗留——
        //   正好又是"写死常量会随功能移位静默失效"那个坑（自检当场红，才没漏过去）。
        Check("操作条：没有「开口方向」那一格（九格 ＋ 图库 = 10）",
              SelectionHandles.BarButtonCount == 10,
              $"格子数 {SelectionHandles.BarButtonCount}");
        // 挪走的只是"入口"，能力本身没动：`Stroke.SetParabolaAxis` 仍然**保形状地转方向**
        //（这条前面④的四个朝向循环已经在验了，这里只补一句"入口换了、算式没换"）。
        pb2.SetParabolaAxis(CurveAxis.OpenRight);
        Check("朝向：入口换了之后，换朝向仍然**保形状**（p 不变）（±0.5）",
              pb2.EffectiveAxis == CurveAxis.OpenRight && Near(pb2.ParabolaPLocal(), 240f, .5f),
              $"{pb2.EffectiveAxis}，p {pb2.ParabolaPLocal():F1}（期望 240）");
        pb2.SetParabolaAxis(CurveAxis.OpenUp);
        SettleFrames(60);

        // 实轴沿 y 的双曲线：轮廓同样**不能有横穿两支之间的幽灵线段**。
        // 两步走来造：第一步拖出**渐近线框**（拖到哪就是哪：A = 300、B = 600 → 斜率 m = 2）；
        // 第二步拖一个"**比渐近线更竖**"的点 (200, 500)（m·|dx| = 400 < 500）
        // → 焦点落在 y 轴，也就是**上下双曲线**。
        var hy2 = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1600f, 500f);
        hy2.SetHyperbolaFromAsymptote(1600f, 500f, 1600f + 300f, 500f + 600f, minAxis);
        hy2.SetHyperbolaThroughPoint(1600f + 200f, 500f + 500f);
        Check("双曲线：第二步的点更竖 → **焦点在 y 轴**（上下双曲线），半轴 300 / 150（±0.5）",
              hy2.CurveAxis == CurveAxis.TransverseY
              && Near(hy2.HyperbolaRealLocal(), 300f, .5f) && Near(hy2.HyperbolaImagLocal(), 150f, .5f),
              $"{hy2.CurveAxis}，实半轴 {hy2.HyperbolaRealLocal():F1}、虚半轴 {hy2.HyperbolaImagLocal():F1}");
        // 这一步的**核心**：曲线必须**正好经过那个点**（不是"投影到实轴"那种近似）。
        // 实轴沿 y 时标准式是 `y²/b² − x²/a² = 1`，把 (200, 500) 代进去应当等于 1。
        // 式子里要用**曲线自己的** x / y 方向系数（`HyperbolaCurveALocal/BLocal`），
        // **不是**渐近线框的 `HyperbolaALocal/BLocal` —— 2026-09-20 拆开之后这两个别拿错。
        float c2A = hy2.HyperbolaCurveALocal(), c2B = hy2.HyperbolaCurveBLocal();
        float hy2Fit = 500f * 500f / (c2B * c2B) - 200f * 200f / (c2A * c2A);
        Check("双曲线：曲线**正好经过那个点**（y²/b² − x²/a² = 1）（±0.01）",
              Near(hy2Fit, 1f, .01f), $"代入得 {hy2Fit:F4}（期望 1）");
        var hy2Outline = hy2.ShapeOutline();
        float nearest2 = float.MaxValue;
        for (int i = 1; i < hy2Outline.Count; i++)
        {
            if (Stroke.IsOutlineBreak(hy2Outline[i - 1]) || Stroke.IsOutlineBreak(hy2Outline[i])) continue;
            nearest2 = MathF.Min(nearest2, DistPointSeg(new Vector2(1600f, 500f),
                                                        hy2Outline[i - 1], hy2Outline[i]));
        }
        Check("双曲线（实轴沿 y）：轮廓也没有幽灵线段（中心到任一段 ≥ 0.9a）",
              nearest2 >= hy2.HyperbolaRealLocal() * 0.9f,
              $"{hy2.CurveAxis}，最近距离 {nearest2:F1}（实半轴 {hy2.HyperbolaRealLocal():F1}）");
        var hy2Box = hy2.CurveBoxLocal();
        Check("双曲线（实轴沿 y）：紧框 = ±A × ±B（就是渐近线框）（±0.5）",
              Near(hy2Box.MinX, 1600f - 300f, .5f) && Near(hy2Box.MaxX, 1600f + 300f, .5f)
              && Near(hy2Box.MinY, 500f - 600f, .5f) && Near(hy2Box.MaxY, 500f + 600f, .5f),
              $"框 ({hy2Box.MinX:F1},{hy2Box.MinY:F1})..({hy2Box.MaxX:F1},{hy2Box.MaxY:F1})"
              + " 期望 (1300,-100)..(1900,1100)");
        var (asFrom2, asTo2) = hy2.HyperbolaAsymptoteLocal(1);
        Check("双曲线：**渐近线的端点就是紧框的角**（两个朝向都成立）（默认 ShowAsymptotes = 画）",
              hy2.ShowAsymptotes
              && Near(asTo2.X, 1600f + 300f, .5f) && Near(asTo2.Y, 500f + 600f, .5f)
              && Near(asTo2.X, hy2Box.MaxX, .5f) && Near(asTo2.Y, hy2Box.MaxY, .5f),
              $"({asFrom2.X:F1},{asFrom2.Y:F1})..({asTo2.X:F1},{asTo2.Y:F1})"
              + $" vs 框角 ({hy2Box.MaxX:F1},{hy2Box.MaxY:F1})");

        // ============ ⑤b 四种开口：**真机拖出来**（用户 2026-09-20 问"是不是应该有四种"）===
        //
        // 面板那一格只有**两档**（上下抛物 / 左右抛物），**具体朝哪边由那一拖的符号定**
        //（见 `Stroke.ParabolaAxisOfDrag`）——所以四种开口本来就是画得出来的，
        // 只是"朝上还是朝下"这件事面板替不了你回答（方向被面板选死时，
        // "选着向上、手却往下拖"会画出跟手指反过来的曲线）。
        //
        // 上面 ⑤ 只钉了**模型层**（直接调 `SetParabolaAxis`），这一段补**真机**：
        // 按住顶点往四个方向各拖一次，看落点那一条的朝向对不对。
        // 判据是 `EffectiveAxis`（读端统一走它，不是那个存下来的字段）。
        Console.WriteLine("  -- ⑤b. 四种开口：真机拖出来的方向 --");
        {
            Doc.Clear();
            Doc.ClearHistory();

            // 把"哪一对"切到指定档（面板那一格点第二下就是干这个的）。
            void SetParaPair(CurveAxis pair)
            {
                for (int k = 0; k < 2 && Host.State.ParabolaAxis != pair; k++)
                    Host.Commands.CycleParabolaAxis();
                SettleFrames(80);
            }
            // 按住顶点 (vx,vy) 拖到 (vx+dx, vy+dy)，松手就成一条抛物线。
            void DragParaAxis(float vx, float vy, float dx, float dy)
            {
                SendMouse((int)vx, (int)vy, 0);                                    SettleFrames(60);
                SendMouse((int)vx, (int)vy, Native.MOUSEEVENTF_LEFTDOWN);          SettleFrames(60);
                SendMouse((int)(vx + dx * 0.5f), (int)(vy + dy * 0.5f), 0);        SettleFrames(80);
                SendMouse((int)(vx + dx), (int)(vy + dy), 0);                      SettleFrames(160);
                SendMouse((int)(vx + dx), (int)(vy + dy), Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(200);
            }
            void SetParaTool()
            {
                SetToolFromUi(Tool.Parabola);
                SettleFrames(80);
            }

            float pvx = _virtualX + 700f, pvy = _virtualY + 1050f;
            // 四个方向都拖 (±300, ±300)：`s = t = 300` → `p = 300²/600 = 150`，一格装得下。
            var openCases = new (CurveAxis pair, float dx, float dy, CurveAxis want, string how)[]
            {
                (CurveAxis.OpenUp,     300f, -300f, CurveAxis.OpenUp,    "往上拖"),
                (CurveAxis.OpenUp,     300f,  300f, CurveAxis.OpenDown,  "往下拖"),
                (CurveAxis.OpenRight,  300f,  300f, CurveAxis.OpenRight, "往右拖"),
                (CurveAxis.OpenRight, -300f,  300f, CurveAxis.OpenLeft,  "往左拖"),
            };
            foreach (var oc in openCases)
            {
                Doc.Clear();
                Doc.ClearHistory();
                SetParaTool();
                SetParaPair(oc.pair);
                DragParaAxis(pvx, pvy, oc.dx, oc.dy);

                var ps = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
                Check($"抛物线·{oc.how}（面板「{(oc.pair == CurveAxis.OpenUp ? "上下" : "左右")}抛物」档）"
                      + $" → 开口 {oc.want}",
                      ps != null && ps.Kind == StrokeKind.Parabola && ps.EffectiveAxis == oc.want,
                      $"对象 {Doc.Strokes.Count} 条，"
                      + $"朝向 {(ps == null ? "（没画出来）" : ps.EffectiveAxis.ToString())}（期望 {oc.want}）");
            }
            Doc.Clear();
            Doc.ClearHistory();
            SetParaPair(CurveAxis.OpenUp);       // 收尾：给别人留下一档干净的（后面几段要用）
        }

        // ================= ⑤c. 正切：真机一笔拖出来 =================
        //
        // 上面那一段只钉了**模型层**（直接调 `SetTangentBox`）。这一段补**真机**：
        // 用户按下那一点、拖出那一笔，**引擎自己写出来的**是不是我们要的那份定义
        //（教训：自己 new 一个对象、字段自己填，**不算测**——那条路绕过了引擎）。
        //
        // 要盯的两个数：原点 = **按下那一点**（不是拖出去的那个角！正切和正弦在这点上
        // 正好相反：正弦把起手点留在原地当起点，正切是**以起手点为中心**往四周长）；
        // 半支长 / 可视半高 = 这一笔的 |dx| / |dy|。
        Console.WriteLine("  -- ⑤c. 正切：真机一笔拖出来（按下 = 原点）--");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Tangent);
            SettleFrames(80);

            float ox = _virtualX + 700f, oy = _virtualY + 1000f;
            SendMouse((int)ox, (int)oy, 0);                                        SettleFrames(60);
            SendMouse((int)ox, (int)oy, Native.MOUSEEVENTF_LEFTDOWN);              SettleFrames(60);
            SendMouse((int)(ox + 80f), (int)(oy + 90f), 0);                        SettleFrames(80);
            SendMouse((int)(ox + 160f), (int)(oy + 180f), 0);                      SettleFrames(160);
            SendMouse((int)(ox + 160f), (int)(oy + 180f), Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);

            var ts = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("正切·真机一笔：画出来的是**正切**一个对象",
                  ts != null && ts.Kind == StrokeKind.Tangent,
                  $"对象 {Doc.Strokes.Count} 条，种类 {(ts == null ? "（没画出来）" : ts.Kind.ToString())}");
            Check("正切·真机一笔：**原点就是按下那一点**（不是拖出去的那个角）（±2）",
                  ts != null && Near(ts.TangentOriginLocal().X, ox, 2f)
                  && Near(ts.TangentOriginLocal().Y, oy, 2f),
                  ts == null ? "（没画出来）"
                  : $"原点 ({ts.TangentOriginLocal().X:F0},{ts.TangentOriginLocal().Y:F0})"
                    + $"（按下的 ({ox:F0},{oy:F0})）");
            Check("正切·真机一笔：半支长 = |dx| = 160；可视半高**被顶到下限 480**（拖的是 180）（±2）",
                  ts != null && Near(ts.TangentHalfSpanLocal(), 160f, 2f)
                  && Near(ts.TangentHalfHeightLocal(), 3f * 160f, 2f),
                  ts == null ? "（没画出来）"
                  : $"半支长 {ts.TangentHalfSpanLocal():F0}、可视半高 {ts.TangentHalfHeightLocal():F0}");

            // 屏幕上也**真的看得见**（不是"只进了文档、没渲染"）：笔色是品红，数那一片的品红像素
            //（这一段的探针口径和上面几段一样，见 `CountMagenta`）。
            Doc.InvalidateAll();
            SettleFrames(200);
            int inkOn = ScreenProbe.CountMagenta((int)(ox - 180f), (int)(oy - 560f), 360, 1120);
            Check("正切·真机一笔：屏幕上真有墨（不是只进了文档没画）", inkOn > 300,
                  $"{inkOn} 个品红像素（门槛 300）");
        }

        // ================= ⑤d. 波浪线：真机一笔拖出来（拖多长画多长） =================
        //
        // 这一格和「正弦」的差别全在"这一拖管什么"上，所以真机这一笔要盯住的就一件事：
        // **横向拖多远 → 画出来几个周期**（`框宽 ÷ 振幅`，因为用户定的"振幅 = 一个周期"），
        // 而不是"拖出来的框就是一个周期"。
        Console.WriteLine("  -- ⑤d. 波浪线：真机一笔拖出来（拖多长画多长）--");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Wave);
            SettleFrames(80);

            float wx = _virtualX + 700f, wy = _virtualY + 900f;
            SendMouse((int)wx, (int)wy, 0);                                        SettleFrames(60);
            SendMouse((int)wx, (int)wy, Native.MOUSEEVENTF_LEFTDOWN);              SettleFrames(60);
            SendMouse((int)(wx + 150f), (int)(wy - 50f), 0);                       SettleFrames(80);
            SendMouse((int)(wx + 300f), (int)(wy - 100f), 0);                      SettleFrames(160);
            SendMouse((int)(wx + 300f), (int)(wy - 100f), Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);

            var ws = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("波浪线·真机一笔：画出来的是**波浪线**一个对象",
                  ws != null && ws.Kind == StrokeKind.Wave,
                  $"对象 {Doc.Strokes.Count} 条，种类 {(ws == null ? "（没画出来）" : ws.Kind.ToString())}");
            Check("波浪线·真机一笔：A = 100、T = 100、画 300 宽 → **3 个整周期**（±2）",
                  ws != null && Near(ws.WaveAmplitudeLocal(), 100f, 2f)
                  && Near(ws.WavePeriodLocal(), 100f, 2f)
                  && Near(ws.WaveCyclesLocal(), 3f, .02f),
                  ws == null ? "（没画出来）"
                  : $"A {ws.WaveAmplitudeLocal():F0}  T {ws.WavePeriodLocal():F0}"
                    + $"  周期数 {ws.WaveCyclesLocal():F2}（期望 3.00）");
            Check("波浪线·真机一笔：两个定义元素（起点 ＋ 终点，极值点是现推的）",
                  ws != null && ws.Points.Count == 2,
                  $"{ws?.Points.Count ?? -1} 个控制点（期望 2）");
        }

        // ================= ⑤e. 椭圆（带焦点）：真机一笔 ＋ 焦点 ＋ 拖 P =================
        //
        // 用户 2026-09-22 定的口径：
        //   · 第二行加一格**椭圆（带焦点）**（和第一行那个"纯椭圆"**并存**，两个工具）；
        //   · **两档**：有焦点三角形 / 没有焦点三角形——**两个焦点两档都画**，
        //     "无"那一档只是**不连** F₁P、F₂P（原话："还是画焦点，只是不连三角形"）；
        //   · 焦点三角形的**顶点 P 在椭圆上、可以拖着走**。
        //
        // 所以这一段盯五件事（第 ③/④ 条是硬判据，不是"看起来对"）：
        //   ① 真机一笔画出来的是它；② 两个焦点满足 `c² = a² − b²`、在长轴上；
        //   ③ **P 一定在椭圆上**——用椭圆的定义判：`|PF₁| + |PF₂| = 2a`（比"反解参数角"硬得多）；
        //   ④ 拖 P：角变了、"和"照样 2a、**一步撤销回得去**；
        //   ⑤ 两档：有三角形时多一笔、无三角形时它没了**但两个焦点还在**，
        //      而且都不走虚线辅助槽（`Aux == false`，焦点三角形是主体不是辅助线）。
        Console.WriteLine("  -- ⑤e. 椭圆（带焦点）：真机一笔 ＋ 焦点三角形 ＋ 拖 P --");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.ConicEllipse);
            // 归到"有焦点三角形"那一档：不写死"进来时一定是默认档"（前面几段可能点过它）。
            for (int k = 0; k < 4 && !Host.State.EllipseFocusTriangle; k++)
                Host.Commands.CycleEllipseFocusTriangle();
            SettleFrames(80);

            // 按下 = **中心**、拖出去 = **外角点**（和第一行那个椭圆同一套定义）：
            // 这里拖 260 × 120 → a = 260、b = 120，于是 c = √(260² − 120²) ≈ 230.65。
            float ex = _virtualX + 1100f, ey = _virtualY + 1000f;
            const float wa = 260f, wb = 120f;
            SendMouse((int)ex, (int)ey, 0);                                        SettleFrames(60);
            SendMouse((int)ex, (int)ey, Native.MOUSEEVENTF_LEFTDOWN);              SettleFrames(60);
            SendMouse((int)(ex + wa), (int)(ey + wb / 2f), 0);                     SettleFrames(80);
            SendMouse((int)(ex + wa), (int)(ey + wb), 0);                          SettleFrames(160);
            SendMouse((int)(ex + wa), (int)(ey + wb), Native.MOUSEEVENTF_LEFTUP);  SettleFrames(250);

            var ce = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("带焦点椭圆·真机一笔：画出来的是**椭圆（带焦点）**一个对象",
                  ce != null && ce.Kind == StrokeKind.ConicEllipse,
                  $"对象 {Doc.Strokes.Count} 条，种类 {(ce == null ? "（没画出来）" : ce.Kind.ToString())}");
            if (ce == null)
            {
                Console.WriteLine("  （后面几项没法验）");
            }
            else
            {
                Check("带焦点椭圆：两个定义元素（中心 ＋ 外角点，和第一行那个椭圆同一套）",
                      ce.Points.Count == 2, $"{ce.Points.Count} 个控制点（期望 2）");
                Check("带焦点椭圆：中心 = 按下那一点、a = 260、b = 120（±2）",
                      Near(ce.ShapeCenterLocal.X, ex, 2f) && Near(ce.ShapeCenterLocal.Y, ey, 2f)
                      && Near(ce.SemiAxisALocal, wa, 2f) && Near(ce.SemiAxisBLocal, wb, 2f),
                      $"中心 ({ce.ShapeCenterLocal.X:F0},{ce.ShapeCenterLocal.Y:F0})"
                      + $"（按下 ({ex:F0},{ey:F0})）、a {ce.SemiAxisALocal:F0}、b {ce.SemiAxisBLocal:F0}");

                // ---- ② 两个焦点：c² = a² − b²，且落在**长轴**上 ----
                float cc = MathF.Sqrt(wa * wa - wb * wb);                   // ≈ 230.65
                var (f1, f2) = ce.ConicEllipseFociLocal();
                Check($"带焦点椭圆：焦点在长轴上、c = √(a²−b²) ≈ {cc:F0}（±2）",
                      Near(f1.X, ex - cc, 2f) && Near(f1.Y, ey, 2f)
                      && Near(f2.X, ex + cc, 2f) && Near(f2.Y, ey, 2f),
                      $"F1 ({f1.X:F0},{f1.Y:F0})、F2 ({f2.X:F0},{f2.Y:F0})（期望 y = {ey:F0}，x = ∓{cc:F0}）");

                // ---- ③ P 在椭圆上：|PF₁| + |PF₂| = 2a（椭圆的定义） ----
                // 这是"P 一定在椭圆上"最硬的一条判据：它不依赖我们对参数角的理解对不对。
                float SumOfDists(Vector2 p) => Vector2.Distance(p, f1) + Vector2.Distance(p, f2);
                var p0 = ce.ConicEllipsePointLocal();
                Check($"带焦点椭圆：默认的 P 在椭圆上（|PF₁|+|PF₂| = 2a = {2 * wa:F0}）（±1）",
                      Near(SumOfDists(p0), 2f * wa, 1f),
                      $"和 = {SumOfDists(p0):F1}（期望 {2 * wa:F0}）");
                // 默认角是**左上方**（120°，见 Stroke.DefaultFocusPointU）：
                // 用户 2026-09-22 的口径"焦点三角形那个顶点……弄在椭圆上面"。
                Check("带焦点椭圆：默认的 P 在椭圆的**左上方**（120° 那个点）",
                      Near(p0.X, ex + wa * MathF.Cos(Stroke.DefaultFocusPointU), 2f)
                      && Near(p0.Y, ey + wb * MathF.Sin(Stroke.DefaultFocusPointU), 2f),
                      $"P ({p0.X:F0},{p0.Y:F0})（期望 ({ex + wa * MathF.Cos(Stroke.DefaultFocusPointU):F0},"
                      + $"{ey + wb * MathF.Sin(Stroke.DefaultFocusPointU):F0})）");
                Check("带焦点椭圆：默认的 P 不跟两个焦点共线（三角形不会退化成一条线段）",
                      MathF.Abs(p0.Y - ey) > 1f && MathF.Abs(p0.X - ex) > 1f,
                      $"P 相对中心 ({p0.X - ex:F0},{p0.Y - ey:F0})，焦点在 y = {ey:F0} 这条线上");
                Check("带焦点椭圆：默认档 = **有焦点三角形**",
                      ce.FocusTriangle, $"FocusTriangle = {ce.FocusTriangle}");

                // ---- ⑤ 两档：折线笔数（① 椭圆 ②③ 三角形的两条边 ④⑤ 两个焦点），且都不走辅助槽 ----
                int Pieces() => ce.InkPieces().Count(pc => pc.Pts.Count >= 2);
                int AuxPieces() => ce.InkPieces().Count(pc => pc.Aux);
                int piecesWithTri = Pieces();
                Check("带焦点椭圆·有三角形档：画出来 5 笔（椭圆 ＋ 两条边 ＋ 两个焦点小圆点）",
                      piecesWithTri == 5, $"{piecesWithTri} 笔（期望 5）");
                Check("带焦点椭圆：**一笔都不走虚线辅助槽**（焦点三角形是主体，不是辅助线）",
                      AuxPieces() == 0, $"{AuxPieces()} 笔是 Aux（期望 0）");

                ce.SetFocusTriangle(false);
                int piecesNoTri = Pieces();
                Check("带焦点椭圆·无三角形档：少一笔（椭圆 ＋ 两个焦点小圆点 = 3 笔）、焦点还在",
                      piecesNoTri == 3 && AuxPieces() == 0, $"{piecesNoTri} 笔（期望 3）");
                ce.SetFocusTriangle(true);                     // 后面的断言接着按"有"那一档走

                // ---- ④ 拖 P：模型里的角变了、"和"照样 2a、一步撤销回得去 ----
                Doc.SelectOnly(new[] { ce });
                SettleFrames(300);
                var pHandleFrom = SelectionHandles.ShapeHandleCanvasPosition(ce, ShapeHandle.FocusPoint);
                // 目标：把 P 拖到"参数角 45°"那个方向上的点（椭圆上那一点：
                // center + (a·cos45°, b·sin45°) —— 注意**拖到哪儿都行**，模型会把方向折成角）。
                var pTarget = new Vector2(ex + wa * 0.70711f, ey + wb * 0.70711f);
                bool tookP = SelectionGestureForTest(pHandleFrom.X, pHandleFrom.Y);
                UpdateSelectionGestureForTest(pTarget.X, pTarget.Y);
                SettleFrames(140);
                EndSelectionGestureForTest();
                SettleFrames(250);

                Check("带焦点椭圆·拖 P：真的抓住了那个圆点（手柄在 P 上）",
                      tookP, $"接住 = {tookP}");
                Check("带焦点椭圆·拖 P：角变成 45°（±0.03 弧度）",
                      !float.IsNaN(ce.FocusPointU) && Near(ce.FocusPointU, MathF.PI / 4f, .03f),
                      $"u = {(float.IsNaN(ce.FocusPointU) ? "NaN（没写进去）" : ce.FocusPointU.ToString("F4"))}"
                      + $"（期望 {MathF.PI / 4f:F4}）");
                var p1 = ce.ConicEllipsePointLocal();
                Check($"带焦点椭圆·拖 P 之后：P **还在椭圆上**（|PF₁|+|PF₂| = 2a = {2 * wa:F0}）（±1）",
                      Near(SumOfDists(p1), 2f * wa, 1f),
                      $"P ({p1.X:F0},{p1.Y:F0})，和 = {SumOfDists(p1):F1}");
                Check("带焦点椭圆·拖 P：**椭圆本身一动没动**（只有那个角变了）",
                      Near(ce.SemiAxisALocal, wa, 2f) && Near(ce.SemiAxisBLocal, wb, 2f),
                      $"a {ce.SemiAxisALocal:F0}、b {ce.SemiAxisBLocal:F0}");

                Doc.Undo();
                SettleFrames(200);
                Check("带焦点椭圆·拖 P：一步撤销回到「还没拖过」的状态（P 回默认那一端）",
                      float.IsNaN(ce.FocusPointU)
                      || Near(ce.ConicEllipsePointLocal().X, p0.X, 2f)
                      && Near(ce.ConicEllipsePointLocal().Y, p0.Y, 2f),
                      $"FocusPointU = {(float.IsNaN(ce.FocusPointU) ? "NaN（自动）" : ce.FocusPointU.ToString("F4"))}"
                      + $"，P = ({ce.ConicEllipsePointLocal().X:F0},{ce.ConicEllipsePointLocal().Y:F0})"
                      + $"（默认 ({p0.X:F0},{p0.Y:F0})）");
                Doc.Selected.Clear();
                SettleFrames(120);
            }
        }

        // ================= ⑥ 存档：往返 ＋ 真 v11 老文件 =================
        Doc.Clear();
        Doc.ClearHistory();
        var saveMe = NewCurve(Tool.Parabola, StrokeKind.Parabola, 700f, 700f);
        saveMe.CurveAxis = CurveAxis.OpenLeft;                             // 选：开口向左
        saveMe.SetParabolaVertex(700f, 700f);
        saveMe.SetParabolaThroughPoint(560f, 420f);                        // s = 140、t = 280 → p = 280
        var saveHy = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1400f, 700f);
        // 两步：第一步拖出**渐近线框**（A = 160、B = 320 → 斜率 2），
        // 第二步拖一个更竖的点 → 焦点在 y 轴（上下双曲线）。
        saveHy.SetHyperbolaFromAsymptote(1400f, 700f, 1560f, 1020f, minAxis);
        saveHy.SetHyperbolaThroughPoint(1400f + 100f, 700f + 250f);
        var saveSin = NewCurve(Tool.Sine, StrokeKind.Sine, 300f, 700f);
        saveSin.SetWaveBox(300f, 700f, 780f, 600f, minAxis);
        // 正切（v21 新增的取值）：一笔，存的就是"原点 ＋ 那个角"，形状全由这两个点派生。
        // ⚠ 存的是**拖出来的那个角**（180），画的时候半高会被下限顶到 480 —— 两个都要读回来。
        var saveTan = NewCurve(Tool.Tangent, StrokeKind.Tangent, 900f, 1200f);
        saveTan.SetTangentBox(900f, 1200f, 1060f, 1380f, minAxis);          // 半支长 160、拖的半高 180
        // 波浪线（v22 新增的取值）：和正弦同一套"起点 ＋ 终点"，差别只是**这一拖管什么**。
        var saveWave = NewCurve(Tool.Wave, StrokeKind.Wave, 900f, 1700f);
        saveWave.SetWaveBox(900f, 1700f, 1200f, 1600f, minAxis);            // A = 100 → 3 个周期
        // 椭圆（带焦点）（v23 新增的取值 ＋ 两个新字段）：存"**无焦点三角形**那一档 ＋
        // 一个**拖过的 P**"，这样两个字段都被真正走到（不是靠默认值蒙过去）。
        var saveCe = NewCurve(Tool.ConicEllipse, StrokeKind.ConicEllipse, 400f, 1200f);
        saveCe.SetEnd(660f, 1320f);                                        // a = 260、b = 120
        saveCe.FocusTriangle = false;
        saveCe.SetConicEllipsePointAngle(MathF.PI / 3f);                    // 60°（不用默认值）
        Doc.AddStroke(saveMe);
        Doc.AddStroke(saveHy);
        Doc.AddStroke(saveSin);
        Doc.AddStroke(saveTan);
        Doc.AddStroke(saveWave);
        Doc.AddStroke(saveCe);

        var blob = InkSerializer.Save(Doc);
        var back = new InkDocument();
        InkSerializer.LoadInto(back, blob);
        bool roundTrip = back.Strokes.Count == 6
                         && back.Strokes[0].Kind == StrokeKind.Parabola
                         && Near(back.Strokes[0].ParabolaDirLocal().X, -1f, .01f)
                         && back.Strokes[1].Kind == StrokeKind.Hyperbola
                         && back.Strokes[1].CurveAxis == CurveAxis.TransverseY
                         && back.Strokes[1].ShowAsymptotes
                         && back.Strokes[2].Kind == StrokeKind.Sine
                         && back.Strokes[3].Kind == StrokeKind.Tangent
                         && Near(back.Strokes[3].TangentHalfSpanLocal(), 160f, .5f)
                         && Near(back.Strokes[3].TangentHalfHeightLocal(), 3f * 160f, .5f)
                         && back.Strokes[4].Kind == StrokeKind.Wave
                         && Near(back.Strokes[4].WaveCyclesLocal(), 3f, .02f)
                         && back.Strokes[5].Kind == StrokeKind.ConicEllipse
                          && Near(back.Strokes[5].SemiAxisALocal, 260f, .5f)
                          && Near(back.Strokes[5].SemiAxisBLocal, 120f, .5f)
                          && !back.Strokes[5].FocusTriangle
                          && Near(back.Strokes[5].FocusPointU, MathF.PI / 3f, .001f);
        Check("存档：五种曲线 ＋ 椭圆（带焦点）＋ 朝向 ＋ 渐近线开关都回来了（含 v21 正切 / v22 波浪线 / v23 椭圆）",
              roundTrip,
              back.Strokes.Count == 5
                  ? $"{back.Strokes[0].Kind}/方向 {back.Strokes[0].ParabolaDirLocal()}、"
                    + $"{back.Strokes[1].Kind}/{back.Strokes[1].CurveAxis}/渐近线 {back.Strokes[1].ShowAsymptotes}、"
                    + $"{back.Strokes[2].Kind}、"
                    + $"{back.Strokes[3].Kind}（半支长 {back.Strokes[3].TangentHalfSpanLocal():F0}）、"
                    + $"{back.Strokes[4].Kind}（周期数 {back.Strokes[4].WaveCyclesLocal():F2}）"
                  : back.Strokes.Count == 6
                    ? $"{back.Strokes[5].Kind}（焦点三角形 {back.Strokes[5].FocusTriangle}、"
                      + $"P 的角 {back.Strokes[5].FocusPointU:F4} —— 期望 False / {MathF.PI / 3f:F4}）"
                    : $"只读回 {back.Strokes.Count} 条");

        // 真·v22 老文件：**一条带焦点椭圆**的末尾少 5 个字节（v23 新增的那两位：
        // 1 字节开关 ＋ 4 字节 float）。读端必须退到"画三角形 ＋ P 还没定过（NaN）"。
        // `MakeLegacyFile` 是"从整个文件末尾砍 N 个字节"，所以这里只能是**单条对象**的文件。
        var legacyCeDoc = new InkDocument();
        var legacyCe = NewCurve(Tool.ConicEllipse, StrokeKind.ConicEllipse, 600f, 900f);
        legacyCe.SetEnd(860f, 1020f);
        legacyCeDoc.AddStroke(legacyCe);
        var oldCe = new InkDocument();
        InkSerializer.LoadInto(oldCe, MakeLegacyFile(legacyCeDoc, 22, 5));
        Check("存档：真 v22 老文件读得进来（1 条对象）", oldCe.Strokes.Count == 1, $"{oldCe.Strokes.Count} 条");
        if (oldCe.Strokes.Count == 1)
        {
            var oc = oldCe.Strokes[0];
            Check("存档：v22 老文件里没有那两位 → 焦点三角形退到默认（画）、P 退到「还没定过」",
                  oc.Kind == StrokeKind.ConicEllipse && oc.FocusTriangle && float.IsNaN(oc.FocusPointU),
                  $"Kind {oc.Kind}、FocusTriangle {oc.FocusTriangle}、"
                  + $"FocusPointU {(float.IsNaN(oc.FocusPointU) ? "NaN（= 自动摆）" : oc.FocusPointU.ToString())}");
        }


        // 真·v11 老文件：**一条对象**的那条笔画末尾少 1 个字节（就是 v12 的"渐近线"那一位）。
        // 注意 `MakeLegacyFile` 是"从整个文件末尾砍 N 个字节"，所以它只对**单条笔画**成立
        // （多条时只有最后一条被砍到，中间几条会整体错位）——这是那个工具的用法约定。
        var legacyDoc = new InkDocument();
        var legacyHy = NewCurve(Tool.Hyperbola, StrokeKind.Hyperbola, 1400f, 700f);
        // **故意按 v13 及以前的语义造**：那时双曲线只有**两个**控制点，第二个点同时是
        // "渐近线框"和"曲线半轴"（渐近线画到 ±2a、±2b）——这正是老文件里的样子。
        // 读端那条迁移（`version <= 13`）必须能从它恢复出**形状不变**的新表示。
        // 这里塞的是老算法下的"角点"：(1400,700) → (1475,850)，于是半轴 a = 75、b = 150、斜率 2。
        legacyHy.SetHyperbolaFromAsymptote(1400f, 700f, 1400f + 75f, 700f + 150f, minAxis);
        legacyHy.CurveAxis = CurveAxis.TransverseY;                              // 老文件里朝向是存的
        // 迁移后应当是：渐近线框 A = 150、B = 300，曲线经过 `u = 1` 那个点 → **实半轴还是 150**。
        legacyDoc.AddStroke(legacyHy);
        var old = new InkDocument();
        InkSerializer.LoadInto(old, MakeLegacyFile(legacyDoc, 11, 1));
        Check("存档：真 v11 老文件读得进来（1 条对象）", old.Strokes.Count == 1, $"{old.Strokes.Count} 条");
        if (old.Strokes.Count == 1)
        {
            Check("存档：老文件里朝向照旧（实轴沿 y）、渐近线退到默认（画）",
                  old.Strokes[0].CurveAxis == CurveAxis.TransverseY
                  && old.Strokes[0].ShowAsymptotes
                  && Near(old.Strokes[0].HyperbolaRealLocal(), 150f, .5f),
                  $"{old.Strokes[0].CurveAxis}，渐近线 {old.Strokes[0].ShowAsymptotes}"
                  + $"，实半轴 {old.Strokes[0].HyperbolaRealLocal():F1}（期望 150）");
        }

        // 真·v12 老文件里的**抛物线**：这一条专盯 v13 那次"换含义"的迁移。
        // v12 存的是"顶点 + **张口点**（落在对称轴上、离顶点 p/2）"，新读端必须把它换成
        // "曲线上的一个点"，否则反解不出张口、曲线会缩到最小——而且**不报错**，
        // 只是"一存一读就瘪了"。
        //
        // 注意这里是**故意**用一个"落在对称轴上"的点来模拟老文件：
        // 新写法 `SetParabolaThroughPoint` 本来是要放曲线上的点，但老文件的第二位就是张口点，
        // 只有这么造才是货真价实的 v12 语义（p = 2 × |顶点→张口点| = 280）。
        var legacyPara = NewCurve(Tool.Parabola, StrokeKind.Parabola, 700f, 700f);
        legacyPara.CurveAxis = CurveAxis.OpenLeft;             // 开口向左：方向 = (−1, 0)
        legacyPara.SetParabolaVertex(700f, 700f);
        legacyPara.SetParabolaThroughPoint(560f, 700f);        // 张口点 = 顶点 − (140, 0) → p = 280
        var legacyParaDoc = new InkDocument();
        legacyParaDoc.AddStroke(legacyPara);
        var oldPara = new InkDocument();
        // 砍 0 位：**v13 没往笔画后面加字节**，v12 的文件和它一样长（只差版本号）。
        InkSerializer.LoadInto(oldPara, MakeLegacyFile(legacyParaDoc, 12, 0));
        Check("存档：真 v12 老文件里的抛物线**读回来还是 p = 280**（迁移过去、形状不变）（±0.5）",
              oldPara.Strokes.Count == 1 && Near(oldPara.Strokes[0].ParabolaPLocal(), 280f, .5f)
              && oldPara.Strokes[0].CurveAxis == CurveAxis.OpenLeft,
              oldPara.Strokes.Count == 1
                  ? $"p {oldPara.Strokes[0].ParabolaPLocal():F1}（期望 280）、"
                    + $"朝向 {oldPara.Strokes[0].CurveAxis}"
                  : $"只读回 {oldPara.Strokes.Count} 条");
        // 迁移之后那个点必须**真的落在曲线上**（不是随便挪一个）：
        // `t ≠ 0` 才有信息定张口，而且代进参数方程应当能对上。
        if (oldPara.Strokes.Count == 1)
        {
            var op = oldPara.Strokes[0];
            var (odir, operp) = Stroke.ParabolaBasis(op.EffectiveAxis);
            var od = op.CurvePointLocal(1) - op.CurvePointLocal(0);
            float os = Vector2.Dot(od, odir), ot = Vector2.Dot(od, operp);
            Check("存档：迁移后那个点在**曲线该在的地方**（s = p/2、t = p = 280）（±0.5）",
                  Near(os, 140f, .5f) && Near(ot, 280f, .5f), $"s {os:F1}、t {ot:F1}（期望 140 / 280）");
        }

        // ============ ⑦ 删除 / 换方向之后不留残影（用户 2026-09-20 报的）============
        //
        // 用户原话："抛物线删除会留残影"。残影这一类 bug 的特点是**自检全绿也可能有**
        // （字段、包围盒全对，只是屏幕上没擦干净），所以只能**数屏幕上的像素**：
        // 画一条抛物线 → 量那块墨 → 删掉 → 再量同一块，必须一个像素都不剩。
        // 两条路都要走一遍：
        //   ① 直接画完就删；
        //   ② **改过一次形状之后再删**——改几何走的是另一条动作路径
        //      （见 SetStrokeGeometryAction：它必须顺手重算 Bounds，
        //       否则 WorldBounds 会拿旧框去套新曲线、擦不干净）。
        Console.WriteLine("  -- G. 删除之后不留残影（数屏幕像素）--");
        {
            var ink2 = new Color4(1f, 0f, 1f, 1f);
            Host.Commands.SetColor(ink2);
            // 量"这条曲线墨迹框再往外 60 像素"那一块里的品红像素。
            // 多留 60 是因为操作条就挂在框下方——那条残影也在这一块里。
            int InkAround(Stroke s)
            {
                var b = s.PaddedBounds;
                return ScreenProbe.CountMagenta((int)b.MinX - 12, (int)b.MinY - 12,
                                                (int)(b.MaxX - b.MinX) + 24,
                                                (int)(b.MaxY - b.MinY) + 84);
            }
            // 抛物线是**一笔拖**（顶点 = 按下那个点，照 InkClass 的 `case 20/21`）：
            // 按住顶点拖到"曲线经过的点"，松手就成。
            // 经过点取右上方 `(gw, −gw)`：`s = t = gw` → `p = gw²/(2gw) = gw/2`。
            void DragParabola(float gx, float gy, float gw)
            {
                SendMouse((int)gx, (int)gy, 0);                             SettleFrames(60);
                SendMouse((int)gx, (int)gy, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(60);
                SendMouse((int)(gx + gw), (int)(gy - gw), 0);               SettleFrames(240);
                SendMouse((int)(gx + gw), (int)(gy - gw), Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(240);
            }

            for (int round = 0; round < 2; round++)
            {
                bool flipFirst = round == 1;
                Doc.Clear();
                Doc.ClearHistory();
                SetToolFromUi(Tool.Parabola);
                float gx = _virtualX + 700f, gy = _virtualY + 1250f;
                DragParabola(gx, gy, 400f);             // p = 200（曲线半宽 300、沿轴 225）
                var g = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
                if (g == null) { Check("⑦ 抛物线没画出来（后面两项没法验）", false, "对象 0 个"); break; }

                // ② 那一轮：先**改一下这条曲线**再撤销——走一遍和"画完就删"不同的动作路径。
                //
                // ⚠ 这里原来是"拖抛物线的 Rim 手柄改 p"，**2026-09-20 第六批把四种曲线的
                //   特殊手柄全砍了**（改走通用框），那个手柄名当场失效、这一轮就静默退化成
                //   round 0 的复制品（断言照样绿）——正是"写死的手柄名会随功能移位而失效"
                //   那条教训。现在改拖**通用框的右下角**（缩放）：它改的是 `Transform`，
                //   走 `SetStrokeTransformAction`，不是几何那条路。
                //   哪天真把曲线手柄加回来，这里也该跟着改回来。
                if (flipFirst)
                {
                    Doc.SelectOnly(new[] { g });
                    SettleFrames(60);
                    var gf = SelectionHandles.FrameOf(Doc.Selected);
                    var gc = SelectionHandles.CanvasPosition(SelHandle.BottomRight, gf, DpiScale);
                    bool got = SelectionGestureForTest(gc.X, gc.Y);
                    UpdateSelectionGestureForTest(gc.X + 120f, gc.Y + 80f);
                    SettleFrames(80);
                    EndSelectionGestureForTest();
                    SettleFrames(140);
                    // 先证明"真的改了"——不然下面那条"改过形状之后删除"是假的
                    Check("⑦ 第二轮：拖通用框真的动了这条抛物线（不然下面那条断言是假的）",
                          got && !g.Transform.IsIdentity,
                          $"接住={got}，变换={(g.Transform.IsIdentity ? "单位（没动）" : "变了")}");
                    Doc.Undo();                     // 撤销那一次改动（再走一遍写回路径）
                    SettleFrames(140);
                    Doc.SelectOnly(new[] { g });
                    SettleFrames(120);
                }

                Doc.SelectOnly(new[] { g });
                SettleFrames(120);
                int before = InkAround(g);
                RunBarActionForTest((int)SelBarButton.Delete);
                SettleFrames(300);
                int after = InkAround(g);
                Check(flipFirst
                        ? "⑦ 改过形状之后删除：那块地方一个墨点都不剩（残影）"
                        : "⑦ 直接删除：那块地方一个墨点都不剩（残影）",
                      before > 200 && after == 0,
                      $"删前 {before} 像素 → 删后 {after}（方向 {(flipFirst ? "换过" : "没换")}）");
            }
        }

        // ============ ⑧ 双曲线**两笔拖动式** ============
        //
        // 口径（用户 2026-09-20 定："他的双曲线是拖动两次吗？按照他的这个规则复刻"）：
        // **一笔 = 按下-拖-松手，松手推进下一笔**——照 InkClass 的
        // `drawMultiStepShapeCurrentStep` ＋ MouseUp 推进（`MW_ShapeDrawing.cs:1814-1822`）：
        //   第 1 笔 → **从中心拖出渐近线**（拖到哪就是哪）→ 松手**锁住**，此后不再变；
        //   第 2 笔 → **再拖一下**：拖到哪、**曲线就经过哪**（中心沿用第 1 笔那个，
        //             InkClass 的第二笔刻意不重设 `iniP`，`:1971-1977`）；
        // 中途松手**不进文档**（半成品留在 ActiveStroke），两笔合起来**只有一条撤销记录**。
        //
        // 和上一版（三下点击 + 中间悬停）的区别：**几何只在按住拖动时更新**，指针不按键时
        // 半成品一动不动（InkClass 就是这样）——这也是触摸屏能用的根子：手指没有"悬停"，
        // 而每一步本来就是"按住拖-松手"，笔 / 鼠标 / 手指走的是同一条路。
        Console.WriteLine("  -- H. 双曲线两笔拖动式（从中心拖出渐近线 → 再拖一下定曲线）--");
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Hyperbola);
            float hpx = _virtualX + 900f, hpy = _virtualY + 1300f;

            // 一次"按住拖"：按下 → 挪到目标 → 抬起。
            void Drag(float x0, float y0, float x1, float y1)
            {
                SendMouse((int)x0, (int)y0, 0);                            SettleFrames(60);
                SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
                SendMouse((int)x1, (int)y1, 0);                            SettleFrames(240);
                SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(180);
            }

            // 第 1 笔：从中心 (hpx,hpy) 拖到右下方 (hpx+400, hpy+160) → 渐近线框 A = 400、B = 160
            //（斜率 0.4，和 InkClass 的 `k = |dy/dx|` 同一个）。
            SendMouse((int)hpx, (int)hpy, 0);                            SettleFrames(60);
            SendMouse((int)hpx, (int)hpy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            Check("两笔式：第 1 笔按下之后**还没提交**（半成品不进文档）",
                  Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 条（期望 0）");

            SendMouse((int)(hpx + 200f), (int)(hpy + 80f), 0);   SettleFrames(60);
            SendMouse((int)(hpx + 400f), (int)(hpy + 160f), 0);  SettleFrames(240);
            Check("两笔式：第 1 笔拖动中是**只画渐近线**的阶段（状态）",
                  HyperAsymptotePreviewOnly && ActiveStroke != null,
                  $"只画渐近线={HyperAsymptotePreviewOnly}，半成品={(ActiveStroke != null)}");
            // 并且要在屏幕上看得见这条差别：曲线上有一小块**渐近线根本不会经过**的地方
            //（中心右下方 205..260 × 30..70，渐近线在那儿是 y = 0.4x ∈ [82, 104]）。
            int inkAux = ScreenProbe.CountMagenta((int)hpx + 205, (int)hpy + 30, 55, 40);

            // 松手 = **第 1 笔完成**：渐近线锁住，但对象**还不进文档**（还有第 2 笔）。
            SendMouse((int)(hpx + 400f), (int)(hpy + 160f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);
            Check("两笔式：第 1 笔松手之后**仍然没提交**（还有第 2 笔）",
                  Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 条（期望 0）");

            // **松手后晃鼠标（不按键）：半成品一动不动**——这是这一版和上一版最大的手感差别，
            // 也是触摸屏上"没有悬停"这件事能成立的原因。
            SendMouse((int)(hpx + 900f), (int)(hpy + 600f), 0);   SettleFrames(240);
            Check("两笔式：松手后**晃鼠标不按键**，渐近线框一动不动（A 400、B 160）（±0.5）",
                  ActiveStroke != null
                  && Near(ActiveStroke.HyperbolaALocal(), 400f, .5f)
                  && Near(ActiveStroke.HyperbolaBLocal(), 160f, .5f),
                  ActiveStroke == null ? "半成品没了"
                      : $"A {ActiveStroke.HyperbolaALocal():F1}  B {ActiveStroke.HyperbolaBLocal():F1}");

            // 第 2 笔：**再拖一下**——先掠过一个"能证明曲线出来了"的位置 (220, 37)，
            // 再拖到最终落点 (500, 150)（中心右下方、比渐近线更横）
            // → 曲线**正好经过这个点**，朝向 = 焦点在 x 轴（左右双曲线）。
            SendMouse((int)hpx, (int)hpy, 0);                            SettleFrames(60);
            SendMouse((int)hpx, (int)hpy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            // 先停在 (220,37)：曲线此刻正好穿过那一小块（a 由这个点反解 ≈ 200），
            // 于是"同一块地方"的墨从 0 变成"有曲线"——证明曲线是**第 2 笔才出来**的。
            SendMouse((int)(hpx + 220f), (int)(hpy + 37f), 0);           SettleFrames(240);
            int inkCurve = ScreenProbe.CountMagenta((int)hpx + 205, (int)hpy + 30, 55, 40);
            Check("两笔式：曲线是**第 2 笔拖动时才出现**的（同一块地方：0 墨 → 有曲线）",
                  inkAux == 0 && inkCurve > 50, $"只出渐近线时 {inkAux} 像素 → 出曲线后 {inkCurve} 像素");
            // 再拖到最终落点；挪到一半时曲线也跟着走（否则松手会跳）。
            SendMouse((int)(hpx + 500f), (int)(hpy + 150f), 0);          SettleFrames(240);
            Check("两笔式：第 2 笔拖动中**仍然没提交**（松手才算完）",
                  Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 条（期望 0）");

            // 第 2 笔松手 = 定稿：a 由那个点反解 —— a² = 500² − 150²/0.4² = 109375。
            float k3 = 0.4f, dx3 = 500f, dy3 = 150f;
            float wantA = MathF.Sqrt(dx3 * dx3 - dy3 * dy3 / (k3 * k3));    // ≈ 330.72
            float wantB = wantA * k3;                                       // ≈ 132.29
            SendMouse((int)(hpx + dx3), (int)(hpy + dy3), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);

            var hyp = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("两笔式：第 2 笔松手之后对象才出来（一条双曲线、实轴沿 x）",
                  hyp != null && hyp.Kind == StrokeKind.Hyperbola
                  && hyp.CurveAxis == CurveAxis.TransverseX,
                  hyp == null ? $"对象 {Doc.Strokes.Count} 条（期望 1）"
                              : $"Kind={hyp.Kind} 朝向={hyp.CurveAxis}");
            if (hyp == null) { Console.WriteLine("  （后面几项没法验）"); }
            else
            {
                Check("两笔式：半轴由那个点反解 —— 实半轴 330.7、虚半轴 132.3（±2）",
                      Near(hyp.HyperbolaRealLocal(), wantA, 2f)
                      && Near(hyp.HyperbolaImagLocal(), wantB, 2f),
                      $"实半轴 {hyp.HyperbolaRealLocal():F1}（期望 {wantA:F1}）、"
                      + $"虚半轴 {hyp.HyperbolaImagLocal():F1}（期望 {wantB:F1}）");
                // 这一步的**核心断言**：把那个点代进标准式，必须**正好等于 1**（曲线穿过它）。
                // 式子里要用**曲线自己的** x / y 方向系数（`HyperbolaCurveALocal/BLocal`），
                // **不是**渐近线框的 `HyperbolaALocal/BLocal`（那两个是框，曲线随时可能比它小）。
                float fit = dx3 * dx3 / (hyp.HyperbolaCurveALocal() * hyp.HyperbolaCurveALocal())
                          - dy3 * dy3 / (hyp.HyperbolaCurveBLocal() * hyp.HyperbolaCurveBLocal());
                Check("两笔式：曲线**正好经过那个点**（x²/a² − y²/b² = 1）（±0.01）",
                      Near(fit, 1f, .01f), $"代入得 {fit:F4}（期望 1）");
                Check("两笔式：**渐近线斜率没动**（还是 0.4）（±0.005）",
                      Near(hyp.HyperbolaBLocal() / hyp.HyperbolaALocal(), k3, .005f),
                      $"{hyp.HyperbolaBLocal() / hyp.HyperbolaALocal():F4}（期望 {k3:F4}）");
                // **这一版的正题**：第 2 笔"定曲线"不许碰第 1 笔锁住的那个框
                //（用户原话："渐近线画好以后大小完全不动，长度也不动"）。
                Check("两笔式：**渐近线框一动都没动**（A 400、B 160 —— 就是第 1 笔拖出来的那个）（±0.5）",
                      Near(hyp.HyperbolaALocal(), 400f, .5f) && Near(hyp.HyperbolaBLocal(), 160f, .5f),
                      $"A {hyp.HyperbolaALocal():F1}（期望 400）  B {hyp.HyperbolaBLocal():F1}（期望 160）");
                Check("两笔式：两笔 = **一条**撤销记录（不是两条）",
                      Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");
                Doc.Undo();
                Check("两笔式：撤销一步 → 整条消失",
                      Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 条（期望 0）");
            }

            // ---- 换工具 = 半成品作废：那条画了一半的必须**当场丢掉** ----
            //
            // 为什么值得单独验：那半条双曲线**不在文档里**，却一直被渲染（ActiveStroke 是
            // 无条件画的）。不丢的话它会永远挂在屏幕上，而且**橡皮也擦不掉**——它根本不是
            // 文档里的对象。所以这里用"数屏幕上的墨"来验，而不是数对象个数。
            SetToolFromUi(Tool.Hyperbola);
            float qx = _virtualX + 1500f, qy = _virtualY + 1300f;
            // 第 1 笔拖到 (200, 80)：渐近线框 A = 200、B = 80（虚线就画到 ±A、±B）。
            SendMouse((int)qx, (int)qy, 0);                            SettleFrames(60);
            SendMouse((int)qx, (int)qy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse((int)(qx + 200f), (int)(qy + 80f), 0);           SettleFrames(240);
            int inkBefore = ScreenProbe.CountMagenta((int)qx - 260, (int)qy - 110, 520, 220);
            SendMouse((int)(qx + 200f), (int)(qy + 80f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);
            SetToolFromUi(Tool.Line);                             // 换工具 → 作废
            SettleFrames(360);
            int inkAfter = ScreenProbe.CountMagenta((int)qx - 260, (int)qy - 110, 520, 220);
            Check("两笔式：换工具把半成品**丢掉**（屏幕上不留痕）",
                  inkBefore > 200 && inkAfter == 0,
                  $"换工具前 {inkBefore} 像素 → 后 {inkAfter}");
            SetToolFromUi(Tool.Hyperbola);

            // ---- 换工具之后重新画 = **新的一条**（不是接着上一条的大小改）----
            // 第 1 笔：从 (qx,qy) 拖出渐近线框（A = 200、B = 80）；
            // 第 2 笔：拖到 (300, 0)（**在框内**、且在实轴上）→ 实半轴就正好是 300。
            Doc.ClearHistory();
            Drag(qx, qy, qx + 200f, qy + 80f);                    // 第 1 笔：渐近线
            Drag(qx, qy, qx + 300f, qy);                          // 第 2 笔：曲线过 (300,0)
            Check("两笔式：换过工具之后，再画一条是**新对象**（不是改上一条）",
                  Doc.Strokes.Count == 1 && Near(Doc.Strokes[0].HyperbolaRealLocal(), 300f, 6f),
                  $"对象 {Doc.Strokes.Count} 条，实半轴 "
                  + $"{(Doc.Strokes.Count > 0 ? Doc.Strokes[0].HyperbolaRealLocal() : -1f):F1}（期望 1 条 / 300）");

            // ---- 第 2 笔拖到"更竖"的位置 → **上下双曲线**（焦点在 y 轴）----
            //
            // 这一条正是用户说的"如果我选的是左右，就是左右；如果是上下，就是上下"：
            // 渐近线斜率 m = 480/240 = 2，第 2 笔拖到 (200, 500) 比渐近线**更竖**
            //（m·|dx| = 400 < 500）→ 焦点落在 y 轴；实半轴用**实轴沿 y**的式子反解：
            // a = √(500²·(240/480)² − 200²) = √(62500 − 40000) = 150、b = a × 2 = 300。
            // 报出来的"实半轴"就是 b = 300（实轴沿 y 时实半轴是纵向那个，见 HyperbolaRealLocal）。
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Hyperbola);
            float ux = _virtualX + 1500f, uy = _virtualY + 1300f;
            Drag(ux, uy, ux + 240f, uy + 480f);                   // 第 1 笔：渐近线（m = 2）
            Drag(ux, uy, ux + 200f, uy + 500f);                   // 第 2 笔：更竖 → 焦点在 y 轴

            var hypY = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("两笔式：第 2 笔拖到更竖的位置 → **上下双曲线**（实轴沿 y），半轴 300 / 150（±2）",
                  hypY != null && hypY.CurveAxis == CurveAxis.TransverseY
                  && Near(hypY.HyperbolaRealLocal(), 300f, 2f)
                  && Near(hypY.HyperbolaImagLocal(), 150f, 2f),
                  hypY == null ? $"对象 {Doc.Strokes.Count} 条（期望 1）"
                               : $"{hypY.CurveAxis}，实半轴 {hypY.HyperbolaRealLocal():F1}、"
                                 + $"虚半轴 {hypY.HyperbolaImagLocal():F1}");
            if (hypY != null)
            {
                // 同样要用**曲线的** x / y 方向系数（不是渐近线框的 A / B）。
                float fitY = 500f * 500f / (hypY.HyperbolaCurveBLocal() * hypY.HyperbolaCurveBLocal())
                           - 200f * 200f / (hypY.HyperbolaCurveALocal() * hypY.HyperbolaCurveALocal());
                // 容差比上面那条宽：鼠标坐标是**取整**的，一个像素的偏差在 a 只有 150 时
                // 相对误差是 a=330 时的两倍多（上面那条 ±0.01 就够，这条要放宽到 ±0.05）。
                Check("两笔式：上下双曲线也**正好经过那个点**（y²/b² − x²/a² = 1）（±0.05）",
                      Near(fitY, 1f, .05f), $"代入得 {fitY:F4}（期望 1）");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }


    /// <summary>
    /// `--wetdrytest`：**湿墨（抬笔前）与干墨（抬笔后）接不接得上**（2026-09-28 定的第一条调查）。
    ///
    /// 背景：真笔/合成笔写字时，屏幕上那一笔是 **DWM 委托轨迹**画的；抬笔那一刻换成
    /// 我们自己画的 `ID2D1Ink`。两边只要**粗细**或**中心线**差一点，每一笔收尾就会
    /// "轻轻沉一下"——这正是"说不清但就是差一点"的头号嫌疑。
    ///
    /// 判据：按住不放的时候先拍一张（笔尖后 100~260px 那一段，肯定已经被轨迹画上），
    /// 抬笔、等轨迹收掉之后再拍同一块，逐列比**墨宽**和**中心线**。
    /// 先要求"湿墨那张真的有墨"，否则"没画出来"会被误判成"没有差别"。
    /// </summary>
    private void WetDryTest(bool live, double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 湿墨 / 干墨 交接测量（真笔）===");
        Console.WriteLine($"  委托墨迹轨迹: 可用={OverlayWindow.InkTrailAvailable}，启用={OverlayWindow.InkTrailEnabled}"
                          + $"（{OverlayWindow.InkTrailNote}）");

        if (!live)
        {
            Console.WriteLine();
            Console.WriteLine("  这条**只能拿真笔画**：合成笔不会被 DWM 画成湿墨（--wetinktest 实测如此），");
            Console.WriteLine("  拿合成笔量到的“湿墨”其实是我们自己画的那一笔，比出来必然一样（假绿）。");
            Console.WriteLine("  用法：拿手写笔在屏幕中间那条浅灰线之间画一条横线（压力从轻到重），然后松手：");
            Console.WriteLine("      InkTeach.exe --wetdrytest --live 20");
            Console.WriteLine();

            // 干跑校验：证明采集缓冲是复用的。第一版每次抓屏都 new 2MB（进 LOH），
            // 一次笔画能刷出 64 次 gen2 —— 那量的是工具自己，不是产品。这条要盯住。
            int tw = Math.Min(1400, _virtualW - 200), th = Math.Min(360, _virtualH - 200);
            var buf = new byte[tw * th * 4];
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            int c0 = GC.CollectionCount(0), c2 = GC.CollectionCount(2);
            int shots = 0;
            for (int i = 0; i < 75; i++)
            {
                if (ScreenProbe.CaptureRegionInto(buf, _virtualX + 50, _virtualY + 50, tw, th)) shots++;
                SettleFrames(1);
            }
            long used = GC.GetAllocatedBytesForCurrentThread() - a0;
            Console.WriteLine($"  干跑校验：抓 {shots} 次（{tw}×{th}，**复用同一块缓冲**）工具自身分配 {used / 1024.0:F0} KB，"
                              + $"GC {GC.CollectionCount(0) - c0}/{GC.CollectionCount(2) - c2}"
                              + "——接近 0 才算工具干净");
            _quit = true; return;
        }
        if (!OverlayWindow.InkTrailEnabled || !OverlayWindow.InkTrailAvailable)
        {
            Console.WriteLine("  SKIP: 没有委托轨迹，这条测量没有意义——它比的就是"
                              + "“系统画的湿墨”与“我们画的干墨”");
            _quit = true; return;
        }

        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        Doc.InvalidateAll();

        int bandW = Math.Min(1400, _virtualW - 200);
        int bandH = Math.Min(360, _virtualH - 200);
        int bandX = _virtualX + (_virtualW - bandW) / 2;
        int bandY = _virtualY + (_virtualH - bandH) / 2;
        float gy = bandY + bandH * 0.5f;

        // 浅灰引导线（**故意浅到进不了墨的判据**：白底判据是 RGB 之和 < 600，
        // 0.85 灰是 651，不会被当成墨）。两端加两个小竖标记，告诉用户画在哪儿。
        var guide = new Color4(0.85f, 0.86f, 0.88f, 1f);
        void Guide(float x1, float y1, float x2, float y2)
        {
            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = guide, Width = 2f * DpiScale };
            s.AddPoint(x1, y1, 0.5f, 0);
            s.AddPoint(x2, y2, 0.5f, 0);
            Doc.AddStroke(s);
        }
        Guide(bandX + 120f, gy, bandX + bandW - 120f, gy);
        Guide(bandX + 120f, gy - 40f, bandX + 120f, gy + 40f);
        Guide(bandX + bandW - 120f, gy - 40f, bandX + bandW - 120f, gy + 40f);
        SettleFrames(150);

        Console.WriteLine();
        Console.WriteLine($"  请在两条**竖标记之间**用笔从左画到右（压力从轻到重），{seconds:F0} 秒内、一笔画完。");
        Console.WriteLine("  松手后会自动出结果。");
        Console.WriteLine();

        byte[] wetBuf = new byte[bandW * bandH * 4];
        byte[] dryBuf = new byte[bandW * bandH * 4];
        bool gotWet = false;
        double wetAt = 0, lastCap = 0, t0 = NowMs;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            if (_dirty || _drawing) { if (OverlayWindow.VBlankPaced) Native.DwmFlush(); RenderAll(); _dirty = false; }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 20, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (ActiveStroke != null)
            {
                // 笔还接触着：这一张就是**用户当下看到的湿墨**（委托轨迹 ∪ 我们自己画的那一笔）。
                // ⚠ 缓冲必须复用（`CaptureRegionInto`）：每帧 new 2MB 会进 LOH，
                //   实测能把一次笔画刷出 64 次 gen2 —— 那量的是测量工具自己。
                // 限到 ~25 次/秒：够拿到"松手前最后一帧"，又不给测量本身加负担。
                if (NowMs - lastCap >= 40 &&
                    ScreenProbe.CaptureRegionInto(wetBuf, bandX, bandY, bandW, bandH))
                {
                    gotWet = true;
                    wetAt = NowMs;
                    lastCap = NowMs;
                }
            }
            else if (gotWet && NowMs - wetAt > 300)
            {
                break;                              // 抬笔 300ms 了，轨迹该收掉了
            }
        }

        if (!gotWet)
        {
            Console.WriteLine("  FAIL: 整个过程没测到任何笔迹（笔接触时才会采样）");
            _quit = true; return;
        }
        SettleFrames(400);
        if (!ScreenProbe.CaptureRegionInto(dryBuf, bandX, bandY, bandW, bandH))
        {
            Console.WriteLine("  FAIL: 抓屏失败"); _quit = true; return;
        }
        ScreenProbe.SaveBuffer("reports/wetdry-wet.bmp", wetBuf, bandW, bandH);
        ScreenProbe.SaveBuffer("reports/wetdry-dry.bmp", dryBuf, bandW, bandH);

        var (widthW, centerW) = InkProfile(wetBuf, bandW, bandH);
        var (widthD, centerD) = InkProfile(dryBuf, bandW, bandH);
        int colsWet = 0, colsBoth = 0;
        var dw = new List<float>();
        var dc = new List<float>();
        float wMin = float.MaxValue, wMax = 0f, dMin = float.MaxValue, dMax = 0f;
        for (int i = 0; i < bandW; i++)
        {
            if (widthW[i] > 0) { colsWet++; wMin = MathF.Min(wMin, widthW[i]); wMax = MathF.Max(wMax, widthW[i]); }
            if (widthD[i] > 0) { dMin = MathF.Min(dMin, widthD[i]); dMax = MathF.Max(dMax, widthD[i]); }
            if (widthW[i] > 0 && widthD[i] > 0)
            {
                colsBoth++;
                dw.Add(MathF.Abs(widthW[i] - widthD[i]));
                dc.Add(MathF.Abs(centerW[i] - centerD[i]));
            }
        }
        dw.Sort();
        dc.Sort();
        float Med(List<float> l) => l.Count == 0 ? -1f : l[l.Count / 2];
        float Max(List<float> l) => l.Count == 0 ? -1f : l[^1];

        Console.WriteLine($"  湿墨有墨的列：{colsWet}/{bandW}（湿墨线宽 {wMin:F0}~{wMax:F0}px）");
        Console.WriteLine($"  干墨有墨的列  线宽 {dMin:F0}~{dMax:F0}px；两边都有墨的列 {colsBoth}");
        Console.WriteLine($"  线宽差（湿 vs 干）：中位 {Med(dw):F1}px，最大 {Max(dw):F1}px");
        Console.WriteLine($"  中心线偏移        ：中位 {Med(dc):F1}px，最大 {Max(dc):F1}px");

        // 沿笔迹方向的最佳对齐位移：把干墨的宽度序列相对湿墨平移 k，取"平均差最小"的那个 k。
        // ⚠ 别用"最急变化处"那种 argmax 判据——两个几乎一样的曲线，最高峰差 2px 就会翻到别处
        //   （2026-09-28 实测：中位差 0.0px 的一条笔，它报出"差 +361px"，纯属噪声）。
        int bestShift = 0;
        float bestErr = float.MaxValue;
        for (int k = -40; k <= 40; k++)
        {
            float sum = 0f;
            int cnt = 0;
            for (int i = 0; i < bandW; i++)
            {
                int j = i + k;
                if (j < 0 || j >= bandW) continue;
                if (widthW[i] <= 0 || widthD[j] <= 0) continue;
                sum += MathF.Abs(widthW[i] - widthD[j]);
                cnt++;
            }
            if (cnt < bandW / 3) continue;
            float err = sum / cnt;
            if (err < bestErr) { bestErr = err; bestShift = k; }
        }
        Console.WriteLine($"  沿笔迹方向的最佳对齐位移：{bestShift:+0;-0;0}px"
                          + $"（对齐后平均线宽差 {bestErr:F2}px）"
                          + (Math.Abs(bestShift) <= 2 ? "——没有滞后" : "——两边在笔迹方向上错开了"));
        Console.WriteLine("  出图：reports/wetdry-wet.bmp / wetdry-dry.bmp（人眼对照）");

        bool ok = colsBoth > bandW / 4 && colsWet > bandW / 4
                  && Med(dw) <= 2.5f && Med(dc) <= 1.5f && Math.Abs(bestShift) <= 2;
        Console.WriteLine(ok
            ? $"  PASS: 湿墨和干墨接得上（线宽差中位 {Med(dw):F1}px、中心线中位 {Med(dc):F1}px、"
              + $"对齐位移 {bestShift}px）"
            : colsWet <= bandW / 4
                ? "  FAIL: 湿墨那张几乎没有墨——轨迹没画出来，测量无效（别当成通过）"
                : $"  FAIL: 抬笔前后对不上（线宽差中位 {Med(dw):F1}px、对齐位移 {bestShift}px）——收笔会“沉一下”");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 一块 BGRA 图里，**逐列**的墨宽与中心线（白底判据：不是白就算墨）。
    /// 返回的两个数组长度 = 图宽；某一列没有墨时该列 = -1。
    /// </summary>


    private void InkTrailTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 委托墨迹轨迹测试（关掉自己画的湿墨）===");
        Console.WriteLine($"  接口状态: {OverlayWindow.InkTrailNote}");

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;

        SettleFrames(300);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int bandX = (int)(cx - 500);
        int bandY = (int)(cy - 140);

        // 按住不放，慢慢画一段，然后**在按住的状态下**截图
        SendMouse((int)(cx - 400), (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)(cx - 400), (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(100);
        for (int i = 1; i <= 40; i++)
        {
            float t = i / 40f;
            SendMouse((int)(cx - 400 + t * 800), (int)(cy + MathF.Sin(t * 6.28f) * 80), 0);
            SettleFrames(18);
        }
        SettleFrames(250);   // 仍然按着

        int whileDown = ScreenProbe.CountRed(bandX, bandY, 1000, 280);
        string shot1 = Path.Combine("reports", "shots", "trail-while-down.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot1));
        ScreenProbe.SaveBmp(shot1, _virtualX, _virtualY, _virtualW, _virtualH);

        SendMouse((int)(cx + 400), (int)cy, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(600);
        int afterUp = ScreenProbe.CountRed(bandX, bandY, 1000, 280);

        Console.WriteLine($"  按住不放时笔迹像素: {whileDown}（这些只能是系统合成器画的）");
        Console.WriteLine($"  松手之后笔迹像素  : {afterUp}（我们自己的笔画接管）");
        Console.WriteLine($"  轨迹调试          : {OverlayWindow.InkTrailDebug}");
        Console.WriteLine($"  截图: {Path.GetFullPath(shot1)}");

        bool ok = whileDown > 800;
        // 委托墨迹轨迹**只对真笔（PT_PEN）生效**（鼠标下系统根本不接这条通道）。
        // 这个用例是用合成鼠标事件跑的，所以鼠标下量不到轨迹是预期结果，
        // 不该报成失败——原来这里一直红着，害得"全套自检"里有一条永远修不掉。
        bool mouseRun = LastPointerType != Native.PT_PEN;
        Console.WriteLine(ok ? "  PASS: 委托墨迹轨迹生效"
                        : mouseRun
                            ? "  SKIP: 当前是鼠标（轨迹只对真笔生效），这一项不适用"
                            : "  FAIL: 手上是真笔却没看到系统画的轨迹");
        _quit = true;
    }


    /// <summary>
    /// 湿墨轨迹（委托墨迹）实测：**只让系统画**（`SuppressActiveStroke`），用合成笔走一遍，
    /// 再从屏幕上数墨色像素。
    ///
    /// 为什么必须测这一条：预测**只喂这条通道**——它画的是"正在写的这一笔"的最后一小段。
    /// 如果这条通道在某台机器上根本没上屏（API 调用成功、返回了 generationId，
    /// 不等于 DWM 真的画出来），那预测就不可能有任何可见效果，
    /// "开/关都一样"就会有完全不同的解释。
    /// </summary>
    private void WetInkTest(bool activate = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 湿墨轨迹实测：只让系统画（自己不画）===");
        Console.WriteLine($"  变体：{(activate ? "把覆盖层强制激活（去掉 NOACTIVATE + 抢前台）" : "按产品原样（置顶 + 不抢焦点）")}");
        if (!EnsureSyntheticPen())
        {
            Console.WriteLine("  SKIP: 拿不到合成笔设备");
            _quit = true; return;
        }

        if (activate && _windows.Count > 0)
        {
            // 假设：DWM 可能只给"前台窗口"画委托湿墨。这里把 NOACTIVATE 摘掉并抢一次前台，
            // 看轨迹会不会突然出现——纯粹是对照实验，产品形态不会这么做。
            var w0 = _windows[0];
            long ex = (long)Native.GetWindowLongPtr(w0.Hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(w0.Hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex & ~(long)Native.WS_EX_NOACTIVATE));
            Native.SetForegroundWindow(w0.Hwnd);
            Console.WriteLine($"  已激活：{w0.Hwnd}");
        }

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;      // 我们自己那一笔不画，屏幕上留的就只能是系统画的
        SettleFrames(200);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int boxX = (int)(cx - 400), boxY = (int)(cy - 160);

        SendPenPoint(cx - 300, cy, 400, contact: true, first: true);
        for (int i = 1; i <= 30; i++)
        {
            SendPenPoint(cx - 300 + i * 20, cy + MathF.Sin(i / 30f * 6.28f) * 60,
                         400, contact: true, first: false);
            SettleFrames(4);
        }
        SettleFrames(60);                 // 笔仍然按着

        int whileDown = ScreenProbe.CountRed(boxX, boxY, 800, 320);
        int whileDownFull = ScreenProbe.CountRed(_virtualX, _virtualY, _virtualW, _virtualH);
        string shot = Path.Combine("reports", "shots", "wetink-down.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot));
        ScreenProbe.SaveBmp(shot, boxX, boxY, 800, 320);
        string shotFull = Path.Combine("reports", "shots", "wetink-down-full.bmp");
        ScreenProbe.SaveBmp(shotFull, _virtualX, _virtualY, _virtualW, _virtualH);

        SendPenPoint(cx + 300, cy, 400, contact: false, first: false);
        SettleFrames(300);
        int afterUp = ScreenProbe.CountRed(boxX, boxY, 800, 320);

        SuppressActiveStroke = false;
        Console.WriteLine($"  按住不放时墨色像素：{whileDown}（这一笔我们自己没画，只能是系统画的）");
        Console.WriteLine($"  同上但扫全屏      ：{whileDownFull}（排除「画在别处」）");
        Console.WriteLine($"  抬笔之后墨色像素  ：{afterUp}（委托轨迹应当被收回）");
        Console.WriteLine($"  轨迹调试          ：{OverlayWindow.InkTrailDebug}");
        Console.WriteLine($"  截图              ：{Path.GetFullPath(shot)}");
        Console.WriteLine($"  全屏截图          ：{Path.GetFullPath(shotFull)}");
        // 重要教训：**合成笔不被 DWM 认**（实测：合成笔下按住时 0 个墨色像素，
        // 换成真笔同样流程是 1.4 万个）。所以合成分支只能判"我们的 API 调用成不成功"，
        // 判不了"系统画没画"——真笔要用 --wetinktest --live N（人来写）。
        Console.WriteLine(whileDown > 500
            ? "  PASS: 委托墨迹轨迹确实上了屏 —— 预测有可见通道"
            : "  不可判定：合成笔不会被 DWM 画成湿墨（实测如此）。要判这一条请用真笔：--wetinktest --live 15");
        Console.WriteLine($"  抬笔后的墨是我们自己提交进文档的那一笔（与轨迹无关），这里只作记录：{afterUp} 像素");
        _quit = true;
    }


    /// <summary>
    /// 湿墨轨迹「真笔 + 自己不画」实测：你写 N 秒，我每 100 ms 采一次屏，
    /// 记下**最多**看到多少墨色像素。
    ///
    /// 为什么要人写：合成笔走的是同一条 WM_POINTER 路径，但 DWM 可能只认真实数字化仪的
    /// 指针（上一轮合成笔下轨迹一个像素都没有）。这一轮就是用来判这一条的。
    /// </summary>
    private void WetInkLiveTest(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 湿墨轨迹实测（真笔 · 只让系统画）===");
        Console.WriteLine("  注意：这一轮你自己写的时候屏幕上**不会**出现笔画（我们自己那层关掉了），");
        Console.WriteLine("        抬笔之后那一笔才会出现。屏幕上中途出现的任何墨，都只能是系统画的委托轨迹。");
        Console.WriteLine($"  现在开始写 {seconds:F0} 秒……");
        Console.WriteLine();

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;
        SettleFrames(120);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int boxX = (int)(cx - 700), boxY = (int)(cy - 350);
        int maxBand = 0, maxBandDown = 0, maxFull = 0, samples = 0, downSamples = 0;
        int maxRise = 0, bandAtDown = 0;
        bool wasDown = false;
        int beforePoints = PenTotalPoints;
        long fedBefore = OverlayWindow.InkTrailPointsFed;

        double t0 = NowMs;
        double nextSampleAt = t0;
        int tick = 0;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            if (_dirty || _drawing) { if (OverlayWindow.VBlankPaced) Native.DwmFlush(); RenderAll(); _dirty = false; }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 20, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= nextSampleAt)
            {
                nextSampleAt = NowMs + 100;
                samples++;
                int band = ScreenProbe.CountRed(boxX, boxY, 1400, 700);
                if (band > maxBand) maxBand = band;
                // 判据用"按下期间像素**增量**"而不是绝对值：抬笔后的干墨、屏幕下面的桌面
                // 内容都会进绝对值，只有"按下这一下让墨水变多"才证明系统轨迹真的在画
                //（2026-10-05 两次踩坑后改成这样）。
                bool down = _drawing;
                if (down && !wasDown) bandAtDown = band;
                if (down)
                {
                    downSamples++;
                    if (band > maxBandDown) maxBandDown = band;
                    int rise = band - bandAtDown;
                    if (rise > maxRise) maxRise = rise;
                }
                wasDown = down;
                if (++tick % 10 == 0)              // 全屏每秒一次，排除"画在别处"
                {
                    int full = ScreenProbe.CountRed(_virtualX, _virtualY, _virtualW, _virtualH);
                    if (full > maxFull) maxFull = full;
                }
            }
        }

        SuppressActiveStroke = false;
        int penSamples = PenTotalPoints - beforePoints;
        long fed = OverlayWindow.InkTrailPointsFed - fedBefore;
        Console.WriteLine();
        Console.WriteLine($"  这 {seconds:F0} 秒里收到真笔采样点：{penSamples}（0 说明没写进来）");
        Console.WriteLine($"  按下期间墨色像素**增量**    ：{maxRise}（判定用；采了 {downSamples} 次）");
        Console.WriteLine($"  按下期间绝对值 / 全程最大  ：{maxBandDown} / {maxBand}（含抬笔后的干墨，仅参考）");
        Console.WriteLine($"  喂进系统轨迹的点数        ：{fed}");
        Console.WriteLine($"  全屏的最大墨色像素        ：{maxFull}");
        Console.WriteLine($"  轨迹调试                  ：{OverlayWindow.InkTrailDebug}");
        bool pass = penSamples > 0 && fed > 0 && maxRise > 200;
        string why = penSamples == 0 ? "没收到真笔采样（鼠标/兼容模式）"
                   : fed == 0 ? "轨迹一个点都没喂进系统"
                   : maxRise <= 200 ? "按下期间屏幕上的墨没有增多 —— 系统轨迹没画"
                   : "系统轨迹正常";
        Console.WriteLine(pass
            ? "  PASS: 真笔下系统确实在画委托轨迹"
            : $"  FAIL: {why} —— 委托轨迹这台机器/这个设备上不生效");
        SettleFrames(120);
        _quit = true;
    }

}
