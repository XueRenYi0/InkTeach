using System.Numerics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// **预测器离线评测**（`--pdmetrics`）：拿真实语料逐点过**当前预测器**，量四件事——
/// **供给率 / 吃到率 / 角度误差 / 超前量**——让"参数改动"由数据说话（2026-10-08 大数据管线第二环）。
///
/// 口径（都有出处）：
///   · **一步预测对比**：预测器在看到第 i-1 点为止的数据时，先要一个"下一步"的预测；
///     误差 = |预测点 − 真实第 i 点|；**吃到 = 1 − 平均误差 / 平均"不预测"基线**（同 `--predictdata` 老口径）。
///   · **角度误差** = 预测方向 vs 真实方向（三星/Google 专利："用户对角度误差比距离误差更敏感"）。
///   · **供给率** = 给得出预测点的比例（门 / 低速 / 断笔都会把它打下去——中文"折钩"多，这条要盯住）。
///   · **超前** = 预测越过真实点的像素（p95 兜底用）。
///   · `--sweep` 扫**弧长截断 ArcCap**（0 = 纯 Chromium 行为、6 = 出厂默认）。
///
/// 2026-11-17（T5 最小版，修法 = REVIEW-OPUS §3 裁剪；只做能验证 T1/T2 的部分）：
///   · `--deliver clean|batch:B` **投递模拟**：真机三套输入栈（学校机/桌面/手写板）都是
///     ~16.6ms 一批、批内同毫秒；干净时标下"同刻点"病灶根本不触发——这是"离线好、真机甩尾"
///     的主因。batch:B = 真相轨迹仍按 dt 产生点，但落在同一 B ms 窗口的点打成一批喂预测器，
///     批内时标全取**窗口末尾锚点**（同刻点）；预测发生在每批喂完之后（每批一个评测样本，
///     目标 = 下一真相点）。clean = 现行行为（每点独立时标 t += dt），保持向后兼容。
///   · `--hz 60`：地平线改按产品公式 `clamp(0.6 × 帧周期ms, 4, 10)`（与 Engine.Host 同源，
///     60Hz → 10ms）；不给 `--hz` 保持 `HorizonMs = dt` 老口径，改动前后输出可对照。
///   · **吃到率分母修正**：门控/无速度帧不再只扔进未供给计数，而是 err = baseLen
///     （= 不预测）计入误差——修掉"门控越多吃到率越虚高"（REVIEW-OPUS §3 现状表末两条）。
///     新列 `含门控吃到率` 是修后口径；旧列保留并标注"仅供给帧"。
///   · **分档统计**：速度（<0.3 / 0.3~1 / >1 px·ms⁻¹，真实相邻点距/dt）× 转角
///     （<30° / 30~75° / >75°，相邻三点）——修掉"拐角被直线段淹没"。只输出样本数≥30 的格子。
///   · `--ref none|oracle`（可选，默认不开；可叠加，如 `--ref none --ref oracle`）：
///     none = 恒不预测（吃到率应 = 0%）；oracle = 取真实未来点（第 i 点，距当前一个 dt）
///     并同样过**本行的弧长截断**（出厂 6px）——被 cap 限住的预测器能拿到的逐样本最好水平，
///     作为上限。自证口径：`oracle ≥ current ≥ none`。
///
/// 用法（pwsh 7）：
///   InkTeach.exe --pdmetrics &lt;语料.txt&gt; [--dt 15] [--limit 20000] [--sweep]
///                [--deliver clean|batch:16.6] [--hz 60] [--ref none|oracle]
/// 语料格式 = `tools/datasets/Export-SCUT-onHCC.ps1` 的输出（`#sample` 行 + 每笔一行的 `x,y x,y`）。
/// </summary>
internal static class PdMetricsProbe
{
    private readonly record struct Pt(float X, float Y);

    /// <summary>一批投递的排程：末点下标 + 该批的时标锚点。</summary>
    private readonly record struct DeliverySchedule(List<int> BatchEnd, List<double> BatchAnchor);

    /// <summary>单帧的评测结果（误差 / 真值分档 / 角度 / 超前），由 <see cref="Accumulate"/> 并入累计量。</summary>
    private readonly record struct FrameScore(bool Supplied, double Step, double Err, int Bin,
                                              bool HasAngle, float Angle, bool HasOver, float Over);

