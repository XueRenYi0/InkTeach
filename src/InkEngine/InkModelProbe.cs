// [停用 2026-10-05] 本文件的渲染实验已停用（入口开关已注释，代码保留）。
// 见 已停用-渲染实验.md：恢复方法 + 停用前完整源码备份（.revert/2026-10-05-渲染减法）。
using System.Numerics;

namespace InkEngine;

/// <summary>
/// `--inkmodeltest`：**ink-stroke-modeler 1:1 移植的自检**（纯算法，不建窗口、不碰输入）。
///
/// 它是"单源对照实验"第一轮的量尺，量五件事：
///   ① 整数像素采样（模拟 `ptPixelLocation`）造成的横向抖动，被模型吃掉多少；
///   ② 端点不许跑（首点必须完全一致，末点要在收笔追赶后回到最后输入附近）；
///   ③ **直角会被弹簧模型圆掉多少**（这是和你们现有"角点保护"最大的差别，如实记录）；
///   ④ 结果"只增不改"（前缀稳定）——活笔每帧增量喂点的前提；
///   ⑤ 退化输入不崩 + 2000 点一次建模的耗时。
///
/// 判据都对着**外部事实**量：抖动用"到已知直线的残差"，端点用输入自身，
/// 前缀稳定用"两次独立建模的逐点比对"——不拿模型器内部状态自证。
/// </summary>
internal static class InkModelProbe
{
    private const double InputHz = 130;          // 常见真笔采样率
    private const float InputSpeedPxPerSec = 400; // 书写速度量级（画布物理像素）

