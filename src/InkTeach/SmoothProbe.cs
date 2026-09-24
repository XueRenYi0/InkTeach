using System.Diagnostics;
using System.Numerics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// **保形平滑的自检**（`--smoothtest`）——纯算法，不需要窗口 / 笔 / 屏幕。
/// 规格与实测口径见 计划-图形工具.md §四十三。
///
/// 三条要量住的事（用户 2026-09-24 定的第一步："只做保形平滑"）：
///   ① **保形**：拟合后离原始笔迹的最大偏差 ≤ 容差。**独立量**——量的是
///      "原始点到展平折线的欧氏距离"（`CurveFit.MaxDeviationToPolyline`），
///      不是拟合器自己报的那个"到贝塞尔的参数化距离"（自己不能给自己打分）；
///   ② **真的变滑**：粗糙度（按弧长等距重采样后，相邻段方向的平均夹角）要显著下降；
///   ③ **不许爆**：点数不增加、耗时在预算内、退化输入不崩、**直角不能被磨圆**。
///
/// 语料 = 上课常用的那几种（抛物线 / 正弦一周期 / 余弦 / 圆弧 120° / 反比例一支 /
/// 直线 / 圆），每条都**叠上手抖**（低频漂移 ＋ 逐点高频噪声）——
/// 干净的语料测不出"平滑在干什么"，必须拿"歪歪扭扭的那种线"当输入。
/// 全部固定种子 → 逐点可复现（跑两次数字必须一样）。
/// </summary>
internal static class SmoothProbe
{
    private static int _pass, _fail;

    internal static int Run()
    {
        _pass = 0;
        _fail = 0;

        float tol = InkEngine.InkEngine.CurveFitMaxErrorLogical;
        float spacing = InkEngine.InkEngine.CurveFitSpacingLogical;
        Console.WriteLine("=== 保形平滑自检（纯算法）===");
        Console.WriteLine($"  容差 {tol:F1} 逻辑像素、展平间距 {spacing:F1} 逻辑像素"
                          + "（画布单位 = 逻辑像素，这一条不开窗口）");

        // ── A. 语料：曲线 × 手抖 —— 保形 + 变滑 ─────────────────────────────
        foreach (var (name, clean, seed) in Corpus())
        {
            var ink = Wobble(clean, seed, lowAmp: 5f, highAmp: 0.7f, cycles: 2.5f);
            var smooth = Smooth(ink, tol, spacing, out int segCount);

            float worst = CurveFit.MaxDeviationToPolyline(ink, smooth);
            float roughBefore = Roughness(ink, 400);
            float roughAfter = Roughness(smooth, 400);
            float ratio = roughAfter > 1e-6f ? roughBefore / roughAfter : float.PositiveInfinity;

            Console.WriteLine($"    {name,-12} {ink.Length,4} 点 → {smooth.Length,4} 点 / {segCount,3} 段；"
                              + $"最大偏差 {worst:F2}（≤ {tol + 1:F1}）；"
                              + $"粗糙度 {roughBefore:F2}° → {roughAfter:F2}°（降 {ratio:F1} 倍）");

            Check($"{name}：保形（偏差 ≤ 容差 + 1）", worst <= tol + 1f, $"{worst:F2}");
            Check($"{name}：真的变滑（粗糙度至少降 2 倍）", ratio >= 2f, $"{ratio:F1} 倍");
            Check($"{name}：点数不增加（留 5% 取整余量）",
                  smooth.Length <= ink.Length * 1.05f + 2f,
                  $"{ink.Length} → {smooth.Length}");
            Check($"{name}：结果里没有 NaN", AllFinite(smooth), "全部有限");
        }

        // ── B. 直角锯齿：**角不许被磨圆**（这是"保形"最苛刻的一种输入）──────
        {
            var zig = Zigzag();
            var smooth = Smooth(zig, tol, spacing, out _);
            float worst = CurveFit.MaxDeviationToPolyline(zig, smooth);
            float turnBefore = MaxTurn(zig, 400);
            float turnAfter = MaxTurn(smooth, 400);
            Console.WriteLine($"    直角锯齿     角上最大转角 {turnBefore:F0}° → {turnAfter:F0}°；最大偏差 {worst:F2}");
            Check("折线：保形（偏差 ≤ 容差 + 1）", worst <= tol + 1f, $"{worst:F2}");
            Check("折线：直角还在（转角保留 ≥ 60%）", turnAfter >= turnBefore * 0.6f,
                  $"{turnAfter:F0}° / {turnBefore:F0}°");
        }

        // ── C. 退化输入：不崩、不产垃圾 ─────────────────────────────────────
        {
            var two = new[] { new Vector2(0, 0), new Vector2(100, 0) };
            var same = new[] { new Vector2(5, 5), new Vector2(5, 5), new Vector2(5, 5) };
            var tiny = new[] { new Vector2(0, 0), new Vector2(0.2f, 0), new Vector2(0.4f, 0.1f) };
            var f2 = Smooth(two, tol, spacing, out _);
            var fs = Smooth(same, tol, spacing, out _);
            var ft = Smooth(tiny, tol, spacing, out _);
            Check("退化：两点 / 重合点 / 极短 —— 都不崩且结果有限",
                  AllFinite(f2) && AllFinite(fs) && AllFinite(ft),
                  $"两点 {f2.Length} 点、重合 {fs.Length} 点、极短 {ft.Length} 点");
            Check("退化：空输入返回空", CurveFit.Fit(Array.Empty<Vector2>(), tol).Count == 0, "0 段");
        }

        // ── D. 成本：这是"停顿那一刻"要做完的事，不能卡 ────────────────────
        {
            var ink = Wobble(Sample(t => new Vector2(150f + 700f * t,
                                   480f + MathF.Sin(MathF.PI * t * 2f) * 160f), 400), 7, 5f, 0.7f, 2.5f);
            // 预热一次（JIT），再量 20 次取平均
            Smooth(ink, tol, spacing, out _);
            var sw = Stopwatch.StartNew();
            const int reps = 20;
            float acc = 0f;
            for (int i = 0; i < reps; i++)
                acc += CurveFit.MaxDeviationToPolyline(ink, Smooth(ink, tol, spacing, out _));
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / reps;
            Console.WriteLine($"    成本：{ink.Length} 点一笔，拟合 + 展平 + 验收 {ms:F2} ms/次（累计偏差 {acc:F0}）");
            Check("成本：一笔 < 3 ms", ms < 3.0, $"{ms:F2} ms");
        }

        Console.WriteLine();
        Console.WriteLine($"  结果: {_pass} 项通过, {_fail} 项失败");
        return _fail == 0 ? 0 : 1;
    }

