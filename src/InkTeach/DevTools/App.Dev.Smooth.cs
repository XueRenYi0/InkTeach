using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;


internal sealed partial class App
{
    private void SmoothShowcase(string path)
    {
        Console.WriteLine($"=== 出图：曲线化对照（{path}）===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);
        var dotInk = new Color4(0.62f, 0.66f, 0.72f, 1f);   // 采样点的小灰点
        float penW = 6f * DpiScale;
        float ox = _virtualX + 120f, oy = _virtualY + 160f;

        void Add(IEnumerable<Vector2> pts, float dx, float dy, bool dots = true)
        {
            var list = new List<Vector2>();
            foreach (var p in pts) list.Add(new Vector2(p.X + dx, p.Y + dy));

            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = ink, Width = penW };
            foreach (var p in list) s.AddPoint(p.X, p.Y, 0.5f, 0);
            Doc.AddStroke(s);

            if (!dots) return;
            // 每个采样点打一个小灰点：曲线过不过点、直角动没动，看这个最直观
            foreach (var p in list)
            {
                var d = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = dotInk, Width = 2.4f * DpiScale,
                };
                d.AddPoint(p.X, p.Y, 0.5f, 0);
                Doc.AddStroke(d);
            }
        }

        // ① 快弧：中心 (ox+300, oy+280)、半径 280、每 13° 一个点（≈65px 一段，折角肉眼可见）
        var arc = new List<Vector2>();
        for (int i = 0; i <= 14; i++)
        {
            float a = MathF.PI * i / 14f;
            arc.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(arc, ox + 300f, oy + 280f);

        // ② 直角：两条臂各 6 个点（步长 56）
        var corner = new List<Vector2>();
        for (int i = 0; i <= 6; i++) corner.Add(new Vector2(i * 56f, 0f));
        for (int i = 1; i <= 6; i++) corner.Add(new Vector2(336f, i * 56f));
        Add(corner, ox + 760f, oy + 100f);

        // ③ 抛物线：y = x²/500，x ∈ [−260, 260]（手画曲线不许被改形）
        var parab = new List<Vector2>();
        for (int i = 0; i <= 13; i++)
        {
            float x = -260f + i * 40f;
            parab.Add(new Vector2(x, x * x / 500f));
        }
        Add(parab, ox + 1400f, oy + 120f);

        // ④ 锯齿：周期 2 的连续尖角（每个峰谷都必须留住）
        var zig = new List<Vector2>();
        for (int i = 0; i <= 12; i++) zig.Add(new Vector2(i * 55f, (i % 2 == 0) ? 0f : 110f));
        Add(zig, ox + 1860f, oy + 100f);

        // ⑤ 慢画直角：90° 摊在 5 小段上、**每段只有 20°**（比角点阈值 35° 小）——
        //    只有 ±2 点窗口判据兜得住；局部判据看到每段 20° 是判不出来的。
        //
        //    ⚠ 2026-10-06 修正：原来这条用的是 **120px 一段**（"90° 摊在三小段上"）。
        //    那个点距对应的速度是 130Hz × 120px ≈ 15600px/s，是"飞笔"不是"慢画"；
        //    而且"90° 摊在 3 个 120px 点上"与"半径 ~229px 的圆弧"在数学上无法区分
        //    （只有 4 个点），逼着窗口判据去误伤圆弧——正是"快速画圆变折线"的根因。
        //    改成 11px 一段（真·慢画的采样密度）之后，它才真的在测它声称要测的东西。
        var slow = new List<Vector2>();
        {
            const float stepPx = 11f;            // 点距（慢画：11px/点）
            int nPer = 6;                        // 先直走 6 段
            float ang5 = 0f;
            var p = new Vector2(0f, 0f);
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            for (int i = 0; i < 5; i++)          // 转 5 段 × 20° = 100°
            {
                ang5 += 20f * MathF.PI / 180f;
                slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx;
            }
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            slow.Add(p);
        }
        Add(slow, ox + 760f, oy + 520f);

        // ⑥ 密采样弧：**和真笔（Windows Ink 开）一个量级**（每 1° 一个点 ≈ 4.9px 一段）。
        // 它回答的是"拿手写板试为什么看不出区别"：点密到一定程度，折线和曲线就是同一根线
        //（弦长 c、半径 R 时中点偏差 ≈ c²/8R，c=5、R=280 → 0.011px，比一个像素小两个数量级）。
        var dense = new List<Vector2>();
        for (int i = 0; i <= 180; i++)
        {
            float a = MathF.PI * i / 180f;
            dense.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(dense, ox + 1560f, oy + 700f, dots: false);

        // ⑦ **稀疏快弧**（用户 2026-10-06 报的那个）：半径 150、每 22.5° 一个点
        //（弦长 = 2×150×sin(11.25°) ≈ 58.5px 一段）。这是"快速画圆 / 手写板关掉
        //  Windows Ink"的采样密度。
        //
        // 为什么单列这一条：窗口判据量到的转角 ≈ 2×(点距/半径)，
        // 这里 = 2×58.5/150 = 44.7° > 阈值 35° → **每个点都被判成角点** →
        // 半圆退化成 8 边形。修前修后一眼能看出来（对照用 `--smoothcornerpx 0`）。
        var sparse = new List<Vector2>();
        for (int i = 0; i <= 8; i++)
        {
            float a = MathF.PI * i / 8f;
            sparse.Add(new Vector2(MathF.Cos(a) * 150f, -MathF.Sin(a) * 150f));
        }
        Add(sparse, ox + 1120f, oy + 380f);

        // ⑧ **真机复现**（用户 2026-10-07 的实测数据）：「ink 关、快速画圆」那一档
        //（`压感=False 活笔=False 点 11 均距 38.9px 总长 389px`）。
        //
        // 关键算术：**圆上每个顶点的转角 = 360°/段数**，与半径无关。
        //   11 点 → 10 段 → **36°/顶点**，刚好越过 35° 阈值；
        //   ink 开的 36 点 → 35 段 → 10.3°/顶点，差得远。
        // 所以这一条要复现的是"**局部判据①在稀采样下把光滑圆弧判成角点**"，
        // 和用例⑦（那条是窗口判据③越界）是**两个不同的毛病**。
        var realcase = new List<Vector2>();
        {
            const int nSeg = 10;                 // 11 个点 = 10 段
            float R = 389f / (2f * MathF.PI);    // 总长 389px → 半径 ≈ 61.9px
            // **必须绕成接近闭合的一圈**：11 点绕 355° → 每段 35.5°，刚好越过
            // 35° 阈值（局部判据①）。绕得少一点（比如半圈）每段只有 17°，
            // 复现不出来——第一版就是这么做的，出图是光滑的，白测一轮。
            for (int i = 0; i <= nSeg; i++)
            {
                float a = 355f * MathF.PI / 180f * i / nSeg - 355f * MathF.PI / 180f * 0.5f;
                realcase.Add(new Vector2(MathF.Cos(a) * R, -MathF.Sin(a) * R));
            }
        }
        Add(realcase, ox + 420f, oy + 760f);

        // ⑨ **真机复现·椭圆**（用户 2026-10-07 第二轮数据）：他的形状不是圆，是
        //    周长 515~564px 的**椭圆**（≈200×150），10~14 个点、点距 40~60px。
        //
        //    为什么必须单列：椭圆上**转角是变化的**——端部曲率半径 = b²/a ≈ 56px、
        //    侧面 = a²/b ≈ 133px，所以端部每顶点转角 ≈48°、侧面只有 ≈20°，
        //    中间连续过渡。任何"相邻差"式的判据如果容差取小了，端部照样被切成角
        //    ——第一版连续性容差 12° 就是这么栽的（圆的转角处处相等，能过；
        //    椭圆过不了）。
        var ellipse = new List<Vector2>();
        {
            const float a = 100f, b = 75f;       // ≈ 周长 553px，和用户实测一致
            const int n = 12;                    // 12 个点
            // 按**弧长均匀**取样：等弧长行走（手画时点的分布接近这个，
            // 不是按参数角均匀——按角均匀会让端部点变稀，测不出真实分布）
            float perimeter = 0f;
            for (int k = 0; k < 720; k++)
            {
                float t0 = k * MathF.PI / 360f, t1 = (k + 1) * MathF.PI / 360f;
                float s0 = MathF.Sqrt(a * a * MathF.Sin(t0) * MathF.Sin(t0) + b * b * MathF.Cos(t0) * MathF.Cos(t0));
                float s1 = MathF.Sqrt(a * a * MathF.Sin(t1) * MathF.Sin(t1) + b * b * MathF.Cos(t1) * MathF.Cos(t1));
                perimeter += (s0 + s1) * 0.5f * (MathF.PI / 360f);
            }
            float step = perimeter / n;
            ellipse.Add(new Vector2(a, 0f));
            float theta = 0f;
            for (int i = 1; i <= n; i++)
            {
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    float d = 0.0005f;
                    acc += s * d;
                    theta += d;
                }
                float ang = theta - MathF.PI * 0.5f;   // 让起笔落在端部
                ellipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(ellipse, ox + 800f, oy + 760f);

        // ⑩ **真机复现·不均匀点距**（用户 2026-10-07 第三轮数据）：
        //    他实测的顶点转角序列是 `51 18 17 24 30 31 37 22 41 35 27` /
        //    `37 42 35 70 27 13 23 69 60 16` —— **同一个光滑形状上，转角在 13°~70° 之间跳**。
        //
        //    为什么：转角 ≈ **点距 / 局部曲率半径**。手快速画时点距不均匀
        //    （60Hz 采样 + 手抖），于是转角也跟着跳。**这不代表形状有角。**
        //    任何"转角 ≥ 固定绝对值就算角"的判据都会被它骗到 —— 这就是前几版反复栽的地方。
        //
        //    这条用例就是用"椭圆 + 均匀弧长点 + 沿路径抖动 ±50% 点距"复现那个分布。
        var jitterEllipse = new List<Vector2>();
        {
            const float a = 110f, b = 78f;
            const int n = 12;
            var rnd = new Random(20261007);
            float theta = 0f;
            jitterEllipse.Add(new Vector2(a, 0f));
            for (int i = 1; i <= n; i++)
            {
                // 基准步长按周长均分，再乘 0.5~1.6 的抖动（复现 60Hz 快速手画的点距分布）
                float baseStep = 553f / n;
                float step = (float)(baseStep * (0.5 + rnd.NextDouble() * 1.1));
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    acc += s * 0.0005f;
                    theta += 0.0005f;
                }
                float ang = theta - MathF.PI * 0.5f;
                jitterEllipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(jitterEllipse, ox + 1300f, oy + 760f);

        // ⑪ **点密度对照**（用户 2026-10-07 问"raw input 值不值"）：
        //    同一个椭圆、同一条弧长路径，按两种点距各画一条：
        //      · 左边 13 点 = 关 ink 时的真实点距（~68Hz × 2900px/s ≈ 43px 一点）
        //      · 右边 33 点 = 接上 raw input 之后的点距（~170Hz ≈ 17px 一点）
        //    左边是"我们现在能拿到的"，右边是"raw input 能拿到的"。
        //    两条都过同一套曲线代码，差别**只在采样密度**——所以这张图直接回答
        //    "多出来的那 60% 输入到底买到了什么"。
        void DensePair(float dx, int n, bool dots)
        {
            var pts = new List<Vector2>();
            const float a2 = 120f, b2 = 88f;
            for (int i = 0; i <= n; i++)
            {
                float ang = MathF.PI * 1.9f * i / n - MathF.PI * 0.95f;
                pts.Add(new Vector2(MathF.Cos(ang) * a2, -MathF.Sin(ang) * b2));
            }
            Add(pts, dx, oy + 1180f, dots);
        }
        DensePair(ox + 260f, 13, dots: true);    // 现在（关 ink 的真实密度）
        DensePair(ox + 760f, 33, dots: true);    // raw input 之后

        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string off = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-off" + ext);
        string on = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-on" + ext);

        // 折线那张：先关掉开关，整层重画（版本号会顶掉每条笔的几何缓存）
        bool wasEnabled = StrokeSmoothing.Enabled;
        StrokeSmoothing.SetEnabled(false);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok1 = ScreenProbe.SaveBmp(off, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);

        // 曲线那张：打开开关，同一批笔重画一遍
        StrokeSmoothing.SetEnabled(true);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok2 = ScreenProbe.SaveBmp(on, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);
        StrokeSmoothing.SetEnabled(wasEnabled);     // 出完图恢复原状态，别把后面的路径带偏

        Console.WriteLine(ok1 ? $"  折线：{off}" : "  折线图保存失败");
        Console.WriteLine(ok2 ? $"  曲线：{on}" : "  曲线图保存失败");
        Console.WriteLine();
        _quit = true;
    }
    private static (int count, float maxDist, float x, float y) DiffFarFromTip(
        byte[] a, byte[] b, int w, int h, int ox, int oy, float tipX, float tipY, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = MathF.Sqrt((px - tipX) * (px - tipX) + (py - tipY) * (py - tipY));
                if (dist <= minDist) continue;      // 笔尖附近本来就是新墨，允许变
                count++;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy);
    }

    /// <summary>
    /// 滚动比对：两张同尺寸图按"内容整体上移 <paramref name="shift"/> 像素"逐像素对
    /// （a[y] 应等于 b[y+shift]）。返回不匹配的像素数（亮度差 &gt; 24）。
    /// 用来找"滚动时某条横带没更新"——那会表现成一整行不匹配。
    /// </summary>
    private static int CountShiftMismatch(byte[] a, byte[] b, int w, int h, int shift)
    {
        if (shift <= 0 || shift >= h) return int.MaxValue;
        int bad = 0;
        for (int y = 0; y + shift < h; y++)
        {
            int ra = y * w * 4, rb = (y + shift) * w * 4;
            for (int x = 0; x < w; x++)
            {
                int oa = ra + x * 4, ob = rb + x * 4;
                int d = Math.Abs(a[oa] - b[ob]) + Math.Abs(a[oa + 1] - b[ob + 1]) + Math.Abs(a[oa + 2] - b[ob + 2]);
                if (d > 24) bad++;
            }
        }
        return bad;
    }
    private static int FirstLineCenter(byte[] cap, int w, int h, int x)
    {
        if (cap == null) return -1;
        int run = -1;
        for (int y = 0; y < h; y++)
        {
            int o = (y * w + x) * 4;
            bool dark = cap[o] < 225 && cap[o + 1] < 225 && cap[o + 2] < 225;
            if (dark) { if (run < 0) run = y; }
            else if (run >= 0) return (run + y - 1) / 2;
        }
        return -1;
    }

    /// <summary>两张同尺寸 BGRA 图的差异像素数（亮度差 &gt; 24）。</summary>
    private static int DiffCount(byte[] a, byte[] b, int w, int h)
    {
        int n = 0;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
            if (d > 24) n++;
        }
        return n;
    }

    /// <summary>
    /// 两张同尺寸 BGRA 图的差异，只看**离折线 <paramref name="path"/> 的前
    /// <paramref name="pathCount"/> 个点 &gt; minDist 的那些像素**。
    /// 返回（这样的像素数、其中最远那个到路径的距离、坐标）。
    /// </summary>
    private static (int count, float maxDist, float x, float y,
                    int minX, int minY, int maxX, int maxY) DiffFarFromPath(
        byte[] a, byte[] b, int w, int h, int ox, int oy,
        List<Vector2> path, int pathCount, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = DistToPath(path, pathCount, px, py);
                if (dist <= minDist) continue;      // B 自己画的 / 盖住的，允许变
                count++;
                if (i < minX) minX = i;
                if (i > maxX) maxX = i;
                if (j < minY) minY = j;
                if (j > maxY) maxY = j;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy, minX, minY, maxX, maxY);
    }

