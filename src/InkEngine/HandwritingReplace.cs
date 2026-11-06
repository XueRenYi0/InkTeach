using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 「手写优化」的行框检测与整行替换。
///
/// 2026-11-06 方案（用户定）：行框**不用 OCR 的 bbox**，用自己的笔画数据算：
///   1. 只看自由笔迹（图形、箭头、文本框、图片全部排除）；
///   2. 按行投影切行（和 `tools/ai-bench/mixed_replace.py` 的已验证原型同一口径）；
///   3. 每行取列范围 + 主色（红 / 蓝分开，避免蓝笔批注把整行染紫）；
///   4. 用 <see cref="GlyphText"/> 把识别文字排回原行框。
///
/// 这里只做“检测 + 生成 + 一步撤销替换”，由引擎在选中对象上调用
/// （AI 菜单「手写优化」在 OCR 结果回来后走 <see cref="Apply"/>）。
/// </summary>
internal static class HandwritingReplace
{
    internal sealed class TextLine
    {
        public RectF Box;
        public Color4 Color;
        public readonly List<Stroke> Strokes = new();
    }

    /// <summary>
    /// 在 <paramref name="region"/> 里切出文字行。区域一般就是用户选中的那块；
    /// 没选中时调用方传整块板。
    /// </summary>
    public static List<TextLine> DetectLines(IEnumerable<Stroke> source, RectF region,
                                             int expectedLines = 0)
    {
        var lines = new List<TextLine>();
        if (region.IsEmpty) return lines;

        float regionH = region.MaxY - region.MinY;
        if (regionH < 8f) return lines;

        var candidates = new List<Stroke>();
        foreach (var s in source)
        {
            if (s == null || s.HiddenForPairing) continue;
            // 只处理笔迹：图形 / 箭头 / 图片 / 文本框 / 已经替换出来的 Glyph 都不碰。
            if (s.Kind != StrokeKind.Freehand) continue;
            if (s.Tool == Tool.Highlighter) continue;
            if (s.IsImage || s.IsShape || s.Locked) continue;
            var b = s.WorldInkBounds;
            if (b.IsEmpty || !b.Intersects(region)) continue;
            // 特别高的笔迹多半是图形里的长线 / 大括号，先不参与切行；
            // 真正的文字行高一般 < 区域高度的 18%。
            float h = b.MaxY - b.MinY;
            if (h > regionH * 0.18f) continue;
            candidates.Add(s);
        }
        if (candidates.Count == 0) return lines;

        int rowCount = Math.Max(1, (int)MathF.Ceiling(regionH) + 1);
        var count = new int[rowCount];
        var minX = new float[rowCount];
        var maxX = new float[rowCount];
        for (int i = 0; i < rowCount; i++) { minX[i] = float.MaxValue; maxX[i] = float.MinValue; }

        foreach (var s in candidates)
        {
            var b = s.WorldInkBounds;
            int y0 = Math.Clamp((int)MathF.Floor(b.MinY - region.MinY), 0, rowCount - 1);
            int y1 = Math.Clamp((int)MathF.Ceiling(b.MaxY - region.MinY), 0, rowCount - 1);
            for (int y = y0; y <= y1; y++)
            {
                count[y]++;
                if (b.MinX < minX[y]) minX[y] = b.MinX;
                if (b.MaxX > maxX[y]) maxX[y] = b.MaxX;
            }
        }

        int maxCount = 0;
        foreach (int c in count) if (c > maxCount) maxCount = c;
        int threshold = Math.Max(2, (int)(maxCount * 0.02f));

        // 行带：连续超过阈值；间隔 < 6px 的合并（避免把“一”上的点切成两行）。
        var bands = new List<(int Y0, int Y1)>();
        int start = -1;
        for (int y = 0; y < rowCount; y++)
        {
            bool on = count[y] >= threshold;
            if (on && start < 0) start = y;
            else if (!on && start >= 0)
            {
                if (y - start >= 8) bands.Add((start, y));
                start = -1;
            }
        }
        if (start >= 0 && rowCount - start >= 8) bands.Add((start, rowCount));

        var merged = new List<(int Y0, int Y1)>();
        foreach (var b in bands)
        {
            if (merged.Count > 0 && b.Y0 - merged[^1].Y1 < 6)
                merged[^1] = (merged[^1].Y0, b.Y1);
            else
                merged.Add(b);
        }

        // 切行只信**几何**：如果一条行带明显比中位行高（上下标、分数、圈注把两行粘在一起），
        // 就在最空的一行中间劈开。不再按 OCR 行数硬补——模型偶尔会把顶点 / 括号批注
        // 单独认成一行（D /（包含）），硬补会让后面的行整体错位。
        if (merged.Count > 0)
        {
            var hs = new List<int>();
            foreach (var b in merged) hs.Add(b.Y1 - b.Y0);
            hs.Sort();
            int median = hs[hs.Count / 2];
            int guard = 0;
            while (guard++ < 24)
            {
                int bi = -1, tallest = 0;
                for (int i = 0; i < merged.Count; i++)
                {
                    int h = merged[i].Y1 - merged[i].Y0;
                    if (h > tallest) { tallest = h; bi = i; }
                }
                if (bi < 0 || tallest < 24 || tallest <= median * 1.7f) break;

                var (y0, y1) = merged[bi];
                int from = y0 + Math.Max(2, (y1 - y0) / 4);
                int to = y1 - Math.Max(2, (y1 - y0) / 4);
                int split = -1, best = int.MaxValue;
                for (int y = from; y <= to; y++)
                    if (count[y] < best) { best = count[y]; split = y; }
                if (split <= y0 + 4 || split >= y1 - 4) break;

                merged[bi] = (y0, split);
                merged.Insert(bi + 1, (split, y1));
            }
        }
        merged.Sort((a, b) => a.Y0.CompareTo(b.Y0));

        foreach (var (y0, y1) in merged)
        {
            float bandY0 = region.MinY + y0;
            float bandY1 = region.MinY + y1;
            if (bandY1 - bandY0 > regionH * 0.22f) continue;   // 图形 / 大括号

            var line = new TextLine();
            float x0 = float.MaxValue, x1 = float.MinValue;

            // 主色按“红 / 蓝 / 其它”分桶；蓝笔批注不会把红字染紫。
            int redW = 0, blueW = 0, otherW = 0;
            float rr = 0, rg = 0, rb = 0, br = 0, bg = 0, bb = 0, or_ = 0, og = 0, ob = 0;

            foreach (var s in candidates)
            {
                var b = s.WorldInkBounds;
                float cy = (b.MinY + b.MaxY) * 0.5f;
                if (cy < bandY0 - 2f || cy > bandY1 + 2f) continue;
                line.Strokes.Add(s);

                var c = s.Color;
                bool red = c.R > c.B * 1.25f && c.R > 0.35f;
                bool blue = c.B > c.R * 1.25f && c.B > 0.35f;
                int w = Math.Max(1, s.Points.Count);
                if (red) { redW += w; rr += c.R * w; rg += c.G * w; rb += c.B * w; }
                else if (blue) { blueW += w; br += c.R * w; bg += c.G * w; bb += c.B * w; }
                else { otherW += w; or_ += c.R * w; og += c.G * w; ob += c.B * w; }

                foreach (var p in s.Points)
                {
                    if (p.Y < bandY0 - 2f || p.Y > bandY1 + 2f) continue;
                    if (p.X < x0) x0 = p.X;
                    if (p.X > x1) x1 = p.X;
                }
            }

            if (line.Strokes.Count == 0 || x1 <= x0) continue;
            line.Box = new RectF { MinX = x0, MinY = bandY0, MaxX = x1, MaxY = bandY1 };
            line.Color = Dominant(redW, blueW, otherW,
                                  rr, rg, rb, br, bg, bb, or_, og, ob,
                                  line.Strokes[0].Color);
            lines.Add(line);
        }

        lines.Sort((a, b) => a.Box.MinY.CompareTo(b.Box.MinY));
        return lines;
    }

