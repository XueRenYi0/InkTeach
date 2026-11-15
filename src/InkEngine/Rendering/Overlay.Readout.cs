using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>Process memory sampling, used to attribute the footprint to each
/// stage of start-up rather than quoting a single number.</summary>

internal sealed partial class OverlayWindow
{
    // =====================================================================
    //  多边形读数（规格 9.7）：三角形的三个内角 / 平行四边形的两个夹角
    //
    //  这一组不是"一颗胶囊"，是**好几颗**（最多三颗），所以另有一套"这一帧有哪些胶囊"
    //  的收集函数：FillAnglePills。**画与脏区都只调它一次**——盒子的位置、大小、文案
    //  全在那一份里定死，两边各算一份的话，脏区会按一个大小擦、按另一个大小画，
    //  拖久了屏幕上就留一条边（选中框那一套 UI 踩过这个坑）。
    //
    //  **不再有"内角和"那一行**（用户 2026-09-19 定：显示和太乱）。为它服务的
    //  0.1° 配平也一起撤了（见 SelectionHandles.PolygonAngles）。
    // =====================================================================

    /// <summary>一帧最多几颗角标：三角形三个角 / 平行四边形两个角。</summary>
    private const int MaxAnglePills = 3;

    /// <summary>一颗角标：盒子 + 文案 + 是不是小号（画与脏区共用同一个实例）。</summary>
    private readonly struct AnglePill
    {
        public readonly RectF Rect;
        public readonly string Text;
        public readonly bool Small;
        public AnglePill(in RectF rect, string text, bool small)
        {
            Rect = rect; Text = text; Small = small;
        }
    }

    /// <summary>角标与角度的复用缓冲（每帧重填，不 new，理由同"手柄命中每帧都跑"那条）。</summary>
    private readonly Vector2[] _angleVerts = new Vector2[4];
    private readonly float[] _angleDegs = new float[3];
    private readonly AnglePill[] _anglePills = new AnglePill[MaxAnglePills];

    /// <summary>
    /// 这一帧该画哪些角标（规格 9.7），返回几颗。**绘制与脏区唯一的来源。**
    ///
    /// 摆位规则：每个角标贴在它那个顶点的**外侧**，而且**只往左右放**（不往上下放）。
    /// 两个理由，都是被现场那两样家具逼出来的：
    ///   · 正上方那一块是**旋转柄**的（框上边中点往上 30 逻辑像素，柄 + 连线都在那儿）；
    ///   · 正下方那一块是**操作条**的（框下边往下 14 逻辑像素，而且横跨整个选区宽度）。
    /// 左右放还顺带保证了"不遮住要讲的那条边/那个顶点"：顶点就是图形在那一侧的最外点，
    /// 让到它外面去，两条边和顶点都在视野里。
    ///
    /// 大小：一律小号字（贴在顶点旁边，太大了会压住那条边）。
    /// </summary>
    private int FillAnglePills(InkEngine app, Span<AnglePill> dst)
    {
        int n = app.FillAngleReadout(_angleVerts, _angleDegs);
        if (n <= 0 || dst.Length < n) return 0;

        float dpi = app.DpiScale;
        var small = ReadoutFormatSmall(dpi);

        // 形心：判"这个顶点在图形的哪一侧"，决定角标往左还是往右放。
        // （三角形用三个顶点；平行四边形只报两角、就按那两个顶点的中点分左右，够用。）
        var centroid = Vector2.Zero;
        for (int i = 0; i < n; i++) centroid += _angleVerts[i];
        centroid /= n;

        for (int i = 0; i < n; i++)
        {
            string text = SelectionHandles.FormatAngleDegrees(_angleDegs[i]);
            var place = _angleVerts[i].X >= centroid.X ? PillPlace.RightOf : PillPlace.LeftOf;
            dst[i] = new AnglePill(
                ReadoutPillRect(_angleVerts[i], dpi, text, 22f, 44f, small, place), text, true);
        }
        return n;
    }

    /// <summary>画那一组角标（盒子与文案来自 <see cref="FillAnglePills"/>，和脏区同一份）。</summary>
    private void DrawAnglePills(InkEngine app)
    {
        int n = FillAnglePills(app, _anglePills);
        for (int i = 0; i < n; i++)
            DrawReadoutPill(_anglePills[i].Rect, _anglePills[i].Text, false, _anglePills[i].Small);
    }

    /// <summary>
    /// 倾斜角标签的文案（画它、按它量盒子宽度、进脏区，都走这一个函数）。
    ///
    /// 参数是**那个要显示的 α**（画线中 / 拖直线端点 / 单选直线拖旋转柄三个来源）——
    /// 不在这里读 app 的状态，是因为"该显示哪个来源的 α"由调用点最清楚。
    /// </summary>
    private static string InclinationLabel(float degrees)
        => SelectionHandles.FormatInclination(degrees);

    /// <summary>
    /// 一个方形手柄（白底 + 蓝边）。八个通用手柄和两个端点手柄**共用这一份画法**：
    /// 端点手柄只是位置不同，长得不一样会让用户以为是两种东西。
    /// </summary>
    private void DrawHandleSquare(Vector2 p, float size, float radius, ID2D1SolidColorBrush white)
    {
        var box = new Vortice.RawRectF(p.X - size * 0.5f, p.Y - size * 0.5f,
                                       p.X + size * 0.5f, p.Y + size * 0.5f);
        var rr = new RoundedRectangle(box, radius, radius);
        _ctx.FillRoundedRectangle(rr, white);
        _ctx.DrawRoundedRectangle(rr, _scratch, 1.8f * _app.UiUnit);
    }

    /// <summary>
    /// 画一颗"读数胶囊"（旋转 Δ 与倾斜角 α 共用这一份画法）。
    ///
    /// 抽出来是因为两处必须**长得一模一样**：它们会在同一条直线上先后出现
    /// （先转一下、再拖端点），样式差一点用户就会以为是两种东西。
    /// <paramref name="snapped"/> 为真时整块用强调色——"这个数是吸出来的"。
    /// <paramref name="small"/> 为真时用小一号字（三角形的三个**角标**：
    /// 它们贴在顶点旁边，太大了会压住那条边）。
    /// </summary>
    private void DrawReadoutPill(in RectF label, string text, bool snapped, bool small = false)
    {
        // **这里要的是"缩放倍数"（96 DPI 基准），不是 Dpi 那个原始值**：
        // Dpi 是窗口的物理 DPI（200% 屏上就是 192），拿它去 CreateTextFormat
        // 会造出一个 2880 像素高的字号，字直接糊满整个画面（踩过）。
        // 2026-10-25 起跟选中家具一起乘界面档（UiUnit），大档读数也跟着大。
        float dpi = _app.UiUnit;
        var accent = new Color4(0f, 0.47f, 0.83f, 1f);      // #0078D4
        var box = new Vortice.RawRectF(label.MinX, label.MinY, label.MaxX, label.MaxY);
        float pill = (label.MaxY - label.MinY) * 0.5f;
        var shape = new RoundedRectangle(box, pill, pill);

        _ctx.FillRoundedRectangle(shape, Brush(snapped
            ? new Color4(accent.R, accent.G, accent.B, 0.96f)
            : _app.FloatingTheme.Panel));
        _scratch.Color = snapped
            ? new Color4(1f, 1f, 1f, 0.85f)
            : new Color4(accent.R, accent.G, accent.B, 0.85f);
        _ctx.DrawRoundedRectangle(shape, _scratch, 1.5f * dpi);

        _scratch.Color = snapped
            ? new Color4(1f, 1f, 1f, 1f)
            : _app.FloatingTheme.Text;
        _ctx.DrawText(text, small ? ReadoutFormatSmall(dpi) : ReadoutFormat(dpi),
                      // 注意：Vortice 的 Rect(x, y, width, height) 是"位置 + 尺寸"，
                      // 不是 (left, top, right, bottom)。写错的话文字会被排到很远的
                      // 地方去（居中排版时直接跑到屏幕外），看起来就像"字没画出来"。
                      new Rect(label.MinX, label.MinY,
                               label.MaxX - label.MinX, label.MaxY - label.MinY),
                      _scratch);
    }

    /// <summary>
    /// 度数标签的文字格式（按 DPI 生成并缓存）。
    ///
    /// 绘制时的变换只有平移、没有缩放，所以字号写死就等于"物理像素"——
    /// 15 物理像素在 200% 缩放下只有 7.5 逻辑像素，投影上根本看不清。
    /// </summary>
    /// <summary>
    /// 用 DirectWrite 量一段文字在给定格式下的宽度。给"盒子要跟着文案变长"的地方用
    /// （度数标签、面板底色）。用同一个格式量，量出来的才和画出来的一致。
    /// </summary>
    /// <summary>度数标签的文案（画它、按它量盒子宽度，都走这一个函数）。</summary>
    private static string RotationLabel(InkEngine app) => SelectionHandles.FormatDegrees(app.SelRotationDegrees);

    private static float MeasureTextWidth(string text, IDWriteTextFormat format)
    {
        if (string.IsNullOrEmpty(text) || format == null) return 0f;
        try
        {
            using var layout = Gfx.WriteFactory.CreateTextLayout(text, format, 4096f, 1024f);
            return layout.Metrics.Width;
        }
        catch { return 0f; }        // 量不出来就退回最小宽度，绝不能因为量个宽度把渲染搞挂
    }

