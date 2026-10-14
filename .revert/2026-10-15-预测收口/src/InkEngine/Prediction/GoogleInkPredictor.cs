// ============================================================================
//  GoogleInkPredictor.cs
//  Google ink-stroke-modeler 的 C# 移植版 —— .NET 10 Native AOT 兼容
//
//  逐行对照原作者实现（Apache License 2.0）：
//      https://github.com/google/ink-stroke-modeler
//      · internal/prediction/kalman_filter/kalman_filter.cc
//      · internal/prediction/kalman_filter/axis_predictor.cc
//      · internal/prediction/kalman_predictor.cc
//      · internal/position_modeler.cc
//      · internal/wobble_smoother.cc
//      · params.h
//
//  ChromeOS / Android 手写笔实际在用的预测算法。
//
//  与"限幅派"（微软 Wet Ink / 本工程 InkPredictor）的根本区别：
//      Google 用「置信度」决定画几个预测点，置信度低就直接不画远点：
//          confidence = γs · γε · γV · γμ     n = ⌈γ · tp / Δt⌉
//      它没有硬上限，所以最坏情况会甩出 17~55px（三套语料实测）。
//
//  零依赖 · 无反射 · Predict 零分配 · Native AOT 直接编译
//  计时单位：毫秒（对外）／秒（内部）；长度单位：屏幕像素
//
//  ==== 本仓修改（2026-10-14，真机排查后的修复；与包内原件及 Python 参考实现的差异）====
//  第一波（白天，真机"预测尾跑到后面/方向反了"）——见 `调研/调研-预测算法三档-...md` §九：
//   ① γV 旅行速度单位：原来拿 px/s 比 px/ms 阈值（0.015/0.075）→ 慢速档恒 1、从未生效。
//      改为按 px/ms 计算后再比（与阈值字段的注释语义一致）。
//   ② WobbleSmoother 速度门限同病（0.02/0.03 对 px/s）→ 抖动平滑事实上关闭。改按 px/ms。
//   ③ min_catchup_velocity 换算写反（/1000 → 应为 ×1000，1e-3 px/ms = 1 px/s）。
//   ④ γε 门限 MaxEstimationDistance：0.375px（=1.5×噪声方差）落在真实噪声残差
//      （实测 0.14~0.52px）正中间 → 前推段在"5 点/0 点"间闪烁（400 帧里 69 帧归零）。
//      校准到 1.0px（=2σ）后归零消失、前推点数下限 2。扫描数据见 §九。
//   另：`_lastIn*` 改存平滑后的位置（上游存的是 corrected_position，不是原始输入）。
//
//  第二波（当晚，与官方 C++ 逐点对拍）——见 §九的 9.7：
//   ⑤ PositionModeler 推进方式（官方 UpdateAlongLinearPath）：锚点在**上一平滑点 → 本点**
//      之间按 (1..n)/n 线性插值、每步 dt = 总时长/n。原来锚点恒为当前点、固定 1/rate 步长
//      连推 n 步 → 等效把 5ms 输入按 8.3ms 推进，弹簧轨迹被扭（对拍差 ~20px）。
//   ⑥ MinOutputRate 240 → **180**（官方测试/示例的参考值；预测点密度、前推段时长随它变）。
//   ⑦ WobbleSmoother 算法体换官方忠实实现（窗口内**按前段时长加权**的平均位置；旧版是
//      "最近两点中点"的简化版）。阈值语义保持本仓的 px/ms。
//   ⑧ 澄清：另一份排查报告里说的"`MaxTailLength=12` 安全阀把预测点投影到圆上"——本版
//      **没有**这个安全阀（预测点直接写坐标，不塌缩）；那是别的修改版引入的，不是这里。
//
//  ⚠ `--predengine google` 的"接入层修复"（丢掉追赶段）在 Engine.UpdatePredictTail，
//    不在本文件——那是"上游笔尖 vs 我们的原始笔尖"的时间线问题，属接入不属算法。
// ============================================================================

using System;
using System.Runtime.CompilerServices;

namespace InkPredict.Google
{
    /// <summary>预测点。字段与 InkPredict 的 PredictedPoint 一致，便于互换。</summary>
    public struct PredictedPoint
    {
        public float X;
        public float Y;
        /// <summary>绝对时间戳（毫秒）= 最后一次输入时间 + τ。</summary>
        public double TimeMs;
    }

