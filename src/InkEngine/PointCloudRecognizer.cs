// =====================================================================================
//  $P Point-Cloud Recognizer —— **照搬官方实现**（JavaScript 版逐函数搬成 C#）
//
//  来源与许可（New BSD，按许可要求保留版权声明）：
//    The $P Point-Cloud Recognizer (JavaScript version)
//    Radu-Daniel Vatavu, Lisa Anthony, Jacob O. Wobbrock
//    http://depts.washington.edu/acelab/proj/dollar/pdollar.js   （官方源码）
//    https://depts.washington.edu/acelab/proj/dollar/pdollar.html
//    许可：New BSD License，Copyright (C) 2012, Radu-Daniel Vatavu, Lisa Anthony,
//          and Jacob O. Wobbrock. All rights reserved. Last updated July 14, 2018.
//    引用：Vatavu, R.-D., Anthony, L. and Wobbrock, J.O. (2012). Gestures as point clouds:
//          A $P recognizer for user interface prototypes. ICMI '12, pp. 273-280.
//
//  为什么用它（用户 2026-09-23 定的："直接去参考别人是如何写的代码，完全复制逻辑过来"）：
//    原来自写的判据是"数角点 + 判直角/平行"——**单点判据**，手画的圆角、过冲、波浪边
//    随便一个就能把它带偏（"矩形识别形变太大、完全和墨迹对不上"就是这么来的）。
//    $P 是学术界公开、被大量项目用过的点云识别器：把笔画看成**一堆无序的点**，
//    和模板点云做"贪心点匹配"，**不看笔画顺序、不看方向、不看起笔点**——
//    正好对上"手画图形"这件事。它只回答"这像哪个图形"，**几何仍然由我们的拟合给**
//    （见 ShapeRecognize：分类用 $P、定形用拟合、最后一关是"贴不贴得住墨迹"）。
//
//  相对官方代码**只改了三处**（都写在对应函数上）：
//    ① 模板换成我们库里的图形（官方那 16 个模板是手势符号：T/N/D/X/星/音符……）；
//    ② `Recognize` 从"只返回最像的一个"改成"**返回排名前几名**"——因为我们的第二步
//       （几何拟合 + 验收）可能否掉第一名，那时候要能拿第二名再试；
//    ③ 每个点带**笔画编号**（官方也有 `Point.ID`），我们用它保证 Resample 不会
//       跨笔画连线（多笔画的四条边那条路要用）。
// =====================================================================================

using System.Numerics;

namespace InkEngine;

/// <summary>一个点 + 它属于第几笔（$P 官方 `Point.ID` 的对应物）。</summary>
internal struct PcPoint
{
    public float X, Y;
    public int Id;
    public PcPoint(float x, float y, int id) { X = x; Y = y; Id = id; }
}

/// <summary>识别结果的一项：模板名 + 距离（**越小越像**）。</summary>
internal struct PcResult
{
    public string Name;
    public float Distance;
}

/// <summary>
/// $P 点云识别器（照抄官方实现，见文件头）。
/// 模板在第一次用到时构建（≈115 个：6 种图形 × 若干长宽比/斜度 × 若干姿态角）。
/// </summary>
internal static class PointCloudRec
{
    // 官方常量（pdollar.js 第 92~94 行）
    private const int NumPoints = 32;          // 重采样成 32 个点
    private const float GreedyEpsilon = 0.50f; // GREEDY-5：官方实测最好的那个 ε

    internal const string NameLine = "Line";
    internal const string NameCircle = "Circle";
    internal const string NameEllipse = "Ellipse";
    internal const string NameRectangle = "Rectangle";
    internal const string NameTriangle = "Triangle";
    internal const string NameParallelogram = "Parallelogram";

    private sealed class Cloud
    {
        public string Name;
        public PcPoint[] Points;      // 已 Resample + Scale + TranslateTo
    }

    private static Cloud[] _templates;

    /// <summary>模板个数（自检报告里打出来：它直接决定单次识别要算多久）。</summary>
    internal static int TemplateCount => (_templates ??= BuildTemplates()).Length;