    private IDWriteTextFormat ReadoutFormat(float dpi)
    {
        float px = MathF.Max(11f, MathF.Round(15f * dpi));
        if (_readoutFormat == null || _readoutFormatPx != px)
        {
            _readoutFormat?.Dispose();
            _readoutFormat = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _readoutFormat.TextAlignment = TextAlignment.Center;
            _readoutFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutFormatPx = px;
        }
        return _readoutFormat;
    }

    /// <summary>收藏页签那一行字（13 逻辑像素、常规、居中）。见字段处的说明。</summary>
    private IDWriteTextFormat ReadoutFormatTab(float dpi)
    {
        float px = MathF.Max(11f, MathF.Round(13f * dpi));
        if (_readoutTabFormat == null || _readoutTabFormatPx != px)
        {
            _readoutTabFormat?.Dispose();
            _readoutTabFormat = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _readoutTabFormat.TextAlignment = TextAlignment.Center;
            _readoutTabFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutTabFormatPx = px;
        }
        return _readoutTabFormat;
    }

    /// <summary>
    /// **角标**用的小号文字格式（11 逻辑像素，比主读数小一号）。
    ///
    /// 和 <see cref="ReadoutFormat"/> 同一套按 DPI 生成 + 缓存的写法（理由见那里）：
    /// 量盒子和画字必须用**同一个格式**，量出来的宽度才和画出来的一致。
    /// </summary>
    private IDWriteTextFormat ReadoutFormatSmall(float dpi)
    {
        float px = MathF.Max(9f, MathF.Round(11f * dpi));
        if (_readoutFormatSmall == null || _readoutFormatSmallPx != px)
        {
            _readoutFormatSmall?.Dispose();
            _readoutFormatSmall = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _readoutFormatSmall.TextAlignment = TextAlignment.Center;
            _readoutFormatSmall.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutFormatSmall.WordWrapping = WordWrapping.NoWrap;
            _readoutFormatSmallPx = px;
        }
        return _readoutFormatSmall;
    }

    private IDWriteTextFormat _readoutLeft;
    private float _readoutLeftPx;
    private IDWriteTextFormat _panelLabel;
    private float _panelLabelPx;

    /// <summary>左对齐的小号字（收藏弹窗的分类列 / 菜单行）。</summary>
    private IDWriteTextFormat ReadoutFormatLeft(float dpi)
    {
        float px = MathF.Max(9f, MathF.Round(11f * dpi));
        if (_readoutLeft == null || _readoutLeftPx != px)
        {
            _readoutLeft?.Dispose();
            _readoutLeft = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _readoutLeft.TextAlignment = TextAlignment.Leading;
            _readoutLeft.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutLeft.WordWrapping = WordWrapping.NoWrap;
            _readoutLeftPx = px;
        }
        return _readoutLeft;
    }

    /// <summary>属性面板左侧标签（字体/字号/行距/背景）：左对齐 + 垂直居中，和右侧色块同一行高。</summary>
    private IDWriteTextFormat PanelLabelFormat(float dpi)
    {
        float px = MathF.Max(9f, MathF.Round(12f * dpi));
        if (_panelLabel == null || _panelLabelPx != px)
        {
            _panelLabel?.Dispose();
            _panelLabel = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _panelLabel.TextAlignment = TextAlignment.Leading;
            _panelLabel.ParagraphAlignment = ParagraphAlignment.Center;
            // 德语/俄语的“行距/背景”比中文长，固定单行会压到第一格；允许在标签列内折两行。
            _panelLabel.WordWrapping = WordWrapping.Wrap;
            _panelLabelPx = px;
        }
        return _panelLabel;
    }

