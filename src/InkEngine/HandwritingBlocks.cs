using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 复杂版面分块：RLSA（Wong/Casey/Wahl 1982）+ 递归 XY-cut（Nagy & Seth 1984）。
///
/// 输入是矢量笔迹：先按 0.5 倍栅格化成墨迹 mask，横向/纵向膨胀把字粘成行、
/// 行粘成段，再用投影空白递归切列/切行。输出每个文字块的笔迹与行框。
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

        var text = new List<Stroke>();
        float regionH = region.MaxY - region.MinY;
        foreach (var s in selected)
        {
            if (s == null || s.Kind != StrokeKind.Freehand) continue;
            if (s.Tool == Tool.Highlighter || s.Locked || s.IsImage || s.IsShape) continue;
            var b = s.WorldInkBounds;
            if (b.IsEmpty || !b.Intersects(region)) continue;
            float w = b.MaxX - b.MinX, h = b.MaxY - b.MinY;
            if (h > regionH * 0.18f) continue;
            if (w > 400f) continue;
            if (w > 220f && s.Points.Count < w * 0.12f) continue;
            text.Add(s);
        }
        if (text.Count < 3) return blocks;

        var area = EditRegion.Of(text);
        if (area.IsEmpty) return blocks;

        // 行高：单笔高度中位数；汉字的一笔高度≈字高。
        var hs = new List<float>();
        foreach (var s in text)
        {
            var b = s.WorldInkBounds;
            float h = b.MaxY - b.MinY;
            if (h > 4f && h < 160f) hs.Add(h);
        }
        hs.Sort();
        float lineH = hs.Count > 0 ? hs[hs.Count / 2] * 1.15f : 48f;

        var sw = Stopwatch.StartNew();
        var mask = new Mask(area, 0.5f);
        foreach (var s in text) mask.DrawStroke(s);
        mask.SmearHorizontal(Math.Max(3, (int)(lineH * 0.5f * mask.Scale)));
        mask.SmearVertical(Math.Max(3, (int)(lineH * 0.9f * mask.Scale)));

        var rects = new List<RectI>();
        mask.Cut(new RectI(0, 0, mask.Width, mask.Height),
                 Math.Max(10, (int)(lineH * 0.4f * mask.Scale)),
                 Math.Max(12, (int)(lineH * 0.8f * mask.Scale)),
                 rects, 0, lineH);
        sw.Stop();
        if (Environment.GetEnvironmentVariable("INKTEACH_BLOCK_DEBUG") == "1")
            Console.WriteLine($"[blocks] text {text.Count}, mask {mask.Width}x{mask.Height}, {rects.Count} rects, {sw.ElapsedMilliseconds}ms");

        foreach (var r in rects)
        {
            RectF rf = r.ToRectF(area, mask.Scale);
            var inBlock = new List<Stroke>();
            foreach (var s in text)
            {
                var b = s.WorldInkBounds;
                float cx = (b.MinX + b.MaxX) * 0.5f, cy = (b.MinY + b.MaxY) * 0.5f;
                if (cx >= rf.MinX && cx <= rf.MaxX && cy >= rf.MinY && cy <= rf.MaxY)
                    inBlock.Add(s);
            }
            if (inBlock.Count < 3) continue;
            var block = new Block { Strokes = inBlock, Region = EditRegion.Of(inBlock) };
            block.Lines = HandwritingReplace.DetectLines(inBlock, block.Region, 0);
            if (Environment.GetEnvironmentVariable("INKTEACH_BLOCK_DEBUG") == "1")
                Console.WriteLine($"[blocks] rect {rf.MinX:F0},{rf.MinY:F0}..{rf.MaxX:F0},{rf.MaxY:F0} " +
                                  $"strokes {inBlock.Count}, lines {block.Lines.Count}");
            if (block.Lines.Count == 0) continue;
            blocks.Add(block);
        }
        return blocks;
    }

    private sealed class Mask
    {
        private readonly int _w, _h;
        private readonly float _x0, _y0, _s;
        private readonly bool[] _b;
        public float Scale => _s;
        public int Width => _w;
        public int Height => _h;

        public Mask(RectF area, float scale)
        {
            _s = scale;
            _x0 = area.MinX;
            _y0 = area.MinY;
            _w = Math.Max(1, (int)MathF.Ceiling((area.MaxX - area.MinX) * _s) + 2);
            _h = Math.Max(1, (int)MathF.Ceiling((area.MaxY - area.MinY) * _s) + 2);
            _b = new bool[_w * _h];
        }

        public void DrawStroke(Stroke s)
        {
            int radius = Math.Max(1, (int)(s.Width * 0.5f * _s));
            for (int i = 0; i + 1 < s.Points.Count; i++)
            {
                var a = s.Points[i];
                var b = s.Points[i + 1];
                float dx = b.X - a.X, dy = b.Y - a.Y;
                int n = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) * _s));
                for (int k = 0; k <= n; k++)
                {
                    float t = k / (float)n;
                    Dot((int)((a.X + dx * t - _x0) * _s), (int)((a.Y + dy * t - _y0) * _s), radius);
                }
            }
        }

        private void Dot(int cx, int cy, int r)
        {
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    int px = cx + x, py = cy + y;
                    if (px < 0 || py < 0 || px >= _w || py >= _h) continue;
                    _b[py * _w + px] = true;
                }
        }

        public void SmearHorizontal(int gap)
        {
            for (int y = 0; y < _h; y++)
            {
                int run = -1;
                for (int x = 0; x <= _w; x++)
                {
                    bool on = x < _w && _b[y * _w + x];
                    if (!on) { if (run < 0) run = x; continue; }
                    if (run >= 0)
                    {
                        if (x - run <= gap)
                            for (int k = run; k < x; k++) _b[y * _w + k] = true;
                        run = -1;
                    }
                }
            }
        }

        public void SmearVertical(int gap)
        {
            for (int x = 0; x < _w; x++)
            {
                int run = -1;
                for (int y = 0; y <= _h; y++)
                {
                    bool on = y < _h && _b[y * _w + x];
                    if (!on) { if (run < 0) run = y; continue; }
                    if (run >= 0)
                    {
                        if (y - run <= gap)
                            for (int k = run; k < y; k++) _b[k * _w + x] = true;
                        run = -1;
                    }
                }
            }
        }

        public void Cut(RectI area, int minColGap, int minRowGap,
                        List<RectI> output, int depth, float lineH)
        {
            int w = area.X1 - area.X0, h = area.Y1 - area.Y0;
            if (depth > 10 || output.Count > 32 || w < 60 * _s || h < 40 * _s)
            {
                output.Add(area);
                return;
            }

            if (TryCut(area, vertical: true, minColGap, minRowGap, output, depth, lineH)) return;
            if (TryCut(area, vertical: false, minColGap, minRowGap, output, depth, lineH)) return;
            output.Add(area);
        }

        private bool TryCut(RectI area, bool vertical, int minColGap, int minRowGap,
                            List<RectI> output, int depth, float lineH)
        {
            int minGap = vertical ? minColGap : minRowGap;
            int n = vertical ? area.X1 - area.X0 : area.Y1 - area.Y0;
            if (n <= 2 * minGap) return false;

            var proj = new int[n];
            if (vertical)
            {
                for (int x = area.X0; x < area.X1; x++)
                {
                    int c = 0;
                    for (int y = area.Y0; y < area.Y1; y++) if (_b[y * _w + x]) c++;
                    proj[x - area.X0] = c;
                }
            }
            else
            {
                for (int y = area.Y0; y < area.Y1; y++)
                {
                    int c = 0;
                    for (int x = area.X0; x < area.X1; x++) if (_b[y * _w + x]) c++;
                    proj[y - area.Y0] = c;
                }
            }

            int bestStart = -1, bestLen = 0, run = -1;
            int emptyThresh = vertical ? Math.Max(1, (area.Y1 - area.Y0) / 50) : 0;
            for (int k = 0; k <= n; k++)
            {
                bool empty = k < n && proj[k] <= emptyThresh;
                if (empty && run < 0) run = k;
                if (!empty && run >= 0)
                {
                    int len = k - run;
                    if (len > bestLen) { bestLen = len; bestStart = run; }
                    run = -1;
                }
            }
            if (bestStart <= 0 || bestLen < minGap || bestStart + bestLen >= n) return false;

            int left = 0, right = 0;
            for (int k = 0; k < n; k++)
            {
                if (k < bestStart) left += proj[k];
                else if (k >= bestStart + bestLen) right += proj[k];
            }
            if (left + right == 0 || left < (left + right) * 0.05f || right < (left + right) * 0.05f)
                return false;

            int cut = bestStart + bestLen / 2;
            if (vertical)
            {
                Cut(new RectI(area.X0, area.Y0, area.X0 + cut, area.Y1),
                    minColGap, minRowGap, output, depth + 1, lineH);
                Cut(new RectI(area.X0 + cut, area.Y0, area.X1, area.Y1),
                    minColGap, minRowGap, output, depth + 1, lineH);
            }
            else
            {
                Cut(new RectI(area.X0, area.Y0, area.X1, area.Y0 + cut),
                    minColGap, minRowGap, output, depth + 1, lineH);
                Cut(new RectI(area.X0, area.Y0 + cut, area.X1, area.Y1),
                    minColGap, minRowGap, output, depth + 1, lineH);
            }
            return true;
        }
    }

    private readonly record struct RectI(int X0, int Y0, int X1, int Y1)
    {
        public RectF ToRectF(RectF area, float scale) => new()
        {
            MinX = area.MinX + X0 / scale,
            MinY = area.MinY + Y0 / scale,
            MaxX = area.MinX + X1 / scale,
            MaxY = area.MinY + Y1 / scale,
        };
    }
}