    /// <summary>
    /// 识别：返回**按距离从小到大排好序**的候选（我们拿它当"先试哪个图形"的顺序）。
    /// <paramref name="pts"/> 里的 `Id` 是笔画编号（多笔画时各不相同；一笔画全填 1 就行）。
    /// </summary>
    internal static List<PcResult> Recognize(IReadOnlyList<PcPoint> pts, int topN = 3)
    {
        var outp = new List<PcResult>();
        if (pts == null || pts.Count < 2) return outp;
        _templates ??= BuildTemplates();

        // 官方 `Recognize` 的第一步：把待识别的那一笔**用同样的方式**归一化
        var candidate = Normalize(pts);
        if (candidate == null) return outp;

        var all = new List<PcResult>(_templates.Length);
        foreach (var t in _templates)
        {
            float d = GreedyCloudMatch(candidate, t.Points);
            all.Add(new PcResult { Name = t.Name, Distance = d });
        }
        all.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        // ⚠ **本地化改动 ②**：同一个图形往往有多个模板（不同长宽比/姿态），
        //   这里把"同名"的合并成一条（取其中最像的那个），否则 topN 会被同一个图形的
        //   几个模板占满，"拿第二名再试"就没有意义了。
        var seen = new HashSet<string>();
        foreach (var r in all)
        {
            if (!seen.Add(r.Name)) continue;
            outp.Add(r);
            if (outp.Count >= topN) break;
        }
        return outp;
    }

    // ---------------------------------------------------------------------------------
    //  以下 private 部分是官方 pdollar.js 的逐函数搬运（函数名保持同名，便于对照）
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// `PointCloud` 构造函数那三行：`Resample → Scale → TranslateTo`。
    /// 返回 null = 这一笔太短/退化，没法归一化。
    /// </summary>
    private static PcPoint[] Normalize(IReadOnlyList<PcPoint> points)
    {
        if (points == null || points.Count < 2) return null;
        var resampled = Resample(points, NumPoints);
        if (resampled == null || resampled.Length < 2) return null;
        var scaled = Scale(resampled);
        var moved = TranslateTo(scaled, new PcPoint(0, 0, 0));
        return moved;
    }

    /// <summary>
    /// 官方 `Resample(points, n)`：把点列重采样成**等距**的 n 个点。
    ///
    /// ⚠ 本地化改动 ③：官方靠 `points[i].ID == points[i-1].ID` 判断"这两个点是不是同一笔"，
    /// 我们照抄这个判断——于是**多笔画之间不会连出一条不存在的线段**
    ///（多笔画的"四条边分着画"那条路就靠它）。
    /// 官方那版是**就地改造输入数组**（`points.splice`）；C# 里我们改成读原数组、写新数组，
    /// 其余逻辑（间隔 I、累积 D、插值点 q）一模一样。
    /// </summary>
    private static PcPoint[] Resample(IReadOnlyList<PcPoint> points, int n)
    {
        float I = PathLength(points) / (n - 1);        // 间隔长度
        if (I <= 0f) return null;
        float D = 0f;
        var src = new List<PcPoint>(points);           // 官方是在原数组上插入新点，我们复制一份再插
        var outp = new List<PcPoint> { src[0] };
        for (int i = 1; i < src.Count; i++)
        {
            if (src[i].Id != src[i - 1].Id) continue;  // 跨笔画不连
            float d = Distance(src[i - 1], src[i]);
            if (D + d >= I)
            {
                float qx = src[i - 1].X + ((I - D) / d) * (src[i].X - src[i - 1].X);
                float qy = src[i - 1].Y + ((I - D) / d) * (src[i].Y - src[i - 1].Y);
                var q = new PcPoint(qx, qy, src[i].Id);
                outp.Add(q);
                src.Insert(i, q);                      // 官方就是插回原数组，让 i 下一轮落在 q 上
                D = 0f;
            }
            else D += d;
        }
        // 官方注释：有时因为舍入误差会少最后一个点，补上
        if (outp.Count == n - 1)
            outp.Add(new PcPoint(src[^1].X, src[^1].Y, src[^1].Id));
        return outp.ToArray();
    }

