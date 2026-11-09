namespace InkEngine;

/// <summary>一条采样完成的曲线：Series 只用于颜色/标签，Points 是数学坐标。</summary>
internal sealed class SampledCurve
{
    public PlotSeries Series;
    public List<(float X, float Y)> Points;
    /// <summary>分段端点的开/闭圆点；非分段函数为空。</summary>
    public List<(float X, float Y, bool Filled)> Breaks;
}

/// <summary>
/// 函数/圆锥曲线的统一采样：Skia 静态快照（PlotRenderer）和 D2D 交互图形
/// （Overlay.DrawFigure）共用同一份，避免"导出的图和屏幕上的图不一样"。
/// </summary>
internal static class PlotSampling
{
    public const int DefaultSamples = 1400;

    /// <summary>采样所有曲线并给出带留边的 y 范围；没有可画内容时返回空表。</summary>
    public static List<SampledCurve> Build(PlotSpec spec, int samples,
                                           out float yMin, out float yMax)
    {
        samples = Math.Clamp(samples, 200, 4000);
        var curves = new List<SampledCurve>();
        yMin = float.MaxValue;
        yMax = float.MinValue;

        if (spec.Kind == "conic" && spec.Conic != null)
        {
            var pts = ConicPoints(spec.Conic, spec.XMin, spec.XMax);
            var series = new PlotSeries
            {
                Precomputed = pts,
                Label = ConicLabel(spec.Conic),
                Expr = spec.Conic.Equation,
                Color = spec.Conic.Color,
            };
            var curve = new SampledCurve { Series = series, Points = pts };
            curves.Add(curve);
            AddRange(curve, ref yMin, ref yMax);
        }
        else
        {
            foreach (var s in spec.Series)
            {
                List<(float X, float Y)> pts;
                List<(float X, float Y, bool)> breaks = null;
                if (s.Precomputed is { Count: > 0 })
                {
                    pts = s.Precomputed;
                }
                else if (s.Pieces.Count > 0)
                {
                    pts = new List<(float, float)>(samples);
                    breaks = new List<(float, float, bool)>();
                    foreach (var piece in s.Pieces)
                    {
                        PlotExpression pe;
                        try { pe = PlotExpression.Parse(piece.Expr, spec.Params); }
                        catch { continue; }
                        if (pe == null) continue;
                        float a = piece.MinX ?? spec.XMin;
                        float b = piece.MaxX ?? spec.XMax;
                        a = MathF.Max(a, spec.XMin);
                        b = MathF.Min(b, spec.XMax);
                        if (b < a) continue;
                        // 分段端点：闭区间实心、开区间空心，跳变一眼能看出来。
                        if (piece.MinX.HasValue && piece.MinX.Value >= spec.XMin
                            && piece.MinX.Value <= spec.XMax)
                        {
                            double by = pe.Eval(piece.MinX.Value);
                            if (double.IsFinite(by))
                                breaks.Add((piece.MinX.Value, (float)by, piece.MinInclusive));
                        }
                        if (piece.MaxX.HasValue && piece.MaxX.Value >= spec.XMin
                            && piece.MaxX.Value <= spec.XMax)
                        {
                            double by = pe.Eval(piece.MaxX.Value);
                            if (double.IsFinite(by))
                                breaks.Add((piece.MaxX.Value, (float)by, piece.MaxInclusive));
                        }
                        if (b - a < 1e-6f)
                        {
                            float y0 = (float)pe.Eval(a);
                            if (double.IsFinite(y0)) pts.Add((a, y0));
                            pts.Add((float.NaN, float.NaN));
                            continue;
                        }
                        int n = Math.Clamp((int)((b - a) / MathF.Max(1e-6f,
                                            spec.XMax - spec.XMin) * samples), 40, samples);
                        bool gap = false;
                        for (int i = 0; i <= n; i++)
                        {
                            float x = a + (b - a) * i / n;
                            if (i == 0 && piece.MinX.HasValue && !piece.MinInclusive
                                && x <= piece.MinX.Value)
                                x = MathF.Min(b, piece.MinX.Value + (b - a) * 0.001f);
                            if (i == n && piece.MaxX.HasValue && !piece.MaxInclusive
                                && x >= piece.MaxX.Value)
                                x = MathF.Max(a, piece.MaxX.Value - (b - a) * 0.001f);
                            double y = pe.Eval(x);
                            if (!double.IsFinite(y) || Math.Abs(y) > 1e6)
                            {
                                if (!gap && pts.Count > 0) pts.Add((float.NaN, float.NaN));
                                gap = true;
                                continue;
                            }
                            gap = false;
                            pts.Add((x, (float)y));
                        }
                        pts.Add((float.NaN, float.NaN));   // 段与段之间断线
                    }
                    // 交给下面统一建 curve；先暂存到局部，后面补。
                }
                else
                {
                    PlotExpression expr;
                    try { expr = PlotExpression.Parse(s.Expr, spec.Params); }
                    catch { continue; }
                    if (expr == null) continue;
                    pts = new List<(float, float)>(samples);
                    bool gap = false;
                    for (int i = 0; i <= samples; i++)
                    {
                        float x = spec.XMin + (spec.XMax - spec.XMin) * i / samples;
                        double y = expr.Eval(x);
                        if (!double.IsFinite(y) || Math.Abs(y) > 1e6)
                        {
                            if (!gap && pts.Count > 0) pts.Add((float.NaN, float.NaN));
                            gap = true;
                            continue;
                        }
                        gap = false;
                        pts.Add((x, (float)y));
                    }
                }
                var curve = new SampledCurve { Series = s, Points = pts, Breaks = breaks };
                if (AddRange(curve, ref yMin, ref yMax) > 0) curves.Add(curve);
            }
        }

        if (curves.Count == 0)
        {
            yMin = -5f;
            yMax = 5f;
            return curves;
        }
        if (!float.IsFinite(yMin) || !float.IsFinite(yMax))
        { yMin = -5f; yMax = 5f; }
        if (yMax - yMin < 1e-4f) { yMin -= 1f; yMax += 1f; }
        float pad = (yMax - yMin) * 0.15f;
        yMin -= pad;
        yMax += pad;
        // 防止 exp 之类把图压扁：显示范围限制在 ±50，曲线出界就裁掉。
        yMin = MathF.Max(yMin, -50f);
        yMax = MathF.Min(yMax, 50f);
        if (yMax - yMin < 0.5f) { yMin -= 0.5f; yMax += 0.5f; }
        return curves;
    }

