namespace InkEngine;

/// <summary>
/// 选中交互矢量图（<see cref="StrokeKind.Figure"/>）时，图卡上方那条参数滑杆。
/// **只画在 overlay**，不进内容层/存档/导出；拖动只改 PlotSpec.Params。
/// </summary>
internal static class FigureParamBar
{
    public const float PanelWidth = 286f;
    public const float RowHeight = 34f;
    public const float Pad = 10f;

    public static List<string> Keys(Stroke s)
    {
        var keys = new List<string>();
        if (s?.Kind != StrokeKind.Figure || s.Figure == null) return keys;
        if (s.Figure.Kind == "conic" && s.Figure.Conic != null)
        {
            keys.Add("a");
            keys.Add("b");
            return keys;
        }
        if (s.Figure.Params.Count > 0)
        {
            keys.AddRange(s.Figure.Params.Keys);
            keys.Sort(StringComparer.Ordinal);
        }
        return keys;
    }

    public static bool TryGet(Stroke s, string key, out double value)
    {
        value = 0;
        if (s?.Kind != StrokeKind.Figure || s.Figure == null) return false;
        if (s.Figure.Kind == "conic" && s.Figure.Conic != null)
        {
            if (key == "a") { value = s.Figure.Conic.A; return true; }
            if (key == "b") { value = s.Figure.Conic.B; return true; }
            return false;
        }
        return s.Figure.Params.TryGetValue(key, out value);
    }

    public static void Set(Stroke s, string key, double value)
    {
        if (s?.Kind != StrokeKind.Figure || s.Figure == null) return;
        if (s.Figure.Kind == "conic" && s.Figure.Conic != null)
        {
            float v = Math.Clamp((float)value, 0.1f, 50f);
            if (key == "a") s.Figure.Conic.A = v;
            else if (key == "b") s.Figure.Conic.B = v;
            return;
        }
        s.Figure.Params[key] = value;
    }

    public static RectF PanelRect(RectF frame, float ui, RectF viewport, int rows)
    {
        float w = PanelWidth * ui;
        float h = (rows * RowHeight + Pad * 2f) * ui;
        float cx = (frame.MinX + frame.MaxX) * 0.5f;
        float x = Math.Clamp(cx - w * 0.5f,
                             viewport.MinX + 8f * ui,
                             MathF.Max(viewport.MinX + 8f * ui, viewport.MaxX - w - 8f * ui));
        float y = frame.MinY - h - 10f * ui;
        if (y < viewport.MinY + 8f * ui)
            y = frame.MaxY + 10f * ui;
        y = Math.Clamp(y, viewport.MinY + 8f * ui,
                       MathF.Max(viewport.MinY + 8f * ui, viewport.MaxY - h - 8f * ui));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    public static RectF RowRect(RectF panel, int i, float ui)
    {
        float y = panel.MinY + (Pad + i * RowHeight) * ui;
        return new RectF
        {
            MinX = panel.MinX + 6f * ui,
            MinY = y,
            MaxX = panel.MaxX - 6f * ui,
            MaxY = y + RowHeight * ui,
        };
    }

    public static RectF TrackRect(RectF row, float ui)
    {
        return new RectF
        {
            MinX = row.MinX + 34f * ui,
            MinY = row.MaxY - 8f * ui,
            MaxX = row.MaxX - 86f * ui,
            MaxY = row.MaxY - 4f * ui,
        };
    }

    /// <summary>拖动范围：以按下时的值为中心，±max(2, |v|)。</summary>
    public static void Range(double value, out float min, out float max)
    {
        float v = (float)value;
        float span = MathF.Max(2f, MathF.Abs(v));
        min = v - span;
        max = v + span;
        if (max - min < 1e-4f) { min -= 1f; max += 1f; }
    }

    public static float ValueAt(RectF track, float x, float min, float max)
    {
        float w = MathF.Max(1f, track.MaxX - track.MinX);
        float t = Math.Clamp((x - track.MinX) / w, 0f, 1f);
        return min + t * (max - min);
    }

    public static int RowAt(RectF panel, int rows, float x, float y, float ui)
    {
        if (rows <= 0 || !panel.Contains(x, y)) return -1;
        for (int i = 0; i < rows; i++)
            if (RowRect(panel, i, ui).Contains(x, y)) return i;
        return -1;
    }
}
