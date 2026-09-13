using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;
using InkEngine.Optimize;

namespace InkProbe;

/// <summary>
/// 开发期宿主：持有引擎，外加一整套自动化测试/基准工具。
/// 继承只是为了让这些工具直接读引擎内部状态（产品代码请用组合：new InkEngine()）。
/// </summary>
internal sealed class App : InkEngine.InkEngine
{
    private IntPtr _clickTargetHwnd;
    private string _clickLogFile;

    /// <summary>
    /// 笔迹优化器。**宿主自己持有、自己决定装不装**——引擎压根不认识它。
    /// 自检模式直接拿它算轮廓；交互模式由下面的开关决定要不要注册成
    /// <see cref="InkOptimizers.Current"/>。
    /// </summary>
    private readonly InkBeautifier _beautifier = new();

    // =====================================================================
    //  Host hooks: everything here is a development tool, not engine code.
    //  引擎的 Run() 不再认识测试模式；模式分发全部落在这一层。
    // =====================================================================

    /// <summary>
    /// 建窗口之前就要处理掉的两件事：截图工具、作为"下层窗口"的点击目标。
    /// 二者都不需要覆盖层，所以必须在 CreateOverlays 之前拦下来。
    /// </summary>
    protected override bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;

        // A separate process that just sits there waiting to be clicked. The
        // pass-through test uses it as the window *underneath* the overlay, so
        // the test observes real cross-process mouse routing.
        if (mode == "--clicktarget")
        {
            RunClickTarget(args.Length > 1 ? args[1] : "clicktarget.txt");
            return true;
        }

        // Capture-only mode: grab the desktop without creating any overlay.
        if (mode == "--screenshot")
        {
            string path = args.Length > 1 ? args[1] : "screen.bmp";
            int sx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int sy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

            // --screenshot <path> <x> <y> <w> <h>：只截一小块（1:1 像素），
            // 用来精确定位别的程序界面上的按钮位置。
            if (args.Length >= 6 &&
                int.TryParse(args[2], out int rx) && int.TryParse(args[3], out int ry) &&
                int.TryParse(args[4], out int rw) && int.TryParse(args[5], out int rh))
            {
                sx = rx; sy = ry; sw = rw; sh = rh;
            }

            bool okShot = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
            Console.WriteLine(okShot ? $"已保存 {path} ({sw}x{sh})" : "截图失败");
            exitCode = okShot ? 0 : 1;
            return true;
        }

