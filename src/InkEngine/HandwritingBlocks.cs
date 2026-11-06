using System;
using System.Collections.Generic;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 把选中区域先切成“文字块”，再按块识别/替换。
///
/// 为什么：整板 OCR 会把双栏、公式碎片、图旁标注都算成独立行，行数一对不上
/// 就整页错位。按列/按大块切开后，每块自己识别、自己配对，错了只影响这一块。
/// </summary>
internal static class HandwritingBlocks
{
    internal sealed class Block
    {
        public List<Stroke> Strokes = new();
        public RectF Region;
        public List<HandwritingReplace.TextLine> Lines = new();
    }

    public static List<Block> Detect(IEnumerable<Stroke> selected, RectF region)
    {
        var blocks = new List<Block>();
        if (region.IsEmpty) return blocks;
        float regionH = region.MaxY - region.MinY;
        var source = new List<Stroke>(selected);

        var text = new List<Stroke>();
        foreach (var s in source)
        {
            if (s == null || s.Kind != StrokeKind.Freehand) continue;
            if (s.Tool == Tool.Highlighter || s.Locked || s.IsImage || s.IsShape) continue;
            var b = s.WorldInkBounds;
            if (b.IsEmpty || !b.Intersects(region)) continue;
            float w = b.MaxX - b.MinX, h = b.MaxY - b.MinY;
            if (h > regionH * 0.18f) continue;
            // 长直线 / 图形边：不是文字；手写字的单笔一般不会超过 400。
            if (w > 400f) continue;
            if (w > 220f && s.Points.Count < w * 0.12f) continue;
            text.Add(s);
        }
        if (text.Count == 0) return blocks;

        var allRegion = EditRegion.Of(text);
        if (allRegion.IsEmpty) return blocks;

        // 竖向分隔线（b3 中间那条红线）→ 左右两栏。
        var cuts = new List<float>();
        foreach (var s in source)
        {
            var b = s.WorldInkBounds;
            if (b.MaxY - b.MinY > regionH * 0.30f && b.MaxX - b.MinX < 40f)
                cuts.Add((b.MinX + b.MaxX) * 0.5f);
        }
        cuts.Sort();
        if (cuts.Count == 0)
        {
            // 没有分隔线：找一条“所有行都没墨”的竖向空带，作为双栏分界。
            int bins = Math.Max(1, (int)MathF.Ceiling(allRegion.MaxX - allRegion.MinX));
            var cover = new bool[bins];
            foreach (var s in text)
            {
                var b = s.WorldInkBounds;
                int a = Math.Clamp((int)(b.MinX - allRegion.MinX), 0, bins - 1);
                int z = Math.Clamp((int)(b.MaxX - allRegion.MinX), 0, bins - 1);
                for (int k = a; k <= z; k++) cover[k] = true;
            }
            int bestStart = -1, bestLen = 0, runStart = -1;
            for (int k = 0; k <= bins; k++)
            {
                bool empty = k < bins && !cover[k];
                if (empty && runStart < 0) runStart = k;
                if (!empty && runStart >= 0)
                {
                    int len = k - runStart;
                    if (len > bestLen) { bestLen = len; bestStart = runStart; }
                    runStart = -1;
                }
            }
            if (bestLen >= 80)
            {
                float cut = allRegion.MinX + bestStart + bestLen * 0.5f;
                int left = 0, right = 0;
                foreach (var s in text)
                {
                    var b = s.WorldInkBounds;
                    if ((b.MinX + b.MaxX) * 0.5f < cut) left++; else right++;
                }
                if (left >= text.Count * 0.12f && right >= text.Count * 0.12f)
                    cuts.Add(cut);
            }
        }
        var edges = new List<float> { allRegion.MinX - 8f };
        foreach (float x in cuts)
        {
            if (x <= allRegion.MinX + 40f || x >= allRegion.MaxX - 40f) continue;
            if (x - edges[^1] < 40f) continue;
            edges.Add(x);
        }
        edges.Add(allRegion.MaxX + 8f);

        for (int c = 0; c + 1 < edges.Count; c++)
        {
            float x0 = edges[c], x1 = edges[c + 1];
            var column = new List<Stroke>();
            var cr = RectF.Empty;
            foreach (var s in text)
            {
                var b = s.WorldInkBounds;
                float cx = (b.MinX + b.MaxX) * 0.5f;
                if (cx < x0 || cx > x1) continue;
                column.Add(s);
                cr.Add(b);
            }
            if (column.Count < 3) continue;
            var block = new Block { Strokes = column, Region = cr };
            block.Lines = HandwritingReplace.DetectLines(column, cr, 0);
            blocks.Add(block);
        }
        return blocks;
    }
}
