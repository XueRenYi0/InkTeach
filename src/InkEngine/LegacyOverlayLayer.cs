using System;

namespace InkEngine;

/// <summary>
/// 迁移期适配器：把现有 PPT/回放/计时/点名/收藏的“可见/命中/按下/移动/抬起/关闭”
/// 包成 IOverlayLayer。绘制仍留在 Overlay，不动一行。
/// </summary>
public sealed class LegacyOverlayLayer : IOverlayLayer
{
    private readonly Func<bool> _visible;
    private readonly Func<RectF> _bounds;
    private readonly Func<float, float, bool> _hit;
    private readonly Func<float, float, uint, uint, bool> _down;
    private readonly Func<float, float, uint, bool> _move;
    private readonly Func<float, float, uint, bool> _up;
    private readonly Func<float, float, int, bool> _wheel;
    private readonly Action _dismiss;

    public LegacyOverlayLayer(
        string id, int priority,
        Func<bool> visible, Func<RectF> bounds,
        Func<float, float, bool> hit = null,
        Func<float, float, uint, uint, bool> down = null,
        Func<float, float, uint, bool> move = null,
        Func<float, float, uint, bool> up = null,
        Func<float, float, int, bool> wheel = null,
        Action dismiss = null,
        bool wantsCapture = true,
        bool blocksInputBelow = true,
        OverlayDismissPolicy dismissPolicy = OverlayDismissPolicy.None)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Priority = priority;
        _visible = visible;
        _bounds = bounds;
        _hit = hit;
        _down = down;
        _move = move;
        _up = up;
        _wheel = wheel;
        _dismiss = dismiss;
        WantsCapture = wantsCapture;
        BlocksInputBelow = blocksInputBelow;
        DismissPolicy = dismissPolicy;
    }

    public string Id { get; }
    public int Priority { get; }
    public bool Visible => _visible?.Invoke() ?? false;
    public bool ConsumesInput { get; init; } = true;
    public bool BlocksInputBelow { get; }
    public bool WantsCapture { get; }
    public RectF Bounds => _bounds?.Invoke() ?? RectF.Empty;
    public OverlayDismissPolicy DismissPolicy { get; }
    public bool IsAnimating => false;

    public bool HitTest(float x, float y)
        => Visible && (_hit != null ? _hit(x, y) : Bounds.Contains(x, y));

    public bool PointerDown(float x, float y, uint pointerId, uint pointerType)
        => _down?.Invoke(x, y, pointerId, pointerType) ?? false;

    public bool PointerMove(float x, float y, uint pointerId)
        => _move?.Invoke(x, y, pointerId) ?? false;

    public bool PointerUp(float x, float y, uint pointerId)
        => _up?.Invoke(x, y, pointerId) ?? false;

    public bool Wheel(float x, float y, int delta)
        => _wheel?.Invoke(x, y, delta) ?? false;

    public void Dismiss() => _dismiss?.Invoke();
}
