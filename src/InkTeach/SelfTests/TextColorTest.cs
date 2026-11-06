using System.IO;
using System.Linq;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：编辑中「选中一段改色 / 不选中改整框」，以及提交后的逐字色存盘。
    ///   --textcolor &lt;out.bmp&gt;
    /// </summary>
    private void TextColorShow(string outPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 编辑中改色：选中 / 整框 ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        ShowHud = false;

        var blue = new Color4(0.12f, 0.30f, 0.80f, 1f);
        var red = new Color4(0.86f, 0.16f, 0.12f, 1f);

        // 1) 选中第二行「第二行变蓝」→ 只改这几个字（跨行索引也要对）。
        InsertTextBoxFromUi();
        SetTextBoxEditTextForTest("红字蓝字\n第二行变蓝");
        SetTextBoxEditSelectionForTest(5, 10);
        BlurTextBoxEditForTest();
        SetTextColorFromUi(blue);
        bool editStayed = TextBoxEditing;
        SettleFrames(300);
        string editingPath = Path.ChangeExtension(outPath, null) + "-editing.bmp";
        bool shotEditing = ScreenProbe.SaveBmp(editingPath, (int)_virtualX, (int)_virtualY,
                                               (int)_virtualW, (int)_virtualH);
        Console.WriteLine($"编辑中截图：{editingPath}（{(shotEditing ? "OK" : "FAIL")}）");
        var box1 = Doc.Selected.Count == 1 ? Doc.Selected[0].TextBox : null;
        bool selectedOk = box1 != null
            && box1.ColorSpans.Any(s => s.Start == 5 && s.Length == 5
                                        && MathF.Abs(s.Color.B - blue.B) < 0.02f);
        EndTextBoxEditFromUi(true);
        bool commitOk = box1 != null && box1.Text == "红字蓝字\n第二行变蓝"
                        && box1.ColorSpans.Any(s => s.Start == 5 && s.Length == 5);
        bool undoOk = false;
        if (commitOk)
        {
            Host.Commands.Undo();
            SettleFrames(120);
            undoOk = box1.ColorSpans.Count == 0;
            Host.Commands.Redo();
            SettleFrames(120);
        }
        SettleFrames(200);
        string partialPath = Path.ChangeExtension(outPath, null) + "-partial.bmp";
        bool shotPartial = ScreenProbe.SaveBmp(partialPath, (int)_virtualX, (int)_virtualY,
                                               (int)_virtualW, (int)_virtualH);
        Console.WriteLine($"部分改色截图：{partialPath}（{(shotPartial ? "OK" : "FAIL")}）");

        // 2) 不选中 → 整框变红，逐字色清空。
        InsertTextBoxFromUi();
        SetTextBoxEditTextForTest("整体变红");
        SetTextBoxEditSelectionForTest(0, 0);
        SetTextColorFromUi(red);
        var box2 = Doc.Selected.Count == 1 ? Doc.Selected[0].TextBox : null;
        bool wholeOk = box2 != null && box2.ColorSpans.Count == 0
                       && box2.LineColors.Count == 0
                       && MathF.Abs(box2.Ink.R - red.R) < 0.02f;
        EndTextBoxEditFromUi(true);

        bool saved = false;
        try
        {
            var doc2 = new InkDocument();
            InkSerializer.LoadInto(doc2, InkSerializer.Save(Doc));
            foreach (var s in doc2.Strokes)
            {
                if (s.Kind != StrokeKind.TextBox || s.TextBox?.Text != "红字蓝字\n第二行变蓝") continue;
                saved = s.TextBox.ColorSpans.Any(x => x.Start == 5 && x.Length == 5);
            }
        }
        catch (System.Exception ex) { Console.WriteLine("存盘失败：" + ex.Message); }

        SettleFrames(300);
        bool shot = ScreenProbe.SaveBmp(outPath, (int)_virtualX, (int)_virtualY,
                                        (int)_virtualW, (int)_virtualH);

        Console.WriteLine($"选中改色={selectedOk}，提交={commitOk}，撤销={undoOk}，" +
                          $"失焦不退出={editStayed}，整框改色={wholeOk}，存盘={saved}");
        Console.WriteLine($"写出：{outPath}");
        ExitCode = selectedOk && commitOk && undoOk && editStayed && wholeOk && saved && shot ? 0 : 1;
    }
}
