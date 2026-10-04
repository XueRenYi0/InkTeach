using System.Numerics;

namespace InkEngine;

/// <summary>
/// `--motiontest`：**笔迹运动模型对照台的自检**（纯算法，不建窗口、不碰输入）。
///
/// 同一批语料分别过六个模式，打一张表——它回答的就是用户那句话：
/// **"细笔抖动到底归谁"**。每一列都是外部可验证的量，不看模型内部状态：
///   · 抖动：浅斜率直线 + 高频噪声（先模拟 ptPixelLocation 取整），到"真实直线"的横向 RMS；
///   · 直角：慢速 90°，输出到原折线的最大距离（越小说明直角保得越好）；
///   · 保真：圆弧到原折线的最大距离（越大说明形状被改得越多）；
///   · 滞后：活笔（不落笔）时"最后一枚输出点"离"最后一条原始输入"多远——跟手程度
///     （含默认开启的活笔镜像；关镜像可见距离窗 ~6px 的模型滞后，见下方镜像检查）；
///   · 端点：落笔后首/末点的位置误差（收笔追赶有效没有）；
///   · 性能：2000 点一次建模耗时；输出点数。
///
/// M0（raw）/ M1（catmull）没有"模型器"，按定义它们的滞后为 0（活笔就是原始折线），
/// 表里会如实标注。
/// </summary>
internal static class MotionProbe
{
    private const double InputHz = 130;
    private const float SpeedPxPerSec = 400;

