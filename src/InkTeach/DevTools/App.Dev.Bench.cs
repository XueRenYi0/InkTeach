using System.Buffers;
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
    private void BallProbe(string prefix)
    {
        SetUiPref("hide", "1");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null) { Console.WriteLine("  界面没挂上"); ExitCode = 1; _quit = true; return; }

        ui.SetExpandForTest(0f);            // 收起态（球）
        var scr = ui.ScreenForTest;
        const float gap = 2f, ball = 48f;
        var spots = new (string Name, float X, float Y)[]
        {
            ("left-mid",    scr.MinX + gap,               (scr.MinY + scr.MaxY) * 0.5f),
            ("right-mid",   scr.MaxX - gap - ball,        (scr.MinY + scr.MaxY) * 0.5f),
            ("bottom-left", scr.MinX + gap,               scr.MaxY - gap - ball),
            ("bottom-mid",  (scr.MinX + scr.MaxX) * 0.5f - ball * 0.5f, scr.MaxY - gap - ball),
            ("bottom-right",scr.MaxX - gap - ball,        scr.MaxY - gap - ball),
            ("left-low",    scr.MinX + gap,               scr.MaxY - 80f),
        };

        foreach (var (name, x, y) in spots)
        {
            foreach (int peek in new[] { 1, 0 })
            {
                ui.SetAnchorForTest(new System.Numerics.Vector2(x, y));
                ui.ForcePeekForTest(peek);
                SettleFrames(250);
                var b = ui.QueryBounds();
                var shot = new RectF
                {
                    MinX = b.MinX - 10f, MinY = b.MinY - 10f,
                    MaxX = b.MaxX + 10f, MaxY = b.MaxY + 10f,
                };
                string path = $"{prefix}-{name}-{(peek == 1 ? "show" : "hide")}.bmp";
                OffscreenShot(path, shot);
                Console.WriteLine($"  {name,-13} peek={peek} 锚=({x:F0},{y:F0}) "
                                  + $"占用=({b.MinX:F0},{b.MinY:F0})-({b.MaxX:F0},{b.MaxY:F0}) "
                                  + $"球高={b.MaxY - b.MinY:F0} {path}");
            }
        }
        _quit = true;
    }

    /// <summary>
    /// `--replayshow &lt;图&gt;`：回放控制条的摆样（排版好不好看自检判不了，只能看图）。
    /// 铺几笔 → 开始回放 → 拖到快一半 → 截控制条那一块（带一点周围）。
    /// 看图用：按钮间距、倍速高亮、进度滑钮、读数、✕。
    /// </summary>
    private void ReplayShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        Doc.ResetToSinglePage();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Tool = Tool.Pen;
        SettleFrames(150);

        void AddInk(float x, float y, float dx, float t0, float t1)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.90f, 0.20f, 0.20f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s.AddPoint(x + dx * k / 4f, y + k * 8f, 0.3f + 0.15f * k, t0 + (t1 - t0) * k / 4f);
            Doc.AddStroke(s);
        }
        for (int i = 0; i < 6; i++) AddInk(300f + i * 40f, 260f + i * 70f, 420f, i * 700f, i * 700f + 600f);
        SettleFrames(200);

        if (!StartReplayForTest())
        {
            Console.WriteLine("  摆样失败：回放没起来（没有可见笔迹？）");
            ExitCode = 1; _quit = true; return;
        }
        ReplaySeekForTest(ReplayTotalForTest * 0.45f);   // 停在"正在长"那儿
        ReplaySetSpeedForTest(2f);
        SettleFrames(300);

        var bar = ReplayBarRectForTest;
        int sx, sy, sw, sh;
        if (Environment.GetCommandLineArgs().Contains("--full"))
        {
            // 整屏：看"已出完的（烘进内容层）＋ 正在长的那一条（前缀）"对不对
            sx = _virtualX; sy = _virtualY; sw = _virtualW; sh = _virtualH;
        }
        else
        {
            int pad = (int)(18 * DpiScale);
            sx = (int)bar.MinX - pad; sy = (int)bar.MinY - pad;
            sw = (int)(bar.MaxX - bar.MinX) + pad * 2;
            sh = (int)(bar.MaxY - bar.MinY) + pad * 2;
        }
        bool ok = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
        Console.WriteLine(ok ? $"  已保存 {path}（{sw}×{sh}）" : "  截屏失败");
        StopReplay("摆样收尾");
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }
    private static IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
    private int DrawAndCountHorizontal(float x, float y, float len, float width)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = width,
        };
        s.AddPoint(x, y, 1f, 0);
        s.AddPoint(x + len, y, 1f, 1);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(350);
        return ScreenProbe.CountMagenta((int)x, (int)(y - width), (int)len, (int)(width * 2f));
    }

    /// <summary>画一条粗竖线并数它的墨像素。竖线的**中心线**画在 x 上。</summary>
    private int DrawAndCountVertical(float x, float y, float len, float width)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = width,
        };
        s.AddPoint(x, y, 1f, 0);
        s.AddPoint(x, y + len, 1f, 1);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(350);
        return ScreenProbe.CountMagenta((int)(x - width), (int)y, (int)(width * 2f), (int)len);
    }
    private bool SkipIfNoSyntheticInput(string what)
    {
        if (ProbeSyntheticInput(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.4f)) return false;
        Console.WriteLine($"  环境：合成输入不可用 → SKIP: {what}跳过（多半是别的程序正占着鼠标；松开后重跑即可）");
        return true;
    }

    private bool ProbeSyntheticInput(float cx, float cy)
    {
        Tool = Tool.Pen;
        int before = Doc.Strokes.Count;
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);
        SendMouse((int)(cx + 60), (int)(cy + 30), 0);
        SettleFrames(80);
        SendMouse((int)(cx + 60), (int)(cy + 30), Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        // **先数再清**：下面那个 Clear() 会把刚画出来的这一笔也清掉，
        // 以前在这里直接 `return Doc.Strokes.Count > before`，于是永远返回 false
        // ——所有靠它把关的用例（残影、橡皮、截图……）其实一直在静默 SKIP，
        // 而屏幕上的日志却写着"合成输入不可用"，把锅推给了环境。
        // 判据必须取 Clear 之前的值。
        int after = Doc.Strokes.Count;
        Doc.Clear();
        Doc.ClearHistory();
        return after > before;
    }
    private Thread StartInjector(float x0, float y0, float x1, float y1, double hz, double ms)
    {
        var th = new Thread(() =>
        {
            SendMouse((int)x0, (int)y0, 0);
            Thread.Sleep(80);
            SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
            var sw = Stopwatch.StartNew();
            double period = 1000.0 / hz;
            int i = 1;
            while (true)
            {
                double target = i * period;
                if (target > ms) break;
                // 睡到接近、再自旋到点：Sleep 的抖动是 1~15ms，直接用来配速
                // 会让"注入时刻"本身变得比被测量的延时还抖。
                while (true)
                {
                    double remain = target - sw.Elapsed.TotalMilliseconds;
                    if (remain <= 0) break;
                    if (remain > 2) Thread.Sleep((int)(remain - 1));
                    else Thread.SpinWait(200);
                }
                float u = (float)(target / ms);
                float px = x0 + (x1 - x0) * u;
                float py = y0 + (y1 - y0) * u + MathF.Sin(u * 9f) * 70f;
                SendMouse((int)px, (int)py, 0);
                i++;
            }
            SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);
        })
        { IsBackground = true };
        th.Start();
        return th;
    }

    /// <summary>
    /// 边抽消息边渲染，直到注入线程收笔。
    ///
    /// **顺序必须和主循环 Loop() 完全一致**：先抽、等到合成边界（配速模式）、
    /// 再抽一次、然后画。第一版把 DwmFlush 放在抽消息之后，量出来的
    /// "收到 → 调 Present" 是 16.2ms——那不是渲染慢，那是测量循环自己排的队。
    /// 测量代码和被测代码的时序不一致，量出来的就是测量方法的问题。
    /// </summary>
    private void PumpUntilDone(Thread injector)
    {
        while (injector.IsAlive)
        {
            DrainMessages();
            if (_dirty || _drawing)
            {
                if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
                RenderAll();
                _dirty = false;
            }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, Native.QS_ALLINPUT, 0);
        }
        DrainMessages();
        RenderAll();
    }

    private string PresentModeName()
    {
        if (OverlayWindow.VBlankPaced) return "合成边界配速 + Present(0)";
        if (OverlayWindow.LatencyWaitEnabled) return "帧延迟等待对象 + Present(0)（不限速）";
        return "Present(1) 阻塞在垂直同步";
    }

    /// <summary>把一个场景跑完：清空采样 → 注入 → 收数据 → 打印 + 落 CSV。</summary>
    private void RunLatencyScenario(string tag, string csvPath, string note,
                                    float x0, float y0, float x1, float y1, double hz, double ms)
    {
        Console.WriteLine();
        Console.WriteLine($"--- 场景 {tag}：{note}");
        Console.WriteLine($"    注入 {hz:F0} Hz，{ms / 1000.0:F1} 秒，路径 ({x0:F0},{y0:F0}) → ({x1:F0},{y1:F0})");

        Latency.Scenario = tag;
        Latency.Clear();
        ResetLatencyProbe();
        LatencyRecording = true;

        long frames0 = FrameCounter;
        double wall0 = _clock.Elapsed.TotalMilliseconds;
        var th = StartInjector(x0, y0, x1, y1, hz, ms);
        PumpUntilDone(th);
        double wall = _clock.Elapsed.TotalMilliseconds - wall0;
        long frames = FrameCounter - frames0;

        LatencyRecording = false;
        Console.WriteLine($"    采到 {Latency.Count} 条样本，渲染 {frames} 帧 / {wall / 1000.0:F1} 秒"
                          + $" = {frames * 1000.0 / wall:F1} fps（笔画数 {Doc.Strokes.Count}）");
        Console.Write(Latency.Report(spikeThresholdMs: 2 * (_windows.Count > 0 ? _windows[0].RefreshPeriodMs : 1000.0 / 60.0)));
        Latency.WriteCsv(csvPath, tag);
    }

    /// <summary>把页面写满，用来量"同一页墨迹很多时"的延时。</summary>
    private void FillDensePage(int strokes, float cx, float cy)
    {
        Doc.Clear();
        var rnd = new Random(7);
        for (int i = 0; i < strokes; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = new Color4(0.2f, 0.3f, 0.9f, 1f),
                Width = 3f * DpiScale,
            };
            float bx = cx - 420 + (float)rnd.NextDouble() * 840;
            float by = cy - 260 + (float)rnd.NextDouble() * 520;
            for (int k = 0; k <= 14; k++)
                s.AddPoint(bx + k * 3.5f, by + MathF.Sin(k * 0.7f) * 5f, 0.6f, NowMs);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(700);
    }

    /// <summary>
    /// 测"Present 返回 → 屏幕真的变色"这一段——DWM 合成 + 扫描输出 + 面板。
    ///
    /// 为什么必须盯着屏幕看：进程内的 API 拿不到这一段的准确时刻。前面四段
    /// 我们都能自己打时标，唯独"像素什么时候亮"只有屏幕自己知道。
    ///
    /// 做法：白板底色按 8 帧一个周期黑白交替（4 帧白、4 帧黑），后台线程用 GDI
    /// 一直读屏幕上固定的一个点，记下每次变色的时刻；主线程每帧记下
    /// (Present 返回时刻, 这一帧的颜色)。周期 8 帧 ≈133ms，远大于任何可能的
    /// 管线延时，所以每次变色都能唯一对上"是哪一帧先变的"。
    /// delta = 变色时刻 − 那一帧 Present 返回的时刻。
    ///
    /// 这个尺子自身的偏差要说清楚：GDI 抓的是 DWM 合成后的桌面，读取本身也可能
    /// 按垂直同步对齐，所以绝对值可能整体偏大几毫秒。**两种呈现方式用同一把
    /// 尺子量出来的差值（差几个刷新周期）才是这条数据真正的价值。**
    /// </summary>
    private void RunPhotonProbe(string csvPath, int cycles)
    {
        Console.WriteLine();
        Console.WriteLine("--- 场景 photon：Present 返回 → 屏幕真的变色（DWM 合成 + 扫描输出）");
        Console.WriteLine("    白板按 8 帧黑白交替，另起一个线程盯着屏幕上一个点看什么时候变");

        Doc.Clear();
        BoardOn = true;
        _dirty = true;
        RenderAll();
        _dirty = false;

        int probeX = _virtualX + 40, probeY = _virtualY + 40;
        var flips = new List<(ulong qpc, int color)>();
        bool stop = false;

        var th = new Thread(() =>
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            var bi = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = 1, biHeight = -1, biPlanes = 1, biBitCount = 32,
                    biCompression = (int)Native.BI_RGB,
                }
            };
            IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS,
                                                 out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(memDc, dib);
            int last = -1;
            while (!stop)
            {
                Native.BitBlt(memDc, 0, 0, 1, 1, screenDc, probeX, probeY, Native.SRCCOPY);
                int c = Marshal.ReadByte(bits, 0) > 128 ? 1 : 0;   // B 通道
                if (last >= 0 && c != last) flips.Add((Qpc.Now, c));
                last = c;
                Thread.SpinWait(150);
            }
            Native.SelectObject(memDc, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        })
        { IsBackground = true };
        th.Start();
        Thread.Sleep(200);

        var frames = new List<(ulong qpc, int color)>();
        int total = cycles * 8;
        for (int i = 0; i < total; i++)
        {
            int color = (i / 4) % 2 == 0 ? 1 : 0;      // 1 = 白，0 = 黑
            BoardColor = color == 1 ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 1f);
            _dirty = true;
            if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
            RenderAll();
            _dirty = false;
            frames.Add((_windows[0].LastPresentEndQpc, color));
        }

        stop = true;
        th.Join(2000);
        BoardOn = false;
        BoardColor = new Color4(0.99f, 0.99f, 0.98f, 1f);
        _dirty = true;
        RenderAll();
        _dirty = false;

        // 把每次变色对上"这一段的头一帧"。同一颜色的帧是连续 4 帧一段，
        // 屏幕上变色对应的是这一段的第 1 帧上屏。
        var deltas = new List<double>();
        int searchFrom = 0;
        foreach (var (qpc, color) in flips)
        {
            int match = -1;
            for (int k = searchFrom; k < frames.Count; k++)
            {
                if (frames[k].color != color) continue;
                if (k > 0 && frames[k - 1].color == color) continue;   // 只要段首
                if (frames[k].qpc <= qpc) { match = k; searchFrom = k + 1; }
                else break;
            }
            if (match < 0) continue;
            deltas.Add(Qpc.Ms(frames[match].qpc, qpc));
        }

        double refresh = _windows[0].RefreshPeriodMs;
        if (deltas.Count == 0)
        {
            Console.WriteLine("    没对上（屏幕抓取可能被别的窗口挡住，或抓取本身失败）");
            return;
        }
        var d = new Dist(deltas);
        Console.WriteLine($"    抓到 {deltas.Count} 次变色，本机交换链报告的刷新周期 {refresh:F2} ms");
        Console.WriteLine($"    Present 返回 → 屏幕变色：{d}");
        Console.WriteLine($"    折合 {(d.Mean / refresh):F2} 个刷新周期（均值）、"
                          + $"{(d.P50 / refresh):F2} 个（中位）");
        _photonDeltaMs = d.Mean;
        _photonDeltaFrames = d.Mean / refresh;
    }

    /// <summary>上机测得的上屏段（毫秒 / 刷新周期数），给报告拼"端到端"用。</summary>
    private double _photonDeltaMs;
    private double _photonDeltaFrames;

    private void LatencyBench(string csvPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 延时实测（笔尖动 → 像素亮）===");
        Console.WriteLine($"  呈现方式      ：{PresentModeName()}");
        Console.WriteLine($"  交换链后缓冲  ：{OverlayWindow.BufferCount}");
        Console.WriteLine($"  性能面板 HUD  ：{(ShowHud ? "开" : "关")}");
        Console.WriteLine($"  委托墨迹轨迹  ：{OverlayWindow.InkTrailNote}"
                          + (OverlayWindow.InkTrailEnabled ? "，已启用" : "，未启用"));
        Console.WriteLine($"  说明          ：注入的是合成鼠标，走的是和真笔同一条 WM_POINTER 路径；"
                          + "“输入 → 收到消息”那一段在合成输入上可能不可得（报告里会标）。");

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.45f;

        // 1) 空白页，正常书写速度。
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("empty-slow", csvPath, "空白页，正常书写速度",
            cx - 380, cy, cx + 380, cy, 140, 3000);

        // 2) 空白页，快速划线。点的间距大、单位时间覆盖面积大，
        //    量的是"同样一帧要光栅化的墨更多"时的表现。
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("empty-fast", csvPath, "空白页，快速划线",
            cx - 600, cy, cx + 600, cy, 200, 2500);

        // 3) 密集页：同一小块里已经有 3000 笔。
        FillDensePage(3000, cx, cy);
        RunLatencyScenario("dense", csvPath, "密集页（同区域已有 3000 笔）",
            cx - 380, cy, cx + 380, cy, 140, 3000);

        // 4) 长跑：连续写 12 秒，专门看"越写越慢"和抖动分布。
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("long-12s", csvPath, "连续书写 12 秒（稳定性）",
            cx - 380, cy, cx + 380, cy, 140, 12000);

        // 5) 上屏段：唯一必须"看屏幕"才知道的一段。
        RunPhotonProbe(csvPath, 20);
        if (_photonDeltaMs > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  端到端估计（最新一笔 → 像素亮）：");
            Console.WriteLine($"    阻塞式 : 16.5（到 Present 返回）+ {_photonDeltaMs:F1}（上屏）"
                              + $" ≈ {16.5 + _photonDeltaMs:F0} ms");
        }

        Console.WriteLine();
        Console.WriteLine($"CSV 已写入 {csvPath}");
        _quit = true;
    }

    /// <summary>
    /// 真笔实测：把探针挂上，请用户用手写笔在屏幕上写，结束后出报告。
    ///
    /// 这个模式同时回答一个关键问题：**手写板到底是以"笔"报进来的，还是以
    /// "鼠标"报进来的**。前者能拿到硬件时标和压感，后者连"笔尖什么时候动"
    /// 都只能靠轮询猜——很多手写板的"迟滞感"就出在这里，跟应用代码无关。
    /// </summary>
    private void LatencyLive(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 真笔延时实测 ===");
        Console.WriteLine($"  呈现方式：{PresentModeName()}，交换链后缓冲 {OverlayWindow.BufferCount}");
        Console.WriteLine($"  请用手写笔画线，持续 {seconds:F0} 秒（或按 Ctrl+Alt+Shift+X 提前结束）……");
        Console.WriteLine();

        Latency.Scenario = "真笔手写";
        Latency.Clear();
        ResetLatencyProbe();
        LatencyRecording = true;

        var seen = new Dictionary<uint, int>();
        double t0 = NowMs;
        double nextTick = t0 + 2000;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            seen[LastPointerType] = seen.GetValueOrDefault(LastPointerType) + 1;
            if (_dirty || _drawing)
            {
                if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
                RenderAll();
                _dirty = false;
            }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= nextTick)
            {
                nextTick = NowMs + 2000;
                Console.WriteLine($"  [{NowMs / 1000:F0}s] 已采 {Latency.Count} 帧，"
                                  + $"当前设备 {DeviceName(LastPointerType)}，"
                                  + $"笔画 {Doc.Strokes.Count}");
            }
        }

        LatencyRecording = false;
        Console.WriteLine();
        Console.WriteLine("设备使用统计（每帧采到的最新型号）："
                          + string.Join("，", seen.OrderByDescending(kv => kv.Value)
                                                  .Select(kv => $"{DeviceName(kv.Key)} {kv.Value} 帧")));
        Console.WriteLine("  笔 = PT_PEN 才是真笔通道（有压感、有硬件时标）；"
                          + "鼠标 = 手写板工作在兼容模式，单这一项就够造成迟滞感。");

        // 真笔的"数据质量"汇总：这次要不要相信压感、合并点收全了没有、
        // 以及预测在什么档位（这些在 调研-压感与预测-原理.md 与 测试-压感与预测.md 里都有判据）。
        Console.WriteLine();
        Console.WriteLine("=== 真笔数据质量汇总 ===");
        Console.WriteLine($"  合并点：{PenMessages} 条消息 → {PenSamples} 个采样点"
                          + (PenMessages > 0 ? $"（每条 {PenSamples / (double)PenMessages:F2} 个）" : ""));
        // 非笔指针（鼠标 / 触摸）也报一份：手写板没开 Windows Ink、以及触摸屏，
        // 走的就是这条路——"合并率"这一项以前只有笔有数，现在两边都有。
        if (PtrMessages > 0)
            Console.WriteLine($"  非笔指针（鼠标/触摸）：{PtrMessages} 条消息 → {PtrSamples} 个采样点"
                              + $"（每条 {PtrSamples / (double)PtrMessages:F2} 个）"
                              + $"，收下 {PtrTotalPoints} 个点");
        Console.WriteLine($"  压感：{PenPressurePoints}/{PenTotalPoints} 个点带有效压感；"
                          + $"设备报 pressure 位={Yes(PenSawPressureMask)}，"
                          + $"倾角位={Yes(PenSawTiltMask)}，旋转位={Yes(PenSawRotationMask)}");
        // [删除 2026-10-05] 预测统计：随老预测系统移除。
        Console.WriteLine($"  湿墨轨迹：{(OverlayWindow.InkTrailEnabled ? "开" : "关")}"
                          + $"（{OverlayWindow.InkTrailNote}）");

        // 书写期间的分配与 GC：低配机排查"偶发卡顿"的依据。
        // 每笔分配越大越容易触发回收；**第 2 代回收出现在书写期间 = 那一下就卡了几十毫秒**。
        if (StrokesMeasured > 0)
            Console.WriteLine($"  书写期间：{StrokesMeasured} 笔，每笔整笔会话分配（累计，非驻留） 平均 {AllocKbSum / StrokesMeasured:F1} KB"
                              + $" / 最大 {AllocBytesMax / 1024.0:F1} KB；"
                              + (StrokesWithGc2 == 0
                                  ? "第 2 代 GC 一次都没出现在书写期间（好）"
                                  : $"⚠ 第 2 代 GC 出现在 {StrokesWithGc2} 笔里（低配机上就是可见卡顿，要查）"));
        Console.WriteLine($"  GC 低延迟档：{GcLatency.Describe()}");

        Console.WriteLine(PenTotalPoints == 0
            ? "  注意：这一轮一个真笔采样点都没有——写的时候用的是鼠标/触摸，或者笔工作在兼容模式"
            : PenSawPressureMask
                ? "  压感可用：这台机器/这支笔确实在报压力"
                : "  压感不可用：设备没报 pressure 位（先查驱动的 Windows Ink 开关）");

        Console.Write(Latency.Report(spikeThresholdMs: 2 * (_windows.Count > 0 ? _windows[0].RefreshPeriodMs : 1000.0 / 60.0)));
        string csv = "reports/latency-live.csv";
        Latency.WriteCsv(csv, "real-pen");
        Console.WriteLine($"CSV 已写入 {csv}");
        _quit = true;
    }

    /// <summary>汇总行里用的小写法。</summary>
}
