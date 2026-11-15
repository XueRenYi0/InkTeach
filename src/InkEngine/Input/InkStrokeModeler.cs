// [停用 2026-10-05] 本文件的渲染实验已停用（入口开关已注释，代码保留）。
// 见 已停用-渲染实验.md：恢复方法 + 停用前完整源码备份（.revert/2026-10-05-渲染减法）。
// =====================================================================================
//  ink-stroke-modeler 的 1:1 移植（C#）
//
//  来源：https://github.com/google/ink-stroke-modeler
//  版本：main @ f2388813（2026-08-02，"Add a Bazel lockfile"）
//  许可证：Apache License 2.0（Copyright 2022-2024 Google LLC，见文件尾部的许可声明）
//
//  移植纪律（2026-10-03 与用户约定）：
//    · 一个模式 = 一个来源，算法、运算顺序、默认参数一字不改；
//    · 只做"外壳适配"：C++ → C#、Time/Duration 换成 double（单位 = 秒，由调用方换算）、
//      std::deque 换成 LinkedList/List（行为等价）、absl::Status 换成异常；
//    · 上游的边界行为（重复点、时间不前进、无压感、首末点）原样保留；
//    · 本文件不引用引擎里的任何其它东西（连 Vector2 都不用），保证可以和上游逐行对照。
//
//  与上游的已知差异（都只影响"无效输入/未用到的分支"，不影响有效输入的输出）：
//    ① KalmanPredictor 未移植（Reset 时若指定 Kalman 会抛 NotSupportedException）；
//       默认预测器是 StrokeEndPredictor，已 1:1 移植。本引擎的渲染路径不使用 Predict()。
//    ② 参数校验按 params.h 的注释实现，未逐条抄 params.cc（有效参数下行为一致）。
//    ③ StylusStateModeler.Project 里"丢弃前面的段"一步用 RemoveRange 一次完成，
//       等价于上游逐次 pop_front 的循环。
// =====================================================================================

using System.Diagnostics;

namespace InkEngine;

// -------------------------------------------------------------------------------------
//  types.h / types.cc
// -------------------------------------------------------------------------------------

/// <summary>二维向量/点（对应上游 Vec2）。</summary>
internal struct InkVec2 : IEquatable<InkVec2>
{
    public float X;
    public float Y;

    public InkVec2(float x, float y) { X = x; Y = y; }

    public float Magnitude() => MathF.Sqrt(X * X + Y * Y);   // 上游用 std::hypot，float 下等价

    public bool IsFinite() => float.IsFinite(X) && float.IsFinite(Y);

