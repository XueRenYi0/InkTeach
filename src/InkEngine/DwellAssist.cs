using System.Numerics;

namespace InkEngine;

/// <summary>停顿成型的三种状态（见 计划-图形工具.md §四十二）。</summary>
internal enum DwellState
{
    /// <summary>手里没有笔在写。</summary>
    Idle,

    /// <summary>笔在写，正在等它**停住**。</summary>
    Tracking,

    /// <summary>停够了、也认出一个图形了，正在等抬手定型（这期间是"幽灵预览"）。</summary>
    Armed,
}

/// <summary>
/// **停顿成型**的状态机：手写一笔 → 笔尖停住 → 变成一个规整图形（用户 2026-09-23 定，
/// 参考 ClassIn / InkClass 的"停顿拉直"）。
///
/// 它只管一件事：**"这一笔停下来了吗"**。识别交给 <see cref="ShapeRecognize"/>，
/// 换对象交给 <see cref="BuildShapeStroke"/>，定时器和输入在 `Engine` 里——
/// 这样这一层能脱开窗口单独跑（`--dwelltest` 就是拿它做的边界用例）。
///
/// 参数全部照 ClassIn / InkClass 的实测口径起手（见 §42.1），真机手感再调。
/// </summary>
internal sealed class DwellAssist
{
    /// <summary>停多久算"停顿"。出处：InkClass `LineAssistHoldMs = 600`
    /// （它注释里写着这个是照 ClassIn 抄的）；**2026-09-23 用户上手之后定成 400**：
    /// "停顿时间变成400" —— 600 要等得太久（华为"一笔成形"默认只 300ms），
    /// 而写字收笔顿笔通常 100~200ms 就抬了，400 仍然分得开。</summary>
    internal const double HoldMs = 400;

    /// <summary>
    /// **静止死区**（逻辑像素）。⚠ 这一条**不开就永远检测不到停顿**：
    /// 笔尖静止按在屏幕上时，驱动仍然会持续上报亚像素级的抖动（0.3~1 px），
    /// 那些抖动会把"最后动过的时刻"一直刷新，600ms 永远攒不满。
    /// InkClass 那边也是同一个数（`LineAssistMoveSlopDip = 2`）。
    /// </summary>
    internal const float DeadZoneLogical = 2f;

    /// <summary>轮询周期（毫秒）。为什么必须有轮询：**笔不动就没有 `WM_POINTERUPDATE`**，
    /// 我们的主循环空闲时又阻塞在 `WaitMessage()`，不主动醒来就问不出"停了多久"。
    /// 40ms 是"反应够快"和"写字期间多醒几次"之间的折中（只在笔画进行中开）。</summary>
    internal const uint TickMs = 40;

    public DwellState State { get; private set; } = DwellState.Idle;

    /// <summary>最后一次"真的动了"的位置（画布坐标）。死区内的抖动不更新它。</summary>
    private Vector2 _anchor;
    private double _stillSinceMs;
    private float _deadZone;      // 画布单位

    public Vector2 Anchor => _anchor;

    /// <summary>这一笔起手。`deadZoneCanvas` 由调用方按 DPI 换算好传进来。</summary>
    public void Begin(double nowMs, Vector2 pos, float deadZoneCanvas)
    {
        State = DwellState.Tracking;
        _anchor = pos;
        _stillSinceMs = nowMs;
        _deadZone = deadZoneCanvas;
    }

    /// <summary>
    /// 每个采样点调一次。返回"这一下算不算真的动了"。
    /// **死区内的抖动一律不算**（见 <see cref="DeadZoneLogical"/>），所以"停在原地"这件事
    /// 才量得出来。
    /// </summary>
    public bool Sample(double nowMs, Vector2 pos)
    {
        if (State == DwellState.Idle) return false;
        if (Vector2.Distance(pos, _anchor) <= _deadZone) return false;
        _anchor = pos;
        _stillSinceMs = nowMs;
        return true;
    }

    /// <summary>停住多久了（毫秒）。</summary>
    public double StillMs(double nowMs) => nowMs - _stillSinceMs;

    /// <summary>停够了吗（这是"该识别了"的唯一判据）。</summary>
    public bool StillEnough(double nowMs) => StillMs(nowMs) >= HoldMs;

    /// <summary>识别通过 → 进入 armed（幽灵预览从这一刻开始）。</summary>
    public void Fire() => State = DwellState.Armed;

    public void Reset() => State = DwellState.Idle;

    /// <summary>
    /// 把识别结果变成**一个普通图形对象**：样式照抄手里那支笔（颜色 / 粗细 / 线型 / 工具）。
    ///
    /// ⚠ **只走 `SetPoints` ＋ `ApplyRotation`**，不另抄一份几何（见 §42.8 第 3 条）：
    /// 于是"停顿变出来的圆"和"用圆工具画的圆"是**同一个类型、同一条渲染 / 命中 / 存档路径**。
    ///
    /// `Tool` 保留原样（笔还是笔、荧光笔还是荧光笔）：这样荧光笔画出来的那半透明带子
    /// 变出来的线**还是半透明的**（`InkPalette.ToHighlighter` 已经把那层透明度做进颜色里了），
    /// 不需要为"变出来的图形"再定一套样式规则。
    /// </summary>
    internal static Stroke BuildShapeStroke(in ShapeGuess g, Stroke style)
    {
        var s = new Stroke
        {
            Tool = style.Tool,
            Color = style.Color,
            Width = style.Width,
            Dash = style.Dash,
            Kind = g.Kind,
        };
        s.SetPoints(g.Def);
        // 自由笔迹的**逐点压力**不能留：图形是按参数描边的，留着那个标志会让它
        // 按压力逐点变宽（一条忽粗忽细的直线）。
        s.HasPressure = false;
        ShapeRecognize.ApplyRotation(s, g);
        return s;
    }
}