    /// <summary>点到折线（前 count 个点）的最短距离。</summary>
    private static float DistToPath(List<Vector2> path, int count, float x, float y)
    {
        int n = Math.Min(count, path.Count);
        if (n <= 0) return float.MaxValue;
        if (n == 1) return Vector2.Distance(path[0], new Vector2(x, y));
        float best = float.MaxValue;
        for (int i = 1; i < n; i++)
        {
            float d = DistToSegment(path[i - 1].X, path[i - 1].Y, path[i].X, path[i].Y, x, y);
            if (d < best) best = d;
        }
        return best;
    }

    private static float DistToSegment(float ax, float ay, float bx, float by, float px, float py)
    {
        float vx = bx - ax, vy = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - vx * t, dy = wy - vy * t;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
    private static (float[] width, float[] center) InkProfile(byte[] bgra, int w, int h)
    {
        var width = new float[w];
        var center = new float[w];
        for (int i = 0; i < w; i++)
        {
            int min = int.MaxValue, max = -1;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                if (bgra[o] + bgra[o + 1] + bgra[o + 2] < 600)
                {
                    if (j < min) min = j;
                    if (j > max) max = j;
                }
            }
            width[i] = max >= 0 ? max - min + 1 : -1f;
            center[i] = max >= 0 ? (min + max) * 0.5f : -1f;
        }
        return (width, center);
    }

