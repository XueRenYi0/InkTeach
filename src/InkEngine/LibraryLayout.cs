using System;

namespace InkEngine;

/// <summary>收藏弹窗的两种形状：横条（贴底/顶）和竖条（贴左/右）。</summary>
internal enum LibraryPanelMode
{
    Horizontal = 0,
    Vertical = 1,
}

/// <summary>统一面板的一级页签（2026-11-02）。</summary>
internal enum LibraryTab
{
    Shapes = 0,
    Favorites = 1,
}

/// <summary>
/// **收藏弹窗的布局**（几何只有这一份：绘制、命中、自检都问它）。
///
/// 2026-10-26 定稿：顶栏 = ☰ + 分类 + ✕；内容 = 整卡网格，按整格缩放，
/// 最小 1×2（一行、两列）；内容滚动（不再分页）；2 列时分类收成「当前分类 ▾」。
/// </summary>
internal static class LibraryLayout
{
    public const float PadLogical = 12f;
    /// <summary>2026-11-02：面板最上面多一行「图形 / 我的收藏」一级页签。</summary>
    public const float TopTabHLogical = 30f;
    public const float TopTabGapLogical = 4f;
    public const float HeaderHLogical = 28f;
    public const float CellWLogical = 84f;
    public const float CellHLogical = 92f;
    /// <summary>图形页的卡比收藏小一档：72×82，9 列 × 5 行刚好 37 张。</summary>
    public const float ShapeCellWLogical = 72f;
    public const float ShapeCellHLogical = 82f;
    public const float CellGapLogical = 8f;
    public const float RowGapLogical = 8f;
    public const float GapAboveUiLogical = 12f;

    public const float IconBtnLogical = 24f;
    public const int MinCols = 2;
    public const int MinRows = 1;
    public const float TabGapLogical = 12f;

    /// <summary>最小 1×2（一行、两列）时的面板尺寸（逻辑像素）。</summary>
    public const float MinWidthLogical = PadLogical * 2 + MinCols * CellWLogical
                                      + (MinCols - 1) * CellGapLogical;
    public const float MinHeightLogical = PadLogical * 2 + TopTabHLogical + TopTabGapLogical
                                       + HeaderHLogical + 4f
                                       + MinRows * CellHLogical
                                       + (MinRows - 1) * RowGapLogical;

    internal readonly struct Info
    {
        public RectF Panel { get; init; }
        /// <summary>一级页签整行：图形 / 我的收藏。</summary>
        public RectF TopTabs { get; init; }
        public RectF ShapesTab { get; init; }
        public RectF FavoritesTab { get; init; }
        /// <summary>面板常驻图钉（一级页签行最右）。</summary>
        public RectF Pin { get; init; }
        /// <summary>「单次 / ∞ 连续」两段开关（只有图形页有；收藏页为 Empty）。</summary>
        public RectF Repeat { get; init; }
        public RectF RepeatSingle { get; init; }
        public RectF RepeatContinuous { get; init; }
        public RectF Header { get; init; }
        public RectF Body { get; init; }
        public RectF Grid { get; init; }

        public RectF Menu { get; init; }
        public RectF Close { get; init; }
        /// <summary>页签可视区；2 列时为 Empty。</summary>
        public RectF Tabs { get; init; }
        /// <summary>2 列时的「当前分类 ▾」；3 列以上为当前页签（引擎补上）。</summary>
        public RectF CurrentCategory { get; init; }
        public RectF Select { get; init; }
        public RectF Trash { get; init; }
        public RectF Plus { get; init; }
        public RectF More { get; init; }
        public RectF Orientation { get; init; }
        public RectF PagePrev { get; init; }
        public RectF PageNext { get; init; }
        public bool Compact => !Tabbed;
        public bool Compact1 => false;

        public float Dpi { get; init; }
        public LibraryPanelMode Mode { get; init; }
        /// <summary>当前页签的卡片尺寸（物理像素）。收藏 84×92，图形 72×82。</summary>
        public float CellW { get; init; }
        public float CellH { get; init; }
        public int Cols { get; init; }
        public int Rows { get; init; }
        public int PageSize => Math.Max(1, Cols * Rows);
        public bool Vertical => Mode == LibraryPanelMode.Vertical;

        /// <summary>3 列以上走页签；2 列走「当前分类 ▾」。292 = 3 张 84 的卡 + 2 个 8 缝 + 24 内边距。</summary>
        public bool Tabbed => (Panel.MaxX - Panel.MinX) >= 292f * Dpi - 0.5f;
    }