    // ---- 语料 ------------------------------------------------------------------

    /// <summary>走引擎里那一条路：拟合 → 展平（间距取"不比原迹更密"那条规则，和引擎同一个函数）。</summary>
    private static Vector2[] Smooth(Vector2[] ink, float tol, float floorSpacing, out int segCount)
    {
        var segs = CurveFit.Fit(ink, tol);
        segCount = segs.Count;
        float total = ShapeRecognize.PathLength(ink);
        return CurveFit.Flatten(segs, CurveFit.FlattenSpacing(total, ink.Length, floorSpacing, 1f));
    }

    /// <summary>上课常用的那几种曲线（干净版；抖动在 <see cref="Wobble"/> 里叠）。</summary>
    private static (string Name, Vector2[] Clean, int Seed)[] Corpus() => new[]
    {
        ("直线",     Sample(t => new Vector2(200f + 620f * t, 200f + 260f * t), 180), 11),
        ("抛物线",   Sample(t => new Vector2(200f + 620f * t, 260f + 260f * (t - 0.5f) * (t - 0.5f) * 4f), 200), 12),
        ("正弦",     Sample(t => new Vector2(200f + 620f * t, 300f - 150f * MathF.Sin(t * MathF.PI * 2f)), 200), 13),
        ("余弦",     Sample(t => new Vector2(200f + 620f * t, 300f - 150f * MathF.Cos(t * MathF.PI * 2f)), 200), 14),
        ("圆弧120°", Sample(t => { float a = (-60f + 120f * t) * MathF.PI / 180f;
                                   return new Vector2(900f + 260f * MathF.Cos(a), 700f + 260f * MathF.Sin(a)); }, 160), 15),
        ("反比例一支", Sample(t => { float x = 300f + 560f * t;
                                    return new Vector2(x, 180f + 42000f / x); }, 200), 16),
        ("圆（闭合）", Sample(t => { float a = t * MathF.PI * 2f * 0.96f;
                                    return new Vector2(600f + 220f * MathF.Cos(a), 1400f + 220f * MathF.Sin(a)); }, 220), 17),
    };

