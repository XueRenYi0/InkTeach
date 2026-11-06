using System.IO;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：选中一个墨迹对象后，操作条末尾应该有彩色 AI 格；点开后是
    /// 「保留公式 / 含公式」两项。产出两张截图：
    ///   --selaishow &lt;out.bmp&gt;
    /// </summary>
    private void SelAiShow(string outPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 选中操作条 AI 格 ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        ShowHud = false;

        var s = new Stroke
        {
            Tool = Tool.Pen,
            Kind = StrokeKind.Freehand,
            Color = new Color4(0.86f, 0.16f, 0.12f, 1f),
            Width = 6f,
        };
        for (int i = 0; i <= 40; i++)
        {
            s.Points.Add(new InkPoint
            {
                X = 420 + i * 7f,
                Y = 300 + MathF.Sin(i * 0.18f) * 26f,
                P = 1f,
                T = i,
            });
        }
        s.InvalidateMetrics();
        s.RecomputeBounds();
        Doc.AppendStroke(s);
        Doc.Selected.Clear();
        Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        SettleFrames(500);

        string p1 = outPath;
        bool ok1 = ScreenProbe.SaveBmp(p1, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        SelAiMenuOpen = true;
        SettleFrames(500);
        string p2 = Path.ChangeExtension(outPath, null) + "-menu.bmp";
        bool ok2 = ScreenProbe.SaveBmp(p2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        Console.WriteLine($"操作条：{SelectionHandles.BarButtonCountFor(false)} 格，" +
                          $"AI 在最后一格；截图 {(ok1 ? "OK" : "FAIL")} / {(ok2 ? "OK" : "FAIL")}");
        Console.WriteLine($"写出：{p1}");
        Console.WriteLine($"写出：{p2}");
        ExitCode = ok1 && ok2 ? 0 : 1;
    }
}
