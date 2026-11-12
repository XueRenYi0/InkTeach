using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：「点箭头清屏」只在**桌面批注**生效（2026-11-12 用户定）。
    ///   --arrowcleartest
    /// </summary>
    private void ArrowClearTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 点箭头清屏 ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(160);
        PassThrough = false;
        ShowHud = false;

        void Check(string name, bool ok, string detail)
            => Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");

        Tool = Tool.Pen;
        SetBoardFromUi(false);
        SetPptModeForTest(false);
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(80);

        Stroke AddInk(float y)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Kind = StrokeKind.Freehand,
                Color = new Color4(0.86f, 0.16f, 0.12f, 1f),
                Width = 6f,
            };
            for (int i = 0; i <= 20; i++)
                s.Points.Add(new InkPoint { X = 300 + i * 8f, Y = y, P = 1f, T = i });
            s.InvalidateMetrics();
            s.RecomputeBounds();
            Doc.AppendStroke(s);
            return s;
        }

        // 1) 桌面 + 设置开 → 清空，一步撤销
        AddInk(300f);
        Host.Commands.SetClearOnArrow(true);
        SettleFrames(60);
        Check("桌面：设置开 → 生效",
              Host.State.ClearOnArrow && Host.State.ClearOnArrowActive,
              $"on={Host.State.ClearOnArrow} active={Host.State.ClearOnArrowActive}");
        ScreenProbe.SaveBmp("reports/arrow-clear-badge.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        int before = Doc.Strokes.Count;
        Host.Commands.ClearDesktopOnArrow();
        SettleFrames(80);
        Check("桌面：点箭头 → 清空本屏", Doc.Strokes.Count == 0, $"{before} → {Doc.Strokes.Count}");
        Doc.Undo();
        SettleFrames(80);
        Check("清空可一步撤销", Doc.Strokes.Count == before, $"{Doc.Strokes.Count} / 期望 {before}");

        // 2) 白板 → 不清
        SetBoardFromUi(true);
        SettleFrames(80);
        AddInk(320f);
        SettleFrames(60);
        int boardCount = Doc.Strokes.Count;
        Check("白板：不生效", !Host.State.ClearOnArrowActive,
              $"active={Host.State.ClearOnArrowActive}");
        Host.Commands.ClearDesktopOnArrow();
        SettleFrames(80);
        Check("白板：点箭头不清", Doc.Strokes.Count == boardCount,
              $"{Doc.Strokes.Count} / 期望 {boardCount}");
        SetBoardFromUi(false);

        // 3) PPT 放映 → 不清
        SetPptModeForTest(true);
        SettleFrames(80);
        Check("PPT：不生效", !Host.State.ClearOnArrowActive,
              $"active={Host.State.ClearOnArrowActive}");
        SetPptModeForTest(false);

        // 收尾：设置关掉
        Host.Commands.SetClearOnArrow(false);
        SettleFrames(60);
        Check("关掉设置后不生效", !Host.State.ClearOnArrowActive,
              $"active={Host.State.ClearOnArrowActive}");
        Console.WriteLine("说明：只有桌面批注会清；白板 / PPT / 文档都不动。");
    }
}