    /// <summary>把参数曲线采成一条点串（`--dwelltest` 也拿它造语料）。</summary>
    internal static Vector2[] Sample(Func<float, Vector2> f, int n)
    {
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++) pts[i] = f(i / (float)n);
        return pts;
    }

    /// <summary>
    /// 叠上"手抖"：**低频漂移**（沿法向，2~3 个波、5 像素量级——这就是"画歪了"的主体）
    /// ＋ **逐点高频噪声**（0.7 像素，数字化抖动）。固定种子。
    /// `--dwelltest` 也拿它造"歪曲线"语料（同一个实现，不写两份）。
    /// </summary>
    internal static Vector2[] Wobble(Vector2[] pts, int seed, float lowAmp, float highAmp, float cycles)
    {
        var rnd = new Random(seed);
        var outp = new Vector2[pts.Length];
        float phase = (float)(rnd.NextDouble() * Math.PI * 2);
        for (int i = 0; i < pts.Length; i++)
        {
            float t = i / (float)(pts.Length - 1);
            var prev = pts[Math.Max(i - 1, 0)];
            var next = pts[Math.Min(i + 1, pts.Length - 1)];
            var dir = next - prev;
            float len = dir.Length();
            var nrm = len <= 1e-6f ? new Vector2(0f, 1f) : new Vector2(-dir.Y, dir.X) / len;
            float low = MathF.Sin(phase + t * MathF.PI * 2f * cycles) * lowAmp;
            float high = (float)(rnd.NextDouble() * 2 - 1) * highAmp;
            outp[i] = pts[i] + nrm * (low + high);
        }
        return outp;
    }

    /// <summary>两个直角的锯齿（"角不许被磨圆"用的语料）。</summary>
    private static Vector2[] Zigzag()
    {
        var v = new[]
        {
            new Vector2(200f, 400f), new Vector2(500f, 400f),
            new Vector2(500f, 700f), new Vector2(800f, 700f),
        };
        var pts = new List<Vector2>();
        for (int i = 0; i < v.Length - 1; i++)
        {
            int n = 60;
            for (int k = 0; k <= n; k++)
            {
                if (i > 0 && k == 0) continue;
                pts.Add(Vector2.Lerp(v[i], v[i + 1], k / (float)n));
            }
        }
        return pts.ToArray();
    }

    // ---- 独立测量 ---------------------------------------------------------------

    /// <summary>
    /// 按**弧长等距**重采样成 n 个点。
    /// 为什么必须重采样再比：两次采样的点距不一样时，"相邻方向的夹角"没有可比性
    /// （点越密、单步转角越小）——不重采样就会得出"原迹更平滑"这种假结论。
    /// </summary>
    private static Vector2[] Resample(Vector2[] p, int n)
    {
        var outp = new Vector2[n + 1];
        outp[0] = p[0];
        int j = 0;
        float acc = 0f, total = 0f;
        for (int i = 1; i < p.Length; i++) total += Vector2.Distance(p[i], p[i - 1]);
        float step = total / n;
        float want = step;
        for (int i = 1; i < p.Length && j < n; i++)
        {
            float seg = Vector2.Distance(p[i], p[i - 1]);
            while (acc + seg >= want && j < n)
            {
                float t = seg <= 1e-6f ? 0f : (want - acc) / seg;
                outp[++j] = Vector2.Lerp(p[i - 1], p[i], t);
                want += step;
            }
            acc += seg;
        }
        for (int k = j + 1; k <= n; k++) outp[k] = p[^1];
        return outp;
    }

    /// <summary>粗糙度 = 等距重采样后，相邻两段方向夹角的**平均值**（度）。</summary>
    private static float Roughness(Vector2[] p, int n)
    {
        if (p.Length < 3) return 0f;
        var r = Resample(p, n);
        float sum = 0f;
        for (int i = 2; i < r.Length; i++)
        {
            var d1 = r[i - 1] - r[i - 2];
            var d2 = r[i] - r[i - 1];
            if (d1.LengthSquared() < 1e-9f || d2.LengthSquared() < 1e-9f) continue;
            sum += MathF.Abs(MathF.Atan2(d1.X * d2.Y - d1.Y * d2.X, Vector2.Dot(d1, d2))) * 180f / MathF.PI;
        }
        return sum / (r.Length - 2);
    }

    /// <summary>最大单步转角（度）——"角还在不在"就看它。</summary>
    private static float MaxTurn(Vector2[] p, int n)
    {
        if (p.Length < 3) return 0f;
        var r = Resample(p, n);
        float worst = 0f;
        for (int i = 2; i < r.Length; i++)
        {
            var d1 = r[i - 1] - r[i - 2];
            var d2 = r[i] - r[i - 1];
            if (d1.LengthSquared() < 1e-9f || d2.LengthSquared() < 1e-9f) continue;
            float a = MathF.Abs(MathF.Atan2(d1.X * d2.Y - d1.Y * d2.X, Vector2.Dot(d1, d2))) * 180f / MathF.PI;
            if (a > worst) worst = a;
        }
        return worst;
    }

    private static bool AllFinite(Vector2[] p)
    {
        foreach (var q in p)
            if (float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsInfinity(q.X) || float.IsInfinity(q.Y))
                return false;
        return true;
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  通过  {what}（{detail}）"); }
        else { _fail++; Console.WriteLine($"  失败  {what}（{detail}）"); }
    }
}
