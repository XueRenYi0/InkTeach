// =====================================================================================
//  google/ink 的 SlidingWindowInputModeler 1:1 移植（C#）
//
//  来源：https://github.com/google/ink （main @ 8bd5c556...，2026-10-01）
//  文件：ink/strokes/internal/stroke_input_modeler/{sliding_window_input_modeler.h/.cc,
//        passthrough_input_modeler.h/.cc, input_model_impl.h}、
//        ink/strokes/internal/{modeled_stroke_input.h, stroke_input_modeler.h/.cc}、
//        ink/strokes/input/stroke_input.h
//  许可证：Apache License 2.0（Copyright 2024-2025 Google LLC，见文件尾声明）
//
//  移植纪律（与 ink-stroke-modeler 那一份相同）：算法、顺序、默认值一字不改；
//  只做外壳适配（C++ → C#、Duration32 → double 秒、absl → 异常、std::vector → List）。
//  已知差异：
//    ① 只搬输入建模这一片（不含笔刷/网格/渲染/序列化）；
//    ② ToolType/StrokeUnitLength 只作为结构字段保留，本引擎固定 Stylus/无值；
//    ③ predicted_inputs 支持但本引擎暂不喂（预测归现有 InkPredictor / DWM 轨迹）。
// =====================================================================================

namespace InkEngine;

// ==== 基础类型（stroke_input.h / modeled_stroke_input.h） ======================

internal enum GiToolType : byte { Unknown, Mouse, Touch, Stylus }

/// <summary>对应 google/ink 的 StrokeInput。</summary>
internal struct GiStrokeInput
{
    public GiToolType ToolType;
    public InkVec2 Position;
    public double ElapsedTime;          // 秒
    public float Pressure;              // -1 = 未知
    public float Tilt;                  // 弧度；-1 = 未知
    public float Orientation;           // 弧度；-1 = 未知
    public float BarrelTwist;           // 弧度；-1 = 未知

    public bool HasPressure() => Pressure != -1f;
    public bool HasTilt() => Tilt != -1f;
    public bool HasOrientation() => Orientation != -1f;
    public bool HasBarrelTwist() => BarrelTwist != -1f;

    public static GiStrokeInput Make(InkVec2 position, double elapsedTime,
                                     float pressure = -1f, float tilt = -1f,
                                     float orientation = -1f, float barrelTwist = -1f)
        => new()
        {
            ToolType = GiToolType.Stylus,
            Position = position,
            ElapsedTime = elapsedTime,
            Pressure = pressure,
            Tilt = tilt,
            Orientation = orientation,
            BarrelTwist = barrelTwist,
        };
}

/// <summary>对应 google/ink 的 StrokeInputBatch（只实现建模用得到的部分）。</summary>
internal sealed class GiStrokeInputBatch
{
    private readonly List<GiStrokeInput> _items = new();
    public int Size => _items.Count;
    public bool IsEmpty => _items.Count == 0;
    public GiStrokeInput Get(int i) => _items[i];
    public GiStrokeInput First => _items[0];
    public GiStrokeInput Last => _items[^1];
    public void Append(GiStrokeInput input) => _items.Add(input);
    public void Append(GiStrokeInputBatch other)
    {
        for (int i = 0; i < other._items.Count; i++) _items.Add(other._items[i]);
    }
    public void Erase(int count) => _items.RemoveRange(_items.Count - count, count);
    public void Erase(int offset, int count) => _items.RemoveRange(offset, count);

    public GiToolType GetToolType() => IsEmpty ? GiToolType.Unknown : _items[0].ToolType;

    public bool HasPressure()
    {
        foreach (var i in _items) if (i.HasPressure()) return true;
        return false;
    }
    public bool HasTilt()
    {
        foreach (var i in _items) if (i.HasTilt()) return true;
        return false;
    }
    public bool HasOrientation()
    {
        foreach (var i in _items) if (i.HasOrientation()) return true;
        return false;
    }
    public bool HasBarrelTwist()
    {
        foreach (var i in _items) if (i.HasBarrelTwist()) return true;
        return false;
    }
}

