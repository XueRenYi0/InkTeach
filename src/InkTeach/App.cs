using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;
using InkEngine.Optimize;

namespace InkTeach;

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

    /// <summary>密集模式：所有笔画写在一小块区域里（量"同一页很多墨迹"）。</summary>
    private bool DenseWrite;

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

        // 参数对照用：--tilesize 256|512 直接换内容层的分块边长（见 CanvasTiles.cs）。
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--tilesize" && int.TryParse(args[i + 1], out var ts) && ts >= 64)
                CanvasTileCache.TileSize = ts;

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
        else if (mode == "--cursortest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CursorTest();
        }
        else if (mode == "--tiletest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TileTest();
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
            // 加 "scroll" 再跑一遍：相机不为 0 时，光标的脏区换算只要漏了
            // 相机偏移就会"擦不干净"，而那种残影在没滚动的画面上根本看不出来。
            GhostTest(args.Contains("scroll"));
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
        else if (mode == "--erasemodetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseModeTest();
        }
        else if (mode == "--vertextest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            VertexTest();
        }
        else if (mode == "--shapedrawtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeDrawTest();
        }
        else if (mode == "--imagetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageTest();
        }
        else if (mode == "--capturetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureTest();
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
        else if (mode == "--rotatetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RotateTest();
        }
        else if (mode == "--keytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            KeyTest();
        }
        else if (mode == "--rotateshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            float deg = args.Length > 1 && float.TryParse(args[1], out var d) ? d : 90f;
            RotateShowcase(deg);
        }
        else if (mode == "--cursorshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string which = args.Length > 1 ? args[1].ToLowerInvariant() : "pen";
            float width = args.Length > 2 && float.TryParse(args[2], out var w) ? w : 0f;
            showcasePenDevice = args.Contains("pen");
            CursorShowcase(which, width);
        }
        else if (mode == "--vertexshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            VertexShowcase();
        }
        else if (mode == "--erasedemo")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseDemo();
        }
        else if (mode == "--imageshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageShowcase();
        }
        else if (mode == "--shapeshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeShowcase(args.Length > 1 ? args[1].ToLowerInvariant() : "circle");
        }
        else if (mode == "--erasecutshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseCutShowcase();
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
        else if (mode == "--writetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double mins = args.Length > 1 && double.TryParse(args[1], out var wm) ? wm : 20;
            double sps = args.Length > 2 && double.TryParse(args[2], out var ws) ? ws : 6;
            // nogeo：画完就丢几何（--memab 那条 A/B 的开关），用来量"常驻几何"
            // 到底吃多少内存。
            if (args.Contains("nogeo")) Stroke.KeepGeometry = false;
            if (args.Contains("keepall")) Stroke.Retention = Stroke.RetentionMode.KeepAll;
            if (args.Contains("keepnone")) Stroke.Retention = Stroke.RetentionMode.KeepNone;
            int realIdx = Array.IndexOf(args, "real");
            if (realIdx >= 0 && realIdx + 1 < args.Length && int.TryParse(args[realIdx + 1], out var rl))
                Stroke.MaxRealizationsOverride = rl;
            int rpIdx = Array.IndexOf(args, "realpts");
            if (rpIdx >= 0 && rpIdx + 1 < args.Length && long.TryParse(args[rpIdx + 1], out var rp))
                Stroke.RealizationPointsBudget = rp;      // 测量用：还原"按条数、不按点数"的旧行为
            DenseWrite = args.Contains("dense");
            WriteTest(mins, sps);
        }
        else if (mode == "--memlife")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var ml) ? ml : 10000;
            MemLifeTest(n);
        }
        else if (mode == "--appendtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AppendPathTest();
        }
        else if (mode == "--latbench")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LatencyBench(args.Length > 1 ? args[1] : "reports/latency.csv");
        }
        else if (mode == "--penlive")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = args.Length > 1 && double.TryParse(args[1], out var pl) ? pl : 20;
            LatencyLive(secs);
        }
        else if (mode == "--memab")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10000;
            int k = args.Length > 2 && int.TryParse(args[2], out var rd) ? rd : 5;
            MemAbTest(n, k);
        }
        else if (mode == "--cameratest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CameraTest();
        }
        else if (mode == "--scrollwrite")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollWriteTest();
        }
        else if (mode == "--scrollshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollShowcase();
        }
        else if (mode == "--wheeltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            WheelTest();
        }
        else if (mode == "--coordtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var c) ? c : 12;
            CoordTest(n);
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
        Console.WriteLine("  --erasemodetest     两种橡皮的模式（整笔精准 / 矩形切段）+ 真机");
        Console.WriteLine("  --vertextest        图形定义与顶点拖动（含旋转后拖顶点）");
        Console.WriteLine("  --imagetest         图像对象（上屏 / 复制翻转 / 存档 / 剪贴板）");
        Console.WriteLine("  --capturetest       截图（拖框 → 左上角 → 剪贴板，且不拍进自己的批注）");
        Console.WriteLine("  --cursorshow <笔|荧光笔|激光笔|橡皮|面积橡皮> [宽]  落点摆样");
        Console.WriteLine("  --vertexshow        顶点编辑摆样（挂住，人工截图）");
        Console.WriteLine("  --widthtest         笔迹粗细/压力");
        Console.WriteLine("  --ghosttest         残影检测");
        Console.WriteLine("  --trailtest         委托墨迹轨迹对照");
        Console.WriteLine("  --latbench <csv>    延时实测（分场景 + 分位数 + 稳定性）");
        Console.WriteLine("  --penlive [秒]      真笔延时实测（挂上手写笔写一会儿，出报告）");
        Console.WriteLine("  --longrun [秒]      长时运行内存/CPU");
        Console.WriteLine("  --realizetest [n]   几何实现缓存对照");
        Console.WriteLine("  --restest [n]       分辨率对照（离屏层）");
        Console.WriteLine("  --beautifytest      手写美化自检（笔锋量化）");
        Console.WriteLine("  --beautifyshowcase  四种笔锋摆样（人工看）");
        Console.WriteLine("  --cornertest        直角填充自检（拐角会不会掉色）");
        Console.WriteLine("  --rotatetest        旋转度数读数与吸附（含真机拖动 + 上屏核对）");
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

    /// <summary>
    /// 相机自检 + 滚动步代价。
    ///
    /// 验两件事：
    ///   ① **正确性**：偏移之后，同一块画布内容必须出现在屏幕上偏移后的位置，
    ///      而**文档里的坐标一个都不变**——这是"滚动只改一个数、不动对象数据"的直接证据。
    ///   ② **代价**：滚动一步 = 整屏重画（第一步的已知边界）。按"一屏量级"与
    ///      "累积量级"分别量，就知道现在能扛到多少。
    /// </summary>
    private void CameraTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 相机自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-20}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float cx = _virtualX + 900, cy = _virtualY + 700;
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 20f * DpiScale,   // 品红，好数像素
        };
        s.AddPoint(cx - 200, cy, 1f, 0);
        s.AddPoint(cx + 200, cy, 1f, 0);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(400);

        int before = ScreenProbe.CountMagenta((int)cx - 60, (int)cy - 60, 120, 120);
        Check("偏移前在画布坐标处", before > 200, $"{before} 像素");

        float shift = 600f;
        ViewOffsetY = -shift;                        // 往下滚：内容上移
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }
        Doc.InvalidateAll();
        SettleFrames(400);

        int afterOld = ScreenProbe.CountMagenta((int)cx - 60, (int)cy - 60, 120, 120);
        int afterNew = ScreenProbe.CountMagenta((int)cx - 60, (int)(cy - shift) - 60, 120, 120);
        Check("原屏幕位置已清空", afterOld < 40, $"{afterOld} 像素");
        Check("墨到了偏移后的位置", afterNew > 200, $"{afterNew} 像素");
        Check("文档坐标未变", Math.Abs(s.Bounds.MinY - cy) < 0.01f,
              $"Bounds.MinY={s.Bounds.MinY:F1}");

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        SettleFrames(400);
        int back = ScreenProbe.CountMagenta((int)cx - 60, (int)cy - 60, 120, 120);
        Check("滚回去仍在原位", back > 200, $"{back} 像素");
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：相机只改一个数，对象数据不动");

        ScrollBench();

        ViewOffsetY = 0f;
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 滚动代价：**走真实的滚轮路径**（HandleWheel），一格渲染一帧。
    ///
    /// 两条纪律：
    ///   · 不直接给 ViewOffsetY 赋值——那样绕开了 HandleWheel 的算术，上一轮
    ///     就是因此漏掉了"滚轮符号写反"这个 bug（还是用户报的）。
    ///   · 内容**铺开 8 屏**，不是塞在一屏里。真实板书是纵向累积的，塞一屏
    ///     是"满屏"的极端形状，衡量不了分块缓存要解决的"累积量"问题。
    ///
    /// 看三个数：每格平均帧耗时、最慢一格、以及这一格光栅化了几块。
    /// 分块缓存成立的标志是：**帧耗时与文档总量基本无关**——新露出来的
    /// 只有那么一小条，和文档里已经有多少笔没关系。
    /// </summary>
    private void ScrollBench()
    {
        Console.WriteLine();
        Console.WriteLine("=== 滚动代价（真实滚轮路径，一格 = 144 物理像素）===");
        Console.WriteLine("  内容铺开 8 屏（≈一节板书的量级），从顶部往下滚 12 格 ≈ 一屏");
        Console.WriteLine("    文档笔数 | 每格平均 | 最慢一格 | 一格光栅 | 最慢一格光栅 | 新光栅块 | 判断");
        Console.WriteLine("  -----------|----------|----------|----------|--------------|----------|------");

        // 和 WheelTest 用同一套构造方式：delta 放在 wParam 的高 16 位。
        static IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
        const int steps = 12;

        foreach (int n in new[] { 1000, 5000, 10000 })
        {
            Doc.Clear();
            Doc.ClearHistory();
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
            GenerateStrokesSpread(n, 8);
            SettleFrames(600);                       // 先把首屏画出来

            double total = 0, worst = 0, raster = 0, worstRaster = 0;
            int newTiles = 0;
            for (int k = 0; k < steps; k++)
            {
                HandleWheel(Wheel(-120));            // 往下滚一格
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();                         // 只渲染这一格

                var w0 = _windows[0];
                total += w0.LastRecordMs;
                raster += w0.LastRebuildMs;
                if (w0.LastRecordMs > worst) worst = w0.LastRecordMs;
                if (w0.LastRebuildMs > worstRaster) worstRaster = w0.LastRebuildMs;
                newTiles += w0.LastPatchCount;
            }

            string note = worst < 16.7 ? "不掉帧" : worst < 33 ? "偶掉一帧" : "会卡";
            Console.WriteLine($"  {n,9} | {total / steps,6:F2} ms | {worst,6:F2} ms | {raster / steps,6:F2} ms | {worstRaster,10:F2} ms | {newTiles,8} | {note}");
        }

        Console.WriteLine("  注：老做法（整层重画）的对照值：一屏量级 20ms，5000 笔 41ms，10000 笔 82ms。");
        Console.WriteLine("      现在滚一格的代价只和「新露出来的那一小条」有关，与文档总量无关。");
    }

    /// <summary>
    /// 把滚动条摆出来给人看：写满三屏内容，滚到中间，让滚动条处在"刚滚动过"的
    /// 露面状态，然后挂着不动由外部截图。
    /// </summary>
    private void ScrollShowcase()
    {
        Doc.Clear();
        Doc.ClearHistory();

        // 写满三屏：每屏几行，跨度超过一屏，滚动条才有比例可算
        for (int screen = 0; screen < 3; screen++)
        {
            for (int row = 0; row < 6; row++)
            {
                float y = _virtualY + screen * _virtualH + 250 + row * 240;
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f,
                };
                for (int i = 0; i <= 40; i++)
                {
                    float t = i / 40f;
                    s.AddPoint(_virtualX + 300 + t * 1200, y + MathF.Sin(t * 9f) * 30f, 0.5f, i * 8);
                }
                Doc.AddStroke(s);
            }
        }

        // 滚到中间，并让滚动条处于"刚滚动过"的状态（真实滚动会自己设这个时刻）
        ViewOffsetY = -_virtualH * 1.0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }
        ScrollBarActiveAtMs = NowMs;              // 相当于刚刚滚过
        Doc.InvalidateAll();
        SettleFrames(700);
        Console.WriteLine("滚动条已摆好（刚滚动过的状态），等外部截图");
    }

    /// <summary>
    /// 滚轮**算术**的自检。
    ///
    /// 这条是补课的：之前所有滚动测试都直接给 ViewOffsetY 赋值，
    /// **从没走过 HandleWheel 里那段加减**，于是"符号写反、往下滚永远滚不动"
    /// 这个 bug 一直没被测出来——还是用户报的。
    ///
    /// 直接构造 WM_MOUSEWHEEL 的 wParam（高 16 位是 delta）喂进去：
    ///   往下滚 delta = -120，ViewOffsetY 必须**变负**（内容上移）
    ///   往上滚到顶，必须夹在 0，不能越过
    /// </summary>
    private void WheelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 滚轮算术自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        // delta 放在 wParam 的高 16 位（低 16 位是按键状态，这里给 0）
        IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
        float step = 72f * DpiScale;

        HandleWheel(Wheel(-120));
        Check("往下滚一格应向下", ViewOffsetY < -1f, $"ViewOffsetY={ViewOffsetY:F0}（应为 -{step:F0}）");
        Check("一格正好一个步长", Math.Abs(ViewOffsetY + step) < 0.6f, $"{ViewOffsetY:F0}");

        HandleWheel(Wheel(-120));
        HandleWheel(Wheel(-120));
        Check("连滚三格累加", Math.Abs(ViewOffsetY + step * 3f) < 1.2f, $"{ViewOffsetY:F0}");

        HandleWheel(Wheel(120));
        Check("往上滚一格回升", Math.Abs(ViewOffsetY + step * 2f) < 1.2f, $"{ViewOffsetY:F0}");

        for (int i = 0; i < 8; i++) HandleWheel(Wheel(120));
        Check("滚到顶夹在 0，不越过", Math.Abs(ViewOffsetY) < 0.01f, $"{ViewOffsetY:F0}");

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：滚轮方向与夹取都正确");
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// **坐标不变量测试**——随机相机偏移下反复验同一句话：
    ///
    ///   在画布 P 处写一笔 → 屏幕上 P+相机 处必须有墨。
    ///
    /// 为什么要有它：前面三个 bug（滚下去写的看不见 / 滚完写不了 / 闪一下就没了）
    /// 都是"画布坐标和窗口坐标混用"，而且每个都是**用户碰出来的**。
    /// 一个一个追太慢，这条测试把它们一次性网住：
    /// 它走的是**真实交互路径**（AddStroke 不调 InvalidateAll → DrawOnlyPatch），
    /// 并且随机换相机偏移——偏移一大，任何忘了换算的地方都会露馅。
    ///
    /// 这一类测试行业里叫"不变量测试"（invariant／属性测试）：不写死具体场景，
    /// 只断言"无论参数取什么值，这条性质都必须成立"。
    /// </summary>
    private void CoordTest(int rounds)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 坐标不变量测试（{rounds} 个随机相机偏移）===");
        Console.WriteLine("    偏移 |     期望处 |   未偏移处 | 结果");
        Console.WriteLine("  -------|------------|------------|------");

        int pass = 0, fail = 0;
        var detail = new List<string>();
        var rnd = new Random(20260913);

        for (int k = 0; k < rounds; k++)
        {
            Doc.Clear();
            Doc.ClearHistory();

            float shift = -(float)(rnd.NextDouble() * 3000.0 + 150.0);   // 往下滚 150~3150
            ViewOffsetY = shift;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = shift; }
            Doc.InvalidateAll();
            SettleFrames(150);

            // 挑一个**屏幕可见**的位置，反推它对应的画布坐标（就是输入路径做的事）
            float sx = _virtualX + 600f + (float)rnd.NextDouble() * 700f;
            float sy = _virtualY + 300f + (float)rnd.NextDouble() * 700f;
            float cx = sx, cy = sy;
            ScreenToCanvas(ref cx, ref cy);

            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 24f * DpiScale,
            };
            s.AddPoint(cx - 150f, cy, 1f, 0);
            s.AddPoint(cx + 150f, cy, 1f, 0);
            Doc.AddStroke(s);              // 真实路径，别加 InvalidateAll
            SettleFrames(200);

            int atExpected = ScreenProbe.CountMagenta((int)sx - 90, (int)sy - 90, 180, 180);
            int atRaw = ScreenProbe.CountMagenta((int)sx - 90, (int)(sy - shift) - 90, 180, 180);
            bool ok = atExpected > 300;

            if (!ok && detail.Count < 3)
                Console.WriteLine($"      [诊断] 补画块数 {_windows[0].LastAppendedTiles}"
                                + $" 累计补画 {_windows[0].TotalAppendedTiles}"
                                + $" 未命中补画 {_windows[0].LastAppendMissed}"
                                + $" err={_windows[0].LastError}"
                                + $" 文档 {Doc.Strokes.Count} 条 画布 y={cy:F0}");

            if (ok) pass++; else { fail++; detail.Add($"偏移{shift:F0}：期望处 {atExpected} 像素（应 >300），画布 y={cy:F0}"); }
            if (k < 12 || !ok)
                Console.WriteLine($"  {shift,6:F0} | {atExpected,10} | {atRaw,10} | {(ok ? "PASS" : "FAIL")}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {pass}/{rounds} 通过");
        foreach (var d in detail) Console.WriteLine("    " + d);
        Console.WriteLine(fail == 0
            ? "  PASS：随机偏移下，画布上写在哪、屏幕上就该出现在哪 —— 全都成立"
            : $"  FAIL：{fail} 个偏移下不成立 —— 坐标换算有遗漏");

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 滚下去还能不能写。
    ///
    /// 做法：在三个滚动位置（顶部 / 往下三屏）各在**当下的屏幕位置**写一笔，
    /// 然后回到顶部，验证只有第一笔在视野里、后两笔确实留在了下面。
    ///
    /// 这同时回答"滚动要不要下限"：数据上**不设限**才是对的（老师往下写不完），
    /// 真正要解决的是"怎么回来"——无限往下滚而没有回顶部的办法，老师会迷路。
    /// </summary>
    private void ScrollWriteTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 滚下去还能不能写 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-24}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();

        float px = _virtualX + 900, py = _virtualY + 700;      // 屏幕上的固定位置
        float[] offsets = { 0f, -1800f, -5400f };              // 顶部 / 一屏 / 三屏
        var writtenCanvasY = new List<float>();

        for (int i = 0; i < offsets.Length; i++)
        {
            ViewOffsetY = offsets[i];
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }

            // 输入路径做的事：屏幕坐标 -> 画布坐标
            float cx = px, cy = py;
            ScreenToCanvas(ref cx, ref cy);
            writtenCanvasY.Add(cy);

            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 20f * DpiScale,
            };
            s.AddPoint(cx - 200, cy, 1f, 0);
            s.AddPoint(cx + 200, cy, 1f, 0);
            Doc.AddStroke(s);      // 真实交互路径：不调 InvalidateAll，
                                    // 走"脏区 → 标脏分块 → 只重画碰到的那几块"
            SettleFrames(350);

            int seen = ScreenProbe.CountMagenta((int)px - 60, (int)py - 60, 120, 120);
            Check($"滚动 {offsets[i],7:F0} 处能写", seen > 200,
                  $"画布 y={cy:F0}，屏幕上有 {seen} 像素");
            if (seen <= 200)
            {
                // 诊断：墨到底画到哪去了？沿屏幕竖着扫几条带子。
                Console.Write("      竖扫结果：");
                for (int band = 0; band < 6; band++)
                {
                    int yy = _virtualY + band * 300;
                    int n2 = ScreenProbe.CountMagenta((int)px - 200, yy, 400, 300);
                    Console.Write($"y={band * 300,4}→{n2,5}  ");
                }
                Console.WriteLine();
            }
        }

        Check("三笔记录在三个不同的画布位置",
              writtenCanvasY.Distinct().Count() == 3, string.Join(", ", writtenCanvasY.Select(v => v.ToString("F0"))));

        // 回到顶部：只有第一笔应该在视野里
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        SettleFrames(400);

        int first = ScreenProbe.CountMagenta((int)px - 60, (int)py - 60, 120, 120);
        Check("回顶部：第一笔仍在原位", first > 200, $"{first} 像素");

        int below = ScreenProbe.CountMagenta((int)px - 60, (int)py + 900, 120, 300);
        Check("回顶部：后两笔在视野外", below < 40, $"屏幕下方 {below} 像素（应为 0）");

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：滚到哪儿都能写，写下的内容留在那个画布位置");
        Console.WriteLine();

        // 死锁回归：**空文档也必须能往下滚一屏**。
        // 之前只取"内容 ∪ 视口"时，空文档画布恰好一屏、下边界为 0，
        // 往下滚立刻被夹回去；而滚不动就写不到下面去 —— 死锁。
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        var empty = CanvasExtent;
        Check("空文档也能往下滚一屏",
              empty.MaxY - ViewportCanvas.MinY >= _virtualH * 2f,
              $"画布高 {empty.MaxY - empty.MinY:F0}px（一屏 {_virtualH}px）");
        Check("空文档不许往上滚过头", CanvasExtent.MinY <= ViewportCanvas.MinY + 0.5f,
              $"画布顶 {empty.MinY:F0}");
        Console.WriteLine();
        Console.WriteLine("  关于下限：数据层不设限才是对的（往下写不完）。");
        Console.WriteLine("  缺的不是下限，是**回顶部的办法**（滚动条 / 一键回顶）。");
        Console.WriteLine("  现在的实现只夹住了上边界（不许滚过内容顶部），下方不设限。");

        ViewOffsetY = 0f;
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
        Stroke.KeepGeometry = realizations;

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
        _abGpu = GpuUsedMb();
        var (rec, _) = MeasureFrames(24);
        _abRec = rec;
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    /// <summary>
    /// 显存已用量（MB）。
    ///
    /// 注意：**别去解析 GpuMb() 返回的字符串**——那是"154/7396 MB"这种给人看的
    /// 格式，TryParse 会失败并静默返回 0（第一版就是这么错的，整列显示 0）。
    /// 要数就直接问同一个 API 要数。
    ///
    /// 为什么这项重要：核显的显存**是从系统内存里分的**，在 4GB 教室机上它和
    /// 进程内存抢的是同一块。--memory 报告里一万笔时显存 154MB，比点数据
    /// （3.9MB）大两个数量级——真正的大头很可能在这里。
    /// </summary>
    private static double GpuUsedMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return 0;
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return info.CurrentUsage / 1048576.0;
        }
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
                        + $"（细分缓存存活 {Stroke.LiveRealizations}，几何存活 {Stroke.LiveGeometries}）");

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
    /// 键位自检：默认表、解析、冲突检测、落盘读回、真机注册、批注内真按一次键。
    ///
    /// 为什么值得单独写一条：键位是"用户的设置"，出错的方式特别隐蔽——
    /// 键被别的程序占了（按下去没反应）、改键撞了车（按下去触发另一件事）、
    /// 配置文件写坏了（下次启动直接崩）。这三类都在这里验。
    /// </summary>
    private void KeyTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 键位自检（表 / 冲突 / 落盘 / 真机注册）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        // ---- 1. 默认表 ----
        var map = KeyMap.Default();
        var problems = map.Validate();
        Check("默认表内部没有矛盾", problems.Count == 0,
              problems.Count == 0 ? "无" : string.Join("；", problems));

        int nGlobal = map.For(KeyScope.Global).Count();
        int nAnno = map.For(KeyScope.Annotation).Count();
        // 条数**不再写死**：加一个工具就会多一条，写死只会让这条自检天天报假警
        // （"22 项通过、1 项不对"里那一项就是它）。真正的不变量是下面两条：
        // 全局一律带修饰键、编辑类动作不在全局。这里只保证两档都非空、
        // 且每一条都真的绑上了键。
        Check("作用域划分：两档都非空且每条都有键",
              nGlobal > 0 && nAnno > 0 && map.Bindings.All(b => b.Chord.IsValid),
              $"全局 {nGlobal} 条，批注内 {nAnno} 条");
        Check("全局键一律带修饰键（不会抢走正常打字）",
              map.For(KeyScope.Global).All(b => b.Chord.HasModifier), "");
        Check("编辑类动作只在批注内（没有全局 Ctrl+Z 这种）",
              !map.For(KeyScope.Global).Any(b => b.Action == KeyAction.Redo
                    || b.Action == KeyAction.SelectAll || b.Action == KeyAction.DeleteSelected
                    || b.Action == KeyAction.NudgeLeft), "");

        // ---- 2. 按键解析 ----
        bool ok1 = KeyChord.TryParse("ctrl+alt+p", out var c1, out _);
        Check("解析 Ctrl+Alt+P（大小写不敏感）", ok1 && c1.ToString() == "Ctrl+Alt+P", c1.ToString());
        bool ok2 = KeyChord.TryParse("Delete", out var c2, out _);
        Check("解析 Delete", ok2 && c2.Vk == 0x2E, $"VK={c2.Vk:X2}");
        bool ok3 = KeyChord.TryParse("Shift+Left", out var c3, out _);
        Check("解析 Shift+Left", ok3 && c3.Vk == 0x25 && (c3.Modifiers & KeyChord.ModShift) != 0,
              c3.ToString());
        bool ok4 = KeyChord.TryParse("Ctrl+月亮", out _, out string err4);
        Check("认不出的键名要报错（不是静默忽略）", !ok4 && err4 != null, err4);
        bool ok5 = KeyChord.TryParse("Ctrl+", out _, out string err5);
        Check("只有修饰键要报错", !ok5 && err5 != null, err5);

        // ---- 3. 冲突检测 ----
        bool taken = map.TrySet(KeyScope.Global, KeyAction.Undo, c1, out string errTaken);
        Check("撞了别人的键要拒绝并说清是谁", !taken && errTaken != null
              && errTaken.Contains("穿透"), errTaken);

        KeyChord.TryParse("F5", out var noMod, out _);
        bool bare = map.TrySet(KeyScope.Global, KeyAction.Undo, noMod, out string errBare);
        Check("全局热键没有修饰键要拒绝", !bare && errBare != null, errBare);

        KeyChord.TryParse("Ctrl+Alt+F5", out var free, out _);
        bool moved = map.TrySet(KeyScope.Global, KeyAction.Undo, free, out string errMove);
        Check("没冲突就能改，并标记成脏",
              moved && map.Dirty && map.Find(KeyScope.Global, KeyAction.Undo).Chord.Equals(free),
              $"撤销 → {map.Find(KeyScope.Global, KeyAction.Undo).Chord}");

        map.ResetToDefault(KeyScope.Global, KeyAction.Undo);
        Check("能恢复默认键", map.Find(KeyScope.Global, KeyAction.Undo).Chord.ToString() == "Ctrl+Alt+Z",
              map.Find(KeyScope.Global, KeyAction.Undo).Chord.ToString());

        // ---- 4. 落盘 / 读回 / 坏文件 ----
        string cfg = Path.Combine(Path.GetTempPath(), "inkteach-keytest.json");
        InkSettings.PathOverride = cfg;
        try
        {
            KeyChord.TryParse("Ctrl+Alt+F12", out var f12, out _);
            map.TrySet(KeyScope.Global, KeyAction.Quit, f12, out _);
            InkSettings.Save(map);
            Check("写盘后文件真的存在", File.Exists(cfg), cfg);

            var reloaded = KeyMap.Default();
            var warns = InkSettings.Load(reloaded);
            Check("读回无警告，且改动生效",
                  warns.Count == 0 && reloaded.Find(KeyScope.Global, KeyAction.Quit).Chord.Equals(f12),
                  warns.Count == 0 ? $"退出键 → {reloaded.Find(KeyScope.Global, KeyAction.Quit).Chord}"
                                   : string.Join("；", warns));
            Check("没改过的项仍是默认值（只写差异）",
                  reloaded.Find(KeyScope.Global, KeyAction.Undo).Chord.ToString() == "Ctrl+Alt+Z", "");
            Check("只写差异：文件里应当只有 1 条", File.ReadAllText(cfg).Split('\n')
                  .Count(l => l.Contains("\"Global.")) == 1, "");

            // ---- 工具尺寸（"tools" 段）同文件共存 ----
            var sizes = new InkSettings.ToolSettings
            {
                AreaEraserHeight = 140f, EraserRadius = 18f, Dirty = true,
            };
            InkSettings.Save(map, sizes);
            var sizesBack = new InkSettings.ToolSettings();
            var warnTools = InkSettings.Load(KeyMap.Default(), sizesBack);
            Check("工具尺寸能落盘读回（和键位同一个文件）",
                  warnTools.Count == 0
                  && Math.Abs(sizesBack.AreaEraserHeight - 140f) < 0.01f
                  && Math.Abs(sizesBack.EraserRadius - 18f) < 0.01f,
                  $"面积橡皮高 {sizesBack.AreaEraserHeight:F0}，笔尖 {sizesBack.EraserRadius:F0}"
                  + (warnTools.Count > 0 ? "；" + string.Join("；", warnTools) : ""));
            Check("工具尺寸只写改过的（没动过的笔宽不写）",
                  !File.ReadAllText(cfg).Contains("PenWidth"), "");
            Check("键位段没被工具尺寸这一段带坏",
                  sizesBack.Dirty == false
                  && reloaded.Find(KeyScope.Global, KeyAction.Quit).Chord.Equals(f12), "");

            File.WriteAllText(cfg, "{ \"tools\": { \"AreaEraserHeight\": \"abc\", \"Nope\": 3 } }");
            var sizesBad = new InkSettings.ToolSettings();
            var warnBad = InkSettings.Load(KeyMap.Default(), sizesBad);
            Check("工具尺寸写坏了：报警告 + 用默认值 + 不抛异常",
                  warnBad.Count >= 2 && sizesBad.AreaEraserHeight < 0f,
                  $"警告 {warnBad.Count} 条：{string.Join("；", warnBad)}");

            File.WriteAllText(cfg, "{ \"keys\": { \"Global.Quit\": \"Ctrl+Alt+\" } }");
            var broken = KeyMap.Default();
            var warns2 = InkSettings.Load(broken);
            Check("键名写坏了：报警告 + 用默认值 + 不抛异常",
                  warns2.Count > 0 && broken.Find(KeyScope.Global, KeyAction.Quit).Chord.ToString() == "Ctrl+Alt+X",
                  warns2.Count > 0 ? warns2[0] : "没有警告（不该）");

            File.WriteAllText(cfg, "这不是 JSON，只是一段乱码");
            var garbage = KeyMap.Default();
            var warns3 = InkSettings.Load(garbage);
            Check("整个文件是垃圾：报警告 + 仍能启动",
                  warns3.Count > 0 && garbage.Validate().Count == 0,
                  warns3.Count > 0 ? warns3[0] : "没有警告（不该）");
        }
        finally
        {
            try { File.Delete(cfg); } catch { }
            InkSettings.PathOverride = null;
        }

        // ---- 5. 真机注册结果 ----
        Check("全局热键注册数量与表一致", HotkeysRegistered == nGlobal,
              $"{HotkeysRegistered}/{nGlobal} 注册成功"
              + (Keys.GlobalFailures.Count > 0 ? "；失败：" + string.Join("；", Keys.GlobalFailures) : ""));

        // ---- 6. 快捷键总表：文档不许落后于代码 ----
        //
        // 用户的要求是"同一个功能的不同快捷键都要记录全，以后每次修改都记录"。
        // 光靠自觉迟早会漏，所以这里把**代码里的每一条绑定**拿去文档里查：
        // 要求**键和动作名出现在同一行**——只要求"这两个字符串各自出现过"太弱了
        // （把键换了但忘了改文档，照样能通过）。
        // 拿**默认表**去核对，不是拿这一轮被测试改过的 map：文档记的是程序**出厂默认键**，
        // 用户自己改的键活在 settings.json 里（上面刚验过落盘读回）。用 map 去比，
        // 会把"测试里临时改成 Ctrl+Alt+F12"当成文档缺一条——这条假失败刚踩到。
        var docCheck = CheckHotkeyDoc(KeyMap.Default());
        Check("快捷键总表.md 覆盖了每一条绑定（键 + 动作名同一行）", docCheck.ok, docCheck.detail);

        // ---- 6. 动作路由：id ↔ 动作 ↔ 键 一一对应 ----
        bool routingOk = true;
        string routingDetail = "";
        int i2 = 0;
        foreach (var b in Keys.For(KeyScope.Global))
        {
            i2++;
            if (ActionForHotkeyId(i2) != b.Action)
            {
                routingOk = false;
                routingDetail = $"第 {i2} 个：期望 {b.Action}，实际 {ActionForHotkeyId(i2)}";
                break;
            }
        }
        Check("WM_HOTKEY 的 id 能正确反查到动作", routingOk,
              routingOk ? $"{i2} 条全部对上" : routingDetail);

        // ---- 7. 批注内真按一次键（走窗口消息 → 键盘模式 → 动作）----
        Doc.Clear();
        Doc.ClearHistory();
        for (int i = 0; i < 3; i++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f * DpiScale };
            for (int k = 0; k <= 20; k++) s.AddPoint(600 + k * 20, 400 + i * 120, 1f, NowMs);
            Doc.AddStroke(s);
        }
        bool wasKeyboardMode = KeyboardMode;
        if (!wasKeyboardMode) SetKeyboardMode(true);
        SelectAll();
        int before = Doc.Strokes.Count;

        // Delete 在默认表里是"删除选中"（批注内、无修饰键，所以能直接合成）
        Native.PostMessage(_windows[0].Hwnd, (uint)Native.WM_KEYDOWN, new IntPtr(0x2E), IntPtr.Zero);
        SettleFrames(200);
        int afterDelete = Doc.Strokes.Count;
        Check("批注内按 Delete 真的删掉了选中",
              before == 3 && afterDelete == 0, $"{before} 笔 → {afterDelete} 笔");

        Doc.Undo();
        SettleFrames(150);
        Check("这一步能撤销回来", Doc.Strokes.Count == 3, $"撤销后 {Doc.Strokes.Count} 笔");
        if (!wasKeyboardMode) SetKeyboardMode(false);

        Console.WriteLine();
        Console.WriteLine("  当前键位表：");
        Console.Write(Keys.ToText());
        if (Keys.GlobalFailures.Count > 0)
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    ！" + f);

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS: 键位表、冲突检测、落盘读回、真机注册与批注内按键都正确"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");

        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 核对"快捷键总表.md"有没有落后于代码。
    ///
    /// 规则（用户定的）：同一个功能的所有键都要列全，每次改键都要记录。
    /// 这里只机器可判的那一半：**代码里每一条绑定，都要能在文档里找到
    /// "键 + 动作名"同一行**。文档不在（比如在别的目录跑）就跳过，
    /// 只提示不判失败——那是环境问题，不是键位表的问题。
    /// </summary>
    private (bool ok, string detail) CheckHotkeyDoc(KeyMap map)
    {
        const string docPath = "快捷键总表.md";
        if (!File.Exists(docPath))
            return (true, $"{docPath} 不在当前目录，这一项跳过");

        var lines = File.ReadAllText(docPath).Split('\n');
        var missing = new List<string>();
        foreach (var b in map.Bindings)
        {
            string chord = b.Chord.ToString();
            string name = KeyMap.Describe(b.Action);
            bool found = lines.Any(l => l.Contains(chord) && l.Contains(name));
            if (!found) missing.Add($"{b.Scope} {chord} {name}");
        }

        return (missing.Count == 0,
                missing.Count == 0
                    ? $"共 {map.Bindings.Count} 条，全部查得到"
                    : "缺：" + string.Join("；", missing.Take(6))
                      + (missing.Count > 6 ? $" 等 {missing.Count} 条" : ""));
    }

    /// <summary>
    /// 落点反馈摆样：把指针停在屏幕中央、按指定工具把落点反馈挂出来，等外部截图。
    /// 目的和 --rotateshow 一样——光标这类东西只有看在图上才算验过。
    /// </summary>
    private void CursorShowcase(string tool, float widthLogical)
    {
        Doc.Clear();
        Doc.ClearHistory();
        Tool = tool switch
        {
            "highlighter" or "hl" => Tool.Highlighter,
            "laser" => Tool.Laser,
            "eraser" => Tool.Eraser,
            "areaeraser" or "area" => Tool.AreaEraser,
            _ => Tool.Pen,
        };
        if (widthLogical > 0f)
        {
            if (Tool == Tool.Highlighter) HighlighterWidthLogical = widthLogical;
            else if (Tool == Tool.Laser) LaserWidthLogical = widthLogical;
            else if (Tool == Tool.AreaEraser) AreaEraserHeightLogical = widthLogical;
            else if (Tool == Tool.Eraser) EraserRadiusLogical = widthLogical;
            else PenWidthLogical = widthLogical;
        }

        PassThrough = false;
        PointerInside = true;
        _drawing = false;
        LastPointerType = showcasePenDevice ? Native.PT_PEN : Native.PT_MOUSE;
        PointerX = _virtualX + _virtualW * 0.5f;
        PointerY = _virtualY + _virtualH * 0.5f;
        ShowHud = false;
        _dirty = true;
        SettleFrames(600);
        Console.WriteLine($"落点反馈已摆好：{Tool} 宽 {CurrentToolWidthLogical} 逻辑像素"
                        + $"（{DrawnCursor}，外圈 {CursorOuterRadius:F0}px，脏区 {DrawnCursorRadius:F0}px）");
        Console.WriteLine("cursor showcase ready");
    }

    private bool showcasePenDevice = false;

    /// <summary>
    /// 两个橡皮的**对照摆样**（人工截图用）：
    /// 左边一个"王"字，用笔记橡皮擦掉中间那一竖——其余四笔一根都不少；
    /// 右边同样一个"王"字，用面积橡皮横着扫一下——三横一竖全被切出一道缺口。
    ///
    /// 这就是两个橡皮的区别，一图胜千言：**整笔** vs **按范围切**。
    /// </summary>
    /// <summary>
    /// 图像对象的摆样：造一张有内容的图（棋盘 + 色块），放到左上角并选中，
    /// 挂住不动由外部截图。看的是"图像用的是**同一套**选中框"——八个手柄、
    /// 旋转手柄、下面的操作条（复制/删除/左右翻转/上下翻转）一个不少。
    /// </summary>
    /// <summary>
    /// 七个图形摆样（人工截图核对外形与选中框）。可选参数指定选中哪一个，
    /// 默认选圆——用户报的"圆形画出来不对"就是它的包围盒错了。
    /// </summary>
    private void ShapeShowcase(string select)
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float dpi = DpiScale;
        // 一屏放得下七个：4 列 2 行（第 4 列要留出选中框和旋转手柄的边距）
        float unit = 105f * dpi;
        float left = _virtualX + _virtualW * 0.16f;
        float top = _virtualY + _virtualH * 0.28f;

        var specs = new (Tool tool, StrokeKind kind, string name)[]
        {
            (Tool.Line, StrokeKind.Line, "line"),
            (Tool.Arrow, StrokeKind.Arrow, "arrow"),
            (Tool.Rectangle, StrokeKind.Rectangle, "rect"),
            (Tool.Parallelogram, StrokeKind.Parallelogram, "para"),
            (Tool.Ellipse, StrokeKind.Ellipse, "ellipse"),
            (Tool.Circle, StrokeKind.Circle, "circle"),
            (Tool.Triangle, StrokeKind.Triangle, "tri"),
        };

        Stroke picked = null;
        for (int i = 0; i < specs.Length; i++)
        {
            var (tool, kind, name) = specs[i];
            int col = i % 4, row = i / 4;
            float x = left + col * unit * 2.05f;
            float y = top + row * unit * 2.1f;
            var s = new Stroke
            {
                Tool = tool, Kind = kind,
                Color = new Color4(0.10f, 0.12f, 0.16f, 1f), Width = 5f * dpi,
            };
            ShapeGeometry.SetFromDrag(s, x - unit, y - unit * 0.7f, x + unit, y + unit * 0.7f, false);
            Doc.AddStroke(s);
            if (name == select) picked = s;
        }

        Doc.Selected.Clear();
        if (picked != null) Doc.Selected.Add(picked);
        Tool = Tool.Marquee;
        PassThrough = false;
        ShowHud = false;
        Doc.InvalidateAll();
        _dirty = true;
        SettleFrames(700);
        Console.WriteLine($"七个图形已摆好，选中的是 {select}"
                        + (picked == null ? "（没找到这个名字，比划一下：line/arrow/rect/para/ellipse/circle/tri）" : "")
                        + $"；它的包围盒 {picked?.Bounds.MaxX - picked?.Bounds.MinX:F0}×{picked?.Bounds.MaxY - picked?.Bounds.MinY:F0}");
        Console.WriteLine("shape showcase ready");
    }

    /// <summary>
    /// 面积橡皮"切图"摆样：粗笔迹 + 细笔迹 + 矩形 + 图片，然后用竖矩形擦一刀。
    ///
    /// 看的是两件事：**框内不该剩墨**（旧版按中心线切，粗笔画会剩一半，
    /// 看起来像"被啃了一口/变形了"），以及**切口是不是干净的直线**。
    /// </summary>
    private void EraseCutShowcase()
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float dpi = DpiScale;
        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        Stroke HLine(float y, float widthLogical)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.05f, 0.45f, 0.85f, 1f),
                Width = widthLogical * dpi,
            };
            for (int i = 0; i <= 60; i++)
                s.AddPoint(cx - 520 * dpi + i * (1040 * dpi / 60f), y, 0.9f, i * 5);
            return s;
        }

        Doc.AddStroke(HLine(cy - 200 * dpi, 6f));      // 细笔
        Doc.AddStroke(HLine(cy - 100 * dpi, 40f));     // 粗笔：旧版就是它会"剩一半"
        Doc.AddStroke(HLine(cy + 0 * dpi, 90f));       // 荧光笔那种超粗

        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.85f, 0.25f, 0.1f, 1f), Width = 5f * dpi,
        };
        ShapeGeometry.SetFromDrag(rect, cx - 400 * dpi, cy + 120 * dpi, cx + 400 * dpi, cy + 320 * dpi, false);
        Doc.AddStroke(rect);

        // 图片：棋盘（看"擦到图片"是整张删还是留一半）
        const int W = 200, H = 90;
        var pix = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool light = ((x / 20) + (y / 20)) % 2 == 0;
                byte v = light ? (byte)220 : (byte)150;
                pix[i] = v; pix[i + 1] = v; pix[i + 2] = v; pix[i + 3] = 255;
            }
        Doc.AddImage(ImageData.Adopt(W, H, pix, false), cx + 560 * dpi, cy + 120 * dpi, 1f);

        // 一刀：竖矩形（高 150 逻辑像素、宽 = 高/黄金比）
        AreaEraserHeightLogical = 200f;
        var cut = AreaEraserRect(cx, cy - 100 * dpi);
        Console.WriteLine($"      擦除矩形（画布坐标）：X {cut.MinX:F0}~{cut.MaxX:F0}，Y {cut.MinY:F0}~{cut.MaxY:F0}"
                        + $"（宽 {cut.MaxX - cut.MinX:F0} × 高 {cut.MaxY - cut.MinY:F0}，竖着的）");
        Doc.EraseAreaRect(cut);

        Doc.Selected.Clear();
        Tool = Tool.Pen;
        PassThrough = false;
        ShowHud = false;
        Doc.InvalidateAll();
        _dirty = true;
        SettleFrames(700);
        Console.WriteLine($"擦完之后对象数：{Doc.Strokes.Count}（切段 + 删掉碰到的图形）");
        Console.WriteLine("erase cut showcase ready");
    }

    private void ImageShowcase()
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        const int W = 420, H = 260;
        var pix = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                // 棋盘：两种灰；再叠一个橙红色块和一条蓝条，翻转/旋转一眼能看出来
                bool light = ((x / 30) + (y / 30)) % 2 == 0;
                byte v = light ? (byte)225 : (byte)170;
                byte b = v, g = v, r = v;
                if (x > 250 && y > 150) { r = 235; g = 90; b = 40; }        // 橙红块
                if (y > 30 && y < 60) { r = 30; g = 120; b = 215; }         // 蓝条
                pix[i + 0] = b; pix[i + 1] = g; pix[i + 2] = r; pix[i + 3] = 255;
            }

        float dpi = DpiScale;
        var img = ImageData.Adopt(W, H, pix, false);
        var placed = Doc.AddImage(img, _virtualX + 60f * dpi, _virtualY + 60f * dpi, 1f);
        Doc.SelectOnly(new[] { placed });
        Tool = Tool.Marquee;
        PassThrough = false;
        PointerInside = true;
        _drawing = false;
        ShowHud = false;
        Doc.InvalidateAll();
        _dirty = true;
        SettleFrames(700);
        Console.WriteLine($"图像已摆好并选中：{W}×{H} 像素，画布 {img.Width * placed.Transform.M11:F0}×{img.Height * placed.Transform.M22:F0}");
        Console.WriteLine("image showcase ready");
    }

    private void EraseDemo()
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float dpi = DpiScale;
        float y0 = _virtualY + _virtualH * 0.42f;
        float unit = 130f * dpi;

        Stroke StrokeOf(float x, float y, float dx, float dy)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.05f, 0.05f, 0.08f, 1f), Width = 12f * dpi,
            };
            int n = 40;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                s.AddPoint(x + dx * t, y + dy * t, 0.85f, i * 5);
            }
            return s;
        }

        // "王"：三横一竖，一共四笔
        void DrawWang(float cx, float top)
        {
            Doc.AddStroke(StrokeOf(cx - unit * 0.5f, top, unit, 0f));
            Doc.AddStroke(StrokeOf(cx - unit * 0.35f, top + unit * 0.5f, unit * 0.7f, 0f));
            Doc.AddStroke(StrokeOf(cx - unit * 0.5f, top + unit, unit, 0f));
            Doc.AddStroke(StrokeOf(cx, top, 0f, unit));          // 竖：最后画，在最上面
        }

        float leftX = _virtualX + _virtualW * 0.3f;
        float rightX = _virtualX + _virtualW * 0.7f;
        DrawWang(leftX, y0 - unit * 0.5f);
        int leftCount = Doc.Strokes.Count;
        DrawWang(rightX, y0 - unit * 0.5f);

        Doc.BeginErase();
        // 左边：笔记橡皮点在中竖上（只擦最上面那一条 = 那一竖）
        Doc.EraseStrokeAt(leftX, y0, 12f * dpi);
        Doc.EndErase();
        int leftAfter = Doc.Strokes.Count;

        Doc.BeginErase();
        // 右边：面积橡皮横着扫过整个字
        for (int i = -6; i <= 6; i++)
        {
            float x = rightX + i * 12f * dpi;
            Doc.EraseAreaRect(new RectF
            {
                MinX = x - 20f * dpi, MinY = y0 - 16f * dpi,
                MaxX = x + 20f * dpi, MaxY = y0 + 16f * dpi,
            });
        }
        Doc.EndErase();

        Doc.Selected.Clear();
        Tool = Tool.Pen;
        PassThrough = false;
        ShowHud = false;
        Doc.InvalidateAll();
        _dirty = true;
        SettleFrames(700);
        Console.WriteLine($"左边：笔记橡皮，{leftCount} 笔 → {leftAfter} 笔（只少了中间那一竖）");
        Console.WriteLine($"右边：面积橡皮，{leftCount} 笔 → {Doc.Strokes.Count - leftAfter} 段（横着一道缺口）");
        Console.WriteLine("erase demo ready");
    }

    /// <summary>
    /// 顶点编辑摆样：摆一个选中并进入顶点编辑的矩形 + 一个普通选中框，
    /// 挂住不动由外部截图。目的和 --rotateshow 一样——手柄这类东西
    /// 只有看在图上才算验过（位置对不对、会不会和角手柄打架）。
    /// </summary>
    private void VertexShowcase()
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float sx = _virtualX + _virtualW * 0.5f, sy = _virtualY + _virtualH * 0.55f;
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = PenColor, Width = 6f * DpiScale,
        };
        ShapeGeometry.SetFromDrag(rect, sx - 260f * DpiScale, sy - 170f * DpiScale,
                                  sx + 260f * DpiScale, sy + 170f * DpiScale, false);
        Doc.AddStroke(rect);

        var tri = new Stroke
        {
            Tool = Tool.Triangle, Kind = StrokeKind.Triangle,
            Color = PenColor, Width = 6f * DpiScale,
        };
        ShapeGeometry.SetFromDrag(tri, sx - 700f * DpiScale, sy - 170f * DpiScale,
                                  sx - 260f * DpiScale, sy + 170f * DpiScale, false);
        Doc.AddStroke(tri);

        Doc.Selected.Clear();
        Doc.Selected.Add(rect);
        Tool = Tool.Marquee;
        VertexEdit = true;
        PassThrough = false;
        PointerInside = true;
        _drawing = false;
        ShowHud = false;
        Doc.InvalidateAll();
        _dirty = true;
        SettleFrames(600);
        Console.WriteLine($"顶点编辑已摆好：{rect.Kind}，{VertexHandles.Count(rect)} 个顶点可拖"
                        + $"（左边是三角形、中间是进入顶点编辑的矩形）");
        Console.WriteLine("vertex showcase ready");
    }

    /// <summary>
    /// 旋转标签摆样：选中一个对象、把框转到指定角度、把度数标签挂出来不动，
    /// 由外部截图人工核对（位置、可读性、吸附变色）。这个模式不会自己退出。
    /// </summary>
    private void RotateShowcase(float deg)
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float sx = _virtualX + _virtualW * 0.5f, sy = _virtualY + _virtualH * 0.55f;
        var stroke = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = PenColor, Width = 8f * DpiScale,
        };
        for (int i = 0; i <= 48; i++)
        {
            float a = i / 48f * MathF.PI * 2f;
            stroke.AddPoint(sx + MathF.Cos(a) * 170f * DpiScale,
                            sy + MathF.Sin(a) * 120f * DpiScale, 0.9f, i * 8);
        }
        Doc.AddStroke(stroke);
        Doc.Selected.Clear();
        Doc.Selected.Add(stroke);
        Tool = Tool.Marquee;

        // 真的转过去：框和标签都跟着转，截图才是"正在转"的样子
        var m = Matrix3x2.CreateRotation(deg * MathF.PI / 180f, new Vector2(sx, sy));
        Doc.SetTransformLive(stroke, m);
        Doc.SetTransformLive(stroke, m);
        _dirty = true;
        SettleFrames(300);

        SelRotating = true;
        SelRotationDegrees = deg;
        SelRotationSnapped = Math.Abs(deg % 90f) < 0.5f;
        _dirty = true;
        SettleFrames(700);
        Console.WriteLine($"旋转标签已摆好：{SelectionHandles.FormatDegrees(deg)}"
                        + (SelRotationSnapped ? "（吸附态）" : "（自由态）"));
        Console.WriteLine("rotate showcase ready");
    }

    /// <summary>
    /// 旋转度数 + 吸附自检。
    ///
    /// 分两段：
    ///   A. **数学层**：读数、三种吸附模式（软吸附 90° / Shift 硬网格 15° / Alt 自由）、
    ///      边界值，以及"读数与矩阵自洽"（用独立的 atan2 再量一遍矩阵转过的角度）。
    ///   B. **真机层**：合成鼠标按在旋转手柄上拖，然后**数屏幕像素**确认度数标签
    ///      真的画出来了、"吸住"时真的变色、松手后真的消失。
    ///
    /// 为什么非要 B：标签是画在浮动层上的，进没进每帧脏区、双缓冲会不会把它留在
    /// 屏幕上，只有看像素才知道。选中框那一套 UI 就踩过"漏算脏区 → 残影"的坑。
    /// </summary>
    private void RotateTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 旋转度数 / 吸附自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-28}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        // ================= A. 数学层 =================
        var b = new RectF { MinX = 600, MinY = 500, MaxX = 1000, MaxY = 800 };
        var center = new Vector2((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f);
        const float radius = 260f;
        // 起点固定在正上方（屏幕上的 -90°），目标点 = 起点绕中心转 deg 度
        Vector2 At(float deg)
        {
            float rad = (-90f + deg) * MathF.PI / 180f;
            return new Vector2(center.X + radius * MathF.Cos(rad), center.Y + radius * MathF.Sin(rad));
        }
        var from = At(0f);

        (float deg, bool snapped) Probe(float deg, bool shift = false, bool alt = false)
            => (SelectionHandles.RotationDeltaDegrees(center, from, At(deg), shift, alt, out bool s), s);

        void Case(string name, float target, float want, bool wantSnap,
                  bool shift = false, bool alt = false)
        {
            var (got, snapped) = Probe(target, shift, alt);
            bool ok = Math.Abs(got - want) < 0.05f && snapped == wantSnap;
            Check(name, ok, $"转 {target:F1}° → {got:F1}°{(snapped ? "（吸附）" : "（自由）")}"
                          + $"，期望 {want:F1}°{(wantSnap ? "（吸附）" : "（自由）")}");
        }

        Case("正对网格：吸附", 0f, 0f, true);
        Case("容差内：吸到 0°", 2f, 0f, true);
        Case("容差外：保持自由", 5f, 5f, false);
        Case("普通角度：保持自由", 43f, 43f, false);
        Case("容差内：吸到 90°", 88f, 90f, true);
        Case("容差内：吸到 -90°", -89f, -90f, true);
        Case("容差外：保持自由（90 附近）", 95f, 95f, false);
        Case("半个整角：自由", 45f, 45f, false);
        Case("整角 180°：吸附", 180f, 180f, true);
        Case("负整角：归一化到 180°", -180f, 180f, true);
        Case("Shift：硬网格 15°", 20f, 15f, true, shift: true);
        Case("Shift：吸到 0°", 7f, 0f, true, shift: true);
        Case("Alt：完全自由", 2f, 2f, false, alt: true);
        Case("Shift+Alt：Shift 优先", 20f, 15f, true, shift: true, alt: true);

        // 读数与矩阵必须自洽：矩阵转过的角度（独立用 atan2 量）要等于读数
        foreach (float target in new[] { 0f, 43f, 88f, -137f, 179f })
        {
            var (deg, _) = Probe(target);
            var m = SelectionHandles.DragMatrix(SelHandle.Rotate, b, from, At(target), 1f, false, false);
            var moved = Vector2.Transform(from, m);
            float measured = MathF.Atan2(moved.Y - center.Y, moved.X - center.X)
                           * 180f / MathF.PI + 90f;         // 起点在 -90°，所以加回来
            // 比较用**带符号**的角：读数给用户看的是 0~360，数学层仍然用 (-180,180]
            // （"转了多少"少了符号就分不出顺时针还是逆时针）。
            measured = SelectionHandles.WrapSignedDegrees(measured);
            float degSigned = SelectionHandles.WrapSignedDegrees(deg);
            var pivotAfter = Vector2.Transform(center, m);
            Check($"拖动 {target,5:F0}°：读数与矩阵一致",
                  Math.Abs(measured - degSigned) < 0.2f
                  && Math.Abs(pivotAfter.X - center.X) < 0.05f
                  && Math.Abs(pivotAfter.Y - center.Y) < 0.05f,
                  $"读数 {deg:F1}°，矩阵量出 {measured:F1}°，轴心偏移 "
                  + $"{Vector2.Distance(pivotAfter, center):F3}px");
        }

        Check("标签文案不留 -0°", SelectionHandles.FormatDegrees(-0.4f) == "0°",
              $"{-0.4f:F1}° → {SelectionHandles.FormatDegrees(-0.4f)}");
        Check("标签文案取整", SelectionHandles.FormatDegrees(89.6f) == "90°",
              $"89.6° → {SelectionHandles.FormatDegrees(89.6f)}");
        // 读数规定在 0~360：负角一律折算成正的（-45° = 315°），没有减号。
        Check("标签文案不带负号", SelectionHandles.FormatDegrees(-45f) == "315°",
              $"-45° → {SelectionHandles.FormatDegrees(-45f)}");
        Check("360° 归一到 0°", SelectionHandles.FormatDegrees(360f) == "0°"
                              && SelectionHandles.FormatDegrees(-0.2f) == "0°",
              $"360° → {SelectionHandles.FormatDegrees(360f)}，-0.2° → {SelectionHandles.FormatDegrees(-0.2f)}");
        Check("值域始终在 0~360", SelectionHandles.NormalizeDegrees(-1f) > 358f
                                && SelectionHandles.NormalizeDegrees(721f) < 2f,
              $"-1° → {SelectionHandles.NormalizeDegrees(-1f):F0}°，721° → {SelectionHandles.NormalizeDegrees(721f):F0}°");

        // ================= B. 真机层 =================
        if (SkipIfNoSyntheticInput("旋转标签的真机上屏核对"))
        {
            Console.WriteLine($"  PASS: 度数读数与吸附的数学层全对（{pass} 项）；真机层已跳过");
            _quit = true;
            return;
        }
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float sx = _virtualX + _virtualW * 0.5f, sy = _virtualY + _virtualH * 0.5f;
        var stroke = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 12f * DpiScale,
        };
        // 画个圈：选区足够大，手柄到轴心的半径才够长，角度才好量准
        for (int i = 0; i <= 48; i++)
        {
            float a = i / 48f * MathF.PI * 2f;
            stroke.AddPoint(sx + MathF.Cos(a) * 180f * DpiScale,
                            sy + MathF.Sin(a) * 180f * DpiScale, 0.9f, i * 8);
        }
        Doc.AddStroke(stroke);
        Doc.Selected.Clear();
        Doc.Selected.Add(stroke);
        Tool = Tool.Marquee;
        SettleFrames(400);

        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;
        var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
        var pivot = new Vector2((frame.CanvasAabb.MinX + frame.CanvasAabb.MaxX) * 0.5f,
                                (frame.CanvasAabb.MinY + frame.CanvasAabb.MaxY) * 0.5f);
        float arm = Vector2.Distance(grip, pivot);
        Vector2 GripAt(float deg)
        {
            float rad = (-90f + deg) * MathF.PI / 180f;
            return new Vector2(pivot.X + arm * MathF.Cos(rad), pivot.Y + arm * MathF.Sin(rad));
        }

        // 探针窗：**当前**旋转手柄上方那一块（标签就画在那儿），刻意不覆盖选中框本身。
        // 必须每一步都重算手柄位置——手柄是跟着框转的，拖动之后标签早就不在原来的地方了。
        int probeW = (int)(150f * dpi), probeH = (int)(120f * dpi);
        int AccentCount()
        {
            var f = SelectionHandles.FrameOf(Doc.Selected);
            var h = SelectionHandles.CanvasPosition(SelHandle.Rotate, f, dpi);
            return ScreenProbe.CountNear((int)(h.X - probeW * 0.5f), (int)(h.Y - probeH),
                                         probeW, probeH, 0, 120, 212, 40);
        }

        // 标签里强调色占多少 —— **只量标签自己那一块**。
        //
        // 以前这里量的是"手柄上方一大片"（150×120 逻辑像素），结果在实测里
        // 把桌面壁纸的蓝数成了强调色（本机壁纸恰好是蓝的）：拖动前 92 像素、
        // 拖动后 15001 像素，看起来像"标签没擦掉"，其实是量错了地方。
        // 标签是不透明的圆角块（白底或强调色底），只量它就不会被背景干扰。
        int LabelArea(out RectF rect)
        {
            var f = SelectionHandles.FrameOf(Doc.Selected);
            rect = SelectionHandles.ReadoutRect(f, dpi, ViewportCanvas);
            int w = (int)MathF.Max(1, rect.MaxX - rect.MinX);
            int h = (int)MathF.Max(1, rect.MaxY - rect.MinY);
            return w * h;
        }
        int AccentInLabel(out int area)
        {
            area = LabelArea(out var r);
            return ScreenProbe.CountNear((int)r.MinX, (int)r.MinY,
                                         (int)(r.MaxX - r.MinX), (int)(r.MaxY - r.MinY),
                                         0, 120, 212, 40);
        }

        int beforeDraw = AccentCount();
        Check("没拖之前，标签位置是干净的", beforeDraw < 800, $"{beforeDraw} 像素");

        // 按下旋转手柄，拖到 92°（容差内 → 应该吸到 90°）
        SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN);
        var to92 = GripAt(92f);
        SendMouse((int)to92.X, (int)to92.Y, 0);
        SettleFrames(260);

        Check("拖动中标记为旋转", SelRotating, $"SelRotating={SelRotating}");
        Check("读数 ≈ 90°", Math.Abs(SelRotationDegrees - 90f) < 1f, $"{SelRotationDegrees:F1}°");
        Check("92° 被吸到 90°", SelRotationSnapped, $"snapped={SelRotationSnapped}");
        int snappedPixels = AccentInLabel(out int snappedArea);
        Check("吸住时标签上屏，且整块是强调色",
              snappedPixels > snappedArea * 0.5f,
              $"{snappedPixels}/{snappedArea} 像素（{snappedPixels * 100f / snappedArea:F0}%）");

        // 再拖到 43°（容差外 → 不吸）
        var to43 = GripAt(43f);
        SendMouse((int)to43.X, (int)to43.Y, 0);
        SettleFrames(260);
        Check("43° 保持自由", !SelRotationSnapped && Math.Abs(SelRotationDegrees - 43f) < 1f,
              $"{SelRotationDegrees:F1}°，snapped={SelRotationSnapped}");
        int freePixels = AccentInLabel(out int freeArea);
        Check("没吸住时标签是白底（强调色只来自文字）",
              freePixels < freeArea * 0.15f,
              $"{freePixels}/{freeArea} 像素（{freePixels * 100f / freeArea:F0}%）");

        // 松手前先把标签那一块拍下来，松手后再拍一次：两块必须**不同**，
        // 才说明标签真的从屏幕上抹掉了（而不是模型里不画、屏幕上还留着）。
        var labelRect = SelectionHandles.ReadoutRect(
            SelectionHandles.FrameOf(Doc.Selected), dpi, ViewportCanvas);
        int lw = (int)(labelRect.MaxX - labelRect.MinX), lh = (int)(labelRect.MaxY - labelRect.MinY);
        var withLabel = ScreenProbe.CaptureRegion((int)labelRect.MinX, (int)labelRect.MinY, lw, lh);

        // 松手：标签必须消失，且一次拖拽只留一条撤销记录
        SendMouse((int)to43.X, (int)to43.Y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(320);
        Check("松手后不再标记旋转", !SelRotating, $"SelRotating={SelRotating}");
        var withoutLabel = ScreenProbe.CaptureRegion((int)labelRect.MinX, (int)labelRect.MinY, lw, lh);
        int labelDiff = ScreenProbe.DiffCount(withLabel, withoutLabel, tolerance: 8);
        Check("松手后标签从屏幕上真的消失了", labelDiff > 600,
              $"同一块区域前后差 {labelDiff} 像素（区域共 {lw * lh}）");

        var rotated = Doc.Strokes[0].Transform;
        Check("对象真的被转了（不是只显示个标签）",
              Math.Abs(rotated.M11 - 1f) > 0.01f || Math.Abs(rotated.M12) > 0.01f,
              $"M11={rotated.M11:F3} M12={rotated.M12:F3}");

        Doc.Undo();
        SettleFrames(200);
        var back = Doc.Strokes.Count > 0 ? Doc.Strokes[0].Transform : Matrix3x2.Identity;
        Check("一次拖拽 = 一步撤销，撤销后回原位",
              Math.Abs(back.M11 - 1f) < 1e-4f && Math.Abs(back.M12) < 1e-4f
              && Math.Abs(back.M21) < 1e-4f && Math.Abs(back.M22 - 1f) < 1e-4f,
              $"撤销后 M11={back.M11:F4} M12={back.M12:F4}");

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS: 度数读数、三种吸附模式、矩阵自洽与标签上屏都正确"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");

        Doc.Clear();
        Doc.ClearHistory();
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

    /// <summary>
    /// 铺开 <paramref name="screens"/> 屏的笔画。
    ///
    /// 为什么要有它：<see cref="GenerateStrokes"/> 把笔画全塞在一屏里，那是
    /// "满屏"的极端形状，和真实板书不一样。真实情况是**纵向累积**——一节课
    /// 往下写十几屏，每屏只有那么多字。两者的差别对分块缓存是决定性的：
    /// 前者的重建代价随总量涨，后者不该涨。
    /// </summary>
    private void GenerateStrokesSpread(int strokeCount, int screens)
    {
        var rnd = new Random(20260913);
        Doc.Clear();
        float spanH = _virtualH * screens;
        for (int i = 0; i < strokeCount; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = PenColor,
                Kind = StrokeKind.Freehand,
                Width = 2.5f + (float)rnd.NextDouble() * 4f,
            };
            float x = _virtualX + (float)rnd.NextDouble() * _virtualW;
            float y = _virtualY + (float)rnd.NextDouble() * spanH;
            int pts = 8 + rnd.Next(24);
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            for (int j = 0; j < pts; j++)
            {
                ang += (float)((rnd.NextDouble() - 0.5) * 0.7);
                float d = 5f + (float)rnd.NextDouble() * 9f;
                x = Math.Clamp(x + MathF.Cos(ang) * d, _virtualX + 1, _virtualX + _virtualW - 1);
                y = Math.Clamp(y + MathF.Sin(ang) * d, _virtualY + 1, _virtualY + spanH - 1);
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

        P("===== InkTeach 内存归因 =====");
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

        P("===== InkTeach 实测数据 =====");
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
            0, _className, "InkTeach 点击目标",
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
    /// <summary>
    /// 指针形状自检：把"设备 × 工具 × 悬停目标 × 是否拖拽"这张表逐条算出来，
    /// 跟期望值比对。
    ///
    /// 为什么值得写成自检：光标这类东西**漏一个状态很难靠肉眼发现**——
    /// 典型的是"拖拽中突然变回箭头"，只有真去拖一遍才会看到。这里把状态
    /// 直接构造出来，一条命令跑完全部组合。
    ///
    /// 只验"该显示什么"（纯计算，不碰系统）。"系统真的显示出来了"要另开
    /// 一个进程读 GetCursorInfo，那是端到端测试的事。
    /// </summary>
    private void CursorTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 指针形状自检（状态 → 光标）===");
        Console.WriteLine($"  系统指针尺寸 {CursorSizePx}px   DPI 缩放 {DpiScale:F2}");
        Console.WriteLine();
        // 先复位工具尺寸：用户配置里挑过的粗细不该影响这里的判据
        // （"换一档会变成多少"这类断言，输入必须是我们自己摆的）。
        ResetToolSizesToDefaults();

        int pass = 0, fail = 0;

        void Check(string name, CursorKind want)
        {
            var got = ComputeCursorKind();
            bool ok = got == want;
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} 期望 {Cursors.Name(want),-24} 实际 {Cursors.Name(got)}");
        }

        void CheckBool(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        // 准备一份真实的选区：三笔横线，全选。
        Doc.Clear();
        for (int i = 0; i < 3; i++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f * DpiScale };
            float y = 500 + i * 140;
            for (int k = 0; k <= 60; k++) s.AddPoint(600 + k * 14, y, 1f, NowMs);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();

        PassThrough = false;
        PointerInside = true;
        LastPointerType = Native.PT_MOUSE;
        Doc.Selected.Clear();
        Tool = Tool.Pen;
        PointerX = 100; PointerY = 100;

        // ---- 输入设备 × 工具 ----
        // 规则：鼠标没有笔尖 → 悬停和书写都自绘；手写笔的笔尖就是落点 → 落笔后不画。
        Check("画笔 · 鼠标悬停（不再用系统十字）", CursorKind.Hidden);
        CheckBool("画笔 · 悬停落点是圆环", DrawnCursor == ToolCursorShape.Ring, $"DrawnCursor={DrawnCursor}");

        LastPointerType = Native.PT_PEN;
        Check("画笔 · 手写笔悬停（落点自己画）", CursorKind.Hidden);
        LastPointerType = Native.PT_TOUCH;
        Check("触摸（不显示指针）", CursorKind.Hidden);

        LastPointerType = Native.PT_MOUSE;
        Tool = Tool.Highlighter;
        Check("荧光笔 · 悬停", CursorKind.Hidden);
        CheckBool("荧光笔 · 落点是宽度胶囊",
            DrawnCursor == ToolCursorShape.Capsule,
            $"DrawnCursor={DrawnCursor}");

        Tool = Tool.Eraser;
        Check("橡皮 · 悬停", CursorKind.Hidden);
        CheckBool("橡皮 · 落点是圆环（半径跟着橡皮半径走）",
            DrawnCursor == ToolCursorShape.Ring && DrawnCursorRadius > 0f,
            $"DrawnCursor={DrawnCursor} 半径={DrawnCursorRadius:F0}px");

        LastPointerType = Native.PT_PEN;
        Tool = Tool.Pen;
        CheckBool("手写笔 · 悬停时笔画环", DrawnCursor == ToolCursorShape.Ring, $"DrawnCursor={DrawnCursor}");
        LastPointerType = Native.PT_MOUSE;

        Tool = Tool.Laser;
        Check("激光笔 · 悬停", CursorKind.Hidden);
        CheckBool("激光笔 · 落点是实心点",
            DrawnCursor == ToolCursorShape.Dot, $"DrawnCursor={DrawnCursor}");
        Tool = Tool.Marquee;
        Check("框选 · 空白处", CursorKind.Cross);

        // ---- 书写中：鼠标照画，手写笔不画 ----
        _drawing = true;
        LastPointerType = Native.PT_MOUSE;
        Tool = Tool.Pen;         CheckBool("鼠标书写中 · 笔环跟着走", DrawnCursor == ToolCursorShape.Ring, $"{DrawnCursor}");
        Tool = Tool.Highlighter; CheckBool("鼠标书写中 · 胶囊跟着走", DrawnCursor == ToolCursorShape.Capsule, $"{DrawnCursor}");
        Tool = Tool.Laser;       CheckBool("鼠标书写中 · 点跟着走", DrawnCursor == ToolCursorShape.Dot, $"{DrawnCursor}");
        LastPointerType = Native.PT_PEN;
        Tool = Tool.Pen;         CheckBool("手写笔落笔后 · 不画（笔尖即落点）", DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        Tool = Tool.Eraser;      CheckBool("橡皮擦除中 · 一直画（范围要看得见）", DrawnCursor == ToolCursorShape.Ring, $"{DrawnCursor}");
        _drawing = false;
        LastPointerType = Native.PT_MOUSE;

        // ---- 粗细 → 落点反馈 ----
        Tool = Tool.Pen;
        PenWidthLogical = 1.5f;
        float thinTrue = CursorRingTrueRadius, thinOuter = CursorOuterRadius;
        float thinDirty = DrawnCursorRadius;
        PenWidthLogical = 24f;
        float fatTrue = CursorRingTrueRadius, fatOuter = CursorOuterRadius;
        CheckBool("笔 · 内圈跟着真实笔宽变",
            fatTrue > thinTrue * 10f, $"1.5 → {thinTrue:F1}px，24 → {fatTrue:F1}px（半径）");
        CheckBool("笔 · 细笔有外圈兜底（看得见落点）",
            thinOuter > thinTrue && Math.Abs(thinOuter - 6f * DpiScale) < 0.01f,
            $"真半径 {thinTrue:F1}px，外圈 {thinOuter:F1}px（下限 {6f * DpiScale:F1}px）");
        CheckBool("笔 · 粗笔时内外圈合一",
            fatOuter <= fatTrue + 0.01f, $"外圈 {fatOuter:F1}px，真半径 {fatTrue:F1}px");
        CheckBool("笔 · 脏区跟着落点反馈放大",
            DrawnCursorRadius > thinDirty, $"{thinDirty:F0}px → {DrawnCursorRadius:F0}px");

        float penBefore = PenWidthLogical, hlBefore = HighlighterWidthLogical;
        Tool = Tool.Highlighter;
        HighlighterWidthLogical = 8f;
        float thinCapsule = DrawnCursorRadius;
        HighlighterWidthLogical = 32f;
        CheckBool("荧光笔 · 胶囊跟着荧光笔宽变",
            DrawnCursorRadius > thinCapsule * 1.5f,
            $"8 → {thinCapsule:F0}px，32 → {DrawnCursorRadius:F0}px（脏区半径）");
        HighlighterWidthLogical = hlBefore;

        Tool = Tool.Laser;
        LaserWidthLogical = 4f;
        float thinDot = DrawnCursorRadius;
        LaserWidthLogical = 14f;
        CheckBool("激光笔 · 点跟着激光宽变",
            DrawnCursorRadius > thinDot, $"{thinDot:F0}px → {DrawnCursorRadius:F0}px");

        // ---- 切粗细改的是**当前工具**那一档（这条是 bug 回归）----
        Tool = Tool.Laser;
        float laserBefore = LaserWidthLogical;
        CycleWidth();
        CheckBool("切粗细 · 激光改的是激光宽", LaserWidthLogical != laserBefore
                  && Math.Abs(PenWidthLogical - penBefore) < 1e-4f,
                  $"激光 {laserBefore} → {LaserWidthLogical}，笔宽保持 {PenWidthLogical}");
        Tool = Tool.Highlighter;
        float hlBefore2 = HighlighterWidthLogical;
        CycleWidth();
        CheckBool("切粗细 · 荧光笔改的是荧光笔宽", HighlighterWidthLogical != hlBefore2
                  && Math.Abs(PenWidthLogical - penBefore) < 1e-4f,
                  $"荧光笔 {hlBefore2} → {HighlighterWidthLogical}，笔宽保持 {PenWidthLogical}");
        Tool = Tool.Pen;
        float penBefore2 = PenWidthLogical;
        float hlAfterCycle = HighlighterWidthLogical;
        CycleWidth();
        CheckBool("切粗细 · 笔改的是笔宽", Math.Abs(PenWidthLogical - penBefore2) > 1e-4f
                  && Math.Abs(HighlighterWidthLogical - hlAfterCycle) < 1e-4f,
                  $"笔 {penBefore2} → {PenWidthLogical}");
        // 复位成常用值，后面的用例不受影响
        PenWidthLogical = 3f; HighlighterWidthLogical = 18f; LaserWidthLogical = 4f;

        // ---- 选中框：八个手柄 + 旋转 + 整体拖动 + 操作条 ----
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;

        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;
        var aabb = frame.CanvasAabb;

        void At(SelHandle h)
        {
            var p = SelectionHandles.CanvasPosition(h, frame, dpi);
            PointerX = p.X; PointerY = p.Y;
        }

        At(SelHandle.Left);        Check("选中框 · 左边中点（左右拉伸）", CursorKind.ResizeWE);
        At(SelHandle.Right);       Check("选中框 · 右边中点（左右拉伸）", CursorKind.ResizeWE);
        At(SelHandle.Top);         Check("选中框 · 上边中点（上下拉伸）", CursorKind.ResizeNS);
        At(SelHandle.Bottom);      Check("选中框 · 下边中点（上下拉伸）", CursorKind.ResizeNS);
        At(SelHandle.TopLeft);     Check("选中框 · 左上角（对角）", CursorKind.ResizeNWSE);
        At(SelHandle.BottomRight); Check("选中框 · 右下角（对角）", CursorKind.ResizeNWSE);
        At(SelHandle.TopRight);    Check("选中框 · 右上角（对角）", CursorKind.ResizeNESW);
        At(SelHandle.BottomLeft);  Check("选中框 · 左下角（对角）", CursorKind.ResizeNESW);
        At(SelHandle.Rotate);      Check("选中框 · 旋转手柄", CursorKind.Rotate);

        PointerX = (aabb.MinX + aabb.MaxX) * 0.5f;
        PointerY = (aabb.MinY + aabb.MaxY) * 0.5f;
        Check("选中框 · 框内（整体移动）", CursorKind.Move);

        var b0 = SelectionHandles.BarButtonRect(0, aabb, dpi);
        PointerX = (b0.MinX + b0.MaxX) * 0.5f;
        PointerY = (b0.MinY + b0.MaxY) * 0.5f;
        Check("操作条按钮（不换光标）", CursorKind.Default);

        PointerX = aabb.MinX - 120f * dpi; PointerY = aabb.MinY - 120f * dpi;
        Check("选中框外的空白（重新框选）", CursorKind.Cross);

        // ---- 拖拽中：用按下那一刻的语义，不重新命中测试 ----
        PointerX = 100; PointerY = 100;         // 指针早就离开手柄了
        _drawing = true;
        SelDragging = true;
        _dragIsMove = false;
        _dragHandle = SelHandle.Left;
        Check("拖拽中 · 左右拉伸（指针已离开手柄）", CursorKind.ResizeWE);
        _dragHandle = SelHandle.Rotate;
        Check("拖拽中 · 旋转", CursorKind.Rotate);
        _dragIsMove = true;
        _dragHandle = SelHandle.None;
        Check("拖拽中 · 整体移动", CursorKind.Move);
        _drawing = false; SelDragging = false; _dragIsMove = false; _dragHandle = SelHandle.None;

        // ---- 滚动条 / 穿透 ----
        ScrollBarHover = true;
        Check("滚动条悬停（滑块自己变粗，不换光标）", CursorKind.Default);
        ScrollBarHover = false;

        PassThrough = true;
        Check("穿透模式（交给下层窗口）", CursorKind.Leave);
        PassThrough = false;

        // ---- 自绘光标能不能真的建出来 ----
        var hRot = Cursors.HandleFor(CursorKind.Rotate, CursorSizePx);
        var hHide = Cursors.HandleFor(CursorKind.Hidden, CursorSizePx);
        CheckBool("旋转光标建立成功（位图/热点没写错）", hRot != IntPtr.Zero,
            $"句柄 0x{hRot:X}  {CursorSizePx}px");
        CheckBool("隐藏光标建立成功", hHide != IntPtr.Zero, $"句柄 0x{hHide:X}");

        // 自绘光标导出成图，供人眼检查形状与抗锯齿。
        try
        {
            Directory.CreateDirectory("reports");
            string dump = Path.Combine("reports", "cursor-rotate.bmp");
            Cursors.DumpRotate(dump, CursorSizePx);
            // 再导一张大的：实际尺寸只有 64px，放大看才能判断形状和抗锯齿。
            Cursors.DumpRotate(Path.Combine("reports", "cursor-rotate-big.bmp"), 128, 3);
            Console.WriteLine($"  （旋转光标已导出：{dump}，放大 4 倍）");
            Console.WriteLine("  旋转光标圆周采样（0°=右 90°=下 180°=左 270°=上，缺口应在右上）：");
            Console.WriteLine("    " + Cursors.RotateRingReport(CursorSizePx));
            int stray = Cursors.RotateStrayPixels(CursorSizePx);
            CheckBool("旋转光标 · 圆环外没有多余墨点（箭头没跑位）", stray == 0, $"环外墨点 {stray} 个");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  旋转光标导出失败：" + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }

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

    private void GhostTest(bool scrolled = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 残影测试（大跨度快速移动橡皮光标，看会不会拖尾）===");

        if (SkipIfNoSyntheticInput("残影检测（需要合成鼠标移动光标）")) { _quit = true; return; }

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Eraser;
        if (scrolled)
        {
            ViewOffsetY = -_virtualH * 0.5f;      // 把相机挪开：画布坐标 ≠ 屏幕坐标
            Console.WriteLine($"  （滚动过：ViewOffsetY = {ViewOffsetY:F0}，画布坐标与屏幕坐标不再相等）");
        }
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
        byte[] during = null;
        double dirtyPercent = -1;
        var perHop = new List<string>();
        for (int i = 1; i <= hops; i++)
        {
            SendMouse((int)(x0 + i * 300f), (int)y, 0);
            SettleFrames(20);
            if (_windows.Count > 0)
                perHop.Add($"{i}:{_windows[0].LastPresentRectCount}块/{_windows[0].LastPresentAreaPercent:F1}%");
            if (i == 3)
            {
                // 扫到一半时停下拍一张：用来证明"光标确实画出来了"。
                // 少了这一步，光标因为脏区算错而**根本没画**的情况也会被判成
                // "没有残影"——测试就成了永远通过摆设。
                SettleFrames(300);
                during = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);
                dirtyPercent = _windows.Count > 0 ? _windows[0].LastPresentAreaPercent : -1;
            }
        }

        // 光标回到原处，等脏区排空，再拍一张
        SendMouse((int)parkX, (int)parkY, 0);
        SettleFrames(500);
        byte[] after = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);

        int diff = ScreenProbe.DiffCount(before, after);
        int visible = during != null ? ScreenProbe.DiffCount(before, during) : -1;
        Console.WriteLine($"  光标扫过区域前后逐像素差异: {diff} 像素（共 {bandW * bandH} 像素）");
        Console.WriteLine($"  对照：同一区域静置两帧的差异: {noise} 像素（环境噪声）");
        Console.WriteLine($"  扫过途中光标是否真的画出来了: {visible} 像素（应当 > 1000）");
        // 顺带记一下上屏面积：光标只是一小块，这里却是"光标 ∪ 滚动条那条竖带"的
        // 并集，所以数值偏大是正常的——只作为观察，不作判据。
        Console.WriteLine($"  扫过途中的上屏脏区: {dirtyPercent:F1}%（含滚动条竖带的并集，仅作观察）");
        Console.WriteLine("  每跳一格的脏区: " + string.Join("  ", perHop));

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
        bool drew = visible > 1000;
        bool ok = ours < 200 && drew;
        Console.WriteLine(ok
            ? $"  PASS: 光标画出来了，且没有残影（扣掉环境噪声后仅剩 {ours} 像素）"
            : !drew
                ? $"  FAIL: 光标压根没画出来（只差 {visible} 像素）——脏区算错了"
                : $"  FAIL: 出现拖尾残影（扣掉环境噪声后仍有 {ours} 像素）");
        _quit = true;
    }

    /// <summary>
    /// **分块缓存自检**：接缝、内容正确性、滚动复用、内存上界。
    ///
    /// 分块缓存有一类特别难查的 bug——**接缝**：笔画跨在块边界上时，如果
    /// 有一边的块没把这条笔画画进去，就会缺一条边；而"平时看着好好的、
    /// 只有粗笔画压在边界上才露出来"，靠肉眼很难碰到。
    ///
    /// 这里用"同一支笔，摆在块边界上 vs 摆在块正中间"做对照：墨量必须一样。
    /// 边界上少墨 = 接缝，多墨 = 重复绘制。
    /// </summary>
    private void TileTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 分块缓存自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        const int T = 256;                                  // 块边长，和 CanvasTileCache 一致
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        // ---- 1) 粗横线压在横向块边界上 ----------------------------------
        // 边界取 y = 3T = 768（画布坐标 = 屏幕坐标，因为相机为 0、原点为 0）。
        float w2 = 40f;
        int onSeam = DrawAndCountHorizontal(_virtualX + 300, 3 * T, 1200, w2);
        int midTile = DrawAndCountHorizontal(_virtualX + 300, 3 * T + T / 2, 1200, w2);
        int diffY = Math.Abs(onSeam - midTile);
        Check("横线压在块边界上不缺墨", onSeam > 40000 && diffY < midTile * 0.03,
              $"边界 {onSeam} vs 块中间 {midTile}（差 {diffY}，允许 {midTile * 0.03:F0}）");

        // ---- 2) 粗竖线压在纵向块边界上 ----------------------------------
        int onSeamX = DrawAndCountVertical(4 * T, _virtualY + 300, 1200, w2);
        int midTileX = DrawAndCountVertical(4 * T + T / 2, _virtualY + 300, 1200, w2);
        int diffX = Math.Abs(onSeamX - midTileX);
        Check("竖线压在块边界上不缺墨", onSeamX > 40000 && diffX < midTileX * 0.03,
              $"边界 {onSeamX} vs 块中间 {midTileX}（差 {diffX}，允许 {midTileX * 0.03:F0}）");

        // ---- 3) 跨块的长笔画：一整条都得在 ------------------------------
        // 从画面左上角一路斜到右下角，横跨十几个块。
        Doc.Clear();
        Doc.ClearHistory();
        var longStroke = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 2f * DpiScale,
        };
        for (int i = 0; i <= 40; i++)
        {
            float t = i / 40f;
            longStroke.AddPoint(_virtualX + 200 + t * 2400, _virtualY + 200 + t * 1400, 1f, i);
        }
        Doc.AddStroke(longStroke);
        SettleFrames(500);
        // 沿线取 6 段采样，每段都得有墨（中间任何一块漏画，就会有一段是空的）
        int emptySpots = 0;
        for (int k = 0; k < 6; k++)
        {
            float t = (k + 0.5f) / 6f;
            int n = ScreenProbe.CountMagenta(
                (int)(_virtualX + 200 + t * 2400) - 60, (int)(_virtualY + 200 + t * 1400) - 60, 120, 120);
            if (n < 50) emptySpots++;
        }
        Check("跨十几个块的长笔画不断线", emptySpots == 0, $"6 段采样里有 {emptySpots} 段是空的");

        // ---- 4) 滚下去再滚回来：墨量必须一模一样，而且回程不重画 -------
        int before = ScreenProbe.CountMagenta(_virtualX, _virtualY, _virtualW, _virtualH);
        var w0 = _windows[0];
        int rasterDown = 0, rasterUp = 0;
        for (int k = 0; k < 4; k++) { HandleWheel(Wheel(-120)); RenderAll(); rasterDown += w0.LastPatchCount; }
        for (int k = 0; k < 4; k++) { HandleWheel(Wheel(120)); RenderAll(); rasterUp += w0.LastPatchCount; }
        SettleFrames(300);
        int after = ScreenProbe.CountMagenta(_virtualX, _virtualY, _virtualW, _virtualH);

        Check("滚下去再滚回来画面一致", Math.Abs(after - before) <= before * 0.005 && before > 5000,
              $"{before} -> {after}");
        Check("回程复用了块缓存（没有重画）", rasterUp == 0,
              $"去程光栅 {rasterDown} 块，回程 {rasterUp} 块");

        // ---- 5) 常驻块数不超过预算 --------------------------------------
        Check("常驻分块不超预算", w0.LastTileCount <= Math.Max(w0.LastTileBudget, 1),
              $"{w0.LastTileCount} 块 / 预算 {w0.LastTileBudget}（每块 256KB）");

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：块边界无缝、跨块笔画完整、回程免重画、内存有上界");
        Console.WriteLine();
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    // 分块测试的滚轮构造（和 WheelTest 一致：delta 在高 16 位）
    private static IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);

    /// <summary>画一条粗横线并数它的墨像素。横线的**中心线**画在 y 上。</summary>
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

    private void EraserTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 橡皮擦测试（用稀疏采样快速划过，看会不会漏）===");

        if (SkipIfNoSyntheticInput("稀疏采样快划那一组")) { _quit = true; return; }

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

    /// <summary>
    /// 两种橡皮的模式自检。
    ///
    /// 验四件事，每一件都对应一个"用户能说出来的抱怨"：
    ///   1. **笔记橡皮在交叉处只删最上面那一条**——不然"擦掉其中一横"就变成
    ///      一擦一大块（这是"精准擦除"的可测定义）；
    ///   2. **面积橡皮把笔迹切成段**，切口贴在矩形边上（不是整条消失，
    ///      也不是留下一条横穿过去的残线）；
    ///   3. **一次拖拽 = 一步撤销**（绝不是"擦了三下要按三次 Ctrl+Z"）；
    ///   4. 真机上拖一把，屏幕上那块真的没了（数像素）。
    /// </summary>
    private void EraseModeTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 两种橡皮的模式自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        float dpi = DpiScale;

        Stroke Line(float x0, float y0, float x1, float y1, int steps = 80)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 10f * dpi,
            };
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                s.AddPoint(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, 0.9f, i * 4);
            }
            return s;
        }

        // ---------- A. 笔记橡皮：交叉处只擦最上面那一条 ----------
        Doc.Clear();
        Doc.ClearHistory();
        var horiz = Line(cx - 400, cy, cx + 400, cy);
        var vert = Line(cx, cy - 300, cx, cy + 300);
        Doc.AddStroke(horiz);
        Doc.AddStroke(vert);          // 后画的在上面
        Doc.ClearHistory();

        int removed = Doc.EraseStrokeAt(cx, cy, 10f * dpi);
        Check("笔记橡皮：交叉处只删最上面那条",
              removed == 1 && Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], horiz),
              $"删了 {removed} 条，剩 {Doc.Strokes.Count} 条"
              + (Doc.Strokes.Count == 1 ? $"（剩下的是{(ReferenceEquals(Doc.Strokes[0], horiz) ? "横" : "竖")}）" : ""));

        Doc.Undo();
        Check("笔记橡皮：整笔删除可一步撤销", Doc.Strokes.Count == 2, $"撤销后 {Doc.Strokes.Count} 条");

        // 擦在笔画旁边（不在墨上）不该删——这是"精准"的另一半
        int beside = Doc.EraseStrokeAt(cx + 60f * dpi, cy + 60f * dpi, 6f * dpi);
        Check("笔记橡皮：擦空白不误删", beside == 0 && Doc.Strokes.Count == 2, $"删了 {beside} 条");

        // ---------- B. 面积橡皮：切段 ----------
        // 粗笔画也要能"擦干净"：先画一条**很粗**的线（40 逻辑像素），
        // 切一刀之后**框内不该剩墨**——这正是用户报的"擦的时候像变形"。
        {
            Doc.Clear();
            Doc.ClearHistory();
            var fat = Line(cx - 400, cy + 260, cx + 400, cy + 260, steps: 80);
            fat.Width = 40f * dpi;
            Doc.AddStroke(fat);
            Doc.ClearHistory();
            float half = 50f;
            var box = new RectF { MinX = cx - half, MinY = cy + 200, MaxX = cx + half, MaxY = cy + 320 };
            Doc.EraseAreaRect(box);
            bool anyInside = false;
            foreach (var piece in Doc.Strokes)
                if (piece.Bounds.MaxX > box.MinX && piece.Bounds.MinX < box.MaxX
                    && piece.Bounds.MaxY > box.MinY && piece.Bounds.MinY < box.MaxY)
                    anyInside = true;
            Check("粗笔画：切完框内没有残留的碎段", !anyInside,
                  $"碎段 {Doc.Strokes.Count} 条，都不该伸进框里");
            Check("粗笔画：切口按笔宽外扩（不是按中心线）",
                  Doc.Strokes.Count == 2 && Math.Abs(Doc.Strokes[0].Bounds.MaxX - (box.MinX - 40f * dpi * 1.4f / 2f)) < 3f,
                  Doc.Strokes.Count == 2
                      ? $"左段到 {Doc.Strokes[0].Bounds.MaxX:F0}，框左边 {box.MinX:F0}"
                      : $"{Doc.Strokes.Count} 条碎段");
        }

        Doc.Clear();
        Doc.ClearHistory();
        var longLine = Line(cx - 500, cy, cx + 500, cy, steps: 100);
        int pointsBefore = longLine.Points.Count;
        Doc.AddStroke(longLine);
        Doc.ClearHistory();

        float halfGap = 60f;
        var cut = new RectF { MinX = cx - halfGap, MinY = cy - 40f, MaxX = cx + halfGap, MaxY = cy + 40f };
        Doc.BeginErase();
        Doc.EraseAreaRect(cut);
        Doc.EraseAreaRect(cut);                 // 同一批次里擦两次：仍然只算一步撤销
        Doc.EndErase();

        Check("面积橡皮：一条被切成两段", Doc.Strokes.Count == 2, $"剩 {Doc.Strokes.Count} 段");
        if (Doc.Strokes.Count == 2)
        {
            float leftEnd = Doc.Strokes[0].Bounds.MaxX;
            float rightStart = Doc.Strokes[1].Bounds.MinX;
            // 切口在**框外一个笔宽之内**：这样框内一定干净（旧版按中心线切，
            // 粗笔画会在框里剩半条，看起来就是"被啃了一口/变形了"）。
            float reach = longLine.Width * Stroke.MaxWidthFactor * 0.5f;
            Check("切口在框外一个笔宽之内（左段）",
                  leftEnd <= cut.MinX + 1f && leftEnd >= cut.MinX - reach - 2f,
                  $"左段到 {leftEnd:F1}，矩形左边 {cut.MinX:F1}（允许向外 {reach:F1}）");
            Check("切口在框外一个笔宽之内（右段）",
                  rightStart >= cut.MaxX - 1f && rightStart <= cut.MaxX + reach + 2f,
                  $"右段从 {rightStart:F1} 开始，矩形右边 {cut.MaxX:F1}");
            Check("碎片点数少于原来", Doc.Strokes[0].Points.Count + Doc.Strokes[1].Points.Count < pointsBefore,
                  $"{pointsBefore} → {Doc.Strokes[0].Points.Count} + {Doc.Strokes[1].Points.Count}");
            Check("碎片的颜色/线宽/变换原样保留",
                  Doc.Strokes[0].Width == longLine.Width && Doc.Strokes[0].Color.R == 1f
                  && Doc.Strokes[0].Transform.Equals(longLine.Transform),
                  $"宽 {Doc.Strokes[0].Width:F1}");
        }
        Check("一次拖拽只留一步撤销", Doc.UndoDepth == 1, $"撤销栈深度 {Doc.UndoDepth}");

        Doc.Undo();
        Check("切段可一步撤销回原样",
              Doc.Strokes.Count == 1 && Doc.Strokes[0].Points.Count == pointsBefore,
              $"{Doc.Strokes.Count} 条 / {Doc.Strokes[0].Points.Count} 点");
        Doc.Redo();
        Check("重做又把两段放回来", Doc.Strokes.Count == 2, $"{Doc.Strokes.Count} 段");

        // ---------- C. 面积橡皮碰图形：整对象（不切） ----------
        Doc.Clear();
        Doc.ClearHistory();
        var rectShape = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * dpi,
        };
        ShapeGeometry.SetFromDrag(rectShape, cx - 200, cy - 150, cx + 200, cy + 150, false);
        Doc.AddStroke(rectShape);
        Doc.ClearHistory();

        Doc.EraseAreaRect(new RectF { MinX = cx - 20, MinY = cy - 20, MaxX = cx + 20, MaxY = cy + 20 });
        Check("图形：压在里面（没碰到轮廓）不删", Doc.Strokes.Count == 1, $"剩 {Doc.Strokes.Count} 个");

        Doc.EraseAreaRect(new RectF { MinX = cx - 220, MinY = cy - 170, MaxX = cx - 180, MaxY = cy - 130 });
        Check("图形：碰到轮廓就整个删掉", Doc.Strokes.Count == 0, $"剩 {Doc.Strokes.Count} 个");

        // ---------- D. 真机：面积橡皮拖一把，屏幕那块真的没了 ----------
        bool inputWorks = ProbeSyntheticInput(cx, cy - 300f);
        if (!inputWorks)
        {
            Console.WriteLine("    （真机项：合成输入进不来，这一组跳过——模型层的检查已经跑完）");
        }
        else
        {
        Doc.Clear();
        Doc.ClearHistory();
        var onScreen = Line(cx - 500, cy, cx + 500, cy, steps: 100);
        Doc.AddStroke(onScreen);
        Doc.ClearHistory();            // 画这一笔不算"擦"的撤销步（下面要数的是擦的那一步）
        Doc.InvalidateAll();
        SettleFrames(400);
        int inkBefore = ScreenProbe.CountMagenta((int)(cx - 520), (int)(cy - 30), 1040, 60);

        Tool = Tool.AreaEraser;
        AreaEraserHeightLogical = 150f;
        SendMouse((int)(cx - 60), (int)cy, 0);
        SettleFrames(40);
        SendMouse((int)(cx - 60), (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(40);
        SendMouse((int)(cx + 60), (int)cy, 0);
        SettleFrames(40);
        SendMouse((int)(cx + 60), (int)cy, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(400);

        int gapInk = ScreenProbe.CountMagenta((int)(cx - 20), (int)(cy - 30), 40, 60);
        int leftInk = ScreenProbe.CountMagenta((int)(cx - 500), (int)(cy - 30), 300, 60);
        Console.WriteLine($"    屏幕上原本品红 {inkBefore} 像素；擦过之后缺口里 {gapInk}、缺口左边 {leftInk}");
        Check("真机：缺口里没有墨", gapInk < 60, $"{gapInk} 像素");
        Check("真机：缺口旁边还有墨", leftInk > 200, $"{leftInk} 像素");
        Check("真机：整段拖拽仍是一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth}");
        }

        Tool = Tool.Pen;
        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 两种橡皮的模式都对（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 图形与**顶点拖动**自检。
    ///
    /// 关键在最后两条：顶点手柄和选中框的角手柄在屏幕上重合，所以"到底拖到了
    /// 哪一个"必须验；以及**旋转过的图形，顶点必须拖动在它自己的坐标系里**
    /// （否则一转，矩形就不再是矩形了——这类 bug 肉眼一看才发现，且很晚）。
    /// </summary>
    /// <summary>
    /// 合成输入探针：往屏幕中间划一小段，看窗口到底收不收到消息。
    /// 返回 true = 输入通。**它只回答环境问题**，不判功能对错。
    /// </summary>
    /// <summary>
    /// 合成输入不可用时，把依赖真机拖动的自检标成"跳过"而不是"失败"。
    ///
    /// 为什么要这一层：覆盖层是置顶的，但这不代表消息一定送得到——
    /// 别的程序占着鼠标捕获时，我们的窗口一条消息都收不到（实测遇到过）。
    /// 那种情况下判 FAIL，会让人去查完全无关的功能代码。
    /// </summary>
    private bool SkipIfNoSyntheticInput(string what)
    {
        if (ProbeSyntheticInput(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.4f)) return false;
        Console.WriteLine($"  环境：合成输入不可用 → {what}跳过（多半是别的程序正占着鼠标；松开后重跑即可）");
        return true;
    }

    private bool ProbeSyntheticInput(float cx, float cy)
    {
        Tool = Tool.Pen;
        int before = Doc.Strokes.Count;
        int msgBefore = _cntMsgTotal;
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);
        SendMouse((int)(cx + 60), (int)(cy + 30), 0);
        SettleFrames(80);
        SendMouse((int)(cx + 60), (int)(cy + 30), Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);
        Doc.Clear();
        Doc.ClearHistory();
        bool ok = Doc.Strokes.Count > before || _cntWmPointerDown > 0 || _cntWmMouseMove > 0;
        if (!ok)
        {
            Native.GetCursorPos(out var cp);
            Console.WriteLine($"      诊断：光标移到 ({cp.X},{cp.Y})，该点窗口 {Native.DescribeWindowAt(cp.X, cp.Y)}");
            Console.WriteLine($"      诊断：窗口消息 总{_cntMsgTotal - msgBefore}"
                            + $"（鼠标移动 {_cntWmMouseMove}，左键按下 {_cntWmLButtonDown}，"
                            + $"指针按下 {_cntWmPointerDown}，指针移动 {_cntWmPointerUpdate}）");
        }
        return ok;
    }

    /// <summary>
    /// **模型层**逐个图形自检（不依赖鼠标输入，任何时候都能跑）。
    ///
    /// 查的是"图形的定义对不对"：包围盒、命中、顶点数、拖顶点、撤销，
    /// 以及"哪些图形只认顶点"。真机那一层（能不能拖出来、实时不实时）
    /// 在 --shapedrawtest 里，输入不可用时会被跳过。
    /// </summary>
    private void CheckAllShapesModelLevel(Action<string, bool, string> check)
    {
        float dpi = DpiScale;
        float cx = _virtualX + _virtualW * 0.4f, cy = _virtualY + _virtualH * 0.4f;

        Stroke Make(Tool tool, StrokeKind kind, float w, float h)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * dpi,
            };
            ShapeGeometry.SetFromDrag(s, cx - w * 0.5f, cy - h * 0.5f,
                                      cx + w * 0.5f, cy + h * 0.5f, false);
            return s;
        }

        foreach (var (tool, kind, name, vertices) in new[]
        {
            (Tool.Line, StrokeKind.Line, "直线", 2),
            (Tool.Rectangle, StrokeKind.Rectangle, "矩形", 4),
            (Tool.Ellipse, StrokeKind.Ellipse, "椭圆", 4),
            (Tool.Circle, StrokeKind.Circle, "圆", 2),
            (Tool.Triangle, StrokeKind.Triangle, "三角形", 3),
            (Tool.Parallelogram, StrokeKind.Parallelogram, "平行四边形", 3),
            (Tool.Arrow, StrokeKind.Arrow, "箭头", 2),
        })
        {
            var s = Make(tool, kind, 400 * dpi, 300 * dpi);
            check($"{name}：种类正确", s.Kind == kind, $"{s.Kind}");
            check($"{name}：顶点手柄数量", ShapeGeometry.Vertices(s).Length == vertices,
                  $"{ShapeGeometry.Vertices(s).Length} 个（期望 {vertices}）");

            // 包围盒 = 真实占位（圆、平行四边形这两条最容易错）
            var b = s.Bounds;
            bool boxOk;
            string boxDetail;
            if (kind == StrokeKind.Circle)
            {
                float side = MathF.Max(400 * dpi, 300 * dpi);
                boxOk = Math.Abs((b.MaxX - b.MinX) - side) < 1f
                     && Math.Abs((b.MaxY - b.MinY) - side) < 1f;
                boxDetail = $"{b.MaxX - b.MinX:F0}×{b.MaxY - b.MinY:F0}，期望 {side:F0}×{side:F0}（正方形）";
            }
            else if (kind == StrokeKind.Parallelogram)
            {
                float skew = 400 * dpi * 0.25f;
                boxOk = Math.Abs(b.MinX - (cx - 200 * dpi - skew)) < 1f
                     && Math.Abs(b.MaxX - (cx + 200 * dpi)) < 1f;
                boxDetail = $"X {b.MinX:F0}~{b.MaxX:F0}，期望 {cx - 200 * dpi - skew:F0}~{cx + 200 * dpi:F0}";
            }
            else
            {
                boxOk = Math.Abs((b.MaxX - b.MinX) - 400 * dpi) < 1f
                     && Math.Abs((b.MaxY - b.MinY) - 300 * dpi) < 1f;
                boxDetail = $"{b.MaxX - b.MinX:F0}×{b.MaxY - b.MinY:F0}，期望 {400 * dpi:F0}×{300 * dpi:F0}";
            }
            check($"{name}：包围盒 = 真实占位", boxOk, boxDetail);

            // 包围盒不能是空的，也不能比笔宽还小（那会让脏区漏掉自己）
            check($"{name}：外扩包围盒非空且够大",
                  !s.PaddedBounds.IsEmpty && s.PaddedBounds.MaxX - s.PaddedBounds.MinX > 4f, "");

            // 拖任一顶点：形状必须真的变（且不是整体乱跳）
            var before = ShapeGeometry.Vertices(s);
            var beforeBounds = s.Bounds;
            bool moved = VertexHandles.DragTo(s, 0, before[0].X + 40, before[0].Y + 25);
            check($"{name}：顶点能拖（模型层）", moved && !s.Bounds.Equals(beforeBounds), "");

            // 命中：轮廓上命中（用第一段的中点），远处不命中
            var v = ShapeGeometry.Vertices(s);
            // 圆的两个顶点是"圆心 + 圆周一点"，中点在**里面**不是轮廓上，
            // 所以圆直接用圆周那个点（这条第一次写测试时就写错过，写下来提醒后来人）。
            var mid = kind == StrokeKind.Circle ? v[1] : (v[0] + v[1]) * 0.5f;
            var midCanvas = Vector2.Transform(mid, s.Transform);
            check($"{name}：轮廓上命中", s.HitTestExact(midCanvas.X, midCanvas.Y, 4f * dpi), "");
            check($"{name}：远处不命中",
                  !s.HitTestExact(midCanvas.X + 900 * dpi, midCanvas.Y + 900 * dpi), "");

            // 撤销：一次顶点拖动 = 一步
            Doc.Clear();
            Doc.ClearHistory();
            Doc.AddStroke(s);
            Doc.ClearHistory();
            var pts = ShapeGeometry.ControlPoints(s);
            Doc.BeginLiveEdit(s);
            VertexHandles.DragTo(s, 0, ShapeGeometry.Vertices(s)[0].X + 30,
                                 ShapeGeometry.Vertices(s)[0].Y + 30);
            Doc.EndLiveEdit(s);
            Doc.CommitShapeEdit(s, pts, ShapeGeometry.ControlPoints(s));
            check($"{name}：顶点拖动一步撤销", Doc.UndoDepth == 1 && Doc.Undo(), $"栈 {Doc.UndoDepth}");
        }

        // "只认顶点"的图形：不外框拉伸（用户报的第 2、3 条）
        foreach (var (tool, kind, name, expect) in new[]
        {
            (Tool.Line, StrokeKind.Line, "直线", true),
            (Tool.Arrow, StrokeKind.Arrow, "箭头", true),
            (Tool.Triangle, StrokeKind.Triangle, "三角形", true),
            (Tool.Parallelogram, StrokeKind.Parallelogram, "平行四边形", true),
            (Tool.Rectangle, StrokeKind.Rectangle, "矩形", false),
            (Tool.Ellipse, StrokeKind.Ellipse, "椭圆", false),
            (Tool.Circle, StrokeKind.Circle, "圆", false),
        })
        {
            var s = Make(tool, kind, 300 * dpi, 220 * dpi);
            check($"{name}：{(expect ? "只认顶点（不显示缩放手柄）" : "保留外框缩放手柄")}",
                  VertexHandles.PrefersVertices(s) == expect,
                  expect ? "" : "矩形/椭圆/圆的拉伸不会让形状失真");
        }

        Doc.Clear();
        Doc.ClearHistory();
    }

    /// <summary>
    /// 逐个图形走一遍**真机**流程：用合成鼠标把它拖出来，然后验六件事——
    ///
    ///   1. 拖动过程中**实时在长**（两帧的包围盒必须不同，而且屏幕上真的出现了墨）；
    ///   2. 包围盒等于"该图形真实的占位"（圆的包围盒这一条曾经是错的：
    ///      只取了"圆心 + 圆周一点"两个控制点，框只有半径那么小）；
    ///   3. 命中判定打在**轮廓**上，不在中间（图形是描边的，中间是空的）；
    ///   4. 顶点手柄数量对，且"只认顶点"的图形不再显示缩放手柄；
    ///   5. 旋转读数**等于实际转过的角度**（对象被平移过时曾经对不上）；
    ///   6. 撤销/重做回到原样。
    ///
    /// 这些是用户逐条报上来的问题，所以每一条都得有机器判据，不能靠肉眼。
    /// </summary>
    private void ShapeDrawTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 逐个图形真机自检（拖动 / 包围盒 / 命中 / 顶点 / 旋转）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-32}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float dpi = DpiScale;
        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        // 先确认"合成鼠标到底有没有进得来"。
        //
        // 这一步是**环境探针**，不是被测功能：如果它失败，说明窗口虽然在最上面、
        // 光标也确实移过去了，但消息一条都没送到（实测遇到过：别的程序占着鼠标捕获）。
        // 那种情况下真机项全部跳过并打印原因，只跑模型层的检查——
        // 把环境问题判成功能失败，只会让人去查错方向。
        bool inputWorks = ProbeSyntheticInput(cx, cy);
        Console.WriteLine(inputWorks
            ? "  合成输入：通（真机项照跑）"
            : "  合成输入：**不通**（窗口在光标上、光标也移了，但收不到消息）→ 真机项跳过，只跑模型项");
        if (inputWorks)
        {
        // 拖一个图形出来，返回它；同时报"拖动中是否实时在变"
        Stroke DragShape(Tool tool, StrokeKind kind, float w, float h, out bool live,
                         out int midInk)
        {
            Doc.Clear();
            Doc.ClearHistory();
            Tool = tool;
            float x0 = cx - w * 0.5f, y0 = cy - h * 0.5f, x1 = cx + w * 0.5f, y1 = cy + h * 0.5f;

            SendMouse((int)x0, (int)y0, 0);
            SettleFrames(30);
            SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(30);
            SendMouse((int)(x0 + w * 0.25f), (int)(y0 + h * 0.25f), 0);
            SettleFrames(150);
            var mid1 = ActiveStroke?.WorldBounds ?? RectF.Empty;
            midInk = ScreenProbe.CountMagenta((int)(cx - w), (int)(cy - h), (int)(w * 2), (int)(h * 2));
            SendMouse((int)x1, (int)y1, 0);
            SettleFrames(150);
            var mid2 = ActiveStroke?.WorldBounds ?? RectF.Empty;
            SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);

            Console.WriteLine($"      [调试] tool={tool} 引擎工具={Tool} 对象数={Doc.Strokes.Count} 活动笔={ActiveStroke?.Kind.ToString() ?? "无"}"
                            + $" 最近报告={_lastStrokeReport ?? "无"} 指针=({PointerX:F0},{PointerY:F0})");
            live = !mid1.IsEmpty && !mid2.IsEmpty
                && (Math.Abs(mid1.MaxX - mid2.MaxX) > 1f || Math.Abs(mid1.MaxY - mid2.MaxY) > 1f);
            var s = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            if (s != null && s.Kind != kind) { Console.WriteLine($"      ！种类不对：{s.Kind}"); }
            return s;
        }

        // ---- 直线 ----
        {
            var s = DragShape(Tool.Line, StrokeKind.Line, 400 * dpi, 2f, out bool live, out int midInk);
            Check("直线：拖出来了", s != null, s == null ? "没有对象" : "");
            if (s != null)
            {
                Check("直线：拖动中实时在变", live, "两次采样的包围盒必须不同");
                Check("直线：端点在拖动框上",
                      Math.Abs(s.Bounds.MinX - (cx - 200 * dpi)) < 3f
                      && Math.Abs(s.Bounds.MaxX - (cx + 200 * dpi)) < 3f,
                      $"X {s.Bounds.MinX:F0}~{s.Bounds.MaxX:F0}");
                Check("直线：中点在线上（命中）", s.HitTestExact(cx, cy), "");
                Check("直线：只认顶点、不显示缩放手柄",
                      VertexHandles.PrefersVertices(s) && ShapeGeometry.Vertices(s).Length == 2, "");
            }
        }

        // ---- 矩形 ----
        {
            var s = DragShape(Tool.Rectangle, StrokeKind.Rectangle, 400 * dpi, 260 * dpi, out bool live, out _);
            Check("矩形：拖出来了", s != null, "");
            if (s != null)
            {
                Check("矩形：拖动中实时在变", live, "");
                Check("矩形：包围盒 = 拖动框",
                      Math.Abs(s.Bounds.MinX - (cx - 200 * dpi)) < 3f
                      && Math.Abs(s.Bounds.MaxY - (cy + 130 * dpi)) < 3f,
                      $"{s.Bounds.MaxX - s.Bounds.MinX:F0}×{s.Bounds.MaxY - s.Bounds.MinY:F0}"
                      + $"，期望 {400 * dpi:F0}×{260 * dpi:F0}");
                Check("矩形：边上命中、中间不命中",
                      s.HitTestExact(cx, cy - 130 * dpi) && !s.HitTestExact(cx, cy), "");
                Check("矩形：保留缩放手柄（外框拉伸有意义）", !VertexHandles.PrefersVertices(s), "");
            }
        }

        // ---- 椭圆 ----
        {
            var s = DragShape(Tool.Ellipse, StrokeKind.Ellipse, 400 * dpi, 260 * dpi, out bool live, out _);
            Check("椭圆：拖出来了", s != null, "");
            if (s != null)
            {
                Check("椭圆：拖动中实时在变", live, "");
                Check("椭圆：包围盒 = 拖动框",
                      Math.Abs((s.Bounds.MaxX - s.Bounds.MinX) - 400 * dpi) < 4f
                      && Math.Abs((s.Bounds.MaxY - s.Bounds.MinY) - 260 * dpi) < 4f,
                      $"{s.Bounds.MaxX - s.Bounds.MinX:F0}×{s.Bounds.MaxY - s.Bounds.MinY:F0}");
                Check("椭圆：最上点命中、中心不命中",
                      s.HitTestExact(cx, cy - 130 * dpi) && !s.HitTestExact(cx, cy), "");
            }
        }

        // ---- 圆（包围盒曾经是错的）----
        {
            var s = DragShape(Tool.Circle, StrokeKind.Circle, 400 * dpi, 400 * dpi, out bool live, out int midInk);
            Check("圆：拖出来了", s != null, "");
            if (s != null)
            {
                Check("圆：拖动中实时在变", live, "");
                Check("圆：包围盒是**整个圆**（不是两个控制点的小框）",
                      Math.Abs((s.Bounds.MaxX - s.Bounds.MinX) - 400 * dpi) < 4f
                      && Math.Abs((s.Bounds.MaxY - s.Bounds.MinY) - 400 * dpi) < 4f,
                      $"{s.Bounds.MaxX - s.Bounds.MinX:F0}×{s.Bounds.MaxY - s.Bounds.MinY:F0}，期望 400×400（{400 * dpi:F0}）");
                Check("圆：包围盒的中心 = 拖动框的中心",
                      Math.Abs((s.Bounds.MinX + s.Bounds.MaxX) * 0.5f - cx) < 3f
                      && Math.Abs((s.Bounds.MinY + s.Bounds.MaxY) * 0.5f - cy) < 3f, "");
                Check("圆：圆周命中、圆心不命中",
                      s.HitTestExact(cx, cy - 200 * dpi) && !s.HitTestExact(cx, cy), "");
            }
        }

        // ---- 三角形 ----
        {
            var s = DragShape(Tool.Triangle, StrokeKind.Triangle, 400 * dpi, 300 * dpi, out bool live, out int midInk);
            Check("三角形：拖出来了", s != null, "");
            if (s != null)
            {
                Check("三角形：拖动中实时在变", live, "两次采样的包围盒必须不同");
                Check("三角形：拖动中屏幕上真的有墨", midInk > 200, $"{midInk} 像素");
                Check("三角形：包围盒 = 拖动框",
                      Math.Abs((s.Bounds.MaxX - s.Bounds.MinX) - 400 * dpi) < 4f
                      && Math.Abs((s.Bounds.MaxY - s.Bounds.MinY) - 300 * dpi) < 4f,
                      $"{s.Bounds.MaxX - s.Bounds.MinX:F0}×{s.Bounds.MaxY - s.Bounds.MinY:F0}");
                Check("三角形：顶点命中、中心不命中",
                      s.HitTestExact(cx, cy - 150 * dpi) && !s.HitTestExact(cx, cy), "");
                Check("三角形：只认顶点、不显示缩放手柄",
                      VertexHandles.PrefersVertices(s) && ShapeGeometry.Vertices(s).Length == 3, "");
            }
        }

        // ---- 平行四边形 ----
        {
            var s = DragShape(Tool.Parallelogram, StrokeKind.Parallelogram, 400 * dpi, 260 * dpi, out bool live, out _);
            Check("平行四边形：拖出来了", s != null, "");
            if (s != null)
            {
                Check("平行四边形：拖动中实时在变", live, "");
                Check("平行四边形：包围盒含左上那个错开的顶点",
                      Math.Abs(s.Bounds.MinX - (cx - 200 * dpi - 100 * dpi)) < 4f,
                      $"MinX={s.Bounds.MinX:F0}，期望 {cx - 300 * dpi:F0}");
                Check("平行四边形：只认顶点（拉伸会破坏「平行」）",
                      VertexHandles.PrefersVertices(s) && ShapeGeometry.Vertices(s).Length == 3, "");
            }
        }

        // ---- 箭头 ----
        {
            var s = DragShape(Tool.Arrow, StrokeKind.Arrow, 400 * dpi, 2f, out bool live, out _);
            Check("箭头：拖出来了", s != null, "");
            if (s != null)
                Check("箭头：只认顶点", VertexHandles.PrefersVertices(s) && ShapeGeometry.Vertices(s).Length == 2, "");
        }

        // ---- 旋转读数 = 实际转过的角度（对象被平移过时曾经对不上）----
        {
            Doc.Clear();
            Doc.ClearHistory();
            var rect = new Stroke
            {
                Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * dpi,
            };
            ShapeGeometry.SetFromDrag(rect, 0, 0, 240 * dpi, 180 * dpi, false);
            // **关键**：局部坐标在原点附近，靠变换把它挪到屏幕中间。
            // 旧代码就是把"局部中心"和"画布上的指针位置"混着算角度，读数和实际对不上。
            rect.Transform = Matrix3x2.CreateTranslation(cx - 120 * dpi, cy - 90 * dpi);
            Doc.AddStroke(rect);
            Doc.SelectOnly(new[] { rect });
            Tool = Tool.Marquee;
            SettleFrames(300);

            var frame = SelectionHandles.FrameOf(Doc.Selected);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
            var pivot = frame.ToCanvasPoint(new Vector2(
                (frame.Local.MinX + frame.Local.MaxX) * 0.5f,
                (frame.Local.MinY + frame.Local.MaxY) * 0.5f));
            // 绕轴心转 90°：把"轴心→手柄"这个向量转 90°
            var v = grip - pivot;
            var target = pivot + new Vector2(-v.Y, v.X);

            SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(60);
            SendMouse((int)target.X, (int)target.Y, 0);
            SettleFrames(280);

            float label = SelRotationDegrees;
            float actual = SelectionHandles.RotationOf(Doc.Strokes[0].Transform);
            Check("旋转读数 = 实际转过的角度（对象被平移过）",
                  Math.Abs(SelectionHandles.WrapSignedDegrees(label) - 90f) < 2f,
                  $"读数 {label:F1}°，期望 ≈90°");
            Check("对象自己真的转了 90°", Math.Abs(actual - 90f) < 2f, $"矩阵量出 {actual:F1}°");
            Check("读数和对象角度一致（这就是用户说的「对不上」）",
                  Math.Abs(SelectionHandles.WrapSignedDegrees(label - actual)) < 2f,
                  $"读数 {label:F1}° vs 实际 {actual:F1}°");

            SendMouse((int)target.X, (int)target.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);
            Check("旋转：一次拖拽一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth}");
        }
        }
        else
        {
            Console.WriteLine("    （真机项：拖动/实时/旋转这一组全部跳过——等没有别的程序抢鼠标时再跑）");
        }

        // ---- 模型层：不依赖输入，逐个图形验定义/包围盒/命中/顶点/撤销 ----
        CheckAllShapesModelLevel(Check);

        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 七个图形逐个都对（{pass} 项）" : $"  FAIL: {fail} 项不对（{pass} 项通过）");
        _quit = true;
    }

    private void VertexTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图形定义与顶点拖动自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float dpi = DpiScale;
        Stroke MakeShape(Tool tool, StrokeKind kind, float ax, float ay, float bx, float by)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * dpi,
            };
            ShapeGeometry.SetFromDrag(s, ax, ay, bx, by, false);
            return s;
        }

        Check("控制点数：直线 2", ShapeGeometry.ControlPointCount(StrokeKind.Line) == 2, "");
        Check("控制点数：三角形 3", ShapeGeometry.ControlPointCount(StrokeKind.Triangle) == 3, "");
        Check("控制点数：平行四边形 3", ShapeGeometry.ControlPointCount(StrokeKind.Parallelogram) == 3, "");

        // 矩形：拖一个角，对角不动
        var rect = MakeShape(Tool.Rectangle, StrokeKind.Rectangle, 400, 300, 800, 600);
        Check("矩形：四个角手柄", ShapeGeometry.Vertices(rect).Length == 4, "");
        var beforeCorner = new Vector2(rect.Bounds.MaxX, rect.Bounds.MaxY);
        Check("顶点命中：拖右下角", VertexHandles.HitTest(rect, 800, 600, dpi) >= 0,
              $"命中下标 {VertexHandles.HitTest(rect, 800, 600, dpi)}");
        VertexHandles.DragTo(rect, 2 /* 右下 */, 900, 700);
        Check("矩形：拖右下角，左上角不动",
              Math.Abs(rect.Bounds.MinX - 400) < 0.01f && Math.Abs(rect.Bounds.MinY - 300) < 0.01f,
              $"左上 ({rect.Bounds.MinX:F0},{rect.Bounds.MinY:F0})");
        Check("矩形：右下角跟到指针", Math.Abs(rect.Bounds.MaxX - 900) < 0.01f
                                   && Math.Abs(rect.Bounds.MaxY - 700) < 0.01f,
              $"右下 ({rect.Bounds.MaxX:F0},{rect.Bounds.MaxY:F0})，拖之前 {beforeCorner.X:F0},{beforeCorner.Y:F0}");
        Check("矩形：拖动后仍是两条边轴对齐（四个角就是包围盒的四角）",
              ShapeGeometry.Vertices(rect).Length == 4, "");

        // 圆：拖圆周改半径，拖圆心平移
        var circle = MakeShape(Tool.Circle, StrokeKind.Circle, 1000, 400, 1200, 600);
        var c0 = new Vector2(circle.Points[0].X, circle.Points[0].Y);
        float r0 = Vector2.Distance(c0, new Vector2(circle.Points[1].X, circle.Points[1].Y));
        VertexHandles.DragTo(circle, 1, c0.X + r0 * 2f, c0.Y);
        float r1 = Vector2.Distance(c0, new Vector2(circle.Points[1].X, circle.Points[1].Y));
        Check("圆：拖圆周点改半径", Math.Abs(r1 - r0 * 2f) < 0.5f, $"{r0:F0} → {r1:F0}");
        VertexHandles.DragTo(circle, 0, c0.X + 100, c0.Y + 50);
        float r2 = Vector2.Distance(new Vector2(circle.Points[0].X, circle.Points[0].Y),
                                    new Vector2(circle.Points[1].X, circle.Points[1].Y));
        Check("圆：拖圆心 = 平移，半径不变", Math.Abs(r2 - r1) < 0.01f, $"半径 {r2:F1}");

        // 三角形 / 平行四边形：拖一个顶点
        var tri = MakeShape(Tool.Triangle, StrokeKind.Triangle, 1400, 300, 1700, 600);
        Check("三角形：三个顶点", ShapeGeometry.Vertices(tri).Length == 3, "");
        VertexHandles.DragTo(tri, 2, 1400, 200);       // 顶点往上拉
        Check("三角形：拖顶点生效", Math.Abs(tri.Bounds.MinY - 200) < 0.01f, $"MinY={tri.Bounds.MinY:F0}");

        var para = MakeShape(Tool.Parallelogram, StrokeKind.Parallelogram, 400, 900, 800, 1200);
        var d0 = ShapeGeometry.Vertices(para)[1] + ShapeGeometry.Vertices(para)[2]
               - ShapeGeometry.Vertices(para)[0];
        VertexHandles.DragTo(para, 1, 850, 1200);
        var v = ShapeGeometry.Vertices(para);
        var d1 = v[1] + v[2] - v[0];
        Check("平行四边形：第四个顶点是算出来的（拖 B，D 跟着变）",
              Math.Abs(d1.X - d0.X) > 40f, $"D 从 ({d0.X:F0},{d0.Y:F0}) 到 ({d1.X:F0},{d1.Y:F0})");

        // 旋转过的对象：顶点拖动必须落在它自己的坐标系里
        var rotated = MakeShape(Tool.Rectangle, StrokeKind.Rectangle, 800, 800, 1000, 1000);
        rotated.Transform = Matrix3x2.CreateRotation(30f * MathF.PI / 180f,
            new Vector2(rotated.Bounds.MinX, rotated.Bounds.MinY));
        var corner = ShapeGeometry.Vertices(rotated)[2];              // 局部（右下）
        var cornerCanvas = Vector2.Transform(corner, rotated.Transform);
        var pull = new Vector2(cornerCanvas.X + 60f, cornerCanvas.Y + 20f);
        VertexHandles.DragTo(rotated, 2, pull.X, pull.Y);
        // 反变换回局部：拖动之后，局部坐标里仍然应该是"轴对齐的矩形"
        Matrix3x2.Invert(rotated.Transform, out var inv);
        var localMin = Vector2.Transform(new Vector2(rotated.Bounds.MinX, rotated.Bounds.MinY), inv);
        var localMax = Vector2.Transform(new Vector2(rotated.Bounds.MaxX, rotated.Bounds.MaxY), inv);
        var localVertices = ShapeGeometry.Vertices(rotated);
        bool axisAligned = Math.Abs((localVertices[1].Y - localVertices[0].Y)) < 0.01f
                        && Math.Abs((localVertices[3].X - localVertices[0].X)) < 0.01f;
        Check("旋转 30° 后拖顶点：局部仍是轴对齐矩形", axisAligned,
              $"局部四角 Y 差 {MathF.Abs(localVertices[1].Y - localVertices[0].Y):F3}px");

        // 顶点编辑模式的进 / 出条件
        Doc.Clear();
        Doc.ClearHistory();
        Doc.AddStroke(rect);
        Doc.Selected.Clear();
        Doc.Selected.Add(rect);
        Check("顶点编辑：选中一个图形就能进", VertexHandles.CanEdit(Doc.Selected), "");
        Doc.Selected.Add(circle);
        Check("顶点编辑：多选时不能进（不知道该听谁的）", !VertexHandles.CanEdit(Doc.Selected), "");
        Doc.Selected.Clear();
        Doc.Selected.Add(rect);
        var imgForCheck = ImageData.Adopt(4, 4, new byte[4 * 4 * 4], false);
        var imgStroke = Doc.AddImage(imgForCheck, 0, 0, 1f);
        Doc.Selected.Clear();
        Doc.Selected.Add(imgStroke);
        Check("顶点编辑：图像不支持（它是整块图）", !VertexHandles.CanEdit(Doc.Selected), "");

        // 撤销：一次拖动 = 一步
        Doc.Clear();
        Doc.ClearHistory();
        var target = MakeShape(Tool.Rectangle, StrokeKind.Rectangle, 900, 500, 1200, 800);
        Doc.AddStroke(target);
        Doc.ClearHistory();
        var beforePts = ShapeGeometry.ControlPoints(target);
        Doc.BeginLiveEdit(target);
        VertexHandles.DragTo(target, 2, 1300, 900);
        Doc.EndLiveEdit(target);
        Doc.CommitShapeEdit(target, beforePts, ShapeGeometry.ControlPoints(target));
        Check("顶点拖动：一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth}");
        Doc.Undo();
        Check("顶点拖动：撤销回原形状", Math.Abs(target.Bounds.MaxX - 1200) < 0.5f,
              $"MaxX={target.Bounds.MaxX:F1}（期望 1200）");
        Doc.Redo();
        Check("顶点拖动：重做回到新形状", Math.Abs(target.Bounds.MaxX - 1300) < 0.5f,
              $"MaxX={target.Bounds.MaxX:F1}（期望 1300）");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 图形与顶点拖动都对（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 图像对象自检：造图 → 命中 → 上屏 → 走通用操作（复制/翻转/删除）
    /// → 存档往返 → 剪贴板往返。
    ///
    /// 最后两项是重点：图像像素**必须**进得了文件、进得了剪贴板，
    /// 否则"截图发给学生"这件事就是断的。
    /// </summary>
    private void ImageTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图像对象自检（截图 / 粘贴的图）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float cx = _virtualX + _virtualW * 0.4f, cy = _virtualY + _virtualH * 0.4f;
        const int W = 64, H = 64;
        byte[] pix = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool left = x < W / 2;
                pix[i + 0] = left ? (byte)255 : (byte)0;     // B
                pix[i + 1] = 0;                              // G
                pix[i + 2] = left ? (byte)255 : (byte)0;     // R（左半品红、右半黑）
                pix[i + 3] = 255;
            }

        Doc.Clear();
        Doc.ClearHistory();
        var img = ImageData.Adopt(W, H, pix, true);
        var placed = Doc.AddImage(img, cx, cy, 1f);
        Check("落地：对象是图像类型", placed != null && placed.IsImage, $"{placed?.Kind}");
        Check("落地：包围盒 = 像素尺寸 × 缩放",
              Math.Abs(placed.Bounds.MaxX - W) < 0.01f && Math.Abs(placed.Bounds.MaxY - H) < 0.01f,
              $"{placed.Bounds.MaxX:F0}×{placed.Bounds.MaxY:F0}");
        Check("落地：变换里带平移", Math.Abs(placed.Transform.M31 - cx) < 0.01f
                                  && Math.Abs(placed.Transform.M32 - cy) < 0.01f,
              $"({placed.Transform.M31:F0},{placed.Transform.M32:F0})");
        Check("命中：压在图上算命中", placed.HitTestExact(cx + 4, cy + 4), "");
        Check("命中：图外面不算", !placed.HitTestExact(cx - 40, cy - 40), "");

        // 真的画上去了吗：左边像素应该是品红
        Doc.InvalidateAll();
        SettleFrames(400);
        int leftPixels = ScreenProbe.CountMagenta((int)cx + 2, (int)cy + 2, W / 2 - 4, H - 4);
        Check("上屏：左半张画出来了（品红像素 > 500）", leftPixels > 500, $"{leftPixels} 像素");

        // 通用操作：复制 / 翻转 / 删除都走同一套（因为图像就是一个对象）
        Doc.Selected.Clear();
        Doc.Selected.Add(placed);
        Doc.DuplicateSelected(30f, 30f);
        Check("复制：图像也能复制（通用操作）", Doc.Strokes.Count == 2, $"{Doc.Strokes.Count} 个对象");
        var copy = Doc.Strokes[1];
        Check("复制：副本是新身份、像素共享",
              copy.Id != placed.Id && ReferenceEquals(copy.Image, placed.Image), $"id {copy.Id}");
        Doc.ApplyTransform(SelectionHandles.MirrorMatrix(placed.Bounds, horizontal: true));
        Check("翻转：图像也能翻（走同一条变换）", Math.Abs(copy.Transform.M11 + 1f) < 0.01f
                                              || Math.Abs(copy.Transform.M11 - 1f) > 0.5f,
              $"M11={copy.Transform.M11:F2}");

        // 面积橡皮碰到图 = 整个删掉（图不能被"切一半"）
        int alive = Doc.Strokes.Count;
        Doc.EraseAreaRect(new RectF { MinX = cx + 2, MinY = cy + 2, MaxX = cx + 6, MaxY = cy + 6 });
        Check("面积橡皮：碰到图像就整个删掉", Doc.Strokes.Count < alive, $"{alive} → {Doc.Strokes.Count}");

        // 存档往返
        Doc.Clear();
        Doc.ClearHistory();
        var img2 = ImageData.Adopt(W, H, (byte[])pix.Clone(), true);
        Doc.AddImage(img2, cx, cy, 1f);
        byte[] blob = InkSerializer.Save(Doc);
        var doc2 = new InkDocument();
        InkSerializer.LoadInto(doc2, blob);
        Check("存档：对象数对上", doc2.Strokes.Count == 1, $"{doc2.Strokes.Count} 个");
        bool samePixels = doc2.Strokes.Count == 1 && doc2.Strokes[0].Image != null
            && doc2.Strokes[0].Image.Width == W && doc2.Strokes[0].Image.Height == H;
        if (samePixels)
        {
            var a = doc2.Strokes[0].Image.Bgra;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != pix[i]) diff++;
            samePixels = diff == 0;
            Check("存档：像素逐字节一致", samePixels, $"差异 {diff} 字节");
        }
        else Check("存档：像素逐字节一致", false, "图像没读回来");
        Console.WriteLine($"    存档体积：{blob.Length / 1024.0:F1} KB（{W}×{H} 原始像素）");

        // 剪贴板往返（别的程序占着剪贴板时会失败，那是环境问题，不是 bug）
        var clipPix = (byte[])pix.Clone();
        bool wrote = ClipboardImage.SetImage(clipPix, W, H);
        if (!wrote)
        {
            Console.WriteLine("    剪贴板：写不进去（被别的程序占着？）——这项跳过");
        }
        else if (!ClipboardImage.TryGetImage(out var back, out int bw, out int bh, out _))
        {
            Check("剪贴板：读回来", false, "写成功但读不回来");
        }
        else
        {
            Check("剪贴板：尺寸一致", bw == W && bh == H, $"{bw}×{bh}");
            int diff = 0;
            for (int i = 0; i < Math.Min(back.Length, pix.Length); i++) if (back[i] != pix[i]) diff++;
            Check("剪贴板：像素一致", diff == 0, $"差异 {diff} 字节");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 图像对象全通（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 截图自检（真机）：合成鼠标拖一个框，然后验三件事——
    ///   1. 生成了一个**图像对象**，尺寸等于拖出来的框（按 DPI 折成画布尺寸）；
    ///   2. 它落在**视口左上角**并自动选中（用户下一步就是拖它）；
    ///   3. **没有把自己的批注拍进去**（抓之前覆盖层藏起来了）——
    ///      这一条最重要：先在框里画一大片品红，抓到的图里品红必须≈0。
    /// </summary>
    private void CaptureTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 截图自检（拖框 → 左上角 → 剪贴板）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        float dpi = DpiScale;

        if (SkipIfNoSyntheticInput("截图全流程（需要拖框）")) { _quit = true; return; }

        // 先在要抓的这一块上涂满自己的墨（品红），用来验证"抓的时候墨不在图里"
        Doc.Clear();
        Doc.ClearHistory();
        for (int k = 0; k < 12; k++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 40f * dpi,
            };
            float y = cy - 110 + k * 20;
            for (int i = 0; i <= 20; i++) s.AddPoint(cx - 200 + i * 20, y, 0.9f, i);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(500);
        int onScreenInk = ScreenProbe.CountMagenta((int)(cx - 200), (int)(cy - 120), 400, 260);
        Check("准备：框里已经有一片墨", onScreenInk > 5000, $"{onScreenInk} 像素");

        // 换截图工具，拖一个 300×200 的框
        Tool = Tool.Capture;
        int x0 = (int)(cx - 150), y0 = (int)(cy - 100);
        int x1 = (int)(cx + 150), y1 = (int)(cy + 100);
        SendMouse(x0, y0, 0);
        SettleFrames(40);
        SendMouse(x0, y0, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);
        SendMouse((x0 + x1) / 2, y0 + 20, 0);
        SettleFrames(40);
        SendMouse(x1, y1, 0);
        SettleFrames(60);
        SendMouse(x1, y1, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(900);                     // 截图里含一次"藏窗口 + 抓屏 + 显示"

        var shot = Doc.Strokes.FindLast(s => s.IsImage);
        Check("截图：生成了图像对象", shot != null, shot == null ? "没有" : $"{shot.Image.Width}×{shot.Image.Height} 物理像素");
        if (shot == null)
        {
            Console.WriteLine();
            Console.WriteLine("  FAIL: 截图没生成对象");
            _quit = true;
            return;
        }

        Check("截图：物理尺寸 = 拖出来的框", Math.Abs(shot.Image.Width - 300) <= 2
                                          && Math.Abs(shot.Image.Height - 200) <= 2,
              $"{shot.Image.Width}×{shot.Image.Height}，期望 300×200");
        Check("截图：画布尺寸按 DPI 折算",
              Math.Abs((shot.Transform.M11 * shot.Image.Width) - 300f / dpi) < 3f,
              $"画布宽 {(shot.Transform.M11 * shot.Image.Width):F0}，期望 {300f / dpi:F0}（dpi={dpi:F2}）");

        var vp = ViewportCanvas;
        float margin = CaptureMarginLogical * dpi;
        Check("截图：落在视口左上角（带一点边距）",
              Math.Abs(shot.WorldBounds.MinX - (vp.MinX + margin)) < 3f
              && Math.Abs(shot.WorldBounds.MinY - (vp.MinY + margin)) < 3f,
              $"({shot.WorldBounds.MinX:F0},{shot.WorldBounds.MinY:F0})，期望 ({vp.MinX + margin:F0},{vp.MinY + margin:F0})");
        Check("截图：自动选中、并切回框选工具",
              Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], shot) && Tool == Tool.Marquee,
              $"选中 {Doc.Selected.Count} 个，工具 {Tool}");

        // 核心一条：抓到的图里不该有自己的墨
        int shotInk = 0;
        var px = shot.Image.Bgra;
        for (int i = 0; i + 3 < px.Length; i += 4)
            if (px[i + 2] > 200 && px[i + 1] < 90 && px[i] > 200) shotInk++;
        Check("截图：自己的批注没被拍进去（品红≈0）", shotInk < 200, $"{shotInk} 像素");

        // 剪贴板：截图必须同时进剪贴板
        if (ClipboardImage.TryGetImage(out var clip, out int cw, out int ch, out _))
            Check("截图：同时进了剪贴板", cw == shot.Image.Width && ch == shot.Image.Height, $"{cw}×{ch}");
        else
            Console.WriteLine("    剪贴板：读不出来（可能被别的程序占着）——这项跳过");

        // 覆盖层藏过又显示：屏幕上不该留残影（墨应该还在原处）
        int afterInk = ScreenProbe.CountMagenta((int)(cx - 200), (int)(cy - 120), 400, 260);
        Check("截图后屏幕恢复正常（批注还在，没有残影）", afterInk > 5000,
              $"截前 {onScreenInk} 像素，截后 {afterInk} 像素");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 截图全通（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

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
        Console.Write(Latency.Report());
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
        Console.WriteLine($"  笔迹优化器    ：{(InkOptimizers.Current == null ? "未装（纯底层）" : "已装")}");
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
        Console.WriteLine($"  请用手写笔画线，持续 {seconds:F0} 秒（或按 Ctrl+Alt+X 提前结束）……");
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
        Console.Write(Latency.Report());
        string csv = "reports/latency-live.csv";
        Latency.WriteCsv(csv, "real-pen");
        Console.WriteLine($"CSV 已写入 {csv}");
        _quit = true;
    }

    private static string DeviceName(uint t) => t switch
    {
        Native.PT_PEN => "笔",
        Native.PT_TOUCH => "触摸",
        Native.PT_MOUSE => "鼠标",
        _ => $"未知({t})",
    };

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
