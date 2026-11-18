// [职责] PPT 条/回放条/滚动条绘制。
// [测试] --ppttest

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
    /// PPT 放映时贴在屏幕上的**那一条**控件（`[◀] [页码] [▶] │ [☰]`），外加它上面的两个浮层
    /// （点页码弹的**页号面板**、点 ☰ 弹的菜单）。几何在 `PptBar`、交互状态在 `InkEngine`（Ppt.cs）。
    ///
    /// 它是**浮层**（和选中操作条同一层、同一套令牌）——位置只跟屏幕走（可拖、可记），
    /// 不跟工具条、不受贴边隐藏影响（用户 2026-09-26："外部就是 ppt 页面上，
    /// 不跟着批注软件走"）。
    ///
    /// **顺序固定、不镜像**（2026-11-15）：翻页组永远在左、☰ 永远在最右。条拖到屏幕右半边
    /// 也不翻过来——老师的肌肉记忆比"离手近"重要。
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

        DrawPptArrow(app, PptBar.ArrowCell(bar, left: true, dpi),
                     left: true, hot: app.PptBarHover == (int)PptBarZone.LeftArrow);
        DrawPptArrow(app, PptBar.ArrowCell(bar, left: false, dpi),
                     left: false, hot: app.PptBarHover == (int)PptBarZone.RightArrow);

        // **文档模式**（PDF / 图片）另有一条**独立缩放条**（右下角，见 DrawDocZoomBar）。
        // 翻页条本身仍是 206 宽、一个像素都不变；缩放条不跟它拴在一起。
        if (app.DocZoomBar) DrawDocZoomBar(app);

        // 1px 分隔线：把"翻页组"和"菜单键"分成两块（页码组内部一对箭头 + 页码仍然是一套）。
        // 画在 8 宽分隔带的正中间，上下各留 10——不是通栏，看着不割裂。
        {
            float dx = PptBar.DividerX(bar, dpi);
            _scratch.Color = new Color4(theme.PanelBorder.R, theme.PanelBorder.G,
                                        theme.PanelBorder.B, 0.7f);
            _ctx.DrawLine(new Vector2(dx, bar.MinY + 10f * dpi),
                          new Vector2(dx, bar.MaxY - 10f * dpi), _scratch, 1f * dpi);
        }

        // 页码格的**悬停高亮**（用户 2026-09-26 指出："页码那里没有悬停指示"）。
        // 它现在是"点一下 = 页号面板 / 按住拖 = 挪条"的那块：没有高亮，整条上最该点的
        // 地方看着反而是"死的"。数字也在这一块的正中间（见下面的绘制）。
        if (app.PptBarHover == (int)PptBarZone.Page)
        {
            var pc = PptBar.MidCell(bar, dpi);
            float cIn = 4f * dpi;
            var cBg = new Vortice.RawRectF(pc.MinX + cIn, pc.MinY + cIn, pc.MaxX - cIn, pc.MaxY - cIn);
            float cBr = MathF.Min(cBg.Right - cBg.Left, cBg.Bottom - cBg.Top) * 0.28f;
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(cBg, cBr, cBr), _scratch);
        }

        // 菜单键（最右 ☰）：悬停有底，三横线自绘（和箭头一样不借 `IconPaths`，
        // 那张表是生成物、且界面那本引擎看不到）。
        DrawPptMenuButton(app, PptBar.MenuCell(bar, dpi),
                          hot: app.PptBarHover == (int)PptBarZone.Menu);

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
    /// 文档模式的**缩放条**（独立的一条）：`[−] [100% ▾] [+]`。参考浏览器 PDF 阅读器
    /// 右下角那颗独立缩放胶囊——和翻页条分开之后，窄窗口里两条各让各的，不会挤成一条 366。
    ///
    /// 悬停 / 拖动反馈和翻页条同一套：悬停格有底、拖动中整条描边变强调色。
    /// 档位浮层锚在它的百分比格上、开在它上方（几何见 <see cref="PptBar.ZoomMenuRect"/>）。
    /// </summary>
    private void DrawDocZoomBar(InkEngine app)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        var bar = app.DocZoomBarRect();
        float radius = MathF.Min(theme.CornerRadius * dpi, (bar.MaxY - bar.MinY) * 0.5f);
        DrawPanelCard(app, bar, radius);

        if (app.DocZoomBarDragging)
        {
            _scratch.Color = theme.ActiveBg;
            _ctx.DrawRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(bar.MinX, bar.MinY, bar.MaxX, bar.MaxY),
                                     radius, radius), _scratch, 2f * dpi);
        }

        DrawPptZoomButton(app, PptBar.ZoomOutCell(bar, dpi), plus: false,
                          hot: app.PptBarHover == (int)PptBarZone.ZoomOut,
                          enabled: app.DocZoomOutEnabled);
        DrawPptZoomButton(app, PptBar.ZoomInCell(bar, dpi), plus: true,
                          hot: app.PptBarHover == (int)PptBarZone.ZoomIn,
                          enabled: app.DocZoomInEnabled);
        DrawPptZoomPercent(app, PptBar.ZoomPctCell(bar, dpi),
                           hot: app.PptBarHover == (int)PptBarZone.ZoomPct,
                           open: app.PptZoomMenuOpen);
        if (app.PptZoomMenuOpen) DrawPptZoomMenu(app);
    }

    /// <summary>
    /// 缩放条里的 − / ＋：**自绘**（和翻页箭头同一套视觉重量：1.5 线宽、次要色，
    /// 悬停转深）。到头的那一颗**置灰**——"点了没反应"是最伤人的，先让它看着不能点。
    /// </summary>
    private void DrawPptZoomButton(InkEngine app, in RectF box, bool plus, bool hot, bool enabled)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;

        if (hot && enabled)
        {
            float inset = 4f * dpi;
            var bg = new Vortice.RawRectF(box.MinX + inset, box.MinY + inset,
                                          box.MaxX - inset, box.MaxY - inset);
            float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.3f;
            _scratch.Color = theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
        }

        var c = hot && enabled ? theme.Text : theme.TextMuted;
        _scratch.Color = enabled ? c : new Color4(c.R, c.G, c.B, 0.34f);
        float cx = (box.MinX + box.MaxX) * 0.5f, cy = (box.MinY + box.MaxY) * 0.5f;
        float half = 7f * dpi;
        float lw = 1.6f * dpi;
        _ctx.DrawLine(new Vector2(cx - half, cy), new Vector2(cx + half, cy), _scratch, lw);
        if (plus)
            _ctx.DrawLine(new Vector2(cx, cy - half), new Vector2(cx, cy + half), _scratch, lw);
    }

    /// <summary>
    /// 百分比格：`100% ▾`。数字是这一组的"主角"（13 逻辑像素、正文字重），
    /// 小三角只表示"点得开一张档位表"；悬停 / 浮层开着时整格有底（和页码格同一个反馈）。
    /// </summary>
    private void DrawPptZoomPercent(InkEngine app, in RectF box, bool hot, bool open)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;

        if (hot || open)
        {
            float inset = 4f * dpi;
            var bg = new Vortice.RawRectF(box.MinX + inset, box.MinY + inset,
                                          box.MaxX - inset, box.MaxY - inset);
            float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.28f;
            _scratch.Color = open ? theme.ActiveBg : theme.Hover;
            _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
        }

        float caretW = 12f * dpi;
        var fmt = PptPageFormat(dpi);
        _scratch.Color = open ? theme.ActiveText : (hot ? theme.Text : theme.TextMuted);
        _ctx.DrawText(app.DocZoomPercentText, fmt,
                      new Rect(box.MinX, box.MinY, MathF.Max(1f, box.MaxX - box.MinX - caretW),
                               box.MaxY - box.MinY),
                      _scratch);

        // ▾（两笔向下的小尖）：提示"这里点得开一张表"
        float cx = box.MaxX - 7f * dpi, cy = (box.MinY + box.MaxY) * 0.5f;
        float w = 3.4f * dpi, h = 2f * dpi, lw = 1.4f * dpi;
        _scratch.Color = hot || open ? theme.Text : theme.TextMuted;
        _ctx.DrawLine(new Vector2(cx - w, cy - h), new Vector2(cx, cy + h), _scratch, lw);
        _ctx.DrawLine(new Vector2(cx, cy + h), new Vector2(cx + w, cy - h), _scratch, lw);
    }

    /// <summary>
    /// 缩放档位浮层（点百分比弹出来）：第一组"适合宽度 / 适合整页"，第二组固定百分比。
    /// 当前档右侧画对勾（**只有一处**：100% 时对勾归"适合宽度"，不在"100%"上重复打）。
    /// 浮层和菜单 / 页号面板同一套画法（DrawPanelCard + 悬停高亮），只是更小一号。
    /// </summary>
    private void DrawPptZoomMenu(InkEngine app)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        app.PptZoomMenuRect(out var menu);
        DrawPanelCard(app, menu, theme.CornerRadius * dpi);

        var fmt = ReadoutFormatSmall(dpi);
        float padX = 12f * dpi;

        for (int i = 0; i < InkEngine.DocZoomPresetCount; i++)
        {
            app.PptZoomMenuItemRectAt(i, out var item);

            // 组间分隔线：适合宽度 / 适合整页 | 固定百分比（画在"50%"之前）
            if (i == 2)
            {
                app.PptZoomMenuItemRectAt(i - 1, out var prev);
                float y = (prev.MaxY + item.MinY) * 0.5f;
                _scratch.Color = new Color4(theme.PanelBorder.R, theme.PanelBorder.G,
                                            theme.PanelBorder.B, 0.6f);
                _ctx.DrawLine(new Vector2(item.MinX + 8f * dpi, y),
                              new Vector2(item.MaxX - 8f * dpi, y), _scratch, 1f * dpi);
            }

            bool hot = app.PptBarHover == 300 + i;
            if (hot)
            {
                float inset = 3f * dpi;
                var bg = new Vortice.RawRectF(item.MinX + inset, item.MinY + inset,
                                              item.MaxX - inset, item.MaxY - inset);
                float br = MathF.Min(bg.Right - bg.Left, bg.Bottom - bg.Top) * 0.25f;
                _scratch.Color = theme.Hover;
                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            string label = Loc.Tr(app.DocZoomPresetText(i));
            float lw = MeasureTextWidth(label, fmt);
            _scratch.Color = theme.Text;
            _ctx.DrawText(label, fmt,
                          new Rect(item.MinX + padX, item.MinY, lw, item.MaxY - item.MinY),
                          _scratch);

            if (app.DocZoomPresetChecked(i))
            {
                float cw = MeasureTextWidth("✓", fmt);
                _scratch.Color = theme.ActiveBg;
                _ctx.DrawText("✓", fmt,
                              new Rect(item.MaxX - padX - cw, item.MinY, cw, item.MaxY - item.MinY),
                              _scratch);
            }
        }
    }

    /// <summary>
    /// 菜单键（最右 ☰）：**三横线自绘**。
    /// 尺寸按"和箭头同一档视觉重量"定：线长 15、线宽 1.6、行距 4.6（总高 ≈ 10.8），
    /// 比 40 的点击格小一圈，扫一眼是"更多"，不抢页码。悬停时三条线一起变深 ＋ 底色，
    /// 和箭头 / 页码格同一套反馈。
    /// </summary>
    private void DrawPptMenuButton(InkEngine app, in RectF box, bool hot)
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
        float half = 7.5f * dpi;        // 半宽：三条线一样长
        float step = 4.6f * dpi;        // 行距
        _scratch.Color = hot ? theme.Text : theme.TextMuted;
        float lw = 1.6f * dpi;
        for (int i = -1; i <= 1; i++)
        {
            float y = cy + i * step;
            _ctx.DrawLine(new Vector2(cx - half, y), new Vector2(cx + half, y), _scratch, lw);
        }
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
        if ((s.HasPressure || StrokeTaper.Eligible(s))
            && s.Dash == StrokeDash.Solid && s.Erased.Count == 0
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
        if (_ctx2 == null || _inkStyle == null || !InkAvailable) return false;
        // 同 DrawPressureInk：压感参与 或 笔锋生效，之一即放行。
        bool taperOn = StrokeTaper.Eligible(s);
        bool pressureOn = PressureWidth.Enabled && s.HasPressure;
        if (!pressureOn && !taperOn) return false;
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
    /// 菜单（**点最右 ☰** 开；2026-11-15 之前是长按 / 点页码格）。PPT 那张四项都是六字，
    /// 文字天然对齐、不用逐项量宽；分隔线是"一项一组"。
    ///
    /// 两个临时样子：
    ///   · 「自动保存墨迹」右边跟一个"开 / 关"（开关项）；
    ///   · 「清除全部墨迹」点过一次之后，整行文字变成「再点确认」并上强调底——
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
                // 自动保存"开"用强调色（白板菜单的 0 是"播放本屏"，没有开关，不上色）
                _scratch.Color = i == 0 && !app.BoardOn && app.PptAutoSaveOn
                    ? theme.ActiveBg : theme.TextMuted;
                _ctx.DrawText(st, fmt,
                              new Rect(item.MaxX - padX - sw, item.MinY, sw, item.MaxY - item.MinY),
                              _scratch);
            }
        }
    }

    /// <summary>
    /// **引导**：进放映的头 1.5 秒，在条的上方浮一行小字（"点页码跳页，点 ☰ 开菜单"）。
    ///
    /// 位置**和菜单同一处**——它淡出之后菜单正好在那儿出现，老师会自然地把两件事连起来。
    /// 生命周期见 <see cref="InkEngine.PptHintVisible"/>：**每次进放映都提示一遍、只停 1.5 秒**
    /// （用户 2026-09-27 定的）——菜单和面板都不是"点一下就猜得到"的入口，
    /// "只提示一次"会让老师第二次上课就想不起来它们了。
    /// </summary>
    private void DrawPptHint(InkEngine app)
    {
        float dpi = app.UiUnit;
        var theme = app.FloatingTheme;
        app.PptMenuRect(out var anchor);            // 借菜单的位置（就是条的上方）
        var hint = new RectF
        {
            MinX = anchor.MinX, MinY = anchor.MinY,
            MaxX = anchor.MinX + 196f * dpi, MaxY = anchor.MinY + 32f * dpi,
        };
        DrawPanelCard(app, hint, 8f * dpi);
        _scratch.Color = theme.Text;
        // 文案（2026-11-15 第五轮）：菜单搬到 ☰、页码格回归跳页之后，这句话要把
        // **两颗键各管什么**一次说清（"☰"就是屏幕上那颗键的样子，比"菜单键"好认）。
        _ctx.DrawText(Loc.Tr("点页码跳页，点 ☰ 开菜单"), ReadoutFormatSmall(dpi),
                      new Rect(hint.MinX, hint.MinY, hint.MaxX - hint.MinX, hint.MaxY - hint.MinY),
                      _scratch);
    }

}

