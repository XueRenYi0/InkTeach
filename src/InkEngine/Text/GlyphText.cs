using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 一行文字 → 一组 <see cref="StrokeKind.Glyph"/> 轮廓。
///
/// 这是「手写优化」真正落地时的排版层：
///   · 字体由调用方给（上线用 OFL 的志莽行书 / 漓雨手书等，测试可换任意本地字体）；
///   · 字号按原行框高度起算，太宽就**缩字号重排**，不横向拉伸；
///   · 中文、英文、符号逐字回退（主字体缺字时找系统字体），保证“每个学生都认识”；
///   · 整行压成**一个** Glyph 对象，保留原行的颜色、位置和撤销粒度。
///
/// 排版口径参照 `tools/ai-bench/mixed_replace.py` 的已验证原型：
/// 字号 = 行高 × 1.18；行宽不够时按比例缩字号；最后按墨迹包围盒左对齐 + 垂直居中。
/// </summary>
public static class GlyphText
{
    /// <summary>行高 → 字号的经验系数（2026-11-06 楷体调到 1.05，避免压上下行）。</summary>
    public const float SizePerLineHeight = 1.05f;

    /// <summary>
    /// 安装包里的默认中文手写字体（OFL 钟齐志莽行书，4MB）。
    /// 2026-11-11 用户拍板：不再内嵌 25MB 的漓雨手书，只留这一款；
    /// 老安装目录里如果还有漓雨，就继续用（升级覆盖后自然消失）。
    /// </summary>
    public static string CjkFontPath
    {
        get
        {
            string p = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "ZhiMangXing.ttf");
            if (File.Exists(p)) return p;
            // 老安装包 / 调试目录只剩漓雨时，退回它。
            p = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "LiyuShoushu.ttf");
            return File.Exists(p) ? p : null;
        }
    }

    /// <summary>兼容旧调用：默认中文手写字体。</summary>
    public static string BundledFontPath => CjkFontPath;

    /// <summary>英文用系统 Segoe UI（正常印刷体），中文用楷体，符号用 Segoe UI Symbol。</summary>
    public sealed class Fonts
    {
        public string Cjk;
        public string Latin;
        public string Symbol;

        public static Fonts Default => new()
        {
            Cjk = CjkFontPath ?? @"C:\Windows\Fonts\simkai.ttf",
            Latin = File.Exists(@"C:\Windows\Fonts\segoeui.ttf")
                ? @"C:\Windows\Fonts\segoeui.ttf" : @"C:\Windows\Fonts\arial.ttf",
            Symbol = File.Exists(@"C:\Windows\Fonts\seguisym.ttf")
                ? @"C:\Windows\Fonts\seguisym.ttf" : @"C:\Windows\Fonts\segoeui.ttf",
        };
    }

    /// <summary>主字体缺字时的系统回退。顺序：雅黑（中文）→ 符号字体。</summary>
    private static readonly string[] FallbackFonts =
    {
        @"C:\Windows\Fonts\msyh.ttc",
        @"C:\Windows\Fonts\msyh.ttf",
        @"C:\Windows\Fonts\simsun.ttc",
        @"C:\Windows\Fonts\seguisym.ttf",
    };

    private static readonly Dictionary<string, SKTypeface> TypefaceCache = new();

    private static SKTypeface Load(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (TypefaceCache.TryGetValue(path, out var tf)) return tf;
        if (!File.Exists(path)) return null;
        tf = SKTypeface.FromFile(path);
        if (tf != null) TypefaceCache[path] = tf;
        return tf;
    }

    /// <summary>这一行实际用到的字体（按字回退后）。</summary>
    public sealed class LineLayout
    {
        public List<List<System.Numerics.Vector2>> Contours = new();
        public float Width;
        public float Height;
        public float MinX;
        public float MinY;
        /// <summary>排完这一串之后的笔位置（含空白 / 字宽，不是墨迹包围盒）。</summary>
        public float Advance;
        /// <summary>主字体缺的字（调用方可以提示 / 记日志）。</summary>
        public string Missing = "";
    }

    /// <summary>按字号、基线排一行（单一字体，兼容旧调用）。</summary>
    public static LineLayout Layout(string fontPath, string text, float fontSize,
                                    float startX, float baselineY)
        => Layout(new Fonts { Cjk = fontPath, Latin = fontPath, Symbol = fontPath },
                  text, fontSize, startX, baselineY);

    /// <summary>
    /// 按字号 <paramref name="fontSize"/>、基线 <paramref name="baselineY"/> 排一行。
    /// 中文走楷体、ASCII 走正常拉丁字体、符号走符号字体；缺字再逐级回退。
    /// </summary>
    public static LineLayout Layout(Fonts fonts, string text, float fontSize,
                                    float startX, float baselineY)
    {
        var result = new LineLayout();
        if (string.IsNullOrEmpty(text) || fontSize <= 0) return result;
        if (fonts == null) fonts = Fonts.Default;

        var cjk = Load(fonts.Cjk);
        var latin = Load(fonts.Latin) ?? cjk;
        var symbol = Load(fonts.Symbol) ?? cjk;
        if (cjk == null) cjk = latin ?? symbol;
        if (cjk == null) return result;

        var fallbacks = new List<SKTypeface>();
        foreach (var f in FallbackFonts)
        {
            var tf = Load(f);
            if (tf != null && !fallbacks.Contains(tf)) fallbacks.Add(tf);
        }
        foreach (var tf in new[] { cjk, latin, symbol })
            if (tf != null && !fallbacks.Contains(tf)) fallbacks.Add(tf);

        var paints = new Dictionary<SKTypeface, SKPaint>();
        SKPaint PaintOf(SKTypeface tf)
        {
            if (paints.TryGetValue(tf, out var p)) return p;
            p = new SKPaint
            {
                Typeface = tf,
                TextSize = fontSize,
                IsAntialias = false,
                SubpixelText = false,
            };
            paints[tf] = p;
            return p;
        }

        float x = startX;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        var missing = new System.Text.StringBuilder();

        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            string ch = char.ConvertFromUtf32(cp);
            i += ch.Length;

            // 空格只推进，不产生轮廓。
            if (char.IsWhiteSpace(ch, 0))
            {
                x += PaintOf(latin).MeasureText(ch);
                continue;
            }

            SKTypeface preferred = IsCjk(cp) ? cjk : cp < 128 ? latin : symbol;
            SKTypeface tf = preferred != null && preferred.ContainsGlyph(cp) ? preferred : null;
            if (tf == null)
            {
                foreach (var fb in fallbacks)
                    if (fb.ContainsGlyph(cp)) { tf = fb; break; }
            }
            if (tf == null)
            {
                tf = cjk;
                missing.Append(ch);
            }

            var paint = PaintOf(tf);
            float advance = paint.MeasureText(ch);
            using var path = paint.GetTextPath(ch, x, baselineY);
            if (path != null && !path.IsEmpty)
            {
                foreach (var contour in GlyphFill.Flatten(path.ToSvgPathData()))
                {
                    if (contour.Count < 3) continue;
                    result.Contours.Add(contour);
                    foreach (var pt in contour)
                    {
                        if (pt.X < minX) minX = pt.X;
                        if (pt.X > maxX) maxX = pt.X;
                        if (pt.Y < minY) minY = pt.Y;
                        if (pt.Y > maxY) maxY = pt.Y;
                    }
                }
            }
            x += advance;
        }

        foreach (var p in paints.Values) p.Dispose();

        if (minX <= maxX)
        {
            result.MinX = minX;
            result.MinY = minY;
            result.Width = maxX - minX;
            result.Height = maxY - minY;
        }
        result.Advance = x - startX;
        result.Missing = missing.ToString();
        return result;
    }

    private static bool IsCjk(int cp)
        => cp >= 0x2E80 || (cp >= 0x3000 && cp <= 0x303F) || (cp >= 0xFF00 && cp <= 0xFFEF);

    /// <summary>
    /// 把一行字排进原行框 <paramref name="box"/>：
    /// 高度按行框起字号；宽度不够时缩字号重排；最后左对齐、垂直居中。
    /// </summary>
    internal static Stroke BuildLine(string fontPath, string text, RectF box, Color4 color,
                                     float sizeOverride = 0f, bool allowShrink = true)
    {
        if (box.IsEmpty || string.IsNullOrEmpty(text)) return null;

        float boxW = box.MaxX - box.MinX, boxH = box.MaxY - box.MinY;
        var fonts = Fonts.Default;
        if (!string.IsNullOrEmpty(fontPath)) fonts.Cjk = fontPath;
        float size = sizeOverride > 0f ? sizeOverride : MathF.Max(4f, boxH * SizePerLineHeight);
        LineLayout layout = null;

        // 最多缩两轮：第一轮按宽度，第二轮防极端窄框。
        for (int pass = 0; pass < 3; pass++)
        {
            layout = Layout(fonts, text, size, 0f, 0f);
            if (layout.Contours.Count == 0) return null;
            if (layout.Width <= boxW * 1.02f) break;
            if (!allowShrink) break;
            size *= MathF.Max(0.05f, boxW / layout.Width * 0.99f);
            if (size < 3f) break;
        }
        if (layout == null || layout.Contours.Count == 0) return null;

        // 宽度已经收进来了；竖向明显超出时再收一点，避免压到上下行。
        if (allowShrink && layout.Height > boxH * 1.02f)
        {
            size *= boxH / layout.Height * 0.99f;
            layout = Layout(fonts, text, size, 0f, 0f);
            if (layout.Contours.Count == 0) return null;
        }

        float dx = box.MinX - layout.MinX;
        float dy = box.MinY + (boxH - layout.Height) * 0.5f - layout.MinY;

        var s = new Stroke { Kind = StrokeKind.Glyph, Color = color, Width = 1f };
        foreach (var contour in layout.Contours)
        {
            bool first = true;
            foreach (var p in contour)
            {
                s.Points.Add(new InkPoint
                {
                    X = p.X + dx,
                    Y = p.Y + dy,
                    P = first ? -1f : 1f,
                    T = 0,
                });
                first = false;
            }
        }
        s.InvalidateMetrics();
        s.RecomputeBounds();
        return s;
    }

    /// <summary>测试用：一行文字按给定字高排成轮廓后，墨迹包围盒。</summary>
    public static RectF Measure(string fontPath, string text, float size)
    {
        var l = Layout(fontPath, text, size, 0f, 0f);
        if (l.Contours.Count == 0) return default;
        return new RectF { MinX = l.MinX, MinY = l.MinY, MaxX = l.MinX + l.Width, MaxY = l.MinY + l.Height };
    }
}
