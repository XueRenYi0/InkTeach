using System.Numerics;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 文本框的 8 手柄改宽高。
///
/// **不是图片那套缩放**：手柄改的是 <see cref="TextBoxData.Width"/> /
/// <see cref="TextBoxData.Height"/>，字号和内容不缩放；文字按新宽度重排，
/// 公式以后也按新宽度/字号重新渲染。
///
/// AI 题卡默认自动高度：只拖左右边时保持自动高度；一旦拖了上/下边或角，
/// 就切成固定高度（内容超出后续做内部滚动/裁切，第一版先裁切）。
/// </summary>
public partial class InkEngine
{
    private bool _textBoxResizing;
    private bool _textBoxResizePlain;
    private Stroke _textBoxResizeStroke;
    private SelHandle _textBoxResizeHandle;
    private RectF _textBoxResizeStartRect;
    private bool _textBoxResizeWasAuto;
    private Vector2 _textBoxResizeStartOrigin;
    private float _textBoxResizeStartWidth;
    private float _textBoxResizeStartHeight;
    private float _textBoxResizeStartFont;
    private RectF _textBoxResizeBeforeBounds;
    private float _textBoxResizeMinWidth;

    internal void BeginTextBoxResize(Stroke s, SelHandle handle, float screenX, float screenY)
    {
        var box = s?.TextBox;
        if (box == null) return;
        // 移动过的框先把平移烘焙进 Points，后面按画布坐标夹屏幕才准。
        BakeTextBoxTranslation(s);

        // 起点用对象局部坐标（文本框只做平移，不旋转；逆变换仍然写全，防以后加旋转）。
        // ⚠ 传进来的 screenX/screenY 已经是**画布坐标**（OnPointerDown 先过了 ScreenToCanvas）；
        // 这里再减一次 ViewOffsetY 会让纵向拖手柄在滚动画布后整体偏移，横向却正常
        // ——用户 2026-10-31 报的“点下边突然长高、点上面又缩下去”就是它。
        Vector2 p = new(screenX, screenY);
        if (!s.Transform.IsIdentity && Matrix3x2.Invert(s.Transform, out var inv))
            p = Vector2.Transform(p, inv);

        _textBoxResizeStroke = s;
        _textBoxResizePlain = box.Role == TextBoxRole.Normal;
        _textBoxResizeHandle = handle;
        _textBoxResizeStartRect = new RectF
        {
            MinX = s.Points.Count > 0 ? s.Points[0].X : 0f,
            MinY = s.Points.Count > 0 ? s.Points[0].Y : 0f,
            MaxX = (s.Points.Count > 0 ? s.Points[0].X : 0f) + box.Width,
            MaxY = (s.Points.Count > 0 ? s.Points[0].Y : 0f) + box.LayoutHeight,
        };
        _textBoxResizeWasAuto = box.AutoHeight;
        _textBoxResizeStartOrigin = new Vector2(_textBoxResizeStartRect.MinX, _textBoxResizeStartRect.MinY);
        _textBoxResizeStartWidth = box.Width;
        _textBoxResizeStartHeight = box.Height;
        _textBoxResizeStartFont = box.FontSize;
        _textBoxResizeBeforeBounds = s.PaddedBounds;
        _textBoxResizeMinWidth = box.MinWidth;
        _textBoxResizing = true;
        SelDragging = true;
        _dirty = true;
    }