/// <summary>对应 google/ink 的 ModeledStrokeInput。</summary>
internal struct GiModeledStrokeInput
{
    public InkVec2 Position;
    public InkVec2 Velocity;
    public InkVec2 Acceleration;
    public float TraveledDistance;
    public double ElapsedTime;
    public float Pressure;
    public float Tilt;
    public float Orientation;
    public float BarrelTwist;
}

/// <summary>对应 google/ink 的 InputMetrics。</summary>
internal struct GiInputMetrics
{
    public float TraveledDistance;
    public double ElapsedTime;
}

/// <summary>对应 google/ink 的 InputModelerState。</summary>
internal sealed class GiInputModelerState
{
    public GiToolType ToolType = GiToolType.Unknown;
    public GiInputMetrics RealInputMetrics;
    public GiInputMetrics FullInputMetrics;
    public double CompleteElapsedTime;
    public int StableInputCount;
    public int RealInputCount;
    public bool InputsAreFinished;
}

// ==== 数学工具（geometry/internal/lerp.h、angle、distance、vec 的用到的部分） ====

internal static class GiMath
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static InkVec2 Lerp(InkVec2 a, InkVec2 b, float t) => a + (b - a) * t;
    public static double Lerp(double a, double b, float t) => a + (b - a) * t;

    public static float InverseLerp(float a, float b, float value)
        => b - a == 0f ? 0f : (value - a) / (b - a);

    public static float Distance(InkVec2 a, InkVec2 b) => (b - a).Magnitude();

    /// <summary>归一化角度到 [0, 2π)。</summary>
    public static float NormalizeAngle(float angle)
    {
        while (angle < 0) angle += 2f * MathF.PI;
        while (angle >= 2f * MathF.PI) angle -= 2f * MathF.PI;
        return angle;
    }

    /// <summary>对应 NormalizedAngleLerp：走短弧插值并归一化。</summary>
    public static float NormalizedAngleLerp(float start, float end, float t)
    {
        start = NormalizeAngle(start);
        end = NormalizeAngle(end);
        float delta = end - start;
        if (delta < -MathF.PI) end += 2f * MathF.PI;
        else if (delta > MathF.PI) end -= 2f * MathF.PI;
        return NormalizeAngle(Lerp(start, end, t));
    }

    /// <summary>方向角 → 单位向量（圆形均值用）。</summary>
    public static InkVec2 UnitVecWithDirection(float radians)
        => new(MathF.Cos(radians), MathF.Sin(radians));

    /// <summary>向量 → 方向角（圆形均值用）。</summary>
    public static float Direction(InkVec2 v) => MathF.Atan2(v.Y, v.X);
}

// ==== InputModel 接口（input_model_impl.h） ====================================

internal interface IGiInputModel
{
    void ExtendStroke(GiInputModelerState state, List<GiModeledStrokeInput> modeledInputs,
                      GiStrokeInputBatch realInputs, GiStrokeInputBatch predictedInputs);
}

// ==== PassthroughInputModeler（1:1） ============================================

internal sealed class GiPassthroughInputModel : IGiInputModel
{
    public void ExtendStroke(GiInputModelerState state, List<GiModeledStrokeInput> modeledInputs,
                             GiStrokeInputBatch realInputs, GiStrokeInputBatch predictedInputs)
    {
        AppendInputs(modeledInputs, realInputs);
        state.RealInputCount = modeledInputs.Count;
        state.StableInputCount = state.RealInputCount;
        AppendInputs(modeledInputs, predictedInputs);
    }

    private static void AppendInputs(List<GiModeledStrokeInput> modeledInputs, GiStrokeInputBatch inputs)
    {
        for (int i = 0; i < inputs.Size; ++i)
        {
            GiStrokeInput input = inputs.Get(i);
            float traveledDistance = 0;
            InkVec2 velocity = default;
            InkVec2 acceleration = default;
            if (modeledInputs.Count > 0)
            {
                var last = modeledInputs[^1];
                traveledDistance = last.TraveledDistance + GiMath.Distance(last.Position, input.Position);
                float deltaSeconds = (float)(input.ElapsedTime - last.ElapsedTime);
                if (deltaSeconds > 0)
                {
                    velocity = (input.Position - last.Position) / deltaSeconds;
                    acceleration = (velocity - last.Velocity) / deltaSeconds;
                }
            }
            modeledInputs.Add(new GiModeledStrokeInput
            {
                Position = input.Position,
                Velocity = velocity,
                Acceleration = acceleration,
                TraveledDistance = traveledDistance,
                ElapsedTime = input.ElapsedTime,
                Pressure = input.Pressure,
                Tilt = input.Tilt,
                Orientation = input.Orientation,
                BarrelTwist = input.BarrelTwist,
            });
        }
    }
}

