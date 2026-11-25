// [职责] 高压保险丝时间驱动状态机：连续慢帧 → 进入高压（收紧分块预算、暂停离屏预取），连续快帧/空闲 → 退出。
// [测试] --unittest FramePressureValve

using System;

namespace InkEngine;

/// <summary>
/// **动态高压保险丝**（2026-11-25）。
///
/// 为什么要有它：书写优先预算（<see cref="CanvasTileCache.WritingFrameBudgetMs"/> = 3.5ms）
/// 是按"这一帧确实在写"来判断的，可现实中卡顿的来源不止一种——后台 OCR、一次大范围橡皮、
/// 密集 PPT 翻页后的整层重烘，都可能让帧时间长期顶在 20ms 以上。这时更要紧的是
/// **保住指针采样和湿墨**，而不是继续抢时间补远处的分块。
///
/// 判据（两个阈值刻意拉开，避免在边界上反复跳）：
///   · 连续 <see cref="EnterSlowStreak"/> 帧活动帧耗时 &gt; <see cref="SlowFrameMs"/> → 进入；
///   · 连续 <see cref="ExitFastStreak"/> 帧 ≤ <see cref="FastFrameMs"/>，或
///     **空闲超过 <see cref="IdleExitMs"/>**（没有帧 = 压力自然消失）→ 退出。
///
/// 边界：状态机本身不碰任何 GPU / 分块对象，只回答"现在紧不紧"；调用方据此
/// 收紧预算、暂停离屏预取（**不改** <see cref="CanvasTileCache.PrefetchEnabled"/> 这个静态配置）。
/// 单例放在类自己身上（<see cref="Host"/>），**不给 Engine 新增私有字段**（ADR-005 D3）。
/// </summary>
internal sealed class FramePressureValve
{
    /// <summary>超过它算"慢帧"。</summary>
    internal const double SlowFrameMs = 15.0;

    /// <summary>连续这么多慢帧才进入高压（偶发一两帧抖动不算）。</summary>
    internal const int EnterSlowStreak = 6;

    /// <summary>不超过它算"快帧"。</summary>
    internal const double FastFrameMs = 10.0;

    /// <summary>连续这么多快帧才退出高压（宁可在高压多待一会儿，也别来回抖）。</summary>
    internal const int ExitFastStreak = 20;

    /// <summary>这么久没有活动帧 → 高压自动解除（空闲不产生帧，没人去数快帧）。</summary>
    internal const double IdleExitMs = 10_000;

    /// <summary>主循环共用的实例（见类头；自检 / 单元测试可另外 new 一份）。</summary>
    internal static readonly FramePressureValve Host = new();

    /// <summary>现在是不是高压态。</summary>
    internal bool HighPressure { get; private set; }

    /// <summary>诊断：累计进入高压的次数。</summary>
    internal int EnterCount { get; private set; }

    /// <summary>连续慢帧 / 连续快帧的当前计数。</summary>
    private int _slowStreak, _fastStreak;

    /// <summary>上一次喂帧的时刻（ms；-1 = 还没喂过）。</summary>
    private double _lastFrameAtMs = -1;

    /// <summary>
    /// 喂一帧的实测耗时（毫秒，**不含等垂直同步**）。返回 true = 这一帧状态发生了翻转
    /// （调用方据此打一行日志）。
    /// </summary>
    internal bool RecordFrame(double frameMs, double nowMs)
    {
        // 空闲超过阈值再回来：中间的帧没人喂，计数作废，压力视为已经消失。
        if (_lastFrameAtMs >= 0 && nowMs - _lastFrameAtMs > IdleExitMs && HighPressure)
        {
            _lastFrameAtMs = nowMs;
            _slowStreak = _fastStreak = 0;
            HighPressure = false;
            return true;
        }
        _lastFrameAtMs = nowMs;

        if (frameMs > SlowFrameMs)
        {
            _slowStreak++;
            _fastStreak = 0;
            if (!HighPressure && _slowStreak >= EnterSlowStreak)
            {
                HighPressure = true;
                EnterCount++;
                return true;
            }
            return false;
        }

        if (frameMs <= FastFrameMs)
        {
            _fastStreak++;
            _slowStreak = 0;
            if (HighPressure && _fastStreak >= ExitFastStreak)
            {
                HighPressure = false;
                return true;
            }
            return false;
        }

        // 中间地带（10ms < 耗时 ≤ 15ms）：两个计数都清零，现有档位保持不变。
        // "连续"是字面意思——中间夹一帧 12ms，就不算"连续 6 帧 > 15ms"了。
        _slowStreak = 0;
        _fastStreak = 0;
        return false;
    }

    /// <summary>
    /// 空闲推一拍（没有出帧时调用）：超过 <see cref="IdleExitMs"/> 没喂帧就解除高压。
    /// 返回 true = 这一拍解除了。
    /// </summary>
    internal bool StepIdle(double nowMs)
    {
        if (!HighPressure || _lastFrameAtMs < 0) return false;
        if (nowMs - _lastFrameAtMs <= IdleExitMs) return false;
        _slowStreak = _fastStreak = 0;
        HighPressure = false;
        return true;
    }
}
