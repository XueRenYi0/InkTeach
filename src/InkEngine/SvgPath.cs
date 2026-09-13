using System.Numerics;
using System.Text.RegularExpressions;
using Vortice.Direct2D1;

namespace InkEngine;

/// <summary>
/// 把 SVG 的 path 数据（d 字符串）解析成 Direct2D 几何，并按 d 缓存。
///
/// 存在的理由：图标不该自己画。圆角、光学校正、笔画粗细的一致性，
/// 专业图标库都调过；手画出来的通常一看就别扭。所以路径数据从
/// Fluent UI System Icons 抓（见 tools/gen-icons.ps1），这里只负责解析。
///
/// 支持的命令只有 M / L / H / V / C / Z（含相对形式）——这不是偷懒，
/// 是**照着上游实际用到的命令写的**：生成脚本会统计 d 里出现过哪些命令，
/// 目前只有这六个。真需要弧线（A）时，脚本的统计会先报出来，再加也不迟。
///
/// 几何按 24×24 的原始坐标缓存，绘制时用 ctx 的变换缩放到目标尺寸——
/// 这样同一份几何可以在不同尺寸/DPI 下复用，不用重建。
/// </summary>
internal static class SvgPath
{
    private static readonly Dictionary<string, ID2D1PathGeometry> Cache = new();

    /// <summary>取图标在 24×24 坐标下的几何（带缓存）。</summary>
    public static ID2D1PathGeometry Get(string d)
    {
        if (string.IsNullOrEmpty(d)) return null;
        if (Cache.TryGetValue(d, out var cached)) return cached;

        var geo = Build(d);
        Cache[d] = geo;
        return geo;
    }

    /// <summary>设备销毁前调用：几何是设备相关的，不释放会随每次 ResetDevice 泄漏。</summary>
    public static void Clear()
    {
        foreach (var g in Cache.Values) g?.Dispose();
        Cache.Clear();
    }

    private static ID2D1PathGeometry Build(string d)
    {
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            // Winding：图标里常有自交和"挖洞"（比如删除图标的桶身），
            // 用 Winding 才会按设计意图填充，Alternate 会把洞挖穿成别的样子。
            sink.SetFillMode(FillMode.Winding);

            var tokens = Regex.Matches(d, @"[A-Za-z]|-?\d*\.?\d+(?:[eE][-+]?\d+)?");
            int i = 0;
            char cmd = 'M';
            Vector2 cur = default, sub = default;
            bool figureOpen = false;

            while (i < tokens.Count)
            {
                int before = i;

                if (char.IsLetter(tokens[i].Value[0])) { cmd = tokens[i].Value[0]; i++; }
                char c = char.ToUpperInvariant(cmd);
                bool rel = char.IsLower(cmd);

                switch (c)
                {
                    case 'M':
                    {
                        var pt = Point(tokens, ref i, cur, rel);
                        if (figureOpen) sink.EndFigure(FigureEnd.Closed);
                        sink.BeginFigure(pt, FigureBegin.Filled);
                        figureOpen = true;
                        cur = sub = pt;
                        // 一个 M 后面跟多组坐标时，后面的都按 L 处理（SVG 的规定）。
                        cmd = rel ? 'l' : 'L';
                        break;
                    }
                    case 'L':
                        cur = Point(tokens, ref i, cur, rel);
                        sink.AddLine(cur);
                        break;
                    case 'H':
                    {
                        float x = Number(tokens, ref i);
                        cur = new Vector2(rel ? cur.X + x : x, cur.Y);
                        sink.AddLine(cur);
                        break;
                    }
                    case 'V':
                    {
                        float y = Number(tokens, ref i);
                        cur = new Vector2(cur.X, rel ? cur.Y + y : y);
                        sink.AddLine(cur);
                        break;
                    }
                    case 'C':
                    {
                        var c1 = Point(tokens, ref i, cur, rel);
                        var c2 = Point(tokens, ref i, cur, rel);
                        var end = Point(tokens, ref i, cur, rel);
                        // SVG 的 C 只给控制点和终点，起点是当前点；
                        // Direct2D 这边收的 BezierSegment 也是这三个点。
                        sink.AddBezier(new BezierSegment(c1, c2, end));
                        cur = end;
                        break;
                    }
                    case 'Z':
                        if (figureOpen) { sink.EndFigure(FigureEnd.Closed); figureOpen = false; }
                        cur = sub;
                        break;
                }

                // 防呆：任何一步没有推进（比如 Z 后面跟了个孤立的数字），
                // 就跳过这个记号。没有这道闸，一个坏字符就能让循环转不出来。
                if (i == before) i++;
            }

            if (figureOpen) sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

    private static float Number(MatchCollection t, ref int i)
    {
        if (i >= t.Count) return 0f;
        return float.TryParse(t[i++].Value, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }

    private static Vector2 Point(MatchCollection t, ref int i, Vector2 cur, bool relative)
    {
        float x = Number(t, ref i);
        float y = Number(t, ref i);
        return relative ? new Vector2(cur.X + x, cur.Y + y) : new Vector2(x, y);
    }
}