    public static bool Contains(in RectF r, float x, float y)
        => x >= r.MinX && x <= r.MaxX && y >= r.MinY && y <= r.MaxY;

    /// <summary>给定行数时面板应该多高（含一级页签 + 二级顶栏）。</summary>
    public static float HeightForRows(int rows, float dpi, float cellHLogical = CellHLogical)
        => PadLogical * 2 * dpi
           + (TopTabHLogical + TopTabGapLogical + HeaderHLogical + 4f) * dpi
           + rows * cellHLogical * dpi + Math.Max(0, rows - 1) * RowGapLogical * dpi;

    /// <summary>把任意大小吸附到“标题栏 + 整数张卡”的尺寸；只改宽高，不动左上角。</summary>
    public static RectF Snap(in RectF desired, float dpi, LibraryPanelMode mode,
                             float cellWLogical = CellWLogical, float cellHLogical = CellHLogical)
    {
        float pad = PadLogical * dpi;
        float gap = CellGapLogical * dpi;
        float rowGap = RowGapLogical * dpi;
        float w = desired.MaxX - desired.MinX;
        float h = desired.MaxY - desired.MinY;
        int cols = Math.Max(1, (int)MathF.Floor((w - pad * 2 + gap) / (cellWLogical * dpi + gap) + 0.01f));
        int rows = Math.Max(1, (int)MathF.Floor((h - pad * 2
                                                - (TopTabHLogical + TopTabGapLogical + HeaderHLogical + 4f) * dpi
                                                + rowGap) / (cellHLogical * dpi + rowGap) + 0.01f));
        // 只保底 2×2；另一方向交给老师拖到屏幕/工具条边界为止。
        // 以前横排最多 3 行、竖排最多 3 列，横排往下拖会被硬截，手感像“拖不动”。
        cols = Math.Max(MinCols, cols);
        rows = Math.Max(MinRows, rows);
        float sw = pad * 2 + cols * cellWLogical * dpi + (cols - 1) * gap;
        float sh = pad * 2 + (TopTabHLogical + TopTabGapLogical + HeaderHLogical + 4f) * dpi
                   + rows * cellHLogical * dpi + (rows - 1) * rowGap;
        return new RectF { MinX = desired.MinX, MinY = desired.MinY,
                           MaxX = desired.MinX + sw, MaxY = desired.MinY + sh };
    }

    /// <summary>
    /// 松手时用：吸到**最近**的整格，而不是像 <see cref="Snap"/> 那样向下取整。
    /// 拖动中预览保持连续，只有松手这一下才重排卡片。
    /// </summary>
    public static RectF SnapNearest(in RectF desired, float dpi, LibraryPanelMode mode,
                                    float cellWLogical = CellWLogical, float cellHLogical = CellHLogical)
    {
        float pad = PadLogical * dpi;
        float gap = CellGapLogical * dpi;
        float rowGap = RowGapLogical * dpi;
        float w = desired.MaxX - desired.MinX;
        float h = desired.MaxY - desired.MinY;
        int cols = Math.Max(1, (int)MathF.Round((w - pad * 2 + gap) / (cellWLogical * dpi + gap)));
        int rows = Math.Max(1, (int)MathF.Round((h - pad * 2
                                                - (TopTabHLogical + TopTabGapLogical + HeaderHLogical + 4f) * dpi
                                                + rowGap) / (cellHLogical * dpi + rowGap)));
        // 只保底 2×2；另一方向交给老师拖到屏幕/工具条边界为止。
        cols = Math.Max(MinCols, cols);
        rows = Math.Max(MinRows, rows);
        float sw = pad * 2 + cols * cellWLogical * dpi + (cols - 1) * gap;
        float sh = pad * 2 + (TopTabHLogical + TopTabGapLogical + HeaderHLogical + 4f) * dpi
                   + rows * cellHLogical * dpi + (rows - 1) * rowGap;
        return new RectF { MinX = desired.MinX, MinY = desired.MinY,
                           MaxX = desired.MinX + sw, MaxY = desired.MinY + sh };
    }

