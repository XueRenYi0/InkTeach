// [职责] 空闲内存瘦身的时间驱动状态机（30s 轻度 / 穿透 5s / 90s 深度；任何活动重置）。
// [测试] --unittest IdleMemoryTrimmer

using System;

namespace InkEngine;

/// <summary>空闲瘦身档位。</summary>
internal enum IdleTrimAction
{
    None = 0,
    /// <summary>轻度：只放视图外缓存 / 非当前页的 GPU 缓存，不动可见内容。</summary>
    Light = 1,
    /// <summary>深度：LOH 压缩 + GC + Trim 显存（仅在不处于书写低延迟档时）。</summary>
    Deep = 2,
}

/// <summary>
/// **空闲内存瘦身状态机**（只做时间判断，不碰任何 GPU / GC 资源）。
///
/// 拆成纯状态机的理由：它决定的动作直接关系"空闲叫醒第一笔会不会卡"，而这件事
/// 不可能靠肉眼看出来。时间轴放在这里，单元测试就能把 5s / 30s / 90s / 活动重置
/// 全部钉住（<c>--unittest IdleMemoryTrimmer</c>），引擎那边只剩"触发后做什么"。
///
/// 边界（用户 2026-11-25 拍板）：
///   · 书写、拖动、回放、相机动画、截图取景期间**绝不触发**；
///   · 每轮空闲里轻度 / 深度**各最多一次**，任何用户活动都会重置计时；
///   · 不调用 <c>EmptyWorkingSet</c>、不重建 D3D/D2D 设备——唤醒时不能缺页顿挫。
///
/// 单例放在类自己身上（<see cref="Host"/>），**不给 Engine 新增私有字段**（ADR-005 D3）：
/// 进程内只有一个引擎实例，状态跟它同寿命。
/// </summary>
internal sealed class IdleMemoryTrimmer
{
    /// <summary>普通模式无操作多久触发轻度瘦身。</summary>
    internal const double LightIdleMs = 30_000;

    /// <summary>穿透模式（用户看不见板）无操作多久触发轻度瘦身——可以更积极。</summary>
    internal const double PassThroughIdleMs = 5_000;

    /// <summary>无操作多久触发深度瘦身（必须排在轻度之后）。</summary>
    internal const double DeepIdleMs = 90_000;

    /// <summary>主循环共用的实例（见类头；自检/单测可另外 new 一份）。</summary>
    internal static readonly IdleMemoryTrimmer Host = new();

    /// <summary>上一次见到的活动时刻；-1 = 还没见过（第一次 Step 会先对齐）。</summary>
    private double _lastActivitySeen = -1;

    private bool _lightDone;
    private bool _deepDone;

    /// <summary>诊断：累计触发次数。</summary>
    internal int LightCount { get; private set; }
    internal int DeepCount { get; private set; }

    /// <summary>
    /// 推进一拍。纯函数式（除了内部"这一轮已触发过"的两个标记），返回这一拍该做什么。
    /// </summary>
    /// <param name="nowMs">当前时钟（毫秒）。</param>
    /// <param name="lastActivityMs">最近一次用户活动时刻（引擎的 <c>_lastActivityMs</c>）。</param>
    /// <param name="busy">书写 / 拖动 / 回放 / 相机动画 / 截图取景期间为 true。</param>
    /// <param name="passThrough">当前是否穿透模式（只影响轻度档的等待时间）。</param>
    internal IdleTrimAction Step(double nowMs, double lastActivityMs, bool busy, bool passThrough)
    {
        // 忙，或活动时刻前进了 = 有新操作：这一轮的额度全部作废。
        if (busy || lastActivityMs > _lastActivitySeen)
        {
            _lastActivitySeen = lastActivityMs;
            _lightDone = false;
            _deepDone = false;
            return IdleTrimAction.None;
        }

        double idle = nowMs - lastActivityMs;
        if (!_lightDone
            && ((passThrough && idle >= PassThroughIdleMs) || idle >= LightIdleMs))
        {
            _lightDone = true;
            LightCount++;
            return IdleTrimAction.Light;
        }
        if (_lightDone && !_deepDone && idle >= DeepIdleMs)
        {
            _deepDone = true;
            DeepCount++;
            return IdleTrimAction.Deep;
        }
        return IdleTrimAction.None;
    }

    /// <summary>
    /// 主循环下一次最多睡多久（毫秒）；<c>0</c> = 可以无限等（没有待触发的档，
    /// 或者有新活动等着 Step 先对齐）。
    ///
    /// 为什么需要它：空闲时主循环睡在 <c>WaitMessage()</c> 上，没有任何消息就永远
    /// 不会醒——30 秒的轻度档就永远不会到点。用一次带超时的
    /// <c>MsgWaitForMultipleObjectsEx</c> 醒来即可，**不产生任何帧**（0 FPS 不变）。
    /// </summary>
    internal int WaitMs(double nowMs, double lastActivityMs, bool busy, bool passThrough)
    {
        if (busy || (_lightDone && _deepDone)) return 0;
        if (lastActivityMs > _lastActivitySeen) return 0;

        double idle = nowMs - lastActivityMs;
        double deadline = !_lightDone
            ? (passThrough ? PassThroughIdleMs : LightIdleMs)
            : DeepIdleMs;
        double remain = deadline - idle;
        if (remain <= 0) return 1;
        return (int)Math.Min(60_000, Math.Ceiling(remain));
    }
}
