using System.Numerics;

namespace InkEngine;

/// <summary>
/// **"墨迹到一条折线的最大距离"** —— 验收用的**一把独立尺子**。
///
/// 这个文件 2026-09-25 之前装的是一份 **Schneider 1990（Graphics Gems）逐段三次贝塞尔
/// 拟合**（`Fit` / `Flatten` 那一套，逐函数移植、出处与许可证写在当时的文件头），
/// 它是"**保形平滑**"那一步的算法（计划 §43 第一步）。
///
/// **2026-09-25 保形平滑被用户取消了** —— 原话："把'保形平滑'这个功能取消掉吧，
/// 因为它影响我画抛物线"。原因很实在：平滑**只在"认不出图形"时生效**，而画抛物线
/// （尤其只画半支的）经常正好落在这一档 → 老师画的那条曲线被悄悄换成另一条，看着像
/// "它自己变了形"。于是那份拟合器**连同它的自检（`--smoothtest`）一起删掉**了
/// （要翻旧账看 git 历史）。
///
/// 只留下这一条 <see cref="MaxDeviationToPolyline"/> —— 它跟拟合器无关，
/// 是当时**专门写来"独立验收"的尺子**（那条规矩："自己不能给自己打分"），
/// 现在被**二次函数（抛物线）的验收**用着（见 `ShapeRecognize.TryFitParabola`）。
/// </summary>
internal static class CurveFit
{
    /// <summary>
    /// **独立验收用**：原始每个采样点到"折线点串"的最近距离，取最大。
    ///
    /// ⚠ 它是**跟被测对象无关**的一条量法（识别器内部比的是"到参数曲线在参数 t 处的距离"，
    /// 这里是"到折线的欧氏距离"）—— 所以它能拿来验识别器，不是自己给自己打分。
    /// ⚠ 复杂度：两条点串都**沿曲线单调走**，所以"最近的段"的下标也单调 —— 用滑窗往后看
    /// （窗口宽度按两边的点数比算）。**不要写成两层全扫**：401 点那一笔全扫要 5 ms，
    /// 卡在"停顿那一刻"不合适（当年量过）。
    /// </summary>
    internal static float MaxDeviationToPolyline(IReadOnlyList<Vector2> pts, IReadOnlyList<Vector2> poly)
    {
        if (pts == null || poly == null || pts.Count == 0 || poly.Count == 0) return float.MaxValue;
        if (poly.Count == 1) return Vector2.Distance(pts[0], poly[0]);

        // 一次全扫也只在点数很少时发生（退化输入）；正常走下面的滑窗
        if (pts.Count * poly.Count <= 4096)
        {
            float w0 = 0f;
            for (int i = 0; i < pts.Count; i++)
                w0 = MathF.Max(w0, NearestToPolyline(pts[i], poly, 0, poly.Count - 2));
            return w0;
        }

        int win = 4 + (int)MathF.Ceiling(poly.Count / (float)pts.Count) * 3;
        int j0 = 0;
        float worst = 0f;
        for (int i = 0; i < pts.Count; i++)
        {
            int from = Math.Max(0, j0 - 2);
            int to = Math.Min(poly.Count - 2, j0 + win);
            float best = float.MaxValue;
            int bestJ = from;
            for (int j = from; j <= to; j++)
            {
                float d = DistanceToSegment(pts[i], poly[j], poly[j + 1]);
                if (d < best) { best = d; bestJ = j; }
            }
            // 只在"窗口右端就是最近"时前移（保证不会跳过更近的段）
            j0 = bestJ;
            if (best > worst) worst = best;
        }
        return worst;
    }

    private static float NearestToPolyline(Vector2 p, IReadOnlyList<Vector2> poly, int fromJ, int toJ)
    {
        float best = float.MaxValue;
        for (int j = fromJ; j <= toJ; j++)
        {
            float d = DistanceToSegment(p, poly[j], poly[j + 1]);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>点到线段的距离（投影夹在两端之间）。</summary>
    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq <= 1e-9f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
