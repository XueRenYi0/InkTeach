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

        OpenSelectionMenuForTest(ai: true);
        SettleFrames(500);
        string p2 = Path.ChangeExtension(outPath, null) + "-menu.bmp";
        bool ok2 = ScreenProbe.SaveBmp(p2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // 方案 A：点 `⋯` 应自动关掉 AI，只留下一个弹层。
        OpenSelectionMenuForTest(ai: false);
        SettleFrames(500);
        string p3 = Path.ChangeExtension(outPath, null) + "-more.bmp";
        bool ok3 = ScreenProbe.SaveBmp(p3, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // 贴底：菜单下方放不下，应翻到**选中对象上方**，不能盖住对象。
        Doc.Clear();
        var low = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.86f, 0.16f, 0.12f, 1f), Width = 6f,
        };
        float lowY = _virtualY + _virtualH - 220f;
        for (int i = 0; i <= 30; i++)
            low.Points.Add(new InkPoint { X = 420 + i * 7f, Y = lowY, P = 1f, T = i });
        low.InvalidateMetrics();
        low.RecomputeBounds();
        Doc.AppendStroke(low);
        Doc.Selected.Clear();
        Doc.Selected.Add(low);
        Tool = Tool.Marquee;
        SettleFrames(400);
        OpenSelectionMenuForTest(ai: false);
        SettleFrames(400);
        string p4 = Path.ChangeExtension(outPath, null) + "-low.bmp";
        bool ok4 = ScreenProbe.SaveBmp(p4, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // 图形条（5 格：收起/颜色/复制/删除/⋯）
        Doc.Clear();
        var shape = MakeCoordinateForTest(420f, 300f, 240f, 180f, grid: false);
        Doc.AppendStroke(shape);
        Doc.Selected.Clear();
        Doc.Selected.Add(shape);
        Tool = Tool.Marquee;
        SettleFrames(400);
        string p5 = Path.ChangeExtension(outPath, null) + "-shape.bmp";
        bool ok5 = ScreenProbe.SaveBmp(p5, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // 图片条（5 格：收起/保存/复制/删除/⋯）
        Doc.Clear();
        var picture = Doc.AddImage(MakeTestImage(96, 72), 420f, 300f, 1f);
        Doc.Selected.Clear();
        Doc.Selected.Add(picture);
        SettleFrames(400);
        string p6 = Path.ChangeExtension(outPath, null) + "-image.bmp";
        bool ok6 = ScreenProbe.SaveBmp(p6, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        Console.WriteLine($"操作条：{SelectionHandles.BarButtonCountFor(false)} 格，" +
                          $"AI 倒数第二、⋯ 最后；截图 {(ok1 ? "OK" : "FAIL")} / " +
                          $"{(ok2 ? "OK" : "FAIL")} / {(ok3 ? "OK" : "FAIL")} / " +
                          $"{(ok4 ? "OK" : "FAIL")} / {(ok5 ? "OK" : "FAIL")} / " +
                          $"{(ok6 ? "OK" : "FAIL")}");
        Console.WriteLine($"写出：{p1}");
        Console.WriteLine($"写出：{p2}");
        Console.WriteLine($"写出：{p3}");
        Console.WriteLine($"写出：{p4}");
        Console.WriteLine($"写出：{p5}");
        Console.WriteLine($"写出：{p6}");
        ExitCode = ok1 && ok2 && ok3 && ok4 && ok5 && ok6 ? 0 : 1;
    }
}
