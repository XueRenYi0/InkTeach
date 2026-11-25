// [职责] 抬笔时的三段式无损点抽稀：首段冻结 + 尾段绝对冻结（保笔锋）+ 中段超保守 RDP。
// [测试] --unittest StrokeSimplifier / --tapertest

using System;
using System.Collections.Generic;

namespace InkEngine;

/// <summary>
/// **三段式首尾冻结点抽稀**（2026-11-25）。
///
/// 上一版对整条笔画一刀切跑 RDP，把收笔尾段的中间点删掉了，结果两个笔锋判据同时崩：
///   · <see cref="StrokeTaper.EndSpeed"/> 看到人为拉大的 <c>dt</c>（≥ 50ms）→
///     <c>crossedPause = true</c> → 整条笔迹**直接关掉笔锋**；
///   · <see cref="StrokeTaper"/> 的尾段按弧长逐点算渐尖，缺顶点之后那段变成硬折线。
///
/// 这一版把它拆成三段，**尾段一个点都不许删**：
///   ```text
///   [0 .. 4]            首段冻结（HeadFreezeCount = 5）——起笔的入笔形状
///   [5 .. tailStart-1]  中段：0.25px / 0.02 压感超保守 RDP ＋ 30ms 时间护栏
///   [tailStart .. n-1]  尾段绝对冻结（≥180ms ＋ ≥40px ＋ ≥8 点）——笔锋 100% 原样
///   ```
/// 尾段阈值刻意**远超** <see cref="StrokeTaper"/> 自己的口径（<c>WindowMs = 100</c>、
/// <c>MaxTailWidths = 4×Width</c>）：只想"够用"，不想"刚好卡在边界上"——差一个点就会换一种笔锋。
///
/// 只动**已落笔**的自由笔迹（<c>Kind == Freehand</c>、笔 / 荧光笔、≥24 点）：
/// 短笔画、标点、小字、图形、激光、橡皮一律原样不动。
/// </summary>
internal static class StrokeSimplifier
{
    /// <summary>总开关（抬笔抽稀；关掉 = 采样点原样进文档，行为和 2026-11-25 之前一致）。</summary>
    internal static bool SimplifyOnPenUp = true;

    /// <summary>首段冻结的点数（索引 0..4 原样保留）。</summary>
    internal const int HeadFreezeCount = 5;

    /// <summary>尾段至少保留的点数（不够这么多点就整条不抽）。</summary>
    internal const int TailMinPoints = 8;

    /// <summary>尾段冻结至少要覆盖的时长（ms）。**远大于** <see cref="StrokeTaper.WindowMs"/> 的 100ms。</summary>
    internal const double TailMinMs = 180.0;

    /// <summary>尾段冻结至少要覆盖的弧长（× 线宽）。**远大于** <see cref="StrokeTaper.MaxTailWidths"/> 的 4×。</summary>
    internal const float TailMinWidths = 4.5f;

    /// <summary>尾段冻结的弧长绝对下限（px）：细笔按 4.5×宽 只有几十像素，抬到 40 更稳。</summary>
    internal const float TailMinPx = 40f;

    /// <summary>中段位置误差上限（px）：1/4 像素，肉眼不可辨（也是抗锯齿实际分辨率的量级）。</summary>
    internal const float EpsPosition = 0.25f;

    /// <summary>中段压感偏差上限（只有 <c>HasPressure</c> 才参与判定）。</summary>
    internal const float EpsPressure = 0.02f;

    /// <summary>
    /// 时间护栏（ms）：中段即使完全共线，也**不许**造出比它更长的"人为时间空洞"。
    /// 取 30 而不是紧贴 50（<see cref="StrokeTaper.EndSpeed"/> 的停顿判据）：留一倍余量，
    /// 让"抽稀后重算出来的速度"和"抽稀前的原始速度"落在同一个档里。
    /// </summary>
    internal const double TimeGuardMs = 30.0;

    /// <summary>中段至少这么多个点才值得抽（再少就没有可删的冗余）。</summary>
    internal const int MiddleMinPoints = 6;

    /// <summary>短笔画 / 标点 / 小字：低于这么多点原样不动（抽稀对小样本只会改变形状）。</summary>
    internal const int MinPoints = 24;

    /// <summary>
    /// 这条笔迹该不该走抽稀（抬笔提交前的唯一判据）。**纯判定**，测试直接钉它。
    /// </summary>
    internal static bool Eligible(Stroke s)
        => SimplifyOnPenUp && s != null && !s.ExactPolyline
           && s.Kind == StrokeKind.Freehand
           && (s.Tool == Tool.Pen || s.Tool == Tool.Highlighter)
           && s.Points.Count >= MinPoints;

