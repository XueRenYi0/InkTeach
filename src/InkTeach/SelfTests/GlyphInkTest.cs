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
    /// <summary>自检：把一个字按「轮廓 + even-odd 填充」渲染成 PNG，验证挖孔。</summary>
    private void GlyphFillPng(string fontPath, char ch, string outPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 字形填充 ===");
        string svg = GlyphInk.GetSvgPath(fontPath, ch);
        if (svg == null) { Console.WriteLine("取不到轮廓"); ExitCode = 1; return; }

        var bgra = _windows[0].RenderGlyphToBgra(svg, new Color4(0.86f, 0.16f, 0.12f, 1f),
                                                  out int w, out int h);
        if (bgra == null) { Console.WriteLine("填充渲染失败"); ExitCode = 1; return; }

        var bytes = ExportFormats.Encode(outPath, bgra, w, h, 1, out string tag);
        if (bytes == null) { Console.WriteLine("编码失败"); ExitCode = 1; return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"写出：{outPath}（{w}×{h}，{bytes.Length / 1024.0:F0} KB）");
        ExitCode = 0;
    }

    /// <summary>自检：造一组 Glyph 对象并渲染整块板（验证对象化后的画法）。</summary>
    private void GlyphObjPng(string fontPath, string text, string outPath, float size)
    {
        Console.WriteLine();
        Console.WriteLine("=== 字形对象 ===");
        var strokes = new List<Stroke>();
        float x = 20;
        foreach (char ch in text)
        {
            string svg = GlyphInk.GetSvgPath(fontPath, ch);
            if (svg == null) continue;
            var s = GlyphFill.BuildStroke(svg, size, new Color4(0.86f, 0.16f, 0.12f, 1f), x, 20);
            if (s == null) continue;
            strokes.Add(s);
            // 用对象自己的包围盒推进
            float maxX = 0;
            foreach (var p in s.Points) if (p.X > maxX) maxX = p.X;
            x = maxX + size * 0.08f;
            Console.WriteLine($"  {ch}: {s.Kind}, 点 {s.Points.Count}");
        }
        if (strokes.Count == 0) { Console.WriteLine("没有生成对象"); ExitCode = 1; return; }

        float minX = float.MaxValue, minY = float.MaxValue, maxX2 = float.MinValue, maxY2 = float.MinValue;
        foreach (var s in strokes)
            foreach (var p in s.Points)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX2) maxX2 = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY2) maxY2 = p.Y;
            }
        var box = new RectF { MinX = minX - 10, MinY = minY - 10, MaxX = maxX2 + 10, MaxY = maxY2 + 10 };
        int w = (int)MathF.Ceiling(box.MaxX - box.MinX);
        int h = (int)MathF.Ceiling(box.MaxY - box.MinY);

        var bgra = _windows[0].RenderStrokesToBgra(strokes, box, w, h);
        if (bgra == null) { Console.WriteLine("渲染失败"); ExitCode = 1; return; }
        var bytes = ExportFormats.Encode(outPath, bgra, w, h, 1, out string tag);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"写出：{outPath}（{w}×{h}）");
        ExitCode = 0;
    }

    /// <summary>自检：Glyph 对象存盘 → 重开 → 再渲染，验证 .inkb 往返。</summary>
    private void GlyphSaveTest(string fontPath, string text, string outPath, float size)
    {
        Console.WriteLine();
        Console.WriteLine("=== 字形对象 存盘往返 ===");
        var doc = new InkDocument();
        float x = 20;
        foreach (char ch in text)
        {
            var s = GlyphFill.BuildStroke(GlyphInk.GetSvgPath(fontPath, ch), size,
                                          new Color4(0.86f, 0.16f, 0.12f, 1f), x, 20);
            if (s == null) continue;
            doc.Strokes.Add(s);
            float maxX = 0;
            foreach (var p in s.Points) if (p.X > maxX) maxX = p.X;
            x = maxX + size * 0.08f;
        }
        Console.WriteLine($"存盘前：{doc.Strokes.Count} 个对象");

        byte[] data = InkSerializer.Save(doc);
        Console.WriteLine($"序列化：{data.Length} 字节");

        var doc2 = new InkDocument();
        InkSerializer.LoadInto(doc2, data);
        Console.WriteLine($"重开后：{doc2.Strokes.Count} 个对象");

        if (doc2.Strokes.Count != doc.Strokes.Count)
        {
            Console.WriteLine("!! 数量不一致，往返失败");
            ExitCode = 1;
            return;
        }
        int glyph = 0;
        foreach (var s in doc2.Strokes) if (s.Kind == StrokeKind.Glyph) glyph++;
        Console.WriteLine($"其中 Glyph：{glyph}");

        var list = new List<Stroke>();
        foreach (var s in doc2.Strokes) list.Add(s);
        float minX = float.MaxValue, minY = float.MaxValue, maxX2 = float.MinValue, maxY2 = float.MinValue;
        foreach (var s in list)
            foreach (var p in s.Points)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX2) maxX2 = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY2) maxY2 = p.Y;
            }
        var box = new RectF { MinX = minX - 10, MinY = minY - 10, MaxX = maxX2 + 10, MaxY = maxY2 + 10 };
        int w = (int)MathF.Ceiling(box.MaxX - box.MinX);
        int h = (int)MathF.Ceiling(box.MaxY - box.MinY);
        var bgra = _windows[0].RenderStrokesToBgra(list, box, w, h);
        if (bgra == null) { Console.WriteLine("渲染失败"); ExitCode = 1; return; }
        File.WriteAllBytes(outPath, ExportFormats.Encode(outPath, bgra, w, h, 1, out _));
        Console.WriteLine($"写出：{outPath}（{w}×{h}）");
        ExitCode = glyph == doc.Strokes.Count ? 0 : 1;
    }

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
