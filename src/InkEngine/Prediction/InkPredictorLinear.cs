// ============================================================================
//  InkPredictorLinear.cs —— 线性预测（Chromium 同族）+ 时间组速度 + 弧长安全层
//                            **唯一预测器**（2026-10-15 接入；2026-11-17 换速度估计）
//
//  来源：用户 2026-10-15 提供的 `线性预测-Chromium.zip`（LinearPredictor.cs）。
//  对照 Chromium 源码：ui/base/prediction/linear_predictor.cc（BSD-3）
//      p(t) = p0 + v·t + ½·a·t²
//
//  【2026-11-17 速度估计：两点差分 → 时间组 + 加权最小二乘】
//  病灶（Opus 5.5 评审 §1/P3，经源码核对成立）：
//    · 旧实现 `_vx = (x2 - x1) / dt`，`dt <= 0` 时 `return`——**速度与方向都不更新**。
//    · 真机是**批量投递**：一条 WM_POINTER 里合并 n 个点、批内时标相同（dt = 0）。
//      于是本批第 2..n 点全被跳过，下一批首点拿"整批间隔"去除"一个子步位移"，
//      速度被低估约 n 倍、方向取的是本批**最旧**那一小段（拐弯时就是旧方向）。
//    · `Predict` 还只判 `_count < 2`、不看速度是否有效 → 时标不可用时**拿陈旧速度外推**。
//
//  修法（照搬成熟实现，不自造）：
//    · **时间组**：与上一组时间差 < `TieMs` 的点并入同组、只刷新位置
//      —— AOSP `VelocityTracker`（libs/input/VelocityTracker.cpp）把同一 eventTime
//      的合并点当一条处理的同族做法；
//    · **加权最小二乘一阶斜率**：AOSP `LeastSquaresVelocityTrackerStrategy` 的
//      LSQ 家族（默认策略就是 LSQ2）——不用相邻差分求速度是这类工程的共性结论；
//    · **速度无效就不预测**：AOSP `getVelocity` 在数据不足时返回空，不返回陈旧值。
//      我们对应 `_hasV == false → Predict 返回 0`。
//    · 40ms 无新点的"停住"判定：本类原有的 `MaxGapMs = 40` 重置已经等价
//      （AOSP `ASSUME_POINTER_STOPPED_TIME = 40ms`），这次不改。
//
//  与 AOSP 的**差异**（按场景裁剪，理由写在这里）：
//    · 阶数取 **1**（只要速度），不搬 degree=2：24ms 地平线上的二次拟合会过拟合；
//    · 窗口 `VelWindowMs = 24`（AOSP 是 100ms，那是松手甩动场景；板书要 ≤40ms 的反应）；
//    · 权重 **1/(1+i)²**（i = 距最新组的组序）：越旧衰减越快。一阶窗口里最新两组
//      占约 87% 权重，急转时不会被历史段"抹平"——这是实测调出来的，不是照抄常数，
//      扫参见 `--predlinetest` 里的钉子与 `调研/调研-线性预测-Chromium.md`。
//
//  以下全部**没动**（2026-10-15 交付件口径，A/B 结论仍然有效）：
//    一阶核心、径向限幅 12px、弧长截断 6px、方向自适应门控（DirAdaptive/DirThresh/DirFloor）、
//    MaxPoints 8、MaxGapMs 40、地平线按刷新率缩放。
//
//  零依赖 · 无反射 · Native AOT 直接编译 · 时间毫秒 / 长度像素
// ============================================================================
namespace InkEngine;

/// <summary>预测出来的一个未来点（屏幕坐标 + 它对应的时间）。</summary>
internal struct PredictedPoint
{
    public float X, Y;
    public double TimeMs;
}

