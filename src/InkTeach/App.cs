using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// 开发期宿主：持有引擎，外加一整套自动化测试/基准工具。
/// 继承只是为了让这些工具直接读引擎内部状态（产品代码请用组合：new InkEngine()）。
/// </summary>
internal sealed class App : InkEngine.InkEngine
{
    private IntPtr _clickTargetHwnd;
    private string _clickLogFile;

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

        // --board：把白板打开（**不透明**底色）。
        //
        // 自检/摆样模式下面板一盖、桌面一露，截出来的图里全是别人的窗口，
        // 看图时反而要费劲把我们的墨从背景里挑出来。开了白板之后覆盖层是
        // 不透明的——截屏得到的就只有我们自己的白板 + 笔迹 + 落点，
        // 桌面上的东西一概进不来（用户提的"截图只截这个软件"就是这个开关）。
        // 注意：产品里的"截图"（Ctrl+Alt+S）**不能**这么做，它要的正是下层内容。
        if (args.Contains("--board"))
        {
            BoardOn = true;
            Console.WriteLine("白板: 已打开（截图只有我们自己的内容，不含桌面）");
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
        else if (mode == "--restest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var s4) ? s4 : 5000;
            ResolutionTest(n);
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
        else if (mode == "--captest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CapTest();
        }
        else if (mode == "--selfcross")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelfCrossTest();
        }
        else if (mode == "--holetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HoleTest();
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
        else if (mode == "--seltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelTest();
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
        Console.WriteLine("  --imagetest         图像对象（上屏 / 复制翻转 / 存档 / 剪贴板）");
        Console.WriteLine("  --capturetest       截图（拖框 → 左上角 → 剪贴板，且不拍进自己的批注）");
        Console.WriteLine("  --cursorshow <笔|荧光笔|激光笔|橡皮> [宽]  落点摆样");
        Console.WriteLine("  --widthtest         笔迹粗细/压力");
        Console.WriteLine("  --ghosttest         残影检测");
        Console.WriteLine("  --trailtest         委托墨迹轨迹对照");
        Console.WriteLine("  --latbench <csv>    延时实测（分场景 + 分位数 + 稳定性）");
        Console.WriteLine("  --penlive [秒]      真笔延时实测（挂上手写笔写一会儿，出报告）");
        Console.WriteLine("  --longrun [秒]      长时运行内存/CPU");
        Console.WriteLine("  --restest [n]       分辨率对照（离屏层）");
        Console.WriteLine("  --cornertest        折角填充自检（45/90/135° 会不会掉色）");
        Console.WriteLine("  --captest           笔迹两端自检（单击＝圆点，宽笔＝圆头收尾）");
        Console.WriteLine("  --seltest           框选自检（框到的就该选中）");
        Console.WriteLine("  --selfcross         自交叠色自检（一笔自己穿过自己，颜色不能变深）");
        Console.WriteLine("  --board             打开白板（不透明底色）：自检截图里只有我们自己的内容");
        Console.WriteLine("  --rotatetest        旋转度数读数与吸附（含真机拖动 + 上屏核对）");
        Console.WriteLine("  --screenshot <路径> [x y w h]  截屏工具");
        Console.WriteLine("  --clicktarget <日志>           穿透测试用的下层窗口");
        Console.WriteLine();
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
            Width = 8f * DpiScale,
        };
        float x = VirtualScreen.MinX + 500, y = VirtualScreen.MinY + 400;
        for (int i = 0; i <= 200; i++) s.AddPoint(x + i * 3f, y + i * 3f, 0.5f, i);

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

        // 一条**很宽**的荧光笔：用来看选中框有没有把墨圈住。
        // 框按中心线算的话，这条 64 像素宽的带子上会压着四条边——一眼就能看出来。
        var wide = new Stroke
        {
            Tool = Tool.Highlighter, Kind = StrokeKind.Freehand,
            Color = HighlighterCurrent, Width = 32f * DpiScale,
        };
        wide.AddPoint(cx - 420, top + 690, 0.5f, 0);
        wide.AddPoint(cx + 420, top + 690, 0.5f, 1);
        Doc.AddStroke(wide);

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
            _ => Tool.Pen,
        };
        if (widthLogical > 0f)
        {
            if (Tool == Tool.Highlighter) HighlighterWidthLogical = widthLogical;
            else if (Tool == Tool.Laser) LaserWidthLogical = widthLogical;
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
            measured = SelectionHandles.NormalizeDegrees(measured);
            var pivotAfter = Vector2.Transform(center, m);
            Check($"拖动 {target,5:F0}°：读数与矩阵一致",
                  Math.Abs(measured - deg) < 0.2f
                  && Math.Abs(pivotAfter.X - center.X) < 0.05f
                  && Math.Abs(pivotAfter.Y - center.Y) < 0.05f,
                  $"读数 {deg:F1}°，矩阵量出 {measured:F1}°，轴心偏移 "
                  + $"{Vector2.Distance(pivotAfter, center):F3}px");
        }

        Check("标签文案不留 -0°", SelectionHandles.FormatDegrees(-0.4f) == "0°",
              $"{-0.4f:F1}° → {SelectionHandles.FormatDegrees(-0.4f)}");
        Check("标签文案取整", SelectionHandles.FormatDegrees(89.6f) == "90°",
              $"89.6° → {SelectionHandles.FormatDegrees(89.6f)}");
        Check("标签文案带符号", SelectionHandles.FormatDegrees(-45f) == "-45°",
              $"-45° → {SelectionHandles.FormatDegrees(-45f)}");

        // ================= B. 真机层 =================
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
        int snappedPixels = AccentCount();
        Check("吸住时标签上屏（强调色填充）", snappedPixels > 1500, $"{snappedPixels} 像素");

        // 再拖到 43°（容差外 → 不吸）
        var to43 = GripAt(43f);
        SendMouse((int)to43.X, (int)to43.Y, 0);
        SettleFrames(260);
        Check("43° 保持自由", !SelRotationSnapped && Math.Abs(SelRotationDegrees - 43f) < 1f,
              $"{SelRotationDegrees:F1}°，snapped={SelRotationSnapped}");
        int freePixels = AccentCount();
        Check("没吸住时标签是白底（强调色像素少）", freePixels < 800, $"{freePixels} 像素");

        // 松手：标签必须消失，且一次拖拽只留一条撤销记录
        SendMouse((int)to43.X, (int)to43.Y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(320);
        Check("松手后不再标记旋转", !SelRotating, $"SelRotating={SelRotating}");
        int afterRelease = AccentCount();
        Check("松手后标签从屏幕上消失", afterRelease < 800, $"{afterRelease} 像素（拖动前 {beforeDraw}）");

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
            Width = 6f,
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
            Width = 12f,
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
                   && a.Width == b.Width
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

        // ---- 老文件兼容：v2（每条笔画多一个"笔锋预设"字节）必须还能打开 ----
        // 手写美化删掉之后，那个字节不再写、不再读；**但读老文件时必须读掉它**，
        // 否则后面的变换、点数据全部错位——用户存了一学期的批注会全部打不开。
        // 这个字节就在"宽度"和"变换"之间，写错了表现是"能打开但东西是乱的"，
        // 比打不开更难查，所以专门造一个 v2 文件来验。
        var v2 = new MemoryStream();
        using (var w = new BinaryWriter(v2, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(new byte[] { (byte)'I', (byte)'N', (byte)'K', (byte)'B' });
            w.Write(2);                     // 版本 2：每条笔画带预设字节
            w.Write(0);                     // flags
            w.Write(1);                     // 一块画布
            w.Write((byte)0);               // Blank
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write("");
            w.Write(1);                     // 一个对象
            w.Write(42);                    // id
            w.Write((byte)Tool.Pen);
            w.Write((byte)StrokeKind.Freehand);
            w.Write(0.95f); w.Write(0.18f); w.Write(0.18f); w.Write(1f);
            w.Write(7.5f);                  // 宽度
            w.Write((byte)1);               // ← 已废弃的"笔锋预设"字节（v2 才有）
            w.Write(1f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(100f); w.Write(200f);
            w.Write(2);                     // 两个点
            w.Write(0.0);                   // 第一个点的时间戳
            w.Write(10f); w.Write(20f); w.Write(0.5f); w.Write(0f);
            w.Write(30f); w.Write(40f); w.Write(0.5f); w.Write(8f);
            w.Write((byte)0);               // 无图像
        }
        var old = new InkDocument();
        bool oldOk = true; string oldNote = "";
        try { InkSerializer.LoadInto(old, v2.ToArray()); }
        catch (Exception ex) { oldOk = false; oldNote = ex.GetType().Name; }
        var os = old.Strokes.Count > 0 ? old.Strokes[0] : null;
        Check("v2 老文件能打开", oldOk && os != null && os.Points.Count == 2, oldNote);
        Check("v2 老文件字段不错位",
            os != null && Math.Abs(os.Width - 7.5f) < 1e-4f
            && Math.Abs(os.Points[1].X - 30f) < 1e-4f && Math.Abs(os.Points[1].Y - 40f) < 1e-4f
            && Math.Abs(os.Transform.M31 - 100f) < 1e-4f,
            os == null ? "没读到对象"
                       : $"宽 {os.Width}、第二个点 ({os.Points[1].X},{os.Points[1].Y})、"
                         + $"平移 ({os.Transform.M31},{os.Transform.M32})");

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

    /// <summary>
    /// 折角填充自检：**拐角处不能掉色**（写"L""V""7""∠"这类折笔时，填充在
    /// 自交处被挖空的那个老 bug）。
    ///
    /// 判据在 2026-09-14 重写过，因为**旧判据自己有问题**：它拿"拐角窗口的墨量"
    /// 比"直段窗口的墨量"，要求前者 ≥ 后者的 95%。可是拐角窗口里本来就有 3/4 的
    /// 地方没墨（两个臂分别从窗口的左边和下边出去），所以这条自检从写下来那天起
    /// 就是红的——一条永远红的自检等于没有自检，它把真正的信号淹掉了。
    ///
    /// 现在的判据直接对着**要防的 bug**：沿笔迹中心线取一串采样点（含拐角本身），
    /// 每个点在填充里都必须有墨（FillContainsPoint）。拐角被挖空 → 拐角那个点
    /// 就是空的 → 报红。另加一条"上屏兜底"（白板 + 拐角窗口数黑像素），
    /// 因为"几何对但没画到屏幕上"也是一种可能。
    /// </summary>
    /// <summary>
    /// 自交叠色自检：**一笔自己穿过自己的地方，颜色不能变深**。
    ///
    /// 为什么单独拎出来验：我们画的是"一条闭合轮廓 + 一次填充"，理论上自交处只会让
    /// Winding 计数变成 2，仍然只合成一次；但中间有一道工序可能破坏这个前提——
    /// **几何细分缓存**（`CreateFilledGeometryRealization`）如果按三角形逐个合成，
    /// 重叠区就会被合成两次。用户报的"画角时内部变深"正是这个形状，所以要专门盯。
    ///
    /// 判据：在同一笔的"自交重叠区"和"普通区"各取一个像素，颜色必须相同
    /// （半透明荧光笔最敏感——叠两次一眼就能看出来）。
    /// </summary>
    private void SelfCrossTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 自交叠色自检（一笔自己穿过自己，颜色不能变深）===");

        bool boardBefore = BoardOn;
        var boardColorBefore = BoardColor;
        BoardOn = true;
        BoardColor = new Color4(0.98f, 0.98f, 0.98f, 1f);
        ViewOffsetY = 0f;
        foreach (var win in _windows) { win.ViewOffsetX = 0f; win.ViewOffsetY = 0f; }
        Doc.Clear();
        Doc.ClearHistory();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        float w = 40f;                 // 物理像素
        float cx = _virtualX + 500f, cy = _virtualY + 500f;

        // 造一条"自己穿过自己"的笔迹：先往右，再绕回来从第一段上方穿过去。
        // 交点定在 (cx+200, cy)：那里会有两段墨重叠。
        Stroke MakeLoop(float dx)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = HighlighterCurrent, Width = w,
            };
            var pts = new List<Vector2>
            {
                new(cx + dx, cy), new(cx + dx + 120, cy), new(cx + dx + 240, cy + 60),
                new(cx + dx + 240, cy + 220), new(cx + dx + 60, cy + 260),
                new(cx + dx - 20, cy + 120), new(cx + dx + 200, cy),   // ← 穿过第一段
                new(cx + dx + 330, cy - 150),
            };
            // 点之间插值，模拟真实采样密度
            for (int i = 0; i + 1 < pts.Count; i++)
                for (int k = 0; k < 20; k++)
                {
                    float t = k / 20f;
                    s.AddPoint(pts[i].X + (pts[i + 1].X - pts[i].X) * t,
                               pts[i].Y + (pts[i + 1].Y - pts[i].Y) * t, 0.5f, i * 20 + k);
                }
            s.AddPoint(pts[^1].X, pts[^1].Y, 0.5f, 999);
            return s;
        }

        Doc.AddStroke(MakeLoop(0f));
        Doc.InvalidateAll();
        SettleFrames(700);

        (int r, int g, int b) Avg(byte[] buf, int ww)
        {
            if (buf.Length < ww * ww * 4) return (0, 0, 0);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i < buf.Length; i += 4) { b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; n++; }
            return ((int)(r / n), (int)(g / n), (int)(b / n));
        }
        // 交叠区（两段墨重叠）与普通区（只有一段墨）各取一个小方块的平均色
        void Probe(string label, float dx)
        {
            var co = Avg(ScreenProbe.CaptureRegion((int)(cx + dx + 200) - 8, (int)cy - 8, 17, 17), 17);
            var cs = Avg(ScreenProbe.CaptureRegion((int)(cx + dx + 60) - 8, (int)cy - 8, 17, 17), 17);
            int diff = Math.Abs(co.r - cs.r) + Math.Abs(co.g - cs.g) + Math.Abs(co.b - cs.b);
            Check($"自交处没有叠色（{label}）", diff <= 6,
                  $"交叠处 ({co.r},{co.g},{co.b}) vs 普通处 ({cs.r},{cs.g},{cs.b})，差 {diff}");
        }
        Probe("一笔自交", 0f);

        string shot = Path.Combine("reports", "自交叠色.bmp");
        ScreenProbe.SaveBmp(shot, (int)(cx - 120), (int)(cy - 260), 700, 700);
        Console.WriteLine($"  （已导出 {shot}：一笔自交，看交叉处有没有更深）");
        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();

        BoardOn = boardBefore;
        BoardColor = boardColorBefore;
        Doc.Clear();
        Doc.InvalidateAll();
        _quit = true;
    }