    /// <summary>一个配置的累计量：误差、角度、超前、分档（跨帧共享，不用静态可变字段）。</summary>
    private sealed class FrameAcc
    {
        public double SumErr, SumErrAll, SumBase, SumBaseAll, SumAngle, SumOver;
        public long NErr, Samples, NUnavail;
        public readonly List<float> Angles = new();
        public readonly List<float> Overs = new();
        public readonly long[] BinSupply = new long[9];
        public readonly long[] BinFrames = new long[9];
        public readonly double[] BinAngSum = new double[9];
        public readonly List<float>[] BinAngles = CreateBinLists();

        private static List<float>[] CreateBinLists()
        {
            var bins = new List<float>[9];
            for (int b = 0; b < 9; b++) bins[b] = new List<float>();
            return bins;
        }
    }

    public static int Run(string[] args)
    {
        string corpus = null;
        float dt = 15f;                 // 采样间隔（ms）；SCUT 是 PDA 稀疏采样，先按 15ms 估
        int limit = int.MaxValue;
        bool sweep = false;
        float batchMs = 0f;             // >0 = 批量投递窗口 B（--deliver batch:B）；0 = clean
        float hz = 0f;                  // >0 = 按产品公式算地平线；0 = 老口径 HorizonMs = dt
        bool refNone = false, refOracle = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pdmetrics" && i + 1 < args.Length) corpus = args[i + 1];
            else if (args[i] == "--dt" && i + 1 < args.Length && float.TryParse(args[i + 1], out var d)) dt = d;
            else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var l)) limit = l;
            else if (args[i] == "--sweep") sweep = true;
            else if (args[i] == "--deliver" && i + 1 < args.Length)
            {
                // 2026-11-17：默认/非法值都落 clean（= 现行行为，行为不变）
                var v = args[i + 1];
                batchMs = v.StartsWith("batch:", StringComparison.OrdinalIgnoreCase)
                          && float.TryParse(v.AsSpan(6), out var b) && b > 0f ? b : 0f;
            }
            else if (args[i] == "--hz" && i + 1 < args.Length && float.TryParse(args[i + 1], out var h) && h > 0f) hz = h;
            else if (args[i] == "--ref" && i + 1 < args.Length)
            {
                if (string.Equals(args[i + 1], "none", StringComparison.OrdinalIgnoreCase)) refNone = true;
                else if (string.Equals(args[i + 1], "oracle", StringComparison.OrdinalIgnoreCase)) refOracle = true;
            }
        }

        if (string.IsNullOrWhiteSpace(corpus) || !File.Exists(corpus))
        {
            Console.WriteLine("  用法：--pdmetrics <语料.txt> [--dt 15] [--limit 20000] [--sweep]"
                               + " [--deliver clean|batch:16.6] [--hz 60] [--ref none|oracle]");
            return 2;
        }

        // 地平线：--hz 给了就套产品公式 clamp(0.6 × 帧周期ms, 4, 10)（60 → 10ms）；
        // 没给保持老口径 HorizonMs = dt（向后兼容，改动前后输出可对照）。
        double horizonMs = hz > 0f
            ? Math.Clamp(0.6 * (1000.0 / hz), 4.0, 10.0)
            : Math.Clamp((double)dt, 1.0, 200.0);

        Console.WriteLine($"=== 预测器离线评测（--pdmetrics）===");
        Console.WriteLine($"  语料：{corpus}");
        Console.WriteLine($"  采样间隔假设 dt={dt:F0}ms（不预测基线 = 当前点 → 下一点的距离）");
        Console.WriteLine(hz > 0f
            ? $"  地平线：产品公式 clamp(0.6×帧周期{1000f / hz:F1}ms, 4, 10) = {horizonMs:F1}ms"
            : $"  地平线：HorizonMs = dt = {horizonMs:F1}ms（老口径，向后兼容）");
        Console.WriteLine(batchMs > 0f
            ? $"  投递：batch:{batchMs:F1}ms（同窗点打成一批、批内同刻，时标 = 窗口末尾锚点；预测在每批喂完后）"
            : "  投递：clean（每点独立时标 t += dt）");
        if (refNone) Console.WriteLine("  参照 none：恒不预测（含门控吃到率应 = 0%）");
        if (refOracle) Console.WriteLine("  参照 oracle：真实未来点同样过本行弧长截断（上限自证：oracle ≥ current ≥ none）");

        var samples = LoadCorpus(corpus, limit);
        int pts = 0;
        long total = 0;
        foreach (var s in samples) { pts += s.Count; total++; }
        Console.WriteLine($"  样本 {total} 条、点 {pts} 个");
        Console.WriteLine();
        Console.WriteLine("  弧长cap   | 供给率  吃到率(仅供给帧)  含门控吃到率  角度均值 角度p95 | 超前均值 超前p95");
        Console.WriteLine("  ----------|-------------------------------------------------------------|----------------");

        foreach (var cap in (sweep ? new[] { 0.0, 3.0, 6.0, 9.0, 12.0 } : new[] { 6.0 }))
        {
            RunConfig(samples, dt, cap, batchMs, horizonMs, 0);
            // oracle 与本行同 cap 截断，逐行自证 oracle ≥ current（默认行 cap=6 = "6px 弧长截断"）
            if (refOracle) RunConfig(samples, dt, cap, batchMs, horizonMs, 2);
        }
        // none 与 cap 无关（恒不预测），只出一行
        if (refNone) RunConfig(samples, dt, 6.0, batchMs, horizonMs, 1);

        return 0;
    }

    // ---------------------------------------------------------------- 单配置评测

    /// <summary>
    /// 跑一个配置。refMode：0 = 真实预测器；1 = none（恒不预测）；2 = oracle
    /// （真实未来点过本行弧长截断）。
    /// 2026-11-17：误差样本 = **全部帧**（含门控/无速度，err = baseLen = 不预测），
    /// 供给样本 = n>0 的帧——两个分母分开，门控越多含门控吃到率越差，不再虚高。
    /// </summary>
    private static void RunConfig(List<List<Pt>> samples, float dt, double arcCap,
                                  float batchMs, double horizonMs, int refMode)
    {
        // 2026-10-15：评测对象 = 唯一预测器（线性，Chromium 同族；v6 已退役）。
        // sweep 扫**弧长截断**（ArcCap；0 = 纯 Chromium 行为、6 = 出厂默认）——
        // 它是这一版最关键的安全旋钮。
        var p = new InkPredictorLinear
        {
            HorizonMs = horizonMs,
            ArcCap = arcCap,
        };
        var predBuf = new PredictedPoint[16];
        var acc = new FrameAcc();

        foreach (var pts in samples)
        {
            if (pts.Count < 3) continue;
            p.Reset();

            var sched = BuildDeliverySchedule(pts.Count, dt, batchMs);
            int lo = 0;
            for (int k = 0; k < sched.BatchEnd.Count; k++)
            {
                int hi = sched.BatchEnd[k];
                for (int j = lo; j <= hi; j++) p.Add(pts[j].X, pts[j].Y, sched.BatchAnchor[k]);
                lo = hi + 1;
                if (k + 1 >= sched.BatchEnd.Count) break; // 最后一批之后没有未来点，不产生评测样本

                acc.Samples++;
                Accumulate(acc, ScoreFrame(p, predBuf, pts, hi, dt, arcCap, horizonMs,
                                           sched.BatchAnchor[k], refMode));
            }
        }

        Report(arcCap, refMode, acc);
    }

    /// <summary>
    /// 投递排程（2026-11-17）。
    /// clean：每点一批，锚点 = 自己的真相时刻 j·dt（= 现行 t += dt 行为）；
    /// batch:B：真相点仍按 dt 产生，但落在同一 B ms 窗口（floor(t/B)）的点打成一批，
    ///          锚点 = 窗口末尾 (w+1)·B——批内同刻点，正是真机输入栈的形态。
    /// </summary>
    private static DeliverySchedule BuildDeliverySchedule(int count, float dt, float batchMs)
    {
        var batchEnd = new List<int>();         // 每批末点下标
        var batchAnchor = new List<double>();   // 每批锚点时刻
        if (batchMs <= 0f)
        {
            for (int j = 0; j < count; j++)
            {
                batchEnd.Add(j);
                batchAnchor.Add(j * (double)dt);
            }
        }
        else
        {
            double curW = -1;
            for (int j = 0; j < count; j++)
            {
                double w = Math.Floor(j * (double)dt / batchMs);
                if (w != curW)
                {
                    if (curW >= 0)
                    {
                        batchEnd.Add(j - 1);
                        batchAnchor.Add((curW + 1) * batchMs);
                    }
                    curW = w;
                }
            }
            batchEnd.Add(count - 1);
            batchAnchor.Add((curW + 1) * batchMs);
        }
        return new DeliverySchedule(batchEnd, batchAnchor);
    }

    /// <summary>
    /// 单帧评测：真值分档 + 预测器（或 none / oracle）给出的点 → 误差、角度误差、超前量。
    /// 该帧的累计由 <see cref="Accumulate"/> 做，保证"先算后加"的顺序与原口径一致。
    /// </summary>
    private static FrameScore ScoreFrame(InkPredictorLinear p, PredictedPoint[] predBuf,
                                         List<Pt> pts, int hi, float dt, double arcCap,
                                         double horizonMs, double anchor, int refMode)
    {
        var prev = pts[hi];                 // 当前点（本批末点 = 笔尖）
        var act = pts[hi + 1];              // 真实未来点（距当前一个 dt）

        // 真值分档（只用真相轨迹，不依赖预测器输出）：
        // 速度 = 真实相邻点距 / dt；转角 = 相邻三点夹角（顶点 = 当前点）。
        double step = Math.Sqrt((act.X - prev.X) * (act.X - prev.X) + (act.Y - prev.Y) * (act.Y - prev.Y));
        int vBin = step / dt < 0.3 ? 0 : (step / dt <= 1.0 ? 1 : 2);
        int bin = -1;
        if (hi >= 1)                        // 缺三点的首个样本不入档（转角不可算）
        {
            var pre = pts[hi - 1];
            double ux = prev.X - pre.X, uy = prev.Y - pre.Y;
            double vx = act.X - prev.X, vy = act.Y - prev.Y;
            double ul = Math.Sqrt(ux * ux + uy * uy), vl = Math.Sqrt(vx * vx + vy * vy);
            if (ul > 1e-3 && vl > 1e-3)
            {
                double cosv = Math.Max(-1.0, Math.Min(1.0, (ux * vx + uy * vy) / (ul * vl)));
                double turn = Math.Acos(cosv) * 180.0 / Math.PI;
                int aBin = turn < 30 ? 0 : (turn <= 75 ? 1 : 2);
                bin = vBin * 3 + aBin;
            }
        }

        int n;
        if (refMode == 1) n = 0;            // none：恒不预测
        else if (refMode == 2)
        {
            // oracle（2026-11-17 自证上限）：理想预测 = 真实未来点本身，但它同样要过
            // 本行弧长截断（出厂 6px）——被 cap 限住的预测器逐样本最好也只能拿到
            // err = max(0, step − cap)。cap=0（不截断）时即纯理想点。
            double cut = (arcCap > 0.0 && step > arcCap && step > 1e-9) ? arcCap / step : 1.0;
            predBuf[0] = new PredictedPoint
            {
                X = (float)(prev.X + (act.X - prev.X) * cut),
                Y = (float)(prev.Y + (act.Y - prev.Y) * cut),
                TimeMs = anchor + horizonMs,
            };
            n = 1;
        }
        else n = p.Predict(predBuf);

        if (n <= 0) return new FrameScore(false, step, 0, bin, false, 0f, false, 0f);

        var tip = predBuf[n - 1];           // 最远那一点（= 地平线处）
        double err = Math.Sqrt((tip.X - act.X) * (tip.X - act.X) + (tip.Y - act.Y) * (tip.Y - act.Y));

        // 角度误差：预测方向 vs 真实方向
        bool hasAngle = false;
        float ang = 0f;
        double pvx = tip.X - prev.X, pvy = tip.Y - prev.Y;
        double avx = act.X - prev.X, avy = act.Y - prev.Y;
        double pl = Math.Sqrt(pvx * pvx + pvy * pvy), al = Math.Sqrt(avx * avx + avy * avy);
        if (pl > 1e-3 && al > 1e-3)
        {
            double cos = Math.Max(-1.0, Math.Min(1.0, (pvx * avx + pvy * avy) / (pl * al)));
            ang = (float)(Math.Acos(cos) * 180.0 / Math.PI);
            hasAngle = true;
        }
        // 超前：预测点越过真实点的像素（沿真实步方向投影）
        bool hasOver = false;
        float over = 0f;
        if (al > 1e-3)
        {
            double proj = (pvx * avx + pvy * avy) / al;
            if (proj > al)
            {
                over = (float)(proj - al);
                hasOver = true;
            }
        }
        return new FrameScore(true, step, err, bin, hasAngle, ang, hasOver, over);
    }

    /// <summary>把单帧结果并入累计量：误差/基线、角度、超前、分档计数。</summary>
    private static void Accumulate(FrameAcc acc, in FrameScore f)
    {
        if (f.Bin >= 0) acc.BinFrames[f.Bin]++;
        if (f.Supplied)
        {
            acc.SumErr += f.Err; acc.SumErrAll += f.Err;
            acc.SumBase += f.Step; acc.SumBaseAll += f.Step;
            if (f.Bin >= 0) acc.BinSupply[f.Bin]++;

            if (f.HasAngle)
            {
                acc.SumAngle += f.Angle; acc.Angles.Add(f.Angle);
                if (f.Bin >= 0) { acc.BinAngSum[f.Bin] += f.Angle; acc.BinAngles[f.Bin].Add(f.Angle); }
            }
            if (f.HasOver)
            {
                acc.SumOver += f.Over; acc.Overs.Add(f.Over);
            }
            acc.NErr++;
        }
        else
        {
            acc.NUnavail++;
            // 2026-11-17 分母修正：门控/无速度帧按"不预测"计入误差（err = baseLen），
            // 该帧吃到 = 0——门控越多吃到率越差，不再虚高（REVIEW-OPUS §3）。
            acc.SumErrAll += f.Step;
            acc.SumBaseAll += f.Step;
        }
    }

    // ---------------------------------------------------------------- 输出

    private static string P95(List<float> a)
    {
        if (a.Count == 0) return "  -";
        a.Sort();
        return $"{a[(int)Math.Min(a.Count - 1, a.Count * 0.95)]:F1}";
    }

    /// <summary>主表一行（供给率 / 两种吃到率 / 角度 / 超前）+ 分档表。</summary>
    private static void Report(double arcCap, int refMode, FrameAcc acc)
    {
        double eat = acc.SumBase > 0 ? (1.0 - acc.SumErr / acc.SumBase) * 100.0 : 0;
        double eatAll = acc.SumBaseAll > 0 ? (1.0 - acc.SumErrAll / acc.SumBaseAll) * 100.0 : 0;
        double supply = acc.Samples > 0 ? 100.0 * acc.NErr / acc.Samples : 0;
        double angMean = acc.Angles.Count > 0 ? acc.SumAngle / acc.Angles.Count : 0;
        string label = refMode == 1 ? "none" : refMode == 2 ? "oracle" : $"{arcCap:F0}px";
        Console.WriteLine($"  {label,8} | {supply,5:F1}%  {eat,8:F1}%  {eatAll,8:F1}%  {angMean,6:F1}°  {P95(acc.Angles),6}°"
                          + $" | {acc.SumOver / Math.Max(1, acc.NErr),7:F1}px {P95(acc.Overs),6}px");

        ReportBins(acc);
    }

    /// <summary>分档表（2026-11-17）：只输出样本数≥30 的格子（<30 不够统计意义）。</summary>
    private static void ReportBins(FrameAcc acc)
    {
        string[] vName = { "v<0.3", "0.3-1", "v>1  " };
        string[] aName = { "拐<30", "30-75", "拐>75" };
        bool any = false;
        for (int bin = 0; bin < 9; bin++)
        {
            if (acc.BinFrames[bin] < 30) continue;
            if (!any)
            {
                Console.WriteLine("      分档 | 样本  供给率   角度均值 角度p95   （速度×转角；<30 样本的格子不输出）");
                any = true;
            }
            double bs = acc.BinFrames[bin] > 0 ? 100.0 * acc.BinSupply[bin] / acc.BinFrames[bin] : 0;
            double ba = acc.BinAngles[bin].Count > 0 ? acc.BinAngSum[bin] / acc.BinAngles[bin].Count : 0;
            Console.WriteLine($"      {vName[bin / 3]}×{aName[bin % 3]} | {acc.BinFrames[bin],5}  {bs,5:F1}%  {ba,7:F1}°  {P95(acc.BinAngles[bin]),6}°");
        }
    }

    // ---------------------------------------------------------------- 语料读取

    /// <summary>
    /// 读 `Export-SCUT-onHCC.ps1` 的输出（`#SCUT-onHCC v1` 头；`#sample …` 起一条；
    /// 每笔一行 `x,y x,y …`；空行结束）。只取坐标，别的字段忽略。
    /// 2026-11-17：保持不变（T5 只修口径，不动语料读取）。
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
