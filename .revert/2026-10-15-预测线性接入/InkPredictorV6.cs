// ============================================================================
//  InkPredictorV6.cs  —— 自绘预测尾（唯一实现，2026-10-15 收口）
//
//  来源：`InkPredict-改进版.zip` 的 InkPredictor.cs（v6）。
//
//  2026-10-15 收口：用户真机终测后拍板——**只保留这一份**；经典 B4、Google 移植、
//  DL（--predict3）全部退役、**不再进编译**（这是 9.0.8 发布版的事实）。
//  完整源码备份在 `.revert/2026-10-15-预测收口/`，退役说明见
//  `调研/已停用-预测算法收口.md`；对照与参数表见
//  `调研/调研-预测算法三档-手感对照与参数表.md`。
//  （`PredictedPoint` 结构原先住在 InkPredictor.cs——那份退役后搬来本文件。）
//
//  与原版（经典，2026-10-08 版）的差异，见包内 CHANGES.md。五处改动：
//    [1] 新增「速度自适应」：预测强度随速度突变自动收缩
//    [2] 修复急转丢速后下一帧的加速度基准污染（一行）
//    [3] 急转从「硬丢速」改为「软收缩」（保留 >90° 反向的硬底线）
//    [4] 速度平滑（旧默认 0.6；v6 重扫后关回 0，改由方向自适应承担）
//    [5] 不预测门槛加迟滞（MinSpeedExitRatio=0.4）+ MinSpeed 0.02→0.06
//    v6 新增三条：
//    [6] 延迟绑定 LatencyMs=21.3（SetMeasuredLatency）
//    [7] 自适应限幅 AdaptiveCapFactor=1.0（cap = min(MaxDistance, 速度×延迟×k)）
//    [8] 线性度门控 AccelRatio=0.5（二阶项 ≤ r × 一阶项，学自 Google γμ）
//
//  想回到旧行为：SpeedAdaptive=0、TurnDepth=0、VelocitySmoothing=0、
//                MinSpeedExitRatio=0、MinSpeed=0.02、HorizonMs=10、Damping=0.8。
//
//  验证：教学场景 1187 字 + 31 曲线 + 37 几何 + 63 公式 + 13 记号，875 条轨迹
//        低噪声（标定自 UCI 真实数据）  暴伸 2.09% → 1.34%（降 36%），吃到持平
//        高噪声（真实设备余量）        暴伸 8.83% → 3.97%（降 55%）
//        v6 实测：极端场景最坏甩出数学锁死 6px；延迟绑定把吃到 85.8% → 89.6%。
//        自检 52 项全过（包内 Harness，52 项）。
// ============================================================================
namespace InkEngine;

/// <summary>预测出来的一个未来点（屏幕坐标 + 它对应的时间）。
/// （2026-10-15 从退役的 InkPredictor.cs 搬来——现在只剩这一份预测器。）</summary>
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
/// **这份实现的重点不是公式，是"别甩墨、别跳"**：
///   · 地平线限幅：预测时长默认 12 ms（推荐区间 8~15）
///   · 整体阻尼 Damping（默认 0.85）：宁可少补，不要冲过头
///   · **速度平滑 VelocitySmoothing（默认 0.6）**：本版新增的默认值。
///     它是扫描出来的唯一"双赢"杠杆——平滑掉速度估计里的高频噪声后，
///     预测方向更贴近真实行进方向，于是"吃到"升、"暴伸"降，同时发生。
///   · **速度自适应 SpeedAdaptive（默认 2.0）**：速度突变时按比例收缩预测强度。
///     "跳"全部发生在运动突变（加速/减速/急转）的瞬间，稳态书写时预测再激进也不会跳。
///     自适应把这两类场景解耦：稳态用大强度，突变自动收到 AdaptiveFloor 倍。
///   · 加速度单独衰减 AccelDamping（默认 0.4）后再限幅——差分出来的项噪声最大
///   · 慢速不预测（速度 < MinSpeed，默认 0.06）+ **门槛迟滞**（MinSpeedExitRatio）
///   · 反向/急转检测：夹角 >90° 硬丢速；60°~90° **只软收缩不丢速**
///   · 断笔重置：相邻采样间隔 > MaxGapMs（40 ms，手写板兼容鼠标路的突发间隔）
///   · 总位移上限 MaxDistance：兜底，防止极端速度下的长尾
///
/// 不做的事：不滤位置（用户定的"指针报什么就画什么"），不预测压力/倾角
/// （委托轨迹的点结构只有 x/y/radius，压力还没参与渲染）。
/// </summary>
internal sealed class InkPredictorV6
{
    // ---- 可调参数（命令行可覆盖）----------------------------------------