    /// <summary>
    /// **找洞自检**：笔是不透明的，所以"墨里出现深色"只可能是**墨真的缺了一块**
    /// （露出后面的背景），不是叠色。缺口的成因是轮廓自交时**绕向翻转**，
    /// 在 Winding 规则下正负抵消 → 那一块变成"外面"。
    ///
    /// 判据不靠肉眼：把笔画所在的区域截下来，从边界做一次**背景的连通填充**，
    /// 凡是"填不到、又被墨围住"的背景像素就是洞。这比"看谁变深"硬得多，
    /// 也不会把正常凹处（比如 V 字两臂之间）误判成洞。
    ///
    /// 造四种最容易出洞的笔迹：急折（内角 10°/20°）、自交（λ）、
    /// 以及"长斜线 + 顶端折返 + 穿过去"这种一笔画出来的交叉。
    /// </summary>
    private void HoleTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 找洞自检（不透明笔迹里不能露出背景）===");

        bool boardBefore = BoardOn;
        var boardColorBefore = BoardColor;
        BoardOn = true;
        BoardColor = new Color4(0.98f, 0.98f, 0.98f, 1f);
        ViewOffsetY = 0f;
        foreach (var win in _windows) { win.ViewOffsetX = 0f; win.ViewOffsetY = 0f; }
        Doc.Clear();
        Doc.ClearHistory();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        float w = 48f;                       // 粗笔：问题在粗笔上才显形
        float arm = 220f;
        var ink = new Color4(0.95f, 0.18f, 0.18f, 1f);   // 不透明红

