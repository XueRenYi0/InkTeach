using System.Numerics;

namespace InkEngine;

/// <summary>
/// 橡皮擦的**算法**（不带状态、不碰界面、不碰撤销栈——调用时机由文档决定）。
///
/// 两个橡皮擦的分工是这一层定义的，也是这一层唯一需要记住的东西：
///
/// | 橡皮 | 擦什么 | 为什么 |
/// |---|---|---|
/// | 笔记橡皮（<see cref="Tool.Eraser"/>）| **整笔**：碰到哪条删哪条 | 一个字由很多笔画组成，"擦掉其中一横"是最常见的手势。整笔删除不会把字的其它部分咬掉一块，所以它是**精准**的那一个。 |
/// | 面积橡皮（<see cref="Tool.AreaEraser"/>）| **部分**：矩形范围内的墨被切掉 | "这一块擦干净"要的是局部，不是整笔。切出来的碎片仍是普通笔画，可以继续选中、移动、撤销。 |
///
/// 两个刻意的取舍写在下面的代码注释里：
///   · 精确擦除一次采样**只擦最上面的一条**（交叉处不连坐）；
///   · 面积擦除对自由笔迹**切段**，对图形/图像**整对象**（图形的"半个矩形"
///     没有意义，硬切只会留下一条开口的折线）。
/// </summary>
internal static class EraserOps
{
    // =====================================================================
    //  笔记橡皮：整笔、精准
    // =====================================================================

    /// <summary>
    /// 这一笔的真实墨迹有没有伸到 (x,y) 半径 radius 之内。
    ///
    /// 用**真实墨迹**判定，不是包围盒：包围盒会让"点在笔画旁边一大截"也算擦掉，
    /// 精准就无从谈起（一个字的包围盒几乎等于整个字）。
    ///
    /// 谁在谁上面不在这里决定——那需要知道文档里的顺序，由文档那一层用
    /// 空间索引的 stamp 反向扫一遍（见 <see cref="InkDocument.EraseStrokeAt"/>），
    /// 结果一样但一次对象分配都没有。
    /// </summary>
    public static bool HitsPoint(Stroke s, float x, float y, float radius)
    {
        if (s.IsShape || s.Kind == StrokeKind.Image)
            return s.HitTestExact(x, y, radius);

        // 自由笔迹：带子画在中心线两侧，所以"点到中心线的距离"已经等价于精确判定。
        float reach = radius + s.Width * Stroke.MaxWidthFactor * 0.5f;
        return s.DistanceToCanvas(x, y) <= reach;
    }

    // =====================================================================
    //  面积橡皮：按矩形切
    // =====================================================================

    /// <summary>
    /// 图形有没有被这个矩形碰到。**判的是轮廓，不是包围盒**：
    /// 一个潦草的大圆，包围盒能圈住半个屏幕，按包围盒判会"擦一下就把圆擦没了"。
    ///
    /// 反过来，矩形整个落在圆内部时**不算碰到**——圆是一条线，不是一块饼。
    /// 这与用户直觉一致：面积橡皮压在圆里面，圆不该消失。
    /// </summary>
    public static bool ShapeTouchesRect(Stroke s, in RectF rect)
    {
        if (!s.WorldBounds.Intersects(rect)) return false;

        var outline = LocalOutline(s);          // 局部坐标
        for (int i = 0; i + 1 < outline.Count; i++)
        {
            var a = Vector2.Transform(outline[i], s.Transform);
            var b = Vector2.Transform(outline[i + 1], s.Transform);
            if (SegmentHitsRect(a, b, rect)) return true;
        }
        return false;
    }

    /// <summary>图像被矩形压住了吗（图像是有面积的，按面积判）。</summary>
    public static bool ImageOverlapsRect(Stroke s, in RectF rect)
        => s.WorldBounds.Intersects(rect);