/// <summary>
/// 线性预测器（唯一实现）。喂点 → 每帧 <see cref="Predict"/> 取一串未来点，
/// 交给显示层限速后画成"预测尾"。
///
/// 纪律（没变）：只算显示用的偏移，不进文档/存档/命中；
/// 抬笔、换笔、换档都会 <see cref="Reset"/>。
/// </summary>
internal sealed class InkPredictorLinear
{
    // ---- 可调参数（产品里不再暴露给设置/命令行；离线评测工具会用）----

    /// <summary>
    /// 预测地平线（毫秒）。**由引擎按刷新率自动缩放**（2026-10-16 用户定）：
    /// 窗口建好后设为 `clamp(0.6 × 帧周期ms, 4, 10)`——60Hz=10ms、90Hz≈6.7ms、120Hz=5ms。
    /// 取舍口径："宁可少补一点（更不甩），不要多补"。
    /// 2026-11-17：出厂值由 21.3 改为 **10.0**——21.3（当年实测的端到端延迟）只在
    /// **拿不到刷新率**时才生效，而那种时候按"延迟近似"补 21.3ms 恰好违背上面那条口径。
    /// 现在出厂值 = 60Hz 的缩放结果，两处兜底同源（见 <see cref="Engine.UpdatePredictTail"/>）。
    /// </summary>
    public double HorizonMs = 10.0;
    /// <summary>
    /// true = 二阶（含 ½at²，Chromium 原式）；false = 一阶（纯线性 p0+vt）。
    /// **默认 false（一阶）**——A/B 实测一阶明显更好（见文件头）；想回 Chromium 原式设 true。
    /// </summary>
    public bool UseAcceleration = false;
    /// <summary>一次最多几个预测点。</summary>
    public int MaxPoints = 8;
    /// <summary>
    /// 固定限幅（px）：每个预测点沿「笔尖→该点」射线压到半径内（**径向**限幅）。
    /// 12 = 交付件默认（配合下面的弧长截断，实际外沿由 ArcCap 决定）。
    /// </summary>
    public double MaxDistance = 12.0;
    /// <summary>
    /// 【安全层 · 弧长截断（px），0 = 关闭】默认 6——**这是"最坏偏离 6px 硬保证"的来源**。
    ///
    /// 与 <see cref="MaxDistance"/> 的区别：
    ///   · MaxDistance 是**径向**限幅——会改变预测形状（直线时点塌缩、弧线被拉扁）；
    ///   · ArcCap 是**弧长**截断——从笔尖起沿预测折线累积长度，超过 cap 就在该段内
    ///     线性插值切断并停止。**保留预测的形状**，只砍总长度。
    /// </summary>
    public double ArcCap = 6.0;
    /// <summary>
    /// 【安全层 · 方向自适应强度，0 = 关闭】默认 4。
    /// 用「最近几段行进方向的平均变化（弧度/段）」衡量运动不平稳度，
    /// 方向越乱，预测尾越短：cap_eff = ArcCap × max(DirFloor, 1 − da×(inst − DirThresh))。
    /// </summary>
    public float DirAdaptive = 4.0f;
    /// <summary>方向不稳度起算阈值（弧度/段）。低于它完全不收缩，正常书写不受影响。</summary>
    public float DirThresh = 0.35f;
    /// <summary>
    /// 方向自适应收缩下限。**0 = 门控**：方向极度不稳时这一帧直接不预测
    /// （A/B：只有下限为 0 才真正改善夹角；0.5/1.0 与关闭无区别）。
    /// </summary>
    public float DirFloor = 0f;
    /// <summary>方向不稳度 EMA 的平滑系数。越大反应越快。</summary>
    public float DirEmaA = 0.3f;
    /// <summary>低于这个速度（px/ms）不预测（0 = 不设门槛）。</summary>
    public float MinSpeed = 0.0f;
    /// <summary>相邻采样间隔超过这个值（ms）就当断笔（同 v6 的 40ms 口径；也 = AOSP 的"停住"判定）。</summary>
    public double MaxGapMs = 40.0;

    // ---- 2026-11-17 新增：时间组 + 最小二乘速度 ----

