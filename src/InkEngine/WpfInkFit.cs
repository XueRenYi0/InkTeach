// [停用 2026-10-05] 本文件的渲染实验已停用（入口开关已注释，代码保留）。
// 见 已停用-渲染实验.md：恢复方法 + 停用前完整源码备份（.revert/2026-10-05-渲染减法）。
// =====================================================================================
//  WPF 墨迹拟合的 1:1 移植：Bezier（带误差容限的逼近拟合）＋ CuspData（尖点检测）
//
//  来源：https://github.com/dotnet/wpf
//        PresentationCore/MS/internal/Ink/Bezier.cs
//        PresentationCore/MS/internal/Ink/CuspData.cs
//        PresentationCore/System/Windows/Ink/Stroke.cs（GetBezierStylusPoints 的用法）
//  许可证：MIT License（Copyright (c) .NET Foundation and Contributors，见文件尾）
//
//  与上游的差异（只做外壳适配，算法不变）：
//    · Point/Vector → System.Numerics.Vector2；double → float；
//    · himetric ↔ Avalon 的单位换算去掉：我们直接画布像素里算（算法本身是尺度无关的）；
//    · 拟合容差不再用"0.03×外接尺寸"，而是显式传 `TolerancePx`（默认 0.5px）——
//      WPF 那个默认对课堂板书太大（会改形），我们要的是"微噪声平均掉、形状别动"；
//    · 输出带上每段两端的压力（上游是 Flatten 后按弧长插值，我们直接在渲染层用端点压力）。
//
//  它在 mean2 里的位置：距离窗输出（已经很干净）→ **本拟合器**（不再要求过点，
//  只要偏离不超过容差；尖点处切断保住直角）→ 渲染。即"过点曲线"换成"逼近曲线"。
// =====================================================================================

using System.Numerics;

namespace InkEngine;

/// <summary>拟合输出的一段三次贝塞尔（P0/P1 是原始点，C1/C2 是拟合控制点）。</summary>
internal struct FitSeg
{
    public Vector2 P0, C1, C2, P1;
    /// <summary>两端的压力（0..1），渲染变宽时插值用。</summary>
    public float R0, R1;
}

/// <summary>WPF CuspData 的移植：找尖点、算切线。</summary>
internal sealed class FitCuspData
{
    private sealed class CDataPoint
    {
        public int Index;          // 原始输入点下标
        public Vector2 Point;      // 坐标（我们的单位 = 画布像素）
        public int TanPrev = -1;
        public int TanNext = -1;
    }

    private readonly List<CDataPoint> _points = new();
    private readonly List<float> _nodes = new();
    private readonly List<int> _cusps = new();
    private float _dist;
    private float _span = 3f;      // 曲率探测跨距（默认 3）

    public int Count => _points.Count;
    public float Distance() => _dist;

    public Vector2 Xy(int i) => _points[i].Point;
    public float Node(int i) => _nodes[i];
    public int GetPointIndex(int nodeIndex) => _points[nodeIndex].Index;

    /// <summary>对应上游 Analyze（去重、累计弦长、设跨距、找尖点）。</summary>
    public void Analyze(IReadOnlyList<Vector2> pts, float rSpan)
    {
        if (pts == null || pts.Count == 0) return;

        _points.Clear();
        _nodes.Clear();
        _nodes.Add(0);
        _points.Add(new CDataPoint { Index = 0, Point = pts[0] });

        int index = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            if (MathF.Abs(pts[i].X - pts[i - 1].X) < 1e-4f &&
                MathF.Abs(pts[i].Y - pts[i - 1].Y) < 1e-4f)
                continue;   // 去重（上游 AreClose）

            index++;
            _points.Add(new CDataPoint { Index = i, Point = pts[i] });
            _nodes.Add(_nodes[index - 1] + Vector2.Distance(Xy(index), Xy(index - 1)));
        }

