// DlPredictController：DL 预测的引擎侧控制器（P1，`--predict3`）。
//
// 职责（与离线评测 `_ml-predict` 逐字对齐）：
//   · 特征管线：8 维/步（dx/S, dy/S, dt/5ms, sp, f/fscale, f_ok, a_n, tr_n）；
//   · **批点重定标**：同批到达的点按 r≈4.7ms 前向填充（治"一帧一批、批内同毫秒"，
//     桌面/手写板/学校三种输入栈都实测存在）；
//   · S（归一化尺度）= 最近步长的滚动中位（clamp 0.5~6，warmup 2.0）；
//   · 每帧一次：喂满 Hist=20 步后调 InkDlPredictor 前向 → 沿点列采样出显示点
//     （截断到光子滞后；γ 阻尼默认 0.9）。
//
// 失败当没有（规矩三）：任何异常由调用方 try/catch 兜住并退回 B4。
using System;
using System.Collections.Generic;

namespace InkEngine;

internal sealed class DlPredictController
{
    /// <summary>训练口径：dt0 = 5ms。</summary>
    private const float Dt0 = 5.0f;
    /// <summary>批点重定标的物理采样间隔估计（ms/点；桌面实测 4.67、手写板 5.21）。</summary>
    private const float RetimeMs = 4.7f;

    private readonly InkDlPredictor _model;
    private readonly int _hist;
    private readonly int _inDim = 8;

    // ---- 特征环（最旧 → 最新）----
    private readonly float[] _ring;      // hist*8
    private int _filled;

    // ---- S（滚动中位）----
    private readonly float[] _stepRing = new float[24];
    private int _stepCount, _stepPos;
    private float _s = 2.0f;

    // ---- fscale（滚动中位，压力为正的样本）----
    private readonly float[] _prRing = new float[32];
    private int _prCount, _prPos;
    private float _fScale = 0.5f;

    // ---- 重定标状态 ----
    private readonly List<(float X, float Y, float P, double T, bool HasP)> _pending = new();
    private double _lastAssignedMs = double.NaN;
    private double _lastBatchNowMs = double.NaN;
    private bool _lastHasP = true;             // 最近提交点是否有压感（无压感路 γ 收紧）
    private readonly float[] _rRing = new float[16];
    private int _rCount, _rPos;
    private float _rAdapt = RetimeMs;          // 板上自适应节距（单点流 → 相邻点距；批流 → 批距/批大小）
    private bool _havePrev;
    private float _lastX, _lastY;
    private double _lastT;
    private float _prev2X, _prev2Y;
    private double _prev2T;

    // ---- 调试/对齐用覆盖（探针对拍时用；产品路径为 null）----
    public float? FixedS;
    public float? FixedFScale;

    /// <summary>整体阻尼（0.9 = 保守）。</summary>
    public float Gamma { get; set; } = 0.9f;
    /// <summary>光子滞后（ms；引擎每帧同步 = 帧周期，对齐产品"Present→上屏 1 帧"）。</summary>
    public float PhotonMs { get; set; } = 1000f / 90f;
    /// <summary>事件门控（引擎侧开启）：最近一步转角速率过大 → 本帧不画尾（对标 B4 急转丢速）。
    /// 探针对拍保持 false（验证纯管线）。</summary>
    public bool EventGate;
    /// <summary>无压感/鼠标路的 γ 缩放（引擎侧 0.85——整数坐标+低报点噪声大；探针对拍保持 1.0）。</summary>
    public float NoPressureGammaScale = 1.0f;

    public bool Ready => _filled >= _hist;
    public int FeedCount => _filled;

    public DlPredictController(InkDlPredictor model)
    {
        _model = model;
        _hist = model.Hist;
        _ring = new float[_hist * _inDim];
    }

    public void Reset()
    {
        _pending.Clear();
        _filled = 0;
        _stepCount = _stepPos = 0;
        _prCount = _prPos = 0;
        _s = 2.0f;
        _fScale = 0.5f;
        _lastAssignedMs = double.NaN;
        _lastBatchNowMs = double.NaN;
        _rCount = _rPos = 0;
        _rAdapt = RetimeMs;
        _havePrev = false;
        _havePrev2 = false;
    }

