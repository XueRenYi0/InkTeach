using System;
using System.Threading.Tasks;

namespace InkEngine;

/// <summary>
/// 图片“美化”：**只做色温 / 光照 / 纸白 / 轻对比**，不旋转、不裁边、不改内容。
///
/// 口径（2026-11-10 和用户定）：
///   · 用在“图片对象”上（截图粘贴 / 拖入白板 / 导入 / 后续图片文档）；
///   · 目标是把手机拍纸的**黄光、灰底、阴影**压掉，纸接近白，红/蓝笔和黑字保留；
///   · 不做二值化、不做生成式重绘、不改变图片尺寸/方向；
///   · 透明图跳过；已经足够白的图直接返回 null，避免重复点“美化”越洗越白。
///
/// 参考实现（照搬思路，不引依赖）：
///   · Microsoft Lens：机内白平衡 + 去阴影 + 对比增强 + 保色；
///   · CamScanner Magic Color：纸白/黑场 + 大尺度光照估计（原图除以背景）；
///   · pagescan.enhance：低饱和亮像素估纸白 + 逐通道背景除法；
///   · Dropbox Document Enhancement：背景均匀白，同时保留原图色相/饱和度。
/// </summary>
internal static class PhotoBeautify
{
    /// <summary>
    /// 输入/输出都是**预乘 BGRA**（和 <see cref="ImageData"/> 一致）。
    /// 透明图返回 null；已经够白/没有明显偏色也返回 null（调用方提示“不用再美化”）。
    /// </summary>
    public static byte[] Process(byte[] src, int w, int h)
    {
        if (src == null || w < 8 || h < 8) return null;
        long n = (long)w * h;
        if (src.LongLength < n * 4) return null;

        // 透明贴纸不处理：预乘空间下做颜色校正会把边缘弄脏。
        for (int i = 3; i < src.Length; i += 4)
            if (src[i] != 255) return null;

        var before = PaperStats(src, w, h);
        if (before.Count > 0 && before.MedianY > 249f
            && MathF.Abs(before.MedianR - before.MedianB) < 3f)
            return null;   // 已经是白纸/中性色，重复点不叠加

        var dst = (byte[])src.Clone();

        RemoveShadows(dst, w, h);
        WhiteBalance(dst, w, h);
        PaperWhiteAndClean(dst, w, h);
        SharpenLuma(dst, w, h);

        return dst;
    }

    // ------------------------------------------------------------------
    //  纸面统计
    // ------------------------------------------------------------------

    private readonly struct Stats
    {
        public readonly int Count;
        public readonly float MedianB, MedianG, MedianR, MedianY;
        public Stats(int count, float b, float g, float r, float y)
        { Count = count; MedianB = b; MedianG = g; MedianR = r; MedianY = y; }
    }

    /// <summary>
    /// 采样“亮 + 低饱和”的像素当纸面：手机拍纸即使偏黄，纸仍然是画面里最亮、最不彩的一块。
    /// 用直方图求中位数，避免均值被黑字/阴影拉偏。
    /// </summary>
    private static Stats PaperStats(byte[] p, int w, int h)
    {
        long n = (long)w * h;
        int step = Math.Max(1, (int)MathF.Sqrt(n / 180000f));
        var hb = new int[256]; var hg = new int[256]; var hr = new int[256]; var hy = new int[256];
        int count = 0;
        for (int y = 0; y < h; y += step)
        {
            int row = y * w;
            for (int x = 0; x < w; x += step)
            {
                int i = (row + x) * 4;
                int b = p[i], g = p[i + 1], r = p[i + 2];
                int mx = Math.Max(b, Math.Max(g, r));
                int mn = Math.Min(b, Math.Min(g, r));
                int sat = mx - mn;
                int lum = (77 * r + 150 * g + 29 * b) >> 8;
                if (lum > 150 && sat < 70)
                {
                    hb[b]++; hg[g]++; hr[r]++; hy[lum]++; count++;
                }
            }
        }
        if (count < 200) return default;
        return new Stats(count,
            Median(hb, count), Median(hg, count), Median(hr, count), Median(hy, count));
    }

