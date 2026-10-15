// ============================================================================
//  InkPredictorLinear.cs —— 线性预测（Chromium 同族）+ 弧长安全层
//                            **唯一预测器**（2026-10-15 接入，替换 v6）
//
//  来源：用户 2026-10-15 提供的 `线性预测-Chromium.zip`（LinearPredictor.cs）。
//  对照 Chromium 源码：ui/base/prediction/linear_predictor.cc（BSD-3）
//      p(t) = p0 + v·t + ½·a·t²
//  其中 v 由最近两点算，a 由最近两个速度算——都用喂进来那串点自己的时标。
//
//  与 Chromium 原式的差异（交付件里带消融实测，2026-10-15 照抄结论）：
//    · **默认一阶（UseAcceleration=false）**：二阶的 a 由速度差分得到、噪声极大，
//      离线 A/B（625 条轨迹）里"一阶·无限幅"延迟消除率 58.8%、横向偏离最大 53.84、
//      闪烁 4.22；"二阶·无限幅"只剩 8.7% 消除率、偏离最大 153.35、闪烁 11.74。
//      Chromium 能用二阶是因为它前面还有一层 one-euro filter 做平滑；我们没有那层。
//    · **弧长安全层**（ArcCap=6 + 方向自适应 DirAdaptive=4/DirFloor=0）：
//      实测（625 条轨迹，L=21.3ms）vs v6——消除率 42.0 vs 41.9 持平，感知滞后 p95
//      22.66 vs 23.36、偏离 p95 1.59 vs 2.25、可见甩尾率 0.04% vs 0.13%、方向夹角 95
//      32.2° vs 56.8° 全面更好；最坏偏离两边同为 6px 硬保证（弧长截断的功劳）。
//      结论在 L=15/21.3/30/60ms 四个延迟档全部成立。
//      ⇒「一阶核心 + 弧长截断 + 方向自适应门控」用约 1/5 的代码量胜过 v6。
//  想恢复纯 Chromium 行为：ArcCap = 0。想更跟手（接受 12px 最坏）：
//  ArcCap = 8, DirAdaptive = 2, DirFloor = 2。
//
//  【2026-10-15 用户定的接入口径】
//    · **只保留预测开关**：设置「墨迹预测」一行（+ `--predict2/--nopredict2` 对照）。
//      **取消毫秒调节**——「预测时长」设置行与 `--predlatency` 一并撤除；
//      地平线固定 21.3ms（= InkTeach 实测端到端延迟；本机 60Hz 实测口径）。
//    · 替换 v6：V6 源码备份 `.revert/2026-10-15-预测线性接入/`，说明见
//      `调研/已停用-预测算法收口.md`；对照与 60Hz 结论见
//      `调研/调研-线性预测-Chromium.md`。
//
//  零依赖 · 无反射 · Native AOT 直接编译 · 时间毫秒 / 长度像素
// ============================================================================
namespace InkEngine;

/// <summary>预测出来的一个未来点（屏幕坐标 + 它对应的时间）。
/// （v6 退役后本结构随唯一预测器落在这里。）</summary>
internal struct PredictedPoint
{
    public float X, Y;
    public double TimeMs;
}

/// <summary>
/// 线性预测器（唯一实现，2026-10-15 起）。喂点 → 每帧 <see cref="Predict"/> 取
/// 一串未来点，交给显示层限速后画成"预测尾"。
///
/// 纪律（和 v6 相同——这一层没变）：只算显示用的偏移，不进文档/存档/命中；
/// 抬笔、换笔、换档都会 <see cref="Reset"/>。
/// </summary>
internal sealed class InkPredictorLinear
{
    // ---- 可调参数（产品里不再暴露给设置/命令行；离线评测工具会用）----

    /// <summary>
    /// 预测地平线（毫秒）。**固定 21.3**（= InkTeach 实测端到端延迟）。
    ///
    /// 2026-10-15 用户定：**取消毫秒调节**（「预测时长」行与 `--predlatency` 撤除）——
    /// 这个值只此一个，产品里没有入口改它；只有离线评测（`--pdmetrics`）按语料 dt 临时覆盖。
    /// </summary>
    public double HorizonMs = 21.3;
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
    /// <summary>相邻采样间隔超过这个值（ms）就当断笔（同 v6 的 40ms 口径）。</summary>
    public double MaxGapMs = 40.0;

