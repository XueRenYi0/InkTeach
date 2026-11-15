using System.IO;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>自检：选中笔迹 → 识别 → 放进普通文本框（公式用 $...$ 渲染）。</summary>
    private void HandTextBoxPng(string inPath, string outPath, float textLeft)
    {
        Console.WriteLine();
        Console.WriteLine("=== 识别文本 ===");
        InkSerializer.LoadInto(Doc, File.ReadAllBytes(inPath));
        // 自检固定从“白纸”开始，避免上一次背景偏好影响结果。
        SetSelectedTextBackgroundFromUi(1);
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes)
        {
            var b = s.WorldInkBounds;
            if (!b.IsEmpty && !(b.MaxX < textLeft && b.MaxY < 560f)) Doc.Selected.Add(s);
        }
        Console.WriteLine($"选中 {Doc.Selected.Count} 个文字候选");

        RecognizeToTextBoxFromUi();
        var timeout = Environment.TickCount64 + 30_000;
        while (HandOcrBusy && Environment.TickCount64 < timeout)
            SettleFrames(50);

        Console.WriteLine($"状态：{HandOcrStatus}");
        int boxes = 0;
        Stroke first = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { boxes++; first ??= s; }
        Console.WriteLine($"文本框：{boxes} 个");
        if (first?.TextBox != null)
        {
            string t = first.TextBox.Text ?? "";
            Console.WriteLine($"文本长度：{t.Length}，前 80 字：{(t.Length > 80 ? t[..80] : t).Replace("\n", " / ")}");
        }

        // 2026-11-09：识别后原墨迹应删除；这一下撤回应同时恢复墨迹、移除文本框。
        Doc.Undo();
        int undoBoxes = 0, undoInk = 0;
        foreach (var s in Doc.Strokes)
        {
            if (s.Kind == StrokeKind.TextBox) undoBoxes++;
            else if (s.Kind == StrokeKind.Freehand) undoInk++;
        }
        Console.WriteLine($"撤销识别：文本框 {undoBoxes}，手写 {undoInk}");
        Doc.Redo();

        bool paperOk = false;
        try
        {
            byte[] saved = InkSerializer.Save(Doc);
            var doc2 = new InkDocument();
            InkSerializer.LoadInto(doc2, saved);
            foreach (var s in doc2.Strokes)
                if (s.Kind == StrokeKind.TextBox && s.TextBox?.Paper == true) paperOk = true;
            Console.WriteLine(paperOk ? "存盘：文本框纸面 OK" : "存盘：纸面丢失");
        }
        catch (Exception ex) { Console.WriteLine("存盘失败：" + ex.Message); }

        // 背景策略：默认白纸；改成透明后，记忆为“上一次调整”。
        if (first?.TextBox != null)
        {
            Doc.SelectOnly(new[] { first });
            SetSelectedTextBackgroundFromUi(0);
            bool bgFollow = first.TextBox.Fill.A < 0.01f && !first.TextBox.Paper;
            Console.WriteLine($"背景跟随上次调整（透明）：{bgFollow}");
            SetSelectedTextBackgroundFromUi(1);   // 还原白纸，截图/后续运行不受影响
        }

        var box = EditRegion.Of(Doc.Strokes);
        if (!box.IsEmpty)
        {
            box = box.Inflate(24f * DpiScale);
            int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            var bgra = _windows[0].RenderStrokesToBgra(Doc.Strokes, box, w, h);
            if (bgra != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                var bytes = ExportFormats.Encode(outPath, bgra, w, h, 1, out _);
                if (bytes != null) File.WriteAllBytes(outPath, bytes);
                Console.WriteLine($"写出：{outPath}（{w}×{h}）");
            }
        }
        ExitCode = boxes > 0 && !HandOcrBusy ? 0 : 1;
    }
}