    private static float Median(int[] hist, int total)
    {
        int need = total / 2, acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= need) return i;
        }
        return 255f;
    }

    // ------------------------------------------------------------------
    //  1) 去阴影：低分辨率背景除法
    // ------------------------------------------------------------------

    private static void RemoveShadows(byte[] p, int w, int h)
    {
        int gw = Math.Clamp(w / 32, 12, 64);
        int gh = Math.Clamp(h / 32, 12, 64);
        var sb = new float[gw * gh]; var sg = new float[gw * gh]; var sr = new float[gw * gh];
        var cn = new int[gw * gh];

        for (int y = 0; y < h; y++)
        {
            int gy = Math.Min(gh - 1, y * gh / h);
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int gx = Math.Min(gw - 1, x * gw / w);
                int c = gy * gw + gx;
                int i = (row + x) * 4;
                sb[c] += p[i]; sg[c] += p[i + 1]; sr[c] += p[i + 2]; cn[c]++;
            }
        }
        for (int i = 0; i < sb.Length; i++)
        {
            float k = cn[i] > 0 ? 1f / cn[i] : 0f;
            sb[i] *= k; sg[i] *= k; sr[i] *= k;
        }

        // 阴影是低频：网格上两遍 3×3 平滑就够了，别把字边当背景。
        for (int pass = 0; pass < 2; pass++)
        {
            var tb = new float[sb.Length]; var tg = new float[sg.Length]; var tr = new float[sr.Length];
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    float ab = 0, ag = 0, ar = 0; int c = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Math.Clamp(gy + dy, 0, gh - 1);
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Math.Clamp(gx + dx, 0, gw - 1);
                            int k = yy * gw + xx;
                            ab += sb[k]; ag += sg[k]; ar += sr[k]; c++;
                        }
                    }
                    int q = gy * gw + gx;
                    tb[q] = ab / c; tg[q] = ag / c; tr[q] = ar / c;
                }
            sb = tb; sg = tg; sr = tr;
        }

        Parallel.For(0, h, y =>
        {
            float fy = ((y + 0.5f) * gh / h) - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, gh - 1);
            int y1 = Math.Min(gh - 1, y0 + 1);
            float ty = fy - y0;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float fx = ((x + 0.5f) * gw / w) - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, gw - 1);
                int x1 = Math.Min(gw - 1, x0 + 1);
                float tx = fx - x0;
                int i00 = y0 * gw + x0, i10 = y0 * gw + x1, i01 = y1 * gw + x0, i11 = y1 * gw + x1;
                float b = Lerp(Lerp(sb[i00], sb[i10], tx), Lerp(sb[i01], sb[i11], tx), ty);
                float g = Lerp(Lerp(sg[i00], sg[i10], tx), Lerp(sg[i01], sg[i11], tx), ty);
                float r = Lerp(Lerp(sr[i00], sr[i10], tx), Lerp(sr[i01], sr[i11], tx), ty);

                int q = (row + x) * 4;
                int B = p[q], G = p[q + 1], R = p[q + 2];
                int mx = Math.Max(B, Math.Max(G, R)), mn = Math.Min(B, Math.Min(G, R));
                float sat = (mx - mn) / 255f;
                float weight = 1f - SmoothStep(0.22f, 0.62f, sat);
                if (weight <= 0.001f) continue;

                p[q] = ClampByte(B + (B * (220f / MathF.Max(8f, b)) - B) * weight);
                p[q + 1] = ClampByte(G + (G * (220f / MathF.Max(8f, g)) - G) * weight);
                p[q + 2] = ClampByte(R + (R * (220f / MathF.Max(8f, r)) - R) * weight);
            }
        });
    }

    // ------------------------------------------------------------------
    //  2) 白平衡：把纸面中位色拉回中性
    // ------------------------------------------------------------------

    private static void WhiteBalance(byte[] p, int w, int h)
    {
        var s = PaperStats(p, w, h);
        if (s.Count <= 0) return;
        float target = (s.MedianB + s.MedianG + s.MedianR) / 3f;
        float gb = Math.Clamp(target / MathF.Max(8f, s.MedianB), 0.72f, 1.40f);
        float gg = Math.Clamp(target / MathF.Max(8f, s.MedianG), 0.72f, 1.40f);
        float gr = Math.Clamp(target / MathF.Max(8f, s.MedianR), 0.72f, 1.40f);
        if (MathF.Abs(gb - 1f) < 0.01f && MathF.Abs(gg - 1f) < 0.01f
            && MathF.Abs(gr - 1f) < 0.01f) return;

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = (row + x) * 4;
                int b = p[i], g = p[i + 1], r = p[i + 2];
                int mx = Math.Max(b, Math.Max(g, r)), mn = Math.Min(b, Math.Min(g, r));
                float sat = (mx - mn) / 255f;
                float weight = 1f - SmoothStep(0.22f, 0.62f, sat);
                if (weight <= 0.001f) continue;
                p[i] = ClampByte(b + (b * gb - b) * weight);
                p[i + 1] = ClampByte(g + (g * gg - g) * weight);
                p[i + 2] = ClampByte(r + (r * gr - r) * weight);
            }
        });
    }

    // ------------------------------------------------------------------
    //  3) 纸白 + 色度去污
    // ------------------------------------------------------------------

    private static void PaperWhiteAndClean(byte[] p, int w, int h)
    {
        var s = PaperStats(p, w, h);
        if (s.Count <= 0) return;

        // 纸面中位亮度 → 242；留几级给锐化，避免纸面直接顶到 255 发死。
        float gain = Math.Clamp(242f / MathF.Max(1f, s.MedianY), 0.95f, 1.60f);
        if (gain > 1.005f)
        {
            Parallel.For(0, h, yy =>
            {
                int row = yy * w;
                for (int x = 0; x < w; x++)
                {
                    int i = (row + x) * 4;
                    int b = p[i], g = p[i + 1], r = p[i + 2];
                    int mx = Math.Max(b, Math.Max(g, r)), mn = Math.Min(b, Math.Min(g, r));
                    float sat = (mx - mn) / 255f;
                    float weight = 1f - SmoothStep(0.20f, 0.60f, sat);
                    if (weight <= 0.001f) continue;
                    float y = 0.299f * r + 0.587f * g + 0.114f * b;
                    if (y < 1f) continue;
                    float ny = y * (1f + (gain - 1f) * 0.92f * weight);
                    float k = ny / y;
                    p[i] = ClampByte(b * k); p[i + 1] = ClampByte(g * k); p[i + 2] = ClampByte(r * k);
                }
            });
        }

        // 残留的黄/绿/蓝灰：低饱和像素往亮度拉一点，红蓝笔迹保留。
        Parallel.For(0, h, yy =>
        {
            int row = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = (row + x) * 4;
                int b = p[i], g = p[i + 1], r = p[i + 2];
                int mx = Math.Max(b, Math.Max(g, r)), mn = Math.Min(b, Math.Min(g, r));
                int sat = mx - mn;
                if (sat >= 60) continue;
                float k = 0.34f * (1f - sat / 60f);
                float y = 0.299f * r + 0.587f * g + 0.114f * b;
                p[i] = ClampByte(b + (y - b) * k);
                p[i + 1] = ClampByte(g + (y - g) * k);
                p[i + 2] = ClampByte(r + (y - r) * k);
            }
        });
    }

    // ------------------------------------------------------------------
    //  4) 轻锐化：只在亮度上做，保持色相
    // ------------------------------------------------------------------

    private static void SharpenLuma(byte[] p, int w, int h)
    {
        int n = w * h;
        var y = new byte[n];
        Parallel.For(0, h, yy =>
        {
            int row = yy * w;
            for (int xx = 0; xx < w; xx++)
            {
                int i = row + xx, q = i * 4;
                y[i] = (byte)((77 * p[q + 2] + 150 * p[q + 1] + 29 * p[q]) >> 8);
            }
        });

        var blur = new byte[n];
        Parallel.For(0, h, yy =>
        {
            int y0 = Math.Max(0, yy - 1), y1 = Math.Min(h - 1, yy + 1);
            int row = yy * w;
            for (int xx = 0; xx < w; xx++)
            {
                int x0 = Math.Max(0, xx - 1), x1 = Math.Min(w - 1, xx + 1);
                int sum = y[y0 * w + x0] + 2 * y[y0 * w + xx] + y[y0 * w + x1]
                        + 2 * y[row + x0] + 4 * y[row + xx] + 2 * y[row + x1]
                        + y[y1 * w + x0] + 2 * y[y1 * w + xx] + y[y1 * w + x1];
                blur[row + xx] = (byte)(sum / 16);
            }
        });

        Parallel.For(0, h, yy =>
        {
            int row = yy * w;
            for (int xx = 0; xx < w; xx++)
            {
                int i = row + xx, q = i * 4;
                int b = p[q], g = p[q + 1], r = p[q + 2];
                int mx = Math.Max(b, Math.Max(g, r)), mn = Math.Min(b, Math.Min(g, r));
                float sat = (mx - mn) / 255f;
                float weight = 1f - SmoothStep(0.12f, 0.55f, sat);
                if (weight <= 0.001f) continue;
                int y0 = y[i], yb = blur[i];
                float ny = y0 + (y0 - yb) * 0.38f * weight;
                if (ny < 0f) ny = 0f;
                if (ny > 255f) ny = 255f;
                float k = y0 > 1 ? ny / y0 : 1f;
                p[q] = ClampByte(b * k); p[q + 1] = ClampByte(g * k); p[q + 2] = ClampByte(r * k);
            }
        });
    }

    // ------------------------------------------------------------------
    //  小工具
    // ------------------------------------------------------------------

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / MathF.Max(1e-5f, b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
}