    private static Color4 Dominant(int rw, int bw, int ow,
        float rr, float rg, float rb, float br, float bg, float bb, float or_, float og, float ob,
        Color4 fallback)
    {
        if (rw == 0 && bw == 0 && ow == 0) return fallback;
        if (rw >= bw && rw >= ow)
            return new Color4(rr / rw, rg / rw, rb / rw, 1f);
        if (bw >= rw && bw >= ow)
            return new Color4(br / bw, bg / bw, bb / bw, 1f);
        return new Color4(or_ / ow, og / ow, ob / ow, 1f);
    }

    /// <summary>
    /// 把检测到的行按顺序配上识别文字，生成替换后的对象列表。
    /// 返回 null 表示没有可替换的行。
    /// </summary>
    public static List<Stroke> BuildReplacement(IReadOnlyList<Stroke> all,
                                                List<TextLine> lines,
                                                IReadOnlyList<string> texts,
                                                string fontPath,
                                                bool includeFormulas,
                                                out List<Stroke> replacedText,
                                                out List<Stroke> glyphs,
                                                out int skipped)
    {
        replacedText = new List<Stroke>();
        glyphs = new List<Stroke>();
        skipped = 0;
        if (lines == null || lines.Count == 0 || texts == null || texts.Count == 0)
            return null;

        int n = Math.Min(lines.Count, texts.Count);
        for (int i = 0; i < n; i++)
        {
            // 公式默认保留原笔迹：OCR 对下标 / 集合符号很敏感（实测 D₁C₁→DD₁、
            // ⊄→∉），自动替换会把数学意思改掉。只有用户明确选“含公式”才替换。
            if (!includeFormulas && OcrText.HasMath(texts[i])) { skipped++; continue; }

            string t = OcrText.Normalize(texts[i]?.Trim());
            if (string.IsNullOrEmpty(t)) continue;
            var g = GlyphText.BuildLine(fontPath, t, lines[i].Box, lines[i].Color);
            if (g == null) continue;
            glyphs.Add(g);
            replacedText.AddRange(lines[i].Strokes);
        }
        if (glyphs.Count == 0) return null;

        var removed = new HashSet<Stroke>(replacedText);
        var result = new List<Stroke>(all.Count + glyphs.Count);
        foreach (var s in all)
            if (!removed.Contains(s)) result.Add(s);
        result.AddRange(glyphs);
        return result;
    }