    // ---- 状态 ----
    private float _x1, _y1, _x2, _y2;
    private double _t1, _t2;
    private float _vx, _vy;          // 最近一次速度（px/ms）
    private float _pvx, _pvy;        // 上一次速度（算加速度）
    private float _ax, _ay;          // 加速度（px/ms²）
    private bool _hasV, _hasA;
    private int _count;
    private readonly double[] _taus = new double[16];
    // 方向不稳度：最近 4 段方向 + 相邻夹角 EMA
    private readonly float[] _dirX = new float[4];
    private readonly float[] _dirY = new float[4];
    private int _dirN;
    private float _dirEma;

    public float TipX => _x2;
    public float TipY => _y2;
    public float Speed => MathF.Sqrt(_vx * _vx + _vy * _vy);
    /// <summary>当前累积的采样点数（诊断用；v6 的 Count 同一口径）。</summary>
    public int Count => _count;
    /// <summary>当前方向不稳度 EMA（诊断用，0 = 平稳）。</summary>
    public float DirEma => _dirEma;

    public void Reset()
    {
        _count = 0;
        _vx = _vy = _pvx = _pvy = _ax = _ay = 0f;
        _hasV = _hasA = false;
        _dirN = 0;
        _dirEma = 0f;
    }

    /// <summary>喂一个采样点（屏幕坐标 + 它自己的时标）。</summary>
    public void Add(float x, float y, double timeMs)
    {
        if (_count > 0 && (timeMs - _t2) > MaxGapMs)
            Reset();                       // 断笔：间隔太大，重新起一条轨迹

        _x1 = _x2; _y1 = _y2; _t1 = _t2;
        _x2 = x; _y2 = y; _t2 = timeMs;
        if (_count < 3) _count++;

        if (_count < 2) return;
        float dt = (float)(_t2 - _t1);
        if (dt <= 0f) return;

        _pvx = _vx; _pvy = _vy;
        _vx = (_x2 - _x1) / dt;
        _vy = (_y2 - _y1) / dt;

        if (_hasV)
        {
            _ax = (_vx - _pvx) / dt;
            _ay = (_vy - _pvy) / dt;
            _hasA = true;
        }
        _hasV = true;

        // ---- 方向不稳度：维护最近 4 段方向，相邻夹角做 EMA ----
        float ddx = _x2 - _x1, ddy = _y2 - _y1;
        float dn = MathF.Sqrt(ddx * ddx + ddy * ddy);
        if (dn > 1e-9f)
        {
            ddx /= dn; ddy /= dn;
            if (_dirN > 0)
            {
                // 与上一段比较
                float px_ = _dirX[(_dirN - 1) % 4], py_ = _dirY[(_dirN - 1) % 4];
                float c = ddx * px_ + ddy * py_;
                c = c < -1f ? -1f : (c > 1f ? 1f : c);
                float ang = MathF.Acos(c);
                _dirEma = _dirEma + DirEmaA * (ang - _dirEma);
            }
            _dirX[_dirN % 4] = ddx;
            _dirY[_dirN % 4] = ddy;
            _dirN++;
        }
    }

    /// <summary>
    /// 生成预测点（时间序，全部在最后一点之后）。
    /// </summary>
    /// <returns>写进 <paramref name="buf"/> 的个数；0 = 不预测</returns>
    public int Predict(PredictedPoint[] buf)
    {
        if (_count < 2 || buf == null || buf.Length == 0) return 0;
        if (MinSpeed > 0f && Speed < MinSpeed) return 0;

        // 采样间隔：优先用真实间隔（注：这是交付件的原口径，与 v6 的取法略有差别，
        // A/B 数据就是在这份代码上测的——不"顺手修"）。
        double interval = 0;
        if (_count >= 2)
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
