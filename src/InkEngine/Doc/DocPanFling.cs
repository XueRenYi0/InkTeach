// [职责] PDF 手模式下拖动松手后的滑动惯性（12 槽采样 + 指数衰减，参数对齐 UiTouchFling）。
// [测试] --unittest DocPanFling

using System;

namespace InkEngine;

/// <summary>
/// PDF「手掌」拖页松手后的滑动惯性。
///
/// 采样最近 100ms 内的逻辑像素位移，松手时按速度决定是否起惯性；
/// 衰减曲线与停转阈值直接复用 <see cref="UiTouchFling"/>，保持与双指纵滑一致的手感。
/// </summary>
internal sealed class DocPanFling
{
    private const int Capacity = 12;
    private const double WindowMs = 100.0;
    private const double StaleMs = 120.0;
    private const float MinLaunchSpeed = 0.15f;

    private readonly struct SampleItem
    {
        public readonly float DxLogical;
        public readonly float DyLogical;
        public readonly double TimeMs;

        public SampleItem(float dxLogical, float dyLogical, double timeMs)
        {
            DxLogical = dxLogical;
            DyLogical = dyLogical;
            TimeMs = timeMs;
        }
    }

    private readonly SampleItem[] _samples = new SampleItem[Capacity];
    private int _head;
    private int _count;
    private double _lastStepMs;

    public bool Active { get; private set; }
    public float Vx { get; private set; }
    public float Vy { get; private set; }

    public void Begin(double nowMs)
    {
        _head = 0;
        _count = 0;
        _lastStepMs = nowMs;
        Stop();
    }

    public void Sample(float dxLogical, float dyLogical, double nowMs)
    {
        _samples[_head] = new SampleItem(dxLogical, dyLogical, nowMs);
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    public bool Release(double nowMs, bool allowX)
    {
        if (_count == 0)
        {
            Stop();
            return false;
        }

        int lastIdx = (_head - 1 + Capacity) % Capacity;
        if (nowMs - _samples[lastIdx].TimeMs > StaleMs)
        {
            Stop();
            return false;
        }

        float sumX = 0f;
        float sumY = 0f;
        double tMin = double.MaxValue;
        double tMax = double.MinValue;
        int start = (_head - _count + Capacity) % Capacity;
        for (int i = 0; i < _count; i++)
        {
            var s = _samples[(start + i) % Capacity];
            bool isLast = i == _count - 1;
            if (nowMs - s.TimeMs <= WindowMs || isLast)
            {
                sumX += s.DxLogical;
                sumY += s.DyLogical;
                if (s.TimeMs < tMin) tMin = s.TimeMs;
                if (s.TimeMs > tMax) tMax = s.TimeMs;
            }
        }

        double rawSpan = tMax - tMin;
        double span = rawSpan < 1.0
            ? Math.Clamp(nowMs - tMax, 16.0, 40.0)
            : Math.Max(16.0, rawSpan);

        float vx = allowX ? sumX / (float)span : 0f;
        float vy = sumY / (float)span;
        float speed = MathF.Sqrt(vx * vx + vy * vy);
        if (speed < MinLaunchSpeed)
        {
            Stop();
            return false;
        }

        if (speed > UiTouchFling.MaxVelocity)
        {
            float scale = UiTouchFling.MaxVelocity / speed;
            vx *= scale;
            vy *= scale;
        }

        Vx = vx;
        Vy = vy;
        Active = true;
        _lastStepMs = nowMs;
        return true;
    }

    public bool Step(double nowMs, out float dxLogical, out float dyLogical)
    {
        if (!Active)
        {
            dxLogical = 0f;
            dyLogical = 0f;
            return false;
        }

        double dt = Math.Clamp(nowMs - _lastStepMs, 0.0, UiTouchFling.MaxStepMs);
        _lastStepMs = nowMs;
        if (dt <= 0.0)
        {
            dxLogical = 0f;
            dyLogical = 0f;
            return true;
        }

        float decay = MathF.Exp(-UiTouchFling.DecayPerSecond * (float)(dt / 1000.0));
        Vx *= decay;
        Vy *= decay;
        if (MathF.Sqrt(Vx * Vx + Vy * Vy) < UiTouchFling.StopVelocity)
        {
            Stop();
            dxLogical = 0f;
            dyLogical = 0f;
            return false;
        }

        dxLogical = Vx * (float)dt;
        dyLogical = Vy * (float)dt;
        return true;
    }

    public void ClampStoppedAxes(bool stoppedX, bool stoppedY)
    {
        if (stoppedX) Vx = 0f;
        if (stoppedY) Vy = 0f;
        if (MathF.Sqrt(Vx * Vx + Vy * Vy) < UiTouchFling.StopVelocity) Stop();
    }

    public void Stop()
    {
        Active = false;
        Vx = 0f;
        Vy = 0f;
    }
}
