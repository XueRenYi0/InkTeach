using System.Numerics;

namespace InkEngine;

/// <summary>
/// The "One Euro" filter - the standard low-latency smoother for pointer input.
///
/// A fixed-strength filter cannot win: smooth enough to kill hand tremor when
/// you are drawing slowly means visible lag when you flick across the screen.
/// This one adapts its cutoff to the measured speed, so it smooths hard while
/// nearly stationary and gets out of the way when you move fast.
/// </summary>
internal sealed class OneEuroFilter
{
    private readonly float _minCutoff;
    private readonly float _beta;
    private readonly float _dCutoff;

    private bool _primed;
    private float _xPrev;
    private float _dxPrev;

    // Tuned for pen feel: a slightly higher floor keeps slow strokes tracking
    // closely, and a healthy beta means fast strokes are barely filtered at all.
    public OneEuroFilter(float minCutoffHz = 1.5f, float beta = 0.035f, float dCutoffHz = 1.0f)
    {
        _minCutoff = minCutoffHz;
        _beta = beta;
        _dCutoff = dCutoffHz;
    }

    public void Reset() => _primed = false;

    public float Filter(float x, double dtSeconds)
    {
        if (!_primed)
        {
            _primed = true;
            _xPrev = x;
            _dxPrev = 0;
            return x;
        }

        float dt = (float)Math.Clamp(dtSeconds, 1.0 / 1000.0, 0.2);
        float dx = (x - _xPrev) / dt;

        float aD = Alpha(_dCutoff, dt);
        float dxHat = aD * dx + (1 - aD) * _dxPrev;

        float cutoff = _minCutoff + _beta * MathF.Abs(dxHat);
        float a = Alpha(cutoff, dt);

        float xHat = a * x + (1 - a) * _xPrev;
        _xPrev = xHat;
        _dxPrev = dxHat;
        return xHat;
    }

    private static float Alpha(float cutoffHz, float dt)
    {
        float tau = 1f / (2f * MathF.PI * cutoffHz);
        return 1f / (1f + tau / dt);
    }
}

/// <summary>
/// Uniform grid over the desktop. Erasing and marquee-selecting used to scan
/// every stroke on every pointer move; with a grid they only look at the cells
/// the query touches. Duplicate hits are suppressed with a per-stroke stamp, so
/// queries allocate nothing.
/// </summary>
internal sealed class SpatialGrid
{
    public const int CellSize = 256;

    private readonly Dictionary<long, List<Stroke>> _cells = new();
    private readonly List<Stroke> _scratch = new();
    private int _stamp;

    private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    public void Clear() => _cells.Clear();

    public void Insert(Stroke s)
    {
        if (s.Bounds.IsEmpty) return;
        int x0 = (int)MathF.Floor(s.Bounds.MinX / CellSize);
        int y0 = (int)MathF.Floor(s.Bounds.MinY / CellSize);
        int x1 = (int)MathF.Floor(s.Bounds.MaxX / CellSize);
        int y1 = (int)MathF.Floor(s.Bounds.MaxY / CellSize);
        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                long k = Key(cx, cy);
                if (!_cells.TryGetValue(k, out var list))
                {
                    list = new List<Stroke>();
                    _cells[k] = list;
                }
                list.Add(s);
            }
    }

    public void Remove(Stroke s)
    {
        if (s.Bounds.IsEmpty) return;
        int x0 = (int)MathF.Floor(s.Bounds.MinX / CellSize);
        int y0 = (int)MathF.Floor(s.Bounds.MinY / CellSize);
        int x1 = (int)MathF.Floor(s.Bounds.MaxX / CellSize);
        int y1 = (int)MathF.Floor(s.Bounds.MaxY / CellSize);
        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (_cells.TryGetValue(Key(cx, cy), out var list)) list.Remove(s);
            }
    }

    /// <summary>Appends candidate strokes whose bounds overlap the rectangle.</summary>
    public int Query(RectF r, List<Stroke> results)
    {
        results.Clear();
        if (r.IsEmpty) return 0;

        _stamp++;
        int x0 = (int)MathF.Floor(r.MinX / CellSize);
        int y0 = (int)MathF.Floor(r.MinY / CellSize);
        int x1 = (int)MathF.Floor(r.MaxX / CellSize);
        int y1 = (int)MathF.Floor(r.MaxY / CellSize);

        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out var list)) continue;
                foreach (var s in list)
                {
                    if (s.QueryStamp == _stamp) continue;
                    s.QueryStamp = _stamp;
                    if (s.Bounds.Intersects(r)) results.Add(s);
                }
            }
        return results.Count;
    }

    public int CellCount => _cells.Count;
}