    public static int Run()
    {
        Console.WriteLine("=== 笔迹运动模型自检（2026-10-05 收敛后：baseline / catmull / mean2）===");
        Console.WriteLine($"  语料：{InputHz:F0}Hz、书写 {SpeedPxPerSec:F0}px/s；D1（himetric 亚像素）需要真笔，这里量不到");
        Console.WriteLine();
        Console.WriteLine("  模式        抖动RMS(px)  折线度(°)  直角偏离(px)  圆弧偏离(px)  滞后(px)  末点误差(px)  输出点   2000点(ms)");
        Console.WriteLine("  ----------  -----------  ---------  ------------  ------------  --------  ------------  --------  ----------");

        // [停用 2026-10-05] sliding / spring / oneeuro / mean / gauss 各模式已停用（代码保留）。
        // probe 只跑 baseline + 保留量：Raw（噪声基线）、Catmull（老路径）、Mean2（默认）。
        var modes = new[]
        {
            StrokeMotionMode.Raw, StrokeMotionMode.Catmull, StrokeMotionMode.Mean2,
        };

        int hardFail = 0;
        foreach (var mode in modes)
        {
            // ① 抖动（浅斜率 + 高频噪声）——**去趋势后的粗糙度**：
            // 先把横向残差做移动平均、再把它减掉，只留高频部分——否则"整体滞后"
            // 会在直线上表现成一个固定横向偏移，把滤波器的分数压低。
            var noisy = BuildNoisyLine(0.013f, 100f, 400, 0.5f, seed: 7);
            var noisyModeled = Model(mode, noisy, committed: true, hasPressure: false, out _);
            float jitterRms = LateralRoughness(noisyModeled, 0.013f, 100f);

            // ② 慢速直角
            var corner = BuildCorner(armPx: 400f);
            var cornerModeled = Model(mode, corner, committed: true, hasPressure: false, out _);
            float cornerDev = MaxDistanceToPolyline(cornerModeled, corner);

            // ②·5 折线度：圆弧输出上"任意 6px 弧长内最大方向变化"（°）。
            //        折线点稀时每个顶点都是大转角；曲线化/升采样后应显著变小。
            // ③ 圆弧保真
            var arc = BuildArc(400f, 800);
            var arcModeled = Model(mode, arc, committed: true, hasPressure: false, out _);
            float arcDev = MaxDistanceToPolyline(arcModeled, arc);
            float faceting = MaxTurnOverWindow(arcModeled, 6f);

            // ④ 活笔滞后（不落笔）
            var liveModeled = Model(mode, arc, committed: false, hasPressure: false, out _);
            float lag = liveModeled.Count > 0
                ? Vector2.Distance(liveModeled[^1], arc[^1])
                : float.NaN;

            // ⑤ 端点误差（落笔后）
            float endErr = arcModeled.Count > 0
                ? Vector2.Distance(arcModeled[^1], arc[^1])
                : float.NaN;

            // ⑥ 性能：2000 点（先跑一遍热身，去掉 JIT/首次分配；报第二遍）
            var big = BuildArc(900f, 2000);
            Model(mode, big, committed: true, hasPressure: true, out _);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Model(mode, big, committed: true, hasPressure: true, out _);
            sw.Stop();

            bool finite = float.IsFinite(jitterRms) && float.IsFinite(cornerDev) && float.IsFinite(arcDev)
                          && float.IsFinite(lag) && float.IsFinite(endErr) && float.IsFinite(faceting);
            if (!finite) hardFail++;

            Console.WriteLine($"  {mode,-10}  {jitterRms,11:F3}  {faceting,9:F2}  {cornerDev,12:F2}  {arcDev,12:F3}  "
                              + $"{lag,8:F2}  {endErr,12:F3}  {arcModeled.Count,8}  {sw.Elapsed.TotalMilliseconds,10:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("  怎么读这张表：");
        Console.WriteLine("   · 抖动列越小越好（细笔抖动的直接对手）；M0 是整数采样本身的噪声）。");
        Console.WriteLine("   · 直角列越小越好——M1 的角点保护是 0.5px 量级；弹簧模型（M3）会明显圆角。");
        Console.WriteLine("   · 圆弧偏离是「形状被改了多少」；抖动降得多但偏离暴涨 = 用形状换平滑，要警惕。");
        Console.WriteLine("   · 滞后列 = 活笔跟手程度：M0/M1 定义上为 0；Mean2 因活笔镜像（默认开）也 ≈0，");
        Console.WriteLine("     关镜像时的模型滞后（~6px）见下方「活笔笔尖镜像检查」。");
        Console.WriteLine("   · 折线度 = 圆弧上 6px 弧长内的最大方向变化：越大越「折」，越小越「圆」。");
        Console.WriteLine();

        // ---- 活笔增量一致性（2026-10-04 补：用户报 `--motion sliding` 活笔"变直线"）----
        // 活笔是"每帧喂几个新点"的增量路径；上面整张表都是一次性喂完的，照不出增量 bug。
        // 判据：分帧喂 与 一次喂 的输出点数/形状必须基本一致，且不许塌成直线。
        Console.WriteLine("  活笔增量检查（每帧 4 点喂 vs 一次喂完；圆弧半径 200、300 点）：");
        int incFail = 0;
        foreach (var mode in modes)
        {
            if (mode is StrokeMotionMode.Raw or StrokeMotionMode.Catmull)
            {
                Console.WriteLine($"  不适用  {mode,-10}  活笔按定义就是原始折线（M0/M1）");
                continue;
            }
            var raw = BuildArc(200f, 300);
            var full = Model(mode, raw, committed: true, hasPressure: false, out _);

            var stroke = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = 4f };
            stroke.HasPressure = false;
            stroke.RawWhileLive = true;
            for (int i = 0; i < raw.Count; i += 4)
            {
                int end = Math.Min(i + 4, raw.Count);
                for (int k = i; k < end; k++)
                    stroke.AddPoint(raw[k].X, raw[k].Y, 0.5f, (float)(k / InputHz * 1000.0));
                StrokeMotion.Build(stroke, mode);
            }
            stroke.RawWhileLive = false;
            StrokeMotion.Build(stroke, mode);

            var inc = new List<Vector2>(StrokeMotion.Count);
            for (int i = 0; i < StrokeMotion.Count; i++)
            {
                var p = StrokeMotion.At(i);
                inc.Add(new Vector2(p.X, p.Y));
            }
            // mean2：full 那张做过渲染层曲线化，inc 这里补同样的加工再比，否则是"曲线 vs 折线"的假失败。
            inc = PostCurve(mode, inc, null);

            float incDev = MaxDistanceToPolyline(inc, raw);
            float fullDev = MaxDistanceToPolyline(full, raw);
            bool ok = inc.Count >= full.Count * 0.8 && incDev <= MathF.Max(0.5f, fullDev + 0.5f);
            if (!ok) incFail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {mode,-10}  点数 {inc.Count,5}/{full.Count,-5}  "
                              + $"到原折线偏离 {incDev,6:F3}/{fullDev:F3}px");
        }

        // ---- 活笔笔尖镜像（A2）不变量：跟手归零 + 落笔不变形 + 成稿不变 --------------
        // 三条都量渲染层同一条取点路径（`Model` 已按渲染层口径把镜像接到曲线前）。
        Console.WriteLine("  活笔笔尖镜像检查（mean2；跟手 / 落笔交接 / 成稿不变）：");
        int tipFail = 0;
        {
            var raw = BuildArc(200f, 300);
            bool saved = StrokeMotion.Mean2TipOverlay;
            bool savedPred = StrokeMotion.TipPredict;
            try
            {
                StrokeMotion.Mean2TipOverlay = true;
                StrokeMotion.TipPredict = false;   // 镜像三条只量镜像：预测有自己的专项
                StrokeMotion.BumpVersion();
                var liveOn = Model(StrokeMotionMode.Mean2, raw, committed: false, hasPressure: false, out _);
                var doneOn = Model(StrokeMotionMode.Mean2, raw, committed: true, hasPressure: false, out _);

                StrokeMotion.Mean2TipOverlay = false;
                StrokeMotion.BumpVersion();
                var liveOff = Model(StrokeMotionMode.Mean2, raw, committed: false, hasPressure: false, out _);
                var doneOff = Model(StrokeMotionMode.Mean2, raw, committed: true, hasPressure: false, out _);

                float tipLag = liveOn.Count > 0 ? Vector2.Distance(liveOn[^1], raw[^1]) : float.NaN;
                float lagOff = liveOff.Count > 0 ? Vector2.Distance(liveOff[^1], raw[^1]) : float.NaN;
                float handoff = MaxSeqDistance(liveOn, doneOn);
                float finalDev = MaxSeqDistance(doneOn, doneOff);
                // 关镜像必须量得到原来的滞后（>1px），否则这条检查没在测东西。
                bool ok = float.IsFinite(tipLag) && tipLag <= 0.05f
                          && float.IsFinite(handoff) && handoff <= 1e-3f
                          && float.IsFinite(finalDev) && finalDev <= 1e-3f
                          && lagOff > 1f;
                if (!ok) tipFail++;
                Console.WriteLine($"  {(ok ? "通过" : "失败")}  开镜像：活笔末端滞后 {tipLag,6:F3}px，落笔首帧偏差 {handoff,7:F4}px；"
                                  + $"关镜像：滞后 {lagOff,6:F2}px；开/关成稿逐点偏差 {finalDev,7:F4}px");
            }
            finally
            {
                StrokeMotion.Mean2TipOverlay = saved;
                StrokeMotion.TipPredict = savedPred;
                StrokeMotion.BumpVersion();
            }
        }

        // ---- 活笔预测（L2）不变量：快写前探 + 成稿不变 + 慢写/拐弯不预测 -----------
        Console.WriteLine("  活笔预测检查（L2；快写前探 / 成稿不变 / 门控）：");
        int predFail = 0;
        {
            bool savedTip = StrokeMotion.TipPredict;
            bool savedMirror = StrokeMotion.Mean2TipOverlay;
            try
            {
                StrokeMotion.Mean2TipOverlay = true;
                StrokeMotion.TipPredict = true;
                StrokeMotion.BumpVersion();

                // (i) 快写前探：1.2px/ms 直线，渲染假设发生在最后采样后 8ms。
                // 预测（12ms）应把"末端到此刻"误差砍下来，且不超前过头（≤视界＋曲线余量 1px）。
                var fast = BuildFastLine(speedPxPerMs: 1.2f, count: 60);
                var dir = new Vector2(1f, 0f);
                var trueNow = fast[^1] + dir * (1.2f * 8f);
                var liveOn = Model(StrokeMotionMode.Mean2, fast, committed: false, hasPressure: false, out _);
                int nPred = StrokeMotion.PredictOverlay.Count;
                StrokeMotion.TipPredict = false;
                StrokeMotion.BumpVersion();
                var liveOff = Model(StrokeMotionMode.Mean2, fast, committed: false, hasPressure: false, out _);
                float baseErr = liveOff.Count > 0 ? Vector2.Distance(liveOff[^1], trueNow) : float.NaN;
                float tipErr = liveOn.Count > 0 ? Vector2.Distance(liveOn[^1], trueNow) : float.NaN;
                float overshoot = liveOn.Count > 0 ? Vector2.Dot(liveOn[^1] - trueNow, dir) : float.NaN;
                bool okProbe = nPred >= 1 && float.IsFinite(tipErr) && tipErr < baseErr
                               && float.IsFinite(overshoot) && overshoot <= 1.2f * StrokeMotion.TipPredictMs + 1f;
                if (!okProbe) predFail++;

                // (ii) 成稿不变：预测开/关，提交后逐点相同。
                StrokeMotion.TipPredict = true;
                StrokeMotion.BumpVersion();
                var doneOn = Model(StrokeMotionMode.Mean2, fast, committed: true, hasPressure: false, out _);
                StrokeMotion.TipPredict = false;
                StrokeMotion.BumpVersion();
                var doneOff = Model(StrokeMotionMode.Mean2, fast, committed: true, hasPressure: false, out _);
                float finalDev = MaxSeqDistance(doneOn, doneOff);
                bool okFinal = float.IsFinite(finalDev) && finalDev <= 1e-3f;
                if (!okFinal) predFail++;

                // (iii) 门控：慢写（0.1px/ms）与尾段急转（90°钩）都不许出预测点。
                StrokeMotion.TipPredict = true;
                StrokeMotion.BumpVersion();
                Model(StrokeMotionMode.Mean2, BuildFastLine(speedPxPerMs: 0.1f, count: 60),
                      committed: false, hasPressure: false, out _);
                int nSlow = StrokeMotion.PredictOverlay.Count;
                Model(StrokeMotionMode.Mean2, BuildHookEnd(), committed: false, hasPressure: false, out _);
                int nHook = StrokeMotion.PredictOverlay.Count;
                bool okGate = nSlow == 0 && nHook == 0;
                if (!okGate) predFail++;

                // (iv) 时标粗糙（鼠标合并点/同批同刻 → 合成时间）：不许"突突突"往外跳。
                var sameT = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = 4f };
                for (int i = 0; i < 20; i++) sameT.AddPoint(100f + i * 9f, 200f, 0.5f, 1000f);
                sameT.HasPressure = false;
                sameT.RawWhileLive = true;
                StrokeMotion.TipPredict = true;
                StrokeMotion.BumpVersion();
                StrokeMotion.Build(sameT, StrokeMotionMode.Mean2);
                int nSame = StrokeMotion.PredictOverlay.Count;
                bool okSame = nSame == 0;
                if (!okSame) predFail++;

                Console.WriteLine($"  {(okProbe && okFinal && okGate && okSame ? "通过" : "失败")}  "
                                  + $"快写前探：预测 {nPred} 点，误差 {baseErr,5:F2}→{tipErr,5:F2}px，"
                                  + $"超前 {overshoot,5:F2}px；成稿偏差 {finalDev,7:F4}px；"
                                  + $"慢写/急转/同刻预测点 {nSlow}/{nHook}/{nSame}");
            }
            finally
            {
                StrokeMotion.Mean2TipOverlay = savedMirror;
                StrokeMotion.TipPredict = savedTip;
                StrokeMotion.BumpVersion();
            }
        }
        Console.WriteLine();