    /// <summary>
    /// `--tailjumptest`：**预测尾"突突突往外跳"的专项检测**（2026-09-28 用户实测：
    /// "鼠标模式下手写板写字，末端总会突突突往外跳，Ink 开了就没有了"）。
    ///
    /// 症状的机理：鼠标模式下没有委托轨迹，预测尾由我们自己画；而鼠标/兼容模式的输入
    /// 是**突发**的（报几个点、停一下、再报几个点）。来一阵好数据 → 末速度算得大 →
    /// 尾巴甩到前面；间隔一超 `MaxGapMs`(20ms) → 速度清零 → 尾巴没了；再来数据又甩出来。
    ///
    /// 判据是**笔尖的逐帧轨迹**（品红墨的最右列）：
    ///   · `往前跳的最大幅度`：一帧里笔尖突然前进多少；
    ///   · `往后退的次数 / 最大后退`：尾巴消失时笔尖会**缩回去**——这就是"突突"的正身。
    /// 对照组 `--off` 应当明显更稳（这就是这条测试的自证）。
    /// 测试**自己摆状态**（不依赖启动默认值）：默认测"开预测"，`--off` 测对照。
    /// </summary>
    // [重启 2026-10-08] B4 预测尾"突突跳"检测（老系统 2026-10-05 停用，本次接回新尾）：
    // 新尾的纪律（长度限速 + 断笔后缓慢回收）就是对着下面这段"机理"修的。
    private void TailJumpTest(bool noisy = false, bool predictOff = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 预测尾“突突跳”检测（合成鼠标，突发节奏）===");
        // 测试自己摆状态（不依赖启动默认值）：默认测"开预测"，`--off` 测对照
        bool predOn = !predictOff;
        global::InkEngine.InkEngine.PredictTailEnabled = predOn;
        Console.WriteLine($"  预测={(predOn ? "开" : "关（对照）")}；节奏={(noisy ? "忽大忽小 + 间隔不齐（像真鼠标）" : "固定步长 + 每 4 点一停")}");

        // ── B4.2（2026-10-08 真机反馈）：**急转**时不许留"旧方向"的尾（**纯引擎，不走屏幕/合成鼠标**，
        //    所以放在 SkipIfNoSyntheticInput 之前——鼠标忙的时候这项也照样能跑）───────────
        //   用户（学校大屏 + 手写板）："写快的时候急转/急拐弯，偶尔会跳一下"。
        //   机理：旧方向的尾留在屏幕上、再按慢速率缩回 = 那一"跳"。
        //   现在两道防线：预测器自己的**方向自适应弧长收缩**（2026-10-15 线性预测内置；
        //   急转那一帧的尾长按方向不稳度自动缩）+ 失效快收（n=0 时 0.72px/ms）。
        //   快写向右 → 急转 75° → 两帧内"沿旧方向伸出"要 ≤4px。
        if (predOn)
        {
            Doc.Clear();
            Doc.ClearHistory();
            float bx = 400f, by = 300f;
            DwellBeginForTest(bx, by);
            double t0 = NowMs;
            for (int i = 1; i <= 20; i++)          // 快写：4ms 一点、每点 12px（=3px/ms，正高速）
            {
                NowMs = t0 + i * 4;
                DwellMoveForTest(bx + i * 12f, by);
            }
            UpdatePredictTail(); UpdatePredictTail(); UpdatePredictTail();
            bool hadTail = PredictTailActive;
            float rad = 75f * MathF.PI / 180f;     // 急转 75°：向下
            for (int k = 1; k <= 2; k++)
            {
                NowMs = t0 + (20 + k) * 4;
                DwellMoveForTest(bx + 240f + 12f * k * MathF.Cos(rad),
                                       by + 12f * k * MathF.Sin(rad));
            }
            UpdatePredictTail(); UpdatePredictTail();
            float lastX = bx + 240f + 24f * MathF.Cos(rad);
            float lead = float.MinValue;
            foreach (var pt in PredictTailPoints) lead = MathF.Max(lead, pt.X - lastX);
            if (lead == float.MinValue) lead = 0f; // 尾收干净了：更谈不上"旧方向残留"
            bool turnOk = hadTail && lead <= 4f;
            Console.WriteLine($"  急转 75°：转前尾={(hadTail ? "有" : "无")}；"
                              + $"转后沿旧方向最远伸出 {lead:F1}px（要求 ≤4）");
            Console.WriteLine(turnOk
                ? "  PASS: 急转处没有'旧方向'的尾残留"
                : $"  FAIL: 急转处残留旧方向的尾 {lead:F1}px（或转前尾就没长出来）");
            DwellEndForTest();
            Doc.Clear();
            Doc.ClearHistory();
        }

        if (SkipIfNoSyntheticInput("预测尾跳跃检测（需要合成鼠标）")) { _quit = true; return; }

        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        PassThrough = false;
        SetColorFromUi(new Color4(1f, 0f, 1f, 1f));      // 品红：和光标/白底分得开
        Doc.InvalidateAll();
        SettleFrames(200);

        float y0 = _virtualY + _virtualH * 0.5f;
        float x0 = _virtualX + 400f;
        const int N = 60;

        // 先把这一步的"步长 + 间隔"定下来（确定性）：忽大忽小的步长是**鼠标加速**的样子，
        // 不齐的间隔是**报点节奏**的样子，两者一起才逼得出"尾巴一出一进"。
        var rnd = new Random(20260928);
        var steps = new float[N];
        var gaps = new int[N];
        float total = 0f;
        for (int i = 0; i < N; i++)
        {
            steps[i] = noisy ? 2f + (float)rnd.NextDouble() * 32f : 30f;
            gaps[i] = noisy ? 6 + rnd.Next(36) : (i % 4 == 3 ? 60 : 8);
            total += steps[i];
        }

        int bandW = (int)(total + 220f), bandH = 120;
        int bandX = (int)(x0 - 60f), bandY = (int)(y0 - bandH * 0.5f);
        var buf = new byte[bandW * bandH * 4];

        SendMouse((int)x0, (int)y0, 0);
        SettleFrames(120);
        SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        var tip = new List<(double t, float x)>();
        var sentAt = new List<(double t, float x)>();   // 每个采样时刻"最后报的坐标"（算前沿-报点差）
        int sent = 0;
        float cx = x0;
        double nextMove = NowMs;
        int tailFrames = 0;                      // 诊断：预测尾处于激活状态的帧数
        while (!_quit && sent < N)
        {
            if (NowMs >= nextMove)
            {
                cx += steps[sent];
                sent++;
                SendMouse((int)cx, (int)y0, 0);
                nextMove = NowMs + gaps[sent - 1];
            }
            SettleFrames(1);                     // 一拍 ≈ 一帧：保证"报点间隔"真的落在
                                                 // MaxGapMs(40ms) 以内——不然预测器每拍都被
                                                 // 断笔重置，尾巴永远长不出来，测试什么都测不到
            if (PredictTailActive) tailFrames++;
            if (ScreenProbe.CaptureRegionInto(buf, bandX, bandY, bandW, bandH))
            {
                int col = MaxInkColumn(buf, bandW, bandH, minRun: 5);
                tip.Add((NowMs, col >= 0 ? bandX + col : float.NaN));
                sentAt.Add((NowMs, cx));
            }
        }
        SendMouse((int)cx, (int)y0, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        float maxFwd = 0f, maxBack = 0f;
        int backCount = 0, solidBack = 0, measured = 0;
        for (int i = 1; i < tip.Count; i++)
        {
            if (float.IsNaN(tip[i].x) || float.IsNaN(tip[i - 1].x)) continue;
            float d = tip[i].x - tip[i - 1].x;
            measured++;
            if (d > maxFwd) maxFwd = d;
            if (d < 0f) { backCount++; if (-d > maxBack) maxBack = -d; }
            if (d < -3f) solidBack++;          // 明确回退：>3px（物理像素）= 尾整段消失再回来的量级
        }
        float lastSent = cx;
        float lastTip = 0f;
        for (int i = tip.Count - 1; i >= 0; i--) if (!float.IsNaN(tip[i].x)) { lastTip = tip[i].x; break; }

        Console.WriteLine($"  采到 {tip.Count} 帧（有效 {measured} 对），画到 x={lastSent:F0}，"
                          + $"最后笔尖 x={lastTip:F0}（落后 {lastSent - lastTip:F0}px）");
        Console.WriteLine($"  往前跳：最大 {maxFwd:F0}px/帧");
        Console.WriteLine($"  往后退：{backCount} 次，最大 {maxBack:F0}px（其中 >3px 的明确回退 {solidBack} 次）  ← “突突”的正身");
        // [B4 新判据] 墨前沿 − 最后报点：量"尾有没有真的伸出去"（恒定的一截在差分里看不见）。
        double leadSum = 0; int leadN = 0; float leadMax = float.MinValue;
        for (int i = 0; i < tip.Count && i < sentAt.Count; i++)
        {
            if (float.IsNaN(tip[i].x)) continue;
            float lead = tip[i].x - sentAt[i].x;
            leadSum += lead; leadN++;
            if (lead > leadMax) leadMax = lead;
        }
        if (leadN > 0)
            Console.WriteLine($"  墨前沿−报点：平均 {leadSum / leadN:F1}px / 最大 {leadMax:F1}px"
                              + "（开预测应明显偏正；它就是‘尾’在屏幕上的样子）");
        Console.WriteLine($"  预测尾：{tailFrames}/{tip.Count} 帧处于激活状态");
        // 判据（2026-10-15 复核，含 v6 基线对照）：
        //  · 默认节奏（固定 30px 步长、每 4 点一停）：回缩次数是"突突跳"的正身，必须 ≤2
        //    （v6 与线性预测实测都是 **0**）；单帧前跳上限 = 步长 30 + 弧长顶 6 + 4px 余量 = 40
        //    （实测 36.0px，正好顶设计上限）。
        //  · `--noisy` 节奏（间隔 6~41ms、步长 2~34px 随机）：小回退（≤3px）是**尾长跟着
        //    速度抖动调整**的固有现象，不是"突突"——v6 基线 8 次、线性预测 6 次，最大都只有
        //    3px。所以这一档只数 **>3px 的明确回退**（尾整段消失再回来的量级，应 ≤2）；
        //    单帧前跳放宽到 42（步长上限 34 + 弧长顶 6 = 40 理论值，实测 39）。
        bool ok = noisy
            ? (solidBack <= 2 && maxFwd <= 42f)
            : (backCount <= 2 && maxFwd <= 40f);
        Console.WriteLine(ok
            ? "  PASS: 笔尖轨迹是稳的（没有反复回缩）"
            : $"  FAIL: 笔尖轨迹不稳（回缩 {backCount} 次（明确 {solidBack}）/ 单帧最大 {maxFwd:F0}px）");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 一块 BGRA 图里**最右边**那一列"够粗的品红墨"（≥ <paramref name="minRun"/> 个连续像素）。
    /// 要求"够粗"是为了把落点光标环排除掉（那是细线，竖着切只有两三个像素）。
    /// </summary>
}