    public static int Run()
    {
        var p = InkStrokeModelParams.Recommended();
        Console.WriteLine("=== 墨迹模型自检（ink-stroke-modeler 1:1 移植，单源对照第一轮）===");
        Console.WriteLine($"  输入 {InputHz:F0}Hz / 速度 {InputSpeedPxPerSec:F0}px/s；"
                          + $"wobble {p.WobbleSmootherParams.Timeout * 1000:F0}ms、"
                          + $"弹簧 {p.PositionModelerParams.SpringMassConstant:F6}、"
                          + $"阻尼 {p.PositionModelerParams.DragConstant:F0}、"
                          + $"输出 {p.SamplingParams.MinOutputRate:F0}Hz");
        Console.WriteLine();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-52} {detail}");
            if (ok) pass++; else fail++;
        }

        // 预热一次，避免把 JIT 算进性能数字。
        ModelSync(BuildStraight(0.013f, 100f, 60).pts, hasPressure: false, out _, out _, out _);

        // ---- ① 防抖：分两种噪声量 ---------------------------------------------
        // a) 高频噪声（模型的目标）：真笔噪声 + 整数取整，逐点独立。
        var (noisyPts, noisyTimes) = BuildStraightWithNoise(0.013f, 100f, 400, noisePx: 0.5f, seed: 7);
        ModelSync(noisyPts, hasPressure: false, out _, out var noisyModeled, out _);
        float noisyRawRms = LateralRms(noisyPts, 0.013f, 100f);
        float noisyModelRms = LateralRms(noisyModeled, 0.013f, 100f);
        Check("高频抖动：模型横向 RMS ≤ 原始一半（且 ≤0.15px）",
              noisyModelRms <= MathF.Max(0.15f, noisyRawRms * 0.5f),
              $"原始 {noisyRawRms:F3}px → 模型 {noisyModelRms:F3}px（{noisyModeled.Count} 点）");

        // b) 纯整数量化（浅斜率直线 ≈1.7Hz 锯齿）：这是**低频**残差，任何低延迟滤波
        //    都去不掉（要动它就得动形状/加滞后）。如实记录，让它和上面那条区分开。
        var (linePts, lineTimes) = BuildStraight(0.013f, 100f, 400);
        ModelSync(linePts, hasPressure: false, out _, out var lineModeled, out _);
        float rawRms = LateralRms(linePts, 0.013f, 100f);
        float modelRms = LateralRms(lineModeled, 0.013f, 100f);
        Check("纯量化（浅斜率）：低频谱残差，记录值（不参与通过判定）", true,
              $"整数采样 {rawRms:F3}px → 模型 {modelRms:F3}px");

        // ---- ② 端点 -----------------------------------------------------------
        float dStart = Vector2.Distance(lineModeled[0], linePts[0]);
        float dEnd = Vector2.Distance(lineModeled[^1], linePts[^1]);
        Check("端点：首点完全一致（≤0.01px）、末点收笔后 ≤0.5px",
              dStart <= 0.01f && dEnd <= 0.5f,
              $"首 {dStart:F4}px、末 {dEnd:F3}px");

        // ---- ③ 直角：如实量"被圆掉多少" --------------------------------------
        var (cornerPts, cornerTimes) = BuildCorner(armPx: 400f);
        ModelSync(cornerPts, hasPressure: false, out _, out var cornerModeled, out _);
        float cornerDev = MaxDistanceToPolyline(cornerModeled, cornerPts);
        Check("直角：到原折线最大距离（记录值，供与过点曲线对比）", true,
              $"被圆掉最多 {cornerDev:F2}px（你们现有角点保护是 ≤0.5px）");

        // ---- ④ 输出率 + 前缀稳定（只增不改）----------------------------------
        var (arcPts, arcTimes) = BuildArc(400f, 800);
        ModelSync(arcPts, hasPressure: false, out _, out var arcFull, out _, feedUp: true);
        int prefixCount = 500;
        ModelSync(arcPts.GetRange(0, prefixCount), hasPressure: false, out _, out var arcPrefix, out _, feedUp: false);
        double duration = arcTimes[^1] - arcTimes[0];
        Check("输出率：130Hz 输入 → 输出点数 ≥ 170Hz",
              arcFull.Count >= duration * 170,
              $"{arcFull.Count} 点 / {duration:F2}s = {arcFull.Count / duration:F0}Hz");

        int compare = Math.Min(arcPrefix.Count, arcFull.Count);
        float worstPrefix = 0f;
        for (int i = 0; i < compare; i++)
            worstPrefix = MathF.Max(worstPrefix, Vector2.Distance(arcPrefix[i], arcFull[i]));
        Check("前缀稳定：先建模前 500 点、再建模全条，前段逐点一致（≤0.01px）",
              compare >= prefixCount && worstPrefix <= 0.01f,
              $"比对 {compare} 点，最大差 {worstPrefix:F5}px");

        // ---- ⑤ 压力插值：斜坡 0→1 不许越界/抖动 ------------------------------
        var (pressPts, pressTimes) = BuildStraight(0.013f, 100f, 300);
        ModelSync(pressPts, hasPressure: true, out _, out _, out var pressModeled);
        bool pressOk = pressModeled.Count >= 2;
        float prevP = -1f;
        float maxDip = 0f;
        foreach (var pressure in pressModeled)
        {
            if (!float.IsFinite(pressure) || pressure < -0.001f || pressure > 1.001f) pressOk = false;
            if (prevP >= 0 && pressure < prevP)
                maxDip = MathF.Max(maxDip, prevP - pressure);
            prevP = pressure;
        }
        Check("压力：插值结果在 [0,1] 且随斜坡不回跳（回跳 ≤0.05）", pressOk && maxDip <= 0.05f,
              $"回跳峰值 {maxDip:F3}");

        // ---- ⑥ 退化输入 -------------------------------------------------------
        bool degenerateOk;
        try
        {
            ModelSync(new List<Vector2> { new(0, 0), new(100, 0) }, false, out _, out var two, out _);
            ModelSync(new List<Vector2> { new(0, 0), new(10, 0), new(20, 0), new(30, 0) },
                      false, out _, out var dup, out _, duplicateTimes: true);
            degenerateOk = two.Count >= 2 && dup.Count >= 1;
        }
        catch (Exception ex)
        {
            degenerateOk = false;
            Console.WriteLine("        异常：" + ex.Message);
        }
        Check("退化：两点 / 重复时刻不崩且有点输出", degenerateOk);

        // ---- ⑦ 性能：2000 点一次建模（落笔/重画都要用）-----------------------
        var (bigPts, bigTimes) = BuildArc(900f, 2000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ModelSync(bigPts, hasPressure: true, out _, out var bigModeled, out _);
        sw.Stop();
        Check("性能：2000 点一次建模 ≤ 20ms", sw.Elapsed.TotalMilliseconds <= 20.0,
              $"{sw.Elapsed.TotalMilliseconds:F2}ms / {bigModeled.Count} 点输出");

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 条：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        return fail == 0 ? 0 : 1;
    }

    // ---- 建模（直接喂上游移植；探针不走引擎的缓存/缓冲）-------------------------

    private static void ModelSync(List<Vector2> pts, bool hasPressure,
                                  out List<InkResult> results,
                                  out List<Vector2> positions, out List<float> pressures,
                                  bool feedUp = true, bool duplicateTimes = false)
    {
        var model = new InkStrokeModel();
        model.Reset(InkStrokeModelParams.Recommended());
        results = new List<InkResult>();

        double TimeAt(int i)
            => duplicateTimes ? 0 : (i * (1.0 / InputHz)) * (InputSpeedPxPerSec / 400.0);

        for (int i = 0; i < pts.Count; i++)
        {
            var type = i == 0 ? InkEventType.Down : InkEventType.Move;
            float pressure = hasPressure ? i / (float)Math.Max(1, pts.Count - 1) : -1f;
            model.Update(InkInput.Make(type, new InkVec2(pts[i].X, pts[i].Y), TimeAt(i), pressure), results);
        }
        if (feedUp && pts.Count > 0)
        {
            int last = pts.Count - 1;
            float pressure = hasPressure ? 1f : -1f;
            model.Update(InkInput.Make(InkEventType.Up, new InkVec2(pts[last].X, pts[last].Y), TimeAt(last), pressure), results);
        }

        positions = new List<Vector2>(results.Count);
        pressures = new List<float>(results.Count);
        foreach (var r in results)
        {
            positions.Add(new Vector2(r.Position.X, r.Position.Y));
            pressures.Add(r.Pressure);
        }
    }

    // ---- 造样本（坐标取整 = 模拟 WM_POINTER 的整数像素）-----------------------

    private static (List<Vector2> pts, List<double> times) BuildStraight(float k, float b, int count)
    {
        var pts = new List<Vector2>(count);
        var times = new List<double>(count);
        float spacing = InputSpeedPxPerSec / (float)InputHz;
        for (int i = 0; i < count; i++)
        {
            float x = i * spacing;
            pts.Add(new Vector2(MathF.Round(x), MathF.Round(b + k * x)));
            times.Add(i / InputHz);
        }
        return (pts, times);
    }

    /// <summary>直线 + 逐点独立横向噪声（模拟真笔噪声），最后同样取整到像素。</summary>
    private static (List<Vector2> pts, List<double> times) BuildStraightWithNoise(
        float k, float b, int count, float noisePx, int seed)
    {
        var rnd = new Random(seed);
        var pts = new List<Vector2>(count);
        var times = new List<double>(count);
        float spacing = InputSpeedPxPerSec / (float)InputHz;
        for (int i = 0; i < count; i++)
        {
            float x = i * spacing;
            float noise = (float)(rnd.NextDouble() * 2 - 1) * noisePx;
            pts.Add(new Vector2(MathF.Round(x), MathF.Round(b + k * x + noise)));
            times.Add(i / InputHz);
        }
        return (pts, times);
    }

    private static (List<Vector2> pts, List<double> times) BuildCorner(float armPx)
    {
        // 慢速直角：每臂 400px，按 200px/s 走（比直线那条更慢 → 弹簧更容易圆角）。
        const float step = 20f;
        const double speed = 200;
        var pts = new List<Vector2>();
        var times = new List<double>();
        double t = 0;
        for (float x = 0; x <= armPx; x += step) { pts.Add(new Vector2(MathF.Round(x), 0)); times.Add(t); t += step / speed; }
        for (float y = step; y <= armPx; y += step) { pts.Add(new Vector2(armPx, MathF.Round(y))); times.Add(t); t += step / speed; }
        return (pts, times);
    }

    private static (List<Vector2> pts, List<double> times) BuildArc(float radius, int points)
    {
        var pts = new List<Vector2>(points);
        var times = new List<double>(points);
        for (int i = 0; i < points; i++)
        {
            float a = MathF.PI * 1.5f * i / (points - 1);
            pts.Add(new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius));
            times.Add(i / InputHz);
        }
        return (pts, times);
    }

    // ---- 量法 -----------------------------------------------------------------

    /// <summary>到已知直线 y = b + kx 的横向残差 RMS（除以 sqrt(1+k²)）。</summary>
    private static float LateralRms(List<Vector2> pts, float k, float b)
    {
        if (pts.Count == 0) return float.MaxValue;
        double sum = 0;
        float norm = MathF.Sqrt(1 + k * k);
        foreach (var p in pts)
        {
            float d = (p.Y - (b + k * p.X)) / norm;
            sum += d * d;
        }
        return (float)Math.Sqrt(sum / pts.Count);
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