    /// <summary>
    /// 与上一时间组相差小于它（ms）的点并入**同一时间组**（只刷新位置）。
    /// 0.5ms：比一个硬件时标刻度的抖动小、比"批内时标全等"的 0 大。
    /// </summary>
    public double TieMs = 0.5;
    /// <summary>
    /// 最小二乘窗口（ms）。24 ≈ 60Hz 下 1.5 批、250Hz 笔 6 个点。
    /// **0 = 退回旧的两点差分**（回退开关，见文件头）。
    /// </summary>
    public double VelWindowMs = 24.0;
    /// <summary>窗口内最多用几个时间组（防止慢写被 20 组线性化拉平，也让急转反应不迟）。</summary>
    public int VelMaxGroups = 4;
    /// <summary>
    /// 【2026-11-17 方向约束】当窗口 LSQ 出的速度方向与最近两个时间组的真实位移方向
    /// 差异过大时，退回直接用这两组的差分速度。与 AOSP/Chromium `LSQ2_RESTRICTED`
    /// 同族：方向与窗口主方向差异过大就不信长窗。0 = 关闭约束。
    /// </summary>
    public float TurnRestrictDeg = 35f;
    /// <summary>
    /// 【2026-11-17】触发方向约束的最小位移（px）。最近两个时间组位移太小说明方向不可信，
    /// 此时不启用约束，仍用 LSQ 速度。
    /// </summary>
    public float TurnRestrictMinPx = 3f;
    /// <summary>
    /// 方向样本的最小段长（px）。同组之间位移小于它**不更新**方向不稳度：
    /// 整像素量化下 1px 段的方向分辨率约为 1/段长（45° 量级），那是纯噪声。
    /// </summary>
    public float MinDirSegPx = 1.5f;

    // ---- 状态 ----

    /// <summary>时间组上限（环形缓冲，取模用位与，必须 2 的幂）。</summary>
    private const int GroupCap = 16;
    private const int GroupMask = GroupCap - 1;

    private float _x1, _y1, _x2, _y2;   // 最近两点（TipX/TipY、两点回退分支用）
    private double _t1, _t2;
    private float _vx, _vy;             // 当前速度（px/ms）——由下面两种方式之一给出
    private float _pvx, _pvy;           // 上一次速度（算加速度）
    private float _ax, _ay;
    private bool _hasV, _hasA;
    private int _count;                 // 当前保留的时间组数 = min(_gn, GroupCap)（诊断 / Predict 的 ≥2、≥3 门槛）
    private readonly double[] _taus = new double[16];

    // 时间组环形缓冲
    private readonly float[] _gx = new float[GroupCap];
    private readonly float[] _gy = new float[GroupCap];
    private readonly double[] _gt = new double[GroupCap];
    private int _gn;                    // 累计推入的组数（单调递增；取模得下标）

    // 方向不稳度：上一次"够长"的段方向 + 相邻夹角 EMA
    private float _lastDirX, _lastDirY;
    private bool _hasLastDir;
    private float _dirEma;

    public float TipX => _x2;
    public float TipY => _y2;
    public float Speed => MathF.Sqrt(_vx * _vx + _vy * _vy);
    /// <summary>当前累积的**时间组**数（诊断用；同刻合并的点算一组）。</summary>
    public int Count => _count;   // 诊断用；同刻合并的点算一组（环形缓冲最多保留 GroupCap 组）
    /// <summary>当前方向不稳度 EMA（诊断用，0 = 平稳）。</summary>
    public float DirEma => _dirEma;
    /// <summary>速度是否可用。false 时 <see cref="Predict"/> 一定给 0（不拿陈旧速度外推）。</summary>
    public bool HasVelocity => _hasV;

    public void Reset()
    {
        _count = 0;
        _gn = 0;
        _vx = _vy = _pvx = _pvy = _ax = _ay = 0f;
        _hasV = _hasA = false;
        _hasLastDir = false;
        _lastDirX = _lastDirY = 0f;
        _dirEma = 0f;
    }

