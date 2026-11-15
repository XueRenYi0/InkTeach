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
    /// <summary>
    /// 把待插入的图库内容落在 (x, y)：**包围盒左上角对齐落点**、原始大小、进文档**一步撤销**、
    /// 插入后**自动选中**——和"粘贴对象"完全同一条路（用户要的就是"点一下就有份能拖的"）。
    /// </summary>
    internal bool TryInsertLibraryAt(float x, float y)
    {
        if (_libraryPending == null) return false;
        var src = _libraryPending;
        string title = _libraryPendingTitle;
        _libraryPending = null;
        _libraryPendingTitle = null;

        var objs = new List<Stroke>(src.Count);
        foreach (var s in src) objs.Add(s.Clone());
        foreach (var s in objs) s.Id = 0;          // 身份重新发（同剪贴板那条教训：撞号会指错对象）

        // 包围盒中心对齐指针，并整体夹进可见区；虚影用同一个矩阵。
        var move = LibraryPlacementMatrix(objs, x, y);
        foreach (var s in objs) s.Transform = s.Transform * move;

        Doc.AddStrokes(objs);
        Doc.SelectOnly(objs);
        // 插入后要能马上拖 / 缩放，所以切到选择工具；否则当前若是画笔，
        // 选中框亮着但一拖就变成写字，老师会觉得“选中了却操作不了”。
        // 记住插入前的工具：点空白取消选中时回到它（荧光笔/橡皮也照原样回）。
        if (Tool != Tool.Marquee || !_restoreToolAfterLibrarySelection)
            _toolBeforeLibraryInsert = Tool;
        // 和粘贴图片同一条口径（见 PasteFromClipboard）。
        SwitchTool(Tool.Marquee);
        _restoreToolAfterLibrarySelection = true;
        // **收起来那一态**：和"刚画完一个图形"一致（见 `_autoSelCollapsed`）——
        // 插进来只是先给个轻的框，要整条操作条点一下圆钮。
        _autoSelCollapsed = true;
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"从图库插入 {objs.Count} 个对象");
        SetInkStatus(string.IsNullOrWhiteSpace(title)
            ? $"已插入收藏（{objs.Count} 个对象）"
            : $"已插入收藏「{title}」（{objs.Count} 个对象）");
        return true;
    }

    /// <summary>屏幕矩形 → 画布矩形。面板位置按**屏幕**记住，绘制/命中按画布。</summary>
    private RectF LibraryScreenToCanvas(in RectF r) => new()
    {
        MinX = r.MinX, MinY = r.MinY - ViewOffsetY,
        MaxX = r.MaxX, MaxY = r.MaxY - ViewOffsetY,
    };

    /// <summary>图库面板这一刻的矩形（**画布坐标**）。绘制、命中、脏区都问它。</summary>
    internal RectF LibraryPanelRectNow()
    {
        float dpi = UiUnit;
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;

        EnsureLibraryPanelPrefs();
        if (_libraryDrag == 2 && !_libraryResizePreviewLogical.IsEmpty)
        {
            // 缩放拖动：外框跟手连续走，不做整格 Snap；卡片布局由 Info 冻结列/行。
            var preview = new RectF
            {
                MinX = _libraryResizePreviewLogical.MinX * dpi,
                MinY = _libraryResizePreviewLogical.MinY * dpi,
                MaxX = _libraryResizePreviewLogical.MaxX * dpi,
                MaxY = _libraryResizePreviewLogical.MaxY * dpi,
            };
            return LibraryScreenToCanvas(
                ClampLibraryPanel(preview, screen, uiTop, dpi, LibraryMode));
        }
        if (_libraryPanelLogical.IsEmpty || _libraryPanelLogical.MaxX <= _libraryPanelLogical.MinX
            || _libraryPanelLogical.MaxY <= _libraryPanelLogical.MinY)
        {
            var def = DefaultLibraryRect(screen, uiTop, dpi, LibraryMode);
            _libraryPanelLogical = new RectF { MinX = def.MinX / dpi, MinY = def.MinY / dpi,
                                               MaxX = def.MaxX / dpi, MaxY = def.MaxY / dpi };
        }

        var desired = new RectF
        {
            MinX = _libraryPanelLogical.MinX * dpi, MinY = _libraryPanelLogical.MinY * dpi,
            MaxX = _libraryPanelLogical.MaxX * dpi, MaxY = _libraryPanelLogical.MaxY * dpi,
        };
        var panel = ClampLibraryPanel(desired, screen, uiTop, dpi, LibraryMode);
        // 宽度和位置两页共用，统一按收藏卡的 84 吸附；图形卡更小，同一个宽度自然就是 9 列。
        panel = LibraryLayout.Snap(panel, dpi, LibraryMode, LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        panel = ClampLibraryPanel(panel, screen, uiTop, dpi, LibraryMode);
        _libraryPanelLogical = new RectF { MinX = panel.MinX / dpi, MinY = panel.MinY / dpi,
                                           MaxX = panel.MaxX / dpi, MaxY = panel.MaxY / dpi };
        // 拿起卡片后连小状态条也不画：整块屏幕交给虚影/落点。
        // 但完整矩形已经存进 _libraryPanelLogical，放下/取消后原样恢复。
        if (LibraryPlacementMode) return RectF.Empty;
        return LibraryScreenToCanvas(panel);
    }

    internal LibraryLayout.Info LibraryPanelInfoNow()
    {
        bool resizing = _libraryDrag == 2 && !_libraryResizePreviewLogical.IsEmpty;
        var m = LibraryLayout.Measure(LibraryPanelRectNow(), UiUnit, LibraryMode,
            resizing ? _libraryResizeCols : 0,
            resizing ? _libraryResizeRows : 0,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellWLogical : LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical,
            showRepeat: LibraryTab == LibraryTab.Shapes);
        if (!m.Tabbed) return m;

        // 页签档：把 CurrentCategory 指到当前页签（抽屉/管理分类定位用）。
        var tabs = LibraryTabsNow();
        string curId = LibraryTab == LibraryTab.Shapes ? ShapeCategoryId : LibraryCategoryId;
        int idx = Array.FindIndex(tabs, t => t.Id == curId);
        if (idx < 0) return m;
        var widths = new float[tabs.Length];
        for (int i = 0; i < tabs.Length; i++)
            widths[i] = LibraryLayout.TabWidthLogical(tabs[i].Name) * UiUnit;
        m = m with { CurrentCategory = LibraryLayout.TabRect(m, widths, idx, UiUnit, LibraryTabScroll) };
        return m;
    }

    internal string LibrarySelectedCategoryId
        => LibraryTab == LibraryTab.Shapes ? ShapeCategoryId : LibraryCategoryId;

    /// <summary>窄面板二级分类下拉：收藏和图形共用；数据只认 LibraryTabsNow。</summary>
    internal RectF LibraryCategoryMenuRect()
    {
        var m = LibraryPanelInfoNow();
        var cats = LibraryTabsNow();
        float dpi = UiUnit;
        int n = Math.Max(1, cats.Length);
        float itemH = 32f * dpi;
        float w = MathF.Max(m.CurrentCategory.MaxX - m.CurrentCategory.MinX, 176f * dpi);
        float h = n * itemH + 8f * dpi;
        float x = m.CurrentCategory.MinX;
        float y = m.CurrentCategory.MaxY + 4f * dpi;
        var panel = LibraryPanelRectNow();
        if (!panel.IsEmpty)
        {
            if (y + h > panel.MaxY - 8f * dpi)
                y = m.CurrentCategory.MinY - h - 4f * dpi;
            y = Math.Clamp(y, panel.MinY + 8f * dpi,
                           MathF.Max(panel.MinY + 8f * dpi, panel.MaxY - 8f * dpi - h));
            x = Math.Clamp(x, panel.MinX + 8f * dpi,
                           MathF.Max(panel.MinX + 8f * dpi, panel.MaxX - 8f * dpi - w));
        }
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    internal RectF LibraryCategoryMenuItemRect(int i)
    {
        var m = LibraryCategoryMenuRect();
        float dpi = UiUnit, itemH = 32f * dpi;
        return new RectF
        {
            MinX = m.MinX + 6f * dpi, MinY = m.MinY + 4f * dpi + i * itemH,
            MaxX = m.MaxX - 6f * dpi, MaxY = m.MinY + 4f * dpi + (i + 1) * itemH,
        };
    }

    internal void SetLibraryCategoryById(string id)
    {
        if (LibraryTab == LibraryTab.Shapes) SetShapeCategory(id);
        else SetLibraryCategory(id);
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        _dirty = true;
    }

    /// <summary>整理操作条：全选 / 移动 / 删除 / 完成。</summary>
    internal (RectF All, RectF Move, RectF Delete, RectF Done) LibraryManageRects()
        => LibraryLayout.ManageRects(LibraryPanelInfoNow());

    /// <summary>旧接口：选中操作条（兼容旧自检；返回 更新/移动/删除/取消）。</summary>
    internal (RectF Update, RectF Move, RectF Delete, RectF Cancel) LibrarySelectionRects()
    {
        var (all, move, del, done) = LibraryManageRects();
        return (all, move, del, done);
    }

    /// <summary>
    /// 「用画布上当前选中的对象更新这条收藏」能不能点。
    /// 条件（沿用 2026-10-25 的规矩）：**只选中一张收藏** ＋ **画布上有选中**。
    /// 页签档把它从 ⋯ 菜单里搬到操作条上（菜单没了），所以需要这个显式的可用性判断。
    /// </summary>
    internal bool CanUpdateFromCanvas => LibrarySelected.Count == 1 && Doc.Selected.Count > 0;

    /// <summary>分类抽屉这一刻的矩形（覆盖在卡片上）。</summary>
    internal RectF LibraryDrawerRectNow()
    {
        var m = LibraryPanelInfoNow();
        int cap = LibraryLayout.CategoryDrawerCapacity(m) - (LibraryDrawerManage ? 1 : 0);
        int shown = Math.Min(Math.Max(1, cap), Math.Max(1, _libraryCategoryInfos.Length));
        var r = LibraryLayout.CategoryDrawerRect(m, shown + (LibraryDrawerManage ? 1 : 0));
        // 抽屉可能比窄面板宽：别伸出屏幕。
        float screenMaxX = UiLogicalScreen.MaxX * UiUnit - 8f * UiUnit;
        if (r.MaxX > screenMaxX)
        {
            float dx = r.MaxX - screenMaxX;
            r = new RectF { MinX = r.MinX - dx, MinY = r.MinY, MaxX = r.MaxX - dx, MaxY = r.MaxY };
        }
        return r;
    }

    private int LibraryDrawerVisibleCount()
    {
        var m = LibraryPanelInfoNow();
        int cap = Math.Max(1, LibraryLayout.CategoryDrawerCapacity(m) - (LibraryDrawerManage ? 1 : 0));
        return Math.Min(cap, Math.Max(1, _libraryCategoryInfos.Length));
    }

    /// <summary>抽屉行的命中：返回分类 id；`"__new"` = 新建；null = 没命中。</summary>
    private string LibraryDrawerCategoryAt(float x, float y)
    {
        var m = LibraryPanelInfoNow();
        var drawer = LibraryDrawerRectNow();
        int shown = LibraryDrawerVisibleCount();
        int first = Math.Clamp(LibraryCategoryScroll, 0, Math.Max(0, _libraryCategoryInfos.Length - shown));
        for (int row = 0; row < shown; row++)
            if (LibraryLayout.CategoryDrawerRowRect(drawer, m.Dpi, row).Contains(x, y))
                return _libraryCategoryInfos[first + row].Id;
        if (LibraryDrawerManage
            && LibraryLayout.CategoryDrawerRowRect(drawer, m.Dpi, shown).Contains(x, y))
            return "__new";
        return null;
    }

    /// <summary>绘制用：分类快照（含「全部」和回收站）。</summary>
    internal LibraryCategoryInfo[] LibraryCategoryListForPaint() => _libraryCategoryInfos;

    /// <summary>☰ → 重置位置与大小：回到默认横排、贴工具条上沿居中。</summary>
    internal void ResetLibraryPlacement()
    {
        CancelLibraryPanelDrag();
        CancelLibraryTouchScroll();
        LibraryMode = LibraryPanelMode.Horizontal;
        _libraryPanelLogical = default;
        _libraryPanelPrefsLoaded = true;
        LibraryScrollY = 0;
        LibraryTabScroll = 0;
        LibraryCategoriesOpen = false;
        LibraryDrawerManage = false;
        LibraryMenu = 0;
        LibraryConfirm = "";
        SaveLibraryPanelPrefs();
        SetInkStatus("收藏弹窗已恢复默认位置");
        _dirty = true;
    }

    /// <summary>自检用：把弹窗位置/大小/方向恢复成默认，避免上一条用例带偏。</summary>
    /// <summary>自检：把面板设成指定逻辑宽度（用于验证 compact 分类下拉）。</summary>
    internal void SetLibraryPanelWidthForTest(float logicalWidth)
    {
        var p = LibraryPanelRectNow();
        float dpi = UiUnit;
        _libraryPanelLogical = new RectF
        {
            MinX = p.MinX / dpi, MinY = p.MinY / dpi,
            MaxX = p.MinX / dpi + logicalWidth, MaxY = p.MaxY / dpi,
        };
        _libraryPanelPrefsLoaded = true;
        _dirty = true;
    }

    internal void ResetLibraryPanelForTest()
    {
        CancelLibraryPanelDrag();
        CancelLibraryTouchScroll();
        _libraryPanelLogical = default;
        LibraryMode = LibraryPanelMode.Horizontal;
        _libraryHeightShapes = 0f;
        _libraryHeightFavorites = 0f;
        _libraryPanelPrefsLoaded = true;
        LibraryPinnedShapes = false;
        LibraryPinnedFavorites = true;
        ShapeRepeatContinuous = false;
        ShapeCategoryId = "all";
        LibraryCategoryId = "all";
        _libraryCategoryRestored = true;     // 自检要确定性：别让上一轮存下的分类把默认带偏
        LibraryPage = 0;
        LibraryScrollY = 0;
        LibraryTabScroll = 0;
        LibraryCategoryScroll = 0;
        LibraryMenu = 0;
        LibraryConfirm = "";
        LibrarySelectMode = false;
        LibraryUseSelected = null;
        LibrarySelected.Clear();
        LibraryCategoriesOpen = false;
        LibraryCategoryMenuOpen = false;
        LibraryCategoryMenuHover = -1;
        LibraryDrawerManage = false;
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        SaveLibraryPanelPrefs();
        _dirty = true;
    }

    /// <summary>
    /// 自检用：**只清内存**里的面板位置 / 每页高度，下一次打开会从盘里重新读
    ///（模拟"关掉软件再开"）。和 <see cref="ResetLibraryPanelForTest"/> 不同，它不写盘。
    /// </summary>
    internal void ReloadLibraryPanelPlacementForTest()
    {
        CancelLibraryPanelDrag();
        CancelLibraryTouchScroll();
        _libraryPanelLogical = default;
        _libraryHeightShapes = 0f;
        _libraryHeightFavorites = 0f;
        _libraryPanelPrefsLoaded = false;
        LibraryPanelOpen = false;
        ShapeCategoryId = "all";
        LibraryCategoryId = "all";
        _libraryCategoryRestored = false;
        _dirty = true;
    }

    private RectF DefaultLibraryRect(in RectF screen, float uiTop, float dpi, LibraryPanelMode mode)
    {
        // 2026-10-26：卡片 72×80 → 84×92 之后重算的默认档。
        // 2026-11-02：顶上加一级页签（30 + 4），默认高度各 +34。
        // 横排 752 = 24 + 8×84 + 7×8；竖排 384 = 24 + 4×84 + 3×8。
        //
        // 2026-11-03：切页签改成**顶边固定**之后，默认高度取两页里更高的那一档
        //（横排 = 图形 5 行 532；竖排 = 收藏 5 行 582）。这样第一次打开任何一页，
        // 顶边都在同一个位置，切页签时菜单栏不会上下跳；矮的那页下方自然留空。
        float hLog = MathF.Max(DefaultTabHeightLogical(LibraryTab.Shapes),
                               DefaultTabHeightLogical(LibraryTab.Favorites));
        float w = (mode == LibraryPanelMode.Horizontal ? 752f : 384f) * dpi;
        float h = hLog * dpi;
        var desired = new RectF
        {
            MinX = (screen.MinX + screen.MaxX) * 0.5f * dpi - w * 0.5f,
            MinY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi - h,
            MaxX = 0, MaxY = 0,
        };
        desired.MaxX = desired.MinX + w;
        desired.MaxY = desired.MinY + h;
        var snapped = LibraryLayout.Snap(desired, dpi, mode,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellWLogical : LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        return ClampLibraryPanel(snapped, screen, uiTop, dpi, mode);
    }

    private RectF ClampLibraryPanel(in RectF rect, in RectF screen, float uiTop, float dpi, LibraryPanelMode mode)
    {
        float minX = screen.MinX * dpi + 8f * dpi;
        float minY = screen.MinY * dpi + 8f * dpi;
        float maxX = screen.MaxX * dpi - 8f * dpi;
        float maxY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi;
        float w = MathF.Min(rect.MaxX - rect.MinX, MathF.Max(72f * dpi, maxX - minX));
        float h = MathF.Min(rect.MaxY - rect.MinY, MathF.Max(72f * dpi, maxY - minY));
        float x = Math.Clamp(rect.MinX, minX, MathF.Max(minX, maxX - w));
        float y = Math.Clamp(rect.MinY, minY, MathF.Max(minY, maxY - h));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    private void EnsureLibraryPanelPrefs()
    {
        if (_libraryPanelPrefsLoaded) return;
        _libraryPanelPrefsLoaded = true;
        LibraryMode = GetUiPref("libMode") == "v" ? LibraryPanelMode.Vertical : LibraryPanelMode.Horizontal;
        // 图钉：图形页默认不常驻，收藏页默认常驻；两个页签各记各的。
        LibraryPinnedShapes = GetUiPref("libPinShapes") == "1";
        LibraryPinnedFavorites = GetUiPref("libPinFav") != "0";
        if (float.TryParse(GetUiPref("libHShapes"), NumberStyles.Float,
                           CultureInfo.InvariantCulture, out float hs) && hs > 60f)
            _libraryHeightShapes = hs;
        if (float.TryParse(GetUiPref("libHFav"), NumberStyles.Float,
                           CultureInfo.InvariantCulture, out float hf) && hf > 60f)
            _libraryHeightFavorites = hf;
        string raw = GetUiPref("libRect");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return;
        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float w)
            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float h)
            && w > 60f && h > 60f)
        {
            // 2026-11-02：面板顶上加了一级页签。老 libRect 没这 34，补一次，
            // 否则老用户打开收藏会只剩 1 行卡片。
            if (GetUiPref("libVer") != "2")
            {
                h += LibraryLayout.TopTabHLogical + LibraryLayout.TopTabGapLogical;
                SetUiPref("libVer", "2");
            }
            _libraryPanelLogical = new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
            // 老配置没有按页签的高度：把这份高度先记给收藏页（启动默认页），
            // 不丢用户以前手动拖过的高度。
            if (_libraryHeightFavorites <= 1f) _libraryHeightFavorites = h;
        }
    }

    private void SaveLibraryPanelPrefs()
    {
        SaveCurrentTabHeight();
        SetUiPref("shapeCat", ShapeCategoryId);
        if (LibraryCategoryId != ShapeLibrary.TrashCategory) SetUiPref("libCat", LibraryCategoryId);
        SetUiPref("libMode", LibraryMode == LibraryPanelMode.Vertical ? "v" : "h");
        SetUiPref("libPinShapes", LibraryPinnedShapes ? "1" : null);
        SetUiPref("libPinFav", LibraryPinnedFavorites ? null : "0");
        SetUiPref("libVer", "2");
        SetUiPref("libHShapes", _libraryHeightShapes > 1f
            ? _libraryHeightShapes.ToString("F1", CultureInfo.InvariantCulture) : null);
        SetUiPref("libHFav", _libraryHeightFavorites > 1f
            ? _libraryHeightFavorites.ToString("F1", CultureInfo.InvariantCulture) : null);
        if (_libraryPanelLogical.IsEmpty) { SetUiPref("libRect", null); return; }
        var r = _libraryPanelLogical;
        SetUiPref("libRect", string.Join(",",
            r.MinX.ToString("F1", CultureInfo.InvariantCulture),
            r.MinY.ToString("F1", CultureInfo.InvariantCulture),
            (r.MaxX - r.MinX).ToString("F1", CultureInfo.InvariantCulture),
            (r.MaxY - r.MinY).ToString("F1", CultureInfo.InvariantCulture)));
    }

    private void StartLibraryDrag(int kind, float x, float y)
    {
        ShapeHoverIndex = -1;
        if (kind == 2)
        {
            // 外框随便变，卡片列/行先冻在这一刻；松手才重排。
            var now = LibraryPanelInfoNow();
            _libraryResizeCols = now.Cols;
            _libraryResizeRows = now.Rows;
            // preview / _libraryPanelLogical 都按**屏幕**逻辑坐标存；now.Panel 是画布。
            _libraryResizePreviewLogical = new RectF
            {
                MinX = now.Panel.MinX / UiUnit,
                MinY = (now.Panel.MinY + ViewOffsetY) / UiUnit,
                MaxX = now.Panel.MaxX / UiUnit,
                MaxY = (now.Panel.MaxY + ViewOffsetY) / UiUnit,
            };
        }
        _libraryDrag = kind;
        _libraryDragStart = new Vector2(x, y + ViewOffsetY);
        _libraryDragStartRect = _libraryPanelLogical;
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
    }

    internal bool LibraryPanelDragMove(float x, float y)
    {
        if (_libraryDrag == 0) return false;
        float dpi = UiUnit;
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;
        // 指针是画布坐标，面板位置是屏幕坐标；先把 y 加回相机偏移。
        float sx = x;
        float sy = y + ViewOffsetY;
        float dx = (sx - _libraryDragStart.X) / dpi;
        float dy = (sy - _libraryDragStart.Y) / dpi;
        var r = _libraryDragStartRect;
        if (_libraryDrag == 1)
        {
            r = new RectF { MinX = r.MinX + dx, MinY = r.MinY + dy,
                            MaxX = r.MaxX + dx, MaxY = r.MaxY + dy };
        }
        else
        {
            // 拖动中允许连续像素，只要不塌到 1×2 以下；松手才吸附整格。
            float w = MathF.Max(LibraryLayout.MinWidthLogical, (r.MaxX - r.MinX) + dx);
            float h = MathF.Max(LibraryLayout.MinHeightLogical, (r.MaxY - r.MinY) + dy);
            r = new RectF { MinX = r.MinX, MinY = r.MinY, MaxX = r.MinX + w, MaxY = r.MinY + h };
        }
        var phys = new RectF { MinX = r.MinX * dpi, MinY = r.MinY * dpi,
                               MaxX = r.MaxX * dpi, MaxY = r.MaxY * dpi };
        phys = ClampLibraryPanel(phys, screen, uiTop, dpi, LibraryMode);
        if (_libraryDrag == 2)
        {
            _libraryResizePreviewLogical = new RectF
            {
                MinX = phys.MinX / dpi, MinY = phys.MinY / dpi,
                MaxX = phys.MaxX / dpi, MaxY = phys.MaxY / dpi,
            };
        }
        else
        {
            _libraryPanelLogical = new RectF { MinX = phys.MinX / dpi, MinY = phys.MinY / dpi,
                                               MaxX = phys.MaxX / dpi, MaxY = phys.MaxY / dpi };
        }
        _dirty = true;
        return true;
    }

    internal void LibraryPanelDragEnd()
    {
        if (_libraryDrag == 0) return;
        int kind = _libraryDrag;
        float dpi = UiUnit;
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;

        // 缩放：拖动中一直是连续外框，松手才吸到**最近**整格。
        // 吸附只改宽高，左上角不动；不在这里再按屏幕边“贴一下”，免得松手整块跳。
        if (kind == 2 && !_libraryResizePreviewLogical.IsEmpty)
        {
            _libraryDrag = 0;
            var raw = new RectF
            {
                MinX = _libraryResizePreviewLogical.MinX * dpi,
                MinY = _libraryResizePreviewLogical.MinY * dpi,
                MaxX = _libraryResizePreviewLogical.MaxX * dpi,
                MaxY = _libraryResizePreviewLogical.MaxY * dpi,
            };
            var settled = LibraryLayout.SnapNearest(raw, dpi, LibraryMode, LibraryLayout.CellWLogical,
                LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
            settled = ClampLibraryPanel(settled, screen, uiTop, dpi, LibraryMode);
            _libraryPanelLogical = new RectF
            {
                MinX = settled.MinX / dpi, MinY = settled.MinY / dpi,
                MaxX = settled.MaxX / dpi, MaxY = settled.MaxY / dpi,
            };
            _libraryResizePreviewLogical = default;
            _libraryResizeCols = 0;
            _libraryResizeRows = 0;
            ClampLibraryScroll();
            EnsureLibraryTabVisible();
            SaveLibraryPanelPrefs();
            _dirty = true;
            return;
        }

        _libraryDrag = 0;
        var p = new RectF { MinX = _libraryPanelLogical.MinX * dpi, MinY = _libraryPanelLogical.MinY * dpi,
                            MaxX = _libraryPanelLogical.MaxX * dpi, MaxY = _libraryPanelLogical.MaxY * dpi };
        bool nearLeft = p.MinX <= screen.MinX * dpi + 40f * dpi;
        bool nearRight = p.MaxX >= screen.MaxX * dpi - 40f * dpi;
        bool nearBottom = p.MaxY >= uiTop * dpi - 40f * dpi;
        bool nearTop = p.MinY <= screen.MinY * dpi + 40f * dpi;
        // **只移动，不改形状**：以前拖到左/右就自动变竖排、拖到上/下就自动变横排，
        // 老师只是想把弹窗挪个位置，卡片却突然全部重排。方向和大小只由右侧
        // 缩放角 + ☰ 里的“切换横排/竖排”决定；这里最多贴边，不改 LibraryMode。
        var desired = new RectF { MinX = p.MinX, MinY = p.MinY, MaxX = p.MaxX, MaxY = p.MaxY };
        var snapped = LibraryLayout.Snap(desired, dpi, LibraryMode, LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        if (nearLeft)
            snapped = new RectF { MinX = screen.MinX * dpi + 8f * dpi, MinY = snapped.MinY,
                                  MaxX = screen.MinX * dpi + 8f * dpi + (snapped.MaxX - snapped.MinX),
                                  MaxY = snapped.MaxY };
        else if (nearRight)
        {
            float nw = snapped.MaxX - snapped.MinX;
            snapped = new RectF { MinX = screen.MaxX * dpi - 8f * dpi - nw, MinY = snapped.MinY,
                                  MaxX = screen.MaxX * dpi - 8f * dpi, MaxY = snapped.MaxY };
        }
        if (nearBottom)
            snapped = new RectF { MinX = snapped.MinX, MinY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi - (snapped.MaxY - snapped.MinY),
                                  MaxX = snapped.MaxX, MaxY = uiTop * dpi - LibraryLayout.GapAboveUiLogical * dpi };
        else if (nearTop)
            snapped = new RectF { MinX = snapped.MinX, MinY = screen.MinY * dpi + 8f * dpi,
                                  MaxX = snapped.MaxX, MaxY = screen.MinY * dpi + 8f * dpi + (snapped.MaxY - snapped.MinY) };
        snapped = ClampLibraryPanel(snapped, screen, uiTop, dpi, LibraryMode);
        _libraryPanelLogical = new RectF { MinX = snapped.MinX / dpi, MinY = snapped.MinY / dpi,
                                           MaxX = snapped.MaxX / dpi, MaxY = snapped.MaxY / dpi };
        var lm = LibraryPanelInfoNow();
        ClampLibraryScroll();
        EnsureLibraryTabVisible();
        SaveLibraryPanelPrefs();
        _dirty = true;
    }

}
