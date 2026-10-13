using System.Numerics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// **预测器离线评测**（`--pdmetrics`）：拿真实语料逐点过**当前预测器**，量四件事——
/// **供给率 / 吃到率 / 角度误差 / 门触发频率**——让"参数改动"由数据说话（2026-10-08 大数据管线第二环）。
///
/// 口径（都有出处）：
///   · **一步预测对比**：预测器在看到第 i-1 点为止的数据时，先要一个"下一步"的预测；
///     误差 = |预测点 − 真实第 i 点|；**吃到 = 1 − 平均误差 / 平均"不预测"基线**（同 `--predictdata` 老口径）。
///   · **角度误差** = 预测方向 vs 真实方向（三星/Google 专利："用户对角度误差比距离误差更敏感"）。
///   · **供给率** = 给得出预测点的比例（门 / 低速 / 断笔都会把它打下去——中文"折钩"多，这条要盯住）。
///   · **门触发** = 急转门 / 反向门各触发几次（每千点）；**超前** = 预测越过真实点的像素（p95 兜底用）。
///
/// 用法（pwsh 7）：
///   InkTeach.exe --pdmetrics <语料.txt> [--dt 15] [--limit 20000] [--sweep]
/// 语料格式 = `tools/datasets/Export-SCUT-onHCC.ps1` 的输出（`#sample` 行 + 每笔一行的 `x,y x,y`）。
/// </summary>
internal static class PdMetricsProbe
{
    private readonly record struct Pt(float X, float Y);

