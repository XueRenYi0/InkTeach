using System.IO;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 开发/演示用：**美化原笔迹**——不换字、不改内容，只把采样点做平滑、
    /// 把笔宽统一，然后按原样重新渲染。
    ///
    /// 真正的产品化会多做：基线对齐、字高归一、行距整理（那几步要按行分组，
    /// 这里只演示“同一笔迹变工整”这一层）。
    /// </summary>
    private void InkBeautify(string inPath, string outPath, int passes)
    {
        Console.WriteLine();
        Console.WriteLine("=== 原笔迹美化 ===");
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

        // 先把整块板子的笔宽中位数算出来，后面把所有笔往它靠——"同一支笔"
        var widths = new List<float>();
        foreach (var s in strokes)
            if (s.Kind == StrokeKind.Freehand && s.Points.Count >= 3) widths.Add(s.Width);
        widths.Sort();
        float medianW = widths.Count > 0 ? widths[widths.Count / 2] : 0f;

        int smoothed = 0, freehand = 0;
        foreach (var s in strokes)
        {
            if (s.Kind != StrokeKind.Freehand || s.Points.Count < 3) continue;
            freehand++;
            if (medianW > 0) s.Width = 0.5f * s.Width + 0.5f * medianW;
            for (int pass = 0; pass < passes; pass++)
            {
                var src = s.Points.ToArray();
                for (int i = 1; i < s.Points.Count - 1; i++)
                {
                    var a = src[i - 1];
                    var b = src[i];
                    var c = src[i + 1];
                    s.Points[i] = new InkPoint
                    {
                        X = (a.X + 2f * b.X + c.X) * 0.25f,
                        Y = (a.Y + 2f * b.Y + c.Y) * 0.25f,
                        P = (a.P + 2f * b.P + c.P) * 0.25f,
                        T = b.T,
                    };
                }
            }
            // 笔宽统一：关掉真实压感，渲染回落到等宽描边
            if (s.HasPressure) { s.HasPressure = false; smoothed++; }
        }
        Console.WriteLine($"笔画：{strokes.Count}，其中手写 {freehand}，压感改等宽 {smoothed}，平滑 {passes} 遍");

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
        Console.WriteLine($"写出：{outPath}（{w}×{h}，{bytes.Length / 1024.0:F0} KB）");
        ExitCode = 0;
    }
}