// ==== SlidingWindowInputModeler（1:1） ==========================================

internal sealed class GiSlidingWindowInputModel : IGiInputModel
{
    private const int MaxUpsampleDivisions = 100;

    private sealed class Integrals
    {
        public InkVec2 PositionDt;
        public float PressureDt;
        public float TiltDt;
        public InkVec2 OrientationDt;
        public InkVec2 BarrelTwistDt;   // 与上游一致：这里名字虽叫 twist，存的也是单位向量
    }

    private readonly GiStrokeInputBatch _rawInputQueue = new();
    private readonly double _halfWindowSize;
    private readonly double _upsamplingPeriod;
    private readonly float _positionEpsilon;

    /// <summary>临时诊断：把每次窗口积分的范围/结果打出来（查"同批同刻"跳点用）。</summary>
    public static bool DebugDump;
    private static int s_debugCount;

    public static void ResetDebug() { s_debugCount = 0; }

    public GiSlidingWindowInputModel(double windowSize, double upsamplingPeriod, float positionEpsilon)
    {
        _halfWindowSize = windowSize * 0.5;
        _upsamplingPeriod = upsamplingPeriod;
        _positionEpsilon = positionEpsilon;
    }

    public void ExtendStroke(GiInputModelerState state, List<GiModeledStrokeInput> modeledInputs,
                             GiStrokeInputBatch realInputs, GiStrokeInputBatch predictedInputs)
    {
        if (realInputs.IsEmpty && predictedInputs.IsEmpty) return;
        EraseUnstableModeledInputs(state, modeledInputs);
        _rawInputQueue.Append(realInputs);
        int rawInputQueueRealInputCount = _rawInputQueue.Size;
        _rawInputQueue.Append(predictedInputs);
        ModelUnstableInputs(state, modeledInputs, rawInputQueueRealInputCount);
        MarkStableModeledInputs(state, modeledInputs);
        TrimRawInputQueue(state, modeledInputs, rawInputQueueRealInputCount);
    }

    private static void EraseUnstableModeledInputs(GiInputModelerState state,
                                                   List<GiModeledStrokeInput> modeledInputs)
    {
        if (modeledInputs.Count > state.StableInputCount)
            modeledInputs.RemoveRange(state.StableInputCount, modeledInputs.Count - state.StableInputCount);
        state.RealInputCount = state.StableInputCount;
    }

    private void ModelUnstableInputs(GiInputModelerState state,
                                     List<GiModeledStrokeInput> modeledInputs,
                                     int rawInputQueueRealInputCount)
    {
        double realInputCutoff = rawInputQueueRealInputCount == 0
            ? double.NegativeInfinity
            : _rawInputQueue.Get(rawInputQueueRealInputCount - 1).ElapsedTime;

        ModelUnstableInputPositions(state, modeledInputs, realInputCutoff);
        ComputeDerivativeForUnstableInputs(DerivativeField.Position, modeledInputs, state.StableInputCount);
        // 上游先算位置的速度、再用速度算加速度 → 两趟，顺序不能换。
        ComputeDerivativeForUnstableInputs(DerivativeField.Velocity, modeledInputs, state.StableInputCount);
    }

    private enum DerivativeField { Position, Velocity }