    public static int Run(string[] args)
    {
        string corpus = null;
        float dt = 15f;                 // 采样间隔（ms）；SCUT 是 PDA 稀疏采样，先按 15ms 估
        int limit = int.MaxValue;
        bool sweep = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pdmetrics" && i + 1 < args.Length) corpus = args[i + 1];
            else if (args[i] == "--dt" && i + 1 < args.Length && float.TryParse(args[i + 1], out var d)) dt = d;
            else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var l)) limit = l;
            else if (args[i] == "--sweep") sweep = true;
        }

        if (string.IsNullOrWhiteSpace(corpus) || !File.Exists(corpus))
        {
            Console.WriteLine("  用法：--pdmetrics <语料.txt> [--dt 15] [--limit 20000] [--sweep]");
            return 2;
        }

        Console.WriteLine($"=== 预测器离线评测（--pdmetrics）===");
        Console.WriteLine($"  语料：{corpus}");
        Console.WriteLine($"  采样间隔假设 dt={dt:F0}ms（不预测基线 = 当前点 → 下一点的距离）");

        var samples = LoadCorpus(corpus, limit);
        int pts = 0;
        long total = 0;
        foreach (var s in samples) { pts += s.Count; total++; }
        Console.WriteLine($"  样本 {total} 条、点 {pts} 个");
        Console.WriteLine();
        Console.WriteLine("  转度阈值 | 供给率  吃到率  角度均值 角度p95 | 超前均值 超前p95 | 急转门/千点 反向门/千点");
        Console.WriteLine("  ---------|-----------------------------------|-----------------|--------------------");

        foreach (var deg in (sweep ? new[] { 180f, 90f, 75f, 60f, 50f } : new[] { 60f }))
            RunConfig(samples, dt, deg);

        return 0;
    }

    // ---------------------------------------------------------------- 单配置评测

    private static void RunConfig(List<List<Pt>> samples, float dt, float turndeg)
    {
        var p = new InkPredictor
        {
            HorizonMs = Math.Clamp(dt, InkPredictor.MinHorizonMs, InkPredictor.HardMaxHorizonMs),
            Damping = 0.8f,
            VelocitySmoothing = 0.4f,      // 笔路口径（关 ink 鼠标路是 0.6；数据集按笔看）
            SharpTurnCos = (float)Math.Cos(Math.Max(0.0, Math.Min(180.0, turndeg)) * Math.PI / 180.0),
        };
        var predBuf = new PredictedPoint[6];

        double sumErr = 0, sumBase = 0, sumAngle = 0, sumOver = 0;
        long nErr = 0, nPoints = 0, nUnavail = 0;
        long gates = 0, reversals = 0;
        var angles = new List<float>();
        var overs = new List<float>();

        foreach (var pts in samples)
        {
            if (pts.Count < 3) continue;
            p.Reset();
            double t = 0;
            p.Add(pts[0].X, pts[0].Y, t);
            for (int i = 1; i < pts.Count; i++)
            {
                var prev = pts[i - 1];
                var act = pts[i];
                nPoints++;

                int n = p.Predict(predBuf);
                if (n > 0)
                {
                    var tip = predBuf[n - 1];          // 最远那一点（= 地平线处）
                    double err = Math.Sqrt((tip.X - act.X) * (tip.X - act.X) + (tip.Y - act.Y) * (tip.Y - act.Y));
                    double baseLen = Math.Sqrt((act.X - prev.X) * (act.X - prev.X) + (act.Y - prev.Y) * (act.Y - prev.Y));
                    sumErr += err; sumBase += baseLen;

                    // 角度误差：预测方向 vs 真实方向
                    double pvx = tip.X - prev.X, pvy = tip.Y - prev.Y;
                    double avx = act.X - prev.X, avy = act.Y - prev.Y;
                    double pl = Math.Sqrt(pvx * pvx + pvy * pvy), al = Math.Sqrt(avx * avx + avy * avy);
                    if (pl > 1e-3 && al > 1e-3)
                    {
                        double cos = Math.Max(-1.0, Math.Min(1.0, (pvx * avx + pvy * avy) / (pl * al)));
                        float ang = (float)(Math.Acos(cos) * 180.0 / Math.PI);
                        sumAngle += ang; angles.Add(ang);
                    }
                    // 超前：预测点越过真实点的像素（沿真实步方向投影）
                    if (al > 1e-3)
                    {
                        double proj = (pvx * avx + pvy * avy) / al;
                        if (proj > al)
                        {
                            float over = (float)(proj - al);
                            sumOver += over; overs.Add(over);
                        }
                    }
                    nErr++;
                }
                else nUnavail++;

                t += dt;
                p.Add(act.X, act.Y, t);
            }
            gates += p.GateFires; reversals += p.ReversalFires;
        }

        static string P95(List<float> a)
        {
            if (a.Count == 0) return "  -";
            a.Sort();
            return $"{a[(int)Math.Min(a.Count - 1, a.Count * 0.95)]:F1}";
        }

        double eat = sumBase > 0 ? (1.0 - sumErr / sumBase) * 100.0 : 0;
        double supply = nPoints > 0 ? 100.0 * nErr / nPoints : 0;
        double angMean = angles.Count > 0 ? sumAngle / angles.Count : 0;
        Console.WriteLine($"  {turndeg,7:F0}° | {supply,5:F1}%  {eat,5:F1}%  {angMean,6:F1}°  {P95(angles),6}°"
                          + $" | {sumOver / Math.Max(1, nErr),7:F1}px {P95(overs),6}px"
                          + $" | {1000.0 * gates / Math.Max(1, nPoints),8:F1} {1000.0 * reversals / Math.Max(1, nPoints),9:F1}");
    }

    // ---------------------------------------------------------------- 语料读取

    /// <summary>
    /// 读 `Export-SCUT-onHCC.ps1` 的输出（`#SCUT-onHCC v1` 头；`#sample …` 起一条；
    /// 每笔一行 `x,y x,y …`；空行结束）。只取坐标，别的字段忽略。
    /// </summary>
    private static List<List<Pt>> LoadCorpus(string path, int limit)
    {
        var samples = new List<List<Pt>>();
        var cur = new List<Pt>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (cur.Count >= 3)
                {
                    samples.Add(cur);
                    if (samples.Count >= limit) return samples;
                }
                cur = new List<Pt>();
                continue;
            }
            if (line.StartsWith('#')) continue;
            foreach (var tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int comma = tok.IndexOf(',');
                if (comma <= 0) continue;
                if (float.TryParse(tok.AsSpan(0, comma), out var x)
                    && float.TryParse(tok.AsSpan(comma + 1), out var y))
                    cur.Add(new Pt(x, y));
            }
        }
        if (cur.Count >= 3 && samples.Count < limit) samples.Add(cur);
        return samples;
    }
}