        SetLinks(rSpan);
    }

    /// <summary>对应上游 SetLinks：算外接尺寸、跨距（span）、然后 FindAllCusps。</summary>
    private void SetLinks(float rSpan)
    {
        int count = Count;
        if (count < 2) return;

        float l = Xy(0).X, t = Xy(0).Y, r = l, b = t;
        for (int i = 0; i < count; i++)
        {
            l = MathF.Min(l, Xy(i).X);
            r = MathF.Max(r, Xy(i).X);
            t = MathF.Min(t, Xy(i).Y);
            b = MathF.Max(b, Xy(i).Y);
        }
        r -= l;
        b -= t;
        _dist = MathF.Abs(r) + MathF.Abs(b);

        if (rSpan != 0) _span = rSpan;
        else if (_dist > 0)
            _span = 0.75f * (_nodes[count - 1] * _nodes[count - 1]) / (count * _dist);
        if (_span < 1.0f) _span = 1.0f;

        FindAllCusps();
    }

    /// <summary>对应上游 SetTanLinks：给每个点找"跨距 rError 之外"的探测点，算切线用。</summary>
    public void SetTanLinks(float rError)
    {
        int count = Count;
        if (rError < 1.0f) rError = 1.0f;

        for (int i = 0; i < count; ++i)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (_nodes[j] - _nodes[i] >= rError)
                {
                    _points[i].TanNext = j;
                    _points[j].TanPrev = i;
                    break;
                }
            }
            if (_points[i].TanPrev < 0)
            {
                for (int j = i - 1; j >= 0; --j)
                {
                    if (_nodes[i] - _nodes[j] >= rError)
                    {
                        _points[i].TanPrev = j;
                        break;
                    }
                }
            }
            if (_points[i].TanNext < 0) _points[i].TanNext = count - 1;
            if (_points[i].TanPrev < 0) _points[i].TanPrev = 0;
        }
    }

    /// <summary>对应上游 GetNextCusp（二分）。</summary>
    public int GetNextCusp(int iCurrent)
    {
        int last = Count - 1;
        if (iCurrent < 0) return 0;
        if (iCurrent >= last) return last;

        int s = 0, e = _cusps.Count, m = (s + e) / 2;
        while (s < m)
        {
            if (_cusps[m] <= iCurrent) s = m; else e = m;
            m = (s + e) / 2;
        }
        return _cusps[m + 1];
    }

    /// <summary>对应上游 Tangent：按探测点近似切线方向。</summary>
    public bool Tangent(ref Vector2 ptT, int nAt, int nPrevCusp, int nNextCusp,
                        bool bReverse, bool bIsCusp)
    {
        int i1, i2, i3;
        if (bIsCusp)
        {
            if (bReverse)
            {
                i1 = _points[nAt].TanPrev;
                if (i1 < nPrevCusp || i1 < 0)
                {
                    i2 = nPrevCusp;
                    i1 = (i2 + nAt) / 2;
                }
                else
                {
                    i2 = _points[i1].TanPrev;
                    if (i2 < nPrevCusp) i2 = nPrevCusp;
                }
            }
            else
            {
                i1 = _points[nAt].TanNext;
                if (i1 > nNextCusp || i1 < 0)
                {
                    i2 = nNextCusp;
                    i1 = (i2 + nAt) / 2;
                }
                else
                {
                    i2 = _points[i1].TanNext;
                    if (i2 > nNextCusp) i2 = nNextCusp;
                }
            }
            ptT = Xy(i1) + 0.5f * Xy(i2) - 1.5f * Xy(nAt);
        }
        else
        {
            i1 = nAt;
            i2 = _points[nAt].TanPrev;
            if (i2 < nPrevCusp)
            {
                i3 = nPrevCusp;
                i2 = (i3 + i1) / 2;
            }
            else
            {
                i3 = _points[i2].TanPrev;
                if (i3 < nPrevCusp) i3 = nPrevCusp;
            }
            nAt = _points[nAt].TanNext;
            if (nAt > nNextCusp) nAt = nNextCusp;
            ptT = Xy(i1) + Xy(i2) + 0.5f * Xy(i3) - 2.5f * Xy(nAt);
        }

        if (ptT.LengthSquared() == 0) return false;
        ptT = Vector2.Normalize(ptT);
        return true;
    }

    /// <summary>对应上游 GetCurvature：1 - cos(转角)，0..2。</summary>
    private float GetCurvature(int iPrev, int iCurrent, int iNext)
    {
        Vector2 v = Xy(iCurrent) - Xy(iPrev);
        Vector2 w = Xy(iNext) - Xy(iCurrent);
        float r = v.Length() * w.Length();
        if (r == 0) return 0;
        return 1 - Vector2.Dot(v, w) / r;
    }

    /// <summary>对应上游 FindAllCusps：曲率 &gt;0.80 且是局部极大才算尖点；&lt;0.035 当直线跳过。</summary>
    private void FindAllCusps()
    {
        _cusps.Clear();
        if (Count < 1) return;
        _cusps.Add(0);

        int iPrev = 0, iNext = 0, iCuspPrev = 0;
        if (!FindNextAndPrev(0, iCuspPrev, ref iPrev, ref iNext))
        {
            if (Count == 0) _cusps.Clear();
            else if (Count > 1) _cusps.Add(iNext);
            return;
        }

        int iPoint = iNext;
        while (FindNextAndPrev(iPoint, iCuspPrev, ref iPrev, ref iNext))
        {
            float rCurv = GetCurvature(iPrev, iPoint, iNext);
            if (rCurv > 0.80f)
            {
                float rMaxCurv = rCurv;
                int iMaxCurv = iPoint;
                int m = 0, k = 0;
                if (!FindNextAndPrev(iNext, iCuspPrev, ref k, ref m)) break;
                for (int i = iPrev + 1; i <= m && FindNextAndPrev(i, iCuspPrev, ref iPrev, ref iNext); ++i)
                {
                    rCurv = GetCurvature(iPrev, i, iNext);
                    if (rCurv > rMaxCurv) { rMaxCurv = rCurv; iMaxCurv = i; }
                }
                _cusps.Add(iMaxCurv);
                iPoint = m + 1;
                iCuspPrev = iMaxCurv;
            }
            else if (rCurv < 0.035f)
            {
                iPoint = iNext;
            }
            else ++iPoint;
        }
        _cusps.Add(Count - 1);
    }

    /// <summary>对应上游 FindNextAndPrev：按跨距找前后探测点。</summary>
    private bool FindNextAndPrev(int iPoint, int iPrevCusp, ref int iPrev, ref int iNext)
    {
        bool bHasMore = true;
        if (iPoint >= Count) { bHasMore = false; iPoint = Count - 1; }

        for (iNext = iPoint + 1; iNext < Count; ++iNext)
            if (_nodes[iNext] - _nodes[iPoint] >= _span) break;
        if (iNext >= Count) { bHasMore = false; iNext = Count - 1; }

        for (iPrev = iPoint - 1; iPrevCusp <= iPrev; --iPrev)
            if (_nodes[iPoint] - _nodes[iPrev] >= _span) break;
        if (iPrev < 0) iPrev = 0;

        return bHasMore;
    }
}

