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
