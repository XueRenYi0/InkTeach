using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// **字库描边**：把字体字形转成真正的墨迹笔画。
///
/// 2026-11-05 用户定：手写优化先用「字库描边」——不是图片、不是文本框，
/// 而是把字形变成一条条 <see cref="Stroke"/>，能选、能擦、能回放。
///
/// 原理（每一字，只算一次，结果可缓存）：
///   1. 用 SkiaSharp 把字渲染成高分辨率黑白位图（超采样，边缘干净）
///   2. 细化（Zhang-Suen）得到笔画中心线
///   3. 距离变换得到每个中心线点的**局部半宽**
///   4. 沿中心线把点串成折线 → 每条折线一笔，宽度取该笔的中位宽度
///
/// 为什么宽度取「每条笔画的中位宽度」而不是逐点宽度：引擎的压感渲染对
/// 极短笔画不稳，而板书字号（40–80px）下按笔取宽已经能还原「横细竖粗」。
/// 以后要更细可以改逐点。
/// </summary>
public static class GlyphInk
{
    /// <summary>超采样倍数：栅格化用 2 倍，缩小回去边缘更稳。</summary>
    private const int SuperSample = 2;

    /// <summary>一个字转出来的笔画（点已归一化到「字高 = 1」，原点在左上）。</summary>
    public sealed class Glyph
    {
        public float WidthRatio;                 // 宽 / 高
        public List<(float[] Xs, float[] Ys, float W)> Strokes = new();
    }

    private static readonly Dictionary<string, Glyph> Cache = new();

    /// <summary>取一个字的笔画（带缓存）。fontPath 用系统字体文件即可。</summary>
    public static Glyph Get(string fontPath, char ch)
    {
        string key = fontPath + "|" + ch;
        if (Cache.TryGetValue(key, out var g)) return g;
        g = Build(fontPath, ch);
        Cache[key] = g;
        return g;
    }

