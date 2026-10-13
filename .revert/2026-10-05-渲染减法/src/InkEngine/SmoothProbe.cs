using System.Numerics;

namespace InkEngine;

/// <summary>
/// `--smoothtest`：**中心线曲线化的自检**（纯算法，不建窗口、不碰输入）。
///
/// 它是 2026-09-28 那次"先测第一条路"的验收尺子，量四件事：
///   ① 直角**不许变形**（这是用户唯一的硬约束）；
///   ② 圆弧要真的变圆滑（和折线比，同样弧长上的最大转角明显下降）；
///   ③ 抛物线这类手画曲线**形状不许被改**（到原折线的最大偏差有上限）；
///   ④ 退化输入不许崩（重复点 / 两点 / 一点）。
///
/// 关于"自己不能给自己打分"：直角那一条的判据用的是**原始折线**（输入自己），
/// 不是曲线器的实现细节；形状那条用的是逐点到折线的欧氏距离。两者都和
/// <see cref="StrokeSmoothing"/> 的算法无关，所以不是自证。
/// </summary>
internal static class SmoothProbe
{
    public static int Run()
    {
        Console.WriteLine("=== 中心线曲线化自检（过点 Catmull-Rom ＋ 角点保护）===");
        Console.WriteLine($"  角点阈值 {StrokeSmoothing.CornerAngleDeg}° · 窗口 {StrokeSmoothing.CornerWindow} · " +
                          $"抽稀 {1.5f}°/6px");
        Console.WriteLine();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-44} {detail}");
            if (ok) pass++; else fail++;
        }

        // ---- ① 直角：角点保护打开 → 曲线不许离开两条臂 ------------------------
        var corner = RightAngle(armPts: 12, step: 30f);
        float bulgeOn = CornerBulge(corner);
        Check("直角：曲线不鼓出两条臂（偏差 ≤ 0.5px）", bulgeOn <= 0.5f,
              $"最远离开折线 {bulgeOn:F2}px");

        // 反例：关掉角点保护，同一条输入应当真的鼓出来——证明保护不是摆设
        StrokeSmoothing.CornerProtection = false;
        float bulgeOff = CornerBulge(corner);
        StrokeSmoothing.CornerProtection = true;
        Check("反例：关掉角点保护后确实鼓出（证明保护在起作用）", bulgeOff > bulgeOn + 0.5f,
              $"关 {bulgeOff:F2}px vs 开 {bulgeOn:F2}px");

        // 锯齿（周期 2 的连续尖角）：**窗口判据会跨过一个周期看成直线**，
        // 所以必须靠"局部转角"兜住；这条是 2026-09-28 出图时发现的漏网，别再漏。
        var zig = new List<Vector2>();
        for (int i = 0; i <= 12; i++) zig.Add(new Vector2(i * 55f, (i % 2 == 0) ? 0f : 110f));
        float zigBulge = MaxDistanceToPolyline(Flatten(BuildSegs(zig), 32), zig);
        Check("锯齿：周期 2 的尖角也保住（偏差 ≤ 0.5px）", zigBulge <= 0.5f, $"{zigBulge:F2}px");

        // 慢画直角：90° 摊成三段、每段只有 30°（比阈值小）——只有窗口判据能兜住它
        var slow = new List<Vector2> { new(0f, 0f), new(30f, 0f) };
        for (int k = 1; k <= 3; k++)
        {
            var d = new Vector2(MathF.Cos(k * MathF.PI / 6f), MathF.Sin(k * MathF.PI / 6f));
            slow.Add(slow[^1] + d * 30f);
        }
        float slowBulge = MaxDistanceToPolyline(Flatten(BuildSegs(slow), 32), slow);
        Check("慢画直角（90° 摊成三段 30°）：不被磨圆（≤ 0.5px）", slowBulge <= 0.5f, $"{slowBulge:F2}px");

        // ---- ② 圆弧：同样 6px 弧长上的最大转角要明显下降 ----------------------
        var arc = Circle(radius: 400f, points: 36);          // 每 10° 一个点，和"快画"同量级
        var rawArc = new List<Vector2>(arc);
        var smoothArc = Flatten(BuildSegs(arc), 64);
        float turnRaw = MaxTurnOverWindow(rawArc, 6f);
        float turnSmooth = MaxTurnOverWindow(smoothArc, 6f);
        Check("圆弧：最大转角至少降到折线的六成以下", turnSmooth <= turnRaw * 0.6f,
              $"折线 {turnRaw:F1}° → 曲线 {turnSmooth:F1}°");

        // ---- ③ 抛物线：形状不许被改（到原折线的最大偏差 ≤ 1px）----------------
        var parab = new List<Vector2>();
        for (int i = 0; i <= 32; i++)
        {
            float x = -400f + i * 25f;
            parab.Add(new Vector2(x, x * x / 600f));
        }
        float devP = MaxDistanceToPolyline(Flatten(BuildSegs(parab), 32), parab);
        Check("抛物线：到原折线的最大偏差 ≤ 1px", devP <= 1.0f, $"{devP:F3}px");

        // ---- ④ 直线：不许过冲（控制点都该落在直线上）-------------------------
        var line = new List<Vector2>();
        for (int i = 0; i < 20; i++) line.Add(new Vector2(i * 25f, 70f));
        float devL = MaxDistanceToPolyline(Flatten(BuildSegs(line), 16), line);
        Check("直线：曲线不离开直线（偏差 ≤ 0.05px）", devL <= 0.05f, $"{devL:F4}px");

        // ---- ⑤ 退化输入 -------------------------------------------------------
        Check("退化：一个点 → 不给曲线（段数 0）", BuildSegs(new List<Vector2> { new(5f, 5f) }).Count == 0);
        Check("退化：两个重合点 → 去重后不给曲线",
              BuildSegs(new List<Vector2> { new(5f, 5f), new(5f, 5f) }).Count == 0);
        int two = BuildSegs(new List<Vector2> { new(0f, 0f), new(100f, 0f) }).Count;
        Check("退化：两个不同点 → 恰好一段（直线）", two == 1, $"段数 {two}");

        // ⑦ 鼠标抖动路径：**残影的硬保证**——曲线不许跑出"采样点包围盒 + 1px"。
        //
        // 为什么单独有这一条（2026-09-28 用户实测到的"笔尖附近一直闪残影"）：
        // 擦除范围是按**原始采样点**的包围盒算的（Stroke.Bounds / PaddedBounds），
        // 曲线只要鼓到盒子外面，那几像素就擦不掉。三次贝塞尔整条都在四个控制点的
        // 凸包里，所以"控制点待在盒子附近"就是"曲线待在盒子附近"。
        var rnd = new Random(11);
        var mouse = new List<Vector2> { new(0f, 0f) };
        float ang = 0f;
        for (int i = 0; i < 400; i++)
        {
            // 每点拐 ±10°（低于角点阈值 → 全都会被平滑）、步长 2~32px 忽长忽短，
            // 再取整到像素——这是鼠标能造出来的最难受的输入。
            ang += (float)(rnd.NextDouble() * 2 - 1) * 10f * (MathF.PI / 180f);
            float step = 2f + (float)rnd.NextDouble() * 30f;
            var prev = mouse[^1];
            mouse.Add(new Vector2(
                MathF.Round(prev.X + MathF.Cos(ang) * step),
                MathF.Round(prev.Y + MathF.Sin(ang) * step)));
        }
        var mouseFlat = Flatten(BuildSegs(mouse), 24);
        float boxOut = MaxOutOfBox(mouseFlat, mouse, 1f);
        float mouseDev = MaxDistanceToPolyline(mouseFlat, mouse);
        Check("鼠标抖动：曲线不跑出采样点包围盒（≤1px，残影的硬保证）", boxOut <= 1f,
              $"超出 {boxOut:F2}px；到折线最大偏差 {mouseDev:F2}px");

        // ---- ⑥ 性能：两千点的一条长笔（活笔每帧都要重算）---------------------
        var big = Circle(radius: 900f, points: 2000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int segs = BuildSegs(big).Count;
        sw.Stop();
        Check("性能：2000 点一次曲线化 ≤ 5ms", sw.Elapsed.TotalMilliseconds <= 5.0,
              $"{sw.Elapsed.TotalMilliseconds:F2}ms / {segs} 段");

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 条：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        return fail == 0 ? 0 : 1;
    }

    // ---- 造样本 ---------------------------------------------------------------

    /// <summary>直角：先沿 +x 走，再沿 +y 走（拐点清晰、两条臂各 12 个点）。</summary>
    private static List<Vector2> RightAngle(int armPts, float step)
    {
        var pts = new List<Vector2>();
        for (int i = 0; i < armPts; i++) pts.Add(new Vector2(i * step, 0f));
        for (int i = 1; i <= armPts; i++) pts.Add(new Vector2((armPts - 1) * step, i * step));
        return pts;
    }

    private static List<Vector2> Circle(float radius, int points)
    {
        var pts = new List<Vector2>(points);
        for (int i = 0; i < points; i++)
        {
            float a = MathF.Tau * i / points;
            pts.Add(new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius));
        }
        // 闭合前的最后一点回到起点附近会让"末点必留"把一圈收尾连成一小段，
        // 这里不闭合（自检只关心曲率），保持开放弧。
        return pts;
    }

    // ---- 跑曲线器并把结果拷出来（缓冲是复用的，必须拷）------------------------

    private static List<StrokeSmoothing.Seg> BuildSegs(List<Vector2> pts)
    {
        StrokeSmoothing.Begin();
        foreach (var p in pts) StrokeSmoothing.Add(p.X, p.Y, 0.5f);
        int n = StrokeSmoothing.Finish();
        var copy = new List<StrokeSmoothing.Seg>(Math.Max(0, n));
        for (int i = 0; i < n; i++) copy.Add(StrokeSmoothing.Out[i]);
        return copy;
    }

    // ---- 量法 ----------------------------------------------------------------

    /// <summary>把贝塞尔段按每段 <paramref name="perSeg"/> 段折线展开。</summary>
    private static List<Vector2> Flatten(List<StrokeSmoothing.Seg> segs, int perSeg)
    {
        var pts = new List<Vector2>(Math.Max(2, segs.Count * perSeg + 1));
        foreach (var s in segs)
        {
            for (int i = 0; i <= perSeg; i++)
            {
                float t = i / (float)perSeg;
                // 三次贝塞尔：B(t) = (1-t)³P0 + 3(1-t)²t·C1 + 3(1-t)t²·C2 + t³P1
                float u = 1f - t;
                Vector2 p = u * u * u * s.P0
                          + 3f * u * u * t * s.C1
                          + 3f * u * t * t * s.C2
                          + t * t * t * s.P1;
                if (pts.Count == 0 || Vector2.DistanceSquared(pts[^1], p) > 1e-8f) pts.Add(p);
            }
        }
        return pts;
    }

    /// <summary>直角那一条：曲线在拐点附近最远离开"两条臂"多少（px）。</summary>
    private static float CornerBulge(List<Vector2> raw)
    {
        var segs = BuildSegs(raw);
        var flat = Flatten(segs, 48);
        Vector2 corner = raw[^13];                       // 两条臂的交点
        float worst = 0f;
        foreach (var p in flat)
        {
            if (Vector2.Distance(p, corner) > 90f) continue;   // 只看拐点附近
            worst = MathF.Max(worst, DistanceToPolyline(p, raw));
        }
        return worst;
    }

    /// <summary>
    /// 一段点串里，任意 <paramref name="windowPx"/> 弧长窗口两端的**方向变化**（度），取最大。
    ///
    /// 先按弧长重采样成等距（0.5px）再量：折线的点距可能有几十像素，直接拿原始点滑窗
    /// 会整个跳过顶点，量出 0° 这种假数（2026-09-28 自己踩过，圆弧那条就是这么假失败的）。
    /// </summary>
    private static float MaxTurnOverWindow(List<Vector2> pts, float windowPx)
    {
        var dense = Resample(pts, 0.5f);
        float worst = 0f;
        for (int i = 0; i + 2 < dense.Count; i++)
        {
            float acc = 0f;
            int j = i + 1;
            while (j < dense.Count && acc < windowPx)
            {
                acc += Vector2.Distance(dense[j - 1], dense[j]);
                j++;
            }
            if (acc < windowPx * 0.8f) break;
            Vector2 d0 = dense[i + 1] - dense[i];
            Vector2 d1 = dense[j - 1] - dense[j - 2];
            if (d0.LengthSquared() < 1e-9f || d1.LengthSquared() < 1e-9f) continue;
            worst = MathF.Max(worst, AngleDeg(Vector2.Normalize(d0), Vector2.Normalize(d1)));
        }
        return worst;
    }

    /// <summary>按弧长重采样成间隔约 <paramref name="stepPx"/> 的点串。</summary>
    private static List<Vector2> Resample(List<Vector2> pts, float stepPx)
    {
        var outp = new List<Vector2> { pts[0] };
        float carry = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i];
            float len = Vector2.Distance(a, b);
            if (len < 1e-6f) continue;
            Vector2 dir = (b - a) / len;
            float t = stepPx - carry;
            while (t <= len)
            {
                outp.Add(a + dir * t);
                t += stepPx;
            }
            carry = len - (t - stepPx);
        }
        if (outp.Count == 0 || Vector2.Distance(outp[^1], pts[^1]) > 1e-6f) outp.Add(pts[^1]);
        return outp;
    }

    private static float AngleDeg(Vector2 a, Vector2 b)
    {
        float c = Math.Clamp(Vector2.Dot(a, b), -1f, 1f);
        return MathF.Acos(c) * (180f / MathF.PI);
    }

    /// <summary>
    /// 曲线（已扁平化）最多跑到"原始采样点包围盒 + <paramref name="margin"/>px"外面多远。
    /// **残影的判据**：>0 就意味着有像素擦不掉（脏区是按原始点的包围盒算的）。
    /// </summary>
    private static float MaxOutOfBox(List<Vector2> flat, List<Vector2> raw, float margin)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in raw)
        {
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
        }
        float worst = 0f;
        foreach (var p in flat)
        {
            float dx = MathF.Max(0f, MathF.Max(minX - p.X, p.X - maxX));
            float dy = MathF.Max(0f, MathF.Max(minY - p.Y, p.Y - maxY));
            float d = MathF.Sqrt(dx * dx + dy * dy) - margin;
            if (d > worst) worst = d;
        }
        return worst;
    }

    private static float MaxDistanceToPolyline(List<Vector2> pts, List<Vector2> poly)
    {
        float worst = 0f;
        foreach (var p in pts) worst = MathF.Max(worst, DistanceToPolyline(p, poly));
        return worst;
    }

    private static float DistanceToPolyline(Vector2 p, List<Vector2> poly)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < poly.Count; i++)
            best = MathF.Min(best, DistanceToSegment(p, poly[i], poly[i + 1]));
        return best;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq <= 1e-9f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
