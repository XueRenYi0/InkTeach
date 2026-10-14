using InkEngine;

namespace InkTeach;

/// <summary>
/// 用**真实笔迹数据**评测预测器（不是合成的圆滑曲线）。
///
/// 数据：UCI「Character Trajectories」——2858 条真实手写轨迹、200 Hz（Δt=5 ms）、
/// 带笔尖压力，共 48.7 万个采样点（约 40 分钟真实书写）。
/// 导出脚本：<c>tools/datasets/Export-CharacterTrajectories.py</c>。
///
/// 做法：把每条轨迹按 5 ms 一格喂进预测器，再拿"向前 H 毫秒的**真实**位置"
/// 与"预测位置"比。两个指标各自回答一个问题：
///
///   · **吃到** = 1 − 预测误差 / 不预测误差 —— 把多少滞后补回来了（越大越好）；
///   · **超前** = 预测点越过真实点、沿运动方向的投影，**除以"笔在这段时间走的距离"**
///     —— 就是"墨跑到笔尖前面"的比例（越小越好，负值=还落在后面）。
///
/// 之所以用比例而不是像素：数据集坐标的绝对尺度与屏幕无关，
/// 比例才是"会不会看出甩墨"这件事的可比口径。
/// </summary>
internal static class PredictEval
{
    // 注意：预测器把地平线夹在 8~15 ms（产品决定），所以评测也只用这个区间——
    // 早先列表里有 5 ms，那一列实际是"用 8 ms 的预测去对 5 ms 的真实"，是假数据。
    private static readonly double[] EvalHorizonsMs = { 8, 10, 12, 15 };

    /// <summary>被评测的参数组（名字, 加速度衰减, 整体阻尼, 速度平滑）。</summary>
    private static readonly (string Name, float AccelDamping, float Damping, float VelSmooth)[] EvalVariants =
    {
        ("一阶 · 阻尼 0.80（对照）", 0f, 0.80f, 0f),
        ("二阶 · 阻尼 0.70（原默认）", 0.4f, 0.70f, 0f),
        ("二阶 · 阻尼 0.80（现默认）", 0.4f, 0.80f, 0f),
        ("二阶 · 阻尼 0.80 + 速度平滑 0.3", 0.4f, 0.80f, 0.30f),
        ("二阶 · 阻尼 0.80 + 速度平滑 0.5", 0.4f, 0.80f, 0.50f),
        ("二阶 · 阻尼 0.80 + 速度平滑 0.7", 0.4f, 0.80f, 0.70f),
        ("二阶 · 阻尼 0.90 + 速度平滑 0.5", 0.4f, 0.90f, 0.50f),
        ("二阶 · 阻尼 1.00（不衰减）", 0.4f, 1.00f, 0f),
    };

