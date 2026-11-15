using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：**题卡选中后 → AI 菜单「识别作图」**（用户 2026-11-11 反馈“识别成题卡后再点 AI 识图不行”）。
    /// 走真实点击路径（弹层第一行 + 真模型），不 mock；顺便验证「画圆不能画成椭圆」。
    ///   --plotfromcard
    /// </summary>
    private void PlotFromCardTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 题卡 → AI 识别作图（真实链路） ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(160);
        PassThrough = false;
        BoardOn = true;
        ShowHud = false;

        var cfg = OcrClient.LoadConfig();
        if (!cfg.IsConfigured)
        {
            Console.WriteLine("AI 未配置，跳过");
            ExitCode = 1;
            return;
        }

        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;

        var img = InsertPaperImageForTest(MakePaperImageForTest());
        var card = InsertImageCardForTest(img, new List<AiQuestionClient.CardItem>
        {
            new()
            {
                Stem = "已知圆 $C: x^2+y^2=4$，求圆心和半径，并画出这个圆。",
                Options = { },
                NeedReview = false,
            },
        });
        SettleFrames(200);

        var tb = card?.TextBox;
        string prompt = PlotPromptFromTextBox(tb);
        Console.WriteLine($"卡：kind={card?.Kind} role={tb?.Role} questions={tb?.Questions.Count}");
        Console.WriteLine($"提示词：{ClipForTest(prompt, 70)}");
        Console.WriteLine($"菜单：kind={AiMenuKindNow} rows={AiMenuRows} selected={Doc.Selected.Count}");
        if (prompt.Length == 0)
            Console.WriteLine("!! 题卡提取不到题干 —— 这就是“点识别作图没反应”的原因");

        // 打开 AI 菜单，点第一行（和用户完全同一条路）。
        OpenSelectionMenuForTest(ai: true);
        SettleFrames(140);
        var menu = SelectionMenuRectNow();
        var row0 = SelectionHandles.BarMenuRowRectIn(menu, 0, UiUnit);
        Console.WriteLine($"菜单矩形：{menu.MinX:F0},{menu.MinY:F0}-{menu.MaxX:F0},{menu.MaxY:F0} " +
                          $"第一行：{row0.MinY:F0}-{row0.MaxY:F0}");
        int before = Doc.Strokes.Count;
        bool down = SelectionGestureForTest((row0.MinX + row0.MaxX) * 0.5f,
                                            (row0.MinY + row0.MaxY) * 0.5f);
        EndSelectionGestureForTest();
        SettleFrames(200);
        Console.WriteLine($"点击：down={down} aiMenuOpen={SelAiMenuOpen} toast={ToastText}");

        for (int i = 0; i < 300 && AiPlotBusyNow; i++) SettleFrames(200);
        SettleFrames(400);

        Console.WriteLine($"结果：busy={AiPlotBusyNow} strokes {before}→{Doc.Strokes.Count} " +
                          $"toast={ToastText} status={HandOcrStatus}");
        var added = Doc.Strokes.Count > before ? Doc.Strokes[^1] : null;
        Console.WriteLine($"新对象：kind={added?.Kind} figure={added?.Figure?.Kind}");
        if (added?.Figure?.Conic is { } conic)
            Console.WriteLine($"圆锥：type={conic.Type} a={conic.A:F2} b={conic.B:F2} eq={conic.Equation}");
        ScreenProbe.SaveBmp("reports/plotfromcard.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 单独验证：一句话“画圆”必须 a=b（不受题卡影响）。
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(120);
        Console.WriteLine();
        Console.WriteLine("=== 画圆 → 正圆 ===");
        int n0 = Doc.Strokes.Count;
        InsertAiPlotFromUi("画圆 x^2+y^2=4 的图象。");
        for (int i = 0; i < 300 && AiPlotBusyNow; i++) SettleFrames(200);
        SettleFrames(300);
        var circle = Doc.Strokes.Count > n0 ? Doc.Strokes[^1] : null;
        var cc = circle?.Figure?.Conic;
        Console.WriteLine($"结果：kind={circle?.Kind} toast={ToastText} " +
                          $"a={cc?.A:F2} b={cc?.B:F2} 正圆={(cc != null && MathF.Abs(cc.A - cc.B) < 0.01f)}");
        ScreenProbe.SaveBmp("reports/plotfromcard-circle.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // ---- 归一化（离线、可重复）：模型给 a≠b 也要拉成正圆 -------------
        Console.WriteLine();
        Console.WriteLine("=== 圆归一化（离线） ===");
        PlotSpec Make(float a, float b, string eq, string title = "")
            => new()
            {
                Kind = "conic",
                Title = title,
                Conic = new PlotConic { Type = "ellipse", A = a, B = b, Equation = eq },
            };
        var c1 = Make(3.2f, 2.4f, "x^2+y^2=4");
        AiPlotClient.NormalizeCircle(c1, "画一个圆");
        var c2 = Make(2f, 2f, "(x-1)^2+(y+2)^2=9");
        AiPlotClient.NormalizeCircle(c2, "画图");
        var e1 = Make(3f, 2f, "x^2/9+y^2/4=1", "椭圆");
        AiPlotClient.NormalizeCircle(e1, "画椭圆 x^2/9+y^2/4=1");
        Console.WriteLine($"a≠b 的圆：{c1.Conic.A:F2}/{c1.Conic.B:F2} 正圆={MathF.Abs(c1.Conic.A - c1.Conic.B) < 0.001f}");
        Console.WriteLine($"标准圆方程：{c2.Conic.A:F2}/{c2.Conic.B:F2} 正圆={MathF.Abs(c2.Conic.A - c2.Conic.B) < 0.001f}");
        Console.WriteLine($"椭圆不动：{e1.Conic.A:F2}/{e1.Conic.B:F2} 保持={MathF.Abs(e1.Conic.A - 3f) < 0.001f && MathF.Abs(e1.Conic.B - 2f) < 0.001f}");
        Console.WriteLine("出图：reports/plotfromcard.bmp / reports/plotfromcard-circle.bmp");
    }

    /// <summary>
    /// 自检：**选中一张图片 → AI 菜单「识别作图」**（真实模型）。
    /// 用来测“从卷面里裁一小块图（函数图/几何图）让 AI 反推作图”。
    ///   --plotlive &lt;图片路径&gt;
    /// </summary>
    private void PlotLiveTest(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 图片 → AI 识别作图（真实 API） ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(160);
        PassThrough = false;
        BoardOn = true;
        ShowHud = false;

        string file = path;
        if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file))
        {
            Console.WriteLine($"找不到图片：{file}");
            ExitCode = 1;
            return;
        }
        var img = LoadPaperImageForTest(file);
        if (img == null)
        {
            Console.WriteLine("图片读不进来");
            ExitCode = 1;
            return;
        }
        Console.WriteLine($"输入：{file}（{img.Width}×{img.Height}）");

        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        var shot = InsertPaperImageForTest(img);
        int before = Doc.Strokes.Count;
        InsertAiPlotFromImageFromUi(shot);
        for (int i = 0; i < 600 && AiPlotBusyNow; i++) SettleFrames(200);
        SettleFrames(400);

        var added = Doc.Strokes.Count > before ? Doc.Strokes[^1] : null;
        Console.WriteLine($"结果：kind={added?.Kind} toast={ToastText} status={InkStatus}");
        if (added?.Figure != null)
        {
            var f = added.Figure;
            Console.WriteLine($"矢量：kind={f.Kind} title={f.Title} " +
                              $"series={f.Series.Count} points={f.Figure?.Points2D.Count + f.Figure?.Points3D.Count ?? 0}");
            if (f.Conic != null)
                Console.WriteLine($"圆锥：type={f.Conic.Type} a={f.Conic.A:F2} b={f.Conic.B:F2} eq={f.Conic.Equation}");
        }
        ScreenProbe.SaveBmp("reports/plotlive.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/plotlive.bmp");
    }
}
