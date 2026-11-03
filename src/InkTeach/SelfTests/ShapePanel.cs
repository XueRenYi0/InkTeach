// 图形面板专项（--shapepaneltest）
//
// 只测这次改动的一条线：
//   图形格 → 图形页；选图形卡 → 工具变了但面板不关；
//   收藏格 → 同一面板切收藏页；两格蓝色互斥；
//   再点当前入口 → 关面板，两格都不蓝。
using InkEngine;
using System.Numerics;

namespace InkTeach;

internal sealed partial class App
{
    private void ShapePanelTest()
    {
        if (SkipIfNoSyntheticInput("图形面板专项"))
        {
            Console.WriteLine("  改用直连布局/命中测试（不依赖合成鼠标）");
            ShapePanelDirectTest();
            return;
        }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(300);

        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine("  界面没挂上，停止");
            ExitCode = 1;
            return;
        }

        void ClickPhysical(float x, float y)
        {
            SendMouse((int)x, (int)y, 0); SettleFrames(70);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP); SettleFrames(320);
        }
        void ClickCell(int cell)
        {
            var r = ui.CellRectForTest(cell);
            ClickPhysical((r.MinX + r.MaxX) * 0.5f * DpiScale,
                          (r.MinY + r.MaxY) * 0.5f * DpiScale);
        }
        void ClickCanvasRect(RectF r)
            => ClickPhysical((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);

        // 1. 图形格 → 图形页
        ClickCell(10);
        Check("图形格 → 面板图形页", LibraryPanelOpen && LibraryTab == LibraryTab.Shapes,
              $"open={LibraryPanelOpen} tab={LibraryTab}");
        Check("图形蓝、收藏不蓝",
              ui.CellActiveForTest(10) && !ui.CellActiveForTest(11),
              $"图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");

        // 2. 第一张卡 = 直线；面板常驻
        var m = LibraryPanelInfoNow();
        ClickCanvasRect(LibraryLayout.CellRect(m, 0, 0f));
        Check("点第一张图形卡 = 选直线，面板不关",
              Tool == Tool.Line && LibraryPanelOpen && LibraryTab == LibraryTab.Shapes,
              $"tool={Tool} open={LibraryPanelOpen} tab={LibraryTab}");
        Check("选图形后：旧图形上带不再出现", ui.BandCellForTest != 10,
              $"bandCell={ui.BandCellForTest}");

        // 2a. 38 张档位卡 + 分类；图形卡 72×82，固定 5 行高
        Check("档位展开后共 38 张卡",
              ShapeCatalog.All.Length == 38 && ShapeCatalog.CountFor("all") == 38,
              $"all={ShapeCatalog.CountFor("all")}");
        Check("分类数量：全部38 / 常用12 / 曲线10 / 立体16",
              ShapeCatalog.CountFor("common") == 12 && ShapeCatalog.CountFor("curve") == 10
              && ShapeCatalog.CountFor("solid") == 16,
              $"常用={ShapeCatalog.CountFor("common")} 曲线={ShapeCatalog.CountFor("curve")} 立体={ShapeCatalog.CountFor("solid")}");
        Check("图形页默认「全部」、9 列 × 5 行、卡片 72×82",
              ShapeCategoryId == "all" && m.Cols == 9 && m.Rows == 5
              && MathF.Abs(m.CellW - LibraryLayout.ShapeCellWLogical * UiUnit) < 0.5f
              && MathF.Abs(m.CellH - LibraryLayout.ShapeCellHLogical * UiUnit) < 0.5f,
              $"cat={ShapeCategoryId} cols={m.Cols} rows={m.Rows} cell={m.CellW / UiUnit:F0}×{m.CellH / UiUnit:F0}");
        float shapesPageH = LibraryPanelRectNow().MaxY - LibraryPanelRectNow().MinY;

        // 卡片缝里也能拖面板（和收藏页同一条逻辑）。
        {
            var card0 = LibraryLayout.CellRect(m, 0, 0f);
            float gapX = card0.MaxX + LibraryLayout.CellGapLogical * UiUnit * 0.5f;
            float gapY = (card0.MinY + card0.MaxY) * 0.5f;
            var beforeDrag = LibraryPanelRectNow();
            LibraryPointerDown(gapX, gapY, Native.PT_MOUSE, 1);
            LibraryPanelDragMove(gapX + 80f * UiUnit, gapY + 40f * UiUnit);
            LibraryPanelDragEnd();
            var afterDrag = LibraryPanelRectNow();
            Check("卡片缝里拖一下：面板跟着走",
                  MathF.Abs(afterDrag.MinX - beforeDrag.MinX) > 20f
                  || MathF.Abs(afterDrag.MinY - beforeDrag.MinY) > 20f,
                  $"x={beforeDrag.MinX:F0}→{afterDrag.MinX:F0} y={beforeDrag.MinY:F0}→{afterDrag.MinY:F0}");
        }

        void ClickShapeTab(string id)
        {
            var mm = LibraryPanelInfoNow();
            var cats = ShapeCatalog.Categories;
            var widths = new float[cats.Length];
            for (int i = 0; i < cats.Length; i++)
                widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * UiUnit;
            int idx = Array.FindIndex(cats, c => c.Id == id);
            ClickCanvasRect(LibraryLayout.TabRect(mm, widths, idx, UiUnit, LibraryTabScroll));
        }

        ClickShapeTab("common");
        Check("点「常用」→ 12 张、面板不变高",
              ShapeCategoryId == "common" && ShapeCardsNow().Length == 12
              && MathF.Abs((LibraryPanelRectNow().MaxY - LibraryPanelRectNow().MinY) - shapesPageH) < 1f,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length} h={(LibraryPanelRectNow().MaxY - LibraryPanelRectNow().MinY):F0}");
        ClickShapeTab("curve");
        Check("点「曲线」→ 10 张", ShapeCategoryId == "curve" && ShapeCardsNow().Length == 10,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length}");
        ClickShapeTab("solid");
        Check("点「立体」→ 16 张", ShapeCategoryId == "solid" && ShapeCardsNow().Length == 16,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length}");
        ClickShapeTab("all");
        Check("点「全部」→ 38 张、仍是 5 行高",
              ShapeCategoryId == "all" && ShapeCardsNow().Length == 38
              && LibraryPanelInfoNow().Rows == 5,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length} rows={LibraryPanelInfoNow().Rows}");

        // 2a2. 点卡直接选：第 6 张 = 梯形
        var cards = ShapeCardsNow();
        m = LibraryPanelInfoNow();
        ClickCanvasRect(LibraryLayout.CellRect(m, 5, 0f));
        Check("点「梯形」卡 = 选梯形",
              Tool == Tool.Trapezoid && ShapePresetActive(cards[5]),
              $"tool={Tool} active={ShapePresetActive(cards[5])}");
        Check("梯形卡高亮、直线卡不高亮",
              ShapePresetActive(cards[5]) && !ShapePresetActive(cards[0]),
              $"直线={ShapePresetActive(cards[0])} 梯形={ShapePresetActive(cards[5])}");
        Check("面板选图形后标记为一次性", ShapeOneShotForTest,
              $"oneShot={ShapeOneShotForTest} tool={Tool}");
        int strokesBefore = Doc.Strokes.Count;
        ShapeDragForTest(200f, 200f, 520f, 360f);
        Check("画完一个图形 → 默认选中、工具切框选、面板不关",
              Tool == Tool.Marquee && !ShapeOneShotForTest && LibraryPanelOpen
              && Doc.Selected.Count == 1 && Doc.Strokes.Count == strokesBefore + 1,
              $"tool={Tool} oneShot={ShapeOneShotForTest} open={LibraryPanelOpen} sel={Doc.Selected.Count} strokes={Doc.Strokes.Count - strokesBefore}");
        Check("画完图形已挂上“点空白回上一个工具”",
              RestoreToolAfterShapeSelectionForTest,
              $"restore={RestoreToolAfterShapeSelectionForTest}");
        ClickBlankAfterShapeForTest();
        Check("点空白 → 回到上一个工具、取消选中",
              Tool == Tool.Pen && Doc.Selected.Count == 0 && !RestoreToolAfterShapeSelectionForTest,
              $"tool={Tool} sel={Doc.Selected.Count} restore={RestoreToolAfterShapeSelectionForTest}");

        // 梯形上底角点：拖过另一个点后自动交换左右身份，仍是梯形、不交叉。
        var trap = Doc.Strokes[^1];
        float oldTL = trap.Points[3].X, oldTR = trap.Points[2].X;
        VertexDragForTest(trap, ShapeHandle.Vertex3, oldTL, trap.Points[3].Y,
                          oldTR + 60f, trap.Points[3].Y);
        Check("梯形上底点拖过另一个点：自动交换左右、仍是梯形",
              trap.Points[3].X < trap.Points[2].X
              && MathF.Abs(trap.Points[2].Y - trap.Points[3].Y) < 0.5f
              && MathF.Abs(trap.Points[0].Y - trap.Points[1].Y) < 0.5f,
              $"上底左/右 {trap.Points[3].X:F0} / {trap.Points[2].X:F0}");

        // 悬停：名字不常驻，指针停在哪张卡就显示哪张。
        m = LibraryPanelInfoNow();
        var hoverCell = LibraryLayout.CellRect(m, 5, 0f);
        LastPointerType = Native.PT_MOUSE;
        LibraryPanelPointerMove((hoverCell.MinX + hoverCell.MaxX) * 0.5f,
                                (hoverCell.MinY + hoverCell.MaxY) * 0.5f, 1);
        Check("悬停卡片：名称索引跟上", ShapeHoverIndex == 5,
              $"hover={ShapeHoverIndex}");
        LibraryPanelPointerMove(m.Panel.MinX - 30f * UiUnit,
                                (hoverCell.MinY + hoverCell.MaxY) * 0.5f, 1);
        Check("移出卡片：悬停清空", ShapeHoverIndex == -1,
              $"hover={ShapeHoverIndex}");

        // 五角星 / 扇形各画一个，确认新工具真的能落笔。
        m = LibraryPanelInfoNow();
        ClickCanvasRect(LibraryLayout.CellRect(m, 8, 0f));
        ShapeDragForTest(300f, 260f, 470f, 390f);
        Check("五角星能画出来", Doc.Strokes.Count > 0 && Doc.Strokes[^1].Kind == StrokeKind.Star,
              $"kind={(Doc.Strokes.Count > 0 ? Doc.Strokes[^1].Kind.ToString() : "无")}");
        m = LibraryPanelInfoNow();
        ClickCanvasRect(LibraryLayout.CellRect(m, 9, 0f));
        ShapeDragForTest(600f, 260f, 760f, 390f);
        Check("扇形能画出来", Doc.Strokes.Count > 0 && Doc.Strokes[^1].Kind == StrokeKind.Sector,
              $"kind={(Doc.Strokes.Count > 0 ? Doc.Strokes[^1].Kind.ToString() : "无")}");
        {
            var sec = Doc.Strokes[^1];
            float r0 = sec.SectorRadiusLocal();
            float a0 = sec.SectorStartAngleLocal();
            var c = new Vector2(sec.Points[0].X, sec.Points[0].Y);
            var end = new Vector2(c.X + MathF.Cos(a0 + MathF.PI) * r0,
                                  c.Y + MathF.Sin(a0 + MathF.PI) * r0);
            var oldEnd = new Vector2(sec.Points[2].X, sec.Points[2].Y);
            VertexDragForTest(sec, ShapeHandle.FocusPoint, oldEnd.X, oldEnd.Y, end.X, end.Y);
            float sweep = sec.SectorSweepDegrees();
            Check("扇形圆弧端点：改角度、半径不变",
                  MathF.Abs(sweep - 180f) < 2f && MathF.Abs(sec.SectorRadiusLocal() - r0) < 0.5f,
                  $"角度={sweep:F0}° 半径={sec.SectorRadiusLocal():F0}（原 {r0:F0}）");
        }

        // 2b. 面板里的页签也能切；主条蓝色同步
        var beforeTab = LibraryPanelRectNow();
        var m2 = LibraryPanelInfoNow();
        ClickCanvasRect(m2.FavoritesTab);
        var afterFav = LibraryPanelRectNow();
        Check("面板点「我的收藏」→ 切页、主条收藏蓝",
              LibraryTab == LibraryTab.Favorites
              && ui.CellActiveForTest(11) && !ui.CellActiveForTest(10),
              $"tab={LibraryTab} 图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");
        Check("切收藏：位置/宽度不变，只换高度",
              MathF.Abs(afterFav.MinX - beforeTab.MinX) < 0.5f
              && MathF.Abs((afterFav.MaxX - afterFav.MinX) - (beforeTab.MaxX - beforeTab.MinX)) < 0.5f
              && MathF.Abs((afterFav.MaxY - afterFav.MinY) - (beforeTab.MaxY - beforeTab.MinY)) > 10f,
              $"x={beforeTab.MinX:F0}→{afterFav.MinX:F0} w={beforeTab.MaxX - beforeTab.MinX:F0}→{afterFav.MaxX - afterFav.MinX:F0} h={beforeTab.MaxY - beforeTab.MinY:F0}→{afterFav.MaxY - afterFav.MinY:F0}");
        m2 = LibraryPanelInfoNow();
        ClickCanvasRect(m2.ShapesTab);
        var afterShapes = LibraryPanelRectNow();
        Check("面板点「图形」→ 切回、主条图形蓝",
              LibraryTab == LibraryTab.Shapes
              && ui.CellActiveForTest(10) && !ui.CellActiveForTest(11),
              $"tab={LibraryTab} 图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");
        Check("切回图形：高度恢复成图形页自己的",
              MathF.Abs((afterShapes.MaxY - afterShapes.MinY) - (beforeTab.MaxY - beforeTab.MinY)) < 1f
              && MathF.Abs(afterShapes.MinX - beforeTab.MinX) < 0.5f,
              $"h={afterShapes.MaxY - afterShapes.MinY:F0} 期望={beforeTab.MaxY - beforeTab.MinY:F0}");

        // 3. 收藏格 → 同一面板切收藏页
        ClickCell(11);
        Check("收藏格 → 同一面板切收藏页",
              LibraryPanelOpen && LibraryTab == LibraryTab.Favorites,
              $"open={LibraryPanelOpen} tab={LibraryTab}");
        Check("收藏蓝、图形不蓝",
              ui.CellActiveForTest(11) && !ui.CellActiveForTest(10),
              $"图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");

        // 4. 收藏页点图形格 = 切回图形页，不是关
        ClickCell(10);
        Check("收藏页点图形格 → 切图形页，不关面板",
              LibraryPanelOpen && LibraryTab == LibraryTab.Shapes,
              $"open={LibraryPanelOpen} tab={LibraryTab}");

        // 5. 图形页再点图形格 = 关；两格都不蓝
        ClickCell(10);
        Check("图形页再点图形格 → 关面板，两格不蓝",
              !LibraryPanelOpen && !ui.CellActiveForTest(10) && !ui.CellActiveForTest(11),
              $"open={LibraryPanelOpen} 图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");

        // 6. 收藏格再开 → 再点关
        ClickCell(11);
        bool opened = LibraryPanelOpen && LibraryTab == LibraryTab.Favorites;
        ClickCell(11);
        Check("收藏格开关一次：开收藏 → 再点关",
              opened && !LibraryPanelOpen,
              $"opened={opened} open={LibraryPanelOpen}");
        Check("落盘：图形/收藏高度各一份",
              !string.IsNullOrEmpty(GetUiPref("libHShapes"))
              && !string.IsNullOrEmpty(GetUiPref("libHFav")),
              $"shapes={GetUiPref("libHShapes")} fav={GetUiPref("libHFav")}");

        // 交换过格子后，截屏的上带不能串成“22 段图形”。
        ClickCell(9);
        Check("截屏格仍正常：工具=Capture、上带=截屏",
              Tool == Tool.Capture && ui.BandCellForTest == 9 && !ui.SegmentRectForTest(1).IsEmpty,
              $"tool={Tool} bandCell={ui.BandCellForTest} seg1={ui.SegmentRectForTest(1)}");

        Console.WriteLine();
        Console.WriteLine($"图形面板专项：通过 {pass} 项，失败 {fail} 项");
        ExitCode = fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 锁屏 / 远程桌面拿不到合成鼠标时的直连版：布局、命中、档位都走正式代码路径，
    /// 只是把「SendMouse 打点到屏幕」换成「直接调 LibraryPointerDown」。
    /// </summary>
    private void ShapePanelDirectTest()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(300);

        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine("  界面没挂上，停止");
            ExitCode = 1;
            return;
        }

        void Click(RectF r)
            => LibraryPointerDown((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f,
                                  Native.PT_MOUSE, 1);
        void ClickShapeTab(string id)
        {
            var mm = LibraryPanelInfoNow();
            var cats = ShapeCatalog.Categories;
            var widths = new float[cats.Length];
            for (int i = 0; i < cats.Length; i++)
                widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * UiUnit;
            int idx = Array.FindIndex(cats, c => c.Id == id);
            Click(LibraryLayout.TabRect(mm, widths, idx, UiUnit, LibraryTabScroll));
        }

        ResetLibraryPanelForTest();
        ShapeCategoryId = "all";
        ToggleShapesPanel();
        Check("图形格 → 面板图形页", LibraryPanelOpen && LibraryTab == LibraryTab.Shapes,
              $"open={LibraryPanelOpen} tab={LibraryTab}");
        Check("图形蓝、收藏不蓝",
              ui.CellActiveForTest(10) && !ui.CellActiveForTest(11),
              $"图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");

        var m = LibraryPanelInfoNow();
        Check("档位展开后共 38 张卡",
              ShapeCatalog.All.Length == 38 && ShapeCatalog.CountFor("all") == 38,
              $"all={ShapeCatalog.CountFor("all")}");
        Check("分类数量：常用12 / 曲线10 / 立体16",
              ShapeCatalog.CountFor("common") == 12 && ShapeCatalog.CountFor("curve") == 10
              && ShapeCatalog.CountFor("solid") == 16,
              $"常用={ShapeCatalog.CountFor("common")} 曲线={ShapeCatalog.CountFor("curve")} 立体={ShapeCatalog.CountFor("solid")}");
        Check("图形页默认「全部」、9 列 × 5 行、卡片 72×82",
              ShapeCategoryId == "all" && m.Cols == 9
              && MathF.Abs((m.Panel.MaxY - m.Panel.MinY)
                           - LibraryLayout.HeightForRows(5, UiUnit, LibraryLayout.ShapeCellHLogical)) < 1f
              && MathF.Abs(m.CellW - LibraryLayout.ShapeCellWLogical * UiUnit) < 0.5f
              && MathF.Abs(m.CellH - LibraryLayout.ShapeCellHLogical * UiUnit) < 0.5f,
              $"cat={ShapeCategoryId} cols={m.Cols} h={m.Panel.MaxY - m.Panel.MinY:F0} cell={m.CellW / UiUnit:F0}×{m.CellH / UiUnit:F0}");
        float shapesPageH = m.Panel.MaxY - m.Panel.MinY;

        // 2a1. 卡片缝里也能拖面板（和收藏页同一条逻辑）。
        {
            var card0 = LibraryLayout.CellRect(m, 0, 0f);
            float gapX = card0.MaxX + LibraryLayout.CellGapLogical * UiUnit * 0.5f;
            float gapY = (card0.MinY + card0.MaxY) * 0.5f;
            var beforeDrag = LibraryPanelRectNow();
            LibraryPointerDown(gapX, gapY, Native.PT_MOUSE, 1);
            LibraryPanelDragMove(gapX + 80f * UiUnit, gapY + 40f * UiUnit);
            LibraryPanelDragEnd();
            var afterDrag = LibraryPanelRectNow();
            Check("卡片缝里拖一下：面板跟着走",
                  MathF.Abs(afterDrag.MinX - beforeDrag.MinX) > 20f
                  || MathF.Abs(afterDrag.MinY - beforeDrag.MinY) > 20f,
                  $"x={beforeDrag.MinX:F0}→{afterDrag.MinX:F0} y={beforeDrag.MinY:F0}→{afterDrag.MinY:F0}");
        }

        ClickShapeTab("common");
        Check("点「常用」→ 12 张、面板不变高",
              ShapeCategoryId == "common" && ShapeCardsNow().Length == 12
              && MathF.Abs((LibraryPanelRectNow().MaxY - LibraryPanelRectNow().MinY) - shapesPageH) < 1f,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length}");
        ClickShapeTab("curve");
        Check("点「曲线」→ 10 张", ShapeCategoryId == "curve" && ShapeCardsNow().Length == 10,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length}");
        ClickShapeTab("solid");
        Check("点「立体」→ 16 张", ShapeCategoryId == "solid" && ShapeCardsNow().Length == 16,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length}");
        ClickShapeTab("all");

        var cards = ShapeCardsNow();
        m = LibraryPanelInfoNow();
        Click(LibraryLayout.CellRect(m, 5, 0f));
        Check("点「梯形」卡 = 选梯形",
              Tool == Tool.Trapezoid && ShapePresetActive(cards[5]),
              $"tool={Tool} active={ShapePresetActive(cards[5])}");
        Check("梯形卡高亮、直线卡不高亮",
              ShapePresetActive(cards[5]) && !ShapePresetActive(cards[0]),
              $"直线={ShapePresetActive(cards[0])} 梯形={ShapePresetActive(cards[5])}");
        Check("面板选图形后标记为一次性",
              ShapeOneShotForTest,
              $"oneShot={ShapeOneShotForTest} tool={Tool}");
        int strokesBefore = Doc.Strokes.Count;
        ShapeDragForTest(200f, 200f, 520f, 360f);
        Check("画完一个图形 → 默认选中、工具切框选、面板不关",
              Tool == Tool.Marquee && !ShapeOneShotForTest && LibraryPanelOpen
              && Doc.Selected.Count == 1 && Doc.Strokes.Count == strokesBefore + 1,
              $"tool={Tool} oneShot={ShapeOneShotForTest} open={LibraryPanelOpen} sel={Doc.Selected.Count} strokes={Doc.Strokes.Count - strokesBefore}");
        Check("画完图形已挂上“点空白回上一个工具”",
              RestoreToolAfterShapeSelectionForTest,
              $"restore={RestoreToolAfterShapeSelectionForTest}");
        ClickBlankAfterShapeForTest();
        Check("点空白 → 回到上一个工具、取消选中",
              Tool == Tool.Pen && Doc.Selected.Count == 0 && !RestoreToolAfterShapeSelectionForTest,
              $"tool={Tool} sel={Doc.Selected.Count} restore={RestoreToolAfterShapeSelectionForTest}");

        // 梯形上底角点：拖过另一个点后自动交换左右身份，仍是梯形、不交叉。
        var trap = Doc.Strokes[^1];
        float oldTL = trap.Points[3].X, oldTR = trap.Points[2].X;
        VertexDragForTest(trap, ShapeHandle.Vertex3, oldTL, trap.Points[3].Y,
                          oldTR + 60f, trap.Points[3].Y);
        Check("梯形上底点拖过另一个点：自动交换左右、仍是梯形",
              trap.Points[3].X < trap.Points[2].X
              && MathF.Abs(trap.Points[2].Y - trap.Points[3].Y) < 0.5f
              && MathF.Abs(trap.Points[0].Y - trap.Points[1].Y) < 0.5f,
              $"上底左/右 {trap.Points[3].X:F0} / {trap.Points[2].X:F0}");

        // 悬停：名字不常驻，指针停在哪张卡就显示哪张。
        m = LibraryPanelInfoNow();
        var hoverCell = LibraryLayout.CellRect(m, 5, 0f);
        LastPointerType = Native.PT_MOUSE;
        LibraryPanelPointerMove((hoverCell.MinX + hoverCell.MaxX) * 0.5f,
                                (hoverCell.MinY + hoverCell.MaxY) * 0.5f, 1);
        Check("悬停卡片：名称索引跟上", ShapeHoverIndex == 5,
              $"hover={ShapeHoverIndex}");
        LibraryPanelPointerMove(m.Panel.MinX - 30f * UiUnit,
                                (hoverCell.MinY + hoverCell.MaxY) * 0.5f, 1);
        Check("移出卡片：悬停清空", ShapeHoverIndex == -1,
              $"hover={ShapeHoverIndex}");
        // 五角星 / 扇形各画一个，确认新工具真的能落笔。
        m = LibraryPanelInfoNow();
        Click(LibraryLayout.CellRect(m, 8, 0f));
        ShapeDragForTest(300f, 260f, 470f, 390f);
        Check("五角星能画出来", Doc.Strokes.Count > 0 && Doc.Strokes[^1].Kind == StrokeKind.Star,
              $"kind={(Doc.Strokes.Count > 0 ? Doc.Strokes[^1].Kind.ToString() : "无")}");
        m = LibraryPanelInfoNow();
        Click(LibraryLayout.CellRect(m, 9, 0f));
        ShapeDragForTest(600f, 260f, 760f, 390f);
        Check("扇形能画出来", Doc.Strokes.Count > 0 && Doc.Strokes[^1].Kind == StrokeKind.Sector,
              $"kind={(Doc.Strokes.Count > 0 ? Doc.Strokes[^1].Kind.ToString() : "无")}");
        {
            var sec = Doc.Strokes[^1];
            float r0 = sec.SectorRadiusLocal();
            float a0 = sec.SectorStartAngleLocal();
            var c = new Vector2(sec.Points[0].X, sec.Points[0].Y);
            var end = new Vector2(c.X + MathF.Cos(a0 + MathF.PI) * r0,
                                  c.Y + MathF.Sin(a0 + MathF.PI) * r0);
            var oldEnd = new Vector2(sec.Points[2].X, sec.Points[2].Y);
            VertexDragForTest(sec, ShapeHandle.FocusPoint, oldEnd.X, oldEnd.Y, end.X, end.Y);
            float sweep = sec.SectorSweepDegrees();
            Check("扇形圆弧端点：改角度、半径不变",
                  MathF.Abs(sweep - 180f) < 2f && MathF.Abs(sec.SectorRadiusLocal() - r0) < 0.5f,
                  $"角度={sweep:F0}° 半径={sec.SectorRadiusLocal():F0}（原 {r0:F0}）");
        }

        Check("选图形后面板常驻、主条图形蓝",
              LibraryPanelOpen && LibraryTab == LibraryTab.Shapes && ui.CellActiveForTest(10),
              $"open={LibraryPanelOpen} tab={LibraryTab} 图形={ui.CellActiveForTest(10)}");

        // 六棱台：立体组最后一张，工具 + 档位都要对上。
        ClickShapeTab("solid");
        cards = ShapeCardsNow();
        m = LibraryPanelInfoNow();
        Click(LibraryLayout.CellRect(m, cards.Length - 1, 0f));
        Check("点「六棱台」卡 = 棱台 + 6 边档",
              Tool == Tool.Frustum && SidesFor(Tool.Frustum) == 6
              && ShapePresetActive(cards[^1]),
              $"tool={Tool} sides={SidesFor(Tool.Frustum)}");

        // 切收藏页：只换高度，位置/宽度不动。
        var beforeTab = LibraryPanelRectNow();
        m = LibraryPanelInfoNow();
        Click(m.FavoritesTab);
        var afterFav = LibraryPanelRectNow();
        Check("面板点「我的收藏」→ 切页、主条收藏蓝",
              LibraryTab == LibraryTab.Favorites
              && ui.CellActiveForTest(11) && !ui.CellActiveForTest(10),
              $"tab={LibraryTab} 图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");
        Check("切收藏：位置/宽度不变，只换高度",
              MathF.Abs(afterFav.MinX - beforeTab.MinX) < 0.5f
              && MathF.Abs((afterFav.MaxX - afterFav.MinX) - (beforeTab.MaxX - beforeTab.MinX)) < 0.5f
              && MathF.Abs((afterFav.MaxY - afterFav.MinY) - (beforeTab.MaxY - beforeTab.MinY)) > 10f,
              $"x={beforeTab.MinX:F0}→{afterFav.MinX:F0} h={beforeTab.MaxY - beforeTab.MinY:F0}→{afterFav.MaxY - afterFav.MinY:F0}");
        m = LibraryPanelInfoNow();
        Click(m.ShapesTab);
        var afterShapes = LibraryPanelRectNow();
        Check("切回图形：高度恢复成图形页自己的",
              MathF.Abs((afterShapes.MaxY - afterShapes.MinY)
                        - (beforeTab.MaxY - beforeTab.MinY)) < 1f
              && MathF.Abs(afterShapes.MinX - beforeTab.MinX) < 0.5f,
              $"h={afterShapes.MaxY - afterShapes.MinY:F0} 期望={beforeTab.MaxY - beforeTab.MinY:F0}");

        ToggleShapesPanel();
        Check("图形页再点图形格 → 关面板，两格不蓝",
              !LibraryPanelOpen && !ui.CellActiveForTest(10) && !ui.CellActiveForTest(11),
              $"open={LibraryPanelOpen} 图形={ui.CellActiveForTest(10)} 收藏={ui.CellActiveForTest(11)}");
        Check("落盘：图形/收藏高度各一份",
              !string.IsNullOrEmpty(GetUiPref("libHShapes"))
              && !string.IsNullOrEmpty(GetUiPref("libHFav")),
              $"shapes={GetUiPref("libHShapes")} fav={GetUiPref("libHFav")}");

        Console.WriteLine();
        Console.WriteLine($"图形面板专项（直连）：通过 {pass} 项，失败 {fail} 项");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }

    /// <summary>
    /// 面板**尺寸 / 位置记忆**专项（2026-11-03）：用户报"图形框位置有点记忆，
    /// 大小没记忆"。这条链子跨"关软件再开"——必须走**写盘 → 清内存 → 从盘重读**，
    /// 只在内存里比对等于没验。图形页和收藏页各验一遍，且两页的高度各记各的。
    /// </summary>
    private void LibrarySizeMemoryTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 面板尺寸记忆自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }
        bool Near(float a, float b, float tol = 1.5f) => MathF.Abs(a - b) <= tol;

        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(250);
        if (CurrentUi is not InkUi.FullUi)
        {
            Console.WriteLine("  界面没挂上，停止");
            ExitCode = 1;
            return;
        }

        void ResizeBy(float dx, float dy, uint id)
        {
            var r = LibraryPanelRectNow();
            var grip = LibraryLayout.ResizeRect(r, UiUnit);
            float gx = (grip.MinX + grip.MaxX) * 0.5f;
            float gy = (grip.MinY + grip.MaxY) * 0.5f;
            LibraryPointerDown(gx, gy, Native.PT_PEN, id);
            LibraryPanelDragMove(gx + dx * UiUnit, gy + dy * UiUnit);
            LibraryPanelDragEnd();
            SettleFrames(150);
        }
        void CloseSaveReload()
        {
            CloseLibraryPanel();
            SaveSettingsForTest();          // 自检模式平时不落盘，这里显式写一次
            ReloadUiPrefsForTest();         // 磁盘 → 内存，模拟"关软件再开"
            ReloadLibraryPanelPlacementForTest();
        }

        // ---- 图形页：拖窄 + 拖矮（故意低于 5 行）----
        ResetLibraryPanelForTest();
        OpenLibraryPanel(LibraryTab.Shapes);
        SettleFrames(150);
        SetShapeCategory("solid");
        Check("图形页切到「立体」→ 16 张", ShapeCategoryId == "solid" && ShapeCardsNow().Length == 16,
              $"分类 {ShapeCategoryId}，{ShapeCardsNow().Length} 张");

        ResizeBy(-2f * (LibraryLayout.CellWLogical + LibraryLayout.CellGapLogical),
                 -2f * (LibraryLayout.ShapeCellHLogical + LibraryLayout.RowGapLogical), 9301u);
        var shrunk = LibraryPanelRectNow();
        var shrunkInfo = LibraryPanelInfoNow();
        Check("图形页可以拖到 5 行以下（不再被硬卡回 5 行）",
              shrunkInfo.Rows < 5,
              $"行 {shrunkInfo.Rows}，高 {shrunk.MaxY - shrunk.MinY:F0}");
        float sx = shrunk.MinX, sy = shrunk.MinY;
        float sw = shrunk.MaxX - shrunk.MinX, sh = shrunk.MaxY - shrunk.MinY;

        CloseSaveReload();
        OpenLibraryPanel(LibraryTab.Shapes);
        SettleFrames(200);
        var reopened = LibraryPanelRectNow();
        Check("图形页：关闭重开后位置 / 宽度 / 高度都记得",
              Near(reopened.MinX, sx) && Near(reopened.MinY, sy)
              && Near(reopened.MaxX - reopened.MinX, sw)
              && Near(reopened.MaxY - reopened.MinY, sh),
              $"({sx:F0},{sy:F0}) {sw:F0}×{sh:F0} → "
              + $"({reopened.MinX:F0},{reopened.MinY:F0}) "
              + $"{reopened.MaxX - reopened.MinX:F0}×{reopened.MaxY - reopened.MinY:F0}");
        Check("图形页：关闭重开后仍停在「立体」",
              ShapeCategoryId == "solid" && ShapeCardsNow().Length == 16,
              $"分类 {ShapeCategoryId}，{ShapeCardsNow().Length} 张");

        // ---- 收藏页：高度是另一份；缩到 1 行后照样记得 ----
        SetLibraryTab(LibraryTab.Favorites);
        SettleFrames(150);
        SetLibraryCategory(ShapeLibrary.Uncategorized);
        Check("收藏页切到「未分类」", LibraryCategoryId == ShapeLibrary.Uncategorized,
              $"分类 {LibraryCategoryId}");
        ResizeBy(0f, -(LibraryLayout.CellHLogical + LibraryLayout.RowGapLogical), 9302u);
        var fav = LibraryPanelRectNow();
        var favInfo = LibraryPanelInfoNow();
        Check("收藏页可以缩到 1 行（最小档）", favInfo.Rows == 1,
              $"行 {favInfo.Rows}，高 {fav.MaxY - fav.MinY:F0}");
        float fx = fav.MinX, fy = fav.MinY;
        float fw = fav.MaxX - fav.MinX, fh = fav.MaxY - fav.MinY;

        CloseSaveReload();
        OpenLibraryPanel(LibraryTab.Favorites);
        SettleFrames(200);
        var favBack = LibraryPanelRectNow();
        Check("收藏页：关闭重开后位置 / 宽度 / 高度都记得",
              Near(favBack.MinX, fx) && Near(favBack.MinY, fy)
              && Near(favBack.MaxX - favBack.MinX, fw)
              && Near(favBack.MaxY - favBack.MinY, fh),
              $"({fx:F0},{fy:F0}) {fw:F0}×{fh:F0} → "
              + $"({favBack.MinX:F0},{favBack.MinY:F0}) "
              + $"{favBack.MaxX - favBack.MinX:F0}×{favBack.MaxY - favBack.MinY:F0}");
        Check("收藏页：关闭重开后仍停在「未分类」",
              LibraryCategoryId == ShapeLibrary.Uncategorized,
              $"分类 {LibraryCategoryId}");

        // ---- 两页高度各一份：切回图形页仍是刚才的矮档 ----
        SetLibraryTab(LibraryTab.Shapes);
        SettleFrames(150);
        var shapesBack = LibraryPanelRectNow();
        Check("切回图形页：仍用图形页自己的高度和分类",
              Near(shapesBack.MaxY - shapesBack.MinY, sh)
              && ShapeCategoryId == "solid",
              $"高 {shapesBack.MaxY - shapesBack.MinY:F0}，期望 {sh:F0}；分类 {ShapeCategoryId}");

        Check("落盘：图形 / 收藏高度各一份，分类也各一份",
              !string.IsNullOrEmpty(GetUiPref("libHShapes"))
              && !string.IsNullOrEmpty(GetUiPref("libHFav"))
              && GetUiPref("shapeCat") == "solid"
              && GetUiPref("libCat") == ShapeLibrary.Uncategorized,
              $"shapes={GetUiPref("libHShapes")} fav={GetUiPref("libHFav")} "
              + $"shapeCat={GetUiPref("shapeCat")} libCat={GetUiPref("libCat")}");

        // ---- 白板滚动后切页签：面板必须还钉在屏幕同一位置 ----
        // （老代码把画布坐标当屏幕坐标用，相机一滚，切一次页签就偏一截。）
        {
            float oldOff = ViewOffsetY;
            ViewOffsetY = oldOff + 220f;
            SettleFrames(120);
            var cam0 = LibraryPanelRectNow();
            float x0 = cam0.MinX;
            float bottom0 = cam0.MaxY + ViewOffsetY;
            SetLibraryTab(LibraryTab.Favorites);
            SettleFrames(120);
            SetLibraryTab(LibraryTab.Shapes);
            SettleFrames(120);
            var cam1 = LibraryPanelRectNow();
            Check("白板滚动后切页签：面板仍钉在屏幕同一位置",
                  Near(cam1.MinX, x0) && Near(cam1.MaxY + ViewOffsetY, bottom0),
                  $"x {x0:F0}→{cam1.MinX:F0}，屏幕底 {bottom0:F0}→{cam1.MaxY + ViewOffsetY:F0}");
            ViewOffsetY = oldOff;
            SettleFrames(120);
        }

        ResetLibraryPanelForTest();
        SaveSettingsForTest();          // 临时配置也恢复成默认，别把测试尺寸留给下一轮
        Console.WriteLine();
        Console.WriteLine($"面板尺寸记忆专项：通过 {pass} 项，失败 {fail} 项");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
