using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>一条函数曲线。第一版只用一条，后面多条直接加 list + 颜色。</summary>
internal sealed class PlotSeries
{
    public string Expr = "";
    public Color4 Color = new(0.12f, 0.14f, 0.18f, 1f);
    public string Label = "";
    /// <summary>非空时直接用预采样点（圆锥曲线/参数曲线），不走 PlotExpression。</summary>
    public List<(float X, float Y)> Precomputed;
}

internal sealed class PlotConic
{
    /// <summary>ellipse / hyperbola / parabola</summary>
    public string Type = "ellipse";
    public float H, K;       // 中心/顶点
    public float A = 3f, B = 2f;
    /// <summary>抛物线开口方向：right / left / up / down。</summary>
    public string Axis = "right";
    public string Equation = "";
    public Color4 Color = new(0.12f, 0.14f, 0.18f, 1f);
    public string Label = "";
}

/// <summary>AI 作图请求：函数 / 圆锥曲线 / 平面几何 / 立体几何。</summary>
internal sealed class PlotSpec
{
    public string Kind = "functions"; // functions / conic / plane / solid
    public string Title = "";
    public float XMin = -5f, XMax = 5f;
    public readonly List<PlotSeries> Series = new();
    public readonly Dictionary<string, double> Params = new();
    public PlotConic Conic;
    public FigureSpec Figure;
}

/// <summary>
/// 函数图象离屏渲染（Skia，仅用于 AI 作图的**静态快照**）。
///
/// 为什么先用 Skia：坐标轴/刻度/文字/曲线一次性画成图片，插入现有 Image 对象，
/// 立刻能选中/移动/擦除/存档；不进入主画布的实时图形路径。
/// 后续可复制为 D2D FigureScene 渲染器，Skia 仍只负责静态快照/公式。
/// </summary>
internal static class PlotRenderer
{
    // 字体只加载一次：首次 PlotRenderer.Render 里创建 typeface 会吃掉 ~10ms。
    private static readonly SKTypeface Face =
        SKTypeface.FromFamilyName("Microsoft YaHei UI") ?? SKTypeface.Default;

    public static ImageData Render(PlotSpec spec, int width = 1100, int height = 760)
    {
        if (spec == null) return null;
        if (spec.Kind is "plane" or "solid")
            return FigureRenderer.RenderCard(spec.Figure, spec.Title, width, height);

        if (spec.Kind == "conic" && spec.Conic != null && spec.Series.Count == 0)
            BuildConicSeries(spec);
        if (spec.Series.Count == 0) return null;

        width = Math.Clamp(width, 400, 2000);
        height = Math.Clamp(height, 300, 1400);

        // 先采样，决定 y 范围。
        const int samples = 1400;
        var curves = new List<(PlotSeries S, List<(float X, float Y)> Pts)>();
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var s in spec.Series)
        {
            List<(float, float)> pts;
            if (s.Precomputed is { Count: > 0 })
            {
                pts = s.Precomputed;
            }
            else
            {
                PlotExpression expr;
                try { expr = PlotExpression.Parse(s.Expr, spec.Params); }
                catch { continue; }
                if (expr == null) continue;
                pts = new List<(float, float)>(samples);
                for (int i = 0; i <= samples; i++)
                {
                    float x = spec.XMin + (spec.XMax - spec.XMin) * i / samples;
                    double y = expr.Eval(x);
                    if (!double.IsFinite(y)) continue;
                    if (Math.Abs(y) > 1e6) continue;
                    pts.Add((x, (float)y));
                }
            }
            int valid = 0;
            foreach (var (x, y) in pts)
            {
                if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
                valid++;
            }
            if (valid > 0) curves.Add((s, pts));
        }
        if (curves.Count == 0) return null;

