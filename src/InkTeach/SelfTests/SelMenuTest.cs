using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：选中条弹层（方案 A）。
    /// 只验“AI / `⋯` 两个入口、一个弹层”：互斥、行数、几何、菜单行可点、清空选中自动关。
    /// </summary>
    private void SelMenuTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 选中条弹层（AI / ⋯） ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        ShowHud = false;

        // 先放一条背景墨迹：验证“置顶”真的把它换到最上。
        var bg = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.20f, 0.45f, 0.85f, 1f), Width = 6f,
        };
        for (int i = 0; i <= 20; i++)
            bg.Points.Add(new InkPoint { X = 340 + i * 6f, Y = 380, P = 1f, T = i });
        bg.InvalidateMetrics();
        bg.RecomputeBounds();

        var s = new Stroke
        {
            Tool = Tool.Pen,
            Kind = StrokeKind.Freehand,
            Color = new Color4(0.86f, 0.16f, 0.12f, 1f),
            Width = 6f,
        };
        for (int i = 0; i <= 30; i++)
            s.Points.Add(new InkPoint { X = 420 + i * 7f, Y = 320 + MathF.Sin(i * 0.2f) * 20f, P = 1f, T = i });
        s.InvalidateMetrics();
        s.RecomputeBounds();
        Doc.AppendStroke(s);
        Doc.AppendStroke(bg);   // 背景放在 s 之后：置顶测试要把 s 从 0 移到 1
        Doc.Selected.Clear();
        Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        SettleFrames(400);

        OpenSelectionMenuForTest(ai: true);
        SettleFrames(120);
        Check("AI 菜单：打开、1 行、有矩形",
              SelAiMenuOpen && !SelMoreMenuOpen && SelectionMenuRowsNow == 1
              && !SelectionMenuRectNow().IsEmpty,
              $"ai={SelAiMenuOpen} more={SelMoreMenuOpen} rows={SelectionMenuRowsNow}");

        OpenSelectionMenuForTest(ai: false);
        SettleFrames(120);
        Check("⋯ 打开：AI 自动关、7 行（置顶/置底直接两行）",
              SelMoreMenuOpen && !SelAiMenuOpen && SelectionMenuRowsNow == 7,
              $"ai={SelAiMenuOpen} more={SelMoreMenuOpen} rows={SelectionMenuRowsNow}");

        var order = SelectionHandles.MoreBarOrder(false);
        bool covers = System.Array.IndexOf(order, SelBarButton.Top) >= 0
                   && System.Array.IndexOf(order, SelBarButton.Bottom) >= 0
                   && System.Array.IndexOf(order, SelBarButton.Export) >= 0
                   && System.Array.IndexOf(order, SelBarButton.FlipH) >= 0
                   && System.Array.IndexOf(order, SelBarButton.FlipV) >= 0
                   && System.Array.IndexOf(order, SelBarButton.Lock) >= 0
                   && System.Array.IndexOf(order, SelBarButton.Library) >= 0;
        Check("⋯ 覆盖 7 个收纳动作（无二级弹窗）", covers,
              string.Join(",", order));

        var mr = SelectionMenuRectNow();
        var vp = FurnitureViewport;
        Check("弹层完整在可见工作区",
              mr.MinX >= vp.MinX - 1f && mr.MaxX <= vp.MaxX + 1f
              && mr.MinY >= vp.MinY - 1f && mr.MaxY <= vp.MaxY + 1f,
              $"menu {mr.MinX:F0},{mr.MinY:F0}-{mr.MaxX:F0},{mr.MaxY:F0}；" +
              $"vp {vp.MinX:F0},{vp.MinY:F0}-{vp.MaxX:F0},{vp.MaxY:F0}");

        // 点第一行「置顶」：直接执行，菜单关；不再开二级“层级”弹窗。
        int idxBefore = Doc.Strokes.IndexOf(s);
        var row0 = SelectionHandles.BarMenuRowRectIn(mr, 0, UiUnit);
        bool down = SelectionGestureForTest((row0.MinX + row0.MaxX) * 0.5f,
                                            (row0.MinY + row0.MaxY) * 0.5f);
        EndSelectionGestureForTest();
        SettleFrames(160);
        Check("点 ⋯ 第一行：置顶执行、菜单关、无二级弹窗",
              down && !SelMoreMenuOpen && SelPanelOpen == SelPanel.None
              && Doc.Strokes.IndexOf(s) == Doc.Strokes.Count - 1,
              $"down={down} more={SelMoreMenuOpen} panel={SelPanelOpen} " +
              $"idx {idxBefore}→{Doc.Strokes.IndexOf(s)}");

        var tOrder = SelectionHandles.MoreBarOrder(true);
        Check("文本框 ⋯：置顶/置底/锁定",
              tOrder.Length == 3
              && System.Array.IndexOf(tOrder, SelBarButton.Top) >= 0
              && System.Array.IndexOf(tOrder, SelBarButton.Bottom) >= 0
              && System.Array.IndexOf(tOrder, SelBarButton.Lock) >= 0,
              string.Join(",", tOrder));

        OpenSelectionMenuForTest(ai: true);
        SettleFrames(60);
        Doc.Selected.Clear();
        SettleFrames(160);
        Check("清空选中：弹层自动关",
              !SelAiMenuOpen && !SelMoreMenuOpen,
              $"ai={SelAiMenuOpen} more={SelMoreMenuOpen}");

        Console.WriteLine($"合计：{pass} 通过 / {fail} 失败");
        ExitCode = fail == 0 ? 0 : 1;
    }
}