/// <summary>WPF Bezier 的移植：把点串拟合成一串三次贝塞尔（带容差 + 尖点）。</summary>
internal sealed class FitBezier
{
    private readonly List<Vector2> _cp = new();
    /// <summary>每个控制点对应的原始输入点下标；控制点（非端点）记 -1。</summary>
    private readonly List<int> _cpSrc = new();
    private FitCuspData _data;

    /// <summary>对应上游 ConstructBezierState。</summary>
    public bool ConstructBezierState(FitCuspData data, float fitError)
    {
        _data = data;
        _cp.Clear();
        _cpSrc.Clear();
        return ConstructFromData(data, fitError);
    }

    public int ControlPointCount => _cp.Count;
    public Vector2 ControlPoint(int i) => _cp[i];
    public int ControlPointSource(int i) => _cpSrc[i];

    /// <summary>对应上游 ConstructFromData。</summary>
    private bool ConstructFromData(FitCuspData data, float fitError)
    {
        if (data.Count < 2) return false;

        AddBezierPoint(data.Xy(0), data.GetPointIndex(0));

        if (data.Count == 3)
        {
            AddParabola(data, 0);
            return true;
        }
        if (data.Count == 2)
        {
            AddLine(data, 0, 1);
            return true;
        }

        // 上游这里在 fitError≈0 时用 0.03×外接尺寸兜底；我们要求显式容差。
        if (fitError < 1e-6f) return false;

        data.SetTanLinks(0.5f * fitError);
        fitError *= fitError;   // 之后比较用平方

        bool done = false;
        int to = 0, nextCusp = 0, prevCusp = 0;
        bool isACusp = true;
        Vector2 tanEnd = default, tanStart = default;

        for (int from = 0; !done; from = to)
        {
            if (isACusp)
            {
                prevCusp = nextCusp;
                nextCusp = data.GetNextCusp(from);
                if (!data.Tangent(ref tanStart, from, prevCusp, nextCusp, false, true))
                    return false;
            }
            else
            {
                tanStart = -tanEnd;
            }

            to = from + 3;
            while (ExtendingRange(fitError, data, from, nextCusp, ref to, ref isACusp, ref done)) { }

            if (!data.Tangent(ref tanEnd, to, prevCusp, nextCusp, true, isACusp))
                return false;

            if (!AddBezierSegment(data, from, ref tanStart, to, ref tanEnd))
                return false;
        }
        return true;
    }

