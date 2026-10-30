using System.Runtime.InteropServices;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 公式渲染 spike：CSharpMath（纯 C# + SkiaSharp，MIT）把 LaTeX 画成透明位图。
///
/// 目前用于**独立公式行**（整行 `$$...$$` / `\[...\]`）；行内公式的 run 排版后续再接。
/// 题卡/文本框只拿"图片 + 宽高 + 基线"，不关心底层是 CSharpMath、MicroTeX 还是 KaTeX。
/// </summary>
internal static class MathRenderer
{
    internal sealed class Rendered
    {
        public byte[] Png;
        public int Width;
        public int Height;
        public float Scale;
        public float LogicalWidth => Width / MathF.Max(0.5f, Scale);
        public float LogicalHeight => Height / MathF.Max(0.5f, Scale);
    }

    internal sealed class RenderedImage
    {
        public ImageData Image;
        public float Scale;
        public float Ascent;
        public float Descent;
        public float LogicalWidth => Image == null ? 0f : Image.Width / MathF.Max(0.5f, Scale);
        public float LogicalHeight => Image == null ? 0f : Image.Height / MathF.Max(0.5f, Scale);
        public float LogicalAscent => Ascent / MathF.Max(0.5f, Scale);
        public float LogicalDescent => Descent / MathF.Max(0.5f, Scale);
    }