    /// <summary>
    /// 在文档上真正执行替换：删原文字笔迹 + 加 Glyph 行，合成**一步撤销**。
    /// 返回替换了几行；<paramref name="skipped"/> = 因公式保留原笔迹的行数。
    /// </summary>
    public static int Apply(InkDocument doc, IReadOnlyList<Stroke> selected,
                            RectF region, IReadOnlyList<string> texts, string fontPath)
        => Apply(doc, selected, region, texts, fontPath, false, out _, out _);

    public static int Apply(InkDocument doc, IReadOnlyList<Stroke> selected,
                            RectF region, IReadOnlyList<string> texts, string fontPath,
                            bool includeFormulas)
        => Apply(doc, selected, region, texts, fontPath, includeFormulas, out _, out _);

    public static int Apply(InkDocument doc, IReadOnlyList<Stroke> selected,
                            RectF region, IReadOnlyList<string> texts, string fontPath,
                            out List<Stroke> glyphs)
        => Apply(doc, selected, region, texts, fontPath, false, out glyphs, out _);

    public static int Apply(InkDocument doc, IReadOnlyList<Stroke> selected,
                            RectF region, IReadOnlyList<string> texts, string fontPath,
                            bool includeFormulas, out List<Stroke> glyphs, out int skipped)
    {
        glyphs = new List<Stroke>();
        skipped = 0;
        if (doc == null || selected == null || texts == null || texts.Count == 0) return 0;
        var lines = DetectLines(selected, region, texts.Count);
        var result = BuildReplacement(selected, lines, texts, fontPath, includeFormulas,
                                      out var replacedText, out glyphs, out skipped);
        if (result == null || replacedText.Count == 0) return 0;

        var remove = new RemoveStrokesAction();
        var seen = new HashSet<Stroke>();
        foreach (var s in replacedText)
        {
            if (!seen.Add(s)) continue;   // 两个相邻行带可能同时圈到一条笔迹，只删一次
            int idx = doc.Strokes.IndexOf(s);
            if (idx >= 0) remove.Items.Add((idx, s));
        }
        if (remove.Items.Count == 0) return 0;

        var add = new AddStrokesAction();
        add.Strokes.AddRange(glyphs);

        remove.Redo(doc);
        add.Redo(doc);
        doc.CommitCompound(remove, add);
        return glyphs.Count;
    }
}