    public static void Run(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 真实笔迹数据上的预测评测 ===");

        if (Directory.Exists(path))
            path = Path.Combine(path, "character_trajectories.bin");
        if (!File.Exists(path))
        {
            Console.WriteLine($"  SKIP: 找不到数据文件 {path}");
            Console.WriteLine("  先跑：python tools/datasets/Export-CharacterTrajectories.py <mixoutALL_shifted.mat> " + path);
            return;
        }

        var (trajs, dtMs) = LoadTrajectories(path);
        int totalPoints = trajs.Sum(t => t.X.Length);
        Console.WriteLine($"  数据：{trajs.Count} 条真实轨迹，{totalPoints} 个采样点，" +
                          $"采样间隔 {dtMs:F1} ms（≈{1000 / dtMs:F0} Hz）");
        Console.WriteLine($"  尺度：{DescribeScale(trajs, dtMs)}");
        Console.WriteLine($"  地平线：{string.Join(" / ", EvalHorizonsMs.Select(h => $"{h:F0}"))} ms");
        Console.WriteLine();
        Console.WriteLine("  " + "参数组".PadRight(26) +
                          string.Join("", EvalHorizonsMs.Select(h => $"{h,2:F0}ms 吃到/超前".PadLeft(16))) +
                          "   （吃到越大越好；超前 = 越过笔尖的比例，越小越好）");

        var baseLine = Evaluate(trajs, dtMs, accelDamping: 0f, damping: 0f, velSmooth: 0f, predictEnabled: false);
        var results = new List<(string Name, EvalResult R)>();
        foreach (var v in EvalVariants)
        {
            var r = Evaluate(trajs, dtMs, v.AccelDamping, v.Damping, v.VelSmooth, predictEnabled: true);
            results.Add((v.Name, r));
            Console.WriteLine("  " + v.Name.PadRight(26) + r.FormatColumns(baseLine));
        }

        Console.WriteLine();
        Console.WriteLine("  不预测时的滞后（该地平线下笔走过的距离，数据集单位）：" + baseLine.FormatBaseline());
        Console.WriteLine();
        const double LeadCap = 0.8;   // 最坏 1% 情形下，墨最多领先"这段滞后"的 0.8 倍
        Console.WriteLine($"  判读：取「超前 p99 ≤ {LeadCap:F1}×滞后」为可接受，在这个前提下挑吃得最多的。");

        bool Safe(EvalResult r) => Enumerable.Range(0, 4)
            .All(v => r.Baseline[v] <= 1e-9 || r.LeadP99[v] / r.Baseline[v] <= LeadCap);
        var safe = results.Where(x => Safe(x.R)).ToList();
        Console.WriteLine();
        if (safe.Count > 0)
        {
            var best = safe.OrderByDescending(x => x.R.CaptureMean).First();
            Console.WriteLine($"  → 可接受的参数里吃到最多的是：{best.Name}（平均吃到 {best.R.CaptureMean * 100:F1}%）");
        }
        else
        {
            Console.WriteLine($"  → 没有参数组满足「超前 p99 ≤ {LeadCap:F1}×滞后」，应当加大阻尼或缩短地平线");
        }

    }

    private sealed class Traj { public float[] X, Y; }

