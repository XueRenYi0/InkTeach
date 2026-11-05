using System.IO;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 开发/基准用：把一个 `.inkb` 板书文件渲染成 PNG（白底），用于 OCR 语料和对照。
    /// 只读文件，不改当前文档、不写剪贴板。
    /// </summary>
    private void InkBoardPng(string inPath, string outPath)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 板书转 PNG ===");
        Console.WriteLine($"读入：{inPath}");
        if (!File.Exists(inPath)) { Console.WriteLine("找不到文件"); ExitCode = 1; return; }

        var doc = new InkDocument();
        InkSerializer.LoadInto(doc, File.ReadAllBytes(inPath));
        var strokes = doc.StrokesOf(InkDocument.BoardPageKey);
        if (strokes == null || strokes.Count == 0)
        {
            strokes = new List<Stroke>();
            foreach (var s in doc.Strokes) strokes.Add(s);
        }
        Console.WriteLine($"笔画：{strokes.Count}（板页 {InkDocument.BoardPageKey}）");

        var box = EditRegion.Of(strokes);
        if (box.IsEmpty) { Console.WriteLine("没有可渲染的内容"); ExitCode = 1; return; }
        box = box.Inflate(24f * DpiScale);
        int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 64_000_000) { Console.WriteLine($"太大：{w}×{h}"); ExitCode = 1; return; }

        var bgra = _windows[0].RenderStrokesToBgra(strokes, box, w, h);
        if (bgra == null) { Console.WriteLine("渲染失败"); ExitCode = 1; return; }
        var bytes = ExportFormats.Encode(outPath, bgra, w, h, 1, out string tag);
        if (bytes == null) { Console.WriteLine("编码失败"); ExitCode = 1; return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"写出：{outPath}（{w}×{h}，{tag}，{bytes.Length / 1024.0:F0} KB）");
        ExitCode = 0;
    }
}
