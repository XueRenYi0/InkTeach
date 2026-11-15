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

    /// <summary>
    /// 操作条（六格）：收起 / 颜色 / 复制 / 删除 / AI / `⋯`。
    /// 顺序的语义见 <see cref="SelBarButton"/>；这一层只负责画。
    ///
    /// **为什么在下方而不是上方**：选中内容靠屏幕上边时，上方的操作条要么被顶出
    /// 屏幕、要么盖住正在讲的内容。下方永远有位置，个子矮的老师也够得着。
    ///
    /// 图标不是自己画的：路径数据来自微软 Fluent UI System Icons（MIT），
    /// 由 tools/gen-icons.ps1 抓取生成。手画的圆角和光学比例总是差一口气。
    ///
    /// 三种状态都要画出来（2026-09-16 补的，起因是用户说"复制以后看不出来"）：
    ///   · **悬停**：浅灰圆角底（`SelBarHover`，以前这个字段是个死字段，没人赋值也没人画）；
    ///   · **激活**：浅蓝底 + 图标加深（颜色/层级面板开着、复制模式开着）；
    ///   · **禁用**：图标减到 35%（导出还没接、锁定对图像无意义……）。
    /// </summary>
    private void DrawSelectionBar(InkEngine app, in RectF sel)
    {
        float dpi = app.UiUnit;
        bool textBoxSel = app.SingleTextBoxSelection;
        // 布局从 SelectionHandles 取，与命中判定同源：分开写迟早差几个像素，
        // 表现就是"看得见按钮却点不中"。
        int barCount = SelectionHandles.BarButtonCountFor(app.BarSurfaceNow);
        // 用引擎的画布缓存：滚动时操作条跟着对象走，不会粘在屏幕下沿。
        var rect = app.SelectionBarCanvasRect;
        // 圆角**用界面那套令牌**（UiTheme.CornerRadius = 10），不自己发明一个胶囊：
        // 工具条、操作条、面板三样东西的圆角必须是同一个数，看着才是一家的。
        float radius = MathF.Min(app.FloatingTheme.CornerRadius * dpi, (rect.MaxY - rect.MinY) * 0.5f);

        // 配色与投影**全取界面推上来的主题**（UiTheme.Default 只是没界面时的兜底）：
        // 操作条和工具条必须是同一套颜色，各写一份迟早会花
        // （用户 2026-09-18 实测：浅色主题改完之后，工具条有投影、操作条没有，一眼就看得出）。
        DrawPanelCard(app, rect, radius, dpi);
        var theme = app.FloatingTheme;

        int n = barCount;
        float glyphBox = SelectionHandles.BarIconBoxLogical * dpi;   // 图标框比字形大一点

        for (int i = 0; i < n; i++)
        {
            var btn = SelectionHandles.BarButtonRectIn(rect, i, dpi);
            var kind = SelectionHandles.BarButtonOf(i, app.BarSurfaceNow);
            bool hot = app.SelBarHover == i;
            bool active = IsBarButtonActive(app, kind);
            bool danger = kind == SelBarButton.Delete && hot;
            // 禁用（图标改用 theme.TextMuted）：九格都是通用操作（收起/颜色/锁定/层级/
            // 导出/复制/翻转×2/删除），对任何选中对象都成立，所以**当前没有一格会灰**。
            // 这一格留着是给"以后真需要禁用"的位置——2026-09-20 加过又撤掉的
            // 「开口方向」就是靠它灰的（现在那格整个没了，朝向改到图形面板里定，
            // 见 Engine.CycleParabolaAxis）。
            bool disabled = false;

            // 悬停 / 激活的底：**正方形**，画与自检同源（见 SelectionHandles.BarHoverChip）。
            // 8.2.1 之前这里是"四周各缩 3"：38×30 的格子里画出来是 **32×24 的长方形**，
            // 用户一眼看出"悬停是长方形不是正方形，所以分散"——现在 30×30 的格子里是 26×26。
            if (hot || active)
            {
                var chip = SelectionHandles.BarHoverChipIn(btn, dpi);
                var bg = new Vortice.RawRectF(chip.MinX, chip.MinY, chip.MaxX, chip.MaxY);
                float br = MathF.Min(bg.Bottom - bg.Top, bg.Right - bg.Left) * 0.32f;
                _scratch.Color = danger
                    ? new Color4(0.86f, 0.18f, 0.18f, 0.12f)     // 删除：悬停淡红，和普通格区分
                    : active
                        ? theme.ActiveBg                            // 激活：主题的实心蓝
                        : theme.Hover;                              // 悬停：主题的浅蓝灰
                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            var iconBrush = Brush(danger ? new Color4(0.80f, 0.16f, 0.16f, 1f)
                                     : disabled ? theme.TextMuted
                                     : active ? theme.ActiveText
                                              : theme.Text);
            float cx = (btn.MinX + btn.MaxX) * 0.5f, cy = (btn.MinY + btn.MaxY) * 0.5f;

            switch (kind)
            {
                case SelBarButton.Color:
                    // 圆环的**半径**按图标框的一半左右给（以前给成 0.62 倍，画出来比
                    // 别的图标还大一圈——用户："那个颜色圈的比例也太大了"）。
                    DrawColorRing(app, cx, cy, glyphBox * 0.40f, disabled || (textBoxSel && app.Doc.Selected[0].TextBox?.Role == TextBoxRole.AiQuestion));
                    break;
                case SelBarButton.Lock:
                    DrawIcon(IsSelectionLocked(app) ? IconPaths.lockClosed : IconPaths.unlock,
                             cx - glyphBox * 0.5f, cy - glyphBox * 0.5f, glyphBox, iconBrush);
                    break;
                case SelBarButton.FontSize:
                {
                    // 文字条的第 2 格：当前字号 + 下拉小箭头（点开是 10/14/18/24/48/64）。
                    float fs = app.Doc.Selected.Count == 1
                        ? app.Doc.Selected[0].TextBox?.FontSize ?? 18f : 18f;
                    string glyph = fs.ToString("F0");
                    var fmt = AiTextFormat(14f * dpi, true, true);
                    float arrow = 9f * dpi;
                    float textW = btn.MaxX - btn.MinX - arrow - 2f * dpi;
                    _ctx.DrawText(glyph, fmt,
                        new Rect(btn.MinX - arrow * 0.25f, btn.MinY,
                                 textW, btn.MaxY - btn.MinY), iconBrush);
                    var chev = IconPaths.chevronDown;
                    float chevX = btn.MaxX - arrow - 2f * dpi;
                    float chevY = btn.MinY + (btn.MaxY - btn.MinY) * 0.5f;
                    DrawIcon(chev, chevX, chevY - arrow * 0.5f, arrow, iconBrush);
                    break;
                }
                case SelBarButton.Library:
                {
                    bool feedback = app.LibraryFeedbackActive;
                    bool collected = app.SelectionInLibrary
                                     || (feedback && app.LibraryFeedback != InkEngine.LibraryFeedbackKind.Error);
                    if (collected)
                    {
                        // 已收藏：实心星 ＋ 绿色对勾（用户 2026-10-25 定的口径）
                        DrawIcon(IconPaths.starFilled, cx - glyphBox * 0.5f, cy - glyphBox * 0.5f,
                                 glyphBox, iconBrush);
                        var checkBrush = Brush(new Color4(0.09f, 0.64f, 0.29f, 1f));
                        DrawCheckBadge(cx + glyphBox * 0.34f, cy + glyphBox * 0.34f,
                                       glyphBox * 0.17f, checkBrush, MathF.Max(1.4f, glyphBox * 0.11f));
                    }
                    else
                    {
                        // 未收藏 / 失败：星标＋（加号是 Fluent 图标自带的，不再手画角标）
                        DrawIcon(IconPaths.starAdd, cx - glyphBox * 0.5f, cy - glyphBox * 0.5f,
                                 glyphBox, iconBrush);
                    }
                    break;
                }
                case SelBarButton.More:
                {
                    // 三个点：不引新图标，手画三颗圆点（和系统“更多”一致）
                    float dotR = MathF.Max(1.6f * dpi, glyphBox * 0.075f);
                    float dotGap = glyphBox * 0.22f;
                    for (int k = -1; k <= 1; k++)
                        _ctx.FillEllipse(new Ellipse(new Vector2(cx + k * dotGap, cy),
                                                     dotR, dotR), iconBrush);
                    break;
                }
                case SelBarButton.Ai:
                {
                    // 青 → 紫 → 粉：和主带 AI 格同一套视觉
                    var chip = SelectionHandles.BarHoverChipIn(btn, dpi);
                    var stops = new[]
                    {
                        new GradientStop(0f, new Color4(0.13f, 0.83f, 0.93f, 1f)),
                        new GradientStop(0.55f, new Color4(0.63f, 0.36f, 0.98f, 1f)),
                        new GradientStop(1f, new Color4(0.96f, 0.45f, 0.71f, 1f)),
                    };
                    using var coll = _ctx.CreateGradientStopCollection(stops, ExtendMode.Clamp);
                    using var grad = _ctx.CreateLinearGradientBrush(
                        new LinearGradientBrushProperties
                        {
                            StartPoint = new Vector2(chip.MinX, chip.MinY),
                            EndPoint = new Vector2(chip.MaxX, chip.MaxY),
                        }, new BrushProperties { Opacity = 1f }, coll);
                    _ctx.FillRoundedRectangle(new RoundedRectangle(
                        new Vortice.RawRectF(chip.MinX, chip.MinY, chip.MaxX, chip.MaxY),
                        7f * dpi, 7f * dpi), grad);
                    var fmt = AiTextFormat(12.5f * dpi, true, true);
                    _ctx.DrawText("AI", fmt,
                        new Rect(chip.MinX, chip.MinY, chip.MaxX - chip.MinX, chip.MaxY - chip.MinY),
                        Brush(new Color4(1f, 1f, 1f, 1f)));
                    break;
                }
                default:
                    DrawIcon(IconForBar(kind), cx - glyphBox * 0.5f, cy - glyphBox * 0.5f,
                             glyphBox, iconBrush);
                    break;
            }
        }

        if (app.SelectionMenuRowsNow > 0) DrawSelectionMenu(app, dpi);
    }

    /// <summary>
    /// 选中条弹层：AI 和 `⋯` **共用这一套**（方案 A）。
    /// 行数/宽度/是否有状态行由引擎的 `SelectionMenuRowsNow` 决定；
    /// 绘制、命中、脏区都读同一个 `SelectionMenuRectNow()`。
    /// </summary>
    private void DrawSelectionMenu(InkEngine app, float dpi)
    {
        var m = app.SelectionMenuRectNow();
        if (m.IsEmpty) return;
        DrawPanelCard(app, m, 10f * dpi, dpi, opaque: true);
        var theme = app.FloatingTheme;
        int rows = app.SelectionMenuRowsNow;

        if (app.SelAiMenuOpen)
        {
            string[] labels = app.AiMenuKindNow switch
            {
                InkEngine.AiMenuKind.Hand => new[] { "📝 识别文本", "📈 识别作图" },
                InkEngine.AiMenuKind.Image => new[] { "🧩 识别内容", "📈 识别作图" },
                _ => new[] { "📈 识别作图" },
            };
            for (int i = 0; i < labels.Length && i < rows; i++)
            {
                var r = SelectionHandles.BarMenuRowRectIn(m, i, dpi);
                if (app.SelMenuHover == i)
                {
                    _scratch.Color = theme.Hover;
                    _ctx.FillRoundedRectangle(new RoundedRectangle(
                        new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f * dpi, 7f * dpi), _scratch);
                }
                _ctx.DrawText(Loc.Tr(labels[i]), MenuTextFormat(dpi, center: true),
                    new Rect(r.MinX + 8f * dpi, r.MinY,
                             r.MaxX - r.MinX - 16f * dpi, r.MaxY - r.MinY),
                    Brush(theme.Text));
            }

            if (app.SelectionMenuHasStatusNow)
            {
                var sr = SelectionHandles.BarMenuStatusRectIn(m, rows, dpi);
                var c = app.HandOcrBusy
                    ? new Color4(0.55f, 0.30f, 0.85f, 1f)
                    : app.HandOcrStatus.Contains("公式可能")
                        ? new Color4(0.85f, 0.50f, 0.10f, 1f)
                        : app.HandOcrStatus.StartsWith("已优化", StringComparison.Ordinal)
                            ? new Color4(0.10f, 0.55f, 0.30f, 1f)
                            : new Color4(0.60f, 0.20f, 0.20f, 1f);
                _ctx.DrawText(app.HandOcrStatus, MenuTextFormat(dpi * 0.8f, center: false),
                    new Rect(sr.MinX, sr.MinY, sr.MaxX - sr.MinX, sr.MaxY - sr.MinY), Brush(c));
            }
            return;
        }

        // `⋯`：图标 + 文字，行高 38；悬停整行浅底。
        var order = SelectionHandles.MoreBarOrder(app.BarSurfaceNow);
        float pad = SelectionHandles.BarMenuPadLogical * dpi;
        float iconBox = 20f * dpi;
        for (int i = 0; i < rows && i < order.Length; i++)
        {
            var r = SelectionHandles.BarMenuRowRectIn(m, i, dpi);
            if (app.SelMenuHover == i)
            {
                _scratch.Color = theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(
                    new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f * dpi, 7f * dpi), _scratch);
            }
            var kind = order[i];
            string icon = kind == SelBarButton.Lock
                ? (IsSelectionLocked(app) ? IconPaths.lockClosed : IconPaths.unlock)
                : IconForBar(kind);
            float cy = (r.MinY + r.MaxY) * 0.5f;
            DrawIcon(icon, r.MinX + 8f * dpi, cy - iconBox * 0.5f, iconBox, Brush(theme.Text));
            _ctx.DrawText(SelectionHandles.BarButtonLabel(kind),
                MenuTextFormat(dpi, center: false),
                new Rect(r.MinX + 34f * dpi, r.MinY,
                         r.MaxX - r.MinX - 34f * dpi - 8f * dpi, r.MaxY - r.MinY),
                Brush(theme.Text));
        }
    }

    /// <summary>操作条十格各自的图标（颜色那格是自绘的"当前色环"，不在这里）。</summary>
    private static string IconForBar(SelBarButton b) => b switch
    {
        SelBarButton.Collapse => IconPaths.collapse,
        SelBarButton.Layer => IconPaths.layer,
        SelBarButton.Top or SelBarButton.Bottom => IconPaths.layer,
        SelBarButton.Export => IconPaths.export,
        SelBarButton.Library => IconPaths.starAdd,
        SelBarButton.Copy => IconPaths.copy,
        SelBarButton.FlipH => IconPaths.flipH,
        SelBarButton.FlipV => IconPaths.flipV,
        SelBarButton.Delete => IconPaths.delete,
        SelBarButton.Beautify => IconPaths.sparkle,
        _ => IconPaths.layer,
    };

    /// <summary>收藏成功/重复时，星标右下角那颗小对勾（两段线，和原来的「＋」角标同一套画法）。</summary>
    private void DrawCheckBadge(float cx, float cy, float arm, ID2D1SolidColorBrush brush, float thick)
    {
        _ctx.DrawLine(new Vector2(cx - arm, cy),
                      new Vector2(cx - arm * 0.15f, cy + arm * 0.75f), brush, thick);
        _ctx.DrawLine(new Vector2(cx - arm * 0.15f, cy + arm * 0.75f),
                      new Vector2(cx + arm, cy - arm * 0.75f), brush, thick);
    }


    /// <summary>这一格是"激活"状态吗（面板开着 / 模式开着）。</summary>
    private static bool IsBarButtonActive(InkEngine app, SelBarButton b) => b switch
    {
        SelBarButton.Color => app.SelPanelOpen == SelPanel.Ink,
        SelBarButton.Layer => app.SelPanelOpen == SelPanel.Layer,
        SelBarButton.FontSize => app.SelPanelOpen == SelPanel.FontSize,
        SelBarButton.Ai => app.SelAiMenuOpen,
        SelBarButton.Beautify => app.BeautifyBusy || app.SelectionAllImagesBeautified,
        SelBarButton.More => app.SelMoreMenuOpen,
        _ => false,
    };

    /// <summary>选中的对象是不是**全都**锁着（决定锁图标画开锁还是闭锁）。</summary>
    private static bool IsSelectionLocked(InkEngine app)
    {
        if (app.Doc.Selected.Count == 0) return false;
        foreach (var s in app.Doc.Selected) if (!s.Locked) return false;
        return true;
    }

    /// <summary>
    /// "颜色"那一格的图标：**当前色的圆环**（参考实现的画法）。
    ///
    /// 一眼能看出"这一批墨现在是什么颜色"；选中的颜色不一致时（混合选区）
    /// 画成**双色环**——左半边第一种色、右半边第二种色，不假装是纯色。
    /// 选中的全是图像对象时画成禁用态（图像没有"颜色"这个概念）。
    /// </summary>
    private void DrawColorRing(InkEngine app, float cx, float cy, float r, bool disabled)
    {
        Color4 a = default; int distinct = 0; Color4 b2 = default;
        bool any = false;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
            if (!any) { a = s.Color; any = true; continue; }
            if (distinct == 0 && !SameRgb(a, s.Color)) { b2 = s.Color; distinct = 1; }
            else if (distinct == 1 && !SameRgb(a, s.Color) && !SameRgb(b2, s.Color)) distinct = 2;
        }
        if (!any)                                   // 全是图像对象：画个灰环（禁用态）
        {
            _scratch.Color = new Color4(0.5f, 0.52f, 0.56f, 0.35f);
            _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), _scratch, 2.6f * app.UiUnit);
            return;
        }

        float w = 2.2f * app.UiUnit;
        var half = new Ellipse(new Vector2(cx, cy), r, r);
        _scratch.Color = disabled ? new Color4(a.R, a.G, a.B, 0.35f) : a;
        // 先整圈画底色，再把"另一种颜色"那一段盖上去（用一小段圆弧当示意，不做精密分色：
        // 这条只需要让人看出来"不止一种颜色"，不需要准确比例）。
        _ctx.DrawEllipse(half, _scratch, w);
        if (distinct >= 1)
        {
            _scratch.Color = disabled ? new Color4(b2.R, b2.G, b2.B, 0.35f) : b2;
            float a0 = -MathF.PI / 2f;
            // 用四条短弧线拼出"另外半圈"（D2D 的 DrawArc 在各版本签名不一致，不值得为它冒险）
            for (int k = 0; k < 12; k++)
            {
                float t0 = a0 + k / 24f * MathF.PI * 2f;
                float t1 = t0 + MathF.PI * 2f / 24f;
                _ctx.DrawLine(new Vector2(cx + MathF.Cos(t0) * r, cy + MathF.Sin(t0) * r),
                              new Vector2(cx + MathF.Cos(t1) * r, cy + MathF.Sin(t1) * r), _scratch, w);
            }
        }
    }

    private static bool SameRgb(in Color4 a, in Color4 b)
        => MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;

    /// <summary>
    /// 收起态的圆钮（参考实现里条首那个 ✕ 点下去之后的样子）。
    /// 画一个圆 + 中间的"＋"：两段线自己画——它不是图标，是个纯几何符号。
    /// </summary>
    private void DrawCollapsedDot(InkEngine app, in RectF sel)
    {
        _ = sel;
        float dpi = app.UiUnit;
        var r = app.SelectionDotCanvasRect;
        float d = r.MaxX - r.MinX;
        var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);

        bool hot = app.SelBarHover == (int)SelBarButton.Collapse;
        var th = app.FloatingTheme;
        _scratch.Color = hot ? th.Hover : th.Panel;
        _ctx.FillEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch, 1f * dpi);

        _scratch.Color = th.Text;
        float arm = d * 0.19f, w = 1.8f * dpi;
        _ctx.DrawLine(new Vector2(c.X - arm, c.Y), new Vector2(c.X + arm, c.Y), _scratch, w);
        _ctx.DrawLine(new Vector2(c.X, c.Y - arm), new Vector2(c.X, c.Y + arm), _scratch, w);
    }

    /// <summary>
    /// 浮动面板 / 操作条的卡片底：**主题给的投影 ＋ 底 ＋ 描边 ＋（浅色的）底沿内阴影**。
    ///
    /// 数字全部来自 `app.FloatingTheme`，而那个主题是**界面推上来的**（`InkUi.Tokens`）——
    /// 所以浮层和工具条永远是一套，不会再出现"工具条换了投影、操作条还是老样子"。
    /// 没有界面挂上来时用 `UiTheme.Default` 兜底（自检宿主）。
    /// </summary>
    private void DrawPanelCard(InkEngine app, in RectF r, float radius, float? uiUnit = null,
                               bool opaque = false)
    {
        float dpi = uiUnit ?? Dpi / 96f;
        var th = app?.FloatingTheme ?? UiTheme.Default;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);

        // **投影按形状大小缩一缩**：这套胀幅（最远 16 逻辑像素）是按 48 高的面板定的，
        // 直接套在一个 28 像素的小圆（收起后那颗球）上会变成一大团光晕。
        // 规则：比 48 矮的按比例缩，不超过 1。2026-10-25 起高度按**界面逻辑**算，
        // 否则 1.3 档下 40 的条会被当成 52 物理像素、投影不跟着放大。
        float hLogical = (r.MaxY - r.MinY) / MathF.Max(0.01f, dpi);
        float shadowK = MathF.Min(1f, hLogical / 48f);
        for (int i = th.Shadow.Length - 1; i >= 0; i--)
        {
            var layer = th.Shadow[i];
            float inf = layer.Inflate * dpi * shadowK;
            float dy = layer.Dy * dpi * shadowK;
            _scratch.Color = layer.Color;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(r.MinX - inf, r.MinY - inf + dy, r.MaxX + inf, r.MaxY + inf + dy),
                radius + inf, radius + inf), _scratch);
        }

        // 菜单类浮层必须不透明：半透明会让下面的选中框/手柄透出来（2026-11-08 截图）。
        _scratch.Color = opaque
            ? new Color4(th.Panel.R, th.Panel.G, th.Panel.B, 1f)
            : th.Panel;
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch, 1f * dpi);

        // 底沿那道"卷边"（和工具条同一个做法：裁一条横带、在带子里描一遍圆角矩形，
        // 这样两端顺着圆角收进去，不会戳出轮廓外）。深色主题这个是全透明的，等于不画。
        if (th.EdgeBottom.A > 0.001f)
        {
            float y = r.MaxY - 1f * dpi;
            _ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, y - 0.75f * dpi, r.MaxX, y + 0.75f * dpi),
                                     AntialiasMode.Aliased);
            var edge = new Vortice.RawRectF(r.MinX + 0.5f * dpi, r.MinY + 0.5f * dpi,
                                            r.MaxX - 0.5f * dpi, r.MaxY - 0.5f * dpi);
            _scratch.Color = th.EdgeBottom;
            _ctx.DrawRoundedRectangle(new RoundedRectangle(edge, radius - 0.5f * dpi, radius - 0.5f * dpi),
                                      _scratch, 1f * dpi);
            _ctx.PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// 颜色 / 粗细 / 线型面板（照参考实现）：一条粗细滑条、一行线型、一块 4 列色板。
    ///
    /// 色板的"当前色"用**环**标出来（参考实现的做法）：比整块换色省地方，
    /// 而且混合选区（几种颜色混着选）也能表达——那时环画成双色（见 DrawColorRing）。
    ///
    /// 8.2.0 两处变化：
    ///   · 滑条从"点档位"改成**连续拖动**：拖动中在滑钮上方显示实时数值（逻辑像素，
    ///     和 HUD 的「粗细」同一口径）、右端一颗跟着变粗的笔尖预览，视觉和色板那一排
    ///     （底轨/已选段/白钮 + 强调色描边）是同一套语言；
    ///   · 色板末格从"虚线灰环占位"改成**色相环**：点它弹出内置 HSV 小色板
    ///     （见 <see cref="DrawPickPanel"/>）。
    /// </summary>
    private void DrawInkPanel(InkEngine app, in RectF sel)
    {
        float dpi = app.UiUnit;
        int sc = SelectionHandles.SwatchCount;
        bool textBar = app.SingleTextBoxSelection;
        var th = app.FloatingTheme;
        var p = SelectionHandles.PanelRect(sel, dpi, app.FurnitureViewport, sc, textBar);
        // 圆角用界面推上来的主题令牌（= InkUi.Tokens.FloatingCorner 10），
        // 三处浮层同一个数——以前这两张面板各写了一个 10f 字面量。
        DrawPanelCard(app, p, th.CornerRadius * dpi, dpi);

        // ---- ① 粗细滑条（连续；拖动实时数值；右端笔尖预览）----
        // 文字的面板只有色板：粗细对文字没有意义。
        if (!textBar)
        {
        var slider = SelectionHandles.SliderRect(sel, dpi, app.FurnitureViewport, sc);
        var (tx0, tx1) = SelectionHandles.SliderTrackRange(sel, dpi, app.FurnitureViewport, sc);
        int steps = app.SliderStepCount();
        float cy = (slider.MinY + slider.MaxY) * 0.5f;

        // 当前值：拖动中用手里的那个（实时），平时取选区第一条墨的宽度。
        var (minW, maxW) = app.WidthRangeForSelection();
        bool dragging = app.WidthSliderDragging;
        float value = dragging ? app.WidthSliderDragValue : app.SliderValueOfSelection();
        float t = maxW > minW ? Math.Clamp((value - minW) / (maxW - minW), 0f, 1f) : 0f;
        float kx = tx0 + (tx1 - tx0) * t;

        // 已选段用**当前墨色**（和主条那条滑条一个语言：这一段是"这一批墨的粗细"）。
        Color4 selColor = th.ActiveBg;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
            selColor = new Color4(s.Color.R, s.Color.G, s.Color.B, 1f);
            break;
        }

        // 底轨（凹槽）＋ 已选段。拖动中轨道加粗 1 像素 = "手里握着它"的反馈。
        float hTrack = (dragging ? 6f : 5f) * dpi;
        _scratch.Color = new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.30f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(
            new Vortice.RawRectF(tx0, cy - hTrack * 0.5f, tx1, cy + hTrack * 0.5f),
            hTrack * 0.5f, hTrack * 0.5f), _scratch);
        if (kx - tx0 > 0.5f * dpi)
        {
            _scratch.Color = new Color4(selColor.R, selColor.G, selColor.B, dragging ? 0.85f : 0.55f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(tx0, cy - hTrack * 0.5f, kx, cy + hTrack * 0.5f),
                hTrack * 0.5f, hTrack * 0.5f), _scratch);
        }

        // 旧档位的位置留一排**参考刻度**（滑条连续了，点还在——凭肌肉记忆找得到地方）。
        for (int i = 0; i < steps; i++)
        {
            float x = SelectionHandles.SliderStepX(i, steps, sel, dpi, app.FurnitureViewport, sc);
            _scratch.Color = new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.55f);
            _ctx.FillEllipse(new Ellipse(new Vector2(x, cy), 1.4f * dpi, 1.4f * dpi), _scratch);
        }

        // 滑钮：白底 + 强调色描边（和色板当前色的环、线型当前档的描边同一套）。
        _ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 7f * dpi, 7f * dpi),
                         Brush(new Color4(1f, 1f, 1f, 1f)));
        _scratch.Color = th.ActiveBg;
        _ctx.DrawEllipse(new Ellipse(new Vector2(kx, cy), 7f * dpi, 7f * dpi), _scratch, 1.8f * dpi);

        // 右端"笔尖预览"：一颗跟着值变大的点（和主条那条滑条同一个公式）。
        {
            float pr = Math.Clamp(2f + t * 6.5f, 2f, 8.5f) * dpi;
            float px = slider.MaxX - SelectionHandles.SliderTailLogical * 0.5f * dpi;
            _scratch.Color = selColor;
            _ctx.FillEllipse(new Ellipse(new Vector2(px, cy), pr, pr), _scratch);
        }

        // 拖动中：数值显示在滑钮上方（"拖动的时候显示值"——用户 2026-09-30）。
        // 行高 30（8.2.1 收矮过）：字号 11 + 盒高 16，正好卡在卡片上沿和轨道之间。
        if (dragging)
        {
            string text = value.ToString("0");           // 整数（2026-10-16：值已整数吸附）
            float tx = Math.Clamp(kx, p.MinX + 18f * dpi, slider.MaxX - 18f * dpi);
            float tw = 44f * dpi;
            _scratch.Color = th.Text;
            _ctx.DrawText(text, ReadoutFormatSmall(dpi),
                          new Rect(tx - tw * 0.5f, cy - 19f * dpi, tw, 16f * dpi), _scratch);
        }

        }

        // ---- ②③ 线型：实线 / 虚线 / 点线 ----
        // 当前是哪一档**由 SelectionHandles 统一算**（见 DashOfSelection 的注释：
        // 画和自检必须读同一份，不然"面板认不认得当前档"这件事自己验不了自己）。
        var curDash = SelectionHandles.DashOfSelection(app.Doc.Selected);
        if (!textBar)
        for (int i = 0; i < SelectionHandles.StyleCellCount; i++)
        {
            var cell = SelectionHandles.StyleCellRect(i, sel, dpi, app.FurnitureViewport, sc);
            var dash = (StrokeDash)i;             // 格序 = 线型取值，见 StyleCellRect 的注释
            bool active = dash == curDash;

            // 当前档：浅底 + **强调色描边**。光靠浅底不够显眼——用户 2026-09-19 报的
            // "实线和虚线好像不能选中"，最可能看到的就是"这一行没有哪一格像被选中的样子"。
            // 描边这一招和滑条那个当前档的圆钮、色板那个当前色的环是同一套语言。
            // 不再用 th.Hover：那是**悬停**色，拿它表示"选中"本身就串了。
            _scratch.Color = th.Panel;
            float cr = 7f * dpi;
            var cellRect = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
            _ctx.FillRoundedRectangle(new RoundedRectangle(cellRect, cr, cr), _scratch);
            if (active)
            {
                _scratch.Color = th.ActiveBg;
                _ctx.DrawRoundedRectangle(new RoundedRectangle(cellRect, cr, cr), _scratch, 1.6f * dpi);
            }

            // 格子里画**这一档真实的样子**（一条整线 / 一段一段 / 一小点一小点）：
            // 老师看的是"哪格是虚线"，画个文字标签反而要多认一眼。
            float lx0 = cell.MinX + 9f * dpi, lx1 = cell.MaxX - 9f * dpi;
            float ly = (cell.MinY + cell.MaxY) * 0.5f;
            _scratch.Color = active ? th.Text : th.TextMuted;
            var a = new Vector2(lx0, ly);
            var b = new Vector2(lx1, ly);
            float lw = 2.6f * dpi;
            switch (dash)
            {
                case StrokeDash.Dashed: DrawDashed(a, b, lw); break;
                case StrokeDash.Dotted: DrawDotted(a, b, lw); break;
                default: _ctx.DrawLine(a, b, _scratch, lw); break;
            }
        }

        // ---- ④ 色板 ----
        var swatches = InkPalette.SelectionSwatches;
        Color4 curColor = default; bool anyColor = false;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
            curColor = s.Color; anyColor = true; break;
        }
        for (int i = 0; i < sc; i++)
        {
            var cell = SelectionHandles.SwatchRect(i, sel, dpi, app.FurnitureViewport, sc, textBar);
            var c = new Vector2((cell.MinX + cell.MaxX) * 0.5f, (cell.MinY + cell.MaxY) * 0.5f);
            float rad = (cell.MaxX - cell.MinX) * 0.5f;
            bool custom = i == swatches.Length - 1;      // 末格 = 自定义取色（8.2.0：色相环）

            if (custom)
            {
                // **色相环**：24 段、每段一个色相（往两边各多画半段，避免段间露缝）。
                // 它是"自定义颜色"的通用语言；内侧再点一颗当前色，说明"现在这个色也是自定义来的"。
                const int segs = 24;
                float rw = 2.8f * dpi;
                for (int k = 0; k < segs; k++)
                {
                    float a0 = k / (float)segs * MathF.PI * 2f;
                    float a1 = (k + 1.5f) / segs * MathF.PI * 2f;       // +半段重叠
                    _scratch.Color = Hsv.ToRgb(k / (float)segs, 0.85f, 0.95f);
                    _ctx.DrawLine(new Vector2(c.X + MathF.Cos(a0) * rad, c.Y + MathF.Sin(a0) * rad),
                                  new Vector2(c.X + MathF.Cos(a1) * rad, c.Y + MathF.Sin(a1) * rad),
                                  _scratch, rw);
                }
                if (anyColor)
                {
                    _scratch.Color = curColor;
                    _ctx.FillEllipse(new Ellipse(c, rad * 0.42f, rad * 0.42f), _scratch);
                }
                // 激活：色板开着，或者当前色**不在**这张色板里（那次取色就是从这儿来的）。
                bool customActive = app.CustomColorOpen
                    || (anyColor && !PaletteContains(swatches, curColor));
                if (customActive)
                {
                    _scratch.Color = th.ActiveBg;
                    _ctx.DrawEllipse(new Ellipse(c, rad + 3.5f * dpi, rad + 3.5f * dpi),
                                     _scratch, 2.2f * dpi);
                }
                continue;
            }

            var col = swatches[i].Color;
            _scratch.Color = col;
            _ctx.FillEllipse(new Ellipse(c, rad, rad), _scratch);
            _scratch.Color = new Color4(0f, 0f, 0f, 0.18f);        // 浅色片压在浅底上要能看见边
            _ctx.DrawEllipse(new Ellipse(c, rad, rad), _scratch, 1f * dpi);

            // 当前色：外面套一圈（参考实现的做法）
            if (anyColor && SameRgb(col, curColor))
            {
                _scratch.Color = th.ActiveBg;
                _ctx.DrawEllipse(new Ellipse(c, rad + 3.5f * dpi, rad + 3.5f * dpi), _scratch, 2.2f * dpi);
            }
        }
    }

    /// <summary>这张色板里有没有这个颜色（阈值同 <see cref="SameRgb"/>）。</summary>
    private static bool PaletteContains((string Name, Color4 Color)[] swatches, in Color4 c)
    {
        for (int i = 0; i < swatches.Length - 1; i++)          // 末格是自定义，不参与
            if (SameRgb(swatches[i].Color, c)) return true;
        return false;
    }

    /// <summary>
    /// **自定义取色板**（8.2.0，色板末格点开）：色相条 + 饱和度/明度方块 + 当前色预览。
    ///
    /// 用户当时选的是 A 方案（"轻量 HSV 色板"，不弹系统对话框）：
    /// 点末格 → 弹出这张小卡片 → 选完**直接应用到选中墨迹**（荧光笔自动保半透明，
    /// 走 <see cref="InkPalette.ForStroke"/>）；点卡片外面 = 取消（保持原色）。
    ///
    /// 尺寸和圆角跟墨迹/层级面板共用一套 token（`FloatPadLogical` / `FloatGapLogical` /
    /// `theme.CornerRadius`），挂在墨迹面板旁边、**底边对齐**——离"自定义"那一格最近。
    ///
    /// 两块取色面用**缓存位图**画（饱和度/明度方块：色相变了才重画一张 104² 的小图；
    /// 色相条：尺寸变了才重画）。不用 D2D 渐变笔刷：那是这套代码里从没出现过的
    /// API 面，而"手算一张小图"是这条渲染管线里现成的做法（HUD 缓存就是这么干的）。
    /// </summary>
    private void DrawPickPanel(InkEngine app, in RectF sel)
    {
        float dpi = app.UiUnit;
        int sc = SelectionHandles.SwatchCount;
        bool textBar = app.SingleTextBoxSelection;
        var th = app.FloatingTheme;
        var p = SelectionHandles.CustomPanelRect(sel, dpi, app.FurnitureViewport, sc, textBar);
        DrawPanelCard(app, p, th.CornerRadius * dpi, dpi);

        var sv = SelectionHandles.PickSvRect(sel, dpi, app.FurnitureViewport, sc, textBar);
        var hue = SelectionHandles.PickHueRect(sel, dpi, app.FurnitureViewport, sc, textBar);
        var prev = SelectionHandles.PickPreviewRect(sel, dpi, app.FurnitureViewport, sc, textBar);
        var (ph, ps, pv) = app.PickHsv;
        var picked = Hsv.ToRgb(ph, ps, pv);

        EnsurePickBitmaps(dpi, ph);

        // ---- ① 饱和度（横）／明度（纵）方块 ----
        if (_pickSvBmp != null)
            _ctx.DrawBitmap(_pickSvBmp, new Vortice.RawRectF(sv.MinX, sv.MinY, sv.MaxX, sv.MaxY),
                            1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.22f);
        _ctx.DrawRectangle(new Vortice.RawRectF(sv.MinX, sv.MinY, sv.MaxX, sv.MaxY), _scratch, 1f * dpi);

        // 选中的那一点：白圈 + 一圈暗边（压在亮色上、暗色上都看得见）
        float sx = sv.MinX + (sv.MaxX - sv.MinX) * ps;
        float sy = sv.MinY + (sv.MaxY - sv.MinY) * pv;
        _ctx.DrawEllipse(new Ellipse(new Vector2(sx, sy), 5.5f * dpi, 5.5f * dpi),
                         Brush(new Color4(1f, 1f, 1f, 1f)), 2.2f * dpi);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.55f);
        _ctx.DrawEllipse(new Ellipse(new Vector2(sx, sy), 6.8f * dpi, 6.8f * dpi), _scratch, 1f * dpi);

        // ---- ② 竖直色相条 ----
        if (_pickHueBmp != null)
            _ctx.DrawBitmap(_pickHueBmp, new Vortice.RawRectF(hue.MinX, hue.MinY, hue.MaxX, hue.MaxY),
                            1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.22f);
        _ctx.DrawRectangle(new Vortice.RawRectF(hue.MinX, hue.MinY, hue.MaxX, hue.MaxY), _scratch, 1f * dpi);

        // 当前色相那条指示线（白线 + 暗边，和方块那颗点同一套）
        float hy = hue.MinY + (hue.MaxY - hue.MinY) * ph;
        _ctx.DrawLine(new Vector2(hue.MinX + 1f * dpi, hy), new Vector2(hue.MaxX - 1f * dpi, hy),
                      Brush(new Color4(1f, 1f, 1f, 1f)), 2.2f * dpi);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.45f);
        _ctx.DrawLine(new Vector2(hue.MinX + 1f * dpi, hy - 1.9f * dpi),
                      new Vector2(hue.MaxX - 1f * dpi, hy - 1.9f * dpi), _scratch, 0.9f * dpi);
        _ctx.DrawLine(new Vector2(hue.MinX + 1f * dpi, hy + 1.9f * dpi),
                      new Vector2(hue.MaxX - 1f * dpi, hy + 1.9f * dpi), _scratch, 0.9f * dpi);

        // ---- ③ 当前色预览 ----
        // 预览画的是**基色**（荧光笔选出来的也是这个色；实际描上去会自动保半透明）。
        float side = MathF.Min(prev.MaxY - prev.MinY, 30f * dpi);
        var sw = new RoundedRectangle(
            new Vortice.RawRectF(prev.MinX, prev.MinY, prev.MinX + side, prev.MinY + side), 7f * dpi, 7f * dpi);
        _scratch.Color = picked;
        _ctx.FillRoundedRectangle(sw, _scratch);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.22f);
        _ctx.DrawRoundedRectangle(sw, _scratch, 1f * dpi);
        _scratch.Color = th.ActiveBg;
        _ctx.DrawRoundedRectangle(new RoundedRectangle(
            new Vortice.RawRectF(prev.MinX - 2f * dpi, prev.MinY - 2f * dpi,
                                 prev.MinX + side + 2f * dpi, prev.MinY + side + 2f * dpi),
            8f * dpi, 8f * dpi), _scratch, 1.6f * dpi);
    }

    // ---- 取色板两张缓存位图（色相/尺寸变了才重画）----------------------------

    private ID2D1Bitmap _pickSvBmp, _pickHueBmp;
    private float _pickSvHue = -1f;
    private int _pickSvPix = -1, _pickHueWPix = -1, _pickHueHPix = -1;

    private void EnsurePickBitmaps(float dpi, float hue)
    {
        int svPix = Math.Max(2, (int)MathF.Round(SelectionHandles.PickSvLogical * dpi));
        int hueW = Math.Max(1, (int)MathF.Round(SelectionHandles.PickHueLogical * dpi));
        int hueH = svPix;

        if (_pickHueBmp == null || _pickHueWPix != hueW || _pickHueHPix != hueH)
        {
            _pickHueBmp?.Dispose();
            _pickHueBmp = CreatePickBitmap(hueW, hueH, (x, y) => Hsv.ToRgb(y / (hueH - 1f), 1f, 1f));
            _pickHueWPix = hueW; _pickHueHPix = hueH;
        }
        if (_pickSvBmp == null || _pickSvPix != svPix || MathF.Abs(_pickSvHue - hue) > 0.004f)
        {
            _pickSvBmp?.Dispose();
            _pickSvBmp = CreatePickBitmap(svPix, svPix,
                (x, y) => Hsv.ToRgb(hue, x / (svPix - 1f), 1f - y / (svPix - 1f)));
            _pickSvPix = svPix; _pickSvHue = hue;
        }
    }

    /// <summary>按逐像素函数建一张 CPU 数据的一次性 D2D 位图（BGRA、不透明）。</summary>
    private ID2D1Bitmap CreatePickBitmap(int w, int h, Func<int, int, Color4> colorAt)
    {
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = colorAt(x, y);
                int i = (y * w + x) * 4;
                buf[i + 0] = (byte)Math.Clamp((int)(c.B * 255f + 0.5f), 0, 255);
                buf[i + 1] = (byte)Math.Clamp((int)(c.G * 255f + 0.5f), 0, 255);
                buf[i + 2] = (byte)Math.Clamp((int)(c.R * 255f + 0.5f), 0, 255);
                buf[i + 3] = 255;
            }
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None);
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            return _ctx.CreateBitmap(new SizeI(w, h), handle.AddrOfPinnedObject(), (uint)(w * 4), props);
        }
        catch
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>层级小面板：置顶 / 置底两格（图标用 Fluent 的"上/下箭头 + 底托"）。</summary>
    /// <summary>
    /// 文字操作条「A」下拉：字体 / 字号 / 行距三行。
    /// 第一行用真实字体画名字（看到的就是会用的字），第二行字号，第三行行距倍数。
    /// </summary>
    private void DrawFontPanel(InkEngine app, in RectF sel)
    {
        float dpi = app.UiUnit;
        var p = SelectionHandles.FontPanelRect(sel, dpi, app.FurnitureViewport);
        // 属性面板内容密、又有色块，半透明会透出后面的文字，看起来脏；这里用实底。
        DrawPanelCard(app, p, app.FloatingTheme.CornerRadius * dpi, dpi, opaque: true);
        var th = app.FloatingTheme;

        var box = app.Doc.Selected.Count == 1 ? app.Doc.Selected[0].TextBox : null;
        float currentSize = box?.FontSize ?? 18f;
        float currentSpacing = box?.LineSpacing ?? TextBoxFaces.DefaultLineSpacing;

        float pad = SelectionHandles.FloatPadLogical * dpi;
        var labelFmt = PanelLabelFormat(dpi);
        string[] rowLabels = { Loc.Tr("字体"), Loc.Tr("字号"), Loc.Tr("行距"), Loc.Tr("背景") };
        for (int row = 0; row < 4; row++)
        {
            var first = row switch
            {
                0 => SelectionHandles.FontPanelFaceRect(0, sel, dpi, app.FurnitureViewport),
                1 => SelectionHandles.FontPanelCellRect(0, sel, dpi, app.FurnitureViewport),
                2 => SelectionHandles.FontPanelSpacingRect(0, sel, dpi, app.FurnitureViewport),
                _ => SelectionHandles.FontPanelBackgroundRect(0, sel, dpi, app.FurnitureViewport),
            };
            _ctx.DrawText(rowLabels[row], labelFmt,
                new Rect(p.MinX + pad, first.MinY, SelectionHandles.StyleLabelWidthLogical * dpi,
                         first.MaxY - first.MinY),
                Brush(th.TextMuted));
        }

        void Chip(RectF cell, string label, bool active, IDWriteTextFormat fmt)
        {
            float cr = 8f * dpi;
            _scratch.Color = active ? th.ActiveBg : th.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY), cr, cr), _scratch);
            _ctx.DrawText(Loc.Tr(label), fmt,
                new Rect(cell.MinX, cell.MinY, cell.MaxX - cell.MinX, cell.MaxY - cell.MinY),
                Brush(active ? th.ActiveText : th.Text));
        }

        float faceChipSize = TextBoxFaces.IsCjkUi && !TextBoxFaces.IsChineseUi ? 11.5f : 13f;
        for (int i = 0; i < TextBoxFaces.Visible.Length; i++)
        {
            var info = TextBoxFaces.Visible[i];
            TextBoxFaces.EnsureBundled(info.Face);      // 格子用字体本身预览，先注册
            var cell = SelectionHandles.FontPanelFaceRect(i, sel, dpi, app.FurnitureViewport);
            Chip(cell, info.Name, TextBoxFaces.IsVisibleFace(box, info),
                 AiTextFormat(faceChipSize * dpi, true, true, false, info.DirectWrite));
        }
        // 最后一格：全部系统字体（2026-11-11 用户定）。
        {
            var cell = SelectionHandles.FontPanelFaceRect(TextBoxFaces.Visible.Length,
                                                          sel, dpi, app.FurnitureViewport);
            float moreSize = TextBoxFaces.IsCjkUi && !TextBoxFaces.IsChineseUi ? 11f : 12.5f;
            Chip(cell, "更多…", app.FontListOpen, AiTextFormat(moreSize * dpi, false, true, false));
        }

        for (int i = 0; i < SelectionHandles.FontPanelSizeCount
                        && i < InkPalette.TextFontSizes.Length; i++)
        {
            var cell = SelectionHandles.FontPanelCellRect(i, sel, dpi, app.FurnitureViewport);
            bool active = MathF.Abs(currentSize - InkPalette.TextFontSizes[i]) < 0.1f;
            Chip(cell, InkPalette.TextFontSizes[i].ToString("F0"), active,
                 AiTextFormat(15f * dpi, true, true));
        }

        for (int i = 0; i < TextBoxFaces.LineSpacings.Length; i++)
        {
            var cell = SelectionHandles.FontPanelSpacingRect(i, sel, dpi, app.FurnitureViewport);
            bool active = MathF.Abs(currentSpacing - TextBoxFaces.LineSpacings[i]) < 0.01f;
            Chip(cell, TextBoxFaces.LineSpacings[i].ToString("0.0"), active,
                 AiTextFormat(14f * dpi, true, true));
        }

        // 背景：透明 / 白纸 / 板色 / 浅色。四格铺满整行，和上面几行右沿对齐。
        bool card = box != null && (box.Rich || box.Role == TextBoxRole.AiQuestion);
        var board = app.BoardColor;
        var bgFmt = AiTextFormat(12f * dpi, true, true);
        for (int i = 0; i < TextBoxBackground.PresetCount; i++)
        {
            var cell = SelectionHandles.FontPanelBackgroundRect(i, sel, dpi, app.FurnitureViewport);
            var fill = TextBoxBackground.Fill(i, board, card);
            var border = TextBoxBackground.Border(i, board, card);
            bool active = box != null
                          && TextBoxBackground.Same(fill, box.Fill)
                          && TextBoxBackground.Same(border, box.Border);
            float cr = 8f * dpi;
            var rf = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
            if (fill.A > 0.01f)
            {
                _scratch.Color = fill;
                _ctx.FillRoundedRectangle(new RoundedRectangle(rf, cr, cr), _scratch);
            }
            else
            {
                _scratch.Color = th.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(rf, cr, cr), _scratch);
                _scratch.Color = new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.55f);
                // 小斜杠只放在左侧，避免从“透明”两个字中间穿过去。
                _ctx.DrawLine(new Vector2(cell.MinX + 7f * dpi, cell.MaxY - 7f * dpi),
                              new Vector2(cell.MinX + 15f * dpi, cell.MinY + 7f * dpi),
                              _scratch, 1.2f * dpi);
            }
            _scratch.Color = border.A > 0.01f
                ? border
                : new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.35f);
            _ctx.DrawRoundedRectangle(new RoundedRectangle(rf, cr, cr), _scratch, 1f * dpi);
            if (active)
            {
                _scratch.Color = th.ActiveBg;
                _ctx.DrawRoundedRectangle(new RoundedRectangle(
                    new Vortice.RawRectF(cell.MinX - 1.5f * dpi, cell.MinY - 1.5f * dpi,
                                         cell.MaxX + 1.5f * dpi, cell.MaxY + 1.5f * dpi),
                    cr + 1.5f * dpi, cr + 1.5f * dpi), _scratch, 2f * dpi);
            }
            Color4 label = fill.A > 0.01f
                ? (TextBoxBackground.Luma(fill) > 0.55f
                    ? new Color4(0.12f, 0.14f, 0.18f, 1f)
                    : new Color4(0.98f, 0.98f, 0.98f, 1f))
                : th.Text;
            _ctx.DrawText(Loc.Tr(TextBoxBackground.Names[i]), bgFmt,
                new Rect(cell.MinX, cell.MinY, cell.MaxX - cell.MinX, cell.MaxY - cell.MinY),
                Brush(label));
        }
    }

    /// <summary>
    /// 「更多…」字体下拉：只列中文字体（可开关显示西文），顶部“最近使用”。
    /// 固定 10 行、像素级连续滚动（滚轮 / 触摸拖动 + 惯性），右侧细滚动条；
    /// 每行**用该字体自己画**（所见即所得），当前字体用强调色。
    /// </summary>
    private void DrawFontList(InkEngine app, float dpi)
    {
        var list = app.FontListRectNow();
        if (list.IsEmpty) return;
        DrawPanelCard(app, list, app.FloatingTheme.CornerRadius * dpi, dpi, opaque: true);
        var th = app.FloatingTheme;
        int capacity = app.FontListCapacityNow;
        float pad = SelectionHandles.FloatPadLogical * dpi;
        float rowH = app.FontListRowHeightNow;
        var box = app.SelectedFontTextBox;
        var entries = app.FontListEntries;

        // 行区 = 列表减去底部开关条；只画真正落在里面的行（列表外不画）。
        var foot = SelectionHandles.FontListFooterRect(list, dpi);
        float rowsBottom = foot.MinY - 2f * dpi;
        {
            int first = Math.Max(0, (int)MathF.Floor(app.FontListScrollPx / rowH));
            for (int i = first; i < entries.Length && i < first + capacity + 1; i++)
            {
                var r = SelectionHandles.FontListRowRect(i, list, dpi, app.FontListScrollPx);
                if (r.MaxY < list.MinY + pad || r.MinY > rowsBottom) continue;
                string family = entries[i];
                bool active = TextBoxFaces.IsFamily(box, family);
                if (active || app.FontListHover == i)
                {
                    _scratch.Color = active ? th.ActiveBg : th.Hover;
                    _ctx.FillRoundedRectangle(new RoundedRectangle(
                        new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f * dpi, 7f * dpi),
                        _scratch);
                }
                var fmt = AiTextFormat(14f * dpi, false, false, false, family);
                float textW = MathF.Max(1f, r.MaxX - r.MinX - pad * 2f - 10f * dpi);
                string label = Ellipsize(family, fmt, textW);
                _ctx.DrawText(label, fmt,
                    new Rect(r.MinX + pad, r.MinY, textW, r.MaxY - r.MinY),
                    Brush(active ? th.ActiveText : th.Text));
            }

            // “最近使用”和全部字体之间一条细分隔线。
            int recent = app.FontListRecentCount;
            if (recent > 0 && recent < entries.Length)
            {
                var last = SelectionHandles.FontListRowRect(recent - 1, list, dpi, app.FontListScrollPx);
                float y = last.MaxY + 2f * dpi;
                if (y > list.MinY + pad && y < rowsBottom)
                {
                    _scratch.Color = new Color4(0.90f, 0.91f, 0.93f, 1f);
                    _ctx.DrawLine(new Vector2(last.MinX, y), new Vector2(last.MaxX, y),
                                  _scratch, MathF.Max(1f, dpi));
                }
            }
        }

        // 右侧细滚动条（内容超出才有）。
        if (app.FontListMaxScroll > 0.5f)
        {
            var track = SelectionHandles.FontListScrollTrack(list, dpi);
            float ratio = (float)capacity / Math.Max(1, entries.Length);
            float thumbH = MathF.Max(18f * dpi, (track.MaxY - track.MinY) * ratio);
            thumbH = MathF.Min(track.MaxY - track.MinY, thumbH);
            float t = app.FontListScrollPx / MathF.Max(0.01f, app.FontListMaxScroll);
            float ty = track.MinY + (track.MaxY - track.MinY - thumbH) * Math.Clamp(t, 0f, 1f);
            _scratch.Color = new Color4(0.86f, 0.88f, 0.91f, 1f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(track.MinX, track.MinY, track.MaxX, track.MaxY),
                3f * dpi, 3f * dpi), _scratch);
            _scratch.Color = new Color4(0.55f, 0.58f, 0.64f, 1f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(track.MinX, ty, track.MaxX, ty + thumbH),
                3f * dpi, 3f * dpi), _scratch);
        }

        // 底部：先铺一条底色盖住半行，再写总数 + 「显示西文字体」开关。
        _scratch.Color = new Color4(th.Panel.R, th.Panel.G, th.Panel.B, 1f);
        _ctx.FillRectangle(new Vortice.RawRectF(foot.MinX - 2f * dpi, foot.MinY - 3f * dpi,
                                                foot.MaxX + 2f * dpi, foot.MaxY + 3f * dpi),
                           _scratch);
        var footFmt = AiTextFormat(11.5f * dpi, false, false);
        string countText = Loc.T("共 {0} 个字体", entries.Length);
        float cw = MeasureTextWidth(countText, footFmt);
        _ctx.DrawText(countText, footFmt,
            new Rect(foot.MinX, foot.MinY, cw + 4f * dpi, foot.MaxY - foot.MinY),
            Brush(th.TextMuted));
        // 非中文/日/韩界面本来就列本语言字体，不再给「显示西文字体」开关。
        if (TextBoxFaces.IsCjkUi)
        {
            string toggle = Loc.Tr("显示西文字体") + (app.FontListShowLatin ? " ✓" : "");
            _ctx.DrawText(toggle, footFmt,
                new Rect(foot.MinX + cw + 14f * dpi, foot.MinY,
                         MeasureTextWidth(toggle, footFmt) + 8f * dpi, foot.MaxY - foot.MinY),
                Brush(app.FontListShowLatin ? th.ActiveText : th.TextMuted));
        }
    }

    /// <summary>按宽度截断，超出加省略号（字体名太长时不硬裁）。</summary>
    private string Ellipsize(string text, IDWriteTextFormat fmt, float maxW)
    {
        if (string.IsNullOrEmpty(text) || maxW <= 1f) return text ?? "";
        if (MeasureTextWidth(text, fmt) <= maxW) return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            string t = text[..n] + "…";
            if (MeasureTextWidth(t, fmt) <= maxW) return t;
        }
        return "…";
    }

    private void DrawLayerPanel(InkEngine app, in RectF sel)
    {
        float dpi = app.UiUnit;
        bool textBar = app.SingleTextBoxSelection;
        var p = SelectionHandles.LayerPanelRect(sel, dpi, app.FurnitureViewport, textBar);
        // 圆角同样取界面推上来的主题令牌（三处浮层一个数，8.2.0 收口）。
        DrawPanelCard(app, p, app.FloatingTheme.CornerRadius * dpi, dpi);

        string[] icons = { IconPaths.toFront, IconPaths.toBack };
        var th = app.FloatingTheme;
        for (int i = 0; i < 2; i++)
        {
            var cell = SelectionHandles.LayerCellRect(i, sel, dpi, app.FurnitureViewport, textBar);
            _scratch.Color = th.Hover;
            float cr = 8f * dpi;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY), cr, cr), _scratch);
            float icon = SelectionHandles.BarIconBoxLogical * dpi;
            DrawIcon(icons[i], (cell.MinX + cell.MaxX) * 0.5f - icon * 0.5f,
                     (cell.MinY + cell.MaxY) * 0.5f - icon * 0.5f, icon,
                     Brush(th.Text));
        }
    }


    /// <summary>
    /// 滚动条（样式 B：Win11 / Figma 那根细线）。
    ///
    /// 默认隐藏，滚动时浮现，**静止 3 秒淡出**（InkClass 实测 1.5 秒太快，
    /// 用户会找不到它）。画在浮动层上——它每帧都在变（淡出），进内容层就等于
    /// 每帧重画内容。
    ///
    /// 比例滑块靠"画布范围"（内容边界 ∪ 一屏，见 InkDocument.Extent）：
    /// 没有总高度就没有比例可算。画布只有一屏时干脆不画。
    ///
    /// 用 <c>_scratch</c> 而不是 Brush(颜色)：淡出时颜色每帧都在变，
    /// Brush() 是按颜色缓存的，会一帧往缓存里塞一个画刷。
    /// </summary>
    private void DrawScrollBar(InkEngine app)
    {
        if (!app.ScrollBarVisible) return;               // 2026-10-09 起停用（见 Engine.ScrollBarEnabled）
        if (!TryScrollBar(app, out var sb)) return;      // 画布只有一屏：没有滚动条

        // 悬停/拖动中：不淡出，而且加粗——"这根线能拖"靠它自己说，
        // 不靠换光标（手型按规范只能表示链接，四向箭头会被读成缩放）。
        bool hot = app.ScrollBarHover || app.ScrollBarDragging;
        float dpi = Dpi / 96f;
        double idle = (app.NowMs - app.ScrollBarActiveAtMs) / 1000.0;
        float alpha = hot ? 1f
                    : idle <= 3.0 ? 1f
                    : MathF.Max(0f, 1f - (float)((idle - 3.0) / 0.4));
        if (alpha <= 0.01f) return;

        float w = (hot ? 9f : 4f) * dpi;
        float x0 = sb.AxisX - w * 0.5f;
        float r = w * 0.5f;

        // 轨道：极淡，只用来"知道这儿有东西"，不抢视觉
        _scratch.Color = new Color4(0.50f, 0.52f, 0.55f, (hot ? 0.22f : 0.16f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.Top, x0 + w, sb.Bottom), r, r), _scratch);

        // 滑块：中灰带透明度，浅色深色背景上都立得住
        _scratch.Color = new Color4(0.47f, 0.49f, 0.53f, (hot ? 0.86f : 0.75f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.ThumbTop, x0 + w, sb.ThumbTop + sb.ThumbLen), r, r),
            _scratch);
    }

    /// <summary>
    /// 滚动条的几何（**屏幕坐标**，和 OriginX/Width 同一套）。
    /// 画和拖动共用它——两处各算一份，改一处忘一处是这类代码的经典翻车点。
    /// 返回 false = 现在不该有滚动条（画布只有一屏）。
    /// </summary>
    internal bool TryScrollBar(InkEngine app, out ScrollBarLayout l)
    {
        l = default;
        float dpi = Dpi / 96f;
        var extent = app.CanvasExtent;
        float extentH = extent.MaxY - extent.MinY;
        if (extentH <= Height + 1f) return false;

        float top = OriginY + 40f * dpi;
        float bottom = OriginY + Height - 40f * dpi;
        float trackLen = bottom - top;
        if (trackLen < 40f * dpi) return false;

        // 滑块长度 = 一屏 / 画布总高；位置 = 视口顶在画布里的比例
        float thumbLen = MathF.Max(40f * dpi, trackLen * (Height / extentH));
        float maxTravel = trackLen - thumbLen;
        float viewTop = OriginY - app.ViewOffsetY;       // 视口顶部对应的画布 Y
        float frac = extentH - Height > 1f
            ? (viewTop - extent.MinY) / (extentH - Height) : 0f;
        frac = Math.Clamp(frac, 0f, 1f);

        l = new ScrollBarLayout
        {
            AxisX = OriginX + Width - 8f * dpi,
            Top = top,
            Bottom = bottom,
            ThumbTop = top + maxTravel * frac,
            ThumbLen = thumbLen,
            MaxTravel = maxTravel,
            ExtentMinY = extent.MinY,
            ExtentH = extentH,
        };
        return true;
    }

    /// <summary>
    /// 画一个四边形。选中框现在是轴对齐的，但边框仍然用四条线拼：
    /// 这条路径同时要能画"框有自己的朝向"的情形（见 SelectionFrame 的注释），
    /// 换成 DrawRectangle 就等于把那条路堵死了。
    /// </summary>
    private void DrawQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float width)
    {
        _ctx.DrawLine(a, b, _scratch, width);
        _ctx.DrawLine(b, c, _scratch, width);
        _ctx.DrawLine(c, d, _scratch, width);
        _ctx.DrawLine(d, a, _scratch, width);
    }

    private void DrawDashed(Vector2 a, Vector2 b, float width) => DrawDashes(a, b, width, 7f, 5f);

    /// <summary>
    /// 点线（线型那一行的第 3 格）：节**短**、缝比节长，才看得出是"一点一点"
    /// 而不是"很短的一段段"。
    /// </summary>
    private void DrawDotted(Vector2 a, Vector2 b, float width) => DrawDashes(a, b, width, 2f, 4.5f);

    /// <summary>
    /// 手工画虚线 / 点线，<paramref name="dash"/> 与 <paramref name="gap"/> 是**逻辑**像素。
    ///
    /// 为什么这里手工画、不用 <see cref="Dashed"/> 那条 D2D 描边样式：
    /// 这个函数画的是**界面上的小预览**（选中框四角、线型格子里的那一段），
    /// 用的是"一条直线段"而不是图形几何，预览要的是"一眼看出虚实"，
    /// 固定节长比"按笔宽成比例"更可控。
    /// **画布上的图形走 D2D 原生样式**（见 <see cref="StyleFor"/>），两条路各自最合适。
    /// </summary>
    private void DrawDashes(Vector2 a, Vector2 b, float width, float dash, float gap)
    {
        float len = Vector2.Distance(a, b);
        if (len < 0.5f) return;
        var dir = (b - a) / len;
        float k = Dpi / 96f;
        dash *= k; gap *= k;
        for (float t = 0; t < len; t += dash + gap)
        {
            float t1 = MathF.Min(t + dash, len);
            _ctx.DrawLine(a + dir * t, a + dir * t1, _scratch, width);
        }
    }

    /// <summary>
    /// 画一个图标。几何按 24×24 的单位坐标缓存，这里用变换缩放到目标尺寸——
    /// 同一份几何能在不同尺寸和 DPI 下复用，不用重建。
    /// </summary>
    private void DrawIcon(string d, float x, float y, float size, ID2D1SolidColorBrush brush)
    {
        var geo = SvgPath.Get(d);
        if (geo == null) return;

        var saved = _ctx.Transform;
        _ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                       * Matrix3x2.CreateTranslation(x, y) * saved;
        _ctx.FillGeometry(geo, brush);
        _ctx.Transform = saved;
    }

    /// <summary>
    /// PPT 放映时贴在屏幕上的**那一条**控件（`[◀] [页码] [▶]`），外加它上面的两个浮层
    /// （点页码弹的**页号面板**、长按弹的菜单）。几何在 `PptBar`、交互状态在 `InkEngine`（Ppt.cs）。
    ///
    /// 它是**浮层**（和选中操作条同一层、同一套令牌）——位置只跟屏幕走（可拖、可记），
    /// 不跟工具条、不受贴边隐藏影响（用户 2026-09-26："外部就是 ppt 页面上，
    /// 不跟着批注软件走"）。
    /// </summary>
    private void DrawPptBar(InkEngine app)
    {
        if (!app.PageBarVisible) return;     // PPT 放映 / 文档共用；文档+穿透时收走（S2 全让开）
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        var bar = app.PptBarRect();
        float radius = MathF.Min(theme.CornerRadius * dpi, (bar.MaxY - bar.MinY) * 0.5f);

        DrawPanelCard(app, bar, radius);

        // **拖动中**：整条描边变强调色（"它现在拿在你手里"）。拖起来本身看得见，
        // 这个描边是给"抓起来那一刻"一个明确回应。
        if (app.PptBarDragging)
        {
            _scratch.Color = theme.ActiveBg;
            _ctx.DrawRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(bar.MinX, bar.MinY, bar.MaxX, bar.MaxY),
                                     radius, radius), _scratch, 2f * dpi);
        }

        float aw = PptBar.ArrowW * dpi;
        var la = new RectF { MinX = bar.MinX, MinY = bar.MinY, MaxX = bar.MinX + aw, MaxY = bar.MaxY };
        var ra = new RectF { MinX = bar.MaxX - aw, MinY = bar.MinY, MaxX = bar.MaxX, MaxY = bar.MaxY };
        DrawPptArrow(app, la, left: true, hot: app.PptBarHover == (int)PptBarZone.LeftArrow);
        DrawPptArrow(app, ra, left: false, hot: app.PptBarHover == (int)PptBarZone.RightArrow);

        // 页码格的**悬停高亮**（用户 2026-09-26 指出："页码那里没有悬停指示"）。
        // 箭头早就有、就它没有——而它恰恰是"按住不动弹菜单 / 点一下弹页号面板"的那块：
        // 没有高亮，整条上最该点的地方看着反而是"死的"。
        // 「⋮」删掉之后（2026-09-27）这一块就是**整个中格**，数字也在它的正中间。
        if (app.PptBarHover == (int)PptBarZone.Page)
        {
            var pc = PptBar.MidCell(bar, dpi);
            float cIn = 4f * dpi;
            var cBg = new Vortice.RawRectF(pc.MinX + cIn, pc.MinY + cIn, pc.MaxX - cIn, pc.MaxY - cIn);
            float cBr = MathF.Min(cBg.Right - cBg.Left, cBg.Bottom - cBg.Top) * 0.28f;
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(cBg, cBr, cBr), _scratch);
        }

        // 页码（**条上的主角**，13 逻辑像素，比角标大一档）：数字用正文色、"/ 总数"用次要色。
        // 分两段画就是"层级"——一个字号一口色的话，"3" 和 "/ 12" 一样重，读不出"我在第几页"。
        // ⚠ `ReadoutFormat*` 那一族的参数是**缩放比**（`px = 13 * dpi`），不是物理 DPI——
        // 传 `Dpi`（200% 屏上是 192）会把字号放大 192 倍、字被排到矩形外，看起来就是"字没画出来"。
        var mid = PptBar.MidCell(bar, dpi);
        var pfmt = PptPageFormat(dpi);
        string pCur = app.BarPageNow.ToString();
        string pRest = $" / {app.BarTotal}";
        float pWCur = MeasureTextWidth(pCur, pfmt);
        float pWRest = MeasureTextWidth(pRest, pfmt);
        float pX0 = (mid.MinX + mid.MaxX) * 0.5f - (pWCur + pWRest) * 0.5f;
        _scratch.Color = theme.Text;
        _ctx.DrawText(pCur, pfmt, new Rect(pX0, mid.MinY, pWCur, mid.MaxY - mid.MinY), _scratch);
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText(pRest, pfmt, new Rect(pX0 + pWCur, mid.MinY, pWRest, mid.MaxY - mid.MinY), _scratch);

        if (!app.BoardOn && app.PptMode && app.PptHintVisible) DrawPptHint(app);
        if (app.PptPagePanelOpen) DrawPptPagePanel(app);
        if (app.PptMenuOpen) DrawPptMenu(app);
    }

    /// <summary>
    /// **页号面板**：点页码弹出来的那张格子墙（点哪页跳哪页）。用户 2026-09-26 选的方案 B
    /// （"我们下面那个进度条可以不用，我们可以点击页码这里实现快速跳页"）。
    ///
    /// 为什么归引擎画（而不是界面）：它长在条的正上方、要跟着条的位置走，
    /// 而条本身就是引擎画的（见 PptBar）；交给界面会多一条"位置同步"的缝。
    /// 画法照抄现成的图库面板（同一套 DrawPanelCard + 悬停高亮）。
    /// </summary>
    // ---- 回放（墨迹 C）-------------------------------------------------------

    /// <summary>
    /// 回放中"正在长"的那一条。**已经出完的都在内容层（影子文档）里**，
    /// 这里只画前缀：自由笔迹 → 中心线前缀（压感仍走 D2D 变宽）；图形/图片 → 到点整条出现。
    /// </summary>
    private void DrawReplayCurrent(InkEngine app)
    {
        int idx = app.ReplayCurrentIndexNow;
        if (idx < 0 || idx >= app.ReplayStrokesNow.Count) return;
        var s = app.ReplayStrokesNow[idx];
        float param = app.ReplayCurrentParamNow;

        if (s.Kind != StrokeKind.Freehand || s.IsImage)
        {
            DrawStroke(s);                      // 图形 / 图片：整条出现（硬拆成逐点反而怪）
            return;
        }
        if (s.Points.Count == 0) return;
        if (s.RendersAsDot)
        {
            float rad = s.DotRadius();
            _ctx.FillEllipse(new Ellipse(new Vector2(s.Points[0].X, s.Points[0].Y),
                                         rad, rad),
                             Brush(s.Color));
            return;
        }

        float pointParam = param * (s.Points.Count - 1);
        if (pointParam <= 0.02f) return;        // 刚起步：还没有可画的段
        if (s.HasPressure && s.Dash == StrokeDash.Solid && s.Erased.Count == 0
            && DrawReplayPressurePrefix(s, pointParam))
            return;

        var geo = s.BuildCenterlinePrefix(Gfx.D2DFactory, pointParam);
        if (geo == null) return;
        try
        {
            _ctx.DrawGeometry(geo, Brush(s.Color), MathF.Max(1f, s.Width), Gfx.StyleFor(s.Dash));
        }
        finally { geo.Dispose(); }
    }

    /// <summary>压感前缀：同 <see cref="DrawPressureInk"/> 那条路，只是只喂到前缀。</summary>
    private bool DrawReplayPressurePrefix(Stroke s, float pointParam)
    {
        if (_ctx2 == null || _inkStyle == null || !InkAvailable || !PressureWidth.Enabled) return false;
        if (!BuildPressureSegments(s, out float startRadius, out int nSeg, pointParam)) return false;
        try
        {
            var ink = _ctx2.CreateInk(new Vortice.Direct2D1.InkPoint
            {
                X = s.Points[0].X, Y = s.Points[0].Y, Radius = startRadius,
            });
            try
            {
                ink.AddSegments(_inkSegs, (uint)nSeg);
                _ctx2.DrawInk(ink, Brush(s.Color), _inkStyle);
            }
            finally { ink.Dispose(); }
            return true;
        }
        catch { InkAvailable = false; return false; }
    }

    /// <summary>
    /// 回放控制条。和 PPT 条同一层、同一套画法（`DrawPanelCard` ＋ 主题色），
    /// 内容是播放器那一套：播放/暂停、四档倍速、进度、读数、关闭。
    /// </summary>
    private void DrawReplayBar(InkEngine app)
    {
        if (!app.ReplayActive) return;
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        var bar = app.ReplayBarRect();
        float radius = MathF.Min(theme.CornerRadius * dpi, (bar.MaxY - bar.MinY) * 0.5f);
        DrawPanelCard(app, bar, radius);

        bool Hot(ReplayBarZone z) => app.ReplayHoverZone == z;
        void FillZone(in RectF r, Color4 color)
        {
            _scratch.Color = color;
            float rad = 6f * dpi;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(r.MinX, r.MinY + 3f * dpi, r.MaxX, r.MaxY - 3f * dpi), rad, rad), _scratch);
        }

        // 播放 / 暂停
        var pr = ReplayBar.ZoneRect(bar, ReplayBarZone.PlayPause, dpi);
        if (Hot(ReplayBarZone.PlayPause)) FillZone(pr, theme.Hover);
        _scratch.Color = theme.Text;
        var pc = new Vector2((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
        if (app.ReplayPlaying)
        {
            float w = 4f * dpi, h = 15f * dpi, gap = 5f * dpi;
            _ctx.FillRectangle(new Vortice.RawRectF(pc.X - gap * 0.5f - w, pc.Y - h * 0.5f,
                                                    pc.X - gap * 0.5f, pc.Y + h * 0.5f), _scratch);
            _ctx.FillRectangle(new Vortice.RawRectF(pc.X + gap * 0.5f, pc.Y - h * 0.5f,
                                                    pc.X + gap * 0.5f + w, pc.Y + h * 0.5f), _scratch);
        }
        else
        {
            float h = 15f * dpi, w = 13f * dpi;
            using var path = Gfx.D2DFactory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(new Vector2(pc.X - w * 0.45f, pc.Y - h * 0.5f), FigureBegin.Filled);
                sink.AddLine(new Vector2(pc.X - w * 0.45f, pc.Y + h * 0.5f));
                sink.AddLine(new Vector2(pc.X + w * 0.55f, pc.Y));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _ctx.FillGeometry(path, _scratch);
        }

        // 四档倍速
        for (int i = 0; i < 4; i++)
        {
            var zone = (ReplayBarZone)(ReplayBarZone.SpeedHalf + i);
            var zr = ReplayBar.ZoneRect(bar, zone, dpi);
            float speed = ReplayBar.SpeedOfZone(zone);
            bool on = MathF.Abs(speed - app.ReplaySpeed) < 0.01f;
            var rr = new RoundedRectangle(new Vortice.RawRectF(zr.MinX, zr.MinY + 5f * dpi,
                                                              zr.MaxX, zr.MaxY - 5f * dpi), 6f * dpi, 6f * dpi);
            if (on) { _scratch.Color = theme.ActiveBg; _ctx.FillRoundedRectangle(rr, _scratch); }
            else if (Hot(zone)) { _scratch.Color = theme.Hover; _ctx.FillRoundedRectangle(rr, _scratch); }
            _scratch.Color = on ? theme.ActiveText : theme.TextMuted;
            _ctx.DrawText(ReplayBar.SpeedName(speed), ReadoutFormatSmall(dpi),
                          new Rect(zr.MinX, zr.MinY, zr.MaxX - zr.MinX, zr.MaxY - zr.MinY), _scratch);
        }

        // 进度：轨道 ＋ 已播 ＋ 滑钮
        var track = ReplayBar.ProgressTrack(bar, dpi);
        float totalMs = MathF.Max(1f, app.ReplayTotalMsNow);
        float frac = Math.Clamp(app.ReplayPosMsNow / totalMs, 0f, 1f);
        float cy = (track.MinY + track.MaxY) * 0.5f;
        float knobR = 5f * dpi;
        float x0 = track.MinX + knobR, x1 = track.MaxX - knobR;
        float kx = x0 + (x1 - x0) * frac;
        if (Hot(ReplayBarZone.Progress))
        {
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(track.MinX, cy - 6f * dpi, track.MaxX, cy + 6f * dpi),
                6f * dpi, 6f * dpi), _scratch);
        }
        _scratch.Color = theme.TextMuted;
        _ctx.DrawLine(new Vector2(x0, cy), new Vector2(x1, cy), _scratch, 3f * dpi);
        _scratch.Color = theme.ActiveBg;
        _ctx.DrawLine(new Vector2(x0, cy), new Vector2(kx, cy), _scratch, 3f * dpi);
        _scratch.Color = theme.Text;
        _ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), knobR, knobR), _scratch);

        // 读数（进度区右侧、关闭键左侧）
        float timeL = track.MaxX + 6f * dpi;
        float timeR = bar.MaxX - (ReplayBar.Pad + ReplayBar.CloseW + ReplayBar.Gap) * dpi;
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText($"{ReplayBar.TimeText(app.ReplayPosMsNow)} / {ReplayBar.TimeText(totalMs)}",
                      ReadoutFormatSmall(dpi),
                      new Rect(timeL, bar.MinY, MathF.Max(1f, timeR - timeL), bar.MaxY - bar.MinY), _scratch);

        // 关闭
        var cr = ReplayBar.ZoneRect(bar, ReplayBarZone.Close, dpi);
        if (Hot(ReplayBarZone.Close)) FillZone(cr, theme.Hover);
        _scratch.Color = theme.TextMuted;
        _ctx.DrawText("✕", ReadoutFormatSmall(dpi),
                      new Rect(cr.MinX, cr.MinY, cr.MaxX - cr.MinX, cr.MaxY - cr.MinY), _scratch);
    }

    private ID2D1SolidColorBrush SetBrush(Color4 c)
    {
        _scratch.Color = c;
        return _scratch;
    }

    private IDWriteTextFormat RollResultFormat(float dpi, float size)
    {
        float px = MathF.Max(12f, MathF.Round(size * dpi));
        if (_rollResultFormat == null || _rollResultFormatPx != px)
        {
            _rollResultFormat?.Dispose();
            _rollResultFormat = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _rollResultFormat.TextAlignment = TextAlignment.Center;
            _rollResultFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _rollResultFormatPx = px;
        }
        return _rollResultFormat;
    }


    private IDWriteTextFormat TimerValueFormat(float dpi)
    {
        float px = MathF.Max(12f, MathF.Round(19f * dpi));
        if (_timerValueFormat == null || _timerValueFormatPx != px)
        {
            _timerValueFormat?.Dispose();
            _timerValueFormat = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _timerValueFormat.TextAlignment = TextAlignment.Center;
            _timerValueFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _timerValueFormatPx = px;
        }
        return _timerValueFormat;
    }

    private IDWriteTextFormat TimerBigFormat(float dpi)
    {
        float px = MathF.Max(24f, MathF.Round(92f * dpi));
        if (_timerBigFormat == null || _timerBigFormatPx != px)
        {
            _timerBigFormat?.Dispose();
            _timerBigFormat = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _timerBigFormat.TextAlignment = TextAlignment.Center;
            _timerBigFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _timerBigFormatPx = px;
        }
        return _timerBigFormat;
    }

    private void DrawPptPagePanel(InkEngine app)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        app.PptPanelRect(out var panel);
        DrawPanelCard(app, panel, theme.CornerRadius * dpi);

        int total = Math.Max(1, app.BarTotal);
        var fmt = ReadoutFormatSmall(dpi);
        for (int i = 0; i < total; i++)
        {
            app.PptPanelCellRectAt(i, out var cell);
            int page = i + 1;
            bool cur = page == app.BarPageNow;           // 当前页：实心强调底
            bool hot = app.PptBarHover == 200 + i;

            if (cur || hot)
            {
                float inset = 2f * dpi;
                var bg = new Vortice.RawRectF(cell.MinX + inset, cell.MinY + inset,
                                              cell.MaxX - inset, cell.MaxY - inset);
                float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.25f;
                _scratch.Color = cur ? theme.ActiveBg : theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            _scratch.Color = cur ? theme.ActiveText : theme.Text;
            _ctx.DrawText(page.ToString(), fmt,
                          new Rect(cell.MinX, cell.MinY, cell.MaxX - cell.MinX, cell.MaxY - cell.MinY),
                          _scratch);
        }
    }

    /// <summary>页码的文字格式（**13 逻辑像素**：条上的主角，比角标 11 大一档）。</summary>
    private IDWriteTextFormat PptPageFormat(float dpi)
    {
        float px = MathF.Max(11f, MathF.Round(13f * dpi));
        if (_pptPageFmt == null || _pptPageFmtPx != px)
        {
            _pptPageFmt?.Dispose();
            _pptPageFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _pptPageFmt.TextAlignment = TextAlignment.Center;
            _pptPageFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _pptPageFmt.WordWrapping = WordWrapping.NoWrap;
            _pptPageFmtPx = px;
        }
        return _pptPageFmt;
    }

    /// <summary>
    /// 翻页箭头：**自绘**（两条线组成的 chevron）。
    ///
    /// 为什么不用 `IconPaths`：那张表是 `tools/gen-icons.ps1` 从官方图标库生成的
    /// （文件头写着"请勿手改"），而且里面没有 chevron（`chevronRight` 在**界面**那本
    /// `InkUi/Icons.g.cs` 里，引擎看不到）——为两个箭头改生成流程不划算，
    /// 两条线更简单，也不会随图标库版本漂移。
    ///
    /// **视觉重量**（用户 2026-09-26："我感觉是这个翻页箭头太大了"）：
    ///   · **尺寸基本不缩**：我们高 15 逻辑像素 / 卡片 56 = 27%，而 Inkeys 的箭头图标框
    ///     是 40 / 组件 60 = **67%**（数据见 调研-PPT模式-参考InkClass.md）——
    ///     我们本来就比人家小一半多，再缩触摸屏上就点不中了，这里只微收 7%；
    ///   · **真正重的是笔画**：9 像素宽的 chevron 配 2 逻辑像素线宽（物理 4px），
    ///     短笔画上显得又粗又黑 → 线宽降到 1.5（对齐项目图标规范"1.4~1.5 描边"，
    ///     见 调研-界面-假面板与图标.md）；
    ///   · 常态用**次要色**、悬停才转深——页码才是主角，箭头是配角。
    /// </summary>
    private void DrawPptArrow(InkEngine app, in RectF box, bool left, bool hot)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        if (hot)
        {
            float inset = 4f * dpi;
            var bg = new Vortice.RawRectF(box.MinX + inset, box.MinY + inset,
                                          box.MaxX - inset, box.MaxY - inset);
            float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.3f;
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
        }

        float cx = (box.MinX + box.MaxX) * 0.5f, cy = (box.MinY + box.MaxY) * 0.5f;
        float h = 7f * dpi;        // 半高（原 7.5，微收）
        float w = 4.2f * dpi;      // 两翼相对中心的偏移（原 4.5）
        float wing = left ? w : -w;         // 左箭头：两翼在右边、尖端在左
        float tipX = cx - wing;
        _scratch.Color = hot ? theme.Text : theme.TextMuted;
        float lw = 1.5f * dpi;
        _ctx.DrawLine(new Vector2(cx + wing, cy - h), new Vector2(tipX, cy), _scratch, lw);
        _ctx.DrawLine(new Vector2(tipX, cy), new Vector2(cx + wing, cy + h), _scratch, lw);
    }

    /// <summary>
    /// 菜单（**长按页码格**开）。三项**一律四字**（用户 2026-09-26："菜单可以四字对齐吗"），
    /// 所以文字天然对齐、不用逐项量宽；分隔线是"一项一组"。
    ///
    /// 两个临时样子：
    ///   · 「墨迹保存」右边跟一个"开 / 关"（开关项）；
    ///   · 「墨迹清空」点过一次之后，整行文字变成「再点确认」并上强调底——
    ///     这是**不弹窗的确认**（我们的规矩是"屏幕上一个字都不留"，见 README 的提示条那段）。
    /// </summary>
    private void DrawPptMenu(InkEngine app)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        app.PptMenuRect(out var menu);
        DrawPanelCard(app, menu, theme.CornerRadius * dpi);

        var fmt = ReadoutFormatSmall(dpi);
        float padX = 16f * dpi;

        for (int i = 0; i < InkEngine.PptMenuItemCount; i++)
        {
            app.PptMenuItemRectAt(i, out var item);

            // 分隔线画在"这一项和上一项之间"（一眼看出分组）
            if (InkEngine.PptMenuDividerBefore(i) && i > 0)
            {
                app.PptMenuItemRectAt(i - 1, out var prev);
                float y = (prev.MaxY + item.MinY) * 0.5f;
                _scratch.Color = new Color4(theme.PanelBorder.R, theme.PanelBorder.G,
                                            theme.PanelBorder.B, 0.6f);
                _ctx.DrawLine(new Vector2(item.MinX + 10f * dpi, y),
                              new Vector2(item.MaxX - 10f * dpi, y), _scratch, 1f * dpi);
            }

            bool danger = app.PptMenuItemDanger(i);
            bool waiting = danger && app.PptClearConfirm;       // 正在等确认：整行上强调底
            bool enabled = app.PptMenuItemEnabled(i);           // 置灰项：不亮、不高亮、点了不动
            if (enabled && (app.PptBarHover == 100 + i || waiting))
            {
                float inset = 3f * dpi;
                var bg = new Vortice.RawRectF(item.MinX + inset, item.MinY + inset,
                                              item.MaxX - inset, item.MaxY - inset);
                float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.25f;
                _scratch.Color = waiting ? theme.ActiveBg : theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            // 文字：框宽**按文字自己量**（而不是整行）——这样四字从左起一样的位置开始，
            // 就是"四字对齐"；用整行居中排的话，字数一变（"再点确认"）就会左右晃。
            string label = app.PptMenuItemText(i);
            float lw = MeasureTextWidth(label, fmt);
            _scratch.Color = waiting ? theme.ActiveText : enabled ? theme.Text : theme.TextMuted;
            _ctx.DrawText(label, fmt,
                          new Rect(item.MinX + padX, item.MinY, lw, item.MaxY - item.MinY),
                          _scratch);

            // 右边的状态（开关 = 开/关；回放 = N 笔）
            string st = app.PptMenuItemStatus(i);
            if (st != null)
            {
                float sw = MeasureTextWidth(st, fmt);
                _scratch.Color = i == 0 && app.PptAutoSaveOn ? theme.ActiveBg : theme.TextMuted;
                _ctx.DrawText(st, fmt,
                              new Rect(item.MaxX - padX - sw, item.MinY, sw, item.MaxY - item.MinY),
                              _scratch);
            }
        }
    }

    /// <summary>
    /// **引导**：进放映的头 1.5 秒，在条的上方浮一行小字（"长按页码可呼出菜单"）。
    ///
    /// 位置**和菜单同一处**——它淡出之后菜单正好在那儿出现，老师会自然地把两件事连起来。
    /// 生命周期见 <see cref="InkEngine.PptHintVisible"/>：**每次进放映都提示一遍、只停 1.5 秒**
    /// （用户 2026-09-27 定的）——因为「⋮」删掉之后菜单只剩长按这一条路，
    /// "只提示一次"会让老师第二次上课就想不起来它了。
    /// </summary>
    private void DrawPptHint(InkEngine app)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        app.PptMenuRect(out var anchor);            // 借菜单的位置（就是条的上方）
        var hint = new RectF
        {
            MinX = anchor.MinX, MinY = anchor.MinY,
            MaxX = anchor.MinX + 172f * dpi, MaxY = anchor.MinY + 32f * dpi,
        };
        DrawPanelCard(app, hint, 8f * dpi);
        _scratch.Color = theme.Text;
        // 文案（2026-10-02 第五轮）：入口从"长按"改成"点一下"之后，这句话只说
        // "点它出来的是页码跳转菜单"——用户拍板的短句，指点性质靠"点页码："三个字。
        _ctx.DrawText(Loc.Tr("点页码：页码跳转菜单"), ReadoutFormatSmall(dpi),
                      new Rect(hint.MinX, hint.MinY, hint.MaxX - hint.MinX, hint.MaxY - hint.MinY),
                      _scratch);
    }

    /// <summary>
    /// 轻提示（toast）的卡片矩形：窗口顶部居中。**量宽、绘制、脏区都读这一份**。
    /// 用窗口坐标（0..Width/Height），不跟虚拟桌面原点走，多屏下也不会跑到别的屏上。
    /// </summary>
    private RectF ToastRect(InkEngine app)
    {
        float dpi = app.UiUnit;
        string text = Loc.Tr(app.ToastText ?? "");
        float textW = MeasureTextWidth(text, ToastFormat(dpi));
        float maxW = MathF.Max(120f * dpi, Width - 24f * dpi);
        float w = MathF.Min(maxW, MathF.Max(180f * dpi, 28f * dpi + textW));
        float h = 36f * dpi;
        float x = MathF.Max(12f * dpi, (Width - w) * 0.5f);
        float y = 22f * dpi;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    private IDWriteTextFormat ToastFormat(float dpi)
    {
        float px = MathF.Max(10f, MathF.Round(12.5f * dpi));
        if (_toastFmt == null || _toastFmtPx != px)
        {
            _toastFmt?.Dispose();
            _toastFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _toastFmt.TextAlignment = TextAlignment.Center;
            _toastFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _toastFmtPx = px;
        }
        return _toastFmt;
    }

    /// <summary>
    /// 选中条弹层的行文字格式：左对齐（`⋯`）或居中（AI），
    /// **两种都 ParagraphAlignment.Center**，保证文字和左侧图标垂直对齐。
    /// </summary>
    private IDWriteTextFormat MenuTextFormat(float dpi, bool center)
    {
        float px = MathF.Max(10f, MathF.Round(12.5f * dpi));
        if (center)
        {
            if (_menuCenterFmt == null || _menuCenterPx != px)
            {
                _menuCenterFmt?.Dispose();
                _menuCenterFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                    FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
                _menuCenterFmt.TextAlignment = TextAlignment.Center;
                _menuCenterFmt.ParagraphAlignment = ParagraphAlignment.Center;
                _menuCenterPx = px;
            }
            return _menuCenterFmt;
        }
        if (_menuLeftFmt == null || _menuLeftPx != px)
        {
            _menuLeftFmt?.Dispose();
            _menuLeftFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _menuLeftFmt.TextAlignment = TextAlignment.Leading;
            _menuLeftFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _menuLeftPx = px;
        }
        return _menuLeftFmt;
    }

    /// <summary>
    /// 画轻提示。**只画、不接命中**：提示显示期间，笔/鼠标/触摸照常落到
    /// 画布和按钮上；没有模态、没有焦点切换。
    /// </summary>
    private void DrawToast(InkEngine app)
    {
        if (!app.ToastVisible) return;
        float dpi = app.UiUnit;
        var r = ToastRect(app);
        var theme = app.FloatingTheme;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        float radius = MathF.Min(9f * dpi, (r.MaxY - r.MinY) * 0.5f);

        _scratch.Color = theme.Panel;
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch, 1f * dpi);

        _scratch.Color = theme.Text;
        _ctx.DrawText(Loc.Tr(app.ToastText), ToastFormat(dpi),
            new Rect(r.MinX + 10f * dpi, r.MinY, r.MaxX - r.MinX - 20f * dpi, r.MaxY - r.MinY),
            _scratch);
    }

    // ---- 引擎侧悬停提示（Tooltip；2026-10-02）--------------------------------
    //
    // 状态在 `InkEngine.Tooltip.cs`（谁悬停、延迟两档 1.5s/400ms、文案表）；这里只负责
    // "量文字、摆位置、画卡片、报脏区"。规矩和界面层那一套一致，规格见
    // 《调研-悬停提示-Tooltip.md》——两套各自实现（两个工程画在不同表面上），
    // 但延迟、内容格式、外观语言必须一致。

    private IDWriteTextFormat TipTitleFormat(float dpi)
    {
        float px = MathF.Max(10f, MathF.Round(12.5f * dpi));
        if (_tipTitleFmt == null || _tipTitleFmtPx != px)
        {
            _tipTitleFmt?.Dispose();
            _tipTitleFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _tipTitleFmt.TextAlignment = TextAlignment.Leading;
            _tipTitleFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _tipTitleFmtPx = px;
        }
        return _tipTitleFmt;
    }

    private IDWriteTextFormat TipNoteFormat(float dpi)
    {
        float px = MathF.Max(9f, MathF.Round(11f * dpi));
        if (_tipNoteFmt == null || _tipNoteFmtPx != px)
        {
            _tipNoteFmt?.Dispose();
            _tipNoteFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _tipNoteFmt.TextAlignment = TextAlignment.Leading;
            _tipNoteFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _tipNoteFmtPx = px;
        }
        return _tipNoteFmt;
    }

    /// <summary>
    /// 提示卡的矩形（物理像素，和浮层同一套屏幕坐标）：宽度按文字量、横向夹进屏幕，
    /// 纵向上**优先锚点上方 8 像素**，顶到屏幕上沿就翻到下方。
    /// 量宽、摆位、绘制、脏区**都读这一份**——各算一遍迟早差几个像素（浮层的老教训）。
    /// </summary>
    private RectF TooltipCardRect(InkEngine app)
    {
        if (!app.TooltipShown || string.IsNullOrEmpty(app.TooltipTitle)) return RectF.Empty;
        float dpi = Dpi / 96f;
        var titleFmt = TipTitleFormat(dpi);
        var noteFmt = TipNoteFormat(dpi);

        float titleW = MeasureTextWidth(Loc.Tr(app.TooltipTitle), titleFmt);
        float keyW = string.IsNullOrEmpty(app.TooltipKey)
            ? 0f : 12f * dpi + MeasureTextWidth(app.TooltipKey, titleFmt);
        float noteW = string.IsNullOrEmpty(app.TooltipNote)
            ? 0f : MeasureTextWidth(Loc.Tr(app.TooltipNote), noteFmt);
        float padX = 12f * dpi, padY = 8f * dpi;
        float w = MathF.Max(titleW + keyW, noteW) + padX * 2f;
        float h = (string.IsNullOrEmpty(app.TooltipNote) ? 20f : 36f) * dpi + padY * 2f;

        var a = app.TooltipAnchorNow;
        var scr = app.TipScreenNow;
        float cx = (a.MinX + a.MaxX) * 0.5f;
        float x0 = cx - w * 0.5f, x1 = cx + w * 0.5f;
        float lo = scr.MinX + 4f * dpi, hi = scr.MaxX - 4f * dpi;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }

        float gap = 8f * dpi;
        float top = a.MinY - gap - h;
        if (top < scr.MinY + 4f * dpi) top = a.MaxY + gap;
        return new RectF { MinX = x0, MinY = top, MaxX = x1, MaxY = top + h };
    }

    private void DrawEngineTooltip(InkEngine app)
    {
        if (!app.TooltipShown) return;
        var box = TooltipCardRect(app);
        if (box.IsEmpty) return;

        float dpi = Dpi / 96f;
        var theme = app.FloatingTheme;
        float radius = MathF.Min(8f * dpi, (box.MaxY - box.MinY) * 0.5f);
        DrawPanelCard(app, box, radius);

        var titleFmt = TipTitleFormat(dpi);
        var noteFmt = TipNoteFormat(dpi);
        float padX = 12f * dpi, padY = 8f * dpi, lineH = 20f * dpi;
        bool hasNote = !string.IsNullOrEmpty(app.TooltipNote);
        string title = Loc.Tr(app.TooltipTitle);
        string note = Loc.Tr(app.TooltipNote);

        float titleW = MeasureTextWidth(title, titleFmt);
        float titleY = hasNote ? box.MinY + padY : (box.MinY + box.MaxY) * 0.5f - lineH * 0.5f;
        _scratch.Color = theme.Text;
        _ctx.DrawText(title, titleFmt,
                      new Rect(box.MinX + padX, titleY, MathF.Max(1f, titleW + 4f), lineH), _scratch);
        if (!string.IsNullOrEmpty(app.TooltipKey))
        {
            _scratch.Color = theme.TextMuted;
            _ctx.DrawText(app.TooltipKey, titleFmt,
                          new Rect(box.MinX + padX + titleW + 12f * dpi, titleY, Width + 4096f, lineH),
                          _scratch);
        }
        if (hasNote)
        {
            _scratch.Color = theme.TextMuted;
            _ctx.DrawText(note, noteFmt,
                          new Rect(box.MinX + padX, box.MinY + padY + lineH,
                                   box.MaxX - box.MinX - padX * 2f, lineH),
                          _scratch);
        }
    }

    /// <summary>
    /// 画激光笔的轨迹：**红边 + 白芯的干净双层带**，不再画外围光晕。
    ///
    /// 沿革：2026-09-27 按 ClassIn 参考图做过"白芯 + 红边 + 十三层柔光"，
    /// 2026-10-16 为压掉光晕台阶又从 6 层加到 13 层；2026-10-18 用户反馈
    /// "外围的光晕没有必要，总感觉影影绰绰、一层一层的"，所以把柔光整层撤掉，
    /// 只留真正承担可见性的红边和白芯：
    ///   · 红边：饱和红，实心，半径 0.50d；白芯盖掉中间后两侧各留 0.20d，
    ///     在白板 / 深色 PPT 上都看得见；
    ///   · 白芯：纯白，半径 0.30d（整条白芯占红带六成），细笔也不取消；
    ///   · 两端仍补圆帽（半径 = 半宽），否则带子端口是平头，像被切断。
    ///
    /// 为什么不是把光晕调淡：十三层是半透明带子叠出来的，层与层之间天然有台阶；
    /// 在真机上看起来就是"一层一层"。用户要的是清楚的指示线，不是发光体；
    /// 直接撤掉比继续调参数更符合使用场景，也省掉每条轨迹每帧 13 次几何填充。
    ///
    /// ⚠ 粗细按每条自己记的 `Stroke.WidthLogical` 读，不读当前值：
    ///   否则画完一条、抬手调粗、再画一条，前一条会跟着变胖。
    /// ⚠ 用 SourceOver（不是加法混合）：加法在白板/浅色 PPT 上会把红色加亮到
    ///   看不见；白芯在白底上本来就看不见，真正让它立住的是那圈红边。
    /// </summary>
    private void DrawLaser(InkEngine app)
    {
        var strokes = app.Laser.Strokes;
        if (strokes.Count == 0) return;

        var edge = new Color4(0.85f, 0.01f, 0.08f, 1f);    // 红边：饱和红
        var core = new Color4(1f, 1f, 1f, 1f);             // 白芯：纯白

        foreach (var s in strokes)
        {
            int n = s.Points.Count;
            if (n < 2) continue;
            float a = 1f - s.Fade;              // 停留期 = 1（一点没淡）；淡出时一路到 0
            if (a <= 0.001f) continue;

            float d = MathF.Max(6f, s.WidthLogical * app.DpiScale);   // 红色主带的直径

            // 红边（白芯随后盖掉中间，只在两侧各留 0.20d 的红）
            Layer(s.Points, d * 0.50f, new Color4(edge.R, edge.G, edge.B, 0.96f * a));

            // 白芯（占红带六成宽；不分粗细都画，细笔时两边自然只剩细红线）
            Layer(s.Points, MathF.Max(0.6f, d * 0.30f), new Color4(core.R, core.G, core.B, a));
        }

        // 一层：等宽的带子 ＋ 两端的圆帽
        void Layer(IReadOnlyList<InkPoint> pts, float half, in Color4 col)
        {
            using (var geo = BuildTaperedRibbon(pts, 0, pts.Count - 1, half, half))
            {
                if (geo == null) return;
                _scratch.Color = col;
                _ctx.FillGeometry(geo, _scratch);
            }
            _scratch.Color = col;
            var head = pts[0]; var tail = pts[pts.Count - 1];
            _ctx.FillEllipse(new Ellipse(new Vector2(head.X, head.Y), half, half), _scratch);
            _ctx.FillEllipse(new Ellipse(new Vector2(tail.X, tail.Y), half, half), _scratch);
        }
    }

    private static ID2D1PathGeometry BuildPolyline(IReadOnlyList<InkPoint> pts, int start, int end)
    {
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(new Vector2(pts[start].X, pts[start].Y), FigureBegin.Hollow);
        for (int i = start + 1; i <= end; i++)
            sink.AddLine(new Vector2(pts[i].X, pts[i].Y));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// Filled ribbon along a slice of the trail, tapering from headHalf at the
    /// newest point down to tailHalf at the oldest. Returns null when the slice
    /// is too short to form a polygon.
    /// </summary>
    private static ID2D1PathGeometry BuildTaperedRibbon(
        IReadOnlyList<InkPoint> pts, int start, int end, float headHalf, float tailHalf)
    {
        start = Math.Max(0, start);
        end = Math.Min(pts.Count - 1, end);
        int n = end - start + 1;
        if (n < 2) return null;

        var outline = new Vector2[n * 2];
        for (int i = 0; i < n; i++)
        {
            int idx = start + i;
            int a = idx > start ? idx - 1 : idx;
            int b = idx < end ? idx + 1 : idx;
            float dx = pts[b].X - pts[a].X;
            float dy = pts[b].Y - pts[a].Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            float nx = -dy, ny = dx;

            float t = i / (float)(n - 1);          // 0 = tail, 1 = head
            float hw = tailHalf + (headHalf - tailHalf) * t;

            outline[i] = new Vector2(pts[idx].X + nx * hw, pts[idx].Y + ny * hw);
            outline[n * 2 - 1 - i] = new Vector2(pts[idx].X - nx * hw, pts[idx].Y - ny * hw);
        }

        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.SetFillMode(Vortice.Direct2D1.FillMode.Winding);
            sink.BeginFigure(outline[0], FigureBegin.Filled);
            for (int i = 1; i < outline.Length; i++) sink.AddLine(outline[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

}
