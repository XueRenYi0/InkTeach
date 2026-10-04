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
/// 支持的命令：M / L / H / V / C / Q / T / A / Z（含相对形式）——这不是偷懒，
/// 是**照着上游实际用到的命令写的**：生成脚本会统计 d 里出现过哪些命令，
/// 用到哪几个就支持哪几个（这一条从第一版起就是这规矩：先报统计，再加实现）。
///
/// **Q / T（二次贝塞尔）是 2026-09-26 补的**：那天要画 Material Symbols 的
/// `stylus_laser_pointer`（"笔射出一道光"那个激光笔图标），它的 d 里全是
/// `q` / `t`，而当时只认 M/L/H/V/C/Z ——**画出来是空的**（出图时三个格子全白，
/// 一眼就看见了）。补的时候按 SVG 规范来：`T`/`t` 是"平滑二次"，
/// 控制点 = 上一个控制点关于当前点的**镜像**；上一条不是二次曲线时控制点就取当前点
/// （退化成一个直线段，这也是规范里写的）。
///
/// **A / a（圆弧）是同日再补的**：同一天要落两张上游图（MDI 的 `selection`、
/// Lucide 的 `square-dashed-mouse-pointer`），它们的圆角都是 `a`（圆弧）——
/// 又是"默默画不出来"（圆弧那段直接丢掉，剩下几条直线看着像图标缺了一角）。
/// 按 SVG 规范 F.6.5 的"端点 → 圆心"参数化换算，再按 ≤90° 分段、
/// 每段用三次贝塞尔逼近（k = 4/3·tan(Δθ/4)，各家 SVG 渲染器的通行做法）。
///
/// 几何按 24×24 的原始坐标缓存，绘制时用 ctx 的变换缩放到目标尺寸——
/// 这样同一份几何可以在不同尺寸/DPI 下复用，不用重建。
///
/// **public**：界面层（`InkUi`）也要用它画图标。它是通用的路径解析工具，
/// 不碰文档模型、不碰工具状态，对界面开放不违反分层（界面照样不许碰文档）。
/// 引擎自己的图标表（`Icons.Paths.cs`）仍是 internal——那是引擎内部的事。
/// </summary>
public static class SvgPath
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
            // Q / T 要用：上一个二次曲线的控制点，以及"上一条命令是不是二次曲线"
            //（`T` 是"平滑二次"——控制点要拿上一条的控制点做镜像，见类注释）。
            Vector2 quadCtrl = default;
            bool prevWasQuad = false;

            while (i < tokens.Count)
            {
                int before = i;

                if (char.IsLetter(tokens[i].Value[0])) { cmd = tokens[i].Value[0]; i++; }
                char c = char.ToUpperInvariant(cmd);
                bool rel = char.IsLower(cmd);

                // 除了 Q / T，别的命令都会"打断"平滑二次的那条链（T 只能在 Q/T 之后用）。
                if (c is not ('Q' or 'T')) prevWasQuad = false;

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
                    case 'Q':
                    {
                        // 二次贝塞尔：只给一个控制点 ＋ 终点（起点是当前点）。
                        quadCtrl = Point(tokens, ref i, cur, rel);
                        var end = Point(tokens, ref i, cur, rel);
                        // Vortice 里这个结构体没有"两点版"的构造函数，只能对象初始化器填字段
                        sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = quadCtrl, Point2 = end });
                        cur = end;
                        prevWasQuad = true;
                        break;
                    }
                    case 'T':
                    {
                        // 平滑二次：控制点 = 上一条二次曲线的控制点关于**当前点**的镜像；
                        // 上一条不是二次曲线时取当前点（这时它退化成一条直线，规范如此）。
                        quadCtrl = prevWasQuad ? cur * 2f - quadCtrl : cur;
                        var end = Point(tokens, ref i, cur, rel);
                        sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = quadCtrl, Point2 = end });
                        cur = end;
                        prevWasQuad = true;
                        break;
                    }
                    case 'A':
                    {
                        // 圆弧：rx ry 旋转角 大弧标志 方向标志 x y（终点；x/y 才分相对/绝对）
                        float rx = Number(tokens, ref i);
                        float ry = Number(tokens, ref i);
                        float rot = Number(tokens, ref i);
                        bool largeArc = Number(tokens, ref i) != 0f;
                        bool sweep = Number(tokens, ref i) != 0f;
                        var end = Point(tokens, ref i, cur, rel);
                        AddArc(sink, cur, rx, ry, rot, largeArc, sweep, end);
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

    /// <summary>
    /// 把一段 SVG 圆弧（`A` / `a`）加进几何里。
    ///
    /// **照 SVG 规范 F.6.5「端点参数 → 圆心参数」那一套算**（这段没有"自己发明"的余地：
    /// 规范给了伪码，照抄即可）：先把椭圆半径按需要放大到能容纳两端点、求出圆心，
    /// 再算出起始角与扫过的角度，最后按 **≤90° 分段**、每段用**三次贝塞尔**逼近
    ///（控制点距 = k = 4/3·tan(Δθ/4)）——90° 一段的误差约 0.027%，图标尺寸下看不出来，
    /// 各家 SVG 渲染器都是这么干的。
    ///
    /// 三种退化按规范处理：两端点重合 → 整段不画；半径里有一个是 0 → 退化成直线；
    /// 半径太小装不下两端点 → 两个半径一起放大到刚好装下。
    /// </summary>
    private static void AddArc(ID2D1GeometrySink sink, Vector2 p0, float rx, float ry,
                               float rotDeg, bool largeArc, bool sweep, Vector2 p1)
    {
        if (p0 == p1) return;                                  // 规范：端点重合 → 这段省略
        rx = MathF.Abs(rx); ry = MathF.Abs(ry);
        if (rx < 1e-6f || ry < 1e-6f) { sink.AddLine(p1); return; }   // 规范：退化成直线

        float phi = rotDeg * MathF.PI / 180f;
        float cosPhi = MathF.Cos(phi), sinPhi = MathF.Sin(phi);

        // ① 把两端点搬到"椭圆自己的坐标系"里（先平移到中点、再反向旋转）
        float dx = (p0.X - p1.X) * 0.5f, dy = (p0.Y - p1.Y) * 0.5f;
        float x1 = cosPhi * dx + sinPhi * dy;
        float y1 = -sinPhi * dx + cosPhi * dy;

        // ② 半径不够大就一起放大（规范 F.6.6）
        float lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1f)
        {
            float s = MathF.Sqrt(lambda);
            rx *= s; ry *= s;
        }

        // ③ 求圆心（规范 F.6.5.2）——注意两个标志的取值决定走哪一边
        float rx2 = rx * rx, ry2 = ry * ry, x12 = x1 * x1, y12 = y1 * y1;
        float den = rx2 * y12 + ry2 * x12;
        float num = rx2 * ry2 - den;
        float coef = (largeArc == sweep ? -1f : 1f) * MathF.Sqrt(MathF.Max(0f, num / den));
        float cxp = coef * (rx * y1 / ry);
        float cyp = coef * (-ry * x1 / rx);
        float cx = cosPhi * cxp - sinPhi * cyp + (p0.X + p1.X) * 0.5f;
        float cy = sinPhi * cxp + cosPhi * cyp + (p0.Y + p1.Y) * 0.5f;

        // ④ 起始角与扫过的角（扫到哪边由 sweep 决定，符号不对就补一整圈）
        float theta1 = MathF.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
        float theta2 = MathF.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);
        float delta = theta2 - theta1;
        if (!sweep && delta > 0f) delta -= MathF.Tau;
        if (sweep && delta < 0f) delta += MathF.Tau;

        // ⑤ 分段 → 每段一条三次贝塞尔。E(θ) 是椭圆上的点，E'(θ) 是它的切向（用来定两个控制点）
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI * 0.5f)));
        float step = delta / steps;
        float k = 4f / 3f * MathF.Tan(step / 4f);

        Vector2 At(float th) => new(
            cx + rx * cosPhi * MathF.Cos(th) - ry * sinPhi * MathF.Sin(th),
            cy + rx * sinPhi * MathF.Cos(th) + ry * cosPhi * MathF.Sin(th));
        Vector2 Dir(float th) => new(
            -rx * cosPhi * MathF.Sin(th) - ry * sinPhi * MathF.Cos(th),
            -rx * sinPhi * MathF.Sin(th) + ry * cosPhi * MathF.Cos(th));

        for (int s = 0; s < steps; s++)
        {
            float a0 = theta1 + step * s;
            float a1 = a0 + step;
            var c1 = At(a0) + Dir(a0) * k;
            var c2 = At(a1) - Dir(a1) * k;
            sink.AddBezier(new BezierSegment(c1, c2, At(a1)));
        }
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