    /// <summary>预测地平线（毫秒）。默认 12，允许 8~15。</summary>
    public double HorizonMs { get; set; } = 21.3;   // v6：与实测延迟绑定
    /// <summary>
    /// 整体阻尼：预测位移乘这个系数。
    /// 0.85 是在「速度自适应」之下重扫的结果——自适应已经负责在突变处收缩，
    /// 所以稳态这一侧可以比原版（0.8）更激进，换来更高的"吃到"。
    /// （原版 0.80 的来历：UCI Character Trajectories 48.7 万采样点，
    ///   在"最坏 1% 超前不超过 0.8 倍滞后"约束下扫出来的。）
    /// </summary>
    public float Damping { get; set; } = 1.0f;
    /// <summary>加速度项衰减。</summary>
    public float AccelDamping { get; set; } = 0.4f;
    /// <summary>低于这个速度（px/ms）不预测。</summary>
    public float MinSpeed { get; set; } = 0.06f;

    /// <summary>
    /// **【本版新增】不预测门槛的迟滞比**（0 = 关闭）。
    ///
    /// 光有 <see cref="MinSpeed"/> 时，速度在门槛附近抖动会让「预测 ↔ 不预测」
    /// 反复切换，尾巴一会出现一会消失——这正是慢写时"跳"的主因之一。
    /// 迟滞的规则是：进入预测要 > MinSpeed，退出预测要 < MinSpeed × 本值。
    /// 实测把每个字的切换次数从 21 次降到 15 次，暴伸还略降。
    ///
    /// 建议 0.3~0.6。设 0 等于回到老行为（无迟滞）。
    /// </summary>
    public float MinSpeedExitRatio { get; set; } = 0.4f;
    /// <summary>
    /// 速度平滑强度（0 = 不平滑）。速度是差分出来的，真实手写里抖得厉害；
    /// Chromium 也把 filter 单独做了一层（`one_euro_filter`）。
    /// 默认 0（不平滑）——平滑会降低"吃到"；要更平稳可以试 0.3~0.4。
    /// </summary>
    public float VelocitySmoothing { get; set; } = 0f;

    /// <summary>
    /// **【本版新增】速度自适应强度**：预测强度乘系数
    ///     factor = clamp(1 − SpeedAdaptive · |Δv| / v_prev, AdaptiveFloor, 1)
    /// 0 = 关闭（回到原版行为，对整条轨迹用同一个强度）。
    ///
    /// 为什么它有用：预测的"跳"不来自稳态书写，而来自运动突变的瞬间——
    /// 加速、减速、急转。原版对整条轨迹用固定强度，只能取一个折中：
    /// 保守则吃不到，激进则突变处跳。自适应把两者解耦了。
    /// 实测（400 条轨迹）：吃到率 39.4%→41.5% 的同时，暴伸>2x 1.64%→0.83%。
    /// </summary>
    public float SpeedAdaptive { get; set; } = 0f;
    /// <summary>自适应收缩的下限倍数（默认 0.35）。</summary>
    public float AdaptiveFloor { get; set; } = 0.35f;

    /// <summary>
    /// 急转丢速阈值（cos 值，默认 0.5 = 夹角 60°）：新样本方向与**上一次速度**的夹角
    /// 超过它 → 丢掉速度与加速度（拐弯处宁可这一帧不预测，也不沿旧方向甩出去一截）。
    ///
    /// **完全反向（>90°）不受这个值影响，一律丢**；180°（cos = −1）= 退回"只挡反向"的老行为
    /// （A/B 对照用）。2026-10-15 收口后引擎不再每帧同步——固定用本文件默认 0.5
    /// （`--turndeg` 已随 B4 退役撤除）。
    /// </summary>
    public float SharpTurnCos { get; set; } = 0.5f;