        // 一笔画出来的形状；返回包围盒（画布坐标）
        RectF Make(string kind, float cx, float cy)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = ink, Width = w,
            };
            var pts = new List<Vector2>();
            if (kind == "折170")
            {
                float rad = 170f * MathF.PI / 180f;
                var d = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + arm * i / 30f, cy));
                for (int i = 1; i <= 30; i++)
                    pts.Add(new(cx + arm + d.X * arm * i / 30f, cy + d.Y * arm * i / 30f));
            }
            else if (kind == "折160")
            {
                float rad = 160f * MathF.PI / 180f;
                var d = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + arm * i / 30f, cy));
                for (int i = 1; i <= 30; i++)
                    pts.Add(new(cx + arm + d.X * arm * i / 30f, cy + d.Y * arm * i / 30f));
            }
            else if (kind == "自交λ")
            {
                // 上、折、再穿回来：经典的"λ"（自己的尾巴穿过自己的身子）
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + i * 6f, cy + 180f - i * 6f));
                for (int i = 1; i <= 30; i++) pts.Add(new(cx + 180f + i * 6f, cy + i * 6f));
            }
            else // "∧ 加穿线"：用户的形状（长斜线 + 顶端折返 + 一笔穿过去）
            {
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + i * 7f, cy + 200f - i * 7f));
                for (int i = 1; i <= 20; i++) pts.Add(new(cx + 210f + i * 3f, cy - 10f + i * 9f));
                for (int i = 1; i <= 40; i++) pts.Add(new(cx + 270f - i * 8f, cy + 170f - i * 9f));
            }
            if (kind == "原路折返")
            {
                // 画出去、再从原路画回来（同一笔）：两段完全重合
                for (int i = 0; i <= 40; i++) pts.Add(new(cx + i * 6f, cy));
                for (int i = 39; i >= 0; i--) pts.Add(new(cx + i * 6f, cy));
            }
            else if (kind == "浅角自交")
            {
                // 两条腿以很小的夹角交叉（"X" 的浅角版本）
                for (int i = 0; i <= 60; i++) pts.Add(new(cx + i * 7f, cy + 200f - i * 4f));
                for (int i = 1; i <= 60; i++) pts.Add(new(cx + 420f - i * 7f, cy - 40f + i * 5f));
            }

            // 点之间插值，贴近真实采样密度（5px 一个点）
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                int steps = Math.Max(1, (int)(Vector2.Distance(a, b) / 5f));
                for (int k = 0; k < steps; k++)
                {
                    float t = k / (float)steps;
                    s.AddPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, 0.5f, i * 100 + k);
                }
            }
            s.AddPoint(pts[^1].X, pts[^1].Y, 0.5f, 9999);
            Doc.AddStroke(s);
            return s.PaddedBounds;
        }

        var kinds = new (string kind, string name, float cx)[]
        {
            ("折170", "急折·内角10°", _virtualX + 260f),
            ("折160", "急折·内角20°", _virtualX + 820f),
            ("自交λ", "自交（λ）", _virtualX + 1420f),
            ("∧穿线", "∧ + 穿线", _virtualX + 2000f),
            ("原路折返", "原路折返", _virtualX + 2600f),
            ("浅角自交", "浅角自交", _virtualX + 900f),
        };
        // 3 列 × 2 行摆开（现在只有一种画法了：D2D 原生描边）
        var boxes = new List<(string name, RectF box)>();
        float[] colX = { _virtualX + 260f, _virtualX + 900f, _virtualX + 1560f };
        for (int i = 0; i < kinds.Length; i++)
        {
            int row = i < 3 ? 0 : 1;
            float cy = _virtualY + 380f + row * 460f;
            boxes.Add((kinds[i].name, Make(kinds[i].kind, colX[i % 3], cy)));
        }
        Doc.InvalidateAll();
        SettleFrames(700);

        // 逐个形状找洞：把区域截下来，从边界对"背景色"做连通填充，
        // 填不到又被墨围住的背景像素 = 洞。
        foreach (var (name, box) in boxes)
        {
            int x0 = (int)box.MinX - 6, y0 = (int)box.MinY - 6;
            int ww = (int)(box.MaxX - box.MinX) + 12, hh = (int)(box.MaxY - box.MinY) + 12;
            var px = ScreenProbe.CaptureRegion(x0, y0, ww, hh);
            if (px.Length < ww * hh * 4) { Check(name, false, "截屏失败"); continue; }

            bool IsBackground(int x, int y)
            {
                int i = (y * ww + x) * 4;
                int b = px[i], g = px[i + 1], r = px[i + 2];
                return r > 225 && g > 225 && b > 225;      // 白板底色
            }

            var seen = new bool[ww * hh];
            var stack = new Stack<int>();
            void Push(int x, int y)
            {
                if (x < 0 || y < 0 || x >= ww || y >= hh) return;
                int k = y * ww + x;
                if (seen[k] || !IsBackground(x, y)) return;
                seen[k] = true; stack.Push(k);
            }
            for (int x = 0; x < ww; x++) { Push(x, 0); Push(x, hh - 1); }
            for (int y = 0; y < hh; y++) { Push(0, y); Push(ww - 1, y); }
            while (stack.Count > 0)
            {
                int k = stack.Pop(); int x = k % ww, y = k / ww;
                Push(x - 1, y); Push(x + 1, y); Push(x, y - 1); Push(x, y + 1);
            }

            int holes = 0; int hx = 0, hy = 0;
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < ww; x++)
                    if (!seen[y * ww + x] && IsBackground(x, y))
                    { holes++; if (holes == 1) { hx = x0 + x; hy = y0 + y; } }

            Check(name, holes == 0,
                  holes == 0 ? "没有露底" : $"露出背景 {holes} 像素（第一处在 {hx},{hy}）");
        }

        string shot = Path.Combine("reports", "洞检.bmp");
        ScreenProbe.SaveBmp(shot, (int)(_virtualX + 100f), (int)(_virtualY + 200f), 2400, 1000);
        Console.WriteLine($"  （已导出 {shot}）");
        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();

        BoardOn = boardBefore;
        BoardColor = boardColorBefore;
        Doc.Clear();
        Doc.InvalidateAll();
        _quit = true;
    }

    private void CornerTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 折角自检（拐角会不会缺墨）===");

        float w = 20f * DpiScale;            // 粗笔，问题在粗笔上最明显
        float cx = VirtualScreen.MinX + 700, cy = VirtualScreen.MinY + 600;
        float arm = 400f;

        // 相机归零：下面的兜底判据要数"屏幕上"的像素，靠的是"画布坐标 = 屏幕坐标"
        // 这个前提。别的用例滚动过之后相机不为 0，不归零就会量到错的地方。
        ViewOffsetY = 0f;
        foreach (var win in _windows) { win.ViewOffsetX = 0f; win.ViewOffsetY = 0f; }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        // 白板：兜底那一处要数屏幕上的黑像素，背景必须是干净的。
        bool boardBefore = BoardOn;
        var boardColorBefore = BoardColor;
        BoardOn = true;
        BoardColor = new Color4(0.98f, 0.98f, 0.98f, 1f);

        // 造一条"先水平走、再按 turnDeg 折过去"的笔迹，中心线上的采样点一并返回。
        (Stroke s, List<Vector2> pts) MakeElbow(float turnDeg)
        {
            var stroke = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1), Width = w,
            };
            var samples = new List<Vector2>();
            float rad = turnDeg * MathF.PI / 180f;
            var dir = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
            for (int i = 0; i <= 30; i++)
            {
                var p = new Vector2(cx + arm * i / 30f, cy);
                stroke.AddPoint(p.X, p.Y, 0.5f, i);
                samples.Add(p);
            }
            for (int i = 1; i <= 30; i++)
            {
                var p = new Vector2(cx + arm + dir.X * arm * i / 30f, cy + dir.Y * arm * i / 30f);
                stroke.AddPoint(p.X, p.Y, 0.5f, 30 + i);
                samples.Add(p);
            }
            return (stroke, samples);
        }

        foreach (float turnDeg in new[] { 45f, 90f, 135f, 170f })
        {
            var (s, pts) = MakeElbow(turnDeg);

            // 判据：中心线上的采样点必须都落在墨里。几何现在是"中心线"，
            // 所以用描边判定（和画出来用的是同一条几何、同一个描边样式）。
            var geo = s.BuildGeometry(Gfx.D2DFactory);
            int dry = 0;
            Vector2 dryAt = default;
            foreach (var p in pts)
            {
                if (Vector2.Distance(p, new Vector2(cx + arm, cy)) > arm * 0.6f) continue;
                if (!geo.StrokeContainsPoint(p, MathF.Max(1f, s.Width), Gfx.Round))
                { dry++; dryAt = p; }
            }

            Check($"{turnDeg:F0}° 折角", dry == 0,
                  dry == 0 ? "中心线上每个采样点都有墨"
                           : $"有 {dry} 个采样点是空的（例如 "
                             + $"({dryAt.X - cx - arm:F0},{dryAt.Y - cy:F0})）");
        }

        // ---- 墨不能超出脏区（残影防线）----
        // 脏区（PaddedBounds）算小了不会报错、只会留残影，所以必须自己盯着。
        // 现在的墨 = 中心线 ± 半个笔宽（圆头圆角），用 D2D 的"加宽边界"对账。
        {
            var (s, _) = MakeElbow(90f);
            var geo = s.BuildGeometry(Gfx.D2DFactory);
            var wb = geo.GetWidenedBounds(MathF.Max(1f, s.Width), Gfx.Round, 0.25f);
            var pb = s.PaddedBounds;
            bool inside = wb.Left >= pb.MinX - 0.5f && wb.Top >= pb.MinY - 0.5f
                       && wb.Right <= pb.MaxX + 0.5f && wb.Bottom <= pb.MaxY + 0.5f;
            Check("墨不超出脏区（残影防线）", inside,
                  $"墨 {wb.Left:F0},{wb.Top:F0}→{wb.Right:F0},{wb.Bottom:F0}；"
                  + $"脏区 {pb.MinX:F0},{pb.MinY:F0}→{pb.MaxX:F0},{pb.MaxY:F0}");
        }

        // ---- 兜底：90° 折角在屏幕上真的画出来了（白板 + 拐角窗口）----
        {
            var (s, pts) = MakeElbow(90f);
            var ideal = new Vector2(cx + arm, cy);

            Doc.Clear();
            Doc.InvalidateAll();
            Doc.AddStroke(s);
            SettleFrames(500);

            var onPathPts = new List<Vector2>();
            foreach (var p in pts)
                if (Vector2.Distance(p, ideal) <= w) onPathPts.Add(p);
            int small = 5, darkest = int.MaxValue;
            foreach (var p in onPathPts)
            {
                int h = ScreenProbe.CountNear((int)(p.X - small * 0.5f), (int)(p.Y - small * 0.5f),
                                              small, small, 0, 0, 0, 60);
                if (h < darkest) darkest = h;
            }
            float cover = onPathPts.Count > 0 ? darkest / (float)(small * small) : 0f;
            Check("拐角上屏（白板）", onPathPts.Count > 0 && cover >= 0.8f,
                  $"拐角附近 {onPathPts.Count} 个中心线点，最暗的一个 {cover:P0} 是黑的");

            string shot = Path.Combine("reports", "拐角-放大.bmp");
            ScreenProbe.SaveBmp(shot, (int)(cx + arm - 200f), (int)(cy - 200f), 400, 400);
            Console.WriteLine($"  （拐角已导出：{shot}，白底黑字）");
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        BoardOn = boardBefore;
        BoardColor = boardColorBefore;
        Doc.Clear();
        Doc.InvalidateAll();
        _quit = true;
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

        double MeasureLaser(out int frames)
        {
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

        double laserRibbon = MeasureLaser(out int framesRibbon);
        P($"【激光笔】1 万笔画之上叠加激光轨迹（每帧记录耗时）");
        P($"    画激光                 : {laserRibbon:F2} ms   （{framesRibbon} 帧）");
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
        CheckBool("荧光笔 · 落点是圆盘（直径 = 笔宽）",
            DrawnCursor == ToolCursorShape.Disc,
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
        Tool = Tool.Highlighter; CheckBool("鼠标书写中 · 圆盘跟着走", DrawnCursor == ToolCursorShape.Disc, $"{DrawnCursor}");
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
        float thinDisc = CursorRingTrueRadius;
        float thinDiscDirty = DrawnCursorRadius;
        HighlighterWidthLogical = 32f;
        CheckBool("荧光笔 · 圆盘半径就是半个笔宽",
            Math.Abs(thinDisc - 8f * DpiScale * 0.5f) < 0.01f
            && Math.Abs(CursorRingTrueRadius - 32f * DpiScale * 0.5f) < 0.01f,
            $"8 → 半径 {thinDisc:F1}px，32 → {CursorRingTrueRadius:F1}px");
        CheckBool("荧光笔 · 脏区盖得住圆盘（含描边）",
            DrawnCursorRadius > CursorRingTrueRadius,
            $"脏区 {DrawnCursorRadius:F0}px > 圆盘 {CursorRingTrueRadius:F0}px");
        CheckBool("荧光笔 · 脏区跟着荧光笔宽变",
            DrawnCursorRadius > thinDiscDirty,
            $"{thinDiscDirty:F0}px → {DrawnCursorRadius:F0}px（脏区半径）");
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

    /// <summary>
    /// 框选自检：**框到的就该选中**。
    ///
    /// 判据（这是产品语义，不是实现细节）：
    ///   ① 框住全部墨 → 全部选中（老师最常做的动作）；
    ///   ② 框只压住笔身的一部分 → 这条也要选中（"碰到就选中"），
    ///      否则屏幕上永远选不全：笔迹只要有一头在屏幕外（或框拖不到的地方），
    ///      要求"整条都在框里"就永远选不上，用户看到的就是"框了但没全选中"；
    ///   ③ 框在空白处 → 一个都不选；
    ///   ④ 图像对象（截图/粘贴）和旋转过的对象，同一套规则。
    /// </summary>
    private void SelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 框选自检（框到的就该选中）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-32} {detail}");
        }

        float x0 = _virtualX + 300f, y0 = _virtualY + 300f;
        var ink = new Color4(1f, 0f, 1f, 1f);

        Stroke Line(float x, float y, float dx, float dy, float w)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
            s.AddPoint(x, y, 0.5f, NowMs);
            s.AddPoint(x + dx, y + dy, 0.5f, NowMs);
            return s;
        }

        Doc.Clear();
        Doc.ClearHistory();

        var a = Line(x0, y0, 200f, 0f, 6f * DpiScale);            // 横线
        var b = Line(x0, y0 + 200f, 0f, 160f, 40f * DpiScale);    // 竖的粗笔（笔身很宽）
        var c = Line(x0 + 300f, y0, 300f, 300f, 8f * DpiScale);   // 斜线
        var d = Line(x0 - 500f, y0 + 500f, 3000f, 0f, 10f * DpiScale); // 长横线（一头在屏幕外）
        foreach (var s in new[] { a, b, c, d }) Doc.AddStroke(s);

        // 旋转 30° 的一条：包围盒是旋转后的 AABB
        var rot = Line(x0 + 200f, y0 + 500f, 200f, 0f, 10f * DpiScale);
        rot.Transform = Matrix3x2.CreateRotation(MathF.PI / 6f) * Matrix3x2.CreateTranslation(0, 0);
        Doc.AddStroke(rot);

        int n = Doc.Strokes.Count;
        Doc.Selected.Clear();

        // ① 框住全部
        var all = RectF.Empty;
        foreach (var s in Doc.Strokes) all.Add(s.PaddedBounds);
        Doc.ApplyMarquee(all.Inflate(20f));
        Check("框住全部 → 全部选中", Doc.Selected.Count == n, $"{Doc.Selected.Count}/{n} 条");

        // ② 只压住粗竖笔的笔身（中心线在框外）
        Doc.Selected.Clear();
        var body = RectF.Empty;
        body.Add(b.Points[0].X - 30f, b.Points[0].Y + 40f);
        body.Add(b.Points[0].X + 30f, b.Points[0].Y + 100f);
        Doc.ApplyMarquee(body);
        Check("框压住笔身的一角 → 这一条也选中",
            Doc.Selected.Contains(b), $"选中 {Doc.Selected.Count} 条（期望含这条粗竖笔）");

        // ③ 框住屏幕外那条长横线可见的一段：也该选中
        Doc.Selected.Clear();
        var edge = RectF.Empty;
        edge.Add(_virtualX + 10f, d.Points[0].Y - 4f);
        edge.Add(_virtualX + 200f, d.Points[0].Y + 4f);
        Doc.ApplyMarquee(edge);
        Check("框住长线露出来的一段 → 整条选中",
            Doc.Selected.Contains(d), $"选中 {Doc.Selected.Count} 条");

        // ④ 空白处 → 一个都不选
        Doc.Selected.Clear();
        var empty = RectF.Empty;
        empty.Add(_virtualX + 40f, _virtualY + 40f);
        empty.Add(_virtualX + 120f, _virtualY + 120f);
        Doc.ApplyMarquee(empty);
        Check("空白处 → 一个都不选", Doc.Selected.Count == 0, $"{Doc.Selected.Count} 条");

        // ⑤ 精确框一条：不能顺手把别的也选进来
        Doc.Selected.Clear();
        var onlyA = RectF.Empty;
        onlyA.Add(a.Points[0].X - 10f, a.Points[0].Y - 10f);
        onlyA.Add(a.Points[1].X + 10f, a.Points[1].Y + 10f);
        Doc.ApplyMarquee(onlyA);
        Check("框住横线 → 只有它", Doc.Selected.Count == 1 && Doc.Selected[0] == a,
              $"选中 {Doc.Selected.Count} 条");

        // ⑥ 旋转过的对象：框住它的包围盒就该选中
        Doc.Selected.Clear();
        Doc.ApplyMarquee(rot.PaddedBounds.Inflate(6f));
        Check("旋转对象 → 框住包围盒即选中", Doc.Selected.Contains(rot),
              $"选中 {Doc.Selected.Count} 条");

        // ⑦ 截图/粘贴来的图像对象走同一套规则
        var img = ImageData.Adopt(40, 30, new byte[40 * 30 * 4], false);
        if (img != null)
        {
            var placed = Doc.AddImage(img, _virtualX + 300f, _virtualY + 700f, 1f);
            Doc.Selected.Clear();
            Doc.ApplyMarquee(placed.PaddedBounds.Inflate(6f));
            Check("图像对象 → 同样能被框到", Doc.Selected.Contains(placed),
                  $"选中 {Doc.Selected.Count} 条");
        }

        // ⑧ 宽墨迹：选中框必须把**墨**圈住（不是只圈中心线）
        //    64 像素宽的荧光笔，屏幕上是一条 64 像素宽的带子；框要是按中心线算，
        //    四条边全压在墨里面——用户实测就是嫌这个。
        {
            var wide = new Stroke
            {
                Tool = Tool.Highlighter, Color = HighlighterCurrent,
                Width = 32f * DpiScale,                 // 最粗的一档荧光笔
            };
            wide.AddPoint(x0 + 400f, y0 + 800f, 0.5f, NowMs);
            wide.AddPoint(x0 + 900f, y0 + 800f, 0.5f, NowMs);
            Doc.AddStroke(wide);
            Doc.SelectOnly(new[] { wide });

            var frame = SelectionHandles.FrameOf(Doc.Selected);
            var inkBox = wide.InkBounds;                // 无变换：局部 = 画布
            float paint = wide.Width * 0.5f;            // 鼠标压感 0.5 → 半宽 = 半个笔宽
            bool contains = frame.Local.MinX <= inkBox.MinX + 0.01f
                         && frame.Local.MinY <= inkBox.MinY + 0.01f
                         && frame.Local.MaxX >= inkBox.MaxX - 0.01f
                         && frame.Local.MaxY >= inkBox.MaxY - 0.01f;
            float slack = MathF.Max(
                MathF.Max(frame.Local.MinX - inkBox.MinX, frame.Local.MinY - inkBox.MinY),
                MathF.Max(inkBox.MaxX - frame.Local.MaxX, inkBox.MaxY - frame.Local.MaxY));
            Check("宽笔选中框把墨圈住", contains,
                  contains ? $"框 {frame.Local.MaxX - frame.Local.MinX:F0}×"
                             + $"{frame.Local.MaxY - frame.Local.MinY:F0}px，"
                             + $"墨是中心线外扩 {paint:F0}px"
                           : $"框 {frame.Local.MinX:F0},{frame.Local.MinY:F0}→"
                             + $"{frame.Local.MaxX:F0},{frame.Local.MaxY:F0}；"
                             + $"墨 {inkBox.MinX:F0},{inkBox.MinY:F0}→"
                             + $"{inkBox.MaxX:F0},{inkBox.MaxY:F0}");
            Check("宽笔选中框不虚胖", slack <= 2f, $"比墨大出 {slack:F1}px（要 ≤ 2）");
            Doc.Selected.Clear();
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 笔迹两端自检：**圆头端帽**。
    ///
    /// 这一层以前把带子两侧的端点直接用直线连起来，于是两端是被切平的方块：
    /// 宽笔拖出来像一根长条尺子，单击一下也不是圆点、而是一小段扁条。
    /// 现在两端各补一段半圆（半径 = 端点处的半宽），收尾自然收成半圆。
    ///
    /// 判定分两层，缺一层都说明不了问题：
    ///   ① **几何精确判定**：直接问 Direct2D"端帽里那个点有没有被墨盖住"
    ///      （FillContainsPoint，和命中测试用的是同一条几何）；
    ///   ② **屏幕上数像素**：端帽那一小块到底有多少墨在真正的屏幕上。
    /// 只看①会漏掉"几何对了但没画出来"，只看②会分不清形状错了还是没上屏。
    /// 最后再导一张图，人能一眼看形状。
    /// </summary>
    private void CapTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 笔迹两端自检（单击＝圆点，宽笔＝圆头收尾）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        float w = 40f * DpiScale;                       // 宽笔：端帽形状在粗笔上最显眼
        float r = w * 0.5f;
        float x0 = _virtualX + 380f, y0 = _virtualY + 320f;
        var ink = new Color4(1f, 0f, 1f, 1f);           // 品红：便于在屏幕上数像素

        // ---------- ① 单击：一个采样点 → 一个圆 ----------
        var click = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        click.AddPoint(x0, y0, 0.5f, NowMs);
        var gClick = click.BuildGeometry(Gfx.D2DFactory);
        // 单点笔迹的几何本身就是那个圆 → 用填充判定
        Check("单击 · 圆心有墨", gClick.FillContainsPoint(new Vector2(x0, y0)), "");
        Check("单击 · 半径内 0.95r 有墨",
            gClick.FillContainsPoint(new Vector2(x0 + r * 0.95f, y0)), $"r = {r:F0}px");
        Check("单击 · 半径外 1.1r 没墨",
            !gClick.FillContainsPoint(new Vector2(x0 + r * 1.1f, y0)), "");

        // ---------- ② 短拖（2 个采样点）：两端都是半圆 ----------
        var drag = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        float dx = x0 + 240f, dy = y0, len = 140f;
        drag.AddPoint(dx, dy, 0.5f, NowMs);
        drag.AddPoint(dx + len, dy, 0.5f, NowMs);
        var gDrag = drag.BuildGeometry(Gfx.D2DFactory);
        // 两点以上的笔迹，几何是**中心线**，墨是 D2D 描出来的 →
        // 判定要用描边判定（和画出来用的是同一条几何、同一个描边样式）。
        bool InkedAt(ID2D1Geometry geo, Stroke s, float x, float y)
            => geo.StrokeContainsPoint(new Vector2(x, y), MathF.Max(1f, s.Width), Gfx.Round);

        // 端点"正前方"只有圆头端帽盖得到：平头的话这里一定是空的。
        Check("短拖 · 收笔正前方 0.8r 有墨（圆头）",
            InkedAt(gDrag, drag, dx + len + r * 0.8f, dy), "");
        Check("短拖 · 起笔正后方 0.8r 有墨（圆头）",
            InkedAt(gDrag, drag, dx - r * 0.8f, dy), "");
        Check("短拖 · 端帽外 1.1r 没墨（半圆，不是超出去的方块）",
            !InkedAt(gDrag, drag, dx + len + r * 1.1f, dy), "");
        Check("短拖 · 侧面外 1.1r 没墨",
            !InkedAt(gDrag, drag, dx + len * 0.5f, dy + r * 1.1f), "");

        // ---------- ③ 起笔连报重复坐标：方向不能翻车 ----------
        // 鼠标刚按下的一瞬间经常连报好几个相同坐标。方向要取"前后各一个真的
        // 不一样的点"，否则会被强行当成水平，起笔处的轮廓就歪了。
        var dup = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        float ex = x0, ey = y0 + 200f;
        dup.AddPoint(ex, ey, 0.5f, NowMs);
        dup.AddPoint(ex, ey, 0.5f, NowMs);
        dup.AddPoint(ex, ey, 0.5f, NowMs);
        dup.AddPoint(ex + 200f, ey + 200f, 0.5f, NowMs);   // 斜着走
        var gDup = dup.BuildGeometry(Gfx.D2DFactory);
        var d = Vector2.Normalize(new Vector2(1f, 1f));    // 真实前进方向
        Check("重复点起笔 · 正后方 0.95r 有墨（端帽朝反方向鼓）",
            InkedAt(gDup, dup, ex - d.X * r * 0.95f, ey - d.Y * r * 0.95f), "");
        Check("重复点起笔 · 正后方 1.1r 没墨",
            !InkedAt(gDup, dup, ex - d.X * r * 1.1f, ey - d.Y * r * 1.1f), "");

        // ---------- ④ 真的画到屏幕上再数一遍像素 ----------
        Doc.Clear();
        Doc.AddStroke(click);
        Doc.AddStroke(drag);
        Doc.AddStroke(dup);

        float hlW = 32f * DpiScale;
        var hl = new Stroke
        {
            Tool = Tool.Highlighter, Color = HighlighterCurrent, Width = hlW,
        };
        hl.AddPoint(x0, y0 + 400f, 0.5f, NowMs);
        hl.AddPoint(x0 + 420f, y0 + 400f, 0.5f, NowMs);
        Doc.AddStroke(hl);

        // 竖着拖一笔：端帽是"斜着/竖着"的时候也得成立（横线和竖线走的
        // 是同一条几何路径，但画到屏幕上的方向完全不同）。
        var vert = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        float vx = x0 + 560f, vy = y0 - 40f;
        vert.AddPoint(vx, vy, 0.5f, NowMs);
        vert.AddPoint(vx, vy + 320f, 0.5f, NowMs);
        Doc.AddStroke(vert);
        Doc.InvalidateAll();
        SettleFrames(700);

        int dotIn = ScreenProbe.CountMagenta((int)(x0 - r), (int)(y0 - r), (int)(2 * r), (int)(2 * r));
        int dotCorner = ScreenProbe.CountMagenta((int)(x0 + r * 0.75f), (int)(y0 + r * 0.75f),
                                                 (int)(r * 0.6f), (int)(r * 0.6f));
        float expect = MathF.PI * r * r;
        Check("单击上屏 · 圆点墨量对得上面积",
            Math.Abs(dotIn - expect) < expect * 0.12f,
            $"实测 {dotIn} / 理论 {expect:F0}（{dotIn / expect:P0}）");
        Check("单击上屏 · 圆的外角没有墨（不是方块）", dotCorner == 0, $"角上 {dotCorner} 像素");

        int capPx = ScreenProbe.CountMagenta((int)(dx + len + r * 0.1f), (int)(dy - r * 0.3f),
                                            (int)(r * 0.8f), (int)(r * 0.6f));
        int capFlat = ScreenProbe.CountMagenta((int)(dx + len + r * 1.15f), (int)(dy - r * 0.3f),
                                              (int)(r * 0.4f), (int)(r * 0.6f));
        Check("宽笔上屏 · 端帽那一段有墨", capPx > 200, $"端帽区 {capPx} 像素");
        Check("宽笔上屏 · 端帽外是空的", capFlat == 0, $"外侧 {capFlat} 像素");

        int vertBody = ScreenProbe.CountMagenta((int)(vx - r * 0.9f), (int)(vy + 60f),
                                                (int)(r * 1.8f), (int)(200f * DpiScale));
        int vertCap = ScreenProbe.CountMagenta((int)(vx - r * 0.3f), (int)(vy + 320f + r * 0.1f),
                                               (int)(r * 0.6f), (int)(r * 0.8f));
        Check("竖笔上屏 · 笔身有墨", vertBody > 20000, $"笔身区 {vertBody} 像素");
        Check("竖笔上屏 · 收笔端帽有墨", vertCap > 200, $"端帽区 {vertCap} 像素");

        string shot = Path.Combine("reports", "端帽-圆头.bmp");
        bool saved = ScreenProbe.SaveBmp(shot, (int)(x0 - 120f), (int)(y0 - 160f), 900, 700);
        Console.WriteLine(saved ? $"  （已导出 {shot}：单击圆点 / 短拖 / 斜拖 / 荧光笔 / 竖拖）"
                                : "  导出失败");

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
        int bad = 0;
        foreach (float wLogical in WidthPresets)
        {
            float wPhys = wLogical * DpiScale;
            float y = _virtualY + 140 + i * 150;
            // The wavy path is ~1480 px of x plus the wiggle.
            float pathLen = 1560f;
            // 墨是"中心线 + 等宽描边"：宽度就是名义笔宽（压感不再影响粗细）。
            float expected = pathLen * wPhys;
            int actual = ScreenProbe.CountMagenta((int)(_virtualX + 190), (int)(y - 90), 1430, 180);
            Console.WriteLine($"  {wLogical,8:F1} | {wPhys,8:F0} | {expected,8:F0} | {actual,8} | {(actual / expected):F2}");
            // 判据：填充带子的墨量要落在理论值的合理区间里。明显偏小 = 自交处
            // 被挖空了（洞），明显偏大 = 重复填充。这条以前只有数字没有结论，
            // 于是"填充出洞"这种事必须靠人看图，现在它自己会红。
            float ratio = actual / expected;
            if (ratio < 0.75f || ratio > 1.15f) bad++;
            i++;
        }

        Console.WriteLine();
        Console.WriteLine(bad == 0
            ? "  PASS: 各档粗细的墨量都在理论值的 0.75~1.15 倍之间（填充没有出洞）"
            : $"  FAIL: 有 {bad} 档墨量偏离理论值（<0.75 或 >1.15）");
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
        // 委托墨迹轨迹**只对真笔（PT_PEN）生效**（鼠标下系统根本不接这条通道）。
        // 这个用例是用合成鼠标事件跑的，所以鼠标下量不到轨迹是预期结果，
        // 不该报成失败——原来这里一直红着，害得"全套自检"里有一条永远修不掉。
        bool mouseRun = LastPointerType != Native.PT_PEN;
        Console.WriteLine(ok ? "  PASS: 委托墨迹轨迹生效"
                        : mouseRun
                            ? "  SKIP: 当前是鼠标（轨迹只对真笔生效），这一项不适用"
                            : "  FAIL: 手上是真笔却没看到系统画的轨迹");
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
        Doc.Clear();
        Doc.ClearHistory();
        return Doc.Strokes.Count > before;
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