/// <summary>
/// Ramer-Douglas-Peucker simplification. A stroke arrives as one sample per
/// pointer message, which is far more points than the shape needs; RDP drops
/// the ones that sit on a straight line and keeps the corners. Fewer points
/// means less memory, faster repaints, and (later) editable vertices a human
/// could actually grab.
/// </summary>
internal static class Simplify
{
    public static List<InkPoint> Rdp(List<InkPoint> pts, float epsilon)
    {
        int n = pts.Count;
        if (n < 3) return new List<InkPoint>(pts);

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        RdpRange(pts, 0, n - 1, epsilon, keep);

        var outl = new List<InkPoint>(n);
        for (int i = 0; i < n; i++)
            if (keep[i]) outl.Add(pts[i]);
        return outl;
    }

    private static void RdpRange(List<InkPoint> pts, int first, int last, float eps, bool[] keep)
    {
        if (last <= first + 1) return;

        float ax = pts[first].X, ay = pts[first].Y;
        float bx = pts[last].X, by = pts[last].Y;
        float vx = bx - ax, vy = by - ay;
        float len2 = vx * vx + vy * vy;

        float worst = -1f;
        int worstIndex = -1;
        for (int i = first + 1; i < last; i++)
        {
            float wx = pts[i].X - ax, wy = pts[i].Y - ay;
            float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
            float dx = wx - vx * t, dy = wy - vy * t;
            float d = dx * dx + dy * dy;
            if (d > worst) { worst = d; worstIndex = i; }
        }

        if (worst > eps * eps && worstIndex > 0)
        {
            keep[worstIndex] = true;
            RdpRange(pts, first, worstIndex, eps, keep);
            RdpRange(pts, worstIndex, last, eps, keep);
        }
    }
}

/// <summary>
/// Schneider's algorithm ("An Algorithm for Automatically Fitting Digitized
/// Curves", Graphics Gems) - fits a chain of cubic Beziers to a polyline within
/// a stated tolerance.
///
/// This is what turns handwriting from "a polygon that happens to curve" into
/// an actual curve. It matters twice over: the outline stops being faceted, and
/// a stroke collapses to a handful of control points, which is the granularity
/// a person can actually drag when editing vertices later.
/// </summary>
internal static class CurveFit
{
    private const int MaxIterations = 4;

    /// <summary>
    /// Returns one Vector2[4] (p0, c1, c2, p3) per fitted segment.
    /// tolerancePx is a distance in pixels; internally the comparisons use
    /// squared distances, which is what the original algorithm assumes.
    /// </summary>
    public static List<Vector2[]> FitCurve(IReadOnlyList<Vector2> pts, float tolerancePx)
    {
        var result = new List<Vector2[]>();
        if (pts.Count < 2) return result;
        tolerancePx = MathF.Max(0.05f, tolerancePx);
        if (pts.Count == 2)
        {
            result.Add(LineAsCubic(pts[0], pts[1]));
            return result;
        }

        Vector2 tHat1 = ComputeLeftTangent(pts, 0);
        Vector2 tHat2 = ComputeRightTangent(pts, pts.Count - 1);
        FitCubic(pts, 0, pts.Count - 1, tHat1, tHat2, tolerancePx, result);
        return result;
    }