    /// <summary>喂一个点（引擎的真实坐标与处理时刻；重定标在本类内部做）。</summary>
    public void Add(float x, float y, float p, double nowMs, bool hasPressure)
    {
        _pending.Add((x, y, p, nowMs, hasPressure));
    }

    /// <summary>
    /// 把当前批提交进特征环。**后向锚定**：批内末点 = 到达时刻（单点流 = 原时标透传），
    /// 向前按自适应节距 r̂ 展开（r̂ = 最近批距/批大小的滚动中位，clamp 2~8ms）。
    /// 等效"还原批内物理节奏"；批过密时按最小推进兜底。
    /// </summary>
    private void CommitPending()
    {
        if (_pending.Count == 0) return;
        int k = _pending.Count;
        double T = _pending[k - 1].T;

        // r̂ 更新：批距 / 批大小
        if (!double.IsNaN(_lastBatchNowMs))
        {
            double gap = T - _lastBatchNowMs;
            if (gap > 0.5 && gap < 200)
            {
                float cand = (float)(gap / k);
                if (cand >= 2.0f && cand <= 8.0f)
                {
                    _rRing[_rPos] = cand;
                    _rPos = (_rPos + 1) % _rRing.Length;
                    if (_rCount < _rRing.Length) _rCount++;
                    _rAdapt = RollingMedian(_rRing, _rCount);
                }
            }
        }
        _lastBatchNowMs = T;

        double prevOut = _lastAssignedMs;
        double t = 0;
        for (int m = 0; m < k; m++)
        {
            var pt = _pending[m];
            double desired = T - (k - 1 - m) * _rAdapt;
            if (double.IsNaN(prevOut)) t = desired;
            else if (T > prevOut + 0.3) t = Math.Max(prevOut + 0.3, desired);
            else t = prevOut + 0.3 * (m + 1);       // 时间没走：给最小推进
            PushStep(pt.X, pt.Y, pt.P, t, pt.HasP);
            prevOut = t;
        }
        _lastAssignedMs = t;
        _pending.Clear();
    }

    private void PushStep(float x, float y, float p, double t, bool hasP)
    {
        if (!_havePrev)
        {
            _havePrev = true;
            _lastX = x; _lastY = y; _lastT = t;
            return;
        }
        // 步长 → S 估计
        float dxp = x - _lastX, dyp = y - _lastY;
        float dist = MathF.Sqrt(dxp * dxp + dyp * dyp);
        _stepRing[_stepPos] = dist;
        _stepPos = (_stepPos + 1) % _stepRing.Length;
        if (_stepCount < _stepRing.Length) _stepCount++;
        float sEff = FixedS ?? RollingMedian(_stepRing, _stepCount);
        _s = Math.Clamp(sEff, 0.5f, 6.0f);

        double dt = Math.Max(1e-3, t - _lastT);
        float sp = dist / _s / (float)(dt / Dt0);

        float aN = 0f, trN = 0f;
        if (_havePrev2)
        {
            float px = _lastX - _prev2X, py = _lastY - _prev2Y;
            double dtp = Math.Max(1e-3, _lastT - _prev2T);
            float dpp = MathF.Sqrt(px * px + py * py);
            float spp = dpp / _s / (float)(dtp / Dt0);
            aN = (sp - spp) / (float)(dt / Dt0);
            float n1 = dpp, n2 = dist;
            if (n1 > 1e-6f && n2 > 1e-6f)
            {
                float cosv = Math.Clamp((px * dxp + py * dyp) / (n1 * n2), -1f, 1f);
                trN = MathF.Acos(cosv) / (float)(dt / Dt0);
            }
        }

        // 压力（滚动中位做 fscale）
        float fi = 0f, fok = 0f;
        if (hasP)
        {
            if (p > 0f)
            {
                _prRing[_prPos] = p;
                _prPos = (_prPos + 1) % _prRing.Length;
                if (_prCount < _prRing.Length) _prCount++;
            }
            float fs = FixedFScale ?? (_prCount > 0 ? RollingMedian(_prRing, _prCount) : 0.5f);
            _fScale = fs > 1e-6f ? fs : 0.5f;
            fi = p / _fScale;
            fok = 1f;
        }

        // 推进环形（丢最旧）
        if (_filled == _hist)
        {
            Array.Copy(_ring, _inDim, _ring, 0, (_hist - 1) * _inDim);
            _filled = _hist - 1;
        }
        int off = _filled * _inDim;
        _ring[off + 0] = dxp / _s;
        _ring[off + 1] = dyp / _s;
        _ring[off + 2] = (float)(dt / Dt0);
        _ring[off + 3] = sp;
        _ring[off + 4] = fi;
        _ring[off + 5] = fok;
        _ring[off + 6] = aN;
        _ring[off + 7] = trN;
        _filled++;

        _prev2X = _lastX; _prev2Y = _lastY; _prev2T = _lastT;
        _havePrev2 = true;
        _lastX = x; _lastY = y; _lastT = t;
        _lastHasP = hasP;
    }

