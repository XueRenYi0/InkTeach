// [职责] L1 纯逻辑测试（2026-11-25 三项性能优化）：多屏跳帧判定 / 高压保险丝 / 三段式点抽稀。
// [测试] --unittest MultiMonitorSkip / FramePressureValve / StrokeSimplifier

using InkEngine;
using System.Numerics;

namespace InkTeach;

/// <summary>
/// 这一批测试的共同点：**判据是数字，靠肉眼验不出来**。
/// 多屏跳帧漏一条会在副屏留残影、保险丝门槛差一点会来回抖、抽稀差一个点就换一种笔锋。
/// 所以全部收成纯逻辑（不建窗口、不碰 D2D、不写用户目录；隔离见 UnitTestRunner.ApplyIsolation）。
/// </summary>
internal static class UnitTestsPerf
{
    // ======================================================================
    //  1. 多屏按窗跳帧
    // ======================================================================

    /// <summary>
    /// 干净的副屏必须跳过；指针离开那一帧**必须再画一帧**把光标擦掉（防残影）；
    /// 单屏 / 自检永远不跳。
    /// </summary>
    [UnitTest]
    internal static void MultiMonitorSkip_SkipsCleanSecondary_And_CleansUpLeavingPointer()
    {
        // 干净副屏：多屏、非自检、无界面、无指针、无湿墨、无脏区、无相机变化、无浮层 → 跳。
        var clean = new WindowFrameInputs { WindowCount = 2 };
        Check.True(!WindowSkipDecision.ShouldRenderWindow(in clean), "干净副屏必须跳过");

        // 单屏：哪怕什么变化都没有，也必须画（不然画面根本不更新）。
        var single = new WindowFrameInputs { WindowCount = 1 };
        Check.True(WindowSkipDecision.ShouldRenderWindow(in single), "单屏永远画");

        // 自检：单帧截图，跳帧会拍到半张画面 → 永远画。
        var selfCheck = new WindowFrameInputs { WindowCount = 2, SelfCheckMode = true };
        Check.True(WindowSkipDecision.ShouldRenderWindow(in selfCheck), "自检永远画");

        // 每一条"必须画"都要钉住一个反例（删任一行判定就会有测试红）。
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { HasUiInWindow = true }), "界面在这扇屏上");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { HasFullDirty = true }), "整层作废");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { HasPendingTiles = true }), "还有脏块欠着");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { FullFramesLeft = 1 }), "还欠整屏帧");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { CameraOrThemeChanged = true }), "相机/底色变了");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { DocPagesChanged = true }), "页层/补丁变了");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { HasGlobalOverlay = true }), "全局浮层在");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { PointerOnWindow = true }), "指针在副屏上");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { PointerWasOnWindow = true }), "指针刚离开");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { ActiveStrokeIntersects = true }), "湿墨压到副屏");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { DocDeltaIntersects = true }), "改动压到副屏");
        Check.True(WindowSkipDecision.ShouldRenderWindow(clean with { HasStalePixels = true }), "后缓冲还有旧浮层/界面像素");

        // ★ 防残影的主线：上一帧画过浮动层（光标/湿墨）→ 这一帧必须再画一次擦干净；
        //   擦完之后（HasDynamicOverlayNow = false）才允许跳过。
        var leaving = clean with { HadDynamicOverlayLastFrame = true };
        Check.True(WindowSkipDecision.ShouldRenderWindow(in leaving), "画过浮动层：必须先擦一帧");

        var mem = new WindowRenderMemory();
        // 第 1 帧：指针在副屏上画光标 → 一定渲染。
        var withCursor = clean with { PointerOnWindow = true, HasDynamicOverlayNow = true };
        Check.True(WindowSkipDecision.ShouldRenderWindow(in withCursor), "有光标必画");
        mem.HadDynamicOverlayLastFrame = true;
        mem.PointerWasOnWindow = true;

        // 第 2 帧：指针移走了，但上一帧画过光标 → 还要画一帧（这一帧不画光标）。
        var cleanup = clean with
        {
            HadDynamicOverlayLastFrame = mem.HadDynamicOverlayLastFrame,
            PointerWasOnWindow = mem.PointerWasOnWindow,
        };
        Check.True(WindowSkipDecision.ShouldRenderWindow(in cleanup), "指针离开那一帧必须补擦");

        // 第 3 帧：上一帧只是"擦"，没画浮动层 → 可以跳过。
        var idle = clean with { HadDynamicOverlayLastFrame = false, PointerWasOnWindow = false };
        Check.True(!WindowSkipDecision.ShouldRenderWindow(in idle), "擦干净之后副屏才允许跳过");
    }

    // ======================================================================
    //  2. 高压保险丝
    // ======================================================================

    /// <summary>
    /// 连续 6 帧 &gt; 15ms → 进入高压；连续 20 帧 ≤ 10ms → 退出；
    /// 中间地带（10~15ms）不推进任一计数；空闲 10 秒自动退出。
    /// </summary>
    [UnitTest]
    internal static void FramePressureValve_EntersOnSustainedSlow_RecoversOnFast()
    {
        var v = new FramePressureValve();
        double t = 1_000;

        // 慢帧差一帧：不进入。
        for (int i = 0; i < FramePressureValve.EnterSlowStreak - 1; i++)
        {
            t += 20;
            Check.True(!v.RecordFrame(20.0, t), "还没到连续慢帧数，不该翻转");
        }
        Check.True(!v.HighPressure, "差一帧不该进入高压");

        // 整 6 帧：进入。
        t += 20;
        Check.True(v.RecordFrame(20.0, t), "连续 6 帧慢 → 必须翻转");
        Check.True(v.HighPressure, "进入高压");
        Check.Equal(1, v.EnterCount);

        // 快帧差一帧：不退出。
        for (int i = 0; i < FramePressureValve.ExitFastStreak - 1; i++)
        {
            t += 8;
            v.RecordFrame(8.0, t);
        }
        Check.True(v.HighPressure, "快帧还不够 20，仍应保持高压");

        // 整 20 帧：退出。
        t += 8;
        Check.True(v.RecordFrame(8.0, t), "连续 20 帧快 → 必须翻转");
        Check.True(!v.HighPressure, "退出高压");

        // 中间地带（12ms）：两个计数都不动，状态保持。
        var m = new FramePressureValve();
        double tm = 1_000;
        for (int i = 0; i < 50; i++) { tm += 12; m.RecordFrame(12.0, tm); }
        Check.True(!m.HighPressure, "10~15ms 的中间地带不该把人推进高压");

        // 慢帧之后来一帧中间地带：慢计数**重置**（不是连续慢帧）。
        var r = new FramePressureValve();
        double tr = 1_000;
        for (int i = 0; i < FramePressureValve.EnterSlowStreak - 1; i++) { tr += 20; r.RecordFrame(20.0, tr); }
        tr += 12; r.RecordFrame(12.0, tr);
        tr += 20; r.RecordFrame(20.0, tr);
        Check.True(!r.HighPressure, "中间地带打断连续慢帧");

        // 空闲 10 秒再回来：下一帧直接解除高压（空闲不产生帧，没人去数快帧）。
        var idle = new FramePressureValve();
        double ti = 1_000;
        for (int i = 0; i < FramePressureValve.EnterSlowStreak; i++) { ti += 20; idle.RecordFrame(20.0, ti); }
        Check.True(idle.HighPressure, "先确认已在高压");
        Check.True(idle.RecordFrame(20.0, ti + FramePressureValve.IdleExitMs + 1), "空闲超 10 秒 → 解除");
        Check.True(!idle.HighPressure, "解除高压");
        // 差 1ms 的空闲不算：仍在高压，且这一帧照常计入。
        var idle2 = new FramePressureValve();
        double tj = 1_000;
        for (int i = 0; i < FramePressureValve.EnterSlowStreak; i++) { tj += 20; idle2.RecordFrame(20.0, tj); }
        Check.True(!idle2.RecordFrame(20.0, tj + FramePressureValve.IdleExitMs - 1), "差 1ms 还不解除");
        Check.True(idle2.HighPressure, "仍在高压");

        // StepIdle：空闲路径同样能解除（没有出帧时用）。
        var si = new FramePressureValve();
        double ts = 1_000;
        for (int i = 0; i < FramePressureValve.EnterSlowStreak; i++) { ts += 20; si.RecordFrame(20.0, ts); }
        Check.True(!si.StepIdle(ts + 5_000), "还没超 10 秒");
        Check.True(si.StepIdle(ts + FramePressureValve.IdleExitMs + 1), "StepIdle 也要能解除");
    }

    // ======================================================================
    //  3. 三段式首尾冻结无损抽稀
    // ======================================================================

    /// <summary>
    /// 硬验收：尾段冻结区逐点原样、<see cref="StrokeTaper.EndSpeed"/> 抽稀前后**完全相等**、
    /// 不造 &gt;30ms 的时间空洞、中段冗余点确实压掉了、拐角与压感波峰完整保留。
    /// </summary>
    [UnitTest]
    internal static void StrokeSimplifier_FreezesTailForTaper_And_CompressesMiddle()
    {
        // ---- 构造：200 点模拟笔画 ----------------------------------------
        // 0..19   起笔（慢，20ms/点，直）
        // 20..139 中段：直行 ~60 点 + 90° 拐角 + 缓弯 + 一个压感波峰（10ms/点，冗余点多）
        // 140..199 尾段：170ms 加速收笔（共 60 点），笔锋全靠这一段
        var pts = new List<InkPoint>(200);
        for (int i = 0; i < 200; i++)
        {
            float x, y, p; double dt;
            if (i < 20)
            {
                x = i * 0.5f; y = 0f; p = 0.35f; dt = 20.0;
            }
            else if (i < 60)
            {
                // 中段直行：每点前进 1px（完全共线 → 必须被压掉）
                x = 10f + (i - 20) * 1.0f; y = 0f; p = 0.35f; dt = 10.0;
            }
            else if (i < 80)
            {
                // 90° 拐角 + 缓弯：从 (50,0) 开始往 +Y 走，并逐点偏一点 X
                int k = i - 60;
                x = 50f + k * 0.35f; y = k * 1.2f; p = 0.35f; dt = 10.0;
            }
            else if (i < 140)
            {
                // 压感波峰：直行 + 压力从中段 0.35 拱到 0.85 再回落（每点采样都不同）
                int k = i - 80;
                x = 50f + 7f + k * 1.0f; y = 24f + k * 0.1f;
                p = 0.35f + 0.5f * (float)Math.Sin(Math.PI * k / 60.0);
                dt = 10.0;
            }
            else
            {
                // 尾段：170ms / 60 点 ≈ 2.83ms 每点，速度明显加快 → 笔锋
                int k = i - 140;
                x = 117f + k * 3.0f; y = 30f + k * 0.5f;
                p = 0.35f + 0.3f * (k / 60f);
                dt = 170.0 / 60.0;
            }
            double t = i == 0 ? 1_000.0 : pts[i - 1].T + dt;
            pts.Add(new InkPoint { X = x, Y = y, P = p, T = t });
        }
        Check.Equal(200, pts.Count);

        // 抽稀前的笔锋口径（这是唯一不能变的数）。
        var before = MakeStroke(pts, width: 6f);
        float speedBefore = StrokeTaper.EndSpeed(before);

        // ---- 抽稀 --------------------------------------------------------
        var work = new List<InkPoint>(pts);
        int removed = StrokeSimplifier.SimplifyInPlace(work, hasPressure: true, width: 6f);
        Check.True(removed > 0, "中段连绵的共线冗余点必须被压掉");
        Check.True(work.Count < pts.Count, "点数必须真的减少");

        // ---- ① 笔锋判据完全相等（差 0）-------------------------------------
        var after = MakeStroke(work, width: 6f);
        float speedAfter = StrokeTaper.EndSpeed(after);
        Check.Equal(speedBefore, speedAfter);
        Check.True(float.IsFinite(speedBefore) && speedBefore > StrokeTaper.VMin,
                   "模拟数据必须真的触发笔锋（否则这一条测了个寂寞）");

        // ---- ①.5 慢写抬笔：StrokeMotion 缓存必须随抽稀重建，不许留旧 200 点 -----
        VerifyMotionCacheRebuildsAfterSimplify(pts);

        // ---- ② 尾段冻结区逐点完全一致 -------------------------------------
        // 尾段冻结的起点：从末点往回满足 ≥180ms、≥ max(4.5×6=27, 40) = 40px、≥8 点。
        int tailStart = FindTailStart(pts, width: 6f);
        Check.True(tailStart > 0, "构造的尾段必须够长");
        int tailCount = pts.Count - tailStart;
        Check.True(tailCount >= StrokeSimplifier.TailMinPoints, "尾段点数下限");
        // 抽稀后的末 tailCount 个点必须和原始的末 tailCount 个点一模一样。
        for (int k = 0; k < tailCount; k++)
        {
            var a = pts[pts.Count - tailCount + k];
            var b = work[work.Count - tailCount + k];
            Check.Equal(a.X, b.X);
            Check.Equal(a.Y, b.Y);
            Check.Equal(a.P, b.P);
            Check.Equal(a.T, b.T);
        }

        // 首段冻结：前 5 点也必须一模一样。
        for (int k = 0; k < StrokeSimplifier.HeadFreezeCount; k++)
        {
            Check.Equal(pts[k].X, work[k].X);
            Check.Equal(pts[k].Y, work[k].Y);
            Check.Equal(pts[k].P, work[k].P);
            Check.Equal(pts[k].T, work[k].T);
        }

        // ---- ③ 不许造出 > 35ms 的人为时间空洞 ------------------------------
        double widest = 0;
        for (int i = 1; i < work.Count; i++)
            widest = Math.Max(widest, work[i].T - work[i - 1].T);
        Check.True(widest <= 35.0, $"抽稀后最大时间间隔 {widest:F1}ms 必须 ≤ 35ms");

        // ---- ④ 拐角与压感波峰必须还在 -------------------------------------
        // 拐角：原始的 (50,0) 是那个 90° 点，它必须仍在保留集里（位置级）。
        Check.True(HasNear(work, 50f, 0f, 0.6f), "90° 拐角点必须保留");
        // 压感波峰：原始最大 P 的点（≈0.85）必须仍在保留集里（压感偏差 > 0.02 → 不会被删）。
        float peakP = 0f;
        foreach (var p in pts) peakP = Math.Max(peakP, p.P);
        Check.True(HasPressureNear(work, peakP, 0.02f), "压感波峰必须保留");

        // ---- ⑤ 位置误差上界（0.25px 阈值 → 实际误差必须同量级）-------------
        float worst = 0f;
        foreach (var p in pts) worst = Math.Max(worst, DistToKept(work, p));
        Check.True(worst <= 0.3f, $"抽稀后任何原始点离保留折线 ≤ 0.3px，实测 {worst:F3}px");

        // ---- 短笔画 / 非自由笔迹：原样不动 ---------------------------------
        var shortPts = new List<InkPoint>(pts.GetRange(0, StrokeSimplifier.MinPoints - 1));
        Check.Equal(0, StrokeSimplifier.SimplifyInPlace(shortPts, true, 6f));
        Check.Equal(StrokeSimplifier.MinPoints - 1, shortPts.Count);
    }

    // ---- 测试辅助 --------------------------------------------------------

    private static Stroke MakeStroke(List<InkPoint> pts, float width)
    {
        var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = width, HasPressure = true };
        foreach (var p in pts) s.Points.Add(p);
        s.InvalidateMetrics();
        return s;
    }

    /// <summary>和 StrokeSimplifier 同一条判据算出尾段起点（测试里独立再写一遍，防止自证）。</summary>
    private static int FindTailStart(List<InkPoint> pts, float width)
    {
        int n = pts.Count;
        double needArc = Math.Max(StrokeSimplifier.TailMinWidths * width, StrokeSimplifier.TailMinPx);
        double ms = 0, arc = 0;
        for (int i = n - 1; i >= 1; i--)
        {
            double dt = pts[i].T - pts[i - 1].T;
            if (dt > 0) ms += dt;
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            arc += Math.Sqrt(dx * dx + dy * dy);
            if (n - i + 1 < StrokeSimplifier.TailMinPoints) continue;
            if (ms >= StrokeSimplifier.TailMinMs && arc >= needArc) return i;
        }
        return -1;
    }

    private static bool HasNear(List<InkPoint> pts, float x, float y, float tol)
    {
        foreach (var p in pts)
            if (MathF.Abs(p.X - x) <= tol && MathF.Abs(p.Y - y) <= tol) return true;
        return false;
    }

    private static bool HasPressureNear(List<InkPoint> pts, float want, float tol)
    {
        foreach (var p in pts) if (MathF.Abs(p.P - want) <= tol) return true;
        return false;
    }

    private static float DistToKept(List<InkPoint> kept, InkPoint p)
    {
        float best = float.MaxValue;
        for (int k = 1; k < kept.Count; k++)
            best = MathF.Min(best, DistToSegment(p, kept[k - 1], kept[k]));
        return best == float.MaxValue ? 0f : best;
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

    private static void VerifyMotionCacheRebuildsAfterSimplify(List<InkPoint> pts)
    {
        var liveSim = MakeStroke(pts, width: 6f);
        liveSim.RawWhileLive = true;
        Check.True(StrokeMotion.Build(liveSim, StrokeMotionMode.Mean2), "活笔建模必须成功");
        Check.Equal(200, StrokeMotion.Count);
        liveSim.RawWhileLive = false;
        int liveRemoved = StrokeSimplifier.SimplifyInPlace(liveSim.Points, hasPressure: true, width: 6f);
        Check.True(liveRemoved > 0, "慢写模拟笔必须真的抽掉中段冗余点");
        liveSim.InvalidateMetrics();
        Check.True(StrokeMotion.Build(liveSim, StrokeMotionMode.Mean2), "抬笔抽稀后重建必须成功");
        Check.True(StrokeMotion.Count <= liveSim.Points.Count + 8,
                   $"StrokeMotion 输出点数 {StrokeMotion.Count} 不许停留在旧的 200（抽稀后 {liveSim.Points.Count}）");
    }

    // ======================================================================
    //  4. 图形面积擦除"零变形"：精确折线熔墨
    // ======================================================================

    /// <summary>
    /// 图形熔墨产物的硬约束（2026-11-25 面积擦除零变形）：
    ///   · 每一片都是 <see cref="Stroke.ExactPolyline"/>，且不带压感；
    ///   · 笔锋 / 抬笔抽稀**都不许碰它**（碰了就会把直角和圆弧重新解释一遍）；
    ///   · 存盘 → 重开之后标记还在（不然"重开又变形"）；
    ///   · 自适应圆弧段数对大圆（r=300）足够密（弦高 ≤ 0.2px，≥80 段）。
    /// </summary>
    [UnitTest]
    internal static void ShapeMelt_ExactPolyline_RoundTrip_And_BypassesTaperAndSimplifier()
    {
        // ---- ① 自适应圆弧段数：r=300 整圆 ≥ 80 段，且弦高真的 ≤ 0.2px ----
        int n300 = Stroke.AdaptiveArcSegments(300f, MathF.Tau);
        Check.True(n300 >= 80, $"r=300 整圆自适应段数应 ≥80，实际 {n300}");
        Check.True(n300 <= 180, $"段数上限 180，实际 {n300}");
        float sagitta = 300f * (1f - MathF.Cos(MathF.PI / n300));
        Check.True(sagitta <= Stroke.ExactChordErrorPx + 1e-3f,
                   $"弦高 {sagitta:F3}px 应 ≤ {Stroke.ExactChordErrorPx}px");
        // 小圆也不能缩到看不出圆（下限 24 段）。
        Check.True(Stroke.AdaptiveArcSegments(1f, MathF.Tau) >= 24, "小圆至少 24 段");

        // ---- ② 39 / 41 张图形卡逐张熔墨：全部带精确折线标记 ----
        int totalPieces = 0, singlePointDots = 0;
        foreach (var p in ShapeCatalog.All)
        {
            var shape = ShapePreview.Create(p);
            var pieces = shape.MeltToInkParts();
            Check.True(pieces.Count > 0, $"{p.Label}：熔墨后一片都没有");
            foreach (var m in pieces)
            {
                Check.True(m.ExactPolyline, $"{p.Label}：熔墨片段缺 ExactPolyline");
                Check.True(!m.HasPressure, $"{p.Label}：熔墨片段不该带压感");
                Check.True(!StrokeTaper.Eligible(m), $"{p.Label}：笔锋不许作用于熔墨");
                Check.True(!StrokeSimplifier.Eligible(m), $"{p.Label}：抬笔抽稀不许作用于熔墨");
                if (m.Points.Count == 1) singlePointDots++;
            }
            totalPieces += pieces.Count;
            // 圆：圆心点必须作为单点笔迹保留（擦一下不能没）。
            if (p.Tool == Tool.Circle)
                Check.True(pieces.Exists(x => x.Points.Count == 1),
                           "圆：圆心点必须熔成一条单点笔迹");
        }
        Check.True(totalPieces >= ShapeCatalog.All.Length, "熔墨片段总数应不少于图形数");
        Check.True(singlePointDots >= 1, "至少圆心的单点装饰被保留下来");

        // ---- ③ 存档往返：ExactPolyline 必须活下来 ----
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f), Width = 3f,
        };
        rect.AddPoint(100f, 100f, 1f, 0);
        rect.AddPoint(400f, 300f, 1f, 0);
        var melted = rect.MeltToInkParts();
        Check.True(melted.Count >= 1, "矩形熔墨至少一片");

        byte[] blob = InkSerializer.SaveStrokes(melted);
        var back = InkSerializer.LoadStrokes(blob, out _);
        Check.Equal(melted.Count, back.Count);
        for (int i = 0; i < back.Count; i++)
        {
            Check.True(back[i].ExactPolyline, $"往返后第 {i} 片丢了 ExactPolyline");
            Check.True(!back[i].HasPressure, $"往返后第 {i} 片不该带压感");
            Check.Equal(melted[i].Points.Count, back[i].Points.Count);
        }
    }
}