    public static Vector2 Evaluate(Vector2[] bez, float t)
    {
        float u = 1f - t;
        float b0 = u * u * u;
        float b1 = 3f * t * u * u;
        float b2 = 3f * t * t * u;
        float b3 = t * t * t;
        return bez[0] * b0 + bez[1] * b1 + bez[2] * b2 + bez[3] * b3;
    }

    private static Vector2[] LineAsCubic(Vector2 a, Vector2 b)
    {
        Vector2 d = (b - a) / 3f;
        return new[] { a, a + d, b - d, b };
    }

    private static void FitCubic(IReadOnlyList<Vector2> d, int first, int last,
                                 Vector2 tHat1, Vector2 tHat2, float tolerancePx, List<Vector2[]> outSegments)
    {
        int nPts = last - first + 1;
        float tolSq = tolerancePx * tolerancePx;

        if (nPts == 2)
        {
            outSegments.Add(LineAsCubic(d[first], d[last]));
            return;
        }

        float[] u = ChordLengthParameterize(d, first, last);
        Vector2[] bez = GenerateBezier(d, first, last, u, tHat1, tHat2);
        (float maxError, int splitPoint) = ComputeMaxError(d, first, last, bez, u);

        if (maxError < tolSq)
        {
            outSegments.Add(bez);
            return;
        }

        // Close enough to converge: refine the parameterisation a few times
        // before resorting to splitting, which keeps the segment count down.
        if (maxError < tolSq * 16f)
        {
            for (int i = 0; i < MaxIterations; i++)
            {
                float[] uPrime = Reparameterize(d, first, last, u, bez);
                bez = GenerateBezier(d, first, last, uPrime, tHat1, tHat2);
                (maxError, splitPoint) = ComputeMaxError(d, first, last, bez, uPrime);
                if (maxError < tolSq)
                {
                    outSegments.Add(bez);
                    return;
                }
                u = uPrime;
            }
        }

        Vector2 tHatCenter = ComputeCenterTangent(d, splitPoint);
        FitCubic(d, first, splitPoint, tHat1, tHatCenter, tolerancePx, outSegments);
        FitCubic(d, splitPoint, last, -tHatCenter, tHat2, tolerancePx, outSegments);
    }

    private static Vector2[] GenerateBezier(IReadOnlyList<Vector2> d, int first, int last,
                                            float[] uPrime, Vector2 tHat1, Vector2 tHat2)
    {
        int nPts = last - first + 1;
        var a0 = new Vector2[nPts];
        var a1 = new Vector2[nPts];

        Vector2 p0 = d[first];
        Vector2 p3 = d[last];

        float c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;

        for (int i = 0; i < nPts; i++)
        {
            float u = uPrime[i];
            a0[i] = tHat1 * B1(u);
            a1[i] = tHat2 * B2(u);

            c00 += Vector2.Dot(a0[i], a0[i]);
            c01 += Vector2.Dot(a0[i], a1[i]);
            c11 += Vector2.Dot(a1[i], a1[i]);

            Vector2 tmp = d[first + i] - (p0 * (B0(u) + B1(u)) + p3 * (B2(u) + B3(u)));
            x0 += Vector2.Dot(a0[i], tmp);
            x1 += Vector2.Dot(a1[i], tmp);
        }

        float detC0C1 = c00 * c11 - c01 * c01;
        float detC0X = c00 * x1 - c01 * x0;
        float detXC1 = x0 * c11 - x1 * c01;

        float alphaL = MathF.Abs(detC0C1) < 1e-9f ? 0f : detXC1 / detC0C1;
        float alphaR = MathF.Abs(detC0C1) < 1e-9f ? 0f : detC0X / detC0C1;

        float segLength = Vector2.Distance(p0, p3);
        float epsilon = 1e-6f * segLength;

        if (alphaL < epsilon || alphaR < epsilon)
        {
            alphaL = alphaR = segLength / 3f;
        }

        // Guard against wild handles on nearly-degenerate input.
        float cap = segLength * 3f;
        alphaL = MathF.Min(alphaL, cap);
        alphaR = MathF.Min(alphaR, cap);

        return new[] { p0, p0 + tHat1 * alphaL, p3 + tHat2 * alphaR, p3 };
    }

