using InkEngine;

namespace InkUi;

/// <summary>
/// 一个数值的动画（0→1 的展开、位置吸附都用它）。
///
/// 时钟用 <see cref="IUiHost.NowMs"/>，**不自己起定时器**：引擎只在"有脏区或有东西在动"
/// 时才渲染一帧，界面只要在动画期间把 <see cref="IOverlayUi.IsAnimating"/> 报成 true，
/// 帧就会一直来（见 计划-底层对接界面.md 4.2）。自己起定时器的做法会让动画与笔迹不同拍。
///
/// 缓动用 ease-out（快出缓停）：展开这种"用户已经决定了"的动作，
/// 起手要跟得上手指，尾巴慢下来才显得稳。
/// </summary>
internal sealed class Anim
{
    private IUiHost _host;
    private double _startMs;
    private double _durationMs;
    private float _from, _to;

    public Anim(float initial)
    {
        _from = _to = initial;
        _startMs = double.NegativeInfinity;
        _durationMs = 0;
    }

    /// <summary>
    /// 接上引擎的时钟。**必须在 Attach 之后调**：界面是引擎建的，
    /// 构造时还拿不到 <see cref="IUiHost"/>；而时钟要是错的，
    /// 动画会永远"正在跑"（`_startMs` 停在 0、`NowMs` 又一直是 0）——
    /// 表现就是动画不动、而且空闲时帧一刻不停地出。
    /// </summary>
    public void Bind(IUiHost host)
    {
        _host = host;
        _startMs = double.NegativeInfinity;
        _durationMs = 0;
    }

    /// <summary>当前值（已经在跑就按进度算，没在跑就是目标值）。</summary>
    public float Value
    {
        get
        {
            if (_host == null || !Running) return _to;
            double t = (_host.NowMs - _startMs) / _durationMs;
            if (t <= 0) return _from;
            if (t >= 1) return _to;
            return _from + (_to - _from) * EaseOut((float)t);
        }
    }

    public bool Running => _host != null && _host.NowMs < _startMs + _durationMs;

    /// <summary>当前动画的目标值（没在跑就等于 <see cref="Value"/>；调用方用它判断"正在朝哪去"）。</summary>
    public float Target => _to;

    /// <summary>动画到某个值。时长 0 = 直接跳（系统关掉动画时走这条）。</summary>
    public void To(float target, double durationMs)
    {
        float now = Value;
        if (durationMs <= 0 || Math.Abs(target - now) < 0.0001f)
        {
            Jump(target);
            return;
        }
        _from = now;
        _to = target;
        _startMs = _host.NowMs;
        _durationMs = durationMs;
    }

    /// <summary>立刻到位（不做动画）。</summary>
    public void Jump(float value)
    {
        _from = _to = value;
        _startMs = double.NegativeInfinity;
        _durationMs = 0;
    }

    /// <summary>快出缓停。</summary>
    private static float EaseOut(float t)
    {
        float u = 1f - t;
        return 1f - u * u * u;
    }
}