    private static (List<Traj>, double) LoadTrajectories(string path)
    {
        var list = new List<Traj>();
        using var br = new BinaryReader(File.OpenRead(path));
        var magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'C' || magic[1] != (byte)'T'
            || magic[2] != (byte)'R' || magic[3] != (byte)'J')
            throw new InvalidDataException("不是本工具导出的数据文件（magic 不对）");
        int dtUs = br.ReadInt32();
        int n = br.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int count = br.ReadInt32();
            var xs = new float[count];
            var ys = new float[count];
            for (int k = 0; k < count; k++)
            {
                xs[k] = br.ReadSingle();
                ys[k] = br.ReadSingle();
                _ = br.ReadSingle();      // force：这次不用
            }
            list.Add(new Traj { X = xs, Y = ys });
        }
        return (list, dtUs / 1000.0);
    }

    /// <summary>给个数感：轨迹有多大、笔有多快（数据集单位）。</summary>
    private static string DescribeScale(List<Traj> trajs, double dtMs)
    {
        double bboxSum = 0, moveSum = 0, moveMax = 0;
        long moves = 0;
        foreach (var t in trajs)
        {
            float minX = t.X.Min(), maxX = t.X.Max(), minY = t.Y.Min(), maxY = t.Y.Max();
            bboxSum += Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            for (int i = 1; i < t.X.Length; i++)
            {
                double d = Dist(t.X[i - 1], t.Y[i - 1], t.X[i], t.Y[i]);
                moveSum += d;
                moveMax = Math.Max(moveMax, d);
                moves++;
            }
        }
        double meanMove = moves > 0 ? moveSum / moves : 0;
        return $"轨迹包围盒对角线均值 {bboxSum / trajs.Count:F1}，" +
               $"相邻采样点位移 均值 {meanMove:F3} / 最大 {moveMax:F3}（每 {dtMs:F0} ms）" +
               $"→ 平均速度约 {meanMove / dtMs:F4} 单位/ms";
    }

    private sealed class EvalResult
    {
        public readonly double[] Capture = new double[4];
        public readonly double[] LeadP95 = new double[4];
        public readonly double[] LeadP99 = new double[4];
        public readonly double[] Baseline = new double[4];   // 不预测时该地平线的平均滞后
        public double CaptureMean;

        public string FormatColumns(EvalResult baseLine)
        {
            var parts = new List<string>();
            for (int v = 0; v < 4; v++)
            {
                double baseMean = baseLine.Baseline[v] > 1e-9 ? baseLine.Baseline[v] : 1;
                parts.Add($"{Capture[v] * 100,5:F1}% {LeadP99[v] / baseMean,6:F2}".PadLeft(16));
            }
            return string.Concat(parts);
        }

        public string FormatBaseline()
            => string.Join("  ", EvalHorizonsMs.Select((h, v) => $"{h:F0}ms {Baseline[v]:F3}"));
    }

    /// <summary>把全部轨迹过一遍，统计"预测点 vs 真实未来点"。</summary>
    private static EvalResult Evaluate(List<Traj> trajs, double dtMs,
                                       float accelDamping, float damping, float velSmooth, bool predictEnabled)
    {
        var res = new EvalResult();
        var errs = new List<double>[4];
        var leads = new List<double>[4];
        var baseErrs = new List<double>[4];
        for (int i = 0; i < 4; i++)
        {
            errs[i] = new List<double>(200000);
            leads[i] = new List<double>(200000);
            baseErrs[i] = new List<double>(200000);
        }

        var preds = EvalHorizonsMs.Select(h => new InkPredictor { HorizonMs = h }).ToArray();
        foreach (var p in preds)
        {
            p.AccelDamping = accelDamping;
            p.Damping = damping;
            p.VelocitySmoothing = velSmooth;
            p.ClampHorizon();
        }

        var buf = new PredictedPoint[8];
        double sumNoPred = 0, sumPred = 0;

        foreach (var t in trajs)
        {
            int n = t.X.Length;
            for (int i = 0; i < n; i++)
            {
                double tMs = i * dtMs;
                for (int v = 0; v < preds.Length; v++) preds[v].Add(t.X[i], t.Y[i], tMs);

                if (i == 0 || i + 1 >= n) continue;

                for (int v = 0; v < preds.Length; v++)
                {
                    var (ax, ay) = SampleAt(t, dtMs, tMs + EvalHorizonsMs[v]);
                    if (double.IsNaN(ax)) continue;

                    double noPred = Dist(t.X[i], t.Y[i], ax, ay);
                    double predErr = noPred;

                    if (predictEnabled)
                    {
                        int m = preds[v].Predict(buf);
                        if (m > 0)
                        {
                            var last = buf[m - 1];
                            predErr = Dist(last.X, last.Y, ax, ay);
                            double dx = ax - t.X[i], dy = ay - t.Y[i];
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            if (len > 1e-6)
                                leads[v].Add(((last.X - ax) * dx + (last.Y - ay) * dy) / len);
                        }
                    }

                    baseErrs[v].Add(noPred);
                    errs[v].Add(predErr);
                    sumNoPred += noPred;
                    sumPred += predErr;
                }
            }
        }

        for (int v = 0; v < 4; v++)
        {
            var b = baseErrs[v];
            res.Baseline[v] = b.Count > 0 ? b.Average() : 0;
            var e = errs[v];
            e.Sort();
            double meanErr = e.Count > 0 ? e.Average() : 0;
            res.Capture[v] = res.Baseline[v] > 1e-9 ? 1.0 - meanErr / res.Baseline[v] : 0;
            var o = leads[v];
            o.Sort();
            res.LeadP95[v] = o.Count > 0 ? o[(int)(o.Count * 0.95)] : 0;
            res.LeadP99[v] = o.Count > 0 ? o[(int)(o.Count * 0.99)] : 0;
        }
        res.CaptureMean = sumNoPred > 0 ? 1.0 - sumPred / sumNoPred : 0;
        return res;
    }

    private static double Dist(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    /// <summary>取 t 时刻的真实位置（线性插值；越界返回 NaN）。</summary>
    private static (double X, double Y) SampleAt(Traj t, double dtMs, double tMs)
    {
        double f = tMs / dtMs;
        int i0 = (int)Math.Floor(f);
        int i1 = i0 + 1;
        if (i0 < 0 || i1 >= t.X.Length) return (double.NaN, double.NaN);
        double a = f - i0;
        return (t.X[i0] + (t.X[i1] - t.X[i0]) * a, t.Y[i0] + (t.Y[i1] - t.Y[i0]) * a);
    }
}