        if (!float.IsFinite(yMin) || !float.IsFinite(yMax))
        { yMin = -5; yMax = 5; }
        if (yMax - yMin < 1e-4f) { yMin -= 1; yMax += 1; }
        float pad = (yMax - yMin) * 0.15f;
        yMin -= pad; yMax += pad;
        // 防止 exp 之类把图压扁：显示范围限制在 ±50，曲线出界就裁掉。
        yMin = MathF.Max(yMin, -50f);
        yMax = MathF.Min(yMax, 50f);
        if (yMax - yMin < 0.5f) { yMin -= 0.5f; yMax += 0.5f; }

        // 含参函数在标题下再留一行显示参数默认值。
        float top = spec.Params.Count > 0 ? 94f : 64f;
        float left = 92f, right = 44f, bottom = 74f;
        float pw = width - left - right;
        float ph = height - top - bottom;
        if (pw < 50 || ph < 50) return null;

        // 圆锥曲线必须等比例：否则椭圆会被画成圆、抛物线会变形。
        // 同时保证采样出来的曲线全部落在视窗内，不够就扩大 x/y 范围。
        if (spec.Kind == "conic" && spec.Conic != null)
        {
            float xSpan = spec.XMax - spec.XMin;
            float ySpan = yMax - yMin;
            float yc = (yMin + yMax) * 0.5f;
            float xc = (spec.XMin + spec.XMax) * 0.5f;
            if (xSpan * ph / pw < ySpan)
            {
                xSpan = ySpan * pw / ph;
                spec.XMin = xc - xSpan * 0.5f;
                spec.XMax = xc + xSpan * 0.5f;
            }
            else
            {
                ySpan = xSpan * ph / pw;
                yMin = yc - ySpan * 0.5f;
                yMax = yc + ySpan * 0.5f;
            }
        }

        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888,
                                               SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            float radius = 26f;
            using (var bg = new SKPaint { Color = SKColors.White, IsAntialias = true })
            {
                canvas.DrawRoundRect(new SKRect(0, 0, width, height), radius, radius, bg);
            }
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(new SKRect(1, 1, width - 1, height - 1),
                                                 radius, radius), SKClipOperation.Intersect, true);

            float X(float x) => left + (x - spec.XMin) / (spec.XMax - spec.XMin) * pw;
            float Y(float y) => top + (yMax - y) / (yMax - yMin) * ph;

            // 网格/刻度
            float xStep = NiceStep(spec.XMax - spec.XMin, 8);
            float yStep = NiceStep(yMax - yMin, 7);
            using var grid = new SKPaint
            {
                Color = new SKColor(232, 235, 240), IsAntialias = false,
                Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
            };
            using var axis = new SKPaint
            {
                Color = new SKColor(70, 74, 82), IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 2f,
            };
            using var tick = new SKPaint
            {
                Color = new SKColor(90, 94, 102), IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f,
            };
            using var label = new SKPaint
            {
                Color = new SKColor(82, 86, 94), IsAntialias = true,
                TextSize = 20f, Typeface = Face,
            };

            for (float x = MathF.Ceiling(spec.XMin / xStep) * xStep;
                 x <= spec.XMax + 1e-4f; x += xStep)
            {
                float sx = X(x);
                canvas.DrawLine(sx, top, sx, top + ph, grid);
            }
            for (float y = MathF.Ceiling(yMin / yStep) * yStep;
                 y <= yMax + 1e-4f; y += yStep)
            {
                float sy = Y(y);
                canvas.DrawLine(left, sy, left + pw, sy, grid);
            }

            float axisY = Y(0f);
            float axisX = X(0f);
            bool hasXAxis = axisY >= top && axisY <= top + ph;
            bool hasYAxis = axisX >= left && axisX <= left + pw;
            if (!hasXAxis) axisY = top + ph;
            if (!hasYAxis) axisX = left;

            canvas.DrawLine(left, axisY, left + pw, axisY, axis);
            canvas.DrawLine(axisX, top + ph, axisX, top, axis);
            // 箭头
            canvas.DrawLine(left + pw, axisY, left + pw - 12, axisY - 6, axis);
            canvas.DrawLine(left + pw, axisY, left + pw - 12, axisY + 6, axis);
            canvas.DrawLine(axisX, top, axisX - 6, top + 12, axis);
            canvas.DrawLine(axisX, top, axisX + 6, top + 12, axis);

            // 刻度数字（避开 0 和轴外）
            for (float x = MathF.Ceiling(spec.XMin / xStep) * xStep;
                 x <= spec.XMax + 1e-4f; x += xStep)
            {
                if (MathF.Abs(x) < xStep * 0.01f) continue;
                float sx = X(x);
                canvas.DrawLine(sx, axisY - 4, sx, axisY + 4, tick);
                string t = Fmt(x);
                float tw = label.MeasureText(t);
                canvas.DrawText(t, sx - tw * 0.5f, axisY + 25, label);
            }
            for (float y = MathF.Ceiling(yMin / yStep) * yStep;
                 y <= yMax + 1e-4f; y += yStep)
            {
                if (MathF.Abs(y) < yStep * 0.01f) continue;
                float sy = Y(y);
                canvas.DrawLine(axisX - 4, sy, axisX + 4, sy, tick);
                string t = Fmt(y);
                float tw = label.MeasureText(t);
                canvas.DrawText(t, axisX - tw - 10, sy + 7, label);
            }
            canvas.DrawText("O", axisX - 18, axisY + 24, label);
            canvas.DrawText("x", left + pw - 20, axisY - 14, label);
            canvas.DrawText("y", axisX + 12, top + 14, label);

            // 曲线（出界裁剪；非连续点断段）
            canvas.Save();
            canvas.ClipRect(new SKRect(left, top, left + pw, top + ph));
            foreach (var (s, pts) in curves)
            {
                using var pen = new SKPaint
                {
                    Color = ToSK(s.Color),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3.2f,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                };
                using var path = new SKPath();
                bool started = false;
                float prevY = 0f;
                foreach (var (x, y) in pts)
                {
                    if (!float.IsFinite(x) || !float.IsFinite(y))
                    {
                        started = false;
                        continue;
                    }
                    float sx = X(x), sy = Y(y);
                    bool jump = started && MathF.Abs(sy - prevY) > ph * 1.4f;
                    if (!started || jump) { path.MoveTo(sx, sy); started = true; }
                    else path.LineTo(sx, sy);
                    prevY = sy;
                }
                canvas.DrawPath(path, pen);
            }

            if (spec.Kind == "conic" && spec.Conic != null)
                DrawConicFeatures(canvas, spec.Conic, X, Y, left, top, pw, ph,
                                  spec.XMin, spec.XMax);
            canvas.Restore();

            // 多条曲线：右上角图例（色线 + 解析式）
            bool wantLegend = curves.Count > 1
                              || (spec.Kind == "conic" && curves.Count > 0);
            if (wantLegend)
            {
                using var legendText = new SKPaint
                {
                    Color = new SKColor(58, 62, 70), IsAntialias = true,
                    TextSize = 17f, Typeface = Face,
                };
                using var legendBg = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, 235), IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                };
                using var legendBorder = new SKPaint
                {
                    Color = new SKColor(220, 224, 230), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
                };
                float maxText = 0f;
                foreach (var (s, _) in curves)
                    maxText = MathF.Max(maxText, legendText.MeasureText(
                        string.IsNullOrWhiteSpace(s.Label) ? "y=" + s.Expr : s.Label));
                float lw = maxText + 56f;
                float lh = curves.Count * 26f + 16f;
                float lx = left + pw - lw - 10f;
                float ly = top + 12f;
                var lr = new SKRect(lx, ly, lx + lw, ly + lh);
                canvas.DrawRoundRect(lr, 8f, 8f, legendBg);
                canvas.DrawRoundRect(lr, 8f, 8f, legendBorder);
                for (int i = 0; i < curves.Count; i++)
                {
                    var (s, _) = curves[i];
                    float yy = ly + 8f + 13f + i * 26f;
                    using var swatch = new SKPaint
                    {
                        Color = ToSK(s.Color),
                        IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = 3.2f, StrokeCap = SKStrokeCap.Round,
                    };
                    canvas.DrawLine(lx + 10f, yy, lx + 38f, yy, swatch);
                    string text = string.IsNullOrWhiteSpace(s.Label) ? "y=" + s.Expr : s.Label;
                    canvas.DrawText(text, lx + 46f, yy + 6f, legendText);
                }
            }

            // 标题 / 解析式
            if (!string.IsNullOrWhiteSpace(spec.Title))
            {
                using var titlePaint = new SKPaint
                {
                    Color = new SKColor(34, 38, 46), IsAntialias = true,
                    TextSize = 26f, Typeface = Face,
                };
                canvas.DrawText(spec.Title, left, 38, titlePaint);
            }

            // 含参函数：标题下显示参数默认值（v1 静态；拖动在 v2 FigureScene）。
            if (spec.Params.Count > 0)
            {
                var keys = new List<string>(spec.Params.Keys);
                keys.Sort(StringComparer.Ordinal);
                var parts = new List<string>(keys.Count);
                foreach (var k in keys) parts.Add(k + " = " + Fmt((float)spec.Params[k]));
                using var paramPaint = new SKPaint
                {
                    Color = new SKColor(92, 96, 104), IsAntialias = true,
                    TextSize = 18f, Typeface = Face,
                };
                canvas.DrawText("参数：" + string.Join("，", parts), left, 64, paramPaint);
            }
            canvas.Restore();
        }

        try
        {
            int rowBytes = bmp.RowBytes;
            var pixels = bmp.GetPixels();
            var bgra = new byte[(long)width * height * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(pixels, y * rowBytes), bgra, y * width * 4, width * 4);
            }
            return ImageData.AdoptPremultiplied(width, height, bgra);
        }
        finally { bmp.Dispose(); }
    }

    // ---- 圆锥曲线 ----------------------------------------------------------

    private static void BuildConicSeries(PlotSpec spec)
    {
        var c = spec.Conic;
        var pts = new List<(float, float)>();
        switch ((c.Type ?? "").ToLowerInvariant())
        {
            case "hyperbola":
            {
                float a = MathF.Max(0.05f, MathF.Abs(c.A));
                float b = MathF.Max(0.05f, MathF.Abs(c.B));
                float dx = MathF.Max(MathF.Abs(spec.XMax - c.H), MathF.Abs(spec.XMin - c.H));
                float tMax = MathF.Min(3.6f, MathF.Max(0.8f, MathF.Asinh(dx / a)));
                const int n = 220;
                for (int branch = -1; branch <= 1; branch += 2)
                {
                    for (int i = 0; i <= n; i++)
                    {
                        float t = -tMax + 2f * tMax * i / n;
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
                        ? MathF.Max(0.5f, c.H - spec.XMin)
                        : MathF.Max(0.5f, spec.XMax - c.H);
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
            default: // ellipse
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

        string label = c.Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            string name = (c.Type ?? "").ToLowerInvariant() switch
            {
                "hyperbola" => "双曲线",
                "parabola" => "抛物线",
                _ => "椭圆",
            };
            label = string.IsNullOrWhiteSpace(c.Equation) ? name : name + "  " + c.Equation;
        }
        spec.Series.Add(new PlotSeries
        {
            Precomputed = pts,
            Label = label,
            Expr = c.Equation,
            Color = c.Color,
        });
    }

    private static void DrawConicFeatures(SKCanvas canvas, PlotConic c,
                                          Func<float, float> X, Func<float, float> Y,
                                          float left, float top, float pw, float ph,
                                          float xMin, float xMax)
    {
        float a = MathF.Max(0.0001f, MathF.Abs(c.A));
        float b = MathF.Max(0.0001f, MathF.Abs(c.B));
        string type = (c.Type ?? "").ToLowerInvariant();
        using var dashFx = SKPathEffect.CreateDash(new[] { 7f, 6f }, 0f);
        using var dash = new SKPaint
        {
            Color = new SKColor(122, 128, 140, 210), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f,
            PathEffect = dashFx,
        };
        using var dot = new SKPaint
        {
            Color = new SKColor(196, 76, 60), IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        using var tiny = new SKPaint
        {
            Color = new SKColor(170, 68, 54), IsAntialias = true,
            TextSize = 15f, Typeface = Face,
        };

        void Vertical(float x, string text)
        {
            float sx = X(x);
            if (sx < left - 4 || sx > left + pw + 4) return;
            canvas.DrawLine(sx, top, sx, top + ph, dash);
            canvas.DrawText(text, sx + 4, top + ph - 8, tiny);
        }
        void Horizontal(float y, string text)
        {
            float sy = Y(y);
            if (sy < top - 4 || sy > top + ph + 4) return;
            canvas.DrawLine(left, sy, left + pw, sy, dash);
            canvas.DrawText(text, left + 6, sy - 5, tiny);
        }
        void Focus(float x, float y, string text)
        {
            float sx = X(x), sy = Y(y);
            if (sx < left - 8 || sx > left + pw + 8 || sy < top - 8 || sy > top + ph + 8)
                return;
            canvas.DrawCircle(sx, sy, 5f, dot);
            canvas.DrawText(text, sx + 7, sy - 7, tiny);
        }

        if (type == "ellipse")
        {
            float cc = MathF.Sqrt(MathF.Max(0f, a * a - b * b));
            if (cc > 0.001f)
            {
                Focus(c.H - cc, c.K, "F1");
                Focus(c.H + cc, c.K, "F2");
                Vertical(c.H - a * a / cc, "准线");
                Vertical(c.H + a * a / cc, "准线");
            }
        }
        else if (type == "hyperbola")
        {
            float cc = MathF.Sqrt(a * a + b * b);
            Focus(c.H - cc, c.K, "F1");
            Focus(c.H + cc, c.K, "F2");
            Vertical(c.H - a * a / cc, "准线");
            Vertical(c.H + a * a / cc, "准线");
            // 渐近线 y - k = ±(b/a)(x - h)
            float slope = b / a;
            foreach (float sgn in new[] { -1f, 1f })
            {
                float y1 = c.K + sgn * slope * (xMin - c.H);
                float y2 = c.K + sgn * slope * (xMax - c.H);
                canvas.DrawLine(X(xMin), Y(y1), X(xMax), Y(y2), dash);
            }
        }
        else // parabola
        {
            float p = a;
            float half = p * 0.5f;
            switch ((c.Axis ?? "right").ToLowerInvariant())
            {
                case "left":
                    Focus(c.H - half, c.K, "F");
                    Vertical(c.H + half, "准线");
                    break;
                case "up":
                    Focus(c.H, c.K + half, "F");
                    Horizontal(c.K - half, "准线");
                    break;
                case "down":
                    Focus(c.H, c.K - half, "F");
                    Horizontal(c.K + half, "准线");
                    break;
                default:
                    Focus(c.H + half, c.K, "F");
                    Vertical(c.H - half, "准线");
                    break;
            }
        }
    }

    private static SKColor ToSK(Color4 c) => new(
        (byte)Math.Clamp((int)(c.R * 255f), 0, 255),
        (byte)Math.Clamp((int)(c.G * 255f), 0, 255),
        (byte)Math.Clamp((int)(c.B * 255f), 0, 255),
        (byte)Math.Clamp((int)(c.A * 255f), 0, 255));

    private static float NiceStep(float range, int target)
    {
        if (range <= 0 || !float.IsFinite(range)) return 1f;
        float raw = range / Math.Max(2, target);
        float mag = MathF.Pow(10f, MathF.Floor(MathF.Log10(raw)));
        float norm = raw / mag;
        float nice = norm <= 1.5f ? 1f : norm <= 3f ? 2f : norm <= 7f ? 5f : 10f;
        return nice * mag;
    }

    private static string Fmt(float v)
    {
        if (MathF.Abs(v) < 1e-6f) return "0";
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
