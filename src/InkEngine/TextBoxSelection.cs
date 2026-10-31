using System.Numerics;

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

        // 起点用对象局部坐标（文本框只做平移，不旋转；逆变换仍然写全，防以后加旋转）。
        // ⚠ 传进来的 screenX/screenY 已经是**画布坐标**（OnPointerDown 先过了 ScreenToCanvas）；
        // 这里再减一次 ViewOffsetY 会让纵向拖手柄在滚动画布后整体偏移，横向却正常
        // ——用户 2026-10-31 报的“点下边突然长高、点上面又缩下去”就是它。
        Vector2 p = new(screenX, screenY);
        if (!s.Transform.IsIdentity && Matrix3x2.Invert(s.Transform, out var inv))
            p = Vector2.Transform(p, inv);

        _textBoxResizeStroke = s;
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

        if (left) r.MinX = p.X;
        if (right) r.MaxX = p.X;
        if (top) r.MinY = p.Y;
        if (bottom) r.MaxY = p.Y;

        // 用户一旦亲手拖过手柄，就不再让输入时的“自动长宽”覆盖他的尺寸。
        box.AutoWidth = false;
        var screen = ScreenRectPhysical();
        float unit = MathF.Max(0.5f, box.Unit);
        float minH = 60f * unit;
        float maxW = MathF.Max(60f * unit, (screen.MaxX - screen.MinX) * 0.9f);
        float maxH = MathF.Max(minH, (screen.MaxY - screen.MinY) * 0.9f);
        // 最窄由"最长不可断的 token"决定（中文单字 / 英文单词 / 可缩小公式）；
        // 最宽由屏幕决定。两者都写在这一处，拖手柄和自适应用同一口径。
        float minW = MathF.Min(MathF.Max(60f * unit, _textBoxResizeMinWidth), maxW);
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

        float w = r.MaxX - r.MinX;
        float h = r.MaxY - r.MinY;
        var old = s.PaddedBounds;

        // 左右拖：只改宽，AI 题卡继续自动高度；
        // 上下/角拖：切成固定高度（用户定："拖了高度就以手动为准"）。
        if (_textBoxResizeWasAuto && (top || bottom))
        {
            box.AutoHeight = false;
            box.Height = h;
        }
        else if (!_textBoxResizeWasAuto)
        {
            box.AutoHeight = false;
            box.Height = h;
        }

        box.InvalidateLayout();
        s.SetTextBoxRect(new Vector2(r.MinX, r.MinY), w,
                         box.AutoHeight ? box.Height : h);
        if (box.AutoHeight)
        {
            _ = box.EnsureLayout();
            s.SetTextBoxRect(new Vector2(r.MinX, r.MinY), w, box.Height);
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
