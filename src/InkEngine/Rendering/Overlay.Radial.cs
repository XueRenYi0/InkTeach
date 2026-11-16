// [职责] 呼出盘绘制。
// [测试] --radialtest

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
    //  呼出盘（Ctrl+Q）：按住 → 划向扇区 → 松手
    // =====================================================================
    //
    // 和落点反馈一样画在**浮动层**、坐标和指针同源（画布坐标 + CanvasToWindow），
    // 所以相机一滚它不会漂。配色**用界面推上来的 FloatingTheme**：浅色/深色主题
    // 各一套，和面板/操作条同一个观感（"界面逻辑跟主程序走"那条要求的落点）。
    //
    // 视觉规格（2026-09-30 v4 定稿：方案 S「全扇面」+ 主条同款图标 + 排序 V-a；
    // 见《调研-呼出盘-扇面与比例.md》与 design/呼出盘-设计稿v4-定稿方向.html）：
    //   · 盘半径 96（直径 192）、环带 内 40 → 外 96、缝 2°、投影照旧层叠胀法；
    //   · 图标 24 居中在 R68（内外各留 16）——输入判据一个没动（死区 24 / 锁定 36）；
    //   · 工具格：常态 regular、选中 filled 白图标（路径 = 主条同款，见 IconPaths）；
    //   · 颜色格：`penFilled` 上色（黑/红/蓝）——"这支颜色的笔"；选中保持原色；
    //   · **没有常显标签**：名字只出现在中央读数；悬停时盘下"浮一行字"。
    internal const float RadialPlateRadiusLogical = 96f;
    // 2026-10-25 定稿（方案 A，后续不再调）：
    // 盘 96 / 扇格 40~96 / 图标环 68 固定；色环 36~40（线宽 4）、中央 72、图标 30。
    internal const float RadialInnerRadiusLogical = 40f;
    internal const float RadialIconRingLogical = 68f;
    internal const float RadialCenterLogical = 36f;
    internal const float RadialSeamDeg = 2f;          // 扇格之间的缝（每边 1°）

    /// <summary>
    /// 第 i 个扇区是不是"彩笔"（画彩色 `penFilled`、选中用淡强调底）。
    /// 2026-10-25 v3：只有北（常用色）和东/西（下一色/上一色）是彩笔。
    /// 2026-11-16 起这三格的颜色读主条活色带（见 `InkEngine.LivePenBand`）。
    /// </summary>
    private static readonly bool[] RadialSectorIsColor =
        { true, false, true, false, false, false, true, false };

    // "0 号扇格"的几何缓存（以盘心为原点、北朝上）；dpi 变了才重建。
    private ID2D1PathGeometry _radialWedgeGeo;
    private float _radialWedgeDpi = -1f;

    private void DrawRadialPalette(InkEngine app)
    {
        if (!app.RadialPaletteActive || !app.RadialPaletteVisible) return;

        float dpi = Dpi / 96f;
        float R = RadialPlateRadiusLogical * dpi;
        float r0 = RadialInnerRadiusLogical * dpi;
        float ring = RadialIconRingLogical * dpi;
        var c = new Vector2(app.RadialCenterX, app.RadialCenterY);
        var theme = app.FloatingTheme;
        int sel = app.RadialPaletteSector;

        // 投影：照面板那套"层叠胀法"（由大到小叠、贴着形状最暗）
        DrawDiscShadow(app, c, R);

        // 指针 → 盘心：点线（让"我在划"这件事有个因果）
        var ptr = new Vector2(app.PointerX, app.PointerY);
        if (Vector2.Distance(c, ptr) > 8f * dpi)
        {
            _scratch.Color = new Color4(theme.TextMuted.R, theme.TextMuted.G,
                                        theme.TextMuted.B, 0.55f);
            _ctx.DrawLine(c, ptr, _scratch, 2.4f * dpi, Gfx.Dotted);
        }

        // 底盘 → 八扇格 → 盘边（盘边最后画，压在扇格外沿上，线才利落）
        _ctx.FillEllipse(new Ellipse(c, R, R), Brush(theme.Panel));
        // 中央实心区直接用盘底 Panel：原图就是“里面一块更深的内盘”。
        // 不再额外填 Hover，否则中央会和扇格糊成一片、丢掉三层层次。
        DrawRadialWedges(c, dpi, r0, R, theme, sel);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, R, R), _scratch, 1f * dpi);

        // 八格图标（主条同款 Fluent 路径）。
        // v3：北 = 常用色；东/西 = “下一支/上一支笔”的实时颜色 + 小箭头；其余 = 工具图标。
        // 2026-11-16：颜色格全部读主条活色带（用户改过色位后盘上跟着变）。
        float sz = 24f * dpi;
        for (int i = 0; i < 8; i++)
        {
            var bp = OnCircle(c, ring, -90f + 45f * i);
            bool on = i == sel;
            if (RadialSectorIsColor[i])
            {
                // **彩笔不转白**：选中也要看得见“这是哪个颜色”（压实心蓝底会发闷）
                _scratch.Color = i switch
                {
                    0 => app.RadialCommonPenColor,           // 北：常用色（活色带第 2 格）
                    2 => app.RadialNextPenColor,             // 东：下一色
                    _ => app.RadialPrevPenColor,             // 西：上一色
                };
                DrawIcon(IconPaths.penFilled, bp.X - sz * 0.5f, bp.Y - sz * 0.5f, sz, _scratch);
                if (i == 2 || i == 6) DrawCycleArrow(bp, i == 2 ? +1 : -1, dpi, theme);
            }
            else
            {
                _scratch.Color = on ? theme.ActiveText : theme.Text;
                DrawIcon(RadialIconFor(i, on), bp.X - sz * 0.5f, bp.Y - sz * 0.5f, sz, _scratch);
            }
        }

        // 清屏格单击即清（可撤销），不再有 0.8s 进度弧。

        // 中央：空心色环 + 当前功能图标（2026-10-25 用户定，替代 v3 的实心色圆）。
        // 色环外径 23.5、内沿 19：整个环都在 24 的中央死区里，
        // “看着还在圈里”就等于“还没选中扇区”，视觉和判位一致。
        // 图标 20px：没划到扇区时 = 当前实际工具；划到扇区时 = 该扇区（实时预览）。
        string centerIcon = sel >= 0 ? RadialSectorIcon(sel) : RadialToolIcon(app.Tool);

        Color4 ringCol;
        if (sel == 2) ringCol = app.RadialNextPenColor;              // 东：预览下一色
        else if (sel == 6) ringCol = app.RadialPrevPenColor;         // 西：预览上一色
        else if (sel == 0) ringCol = app.RadialCommonPenColor;      // 北：常用色（活色带）
        else if (sel == 1)                                          // 荧光笔：荧光色
            ringCol = new Color4(app.HighlighterCurrent.R, app.HighlighterCurrent.G,
                                 app.HighlighterCurrent.B, 1f);
        else
        {
            ringCol = app.Tool == Tool.Highlighter
                ? new Color4(app.HighlighterCurrent.R, app.HighlighterCurrent.G,
                             app.HighlighterCurrent.B, 1f)
                : app.CurrentColor;
            // 橡皮/框选：显示“上次笔色”，压暗，提示“回去还是这个色”。
            if (app.Tool == Tool.Eraser || app.Tool == Tool.PixelEraser || app.Tool == Tool.Marquee)
                ringCol = new Color4(ringCol.R, ringCol.G, ringCol.B, 0.45f);
        }

        // 方案 A：色环 36~40、线宽 4；扇格内沿固定 40，色环不再挤扇格。
        float ringMid = 38f * dpi;         // 彩环中心线：内沿 36、外沿 40
        float ringW = 4f * dpi;

        // 底环比彩环宽 2px：白笔/浅色在浅色盘面上也有轮廓（外沿 41）。
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, ringMid, ringMid), _scratch, ringW + 2f * dpi);
        _scratch.Color = ringCol;
        _ctx.DrawEllipse(new Ellipse(c, ringMid, ringMid), _scratch, ringW);

        // 中心功能图标：30px 外接圆 21.2，离色环内沿 36 还有 14.8——不碰环。
        float iconSz = 30f * dpi;
        _scratch.Color = theme.Text;
        DrawIcon(centerIcon, c.X - iconSz * 0.5f, c.Y - iconSz * 0.5f, iconSz, _scratch);

        // 悬停时"浮一行字"：盘外下方的小白条，给一句人话
        if (sel >= 0)
        {
            string hint = RadialHintFor(app, sel);
            var fmt = RadialHintFormat(dpi);
            float tw = MeasureTextWidth(hint, fmt) + 30f * dpi;
            float hh = 26f * dpi;
            float top = c.Y + R + 12f * dpi;
            DrawPanelCard(app, new RectF
            {
                MinX = c.X - tw * 0.5f, MinY = top,
                MaxX = c.X + tw * 0.5f, MaxY = top + hh,
            }, hh * 0.5f);
            _scratch.Color = theme.Text;
            _ctx.DrawText(hint, fmt, new Rect(c.X - tw * 0.5f, top, tw, hh), _scratch);
        }
    }

    /// <summary>圆盘投影：照面板的"层叠胀法"（主题推上来几层就画几层，由大到小）。</summary>
    private void DrawDiscShadow(InkEngine app, Vector2 c, float R)
    {
        var th = app.FloatingTheme;
        float dpi = Dpi / 96f;
        for (int i = th.Shadow.Length - 1; i >= 0; i--)
        {
            var layer = th.Shadow[i];
            _scratch.Color = layer.Color;
            float r2 = R + layer.Inflate * dpi;
            _ctx.FillEllipse(new Ellipse(new Vector2(c.X, c.Y + layer.Dy * dpi), r2, r2), _scratch);
        }
    }

    /// <summary>悬停那一行字：一句人话（"已经是笔：换下一色"这种）。</summary>
    private static string RadialHintFor(InkEngine app, int i)
    {
        bool already = i switch
        {
            0 => app.Tool == Tool.Pen,
            1 => app.Tool == Tool.Highlighter,
            3 => app.Tool == Tool.Marquee,
            4 => app.Tool == Tool.Eraser || app.Tool == Tool.PixelEraser,
            _ => false,
        };
        return i switch
        {
            0 => already ? "已经是常用色" : $"常用色：{app.RadialCommonPenName}",
            1 => already ? "已经是荧光笔：换下一个色" : "切到荧光笔",
            2 => $"下一色：{app.RadialNextPenName}",
            3 => already ? "矩形 ⇄ 套索" : "切到框选",
            4 => already ? "整笔擦 ⇄ 面积擦" : "切到橡皮",
            5 => "清屏：单击清空本页（可撤销）",
            6 => $"上一色：{app.RadialPrevPenName}",
            _ => "撤销一步",
        };
    }

    /// <summary>
    /// 八个扇格：0 号在北（-90°），一格 45°，格与格之间留 <see cref="RadialSeamDeg"/> 的缝。
    /// 先铺未选中的格、再描格缝、最后把选中格盖上去。
    /// </summary>
    private void DrawRadialWedges(Vector2 c, float dpi, float r0, float r1, UiTheme theme, int sel)
    {
        EnsureRadialWedgeGeo(dpi, r0, r1);

        // 未选中：浅底（深色主题里它就是"比盘底亮一档"的格）
        for (int i = 0; i < 8; i++)
        {
            if (i == sel) continue;
            FillRotatedWedge(c, i, Brush(theme.Hover));
        }

        // 格缝：8 条边界线（细、淡——把"一格一格"说清楚，别画成切蛋糕）
        _scratch.Color = new Color4(theme.PanelBorder.R, theme.PanelBorder.G,
                                    theme.PanelBorder.B, 0.7f);
        for (int i = 0; i < 8; i++)
        {
            float a = -90f + 45f * i - 22.5f;
            _ctx.DrawLine(OnCircle(c, r0, a), OnCircle(c, r1, a), _scratch, 1f * dpi);
        }

        if (sel < 0) return;

        // 选中格：工具 = 实心强调色；颜色 = 淡强调色（彩笔保持原色，红蓝不压蓝底）
        var fill = RadialSectorIsColor[sel]
            ? new Color4(theme.ActiveBg.R, theme.ActiveBg.G, theme.ActiveBg.B, 0.14f)
            : theme.ActiveBg;
        FillRotatedWedge(c, sel, Brush(fill));
        if (RadialSectorIsColor[sel]) StrokeSelectedWedge(c, sel, dpi, r0, r1, theme.ActiveBg);
    }

    /// <summary>
    /// 缓存"0 号扇格"的几何（以盘心为原点、北朝上）：弧用折线逼近
    /// （每 45° 分 12 段，R96 下最大矢高 ≈0.1px，肉眼看不出是折线）。
    /// 绘制时按格号旋转 / 平移（见 <see cref="FillRotatedWedge"/>），不必每帧重建。
    /// </summary>
    private void EnsureRadialWedgeGeo(float dpi, float r0, float r1)
    {
        if (_radialWedgeGeo != null && Math.Abs(_radialWedgeDpi - dpi) < 0.001f) return;
        _radialWedgeGeo?.Dispose();
        _radialWedgeDpi = dpi;

        float half = 22.5f - RadialSeamDeg * 0.5f;
        const int seg = 12;
        var pts = new List<Vector2>(seg * 2 + 2);
        for (int t = 0; t <= seg; t++) pts.Add(WedgePt(r0, -half + 2f * half * t / seg));
        for (int t = seg; t >= 0; t--) pts.Add(WedgePt(r1, -half + 2f * half * t / seg));

        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int k = 1; k < pts.Count; k++) sink.AddLine(pts[k]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        _radialWedgeGeo = geo;
    }

    /// <summary>扇格坐标：角度从北起顺时针（和引擎的扇区定义同一套）。</summary>
    private static Vector2 WedgePt(float r, float deg)
    {
        float a = (deg - 90f) * MathF.PI / 180f;
        return new Vector2(r * MathF.Cos(a), r * MathF.Sin(a));
    }

    /// <summary>把缓存的那一格转到第 i 个位置再填（正数角度 = 屏幕上顺时针）。</summary>
    private void FillRotatedWedge(Vector2 c, int i, ID2D1SolidColorBrush brush)
    {
        var saved = _ctx.Transform;
        _ctx.Transform = Matrix3x2.CreateRotation(i * MathF.PI / 4f)
                       * Matrix3x2.CreateTranslation(c) * saved;
        _ctx.FillGeometry(_radialWedgeGeo, brush);
        _ctx.Transform = saved;
    }

    /// <summary>颜色格选中的强调描边：外弧 + 两条半径（内弧不描，中央读数那边干净）。</summary>
    private void StrokeSelectedWedge(Vector2 c, int i, float dpi, float r0, float r1, Color4 color)
    {
        float half = 22.5f - RadialSeamDeg * 0.5f;
        float mid = -90f + 45f * i;
        _scratch.Color = color;
        float w = 1.5f * dpi;
        for (int t = 0; t < 12; t++)
        {
            float a0 = mid - half + 2f * half * t / 12f;
            float a1 = mid - half + 2f * half * (t + 1) / 12f;
            _ctx.DrawLine(OnCircle(c, r1 - 1f * dpi, a0), OnCircle(c, r1 - 1f * dpi, a1), _scratch, w);
        }
        _ctx.DrawLine(OnCircle(c, r0, mid - half), OnCircle(c, r1, mid - half), _scratch, w);
        _ctx.DrawLine(OnCircle(c, r0, mid + half), OnCircle(c, r1, mid + half), _scratch, w);
    }

    /// <summary>
    /// 呼出盘第 i 格的图标（主条同款 Fluent 路径）：常态 regular、选中 filled。
    /// 彩笔格（0 北 / 2 东 / 6 西）不走这里——它们是 `penFilled` 上色，见 <see cref="DrawRadialPalette"/>。
    /// 清屏（垃圾桶）和撤销（undo）没有 filled 版本，选中靠底下的淡强调扇格表达。
    /// </summary>
    private static string RadialIconFor(int i, bool selected) => i switch
    {
        1 => selected ? IconPaths.highlighterFilled : IconPaths.highlighter,
        3 => selected ? IconPaths.selectFilled : IconPaths.select,
        4 => selected ? IconPaths.eraserFilled : IconPaths.eraser,
        5 => IconPaths.delete,
        7 => IconPaths.undo,
        _ => selected ? IconPaths.penFilled : IconPaths.pen,
    };

    /// <summary>
    /// 中央预览用的扇区图标（regular 20px）。翻色格（2/6）显示笔。
    /// 清屏格 2026-11-16 起用垃圾桶（和主条"清空本页"同一个图标，取代扫帚）。
    /// </summary>
    private static string RadialSectorIcon(int i) => i switch
    {
        1 => IconPaths.highlighter,
        3 => IconPaths.select,
        4 => IconPaths.eraser,
        5 => IconPaths.delete,
        7 => IconPaths.undo,
        _ => IconPaths.pen,
    };

    /// <summary>
    /// 自检/摆样：第 i 格的（扇格图标, 中央预览图标）路径。
    /// 单独开一个口子是给 `--radialtest` 钉"清屏格 = 垃圾桶"用的（绘制本体不对外）。
    /// </summary>
    internal static (string Wedge, string Center) RadialIconPathsForTest(int i)
        => (RadialIconFor(i, false), RadialSectorIcon(i));

    /// <summary>没划到扇区时，中央显示“当前实际工具”。</summary>
    private static string RadialToolIcon(Tool t) => t switch
    {
        Tool.Highlighter => IconPaths.highlighter,
        Tool.Eraser or Tool.PixelEraser => IconPaths.eraser,
        Tool.Marquee => IconPaths.select,
        Tool.Laser => IconPaths.laser,
        _ => IconPaths.pen,
    };

    /// <summary>
    /// 东/西格的小箭头：告诉用户“往这个方向是下一色 / 上一色”。
    /// 画在彩笔图标下方 16 逻辑像素处，只占 8×8，不抢图标、也不加文字。
    /// </summary>
    private void DrawCycleArrow(Vector2 iconCenter, int dir, float dpi, UiTheme theme)
    {
        // 放到彩笔的“外侧”（东格在右、西格在左），不压笔杆；箭头本身朝外，
        // “› = 下一色 / ‹ = 上一色”比夹在图标下面更好读。
        float s = 5f * dpi;
        float x = iconCenter.X + dir * 14f * dpi;
        float y = iconCenter.Y;
        float w = 2.2f * dpi;
        _scratch.Color = new Color4(theme.Text.R, theme.Text.G, theme.Text.B, 0.85f);
        if (dir > 0)
        {
            _ctx.DrawLine(new Vector2(x - s * 0.55f, y - s), new Vector2(x + s * 0.55f, y), _scratch, w);
            _ctx.DrawLine(new Vector2(x + s * 0.55f, y), new Vector2(x - s * 0.55f, y + s), _scratch, w);
        }
        else
        {
            _ctx.DrawLine(new Vector2(x + s * 0.55f, y - s), new Vector2(x - s * 0.55f, y), _scratch, w);
            _ctx.DrawLine(new Vector2(x - s * 0.55f, y), new Vector2(x + s * 0.55f, y + s), _scratch, w);
        }
    }

    private static Vector2 OnCircle(Vector2 c, float r, float deg)
    {
        float a = deg * MathF.PI / 180f;
        return new Vector2(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a));
    }

    private IDWriteTextFormat _radialHintFmt;
    private float _radialHintPx;

    /// <summary>悬停那一行字的格式（12px，塞在盘下的小白条里）。</summary>
    private IDWriteTextFormat RadialHintFormat(float dpi)
    {
        float px = MathF.Max(10f, MathF.Round(12f * dpi));
        if (_radialHintFmt == null || _radialHintPx != px)
        {
            _radialHintFmt?.Dispose();
            _radialHintFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _radialHintFmt.TextAlignment = TextAlignment.Center;
            _radialHintFmt.ParagraphAlignment = ParagraphAlignment.Center;
            _radialHintPx = px;
        }
        return _radialHintFmt;
    }

    /// <summary>
    /// 落点反馈：自己画在浮动层上的"指针"。
    ///
    /// 为什么不直接用系统光标：圆环/圆盘要跟着**笔宽**变，还要在深色 PPT 和
    /// 白色白板上都看得见——系统光标做不到这两点。代价是真光标必须同时藏起来，
    /// 那份逻辑在引擎里（InkEngine.DrawnCursor / CursorKind.Hidden）。
    ///
    /// 画在浮动层还有个好处：它的坐标和笔迹同源，不存在"光标热点偏了一两个
    /// 像素"的问题——写字时那一两个像素是能感觉出来的。
    /// </summary>
    private void DrawToolCursor(InkEngine app)
    {
        var shape = app.DrawnCursor;
        if (shape == InkEngine.ToolCursorShape.None) return;

        var c = new Vector2(app.PointerX, app.PointerY);
        if (shape == InkEngine.ToolCursorShape.Disc)
        {
            DrawHighlighterDisc(app, c);
            return;
        }
        if (shape == InkEngine.ToolCursorShape.Dot)
        {
            DrawLaserDot(app, c);
            return;
        }
        if (shape == InkEngine.ToolCursorShape.Rect)
        {
            DrawEraserRectCursor(app, c);
            return;
        }

        // 问 EffectiveTool（不是 Tool）：**笔倒过来拿（笔尾橡皮）时，这一圈就是橡皮的圈**
        // ——半径按橡皮、颜色按橡皮。引擎那边（DrawnCursor / CursorOuterRadius）也是问它，
        // 两处同一个判据，不然会出现"半径是橡皮的、颜色是笔的"这种半吊子。
        bool erasing = app.EffectiveTool == Tool.Eraser;

        float outer = app.CursorOuterRadius;
        float truth = erasing
            ? app.EraserRadius
            : app.CursorRingTrueRadius;
        var fill = erasing
            ? new Color4(0.35f, 0.55f, 0.95f, 0.10f)      // 橡皮：淡蓝，"要擦掉这一块"
            : new Color4(0.35f, 0.55f, 0.95f, 0.06f);     // 笔尖：更淡，不挡视线
        DrawRingCursor(c, truth, outer, fill);
    }

    /// <summary>
    /// 圆环：橡皮的"要擦掉多大一块"、笔的"这一笔多粗"。
    ///
    /// 双色描边和四向刻度都是被场景逼出来的：单色圆在白板（浅）和深色 PPT（深）
    /// 上总有一种看不清；刻度标出圆心，投影上写字手是抖的，得知道落点在哪。
    ///
    /// **两个圈**：内圈是**真实笔宽**（"我写出来就这么粗"），外圈是固定尺寸的
    /// 最小可见范围。只有一个圈时无解：按真实宽度画，细笔看不见；按下限画，
    /// 细笔的圈比笔迹粗一倍，反而误判。Photoshop 的笔刷光标就是这个做法。
    /// </summary>
    /// <summary>
    /// 像素橡皮的落点：**竖着的黄金比例矩形**（高 : 宽 = 1.618）。
    ///
    /// 为什么是矩形而不是圆环：它就是"一块橡皮"。从上往下抹一列板书时，用户要知道
    /// 这一抹有多宽、多高；圆环表达不了这个。
    ///
    /// 三层（和圆环同一套色彩逻辑）：
    ///   · **半透明填充**——看得见"这一块会被擦掉"，同时底下的字还看得见（用户要的
    ///     "有点透明度"）。太实会挡住要擦的东西，太淡又看不出边界；
    ///   · 白色外圈 + 深色内圈的双色描边：深色 PPT 和白色白板上都得看得见；
    ///   · 中心一个小十字：投影上写字手会抖，得知道精确落点在哪。
    /// </summary>
    private void DrawEraserRectCursor(InkEngine app, Vector2 c)
    {
        // 框 = **真正会被擦掉的那一块**：和 EraseRectAlongPath 读同一份尺寸。
        // 拖动中跟着移动速度变大（8.3.4 动态橡皮），悬停时是基准（= 按下去第一下的大小）。
        DrawEraserBoxAt(c, MathF.Max(1f, app.PixelEraserCursorHalfWidthPx),
                           MathF.Max(1f, app.PixelEraserCursorHalfHeightPx));
    }

    /// <summary>画一块"面积橡皮"的落点框（鼠标/笔的光标与触摸反馈共用同一份画法）。</summary>
    private void DrawEraserBoxAt(Vector2 c, float hw, float hh)
    {
        _ctx.FillRectangle(
            new Vortice.RawRectF(c.X - hw, c.Y - hh, c.X + hw, c.Y + hh),
            // 填充色：**0.12 → 0.22**（用户 2026-09-17："面积橡皮擦太透明了"）。
            // 参考点：笔尖那个圆环填充是 0.06，整笔橡皮的圆是 0.10——面积橡皮是
            // 唯一"要看清边界的一块面"，它最实才对；0.22 之后底下的字仍然透得出来。
            Brush(new Color4(0.35f, 0.55f, 0.95f, 0.22f)));

        // 两层描边都比原来略实一档，且线宽 1.5 → 1.8：面积橡皮的边界是"会不会擦掉"
        // 的判据，投影上要一眼看清。
        _scratch.Color = new Color4(1f, 1f, 1f, 0.85f);
        _ctx.DrawRectangle(new Vortice.RawRectF(
            c.X - hw - 0.9f, c.Y - hh - 0.9f, c.X + hw + 0.9f, c.Y + hh + 0.9f), _scratch, 1.8f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.9f);
        _ctx.DrawRectangle(new Vortice.RawRectF(
            c.X - hw + 0.9f, c.Y - hh + 0.9f, c.X + hw - 0.9f, c.Y + hh - 0.9f), _scratch, 1.8f);

        float tick = MathF.Max(5f, MathF.Min(hw, hh) * 0.25f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.85f);
        _ctx.DrawLine(new Vector2(c.X - tick, c.Y), new Vector2(c.X + tick, c.Y), _scratch, 1.8f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - tick), new Vector2(c.X, c.Y + tick), _scratch, 1.8f);
    }

    /// <summary>
    /// **多指擦会话的落点反馈**（三指 / 手掌；2026-10-05 实测："能擦，但看不见橡皮"）：
    /// 落点 = **所有触点的中心**（一只手 = 一个橡皮，和鼠标同源；不再每个触点画一个，
    /// 真机反馈"三个指头出来三个橡皮擦"）；形状、尺寸、颜色全走鼠标那份
    /// （整笔擦 = 圆环、面积擦 = 矩形），真正擦的尺寸和它同一份，所以"看见的 = 擦掉的"。
    /// 单指按着橡皮工具走 `DrawToolCursor`（引擎把 DrawnCursor 打开了），不在这一条里。
    /// 数据现取现画、模式一落就不画——没有缓存，不会"出现不消失"。
    /// </summary>
    private void DrawTouchEraseFeedback(InkEngine app)
    {
        if (!app.TouchEraseSessionActive || !app.TouchEraseCenter(out var c)) return;
        if (app.TouchEraseSessionPixel)
            DrawEraserBoxAt(c, MathF.Max(1f, app.PixelEraserCursorHalfWidthPx),
                               MathF.Max(1f, app.PixelEraserCursorHalfHeightPx));
        else
        {
            float r = MathF.Max(1f, app.EraserRadius);
            DrawRingCursor(c, r, r, new Color4(0.35f, 0.55f, 0.95f, 0.10f));
        }
    }

    private void DrawRingCursor(Vector2 c, float truthR, float outerR, Color4 fill)
    {
        float r = MathF.Max(outerR, 1f);
        bool doubleRing = r - truthR > 1.5f;         // 两个圈差得太近就只画外圈

        // 中间一层很淡的填充，让"范围"一眼可见。
        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(fill));

        // 内圈：真实笔宽。它才是"这一笔有多粗"的答案。
        if (doubleRing)
        {
            _ctx.FillEllipse(new Ellipse(c, truthR, truthR),
                             Brush(new Color4(0.35f, 0.55f, 0.95f, 0.16f)));
            _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.55f);
            _ctx.DrawEllipse(new Ellipse(c, truthR, truthR), _scratch, 1f);
        }

        // 双色描边：外圈浅、内圈深。单色圆在深色桌面和白色白板上总有一种看不清，
        // 双层描边两种背景上都成立。
        _ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f),
            Brush(new Color4(1f, 1f, 1f, 0.75f)), 1.5f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.85f);
        _ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f), _scratch, 1.5f);

        // 四个方向的小刻度标出圆心，落点更准。
        float tick = MathF.Max(4f, r * 0.22f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.7f);
        _ctx.DrawLine(new Vector2(c.X - r - tick, c.Y), new Vector2(c.X - r + tick * 0.2f, c.Y), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X + r - tick * 0.2f, c.Y), new Vector2(c.X + r + tick, c.Y), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - r - tick), new Vector2(c.X, c.Y - r + tick * 0.2f), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X, c.Y + r - tick * 0.2f), new Vector2(c.X, c.Y + r + tick), _scratch, 1.5f);
    }

    /// <summary>
    /// 激光笔的落点：一个实心小圆点 + 白色外圈。
    ///
    /// 为什么不是圆环：激光是"我说的是这里"，不需要表达范围（它连墨都不留）。
    /// 白圈是为了在深色背景上也能看见，并且和激光轨迹的红是同一个色系。
    /// </summary>
    private void DrawLaserDot(InkEngine app, Vector2 c)
    {
        float r = MathF.Max(app.CursorDotRadius, 2f);
        _ctx.FillEllipse(new Ellipse(c, r + 1f, r + 1f), Brush(new Color4(1f, 1f, 1f, 0.85f)));
        // 和激光轨迹同色（DrawLaser 里用的就是这组红）
        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(new Color4(1f, 0.16f, 0.16f, 0.95f)));
    }

    /// <summary>
    /// 荧光笔的落点：**一个直径 = 笔宽的实心圆盘**。
    ///
    /// 为什么是圆，而不是旧版那根横条（胶囊）：荧光笔画出来的墨迹就是"中心线
    /// 两侧各半个笔宽 + 两端圆帽"，落笔处本身是一个直径 = 笔宽的圆；单击一下
    /// 留下的也正是这个圆。横条只在水平方向变宽，竖着画的那一刻和实际墨迹
    /// 对不上，拖动时看上去像拖着一根尺子。
    ///
    /// 填充用荧光笔当前的颜色，落笔前就能看到会画出什么色；描边用和笔/橡皮
    /// 圆环同一套的双色（浅底、深底上都看得见）。
    /// </summary>
    private void DrawHighlighterDisc(InkEngine app, Vector2 c)
    {
        // 半径就是半个笔宽——和真正落到屏幕上的墨迹同一个尺寸，不放大也不缩小。
        float r = MathF.Max(app.HighlighterWidthLogical * app.DpiScale * 0.5f, 2f);
        var hl = app.HighlighterCurrent;

        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(new Color4(hl.R, hl.G, hl.B, 0.22f)));

        _scratch.Color = new Color4(1f, 1f, 1f, 0.75f);
        _ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f), _scratch, 1.5f);

        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.55f);
        _ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f), _scratch, 1.5f);
    }

    private void DrawMarquee(InkEngine app)
    {
        if (!app.MarqueeActive) return;

        // 套索：画**自由曲线**，不是矩形。手势是什么样，屏幕上就得是什么样——
        // 用户要能看见自己"圈"到哪儿了，否则 80% 这条判据完全不可预期。
        // 收口那条边画淡一点：它提示"松手会自动闭合"，但还不是现在的边界。
        //
        // 线头必须画到**指针此刻的位置**（LassoLive），不能只画抽稀过的路径点：
        // 路径点之间隔着 3 逻辑像素，只画路径的话线头会慢半拍（200% 缩放 = 6 个物理像素，
        // 看得出来）。抽稀是为了判据便宜，不是为了少画这一截。
        if (app.SelMode == SelectMode.Lasso && app.LassoPath.Count >= 1
            && (app.LassoPath.Count >= 2
                || Vector2.Distance(app.LassoPath[0], app.LassoLive) > 0.5f))
        {
            _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.95f);
            using (var geo = BuildFreePolyline(app.LassoPath, app.LassoLive))
                _ctx.DrawGeometry(geo, _scratch, 1.6f, Gfx.Round);

            _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.30f);
            var p0 = app.LassoPath[0];
            _ctx.DrawLine(p0, app.LassoLive, _scratch, 1.2f);
            return;
        }

        var r = new Vortice.RawRectF(app.MqMinX, app.MqMinY, app.MqMaxX, app.MqMaxY);
        _ctx.FillRectangle(r, Brush(new Color4(0.25f, 0.6f, 1f, 0.12f)));
        _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.95f);
        _ctx.DrawRectangle(r, _scratch, 1.2f);
    }

    /// <summary>
    /// 把一串画布坐标的点连成一条**开放折线**（套索预览用）。
    ///
    /// 为什么不复用手写笔迹那套：那是"中心线 + 原生描边"的墨，要按笔宽、颜色、
    /// 分块缓存走；套索只是一根临时的引导线，每帧现建现扔就够（几十个顶点）。
    /// </summary>
    private static ID2D1PathGeometry BuildFreePolyline(IReadOnlyList<Vector2> pts, Vector2 tail)
    {
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(pts[0], FigureBegin.Hollow);
        for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
        if (Vector2.Distance(tail, pts[pts.Count - 1]) > 0.5f) sink.AddLine(tail);
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

}