    /// <param name="forceCols">拖动缩放时冻结列数；0 = 按面板宽度重新算。</param>
    /// <param name="forceRows">拖动缩放时冻结行数；0 = 按面板高度重新算。</param>
    public static Info Measure(in RectF panel, float dpi, LibraryPanelMode mode,
                               int forceCols = 0, int forceRows = 0,
                               float cellWLogical = CellWLogical, float cellHLogical = CellHLogical,
                               bool showRepeat = false)
    {
        float pad = PadLogical * dpi;
        float headerH = HeaderHLogical * dpi;
        float gap = CellGapLogical * dpi;
        float rowGap = RowGapLogical * dpi;
        float cellW = cellWLogical * dpi;
        float cellH = cellHLogical * dpi;
        float icon = IconBtnLogical * dpi;
        bool tabbed = (panel.MaxX - panel.MinX) >= 292f * dpi - 0.5f;

        // 一级页签行最右：📌 常驻 + （图形页）单次/∞ 连续。
        // 这两个是面板级开关，放在最上面一行；二级顶栏仍留给分类，窄面板也不挤。
        float topRowH = TopTabHLogical * dpi;
        float topRowY = panel.MinY + pad;
        var pin = new RectF
        {
            MinX = panel.MaxX - pad - icon,
            MinY = topRowY + (topRowH - icon) * 0.5f,
            MaxX = panel.MaxX - pad,
            MaxY = topRowY + (topRowH + icon) * 0.5f,
        };
        var repeat = RectF.Empty;
        if (showRepeat)
        {
            float repeatW = (tabbed ? 88f : 50f) * dpi;
            repeat = new RectF
            {
                MinX = pin.MinX - 4f * dpi - repeatW,
                MinY = pin.MinY,
                MaxX = pin.MinX - 4f * dpi,
                MaxY = pin.MaxY,
            };
        }
        float topTabsRight = (showRepeat ? repeat.MinX : pin.MinX) - 8f * dpi;
        var topTabs = new RectF
        {
            MinX = panel.MinX + pad, MinY = topRowY,
            MaxX = MathF.Max(panel.MinX + pad + 80f * dpi, topTabsRight),
            MaxY = topRowY + topRowH,
        };
        float topGap = 6f * dpi;
        float topTabW = MathF.Max(36f * dpi, (topTabs.MaxX - topTabs.MinX - topGap) * 0.5f);
        var shapesTab = new RectF
        {
            MinX = topTabs.MinX, MinY = topTabs.MinY,
            MaxX = topTabs.MinX + topTabW, MaxY = topTabs.MaxY,
        };
        var favoritesTab = new RectF
        {
            MinX = shapesTab.MaxX + topGap, MinY = topTabs.MinY,
            MaxX = topTabs.MaxX, MaxY = topTabs.MaxY,
        };
        var repeatSingle = RectF.Empty;
        var repeatContinuous = RectF.Empty;
        if (!repeat.IsEmpty)
        {
            if (tabbed)
            {
                float mid = (repeat.MinX + repeat.MaxX) * 0.5f;
                repeatSingle = new RectF { MinX = repeat.MinX, MinY = repeat.MinY,
                                           MaxX = mid, MaxY = repeat.MaxY };
                repeatContinuous = new RectF { MinX = mid, MinY = repeat.MinY,
                                               MaxX = repeat.MaxX, MaxY = repeat.MaxY };
            }
            else
            {
                // 窄面板只画一颗按钮：点整颗切换。
                repeatSingle = repeat;
                repeatContinuous = repeat;
            }
        }
        var header = new RectF
        {
            MinX = panel.MinX + pad, MinY = topTabs.MaxY + TopTabGapLogical * dpi,
            MaxX = panel.MaxX - pad, MaxY = topTabs.MaxY + TopTabGapLogical * dpi + headerH,
        };
        float cy = (header.MinY + header.MaxY) * 0.5f;
        var menu = Sq(header.MinX, cy, icon);
        var close = Sq(header.MaxX - icon, cy, icon);
        var tabs = RectF.Empty;
        var current = RectF.Empty;
        if (tabbed)
        {
            tabs = new RectF
            {
                MinX = menu.MaxX + 8f * dpi,
                MinY = header.MinY,
                MaxX = MathF.Max(menu.MaxX + 40f * dpi, close.MinX - 8f * dpi),
                MaxY = header.MaxY,
            };
        }
        else
        {
            current = new RectF
            {
                MinX = menu.MaxX + 6f * dpi,
                MinY = header.MinY,
                MaxX = MathF.Max(menu.MaxX + 46f * dpi, close.MinX - 6f * dpi),
                MaxY = header.MaxY,
            };
        }

        var body = new RectF
        {
            MinX = panel.MinX + pad, MinY = header.MaxY + 4f * dpi,
            MaxX = panel.MaxX - pad, MaxY = panel.MaxY - pad,
        };
        // +0.01???????????? 5 ??? 4.999 ?? floor ? 4?
        int cols = Math.Max(MinCols,
            (int)MathF.Floor((body.MaxX - body.MinX + gap) / (cellW + gap) + 0.01f));
        int rows = Math.Max(MinRows,
            (int)MathF.Floor((body.MaxY - body.MinY + rowGap) / (cellH + rowGap) + 0.01f));
        if (forceCols > 0) cols = forceCols;
        if (forceRows > 0) rows = forceRows;
        return new Info
        {
            Panel = panel, TopTabs = topTabs, ShapesTab = shapesTab, FavoritesTab = favoritesTab,
            Pin = pin, Repeat = repeat, RepeatSingle = repeatSingle,
            RepeatContinuous = repeatContinuous,
            Header = header, Body = body, Grid = body,
            Menu = menu, Close = close, Tabs = tabs, CurrentCategory = current,
            Dpi = dpi, Mode = mode, CellW = cellW, CellH = cellH, Cols = cols, Rows = rows,
        };
    }