    private void UpdateTextBoxResize(float screenX, float screenY)
    {
        if (!_textBoxResizing || _textBoxResizeStroke == null) return;
        var s = _textBoxResizeStroke;
        var box = s.TextBox;
        if (box == null) return;

        Vector2 p = new(screenX, screenY);
        if (!s.Transform.IsIdentity && Matrix3x2.Invert(s.Transform, out var inv))
            p = Vector2.Transform(p, inv);

        var r = _textBoxResizeStartRect;
        bool left = _textBoxResizeHandle is SelHandle.Left or SelHandle.TopLeft or SelHandle.BottomLeft;
        bool right = _textBoxResizeHandle is SelHandle.Right or SelHandle.TopRight or SelHandle.BottomRight;
        bool top = _textBoxResizeHandle is SelHandle.Top or SelHandle.TopLeft or SelHandle.TopRight;
        bool bottom = _textBoxResizeHandle is SelHandle.Bottom or SelHandle.BottomLeft or SelHandle.BottomRight;

        var screen = ScreenRectPhysical();
        float unit = MathF.Max(0.5f, box.Unit);
        float margin = 16f * unit;
        float screenLeft = screen.MinX + margin;
        float screenRight = screen.MaxX - margin;
        // 宽度上限 = 屏幕可用宽；只按"当前起点的右沿"算会让框一拖就顶出屏幕。
        float maxW = MathF.Max(24f * unit, screenRight - screenLeft);
        float minW = MathF.Min(MathF.Max(24f * unit, _textBoxResizeMinWidth), maxW);
        var old = s.PaddedBounds;

        // ---- 普通文本框：左右竖线 = 改宽；四个角 = 等比缩放字号 ----
        if (_textBoxResizePlain)
        {
            float startW = MathF.Max(1f, _textBoxResizeStartWidth);
            float startH = MathF.Max(1f, _textBoxResizeStartHeight);

            if (_textBoxResizeHandle is SelHandle.Left or SelHandle.Right)
            {
                if (left) r.MinX = MathF.Max(p.X, screenLeft);
                if (right) r.MaxX = MathF.Min(p.X, screenRight);
                float w = r.MaxX - r.MinX;
                if (w < minW)
                {
                    if (left) r.MinX = r.MaxX - minW;
                    else r.MaxX = r.MinX + minW;
                }
                if (r.MaxX - r.MinX > maxW)
                {
                    if (left) r.MinX = r.MaxX - maxW;
                    else r.MaxX = r.MinX + maxW;
                }
                // 再夹一次左右沿（左边拖到屏幕外时只收左沿，不动右边）。
                if (r.MinX < screenLeft) r.MinX = screenLeft;
                if (r.MaxX > screenRight) r.MaxX = screenRight;
                w = MathF.Max(1f, r.MaxX - r.MinX);

                box.AutoWidth = false;
                box.AutoHeight = true;
                box.InvalidateLayout();
                s.SetTextBoxRect(new Vector2(r.MinX, _textBoxResizeStartOrigin.Y), w, box.Height);
                _ = box.EnsureLayout();
                s.SetTextBoxRect(new Vector2(r.MinX, _textBoxResizeStartOrigin.Y), w, box.Height);
            }
            else
            {
                // 角拖：按“长出去更多的那一轴”等比放大字号；宽跟着字号走，高再按行数算。
                float anchorX = left ? r.MaxX : r.MinX;
                float anchorY = top ? r.MaxY : r.MinY;
                float dragW = left ? anchorX - p.X : p.X - anchorX;
                float dragH = top ? anchorY - p.Y : p.Y - anchorY;
                float scale = MathF.Max(dragW / startW, dragH / startH);
                float minFont = 8f;
                float maxFont = 72f;
                float newFont = Math.Clamp(
                    _textBoxResizeStartFont * MathF.Max(scale, minFont / _textBoxResizeStartFont),
                    minFont, maxFont);
                float k = newFont / MathF.Max(1f, _textBoxResizeStartFont);
                box.FontSize = newFont;
                box.AutoWidth = false;
                box.AutoHeight = true;
                box.InvalidateLayout();
                float newW = MathF.Max(MathF.Max(24f * unit, box.MinWidth), startW * k);
                newW = MathF.Min(newW, maxW);
                box.Width = newW;
                s.SetTextBoxRect(new Vector2(anchorX, anchorY), newW, box.Height);
                _ = box.EnsureLayout();
                float newH = MathF.Max(1f, box.Height);
                float newX = left ? anchorX - newW : anchorX;
                // 角拖也不能把框拉出屏幕：右边顶住就往左收，左边顶住就往右收。
                if (newX + newW > screenRight) newX = screenRight - newW;
                if (newX < screenLeft) newX = screenLeft;
                s.SetTextBoxRect(new Vector2(newX, top ? anchorY - newH : anchorY), newW, newH);
            }

            Doc.RefreshStrokeGrid(s);
            Doc.Dirty.Add(old);
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            _dirty = true;
            return;
        }

        // ---- AI 题卡：保留原来的宽/高拖动语义 ----
        if (left) r.MinX = p.X;
        if (right) r.MaxX = p.X;
        if (top) r.MinY = p.Y;
        if (bottom) r.MaxY = p.Y;

        // 用户一旦亲手拖过手柄，就不再让输入时的“自动长宽”覆盖他的尺寸。
        box.AutoWidth = false;
        float minH = 60f * unit;
        float maxH = MathF.Max(minH, (screen.MaxY - screen.MinY) * 0.9f);
        if (r.MaxX - r.MinX < minW)
        {
            if (left) r.MinX = r.MaxX - minW;
            else r.MaxX = r.MinX + minW;
        }
        if (r.MaxY - r.MinY < minH)
        {
            if (top) r.MinY = r.MaxY - minH;
            else r.MaxY = r.MinY + minH;
        }
        if (r.MaxX - r.MinX > maxW) r.MaxX = r.MinX + maxW;
        if (r.MaxY - r.MinY > maxH) r.MaxY = r.MinY + maxH;

        float w2 = r.MaxX - r.MinX;
        float h2 = r.MaxY - r.MinY;

        // 左右拖：只改宽，AI 题卡继续自动高度；
        // 上下/角拖：切成固定高度（用户定："拖了高度就以手动为准"）。
        if (_textBoxResizeWasAuto && (top || bottom))
        {
            box.AutoHeight = false;
            box.Height = h2;
        }
        else if (!_textBoxResizeWasAuto)
        {
            box.AutoHeight = false;
            box.Height = h2;
        }

        box.InvalidateLayout();
        s.SetTextBoxRect(new Vector2(r.MinX, r.MinY), w2,
                         box.AutoHeight ? box.Height : h2);
        if (box.AutoHeight)
        {
            _ = box.EnsureLayout();
            s.SetTextBoxRect(new Vector2(r.MinX, r.MinY), w2, box.Height);
        }
        Doc.RefreshStrokeGrid(s);
        Doc.Dirty.Add(old);
        Doc.Dirty.Add(s.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
    }

    private void FinishTextBoxResize()
    {
        if (!_textBoxResizing) return;
        var s = _textBoxResizeStroke;
        _textBoxResizing = false;
        _textBoxResizePlain = false;
        _textBoxResizeStroke = null;
        SelDragging = false;
        if (s?.TextBox != null)
        {
            var after = s.PaddedBounds;
            var beforeOrigin = _textBoxResizeStartOrigin;
            float beforeW = _textBoxResizeStartWidth;
            float beforeH = _textBoxResizeStartHeight;
            float beforeFont = _textBoxResizeStartFont;
            bool beforeAuto = _textBoxResizeWasAuto;
            bool changed = MathF.Abs(after.MinX - _textBoxResizeBeforeBounds.MinX) > 0.5f
                        || MathF.Abs(after.MinY - _textBoxResizeBeforeBounds.MinY) > 0.5f
                        || MathF.Abs(after.MaxX - _textBoxResizeBeforeBounds.MaxX) > 0.5f
                        || MathF.Abs(after.MaxY - _textBoxResizeBeforeBounds.MaxY) > 0.5f
                        || MathF.Abs(s.TextBox.FontSize - beforeFont) > 0.01f
                        || s.TextBox.AutoHeight != beforeAuto;
            if (changed)
            {
                var act = new TextBoxGeometryAction(
                    s, beforeOrigin, beforeW, beforeH, beforeFont, beforeAuto,
                    s.Points.Count > 0 ? new Vector2(s.Points[0].X, s.Points[0].Y) : beforeOrigin,
                    s.TextBox.Width, s.TextBox.Height, s.TextBox.FontSize, s.TextBox.AutoHeight,
                    _textBoxResizeBeforeBounds);
                Doc.CommitInteractive(act);
            }
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>操作条 A- / A+：只改字号，不改框宽高（自动高度会随内容重算）。</summary>
    private void AdjustTextBoxFont(float delta)
    {
        if (Doc.Selected.Count != 1) return;
        var s = Doc.Selected[0];
        var box = s.TextBox;
        if (s.Kind != StrokeKind.TextBox || box == null) return;
        var old = s.PaddedBounds;
        var origin = s.Points.Count > 0 ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero;
        float oldFont = box.FontSize;
        float oldW = box.Width, oldH = box.Height;
        bool oldAuto = box.AutoHeight;
        box.FontSize = Math.Clamp(box.FontSize + delta, 8f, 72f);
        box.InvalidateLayout();
        s.SyncTextBoxBounds();
        ClampTextBoxIntoView(s);
        Doc.RefreshStrokeGrid(s);
        Doc.Dirty.Add(old);
        Doc.Dirty.Add(s.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
        NotifyUiStateChanged();

        if (MathF.Abs(box.FontSize - oldFont) > 0.01f)
        {
            var act = new TextBoxGeometryAction(
                s, origin, oldW, oldH, oldFont, oldAuto,
                origin, box.Width, box.Height, box.FontSize, box.AutoHeight, old);
            Doc.CommitInteractive(act);
        }
    }
}

/// <summary>
/// 文本框改宽高 / 改字号的一步撤销。它只动 <see cref="TextBoxData"/> 的尺寸和字号，
/// **不碰 Transform**（移动仍然是通用的 Transform 动作）。
/// </summary>
internal sealed class TextBoxGeometryAction : EditAction
{
    private readonly Stroke _stroke;
    private readonly Vector2 _beforeOrigin, _afterOrigin;
    private readonly float _beforeW, _beforeH, _beforeFont;
    private readonly bool _beforeAuto;
    private readonly float _afterW, _afterH, _afterFont;
    private readonly bool _afterAuto;
    private readonly RectF _beforeBounds, _afterBounds;

    public TextBoxGeometryAction(
        Stroke s, Vector2 beforeOrigin, float beforeW, float beforeH, float beforeFont, bool beforeAuto,
        Vector2 afterOrigin, float afterW, float afterH, float afterFont, bool afterAuto,
        RectF beforeBounds)
    {
        _stroke = s;
        _beforeOrigin = beforeOrigin; _afterOrigin = afterOrigin;
        _beforeW = beforeW; _beforeH = beforeH; _beforeFont = beforeFont; _beforeAuto = beforeAuto;
        _afterW = afterW; _afterH = afterH; _afterFont = afterFont; _afterAuto = afterAuto;
        _beforeBounds = beforeBounds;
        _afterBounds = s.PaddedBounds;
    }

    public override int HeldStrokes => 1;
    public override RectF AffectedBefore => _beforeBounds;
    public override RectF AffectedAfter => _afterBounds;

    public override void Undo(InkDocument doc)
    {
        Apply(doc, _beforeOrigin, _beforeW, _beforeH, _beforeFont, _beforeAuto);
    }

    public override void Redo(InkDocument doc)
    {
        Apply(doc, _afterOrigin, _afterW, _afterH, _afterFont, _afterAuto);
    }

    private void Apply(InkDocument doc, Vector2 origin, float w, float h, float font, bool auto)
    {
        var box = _stroke.TextBox;
        if (box == null) return;
        box.FontSize = font;
        box.AutoHeight = auto;
        box.Width = w;
        box.Height = h;
        box.InvalidateLayout();
        _stroke.SetTextBoxRect(origin, w, h);
        if (auto) _stroke.SyncTextBoxBounds();
        doc.RefreshStrokeGrid(_stroke);
    }
}

/// <summary>
/// 文本框“字体 / 行距”的一步撤销。字体和行距都会改排版高度，
/// 所以把宽高和原点一起快照；和改字号那条 <see cref="TextBoxGeometryAction"/> 分开，
/// 免得老调用方误把字体重置回雅黑。
/// </summary>
internal sealed class TextBoxStyleAction : EditAction
{
    private readonly Stroke _stroke;
    private readonly Vector2 _beforeOrigin, _afterOrigin;
    private readonly float _beforeW, _beforeH, _beforeFont, _beforeSpacing;
    private readonly float _afterW, _afterH, _afterFont, _afterSpacing;
    private readonly bool _beforeAuto, _afterAuto;
    private readonly TextBoxFace _beforeFace, _afterFace;
    private readonly RectF _beforeBounds, _afterBounds;

    public TextBoxStyleAction(Stroke s, Vector2 beforeOrigin,
                              float beforeW, float beforeH, float beforeFont, bool beforeAuto,
                              TextBoxFace beforeFace, float beforeSpacing, RectF beforeBounds)
    {
        _stroke = s;
        var box = s.TextBox;
        _beforeOrigin = beforeOrigin;
        _beforeW = beforeW; _beforeH = beforeH; _beforeFont = beforeFont;
        _beforeAuto = beforeAuto; _beforeFace = beforeFace; _beforeSpacing = beforeSpacing;
        _beforeBounds = beforeBounds;

        _afterOrigin = s.Points.Count > 0 ? new Vector2(s.Points[0].X, s.Points[0].Y) : beforeOrigin;
        _afterW = box?.Width ?? beforeW;
        _afterH = box?.Height ?? beforeH;
        _afterFont = box?.FontSize ?? beforeFont;
        _afterAuto = box?.AutoHeight ?? beforeAuto;
        _afterFace = box?.Face ?? beforeFace;
        _afterSpacing = box?.LineSpacing ?? beforeSpacing;
        _afterBounds = s.PaddedBounds;
    }

    public override int HeldStrokes => 1;
    public override RectF AffectedBefore => _beforeBounds;
    public override RectF AffectedAfter => _afterBounds;

    public override void Undo(InkDocument doc)
        => Apply(doc, _beforeOrigin, _beforeW, _beforeH, _beforeFont, _beforeAuto,
                 _beforeFace, _beforeSpacing);

    public override void Redo(InkDocument doc)
        => Apply(doc, _afterOrigin, _afterW, _afterH, _afterFont, _afterAuto,
                 _afterFace, _afterSpacing);

    private void Apply(InkDocument doc, Vector2 origin, float w, float h, float font, bool auto,
                       TextBoxFace face, float spacing)
    {
        var box = _stroke.TextBox;
        if (box == null) return;
        box.FontSize = font;
        box.AutoHeight = auto;
        box.Face = face;
        box.LineSpacing = spacing;
        box.Width = w;
        box.Height = h;
        box.InvalidateLayout();
        _stroke.SetTextBoxRect(origin, w, h);
        if (auto) _stroke.SyncTextBoxBounds();
        doc.RefreshStrokeGrid(_stroke);
    }
}

/// <summary>背景样式（Fill / Border / 圆角）+ 自动字色的撤销动作。</summary>
internal sealed class TextBoxBackgroundAction : EditAction
{
    private readonly Stroke _stroke;
    private readonly Color4 _beforeFill, _beforeBorder, _beforeInk, _beforeColor;
    private readonly Color4 _afterFill, _afterBorder, _afterInk, _afterColor;
    private readonly float _beforeRadius, _afterRadius;
    private readonly bool _beforePaper, _afterPaper;
    private readonly RectF _beforeBounds, _afterBounds;

    public TextBoxBackgroundAction(Stroke s, Color4 beforeFill, Color4 beforeBorder,
                                   float beforeRadius, Color4 beforeInk, Color4 beforeColor,
                                   bool beforePaper, RectF beforeBounds)
    {
        _stroke = s;
        _beforeFill = beforeFill; _beforeBorder = beforeBorder;
        _beforeRadius = beforeRadius; _beforeInk = beforeInk; _beforeColor = beforeColor;
        _beforePaper = beforePaper;
        _beforeBounds = beforeBounds;
        var box = s.TextBox;
        _afterFill = box?.Fill ?? beforeFill;
        _afterBorder = box?.Border ?? beforeBorder;
        _afterRadius = box?.Radius ?? beforeRadius;
        _afterInk = box?.Ink ?? beforeInk;
        _afterColor = s.Color;
        _afterPaper = box?.Paper ?? beforePaper;
        _afterBounds = s.PaddedBounds;
    }

    public override int HeldStrokes => 1;
    public override RectF AffectedBefore => _beforeBounds;
    public override RectF AffectedAfter => _afterBounds;

    public override void Undo(InkDocument doc) => Apply(doc, _beforeFill, _beforeBorder,
        _beforeRadius, _beforeInk, _beforeColor, _beforePaper);

    public override void Redo(InkDocument doc) => Apply(doc, _afterFill, _afterBorder,
        _afterRadius, _afterInk, _afterColor, _afterPaper);

    private void Apply(InkDocument doc, Color4 fill, Color4 border, float radius,
                       Color4 ink, Color4 color, bool paper)
    {
        var box = _stroke.TextBox;
        if (box == null) return;
        box.Fill = fill;
        box.Border = border;
        box.Radius = radius;
        box.Ink = ink;
        box.Paper = paper;
        _stroke.Color = color;
        box.InvalidateLayout();
        _stroke.SyncTextBoxBounds();
        doc.RefreshStrokeGrid(_stroke);
    }
}
