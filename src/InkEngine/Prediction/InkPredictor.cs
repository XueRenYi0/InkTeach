namespace InkEngine;

/// <summary>预测出来的一个未来点（屏幕坐标 + 它对应的时间）。</summary>
internal struct PredictedPoint
{
    public float X, Y;
    public double TimeMs;
}

/// <summary>
/// 笔迹预测：把"这一帧之后笔尖会在哪"算出来，交给委托墨迹轨迹去画，
/// 让墨的末端不是过去而是现在。
///
/// **模型**（和 Chromium `ui/base/prediction/linear_predictor.cc` 同一族，BSD-3）：
///     一阶： p(t) = p0 + v·τ
///     二阶： p(t) = p0 + v·τ + ½·a·τ²
/// 速度由最近两点算，加速度由最近两个速度算——**都用每一点自己的硬件时标**，
/// 不用"消息到达时刻"（那是投递节奏，不是笔的节奏）。
///
/// **这份实现的重点不是公式，是"别甩墨"**（见 调研-压感与预测-原理.md 第三节）：
///   · 地平线限幅：预测时长 ≤ MaxHorizonMs（默认 10 ms，允许 8~15）
///   · 整体阻尼 Damping（默认 0.7）：宁可少补，不要冲过头
///   · 加速度单独衰减 AccelDamping（默认 0.4）后再限幅——差分出来的项噪声最大
///   · 慢速不预测（速度 < MinSpeed）：慢写时预测没有收益，只有抖动
///   · 反向/急转检测：新点与当前速度反向时丢掉速度与加速度
///   · 断笔重置：相邻采样间隔 > MaxGapMs（20 ms，Chromium 的 kMaxTimeDelta）
///   · 总位移上限 MaxDistance：兜底，防止极端速度下的长尾
///
/// 不做的事：不滤位置（用户定的"指针报什么就画什么"），不预测压力/倾角
/// （委托轨迹的点结构只有 x/y/radius，压力还没参与渲染）。
/// </summary>
internal sealed class InkPredictor
{
    // ---- 可调参数（命令行可覆盖）----------------------------------------

    /// <summary>预测地平线（毫秒）。默认 10，允许 8~15。</summary>
    public double HorizonMs { get; set; } = 10.0;
    /// <summary>
    /// 整体阻尼：预测位移乘这个系数。
    /// 0.80 是**用真实笔迹数据扫出来的**（UCI Character Trajectories，48.7 万个采样点，
    /// 见 测试-压感与预测.md 与 `--predictdata`）：在"最坏 1% 情形下超前不超过 0.8 倍滞后"
    /// 这个约束里，它把平均滞后吃掉了 56%，而阻尼 1.0 只多 6 个点却把最坏超前拉到 1.3 倍。
    /// </summary>
    public float Damping { get; set; } = 0.8f;
    /// <summary>加速度项衰减。</summary>
    public float AccelDamping { get; set; } = 0.4f;
    /// <summary>低于这个速度（px/ms）不预测。</summary>
    public float MinSpeed { get; set; } = 0.02f;
    /// <summary>
    /// 速度平滑强度（0 = 不平滑）。速度是差分出来的，真实手写里抖得厉害；
    /// Chromium 也把 filter 单独做了一层（`one_euro_filter`）。
    /// 这里的取值是用真实数据扫出来的，见 测试-压感与预测.md。
    /// </summary>
    public float VelocitySmoothing { get; set; } = 0f;
    /// <summary>预测段相对最后一点的最大位移（px）。</summary>
    public float MaxDistance { get; set; } = 12f;
    /// <summary>相邻采样间隔超过这个值就当断笔。</summary>
    public double MaxGapMs { get; set; } = 20.0;
    /// <summary>一次最多给出几个预测点（按采样间隔铺满地平线）。</summary>
    public int MaxPoints { get; set; } = 4;

    public const double MinHorizonMs = 8.0;
    public const double MaxHorizonMs = 15.0;

    // ---- 状态 -------------------------------------------------------------

    private float _x0, _y0, _x1, _y1, _x2, _y2;   // 最近三点（旧→新）
    private double _t0, _t1, _t2;
    private int _count;

    private float _vx, _vy;         // 最近一次速度（px/ms）
    private float _px, _py;         // 上一次的速度（算加速度用）
    private float _ax, _ay;         // 加速度（px/ms²）
    private bool _hasPrevVelocity;

    /// <summary>预测点的时间偏移（复用，避免每帧分配）。</summary>
    private readonly double[] _taus = new double[8];

    /// <summary>最近一次算出来的速度大小（诊断/测试用）。</summary>
    public float Speed => MathF.Sqrt(_vx * _vx + _vy * _vy);
    /// <summary>最近一次算出来的加速度大小（诊断/测试用）。</summary>
    public float Acceleration => MathF.Sqrt(_ax * _ax + _ay * _ay);
    /// <summary>当前累积的采样点数（诊断用）。</summary>
    public int Count => _count;