    private static Glyph Build(string fontPath, char ch)
    {
        const int H = 384;                       // 归一化用的参考字高（越高，缩放后越平滑）
        int W = H * 2;
        using var bmp = new SKBitmap(W * SuperSample, H * SuperSample);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var tf = SKTypeface.FromFile(fontPath);
            if (tf == null) return null;
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.Black,
                Typeface = tf,
                TextSize = H * SuperSample * 0.92f,
                TextAlign = SKTextAlign.Center,
            };
            canvas.DrawText(ch.ToString(), W * SuperSample * 0.5f, H * SuperSample * 0.88f, paint);
        }

        int w = bmp.Width, h = bmp.Height;
        var mask = new byte[w * h];
        var pix = bmp.Pixels;
        for (int i = 0; i < w * h; i++)
            mask[i] = (byte)(pix[i].Alpha > 110 ? 1 : 0);

        // 去掉四周空白，得到紧致包围盒
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x] != 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return null;

        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        var sub = new byte[bw * bh];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                sub[y * bw + x] = mask[(y + minY) * w + (x + minX)];

        // **中心线 + 逐笔宽度**：骨架折线是曲线，边缘天然平滑；
        // 每条折线的宽度取该笔的局部半宽中位数 —— 正好还原「横细竖粗」。
        var skel = Thin(sub, bw, bh);
        var dist = Distance(sub, bw, bh);
        var lines = Trace(skel, bw, bh);

        var glyph = new Glyph { WidthRatio = (float)bw / bh };
        foreach (var line in lines)
        {
            if (line.Count < 2) continue;
            var ws = new List<float>();
            foreach (var (x, y) in line)
            {
                int idx = y * bw + x;
                if (idx >= 0 && idx < dist.Length) ws.Add(dist[idx]);
            }
            if (ws.Count == 0) continue;
            ws.Sort();
            float med = ws[ws.Count / 2] * 2f / bh;
            if (med < 0.002f) continue;

            var xs = new float[line.Count];
            var ys = new float[line.Count];
            for (int i = 0; i < line.Count; i++)
            {
                xs[i] = (float)line[i].x / bh;
                ys[i] = (float)line[i].y / bh;
            }
            glyph.Strokes.Add((xs, ys, med));
        }
        return glyph;
    }

    // ---- Zhang-Suen 细化 -----------------------------------------------------

    private static byte[] Thin(byte[] src, int w, int h)
    {
        var img = (byte[])src.Clone();
        var marker = new List<int>();
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 60)
        {
            changed = false;
            for (int step = 0; step < 2; step++)
            {
                marker.Clear();
                for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        if (img[i] == 0) continue;
                        int p2 = img[i - w], p3 = img[i - w + 1], p4 = img[i + 1],
                            p5 = img[i + w + 1], p6 = img[i + w], p7 = img[i + w - 1],
                            p8 = img[i - 1], p9 = img[i - w - 1];
                        int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                        if (b < 2 || b > 6) continue;
                        int a = 0;
                        if (p2 == 0 && p3 == 1) a++;
                        if (p3 == 0 && p4 == 1) a++;
                        if (p4 == 0 && p5 == 1) a++;
                        if (p5 == 0 && p6 == 1) a++;
                        if (p6 == 0 && p7 == 1) a++;
                        if (p7 == 0 && p8 == 1) a++;
                        if (p8 == 0 && p9 == 1) a++;
                        if (p9 == 0 && p2 == 1) a++;
                        if (a != 1) continue;
                        if (step == 0)
                        {
                            if (p2 * p4 * p6 != 0) continue;
                            if (p4 * p6 * p8 != 0) continue;
                        }
                        else
                        {
                            if (p2 * p4 * p8 != 0) continue;
                            if (p2 * p6 * p8 != 0) continue;
                        }
                        marker.Add(i);
                    }
                if (marker.Count > 0)
                {
                    changed = true;
                    foreach (int i in marker) img[i] = 0;
                }
            }
        }
        return img;
    }

    // ---- 距离变换（两遍倒角） --------------------------------------------------

    private static float[] Distance(byte[] mask, int w, int h)
    {
        var d = new float[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = mask[i] != 0 ? 1e6f : 0f;
        const float s2 = 1.41421356f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (d[i] == 0f) continue;
                if (x > 0) d[i] = Math.Min(d[i], d[i - 1] + 1);
                if (y > 0) d[i] = Math.Min(d[i], d[i - w] + 1);
                if (x > 0 && y > 0) d[i] = Math.Min(d[i], d[i - w - 1] + s2);
                if (x < w - 1 && y > 0) d[i] = Math.Min(d[i], d[i - w + 1] + s2);
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (d[i] == 0f) continue;
                if (x < w - 1) d[i] = Math.Min(d[i], d[i + 1] + 1);
                if (y < h - 1) d[i] = Math.Min(d[i], d[i + w] + 1);
                if (x < w - 1 && y < h - 1) d[i] = Math.Min(d[i], d[i + w + 1] + s2);
                if (x > 0 && y < h - 1) d[i] = Math.Min(d[i], d[i + w - 1] + s2);
            }
        return d;
    }

    // ---- 骨架 → 折线 ---------------------------------------------------------

    private static List<List<(int x, int y)>> Trace(byte[] skel, int w, int h)
    {
        var used = new bool[w * h];
        var lines = new List<List<(int x, int y)>>();

        int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

        int Deg(int x, int y)
        {
            int n = 0;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && skel[ny * w + nx] != 0) n++;
            }
            return n;
        }

        // 两轮：先从端点起笔（线条最完整），再兜底从任意未访问点起笔（闭环不会漏）。
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (skel[i] == 0 || used[i]) continue;
                    if (pass == 0 && Deg(x, y) != 1) continue;

                    var line = new List<(int x, int y)> { (x, y) };
                    used[i] = true;
                    int cx = x, cy = y;
                    while (true)
                    {
                        int nx = -1, ny = -1, best = 99;
                        for (int k = 0; k < 8; k++)
                        {
                            int tx = cx + dx[k], ty = cy + dy[k];
                            if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                            int ti = ty * w + tx;
                            if (skel[ti] == 0 || used[ti]) continue;
                            int dg = Deg(tx, ty);
                            if (dg < best) { best = dg; nx = tx; ny = ty; }
                        }
                        if (nx < 0) break;
                        used[ny * w + nx] = true;
                        line.Add((nx, ny));
                        cx = nx; cy = ny;
                    }
                    if (line.Count >= 2) lines.Add(line);
                }
        }
        return lines;
    }
}