    /// <summary>
    /// 收藏卡片跟随指针的虚影：只在指针真的在画布上时画，位置和最终落点同源
    /// （<see cref="InkEngine.LibraryPlacementMatrix"/>）。图片按 0.45 透明度画位图，
    /// 其它对象复制一份把颜色 alpha 降下来。
    /// </summary>
    private void DrawLibraryGhost(InkEngine app)
    {
        if (!app.LibraryGhostVisibleNow) return;
        var entry = app.LibraryUseSelected;
        if (entry?.Strokes == null || entry.Strokes.Count == 0) return;

        var move = app.LibraryPlacementMatrix(entry.Strokes, app.PointerX, app.PointerY);
        foreach (var s in entry.Strokes)
        {
            if (s == null || s.HiddenForPairing) continue;
            // 和最终插入同一条路：先把 move 烘进 Transform，再画。
            // 不再用 DrawStroke 的 extra 参数，避免图形/图片两套变换路径在
            // 非单位 Transform 上出现偏差（虚影和鼠标对不上）。
            var ghost = s.Clone();
            ghost.Transform = s.Transform * move;

            if (ghost.IsImage)
            {
                var bmp = ghost.Image?.GetBitmap(_ctx);
                if (bmp == null || ghost.Points.Count < 2) continue;
                var a = new Vector2(ghost.Points[0].X, ghost.Points[0].Y);
                var b = new Vector2(ghost.Points[^1].X, ghost.Points[^1].Y);
                var dst = new Vortice.RawRectF(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                                               MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
                var canvasToWindow = _ctx.Transform;
                _ctx.Transform = ghost.Transform * canvasToWindow;
                _ctx.DrawBitmap(bmp, dst, 0.45f, Vortice.Direct2D1.InterpolationMode.Linear,
                                null, null);
                _ctx.Transform = canvasToWindow;
                continue;
            }

            ghost.Color = new Color4(s.Color.R, s.Color.G, s.Color.B, s.Color.A * 0.45f);
            DrawStroke(ghost);
        }
    }

    /// **图库面板**（"我的图形"）：从工具条上沿往上长的一张卡片，里面是收藏的缩略图墙。
    ///
    /// 为什么这个面板归引擎画（而不是界面）：格子里的内容是**画笔迹**——把条目的包围盒
    /// 等比缩进格子、`DrawStroke(s, fit)` 直接画。这条路现成、零新机制，而且**不落缩略图位图**：
    /// 存的是对象，画的时候就永远和对象一致（参考实现也是"加载时现渲染"，只是它那边得
    /// 造一张 RenderTargetBitmap，我们连位图都不用建）。
    /// 代价：每帧重画 N 个格子里的笔画——格子最多 16 个、每个条目几个对象，忽略不计。
    ///
    /// 画法与操作条/其它面板同一套：`DrawPanelCard` 卡片 ＋ 主题色，圆角取界面推上来的
    /// `CornerRadius`（三块东西必须是一家）。
    /// </summary>
    /// <summary>带投影的浮层脏区外扩：4px 余量 + 投影最远半径；unit = 逻辑→物理的倍数。</summary>
    private static float FloatingDirtyPad(InkEngine app, float unit)
        => (4f + app.FloatingTheme.ShadowReachLogical) * unit;

    private void DrawLibraryPanel(InkEngine app)
    {
        if (!app.LibraryPanelVisibleNow || app.LibraryPlacementMode)
        {
            // 全屏层 / 截图 / 放映 / 拿起卡片：只藏，不销毁。改名输入框也一起收。
            if (!string.IsNullOrEmpty(app.LibraryRenamingId)) app.EndLibraryCategoryRename(false);
            return;
        }
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        var m = app.LibraryPanelInfoNow();
        var panel = m.Panel;
        int n = app.LibraryEntries.Count;

        float radius = MathF.Min(theme.CornerRadius * dpi, 12f * dpi);
        DrawPanelCard(app, panel, radius, dpi);
        DrawLibraryTopTabs(app, m, dpi, theme);

        if (app.LibraryTab == LibraryTab.Shapes)
        {
            DrawShapeLibraryPage(app, m, dpi, theme);
            if (app.LibraryCategoryMenuOpen && !m.Tabbed) DrawLibraryCategoryMenu(app, dpi, theme);
            return;
        }

        var head = m.Header;
        bool trashView = app.LibraryCategoryId == ShapeLibrary.TrashCategory;
        string catName = "全部";
        int catCount = 0;
        foreach (var c in app.LibraryCategoryListForPaint())
            if (c.Id == app.LibraryCategoryId) { catName = c.Name; catCount = c.Count; break; }

        if (app.LibrarySelectMode)
        {
            // 整理：顶栏整条变成操作条（全选 / 移动 / 删除 / 完成）。
            var (all, move, del, done) = LibraryLayout.ManageRects(m);
            _scratch.Color = theme.Text;
            _ctx.DrawText(Loc.T("已选 {0}", app.LibrarySelectionCount), ReadoutFormatSmall(dpi),
                          new Rect(head.MinX, head.MinY, MathF.Max(10f, all.MinX - head.MinX),
                                   head.MaxY - head.MinY), _scratch);
            DrawLibPill(all, "全选", false, dpi, theme);
            DrawLibPill(move, "移动", false, dpi, theme);
            DrawLibPill(del, app.LibraryConfirm == "deleteselected" ? "再点" : "删除", true, dpi, theme);
            DrawLibPill(done, "完成", false, dpi, theme);
        }
        else if (app.LibraryUseSelected != null)
        {
            // 使用态：卡片已经“拿起”，顶上只留一个状态字；放置/取消都用手势。
            _scratch.Color = theme.Text;
            _ctx.DrawText(Loc.Tr("已选 1 项"), ReadoutFormatLeft(dpi),
                          new Rect(head.MinX + 8f * dpi, head.MinY,
                                   MathF.Max(10f, m.Close.MinX - head.MinX - 12f * dpi),
                                   head.MaxY - head.MinY), _scratch);
            DrawLibGlyph(m.Close, "✕", theme.TextMuted, dpi);
            return;                 // 收起后只画状态条，不画卡片区 / 滚动条 / 缩放手柄
        }
        else
        {
            DrawLibMenuGlyph(m.Menu, theme.TextMuted, dpi);
            if (trashView)
            {
                _scratch.Color = theme.Text;
                _ctx.DrawText(Loc.T("‹ 返回收藏 · 回收站 {0}", n), ReadoutFormatLeft(dpi),
                              new Rect(m.Menu.MaxX + 8f * dpi, head.MinY,
                                       MathF.Max(20f, m.Close.MinX - m.Menu.MaxX - 16f * dpi),
                                       head.MaxY - head.MinY), _scratch);
            }
            else if (m.Tabbed)
            {
                DrawLibraryTabs(app, m, dpi, theme);
            }
            else
            {
                DrawLibPill(m.CurrentCategory, Loc.T("{0} · {1} ▾", Loc.Tr(catName), catCount),
                            false, dpi, theme);
            }
            DrawLibGlyph(m.Close, "✕", theme.TextMuted, dpi);
        }

        // ---- 内容 ----
        if (n == 0)
        {
            var g = m.Grid;
            _scratch.Color = theme.TextMuted;
            bool scopedEmpty = !trashView && app.LibraryCategoryId != "all";
            _ctx.DrawText(trashView ? Loc.Tr("回收站是空的")
                          : scopedEmpty ? Loc.Tr("这个分类还是空的") : Loc.Tr("收藏还是空的"),
                          ReadoutFormat(dpi),
                          new Rect(g.MinX, g.MinY + 8f * dpi, g.MaxX - g.MinX, 24f * dpi), _scratch);
            _ctx.DrawText(trashView
                              ? Loc.Tr("收藏里删掉的东西会先来这里；点卡片恢复")
                              : scopedEmpty
                                  ? Loc.Tr("把卡片拖到页签，或点 ☰ → 导入图片")
                                  : Loc.Tr("选中对象 → 收藏；透明贴纸点 ☰ → 导入图片"),
                          ReadoutFormatSmall(dpi),
                          new Rect(g.MinX, g.MinY + 34f * dpi, g.MaxX - g.MinX, 20f * dpi), _scratch);
        }
        else
        {
            // D2D ? PushAxisAlignedClip ?**????**???????????
            // ??? CanvasRectToWindow??????????????
            var clip = m.Grid;
            _ctx.PushAxisAlignedClip(
                new Vortice.RawRectF(clip.MinX, clip.MinY, clip.MaxX, clip.MaxY),
                AntialiasMode.Aliased);
            for (int i = 0; i < n; i++)
            {
                var cell = LibraryLayout.CellRect(m, i, app.LibraryScrollY);
                if (cell.MaxY < m.Grid.MinY - 1f) continue;
                if (cell.MinY > m.Grid.MaxY + 1f) break;

                bool selected = app.LibrarySelectMode
                    ? app.IsLibrarySelected(app.LibraryEntries[i])
                    : ReferenceEquals(app.LibraryUseSelected, app.LibraryEntries[i]);
                if (i == app.LibraryHover || selected)
                {
                    _scratch.Color = selected ? theme.ActiveBg : theme.Hover;
                    float inset = 2f * dpi;
                    _ctx.FillRoundedRectangle(new RoundedRectangle(
                        new Vortice.RawRectF(cell.MinX + inset, cell.MinY + inset,
                                             cell.MaxX - inset, cell.MaxY - inset),
                        radius * 0.6f, radius * 0.6f), _scratch);
                }

                var entry = app.LibraryEntries[i];
                var box = EditRegion.Of(entry.Strokes);
                float boxW = box.MaxX - box.MinX, boxH = box.MaxY - box.MinY;
                if (boxW > 0.1f && boxH > 0.1f)
                {
                    float inner = 6f * dpi;
                    float availW = MathF.Max(1f, (cell.MaxX - cell.MinX) - inner * 2f);
                    float availH = MathF.Max(1f, (cell.MaxY - cell.MinY) - inner * 2f);
                    float k = MathF.Min(availW / boxW, availH / boxH);
                    float thumbCy = (cell.MinY + cell.MaxY) * 0.5f;
                    var fit = Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(
                        (cell.MinX + cell.MaxX) * 0.5f - k * (box.MinX + box.MaxX) * 0.5f,
                        thumbCy - k * (box.MinY + box.MaxY) * 0.5f);
                    var cellClip = cell;
                    _ctx.PushAxisAlignedClip(
                        new Vortice.RawRectF(cellClip.MinX, cellClip.MinY, cellClip.MaxX, cellClip.MaxY),
                        AntialiasMode.Aliased);
                    foreach (var st in entry.Strokes) DrawStroke(st, fit);
                    _ctx.PopAxisAlignedClip();
                }

                if (app.LibrarySelectMode)
                {
                    // 整理：勾选框常驻；右下角排序把手。
                    var check = LibraryLayout.CheckRect(cell, dpi);
                    bool on = app.IsLibrarySelected(entry);
                    _scratch.Color = on ? theme.ActiveBg : new Color4(1f, 1f, 1f, 0.9f);
                    _ctx.FillRoundedRectangle(new RoundedRectangle(
                        new Vortice.RawRectF(check.MinX, check.MinY, check.MaxX, check.MaxY),
                        4f * dpi, 4f * dpi), _scratch);
                    _scratch.Color = on ? theme.ActiveText : theme.TextMuted;
                    _ctx.DrawRoundedRectangle(new RoundedRectangle(
                        new Vortice.RawRectF(check.MinX, check.MinY, check.MaxX, check.MaxY),
                        4f * dpi, 4f * dpi), _scratch, 1f * dpi);
                    if (on)
                        _ctx.DrawText("✓", ReadoutFormatSmall(dpi),
                                      new Rect(check.MinX, check.MinY, check.MaxX - check.MinX,
                                               check.MaxY - check.MinY), _scratch);

                    var grip = LibraryLayout.GripRect(cell, dpi);
                    _scratch.Color = theme.TextMuted;
                    for (int k2 = 0; k2 < 3; k2++)
                        _ctx.DrawLine(new System.Numerics.Vector2(grip.MinX + 4f * dpi,
                                                                  grip.MinY + (5f + k2 * 4f) * dpi),
                                      new System.Numerics.Vector2(grip.MaxX - 4f * dpi,
                                                                  grip.MinY + (5f + k2 * 4f) * dpi),
                                      _scratch, 1.1f * dpi);
                }
            }
            _ctx.PopAxisAlignedClip();
        }

        // 框选
        if (app.LibraryMarqueeActive)
        {
            var r = app.LibraryMarqueeRect;
            var rr = CanvasRectToWindow(r);
            _scratch.Color = new Color4(theme.ActiveBg.R, theme.ActiveBg.G, theme.ActiveBg.B, 0.12f);
            _ctx.FillRectangle(new Vortice.RawRectF(rr.MinX, rr.MinY, rr.MaxX, rr.MaxY), _scratch);
            _scratch.Color = theme.ActiveBg;
            _ctx.DrawRectangle(new Vortice.RawRectF(rr.MinX, rr.MinY, rr.MaxX, rr.MaxY), _scratch, 1.5f * dpi);
        }

        // 整理排序的插入线
        if (app.LibraryManageSortingForPaint)
        {
            int drop = app.LibraryManageDropIndex;
            float x = float.NaN;
            if (drop >= 0)
            {
                if (drop < n)
                    x = LibraryLayout.CellRect(m, drop, app.LibraryScrollY).MinX - 3f * dpi;
                else if (n > 0)
                    x = LibraryLayout.CellRect(m, n - 1, app.LibraryScrollY).MaxX + 3f * dpi;
                if (x >= m.Grid.MinX && x <= m.Grid.MaxX)
                {
                    _scratch.Color = theme.ActiveBg;
                    _ctx.DrawLine(new System.Numerics.Vector2(x, m.Grid.MinY),
                                  new System.Numerics.Vector2(x, m.Grid.MaxY), _scratch, 2f * dpi);
                }
            }
        }

        // 滚动条
        float maxScroll = LibraryLayout.MaxScroll(m, n);
        if (maxScroll > 0.5f)
        {
            var track = LibraryLayout.ScrollTrack(m);
            var thumb = LibraryLayout.ScrollThumb(m, n, app.LibraryScrollY);
            _scratch.Color = new Color4(theme.TextMuted.R, theme.TextMuted.G, theme.TextMuted.B, 0.12f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(track.MinX, track.MinY, track.MaxX, track.MaxY),
                2f * dpi, 2f * dpi), _scratch);
            _scratch.Color = new Color4(theme.TextMuted.R, theme.TextMuted.G, theme.TextMuted.B, 0.55f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(thumb.MinX, thumb.MinY, thumb.MaxX, thumb.MaxY),
                2f * dpi, 2f * dpi), _scratch);
        }