    /// <summary>
    /// 就地抽稀。返回**删掉的点数**（0 = 一个都没动，调用方不必收拾缓存）。
    ///
    /// 尾段 / 首段的点逐点原样搬过去（连 <c>X/Y/P/T</c> 都不重算），只有中段会被挑着保留。
    /// </summary>
    internal static int SimplifyInPlace(List<InkPoint> pts, bool hasPressure, float width)
    {
        if (pts == null) return 0;
        int n = pts.Count;
        if (n < MinPoints) return 0;

        int headEnd = HeadFreezeCount;                  // 首段 [0, headEnd) 冻结
        int tailStart = FindTailFreezeStart(pts, width);
        if (tailStart < 0) return 0;                    // 找不到满足条件的尾段：整条不动

        // 中段（[headEnd, tailStart)）太短就不值得抽：删掉的点会把形状带偏，不如不动。
        if (tailStart - headEnd < MiddleMinPoints) return 0;

        // 首尾两段的索引原样留下；中段两端也在 keep 里（中段内部自己再挑）。
        var keep = new List<int>(n);
        for (int i = 0; i < headEnd; i++) keep.Add(i);
        RdpMiddle(pts, hasPressure, headEnd, tailStart - 1, keep);
        for (int i = tailStart; i < n; i++) keep.Add(i);

        int removed = n - keep.Count;
        if (removed <= 0) return 0;

        // 严格按原顺序搬（RdpMiddle 保证 keep 是有序的：递归拆分的先后不保证，
        // 所以这里统一排一次；中段点数 ≤ 数千，排序代价可忽略）。
        keep.Sort();
        var copy = new InkPoint[keep.Count];
        for (int i = 0; i < keep.Count; i++) copy[i] = pts[keep[i]];
        pts.Clear();
        pts.AddRange(copy);
        return removed;
    }

    /// <summary>
    /// 从末点往回扫，找出尾段冻结的起点（**满足即停**：至少 180ms、至少 40px、至少 8 点）。
    ///
    /// 注意"满足即停"是有意的：再往前多冻一段并不会更保笔锋，只会让中段没得压。
    /// 返回 -1 = 整条笔画都没有 180ms 这么长的尾巴（快速小笔画），原样不动。
    /// </summary>
    private static int FindTailFreezeStart(List<InkPoint> pts, float width)
    {
        int n = pts.Count;
        double needMs = TailMinMs;
        double needArc = Math.Max(TailMinWidths * Math.Max(0f, width), TailMinPx);

        double ms = 0;
        double arc = 0;
        // 末点本身算 1 个点：从 n-1 往回至少要走 TailMinPoints-1 段。
        for (int i = n - 1; i >= 1; i--)
        {
            double dt = pts[i].T - pts[i - 1].T;
            if (dt > 0) ms += dt;
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            arc += Math.Sqrt(dx * dx + dy * dy);

            int count = n - i + 1;                  // i..n-1 共几个点
            if (count < TailMinPoints) continue;
            if (ms >= needMs && arc >= needArc) return i;
        }
        return -1;
    }

    /// <summary>
    /// 中段 RDP（显式栈，不递归：长笔画在极端采样下递归层数能到几百层）。
    /// 保留点写进 <paramref name="keep"/>（顺序不保证，调用方排序）。
    /// </summary>
    private static void RdpMiddle(List<InkPoint> pts, bool hasPressure, int lo, int hi, List<int> keep)
    {
        keep.Add(lo);
        if (hi <= lo) return;
        keep.Add(hi);

        var stack = new Stack<(int Lo, int Hi)>(16);
        stack.Push((lo, hi));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;

            int far = -1;
            float worstErr = 0f;
            for (int i = a + 1; i < b; i++)
            {
                float err = DistanceToSegment(pts[i], pts[a], pts[b]) / EpsPosition;
                if (hasPressure)
                {
                    float t = ParamOnSegment(pts[i], pts[a], pts[b]);
                    float want = pts[a].P + (pts[b].P - pts[a].P) * t;
                    float pErr = MathF.Abs(pts[i].P - want) / EpsPressure;
                    if (pErr > err) err = pErr;
                }
                if (err > worstErr) { worstErr = err; far = i; }
            }

            if (worstErr > 1f && far > a)
            {
                keep.Add(far);
                stack.Push((a, far));
                stack.Push((far, b));
                continue;
            }

            // 共线（误差在像素级以内）——但**绝不制造 >30ms 的人为时间空洞**：
            // 首尾时间差超了就强制在中点保留一个点，两半继续判。
            if (pts[b].T - pts[a].T > TimeGuardMs && b - a >= 2)
            {
                int mid = (a + b) / 2;
                keep.Add(mid);
                stack.Push((a, mid));
                stack.Push((mid, b));
            }
        }
    }

    /// <summary>点 <paramref name="p"/> 到线段 a→b 的最短距离（a=b 时退化成点距）。</summary>
    private static float DistanceToSegment(InkPoint p, InkPoint a, InkPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        float t = len2 < 1e-9f ? 0f
            : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0f, 1f);
        float qx = a.X + dx * t, qy = a.Y + dy * t;
        float vx = p.X - qx, vy = p.Y - qy;
        return MathF.Sqrt(vx * vx + vy * vy);
    }

    /// <summary>点在 a→b 上的投影参数（夹在 0..1）；用来插值"这一点应该有多少压感"。</summary>
    private static float ParamOnSegment(InkPoint p, InkPoint a, InkPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-9f) return 0f;
        return Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0f, 1f);
    }
}