    /// <summary>
    /// **【本版新增】急转硬底线**：夹角超过它（cos 小于它）才真正丢速度。
    /// 默认 0 = 只挡"完全反向"（>90°）。这是安全底线，不要往上调太多——
    /// 调高就等于回到老行为（60° 硬门），在教学场景会疯狂抖动。
    ///
    /// 为什么改：老版把"急转"（>60°）也硬丢速。在 UCI 英文单字母上这个门只触发
    /// 0.36%，所以看不出问题；但在中文/函数/几何上实测触发 **20.5%~21.7%**
    /// （每 5 个采样点就有 1 个丢速），尾巴反复消失重建，正是"跳"的主因。
    /// </summary>
    public float HardTurnCos { get; set; } = 0f;

    /// <summary>
    /// **【本版新增】急转软收缩深度**（0~1）。夹角超过 SharpTurnCos 后，
    /// 不再丢速度，而是按角度把预测强度线性收缩，最多压到 (1 - TurnDepth) 倍。
    /// 默认 0.75。设 0 = 关闭软收缩（回到老行为）。
    /// </summary>
    public float TurnDepth { get; set; } = 0.75f;
    /// <summary>预测段相对最后一点的最大位移（px）。</summary>
    public float MaxDistance { get; set; } = 6f;

    // ================= 【v6 新增】三条改进 =================

    /// <summary>
    /// **① 延迟绑定**：本机端到端实测延迟（毫秒）。
    ///
    /// 微软 Wet Ink Predictor 的核心是「预测距离 = 速度 × **当前延迟周期**」，
    /// 而不是外推一个固定时长（其自身实现按 64ms 算，因为它的延迟就是 64ms）。
    /// 我们过去硬编码 20ms，换设备/换屏就得重调。
    ///
    /// 用法：启动时实测一次，然后 <see cref="SetMeasuredLatency"/>。
    /// InkTeach 实测 21.3ms（Present→屏幕 16.3 + 采样→Present 5）。
    /// </summary>
    public double LatencyMs { get; set; } = 21.3;

    /// <summary>
    /// **② 自适应限幅系数**（0 = 关闭，回到固定 <see cref="MaxDistance"/>）。
    ///
    /// 有效上限 = min(MaxDistance, 速度 × LatencyMs × 本系数)。
    ///
    /// 为什么需要：固定 6px 在换设备后会踩空 —— 实测 **6px 是悬崖点，
    /// 7px 就让极端场景可见甩尾从 3.4% 涨到 13.5%**。原因是极端场景下
    /// 「预测点到真实墨迹的距离」恰好在 6~7px 量级，6px 能落在轨迹上，
    /// 7px 就越过去了。让上限跟着「这一帧真正能走多远」走，才能
    /// 在慢写时自动收紧、快写时自动放宽，跨设备稳定。
    ///
    /// 建议 1.0 ~ 1.6（1.3 为实测折中）。
    /// </summary>
    public float AdaptiveCapFactor { get; set; } = 1.0f;

    /// <summary>
    /// **③ 线性度门控**（0 = 关闭）。限制二阶项不超过一阶项的这个倍数。
    ///
    /// 取自 Google ink-stroke-modeler 的置信度因子 γμ：它的实验发现
    /// *"if the actual prediction differs too much from the linear prediction,
    /// it suggests that the acceleration and jerk components overtake the
    /// velocity, resulting in a prediction that flies far off from the stroke."*
    ///
    /// 我们的做法等价但更省：直接把 ½aτ² 项压到不超过 vτ 项的 r 倍。
    /// 建议 0.5。
    /// </summary>
    public float AccelRatio { get; set; } = 0.5f;
    /// <summary>相邻采样间隔超过这个值就当断笔。
    /// 20 → **40**：手写板走兼容鼠标路时输入是突发的，实测报点间隔
    /// 常态到 ~30ms（Chromium 的 20 是按他自己的流定的）；40ms 以内继续预测，超过才断笔
    /// ——超过的那一下由显示层的"长度限速"收尾（不会一出一进地弹）。</summary>
    public double MaxGapMs { get; set; } = 40.0;
    /// <summary>一次最多给出几个预测点（按采样间隔铺满地平线）。</summary>
    public int MaxPoints { get; set; } = 4;

    // ---- 【本版新增】方向不稳定度自适应 ----------------------------------