        // [停用 2026-10-05] sliding 时间戳变体专项（sliding 已停用；代码保留，见 已停用-渲染实验.md）。
        /*
        // 时间戳变体（真实设备常见）：**同批同刻**（Windows Ink 关 → PT_MOUSE 合并点）
        // 和**完全无时标**（QPC 不可用）。两种都会走 EnsureTimes 的"可用/合成"分支，
        // 必须不崩、不塌成直线。
        foreach (var (variantName, timeAt) in new (string, Func<int, float>)[]
                 {
                     ("同批同刻", k => 1000f + (k / 4) * 8f),
                     ("全部无时标", _ => 0f),
                 })
        {
            var raw = BuildArc(200f, 300);
            var stroke = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = 4f };
            stroke.HasPressure = false;
            stroke.RawWhileLive = true;
            for (int i = 0; i < raw.Count; i += 4)
            {
                int end = Math.Min(i + 4, raw.Count);
                for (int k = i; k < end; k++)
                    stroke.AddPoint(raw[k].X, raw[k].Y, 0.5f, timeAt(k));
                StrokeMotion.Build(stroke, StrokeMotionMode.Sliding);
            }
            stroke.RawWhileLive = false;
            StrokeMotion.Build(stroke, StrokeMotionMode.Sliding);

            var inc = new List<Vector2>(StrokeMotion.Count);
            for (int i = 0; i < StrokeMotion.Count; i++)
            {
                var p = StrokeMotion.At(i);
                inc.Add(new Vector2(p.X, p.Y));
            }
            float dev = MaxDistanceToPolyline(inc, raw);
            bool ok = StrokeMotion.Count >= 100 && dev <= 5f;
            if (!ok) incFail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  sliding/{variantName,-8}  点数 {StrokeMotion.Count,5}  "
                              + $"到原折线偏离 {dev,6:F3}px");
            if (!ok)
            {
                // 诊断：把前 8 个输出点打出来，看位置丢在哪一段（临时保留，帮助复现）。
                Console.Write("        前 8 点：");
                for (int i = 0; i < Math.Min(8, StrokeMotion.Count); i++)
                {
                    var p = StrokeMotion.At(i);
                    Console.Write($"({p.X:F0},{p.Y:F0}) ");
                }
                Console.WriteLine($" … 末点 ({StrokeMotion.At(StrokeMotion.Count - 1).X:F0},"
                                  + $"{StrokeMotion.At(StrokeMotion.Count - 1).Y:F0})，"
                                  + $"原始首末 ({raw[0].X:F0},{raw[0].Y:F0})/({raw[^1].X:F0},{raw[^1].Y:F0})");
            }
        }
        */
        Console.WriteLine();