    /// <summary>喂一个采样点（屏幕坐标 + 它自己的时标）。</summary>
    public void Add(float x, float y, double timeMs)
    {
        if (_gn > 0 && (timeMs - _t2) > MaxGapMs)
            Reset();                       // 断笔：间隔太大，重新起一条轨迹（= AOSP "停住"口径）

        // 时间倒流（个别点时标不可用退回 nowMs）：整体丢弃这一帧的点，不改 _x2/_y2/_t2——
        // 否则 MaxGapMs 断笔判断和 Predict 的起点会被较旧值污染。PushGroup 里 dt<0 也会丢，
        // 但那发生在 _x2/_y2/_t2 已更新之后、太晚；这里先拦，PushGroup 的 dt<0 只剩双保险。
        if (_gn > 0 && timeMs < _t2)
            return;

        _x1 = _x2; _y1 = _y2; _t1 = _t2;
        _x2 = x; _y2 = y; _t2 = timeMs;

        bool newGroup = PushGroup(x, y, timeMs);
        _count = Math.Min(_gn, GroupCap);

        if (VelWindowMs > 0.0)
        {
            FitVelocity();
        }
        else
        {
            // ---- 回退分支：旧的两点差分（`VelWindowMs = 0`；A/B 对照与"改坏了没有"的锚点）----
            if (_gn < 2) return;
            float dt = (float)(_t2 - _t1);
            if (dt <= 0f) return;
            _pvx = _vx; _pvy = _vy;
            _vx = (_x2 - _x1) / dt;
            _vy = (_y2 - _y1) / dt;
            if (_hasV) { _ax = (_vx - _pvx) / dt; _ay = (_vy - _pvy) / dt; _hasA = true; }
            _hasV = true;
        }

        // ---- 方向不稳度：只在**新时间组**、且段够长时更新 ----
        // 旧实现是"每个 dt>0 的子段都算"——批内同刻点直接被 `dt <= 0 → return` 跳过，
        // 于是整批只留下最旧那一小段的方向（拐弯时就是旧方向），而慢写 1px 段又会把 EMA 推高。
        if (newGroup && _gn >= 2)
        {
            int prev = (_gn - 2) & GroupMask, last = (_gn - 1) & GroupMask;
            float ddx = _gx[last] - _gx[prev], ddy = _gy[last] - _gy[prev];
            float dn = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (dn >= MinDirSegPx)
            {
                ddx /= dn; ddy /= dn;
                if (_hasLastDir)
                {
                    float c = ddx * _lastDirX + ddy * _lastDirY;
                    c = c < -1f ? -1f : (c > 1f ? 1f : c);
                    float ang = MathF.Acos(c);
                    _dirEma += DirEmaA * (ang - _dirEma);
                }
                _lastDirX = ddx; _lastDirY = ddy;
                _hasLastDir = true;
            }
        }
    }

    /// <summary>
    /// 推一个点到时间组里：与上一组相差 &lt; <see cref="TieMs"/> 就并入（只刷新位置），
    /// 否则新开一组。返回 true = 这是一个**新组**。
    /// </summary>
    private bool PushGroup(float x, float y, double t)
    {
        if (_gn > 0)
        {
            int last = (_gn - 1) & GroupMask;
            double dt = t - _gt[last];
            if (dt >= 0.0 && dt < TieMs)
            {
                _gx[last] = x; _gy[last] = y;     // 批内后到的点覆盖位置（同 AOSP 的合并点口径）
                return false;
            }
            // 时间倒流（个别点时标不可用退回 nowMs）：**丢掉这一组**，不污染窗口。
            if (dt < 0.0) return false;
        }
        int k = _gn & GroupMask;
        _gx[k] = x; _gy[k] = y; _gt[k] = t;
        _gn++;
        return true;
    }

