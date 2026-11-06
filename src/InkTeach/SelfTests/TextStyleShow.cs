using System.IO;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：文字操作条 →「A」→ 字体/字号/行距三行面板；同时验证逐行双色和存盘。
    ///   --textstyle &lt;out.bmp&gt;
    /// </summary>
    private void TextStyleShow(string outPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 文本框：字体 / 行距 / 逐行双色 ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        ShowHud = false;

        InsertPlainTextBoxForTest("第一行：红笔板书 $A \\in B$\n第二行：蓝笔批注 $x^2$");
        var s = Doc.Selected.Count == 1 ? Doc.Selected[0] : null;
        var box = s?.TextBox;
        if (s == null || box == null)
        {
            Console.WriteLine("FAIL：没有文本框");
            ExitCode = 1;
            return;
        }

        var red = new Color4(0.86f, 0.16f, 0.12f, 1f);
        var blue = new Color4(0.12f, 0.30f, 0.80f, 1f);
        s.Color = red;
        box.Ink = red;
        box.LineColors.Clear();
        box.LineColors.Add(red);
        box.LineColors.Add(blue);
        box.Paper = true;
        box.InvalidateLayout();
        s.SyncTextBoxBounds();

        SetSelectedTextFaceFromUi(1);      // 楷体
        SetSelectedTextSpacingFromUi(1.8f);
        Tool = Tool.Marquee;
        SelPanelOpen = SelPanel.FontSize;
        SettleFrames(500);

        bool shot = ScreenProbe.SaveBmp(outPath, (int)_virtualX, (int)_virtualY,
                                        (int)_virtualW, (int)_virtualH);

        // 面板命中：字体第 2 格 / 行距第 4 格必须分别落到 FaceBase+1、SpacingBase+3。
        var sel = LiveSelectionFrame.CanvasAabb;
        var faceCell = SelectionHandles.FontPanelFaceRect(1, sel, UiUnit, FurnitureViewport);
        var gapCell = SelectionHandles.FontPanelSpacingRect(3, sel, UiUnit, FurnitureViewport);
        var facePart = SelectionHandles.PanelPartAt(
            (faceCell.MinX + faceCell.MaxX) * 0.5f, (faceCell.MinY + faceCell.MaxY) * 0.5f,
            sel, UiUnit, FurnitureViewport, SelPanel.FontSize, SelectionHandles.SwatchCount, true);
        var gapPart = SelectionHandles.PanelPartAt(
            (gapCell.MinX + gapCell.MaxX) * 0.5f, (gapCell.MinY + gapCell.MaxY) * 0.5f,
            sel, UiUnit, FurnitureViewport, SelPanel.FontSize, SelectionHandles.SwatchCount, true);
        bool hit = facePart == (SelectionHandles.PanelPart)((int)SelectionHandles.PanelPart.FaceBase + 1)
                   && gapPart == (SelectionHandles.PanelPart)((int)SelectionHandles.PanelPart.SpacingBase + 3);

        bool saved = false;
        try
        {
            var doc2 = new InkDocument();
            InkSerializer.LoadInto(doc2, InkSerializer.Save(Doc));
            foreach (var st in doc2.Strokes)
            {
                if (st.Kind != StrokeKind.TextBox || st.TextBox == null) continue;
                var b = st.TextBox;
                saved = b.Face == TextBoxFace.KaiTi
                        && MathF.Abs(b.LineSpacing - 1.8f) < 0.01f
                        && b.LineColors.Count == 2
                        && b.Paper;
            }
        }
        catch (System.Exception ex) { Console.WriteLine("存盘失败：" + ex.Message); }

        Console.WriteLine($"字体={TextBoxFaces.Name(box.Face)}，行距={box.LineSpacing:F2}，" +
                          $"逐行色={box.LineColors.Count}，面板命中={(hit ? "OK" : "FAIL")}，" +
                          $"存盘往返={(saved ? "OK" : "FAIL")}");
        Console.WriteLine($"写出：{outPath}");
        ExitCode = shot && saved && hit ? 0 : 1;
    }
}