    // ========================================================================
    //  小工具
    // ========================================================================
    internal static class Num
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>把 value 从 [start,end] 线性映射到 [0,1]，并夹紧。对应原作者的 Normalize01。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Normalize01(double start, double end, double value)
        {
            if (start == end) return value > start ? 1.0 : 0.0;
            return Clamp01((value - start) / (end - start));
        }

        /// <summary>线性插值并夹紧 interp 到 [0,1]。对应原作者的 Interp。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Interp(double start, double end, double amount)
            => start + (end - start) * Clamp01(amount);
    }

    // ========================================================================
    //  1. 4 维卡尔曼滤波器（x / y 各一个）
    //     状态 = [位置, 速度, 加速度, jerk]
    //     因 H = [1,0,0,0]，S 是标量，**不需要任何矩阵求逆**
    // ========================================================================
    internal sealed class KalmanFilter
    {
        private readonly double[] _f = new double[16];
        private readonly double[] _q = new double[16];
        private readonly double _r;
        private readonly int _minStableIteration;

        private readonly double[] _x = new double[4];
        private readonly double[] _p = new double[16];
        private int _iter;

        private readonly double[] _tmp1 = new double[16];
        private readonly double[] _tmp2 = new double[16];
        private readonly double[] _ft = new double[16];
        private readonly double[] _k = new double[4];

        public KalmanFilter(double processNoise, double measurementNoise, int minStableIteration)
        {
            const double dt = 1.0;
            double dt2 = dt * dt, dt3 = dt2 * dt;

            // new_pos = pos + v·dt + ½a·dt² + ⅙j·dt³ ; new_v = v + a·dt + ½j·dt²
            // new_a   = a + j·dt                    ; new_j = j
            _f[0] = 1; _f[1] = dt; _f[2] = 0.5 * dt2; _f[3] = dt3 / 6.0;
            _f[4] = 0; _f[5] = 1; _f[6] = dt; _f[7] = 0.5 * dt2;
            _f[8] = 0; _f[9] = 0; _f[10] = 1; _f[11] = dt;
            _f[12] = 0; _f[13] = 0; _f[14] = 0; _f[15] = 1;

            // 噪声视为作用在笔上的随机力 → Q = outer(nv, nv) * process_noise
            Span<double> nv = stackalloc double[4] { dt3 / 6.0, 0.5 * dt2, dt, 1.0 };
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    _q[i * 4 + j] = nv[i] * nv[j] * processNoise;

            _r = measurementNoise;
            _minStableIteration = minStableIteration;
            Reset();
        }

        public bool Stable => _iter >= _minStableIteration;
        public int NumIterations => _iter;
        public double Position => _x[0];
        public double Velocity => _x[1];
        public double Acceleration => _x[2];
        public double Jerk => _x[3];
        public double P00 => _p[0];
        public double LastK1 => _k[1];

        public void Reset()
        {
            Array.Clear(_x, 0, 4);
            Array.Clear(_p, 0, 16);
            for (int i = 0; i < 4; i++) _p[i * 4 + i] = 1.0;    // P = I
            _iter = 0;
        }

        public void Update(double observation)
        {
            if (_iter++ == 0)
            {
                _x[0] = observation;                            // 第一次只设位置
                return;
            }

            // ---- Predict: X = F X ; P = F P Fᵀ + Q
            MatVec(_f, _x, _tmp1);
            Array.Copy(_tmp1, _x, 4);

            Transpose(_f, _ft);
            MatMul(_f, _p, _tmp1);
            MatMul(_tmp1, _ft, _tmp2);
            for (int i = 0; i < 16; i++) _p[i] = _tmp2[i] + _q[i];

            // ---- Update（H = [1,0,0,0] ⇒ S = P[0,0] + R，K = P 的第一列 / S）
            double y = observation - _x[0];
            double s = _p[0] + _r;
            _k[0] = _p[0] / s; _k[1] = _p[4] / s; _k[2] = _p[8] / s; _k[3] = _p[12] / s;

            _x[0] += _k[0] * y;
            _x[1] += _k[1] * y;
            _x[2] += _k[2] * y;
            _x[3] += _k[3] * y;

            // P = (I-KH) P (I-KH)ᵀ + K R Kᵀ
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    _tmp1[i * 4 + j] = (i == j ? 1.0 : 0.0) - _k[i] * (j == 0 ? 1.0 : 0.0);
            MatMul(_tmp1, _p, _tmp2);
            Transpose(_tmp1, _ft);
            MatMul(_tmp2, _ft, _p);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    _p[i * 4 + j] += _k[i] * _k[j] * _r;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MatVec(double[] m, double[] v, double[] r)
        {
            for (int i = 0; i < 4; i++)
            {
                double s = 0;
                for (int j = 0; j < 4; j++) s += m[i * 4 + j] * v[j];
                r[i] = s;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MatMul(double[] a, double[] b, double[] r)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                    r[i * 4 + j] = s;
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transpose(double[] m, double[] r)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    r[j * 4 + i] = m[i * 4 + j];
        }
    }


    /// <summary>调试探针：直接驱动单个卡尔曼滤波器（用于数值对齐核对）。</summary>
    public sealed class KalmanProbe
    {
        private readonly KalmanFilter _kf;
        public KalmanProbe(double processNoise, double measurementNoise, int minStable)
            => _kf = new KalmanFilter(processNoise, measurementNoise, minStable);
        public void Update(double obs) => _kf.Update(obs);
        public double X0 => _kf.Position;
        public double X1 => _kf.Velocity;
        public double X2 => _kf.Acceleration;
        public double X3 => _kf.Jerk;
        private readonly double[] _p = new double[16];
        public double P0 => _kf.P00;
        public double K1 => _kf.LastK1;
    }

    // ========================================================================
    //  2. 单轴预测器（内部假设 dt = 1，读取时按真实间隔做幂次修正）
    // ========================================================================
    internal sealed class AxisPredictor
    {
        private readonly KalmanFilter _kf;
        public AxisPredictor(double processNoise, double measurementNoise, int minStable)
            => _kf = new KalmanFilter(processNoise, measurementNoise, minStable);

        public bool Stable => _kf.Stable;
        public int NumIterations => _kf.NumIterations;
        public void Reset() => _kf.Reset();
        public void Update(double obs) => _kf.Update(obs);
        public double Position => _kf.Position;
        public double Velocity => _kf.Velocity;
        public double Acceleration => _kf.Acceleration;
        public double Jerk => _kf.Jerk;
    }

    // ========================================================================
    //  3. 抖动平滑（低速时对输入做移动平均；官方推荐 timeout≈2.5/输入频率）
    // ========================================================================
    internal sealed class WobbleSmoother
    {
        private const int Cap = 128;
        private readonly double[] _px = new double[Cap];
        private readonly double[] _py = new double[Cap];
        private readonly double[] _wpx = new double[Cap];   // 位置 × 前段时长（官方 weighted_position）
        private readonly double[] _wpy = new double[Cap];
        private readonly double[] _dist = new double[Cap];  // 与前一点的距离（官方 distance）
        private readonly double[] _dur = new double[Cap];   // 前段时长（秒）
        private readonly double[] _pt = new double[Cap];
        private int _head, _count;
        private double _sumWx, _sumWy, _sumDist, _sumDur;

        public bool Enabled;
        public double TimeoutSec;
        public double SpeedFloor;
        public double SpeedCeiling;

        public WobbleSmoother(bool enabled, double timeoutSec, double speedFloor, double speedCeiling)
        {
            Enabled = enabled; TimeoutSec = timeoutSec;
            SpeedFloor = speedFloor; SpeedCeiling = speedCeiling;
        }

        public void Reset()
        {
            _head = 0; _count = 0;
            _sumWx = _sumWy = _sumDist = _sumDur = 0;
        }

        /// <summary>
        /// 官方 WobbleSmoother::Update 的忠实实现（2026-10-14 第二波换，本仓修改⑦）：
        /// 窗口内**按前段时长加权的平均位置**，再按平均速度在"均值 ↔ 原始点"之间插值——
        /// 慢 → 用均值（平滑抖动）；快 → 用原始点（不加滞后）。旧版是"最近两点的中点"简化版。
        /// 生命周期对齐官方：Reset 后**第一个点当即时锚**（返回原始值、不进统计，
        /// 对应官方 Reset(pos0,t0)），之后每个点按官方公式统计。阈值保持本仓 px/ms 语义。
        /// </summary>
        public void Update(double x, double y, double t, out double ox, out double oy)
        {
            if (!Enabled) { ox = x; oy = y; return; }

            if (_count == 0)
            {
                // 官方 Reset(pos0,t0)：起点只存不统计（weighted/distance/duration 全 0）。
                _px[0] = x; _py[0] = y; _wpx[0] = _wpy[0] = _dist[0] = _dur[0] = 0; _pt[0] = t;
                _head = 0; _count = 1;
                ox = x; oy = y;
                return;
            }

            // 保险：按时间剪枝后几乎不会满；真满了丢最旧（官方式"先减后加"）。
            if (_count == Cap)
            {
                _sumWx -= _wpx[_head]; _sumWy -= _wpy[_head];
                _sumDist -= _dist[_head]; _sumDur -= _dur[_head];
                _head = (_head + 1) % Cap; _count--;
            }

            int tail = (_head + _count) % Cap;
            int prev = (_head + _count - 1) % Cap;
            double delta = t - _pt[prev];
            double ddx = x - _px[prev], ddy = y - _py[prev];
            _px[tail] = x; _py[tail] = y;
            _wpx[tail] = x * delta; _wpy[tail] = y * delta;
            _dist[tail] = Math.Sqrt(ddx * ddx + ddy * ddy);
            _dur[tail] = delta; _pt[tail] = t;
            _sumWx += _wpx[tail]; _sumWy += _wpy[tail];
            _sumDist += _dist[tail]; _sumDur += _dur[tail];
            _count++;

            // 官方剪枝：while (front.time < time - timeout) 弹队首（至少留最新那个）。
            while (_count > 1 && _pt[_head] < t - TimeoutSec)
            {
                _sumWx -= _wpx[_head]; _sumWy -= _wpy[_head];
                _sumDist -= _dist[_head]; _sumDur -= _dur[_head];
                _head = (_head + 1) % Cap; _count--;
            }

            if (_sumDur <= 0) { ox = x; oy = y; return; }   // 同刻多点（批内时间相同）时退回原始值
            double avgX = _sumWx / _sumDur, avgY = _sumWy / _sumDur;
            double avgSpeedPxPerSec = _sumDist / _sumDur;
            // 阈值语义：px/ms（本仓 0.02/0.03），换算后再 Normalize01。
            double speedMs = avgSpeedPxPerSec / 1000.0;
            double amt = Num.Normalize01(SpeedFloor, SpeedCeiling, speedMs);
            ox = avgX + (x - avgX) * amt;
            oy = avgY + (y - avgY) * amt;
        }
    }

    // ========================================================================
    //  4. 位置模型（弹簧-阻尼把笔尖拉向输入，形成平滑的"追赶"）
    //     参数为原作者默认值；内部时间单位是**秒**
    // ========================================================================
    internal sealed class PositionModeler
    {
        private const double SpringMassConstant = 11.0 / 32400.0;
        private const double DragConstant = 72.0;

        public double X, Y;       // 像素
        public double Vx, Vy;     // 像素/秒
        public double Ax, Ay;     // 像素/秒²

        public void Reset(double x, double y) { X = x; Y = y; Vx = Vy = Ax = Ay = 0; }

        /// <summary>按固定步长推进（步长 = 1/min_output_rate 秒）。</summary>
        public void Step(double anchorX, double anchorY, double dt)
        {
            Ax = (anchorX - X) / SpringMassConstant - DragConstant * Vx;
            Ay = (anchorY - Y) / SpringMassConstant - DragConstant * Vy;
            Vx += dt * Ax; Vy += dt * Ay;
            X += dt * Vx; Y += dt * Vy;
        }
    }

    // ========================================================================
    //  5. 主类：完整管线
    //     输入 → 抖动平滑 → 位置模型（按 MinOutputRate 上采样推进；默认 180Hz）→ 卡尔曼预测
    //     预测 = 追赶段（连到估计位置）+ 前推段（三次多项式，点数由置信度决定）
    // ========================================================================
    public sealed class GoogleInkPredictor
    {
        // ------------------------------------------------------------------
        //  参数（对应 params.h；官方明说该库 unit-agnostic、必须自己标定）
        //  下面这套已按「屏幕像素 + 毫秒」标定，可直接用。
        //  换单位时按同比例缩放即可（见 README「单位与标定」一节）。
        // ------------------------------------------------------------------
        public double ProcessNoise = 1e-2;
        public double MeasurementNoise = 0.25;
        public int MinStableIteration = 4;
        public int MaxTimeSamples = 20;

        /// <summary>期望预测时长（毫秒）= 预测尾的长度上限。建议 = 本机实测端到端延迟。</summary>
        public double PredictionIntervalMs = 21.3;
        /// <summary>输出采样率（Hz）。官方测试/示例的参考值 = **180**（2026-10-14 第二波对齐，
        /// 本仓修改⑥；原来是 240——预测点密度与前推段时长都会跟着它变）。</summary>
        public double MinOutputRate = 180.0;
        /// <summary>追赶段速度下限（像素/毫秒），避免除零。</summary>
        public double MinCatchupVelocity = 1e-3;

        /// <summary>加速度项权重（原作者默认 0.5）。</summary>
        public float AccelerationWeight = 0.5f;
        /// <summary>jerk 项权重（原作者默认 0.1）。</summary>
        public float JerkWeight = 0.1f;

        // ---- 置信度四因子：防甩尾的核心 ----
        public int DesiredNumberOfSamples = 20;
        // γε 门限（2026-10-14 校准，本仓修改④）：官方推荐 1.5×测量噪声——按方差算是 0.375px，
        // 但它落在真实噪声残差（实测 |估计−输入| ≈ 0.14~0.52px）正中间：400 帧里 69 帧
        // 前推段被整段掐掉（尾巴闪烁）。取 1.0px（=2σ，σ=√0.25=0.5px）后归零消失、
        // 前推点数下限升到 2。扫描数据见 `调研/调研-预测算法三档-手感对照与参数表.md` §九。
        public double MaxEstimationDistance = 1.0;
        public double MinTravelSpeed = 0.015;          // 像素/毫秒，官方推荐典型速度的 5%
        public double MaxTravelSpeed = 0.075;          // 像素/毫秒，官方推荐典型速度的 25%
        public double MaxLinearDeviation = 2.5;        // 官方推荐 10 × MeasurementNoise
        public float BaselineLinearityConfidence = 0.4f;

        // ---- 前级开关 ----
        /// <summary>低速抖动平滑。若你的输入已平滑过，可关掉。</summary>
        public bool EnableWobbleSmoother = true;
        /// <summary>弹簧位置模型。关掉后预测直接基于原始输入（更跟手但更抖）。</summary>
        public bool EnablePositionModeler = true;

        // ---- 内部状态 ----
        private AxisPredictor _xp, _yp;
        private readonly WobbleSmoother _wob;
        private readonly PositionModeler _pm = new PositionModeler();
        private readonly double[] _times = new double[64];
        private int _tCount;
        private bool _hasLast;
        private double _lastInX, _lastInY, _lastInT;
        private readonly PredictedPoint[] _scratch = new PredictedPoint[512];

        // ---- 调试用：最近一次的状态估计与置信度点数（供数值对齐核对）----
        public readonly double[] DebugEst = new double[8];   // px,py,vx,vy,ax,ay,jx,jy
        private readonly double[] _dbgX = new double[512];
        private readonly double[] _dbgY = new double[512];
        public int DebugN;
        public int DebugConnector;
        public double DebugPredX, DebugPredY;

        public GoogleInkPredictor()
        {
            _xp = new AxisPredictor(ProcessNoise, MeasurementNoise, MinStableIteration);
            _yp = new AxisPredictor(ProcessNoise, MeasurementNoise, MinStableIteration);
            _wob = new WobbleSmoother(EnableWobbleSmoother, 0.05, 0.02, 0.03);
            Reset();
        }

        /// <summary>
        /// 改过参数后必须调用一次（噪声参数在滤波器构造时固化，这里重建）。
        /// 改 PredictionIntervalMs / 置信度阈值 / 开关等，则不必调用。
        /// </summary>
        public void ApplyParams()
        {
            bool lastStable = _xp != null && _xp.Stable;
            _xp = new AxisPredictor(ProcessNoise, MeasurementNoise, MinStableIteration);
            _yp = new AxisPredictor(ProcessNoise, MeasurementNoise, MinStableIteration);
            _wob.Enabled = EnableWobbleSmoother;
            _ = lastStable;
        }

        public void Reset()
        {
            _xp.Reset(); _yp.Reset();
            _wob.Reset();
            _pm.Reset(0, 0);
            Array.Clear(_times, 0, _times.Length);
            _tCount = 0;
            _hasLast = false;
        }

        public bool HasStroke => _hasLast;

        /// <summary>笔尖当前位置 = 屏幕上墨迹的末端。</summary>
        public double TipX => EnablePositionModeler ? _pm.X : _lastInX;
        public double TipY => EnablePositionModeler ? _pm.Y : _lastInY;

        // ------------------------------------------------------------------
        //  输入
        // ------------------------------------------------------------------
        /// <summary>喂一个采样点（毫秒时间戳，须单调递增）。</summary>
        public void Add(double x, double y, double tMs)
        {
            double t = tMs;

            // 时间戳缓冲：最多保留 MaxTimeSamples 个（与原作者 deque 语义一致）
            // 注意 _tCount 必须恒 ≤ MaxTimeSamples，否则 dt 修正会算错
            int cap = Math.Min(MaxTimeSamples, _times.Length);
            if (_tCount < cap)
            {
                _times[_tCount++] = t;
            }
            else
            {
                for (int i = 0; i < cap - 1; i++) _times[i] = _times[i + 1];
                _times[cap - 1] = t;
                _tCount = cap;
            }

            _wob.Update(x, y, t / 1000.0, out double sx, out double sy);   // 内部用秒

            if (!_hasLast)
            {
                _pm.Reset(sx, sy);
                _xp.Reset(); _yp.Reset();
                _xp.Update(sx); _yp.Update(sy);
                _hasLast = true;
                // 2026-10-14：存平滑后的位置（上游 KalmanPredictor 收到的是 corrected_position）
                _lastInX = sx; _lastInY = sy; _lastInT = t;
                return;
            }

            double dtIn = Math.Max(t - _lastInT, 1e-6);      // 毫秒

            if (EnablePositionModeler)
            {
                // 官方 NumberOfStepsBetweenInputs：n = ceil(Δ秒 × min_output_rate)（同一个式子）。
                int n = Math.Max((int)Math.Ceiling(dtIn / 1000.0 * MinOutputRate), 1);
                if (n > 240) n = 240;                        // 断档保护（官方靠 max_outputs_per_call 报错；我们夹住）
                double stepSec = dtIn / 1000.0 / n;          // 官方：每步 dt = 总时长 / n
                // 官方 UpdateAlongLinearPath（2026-10-14 第二波修，本仓修改⑤）：锚点在
                // **上一平滑点 → 本点**之间按 (1..n)/n 线性插值推进。原来锚点恒为当前点、
                // 按固定 1/rate 步长连推 n 步——等效把 5ms 输入按 8.3ms 推进，弹簧轨迹被扭，
                // 与官方 C++ 逐点对拍差 ~20px（官方源码见 position_modeler.h）。
                for (int i = 1; i <= n; i++)
                {
                    double k = (double)i / n;
                    double ax = _lastInX + (sx - _lastInX) * k;
                    double ay = _lastInY + (sy - _lastInY) * k;
                    _pm.Step(ax, ay, stepSec);
                }
            }

            _xp.Update(sx); _yp.Update(sy);
            // 2026-10-14：同上——上游存的是平滑后的位置（γε 量的是"平滑输入−估计"）
            _lastInX = sx; _lastInY = sy; _lastInT = t;
        }

        // ------------------------------------------------------------------
        //  预测
        // ------------------------------------------------------------------
        /// <summary>
        /// 输出预测点（由近及远）。返回点数，0 = 这一帧不预测。
        /// outPoints 长度建议 ≥ 64；Predict 内部零分配。
        /// </summary>
        public int Predict(PredictedPoint[] outPoints)
        {
            DebugPredX = TipX; DebugPredY = TipY;      // 不可预测时语义 = 笔尖位置
            if (outPoints == null || outPoints.Length == 0) return 0;
            if (!_hasLast || _tCount == 0) return 0;
            if (!_xp.Stable || !_yp.Stable) return 0;

            if (!EstimatedState(out double ex, out double ey, out double evx, out double evy,
                                out double eax, out double eay, out double ejx, out double ejy))
                return 0;

            double sampleDt = 1.0 / MinOutputRate;           // 秒
            int count = 0;

            // ---- 段 1：追赶段（笔尖 → 卡尔曼估计位置）----
            count = CubicConnector(TipX, TipY, TipVx(), TipVy(),
                                   ex, ey, evx, evy, sampleDt, outPoints, count);
            DebugConnector = count;
            if (count >= outPoints.Length) return Stamp(outPoints, count);

            // ---- 段 2：前推段（点数由置信度决定）----
            int nPredict = PointsToPredict(ex, ey, evx, evy, eax, eay, ejx, ejy);
            DebugN = nPredict;
            double cx = ex, cy = ey, cvx = evx, cvy = evy, cax = eax, cay = eay;
            double cjx = ejx, cjy = ejy;
            double dt = sampleDt, dt2 = dt * dt, dt3 = dt2 * dt;

            for (int i = 0; i < nPredict && count < outPoints.Length; i++)
            {
                cx = cx + cvx * dt + cax * dt2 * 0.5 + cjx * dt3 / 6.0;
                cy = cy + cvy * dt + cay * dt2 * 0.5 + cjy * dt3 / 6.0;
                cvx = cvx + cax * dt + cjx * dt2 * 0.5;
                cvy = cvy + cay * dt + cjy * dt2 * 0.5;
                cax += cjx * dt; cay += cjy * dt;

                _dbgX[count] = cx; _dbgY[count] = cy;
                outPoints[count].X = (float)cx;
                outPoints[count].Y = (float)cy;
                count++;
            }

            if (count > 0) { DebugPredX = _dbgX[count - 1]; DebugPredY = _dbgY[count - 1]; }
            return Stamp(outPoints, count);
        }

        /// <summary>只取预测终点（预测尾通常只需要这一个点）。</summary>
        public bool PredictTip(out float x, out float y)
        {
            int n = Predict(_scratch);
            if (n <= 0) { x = (float)TipX; y = (float)TipY; return false; }
            x = _scratch[n - 1].X; y = _scratch[n - 1].Y;
            return true;
        }

        private double TipVx() => EnablePositionModeler ? _pm.Vx : 0;
        private double TipVy() => EnablePositionModeler ? _pm.Vy : 0;

        private int Stamp(PredictedPoint[] outPoints, int count)
        {
            double dtMs = 1000.0 / MinOutputRate;
            for (int i = 0; i < count; i++)
                outPoints[i].TimeMs = _lastInT + (i + 1) * dtMs;
            return count;
        }

        // ------------------------------------------------------------------
        //  内部：状态估计（含 dt 修正与高阶项抑制）
        // ------------------------------------------------------------------
        private bool EstimatedState(out double px, out double py,
                                    out double vx, out double vy,
                                    out double ax, out double ay,
                                    out double jx, out double jy)
        {
            px = _xp.Position; py = _yp.Position;
            vx = _xp.Velocity; vy = _yp.Velocity;
            ax = _xp.Acceleration; ay = _yp.Acceleration;
            jx = _xp.Jerk; jy = _yp.Jerk;

            // 轴预测器不感知时间（假设 dt=1）→ 按平均观测间隔做幂次修正
            double dtSec = (_times[_tCount - 1] - _times[0]) / _tCount / 1000.0;
            if (dtSec <= 0) return false;
            double dt2 = dtSec * dtSec, dt3 = dt2 * dtSec;
            vx /= dtSec; vy /= dtSec;              // → 像素/秒
            ax /= dt2; ay /= dt2;                  // → 像素/秒²
            jx /= dt3; jy /= dt3;                  // → 像素/秒³

            // 让预测偏向线性（原作者注释原文）
            ax *= AccelerationWeight; ay *= AccelerationWeight;
            jx *= JerkWeight; jy *= JerkWeight;

            DebugEst[0] = px; DebugEst[1] = py;
            DebugEst[2] = vx; DebugEst[3] = vy;
            DebugEst[4] = ax; DebugEst[5] = ay;
            DebugEst[6] = jx; DebugEst[7] = jy;
            return true;
        }

        // ------------------------------------------------------------------
        //  内部：追赶段（三次曲线，由两端位置与速度解出）
        // ------------------------------------------------------------------
        private int CubicConnector(double p0x, double p0y, double v0x, double v0y,
                                   double p1x, double p1y, double v1x, double v1y,
                                   double sampleDt, PredictedPoint[] outPoints, int count)
        {
            double dx = p1x - p0x, dy = p1y - p0y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double maxV = Math.Max(Math.Sqrt(v0x * v0x + v0y * v0y),
                                   Math.Sqrt(v1x * v1x + v1y * v1y));
            // 2026-10-14 修单位：最小追赶速度字段的语义是 px/ms（1e-3），
            // 这里要换算成 px/s（×1000）再与 maxV 比——原来写成 ÷1000（1e-6 px/s），
            // 近停时追赶段会顶到 48 点上限。上游口径：下限取"预期速度的 1/1000"。
            double target = dist / Math.Max(maxV, MinCatchupVelocity * 1000.0);
            int n = Math.Max((int)Math.Ceiling(target / sampleDt), 1);
            if (n > 48) n = 48;                        // 输出缓冲保护
            double dur = n * sampleDt;

            double ax = 2 * p0x - 2 * p1x + (v0x + v1x) * dur;
            double ay = 2 * p0y - 2 * p1y + (v0y + v1y) * dur;
            double bx = -3 * p0x + 3 * p1x - (2 * v0x + v1x) * dur;
            double by = -3 * p0y + 3 * p1y - (2 * v0y + v1y) * dur;
            double cx = v0x * dur, cy = v0y * dur;

            for (int i = 1; i <= n && count < outPoints.Length; i++)
            {
                double t = (double)i / n, t2 = t * t, t3 = t2 * t;
                double qx = ax * t3 + bx * t2 + cx * t + p0x;
                double qy = ay * t3 + by * t2 + cy * t + p0y;
                _dbgX[count] = qx; _dbgY[count] = qy;
                outPoints[count].X = (float)qx;
                outPoints[count].Y = (float)qy;
                count++;
            }
            return count;
        }

        // ------------------------------------------------------------------
        //  内部：置信度 → 预测点数（防甩尾的核心）
        // ------------------------------------------------------------------
        private int PointsToPredict(double px, double py, double vx, double vy,
                                    double ax, double ay, double jx, double jy)
        {
            double tau = PredictionIntervalMs / 1000.0;              // 秒
            double target = tau * MinOutputRate;

            // γs：样本数
            double gS = Math.Min(1.0, (double)_xp.NumIterations / DesiredNumberOfSamples);

            // γε：估计位置与最后输入点的偏离
            double ddx = _lastInX - px, ddy = _lastInY - py;
            double gE = 1.0 - Num.Normalize01(0, MaxEstimationDistance,
                                              Math.Sqrt(ddx * ddx + ddy * ddy));

            // 满置信度时预测段的终点
            double dt = tau, dt2 = dt * dt, dt3 = dt2 * dt;
            double endX = px + vx * dt + ax * dt2 * 0.5 + jx * dt3 / 6.0;
            double endY = py + vy * dt + ay * dt2 * 0.5 + jy * dt3 / 6.0;

            // γV：预测段行进速度（2026-10-14 修单位：算成 px/ms 再比——
            // 原来拿 px/s 比 px/ms 阈值（0.015/0.075），除"几乎完全静止"外恒 1、从未生效）
            double ex = endX - px, ey = endY - py;
            double travelSpeedMs = Math.Sqrt(ex * ex + ey * ey) / PredictionIntervalMs;
            double gV = Num.Normalize01(MinTravelSpeed, MaxTravelSpeed, travelSpeedMs);

            // γμ：三次预测偏离线性预测的程度
            double lx = endX - (px + tau * vx), ly = endY - (py + tau * vy);
            double dev = Math.Sqrt(lx * lx + ly * ly);
            double gL = Num.Interp(BaselineLinearityConfidence, 1.0,
                                   1.0 - Num.Normalize01(0, MaxLinearDeviation, dev));

            double confidence = gS * gE * gV * gL;
            return Math.Max(0, (int)Math.Ceiling(target * confidence));
        }
    }

    // ============================================================================
    //  #region 接入示例（Inkboard / InkTeach）
    // ============================================================================
    //
    //  var pred = new GoogleInkPredictor();
    //  // 可选：按实测延迟设定预测尾长度
    //  pred.PredictionIntervalMs = 21.3;
    //  // 若你的输入已经平滑过，关掉前级抖动平滑可减少一级延迟：
    //  // pred.EnableWobbleSmoother = false; pred.ApplyParams();
    //
    //  var buf = new PredictedPoint[64];          // 复用一个即可，Predict 零分配
    //
    //  void OnStylusMove(float x, float y, double tMs)
    //  {
    //      pred.Add(x, y, tMs);                   // 1) 喂输入
    //      int n = pred.Predict(buf);             // 2) 取预测点
    //      if (n > 0) DrawTemporaryTail(buf, n);  // 3) 画成临时尾巴，下一帧先清掉
    //      AppendToRealStroke(x, y);              // 4) 真实墨迹照常
    //  }
    //
    //  void OnStylusUp() => pred.Reset();         // 5) 抬笔务必 Reset
    //
    //  // 只要终点（更省）：
    //  // if (pred.PredictTip(out float tx, out float ty)) DrawTempSegment(x, y, tx, ty);
    //
    //  var buf = new PredictedPoint[64];          // 复用一个即可，Predict 零分配
    //
    // ============================================================================
}