    /// <summary>对应上游 ExtendingRange（用 5 个点的四阶差分判"像不像三次曲线"）。</summary>
    private bool ExtendingRange(float error, FitCuspData data, int from, int nextCusp,
                                ref int to, ref bool cusp, ref bool done)
    {
        to++;
        cusp = true;
        done = to >= data.Count - 1;
        if (done) { to = data.Count - 1; cusp = true; return false; }

        cusp = to >= nextCusp;
        if (cusp) { to = nextCusp; return false; }

        int d = (to - from) / 4;
        int[] idx = { from, from + d, (to + from) / 2, to - d, to };
        return CoCubic(data, idx, error);
    }

    /// <summary>对应上游 AddBezierSegment。</summary>
    private bool AddBezierSegment(FitCuspData data, int from, ref Vector2 tanStart, int to, ref Vector2 tanEnd)
    {
        switch (to - from)
        {
            case 1:
                AddLine(data, from, to);
                return true;
            case 2:
                AddParabola(data, from);
                return true;
        }
        return AddLeastSquares(data, from, ref tanStart, to, ref tanEnd);
    }

    /// <summary>对应上游 AddParabola：三点定抛物线，升阶成三次。</summary>
    private void AddParabola(FitCuspData data, int from)
    {
        float t = (data.Node(from + 1) - data.Node(from)) /
                  (data.Node(from + 2) - data.Node(from));
        float s = 1 - t;
        if (t < .001f || s < .001f)
        {
            AddLine(data, from, from + 2);
            return;
        }

        float tt = 1 / t, ss = 1 / s;
        const float third = 1.0f / 3.0f;
        Vector2 p = (tt * ss) * data.Xy(from + 1);
        Vector2 b = third * (p + (1 - s * tt) * data.Xy(from) - (t * ss) * data.Xy(from + 1));
        AddBezierPoint(b, -1);
        b = third * (p - (s * tt) * data.Xy(from) + (1 - t * ss) * data.Xy(from + 2));
        AddBezierPoint(b, -1);
        AddSegmentPoint(data, from + 2);
    }