    private static SKBitmap RenderBitmap(string latex, float fontSizePx, Color4 color, float scale,
                                         out int width, out int height,
                                         out float ascent, out float descent)
    {
        width = height = 0;
        ascent = descent = 0f;
        if (string.IsNullOrWhiteSpace(latex)) return null;
        latex = LatexCompat.Normalize(latex);
        if (string.IsNullOrWhiteSpace(latex)) return null;
        try
        {
            var painter = new MathPainter
            {
                LaTeX = latex,
                DisplayErrorInline = false,
                FontSize = MathF.Max(6f, fontSizePx * Math.Clamp(scale, 1f, 4f)),
                TextColor = new SKColor(
                    (byte)Math.Clamp((int)MathF.Round(color.R * 255f), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(color.G * 255f), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(color.B * 255f), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(color.A * 255f), 0, 255)),
            };
            var measured = painter.Measure(1000f);
            if (!string.IsNullOrEmpty(painter.ErrorMessage)) return null;
            if (measured.Width < 1f || measured.Height < 1f) return null;
            width = Math.Max(1, (int)MathF.Ceiling(measured.Width) + 8);
            height = Math.Max(1, (int)MathF.Ceiling(measured.Height) + 8);
            ascent = MathF.Max(0f, -measured.Y);
            descent = MathF.Max(0f, measured.Height - ascent);
            var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                painter.Draw(canvas, CSharpMath.Rendering.FrontEnd.TextAlignment.Left);
            }
            return bmp;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[math] CSharpMath 失败：" + ex.Message);
            return null;
        }
    }

    public static Rendered RenderPng(string latex, float fontSizePx, Color4 color,
                                     float scale = 2f)
    {
        using var bmp = RenderBitmap(latex, fontSizePx, color, scale, out int w, out int h, out _, out _);
        if (bmp == null) return null;
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data == null) return null;
        return new Rendered { Png = data.ToArray(), Width = w, Height = h, Scale = scale };
    }

    public static RenderedImage RenderImage(string latex, float fontSizePx, Color4 color,
                                            float scale = 2f)
    {
        latex = LatexCompat.Normalize(latex);
        if (string.IsNullOrWhiteSpace(latex)) return null;

        var key = new CacheKey(latex,
            Math.Max(6, (int)MathF.Round(fontSizePx * 10f)),
            (uint)(color.R * 255f) | ((uint)(color.G * 255f) << 8)
            | ((uint)(color.B * 255f) << 16) | ((uint)(color.A * 255f) << 24),
            Math.Max(1, (int)MathF.Round(scale * 10f)));
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }

        using var bmp = RenderBitmap(latex, fontSizePx, color, scale, out int w, out int h,
                                     out float asc, out float desc);
        if (bmp == null) return null;
        var bgra = new byte[w * h * 4];
        IntPtr pixels = bmp.GetPixels();
        if (pixels == IntPtr.Zero) return null;
        int row = w * 4;
        for (int y = 0; y < h; y++)
            Marshal.Copy(IntPtr.Add(pixels, y * bmp.RowBytes), bgra, y * row, row);
        var img = ImageData.AdoptPremultiplied(w, h, bgra);
        var result = new RenderedImage { Image = img, Scale = scale, Ascent = asc, Descent = desc };
        lock (_cache)
        {
            if (_cache.Count >= CacheLimit) _cache.Clear();
            _cache[key] = result;
        }
        return result;
    }

    private readonly record struct CacheKey(string Latex, int Size10, uint Color, int Scale10);
    private const int CacheLimit = 256;
    private static readonly Dictionary<CacheKey, RenderedImage> _cache = new();

    /// <summary>
    /// 排版层每次改宽/改字号都会重排一遍；公式位图按 `(LaTeX, 字号, 颜色, 倍率)` 缓存，
    /// 拖手柄时不再反复走 CSharpMath + Skia。缓存满了整表清空（数量很小，够用）。
    /// </summary>
    internal static int CacheCount
    {
        get { lock (_cache) return _cache.Count; }
    }

    /// <summary>自检/摆样用：渲染一组典型公式，输出 PNG，返回成功数。</summary>
    internal static int RenderSamples(string dir, out int total)
    {
        Directory.CreateDirectory(dir);
        var cases = new (string Name, string Latex)[]
        {
            ("fraction", @"\frac{\sqrt{2}}{3}"),
            ("quadratic", @"x=\frac{-b\pm\sqrt{b^2-4ac}}{2a}"),
            ("scripts", @"a_1^2+a_2^2+\cdots+a_n^2"),
            ("integral", @"\int_0^1 x^2\,dx=\frac{1}{3}"),
            ("matrix", @"\begin{pmatrix}1&2\\3&4\end{pmatrix}"),
            ("trig", @"\sin^2 x+\cos^2 x=1"),
            ("set", @"\{a_n\}"),
            ("spacing", @"a_1=2,\qquad a_{n+1}=3a_n+2\quad(n\ge 1)"),
            ("boxed", @"\boxed{a_n=3^n-1}"),
            ("cases", @"\begin{cases}a_{n+1}=3a_n+2\\a_1=2\end{cases}"),
            ("text", @"\text{当 }x>0\text{ 时}"),
            ("dfrac", @"\dfrac{1}{2}"),
            ("arrow", @"\overrightarrow{AB}"),
            ("logic", @"\because a>b,\ \therefore a-b>0"),
            ("sum", @"\sum_{k=1}^{n}k=\frac{n(n+1)}{2}"),
            ("lim", @"\lim_{n\to\infty}\frac{1}{n}=0"),
            ("vec", @"\vec{AB}"),
            ("overline", @"\overline{AB}"),
            ("operatorname", @"\operatorname{sin}x"),
            ("aligned", @"\begin{aligned}a&=b+c\\&=d\end{aligned}"),
            ("align", @"\begin{align}a&=b+c\\&=d\end{align}"),
            ("array", @"\left\{\begin{array}{l}a=1\\b=2\end{array}\right."),
            ("displaystyle", @"\displaystyle\sum_{k=1}^n k"),
            ("mathbb", @"x\in\mathbb{R}"),
            ("mathbf", @"\mathbf{AB}"),
            ("hspace", @"a\hspace{1em}b"),
            ("tag", @"x=1\tag{1}"),
            ("unicode", @"x?1,\ y?2,\ a?b"),
            ("limits", @"\sum\limits_{k=1}^n k"),
            ("eqenv", @"\begin{equation}x=1\end{equation}"),
            ("rowskip", @"\begin{cases}a=1\\[2pt]b=2\end{cases}"),
            ("textcolor", @"\textcolor{red}{x}"),
            ("cancel", @"\cancel{x}"),
            ("xarrow", @"A\xrightarrow{f}B"),
            ("leftright", @"\left\{\begin{array}{l}a=1\\b=2\end{array}\right."),
        };
        int ok = 0;
        total = cases.Length;
        foreach (var (name, latex) in cases)
        {
            var r = RenderPng(latex, 18f, new Color4(0.12f, 0.14f, 0.18f, 1f), 2f);
            if (r == null) { Console.WriteLine($"  FAIL  {name}"); continue; }
            File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), r.Png);
            Console.WriteLine($"  PASS  {name}  {r.Width}×{r.Height}  {r.Png.Length / 1024} KB");
            ok++;
        }
        return ok;
    }
}
