using System.IO;
using System.Text.Json;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：拿一张真实板书 + 一份 OCR 结果，整行换成本地手写字体（Glyph 对象）。
    ///   --handreplace &lt;板书.inkb&gt; &lt;ocr.json&gt; &lt;输出png&gt; &lt;字体文件&gt; [文字区左边界]
    ///
    /// 输出图里：文字行被替换，图形 / 箭头 / 圈注保持原样。
    /// </summary>
    private void HandReplacePng(string inPath, string ocrPath, string outPath,
                                string fontPath, float textLeft)
    {
        Console.WriteLine();
        Console.WriteLine("=== 整行手写替换 ===");
        if (!File.Exists(inPath) || !File.Exists(ocrPath) || !File.Exists(fontPath))
        {
            Console.WriteLine("文件不存在（板书 / OCR / 字体）");
            ExitCode = 1;
            return;
        }

        var doc = new InkDocument();
        InkSerializer.LoadInto(doc, File.ReadAllBytes(inPath));
        var all = doc.StrokesOf(InkDocument.BoardPageKey);
        if (all == null || all.Count == 0)
        {
            all = new List<Stroke>();
            foreach (var s in doc.Strokes) all.Add(s);
        }

        var candidates = new List<Stroke>();
        foreach (var s in all)
        {
            var b = s.WorldInkBounds;
            if (b.IsEmpty) continue;
            // 真实板书 b1 左上角是立方体 + A/B/C/D 标注（x<480, y<560）。
            // 它们不是文字，不能进切行候选；但保留在结果里。
            if (b.MaxX < textLeft && b.MaxY < 560f) continue;
            candidates.Add(s);
        }
        var region = EditRegion.Of(candidates);
        if (region.IsEmpty)
        {
            Console.WriteLine("没有文字区");
            ExitCode = 1;
            return;
        }
        Console.WriteLine($"文字区：({region.MinX:F0},{region.MinY:F0})..({region.MaxX:F0},{region.MaxY:F0})");

        var texts = LoadOcrLines(ocrPath).ToList();
        var lines = HandwritingReplace.DetectLines(candidates, region, texts.Count);
        Console.WriteLine($"检测到 {lines.Count} 行，OCR 给了 {texts.Count} 行");
        for (int i = 0; i < Math.Min(lines.Count, texts.Count); i++)
        {
            var b = lines[i].Box;
            string t = texts[i].Length > 42 ? texts[i].Substring(0, 42) + "…" : texts[i];
            Console.WriteLine($"  {i + 1,2}. y {b.MinY,5:F0}..{b.MaxY,-5:F0} " +
                              $"x {b.MinX,5:F0}..{b.MaxX,-5:F0}  {t}");
        }

        // 走真正的替换路径（删原笔迹 + 加 Glyph + 合成一步撤销），
        // 再在第二份文档里验证 Ctrl+Z 能回到替换前。
        var work = new InkDocument();
        InkSerializer.LoadInto(work, File.ReadAllBytes(inPath));
        var workAll = new List<Stroke>();
        foreach (var s in work.Strokes) workAll.Add(s);
        Console.WriteLine($"work.Strokes = {work.Strokes.Count}，workAll = {workAll.Count}");
        var workCandidates = new List<Stroke>();
        foreach (var s in workAll)
        {
            var b = s.WorldInkBounds;
            if (!b.IsEmpty && !(b.MaxX < textLeft && b.MaxY < 560f)) workCandidates.Add(s);
        }
        var workRegion = EditRegion.Of(workCandidates);
        int before = work.Strokes.Count;
        int applied = HandwritingReplace.Apply(work, workCandidates, workRegion, texts, fontPath, true);
        if (applied == 0)
        {
            Console.WriteLine("没有生成替换对象");
            ExitCode = 1;
            return;
        }
        Console.WriteLine($"替换 {applied} 行：{before} → {work.Strokes.Count} 个对象");
        work.Undo();
        bool undoOk = work.Strokes.Count == before;
        Console.WriteLine(undoOk ? "一步撤销：OK（回到替换前）" : $"一步撤销：FAIL（{work.Strokes.Count} ≠ {before}）");
        if (!undoOk) { ExitCode = 1; return; }
        // 撤销会回到替换前；重新做一次用来出图。
        work.Redo();

        var box = EditRegion.Of(work.Strokes);
        if (box.IsEmpty) { Console.WriteLine("结果为空"); ExitCode = 1; return; }
        Console.WriteLine($"结果墨迹框：({box.MinX:F0},{box.MinY:F0})..({box.MaxX:F0},{box.MaxY:F0})  Dpi={DpiScale:F2}");
        box = box.Inflate(24f * DpiScale);
        int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 64_000_000) { Console.WriteLine($"太大：{w}×{h}"); ExitCode = 1; return; }

        var bgra = _windows[0].RenderStrokesToBgra(work.Strokes, box, w, h);
        if (bgra == null) { Console.WriteLine("渲染失败"); ExitCode = 1; return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var bytes = ExportFormats.Encode(outPath, bgra, w, h, 1, out string tag);
        if (bytes == null) { Console.WriteLine("编码失败"); ExitCode = 1; return; }
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"写出：{outPath}（{w}×{h}，{tag}，{bytes.Length / 1024.0:F0} KB）");
        ExitCode = 0;
    }

    private static List<string> LoadOcrLines(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("text", out var t)) return new List<string>();
        var lines = new List<string>();
        foreach (var line in (t.GetString() ?? "").Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
        return lines;
    }
}