    public void Reset()
    {
        _count = 0;
        _vx = _vy = _px = _py = _ax = _ay = 0f;
        _hasPrevVelocity = false;
    }

    /// <summary>喂一个采样点（屏幕坐标 + 它自己的时间）。</summary>
    public void Add(float x, float y, double timeMs)
    {
        if (_count > 0 && (timeMs - _t2) > MaxGapMs)
            Reset();                       // 断笔：间隔太大，重新起一条轨迹

        _x0 = _x1; _y0 = _y1; _t0 = _t1;
        _x1 = _x2; _y1 = _y2; _t1 = _t2;
        _x2 = x; _y2 = y; _t2 = timeMs;
        if (_count < 3) _count++;

        if (_count < 2) return;

        float dt = (float)(_t2 - _t1);
        if (dt <= 0f) return;              // 同一时刻的两个点，速度无从谈起

        _px = _vx; _py = _vy;
        float rawVx = (_x2 - _x1) / dt;
        float rawVy = (_y2 - _y1) / dt;
        // 速度平滑：只有不是第一个速度（_hasPrevVelocity）时才和上一次平滑值混合，
        // 否则起笔那一下会被 0 拖慢。
        float s = Math.Clamp(VelocitySmoothing, 0f, 0.9f);
        if (s > 0f && _hasPrevVelocity)
        {
            _vx = rawVx * (1f - s) + _vx * s;
            _vy = rawVy * (1f - s) + _vy * s;
        }
        else
        {
            _vx = rawVx;
            _vy = rawVy;
        }

        // 反向/急转：速度与上一次方向相反 → 丢掉速度和加速度，
        // 宁可这一帧不预测，也不要在拐弯处甩出去一截。
        if (_hasPrevVelocity && (rawVx * _px + rawVy * _py) < 0f)
        {
            _vx = _vy = 0f;
            _ax = _ay = 0f;
            return;
        }

        if (_hasPrevVelocity && _count >= 3)
        {
            _ax = (_vx - _px) / dt;
            _ay = (_vy - _py) / dt;
        }
        else
        {
            _ax = _ay = 0f;
        }
        _hasPrevVelocity = true;
    }

    /// <summary>
    /// 生成预测点（时间序，全部在最后一点之后）。
    /// </summary>
    /// <returns>写进 <paramref name="outPoints"/> 的个数；0 = 不预测</returns>
    public int Predict(PredictedPoint[] outPoints)
    {
        if (_count < 2 || outPoints == null || outPoints.Length == 0) return 0;

        float speed = Speed;
        if (speed < MinSpeed) return 0;                 // 慢写：不预测

        double horizon = Math.Clamp(HorizonMs, MinHorizonMs, MaxHorizonMs);

        // 采样间隔：优先用真实间隔，异常时退回 8 ms（Chromium 的 kTimeInterval）。
        double interval = 0;
        if (_count >= 2)
        {
            double span = (_t2 - (_count >= 3 ? _t0 : _t1)) / (_count >= 3 ? 2 : 1);
            if (span > 0.1 && span <= MaxGapMs) interval = span;
        }
        if (interval <= 0) interval = 8.0;

        // 预测点按采样间隔铺开，**最后一点必须落在正地平线上**：
        // 只铺到"离地平线最近的那个整数倍"会白白少补一截——
        // 实测（真实笔迹数据，见 测试-压感与预测.md）：5 ms 采样 + 8 ms 地平线时，
        // 只铺到 5 ms 的话"吃到"只有 40%，补上 8 ms 那一点之后才吃满。
        int cap = Math.Min(Math.Min(MaxPoints, outPoints.Length), _taus.Length);
        int steps = (int)Math.Floor(horizon / interval);
        if (steps < 0) steps = 0;
        if (steps > cap) steps = cap;

        int count = 0;
        for (int i = 1; i <= steps; i++) _taus[count++] = interval * i;
        if (count == 0 || horizon - _taus[count - 1] > 1e-6)
        {
            if (count < cap) _taus[count++] = horizon;
            else if (count > 0) _taus[count - 1] = horizon;
        }

        for (int i = 0; i < count; i++)
        {
            double tau = _taus[i];
            float t = (float)tau;
            float dx = _vx * t * Damping + 0.5f * _ax * t * t * AccelDamping;
            float dy = _vy * t * Damping + 0.5f * _ay * t * t * AccelDamping;

            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > MaxDistance)
            {
                float k = MaxDistance / len;
                dx *= k; dy *= k;
            }

            outPoints[i] = new PredictedPoint
            {
                X = _x2 + dx,
                Y = _y2 + dy,
                TimeMs = _t2 + tau,
            };
        }
        return count;
    }

    /// <summary>地平线/参数越界时收进合法范围（命令行传进来的值也走这里）。</summary>
    public void ClampHorizon() => HorizonMs = Math.Clamp(HorizonMs, MinHorizonMs, MaxHorizonMs);
}