    /// <summary>对应上游 AddLine：直线也是三次贝塞尔（控制点 1/3、2/3）。</summary>
    private void AddLine(FitCuspData data, int from, int to)
    {
        const float third = 1.0f / 3.0f;
        AddBezierPoint((2 * data.Xy(from) + data.Xy(to)) * third, -1);
        AddBezierPoint((data.Xy(from) + 2 * data.Xy(to)) * third, -1);
        AddSegmentPoint(data, to);
    }

    /// <summary>对应上游 AddLeastSquares：固定两端切线，最小二乘解两个控制点长度。</summary>
    private bool AddLeastSquares(FitCuspData data, int from, ref Vector2 v, int to, ref Vector2 w)
    {
        double a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
        double b11 = 0, b12 = 0, b21 = 0, b22 = 0;

        for (int j = from + 1; j < to; j++)
        {
            double tj = (data.Node(j) - data.Node(from)) / (data.Node(to) - data.Node(from));
            double tj2 = tj * tj;
            double rj = 1 - tj;
            double rj2 = rj * rj;

            double f0j = rj2 * rj;
            double f1j = 3 * rj2 * tj;
            double f2j = 3 * rj * tj2;
            double f3j = tj2 * tj;

            a11 += f1j * f1j;
            a22 += f2j * f2j;
            a12 += f1j * f2j;

            b11 -= (f0j + f1j) * f1j;
            b12 -= (f2j + f3j) * f1j;
            b1 += f1j * Vector2.Dot(data.Xy(j), v);

            b21 -= (f0j + f1j) * f2j;
            b22 -= (f2j + f3j) * f2j;
            b2 += f2j * Vector2.Dot(data.Xy(j), w);
        }

        a12 *= Vector2.Dot(v, w);
        b1 += Vector2.Dot(v, data.Xy(from)) * b11 + Vector2.Dot(v, data.Xy(to)) * b12;
        b2 += Vector2.Dot(w, data.Xy(from)) * b21 + Vector2.Dot(w, data.Xy(to)) * b22;

        double s = b1 * a22 - b2 * a12;
        double u = b2 * a11 - b1 * a12;
        double det = a11 * a22 - a12 * a12;
        bool accept = Math.Abs(det) > Math.Abs(s) * 1e-12 && Math.Abs(det) > Math.Abs(u) * 1e-12;
        if (accept)
        {
            s /= det;
            u /= det;
            accept = s > 1.0e-6 && u > 1.0e-6;
        }
        if (!accept) s = u = (data.Node(to) - data.Node(from)) / 3;

        AddBezierPoint(data.Xy(from) + (float)s * v, -1);
        AddBezierPoint(data.Xy(to) + (float)u * w, -1);
        AddSegmentPoint(data, to);
        return true;
    }

    /// <summary>对应上游 CoCubic：四阶差分平方 &lt; 容差² 就继续延长这一段。</summary>
    private static bool CoCubic(FitCuspData data, int[] i, float fitError)
    {
        float d04 = data.Node(i[4]) - data.Node(i[0]);
        float d01 = d04 / (data.Node(i[1]) - data.Node(i[0]));
        float d02 = d04 / (data.Node(i[2]) - data.Node(i[0]));
        float d03 = d04 / (data.Node(i[3]) - data.Node(i[0]));
        float d12 = d04 / (data.Node(i[2]) - data.Node(i[1]));
        float d13 = d04 / (data.Node(i[3]) - data.Node(i[1]));
        float d14 = d04 / (data.Node(i[4]) - data.Node(i[1]));
        float d23 = d04 / (data.Node(i[3]) - data.Node(i[2]));
        float d24 = d04 / (data.Node(i[4]) - data.Node(i[2]));
        float d34 = d04 / (data.Node(i[4]) - data.Node(i[3]));
        Vector2 p = d01 * d02 * d03 * data.Xy(i[0])
                  - d01 * d12 * d13 * d14 * data.Xy(i[1])
                  + d02 * d12 * d23 * d24 * data.Xy(i[2])
                  - d03 * d13 * d23 * d34 * data.Xy(i[3])
                  + d14 * d24 * d34 * data.Xy(i[4]);
        return p.LengthSquared() < fitError;
    }

