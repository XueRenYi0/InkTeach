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
    internal void ToggleLibraryOrientation()
    {
        float dpi = UiUnit;
        var p = LibraryPanelRectNow();
        float cx = (p.MinX + p.MaxX) * 0.5f;
        float cy = (p.MinY + p.MaxY) * 0.5f + ViewOffsetY;   // 屏幕坐标
        LibraryMode = LibraryMode == LibraryPanelMode.Horizontal
            ? LibraryPanelMode.Vertical : LibraryPanelMode.Horizontal;
        // 换横竖后，旧的“按页签高度”已经不适用；两页都回到新方向的默认高度。
        _libraryHeightShapes = 0f;
        _libraryHeightFavorites = 0f;
        float w = (LibraryMode == LibraryPanelMode.Horizontal ? 752f : 384f) * dpi;
        float h = DefaultTabHeightLogical(LibraryTab) * dpi;
        var desired = new RectF { MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
                                  MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f };
        var screen = UiLogicalScreen;
        var ui = UiBarBoundsNow();
        float uiTop = ui.IsEmpty ? screen.MaxY - 80f : ui.MinY;
        var snapped = LibraryLayout.Snap(desired, dpi, LibraryMode, LibraryLayout.CellWLogical,
            LibraryTab == LibraryTab.Shapes ? LibraryLayout.ShapeCellHLogical : LibraryLayout.CellHLogical);
        snapped = ClampLibraryPanel(snapped, screen, uiTop, dpi, LibraryMode);
        _libraryPanelLogical = new RectF { MinX = snapped.MinX / dpi, MinY = snapped.MinY / dpi,
                                           MaxX = snapped.MaxX / dpi, MaxY = snapped.MaxY / dpi };
        var lm = LibraryPanelInfoNow();
        ClampLibraryScroll();
        EnsureLibraryTabVisible();
        SaveLibraryPanelPrefs();
        _dirty = true;
    }

    /// <summary>点选卡片（触摸滚动里"没滚 = 点了一下"走这里，抬手才选）。</summary>
    private void SelectLibraryCardForTap(ShapeLibrary.Entry entry)
    {
        LibraryUseSelected = entry;
        LibrarySelected.Clear();
        _libraryPending = null;
        _libraryPendingTitle = null;
        LibraryConfirm = "";
        LibraryCategoriesOpen = false;
        LibraryDrawerManage = false;
        LibraryMenu = 0;
        if (entry == null)
        {
            LibraryHover = -1;
        }
        else
        {
            SetInkStatus(Loc.T("已选中「{0}」：点画布落下，Esc 返回", entry.Title));
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>再点同一张卡 = 放下；点别的卡 = 换一张。</summary>
    private void ToggleLibraryCardForTap(ShapeLibrary.Entry entry)
    {
        if (entry != null && ReferenceEquals(LibraryUseSelected, entry))
        {
            LibraryUseSelected = null;
            _libraryPending = null;
            _libraryPendingTitle = null;
            _dirty = true;
            NotifyUiStateChanged();
            return;
        }
        SelectLibraryCardForTap(entry);
    }

    /// <summary>取消面板移动/缩放拖动（失焦 / 关面板 / 进穿透）。</summary>
    private void CancelLibraryPanelDrag()
    {
        bool had = _libraryDrag != 0 || !_libraryResizePreviewLogical.IsEmpty
                || _libraryCardPress != 0;
        _libraryDrag = 0;
        _libraryResizePreviewLogical = default;
        _libraryResizeCols = 0;
        _libraryResizeRows = 0;
        _libraryCardPress = 0;
        _libraryCardPressEntry = null;
        _libraryCardPressWasSelected = false;
        if (had)
        {
            Native.ReleaseCapture();
            _dirty = true;
        }
    }

    /// <summary>画布上的这个落点能不能放下收藏（面板、工具栏、PPT 条等都不算画布）。</summary>
    private bool LibraryCanvasPointAllowed(float x, float y)
    {
        if (!LibraryPanelVisibleNow) return false;
        if (LibraryLayout.Contains(LibraryPanelRectNow(), x, y)) return false;
        float sy = y + ViewOffsetY;
        if (InUiPopup(x, sy) || UiContains(x, sy) || PptBarContains(x, sy)
            || TimerCardContains(x, sy) || RollCardContains(x, sy)) return false;
        var vp = ViewportCanvas;
        return x >= vp.MinX && x <= vp.MaxX && y >= vp.MinY && y <= vp.MaxY;
    }

    /// <summary>
    /// 收藏对象放到画布上时的矩阵：**包围盒中心对齐指针**，并整体夹进可见区
    /// （包括工具栏上方）。虚影和最终落点都用这一份，保证"看到哪就落在哪"。
    /// </summary>
    internal Matrix3x2 LibraryPlacementMatrix(IReadOnlyList<Stroke> strokes, float x, float y)
    {
        var box = EditRegion.Of(strokes);
        if (box.IsEmpty) return Matrix3x2.CreateTranslation(x, y);

        float halfW = (box.MaxX - box.MinX) * 0.5f;
        float halfH = (box.MaxY - box.MinY) * 0.5f;
        float margin = 8f * UiUnit;
        var vp = ViewportCanvas;
        var ui = UiBarBoundsNow();
        // 拿起卡片时工具栏暂时让位，所以下边界直接到屏幕底；
        // 普通放置才夹在工具条上方，避免落进看不见的面板底下。
        float bottom = LibraryPlacementMode ? vp.MaxY : vp.MaxY - margin;
        // UiBarBounds 是**逻辑**坐标，这里要和 ViewportCanvas 的物理坐标对齐；
        // 少乘一个 UiUnit 会把落点夹到屏幕上半（虚影和放置都只能到上半屏）。
        if (!LibraryPlacementMode && !ui.IsEmpty)
            bottom = MathF.Min(bottom, ui.MinY * UiUnit - ViewOffsetY - margin);

        float minCx = vp.MinX + margin + halfW;
        float maxCx = vp.MaxX - margin - halfW;
        float minCy = vp.MinY + margin + halfH;
        float maxCy = bottom - halfH;
        float cx = minCx > maxCx ? (vp.MinX + vp.MaxX) * 0.5f : Math.Clamp(x, minCx, maxCx);
        float cy = minCy > maxCy ? (vp.MinY + bottom) * 0.5f : Math.Clamp(y, minCy, maxCy);
        return Matrix3x2.CreateTranslation(cx - (box.MinX + box.MaxX) * 0.5f,
                                           cy - (box.MinY + box.MaxY) * 0.5f);
    }

    /// <summary>虚影只在"指针真的在画布上"时显示；移到面板/界面/别的窗口就藏起来。</summary>
    internal bool LibraryGhostVisibleNow
    {
        get
        {
            if (!LibraryPanelVisibleNow || LibraryUseSelected == null || LibrarySelectMode
                || !PointerInside) return false;
            if (LibraryLayout.Contains(LibraryPanelRectNow(), PointerX, PointerY)) return false;
            if (LibraryMenu != 0 && LibraryLayout.Contains(LibraryMenuRect(), PointerX, PointerY))
                return false;
            float sy = PointerY + ViewOffsetY;
            if (InUiPopup(PointerX, sy) || UiContains(PointerX, sy)
                || PptBarContains(PointerX, sy) || TimerCardContains(PointerX, sy)
                || RollCardContains(PointerX, sy)) return false;
            var vp = ViewportCanvas;
            return PointerX >= vp.MinX && PointerX <= vp.MaxX
                && PointerY >= vp.MinY && PointerY <= vp.MaxY;
        }
    }

    /// <summary>
    /// 放下后对象是临时选中的；点空白 / Esc 时取消选中，并回到插入前的工具。
    /// 截屏、激光这种瞬态工具回退到画笔，其余按老师原来的工具恢复。
    /// </summary>
    private void ExitLibraryPlacementSelection()
    {
        if (!_restoreToolAfterLibrarySelection) return;
        var back = _toolBeforeLibraryInsert;
        _restoreToolAfterLibrarySelection = false;
        ClearSelectionForNewContext();
        if (back == Tool.Capture || back == Tool.Laser) back = Tool.Pen;
        SwitchTool(back);
        NotifyUiStateChanged();
        _dirty = true;
    }

    /// <summary>取消触摸滚动（失焦 / 关面板 / 进穿透都要走，别让状态粘住）。</summary>
    private void CancelLibraryTouchScroll()
    {
        bool had = _libraryTouchScrollArmed || _libraryTouchScrolling;
        _libraryTouchScrollArmed = false;
        _libraryTouchScrolling = false;
        _libraryTouchScrollId = 0;
        _libraryTouchTapEntry = null;
        _libraryTouchTapShape = null;
        if (had) Native.ReleaseCapture();
        _dirty = true;
    }

    internal bool LibraryPointerDown(float x, float y, uint pointerType, uint pointerId)
    {
        if (InUiPopup(x, y)) return false;
        float dpi = UiUnit;
        var m = LibraryPanelInfoNow();
        var panel = m.Panel;

        // 一级页签行最右：📌 常驻 / 单次-∞连续。先于菜单和卡片处理。
        // 图钉本体 24×24 偏小，命中区外扩 2px，触摸时更好点。
        if (m.Pin.Inflate(2f * dpi).Contains(x, y))
        {
            if (LibraryMenu != 0) CloseLibraryMenu();
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            ToggleLibraryPinFromUi();
            return true;
        }
        if (LibraryTab == LibraryTab.Shapes && !m.Repeat.IsEmpty && m.Repeat.Contains(x, y))
        {
            if (LibraryMenu != 0) CloseLibraryMenu();
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            bool continuous = m.Tabbed ? m.RepeatContinuous.Contains(x, y)
                                        : !ShapeRepeatContinuous;
            SetShapeRepeatContinuousFromUi(continuous);
            return true;
        }

        if (LibraryCategoryMenuOpen && m.Tabbed)
        {
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
        }
        // 窄面板二级分类下拉：收藏/图形共用；菜单项优先于下面的卡片/页签。
        if (LibraryCategoryMenuOpen)
        {
            var cats = LibraryTabsNow();
            for (int i = 0; i < cats.Length; i++)
                if (LibraryCategoryMenuItemRect(i).Contains(x, y))
                {
                    SetLibraryCategoryById(cats[i].Id);
                    return true;
                }
            if (LibraryCategoryMenuRect().Contains(x, y)) return true;
            bool onPill = m.CurrentCategory.Contains(x, y);
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            _dirty = true;
            if (onPill) return true;   // 再点 pill = 只收起，不要立刻又打开
        }

        // 改名中：只有「✓ / ✕」留给下面的抽屉分支，其余任何点击都先取消。
        if (!string.IsNullOrEmpty(LibraryRenamingId))
        {
            var renRow = LibraryDrawerRowRectForId(LibraryRenamingId);
            bool keep = LibraryCategoriesOpen && !renRow.IsEmpty
                        && (LibraryLayout.DrawerSaveRect(renRow, dpi).Contains(x, y)
                            || LibraryLayout.DrawerCancelRect(renRow, dpi).Contains(x, y));
            if (!keep) EndLibraryCategoryRename(false);
        }

        if (LibraryMenu != 0)
        {
            var menu = LibraryMenuRect();
            var items = LibraryMenuItems();
            if (LibraryLayout.Contains(menu, x, y))
            {
                for (int i = 0; i < items.Count; i++)
                    if (LibraryLayout.Contains(LibraryMenuRowRect(menu, i), x, y))
                    {
                        if (items[i].Enabled) RunLibraryMenu(items[i].Key);
                        return true;
                    }
                return true;
            }
            CloseLibraryMenu();
            return LibraryLayout.Contains(panel, x, y);
        }

        // 分类抽屉：管理分类 / 2 列的快速切换。
        if (LibraryCategoriesOpen)
        {
            var drawer = LibraryDrawerRectNow();
            if (LibraryLayout.Contains(drawer, x, y))
            {
                if (!string.IsNullOrEmpty(LibraryRenamingId))
                {
                    var renRow = LibraryDrawerRowRectForId(LibraryRenamingId);
                    if (!renRow.IsEmpty && LibraryLayout.DrawerSaveRect(renRow, dpi).Contains(x, y))
                    { EndLibraryCategoryRename(true); return true; }
                    if (!renRow.IsEmpty && LibraryLayout.DrawerCancelRect(renRow, dpi).Contains(x, y))
                    { EndLibraryCategoryRename(false); return true; }
                    EndLibraryCategoryRename(false);
                }

                string id = LibraryDrawerCategoryAt(x, y);
                if (id == "__new")
                {
                    LibraryConfirm = "";
                    CreateLibraryCategory();
                    BeginLibraryCategoryRename(LibraryCategoryId);
                    return true;
                }
                if (!string.IsNullOrEmpty(id))
                {
                    var row = LibraryDrawerRowRectForId(id);
                    bool userCat = LibraryLayout.DrawerRowIsUserCategory(id);
                    if (LibraryDrawerManage && userCat
                        && LibraryLayout.DrawerStarRect(row, dpi).Contains(x, y))
                    {
                        bool pinned = false;
                        foreach (var c in _libraryCategoryInfos)
                            if (c.Id == id) { pinned = c.Pinned; break; }
                        ShapeLibrary.SetCategoryPinned(id, !pinned);
                        LibraryCategories = ShapeLibrary.ListCategories();
                        RefreshLibraryCategoryInfos();
                        EnsureLibraryCategoryVisible();
                        EnsureLibraryTabVisible();
                        SetInkStatus(!pinned ? "已标记为常用分类" : "已取消常用标记");
                        _dirty = true;
                        return true;
                    }
                    if (LibraryDrawerManage && userCat
                        && LibraryLayout.DrawerPencilRect(row, dpi).Contains(x, y))
                    { BeginLibraryCategoryRename(id); return true; }
                    if (LibraryDrawerManage && userCat
                        && LibraryLayout.DrawerTrashRect(row, dpi).Contains(x, y))
                    {
                        if (LibraryConfirm == "delcat:" + id)
                        {
                            DeleteLibraryCategory(id);
                        }
                        else
                        {
                            int count = 0;
                            foreach (var c in _libraryCategoryInfos)
                                if (c.Id == id) { count = c.Count; break; }
                            LibraryConfirm = "delcat:" + id;
                            SetInkStatus(Loc.T("再点一次删除分类；{0} 个内容会移到「未分类」", count));
                            _dirty = true;
                        }
                        return true;
                    }
                    LibraryConfirm = "";
                    LibraryCategoriesOpen = false;
                    LibraryDrawerManage = false;
                    SetLibraryCategory(id);
                    return true;
                }
                return true;
            }
            LibraryCategoriesOpen = false;
            LibraryDrawerManage = false;
            LibraryConfirm = "";
            _dirty = true;
        }

        if (!LibraryLayout.Contains(panel, x, y)) return false;

        if (LibraryLayout.ResizeRect(panel, dpi).Contains(x, y))
        {
            StartLibraryDrag(2, x, y);
            return true;
        }

        // 一级页签：图形 / 我的收藏。只换内容，不动位置/大小。
        if (m.ShapesTab.Contains(x, y)) { SetLibraryTab(LibraryTab.Shapes); return true; }
        if (m.FavoritesTab.Contains(x, y)) { SetLibraryTab(LibraryTab.Favorites); return true; }

        // 图形页：分类页签 + 37 张档位卡；点卡换工具并直接设档，面板常驻。
        if (LibraryTab == LibraryTab.Shapes)
        {
            if (m.Close.Contains(x, y)) { CloseLibraryPanel(); return true; }
            var shapeCats = ShapeCatalog.Categories;
            var shapeCards = ShapeCardsNow();
            if (LibraryLayout.MaxScroll(m, shapeCards.Length) > 0.5f)
            {
                var track = LibraryLayout.ScrollTrack(m);
                if (x >= track.MinX - 6f * dpi && x <= track.MaxX + 6f * dpi
                    && y >= track.MinY && y <= track.MaxY)
                {
                    _libraryScrollDrag = true;
                    _libraryScrollDragStartY = y;
                    _libraryScrollDragStart = LibraryScrollY;
                    if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
                    LibraryScrollTo(y);
                    return true;
                }
            }
            if (m.Tabbed && m.Tabs.Contains(x, y))
            {
                var widths = new float[shapeCats.Length];
                for (int i = 0; i < shapeCats.Length; i++)
                    widths[i] = LibraryLayout.TabWidthLogical(shapeCats[i].Name) * dpi;
                int ti = LibraryLayout.TabAt(m, widths, x, y, dpi, LibraryTabScroll);
                if (ti >= 0 && ti < shapeCats.Length) SetShapeCategory(shapeCats[ti].Id);
                else StartLibraryDrag(1, x, y);   // 页签右边的空白也能拖面板
                return true;
            }
            if (!m.Tabbed && m.CurrentCategory.Contains(x, y))
            {
                // 窄面板放不下四个页签：打开同一个分类下拉（不再"点一下跳下一个"）。
                LibraryCategoryMenuOpen = !LibraryCategoryMenuOpen;
                LibraryCategoryMenuHover = -1;
                _dirty = true;
                return true;
            }
            if (m.Grid.Contains(x, y))
            {
                var cards = shapeCards;
                int idx = LibraryLayout.CellAt(m, cards.Length, x, y, LibraryScrollY);
                // 触摸：可滚动时先当“可能滚动”，点一下（没超阈值）才选卡。
                if (pointerType == Native.PT_TOUCH
                    && LibraryLayout.MaxScroll(m, cards.Length) > 0.5f)
                {
                    _libraryTouchScrollArmed = true;
                    _libraryTouchScrolling = false;
                    _libraryTouchScrollId = pointerId;
                    _libraryTouchScrollStart = new Vector2(x, y);
                    _libraryTouchScrollStartY = LibraryScrollY;
                    _libraryTouchTapEntry = null;
                    _libraryTouchTapShape = idx >= 0 && idx < cards.Length ? cards[idx] : null;
                    if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
                    return true;
                }
                if (idx >= 0 && idx < cards.Length)
                {
                    ShapeNameIndex = idx;
                    ShapeNameUntilMs = NowMs + 1500;
                    SetShapePresetFromUi(cards[idx]);
                }
                else StartLibraryDrag(1, x, y);   // 卡片之间的缝 / 最后一行空白 = 拖面板
                return true;
            }
            StartLibraryDrag(1, x, y);
            return true;
        }

        // 右侧很细的滚动条（内容超过一屏才有）。
        if (LibraryLayout.MaxScroll(m, LibraryEntries.Count) > 0.5f)
        {
            var track = LibraryLayout.ScrollTrack(m);
            if (x >= track.MinX - 6f * dpi && x <= track.MaxX + 6f * dpi
                && y >= track.MinY && y <= track.MaxY)
            {
                _libraryScrollDrag = true;
                _libraryScrollDragStartY = y;
                _libraryScrollDragStart = LibraryScrollY;
                if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
                LibraryScrollTo(y);
                return true;
            }
        }

        // ================= 整理模式 =================
        if (LibrarySelectMode)
        {
            var (all, move, del, done) = LibraryLayout.ManageRects(m);
            if (done.Contains(x, y)) { ExitLibraryManage(); return true; }
            if (all.Contains(x, y)) { ToggleSelectAllLibrary(); return true; }
            if (move.Contains(x, y))
            {
                if (LibrarySelected.Count > 0)
                {
                    LibraryMenu = 3;
                    LibraryMenuPage = 0;
                    LibraryConfirm = "";
                    _dirty = true;
                }
                return true;
            }
            if (del.Contains(x, y)) { DeleteLibraryManage(); return true; }
            if (m.Header.Contains(x, y)) return true;

            int idx = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);
            if (idx >= 0 && idx < LibraryEntries.Count)
            {
                var cell = LibraryLayout.CellRect(m, idx, LibraryScrollY);
                var entry = LibraryEntries[idx];
                if (LibraryLayout.GripRect(cell, dpi).Contains(x, y))
                {
                    BeginLibraryManageSort(entry, x, y);
                    return true;
                }
                BeginLibraryManagePress(entry, x, y);
                return true;
            }
            BeginLibraryManagePress(null, x, y);
            return true;
        }

        // ================= 回收站（使用态） =================
        if (LibraryCategoryId == ShapeLibrary.TrashCategory)
        {
            if (m.Menu.Contains(x, y))
            {
                LibraryMenu = 4;
                LibraryMenuPage = 0;
                LibraryConfirm = "";
                _dirty = true;
                return true;
            }
            if (m.Close.Contains(x, y)) { CloseLibraryPanel(); return true; }
            if (m.Header.Contains(x, y)) { SetLibraryCategory(LibraryCategoryIds.All); return true; }
            int idx = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);
            if (idx >= 0 && idx < LibraryEntries.Count)
            {
                string t = LibraryEntries[idx].Title;
                ShapeLibrary.Restore(LibraryEntries[idx]);
                ReloadLibrary();
                SetInkStatus(Loc.T("已恢复「{0}」", t));
                NotifyUiStateChanged();
                return true;
            }
            StartLibraryDrag(1, x, y);
            return true;
        }

        // 顶栏：☰ / 页签（或当前分类）/ ✕
        if (m.Menu.Contains(x, y))
        {
            LibraryMenu = 4;
            LibraryMenuPage = 0;
            LibraryConfirm = "";
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            _dirty = true;
            return true;
        }
        if (m.Close.Contains(x, y)) { CloseLibraryPanel(); return true; }

        if (m.Tabbed)
        {
            var cats = LibraryTabCategories();
            var widths = new float[cats.Length];
            for (int i = 0; i < cats.Length; i++)
                widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * dpi;
            int ti = LibraryLayout.TabAt(m, widths, x, y, dpi, LibraryTabScroll);
            if (ti >= 0 && ti < cats.Length)
            {
                BeginLibraryTabPress(ti, cats[ti].Id, x, y);
                return true;
            }
        }
        else if (m.CurrentCategory.Contains(x, y))
        {
            // 收藏页窄面板：和图形页同一条下拉，不再弹分类抽屉（抽屉只做管理）。
            LibraryCategoryMenuOpen = !LibraryCategoryMenuOpen;
            LibraryCategoryMenuHover = -1;
            _dirty = true;
            return true;
        }

        int hit = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);

        // 触摸：单指在卡片区上下拖 = 滚动，点一下（移动没过阈值）= 选中。
        // 鼠标 / 笔保持原来的立即点选；整理模式下单指仍归框选/排序，不抢。
        if (pointerType == Native.PT_TOUCH && !LibrarySelectMode
            && LibraryLayout.Contains(m.Grid, x, y)
            && LibraryLayout.MaxScroll(m, LibraryEntries.Count) > 0.5f)
        {
            _libraryTouchScrollArmed = true;
            _libraryTouchScrolling = false;
            _libraryTouchScrollId = pointerId;
            _libraryTouchScrollStart = new Vector2(x, y);
            _libraryTouchScrollStartY = LibraryScrollY;
            _libraryTouchTapEntry = (hit >= 0 && hit < LibraryEntries.Count)
                ? LibraryEntries[hit] : null;
            if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
            return true;
        }

        if (hit >= 0 && hit < LibraryEntries.Count)
        {
            var entry = LibraryEntries[hit];
            // 触摸没有 hover：直接点选/再点收起；鼠标/笔要等抬手或拖出阈值，
            // 才能分清“点一下”和“按住拖到画布”。
            if (pointerType == Native.PT_TOUCH)
            {
                ToggleLibraryCardForTap(entry);
                return true;
            }

            _libraryCardPress = 1;
            _libraryCardPressEntry = entry;
            _libraryCardPressStart = new Vector2(x, y);
            _libraryCardPressId = pointerId;
            _libraryCardPressWasSelected = ReferenceEquals(LibraryUseSelected, entry);
            if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
            return true;
        }

        LibraryUseSelected = null;
        _libraryPending = null;
        _libraryPendingTitle = null;
        StartLibraryDrag(1, x, y);
        return true;
    }

    // ================= 指针移动 / 抬起（整理、页签、滚动条） =================

    /// <summary>面板内的专用拖动状态；返回 true = 这一串移动归它。</summary>
    internal bool LibraryPanelPointerMove(float x, float y, uint pointerId)
    {
        if (LibraryCategoryMenuOpen)
        {
            int hov = -1;
            var cats = LibraryTabsNow();
            for (int i = 0; i < cats.Length; i++)
                if (LibraryCategoryMenuItemRect(i).Contains(x, y)) { hov = i; break; }
            if (hov != LibraryCategoryMenuHover)
            {
                LibraryCategoryMenuHover = hov;
                _dirty = true;
            }
            if (LibraryCategoryMenuRect().Contains(x, y)) return true;
        }

        // 从卡片按住往外拖：过阈值才算拖，松手在画布上放置。
        if (_libraryCardPress != 0)
        {
            if (pointerId != _libraryCardPressId) return true;
            if (_libraryCardPress == 1
                && Vector2.Distance(new Vector2(x, y), _libraryCardPressStart) > 6f * UiUnit)
            {
                _libraryCardPress = 2;
                if (_libraryCardPressEntry != null)
                    SelectLibraryCardForTap(_libraryCardPressEntry);
            }
            if (_libraryCardPress == 2)
            {
                ClearEngineTooltip();
                _dirty = true;       // 虚影位置靠 PointerX/Y，重画一帧
            }
            return true;
        }

        // 触摸滚动：第一根手指负责滚动，其它手指吞掉，别落成笔画/第二次点选。
        if (_libraryTouchScrollArmed || _libraryTouchScrolling)
        {
            if (pointerId != _libraryTouchScrollId) return true;
            float dx = x - _libraryTouchScrollStart.X;
            float dy = y - _libraryTouchScrollStart.Y;
            float threshold = 8f * UiUnit;
            if (!_libraryTouchScrolling)
            {
                if (MathF.Abs(dy) >= threshold && MathF.Abs(dy) > MathF.Abs(dx) * 1.1f)
                {
                    _libraryTouchScrolling = true;
                    _libraryTouchTapEntry = null;   // 已经是在滚动，抬手不能当成点选
                    _libraryTouchTapShape = null;
                }
                else if (MathF.Abs(dx) >= threshold)
                {
                    // 横向滑：这次不算点选，也不让面板跟着跑；继续等纵向手势。
                    _libraryTouchTapEntry = null;
                    _libraryTouchTapShape = null;
                }
            }
            if (_libraryTouchScrolling)
            {
                var m = LibraryPanelInfoNow();
                float max = LibraryLayout.MaxScroll(m, LibraryScrollCountNow());
                LibraryScrollY = Math.Clamp(_libraryTouchScrollStartY - dy, 0f, max);
                LibraryHover = -1;
                ClearEngineTooltip();
                _dirty = true;
            }
            return true;
        }

        if (_libraryScrollDrag) { LibraryScrollTo(y); return true; }
        if (_libraryTabPress != 0) { LibraryTabDragMove(x, y); return true; }
        if (LibraryManageSorting) { LibraryManageSortMove(x, y); return true; }
        if (_libraryManagePress != 0) { LibraryManagePressMove(x, y); return true; }

        if (LibraryTab == LibraryTab.Shapes && LastPointerType != Native.PT_TOUCH)
        {
            var m = LibraryPanelInfoNow();
            var cards = ShapeCardsNow();
            int hover = m.Grid.Contains(x, y)
                ? LibraryLayout.CellAt(m, cards.Length, x, y, LibraryScrollY) : -1;
            if (hover != ShapeHoverIndex)
            {
                ShapeHoverIndex = hover;
                _dirty = true;
            }
        }
        return false;
    }

    internal bool LibraryPanelPointerUp(float x, float y, uint pointerId)
    {
        if (_libraryCardPress != 0)
        {
            if (pointerId != _libraryCardPressId) return true;
            bool dragged = _libraryCardPress == 2;
            bool wasSelected = _libraryCardPressWasSelected;
            var entry = _libraryCardPressEntry;
            _libraryCardPress = 0;
            _libraryCardPressEntry = null;
            _libraryCardPressWasSelected = false;
            Native.ReleaseCapture();
            if (dragged && entry != null && LibraryCanvasPointAllowed(x, y))
            {
                LibraryUseSelected = null;
                InsertLibraryGroupAt(new[] { entry }, x, y);
            }
            else if (!dragged)
            {
                // 没拖 = 点了一下：再点同一张收起，点别的换一张。
                if (wasSelected) ToggleLibraryCardForTap(entry);
                else SelectLibraryCardForTap(entry);
            }
            _dirty = true;
            return true;
        }

        if (_libraryTouchScrollArmed || _libraryTouchScrolling)
        {
            if (pointerId != _libraryTouchScrollId) return true;
            bool scrolled = _libraryTouchScrolling;
            var tap = _libraryTouchTapEntry;
            var tapShape = _libraryTouchTapShape;
            _libraryTouchScrollArmed = false;
            _libraryTouchScrolling = false;
            _libraryTouchScrollId = 0;
            _libraryTouchTapEntry = null;
            _libraryTouchTapShape = null;
            Native.ReleaseCapture();
            if (!scrolled)
            {
                if (tapShape != null) SetShapePresetFromUi(tapShape);
                else ToggleLibraryCardForTap(tap);
            }
            _dirty = true;
            return true;
        }
        if (_libraryScrollDrag)
        {
            _libraryScrollDrag = false;
            Native.ReleaseCapture();
            return true;
        }
        if (_libraryTabPress != 0)
        {
            LibraryTabDragEnd(x, y);
            return true;
        }
        if (LibraryManageSorting)
        {
            LibraryManageSortEnd();
            return true;
        }
        if (_libraryManagePress != 0)
        {
            LibraryManagePressEnd(x, y);
            return true;
        }
        return false;
    }

    private void LibraryScrollTo(float y)
    {
        var m = LibraryPanelInfoNow();
        int count = LibraryScrollCountNow();
        float max = LibraryLayout.MaxScroll(m, count);
        var track = LibraryLayout.ScrollTrack(m);
        var thumb = LibraryLayout.ScrollThumb(m, count, LibraryScrollY);
        if (max <= 0.5f || track.IsEmpty) return;
        float thumbH = thumb.IsEmpty ? 22f * UiUnit : thumb.MaxY - thumb.MinY;
        float span = MathF.Max(1f, (track.MaxY - track.MinY) - thumbH);
        float t = Math.Clamp((y - track.MinY - thumbH * 0.5f) / span, 0f, 1f);
        LibraryScrollY = t * max;
        LibraryHover = -1;
        _dirty = true;
    }

    // ---- 页签拖动 / 点击 ----

    private void BeginLibraryTabPress(int index, string id, float x, float y)
    {
        _libraryTabPress = 1;
        _libraryTabDragIndex = index;
        _libraryTabDragId = id;
        _libraryTabDragStartX = x;
        _libraryTabDragStartScroll = LibraryTabScroll;
        LibraryTabDropIndex = -1;
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
    }

    private void LibraryTabDragMove(float x, float y)
    {
        if (_libraryTabPress == 0) return;
        if (_libraryTabPress == 1)
        {
            if (MathF.Abs(x - _libraryTabDragStartX) < 4f * UiUnit) return;
            if (!LibraryLayout.DrawerRowIsUserCategory(_libraryTabDragId))
                return;   // 「全部 / 未分类」固定，不参与排序
            _libraryTabPress = 2;
        }
        var m = LibraryPanelInfoNow();
        var cats = LibraryTabCategories();
        var widths = new float[cats.Length];
        for (int i = 0; i < cats.Length; i++)
            widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * UiUnit;
        int drop = cats.Length;
        for (int i = 0; i < cats.Length; i++)
        {
            if (!LibraryLayout.DrawerRowIsUserCategory(cats[i].Id)) continue;
            var tr = LibraryLayout.TabRect(m, widths, i, UiUnit, LibraryTabScroll);
            if (x < (tr.MinX + tr.MaxX) * 0.5f) { drop = i; break; }
        }
        LibraryTabDropIndex = drop;
        _dirty = true;
    }

    private void LibraryTabDragEnd(float x, float y)
    {
        int state = _libraryTabPress;
        string id = _libraryTabDragId;
        int drop = LibraryTabDropIndex;
        _libraryTabPress = 0;
        LibraryTabDropIndex = -1;
        _libraryTabDragIndex = -1;
        _libraryTabDragId = null;
        Native.ReleaseCapture();
        if (state == 2 && !string.IsNullOrEmpty(id) && LibraryLayout.DrawerRowIsUserCategory(id))
        {
            var cats = LibraryTabCategories();
            string before = drop >= 0 && drop < cats.Length ? cats[drop].Id : null;
            if (before == id) before = null;
            if (ShapeLibrary.ReorderCategory(id, before))
            {
                ReloadLibrary();
                SetLibraryCategory(id);
                SetInkStatus("分类顺序已更新");
            }
        }
        else if (!string.IsNullOrEmpty(id))
        {
            SetLibraryCategory(id);
        }
        _dirty = true;
    }

    // ---- 整理模式：点击 / 框选 ----

    private void BeginLibraryManagePress(ShapeLibrary.Entry entry, float x, float y)
    {
        _libraryManagePress = 1;
        _libraryManageDown = entry;
        _libraryManageStart = new Vector2(x, y);
        LibraryMarqueeActive = false;
        _libraryPending = null;
        _libraryPendingTitle = null;
        LibraryUseSelected = null;
        LibraryConfirm = "";
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
    }

    private void LibraryManagePressMove(float x, float y)
    {
        if (_libraryManagePress == 0) return;
        if (_libraryManagePress == 1)
        {
            if (Vector2.Distance(new Vector2(x, y), _libraryManageStart) < 4f * UiUnit) return;
            _libraryManagePress = 2;
            LibraryMarqueeActive = true;
        }
        var m = LibraryPanelInfoNow();
        LibraryAutoScrollForDrag(m, y);
        var rect = new RectF
        {
            MinX = MathF.Min(_libraryManageStart.X, x),
            MinY = MathF.Min(_libraryManageStart.Y, y),
            MaxX = MathF.Max(_libraryManageStart.X, x),
            MaxY = MathF.Max(_libraryManageStart.Y, y),
        };
        LibraryMarqueeRect = rect;
        var picked = new List<ShapeLibrary.Entry>();
        for (int i = 0; i < LibraryEntries.Count; i++)
        {
            var cell = LibraryLayout.CellRect(m, i, LibraryScrollY);
            if (!cell.Intersects(m.Grid)) continue;
            if (cell.Intersects(rect)) picked.Add(LibraryEntries[i]);
        }
        SetLibrarySelection(picked);
        _dirty = true;
    }

    private void LibraryManagePressEnd(float x, float y)
    {
        int state = _libraryManagePress;
        var down = _libraryManageDown;
        _libraryManagePress = 0;
        _libraryManageDown = null;
        LibraryMarqueeActive = false;
        Native.ReleaseCapture();
        if (state == 1)
        {
            if (down != null) ApplyLibrarySelection(down, !IsLibrarySelected(down));
            else SetLibrarySelection(Array.Empty<ShapeLibrary.Entry>());
        }
        _dirty = true;
    }

    // ---- 整理模式：拖把手排序 ----

    private void BeginLibraryManageSort(ShapeLibrary.Entry entry, float x, float y)
    {
        LibraryManageSorting = true;
        LibraryManageSortEntry = entry;
        LibraryManageDropBefore = null;
        LibraryManageDropIndex = -1;
        LibrarySelected.Clear();
        LibrarySelected.Add(entry);
        if (_windows.Count > 0) Native.SetCapture(_windows[0].Hwnd);
        NotifyUiStateChanged();
        _dirty = true;
    }

    private void LibraryManageSortMove(float x, float y)
    {
        if (!LibraryManageSorting || LibraryManageSortEntry == null) return;
        var m = LibraryPanelInfoNow();
        LibraryAutoScrollForDrag(m, y);
        int moving = LibraryEntries.FindIndex(e => e.Path == LibraryManageSortEntry.Path);
        int idx = LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY);
        int drop;
        if (idx < 0)
        {
            drop = LibraryEntries.Count;
        }
        else
        {
            var cell = LibraryLayout.CellRect(m, idx, LibraryScrollY);
            bool after = x > (cell.MinX + cell.MaxX) * 0.5f;
            drop = idx + (after ? 1 : 0);
        }
        // 落回自己身上 = 不做任何事。
        if (drop == moving || drop == moving + 1) LibraryManageDropIndex = -1;
        else LibraryManageDropIndex = drop;
        LibraryManageDropBefore = drop < LibraryEntries.Count ? LibraryEntries[drop] : null;
        _dirty = true;
    }

    private void LibraryManageSortEnd()
    {
        var entry = LibraryManageSortEntry;
        int drop = LibraryManageDropIndex;
        LibraryManageSorting = false;
        LibraryManageSortEntry = null;
        LibraryManageDropIndex = -1;
        LibraryManageDropBefore = null;
        Native.ReleaseCapture();
        if (entry != null && drop >= 0)
        {
            var before = drop < LibraryEntries.Count ? LibraryEntries[drop] : null;
            if (before != null && before.Path == entry.Path) before = null;
            if (ShapeLibrary.ReorderBefore(new[] { entry }, before))
            {
                ReloadLibrary();
                SetInkStatus("顺序已更新");
            }
        }
        _dirty = true;
    }

    private void LibraryAutoScrollForDrag(in LibraryLayout.Info m, float y)
    {
        float margin = 24f * UiUnit;
        if (y < m.Grid.MinY + margin) LibraryScrollBy(-12f * UiUnit);
        else if (y > m.Grid.MaxY - margin) LibraryScrollBy(12f * UiUnit);
    }

    private void ToggleSelectAllLibrary()
    {
        if (LibraryEntries.Count == 0) return;
        if (LibrarySelected.Count == LibraryEntries.Count)
            LibrarySelected.Clear();
        else
            foreach (var e in LibraryEntries) LibrarySelected.Add(e);
        NotifyUiStateChanged();
        _dirty = true;
    }

    private void DeleteLibraryManage()
    {
        if (LibrarySelected.Count == 0) return;
        if (LibraryCategoryId == ShapeLibrary.TrashCategory
            && LibraryConfirm != "deleteselected")
        {
            LibraryConfirm = "deleteselected";
            SetInkStatus(Loc.T("再点一次：永久删除选中的 {0} 项", LibrarySelected.Count));
            _dirty = true;
            return;
        }
        DeleteLibrarySelection();
        LibraryConfirm = "";
    }

    private void ExitLibraryManage()
    {
        LibrarySelectMode = false;
        LibrarySelected.Clear();
        LibraryMarqueeActive = false;
        LibraryManageSorting = false;
        LibraryManageSortEntry = null;
        LibraryConfirm = "";
        NotifyUiStateChanged();
        _dirty = true;
    }

}