    private bool _havePrev2;

    private static float RollingMedian(float[] buf, int count)
    {
        Span<float> tmp = stackalloc float[64];
        int n = Math.Min(count, buf.Length);
        for (int i = 0; i < n; i++) tmp[i] = buf[i];
        tmp.Slice(0, n).Sort();
        return (n & 1) == 1 ? tmp[n / 2] : 0.5f * (tmp[n / 2 - 1] + tmp[n / 2]);
    }

    /// <summary>
    /// 每帧调一次：提交批点、前向、输出显示点（≤ maxPoints 个，沿预测点列、γ 阻尼、截断到光子）。
    /// 返回点数；0 = 本帧不画（warmup/无数据）。
    /// </summary>
    public int PredictPoints(PredictedPoint[] outPts)
    {
        CommitPending();
        if (!Ready || _hist > 20) return 0;

        // 事件门控（对标 B4 急转丢速）：最近一步转角速率过急 → 本帧不画尾（下一帧自动恢复）。
        if (EventGate)
        {
            float tr = _ring[(_filled - 1) * _inDim + 7];
            if (tr > 1.2f) return 0;           // ≈69°/步
        }

        Span<float> x = stackalloc float[20 * 8];
        int need = _hist * _inDim;
        // 环形此刻按"最旧→最新"连续排列（满环时上面做过搬移）
        for (int i = 0; i < need; i++) x[i] = _ring[i];
        Span<float> mu = stackalloc float[20 * 2];
        Span<float> lv = stackalloc float[20 * 2];
        _model.Predict(x.Slice(0, need), mu.Slice(0, _model.Horizons.Length * 2), lv, out _);

        float gammaEff = Gamma * (_lastHasP ? 1f : NoPressureGammaScale);
        float tUse = Math.Min(PhotonMs, _model.Horizons[^1]);
        if (tUse <= 0.5f) return 0;
        int n = Math.Min(outPts.Length, 3);
        for (int j = 1; j <= n; j++)
        {
            float tj = tUse * j / n;
            InterpMu(mu, tj, out float mx, out float my);
            outPts[j - 1] = new PredictedPoint
            {
                X = _lastX + gammaEff * mx * _s,
                Y = _lastY + gammaEff * my * _s,
                TimeMs = _lastT + tj,
            };
        }
        return n;
    }

    /// <summary>在视界点列上按时间插值（0 点 = 原点；超末点截断）。</summary>
    private void InterpMu(ReadOnlySpan<float> mu, float t, out float mx, out float my)
    {
        var hz = _model.Horizons;
        int K = hz.Length;
        if (t <= hz[0])
        {
            float a = t / hz[0];
            mx = mu[0] * a; my = mu[1] * a;
            return;
        }
        if (t >= hz[K - 1])
        {
            mx = mu[(K - 1) * 2]; my = mu[(K - 1) * 2 + 1];
            return;
        }
        int j = 0;
        while (hz[j + 1] < t) j++;
        float w = (t - hz[j]) / (hz[j + 1] - hz[j]);
        mx = mu[j * 2] * (1 - w) + mu[(j + 1) * 2] * w;
        my = mu[j * 2 + 1] * (1 - w) + mu[(j + 1) * 2 + 1] * w;
    }
}
