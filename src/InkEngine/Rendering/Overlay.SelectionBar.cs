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


}
