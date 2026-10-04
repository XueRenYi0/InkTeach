using System.Numerics;
using Vortice.Direct2D1;

namespace InkEngine;

/// <summary>
/// `--alloctest`：**写一笔的托管分配分段计量**（纯算法＋离线几何，不建窗口、不碰输入）。
///
/// 背景：默认模式随手一笔（30 点）量出 ~1.3MB，明显不对；先按阶段把账算清再动手。
/// 三段：
///   · A 运动模型增量：120 次 `StrokeMotion.Build`（模拟活笔每来一个点建一次模）；
///   · B 整笔几何重建：120 次 `BuildGeometry`＋逐次释放（模拟活笔每帧重画；
///     走真 D2D 工厂，只建 factory、不建设备窗口）；
///   · C 提交：50 次 `InkDocument.AddStroke`（含撤销记录）。
/// 每段跑两遍、报第二遍（去掉首次分配/JIT）；只量托管字节
///（`GC.GetAllocatedBytesForCurrentThread` 差额），与线上"分配 KB"是同一口径。
/// </summary>
internal static class AllocProbe
{
    public static int Run()
    {
        Console.WriteLine("=== 写一笔的托管分配分段计量（诊断探针，只出数、不断言）===");
        Console.WriteLine();

        // 热身：JIT 与静态缓冲（StrokeSmoothing/StrokeMotion 的复用数组）先落袋。
        MeasureA();
        MeasureB();
        MeasureC();
        Console.WriteLine("  （上面是热身。下面是正式数。）");
        Console.WriteLine();

        double aKb = MeasureA();
        double bKb = MeasureB();
        double cKb = MeasureC();

        Console.WriteLine("  阶段                                   总分配      单次");
        Console.WriteLine("  --------------------------------  ------------  ----------");
        Console.WriteLine($"  A 运动模型增量（120 次 Build）       {aKb,10:F1} KB  {aKb / 120 * 1024,8:F0} B/次");
        Console.WriteLine($"  B 整笔几何重建（120 次 BuildGeometry） {bKb,9:F1} KB  {bKb / 120 * 1024,8:F0} B/次");
        Console.WriteLine($"  C 提交（50 次 AddStroke）            {cKb,10:F1} KB  {cKb / 50 * 1024,8:F0} B/次");
        Console.WriteLine();
        Console.WriteLine("  怎么读：线上 30 点/百来条消息的一笔 ≈ B×消息数 ＋ C×1。"
                          + "哪一段单次上 KB 级，就是凶手。");
        Console.WriteLine();
        return 0;
    }

    private static double MeasureA()
    {
        var stroke = NewLiveStroke();
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++)
        {
            float a = MathF.PI * 1.5f * i / 119f;
            stroke.AddPoint(MathF.Cos(a) * 200f, MathF.Sin(a) * 200f, 0.5f, i * 8f);
            if (stroke.Points.Count >= 2)
                StrokeMotion.Build(stroke, StrokeMotionMode.Mean2);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - b0) / 1024.0;
    }

    private static double MeasureB()
    {
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(
            FactoryType.SingleThreaded, DebugLevel.None);
        var stroke = NewLiveStroke();
        for (int i = 0; i < 33; i++)
            stroke.AddPoint(100f + i * 8f, 200f + (i % 2), 0.5f, i * 8f);
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++)
        {
            stroke.AddPoint(100f + (33 + i) * 8f, 200f + (i % 2), 0.5f, (33 + i) * 8f);
            stroke.BuildGeometry(factory);
        }
        double kb = (GC.GetAllocatedBytesForCurrentThread() - b0) / 1024.0;
        stroke.Release();
        return kb;
    }

    private static double MeasureC()
    {
        var doc = new InkDocument();
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            var stroke = NewLiveStroke();
            for (int k = 0; k < 33; k++)
                stroke.AddPoint(100f + k * 8f, 200f, 0.5f, k * 8f);
            stroke.RawWhileLive = false;
            doc.AddStroke(stroke);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - b0) / 1024.0;
    }

    private static Stroke NewLiveStroke()
    {
        var stroke = new Stroke
        {
            Tool = Tool.Pen,
            Kind = StrokeKind.Freehand,
            Width = 4f,
        };
        stroke.HasPressure = false;
        stroke.RawWhileLive = true;
        return stroke;
    }
}