    /// <summary>
    /// 用最近若干个时间组做**加权一阶最小二乘**，求当前速度（px/ms）。
    /// 数据不足（不足两组 / 退化）时把 <see cref="_hasV"/> 置 false——
    /// 调用方据此**不预测**，而不是拿上一次的速度外推。
    /// </summary>
    private void FitVelocity()
    {
        int avail = Math.Min(_gn, GroupCap);
        if (avail < 2) { _hasV = false; return; }

        int last = (_gn - 1) & GroupMask;
        double tLast = _gt[last];

        // 先按时间窗收组，再按组数封顶
        int m = 1;
        int cap = Math.Min(avail, Math.Max(2, VelMaxGroups));
        while (m < cap)
        {
            int k = (_gn - 1 - m) & GroupMask;
            if (tLast - _gt[k] > VelWindowMs) break;
            m++;
        }
        if (m < 2) { _hasV = false; return; }

        double sw = 0, st = 0, sx = 0, sy = 0, stt = 0, stx = 0, sty = 0;
        for (int i = 0; i < m; i++)
        {
            int k = (_gn - 1 - i) & GroupMask;
            double t = _gt[k] - tLast;              // ≤ 0，最新一组是 0
            double w = 1.0 / (1.0 + i); w *= w;     // 1/(1+i)²：越旧衰减越快（急转不被抹平）
            sw += w; st += w * t; stt += w * t * t;
            sx += w * _gx[k]; sy += w * _gy[k];
            stx += w * t * _gx[k]; sty += w * t * _gy[k];
        }
        double den = sw * stt - st * st;
        if (den < 1e-9) { _hasV = false; return; }   // 所有组同刻（还没跨过一个真实间隔）

        float nvx = (float)((sw * stx - st * sx) / den);
        float nvy = (float)((sw * sty - st * sy) / den);
        if (!float.IsFinite(nvx) || !float.IsFinite(nvy)) { _hasV = false; return; }

        // ---- 2026-11-17 方向约束（LSQ2_RESTRICTED 同族）----
        // 最近两个时间组的真实位移方向与 LSQ 速度差异过大时，退回差分速度。
        float vxOut = nvx, vyOut = nvy;
        if (TurnRestrictDeg > 0f && _gn >= 2)
        {
            int g0 = (_gn - 1) & GroupMask;
            int g1 = (_gn - 2) & GroupMask;
            float dx = _gx[g0] - _gx[g1];
            float dy = _gy[g0] - _gy[g1];
            double dt = _gt[g0] - _gt[g1];
            float dLen = MathF.Sqrt(dx * dx + dy * dy);
            if (dt > 1e-3 && dLen >= TurnRestrictMinPx)
            {
                float vLen = MathF.Sqrt(nvx * nvx + nvy * nvy);
                bool reject = vLen < 1e-6f;
                if (!reject)
                {
                    // cos(θ) = (d·v) / (|d||v|)；用 dt 缩放后的差分速度与 nv 单位相同
                    float cos = (dx * nvx + dy * nvy) / (dLen * vLen);
                    cos = cos < -1f ? -1f : (cos > 1f ? 1f : cos);
                    float angDeg = MathF.Acos(cos) * (180f / MathF.PI);
                    reject = angDeg > TurnRestrictDeg;
                }
                if (reject)
                {
                    vxOut = (float)(dx / dt);
                    vyOut = (float)(dy / dt);
                }
            }
        }

        if (_hasV && UseAcceleration)
        {
            double dtA = (tLast - _gt[(_gn - 1 - (m - 1)) & GroupMask]) / Math.Max(1, m - 1);
            if (dtA > 1e-3)
            {
                _ax = (float)((vxOut - _pvx) / dtA);
                _ay = (float)((vyOut - _pvy) / dtA);
                _hasA = true;
            }
        }
        _pvx = _vx; _pvy = _vy;
        _vx = vxOut; _vy = vyOut;
        _hasV = true;
    }

