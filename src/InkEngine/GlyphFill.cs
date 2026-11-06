using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 字形**填充**渲染：SVG 轮廓（M/L/Q/C/Z）→ D2D PathGeometry → FillGeometry。
///
/// 和墨迹描边的区别：这里是「填充 + even-odd 挖孔」，任意缩放都一模一样，
/// 也是 <see cref="StrokeKind.Glyph"/> 将来的渲染路径。
/// </summary>
public static class GlyphFill
{
    /// <summary>把 SVG path 塞进几何 sink。</summary>
    public static void Parse(string svg, ID2D1GeometrySink sink)
    {
        sink.SetFillMode(FillMode.Alternate);      // even-odd：内外轮廓交替 → 自动挖孔
        // SkiaSharp 的输出里命令和数字是**粘在一起**的（如 "-110.938Q107.813"），
        // 不能按空格切，必须逐字符分出「命令字母」和「数字」。
        var t = new List<string>();
        for (int p = 0; p < svg.Length;)
        {
            char c = svg[p];
            if (char.IsLetter(c)) { t.Add(c.ToString()); p++; }
            else if (c == '-' || c == '+' || c == '.' || char.IsDigit(c))
            {
                int s = p++;
                while (p < svg.Length && (char.IsDigit(svg[p]) || svg[p] == '.')) p++;
                t.Add(svg.Substring(s, p - s));
            }
            else p++;
        }
        int i = 0;
        float Num() => float.Parse(t[i++], CultureInfo.InvariantCulture);

        System.Numerics.Vector2 cur = default;
        while (i < t.Count)
        {
            string c = t[i++];
            switch (c)
            {
                case "M":
                {
                    cur = new System.Numerics.Vector2(Num(), Num());
                    sink.BeginFigure(cur, FigureBegin.Filled);
                    break;
                }
                case "L":
                {
                    cur = new System.Numerics.Vector2(Num(), Num());
                    sink.AddLine(cur);
                    break;
                }
                case "H":
                {
                    cur = new System.Numerics.Vector2(Num(), cur.Y);
                    sink.AddLine(cur);
                    break;
                }
                case "V":
                {
                    cur = new System.Numerics.Vector2(cur.X, Num());
                    sink.AddLine(cur);
                    break;
                }
                case "Q":
                {
                    var q = new System.Numerics.Vector2(Num(), Num());
                    var p = new System.Numerics.Vector2(Num(), Num());
                    sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = q, Point2 = p });
                    cur = p;
                    break;
                }
                case "C":
                {
                    var c1 = new System.Numerics.Vector2(Num(), Num());
                    var c2 = new System.Numerics.Vector2(Num(), Num());
                    var p = new System.Numerics.Vector2(Num(), Num());
                    sink.AddBezier(new BezierSegment(c1, c2, p));
                    cur = p;
                    break;
                }
                case "Z":
                case "z":
                    sink.EndFigure(FigureEnd.Closed);
                    break;
                default:
                    break;
            }
        }
        sink.Close();
    }

    /// <summary>渲染一段字形轮廓到 BGRA（透明底）。</summary>
    public static byte[] Render(ID2D1DeviceContext ctx, string svg, Color4 color,
                                out int w, out int h, float pad = 8f)
    {
        w = h = 0;
        if (ctx == null || string.IsNullOrEmpty(svg)) return null;

        using var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open()) Parse(svg, sink);

        var b = geo.GetBounds();
        float minX = b.Left - pad, minY = b.Top - pad;
        w = Math.Max(1, (int)MathF.Ceiling(b.Right - b.Left + pad * 2));
        h = Math.Max(1, (int)MathF.Ceiling(b.Bottom - b.Top + pad * 2));

        var pf = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        ID2D1Bitmap1 target = null, cpu = null;
        try
        {
            target = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            cpu = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            ctx.Target = target;
            ctx.BeginDraw();
            ctx.Clear(new Color4(0f, 0f, 0f, 0f));
            ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-minX, -minY);

            using var brush = ctx.CreateSolidColorBrush(color);
            ctx.FillGeometry(geo, brush);

            ctx.Transform = System.Numerics.Matrix3x2.Identity;
            var hr = ctx.EndDraw();
            ctx.Target = null;
            if (hr.Failure) return null;

            cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
            var map = cpu.Map(MapOptions.Read);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(map.Bits, (int)(y * map.Pitch)), buf, y * w * 4, w * 4);
            cpu.Unmap();
            return buf;
        }
        finally
        {
            target?.Dispose();
            cpu?.Dispose();
        }
    }
}