        // [停用 2026-10-05] 预测笔尖专项（老预测系统停用；代码保留，见 已停用-渲染实验.md）。
        /*
        // ---- 预测笔尖（`--predicttip`）效果量化 ---------------------------------
        // 合成：130Hz、1.2px/ms 的快写直线；假设"渲染发生在最后一个采样后 8ms"（一帧）。
        // 端到端误差 = |输出末点 − 此刻真实位置|。不接预测时，误差 = 距离窗滞后（≈窗一半）
        // ＋ 这 8ms 的位移；接上预测笔尖后应显著变小，而且不许补过头。
        Console.WriteLine("  预测笔尖自检（快写直线 1.2px/ms，渲染比最后采样晚 8ms）：");
        int predFail = 0;
        {
            const float spd = 1.2f;                  // px/ms
            const double sampleMs = 1000.0 / 130.0;
            const double nowMs = 8.0;
            const int nPts = 40;
            var raw = new List<Vector2>(nPts);
            for (int i = 0; i < nPts; i++)
                raw.Add(new Vector2(100f + spd * (float)(i * sampleMs), 200f));
            var trueNow = new Vector2(100f + spd * (float)((nPts - 1) * sampleMs + nowMs), 200f);

            bool savedFlag = StrokeMotion.PredictTip;
            try
            {
                StrokeMotion.PredictTip = false;
                StrokeMotion.BumpVersion();
                var baseOut = LiveMean2(raw, null);
                float baseErr = Vector2.Distance(baseOut[^1], trueNow);

                // 用真实预测器（和引擎同一条路）算出预测链。
                var pred = new InkPredictor();
                for (int i = 0; i < nPts; i++) pred.Add(raw[i].X, raw[i].Y, 1000.0 + i * sampleMs);
                var buf = new PredictedPoint[8];
                int np = pred.Predict(buf);
                var chain = new List<Vector2>(np);
                for (int i = 0; i < np; i++) chain.Add(new Vector2(buf[i].X, buf[i].Y));

                StrokeMotion.PredictTip = true;
                StrokeMotion.BumpVersion();
                var tipOut = LiveMean2(raw, chain);
                float tipErr = Vector2.Distance(tipOut[^1], trueNow);
                float overshoot = Vector2.Dot(tipOut[^1] - trueNow, new Vector2(1f, 0f));

                // 判据：预测至少 1 点、误差砍半以上、沿运动方向不超前（超前 > 半帧位移算补过头）。
                bool ok = np >= 1 && tipErr < baseErr * 0.5f + 0.5f
                          && overshoot < spd * nowMs * 0.5f;
                if (!ok) predFail++;
                Console.WriteLine($"  {(ok ? "通过" : "失败")}  不接预测：端到此刻误差 {baseErr,6:F2}px；"
                                  + $"接预测：{tipErr,6:F2}px（预测 {np} 点，超出此刻 {overshoot,5:F2}px）");
            }
            finally
            {
                StrokeMotion.PredictTip = savedFlag;
                StrokeMotion.BumpVersion();
            }
        }
        */
        Console.WriteLine();

