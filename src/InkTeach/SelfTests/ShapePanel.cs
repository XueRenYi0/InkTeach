// 图形面板专项（--shapepaneltest）
//
// 只测这次改动的一条线：
//   图形格 → 图形页；选图形卡 → 工具变了但面板不关；
//   收藏格 → 同一面板切收藏页；两格蓝色互斥；
//   再点当前入口 → 关面板，两格都不蓝。
using InkEngine;

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

        // 2a. 37 张档位卡 + 五个分类；图形卡 72×82，固定 5 行高
        Check("档位展开后共 37 张卡",
              ShapeCatalog.All.Length == 37 && ShapeCatalog.CountFor("all") == 37,
              $"all={ShapeCatalog.CountFor("all")}");
        Check("分类数量：全部37 / 常用11 / 曲线10 / 立体16",
              ShapeCatalog.CountFor("common") == 11 && ShapeCatalog.CountFor("curve") == 10
              && ShapeCatalog.CountFor("solid") == 16,
              $"常用={ShapeCatalog.CountFor("common")} 曲线={ShapeCatalog.CountFor("curve")} 立体={ShapeCatalog.CountFor("solid")}");
        Check("图形页默认「全部」、9 列 × 5 行、卡片 72×82",
              ShapeCategoryId == "all" && m.Cols == 9 && m.Rows == 5
              && MathF.Abs(m.CellW - LibraryLayout.ShapeCellWLogical * UiUnit) < 0.5f
              && MathF.Abs(m.CellH - LibraryLayout.ShapeCellHLogical * UiUnit) < 0.5f,
              $"cat={ShapeCategoryId} cols={m.Cols} rows={m.Rows} cell={m.CellW / UiUnit:F0}×{m.CellH / UiUnit:F0}");
        float shapesPageH = LibraryPanelRectNow().MaxY - LibraryPanelRectNow().MinY;

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
        Check("点「常用」→ 11 张、面板不变高",
              ShapeCategoryId == "common" && ShapeCardsNow().Length == 11
              && MathF.Abs((LibraryPanelRectNow().MaxY - LibraryPanelRectNow().MinY) - shapesPageH) < 1f,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length} h={(LibraryPanelRectNow().MaxY - LibraryPanelRectNow().MinY):F0}");
        ClickShapeTab("curve");
        Check("点「曲线」→ 10 张", ShapeCategoryId == "curve" && ShapeCardsNow().Length == 10,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length}");
        ClickShapeTab("solid");
        Check("点「立体」→ 16 张", ShapeCategoryId == "solid" && ShapeCardsNow().Length == 16,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length}");
        ClickShapeTab("all");
        Check("点「全部」→ 37 张、仍是 5 行高",
              ShapeCategoryId == "all" && ShapeCardsNow().Length == 37
              && LibraryPanelInfoNow().Rows == 5,
              $"cat={ShapeCategoryId} n={ShapeCardsNow().Length} rows={LibraryPanelInfoNow().Rows}");

        // 2a2. 档位直接选：第 2 张 = 虚线，不再“同卡再点换档”
        var cards = ShapeCardsNow();
        m = LibraryPanelInfoNow();
        ClickCanvasRect(LibraryLayout.CellRect(m, 1, 0f));
        Check("点「虚线」卡 = 直线 + 虚线档",
              Tool == Tool.Line && LineDash == StrokeDash.Dashed && ShapePresetActive(cards[1]),
              $"tool={Tool} dash={LineDash} active={ShapePresetActive(cards[1])}");
        Check("虚线卡高亮、实线卡不高亮",
              ShapePresetActive(cards[1]) && !ShapePresetActive(cards[0]),
              $"实线={ShapePresetActive(cards[0])} 虚线={ShapePresetActive(cards[1])}");

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
        Check("档位展开后共 37 张卡",
              ShapeCatalog.All.Length == 37 && ShapeCatalog.CountFor("all") == 37,
              $"all={ShapeCatalog.CountFor("all")}");
        Check("分类数量：常用11 / 曲线10 / 立体16",
              ShapeCatalog.CountFor("common") == 11 && ShapeCatalog.CountFor("curve") == 10
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

        ClickShapeTab("common");
        Check("点「常用」→ 11 张、面板不变高",
              ShapeCategoryId == "common" && ShapeCardsNow().Length == 11
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
        Click(LibraryLayout.CellRect(m, 1, 0f));
        Check("点「虚线」卡 = 直线 + 虚线档",
              Tool == Tool.Line && LineDash == StrokeDash.Dashed && ShapePresetActive(cards[1]),
              $"tool={Tool} dash={LineDash} active={ShapePresetActive(cards[1])}");
        Check("虚线卡高亮、实线卡不高亮",
              ShapePresetActive(cards[1]) && !ShapePresetActive(cards[0]),
              $"实线={ShapePresetActive(cards[0])} 虚线={ShapePresetActive(cards[1])}");
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
}
