using System.IO;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>自检：Glyph 行用整笔橡皮擦局部（布尔减），不是整行消失；Ctrl+Z 还原。</summary>
    private void GlyphErasePng(string outBefore, string outAfter)
    {
        Console.WriteLine();
        Console.WriteLine("=== Glyph 局部擦除 ===");
        Doc.Clear();
        Doc.ClearHistory();
        var box = new RectF { MinX = 40, MinY = 40, MaxX = 1040, MaxY = 132 };
        var g = GlyphText.BuildLine(GlyphText.CjkFontPath, "手写优化测试", box,
                                    new Color4(0.86f, 0.16f, 0.12f, 1f));
        if (g == null) { Console.WriteLine("没有生成 Glyph"); ExitCode = 1; return; }
        Doc.AppendStroke(g);
        int points0 = g.Points.Count;

        SaveGlyphImage(outBefore, "擦前");
        float cx = 0f, cy = 0f;
        int n = 0;
        foreach (var p in g.Points) { cx += p.X; cy += p.Y; n++; }
        cx /= MathF.Max(1, n); cy /= MathF.Max(1, n);

        Doc.BeginErase();
        int hit = Doc.EraseAt(cx, cy, 18f);
        Doc.EndErase();
        Console.WriteLine($"擦除命中：{hit}，点 {points0} → {g.Points.Count}，对象数 {Doc.Strokes.Count}");
        SaveGlyphImage(outAfter, "擦后");

        bool local = hit > 0 && Doc.Strokes.Count == 1 && g.Points.Count != points0;
        Doc.Undo();
        bool undo = Doc.Strokes.Count == 1 && g.Points.Count == points0;
        Console.WriteLine(undo ? $"撤销：OK（回到 {points0} 点）" : $"撤销：FAIL（{g.Points.Count} ≠ {points0}）");
        Console.WriteLine(local && undo ? "结论：OK" : "结论：FAIL");
        ExitCode = local && undo ? 0 : 1;
    }

    private void SaveGlyphImage(string path, string label)
    {
        var list = new List<Stroke>();
        foreach (var s in Doc.Strokes) list.Add(s);
        var box = EditRegion.Of(list);
        if (box.IsEmpty) return;
        box = box.Inflate(24f * DpiScale);
        int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        var bgra = _windows[0].RenderStrokesToBgra(list, box, w, h);
        if (bgra == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = ExportFormats.Encode(path, bgra, w, h, 1, out _);
        if (bytes != null) File.WriteAllBytes(path, bytes);
        Console.WriteLine($"写出：{path}（{label}）");
    }
}