        // [停用 2026-10-05] 稀疏快圆比对（WPF 拟合已停用；代码保留，见 已停用-渲染实验.md）。
        /*
        // ---- 稀疏快圆检查（模拟 Windows Ink 关）----------------------------------
        // 用户 2026-10-04 报：Windows Ink 关、快速画圆，mean2 仍见折线。
        // 语料：半径 60px、每 8px 一个采样、坐标取整（PT_MOUSE 的"稀疏 + 整数像素"），两圈。
        // 对比 mean2 的三个曲线层：过点曲线（现状）/ 逼近拟合 0.5px / 逼近拟合 1.2px。
        Console.WriteLine("  稀疏快圆检查（r=60px、每 8px 采样、整数像素；模拟 Windows Ink 关的快圆）：");
        int sparseFail = 0;
        {
            var circle = BuildCircle(60f, 8f, 2);
            var savedCurve = StrokeMotion.Mean2CurveMode;
            float savedTol = WpfInkFit.TolerancePx;
            var cases = new (string name, StrokeMotion.Mean2CurveKind kind, float tol)[]
            {
                ("过点曲线（现状）", StrokeMotion.Mean2CurveKind.Smooth, savedTol),
                ("逼近拟合 0.5px", StrokeMotion.Mean2CurveKind.Fit, 0.5f),
                ("逼近拟合 1.2px", StrokeMotion.Mean2CurveKind.Fit, 1.2f),
            };
            try
            {
                foreach (var c in cases)
                {
                    StrokeMotion.Mean2CurveMode = c.kind;
                    WpfInkFit.TolerancePx = c.tol;
                    StrokeMotion.BumpVersion();
                    var outp = Model(StrokeMotionMode.Mean2, circle, committed: true, hasPressure: false, out _);
                    float f6 = MaxTurnOverWindow(outp, 6f);
                    float f3 = MaxTurnOverWindow(outp, 3f);
                    float radial = MaxRadialError(outp, 60f);
                    bool finite = float.IsFinite(f6) && float.IsFinite(f3) && float.IsFinite(radial);
                    if (!finite) sparseFail++;
                    Console.WriteLine($"  {(finite ? "通过" : "失败")}  {c.name,-16}  折线度 6px {f6,5:F2}° / 3px {f3,5:F2}°  "
                                      + $"到理想圆最大偏差 {radial,5:F2}px");
                }
            }
            finally
            {
                StrokeMotion.Mean2CurveMode = savedCurve;
                WpfInkFit.TolerancePx = savedTol;
                StrokeMotion.BumpVersion();
            }
        }
        */
        Console.WriteLine();