    /// <summary>
    /// 官方 `Scale(points)`：**按较大的一边**等比缩放到 1×1 附近
    ///（`size = max(width, height)`，所以长宽比是**保留**的——这对我们很关键：
    /// 一个 3:1 的矩形不会和正方形模板混起来）。
    /// </summary>
    private static PcPoint[] Scale(PcPoint[] points)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        float size = MathF.Max(maxX - minX, maxY - minY);
        if (size <= 0f) return points;
        var outp = new PcPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
            outp[i] = new PcPoint((points[i].X - minX) / size, (points[i].Y - minY) / size, points[i].Id);
        return outp;
    }

    /// <summary>官方 `TranslateTo(points, pt)`：把**重心**移到 pt（官方用它把点云归到原点）。</summary>
    private static PcPoint[] TranslateTo(PcPoint[] points, PcPoint pt)
    {
        var c = Centroid(points);
        var outp = new PcPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
            outp[i] = new PcPoint(points[i].X + pt.X - c.X, points[i].Y + pt.Y - c.Y, points[i].Id);
        return outp;
    }

    /// <summary>官方 `Centroid(points)`。</summary>
    private static PcPoint Centroid(PcPoint[] points)
    {
        float x = 0f, y = 0f;
        foreach (var p in points) { x += p.X; y += p.Y; }
        return new PcPoint(x / points.Length, y / points.Length, 0);
    }

    /// <summary>官方 `PathLength(points)`：**同一笔内**相邻点的距离之和。</summary>
    private static float PathLength(IReadOnlyList<PcPoint> points)
    {
        float d = 0f;
        for (int i = 1; i < points.Count; i++)
            if (points[i].Id == points[i - 1].Id) d += Distance(points[i - 1], points[i]);
        return d;
    }

    /// <summary>官方 `Distance(p1, p2)`。</summary>
    private static float Distance(PcPoint p1, PcPoint p2)
    {
        float dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 官方 `GreedyCloudMatch(points, P)`：GREEDY-5（ε = 0.50）。
    /// 从若干起点各试一次**双向**的贪心匹配，取最小的那个距离。
    /// </summary>
    private static float GreedyCloudMatch(PcPoint[] points, PcPoint[] template)
    {
        int step = (int)MathF.Floor(MathF.Pow(points.Length, 1f - GreedyEpsilon));
        if (step < 1) step = 1;
        float min = float.MaxValue;
        for (int i = 0; i < points.Length; i += step)
        {
            float d1 = CloudDistance(points, template, i);
            float d2 = CloudDistance(template, points, i);
            if (d1 < min) min = d1;
            if (d2 < min) min = d2;
        }
        return min;
    }

    /// <summary>
    /// 官方 `CloudDistance(pts1, pts2, start)`：对 pts1 的每个点，找 pts2 里**还没被占用的**
    /// 最近点（贪心，近似匈牙利算法），按"离起点多远"加权求和。
    /// 这个"无序匹配"正是 $P 能忽略笔画顺序/方向的原因。
    /// </summary>
    private static float CloudDistance(PcPoint[] pts1, PcPoint[] pts2, int start)
    {
        var matched = new bool[pts1.Length];
        float sum = 0f;
        int i = start;
        do
        {
            int index = -1;
            float min = float.MaxValue;
            for (int j = 0; j < matched.Length; j++)
            {
                if (matched[j]) continue;
                float d = Distance(pts1[i], pts2[j]);
                if (d < min) { min = d; index = j; }
            }
            if (index < 0) break;                       // 理论上不会发生（两边点数相同）
            matched[index] = true;
            float weight = 1f - ((i - start + pts1.Length) % pts1.Length) / (float)pts1.Length;
            sum += weight * min;
            i = (i + 1) % pts1.Length;
        } while (i != start);
        return sum;
    }

    // ---------------------------------------------------------------------------------
    //  模板：我们库里的图形（本地化改动 ①）
    //
    //  为什么同一种图形要放好几个模板：$P 的官方设计就是"一类手势放多个样本"，
    //  被认错时把那一笔**加进模板**（官方 demo 的整条交互就是这个）。我们没有"用户样本"，
    //  所以放的是**理想图形的变体**：不同长宽比 + 不同姿态角。
    //  ⚠ $P **不做旋转归一化**（官方就只有 Resample/Scale/TranslateTo 三步），
    //    所以斜着画的矩形必须靠"斜的模板"去对——姿态角每 20°~30° 放一个。
    // ---------------------------------------------------------------------------------

    private static Cloud[] BuildTemplates()
    {
        var list = new List<(string name, List<PcPoint> pts)>();

        // 直线：官方模板里就叫 line，是两个点。姿态角 0~170°（直线差 180° 是同一件事）。
        for (int a = 0; a < 180; a += 20)
            list.Add((NameLine, Rot(Line(1f), a)));

        // 圆：转不转都是一个点云，一个模板就够
        list.Add((NameCircle, Ellipse(1f, 1f)));

        // 椭圆：长宽比 1.5 / 2 / 3；转 0~80°（椭圆差 180° 是同一件事）
        foreach (float ratio in new[] { 1.5f, 2f, 3f })
            for (int a = 0; a < 180; a += 20)
                list.Add((NameEllipse, Rot(Ellipse(1f, 1f / ratio), a)));

        // 矩形：长宽比 1.2 / 1.6 / 2.4 / 3.5；转 0~170°（矩形差 90° 是同一件事，多放几个无妨）
        foreach (float ratio in new[] { 1.2f, 1.6f, 2.4f, 3.5f })
            for (int a = 0; a < 90; a += 20)
                list.Add((NameRectangle, Rot(Rect(1f, 1f / ratio), a)));

        // 三角形：正三角 / 直角 / 扁平；转 0~330°（三角形没有小于 360° 的对称，得放满一圈）
        foreach (var tri in new[] { TriangleEquilateral(), TriangleRight(), TriangleWide() })
            for (int a = 0; a < 360; a += 30)
                list.Add((NameTriangle, Rot(tri, a)));

        // 平行四边形：斜 30° / 50°，各两个长宽比；转 0~150°（差 180° 是同一件事）
        foreach (float skew in new[] { 30f, 50f })
            foreach (float h in new[] { 0.5f, 0.8f })
                for (int a = 0; a < 180; a += 30)
                    list.Add((NameParallelogram, Rot(Parallelogram(1f, h, skew), a)));

        var clouds = new List<Cloud>(list.Count);
        foreach (var (name, pts) in list)
        {
            var norm = Normalize(pts);
            if (norm != null) clouds.Add(new Cloud { Name = name, Points = norm });
        }
        return clouds.ToArray();
    }

    // ---- 理想图形的点列（都画成"一笔"，Id 全填 1）--------------------------------------

    private static List<PcPoint> Line(float w)
    {
        var pts = new List<PcPoint>();
        for (int i = 0; i <= 24; i++) pts.Add(new PcPoint(w * i / 24f, 0f, 1));
        return pts;
    }

    private static List<PcPoint> Ellipse(float rx, float ry)
    {
        var pts = new List<PcPoint>();
        int n = 48;
        for (int i = 0; i <= n; i++)
        {
            float t = 2f * MathF.PI * i / n;
            pts.Add(new PcPoint(MathF.Cos(t) * rx, MathF.Sin(t) * ry, 1));
        }
        return pts;
    }

    private static List<PcPoint> Rect(float w, float h)
    {
        var v = new[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, h), new Vector2(0, h) };
        return Walk(v, 10);
    }

    private static List<PcPoint> Parallelogram(float w, float h, float skewDeg)
    {
        float dx = h / MathF.Tan(skewDeg * MathF.PI / 180f);
        var v = new[] { new Vector2(0, 0), new Vector2(w + dx, 0), new Vector2(w, h), new Vector2(0, h) };
        return Walk(v, 10);
    }

    private static List<PcPoint> TriangleEquilateral()
    {
        float h = MathF.Sqrt(3f) / 2f;
        return Walk(new[] { new Vector2(0, h), new Vector2(1f, h), new Vector2(0.5f, 0) }, 12);
    }

    private static List<PcPoint> TriangleRight()
        => Walk(new[] { new Vector2(0, 1f), new Vector2(1f, 1f), new Vector2(0, 0) }, 12);

    private static List<PcPoint> TriangleWide()
        => Walk(new[] { new Vector2(0, 0.45f), new Vector2(1f, 0.45f), new Vector2(0.42f, 0) }, 12);

    /// <summary>沿多边形走一圈（顶点之间均匀取点），模拟"一笔画完"的顺序。</summary>
    private static List<PcPoint> Walk(Vector2[] v, int perEdge)
    {
        var pts = new List<PcPoint>();
        for (int i = 0; i < v.Length; i++)
        {
            var a = v[i];
            var b = v[(i + 1) % v.Length];
            for (int k = 0; k <= perEdge; k++)
            {
                if (i > 0 && k == 0) continue;
                pts.Add(new PcPoint(a.X + (b.X - a.X) * k / perEdge, a.Y + (b.Y - a.Y) * k / perEdge, 1));
            }
        }
        return pts;
    }

    /// <summary>把整条点列绕自己的重心转 <paramref name="deg"/> 度（**模板姿态变体**用）。</summary>
    private static List<PcPoint> Rot(List<PcPoint> pts, float deg)
    {
        if (deg == 0f) return pts;
        float cx = 0f, cy = 0f;
        foreach (var p in pts) { cx += p.X; cy += p.Y; }
        cx /= pts.Count; cy /= pts.Count;
        float rad = deg * MathF.PI / 180f, cs = MathF.Cos(rad), sn = MathF.Sin(rad);
        var outp = new List<PcPoint>(pts.Count);
        foreach (var p in pts)
        {
            float dx = p.X - cx, dy = p.Y - cy;
            outp.Add(new PcPoint(cx + dx * cs - dy * sn, cy + dx * sn + dy * cs, p.Id));
        }
        return outp;
    }
}