        if (app.LibraryCategoriesOpen) DrawLibraryDrawer(app, m, dpi, theme);
        if (app.LibraryCategoryMenuOpen && !m.Tabbed) DrawLibraryCategoryMenu(app, dpi, theme);

        var grip2 = LibraryLayout.ResizeRect(panel, dpi);
        _scratch.Color = theme.TextMuted;
        for (int i = 0; i < 3; i++)
            _ctx.DrawLine(new System.Numerics.Vector2(grip2.MaxX - (5f + i * 4f) * dpi, grip2.MaxY - 3f * dpi),
                          new System.Numerics.Vector2(grip2.MaxX - 3f * dpi, grip2.MaxY - (5f + i * 4f) * dpi),
                          _scratch, 1.2f * dpi);

        if (app.LibraryMenu != 0) DrawLibraryMenu(app, dpi, theme);
    }

    /// <summary>一级页签：图形 / 我的收藏。2026-11-02 起面板最上面这一行。</summary>
    private void DrawLibraryTopTabs(InkEngine app, in LibraryLayout.Info m, float dpi, UiTheme theme)
    {
        var bg = new RoundedRectangle(
            new Vortice.RawRectF(m.TopTabs.MinX, m.TopTabs.MinY, m.TopTabs.MaxX, m.TopTabs.MaxY),
            8f * dpi, 8f * dpi);
        _scratch.Color = theme.Hover;
        _ctx.FillRoundedRectangle(bg, _scratch);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawRoundedRectangle(bg, _scratch, 1f);

        DrawLibraryTopTab(m.ShapesTab, "图形", app.LibraryTab == LibraryTab.Shapes, dpi, theme);
        DrawLibraryTopTab(m.FavoritesTab, m.Compact ? "收藏" : "我的收藏",
                          app.LibraryTab == LibraryTab.Favorites, dpi, theme);
        DrawLibraryTopControls(app, m, dpi, theme);
    }

    /// <summary>一级页签行最右：📌 常驻 +（图形页）单次/∞连续。</summary>
    private void DrawLibraryTopControls(InkEngine app, in LibraryLayout.Info m,
                                        float dpi, UiTheme theme)
    {
        DrawLibraryPin(app, m, dpi, theme);
        if (app.LibraryTab == LibraryTab.Shapes)
            DrawLibraryRepeat(app, m, dpi, theme);
    }

    private void DrawLibraryPin(InkEngine app, in LibraryLayout.Info m, float dpi, UiTheme theme)
    {
        if (m.Pin.IsEmpty) return;
        bool on = app.LibraryPanelPinned;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(m.Pin.MinX + 2f * dpi, m.Pin.MinY + 2f * dpi,
                                 m.Pin.MaxX - 2f * dpi, m.Pin.MaxY - 2f * dpi),
            6f * dpi, 6f * dpi);
        _scratch.Color = on ? theme.ActiveBg : theme.Hover;
        _ctx.FillRoundedRectangle(rr, _scratch);
        float icon = 18f * dpi;
        _scratch.Color = on ? theme.ActiveText : theme.TextMuted;
        DrawIcon(IconPaths.pin,
                 (m.Pin.MinX + m.Pin.MaxX) * 0.5f - icon * 0.5f,
                 (m.Pin.MinY + m.Pin.MaxY) * 0.5f - icon * 0.5f,
                 icon, _scratch);
    }

    private void DrawLibraryRepeat(InkEngine app, in LibraryLayout.Info m,
                                   float dpi, UiTheme theme)
    {
        if (m.Repeat.IsEmpty) return;
        var outer = new RoundedRectangle(
            new Vortice.RawRectF(m.Repeat.MinX, m.Repeat.MinY, m.Repeat.MaxX, m.Repeat.MaxY),
            7f * dpi, 7f * dpi);
        _scratch.Color = theme.Hover;
        _ctx.FillRoundedRectangle(outer, _scratch);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawRoundedRectangle(outer, _scratch, 1f);

        if (m.Tabbed)
        {
            var active = app.ShapeRepeatContinuous ? m.RepeatContinuous : m.RepeatSingle;
            var fill = new RoundedRectangle(
                new Vortice.RawRectF(active.MinX + 2f * dpi, active.MinY + 2f * dpi,
                                     active.MaxX - 2f * dpi, active.MaxY - 2f * dpi),
                6f * dpi, 6f * dpi);
            _scratch.Color = theme.ActiveBg;
            _ctx.FillRoundedRectangle(fill, _scratch);

            _scratch.Color = app.ShapeRepeatContinuous ? theme.TextMuted : theme.ActiveText;
            _ctx.DrawText(Loc.Tr("单次"), ReadoutFormatSmall(dpi),
                          new Rect(m.RepeatSingle.MinX, m.RepeatSingle.MinY,
                                   m.RepeatSingle.MaxX - m.RepeatSingle.MinX,
                                   m.RepeatSingle.MaxY - m.RepeatSingle.MinY), _scratch);
            _scratch.Color = app.ShapeRepeatContinuous ? theme.ActiveText : theme.TextMuted;
            _ctx.DrawText(Loc.Tr("∞ 连续"), ReadoutFormatSmall(dpi),
                          new Rect(m.RepeatContinuous.MinX, m.RepeatContinuous.MinY,
                                   m.RepeatContinuous.MaxX - m.RepeatContinuous.MinX,
                                   m.RepeatContinuous.MaxY - m.RepeatContinuous.MinY), _scratch);
        }
        else
        {
            // 窄面板：一颗按钮，显示当前状态，点一下切。
            if (app.ShapeRepeatContinuous)
            {
                var fill = new RoundedRectangle(
                    new Vortice.RawRectF(m.Repeat.MinX + 2f * dpi, m.Repeat.MinY + 2f * dpi,
                                         m.Repeat.MaxX - 2f * dpi, m.Repeat.MaxY - 2f * dpi),
                    6f * dpi, 6f * dpi);
                _scratch.Color = theme.ActiveBg;
                _ctx.FillRoundedRectangle(fill, _scratch);
            }
            _scratch.Color = app.ShapeRepeatContinuous ? theme.ActiveText : theme.Text;
            _ctx.DrawText(app.ShapeRepeatContinuous ? Loc.Tr("∞ 连续") : Loc.Tr("单次"),
                          ReadoutFormatSmall(dpi),
                          new Rect(m.Repeat.MinX, m.Repeat.MinY,
                                   m.Repeat.MaxX - m.Repeat.MinX,
                                   m.Repeat.MaxY - m.Repeat.MinY), _scratch);
        }
    }

    private void DrawLibraryTopTab(in RectF r, string label, bool active, float dpi, UiTheme theme)
    {
        if (r.IsEmpty) return;
        if (active)
        {
            var rr = new RoundedRectangle(
                new Vortice.RawRectF(r.MinX + 3f * dpi, r.MinY + 3f * dpi,
                                     r.MaxX - 3f * dpi, r.MaxY - 3f * dpi),
                7f * dpi, 7f * dpi);
            _scratch.Color = theme.ActiveBg;
            _ctx.FillRoundedRectangle(rr, _scratch);
        }
        _scratch.Color = active ? theme.ActiveText : theme.TextMuted;
        _ctx.DrawText(Loc.Tr(label), ReadoutFormatSmall(dpi),
                      new Rect(r.MinX, r.MinY, r.MaxX - r.MinX, r.MaxY - r.MinY), _scratch);
    }

    /// <summary>
    /// 图形页：全部 / 常用 / 曲线 / 立体 四个分类 + 每个档位一张卡（37 张）。
    /// 图标先用基础几何近似（墨迹预览），点卡 = 换工具并直接设到该档。
    /// </summary>
    private void DrawShapeLibraryPage(InkEngine app, in LibraryLayout.Info m, float dpi, UiTheme theme)
    {
        var cards = app.ShapeCardsNow();
        if (m.Tabbed)
        {
            DrawShapeTabs(app, m, dpi, theme);
        }
        else
        {
            DrawLibPill(m.CurrentCategory,
                        Loc.T("{0} · {1} ▾", Loc.Tr(ShapeCatalog.NameFor(app.ShapeCategoryId)), cards.Length),
                        false, dpi, theme);
        }
        DrawLibGlyph(m.Close, "✕", theme.TextMuted, dpi);

        float maxScroll = LibraryLayout.MaxScroll(m, cards.Length);
        float scroll = Math.Clamp(app.LibraryScrollY, 0f, maxScroll);

        var clip = m.Grid;
        _ctx.PushAxisAlignedClip(
            new Vortice.RawRectF(clip.MinX, clip.MinY, clip.MaxX, clip.MaxY),
            AntialiasMode.Aliased);
        for (int i = 0; i < cards.Length; i++)
        {
            var cell = LibraryLayout.CellRect(m, i, scroll);
            if (cell.MaxY < m.Grid.MinY - 1f) continue;
            if (cell.MinY > m.Grid.MaxY + 1f) break;
            DrawShapeCatalogCard(cards[i], cell, app.ShapePresetActive(cards[i]),
                                 i == app.ShapeHoverIndex, dpi, theme);
        }
        _ctx.PopAxisAlignedClip();

        // 名字不常驻：悬停时在卡片上方浮一条；触摸点完短暂显示 1.5 秒。
        int nameIdx = app.ShapeHoverIndex >= 0
            ? app.ShapeHoverIndex
            : (app.NowMs <= app.ShapeNameUntilMs ? app.ShapeNameIndex : -1);
        if (nameIdx >= 0 && nameIdx < cards.Length)
        {
            var cell = LibraryLayout.CellRect(m, nameIdx, scroll);
            string label = cards[nameIdx].Label;
            float tw = MathF.Max(44f * dpi, (label.Length * 13f + 18f) * dpi);
            float th = 22f * dpi;
            float tx = Math.Clamp((cell.MinX + cell.MaxX - tw) * 0.5f,
                                  m.Grid.MinX, MathF.Max(m.Grid.MinX, m.Grid.MaxX - tw));
            float ty = cell.MinY - th - 5f * dpi;
            if (ty < m.Grid.MinY) ty = MathF.Min(m.Grid.MaxY - th, cell.MaxY + 5f * dpi);
            var tip = new RectF { MinX = tx, MinY = ty, MaxX = tx + tw, MaxY = ty + th };
            var rr = new RoundedRectangle(
                new Vortice.RawRectF(tip.MinX, tip.MinY, tip.MaxX, tip.MaxY),
                6f * dpi, 6f * dpi);
            _scratch.Color = new Color4(theme.Panel.R, theme.Panel.G, theme.Panel.B, 0.98f);
            _ctx.FillRoundedRectangle(rr, _scratch);
            _scratch.Color = theme.PanelBorder;
            _ctx.DrawRoundedRectangle(rr, _scratch, 1f);
            _scratch.Color = theme.Text;
            _ctx.DrawText(label, ReadoutFormatSmall(dpi),
                          new Rect(tip.MinX, tip.MinY, tip.MaxX - tip.MinX, tip.MaxY - tip.MinY),
                          _scratch);
        }

        if (maxScroll > 0.5f)
        {
            var track = LibraryLayout.ScrollTrack(m);
            var thumb = LibraryLayout.ScrollThumb(m, cards.Length, scroll);
            _scratch.Color = new Color4(theme.TextMuted.R, theme.TextMuted.G, theme.TextMuted.B, 0.12f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(track.MinX, track.MinY, track.MaxX, track.MaxY),
                2f * dpi, 2f * dpi), _scratch);
            _scratch.Color = new Color4(theme.TextMuted.R, theme.TextMuted.G, theme.TextMuted.B, 0.55f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(thumb.MinX, thumb.MinY, thumb.MaxX, thumb.MaxY),
                2f * dpi, 2f * dpi), _scratch);
        }
    }

    /// <summary>图形页的二级分类页签：全部 / 常用 / 曲线 / 立体（和收藏共用同一个选择器）。</summary>
    private void DrawShapeTabs(InkEngine app, in LibraryLayout.Info m, float dpi, UiTheme theme)
        => DrawCategoryTabs(app, m, dpi, theme);

    private void DrawShapeCatalogCard(ShapePreset preset, in RectF cell,
                                      bool active, bool hover, float dpi, UiTheme theme)
    {
        float inset = 2f * dpi;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(cell.MinX + inset, cell.MinY + inset,
                                 cell.MaxX - inset, cell.MaxY - inset),
            8f * dpi, 8f * dpi);
        _scratch.Color = active ? theme.ActiveBg
                        : hover ? new Color4(theme.ActiveBg.R, theme.ActiveBg.G, theme.ActiveBg.B, 0.12f)
                        : theme.Hover;
        _ctx.FillRoundedRectangle(rr, _scratch);
        _scratch.Color = active ? theme.ActiveBg
                        : hover ? theme.ActiveBg : theme.PanelBorder;
        _ctx.DrawRoundedRectangle(rr, _scratch, active || hover ? 2f : 1f);

        var glyph = new RectF
        {
            MinX = cell.MinX + 6f * dpi, MinY = cell.MinY + 5f * dpi,
            MaxX = cell.MaxX - 6f * dpi, MaxY = cell.MaxY - 5f * dpi,
        };
        _ctx.PushAxisAlignedClip(
            new Vortice.RawRectF(cell.MinX + 2f * dpi, cell.MinY + 2f * dpi,
                                 cell.MaxX - 2f * dpi, cell.MaxY - 2f * dpi),
            AntialiasMode.Aliased);
        DrawShapePreview(preset, glyph, dpi, active ? theme.ActiveText : theme.Text);
        _ctx.PopAxisAlignedClip();
    }

    private Stroke ShapePreviewFor(ShapePreset preset)
    {
        if (!_shapePreviewCache.TryGetValue(preset, out var s))
        {
            s = ShapePreview.Create(preset);
            _shapePreviewCache[preset] = s;
        }
        return s;
    }

    /// <summary>用正式 <see cref="Stroke"/> 渲染路径画卡片小样；线宽不随卡片缩放。</summary>
    private void DrawShapePreview(ShapePreset preset, in RectF box, float dpi, Color4 color)
    {
        var s = ShapePreviewFor(preset);
        s.Color = color;
        s.Width = MathF.Max(1.4f, 2.1f * dpi);

        // 统一 1:1 视觉框：量**实际轮廓**再等比缩到同一个正方形里居中。
        // 不能用 InkBounds：圆/椭圆的控制点只有"圆心 + 边上一点"，量出来会漏掉整个圆。
        var b = RectF.Empty;
        foreach (var p in s.ShapeOutline())
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) continue;
            b.Add(p.X, p.Y);
        }
        if (b.IsEmpty) b = s.InkBounds;
        b = b.Inflate(s.Width * 0.5f);
        float w = box.MaxX - box.MinX, h = box.MaxY - box.MinY;
        float side = MathF.Min(w, h) * 0.92f;
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;

        // 正弦 / 余弦 / 波浪用**同一套局部框**（宽 76、高 64、中心 50），
        // 避免各自的轮廓包围盒略有差异，导致三条振幅看起来不一样高。
        if (preset.Tool is Tool.Sine or Tool.Cosine or Tool.Wave)
        {
            float s0 = side / 76f;
            var wm = Matrix3x2.CreateScale(s0)
                   * Matrix3x2.CreateTranslation(cx - 50f * s0, cy - 50f * s0);
            DrawStroke(s, wm);
            return;
        }

        float bw = b.MaxX - b.MinX, bh = b.MaxY - b.MinY;
        if (bw <= 0.01f || bh <= 0.01f) return;
        float scale = side / MathF.Max(bw, bh);
        var m = Matrix3x2.CreateScale(scale)
              * Matrix3x2.CreateTranslation(cx - (b.MinX + b.MaxX) * 0.5f * scale,
                                            cy - (b.MinY + b.MaxY) * 0.5f * scale);
        DrawStroke(s, m);
    }

    private void DrawShapeGlyph(ShapePreset preset, in RectF box, float dpi, Color4 color)
    {
        Tool tool = preset.Tool;
        int variant = preset.Variant;
        _scratch.Color = color;
        float w = MathF.Min(box.MaxX - box.MinX, 44f * dpi);
        float h = MathF.Min(box.MaxY - box.MinY, 38f * dpi);
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        float l = cx - w * 0.5f, r = cx + w * 0.5f;
        float t = cy - h * 0.5f, b = cy + h * 0.5f;
        float lw = 1.7f * dpi;

        void Line(float x0, float y0, float x1, float y1)
            => _ctx.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), _scratch, lw);

        void Dashed(float x0, float y0, float x1, float y1, bool dotted)
        {
            float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (len < 0.1f) return;
            float seg = dotted ? 2f * dpi : 5f * dpi;
            float gap = dotted ? 3f * dpi : 3.5f * dpi;
            for (float s = 0; s < len; s += seg + gap)
            {
                float e = MathF.Min(len, s + seg);
                Line(x0 + (x1 - x0) * s / len, y0 + (y1 - y0) * s / len,
                     x0 + (x1 - x0) * e / len, y0 + (y1 - y0) * e / len);
            }
        }

        void Poly(Vector2[] pts, bool close)
        {
            for (int i = 1; i < pts.Length; i++) Line(pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y);
            if (close && pts.Length > 2) Line(pts[^1].X, pts[^1].Y, pts[0].X, pts[0].Y);
        }

        int sides = variant is >= 3 and <= 6 ? variant : 4;

        Vector2[] BasePoints(float rx, float ry, float baseY, int n)
        {
            var pts = new Vector2[n];
            float ang0 = ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f;
            for (int i = 0; i < n; i++)
            {
                float a = ang0 + i * MathF.Tau / n;
                var off = ShapeSpec.PrismBasePointOffset(MathF.Cos(a), MathF.Sin(a), rx, ry);
                pts[i] = new Vector2(cx + off.X, baseY + off.Y);
            }
            return pts;
        }

        void Solid(Vector2[] bot, Vector2[] top, bool hasTopRing,
                   float rx, float ry, bool fullWire = false)
        {
            int n = bot.Length;
            float ang0 = ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f;
            for (int i = 0; i < n; i++)
            {
                float mid = ang0 + (i + 0.5f) * MathF.Tau / n;
                bool face = ShapeSpec.PrismFaceVisible(MathF.Cos(mid), MathF.Sin(mid), rx, ry);
                int nx = (i + 1) % n;
                if (hasTopRing) Line(top[i].X, top[i].Y, top[nx].X, top[nx].Y);
                if (fullWire || face) Line(bot[i].X, bot[i].Y, bot[nx].X, bot[nx].Y);
                int prev = (i + n - 1) % n;
                bool prevFace = ShapeSpec.PrismFaceVisible(
                    MathF.Cos(ang0 + (prev + 0.5f) * MathF.Tau / n),
                    MathF.Sin(ang0 + (prev + 0.5f) * MathF.Tau / n), rx, ry);
                if (fullWire || face || prevFace) Line(bot[i].X, bot[i].Y, top[i].X, top[i].Y);
            }
        }

        switch (tool)
        {
            case Tool.Line:
                if (variant == (int)StrokeDash.Dashed) Dashed(l, b, r, t, false);
                else if (variant == (int)StrokeDash.Dotted) Dashed(l, b, r, t, true);
                else Line(l, b, r, t);
                break;
            case Tool.Arrow:
                Line(l, b, r, t);
                Line(r, t, r - 8f * dpi, t + 4f * dpi);
                Line(r, t, r - 4f * dpi, t + 8f * dpi);
                break;
            case Tool.Rectangle:
                _ctx.DrawRectangle(new Vortice.RawRectF(l, t, r, b), _scratch, lw);
                break;
            case Tool.Parallelogram:
                Line(l + 6f * dpi, t, r, t);
                Line(r, t, r - 6f * dpi, b);
                Line(r - 6f * dpi, b, l, b);
                Line(l, b, l + 6f * dpi, t);
                break;
            case Tool.Ellipse:
            case Tool.ConicEllipse:
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy),
                                             w * 0.5f, h * 0.42f), _scratch, lw);
                if (tool == Tool.ConicEllipse)
                {
                    float fx = w * 0.2f;
                    _ctx.DrawEllipse(new Ellipse(new Vector2(cx - fx, cy), 1.6f * dpi, 1.6f * dpi),
                                     _scratch, 1f * dpi);
                    _ctx.DrawEllipse(new Ellipse(new Vector2(cx + fx, cy), 1.6f * dpi, 1.6f * dpi),
                                     _scratch, 1f * dpi);
                    if (variant == 0)
                    {
                        Line(cx - fx, cy, cx, t + h * 0.16f);
                        Line(cx + fx, cy, cx, t + h * 0.16f);
                    }
                }
                break;
            case Tool.Circle:
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy),
                                             h * 0.42f, h * 0.42f), _scratch, lw);
                break;
            case Tool.Triangle:
                Line(cx, t, r, b);
                Line(r, b, l, b);
                Line(l, b, cx, t);
                break;
            case Tool.Coordinate:
                if (variant == 0)
                {
                    for (int i = 1; i <= 2; i++)
                    {
                        float gx = l + (r - l) * i / 3f;
                        float gy = t + (b - t) * i / 3f;
                        Line(gx, t, gx, b);
                        Line(l, gy, r, gy);
                    }
                }
                Line(l, cy, r, cy);
                Line(cx, b, cx, t);
                Line(r, cy, r - 4f * dpi, cy - 3f * dpi);
                Line(r, cy, r - 4f * dpi, cy + 3f * dpi);
                Line(cx, t, cx - 3f * dpi, t + 4f * dpi);
                Line(cx, t, cx + 3f * dpi, t + 4f * dpi);
                break;
            case Tool.Hyperbola:
                if (variant == 0)
                {
                    Dashed(l, t, r, b, false);
                    Dashed(l, b, r, t, false);
                }
                {
                    float a = w * 0.18f, bb = h * 0.40f;
                    for (int branch = -1; branch <= 1; branch += 2)
                    {
                        Vector2? prev = null;
                        for (int i = 0; i <= 14; i++)
                        {
                            float u = -1.25f + 2.5f * i / 14f;
                            var pt = new Vector2(cx + branch * a * MathF.Cosh(u),
                                                 cy + bb * MathF.Sinh(u));
                            if (prev != null) Line(prev.Value.X, prev.Value.Y, pt.X, pt.Y);
                            prev = pt;
                        }
                    }
                }
                break;
            case Tool.Parabola:
                if (variant == 0)
                {
                    // 开口上：顶点在底边中点，向左右上方展开。
                    Vector2? prev = null;
                    for (int i = 0; i <= 16; i++)
                    {
                        float u = -1f + 2f * i / 16f;
                        var pt = new Vector2(cx + u * w * 0.5f, b - (1f - u * u) * h * 0.9f);
                        if (prev != null) Line(prev.Value.X, prev.Value.Y, pt.X, pt.Y);
                        prev = pt;
                    }
                }
                else
                {
                    // 开口右：顶点在左边中点，向上下右方展开。
                    Vector2? prev = null;
                    for (int i = 0; i <= 16; i++)
                    {
                        float u = -1f + 2f * i / 16f;
                        var pt = new Vector2(l + (1f - u * u) * w * 0.9f, cy + u * h * 0.5f);
                        if (prev != null) Line(prev.Value.X, prev.Value.Y, pt.X, pt.Y);
                        prev = pt;
                    }
                }
                break;
            case Tool.Sine:
            case Tool.Cosine:
            case Tool.Wave:
                {
                    int n = 16;
                    float phase = tool == Tool.Cosine ? MathF.PI * 0.5f : 0f;
                    float amp = h * (tool == Tool.Wave ? 0.22f : 0.30f);
                    float prevX = l, prevY = cy + MathF.Sin(phase) * amp;
                    for (int i = 1; i <= n; i++)
                    {
                        float u = i / (float)n;
                        float x = l + (r - l) * u;
                        float y = cy + MathF.Sin(phase + u * MathF.PI * 2f) * amp;
                        Line(prevX, prevY, x, y);
                        prevX = x; prevY = y;
                    }
                }
                break;
            case Tool.Tangent:
                Line(l, b, r, t);
                Line(l, t, r, b);
                break;
            case Tool.Cylinder:
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, t + 4f * dpi),
                                             w * 0.45f, 5f * dpi), _scratch, lw);
                Line(l, t + 4f * dpi, l, b - 4f * dpi);
                Line(r, t + 4f * dpi, r, b - 4f * dpi);
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, b - 4f * dpi),
                                             w * 0.45f, 5f * dpi), _scratch, lw);
                break;
            case Tool.Cone:
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, b - 5f * dpi),
                                             w * 0.45f, 5f * dpi), _scratch, lw);
                Line(l, b - 5f * dpi, cx, t);
                Line(r, b - 5f * dpi, cx, t);
                break;
            case Tool.ConeFrustum:
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, t + 5f * dpi),
                                             w * 0.22f, 4f * dpi), _scratch, lw);
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, b - 5f * dpi),
                                             w * 0.45f, 5f * dpi), _scratch, lw);
                Line(cx - w * 0.22f, t + 5f * dpi, cx - w * 0.45f, b - 5f * dpi);
                Line(cx + w * 0.22f, t + 5f * dpi, cx + w * 0.45f, b - 5f * dpi);
                break;
            case Tool.Sphere:
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy),
                                             h * 0.42f, h * 0.42f), _scratch, lw);
                _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy),
                                             h * 0.42f, h * 0.18f), _scratch, lw);
                break;
            case Tool.Prism:
                {
                    float rx = w * 0.42f, ry = h * 0.14f;
                    var bot = BasePoints(rx, ry, b - h * 0.20f, sides);
                    var top = new Vector2[bot.Length];
                    for (int i = 0; i < bot.Length; i++)
                        top[i] = new Vector2(bot[i].X, bot[i].Y - h * 0.52f);
                    Solid(bot, top, true, rx, ry);
                }
                break;
            case Tool.Pyramid:
                {
                    float rx = w * 0.42f, ry = h * 0.14f;
                    var bot = BasePoints(rx, ry, b - h * 0.14f, sides);
                    var top = new Vector2[bot.Length];
                    for (int i = 0; i < top.Length; i++) top[i] = new Vector2(cx, t);
                    Solid(bot, top, false, rx, ry, fullWire: sides == 3);
                }
                break;
            case Tool.Frustum:
                {
                    float rx = w * 0.42f, ry = h * 0.13f;
                    float baseY = b - h * 0.14f;
                    var bot = BasePoints(rx, ry, baseY, sides);
                    float k0 = ShapeSpec.FrustumTopScale;
                    var apex = new Vector2(cx, t + h * 0.06f);
                    var center = new Vector2(cx, baseY);
                    var top = new Vector2[bot.Length];
                    for (int i = 0; i < bot.Length; i++) top[i] = apex + (bot[i] - center) * k0;
                    Solid(bot, top, true, rx, ry);
                }
                break;
            default:
                Line(l + 5f * dpi, t, r, t);
                Line(l + 5f * dpi, t, l, t + 6f * dpi);
                Line(l, t + 6f * dpi, l, b);
                Line(l, b, r - 5f * dpi, b);
                Line(r - 5f * dpi, b, r, b - 6f * dpi);
                Line(r, b - 6f * dpi, r, t);
                Line(l + 5f * dpi, t, l, b - 6f * dpi);
                Line(l, b - 6f * dpi, r - 5f * dpi, b - 6f * dpi);
                Line(r, t, r - 5f * dpi, b - 6f * dpi);
                break;
        }
    }

    /// <summary>
    /// 分类选择器**唯一实现**：收藏 / 图形 / 以后新增“物理”等分类都走这里。
    /// 宽面板 = 同一套胶囊页签；窄面板 = 同一个下拉 pill（调用方走 DrawLibPill）。
    /// 数据只认 `LibraryTabsNow()`，选中项由当前页决定，不再各写一套绘制。
    /// </summary>
    private void DrawCategoryTabs(InkEngine app, in LibraryLayout.Info m, float dpi, UiTheme theme)
    {
        var cats = app.LibraryTabsNow();
        bool shapes = app.LibraryTab == LibraryTab.Shapes;
        string selected = shapes ? app.ShapeCategoryId : app.LibraryCategoryId;
        var favCats = shapes ? null : app.LibraryTabCategories();
        var widths = new float[cats.Length];
        for (int i = 0; i < cats.Length; i++)
            widths[i] = LibraryLayout.TabWidthLogical(Loc.Tr(cats[i].Name)) * dpi;
        var clip = m.Tabs;
        _ctx.PushAxisAlignedClip(
            new Vortice.RawRectF(clip.MinX, clip.MinY, clip.MaxX, clip.MaxY),
            AntialiasMode.Aliased);
        for (int i = 0; i < cats.Length; i++)
        {
            var tr = LibraryLayout.TabRect(m, widths, i, dpi, app.LibraryTabScroll);
            if (tr.MaxX < m.Tabs.MinX) continue;
            if (tr.MinX > m.Tabs.MaxX) break;
            bool on = cats[i].Id == selected;
            if (on)
            {
                var rr = new RoundedRectangle(
                    new Vortice.RawRectF(tr.MinX + 2f * dpi, tr.MinY + 2f * dpi,
                                         tr.MaxX - 2f * dpi, tr.MaxY - 2f * dpi),
                    7f * dpi, 7f * dpi);
                _scratch.Color = theme.ActiveBg;
                _ctx.FillRoundedRectangle(rr, _scratch);
            }
            float starW = 0f;
            if (!shapes && favCats != null && i < favCats.Length && favCats[i].Pinned)
            {
                starW = 12f * dpi;
                var starBox = new RectF
                {
                    MinX = tr.MinX + 4f * dpi, MinY = tr.MinY + 2f * dpi,
                    MaxX = tr.MinX + 4f * dpi + starW, MaxY = tr.MaxY - 2f * dpi,
                };
                DrawLibStar(starBox, true, new Color4(0.95f, 0.68f, 0.12f, 1f), dpi);
            }
            _scratch.Color = on ? theme.ActiveText : theme.TextMuted;
            _ctx.DrawText(Loc.Tr(cats[i].Name), ReadoutFormatTab(dpi),
                          new Rect(tr.MinX + starW + 4f * dpi, tr.MinY,
                                   tr.MaxX - tr.MinX - starW - 8f * dpi,
                                   tr.MaxY - tr.MinY - 2f * dpi), _scratch);
        }
        // 页签拖动：目标位置一条竖线（只收藏页有重排）。
        if (!shapes && app.LibraryTabDropIndex >= 0)
        {
            float x = m.Tabs.MaxX;
            if (app.LibraryTabDropIndex < cats.Length)
                x = LibraryLayout.TabRect(m, widths, app.LibraryTabDropIndex, dpi, app.LibraryTabScroll).MinX - 3f * dpi;
            else if (cats.Length > 0)
                x = LibraryLayout.TabRect(m, widths, cats.Length - 1, dpi, app.LibraryTabScroll).MaxX + 3f * dpi;
            if (x >= m.Tabs.MinX && x <= m.Tabs.MaxX)
            {
                _scratch.Color = theme.ActiveBg;
                _ctx.DrawLine(new System.Numerics.Vector2(x, m.Tabs.MinY),
                              new System.Numerics.Vector2(x, m.Tabs.MaxY),
                              _scratch, 2f * dpi);
            }
        }
        _ctx.PopAxisAlignedClip();
    }

    private void DrawLibraryTabs(InkEngine app, in LibraryLayout.Info m, float dpi, UiTheme theme)
        => DrawCategoryTabs(app, m, dpi, theme);

    /// <summary>窄面板的二级分类下拉：收藏/图形共用，数据来自 LibraryTabsNow。</summary>
    private void DrawLibraryCategoryMenu(InkEngine app, float dpi, UiTheme theme)
    {
        var m = app.LibraryCategoryMenuRect();
        if (m.IsEmpty) return;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(m.MinX, m.MinY, m.MaxX, m.MaxY), 10f * dpi, 10f * dpi);
        _scratch.Color = new Color4(theme.Panel.R, theme.Panel.G, theme.Panel.B, 0.99f);
        _ctx.FillRoundedRectangle(rr, _scratch);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawRoundedRectangle(rr, _scratch, 1f);

        var cats = app.LibraryTabsNow();
        string selected = app.LibrarySelectedCategoryId;
        for (int i = 0; i < cats.Length; i++)
        {
            var item = app.LibraryCategoryMenuItemRect(i);
            if (item.IsEmpty) continue;
            bool on = cats[i].Id == selected;
            if (app.LibraryCategoryMenuHover == i)
            {
                _scratch.Color = theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new Vortice.RawRectF(item.MinX, item.MinY, item.MaxX, item.MaxY),
                    6f * dpi, 6f * dpi), _scratch);
            }
            _scratch.Color = on ? theme.ActiveBg : theme.Text;
            _ctx.DrawText(Loc.Tr(cats[i].Name), ReadoutFormatSmall(dpi),
                          new Rect(item.MinX + 10f * dpi, item.MinY,
                                   item.MaxX - item.MinX - 30f * dpi, item.MaxY - item.MinY), _scratch);
            if (on)
            {
                float cx = item.MaxX - 14f * dpi, cy = (item.MinY + item.MaxY) * 0.5f;
                _scratch.Color = theme.ActiveBg;
                _ctx.DrawLine(new System.Numerics.Vector2(cx - 4f * dpi, cy),
                              new System.Numerics.Vector2(cx - 1f * dpi, cy + 3f * dpi),
                              _scratch, 1.6f * dpi);
                _ctx.DrawLine(new System.Numerics.Vector2(cx - 1f * dpi, cy + 3f * dpi),
                              new System.Numerics.Vector2(cx + 5f * dpi, cy - 4f * dpi),
                              _scratch, 1.6f * dpi);
            }
        }
    }

    private void DrawLibMenuGlyph(in RectF r, Color4 color, float dpi)
    {
        if (r.IsEmpty) return;
        _scratch.Color = color;
        float cx = (r.MinX + r.MaxX) * 0.5f;
        float cy = (r.MinY + r.MaxY) * 0.5f;
        for (int i = -1; i <= 1; i++)
            _ctx.DrawLine(new System.Numerics.Vector2(cx - 8f * dpi, cy + i * 4f * dpi),
                          new System.Numerics.Vector2(cx + 8f * dpi, cy + i * 4f * dpi),
                          _scratch, 1.4f * dpi);
    }

    private void DrawLibPill(in RectF r, string label, bool danger, float dpi, UiTheme theme)
    {
        if (r.IsEmpty) return;
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f * dpi, 7f * dpi);
        _scratch.Color = danger ? new Color4(0.85f, 0.22f, 0.22f, 0.14f) : theme.Hover;
        _ctx.FillRoundedRectangle(rr, _scratch);
        _scratch.Color = danger ? new Color4(0.85f, 0.22f, 0.22f, 1f) : theme.Text;
        _ctx.DrawText(Loc.Tr(label), ReadoutFormatSmall(dpi),
                      new Rect(r.MinX + 2f * dpi, r.MinY,
                               r.MaxX - r.MinX - 4f * dpi, r.MaxY - r.MinY), _scratch);
    }

    private void DrawLibGlyph(in RectF r, string glyph, Color4 color, float dpi)
    {
        if (r.IsEmpty) return;
        _scratch.Color = color;
        _ctx.DrawText(glyph, ReadoutFormatSmall(dpi),
                      new Rect(r.MinX, r.MinY, r.MaxX - r.MinX, r.MaxY - r.MinY), _scratch);
    }

    private void DrawLibraryDrawer(InkEngine app, in LibraryLayout.Info m, float dpi, UiTheme theme)
    {
        var drawer = app.LibraryDrawerRectNow();
        var rr = new RoundedRectangle(new Vortice.RawRectF(
            drawer.MinX, drawer.MinY, drawer.MaxX, drawer.MaxY), 10f * dpi, 10f * dpi);
        _scratch.Color = new Color4(theme.Panel.R, theme.Panel.G, theme.Panel.B, 0.99f);
        _ctx.FillRoundedRectangle(rr, _scratch);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawRoundedRectangle(rr, _scratch, 1f);

        var cats = app.LibraryCategoryListForPaint();
        int shown = app.LibraryDrawerVisibleCountForPaint;
        int first = Math.Clamp(app.LibraryCategoryScroll, 0, Math.Max(0, cats.Length - shown));
        for (int row = 0; row < shown; row++)
        {
            var info = cats[first + row];
            var r = LibraryLayout.CategoryDrawerRowRect(drawer, dpi, row);
            bool active = info.Id == app.LibraryCategoryId;
            bool target = app.LibraryDropTarget == 1 && app.LibraryDropCategory == info.Id;
            bool renaming = app.LibraryRenamingId == info.Id;
            bool confirm = app.LibraryConfirm == "delcat:" + info.Id;
            if (active || target || confirm)
            {
                _scratch.Color = confirm ? new Color4(0.85f, 0.22f, 0.22f, 0.12f)
                                : target ? theme.ActiveBg : theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new Vortice.RawRectF(r.MinX + 3f * dpi, r.MinY + 1f * dpi,
                                         r.MaxX - 3f * dpi, r.MaxY - 1f * dpi), 6f * dpi, 6f * dpi), _scratch);
            }

            bool userCat = LibraryLayout.DrawerRowIsUserCategory(info.Id);
            bool manage = app.LibraryDrawerManage;
            float textRight = manage && userCat
                ? LibraryLayout.DrawerStarRect(r, dpi).MinX - 4f * dpi
                : r.MaxX - 8f * dpi;
            if (confirm)
            {
                _scratch.Color = new Color4(0.85f, 0.22f, 0.22f, 1f);
                _ctx.DrawText(Loc.T("再点一次删除「{0}」", Loc.Tr(info.Name)), ReadoutFormatSmall(dpi),
                              new Rect(r.MinX + 12f * dpi, r.MinY + 1f * dpi,
                                       textRight - r.MinX - 16f * dpi, 13f * dpi), _scratch);
                _scratch.Color = theme.TextMuted;
                _ctx.DrawText(Loc.T("{0} 个内容会移到「未分类」", info.Count), ReadoutFormatSmall(dpi),
                              new Rect(r.MinX + 12f * dpi, r.MinY + 14f * dpi,
                                       textRight - r.MinX - 16f * dpi, 13f * dpi), _scratch);
            }
            else if (!renaming)
            {
                _scratch.Color = active || target ? theme.ActiveText : theme.Text;
                _ctx.DrawText(Loc.T("{0} · {1}", Loc.Tr(info.Name), info.Count), ReadoutFormatLeft(dpi),
                              new Rect(r.MinX + 12f * dpi, r.MinY,
                                       MathF.Max(10f, textRight - r.MinX - 12f * dpi),
                                       r.MaxY - r.MinY), _scratch);
            }

            if (manage && userCat && !renaming)
            {
                var star = LibraryLayout.DrawerStarRect(r, dpi);
                var pencil = LibraryLayout.DrawerPencilRect(r, dpi);
                var trash = LibraryLayout.DrawerTrashRect(r, dpi);
                DrawLibStar(star, info.Pinned,
                            info.Pinned ? new Color4(0.95f, 0.68f, 0.12f, 1f) : theme.TextMuted, dpi);
                DrawLibPencil(pencil, theme.TextMuted, dpi);
                DrawLibTrash(trash, confirm ? new Color4(0.85f, 0.22f, 0.22f, 1f) : theme.TextMuted, dpi);
            }

            if (renaming)
            {
                var save = LibraryLayout.DrawerSaveRect(r, dpi);
                var cancel = LibraryLayout.DrawerCancelRect(r, dpi);
                _scratch.Color = new Color4(0.18f, 0.66f, 0.36f, 1f);
                _ctx.DrawText("✓", ReadoutFormatSmall(dpi),
                              new Rect(save.MinX, save.MinY, save.MaxX - save.MinX,
                                       save.MaxY - save.MinY), _scratch);
                _scratch.Color = new Color4(0.85f, 0.22f, 0.22f, 1f);
                _ctx.DrawText("✕", ReadoutFormatSmall(dpi),
                              new Rect(cancel.MinX, cancel.MinY, cancel.MaxX - cancel.MinX,
                                       cancel.MaxY - cancel.MinY), _scratch);
            }
        }
        var add = LibraryLayout.CategoryDrawerRowRect(drawer, dpi, shown);
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText(Loc.Tr("＋ 新建分类"), ReadoutFormatLeft(dpi),
                      new Rect(add.MinX + 12f * dpi, add.MinY, add.MaxX - add.MinX - 24f * dpi,
                               add.MaxY - add.MinY), _scratch);
    }

    /// <summary>收藏分类的“常用”星标：矢量画，不依赖字体里有没有 ★。</summary>
    private void DrawLibStar(in RectF r, bool filled, Color4 color, float dpi)
    {
        if (r.IsEmpty) return;
        float cx = (r.MinX + r.MaxX) * 0.5f;
        float cy = (r.MinY + r.MaxY) * 0.5f;
        float outer = MathF.Min(r.MaxX - r.MinX, r.MaxY - r.MinY) * 0.42f;
        float inner = outer * 0.46f;
        var pts = new Vector2[10];
        for (int i = 0; i < pts.Length; i++)
        {
            float a = -MathF.PI * 0.5f + i * MathF.PI / 5f;
            float rad = (i & 1) == 0 ? outer : inner;
            pts[i] = new Vector2(cx + MathF.Cos(a) * rad, cy + MathF.Sin(a) * rad);
        }
        using var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
            sink.EndFigure(FigureEnd.Closed);
        }
        _scratch.Color = color;
        if (filled) _ctx.FillGeometry(geo, _scratch);
        else _ctx.DrawGeometry(geo, _scratch, 1.2f * dpi);
    }

    /// <summary>收藏分类的改名铅笔：矢量画，不依赖字体里有没有 ✎。</summary>
    private void DrawLibPencil(in RectF r, Color4 color, float dpi)
    {
        if (r.IsEmpty) return;
        float cx = (r.MinX + r.MaxX) * 0.5f;
        float cy = (r.MinY + r.MaxY) * 0.5f;
        float len = MathF.Min(r.MaxX - r.MinX, r.MaxY - r.MinY) * 0.62f;
        float dx = len * 0.5f, dy = -len * 0.5f;
        var n = new Vector2(-dy, dx) * (2.2f * dpi / MathF.Max(1f, len));
        var a = new Vector2(cx - dx, cy - dy);
        var b = new Vector2(cx + dx, cy + dy);
        var dir = new Vector2(dx, dy) / MathF.Max(1f, len);
        var tip = a - dir * 3.2f * dpi;
        _scratch.Color = color;
        void L(Vector2 p, Vector2 q)
            => _ctx.DrawLine(p, q, _scratch, 1.2f * dpi);
        L(a + n, b + n);
        L(a - n, b - n);
        L(a + n, a - n);
        L(tip, a + n);
        L(tip, a - n);
    }

    private void DrawLibTrash(in RectF r, Color4 color, float dpi)
    {        _scratch.Color = color;
        float w = 10f * dpi, h = 11f * dpi;
        float cx = (r.MinX + r.MaxX) * 0.5f;
        float top = (r.MinY + r.MaxY - h) * 0.5f;
        _ctx.DrawLine(new System.Numerics.Vector2(cx - 5f * dpi, top + 1.5f * dpi),
                      new System.Numerics.Vector2(cx + 5f * dpi, top + 1.5f * dpi),
                      _scratch, 1.4f * dpi);
        _ctx.DrawLine(new System.Numerics.Vector2(cx - 2.5f * dpi, top),
                      new System.Numerics.Vector2(cx + 2.5f * dpi, top),
                      _scratch, 1.4f * dpi);
        _ctx.DrawRoundedRectangle(new RoundedRectangle(
            new Vortice.RawRectF(cx - w * 0.5f, top + 3f * dpi,
                                 cx + w * 0.5f, top + h), 2f * dpi, 2f * dpi),
            _scratch, 1.2f * dpi);
        for (int i = -1; i <= 1; i++)
            _ctx.DrawLine(new System.Numerics.Vector2(cx + i * 2.4f * dpi, top + 4.5f * dpi),
                          new System.Numerics.Vector2(cx + i * 2.4f * dpi, top + h - 1.5f * dpi),
                          _scratch, 0.9f * dpi);
    }

    private void DrawLibraryMenu(InkEngine app, float dpi, UiTheme theme)
    {
        var menu = app.LibraryMenuRect();
        var items = app.LibraryMenuItems();
        var rr = new RoundedRectangle(new Vortice.RawRectF(
            menu.MinX, menu.MinY, menu.MaxX, menu.MaxY), 10f * dpi, 10f * dpi);
        _scratch.Color = new Color4(theme.Panel.R, theme.Panel.G, theme.Panel.B, 0.99f);
        _ctx.FillRoundedRectangle(rr, _scratch);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawRoundedRectangle(rr, _scratch, 1f);
        var danger = new Color4(0.85f, 0.22f, 0.22f, 1f);
        for (int i = 0; i < items.Count; i++)
        {
            var r = app.LibraryMenuRowRect(menu, i);
            bool armed = items[i].Danger && app.LibraryConfirm == items[i].Key;
            if (armed)
            {
                _scratch.Color = new Color4(0.85f, 0.22f, 0.22f, 0.14f);
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 6f * dpi, 6f * dpi), _scratch);
            }
            _scratch.Color = !items[i].Enabled ? theme.TextMuted : items[i].Danger ? danger : theme.Text;
            _ctx.DrawText(items[i].Label, ReadoutFormatLeft(dpi),
                          new Rect(r.MinX + 10f * dpi, r.MinY,
                                   r.MaxX - r.MinX - 20f * dpi, r.MaxY - r.MinY), _scratch);
        }
    }

}