    private void ComputeDerivativeForUnstableInputs(DerivativeField field,
                                                    List<GiModeledStrokeInput> modeledInputs,
                                                    int stableInputCount)
    {
        int numModeled = modeledInputs.Count;
        int startIndex = 0;
        int endIndex = stableInputCount;
        for (int index = stableInputCount; index < numModeled; ++index)
        {
            var input = modeledInputs[index];
            double startTime = Math.Max(input.ElapsedTime - _halfWindowSize, modeledInputs[0].ElapsedTime);
            double endTime = Math.Min(input.ElapsedTime + _halfWindowSize, modeledInputs[^1].ElapsedTime);
            float dt = (float)(endTime - startTime);
            if (dt == 0)
            {
                if (field == DerivativeField.Position) input.Velocity = default;
                else input.Acceleration = default;
                modeledInputs[index] = input;
                continue;
            }

            while (startIndex + 1 < numModeled && modeledInputs[startIndex + 1].ElapsedTime <= startTime)
                ++startIndex;
            while (endIndex + 1 < numModeled && modeledInputs[endIndex].ElapsedTime <= endTime)
                ++endIndex;

            InkVec2 startValue;
            InkVec2 endValue;
            if (field == DerivativeField.Position)
            {
                startValue = startIndex + 1 < numModeled
                    ? GiMath.Lerp(modeledInputs[startIndex].Position, modeledInputs[startIndex + 1].Position,
                                  InterpRatio(modeledInputs[startIndex].ElapsedTime,
                                              modeledInputs[startIndex + 1].ElapsedTime, startTime))
                    : modeledInputs[startIndex].Position;
                endValue = endIndex > 0
                    ? GiMath.Lerp(modeledInputs[endIndex - 1].Position, modeledInputs[endIndex].Position,
                                  InterpRatio(modeledInputs[endIndex - 1].ElapsedTime,
                                              modeledInputs[endIndex].ElapsedTime, endTime))
                    : modeledInputs[endIndex].Position;
                input.Velocity = (endValue - startValue) / dt;
            }
            else
            {
                startValue = startIndex + 1 < numModeled
                    ? GiMath.Lerp(modeledInputs[startIndex].Velocity, modeledInputs[startIndex + 1].Velocity,
                                  InterpRatio(modeledInputs[startIndex].ElapsedTime,
                                              modeledInputs[startIndex + 1].ElapsedTime, startTime))
                    : modeledInputs[startIndex].Velocity;
                endValue = endIndex > 0
                    ? GiMath.Lerp(modeledInputs[endIndex - 1].Velocity, modeledInputs[endIndex].Velocity,
                                  InterpRatio(modeledInputs[endIndex - 1].ElapsedTime,
                                              modeledInputs[endIndex].ElapsedTime, endTime))
                    : modeledInputs[endIndex].Velocity;
                input.Acceleration = (endValue - startValue) / dt;
            }
            modeledInputs[index] = input;
        }
    }

    private static float InterpRatio(double a, double b, double value)
        => b - a == 0 ? 0f : (float)((value - a) / (b - a));

    private void ModelUnstableInputPositions(GiInputModelerState state,
                                             List<GiModeledStrokeInput> modeledInputs,
                                             double realInputCutoff)
    {
        int rawInputQueueSize = _rawInputQueue.Size;
        int startIndex = 0;
        int endIndex = 0;
        double prevModeledInputTime = modeledInputs.Count > 0
            ? modeledInputs[^1].ElapsedTime
            : double.NegativeInfinity;

        for (int i = 0; i < rawInputQueueSize; ++i)
        {
            double rawInputTime = _rawInputQueue.Get(i).ElapsedTime;
            if (rawInputTime <= prevModeledInputTime) continue;

            if (double.IsFinite(prevModeledInputTime))
            {
                double dt = rawInputTime - prevModeledInputTime;
                int numDivisions = (int)Math.Min(Math.Ceiling(dt / _upsamplingPeriod),
                                                 (double)MaxUpsampleDivisions);
                if (numDivisions > 1)
                {
                    double period = dt / numDivisions;
                    for (int k = 1; k < numDivisions; ++k)
                    {
                        double elapsedTime = prevModeledInputTime + period * k;
                        ModelUnstableInputPosition(modeledInputs, elapsedTime, ref startIndex, ref endIndex);
                    }
                }
            }

            double rawTime = rawInputTime;
            ModelUnstableInputPosition(modeledInputs, rawTime, ref startIndex, ref endIndex);
            if (rawTime <= realInputCutoff)
                state.RealInputCount = modeledInputs.Count;
            prevModeledInputTime = rawTime;
        }
    }