    /// <summary>
    /// 生成预测点（时间序，全部在最后一点之后）。
    /// </summary>
    /// <returns>写进 <paramref name="buf"/> 的个数；0 = 不预测</returns>
    public int Predict(PredictedPoint[] buf)
    {
        if (buf == null || buf.Length == 0) return 0;
        // 2026-11-17：**没有有效速度就一个字都不给**。
        // 旧实现只判 `_count < 2`，时标不可用（批内同刻 / 合成输入）时会拿上一次的
        // 速度从最新点继续外推——屏幕上表现为"尾还在往老方向伸"，正是甩尾的来源之一。
        if (!_hasV || _count < 2) return 0;
        if (MinSpeed > 0f && Speed < MinSpeed) return 0;

        // 采样间隔：优先用真实间隔（注：这是交付件的原口径，与 v6 的取法略有差别，
        // A/B 数据就是在这份代码上测的——不"顺手修"）。
        // 一阶外推下所有预测点共线、末点固定在地平线，interval 只决定线上插几个点，
        // 几何不受影响（2026-11-17 复核：渲染只用 X/Y，不看 TimeMs）。
        double interval = 0;
        if (_gn >= 2)
        {
            double span = (_t2 - (_count >= 3 ? _t1 : _t2)) / (_count >= 3 ? 2 : 1);
            if (span > 0.1 && span <= MaxGapMs) interval = span;
        }
        if (interval <= 0) interval = 8.0;

        double horizon = HorizonMs;
        int cap = Math.Min(Math.Min(MaxPoints, buf.Length), _taus.Length);
        int steps = Math.Max(0, (int)Math.Floor(horizon / interval));
        if (steps > cap) steps = cap;

        int n = 0;
        for (int i = 1; i <= steps; i++) _taus[n++] = interval * i;
        if (n == 0 || horizon - _taus[n - 1] > 1e-6)
        {
            if (n < cap) _taus[n++] = horizon;
            else if (n > 0) _taus[n - 1] = horizon;
        }

        for (int i = 0; i < n; i++)
        {
            float t = (float)_taus[i];
            float dx = _vx * t;
            float dy = _vy * t;
            if (UseAcceleration && _hasA)
            {
                dx += 0.5f * _ax * t * t;
                dy += 0.5f * _ay * t * t;
            }
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > MaxDistance && len > 1e-9f)
            {
                float k = (float)MaxDistance / len;
                dx *= k; dy *= k;
            }
            buf[i] = new PredictedPoint
            {
                X = _x2 + dx,
                Y = _y2 + dy,
                TimeMs = _t2 + _taus[i],
            };
        }

        // ---- 安全层：方向自适应 + 弧长截断 ----
        if (ArcCap > 0.0)
        {
            double arcCap = ArcCap;
            if (DirAdaptive > 0f && _dirEma > DirThresh)
                arcCap = ArcCap * Math.Max(DirFloor, 1.0 - DirAdaptive * (_dirEma - DirThresh));
            if (arcCap <= 0.0)
                return 0;                       // 门控：方向极度不稳，本帧不预测

            double acc = 0.0, cx = _x2, cy = _y2;
            int keep = 0;
            for (int i = 0; i < n; i++)
            {
                double qx = buf[i].X, qy = buf[i].Y;
                double seg = Math.Sqrt((qx - cx) * (qx - cx) + (qy - cy) * (qy - cy));
                if (acc + seg >= arcCap && seg > 1e-9)
                {
                    double u = Math.Max(0.0, (arcCap - acc) / seg);
                    buf[i].X = (float)(cx + (qx - cx) * u);
                    buf[i].Y = (float)(cy + (qy - cy) * u);
                    keep = i + 1;
                    break;
                }
                acc += seg;
                cx = qx; cy = qy;
                keep = i + 1;
            }
            n = keep;
        }
        return n;
    }
}
