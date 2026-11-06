using System.IO;
using System.Text.Json;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：用 OCR 返回的 bbox 直接放回原位置（不靠行投影配对）。
    ///   --handbbox &lt;板书.inkb&gt; &lt;bbox.json&gt; &lt;输出png&gt; [safe|all]
    /// </summary>
    private void HandBboxPng(string inPath, string bboxPath, string outPath, bool includeFormulas)
    {
        Console.WriteLine();
        Console.WriteLine("=== 按 OCR bbox 替换 ===");
        var doc = new InkDocument();
        InkSerializer.LoadInto(doc, File.ReadAllBytes(inPath));
        var all = new List<Stroke>();
        foreach (var s in doc.Strokes) all.Add(s);

        var inkBox = EditRegion.Of(all);
        var pngBox = inkBox.Inflate(24f * DpiScale);
        float pngW = pngBox.MaxX - pngBox.MinX, pngH = pngBox.MaxY - pngBox.MinY;

        var entries = LoadBboxEntries(bboxPath);
        Console.WriteLine($"OCR 条目：{entries.Count}  画布 {pngW:F0}×{pngH:F0}");

        var removed = new HashSet<Stroke>();
        var glyphs = new List<Stroke>();
        int used = 0, skipped = 0;
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) continue;
            if (!includeFormulas && OcrText.HasMath(e.Text)) { skipped++; continue; }

            var box = new RectF
            {
                MinX = pngBox.MinX + (float)e.X0 * pngW,
                MinY = pngBox.MinY + (float)e.Y0 * pngH,
                MaxX = pngBox.MinX + (float)e.X1 * pngW,
                MaxY = pngBox.MinY + (float)e.Y1 * pngH,
            };
            if (box.MaxX - box.MinX < 4 || box.MaxY - box.MinY < 4) continue;

            var color = LineColor(all, box);
            var part = HandwritingComposer.BuildLine(e.Text, box, color);
            if (part == null || part.Count == 0) continue;
            glyphs.AddRange(part);
            used++;
            var rb = box.Inflate(6f);
            foreach (var s in all)
            {
                var b = s.WorldInkBounds;
                float iw = MathF.Min(b.MaxX, rb.MaxX) - MathF.Max(b.MinX, rb.MinX);
                float ih = MathF.Min(b.MaxY, rb.MaxY) - MathF.Max(b.MinY, rb.MinY);
                float area = MathF.Max(1f, (b.MaxX - b.MinX) * (b.MaxY - b.MinY));
                bool overlap = iw > 0 && ih > 0 && iw * ih / area > 0.25f;
                float cx = (b.MinX + b.MaxX) * 0.5f, cy = (b.MinY + b.MaxY) * 0.5f;
                bool center = cx >= rb.MinX && cx <= rb.MaxX && cy >= rb.MinY && cy <= rb.MaxY;
                if (overlap || center) removed.Add(s);
            }
        }
        Console.WriteLine($"使用 {used} 条，跳过公式 {skipped} 条，删原笔迹 {removed.Count}，加对象 {glyphs.Count}");

        var result = new List<Stroke>();
        foreach (var s in all) if (!removed.Contains(s)) result.Add(s);
        result.AddRange(glyphs);
        var r = EditRegion.Of(result);
        if (r.IsEmpty) { Console.WriteLine("结果为空"); ExitCode = 1; return; }
        r = r.Inflate(24f * DpiScale);
        int w = Math.Max(1, (int)MathF.Ceiling(r.MaxX - r.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(r.MaxY - r.MinY));
        var bgra = _windows[0].RenderStrokesToBgra(result, r, w, h);
        if (bgra == null) { Console.WriteLine("渲染失败"); ExitCode = 1; return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, ExportFormats.Encode(outPath, bgra, w, h, 1, out _));
        Console.WriteLine($"写出：{outPath}（{w}×{h}）");
        ExitCode = 0;
    }

    private sealed class BboxEntry
    {
        public string Text;
        public double X0, Y0, X1, Y1;
    }

    private static List<BboxEntry> LoadBboxEntries(string path)
    {
        var list = new List<BboxEntry>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement arr;
        if (doc.RootElement.ValueKind == JsonValueKind.Array) arr = doc.RootElement;
        else if (doc.RootElement.TryGetProperty("parsed", out var p) && p.ValueKind == JsonValueKind.Array)
            arr = p;
        else return list;

        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty("text", out var t)) continue;
            var b = new BboxEntry { Text = t.GetString() ?? "" };
            if (!TryNum(e, "x0", out b.X0) || !TryNum(e, "y0", out b.Y0) ||
                !TryNum(e, "x1", out b.X1) || !TryNum(e, "y1", out b.Y1)) continue;
            list.Add(b);
        }
        return list;
    }

    private static bool TryNum(JsonElement e, string name, out double v)
    {
        v = 0;
        return e.TryGetProperty(name, out var x) && x.TryGetDouble(out v);
    }

    private static Color4 LineColor(IReadOnlyList<Stroke> all, RectF box)
    {
        int n = 0;
        float r = 0, g = 0, b = 0;
        foreach (var s in all)
        {
            var bb = s.WorldInkBounds;
            float cx = (bb.MinX + bb.MaxX) * 0.5f, cy = (bb.MinY + bb.MaxY) * 0.5f;
            if (cx < box.MinX || cx > box.MaxX || cy < box.MinY || cy > box.MaxY) continue;
            var c = s.Color;
            int w = Math.Max(1, s.Points.Count);
            r += c.R * w; g += c.G * w; b += c.B * w; n += w;
        }
        return n > 0 ? new Color4(r / n, g / n, b / n, 1f) : new Color4(0.86f, 0.16f, 0.12f, 1f);
    }
}
