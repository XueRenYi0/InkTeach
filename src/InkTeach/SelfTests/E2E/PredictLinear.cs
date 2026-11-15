using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 线性预测**纯算法**自检（`--predlinetest`，2026-10-15 加）：不需要真笔 / 屏幕 / 画东西，
    /// 把 `InkPredictorLinear`（唯一预测器，交付件口径）的硬保证逐条钉住——
    /// 弧长截断 ≤6px（跨速度/跨方向都成立）、地平线 21.3ms、方向门控、断笔重置、出厂参数。
    ///
    /// 分工：本测是"算法钉子"；尾的**屏幕级**行为（突突跳/急转残留）看 `--tailjumptest`。
    /// </summary>
    private void PredictLinearTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 线性预测自检（纯算法）===");
        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {what,-44} {detail}");
            if (ok) pass++; else fail++;
        }

        var buf = new PredictedPoint[16];

        // ---- ① 直线匀速 1px/ms：裸外推 21.3px，被弧长截断钉在 6px ----
        {
            var p = new InkPredictorLinear();
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(10, 0, 10);
            int n = p.Predict(buf);
            float tipX = n > 0 ? buf[n - 1].X - 10f : float.NaN;
            Check("直线匀速：给点且在前方", n >= 1 && tipX > 0f, $"点数 {n}，位移 {tipX:F2}px");
            Check("弧长硬保证：位移 ≤6px", n >= 1 && tipX <= 6.01f, $"{tipX:F2}px（上限 6）");
            Check("时间单调递增且不超地平线",
                  n >= 2 && buf[1].TimeMs > buf[0].TimeMs && buf[n - 1].TimeMs <= 10 + 21.3 + 0.01,
                  n >= 2 ? $"{buf[0].TimeMs:F1} → {buf[n - 1].TimeMs:F1} ms（t0=10）" : "点不够");
        }

        // ---- ② 慢速 0.1px/ms：外推只有 2.13px、未触顶 → 按真实距离给，末点落在地平线 ----
        {
            var p = new InkPredictorLinear();
            p.Add(0, 0, 0); p.Add(1, 0, 10); p.Add(2, 0, 20);
            int n = p.Predict(buf);
            float tipX = n > 0 ? buf[n - 1].X - 2f : float.NaN;
            Check("慢速：位移 = v·21.3 ≈ 2.13px", n >= 1 && MathF.Abs(tipX - 2.13f) < 0.05f, $"{tipX:F2}px");
            Check("慢速：末点时间落在地平线（20+21.3）",
                  n >= 1 && Math.Abs(buf[n - 1].TimeMs - 41.3) < 0.01, $"{buf[n - 1].TimeMs:F2}ms");
        }

        // ---- ③ 极高速 10px/ms：位移仍 ≤6px（硬保证跨速度成立）----
        {
            var p = new InkPredictorLinear();
            p.Add(0, 0, 0); p.Add(50, 0, 5); p.Add(100, 0, 10);
            int n = p.Predict(buf);
            float tipX = n > 0 ? buf[n - 1].X - 100f : float.NaN;
            Check("极高速：位移仍 ≤6px（裸外推 213px）", n >= 1 && tipX <= 6.01f, $"{tipX:F2}px");
        }

        // ---- ④ 急转 90°：预测沿**新方向**（不沿旧方向甩），弧长仍 ≤6px ----
        {
            var p = new InkPredictorLinear();
            p.Add(0, 0, 0); p.Add(12, 0, 4); p.Add(24, 0, 8); p.Add(36, 0, 12);  // 向右 3px/ms
            p.Add(36, 12, 16);                                                    // 急转 90°：向下
            int n = p.Predict(buf);
            float dx = n > 0 ? buf[n - 1].X - 36f : float.NaN;
            float dy = n > 0 ? buf[n - 1].Y - 12f : float.NaN;
            float len = n > 0 ? MathF.Sqrt(dx * dx + dy * dy) : float.NaN;
            Check("急转 90°：沿新方向（向下）走", n >= 1 && dy > 0.5f && MathF.Abs(dx) < 2f, $"Δ=({dx:F2},{dy:F2})");
            Check("急转 90°：弧长仍 ≤6px", n >= 1 && len <= 6.01f, $"{len:F2}px");
        }

        // ---- ⑤ 方向门控（DirFloor=0）：连续两次急转后 DirEma 超线 → 本帧直接不预测 ----
        {
            var p = new InkPredictorLinear();
            p.Add(0, 0, 0);
            float x = 0, y = 0, t = 0, ang = 0;
            for (int seg = 1; seg <= 3; seg++)
            {
                if (seg > 1) ang += 80f * MathF.PI / 180f;   // 每段比上一段转 80°
                x += 20f * MathF.Cos(ang); y += 20f * MathF.Sin(ang); t += 4;
                p.Add(x, y, t);
            }
            int n = p.Predict(buf);
            Check("方向门控：连续急转后本帧不预测", n == 0, $"点数 {n}，DirEma = {p.DirEma:F2}");
        }

        // ---- ⑥ 断笔：相邻间隔 >40ms 重置轨迹（本帧不预测）----
        {
            var p = new InkPredictorLinear();
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(10, 0, 10);
            p.Add(10, 0, 60);                                    // 间隔 50ms > MaxGapMs(40) → 重置
            int n = p.Predict(buf);
            Check("断笔：间隔 >40ms 重置（本帧不预测）", n == 0 && p.Count == 1, $"点数 {n}，Count = {p.Count}");
        }

        // ---- ⑦ 出厂参数钉子（交付件口径；改它们要连这里一起解释清楚）----
        {
            var d = new InkPredictorLinear();
            Check("出厂参数：地平线 21.3（引擎按刷新率覆盖）/ 弧长顶 6 / 半径顶 12",
                  Math.Abs(d.HorizonMs - 21.3) < 1e-9 && Math.Abs(d.ArcCap - 6.0) < 1e-9
                  && Math.Abs(d.MaxDistance - 12.0) < 1e-9,
                  $"{d.HorizonMs} / {d.ArcCap} / {d.MaxDistance}（21.3 = 未缩放参考值）");
            Check("出厂默认一阶（UseAcceleration = false）", !d.UseAcceleration, "Chromium 二阶默认关（噪声大）");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 线性预测正确（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }
}