    private void ModelUnstableInputPosition(List<GiModeledStrokeInput> modeledInputs,
                                            double elapsedTime, ref int startIndex, ref int endIndex)
    {
        int rawInputQueueSize = _rawInputQueue.Size;
        double firstRawElapsedTime = _rawInputQueue.First.ElapsedTime;
        double lastRawElapsedTime = _rawInputQueue.Last.ElapsedTime;

        double halfWindowSize = Math.Min(_halfWindowSize,
            Math.Min(elapsedTime - firstRawElapsedTime, lastRawElapsedTime - elapsedTime));
        double windowStartTime = Math.Max(elapsedTime - halfWindowSize, firstRawElapsedTime);
        double windowEndTime = Math.Min(elapsedTime + halfWindowSize, lastRawElapsedTime);

        while (startIndex + 1 < rawInputQueueSize &&
               _rawInputQueue.Get(startIndex + 1).ElapsedTime <= windowStartTime)
            ++startIndex;
        while (endIndex + 1 < rawInputQueueSize &&
               _rawInputQueue.Get(endIndex).ElapsedTime <= windowEndTime)
            ++endIndex;

        float dt = (float)(windowEndTime - windowStartTime);
        if (dt <= 0)
        {
            GiStrokeInput input = _rawInputQueue.Get(startIndex);
            if (IsWithinEpsilonOfLastInput(modeledInputs, input.Position)) return;
            modeledInputs.Add(new GiModeledStrokeInput
            {
                Position = input.Position,
                TraveledDistance = DistanceTraveled(modeledInputs, input.Position),
                ElapsedTime = elapsedTime,
                Pressure = input.Pressure,
                Tilt = input.Tilt,
                Orientation = input.Orientation,
                BarrelTwist = input.BarrelTwist,
            });
            return;
        }

        var integrals = new Integrals();
        for (int i = startIndex; i < endIndex; ++i)
        {
            GiStrokeInput input1 = _rawInputQueue.Get(i);
            GiStrokeInput input2 = _rawInputQueue.Get(i + 1);
            if (input1.ElapsedTime < windowStartTime)
                input1 = InterpolateStrokeInput(input1, input2, windowStartTime);
            if (input2.ElapsedTime > windowEndTime)
                input2 = InterpolateStrokeInput(input1, input2, windowEndTime);
            Integrate(integrals, input1, input2);
        }

        InkVec2 position = integrals.PositionDt / dt;
        if (DebugDump && s_debugCount++ < 40)
        {
            Console.WriteLine($"[slide] t={elapsedTime * 1000:F1}ms "
                              + $"win=[{windowStartTime * 1000:F1},{windowEndTime * 1000:F1}] "
                              + $"start={startIndex} end={endIndex} "
                              + $"pos=({position.X:F1},{position.Y:F1})");
        }
        if (IsWithinEpsilonOfLastInput(modeledInputs, position)) return;

        var modeled = new GiModeledStrokeInput
        {
            Position = position,
            TraveledDistance = DistanceTraveled(modeledInputs, position),
            ElapsedTime = elapsedTime,
            Pressure = -1f,
            Tilt = -1f,
            Orientation = -1f,
            BarrelTwist = -1f,
        };
        if (_rawInputQueue.HasPressure()) modeled.Pressure = integrals.PressureDt / dt;
        if (_rawInputQueue.HasTilt()) modeled.Tilt = integrals.TiltDt / dt;
        if (_rawInputQueue.HasOrientation())
            modeled.Orientation = GiMath.NormalizeAngle(GiMath.Direction(integrals.OrientationDt / dt));
        if (_rawInputQueue.HasBarrelTwist())
            modeled.BarrelTwist = GiMath.NormalizeAngle(GiMath.Direction(integrals.BarrelTwistDt / dt));
        modeledInputs.Add(modeled);
    }