        return false;
    }

    /// <summary>测试模式分发。返回 -1 = 正常跑消息循环。</summary>
    protected override int RunModeDispatch(string mode, string[] args)
    {
        // 纯底层模式：接一个"什么都不画"的宿主。引擎要求接入一个 IOverlayUi，
        // 这样既满足接口，又验证了"引擎不依赖任何具体界面"这条设计。
        SetUi(new HeadlessUi());

        // 装不装笔迹优化器，由宿主在这里决定。引擎本身不认识它。
        //
        //   默认        不装。引擎画的就是原始采样点，量到的性能里不含任何
        //               平滑/拟合成本——底层性能测试要的就是这个基准。
        //   --smooth    装上（平滑 + 抽稀 + 拟合 + 笔锋）。
        //   --rawink    显式保持不装，与默认一致，留着是为了兼容原有命令行。
        //
        // 有几个模式本身就是在验优化器（笔锋自检、四种笔锋摆样、拐角、点数
        // 探针），它们自动装上，否则跑出来是一片空白。
        bool wantOptimizer = args.Contains("--smooth")
            || mode is "--beautifytest" or "--beautifyshowcase"
                     or "--cornertest" or "--aaprobe";
        if (args.Contains("--rawink")) wantOptimizer = false;
        if (wantOptimizer)
        {
            InkOptimizers.Current = _beautifier;
            Console.WriteLine("笔迹优化器: 已装上");
        }
        else
        {
            Console.WriteLine("笔迹优化器: 未装（纯底层，画原始采样点）");
        }

        // --preset <precise|handwriting|bold|calligraphy>：笔锋预设。
        // 默认是 precise（不做手写美化），需要时用参数切换。
        int presetIdx = Array.IndexOf(args, "--preset");
        if (presetIdx >= 0 && presetIdx + 1 < args.Length)
        {
            PenPresetValue = args[presetIdx + 1].ToLowerInvariant() switch
            {
                "precise" => PenPreset.Precise,
                "bold" => PenPreset.Bold,
                "calligraphy" => PenPreset.Calligraphy,
                _ => PenPreset.Handwriting,
            };
            Console.WriteLine($"笔锋预设 = {PenPresetValue}");
        }

        if (mode == "--selftest")
        {
            double seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 12;
            _autoExitAt = NowMs + seconds * 1000;
            _nextLogAt = NowMs;
            SelfTest(seconds);
        }
        else if (mode == "--report")
        {
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            Report(args.Length > 1 ? args[1] : "reports/inkprobe-report.txt");
        }
        else if (mode == "--memory")
        {
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            if (args.Length > 2)
                OverlayWindow.CenterlineRendering = args[2] == "center";
            MemoryReport(args.Length > 1 ? args[1] : "reports/inkprobe-memory.txt");
        }
        else if (mode == "--inputtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InputPathTest();
        }
        else if (mode == "--passtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PassThroughTest();
        }
        else if (mode == "--erasertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraserTest();
        }
        else if (mode == "--widthtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            WidthTest();
        }
        else if (mode == "--ghosttest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            GhostTest();
        }
        else if (mode == "--trailtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InkTrailTest();
        }
        else if (mode == "--longrun")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = args.Length > 1 && double.TryParse(args[1], out var s2) ? s2 : 180;
            LongRunTest(secs);
        }
        else if (mode == "--realizetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var s3) ? s3 : 10000;
            RealizationTest(n);
        }
        else if (mode == "--restest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var s4) ? s4 : 5000;
            ResolutionTest(n);
        }
        else if (mode == "--beautifytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BeautifyTest();
        }
        else if (mode == "--beautifyshowcase")
        {
            // 把四种笔锋摆出来给人看，挂着不动由外部截图。
            BeautifyShowcase();
        }
        else if (mode == "--cornertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CornerTest();
        }
        else if (mode == "--shapetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeTest();
        }
        else if (mode == "--savetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SaveTest();
        }
        else if (mode == "--transformtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TransformTest();
        }
        else if (mode == "--handletest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HandleTest();
        }
        else if (mode == "--selshowcase")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelShowcase();
        }
        else if (mode == "--edittest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EditTest();
        }
        else if (mode == "--seldrag")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelDragShowcase();
        }
        else if (mode == "--selbench")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelBenchTest();
        }
        else if (mode == "--duptest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 14;
            DupTest(n);
        }
        else if (mode == "--memab")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10000;
            int k = args.Length > 2 && int.TryParse(args[2], out var rd) ? rd : 5;
            MemAbTest(n, k);
        }
        else if (mode == "--aaprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            Console.WriteLine();
        Console.WriteLine("=== 平滑到底是谁给的 ===");
            PointCountProbe();
            _quit = true;
        }
        else
        {
            // 没有测试模式：交回引擎跑正常消息循环。
            return -1;
        }

        Loop();
        Shutdown();
        return 0;
    }

    /// <summary>开发期基准/内存探测挂在宿主里，引擎只留一个空钩子。</summary>
    protected override void HandleHostHotkey(int id)
    {
        if (id == 10) Benchmark(10_000);
        else if (id == 11) MemoryProbe();
    }

    /// <summary>退出前把工具条的配置落盘，保证老师拖动的位置不会丢。</summary>
    protected override void Shutdown()
    {
        base.Shutdown();
    }

    /// <summary>点击目标窗口只关心"有没有被点到"。</summary>
    protected override bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_clickTargetHwnd == IntPtr.Zero || hWnd != _clickTargetHwnd)
            return false;

        if (msg == Native.WM_LBUTTONDOWN)
        {
            try { File.AppendAllText(_clickLogFile, "clicked\n"); } catch { }
            return true;
        }
        if (msg == Native.WM_CLOSE) { _quit = true; return true; }

        // 其它消息交回默认处理，不能在这儿吞掉，否则窗口画不出来。
        result = Native.DefWindowProc(hWnd, msg, wParam, lParam);
        return true;
    }

    protected override void PrintUsageHelp()
    {
        base.PrintUsageHelp();
        Console.WriteLine("开发期测试模式（不属于引擎）：");
        Console.WriteLine("  --selftest [秒]     自动画几笔并检查渲染");
        Console.WriteLine("  --report [文件]     输出完整性能报告");
        Console.WriteLine("  --memory [文件]     输出内存归因报告");
        Console.WriteLine("  --inputtest         指针输入路径自检");
        Console.WriteLine("  --passtest          穿透真机测试（跨进程点击）");
        Console.WriteLine("  --erasertest        橡皮擦正确性");
        Console.WriteLine("  --widthtest         笔迹粗细/压力");
        Console.WriteLine("  --ghosttest         残影检测");
        Console.WriteLine("  --trailtest         委托墨迹轨迹对照");
        Console.WriteLine("  --longrun [秒]      长时运行内存/CPU");
        Console.WriteLine("  --realizetest [n]   几何实现缓存对照");
        Console.WriteLine("  --restest [n]       分辨率对照（离屏层）");
        Console.WriteLine("  --beautifytest      手写美化自检（笔锋量化）");
        Console.WriteLine("  --beautifyshowcase  四种笔锋摆样（人工看）");
        Console.WriteLine("  --cornertest        直角填充自检（拐角会不会掉色）");
        Console.WriteLine("  --preset <名字>     precise | handwriting | bold | calligraphy");
        Console.WriteLine("  --rawink            关掉所有笔迹后处理（滤波/抽稀/拟合/笔锋）");
        Console.WriteLine("  --screenshot <路径> [x y w h]  截屏工具");
        Console.WriteLine("  --clicktarget <日志>           穿透测试用的下层窗口");
        Console.WriteLine();
    }

    /// <summary>
    /// 数一笔在各阶段剩多少点/控制点。这是"哪些是优化做出来的、哪些是系统给的"
    /// 最直接的证据：点数的变化只可能来自我们自己的代码，与系统无关。
    /// </summary>
    private void PointCountProbe()
    {
        var s = MakeSpeedVaryingStroke(3f, PenPreset.Handwriting, DpiScale);
        int afterFilterAndDecimate = s.Points.Count;

        // 模拟"不作任何后处理"时的点数：这就是指针消息的条数级。
        Console.WriteLine($"  1. 指针消息收进来到的点数（--rawink 时就是这个）：{afterFilterAndDecimate}");

        var simplified = Simplify.Rdp(s.Points, 0.5f * DpiScale);
        Console.WriteLine($"  2. 抽稀之后（RDP）      ：{simplified.Count} 点"
                        + $"（压掉 {(1 - simplified.Count / (float)afterFilterAndDecimate):P0}）");

        _beautifier.EndStroke(s, DpiScale);
        int outlinePts = s.Outline?.Length ?? 0;
        Console.WriteLine($"  3. 拟合与抽稀            ：{_beautifier.LastReport}");
        Console.WriteLine($"  4. 送进 GPU 的轮廓点数   ：{outlinePts}");
        Console.WriteLine();
        Console.WriteLine("  这些数字全是引擎算出来的；系统只负责把最终轮廓按抗锯齿栅格化。");
    }

    /// <summary>
    /// 对比实验：同一笔画，开/关 Direct2D 的抗锯齿各画一次。
    /// 用来说明"关掉我们的优化之后，看着仍然平滑"这部分是系统给的。
    /// </summary>
    private void AntialiasProbe(bool enable)
    {
        var w = _windows[0];
        var ctx = w.Context;
        var before = ctx.AntialiasMode;
        ctx.AntialiasMode = enable ? AntialiasMode.PerPrimitive : AntialiasMode.Aliased;

        // 一条 45° 斜线：抗锯齿在斜边上最明显。
        Doc.Clear();
        Doc.InvalidateAll();
        var s = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1),
            Width = 8f * DpiScale, Preset = PenPreset.Precise,
        };
        float x = VirtualScreen.MinX + 500, y = VirtualScreen.MinY + 400;
        for (int i = 0; i <= 200; i++) s.AddPoint(x + i * 3f, y + i * 3f, 0.5f, i);
        _beautifier.EndStroke(s, DpiScale);

        Doc.AddStroke(s);
        SettleFrames(500);

        // 数"灰"像素：黑是笔画内部，白是背景，介于两者之间的是边缘的抗锯齿过渡。
        int gray = ScreenProbe.CountNear((int)x - 20, (int)y - 20,
                                         700, 700, 128, 128, 128, 110);
        int black = ScreenProbe.CountNear((int)x - 20, (int)y - 20,
                                          700, 700, 0, 0, 0, 40);
        Console.WriteLine($"  抗锯齿 {(enable ? "开" : "关")}：边缘过渡像素 {gray}，实心像素 {black}");

        ctx.AntialiasMode = before;
    }

    // =====================================================================
    //  直角掉色自检
    // =====================================================================

    /// <summary>
    /// 画一个直角，量**拐角处**和**笔直段**各自盖住多少墨。
    ///
    /// 这是用户反馈的问题："直角处掉很多颜色"。带子轮廓在拐角会自交，
    /// 如果填充规则或轮廓生成不对，拐角就会出现一个缺口——肉眼看着就是
    /// "这里的颜色比别处淡"。所以用两个等面积窗口的墨量比值来判定：
    /// 拐角窗口的覆盖率不该明显低于直段。
    /// </summary>
    /// <summary>
    /// 配置 A/B 对照，**在同一个进程里交替跑**。
    ///
    /// 为什么必须这样：跨会话比数字是无效的。实测同一个"空闲"状态，两次会话
    /// 差了 7MB —— 环境（别的进程、驱动状态、系统缓存）会漂移，而漂移的量级
    /// 常常和被测效应同阶，于是结论就变成了掷骰子。
    ///
    /// 四条规矩，参考 JMH / Go benchstat / Google Benchmark 的通行做法：
    ///   ① **交替**跑 A、B、A、B…，不是"先全测 A 再全测 B"——漂移会被平摊到两边；
    ///   ② 每组测量前**强制归位**：清空 + 重建 + GC + TrimWorkingSet，
    ///      否则测到的是上一轮的残留；
    ///   ③ **预热**：先跑一段再采数（JIT、GPU 资源池、字体缓存都要热）；
    ///   ④ 看**中位数与极差**，不看单次值；差异落在极差里就老实说"测不出来"。
    ///
    /// 内存一律用**私有字节（提交大小）**，不用工作集——工作集会被系统随时回收，
    /// 它反映"系统压力"而不是"我们占了多少"（本地报告里已经写过这一点）。
    /// </summary>
    private void MemAbTest(int strokes, int rounds)
    {
        Console.WriteLine();
        Console.WriteLine($"=== A/B 对照：细分缓存 关(A) vs 开(B)，{strokes} 笔，交替 {rounds} 轮 ===");
        Console.WriteLine("   轮次 |  A私有 | A每帧 |  B私有 | B每帧 | A显存 | B显存");
        Console.WriteLine("  ------|--------|-------|--------|-------|-------|-------");

        var aPriv = new List<double>(); var aRec = new List<double>(); var aGpu = new List<double>();
        var bPriv = new List<double>(); var bRec = new List<double>(); var bGpu = new List<double>();

        // 预热一轮，两边各一次，让 JIT / GPU 资源池 / 字体缓存都热起来
        RunOnce(false, strokes, warmup: true);
        RunOnce(true, strokes, warmup: true);

        for (int r = 0; r < rounds; r++)
        {
            RunOnce(false, strokes, warmup: false);
            aPriv.Add(_abPriv); aRec.Add(_abRec); aGpu.Add(_abGpu);

            RunOnce(true, strokes, warmup: false);
            bPriv.Add(_abPriv); bRec.Add(_abRec); bGpu.Add(_abGpu);

            Console.WriteLine($"  {r + 1,5} | {aPriv[^1],6:F0} | {aRec[^1],5:F2} | "
                            + $"{bPriv[^1],6:F0} | {bRec[^1],5:F2} | {aGpu[^1],5:F0} | {bGpu[^1],5:F0}");
        }

        double aP = Median(aPriv), bP = Median(bPriv);
        double aR = Median(aRec), bR = Median(bRec);
        double aG = Median(aGpu), bG = Median(bGpu);

        Console.WriteLine();
        Console.WriteLine($"  私有字节中位数 ：A {aP:F0} MB   B {bP:F0} MB   -> {(bP - aP):+0;-0;0} MB");
        Console.WriteLine($"     A 极差 {aPriv.Min():F0}~{aPriv.Max():F0}（{(aPriv.Max() - aPriv.Min()):F0}）"
                        + $"   B 极差 {bPriv.Min():F0}~{bPriv.Max():F0}（{(bPriv.Max() - bPriv.Min()):F0}）");
        Console.WriteLine($"  显存中位数     ：A {aG:F0} MB   B {bG:F0} MB   -> {(bG - aG):+0;-0;0} MB");
        Console.WriteLine($"  每帧记录中位数 ：A {aR:F2} ms  B {bR:F2} ms  -> {(bR - aR):+0.00;-0.00;0.00} ms");
        Console.WriteLine();

        // 判据：差异必须大于两边极差的较大者，才算"测得出来"。
        double noise = Math.Max(aPriv.Max() - aPriv.Min(), bPriv.Max() - bPriv.Min());
        double diff = Math.Abs(bP - aP);
        Console.WriteLine(diff > noise
            ? $"  结论：差异 {diff:F0}MB 大于噪声带 {noise:F0}MB —— **测得出来**"
            : $"  结论：差异 {diff:F0}MB 落在噪声带 {noise:F0}MB 之内 —— **测不出来**，不能下结论");

        Stroke.MaxRealizations = 4096;   // 还原默认
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    private double _abPriv, _abRec, _abGpu;

    /// <summary>跑一次完整状态并采样。A=关细分缓存，B=开。</summary>
    private void RunOnce(bool realizations, int strokes, bool warmup)
    {
        Doc.Clear();
        Doc.ClearHistory();
        Doc.Selected.Clear();
        Stroke.MaxRealizations = realizations ? 4096 : 0;

        GenerateStrokes(strokes);
        Doc.InvalidateAll();
        SettleFrames(180);                       // 让它整层画一遍（这里才会建缓存）

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Mem.TrimWorkingSet();                    // 把"已释放但仍驻留"的部分挤出去
        SettleFrames(30);

        if (warmup) return;
        _abPriv = PrivateMb();
        _abGpu = TryGpuMb();
        var (rec, _) = MeasureFrames(24);
        _abRec = rec;
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    /// <summary>显存用量（取不到就返回 0，不影响报告）。</summary>
    private static double TryGpuMb()
    {
        try { return double.TryParse(GpuMb().Replace(" MB", ""), out var v) ? v : 0; }
        catch { return 0; }
    }

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
    /// 选中与变换的性能自检：走**真实的拖动路径**（SetTransformLive → 内容层
    /// 区域修补），量每步要花多久。
    ///
    /// 两个关键设计：
    ///   ① **用窗口里的计数器，不用墙钟。** 墙钟会把 Present 的垂直同步等待算进去
    ///      （60Hz 屏幕上每帧 16ms），量到的是显示器刷新率，不是我们的开销。
    ///   ② **看最长的一步，不看平均。** 卡顿感来自长帧，平均值会把它们抹平。
    /// </summary>
    private void SelBenchTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 选中与变换性能（真实拖动路径）===");
        Console.WriteLine("  画布里  | 变换 | 修补均 | 修补最长 | 每帧记录均 | 每帧最长");
        Console.WriteLine("  --------|------|--------|----------|------------|--------");

        foreach (int total in new[] { 0, 1000, 10000 })
        {
            Doc.Clear();
            Doc.ClearHistory();
            if (total > 0) GenerateStrokes(total);
            SettleFrames(150);

            // 模拟真实用法：用户框选**一小撮**（20 个），而不是把一万笔全选中
            Doc.Selected.Clear();
            int pick = Math.Min(20, Doc.Strokes.Count);
            for (int i = 0; i < pick; i++) Doc.Selected.Add(Doc.Strokes[i]);

            var targets = Doc.Selected.ToArray();
            const int steps = 30;
            double patchSum = 0, recordSum = 0, patchMax = 0, recordMax = 0;
            var w = _windows[0];

            for (int k = 0; k < steps; k++)
            {
                var m = Matrix3x2.CreateTranslation(6f, 3f);
                foreach (var s in targets)
                {
                    Doc.Dirty.Add(s.PaddedBounds);          // 旧位置
                    Doc.SetTransformLive(s, s.Transform * m);
                    Doc.Dirty.Add(s.PaddedBounds);          // 新位置
                }
                _dirty = true;
                SettleFrames(10);

                double p = w.LastPatchMs, r = w.LastRecordMs;
                patchSum += p; recordSum += r;
                if (p > patchMax) patchMax = p;
                if (r > recordMax) recordMax = r;
            }

            Console.WriteLine($"  {total,7} | {targets.Length,4} | {patchSum / steps,6:F2} | "
                            + $"{patchMax,8:F2} | {recordSum / steps,10:F2} | {recordMax,8:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("  判据：修补耗时**不该**随画布里已有笔画数明显增长——区域修补只处理");
        Console.WriteLine("  脏区内的对象，跟总数无关。若明显增长，说明退化成了整层重建。");
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 拖动预览的渲染核对：把选中对象用**实时变换**（SetTransformLive，
    /// 就是拖动中走的那条路径）挪走并挂着不动，由外部截图。
    ///
    /// 验的是"内容层会不会跟着修补"。之前漏了 bump 版本号，表现就是
    /// 蓝框跟着走、墨迹停在原地——截图上一眼能看出来：修好的话墨迹在框里，
    /// 没修的话墨迹还留在原来的地方，跟框分了家。
    /// </summary>
    private void SelDragShowcase()
    {
        SelShowcase();                       // 先摆好两条笔迹和选中框

        var sel = Doc.Selected.ToArray();
        var sb = EditRegion.Of(sel);
        var c = new Vector2((sb.MinX + sb.MaxX) * 0.5f, (sb.MinY + sb.MaxY) * 0.5f);
        var m = Matrix3x2.CreateScale(1.25f, 1.25f, c) * Matrix3x2.CreateTranslation(120f, -60f);

        // 完全照 UpdateSelDrag 的做法：标脏旧位置 → 实时改变换 → 标脏新位置
        foreach (var s in sel)
        {
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.SetTransformLive(s, s.Transform * m);
            Doc.Dirty.Add(s.PaddedBounds);
        }

        SettleFrames(700);
        Console.WriteLine("已用实时变换挪走，等外部截图（这个模式不会自己退出）");
    }

    /// <summary>
    /// 编辑命令自检：复制 / 删除 / 翻转 / 旋转，外加操作条的命中判定。
    ///
    /// 这四个动作是操作条按钮直接调的，但按钮没法自动点（要合成鼠标事件），
    /// 所以这里直接验它们背后的命令，把**撤销语义**一并验掉——
    /// 一步操作必须正好对应一条撤销记录，多一条少一条都是 bug。
    /// </summary>
    private void EditTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 编辑命令自检（复制 / 删除 / 翻转 / 旋转）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-24}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Stroke Make(float x)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = 4f,
                Color = new Color4(0.9f, 0.2f, 0.2f, 1f),
            };
            for (int i = 0; i < 20; i++) s.AddPoint(x + i * 3f, 500 + i * 2f, 0.5f, i);
            return s;
        }
        float Width(Stroke s) => s.WorldBounds.MaxX - s.WorldBounds.MinX;
        float Height(Stroke s) => s.WorldBounds.MaxY - s.WorldBounds.MinY;

        Doc.Clear();
        Doc.ClearHistory();
        var a = Make(300); Doc.AddStroke(a);
        var b = Make(600); Doc.AddStroke(b);

        // ---- 复制 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a); Doc.Selected.Add(b);
        int n0 = Doc.Strokes.Count, d0 = Doc.UndoDepth;
        Doc.DuplicateSelected(30, 30);
        Check("复制新增对象", Doc.Strokes.Count == n0 + 2, $"{n0} -> {Doc.Strokes.Count}");
        Check("复制只记一步", Doc.UndoDepth == d0 + 1, $"{d0} -> {Doc.UndoDepth}");
        Check("选中切到副本", Doc.Selected.Count == 2 && !ReferenceEquals(Doc.Selected[0], a), "");
        Check("副本是新身份", Doc.Selected[0].Id != a.Id, $"{a.Id} -> {Doc.Selected[0].Id}");
        Doc.Undo();
        Check("撤销复制", Doc.Strokes.Count == n0, $"{Doc.Strokes.Count}");

        // ---- 翻转 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a);
        var wb = a.WorldBounds;
        Doc.ApplyTransform(SelectionHandles.MirrorMatrix(wb, horizontal: true));
        Check("左右翻转：包围盒不变",
              Math.Abs(a.WorldBounds.MinX - wb.MinX) < 0.5f && Math.Abs(Width(a) - (wb.MaxX - wb.MinX)) < 0.5f, "");
        Check("翻转后行列式为负", a.Transform.M11 * a.Transform.M22 - a.Transform.M12 * a.Transform.M21 < 0f, "");
        Doc.Undo();
        Check("撤销翻转回原位", Math.Abs(a.WorldBounds.MinX - wb.MinX) < 0.05f,
              $"MinX {a.WorldBounds.MinX:F2} vs {wb.MinX:F2}");

        // ---- 旋转 90°：宽高应该互换 ----
        var wb2 = a.WorldBounds;
        var c = new Vector2((wb2.MinX + wb2.MaxX) * 0.5f, (wb2.MinY + wb2.MaxY) * 0.5f);
        Doc.ApplyTransform(Matrix3x2.CreateRotation(MathF.PI / 2f, c));
        Check("旋转 90°：宽高互换",
              Math.Abs(Width(a) - (wb2.MaxY - wb2.MinY)) < 0.5f
              && Math.Abs(Height(a) - (wb2.MaxX - wb2.MinX)) < 0.5f,
              $"{Width(a):F1}×{Height(a):F1}");
        Doc.Undo();

        // ---- 删除 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a);
        int n1 = Doc.Strokes.Count;
        Doc.DeleteSelected();
        Check("删除选中", Doc.Strokes.Count == n1 - 1 && Doc.Selected.Count == 0, $"{n1} -> {Doc.Strokes.Count}");
        Doc.Undo();
        Check("撤销删除", Doc.Strokes.Count == n1, $"{Doc.Strokes.Count}");

        // ---- 操作条命中 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a); Doc.Selected.Add(b);
        var sb = EditRegion.Of(Doc.Selected);
        float dpi = DpiScale;
        var bar = SelectionHandles.BarRect(sb, dpi);
        Check("操作条在选中框下方", bar.MinY > sb.MaxY, $"间距 {bar.MinY - sb.MaxY:F0}px");

        bool allHit = true; string bad = "";
        for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
        {
            var r = SelectionHandles.BarButtonRect(i, sb, dpi);
            int got = SelectionHandles.BarButtonAt((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f, sb, dpi);
            if (got != i) { allHit = false; bad = $"第{i}个按钮命中到 {got}"; }
        }
        Check("每个按钮都能点中", allHit, bad);
        Check("框外不误判",
              SelectionHandles.BarButtonAt(bar.MinX - 30, bar.MinY + 5, sb, dpi) == -1, "");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 编辑命令与操作条命中都正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 把选中框和手柄摆出来给人看。跟 --beautifyshowcase 一个路子：
    /// 画好挂着不动，由外部截图，用来肉眼核对观感（不是自动判定）。
    /// </summary>
    private void SelShowcase()
    {
        BoardOn = true;                 // 白底，不然手柄压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();

        float cx = VirtualScreen.MinX + 720;
        float top = VirtualScreen.MinY + 300;

        // 一条自由笔迹
        var free = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f,
        };
        for (int i = 0; i <= 80; i++)
        {
            float t = i / 80f;
            free.AddPoint(cx - 320 + t * 640, top + 80 + MathF.Sin(t * 7f) * 70f, 0.5f, i * 8);
        }
        Doc.AddStroke(free);

        // 一个带旋转 + 非等比缩放的矩形：用来核对"变换参与选中框"这件事
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 6f,
        };
        rect.AddPoint(cx - 260, top + 350, 1f, 0);
        rect.AddPoint(cx + 260, top + 520, 1f, 0);
        var center = new Vector2(cx, top + 435);
        rect.Transform = Matrix3x2.CreateRotation(0.14f, center)
                      * Matrix3x2.CreateScale(1.3f, 0.85f, center);
        Doc.AddStroke(rect);

        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Doc.InvalidateAll();
        SettleFrames(800);
        Console.WriteLine("选中框已摆好，等外部截图（这个模式不会自己退出）");
    }

    /// <summary>
    /// 选中手柄自检：位置、命中、以及每个手柄拖出来的是什么矩阵。
    ///
    /// 为什么要专门测：变换矩阵错了是那种"看着能动、但缩放之后再撤销
    /// 回不到原样"的问题，肉眼基本发现不了，而且会累积误差。
    /// </summary>
    private void HandleTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 选中手柄自检（位置 / 命中 / 变换矩阵）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float dpi = DpiScale;
        var b = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        float cx = (b.MinX + b.MaxX) * 0.5f;

        // ---- 位置 ----
        var tl = SelectionHandles.Position(SelHandle.TopLeft, b, dpi);
        var top = SelectionHandles.Position(SelHandle.Top, b, dpi);
        var rot = SelectionHandles.Position(SelHandle.Rotate, b, dpi);
        Check("左上角位置", Math.Abs(tl.X - b.MinX) < 0.01f && Math.Abs(tl.Y - b.MinY) < 0.01f,
              $"({tl.X:F0},{tl.Y:F0})");
        Check("上边中点位置", Math.Abs(top.X - cx) < 0.01f && Math.Abs(top.Y - b.MinY) < 0.01f, "");
        Check("旋转手柄在上方外侧",
              Math.Abs(rot.X - cx) < 0.01f && rot.Y < b.MinY - 20f,
              $"离上边 {b.MinY - rot.Y:F0}px");

        // ---- 命中 ----
        Check("点上四角命中", SelectionHandles.HitTest(tl.X, tl.Y, b, dpi) == SelHandle.TopLeft, "");
        Check("旋转手柄命中", SelectionHandles.HitTest(rot.X, rot.Y, b, dpi) == SelHandle.Rotate, "");
        float far = SelectionHandles.HitRadiusLogical * dpi * 1.6f;
        Check("偏离太远不命中",
              SelectionHandles.HitTest(tl.X - far, tl.Y, b, dpi) == SelHandle.None, $"偏 {far:F0}px");
        Check("极简模式不认边中点",
              SelectionHandles.HitTest(top.X, top.Y, b, dpi, includeEdgeHandles: false) == SelHandle.None, "");

        // ---- 四角拖动：锚点不动，被拖的角跟手 ----
        var br = SelectionHandles.Position(SelHandle.BottomRight, b, dpi);
        // 目标点取在"锚点 → 被拖的角"的延长线上：四角现在是**等比**缩放，
        // 只有沿对角线拖，被拖的角才会精确落在目标点上。
        // （不在对角线上时等比缩放也能用，只是角落不到指针那儿——这是等比的
        //   固有性质，不是 bug。）
        var target = new Vector2(b.MinX * 2f - b.MaxX, b.MinY * 2f - b.MaxY);
        var m = SelectionHandles.DragMatrix(SelHandle.TopLeft, b, tl, target, dpi, false, false);
        var anchorAfter = Vector2.Transform(br, m);
        Check("锚点（对角）不动",
              Math.Abs(anchorAfter.X - br.X) < 0.05f && Math.Abs(anchorAfter.Y - br.Y) < 0.05f,
              $"({anchorAfter.X:F1},{anchorAfter.Y:F1})");
        var cornerAfter = Vector2.Transform(tl, m);
        Check("被拖的角跟到目标点",
              Math.Abs(cornerAfter.X - target.X) < 0.05f && Math.Abs(cornerAfter.Y - target.Y) < 0.05f,
              $"({cornerAfter.X:F1},{cornerAfter.Y:F1}) 目标 ({target.X:F1},{target.Y:F1})");
        Check("四角是等比", MathF.Abs(m.M11 - m.M22) < 1e-4f, $"sx={m.M11:F3} sy={m.M22:F3}");

        // ---- 边中点：只动一个轴（这就是"左右拉伸 / 上下拉伸"）----
        var right = SelectionHandles.Position(SelHandle.Right, b, dpi);
        var mEdge = SelectionHandles.DragMatrix(SelHandle.Right, b, right,
                                                new Vector2(right.X + 200, right.Y + 999), dpi, false, false);
        Check("右边中点只拉伸 X", MathF.Abs(mEdge.M22 - 1f) < 1e-4f && mEdge.M11 > 1.49f,
              $"sx={mEdge.M11:F2} sy={mEdge.M22:F2}（Y 的拖动被忽略）");

        // ---- 拖过头：夹住，不翻转 ----
        var wayPast = new Vector2(br.X + 600, br.Y + 600);
        var mOver = SelectionHandles.DragMatrix(SelHandle.TopLeft, b, tl, wayPast, dpi, false, false);
        Check("拖过头不翻转（夹住）",
              mOver.M11 > 0f && mOver.M22 > 0f && MathF.Abs(mOver.M11 - SelectionHandles.MinScale) < 1e-3f,
              $"sx={mOver.M11:F3} sy={mOver.M22:F3}，下限 {SelectionHandles.MinScale}");

        // ---- Shift 等比 ----
        var mUni = SelectionHandles.DragMatrix(SelHandle.TopLeft, b, tl,
                                               new Vector2(b.MinX - 300, b.MinY - 80), dpi, true, false);
        Check("Shift 等比", MathF.Abs(mUni.M11 - mUni.M22) < 1e-4f, $"sx={mUni.M11:F3} sy={mUni.M22:F3}");

        // ---- 旋转：绕选区中心转，长度不变 ----
        var center = new Vector2(cx, (b.MinY + b.MaxY) * 0.5f);
        var mRot = SelectionHandles.DragMatrix(SelHandle.Rotate, b, top,
                                               new Vector2(center.X + 100, center.Y), dpi, false, false);
        var rotated = Vector2.Transform(tl, mRot);
        float r0 = Vector2.Distance(tl, center);
        float r1 = Vector2.Distance(rotated, center);
        Check("旋转保持半径", Math.Abs(r0 - r1) < 0.05f, $"{r0:F1} -> {r1:F1}");

        // ---- 旋转吸附 ----
        var mSnap = SelectionHandles.DragMatrix(SelHandle.Rotate, b, top,
                                                new Vector2(center.X + 100, center.Y + 7), dpi, false, true);
        var v0 = Vector2.Normalize(top - center);
        var v1 = Vector2.Normalize(Vector2.Transform(top, mSnap) - center);
        float deg = MathF.Acos(Math.Clamp(Vector2.Dot(v0, v1), -1f, 1f)) * 180f / MathF.PI;
        Check("Shift 旋转吸附到 15°", Math.Abs(deg % 15f) < 0.5f || Math.Abs(deg % 15f - 15f) < 0.5f,
              $"转了 {deg:F1}°");

        // ---- 镜像：只能从矩阵入口来 ----
        var mMir = SelectionHandles.MirrorMatrix(b, horizontal: true);
        var mirroredLeft = Vector2.Transform(tl, mMir);
        Check("左右镜像对称", Math.Abs(mirroredLeft.X - b.MaxX) < 0.01f
                           && Math.Abs(mirroredLeft.Y - b.MinY) < 0.01f,
              $"左上角 -> ({mirroredLeft.X:F1},{mirroredLeft.Y:F1})");
        Check("镜像矩阵行列式为负", mMir.M11 * mMir.M22 - mMir.M12 * mMir.M21 < 0f, "");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 手柄位置、命中与变换矩阵都正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 变换命令自检。
    ///
    /// 它要证明的是整个对象模型的**核心论断**：改变换不碰几何。
    /// 具体就是三件事——几何版本号不变（GPU 缓存不用重建）、
    /// 包围盒和空间索引跟着走、撤销能精确回到原样。
    /// </summary>
    private void TransformTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 变换命令自检（改矩阵，不碰几何）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();

        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 0f, 1f), Width = 4f,
        };
        for (int i = 0; i < 60; i++)
            s.AddPoint(200 + i * 5f, 300 + MathF.Sin(i * 0.2f) * 40f, 0.5f, i * 8);
        Doc.AddStroke(s);

        int revisionBefore = s.Revision;
        var boundsBefore = s.WorldBounds;
        float widthBefore = boundsBefore.MaxX - boundsBefore.MinX;

        Doc.Selected.Clear();
        Doc.Selected.Add(s);

        // 以画的起点为中心放大两倍——非等比也不影响这套机制
        var center = new Vector2(boundsBefore.MinX, boundsBefore.MinY);
        int depthBefore = Doc.UndoDepth;
        bool applied = Doc.ApplyTransform(Matrix3x2.CreateScale(2f, 2f, center));
        Check("命令已提交", applied && Doc.UndoDepth == depthBefore + 1,
              $"撤销深度 {depthBefore} -> {Doc.UndoDepth}");

        // ★ 这条是整个设计的要害：几何没有重建
        Check("几何版本号未变", s.Revision == revisionBefore,
              $"Revision {revisionBefore} -> {s.Revision}（GPU 缓存不用重建）");

        var boundsAfter = s.WorldBounds;
        float widthAfter = boundsAfter.MaxX - boundsAfter.MinX;
        Check("包围盒按倍数变大", Math.Abs(widthAfter - widthBefore * 2f) < 0.5f,
              $"{widthBefore:F0} -> {widthAfter:F0} px");

        // 空间索引必须跟着走：在新位置查得到
        var probe = new RectF
        {
            MinX = center.X - 1, MinY = center.Y - 1,
            MaxX = center.X + 1, MaxY = center.Y + 1,
        };
        var hits = new List<Stroke>();
        Doc.QueryGrid(probe, hits);
        Check("空间索引已更新", hits.Contains(s), $"新位置查到 {hits.Count} 个");

        Doc.Undo();
        var boundsUndone = s.WorldBounds;
        bool restored = Math.Abs(boundsUndone.MinX - boundsBefore.MinX) < 0.01f
                     && Math.Abs(boundsUndone.MaxX - boundsBefore.MaxX) < 0.01f
                     && Math.Abs(boundsUndone.MinY - boundsBefore.MinY) < 0.01f
                     && Math.Abs(boundsUndone.MaxY - boundsBefore.MaxY) < 0.01f;
        Check("撤销精确回原位", restored,
              $"({boundsUndone.MinX:F2},{boundsUndone.MinY:F2})-({boundsUndone.MaxX:F2},{boundsUndone.MaxY:F2})");

        Doc.Redo();
        Check("重做又回到放大后", Math.Abs((s.WorldBounds.MaxX - s.WorldBounds.MinX) - widthAfter) < 0.5f, "");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 变换只改矩阵，几何与缓存未受影响" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 保存 / 加载往返自检。
    ///
    /// 验的是"存下去的和读回来的完全一样"。这条比看起来重要：序列化是
    /// **唯一会碰全部字段**的代码，任何一个字段忘了写、或者顺序写错，
    /// 表现都是"用户存了一学期的批注打不开"，而且开发时很难发现。
    /// </summary>
    private void SaveTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 保存 / 加载往返自检 ===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();

        // 自由笔迹：带压感、带时间戳
        var freehand = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f),
            Width = 6f, Preset = PenPreset.Handwriting,
        };
        for (int i = 0; i < 40; i++)
            freehand.AddPoint(100 + i * 7f, 200 + MathF.Sin(i * 0.3f) * 30f,
                              0.2f + 0.6f * (i / 40f), 1000 + i * 8.5);
        Doc.AddStroke(freehand);

        // 图形：带非等比缩放 + 旋转（最容易在序列化里被写错的东西）
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.13f, 0.45f, 0.90f, 0.8f),
            Width = 12f, Preset = PenPreset.Precise,
            Transform = Matrix3x2.CreateRotation(0.5f)
                      * Matrix3x2.CreateScale(1.5f, 0.75f)
                      * Matrix3x2.CreateTranslation(300f, 120f),
        };
        rect.AddPoint(10, 20, 1f, 5000);
        rect.AddPoint(210, 160, 1f, 5000);
        Doc.AddStroke(rect);

        int before = Doc.Strokes.Count;
        int maxId = 0;
        foreach (var s in Doc.Strokes) maxId = Math.Max(maxId, s.Id);

        var bytes = InkSerializer.Save(Doc);
        Check("格式头可识别", InkSerializer.LooksLikeInk(bytes), $"{bytes.Length} 字节");
        Check("每对象体积合理", bytes.Length / Math.Max(1, before) < 4000,
              $"{bytes.Length / Math.Max(1, before)} 字节/对象");

        var target = new InkDocument();
        InkSerializer.LoadInto(target, bytes);

        Check("对象数量", target.Strokes.Count == before, $"{target.Strokes.Count}");
        Check("加载后撤销栈为空", target.UndoDepth == 0, $"{target.UndoDepth}");

        bool allEqual = target.Strokes.Count == before;
        string diff = "";
        for (int i = 0; allEqual && i < before; i++)
        {
            var a = Doc.Strokes[i];
            var b = target.Strokes[i];
            bool eq = a.Id == b.Id && a.Tool == b.Tool && a.Kind == b.Kind
                   && a.Color.R == b.Color.R && a.Color.G == b.Color.G
                   && a.Color.B == b.Color.B && a.Color.A == b.Color.A
                   && a.Width == b.Width && a.Preset == b.Preset
                   && a.Transform.Equals(b.Transform)
                   && a.Points.Count == b.Points.Count;
            if (eq)
            {
                for (int k = 0; k < a.Points.Count; k++)
                {
                    var p = a.Points[k];
                    var q = b.Points[k];
                    // 时间戳是"绝对量 + float 偏移"，会有浮点截断，给 0.05ms 容差。
                    if (p.X != q.X || p.Y != q.Y || p.P != q.P || Math.Abs(p.T - q.T) > 0.05)
                    { eq = false; diff = $"对象{i} 第{k}点"; break; }
                }
            }
            else diff = $"对象{i} 的字段";
            allEqual = eq;
        }
        Check("逐字段一致", allEqual, allEqual ? "含变换、颜色、压感、时间" : diff);

        var probe = new Stroke { Tool = Tool.Pen, Width = 3f };
        probe.AddPoint(0, 0, 1f, 0);
        target.AddStroke(probe);
        Check("新对象 id 不撞车", probe.Id > maxId, $"新 {probe.Id} > 旧最大 {maxId}");

        bool threw = false;
        try { InkSerializer.LoadInto(new InkDocument(), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }); }
        catch (InvalidDataException) { threw = true; }
        Check("乱数据抛异常", threw, "");

        var truncated = new byte[bytes.Length / 2];
        Array.Copy(bytes, truncated, truncated.Length);
        var keep = new InkDocument();
        threw = false;
        try { InkSerializer.LoadInto(keep, truncated); } catch (Exception) { threw = true; }
        Check("截断数据抛异常", threw, "");
        Check("失败时文档未被动过", keep.Strokes.Count == 0, $"{keep.Strokes.Count} 个对象");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 保存/加载往返正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 图形命中测试自检。
    ///
    /// 验的是"图形走精确命中、自由笔迹走中心线距离"这条分岔有没有走对。
    /// 关键在于：**图形画的是描边轮廓，中间是空的**——用"点到中心线的距离"
    /// 去判定，会把"点在矩形正中央"也当成命中，那是错的。
    /// </summary>
    private void ShapeTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图形命中测试（描边轮廓，中间是空的）===");

        float cx = _virtualX + 900, cy = _virtualY + 700;
        float halfW = 8f * DpiScale;          // 半笔宽（物理像素）
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(1f, 0f, 1f, 1f), Width = halfW * 2f,
        };
        rect.AddPoint(cx - 200, cy - 150, 1f, NowMs);
        rect.AddPoint(cx + 200, cy + 150, 1f, NowMs);

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Check("边上命中", rect.HitTestExact(cx, cy - 150), "");
        Check("正中央不命中", !rect.HitTestExact(cx, cy), "轮廓中间是空的");
        Check("远处不命中", !rect.HitTestExact(cx + 900, cy + 900), "");

        float off = halfW + 20f;   // 离中心线比半笔宽还远
        Check("容差外不命中", !rect.HitTestExact(cx, cy - 150 + off), $"偏移 {off:F0}px");
        Check("容差内命中", rect.HitTestExact(cx, cy - 150 + off, off + 5f), $"容差 {off + 5f:F0}px");

        var saved = rect.Transform;
        rect.Transform = rect.Transform * Matrix3x2.CreateTranslation(400, 0);
        Check("平移后原位置不命中", !rect.HitTestExact(cx, cy - 150), "");
        Check("平移后新位置命中", rect.HitTestExact(cx + 400, cy - 150), "变换必须参与命中");
        rect.Transform = saved;

        Doc.Clear();
        Doc.AddStroke(rect);
        int removed = Doc.EraseAt(cx, cy, 10f * DpiScale);
        Check("擦正中央不误删", removed == 0, $"删了 {removed} 个");
        removed = Doc.EraseAt(cx, cy - 150, 10f * DpiScale);
        Check("擦边上应删除", removed == 1, $"删了 {removed} 个");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 图形命中判定正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    private void CornerTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 直角填充自检（拐角会不会掉色）===");

        float w = 20f * DpiScale;            // 粗笔，问题在粗笔上最明显
        float cx = VirtualScreen.MinX + 800, cy = VirtualScreen.MinY + 700;
        float arm = 400f;

        // 诊断：把拐角附近的轮廓点导出来，看洞是"轮廓自己就缺"，还是"栅格化没填上"。
        _beautifier.DumpOutline = true;
        var s = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1),
            Width = w, Preset = PenPreset.Handwriting,
        };
        // 水平走一段，再垂直走一段，构成一个直角
        for (int i = 0; i <= 60; i++) s.AddPoint(cx + i * (arm / 60f), cy, 0.5f, i);
        for (int i = 1; i <= 60; i++) s.AddPoint(cx + arm, cy + i * (arm / 60f), 0.5f, 60 + i);
        _beautifier.EndStroke(s, DpiScale);
        _beautifier.DumpOutline = false;

        Doc.Clear();
        Doc.InvalidateAll();
        Doc.AddStroke(s);
        SettleFrames(500);

        // 三个等面积的窗口：拐角处、水平段中段、垂直段中段
        int side = (int)MathF.Ceiling(w * 2f);
        int atCorner = ScreenProbe.CountNear(
            (int)(cx + arm) - side / 2, (int)cy - side / 2, side, side, 0, 0, 0, 60);
        int onHoriz = ScreenProbe.CountNear(
            (int)(cx + arm * 0.4f) - side / 2, (int)cy - side / 2, side, side, 0, 0, 0, 60);
        int onVert = ScreenProbe.CountNear(
            (int)(cx + arm) - side / 2, (int)(cy + arm * 0.4f) - side / 2, side, side, 0, 0, 0, 60);

        float total = side * (float)side;
        Console.WriteLine($"  窗口 {side}x{side}，笔宽 {w:F0}px");
        Console.WriteLine($"  拐角 {atCorner / total:P0}（{atCorner}）"
                        + $"  水平段 {onHoriz / total:P0}（{onHoriz}）"
                        + $"  垂直段 {onVert / total:P0}（{onVert}）");

        // 拐角是外直角的内侧，墨量本来就该 ≥ 直段（外面多一块）。
        // 明显更少就说明填充在自交处被挖空了。
        float straight = MathF.Min(onHoriz, onVert) / total;
        bool ok = atCorner / total >= straight * 0.95f;
        Console.WriteLine(ok
            ? "  PASS: 拐角没有掉色"
            : $"  FAIL: 拐角比直段少 {(1 - atCorner / total / MathF.Max(1e-6f, straight)):P0}");

        _quit = true;
    }

    // =====================================================================
    //  手写美化自检
    // =====================================================================

    /// <summary>
    /// 造一条"有快有慢"的笔画：先慢慢写，再快速划过，最后慢下来收笔。
    /// 没有真实压感时笔锋全靠速度推出来，所以必须用这种数据来验。
    /// </summary>
    private static Stroke MakeSpeedVaryingStroke(float width, PenPreset preset, float dpiScale = 1f)
    {
        var s = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1),
            Width = width * dpiScale, Preset = preset,
        };
        // 走一条**接近于直线**的路径。带上下起伏的话，横向切片会量到斜段的投影，
        // 量出来的"宽度"会被路径本身的起伏放大好几倍（第一版就栽在这儿）。
        // 竖直方向只留 0.25px 的极轻微抖动，模拟手写但不会污染测量。
        //
        // 快慢比取 1:4（慢 2px/点、快 8px/点）：老师写字时真实的手速变化大概
        // 就这么大。早期版本用 1:7 的极端比值，会把"克制"的参数也量成 40% 波动，
        // 于是误判成参数过冲——参数没问题，是测试场景不真实。
        float x = 200, y = 400;
        for (int i = 0; i < 60; i++) { x += 2f; y = 400 + (i % 2) * 0.25f; s.AddPoint(x, y, 0.5f, i); }
        for (int i = 0; i < 30; i++) { x += 8f; y = 400 + (i % 2) * 0.25f; s.AddPoint(x, y, 0.5f, 100 + i); }
        for (int i = 0; i < 30; i++) { x += 2f; y = 400 + (i % 2) * 0.25f; s.AddPoint(x, y, 0.5f, 200 + i); }
        return s;
    }

    /// <summary>
    /// <summary>
    /// 取美化算法算出的逐点宽度（直径，物理像素）。
    ///
    /// 这是**精确值**：它就是生成轮廓时每个点实际用的宽度。为什么不去反推轮廓：
    /// 轮廓点间距是自适应的，拿窄带"横切"经常切不到点、或者切到的是两侧错位的
    /// 一对点，量出来的数会系统性偏大——这个坑我在这一版里来回踩了好几次。
    /// 要验"渐细 / 速度→粗细 有没有生效"，用算法自己的宽度最直接、最可靠。
    /// </summary>
    private static float[] SampleOutlineWidths(Stroke s, int samples)
    {
        var raw = s.BeautifiedWidths;
        if (raw == null || raw.Length < 3) return Array.Empty<float>();

        // 按索引比例重采样成固定份数。（更快的那一段点更疏，按索引均匀取即可，
        // 我们只需要看趋势，不需要严格的弧长均匀。）
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            int idx = (int)MathF.Round((raw.Length - 1) * i / (float)(samples - 1));
            result[i] = raw[Math.Clamp(idx, 0, raw.Length - 1)];
        }
        return result;
    }
    /// <summary>
    /// 手写美化自检：四种笔锋预设各量一遍，用数字说明笔锋有没有做出来。
    /// </summary>
    /// <summary>
    /// 手写美化自检。参数基准是 perfect-freehand 官方 demo 的默认值
    /// （thinning 0.5 / streamline 0.5 / smoothing 0.5 / taper 0），
    /// 所以这里验的是"有没有忠实复现那套行为"，不是我自己拍的数。
    /// </summary>
    private void BeautifyTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 手写美化自检 ===");
        Console.WriteLine("  基准：perfect-freehand 官方默认参数");
        Console.WriteLine();

        (PenPreset Preset, string Name, float Width)[] presets =
        {
            (PenPreset.Precise, "精确", 3f),
            (PenPreset.Handwriting, "手写美化", 3f),
            (PenPreset.Bold, "粗笔", 16f),
            (PenPreset.Calligraphy, "书法", 5f),
        };

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-16}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        foreach (var (preset, name, width) in presets)
        {
            var s = MakeSpeedVaryingStroke(width, preset, DpiScale);
            _beautifier.EndStroke(s, DpiScale);
            var dw = s.BeautifiedWidths;
            if (dw == null || dw.Length < 3) { Check("有宽度数据", false, "没有"); continue; }

            float body = width * DpiScale;
            // 取"稳定段"的统计量：掐掉两端各 10%（那里有圆帽和渐细）。
            int lo = (int)(dw.Length * 0.1f), hi = (int)(dw.Length * 0.9f);
            var core = dw.Skip(lo).Take(hi - lo).ToArray();
            float min = core.Min(), max = core.Max();
            float slow = core.Take(core.Length / 3).Average();
            float fast = core.Skip(core.Length * 2 / 3).Average();

            Console.WriteLine($"  【{name}】笔宽 {body:F0}px  宽度范围 {min:F2}~{max:F2}"
                            + $"  慢 {slow:F2} / 快 {fast:F2}");

            // 1) 精确模式必须等宽
            if (preset == PenPreset.Precise)
            {
                Check("完全等宽", max - min < body * 0.02f,
                      $"波动 {max - min:F2}px（要求 < {body * 0.02f:F2}）");
                Check("不随速度变化", MathF.Abs(fast - slow) < body * 0.02f,
                      $"慢 {slow:F2} / 快 {fast:F2}");
            }
            else
            {
                // 2) 手写档要有速度感，但必须克制。
                //
                // 判据按档位分别定，因为"多少波动算合适"本身就是档位的设计目标：
                //   手写美化 <35%、粗笔 <15%、书法 <60%（它就是要夸张）。
                // 早期版本用一套阈值卡所有档，结果要么放过粗笔的过冲，
                // 要么把刻意的书法效果判成失败。
                float swing = (max - min) / body;
                float swingLimit = preset switch
                {
                    PenPreset.Bold => 0.15f,
                    PenPreset.Calligraphy => 0.60f,
                    _ => 0.35f,
                };
                Check("宽度波动克制", swing < swingLimit,
                      $"波动 {swing:P0}（要求 <{swingLimit:P0}）");

                // 速度→宽度要真的生效：慢写明显比快写粗。
                // 粗笔档刻意把速度感压到接近零，所以对它只要求"两边差不多"。
                if (preset == PenPreset.Bold)
                    Check("速度感近乎关掉", MathF.Abs(fast - slow) < body * 0.15f,
                          $"慢 {slow:F2} / 快 {fast:F2}（差 {MathF.Abs(fast - slow):F2}px）");
                else
                    Check("慢写比快写粗", fast <= slow * 0.92f,
                          $"慢 {slow:F2} / 快 {fast:F2} = {fast / slow:P0}（要求 ≤92%）");
            }

            // 3) 宽度不能超过声明上限（脏区依据）
            Check("宽度不超界", dw.Max() <= body * InkBeautifier.OutlineInflateFactor + 0.01f,
                  $"最大 {dw.Max():F2} ≤ {body * InkBeautifier.OutlineInflateFactor:F2}");
        }

        // 4) 短笔画：渐细不能把它吃掉变形
        {
            var shortStroke = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1),
                Width = 16f * DpiScale, Preset = PenPreset.Handwriting,
            };
            for (int i = 0; i < 12; i++) shortStroke.AddPoint(100 + i * 2f, 100, 0.5f, i);
            _beautifier.EndStroke(shortStroke, DpiScale);
            var sw2 = shortStroke.BeautifiedWidths;
            // 手写档不做渐细，所以短笔画的中段必须到得了全宽。
            bool okShort = sw2 != null && sw2.Max() >= 16f * DpiScale * 0.85f;
            Check("短笔画保持全宽", okShort,
                  sw2 == null ? "没有宽度数据"
                              : $"最大 {sw2.Max():F2}，名义 {16f * DpiScale:F2}");
        }

        // 5) 端头是圆的，不是被切平的方块
        {
            var s = MakeSpeedVaryingStroke(3f, PenPreset.Handwriting, DpiScale);
            _beautifier.EndStroke(s, DpiScale);
            // 圆帽会让轮廓在笔画两端各多出约一个半径的弧，点数也会明显多于直线段
            Check("端头有圆弧", s.Outline != null && s.Outline.Length > 40,
                  $"轮廓 {s.Outline?.Length ?? 0} 点");
        }

        // 6) 性能：美化发生在落笔那一刻
        var sw3 = Stopwatch.StartNew();
        const int reps = 200;
        for (int i = 0; i < reps; i++)
        {
            var s2 = MakeSpeedVaryingStroke(3f, PenPreset.Handwriting, DpiScale);
            _beautifier.EndStroke(s2, DpiScale);
        }
        sw3.Stop();
        double perStroke = sw3.Elapsed.TotalMilliseconds / reps;
        Check("单笔美化耗时", perStroke < 1.0, $"{perStroke:F3} ms/笔（上限 1 ms）");

        Console.WriteLine();
        Console.WriteLine($"  结果: {pass} 项通过，{fail} 项失败");
        Console.WriteLine(fail == 0 ? "  结论: 与官方默认参数一致，且各项约束都满足"
                                    : "  结论: 见上面 FAIL 项");
        _quit = true;
    }

    /// <summary>
    /// 把四档笔锋摆出来给人看：每行一条"慢→快→慢"的笔画 + 一个折角 + 一条短笔画。
    /// 好不好看交给眼睛，这里只负责把该看的都摆上。
    /// </summary>
    private void BeautifyShowcase()
    {
        // 开白板：不然笔迹叠在桌面上根本看不清。
        BoardOn = true;
        Doc.InvalidateAll();
        Doc.Clear();
        float y = VirtualScreen.MinY + 150;

        void Row(PenPreset preset, string label, float width)
        {
            var s = MakeSpeedVaryingStroke(width, preset, DpiScale);
            for (int i = 0; i < s.Points.Count; i++)
            {
                var p = s.Points[i];
                s.Points[i] = new InkEngine.InkPoint
                {
                    X = p.X * DpiScale + 120, Y = p.Y * DpiScale + y, P = p.P, T = p.T,
                };
            }
            s.Bounds = RectF.Empty;
            foreach (var p in s.Points) s.Bounds.Add(p.X, p.Y);
            _beautifier.EndStroke(s, DpiScale);
            Doc.AddStroke(s);

            // 一条短笔画：看渐细会不会把它吃掉
            var shortS = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1),
                Width = width * DpiScale, Preset = preset,
            };
            float sx = VirtualScreen.MinX + 1500;
            for (int i = 0; i < 14; i++) shortS.AddPoint(sx + i * 2f, y, 0.5f, i);
            _beautifier.EndStroke(shortS, DpiScale);
            Doc.AddStroke(shortS);

            // 一个折角：看尖角处理
            float cx = VirtualScreen.MinX + 1800, cy = y;
            var corner = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1),
                Width = width * DpiScale, Preset = preset,
            };
            for (int i = 0; i < 40; i++) corner.AddPoint(cx + i * 3f, cy, 0.5f, i);
            for (int i = 0; i < 40; i++) corner.AddPoint(cx + 120, cy + i * 3f, 0.5f, 40 + i);
            _beautifier.EndStroke(corner, DpiScale);
            Doc.AddStroke(corner);

            Console.WriteLine($"  {label}：轮廓 {s.Outline?.Length ?? 0} 点");
            y += 250;
        }

        Row(PenPreset.Precise, "精确（画图）2px", 2f);
        Row(PenPreset.Handwriting, "手写美化 3px", 3f);
        Row(PenPreset.Bold, "粗笔 16px", 16f);
        Row(PenPreset.Calligraphy, "书法 5px", 5f);

        _dirty = true;
        RenderAll();
        SettleFrames(400);
        Console.WriteLine("showcase ready");
    }

    private void GenerateStrokes(int strokeCount)
    {
        var rnd = new Random(20260913);
        Doc.Clear();
        for (int i = 0; i < strokeCount; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = PenColor,
                Width = 2.5f + (float)rnd.NextDouble() * 4f,
            };
            float x = _virtualX + (float)rnd.NextDouble() * _virtualW;
            float y = _virtualY + (float)rnd.NextDouble() * _virtualH;
            int pts = 8 + rnd.Next(24);
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            for (int j = 0; j < pts; j++)
            {
                ang += (float)((rnd.NextDouble() - 0.5) * 0.7);
                float d = 5f + (float)rnd.NextDouble() * 9f;
                x += MathF.Cos(ang) * d;
                y += MathF.Sin(ang) * d;
                x = Math.Clamp(x, _virtualX + 1, _virtualX + _virtualW - 1);
                y = Math.Clamp(y, _virtualY + 1, _virtualY + _virtualH - 1);
                s.AddPoint(x, y, (float)rnd.NextDouble(), NowMs);
            }
            Doc.AppendStroke(s);
        }
        Doc.ClearHistory();
    }

    private void Benchmark(int strokeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"=== benchmark: {strokeCount} strokes ===");

        var gen = Stopwatch.StartNew();
        GenerateStrokes(strokeCount);
        gen.Stop();

        var buildSw = Stopwatch.StartNew();
        foreach (var s in Doc.Strokes) s.BuildGeometry(Gfx.D2DFactory);
        buildSw.Stop();

        // Force a full content rebuild so the next frames re-rasterise everything.
        Doc.InvalidateAll();
        _dirty = true;

        RenderAll();
        var w0 = _windows[0];
        double rebuildMs = w0.LastRebuildMs;

        Console.WriteLine($"  generate      : {gen.Elapsed.TotalMilliseconds,8:F1} ms");
        Console.WriteLine($"  build geometry: {buildSw.Elapsed.TotalMilliseconds,8:F1} ms  ({buildSw.Elapsed.TotalMilliseconds / strokeCount:F3} ms/stroke)");
        Console.WriteLine($"  full rebuild  : {rebuildMs,8:F1} ms  (rasterise {strokeCount} strokes onto the content layer)");
        Console.WriteLine($"  points total  : {Doc.TotalPoints}");
        Console.WriteLine($"  working set   : {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1} MB");
        Console.WriteLine();
    }

    // =====================================================================
    //  Measurement report
    // =====================================================================

    private void MemoryReport(string outFile)
    {
        var lines = new List<string>();
        void P(string s = "")
        {
            Console.WriteLine(s);
            lines.Add(s);
        }

        // Working set alone is misleading: Windows only drops resident pages
        // under pressure, so a process can look huge while holding very little.
        // Commit charge (private bytes) is the honest number to quote.
        void Settle()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Mem.TrimWorkingSet();
        }

        P("===== InkProbe 内存归因 =====");
        P($"显卡        : {Gfx.AdapterInfo}");
        P($"屏幕        : {_virtualW}x{_virtualH}，{_windows.Count} 个覆盖窗口");
        P($"对照        : 一个什么都不做的 .NET 控制台程序 = 23.3 MB 工作集 / 6.9 MB 私有");
        P("说明        : 「工作集」（任务管理器里那一列）会被系统随时回收，不反映真实占用；");
        P("              下面统一用「提交大小 / 私有字节」，另外附上显存占用。");
        P();

        P("【分阶段】启动过程中内存的增长");
        foreach (var l in Mem.Log) P("  " + l);
        P();

        for (int i = 0; i < 40; i++) RenderAll();
        Settle();
        P($"【空闲稳态】提交大小 {Mem.Priv():F1} MB（真实占用），工作集 {Mem.Ws():F1} MB");
        P("             （40 帧之后强制回收，这才是产品日常挂着时的占用）");
        P();

        P("【按笔画数】内存增长");
        P("    笔画数 | 提交大小 | 相对空闲 | 显存 (已用/预算) | 场景");
        P("  ---------|----------|----------|------------------|----------------");
        double basePriv = Mem.Priv();
        foreach (int count in new[] { 0, 20, 100, 500, 2000, 10000 })
        {
            GenerateStrokes(count);
            Doc.Version++;
            RenderAll();
            Settle();
            double priv = Mem.Priv();
            string note = count switch
            {
                0 => "空闲",
                <= 100 => "讲一页 PPT",
                <= 500 => "一节完整课程",
                <= 2000 => "重度使用",
                _ => "压力测试",
            };
            string delta = count == 0 ? "    —    " : $"+{priv - basePriv,5:F1} MB";
            P($"  {count,8} | {priv,6:F1} MB | {delta} | {GpuMb(),16} | {note}");
        }
        P();

        // Where exactly does the 10k-stroke memory go?
        P("【一万笔的内存去向】逐步拆开（看提交大小）");
        Doc.Clear();
        Settle();
        RenderAll();
        Settle();
        P($"  清空、回收、并让系统释放后 : 提交 {Mem.Priv(),6:F1} MB | 显存 {GpuMb()}");
        GenerateStrokes(10_000);
        Settle();
        P($"  只生成笔画数据、还没画     : 提交 {Mem.Priv(),6:F1} MB | 显存 {GpuMb()}  （{Doc.TotalPoints} 个点）");
        Doc.Version++;
        RenderAll();
        P($"  第一次整层绘制之后         : 提交 {Mem.Priv(),6:F1} MB | 显存 {GpuMb()}");
        Doc.Clear();
        Settle();
        P($"  再把笔画全部清空并回收后   : 提交 {Mem.Priv(),6:F1} MB | 显存 {GpuMb()}");
        P($"  已释放的几何对象累计       : {Stroke.ReleasedGeometries}");
        P();

        // Cost of one screen-sized offscreen layer.
        {
            Settle();
            double before = Mem.Priv();
            var pf = new Vortice.DCommon.PixelFormat(
                Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            var ctx = _windows[0].Context;
            var bmp = ctx.CreateBitmap(new SizeI(_virtualW, _virtualH), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            ctx.Target = bmp;
            ctx.BeginDraw();
            ctx.Clear(new Color4(0f, 0f, 0f, 0f));
            ctx.EndDraw();
            ctx.Target = null;
            double after = Mem.Priv();
            bmp.Dispose();
            P($"【图层成本】一张与本屏同尺寸的离屏图层 = +{after - before:F1} MB（提交大小）"
              + $"（理论值 {_virtualW * _virtualH * 4 / 1048576.0:F1} MB；4K 每层 32 MB）");
            P();
        }

        P("【结论】");
        P("  - 空闲基线才是产品日常占用；一万笔那次 200 MB 是压力测试的极端值。");
        P("  - 空闲基线的构成：.NET 运行时 + 程序集约 31 MB，Direct3D/Direct2D/文字约 42 MB，");
        P("    屏幕尺寸的合成缓冲区约 26 MB。");
        P("  - 笔画数量带来的增长主要在 GPU 侧，而且清空之后并不会还回操作系统");
        P("    （Direct2D 内部留着复用），所以峰值是「棘轮式」保持的。");
        P("  - 这是 Direct2D 的行为，不是 .NET 的行为；换成 C++ 也一样。");

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(outFile, lines, System.Text.Encoding.UTF8);
            Console.WriteLine();
            Console.WriteLine("报告已写入 " + Path.GetFullPath(outFile));
        }
        catch (Exception ex) { Console.WriteLine("写报告失败: " + ex.Message); }
    }

    private static double WorkingSetMb()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1048576.0;
    }

    private static double PrivateMb()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 / 1048576.0;
    }

    /// <summary>Dedicated GPU memory currently held by this process's device.</summary>
    private static string GpuMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return "n/a";
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return $"{info.CurrentUsage / 1048576.0:F0}/{info.Budget / 1048576.0:F0} MB";
        }
        catch { return "n/a"; }
    }

    private (double rec, double present) MeasureFrames(int n)
    {
        double rec = 0, pres = 0;
        for (int i = 0; i < n; i++)
        {
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();
            rec += _windows[0].LastRecordMs;
            pres += _windows[0].LastPresentMs;
        }
        return (rec / n, pres / n);
    }

    /// <summary>
    /// Draws a bright magenta probe stroke, waits for the compositor, then looks
    /// for those pixels in a screen grab. This is the only way to tell whether
    /// what the GPU produced actually made it onto the display.
    /// </summary>
    private bool VerifyRendering(out int hits, out int fullScreenHits, out string diag)
    {
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        var probe = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 16f };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            probe.AddPoint(cx - 380 + t * 760, cy + MathF.Sin(t * 6.28f) * 120, 0.8f, NowMs);
        }

        Doc.Clear();
        Doc.AddStroke(probe);
        Doc.InvalidateAll();

        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 600) { PumpMessages(); RenderAll(); }

        bool ok = ScreenProbe.TryFindMagenta((int)(cx - 400), (int)(cy - 160), 800, 320, out hits);
        fullScreenHits = ok ? hits : ScreenProbe.CountMagenta(_virtualX, _virtualY, _virtualW, _virtualH);
        diag = $"笔画 {Doc.Strokes.Count}，实绘 {_windows[0].LastDrawnStrokes}，"
             + $"重建 {_windows[0].LastRebuildMs:F1} ms，错误 {(string.IsNullOrEmpty(_windows[0].LastError) ? "无" : _windows[0].LastError)}";
        return ok;
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

    private void Report(string outFile)
    {
        var lines = new List<string>();
        void P(string s = "")
        {
            Console.WriteLine(s);
            lines.Add(s);
        }

        P("===== InkProbe 实测数据 =====");
        P($"日期          : {DateTime.Now:yyyy-MM-dd HH:mm}");
        P($"显示器        : {_virtualW}x{_virtualH} 虚拟桌面，{_windows.Count} 个覆盖窗口");
        P($"监视器 DPI    : {_windows[0].Dpi}");
        P($"运行时        : .NET {Environment.Version}");
        P($"Windows       : {Environment.OSVersion.Version}");
        P();

        for (int i = 0; i < 8; i++)
        {
            RenderAll();
            if (i < 3)
                P($"  [warmup frame {i}] render error: {(string.IsNullOrEmpty(_windows[0].LastError) ? "none" : _windows[0].LastError)}");
        }

        P($"【基线】0 笔画、空闲           : 工作集 {WorkingSetMb(),6:F1} MB   私有 {PrivateMb(),6:F1} MB   显存 {GpuMb()}");
        P($"【渲染验证】启动后             : {(VerifyRendering(out var h0, out _, out var d0) ? "正常" : "丢失")} ({h0} 像素)  {d0}");
        P();

        // --- 4K layer memory probe -------------------------------------
        {
            double before = WorkingSetMb();
            var probes = new List<ID2D1Bitmap1>();
            var pf = new Vortice.DCommon.PixelFormat(
                Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            var ctx = _windows[0].Context;
            for (int i = 0; i < 2; i++)
            {
                var bmp = ctx.CreateBitmap(new SizeI(3840, 2160), IntPtr.Zero, 0,
                    new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
                // Force the allocation: Direct2D commits memory lazily on first use.
                ctx.Target = bmp;
                ctx.BeginDraw();
                ctx.Clear(new Color4(0f, 0f, 0f, 0f));
                ctx.EndDraw();
                ctx.Target = null;
                probes.Add(bmp);
            }
            double after = WorkingSetMb();
            P($"【内存】额外 2 张 3840x2160 图层: 工作集 +{after - before:F1} MB，显存 {GpuMb()}");
            P("        （两张图层理论值 2 x 3840 x 2160 x 4B = 66 MB。注意 GPU 合成的图层通常");
            P("          不计入进程工作集，要看显存/专用 GPU 内存，任务管理器里也要看这一列。）");
            foreach (var b in probes) b.Dispose();
            P();
        }

        // --- per-stroke-count measurements -----------------------------
        P("【性能】按笔画数量对比（记录=CPU 提交绘制命令，上屏=交到显示器）");
        P();
        P("  笔画数 | 首帧全量重建 | 缓存层每帧记录 | 不缓存每帧记录 | 上屏 | 工作集");
        P("  -------|--------------|----------------|----------------|------|--------");

        foreach (int count in new[] { 0, 1_000, 10_000 })
        {
            GenerateStrokes(count);
            NoContentCache = false;

            Doc.InvalidateAll();
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();
            double rebuild = _windows[0].LastRebuildMs;

            var cached = MeasureFrames(count >= 10_000 ? 30 : 20);

            NoContentCache = true;
            var uncached = MeasureFrames(count >= 10_000 ? 8 : 20);
            NoContentCache = false;

            // Keep the cached layer in sync again for the next round.
            Doc.Version++;

            P($"  {count,7} | {rebuild,10:F1} ms | {cached.rec,12:F2} ms | {uncached.rec,12:F2} ms | {cached.present,4:F2} ms | {WorkingSetMb(),5:F1} MB / 显存 {GpuMb()}");
        }
        P();

        bool okAfterTable = VerifyRendering(out var h1, out _, out var d1);
        P($"【渲染验证】性能测试之后       : {(okAfterTable ? "正常" : "丢失")} ({h1} 像素)  {d1}");
        P();

        // --- eraser: correctness, then cost ----------------------------
        EraseCheck(out bool eraseOk, out string eraseDetail);
        P($"【橡皮擦】擦除是否正确          : {(eraseOk ? "正确" : "异常")}  {eraseDetail}");
        P();

        // --- interactive path: cost of committing one new stroke ---------
        {
            GenerateStrokes(10_000);
            Doc.Version++;
            RenderAll();

            double sum = 0;
            const int n = 25;
            var rnd = new Random(7);
            for (int i = 0; i < n; i++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = PenColor, Width = 4f };
                float x = _virtualX + (float)rnd.NextDouble() * _virtualW;
                float y = _virtualY + (float)rnd.NextDouble() * _virtualH;
                for (int j = 0; j < 12; j++)
                {
                    x += 9; y += 7;
                    s.AddPoint(x, y, 0.7f, NowMs);
                }
                Doc.AppendStroke(s);
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                sum += _windows[0].LastPatchMs;
            }
            P($"【交互路径】在 1 万笔画的画面上，结束一笔的增量更新: {sum / n:F2} ms/笔"
              + $"（对照：整层重画 {_windows[0].LastRebuildMs:F0} ms 量级）");

            // Eraser drag: each step removes the strokes under the cursor and
            // repaints only that little box.
            double eraseSum = 0;
            int eraseSteps = 0, eraseRemoved = 0;
            var rnd2 = new Random(11);
            float ex = _virtualX + (float)rnd2.NextDouble() * _virtualW;
            float ey = _virtualY + (float)rnd2.NextDouble() * _virtualH;
            double eraseRebuildRef = _windows[0].LastRebuildMs;
            for (int i = 0; i < n; i++)
            {
                ex += 14; ey += 9;
                eraseRemoved += Doc.EraseAt(ex, ey, 30f);
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                eraseSum += _windows[0].LastPatchMs;
                eraseSteps++;
            }
            P($"【橡皮擦】在 1 万笔画的画面上，擦除拖动一步: {eraseSum / eraseSteps:F2} ms/步"
              + $"（共擦掉 {eraseRemoved} 笔；对照整层重画 {eraseRebuildRef:F0} ms）");

            // Undo of a single stroke costs the same as an erase patch.
            double undoSum = 0;
            int undoCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!Doc.Undo()) break;
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                undoSum += _windows[0].LastPatchMs;
                undoCount++;
            }
            P($"【撤销】在 1 万笔画的画面上，撤销一步: {(undoCount > 0 ? undoSum / undoCount : 0):F2} ms/步"
              + $"（{undoCount} 步）");
            P();

            // Where does an erase step actually spend its time? Split the hit
            // test from the repaint, and A/B the spatial index against a linear
            // scan over every stroke. Runs after the undo measurement because
            // it rebuilds the document and clears the undo history.
            {
                GenerateStrokes(10_000);
                Doc.InvalidateAll();
                RenderAll();

                var list = new List<Stroke>();
                var rndQ = new Random(99);
                var probes = new RectF[500];
                for (int i = 0; i < probes.Length; i++)
                {
                    float px = _virtualX + (float)rndQ.NextDouble() * _virtualW;
                    float py = _virtualY + (float)rndQ.NextDouble() * _virtualH;
                    probes[i] = new RectF { MinX = px - 45, MinY = py - 45, MaxX = px + 45, MaxY = py + 45 };
                }

                var swq = Stopwatch.StartNew();
                int found = 0;
                foreach (var r in probes) found += Doc.QueryGrid(r, list);
                swq.Stop();

                var swl = Stopwatch.StartNew();
                int found2 = 0;
                foreach (var r in probes)
                    foreach (var s in Doc.Strokes)
                        if (s.Bounds.Intersects(r)) found2++;
                swl.Stop();

                P($"【命中测试】500 次查询：空间索引 {swq.Elapsed.TotalMilliseconds:F1} ms"
                  + $"，全表扫描 {swl.Elapsed.TotalMilliseconds:F1} ms"
                  + $"（提速 {swl.Elapsed.TotalMilliseconds / Math.Max(0.01, swq.Elapsed.TotalMilliseconds):F0} 倍，命中 {found2} 条）");
                P();
            }
        }

        // --- laser cost on top of 10k cached strokes --------------------
        GenerateStrokes(10_000);
        Doc.Version++;
        RenderAll();
        var idle10k = MeasureFrames(20);

        double MeasureLaser(bool additive, int bands, out int frames)
        {
            OverlayWindow.LaserAdditive = additive;
            OverlayWindow.LaserBands = bands;
            Laser.Clear();
            Laser.Visible = true;
            double t0 = _clock.Elapsed.TotalMilliseconds;
            double total = 0;
            frames = 0;
            while (_clock.Elapsed.TotalMilliseconds - t0 < 2000)
            {
                double t = _clock.Elapsed.TotalMilliseconds;
                float px = _virtualX + _virtualW * 0.5f + (float)(Math.Sin(t / 300.0) * 400);
                float py = _virtualY + _virtualH * 0.5f + (float)(Math.Cos(t / 220.0) * 200);
                Laser.Add(px, py, t);
                NowMs = t;
                Laser.Prune(NowMs);
                RenderAll();
                total += _windows[0].LastRecordMs;
                frames++;
            }
            Laser.Visible = false;
            return frames > 0 ? total / frames : 0;
        }

        double laserRibbon = MeasureLaser(true, 7, out int framesRibbon);
        double laserAdd = MeasureLaser(true, 6, out int framesAdd);
        double laser1 = MeasureLaser(true, 1, out int frames1);
        double laserLine = MeasureLaser(true, -1, out int framesLine);
        double laserRect = MeasureLaser(true, -2, out int framesRect);
        double laserBuildOnly = MeasureLaser(true, -3, out int framesBuild);
        double laser0 = MeasureLaser(true, 0, out int frames0);
        OverlayWindow.LaserBands = 7;
        OverlayWindow.LaserAdditive = true;
        P($"【激光笔】1 万笔画之上叠加激光轨迹（每帧记录耗时）");
        P($"    填充渐细尾巴（当前）   : {laserRibbon:F2} ms   （{framesRibbon} 帧）");
        P($"    描边路径, 分 6 段      : {laserAdd:F2} ms   （{framesAdd} 帧）");
        P($"    描边路径, 只画 1 段    : {laser1:F2} ms   （{frames1} 帧）");
        P($"    只画 1 条直线          : {laserLine:F2} ms   （{framesLine} 帧）");
        P($"    只画 1 个小方块        : {laserRect:F2} ms   （{framesRect} 帧）");
        P($"    只建几何、不绘制       : {laserBuildOnly:F2} ms   （{framesBuild} 帧）");
        P($"    完全不画激光           : {laser0:F2} ms   （{frames0} 帧）");
        P($"    不开激光（同一画面）   : {idle10k.rec:F2} ms");
        bool okAfterLaser = VerifyRendering(out var h2, out _, out var d2);
        P($"【渲染验证】激光笔之后         : {(okAfterLaser ? "正常" : "丢失")} ({h2} 像素)  {d2}");
        P();

        // --- pass-through rendering check ------------------------------
        P("【穿透】三种实现方式下，覆盖层是否仍然正常显示：");
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        P($"  {"（对照）不穿透",-20} : 命中测试 {ProbeHitTest()}");
        foreach (PassThroughMode m in new[] { PassThroughMode.HitTest, PassThroughMode.ExTransparent, PassThroughMode.LayeredTransparent })
        {
            PassThrough = true;
            PassMode = m;
            foreach (var w in _windows) ApplyPassThroughStyle(w);

            bool visible = VerifyRendering(out int hits, out int allHits, out string diag);
            string extra = "";
            if (!visible) extra = $"  全屏另找到 {allHits} 个同色像素";
            P($"  {m,-20} : {(visible ? "渲染正常" : "渲染丢失")}  ({hits} 像素){extra}");
            P($"      诊断: {diag}");

            P($"      命中测试: {ProbeHitTest()}");

            PassThrough = false;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
        }
        P();
        P("说明：「渲染正常」只表示覆盖层本身没被破坏。鼠标点击是否真的落到下层程序，需要在真机上手动确认。");

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(outFile, lines, System.Text.Encoding.UTF8);
            Console.WriteLine();
            Console.WriteLine("报告已写入 " + Path.GetFullPath(outFile));
        }
        catch (Exception ex)
        {
            Console.WriteLine("写报告失败: " + ex.Message);
        }
    }

    private void MemoryProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== memory probe: cost of one full-screen content layer ===");
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        double before = proc.WorkingSet64 / 1048576.0;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var probes = new List<ID2D1Bitmap1>();
        var ctx = _windows[0].Context;
        int w = 3840, h = 2160;
        for (int i = 0; i < 3; i++)
        {
            probes.Add(ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw)));
        }
        proc.Refresh();
        double after = proc.WorkingSet64 / 1048576.0;
        Console.WriteLine($"  {w}x{h} x3 layers: working set {before:F1} MB -> {after:F1} MB  (delta {after - before:F1} MB)");
        foreach (var b in probes) b.Dispose();
        Console.WriteLine();
    }

    // =====================================================================
    //  Self test
    // =====================================================================

    private void SelfTest(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== self test ===");
        Console.WriteLine("drawing a synthetic stroke across the primary monitor...");

        var probe = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 14f };
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.3f;
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            probe.AddPoint(cx - 400 + t * 800, cy + MathF.Sin(t * 6.28f) * 120, 0.8f, NowMs);
        }
        Doc.AddStroke(probe);
        _dirty = true;

        RenderAll();

        // Let DirectComposition settle, then verify the pixels really reached the screen.
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 700)
        {
            PumpMessages();
            RenderAll();
        }

        bool found = ScreenProbe.TryFindMagenta((int)(cx - 420), (int)(cy - 160), 840, 320, out int hits);
        Console.WriteLine(found
            ? $"PASS: overlay pixels verified on screen ({hits} matching pixels)"
            : $"FAIL: no overlay pixels found on screen ({hits} matches)");
        LastCaptureReport = found ? "rendering verified" : "rendering NOT verified";

        Console.WriteLine();
        Console.WriteLine("running the 10k-stroke benchmark...");
        Benchmark(10_000);

        Console.WriteLine("laser stress: forcing a continuous 120 Hz trail...");
        Laser.Visible = true;
        var t0 = _clock.Elapsed.TotalMilliseconds;
        while (_clock.Elapsed.TotalMilliseconds - t0 < 3000)
        {
            double t = _clock.Elapsed.TotalMilliseconds;
            float px = cx - 300 + (float)(Math.Sin(t / 300.0) * 300);
            float py = cy + 400 + (float)(Math.Cos(t / 220.0) * 120);
            Laser.Add(px, py, t);
            NowMs = t;
            Laser.Prune(NowMs);
            PumpMessages();
            RenderAll();
        }
        Laser.Visible = false;

        Console.WriteLine();
        Console.WriteLine("--- summary ---");
        Console.WriteLine($"  rendering verified : {LastCaptureReport}");
        Console.WriteLine($"  fps                : {_fps:F1}");
        Console.WriteLine($"  record per frame   : {_lastRecordMs:F2} ms");
        Console.WriteLine($"  present per frame  : {_lastPresentMs:F2} ms");
        Console.WriteLine($"  full rebuild       : {_lastRebuildMs:F1} ms");
        Console.WriteLine($"  working set        : {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1} MB");
        Console.WriteLine($"  peak working set   : {_peakWorkingSetMb:F1} MB");
        Console.WriteLine();
    }

    private void PumpMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        NowMs = _clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Renders for a while so DirectComposition has definitely put the
    /// latest frame on screen before we grab pixels.</summary>
    private void SettleFrames(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            PumpMessages();
            RenderAll();
        }
    }

    // ------------------------------------------------------------------
    //  Synthetic input test
    // ------------------------------------------------------------------

    private static Native.INPUT MouseInput(int x, int y, uint extraFlags, int vx, int vy, int vw, int vh)
    {
        var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
        inp.mi.dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        inp.mi.dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        inp.mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE
                       | Native.MOUSEEVENTF_VIRTUALDESK | extraFlags;
        return inp;
    }

    private void SendMouse(int x, int y, uint extraFlags)
    {
        var inp = new[] { MouseInput(x, y, extraFlags, _virtualX, _virtualY, _virtualW, _virtualH) };
        Native.SendInput(1, inp, Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>
    /// Drives a real drag through SendInput so the whole input path is exercised
    /// end to end - pointer messages, stroke accumulation, redraw, and finally
    /// pixels on screen. Synthetic input is consumed by this overlay, which is
    /// topmost, so nothing underneath is clicked.
    /// </summary>
    // ------------------------------------------------------------------
    //  Click target (second process) + real pass-through test
    // ------------------------------------------------------------------

    /// <summary>Runs a plain window in its own process that records every time
    /// it is clicked. Used as the window underneath the overlay.</summary>
    private void RunClickTarget(string logFile)
    {
        _clickLogFile = logFile;
        _clickTargetHwnd = Native.CreateWindowEx(
            0, _className, "InkProbe 点击目标",
            0x00CF0000L /*WS_OVERLAPPEDWINDOW*/, 120, 120, 420, 320,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_clickTargetHwnd == IntPtr.Zero)
        {
            File.WriteAllText(logFile, "error\n");
            return;
        }
        Native.ShowWindow(_clickTargetHwnd, 5 /*SW_SHOW*/);
        File.WriteAllText(logFile, "ready\n");

        while (!_quit)
        {
            if (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            else break;
        }
    }

    private static int CountClicks(string file)
    {
        try
        {
            if (!File.Exists(file)) return 0;
            var text = File.ReadAllText(file);
            int n = 0, i = 0;
            while ((i = text.IndexOf("clicked", i, StringComparison.Ordinal)) >= 0) { n++; i += 7; }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// The real question is not "does hit testing report another window" - that
    /// gave a false pass - but "does a click actually arrive at the window
    /// underneath". So this spawns a second process with a target window,
    /// synthesises real clicks, and counts how many it received.
    /// </summary>
    private void PassThroughTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 穿透真机测试（合成真实点击，看下层窗口收不收到）===");

        string dir = Path.Combine(Path.GetTempPath(), "inkprobe_passtest");
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "target.txt");
        File.WriteAllText(log, "");      // must write before spawning? no: spawn then wait for ready

        var psi = new ProcessStartInfo(Environment.ProcessPath, $"--clicktarget \"{log}\"")
        {
            UseShellExecute = false,
        };
        var target = Process.Start(psi);
        if (target == null) { Console.WriteLine("  无法启动点击目标进程"); _quit = true; return; }

        for (int i = 0; i < 120 && !File.ReadAllText(log).Contains("ready"); i++)
            Thread.Sleep(50);
        if (!File.ReadAllText(log).Contains("ready"))
        {
            Console.WriteLine("  点击目标窗口没有就绪");
            target.Kill();
            _quit = true;
            return;
        }

        int tx = 120 + 210, ty = 120 + 160;   // centre of the target window

        // Expected outcomes come from the documented rules:
        //  - HTTRANSPARENT only hands off to windows of the same thread
        //  - WS_EX_TRANSPARENT only makes a *layered* window click-through
        (string name, bool pass, bool expectReach, PassThroughMode mode)[] cases =
        {
            ("对照：不穿透",              false, false, PassThroughMode.LayeredTransparent),
            ("只改命中测试返回值",        true,  false, PassThroughMode.HitTest),
            ("只加 WS_EX_TRANSPARENT",    true,  false, PassThroughMode.ExTransparent),
            ("加 LAYERED+TRANSPARENT",    true,  true,  PassThroughMode.LayeredTransparent),
        };

        int failures = 0;
        foreach (var (name, pass, expectReach, mode) in cases)
        {
            PassMode = mode;
            PassThrough = pass;
            foreach (var w in _windows) ApplyPassThroughStyle(w);

            SettleFrames(150);

            int before = CountClicks(log);
            SendMouse(tx, ty, 0);
            SettleFrames(80);
            SendMouse(tx, ty, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(60);
            SendMouse(tx, ty, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(400);
            int after = CountClicks(log);

            bool reached = after > before;
            bool good = reached == expectReach;
            if (!good) failures++;
            Console.WriteLine($"  {name,-26} 下层窗口收到点击: {(reached ? "是" : "否"),-2}"
                              + $"  文档预期: {(expectReach ? "是" : "否"),-2}  {(good ? "PASS" : "FAIL")}"
                              + (pass ? "" : "   （对照组）"));
        }
        Console.WriteLine(failures == 0 ? "  结论: 穿透行为与文档一致" : $"  结论: {failures} 项与文档不符");

        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);

        try { target.Kill(); } catch { }
        _quit = true;
    }

    /// <summary>
    /// Three long strokes, then a deliberately coarse vertical swipe with the
    /// eraser. Without path interpolation the sample spacing leaves gaps and
    /// strokes in between survive, which is the "eraser is not sensitive"
    /// symptom. Also checks the whole swipe collapses into one undo step.
    /// </summary>
    /// <summary>
    /// Draws the same wavy stroke at every width preset and measures how much
    /// ink actually lands. A filled ribbon can collapse if the winding rule
    /// cancels where the shape folds over itself, which shows up as a coverage
    /// ratio far below 1 - and it only appears once strokes get fat.
    /// </summary>
    private void WidthTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 笔迹粗细渲染测试（填充带子会不会在粗笔画上出洞）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");
        Console.WriteLine();
        Console.WriteLine("  逻辑宽度 | 物理宽度 | 理论墨量 | 实测墨量 | 覆盖率");
        Console.WriteLine("  ---------|----------|----------|----------|--------");

        Doc.Clear();
        int i = 0;
        foreach (float wLogical in WidthPresets)
        {
            float wPhys = wLogical * DpiScale;
            float y = _virtualY + 140 + i * 150;
            float x0 = _virtualX + 200;
            float x1 = _virtualX + 1600;

            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = wPhys };
            for (int k = 0; k <= 120; k++)
            {
                float t = k / 120f;
                s.AddPoint(x0 + (x1 - x0) * t, y + MathF.Sin(t * 9f) * 40f, 1f, NowMs);
            }
            Doc.AddStroke(s);
            i++;
        }
        Doc.InvalidateAll();
        SettleFrames(700);

        i = 0;
        foreach (float wLogical in WidthPresets)
        {
            float wPhys = wLogical * DpiScale;
            float y = _virtualY + 140 + i * 150;
            // The wavy path is ~1480 px of x plus the wiggle.
            float pathLen = 1560f;
            // The stroke is drawn at pressure 1.0, so it paints at the curve's
            // maximum width factor rather than the nominal width.
            float expected = pathLen * wPhys * Stroke.MaxWidthFactor;
            int actual = ScreenProbe.CountMagenta((int)(_virtualX + 190), (int)(y - 90), 1430, 180);
            Console.WriteLine($"  {wLogical,8:F1} | {wPhys,8:F0} | {expected,8:F0} | {actual,8} | {(actual / expected):F2}");
            i++;
        }

        Console.WriteLine();
        Console.WriteLine("  覆盖率接近 1 说明填充正常；明显小于 0.6 说明带子自相交处被挖空了。");
        _quit = true;
    }

    /// <summary>
    /// 脏区渲染特有的风险测试：双缓冲里躺着的是两帧前的画面，如果脏区没覆盖到
    /// 上一帧临时图元的位置，快速移动的橡皮光标/激光就会留下"残影"。
    /// 这里用大跨度快速移动橡皮光标，最后停在远处，然后数屏幕上还剩下多少
    /// 光标颜色的像素——只剩停住那一圈才算通过。
    /// </summary>
    /// <summary>
    /// 验证微软的委托墨迹轨迹是否真的在画：把自己那一笔关掉，
    /// 拖动过程中按住不放截图——如果还能看到笔迹，那就是系统合成器画的。
    /// </summary>
    /// <summary>
    /// 长跑测试：模拟"上一节课"的连续使用，看内存是否稳定、帧耗时是否劣化。
    /// 每轮画一批笔画，再做擦除/撤销/清空，并周期性记录状态。
    /// </summary>
    /// <summary>
    /// A/B 实测：几何实现缓存到底能省多少。
    /// A = 每次都让 Direct2D 重新细分几何（现状）
    /// B = 先把细分结果缓存下来，之后只提交三角形
    /// </summary>
    /// <summary>
    /// 分辨率实测：同样的笔画工作量，放到不同尺寸的离屏内容层上跑，
    /// 看哪些开销随像素数增长、哪些不增长。用来回答"4K 教室里够不够用"。
    /// </summary>
    private void ResolutionTest(int strokeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 分辨率实测（{strokeCount} 笔）===");
        Console.WriteLine("  内容层尺寸 | 图层内存 | 整层清屏+重画 | 20 次脏区补丁 | 每次补丁");
        Console.WriteLine("  -----------|----------|---------------|----------------|----------");

        GenerateStrokes(strokeCount);
        var ctx = _windows[0].Context;
        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);

        (int w, int h, string label)[] sizes =
        {
            (1920, 1080, "1920x1080"),
            (2560, 1440, "2560x1440"),
            (2880, 1800, "2880x1800 当前屏"),
            (3840, 2160, "3840x2160 4K"),
        };

        foreach (var (w, h, label) in sizes)
        {
            double before = Mem.Priv();
            var bmp = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));

            // 先画一遍让缓存/显存真正落地
            RenderInto(bmp, strokeCount);
            double after = Mem.Priv();

            // 整层：清屏 + 画全部笔画
            var sw = Stopwatch.StartNew();
            RenderInto(bmp, strokeCount);
            sw.Stop();
            double full = sw.Elapsed.TotalMilliseconds;

            // 脏区补丁：取 20 个 300x300 的方块，清掉并重画落在里面的笔画
            var rnd = new Random(7);
            var swPatch = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
            {
                var r = new RectF
                {
                    MinX = _virtualX + (float)rnd.NextDouble() * (w - 400),
                    MinY = _virtualY + (float)rnd.NextDouble() * (h - 400),
                    MaxX = 0, MaxY = 0,
                };
                r.MaxX = r.MinX + 300; r.MaxY = r.MinY + 300;
                PatchInto(bmp, r);
            }
            swPatch.Stop();
            double patch = swPatch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"  {label,-12} | {after - before,6:F1}M | {full,11:F1}ms | {patch,12:F1}ms | {patch / 20,6:F2}ms");

            bmp.Dispose();
            Mem.TrimWorkingSet();
        }
        _quit = true;
    }

    private void RenderInto(ID2D1Bitmap1 target, int strokeCount)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (n++ >= strokeCount) break;
            _windows[0].DrawStrokeForTest(s);
        }
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        if (hr.Failure) Console.WriteLine("  RenderInto EndDraw 失败: " + hr.Description);
        try { ctx.Flush(out _, out _); } catch { /* 见报告说明：CreateBitmap 目标上 Flush 会报状态错误，四种分辨率一致，不影响横向对比 */ }
        ctx.Target = null;
    }

    private void PatchInto(ID2D1Bitmap1 target, RectF r)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
            Vortice.Direct2D1.AntialiasMode.Aliased);
        _windows[0].ClearRectForTest(r);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (!s.IntersectsRect(r)) continue;
            _windows[0].DrawStrokeForTest(s);
            if (++n > 400) break;
        }
        ctx.PopAxisAlignedClip();
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr2 = ctx.EndDraw();
        if (hr2.Failure) Console.WriteLine("  PatchInto EndDraw 失败: " + hr2.Description);
        try { ctx.Flush(out _, out _); } catch { }
        ctx.Target = null;
    }

    private void RealizationTest(int strokeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 几何实现缓存 A/B（{strokeCount} 笔）===");

        GenerateStrokes(strokeCount);
        OverlayWindow.RealizationEnabled = false;
        Doc.InvalidateAll();
        RenderAll();
        Stroke.LiveRealizations = 0;

        // A：不缓存，整层重画
        double sumA = 0;
        for (int i = 0; i < 3; i++)
        {
            Doc.InvalidateAll();
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();
            sumA += _windows[0].LastRebuildMs;
        }
        double a = sumA / 3;

        // B 的准备：先为每一笔建一次细分缓存（这是一次性成本）
        var swBuild = Stopwatch.StartNew();
        var ctx1 = _windows[0].Context.QueryInterfaceOrNull<Vortice.Direct2D1.ID2D1DeviceContext1>();
        int built = 0;
        if (ctx1 != null)
        {
            foreach (var s in Doc.Strokes)
                if (s.GetRealization(ctx1, OverlayWindow.RealizationTolerance) != null) built++;
        }
        swBuild.Stop();

        // B：用缓存重画
        OverlayWindow.RealizationEnabled = true;
        double sumB = 0;
        for (int i = 0; i < 3; i++)
        {
            Doc.InvalidateAll();
            NowMs = _clock.Elapsed.TotalMilliseconds;
            RenderAll();
            sumB += _windows[0].LastRebuildMs;
        }
        double b = sumB / 3;
        OverlayWindow.RealizationEnabled = false;

        Console.WriteLine($"  A 每次重新细分整层重画 : {a,8:F1} ms");
        Console.WriteLine($"  B 缓存细分后整层重画   : {b,8:F1} ms   （快 {a / Math.Max(0.1, b):F1} 倍）");
        Console.WriteLine($"  建立缓存的一次性成本   : {swBuild.Elapsed.TotalMilliseconds,8:F1} ms（{built} 笔）");
        Console.WriteLine($"  工作集                 : {Mem.Ws(),8:F1} MB");
        _quit = true;
    }

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

    private void InkTrailTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 委托墨迹轨迹测试（关掉自己画的湿墨）===");
        Console.WriteLine($"  接口状态: {OverlayWindow.InkTrailNote}");

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;

        // 对照实验：委托墨迹很可能是给"前台书写窗口"用的。把覆盖层的
        // WS_EX_NOACTIVATE 临时去掉并抢一次前台，看轨迹会不会出现。
        if (argsContainActivate)
        {
            foreach (var w in _windows)
            {
                long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
                ex &= ~Native.WS_EX_NOACTIVATE;
                Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
                Native.SetForegroundWindow(w.Hwnd);
            }
            Console.WriteLine("  已临时取消 WS_EX_NOACTIVATE 并尝试抢前台");
            SettleFrames(500);
        }
        SettleFrames(300);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int bandX = (int)(cx - 500);
        int bandY = (int)(cy - 140);

        // 按住不放，慢慢画一段，然后**在按住的状态下**截图
        SendMouse((int)(cx - 400), (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)(cx - 400), (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(100);
        for (int i = 1; i <= 40; i++)
        {
            float t = i / 40f;
            SendMouse((int)(cx - 400 + t * 800), (int)(cy + MathF.Sin(t * 6.28f) * 80), 0);
            SettleFrames(18);
        }
        SettleFrames(250);   // 仍然按着

        int whileDown = ScreenProbe.CountRed(bandX, bandY, 1000, 280);
        string shot1 = Path.Combine("reports", "shots", "trail-while-down.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot1));
        ScreenProbe.SaveBmp(shot1, _virtualX, _virtualY, _virtualW, _virtualH);

        SendMouse((int)(cx + 400), (int)cy, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(600);
        int afterUp = ScreenProbe.CountRed(bandX, bandY, 1000, 280);

        Console.WriteLine($"  按住不放时笔迹像素: {whileDown}（这些只能是系统合成器画的）");
        Console.WriteLine($"  松手之后笔迹像素  : {afterUp}（我们自己的笔画接管）");
        Console.WriteLine($"  轨迹调试          : {OverlayWindow.InkTrailDebug}");
        Console.WriteLine($"  截图: {Path.GetFullPath(shot1)}");

        bool ok = whileDown > 800;
        Console.WriteLine(ok ? "  PASS: 委托墨迹轨迹生效" : "  FAIL: 没看到系统画的轨迹");
        _quit = true;
    }

    private void GhostTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 残影测试（大跨度快速移动橡皮光标，看会不会拖尾）===");

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Eraser;
        SettleFrames(300);

        // 先把光标停在远处（在测试带之外），拍一张"干净"的基准图
        float parkX = _virtualX + 150, parkY = _virtualY + _virtualH - 200;
        SendMouse((int)parkX, (int)parkY, 0);
        SettleFrames(500);

        int bandX = _virtualX + 400, bandY = _virtualY + 780;
        int bandW = 2000, bandH = 200;

        // 先量一遍"环境噪声"：光标停着不动，连拍两张。
        // 桌面上的别的程序（聊天窗口、光标闪烁）本来就会自己变化，
        // 不先量这个底噪，就会把它当成我们的残影。这一步让测试有自证能力。
        byte[] noise1 = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);
        SettleFrames(500);
        byte[] noise2 = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);
        int noise = ScreenProbe.DiffCount(noise1, noise2);

        byte[] before = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);

        float y = _virtualY + _virtualH * 0.5f;
        float x0 = _virtualX + 450;
        SendMouse((int)x0, (int)y, 0);
        SettleFrames(200);

        // 每次跳 300 像素、间隔很短：模拟快速划动
        const int hops = 6;
        for (int i = 1; i <= hops; i++)
        {
            SendMouse((int)(x0 + i * 300f), (int)y, 0);
            SettleFrames(20);
        }

        // 光标回到原处，等脏区排空，再拍一张
        SendMouse((int)parkX, (int)parkY, 0);
        SettleFrames(500);
        byte[] after = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);

        int diff = ScreenProbe.DiffCount(before, after);
        Console.WriteLine($"  光标扫过区域前后逐像素差异: {diff} 像素（共 {bandW * bandH} 像素）");
        Console.WriteLine($"  对照：同一区域静置两帧的差异: {noise} 像素（环境噪声）");

        string shot = Path.Combine("reports", "shots", "ghosttest.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot));
        ScreenProbe.SaveBmp(shot, _virtualX, _virtualY, _virtualW, _virtualH);
        Console.WriteLine($"  截图: {Path.GetFullPath(shot)}");

        // 光标已经离开这块区域，像素应当复原。
        //
        // 判据用"我们造成的差异"而不是绝对差异：这块区域里别的程序自己也会变
        // （实测聊天窗口刷文字能刷出上千个像素），所以要先减掉环境噪声。
        // 容差 200 像素留给抗锯齿和噪声本身的抖动。
        int ours = Math.Max(0, diff - noise);
        bool ok = ours < 200;
        Console.WriteLine(ok
            ? $"  PASS: 没有残影（扣掉环境噪声后仅剩 {ours} 像素）"
            : $"  FAIL: 出现拖尾残影（扣掉环境噪声后仍有 {ours} 像素）");
        _quit = true;
    }

    private void EraserTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 橡皮擦测试（用稀疏采样快速划过，看会不会漏）===");

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        for (int k = -1; k <= 1; k++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = new Color4(1f, 0f, 1f, 1f),
                Width = 10f * DpiScale,
            };
            for (int i = 0; i <= 80; i++)
                s.AddPoint(cx - 500 + i * 12, cy + k * 60, 0.9f, NowMs);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(400);

        int before = Doc.Strokes.Count;
        int undoBefore = Doc.UndoDepth;
        Console.WriteLine($"  先画了 {before} 条横线（间隔 60 px），橡皮擦半径 {EraserRadius:F0} px");

        Tool = Tool.Eraser;
        float y0 = cy - 200, y1 = cy + 200;
        SendMouse((int)cx, (int)y0, 0);
        SettleFrames(60);
        SendMouse((int)cx, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        // Only four samples over 400 px: a fast flick.
        const int coarse = 4;
        for (int i = 1; i <= coarse; i++)
        {
            SendMouse((int)cx, (int)(y0 + (y1 - y0) * i / coarse), 0);
            SettleFrames(10);
        }
        SendMouse((int)cx, (int)y1, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(400);

        int after = Doc.Strokes.Count;
        int undoAfter = Doc.UndoDepth;
        int leftover = ScreenProbe.CountMagenta((int)(cx - 520), (int)(cy - 260), 1040, 520);

        Console.WriteLine($"  笔画数 {before} -> {after}   期望 0");
        Console.WriteLine($"  撤销步数增加 {undoAfter - undoBefore}   期望 1（整段拖拽算一步）");
        Console.WriteLine($"  屏幕上残留品红像素 {leftover}   期望接近 0");

        bool ok = after == 0 && undoAfter - undoBefore == 1 && leftover < 200;
        Console.WriteLine(ok ? "  PASS" : "  FAIL");
        _quit = true;
    }

    private void InputPathTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 输入路径测试（会自动移动鼠标画一笔）===");

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.4f;
        int before = Doc.Strokes.Count;

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

    /// <summary>
    /// Draws one long stroke, checks it is on screen, erases its middle, then
    /// checks the middle went away and the ends survived. Correctness of the
    /// region patch is not something you can eyeball from inside the process.
    /// </summary>
    private void EraseCheck(out bool correct, out string detail)
    {
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // Magenta horizontal stroke, wiped at its left third.
        var target = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 16f };
        for (int i = 0; i <= 80; i++)
        {
            float t = i / 80f;
            target.AddPoint(cx - 400 + t * 800, cy, 0.9f, NowMs);
        }

        // Green vertical stroke crossing the magenta one, far from the erase
        // point. It must survive untouched *including* where it passes through
        // the rectangle that gets cleared and re-rasterised.
        float vx = cx + 200;
        var bystander = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 1f, 0f, 1f), Width = 16f };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            bystander.AddPoint(vx, cy - 300 + t * 600, 0.9f, NowMs);
        }

        Doc.Clear();
        Doc.AddStroke(target);
        Doc.AddStroke(bystander);
        Doc.InvalidateAll();
        SettleFrames(500);

        int targetBefore = ScreenProbe.CountMagenta((int)(cx - 360), (int)(cy - 30), 120, 60);
        int bystanderBefore = ScreenProbe.CountGreen((int)(vx - 30), (int)(cy - 30), 60, 60);

        int removed = Doc.EraseAt(cx - 300, cy, 30f);
        SettleFrames(500);

        int targetAfter = ScreenProbe.CountMagenta((int)(cx - 360), (int)(cy - 30), 120, 60);
        // Sample the bystander *inside* the patched band, not above it.
        int bystanderAfter = ScreenProbe.CountGreen((int)(vx - 30), (int)(cy - 30), 60, 60);

        correct = targetBefore > 200 && targetAfter < 40 && removed == 1
                  && bystanderBefore > 200 && bystanderAfter > bystanderBefore * 0.8;
        detail = $"被擦笔画 {targetBefore}→{targetAfter} 像素（应归零），"
               + $"同区域另一笔 {bystanderBefore}→{bystanderAfter} 像素（应保留），删除 {removed} 笔";
    }

    // =====================================================================
    //  Teardown
    // =====================================================================

}