    private static (float, int) ComputeMaxError(IReadOnlyList<Vector2> d, int first, int last,
                                                Vector2[] bez, float[] u)
    {
        float maxDist = 0;
        int splitPoint = (last - first + 1) / 2;

        for (int i = 1; i < last - first; i++)
        {
            Vector2 p = Evaluate(bez, u[i]);
            float dist = Vector2.DistanceSquared(p, d[first + i]);
            if (dist >= maxDist)
            {
                maxDist = dist;
                splitPoint = first + i;
            }
        }
        return (maxDist, splitPoint);
    }

    private static float[] Reparameterize(IReadOnlyList<Vector2> d, int first, int last,
                                          float[] u, Vector2[] bez)
    {
        // u is indexed locally (0 == first), unlike d.
        var uPrime = new float[u.Length];
        for (int i = 0; i < u.Length; i++)
            uPrime[i] = NewtonRaphson(bez, d[first + i], u[i]);
        return uPrime;
    }

    private static float NewtonRaphson(Vector2[] q, Vector2 p, float u)
    {
        float ux = 1f - u;
        var q1 = new Vector2[3];
        var q2 = new Vector2[2];

        q1[0] = (q[1] - q[0]) * 3f;
        q1[1] = (q[2] - q[1]) * 3f;
        q1[2] = (q[3] - q[2]) * 3f;

        q2[0] = (q1[1] - q1[0]) * 2f;
        q2[1] = (q1[2] - q1[1]) * 2f;

        Vector2 qU = q[0] * (ux * ux * ux) + q[1] * (3f * u * ux * ux)
                   + q[2] * (3f * u * u * ux) + q[3] * (u * u * u);
        Vector2 q1U = q1[0] * (ux * ux) + q1[1] * (2f * u * ux) + q1[2] * (u * u);
        Vector2 q2U = q2[0] * ux + q2[1] * u;

        Vector2 diff = qU - p;
        float numerator = Vector2.Dot(diff, q1U);
        float denominator = Vector2.Dot(q1U, q1U) + Vector2.Dot(diff, q2U);

        if (MathF.Abs(denominator) < 1e-12f) return u;
        return u - numerator / denominator;
    }

    private static float[] ChordLengthParameterize(IReadOnlyList<Vector2> d, int first, int last)
    {
        var u = new float[last - first + 1];
        u[0] = 0;
        for (int i = 1; i < u.Length; i++)
            u[i] = u[i - 1] + Vector2.Distance(d[first + i], d[first + i - 1]);

        float total = u[^1];
        if (total <= 1e-6f) total = 1f;
        for (int i = 1; i < u.Length; i++) u[i] /= total;
        return u;
    }

    private static Vector2 ComputeLeftTangent(IReadOnlyList<Vector2> d, int end)
        => Vector2.Normalize(d[end + 1] - d[end]);

    private static Vector2 ComputeRightTangent(IReadOnlyList<Vector2> d, int end)
        => Vector2.Normalize(d[end - 1] - d[end]);

    private static Vector2 ComputeCenterTangent(IReadOnlyList<Vector2> d, int center)
    {
        Vector2 v1 = d[center - 1] - d[center];
        Vector2 v2 = d[center] - d[center + 1];
        Vector2 t = (v1 + v2) * 0.5f;
        return t.LengthSquared() < 1e-12f ? Vector2.UnitX : Vector2.Normalize(t);
    }

    private static float B0(float u) { float v = 1f - u; return v * v * v; }
    private static float B1(float u) { float v = 1f - u; return 3f * u * v * v; }
    private static float B2(float u) { float v = 1f - u; return 3f * u * u * v; }
    private static float B3(float u) => u * u * u;
}
