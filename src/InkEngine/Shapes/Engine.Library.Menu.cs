using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;


public partial class InkEngine
{
    /// <summary>收藏弹窗菜单里的一行（绘制与命中共用）。</summary>
    internal readonly record struct LibraryMenuItem(string Key, string Label, bool Danger, bool Enabled);

    private const int LibraryMenuPerPage = 8;

    internal List<LibraryMenuItem> LibraryMenuItems()
    {
        var rows = new List<LibraryMenuItem>(8);
        if (LibraryMenu == 3)
        {
            rows.Add(new LibraryMenuItem("back", Loc.Tr("‹ 返回"), false, true));
            var cats = _libraryCategoryInfos;
            int pages = Math.Max(1, (cats.Length + LibraryMenuPerPage - 1) / LibraryMenuPerPage);
            int page = Math.Clamp(LibraryMenuPage, 0, pages - 1);
            int start = page * LibraryMenuPerPage;
            int end = Math.Min(cats.Length, start + LibraryMenuPerPage);
            for (int i = start; i < end; i++)
            {
                if (cats[i].Id == LibraryCategoryIds.All || cats[i].Id == LibraryCategoryIds.Trash) continue;
                rows.Add(new LibraryMenuItem("move:" + cats[i].Id, Loc.Tr(cats[i].Name), false, true));
            }
            if (pages > 1)
                rows.Add(new LibraryMenuItem("nextmove", Loc.T("下一页 {0}/{1} ›", page + 1, pages), false, true));
            return rows;
        }

        // ☰ 主菜单：只放“调整 / 补充内容”，使用态不出现删除/改名。
        // “导入图片”是**内容导入**（透明贴纸），不是收藏备份的导入/导出。
        rows.Add(new LibraryMenuItem("importimg", Loc.Tr("导入图片 / 贴纸…"), false, true));
        rows.Add(new LibraryMenuItem("manage", Loc.Tr("整理收藏…"), false, true));
        rows.Add(new LibraryMenuItem("categories", Loc.Tr("管理分类…"), false, true));
        bool trashView = LibraryCategoryId == ShapeLibrary.TrashCategory;
        int trashN = 0;
        foreach (var e in LibraryAllEntries) if (e.InTrash) trashN++;
        if (trashView)
        {
            rows.Add(new LibraryMenuItem("backcat", Loc.Tr("‹ 返回收藏"), false, true));
            rows.Add(new LibraryMenuItem("emptytrash",
                LibraryConfirm == "emptytrash" ? Loc.Tr("再点一次：清空回收站") : Loc.T("清空回收站 · {0} 个", trashN),
                true, trashN > 0));
        }
        else
        {
            rows.Add(new LibraryMenuItem("trash",
                trashN > 0 ? Loc.T("回收站 · {0} 个", trashN) : Loc.Tr("回收站（空）"), false, true));
        }
        // 方向和位置分开：拖动只挪位置，要换横/竖排走这里，不靠“拖到哪儿”。
        rows.Add(new LibraryMenuItem("orientation",
            LibraryMode == LibraryPanelMode.Horizontal ? Loc.Tr("切换为竖排") : Loc.Tr("切换为横排"),
            false, true));
        rows.Add(new LibraryMenuItem("reset", Loc.Tr("重置位置与大小"), false, true));
        return rows;
    }

