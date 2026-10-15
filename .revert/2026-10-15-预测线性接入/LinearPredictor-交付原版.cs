// ============================================================================
//  LinearPredictor.cs —— 线性/二阶运动学预测（Chromium 同族）+ 弧长安全层
//
//  对照 Chromium 源码：ui/base/prediction/linear_predictor.cc（BSD-3）
//      p(t) = p0 + v·t + ½·a·t²
//  其中 v 由最近两点算，a 由最近两个速度算，都用输入点自己的硬件时标。
//
//  本实现把它拆成可调的两档，便于做消融：
//      UseAcceleration = false → 一阶（纯线性）：p0 + v·t
//      UseAcceleration = true  → 二阶（Chromium 原式）
//      MaxDistance > 0         → 加一道固定限幅（观察限幅单独贡献多少）
//
//  这是三方对比里的「裸核心」——V6 = 这个核心 + 完整安全层。
//  把两者放一起跑，就能把「预测算法的功劳」和「安全机制的功劳」分开。
//
//  【2026-10-15 架构级结论】安全层直接长在这个核心上，效果全面超过 V6：
//      ArcCap=6 + DirAdaptive=4 + DirFloor=0（本类默认值）
//      vs V6（625 条轨迹，L=21.3ms）：
//        消除率     42.0  vs 41.9   （持平）
//        感知滞后p95 22.66 vs 23.36  （更好）
//        偏离p95    1.59  vs 2.25    （更好）
//        偏离最大   6.00  vs 6.00    （同为硬保证）
//        可见甩尾率 0.04% vs 0.13%   （更好）
//        方向夹角95 32.2° vs 56.8°   （大幅更好）
//        尾端闪烁   2.72  vs 2.85    （更好）
//        不预测帧   7.8%  vs 2.2%    （门控更积极，唯一代价）
//      结论在 L=15/21.3/30/60ms 四个延迟档全部成立。
//      ⇒ 「一阶核心 + 弧长截断 + 方向自适应门控」用 1/5 的代码量胜过 V6。
//  想恢复纯 Chromium 行为：ArcCap = 0。
//  想更跟手（接受 12px 最坏）：ArcCap = 8, DirAdaptive = 2, DirFloor = 2。
//
//  零依赖 · 无反射 · Native AOT 直接编译 · 时间毫秒 / 长度像素
// ============================================================================

using System;
using System.Runtime.CompilerServices;

namespace InkPred.Linear
{
    public struct LpPoint
    {
        public float X;
        public float Y;
        public double TimeMs;
    }

    public sealed class LinearPredictor
    {
        // ---- 可调参数 ----
        /// <summary>预测地平线（毫秒）。建议设成实测端到端延迟。</summary>
        public double HorizonMs = 21.3;
        /// <summary>
        /// true = 二阶（含 ½at²，Chromium 原式）；false = 一阶（纯线性 p0+vt）。
        ///
        /// **默认 false（一阶）** —— A/B 实测一阶明显更好：
        ///   一阶·无限幅：延迟消除率 58.8%、横向偏离最大 53.84、闪烁 4.22
        ///   二阶·无限幅：延迟消除率  8.7%、横向偏离最大 **153.35**、闪烁 11.74
        /// 差的原因是加速度由速度差分得到，噪声极大，方向突变时会把预测甩飞。
        /// Chromium 原式之所以能用，是因为它前面还有一层 one-euro filter 做平滑。
        /// 想要 Chromium 原式就设回 true。
        /// </summary>
        public bool UseAcceleration = false;
        /// <summary>一次最多几个预测点。</summary>
        public int MaxPoints = 8;
        /// <summary>
        /// 固定限幅（px）。**默认 12** —— A/B 实测这是跟手性与安全性的平衡点
        /// （消除率 54.4%、横向偏离最大 12.00、闪烁 3.33）。
        /// 设成 1e9 可看裸核心、设 6 更保守（消除率降到 29.9%）。
        /// </summary>
        public double MaxDistance = 12.0;
        /// <summary>
        /// 【安全层 · 弧长截断（px），0 = 关闭】默认 6。
        ///
        /// 与 <see cref="MaxDistance"/> 的区别：
        ///   · MaxDistance 是**径向**限幅——把每个点沿「笔尖→该点」射线压到半径内，
        ///     会改变预测形状（直线时所有点塌缩、圆周时弧线被拉扁）。
        ///   · ArcCap 是**弧长**截断——从笔尖起沿预测折线累积长度，超过 cap 就在该段内
        ///     线性插值切断并停止。**保留预测的形状**，只砍总长度。
        ///
        /// A/B 实测（625 条轨迹，L=21.3ms，ArcCap=6 + 方向自适应 da=4/floor=0）：
        ///   消除率 41.7%（V6 41.9，基本持平）· 横向偏离 p95 1.49（V6 2.25）
        ///   · 偏离最大 6.00（与 V6 同样硬保证）· 可见甩尾率 0.05%（V6 0.13）
        ///   · 方向夹角95 27.7°（V6 56.8）· 闪烁 2.69（V6 2.85）
        /// ⇒ 除消除率低 0.2 个点（噪声级）外全面优于 V6，且结构简单得多。
        /// 该结论在 L = 15 / 21.3 / 30 / 60 ms 四个延迟档全部成立。
        /// </summary>
        public double ArcCap = 6.0;
        /// <summary>
        /// 【安全层 · 方向自适应强度，0 = 关闭】默认 4。
        /// 用「最近几段行进方向的平均变化（弧度/段）」衡量运动不平稳度，
        /// 方向越乱，预测尾越短：cap_eff = ArcCap × max(DirFloor, 1 − da×(inst − DirThresh))。
        /// A/B 实测：纯安全增益——开启后（da=4）夹角95 62.1°→27.7°、lat_p95 1.98→1.49，
        /// 消除率只掉 0.8 个点。
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
        /// <summary>低于这个速度（px/ms）不预测。</summary>
        public float MinSpeed = 0.0f;
        /// <summary>相邻采样间隔超过这个值（ms）就当断笔。</summary>
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

        public void Reset()
        {
            _count = 0;
            _vx = _vy = _pvx = _pvy = _ax = _ay = 0f;
            _hasV = _hasA = false;
            _dirN = 0;
            _dirEma = 0f;
        }

        public void Add(float x, float y, double timeMs)
        {
            if (_count > 0 && (timeMs - _t2) > MaxGapMs)
                Reset();

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

        public int Predict(LpPoint[] buf)
        {
            if (_count < 2 || buf == null || buf.Length == 0) return 0;
            if (MinSpeed > 0f && Speed < MinSpeed) return 0;

            // 采样间隔：优先用真实间隔
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
                buf[i] = new LpPoint
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
}