    /// <summary>
    /// 图形的轮廓折线（**局部坐标**）。
    ///
    /// 圆和椭圆用 24 段折线逼近：24 段在投影尺寸下与真椭圆没有可见差别，
    /// 而它让"用同一套线段求交"成立——不必为曲线单独写一套求交。
    /// </summary>
    private static List<Vector2> LocalOutline(Stroke s)
    {
        var list = new List<Vector2>();
        switch (s.Kind)
        {
            case StrokeKind.Line or StrokeKind.Arrow:
            {
                var (a, b) = ShapeGeometry.Endpoints(s);
                list.Add(a); list.Add(b);
                break;
            }
            case StrokeKind.Rectangle or StrokeKind.Triangle:
            {
                var v = ShapeGeometry.Vertices(s);
                for (int i = 0; i < v.Length; i++) list.Add(v[i]);
                list.Add(v[0]);                          // 闭合
                break;
            }
            case StrokeKind.Parallelogram:
            {
                // 顶点顺序：A → B → C → D（D = A + C − B，算出来的第四个）→ 回到 A。
                // 顺序错了会让"求交"用的折线自交，判定结果就不可信了。
                var v = ShapeGeometry.Vertices(s);
                list.Add(v[0]); list.Add(v[1]);
                list.Add(v[2]);
                list.Add(v[0] + v[2] - v[1]);
                list.Add(v[0]);
                break;
            }
            case StrokeKind.Ellipse or StrokeKind.Circle:
            {
                var (c, rx, ry) = EllipseParams(s);
                const int n = 24;
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n * MathF.Tau;
                    list.Add(new Vector2(c.X + rx * MathF.Cos(t), c.Y + ry * MathF.Sin(t)));
                }
                break;
            }
        }
        return list;
    }

    /// <summary>椭圆 / 圆的中心与半径（局部坐标）。</summary>
    private static (Vector2 c, float rx, float ry) EllipseParams(Stroke s)
    {
        if (s.Kind == StrokeKind.Circle)
        {
            var c = new Vector2(s.Points[0].X, s.Points[0].Y);
            var rim = s.Points.Count > 1 ? new Vector2(s.Points[1].X, s.Points[1].Y) : c;
            float r = MathF.Max(0.5f, Vector2.Distance(c, rim));
            return (c, r, r);
        }
        var (a, b) = ShapeGeometry.Endpoints(s);
        return (new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f),
                MathF.Max(0.5f, MathF.Abs(b.X - a.X) * 0.5f),
                MathF.Max(0.5f, MathF.Abs(b.Y - a.Y) * 0.5f));
    }

    /// <summary>线段（画布坐标）有没有碰到矩形。</summary>
    private static bool SegmentHitsRect(Vector2 a, Vector2 b, in RectF r)
    {
        var pa = new InkPoint { X = a.X, Y = a.Y };
        var pb = new InkPoint { X = b.X, Y = b.Y };
        return ClipSegment(pa, pb, r) != null;
    }

    /// <summary>
    /// 把一条自由笔迹按 <paramref name="rect"/>（**画布坐标**）切开，
    /// 保留下来的碎片写进 <paramref name="piecesOut"/>。
    ///
    /// 返回 true 表示这条笔画真的被切过（碎片可能为空 = 整条都在矩形里）。
    /// 返回 false = 一个点都没碰到，调用方什么都别做。
    ///
    /// **切在边界上**：跨过矩形边的那些线段，会在交点处断开并生成一个新端点
    /// （压力按比例插值）。不这么做的话，快速书写留下的长直线段会被整段保留，
    /// 表现就是"擦了半天还剩一条横线横穿过去"。
    /// </summary>
    public static bool CutByRect(Stroke s, in RectF rect, List<Stroke> piecesOut)
    {
        piecesOut.Clear();
        var pts = s.Points;
        if (pts.Count == 0) return false;

        // 先看包围盒：完全不相交就快速返回（面积橡皮拖动时绝大多数候选都走这条）。
        if (!s.WorldBounds.Intersects(rect)) return false;

        if (pts.Count == 1)
        {
            var p = new Vector2(pts[0].X, pts[0].Y);
            // 一个点：在里面就没了，在外面就原样不动（调用方拿不到碎片 = 没变化）。
            return p.X >= rect.MinX && p.X <= rect.MaxX
                && p.Y >= rect.MinY && p.Y <= rect.MaxY;
        }

        bool changed = false;
        var run = new List<InkPoint>(pts.Count);

        void Flush()
        {
            // 单个孤点不保留：它画出来是一个几乎看不见的点，却会让对象数
            // 在反复擦拭时持续增长（每个碎点一条对象，撤销栈跟着涨）。
            if (run.Count >= 2) piecesOut.Add(MakePiece(s, run));
            run.Clear();
        }

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            var clip = ClipSegment(a, b, rect);             // 与矩形重叠的那一段的参数范围

            if (clip == null)
            {
                // 整段都在外面：接到当前碎片上
                if (run.Count == 0) run.Add(a);
                run.Add(b);
                continue;
            }

            var (t0, t1) = clip.Value;
            changed = true;

            if (t0 > 0f) { if (run.Count == 0) run.Add(a); run.Add(Lerp(a, b, t0)); }
            Flush();
            if (t1 < 1f) run.Add(Lerp(a, b, t1));
            // 注意最后一段：循环结束时再 Flush 一次（见下面）
        }
        Flush();

        if (!changed) return false;
        return true;
    }

    /// <summary>按一组点做一条同款新笔画（颜色/线宽/压感/变换都跟着走）。</summary>
    private static Stroke MakePiece(Stroke src, List<InkPoint> run)
    {
        var p = new Stroke
        {
            Tool = src.Tool,
            Kind = src.Kind,
            Color = src.Color,
            Width = src.Width,
            Preset = src.Preset,
            Transform = src.Transform,
            BoundsInflateFactor = src.BoundsInflateFactor,
        };
        for (int i = 0; i < run.Count; i++)
        {
            var q = run[i];
            p.AddPoint(q.X, q.Y, q.P, q.T);
        }

        // 轮廓（笔锋）是按整条笔画算的闭合多边形，切完就不再成立。
        // **必须清掉**：留着的话碎片会画成原来那一整条的轮廓。
        p.Outline = null;
        p.BeautifiedWidths = null;
        return p;
    }

    private static InkPoint Lerp(InkPoint a, InkPoint b, float t) => new()
    {
        X = a.X + (b.X - a.X) * t,
        Y = a.Y + (b.Y - a.Y) * t,
        P = a.P + (b.P - a.P) * t,
        T = a.T + (b.T - a.T) * t,
    };

    /// <summary>
    /// 线段与矩形重叠部分（Liang–Barsky 裁剪）。返回 null = 完全不相交。
    ///
    /// 选它的理由：只要四次除法、不分配对象，而且**给出准确的交点参数**——
    /// 切段要的正是这个参数（拿交点去插值压力）。Sutherland–Hodgman 那种
    /// 多边形裁剪在这里是杀鸡用牛刀，还得为每条笔画分配临时多边形。
    /// </summary>
    private static (float t0, float t1)? ClipSegment(InkPoint a, InkPoint b, in RectF r)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float t0 = 0f, t1 = 1f;

        foreach (var (p, q) in new (float p, float q)[]
        {
            (-dx, a.X - r.MinX),
            ( dx, r.MaxX - a.X),
            (-dy, a.Y - r.MinY),
            ( dy, r.MaxY - a.Y),
        })
        {
            if (MathF.Abs(p) < 1e-9f)
            {
                if (q < 0f) return null;              // 平行且在矩形外侧
                continue;
            }
            float t = q / p;
            if (p < 0f) { if (t > t1) return null; if (t > t0) t0 = t; }
            else { if (t < t0) return null; if (t < t1) t1 = t; }
        }
        return t1 >= t0 ? (t0, t1) : null;
    }
}