    /// <summary>圆锥曲线等比例：保证椭圆不变成圆、抛物线不被拉扁。</summary>
    public static void FitConicAspect(PlotSpec spec, ref float xMin, ref float xMax,
                                      ref float yMin, ref float yMax,
                                      float plotWidth, float plotHeight)
    {
        if (spec.Kind != "conic" || spec.Conic == null ||
            plotWidth <= 1f || plotHeight <= 1f) return;
        float xSpan = xMax - xMin;
        float ySpan = yMax - yMin;
        float yc = (yMin + yMax) * 0.5f;
        float xc = (xMin + xMax) * 0.5f;
        if (xSpan * plotHeight / plotWidth < ySpan)
        {
            xSpan = ySpan * plotWidth / plotHeight;
            xMin = xc - xSpan * 0.5f;
            xMax = xc + xSpan * 0.5f;
        }
        else
        {
            ySpan = xSpan * plotHeight / plotWidth;
            yMin = yc - ySpan * 0.5f;
            yMax = yc + ySpan * 0.5f;
        }
    }

    public static List<(float X, float Y)> ConicPoints(PlotConic c, float viewMin, float viewMax)
    {
        var pts = new List<(float, float)>();
        switch ((c.Type ?? "").ToLowerInvariant())
        {
            case "hyperbola":
            {
                float a = MathF.Max(0.05f, MathF.Abs(c.A));
                float b = MathF.Max(0.05f, MathF.Abs(c.B));
                bool vertical = (c.Axis ?? "x").ToLowerInvariant() is "y" or "vertical";
                float span = vertical
                    ? MathF.Max(MathF.Abs(viewMax - c.H), MathF.Abs(viewMin - c.H)) / b
                    : MathF.Max(MathF.Abs(viewMax - c.H), MathF.Abs(viewMin - c.H)) / a;
                float tMax = MathF.Min(3.6f, MathF.Max(0.8f, MathF.Asinh(span)));
                const int n = 220;
                for (int branch = -1; branch <= 1; branch += 2)
                {
                    for (int i = 0; i <= n; i++)
                    {
                        float t = -tMax + 2f * tMax * i / n;
                        if (vertical)
                            pts.Add((c.H + b * MathF.Sinh(t),
                                     c.K + branch * a * MathF.Cosh(t)));
                        else
                            pts.Add((c.H + branch * a * MathF.Cosh(t),
                                     c.K + b * MathF.Sinh(t)));
                    }
                    pts.Add((float.NaN, float.NaN));
                }
                break;
            }
            case "parabola":
            {
                float p = MathF.Max(0.05f, MathF.Abs(c.A));
                string axis = (c.Axis ?? "right").ToLowerInvariant();
                float tMax;
                if (axis is "up" or "down")
                    tMax = MathF.Min(18f, MathF.Sqrt(2f * p * 16f));
                else
                {
                    float dx = axis == "left"
                        ? MathF.Max(0.5f, c.H - viewMin)
                        : MathF.Max(0.5f, viewMax - c.H);
                    tMax = MathF.Min(18f, MathF.Sqrt(2f * p * dx));
                }
                const int n = 320;
                for (int i = 0; i <= n; i++)
                {
                    float t = -tMax + 2f * tMax * i / n;
                    switch (axis)
                    {
                        case "left":
                            pts.Add((c.H - t * t / (2f * p), c.K + t));
                            break;
                        case "up":
                            pts.Add((c.H + t, c.K + t * t / (2f * p)));
                            break;
                        case "down":
                            pts.Add((c.H + t, c.K - t * t / (2f * p)));
                            break;
                        default:
                            pts.Add((c.H + t * t / (2f * p), c.K + t));
                            break;
                    }
                }
                break;
            }
            default:
            {
                float a = MathF.Max(0.05f, MathF.Abs(c.A));
                float b = MathF.Max(0.05f, MathF.Abs(c.B));
                const int n = 720;
                for (int i = 0; i <= n; i++)
                {
                    float t = MathF.PI * 2f * i / n;
                    pts.Add((c.H + a * MathF.Cos(t), c.K + b * MathF.Sin(t)));
                }
                break;
            }
        }
        return pts;
    }