    private void AddBezierPoint(Vector2 point, int srcIndex)
    {
        _cp.Add(point);
        _cpSrc.Add(srcIndex);
    }

    private void AddSegmentPoint(FitCuspData data, int index)
    {
        _cp.Add(data.Xy(index));
        _cpSrc.Add(data.GetPointIndex(index));
    }
}

/// <summary>
/// 引擎入口：把一串点（+压力）拟合成贝塞尔段，写进静态复用的 <see cref="FitSeg"/> 数组。
/// </summary>
internal static class WpfInkFit
{
    /// <summary>拟合容差（画布像素）。默认 0.5px——够吃掉细笔放大可见的微折线，又不至于改形。</summary>
    public static float TolerancePx = 0.5f;

    private static FitSeg[] _out = new FitSeg[512];
    public static int Count { get; private set; }
    public static FitSeg At(int i) => _out[i];

    public static bool Fit(IReadOnlyList<Vector2> pts, IReadOnlyList<float> pressures)
    {
        Count = 0;
        if (pts == null || pts.Count < 2) return false;

        var data = new FitCuspData();
        data.Analyze(pts, TolerancePx);

        var bezier = new FitBezier();
        if (!bezier.ConstructBezierState(data, TolerancePx)) return false;

        int cps = bezier.ControlPointCount;
        int segCount = cps / 3;
        // 段数 = (控制点数 - 1) / 3；构造失败/退化时退回（调用方会退回过点曲线）。
        if (segCount <= 0) return false;
        if (_out.Length < segCount) Array.Resize(ref _out, Math.Max(segCount, _out.Length * 2));

        float prevR = pressures != null && pressures.Count > 0 ? pressures[0] : 0.5f;
        for (int k = 0; k < segCount; k++)
        {
            int i0 = k * 3;
            int src = bezier.ControlPointSource(i0 + 3);
            float r1 = (pressures != null && src >= 0 && src < pressures.Count)
                ? pressures[src] : prevR;
            var seg = new FitSeg
            {
                P0 = bezier.ControlPoint(i0),
                C1 = bezier.ControlPoint(i0 + 1),
                C2 = bezier.ControlPoint(i0 + 2),
                P1 = bezier.ControlPoint(i0 + 3),
                R0 = prevR,
                R1 = r1,
            };

            // **失控兜底**（2026-10-04，用户实测 0.3/0.8 容差"线乱飞"后加）：
            // 控制点离端点的长度不许超过弦长的 1.5 倍（+容差）；否则整条拟合作废，
            // 调用方会退回过点曲线。宁可没效果，绝不允许画出乱线。
            float chord = Vector2.Distance(seg.P0, seg.P1);
            float maxHandle = chord * 1.5f + TolerancePx;
            if (Vector2.Distance(seg.P0, seg.C1) > maxHandle ||
                Vector2.Distance(seg.P1, seg.C2) > maxHandle)
            {
                Count = 0;
                return false;
            }

            _out[k] = seg;
            prevR = r1;
        }
        Count = segCount;
        return Count > 0;
    }
}

/*
 * =====================================================================================
 *  本文件移植自 dotnet/wpf（MIT License）：
 *
 *  Copyright (c) .NET Foundation and Contributors
 *
 *  Permission is hereby granted, free of charge, to any person obtaining a copy
 *  of this software and associated documentation files (the "Software"), to deal
 *  in the Software without restriction, including without limitation the rights
 *  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 *  copies of the Software, and to permit persons to whom the Software is
 *  furnished to do so, subject to the following conditions:
 *
 *  The above copyright notice and this permission notice shall be included in all
 *  copies or substantial portions of the Software.
 * =====================================================================================
 */