    internal RectF LibraryMenuRect()
    {
        var m = LibraryPanelInfoNow();
        var screen = UiLogicalScreen;
        float dpi = UiUnit;
        var items = LibraryMenuItems();
        float w = 220f * dpi;
        float h = (8f + Math.Max(1, items.Count) * 30f) * dpi;
        var anchor = m.Menu;                 // 画布坐标
        var uiBar = UiBarBoundsNow();
        // 屏幕边界先换成画布坐标，再和 anchor 比；不然滚动白板后菜单会跑。
        float screenMinX = screen.MinX * dpi;
        float screenMaxX = screen.MaxX * dpi;
        float screenMinY = screen.MinY * dpi - ViewOffsetY;
        float screenMaxY = screen.MaxY * dpi - ViewOffsetY;
        float bottom = uiBar.IsEmpty ? screenMaxY - 8f * dpi
                                     : uiBar.MinY * dpi - ViewOffsetY - 8f * dpi;
        float x = Math.Clamp(anchor.MinX, screenMinX + 8f * dpi,
                             MathF.Max(screenMinX + 8f * dpi, screenMaxX - 8f * dpi - w));
        float y = anchor.MaxY + 6f * dpi;
        if (y + h > bottom) y = anchor.MinY - h - 6f * dpi;
        y = Math.Clamp(y, screenMinY + 8f * dpi,
                       MathF.Max(screenMinY + 8f * dpi, bottom - h));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    internal RectF LibraryMenuRowRect(in RectF menu, int i)
        => new()
        {
            MinX = menu.MinX + 4f * UiUnit, MinY = menu.MinY + 4f * UiUnit + i * 30f * UiUnit,
            MaxX = menu.MaxX - 4f * UiUnit, MaxY = menu.MinY + 4f * UiUnit + (i + 1) * 30f * UiUnit,
        };

    private void CloseLibraryMenu()
    {
        LibraryMenu = 0;
        LibraryMenuPage = 0;
        LibraryConfirm = "";
        _dirty = true;
    }

    /// <summary>自检用：关掉收藏弹窗的菜单。</summary>
    internal void CloseLibraryMenuForTest() => CloseLibraryMenu();

    /// <summary>自检用：画完图形后点空白是否已经挂上“回到上一个工具”。</summary>
    internal bool RestoreToolAfterShapeSelectionForTest => _restoreToolAfterLibrarySelection;
    /// <summary>自检用：模拟点空白，兑现“回到上一个工具”。</summary>
    internal void ClickBlankAfterShapeForTest() => ExitLibraryPlacementSelection();

    private void RunLibraryMenu(string key)
    {
        if (key.StartsWith("move:", StringComparison.Ordinal))
        {
            string id = key.Substring(5);
            CloseLibraryMenu();
            MoveLibraryEntry(id);
            return;
        }
        if (key == "nextmove")
        {
            LibraryMenuPage++;
            _dirty = true;
            return;
        }
        switch (key)
        {
            case "importimg":
                CloseLibraryMenu();
                ImportLibraryImages();
                break;
            case "manage":
                CloseLibraryMenu();
                LibrarySelectMode = true;
                LibraryUseSelected = null;
                _libraryPending = null;
                _libraryPendingTitle = null;
                LibrarySelected.Clear();
                LibraryMarqueeActive = false;
                LibraryConfirm = "";
                NotifyUiStateChanged();
                _dirty = true;
                break;
            case "categories":
                CloseLibraryMenu();
                LibraryCategoriesOpen = true;
                LibraryCategoryMenuOpen = false;
                LibraryCategoryMenuHover = -1;
                LibraryDrawerManage = true;
                EnsureLibraryCategoryVisible();
                _dirty = true;
                break;
            case "trash":
                CloseLibraryMenu();
                SetLibraryCategory(LibraryCategoryIds.Trash);
                break;
            case "backcat":
                CloseLibraryMenu();
                SetLibraryCategory(LibraryCategoryIds.All);
                break;
            case "orientation":
                CloseLibraryMenu();
                ToggleLibraryOrientation();
                break;
            case "reset":
                CloseLibraryMenu();
                ResetLibraryPlacement();
                break;
            case "back":
                CloseLibraryMenu();
                break;
            case "emptytrash":
                if (LibraryConfirm != key) { LibraryConfirm = key; _dirty = true; break; }
                CloseLibraryMenu();
                EmptyLibraryTrash();
                break;
        }
    }

    private void ApplyLibraryFilter()
    {
        if (LibraryCategoryId == "all")
        {
            LibraryEntries = new List<ShapeLibrary.Entry>();
            foreach (var e in LibraryAllEntries) if (!e.InTrash) LibraryEntries.Add(e);
        }
        else if (LibraryCategoryId == ShapeLibrary.TrashCategory)
        {
            LibraryEntries = new List<ShapeLibrary.Entry>();
            foreach (var e in LibraryAllEntries) if (e.InTrash) LibraryEntries.Add(e);
        }
        else
        {
            LibraryEntries = new List<ShapeLibrary.Entry>();
            foreach (var e in LibraryAllEntries)
                if (!e.InTrash && e.CategoryId == LibraryCategoryId) LibraryEntries.Add(e);
        }
        LibraryHover = -1;
        LibrarySelected.Clear();
        LibraryUseSelected = null;
    }

    /// <summary>当前页签的滚动项数（图形页是过滤后的卡片数）。</summary>
    private int LibraryScrollCountNow()
        => LibraryTab == LibraryTab.Shapes ? ShapeCardsNow().Length : LibraryEntries.Count;

    /// <summary>滚轮 / 滚动条滚内容（0 = 顶部）。</summary>
    internal void LibraryScrollBy(float dy)
    {
        var m = LibraryPanelInfoNow();
        float max = LibraryLayout.MaxScroll(m, LibraryScrollCountNow());
        float next = Math.Clamp(LibraryScrollY + dy, 0f, max);
        if (MathF.Abs(next - LibraryScrollY) < 0.01f) return;
        LibraryScrollY = next;
        LibraryHover = -1;
        _dirty = true;
    }

    internal void ClampLibraryScroll()
    {
        var m = LibraryPanelInfoNow();
        LibraryScrollY = Math.Clamp(LibraryScrollY, 0f,
            LibraryLayout.MaxScroll(m, LibraryScrollCountNow()));
    }

    /// <summary>让当前分类的页签始终露在可视区里；分类多时页签条横向滚。</summary>
    internal void EnsureLibraryTabVisible()
    {
        var m = LibraryPanelInfoNow();
        if (!m.Tabbed) { LibraryTabScroll = 0; return; }
        var cats = LibraryTabsNow();
        if (cats.Length == 0) return;
        var widths = new float[cats.Length];
        for (int i = 0; i < cats.Length; i++)
            widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * UiUnit;
        string curId = LibraryTab == LibraryTab.Shapes ? ShapeCategoryId : LibraryCategoryId;
        int idx = Array.FindIndex(cats, c => c.Id == curId);
        if (idx < 0) { LibraryTabScroll = 0; return; }
        float gap = LibraryLayout.TabGapLogical * UiUnit;
        float left = 0;
        for (int i = 0; i < idx; i++) left += widths[i] + gap;
        float right = left + widths[idx];
        float view = m.Tabs.MaxX - m.Tabs.MinX;
        float max = MathF.Max(0f, LibraryLayout.TabsWidth(widths, UiUnit) - view);
        if (left < LibraryTabScroll) LibraryTabScroll = left;
        else if (right > LibraryTabScroll + view) LibraryTabScroll = right - view;
        LibraryTabScroll = Math.Clamp(LibraryTabScroll, 0f, max);
    }

    /// <summary>页签用到的分类：全部 + 未分类 + 用户分类；回收站不进页签（在 ☰ 里）。</summary>
    internal LibraryCategoryInfo[] LibraryTabCategories()
    {
        var list = new List<LibraryCategoryInfo>(_libraryCategoryInfos.Length);
        foreach (var c in _libraryCategoryInfos)
            if (c.Id != ShapeLibrary.TrashCategory) list.Add(c);
        return list.ToArray();
    }

    /// <summary>当前页签真正显示的页签表：图形页是 全部/常用/曲线/立体，收藏页是分类。</summary>
    internal (string Id, string Name)[] LibraryTabsNow()
    {
        if (LibraryTab == LibraryTab.Shapes)
        {
            var a = new (string Id, string Name)[ShapeCatalog.Categories.Length];
            for (int i = 0; i < a.Length; i++)
                a[i] = (ShapeCatalog.Categories[i].Id, ShapeCatalog.Categories[i].Name);
            return a;
        }
        var cats = LibraryTabCategories();
        var b = new (string Id, string Name)[cats.Length];
        for (int i = 0; i < cats.Length; i++) b[i] = (cats[i].Id, cats[i].Name);
        return b;
    }

    /// <summary>图形页当前分类的卡片（已按分类过滤）。</summary>
    internal ShapePreset[] ShapeCardsNow() => ShapeCatalog.ForCategory(ShapeCategoryId);

    internal void SetShapeCategory(string id)
    {
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        if (ShapeCategoryId == id) return;
        ShapeCategoryId = id;
        SetUiPref("shapeCat", id);
        ShapeHoverIndex = -1;
        ShapeNameIndex = -1;
        LibraryScrollY = 0;
        EnsureLibraryTabVisible();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>图形卡是否就是当前选中的那一张：工具相同 + 档位相同。</summary>
    internal bool ShapePresetActive(ShapePreset p)
    {
        if (Tool != p.Tool) return false;
        return p.Tool switch
        {
            // 直线三张卡各代表一档线型：只有当前 LineDash 对得上时才高亮。
            Tool.Line => LineDash == (StrokeDash)Math.Clamp(p.Variant, 0, 2),
            Tool.Parabola => p.Variant == 0 ? ParabolaAxis == CurveAxis.OpenUp : ParabolaAxis != CurveAxis.OpenUp,
            Tool.Hyperbola => HyperbolaAsymptotes == (p.Variant == 0),
            Tool.ConicEllipse => EllipseFocusTriangle == (p.Variant == 0),
            Tool.Coordinate => CoordGridDefault == (p.Variant == 0),
            Tool.Prism or Tool.Pyramid or Tool.Frustum => SidesFor(p.Tool) == p.Variant,
            _ => true,
        };
    }

    /// <summary>点面板里的图形卡：换工具 + 直接设到这张卡对应的档位。</summary>
    internal void SetShapePresetFromUi(ShapePreset p)
    {
        // 从面板点卡进来时记住“上一次的非图形工具”：
        // 单次 = 画完一个就回去；连续 = 保持图形工具，Esc 再回去。
        var back = _shapeOneShot ? _shapeOneShotReturn : _lastNonShapeTool;
        SetToolFromUi(p.Tool);
        _shapeOneShotReturn = back;
        _shapeFromPanel = true;
        _shapeOneShot = !ShapeRepeatContinuous;
        switch (p.Tool)
        {
            // 直线卡自己带线型（实线/虚线/点线各一张，2026-11-12 用户定）：
            // 点哪张就用哪档，**不受工具栏上次切到哪档影响**；同时同步 LineDash，
            // 工具栏那颗直线图标跟着变，画到画布上也是同一档。
            case Tool.Line:
                LineDash = (StrokeDash)Math.Clamp(p.Variant, 0, 2);
                SetUiPref("lineDash", ((int)LineDash).ToString());
                break;
            case Tool.Parabola:
                ParabolaAxis = p.Variant == 0 ? CurveAxis.OpenUp : CurveAxis.OpenRight;
                break;
            case Tool.Hyperbola:
                HyperbolaAsymptotes = p.Variant == 0;
                break;
            case Tool.ConicEllipse:
                EllipseFocusTriangle = p.Variant == 0;
                break;
            case Tool.Coordinate:
                CoordGridDefault = p.Variant == 0;
                SetUiPref("coordGrid", CoordGridDefault ? "1" : null);
                break;
            case Tool.Prism:
                _sidesPrism = p.Variant;
                break;
            case Tool.Pyramid:
                _sidesPyramid = p.Variant;
                break;
            case Tool.Frustum:
                _sidesFrustum = p.Variant;
                break;
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>竖排左列：保证当前分类在可视范围内（分类多时跟着滚动）。</summary>
    private void EnsureLibraryCategoryVisible()
    {
        var m = LibraryPanelInfoNow();
        if (_libraryCategoryInfos.Length == 0) return;
        int cap = Math.Max(1, LibraryLayout.CategoryDrawerCapacity(m) - 1);
        int idx = Array.FindIndex(_libraryCategoryInfos, c => c.Id == LibraryCategoryId);
        if (idx < 0) return;
        // 当前分类尽量放在第二行：上面留一行、下面也露一行，拖到相邻分类不用先滚。
        int maxScroll = Math.Max(0, _libraryCategoryInfos.Length - cap);
        LibraryCategoryScroll = Math.Clamp(idx - (cap > 1 ? 1 : 0), 0, maxScroll);
    }

    private void RefreshLibraryCategoryInfos()
    {
        var list = new List<LibraryCategoryInfo>(LibraryCategories.Count + 2);
        int activeCount = 0, trashCount = 0;
        foreach (var e in LibraryAllEntries)
        {
            if (e.InTrash) trashCount++; else activeCount++;
        }
        list.Add(new LibraryCategoryInfo { Id = "all", Name = "全部", Count = activeCount });
        foreach (var c in LibraryCategories)
        {
            int n = 0;
            foreach (var e in LibraryAllEntries)
                if (!e.InTrash && e.CategoryId == c.Id) n++;
            list.Add(new LibraryCategoryInfo
            {
                Id = c.Id, Name = c.Name, Count = n, Pinned = c.Pinned
            });
        }
        // 回收站是虚拟分类：不写进 categories.txt，但界面要能选到它。
        list.Add(new LibraryCategoryInfo
        {
            Id = ShapeLibrary.TrashCategory, Name = ShapeLibrary.TrashName, Count = trashCount
        });
        _libraryCategoryInfos = list.ToArray();
    }

}
