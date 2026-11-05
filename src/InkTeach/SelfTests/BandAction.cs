using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 「选择」上带右端三连（全选 / 复制 / 粘贴）的几何与提示（2026-11-03）。
    /// 只验布局、命中、文案；剪贴板读写本身由 `--clipboardtest` 覆盖。
    /// </summary>
    private void BandActionTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 上带动作按钮：全选 / 复制 / 粘贴 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {(ok ? "PASS" : "FAIL")}  {name,-30} {detail}");
        }

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        if (CurrentUi is not InkUi.FullUi ui)
        {
            Console.WriteLine("    UI 没建起来，跳过");
            ExitCode = 1;
            _quit = true;
            return;
        }

        Host.Commands.SetTool(Tool.Marquee);
        ui.SelectBandCellForTest(8);
        ui.OpenRailForTest();
        SettleFrames(80);

        int n = ui.ActionCountForTest;
        Check("选择带：三个动作", n == 3, $"动作数 {n}");

        var a0 = ui.ActionRectAtForTest(0);
        var a1 = ui.ActionRectAtForTest(1);
        var a2 = ui.ActionRectAtForTest(2);
        bool ordered = a0.MaxX - a0.MinX > 1f && a1.MaxX - a1.MinX > 1f
                    && a2.MaxX - a2.MinX > 1f
                    && a1.MinX >= a0.MaxX - 0.01f && a2.MinX >= a1.MaxX - 0.01f;
        Check("从左到右、不重叠", ordered,
              $"{a0.MinX:F0}..{a0.MaxX:F0} / {a1.MinX:F0}..{a1.MaxX:F0} / {a2.MinX:F0}..{a2.MaxX:F0}");

        var band = ui.BandRectForTest;
        bool inside = !band.IsEmpty && a0.MinX >= band.MinX - 0.5f && a2.MaxX <= band.MaxX + 0.5f;
        Check("三连都在带子里", inside,
              $"带 {band.MinX:F0}..{band.MaxX:F0}，三连 {a0.MinX:F0}..{a2.MaxX:F0}");

        float w0 = a0.MaxX - a0.MinX, w1 = a1.MaxX - a1.MinX, w2 = a2.MaxX - a2.MinX;
        Check("每个按钮够宽（≥58）", w0 >= 58f && w1 >= 58f && w2 >= 58f,
              $"{w0:F0} / {w1:F0} / {w2:F0}");

        int h0 = ui.HoverAtForTest((a0.MinX + a0.MaxX) * 0.5f, (a0.MinY + a0.MaxY) * 0.5f);
        int h1 = ui.HoverAtForTest((a1.MinX + a1.MaxX) * 0.5f, (a1.MinY + a1.MaxY) * 0.5f);
        int h2 = ui.HoverAtForTest((a2.MinX + a2.MaxX) * 0.5f, (a2.MinY + a2.MaxY) * 0.5f);
        Check("悬停命中：400 / 401 / 402", h0 == 400 && h1 == 401 && h2 == 402,
              $"{h0} / {h1} / {h2}");

        Check("复制、粘贴都有提示",
              ui.TipContentForTest(401).Title == "复制"
              && ui.TipContentForTest(402).Title == "粘贴",
              $"{ui.TipContentForTest(401).Title} / {ui.TipContentForTest(402).Title}");

        Host.Commands.SetTool(Tool.Eraser);
        ui.SelectBandCellForTest(6);
        SettleFrames(40);
        var clear = ui.ActionRectForTest;
        Check("橡皮带仍是一个「清空」",
              ui.ActionCountForTest == 1 && clear.MaxX - clear.MinX > 40f,
              $"动作数 {ui.ActionCountForTest}，宽 {clear.MaxX - clear.MinX:F0}");

        Console.WriteLine($"上带动作按钮：通过 {pass}，失败 {fail}");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