    private static RectF Sq(float x, float cy, float s)
        => new() { MinX = x, MinY = cy - s * 0.5f, MaxX = x + s, MaxY = cy + s * 0.5f };

    // ---- 分类页签 ---------------------------------------------------------

    /// <summary>页签宽度按字数估（中文一字约 13，西文约 7；统一按 13，宁宽勿挤）。</summary>
    public static float TabWidthLogical(string name)
    {
        int n = string.IsNullOrEmpty(name) ? 0 : name.Length;
        return Math.Clamp(20f + n * 13f, 44f, 140f);
    }

    public static float TabsWidth(IReadOnlyList<float> widths, float dpi)
    {
        float gap = TabGapLogical * dpi;
        float w = 0;
        for (int i = 0; i < widths.Count; i++) w += widths[i] + (i == 0 ? 0 : gap);
        return w;
    }

    public static RectF TabRect(in Info m, IReadOnlyList<float> widths, int index,
                                float dpi, float scrollX)
    {
        float gap = TabGapLogical * dpi;
        float x = m.Tabs.MinX - scrollX;
        for (int i = 0; i < index && i < widths.Count; i++) x += widths[i] + gap;
        float w = index >= 0 && index < widths.Count ? widths[index] : 0f;
        return new RectF { MinX = x, MinY = m.Tabs.MinY, MaxX = x + w, MaxY = m.Tabs.MaxY };
    }

