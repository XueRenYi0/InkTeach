using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SkiaSharp;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// AI 题图的“结构化图形”描述：点 / 线段 / 圆 / 标签。
///
/// 为什么不用图片：数学题图需要清晰、可缩放、标签位置稳定；让模型返回坐标，
/// 我们自己画矢量图，比让模型生成图片可靠得多。图片只作为 OCR 裁图/AI 配图兜底。
/// </summary>
internal sealed class FigureSpec
{
    public string Kind = "geometry2d";
    public readonly Dictionary<string, Vector2> Points2D = new();
    public readonly Dictionary<string, Vector3> Points3D = new();
    public readonly List<(string A, string B)> Segments = new();
    public readonly List<(string Center, float Radius)> Circles = new();
    public readonly Dictionary<string, string> Labels = new();
    /// <summary>kind=quadratic 时的 y = a x² + b x + c 系数。</summary>
    public float Qa = 1f, Qb, Qc;
    public float DomainMin = -6f, DomainMax = 6f;

    public static FigureSpec Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var spec = new FigureSpec { Kind = Str(root, "kind") ?? "geometry2d" };

            if (root.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in pts.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) continue;
                    var nums = new List<float>();
                    foreach (var n in p.Value.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.Number) nums.Add((float)n.GetDouble());
                    if (nums.Count >= 3)
                        spec.Points3D[p.Name] = new Vector3(nums[0], nums[1], nums[2]);
                    else if (nums.Count >= 2)
                        spec.Points2D[p.Name] = new Vector2(nums[0], nums[1]);
                }
            }

            void ReadEdges(string name)
            {
                if (!root.TryGetProperty(name, out var segs) || segs.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var e in segs.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Array) continue;
                    string a = null, b = null;
                    int k = 0;
                    foreach (var v in e.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.String) continue;
                        if (k++ == 0) a = v.GetString(); else b = v.GetString();
                    }
                    if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
                        spec.Segments.Add((a, b));
                }
            }
            ReadEdges("segments");
            ReadEdges("edges");

            if (root.TryGetProperty("circles", out var circles) && circles.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in circles.EnumerateArray())
                {
                    string center = Str(c, "center");
                    float radius = 1f;
                    if (c.TryGetProperty("r", out var rv) && rv.ValueKind == JsonValueKind.Number)
                        radius = (float)rv.GetDouble();
                    if (!string.IsNullOrEmpty(center) && radius > 0f)
                        spec.Circles.Add((center, radius));
                }
            }

            if (root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                foreach (var l in labels.EnumerateObject())
                    if (l.Value.ValueKind == JsonValueKind.String)
                        spec.Labels[l.Name] = l.Value.GetString();

            // 二次函数：AI 只给 a/b/c，曲线由本地采样/现有抛物线对象画。
            if (spec.Kind == "quadratic")
            {
                if (root.TryGetProperty("a", out var av) && av.ValueKind == JsonValueKind.Number)
                    spec.Qa = (float)av.GetDouble();
                if (root.TryGetProperty("b", out var bv) && bv.ValueKind == JsonValueKind.Number)
                    spec.Qb = (float)bv.GetDouble();
                if (root.TryGetProperty("c", out var cv) && cv.ValueKind == JsonValueKind.Number)
                    spec.Qc = (float)cv.GetDouble();
                if (root.TryGetProperty("domain", out var dv) && dv.ValueKind == JsonValueKind.Array)
                {
                    var nums = new List<float>();
                    foreach (var n in dv.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.Number) nums.Add((float)n.GetDouble());
                    if (nums.Count >= 2) { spec.DomainMin = nums[0]; spec.DomainMax = nums[1]; }
                }
                return MathF.Abs(spec.Qa) > 0.0001f ? spec : null;
            }

            bool hasPoints = spec.Points2D.Count + spec.Points3D.Count > 0;
            return hasPoints && spec.Segments.Count > 0 ? spec : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// 把 FigureSpec 画成一张高分辨率透明 PNG/BGRA：自动缩放到画布、居中、点小圆、线宽、标签。
/// 2D 直接画；3D 先做等轴测投影，再按 2D 画。
///
/// ⚠ 这是**原型/卡片快照**用的离屏栅格器（Skia）。主画布一直是 D2D；
/// 交互图形不应长期走“Skia 位图再交给 D2D”，目标是在 FigureScene 渲染层直接用 D2D。
/// </summary>
internal static class FigureRenderer
{
    public static ImageData Render(FigureSpec spec, int width = 820, int height = 600)
    {
        if (spec == null || width <= 0 || height <= 0) return null;

        var projected = new Dictionary<string, Vector2>();
        foreach (var p in spec.Points2D) projected[p.Key] = p.Value;
        foreach (var p in spec.Points3D)
        {
            // 等轴测：x 向右下、y 向左下、z 向上。
            float sx = (p.Value.X - p.Value.Y) * 0.866f;
            float sy = (p.Value.X + p.Value.Y) * 0.5f - p.Value.Z;
            projected[p.Key] = new Vector2(sx, sy);
        }
        if (projected.Count == 0) return null;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in projected.Values)
        {
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
        }
        float bw = MathF.Max(0.001f, maxX - minX);
        float bh = MathF.Max(0.001f, maxY - minY);
        const float pad = 64f;
        float scale = MathF.Min((width - pad * 2f) / bw, (height - pad * 2f) / bh);
        if (!float.IsFinite(scale) || scale <= 0f) scale = 1f;

        // 居中：把缩放后的 bbox 放到画布中心。
        float offsetX = (width - bw * scale) * 0.5f - minX * scale;
        float offsetY = (height - bh * scale) * 0.5f + maxY * scale;

        Vector2 ToScreen(Vector2 p) => new(
            offsetX + p.X * scale,
            offsetY - p.Y * scale);

        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888,
                                               SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);

            using var line = new SKPaint
            {
                Color = new SKColor(32, 36, 44),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 4f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            using var dot = new SKPaint
            {
                Color = new SKColor(32, 36, 44),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            using var circleLine = new SKPaint
            {
                Color = new SKColor(32, 36, 44),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f,
            };

            foreach (var seg in spec.Segments)
            {
                if (!projected.TryGetValue(seg.A, out var a) ||
                    !projected.TryGetValue(seg.B, out var b)) continue;
                var sa = ToScreen(a);
                var sb = ToScreen(b);
                canvas.DrawLine(sa.X, sa.Y, sb.X, sb.Y, line);
            }

            foreach (var c in spec.Circles)
            {
                if (!projected.TryGetValue(c.Center, out var center)) continue;
                var sc = ToScreen(center);
                canvas.DrawCircle(sc.X, sc.Y, MathF.Max(1f, c.Radius * scale), circleLine);
            }

            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
                                 ?? SKTypeface.Default;
            using var text = new SKPaint
            {
                Color = new SKColor(24, 28, 36),
                IsAntialias = true,
                TextSize = 34f,
                Typeface = typeface,
                TextAlign = SKTextAlign.Center,
            };

            // 标签：从点集的中心向外偏移，避免压在线上。
            Vector2 centerAll = new(minX + bw * 0.5f, minY + bh * 0.5f);
            foreach (var p in projected)
            {
                var s = ToScreen(p.Value);
                canvas.DrawCircle(s.X, s.Y, 5f, dot);

                string label = spec.Labels.TryGetValue(p.Key, out var l) ? l : p.Key;
                if (string.IsNullOrEmpty(label)) continue;
                var dir = p.Value - centerAll;
                float len = dir.Length();
                if (len < 0.001f) dir = new Vector2(0, 1);
                else dir /= len;
                float ox = dir.X * 28f;
                float oy = -dir.Y * 28f;
                canvas.DrawText(label, s.X + ox, s.Y + oy + 12f, text);
            }
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
        finally
        {
            bmp.Dispose();
        }
    }

    /// <summary>自检/兜底：一个带标签的直角三角形。</summary>
    public static FigureSpec SampleTriangle()
    {
        var s = new FigureSpec { Kind = "geometry2d" };
        s.Points2D["A"] = new Vector2(0, 0);
        s.Points2D["B"] = new Vector2(4, 0);
        s.Points2D["C"] = new Vector2(0, 3);
        s.Segments.Add(("A", "B"));
        s.Segments.Add(("B", "C"));
        s.Segments.Add(("C", "A"));
        s.Labels["A"] = "A";
        s.Labels["B"] = "B";
        s.Labels["C"] = "C";
        return s;
    }
}