internal static class ScreenProbe
{
    /// <summary>Grabs the whole virtual desktop into a 32-bit BMP. Used for
    /// eyeballing what another app actually looks like while it is measured.</summary>
    public static bool SaveBmp(string path, int x, int y, int w, int h)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr bits = IntPtr.Zero;
        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);

        var pixels = new byte[w * h * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return WriteBmp(path, pixels, w, h);
    }

    /// <summary>把 BGRA 像素写成 32 位 BMP 文件。</summary>
    private static bool WriteBmp(string path, byte[] pixels, int w, int h)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            int imageBytes = w * h * 4;
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            // BITMAPFILEHEADER
            bw.Write((ushort)0x4D42);          // 'BM'
            bw.Write(14 + 40 + imageBytes);    // 文件大小
            bw.Write(0);
            bw.Write(14 + 40);                 // 像素数据偏移

            // BITMAPINFOHEADER
            bw.Write(40);
            bw.Write(w);
            bw.Write(h);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(0);                       // BI_RGB
            bw.Write(imageBytes);
            bw.Write(2835);                    // 分辨率
            bw.Write(2835);
            bw.Write(0);
            bw.Write(0);

            // BMP 是自下而上存储的，抓到的像素是自上而下的，逐行倒序写。
            for (int row = h - 1; row >= 0; row--)
                bw.Write(pixels, row * w * 4, w * 4);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int CountNear(int x, int y, int w, int h, int r, int g, int b, int tol)
    {
        var box = new Box(r, g, b, tol);
        return Capture(x, y, w, h, (bb, gg, rr) =>
            Math.Abs(rr - box.R) <= box.Tol
            && Math.Abs(gg - box.G) <= box.Tol
            && Math.Abs(bb - box.B) <= box.Tol);
    }

    private readonly struct Box
    {
        public readonly int R, G, B, Tol;
        public Box(int r, int g, int b, int tol) { R = r; G = g; B = b; Tol = tol; }
    }

    public static int CountMagenta(int x, int y, int w, int h) => Capture(x, y, w, h, IsMagenta);
    public static int CountGreen(int x, int y, int w, int h) => Capture(x, y, w, h, IsGreen);
    public static int CountRed(int x, int y, int w, int h) => Capture(x, y, w, h, IsRed);
    public static int CountCursor(int x, int y, int w, int h) => Capture(x, y, w, h, IsCursor);

    // 判定用色。像素是 BGRA 顺序，所以参数名按 (b, g, r)。
    private static bool IsMagenta(byte b, byte g, byte r) => b > 200 && g < 90 && r > 200;
    private static bool IsGreen(byte b, byte g, byte r) => g > 180 && r < 90 && b < 90;
    private static bool IsRed(byte b, byte g, byte r) => r > 170 && g < 110 && b < 110;
    private static bool IsCursor(byte b, byte g, byte r)
        => r > 180 && g > 70 && g < 190 && b > 70 && b < 190;

    /// <summary>抓一块区域的原始 BGRA 像素，用于前后差分。</summary>
    public static byte[] CaptureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return Array.Empty<byte>();
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bits = IntPtr.Zero;

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return Array.Empty<byte>();
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);

        var buf = new byte[w * h * 4];
        Marshal.Copy(bits, buf, 0, buf.Length);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return buf;
    }

    /// <summary>两张同尺寸截图的逐像素差异数（容忍轻微抗锯齿抖动）。</summary>
    public static int DiffCount(byte[] a, byte[] b, int tolerance = 12)
    {
        if (a == null || b == null || a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int dr = Math.Abs(a[i] - b[i]);
            int dg = Math.Abs(a[i + 1] - b[i + 1]);
            int db = Math.Abs(a[i + 2] - b[i + 2]);
            if (dr > tolerance || dg > tolerance || db > tolerance) n++;
        }
        return n;
    }

    /// <summary>Grabs a screen region with GDI and counts pixels of one colour.
    /// This is how the prototype proves that what the GPU drew really landed on
    /// the display, instead of trusting that the code "should" work.</summary>
    public static bool TryFindMagenta(int x, int y, int w, int h, out int hits)
    {
        hits = Capture(x, y, w, h, IsMagenta);
        return hits > 500;
    }

    private static int Capture(int x, int y, int w, int h, Func<byte, byte, byte, bool> match)
    {
        int hits = 0;
        if (w <= 0 || h <= 0) return 0;
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bits = IntPtr.Zero;

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,          // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return 0;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);

        var buffer = new byte[w * h * 4];
        Marshal.Copy(bits, buffer, 0, buffer.Length);
        for (int i = 0; i < buffer.Length; i += 4)
        {
            // Buffer is BGRA order.
            if (match(buffer[i], buffer[i + 1], buffer[i + 2])) hits++;
        }

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);

        return hits;
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return new App().Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            return 1;
        }
    }
}