    /// <summary>
    /// **【本版新增】方向不稳定度自适应强度**（0 = 关闭）。
    ///
    /// 原理：用「相邻采样点的速度方向变化（弧度）」的指数移动平均衡量这段运动
    /// 有多不平稳。乱涂、密集锯齿、快速起停时角速度极高，此时**任何**运动学外推
    /// 都会把线甩到反方向去（实测可见甩尾占 20%~66% 的帧）。与其等它错，不如
    /// 在方向本身就不可预测时主动把预测强度压下去。
    ///
    /// 实测（38 条极端轨迹 + 635 条教学轨迹）：
    ///   随机乱涂   可见甩尾 37.8% → 3.9%
    ///   密集锯齿   可见甩尾 66.4% → 19.4%
    ///   快速螺旋   可见甩尾  2.5% → 1.7%
    ///   教学场景   可见甩尾 0.10% → 0.09%（稳态书写方向平稳，几乎不触发）
    ///
    /// 建议 1.5~3.0。设 0 等于关闭。
    /// </summary>
    public float DirAdaptive { get; set; } = 2.0f;

    /// <summary>
    /// 方向不稳定度的起算阈值（弧度/采样点）。
    /// 低于它完全不收缩 —— 这样正常板书（实测 0.03~0.32）不受影响。
    /// 建议 0.35。调低会更激进（更容易触发收缩）。
    /// </summary>
    public float DirThresh { get; set; } = 0.35f;

    /// <summary>方向自适应最多收缩到几倍。建议 0.15。</summary>
    public float DirFloor { get; set; } = 0.15f;

    /// <summary>方向不稳定度 EMA 的平滑系数。越大反应越快、越容易被单个抖动带偏。建议 0.3。</summary>
    public float DirEmaA { get; set; } = 0.3f;

    public const double MinHorizonMs = 8.0;
    /// <summary>地平线的**推荐区间上界**：8~15 是用真实笔迹数据扫出来的。</summary>
    public const double MaxHorizonMs = 30.0;   // 推荐上限；延迟绑定时取实测值
    /// <summary>
    /// 地平线的**硬上限**：命令行（`--predictms`）给的再大也收在这里。
    ///
    /// 「推荐 8~15」和「只能到 15」是两件事：要把"预测过头有多难受"
    /// 在真机上看明白，就得敢把地平线调到远超推荐值——夹在 15 的话，人只会觉得
    /// "开了跟没开一样"，然后把"功能没用"这个错误结论记下来。上限仍然留着，
    /// 防的是手滑填个 100000。
    /// </summary>
    public const double HardMaxHorizonMs = 200.0;   // 默认 20ms，仍在硬上限内

    // ---- 状态 -------------------------------------------------------------

    private float _x0, _y0, _x1, _y1, _x2, _y2;   // 最近三点（旧→新）
    private double _t0, _t1, _t2;
    private int _count;

    private float _vx, _vy;         // 最近一次速度（px/ms）
    private float _px, _py;         // 上一次的速度（算加速度用）
    private float _ax, _ay;         // 加速度（px/ms²）
    private bool _hasPrevVelocity;

    // ---- 【本版新增】自适应状态 ------------------------------------------
    private float _prevSpeed = -1f;  // 上一次的速度大小（算相对变化用）
    private float _adaptFactor = 1f; // 当前收缩系数
    private bool _predicting;        // 迟滞用：当前是否处于「预测中」

    // ---- 【本版新增】方向不稳定度状态 ------------------------------------
    private float _prvx, _prvy;      // 上一次的**原始**速度（未平滑，看真实输入）
    private bool _hasRawVelocity;
    private float _dirEma;           // 方向变化（弧度）的指数移动平均

    /// <summary>预测点的时间偏移（复用，避免每帧分配）。</summary>
    private readonly double[] _taus = new double[8];

    /// <summary>最近一次算出来的速度大小（诊断/测试用）。</summary>
    public float Speed => MathF.Sqrt(_vx * _vx + _vy * _vy);
    /// <summary>最近一次算出来的加速度大小（诊断/测试用）。</summary>
    public float Acceleration => MathF.Sqrt(_ax * _ax + _ay * _ay);
    /// <summary>当前累积的采样点数（诊断用）。</summary>
    public int Count => _count;
    /// <summary>【本版新增】当前的自适应收缩系数（诊断用，1 = 未收缩）。</summary>
    public float AdaptFactor => _adaptFactor;
    /// <summary>【本版新增】自适应实际生效的阻尼（= Damping × _adaptFactor，诊断用）。</summary>
    public float EffectiveDamping => Damping * _adaptFactor;

