using System.IO;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>自检：把一块选区分成文字块，打印每块的行数/范围。</summary>
    private void HandBlocksDump(string inPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 文字分块 ===");
        var doc = new InkDocument();
        InkSerializer.LoadInto(doc, File.ReadAllBytes(inPath));
        var all = new List<Stroke>();
        foreach (var s in doc.Strokes) all.Add(s);
        var region = EditRegion.Of(all);
        var blocks = HandwritingBlocks.Detect(all, region);
        Console.WriteLine($"块数：{blocks.Count}");
        foreach (var s in all)
        {
            var b = s.WorldInkBounds;
            if (b.MaxY - b.MinY > (region.MaxY - region.MinY) * 0.3f)
                Console.WriteLine($"  高对象：{s.Kind} {b.MinX:F0}..{b.MaxX:F0}  y {b.MinY:F0}..{b.MaxY:F0}");
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var r = b.Region;
            Console.WriteLine($"  块 {i + 1}：{b.Strokes.Count} 笔，{b.Lines.Count} 行，" +
                              $"x {r.MinX:F0}..{r.MaxX:F0}，y {r.MinY:F0}..{r.MaxY:F0}");
        }
        ExitCode = blocks.Count > 0 ? 0 : 1;
    }
}