    public static int TabAt(in Info m, IReadOnlyList<float> widths, float x, float y,
                            float dpi, float scrollX)
    {
        if (!Contains(m.Tabs, x, y)) return -1;
        float gap = TabGapLogical * dpi;
        float cx = m.Tabs.MinX - scrollX;
        for (int i = 0; i < widths.Count; i++)
        {
            float right = cx + widths[i];
            if (x >= cx && x <= right && right <= m.Tabs.MaxX + 0.5f) return i;
            cx = right + gap;
        }
        return -1;
    }

    // ---- 滚动内容 ---------------------------------------------------------

    public static float ContentHeight(in Info m, int count)
    {
        if (count <= 0) return 0f;
        int cols = Math.Max(1, m.Cols);
        int rows = (count + cols - 1) / cols;
        float cellH = m.CellH;
        float rowGap = RowGapLogical * m.Dpi;
        return rows * cellH + (rows - 1) * rowGap;
    }

    public static float MaxScroll(in Info m, int count)
        => MathF.Max(0f, ContentHeight(m, count) - (m.Grid.MaxY - m.Grid.MinY));

    public static RectF CellRect(in Info m, int index, float scrollY = 0f)
    {
        int col = index % Math.Max(1, m.Cols);
        int row = index / Math.Max(1, m.Cols);
        float gap = CellGapLogical * m.Dpi;
        float w = m.CellW, h = m.CellH;
        float x = m.Grid.MinX + col * (w + gap);
        float y = m.Grid.MinY - scrollY + row * (h + RowGapLogical * m.Dpi);
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>指针落在第几张卡（已按滚动换算）；-1 = 空白 / 不在网格里。</summary>
    public static int CellAt(in Info m, int count, float x, float y, float scrollY = 0f)
    {
        if (!Contains(m.Grid, x, y)) return -1;
        float gap = CellGapLogical * m.Dpi;
        float w = m.CellW, h = m.CellH;
        float contentY = y + scrollY - m.Grid.MinY;
        if (contentY < 0) return -1;
        int row = (int)MathF.Floor(contentY / (h + RowGapLogical * m.Dpi));
        if (row < 0) return -1;
        float localY = contentY - row * (h + RowGapLogical * m.Dpi);
        if (localY > h) return -1;
        int col = (int)MathF.Floor((x - m.Grid.MinX) / (w + gap));
        if (col < 0 || col >= m.Cols) return -1;
        float localX = x - m.Grid.MinX - col * (w + gap);
        if (localX > w) return -1;
        int idx = row * m.Cols + col;
        return idx >= 0 && idx < count ? idx : -1;
    }

    // ????????????????????
    public static int PageCount(int count, in Info m)
        => Math.Max(1, (count + m.PageSize - 1) / m.PageSize);
    public static int PageStart(int page, int count, in Info m)
        => Math.Clamp(page, 0, PageCount(count, m) - 1) * m.PageSize;
    public static int PageVisibleCount(int page, int count, in Info m)
        => Math.Clamp(count - PageStart(page, count, m), 0, m.PageSize);
    public static RectF TrashBackRect(in Info m, float dpi)
        => new() { MinX = m.Header.MinX + m.Menu.MaxX - m.Header.MinX, MinY = m.Header.MinY,
                   MaxX = m.Header.MinX + 120f * dpi, MaxY = m.Header.MaxY };
    public static RectF TrashClearRect(in Info m, float dpi)
        => new() { MinX = m.Close.MinX - 80f * dpi, MinY = m.Header.MinY,
                   MaxX = m.Close.MinX - 8f * dpi, MaxY = m.Header.MaxY };

    public static RectF ScrollTrack(in Info m)
    {
        float w = 4f * m.Dpi;
        float right = m.Panel.MaxX - 6f * m.Dpi;
        return new RectF { MinX = right - w, MinY = m.Grid.MinY,
                           MaxX = right, MaxY = m.Grid.MaxY };
    }

    public static RectF ScrollThumb(in Info m, int count, float scrollY)
    {
        var track = ScrollTrack(m);
        float max = MaxScroll(m, count);
        if (max <= 0.5f) return RectF.Empty;
        float view = m.Grid.MaxY - m.Grid.MinY;
        float content = ContentHeight(m, count);
        float h = MathF.Max(22f * m.Dpi, view * view / MathF.Max(1f, content));
        float t = Math.Clamp(scrollY / max, 0f, 1f);
        float y = track.MinY + t * MathF.Max(0f, (track.MaxY - track.MinY) - h);
        return new RectF { MinX = track.MinX, MinY = y, MaxX = track.MaxX, MaxY = y + h };
    }

    // ---- 卡片角标 / 操作条 -------------------------------------------------

    /// <summary>整理模式的勾选框：右上角。</summary>
    public static RectF CheckRect(in RectF cell, float dpi)
    {
        float s = 16f * dpi;
        return new RectF { MinX = cell.MaxX - s - 2f * dpi, MinY = cell.MinY + 2f * dpi,
                           MaxX = cell.MaxX - 2f * dpi, MaxY = cell.MinY + s + 2f * dpi };
    }

    /// <summary>整理模式的排序把手：右下角（拖它才排序，点卡身是勾选/框选）。</summary>
    public static RectF GripRect(in RectF cell, float dpi)
    {
        float s = 18f * dpi;
        return new RectF { MinX = cell.MaxX - s - 2f * dpi, MinY = cell.MaxY - s - 2f * dpi,
                           MaxX = cell.MaxX - 2f * dpi, MaxY = cell.MaxY - 2f * dpi };
    }

    public static RectF ResizeRect(in RectF panel, float dpi)
    {
        float s = 18f * dpi;
        return new RectF { MinX = panel.MaxX - s, MinY = panel.MaxY - s, MaxX = panel.MaxX, MaxY = panel.MaxY };
    }

    /// <summary>整理操作条的四颗：全选 / 移动 / 删除 / 完成；占满整条顶栏。</summary>
    public static (RectF All, RectF Move, RectF Delete, RectF Done) ManageRects(in Info m)
    {
        float dpi = m.Dpi;
        float avail = m.Header.MaxX - m.Header.MinX;
        float labelW = MathF.Min(30f * dpi, avail * 0.15f);
        float gap = 2f * dpi;
        float btnW = MathF.Max(24f * dpi, (avail - labelW - 3f * gap) / 4f);
        float x = m.Header.MinX + labelW;
        var all = new RectF { MinX = x, MinY = m.Header.MinY, MaxX = x + btnW, MaxY = m.Header.MaxY };
        x = all.MaxX + gap;
        var move = new RectF { MinX = x, MinY = m.Header.MinY, MaxX = x + btnW, MaxY = m.Header.MaxY };
        x = move.MaxX + gap;
        var del = new RectF { MinX = x, MinY = m.Header.MinY, MaxX = x + btnW, MaxY = m.Header.MaxY };
        x = del.MaxX + gap;
        var done = new RectF { MinX = x, MinY = m.Header.MinY, MaxX = x + btnW, MaxY = m.Header.MaxY };
        return (all, move, del, done);
    }

    // ---- 分类抽屉（管理分类 / 2 列的快速切换） -----------------------------

    public static RectF CategoryDrawerRect(in Info m, int rows)
    {
        float dpi = m.Dpi;
        float w = 210f * dpi;
        float h = 8f * dpi + Math.Max(1, rows) * 30f * dpi;
        float x = m.Vertical ? m.Panel.MinX + PadLogical * dpi : m.CurrentCategory.MinX;
        float minX = m.Panel.MinX + PadLogical * dpi;
        float maxX = m.Panel.MaxX - PadLogical * dpi - w;
        if (m.Panel.MaxX - m.Panel.MinX >= w + PadLogical * dpi * 2f)
            x = Math.Clamp(x, minX, MathF.Max(minX, maxX));
        else
            x = minX;
        float y = m.Header.MaxY + 4f * dpi;
        if (y + h > m.Panel.MaxY - PadLogical * dpi)
            h = MathF.Max(30f * dpi, (m.Panel.MaxY - PadLogical * dpi) - y);
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    public static RectF CategoryDrawerRowRect(in RectF drawer, float dpi, int row)
        => new()
        {
            MinX = drawer.MinX + 4f * dpi, MinY = drawer.MinY + 4f * dpi + row * 30f * dpi,
            MaxX = drawer.MaxX - 4f * dpi, MaxY = drawer.MinY + 4f * dpi + (row + 1) * 30f * dpi,
        };

    public static int CategoryDrawerCapacity(in Info m)
    {
        float top = m.Header.MaxY + 4f * m.Dpi + 8f * m.Dpi;
        float bottom = m.Panel.MaxY - PadLogical * m.Dpi;
        return Math.Max(1, (int)MathF.Floor((bottom - top) / (30f * m.Dpi)));
    }

    public static bool DrawerRowIsUserCategory(string id)
        => !string.IsNullOrEmpty(id)
           && id != "all"
           && id != ShapeLibrary.Uncategorized
           && id != ShapeLibrary.TrashCategory;

    public static RectF DrawerTrashRect(in RectF row, float dpi)
    {
        float s = 22f * dpi;
        float right = row.MaxX - 6f * dpi;
        return new RectF { MinX = right - s, MinY = (row.MinY + row.MaxY - s) * 0.5f,
                           MaxX = right, MaxY = (row.MinY + row.MaxY + s) * 0.5f };
    }

    public static RectF DrawerPencilRect(in RectF row, float dpi)
    {
        var trash = DrawerTrashRect(row, dpi);
        float s = 22f * dpi;
        return new RectF { MinX = trash.MinX - 4f * dpi - s, MinY = trash.MinY,
                           MaxX = trash.MinX - 4f * dpi, MaxY = trash.MaxY };
    }

    /// <summary>「常用」星标：在铅笔左边。</summary>
    public static RectF DrawerStarRect(in RectF row, float dpi)
    {
        var pencil = DrawerPencilRect(row, dpi);
        float s = 22f * dpi;
        return new RectF { MinX = pencil.MinX - 4f * dpi - s, MinY = pencil.MinY,
                           MaxX = pencil.MinX - 4f * dpi, MaxY = pencil.MaxY };
    }

    public static RectF DrawerCancelRect(in RectF row, float dpi) => DrawerTrashRect(row, dpi);

    public static RectF DrawerSaveRect(in RectF row, float dpi)
    {
        var cancel = DrawerCancelRect(row, dpi);
        float s = 22f * dpi;
        return new RectF { MinX = cancel.MinX - 4f * dpi - s, MinY = cancel.MinY,
                           MaxX = cancel.MinX - 4f * dpi, MaxY = cancel.MaxY };
    }

    public static RectF DrawerEditRect(in RectF row, float dpi)
    {
        var save = DrawerSaveRect(row, dpi);
        return new RectF { MinX = row.MinX + 8f * dpi, MinY = row.MinY + 3f * dpi,
                           MaxX = save.MinX - 6f * dpi, MaxY = row.MaxY - 3f * dpi };
    }
}
