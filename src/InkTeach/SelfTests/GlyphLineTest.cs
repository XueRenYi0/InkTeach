using System.IO;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：一整行文字 → 一个 Glyph 对象（原行框内排版）。
    ///   --glyphline &lt;字体文件&gt; &lt;文字&gt; &lt;输出png&gt; [行高] [行宽]
    /// </summary>
    private void GlyphLinePng(string fontPath, string text, string outPath, float lineH, float lineW)
    {
        Console.WriteLine();
        Console.WriteLine("=== 一行文字 → Glyph ===");
        Console.WriteLine($"字体：{fontPath}");
        Console.WriteLine($"文字：{text}");

        if (!File.Exists(fontPath))
        {
            Console.WriteLine("找不到字体");
            ExitCode = 1;
            return;
        }

        var box = new RectF { MinX = 20, MinY = 20, MaxX = 20 + lineW, MaxY = 20 + lineH };
        var s = GlyphText.BuildLine(fontPath, text, box, new Color4(0.86f, 0.16f, 0.12f, 1f));
        if (s == null)
        {
            Console.WriteLine("没有生成轮廓");
            ExitCode = 1;
            return;
        }

        // 轮廓数 / 起点数必须一一对应（每个闭合轮廓第一个点的 P = -1）。
        int contours = 0, starts = 0;
        foreach (var p in s.Points)
        {
            if (p.P < 0) { contours++; starts++; }
        }

        // 版心检查：墨迹必须落在原行框里（允许 1px 抗锯齿误差）。
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in s.Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        Console.WriteLine($"轮廓：{contours}  点：{s.Points.Count}");
        Console.WriteLine($"对象 Bounds：({s.Bounds.MinX:F1},{s.Bounds.MinY:F1})..({s.Bounds.MaxX:F1},{s.Bounds.MaxY:F1})");
        s.RecomputeBounds();
        Console.WriteLine($"重算后 Bounds：({s.Bounds.MinX:F1},{s.Bounds.MinY:F1})..({s.Bounds.MaxX:F1},{s.Bounds.MaxY:F1})");
        Console.WriteLine($"墨迹框：({minX:F1},{minY:F1})..({maxX:F1},{maxY:F1})  " +
                          $"行框：({box.MinX:F0},{box.MinY:F0})..({box.MaxX:F0},{box.MaxY:F0})");
        Console.WriteLine($"宽 {maxX - minX:F1}/{lineW:F0}  高 {maxY - minY:F1}/{lineH:F0}");

        // 排版层回退提示：打印主字体缺的字（回退字体已经补上，但值得知道）。
        var l = GlyphText.Layout(fontPath, text, lineH * GlyphText.SizePerLineHeight, 0, 0);
        if (l.Missing.Length > 0) Console.WriteLine($"主字体缺字（已回退）：{l.Missing}");

        bool ok = contours > 0 && contours == starts
                  && minX >= box.MinX - 1f && maxX <= box.MaxX + 1f
                  && minY >= box.MinY - 1f && maxY <= box.MaxY + 1f;

        var r = new RectF
        {
            MinX = minX - 12, MinY = minY - 12,
            MaxX = maxX + 12, MaxY = maxY + 12,
        };
        int w = Math.Max(1, (int)MathF.Ceiling(r.MaxX - r.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(r.MaxY - r.MinY));
        var bgra = _windows[0].RenderStrokesToBgra(new List<Stroke> { s }, r, w, h);
        if (bgra == null)
        {
            Console.WriteLine("渲染失败");
            ExitCode = 1;
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, ExportFormats.Encode(outPath, bgra, w, h, 1, out _));
        Console.WriteLine($"写出：{outPath}（{w}×{h}）");
        Console.WriteLine(ok ? "结论：OK" : "结论：FAIL（轮廓/版心不对）");
        ExitCode = ok ? 0 : 1;
    }
}