    /// <summary>
    /// 诊断计数（`--pdmetrics` 用）：急转门（60~90°档）与反向门（>90°档）各自触发了几次。
    /// 每次 `Reset()`（= 一笔开始 / 换笔）清零。
    /// </summary>
    public int GateFires, ReversalFires;

    public void Reset()
    {
        _count = 0;
        _vx = _vy = _px = _py = _ax = _ay = 0f;
        _hasPrevVelocity = false;
        _prevSpeed = -1f;
        _adaptFactor = 1f;
        _predicting = false;
        _prvx = _prvy = 0f;
        _hasRawVelocity = false;
        _dirEma = 0f;
        GateFires = 0; ReversalFires = 0;
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

        // 反向/急转：新方向与上一次速度**完全反向**（>90°）一律丢；
        // **急转**（夹角 > SharpTurnCos 阈值，默认 60°）也丢——宁可这一帧不预测，
        // 也不要在拐弯处甩出去一截。
        float turnShrink = 1f;
        if (_hasPrevVelocity)
        {
            float dot = rawVx * _px + rawVy * _py;
            float mags = MathF.Sqrt(rawVx * rawVx + rawVy * rawVy)
                       * MathF.Sqrt(_px * _px + _py * _py);
            float cosTurn = mags > 1e-12f ? dot / mags : 1f;

            // 真·反向（夹角 > 90°）：仍然硬丢速——这是安全底线，
            // 否则会沿旧方向甩出去一截。
            if (cosTurn < HardTurnCos)
            {
                ReversalFires++;
                _vx = _vy = 0f;
                _ax = _ay = 0f;
                // 【本版修复】丢速后不能让这个 0 变成下一帧的"上一次速度"。
                // 原版只清 _vx/_ax，_hasPrevVelocity 仍为 true → 下一帧算
                //   _ax = (v_new − 0) / dt
                // 凭空造出一个大加速度，二阶项把预测多推 ~31%（0.5px/ms 速度下
                // 4.0px → 5.25px），表现就是"急转后跳一下"。
                // 置 false 后，下一帧走 _ax = 0 分支，且那一帧 _px 本来就是 0、
                // 门控也判不出东西，没有副作用。
                _hasPrevVelocity = false;
                _prevSpeed = -1f;
                return;
            }

            // 急转（夹角介于 HardTurnCos 与 SharpTurnCos 之间）：
            // **不丢速度**，改为按角度线性收缩预测强度。
            // 老版这里是直接丢速：在教学场景（中文/函数/几何/公式）实测急转门
            // 触发率 3.46%，是 UCI 英文单字母（0.36%）的近 10 倍，
            // 每触发一次尾巴就断一次，表现出来就是"跳"。
            if (TurnDepth > 0f && cosTurn < SharpTurnCos)
            {
                GateFires++;
                float denom = MathF.Max(SharpTurnCos - HardTurnCos, 1e-6f);
                float u = Math.Clamp((SharpTurnCos - cosTurn) / denom, 0f, 1f);
                turnShrink = 1f - TurnDepth * u;
            }
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

        // 【本版新增】速度自适应：速度突变（加速/减速/急转后重建）时收缩预测强度。
        // 放在最后算，保证用的是本帧最终速度。
        float speedFactor = 1f;
        if (SpeedAdaptive > 0f)
        {
            float sp = Speed;
            if (_prevSpeed > 1e-6f && sp > 1e-6f)
            {
                float rel = MathF.Abs(sp - _prevSpeed) / _prevSpeed;
                speedFactor = Math.Clamp(1f - SpeedAdaptive * rel, AdaptiveFloor, 1f);
            }
            _prevSpeed = sp;
        }
        // 【本版新增】方向不稳定度自适应：
        // 乱涂/密集锯齿时角速度极高，此时任何运动学外推都会画反，
        // 按不稳定度主动收缩。正常书写方向平稳，不触发。
        float dirFactor = 1f;
        if (DirAdaptive > 0f)
        {
            if (_hasRawVelocity)
            {
                float dotR = rawVx * _prvx + rawVy * _prvy;
                float magR = MathF.Sqrt(rawVx * rawVx + rawVy * rawVy)
                           * MathF.Sqrt(_prvx * _prvx + _prvy * _prvy);
                if (magR > 1e-12f)
                {
                    float ct = Math.Clamp(dotR / magR, -1f, 1f);
                    _dirEma = (1f - DirEmaA) * _dirEma + DirEmaA * MathF.Acos(ct);
                }
            }
            _prvx = rawVx; _prvy = rawVy;
            _hasRawVelocity = true;
            float excess = MathF.Max(0f, _dirEma - DirThresh);
            dirFactor = Math.Clamp(1f - DirAdaptive * excess, DirFloor, 1f);
        }

        // 三个收缩取最保守的那个：速度突变 / 急转折角 / 方向不稳定度
        _adaptFactor = MathF.Min(MathF.Min(speedFactor, turnShrink), dirFactor);
    }

    /// <summary>
    /// 生成预测点（时间序，全部在最后一点之后）。
    /// </summary>
    /// <returns>写进 <paramref name="outPoints"/> 的个数；0 = 不预测</returns>
    public int Predict(PredictedPoint[] outPoints)
    {
        if (_count < 2 || outPoints == null || outPoints.Length == 0) return 0;

        float speed = Speed;

        // 不预测门槛（可带迟滞）。
        // 无迟滞：速度 < MinSpeed 就不预测。速度在门槛附近抖 → 反复开关 → 尾巴闪烁。
        // 有迟滞：进入要 > MinSpeed，退出要 < MinSpeed × MinSpeedExitRatio，中间维持现状。
        if (MinSpeedExitRatio > 0f)
        {
            if (_predicting)
            {
                if (speed < MinSpeed * MinSpeedExitRatio) _predicting = false;
            }
            else if (speed > MinSpeed)
            {
                _predicting = true;
            }
            if (!_predicting) return 0;
        }
        else
        {
            if (speed < MinSpeed) return 0;             // 慢写：不预测
        }

        double horizon = Math.Clamp(HorizonMs, MinHorizonMs, HardMaxHorizonMs);

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
        // 实测：5 ms 采样 + 8 ms 地平线时，只铺到 5 ms 的话"吃到"只有 40%，
        // 补上 8 ms 那一点之后才吃满。
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

        // 【本版新增】把自适应系数乘进阻尼
        float damp = Damping * _adaptFactor;
        float adamp = AccelDamping * _adaptFactor;

        for (int i = 0; i < count; i++)
        {
            double tau = _taus[i];
            float t = (float)tau;
            float dx1 = _vx * t * damp;
            float dy1 = _vy * t * damp;
            float dx2 = 0.5f * _ax * t * t * adamp;
            float dy2 = 0.5f * _ay * t * t * adamp;

            // ③ 线性度门控（v6）：二阶项不得喧宾夺主（Google 的 γμ 同思想）
            if (AccelRatio > 0f)
            {
                float lim = AccelRatio * MathF.Sqrt(dx1 * dx1 + dy1 * dy1);
                float m2 = MathF.Sqrt(dx2 * dx2 + dy2 * dy2);
                if (m2 > lim && m2 > 1e-9f)
                {
                    float k2 = lim / m2;
                    dx2 *= k2; dy2 *= k2;
                }
            }

            float dx = dx1 + dx2;
            float dy = dy1 + dy2;

            // ② 自适应限幅（v6）：上限跟着「这一帧能走多远」走
            float tailCap = MaxDistance;
            if (AdaptiveCapFactor > 0f)
                tailCap = MathF.Min(tailCap, speed * (float)LatencyMs * AdaptiveCapFactor);

            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > tailCap && len > 1e-9f)
            {
                float k = tailCap / len;
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
    public void ClampHorizon() => HorizonMs = Math.Clamp(HorizonMs, MinHorizonMs, HardMaxHorizonMs);

    /// <summary>
    /// **① 延迟绑定**：把实测端到端延迟灌进来，地平线与自适应限幅同时跟着走。
    ///
    /// 学自微软 Wet Ink Predictor —— 它的预测距离 = 速度 × **实测延迟周期**，
    /// 而不是固定外推。这样换设备、换屏幕刷新率都不用重新调参。
    /// </summary>
    /// <param name="measuredLatencyMs">实测端到端延迟（毫秒），如 InkTeach 的 21.3</param>
    public void SetMeasuredLatency(double measuredLatencyMs)
    {
        if (measuredLatencyMs <= 0) return;
        LatencyMs = measuredLatencyMs;
        HorizonMs = Math.Clamp(measuredLatencyMs, MinHorizonMs, HardMaxHorizonMs);
    }
}
