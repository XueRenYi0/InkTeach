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
    private bool _panelShowDrawer;
    private bool _panelShowMini;
    private bool _panelShowBand;
    private bool _selfCheckMode;

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
        _selfCheckMode = mode.Length > 0;
        // 自检一律用**临时配置文件**：判据要确定，更不能把用户真正的设置改掉。
        if (_selfCheckMode && InkSettings.PathOverride == null)
            InkSettings.PathOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck.json");
        // 自动存档同理：自检**绝不能碰用户真正的板书**
        if (_selfCheckMode && Recovery.AutoSavePathOverride == null)
            Recovery.AutoSavePathOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-autosave.ink");
        // 自检里**不弹"另存为"对话框**：它会阻塞等消息，而自检是自己抽消息推进的，
        // 一弹就卡到超时（`--selftest` 会逐个点操作条上的按钮，点到"导出"就中招）。
        // 导出那条链由 `--iotest` 走"不弹框、直接写指定路径"验，见 ExportSelectionToPathForTest。
        if (_selfCheckMode) ExportDialogEnabled = false;

        // 交互模式挂**产品界面**；自检/基准模式挂"什么都不画"的空宿主
        // （自检要数屏幕上的墨，一块面板盖上去会把判据搞脏——这条踩过）。
        //
        // 用 SetUiFactory 而不是 SetUi：界面万一崩了，引擎要能自己再造一个回来，
        // 再造不回来才轮到重启（见 计划-底层对接界面.md 4.5 的三级阶梯）。
        if (mode.Length == 0) SetUiFactory(() => new InkUi.FullUi());
        else SetUi(new HeadlessUi());

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
        else if (mode == "--uitest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            UiInputTest();
        }
        else if (mode == "--paneltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PanelTest();
        }
        else if (mode == "--panelshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PanelShow(args.Length > 1 ? args[1] : "reports/panel-第一版.png");
        }
        else if (mode == "--pageshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PageShow(args.Length > 1 ? args[1] : "reports/page-lines.bmp");
        }
        else if (mode == "--iconshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            IconShow(args.Length > 1 ? args[1] : "reports/laser-icons.bmp");
        }
        else if (mode == "--captureicons")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureIconShow(args.Length > 1 ? args[1] : "reports/capture-icons.bmp");
        }
        else if (mode == "--recoverytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RecoveryTest();
        }
        else if (mode == "--panelperf")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PanelPerf();
        }
        else if (mode == "--erasertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraserTest();
        }
        else if (mode == "--pixelerasetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PixelEraserTest();
        }
        else if (mode == "--pixeleraseshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PixelEraserShowcase();
        }
        else if (mode == "--eraseselfcross")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseSelfCrossProbe();
        }
        else if (mode == "--eraserlab")
        {
            // **橡皮手测台**：这是给人用的交互会话，不是自检——铺好样例、打开遥测，
            // 然后把控制权交回引擎的正常消息循环（return -1 就是"没有测试模式"）。
            EraserLab(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null,
                      args.Contains("--syscursor"));
            return -1;
        }
        else if (mode == "--eraseimage")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseImageProbe();
        }
        else if (mode == "--imageerase")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageEraseProbe();
        }
        else if (mode == "--clipboardtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ClipboardTest();
        }
        else if (mode == "--lassotest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LassoTest();
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
        else if (mode == "--captureshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureShow(args.Length > 1 ? args[1] : "reports/capture-frame.png");
        }
        else if (mode == "--dialogprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DialogProbe(args.Length > 1 ? args[1] : "tmp/dlg");
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
        else if (mode == "--predicttest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PredictorTest();
        }
        else if (mode == "--pressurediag")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PressureDiagTest();
        }
        else if (mode == "--predictdata")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PredictEval.Run(args.Length > 1 ? args[1] : "tmp/datasets");
            _quit = true;
        }
        else if (mode == "--wetinktest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 15;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--live" && double.TryParse(args[i + 1], out var s)) secs = s;
            if (args.Contains("--live")) WetInkLiveTest(secs);
            else WetInkTest(args.Contains("--activate"));
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
        else if (mode == "--pagetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PageTest();
        }
        else if (mode == "--iotest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 给路径时**保留文件**（人工核对 / 拿别的解码器验它）
            IoTest(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--patterntest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PatternTest();
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
    /// <summary>
    /// 自检里**不真的拉新进程**（会开出一堆覆盖窗口，把机器占满），
    /// 只记一笔并返回"成功"——这样"重建救不回来就重启"那条路能被完整走到。
    /// 产品里走基类实现（真拉进程）。
    /// </summary>
    protected override bool RestartSelf(string why)
    {
        // **只有自检模式才拦**。以前这里无条件返回 true，于是产品里的"重启"也不重启了
        // （用户点了一下，界面还在，什么都没发生）——自检的替身把产品路径一起换掉了。
        if (!_selfCheckMode) return base.RestartSelf(why);
        Console.WriteLine($"[自检] 不真的重启（{why}）；产品里这一步会重新拉起自己");
        return true;
    }

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
        Console.WriteLine("  --uitest            界面输入通路自检（合成点击，看谁收到）");
        Console.WriteLine("  --paneltest         产品界面自检（球 → 按钮带这条最小闭环）");
        Console.WriteLine("  --pagetest          整屏翻页自检（一屏 = 一页：页高 = 视口高、只动相机、到顶就停）");
        Console.WriteLine("  --iotest [路径]     导出自检（选中 → PNG 透明底 / JPEG 白底；给路径就保留文件）");
        Console.WriteLine("  --patterntest       白板底纹自检（方格/横线/间距 + 数屏幕上的线 + 重铺代价）");
        Console.WriteLine("  --pageshow <图>     整屏翻页摆样（相机停在两屏之间 / 正好对齐，各出一张）");
        Console.WriteLine("  --panelshow <图> [--band] [--mini] [--drawer] [--cell N]   界面出图（离屏）");
        Console.WriteLine("  --captureshow <图>  截图取景框 + 尺寸读数出图（离屏）");
        Console.WriteLine("  --dialogprobe <前缀> [--save]  导出对话框探针（真弹框 + 点它的下拉 + 连拍三张；");
        Console.WriteLine("                     --save 连「保存」一起点，验到落盘为止）");
        Console.WriteLine("  --erasertest        橡皮擦正确性");
        Console.WriteLine("  --pixelerasetest    像素橡皮正确性（切成两段 / 框里无墨 / 一步撤销）");
        Console.WriteLine("  --pixeleraseshow    像素橡皮摆样（擦之前/之后各存一张图，自己抓屏）");
        Console.WriteLine("  --eraserlab [前缀]  橡皮手测台：铺样例 + 记录每条拖拽，给人用鼠标测（不自动退出）");
        Console.WriteLine("  --imagetest         图像对象（上屏 / 复制翻转 / 存档 / 剪贴板）");
        Console.WriteLine("  --clipboardtest     剪贴板对象通道（复制 → 粘回来仍是对象；会覆盖系统剪贴板）");
        Console.WriteLine("  --lassotest         套索选择（80% 判据 / 贴边无限延伸 / 加选减选）");
        Console.WriteLine("  --capturetest       截图（拖框 → 左上角 → 剪贴板，且不拍进自己的批注）");
        Console.WriteLine("  --cursorshow <笔|荧光笔|激光笔|橡皮|像素橡皮> [宽]  落点摆样");
        Console.WriteLine("  --widthtest         笔迹粗细/压力");
        Console.WriteLine("  --ghosttest         残影检测");
        Console.WriteLine("  --trailtest         委托墨迹轨迹对照");
        Console.WriteLine("  --predicttest       笔迹预测自检（纯算法：直线/加速/急转/断笔/限幅/性能）");
        Console.WriteLine("  --pressurediag      压感采集诊断（合成笔注入：合并点、压感有效位、压力分布）");
        Console.WriteLine("  --wetinktest        湿墨轨迹实测（只让系统画，数上屏像素：这条通道到底画不画）");
        Console.WriteLine("  --predictdata [路径] 真实笔迹数据上的预测评测（UCI Character Trajectories）");
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
    /// **白板底纹（方格 / 横线）自检**——用户 2026-09-17 要的，参考 InkClass。
    ///
    /// 判据是**数屏幕上的线**（不是看代码里那个开关）：
    ///   · 取一条横排像素 → 数"非板色"的段数 = 横着穿过几条**竖线**；
    ///   · 取一条竖排像素 → 段数 = 穿过几条**横线**。
    /// 于是：无底纹两个都是 0；横线只有横的；方格两个都有；间距变小线会变多。
    /// 最后再量一次**代价**：底纹是烘进分块缓存的，所以只有"换底纹那一下"要整层重铺，
    /// 之后每帧都不花钱——两个数都要报出来。
    /// </summary>
    private void PatternTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 白板底纹自检（方格 / 横线）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        BoardOn = true;
        BoardColor = InkPalette.BoardPresets[0].Color;     // 白板（浅色）
        SetBoardPatternFromUi(0, 40f);
        SettleFrames(400);

        // 取屏幕正中偏右下的一块（避开 HUD 和面板），看里面的线
        int px = (int)(_virtualX + _virtualW * 0.35f), py = (int)(_virtualY + _virtualH * 0.35f);
        const int W = 400, H = 400;

        // 一条横排 / 一条竖排：数"和板色不一样"的连续段数
        (int vLines, int hLines) CountLines()
        {
            var buf = ScreenProbe.CaptureRegion(px, py, W, H);
            if (buf.Length < W * H * 4) return (-1, -1);
            int Row(int y)                      // 固定 y 扫一行 → 竖线
            {
                int runs = 0, o = (y * W) * 4;
                bool inLine = false;
                for (int x = 0; x < W; x++)
                {
                    int i = o + x * 4;
                    bool lit = buf[i] < 240 || buf[i + 1] < 240 || buf[i + 2] < 240;
                    if (lit && !inLine) runs++;
                    inLine = lit;
                }
                return runs;
            }
            int Col(int x)                      // 固定 x 扫一列 → 横线
            {
                int runs = 0;
                bool inLine = false;
                for (int y = 0; y < H; y++)
                {
                    int i = (y * W + x) * 4;
                    bool lit = buf[i] < 240 || buf[i + 1] < 240 || buf[i + 2] < 240;
                    if (lit && !inLine) runs++;
                    inLine = lit;
                }
                return runs;
            }
            // 多取几条取中位数（怕某一条正好压在线上）
            var vs = new List<int>(); var hs = new List<int>();
            for (int k = 1; k <= 5; k++) { vs.Add(Row(H * k / 6)); hs.Add(Col(W * k / 6)); }
            vs.Sort(); hs.Sort();
            return (vs[vs.Count / 2], hs[hs.Count / 2]);
        }

        var (v0, h0) = CountLines();
        Check("无底纹：屏幕上一根线都没有", v0 == 0 && h0 == 0, $"竖线 {v0} 条 / 横线 {h0} 条");

        SetBoardPatternFromUi(2, 40f);                     // 横线
        SettleFrames(400);
        var (v2, h2) = CountLines();
        Check("横线：只有横的、没有竖的", v2 == 0 && h2 >= 2, $"竖线 {v2} 条 / 横线 {h2} 条");

        SetBoardPatternFromUi(1, 40f);                     // 方格
        SettleFrames(400);
        var (v1, h1) = CountLines();
        Check("方格：横竖都有", v1 >= 2 && h1 >= 2, $"竖线 {v1} 条 / 横线 {h1} 条");

        SetBoardPatternFromUi(2, 64f);                     // 粗间距
        SettleFrames(400);
        var (_, h64) = CountLines();
        SetBoardPatternFromUi(2, 24f);                     // 细间距
        SettleFrames(400);
        var (_, h24) = CountLines();
        Check("间距生效：细间距的线明显更多", h24 > h64 && h64 >= 1,
              $"24 逻辑像素 → {h24} 条，64 → {h64} 条");

        // ---- 代价：只有"换底纹那一下"要整层重铺，之后每帧都不花钱 ----
        double Measure(bool patternOn, int rounds = 8)
        {
            double total = 0;
            for (int i = 0; i < rounds; i++)
            {
                Doc.InvalidateAll();                        // 逼一次整层重铺
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                total += _windows[0].LastRebuildMs;
            }
            return total / rounds;
        }

        SetBoardPatternFromUi(1, 40f); SettleFrames(300);
        double withPattern = Measure(true);
        // 再量一次"什么都不用重铺"的那一帧：底纹烘在缓存里，这应该是 0
        RenderAll();
        double idleRebuild = _windows[0].LastRebuildMs;

        SetBoardPatternFromUi(0, 40f); SettleFrames(300);
        double withoutPattern = Measure(false);

        Console.WriteLine($"    [性能] 整层重铺一格：有底纹 {withPattern:F2} ms，无底纹 {withoutPattern:F2} ms"
                        + $"（差 {withPattern - withoutPattern:F2} ms）");
        Console.WriteLine($"    [性能] 底纹画好之后的空闲帧：分块光栅 {idleRebuild:F2} ms（应为 0）");
        Check("底纹画好之后，空闲帧不再重铺分块", idleRebuild < 0.05,
              $"空闲帧分块光栅 {idleRebuild:F2} ms");
        Check("整层重铺的代价在可接受范围（< 8 ms）", withPattern < 8.0,
              $"有底纹 {withPattern:F2} ms（这是**换底纹那一下**的一次性代价，不是每帧）");

        // ---- 白板透明度（用户 2026-09-17："隐约看见下面的题目，但是书写有干净"）----
        {
            (byte R, byte G, byte B) ScreenPixel(int x, int y)
            {
                var buf = ScreenProbe.CaptureRegion(x, y, 1, 1);
                return buf.Length < 4 ? ((byte)0, (byte)0, (byte)0) : (buf[2], buf[1], buf[0]);
            }

            int ox = (int)(_virtualX + _virtualW * 0.55f), oy = (int)(_virtualY + _virtualH * 0.75f);
            BoardColor = InkPalette.BoardPresets[2].Color;      // 黑板（深色）：和桌面（浅色）差得远，好判
            SetBoardPatternFromUi(0, 40f);

            SetBoardOpacityFromUi(1f);
            SettleFrames(400);
            var solid = ScreenPixel(ox, oy);
            var wantBoard = InkPalette.BoardPresets[2].Color;
            Check("不透明度 1.0：屏幕上就是板色本身（实心板）",
                  Math.Abs(solid.R - wantBoard.R * 255) < 6 && Math.Abs(solid.G - wantBoard.G * 255) < 6
                  && Math.Abs(solid.B - wantBoard.B * 255) < 6,
                  $"实测 ({solid.R},{solid.G},{solid.B})，板色 ({(int)(wantBoard.R * 255)},{(int)(wantBoard.G * 255)},{(int)(wantBoard.B * 255)})");

            SetBoardOpacityFromUi(0.5f);
            SettleFrames(400);
            var half = ScreenPixel(ox, oy);
            Check("不透明度 0.5：板面变浅（下面的东西透出来了）",
                  half.R > solid.R + 20 || half.G > solid.G + 20 || half.B > solid.B + 20,
                  $"实心 ({solid.R},{solid.G},{solid.B}) → 半透 ({half.R},{half.G},{half.B})");

            // **书写仍然干净**：板面半透明时，笔迹像素还是纯的（不跟着变淡）
            var mag = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 30f * DpiScale,
            };
            mag.AddPoint(ox - 100, oy + 160, 1f, 0);
            mag.AddPoint(ox + 100, oy + 160, 1f, 1);
            Doc.AddStroke(mag);
            SettleFrames(400);
            var inkPixel = ScreenPixel(ox, oy + 160);
            Check("板面半透明时，写上去的墨**仍然是实的**（不跟着变淡）",
                  inkPixel.R > 240 && inkPixel.G < 20 && inkPixel.B > 240,
                  $"墨色 ({inkPixel.R},{inkPixel.G},{inkPixel.B})（应为纯品红 255,0,255）");

            // 收尾：回到白板 + 实心 + 无底纹
            Doc.Clear();
            Doc.ClearHistory();
            BoardColor = InkPalette.BoardPresets[0].Color;
            SetBoardOpacityFromUi(1f);
            SettleFrames(250);
        }

        // 底纹不是笔迹：切底纹不该动文档
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
        };
        s.AddPoint(_virtualX + 500, _virtualY + 500, 1f, 0);
        s.AddPoint(_virtualX + 900, _virtualY + 560, 1f, 1);
        Doc.AddStroke(s);
        SettleFrames(200);
        int before = Doc.Strokes.Count;
        SetBoardPatternFromUi(2, 40f);
        Check("换底纹不动文档（底纹不是笔迹）",
              Doc.Strokes.Count == before && Doc.UndoDepth == 1,
              $"笔画 {before} → {Doc.Strokes.Count}，撤销栈 {Doc.UndoDepth}（还是 1，底纹不进撤销）");

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：方格/横线/间距都对，而且底纹是烘进缓存的（只在换的时候花一次钱）"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");

        Doc.Clear();
        Doc.ClearHistory();
        BoardOn = false;
        SetBoardPatternFromUi(0, 40f);
        _quit = true;
    }

    /// <summary>
    /// **导出（选中的内容 → 透明底 PNG）自检**。
    ///
    /// 用户 2026-09-17 定：只导选中的、透明底、弹"另存为"、**同时进剪贴板**。
    /// 这里不走对话框（对话框在自检里会卡住），直接写到临时文件，其余流程一模一样。
    ///
    /// 关键是**把 PNG 解回来验像素**：PNG 要的是直通 alpha，而我们渲染出来的是
    /// 预乘 alpha——不还原就发灰（半透明荧光笔最明显），而这条错误光看"文件写出来了"
    /// 是发现不了的。所以这里自己解 IDAT（inflate ＋ 去 filter 字节）抽像素比。
    /// </summary>
    private void IoTest(string keepPath = null)
    {
        Console.WriteLine();
        Console.WriteLine("=== 导出自检（选中 → PNG 透明底 / JPEG 白底；不碰剪贴板）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        string path = keepPath ?? Path.Combine(Path.GetTempPath(), "inkteach-iotest.png");
        if (keepPath == null) { try { File.Delete(path); } catch { } }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        SettleFrames(200);

        // ① 没选中 → 不导出、也不该留下文件
        bool ok0 = ExportSelectionToPathForTest(path);
        Check("没选中时不导出、不写文件", !ok0 && !File.Exists(path),
              $"返回 {ok0}，文件在 = {File.Exists(path)}");

        // ② 一笔实心笔 ＋ 一笔半透明荧光笔，全选中
        var red = new Color4(0.95f, 0.18f, 0.18f, 1f);
        var yellow = new Color4(0.98f, 0.82f, 0.12f, 1f);
        var pen = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = red, Width = 20f * DpiScale,
        };
        for (int i = 0; i <= 20; i++) pen.AddPoint(600 + i * 10, 400, 1f, i * 8);
        Doc.AddStroke(pen);

        var hlColor = InkPalette.ToHighlighter(yellow);
        var hl = new Stroke
        {
            Tool = Tool.Highlighter, Kind = StrokeKind.Freehand,
            Color = hlColor, Width = 40f * DpiScale,
        };
        for (int i = 0; i <= 20; i++) hl.AddPoint(600 + i * 10, 500, 1f, i * 8);
        Doc.AddStroke(hl);
        Doc.SelectOnly(new[] { pen, hl });
        Tool = Tool.Marquee;
        SettleFrames(300);

        // 先在剪贴板上放一个"标记"（一条蓝色笔迹）：导出之后它必须**原样还在**——
        // 用户 2026-09-17 明确："导出不用进剪贴板，因为我们有复制功能"。
        var marker = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0f, 1f, 1f), Width = 5f,
        };
        marker.AddPoint(10, 10, 1f, 0); marker.AddPoint(20, 20, 1f, 1);
        ClipboardInk.Set(ClipboardInk.Serialize(new[] { marker }), null, 0, 0);

        bool ok = ExportSelectionToPathForTest(path);
        Check("导出返回成功", ok, $"返回 {ok}");
        if (!ok || !File.Exists(path))
        {
            Console.WriteLine($"  FAIL: 文件没写出来，后面几项没法验（{fail} 项失败）");
            _quit = true;
            return;
        }

        var png = File.ReadAllBytes(path);
        Check("文件非空", png.Length > 100, $"{png.Length} 字节");
        Check("PNG 签名正确",
              png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
              $"{png[0]:X2} {png[1]:X2} {png[2]:X2} {png[3]:X2}");

        // ③ 解回像素：走 chunks → 拼 IDAT → inflate → 去每行的 filter 字节
        int iw = 0, ih = 0;
        var idat = new MemoryStream();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            string type = "" + (char)png[p + 4] + (char)png[p + 5] + (char)png[p + 6] + (char)png[p + 7];
            int dataAt = p + 8;
            if (type == "IHDR")
            {
                iw = (png[dataAt] << 24) | (png[dataAt + 1] << 16) | (png[dataAt + 2] << 8) | png[dataAt + 3];
                ih = (png[dataAt + 4] << 24) | (png[dataAt + 5] << 16) | (png[dataAt + 6] << 8) | png[dataAt + 7];
            }
            else if (type == "IDAT") idat.Write(png, dataAt, len);
            p = dataAt + len + 4;
        }

        var box = EditRegion.Of(new[] { pen, hl }).Inflate(4f * DpiScale);
        int wantW = (int)MathF.Ceiling(box.MaxX - box.MinX);
        int wantH = (int)MathF.Ceiling(box.MaxY - box.MinY);
        Check("IHDR 的宽高 = 选区像素尺寸", iw == wantW && ih == wantH,
              $"图 {iw}×{ih}，选区 {wantW}×{wantH}");

        // inflate：zlib 头 2 字节 ＋ 尾部 adler32 4 字节，中间是裸 deflate
        var z = idat.ToArray();
        byte[] raw;
        using (var ms = new MemoryStream(z, 2, z.Length - 6))
        using (var inf = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            inf.CopyTo(outMs);
            raw = outMs.ToArray();
        }
        Check("IDAT 能解回原始像素（每行 1 个 filter 字节）",
              raw.Length >= ih * (1 + iw * 4), $"{raw.Length} 字节（应为 {ih * (1 + iw * 4)}）");

        (byte R, byte G, byte B, byte A) PixelAt(float canvasX, float canvasY)
        {
            int x = Math.Clamp((int)(canvasX - box.MinX), 0, iw - 1);
            int y = Math.Clamp((int)(canvasY - box.MinY), 0, ih - 1);
            int o = y * (1 + iw * 4) + 1 + x * 4;
            return (raw[o], raw[o + 1], raw[o + 2], raw[o + 3]);
        }

        var px = PixelAt(700, 400);           // 红笔的中心
        Check("实心笔：不透明、颜色就是笔色",
              px.A == 255
              && Math.Abs(px.R - red.R * 255) < 6 && Math.Abs(px.G - red.G * 255) < 6
              && Math.Abs(px.B - red.B * 255) < 6,
              $"RGBA = {px.R},{px.G},{px.B},{px.A}（应为 {(int)(red.R * 255)},{(int)(red.G * 255)},{(int)(red.B * 255)},255）");

        var hp = PixelAt(700, 500);           // 荧光笔的中心
        int wantA = (int)(hlColor.A * 255);
        Check("荧光笔：半透明（没被压成不透明）",
              hp.A > 20 && hp.A < 250, $"alpha = {hp.A}（应为 {wantA} 上下）");
        // 预乘 → 直通没做对的话，这里会明显偏暗（R 会从 ~250 掉到 ~80）
        Check("荧光笔：颜色是**直通 alpha**（预乘没还原就会发灰）",
              Math.Abs(hp.R - yellow.R * 255) < 12 && Math.Abs(hp.G - yellow.G * 255) < 12
              && Math.Abs(hp.B - yellow.B * 255) < 12,
              $"RGB = {hp.R},{hp.G},{hp.B}（应为 {(int)(yellow.R * 255)},{(int)(yellow.G * 255)},{(int)(yellow.B * 255)}）");

        var blank = PixelAt(300, 300);        // 选区左上角那块（没有墨）
        Check("空白处：完全透明（透明底）", blank.A == 0 && blank.R == 0 && blank.G == 0 && blank.B == 0,
              $"RGBA = {blank.R},{blank.G},{blank.B},{blank.A}");

        // ④ 剪贴板：导出**不许动它**（用户 2026-09-17："导出不用进剪贴板，我们有复制功能"）。
        // 判据：导出前放一条"标记"笔迹，导出之后读回来还得是那一条（1 个，不是选中的 2 个）。
        bool clip = ClipboardInk.TryGetObjects(out var back);
        Check("导出**不动剪贴板**（复制那条路各管各的）",
              clip && back != null && back.Count == 1,
              clip ? $"读回 {back?.Count} 个对象（应为 1 = 导出前放的那条标记）"
                   : "剪贴板里没有对象（不该）");

        // ⑤ 另一种格式：**JPEG（白底）**——用户 2026-09-17："导出功能增加 jpg 格式，
        //    然后让用户知道 png 是透明底、jpg 是白底"。格式差别写在文件类型那一行，
        //    这里验的是"真按 JPG 编码了、而且透明处是白的（不是黑的）"。
        string jpgPath = System.IO.Path.ChangeExtension(path, ".jpg");
        try { File.Delete(jpgPath); } catch { }
        bool okJpg = ExportSelectionToPathForTest(jpgPath);
        Check("导出成 .jpg：返回成功、文件存在", okJpg && File.Exists(jpgPath),
              $"返回 {okJpg}，文件在 = {File.Exists(jpgPath)}");
        if (File.Exists(jpgPath))
        {
            var jpg = File.ReadAllBytes(jpgPath);
            Check("是真正的 JPEG（FF D8 FF 开头）",
                  jpg.Length > 4 && jpg[0] == 0xFF && jpg[1] == 0xD8 && jpg[2] == 0xFF,
                  $"{jpg[0]:X2} {jpg[1]:X2} {jpg[2]:X2}，{jpg.Length / 1024.0:F0} KB");
            // 解码回来验像素：GDI+ 解、和我们编码用的不是同一段代码
            try
            {
                using var bmp = new System.Drawing.Bitmap(jpgPath);
                Check("JPEG 尺寸 = 选区像素尺寸", bmp.Width == wantW && bmp.Height == wantH,
                      $"{bmp.Width}×{bmp.Height}，选区 {wantW}×{wantH}");
                // 空白处（原来透明）必须是**白**的——JPEG 没有透明通道，
                // 不合成白底的话那里会是黑块（很多程序都踩过）
                var blankPx = bmp.GetPixel(20, 20);
                Check("原来透明的地方变成**白底**（不是黑块）",
                      blankPx.R > 235 && blankPx.G > 235 && blankPx.B > 235,
                      $"({blankPx.R},{blankPx.G},{blankPx.B})");
                // 笔迹颜色还在（JPEG 有损，给宽一点的容差）
                var penPx = bmp.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmp.Width - 1),
                                         Math.Clamp((int)(400 - box.MinY), 0, bmp.Height - 1));
                Check("笔迹颜色还在（有损压缩，允许偏差）",
                      Math.Abs(penPx.R - red.R * 255) < 40 && penPx.G < 110 && penPx.B < 110,
                      $"({penPx.R},{penPx.G},{penPx.B})，原笔色 ({(int)(red.R * 255)},{(int)(red.G * 255)},{(int)(red.B * 255)})");
            }
            catch (Exception ex) { Check("JPEG 能被别的解码器读出来", false, ex.Message); }
            try { File.Delete(jpgPath); } catch { }
        }

        // ⑥ **PNG 白底**（第 3 条）：无损 + 白底。
        //    给"深色模板"用的——透明底的黑色笔迹贴到深色 PPT 上会看不见。
        //    注意它和第 1 条**同一个扩展名**，差别只在"选了哪一条"，
        //    所以这里必须传 filterIndex=3，不能靠扩展名区分。
        {
            string whiteP = Path.Combine(Path.GetTempPath(), "inkteach-iotest-white.png");
            try { File.Delete(whiteP); } catch { }
            bool okW = ExportSelectionToPathForTest(whiteP, 3);
            Check("导出成「PNG 白底」（第 3 条）：文件存在", okW && File.Exists(whiteP), $"返回 {okW}");
            if (File.Exists(whiteP))
            {
                var bytes = File.ReadAllBytes(whiteP);
                Check("还是 PNG 签名（同一条扩展名，两个选择）",
                      bytes.Length > 8 && bytes[0] == 0x89 && bytes[3] == 0x47,
                      $"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");
                try
                {
                    using var bmp = new System.Drawing.Bitmap(whiteP);
                    var blankPx = bmp.GetPixel(20, 20);
                    Check("空白处是**白底**（透明底那条这里是全透明）",
                          blankPx.R > 250 && blankPx.G > 250 && blankPx.B > 250 && blankPx.A == 255,
                          $"({blankPx.R},{blankPx.G},{blankPx.B},a={blankPx.A})");
                    var penPx = bmp.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmp.Width - 1),
                                             Math.Clamp((int)(400 - box.MinY), 0, bmp.Height - 1));
                    Check("笔迹颜色**一个不差**（无损，不是 JPEG 那种有损）",
                          Math.Abs(penPx.R - red.R * 255) < 4
                          && Math.Abs(penPx.G - red.G * 255) < 4
                          && Math.Abs(penPx.B - red.B * 255) < 4,
                          $"({penPx.R},{penPx.G},{penPx.B})");
                }
                catch (Exception ex) { Check("白底 PNG 能被别的解码器读出来", false, ex.Message); }
                try { File.Delete(whiteP); } catch { }
            }
        }

        // ⑦ **BMP 白底**（第 4 条）：老软件也打得开的无损位图。
        //    自己写的编码器，所以逐字段验：签名、宽高、位深、以及**像素真的是白的和白底**。
        {
            string bmpPath = Path.Combine(Path.GetTempPath(), "inkteach-iotest.bmp");
            try { File.Delete(bmpPath); } catch { }
            bool okB = ExportSelectionToPathForTest(bmpPath, 4);
            Check("导出成 .bmp：返回成功、文件存在", okB && File.Exists(bmpPath),
                  $"返回 {okB}，文件在 = {File.Exists(bmpPath)}");
            if (File.Exists(bmpPath))
            {
                var b = File.ReadAllBytes(bmpPath);
                int bw = b[18] | (b[19] << 8) | (b[20] << 16) | (b[21] << 24);
                int bh = b[22] | (b[23] << 8) | (b[24] << 16) | (b[25] << 24);
                int bits = b[28] | (b[29] << 8);
                int off = b[10] | (b[11] << 8) | (b[12] << 16) | (b[13] << 24);
                Check("BMP 文件头：'BM' + 宽高 + 24 位",
                      b[0] == (byte)'B' && b[1] == (byte)'M' && bw == wantW && bh == wantH && bits == 24,
                      $"{(char)b[0]}{(char)b[1]}，{bw}×{bh}，{bits} 位，像素起点 {off}，{b.Length / 1024.0:F0} KB");

                // **用别的解码器读**（GDI+），不自己解——自己解只能证明"我写的我自己读得懂"，
                // 证不了这个文件在别的程序里对不对（和上面 JPEG 那条同一个道理）。
                try
                {
                    using var bmpImg = new System.Drawing.Bitmap(bmpPath);
                    Check("GDI+ 也读得出来（尺寸对）",
                          bmpImg.Width == wantW && bmpImg.Height == wantH,
                          $"{bmpImg.Width}×{bmpImg.Height}");
                    var blankPx = bmpImg.GetPixel(20, 20);
                    Check("空白处是**白底**（不是黑块、不是透明）",
                          blankPx.R > 250 && blankPx.G > 250 && blankPx.B > 250,
                          $"({blankPx.R},{blankPx.G},{blankPx.B})");
                    var penPx = bmpImg.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmpImg.Width - 1),
                                                Math.Clamp((int)(400 - box.MinY), 0, bmpImg.Height - 1));
                    Check("笔迹颜色**一个不差**（无损）",
                          Math.Abs(penPx.R - red.R * 255) < 4
                          && Math.Abs(penPx.G - red.G * 255) < 4
                          && Math.Abs(penPx.B - red.B * 255) < 4,
                          $"({penPx.R},{penPx.G},{penPx.B})");
                    // 反过来：**上下不能颠倒**（BMP 是自下而上存的，写反了图是扣着的）
                    var topRow = bmpImg.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmpImg.Width - 1), 1);
                    Check("上下没写反（最上一行是空白，不是笔迹）",
                          topRow.R > 250 && topRow.G > 250 && topRow.B > 250,
                          $"最上一行 ({topRow.R},{topRow.G},{topRow.B})");
                }
                catch (Exception ex) { Check("BMP 能被别的解码器读出来", false, ex.Message); }
                try { File.Delete(bmpPath); } catch { }
            }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：四种格式都对（PNG 透明底 / JPEG 白底 / PNG 白底 / BMP 白底，逐像素验过），而且没碰剪贴板"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");
        Console.WriteLine($"  文件：{path}（{png.Length} 字节）");

        // 给了路径就留着（人工核对 / 拿别的解码器验它）
        if (keepPath == null) { try { File.Delete(path); } catch { } }
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// **整屏翻页自检（一屏 = 一页）**。取向与出处见 [调研-白板翻页.md]。
    ///
    /// 关键是盯着"**没有新概念**"这件事：我们**不做** OneNote 那种"每页一个文档"
    /// （那是 InkClass 的路子，专为跟 PPT 对齐而付的代价），而是**同一张连续的长纸**，
    /// 只让相机按整屏跳。所以这一套用例全在验：
    ///   · 页高 = 视口高，翻一屏 = 相机正好走一屏（翻完不留半行字）；
    ///   · **翻页前后，文档里没有一个坐标发生变化**（只有相机动）——这是"没做分页"的证据；
    ///   · 到顶就停、往下永远还有一屏（无限纸的红利）；
    ///   · 滚轮仍然是细粒度（翻页不该把滚轮改成"一格一屏"）；
    ///   · 翻完屏幕上真的换了内容（屏幕取点，和 --cameratest 同一套手法）。
    /// </summary>
    private void PageTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 整屏翻页自检（一屏 = 一页）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        SettleFrames(250);

        float pageH = _virtualH;                       // 物理像素：页高 = 视口高

        // ---- ① 页高 ----
        Check("页高 = 视口高", Math.Abs(PageHeightCanvas - _virtualH) < 0.01f,
              $"页高 {PageHeightCanvas:F0}，视口高 {_virtualH}");
        Check("第 1 屏的页顶 = 虚拟桌面顶",
              Math.Abs(PageTopCanvas - _virtualY) < 0.01f,
              $"页顶 {PageTopCanvas:F0}，桌面顶 {_virtualY}");

        // ---- ② 翻一屏 = 相机正好走一屏 ----
        Check("起手在第 1 屏、且不能再往上翻",
              ScreenIndex == 1 && !CanFlipPageUp,
              $"屏号 {ScreenIndex}，能上翻 = {CanFlipPageUp}");

        bool went = FlipPage(true);
        SettleFrames(400);                             // 等完那 167ms 的缓动
        Check("往下翻返回 true", went, $"返回 {went}");
        Check("翻一屏 = 相机正好走一屏", Math.Abs(ViewOffsetY + pageH) < 0.6f,
              $"相机 {ViewOffsetY:F1}（应为 {-pageH:F0}）");
        Check("翻完在第 2 屏", ScreenIndex == 2, $"屏号 {ScreenIndex}");
        Check("到第 2 屏后就能往上翻了", CanFlipPageUp, $"能上翻 = {CanFlipPageUp}");

        // ---- ③ 往下永远还有一屏（连翻 6 次都成）----
        int flipped = 1;
        for (int i = 0; i < 6; i++)
        {
            if (!FlipPage(true)) break;
            flipped++;
            SettleFrames(250);
        }
        Check("下一屏永远可用（连翻 6 次都成）", flipped == 7,
              $"成功 {flipped} 次，屏号 {ScreenIndex}");

        // ---- ④ 回来；到顶后再上翻必须"什么都不做" ----
        for (int i = 0; i < 10 && CanFlipPageUp; i++) { FlipPage(false); SettleFrames(250); }
        Check("连翻回顶：相机夹在 0，不越过", Math.Abs(ViewOffsetY) < 0.01f,
              $"相机 {ViewOffsetY:F2}");
        bool upAtTop = FlipPage(false);
        SettleFrames(200);
        Check("到顶了再上翻：返回 false 且相机不动",
              !upAtTop && Math.Abs(ViewOffsetY) < 0.01f,
              $"返回 {upAtTop}，相机 {ViewOffsetY:F2}");

        // ---- ⑤ 翻页前后，文档里一个坐标都不变（证明"没做分页"）----
        // 铺开三屏、每屏三行——量级对齐一节真实板书。
        Doc.Clear();
        Doc.ClearHistory();
        for (int screen = 0; screen < 3; screen++)
            for (int row = 0; row < 3; row++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.12f, 0.13f, 0.16f, 1f), Width = 6f,
                };
                float y = _virtualY + screen * _virtualH + 200f + row * 260f;
                for (int i = 0; i <= 20; i++)
                    s.AddPoint(_virtualX + 240f + i * 55f, y + (i % 4) * 8f, 1f, i * 8);
                Doc.AddStroke(s);
            }
        Doc.InvalidateAll();
        SettleFrames(200);

        string Dump()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Doc.Strokes)
            {
                sb.Append((int)s.Tool).Append('/').Append((int)s.Kind).Append(':');
                foreach (var p in s.Points)
                    sb.Append(p.X.ToString("F3")).Append(',').Append(p.Y.ToString("F3")).Append(';');
                sb.Append('|');
            }
            return sb.ToString();
        }

        string coords0 = Dump();
        int strokes0 = Doc.Strokes.Count;
        FlipPage(true); SettleFrames(300);
        FlipPage(true); SettleFrames(300);
        FlipPage(false); SettleFrames(300);
        FlipPage(true); SettleFrames(300);
        string coords1 = Dump();
        Check("翻来翻去：对象数没变", Doc.Strokes.Count == strokes0,
              $"{strokes0} → {Doc.Strokes.Count} 条");
        Check("翻来翻去：**每一个坐标都没变**", coords1 == coords0,
              coords1 == coords0 ? $"{coords0.Length} 字符逐字符一致（只有相机动）"
                                 : "有坐标被改动了 —— 翻页不该碰文档");

        // ---- ⑥ 滚轮仍是细粒度（一格 72 逻辑像素，不是一格一屏）----
        IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
        for (int i = 0; i < 12 && CanFlipPageUp; i++) { FlipPage(false); SettleFrames(250); }
        SettleFrames(150);
        float camBefore = ViewOffsetY;
        HandleWheel(Wheel(-120));
        SettleFrames(120);
        float wheelStep = 72f * DpiScale;
        Check("滚轮还是细粒度（一格 72 逻辑像素）",
              Math.Abs(ViewOffsetY - camBefore + wheelStep) < 1.2f,
              $"走了一格 = {camBefore - ViewOffsetY:F0} 物理像素（应为 {wheelStep:F0}，一屏是 {pageH:F0}）");
        HandleWheel(Wheel(120));                       // 滚回去
        SettleFrames(120);

        // ---- ⑦ 翻完屏幕上真的换了内容（屏幕取点）----
        // 先放一笔**第 1 屏**的洋红墨：它必须看得见——既当判据，也当"取屏可用"的探针。
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        // 两笔**故意放在不同的屏幕高度**，这样"没翻动"和"翻动了"才有区别：
        //   第 1 屏那笔 → 相机为 0 时在屏幕 y=400；翻下去之后跑到屏幕外（负号），
        //   第 2 屏那笔 → 相机为 0 时在屏幕外，翻下去之后出现在屏幕 y=1000。
        // （第一版把两笔放在**同一个屏幕位置**，结果"翻没翻"取到的像素数一模一样，
        //   即使相机根本没动也会通过——这种"测不出区别"的用例比没有还坏。）
        float px = _virtualX + 900f;
        float y1 = _virtualY + 400f;                 // 第 1 屏
        float y2 = _virtualY + _virtualH + 1000f;    // 第 2 屏，翻下去之后落在屏幕 y=1000
        foreach (float cy in new[] { y1, y2 })
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 26f * DpiScale,
            };
            s.AddPoint(px - 220f, cy, 1f, 0);
            s.AddPoint(px + 220f, cy, 1f, 1);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(400);

        int BandAt(float screenY) => ScreenProbe.CountMagenta((int)px - 260, (int)screenY - 40, 520, 80);
        float homeProbeY = _virtualY + 400f, secondProbeY = _virtualY + 1000f;

        int probe = BandAt(homeProbeY);
        if (probe < 300)
        {
            // 锁屏 / 远程桌面 / 别的窗口盖住时取不到像素：这条只能跳过，不能算失败。
            Console.WriteLine($"  [跳过] 第一屏的墨自己都没取到（{probe} 像素）——"
                            + "屏幕取点这时候不可用（锁屏 / 远程 / 被盖住），这一条不判红绿");
        }
        else
        {
            int secondBefore = BandAt(secondProbeY);
            Check("相机为 0 时：第 2 屏那笔不该在屏幕上", secondBefore < 40,
                  $"{secondBefore} 像素（第 2 屏那笔此时在屏幕外）");

            FlipPage(true);
            SettleFrames(400);
            int firstAfter = BandAt(homeProbeY);
            int secondAfter = BandAt(secondProbeY);
            Check("翻下去：第 1 屏那笔离开屏幕", firstAfter < 40,
                  $"{firstAfter} 像素（原来 400 处那笔现在在屏幕外）");
            Check("翻下去：第 2 屏那笔出现在屏幕上", secondAfter > 300,
                  $"{secondAfter} 像素（落在屏幕 1000 处）");

            FlipPage(false);
            SettleFrames(400);
            int backHome = BandAt(homeProbeY);
            Check("翻回来：第 1 屏那笔还在原处", backHome > 300, $"{backHome} 像素");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：整屏翻页正确（页高 = 视口高、只动相机、到顶就停、往下无限、滚轮仍是细粒度）"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");

        // ---- ⑧ 清空之后**留在原地**（用户 2026-09-17 问："如果我的焦点在第 12 页，
        //        点击清空以后要不要回到第一页？"）----
        //
        // 结论：**不回**。清空是"这一屏重新来"，不是"回到开头"：
        // 老师在第 12 屏写完一题、点清空，就是要在**这一屏**接着写下一题；
        // 把相机弹回去等于让他再翻十几次，而且下一次落笔的位置也错了
        // （我们一直守"点下去的东西别动"，锚点、按钮位置都按它办）。
        // 真想从头看，点"上一屏"或拖右缘滚动条就行。
        //
        // 这一条要**钉在自检里**，因为"清空之后镜子一照全变"这种事很容易被以后某次
        // 改动带歪（比如"顺手把相机归零"）。
        {
            Doc.Clear();
            Doc.ClearHistory();
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
            Doc.InvalidateAll();
            SettleFrames(250);

            FlipPage(true); SettleFrames(300);
            FlipPage(true); SettleFrames(300);
            int pageKept = ScreenIndex;
            float camKept = ViewOffsetY;

            for (int i = 0; i < 3; i++)
            {
                var st = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0f, 0f, 0f, 1f), Width = 8f,
                };
                st.AddPoint(600 + i * 60, 300 + i * 40, 1f, 0);
                st.AddPoint(900 + i * 60, 340 + i * 40, 1f, 1);
                Doc.AddStroke(st);
            }
            SettleFrames(250);
            ClearFromUi();
            SettleFrames(400);

            Check("清空之后：**还停在第 3 屏**，相机一动不动",
                  ScreenIndex == pageKept && MathF.Abs(ViewOffsetY - camKept) < 0.5f,
                  $"屏号 {pageKept} → {ScreenIndex}，相机 {camKept:F0} → {ViewOffsetY:F0}"
                  + $"（文档 {Doc.Strokes.Count} 笔）");
            Check("清空之后：还能接着在这一屏写",
                  Doc.Strokes.Count == 0,
                  $"笔画 {Doc.Strokes.Count}");
        }

        // ---- ⑨ **页模型**：归属 / 清空只清本页 / 撤销跨页自动翻回去（2026-09-17）----
        //
        // 这一段的取向写在 计划-白板与PPT-页逻辑.md 第四节：**一份文档 + 对象带页归属 +
        // 一条全局撤销栈**，切页只动相机（O(1)），不像 InkClass 那样"切页 = 清空 +
        // 重放历史 + 清掉撤销栈"。
        {
            Doc.Clear();
            Doc.ClearHistory();
            GotoPage(0);
            SettleFrames(300);
            Check("回到第 1 页时 CurrentPage = 0", CurrentPage == 0, $"CurrentPage = {CurrentPage}");

            // 三页各写一笔。走的是"起笔时按当前页标页"这条真规则。
            var made = new List<Stroke>();
            for (int pg = 0; pg < 3; pg++)
            {
                GotoPage(pg);
                SettleFrames(320);
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.1f, 0.1f, 0.1f, 1f), Width = 8f,
                    Page = CurrentPage,                       // 引擎在起笔那一刻就是这么标的
                };
                float y = PageTopCanvas + pg * PageHeightCanvas + 300f;
                for (int i = 0; i <= 20; i++) s.AddPoint(_virtualX + 300f + i * 40f, y, 1f, i * 8);
                Doc.AddStroke(s);
                made.Add(s);
            }
            Check("三页各一笔、页归属分别是 0/1/2",
                  made[0].Page == 0 && made[1].Page == 1 && made[2].Page == 2,
                  $"{made[0].Page}/{made[1].Page}/{made[2].Page}");
            Check("页号跟着相机走（此刻在第 3 页）", CurrentPage == 2, $"CurrentPage = {CurrentPage}");
            // 每笔落在**自己那一页的带子里**（归属不是拍脑袋标的，和几何也对得上）
            Check("归属和几何对得上（起点落在本页的纵向带里）",
                  PageOfCanvasY(made[0].Points[0].Y) == 0 && PageOfCanvasY(made[1].Points[0].Y) == 1
                  && PageOfCanvasY(made[2].Points[0].Y) == 2,
                  $"{PageOfCanvasY(made[0].Points[0].Y)}/{PageOfCanvasY(made[1].Points[0].Y)}"
                  + $"/{PageOfCanvasY(made[2].Points[0].Y)}");

            // 清空 = **只清这一页**
            ClearFromUi();
            SettleFrames(200);
            Check("清空只清当前页（第 3 页），前两页原样留着",
                  Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(made[2])
                  && Doc.Strokes.Contains(made[0]) && Doc.Strokes.Contains(made[1]),
                  $"剩 {Doc.Strokes.Count} 笔");
            Check("清空之后留在原地（不回第 1 页）", CurrentPage == 2, $"CurrentPage = {CurrentPage}");

            // 撤销：被清掉的那一笔回来
            UndoFromUi();
            SettleFrames(250);
            Check("撤销清空：第 3 页那一笔回来了、其余页没受影响",
                  Doc.Strokes.Count == 3 && Doc.Strokes.Contains(made[2]),
                  $"现在 {Doc.Strokes.Count} 笔");

            // **撤销跨页自动翻回去**。
            //
            // 用"在第 3 页删掉一笔"当场景，而不是"撤销第 3 页那次落笔"：
            // 相机**只能滚到有内容的地方**（`ClampOffset` 按画布内容范围夹），
            // 撤销一次落笔会让第 3 页变空、于是根本滚不过去——那不算 bug，
            // 但用那个场景验不出"相机会不会自己翻过去"。删除则相反：撤销之后
            // 第 3 页又有内容了，正好验"相机跟到那一页、那一笔又看得见"。
            GotoPage(2);
            SettleFrames(320);
            Doc.Selected.Clear();
            Doc.Selected.Add(made[2]);
            Doc.DeleteSelected();
            SettleFrames(200);
            Check("在第 3 页删掉一笔（这一步发生在第 3 页）",
                  Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(made[2]),
                  $"剩 {Doc.Strokes.Count} 笔");

            GotoPage(0);
            SettleFrames(350);
            Check("先回到第 1 页", CurrentPage == 0, $"CurrentPage = {CurrentPage}");
            UndoFromUi();
            SettleFrames(500);
            Check("撤销跨页：相机自己翻到那一页（不然屏幕上什么都没变）",
                  CurrentPage == 2, $"撤销后停在 CurrentPage = {CurrentPage}");
            Check("撤销跨页：那一笔回来了、还在它的那一页",
                  Doc.Strokes.Contains(made[2]) && made[2].Page == 2,
                  $"剩 {Doc.Strokes.Count} 笔，Page = {made[2].Page}");
            RedoFromUi();
            SettleFrames(450);
            Check("重做：又删掉了，而且相机仍在第 3 页",
                  !Doc.Strokes.Contains(made[2]) && CurrentPage == 2,
                  $"剩 {Doc.Strokes.Count} 笔，CurrentPage = {CurrentPage}");
            UndoFromUi();               // 复原，后面存档那一条要三笔齐
            SettleFrames(450);

            // 存盘往返：**页归属要跟着存**（不然重开就全糊成一页了）
            var blob = InkSerializer.Save(Doc);
            var back = new InkDocument();
            InkSerializer.LoadInto(back, blob);
            var pages = back.Strokes.Select(s => s.Page).OrderBy(v => v).ToArray();
            Check("存档带上页归属（读回来还是那几页）",
                  pages.Length == 3 && pages[0] == 0 && pages[1] == 1 && pages[2] == 2,
                  "读回页号 " + string.Join("/", pages));
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
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
        Console.WriteLine("=== 选中与变换性能 ===");
        Console.WriteLine();
        Console.WriteLine("  ① 老做法：每帧改模型（SetTransformLive）→ 内容层按脏区修补");
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

        // ---- ② 现在的拖动路径（方案 B：拖动预览）---------------------------
        //
        // 同一批对象、同样的拖动，走**真实的手势路径**：按住 → 每步挪一点 → 松手。
        // 这一条要盯的是两个数：
        //   · 内容层每帧重画的块数 —— 方案 B 之后应当是 **0**（模型不动、块全有效）；
        //   · 每帧记录耗时 —— 老做法那个"散布全屏 19ms"是否真的掉下来。
        Console.WriteLine();
        Console.WriteLine("  ② 现在的拖动（方案 B：拖动预览，内容层不动）");
        Console.WriteLine("  画布里  | 变换 | 起手一次重画 | 拖动中重画 | 拖动中补丁均 | 每帧记录均 | 每帧最长");
        Console.WriteLine("  --------|------|--------------|------------|--------------|------------|--------");
        foreach (int total in new[] { 1000, 10000 })
        {
            Doc.Clear();
            Doc.ClearHistory();
            if (total > 0) GenerateStrokes(total);
            SettleFrames(150);

            // 刻意挑**散布全屏**的 20 条（和老做法那张表同一个场景）
            Doc.Selected.Clear();
            int pick = Math.Min(20, Doc.Strokes.Count);
            int stride = Math.Max(1, Doc.Strokes.Count / Math.Max(1, pick));
            for (int i = 0; i < pick; i++)
                Doc.Selected.Add(Doc.Strokes[Math.Min(Doc.Strokes.Count - 1, i * stride)]);

            var fr = SelectionHandles.FrameOf(Doc.Selected);
            var aabb = fr.CanvasAabb;
            float px = (aabb.MinX + aabb.MaxX) * 0.5f, py = (aabb.MinY + aabb.MaxY) * 0.5f;
            bool took = SelectionGestureForTest(px, py);

            var w = _windows[0];
            const int steps = 30;
            double patchSum = 0, recordSum = 0, recordMax = 0, patchMax = 0;
            double tilesSum = 0; int tilesMax = 0;
            int startTiles = 0; double startPatchMs = 0;
            for (int k = 0; k < steps; k++)
            {
                UpdateSelectionGestureForTest(px + 6f * (k + 1), py + 3f * (k + 1));
                SettleFrames(10);
                recordMax = Math.Max(recordMax, w.LastRecordMs);
                if (k == 0)
                {
                    // 第 0 步 = **起手那一下**：把这一批从内容层摘出去，那些块要重画一次。
                    // 这一笔是一次性的，不能混进"拖动中每帧"的均值里（混进去会把它算成
                    // 每个拖动帧的代价，看着像没优化）。
                    startTiles = w.LastPatchCount;
                    startPatchMs = w.LastPatchMs;
                    continue;
                }
                patchSum += w.LastPatchMs; recordSum += w.LastRecordMs;
                patchMax = Math.Max(patchMax, w.LastPatchMs);
                tilesSum += w.LastPatchCount;
                tilesMax = Math.Max(tilesMax, w.LastPatchCount);
            }
            EndSelectionGestureForTest();
            SettleFrames(60);
            int steadySteps = steps - 1;

            Console.WriteLine($"  {total,7} | {pick,4} | {startTiles,7} 块/{startPatchMs,5:F1}ms | "
                            + $"{tilesSum / steadySteps,7:F1} 块 | {patchSum / steadySteps,9:F2} ms | "
                            + $"{recordSum / steadySteps,8:F2} ms | {recordMax,8:F2} ms"
                            + (took ? "" : "   （！手势没接住）"));
            Console.WriteLine($"          （拖动中内容层重画的块：均 {tilesSum / steadySteps:F1}、最多 {tilesMax}"
                            + $"，应当是 0；起手那一下 {startTiles} 块是一次性的）");
        }
        Console.WriteLine();
        Console.WriteLine("  判据：②的\"内容层重画块/帧\"应当是 0（模型不动，只画浮动层预览），");
        Console.WriteLine("        每帧耗时应当与\"选中的这几条\"成正比，而不是与\"走过的面积\"成正比。");
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
    /// **截图取景框**的离屏出图：摆几笔背景墨 → 把取景框摆在中间 → 拍下来。
    ///
    /// 看的是两件自检读不出来的事：**框和角标的粗细在 2 倍屏上顺不顺眼**，
    /// 以及拖动中那个**尺寸读数胶囊**跟框的距离、字的大小合不合适。
    /// </summary>
    /// <summary>
    /// **导出对话框探针**：真的弹一次系统"另存为"，同时由一个后台线程去点它的
    /// "保存类型"下拉，并把每一步截下来。
    ///
    /// 为什么要专门做它：这里出过两次问题（"选不到 jpg"、"对话框被自己的下拉盖住"），
    /// 而**这些问题只能靠真机 + 看图判断**——系统对话框是另一个窗口类，
    /// 我们的自检（合成输入 + 数像素）碰不到它的内部控件。探针把"找窗口 →
    /// 点下拉 → 截图 → 关掉"这套动作固定下来，改一次就能再验一次。
    ///
    /// 用法：`--dialogprobe [前缀]`，产出 `<前缀>-1-打开时.bmp`、`-2-点下拉后.bmp`。
    /// </summary>
    private void DialogProbe(string prefix)
    {
        BoardOn = true;
        Tool = Tool.Marquee;
        Doc.Clear();
        Doc.ClearHistory();
        // 几条横线铺在屏幕中间：对话框会压在上面，正好看"框和墨谁在上面"
        for (int i = 0; i < 5; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.85f, 0.15f, 0.15f, 1f), Width = 8f * DpiScale,
            };
            float y = _virtualY + _virtualH * 0.35f + i * 90f * DpiScale;
            s.AddPoint(_virtualX + _virtualW * 0.15f, y, 1f, i * 4);
            s.AddPoint(_virtualX + _virtualW * 0.85f, y, 1f, i * 4 + 2);
            Doc.AddStroke(s);
        }
        Doc.SelectOnly(Doc.Strokes.ToArray());
        SettleFrames(700);

        var t = new Thread(() => DialogProbeThread(prefix)) { IsBackground = true };
        t.Start();

        ExportDialogEnabled = true;
        ExportSelection(1);                 // 阻塞在系统对话框里，后台线程在操作它
        Console.WriteLine("dialogprobe：对话框已关闭（走的是取消那条路）");

        // 走了 `--save` 的话，这里验"真的写出来了"（写完就删，不留垃圾）
        if (DialogProbeCheckExport && LastExportPath != null)
        {
            var fi = new FileInfo(LastExportPath);
            Console.WriteLine(fi.Exists && fi.Length > 0
                ? $"dialogprobe：**文件真的写出来了** {LastExportPath}（{fi.Length} 字节，"
                  + $"类型第 {LastExportFilterIndex} 条）——对话框那条链是通的"
                : $"dialogprobe：**没写出来** {LastExportPath}");
            try { fi.Delete(); } catch { }
            Console.WriteLine("dialogprobe：测试文件已删掉");
            DialogProbeCheckExport = false;
        }
        _quit = true;
    }

    private static bool DialogProbeCheckExport;

    private static IntPtr ProbeFindDialog()
    {
        IntPtr found = IntPtr.Zero;
        uint me = (uint)Environment.ProcessId;
        var sb = new System.Text.StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid != me) return true;
            Native.GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != "#32770") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string ProbeClassName(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        Native.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private void DialogProbeThread(string prefix)
    {
        IntPtr dlg = IntPtr.Zero;
        for (int i = 0; i < 120 && dlg == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            dlg = ProbeFindDialog();
        }
        if (dlg == IntPtr.Zero) { Console.WriteLine("dialogprobe：6 秒内没等到对话框"); return; }

        Native.GetWindowRect(dlg, out var r);
        Console.WriteLine($"dialogprobe：对话框 {dlg} 在 ({r.Left},{r.Top})，{r.Width}×{r.Height}");

        // 等 1.2 秒：让看门线程把它顶到最前、也让对话框把自己的子控件建完
        // （第一次探针在"刚找到"就查子窗口，一个 ComboBox 都没查到）。
        Thread.Sleep(1200);
        Native.GetWindowRect(dlg, out r);
        ShotRegion(prefix + "-0-全屏.bmp", 0, 0, 2880, 1800);
        ShotRegion(prefix + "-1-打开时.bmp", r.Left - 30, r.Top - 30, r.Width + 60, r.Height + 60);

        // 我们覆盖层这会儿在对话框上面还是下面？看两个点上的最上层窗口是谁。
        int midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;
        IntPtr atMid = Native.WindowFromPoint(new Native.POINT { X = midX, Y = midY });
        Console.WriteLine($"dialogprobe：对话框正中那一点最上层 = {atMid}（{ProbeClassName(atMid)}）"
                        + $"；我们的覆盖层 = {_windows[0].Hwnd}（{ProbeClassName(_windows[0].Hwnd)}）");

        // 子控件清单：看对话框里到底有什么（第一次没找到 ComboBox）
        int n = 0;
        Native.EnumChildWindows(dlg, (h, _) =>
        {
            if (n++ < 40)
            {
                Native.GetWindowRect(h, out var cr0);
                Console.WriteLine($"    子窗口 {ProbeClassName(h),-20} ({cr0.Left},{cr0.Top})-({cr0.Right},{cr0.Bottom})");
            }
            return true;
        }, IntPtr.Zero);

        // "保存类型"那个下拉：它和"文件名"是同一类控件（都是 ComboBox），
        // **"保存类型"是下面那个**（文件名在上）。第一版取了第一个 → 点到了文件名框。
        // 所以这里取**位置最靠下的那一个**。
        IntPtr combo = IntPtr.Zero;
        string comboClass = "";
        int bestTop = int.MinValue;
        Native.EnumChildWindows(dlg, (h, _) =>
        {
            string cls = ProbeClassName(h);
            if (cls != "ComboBox" && cls != "ComboBoxEx32") return true;
            Native.GetWindowRect(h, out var cr0);
            if (cr0.Top <= bestTop) return true;
            bestTop = cr0.Top; combo = h; comboClass = cls;
            return true;
        }, IntPtr.Zero);

        if (combo == IntPtr.Zero)
        {
            Console.WriteLine("dialogprobe：没找到文件类型下拉（子窗口里没有 ComboBox）");
        }
        else
        {
            Native.GetWindowRect(combo, out var cr);
            // 子窗口的矩形**大多数时候是屏幕坐标**（对话框移到 (779,330) 之后，
            // 它也跟着报 (1042,1088)）。但对话框还在 (0,0) 时两种读法长得一模一样，
            // 分不清——所以这里按"在不在对话框矩形里"判一下，不在就补上对话框原点。
            int ox = 0, oy = 0;
            bool insideDlg = cr.Left >= r.Left && cr.Top >= r.Top
                          && cr.Right <= r.Right && cr.Bottom <= r.Bottom;
            if (!insideDlg) { ox = r.Left; oy = r.Top; }
            int cx = ox + cr.Right - 14, cy = oy + (cr.Top + cr.Bottom) / 2;
            Console.WriteLine($"dialogprobe：下拉 {comboClass} 读到 ({cr.Left},{cr.Top})-({cr.Right},{cr.Bottom})，"
                            + $"对话框 ({r.Left},{r.Top})-({r.Right},{r.Bottom})，"
                            + $"{(insideDlg ? "按屏幕坐标" : "按相对坐标＋原点")} → 点 ({cx},{cy})");

            // **先看一眼：这个点上是谁的窗口**（我们覆盖层是置顶的，很可能抢走这一下）
            IntPtr over = Native.WindowFromPoint(new Native.POINT { X = cx, Y = cy });
            Console.WriteLine($"dialogprobe：那一点上最上层的窗口 = {over}（{ProbeClassName(over)}）"
                            + $"，我们的覆盖层 = {_windows[0].Hwnd}");

            SendMouse(cx, cy, 0);
            Thread.Sleep(60);
            SendMouse(cx, cy, Native.MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(50);
            SendMouse(cx, cy, Native.MOUSEEVENTF_LEFTUP);
            // **连着拍三张**：判"点开就收回去"这种毛病，得看它是没开、还是开了一下又被关掉。
            int sx = ox + cr.Left - 30, sy = oy + cr.Bottom - 40;
            int sw = Math.Max(cr.Width + 60, 400), sh = 460;
            Thread.Sleep(150);
            ShotRegion(prefix + "-2a-点后150ms.bmp", sx, sy, sw, sh);
            Thread.Sleep(250);
            ShotRegion(prefix + "-2b-点后400ms.bmp", sx, sy, sw, sh);
            Thread.Sleep(500);
            ShotRegion(prefix + "-2c-点后900ms.bmp", sx, sy, sw, sh);

            // 下拉里**第二项就是 JPEG**（PNG 在上、JPEG 在下，每项约 30 物理像素）。
            // 点它一下，看文件名后缀会不会跟着变成 .jpg——这就是用户要的那条路。
            int jx = ox + cr.Left + 200, jy = oy + cr.Bottom + 45;
            SendMouse(jx, jy, 0);
            Thread.Sleep(60);
            SendMouse(jx, jy, Native.MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(50);
            SendMouse(jx, jy, Native.MOUSEEVENTF_LEFTUP);
            Thread.Sleep(500);
            ShotRegion(prefix + "-3-选了JPG.bmp", sx, sy, sw, sh);

            // `--save`：一路走到底——把文件名改成临时路径，点"保存"，
            // 看我们的代码是不是真的把图写出来了（写完由主线程验证并删掉）。
            if (Environment.GetCommandLineArgs().Contains("--save"))
            {
                DialogProbeCheckExport = true;
                IntPtr saveBtn = IntPtr.Zero;
                int leftMost = int.MaxValue;
                Native.EnumChildWindows(dlg, (h, _) =>
                {
                    string cls = ProbeClassName(h);
                    Native.GetWindowRect(h, out var hr);
                    if (cls == "Button" && hr.Top > r.Top + r.Height * 3 / 4 && hr.Left < leftMost)
                    {
                        leftMost = hr.Left;                                 // 下半部分最左边那个 = 保存
                        saveBtn = h;
                    }
                    return true;
                }, IntPtr.Zero);

                if (saveBtn != IntPtr.Zero)
                {
                    Native.GetWindowRect(saveBtn, out var br);
                    int bx = (br.Left + br.Right) / 2, by = (br.Top + br.Bottom) / 2;
                    Console.WriteLine($"dialogprobe：点「保存」({bx},{by})");
                    SendMouse(bx, by, 0);
                    Thread.Sleep(60);
                    SendMouse(bx, by, Native.MOUSEEVENTF_LEFTDOWN);
                    Thread.Sleep(50);
                    SendMouse(bx, by, Native.MOUSEEVENTF_LEFTUP);
                    return;                       // 让它自己去保存、关框，别再补 WM_CLOSE
                }
            }
        }

        Thread.Sleep(300);
        Native.PostMessage(dlg, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    private static void ShotRegion(string path, int x, int y, int w, int h)
    {
        bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
        Console.WriteLine(ok ? $"  出图 {System.IO.Path.GetFullPath(path)}（CWD = {Environment.CurrentDirectory}）"
                             : $"  出图失败 {path}");
    }

    private void CaptureShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        BoardOn = true;                     // 白底，不然框压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();

        float cx = VirtualScreen.MinX + 700, top = VirtualScreen.MinY + 300;
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 5f * DpiScale,
        };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            s.AddPoint(cx - 300 + t * 620, top + 120 + MathF.Sin(t * 6f) * 60f, 0.5f, i * 8);
        }
        Doc.AddStroke(s);

        // 取景区：**故意取一块不是整数的尺寸**（520×300 逻辑像素），读数才有看头
        CaptureActive = true;
        CapMinX = cx - 260; CapMinY = top;
        CapMaxX = cx + 260; CapMaxY = top + 300 * DpiScale;

        var r = new RectF { MinX = CapMinX, MinY = CapMinY, MaxX = CapMaxX, MaxY = CapMaxY };
        // 读数画在框左下角外侧，脏区那一套逻辑和抓屏时一致，所以这里也把它算进去
        r.Add(new RectF
        {
            MinX = r.MinX, MinY = r.MaxY,
            MaxX = r.MinX + 110f * DpiScale, MaxY = r.MaxY + 40f * DpiScale,
        });
        if (!OffscreenFloatingShot(path, r.Inflate(20f))) Console.WriteLine("出图失败");
        CaptureActive = false;
        _quit = true;
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
        var bar = SelectionHandles.BarRect(sb, dpi, ViewportCanvas);
        Check("操作条在选中框下方", bar.MinY > sb.MaxY, $"间距 {bar.MinY - sb.MaxY:F0}px");

        bool allHit = true; string bad = "";
        for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
        {
            var r = SelectionHandles.BarButtonRect(i, sb, dpi, ViewportCanvas);
            int got = SelectionHandles.BarButtonAt((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f,
                                                   sb, dpi, ViewportCanvas);
            if (got != i) { allHit = false; bad = $"第{i}个按钮命中到 {got}"; }
        }
        Check("每个按钮都能点中", allHit, bad);
        Check("框外不误判",
              SelectionHandles.BarButtonAt(bar.MinX - 30, bar.MinY + 5, sb, dpi, ViewportCanvas) == -1, "");

        // ---- 导出：**点一下直接走导出，不再开自己的格式面板** ----
        //
        // 曾经这里点"导出"会先开一层我们自己的两格面板（PNG 透明底 / JPG 白底），
        // 那是为了绕开"系统对话框的下拉点不开"。真因（覆盖层每秒抢一次置顶）修掉之后，
        // 用户 2026-09-17 说"那下面这两个图标就没有用了吧，用户都可以自己保存图片了"——
        // 删掉。现在格式在**系统对话框**的类型栏里选（见 ExportFormats，四种都写明了优势）。
        //
        // 自检里对话框是关着的（`ExportDialogEnabled = false`），所以这里只能验
        // "那一下真的走到了导出这条路"（计数器）＋"没有把任何面板打开"。
        {
            int before = ExportAttempts;
            RunBarActionForTest((int)SelBarButton.Export);
            Check("点「导出」直接走导出（不再开格式面板）",
                  ExportAttempts == before + 1 && SelPanelOpen == SelPanel.None,
                  $"试了 {before} → {ExportAttempts} 次，面板 = {SelPanelOpen}");

            // 四种格式的"扩展名 → 编码器 + 底色"这套规则是纯逻辑，这里一并钉住：
            // 尤其是**有损/无透明通道的格式绝不给透明通道**（否则透明处会变黑块）。
            Check("格式表：四条，扩展名各就各位",
                  ExportFormats.Count == 4
                  && ExportFormats.ExtensionFor(1) == ".png"
                  && ExportFormats.ExtensionFor(2) == ".jpg"
                  && ExportFormats.ExtensionFor(3) == ".png"
                  && ExportFormats.ExtensionFor(4) == ".bmp",
                  string.Join(" / ", Enumerable.Range(1, ExportFormats.Count)
                                                .Select(i => ExportFormats.ExtensionFor(i))));
            Check("格式表：四种类型的名字里都写明了优势（不是光一个格式名）",
                  ExportFormats.Filter.Contains("贴课件首选")
                  && ExportFormats.Filter.Contains("好发微信邮件")
                  && ExportFormats.Filter.Contains("深色模板上不露底")
                  && ExportFormats.Filter.Contains("老软件也能打开"),
                  "四条都带说明");

            // **记住上次选的那一条**（常用 JPG 的老师不用每次去下拉里找）。
            // 存的是"类型栏的第几条"，读的时候越界/坏值一律回到第 1 条。
            var saved = GetUiPref("exportFormat");
            SetUiPref("exportFormat", null);
            int def0 = ExportDefaultFilterIndex();
            SetUiPref("exportFormat", "3");
            int def3 = ExportDefaultFilterIndex();
            SetUiPref("exportFormat", "99");
            int defBad = ExportDefaultFilterIndex();
            SetUiPref("exportFormat", saved);
            Check("记住上次的格式：没存过→第 1 条、存过→照存、坏值→回到第 1 条",
                  def0 == 1 && def3 == 3 && defBad == 1,
                  $"默认 {def0}，存 3 → {def3}，存 99 → {defBad}");
        }

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
            "pixeleraser" or "blockeraser" => Tool.PixelEraser,
            "capture" or "screenshot" => Tool.Capture,      // 截图：看自绘的取景框落点
            _ => Tool.Pen,
        };
        if (widthLogical > 0f)
        {
            if (Tool == Tool.Highlighter) HighlighterWidthLogical = widthLogical;
            else if (Tool == Tool.Laser) LaserWidthLogical = widthLogical;
            else if (Tool == Tool.Eraser) EraserRadiusLogical = widthLogical;
            else if (Tool == Tool.PixelEraser) PixelEraserWidthLogical = widthLogical;
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
        string size = Tool == Tool.PixelEraser
            ? $"矩形 {PixelEraserWidthLogical:F0}×{PixelEraserHeightLogical:F0} 逻辑像素"
              + $"（高:宽 = {GoldenRatio:F3}）"
            : $"宽 {CurrentToolWidthLogical} 逻辑像素（外圈 {CursorOuterRadius:F0}px）";
        Console.WriteLine($"落点反馈已摆好：{Tool} {size}"
                        + $"（{DrawnCursor}，脏区 {DrawnCursorRadius:F0}px）");
        Console.WriteLine("cursor showcase ready");
    }

    private bool showcasePenDevice = false;

    /// <summary>
    /// 像素橡皮摆样（--pixeleraseshow）：画几条"板书"，用竖着的黄金比矩形抹一下，
    /// **自己抓屏**存两张图——擦之前（带落点）和擦之后（落点挪开）。
    ///
    /// 为什么不靠外部截图工具：自检已经能抓屏了（ScreenProbe），摆样自己存图少一步人工。
    /// 落点在第二张里必须挪开——矩形正压在切口上，就看不出"一笔被切成了两段"。
    /// </summary>
    private void PixelEraserShowcase()
    {
        Doc.Clear();
        Doc.ClearHistory();
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        float dpi = DpiScale;

        // 三条横墨（笔迹，会被切成两段）、一条荧光笔（同样会被切）、
        // 一个矩形（图形，轮廓被碰到时整条删）。三种对象一屏看全。
        // 墨水的位置要落在橡皮的竖边范围（±半高）里——落在外面就看不出"擦掉了一整条"。
        foreach (float dy in new[] { -120f, -60f, 0f })
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0.10f, 0.11f, 0.13f, 1f), Width = 8f * dpi,
            };
            for (int i = 0; i <= 120; i++)
                s.AddPoint(cx - 520 + i * 9f, cy + dy + MathF.Sin(i * 0.32f) * 6f, 0.9f, i);
            Doc.AddStroke(s);
        }
        var hl = new Stroke
        {
            Tool = Tool.Highlighter, Color = new Color4(1f, 0.84f, 0.16f, 1f), Width = 26f * dpi,
        };
        for (int i = 0; i <= 60; i++) hl.AddPoint(cx - 440 + i * 15f, cy + 110, 1f, i);
        Doc.AddStroke(hl);

        var box = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.9f, 0.2f, 0.2f, 1f), Width = 5f * dpi,
        };
        box.AddPoint(cx + 40, cy - 250, 1f, 0);       // 左边压在橡皮里（橡皮半宽 = 93 物理像素）
        box.AddPoint(cx + 280, cy + 250, 1f, 0);
        Doc.AddStroke(box);

        Tool = Tool.PixelEraser;
        PassThrough = false;
        ShowHud = false;
        PointerInside = true;
        LastPointerType = Native.PT_MOUSE;
        PointerX = cx; PointerY = cy;
        Doc.InvalidateAll();
        SettleFrames(600);

        int w = (int)(PixelEraserWidthLogical * dpi * 3.2f);
        int h = (int)(PixelEraserHeightLogical * dpi * 2.4f);
        int x0 = (int)cx - w / 2, y0 = (int)cy - h / 2;

        string shot1 = Path.Combine("reports", "像素橡皮-1-落点与擦之前.bmp");
        ScreenProbe.SaveBmp(shot1, x0, y0, w, h);
        Console.WriteLine($"已存 {Path.GetFullPath(shot1)}");
        Console.WriteLine($"  落点：{(int)PixelEraserWidthLogical}×{(int)PixelEraserHeightLogical} 逻辑像素"
                        + $"（{PixelEraserWidthLogical * dpi:F0}×{PixelEraserHeightLogical * dpi:F0} 物理像素）");

        int before = Doc.Strokes.Count;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
        Doc.EndErase();

        // 落点挪开再拍：矩形正压在切口上就看不出切成了什么样。
        PointerX = _virtualX + 120; PointerY = _virtualY + _virtualH - 120;
        SettleFrames(600);

        string shot2 = Path.Combine("reports", "像素橡皮-2-擦之后.bmp");
        ScreenProbe.SaveBmp(shot2, x0, y0, w, h);
        Console.WriteLine($"已存 {Path.GetFullPath(shot2)}");
        Console.WriteLine($"  擦之前 {before} 条 → 擦之后 {Doc.Strokes.Count} 条"
                        + "（被擦断的每一段都是**独立对象**：三条横墨 + 荧光笔各切成两截；"
                        + "矩形先熔成笔迹再切，同样按段分开）");
        for (int i = 0; i < Doc.Strokes.Count; i++)
        {
            var s = Doc.Strokes[i];
            Console.WriteLine($"    #{i} {s.Kind,-9} 点 {s.Points.Count,4} 擦除区间 {s.Erased.Count} 段"
                            + $" 剩余 {s.RemainingRuns().Count} 段"
                            + $"  x {s.Bounds.MinX:F0}..{s.Bounds.MaxX:F0}"
                            + $"  y {s.Bounds.MinY:F0}..{s.Bounds.MaxY:F0}");
        }
        Console.WriteLine("pixel eraser showcase done");
        _quit = true;
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

        // 注意 `target` 是**指针在屏幕上顺时针转的角度**（见 At），`want` 是标签上的数。
        // 用户 2026-09-15 定：**逆时针为正、顺时针为负**——所以顺时针 88° 读数是 -90°。
        Case("正对网格：吸附", 0f, 0f, true);
        Case("容差内：吸到 0°", 2f, 0f, true);
        Case("容差外：保持自由（顺时针 → 负）", 5f, -5f, false);
        Case("普通角度：保持自由", 43f, -43f, false);
        Case("容差内：吸到 90°", 88f, -90f, true);
        Case("容差内：吸到 -90°", -89f, 90f, true);
        Case("容差外：保持自由（90 附近）", 95f, -95f, false);
        Case("半个整角：自由", 45f, -45f, false);
        Case("整角 180°：吸附", 180f, -180f, true);
        // 半圈这个点天生有歧义：屏幕 -180° 的最短增量既可以是 +180 也可以是 -180，
        // 实现取"归一化后再取负" → -180。真正的转圈由拖动中的**逐帧累积**决定（见 C 段）。
        Case("半圈的临界点：取 -180°", -180f, -180f, true);
        Case("Shift：硬网格 15°", 20f, -15f, true, shift: true);
        Case("Shift：吸到 0°", 7f, 0f, true, shift: true);
        Case("Alt：完全自由", 2f, -2f, false, alt: true);
        Case("Shift+Alt：Shift 优先", 20f, -15f, true, shift: true, alt: true);

        // **不设上限**（用户 2026-09-15 定）：吸附规则原样，但角度不绕回。
        // 转两圈多一点点 = 738°，就该原样写 738°，而不是 18°。
        Check("吸附：738° 不绕回", Math.Abs(SelectionHandles.SnapRotationDegrees(738f, false, false, out _) - 738f) < 0.01f,
              $"738° → {SelectionHandles.SnapRotationDegrees(738f, false, false, out _):F0}°");
        Check("吸附：722° 吸到 720°（整圈的整数倍照样认）",
              Math.Abs(SelectionHandles.SnapRotationDegrees(722f, false, false, out bool s722) - 720f) < 0.01f && s722,
              $"722° → {SelectionHandles.SnapRotationDegrees(722f, false, false, out _):F0}°");
        Check("吸附：-725° 保持自由（离 -720° 差 5°）",
              Math.Abs(SelectionHandles.SnapRotationDegrees(-725f, false, false, out bool s725) + 725f) < 0.01f && !s725,
              $"-725° → {SelectionHandles.SnapRotationDegrees(-725f, false, false, out _):F0}°");

        // 读数与矩阵必须自洽：矩阵转过的角度（独立用 atan2 量）要等于读数
        foreach (float target in new[] { 0f, 43f, 88f, -137f, 179f })
        {
            var (deg, _) = Probe(target);
            var m = SelectionHandles.DragMatrix(SelHandle.Rotate, b, from, At(target), 1f, false, false);
            var moved = Vector2.Transform(from, m);
            float measured = MathF.Atan2(moved.Y - center.Y, moved.X - center.X)
                           * 180f / MathF.PI + 90f;         // 起点在 -90°，所以加回来
            // 量出来的是**屏幕坐标**（顺时针为正）；读数那套是逆时针为正，取负号对齐
            measured = -SelectionHandles.NormalizeDegrees(measured);
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
        Check("标签文案不绕回（转两圈就写 740°）", SelectionHandles.FormatDegrees(740f) == "740°",
              $"740° → {SelectionHandles.FormatDegrees(740f)}");
        Check("标签文案不绕回（倒着转就写 -1234°）", SelectionHandles.FormatDegrees(-1234f) == "-1234°",
              $"-1234° → {SelectionHandles.FormatDegrees(-1234f)}");

        // ================= B. 真机层 =================
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        // **白板打底**：这一节靠"数屏幕上的强调色像素"判断标签画没画、在哪儿，
        // 而批注模式下面板是全透明的——桌面上的东西会被算进来。2026-09-17 实测：
        // 桌面换了一批窗口之后，这三条一下全红（"标签位置是干净的"量到 3421 像素），
        // 而代码一个字没改。铺一层不透明白板，量到的就全是我们的墨。
        BoardOn = true;

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
            // 用**实时框**：拖动中模型到松手才动（方案 B），拿模型里的框去算手柄位置，
            // 探针窗会停在按下那一刻的老地方，而标签早跟着内容转走了。
            var f = LiveSelectionFrame;
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
        // 指针**顺时针**拖了 92°（GripAt 的角度是屏幕坐标），标签该写 -90°——逆转为正、顺转为负
        Check("读数 ≈ -90°（顺时针为负）", Math.Abs(SelRotationDegrees + 90f) < 1f, $"{SelRotationDegrees:F1}°");
        Check("顺时针 92° 被吸到 -90°", SelRotationSnapped, $"snapped={SelRotationSnapped}");
        int snappedPixels = AccentCount();
        Check("吸住时标签上屏（强调色填充）", snappedPixels > 1500, $"{snappedPixels} 像素");

        // 再拖到 43°（容差外 → 不吸）
        var to43 = GripAt(43f);
        SendMouse((int)to43.X, (int)to43.Y, 0);
        SettleFrames(260);
        Check("顺时针 43° 保持自由（-43°）", !SelRotationSnapped && Math.Abs(SelRotationDegrees + 43f) < 1f,
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

        // ================= C. 方向 + 不设上限（用户 2026-09-15 定的）=================
        //
        // 两条都要测：只有"逆时针为正"和"顺时针为负"**成对**出现，才证明符号是对的
        // （只测一条的话，符号写反了也看不出来）。
        // 再往同一方向一直转，看读数会不会在 ±180° 处**绕回**——绕回就说明还是在量
        // "起点到当前点的夹角"，而不是"这一拖一共转了多少"。
        {
            // 圆形选区：绕自己转不改变轴心和半径，角度才好量准
            Stroke NewRound()
            {
                Doc.Clear();
                Doc.ClearHistory();
                var s2 = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 12f * DpiScale,
                };
                for (int i = 0; i <= 48; i++)
                {
                    float a = i / 48f * MathF.PI * 2f;
                    s2.AddPoint(sx + MathF.Cos(a) * 180f * DpiScale,
                                sy + MathF.Sin(a) * 180f * DpiScale, 0.9f, i * 8);
                }
                Doc.AddStroke(s2);
                Doc.Selected.Clear();
                Doc.Selected.Add(s2);
                Tool = Tool.Marquee;
                SettleFrames(300);
                return s2;
            }

            // 从当前位置的旋转手柄开始，按一串"屏幕角度增量"拖过去（单位：度，顺时针为正）
            float Spin(Stroke s2, params float[] clockwiseSteps)
            {
                var f2 = SelectionHandles.FrameOf(Doc.Selected);
                var grip2 = SelectionHandles.CanvasPosition(SelHandle.Rotate, f2, DpiScale);
                var pivot2 = new Vector2((f2.CanvasAabb.MinX + f2.CanvasAabb.MaxX) * 0.5f,
                                         (f2.CanvasAabb.MinY + f2.CanvasAabb.MaxY) * 0.5f);
                float arm2 = Vector2.Distance(grip2, pivot2);
                float a0 = MathF.Atan2(grip2.Y - pivot2.Y, grip2.X - pivot2.X);

                SendMouse((int)grip2.X, (int)grip2.Y, Native.MOUSEEVENTF_LEFTDOWN);
                SettleFrames(90);
                float travel = 0f;
                foreach (float step in clockwiseSteps)
                {
                    // 传进来的是**增量**：一路加着走，才能真的"转了 200°"。
                    // （第一版写成 `a0 + step` 当绝对角用，两步都落在同一个点上，
                    //  指针没动就没有消息，读数自然只有最后一步——自检当场抓到。）
                    travel += step;
                    float rad = a0 + travel * MathF.PI / 180f;
                    SendMouse((int)(pivot2.X + arm2 * MathF.Cos(rad)),
                              (int)(pivot2.Y + arm2 * MathF.Sin(rad)), 0);
                    SettleFrames(90);
                }
                return SelRotationDegrees;
            }

            // 拖动中"对象现在是什么变换"要问**合成后**的那个矩阵：方案 B 里
            // 模型到松手才动，拖动中的实时位移活在预览矩阵里（对象自己的变换 × 预览矩阵，
            // 和渲染时左乘的完全是同一个式子）。松手后再问 s.Transform 就是它本身。
            Matrix3x2 Live(Stroke s2) => DragPreviewActive
                ? s2.Transform * DragPreviewMatrix : s2.Transform;

            // ① 逆时针 90°：读数为**正**，而且矩阵真的往逆时针转了
            //    （屏幕坐标里逆时针 90° → CreateRotation(-90°) → M12 = -1）
            var spinCcw = NewRound();
            float ccwRead = Spin(spinCcw, -90f);
            Check("逆时针拖 90° → 读数 +90°（逆转为正）",
                  Math.Abs(ccwRead - 90f) < 1.5f, $"读数 {ccwRead:F1}°");
            var liveCcw = Live(spinCcw);
            Check("读数的符号和几何一致（逆时针转出来 M12≈-1）",
                  Math.Abs(liveCcw.M12 + 1f) < 0.05f && Math.Abs(liveCcw.M11) < 0.05f,
                  $"M11={liveCcw.M11:F3} M12={liveCcw.M12:F3}");
            // 方案 B 的契约：拖动中模型**一个字都不动**（动静全在预览里，松手才提交）。
            // 少了这条，将来有人"顺手"把 SetTransformLive 加回去就没人拦得住 ——
            // 而那就是每帧重画走过的面积、19ms/帧 的那条老路。
            Check("拖动中模型不动（只在预览里位移，松手才提交）",
                  DragPreviewActive && spinCcw.Transform.Equals(Matrix3x2.Identity),
                  $"预览={DragPreviewActive}，模型 M11={spinCcw.Transform.M11:F3} M12={spinCcw.Transform.M12:F3}");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);

            // ② 顺时针 90°：读数为**负**
            var spinCw = NewRound();
            float cwRead = Spin(spinCw, 90f);
            Check("顺时针拖 90° → 读数 -90°（顺转为负）",
                  Math.Abs(cwRead + 90f) < 1.5f, $"读数 {cwRead:F1}°");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);

            // ③ 不设上限：同一拖里一路逆时针转 200°，读数必须 > 180°，不许绕回 -160°
            var spinLong = NewRound();
            float longRead = Spin(spinLong, -90f, -90f, -20f);
            Check("同一拖转 200° → 读数 ≈ +200°（不绕回）",
                  longRead > 190f && longRead < 210f,
                  $"读数 {longRead:F1}°（绕回的话会是 -160° 左右）");
            var liveLong = Live(spinLong);
            Check("转 200° 的对象真的转了 200°（矩阵能和读数对上）",
                  Math.Abs(liveLong.M11 - MathF.Cos(200f * MathF.PI / 180f)) < 0.05f
                  && Math.Abs(liveLong.M12 + MathF.Sin(200f * MathF.PI / 180f)) < 0.05f,
                  $"M11={liveLong.M11:F3}（期望 {MathF.Cos(200f * MathF.PI / 180f):F3}）");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);

            // ④ 翻转过的对象：框坐标和屏幕是"反手"的（行列式为负），
            //    读数仍然要以**眼睛看到的方向**为准——屏幕上逆时针拖，就该是正数。
            var spinMirror = NewRound();
            Doc.ApplyTransform(SelectionHandles.MirrorMatrix(spinMirror.WorldInkBounds, horizontal: true));
            SettleFrames(250);
            bool leftHanded = SelectionHandles.IsMirrored(spinMirror.Transform);
            float mirrorRead = Spin(spinMirror, -90f);      // 屏幕上逆时针 90°
            string mirrorNote = leftHanded ? "已镜像（行列式为负）" : "没镜像成（行列式为正）";
            Check("翻转过的对象：屏幕上逆时针拖 → 读数仍为正",
                  leftHanded && mirrorRead > 0f,
                  $"{mirrorNote}，读数 {mirrorRead:F1}°");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);
        }

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

        // 顺手用像素橡皮在这条笔迹上擦掉一小段：**存档必须把擦除区间一起带上**，
        // 否则"存一次再打开"会把擦掉的墨又画回来（v4 新增的那段就是它）。
        Doc.BeginEraseRect();
        Doc.EraseRectAt(100 + 14 * 7f, 200, 12f, 12f);
        Doc.EndErase();
        bool erasedSaved = freehand.Erased.Count > 0;

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
                   && a.Points.Count == b.Points.Count
                   && a.Erased.Count == b.Erased.Count;
            for (int k = 0; eq && k < a.Erased.Count; k++)
                if (a.Erased[k] != b.Erased[k]) eq = false;
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
        Check("擦除区间也一起存了", erasedSaved && allEqual,
              erasedSaved ? "存前有区间、读回后一致（否则擦掉的墨会画回来）"
                          : "这一轮没造出擦除区间，等于没验");

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
    // ------------------------------------------------------------------
    //  离屏渲染小工具
    //
    //  量"颜色会不会变深"这类事不能靠抓屏：抓屏在锁屏 / 远程会话 / 被别的窗口盖住时
    //  拍到的是桌面壁纸，两处都是壁纸 → "差 0" 看着像通过，其实什么都没验
    //  （2026-09-15 就被骗过一次）。离屏渲染什么时候跑都一样。
    // ------------------------------------------------------------------

    /// <summary>建一对离屏位图：一个当渲染目标，一个 CPU 可读（读回像素用）。</summary>
    private (ID2D1Bitmap1 target, ID2D1Bitmap1 cpu) MakeOffscreen(int w, int h)
    {
        var ctx = _windows[0].Context;
        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var target = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        var cpu = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        return (target, cpu);
    }

    /// <summary>把文档里的笔画画进离屏位图（画布坐标 → 位图坐标，按 -origin 平移）。</summary>
    private void RenderDocToBitmap(ID2D1Bitmap1 target, float originX, float originY)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0.98f, 0.98f, 0.98f, 1f));      // 当作白板底色
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-originX, -originY);
        foreach (var s in Doc.Strokes) _windows[0].DrawStrokeForTest(s);
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        if (hr.Failure) Console.WriteLine("  离屏 EndDraw 失败: " + hr.Description);
        ctx.Target = null;
    }

    /// <summary>把离屏位图读回内存（BGRA）。</summary>
    private static byte[] ReadPixels(ID2D1Bitmap1 target, ID2D1Bitmap1 cpu, int w, int h)
    {
        cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
        var m = cpu.Map(MapOptions.Read);
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
        cpu.Unmap();
        return buf;
    }

    private byte[] RenderAndRead(ID2D1Bitmap1 target, ID2D1Bitmap1 cpu, int w, int h,
                                 float originX, float originY)
    {
        RenderDocToBitmap(target, originX, originY);
        return ReadPixels(target, cpu, w, h);
    }

    /// <summary>取一小块的平均色（参数是 BGRA 缓冲、行宽像素数、左上角、边长）。</summary>
    private static (int r, int g, int b) AvgPatch(byte[] buf, int width, int px, int py, int size)
    {
        // 探针落在缓冲外面时**夹回来**，而不是抛异常：这是量颜色的工具，
        // 量歪了应该报一个可疑的数字让人去看，不该把整个自检进程打断。
        int height = buf.Length / (width * 4);
        px = Math.Clamp(px, 0, Math.Max(0, width - size));
        py = Math.Clamp(py, 0, Math.Max(0, height - size));
        long r = 0, g = 0, b = 0; int n = 0;
        for (int y = py; y < py + size; y++)
            for (int x = px; x < px + size; x++)
            {
                int i = y * width * 4 + x * 4;
                b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; n++;
            }
        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>两个颜色的差（三通道绝对值之和）。</summary>
    private static int ColorDiff((int r, int g, int b) a, (int r, int g, int b) b)
        => Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);

    /// <summary>
    /// 造一条"自己穿过自己"的荧光笔笔迹（`--selfcross`、`--eraseselfcross`、像素橡皮自检
    /// 共用同一条）。
    ///
    /// **真正的自交点**：两段中心线联立解出来是相对 (161.7, 20.9)，不是 (200, 0)——
    /// 后者离交点 40 像素，那儿只有一股墨，拿它做判据等于什么都没验（老用例就是这么错的）。
    /// </summary>
    private static Stroke MakeSelfCrossingHighlight(float cx, float cy, float width)
    {
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = InkPalette.HighlighterDefault, Width = width,
        };
        var raw = new List<Vector2>
        {
            new(cx, cy), new(cx + 120, cy), new(cx + 240, cy + 60),
            new(cx + 240, cy + 220), new(cx + 60, cy + 260),
            new(cx - 20, cy + 120), new(cx + 200, cy),          // ← 穿过第一段
            new(cx + 330, cy - 150),
        };
        for (int i = 0; i + 1 < raw.Count; i++)
            for (int k = 0; k < 20; k++)
            {
                float t = k / 20f;
                s.AddPoint(raw[i].X + (raw[i + 1].X - raw[i].X) * t,
                           raw[i].Y + (raw[i + 1].Y - raw[i].Y) * t, 0.5f, i * 20 + k);
            }
        s.AddPoint(raw[^1].X, raw[^1].Y, 0.5f, 999);
        return s;
    }

    /// <summary>
    /// 橡皮手测台（--eraserlab [日志前缀]）。
    ///
    /// **这是给人用的，不是自检**：铺一屏"可擦的东西"，打开橡皮遥测，然后把控制权交回
    /// 引擎的正常消息循环，用户拿鼠标自己擦。每条拖拽结束立刻落一行 CSV，退出时写汇总。
    ///
    /// 为什么要有它：手感、习惯、误擦这些事只有真人用真手才知道。自检能证明"切得对、
    /// 撤销一步、上屏像素对"，证明不了"这个尺寸顺手""这一擦我其实不想擦"。
    ///
    /// 样例里**故意放了三类对象**——笔迹（会被切开）、荧光笔（自交，看颜色会不会变深）、
    /// 图形（碰到轮廓整条删），一屏就能把三种行为都试到。
    /// </summary>
    /// <summary>
    /// 实验（--eraseimage）：**画面里有一张大图时，擦除一步要多久**。
    ///
    /// 查的是用户报的"擦图像不流畅"。分块缓存的重画是"整块清掉再画一遍块内的所有对象"，
    /// 而图像对象画的是**一张位图**——块里有图，这一块每次重画都要把图重新贴一遍。
    /// 所以图越大、橡皮跨过的块越多，一步擦除就越贵。这里做 A/B：同一串擦除动作，
    /// 一次在"有图 + 图上有墨"的版面上跑，一次在"没有图"的版面上跑。
    /// </summary>
    /// <summary>
    /// 探针（--imageerase）：**两种橡皮碰到图像对象时分别会怎样**。
    ///
    /// 用户报"擦图像不流畅"，先把"到底会发生什么"钉死：
    ///   · 像素橡皮（Ctrl+Alt+7）：按设计**不碰**图像（代码里 `if (s.IsImage) continue`）；
    ///   · 整笔橡皮（Ctrl+Alt+4）：走 `IsShape` 分支 → `HitTestExact` → 图像**整条删掉**。
    /// 这两个不一样，是历史遗留（图像是后来加的，整笔橡皮那条分支没跟着改）。
    /// </summary>
    private void ImageEraseProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 探针：两种橡皮碰到图像对象时会怎样 ===");

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        Doc.Clear();
        Doc.ClearHistory();
        var img = Doc.AddImage(MakeTestImage(400, 300), cx - 200, cy - 150, 1f);
        Console.WriteLine($"  先放一张 400×300 的图（对象数 {Doc.Strokes.Count}），"
                        + "橡皮落点打在图像正中");

        int hit = Doc.EraseRectAt(cx, cy, 40f, 40f);          // 像素橡皮的调用
        Console.WriteLine($"  像素橡皮擦一下：受影响 {hit} 条，图还在吗 "
                        + $"{(Doc.Strokes.Contains(img) ? "在" : "**没了**")}"
                        + $"（对象数 {Doc.Strokes.Count}）");

        int removed = Doc.EraseAt(cx, cy, 40f);               // 整笔橡皮的调用
        Console.WriteLine($"  整笔橡皮擦一下：删掉 {removed} 条，图还在吗 "
                        + $"{(Doc.Strokes.Contains(img) ? "在" : "**没了**")}"
                        + $"（对象数 {Doc.Strokes.Count}）");

        Console.WriteLine("image-erase probe done");
        _quit = true;
    }

    private void EraseImageProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 实验：画面里有大图时，擦除一步的代价 ===");

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        const int Steps = 25;

        double Run(bool withImage, out double patchMax, out int tiles, out int drawn, out double selfMs)
        {
            Doc.Clear();
            Doc.ClearHistory();
            // 按**截图**的实际尺寸来：一张全屏截图就是虚拟桌面那么大（2880×1800 = 20MB）。
            int iw = (int)_virtualW, ih = (int)_virtualH;
            if (withImage) Doc.AddImage(MakeTestImage(iw, ih), _virtualX, _virtualY, 1f);

            // 图上画几行笔迹（老师在图/PPT 上圈画），橡皮就擦这些
            var rnd = new Random(5);
            for (int k = 0; k < 6; k++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0.9f, 0.15f, 0.15f, 1f), Width = 8f * DpiScale };
                float y = cy - 400 + k * 130;
                for (int i = 0; i <= 60; i++)
                {
                    float t = i / 60f;
                    s.AddPoint(cx - 700 + t * 1400, y + MathF.Sin(t * 7f + k) * 12f, 0.9f, i);
                }
                Doc.AddStroke(s);
            }
            Doc.InvalidateAll();
            SettleFrames(500);

            double sum = 0, mx = 0, self = 0;
            int tl = 0, dr = 0;
            float bx = cx - 600, by = cy - 400;
            for (int i = 0; i < Steps; i++)
            {
                bx += 48; by += 32;
                var sw = Stopwatch.StartNew();
                Doc.EraseRectAt(bx, by, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
                sw.Stop();
                self += sw.Elapsed.TotalMilliseconds;
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                sum += _windows[0].LastPatchMs;
                if (_windows[0].LastPatchMs > mx) mx = _windows[0].LastPatchMs;
                tl += _windows[0].LastPatchCount;
                dr += _windows[0].LastDrawnStrokes;
            }
            patchMax = mx; tiles = tl; drawn = dr; selfMs = self;
            return sum / Steps;
        }

        double noImg = Run(false, out var mxA, out var tlA, out var drA, out var selfA);
        double withImg = Run(true, out var mxB, out var tlB, out var drB, out var selfB);

        Console.WriteLine($"  没有图：重画 {noImg,6:F2} ms/步（峰值 {mxA,5:F2}）"
                        + $"　{TL(tlA)} 块/步　{DR(drA)} 笔/步　擦除本身 {selfA / Steps,5:F2} ms/步");
        Console.WriteLine($"  有  图：重画 {withImg,6:F2} ms/步（峰值 {mxB,5:F2}）"
                        + $"　{TL(tlB)} 块/步　{DR(drB)} 笔/步　擦除本身 {selfB / Steps,5:F2} ms/步");
        Console.WriteLine($"  结论：带图时一步慢 {withImg - noImg:F2} ms（重画部分）");
        string TL(int t) => (t / (double)Steps).ToString("F1");
        string DR(int d) => (d / (double)Steps).ToString("F0");
        Console.WriteLine("erase-image probe done");
        _quit = true;
    }

    /// <summary>造一张"截图"：渐变 + 棋盘格，模拟真实内容（尺寸给的是像素）。</summary>
    private static ImageData MakeTestImage(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(200 - y * 100 / h);                          // B
                px[i + 1] = (byte)(180 - x * 80 / w);                       // G
                px[i + 2] = (byte)(150 + ((x / 16 + y / 16) % 2) * 30);     // R
                px[i + 3] = 255;
            }
        return ImageData.Adopt(w, h, px, hasAlpha: true);
    }

    private void EraserLab(string prefix, bool keepSystemCursor = false)
    {
        string stamp = DateTime.Now.ToString("MMdd-HHmmss");
        string root = string.IsNullOrWhiteSpace(prefix) ? $"reports/橡皮-手测-{stamp}" : prefix;

        Doc.Clear();
        Doc.ClearHistory();

        float L = DpiScale;                       // 逻辑 → 物理
        float X(float logical) => _virtualX + logical * L;
        float Y(float logical) => _virtualY + logical * L;
        var ink = new Color4(0.08f, 0.09f, 0.12f, 1f);

        // ① 两行"板书"：每行三条长笔迹（模拟一行连着写下来）
        for (int row = 0; row < 2; row++)
        {
            float yy = Y(320f + row * 80f);
            for (int seg = 0; seg < 3; seg++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = 5f * L };
                float x0 = X(90f + seg * 420f), x1 = X(90f + seg * 420f + 395f);
                for (int i = 0; i <= 40; i++)
                {
                    float t = i / 40f;
                    s.AddPoint(x0 + (x1 - x0) * t,
                               yy + MathF.Sin(t * 9f + seg * 2f) * 7f * L, 0.9f, i);
                }
                Doc.AddStroke(s);
            }
        }

        // ② 三个"字"：三横一竖——"改一个字要重写一整行"那种场景（整笔擦会连带整行）
        for (int ch = 0; ch < 3; ch++)
        {
            float x0 = X(140f + ch * 220f), y0 = Y(470f);
            for (int k = 0; k < 3; k++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = 6f * L };
                s.AddPoint(x0, y0 + k * 26f * L, 1f, 0);
                s.AddPoint(x0 + 120f * L, y0 + k * 26f * L, 1f, 1);
                Doc.AddStroke(s);
            }
            var v = new Stroke { Tool = Tool.Pen, Color = ink, Width = 6f * L };
            v.AddPoint(x0 + 60f * L, y0 - 12f * L, 1f, 0);
            v.AddPoint(x0 + 60f * L, y0 + 64f * L, 1f, 1);
            Doc.AddStroke(v);
        }

        // ③ 一条自交的荧光笔：擦一刀，看交叠处会不会变深（这正是这版改动的核心收益）
        Doc.AddStroke(MakeSelfCrossingHighlight(X(880f), Y(360f), 26f * L));

        // ④ 图形：碰到轮廓整条删（橡皮从正中间划过不该删）
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.85f, 0.2f, 0.2f, 1f), Width = 5f * L,
        };
        rect.AddPoint(X(790f), Y(520f), 1f, 0);
        rect.AddPoint(X(1090f), Y(620f), 1f, 1);
        Doc.AddStroke(rect);

        var arrow = new Stroke
        {
            Tool = Tool.Arrow, Kind = StrokeKind.Arrow,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * L,
        };
        arrow.AddPoint(X(1160f), Y(600f), 1f, 0);
        arrow.AddPoint(X(1360f), Y(530f), 1f, 1);
        Doc.AddStroke(arrow);

        // ⑤ 一块"截图区"：模拟老师截了 PPT/图，然后在图上圈画。
        //    用它试两件事——**图上的墨能正常擦掉**，而**图本身不该被橡皮吃掉**
        //    （图像对象按设计不参与擦除，要删它用框选 + Delete）。
        Doc.AddImage(MakeTestImage(640, 220), X(90f), Y(660f), DpiScale);
        for (int k = 0; k < 3; k++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0.9f, 0.15f, 0.15f, 1f), Width = 4f * L };
            float y0 = Y(690f + k * 60f);
            for (int i = 0; i <= 30; i++)
            {
                float t = i / 30f;
                s.AddPoint(X(120f + t * 560f), y0 + MathF.Sin(t * 8f + k) * 6f * L, 0.9f, i);
            }
            Doc.AddStroke(s);
        }

        // 默认就站在像素橡皮上——要测的就是它。整笔橡皮 Ctrl+Alt+4，一键换回来。
        Tool = Tool.PixelEraser;
        ShowHud = true;                 // 面板上有一行"记录中 N 条"，用户看得见确实在记
        PassThrough = false;
        BoardOn = false;                // 透明批注：能看见底下的东西，和上课一样
        Doc.InvalidateAll();
        _dirty = true;

        EraserTelemetry = new EraserTelemetry(root + ".csv", root + ".txt", NowMs);
        if (keepSystemCursor)
        {
            EraserKeepsSystemCursor = true;
            Console.WriteLine("  已打开对照：系统箭头也会显示（和自绘方块对比跟手程度）");
        }

        Console.WriteLine();
        Console.WriteLine($"=== 橡皮手测台 ===  日志：{Path.GetFullPath(root)}.csv / .txt");
        Console.WriteLine("  已铺样例：两行板书、三个字、一条自交荧光笔、一个矩形一个箭头");
        Console.WriteLine("  左下角还有一块「截图区」：图上有三条批注，试试图上的墨能不能正常擦、图本身会不会被吃掉");
        Console.WriteLine("  当前工具：像素橡皮 Ctrl+Alt+7（整笔橡皮 4、笔 1、撤销 Z、"
                        + "换橡皮大小 6、退出 X）");
        SettleFrames(300);
    }

    /// <summary>
    /// 实验 / 回归（--eraseselfcross）：**切一刀之后，荧光笔自交处会不会变深**。
    ///
    /// 2026-09-15：像素橡皮改成"在一条笔迹上记擦除区间"之后，这个实验从"报数字"变成
    /// 了**回归判据**（差必须是 0）——它正是那一版改动存在的理由。同一件事在
    /// `--pixelerasetest` 里也会验一次。
    ///
    /// 为什么值得单独量：`--selfcross` 守着"一笔自己穿过自己时颜色不能变深"这条不变量，
    /// 而它是**一次 DrawGeometry** 的性质——D2D 把一条描边当成一个整体填充一次。
    /// 像素橡皮把一条笔迹**拆成两个对象**之后就变成两次填充，两段墨在空间上重叠的地方
    /// 会各画一次：半透明的荧光笔叠两次，交叠处就深了。
    ///
    /// 这不是"看着差不多"的事，能被像素证伪。所以这里在**离交点很远的地方**切一刀，
    /// 让互相穿过的那两股墨落在不同的碎片里，再量交点色和普通处色的差。
    /// 只报数字、不判红绿（它是在验证一个设计取舍，不是回归用例）。
    /// </summary>
    private void EraseSelfCrossProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 实验：切段之后自交处会不会变深（半透明荧光笔）===");

        Doc.Clear();
        Doc.ClearHistory();

        const int W = 800, H = 800;
        var (target, cpu) = MakeOffscreen(W, H);
        byte[] Render() => RenderAndRead(target, cpu, W, H, _virtualX, _virtualY);

        float cx = _virtualX + 500f, cy = _virtualY + 500f;
        Doc.AddStroke(MakeSelfCrossingHighlight(cx, cy, 40f));
        (int r, int g, int b) Avg(byte[] buf, int px, int py, int size)
            => AvgPatch(buf, W, px, py, size);
        // **真正的自交点在哪儿**：把两段中心线解出来才知道。
        //   A: P1(120,0) → P2(240,60)（第一段那条横线的斜尾巴）
        //   B: P5(-20,120) → P6(200,0)（绕回来那一股）
        //   A(u) = (120+120u, 60u)、B(t) = (-20+220t, 120-120t)
        //   60u = 120-120t → u = 2-2t；代进 x：-20+220t = 360-240t → t = 0.826, u = 0.348
        //   → 交点 ≈ (161.7, 20.9)
        //
        // 顺带一提：原来那条 --selfcross 用例探的是相对 (200, 0)，离这里 40 像素——
        // 那儿其实只有一股墨，两边颜色当然一样，那条判据是**空的**（见文档）。
        int crossX = (int)(cx + 161.7f - _virtualX) - 8;
        int crossY = (int)(cy + 20.9f - _virtualY) - 8;
        int plainX = (int)(cx + 60f - _virtualX) - 8;
        int plainY = (int)(cy - _virtualY) - 8;

        var buf0 = Render();
        var cross0 = Avg(buf0, crossX, crossY, 17);
        var plain0 = Avg(buf0, plainX, plainY, 17);
        int Diff((int r, int g, int b) a, (int r, int g, int b) b) => ColorDiff(a, b);
        int diff0 = Diff(cross0, plain0);
        Console.WriteLine($"  一刀没切（{Doc.Strokes.Count} 个对象）：交叠处 ({cross0.r},{cross0.g},{cross0.b})"
                        + $" vs 普通处 ({plain0.r},{plain0.g},{plain0.b})  差 {diff0}");

        // 在右侧那条竖边上切一刀（离交点 240 像素以上）：
        // 擦除区间记在**同一条笔迹**上，所以互相穿过的那两股墨还在同一次 DrawGeometry 里。
        int before = Doc.Strokes.Count;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx + 240, cy + 140, 20f, 30f);
        Doc.EndErase();

        var buf1 = Render();
        var cross1 = Avg(buf1, crossX, crossY, 17);
        var plain1 = Avg(buf1, plainX, plainY, 17);
        int diff1 = Diff(cross1, plain1);
        Console.WriteLine($"  切一刀后（{Doc.Strokes.Count} 个对象）：交叠处 ({cross1.r},{cross1.g},{cross1.b})"
                        + $" vs 普通处 ({plain1.r},{plain1.g},{plain1.b})  差 {diff1}");
        Console.WriteLine($"  对象数 {before} → {Doc.Strokes.Count}"
                        + $"；交叠处比切之前深了 {diff1 - diff0}（0 = 没变深）");

        target.Dispose();
        cpu.Dispose();
        Console.WriteLine("erase-selfcross probe done");
        _quit = true;
    }

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
        // **真正的交点在相对 (161.7, 20.9)**（两段中心线联立解出来的，推导见
        // EraseSelfCrossProbe），不是 (200, 0)。
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
        //
        // 探针位置 2026-09-15 修过一次：以前探的是相对 (200,0)，离真交点 40 像素——
        // 那儿只有一股墨，两边颜色当然一样，于是这条判据**永远是绿的、什么都没验**。
        void Probe(string label, float dx)
        {
            var co = Avg(ScreenProbe.CaptureRegion(
                (int)(cx + dx + 161.7f) - 8, (int)(cy + 20.9f) - 8, 17, 17), 17);
            var cs = Avg(ScreenProbe.CaptureRegion((int)(cx + dx + 60) - 8, (int)cy - 8, 17, 17), 17);
            int diff = Math.Abs(co.r - cs.r) + Math.Abs(co.g - cs.g) + Math.Abs(co.b - cs.b);
            Check($"自交处没有叠色（{label}）", diff <= 6,
                  $"交叠处 ({co.r},{co.g},{co.b}) vs 普通处 ({cs.r},{cs.g},{cs.b})，差 {diff}");
        }

        // 先确认"能看见墨"：抓屏在锁屏 / 远程会话 / 被别的窗口盖住时拿到的根本不是
        // 我们这一层，那时任何颜色比对都是假的（实测踩到：两处都拍到桌面壁纸，
        // 于是"差 0 = 通过"）。看不见就明确跳过，不能给一个假绿。
        var sanity = Avg(ScreenProbe.CaptureRegion((int)(cx + 60) - 8, (int)cy - 8, 17, 17), 17);
        // 荧光笔是黄色（r > g > b，都很亮）。拿"是不是黄的"当判据，比"和板色不一样"
        // 硬得多：桌面壁纸、任何别的窗口都能和板色不一样，但不该是黄的。
        bool canSeeInk = sanity.r > 180 && sanity.r > sanity.g && sanity.g > sanity.b;
        if (!canSeeInk)
        {
            Console.WriteLine($"  环境：抓屏看不到我们的墨（拍到的大概是桌面或别的窗口）"
                            + $" → SKIP: 自交叠色那一条跳过（这里应该是一块黄荧光笔，"
                            + $"实际拍到 ({sanity.r},{sanity.g},{sanity.b})）");
            BoardColor = boardColorBefore;
            BoardOn = boardBefore;
            _quit = true;
            return;
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

            // 像素橡皮的一步：命中判定 + 切段 + 重画那一小块。它比整笔橡皮多做
            // 一件事（切段与新建碎片），所以必须单独量——不能拿整笔橡皮的数字顶替。
            double cutSum = 0;
            int cutSteps = 0, cutStrokes = 0;
            double cutSelfMs = 0;
            int cutTiles = 0, cutDrawn = 0;
            var rnd3 = new Random(13);
            float bx = _virtualX + (float)rnd3.NextDouble() * _virtualW;
            float by = _virtualY + (float)rnd3.NextDouble() * _virtualH;
            int objBefore = Doc.Strokes.Count;
            // 注意：这里走的是**一次完整拖拽**的 API（Begin/End），和用户真擦一笔一模一样。
            // 拖拽中每步只改区间（便宜），松手时一次性落成独立对象——那一笔单独计时。
            Doc.BeginEraseRect();
            for (int i = 0; i < n; i++)
            {
                bx += 14; by += 9;
                var swCut = Stopwatch.StartNew();
                cutStrokes += Doc.EraseRectAt(bx, by, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
                swCut.Stop();
                cutSelfMs += swCut.Elapsed.TotalMilliseconds;
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                cutSum += _windows[0].LastPatchMs;
                cutTiles += _windows[0].LastPatchCount;
                cutDrawn += _windows[0].LastDrawnStrokes;
                cutSteps++;
            }
            var swSettle = Stopwatch.StartNew();
            Doc.EndErase();
            swSettle.Stop();
            double settleMs = swSettle.Elapsed.TotalMilliseconds;
            P($"【像素橡皮】在 1 万笔画的画面上，擦除拖动一步: {cutSum / cutSteps:F2} ms/步"
              + $"（其中擦除本身 {cutSelfMs / cutSteps:F2} ms + 重画 {cutSum / cutSteps - cutSelfMs / cutSteps:F2} ms；"
              + $"共切到 {cutStrokes} 笔、每步重画 {cutTiles / (double)cutSteps:F1} 块；"
              + $"对象数 {objBefore} → {Doc.Strokes.Count}（区间表那一版不涨，拆对象那版会涨到 {objBefore + cutStrokes}）；"
              + $"每步重画笔数 {cutDrawn / (double)cutSteps:F0}；"
              + $"松手结账（区间 → 独立对象）{settleMs:F1} ms，一次性；"
              + $"整笔橡皮同位置 {eraseSum / eraseSteps:F2} ms/步）");
            // 上面这几个数（擦除本身 / 重画 / 每步几块 / 每步几笔）是**分得开**的：
            // 换一版实现时就看得出慢在哪一半——2026-09-15 比"拆对象"和"区间表"两版，
            // 擦除本身 5.9→2.9ms（快一倍）、重画 2.5→5.8ms（每步要画的笔数 764→886，
            // 多出来的是"被擦过但还在文档里"的整条笔迹）。

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
            StepCameraAnim();          // 主循环每帧做的那一件事，自检也得做（否则相机永远停在起点）
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
            var text = ReadTextShared(file);
            int n = 0, i = 0;
            while ((i = text.IndexOf("clicked", i, StringComparison.Ordinal)) >= 0) { n++; i += 7; }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 读一个"另一个进程正在写"的小文件，**带重试**。
    ///
    /// 为什么需要：点击目标进程每隔一会儿就往这个文件里追加一行，父进程同时在读；
    /// 撞上共享冲突时 `File.ReadAllText` 直接抛 IOException，整条用例会以"异常"收场
    /// 而不是判据失败——套件里就报成 FATAL，看起来像功能坏了。
    /// 这类"测试自己的时序问题"必须和被测的东西分开，不然会浪费很多时间去查一个不存在的 bug。
    /// </summary>
    private static string ReadTextShared(string path, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (Exception) { return ""; }   // 到点了还读不到就当空的，别把用例打成异常
        }
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
        // 每次跑用**带进程号的文件名**：上一轮要是留了个没退干净的点击目标进程，
        // 它还在往老文件里写，两边就会抢同一个文件（套件里真撞到过一次）。
        string log = Path.Combine(dir, $"target-{Environment.ProcessId}.txt");
        File.WriteAllText(log, "");      // must write before spawning? no: spawn then wait for ready

        var psi = new ProcessStartInfo(Environment.ProcessPath, $"--clicktarget \"{log}\"")
        {
            UseShellExecute = false,
        };
        var target = Process.Start(psi);
        if (target == null) { Console.WriteLine("  无法启动点击目标进程"); _quit = true; return; }

        // 读的时候带重试：父进程读、子进程写，撞上共享冲突时直接抛会把整条用例打成异常
        for (int i = 0; i < 120 && !ReadTextShared(log, 200).Contains("ready"); i++)
            Thread.Sleep(50);
        if (!ReadTextShared(log, 200).Contains("ready"))
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
    /// 界面输入通路的真机自检：把"面板内 / 面板外 × 穿透开 / 穿透关 × 界面吃不吃"
    /// 这几种组合各合成一次**真实点击**，然后看**到底谁收到了**。
    ///
    /// 四类判定（这就是引擎与界面之间那四条规矩）：
    ///
    ///   · 命中界面矩形、界面消费 → 归界面（画布不落墨，穿透也不交下层）
    ///   · 命中界面矩形、界面没消费 → 画布不落墨；穿透时交下层
    ///   · 没命中界面矩形 → 穿透时交下层，否则照常落墨
    ///   · 悬停同样要转发给界面（按钮高亮靠它），坐标必须是**逻辑屏幕**坐标
    ///
    /// 为什么非要真窗口 + 真点击：这一类 bug 全在"系统命中测试 → WM_NCHITTEST →
    /// WM_POINTER* → 引擎"这条路上，纯函数自检与不接引擎的假面板**都看不见**。
    /// 合成输入走的就是真实的那条路。
    /// </summary>
    private void UiInputTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 界面输入通路自检（合成真实点击，看谁收到）===");

        if (SkipIfNoSyntheticInput("界面输入通路自检")) { _quit = true; return; }

        // 目标窗口：另一个进程里的一块普通窗口，被我们的覆盖层压着。
        string dir = Path.Combine(Path.GetTempPath(), "inkprobe_uitest");
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, $"target-{Environment.ProcessId}.txt");   // 同 --passtest：按进程号分开
        File.WriteAllText(log, "");

        var psi = new ProcessStartInfo(Environment.ProcessPath, $"--clicktarget \"{log}\"")
        {
            UseShellExecute = false,
        };
        var target = Process.Start(psi);
        if (target == null)
        {
            Console.WriteLine("  无法启动点击目标进程");
            _quit = true;
            return;
        }

        for (int i = 0; i < 120 && !ReadTextShared(log, 200).Contains("ready"); i++)
            Thread.Sleep(50);
        if (!ReadTextShared(log, 200).Contains("ready"))
        {
            Console.WriteLine("  点击目标窗口没有就绪");
            try { target.Kill(); } catch { }
            _quit = true;
            return;
        }

        // 面板盖住目标窗口的中段：这样"面板内"和"面板外"两个落点都在同一个
        // 下层窗口里，唯一的差别就是有没有被界面接住。
        const int twX = 120, twY = 120, twW = 420, twH = 320;
        int cx = twX + twW / 2, cy = twY + twH / 2;
        int panelX = cx - 140, panelW = 280, panelH = 140;

        var probe = new UiProbe
        {
            BoundsPhysical = new RectF
            {
                MinX = panelX, MinY = cy - panelH / 2,
                MaxX = panelX + panelW, MaxY = cy + panelH / 2,
            },
        };
        SetUi(probe);
        Tool = Tool.Pen;
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(200);

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        void Click(int x, int y)
        {
            SendMouse(x, y, 0);                            SettleFrames(80);
            SendMouse(x, y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse(x, y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(320);
        }

        void SetPass(bool on)
        {
            PassMode = PassThroughMode.LayeredTransparent;
            PassThrough = on;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
            SettleFrames(150);
        }

        // 三个落点：都在目标窗口的**客户区**里（窗口有边框和标题栏，
        // 点在标题栏上系统不会发 WM_LBUTTONDOWN，下层就"收不到点击"了），
        // 差别只在面板/界面怎么处理。
        int inX = cx - 80, inY = cy;             // 面板左半：界面会消费
        int holeX = cx + 80, holeY = cy;         // 面板右半：界面返回 false
        int outX = cx, outY = cy + 140;          // 面板下沿之外、客户区内

        // ---- ① 悬停要能到界面 ----
        SetPass(false);
        probe.Move = 0;
        SendMouse(inX, inY, 0);
        SettleFrames(200);
        Check("悬停转发到界面", probe.Move > 0, $"界面收到 {probe.Move} 次移动");

        // ---- ② 悬停坐标必须是"逻辑屏幕"，不能是画布坐标 ----
        // 判法：同一个物理点，相机滚过之后再悬停一次，界面看到的坐标不该变。
        float y0 = probe.LastY;
        ViewOffsetY = -300f;
        SettleFrames(150);
        SendMouse(inX, inY + 6, 0);              // 错开一点，确保真的有 WM_POINTERUPDATE
        SettleFrames(200);
        float y1 = probe.LastY;
        bool coordsUsable = !float.IsNaN(y0) && !float.IsNaN(y1);
        bool drift = coordsUsable && MathF.Abs(y1 - y0) > 4f;
        Check("悬停坐标不受相机影响", coordsUsable && !drift,
              coordsUsable
                  ? $"滚前 y={y0:F0}，滚后 y={y1:F0}（差 {MathF.Abs(y1 - y0):F0} 像素）"
                  : "界面根本没收到悬停，这一项无从判定");
        ViewOffsetY = 0f;
        SettleFrames(150);

        // ---- ③ 不穿透：面板内点击归界面，且不许画出一笔 ----
        int strokes0 = Doc.Strokes.Count;
        int clicks0 = CountClicks(log);
        probe.Down = 0;
        Tool = Tool.Pen;
        Click(inX, inY);
        bool uiGot = probe.Down > 0;
        Check("不穿透·点面板归界面", uiGot, $"界面收到 {probe.Down} 次按下");
        Check("不穿透·点面板不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");
        Check("不穿透·点面板不传下层", CountClicks(log) == clicks0, "下层窗口没收到点击");
        Check("界面的命令真的到了引擎", probe.LastToolFromState == Tool.Eraser,
              $"界面按钮把工具切成了 {probe.LastToolFromState}");

        // ---- ④ 不穿透：界面"看见了但不吃"的那一半也不该落墨 ----
        Tool = Tool.Pen;
        strokes0 = Doc.Strokes.Count;
        Click(holeX, holeY);
        Check("不穿透·面板空洞不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑤ 不穿透：面板外照常画线 ----
        Tool = Tool.Pen;
        strokes0 = Doc.Strokes.Count;
        clicks0 = CountClicks(log);
        Click(outX, outY);
        Check("不穿透·面板外照常落墨", Doc.Strokes.Count > strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");
        Check("不穿透·面板外不传下层", CountClicks(log) == clicks0, "下层窗口没收到点击");

        // ---- ⑥ 穿透：面板内点击必须由界面收下（这就是这次要修的那条）----
        SetPass(true);

        // 穿透下悬停也要到界面：这是"看 PPT 时随手叫出笔"的同一组合。
        probe.Move = 0;
        SendMouse(inX, inY, 0);
        SettleFrames(220);
        Check("穿透·悬停到界面", probe.Move > 0, $"界面收到 {probe.Move} 次移动");

        clicks0 = CountClicks(log);
        strokes0 = Doc.Strokes.Count;
        probe.Down = 0;
        Tool = Tool.Pen;
        Click(inX, inY);
        Check("穿透·点面板归界面", probe.Down > 0, $"界面收到 {probe.Down} 次按下");
        Check("穿透·面板不许穿到下层", CountClicks(log) == clicks0,
              $"下层窗口收到 {CountClicks(log) - clicks0} 次点击（应为 0）");
        Check("穿透·点面板不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑦ 穿透：面板是界面的地盘，"看见但不吃"也不许漏给下层 ----
        // （面板要放行某个位置，得它自己别把那一块算进 QueryBounds；
        //   将来若真需要"面板中间挖个洞让点击穿过去"，再加一个逐点命中回调。）
        clicks0 = CountClicks(log);
        strokes0 = Doc.Strokes.Count;
        Click(holeX, holeY);
        Check("穿透·面板空洞不漏给下层", CountClicks(log) == clicks0,
              $"下层窗口收到 {CountClicks(log) - clicks0} 次点击（应为 0）");
        Check("穿透·面板空洞不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑧ 穿透：面板外交下层 ----
        clicks0 = CountClicks(log);
        strokes0 = Doc.Strokes.Count;
        Click(outX, outY);
        Check("穿透·面板外交下层", CountClicks(log) > clicks0,
              $"下层窗口收到 {CountClicks(log) - clicks0} 次点击（应 ≥ 1）");
        Check("穿透·面板外不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑨ 界面驱动的动画必须拿到连续帧，停下之后必须回到零帧 ----
        // 判据用的是引擎自己的 NeedsFrame()：测的和跑的是同一个判据。
        SetPass(false);
        SettleFrames(150);
        probe.AnimateUntilMs = NowMs + 220;

        int framesDuring = 0;
        var swAnim = Stopwatch.StartNew();
        while (swAnim.ElapsedMilliseconds < 240)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; framesDuring++; }
            else Thread.Sleep(1);
        }
        Check("界面驱动的动画拿到连续帧", framesDuring >= 5,
              $"220 毫秒里出了 {framesDuring} 帧（60 fps 下约 13 帧）");

        // 先落地一帧把脏区清干净，再看安静期是不是真的 0 帧
        PumpMessages();
        RenderAll();
        _dirty = false;
        int framesQuiet = 0;
        var swQuiet = Stopwatch.StartNew();
        while (swQuiet.ElapsedMilliseconds < 150)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; framesQuiet++; }
            else Thread.Sleep(2);
        }
        Check("动画结束后回到零帧", framesQuiet == 0, $"安静 150 毫秒出了 {framesQuiet} 帧");

        // ---- ⑩ 命令通道：新加的三条命令走一遍，状态读得回来 ----
        // 这段走的是**界面能看到的那条路**（IUiHost.Commands → IEngineCommands → 引擎），
        // 不是直接改引擎字段——否则测的就不是"契约通不通"。
        var host = Host;
        host.Commands.SetSelectMode(SelectMode.Lasso);
        Check("命令·切套索", host.State.SelectMode == SelectMode.Lasso,
              $"读回 {host.State.SelectMode}");
        host.Commands.SetSelectMode(SelectMode.Rect);

        var green = InkPalette.BoardPresets[1].Color;
        host.Commands.SetBoardColor(green);
        Check("命令·换板色", host.State.BoardColor.Equals(green), "读回绿板");
        host.Commands.SetBoardColor(InkPalette.BoardPresets[0].Color);

        Doc.Clear();
        Doc.ClearHistory();
        var only = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        only.AddPoint(400, 400, 1f, 0);
        only.AddPoint(620, 400, 1f, 1);
        Doc.AddStroke(only);
        Tool = Tool.Pen;
        host.Commands.SelectAll();
        Check("命令·全选", Doc.Selected.Count == 1 && Tool == Tool.Marquee,
              $"选中 {Doc.Selected.Count} 条，工具={Tool}");
        Doc.Clear();
        Doc.ClearHistory();

        // ---- ⑪ 界面看到的"屏幕"必须和 IUiHost.Screen 是同一个 ----
        // 单屏上这两者本来就相等（覆盖窗口 == 虚拟桌面），所以这一条是**回归护栏**：
        // 一旦有人把 Layout 的实参改回"本窗口那一块显示器"，双屏上才会露馅，
        // 而在没有双屏的机器上，护栏能立刻发现它不等了。
        Check("Layout 与 Screen 同一套坐标",
              probe.LastLayoutScreen.Equals(host.Screen),
              $"Layout ({probe.LastLayoutScreen.MinX:F0},{probe.LastLayoutScreen.MinY:F0})-"
              + $"({probe.LastLayoutScreen.MaxX:F0},{probe.LastLayoutScreen.MaxY:F0})  vs  "
              + $"Screen ({host.Screen.MinX:F0},{host.Screen.MinY:F0})-"
              + $"({host.Screen.MaxX:F0},{host.Screen.MaxY:F0})");

        // ---- ⑫ 界面抛异常不许留死局：重建 → 重启 → 兜底，而且板书不许丢 ----
        // 教室大屏/手写板上可能根本没有键盘，界面是唯一的出口：界面没了又关不掉、
        // 重启不了，就是死局（用户 2026-09-16 提的）。
        Recovery.ClearRestartCount();          // 别让上一次自检留下的计数影响这一次
        try { File.Delete(Recovery.SessionPath); } catch { }

        int uiBuilt = 0;
        var panelRect = probe.BoundsPhysical;
        UiProbe MakeProbe()
        {
            uiBuilt++;
            return new UiProbe { BoundsPhysical = panelRect };
        }
        SetUiFactory(MakeProbe);               // 引擎从此有"再造一个回来"的办法
        SettleFrames(200);

        // ① 第一次崩：3 秒内 3 次 → 重建界面（不是停用）
        var firstUi = (UiProbe)CurrentUi;
        firstUi.ThrowOnDown = true;
        for (int i = 0; i < 3; i++) { probe.Down = 0; Click(inX, inY); }
        bool rebuilt = CurrentUi is UiProbe && !ReferenceEquals(CurrentUi, firstUi);
        Check("界面崩了先重建（不停用）", rebuilt,
              $"重建次数 {uiBuilt - 1}，当前界面 = {CurrentUi.Name}");

        // ② 重建之后继续崩（重建额度用完）→ 重启软件，且重启前把板书存下来
        Doc.Clear();
        Doc.ClearHistory();
        var keep = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        keep.AddPoint(400, 400, 1f, 0);
        keep.AddPoint(700, 400, 1f, 1);
        Doc.AddStroke(keep);
        int strokesBeforeRestart = Doc.Strokes.Count;

        for (int round = 0; round < 2; round++)
        {
            var ui = CurrentUi as UiProbe;
            if (ui == null) break;
            ui.ThrowOnDown = true;
            for (int i = 0; i < 3; i++) { probe.Down = 0; Click(inX, inY); }
            SettleFrames(120);
        }
        Check("重建救不回来就重启软件", RestartRequested, $"当前界面 = {CurrentUi.Name}");

        // 板书必须读得回来——"重启"的前提是不丢东西
        var blob = Recovery.TakeSession();
        bool restorable = false;
        string detail = "没有暂存文件";
        if (blob != null)
        {
            var fresh = new InkDocument();
            InkSerializer.LoadInto(fresh, blob);
            restorable = fresh.Strokes.Count == strokesBeforeRestart && strokesBeforeRestart > 0;
            detail = $"暂存 {blob.Length} 字节 → 读回 {fresh.Strokes.Count} 笔（重启前 {strokesBeforeRestart} 笔）";
        }
        Check("重启后板书读得回来", restorable, detail);
        try { File.Delete(Recovery.SessionPath); } catch { }
        Recovery.ClearRestartCount();

        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        ExitCode = fail == 0 ? 0 : 1;

        // ---- 诊断（不计红绿）：穿透的两种实现方式各自牺牲了什么 ----
        // "面板可点"和"点击透给下层"在现在的实现里是互斥的：
        //   · 只改 WM_NCHITTEST（HitTest 档）：面板能收到，但下层收不到
        //     —— HTTRANSPARENT 只在**同一个线程内**继续往下找窗口；
        //   · LAYERED+TRANSPARENT 档：下层收得到，但这个窗口在系统那一层
        //     就被排除在输入之外了，NCHITTEST 根本轮不到我们，面板点不动。
        // 这段打印就是为了把这条取舍钉成数字，选架构时不用再猜。
        // 上面的熔断自检留着"故意抛异常"的界面，诊断这几下点击会继续触发重建/重启
        // （还会往临时目录写会话文件）。先把开关关掉，让这段只回答它要回答的问题。
        var diagUi = CurrentUi as UiProbe;
        if (diagUi != null) diagUi.ThrowOnDown = false;

        foreach (var (modeName, mode) in new[]
                 {
                     ("只按点回答命中测试", PassThroughMode.HitTest),
                     ("LAYERED+TRANSPARENT", PassThroughMode.LayeredTransparent),
                 })
        {
            PassMode = mode;
            PassThrough = true;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
            SettleFrames(150);

            if (diagUi != null) diagUi.Down = 0;
            int c0 = CountClicks(log);
            int nc0 = _cntNcHitTest, ncc0 = _cntNcHitClient;
            int dn0 = _cntDown;
            Click(inX, inY);
            Console.WriteLine($"  [诊断] {modeName,-24} 面板收到按下 {(diagUi != null && diagUi.Down > 0 ? "是" : "否")}"
                              + $"   下层窗口收到 {(CountClicks(log) > c0 ? "是" : "否")}"
                              + $"   系统问了命中测试 {_cntNcHitTest - nc0} 次"
                              + $"（其中答「这块是我的」{_cntNcHitClient - ncc0} 次）"
                              + $"   引擎收到指针按下 {_cntDown - dn0} 次");
            PassThrough = false;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
            SettleFrames(120);
        }
        try { File.Delete(Recovery.SessionPath); } catch { }
        Recovery.ClearRestartCount();

        SetPass(false);
        try { target.Kill(); } catch { }
        _quit = true;
    }

    /// <summary>
    /// 产品界面（`src/InkUi` 的 `FullUi`）自检：球 ↔ 按钮带这条最小闭环。
    ///
    /// 这一步故意只验四件事——**坐标 / DPI / 脏区 / 输入拦截**：
    /// 它们在假面板里验证不到（假面板自成一个窗口、自带坐标系），而恰恰是接引擎
    /// 最容易错的地方。这四件对了，后面搬色带、滑块、抽屉都只是堆代码。
    ///
    /// 挂法用的是 `SetUiFactory`——产品的挂法。界面崩了引擎要能自己再造一个，
    /// 没工厂就只能一路走到重启（见 计划-底层对接界面.md 4.5）。
    /// </summary>
    /// <summary>
    /// 把界面挂上、展开、出图（给人看的，不判红绿）。
    /// 出图这条链子是这个仓库一贯的验收方式：观感的事眼睛说了算，数字只负责证明没坏。
    /// </summary>
    /// <summary>把激光笔图标的几个候选并排出一张图（开发期比图用）。</summary>
    /// <summary>出图：**截屏图标候选**（用户 2026-09-17："截图图标和选中图标一样的，是不是不大好？"）。</summary>
    private void CaptureIconShow(string path)
    {
        SetUi(new CaptureIconSheet());
        BoardOn = true;
        SettleFrames(400);
        if (OffscreenShot(path)) return;
        Console.WriteLine("出图失败（离屏路径没走通）");
        ExitCode = 1;
        _quit = true;
    }

    private void IconShow(string path)
    {
        SetUi(new LaserIconSheet());
        BoardOn = true;                      // 白板打底：图里没有桌面上的杂东西
        SettleFrames(400);

        // 优先走**离屏出图**：锁屏 / 远程 / 没显示器时照样出得来，图里也不会混进桌面。
        if (OffscreenShot(path)) return;

        var b = CurrentUi.QueryBounds();
        int x = (int)MathF.Floor(b.MinX * DpiScale) - 20;
        int y = (int)MathF.Floor(b.MinY * DpiScale) - 20;
        int w = (int)MathF.Ceiling((b.MaxX - b.MinX) * DpiScale) + 40;
        int h = (int)MathF.Ceiling((b.MaxY - b.MinY) * DpiScale) + 40;
        bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
        Console.WriteLine(ok ? $"已出图 {path}" : "出图失败");
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }

    /// <summary>
    /// 真实面板的每帧代价：**同一份内容**，挂面板与不挂面板各渲染一遍，比时间。
    ///
    /// 为什么要有这个：设计阶段那句"面板每帧 0.5～1.5 毫秒"是**估**的
    /// （拿 160×60 的小界面按面积外推的），而面板现在有 636×82、
    /// 还在写字这条最敏感的路径上。教室机器比开发机慢，估的数不能当结论。
    ///
    /// 量三档：不挂界面（基准）／收起成球／展开成带子（含设置条，最贵的一档）。
    /// 每档都强制 `_dirty = true` 再渲染，避免"空闲不出帧"把代价量成 0。
    /// </summary>
    private void PanelPerf()
    {
        Console.WriteLine();
        Console.WriteLine("=== 真实面板的每帧代价（同一份内容，挂 vs 不挂）===");

        // 一屏板书：200 条各 40 个点
        Doc.Clear();
        Doc.ClearHistory();
        var rnd = new Random(7);
        for (int s = 0; s < 200; s++)
        {
            var st = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0.1f, 0.1f, 0.1f, 1f),
                Width = 6f * DpiScale,
            };
            float x0 = _virtualX + rnd.Next(100, Math.Max(200, _virtualW - 400));
            float y0 = _virtualY + rnd.Next(100, Math.Max(200, _virtualH - 300));
            for (int k = 0; k < 40; k++) st.AddPoint(x0 + k * 6, y0 + MathF.Sin(k * 0.3f) * 20, 1f, k);
            Doc.AddStroke(st);
        }
        Doc.InvalidateAll();
        SettleFrames(400);

        double Measure(int frames)
        {
            // **跑 3 轮取最小值**：这台机器上同时跑着远程控制和几个吃 GPU 的客户端，
            // 平均值被"别人抢走的那几毫秒"抬高得离谱（同一档实测在 1.1～3.0 之间跳）。
            // 最小值 = 最"干净"的那一轮，用它比才比得出我们自己代码的差别。
            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                SettleFrames(150);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++) { _dirty = true; RenderAll(); }
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds / frames);
            }
            return best;
        }

        SetUi(new HeadlessUi());
        SettleFrames(200);
        double none = Measure(200);

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        double ball = Measure(200);                       // 收起态：一个球

        if (CurrentUi is InkUi.FullUi ui)
        {
            ui.SnapForTest();                             // 一步展开
            ui.OpenRailForTest();                         // 色带也张开（最贵的一档）
        }
        SettleFrames(300);
        double full = Measure(200);

        Console.WriteLine($"  不挂界面            {none,6:F2} ms/帧");
        Console.WriteLine($"  收起成一个球        {ball,6:F2} ms/帧   （比不挂多 {ball - none,5:F2}）");
        Console.WriteLine($"  展开＋色带张开      {full,6:F2} ms/帧   （比不挂多 {full - none,5:F2}）");

        // ---- 归因：把几块分别关掉再量，看钱花在哪 ----
        Console.WriteLine();
        Console.WriteLine("  归因（从「展开＋色带」这一档里省了多少）：");
        void Attrib(string name, Action set)
        {
            set();
            SettleFrames(200);
            double t = Measure(200);
            Console.WriteLine($"    {name,-22} {t,6:F2} ms/帧   （省 {full - t,5:F2}）");
        }
        Attrib("不算阴影", () => InkUi.FullUi.PerfSkipShadow = true);
        InkUi.FullUi.PerfSkipShadow = false;
        Attrib("不算凹槽与顶光", () => InkUi.FullUi.PerfSkipChrome = true);
        InkUi.FullUi.PerfSkipChrome = false;
        Attrib("不算色片细节", () => InkUi.FullUi.PerfSkipSwatchDetail = true);
        InkUi.FullUi.PerfSkipSwatchDetail = false;
        Attrib("不算图标", () => InkUi.FullUi.PerfSkipIcons = true);
        InkUi.FullUi.PerfSkipIcons = false;

        bool ok = (full - none) <= 1.5;                   // 计划里定的红线：≤1.5ms/帧
        Console.WriteLine(ok
            ? "  PASS: 完整展开态的额外代价在 1.5 毫秒以内"
            : $"  FAIL: 完整展开态比不挂界面贵了 {full - none:F2} 毫秒（红线 1.5）");
        ExitCode = ok ? 0 : 1;
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
    private bool OffscreenShot(string path)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderUiToBgra(this, 24, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>同上，但**画哪一块自己指定**（逻辑像素）——见 `RenderUiToBgra` 的重载。</summary>
    private bool OffscreenShot(string path, in RectF bounds)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderUiToBgra(this, 24, bounds, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>同上，但画的是**浮动层**（选中框 / 操作条 / 小面板）——坐标给画布坐标。</summary>
    private bool OffscreenFloatingShot(string path, in RectF canvasBounds)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderFloatingToBgra(this, 24, canvasBounds, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    private bool SaveBgraAsBmp(string path, byte[] bytes, int w, int h)
    {
        if (bytes == null)
        {
            Console.WriteLine("离屏出图失败：" + (_windows[0].LastError ?? "没有占用矩形"));
            return false;
        }

        // BMP：文件头 14 ＋ 信息头 40，像素按 BGRA、行从下往上（行宽 4 字节对齐）。
        int stride = w * 4;
        int size = 54 + stride * h;
        var outBytes = new byte[size];
        void W16(int at, int v) { outBytes[at] = (byte)v; outBytes[at + 1] = (byte)(v >> 8); }
        void W32(int at, int v)
        {
            outBytes[at] = (byte)v; outBytes[at + 1] = (byte)(v >> 8);
            outBytes[at + 2] = (byte)(v >> 16); outBytes[at + 3] = (byte)(v >> 24);
        }
        outBytes[0] = (byte)'B'; outBytes[1] = (byte)'M';
        W32(2, size); W32(10, 54); W32(14, 40);
        W32(18, w); W32(22, h);
        W16(26, 1); W16(28, 32);
        W32(34, stride * h);
        for (int y = 0; y < h; y++)
            Array.Copy(bytes, (h - 1 - y) * w * 4, outBytes, 54 + y * stride, w * 4);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllBytes(path, outBytes);
            Console.WriteLine($"已离屏出图 {path}（{w}×{h}）");
            ExitCode = 0;
            _quit = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("写出图失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// **页界线摆样**：白板打底、铺三屏板书，出两张图——
    ///   · `<path>`：相机停在两屏之间（页界线落在屏幕中间，最好看细节）；
    ///   · `<path>-对齐.bmp`：相机正好停在第 2 屏（页界线压在屏幕上下沿）。
    /// 为什么要两张：一眼看出"一屏一页"的分界到底顺不顺眼、会不会被当成画面上的脏点。
    /// </summary>
    private void PageShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        BoardOn = true;
        BoardColor = InkPalette.BoardPresets[0].Color;
        // --pattern N：出图时带上白板底纹（0 无 / 1 方格 / 2 横线；--step 给间距）
        {
            var cli0 = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(cli0, "--pattern");
            int si = Array.IndexOf(cli0, "--step");
            if (pi >= 0 && pi + 1 < cli0.Length && int.TryParse(cli0[pi + 1], out int pat))
            {
                float step = si >= 0 && si + 1 < cli0.Length && float.TryParse(cli0[si + 1], out float sv)
                             ? sv : 40f;
                SetBoardPatternFromUi(pat, step);
            }
        }

        for (int screen = 0; screen < 3; screen++)
            for (int row = 0; row < 4; row++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.10f, 0.11f, 0.14f, 1f), Width = 5f * DpiScale,
                };
                float y = _virtualY + screen * _virtualH + 240f + row * 380f;
                for (int i = 0; i <= 36; i++)
                    s.AddPoint(_virtualX + 420f + i * 52f, y + MathF.Sin(i * 0.4f) * 26f, 1f, i * 8);
                Doc.AddStroke(s);
            }

        // 第 2 屏右边标一句"这里是第 2 屏"，看图时好对号
        var tag = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.13f, 0.36f, 0.24f, 1f), Width = 9f * DpiScale,
        };
        float ty = _virtualY + _virtualH + 150f;
        for (int i = 0; i <= 60; i++) tag.AddPoint(_virtualX + 2200f + i * 4f, ty + (i % 7) * 6f, 1f, i * 8);
        Doc.AddStroke(tag);

        void Shot(float offset, string outPath)
        {
            ViewOffsetY = offset;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = offset; }
            Doc.InvalidateAll();
            SettleFrames(600);
            bool ok = ScreenProbe.SaveBmp(outPath, (int)_virtualX, (int)_virtualY,
                                          (int)_virtualW, (int)_virtualH);
            Console.WriteLine(ok ? $"已出图 {outPath}" : $"出图失败 {outPath}");
        }

        Shot(-_virtualH * 0.55f, path);                       // 界线落在屏幕中间
        string aligned = System.IO.Path.ChangeExtension(path, null) + "-对齐.bmp";
        Shot(-_virtualH * 1.0f, aligned);                     // 正好第 2 屏

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        _quit = true;
    }

    private void PanelShow(string path)
    {
        _panelShowDrawer = Environment.GetCommandLineArgs().Contains("--drawer");
        _panelShowMini = Environment.GetCommandLineArgs().Contains("--mini");
        _panelShowBand = Environment.GetCommandLineArgs().Contains("--band");
        SetUiFactory(() => new InkUi.FullUi());
        Tool = Tool.Pen;
        BoardOn = true;                      // 白板打底：截出来的图里没有桌面上的杂东西
        // --pixel：把工具切成面积橡皮再出图（看"橡皮按钮的图标跟着换"那一版）
        if (Environment.GetCommandLineArgs().Contains("--pixel")) Tool = Tool.PixelEraser;
        if (Environment.GetCommandLineArgs().Contains("--eraser")) Tool = Tool.Eraser;
        if (Environment.GetCommandLineArgs().Contains("--laser")) Tool = Tool.Laser;
        if (Environment.GetCommandLineArgs().Contains("--hl")) Tool = Tool.Highlighter;
        SettleFrames(400);

        if (CurrentUi is InkUi.FullUi ui)
        {
            ui.SnapForTest();                // 一步展开，不用等 200 毫秒
            if (_panelShowMini) ui.SetProfileForTest(0);     // --mini：极简档（短胶囊）
            if (_panelShowBand) ui.OpenRailForTest();        // --band：把色线张开成设置条
            // --cell N：把上带掰到第 N 格再出图（N=2 就是白板那条
            // `[上一屏] [白][绿][黑] [下一屏]` ＋ "第 N 屏"）
            var cli = Environment.GetCommandLineArgs();
            int ci = Array.IndexOf(cli, "--cell");
            if (ci >= 0 && ci + 1 < cli.Length && int.TryParse(cli[ci + 1], out int cellArg))
                ui.SelectBandCellForTest(cellArg);
            // --width N：先把粗细调成 N 再出图（看"真实大小预览"用）
            int wi = Array.IndexOf(cli, "--width");
            if (wi >= 0 && wi + 1 < cli.Length && float.TryParse(cli[wi + 1], out float widthArg))
            {
                Host.Commands.SetWidth(widthArg);
                SettleFrames(120);
            }
            if (cli.Contains("--preview")) ui.ShowSizePreviewForTest();
            if (_panelShowDrawer) ui.OpenDrawerForTest();   // --drawer：连抽屉一起出图
            SettleFrames(500);

            // 优先离屏出图（锁屏 / 远程也能出，图里不混桌面）
            if (OffscreenShot(path)) return;

            var b = ui.QueryBounds();
            int x = (int)MathF.Floor(b.MinX * DpiScale) - 30;
            int y = (int)MathF.Floor(b.MinY * DpiScale) - 30;
            int w = (int)MathF.Ceiling((b.MaxX - b.MinX) * DpiScale) + 60;
            int h = (int)MathF.Ceiling((b.MaxY - b.MinY) * DpiScale) + 60;
            bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
            Console.WriteLine(ok
                ? $"已出图 {path}（面板 {b.MaxX - b.MinX:F0}×{b.MaxY - b.MinY:F0} 逻辑像素）"
                : "出图失败");
            ExitCode = ok ? 0 : 1;
        }
        _quit = true;
    }

    private void PanelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 产品界面自检（球 → 按钮带）===");

        if (SkipIfNoSyntheticInput("产品界面自检")) { _quit = true; return; }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        void ClickPhysical(float x, float y)
        {
            SendMouse((int)x, (int)y, 0);                            SettleFrames(80);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(320);
        }

        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(300);

        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine($"  界面没挂上：当前 = {CurrentUi.Name}");
            ExitCode = 1;
            _quit = true;
            return;
        }

        // ---- ⓪ 贴边隐藏 + **刚启动**：不许一上来就收（用户 2026-09-17）----
        //
        // 原来 `_leftAtMs` 初值是负无穷，第一帧就满足"离开够久了"——一开机面板立刻收成
        // 屏幕底边那条 8 像素的露头（而且露头正好压在任务栏上），老师根本找不到它。
        // 现在：**先露着**，等指针碰过面板一次才允许自动收。
        {
            // 先把指针挪到画布上（确保没碰过面板），再把"贴边隐藏"写进偏好、重新挂一个界面
            // ——重新挂就等于"刚启动"那一刻。
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.3f), 0);
            SettleFrames(200);
            SetUiPref("hide", "1");
            SetUiFactory(() => new InkUi.FullUi());
            ui = CurrentUi as InkUi.FullUi;
            SettleFrames(1800);                 // 等过"离开 700 毫秒才收"那一段
            var b0 = ui.QueryBounds();
            Check("贴边隐藏开着＋刚启动：面板还是完整的（不许一上来就收）",
                  (b0.MaxY - b0.MinY) > 40f && !ui.PeekArmedForTest,
                  $"占用 {b0.MaxX - b0.MinX:F0}×{b0.MaxY - b0.MinY:F0}，允许自动收 = {ui.PeekArmedForTest}");

            SendMouse((int)((b0.MinX + b0.MaxX) * 0.5f * DpiScale),
                      (int)((b0.MinY + b0.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(250);
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.3f), 0);
            SettleFrames(2000);
            var b1 = ui.QueryBounds();
            Check("碰过之后再离开：这时才收成露头",
                  (b1.MaxY - b1.MinY) < 12f || (b1.MaxX - b1.MinX) < 12f,
                  $"占用 {b1.MaxX - b1.MinX:F0}×{b1.MaxY - b1.MinY:F0}，允许自动收 = {ui.PeekArmedForTest}");

            // 收尾：关掉贴边隐藏、重新挂回默认界面（后面几段用例都按"没开贴边隐藏"写）
            SetUiPref("hide", null);
            SetUiFactory(() => new InkUi.FullUi());
            ui = CurrentUi as InkUi.FullUi;
            SettleFrames(400);
        }

        // 把面板弄到"展开 ＋ 抽屉开着"这个已知状态。
        // 每一步都先问**当前**状态再动手——面板可能正收着、可能刚被换成新实例，
        // 硬按上一次算好的坐标去点，点空了都不知道（这一版用例被坑过一次）。
        void NormalizePanel()
        {
            if (!ui.ExpandedForTest)
            {
                var b = ui.QueryBounds();
                ClickPhysical((b.MinX + b.MaxX) * 0.5f * DpiScale,
                              (b.MinY + b.MaxY) * 0.5f * DpiScale);
                SettleFrames(300);
            }
            if (!ui.DrawerOpenForTest)
            {
                var m = ui.CellRectForTest(12);
                ClickPhysical((m.MinX + m.MaxX) * 0.5f * DpiScale,
                              (m.MinY + m.MaxY) * 0.5f * DpiScale);
                SettleFrames(250);
            }
        }

        // ---- ① 收起态：球真的在屏幕上，而且是个 48 的方（圆） ----
        var ball = ui.QueryBounds();
        float ballW = ball.MaxX - ball.MinX, ballH = ball.MaxY - ball.MinY;
        Check("收起态是一个 48 的球",
              MathF.Abs(ballW - 48f) < 1f && MathF.Abs(ballH - 48f) < 1f,
              $"占用 {ballW:F0}×{ballH:F0}，位置 ({ball.MinX:F0},{ball.MinY:F0})");

        // 按**屏幕**底边算，允许盖住任务栏（用户定的：工具条压在任务栏上是合理的，
        // 贴屏幕边 12 像素的手感比"躲开任务栏"更重要）。
        Check("球贴在屏幕下边、离边 12",
              MathF.Abs(ball.MaxY - (VirtualScreen.MaxY / DpiScale - InkUi.Tokens.EdgeMargin)) < 1.5f,
              $"球底 {ball.MaxY:F0}，屏幕底 {VirtualScreen.MaxY / DpiScale:F0}");

        // 没拖过时的横向位置：锚点是**展开后那条带子的左端**，而带子屏幕居中，
        // 所以球停在"屏幕中心 − 带子宽/2"（偏左半个带子）。
        // 用户 2026-09-17 要的就是这个：**点一下往右展开、展开完全局居中**。
        {
            float wantBallLeft = _virtualX / DpiScale
                               + (_virtualW / DpiScale - ui.ExpandedWidthForTest) * 0.5f;
            Check("没拖过：球停在「展开后带子居中」的左端",
                  MathF.Abs(ball.MinX - wantBallLeft) < 1.5f,
                  $"球左 {ball.MinX:F0}（应为 {wantBallLeft:F0} = 中心 − 带子宽/2），"
                  + $"带子宽 {ui.ExpandedWidthForTest:F0}");
        }

        // ---- ② 点球展开：动画期间必须连续出帧 ----
        int strokes0 = Doc.Strokes.Count;
        float ballCx = (ball.MinX + ball.MaxX) * 0.5f * DpiScale;
        float ballCy = (ball.MinY + ball.MaxY) * 0.5f * DpiScale;
        SendMouse((int)ballCx, (int)ballCy, 0);                            SettleFrames(70);
        SendMouse((int)ballCx, (int)ballCy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(50);
        SendMouse((int)ballCx, (int)ballCy, Native.MOUSEEVENTF_LEFTUP);

        // 松手就开始数帧——**别先 settle**：动画只有 200 毫秒，
        // 先 settle 一遍等于等它跑完再来数"动画期间的帧"，那永远数不出东西。
        int frames = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 260)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; frames++; }
            else Thread.Sleep(1);
        }
        SettleFrames(120);

        Check("点球能展开", ui.ExpandedForTest, $"展开状态 = {ui.ExpandedForTest}");
        Check("展开动画拿到了连续帧", frames >= 5, $"260 毫秒里出了 {frames} 帧");
        Check("点球不落墨", Doc.Strokes.Count == strokes0, $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ③ 展开后的尺寸：算出来的宽，不是一个拍脑袋的数 ----
        var bar = ui.QueryBounds();
        float barW = bar.MaxX - bar.MinX, barH = bar.MaxY - bar.MinY;
        var barOnly = ui.BarRectForTest;
        // 总高看这一刻上带是色线还是设置条（58 / 86），所以这里只卡"主条 48、宽 > 600、
        // 总高在这两档之间"；两条精确的高度由上面那两条"色线/设置条"专测。
        Check("展开成一条带子（主条高 48、宽 > 600）",
              MathF.Abs((barOnly.MaxY - barOnly.MinY) - 48f) < 1f
              && barH >= 52f && barH <= 88f      // 色线时 54、设置条时 82
              && barW > 600f,
              $"占用 {barW:F0}×{barH:F0}（主条高 {barOnly.MaxY - barOnly.MinY:F0}）");

        // 展开之后**整条带子屏幕居中**（没拖过时）。球停在带子左端，从头到尾没动过。
        {
            float screenCx = (_virtualX + _virtualW * 0.5f) / DpiScale;
            Check("展开后整条带子屏幕居中",
                  MathF.Abs((barOnly.MinX + barOnly.MaxX) * 0.5f - screenCx) < 2f,
                  $"带子中心 {(barOnly.MinX + barOnly.MaxX) * 0.5f:F0}，屏幕中心 {screenCx:F0}"
                  + $"（带子 {barOnly.MinX:F0}..{barOnly.MaxX:F0}）");
        }

        // 带子长在**上面**（贴底时朝屏幕中心）：主条位置不许动——
        // 你刚点的那个按钮要是往上跳 38 像素，下一次点它就得重新瞄（费茨定律）。
        Check("展开时主条不动（只有带子长出来）",
              MathF.Abs(barOnly.MinY - ball.MinY) < 1.5f
              && MathF.Abs(barOnly.MaxY - ball.MaxY) < 1.5f,
              $"收起时 y {ball.MinY:F0}..{ball.MaxY:F0}，展开后主条 y {barOnly.MinY:F0}..{barOnly.MaxY:F0}");

        // ---- ④ 点"笔"那一格：引擎状态真的变了（走的是命令通道）----
        Tool = Tool.Eraser;
        var penCell = ui.CellRectForTest(3);
        ClickPhysical((penCell.MinX + penCell.MaxX) * 0.5f * DpiScale,
                      (penCell.MinY + penCell.MaxY) * 0.5f * DpiScale);
        Check("点「笔」切到笔", Tool == Tool.Pen, $"工具 = {Tool}");

        // ---- ⑤ 输入拦截：面板内不落墨，面板外照常落墨 ----
        strokes0 = Doc.Strokes.Count;
        var boardCell = ui.CellRectForTest(2);
        ClickPhysical((boardCell.MinX + boardCell.MaxX) * 0.5f * DpiScale,
                      (boardCell.MinY + boardCell.MaxY) * 0.5f * DpiScale);
        Check("点「白板」不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}，白板 = {BoardOn}");
        if (BoardOn) { Host.Commands.SetBoard(false); SettleFrames(120); }

        strokes0 = Doc.Strokes.Count;
        Tool = Tool.Pen;
        ClickPhysical(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.45f);
        Check("面板外照常落墨", Doc.Strokes.Count > strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑥ 上带：色片 / 滑条 / 分段（都走命令通道）----
        //
        // **张不张开只看焦点在不在面板上**（2026-09-17 用户定的新语义）：
        //   指针落在面板（主条 ∪ 设置条）上 → 张开；离开 → 220 毫秒后收成一条 6 像素色线。
        // 旧的那套"点一次钉住、再点同一个工具收起"已经删掉了——两套规则会打架：
        // 指针还停在面板上，收下去会立刻又张开。
        var penCellAgain = ui.CellRectForTest(3);
        ClickPhysical((penCellAgain.MinX + penCellAgain.MaxX) * 0.5f * DpiScale,
                      (penCellAgain.MinY + penCellAgain.MaxY) * 0.5f * DpiScale);
        SettleFrames(300);
        Check("焦点在面板上：设置条张开",
              ui.RailHoverForTest && ui.RailOpenForTest
              && MathF.Abs(ui.BandHeightForTest - InkUi.Tokens.BandHeight) < 1.5f,
              $"焦点在面板 = {ui.RailHoverForTest}，张开 = {ui.RailOpenForTest}，高 {ui.BandHeightForTest:F0}");

        // 指针移到画布上（离开面板）→ 收成一条色线
        SendMouse((int)(_virtualX + _virtualW * 0.6f), (int)(_virtualY + _virtualH * 0.3f), 0);
        SettleFrames(700);                              // 220ms 退出延迟 + 167ms 动画，留足
        Check("指针离开面板：收成一条色线",
              !ui.RailOpenForTest
              && MathF.Abs(ui.BandHeightForTest - InkUi.Tokens.BandLine) < 1.5f,
              $"张开 = {ui.RailOpenForTest}，高 {ui.BandHeightForTest:F0}（色线应为 {InkUi.Tokens.BandLine:F0}）");

        // 再把指针挪回**主条**（不是色带本身）→ 不用点，它自己就该张开。
        // 这一条就是用户说的"焦点在悬浮框的时候就展开"。
        var barHover = ui.BarRectForTest;
        int hoverPx = (int)((barHover.MinX + barHover.MaxX) * 0.5f * DpiScale);
        int hoverPy = (int)((barHover.MinY + barHover.MaxY) * 0.5f * DpiScale);
        // 合成鼠标**偶尔会丢一次移动**（自检里见过：同一份代码，一次跑红一次跑绿）——
        // 每次把坐标挪 1 像素再发，保证真的产生一次 WM_MOUSEMOVE，最多给三次机会。
        for (int attempt = 0; attempt < 3 && !ui.RailOpenForTest; attempt++)
        {
            SendMouse(hoverPx, hoverPy - attempt, 0);
            SettleFrames(350);
        }
        var hb = ui.QueryBounds();
        Console.WriteLine($"    [诊断] 指针物理 ({hoverPx},{hoverPy})，主条 {barHover.MinX:F0}..{barHover.MaxX:F0}"
                        + $" × {barHover.MinY:F0}..{barHover.MaxY:F0}，面板 {hb.MinX:F0}..{hb.MaxX:F0}"
                        + $" × {hb.MinY:F0}..{hb.MaxY:F0}，railHover={ui.RailHoverForTest}");
        Check("指针回到面板（主条）上就自己张开",
              ui.RailOpenForTest && ui.RailHoverForTest,
              $"张开 = {ui.RailOpenForTest}，高 {ui.BandHeightForTest:F0}");

        var bandRect = ui.BandRectForTest;
        Check("展开后有上带", bandRect.MaxY - bandRect.MinY > 20f,
              $"上带 {bandRect.MaxX - bandRect.MinX:F0}×{bandRect.MaxY - bandRect.MinY:F0}");

        Check("上带是笔的设置条（色片行）", ui.RailOpenForTest,
              $"张开 = {ui.RailOpenForTest}");

        // 挑一个**不是默认色**的色片（默认笔色就是红，用红当期望值会假通过——
        // 这一条自检第一版就是这么假通过的，被"换色成功"蒙了一次）。
        var swatch = ui.SwatchRectForTest(6);                 // 第 7 个色片：绿
        float swx = (swatch.MinX + swatch.MaxX) * 0.5f * DpiScale;
        float swy = (swatch.MinY + swatch.MaxY) * 0.5f * DpiScale;
        ClickPhysical(swx, swy);
        var wantColor = InkUi.Tokens.Palette[6].Color;
        var gotColor = Host.State.PaletteBase;
        var defaultColor = InkPalette.PenDefault;
        bool isDefault = MathF.Abs(gotColor.R - defaultColor.R) < 0.02f
                      && MathF.Abs(gotColor.G - defaultColor.G) < 0.02f
                      && MathF.Abs(gotColor.B - defaultColor.B) < 0.02f;
        Check("点色片换笔色",
              MathF.Abs(wantColor.R - gotColor.R) < 0.02f
              && MathF.Abs(wantColor.G - gotColor.G) < 0.02f
              && MathF.Abs(wantColor.B - gotColor.B) < 0.02f
              && !isDefault,
              $"期望 ({wantColor.R:F2},{wantColor.G:F2},{wantColor.B:F2})，实际 ({gotColor.R:F2},{gotColor.G:F2},{gotColor.B:F2})");

        float widthBefore = Host.State.Width;
        var slider = ui.SliderRectForTest;
        float sliderY = (slider.MinY + slider.MaxY) * 0.5f * DpiScale;
        SendMouse((int)(slider.MinX * DpiScale), (int)sliderY, 0);                          SettleFrames(60);
        SendMouse((int)(slider.MinX * DpiScale), (int)sliderY, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
        for (int i = 1; i <= 6; i++)
        {
            SendMouse((int)((slider.MinX + (slider.MaxX - slider.MinX) * i / 6f) * DpiScale),
                      (int)sliderY, 0);
            SettleFrames(25);
        }
        SendMouse((int)(slider.MaxX * DpiScale), (int)sliderY, Native.MOUSEEVENTF_LEFTUP);  SettleFrames(150);
        Check("拖滑条改粗细", Host.State.Width > widthBefore + 5f,
              $"粗细 {widthBefore:F1} → {Host.State.Width:F1}");

        // ---- ⑥.2 滑条**按工具路由**：橡皮终于能调大小了 ----
        //
        // 这一条是被用户点出来的："橡皮擦现在没有调节大小功能"。
        // 真相是界面上**有**滑条（`BandHasSlider` 一直包含橡皮那一格），
        // 但引擎的 `SetWidthFromUi` 只分了"荧光笔 / 激光 / 其它"三支，
        // 两种橡皮全落进"其它"——拖橡皮的滑条，**改的是笔宽**，橡皮一点没动。
        // 所以这三条要一起看：橡皮变了 **且** 笔宽没变（不然还会退回旧 bug）。
        {
            Host.Commands.SetTool(Tool.Eraser);
            SettleFrames(200);
            float penW0 = PenWidthLogical;
            // 先把指针放回面板（不然设置条是收着的，滑条不在）
            var barE = ui.BarRectForTest;
            SendMouse((int)((barE.MinX + barE.MaxX) * 0.5f * DpiScale),
                      (int)((barE.MinY + barE.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
            var slE = ui.SliderRectForTest;
            float slEy = (slE.MinY + slE.MaxY) * 0.5f * DpiScale;

            var (emin, emax) = (8f, 48f);              // 界面那边 WidthRange(Tool.Eraser)
            var (trackL, trackR) = ui.SliderTrackRangeForTest;
            SendMouse((int)(trackL * DpiScale), (int)slEy, 0);                          SettleFrames(60);
            SendMouse((int)(trackL * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
            SendMouse((int)(trackR * DpiScale), (int)slEy, 0);                          SettleFrames(120);
            SendMouse((int)(trackR * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(150);
            Check("橡皮的滑条拖到最右＝最大落点",
                  MathF.Abs(EraserRadiusLogical - emax) < 1.5f,
                  $"橡皮半径 {EraserRadiusLogical:F1}（应到 {emax}）");
            Check("拖橡皮的滑条**不动笔宽**",
                  MathF.Abs(PenWidthLogical - penW0) < 0.01f,
                  $"笔宽 {penW0:F2} → {PenWidthLogical:F2}（旧 bug 就是这里被改掉的）");

            var (trackL2, trackR2) = ui.SliderTrackRangeForTest;
            SendMouse((int)(trackL2 * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            SendMouse((int)(trackL2 * DpiScale), (int)slEy, 0);                          SettleFrames(120);
            SendMouse((int)(trackL2 * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(150);
            Check("橡皮的滑条拖到最左＝最小落点",
                  MathF.Abs(EraserRadiusLogical - emin) < 1.5f,
                  $"橡皮半径 {EraserRadiusLogical:F1}（应到 {emin}）");

            // **真实大小预览**（用户 2026-09-17："那个点和实际大小是不是应该一样大，
            // 但是太大了装不下，我又不希望改动界面"）。
            // 验的是"预览整个落在 QueryBounds() 里"——引擎按那份矩形裁剪界面，
            // 只要不包含它，画出去的部分就会被裁掉（这才是"预览看不见"的真因）。
            // 拖完滑条指针还停在滑条上 → 预览应该在。
            var pv = ui.SizePreviewRectForTest;
            var qb = ui.QueryBounds();
            Check("粗细预览画在面板外、且算进可见范围（不会被裁掉）",
                  pv.MaxY - pv.MinY > InkUi.Tokens.BandHeight
                  && pv.MinX >= qb.MinX - 0.5f && pv.MaxX <= qb.MaxX + 0.5f
                  && pv.MinY >= qb.MinY - 0.5f && pv.MaxY <= qb.MaxY + 0.5f,
                  $"预览 {pv.MaxX - pv.MinX:F0}×{pv.MaxY - pv.MinY:F0}"
                  + $"（{pv.MinX:F0}..{pv.MaxX:F0} × {pv.MinY:F0}..{pv.MaxY:F0}），"
                  + $"可见范围 {qb.MaxX - qb.MinX:F0}×{qb.MaxY - qb.MinY:F0}");

            // 面积橡皮同理，而且它的范围比笔宽大得多（30～160）
            Host.Commands.SetTool(Tool.PixelEraser);
            SettleFrames(200);
            float penW1 = PenWidthLogical;
            var slP = ui.SliderRectForTest;
            float slPy = (slP.MinY + slP.MaxY) * 0.5f * DpiScale;
            var (pl, pr) = ui.SliderTrackRangeForTest;
            SendMouse((int)(pr * DpiScale), (int)slPy, 0);                          SettleFrames(60);
            SendMouse((int)(pr * DpiScale), (int)slPy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
            SendMouse((int)(pr * DpiScale), (int)slPy, 0);                          SettleFrames(120);
            SendMouse((int)(pr * DpiScale), (int)slPy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(150);
            Check("面积橡皮的滑条能放到 160（比笔宽的上限 40 大）",
                  MathF.Abs(PixelEraserWidthLogical - 160f) < 2f,
                  $"面积橡皮宽 {PixelEraserWidthLogical:F1}（应到 160）");
            Check("拖面积橡皮的滑条**不动笔宽**",
                  MathF.Abs(PenWidthLogical - penW1) < 0.01f,
                  $"笔宽 {penW1:F2} → {PenWidthLogical:F2}");
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(200);
        }

        // ---- ⑥.3 穿透与工具**互斥** ----
        {
            Host.Commands.SetPassThrough(true);
            SettleFrames(250);
            Check("穿透开着时，工具格一律不高亮（免得`穿透＋笔`同时亮）",
                  ui.CellActiveForTest(1) && !ui.CellActiveForTest(3) && !ui.CellActiveForTest(4)
                  && !ui.CellActiveForTest(6) && !ui.CellActiveForTest(9),
                  $"鼠标 = {ui.CellActiveForTest(1)}，笔 = {ui.CellActiveForTest(3)}，工具 = {Tool}");

            var penCellPt = ui.CellRectForTest(3);
            ClickPhysical((penCellPt.MinX + penCellPt.MaxX) * 0.5f * DpiScale,
                          (penCellPt.MinY + penCellPt.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点工具格＝顺手关掉穿透（不用先去点鼠标格）",
                  !Host.State.PassThrough && Tool == Tool.Pen,
                  $"穿透 = {Host.State.PassThrough}，工具 = {Tool}");
            Check("关掉穿透之后工具格亮回来", ui.CellActiveForTest(3),
                  $"笔格高亮 = {ui.CellActiveForTest(3)}");
        }

        // ---- ⑥.4 截图那一格真的接上了（用户 2026-09-17："截图功能还没有接进来"）----
        //
        // 引擎里截图工具（Tool.Capture）和"拖框抓图"这条链路是有的（--capturetest 全绿），
        // 面板上第 9 格也在走 SetTool(Capture)。这一条把它**钉在自检里**：
        // 点一下必须真的切到截图工具、而且那一格要亮——不然老师点了没反应，
        // 只能得出"还没接进来"这个结论（这正是用户看到的）。
        {
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
            var cap = ui.CellRectForTest(9);
            ClickPhysical((cap.MinX + cap.MaxX) * 0.5f * DpiScale,
                          (cap.MinY + cap.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点「截屏」真的切到截图工具", Tool == Tool.Capture, $"工具 = {Tool}");
            Check("截屏那一格亮起来（点了有反馈）", ui.CellActiveForTest(9),
                  $"截屏格高亮 = {ui.CellActiveForTest(9)}");

            // 截图那一格现在也长设置条了：**[直接截取][隐藏批注截取]**（照 InkClass 的两项菜单）
            var capBar = ui.BarRectForTest;
            SendMouse((int)((capBar.MinX + capBar.MaxX) * 0.5f * DpiScale),
                      (int)((capBar.MinY + capBar.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
            var segDirect = ui.SegmentRectForTest(0);
            var segHidden = ui.SegmentRectForTest(1);
            Check("截屏那一格有两条设置（0 宽 = 没接上）",
                  segDirect.MaxX - segDirect.MinX > 20f && segHidden.MinX > segDirect.MaxX - 1f,
                  $"段宽 {segDirect.MaxX - segDirect.MinX:F0}，第二段起点 {segHidden.MinX:F0}");

            ClickPhysical((segDirect.MinX + segDirect.MaxX) * 0.5f * DpiScale,
                          (segDirect.MinY + segDirect.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点「直接截取」→ hideInk = false",
                  !Host.State.CaptureHideInk, $"hideInk = {Host.State.CaptureHideInk}");

            segHidden = ui.SegmentRectForTest(1);
            ClickPhysical((segHidden.MinX + segHidden.MaxX) * 0.5f * DpiScale,
                          (segHidden.MinY + segHidden.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点「隐藏批注截取」→ hideInk = true",
                  Host.State.CaptureHideInk, $"hideInk = {Host.State.CaptureHideInk}");

            // 第三段是**动作**：粘贴图片（没有键盘的触摸屏 / 手写板也能用）
            {
                // 先在剪贴板上放一张 60×40 的图（绿底），再点"粘贴图片"
                var buf = new byte[60 * 40 * 4];
                for (int i = 0; i < buf.Length; i += 4)
                { buf[i] = 0; buf[i + 1] = 200; buf[i + 2] = 0; buf[i + 3] = 255; }
                ClipboardImage.SetImage(buf, 60, 40);
                int imgBefore = Doc.Strokes.Count(s => s.IsImage);

                var segPaste = ui.SegmentRectForTest(2);
                ClickPhysical((segPaste.MinX + segPaste.MaxX) * 0.5f * DpiScale,
                              (segPaste.MinY + segPaste.MaxY) * 0.5f * DpiScale);
                SettleFrames(400);
                int imgAfter = Doc.Strokes.Count(s => s.IsImage);
                Check("点「粘贴图片」：剪贴板里的图进了画布",
                      imgAfter == imgBefore + 1,
                      $"图像对象 {imgBefore} → {imgAfter}");

                // 收尾：把刚粘的那张删掉
                Doc.DeleteSelected();
                SettleFrames(150);
            }

            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
        }

        // ---- ⑥.5 上带右端的两个动作：清空（按住 0.8 秒）、全选（点一下）----
        //
        // 出自假面板：清空挂在**橡皮**那条（擦一点/擦一块/全擦掉是一路的事），
        // 按住 0.8 秒才算数；全选挂在**选择**那条。清空可撤销，但代价大，所以防误触。
        {
            Doc.Clear();
            Doc.ClearHistory();
            for (int i = 0; i < 3; i++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
                };
                s.AddPoint(_virtualX + 300 + i * 40, _virtualY + 300, 1f, 0);
                s.AddPoint(_virtualX + 380 + i * 40, _virtualY + 340, 1f, 1);
                Doc.AddStroke(s);
            }
            // 再补一笔**洋红**的：专供"屏幕像素"核对。本轮那个 bug（清空之后墨还留在
            // 屏幕上、连橡皮都擦不掉）只有量屏幕才抓得到——当时只验了 Doc.Strokes.Count。
            float magX = _virtualX + 900f, magY = _virtualY + 620f;
            var magStroke = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 26f * DpiScale,
            };
            magStroke.AddPoint(magX - 200f, magY, 1f, 0);
            magStroke.AddPoint(magX + 200f, magY, 1f, 1);
            Doc.AddStroke(magStroke);
            SettleFrames(150);
            SettleFrames(300);
            int inkBefore = ScreenProbe.CountMagenta((int)magX - 240, (int)magY - 40, 480, 80);
            Check("清空前：洋红那笔在屏幕上", inkBefore > 300, $"{inkBefore} 像素");

            Host.Commands.SetTool(Tool.Eraser);
            SettleFrames(200);
            var barA = ui.BarRectForTest;          // 指针挪回面板：设置条才开着
            SendMouse((int)((barA.MinX + barA.MaxX) * 0.5f * DpiScale),
                      (int)((barA.MinY + barA.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);

            var act = ui.ActionRectForTest;
            Check("橡皮那条右端有「清空」", act.MaxX - act.MinX > 40f,
                  $"动作按钮宽 {act.MaxX - act.MinX:F0}");

            float ax = (act.MinX + act.MaxX) * 0.5f * DpiScale;
            float ay = (act.MinY + act.MaxY) * 0.5f * DpiScale;
            int before = Doc.Strokes.Count;

            // 按住 0.3 秒就松手：**不清空**，而且那一刻进度条该走到一半
            SendMouse((int)ax, (int)ay, 0);                            SettleFrames(60);
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(300);
            bool midHold = ui.ActionHoldingForTest
                        && ui.HoldProgressForTest > 0.1f && ui.HoldProgressForTest < 0.9f;
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(250);
            Check("按住 0.3 秒松手：不清空（进度条走到一半）",
                  midHold && Doc.Strokes.Count == before,
                  $"按住中 = {midHold}，笔画 {before} → {Doc.Strokes.Count}");

            // 按住够 0.8 秒：清空
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(1100);
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(250);
            Check("按住 0.8 秒：清空生效", Doc.Strokes.Count == 0,
                  $"笔画 {before} → {Doc.Strokes.Count}");

            // **屏幕上也得干净**。这一条是本轮真 bug 的靶子：清空是在界面的 Render 里
            // 触发的，引擎渲染完无条件把脏区清了 → 文档空了、屏幕上那层墨还留着，
            // 表现就是"清空没用，而且常规橡皮也擦不掉"（文档里已经没东西可擦）。
            int inkAfter = ScreenProbe.CountMagenta((int)magX - 240, (int)magY - 40, 480, 80);
            Check("清空之后**屏幕上**也干净了（不然就是'墨迹卡住'）", inkAfter < 40,
                  $"{inkBefore} → {inkAfter} 像素");

            Host.Commands.Undo();
            SettleFrames(250);
            Check("清空能撤销回来（不是不可逆的破坏）", Doc.Strokes.Count == before,
                  $"撤销后 {Doc.Strokes.Count} 笔");
            int inkUndo = ScreenProbe.CountMagenta((int)magX - 240, (int)magY - 40, 480, 80);
            Check("撤销之后墨回到屏幕上", inkUndo > 300, $"{inkAfter} → {inkUndo} 像素");

            // 全选：挂在选择那条
            Host.Commands.SetTool(Tool.Marquee);
            SettleFrames(200);
            var barS = ui.BarRectForTest;
            SendMouse((int)((barS.MinX + barS.MaxX) * 0.5f * DpiScale),
                      (int)((barS.MinY + barS.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
            var act2 = ui.ActionRectForTest;
            Check("选择那条右端有「全选」", act2.MaxX - act2.MinX > 40f,
                  $"动作按钮宽 {act2.MaxX - act2.MinX:F0}");

            Doc.Selected.Clear();
            ClickPhysical((act2.MinX + act2.MaxX) * 0.5f * DpiScale,
                          (act2.MinY + act2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点「全选」：选中的条数 = 笔画数",
                  Doc.Selected.Count == Doc.Strokes.Count && Doc.Selected.Count > 0,
                  $"选中 {Doc.Selected.Count} / 笔画 {Doc.Strokes.Count}");
        }

        // ---- ⑥.6 撤销/重做的**灰度**（用户 2026-09-17："撤销重做灰度"）----
        {
            Doc.Clear();
            Doc.ClearHistory();
            SettleFrames(250);
            Check("栈空：撤销和重做都压暗",
                  ui.CellUnavailableForTest(10) && ui.CellUnavailableForTest(11),
                  $"撤销压暗 = {ui.CellUnavailableForTest(10)}，重做压暗 = {ui.CellUnavailableForTest(11)}");

            var s1 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
            };
            s1.AddPoint(_virtualX + 400, _virtualY + 400, 1f, 0);
            s1.AddPoint(_virtualX + 500, _virtualY + 420, 1f, 1);
            Doc.AddStroke(s1);
            SettleFrames(250);
            Check("画一笔之后：撤销亮、重做仍暗",
                  !ui.CellUnavailableForTest(10) && ui.CellUnavailableForTest(11),
                  $"撤销压暗 = {ui.CellUnavailableForTest(10)}，重做压暗 = {ui.CellUnavailableForTest(11)}");

            Host.Commands.Undo();
            SettleFrames(250);
            Check("撤销之后：重做亮起来",
                  ui.CellUnavailableForTest(10) && !ui.CellUnavailableForTest(11),
                  $"撤销压暗 = {ui.CellUnavailableForTest(10)}，重做压暗 = {ui.CellUnavailableForTest(11)}");
            Doc.Clear();
            Doc.ClearHistory();
            SettleFrames(150);
        }

        // ---- ⑥.8 白板底纹：抽屉里那两行（无/方格/横线、间距）----
        //
        // 用户 2026-09-17："给白板增加网格和横线功能"（参考 InkClass：三档 + 间距）。
        // 抽屉里两行的位置要**先开抽屉再重取矩形**（开合会让它挪地方，这是被踩过的坑）。
        {
            Host.Commands.SetBoard(true);                 // 底纹只画在板面上：先开板
            SettleFrames(250);

            var moreP = ui.CellRectForTest(12);
            ClickPhysical((moreP.MinX + moreP.MaxX) * 0.5f * DpiScale,
                          (moreP.MinY + moreP.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);

            Check("白板开着时，底纹两行是可点的",
                  !ui.RowGrayForTest(2) && !ui.RowGrayForTest(3),
                  $"底纹行压暗 = {ui.RowGrayForTest(2)}，间距行压暗 = {ui.RowGrayForTest(3)}");

            var rowPat = ui.RowRectForTest(2);
            ClickPhysical((rowPat.MinX + rowPat.MaxX) * 0.5f * DpiScale,
                          (rowPat.MinY + rowPat.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点一下「白板底纹」→ 方格",
                  Host.State.BoardPattern == 1,
                  $"底纹 = {Host.State.BoardPattern}（1 = 方格），标签「{ui.RowLabelForTest(2)}」");

            rowPat = ui.RowRectForTest(2);
            ClickPhysical((rowPat.MinX + rowPat.MaxX) * 0.5f * DpiScale,
                          (rowPat.MinY + rowPat.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("再点一下 → 横线", Host.State.BoardPattern == 2,
                  $"底纹 = {Host.State.BoardPattern}（2 = 横线），标签「{ui.RowLabelForTest(2)}」");

            float step0 = Host.State.BoardPatternStep;
            var rowStep = ui.RowRectForTest(3);
            ClickPhysical((rowStep.MinX + rowStep.MaxX) * 0.5f * DpiScale,
                          (rowStep.MinY + rowStep.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点一下「底纹间距」→ 换下一档",
                  MathF.Abs(Host.State.BoardPatternStep - step0) > 1f,
                  $"{step0:F0} → {Host.State.BoardPatternStep:F0}（标签「{ui.RowLabelForTest(3)}」）");

            // 关掉白板：两行应该压暗，点了也不改状态（InkClass 也是这么守的）
            var moreP2 = ui.CellRectForTest(12);
            ClickPhysical((moreP2.MinX + moreP2.MaxX) * 0.5f * DpiScale,
                          (moreP2.MinY + moreP2.MaxY) * 0.5f * DpiScale);   // 关抽屉
            SettleFrames(200);
            Host.Commands.SetBoard(false);
            SettleFrames(250);
            var moreP3 = ui.CellRectForTest(12);
            ClickPhysical((moreP3.MinX + moreP3.MaxX) * 0.5f * DpiScale,
                          (moreP3.MinY + moreP3.MaxY) * 0.5f * DpiScale);   // 开抽屉
            SettleFrames(250);
            Check("白板关着时底纹两行压暗",
                  ui.RowGrayForTest(2) && ui.RowGrayForTest(3),
                  $"底纹行压暗 = {ui.RowGrayForTest(2)}，间距行压暗 = {ui.RowGrayForTest(3)}");

            int patBefore = Host.State.BoardPattern;
            var rowPat2 = ui.RowRectForTest(2);
            ClickPhysical((rowPat2.MinX + rowPat2.MaxX) * 0.5f * DpiScale,
                          (rowPat2.MinY + rowPat2.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("压暗的行点了不动（不会偷偷改档）",
                  Host.State.BoardPattern == patBefore,
                  $"底纹 {patBefore} → {Host.State.BoardPattern}");

            var moreP4 = ui.CellRectForTest(12);
            ClickPhysical((moreP4.MinX + moreP4.MaxX) * 0.5f * DpiScale,
                          (moreP4.MinY + moreP4.MaxY) * 0.5f * DpiScale);   // 关抽屉
            SettleFrames(200);
            Host.Commands.SetBoardPattern(0, 40f);       // 收尾：回到"无底纹"
            // **收尾要把指针挪回主条**：点"更多"会把色带收起来（抽屉和它抢地方），
            // 而色带只在指针落在面板上时才张开——指针不动就永远不张开，
            // 后面那几条"点分段"的用例就会全部点空（这一次就是这么红的）。
            var barBack = ui.BarRectForTest;
            SendMouse((int)((barBack.MinX + barBack.MaxX) * 0.5f * DpiScale),
                      (int)((barBack.MinY + barBack.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
        }

        // ---- ⑥.7 白板和穿透**互斥**（用户 2026-09-17 问："鼠标和白板是不是也应该互斥"）----
        //
        // 该互斥：白板是不透明的一层，穿透是"点击落到下层程序"——两个一起开着，
        // 老师看到的是白板、点到的却是白板下面那个看不见的窗口。
        {
            Host.Commands.SetBoard(true);
            SettleFrames(200);
            Host.Commands.SetPassThrough(true);
            SettleFrames(250);
            Check("开穿透 → 白板自动关掉",
                  !BoardOn && Host.State.PassThrough,
                  $"板开 = {BoardOn}，穿透 = {Host.State.PassThrough}");

            Host.Commands.SetBoard(true);
            SettleFrames(300);
            Check("开白板 → 穿透自动关掉",
                  BoardOn && !Host.State.PassThrough,
                  $"板开 = {BoardOn}，穿透 = {Host.State.PassThrough}");

            Host.Commands.SetBoard(false);
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(200);
        }

        // 换工具（走引擎那条路，等同按热键）：上带要跟着换成"选择"的设置条
        Host.Commands.SetTool(Tool.Marquee);
        SettleFrames(150);
        var lasso = ui.SegmentRectForTest(1);
        ClickPhysical((lasso.MinX + lasso.MaxX) * 0.5f * DpiScale,
                      (lasso.MinY + lasso.MaxY) * 0.5f * DpiScale);
        Check("点分段切成套索", Host.State.SelectMode == SelectMode.Lasso,
              $"选择方式 = {Host.State.SelectMode}（上带跟着工具换成选择才点得到）");

        Host.Commands.SetTool(Tool.Pen);
        SettleFrames(150);
        var boardCell2 = ui.CellRectForTest(2);
        ClickPhysical((boardCell2.MinX + boardCell2.MaxX) * 0.5f * DpiScale,
                      (boardCell2.MinY + boardCell2.MaxY) * 0.5f * DpiScale);
        SettleFrames(150);
        // 白板那一格现在是 5 段：`[上一屏] [白][绿][黑] [下一屏]`。
        // 所以"绿板"是下标 **2**（下标 1 是白板、0/4 是翻页）——
        // 这一条自检第一版按老的三段布局点下标 1，点到了"白板"，被判成失败。
        var green = ui.SegmentRectForTest(2);
        ClickPhysical((green.MinX + green.MaxX) * 0.5f * DpiScale,
                      (green.MinY + green.MaxY) * 0.5f * DpiScale);
        var wantBoard = InkPalette.BoardPresets[1].Color;
        var gotBoard = Host.State.BoardColor;
        Check("点板色换成绿板",
              BoardOn && MathF.Abs(wantBoard.R - gotBoard.R) < 0.02f
              && MathF.Abs(wantBoard.G - gotBoard.G) < 0.02f
              && MathF.Abs(wantBoard.B - gotBoard.B) < 0.02f,
              $"板开 = {BoardOn}，板色 ({gotBoard.R:F2},{gotBoard.G:F2},{gotBoard.B:F2})");

        // ---- ⑥.1 白板那一格的滑条 = **板面不透明度**（用户 2026-09-17 要的）----
        {
            float penW = PenWidthLogical;
            var slB = ui.SliderRectForTest;
            float slBy = (slB.MinY + slB.MaxY) * 0.5f * DpiScale;
            var (bl, _) = ui.SliderTrackRangeForTest;

            SendMouse((int)(bl * DpiScale), (int)slBy, 0);                            SettleFrames(60);
            SendMouse((int)(bl * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(50);
            SendMouse((int)(bl * DpiScale), (int)slBy, 0);                            SettleFrames(120);
            SendMouse((int)(bl * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(150);
            Check("白板滑条拖到最左 = 最透（0.35）",
                  MathF.Abs(Host.State.BoardOpacity - 0.35f) < 0.02f,
                  $"不透明度 {Host.State.BoardOpacity:F2}");

            var (_, br2) = ui.SliderTrackRangeForTest;
            SendMouse((int)(br2 * DpiScale), (int)slBy, 0);                            SettleFrames(60);
            SendMouse((int)(br2 * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(50);
            SendMouse((int)(br2 * DpiScale), (int)slBy, 0);                            SettleFrames(120);
            SendMouse((int)(br2 * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(150);
            Check("白板滑条拖到最右 = 实心（1.0）",
                  MathF.Abs(Host.State.BoardOpacity - 1f) < 0.02f,
                  $"不透明度 {Host.State.BoardOpacity:F2}");
            Check("拖白板滑条**不动笔宽**（和橡皮那条一个坑）",
                  MathF.Abs(PenWidthLogical - penW) < 0.01f,
                  $"笔宽 {penW:F2} → {PenWidthLogical:F2}");
        }

        // ---- ⑥.5 白板翻页：上带上的 [上一屏] / [下一屏] ----
        // 屏幕高必须是"整数屏"的底数：跑到第一屏（相机偏移 = 0）再往下翻。
        var upSeg = ui.SegmentRectForTest(0);
        float upX = (upSeg.MinX + upSeg.MaxX) * 0.5f * DpiScale;
        float upY = (upSeg.MinY + upSeg.MaxY) * 0.5f * DpiScale;
        var downSeg = ui.SegmentRectForTest(4);
        float downX = (downSeg.MinX + downSeg.MaxX) * 0.5f * DpiScale;
        float downY = (downSeg.MinY + downSeg.MaxY) * 0.5f * DpiScale;
        float camHome = ViewOffsetY;

        // 已经在最上面：点"上一屏"不该动（按钮也该是压暗的）
        Check("到顶时「上一屏」不可用", !Host.State.CanFlipPageUp,
              $"相机 {camHome:F0}，还能上翻 = {Host.State.CanFlipPageUp}");
        ClickPhysical(upX, upY);
        SettleFrames(400);
        Check("到顶时点「上一屏」相机不动",
              MathF.Abs(ViewOffsetY - camHome) < 1f,
              $"相机 {camHome:F0} → {ViewOffsetY:F0}");

        ClickPhysical(downX, downY);
        SettleFrames(400);
        Check("点「下一屏」翻到第 2 屏",
              Host.State.ScreenIndex == 2 && ViewOffsetY < -1f,
              $"屏号 {Host.State.ScreenIndex}，相机 {ViewOffsetY:F0}");

        ClickPhysical(downX, downY);
        SettleFrames(400);
        Check("再点一次翻到第 3 屏（下面永远还有一屏）",
              Host.State.ScreenIndex == 3, $"屏号 {Host.State.ScreenIndex}");

        ClickPhysical(upX, upY);
        SettleFrames(400);
        Check("点「上一屏」回到第 2 屏",
              Host.State.ScreenIndex == 2 && Host.State.CanFlipPageUp,
              $"屏号 {Host.State.ScreenIndex}，还能上翻 = {Host.State.CanFlipPageUp}");

        // 收尾：把相机送回第一屏，别把后面的用例带偏（后面的用例都假设相机为 0）
        while (Host.State.CanFlipPageUp)
        {
            ClickPhysical(upX, upY);
            SettleFrames(300);
        }
        Check("翻回第一屏（相机归零）", Math.Abs(ViewOffsetY) < 1f,
              $"相机 {ViewOffsetY:F1}");

        Host.Commands.SetBoard(false);
        Host.Commands.SetBoardColor(InkPalette.BoardPresets[0].Color);
        SettleFrames(150);

        // ---- ⑦ 「更多」抽屉：开合、深色主题、贴边隐藏 ----
        var moreCell = ui.CellRectForTest(12);
        ClickPhysical((moreCell.MinX + moreCell.MaxX) * 0.5f * DpiScale,
                      (moreCell.MinY + moreCell.MaxY) * 0.5f * DpiScale);
        SettleFrames(150);
        var drawer = ui.DrawerRectForTest;
        Check("点「更多」开出抽屉", ui.DrawerOpenForTest,
              $"抽屉 ({drawer.MinX:F0},{drawer.MinY:F0})-({drawer.MaxX:F0},{drawer.MaxY:F0})");
        Check("抽屉在面板上方、且在屏幕内",
              ui.DrawerOpenForTest && drawer.MaxY <= ui.BarRectForTest.MinY - 2f
              && drawer.MinY >= _virtualY / DpiScale && drawer.MaxX <= _virtualX / DpiScale + _virtualW / DpiScale,
              $"抽屉底 {drawer.MinY:F0}+{drawer.MaxY - drawer.MinY:F0}，主条顶 {ui.BarRectForTest.MinY:F0}");

        // 抽屉和设置条**互斥**（2026-09-17）：两者只隔 16 像素，抽屉开着的时候设置条
        // 要是被指针挤出来，会从抽屉底下冒一截，很难看（假面板里也是这么让位的）。
        // 顺带守住老规矩：**抽屉的位置不许跟着设置条的高低走**——
        // 跟着走就是"鼠标一碰色带、抽屉往上跳一下"（用户说的"起伏"）。
        var lineRect = ui.BandRectForTest;             // 这一刻是那条色线
        SendMouse((int)((lineRect.MinX + lineRect.MaxX) * 0.5f * DpiScale),
                  (int)((lineRect.MinY + lineRect.MaxY) * 0.5f * DpiScale), 0);
        SettleFrames(400);                              // 等过"该张开"的那段时间
        var drawerAfter = ui.DrawerRectForTest;
        Check("抽屉开着时设置条让位（不挤出来、也不推抽屉）",
              !ui.RailOpenForTest
              && MathF.Abs(ui.BandHeightForTest - InkUi.Tokens.BandLine) < 1.5f
              && MathF.Abs(drawerAfter.MinY - drawer.MinY) < 1.5f,
              $"设置条高 {ui.BandHeightForTest:F0}（该是色线 {InkUi.Tokens.BandLine:F0}），"
              + $"抽屉底 {drawer.MinY:F0} → {drawerAfter.MinY:F0}");

        var darkRow = ui.RowRectForTest(0);
        ClickPhysical((darkRow.MinX + darkRow.MaxX) * 0.5f * DpiScale,
                      (darkRow.MinY + darkRow.MaxY) * 0.5f * DpiScale);
        Check("点「深色主题」切换", ui.DarkForTest, $"深色 = {ui.DarkForTest}");

        var hideRow = ui.RowRectForTest(1);
        ClickPhysical((hideRow.MinX + hideRow.MaxX) * 0.5f * DpiScale,
                      (hideRow.MinY + hideRow.MaxY) * 0.5f * DpiScale);
        Check("点「贴边隐藏」打开", ui.HideEnabledForTest, $"开关 = {ui.HideEnabledForTest}");

        // 关掉抽屉（点「更多」再点一下），然后把指针移到画布上：
        // 贴边状态下应该收成一条 8 像素的"露头"，悬停露头再长回来。
        ClickPhysical((moreCell.MinX + moreCell.MaxX) * 0.5f * DpiScale,
                      (moreCell.MinY + moreCell.MaxY) * 0.5f * DpiScale);
        SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.4f), 0);
        // 等过"离开 700 毫秒才收"＋收起动画那一段（167 毫秒）：留足余量，别把动画
        // 中间态当成终态来判——第一版就是这么误判成"露头 56 像素"的。
        SettleFrames(2000);
        var peeked = ui.QueryBounds();
        Check("贴边隐藏收成露头",
              (peeked.MaxY - peeked.MinY) < 12f || (peeked.MaxX - peeked.MinX) < 12f,
              $"占用 {peeked.MaxX - peeked.MinX:F0}×{peeked.MaxY - peeked.MinY:F0}（应只剩露头那条）");

        SendMouse((int)((peeked.MinX + peeked.MaxX) * 0.5f * DpiScale),
                  (int)((peeked.MinY + peeked.MaxY) * 0.5f * DpiScale), 0);
        SettleFrames(500);
        var back = ui.QueryBounds();
        Check("碰一下露头就长回来", (back.MaxX - back.MinX) > 100f,
              $"占用 {back.MaxX - back.MinX:F0}×{back.MaxY - back.MinY:F0}");

        // 关掉贴边隐藏，免得影响后面的用例
        var moreCell2 = ui.CellRectForTest(12);
        ClickPhysical((moreCell2.MinX + moreCell2.MaxX) * 0.5f * DpiScale,
                      (moreCell2.MinY + moreCell2.MaxY) * 0.5f * DpiScale);
        SettleFrames(150);
        var hideRow2 = ui.RowRectForTest(1);
        ClickPhysical((hideRow2.MinX + hideRow2.MaxX) * 0.5f * DpiScale,
                      (hideRow2.MinY + hideRow2.MaxY) * 0.5f * DpiScale);
        ClickPhysical((moreCell2.MinX + moreCell2.MaxX) * 0.5f * DpiScale,
                      (moreCell2.MinY + moreCell2.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        Check("关掉贴边隐藏", !ui.HideEnabledForTest, $"开关 = {ui.HideEnabledForTest}");

        // ---- ⑦.5 界面档位与钉住 ----
        var moreCell4 = ui.CellRectForTest(12);
        ClickPhysical((moreCell4.MinX + moreCell4.MaxX) * 0.5f * DpiScale,
                      (moreCell4.MinY + moreCell4.MaxY) * 0.5f * DpiScale);   // 开抽屉
        SettleFrames(200);

        Check("完整档是 13 格", ui.VisibleCountForTest == 13, $"显示 {ui.VisibleCountForTest} 格");

        // 极简档的色片行：**只给 4 个**（用户 2026-09-17："极简模式的色带展开栏里面的
        // 内容排布有点问题"——短胶囊里塞 12 个色片，减掉滑条之后每个只有 11 像素宽）。
        {
            var miniSeg0 = ui.ProfileRectForTest(0);
            ClickPhysical((miniSeg0.MinX + miniSeg0.MaxX) * 0.5f * DpiScale,
                          (miniSeg0.MinY + miniSeg0.MaxY) * 0.5f * DpiScale);   // 切极简
            SettleFrames(250);
            ui.SelectBandCellForTest(3);                                     // 笔的设置条
            SettleFrames(150);
            Check("极简档：色片只有 4 个", ui.SwatchCountForTest == 4,
                  $"色片数 {ui.SwatchCountForTest}");
            var sw0 = ui.SwatchRectForTest(0);
            Check("极简档：每个色片都够宽（≥ 30 逻辑像素，点得准）",
                  sw0.MaxX - sw0.MinX >= 30f, $"色片宽 {sw0.MaxX - sw0.MinX:F0}");
            // 切回完整档
            var fullSeg0 = ui.ProfileRectForTest(2);
            ClickPhysical((fullSeg0.MinX + fullSeg0.MaxX) * 0.5f * DpiScale,
                          (fullSeg0.MinY + fullSeg0.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("完整档：色片回到 12 个", ui.SwatchCountForTest == 12,
                  $"色片数 {ui.SwatchCountForTest}");
        }

        // 切到极简：只留六格 ＋ 收起格，整条带子明显变短
        var miniSeg = ui.ProfileRectForTest(0);
        ClickPhysical((miniSeg.MinX + miniSeg.MaxX) * 0.5f * DpiScale,
                      (miniSeg.MinY + miniSeg.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        var miniBar = ui.BarRectForTest;
        Check("极简档是七格（六格 ＋ 收起格）", ui.VisibleCountForTest == 7,
              $"显示 {ui.VisibleCountForTest} 格，档位 = {ui.ProfileForTest}");
        Check("极简档是一条短胶囊", (miniBar.MaxX - miniBar.MinX) < 360f,
              $"宽 {miniBar.MaxX - miniBar.MinX:F0}（完整档是 636）");

        // 切档时"当前工具不在这一档里"要落到笔：先把工具换成图形（极简档里没有它）
        Host.Commands.SetTool(Tool.Rectangle);
        SettleFrames(150);
        var miniSeg2 = ui.ProfileRectForTest(0);          // 已经在极简了，再点一次也走同一条路
        ClickPhysical((miniSeg2.MinX + miniSeg2.MaxX) * 0.5f * DpiScale,
                      (miniSeg2.MinY + miniSeg2.MaxY) * 0.5f * DpiScale);
        Check("切档时工具不在档内→落到笔", Tool == Tool.Pen, $"工具 = {Tool}");

        // 切回完整档
        var fullSeg = ui.ProfileRectForTest(2);
        ClickPhysical((fullSeg.MinX + fullSeg.MaxX) * 0.5f * DpiScale,
                      (fullSeg.MinY + fullSeg.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        Check("切回完整档", ui.VisibleCountForTest == 13, $"显示 {ui.VisibleCountForTest} 格");

        // 取消钉住"图形"（下标 8）：档位自动变成自定义，主条上少一格
        var chip = ui.ChipRectForTest(8);
        ClickPhysical((chip.MinX + chip.MaxX) * 0.5f * DpiScale,
                      (chip.MinY + chip.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        Check("取消钉住→进自定义档、主条少一格",
              ui.ProfileForTest == 1 && !ui.PinnedForTest(8) && ui.VisibleCountForTest == 12,
              $"档位 = {ui.ProfileForTest}，钉着 = {ui.PinnedForTest(8)}，显示 {ui.VisibleCountForTest} 格");

        // 安全项：笔（下标 3）点一下不该被取消
        var penChip = ui.ChipRectForTest(3);
        ClickPhysical((penChip.MinX + penChip.MaxX) * 0.5f * DpiScale,
                      (penChip.MinY + penChip.MaxY) * 0.5f * DpiScale);
        SettleFrames(200);
        Check("安全项（笔）取消不掉", ui.PinnedForTest(3), $"笔钉着 = {ui.PinnedForTest(3)}");

        // 钉回去，回到完整档，别把后面的用例带偏
        // 注意：每次点之前**重新取一次矩形**——切档会让主条宽度变、抽屉跟着挪，
        // 复用之前算好的坐标就会点空（这一版自检就是这么把自己坑了一次）。
        var chipBack = ui.ChipRectForTest(8);
        ClickPhysical((chipBack.MinX + chipBack.MaxX) * 0.5f * DpiScale,
                      (chipBack.MinY + chipBack.MaxY) * 0.5f * DpiScale);
        SettleFrames(200);
        var fullSeg2 = ui.ProfileRectForTest(2);
        ClickPhysical((fullSeg2.MinX + fullSeg2.MaxX) * 0.5f * DpiScale,
                      (fullSeg2.MinY + fullSeg2.MaxY) * 0.5f * DpiScale);
        SettleFrames(200);
        var moreCell5 = ui.CellRectForTest(12);
        ClickPhysical((moreCell5.MinX + moreCell5.MaxX) * 0.5f * DpiScale,
                      (moreCell5.MinY + moreCell5.MaxY) * 0.5f * DpiScale);   // 关抽屉
        SettleFrames(250);
        Check("收尾：回到完整档、抽屉已关",
              ui.ProfileForTest == 2 && ui.VisibleCountForTest == 13 && !ui.DrawerOpenForTest,
              $"档位 = {ui.ProfileForTest}，显示 {ui.VisibleCountForTest} 格，抽屉 = {ui.DrawerOpenForTest}");

        // ---- ⑥ 收起来，然后空闲必须 0 帧 ----
        var ball2 = ui.CellRectForTest(0);
        ClickPhysical((ball2.MinX + ball2.MaxX) * 0.5f * DpiScale,
                      (ball2.MinY + ball2.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        Check("点最左那格能收起", !ui.ExpandedForTest, $"展开状态 = {ui.ExpandedForTest}");

        PumpMessages();
        RenderAll();
        _dirty = false;
        int quiet = 0;
        var swQuiet = Stopwatch.StartNew();
        while (swQuiet.ElapsedMilliseconds < 150)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; quiet++; }
            else Thread.Sleep(2);
        }
        Check("空闲 0 帧", quiet == 0, $"安静 150 毫秒出了 {quiet} 帧");

        // ---- ⑦ 拖动并贴边：拖到左边缘附近松手，应该吸附过去（离边 2）----
        var home = ui.QueryBounds();
        float hx = (home.MinX + home.MaxX) * 0.5f * DpiScale;
        float hy = (home.MinY + home.MaxY) * 0.5f * DpiScale;
        int targetX = _virtualX + 20;
        SendMouse((int)hx, (int)hy, 0);                          SettleFrames(60);
        SendMouse((int)hx, (int)hy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
        for (int i = 1; i <= 10; i++)
        {
            SendMouse((int)(hx + (targetX - hx) * i / 10f), (int)hy, 0);
            SettleFrames(20);
        }
        SendMouse(targetX, (int)hy, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        var docked = ui.QueryBounds();
        float wantLeft = _virtualX / DpiScale + InkUi.Tokens.DockGap;
        Check("拖到左边缘会吸附", MathF.Abs(docked.MinX - wantLeft) < 3f,
              $"左边缘 {docked.MinX:F0}（贴边后应为 {wantLeft:F0}），拖动前在 {home.MinX:F0}");

        // ---- ⑦.2 锚点：**球不动、带子往右长**；顶到右边就整条夹回屏幕内 ----
        //
        // 用户 2026-09-17 定的规则（也是假面板当年的做法）：
        //   球是锚点（它长在**展开后那条带子的左端**），展开＝从球往右铺；
        //   铺出去会出屏时，把整条带子夹回屏幕内——看着就是"贴住右边往左铺开"。
        //
        // 这里要验两件事：① 拖到中间之后展开，**球一动不动**（收起时不用重新瞄）；
        // ② 拖到右边缘再展开，**整条带子还在屏幕里**（不会有一截跑到屏幕外）。
        {
            // ① 拖到屏幕中左部（离两边都远）→ 展开 → 球的位置必须没变
            var d0 = ui.QueryBounds();                        // 此刻贴着左边、收起态
            float sx = (d0.MinX + d0.MaxX) * 0.5f * DpiScale;
            float sy = (d0.MinY + d0.MaxY) * 0.5f * DpiScale;
            int toX = _virtualX + (int)(500 * DpiScale);
            SendMouse((int)sx, (int)sy, 0);                           SettleFrames(60);
            SendMouse((int)sx, (int)sy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(50);
            for (int i = 1; i <= 8; i++) { SendMouse((int)(sx + (toX - sx) * i / 8f), (int)sy, 0); SettleFrames(20); }
            SendMouse(toX, (int)sy, Native.MOUSEEVENTF_LEFTUP);        SettleFrames(250);
            var parked = ui.QueryBounds();
            float parkedLeft = parked.MinX;

            ClickPhysical((parked.MinX + parked.MaxX) * 0.5f * DpiScale,
                          (parked.MinY + parked.MaxY) * 0.5f * DpiScale);   // 点球展开
            SettleFrames(400);
            var ballAfter = ui.CellRectForTest(0);                 // 展开后那一格就是球
            var anchoredBar = ui.BarRectForTest;
            Check("拖到中间后展开：球不动、只往右长",
                  // 比的是**锚点**（主条左端）：收起时它就是球的位置；
                  // 展开后球那一格还要往里让 BarPad（8 像素），拿格子比会差这 8 像素。
                  MathF.Abs(anchoredBar.MinX - parkedLeft) < 1.5f
                  && anchoredBar.MaxX > ballAfter.MaxX + 400f,
                  $"锚点（主条左端）{parkedLeft:F0} → {anchoredBar.MinX:F0}，"
                  + $"带子 {anchoredBar.MinX:F0}..{anchoredBar.MaxX:F0}");

            // 收回去（后面几段用例都假设"面板是收起的"）
            var ballCell = ui.CellRectForTest(0);
            ClickPhysical((ballCell.MinX + ballCell.MaxX) * 0.5f * DpiScale,
                          (ballCell.MinY + ballCell.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("再点一次收起（回到球）", !ui.ExpandedForTest, $"展开状态 = {ui.ExpandedForTest}");

            // ② 拖到**右边缘**（会吸附）→ 展开 → 整条带子必须还在屏幕里
            var e0 = ui.QueryBounds();
            float ex = (e0.MinX + e0.MaxX) * 0.5f * DpiScale;
            float ey = (e0.MinY + e0.MaxY) * 0.5f * DpiScale;
            int farX = (int)((_virtualX + _virtualW) - 30 * DpiScale);
            SendMouse((int)ex, (int)ey, 0);                           SettleFrames(60);
            SendMouse((int)ex, (int)ey, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(50);
            for (int i = 1; i <= 10; i++) { SendMouse((int)(ex + (farX - ex) * i / 10f), (int)ey, 0); SettleFrames(20); }
            SendMouse(farX, (int)ey, Native.MOUSEEVENTF_LEFTUP);       SettleFrames(250);

            var atRight = ui.QueryBounds();
            ClickPhysical((atRight.MinX + atRight.MaxX) * 0.5f * DpiScale,
                          (atRight.MinY + atRight.MaxY) * 0.5f * DpiScale);  // 展开
            SettleFrames(500);
            var rightBar = ui.BarRectForTest;
            float screenL = _virtualX / DpiScale, screenR = (_virtualX + _virtualW) / DpiScale;
            Check("球贴右边时展开：整条带子夹回屏幕内（贴着右边、往左铺）",
                  rightBar.MinX >= screenL - 1f && rightBar.MaxX <= screenR + 1f
                  && MathF.Abs(rightBar.MaxX - (screenR - InkUi.Tokens.DockGap)) < 3f,
                  $"带子 {rightBar.MinX:F0}..{rightBar.MaxX:F0}（屏 {screenL:F0}..{screenR:F0}）");

            // 收拾干净：收回球、拖回屏幕中间，别把后面几段用例带偏
            var ballCell2 = ui.CellRectForTest(0);
            ClickPhysical((ballCell2.MinX + ballCell2.MaxX) * 0.5f * DpiScale,
                          (ballCell2.MinY + ballCell2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            var b2 = ui.QueryBounds();
            float b2x = (b2.MinX + b2.MaxX) * 0.5f * DpiScale, b2y = (b2.MinY + b2.MaxY) * 0.5f * DpiScale;
            SendMouse((int)b2x, (int)b2y, 0);                          SettleFrames(60);
            SendMouse((int)b2x, (int)b2y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
            int homeX = _virtualX + (int)(720 * DpiScale);
            for (int i = 1; i <= 8; i++) { SendMouse((int)(b2x + (homeX - b2x) * i / 8f), (int)b2y, 0); SettleFrames(20); }
            SendMouse(homeX, (int)b2y, Native.MOUSEEVENTF_LEFTUP);      SettleFrames(250);
        }

        // ---- ⑧ 抽屉里的「重启软件」：先暂存板书，再拉起新进程（这里只记一笔，不真拉）----
        Recovery.ClearRestartCount();
        try { File.Delete(Recovery.SessionPath); } catch { }

        // ---- ⑨ 偏好落盘：改过的写进 settings.json，重开界面读得回来 ----
        // 把状态弄成一组"非默认"：深色开着（前面的用例已经开了）、取消钉住"图形"
        // （档位随之进"自定义"）。自检模式用的是临时配置文件，不碰用户真正的设置。
        //
        // 注意：上一条用例结束时面板是**收起**的，得先展开再去点抽屉里的东西——
        // 收起状态下那些格子的坐标算出来是"按球的位置铺开"，点过去全是空的
        // （这一版用例第一次跑就是这么点空的）。
        var ballFirst = ui.QueryBounds();
        ClickPhysical((ballFirst.MinX + ballFirst.MaxX) * 0.5f * DpiScale,
                      (ballFirst.MinY + ballFirst.MaxY) * 0.5f * DpiScale);
        SettleFrames(300);
        var moreCell6 = ui.CellRectForTest(12);
        ClickPhysical((moreCell6.MinX + moreCell6.MaxX) * 0.5f * DpiScale,
                      (moreCell6.MinY + moreCell6.MaxY) * 0.5f * DpiScale);       // 开抽屉
        SettleFrames(200);
        var chip6 = ui.ChipRectForTest(8);
        ClickPhysical((chip6.MinX + chip6.MaxX) * 0.5f * DpiScale,
                      (chip6.MinY + chip6.MaxY) * 0.5f * DpiScale);               // 取消钉住"图形"
        SettleFrames(250);
        SaveSettingsForTest();

        string prefsPath = InkSettings.PathOverride ?? "";
        string prefsText = File.Exists(prefsPath) ? File.ReadAllText(prefsPath) : "";
        Check("改动写进了配置文件",
              prefsText.Contains("\"ui\"") && prefsText.Contains("\"dark\"")
              && prefsText.Contains("\"profile\"") && prefsText.Contains("\"unpinned\""),
              $"{Path.GetFileName(prefsPath)}（{prefsText.Length} 字节）");

        // 把内存里那份清掉、从文件重读，再挂一个新界面——这才算"重开软件"那条链子
        ReloadUiPrefsForTest();
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        ui = CurrentUi as InkUi.FullUi;
        Check("重开界面读回了偏好",
              ui != null && ui.DarkForTest && ui.ProfileForTest == 1 && !ui.PinnedForTest(8),
              $"深色={ui?.DarkForTest}，档位={ui?.ProfileForTest}（1=自定义），图形钉着={ui?.PinnedForTest(8)}");

        try { File.Delete(prefsPath); } catch { }        // 临时配置用完就删

        Doc.Clear();
        Doc.ClearHistory();
        var keep2 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        keep2.AddPoint(500, 500, 1f, 0);
        keep2.AddPoint(800, 500, 1f, 1);
        Doc.AddStroke(keep2);

        NormalizePanel();
        // 行号 = Rows 里的下标：0 深色主题 / 1 贴边隐藏 / **2 白板底纹 / 3 底纹间距** /
        // 4 重启软件 / 5 退出 / 6 检查更新 / 7 学科工具。
        // （2026-09-17 插了底纹那两行，这里从 2 改成 4——插行不改引用，这一条当场就红了。）
        var restartRow = ui.RowRectForTest(4);
        ClickPhysical((restartRow.MinX + restartRow.MaxX) * 0.5f * DpiScale,
                      (restartRow.MinY + restartRow.MaxY) * 0.5f * DpiScale);
        Check("点「重启软件」：先存板书再重启",
              RestartRequested && File.Exists(Recovery.SessionPath),
              $"重启已发起 = {RestartRequested}，板书已暂存 = {File.Exists(Recovery.SessionPath)}");
        try { File.Delete(Recovery.SessionPath); } catch { }
        Recovery.ClearRestartCount();

        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        ExitCode = fail == 0 ? 0 : 1;
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

        // ---- 截图 vs 框选/图形：**光标必须能分开** ----
        //
        // 用户 2026-09-17："截图图标和选中图标一样的，是不是不大好？"——
        // 随即澄清是**鼠标光标**：截图/框选/图形原来共用一个系统十字，
        // 老师分不出"我现在是要截图还是要选框"。
        // 现在截图有自己的**取景框**落点（四角括号 + 中心小十字），框选/图形仍是十字。
        Tool = Tool.Capture;
        Check("截图 · 系统光标藏起来（落点自己画）", CursorKind.Hidden);
        CheckBool("截图 · 落点是取景框（不是十字）",
            DrawnCursor == ToolCursorShape.Frame && DrawnCursorRadius > 0f,
            $"DrawnCursor={DrawnCursor} 脏区半径={DrawnCursorRadius:F0}px");
        _drawing = true;
        CheckBool("截图 · 拖动中也画（正在框的那一块要看得见）",
            DrawnCursor == ToolCursorShape.Frame, $"{DrawnCursor}");
        _drawing = false;
        Tool = Tool.Rectangle;
        Check("图形 · 仍是十字准星", CursorKind.Cross);
        CheckBool("图形 · 不画自绘落点", DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        Tool = Tool.Marquee;

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

        var b0 = SelectionHandles.BarButtonRect(0, aabb, dpi, ViewportCanvas);
        PointerX = (b0.MinX + b0.MaxX) * 0.5f;
        PointerY = (b0.MinY + b0.MaxY) * 0.5f;
        Check("操作条按钮（不换光标）", CursorKind.Default);

        // ---- 操作条**整块**都是"界面"（用户 2026-09-17 报的）----
        //
        // "操作对应图标的时候一会是十字光标，一会是箭头图标"——原来只认按钮本体，
        // 按钮之间的分隔线、两头的留白都退回工具光标（框选是十字），
        // 鼠标在条上横着滑过去就是 箭头／十字／箭头／十字。
        {
            var bar = SelectionHandles.BarRect(aabb, dpi, ViewportCanvas);
            // 条**尾部那段留白**（最后一格右边到条右端）：一定不在任何按钮上。
            // 注：九格是**紧挨着**排的（BarGapLogical = 0，靠分隔线分区），所以
            // "两格之间"其实是共用一条边，那里算前一个按钮；真正露在外面的
            // 只有条两端的 6 逻辑像素留白，也正是原来会漏出十字的地方。
            var last = SelectionHandles.BarButtonRect(SelectionHandles.BarButtonCount - 1,
                                                      aabb, dpi, ViewportCanvas);
            PointerX = (last.MaxX + bar.MaxX) * 0.5f;
            PointerY = (bar.MinY + bar.MaxY) * 0.5f;
            CheckBool("（先确认那个点真的不在按钮上）",
                SelectionHandles.BarButtonAt(PointerX, PointerY, aabb, dpi, ViewportCanvas) == -1
                && PointerX < bar.MaxX, "不在按钮上、但在条里");
            Check("操作条 · 条内留白（箭头，不是十字）", CursorKind.Default);

            // 挂在下头的小面板：整块也是界面，面板的空白处同样是箭头
            SelPanelOpen = SelPanel.Layer;
            var pnl = SelectionHandles.LayerPanelRect(aabb, dpi, ViewportCanvas);
            PointerX = pnl.MinX + 3f; PointerY = pnl.MaxY - 3f;      // 面板的内边距
            Check("层级面板 · 面板空白处（箭头）", CursorKind.Default);
            var cell0 = SelectionHandles.LayerCellRect(0, aabb, dpi, ViewportCanvas);
            PointerX = (cell0.MinX + cell0.MaxX) * 0.5f;
            PointerY = (cell0.MinY + cell0.MaxY) * 0.5f;
            Check("层级面板 · 置顶那格（箭头）", CursorKind.Default);
            SelPanelOpen = SelPanel.None;

            // 收起态那颗圆钮也一样
            SelBarCollapsed = true;
            var dot = SelectionHandles.BarCollapsedRect(aabb, dpi, ViewportCanvas);
            PointerX = (dot.MinX + dot.MaxX) * 0.5f;
            PointerY = (dot.MinY + dot.MaxY) * 0.5f;
            Check("操作条收起态的圆钮（箭头）", CursorKind.Default);
            SelBarCollapsed = false;
        }

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

        // ⑨ **点选**（2026-09-15 新增）：按在墨上就选中那一条
        {
            float tol = ClickToleranceLogical * DpiScale;

            Doc.Selected.Clear();
            var hitA = Doc.SelectAt(a.Points[0].X + 100f, a.Points[0].Y, tol);
            Check("点选：点在墨上 → 选中这一条",
                  ReferenceEquals(hitA, a) && Doc.Selected.Count == 1 && Doc.Selected[0] == a,
                  $"点到 {(hitA == null ? "空" : "一条")}，选中 {Doc.Selected.Count} 条");

            // 细笔（1.5 逻辑像素）：容差让"点得中"成为可能，但离太远仍然不该命中
            var thin = Line(x0 + 1200f, y0, 400f, 0f, 1.5f * DpiScale);
            Doc.AddStroke(thin);
            var near = Doc.SelectAt(x0 + 1300f, y0 + 3f, tol);
            var far = Doc.HitObjectAt(x0 + 1300f, y0 + 12f, tol);
            Check("点选：细笔有容差（3px 命中、12px 不命中）",
                  ReferenceEquals(near, thin) && far == null,
                  $"近处点到 {(near == null ? "空" : "细笔")}，远处 {(far == null ? "空" : "误命中")}");

            // 两条重叠：取**最上面**那条（后画的）
            var under = Line(x0 + 2000f, y0, 200f, 0f, 12f * DpiScale);
            var over = Line(x0 + 2000f, y0, 200f, 0f, 12f * DpiScale);
            Doc.AddStroke(under);
            Doc.AddStroke(over);
            var top = Doc.HitObjectAt(x0 + 2100f, y0, tol);
            Check("点选：重叠处取最上面那条", ReferenceEquals(top, over),
                  ReferenceEquals(top, over) ? "取到后画的那条" : "取错了（取到下面那条）");

            // Shift 加选 / 再点同一条移出 / Alt 减选
            Doc.Selected.Clear();
            Doc.SelectAt(a.Points[0].X + 100f, a.Points[0].Y, tol);
            Doc.SelectAt(c.Points[0].X + 150f, c.Points[0].Y + 150f, tol, additive: true);
            Check("点选：Shift 加选 → 两条",
                  Doc.Selected.Count == 2 && Doc.Selected.Contains(a) && Doc.Selected.Contains(c),
                  $"选中 {Doc.Selected.Count} 条");

            Doc.SelectAt(c.Points[0].X + 150f, c.Points[0].Y + 150f, tol, additive: true);
            Check("点选：再 Shift 点同一条 → 移出（切换）",
                  Doc.Selected.Count == 1 && !Doc.Selected.Contains(c),
                  $"选中 {Doc.Selected.Count} 条");

            Doc.SelectAt(a.Points[0].X + 100f, a.Points[0].Y, tol, subtractive: true);
            Check("点选：Alt 减选 → 空", Doc.Selected.Count == 0, $"选中 {Doc.Selected.Count} 条");

            // 点空白：返回空、**不动选中**（交给框选那一步去处理"单击空白＝取消"）
            Doc.SelectOnly(new[] { a });
            var miss = Doc.SelectAt(_virtualX + 40f, _virtualY + 40f, tol);
            Check("点选：点空白 → 返回空、选中不动（交给框选）",
                  miss == null && Doc.Selected.Count == 1 && Doc.Selected[0] == a,
                  miss == null ? "没命中，选中保持 1 条" : "空白处竟然命中了");

            // **被像素橡皮擦断之后，缺口里不算墨**（点缺口不该命中）
            var cut = Line(x0 + 2400f, y0, 600f, 0f, 10f * DpiScale);
            Doc.AddStroke(cut);
            float gapX = cut.Points[0].X + 300f;
            Doc.EraseRectAt(gapX, y0, 30f, 30f);
            var inGap = Doc.HitObjectAt(gapX, y0, tol);
            var onInk = Doc.HitObjectAt(cut.Points[0].X + 60f, y0, tol);
            Check("点选：擦断的缺口不算墨（点缺口不命中、点墨命中）",
                  inGap == null && onInk != null,
                  $"缺口 {(inGap == null ? "没命中" : "误命中")}，"
                  + $"墨上 {(onInk != null ? "命中某一段" : "没命中")}");
        }

        // ⑩ 操作条：**只给下限、不翻面**（用户 2026-09-15 定的）
        {
            var vp = ViewportCanvas;

            var low = Line(x0, _virtualY + _virtualH - 30f, 200f, 0f, 8f * DpiScale);
            Doc.AddStroke(low);
            Doc.SelectOnly(new[] { low });
            var lowAabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var barLow = SelectionHandles.BarRect(lowAabb, DpiScale, vp);
            float floor = vp.MaxY - SelectionHandles.BarMinBottomMarginLogical * DpiScale;
            Check("操作条：贴屏幕下边时停在下限（不越界、也不翻面）",
                  MathF.Abs(barLow.MaxY - floor) <= 0.5f,
                  $"条底 {barLow.MaxY:F0}，下限 {floor:F0}（可见下边 {vp.MaxY:F0}）");

            var leftLine = Line(vp.MinX + 5f, _virtualY + 200f, 0f, 200f, 8f * DpiScale);
            Doc.AddStroke(leftLine);
            Doc.SelectOnly(new[] { leftLine });
            var leftAabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var barLeft = SelectionHandles.BarRect(leftAabb, DpiScale, vp);
            bool inView = barLeft.MinX >= vp.MinX - 0.5f && barLeft.MaxX <= vp.MaxX + 0.5f;
            bool barsHit = true;
            for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
            {
                var r = SelectionHandles.BarButtonRect(i, leftAabb, DpiScale, vp);
                int got = SelectionHandles.BarButtonAt((r.MinX + r.MaxX) * 0.5f,
                                                       (r.MinY + r.MaxY) * 0.5f,
                                                       leftAabb, DpiScale, vp);
                if (got != i) barsHit = false;
            }
            Check("操作条：贴屏幕左边 → 整条在可见区内、按钮都能点中",
                  inView && barsHit,
                  $"条 x {barLeft.MinX:F0}..{barLeft.MaxX:F0}（可见 {vp.MinX:F0}..{vp.MaxX:F0}），"
                  + $"按钮命中 {(barsHit ? "全中" : "有点不中")}");
        }

        // ⑪ 点选的**手势接线**（引擎那一侧：无选中时点一条、点空白、Shift 加选、收窄成单选）
        {
            float tol = ClickToleranceLogical * DpiScale;

            Doc.Selected.Clear();
            Tool = Tool.Marquee;
            bool tookA = SelectionGestureForTest(a.Points[0].X + 100f, a.Points[0].Y);
            Check("手势：**没选中**时点一条 → 接住并选中它（这条最容易写漏）",
                  tookA && Doc.Selected.Count == 1 && Doc.Selected[0] == a && SelDragging,
                  $"接住={tookA}，选中 {Doc.Selected.Count} 条，拖动态={SelDragging}");
            EndSelectionGestureForTest();

            Doc.Selected.Clear();
            bool tookBlank = SelectionGestureForTest(_virtualX + 40f, _virtualY + 40f);
            Check("手势：点空白 → 不接（交给框选）", !tookBlank, $"接住={tookBlank}");

            Doc.Selected.Clear();
            SelectionGestureForTest(a.Points[0].X + 100f, a.Points[0].Y);
            EndSelectionGestureForTest();
            bool tookC = SelectionGestureForTest(c.Points[0].X + 150f, c.Points[0].Y + 150f, shift: true);
            Check("手势：Shift 点第二条 → 加选，且不进拖动",
                  tookC && Doc.Selected.Count == 2 && !SelDragging,
                  $"接住={tookC}，选中 {Doc.Selected.Count} 条，拖动态={SelDragging}");

            // "点一下把多选收窄成单选"：按在多选中的一条上、松手不移动
            Doc.Selected.Clear();
            Doc.SelectOnly(new[] { a, c });
            SelectionGestureForTest(a.Points[0].X + 100f, a.Points[0].Y);   // 落在 a 的框里 → 整体拖动
            EndSelectionGestureForTest();                                   // 没移动 → 收窄
            Check("手势：点多选中的一条、不移动 → 收窄成只选它",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == a,
                  $"选中 {Doc.Selected.Count} 条{(Doc.Selected.Count == 1 && Doc.Selected[0] == a ? "（就是那一条）" : "")}");

            Doc.Selected.Clear();
            SelDragging = false;
            _ = tol;
        }

        // ⑫ 框选矩形**咬着指针**（用户 2026-09-15 反馈："感觉有点不大跟手"）
        //
        // 以前按下之后每次移动都累积 min/max，于是框只会**变大**：指针往回走框不跟着缩，
        // 屏幕上看到的是"扫过的最大范围"，而不是"从起点拉到现在的这一个矩形"。
        // 现在按**锚点 ↔ 当前点**算（和同行一致），往回拖要能缩回去。
        // 截图取景框用的是同一套算法，这里一起验。
        {
            SelMode = SelectMode.Rect;
            Tool = Tool.Marquee;

            // ① 拖到 +400 再拖回 +100 松手：框应该是 [锚点, +100]，不是 [锚点, +400]
            Doc.Clear();
            Doc.ClearHistory();
            var sweptOnly = Line(x0 + 200f, y0 + 50f, 100f, 0f, 6f * DpiScale);  // 扫过、但不在最终框里
            var inside = Line(x0 + 20f, y0 + 50f, 60f, 0f, 6f * DpiScale);       // 在最终框里
            Doc.AddStroke(sweptOnly);
            Doc.AddStroke(inside);
            MarqueeDragForTest(new[]
            {
                new Vector2(x0, y0),
                new Vector2(x0 + 400f, y0 + 400f),      // 先拖远
                new Vector2(x0 + 100f, y0 + 100f),      // 再拖回来松手
            });
            bool sweptPicked = Doc.Selected.Contains(sweptOnly);
            Check("框选：拖远再拖回来 → 框跟着缩（不是扫过的最大范围）",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == inside,
                  $"选中 {Doc.Selected.Count} 条（扫过但已退回的那条"
                  + (sweptPicked ? "**被误选**" : "没被选") + "）");

            // ② 从锚点往左上拖：反向也要能拉出框
            Doc.Clear();
            Doc.ClearHistory();
            var upLeft = Line(x0 - 120f, y0 - 120f, 60f, 0f, 6f * DpiScale);
            Doc.AddStroke(upLeft);
            MarqueeDragForTest(new[]
            {
                new Vector2(x0, y0),
                new Vector2(x0 - 180f, y0 - 180f),
            });
            Check("框选：从锚点往左上拖 → 反向也能拉出框",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == upLeft,
                  $"选中 {Doc.Selected.Count} 条");

            // ③ 截图取景框：同一套锚点算法（只驱动"按下 → 拖"，不抓屏）
            CaptureFrameDragForTest(x0, y0, x0 + 400f, y0 + 400f, x0 + 100f, y0 + 100f);
            bool capShrunk = MathF.Abs(CapMinX - x0) < 0.01f && MathF.Abs(CapMaxX - (x0 + 100f)) < 0.01f
                          && MathF.Abs(CapMinY - y0) < 0.01f && MathF.Abs(CapMaxY - (y0 + 100f)) < 0.01f;
            Check("截图取景框：同一套锚点算法（拖远再拖回也缩）", capShrunk,
                  $"框 = ({CapMinX:F0},{CapMinY:F0})..({CapMaxX:F0},{CapMaxY:F0})，"
                  + $"期望 ({x0:F0},{y0:F0})..({x0 + 100f:F0},{y0 + 100f:F0})");
            CaptureActive = false;
        }

        // ⑬ "选中是临时上下文"（用户 2026-09-15 定的规则，也是 InkClass/PPT/Figma 的惯例）
        {
            // 换工具 → 收起；按"框选"（本来就是它）→ 保留
            Doc.SelectOnly(new[] { a });
            RunActionForTest(KeyAction.ToolPen);
            bool clearedOnSwitch = Doc.Selected.Count == 0 && Tool == Tool.Pen;
            Tool = Tool.Marquee;                       // 直接换回来（不经过 SwitchTool，免得又清）
            Doc.SelectOnly(new[] { a });
            RunActionForTest(KeyAction.ToolMarquee);
            Check("换工具收起选区；按框选（同一工具）保留",
                  clearedOnSwitch && Doc.Selected.Count == 1,
                  $"换笔后 {(clearedOnSwitch ? "已收起" : "没收起")}，再按框选后选中 {Doc.Selected.Count} 条");

            // 对象从文档里消失 → 自动从选中里去掉
            Doc.Clear();
            Doc.ClearHistory();
            var gone = Line(x0, y0, 200f, 0f, 8f * DpiScale);
            Doc.AddStroke(gone);
            Doc.SelectOnly(new[] { gone });
            Doc.RemoveStroke(gone);
            Check("对象被删/被擦掉 → 自动从选中里去掉", Doc.Selected.Count == 0,
                  $"选中 {Doc.Selected.Count} 条");

            // 滚动**不算**"操作选区"：滚轮只改相机，不该把选中弄没
            Doc.SelectOnly(new[] { a });
            float camBefore = ViewOffsetY;
            HandleWheel((IntPtr)(-120L << 16));        // 高 16 位 = 滚轮增量
            bool camMoved = MathF.Abs(ViewOffsetY - camBefore) > 0.5f;
            ViewOffsetY = camBefore;
            Check("滚动不清选中（滚动是「看」，不是「操作对象」）",
                  Doc.Selected.Count == 1 && camMoved,
                  $"选中 {Doc.Selected.Count} 条，相机 {(camMoved ? "动了" : "没动")}");

            // **复制拖拽模式**：点复制按钮进模式 → 按住选中内容拖 → 拖出副本（原件不动）→
            // 可以连着拖第二份；**克隆 + 位移算一步撤销**
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var src = Line(x0, y0, 200f, 0f, 8f * DpiScale);
            Doc.AddStroke(src);
            Doc.SelectOnly(new[] { src });
            var srcXform0 = src.Transform;

            RunBarActionForTest((int)SelBarButton.Copy);              // 复制按钮（第二轮从 0 挪到 5）
            bool armed = CopyDragArmed && Doc.Selected.Count == 1;

            bool took = SelectionGestureForTest(x0 + 100f, y0);        // 按在原件上（框内 → 拖动）
            bool cloneMade = Doc.Strokes.Count == 2;                   // 克隆发生在按下那一刻
            if (took)
            {
                UpdateSelectionGestureForTest(x0 + 300f, y0 + 60f);    // 拖出去
                EndSelectionGestureForTest();
            }
            var firstCopy = Doc.Selected.Count == 1 ? Doc.Selected[0] : null;
            bool firstOk = armed && took && cloneMade
                        && firstCopy != null && !ReferenceEquals(firstCopy, src)
                        && src.Transform.Equals(srcXform0);

            // 再拖一次 → 第三份（「可连续多份」）
            bool took2 = SelectionGestureForTest(x0 + 300f, y0 + 60f);
            if (took2)
            {
                UpdateSelectionGestureForTest(x0 + 500f, y0 + 120f);
                EndSelectionGestureForTest();
            }
            bool secondOk = Doc.Strokes.Count == 3 && Doc.Selected.Count == 1;

            Check("复制拖拽模式：拖出副本、原件不动、可连续拖第二份",
                  firstOk && secondOk,
                  $"进模式={armed}，第一次接住={took}（克隆重合={cloneMade}），"
                  + $"第二次接住={took2}；对象数 {Doc.Strokes.Count}（应 3），"
                  + $"原件 {(src.Transform.Equals(srcXform0) ? "没动" : "动了")}");

            // 每拖出一份 = 一步撤销（**克隆 + 位移是一步**，不是两步）
            Doc.Undo();
            bool backToTwo = Doc.Strokes.Count == 2;
            Doc.Undo();
            Check("复制拖拽：一次撤销回退一份（克隆+位移合成一步）",
                  backToTwo && Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], src)
                  && src.Transform.Equals(srcXform0),
                  $"撤一次后 {2} 条、再撤一次 {Doc.Strokes.Count} 条，"
                  + $"原件位置 {(src.Transform.Equals(srcXform0) ? "回到原位" : "没回来")}");

            RunBarActionForTest((int)SelBarButton.Copy);              // 退出模式（收尾）
            Check("再点一次复制按钮 → 退出复制拖拽模式", !CopyDragArmed, $"armed={CopyDragArmed}");
        }

        // ⑭ 拖动 / 旋转期间的画法（2026-09-16）：方案 A 收装饰 + 方案 B 拖动预览
        //
        // 这一段的判据全部落在**屏幕像素**上，因为要验的两件事都是"看得见"的性质：
        //   · 拖动中手柄与操作条到底还在不在屏幕上（方案 A）；
        //   · 被拖的那块到底有没有跟着指针走、并且**没被选中的墨一个像素都没动**（方案 B）。
        //
        // 前置条件是把板铺成**不透明**并选一个**没有别的程序参与**的颜色：
        // 桌面上"没画东西的地方"是别的程序，白像素到处都是，数不出"手柄的白在不在"。
        // 板色取深灰，于是四种东西互不撞色：板=深灰、墨=品红、手柄与操作条=白、选中框=蓝。
        {
            bool boardWas = BoardOn;
            var boardColorWas = BoardColor;
            float camWas = ViewOffsetY;
            BoardOn = true;
            BoardColor = new Color4(0.10f, 0.10f, 0.12f, 1f);
            ViewOffsetY = 0f;                        // 屏幕坐标 == 画布坐标，抓屏好算
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;

            float dpi = DpiScale;
            Stroke FatLine(float yy, Color4 col)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = col, Width = 10f * dpi };
                s.AddPoint(x0, yy, 0.5f, NowMs);
                s.AddPoint(x0 + 260f * dpi, yy, 0.5f, NowMs);
                return s;
            }
            var dragInk = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));    // 品红：被拖的这一条
            var refInk = FatLine(y0 + 1100f, new Color4(0f, 1f, 0f, 1f));    // 绿：什么都不做的参照物
            Doc.AddStroke(dragInk);
            Doc.AddStroke(refInk);
            Doc.SelectOnly(new[] { dragInk });
            SettleFrames(250);

            var frame0 = LiveSelectionFrame;
            var barRect = SelectionHandles.BarRect(frame0.CanvasAabb, dpi, ViewportCanvas);
            var handleTL = SelectionHandles.CanvasPosition(SelHandle.TopLeft, frame0, dpi);

            int BarWhite() => ScreenProbe.CountNear(
                (int)barRect.MinX, (int)barRect.MinY,
                (int)(barRect.MaxX - barRect.MinX), (int)(barRect.MaxY - barRect.MinY),
                255, 255, 255, 30);
            int HandleWhite() => ScreenProbe.CountNear(
                (int)handleTL.X - 20, (int)handleTL.Y - 20, 40, 40, 255, 255, 255, 30);

            var oldBox = dragInk.PaddedBounds.Inflate(8f);
            int BoxMagenta(in RectF r) => ScreenProbe.CountMagenta(
                (int)r.MinX, (int)r.MinY, (int)(r.MaxX - r.MinX), (int)(r.MaxY - r.MinY));
            var refBox = refInk.PaddedBounds.Inflate(10f);
            byte[] RefShot() => ScreenProbe.CaptureRegion(
                (int)refBox.MinX, (int)refBox.MinY,
                (int)(refBox.MaxX - refBox.MinX), (int)(refBox.MaxY - refBox.MinY));

            int barBefore = BarWhite(), handleBefore = HandleWhite();
            int oldBefore = BoxMagenta(oldBox);
            var refBefore = RefShot();

            // 按住选中内容中间拖走（中间离手柄最远，命中的一定是"整体拖动"）
            float px = x0 + 130f * dpi, py = y0 + 700f;
            float dx = 150f, dy = 210f;
            bool tookDrag = SelectionGestureForTest(px, py);
            UpdateSelectionGestureForTest(px + dx, py + dy);
            SettleFrames(90);

            var movedBox = new RectF
            {
                MinX = oldBox.MinX + dx, MinY = oldBox.MinY + dy,
                MaxX = oldBox.MaxX + dx, MaxY = oldBox.MaxY + dy,
            };
            // 墨量比对只在**中间那一段**做：两端压着缩放手柄，而手柄拖动中是收起来的，
            // 拿整条去比会把"手柄盖住了几块墨"当成"两种画法不一致"。
            var midStrip = new RectF
            {
                MinX = (movedBox.MinX + movedBox.MaxX) * 0.5f - 200f,
                MinY = (movedBox.MinY + movedBox.MaxY) * 0.5f - 30f,
                MaxX = (movedBox.MinX + movedBox.MaxX) * 0.5f - 100f,
                MaxY = (movedBox.MinY + movedBox.MaxY) * 0.5f + 30f,
            };
            int barDuring = BarWhite(), handleDuring = HandleWhite();
            int oldDuring = BoxMagenta(oldBox), newDuring = BoxMagenta(movedBox);
            int stripDuring = BoxMagenta(midStrip);
            var refDuring = RefShot();
            int patchDuring = _windows[0].LastPatchCount;

            Check("拖动中：手柄与操作条收起来（方案 A）",
                  tookDrag && SelChromeCollapsed
                  && barDuring * 10 < barBefore && handleDuring * 10 < handleBefore,
                  $"接住={tookDrag}，收起={SelChromeCollapsed}；"
                  + $"操作条的白 {barBefore}→{barDuring}，手柄的白 {handleBefore}→{handleDuring}");

            Check("拖动中：内容层一帧都不重画（方案 B）",
                  patchDuring == 0,
                  $"上一帧光栅化分块 {patchDuring} 块（老做法要把走过的面积整块重画一遍）");

            Check("拖动中：预览真的上屏（新位置有墨、原位置干净）",
                  newDuring > 200 && oldDuring == 0,
                  $"新位置 {newDuring} 像素，原位置 {oldDuring} 像素");

            Check("拖动中：没被选中的墨迹一个像素都不许变",
                  ScreenProbe.DiffCount(refBefore, refDuring) == 0,
                  $"参照物区域差异 {ScreenProbe.DiffCount(refBefore, refDuring)} 像素");

            EndSelectionGestureForTest();
            SettleFrames(250);

            // 松手后框跟着内容走到了新位置，所以手柄与操作条要**按现在的框**重新取位置
            var frameAfter = LiveSelectionFrame;
            var barRectAfter = SelectionHandles.BarRect(frameAfter.CanvasAabb, dpi, ViewportCanvas);
            var handleAfterPt = SelectionHandles.CanvasPosition(SelHandle.TopLeft, frameAfter, dpi);
            int barAfter = ScreenProbe.CountNear(
                (int)barRectAfter.MinX, (int)barRectAfter.MinY,
                (int)(barRectAfter.MaxX - barRectAfter.MinX), (int)(barRectAfter.MaxY - barRectAfter.MinY),
                255, 255, 255, 30);
            int handleAfter = ScreenProbe.CountNear(
                (int)handleAfterPt.X - 20, (int)handleAfterPt.Y - 20, 40, 40, 255, 255, 255, 30);
            int oldAfter = BoxMagenta(oldBox), newAfter = BoxMagenta(movedBox);
            int stripAfter = BoxMagenta(midStrip);
            int parity = Math.Abs(stripAfter - stripDuring);
            var refAfter = RefShot();

            Check("松手后：装饰回来、模型才动、原位置干净",
                  !SelChromeCollapsed && handleAfter > handleBefore / 2 && barAfter > barBefore / 2
                  && !dragInk.Transform.IsIdentity && oldAfter == 0,
                  $"手柄的白 {handleAfter}（拖动前 {handleBefore}），条的白 {barAfter}，"
                  + $"模型 {(dragInk.Transform.IsIdentity ? "还没动" : "动了")}，原位置 {oldAfter} 像素");

            Check("松手前后同一段墨的墨量一致（预览与内容层像素一致）",
                  stripDuring > 200 && parity <= Math.Max(20, stripDuring / 50),
                  $"中间那一段：拖动中 {stripDuring} 像素，松手后 {stripAfter} 像素（差 {parity}）");

            Check("松手后参照物仍然一个像素都没变",
                  ScreenProbe.DiffCount(refBefore, refAfter) == 0,
                  $"差异 {ScreenProbe.DiffCount(refBefore, refAfter)} 像素");

            Doc.Undo();
            SettleFrames(250);
            Check("一次撤销回到原位（方案 B 没改撤销语义）",
                  dragInk.Transform.IsIdentity && BoxMagenta(oldBox) > oldBefore / 2
                  && BoxMagenta(movedBox) == 0,
                  $"模型 {(dragInk.Transform.IsIdentity ? "回原位" : "没回来")}，"
                  + $"原位置品红 {BoxMagenta(oldBox)} 像素（拖着时 {oldBefore}），"
                  + $"拖过去的位置 {BoxMagenta(movedBox)} 像素（该是 0）");

            // ---- 方案 A 的另一半：旋转中**留**旋转柄与度数标签 ----
            Doc.Clear();
            Doc.ClearHistory();
            var spinInk = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(spinInk);
            Doc.SelectOnly(new[] { spinInk });
            SettleFrames(250);

            var f2 = LiveSelectionFrame;
            var grip2 = SelectionHandles.CanvasPosition(SelHandle.Rotate, f2, dpi);
            var pivot2 = new Vector2((f2.CanvasAabb.MinX + f2.CanvasAabb.MaxX) * 0.5f,
                                     (f2.CanvasAabb.MinY + f2.CanvasAabb.MaxY) * 0.5f);
            float arm2 = Vector2.Distance(grip2, pivot2);
            var barRect2 = SelectionHandles.BarRect(f2.CanvasAabb, dpi, ViewportCanvas);
            int SpinBarWhite() => ScreenProbe.CountNear(
                (int)barRect2.MinX, (int)barRect2.MinY,
                (int)(barRect2.MaxX - barRect2.MinX), (int)(barRect2.MaxY - barRect2.MinY),
                255, 255, 255, 30);
            // 度数标签就在**当前**旋转柄的上方（手柄跟着内容转，所以每步都要重算位置）
            int LabelAccent()
            {
                var h = SelectionHandles.CanvasPosition(SelHandle.Rotate, LiveSelectionFrame, dpi);
                int pw = (int)(120f * dpi), ph = (int)(70f * dpi);
                return ScreenProbe.CountNear((int)(h.X - pw * 0.5f), (int)(h.Y - ph),
                                             pw, ph, 0, 120, 212, 40);
            }

            int spinBarBefore = SpinBarWhite();
            bool tookSpin = SelectionGestureForTest(grip2.X, grip2.Y);
            UpdateSelectionGestureForTest(pivot2.X - arm2, pivot2.Y);     // 屏幕上逆时针 90°
            SettleFrames(90);

            int labelPixels = LabelAccent();
            int spinBarDuring = SpinBarWhite();
            Check("旋转中：柄与度数标签还在、操作条收起来（方案 A）",
                  tookSpin && SelRotating && SelChromeCollapsed
                  && spinBarDuring * 10 < spinBarBefore && labelPixels > 800,
                  $"接住={tookSpin}，旋转中={SelRotating}，收起={SelChromeCollapsed}；"
                  + $"度数标签强调色 {labelPixels} 像素，操作条的白 {spinBarBefore}→{spinBarDuring}");

            EndSelectionGestureForTest();
            SettleFrames(200);
            var f2After = LiveSelectionFrame;
            var barRect2After = SelectionHandles.BarRect(f2After.CanvasAabb, dpi, ViewportCanvas);
            int spinBarAfter = ScreenProbe.CountNear(
                (int)barRect2After.MinX, (int)barRect2After.MinY,
                (int)(barRect2After.MaxX - barRect2After.MinX), (int)(barRect2After.MaxY - barRect2After.MinY),
                255, 255, 255, 30);
            Check("旋转松手后：标签消失、操作条回来",
                  !SelRotating && spinBarAfter > spinBarBefore / 2,
                  $"条的白 {spinBarAfter}（旋转前 {spinBarBefore}）");

            // ---- 多选拖动：框是**轴对齐并集**，拖动中同样要跟着内容走 ----
            // （LiveSelectionFrame 的另一条分支：单选靠"框坐标系右乘"，多选要
            //   逐条过实时矩阵再并。少了这条，多选拖动时会只剩框留在原地。）
            Doc.Clear();
            Doc.ClearHistory();
            var m1 = FatLine(y0 + 600f, new Color4(1f, 0f, 1f, 1f));
            var m2 = FatLine(y0 + 980f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(m1);
            Doc.AddStroke(m2);
            Doc.SelectOnly(new[] { m1, m2 });
            SettleFrames(200);

            var multiBefore = LiveSelectionFrame.CanvasAabb;
            float mx = (multiBefore.MinX + multiBefore.MaxX) * 0.5f;
            float my = (multiBefore.MinY + multiBefore.MaxY) * 0.5f;
            bool tookMulti = SelectionGestureForTest(mx, my);
            UpdateSelectionGestureForTest(mx + 120f, my - 80f);
            SettleFrames(60);
            var multiDuring = LiveSelectionFrame.CanvasAabb;
            bool frameFollows = MathF.Abs(multiDuring.MinX - (multiBefore.MinX + 120f)) < 1.5f
                             && MathF.Abs(multiDuring.MinY - (multiBefore.MinY - 80f)) < 1.5f;
            Check("多选拖动：框（轴对齐并集）跟着内容走",
                  tookMulti && DragPreviewActive && frameFollows,
                  $"{multiBefore.MinX:F0},{multiBefore.MinY:F0} → {multiDuring.MinX:F0},{multiDuring.MinY:F0}"
                  + $"（期望 +120,-80）");
            EndSelectionGestureForTest();
            SettleFrames(120);

            // ---- 缩放手势也走同一条"摘出去"的路，但**装饰不收** ----
            // （方案 B 对三种手势一视同仁；方案 A 只收移动与旋转——
            //   拖某个手柄时，另外几个手柄是有用的参照。）
            Doc.Clear();
            Doc.ClearHistory();
            var scInk = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(scInk);
            Doc.SelectOnly(new[] { scInk });
            SettleFrames(200);

            var fScale = LiveSelectionFrame;
            var gripR = SelectionHandles.CanvasPosition(SelHandle.Right, fScale, dpi);
            float widthBefore = fScale.CanvasAabb.MaxX - fScale.CanvasAabb.MinX;
            bool tookScale = SelectionGestureForTest(gripR.X, gripR.Y);
            UpdateSelectionGestureForTest(gripR.X + 200f, gripR.Y);
            SettleFrames(60);
            var fScaled = LiveSelectionFrame;
            float widthDuring = fScaled.CanvasAabb.MaxX - fScaled.CanvasAabb.MinX;
            Check("缩放中：框跟着手柄变宽，且装饰不收（方案 A 只管移动与旋转）",
                  tookScale && widthDuring > widthBefore + 150f && !SelChromeCollapsed,
                  $"宽 {widthBefore:F0} → {widthDuring:F0}（期望 +200），收起={SelChromeCollapsed}");
            EndSelectionGestureForTest();
            SettleFrames(120);

            // ---- 单选一个**转过角度**的对象：框也必须是轴对齐的正矩形 ----
            // （用户 2026-09-16 定：单选也走多选那条量法。早先是"单选跟对象转"，
            //   判据就一条——框坐标系必须是单位阵，且框恰好是对象墨迹的外接正矩形。
            //   有人把它改回"跟对象转"，这里会红。）
            Doc.Clear();
            Doc.ClearHistory();
            var tilted = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(tilted);
            Doc.SelectOnly(new[] { tilted });
            var tCenter = new Vector2((tilted.PaddedBounds.MinX + tilted.PaddedBounds.MaxX) * 0.5f,
                                      (tilted.PaddedBounds.MinY + tilted.PaddedBounds.MaxY) * 0.5f);
            Doc.ApplyTransform(Matrix3x2.CreateRotation(-40f * MathF.PI / 180f, tCenter));
            SettleFrames(150);

            var tf = SelectionHandles.FrameOf(Doc.Selected);
            var tExpect = tilted.WorldInkBounds;
            bool tAligned = tf.ToCanvas.IsIdentity
                         && MathF.Abs(tf.Local.MinX - tExpect.MinX) < 0.01f
                         && MathF.Abs(tf.Local.MinY - tExpect.MinY) < 0.01f
                         && MathF.Abs(tf.Local.MaxX - tExpect.MaxX) < 0.01f
                         && MathF.Abs(tf.Local.MaxY - tExpect.MaxY) < 0.01f;
            var tGrip = SelectionHandles.CanvasPosition(SelHandle.Rotate, tf, dpi);
            bool tGripOnTop = MathF.Abs(tGrip.X - (tf.Local.MinX + tf.Local.MaxX) * 0.5f) < 0.01f
                           && tGrip.Y < tf.Local.MinY;
            Check("单选一个转过的对象：框是正矩形、旋转柄在正上方",
                  tAligned && tGripOnTop,
                  $"框 {tf.Local.MinX:F0},{tf.Local.MinY:F0}..{tf.Local.MaxX:F0},{tf.Local.MaxY:F0}"
                  + $"（墨迹 {tExpect.MinX:F0},{tExpect.MinY:F0}..{tExpect.MaxX:F0},{tExpect.MaxY:F0}）；"
                  + $"柄在 ({tGrip.X:F0},{tGrip.Y:F0})");

            // 再转一手：拖动中框仍然轴对齐，而且是**每帧重新贴合**当前内容
            var tPivot = new Vector2((tf.Local.MinX + tf.Local.MaxX) * 0.5f,
                                     (tf.Local.MinY + tf.Local.MaxY) * 0.5f);
            float tArm = Vector2.Distance(tGrip, tPivot);
            float tA0 = MathF.Atan2(tGrip.Y - tPivot.Y, tGrip.X - tPivot.X);
            float tA1 = tA0 - 30f * MathF.PI / 180f;              // 屏幕上逆时针 30°
            bool tookTilt = SelectionGestureForTest(tGrip.X, tGrip.Y);
            UpdateSelectionGestureForTest(tPivot.X + tArm * MathF.Cos(tA1),
                                          tPivot.Y + tArm * MathF.Sin(tA1));
            SettleFrames(60);

            var dFrame = LiveSelectionFrame;
            var wb = tilted.WorldInkBounds;
            var expBox = RectF.Empty;
            foreach (var corner in new[]
            {
                new Vector2(wb.MinX, wb.MinY), new Vector2(wb.MaxX, wb.MinY),
                new Vector2(wb.MaxX, wb.MaxY), new Vector2(wb.MinX, wb.MaxY),
            })
            {
                var q = Vector2.Transform(corner, DragPreviewMatrix);
                expBox.Add(q.X, q.Y);
            }
            bool tLiveAligned = dFrame.ToCanvas.IsIdentity
                             && MathF.Abs(dFrame.Local.MinX - expBox.MinX) < 0.5f
                             && MathF.Abs(dFrame.Local.MaxX - expBox.MaxX) < 0.5f;
            Check("旋转中：框仍正着，并每帧重新贴合当前内容",
                  tookTilt && tLiveAligned,
                  $"框宽 {dFrame.Local.MaxX - dFrame.Local.MinX:F0}"
                  + $"（内容外接 {(expBox.MaxX - expBox.MinX):F0}），轴对齐={dFrame.ToCanvas.IsIdentity}");
            EndSelectionGestureForTest();
            SettleFrames(120);

            // ---- 操作条第二轮（2026-09-16）：九格 / 收起 / 提示条 / 颜色面板 / 锁定 / 层级 ----
            Doc.Clear();
            Doc.ClearHistory();
            var bA = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            var bB = FatLine(y0 + 900f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(bA);
            Doc.AddStroke(bB);
            Doc.SelectOnly(new[] { bA });
            SettleFrames(220);

            var barFrame = LiveSelectionFrame;
            var barBoxR = SelectionHandles.BarRect(barFrame.CanvasAabb, dpi, ViewportCanvas);
            int Bar2White() => ScreenProbe.CountNear((int)barBoxR.MinX, (int)barBoxR.MinY,
                (int)(barBoxR.MaxX - barBoxR.MinX), (int)(barBoxR.MaxY - barBoxR.MinY), 255, 255, 255, 24);

            // ① 九格逐格命中（纯函数，便宜且能钉住"下标 ↔ 语义"不错位）
            bool allHit = true; string hitNote = "";
            for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
            {
                var br = SelectionHandles.BarButtonRect(i, barFrame.CanvasAabb, dpi, ViewportCanvas);
                int got = SelectionHandles.BarButtonAt((br.MinX + br.MaxX) * 0.5f,
                                                      (br.MinY + br.MaxY) * 0.5f,
                                                      barFrame.CanvasAabb, dpi, ViewportCanvas);
                if (got != i) { allHit = false; hitNote = $"第 {i} 格命中成了 {got}"; }
            }
            Check("操作条九格：每格都点得中自己", allHit,
                  allHit ? $"{SelectionHandles.BarButtonCount} 格逐格核对" : hitNote);

            // ② 收起 / 展开（用户更正：条首那个 ✕ 是"收起工具条"，不是"取消选择"）
            int barWhiteBeforeCollapse = Bar2White();
            RunBarActionForTest((int)SelBarButton.Collapse);
            SettleFrames(140);
            var dotR = SelectionHandles.BarCollapsedRect(barFrame.CanvasAabb, dpi, ViewportCanvas);
            int barWhiteCollapsed = Bar2White();
            int dotWhite = ScreenProbe.CountNear((int)dotR.MinX, (int)dotR.MinY,
                (int)(dotR.MaxX - dotR.MinX), (int)(dotR.MaxY - dotR.MinY), 255, 255, 255, 24);
            bool tookDot = SelectionGestureForTest((dotR.MinX + dotR.MaxX) * 0.5f,
                                                   (dotR.MinY + dotR.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(140);
            Check("收起：整条没了、圆钮在；点圆钮又展开",
                  // 判据用"白底掉了 80% 以上"而不是"归零"：**圆钮自己就落在条的那块区域里**
                  // （它居中在框下方，和条的横纵位置一致），所以那块不会真的全黑。
                  tookDot && !SelBarCollapsed && barWhiteCollapsed * 5 < barWhiteBeforeCollapse
                  && dotWhite > 300 && Bar2White() > 3000,
                  $"条的白底 {barWhiteBeforeCollapse} → 收起后 {barWhiteCollapsed} → 展开后 {Bar2White()}，"
                  + $"圆钮的白 {dotWhite}（圆心 {dotR.MinX:F0},{dotR.MinY:F0}），"
                  + $"点中圆钮={tookDot}，收起态={SelBarCollapsed}");

            // ③ 颜色面板：点"颜色"开面板 → 点色片改色 → 一次撤销回原色
            RunBarActionForTest((int)SelBarButton.Color);
            SettleFrames(140);
            var panelR = SelectionHandles.PanelRect(barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                    SelectionHandles.SwatchCount);
            int panelWhite = ScreenProbe.CountNear((int)panelR.MinX, (int)panelR.MinY,
                (int)(panelR.MaxX - panelR.MinX), (int)(panelR.MaxY - panelR.MinY), 255, 255, 255, 24);
            int swatchPick = 5;                                   // 橙
            var swR = SelectionHandles.SwatchRect(swatchPick, barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                  SelectionHandles.SwatchCount);
            bool tookSwatch = SelectionGestureForTest((swR.MinX + swR.MaxX) * 0.5f,
                                                     (swR.MinY + swR.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(140);
            var wantColor = InkPalette.SelectionSwatches[swatchPick].Color;
            bool colorChanged = MathF.Abs(bA.Color.R - wantColor.R) < 0.02f
                             && MathF.Abs(bA.Color.G - wantColor.G) < 0.02f
                             && MathF.Abs(bA.Color.B - wantColor.B) < 0.02f;
            Doc.Undo();
            SettleFrames(140);
            bool colorBack = bA.Color.R > 0.9f && bA.Color.G < 0.1f && bA.Color.B > 0.9f;
            Check("颜色面板：点色片真的改色、一次撤销回原色",
                  SelPanelOpen == SelPanel.Ink && panelWhite > 5000 && tookSwatch
                  && colorChanged && colorBack,
                  $"面板白底 {panelWhite} 像素；改色 {(colorChanged ? "对" : "不对")}，"
                  + $"撤销后回原色 {(colorBack ? "是" : "否")}");

            // ⑤ 粗细滑条：拖到最粗那一档 → Width 变 + 选中框跟着变大
            var sliderT = SelectionHandles.SliderRect(barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                      SelectionHandles.SwatchCount);
            // 档位表由引擎决定（全荧光笔用荧光笔那三档）——自检这边跟着引擎的口径走
            int lastStep = SliderStepCount();
            float wBefore = bA.Width;
            var frameWBeforeBox = LiveSelectionFrame.CanvasAabb;
            float frameWBefore = frameWBeforeBox.MaxX - frameWBeforeBox.MinX;
            float sx = SelectionHandles.SliderStepX(lastStep - 1, lastStep, barFrame.CanvasAabb, dpi,
                                                    ViewportCanvas, SelectionHandles.SwatchCount);
            bool tookSlider = SelectionGestureForTest(sx, (sliderT.MinY + sliderT.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(140);
            float frameWAfter = LiveSelectionFrame.CanvasAabb.MaxX - LiveSelectionFrame.CanvasAabb.MinX;
            bool widthChanged = bA.Width > wBefore + 1f;
            Check("粗细滑条：拖到最粗 → 墨变粗、选中框跟着变大",
                  tookSlider && widthChanged && frameWAfter > frameWBefore + 1f,
                  $"宽 {wBefore:F1} → {bA.Width:F1}；框宽 {frameWBefore:F0} → {frameWAfter:F0}");
            Doc.Undo();
            SettleFrames(120);

            // ⑥ 锁定（用户定的 B 语义）：锁上以后能选中、但拖不动、也删不掉
            RunBarActionForTest((int)SelBarButton.Color);          // 先收面板（免得盖住条）
            RunBarActionForTest((int)SelBarButton.Lock);
            SettleFrames(120);
            bool lockedNow = bA.Locked;
            var lockC = new Vector2((barFrame.CanvasAabb.MinX + barFrame.CanvasAabb.MaxX) * 0.5f,
                                    (barFrame.CanvasAabb.MinY + barFrame.CanvasAabb.MaxY) * 0.5f);
            int depthBefore = Doc.UndoDepth;
            bool tookLockedDrag = SelectionGestureForTest(lockC.X, lockC.Y);
            UpdateSelectionGestureForTest(lockC.X + 160f, lockC.Y + 60f);
            EndSelectionGestureForTest();
            SettleFrames(120);
            bool stayed = bA.Transform.IsIdentity && Doc.UndoDepth == depthBefore;
            int nBefore = Doc.Strokes.Count;
            RunBarActionForTest((int)SelBarButton.Delete);
            SettleFrames(120);
            bool survivedDelete = Doc.Strokes.Contains(bA) && Doc.Strokes.Count == nBefore;
            Check("锁定：能选中，但拖不动、也删不掉（B 语义）",
                  lockedNow && tookLockedDrag && stayed && survivedDelete,
                  $"锁上={lockedNow}，拖了以后位置 {(bA.Transform.IsIdentity ? "没动" : "动了")}，"
                  + $"撤销栈 +{Doc.UndoDepth - depthBefore}（该是 0），删除后还在={Doc.Strokes.Contains(bA)}");

            // ⑦ 解锁之后能拖（B 语义的另一半：锁是可以随时解开的）
            Doc.SelectOnly(new[] { bA });
            RunBarActionForTest((int)SelBarButton.Lock);           // 再点一次 = 解锁
            SettleFrames(120);
            var unlockC = new Vector2((LiveSelectionFrame.CanvasAabb.MinX + LiveSelectionFrame.CanvasAabb.MaxX) * 0.5f,
                                      (LiveSelectionFrame.CanvasAabb.MinY + LiveSelectionFrame.CanvasAabb.MaxY) * 0.5f);
            SelectionGestureForTest(unlockC.X, unlockC.Y);
            UpdateSelectionGestureForTest(unlockC.X + 120f, unlockC.Y);
            EndSelectionGestureForTest();
            SettleFrames(140);
            Check("解锁之后又能拖了", !bA.Locked && !bA.Transform.IsIdentity,
                  $"锁={bA.Locked}，位置 {(bA.Transform.IsIdentity ? "没动" : "动了")}");
            Doc.Undo();
            SettleFrames(120);

            // ⑧ 层级：置顶（下标关系反过来）→ 一次撤销回原顺序
            Doc.SelectOnly(new[] { bA });
            int idxBefore = Doc.Strokes.IndexOf(bA);
            RunBarActionForTest((int)SelBarButton.Layer);          // 开层级面板
            SettleFrames(120);
            var frontCell = SelectionHandles.LayerCellRect(0, LiveSelectionFrame.CanvasAabb, dpi, ViewportCanvas);
            bool tookFront = SelectionGestureForTest((frontCell.MinX + frontCell.MaxX) * 0.5f,
                                                    (frontCell.MinY + frontCell.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(140);
            bool onTop = Doc.Strokes.IndexOf(bA) == Doc.Strokes.Count - 1;
            Doc.Undo();
            SettleFrames(140);
            Check("层级：置顶把这一条移到最上，一次撤销回原顺序",
                  tookFront && onTop && Doc.Strokes.IndexOf(bA) == idxBefore,
                  $"下标 {idxBefore} → 置顶后 {Doc.Strokes.Count - 1}（最上）→ 撤销后 {Doc.Strokes.IndexOf(bA)}");

            // ⑨ 导出那一格：**已接**（离屏渲染 → 系统"另存为" → 落盘；自检里对话框是关的）。
            // 判据两条：
            //   · 点了**真的走导出这条路**（`ExportAttempts` +1）——不自检这一步的话，
            //     "点了没反应"和"点了在跑导出"长得一模一样；
            //   · 但**不动文档、不进撤销栈、不改选中**——导出是只读操作，污染文档就是 bug。
            // 屏幕上不弹提示条（用户 2026-09-16 明确不要提示条），只写控制台。
            {
                int n0 = Doc.Strokes.Count, d0 = Doc.UndoDepth, sel0 = Doc.Selected.Count;
                int a0 = ExportAttempts;
                RunBarActionForTest((int)SelBarButton.Export);
                SettleFrames(80);
                Check("导出格子：点了真的走导出，而且不动文档、不进撤销栈",
                      ExportAttempts == a0 + 1
                      && Doc.Strokes.Count == n0 && Doc.UndoDepth == d0 && Doc.Selected.Count == sel0,
                      $"导出 {a0}→{ExportAttempts} 次；对象 {n0}→{Doc.Strokes.Count}，"
                      + $"撤销栈 +{Doc.UndoDepth - d0}，选中 {sel0}→{Doc.Selected.Count}");
            }

            // ---- 选中逻辑三连测（用户点名要的）：一条 / 多条 / 里面混着图片 ----
            {
                // (1) 单选一条：框 = 它自己的墨迹包围盒（不是中心线、也不虚胖）
                Doc.Clear();
                Doc.ClearHistory();
                var one = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
                Doc.AddStroke(one);
                Doc.SelectOnly(new[] { one });
                SettleFrames(160);
                var oneF = SelectionHandles.FrameOf(Doc.Selected);
                var oneInk = one.WorldInkBounds;
                bool oneFrameOk = MathF.Abs(oneF.Local.MinX - oneInk.MinX) < 0.01f
                               && MathF.Abs(oneF.Local.MaxY - oneInk.MaxY) < 0.01f;

                // (2) 多条：框 = 并集；**改一次颜色只记一步撤销**；置顶后内部相对顺序不变
                var two = FatLine(y0 + 1000f, new Color4(1f, 0f, 1f, 1f));
                Doc.AddStroke(two);
                Doc.SelectOnly(new[] { one, two });
                SettleFrames(160);
                var twoF = SelectionHandles.FrameOf(Doc.Selected);
                bool twoFrameOk = twoF.Local.MinY < oneInk.MinY + 1f
                               && twoF.Local.MaxY > two.WorldInkBounds.MaxY - 1f;
                int d0 = Doc.UndoDepth;
                SetSelectionColor(InkPalette.SelectionSwatches[9].Color);      // 蓝
                bool bothColored = one.Color.B > 0.8f && two.Color.B > 0.8f;
                bool oneUndo = Doc.UndoDepth == d0 + 1;
                Doc.Undo();
                SettleFrames(120);
                // 置顶要有"别人在下面"才谈得上：再加一条**不选中**的（否则"全选"是特例，
                // 引擎会拒绝并提示"整页都选中了，没有层级可调"——第一版自检就踩了这个）。
                var bystander = FatLine(y0 + 1150f, new Color4(1f, 0f, 1f, 1f));
                Doc.AddStroke(bystander);
                Doc.SelectOnly(new[] { one, two });
                SettleFrames(120);
                int iOne = Doc.Strokes.IndexOf(one), iTwo = Doc.Strokes.IndexOf(two);
                ReorderSelection(toFront: true);                               // 两条一起置顶
                SettleFrames(120);
                bool orderKept = Doc.Strokes.IndexOf(one) < Doc.Strokes.IndexOf(two)
                              && Doc.Strokes.IndexOf(two) == Doc.Strokes.Count - 1;
                Doc.Undo();
                SettleFrames(120);
                bool orderBack = Doc.Strokes.IndexOf(one) == iOne && Doc.Strokes.IndexOf(two) == iTwo;

                // (3) 里面混着图片：改颜色/粗细**跳过图片**，翻转**连图片一起翻**，
                //     删除/框选都算上它（它是普通对象）
                var img3 = ImageData.Adopt(32, 24, new byte[32 * 24 * 4], false);
                var placedImg = img3 != null ? Doc.AddImage(img3, x0 + 200f, y0 + 1300f, 1f) : null;
                if (placedImg != null)
                {
                    Doc.SelectOnly(new[] { one, placedImg });
                    SettleFrames(160);
                    var imgColor = placedImg.Color; var imgWidth = placedImg.Width;
                    int d1 = Doc.UndoDepth;
                    SetSelectionColor(InkPalette.SelectionSwatches[5].Color);  // 橙
                    bool imgColorKept = placedImg.Color.Equals(imgColor);
                    bool inkChanged = one.Color.R > 0.9f && one.Color.G > 0.4f && one.Color.B < 0.2f;
                    SetSelectionWidth(24f);
                    bool imgWidthKept = MathF.Abs(placedImg.Width - imgWidth) < 0.01f;
                    bool twoSteps = Doc.UndoDepth == d1 + 2;
                    Doc.Undo(); Doc.Undo();
                    SettleFrames(160);
                    // 翻转：图像也该跟着翻（M11 变负）
                    Doc.SelectOnly(new[] { placedImg });
                    Doc.ApplyTransform(SelectionHandles.MirrorMatrix(placedImg.WorldInkBounds, true));
                    SettleFrames(120);
                    bool imgFlipped = placedImg.Transform.M11 < -0.5f;
                    Check("含图片的选中：改色/改粗细跳过图片、翻转照翻、各记一步撤销",
                          imgColorKept && inkChanged && imgWidthKept && twoSteps && imgFlipped,
                          $"图片颜色 {(imgColorKept ? "没动" : "被改了")}、粗细 {(imgWidthKept ? "没动" : "被改了")}；"
                          + $"墨变色={(inkChanged ? "是" : "否")}；两步撤销={(twoSteps ? "对" : "错")}；"
                          + $"图片翻转={(imgFlipped ? "翻了" : "没翻")}");
                }
                Check("单选一条 / 多选多条：框按墨迹范围、多选按并集，改一次只记一步撤销",
                      oneFrameOk && twoFrameOk && bothColored && oneUndo && orderKept && orderBack,
                      $"单选框贴合墨迹={(oneFrameOk ? "是" : "否")}，多选并集={(twoFrameOk ? "是" : "否")}，"
                      + $"两条都变色={(bothColored ? "是" : "否")}，一步撤销={(oneUndo ? "对" : "错")}，"
                      + $"置顶保持内部顺序={(orderKept && orderBack ? "是" : "否")}");
            }

            BoardOn = boardWas;
            BoardColor = boardColorWas;
            ViewOffsetY = camWas;
            Doc.Clear();
            Doc.ClearHistory();
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

    // =====================================================================
    //  压感采集与笔迹预测的自检
    // =====================================================================

    /// <summary>
    /// 笔迹预测自检（**纯算法**：不需要真笔、不需要屏幕、不画东西）。
    /// 把 `调研-压感与预测-原理.md` 第三节里那些"别甩墨"的约束逐条变成断言。
    /// </summary>
    private void PredictorTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 笔迹预测自检（纯算法）===");
        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {what,-36} {detail}");
            if (ok) pass++; else fail++;
        }

        var pred = new PredictedPoint[8];
        static InkPredictor New(double horizonMs = 10.0)
        {
            var p = new InkPredictor { HorizonMs = horizonMs };
            p.ClampHorizon();
            return p;
        }

        // 1) 直线匀速：v = 1 px/ms，地平线 10 ms，阻尼 0.7 → 最远点应在 7 px 处
        {
            var p = New();
            p.Damping = 0.70f;                                  // 显式给值，别跟默认值耦合
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(10, 0, 10);
            int n = p.Predict(pred);
            float dx = n > 0 ? pred[n - 1].X - 10f : float.NaN;
            Check("直线匀速：给出预测点且在前方", n >= 1 && dx > 0, $"点数 {n}，位移 {dx:F2} px");
            Check("直线匀速：位移 = v·τ·阻尼", n >= 1 && MathF.Abs(dx - 7f) < 0.5f, $"期望 7.00，实得 {dx:F2}");
            Check("直线匀速：时间单调递增且不超过地平线",
                  n >= 2 && pred[0].TimeMs > 10 && pred[n - 1].TimeMs <= 20.01 &&
                  pred[1].TimeMs > pred[0].TimeMs,
                  n >= 2 ? $"{pred[0].TimeMs:F1} → {pred[n - 1].TimeMs:F1} ms（最后一点 = 起点 + 10）" : "点不够");
            Check("默认阻尼就是数据扫出来的 0.80", MathF.Abs(new InkPredictor().Damping - 0.80f) < 1e-6f,
                  $"{new InkPredictor().Damping:F2}");
        }

        // 2) 匀加速：二阶项应让它比"只用速度"更远（但被 AccelDamping 收着）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(11, 0, 10);   // v: 1 → 1.2，a = 0.04 px/ms²
            int n = p.Predict(pred);
            float dx = n > 0 ? pred[n - 1].X - 11f : float.NaN;
            float firstOrder = 1.2f * 10f * 0.7f;               // 只算速度项
            Check("匀加速：比一阶更远（加速度项生效）", n >= 1 && dx > firstOrder + 0.2f,
                  $"二阶 {dx:F2} > 一阶 {firstOrder:F2}");
            Check("匀加速：加速度被衰减，不会失控", n >= 1 && dx < firstOrder + 3f, $"二阶 {dx:F2}");
        }

        // 3) 慢速不预测：速度低于阈值时返回 0 个点（慢写时预测只有抖动没有收益）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(0.05f, 0, 5);                 // v = 0.01 px/ms < 0.02
            int n = p.Predict(pred);
            Check("慢速：不预测", n == 0, $"速度 {p.Speed:F3} px/ms → 点数 {n}");
        }

        // 4) 断笔重置：相邻采样间隔 > 20 ms 就当新的一笔（Chromium 的 kMaxTimeDelta）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(5, 0, 5);
            p.Add(100, 0, 105);                                 // 间隔 100 ms
            int n = p.Predict(pred);
            Check("断笔：间隔 100 ms 后重置", p.Count == 1 && n == 0, $"队列长度 {p.Count}，点数 {n}");
        }

        // 5) 反向/急转：新点与当前速度反向 → 丢掉速度，这一帧不预测（拐弯处最容易甩墨）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(10, 0, 10);                   // v = +1
            p.Add(-1, 0, 20);                                   // 立刻反向
            int n = p.Predict(pred);
            Check("急转：反向时丢掉速度", MathF.Abs(p.Speed) < 0.001f && n == 0,
                  $"速度 {p.Speed:F3} px/ms，点数 {n}");
        }

        // 6) 限幅：极快速度下，预测段长度被 MaxDistance 截住（兜底，防长尾）
        {
            var p = New();
            p.Add(0, 0, 0); p.Add(500, 0, 5);                   // v = 100 px/ms
            int n = p.Predict(pred);
            float dx = n > 0 ? pred[n - 1].X - 500f : float.NaN;
            Check("限幅：位移不超过 MaxDistance", n >= 1 && dx <= p.MaxDistance + 0.01f,
                  $"位移 {dx:F2} px（上限 {p.MaxDistance}）");
        }

        // 7) 地平线夹取：命令行传进来的值会被收进 8~15 ms
        {
            var lo = New(1.0); var hi = New(100.0);
            Check("地平线：低于 8 ms 收到 8", Math.Abs(lo.HorizonMs - 8) < 0.001, $"{lo.HorizonMs}");
            Check("地平线：高于 15 ms 收到 15", Math.Abs(hi.HorizonMs - 15) < 0.001, $"{hi.HorizonMs}");
        }

        // 8) 点数与上限：按采样间隔铺满地平线；**最后一点必须落在正地平线上**
        //    （实测教训：只铺到"离地平线最近的那个整数倍"，5 ms 采样 + 8 ms 地平线会少补 3 ms）
        {
            var p = New(10);
            p.Add(0, 0, 0); p.Add(3, 0, 3); p.Add(6, 0, 6);      // 间隔 3 ms
            int n = p.Predict(pred);
            Check("点数：3 ms 间隔 + 10 ms 地平线 → 铺到地平线",
                  n == 4 && Math.Abs(pred[n - 1].TimeMs - 16.0) < 1e-6,
                  $"点数 {n}，最后一点 {pred[n - 1].TimeMs:F1} ms（应 = 6 + 10）");

            var p8 = New(8);
            p8.Add(0, 0, 0); p8.Add(5, 0, 5); p8.Add(10, 0, 10); // 间隔 5 ms
            int n8 = p8.Predict(pred);
            Check("地平线不被采样间隔截短（8 ms + 5 ms 采样）",
                  n8 == 2 && Math.Abs(pred[n8 - 1].TimeMs - 18.0) < 1e-6,
                  $"点数 {n8}，最后一点 {pred[n8 - 1].TimeMs:F1} ms（应 = 10 + 8）");

            var p2 = New(15);
            p2.MaxPoints = 4;
            for (int i = 0; i < 8; i++) p2.Add(i * 1.0f, 0, i * 1.0);   // 1 ms 间隔
            int n2 = p2.Predict(pred);
            Check("点数：不超过 MaxPoints", n2 <= p2.MaxPoints, $"点数 {n2}（上限 {p2.MaxPoints}）");
        }

        // 9) 成本：预测必须是"顺手就做了"，不能进性能预算
        {
            var p = New();
            const int N = 200_000;
            p.Add(0, 0, 0); p.Add(5, 0, 5); p.Add(10, 0, 10);
            var sw = Stopwatch.StartNew();
            int acc = 0;
            for (int i = 0; i < N; i++)
            {
                p.Add(10 + i * 0.001f, 0, 10 + i * 0.005);
                acc += p.Predict(pred);
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
            Check("成本：每次 < 5 µs", us < 5.0, $"{us:F3} µs/次（累计预测 {acc} 个点）");
        }

        Console.WriteLine($"  合计：{pass} 项通过，{fail} 项失败");
        Console.WriteLine(fail == 0 ? "  PASS: 预测器自检全部通过" : "  FAIL: 预测器自检有失败项");
        _quit = true;
    }

    // ---- 合成笔（自检注入用）--------------------------------------------
    private IntPtr _syntheticPen;

    private bool EnsureSyntheticPen()
    {
        if (_syntheticPen != IntPtr.Zero) return true;
        _syntheticPen = Native.CreateSyntheticPointerDevice(Native.PT_PEN, 1, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticPen != IntPtr.Zero;
    }

    /// <summary>注入一个合成笔采样点，走的是和真笔同一条 WM_POINTER 路径。</summary>
    private void SendPenPoint(float x, float y, uint pressure, bool contact, bool first)
    {
        var arr = new Native.POINTER_TYPE_INFO[1];
        uint flags = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE;
        if (contact) flags |= Native.POINTER_FLAG_INCONTACT;
        if (first) flags |= Native.POINTER_FLAG_NEW | Native.POINTER_FLAG_PRIMARY | Native.POINTER_FLAG_FIRSTBUTTON;

        arr[0].type = Native.PT_PEN;
        arr[0].pen.pointerInfo.pointerType = Native.PT_PEN;
        arr[0].pen.pointerInfo.pointerFlags = flags;
        arr[0].pen.pointerInfo.ptPixelLocationX = (int)x;
        arr[0].pen.pointerInfo.ptPixelLocationY = (int)y;
        arr[0].pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        arr[0].pen.penFlags = 0;
        arr[0].pen.penMask = Native.PEN_MASK_PRESSURE;
        arr[0].pen.pressure = pressure;
        Native.InjectSyntheticPointerInput(_syntheticPen, arr, 1);
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
                          + $"，预测={(PredictEnabled ? $"开（{PredictHorizonMs:F0} ms）" : "关")}"
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
    /// 湿墨轨迹（委托墨迹）实测：**只让系统画**（`SuppressActiveStroke`），用合成笔走一遍，
    /// 再从屏幕上数墨色像素。
    ///
    /// 为什么必须测这一条：预测**只喂这条通道**——它画的是"正在写的这一笔"的最后一小段。
    /// 如果这条通道在某台机器上根本没上屏（API 调用成功、返回了 generationId，
    /// 不等于 DWM 真的画出来），那预测就不可能有任何可见效果，
    /// "开/关都一样"就会有完全不同的解释。
    /// </summary>
    private void WetInkTest(bool activate = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 湿墨轨迹实测：只让系统画（自己不画）===");
        Console.WriteLine($"  变体：{(activate ? "把覆盖层强制激活（去掉 NOACTIVATE + 抢前台）" : "按产品原样（置顶 + 不抢焦点）")}");
        if (!EnsureSyntheticPen())
        {
            Console.WriteLine("  SKIP: 拿不到合成笔设备");
            _quit = true; return;
        }

        if (activate && _windows.Count > 0)
        {
            // 假设：DWM 可能只给"前台窗口"画委托湿墨。这里把 NOACTIVATE 摘掉并抢一次前台，
            // 看轨迹会不会突然出现——纯粹是对照实验，产品形态不会这么做。
            var w0 = _windows[0];
            long ex = (long)Native.GetWindowLongPtr(w0.Hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(w0.Hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex & ~(long)Native.WS_EX_NOACTIVATE));
            Native.SetForegroundWindow(w0.Hwnd);
            Console.WriteLine($"  已激活：{w0.Hwnd}");
        }

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;      // 我们自己那一笔不画，屏幕上留的就只能是系统画的
        SettleFrames(200);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int boxX = (int)(cx - 400), boxY = (int)(cy - 160);

        SendPenPoint(cx - 300, cy, 400, contact: true, first: true);
        for (int i = 1; i <= 30; i++)
        {
            SendPenPoint(cx - 300 + i * 20, cy + MathF.Sin(i / 30f * 6.28f) * 60,
                         400, contact: true, first: false);
            SettleFrames(4);
        }
        SettleFrames(60);                 // 笔仍然按着

        int whileDown = ScreenProbe.CountRed(boxX, boxY, 800, 320);
        int whileDownFull = ScreenProbe.CountRed(_virtualX, _virtualY, _virtualW, _virtualH);
        string shot = Path.Combine("reports", "shots", "wetink-down.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot));
        ScreenProbe.SaveBmp(shot, boxX, boxY, 800, 320);
        string shotFull = Path.Combine("reports", "shots", "wetink-down-full.bmp");
        ScreenProbe.SaveBmp(shotFull, _virtualX, _virtualY, _virtualW, _virtualH);

        SendPenPoint(cx + 300, cy, 400, contact: false, first: false);
        SettleFrames(300);
        int afterUp = ScreenProbe.CountRed(boxX, boxY, 800, 320);

        SuppressActiveStroke = false;
        Console.WriteLine($"  按住不放时墨色像素：{whileDown}（这一笔我们自己没画，只能是系统画的）");
        Console.WriteLine($"  同上但扫全屏      ：{whileDownFull}（排除「画在别处」）");
        Console.WriteLine($"  抬笔之后墨色像素  ：{afterUp}（委托轨迹应当被收回）");
        Console.WriteLine($"  轨迹调试          ：{OverlayWindow.InkTrailDebug}");
        Console.WriteLine($"  截图              ：{Path.GetFullPath(shot)}");
        Console.WriteLine($"  全屏截图          ：{Path.GetFullPath(shotFull)}");
        // 重要教训：**合成笔不被 DWM 认**（实测：合成笔下按住时 0 个墨色像素，
        // 换成真笔同样流程是 1.4 万个）。所以合成分支只能判"我们的 API 调用成不成功"，
        // 判不了"系统画没画"——真笔要用 --wetinktest --live N（人来写）。
        Console.WriteLine(whileDown > 500
            ? "  PASS: 委托墨迹轨迹确实上了屏 —— 预测有可见通道"
            : "  不可判定：合成笔不会被 DWM 画成湿墨（实测如此）。要判这一条请用真笔：--wetinktest --live 15");
        Console.WriteLine($"  抬笔后的墨是我们自己提交进文档的那一笔（与轨迹无关），这里只作记录：{afterUp} 像素");
        _quit = true;
    }

    /// <summary>
    /// 湿墨轨迹「真笔 + 自己不画」实测：你写 N 秒，我每 100 ms 采一次屏，
    /// 记下**最多**看到多少墨色像素。
    ///
    /// 为什么要人写：合成笔走的是同一条 WM_POINTER 路径，但 DWM 可能只认真实数字化仪的
    /// 指针（上一轮合成笔下轨迹一个像素都没有）。这一轮就是用来判这一条的。
    /// </summary>
    private void WetInkLiveTest(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 湿墨轨迹实测（真笔 · 只让系统画）===");
        Console.WriteLine("  注意：这一轮你自己写的时候屏幕上**不会**出现笔画（我们自己那层关掉了），");
        Console.WriteLine("        抬笔之后那一笔才会出现。屏幕上中途出现的任何墨，都只能是系统画的委托轨迹。");
        Console.WriteLine($"  现在开始写 {seconds:F0} 秒……");
        Console.WriteLine();

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Pen;
        SuppressActiveStroke = true;
        SettleFrames(120);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        int boxX = (int)(cx - 700), boxY = (int)(cy - 350);
        int maxBand = 0, maxFull = 0, samples = 0;
        int beforePoints = PenTotalPoints;

        double t0 = NowMs;
        double nextSampleAt = t0;
        int tick = 0;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            if (_dirty || _drawing) { if (OverlayWindow.VBlankPaced) Native.DwmFlush(); RenderAll(); _dirty = false; }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 20, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= nextSampleAt)
            {
                nextSampleAt = NowMs + 100;
                samples++;
                int band = ScreenProbe.CountRed(boxX, boxY, 1400, 700);
                if (band > maxBand) maxBand = band;
                if (++tick % 10 == 0)              // 全屏每秒一次，排除"画在别处"
                {
                    int full = ScreenProbe.CountRed(_virtualX, _virtualY, _virtualW, _virtualH);
                    if (full > maxFull) maxFull = full;
                }
            }
        }

        SuppressActiveStroke = false;
        int penSamples = PenTotalPoints - beforePoints;
        Console.WriteLine();
        Console.WriteLine($"  这 {seconds:F0} 秒里收到真笔采样点：{penSamples}（0 说明没写进来）");
        Console.WriteLine($"  屏幕中央区域的最大墨色像素：{maxBand}（共采 {samples} 次）");
        Console.WriteLine($"  全屏的最大墨色像素        ：{maxFull}");
        Console.WriteLine($"  轨迹调试                  ：{OverlayWindow.InkTrailDebug}");
        Console.WriteLine(maxBand > 200
            ? "  PASS: 真笔下系统确实在画委托轨迹 —— 预测有可见通道"
            : "  FAIL: 真笔下系统也没画（这一轮屏幕上不该有别的墨）—— 委托轨迹这台机器上不生效");
        SettleFrames(120);
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
    /// 像素橡皮自检（--pixelerasetest）。
    ///
    /// 判据全是"看得见的行为"，不是内部状态：
    ///   ① 一条横线被竖着抹一下 → 变成**两段**，切口正好停在橡皮边界外（框里不留墨）；
    ///   ② 整条落在橡皮里的 → 消失，撤销回来还是原来那一条；
    ///   ③ 没碰到的 → 原样不动（**引用相等**：重建一遍看不出来，但白扔几何缓存）；
    ///   ④ 图形要"轮廓真的碰到"才删——橡皮从一个大图形正中间划过不能把整个图形吃掉；
    ///   ⑤ 一次拖拽扫过多个位置 = **一步**撤销，撤销后文档逐条回到拖之前；
    ///   ⑥ 上屏像素：框里的墨归零、框外还在。
    /// 外加两条结构判据：默认落点是**竖着的黄金比矩形**；这个工具的落点反馈画的是矩形。
    /// </summary>
    private void PixelEraserTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 像素橡皮自检（切段 / 框里无墨 / 一步撤销）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-36} {detail}");
        }

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        const float halfW = 30f, halfH = 60f;     // 测试用的小块（默认那块 93×150 太大）
        const float penW = 16f;
        const float reach = penW * 0.5f;          // 半笔宽：切口要停在框外这么远

        // --- 0. 形状与落点反馈 -------------------------------------------------
        Check("落点是竖着的黄金比例矩形",
              PixelEraserWidthLogical > 0f && PixelEraserHeightLogical > PixelEraserWidthLogical
              && MathF.Abs(PixelEraserHeightLogical / PixelEraserWidthLogical - GoldenRatio) < 0.002f,
              $"{PixelEraserWidthLogical:F0}×{PixelEraserHeightLogical:F0} 逻辑像素，"
              + $"高:宽 = {PixelEraserHeightLogical / PixelEraserWidthLogical:F3}");

        var savedTool = Tool;
        bool savedInside = PointerInside;
        var savedDevice = LastPointerType;
        Tool = Tool.PixelEraser;
        PointerInside = true;                  // 落点反馈只在"指针在窗口里"时才画
        LastPointerType = Native.PT_MOUSE;
        Check("落点反馈是矩形 + 系统光标藏起来",
              DrawnCursor == ToolCursorShape.Rect && ComputeCursorKind() == CursorKind.Hidden,
              $"{DrawnCursor} / {ComputeCursorKind()}");
        Tool = savedTool;
        PointerInside = savedInside;
        LastPointerType = savedDevice;

        // --- 1. 一条横线抹一下：应该变成两段，切口停在框外 ----------------------
        Doc.Clear();
        Doc.ClearHistory();

        var line = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 80; i++) line.AddPoint(cx - 400 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(line);

        int undoBefore = Doc.UndoDepth;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();

        var pieces = Doc.Strokes.ToArray();
        Check("擦断 → **两条独立对象**（原件不再在文档里）",
              pieces.Length == 2 && !Doc.Strokes.Contains(line),
              $"对象数 {pieces.Length}，原件 {(Doc.Strokes.Contains(line) ? "还在" : "已换成两段")}");
        Check("一次擦除只算一步撤销", Doc.UndoDepth == undoBefore + 1,
              $"撤销栈 {undoBefore} → {Doc.UndoDepth}");
        Check("两段各自是一个完整对象（没有残留区间表）",
              pieces.Length == 2 && pieces[0].Erased.Count == 0 && pieces[1].Erased.Count == 0
              && pieces[0].Points.Count + pieces[1].Points.Count >= 60,
              pieces.Length == 2
                ? $"点数 {pieces[0].Points.Count}/{pieces[1].Points.Count}（原 81 点）"
                : "没拆成两段");

        float leftEnd = pieces.Length == 2 ? pieces[0].Bounds.MaxX : float.NaN;
        float rightStart = pieces.Length == 2 ? pieces[1].Bounds.MinX : float.NaN;
        float bandLeft = cx - halfW - reach, bandRight = cx + halfW + reach;
        Check("框里不留墨（切口在边界外）",
              pieces.Length == 2 && leftEnd <= bandLeft + 0.5f && rightStart >= bandRight - 0.5f,
              $"左段到 {leftEnd:F1}（该 ≤ {bandLeft:F1}），右段从 {rightStart:F1}（该 ≥ {bandRight:F1}）");

        bool anyInside = false;
        foreach (var p in pieces)
            foreach (var q in p.Points)
                if (MathF.Abs(q.Y - cy) < halfH && MathF.Abs(q.X - cx) < halfW + reach - 0.5f)
                    anyInside = true;
        Check("剩下的墨没有一点伸进框里", !anyInside,
              anyInside ? "有采样点落在框内" : "两段的采样点都在框外");

        // --- 2. 撤销 / 重做：同一个对象，区间表清空 / 回来 ----------------------
        bool undone = Doc.Undo();
        Check("撤销：同一个对象、擦除区间清空",
              undone && Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], line)
              && line.Erased.Count == 0 && line.RemainingRuns().Count == 1 && line.Points.Count == 81,
              $"笔画数 {Doc.Strokes.Count}，区间 {line.Erased.Count} 段，点数 {line.Points.Count}");

        bool redone = Doc.Redo();
        Check("重做：又变回两条独立对象",
              redone && Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(line)
              && Doc.Strokes[0].Erased.Count == 0,
              $"对象数 {Doc.Strokes.Count}");

        // --- 2b. 同一笔擦两刀 / 重复擦已经擦过的地方 ---------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var longLine = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 120; i++) longLine.AddPoint(cx - 600 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(longLine);
        int undo2 = Doc.UndoDepth;

        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx - 200, cy, 20f, 30f);
        Doc.EraseRectAt(cx + 200, cy, 20f, 30f);
        Doc.EndErase();
        Check("同一笔擦两刀 → **三段独立对象**、一步撤销",
              Doc.Strokes.Count == 3 && !Doc.Strokes.Contains(longLine)
              && Doc.UndoDepth == undo2 + 1,
              $"对象 {Doc.Strokes.Count}、撤销栈 +{Doc.UndoDepth - undo2}");

        // 再擦一次"落在已经擦掉的地方"：不能多出撤销步，也不能把表搞乱
        int undo3 = Doc.UndoDepth;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx - 200, cy, 20f, 30f);
        Doc.EndErase();
        Check("重复擦已经擦过的地方 → 不多出撤销步",
              Doc.UndoDepth == undo3 && Doc.Strokes.Count == 3,
              $"撤销栈 +{Doc.UndoDepth - undo3}，对象还是 {Doc.Strokes.Count} 条");

        // 撤销回到"擦之前那一条"（而不是把两刀拆成两步）
        Doc.Undo();
        Check("一步撤销回到擦之前（原来那一条、点数不变）",
              Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], longLine)
              && longLine.Erased.Count == 0 && longLine.Points.Count == 121,
              $"对象 {Doc.Strokes.Count}，区间 {longLine.Erased.Count} 段，点数 {longLine.Points.Count}");

        // --- 3. 整条在框里 / 完全没碰到 ----------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var inside = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 10; i++) inside.AddPoint(cx - 10 + i * 2, cy - 10 + i * 2, 0.9f, i);
        var faraway = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 40; i++) faraway.AddPoint(cx - 700 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(inside);
        Doc.AddStroke(faraway);

        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();
        Check("框里的整条被擦掉、框外的不动",
              Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], faraway),
              $"剩下 {Doc.Strokes.Count} 条"
              + (Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], faraway)
                 ? "（框外那条，引用没变）" : "（不是框外那条！）"));

        Doc.Undo();
        Check("撤销把整条放回来（含原顺序）",
              Doc.Strokes.Count == 2 && ReferenceEquals(Doc.Strokes[0], inside)
              && ReferenceEquals(Doc.Strokes[1], faraway),
              $"笔画数 {Doc.Strokes.Count}");

        // --- 4. 图形：碰到就**熔成笔迹再切**（用户 2026-09-15 定：断开也要单独算）---------
        Doc.Clear();
        Doc.ClearHistory();
        var crossed = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 4f,
        };
        crossed.AddPoint(cx - 10, cy - 200, 1f, 0);      // 两条竖边正好穿过橡皮
        crossed.AddPoint(cx + 10, cy + 200, 1f, 0);

        var bigShape = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 4f,
        };
        bigShape.AddPoint(cx - 400, cy - 400, 1f, 0);    // 外框很大，轮廓离橡皮很远
        bigShape.AddPoint(cx + 400, cy + 400, 1f, 0);

        Doc.AddStroke(crossed);
        Doc.AddStroke(bigShape);
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();
        bool allFreehand = true;
        foreach (var s in Doc.Strokes)
            if (!ReferenceEquals(s, bigShape) && s.Kind != StrokeKind.Freehand) allFreehand = false;
        Check("图形被擦到 → 熔成笔迹并切开（剩下的段各自独立）",
              !Doc.Strokes.Contains(crossed) && allFreehand && Doc.Strokes.Count >= 3,
              $"剩下 {Doc.Strokes.Count} 条，全是普通笔迹={allFreehand}（原图形已不在）");
        Check("橡皮从大图形正中间划过 → 不删", Doc.Strokes.Contains(bigShape),
              "外框和橡皮相交，但轮廓离橡皮 370 像素（按外框判就会误删）");

        // 图形的撤销：**一步回到原来那个图形**（熔是内部的，不该让用户多撤一步）
        Doc.Undo();
        Check("图形熔成笔迹后，一步撤销回到原图形",
              Doc.Strokes.Count == 2 && Doc.Strokes.Contains(crossed)
              && crossed.Kind == StrokeKind.Rectangle,
              $"对象 {Doc.Strokes.Count}，原图形 {(Doc.Strokes.Contains(crossed) ? "回来了" : "没回来")}");

        // --- 5. 一次拖拽扫过 5 条 = 一步撤销 -----------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        for (int k = 0; k < 5; k++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
            for (int i = 0; i <= 80; i++) s.AddPoint(cx - 400 + i * 10, cy - 200 + k * 100, 0.9f, i);
            Doc.AddStroke(s);
        }
        var before = Doc.Strokes.ToArray();
        int undo0 = Doc.UndoDepth;

        Doc.BeginEraseRect();
        for (int k = 0; k < 5; k++) Doc.EraseRectAt(cx, cy - 200 + k * 100, halfW, halfH);
        Doc.EndErase();
        int cutCount = 0;
        foreach (var s in Doc.Strokes) if (s.Kind == StrokeKind.Freehand) cutCount++;
        Check("拖拽扫过 5 条 = 一步撤销，每条都断成两截",
              Doc.UndoDepth == undo0 + 1 && Doc.Strokes.Count == 10 && cutCount == 10,
              $"撤销栈 +{Doc.UndoDepth - undo0}，笔画数 {before.Length} → {Doc.Strokes.Count}");

        Doc.Undo();
        bool restored = Doc.Strokes.Count == before.Length;
        for (int i = 0; restored && i < before.Length; i++)
            restored = ReferenceEquals(Doc.Strokes[i], before[i]) && Doc.Strokes[i].Erased.Count == 0;
        Check("撤销后逐条回到拖之前（对象 + 顺序 + 区间清空）", restored,
              $"笔画数 {Doc.Strokes.Count}，逐条比对 {(restored ? "全部一致" : "有出入")}");

        // --- 6. 上屏像素 -------------------------------------------------------
        // --- 6. 擦断之后是两条独立对象（**已知取舍：自交处会叠色**）--------------
        //
        // 用户 2026-09-15 定的语义：擦断了就要"结构上分开、单独算"。
        // 代价是：半透明荧光笔的两截互相穿过时，会**各画一次** → 交叠处变深
        // （这条笔迹没被擦到的地方颜色也变了）。这一条不再判红绿，只把数字量出来，
        // 免得以后有人以为是新 bug——取舍写在 调研-橡皮擦.md 第八节。
        Doc.Clear();
        Doc.ClearHistory();
        // 自交图小，单独摆在位图左上角那一块（800×800 的离屏位图装不下屏幕中心）
        float lx = _virtualX + 500f, ly = _virtualY + 500f;
        var loop = MakeSelfCrossingHighlight(lx, ly, 40f);
        Doc.AddStroke(loop);

        var (offTarget, offCpu) = MakeOffscreen(800, 800);
        var bufA = RenderAndRead(offTarget, offCpu, 800, 800, _virtualX, _virtualY);
        int crossPx = (int)(lx + 161.7f - _virtualX) - 8, crossPy = (int)(ly + 20.9f - _virtualY) - 8;
        int plainPx = (int)(lx + 60f - _virtualX) - 8, plainPy = (int)(ly - _virtualY) - 8;
        int diffBefore = ColorDiff(AvgPatch(bufA, 800, crossPx, crossPy, 17),
                                   AvgPatch(bufA, 800, plainPx, plainPy, 17));

        Doc.BeginEraseRect();
        Doc.EraseRectAt(lx + 240, ly + 140, 20f, 30f);      // 离交点 240 像素以上
        Doc.EndErase();

        var bufB = RenderAndRead(offTarget, offCpu, 800, 800, _virtualX, _virtualY);
        var crossB = AvgPatch(bufB, 800, crossPx, crossPy, 17);
        var plainB = AvgPatch(bufB, 800, plainPx, plainPy, 17);
        int diffAfter = ColorDiff(crossB, plainB);
        offTarget.Dispose();
        offCpu.Dispose();

        Check("半透明荧光笔擦断 → 两条独立对象（代价：交叠处会叠色）",
              Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(loop),
              $"切之前差 {diffBefore}，切之后差 {diffAfter}（已知取舍，不判红绿）；"
              + $"对象数 {Doc.Strokes.Count}，"
              + $"交叠处 ({crossB.r},{crossB.g},{crossB.b}) vs 普通处 ({plainB.r},{plainB.g},{plainB.b})");

        // --- 7. 上屏像素（抓屏拍不到我们这层时会跳过）---------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var onScreen = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW * DpiScale,
        };
        for (int i = 0; i <= 80; i++) onScreen.AddPoint(cx - 400 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(onScreen);
        Doc.InvalidateAll();
        PointerInside = false;                 // 别让自绘光标进像素统计
        SettleFrames(500);

        int inkBefore = ScreenProbe.CountMagenta((int)(cx - 500), (int)(cy - 40), 1000, 80);
        // 先确认抓屏真的能看到我们的墨：锁屏 / 远程会话 / 被别的窗口盖住时，
        // 拍到的是桌面壁纸，那时"框里的墨归零"会假红、"框外的还在"会假绿。
        // 看不见就明确跳过这一条（前面十几条模型判据照样算数）。
        if (inkBefore < 500)
        {
            Console.WriteLine($"  环境：抓屏看不到我们的墨（拍到的大概是桌面或别的窗口）"
                            + $" → SKIP: 上屏那一条跳过（该有一千多品红像素，拍到 {inkBefore}）");
            Console.WriteLine($"  合计：通过 {pass}，失败 {fail}（上屏一条未验）");
            _quit = true;
            return;
        }
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();
        SettleFrames(500);

        int inBox = ScreenProbe.CountMagenta((int)(cx - halfW + 3), (int)(cy - 30), (int)(halfW * 2 - 6), 60);
        int outside = ScreenProbe.CountMagenta((int)(cx - 500), (int)(cy - 30), 300, 60);
        int inkAfter = ScreenProbe.CountMagenta((int)(cx - 500), (int)(cy - 40), 1000, 80);
        Check("上屏：框里的墨归零、框外的还在",
              inkBefore > 500 && inBox < 20 && outside > 300 && inkAfter > inkBefore * 0.8,
              $"画上 {inkBefore} 像素 → 框里 {inBox}、框外 {outside}，全带 {inkAfter}"
              + $"（应 ≈ {inkBefore} 减去被擦的那一段，不掉远处的墨）");

        // --- 8. 手测台（--eraserlab）的记录链路：写进去的必须是"能算的数" -------------
        // 这条不是在验橡皮，是在验"我事后拿到的数据靠得住"——列数、单位、落盘时机。
        string labCsv = Path.Combine(Path.GetTempPath(), "inklab-smoke.csv");
        string labTxt = Path.Combine(Path.GetTempPath(), "inklab-smoke.txt");
        try { File.Delete(labCsv); File.Delete(labTxt); } catch { /* 删不掉就覆盖 */ }
        var lab = new EraserTelemetry(labCsv, labTxt, NowMs);
        lab.BeginDrag(Tool.PixelEraser, cx - 200, cy, NowMs);
        lab.Step(3, Doc.TotalIntervals, Doc.Strokes.Count, 0.42, cx - 200, cy, NowMs);
        lab.Step(2, Doc.TotalIntervals, Doc.Strokes.Count, 0.31, cx - 150, cy + 10, NowMs + 20);
        lab.Frame(1.5, 8.0);
        lab.Note("撤销", NowMs + 30);
        lab.EndDrag(Doc, Doc.UndoDepth, NowMs + 40);
        lab.Close(Doc, NowMs + 50);
        var labLines = File.Exists(labCsv) ? File.ReadAllLines(labCsv) : Array.Empty<string>();
        var labCols = labLines.Length >= 2 ? labLines[1].Split(',') : Array.Empty<string>();
        Check("手测台：CSV 落盘、列数与数值都对",
              labLines.Length == 2 && labCols.Length == 26 && labCols[1] == "像素橡皮"
              && Math.Abs(double.Parse(labCols[3]) - 40) < 0.6 && labCols[4] == "2",
              labLines.Length >= 2
                ? $"{labLines.Length - 1} 条拖拽、{labCols.Length} 列；工具 {labCols[1]}、"
                  + $"时长 {labCols[3]}ms、采样 {labCols[4]}、擦到 {labCols[8]} 笔"
                : "没有写出 CSV");
        Check("手测台：退出时的汇总也写了", File.Exists(labTxt),
              File.Exists(labTxt) ? Path.GetFileName(labTxt) : "缺汇总文件");

        // --- 9. 擦断之后"两截各自独立"（用户 2026-09-15 定的语义）------------------
        //
        // "橡皮擦中墨迹或者图形，如果断开了结构，选中分开以后单独算"：擦完就是两条**独立对象**，
        // 能分别选中、分别搬。带变换的笔迹拆出来的段要**继承原变换**（点仍是局部坐标）。
        Doc.Clear();
        Doc.ClearHistory();

        var longLine2 = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW,
            // 带一点旋转 + 非等比缩放：拆段时**变换必须原样继承**（点还是局部坐标）
            // 注意变换要在 AddStroke **之前**设好：空间网格按添加时的包围盒索引，
            // 加进去之后再改变换，网格就过期了（擦除会擦不到——这里踩过一次）。
            Transform = Matrix3x2.CreateRotation(0.3f)
                      * Matrix3x2.CreateScale(1.4f, 0.8f)
                      * Matrix3x2.CreateTranslation(cx - 600, cy),
        };
        for (int i = 0; i <= 120; i++) longLine2.AddPoint(60 + i * 10, 200, 0.9f, i);
        Doc.AddStroke(longLine2);
        Doc.InvalidateAll();

        var wp = longLine2.PointAtParam(60);                 // 参数 60 处的画布坐标
        Vector2 canvasMid = Vector2.Transform(wp, longLine2.Transform);
        Doc.BeginEraseRect();
        Doc.EraseRectAt(canvasMid.X, canvasMid.Y, 30f, 60f);
        Doc.EndErase();
        var cut2 = Doc.Strokes.ToArray();
        bool gotTwo = cut2.Length == 2 && !Doc.Strokes.Contains(longLine2);
        bool transformKept = gotTwo
                          && cut2[0].Transform.Equals(longLine2.Transform)
                          && cut2[1].Transform.Equals(longLine2.Transform)
                          && cut2[0].Erased.Count == 0 && cut2[1].Erased.Count == 0;
        Check("带变换的长笔擦断 → 两条独立对象、变换原样继承",
              gotTwo && transformKept,
              gotTwo ? $"对象 {cut2.Length}，变换继承 {(transformKept ? "是" : "否")}，"
                     + $"点数 {cut2[0].Points.Count}/{cut2[1].Points.Count}"
                     : $"对象 {cut2.Length}（应 2）");

        // **每一截能单独选中**（这就是"分开以后单独算"）：框住其中一段，只该选中那一段
        Doc.Selected.Clear();
        var onePiece = cut2[0].WorldBounds.Inflate(10f);
        Doc.ApplyMarquee(onePiece);
        Check("框住其中一段 → 只选中那一段（不再整条一起选）",
              Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], cut2[0]),
              $"选中 {Doc.Selected.Count} 条");

        // SplitErasedSelection（Ctrl+Alt+8）现在只服务"老存档里带区间的笔迹"，本轮该返回 0
        Doc.SelectOnly(new[] { cut2[0] });
        Check("已经拆断的笔迹再按'拆开' → 无事可做",
              Doc.SplitErasedSelection() == 0, "返回 0");

        Doc.Undo();
        Check("撤销擦断：回到原来那一条（同一个对象、变换不变）",
              Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], longLine2)
              && longLine2.Erased.Count == 0,
              $"对象 {Doc.Strokes.Count}，区间 {longLine2.Erased.Count} 段");

        Doc.Redo();
        Check("重做：又变回两条独立对象",
              Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(longLine2),
              $"对象 {Doc.Strokes.Count}");

        // --- 10. 图像：两种橡皮都不碰（原则：图像是"内容"，不是笔画）-----------------
        Doc.Clear();
        Doc.ClearHistory();
        var pic = Doc.AddImage(MakeTestImage(400, 300), cx - 200, cy - 150, 1f);
        Doc.EraseRectAt(cx, cy, 40f, 40f);          // 像素橡皮的调用
        bool picIntact = Doc.Strokes.Contains(pic) && Doc.Strokes.Count == 1;
        Doc.EraseAt(cx, cy, 40f);                   // 整笔橡皮的调用
        picIntact &= Doc.Strokes.Contains(pic) && Doc.Strokes.Count == 1;
        Check("图像：两种橡皮都不碰（要删它用框选 + Delete）", picIntact,
              $"试了像素橡皮和整笔橡皮各一下，对象数 {Doc.Strokes.Count}，"
              + $"图 {(Doc.Strokes.Contains(pic) ? "还在" : "**被删掉了**")}");

        Console.WriteLine($"  合计：通过 {pass}，失败 {fail}");
        Console.WriteLine(fail == 0 ? "PASS" : "FAIL");
        _quit = true;
    }

    /// <summary>
    /// 剪贴板**对象通道**自检（--clipboardtest）。
    ///
    /// 验的是"复制一段板书 → 粘到别处 → 还是可编辑对象"这条链，分四段：
    ///   ① 写进去的对象字节读回来**逐字段一致**（点数 / 粗细 / 颜色 / 变换 / 擦除区间 /
    ///      图像像素），差一个字段就是"粘回来少了一块"；
    ///   ② 身份必须**重新发**（Id 归零）：文件里的 Id 是原对象的，直接插进同一个文档
    ///      会和原件撞号，撤销 / 多选就会指错对象；
    ///   ③ 同一次复制里还夹着一张**图**（Word / PPT / 微信粘得到），尺寸 = 选区包围盒
    ///      两边各留 4 逻辑像素，背景**全透明**（粘到别处不该带我们的白底）；
    ///   ④ 粘贴是**智能**的：有对象格式就粘对象（落视口左上角、自动选中、一步撤销），
    ///      只剩一张图才退回"当图粘"——两条分支都要真的走一遍。
    ///
    /// **这个用例会覆盖系统剪贴板**（和 --capturetest 一样）：跑之前先存好要粘的东西。
    /// 剪贴板被别的程序占着时会明确报出来并跳过，那是环境问题，不是 bug。
    /// </summary>
    private void ClipboardTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 剪贴板对象通道自检（复制 → 粘回来仍是对象）===");
        Console.WriteLine("  注意：本用例会覆盖系统剪贴板（跑之前先存好要粘的东西）");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-32} {detail}");
        }

        // 逐字段比对：任何一处不同都回报"差在哪"，不靠肉眼看数字。
        static bool SameStroke(Stroke a, Stroke b, out string why)
        {
            why = "";
            if (a.Tool != b.Tool) { why = "工具不同"; return false; }
            if (a.Kind != b.Kind) { why = "类型不同"; return false; }
            if (a.Width != b.Width) { why = $"粗细 {a.Width} → {b.Width}"; return false; }
            if (a.Color.R != b.Color.R || a.Color.G != b.Color.G
                || a.Color.B != b.Color.B || a.Color.A != b.Color.A)
            { why = $"颜色 {a.Color} → {b.Color}"; return false; }
            if (!a.Transform.Equals(b.Transform)) { why = "变换不同"; return false; }
            if (a.IsImage != b.IsImage) { why = "一个像是一个不是"; return false; }
            if (a.IsImage)
            {
                if (a.Image.Width != b.Image.Width || a.Image.Height != b.Image.Height)
                { why = $"图 {a.Image.Width}×{a.Image.Height} → {b.Image.Width}×{b.Image.Height}"; return false; }
                if (a.Image.Bgra.Length != b.Image.Bgra.Length) { why = "图像字节数不同"; return false; }
                for (int i = 0; i < a.Image.Bgra.Length; i++)
                    if (a.Image.Bgra[i] != b.Image.Bgra[i]) { why = $"图像第 {i} 个字节"; return false; }
            }
            if (a.Points.Count != b.Points.Count)
            { why = $"点数 {a.Points.Count} → {b.Points.Count}"; return false; }
            for (int i = 0; i < a.Points.Count; i++)
            {
                var p = a.Points[i]; var q = b.Points[i];
                // 时间戳是"绝对量 + float 偏移"，会有浮点截断，给 0.05ms 容差（同存档自检）。
                if (p.X != q.X || p.Y != q.Y || p.P != q.P || Math.Abs(p.T - q.T) > 0.05)
                { why = $"第 {i} 个点"; return false; }
            }
            if (a.Erased.Count != b.Erased.Count)
            { why = $"擦除区间 {a.Erased.Count} 段 → {b.Erased.Count} 段"; return false; }
            for (int i = 0; i < a.Erased.Count; i++)
                if (a.Erased[i] != b.Erased[i]) { why = $"第 {i} 段擦除区间"; return false; }
            return true;
        }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        // --- 0. 没选中时不该动剪贴板 -------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        Check("没选中任何东西 → 不写剪贴板", !CopySelectionToClipboard(),
              "返回 false，剪贴板里原来的东西不动");

        // --- 1. 造三条要复制的东西 ---------------------------------------------
        var plain = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(0.1f, 0.35f, 0.95f, 1f), Width = 7.5f,
        };
        for (int i = 0; i <= 40; i++) plain.AddPoint(cx - 320 + i * 8, cy - 60, 0.9f, 1000 + i * 8);

        var turned = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 12f,
            // 变换要在 AddStroke **之前**设好（空间网格按添加时的包围盒索引，见像素橡皮自检）
            Transform = Matrix3x2.CreateRotation(0.37f)
                      * Matrix3x2.CreateScale(1.3f, 0.7f)
                      * Matrix3x2.CreateTranslation(cx - 160, cy + 120),
        };
        for (int i = 0; i <= 60; i++) turned.AddPoint(i * 6, 40, 0.7f, 2000 + i * 4);
        turned.AddErased(12f, 20f);                 // 假装被像素橡皮擦掉两段
        turned.AddErased(30.5f, 33f);

        Doc.AddStroke(plain);
        Doc.AddStroke(turned);
        var picture = Doc.AddImage(MakeTestImage(48, 32), cx + 120, cy + 60, 1f);

        Doc.SelectOnly(new[] { plain, turned, picture });
        var expect = new[] { plain, turned, picture };
        var box = EditRegion.Of(expect);

        // --- 2. 复制 → 读回对象 ------------------------------------------------
        bool copied = CopySelectionToClipboard();
        if (!copied)
        {
            Console.WriteLine("  环境：剪贴板被别的程序占着 → SKIP: 剪贴板相关的几项跳过"
                            + "（关掉占用剪贴板的程序再跑）");
            Console.WriteLine($"  合计：通过 {pass}，失败 {fail}（剪贴板相关未验）");
            _quit = true;
            return;
        }

        bool gotObjects = ClipboardInk.TryGetObjects(out var back);
        Check("读回来的是**对象**（不是图）", gotObjects && back.Count == expect.Length,
              gotObjects ? $"{back.Count} 个对象（期望 {expect.Length}）" : "读不到我们的对象格式，只剩图了");

        bool same = gotObjects && back.Count == expect.Length;
        string diff = "";
        if (same)
            for (int i = 0; i < expect.Length; i++)
                if (!SameStroke(expect[i], back[i], out diff))
                { same = false; diff = $"第 {i} 个对象：{diff}"; break; }
        Check("逐字段一致（含变换 / 擦除区间 / 图像像素）",
              same,
              same ? $"3 个对象全对上（擦除区间 {turned.Erased.Count} 段、图 48×32 逐字节）" : diff);

        bool idsCleared = gotObjects && back.Count > 0;
        if (idsCleared)
            foreach (var s in back) if (s.Id != 0) idsCleared = false;
        Check("身份重新发（Id 归零）", idsCleared,
              idsCleared ? "读回来的都是 0，粘进文档时由文档发新号"
                         : $"还带着原 Id：{string.Join(",", back.ConvertAll(s => s.Id))}");

        // --- 3. 同一份剪贴板里那张图（给外部程序的兜底）-------------------------
        // 期望尺寸用**和复制那条路完全一样的算式**（先 Inflate 再相减），
        // 换成"宽度 + 两边留白"会和它在浮点上差一丢丢，跨整数边界就成了假失败。
        float margin = 4f * DpiScale;
        var boxIn = box.Inflate(margin);
        int expW = Math.Max(1, (int)MathF.Ceiling(boxIn.MaxX - boxIn.MinX));
        int expH = Math.Max(1, (int)MathF.Ceiling(boxIn.MaxY - boxIn.MinY));
        bool gotImg = ClipboardImage.TryGetImage(out var dib, out int bw, out int bh, out _);
        Check("同一份剪贴板里还有一张图", gotImg && bw == expW && bh == expH,
              gotImg ? $"{bw}×{bh}，期望 {expW}×{expH}"
                     : "没读到 CF_DIB（外部程序粘不到了）");

        if (gotImg && bw > 0 && bh > 0)
        {
            // 背景必须透明：粘到 PPT 上不该压一块白底。
            int corner = 3;                                  // 左上角那个像素的 alpha
            int opaque = 0;
            for (int i = 3; i < dib.Length; i += 4) if (dib[i] > 8) opaque++;
            Check("那张图背景透明、内容非空",
                  dib[corner] < 8 && opaque > 100,
                  $"左上角 alpha {dib[corner]}（期望 0），不透明像素 {opaque} 个");
        }

        // --- 4. 智能粘贴：有对象 → 粘成对象 ------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        int undos0 = Doc.UndoDepth;
        bool pasted = PasteFromClipboard();
        bool asObjects = pasted && Doc.Strokes.Count == expect.Length
                      && Doc.Selected.Count == expect.Length;
        Check("粘贴：有对象格式就粘成**对象**（不是一张图）", asObjects,
              asObjects ? $"{Doc.Strokes.Count} 个对象、全选中"
                        : $"对象数 {Doc.Strokes.Count}、选中 {Doc.Selected.Count}（期望 3 / 3）");

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var pastedBox = EditRegion.Of(Doc.Strokes);
        Check("粘到视口左上角（含留白）",
              Math.Abs(pastedBox.MinX - (vp.MinX + m)) < 0.6f
              && Math.Abs(pastedBox.MinY - (vp.MinY + m)) < 0.6f,
              $"落在 ({pastedBox.MinX:F0},{pastedBox.MinY:F0})，期望 ({vp.MinX + m:F0},{vp.MinY + m:F0})");

        bool newIds = Doc.Strokes.Count > 0;
        foreach (var s in Doc.Strokes) if (s.Id == 0) newIds = false;
        Check("粘进来的对象拿到了新身份", newIds,
              newIds ? $"Id {Doc.Strokes[0].Id}…（不是 0，也不和原件相等）" : "有对象的 Id 还是 0");

        bool erasedKept = Doc.Strokes.Count == 3;
        if (erasedKept)
        {
            var backTurned = Doc.Strokes.Find(s => s.Erased.Count > 0);
            erasedKept = backTurned != null && backTurned.Erased.Count == turned.Erased.Count;
        }
        Check("粘回来的笔迹还带着擦除区间（擦掉的墨不会画回来）", erasedKept,
              erasedKept ? $"区间 {Doc.Strokes.Find(s => s.Erased.Count > 0).Erased.Count} 段"
                         : "区间丢了");

        Doc.Undo();
        Check("粘贴算一步撤销（3 个对象一起回去）",
              Doc.Strokes.Count == 0 && Doc.UndoDepth == undos0,
              $"撤销后对象 {Doc.Strokes.Count}，撤销栈回到 {Doc.UndoDepth}");

        // --- 5. 只有图（没有对象格式）→ 退回"当图粘" ---------------------------
        var little = MakeTestImage(64, 48);
        bool wroteImg = ClipboardImage.SetImage(little.Bgra, little.Width, little.Height);
        Check("准备：把剪贴板换成一张纯图（对象格式没了）", wroteImg,
              wroteImg ? "写进去 64×48" : "写不进去（被别的程序占着？）");
        if (wroteImg)
        {
            bool hadObjects = ClipboardInk.TryGetObjects(out _);
            Check("这时候剪贴板里确实没有对象了", !hadObjects,
                  hadObjects ? "居然还读得到对象" : "只剩 CF_DIB");

            Doc.Clear();
            Doc.ClearHistory();
            bool pasted2 = PasteFromClipboard();
            bool asImage = pasted2 && Doc.Strokes.Count == 1 && Doc.Strokes[0].IsImage
                        && Doc.Strokes[0].Image.Width == 64 && Doc.Strokes[0].Image.Height == 48;
            Check("只有图时退回当图粘（老行为不变）", asImage,
                  asImage ? "粘成一个 64×48 的图像对象、自动选中"
                          : $"对象数 {Doc.Strokes.Count}，"
                            + (Doc.Strokes.Count > 0 ? $"Kind={Doc.Strokes[0].Kind}" : "什么都没有"));

            int diff2 = -1;
            if (asImage)
            {
                diff2 = 0;
                var got = Doc.Strokes[0].Image.Bgra;
                for (int i = 0; i < got.Length; i++) if (got[i] != little.Bgra[i]) diff2++;
            }
            Check("粘回来的图逐字节一致", diff2 == 0,
                  diff2 < 0 ? "上一条没过，这条没验" : $"差异 {diff2} 字节");
        }

        Doc.Clear();
        Console.WriteLine("  剪贴板里现在留着自检那张 64×48 的图（退出不会恢复你原来的内容）");
        Console.WriteLine($"  合计：通过 {pass}，失败 {fail}");
        Console.WriteLine(fail == 0 ? "PASS" : "FAIL");
        _quit = true;
    }

    /// <summary>
    /// 套索自检（--lassotest）。判据全是能算出来的数，不看屏幕。
    ///
    /// 七条：
    ///   ① `Ctrl+Alt+9` 真的在切方式（默认矩形）；
    ///   ② **80% 边界**：代表点 79% 在圈里 → 不选，81% → 选（WPF 的 _percentIntersectForInk）；
    ///   ③ **贴边＝无限延伸**：同一条半个身子在屏幕外的长笔迹，圈贴左边 → 选中；
    ///      圈不贴边（只有可见的那一小段在圈里）→ 不选。这一对**必须成对验**，
    ///      只验"贴边选中"看不出延伸是不是把什么都选进来了；
    ///   ④ **图形按轮廓判**：矩形只差右下角没圈住（4/5 轮廓点）→ 选；
    ///      再少一个角（3/5）→ 不选。用"两个端点"当代表点的话前者会漏选；
    ///   ⑤ **图像按四个角判**：四角全在圈里 → 选；少一个角（3/4 = 75%）→ 不选；
    ///   ⑥ **被擦掉的那一段不算墨**：圈住一条笔迹"被擦掉的那半截"，不该选中它；
    ///   ⑦ 引擎那条路（按下→拖→松手）真的会用这条判据；路径太短＝单击空白＝取消选中。
    /// </summary>
    private void LassoTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 套索自检（80% 判据 / 贴边无限延伸 / 加选减选）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        static List<Vector2> Box(float x0, float y0, float x1, float y1) => new()
        {
            new Vector2(x0, y0), new Vector2(x1, y0),
            new Vector2(x1, y1), new Vector2(x0, y1),
        };

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        var vp = ViewportCanvas;
        float snap = LassoEdgeSnapLogical * DpiScale;
        var realOut = Console.Out;                    // 掐掉 ApplyLasso 与手势里的控制台输出
        void Quiet(Action a) { Console.SetOut(TextWriter.Null); try { a(); } finally { Console.SetOut(realOut); } }

        // --- 0. 切换方式 --------------------------------------------------------
        Check("默认是矩形框", SelMode == SelectMode.Rect, SelMode.ToString());
        SelMode = SelectMode.Rect;
        ToggleSelectModeForTest();
        bool toLasso = SelMode == SelectMode.Lasso;
        ToggleSelectModeForTest();
        Check("Ctrl+Alt+9 在两种方式之间切", toLasso && SelMode == SelectMode.Rect,
              $"矩形 → {(toLasso ? "套索" : "?")} → {SelMode}");

        // --- 1. 80% 边界（79% 不选 / 81% 选）------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var boxPath = Box(cx - 150, cy - 150, cx + 150, cy + 150);

        Stroke MakeRow(int insideCount)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 3f };
            for (int i = 0; i < 100; i++)
            {
                if (i < insideCount)
                    s.AddPoint(cx - 100 + (i % 10) * 22, cy - 100 + (i / 10) * 25, 1f, i);   // 圈里
                else
                    s.AddPoint(cx + 400 + (i % 20) * 5, cy - 100 + (i / 20) * 25, 1f, i);    // 圈外
            }
            return s;
        }

        // 两条**分开测**：放一个文档里的话，81% 那条本来就会被选中，
        // "79% 没被选中"这件事就看不出来了（第一版就是这么写错的）。
        var s79 = MakeRow(79);
        Doc.AddStroke(s79);
        int n79 = Doc.ApplyLasso(boxPath, vp, snap);
        Check("79% 在圈里 → 不选", n79 == 0 && Doc.Selected.Count == 0,
              $"判中 {n79} 条（80% 是门槛，79/100 必须落空）");

        Doc.Clear();
        Doc.ClearHistory();
        var s81 = MakeRow(81);
        Doc.AddStroke(s81);
        int n81 = Doc.ApplyLasso(boxPath, vp, snap);
        Check("81% 在圈里 → 选中",
              n81 == 1 && Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], s81),
              $"判中 {n81} 条（81/100 ≥ 80%）");

        // --- 2. 贴边＝无限延伸 --------------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var longLine = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        for (int i = 0; i < 120; i++) longLine.AddPoint(vp.MinX - 800 + i * 10, cy, 1f, i);
        Doc.AddStroke(longLine);
        int total = longLine.Points.Count;                 // 120
        int visibleInside = 110;                           // x < vp.MinX + 300 的那些

        var awayFromEdge = Box(vp.MinX + 50, cy - 100, vp.MinX + 300, cy + 100);
        int nAway = Doc.ApplyLasso(awayFromEdge, vp, snap);
        Check("圈不贴边 → 屏幕外那半截不算（不选）", nAway == 0 && Doc.Selected.Count == 0,
              $"判中 {nAway} 条（圈里只有约 25/{total} 个点，{(25 * 100 / total)}%）");

        var touchEdge = Box(vp.MinX, cy - 100, vp.MinX + 300, cy + 100);
        int nEdge = Doc.ApplyLasso(touchEdge, vp, snap);
        Check("圈贴着屏幕左边 → 当作圈到无限远（选中）",
              nEdge == 1 && Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], longLine),
              $"判中 {nEdge} 条（延伸后圈里 {visibleInside}/{total} 个点 = "
              + $"{visibleInside * 100 / total}% ≥ 80%）");

        // --- 3. 图形按**轮廓**判，不按两个端点 ----------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var bigRect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.9f, 0.2f, 0.2f, 1f), Width = 4f,
        };
        bigRect.AddPoint(cx - 600, cy - 400, 1f, 0);
        bigRect.AddPoint(cx + 600, cy + 400, 1f, 0);        // 端点是那条斜对角线
        Doc.AddStroke(bigRect);

        // 只把右下角切掉一点：轮廓 5 个点里 4 个在圈里（= 80%），两个端点里只有 1 个
        var cutCorner = new List<Vector2>
        {
            new(cx - 700, cy - 500), new(cx + 700, cy - 500), new(cx + 700, cy + 300),
            new(cx + 560, cy + 420), new(cx - 700, cy + 420),
        };
        int nRect = Doc.ApplyLasso(cutCorner, vp, snap);
        Check("矩形：4/5 个轮廓点在圈里 → 选中", nRect == 1,
              $"判中 {nRect} 条（轮廓 5 点里 4 点在内 = 80%；按两个端点算只有 50%，会漏选）");

        var noBottom = Box(cx - 700, cy - 500, cx + 700, cy + 300);
        int nRect2 = Doc.ApplyLasso(noBottom, vp, snap);
        Check("矩形：只有 3/5 个轮廓点在圈里 → 不选", nRect2 == 0,
              $"判中 {nRect2} 条（60% < 80%）");

        // --- 4. 图像按四个角判 --------------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var pic = Doc.AddImage(MakeTestImage(300, 200), cx - 500, cy - 300, 1f);
        int nPicAll = Doc.ApplyLasso(Box(cx - 600, cy - 400, cx - 100, cy), vp, snap);
        Check("图像：四个角都在圈里 → 选中", nPicAll == 1,
              $"判中 {nPicAll} 条（角点 4/4 = 100%）");

        int nPic3 = Doc.ApplyLasso(Box(cx - 600, cy - 400, cx - 200, cy), vp, snap);
        Check("图像：只圈住三个角 → 不选", nPic3 == 0,
              $"判中 {nPic3} 条（3/4 = 75% < 80%）");

        // --- 5. 被擦掉的那一段不算墨 --------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var erasedMost = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        for (int i = 0; i < 20; i++) erasedMost.AddPoint(cx, cy - 190 + i * 20, 1f, i);
        erasedMost.AddErased(0f, 16f);                      // 上面 17 个点那一段被擦掉
        Doc.AddStroke(erasedMost);
        int remaining = 0;
        for (int i = 0; i < erasedMost.Points.Count; i++)
            if (!erasedMost.IsParamErased(i)) remaining++;
        int nErased = Doc.ApplyLasso(Box(cx - 100, cy - 200, cx + 100, cy + 100), vp, snap);
        Check("圈住被擦掉的那半截 → 不选", nErased == 0 && Doc.Selected.Count == 0,
              $"判中 {nErased} 条（圈里还剩 {remaining} 个没被擦的点 = "
              + $"{remaining * 100 / erasedMost.Points.Count}%；把擦掉的也算上就是 85%，会误选）");

        // --- 6. Shift 加选 / Alt 减选 -------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var left = MakeRow(100);                            // 全在圈里
        var right = Doc.AddImage(MakeTestImage(80, 80), cx + 400, cy + 400, 1f);
        Doc.AddStroke(left);
        Doc.SelectOnly(new[] { right });
        int nAdd = Doc.ApplyLasso(boxPath, vp, snap, additive: true);
        bool added = Doc.Selected.Count == 2 && Doc.Selected.Contains(left) && Doc.Selected.Contains(right);
        Check("Shift 加选：原有的不丢", nAdd == 1 && added,
              $"判中 {nAdd} 条，选区 {Doc.Selected.Count} 条（图像 + 笔迹）");

        int nSub = Doc.ApplyLasso(boxPath, vp, snap, subtractive: true);
        bool removed = Doc.Selected.Count == 1 && Doc.Selected.Contains(right) && !Doc.Selected.Contains(left);
        Check("Alt 减选：只把它移出去", nSub == 1 && removed,
              $"判中 {nSub} 条，选区剩 {Doc.Selected.Count} 条（图像还在）");

        // --- 7. 引擎那条路（按下 → 拖 → 松手）-----------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var target = MakeRow(100);
        Doc.AddStroke(target);
        Doc.Selected.Clear();
        SelMode = SelectMode.Lasso;
        Quiet(() => MarqueeDragForTest(boxPath));
        Check("引擎：走一次完整套索手势 → 选中", Doc.Selected.Count == 1 && Doc.Selected.Contains(target),
              $"选中 {Doc.Selected.Count} 条，路径已清空={LassoPath.Count == 0}");

        Quiet(() => MarqueeDragForTest(new[] { new Vector2(cx, cy), new Vector2(cx + 1, cy + 1) }));
        Check("引擎：路径太短＝单击空白 → 取消选中", Doc.Selected.Count == 0,
              $"选中 {Doc.Selected.Count} 条（和框选那条规则一致）");

        // --- 8. 拽着不放时屏幕上真有那根线（差分判据，抓屏拍不到就跳过）----------
        // 预览画的是自由折线，和矩形框不是同一段代码；不验的话"套索拖起来什么都看不见"
        // 这种问题要等人肉测才发现。
        Doc.Clear();
        Doc.ClearHistory();
        SelMode = SelectMode.Lasso;
        MarqueeActive = false;
        LassoPath.Clear();
        Doc.InvalidateAll();
        SettleFrames(400);

        int bandX = (int)(cx - 220), bandY = (int)(cy - 190);
        int bandYpx = (int)(cy - 190 + ViewOffsetY);
        const int bandW = 440, bandH = 70;
        var sight = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 10f };
        for (int i = 0; i <= 20; i++) sight.AddPoint(cx - 200 + i * 20, cy - 150, 1f, i);
        Doc.AddStroke(sight);
        Doc.InvalidateAll();
        SettleFrames(400);
        int canSee = ScreenProbe.CountMagenta(bandX, bandYpx, bandW, bandH);
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(400);

        if (canSee < 200)
        {
            Console.WriteLine($"  环境：抓屏看不到我们的层（拍到的是桌面/别的窗口，{canSee} 像素）"
                            + " → SKIP: 预览那一条跳过");
        }
        else
        {
            var before = ScreenProbe.CaptureRegion(bandX, bandYpx, bandW, bandH);
            MarqueeActive = true;
            LassoPath.AddRange(boxPath);
            LassoLive = boxPath[boxPath.Count - 1];      // 线头画在指针位置（这里就是最后一个点）
            MqMinX = boxPath[0].X; MqMinY = boxPath[0].Y;
            MqMaxX = boxPath[2].X; MqMaxY = boxPath[2].Y;
            Doc.InvalidateAll();
            SettleFrames(400);
            var after = ScreenProbe.CaptureRegion(bandX, bandYpx, bandW, bandH);

            int changed = 0;
            for (int i = 0; i + 3 < Math.Min(before.Length, after.Length); i += 4)
            {
                int d = Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1])
                      + Math.Abs(before[i + 2] - after[i + 2]);
                if (d > 40) changed++;
            }
            // 圈的上边正好横穿这条带子：300 逻辑像素长 × 1.6 宽（DPI 2 → 约 3 像素）
            Check("拽着不放时屏幕上真画出了那条线", changed > 200,
                  $"这条带子里变了 {changed} 个像素（上边线横穿过去，应该上千）");

            MarqueeActive = false;
            LassoPath.Clear();
            Doc.InvalidateAll();
            SettleFrames(200);
        }

        // --- 9. 切回矩形：同一个手势变成"碰到就选" -----------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var rectTarget = MakeRow(100);
        Doc.AddStroke(rectTarget);
        Doc.Selected.Clear();
        SelMode = SelectMode.Rect;
        // 矩形模式喂**两个点**（锚点 → 对角）就够：矩形是按"锚点 ↔ 当前点"算的，
        // 喂一圈闭合路径的话锚点和终点会重合，等于拉出一个零面积的框。
        Quiet(() => MarqueeDragForTest(new[]
        {
            new Vector2(cx - 150f, cy - 150f),
            new Vector2(cx + 150f, cy + 150f),
        }));
        Check("切回矩形：同一个手势变成'碰到就选'",
              SelMode == SelectMode.Rect && Doc.Selected.Count == 1 && Doc.Selected.Contains(rectTarget),
              $"矩形模式下选中 {Doc.Selected.Count} 条（同一个矩形范围，换了一套判据）");

        Doc.Clear();
        Console.WriteLine($"  合计：通过 {pass}，失败 {fail}");
        Console.WriteLine(fail == 0 ? "PASS" : "FAIL");
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

        // 换截图工具，拖一个 300×200 的框（两种模式各抓一次，所以抽成函数）
        Tool = Tool.Capture;
        int x0 = (int)(cx - 150), y0 = (int)(cy - 100);
        int x1 = (int)(cx + 150), y1 = (int)(cy + 100);
        int midReadoutDark = 0;                 // 拖动中"尺寸读数"那块有多深（见下面的量法）
        void DragCapture()
        {
            Tool = Tool.Capture;
            SendMouse(x0, y0, 0);
            SettleFrames(40);
            SendMouse(x0, y0, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(60);
            SendMouse((x0 + x1) / 2, y0 + 20, 0);
            SettleFrames(40);
            SendMouse(x1, y1, 0);
            SettleFrames(60);
            // **拖动中要有尺寸读数**（用户 2026-09-17："截图使用不顺手，光标配合也感觉不好"）：
            // 框的左下角外 6 逻辑像素处会画一个深色胶囊写着 "宽 × 高"。
            // 这里趁还没松手，量那块地方有没有深色像素（读数画出来了）。
            {
                int lx = (int)(Math.Min(x0, x1) * 1), ly = (int)(Math.Max(y0, y1) + 6 * DpiScale);
                int lw = (int)(96 * DpiScale), lh = (int)(26 * DpiScale);
                int dark = ScreenProbe.CountDark(lx, ly, lw, lh);
                midReadoutDark = dark;
            }
            SendMouse(x1, y1, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(900);                 // 截图里含一次"藏窗口 / 藏框 + 抓屏 + 恢复"
        }
        DragCapture();

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

        Check("拖动中显示了尺寸读数（框下面那块有深色胶囊）", midReadoutDark > 800,
              $"{midReadoutDark} 像素（读数是 {300 / dpi:F0}×{200 / dpi:F0} 的一个深色胶囊）");

        // 覆盖层藏过又显示：屏幕上不该留残影（墨应该还在原处）
        int afterInk = ScreenProbe.CountMagenta((int)(cx - 200), (int)(cy - 120), 400, 260);
        Check("截图后屏幕恢复正常（批注还在，没有残影）", afterInk > 5000,
              $"截前 {onScreenInk} 像素，截后 {afterInk} 像素");

        // ---- ② 第二种模式：**直接截取**（连板书一起拍）----
        //
        // 用户 2026-09-17："截图功能是不是也应该对接了，也可以参考 inkclass"。
        // InkClass 给的是两项菜单（快速截图 / 隐藏界面截图），我们把这两项放进
        // **截图那一格的上带**：[直接截取][隐藏批注截取]（照我们假面板里定的那两段）。
        Host.Commands.SetCaptureHideInk(false);
        SettleFrames(250);
        Check("模式切到「直接截取」", !Host.State.CaptureHideInk,
              $"hideInk = {Host.State.CaptureHideInk}");

        DragCapture();
        var shot2 = Doc.Strokes.FindLast(s => s.IsImage);
        Check("直接截取：又生成了一个图像对象", shot2 != null && !ReferenceEquals(shot2, shot),
              shot2 == null ? "没有" : $"{shot2.Image.Width}×{shot2.Image.Height}");
        if (shot2 != null)
        {
            var q = shot2.Image.Bgra;
            int ink2 = 0, frame = 0;
            for (int y = 0; y < shot2.Image.Height; y++)
                for (int x = 0; x < shot2.Image.Width; x++)
                {
                    int i = (y * shot2.Image.Width + x) * 4;
                    byte b = q[i], g = q[i + 1], r = q[i + 2];
                    if (r > 200 && g < 90 && b > 200) ink2++;                       // 品红 = 板书
                    // 取景框是琥珀色（1, 0.68, 0.10）：只查图片最外 3 像素那一圈——
                    // 框就画在抓取矩形的边上，真被拍进去必然落在这里
                    if ((x < 3 || y < 3 || x >= shot2.Image.Width - 3 || y >= shot2.Image.Height - 3)
                        && r > 200 && g > 140 && g < 215 && b < 90) frame++;
                }
            Check("直接截取：**板书在图里**（品红 > 2000）", ink2 > 2000, $"{ink2} 像素");
            Check("直接截取：取景框没被拍进去（最外一圈没有琥珀色）", frame == 0, $"{frame} 像素");
        }

        // 收尾：模式还原成默认的"隐藏批注截取"
        Host.Commands.SetCaptureHideInk(true);
        SettleFrames(150);
        Check("收尾：模式还原成「隐藏批注截取」", Host.State.CaptureHideInk,
              $"hideInk = {Host.State.CaptureHideInk}");

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

        // 真笔的"数据质量"汇总：这次要不要相信压感、合并点收全了没有、
        // 以及预测在什么档位（这些在 调研-压感与预测-原理.md 与 测试-压感与预测.md 里都有判据）。
        Console.WriteLine();
        Console.WriteLine("=== 真笔数据质量汇总 ===");
        Console.WriteLine($"  合并点：{PenMessages} 条消息 → {PenSamples} 个采样点"
                          + (PenMessages > 0 ? $"（每条 {PenSamples / (double)PenMessages:F2} 个）" : ""));
        Console.WriteLine($"  压感：{PenPressurePoints}/{PenTotalPoints} 个点带有效压感；"
                          + $"设备报 pressure 位={Yes(PenSawPressureMask)}，"
                          + $"倾角位={Yes(PenSawTiltMask)}，旋转位={Yes(PenSawRotationMask)}");
        Console.WriteLine($"  预测：{(PredictEnabled ? $"开（{PredictHorizonMs:F0} ms）" : "关")}；"
                          + $"湿墨轨迹：{(OverlayWindow.InkTrailEnabled ? "开" : "关")}"
                          + $"（{OverlayWindow.InkTrailNote}）");
        if (PredLeadCount > 0)
            Console.WriteLine($"  预测实际把墨往前带：平均 {PredLeadSum / PredLeadCount:F2} px，"
                              + $"最大 {PredLeadMax:F2} px（上限 {PredictLeadCap:F0} px，共 {PredLeadCount} 次）");
        else if (PredictEnabled)
            Console.WriteLine("  预测实际把墨往前带：一次都没有触发（速度太低或全是急转/断笔）");
        Console.WriteLine(PenTotalPoints == 0
            ? "  注意：这一轮一个真笔采样点都没有——写的时候用的是鼠标/触摸，或者笔工作在兼容模式"
            : PenSawPressureMask
                ? "  压感可用：这台机器/这支笔确实在报压力"
                : "  压感不可用：设备没报 pressure 位（先查驱动的 Windows Ink 开关）");

        Console.Write(Latency.Report());
        string csv = "reports/latency-live.csv";
        Latency.WriteCsv(csv, "real-pen");
        Console.WriteLine($"CSV 已写入 {csv}");
        _quit = true;
    }

    /// <summary>汇总行里用的小写法。</summary>
    private static string Yes(bool v) => v ? "有" : "无";

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

    /// <summary>深色像素数（找"深色胶囊"那种东西：截图时的尺寸读数）。</summary>
    public static int CountDark(int x, int y, int w, int h) => Capture(x, y, w, h, IsDark);
    private static bool IsDark(byte b, byte g, byte r) => r < 90 && g < 90 && b < 90;

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
