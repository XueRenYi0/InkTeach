// [职责] L1 纯逻辑测试：空闲瘦身状态机（IdleMemoryTrimmer）。
// [测试] --unittest IdleMemoryTrimmer

using InkEngine;

namespace InkTeach;

/// <summary>
/// 2026-11-25 "只能靠数字验证"的纯算法：空闲瘦身时间轴。
/// 不建窗口、不碰 D2D、不写用户目录（隔离由 UnitTestRunner.ApplyIsolation 负责）。
/// </summary>
internal static class UnitTestsAlgo
{
    /// <summary>
    /// 空闲瘦身时间轴：30s 轻度一次、穿透 5s 轻度一次、90s 深度一次；
    /// 任何用户活动重置额度；书写中（busy）绝不触发。
    /// </summary>
    [UnitTest]
    internal static void IdleMemoryTrimmer_StateTransitions()
    {
        const double act = 1_000;

        // 普通模式：30s 前一拍不动，30s 整按一次轻度，再按不再触发。
        var t = new IdleMemoryTrimmer();
        Check.Equal(IdleTrimAction.None, t.Step(2_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, t.Step(act + 30_000 - 1, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Light, t.Step(act + 30_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, t.Step(act + 30_500, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(1, t.LightCount, file: "IdleMemoryTrimmer");

        // 90s 整按一次深度，且不会重复。
        Check.Equal(IdleTrimAction.None, t.Step(act + 90_000 - 1, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Deep, t.Step(act + 90_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, t.Step(act + 200_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(1, t.DeepCount, file: "IdleMemoryTrimmer");

        // 活动重置：新操作之后，轻度可以再按一次，但深度要重新等满 90s。
        double act2 = act + 200_100;
        Check.Equal(IdleTrimAction.None, t.Step(act2, act2, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Light, t.Step(act2 + 30_000, act2, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(2, t.LightCount, file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, t.Step(act2 + 89_999, act2, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Deep, t.Step(act2 + 90_000, act2, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(2, t.DeepCount, file: "IdleMemoryTrimmer");

        // 穿透：5s 就按轻度；深度仍守 90s。
        var p = new IdleMemoryTrimmer();
        Check.Equal(IdleTrimAction.None, p.Step(2_000, act, false, true), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, p.Step(act + 5_000 - 1, act, false, true), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Light, p.Step(act + 5_000, act, false, true), file: "IdleMemoryTrimmer");
        Check.Equal(1, p.LightCount, file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, p.Step(act + 89_999, act, false, true), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Deep, p.Step(act + 90_000, act, false, true), file: "IdleMemoryTrimmer");
        Check.Equal(1, p.DeepCount, file: "IdleMemoryTrimmer");

        // 书写中：无论空闲多久，一拍都不许按；挂了 busy 之后额度作废，停下要重新等。
        var b = new IdleMemoryTrimmer();
        Check.Equal(IdleTrimAction.None, b.Step(2_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, b.Step(act + 120_000, act, true, true), file: "IdleMemoryTrimmer");
        Check.Equal(0, b.LightCount, file: "IdleMemoryTrimmer");
        Check.Equal(0, b.DeepCount, file: "IdleMemoryTrimmer");
        // 抬笔（busy=false）后从"这笔开始写的那一刻"重新计时：刚放下笔不该立刻瘦身。
        double resting = act + 120_000;
        Check.Equal(IdleTrimAction.None, b.Step(resting, resting, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, b.Step(resting + 29_999, resting, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Light, b.Step(resting + 30_000, resting, false, false), file: "IdleMemoryTrimmer");

        // WaitMs：没有待触发档 = 0（可以无限等，保持空闲 0 FPS）。
        var w = new IdleMemoryTrimmer();
        Check.Equal(0, w.WaitMs(2_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.None, w.Step(2_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(28_000, w.WaitMs(act + 2_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Light, w.Step(act + 30_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(60_000, w.WaitMs(act + 30_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(IdleTrimAction.Deep, w.Step(act + 90_000, act, false, false), file: "IdleMemoryTrimmer");
        Check.Equal(0, w.WaitMs(act + 90_000, act, false, false), file: "IdleMemoryTrimmer");
    }

    /// <summary>
    /// 分帧烘焙预算：焦点近的块排序键更小；**第 1 块永远烘**（进度必推进），
    /// 之后累计耗时到预算就停（由 Sync 置 HasPendingDirtyTiles）。
    /// </summary>
    [UnitTest]
    internal static void TileSyncBudget_PrioritizesFocus_And_ProgressesAtLeastOne()
    {
        // 焦点在画布原点：块中心越近键越小（升序 = 先烘近的）。
        double near = CanvasTileCache.FocusKey(128, 128, 0, 0);
        double far = CanvasTileCache.FocusKey(1024, 1024, 0, 0);
        Check.True(near < far, "焦点近的块必须排在前面");
        // 焦点无效（NaN）= 无穷远：Sync 里据此退回原收集顺序。
        Check.True(double.IsPositiveInfinity(CanvasTileCache.FocusKey(0, 0, float.NaN, 0)),
                   "无焦点时排序键应为 +∞");

        const double budget = CanvasTileCache.DefaultFrameBudgetMs;
        Check.True(CanvasTileCache.CanRasterMore(9_999, budget, 0), "至少烘 1 块（进度必推进）");
        Check.True(CanvasTileCache.CanRasterMore(budget - 0.1, budget, 1), "预算内继续烘");
        Check.True(!CanvasTileCache.CanRasterMore(budget, budget, 1), "到预算停止（剩余置 pending）");
        Check.True(!CanvasTileCache.CanRasterMore(budget * 5, budget, 3), "远超预算也只烘已烘的几块");
    }

    /// <summary>
    /// 分帧追帧状态转移：pending→pending(2)→justFinished(2, HasTileCatchUp=true)→next(1, HasTileCatchUp=false)→idle(0)。
    /// </summary>
    [UnitTest]
    internal static void TileSyncCatchUp_Transitions_And_HasTileCatchUpWindow()
    {
        int c = 0;
        // pending -> pending
        c = CanvasTileCache.NextCatchUpFrames(c, wasPending: false, hasPending: true);
        Check.Equal(2, c);
        Check.True(c > 1, "pending 首帧 HasTileCatchUp 应为 true");

        c = CanvasTileCache.NextCatchUpFrames(c, wasPending: true, hasPending: true);
        Check.Equal(2, c);
        Check.True(c > 1, "持续 pending 时 HasTileCatchUp 应为 true");

        // pending -> justFinished
        c = CanvasTileCache.NextCatchUpFrames(c, wasPending: true, hasPending: false);
        Check.Equal(2, c);
        Check.True(c > 1, "刚烘完那一帧 HasTileCatchUp 应为 true 以续出追帧");

        // justFinished -> next (另一份后缓冲合成完成)
        c = CanvasTileCache.NextCatchUpFrames(c, wasPending: false, hasPending: false);
        Check.Equal(1, c);
        Check.True(!(c > 1), "追帧后 HasTileCatchUp 应降为 false（停止续帧）");

        // next -> idle
        c = CanvasTileCache.NextCatchUpFrames(c, wasPending: false, hasPending: false);
        Check.Equal(0, c);
    }

    /// <summary>原始点集到"保留折线"的最大垂距（验证抽稀误差上界）。</summary>
    private static float MaxDeviationFromPolyline(List<InkPoint> origin, List<InkPoint> kept)
    {
        float worst = 0f;
        foreach (var p in origin)
        {
            float best = float.MaxValue;
            for (int k = 1; k < kept.Count; k++)
                best = MathF.Min(best, DistToSegment(p, kept[k - 1], kept[k]));
            if (best == float.MaxValue) best = 0f;
            if (best > worst) worst = best;
        }
        return worst;
    }

    private static float DistToSegment(InkPoint p, InkPoint a, InkPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-12f)
        {
            float ex = p.X - a.X, ey = p.Y - a.Y;
            return MathF.Sqrt(ex * ex + ey * ey);
        }
        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0f, 1f);
        float qx = a.X + dx * t, qy = a.Y + dy * t;
        float vx = p.X - qx, vy = p.Y - qy;
        return MathF.Sqrt(vx * vx + vy * vy);
    }
}