    private static int AddRange(SampledCurve curve, ref float yMin, ref float yMax)
    {
        int n = 0;
        foreach (var (x, y) in curve.Points)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
            if (y < yMin) yMin = y;
            if (y > yMax) yMax = y;
            n++;
        }
        return n;
    }

    private static string ConicLabel(PlotConic c)
    {
        string name = (c.Type ?? "").ToLowerInvariant() switch
        {
            "hyperbola" => "双曲线",
            "parabola" => "抛物线",
            _ => "椭圆",
        };
        string eq = ConicEquation(c);
        if (string.IsNullOrWhiteSpace(eq)) return name;
        return name + "  " + eq;
    }

    /// <summary>按当前 a/b/h/k 生成方程，参数拖动后图例不会停在旧方程上。</summary>
    private static string ConicEquation(PlotConic c)
    {
        float a = MathF.Max(0.0001f, MathF.Abs(c.A));
        float b = MathF.Max(0.0001f, MathF.Abs(c.B));
        string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string X = MathF.Abs(c.H) < 1e-4f ? "x^2" : $"(x{(c.H < 0 ? "+" : "-")}{F(MathF.Abs(c.H))})^2";
        string Y = MathF.Abs(c.K) < 1e-4f ? "y^2" : $"(y{(c.K < 0 ? "+" : "-")}{F(MathF.Abs(c.K))})^2";
        string Xl = MathF.Abs(c.H) < 1e-4f ? "x" : $"(x{(c.H < 0 ? "+" : "-")}{F(MathF.Abs(c.H))})";
        string Yl = MathF.Abs(c.K) < 1e-4f ? "y" : $"(y{(c.K < 0 ? "+" : "-")}{F(MathF.Abs(c.K))})";
        switch ((c.Type ?? "").ToLowerInvariant())
        {
            case "hyperbola":
                if ((c.Axis ?? "x").ToLowerInvariant() is "y" or "vertical")
                    return $"{Y}/{F(a * a)} - {X}/{F(b * b)} = 1";
                return $"{X}/{F(a * a)} - {Y}/{F(b * b)} = 1";
            case "parabola":
            {
                float q = 2f * a;
                string axis = (c.Axis ?? "right").ToLowerInvariant();
                return axis switch
                {
                    "left" => $"{Y} = -{F(q)}{Xl}",
                    "up" => $"{X} = {F(q)}{Yl}",
                    "down" => $"{X} = -{F(q)}{Yl}",
                    _ => $"{Y} = {F(q)}{Xl}",
                };
            }
            default:
                return $"{X}/{F(a * a)} + {Y}/{F(b * b)} = 1";
        }
    }
}