    public static float DotProduct(InkVec2 a, InkVec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>与另一个向量的夹角（弧度，[0, π]）；任一输入非有限时抛异常（上游返回错误状态）。</summary>
    public float AbsoluteAngleTo(InkVec2 other)
    {
        if (!IsFinite() || !other.IsFinite())
            throw new InkStrokeModelException($"Non-finite inputs: this={this}; other={other}.");
        float magnitude = Magnitude();
        float otherMagnitude = other.Magnitude();
        if (magnitude == 0 || otherMagnitude == 0) return 0;
        InkVec2 unit = this / magnitude;
        InkVec2 otherUnit = other / otherMagnitude;
        float dot = DotProduct(unit, otherUnit);
        return MathF.Acos(Math.Clamp(dot, -1f, 1f));
    }

    public static InkVec2 operator +(InkVec2 a, InkVec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static InkVec2 operator -(InkVec2 a, InkVec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static InkVec2 operator *(float s, InkVec2 v) => new(s * v.X, s * v.Y);
    public static InkVec2 operator *(InkVec2 v, float s) => new(v.X * s, v.Y * s);
    public static InkVec2 operator /(InkVec2 v, float s) => new(v.X / s, v.Y / s);

    public bool Equals(InkVec2 other) => X == other.X && Y == other.Y;
    public override bool Equals(object obj) => obj is InkVec2 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>输入事件类型（对应上游 Input::EventType）。</summary>
internal enum InkEventType
{
    Down,
    Move,
    Up,
}

/// <summary>喂给模型器的原始输入（对应上游 Input）。时间单位由调用方决定，本引擎用秒。</summary>
internal struct InkInput : IEquatable<InkInput>
{
    public InkEventType EventType;
    public InkVec2 Position;
    public double Time;

    /// <summary>[0,1]；负数 = 未知（上游约定 -1）。</summary>
    public float Pressure;
    /// <summary>[0, π/2]；负数 = 未知。</summary>
    public float Tilt;
    /// <summary>[0, 2π)；负数 = 未知。</summary>
    public float Orientation;

    public static InkInput Make(InkEventType type, InkVec2 position, double time,
                                float pressure = -1f, float tilt = -1f, float orientation = -1f)
        => new()
        {
            EventType = type, Position = position, Time = time,
            Pressure = pressure, Tilt = tilt, Orientation = orientation,
        };

    public bool Equals(InkInput other)
        => EventType == other.EventType && Position.Equals(other.Position) && Time == other.Time
           && Pressure == other.Pressure && Tilt == other.Tilt && Orientation == other.Orientation;
    public override bool Equals(object obj) => obj is InkInput other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(EventType, Position, Time, Pressure, Tilt, Orientation);
}

/// <summary>模型器产出的一个点（对应上游 Result）。</summary>
internal struct InkResult
{
    public InkVec2 Position;
    public InkVec2 Velocity;
    public InkVec2 Acceleration;
    public double Time;

    public float Pressure;
    public float Tilt;
    public float Orientation;

    /// <summary>对应上游 `return {};`（默认构造）时的取值：位置零、三个触笔字段为 -1。</summary>
    public static InkResult Empty => new()
    {
        Pressure = -1f, Tilt = -1f, Orientation = -1f,
    };
}

/// <summary>笔尖状态（对应上游 TipState）。</summary>
internal struct InkTipState
{
    public InkVec2 Position;
    public InkVec2 Velocity;
    public InkVec2 Acceleration;
    public double Time;
}

/// <summary>触笔的非位置状态（对应上游 StylusState）。</summary>
internal struct InkStylusState
{
    public float Pressure;
    public float Tilt;
    public float Orientation;

    public static InkStylusState Make(float pressure, float tilt, float orientation)
        => new() { Pressure = pressure, Tilt = tilt, Orientation = orientation };
}

/// <summary>模型器内部错误（对应上游 absl::Status 的错误返回）。</summary>
internal sealed class InkStrokeModelException : Exception
{
    public InkStrokeModelException(string message) : base(message) { }
}

// -------------------------------------------------------------------------------------
//  params.h
// -------------------------------------------------------------------------------------

/// <summary>对应上游 PositionModelerParams::LoopContractionMitigationParameters。</summary>
internal sealed class InkLoopContractionMitigationParams
{
    public bool IsEnabled = false;
    public float SpeedLowerBound = -1f;
    public float SpeedUpperBound = -1f;
    public float InterpolationStrengthAtSpeedLowerBound = -1f;
    public float InterpolationStrengthAtSpeedUpperBound = -1f;
    public double MinSpeedSamplingWindow = -1;
}

/// <summary>对应上游 PositionModelerParams。</summary>
internal sealed class InkPositionModelerParams
{
    public float SpringMassConstant = 11f / 32400f;
    public float DragConstant = 72f;
    public InkLoopContractionMitigationParams LoopContractionMitigationParams = new();
}

/// <summary>对应上游 SamplingParams。</summary>
internal sealed class InkSamplingParams
{
    public double MinOutputRate = -1;
    public float EndOfStrokeStoppingDistance = -1f;
    public int EndOfStrokeMaxIterations = 20;
    public int MaxOutputsPerCall = 100000;
    public double MaxEstimatedAngleToTraversePerInput = -1;
}

/// <summary>对应上游 StylusStateModelerParams（当前版本只有这一个字段）。</summary>
internal sealed class InkStylusStateModelerParams
{
    public bool UseStrokeNormalProjection = false;
}

/// <summary>对应上游 WobbleSmootherParams。</summary>
internal sealed class InkWobbleSmootherParams
{
    public bool IsEnabled = true;
    public double Timeout = -1;
    public float SpeedFloor = -1f;
    public float SpeedCeiling = -1f;
}

/// <summary>对应上游 PredictionParams 的三选一。</summary>
internal enum InkPredictionKind
{
    StrokeEnd,
    Kalman,
    Disabled,
}

/// <summary>对应上游 StrokeModelParams。</summary>
internal sealed class InkStrokeModelParams
{
    public InkWobbleSmootherParams WobbleSmootherParams = new();
    public InkPositionModelerParams PositionModelerParams = new();
    public InkSamplingParams SamplingParams = new();
    public InkStylusStateModelerParams StylusStateModelerParams = new();
    public InkPredictionKind PredictionKind = InkPredictionKind.StrokeEnd;

    /// <summary>
    /// README 的示例参数（"good starting point"）：
    /// wobble timeout .04s / floor 1.31 / ceiling 1.44；输出 ≥180Hz；收笔停止距离 .001。
    /// 注意：模型器空间/时间都是无量纲的，速度阈值（1.31/1.44）要按"位置单位/秒"理解；
    /// 本引擎位置是画布物理像素、时间是秒，所以这两个值只在"几乎停住"时起作用
    /// （上游注释：取期望速度的 2%~3%；真笔写字远高于它 → 抖动主要由弹簧模型吃掉）。
    /// </summary>
    public static InkStrokeModelParams Recommended() => new()
    {
        WobbleSmootherParams = new InkWobbleSmootherParams
        {
            IsEnabled = true,
            Timeout = 0.04,
            SpeedFloor = 1.31f,
            SpeedCeiling = 1.44f,
        },
        PositionModelerParams = new InkPositionModelerParams
        {
            SpringMassConstant = 11f / 32400f,
            DragConstant = 72f,
            LoopContractionMitigationParams = new InkLoopContractionMitigationParams
            {
                IsEnabled = false,
                SpeedLowerBound = -1f,
                SpeedUpperBound = -1f,
                InterpolationStrengthAtSpeedLowerBound = -1f,
                InterpolationStrengthAtSpeedUpperBound = -1f,
                MinSpeedSamplingWindow = -1,
            },
        },
        SamplingParams = new InkSamplingParams
        {
            MinOutputRate = 180,
            EndOfStrokeStoppingDistance = 0.001f,
            EndOfStrokeMaxIterations = 20,
            MaxOutputsPerCall = 100000,
            MaxEstimatedAngleToTraversePerInput = -1,
        },
        StylusStateModelerParams = new InkStylusStateModelerParams
        {
            UseStrokeNormalProjection = false,
        },
        PredictionKind = InkPredictionKind.StrokeEnd,
    };

    /// <summary>参数校验（对应上游 ValidateStrokeModelParams；有效参数下行为一致，见文件头差异②）。</summary>
    public string Validate()
    {
        if (!double.IsFinite(SamplingParams.MinOutputRate) || SamplingParams.MinOutputRate <= 0)
            return "sampling_params.min_output_rate must be finite and positive.";
        if (!float.IsFinite(SamplingParams.EndOfStrokeStoppingDistance) ||
            SamplingParams.EndOfStrokeStoppingDistance <= 0)
            return "sampling_params.end_of_stroke_stopping_distance must be finite and positive.";
        if (SamplingParams.EndOfStrokeMaxIterations <= 0)
            return "sampling_params.end_of_stroke_max_iterations must be positive.";
        if (SamplingParams.MaxOutputsPerCall <= 0)
            return "sampling_params.max_outputs_per_call must be positive.";

        if (!float.IsFinite(PositionModelerParams.SpringMassConstant) ||
            PositionModelerParams.SpringMassConstant <= 0)
            return "position_modeler_params.spring_mass_constant must be finite and positive.";
        if (!float.IsFinite(PositionModelerParams.DragConstant) ||
            PositionModelerParams.DragConstant < 0)
            return "position_modeler_params.drag_constant must be finite and non-negative.";

        var loop = PositionModelerParams.LoopContractionMitigationParams;
        if (loop.IsEnabled)
        {
            if (!float.IsFinite(loop.SpeedLowerBound) || loop.SpeedLowerBound < 0)
                return "loop_contraction_mitigation_params.speed_lower_bound must be >= 0.";
            if (!float.IsFinite(loop.SpeedUpperBound) || loop.SpeedUpperBound < loop.SpeedLowerBound)
                return "loop_contraction_mitigation_params.speed_upper_bound must be >= speed_lower_bound.";
            if (loop.InterpolationStrengthAtSpeedLowerBound < loop.InterpolationStrengthAtSpeedUpperBound ||
                loop.InterpolationStrengthAtSpeedLowerBound > 1)
                return "loop_contraction_mitigation_params.interpolation_strength_at_speed_lower_bound invalid.";
            if (loop.InterpolationStrengthAtSpeedUpperBound < 0)
                return "loop_contraction_mitigation_params.interpolation_strength_at_speed_upper_bound invalid.";
            if (!double.IsFinite(loop.MinSpeedSamplingWindow) || loop.MinSpeedSamplingWindow < 0)
                return "loop_contraction_mitigation_params.min_speed_sampling_window must be >= 0.";
        }

        var wobble = WobbleSmootherParams;
        if (wobble.IsEnabled)
        {
            if (!double.IsFinite(wobble.Timeout) || wobble.Timeout < 0)
                return "wobble_smoother_params.timeout must be finite and non-negative.";
            if (!float.IsFinite(wobble.SpeedFloor) || !float.IsFinite(wobble.SpeedCeiling) ||
                wobble.SpeedFloor > wobble.SpeedCeiling || wobble.SpeedFloor < 0)
                return "wobble_smoother_params speed bounds invalid.";
        }

        if (PredictionKind == InkPredictionKind.Kalman)
            return "KalmanPredictor 尚未移植（见文件头差异①）——请用 StrokeEnd 或 Disabled。";

        return null;
    }
}

// -------------------------------------------------------------------------------------
//  internal/utils.h / utils.cc（数学工具）
// -------------------------------------------------------------------------------------

internal static class InkModelMath
{
    /// <summary>Clamp(value, 0, 1)。</summary>
    public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

    /// <summary>
    /// 对应上游 Normalize01：把 value 相对 [start,end] 归一化并夹到 [0,1]；
    /// start == end 时，value &gt; start 返回 1，否则 0。
    /// </summary>
    public static float Normalize01(float start, float end, float value)
    {
        if (start == end) return value > start ? 1f : 0f;
        return Clamp01((value - start) / (end - start));
    }

    /// <summary>对应上游 Interp：start + (end - start) * Clamp01(t)。</summary>
    public static float Interp(float start, float end, float t)
        => start + (end - start) * Clamp01(t);
    public static InkVec2 Interp(InkVec2 start, InkVec2 end, float t)
        => start + (end - start) * Clamp01(t);
    public static double Interp(double start, double end, float t)
        => start + (end - start) * Clamp01(t);

    /// <summary>对应上游 InverseLerp（不夹）。</summary>
    public static float InverseLerp(float a, float b, float value)
    {
        if (b - a == 0f) return 0f;
        return (value - a) / (b - a);
    }

    /// <summary>对应上游 InterpAngle：沿短弧插值，返回 [0, 2π)。</summary>
    public static float InterpAngle(float start, float end, float t)
    {
        static float NormalizeAngle(float angle)
        {
            while (angle < 0) angle += 2f * MathF.PI;
            while (angle > 2f * MathF.PI) angle -= 2f * MathF.PI;
            return angle;
        }

        start = NormalizeAngle(start);
        end = NormalizeAngle(end);
        float delta = end - start;
        if (delta < -MathF.PI) end += 2f * MathF.PI;
        else if (delta > MathF.PI) end -= 2f * MathF.PI;
        return NormalizeAngle(Interp(start, end, t));
    }

    /// <summary>对应上游 InterpResult：orientation 走角度插值；任一端的未知字段（&lt;0）结果为 -1。</summary>
    public static InkResult InterpResult(InkResult start, InkResult end, float t)
    {
        InkResult r = InkResult.Empty;
        r.Position = Interp(start.Position, end.Position, t);
        r.Velocity = Interp(start.Velocity, end.Velocity, t);
        r.Acceleration = Interp(start.Acceleration, end.Acceleration, t);
        r.Time = Interp(start.Time, end.Time, t);
        r.Pressure = start.Pressure < 0 || end.Pressure < 0
            ? -1f : Interp(start.Pressure, end.Pressure, t);
        r.Tilt = start.Tilt < 0 || end.Tilt < 0
            ? -1f : Interp(start.Tilt, end.Tilt, t);
        r.Orientation = start.Orientation < 0 || end.Orientation < 0
            ? -1f : InterpAngle(start.Orientation, end.Orientation, t);
        return r;
    }

    /// <summary>对应上游 Distance。</summary>
    public static float Distance(InkVec2 start, InkVec2 end) => (end - start).Magnitude();

    /// <summary>对应上游 NearestPointOnSegment：返回最接近点在线段上的比例。</summary>
    public static float NearestPointOnSegment(InkVec2 segmentStart, InkVec2 segmentEnd, InkVec2 point)
    {
        if (segmentStart.Equals(segmentEnd)) return 0f;
        InkVec2 segmentVector = segmentEnd - segmentStart;
        InkVec2 projectionVector = point - segmentStart;
        return Clamp01(InkVec2.DotProduct(projectionVector, segmentVector) /
                       InkVec2.DotProduct(segmentVector, segmentVector));
    }

    /// <summary>
    /// 对应上游 GetStrokeNormal：按速度/加速度估笔画法线（指向左侧）；都为零时返回 null。
    /// </summary>
    public static InkVec2? GetStrokeNormal(InkTipState tipState, double prevTime)
    {
        const float kCosineHalfDegree = 0.99996192f;

        static InkVec2 Orthogonal(InkVec2 v) => new(-v.Y, v.X);

        float vMagnitude = tipState.Velocity.Magnitude();
        float aMagnitude = tipState.Acceleration.Magnitude();

        if (vMagnitude == 0 && aMagnitude == 0) return null;
        if (vMagnitude == 0) return Orthogonal(tipState.Acceleration);
        if (aMagnitude == 0) return Orthogonal(tipState.Velocity);

        if (MathF.Abs(InkVec2.DotProduct(tipState.Velocity, tipState.Acceleration)) >
            kCosineHalfDegree * vMagnitude * aMagnitude)
        {
            return Orthogonal(tipState.Velocity);
        }

        static InkVec2 UnitVec(InkVec2 x) => x / x.Magnitude();
        double deltaT = tipState.Time - prevTime;
        InkVec2 strokeDir = UnitVec(tipState.Velocity) +
                            UnitVec(tipState.Velocity + tipState.Acceleration * (float)deltaT);
        return Orthogonal(strokeDir);
    }

    /// <summary>对应上游 ProjectToSegmentAlongNormal。</summary>
    public static float? ProjectToSegmentAlongNormal(InkVec2 segmentStart, InkVec2 segmentEnd,
                                                     InkVec2 position, InkVec2 strokeNormal)
    {
        static float Cross(InkVec2 a, InkVec2 b) => a.X * b.Y - a.Y * b.X;

        InkVec2 v = segmentEnd - segmentStart;
        float det = Cross(strokeNormal, v);
        if (det == 0) return null;

        InkVec2 w = segmentStart - position;
        float param = Cross(w, strokeNormal) / det;
        if (param < 0 || param > 1) return null;
        return param;
    }
}

// -------------------------------------------------------------------------------------
//  internal/wobble_smoother.h / .cc
// -------------------------------------------------------------------------------------

/// <summary>
/// 对应上游 WobbleSmoother：把位置做"按时间加权的移动平均"，再用平均速度在
/// 平均值与原始值之间插值——慢速（低于 floor）几乎全用平均值，快速（高于 ceiling）用原始值。
/// </summary>
internal sealed class InkWobbleSmoother
{
    private struct Sample
    {
        public InkVec2 Position;
        public InkVec2 WeightedPosition;
        public float Distance;
        public double Duration;
        public double Time;
    }

    private sealed class State
    {
        public readonly LinkedList<Sample> Samples = new();
        public InkVec2 WeightedPositionSum;
        public float DistanceSum;
        public double DurationSum;
    }

    private readonly State _state = new();
    private State _savedState;
    private bool _saveActive;
    private InkWobbleSmootherParams _params;

    public void Reset(InkWobbleSmootherParams p, InkVec2 position, double time)
    {
        _state.Samples.Clear();
        _state.WeightedPositionSum = default;
        _state.DistanceSum = 0;
        _state.DurationSum = 0;
        _state.Samples.AddLast(new Sample { Position = position, Time = time });
        _saveActive = false;
        _params = p;
    }

    public InkVec2 Update(InkVec2 position, double time)
    {
        if (!_params.IsEnabled) return position;

        double deltaTime = time - _state.Samples.Last!.Value.Time;
        var sample = new Sample
        {
            Position = position,
            WeightedPosition = position * (float)deltaTime,
            Distance = InkModelMath.Distance(position, _state.Samples.Last.Value.Position),
            Duration = deltaTime,
            Time = time,
        };
        _state.Samples.AddLast(sample);
        _state.WeightedPositionSum += sample.WeightedPosition;
        _state.DistanceSum += sample.Distance;
        _state.DurationSum += sample.Duration;

        while (_state.Samples.First!.Value.Time < time - _params.Timeout)
        {
            var front = _state.Samples.First.Value;
            _state.WeightedPositionSum -= front.WeightedPosition;
            _state.DistanceSum -= front.Distance;
            _state.DurationSum -= front.Duration;
            _state.Samples.RemoveFirst();
        }

        if (_state.DurationSum == 0) return position;

        InkVec2 avgPosition = _state.WeightedPositionSum / (float)_state.DurationSum;
        float avgSpeed = _state.DistanceSum / (float)_state.DurationSum;
        return InkModelMath.Interp(
            avgPosition, position,
            InkModelMath.Normalize01(_params.SpeedFloor, _params.SpeedCeiling, avgSpeed));
    }

    public void Save()
    {
        var copy = new State();
        foreach (var s in _state.Samples) copy.Samples.AddLast(s);
        copy.WeightedPositionSum = _state.WeightedPositionSum;
        copy.DistanceSum = _state.DistanceSum;
        copy.DurationSum = _state.DurationSum;
        _savedState = copy;
        _saveActive = true;
    }

    public void Restore()
    {
        if (!_saveActive || _savedState == null) return;
        _state.Samples.Clear();
        foreach (var s in _savedState.Samples) _state.Samples.AddLast(s);
        _state.WeightedPositionSum = _savedState.WeightedPositionSum;
        _state.DistanceSum = _savedState.DistanceSum;
        _state.DurationSum = _savedState.DurationSum;
    }
}

// -------------------------------------------------------------------------------------
//  internal/position_modeler.h / .cc
// -------------------------------------------------------------------------------------

/// <summary>对应上游 PositionModeler：笔尖 = 质量块，弹簧拉向移动的锚点，带阻尼。</summary>
internal sealed class InkPositionModeler
{
    private InkPositionModelerParams _params;
    private InkTipState _state;
    private InkTipState? _savedState;

    public void Reset(InkTipState state, InkPositionModelerParams p)
    {
        _params = p;
        _state = state;
        _savedState = null;
    }

    public InkTipState CurrentState => _state;
    public InkPositionModelerParams Params => _params;

    private InkVec2 SpringAcceleration(InkTipState tipState, InkVec2 anchorPosition)
        => (anchorPosition - tipState.Position) / _params.SpringMassConstant
           - _params.DragConstant * tipState.Velocity;

    /// <summary>对应上游 NumberOfStepsBetweenInputs（返回本次要插值出几个输出）。</summary>
    public int NumberOfStepsBetweenInputs(InkInput start, InkInput end, InkSamplingParams sampling)
    {
        double deltaT = end.Time - start.Time;
        float floatDelta = (float)deltaT;
        int nSteps = (int)Math.Min(Math.Ceiling(floatDelta * sampling.MinOutputRate), int.MaxValue);

        InkVec2 estimatedDeltaV = SpringAcceleration(_state, end.Position) * floatDelta;
        InkVec2 estimatedEndV = _state.Velocity + estimatedDeltaV;
        float estimatedAngle = _state.Velocity.AbsoluteAngleTo(estimatedEndV);

        if (sampling.MaxEstimatedAngleToTraversePerInput > 0)
        {
            int stepsForAngle = (int)Math.Min(
                Math.Ceiling(estimatedAngle / sampling.MaxEstimatedAngleToTraversePerInput),
                int.MaxValue);
            if (stepsForAngle > nSteps) nSteps = stepsForAngle;
        }

        if (nSteps > sampling.MaxOutputsPerCall)
            throw new InkStrokeModelException(
                $"Input events are too far apart; requested {nSteps} > {sampling.MaxOutputsPerCall} samples.");
        return nSteps;
    }

    /// <summary>对应上游 Update：按锚点位置和时间推进一步。</summary>
    public InkTipState Update(InkVec2 anchorPosition, double time)
    {
        double deltaTime = time - _state.Time;
        _state.Acceleration = SpringAcceleration(_state, anchorPosition);
        _state.Velocity += (float)deltaTime * _state.Acceleration;
        _state.Position += (float)deltaTime * _state.Velocity;
        _state.Time = time;
        return _state;
    }

    /// <summary>对应上游 UpdateAlongLinearPath：沿"锚点直线段"插值 n 步，逐步 Update。</summary>
    public void UpdateAlongLinearPath(InkVec2 startAnchorPosition, double startTime,
                                      InkVec2 endAnchorPosition, double endTime,
                                      int nSamples, List<InkTipState> output)
    {
        for (int i = 1; i <= nSamples; ++i)
        {
            float interpValue = (float)i / nSamples;
            InkVec2 position = InkModelMath.Interp(startAnchorPosition, endAnchorPosition, interpValue);
            double time = InkModelMath.Interp(startTime, endTime, interpValue);
            output.Add(Update(position, time));
        }
    }

    /// <summary>对应上游 ModelEndOfStroke：固定锚点反复 Update，过冲就减半步长重试。</summary>
    public void ModelEndOfStroke(InkVec2 anchorPosition, double deltaTime,
                                 int maxIterations, float stopDistance, List<InkTipState> output)
    {
        for (int i = 0; i < maxIterations; ++i)
        {
            InkTipState previousState = _state;
            InkTipState candidate = Update(anchorPosition, previousState.Time + deltaTime);
            if (InkModelMath.Distance(previousState.Position, candidate.Position) < stopDistance)
                return;

            float closestT = InkModelMath.NearestPointOnSegment(
                previousState.Position, candidate.Position, anchorPosition);
            if (closestT < 1)
            {
                deltaTime *= 0.5;
                _state = previousState;
                continue;
            }
            output.Add(candidate);

            if (InkModelMath.Distance(candidate.Position, anchorPosition) < stopDistance)
                return;
        }
    }

    public void Save() => _savedState = _state;
    public void Restore() { if (_savedState.HasValue) _state = _savedState.Value; }
}

// -------------------------------------------------------------------------------------
//  internal/loop_contraction_mitigation_modeler.h / .cc
// -------------------------------------------------------------------------------------

/// <summary>
/// 对应上游 LoopContractionMitigationModeler：用速度的移动平均决定"弹簧结果 ↔ 原始折线
/// 最近点"的插值强度，缓解快速画圈时弹簧模型把圈画小的问题。
/// </summary>
internal sealed class InkLoopContractionMitigationModeler
{
    private struct SpeedSample
    {
        public float Speed;
        public double Time;
    }

    private readonly LinkedList<SpeedSample> _speedSamples = new();
    private LinkedList<SpeedSample> _savedSpeedSamples;
    private bool _saveActive;
    private InkLoopContractionMitigationParams _params;

    public void Reset(InkLoopContractionMitigationParams p)
    {
        _speedSamples.Clear();
        _saveActive = false;
        _params = p;
    }

    /// <summary>状态深拷贝（对应 C++ 的拷贝构造；Predict() 用它做"不改动原状态"的预测）。</summary>
    public InkLoopContractionMitigationModeler Clone()
    {
        var copy = new InkLoopContractionMitigationModeler { _params = _params };
        foreach (var s in _speedSamples) copy._speedSamples.AddLast(s);
        return copy;
    }

    public float GetInterpolationValue()
    {
        if (_speedSamples.Count == 0 || !_params.IsEnabled) return 1;

        float sum = 0;
        foreach (var sample in _speedSamples) sum += sample.Speed;
        float averageSpeed = sum / _speedSamples.Count;

        float sourceRatio = InkModelMath.Clamp01(InkModelMath.InverseLerp(
            _params.SpeedLowerBound, _params.SpeedUpperBound, averageSpeed));
        return InkModelMath.Interp(
            _params.InterpolationStrengthAtSpeedLowerBound,
            _params.InterpolationStrengthAtSpeedUpperBound,
            sourceRatio);
    }

    public float Update(InkVec2 velocity, double time)
    {
        if (!_params.IsEnabled) return 1;
        _speedSamples.AddLast(new SpeedSample { Speed = velocity.Magnitude(), Time = time });
        while (_speedSamples.Count > 0 &&
               _speedSamples.Last!.Value.Time - _speedSamples.First!.Value.Time >
               _params.MinSpeedSamplingWindow)
        {
            _speedSamples.RemoveFirst();
        }
        return GetInterpolationValue();
    }

    public void Save()
    {
        _savedSpeedSamples = new LinkedList<SpeedSample>(_speedSamples);
        _saveActive = true;
    }

    public void Restore()
    {
        if (!_saveActive || _savedSpeedSamples == null) return;
        _speedSamples.Clear();
        foreach (var s in _savedSpeedSamples) _speedSamples.AddLast(s);
    }
}

// -------------------------------------------------------------------------------------
//  internal/stylus_state_modeler.h / .cc
// -------------------------------------------------------------------------------------

/// <summary>对应上游 RawInputProjection。</summary>
internal struct InkRawInputProjection
{
    public int SegmentIndex;
    public float RatioAlongSegment;
}

/// <summary>
/// 对应上游 StylusStateModeler：把建模出来的笔尖位置投影回"原始输入折线"，
/// 沿折线插值压力/倾角/方向。
/// </summary>
internal sealed class InkStylusStateModeler
{
    private sealed class ModelerState
    {
        public bool ReceivedUnknownPressure;
        public bool ReceivedUnknownTilt;
        public bool ReceivedUnknownOrientation;
        public readonly List<InkResult> RawInputAndStylusStates = new();
        public InkRawInputProjection Projection;
    }

    private readonly ModelerState _state = new();
    private ModelerState _savedState;
    private bool _saveActive;
    private InkStylusStateModelerParams _params;

    /// <summary>状态深拷贝（对应 C++ 的拷贝构造；Predict() 用它做"不改动原状态"的预测）。</summary>
    public InkStylusStateModeler Clone()
    {
        var copy = new InkStylusStateModeler { _params = _params };
        copy._state.ReceivedUnknownPressure = _state.ReceivedUnknownPressure;
        copy._state.ReceivedUnknownTilt = _state.ReceivedUnknownTilt;
        copy._state.ReceivedUnknownOrientation = _state.ReceivedUnknownOrientation;
        copy._state.RawInputAndStylusStates.AddRange(_state.RawInputAndStylusStates);
        copy._state.Projection = _state.Projection;
        return copy;
    }

    public void Reset(InkStylusStateModelerParams p)
    {
        _state.RawInputAndStylusStates.Clear();
        _state.Projection = default;
        _state.ReceivedUnknownPressure = false;
        _state.ReceivedUnknownTilt = false;
        _state.ReceivedUnknownOrientation = false;
        _saveActive = false;
        _params = p;
    }

    public void Update(InkVec2 position, double time, InkStylusState state)
    {
        if (state.Pressure < 0 || float.IsNaN(state.Pressure)) _state.ReceivedUnknownPressure = true;
        if (state.Tilt < 0 || float.IsNaN(state.Tilt)) _state.ReceivedUnknownTilt = true;
        if (state.Orientation < 0 || float.IsNaN(state.Orientation)) _state.ReceivedUnknownOrientation = true;

        if (!_params.UseStrokeNormalProjection &&
            _state.ReceivedUnknownPressure && _state.ReceivedUnknownTilt &&
            _state.ReceivedUnknownOrientation)
        {
            _state.RawInputAndStylusStates.Clear();
            return;
        }

        InkVec2 velocity = default;
        InkVec2 acceleration = default;
        if (_state.RawInputAndStylusStates.Count > 0 &&
            time != _state.RawInputAndStylusStates[^1].Time)
        {
            velocity = (position - _state.RawInputAndStylusStates[^1].Position) /
                       (float)(time - _state.RawInputAndStylusStates[^1].Time);
            acceleration = (velocity - _state.RawInputAndStylusStates[^1].Velocity) /
                           (float)(time - _state.RawInputAndStylusStates[^1].Time);
        }

        _state.RawInputAndStylusStates.Add(new InkResult
        {
            Position = position,
            Velocity = velocity,
            Acceleration = acceleration,
            Time = time,
            Pressure = state.Pressure,
            Tilt = state.Tilt,
            Orientation = state.Orientation,
        });
    }

    private static InkRawInputProjection ProjectAlongStrokeNormal(
        InkVec2 position, InkVec2 acceleration, double time, InkVec2 strokeNormal,
        List<InkResult> rawInputPolyline, InkRawInputProjection previousProjection)
    {
        InkRawInputProjection? bestLeftProjection = null;
        InkRawInputProjection? bestRightProjection = null;
        float bestDistanceLeft = float.PositiveInfinity;
        float bestDistanceRight = float.PositiveInfinity;

        static void MaybeUpdate(InkRawInputProjection candidate, float distance,
                                ref InkRawInputProjection? bestProjection, ref float bestDistance)
        {
            if (distance < bestDistance)
            {
                bestProjection = candidate;
                bestDistance = distance;
            }
        }

        for (int i = previousProjection.SegmentIndex; i < rawInputPolyline.Count - 1; ++i)
        {
            InkVec2 segmentStart = rawInputPolyline[i].Position;
            InkVec2 segmentEnd = rawInputPolyline[i + 1].Position;

            float? segmentRatio = InkModelMath.ProjectToSegmentAlongNormal(
                segmentStart, segmentEnd, position, strokeNormal);
            if (!segmentRatio.HasValue) continue;

            if (i == previousProjection.SegmentIndex &&
                segmentRatio.Value <= previousProjection.RatioAlongSegment)
                continue;

            InkVec2 projection = InkModelMath.Interp(segmentStart, segmentEnd, segmentRatio.Value);
            float distance = InkModelMath.Distance(position, projection);

            var candidate = new InkRawInputProjection
            {
                SegmentIndex = i,
                RatioAlongSegment = segmentRatio.Value,
            };
            float dotProduct = InkVec2.DotProduct(projection - position, strokeNormal);
            if (dotProduct == 0)
            {
                return candidate;
            }
            else if (dotProduct < 0)
            {
                MaybeUpdate(candidate, distance, ref bestRightProjection, ref bestDistanceRight);
            }
            else
            {
                MaybeUpdate(candidate, distance, ref bestLeftProjection, ref bestDistanceLeft);
            }
        }

        if (bestLeftProjection.HasValue && bestRightProjection.HasValue)
        {
            // 加速度指向弯道内侧；法线恒指左侧 → 用点积决定取哪一侧（上游原意）。
            return InkVec2.DotProduct(strokeNormal, acceleration) > 0
                ? bestRightProjection.Value
                : bestLeftProjection.Value;
        }
        if (bestRightProjection.HasValue) return bestRightProjection.Value;
        return bestLeftProjection ?? previousProjection;
    }

    private static InkRawInputProjection ProjectToClosestPoint(
        InkVec2 position, List<InkResult> rawInputPolyline, InkRawInputProjection previousProjection)
    {
        InkRawInputProjection? bestProjection = null;
        float minDistance = float.PositiveInfinity;
        for (int i = 0; i < rawInputPolyline.Count - 1; ++i)
        {
            InkVec2 segmentStart = rawInputPolyline[i].Position;
            InkVec2 segmentEnd = rawInputPolyline[i + 1].Position;
            float segmentRatio = InkModelMath.NearestPointOnSegment(segmentStart, segmentEnd, position);
            if (i == previousProjection.SegmentIndex &&
                segmentRatio < previousProjection.RatioAlongSegment)
                continue;
            float distance = InkModelMath.Distance(
                position, InkModelMath.Interp(segmentStart, segmentEnd, segmentRatio));
            if (distance <= minDistance)
            {
                bestProjection = new InkRawInputProjection
                {
                    SegmentIndex = i,
                    RatioAlongSegment = segmentRatio,
                };
                minDistance = distance;
            }
        }
        return bestProjection ?? previousProjection;
    }

    /// <summary>对应上游 Project。</summary>
    public InkResult Project(InkTipState tip, InkVec2? strokeNormal)
    {
        List<InkResult> states = _state.RawInputAndStylusStates;
        if (states.Count == 0) return InkResult.Empty;

        InkRawInputProjection projection;
        if (_params.UseStrokeNormalProjection && strokeNormal.HasValue)
        {
            projection = ProjectAlongStrokeNormal(
                tip.Position, tip.Acceleration, tip.Time, strokeNormal.Value, states, _state.Projection);
        }
        else
        {
            projection = ProjectToClosestPoint(tip.Position, states, _state.Projection);
        }

        // 丢弃更早的段：投影不允许回退，所以这些段以后再也用不到。
        // （上游是逐次 pop_front；这里一次性移除，行为等价——见文件头差异③。）
        if (projection.SegmentIndex > 0)
        {
            states.RemoveRange(0, projection.SegmentIndex);
            projection.SegmentIndex = 0;
        }
        _state.Projection = projection;

        InkResult projectedResult = states.Count > 1
            ? InkModelMath.InterpResult(states[0], states[1], projection.RatioAlongSegment)
            : states[0];

        projectedResult.Time = tip.Time;
        if (_state.ReceivedUnknownPressure) projectedResult.Pressure = -1f;
        if (_state.ReceivedUnknownTilt) projectedResult.Tilt = -1f;
        if (_state.ReceivedUnknownOrientation) projectedResult.Orientation = -1f;
        return projectedResult;
    }

    public void Save()
    {
        var copy = new ModelerState
        {
            ReceivedUnknownPressure = _state.ReceivedUnknownPressure,
            ReceivedUnknownTilt = _state.ReceivedUnknownTilt,
            ReceivedUnknownOrientation = _state.ReceivedUnknownOrientation,
            Projection = _state.Projection,
        };
        copy.RawInputAndStylusStates.AddRange(_state.RawInputAndStylusStates);
        _savedState = copy;
        _saveActive = true;
    }

    public void Restore()
    {
        if (!_saveActive || _savedState == null) return;
        _state.RawInputAndStylusStates.Clear();
        _state.RawInputAndStylusStates.AddRange(_savedState.RawInputAndStylusStates);
        _state.ReceivedUnknownPressure = _savedState.ReceivedUnknownPressure;
        _state.ReceivedUnknownTilt = _savedState.ReceivedUnknownTilt;
        _state.ReceivedUnknownOrientation = _savedState.ReceivedUnknownOrientation;
        _state.Projection = _savedState.Projection;
    }
}

// -------------------------------------------------------------------------------------
//  internal/prediction/input_predictor.h + stroke_end_predictor.h / .cc
// -------------------------------------------------------------------------------------

internal interface IInkInputPredictor
{
    void Reset();
    void Update(InkVec2 position, double time);
    void ConstructPrediction(InkTipState lastState, List<InkTipState> prediction);
    IInkInputPredictor MakeCopy();
}

/// <summary>对应上游 StrokeEndPredictor：把"最后一个原始输入位置"当锚点，让弹簧模型追上去。</summary>
internal sealed class InkStrokeEndPredictor : IInkInputPredictor
{
    private readonly InkPositionModelerParams _positionModelerParams;
    private readonly InkSamplingParams _samplingParams;
    private InkVec2? _lastPosition;

    public InkStrokeEndPredictor(InkPositionModelerParams positionModelerParams,
                                 InkSamplingParams samplingParams)
    {
        _positionModelerParams = positionModelerParams;
        _samplingParams = samplingParams;
    }

    public void Reset() => _lastPosition = null;

    public void Update(InkVec2 position, double time) => _lastPosition = position;

    public void ConstructPrediction(InkTipState lastState, List<InkTipState> prediction)
    {
        prediction.Clear();
        if (!_lastPosition.HasValue) return;

        var modeler = new InkPositionModeler();
        modeler.Reset(lastState, _positionModelerParams);
        modeler.ModelEndOfStroke(
            _lastPosition.Value,
            1.0 / _samplingParams.MinOutputRate,
            _samplingParams.EndOfStrokeMaxIterations,
            _samplingParams.EndOfStrokeStoppingDistance,
            prediction);
    }

    public IInkInputPredictor MakeCopy() => new InkStrokeEndPredictor(_positionModelerParams, _samplingParams);
}

// -------------------------------------------------------------------------------------
//  stroke_modeler.h / .cc（总装）
// -------------------------------------------------------------------------------------

/// <summary>
/// 对应上游 StrokeModeler：把原始输入流按
/// wobble 平滑 → 弹簧位置建模 →（收笔追赶）→ 触笔状态投影 的顺序建模，
/// 输出一串 <see cref="InkResult"/>；结果只增不改（前缀稳定）。
/// </summary>
internal sealed class InkStrokeModel
{
    private struct InputAndCorrectedPosition
    {
        public InkInput Input;
        public InkVec2 CorrectedPosition;
    }

    private IInkInputPredictor _predictor;
    private InkStrokeModelParams _strokeModelParams;
    private readonly InkWobbleSmoother _wobbleSmoother = new();
    private readonly InkPositionModeler _positionModeler = new();
    private readonly InkStylusStateModeler _stylusStateModeler = new();
    private readonly InkLoopContractionMitigationModeler _loopContractionMitigationModeler = new();
    private readonly List<InkTipState> _tipStateBuffer = new();
    private InputAndCorrectedPosition? _lastInput;
    private IInkInputPredictor _savedPredictor;
    private InputAndCorrectedPosition? _savedLastInput;
    private bool _saveActive;

    /// <summary>对应上游 Reset(StrokeModelParams)。</summary>
    public void Reset(InkStrokeModelParams strokeModelParams)
    {
        string error = strokeModelParams.Validate();
        if (error != null) throw new InkStrokeModelException(error);

        _strokeModelParams = strokeModelParams;
        ResetInternal();

        _predictor = strokeModelParams.PredictionKind switch
        {
            InkPredictionKind.StrokeEnd => new InkStrokeEndPredictor(
                strokeModelParams.PositionModelerParams, strokeModelParams.SamplingParams),
            InkPredictionKind.Disabled => null,
            InkPredictionKind.Kalman => throw new NotSupportedException(
                "KalmanPredictor 尚未移植（见 InkStrokeModeler.cs 文件头差异①）。"),
            _ => null,
        };

        _loopContractionMitigationModeler.Reset(
            strokeModelParams.PositionModelerParams.LoopContractionMitigationParams);
    }

    /// <summary>对应上游 Reset()（保持参数、清空当前笔画）。</summary>
    public void Reset()
    {
        if (_strokeModelParams == null)
            throw new InkStrokeModelException("Initial call to Reset must pass StrokeModelParams.");
        ResetInternal();
    }

    private void ResetInternal()
    {
        _lastInput = null;
        _saveActive = false;
    }

    /// <summary>
    /// 对应上游 Update：喂一条原始输入，把新产出的结果追加到 results。
    /// 之前已经产出的结果不会被修改。
    /// </summary>
    public void Update(InkInput input, List<InkResult> results)
    {
        if (_strokeModelParams == null)
            throw new InkStrokeModelException("Stroke model has not yet been initialized");

        ValidateInput(input);

        if (_lastInput.HasValue)
        {
            if (_lastInput.Value.Input.Equals(input))
                throw new InkStrokeModelException("Received duplicate input");
            if (input.Time < _lastInput.Value.Input.Time)
                throw new InkStrokeModelException("Inputs travel backwards in time");
        }

        switch (input.EventType)
        {
            case InkEventType.Down: ProcessDownEvent(input, results); break;
            case InkEventType.Move: ProcessMoveEvent(input, results); break;
            case InkEventType.Up: ProcessUpEvent(input, results); break;
            default: throw new InkStrokeModelException("Invalid EventType.");
        }
    }

    /// <summary>对应上游 Predict（本引擎渲染路径不用；为完整性移植）。</summary>
    public void Predict(List<InkResult> results)
    {
        results.Clear();
        if (_strokeModelParams == null)
            throw new InkStrokeModelException("Stroke model has not yet been initialized");
        if (_predictor == null)
            throw new InkStrokeModelException("Prediction has been disabled by StrokeModelParams.");
        if (!_lastInput.HasValue)
            throw new InkStrokeModelException("Cannot construct prediction when no stroke is in-progress");

        _predictor.ConstructPrediction(_positionModeler.CurrentState, _tipStateBuffer);

        // 拷贝，因为 ModelStylus 会改这两份状态（上游是 C++ 值拷贝结构体）。
        var predictionStylus = _stylusStateModeler.Clone();
        var predictionLoop = _loopContractionMitigationModeler.Clone();

        ModelStylus(_tipStateBuffer, predictionStylus, predictionLoop, results, _lastInput.Value.Input.Time);
    }

    private static void ValidateInput(InkInput input)
    {
        if (input.EventType is not (InkEventType.Down or InkEventType.Move or InkEventType.Up))
            throw new InkStrokeModelException("Unknown Input.event_type.");
        if (!float.IsFinite(input.Position.X) || !float.IsFinite(input.Position.Y))
            throw new InkStrokeModelException("Input.position must be finite.");
        if (!double.IsFinite(input.Time))
            throw new InkStrokeModelException("Input.time must be finite.");
        // 压力/倾角/方向按上游注释不强制有限（NaN 由 StylusStateModeler 当未知处理）。
    }

    private static InkResult MakeResultFromTipState(InkTipState tipState, InkResult stylusState)
        => new()
        {
            Position = tipState.Position,
            Velocity = tipState.Velocity,
            Acceleration = tipState.Acceleration,
            Time = tipState.Time,
            Pressure = stylusState.Pressure,
            Tilt = stylusState.Tilt,
            Orientation = stylusState.Orientation,
        };

    private static void ModelStylus(List<InkTipState> tipStates,
                                    InkStylusStateModeler stylusStateModeler,
                                    InkLoopContractionMitigationModeler loopModeler,
                                    List<InkResult> result, double prevTime)
    {
        float interpValue = loopModeler.GetInterpolationValue();
        foreach (var tipState in tipStates)
        {
            InkVec2? strokeNormal = InkModelMath.GetStrokeNormal(tipState, prevTime);
            InkResult projectedState = stylusStateModeler.Project(tipState, strokeNormal);
            InkResult modeledState = MakeResultFromTipState(tipState, projectedState);
            result.Add(InkModelMath.InterpResult(projectedState, modeledState, interpValue));
            interpValue = loopModeler.Update(result[^1].Velocity, tipState.Time);
            prevTime = tipState.Time;
        }
    }

    private void ProcessDownEvent(InkInput input, List<InkResult> result)
    {
        if (_lastInput.HasValue)
            throw new InkStrokeModelException("Received down event while stroke is in-progress");

        _wobbleSmoother.Reset(_strokeModelParams.WobbleSmootherParams, input.Position, input.Time);
        _positionModeler.Reset(
            new InkTipState { Position = input.Position, Time = input.Time },
            _strokeModelParams.PositionModelerParams);
        _stylusStateModeler.Reset(_strokeModelParams.StylusStateModelerParams);
        _loopContractionMitigationModeler.Reset(
            _strokeModelParams.PositionModelerParams.LoopContractionMitigationParams);

        _stylusStateModeler.Update(input.Position, input.Time,
            InkStylusState.Make(input.Pressure, input.Tilt, input.Orientation));

        InkTipState tipState = _positionModeler.CurrentState;
        if (_predictor != null)
        {
            _predictor.Reset();
            _predictor.Update(input.Position, input.Time);
        }

        _lastInput = new InputAndCorrectedPosition
        {
            Input = input,
            CorrectedPosition = input.Position,   // down 事件不做位置修正
        };
        result.Add(new InkResult
        {
            Position = tipState.Position,
            Velocity = tipState.Velocity,
            Acceleration = tipState.Acceleration,
            Time = tipState.Time,
            Pressure = input.Pressure,
            Tilt = input.Tilt,
            Orientation = input.Orientation,
        });
    }

    private void ProcessUpEvent(InkInput input, List<InkResult> results)
    {
        if (!_lastInput.HasValue)
            throw new InkStrokeModelException("Received up event while no stroke is in-progress");

        int nSteps = _positionModeler.NumberOfStepsBetweenInputs(
            _lastInput.Value.Input, input, _strokeModelParams.SamplingParams);
        _tipStateBuffer.Clear();
        _positionModeler.UpdateAlongLinearPath(
            _lastInput.Value.CorrectedPosition, _lastInput.Value.Input.Time,
            input.Position, input.Time, nSteps, _tipStateBuffer);

        _positionModeler.ModelEndOfStroke(
            input.Position,
            1.0 / _strokeModelParams.SamplingParams.MinOutputRate,
            _strokeModelParams.SamplingParams.EndOfStrokeMaxIterations,
            _strokeModelParams.SamplingParams.EndOfStrokeStoppingDistance,
            _tipStateBuffer);

        if (_tipStateBuffer.Count == 0)
        {
            // 没有产出新状态（比如 Up 和上一个输入同一时刻）——补当前状态。
            _tipStateBuffer.Add(_positionModeler.CurrentState);
        }

        _stylusStateModeler.Update(input.Position, input.Time,
            InkStylusState.Make(input.Pressure, input.Tilt, input.Orientation));

        ModelStylus(_tipStateBuffer, _stylusStateModeler,
            _loopContractionMitigationModeler, results, _lastInput.Value.Input.Time);
        _lastInput = null;   // 笔画结束
    }

    private void ProcessMoveEvent(InkInput input, List<InkResult> results)
    {
        if (!_lastInput.HasValue)
            throw new InkStrokeModelException("Received move event while no stroke is in-progress");

        InkVec2 correctedPosition = _wobbleSmoother.Update(input.Position, input.Time);
        _stylusStateModeler.Update(correctedPosition, input.Time,
            InkStylusState.Make(input.Pressure, input.Tilt, input.Orientation));

        int nSteps = _positionModeler.NumberOfStepsBetweenInputs(
            _lastInput.Value.Input, input, _strokeModelParams.SamplingParams);
        _tipStateBuffer.Clear();
        _positionModeler.UpdateAlongLinearPath(
            _lastInput.Value.CorrectedPosition, _lastInput.Value.Input.Time,
            correctedPosition, input.Time, nSteps, _tipStateBuffer);

        if (_predictor != null) _predictor.Update(correctedPosition, input.Time);
        _lastInput = new InputAndCorrectedPosition { Input = input, CorrectedPosition = correctedPosition };

        ModelStylus(_tipStateBuffer, _stylusStateModeler,
            _loopContractionMitigationModeler, results, _lastInput.Value.Input.Time);
    }

    public void Save()
    {
        _wobbleSmoother.Save();
        _positionModeler.Save();
        _stylusStateModeler.Save();
        _loopContractionMitigationModeler.Save();
        _savedLastInput = _lastInput;
        _savedPredictor = _predictor?.MakeCopy();
        _saveActive = true;
    }

    public void Restore()
    {
        if (!_saveActive) return;
        _wobbleSmoother.Restore();
        _positionModeler.Restore();
        _stylusStateModeler.Restore();
        _loopContractionMitigationModeler.Restore();
        _lastInput = _savedLastInput;
        if (_savedPredictor != null) _predictor = _savedPredictor.MakeCopy();
    }
}

/*
 * =====================================================================================
 *  本文件移植自 google/ink-stroke-modeler（Apache License 2.0）：
 *
 *  Copyright 2022-2024 Google LLC
 *
 *  Licensed under the Apache License, Version 2.0 (the "License");
 *  you may not use this file except in compliance with the License.
 *  You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 *  Unless required by applicable law or agreed to in writing, software
 *  distributed under the License is distributed on an "AS IS" BASIS,
 *  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *  See the License for the specific language governing permissions and
 *  limitations under the License.
 * =====================================================================================
 */