    private static void Integrate(Integrals integrals, GiStrokeInput input1, GiStrokeInput input2)
    {
        float dt = (float)(input2.ElapsedTime - input1.ElapsedTime);
        integrals.PositionDt += 0.5f * dt * (input1.Position + input2.Position);
        if (input1.HasPressure())
            integrals.PressureDt += 0.5f * dt * (input1.Pressure + input2.Pressure);
        if (input1.HasTilt())
            integrals.TiltDt += 0.5f * dt * (input1.Tilt + input2.Tilt);
        if (input1.HasOrientation())
            integrals.OrientationDt += 0.5f * dt *
                (GiMath.UnitVecWithDirection(input1.Orientation) +
                 GiMath.UnitVecWithDirection(input2.Orientation));
        if (input1.HasBarrelTwist())
            integrals.BarrelTwistDt += 0.5f * dt *
                (GiMath.UnitVecWithDirection(input1.BarrelTwist) +
                 GiMath.UnitVecWithDirection(input2.BarrelTwist));
    }

    private static GiStrokeInput InterpolateStrokeInput(GiStrokeInput input1, GiStrokeInput input2,
                                                        double elapsedTime)
    {
        float ratio = InterpRatio(input1.ElapsedTime, input2.ElapsedTime, elapsedTime);
        var result = new GiStrokeInput
        {
            ToolType = input1.ToolType,
            Position = GiMath.Lerp(input1.Position, input2.Position, ratio),
            ElapsedTime = elapsedTime,
        };
        if (input1.HasPressure() && input2.HasPressure())
            result.Pressure = GiMath.Lerp(input1.Pressure, input2.Pressure, ratio);
        if (input1.HasTilt() && input2.HasTilt())
            result.Tilt = GiMath.Lerp(input1.Tilt, input2.Tilt, ratio);
        if (input1.HasOrientation() && input2.HasOrientation())
            result.Orientation = GiMath.NormalizedAngleLerp(input1.Orientation, input2.Orientation, ratio);
        if (input1.HasBarrelTwist() && input2.HasBarrelTwist())
            result.BarrelTwist = GiMath.NormalizedAngleLerp(input1.BarrelTwist, input2.BarrelTwist, ratio);
        return result;
    }

    private static float DistanceTraveled(List<GiModeledStrokeInput> modeledInputs, InkVec2 position)
    {
        if (modeledInputs.Count == 0) return 0;
        var last = modeledInputs[^1];
        return last.TraveledDistance + GiMath.Distance(last.Position, position);
    }

    private bool IsWithinEpsilonOfLastInput(List<GiModeledStrokeInput> modeledInputs, InkVec2 position)
    {
        if (modeledInputs.Count == 0) return false;
        return GiMath.Distance(modeledInputs[^1].Position, position) <= _positionEpsilon;
    }

    private void MarkStableModeledInputs(GiInputModelerState state,
                                         List<GiModeledStrokeInput> modeledInputs)
    {
        if (state.RealInputCount == 0) return;
        var lastRealInput = modeledInputs[state.RealInputCount - 1];
        while (state.StableInputCount < state.RealInputCount &&
               modeledInputs[state.StableInputCount].ElapsedTime + _halfWindowSize < lastRealInput.ElapsedTime)
        {
            ++state.StableInputCount;
        }
    }

    private void TrimRawInputQueue(GiInputModelerState state,
                                   List<GiModeledStrokeInput> modeledInputs,
                                   int rawInputQueueRealInputCount)
    {
        if (rawInputQueueRealInputCount < _rawInputQueue.Size)
            _rawInputQueue.Erase(_rawInputQueue.Size - rawInputQueueRealInputCount);

        if (state.StableInputCount == 0) return;
        var lastStableInput = modeledInputs[state.StableInputCount - 1];
        double cutoff = lastStableInput.ElapsedTime - _halfWindowSize;
        int nextInputIndex = 1;
        while (nextInputIndex < rawInputQueueRealInputCount &&
               _rawInputQueue.Get(nextInputIndex).ElapsedTime <= cutoff)
            ++nextInputIndex;
        int numSlidingInputsToTrim = nextInputIndex - 1;
        if (numSlidingInputsToTrim > 0) _rawInputQueue.Erase(0, numSlidingInputsToTrim);
    }
}