        // 硬保证：保留的模式都能给出 >=2 个有限点，且没抛异常。
        hardFail += incFail + tipFail + predFail;
        Console.WriteLine($"  硬检查：{modes.Length} 个模式全部产出有限点——{(hardFail == 0 ? "通过" : $"失败 {hardFail} 个")}");
        Console.WriteLine();
        return hardFail == 0 ? 0 : 1;
    }

    // ---- 跑一个模式（fresh stroke，互不污染缓存）----------------------------

    private static List<Vector2> Model(StrokeMotionMode mode, List<Vector2> pts, bool committed,
                                       bool hasPressure, out List<float> pressures)
    {
        pressures = new List<float>();
        // M0/M1 不用模型器：M0 原样；M1 过点曲线（活笔按定义也是原始折线）。
        if (mode == StrokeMotionMode.Raw || (mode == StrokeMotionMode.Catmull && !committed))
            return new List<Vector2>(pts);
        if (mode == StrokeMotionMode.Catmull)
            return RunCatmull(pts);

        var stroke = new Stroke
        {
            Tool = Tool.Pen,
            Kind = StrokeKind.Freehand,
            Width = 4f,
        };
        for (int i = 0; i < pts.Count; i++)
        {
            float t = (float)(i / InputHz * 1000.0);
            stroke.AddPoint(pts[i].X, pts[i].Y, hasPressure ? i / (float)Math.Max(1, pts.Count - 1) : 0.5f, t);
        }
        stroke.HasPressure = hasPressure;
        stroke.RawWhileLive = !committed;

        if (!StrokeMotion.Build(stroke, mode)) return new List<Vector2>(pts);

        var result = new List<Vector2>(StrokeMotion.LiveDrawnCount);
        for (int i = 0; i < StrokeMotion.LiveDrawnCount; i++)
        {
            var p = StrokeMotion.LiveDrawnAt(i);
            result.Add(new Vector2(p.X, p.Y));
            pressures.Add(p.Z);
        }
        // 口径 = 渲染层（`LiveDrawnCount/LiveDrawnAt`：模型输出 ＋ 镜像尾 ＋ 预测段）；
        // 量到的"滞后"才是用户眼睛看到的。

        // mean2：渲染层会在建模输出之上再过一遍曲线；探针这里做同样的加工，
        // 否则"折线度"量的是加工前的原始输出，表上会看不出曲线化的收益。
        result = PostCurve(mode, result, pressures);
        return result;
    }

    /// <summary>mean2 的曲线层固定为过点曲线（与渲染层一致；探针度量用）。</summary>
    private static List<Vector2> PostCurve(StrokeMotionMode mode, List<Vector2> pts, List<float> pressures)
        => mode == StrokeMotionMode.Mean2 ? RunCatmull(pts) : pts;

    private static List<Vector2> RunCatmull(List<Vector2> raw)
    {
        StrokeSmoothing.Begin();
        foreach (var p in raw) StrokeSmoothing.Add(p.X, p.Y, 0.5f);
        int n = StrokeSmoothing.Finish();
        if (n <= 0) return new List<Vector2>(raw);
        var outp = new List<Vector2>(n * 8 + 1);
        for (int k = 0; k < n; k++)
        {
            var s = StrokeSmoothing.Out[k];
            for (int i = 0; i <= 8; i++)
            {
                float t = i / 8f, u = 1f - t;
                Vector2 p = u * u * u * s.P0 + 3f * u * u * t * s.C1 + 3f * u * t * t * s.C2 + t * t * t * s.P1;
                outp.Add(p);
            }
        }
        return outp;
    }

    // ---- 语料 -----------------------------------------------------------------

    private static List<Vector2> BuildNoisyLine(float k, float b, int count, float noisePx, int seed)
    {
        var rnd = new Random(seed);
        var pts = new List<Vector2>(count);
        float spacing = SpeedPxPerSec / (float)InputHz;
        for (int i = 0; i < count; i++)
        {
            float x = i * spacing;
            float noise = (float)(rnd.NextDouble() * 2 - 1) * noisePx;
            pts.Add(new Vector2(MathF.Round(x), MathF.Round(b + k * x + noise)));
        }
        return pts;
    }

    private static List<Vector2> BuildCorner(float armPx)
    {
        const float step = 20f;
        var pts = new List<Vector2>();
        for (float x = 0; x <= armPx; x += step) pts.Add(new Vector2(MathF.Round(x), 0));
        for (float y = step; y <= armPx; y += step) pts.Add(new Vector2(armPx, MathF.Round(y)));
        return pts;
    }

    /// <summary>等速直线（预测专项语料；时标按 <see cref="InputHz"/> 配，速度自定）。</summary>
    private static List<Vector2> BuildFastLine(float speedPxPerMs, int count)
    {
        var pts = new List<Vector2>(count);
        float step = speedPxPerMs * (float)(1000.0 / InputHz);
        for (int i = 0; i < count; i++) pts.Add(new Vector2(100f + i * step, 200f));
        return pts;
    }

    /// <summary>直行后尾段 90° 急转（预测门2语料：最后一刻在拐弯，不许预测）。</summary>
    private static List<Vector2> BuildHookEnd()
    {
        var pts = new List<Vector2>();
        for (int i = 0; i < 20; i++) pts.Add(new Vector2(100f + i * 8f, 200f));
        pts.Add(new Vector2(100f + 20 * 8f, 208f));
        pts.Add(new Vector2(100f + 20 * 8f, 216f));
        return pts;
    }

    private static List<Vector2> BuildArc(float radius, int points)
    {
        var pts = new List<Vector2>(points);
        for (int i = 0; i < points; i++)
        {
            float a = MathF.PI * 1.5f * i / (points - 1);
            pts.Add(new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius));
        }
        return pts;
    }

    // [删除 2026-10-05] `BuildCircle`（稀疏快圆语料）：随拟合专项检查移除。

    // ---- 量法（和 SmoothProbe/InkModelProbe 同口径）----------------------------

    private static float LateralRms(List<Vector2> pts, float k, float b)
    {
        if (pts.Count == 0) return float.MaxValue;
        double sum = 0;
        float norm = MathF.Sqrt(1 + k * k);
        foreach (var p in pts)
        {
            float d = (p.Y - (b + k * p.X)) / norm;
            sum += d * d;
        }
        return (float)Math.Sqrt(sum / pts.Count);
    }

    /// <summary>
    /// 去趋势后的横向粗糙度：残差先做宽度 20 的居中移动平均，再算"残差 − 均值"的 RMS。
    /// 它对应眼睛看到的"抖动/波浪"；整体滞后（常速时的固定偏移）被移动平均吃掉了。
    /// </summary>
    private static float LateralRoughness(List<Vector2> pts, float k, float b)
    {
        if (pts.Count == 0) return float.MaxValue;
        float norm = MathF.Sqrt(1 + k * k);
        var d = new float[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            d[i] = (pts[i].Y - (b + k * pts[i].X)) / norm;

        const int half = 10;
        double sum = 0;
        for (int i = 0; i < d.Length; i++)
        {
            float avg = 0;
            int count = 0;
            for (int j = Math.Max(0, i - half); j <= Math.Min(d.Length - 1, i + half); j++)
            {
                avg += d[j];
                count++;
            }
            avg /= count;
            float r = d[i] - avg;
            sum += r * r;
        }
        return (float)Math.Sqrt(sum / d.Length);
    }

    private static float MaxDistanceToPolyline(List<Vector2> pts, List<Vector2> poly)
    {
        float worst = 0f;
        foreach (var p in pts) worst = MathF.Max(worst, DistanceToPolyline(p, poly));
        return worst;
    }

    /// <summary>两条点串逐点最大偏差（点数不同 = 无穷大，代表"不同形"）。</summary>
    private static float MaxSeqDistance(List<Vector2> a, List<Vector2> b)
    {
        if (a.Count != b.Count || a.Count == 0) return float.MaxValue;
        float worst = 0f;
        for (int i = 0; i < a.Count; i++)
            worst = MathF.Max(worst, Vector2.Distance(a[i], b[i]));
        return worst;
    }

    // [删除 2026-10-05] `MaxRadialError`（稀疏快圆专项度量）：随拟合专项检查移除。

    private static float DistanceToPolyline(Vector2 p, List<Vector2> poly)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < poly.Count; i++)
            best = MathF.Min(best, DistanceToSegment(p, poly[i], poly[i + 1]));
        return best;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq <= 1e-9f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// 一段点串里，任意 <paramref name="windowPx"/> 弧长窗口两端的方向变化（°），取最大。
    /// 先按弧长重采样成 0.5px 再量（和 SmoothProbe 同一口径），否则稀采样会量出假数。
    /// </summary>
    private static float MaxTurnOverWindow(List<Vector2> pts, float windowPx)
    {
        if (pts.Count < 3) return 0f;
        var dense = Resample(pts, 0.5f);
        float worst = 0f;
        for (int i = 0; i + 2 < dense.Count; i++)
        {
            float acc = 0f;
            int j = i + 1;
            while (j < dense.Count && acc < windowPx)
            {
                acc += Vector2.Distance(dense[j - 1], dense[j]);
                j++;
            }
            if (acc < windowPx * 0.8f) break;
            Vector2 d0 = dense[i + 1] - dense[i];
            Vector2 d1 = dense[j - 1] - dense[j - 2];
            if (d0.LengthSquared() < 1e-9f || d1.LengthSquared() < 1e-9f) continue;
            float cos = Math.Clamp(Vector2.Dot(Vector2.Normalize(d0), Vector2.Normalize(d1)), -1f, 1f);
            worst = MathF.Max(worst, MathF.Acos(cos) * (180f / MathF.PI));
        }
        return worst;
    }

    private static List<Vector2> Resample(List<Vector2> pts, float stepPx)
    {
        var outp = new List<Vector2> { pts[0] };
        float carry = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i];
            float len = Vector2.Distance(a, b);
            if (len < 1e-6f) continue;
            Vector2 dir = (b - a) / len;
            float t = stepPx - carry;
            while (t <= len)
            {
                outp.Add(a + dir * t);
                t += stepPx;
            }
            carry = len - (t - stepPx);
        }
        if (outp.Count == 0 || Vector2.Distance(outp[^1], pts[^1]) > 1e-6f) outp.Add(pts[^1]);
        return outp;
    }
}
