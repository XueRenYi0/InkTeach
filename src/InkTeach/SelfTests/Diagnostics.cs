// 本文件由 App.cs 拆出（2026-10-07）：Diagnostics 这一组。
// **纯搬家，逻辑一字未改** —— 靠 partial class 共享 App 的私有成员。
// 拆开的目的：产品代码与自检代码互不干扰，人和 AI 读代码时不必互相穿插。

using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{

    /// <summary>
    /// 选中与变换的性能自检：走**真实的拖动路径**（SetTransformLive → 内容层
    /// 区域修补），量每步要花多久。
    ///
    /// 两个关键设计：
    ///   ① **用窗口里的计数器，不用墙钟。** 墙钟会把 Present 的垂直同步等待算进去
    ///      （60Hz 屏幕上每帧 16ms），量到的是显示器刷新率，不是我们的开销。
    ///   ② **看最长的一步，不看平均。** 卡顿感来自长帧，平均值会把它们抹平。
    /// </summary>
    /// <summary>
    /// 板书长跑：模拟老师连续写字（默认 20 分钟、每秒 6 笔）。
    ///
    /// 和其它压测的区别：**按真实节奏生成真实形状的笔画**——一行一行往下写，
    /// 写满一屏就往下滚，每写一笔渲染一帧。输出是一条"笔画数 → 每帧代价"
    /// 的曲线：卡不卡、从多少笔开始卡，看曲线比看平均值有用。
    ///
    /// 参照点：2 屏写满约 400 笔（本机 2880x1800、每行 12 笔、每屏 16 行）；
    /// 20 分钟 × 6 笔/秒 = 7200 笔，大约 18 屏。
    /// </summary>
    private void WriteTest(double minutes, double strokesPerSecond)
    {
        int total = (int)Math.Round(minutes * 60 * strokesPerSecond);
        Console.WriteLine();
        Console.WriteLine($"=== 板书长跑（{minutes:F0} 分钟 × {strokesPerSecond:F0} 笔/秒 = {total} 笔）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}，逻辑屏 {_virtualW / DpiScale:F0}x{_virtualH / DpiScale:F0}");
        Console.WriteLine();
        Console.WriteLine("    笔画 |     点数 |  提交MB |  工作集 |    显存(已用/预算) |  记录均 | 记录最差 |  上屏均 | 分块光栅 | 几何存活 | 已释放");
        Console.WriteLine("  -------|----------|---------|---------|--------------------|---------|----------|---------|----------|----------|--------");

        Doc.Clear();
        Doc.ClearHistory();

        // 一行一行往下写。**坐标一律用画布坐标**：板书是往下长的，
        // 写满一屏把视图跟着往下带——这才是老师的真实动作。
        // （第一版这里写的是屏幕坐标，结果写出去的字其实在视野外，
        //   量出来的内存和耗时全都偏小，属于"测了个假的"。）
        float dpi = DpiScale;
        float marginX = _virtualX + 160f * dpi;
        float rightX = _virtualX + _virtualW - 160f * dpi;
        float lineH = 120f * dpi;
        if (DenseWrite)
        {
            // 密集模式：所有笔画都写在一小块区域里（对应"同一页墨迹很多"）。
            // 这样每块（tile）里会挤进成百上千条笔画，正好量出"块内条数 → 每帧代价"。
            marginX = _virtualX + _virtualW * 0.25f;
            rightX = marginX + 900f * dpi;
            lineH = 14f * dpi;
            Console.WriteLine($"  密集模式：所有笔画挤在 {rightX - marginX:F0}×{_virtualH * 0.6f:F0} 像素内，行距 {lineH:F0}");
        }
        float x = marginX;
        float y = _virtualY + 260f * dpi;
        var rnd = new Random(20260913);

        double recSum = 0, presSum = 0, rebSum = 0, recWorst = 0;
        int n = 0, sinceReport = 0;
        var clock = Stopwatch.StartNew();

        for (int i = 0; i < total; i++)
        {
            // 一笔"字"：一小段起伏的折线，40~140 个点（真实手写的量级）
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = PenColor, Width = 3.2f * dpi,
            };
            int pts = 40 + rnd.Next(100);
            float px = x, py = y + (float)(rnd.NextDouble() - 0.5) * 24f;
            float ang = 0f;
            for (int k = 0; k < pts; k++)
            {
                ang += (float)((rnd.NextDouble() - 0.5) * 0.9);
                px += 2.2f * dpi * MathF.Cos(ang) + 1.6f * dpi;
                py += 2.2f * dpi * MathF.Sin(ang);
                s.AddPoint(px, py, 0.5f + (float)rnd.NextDouble() * 0.5f, NowMs);
            }
            Doc.AddStroke(s);
            n++;
            sinceReport++;

            x = px + 14f * dpi;
            if (x > rightX)
            {
                x = marginX;
                y += lineH;
                // 视图跟着笔尖走：让正在写的这行固定在屏幕下方 72% 处。
                float wantTop = y - _virtualH * 0.72f;
                ViewOffsetY = _virtualY - wantTop;
                ClampViewOffset();
            }

            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();

            double rec = _windows[0].LastRecordMs;
            recSum += rec; presSum += _windows[0].LastPresentMs; rebSum += _windows[0].LastRebuildMs;
            if (rec > recWorst) recWorst = rec;

            if (sinceReport >= 250 || i == total - 1)
            {
                sinceReport = 0;
                Console.WriteLine($"  {n,6} | {Doc.TotalPoints,8} | {Mem.Priv(),7:F0} | {Mem.Ws(),7:F0} |"
                                + $" {GpuMb(),18} | {recSum / n,7:F2} | {recWorst,8:F2} | {presSum / n,7:F2} | {rebSum / n,8:F2}"
                                + $" | {Stroke.LiveGeometries,8} | {Stroke.ReleasedGeometries,6}"
                                + $" | 块 {_windows[0].LastTileCount}/{_windows[0].LastTileVisible}"
                                + $"(预算 {_windows[0].LastTileBudget})");
            }
        }

        clock.Stop();
        Console.WriteLine();
        Console.WriteLine($"  合计 {n} 笔 / {Doc.TotalPoints} 点，墙钟 {clock.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine($"  记录耗时 均值 {recSum / n:F2} ms，最差 {recWorst:F2} ms（超 16.7ms 就是掉帧）");
        Console.WriteLine($"  提交 {Mem.Priv():F0} MB，工作集 {Mem.Ws():F0} MB，显存 {GpuMb()}");

        // 收尾再量两件真实操作：整屏重画、滚一屏
        Doc.Dirty.MarkFull();
        NowMs = _clock.Elapsed.TotalMilliseconds;
        RenderAll();
        Console.WriteLine($"  整屏重画（MarkFull）: 记录 {_windows[0].LastRecordMs:F1} ms，分块光栅 {_windows[0].LastRebuildMs:F1} ms");

        // 这一项才是"细分缓存"的用武之地：把同一屏反复重画（拖动、来回滚动都属于这类）。
        // 缓存策略三档（全留 / 只留细分 / 全丢）在这里会拉开差距。
        double repaintSum = 0;
        const int repaints = 20;
        for (int i = 0; i < repaints; i++)
        {
            Doc.Dirty.MarkFull();
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();
            repaintSum += _windows[0].LastRecordMs;
        }
        Console.WriteLine($"  整屏连续重画 ×{repaints}: 平均记录 {repaintSum / repaints:F2} ms"
                        + $"（几何缓存存活 {Stroke.LiveGeometries}）");

        var sw = Stopwatch.StartNew();
        ViewOffsetY -= _virtualH;
        ClampViewOffset();
        NowMs = _clock.Elapsed.TotalMilliseconds;
        RenderAll();
        sw.Stop();
        Console.WriteLine($"  滚一屏: 记录 {_windows[0].LastRecordMs:F1} ms（含贴图），墙钟 {sw.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine($"  撤销栈: {Doc.UndoDepth} 步，仍引用 {Doc.HistoryStrokes} 条笔画；几何存活 {Stroke.LiveGeometries} 个");
        int withGeo = 0;
        foreach (var st in Doc.Strokes) if (st.Geometry != null) withGeo++;
        Console.WriteLine($"  直接数一遍：{Doc.Strokes.Count} 条笔画里有 {withGeo} 条挂着几何（其余是没画过、或几何已释放）");

        // 收尾：清空 + 强制回收，看这些内存在"文档数据"和"引擎池"之间怎么分。
        Doc.Clear();
        Doc.ClearHistory();
        RenderAll();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Mem.TrimWorkingSet();
        Console.WriteLine($"  清空 + 回收之后：提交 {Mem.Priv():F0} MB，工作集 {Mem.Ws():F0} MB，显存 {GpuMb()}，几何存活 {Stroke.LiveGeometries}");
        Gfx.TrimVideoMemory();
        Console.WriteLine($"  再 Trim 显存之后：提交 {Mem.Priv():F0} MB，工作集 {Mem.Ws():F0} MB，显存 {GpuMb()}");
        _quit = true;
    }


    private void MemLifeTest(int strokes)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 内存生命周期（{strokes} 笔）===");

        void Row(string what)
            => Console.WriteLine($"  {what,-22} 提交 {Mem.Priv(),7:F1} MB | 工作集 {Mem.Ws(),7:F1} MB | 显存 {GpuMb(),18}"
                               + $" | 文档 {Doc.Strokes.Count,7} 笔 | 撤销栈持有 {Doc.HistoryStrokes,7} 笔"
                               + $" | 几何存活 {Stroke.LiveGeometries,7}");

        void Reclaim()
        {
            // 文档说的标准组合：先让 GC 收干净（含 LOH 压缩），再让 Windows
            // 把回收过的页面从工作集里踢出去。
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Mem.TrimWorkingSet();
        }

        void ReclaimAll()
        {
            Reclaim();
            bool trimmed = Gfx.TrimVideoMemory();      // 把驱动内部缓存也还回去
            Mem.TrimWorkingSet();
            Console.WriteLine($"  （显存 Trim 调用 {(trimmed ? "成功" : "失败")}）");
        }

        Doc.Clear();
        Doc.ClearHistory();
        RenderAll();
        Reclaim();
        Row("0. 空文档");

        GenerateStrokes(strokes);
        Reclaim();
        Row("1a. 只建笔画数据（没画）");
        RenderAll();
        Reclaim();
        Row("1. 生成之后");

        // = Ctrl+A + Delete：删除是"移进撤销栈"，不是消失
        Doc.Selected.Clear();
        foreach (var st in Doc.Strokes) Doc.Selected.Add(st);
        Doc.DeleteSelected();
        RenderAll();
        Row("2. 删除（未回收）");

        Reclaim();
        Row("3. 删除 + 强制回收");

        ReclaimAll();
        Row("3b. 再 Trim 显存");

        Doc.ClearHistory();
        Reclaim();
        Row("4. 再清空撤销栈");

        // 对照：走"清空"这条路
        GenerateStrokes(strokes);
        RenderAll();
        Doc.Clear();
        RenderAll();
        Reclaim();
        Row("5. 清空(Clear)+回收");

        Doc.ClearHistory();
        Reclaim();
        Row("6. 清空 + 清空撤销栈");

        ReclaimAll();
        Row("6b. 再 Trim 显存");

        Console.WriteLine();
        Console.WriteLine("  说明：'提交大小'才是真实占用；工作集（任务管理器那一列）会被系统随时收回。");
        Console.WriteLine("        '撤销栈持有' = 被删/被清空的笔画还挂在撤销栈里等着还原。");
        _quit = true;
    }


    /// <summary>
    /// 书写期间 GC 低延迟档自检（见 GcLatency.cs）。
    ///
    /// 为什么单开一条：这是**新机制，而且它的失效方式是"悄悄没生效"**——设置被系统拒绝，
    /// 或者后台 GC 被关掉的时候，它只是不工作，屏幕上一点异常都看不出来。
    /// 所以要验三件眼睛看不见的事：
    ///   ① 写一笔的时候真的处于 SustainedLowLatency；
    ///   ② 这一笔期间**没有第 2 代回收**（那正是低配机上"卡一下"的元凶）；
    ///   ③ 超过保持窗口之后**真的退得回来**（不退的话堆一直不压缩，内存越用越多）。
    /// 顺带验分配 / GC 仪表本身有没有在记数——仪表坏了，后面所有低配优化都没有依据。
    /// </summary>
    private void GcLatencyTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 书写期间 GC 低延迟档自检 ===");
        if (!GcLatency.Enabled)
        {
            Console.WriteLine("  SKIP: --nogclatency 关掉了它（那是做「开/不开」对照测量用的，不是失败）");
            _quit = true;
            return;
        }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-46} {detail}");
        }

        var oldHold = GcLatency.HoldMs;
        GcLatency.HoldMs = 150;                 // 自检里不用等 5 秒
        GcLatency.EnterCount = GcLatency.ExitCount = 0;
        StrokesMeasured = StrokesWithGc2 = 0;

        Doc.Clear();
        Doc.ClearHistory();
        Doc.InvalidateAll();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Tool = Tool.Pen;
        SettleFrames(150);

        // ---- 真拖一笔（和 --predicttailtest 同一套注入）----
        float x0 = _virtualX + _virtualW * 0.30f;
        float y0 = _virtualY + _virtualH * 0.50f;
        SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
        bool sawOn = false;
        for (int i = 1; i <= 10; i++)
        {
            SendMouse((int)(x0 + 16f * i), (int)(y0 + 2f * i), 0);
            SettleFrames(17);
            if (GcLatency.On) sawOn = true;
        }
        // **笔画尽头也必须还在低延迟档里**：第一版只在"按下"那一刻刷新保持窗口，
        // 一笔拖得比窗口长就会在**笔画中途**退出来（越写越容易卡，正好相反）。
        // 这一条就是钉住那个 bug 的。
        bool onAtEnd = GcLatency.On;
        Check("整笔（含笔画末端）都处于低延迟档", sawOn && onAtEnd,
              $"中途见到={(sawOn ? "是" : "否")}、末端={(onAtEnd ? "是" : "否")}；{GcLatency.Describe()}");
        SendMouse((int)(x0 + 160f), (int)y0, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        Check("低延迟档至少进去过一次", GcLatency.EnterCount > 0, GcLatency.Describe());
        Check("这一笔期间没有第 2 代回收", StrokesWithGc2 == 0,
              $"第 2 代出现在 {StrokesWithGc2} 笔里；这一笔 GC {StrokeGc0}/{StrokeGc1}/{StrokeGc2}");
        Check("分配仪表真的在记数", StrokeAllocBytes > 0,
              $"这一笔分配 {StrokeAllocBytes / 1024.0:F1} KB"
              + "（阈值等有了低配基线再定，这一条只验仪表通了）");
        if (GcLatency.FailNote != null)
            Console.WriteLine($"  注意：这台机器上低延迟档不可用——{GcLatency.FailNote}");

        // ---- ③ 超时退回：等过保持窗口，并持续渲染（Tick 挂在每帧渲染里）----
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < GcLatency.HoldMs + 80) SettleFrames(20);
        Check("超过保持窗口后退回默认档", !GcLatency.On && GcLatency.ExitCount > 0, GcLatency.Describe());

        GcLatency.HoldMs = oldHold;
        Console.WriteLine();
        Console.WriteLine($"  合计：{pass} 项通过，{fail} 项失败");
        Console.WriteLine(fail == 0 ? "  PASS: GC 低延迟档自检全部通过" : "  FAIL: GC 低延迟档自检有失败项");
        Doc.Clear();
        Doc.ClearHistory();
        Doc.InvalidateAll();
        SettleFrames(120);
        _quit = true;
    }

    // ---- 合成笔（自检注入用）--------------------------------------------



    private void LongRunTest(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 长跑测试（{seconds:F0} 秒，模拟连续上课使用）===");
        Console.WriteLine("  每轮：画 24 笔 → 擦掉几笔 → 撤销几次 → 偶尔清空");
        Console.WriteLine();
        Console.WriteLine("   时间 | 工作集 | 提交   | 笔画数 | 平均记录 | 平均上屏 | 最慢帧");
        Console.WriteLine("  ------|--------|--------|--------|----------|----------|--------");

        var started = _clock.Elapsed.TotalMilliseconds;
        double nextReport = 0;
        int round = 0;
        double worstFrame = 0;
        var recordSum = new List<double>();

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        var rnd = new Random(4242);

        while (_clock.Elapsed.TotalMilliseconds - started < seconds * 1000)
        {
            round++;
            // 画 24 笔（直接构造笔画，省掉合成鼠标的等待时间，把 CPU 全留给渲染）
            var batch = new List<Stroke>();
            for (int i = 0; i < 24; i++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = PenColor, Width = 3f * DpiScale };
                float x = _virtualX + (float)rnd.NextDouble() * _virtualW;
                float y = _virtualY + (float)rnd.NextDouble() * _virtualH;
                float ang = (float)(rnd.NextDouble() * Math.PI * 2);
                for (int k = 0; k < 24; k++)
                {
                    ang += (float)((rnd.NextDouble() - 0.5) * 0.7);
                    x += MathF.Cos(ang) * 12f;
                    y += MathF.Sin(ang) * 12f;
                    s.AddPoint(x, y, 0.8f, NowMs);
                }
                Doc.AddStroke(s);
                batch.Add(s);
            }
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();

            // 擦掉几笔
            for (int i = 0; i < 6 && batch.Count > 0; i++)
            {
                var s = batch[rnd.Next(batch.Count)];
                Doc.EraseAt(s.Bounds.MinX + 1, s.Bounds.MinY + 1, 20f);
            }
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();

            // 撤销几次
            for (int i = 0; i < 8; i++) Doc.Undo();
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();

            // 每 8 轮清空一次，模拟换一页
            if (round % 8 == 0) { Doc.Clear(); RenderAll(); }

            recordSum.Add(_windows[0].LastRecordMs);
            if (_windows[0].LastRecordMs > worstFrame) worstFrame = _windows[0].LastRecordMs;
            if (recordSum.Count > 60) recordSum.RemoveAt(0);

            double elapsed = (_clock.Elapsed.TotalMilliseconds - started) / 1000.0;
            if (elapsed >= nextReport)
            {
                nextReport += 15;
                double avg = 0;
                foreach (var v in recordSum) avg += v;
                avg = recordSum.Count > 0 ? avg / recordSum.Count : 0;
                Console.WriteLine($"  {elapsed,5:F0}s | {Mem.Ws(),5:F1}M | {Mem.Priv(),5:F1}M | {Doc.Strokes.Count,6} | {avg,7:F2}ms | {_windows[0].LastPresentMs,7:F2}ms | {worstFrame,5:F2}ms");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  结束：工作集 {Mem.Ws():F1} MB，提交 {Mem.Priv():F1} MB，笔画 {Doc.Strokes.Count}，共 {round} 轮");
        Console.WriteLine($"  最慢单帧记录耗时：{worstFrame:F2} ms");
        _quit = true;
    }


    /// <summary>Asks the OS which window is top-most under the screen centre.
    /// Read-only: no synthetic clicks are ever sent.</summary>
    private string ProbeHitTest()
    {
        var pt = new Native.POINT { X = _virtualX + _virtualW / 2, Y = _virtualY + _virtualH / 2 };
        IntPtr under = Native.WindowFromPoint(pt);
        IntPtr own = _windows[0].Hwnd;
        bool ours = under == own || Native.GetAncestor(under, 2 /*GA_ROOT*/) == own;
        return $"hwnd 0x{under:X}{(ours ? " = 本覆盖窗口" : $" ≠ 本覆盖窗口 0x{own:X}")}";
    }


    /// <summary>
    /// 压感采集诊断：用合成笔注入一条**已知**的轨迹（点数、节奏、压力都已知），
    /// 然后看引擎收下了多少点、合并点有没有读全、压感有效位判得对不对。
    ///
    /// 这个用例的价值在于它**不依赖真笔**：注入 160 个点、每帧塞 4 个，
    /// 系统必然把其中几条合并成一条消息——如果合并点没读全，笔画的点数会明显少于 160。
    /// </summary>
    private void PressureDiagTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 压感采集诊断（合成笔注入）===");
        if (!EnsureSyntheticPen())
        {
            Console.WriteLine("  SKIP: 拿不到合成笔设备（CreateSyntheticPointerDevice 失败）");
            _quit = true; return;
        }

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SettleFrames(120);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        const int PerFrame = 4;
        const int Frames = 40;
        int injected = 0;

        SendPenPoint(cx - 300, cy, 200, contact: true, first: true);
        injected++;

        for (int f = 0; f < Frames; f++)
        {
            // 每帧塞 PerFrame 个点：系统会把它们合并进同一条消息（这正是要测的）
            for (int k = 0; k < PerFrame; k++)
            {
                float t = (f * PerFrame + k) / (float)(Frames * PerFrame);
                SendPenPoint(cx - 300 + t * 600, cy + MathF.Sin(t * 6.28f) * 40,
                             (uint)(200 + t * 700), contact: true, first: false);
                injected++;
            }
            SettleFrames(1);        // 抽一次消息：这一帧应该把合并的几个点一起读进来
        }

        SendPenPoint(cx + 300, cy, 400, contact: false, first: false);   // 抬笔
        SettleFrames(60);

        int strokePoints = 0, withPressure = 0;
        float pMin = 9f, pMax = -1f;
        if (Doc.Strokes.Count > 0)
        {
            var s = Doc.Strokes[^1];
            strokePoints = s.Points.Count;
            foreach (var pt in s.Points)
            {
                if (pt.P > 0f && MathF.Abs(pt.P - 0.5f) > 0.001f) withPressure++;
                pMin = MathF.Min(pMin, pt.P);
                pMax = MathF.Max(pMax, pt.P);
            }
        }

        double ratio = LastCoalescedMessages > 0 ? LastCoalescedSamples / (double)LastCoalescedMessages : 0;
        Console.WriteLine($"  注入采样点            : {injected}");
        Console.WriteLine($"  引擎收到的消息 / 采样点: {LastCoalescedMessages} / {LastCoalescedSamples}（每条消息平均 {ratio:F2} 个点）");
        Console.WriteLine($"  本笔判定有无压感       : {(ActiveStrokeHasPressure ? "有" : "无")}");
        Console.WriteLine($"  落到笔画里的点数       : {strokePoints}（其中带压感的 {withPressure} 个）");
        Console.WriteLine($"  压力范围              : {(pMax >= 0 ? $"{pMin:F3} ~ {pMax:F3}" : "（没有点）")}");
        Console.WriteLine($"  设备                  : {DeviceName(LastPointerType)}");
        Console.WriteLine($"  湿墨轨迹              : 开关={(OverlayWindow.InkTrailEnabled ? "开" : "关")}"
                          + $"，最后一次调用={OverlayWindow.InkTrailDebug}");

        bool readAll = strokePoints >= injected * 0.8;
        bool coalesced = ratio > 1.3;
        bool pressureOk = ActiveStrokeHasPressure && pMax - pMin > 0.3f;

        Console.WriteLine(readAll
            ? $"  PASS: 合并点读全了（{strokePoints} ≥ 注入 {injected} 的 80%）"
            : $"  FAIL: 点数明显少于注入（{strokePoints} vs {injected}）——合并点没读全");
        Console.WriteLine(coalesced
            ? $"  PASS: 确实发生了合并（每条消息 {ratio:F2} 个点）"
            : $"  WARN: 这次没观察到合并（每条消息 {ratio:F2} 个点），用例的说服力打折");
        Console.WriteLine(pressureOk
            ? "  PASS: 压感有效位与压力数值都对"
            : $"  FAIL: 压感判定不对（有压感={ActiveStrokeHasPressure}，范围 {pMin:F3}~{pMax:F3}）");

        _quit = true;
    }

    /// <summary>
    /// `--tapdotprobe`：**"点一下冒圆"复现器**（2026-10-08 用户报："起笔先出一个圆、
    /// 续笔后变尖笔锋；轻轻点一下也会出"）。合成笔注入三段，全部走真笔同一条 WM_POINTER 路：
    ///   ① 轻点（P≈0.15）按住 8 帧 → 抬；② 重点（P≈0.6）同上；③ 点住停 4 帧再横写 8 点（P 渐升）。
    /// 每帧打印 `ActiveStroke` 点数（判断"按住不动时点进不进笔画"——机理的关键），四处存图。
    /// 对照：裸跑 vs `--predict2`（跑两次看有没有尾的份）。
    /// </summary>
    private void TapDotProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 点一下圆头探测（合成笔；预测="
                          + (global::InkEngine.InkEngine.PredictTailEnabled ? "开" : "关") + "）===");
        if (!EnsureSyntheticPen())
        {
            Console.WriteLine("  SKIP: 拿不到合成笔设备（CreateSyntheticPointerDevice 失败）");
            _quit = true; return;
        }

        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        PassThrough = false;
        Tool = Tool.Pen;
        SetColorFromUi(new Color4(1f, 0f, 0f, 1f));    // 用户图里的红色
        Doc.InvalidateAll();
        SettleFrames(200);

        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opencode");
        Directory.CreateDirectory(tmp);
        float x0 = _virtualX + 400f, y0 = _virtualY + 300f;
        int ShotX = (int)(x0 - 70), ShotY = (int)(y0 - 80);
        int ShotW = 700, ShotH = 180;
        void Shot(string name) => ScreenProbe.SaveBmp(
            System.IO.Path.Combine(tmp, name + ".bmp"), ShotX, ShotY, ShotW, ShotH);
        string Count() => ActiveStroke == null ? "-" : ActiveStroke.Points.Count.ToString();

        // ① 轻点：**接触尖峰（0.5×2 点）→ 轻压稳定（0.04×30 点）**，每帧塞 4 点（像真笔的高采样）。
        //    这条对齐用户实况（"开 ink + 轻轻点 = 先冒个圆、再变尖笔锋"）。
        Console.WriteLine("  ① 轻点：尖峰 0.5×2 → 0.04×30（每帧 4 点）");
        {
            bool firstPt = true;
            void Burst(uint p, int n)
            {
                for (int k = 0; k < n; k++)
                {
                    SendPenPoint(x0, y0, p, contact: true, first: firstPt);
                    firstPt = false;
                    if (k % 4 == 3) SettleFrames(1);
                }
                SettleFrames(1);
            }
            Burst(500, 2);
            Burst(40, 10);
            Shot("tapdot-1a-spike-then-light");
            Burst(40, 20);
            Console.WriteLine($"    活笔点数={Count()}");
            Shot("tapdot-1b-light-full");
            SendPenPoint(x0, y0, 0, contact: false, first: false);
            SettleFrames(40);
            Shot("tapdot-1-light-done");
        }

        // ② 重点：按住 8 帧再抬
        Console.WriteLine("  ② 重点 P=0.60（按住 8 帧）");
        float x1 = x0 + 200f;
        SendPenPoint(x1, y0, 600, contact: true, first: true);
        for (int f = 0; f < 8; f++)
        {
            SendPenPoint(x1, y0, 600, contact: true, first: false);
            SettleFrames(1);
            Console.WriteLine($"    帧{f}: 活笔点数={Count()}");
        }
        Shot("tapdot-2-heavy-hold");
        SendPenPoint(x1, y0, 0, contact: false, first: false);
        SettleFrames(40);
        Shot("tapdot-2-heavy-done");

        // ③ 点住停 4 帧，再沿小弧走 8 点（压力 0.15→0.3）——对齐用户图里的"圆点＋弧"
        Console.WriteLine("  ③ 点住停 4 帧 → 小弧 8 点（P 0.15→0.3）");
        float x2 = x0 + 400f;
        SendPenPoint(x2, y0, 150, contact: true, first: true);
        for (int f = 0; f < 4; f++)
        {
            SendPenPoint(x2, y0, 150, contact: true, first: false);
            SettleFrames(1);
            Console.WriteLine($"    停{f}: 活笔点数={Count()}");
        }
        Shot("tapdot-3-before-write");
        for (int i = 1; i <= 8; i++)
        {
            float ang = i / 8f * 1.6f;              // ≈92°的小弧
            float ax = x2 + MathF.Sin(ang) * 40f;
            float ay = y0 + (1f - MathF.Cos(ang)) * 40f;
            uint p = (uint)(150 + (300 - 150) * i / 8f);
            SendPenPoint(ax, ay, p, contact: true, first: false);
            SettleFrames(1);
            Console.WriteLine($"    弧{i}: 活笔点数={Count()}");
            if (i == 2) Shot("tapdot-3-write-2");
            if (i == 5) Shot("tapdot-3-write-5");
        }
        float axEnd = x2 + MathF.Sin(1.6f) * 40f;
        float ayEnd = y0 + (1f - MathF.Cos(1.6f)) * 40f;
        SendPenPoint(axEnd, ayEnd, 0, contact: false, first: false);
        SettleFrames(40);
        Shot("tapdot-3-done");

        // ④ 轻压小圈（P≈0.08、半径 28px、16 点）——检验"轻压=发丝线 → 圆被打碎"
        Console.WriteLine("  ④ 轻压小圈（P≈0.08）");
        float cx4 = x0 + 560f, cy4 = y0 + 20f;
        for (int i = 0; i <= 16; i++)
        {
            float ang = i / 16f * MathF.PI * 2f;
            SendPenPoint(cx4 + MathF.Cos(ang) * 28f, cy4 + MathF.Sin(ang) * 28f,
                         82, contact: true, first: i == 0);
            SettleFrames(1);
            if (i == 8) Shot("tapdot-4-circle-mid");
        }
        SendPenPoint(cx4 + 28f, cy4, 0, contact: false, first: false);
        SettleFrames(40);
        Shot("tapdot-4-circle-done");

        // ⑤ 压力掉坑：一条直线 0.4 → 0.03 → 0.35（检验"看不见的段 = 假断笔"）
        Console.WriteLine("  ⑤ 压力掉坑直线（0.4→0.03→0.35）");
        float x5 = x0 + 120f, y5 = y0 + 60f;
        for (int i = 0; i <= 14; i++)
        {
            uint p = i < 3 ? 400u : (i < 9 ? 30u : 350u);
            SendPenPoint(x5 + i * 14f, y5, p, contact: true, first: i == 0);
            SettleFrames(1);
        }
        SendPenPoint(x5 + 14 * 14f, y5, 0, contact: false, first: false);
        SettleFrames(40);
        Shot("tapdot-5-dip-done");
        // ⑥ 用户实测形态（2026-10-08 样本）：点住 40 点（P 0.04→0.33）→ 起写掉坑（P→0.03）
        //    → 弧上回升到 0.30。这就是"先出个圆、一开写变尖尖笔锋"的实况复现。
        Console.WriteLine("  ⑥ 实况复现：点住 0.04→0.33 → 起写掉到 0.03 → 弧上回升");
        float x6 = x0 + 120f, y6 = y0 - 40f;
        for (int k = 0; k < 40; k++)
        {
            uint p6 = (uint)(41 + (338 - 41) * k / 39f);          // 0.040→0.330
            SendPenPoint(x6, y6, p6, contact: true, first: k == 0);
            if (k % 4 == 3) SettleFrames(1);
        }
        SettleFrames(1);
        Shot("tapdot-6a-dwell");
        for (int i = 1; i <= 16; i++)
        {
            float ang = i / 16f * 1.8f;
            float ax6 = x6 + MathF.Sin(ang) * 45f;
            float ay6 = y6 + (1f - MathF.Cos(ang)) * 45f;
            uint p6 = i <= 4 ? 31u : (uint)(31 + (307 - 31) * (i - 4) / 12f);   // 0.03 → 0.30
            SendPenPoint(ax6, ay6, p6, contact: true, first: false);
            SettleFrames(1);
            if (i == 3) Shot("tapdot-6b-dip");
            if (i == 8) Shot("tapdot-6c-mid");
        }
        SendPenPoint(x6 + MathF.Sin(1.8f) * 45f, y6 + (1f - MathF.Cos(1.8f)) * 45f, 0, contact: false, first: false);
        SettleFrames(40);
        Shot("tapdot-6d-done");

        Console.WriteLine($"  存图目录: {tmp}");
        _quit = true;
    }

    /// <summary>
    /// 压感自检（2026-09-20 加：用户有了手写笔之后要"和人家一样、手感正常"）。
    ///
    /// 四层判据，缺一层都可能"看着绿其实没做"：
    ///   ① **映射**（纯函数）：0 压 = 最小倍、满压 = 最大倍、单调、gamma 真的起作用；
    ///   ② **上屏**（真机路径）：合成笔注入**从用户那张三线图反推回来的**几个力度
    ///      （鼠标参照 / 轻描 0.17 / 半压 0.50 / 重压 0.86 / 满压 1.00），量出来的**墨量比**
    ///      必须对得上那张图（0.33× / 1.00× / 1.72× / 2.00×）——只看 `HasPressure` 那一位的话，
    ///      渲染没接上也会是绿的；
    ///   ③ **回退**：鼠标画的（没有压感）与**虚线**笔迹必须仍然**等宽**（老路一个字没改）；
    ///   ④ **存档**：v10 往返把"有压感"这一位带回来；再造一个**真·v9 老文件**，
    ///      读进来必须是"没有压感"（老文件本来就没这一位）——版本闸写错时唯一会炸的地方。
    ///   ⑤ **离屏那条路**（导出 / 剪贴板那张图）：换的是渲染目标，同一条 DrawStroke，
    ///      但"同一个入口"这种事只有真画一遍、数一遍像素才算数。
    ///
    /// 顺带断言"最粗那一处**点得中**"：命中按最粗处算，否则重压的地方看得见、点不着。
    /// </summary>
    /// <summary>
    /// `--ballprobe [前缀]`：**收起球贴边诊断**（用户 2026-10-01 报"球贴到左右边会变成
    /// 一个纯颜色的小圆球、还吸不进去"）。把球摆到左/右/四角，显示态与隐藏态各出一张图，
    /// 顺便打印每一格的占用矩形与 peek 值——"看得见的"和"算出来的"对不上时一眼就能看见。
    /// </summary>


    private void InputPathTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 输入路径测试（会自动移动鼠标画一笔）===");

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.4f;
        int before = Doc.Strokes.Count;

        if (SkipIfNoSyntheticInput("输入路径自检")) { _quit = true; return; }

        SendMouse((int)(cx - 400), (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)(cx - 400), (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        const int steps = 60;
        for (int i = 1; i <= steps; i++)
        {
            float px = cx - 400 + i * 13f;
            float py = cy + MathF.Sin(i * 0.18f) * 70f;
            SendMouse((int)px, (int)py, 0);
            SettleFrames(8);
        }

        SendMouse((int)(cx - 400 + steps * 13f), (int)(cy + MathF.Sin(steps * 0.18f) * 70f),
                  Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(300);

        Console.WriteLine("  笔画报告: " + (_lastStrokeReport ?? "（没有笔画被提交）"));
        Console.WriteLine($"  文档笔画数: {before} -> {Doc.Strokes.Count}");

        bool gotStroke = Doc.Strokes.Count > before;
        int points = gotStroke ? Doc.Strokes[^1].Points.Count : 0;
        Console.WriteLine($"  采集点数: {points}");

        // A dot would only paint a small blob where the press happened. Sample
        // along the whole path: every sample must have ink, not just the start.
        Console.WriteLine("  沿路径采样（屏幕上的红色像素数）：");
        int hits = 0;
        for (int s = 0; s <= 6; s++)
        {
            int i = s * steps / 6;
            float px = cx - 400 + i * 13f;
            float py = cy + MathF.Sin(i * 0.18f) * 70f;
            int n = ScreenProbe.CountRed((int)(px - 40), (int)(py - 40), 80, 80);
            Console.WriteLine($"    第{i,3}步 ({px,6:F0},{py,6:F0}) : {n,5} 像素");
            if (n > 30) hits++;
        }
        int whole = ScreenProbe.CountRed((int)(cx - 430), (int)(cy - 130), 860, 260);
        Console.WriteLine($"  整条路径范围内红色像素合计: {whole}");

        if (gotStroke && Doc.Strokes[^1].Points.Count > 1)
        {
            var st = Doc.Strokes[^1];
            float sum = 0;
            for (int k = 1; k < st.Points.Count; k++)
                sum += Vector2.Distance(
                    new Vector2(st.Points[k - 1].X, st.Points[k - 1].Y),
                    new Vector2(st.Points[k].X, st.Points[k].Y));
            float avg = sum / (st.Points.Count - 1);
            Console.WriteLine($"  轮廓平滑度: 折线拐点平均间距 {avg:F1} 物理像素"
                              + $"（拟合后按 1.6 像素重采样，所以实际轮廓精度是 1.6）");
        }

        bool ok = points > 10 && hits == 7;
        Console.WriteLine(ok
            ? "  PASS: 路径上处处有墨，画出来的是线不是点"
            : $"  FAIL: 路径上有 {7 - hits} 处没有墨");
        _quit = true;
    }

    // =====================================================================
    //  延时实测：笔尖动 → 像素亮
    // =====================================================================
    //
    // 为什么要一个专门的模式：性能面板上的"输入到上屏"是一个 EMA，看得见趋势
    // 但看不到分布，也分不清是哪一段慢。延时是**分布**问题——平均值好看、
    // 偶尔一帧慢，手感就是"偶尔一顿"。所以这里采每条样本的四个分段，出分位数。
    //
    // 各段归属（哪一段该由谁负责）见 Latency.cs 的注释。

    /// <summary>
    /// 起一个后台线程按**固定频率**喂合成指针输入。
    ///
    /// 必须独立成线程：如果在主线程上"发一个点、渲染一帧、再发一个点"，
    /// 输入节奏就被渲染节奏绑死了，量到的是自己设计的模式，而不是真实的排队
    /// 行为。真笔是按自己的采样率一直发，应用爱怎么画怎么画——只有把两者分开，
    /// 才能量到"Present 阻塞期间输入排了多久的队"。
    /// </summary>


    /// `--replaytest`：墨迹回放自检（墨迹 C）。
    /// 时间轴 / 前缀进度 / 暂停 / 倍速 / 只读 / 控制条 / 翻页退出 / 放映中当前页。
    /// </summary>
    private void ReplayTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 墨迹回放自检（时间轴 / 暂停 / 倍速 / 只读 / 控制条）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        if (ReplayActive) StopReplay("自检起手");
        Doc.ResetToSinglePage();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SettleFrames(150);

        // 三条笔迹：1000ms 长笔 ＋ 停 500ms ＋ 200ms 短笔 ＋ 停 300ms ＋ 单点（120ms）
        // → 期望总长 2120ms（时间轴口径见计划 6.4.3）
        void AddInk(float x, float y, float dx, float t0, float t1)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.90f, 0.20f, 0.20f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s.AddPoint(x + dx * k / 4f, y + k * 6f, 0.3f + 0.15f * k, t0 + (t1 - t0) * k / 4f);
            Doc.AddStroke(s);
        }
        AddInk(300f, 300f, 400f, 0f, 1000f);
        AddInk(300f, 500f, 400f, 1500f, 1700f);
        var dot = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.13f, 0.45f, 0.90f, 1f), Width = 8f,
        };
        dot.AddPoint(520f, 700f, 0.8f, 2000f);
        Doc.AddStroke(dot);
        SettleFrames(150);
        Check("起手：3 条笔迹都在视口里", Doc.Strokes.Count == 3, $"{Doc.Strokes.Count} 条");

        // ---- 时间轴口径（纯函数，先钉死再进会话）----
        // 写一笔 600ms → 想 5 秒 → 再写一笔 600ms：中间那口"气"压成一个 700ms 节拍；
        // 第一笔从 0ms 开始（没有前置等待）。
        {
            var a = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
            };
            a.AddPoint(0f, 0f, 1f, 0.0);
            a.AddPoint(100f, 0f, 1f, 600.0);
            var b = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
            };
            b.AddPoint(0f, 50f, 1f, 5600.0);
            b.AddPoint(100f, 50f, 1f, 6200.0);
            var tl = ReplayTimeline.Build(new List<Stroke> { a, b });
            Check("时间轴：第一笔从 0ms 开始（没有前置等待）",
                  MathF.Abs(tl.Start[0]) < 0.001f, $"{tl.Start[0]:F0}ms");
            Check("时间轴：想 5 秒再写 → 间隔压成 700ms 一个节拍",
                  MathF.Abs(tl.Start[1] - 1300f) < 1f,
                  $"第二笔起点 {tl.Start[1]:F0}ms（真实 5s，压缩后 600＋700）");
            Check("时间轴：总长 = 600＋700＋600 = 1900ms",
                  MathF.Abs(tl.Total - 1900f) < 1f, $"{tl.Total:F0}ms");
        }

        // ---- ① 时间轴：开始 / 各时刻的"出完条数 + 前缀进度" ----
        Check("开始回放", StartReplayForTest() && ReplayActive, "");
        Check("时间轴：总长 = 1000＋500＋200＋300＋120 = 2120ms",
              MathF.Abs(ReplayTotalForTest - 2120f) < 1f, $"{ReplayTotalForTest:F0} ms");
        ReplaySeekForTest(500f);
        Check("t=500：一条都没出完、正在长第 1 条（param≈0.5）",
              ReplayDoneForTest == 0 && ReplayCurrentIndexForTest == 0
              && MathF.Abs(ReplayCurrentParamForTest - 0.5f) < 0.02f,
              $"done={ReplayDoneForTest}，idx={ReplayCurrentIndexForTest}，param={ReplayCurrentParamForTest:F2}");
        ReplaySeekForTest(1000f);
        Check("t=1000：第 1 条出完、正在停顿",
              ReplayDoneForTest == 1 && ReplayCurrentIndexForTest == -1
              && ReplayScratchCountForTest == 1,
              $"done={ReplayDoneForTest}，idx={ReplayCurrentIndexForTest}，影子={ReplayScratchCountForTest}");
        ReplaySeekForTest(1600f);
        Check("t=1600：第 2 条长了一半",
              ReplayDoneForTest == 1 && ReplayCurrentIndexForTest == 1
              && MathF.Abs(ReplayCurrentParamForTest - 0.5f) < 0.02f,
              $"idx={ReplayCurrentIndexForTest}，param={ReplayCurrentParamForTest:F2}");
        ReplaySeekForTest(2120f);
        Check("t=总长：三条全部出完（影子文档 = 3）",
              ReplayDoneForTest == 3 && ReplayScratchCountForTest == 3,
              $"done={ReplayDoneForTest}，影子={ReplayScratchCountForTest}");
        ReplaySeekForTest(400f);
        Check("往回拖：影子文档回造到 0（不会残留后面的笔迹）",
              ReplayDoneForTest == 0 && ReplayScratchCountForTest == 0,
              $"done={ReplayDoneForTest}，影子={ReplayScratchCountForTest}");
        ReplaySeekForTest(2120f);
        Check("再往前：又全部出完", ReplayDoneForTest == 3 && ReplayScratchCountForTest == 3, "");
        // 出完一条必须走**补画快路径**（只往分块里补那一条）；走成"整层重铺"就是
        // 用户报的"写完一个字闪一下"那个 bug 的根因（影子文档的脏区没被清）。
        // 判据：先回造到 900ms、稳一帧（脏区清干净），再往前跨过第 1 条的结尾 1000ms。
        {
            if (ReplayPlaying) ReplayTogglePauseForTest();   // 先暂停：位置不会被"播着播着"带走
            ReplaySeekForTest(900f);
            SettleFrames(200);
            long appBefore = _windows.Count > 0 ? _windows[0].TotalAppendedTiles : 0;
            ReplaySeekForTest(1050f);
            SettleFrames(200);
            long appAfter = _windows.Count > 0 ? _windows[0].TotalAppendedTiles : 0;
            Check("出完一条走补画快路径（不是整层重铺——'闪一下'的根因）",
                  appAfter > appBefore, $"累计补画块数 {appBefore} → {appAfter}");
            if (!ReplayPlaying) ReplayTogglePauseForTest();  // 恢复播放给下面的倍速用例
        }

        // 完成那一帧墨不能缺席：**前缀撤掉与"补进分块"必须同一帧**（用户 2026-10-01：
        // "每写完一笔还是会闪一下"）。必须让"完成"发生在**帧内**（播放跨过结尾），
        // 用 Seek 测不到——Seek 里补画发生在进入这一帧之前。
        {
            int InkPixels()
            {
                var px = ScreenProbe.CaptureRegion(280, 270, 440, 80);   // 第 1 条笔迹那一带
                int n = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                    if (px[i + 2] > px[i + 1] + 30) n++;
                return n;
            }
            if (ReplayPlaying) ReplayTogglePauseForTest();   // 先冻结在 995ms（差 5ms 完成）
            ReplaySeekForTest(995f);
            SettleFrames(150);
            ReplayTogglePauseForTest();                      // 恢复：让"完成"落在接下来的帧里
            int minInk = int.MaxValue;
            for (int f = 0; f < 6; f++)
            {
                PumpMessages();
                RenderAll();
                minInk = Math.Min(minInk, InkPixels());
                SettleFrames(20);
            }
            Check("完成那一帧墨不缺席（前缀撤掉＋分块补上同一帧）",
                  minInk > 0, $"连续帧里最少的红像素 = {minInk}");
            if (!ReplayPlaying) ReplayTogglePauseForTest();   // 恢复播放，给下面的倍速/播完用例
        }

        // ---- ② 暂停 / 倍速 / 播完自动停 ----
        Check("倍速公式（纯函数）：2× 走过的是 1× 的两倍",
              MathF.Abs((ReplayTimeline.PositionAt(100f, 0, 1000, 2f) - 100f)
                        - 2f * (ReplayTimeline.PositionAt(100f, 0, 1000, 1f) - 100f)) < 1e-3f, "");
        ReplaySeekForTest(0f);
        ReplaySetSpeedForTest(1f);
        SettleFrames(220);
        float pos1 = ReplayPosForTest;
        ReplaySeekForTest(0f);
        ReplaySetSpeedForTest(2f);
        SettleFrames(220);
        float pos2 = ReplayPosForTest;
        Check("倍速：同样播 220ms，2× 走的大约是 1× 的两倍",
              pos1 > 40f && pos2 > pos1 * 1.3f && pos2 < pos1 * 2.7f,
              $"1×={pos1:F0}ms，2×={pos2:F0}ms");
        ReplayTogglePauseForTest();
        float frozen = ReplayPosForTest;
        SettleFrames(300);
        Check("暂停：过 300ms 位置一点不动", !ReplayPlaying && MathF.Abs(ReplayPosForTest - frozen) < 1f,
              $"{frozen:F0} → {ReplayPosForTest:F0}");
        ReplayTogglePauseForTest();
        SettleFrames(180);
        Check("继续：又开始走了", ReplayPlaying && ReplayPosForTest > frozen + 50f,
              $"{frozen:F0} → {ReplayPosForTest:F0}");
        ReplaySeekForTest(ReplayTotalForTest - 80f);
        SettleFrames(350);
        Check("播到结尾：自动停住、全部出完",
              !ReplayPlaying && ReplayDoneForTest == 3
              && MathF.Abs(ReplayPosForTest - ReplayTotalForTest) < 2f,
              $"playing={ReplayPlaying}，pos={ReplayPosForTest:F0}/{ReplayTotalForTest:F0}");

        // ---- ③ 只读 ＋ 点画布 = 暂停/继续（不落墨） ----
        int nStrokes = Doc.Strokes.Count, nUndo = Doc.UndoDepth, nVer = Doc.Version;
        ReplaySeekForTest(0f);
        if (!ReplayPlaying) ReplayTogglePauseForTest();
        float cx = _virtualX + _virtualW * 0.5f;
        float cyc = _virtualY + _virtualH * 0.4f;      // 画布中部（控制条在底部）
        SendMouse((int)cx, (int)cyc, 0);                          SettleFrames(60);
        SendMouse((int)cx, (int)cyc, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
        SendMouse((int)cx, (int)cyc, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(150);
        Check("点画布：只暂停/继续、不落墨",
              !ReplayPlaying && !Host.State.IsDrawing && Doc.Strokes.Count == nStrokes,
              $"playing={ReplayPlaying}，drawing={Host.State.IsDrawing}，笔画={Doc.Strokes.Count}");
        Check("回放只读：笔画数 / 撤销栈 / 文档版本都不变",
              Doc.Strokes.Count == nStrokes && Doc.UndoDepth == nUndo && Doc.Version == nVer,
              $"笔画 {nStrokes}，撤销栈 {nUndo}，版本 {nVer}");

        // ---- ④ 控制条：逐热区 ----
        var bar = ReplayBarRectForTest;
        Check("控制条：贴在屏幕**下边**、整条在屏幕里（够得着；本自检没挂界面所以不抬）",
              bar.MaxY <= _virtualY + _virtualH && bar.MinY > _virtualY + _virtualH * 0.5f
              && bar.MaxX <= _virtualX + _virtualW && bar.MinX >= _virtualX,
              $"({bar.MinX:F0},{bar.MinY:F0})-({bar.MaxX:F0},{bar.MaxY:F0})");

        void TapBar(ReplayBarZone zone, float fracX = 0.5f)
        {
            var r = ReplayBar.ZoneRect(bar, zone, DpiScale);
            float x = r.MinX + (r.MaxX - r.MinX) * fracX;
            float y = (r.MinY + r.MaxY) * 0.5f;
            Check($"[控制条] 命中判定：{zone} 落在这块里",
                  ReplayBar.ZoneAt(bar, x, y, DpiScale) == zone,
                  $"点在 ({x:F0},{y:F0})");
            ReplayPointerDownForTest(x, y);
            ReplayPointerUpForTest(x, y);
            SettleFrames(120);
        }

        bool wasPlaying = ReplayPlaying;
        TapBar(ReplayBarZone.PlayPause);
        Check("控制条：播放/暂停按钮可点", ReplayPlaying != wasPlaying,
              $"playing={ReplayPlaying}");
        TapBar(ReplayBarZone.Speed2);
        Check("控制条：2× 可点", MathF.Abs(ReplaySpeedForTest - 2f) < 0.01f,
              $"倍速 {ReplaySpeedForTest}");
        {
            var track = ReplayBar.ProgressTrack(bar, DpiScale);
            float x25 = track.MinX + (track.MaxX - track.MinX) * 0.25f;
            float yMid = (track.MinY + track.MaxY) * 0.5f;
            ReplayPointerDownForTest(x25, yMid);
            ReplayPointerUpForTest(x25, yMid);
            SettleFrames(150);
            Check("控制条：点进度 25% → 位置≈25%（并暂停）",
                  MathF.Abs(ReplayPosForTest / ReplayTotalForTest - 0.25f) < 0.03f && !ReplayPlaying,
                  $"pos={ReplayPosForTest:F0}/{ReplayTotalForTest:F0}");
        }
        TapBar(ReplayBarZone.Close);
        Check("控制条：✕ 关掉回放", !ReplayActive, $"active={ReplayActive}");

        // ---- ⑤ 编辑动作 / 翻页：先退出回放 ----
        Check("再开始（为翻页用例）", StartReplayForTest() && ReplayActive, "");
        FlipPageFromUi(true);
        Check("翻页：自动退出回放", !ReplayActive, $"active={ReplayActive}");
        FlipPageFromUi(false);

        // ---- ⑥ PPT 回放（当前页）＋ 与 PPT 条/菜单的冲突 ----
        //
        // 用户 2026-10-01 提醒："PPT 条长按菜单里那几项（自动保存 / 清空所有墨迹 /
        // 结束放映）会不会和回放冲突？" 结论与口径：
        //   · 回放只演**当前这一页**；碰 PPT 条（箭头 / 页码 / 菜单）= 先退出回放再执行；
        //   · 翻页 / 跳页 / 退出放映（含键盘 ←→）都会经 `GotoPage`/`ExitPptMode` 收掉回放；
        //   · 菜单里的"清空所有墨迹"因此永远在回放已退场的状态下执行，不会出现
        //     "清了屏、回放还在演旧墨"；
        //   · 同日用户又提议菜单里直接加「回放本页墨迹」（放映时手就在条上）——已加，
        //     这一段最后一小节顺手验"长按 → 点它 → 真的起回放"。
        {
            int desktopStrokes = Doc.Strokes.Count;
            var fake = new PptFakeSource { Showing = true, Slide = 1, SlideId = 256, Total = 3 };
            AttachPptSource(fake, watch: false);
            StepPpt();
            SettleFrames(150);
            Check("进放映（准备 D）", PptMode && Doc.PageKey == 256, $"页键 {Doc.PageKey}");

            AddInk(300f, 300f, 400f, 0f, 800f);
            AddInk(300f, 500f, 400f, 1200f, 1800f);
            SettleFrames(150);
            Check("开始回放：放映中演**当前页**",
                  StartReplayForTest() && ReplayActive && ReplayCountForTest == 2,
                  $"active={ReplayActive}，条数={ReplayCountForTest}");
            ReplaySeekForTest(ReplayTotalForTest);
            Check("放映中回放：能 seek 到结尾（两条都出完）",
                  ReplayDoneForTest == 2 && ReplayScratchCountForTest == 2,
                  $"done={ReplayDoneForTest}，影子={ReplayScratchCountForTest}");

            // 翻页（假源换了一张）→ 回放自动停
            fake.Slide = 2; fake.SlideId = 257;
            StepPpt();
            SettleFrames(200);
            Check("放映中翻页：回放自动停", !ReplayActive && Doc.PageKey == 257,
                  $"active={ReplayActive}，页键 {Doc.PageKey}");

            // 再回放，然后**点 PPT 条的 ▶**：回放先退场，翻页命令照发
            AddInk(300f, 400f, 400f, 0f, 600f);
            SettleFrames(120);
            Check("（准备）第 2 页能起回放", StartReplayForTest(), "");
            {
                var pptBar = PptBarRect();
                float ax = pptBar.MaxX - PptBar.ArrowW * DpiScale * 0.5f;
                float ay = (pptBar.MinY + pptBar.MaxY) * 0.5f;
                int nextBefore = fake.NextCalls;
                SendMouse((int)ax, (int)ay, 0);                          SettleFrames(60);
                SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
                SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(120);
                StepPpt();
                SettleFrames(120);
                Check("点 PPT 条 ▶：先退出回放，翻页命令照发",
                      !ReplayActive && fake.NextCalls > nextBefore,
                      $"active={ReplayActive}，NextCalls {nextBefore} → {fake.NextCalls}");
            }

            // 再回放，然后**点页码**：回放先退场，菜单照开（互不打架）
            fake.Slide = 3; fake.SlideId = 258;
            StepPpt();
            SettleFrames(150);
            AddInk(300f, 400f, 400f, 0f, 600f);
            SettleFrames(120);
            Check("（准备）第 3 页能起回放", StartReplayForTest(), "");
            {
                var pptBar2 = PptBarRect();
                var mid = PptBar.MidCell(pptBar2, DpiScale);
                float mx = (mid.MinX + mid.MaxX) * 0.5f;
                float my = (mid.MinY + mid.MaxY) * 0.5f;
                SendMouse((int)mx, (int)my, 0);                           SettleFrames(60);
                SendMouse((int)mx, (int)my, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
                SendMouse((int)mx, (int)my, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(150);
                Check("点页码：回放先退场、菜单照开（不打架）",
                      !ReplayActive && PptMenuOpen,
                      $"active={ReplayActive}，菜单={PptMenuOpen}");
                // 用户 2026-10-01 提议：菜单里直接给「回放本页墨迹」——
                // 放映时手就在条上，不用再去开中央面板。点它 = 收菜单 + 起当前页回放。
                PptMenuItemRectAt(2, out var miReplay);
                float rix = (miReplay.MinX + miReplay.MaxX) * 0.5f;
                float riy = (miReplay.MinY + miReplay.MaxY) * 0.5f;
                SendMouse((int)rix, (int)riy, 0);                           SettleFrames(60);
                SendMouse((int)rix, (int)riy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
                SendMouse((int)rix, (int)riy, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(120);
                Check("点菜单「回放本页墨迹」：起回放、菜单收起",
                      ReplayActive && !PptMenuOpen,
                      $"active={ReplayActive}，菜单={PptMenuOpen}");
                StopReplayForTest("自检收尾");
                SettleFrames(120);
                Check("（收尾）回放已停", !ReplayActive, $"active={ReplayActive}");
            }

            // 退出放映 → 回放自动停；桌面那一页的笔迹原样
            AddInk(300f, 400f, 400f, 0f, 600f);
            SettleFrames(120);
            Check("（准备）退出放映前能起回放", StartReplayForTest(), "");
            fake.Showing = false;
            StepPpt();
            SettleFrames(200);
            Check("退出放映：回放自动停、回到桌面页",
                  !ReplayActive && !PptMode && Doc.PageKey == 0,
                  $"active={ReplayActive}，Ppt={PptMode}，页键 {Doc.PageKey}");
            Check("退出放映：桌面板书原样（回放没动过真文档）",
                  Doc.Strokes.Count == desktopStrokes, $"{Doc.Strokes.Count} / {desktopStrokes}");
        }

        if (ReplayActive) StopReplay("自检收尾");
        Doc.ResetToSinglePage();
        Doc.ClearHistory();

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        if (fail > 0) ExitCode = 1;
        _quit = true;
    }


    /// <summary>
    /// `--inkfiletest`：墨迹文件自检（墨迹 A：保存 / 打开 / 打开前备份 / 失败模式）。
    ///
    /// 不弹任何对话框：走 <see cref="InkEngine.InkEngine.SaveInkFileForTest"/> /
    /// <c>OpenInkFileForTest</c> 两条"绕开对话框"的路（其余流程和产品一模一样）。
    /// </summary>
    private void InkFileTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 墨迹文件自检（保存 / 打开 / 备份 / 失败模式）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        // 临时目录：文件与备份都不碰用户真货（和 Recovery / PptStore 一个套路）。
        string temp = Path.Combine(Path.GetTempPath(), "inkteach-inkfiletest");
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        Directory.CreateDirectory(temp);
        string backupDir = Path.Combine(temp, "backup");
        InkFileStore.BackupDirOverride = backupDir;

        // ---- 签名：往返一致要比"每一条的对象"逐项相同，不是只数条数 ----
        string Sig(Stroke s)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append((int)s.Kind).Append('|').Append(s.Width.ToString("F3")).Append('|');
            sb.Append($"{s.Color.R:F2},{s.Color.G:F2},{s.Color.B:F2},{s.Color.A:F2}").Append('|');
            sb.Append((int)s.Dash).Append('|').Append(s.Locked ? 1 : 0).Append('|')
              .Append(s.HasPressure ? 1 : 0).Append('|').Append(s.Points.Count).Append('|');
            foreach (var (a, b) in s.Erased) sb.Append($"{a:F2}-{b:F2},");
            sb.Append('|').Append(s.Transform.M11.ToString("F3")).Append(',')
              .Append(s.Transform.M22.ToString("F3")).Append(',')
              .Append(s.Transform.M31.ToString("F3")).Append(',')
              .Append(s.Transform.M32.ToString("F3"));
            if (s.Points.Count > 0)
            {
                var p0 = s.Points[0]; var pn = s.Points[^1];
                sb.Append('|').Append($"{p0.X:F2},{p0.Y:F2},{p0.P:F3};{pn.X:F2},{pn.Y:F2},{pn.P:F3}");
            }
            if (s.Image != null)
            {
                uint h = 2166136261;
                foreach (var b in s.Image.Bgra) { h ^= b; h *= 16777619; }
                sb.Append("|img").Append(s.Image.Width).Append('x').Append(s.Image.Height).Append(':').Append(h);
            }
            return sb.ToString();
        }
        string DocSig()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Doc.Strokes) sb.Append(Sig(s)).Append('\n');
            return sb.ToString();
        }
        string SigOfDoc(InkDocument d)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in d.Strokes) sb.Append(Sig(s)).Append('\n');
            return sb.ToString();
        }

        void BuildDoc()
        {
            Doc.ResetToSinglePage();
            Doc.ClearHistory();

            // ① 有压感的自由笔迹：虚线 + 锁定 + 擦除区间 + 变换（把能存的位都占上）
            var ink = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 7.5f,
                HasPressure = true, Dash = StrokeDash.Dashed, Locked = true,
            };
            ink.AddPoint(100f, 100f, 0.2f, 0);
            ink.AddPoint(160f, 130f, 0.6f, 30);
            ink.AddPoint(220f, 100f, 0.9f, 60);
            ink.AddPoint(280f, 140f, 1.0f, 90);
            ink.AddErased(1.2f, 2.4f);
            ink.Transform = System.Numerics.Matrix3x2.CreateScale(1.25f)
                          * System.Numerics.Matrix3x2.CreateTranslation(33f, -7f);
            Doc.AddStroke(ink);

            // ② 图形（矩形）
            var rect = new Stroke
            {
                Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                Color = new Color4(0.13f, 0.45f, 0.90f, 1f), Width = 3f,
            };
            rect.AddPoint(400f, 200f, 1f, 0);
            rect.AddPoint(520f, 260f, 1f, 10);
            Doc.AddStroke(rect);

            // ③ 图像对象（2×2 纯色块，够验"像素字节也往返"）
            var img = new Stroke
            {
                Tool = Tool.Capture, Kind = StrokeKind.Image, Width = 1f,
                Image = ImageData.Adopt(2, 2, new byte[]
                {
                    0, 0, 255, 255,   0, 255, 0, 255,
                    255, 0, 0, 255,   0, 255, 255, 255,
                }),
            };
            img.AddPoint(600f, 300f, 1f, 0);
            img.AddPoint(610f, 310f, 1f, 1);
            Doc.AddStroke(img);
        }

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        SettleFrames(120);

        BuildDoc();
        string sigBefore = DocSig();
        Check("起手：板书里有各种对象", Doc.Strokes.Count == 3, $"{Doc.Strokes.Count} 条");

        // ---- 保存 → 清空 → 打开：逐项一致 ----
        string savePath = Path.Combine(temp, "板书-测试.inkb");
        Check("保存：写盘成功", SaveInkFileForTest(savePath) && File.Exists(savePath), $"状态：{InkStatus}");
        Check("保存：文件头是 InkTeach 墨迹",
              File.Exists(savePath) && InkSerializer.LooksLikeInk(File.ReadAllBytes(savePath)), savePath);

        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(80);
        Check("打开：读回成功", OpenInkFileForTest(savePath), $"状态：{InkStatus}");
        Check("打开：往返逐项一致（种类/颜色/宽度/压感/擦除/变换/图像哈希）",
              DocSig() == sigBefore, $"{Doc.Strokes.Count} 条");

        // ---- 打开前备份：改一下再打开，备份里必须是"打开前"那一份 ----
        var extra = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                                 Color = new Color4(0f, 0f, 0f, 1f), Width = 5f };
        extra.AddPoint(10f, 10f, 1f, 0);
        Doc.AddStroke(extra);
        SettleFrames(80);
        string sigBeforeOpen2 = DocSig();
        Check("打开前：备份目录还是空的", InkFileStore.BackupCount() == 0, $"{InkFileStore.BackupCount()} 份");
        Check("打开：第二次读回成功（会先写备份）", OpenInkFileForTest(savePath), $"状态：{InkStatus}");
        Check("打开前备份：确实写了一份", InkFileStore.BackupCount() == 1, $"{InkFileStore.BackupCount()} 份");
        {
            bool matches = false;
            var files = Directory.GetFiles(backupDir, "*.inkb");
            if (files.Length == 1)
            {
                var probe = new InkDocument();
                try { InkSerializer.LoadInto(probe, File.ReadAllBytes(files[0])); matches = SigOfDoc(probe) == sigBeforeOpen2; }
                catch { }
            }
            Check("打开前备份：内容 = 打开前那一份板书（能解析、逐项对）", matches,
                  "备份文件自己也要解得回来——只断'文件存在'等于没验");
        }

        // ---- 坏文件 / 版本过新：明确失败、文档一个字节不动 ----
        BuildDoc();
        string sigGood = DocSig();
        string bad = Path.Combine(temp, "坏文件.inkb");
        File.WriteAllText(bad, "这不是墨迹");
        Check("坏文件：明确失败", !OpenInkFileForTest(bad), $"状态：{InkStatus}");
        Check("坏文件：文档一个字节没动", DocSig() == sigGood, $"{Doc.Strokes.Count} 条");

        {
            var tooNew = File.ReadAllBytes(savePath);
            BitConverter.GetBytes(InkSerializer.FormatVersion + 1).CopyTo(tooNew, 4);
            string newer = Path.Combine(temp, "未来版本.inkb");
            File.WriteAllBytes(newer, tooNew);
            Check("版本过新：失败且提示升级",
                  !OpenInkFileForTest(newer) && InkStatus.Contains("升级"), $"状态：{InkStatus}");
            Check("版本过新：文档一个字节没动", DocSig() == sigGood, $"{Doc.Strokes.Count} 条");
        }

        // ---- 空板书：保存被拒（不产生空文件） ----
        Doc.Clear();
        Doc.ClearHistory();
        string emptyPath = Path.Combine(temp, "空.inkb");
        Check("空板书：保存被拒（不产生空文件）",
              !SaveInkFileForTest(emptyPath) && !File.Exists(emptyPath), $"状态：{InkStatus}");

        // ---- 备份轮转：最多 5 份 ----
        for (int i = 0; i < 6; i++) InkFileStore.WriteBackup(new byte[] { 1, 2, 3 });
        Check("备份轮转：最多留 5 份", InkFileStore.BackupCount() == 5, $"{InkFileStore.BackupCount()} 份");

        // ---- 历史清理（墨迹 B）：按保留期清缓存；**永不动当前板书** ----
        {
            string pptRoot = Path.Combine(temp, "ppt");
            PptStore.RootOverride = pptRoot;
            string cwA = Path.Combine(pptRoot, "课件A");
            string cwB = Path.Combine(pptRoot, "课件B");
            Directory.CreateDirectory(cwA);
            Directory.CreateDirectory(cwB);
            string oldPptA = Path.Combine(cwA, "00000001.inkb");
            string newPptA = Path.Combine(cwA, "00000002.inkb");
            string oldPos = Path.Combine(cwA, "Position");
            string oldPptB = Path.Combine(cwB, "00000001.inkb");
            File.WriteAllBytes(oldPptA, new byte[] { 1 });
            File.WriteAllBytes(newPptA, new byte[] { 2 });
            File.WriteAllText(oldPos, "3");
            File.WriteAllBytes(oldPptB, new byte[] { 3 });
            File.SetLastWriteTimeUtc(oldPptA, DateTime.UtcNow.AddDays(-100));
            File.SetLastWriteTimeUtc(oldPos, DateTime.UtcNow.AddDays(-100));
            File.SetLastWriteTimeUtc(oldPptB, DateTime.UtcNow.AddDays(-100));

            // 备份目录重造：一旧一新（前面轮转留下的 5 份清掉，判据才确定）
            try { Directory.Delete(backupDir, true); } catch { }
            Directory.CreateDirectory(backupDir);
            string oldBak = Path.Combine(backupDir, "板书-打开前-20000101-000000000.inkb");
            string newBak = Path.Combine(backupDir, "板书-打开前-20990101-000000000.inkb");
            File.WriteAllBytes(oldBak, new byte[] { 1 });
            File.WriteAllBytes(newBak, new byte[] { 2 });
            File.SetLastWriteTimeUtc(oldBak, DateTime.UtcNow.AddDays(-100));

            // "当前板书"（自动存档）指到 temp：时间再老也不许被清
            Recovery.AutoSavePathOverride = Path.Combine(temp, "autosave.ink");
            File.WriteAllBytes(Recovery.AutoSavePath, new byte[] { 9 });
            File.SetLastWriteTimeUtc(Recovery.AutoSavePath, DateTime.UtcNow.AddDays(-365));

            var (sf, sd) = SweepHistoryForTest(90);
            Check("清理：90 天档删旧留新（Ppt ＋ 备份），共删 4 个文件",
                  sf == 4 && !File.Exists(oldPptA) && File.Exists(newPptA)
                  && !File.Exists(oldBak) && File.Exists(newBak),
                  $"删 {sf} 文件 / {sd} 目录");
            Check("清理：Position（旧）也随课件清掉", !File.Exists(oldPos), oldPos);
            Check("清理：文件被清光的课件目录一起删掉", !Directory.Exists(cwB), cwB);
            Check("清理：**当前板书（autosave）永不动**",
                  File.Exists(Recovery.AutoSavePath) && Recovery.AutoSaveExists, "放了 365 天，仍在");
            var (pf, pd) = SweepHistoryForTest(0);
            Check("清理：永久档一份都不删",
                  pf == 0 && pd == 0 && File.Exists(newPptA) && File.Exists(newBak),
                  $"删 {pf} 文件 / {pd} 目录");

            Recovery.AutoSavePathOverride = null;
            PptStore.RootOverride = null;
        }

        // ---- 放映中：两条都置灰（PPT 有自己的存取） ----
        BuildDoc();
        string sigPpt = DocSig();
        {
            var fake = new PptFakeSource { Showing = true, Slide = 1, SlideId = 256, Total = 3 };
            AttachPptSource(fake, watch: false);
            StepPpt();
            SettleFrames(150);
            string pptPath = Path.Combine(temp, "放映中.inkb");
            Check("放映中：保存被拒", !SaveInkFileForTest(pptPath), $"状态：{InkStatus}");
            Check("放映中：打开被拒", !OpenInkFileForTest(savePath), $"状态：{InkStatus}");
            Check("放映中：没有落文件、也没换页",
                  !File.Exists(pptPath) && Doc.PageKey == 256, $"页键 {Doc.PageKey}");
            fake.Showing = false;
            StepPpt();
            SettleFrames(150);
            Check("放映中：退出放映后桌面板书原样（那两条真的什么都没做）",
                  Doc.PageKey == 0 && DocSig() == sigPpt, $"页键 {Doc.PageKey}，{Doc.Strokes.Count} 条");
        }
        Doc.ResetToSinglePage();
        Doc.ClearHistory();

        try { Directory.Delete(temp, true); } catch { }
        InkFileStore.BackupDirOverride = null;

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        if (fail > 0) ExitCode = 1;
        _quit = true;
    }


    private void PressureTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 压感自检（映射 / 上屏粗细 / 回退 / 存档）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}；变宽通道: {OverlayWindow.InkNote}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-38} {detail}");
        }

        // ================= ① 映射：纯函数先钉死 =================
        {
            float f0 = PressureWidth.Factor(0f), f1 = PressureWidth.Factor(1f), fh = PressureWidth.Factor(0.5f);
            Check("映射：0 压 = 最小倍、满压 = 最大倍、中点居中",
                  MathF.Abs(f0 - PressureWidth.Min) < 1e-4f && MathF.Abs(f1 - PressureWidth.Max) < 1e-4f
                  && fh > f0 && fh < f1,
                  $"{f0:F3} / {fh:F3} / {f1:F3}（配置 {PressureWidth.Min:F2}~{PressureWidth.Max:F2}）");

            bool mono = true;
            float prev = -1f;
            for (int i = 0; i <= 20; i++)
            {
                float f = PressureWidth.Factor(i / 20f);
                if (f < prev - 1e-5f) mono = false;
                prev = f;
            }
            Check("映射：整条曲线单调不减", mono, "0 → 1 走一遍");

            float linear = PressureWidth.Factor(0.25f);
            PressureWidth.Gamma = 2f;
            float curved = PressureWidth.Factor(0.25f);
            PressureWidth.Gamma = 1f;
            Check("映射：gamma 真的起作用（>1 把轻压区间压低）",
                  curved < linear - 1e-4f, $"线性 {linear:F3} → gamma=2 {curved:F3}");
        }

        // [停用/删除 2026-10-05] 这里原来有两块纯函数自检：
        //   ① 模拟压力（Xournal++ / perfect-freehand）——随功能停用移除；
        //   ② 末尾甩速收尖（`--flicktip`）——随功能**删除**（用户判定效果不对）。
        // 代码与备份见 `已停用-渲染实验.md` 与 `.revert/2026-10-05-渲染减法/`。

        if (!EnsureSyntheticPen())
        {
            Console.WriteLine("  SKIP: 拿不到合成笔设备（CreateSyntheticPointerDevice 失败），②③④ 没法验");
            Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
            _quit = true; return;
        }

        // 画面要干净：开白板（底色不透明）、关掉委托湿墨（免得系统画的那条轨迹混进探针）。
        bool oldBoard = BoardOn, oldTrail = OverlayWindow.InkTrailEnabled;
        float oldPenWidth = PenWidthLogical;
        var oldColor = CurrentColor;
        BoardOn = true;
        OverlayWindow.InkTrailEnabled = false;
        PenWidthLogical = 6f;                        // 粗一点：粗细差在像素上更稳
        SetColorFromUi(new Color4(1f, 0f, 0f, 1f));  // 纯红：探针数红像素
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ViewOffsetY = 0f;                            // 画布坐标 == 屏幕坐标，探针才好对位
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        SettleFrames(300);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        const float halfLen = 260f;

        void PenLine(float y, uint pressure)
        {
            SendPenPoint(cx - halfLen, y, pressure, contact: true, first: true);
            for (int i = 1; i <= 24; i++)
            {
                SendPenPoint(cx - halfLen + i * (halfLen * 2f / 24f), y, pressure, contact: true, first: false);
                SettleFrames(2);
            }
            SendPenPoint(cx + halfLen, y, pressure, contact: false, first: false);
            SettleFrames(200);
        }

        // ================= ② 上屏：对齐用户在 WPF 里画的那三条线 =================
        //
        // 用户在同一台机上画了三条横线（鼠标 / 笔轻轻描 / 笔重重压），我们逐像素量出来
        // 18.0 / 6.0 / 31.0 px，并且 WPF 官方文档写明"无压感设备的 PressureFactor 默认 0.5"，
        // 于是反推出那张图的口径：**鼠标 = 0.5 压力处 = 档位宽度；满压 = 2 倍**。
        //
        // 所以下面每条笔迹的压力都是**从那三条线反推回来的值**，判据直接对着它们比：
        //   鼠标（参照 1.00×）→ 轻描 0.17（图里 0.33×）→ 半压 0.50（应当和鼠标一样粗）
        //   → 重压 0.86（图里 1.72×）→ 满压 1.00（外推 2.00×）→ 虚线（回退等宽的对照）
        float yMouse = cy - 300f, yLight = cy - 210f, yHalf = cy - 120f;
        float yHeavy = cy - 30f, yFull = cy + 60f, yDash = cy + 150f;

        // ① 鼠标：没有压感那一路。**它是整段的参照物**（WPF 里那支笔的"中值压力"）。
        SendMouse((int)(cx - halfLen), (int)yMouse, 0);
        SendMouse((int)(cx - halfLen), (int)yMouse, Native.MOUSEEVENTF_LEFTDOWN);
        for (int i = 1; i <= 12; i++)
        {
            SendMouse((int)(cx - halfLen + i * (halfLen * 2f / 12f)), (int)yMouse, 0);
            SettleFrames(3);
        }
        SendMouse((int)(cx + halfLen), (int)yMouse, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(250);

        PenLine(yLight, 174);      // 0.17 轻轻描
        PenLine(yHalf, 512);       // 0.50 半压（应当和鼠标那条一样粗）
        PenLine(yHeavy, 881);      // 0.86 重重压
        PenLine(yFull, 1024);      // 1.00 满压

        // 虚线 + 满压：**本该最粗**，但 ink 通道不吃 dash 图案，必须回退等宽。
        SetDashFromUi(StrokeDash.Dashed);
        SettleFrames(120);
        PenLine(yDash, 1024);
        SetDashFromUi(StrokeDash.Solid);

        // 墨量 = 在笔迹正上方量一块固定大小的框，**按覆盖率求和**（面积 ∝ 线宽）。
        //
        // 为什么不用"数红像素"：抗锯齿会把细线的两侧边缘渲成"半红的过渡色"，
        // 严格按红色阈值数就会漏掉它们——2.8 像素宽的线只数到 2 像素（−29%），
        // 而 12 像素宽的只少 8%。这个**偏置随线宽变化**，会把比值整体放大
        // （实测：真实 3.71 倍量出来是 5.00 倍，2026-09-20 就是这么假红了一条）。
        // 覆盖率是**线性叠加**的：红墨 g=0、白板 g=255，所以每个像素的墨覆盖 = (255−g)/255。
        double Area(float y)
        {
            var px = ScreenProbe.CaptureRegion((int)cx - 200, (int)y - 30, 400, 60);
            double sum = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                int g = px[i + 1], r = px[i + 2];             // BGRA
                if (r > g + 30) sum += (255.0 - g) / 255.0;   // 只算偏红的像素，免得把底纹算进去
            }
            return sum;
        }
        // 粗细（竖直游程）= 虚线那种"有缝"的笔迹要看最粗的地方，所以横向多切几刀取最大。
        int MaxThick(float y)
        {
            int best = 0;
            for (int dx = -120; dx <= 120; dx += 20)
                best = Math.Max(best, ScreenProbe.CountRed((int)cx + dx - 1, (int)y - 40, 2, 80));
            return best;
        }

        double aMouse = Area(yMouse), aLight = Area(yLight), aHalf = Area(yHalf);
        double aHeavy = Area(yHeavy), aFull = Area(yFull), aDash = Area(yDash);

        Check("上屏：六条笔迹都真的画出来了",
              aMouse > 0 && aLight > 0 && aHalf > 0 && aHeavy > 0 && aFull > 0 && aDash > 0,
              $"鼠标 {aMouse:F0} / 轻 {aLight:F0} / 半 {aHalf:F0} / 重 {aHeavy:F0} / 满 {aFull:F0} / 虚线 {aDash:F0}");
        Check("上屏：压力越大墨越多（单调）",
              aLight < aHalf && aHalf < aHeavy && aHeavy < aFull,
              $"{aLight:F0} < {aHalf:F0} < {aHeavy:F0} < {aFull:F0}");

        // ---- 下面四条是**对着用户那三条线**比的（这就是"和 Windows Ink 对齐了没有"）----
        double rHalf = aMouse > 0 ? aHalf / aMouse : 0;
        Check("上屏：半压（0.50）那条 = 鼠标那条（±10%）——「无压感设备压力 0.5」这条前提的验证",
              rHalf >= 0.90 && rHalf <= 1.10, $"半压/鼠标 = {rHalf:F2}");

        double rLight = aMouse > 0 ? aLight / aMouse : 0;
        Check("上屏：轻描（0.17）= 鼠标的 0.33 倍（图里 6.0/18.0 = 0.333）",
              rLight >= 0.28 && rLight <= 0.42, $"轻描/鼠标 = {rLight:F2}");

        double rHeavy = aMouse > 0 ? aHeavy / aMouse : 0;
        Check("上屏：重压（0.86）= 鼠标的 1.72 倍（图里 31.0/18.0 = 1.72）",
              rHeavy >= 1.55 && rHeavy <= 1.90, $"重压/鼠标 = {rHeavy:F2}");

        double rFull = aMouse > 0 ? aFull / aMouse : 0;
        Check("上屏：满压 = 鼠标的 2.00 倍（图上外推值）",
              rFull >= 1.85 && rFull <= 2.15, $"满压/鼠标 = {rFull:F2}");

        // 诊断：分清"没走变宽那条路"和"走了但屏幕上没画出来"（后者是一种可能的失败形态：
        // API 全部返回成功、却一个像素都没有——2026-09-20 就是这么踩到 NibTransform 那个坑的）。
        int wholeScreenRed = ScreenProbe.CountRed(_virtualX, _virtualY, _virtualW, _virtualH);
        Console.WriteLine($"    [诊断] 变宽墨迹绘制次数 = {OverlayWindow.PressureInkDraws}；"
                          + $"全屏红像素 = {wholeScreenRed}；"
                          + $"六条笔迹 y = {yMouse:F0}/{yLight:F0}/{yHalf:F0}/{yHeavy:F0}/{yFull:F0}/{yDash:F0}，"
                          + $"画布偏移 {ViewOffsetY:F0}");

        // 离屏那条路（导出 / 剪贴板给外部程序的那张图）也要画得出变宽墨——它换的是渲染目标，
        // 代码上同一条 DrawStroke，但"同一个入口"这种事只有真画一遍才算数。
        {
            var probe = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(1f, 0f, 0f, 1f), Width = 12f, HasPressure = true,
            };
            probe.AddPoint(0f, 0f, 0.2f, 0);
            probe.AddPoint(100f, 0f, 0.9f, 8);
            var reg = new RectF { MinX = -40, MinY = -40, MaxX = 140, MaxY = 40 };
            var bgra = _windows.Count > 0 ? _windows[0].RenderStrokesToBgra(new[] { probe }, reg, 180, 80) : null;
            int offRed = 0;
            if (bgra != null)
                for (int i = 0; i + 3 < bgra.Length; i += 4)
                    if (bgra[i + 2] > 170 && bgra[i + 1] < 110 && bgra[i] < 110) offRed++;
            Check("离屏那条路（导出 / 剪贴板那张图）也画得出变宽墨",
                  offRed > 0, $"离屏位图里红像素 {offRed}（位图 {bgra?.Length ?? 0} 字节）");
        }

        // ================= ②.5 压感开关（2026-10-01，批次 0.2）=================
        //
        // 用户开关走的是**渲染期**那条路：关掉 → 整块板立刻等宽、文档一个字节不动；
        // 打开 → 恢复。这里必须量**屏幕上的墨**（不是量函数），因为它要盯住的
        // 恰恰是"内容层缓存有没有整层作废"——漏了 InvalidateAll 的话，
        // 函数全对、屏幕上一动不动还是旧粗细（"点了没反应"最典型的一种）。
        {
            int drawsBefore = OverlayWindow.PressureInkDraws;
            // "文档没动"的不变量：笔画数 / 撤销栈深度 / 压力值 / 压感标记——
            // **不能用 `Doc.Version`**：它是"渲染作废"的计数，`InvalidateAll()` 会 +1
            //（开关本来就必须整层作废，所以它变才是对的。第一版拿它当判据，当场红）。
            int strokesBefore = Doc.Strokes.Count;
            int undoBefore = Doc.UndoDepth;
            var fullStroke = Doc.Strokes.FirstOrDefault(
                s => s.HasPressure && s.Points.Count > 0 && s.Points[^1].P > 0.95f);
            float pBefore = fullStroke?.Points[^1].P ?? -1f;
            bool hasPressureBefore = fullStroke?.HasPressure ?? false;

            SetPressureFromUi(false);
            SettleFrames(300);
            double aFullOff = Area(yFull), aHeavyOff = Area(yHeavy), aMouseOff = Area(yMouse);
            Check("开关：关掉后满压那条立刻收成等宽（≈鼠标那条）",
                  aMouseOff > 0 && aFullOff / aMouseOff >= 0.85 && aFullOff / aMouseOff <= 1.15,
                  $"满压/鼠标 = {(aMouseOff > 0 ? aFullOff / aMouseOff : 0):F2}（原来是 {rFull:F2}）");
            Check("开关：关掉后重压 / 满压一样宽",
                  aHeavyOff > 0 && aFullOff / aHeavyOff >= 0.90 && aFullOff / aHeavyOff <= 1.10,
                  $"满压/重压 = {(aHeavyOff > 0 ? aFullOff / aHeavyOff : 0):F2}");
            Check("开关：文档一个字节没动（笔画数 / 撤销栈 / 压力值 / 压感标记）",
                  Doc.Strokes.Count == strokesBefore && Doc.UndoDepth == undoBefore
                  && MathF.Abs((fullStroke?.Points[^1].P ?? -1f) - pBefore) < 1e-6f
                  && (fullStroke?.HasPressure ?? false) == hasPressureBefore,
                  $"笔画 {strokesBefore} → {Doc.Strokes.Count}，撤销栈 {undoBefore} → {Doc.UndoDepth}，"
                  + $"末点压力 {pBefore:F3}，HasPressure = {hasPressureBefore}");

            // 关着的时候**新画**一笔：不该再走变宽通道（PressureInkDraws 不涨）
            float yNew = cy + 240f;
            int drawsOff0 = OverlayWindow.PressureInkDraws;
            PenLine(yNew, 1024);
            Check("开关：关着时新画的压感笔迹也不走变宽通道",
                  OverlayWindow.PressureInkDraws == drawsOff0,
                  $"变宽绘制 {drawsOff0} → {OverlayWindow.PressureInkDraws}");

            SetPressureFromUi(true);
            SettleFrames(300);
            double aFullOn2 = Area(yFull);
            Check("开关：打开后恢复变宽（和关之前一样粗）",
                  aFull > 0 && aFullOn2 / aFull >= 0.90 && aFullOn2 / aFull <= 1.10,
                  $"满压 {aFull:F0} → {aFullOn2:F0}");
            Check("开关：打开后新笔画又走回变宽通道（对照上一条）",
                  OverlayWindow.PressureInkDraws > drawsBefore,
                  $"变宽绘制 {drawsBefore} → {OverlayWindow.PressureInkDraws}");

            // 偏好应用（模拟"启动时读 ui.pressure"那一步）：写 "0" → 关；删掉 → 回到默认开
            SetUiPref("pressure", "0");
            ApplyPressurePrefForTest();
            Check("偏好：ui.pressure = \"0\" → 应用后是关", !PressureWidth.Enabled,
                  $"PressureOn = {PressureWidth.Enabled}");
            SetUiPref("pressure", null);
            ApplyPressurePrefForTest();
            Check("偏好：删掉那一项 → 回到默认开（只写关过的那一份）", PressureWidth.Enabled,
                  $"PressureOn = {PressureWidth.Enabled}");
        }

        // ================= ③ 命中与回退：按"这一笔自己的最粗处"算 =================
        {
            // 按压力**找**两条笔迹（不按索引——自检里写死下标会随功能移位而静默失效）。
            Stroke Light() => Doc.Strokes.FirstOrDefault(
                s => s.Kind == StrokeKind.Freehand && !s.IsImage && s.HasPressure
                     && s.Points.Count > 0 && s.Points[0].P < 0.3f);
            Stroke Full() => Doc.Strokes.FirstOrDefault(
                s => s.Kind == StrokeKind.Freehand && !s.IsImage && s.HasPressure
                     && s.Points.Count > 0 && s.Points[0].P > 0.95f);
            var light = Light();
            var full = Full();

            Check("命中：轻写那条按**它自己**的最粗处算（比档位半宽窄）",
                  light != null && light.MaxHalfWidth < light.Width * 0.5f - 0.5f,
                  light == null ? "没找到轻写那条"
                                : $"最粗半宽 {light.MaxHalfWidth:F2} vs 档位半宽 {light.Width * 0.5f:F2}");
            Check("命中：满压那条 = 档位宽（半宽 = 档位宽本身，因为满压是 2 倍）",
                  full != null && MathF.Abs(full.MaxHalfWidth - full.Width) < 0.3f,
                  full == null ? "没找到满压那条"
                               : $"最粗半宽 {full.MaxHalfWidth:F2} vs 档位宽 {full.Width:F2}");
            float edgeY = yFull + full.Width - 1f;      // 满压边缘往里 1 像素
            Check("命中：满压那条在最粗边缘点得中",
                  full != null && full.HitTestExact(cx, edgeY, 0f),
                  $"点 ({cx:F0},{edgeY:F0})");
        }

        {
            var ms = Doc.Strokes.FirstOrDefault(s => !s.IsImage && !s.HasPressure);
            Check("回退：鼠标画的这一条没有压感标志",
                  ms != null && !ms.HasPressure, ms == null ? "没找到鼠标那条" : "HasPressure = False");
            // 没有压感 → 等宽 = 档位宽度；虚线也一样（ink 通道不吃 dash 图案）。
            // 三条都用同一个探针量**粗细**（虚线有缝，取横向最大值），所以可以直接比。
            int tm = MaxThick(yMouse), td = MaxThick(yDash), tf = MaxThick(yFull);
            Check("回退：虚线笔迹仍然是等宽（粗细 = 档位宽，不随压力变）",
                  td > 0 && td <= tm * 1.25f,
                  $"虚线 {td} 像素 vs 档位 {tm} 像素");
            Check("回退：满压那条确实画粗了（≈ 2 × 档位，证明它没走回退那条路）",
                  tf > 0 && tf >= tm * 1.6f,
                  $"满压 {tf} 像素 vs 档位 {tm} 像素");
            var ds = Doc.Strokes.FirstOrDefault(s => s.Dash == StrokeDash.Dashed);
            Check("回退：虚线笔迹**确实带压感**（不是没压感才等宽）",
                  ds != null && ds.HasPressure, ds == null ? "没找到虚线那条" : $"HasPressure = {ds.HasPressure}");
        }

        // 荧光笔不吃压感（对齐官方示例的 IgnorePressure = true）：拿合成笔写一条荧光笔，
        // 断言它**不带压感标志**（也就是会走等宽那条路）。
        {
            Host.Commands.SetTool(Tool.Highlighter);
            SetColorFromUi(new Color4(1f, 0.6f, 0f, 1f));
            SettleFrames(120);
            PenLine(yDash + 90f, 1024);
            var hl = Doc.Strokes.FirstOrDefault(s => s.Tool == Tool.Highlighter);
            Check("荧光笔不吃压感（官方示例的 IgnorePressure = true 同款）",
                  hl != null && !hl.HasPressure,
                  hl == null ? "没找到荧光笔那条" : $"HasPressure = {hl.HasPressure}");
            Host.Commands.SetTool(Tool.Pen);
            SetColorFromUi(new Color4(1f, 0f, 0f, 1f));
            SettleFrames(120);
        }
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(150);

        // ================= ④ 存档：v10 往返 + 真·v9 老文件 =================
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 12f, HasPressure = true };
            s.AddPoint(10f, 10f, 0.2f, 0);
            s.AddPoint(60f, 10f, 0.9f, 8);
            var d1 = new InkDocument();
            d1.AddStroke(s);
            var back = new InkDocument();
            InkSerializer.LoadInto(back, InkSerializer.Save(d1));
            Check("存档：v10 往返把「有压感」带回来",
                  back.Strokes.Count == 1 && back.Strokes[0].HasPressure,
                  back.Strokes.Count == 1 ? $"HasPressure = {back.Strokes[0].HasPressure}" : "条数不对");

            // 真·v9 老文件（见 MakeLegacyFile）：读进来必须是"没有压感"。
            var d2 = new InkDocument();
            d2.AddStroke(new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 12f, HasPressure = true });
            d2.Strokes[0].AddPoint(10f, 10f, 0.5f, 0);
            d2.Strokes[0].AddPoint(60f, 10f, 0.5f, 8);
            // 砍 3 位 = v10 压感 ＋ v11 朝向 ＋ v12 渐近线（v13 没加字段）。
            byte[] v9 = MakeLegacyFile(d2, 9, 3);
            var old = new InkDocument();
            bool ok = true;
            try { InkSerializer.LoadInto(old, v9); }
            catch (Exception ex) { ok = false; Console.WriteLine("    " + ex.Message); }
            Check("存档：真·v9 老文件读得进、且是「没有压感」",
                  ok && old.Strokes.Count == 1 && !old.Strokes[0].HasPressure,
                  ok ? $"读回 {old.Strokes.Count} 条，HasPressure = {(old.Strokes.Count > 0 ? old.Strokes[0].HasPressure.ToString() : "?")}"
                     : "读取抛异常");
        }

        // 收尾：把状态还回去（自检不该改坏后面要用的东西）。
        BoardOn = oldBoard;
        OverlayWindow.InkTrailEnabled = oldTrail;
        PenWidthLogical = oldPenWidth;
        SetColorFromUi(oldColor);
        SetDashFromUi(StrokeDash.Solid);
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(120);

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }


    /// <summary>
    /// 坐标系 / 数轴自检（2026-09-19 第三批）。
    ///
    /// 四层判据，缺一层都可能"看着绿其实没做"：
    ///   ① **画法**：真机拖一下 → 定义元素按规格落地（**原点 = 按下点**、外框 = 原点对称展开
    ///      出来的半宽半高）；
    ///   ② **手柄**：三个顶点柄、**没有旋转柄**、别的 SelHandle 不留后门；
    ///      并真拖一遍——原点"框不动"、拖出框外会被夹回来；
    ///   ③ **屏幕**：两条轴真的上屏（轴上有墨、框里空白处没墨），打开网格墨量明显变多；
    ///   ④ **存档**：v10 往返（含网格与压感那两位）；再造一个**真·v8 老文件**
    ///      （见 MakeLegacyFile），读进来必须是"没有网格"（老文件本来就没这个概念）。
    /// </summary>
    private void AxisTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 坐标系 / 数轴自检 ===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        // 屏幕坐标 == 画布坐标（探针才好对位），和 O 段同一套做法。
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        const int ProbeHalf = 12;
        const int HasInk = 60, NoInk = 20;
        int Ink(float x, float y)
            => ScreenProbe.CountMagenta((int)x - ProbeHalf, (int)y - ProbeHalf,
                                        ProbeHalf * 2, ProbeHalf * 2);
        // ⚠ 这里原来还有一个 `WholeInk`（整块数品红像素），专门用来判"网格那些线画出来了没有"。
        // 网格改成"细一半、淡一半"之后它就不再适用（0.45 alpha 的像素不是纯品红，数不到），
        // ④ 那一段改用了**前后差分**，于是它没了用武之地——删掉，别留着当摆设。
        bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        // **探针数的是品红**，所以真机拖出来的图形也必须是品红：
        // 画笔的当前色默认是红的，不换色的话"轴上屏了吗"这一条永远是 0 像素
        // ——第一版就是这么假失败的（图形好好地画在那儿，只是颜色不对）。
        Host.Commands.SetColor(new Color4(1f, 0f, 1f, 1f));

        // ================= ① 画法：真机拖一个外框 =================
        Doc.Clear();
        Doc.ClearHistory();
        SetToolFromUi(Tool.Coordinate);
        float x0 = _virtualX + 500f, y0 = _virtualY + 420f;
        float bw = 800f, bh = 600f;
        SendMouse((int)x0, (int)y0, 0);                              SettleFrames(60);
        SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);    SettleFrames(60);
        for (int i = 1; i <= 4; i++)
        {
            SendMouse((int)(x0 + bw * i / 4f), (int)(y0 + bh * i / 4f), 0);
            SettleFrames(30);
        }
        SendMouse((int)(x0 + bw), (int)(y0 + bh), Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);

        var cs = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("坐标系：拖出一个坐标系对象",
              cs != null && cs.Kind == StrokeKind.Coordinate,
              cs == null ? "没有对象" : $"Kind={cs.Kind}");
        if (cs == null) { Console.WriteLine("  （后面几项没法验）"); _quit = true; return; }

        Check("坐标系：三个定义元素（外框两角 / 原点）",
              cs.Points.Count == 3, $"控制点 {cs.Points.Count} 个");
        // 按下点 = **原点**（用户 2026-09-19："先确定原点，原点确定以后，再把它展开"）；
        // 外框 = 原点 ± 拖动量（对称展开，所以框是拖出来的那个框的两倍大、原点在正中间）。
        var fr = cs.AxisFrameLocal();
        Check("坐标系：外框 = 原点 ± 拖动量（对称展开，±2 像素）",
              Near(fr.MinX, x0 - bw, 2f) && Near(fr.MinY, y0 - bh, 2f)
              && Near(fr.MaxX, x0 + bw, 2f) && Near(fr.MaxY, y0 + bh, 2f),
              $"框 ({fr.MinX:F0},{fr.MinY:F0})..({fr.MaxX:F0},{fr.MaxY:F0})"
              + $" 期望 ({x0 - bw:F0},{y0 - bh:F0})..({x0 + bw:F0},{y0 + bh:F0})");
        var o0 = cs.AxisOriginLocal();
        Check("坐标系：**原点就是按下的那个点**（±2 像素）",
              Near(o0.X, x0, 2f) && Near(o0.Y, y0, 2f),
              $"原点 ({o0.X:F0},{o0.Y:F0}) 期望按下点 ({x0:F0},{y0:F0})");
        Check("坐标系：一次拖拽 = 一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");

        // ================= ② 手柄：三格、无旋转柄、不留后门 =================
        Span<ShapeHandle> handles = stackalloc ShapeHandle[5];
        int hn = SelectionHandles.ShapeHandlesOf(cs, handles);
        Check("坐标系：三个顶点柄",
              hn == 3 && handles[0] == ShapeHandle.Vertex0 && handles[2] == ShapeHandle.Vertex2,
              $"{hn} 个，第 3 格 = {(hn > 2 ? handles[2].ToString() : "无")}");
        Check("坐标系：**不给旋转柄**（转歪了就不是坐标系了）",
              !SelectionHandles.RotateHandleVisible(cs), "");
        Check("坐标系：八向缩放柄不留后门（翻译成 None）",
              SelectionHandles.HandleOf(cs, SelHandle.TopLeft) == ShapeHandle.None
              && SelectionHandles.HandleOf(cs, SelHandle.Right) == ShapeHandle.None,
              $"TopLeft → {SelectionHandles.HandleOf(cs, SelHandle.TopLeft)}");
        Check("坐标系：顶点柄 ↔ SelHandle 双向对得上",
              SelectionHandles.HandleOf(cs, SelHandle.VertexD) == ShapeHandle.Vertex3
              && SelectionHandles.SelHandleOf(ShapeHandle.Vertex3) == SelHandle.VertexD,
              "");

        // ================= ③ 屏幕：轴真的上屏、框里是空的 =================
        Doc.SelectOnly(Array.Empty<Stroke>());     // 先取消选中，免得选中框和手柄混进探针
        SettleFrames(300);
        // 原点在**按下的那个点**上，所以两条轴分别过 (任意 x, y0) 与 (x0, 任意 y)。
        int onXAxis = Ink(x0 + bw * 0.2f, y0);                  // x 轴那一行、离原点远一点
        int onYAxis = Ink(x0, y0 - bh * 0.2f);                  // y 轴那一列
        int inBlank = Ink(x0 + bw * 0.5f, y0 + bh * 0.5f);      // 第一象限中间：两条轴都够不着
        Check("屏幕：x 轴那一行有墨", onXAxis > HasInk, $"{onXAxis} 像素（期望 >{HasInk}）");
        Check("屏幕：y 轴那一列有墨", onYAxis > HasInk, $"{onYAxis} 像素（期望 >{HasInk}）");
        Check("屏幕：框里的空白处没有墨（不是画了个框）",
              inBlank <= NoInk, $"{inBlank} 像素（期望 ≤{NoInk}）");

        // ---- 拖原点：**框不动**（这是老师最常用的那个动作）----
        Tool = Tool.Marquee;
        Doc.SelectOnly(new[] { cs });
        SettleFrames(300);
        var frameBefore = cs.AxisFrameLocal();
        var oHandle = SelectionHandles.ShapeHandleCanvasPosition(cs, ShapeHandle.Vertex2);
        var oTarget = new Vector2(fr.MinX + 60f, fr.MaxY - 60f);      // 拖到框的左下角附近
        bool tookO = SelectionGestureForTest(oHandle.X, oHandle.Y);
        UpdateSelectionGestureForTest(oTarget.X, oTarget.Y);
        SettleFrames(120);
        EndSelectionGestureForTest();
        SettleFrames(200);
        var frameAfter = cs.AxisFrameLocal();
        Check("拖原点：真的抓住了原点手柄", tookO, $"起点 ({oHandle.X:F0},{oHandle.Y:F0})");
        Check("拖原点：原点到了目标位置（±2 像素）",
              Near(cs.AxisOriginLocal().X, oTarget.X, 2f) && Near(cs.AxisOriginLocal().Y, oTarget.Y, 2f),
              $"原点 ({cs.AxisOriginLocal().X:F0},{cs.AxisOriginLocal().Y:F0})"
              + $" 期望 ({oTarget.X:F0},{oTarget.Y:F0})");
        Check("拖原点：**外框一分不动**（哪部分是范围、哪部分是原点，各管各的）",
              Near(frameAfter.MinX, frameBefore.MinX, 0.01f)
              && Near(frameAfter.MaxX, frameBefore.MaxX, 0.01f)
              && Near(frameAfter.MinY, frameBefore.MinY, 0.01f)
              && Near(frameAfter.MaxY, frameBefore.MaxY, 0.01f),
              $"框 {frameBefore.MinX:F0},{frameBefore.MinY:F0}..{frameBefore.MaxX:F0},{frameBefore.MaxY:F0} 未变");

        // 原点拖出框外要被夹回来（跑到框外，两条轴就都不在框里交叉了）
        {
            var far = new Vector2(fr.MaxX + 900f, fr.MaxY + 900f);
            SelectionGestureForTest(SelectionHandles.ShapeHandleCanvasPosition(cs, ShapeHandle.Vertex2).X,
                                    SelectionHandles.ShapeHandleCanvasPosition(cs, ShapeHandle.Vertex2).Y);
            UpdateSelectionGestureForTest(far.X, far.Y);
            SettleFrames(120);
            EndSelectionGestureForTest();
            SettleFrames(200);
            var ov = cs.AxisOriginLocal();
            Check("拖原点：拖出框外会被夹回框里（轴永远不会跑出框）",
                  ov.X <= fr.MaxX + 0.5f && ov.Y <= fr.MaxY + 0.5f
                  && ov.X >= fr.MinX - 0.5f && ov.Y >= fr.MinY - 0.5f,
                  $"原点 ({ov.X:F0},{ov.Y:F0})，框 ({fr.MinX:F0},{fr.MinY:F0})..({fr.MaxX:F0},{fr.MaxY:F0})");
        }

        // ---- 拖外框角：改范围 ----
        var cHandle = SelectionHandles.ShapeHandleCanvasPosition(cs, ShapeHandle.Vertex0);
        var cTarget = new Vector2(cHandle.X - 120f, cHandle.Y - 90f);
        SelectionGestureForTest(cHandle.X, cHandle.Y);
        UpdateSelectionGestureForTest(cTarget.X, cTarget.Y);
        SettleFrames(120);
        EndSelectionGestureForTest();
        SettleFrames(200);
        Check("拖外框角：外框跟着变大（±2 像素）",
              Near(cs.AxisFrameLocal().MinX, cTarget.X, 2f)
              && Near(cs.AxisFrameLocal().MinY, cTarget.Y, 2f),
              $"左上角 ({cs.AxisFrameLocal().MinX:F0},{cs.AxisFrameLocal().MinY:F0})"
              + $" 期望 ({cTarget.X:F0},{cTarget.Y:F0})");

        // ---- 紧框就是"定义点的外接 + 半笔宽"（**刻度取消之后不该再虚报那一截**）----
        {
            var wb = cs.WorldInkBounds;
            var f2 = cs.AxisFrameLocal();
            float halfPen = cs.Width * 0.5f;
            // 外框宽 + 半笔宽 ×2 = 紧框宽（±2 像素：渲染那边还会各留一点抗锯齿余量）。
            float needW = (f2.MaxX - f2.MinX) + halfPen * 2f;
            Check("紧框：等于「外框 + 半笔宽」（没有刻度就不该再往外多算）",
                  Near(wb.MaxX - wb.MinX, needW, 2.5f),
                  $"紧框宽 {wb.MaxX - wb.MinX:F1}，期望 {needW:F1}"
                  + $"（外框 {f2.MaxX - f2.MinX:F0} + 2×{halfPen:F1}）");
        }

        // ================= ④ 网格：开着比关着**屏幕上真的多了那些格线** =================
        //
        // ⚠ **判据 2026-09-24 换过**：网格改成"细一半、淡一半"之后，它的像素是 0.45 alpha
        // 混过背景的——**不再是纯品红**，`CountMagenta` 数不到它（实测"关 18882 → 开 18925"，
        // 只多出 43 个像素，看着像"格线没画出来"）。所以改照 `--paneltest` 那套
        // **前后差分**（同一块区域开 / 关各截一次，数"变了的像素"）：
        // 这比"数某种颜色"更贴问题本身——要问的就是"那些格线有没有上屏"。
        {
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var g = MakeCoordinateForTest(x0, y0 + 300f, 400f, 300f, grid: false);
            Doc.AddStroke(g);
            Doc.SelectOnly(new[] { g });
            Doc.InvalidateAll();
            SettleFrames(400);
            var wb = g.WorldInkBounds;
            int gx0 = (int)wb.MinX - 20, gy0 = (int)wb.MinY - 20;
            int gw0 = (int)(wb.MaxX - wb.MinX) + 40, gh0 = (int)(wb.MaxY - wb.MinY) + 40;
            var shotOff = ScreenProbe.CaptureRegion(gx0, gy0, gw0, gh0);

            int changed = ToggleSelectionGrid();       // 选中了坐标系 → 改的是它
            SettleFrames(400);
            var shotOn = ScreenProbe.CaptureRegion(gx0, gy0, gw0, gh0);
            int gridPix = ScreenProbe.DiffCount(shotOff, shotOn);
            Check("网格：选中坐标系时点那一下改的是它（返回 1）", changed == 1, $"返回 {changed}");
            Check("网格：开关落到对象身上", g.Grid, $"Grid = {g.Grid}");
            Check("网格：开了之后**屏幕上真的多出格线**（前后差分，关 → 开）",
                  gridPix > 300, $"{gridPix} 个像素变了");
            Doc.Undo();
            SettleFrames(200);
            Check("网格：一步撤销能回到没有网格", !g.Grid, $"Grid = {g.Grid}");
        }

        // ================= ⑤ 数轴：水平锁 + 右端箭头 =================
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.NumberLine);
            float nx = _virtualX + 500f, ny = _virtualY + 1400f, nl = 900f;
            SendMouse((int)nx, (int)(ny + 300f), 0);                     SettleFrames(60);
            SendMouse((int)nx, (int)(ny + 300f), Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                // 故意**斜着**拖：数轴必须还是水平的（y 钉在按下点那一行）。
                SendMouse((int)(nx + nl * i / 4f), (int)(ny + 300f - 120f * i / 4f), 0);
                SettleFrames(30);
            }
            SendMouse((int)(nx + nl), (int)(ny + 180f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);

            var nl0 = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("数轴：拖出一个数轴对象",
                  nl0 != null && nl0.Kind == StrokeKind.NumberLine,
                  nl0 == null ? "没有对象" : $"Kind={nl0?.Kind}");
            if (nl0 != null)
            {
                Check("数轴：两个定义元素（左端 / 右端）",
                      nl0.Points.Count == 2, $"控制点 {nl0.Points.Count} 个");
                Check("数轴：斜着拖也还是水平的（两个点 y 相同）",
                      Near(nl0.Points[0].Y, ny + 300f, 2f) && Near(nl0.Points[1].Y, ny + 300f, 2f),
                      $"两个点 y = {nl0.Points[0].Y:F0} / {nl0.Points[1].Y:F0}"
                      + $"（按下点 {ny + 300f:F0}）");
                Span<ShapeHandle> nlh = stackalloc ShapeHandle[5];
                Check("数轴：两个顶点柄（没有刻度之后不再有零点/单位长度两个柄）",
                      SelectionHandles.ShapeHandlesOf(nl0, nlh) == 2
                      && nlh[0] == ShapeHandle.Vertex0 && nlh[1] == ShapeHandle.Vertex1,
                      $"{SelectionHandles.ShapeHandlesOf(nl0, nlh)} 个");

                // 拖右端到斜上方：x 要走过去，y 一动不动。
                Tool = Tool.Marquee;
                Doc.SelectOnly(new[] { nl0 });
                SettleFrames(300);
                var rHandle = SelectionHandles.ShapeHandleCanvasPosition(nl0, ShapeHandle.Vertex1);
                var rTarget = new Vector2(rHandle.X - 150f, rHandle.Y - 260f);
                SelectionGestureForTest(rHandle.X, rHandle.Y);
                UpdateSelectionGestureForTest(rTarget.X, rTarget.Y);
                SettleFrames(120);
                EndSelectionGestureForTest();
                SettleFrames(200);
                Check("数轴：拖右端到斜上方 → x 跟过去、y 一动不动",
                      Near(nl0.Points[1].X, rTarget.X, 2f)
                      && Near(nl0.Points[1].Y, rTarget.Y + 260f, 0.01f),
                      $"右端 ({nl0.Points[1].X:F0},{nl0.Points[1].Y:F0})"
                      + $" 期望 ({rTarget.X:F0},{rTarget.Y + 260f:F0})");

                Doc.SelectOnly(Array.Empty<Stroke>());
                SettleFrames(300);
                int nlInk = Ink(nx + nl * 0.5f, ny + 300f);
                Check("数轴：线本身有墨上屏", nlInk > HasInk, $"{nlInk} 像素");
            }
        }

        // ================= ⑤ 格距手柄（**开网格才有**）：拖动 / 夹范围 / 一步撤销 ==========
        //
        // 用户 2026-09-24："那个坐标系坐标原点现在不是有一个空心点吗？选中的时候它是可以拖动的。
        // 那么我打算那个第一个方格那个格点，它也做一个空心点，这样的话我就可以通过拖动那个点
        // 来改变这个方格的大小。"
        // 落点 = **第 4 颗手柄**，位置 = 第一象限第一个格子的外角 = 原点 ＋ (格距, −格距)。
        // 盯五件事：① 只在开网格时才有；② 位置真的在那个格点上；③ 拖它只改格距、
        // **三个控制点一个字不动**；④ 一步撤销回得去；⑤ 拖到天边也只夹到外框短边。
        {
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var gh = MakeCoordinateForTest(x0, y0 + 1600f, 500f, 400f, grid: true);
            gh.AxisGridStep = 0f;                       // 从「自动」那一档起手
            Doc.AddStroke(gh);
            Doc.SelectOnly(new[] { gh });
            SettleFrames(300);

            float autoStep = gh.AxisGridStepLocal();
            Span<ShapeHandle> hbuf = stackalloc ShapeHandle[5];
            int hn2 = SelectionHandles.ShapeHandlesOf(gh, hbuf);
            Check("格距手柄：开网格之后是**四颗**，第 4 颗是 Vertex3",
                  hn2 == 4 && hbuf[3] == ShapeHandle.Vertex3, $"{hn2} 颗");
            var hpos = SelectionHandles.ShapeHandleCanvasPosition(gh, ShapeHandle.Vertex3);
            var gridOrg = gh.AxisOriginLocal();
            Check("格距手柄：落在**第一象限第一个格子的外角**（右上：x 加、y 减）",
                  Near(hpos.X, gridOrg.X + autoStep, 1.5f) && Near(hpos.Y, gridOrg.Y - autoStep, 1.5f),
                  $"手柄 ({hpos.X:F0},{hpos.Y:F0}) 期望 ({gridOrg.X + autoStep:F0},{gridOrg.Y - autoStep:F0})");

            // 拖到「每格大一半」的位置：手柄本来在原点 +(s,−s)，目标放到原点 +(1.5s,−1.5s)
            var pts0 = new[]
            {
                new Vector2(gh.Points[0].X, gh.Points[0].Y),
                new Vector2(gh.Points[1].X, gh.Points[1].Y),
                new Vector2(gh.Points[2].X, gh.Points[2].Y),
            };
            int undoBeforeDrag = Doc.UndoDepth;   // 拖之前现取底数
            SelectionGestureForTest(hpos.X, hpos.Y);
            UpdateSelectionGestureForTest(hpos.X + autoStep * 0.5f, hpos.Y - autoStep * 0.5f);
            SettleFrames(120);
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check("格距手柄：拖完格距就是拖到的那一档（±3%）",
                  Near(gh.AxisGridStepLocal(), autoStep * 1.5f, autoStep * 0.03f),
                  $"格距 {gh.AxisGridStepLocal():F1}，期望 {autoStep * 1.5f:F1}");
            Check("格距手柄：**三个控制点一个字都没动**（它不是控制点）",
                  Near(gh.Points[0].X, pts0[0].X, 0.01f) && Near(gh.Points[1].Y, pts0[1].Y, 0.01f)
                  && Near(gh.Points[2].X, pts0[2].X, 0.01f) && Near(gh.Points[2].Y, pts0[2].Y, 0.01f),
                  $"原点 ({gh.Points[2].X:F0},{gh.Points[2].Y:F0})");
            // 底数由「拖之前」现取：AddStroke 自己也占一步撤销，写死数字会随功能移位而假红
            Check("格距手柄：一次拖拽 = 一步撤销", Doc.UndoDepth == undoBeforeDrag + 1,
                  $"撤销栈 {undoBeforeDrag} → {Doc.UndoDepth} 步");

            Doc.Undo();
            SettleFrames(120);
            Check("格距手柄：撤销回到「自动」那一档（0）",
                  Near(gh.AxisGridStep, 0f, 0.001f), $"格距字段 {gh.AxisGridStep:F2}（期望 0 = 自动）");
            Doc.Redo();
            SettleFrames(120);

            // 拖到很远处：只能夹到「外框短边」为止（再大就一个格子都看不见了）
            var hpos2 = SelectionHandles.ShapeHandleCanvasPosition(gh, ShapeHandle.Vertex3);
            SelectionGestureForTest(hpos2.X, hpos2.Y);
            UpdateSelectionGestureForTest(hpos2.X + 5000f, hpos2.Y - 5000f);
            SettleFrames(120);
            EndSelectionGestureForTest();
            SettleFrames(200);
            var fr2 = gh.AxisFrameLocal();
            float shortSide = MathF.Min(fr2.MaxX - fr2.MinX, fr2.MaxY - fr2.MinY);
            Check("格距手柄：拖到天边也只夹到「外框短边」为止",
                  gh.AxisGridStepLocal() <= shortSide + 0.5f,
                  $"格距 {gh.AxisGridStepLocal():F0}，外框短边 {shortSide:F0}");

            // 关掉网格 → 第 4 颗必须消失（没格子就没有「第一个格子」）
            gh.Grid = false;
            gh.InvalidateMetrics();
            Doc.SelectOnly(new[] { gh });
            SettleFrames(200);
            Check("格距手柄：**关掉网格就没有第 4 颗**（不留看不见却点得到的死元素）",
                  SelectionHandles.ShapeHandlesOf(gh, hbuf) == 3,
                  $"{SelectionHandles.ShapeHandlesOf(gh, hbuf)} 颗");
        }

        // ================= ⑥ 存档：v9 往返 + 真·v8 老文件 =================
        {
            Doc.Clear();
            Doc.ClearHistory();
            var a1 = MakeCoordinateForTest(x0, y0, bw, bh, grid: true);
            // v24：拖过格距的那些，格距要跟着存档走（**不是 0**，0 是"自动"、
            // 和"拖过"分不开，这里特意挑一个非整、非默认的数）。
            a1.AxisGridStep = 37.5f;
            var a2 = MakeNumberLineForTest(x0, y0 + 800f, 600f);
            Doc.AddStroke(a1);
            Doc.AddStroke(a2);

            byte[] blob = InkSerializer.Save(Doc);
            var back = new InkDocument();
            InkSerializer.LoadInto(back, blob);
            Check("存档：读回两条都在", back.Strokes.Count == 2, $"读回 {back.Strokes.Count} 条");
            Check("存档：坐标系读回来还是坐标系、网格位也在",
                  back.Strokes.Count > 0 && back.Strokes[0].Kind == StrokeKind.Coordinate
                  && back.Strokes[0].Grid && back.Strokes[0].Points.Count == 3,
                  back.Strokes.Count > 0
                      ? $"{back.Strokes[0].Kind}，网格 {back.Strokes[0].Grid}，"
                        + $"{back.Strokes[0].Points.Count} 个点"
                      : "条数不对");
            Check("存档：数轴读回来还是数轴（第二条，能验出闸门错位）",
                  back.Strokes.Count > 1 && back.Strokes[1].Kind == StrokeKind.NumberLine
                  && back.Strokes[1].Points.Count == 2,
                  back.Strokes.Count > 1 ? $"{back.Strokes[1].Kind}" : "条数不对");
            // v24 那一位：格距往返（0 = 自动也算"带回来了"，所以这里挑的是 37.5）
            Check("存档：拖过的**格距**读回来原样（v24 那一位）",
                  back.Strokes.Count > 0 && Near(back.Strokes[0].AxisGridStep, 37.5f, 0.01f),
                  back.Strokes.Count > 0 ? $"{back.Strokes[0].AxisGridStep:F1}（期望 37.5）" : "条数不对");

            // 真·v8 老文件（见 MakeLegacyFile）：读进来必须是"没有网格"。
            // 砍 4 位 = v9 网格 ＋ v10 压感 ＋ v11 朝向 ＋ v12 渐近线（**v13 没加字段**）。
            var one = new InkDocument();
            one.AddStroke(MakeCoordinateForTest(x0, y0, bw, bh, grid: true));
            byte[] v8 = MakeLegacyFile(one, 8, 4);
            var old = new InkDocument();
            bool oldOk = true;
            try { InkSerializer.LoadInto(old, v8); } catch (Exception ex) { oldOk = false; Console.WriteLine("    " + ex.Message); }
            Check("真·v8 老文件读得进，网格是关的（老文件没这个概念）",
                  oldOk && old.Strokes.Count == 1 && !old.Strokes[0].Grid
                  && old.Strokes[0].Points.Count == 3,
                  oldOk ? $"{old.Strokes.Count} 条，网格 {old.Strokes[0].Grid}" : "读取抛异常");

            // 真·v23 老文件（**没有格距这一位**）：把 v24 追加的那 4 个字节（一个 float）砍掉。
            // 期望 = 格距读成 **0 = 自动**，也就是加这一位之前的行为 → **不用迁移**。
            var doc23 = new InkDocument();
            var g23 = MakeCoordinateForTest(x0, y0, bw, bh, grid: true);
            g23.AxisGridStep = 88f;                 // 先塞一个值：砍掉之后必须读不到它
            doc23.AddStroke(g23);
            byte[] v23blob = MakeLegacyFile(doc23, 23, 4);
            var old23 = new InkDocument();
            bool ok23 = true;
            try { InkSerializer.LoadInto(old23, v23blob); }
            catch (Exception ex) { ok23 = false; Console.WriteLine("    " + ex.Message); }
            Check("真·v23 老文件读得进：格距 = 0（自动）——加这一位之前的行为，不用迁移",
                  ok23 && old23.Strokes.Count == 1 && old23.Strokes[0].Grid
                  && Near(old23.Strokes[0].AxisGridStep, 0f, 0.001f)
                  && old23.Strokes[0].Points.Count == 3,
                  ok23 ? $"{old23.Strokes.Count} 条，格距 {old23.Strokes[0].AxisGridStep:F1}"
                       + $"、网格 {old23.Strokes[0].Grid}"
                       : "读取抛异常");
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 自检用：造一个**真·老版本**存档。
    ///
    /// 原理：从 v8 起，每抬一版基本就是"每条笔画末尾多 1 个字节"（v8 线型 / v9 网格 /
    /// v10 压感 / v12 渐近线），所以"砍掉那几位 + 把版本号改回去"得到的就是货真价实的老文件。
    ///
    /// **为什么按"砍几位"传参、不写死**：2026-09-20 加 v10 时，三处"砍 1 字节"的自检
    /// 一起静默失效——它们各留了一两个字节的尾巴，而读端不检查"流有没有读完"，
    /// 于是照样通过、却不再是在验老文件（这正是仓库里那条教训：
    /// "自检里写死常量会随功能移位而静默失效"）。
    ///
    /// **但 v13 破了"版本号之差 = 砍几位"这条公式**：它只改了抛物线第二个点的含义、
    /// 没加字段。所以砍几位改成**显式传**（见下面 <paramref name="droppedBytes"/>），
    /// 不能让调用方去猜。
    /// </summary>
    /// <summary>
    /// 自检用：把当前格式的存档"退化"成某个老版本的文件。
    ///
    /// <paramref name="droppedBytes"/> = **从这个老版本到现在，每条笔画后面一共追加了几位**
    /// ——**它不等于版本号之差**：v13 只改了抛物线第二个点的**含义**、没加字段，
    /// 所以"v12 的文件"和"v13 的文件"**字节数一模一样**，要传 0。
    /// （v12 则比 v13 少一位：少了渐近线那一个字节。）
    /// </summary>


    /// <summary>
    /// 指数复制压力测验：画一笔，然后反复"全选 + 复制"（每次翻倍），
    /// 每轮打印对象数与内存。就是用户按 Ctrl+A / Ctrl+D 那个动作的等价脚本。
    ///
    /// 它能回答两件事：
    ///   · 内存随对象数量怎么涨 —— 换算成"每条多少 KB"
    ///   · 涨到多少开始变慢 —— 看每轮耗时（含一次完整的绘制）
    ///
    /// 这也是**和 WPF 版做同一个动作做对照**用的脚本。
    /// </summary>
    private void DupTest(int rounds)
    {
        Doc.Clear();
        Doc.ClearHistory();

        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.9f, 0.2f, 0.2f, 1f),
            Width = 4f,
        };
        for (int i = 0; i < 20; i++) s.AddPoint(400 + i * 6f, 500 + MathF.Sin(i * 0.3f) * 40f, 0.5f, i);
        Doc.AddStroke(s);
        SettleFrames(150);

        Console.WriteLine();
        Console.WriteLine("=== 指数复制压力（Ctrl+A + Ctrl+D 的等价动作）===");
        Console.WriteLine("   轮次 |   对象数 |   工作集 |  私有   | 本轮耗时 | 每对象");
        Console.WriteLine("  ------|----------|----------|---------|----------|--------");

        double baseMb = WorkingSetMb();
        var clock = Stopwatch.StartNew();
        for (int r = 1; r <= rounds; r++)
        {
            double t0 = clock.Elapsed.TotalMilliseconds;

            Doc.Selected.Clear();
            foreach (var st in Doc.Strokes) Doc.Selected.Add(st);   // = Ctrl+A
            bool ok = Doc.DuplicateSelected();                       // = Ctrl+D
            _dirty = true;
            SettleFrames(30);

            double dt = clock.Elapsed.TotalMilliseconds - t0;
            double ws = WorkingSetMb();
            int count = Doc.Strokes.Count;
            Console.WriteLine($"  {r,6} | {count,8} | {ws,6:F0} MB | {PrivateMb(),5:F0} MB | "
                            + $"{dt,6:F0} ms | {(ws - baseMb) / Math.Max(1, count) * 1024:F1} KB");
            if (!ok)
            {
                Console.WriteLine("  到上限被拒绝——护栏生效，程序仍然可响应");
                break;
            }
        }
        _quit = true;
    }


    /// <summary>
    /// 内存生命周期：一条一条问清"删掉之后内存去哪了"。
    ///
    /// 每个阶段都量提交大小 / 工作集 / 显存，并报告撤销栈此刻还引用着多少条笔画。
    /// 这样"是不是撤销的原因"就有一个数字答案，而不是靠猜。
    /// </summary>
    /// <summary>
    /// **"只补画新笔画"这条快路径的正确性自检。**
    ///
    /// 它省掉的是"整块重画"，而整块重画恰好也是唯一能擦掉旧墨的机会——
    /// 一旦判断错了（比如结构变了还去补画），屏幕上就会留下早该消失的墨，
    /// 这是那种"平时看不出来、演示时突然出现"的 bug。所以这里每一步都用
    /// **屏幕像素数**来判，不看内部状态：
    ///   新写的在 → 旧的还在 → 擦了就没了 → 撤销又回来 → 移走两边都对 → 清空全没。
    /// </summary>
    private void AppendPathTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 补画快路径正确性（只补画新笔画，不重画整块）===");

        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {what,-30} {detail}");
        }

        int ax = _virtualX + 600, ay = _virtualY + 380, aw = 520, ah = 200;
        int bx = _virtualX + 600, by = _virtualY + 860;

        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        RenderAll();
        SettleFrames(250);

        int baseA = ScreenProbe.CountMagenta(ax, ay, aw, ah);
        int baseB = ScreenProbe.CountMagenta(bx, by, aw, ah);
        Check("环境干净（背景没有洋红）", baseA + baseB < 100, $"A {baseA} / B {baseB} 像素");

        void AddLine(float cx, float cy)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 20f * DpiScale,
            };
            s.AddPoint(cx - 160f * DpiScale, cy, 1f, 0);
            s.AddPoint(cx, cy, 1f, 0);
            s.AddPoint(cx + 160f * DpiScale, cy, 1f, 0);
            Doc.AddStroke(s);
            SettleFrames(250);
        }

        AddLine(ax + aw * 0.5f, ay + ah * 0.5f);
        Check("写入第一笔（走补画路径）>", ScreenProbe.CountMagenta(ax, ay, aw, ah) > 300,
            $"A {ScreenProbe.CountMagenta(ax, ay, aw, ah)} 像素");

        AddLine(bx + aw * 0.5f, by + ah * 0.5f);
        int afterB_A = ScreenProbe.CountMagenta(ax, ay, aw, ah);
        int afterB_B = ScreenProbe.CountMagenta(bx, by, aw, ah);
        Check("再写第二笔，第一笔没被弄丢", afterB_A > 300 && afterB_B > 300,
            $"A {afterB_A} / B {afterB_B} 像素");

        // 擦掉 A：这是"结构变化"，必须整块重画（补画清单要作废）
        Doc.BeginErase();
        Doc.EraseAt(ax + aw * 0.5f, ay + ah * 0.5f, 120f * DpiScale);
        Doc.EndErase();
        SettleFrames(300);
        int erasedA = ScreenProbe.CountMagenta(ax, ay, aw, ah);
        int erasedB = ScreenProbe.CountMagenta(bx, by, aw, ah);
        Check("擦掉 A：A 没了、B 还在", erasedA < 100 && erasedB > 300, $"A {erasedA} / B {erasedB} 像素");

        Doc.Undo();
        SettleFrames(300);
        int undoA = ScreenProbe.CountMagenta(ax, ay, aw, ah);
        int undoB = ScreenProbe.CountMagenta(bx, by, aw, ah);
        Check("撤销：A 回来、B 不受影响", undoA > 300 && undoB > 300, $"A {undoA} / B {undoB} 像素");

        // 撤销"新增"：把刚写的那一笔撤掉，它必须立刻从屏幕上消失
        AddLine(_virtualX + 1700, _virtualY + 1300);
        SettleFrames(250);
        int cxr = _virtualX + 1500, cyr = _virtualY + 1200, cwr = 520, chr = 200;
        int addC = ScreenProbe.CountMagenta(cxr, cyr, cwr, chr);
        Doc.Undo();
        SettleFrames(300);
        int undoAdd = ScreenProbe.CountMagenta(cxr, cyr, cwr, chr);
        Check("撤销刚写的一笔：屏幕上也要消失", addC > 300 && undoAdd < 100, $"写后 {addC} / 撤销后 {undoAdd}");

        // 全选整体移动：老位置要擦干净、新位置要出现
        Doc.Selected.Clear();
        foreach (var st in Doc.Strokes) Doc.Selected.Add(st);
        Doc.ApplyTransform(Matrix3x2.CreateTranslation(0, 260f * DpiScale));
        SettleFrames(350);
        int movedOldA = ScreenProbe.CountMagenta(ax, ay, aw, ah);
        int movedNewB = ScreenProbe.CountMagenta(bx - _virtualX + _virtualX, by, aw, ah);
        Check("整体下移：老位置擦干净、新位置有墨", movedOldA < 100 && movedNewB > 300,
            $"老位置 {movedOldA} / 新位置 {movedNewB}");

        Doc.Clear();
        SettleFrames(350);
        int cleared = ScreenProbe.CountMagenta(ax, ay, aw, ah) + ScreenProbe.CountMagenta(bx, by, aw, ah);
        Check("清空：屏幕上全都没有了", cleared < 150, $"残留 {cleared} 像素");

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }


    /// <summary>
    /// 自动存档 / 崩溃恢复自检（开发期）。
    ///
    /// 它是"一节课的板书会不会丢"这条链子的唯一自动化验证：
    ///   ① 存了能原样读回来（笔画数/颜色/粗细/身份一致）；
    ///   ② **没变就不写**（靠文档版本号节流，不然每 15 秒白写一遍全量）；
    ///   ③ **清空之后存的是空的**（不然重启会把擦掉的东西又变回来）；
    ///   ④ 坏文件读不出来时**不许影响启动**（从空白开始，只提示）；
    ///   ⑤ 存档落在 LOCALAPPDATA，**不是 TEMP**（TEMP 会被清理工具删掉，
    ///      而"断电一节课"恰恰要跨重启活下来）。
    ///
    /// 自检全程用临时路径（`Recovery.AutoSavePathOverride`），不碰用户真正的板书。
    /// </summary>
    private void RecoveryTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 自动存档/崩溃恢复自检 ===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        string path = Recovery.AutoSavePath;
        Recovery.DeleteAuto();

        Check("存档落在 LOCALAPPDATA（不是 TEMP）",
              path.Contains("Local", StringComparison.OrdinalIgnoreCase)
              || Recovery.AutoSavePathOverride != null,
              path);

        // ⓪ **默认档：不接上次的板书，也不写那个文件**（用户 2026-09-17）
        //
        // "退出以后再打开不用恢复墨迹吧……我觉得默认不恢复墨迹。"
        // 理由：教室机器是公用的，一开机铺满上一节课的板书不合理。
        // 这里先按"上次留下了一份存档"造现场，再走一遍**启动时那条真路径**。
        {
            SetUiPref("restoreInk", null);              // 默认：没有这一项
            Doc.Clear();
            Doc.ClearHistory();
            var keep = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 5f * DpiScale };
            keep.AddPoint(300, 300, 1f, 0); keep.AddPoint(500, 320, 1f, 1);
            Doc.AddStroke(keep);
            AutoSaveNow();                              // 硬盘上留下一份"上次的板书"
            Doc.Clear();
            Doc.ClearHistory();

            RestoreAutoSaveForTest();                   // 启动时走的就是这一条
            Check("默认（没设偏好）：启动**不接**上次的板书",
                  Doc.Strokes.Count == 0 && !RestoreInkOnStartup,
                  $"偏好 = {RestoreInkOnStartup}，读回 {Doc.Strokes.Count} 笔");

            // 不写：改一下板书、跑够时间，自动存档次数不该动
            SetAutoSaveIntervalForTest(80);
            int n0 = AutoSaveCount;
            var s0 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 5f * DpiScale };
            s0.AddPoint(200, 200, 1f, 0); s0.AddPoint(260, 230, 1f, 1);
            Doc.AddStroke(s0);
            var sw0 = Stopwatch.StartNew();
            while (sw0.ElapsedMilliseconds < 260) { PumpMessages(); RenderAll(); }
            Check("默认（没设偏好）：板书变了也不写档（不留没人读的文件）",
                  AutoSaveCount == n0, $"{n0} → {AutoSaveCount} 次");

            // 打开偏好：下面几条按"要恢复"的老路径验
            SetUiPref("restoreInk", "1");
            Check("打开偏好之后：读得到这一项", RestoreInkOnStartup, "restoreInk = 1");
        }

        // ① 存 → 读回来，逐项一致
        Doc.Clear();
        Doc.ClearHistory();
        var a = new Stroke { Tool = Tool.Pen, Color = new Color4(0.13f, 0.70f, 0.33f, 1f), Width = 7f * DpiScale };
        a.AddPoint(400, 400, 1f, 0); a.AddPoint(700, 500, 1f, 1);
        Doc.AddStroke(a);
        var b = new Stroke { Tool = Tool.Highlighter, Color = new Color4(1f, 0.85f, 0.15f, 0.32f), Width = 24f * DpiScale };
        b.AddPoint(500, 700, 1f, 0); b.AddPoint(900, 760, 1f, 1);
        Doc.AddStroke(b);

        AutoSaveNow();
        long size = new FileInfo(path).Length;
        var fresh = new InkDocument();
        InkSerializer.LoadInto(fresh, Recovery.LoadAuto());
        bool same = fresh.Strokes.Count == 2
                 && MathF.Abs(fresh.Strokes[0].Color.G - 0.70f) < 0.02f
                 && MathF.Abs(fresh.Strokes[1].Width - 24f * DpiScale) < 0.5f
                 && fresh.Strokes[1].Tool == Tool.Highlighter;
        Check("存了能原样读回来", same,
              $"暂存 {size} 字节 → 读回 {fresh.Strokes.Count} 笔，第 2 笔 {fresh.Strokes[1].Width:F0} 物理像素");

        // ② 没变就不写
        SetAutoSaveIntervalForTest(80);
        int before = AutoSaveCount;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 260) { PumpMessages(); RenderAll(); }
        int afterIdle = AutoSaveCount;
        Check("板书没变就不写", afterIdle == before, $"{before} → {afterIdle} 次");

        // 变一下 → 到点就该写
        var c = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 4f * DpiScale };
        c.AddPoint(300, 300, 1f, 0); c.AddPoint(360, 330, 1f, 1);
        Doc.AddStroke(c);
        sw.Restart();
        while (sw.ElapsedMilliseconds < 260) { PumpMessages(); RenderAll(); }
        Check("板书变了就到点写一次", AutoSaveCount > afterIdle, $"{afterIdle} → {AutoSaveCount} 次");

        // ③ 清空之后存的是空的
        Doc.Clear();
        Doc.ClearHistory();
        AutoSaveNow();
        var empty = new InkDocument();
        InkSerializer.LoadInto(empty, Recovery.LoadAuto());
        Check("清空之后存的是空文档", empty.Strokes.Count == 0, $"读回 {empty.Strokes.Count} 笔");

        // ④ 坏文件不影响启动
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        Doc.Clear();
        Doc.ClearHistory();
        bool threw = false;
        try { RestoreAutoSaveForTest(); } catch { threw = true; }
        Check("坏文件不影响启动（从空白开始）", !threw && Doc.Strokes.Count == 0,
              $"抛异常 = {threw}，读回 {Doc.Strokes.Count} 笔");

        // ⑤ 真存档能"重启接上"（走启动那条路）
        Doc.Clear();
        Doc.ClearHistory();
        var d = new Stroke { Tool = Tool.Pen, Color = new Color4(0.1f, 0.4f, 0.9f, 1f), Width = 6f * DpiScale };
        d.AddPoint(200, 200, 1f, 0); d.AddPoint(600, 260, 1f, 1);
        Doc.AddStroke(d);
        AutoSaveNow();
        Doc.Clear();
        Doc.ClearHistory();
        RestoreAutoSaveForTest();
        Check("重开接上上次的板书", Doc.Strokes.Count == 1, $"读回 {Doc.Strokes.Count} 笔");

        Recovery.DeleteAuto();
        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }

    /// <summary>
    /// 离屏把界面画下来存成 BMP。返回 true = 出图成功。
    ///
    /// 这条路**不截屏**：它让界面渲染到自己的位图上。
    /// 好处是锁屏 / 远程 / 这台机器上没人看着的时候照样能出图，
    /// 而且图里只有界面本身，没有桌面、任务栏、别的窗口。
    /// </summary>

}
