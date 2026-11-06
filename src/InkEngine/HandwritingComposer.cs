using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 一行 OCR 文字 → 文字 Glyph + 行内公式图片。
///
/// 2026-11-06 用户要求：
///   · 中文用楷体（漓雨手书），英文用正常拉丁字体（Segoe UI），符号用符号字体；
///   · 公式按正规数学排版（CSharpMath / LaTeX），不把 `\in` 当普通字符画。
///
/// 文字段仍走 <see cref="GlyphText"/> 的矢量轮廓；公式段走
/// <see cref="MathRenderer"/> 渲成透明位图，作为 <see cref="StrokeKind.Image"/> 放回原行。
/// </summary>
internal static class HandwritingComposer
{
    private sealed class MathRun
    {
        public ImageData Image;
        public float X;
        public float Top;
        public float Width;
        public float Height;
    }

    /// <summary>返回这一行要加进文档的对象；没有可画内容时返回 null。</summary>
    public static List<Stroke> BuildLine(string raw, RectF box, Color4 color,
                                         float sizeOverride = 0f, bool allowShrink = true)
    {
        if (box.IsEmpty || string.IsNullOrWhiteSpace(raw)) return null;
        var runs = OcrText.SplitRuns(raw);
        if (runs.Count == 0 || !runs.Exists(r => r.IsMath))
            return Fallback(raw, box, color, sizeOverride, allowShrink);

        float boxW = box.MaxX - box.MinX, boxH = box.MaxY - box.MinY;
        float size = sizeOverride > 0f
            ? sizeOverride
            : MathF.Max(4f, boxH * GlyphText.SizePerLineHeight);
        Layout layout = null;

        for (int pass = 0; pass < 3; pass++)
        {
            layout = LayoutRuns(runs, size, color);
            if (layout == null) return Fallback(raw, box, color, sizeOverride, allowShrink);
            if (layout.Width <= boxW * 1.02f && layout.Height <= boxH * 1.08f) break;
            if (!allowShrink) break;
            float k = MathF.Min(boxW * 1.02f / MathF.Max(1f, layout.Width),
                                boxH * 1.08f / MathF.Max(1f, layout.Height));
            size *= MathF.Max(0.05f, k * 0.99f);
            if (size < 3f) break;
        }
        if (layout == null) return Fallback(raw, box, color, sizeOverride, allowShrink);

        float dx = box.MinX - layout.MinX;
        float dy = box.MinY + (boxH - layout.Height) * 0.5f - layout.MinY;
        var result = new List<Stroke>();

        if (layout.Contours.Count > 0)
        {
            var g = new Stroke { Kind = StrokeKind.Glyph, Color = color, Width = 1f };
            foreach (var contour in layout.Contours)
            {
                bool first = true;
                foreach (var p in contour)
                {
                    g.Points.Add(new InkPoint
                    {
                        X = p.X + dx, Y = p.Y + dy,
                        P = first ? -1f : 1f, T = 0,
                    });
                    first = false;
                }
            }
            g.InvalidateMetrics();
            g.RecomputeBounds();
            result.Add(g);
        }

        foreach (var m in layout.MathRuns)
        {
            var img = new Stroke
            {
                Tool = Tool.Pen,
                Kind = StrokeKind.Image,
                Color = color,
                Width = 0f,
                Image = m.Image,
            };
            img.SetPoints(new[]
            {
                new Vector2(m.X + dx, m.Top + dy),
                new Vector2(m.X + dx + m.Width, m.Top + dy + m.Height),
            });
            img.RecomputeBounds();
            result.Add(img);
        }
        return result.Count > 0 ? result : null;
    }

    private static List<Stroke> Fallback(string raw, RectF box, Color4 color,
                                         float sizeOverride = 0f, bool allowShrink = true)
    {
        var g = GlyphText.BuildLine(GlyphText.CjkFontPath, OcrText.Normalize(raw), box, color,
                                    sizeOverride, allowShrink);
        return g == null ? null : new List<Stroke> { g };
    }

    private sealed class Layout
    {
        public List<List<Vector2>> Contours = new();
        public List<MathRun> MathRuns = new();
        public float MinX, MinY, MaxX, MaxY;
        public float Width => MaxX - MinX;
        public float Height => MaxY - MinY;
    }

    private static Layout LayoutRuns(List<(bool IsMath, string Text)> runs,
                                     float size, Color4 color)
    {
        var layout = new Layout();
        var fonts = GlyphText.Fonts.Default;
        float x = 0f;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        void Add(float x0, float y0, float x1, float y1)
        {
            if (x0 < minX) minX = x0;
            if (x1 > maxX) maxX = x1;
            if (y0 < minY) minY = y0;
            if (y1 > maxY) maxY = y1;
        }

        foreach (var (isMath, text) in runs)
        {
            if (!isMath)
            {
                string t = OcrText.Normalize(text);
                var l = GlyphText.Layout(fonts, t, size, x, 0f);
                if (l.Contours.Count == 0) { x += l.Advance; continue; }
                foreach (var c in l.Contours)
                {
                    layout.Contours.Add(c);
                    foreach (var p in c) Add(p.X, p.Y, p.X, p.Y);
                }
                x += MathF.Max(l.Advance, size * 0.1f);
                continue;
            }

            // OCR 常把平行的双斜杠写成 //；CSharpMath 需要 \parallel。
            string math = text.Replace("//", "\\parallel");
            var r = MathRenderer.RenderImage(math, size, color, 2f);
            if (r?.Image == null || r.LogicalWidth <= 0 || r.LogicalHeight <= 0)
            {
                // 公式渲不出来：退回 Unicode 文字，至少不丢内容。
                string fallback = OcrText.Normalize("$" + text + "$");
                var l = GlyphText.Layout(fonts, fallback, size, x, 0f);
                foreach (var c in l.Contours) layout.Contours.Add(c);
                x += MathF.Max(l.Advance, size * 0.1f);
                continue;
            }

            var img = ImageData.AdoptPremultiplied(r.Image.Width, r.Image.Height,
                                                   (byte[])r.Image.Bgra.Clone());
            if (img == null) continue;

            // CSharpMath 的自然字号偏高；行内公式按正文 x-height 缩，
            // 分式 / 根式这种多层的再给大一点。0.52 太小会和中文脱节（用户说的“不和谐”）。
            float targetH = size * (text.Contains("\\frac") || text.Contains("\\sqrt") ? 1.0f : 0.68f);
            float k = targetH / MathF.Max(1f, r.LogicalHeight);
            float w = r.LogicalWidth * k;
            float asc = r.LogicalAscent * k;
            float top = -asc;
            layout.MathRuns.Add(new MathRun { Image = img, X = x, Top = top, Width = w, Height = targetH });
            Add(x, top, x + w, top + targetH);
            x += w + size * 0.06f;
        }

        if (minX > maxX) return null;
        layout.MinX = minX; layout.MinY = minY;
        layout.MaxX = maxX; layout.MaxY = maxY;
        return layout;
    }
}