// ==== StrokeInputModeler（1:1，编排层） =========================================

internal enum GiInputModelKind { Passthrough, SlidingWindow }

internal sealed class GiStrokeInputModel
{
    private readonly GiInputModelerState _state = new();
    private readonly List<GiModeledStrokeInput> _modeledInputs = new();
    private IGiInputModel _impl;

    public GiInputModelerState State => _state;
    public IReadOnlyList<GiModeledStrokeInput> ModeledInputs => _modeledInputs;

    public void StartStroke(GiInputModelKind kind, float brushEpsilon)
    {
        _state.ToolType = GiToolType.Unknown;
        _state.RealInputMetrics = default;
        _state.FullInputMetrics = default;
        _state.CompleteElapsedTime = 0;
        _state.StableInputCount = 0;
        _state.RealInputCount = 0;
        _state.InputsAreFinished = false;
        _modeledInputs.Clear();
        _impl = kind switch
        {
            GiInputModelKind.Passthrough => new GiPassthroughInputModel(),
            GiInputModelKind.SlidingWindow => new GiSlidingWindowInputModel(
                GiSlidingWindowConfig.WindowSize, GiSlidingWindowConfig.UpsamplingPeriod, brushEpsilon),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public void ExtendStroke(GiStrokeInputBatch realInputs, GiStrokeInputBatch predictedInputs,
                             double currentElapsedTime)
    {
        if (_impl == null) throw new InvalidOperationException("StartStroke() 还没调用。");
        if (_state.InputsAreFinished && (!realInputs.IsEmpty || !predictedInputs.IsEmpty))
            throw new InvalidOperationException("FinishStrokeInputs() 之后不能再加输入。");

        ErasePredictedModeledInputs();
        SetToolTypeAndStrokeUnitLength(realInputs, predictedInputs);
        _impl.ExtendStroke(_state, _modeledInputs, realInputs, predictedInputs);
        SetMetricsFromInputCount(_state.RealInputCount, ref _state.RealInputMetrics);
        SetMetricsFromInputCount(_modeledInputs.Count, ref _state.FullInputMetrics);
        _state.CompleteElapsedTime = Math.Max(_state.FullInputMetrics.ElapsedTime, currentElapsedTime);
    }

    public void FinishStrokeInputs() => _state.InputsAreFinished = true;

    private void ErasePredictedModeledInputs()
    {
        if (_modeledInputs.Count > _state.RealInputCount)
            _modeledInputs.RemoveRange(_state.RealInputCount, _modeledInputs.Count - _state.RealInputCount);
        _state.FullInputMetrics = _state.RealInputMetrics;
        _state.CompleteElapsedTime = _state.RealInputMetrics.ElapsedTime;
    }

    private void SetToolTypeAndStrokeUnitLength(GiStrokeInputBatch realInputs,
                                                GiStrokeInputBatch predictedInputs)
    {
        if (!realInputs.IsEmpty) _state.ToolType = realInputs.GetToolType();
        else if (!predictedInputs.IsEmpty) _state.ToolType = predictedInputs.GetToolType();
    }

    private void SetMetricsFromInputCount(int modeledInputCount, ref GiInputMetrics metrics)
    {
        if (modeledInputCount == 0)
        {
            metrics.ElapsedTime = 0;
            metrics.TraveledDistance = 0;
        }
        else
        {
            var input = _modeledInputs[modeledInputCount - 1];
            metrics.ElapsedTime = input.ElapsedTime;
            metrics.TraveledDistance = input.TraveledDistance;
        }
    }
}

/// <summary>滑动窗参数（对应 google/ink BrushFamily::SlidingWindowModel 的默认值）。</summary>
internal static class GiSlidingWindowConfig
{
    public static double WindowSize = 0.020;                 // 20ms
    public static double UpsamplingPeriod = 1.0 / 180.0;     // 180Hz
}

/*
 * =====================================================================================
 *  本文件移植自 google/ink（Apache License 2.0）：
 *
 *  Copyright 2024-2025 Google LLC
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
