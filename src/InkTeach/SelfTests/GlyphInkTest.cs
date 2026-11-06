using System.IO;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 开发/自检用：把一段文字用「字库描边」转成墨迹，渲染成 PNG。
    ///   --glyphink &lt;字体文件&gt; &lt;文字&gt; &lt;输出png&gt; [字高]
    /// </summary>
    /// <summary>自检：打印字形的矢量轮廓（验证「挖孔」是不是分成多条子路径）。</summary>
    private void GlyphPathDump(string fontPath, string text)
    {
        Console.WriteLine();
        Console.WriteLine("=== 字形轮廓 ===");
        foreach (char ch in text)
        {
            string svg = GlyphInk.GetSvgPath(fontPath, ch);
            if (svg == null) { Console.WriteLine($"{ch}: 取不到"); continue; }
            int contours = 0;
            foreach (char c in svg) if (c == 'M') contours++;
            Console.WriteLine($"{ch}: 轮廓 {contours} 条，SVG {svg.Length} 字符");
            Console.WriteLine("    " + (svg.Length > 160 ? svg.Substring(0, 160) + "…" : svg));
        }
        ExitCode = 0;
    }

    private void GlyphInkPng(string fontPath, string text, string outPath, float height)
    {
        Console.WriteLine();
        Console.WriteLine("=== 字库描边 ===");
        if (!File.Exists(fontPath)) { Console.WriteLine("找不到字体：" + fontPath); ExitCode = 1; return; }

        var strokes = new List<Stroke>();
        float x = 0;
        foreach (char ch in text)
        {
            var g = GlyphInk.Get(fontPath, ch);
            if (g == null) { x += height * 0.6f; continue; }
            float charW = g.WidthRatio * height;
            foreach (var (xs, ys, strokeW) in g.Strokes)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen,
                    Kind = StrokeKind.Freehand,
                    Color = new Color4(0.86f, 0.16f, 0.12f, 1f),
                    Width = Math.Max(1f, strokeW * height),
                    HasPressure = false,
                };
                for (int i = 0; i < xs.Length; i++)
                    s.Points.Add(new InkPoint { X = x + xs[i] * height, Y = ys[i] * height, P = 1f, T = i });
                if (s.Points.Count >= 2) { s.InvalidateMetrics(); strokes.Add(s); }
            }
            x += charW + height * 0.08f;
        }

        Console.WriteLine($"字：{text.Length}，笔画：{strokes.Count}");
        if (strokes.Count == 0) { Console.WriteLine("没有生成笔画"); ExitCode = 1; return; }

        // 自己算包围盒：EditRegion.Of 依赖的对象缓存这里没有走完整流程。
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var s in strokes)
            foreach (var p in s.Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        var box = new RectF { MinX = minX - 20f, MinY = minY - 20f, MaxX = maxX + 20f, MaxY = maxY + 20f };
        int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));

        var bgra = _windows[0].RenderStrokesToBgra(strokes, box, w, h);
        if (bgra == null) { Console.WriteLine("渲染失败"); ExitCode = 1; return; }
        var bytes = ExportFormats.Encode(outPath, bgra, w, h, 1, out string tag);
        if (bytes == null) { Console.WriteLine("编码失败"); ExitCode = 1; return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"写出：{outPath}（{w}×{h}，{bytes.Length / 1024.0:F0} KB）");
        ExitCode = 0;
    }
}
