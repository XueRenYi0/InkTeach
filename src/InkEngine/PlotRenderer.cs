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
}

/// <summary>AI 作图请求：解析式 + 定义域 + 标题。</summary>
internal sealed class PlotSpec
{
    public string Title = "";
    public float XMin = -5f, XMax = 5f;
    public readonly List<PlotSeries> Series = new();
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
    public static ImageData Render(PlotSpec spec, int width = 1100, int height = 760)
    {
        if (spec == null || spec.Series.Count == 0) return null;
        width = Math.Clamp(width, 400, 2000);
        height = Math.Clamp(height, 300, 1400);

        // 先采样，决定 y 范围。
        const int samples = 1400;
        var curves = new List<(PlotSeries S, List<(float X, float Y)> Pts)>();
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var s in spec.Series)
        {
            PlotExpression expr;
            try { expr = PlotExpression.Parse(s.Expr); }
            catch { continue; }
            if (expr == null) continue;
            var pts = new List<(float, float)>(samples);
            for (int i = 0; i <= samples; i++)
            {
                float x = spec.XMin + (spec.XMax - spec.XMin) * i / samples;
                double y = expr.Eval(x);
                if (!double.IsFinite(y)) continue;
                if (Math.Abs(y) > 1e6) continue;
                pts.Add((x, (float)y));
                if (y < yMin) yMin = (float)y;
                if (y > yMax) yMax = (float)y;
            }
            if (pts.Count > 0) curves.Add((s, pts));
        }
        if (curves.Count == 0) return null;

        if (!float.IsFinite(yMin) || !float.IsFinite(yMax))
        { yMin = -5; yMax = 5; }
        if (yMax - yMin < 1e-4f) { yMin -= 1; yMax += 1; }
        float pad = (yMax - yMin) * 0.15f;
        yMin -= pad; yMax += pad;
        // 防止 exp 之类把图压扁：显示范围限制在 ±30，曲线出界就裁掉。
        yMin = MathF.Max(yMin, -30f);
        yMax = MathF.Min(yMax, 30f);
        if (yMax - yMin < 0.5f) { yMin -= 0.5f; yMax += 0.5f; }

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

            float left = 92f, right = 44f, top = 64f, bottom = 74f;
            float pw = width - left - right;
            float ph = height - top - bottom;
            if (pw < 50 || ph < 50) { canvas.Restore(); bmp.Dispose(); return null; }

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
            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
                                 ?? SKTypeface.Default;
            using var label = new SKPaint
            {
                Color = new SKColor(82, 86, 94), IsAntialias = true,
                TextSize = 20f, Typeface = typeface,
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
                    Color = new SKColor(
                        (byte)Math.Clamp((int)(s.Color.R * 255f), 0, 255),
                        (byte)Math.Clamp((int)(s.Color.G * 255f), 0, 255),
                        (byte)Math.Clamp((int)(s.Color.B * 255f), 0, 255),
                        (byte)Math.Clamp((int)(s.Color.A * 255f), 0, 255)),
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
                    float sx = X(x), sy = Y(y);
                    bool jump = started && MathF.Abs(sy - prevY) > ph * 1.4f;
                    if (!started || jump) { path.MoveTo(sx, sy); started = true; }
                    else path.LineTo(sx, sy);
                    prevY = sy;
                }
                canvas.DrawPath(path, pen);
            }
            canvas.Restore();

            // 标题 / 解析式
            if (!string.IsNullOrWhiteSpace(spec.Title))
            {
                using var titlePaint = new SKPaint
                {
                    Color = new SKColor(34, 38, 46), IsAntialias = true,
                    TextSize = 26f, Typeface = typeface,
                };
                canvas.DrawText(spec.Title, left, 38, titlePaint);
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
