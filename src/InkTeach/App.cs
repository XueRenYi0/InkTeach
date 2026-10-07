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
internal sealed partial class App : InkEngine.InkEngine
{
    private IntPtr _clickTargetHwnd;
    private string _clickLogFile;
    private bool _panelShowMore;
    private bool _panelShowMini;
    private bool _panelShowBand;
    private bool _selfCheckMode;

    /// <summary>
    /// `--doc <文件>`：启动后打开这份文档（图片 / PDF）。null = 没给。
    /// 解析在 <see cref="PrepareHostStartup"/>；真正的"打开"在窗口建好之后的正常启动分支里做
    /// （S4/S6 接线——现在先只记住并校验）。
    /// </summary>
    private string _startupDocPath;

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

        // **识别诊断日志**（见 `Engine.InkLogEnabled`）：`--reclog` 或环境变量
        // `INKTEACH_RECLOG=1`。放在 `PrepareHostStartup` 里，所有模式（含各种自检）都吃得到。
        if (args.Contains("--reclog") || Environment.GetEnvironmentVariable("INKTEACH_RECLOG") == "1")
            InkLogEnabled = true;

        // **录墨迹**（`--recink <文件>`）：见 `InkRecordPath`。用户 2026-09-26 提的做法 ——
        // "我手画多少条双曲线给你，你根据这些来定制判据"：**真手画的样本**比任何合成语料都值钱。
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--recink")
            {
                InkRecordPath = System.IO.Path.GetFullPath(args[i + 1]);
                Console.WriteLine($"[录墨迹] 每一笔都会追加写进：{InkRecordPath}");
                if (args.Contains("--recinkp"))
                {
                    InkRecordWithPressure = true;
                    Console.WriteLine("           （--recinkp：多录 压力/来源/当时平板坐标）");
                }
            }

        // **打开文档**（`--doc <文件>`）：图片 / PDF。实测与开发期直达用。
        // 只是"记住路径"：真正打开在窗口建好之后（文档模式要有覆盖层才能画页）。
        // 文件不存在 → 只提示、当没给（绝不因此影响启动——规矩三：失败当没有）。
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--doc")
            {
                var p = System.IO.Path.GetFullPath(args[i + 1]);
                if (System.IO.File.Exists(p))
                {
                    _startupDocPath = p;
                    Console.WriteLine($"[文档] 启动后打开：{p}");
                }
                else
                {
                    Console.WriteLine($"[文档] 找不到文件，已忽略：{p}");
                }
            }

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
        // PPT 批注缓存同理：假源用的键是"假演示文稿"，之前没隔离时历次自检会把它
        // **永久写进用户真目录**（还会跨次累积、把计数类断言撑坏——2026-10-01 抓到）。
        // 自检每次启动先清空这份临时缓存；--ppttest 会在运行时再指向自己的目录。
        if (_selfCheckMode && PptStore.RootOverride == null)
        {
            PptStore.RootOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-ppt");
            try { if (Directory.Exists(PptStore.RootOverride)) Directory.Delete(PptStore.RootOverride, true); } catch { }
        }
        // 文档批注缓存同理：自检每次启动先清空这份临时缓存（和 PptStore 一样，别碰用户真目录）
        if (_selfCheckMode && DocStore.RootOverride == null)
        {
            DocStore.RootOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-doc");
            try { if (Directory.Exists(DocStore.RootOverride)) Directory.Delete(DocStore.RootOverride, true); } catch { }
        }
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
            // 基准的两个开关（2026-10-07）：`--benchpressure` 让笔画带压感，
            // `--benchpts N` 指定每笔点数——老基准每笔只 19.5 点，比真实写法轻近 9 倍，
            // 不问这两个问题，"一万笔吃多少"答不准。
            _benchPressure = args.Contains("--benchpressure");
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--benchpts" && int.TryParse(args[i + 1], out int bp))
                    _benchPts = Math.Clamp(bp, 2, 4000);
                if (args[i] == "--benchspread" && int.TryParse(args[i + 1], out int bs))
                    _benchSpread = Math.Clamp(bs, 1, 60);
            }
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
        else if (mode == "--radialtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RadialTest();
        }
        else if (mode == "--radialshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RadialShow(args.Length > 1 ? args[1] : "reports/radial-palette.bmp");
        }
        else if (mode == "--shapebandtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeBandTest();
        }
        else if (mode == "--inktest")
        {
            // **纯几何，不建窗口、不碰界面**：识别器是离线慢路径，
            // 它该验的是"判据对不对"，不该混进界面/输入那套。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = RecoProbe.Run();
            _quit = true;
        }
        else if (mode == "--inkfile")
        {
            // **读用户手画的样本**（`--recink` 录出来的那份）：见 `RecoProbe.RunInkFile`。
            // 纯离线：不建窗口、不碰界面。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = RecoProbe.RunInkFile(args.Length > 1 ? args[1] : "reports/我的双曲线.txt");
            _quit = true;
        }
        else if (mode == "--dwelltest")
        {
            // 手势那一套**必须真跑**（起笔 / 采样 / 轮询 / 抬手都走引擎里真在用的函数），
            // 所以它和 `--inktest` 不同：这条要在活着的引擎里跑。时钟是推的，不 sleep。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = DwellProbe.Run(this);
            _quit = true;
        }
        else if (mode == "--librarytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 第二个参数给了路径就顺手出一张面板的图（和 --shapetoolshow 一个用法）
            LibraryTest(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--dashtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DashTest();
        }
        else if (mode == "--axistest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AxisTest();
        }
        else if (mode == "--curvetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CurveTest();
        }
        else if (mode == "--prismtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrismTest();
        }
        else if (mode == "--prismshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrismShowcase(args.Length > 1 ? args[1] : "reports/棱柱.bmp");
        }
        else if (mode == "--axisshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AxisShowcase(args.Length > 1 ? args[1] : "reports/坐标系与数轴.bmp");
        }
        else if (mode == "--curveshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CurveShowcase(args.Length > 1 ? args[1] : "reports/四种曲线.bmp");
        }
        else if (mode == "--conicshow")
        {
            // 2026-09-22：双曲线两档（有 / 无渐近线）＋ 椭圆两档（有 / 无焦点三角形）
            // 摆成一张四格的图——这一批要看的正是"两档之间的差别"。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ConicShowcase(args.Length > 1 ? args[1] : "reports/圆锥曲线-两档.bmp");
        }
        else if (mode == "--panelshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 可选的两个参数（出"改之前 / 改之后"的对照图专用，产品里都不会传）：
            //   第 2 个 ＝ 描边型外库图标强制线宽（不传就按 IconAtlas.StrokedIcons 那一份）；
            //   第 3 个 ＝ 激光笔那一格强制成哪个档号（不传就用产品那支）。
            if (args.Length > 2 && float.TryParse(args[2], out var forcedStroke))
                InkUi.IconAtlas.DevForceStroke = forcedStroke;
            if (args.Length > 3 && int.TryParse(args[3], out var laserVariant))
                InkUi.IconAtlas.DevLaserVariant = laserVariant;
            PanelShow(args.Length > 1 ? args[1] : "reports/panel-第一版.png");
        }
        else if (mode == "--demogif")
        {
            // 演示连拍（写字 → 停顿变图形 → 选中拖动 → 截图取景）：帧存成 <目录>\NNNN.bmp，
            // 之后用 tools\make-demo-gif.ps1 拼成 README 头部那张 GIF。只在开发机上跑。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DemoGif(args.Length > 1 ? args[1] : "tmp/demo");
        }
        else if (mode == "--makeicon")
        {
            // 生成程序图标（用界面自己的渲染画 <see cref="AppIconUi"/> 那张：白砖＋大笔），见 MakeIcon。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            MakeIcon(args.Length > 1 ? args[1] : "assets/InkTeach.ico");
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
        else if (mode == "--shapeiconshow")
        {
            // 图形面板那些自绘图标的**对照表**（含每一档的变体），见 ShapeIconSheet。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeIconShow(args.Length > 1 ? args[1] : "reports/shape-icons.bmp");
        }
        else if (mode == "--captureicons")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureIconShow(args.Length > 1 ? args[1] : "reports/capture-icons.bmp");
        }
        else if (mode == "--toolicons")
        {
            // 2026-09-26：白板 / 激光笔的图标候选（用户："白板：感觉不像"、
            // "激光笔：我喜欢那种'笔射出一道光'的形态"），见 ToolIconSheet。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ToolIconShow(args.Length > 1 ? args[1] : "reports/tool-icons.bmp");
        }
        // 2026-09-27：上游那一批"带笔的"图标（笔 / 铅笔 / 荧光笔 / 书法笔 / 画笔…），
        // 第 2 个参数给了东西就在每支笔上加一副"三道杠"（＝当激光笔用的样子）。
        // 数据来自 PenIcons（tmp/gen-pen-icons.ps1 拉的），**产品图标表没动**。
        else if (mode == "--penshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUi(new PenSheet());
            BoardOn = true;                      // 白板打底：图里没有桌面上的杂东西
            SettleFrames(400);
            string path = args.Length > 1 ? args[1] : "reports/pen-candidates.png";
            if (!OffscreenShot(path)) Console.WriteLine("出图失败");
            _quit = true;
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
        else if (mode == "--dynerasertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DynEraserTest();
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
        else if (mode == "--prefetchtest")
        {
            // 分块空闲预取自检：滚两格 → 预取把视口外一圈烘好 → 再滚一格不重画；
            // 自带"关预取就重画"的对照（自证有效）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrefetchTest();
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
        else if (mode == "--shapetooltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeToolTest();
        }
        else if (mode == "--shapetoolshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // `--circle / --ellipse / --triangle / --parallelogram / --rectangle / --parabola /
            //  --cuboid` 选图形（不给就是直线）；`--snap` = 那一张要拍"吸附生效中"的帧；
            // `--angles` = 内角 / 夹角那一组读数；`--pose` = 拖旋转柄时的**姿态角**读数。
            // 后两个（抛物线 / 长方体）是 2026-09-20 加的：它们代表"**没有特殊手柄**"的那一类，
            // 要拍的是"通用八手柄 ＋ 旋转柄那根线挂哪儿"（用户："抛物线的旋转手柄线连的地方很奇怪"）。
            string kind = args.Contains("--circle") ? "circle"
                        : args.Contains("--ellipse") ? "ellipse"
                        : args.Contains("--triangle") ? "triangle"
                        : args.Contains("--parallelogram") ? "parallelogram"
                        : args.Contains("--parabola") ? "parabola"
                        : args.Contains("--cuboid") ? "cuboid"
                        : args.Contains("--rectangle") ? "rectangle" : "line";
            ShapeToolShowcase(args.Length > 1 ? args[1] : "reports/shape-line-handles.bmp",
                              kind, args.Contains("--drag"), args.Contains("--draw"),
                              args.Contains("--rotate"), args.Contains("--rotated"),
                              args.Contains("--snap"), args.Contains("--angles"),
                              args.Contains("--pose"), args.Contains("--autosel"));
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
        else if (mode == "--hotkeytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HotkeyTest();
        }
        else if (mode == "--touchtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TouchTest();
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
        else if (mode == "--wintabprobe")
        {
            // 探 Wintab（数位板驱动的原生 API）这条路通不通。
            // 只读、不改产品行为；见 Engine.WintabProbe 的说明。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = WintabProbe();
            _quit = true;
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
            SelShowcase(args.Length > 1 ? args[1] : null);
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
        else if (mode == "--gclatencytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            GcLatencyTest();
        }
        else if (mode == "--touchguardtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TouchGuardTest();
        }
        // [停用 2026-10-05] 预测尾/预测算法自检（老预测系统停用，见 已停用-渲染实验.md）
        // else if (mode == "--predicttailtest")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     PredictTailTest();
        // }
        // else if (mode == "--predicttest")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     PredictorTest();
        // }
        else if (mode == "--smoothtest")
        {
            // **纯算法，不建窗口**（和 `--inktest` 同一个口径）：曲线器是离线几何，
            // 它该验的是"直角变没变形 / 圆有没有更圆"，不该混进界面/输入那一套。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = SmoothProbe.Run();
            _quit = true;
        }
        // [停用 2026-10-05] M3 弹簧专项自检（模式已停用，见 已停用-渲染实验.md）
        // else if (mode == "--inkmodeltest")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     ExitCode = InkModelProbe.Run();
        //     _quit = true;
        // }
        else if (mode == "--motiontest")
        {
            // 对照台总自检：M0/M1/M2/M3/M4/M5 同一批语料出表。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = MotionProbe.Run();
            _quit = true;
        }
        else if (mode == "--smoothshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SmoothShowcase(args.Length > 1 ? args[1] : "reports/曲线化-对照.bmp");
        }
        else if (mode == "--smoothflashtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SmoothFlashTest(!args.Contains("--off"), args.Contains("--fast"));
        }
        else if (mode == "--prevflash")
        {
            // 诊断：**"写下一笔时，上一笔闪不闪"**（用户 2026-10-04 报）。
            // --pen = 走合成笔（PT_PEN + 压感，覆盖 ID2D1Ink 那条真实渲染路）；
            // --left = 用户的复现场景：横线底纹白板 + 屏幕左侧竖写。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrevFlashTest(args.Contains("--pen"), args.Contains("--left"),
                          args.Contains("--ui"), args.Contains("--fast"));
        }
        else if (mode == "--scrollflash")
        {
            // 诊断：**"滚轮滚动之后一按鼠标就闪/错位"**（用户 2026-10-04 报，
            // 关键线索：滚动之后、按下才闪，松手就不闪；以前竖写时也遇到过）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollFlashTest();
        }
        else if (mode == "--wetdrytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 20;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--live" && double.TryParse(args[i + 1], out var s)) secs = s;
            WetDryTest(args.Contains("--live"), secs);
        }
        else if (mode == "--updatetest")
        {
            // **纯离线**（解析 / 版本比较 / sha256 / 本地取清单 / 换壳脚本语法），不碰网络
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = UpdateProbe.Run();
            _quit = true;
        }
        else if (mode == "--updatecheck")
        {
            // **验收用（会真的换壳！）**：`--updatecheck [清单地址] [--apply]`
            //   不给地址 = 用配置里的更新源；`--apply` = 查到新版本就自动下载安装。
            // 它是"在真机上验一遍自动更新"的入口（教室里也能用这条命令验）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string u = (args.Length > 1 && !args[1].StartsWith("--")) ? args[1] : null;
            if (u != null) UpdateFeed.Url = u;
            AutoApplyUpdate = args.Contains("--apply");
            AutoCheckOnly = !AutoApplyUpdate;      // 只查不装 → 查完自己退（别把界面挂在屏幕上）
            Console.WriteLine($"=== 自动更新验收：源={(UpdateFeed.Url.Length > 0 ? UpdateFeed.Url : "（未配置）")}"
                              + $"，apply={AutoApplyUpdate} ===");
            CheckUpdateFromUi();
        }
        // [停用 2026-10-05] 预测尾"突突跳"检测（老预测系统停用，见 已停用-渲染实验.md）
        // else if (mode == "--tailjumptest")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     TailJumpTest(args.Contains("--noisy"), args.Contains("--off"));
        // }
        else if (mode == "--pressurediag")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PressureDiagTest();
        }
        else if (mode == "--pressuretest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PressureTest();
        }
        else if (mode == "--inkfiletest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InkFileTest();
        }
        else if (mode == "--replaytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ReplayTest();
        }
        else if (mode == "--timertest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TimerTest();
        }
        else if (mode == "--rolltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RollTest();
        }
        else if (mode == "--replayshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ReplayShow(args.Length > 1 ? args[1] : "reports/replay-bar.bmp");
        }
        else if (mode == "--ballprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BallProbe(args.Length > 1 ? args[1] : "reports/ball");
        }
        // [停用 2026-10-05] 真实笔迹数据预测评测（老预测系统停用，见 已停用-渲染实验.md）
        // else if (mode == "--predictdata")
        // {
        //     _autoExitAt = double.MaxValue;
        //     _nextLogAt = double.MaxValue;
        //     PredictEval.Run(args.Length > 1 ? args[1] : "tmp/datasets");
        //     _quit = true;
        // }
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
        else if (mode == "--ppttest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PptTest();
        }
        else if (mode == "--pptshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PptShow(args.Length > 1 ? args[1] : null);
        }
        else if (mode == "--pptprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 12;
            if (args.Length > 1 && double.TryParse(args[1], out var ps)) secs = ps;
            PptProbe(secs);
        }
        else if (mode == "--pagetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PageTest();
        }
        else if (mode == "--doctest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DocumentTest();
        }
        else if (mode == "--pdfprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int pn = args.Length > 2 && int.TryParse(args[2], out var ptmp) ? ptmp : 3;
            PdfProbe(args.Length > 1 ? args[1] : null, pn);
        }
        else if (mode == "--docstates")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DocStatesProbe(args.Length > 1 ? args[1] : null);
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
        else if (mode == "--lasertest")
        {
            // 激光笔自检（2026-09-27）：拖尾寿命（写过整条不缩、松手停 2 秒再淡）、
            // 多条并存、粗细真的管用、不进文档。顺带出一张图（摆样要看，自检判不了好不好看）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LaserTest();
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
            //
            // ⚠ **但要把"自检那套隔离"撤掉**（2026-10-07 修）。
            // 上面一进来就 `_selfCheckMode = mode.Length > 0`，于是**任何一个非空的第一参数**
            // （包括 `--nowintab` / `--clean 3` / `--recink …` 这些**开关**）
            // 都会进"半个自检状态"：换临时设置、换存档路径、关掉导出对话框 ——
            // **而用户完全看不出来**（他只会觉得"怎么和平时不一样"）。
            //
            // 判据改成结构性的：**只有当某个 mode 分支真的认领了它，才算自检模式**。
            // 走到这个兜底分支 = 没人认领 = 当成正常启动。
            // （以前靠一张"这些开关不是 mode"的清单来挡 —— 那种清单一定会漏，今天漏了三次。）
            _selfCheckMode = false;
            SelfCheckMode = false;                 // 引擎那一份也撤掉，免得日志里谎报"自检"
            InkSettings.PathOverride = null;
            Recovery.AutoSavePathOverride = null;
            PptStore.RootOverride = null;
            DocStore.RootOverride = null;
            ExportDialogEnabled = true;

            // `--doc <文件>`：到这儿窗口/视口都就绪了，真正打开它
            // （失败只提示——规矩三：失败当没有，不影响启动）
            if (_startupDocPath != null)
            {
                var err = OpenDocuments(new[] { _startupDocPath });
                if (err != null) Console.WriteLine($"[文档] 打开失败：{err}");
            }
            return -1;
        }

        Loop();
        Shutdown();
        return 0;
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
        Console.WriteLine("  --radialtest        呼出盘自检（Ctrl+Alt+Shift+Q：开 / 划 / 松 / 取消 / 穿透 / 松键轮询）");
        Console.WriteLine("  --radialshow [图]   呼出盘摆样：定格在屏幕中央出图（默认 reports/radial-palette.bmp）");
        Console.WriteLine("  --shapeiconshow [路径] 出图：图形面板图标的对照表（含每一档的变体）");
        Console.WriteLine("  --toolicons [路径]  出图：白板 / 激光笔的图标候选（未选中 / 选中 / 放大三格）");
        Console.WriteLine("  --penshow [路径] [bars]  出图：上游那批「带笔的」图标；带第 2 个参数就每格加一副「三道杠」");
        Console.WriteLine("  --shapebandtest     图形那格的界面入口自检（七段 / 三个新热键 / 主条图标跟着变）");
        Console.WriteLine("  --inktest           **墨迹识别的单独测试**（日常改识别只跑这一条：准确率 / 误报 / 难例 / 写回）");
        Console.WriteLine("  --dwelltest         停顿成型的手势自检（真入口 + 推时钟：触发 / 定型 / 撤销回手绘 / 不留墨）");
        Console.WriteLine("  --dashtest          线型自检（实线/虚线/点线上屏墨量、面板那一行、存档往返）");
        Console.WriteLine("  --axistest          坐标系/数轴自检（画法 / 四个手柄 / 网格 / 上屏 / 存档往返）");
        Console.WriteLine("  --axisshow [路径]   出图：坐标系（带网格/不带）+ 数轴 + 一条虚线");
        Console.WriteLine("  --curvetest         四种曲线自检（画法 / 紧框 / 手柄精简 / 朝向 / 存档往返）");
        Console.WriteLine("  --curveshow [路径]  出图：抛物线四种开口 + 双曲线两个方向 + 正弦/余弦各一周期");
        Console.WriteLine("  --conicshow [路径]  出图：双曲线两档（有/无渐近线）+ 椭圆两档（有/无焦点三角形）");
        Console.WriteLine("                      （第三行顺带出**旋转体那一族**：圆柱 / 圆锥 / 圆台 / 球）");
        Console.WriteLine("  --pagetest          整屏翻页自检（一屏 = 一页：页高 = 视口高、只动相机、到顶就停）");
        Console.WriteLine("  --ppttest           PPT 模式自检（假页码源：隔离 / 页内滚动 / 清空只清本页 / 退出写盘 / 再进读回）");
        Console.WriteLine("  --pptprobe [秒]     PPT 连接探针（真机跑：打开 PPT 按 F5，看连不连得上）");
        Console.WriteLine("                     加 --pptcmd 会实际按 Next/Prev/GotoSlide 验证命令；--pptdebug 打 ROT 细节");
        Console.WriteLine("  --iotest [路径]     导出自检（选中 → PNG 透明底 / JPEG 白底；给路径就保留文件）");
        Console.WriteLine("  --patterntest       白板底纹自检（方格/横线/间距 + 数屏幕上的线 + 重铺代价）");
        Console.WriteLine("  --lasertest         激光笔自检（拖尾一直写不缩 / 松手停 2 秒再整体淡出 / 多条并存 /");
        Console.WriteLine("                      粗细真的管用 / 不是笔迹）；顺带出图 reports/laser-trail.png");
        Console.WriteLine("  --pageshow <图>     整屏翻页摆样（相机停在两屏之间 / 正好对齐，各出一张）");
        Console.WriteLine("  --panelshow <图> [--band] [--mini] [--more [--page N]] [--cell N] [--shape 名字] [--zoom N]   界面出图（离屏；--more --page 0=启动器 1=设置）");
        Console.WriteLine("  --demogif <目录>    演示连拍（写字→停顿变图形→选中拖动→截图取景，存 NNNN.bmp 帧）");
        Console.WriteLine("  --makeicon <图.ico>      用界面自己的渲染生成程序图标（线条笔＋白砖＋带笔锋的红笔迹）");
        Console.WriteLine("  --captureshow <图>  截图取景框 + 尺寸读数出图（离屏）");
        Console.WriteLine("  --dialogprobe <前缀> [--save]  导出对话框探针（真弹框 + 点它的下拉 + 连拍三张；");
        Console.WriteLine("                     --save 连「保存」一起点，验到落盘为止）");
        Console.WriteLine("  --erasertest        橡皮擦正确性");
        Console.WriteLine("  --pixelerasetest    像素橡皮正确性（切成两段 / 框里无墨 / 一步撤销）");
        Console.WriteLine("  --dynerasertest     动态橡皮（曲线：死区到 0.8 / 慢=基准1.0不缩 / 快封顶 2.5；窗口抗抖；慢扫 vs 快扫；框跟速度；严丝合缝）");
        Console.WriteLine("  --eraserhud         橡皮读数浮层（左下角实时 速度/目标/当前系数/尺寸；调门槛用，不进界面）");
        Console.WriteLine("  --pixeleraseshow    像素橡皮摆样（擦之前/之后各存一张图，自己抓屏）");
        Console.WriteLine("  --eraserlab [前缀]  橡皮手测台：铺样例 + 记录每条拖拽，给人用鼠标测（不自动退出）");
        Console.WriteLine("  --imagetest         图像对象（上屏 / 复制翻转 / 存档 / 剪贴板）");
        Console.WriteLine("  --clipboardtest     剪贴板对象通道（复制 → 粘回来仍是对象；会覆盖系统剪贴板）");
        Console.WriteLine("  --lassotest         套索选择（80% 判据 / 贴边无限延伸 / 加选减选）");
        Console.WriteLine("  --capturetest       截图（拖框 → 左上角 → 剪贴板，且不拍进自己的批注）");
        Console.WriteLine("  --cursorshow <笔|荧光笔|激光笔|橡皮|像素橡皮> [宽]  落点摆样");
        Console.WriteLine("  --widthtest         笔迹粗细/压力");
        Console.WriteLine("  --ghosttest         残影检测");
        Console.WriteLine("  --tiletest          分块缓存自检（边界无缝 / 回程复用 / 内存上界）");
        Console.WriteLine("  --prefetchtest      分块空闲预取自检（预取命中则再滚一格不重画；含关预取对照）");
        Console.WriteLine("  --noprefetch        关掉分块空闲预取（对照；默认开）");
        Console.WriteLine("  --trailtest         委托墨迹轨迹对照");
        Console.WriteLine("  --smoothtest        中心线曲线化自检（过点 Catmull-Rom：直角不变形 / 圆弧更圆滑 / 形状不跑）");
        Console.WriteLine("  --smoothshow [图]   出图：曲线化开/关对照（同一组样本各存一张 -off / -on，32 位 BMP）");
        Console.WriteLine("  --smoothflashtest [--off]  “画的时候闪不闪”专项检测（合成鼠标画过去，看已经画过的墨还动不动）");
        Console.WriteLine("  --prevflash [--pen] [--left]  “写下一笔时，上一笔闪不闪”专项检测（合成鼠标/合成笔；--left=左侧竖写+横线底纹）");
        Console.WriteLine("  --motion <名字>     catmull / mean2（**默认 mean2**=距离窗＋过点曲线＋收笔追赶）");
        Console.WriteLine("  --motiontest        运动模型自检（baseline / catmull / mean2 同批语料出表）");
        Console.WriteLine("  --himetric          D1 亚像素输入（用 ptHimetricLocation 映射小数像素；默认关，做 A/B）");
        Console.WriteLine("  模型调参：--mean2win 画布像素 / --smoothcorner N 角点阈值");
        Console.WriteLine("  [已停用] 预测、拟合(--mean2fit)、模拟压力(--simpressure/--pfpressure)、笔锋");
        Console.WriteLine("           (--simtaper/--flicktip)、对照模式(raw/sliding/spring/oneeuro/mean/gauss)等：");
        Console.WriteLine("           见 已停用-渲染实验.md（代码保留）");
        Console.WriteLine("  --wetdrytest [--live 20] 湿墨/干墨交接测量（**要真笔**：在中间那条浅灰线间画一笔）");
        Console.WriteLine("  --updatetest        自动更新自检（离线：解析 / 版本比较 / sha256 / 下载候选 / 换壳脚本沙箱真跑）");
        Console.WriteLine("  --updatecheck [清单地址] [--apply]  自动更新验收（**会真的换壳**：--apply = 查到就装）");
        Console.WriteLine("  --touchguardtest    触摸自检（合成触摸：PT_TOUCH 通路 + 第二根手指不许抢笔）");
        Console.WriteLine("  --touchtest         触摸手势自检（单指写 / 双指漫游翻页 / 三指擦 / 长按选 / 选中变换 / 总开关）");
        Console.WriteLine("  --notouch           触摸手势总开关**临时关掉**（保险丝 / A-B 对照；不写偏好）");
        Console.WriteLine("  --touchhud          触点诊断浮层（触点数 / 接触面积 / 最近的输入设备）");
        Console.WriteLine("  --gclatencytest     书写期间 GC 低延迟档自检（真进得去 / 无第 2 代回收 / 超时退得回）"
                          + "；--nogclatency 对照、--gchold N 调保持毫秒");
        Console.WriteLine("  --pressurediag      压感采集诊断（合成笔注入：合并点、压感有效位、压力分布）");
        Console.WriteLine("  --pressuretest      压感自检（映射函数 / 上屏粗细随压力变 / 无压感与虚线回退 / 开关往返 / 存档往返）");
        Console.WriteLine("  --inkfiletest       墨迹文件自检（保存/打开往返 / 打开前备份与轮转 / 坏文件不动文档 / 放映中置灰）");
        Console.WriteLine("  --replaytest        墨迹回放自检（时间轴 / 前缀 / 暂停 / 倍速 / 只读 / 控制条 / 翻页退出）");
        Console.WriteLine("  --timertest         课堂计时器自检（功能卡：设置态↔运行态 / ⚙ / ±1分 / 到点超时 / 提示音 / 穿透小窗）");
        Console.WriteLine("  --rolltest          课堂点名自检（功能卡：设置↔结果 / 滚动定格 / 去重池子 / 名单 / 穿透小窗）");
        Console.WriteLine("  --replayshow <图>   墨迹回放摆样（铺几笔 → 播到一半 → 截控制条那一块）");
        Console.WriteLine("  --ballprobe [前缀]  收起球贴边诊断（左/右/四角 × 显示/隐藏，逐个出图）");
        Console.WriteLine("  --wetinktest        湿墨轨迹实测（只让系统画，数上屏像素：这条通道到底画不画）");
        Console.WriteLine("  --syswet            真笔 + 实线笔：湿墨只让系统轨迹画（⚠ 本机实测系统轨迹不渲染，仅留作换机验证）");
        // [停用] Console.WriteLine("  --predictdata [路径] 真实笔迹数据上的预测评测（UCI Character Trajectories）");
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

    private void PptShow(string path)
    {
        if (path == null)
        {
            Console.WriteLine("  用法：--pptshow <图.bmp> [--panel] [--menu] [--ink] [--wide] [--pass]");
            _quit = true;
            return;
        }

        // 颜色与投影是界面推给引擎的（见 IUiHost.SetFloatingTheme）——
        // 不挂界面拿到的是 UiTheme.Default 那套兜底，出的图就不是产品里的样子。
        SetUiFactory(() => new InkUi.FullUi());

        var fake = new PptFakeSource { Showing = true, Slide = 3, SlideId = 258, Total = 12, Key = "摆样" };
        AttachPptSource(fake, watch: false);
        var argv = Environment.GetCommandLineArgs();

        ResetPptBarPosForTest();            // 从默认位置（左下角）起，出的图才是"第一眼"的样子
        StepPpt();
        SettleFrames(150);
        // 引导那一行字**每次进放映都会出现**（2026-09-27 起），而它和菜单长在同一处、
        // 会互相压着看不清——所以除了专门出它的 `--hint`，其余出图一律把它按下去。
        // ⚠ 必须压在 `StepPpt()` **之后**：进放映那一刻它才被点起来，压早了没用。
        if (!argv.Contains("--hint")) PptHintSuppressForTest();

        // `--ink`：给当前页铺三笔（一长一短一点）——「回放本页墨迹」那一项
        // 亮不亮、右侧"N 笔"对不对，出图时一眼就能看到。
        if (argv.Contains("--ink"))
        {
            var s1 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.90f, 0.20f, 0.20f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s1.AddPoint(300f + 320f * k / 4f, 300f + 40f * k, 0.3f + 0.1f * k, k * 200f);
            Doc.AddStroke(s1);
            var s2 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.13f, 0.45f, 0.90f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s2.AddPoint(320f + 280f * k / 4f, 560f - 30f * k, 0.4f + 0.1f * k, 1200f + k * 180f);
            Doc.AddStroke(s2);
            var dot = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.15f, 0.65f, 0.35f, 1f), Width = 8f,
            };
            dot.AddPoint(780f, 420f, 0.8f, 2200f);
            Doc.AddStroke(dot);
            SettleFrames(120);
        }

        // `--pass`：诊断用——打开穿透，让"接输入小窗"铺上（代码注释说它是 1/255 的灰、
        // 肉眼看不见；这个开关就是拿来核对"到底看不看得见"的）。
        if (argv.Contains("--pass"))
        {
            SetPassThroughFromUi(true);
            SettleFrames(150);
        }

        var bar = PptBarRect();
        float midX = (bar.MinX + bar.MaxX) * 0.5f;
        float midY = (bar.MinY + bar.MaxY) * 0.5f;

        if (argv.Contains("--menu"))
        {
            PptBarPointerDown(midX, midY);      // 2026-10-02 第五轮：点页码 = 菜单
            PptBarPointerUp(midX, midY);
        }
        else if (argv.Contains("--panel"))
        {
            PptBarPointerDown(midX, midY);      // 点页码 → 菜单
            PptBarPointerUp(midX, midY);
            PptMenuItemRectAt(0, out var miJump);
            PptBarPointerDown((miJump.MinX + miJump.MaxX) * 0.5f,   // 菜单第一项 → 页号面板
                              (miJump.MinY + miJump.MaxY) * 0.5f);
        }
        else
        {
            // 悬停在**页码格**上：它才是条上的主角（点它出菜单），顺便把悬停高亮出进图里
            // ——用户 2026-09-26 指出过"页码那里没有悬停指示"，这条图就是给那件事看的。
            PptBarPointerMove(midX, midY);
        }
        SettleFrames(200);

        // 截图范围 = 这一刻要拍的东西（条 ∪ 面板 ∪ 菜单 ∪ 首次引导）
        var box = PptBarRect();
        if (PptPagePanelOpen) { PptPanelRect(out var p); box.Add(p.MinX, p.MinY); box.Add(p.MaxX, p.MaxY); }
        if (PptMenuOpen || PptHintVisible) { PptMenuRect(out var m); box.Add(m.MinX, m.MinY); box.Add(m.MaxX, m.MaxY); }
        float pad = argv.Contains("--wide") ? 160f : 24f;   // --wide：出图带上一圈周围，查"条外面有什么"
        int sx = (int)Math.Max(_virtualX, box.MinX - pad);
        int sy = (int)Math.Max(_virtualY, box.MinY - pad);
        int sw = (int)Math.Min(_virtualX + _virtualW - sx, box.MaxX - box.MinX + pad * 2);
        int sh = (int)Math.Min(_virtualY + _virtualH - sy, box.MaxY - box.MinY + pad * 2);
        bool ok = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
        Console.WriteLine(ok ? $"  已保存 {path}（{sw}×{sh}）" : "  截屏失败");
        _quit = true;
    }

    /// <summary>
    /// `--pptprobe [秒]`：**真机连接探针**（用户在自己的机器上跑一次就知道通不通）。
    ///
    /// 为什么只能这样验：我这边没有 PowerPoint / WPS 环境，产品那条 COM 连接
    /// 没法在自检里跑（自检也不该去碰它——`StartPptLink` 在自检模式下直接返回）。
    /// 这条命令每秒问一次"现在连上谁了、在不在放映、第几页"，把答案打出来。
    ///
    /// 用法（老师的机器上）：
    ///   ① 跑 `InkTeach.exe --pptprobe 20`；
    ///   ② 趁这 20 秒打开一个 PPT，按 F5 放映、翻两页（WPS 演示也一样试一次）；
    ///   ③ 看输出：放映中应当从 False 变 True、页码跟着翻页变、SlideID 是个大整数。
    /// 三条都对 = 连接层在这台机器上没问题（WPS 也照这条走，它就是给国产办公套件铺的路）。
    /// </summary>
    private void PptProbe(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== PPT 连接探针 ===");
        Console.WriteLine($"  接下来 {seconds:F0} 秒里，请去打开一个 PPT 并按 F5 放映、翻两页；");
        Console.WriteLine("  WPS 演示也一样试一次（这条探针就是用来分清哪家的 Office 能连上的）。");
        Console.WriteLine();

        // `--pptcmd`：连接上之后真的按一下 Next / Prev / GotoSlide，看页码跟不跟着动
        //（这条路径走的是 IDispatch::Invoke，和只读属性不是同一条调用）。
        bool cmdTest = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--pptcmd");
        bool cmdDone = !cmdTest;

        var src = new PptComSource();
        int connectedSamples = 0, showingSamples = 0;
        double t0 = NowMs;
        string lastLine = "";
        while (NowMs - t0 < seconds * 1000)
        {
            var s = src.Poll();
            if (s.Connected) connectedSamples++;
            if (s.Showing) showingSamples++;
            string line = $"  连上={s.Connected}  放映中={s.Showing}  第 {s.Slide}/{s.Total} 页  "
                        + $"SlideID={s.SlideId}  标识={s.Key}";
            if (line != lastLine) { Console.WriteLine(line); lastLine = line; }

            if (!cmdDone && s.Showing && NowMs - t0 > 1500)
            {
                // 命令后 PowerPoint 会忙一下，属性可能短暂读失败——轮询重试到恢复为止。
                PptSnapshot WaitShow(int ms)
                {
                    var end = NowMs + ms; var last = default(PptSnapshot);
                    while (NowMs < end) { last = src.Poll(); if (last.Showing) return last; SettleFrames(100); }
                    return last;
                }
                PptSnapshot WaitSlide(int want, int ms)
                {
                    var end = NowMs + ms; var last = default(PptSnapshot);
                    while (NowMs < end) { last = src.Poll(); if (last.Showing && last.Slide == want) return last; SettleFrames(100); }
                    return last;
                }

                int before = s.Slide;

                // ① GotoSlide：动画页也不影响，立刻能验。
                src.GotoSlide(before + 1);
                var sG1 = WaitSlide(before + 1, 2500);
                src.GotoSlide(before);
                var sG0 = WaitSlide(before, 2500);
                bool okGoto = sG1.Showing && sG1.Slide == before + 1 && sG0.Showing && sG0.Slide == before;

                // ② Next：**动画页上要按好几下才翻页**（每一下是一个动画步），
                //    循环到页码变化为止，不是"按一下就必须翻"。
                int nextPresses = 0;
                var sNext = sG0;
                while (nextPresses < 6 && (!sNext.Showing || sNext.Slide <= before))
                { src.Next(); nextPresses++; sNext = WaitShow(1200); }
                bool okNext = sNext.Showing && sNext.Slide > before;

                // ③ Prev：同样循环回到原页；最后 Goto 对齐，防停在动画中间。
                int prevPresses = 0;
                var sPrev = sNext;
                while (prevPresses < 8 && (!sPrev.Showing || sPrev.Slide > before))
                { src.Prev(); prevPresses++; sPrev = WaitShow(1200); }
                src.GotoSlide(before); WaitShow(1500);
                bool okPrev = sPrev.Showing && sPrev.Slide <= before;

                Console.WriteLine($"  命令自检: Goto {before}->{before + 1}->{before} {(okGoto ? "PASS" : "FAIL")}; "
                                + $"Next x{nextPresses}->{sNext.Slide} {(okNext ? "PASS" : "FAIL")}; "
                                + $"Prev x{prevPresses}->{sPrev.Slide} {(okPrev ? "PASS" : "FAIL")}");
                cmdDone = true;
            }

            SettleFrames(200);
        }

        Console.WriteLine();
        if (cmdTest && !cmdDone)
            Console.WriteLine("  命令自检：没跑（探针期间没读到放映状态）");
        Console.WriteLine(showingSamples > 0
            ? $"  PASS：读到过放映状态（{showingSamples} 次采样在放映中）——连接层在这台机器上可用"
            : connectedSamples > 0
                ? $"  半通：连上了演示文稿（{connectedSamples} 次采样），但没读到放映状态——请确认按了 F5 全屏放映"
                : "  不通：一次都没连上（没装桌面 Office / WPS、没有打开的演示文稿，或 COM 被安全软件挡住）");
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
        // `--adjust`：出"松开后调整"那一版（8.3.0：8 个手柄 + ✓/✕ 两颗按钮）
        CaptureAdjusting = Environment.GetCommandLineArgs().Contains("--adjust");
        // `--ready`：出"刚进屋"那一版（8.3.1：整屏灰 + 顶部提示 + 右上角「✕ 取消」，还没有框）
        bool ready = Environment.GetCommandLineArgs().Contains("--ready");
        if (ready) CapMinX = CapMaxX = CapMinY = CapMaxY = cx;

        // 出图范围 = 框 + 四周一圈（遮罩/准线/读数/按钮都可能有）：整块视口太大，
        // 给"框 + 120 逻辑像素"就够（看图看的是那几个控件的排版）。
        var r = new RectF { MinX = CapMinX, MinY = CapMinY, MaxX = CapMaxX, MaxY = CapMaxY };
        // ⚠ `--ready` 那两件东西（顶部提示、右上角「✕」）都贴在**屏幕上沿**，
        //   "点 + 120"根本拍不到（2026-10-03 修：以前只出一块 240×240 的灰）。
        //   改成**屏幕上沿一条通栏**——两件都在里面。
        if (ready)
            r = new RectF
            {
                MinX = VirtualScreen.MinX, MinY = VirtualScreen.MinY,
                MaxX = VirtualScreen.MaxX, MaxY = VirtualScreen.MinY + 360f * DpiScale,
            };
        if (!OffscreenFloatingShot(path, ready ? r : r.Inflate(120f * DpiScale))) Console.WriteLine("出图失败");
        CaptureActive = false;
        CaptureAdjusting = false;
        _quit = true;
    }

    /// <summary>
    /// `--demogif &lt;目录&gt;`：录一段**演示连拍**（写字 → 停顿变图形 → 选中拖动 → 截图取景）。
    ///
    /// 为什么要有它：README 头部那张动图没法"离屏摆拍"——它要的正是**真实交互过程**
    /// （合成鼠标走真实输入通路、停顿变形走真实定时器、截图取景走真冻结）。所以这里
    /// 和自检同一套路：白板打底（不透明，画面里没有桌面杂物）+ 合成输入 + 约 110ms 截一帧。
    /// 帧存成 `&lt;目录&gt;\0000.bmp…`，之后用 `tools\make-demo-gif.ps1` 拼成 GIF。
    /// 只在开发机上跑，不进产品、也不进默认自检套件。
    /// </summary>
    private void DemoGif(string dir)
    {
        SetUiFactory(() => new InkUi.FullUi());
        Tool = Tool.Pen;
        BoardOn = true;
        Host.Commands.SetBoard(true);              // 引擎侧真开板：截屏里是干净白底
        SettleFrames(700);

        Directory.CreateDirectory(dir);
        int frame = 0;
        // 截屏范围（物理像素）：动作区 + 底部工具带（1600×1420）。
        int fx = _virtualX + 850, fy = _virtualY + 380, fw = 1600, fh = 1420;
        void Snap() => ScreenProbe.SaveBmp(
            Path.Combine(dir, frame++.ToString("0000") + ".bmp"), fx, fy, fw, fh);

        // 一边按 `t∈[0,1]` 推进动作，一边约 120ms 截一帧；结束再补一张。
        void Animate(int ms, Action<float> at)
        {
            var sw = Stopwatch.StartNew();
            // ⚠ 别写 `long.MinValue`：`el - last` 会溢出成负数，条件永远不成立
            //   （第一版就是这么只拍了 10 张——每个阶段结束一张，中间一张没有）。
            long last = -10000;
            for (;;)
            {
                long el = sw.ElapsedMilliseconds;
                at?.Invoke(ms <= 0 ? 1f : Math.Min(1f, el / (float)ms));
                PumpMessages();
                StepCameraAnim();
                RenderAll();
                if (el - last >= 120) { Snap(); last = el; }
                if (el >= ms) break;
                Thread.Sleep(10);
            }
            Snap();
        }

        // ── ① 用笔画一个"手画圆"（带手抖；3.0 秒走完）────────────────────────
        float cx = _virtualX + 1560, cy = _virtualY + 900, r = 250;
        const int N = 64;
        var pts = new Vector2[N + 1];
        for (int i = 0; i <= N; i++)
        {
            float a = -MathF.PI / 2f + i / (float)N * MathF.PI * 2f;
            float rr = r * (1f + 0.045f * MathF.Sin(i * 1.9f));   // 手抖：别是完美圆
            pts[i] = new Vector2(cx + rr * MathF.Cos(a), cy + rr * MathF.Sin(a));
        }
        SendMouse((int)pts[0].X, (int)pts[0].Y, 0);
        SettleFrames(150); Snap();
        SendMouse((int)pts[0].X, (int)pts[0].Y, Native.MOUSEEVENTF_LEFTDOWN);
        Animate(2800, t => SendMouse((int)pts[Math.Min(N, (int)(t * N))].X,
                                     (int)pts[Math.Min(N, (int)(t * N))].Y, 0));

        // ── ② 停住不动：400ms 后"停顿变图形"（圆）＋ 松手自动选中 ────────────
        Animate(950, _ => SendMouse((int)pts[N].X, (int)pts[N].Y, 0));
        SendMouse((int)pts[N].X, (int)pts[N].Y, Native.MOUSEEVENTF_LEFTUP);
        Animate(900, _ => { });                    // 定型 + 自动选中（收起成一颗圆钮）

        // 点一下那颗圆钮：把操作条摊开（图里才有完整的十格）。
        {
            var dot = SelectionHandles.BarCollapsedRect(
                SelectionHandles.FrameOf(Doc.Selected).CanvasAabb, DpiScale, ViewportCanvas);
            float dx = (dot.MinX + dot.MaxX) * 0.5f, dy = (dot.MinY + dot.MaxY) * 0.5f;
            SendMouse((int)dx, (int)dy, 0);
            SettleFrames(90); Snap();
            SendMouse((int)dx, (int)dy, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(90);
            SendMouse((int)dx, (int)dy, Native.MOUSEEVENTF_LEFTUP);
            Animate(650, _ => { });                // 摊开 + 看一眼
        }

        // ── ③ 按住框内拖动（在框里按下 = 拖整体，一步撤销那种）──────────────
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(140); Snap();
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        Animate(1100, t => SendMouse((int)(cx + t * 330f), (int)(cy + t * 180f), 0));
        SendMouse((int)(cx + 330f), (int)(cy + 180f), Native.MOUSEEVENTF_LEFTUP);
        Animate(700, _ => { });

        // ── ④ 截图取景：整屏压暗 → 拖出取景框 → 进调整态（8 手柄 + ✓/✕）─────
        CaptureHideInk = false;                    // 连批注一起冻：冻出来的底就是我们的板书
        BeginCaptureMode();
        SettleFrames(220); Snap();
        CapMinX = CapMaxX = cx - 240; CapMinY = CapMaxY = cy - 140;
        Animate(1000, t =>
        {
            CapMaxX = cx - 240 + t * 720f;
            CapMaxY = cy - 140 + t * 430f;
        });
        CaptureAdjusting = true;
        Animate(1500, _ => { });

        Console.WriteLine($"[演示连拍] {frame} 帧 → {dir}");
        ExitCode = 0;
        _quit = true;
    }
    private void SelShowcase(string path = null)
    {
        BoardOn = true;                 // 白底，不然手柄压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();

        // **必须挂上界面**：操作条/小面板的颜色与投影是界面推给引擎的
        // （见 IUiHost.SetFloatingTheme）——不挂界面拿到的是 UiTheme.Default 那套兜底，
        // 出的图就不是产品里看到的样子。
        // 注意顺序：**偏好要先设**，SetUiFactory 是立刻挂载的（挂载时就会读偏好）。
        // --dark：深色那档也出一张（浮层的深色是这一轮新加的）
        if (Environment.GetCommandLineArgs().Contains("--dark")) SetUiPref("dark", "1");
        SetUiFactory(() => new InkUi.FullUi());

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

        // 2026-09-30（8.2.0）：出图也把**面板**带上——
        //   --ink    开墨迹面板（颜色/粗细/线型，含新的连续滑条）
        //   --layer  开层级面板
        //   --custom 再叠上自定义取色板（色相条 + 饱和度/明度方块）
        // 排版好不好看只能靠图看，所以这些"一张图里要有面板"的对照必须有出口。
        //
        // ⚠ 工具必须是**框选**：操作条/面板由 `SelectionBarShown` 把守
        //（只有框选 / 图形工具才有点得到的那一块）。忘了这一句时出图里只有框和手柄，
        // 看图的还以为"面板没开"——出图这条路第一版就踩了这个。
        var cmdArgs = Environment.GetCommandLineArgs();
        Tool = Tool.Marquee;
        SelBarCollapsed = false;
        bool wantInk = cmdArgs.Contains("--ink"), wantLayer = cmdArgs.Contains("--layer");
        bool wantCustom = cmdArgs.Contains("--custom");
        SelPanelOpen = (wantInk || wantCustom) ? SelPanel.Ink : wantLayer ? SelPanel.Layer : SelPanel.None;

        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Doc.InvalidateAll();
        // 色板放在**选中摆好之后**开：起点色取的是"选中墨迹"，先开就退成手里那支笔的默认色
        // （第一版出图就踩了这个：选中那条黑墨，色板却停在红色上）。
        if (wantCustom) OpenCustomColorForTest();

        // `--dragmid`：把粗细滑条**按在中间不松手**——给"拖动中显示数值"出对照图用。
        // 真机上它靠指针消息驱动，出图这条路没有指针，只能借自检那两个钩子把它摆出来。
        if (cmdArgs.Contains("--dragmid"))
        {
            var sl = SelectionHandles.SliderRect(SelectionHandles.FrameOf(Doc.Selected).CanvasAabb,
                                                  DpiScale, ViewportCanvas, SelectionHandles.SwatchCount);
            float mx = SelectionHandles.SliderXOfT(0.62f, SelectionHandles.FrameOf(Doc.Selected).CanvasAabb,
                                                   DpiScale, ViewportCanvas, SelectionHandles.SwatchCount);
            SelectionGestureForTest(mx, (sl.MinY + sl.MaxY) * 0.5f);
            PanelDragMoveForTest(mx + 2f, (sl.MinY + sl.MaxY) * 0.5f);
        }
        SettleFrames(800);

        // 量尺：把三处浮层的实际尺寸/间距打出来（不凭感觉调，前后两版都跑同一条）。
        PrintOverlayMetrics();

        if (path == null)
        {
            Console.WriteLine("选中框已摆好，等外部截图（这个模式不会自己退出）");
            return;
        }

        // 出图（离屏，锁屏/远程也能出）：范围 = 选中框 ∪ 操作条 ∪（开着的）面板，再留一圈给投影。
        // 操作条挂在框下方（SelectionHandles.BarRect 算的就是那个位置），
        // 所以把它一起并进来，否则出图会把操作条切掉一半。
        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        var bar = SelectionHandles.BarRect(aabb, DpiScale, ViewportCanvas);
        var region = new RectF
        {
            MinX = MathF.Min(aabb.MinX, bar.MinX), MinY = MathF.Min(aabb.MinY, bar.MinY),
            MaxX = MathF.Max(aabb.MaxX, bar.MaxX), MaxY = MathF.Max(aabb.MaxY, bar.MaxY),
        };
        if (SelPanelOpen == SelPanel.Ink)
        {
            region.Add(SelectionHandles
                .PanelRect(aabb, DpiScale, ViewportCanvas, SelectionHandles.SwatchCount));
            if (CustomColorOpen)
                region.Add(SelectionHandles.CustomPanelRect(aabb, DpiScale, ViewportCanvas,
                                                           SelectionHandles.SwatchCount));
        }
        else if (SelPanelOpen == SelPanel.Layer)
        {
            region.Add(SelectionHandles.LayerPanelRect(aabb, DpiScale, ViewportCanvas));
        }
        if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");
        _quit = true;
    }

    /// <summary>
    /// **浮层量尺**（8.2.0）：把三处浮层（操作条 / 墨迹面板 / 层级面板）的实际尺寸和
    /// 内边距 / 格间距 / 分组缝按**几何读出来**打成一张表。
    ///
    /// 为什么要有它（用户 2026-09-30："先用 --uitest 把三处浮层的实际尺寸/间距量出来，
    /// 不凭感觉调"）：收紧要收在哪儿、收了多少，得先有前后两版的同一把尺子。
    /// 它走的是**绘制/命中同一份几何函数**，所以量出来的就是屏幕上真正画出来的那个数，
    /// 不是另抄一份"设计稿数字"。
    ///
    /// 单位一律逻辑像素（dpi 传 1、visible 传空 = 不夹取，量的是纯公式）。
    /// </summary>
    private void PrintOverlayMetrics()
    {
        const float dpi = 1f;
        var sel = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        var none = RectF.Empty;
        int sc = SelectionHandles.SwatchCount;

        var bar = SelectionHandles.BarRect(sel, dpi, none);
        var b0 = SelectionHandles.BarButtonRect(0, sel, dpi, none);
        var b1 = SelectionHandles.BarButtonRect(1, sel, dpi, none);
        float barPad = b0.MinX - bar.MinX;
        float barGap = b1.MinX - b0.MaxX;
        float barH = bar.MaxY - bar.MinY;
        float barRadius = MathF.Min(FloatingTheme.CornerRadius, barH * 0.5f);

        var p = SelectionHandles.PanelRect(sel, dpi, none, sc);
        var s0 = SelectionHandles.SwatchRect(0, sel, dpi, none, sc);
        var s1 = SelectionHandles.SwatchRect(1, sel, dpi, none, sc);
        var sRow = SelectionHandles.SwatchRect(SelectionHandles.SwatchColumns, sel, dpi, none, sc);
        var c0 = SelectionHandles.StyleCellRect(0, sel, dpi, none, sc);
        var c1 = SelectionHandles.StyleCellRect(1, sel, dpi, none, sc);

        var lp = SelectionHandles.LayerPanelRect(sel, dpi, none);
        var l0 = SelectionHandles.LayerCellRect(0, sel, dpi, none);
        var l1 = SelectionHandles.LayerCellRect(1, sel, dpi, none);

        Console.WriteLine();
        Console.WriteLine("=== 浮层量尺（逻辑像素；dpi=1、不夹取）===");
        Console.WriteLine($"  操作条     {bar.MaxX - bar.MinX:F0} × {barH:F0}   两端内边距 {barPad:F1}   "
                          + $"按钮 {b0.MaxX - b0.MinX:F0}（方格子）格缝 {barGap:F1}  "
                          + $"悬停底 {SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}×"
                          + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}  "
                          + $"圆角 {barRadius:F0}");
        Console.WriteLine($"  墨迹面板   {p.MaxX - p.MinX:F0} × {p.MaxY - p.MinY:F0}   内边距 {s0.MinX - p.MinX:F1}   "
                          + $"色片 {s0.MaxX - s0.MinX:F0} 缝 {s1.MinX - s0.MaxX:F1} 行缝 {sRow.MinY - s0.MaxY:F1}");
        Console.WriteLine($"            线型格 {c0.MaxX - c0.MinX:F1}×{c0.MaxY - c0.MinY:F0}、格缝 {c1.MinX - c0.MaxX:F1}，"
                          + $"滑条行 {SelectionHandles.SliderRowLogical:F0} / 线型行 {SelectionHandles.StyleRowLogical:F0}，"
                          + $"面板离条 {SelectionHandles.PanelGapLogical:F0}");
        Console.WriteLine($"  层级面板   {lp.MaxX - lp.MinX:F0} × {lp.MaxY - lp.MinY:F0}   内边距 {l0.MinX - lp.MinX:F1}   "
                          + $"格 {l0.MaxX - l0.MinX:F0} 缝 {l1.MinX - l0.MaxX:F1}");
        var pk = SelectionHandles.CustomPanelRect(sel, dpi, none, sc);
        var psv = SelectionHandles.PickSvRect(sel, dpi, none, sc);
        var phue = SelectionHandles.PickHueRect(sel, dpi, none, sc);
        Console.WriteLine($"  取色板     {pk.MaxX - pk.MinX:F0} × {pk.MaxY - pk.MinY:F0}   内边距 {psv.MinX - pk.MinX:F1}   "
                          + $"SV {psv.MaxX - psv.MinX:F0} 色相条 {phue.MaxX - phue.MinX:F0}  格缝 {phue.MinX - psv.MaxX:F1}");
        Console.WriteLine();
    }

    /// <summary>
    /// **浮层尺寸 token 自检**（8.2.0 验收①）：三处浮层（操作条 / 墨迹面板 / 层级面板 / 取色板）
    /// 的内边距、格间距、格与缝必须**从同一套 token 读出来**。
    ///
    /// 判据是"几何读出来的数 == token 算出来的数"（容差 0.01）——哪天有人绕过 token
    /// 顺手写一个 10f，这里当场变红。`--selftest` 和 `--uitest` 共用同一份（计划里写的是
    /// 两条自检，两边跑到的就是同一个它）。
    ///
    /// <paramref name="check"/> 是调用方自己的红绿回调（两边的输出格式不一样）。
    /// </summary>
    private void CheckFloatOverlayTokens(Action<string, bool, string> check)
    {
        float dpi = DpiScale;
        const float tol = 0.01f;
        var sel = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        int sc = SelectionHandles.SwatchCount;

        var bar = SelectionHandles.BarRect(sel, dpi, RectF.Empty);
        var btn0 = SelectionHandles.BarButtonRect(0, sel, dpi, RectF.Empty);
        var btn1 = SelectionHandles.BarButtonRect(1, sel, dpi, RectF.Empty);
        float barPad = (btn0.MinX - bar.MinX) / dpi;
        float barGap = (btn1.MinX - btn0.MaxX) / dpi;
        float barW = (bar.MaxX - bar.MinX) / dpi;
        float barWTok = SelectionHandles.FloatPadLogical * 2
                      + SelectionHandles.BarButtonCount * SelectionHandles.BarButtonWidthLogical
                      + (SelectionHandles.BarButtonCount - 1) * SelectionHandles.BarGapLogical;
        bool barOk = Near(barPad, SelectionHandles.FloatPadLogical, tol)
                  && Near(barGap, SelectionHandles.BarGapLogical, tol)
                  && Near(barW, barWTok, tol)
                  && Near((bar.MaxY - bar.MinY) / dpi, SelectionHandles.BarHeightLogical, tol);

        var p = SelectionHandles.PanelRect(sel, dpi, RectF.Empty, sc);
        var s0 = SelectionHandles.SwatchRect(0, sel, dpi, RectF.Empty, sc);
        var s1 = SelectionHandles.SwatchRect(1, sel, dpi, RectF.Empty, sc);
        var sRow = SelectionHandles.SwatchRect(SelectionHandles.SwatchColumns, sel, dpi, RectF.Empty, sc);
        var c0 = SelectionHandles.StyleCellRect(0, sel, dpi, RectF.Empty, sc);
        var c1 = SelectionHandles.StyleCellRect(1, sel, dpi, RectF.Empty, sc);
        int rows = (sc + SelectionHandles.SwatchColumns - 1) / SelectionHandles.SwatchColumns;
        float panelWTok = SelectionHandles.FloatPadLogical * 2
                        + SelectionHandles.SwatchColumns * SelectionHandles.SwatchSizeLogical
                        + (SelectionHandles.SwatchColumns - 1) * SelectionHandles.FloatGapLogical;
        float panelHTok = SelectionHandles.FloatPadLogical * 2
                        + SelectionHandles.SliderRowLogical + SelectionHandles.StyleRowLogical
                        + rows * SelectionHandles.SwatchSizeLogical + (rows - 1) * SelectionHandles.FloatGapLogical;
        bool panelOk = Near((s0.MinX - p.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                    && Near((s1.MinX - s0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((sRow.MinY - s0.MaxY) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((s0.MaxX - s0.MinX) / dpi, SelectionHandles.SwatchSizeLogical, tol)
                    && Near((c1.MinX - c0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((c0.MaxY - c0.MinY) / dpi, SelectionHandles.StyleRowLogical, tol)
                    && Near((p.MaxX - p.MinX) / dpi, panelWTok, tol)
                    && Near((p.MaxY - p.MinY) / dpi, panelHTok, tol);

        // 8.2.1：悬停 / 激活的底必须是**正方形**（用户点名的那条："悬停是长方形不是正方形，
        // 所以分散"）——逐格核对"宽 = 高 = 格宽 − 2×inset"，并检查它居中。
        bool chipOk = true; string chipNote = "";
        for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
        {
            var chip = SelectionHandles.BarHoverChip(i, sel, dpi, RectF.Empty);
            var btnI = SelectionHandles.BarButtonRect(i, sel, dpi, RectF.Empty);
            float cw = (chip.MaxX - chip.MinX) / dpi, chh = (chip.MaxY - chip.MinY) / dpi;
            float want = SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2;
            if (MathF.Abs(cw - chh) > tol || MathF.Abs(cw - want) > tol
                || MathF.Abs((chip.MinX + chip.MaxX) * 0.5f - (btnI.MinX + btnI.MaxX) * 0.5f) > tol * dpi)
            {
                chipOk = false;
                chipNote = $"第 {i} 格底 {cw:F1}×{chh:F1}（该 {want:F0}×{want:F0} 且居中）";
                break;
            }
        }

        var lp = SelectionHandles.LayerPanelRect(sel, dpi, RectF.Empty);
        var l0 = SelectionHandles.LayerCellRect(0, sel, dpi, RectF.Empty);
        var l1 = SelectionHandles.LayerCellRect(1, sel, dpi, RectF.Empty);
        bool layerOk = Near((l0.MinX - lp.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                    && Near((l1.MinX - l0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((l0.MaxX - l0.MinX) / dpi, SelectionHandles.LayerCellLogical, tol);

        var pk = SelectionHandles.CustomPanelRect(sel, dpi, RectF.Empty, sc);
        var psv = SelectionHandles.PickSvRect(sel, dpi, RectF.Empty, sc);
        var phue = SelectionHandles.PickHueRect(sel, dpi, RectF.Empty, sc);
        bool pickOk = Near((psv.MinX - pk.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                   && Near((phue.MinX - psv.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                   && Near((psv.MaxX - psv.MinX) / dpi, SelectionHandles.PickSvLogical, tol)
                   && Near((phue.MaxX - phue.MinX) / dpi, SelectionHandles.PickHueLogical, tol);

        // 圆角也是 token：三处浮层都读界面推上来的主题（= InkUi.Tokens.FloatingCorner）。
        bool cornerOk = Near(FloatingTheme.CornerRadius, InkUi.Tokens.FloatingCorner, tol);

        check("浮层尺寸：内边距/格间距/格尺寸/圆角都在 token 上（操作条）", barOk,
              $"内边距 {barPad:F1}、格缝 {barGap:F1}、整条 {barW:F0}×{(bar.MaxY - bar.MinY) / dpi:F0}");
        check("浮层尺寸：操作条悬停/激活底是正方形（8.2.1，用户点名）", chipOk,
              chipOk
                  ? $"{SelectionHandles.BarButtonCount} 格逐格核对："
                    + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}×"
                    + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}、居中"
                  : chipNote);
        check("浮层尺寸：墨迹面板在 token 上（含行缝与整卡宽高）", panelOk,
              $"内边距 {(s0.MinX - p.MinX) / dpi:F1}、色片缝 {(s1.MinX - s0.MaxX) / dpi:F1}、"
              + $"行缝 {(sRow.MinY - s0.MaxY) / dpi:F1}、卡片 {(p.MaxX - p.MinX) / dpi:F0}×{(p.MaxY - p.MinY) / dpi:F0}");
        check("浮层尺寸：层级面板在 token 上", layerOk,
              $"内边距 {(l0.MinX - lp.MinX) / dpi:F1}、格缝 {(l1.MinX - l0.MaxX) / dpi:F1}");
        check("浮层尺寸：自定义取色板在 token 上", pickOk,
              $"内边距 {(psv.MinX - pk.MinX) / dpi:F1}、SV {(psv.MaxX - psv.MinX) / dpi:F0}、"
              + $"色相条 {(phue.MaxX - phue.MinX) / dpi:F0}");
        check("浮层圆角：走界面主题令牌（三处一个数）", cornerOk,
              $"主题 {FloatingTheme.CornerRadius:F0} vs InkUi.Tokens.FloatingCorner {InkUi.Tokens.FloatingCorner:F0}");
    }

    private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
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

        // **多段图形**（长方体 12 条棱、圆柱 5 笔）——2026-09-20 修的那两类：
        // 擦一刀之后外形逐笔不变：被挡的棱 / 被挡的半圈熔完**还是细虚线**，
        // 段与段之间也**不会连出假线**。都摆在橡皮的竖直带里，好看出"确实切了一刀"。
        var cub = new Stroke
        {
            Tool = Tool.Cuboid, Kind = StrokeKind.Cuboid,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * dpi,
        };
        cub.SetCuboidFront(cx - 40, cy + 40, cx + 180, cy + 240);
        cub.SetCuboidDepth(cx + 20, cy - 20);         // 深度 = |40 − (−20)| = 60
        Doc.AddStroke(cub);

        var cyl = new Stroke
        {
            Tool = Tool.Cylinder, Kind = StrokeKind.Cylinder,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * dpi,
        };
        cyl.SetSolidBox(cx - 320, cy - 300, cx - 80, cy + 60);   // 右边那条母线压在橡皮里
        Doc.AddStroke(cyl);

        Tool = Tool.PixelEraser;
        PassThrough = false;
        ShowHud = false;
        PointerInside = true;
        LastPointerType = Native.PT_MOUSE;
        PointerX = cx; PointerY = cy;
        Doc.InvalidateAll();
        SettleFrames(600);

        int w = (int)(PixelEraserWidthLogical * dpi * 4.6f);
        int h = (int)(PixelEraserHeightLogical * dpi * 3.6f);
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
                        + "（三条横墨 + 荧光笔各切成两截；矩形熔成笔迹后再切；"
                        + "长方体、圆柱熔成**一条棱一段**——虚线棱 / 虚线半圈还是虚线，"
                        + "段与段之间没有假线）");
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
    private void ShapeToolShowcase(string path, string kind, bool drag, bool drawing,
                                   bool rotate, bool rotated, bool snap,
                                   bool angleRead, bool poseRead, bool autoSel)
    {
        BoardOn = true;                       // 白底：手柄压在桌面上看不清
        SetUiFactory(() => new InkUi.FullUi());   // 浮层配色/投影由界面推上来
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        // ---- `--autosel`：**画完自动选中那一帧**（2026-09-22）----
        //
        // 真机拖一个矩形、松手，拍"刚画完"的样子。要看的是两件事：
        //   ① 框在（框 ＋ 八个手柄 ＋ 旋转柄）；
        //   ② **下面没有操作条**——图形工具下那一条不画（见 Engine.SelectionBarShown），
        //      因为它正好挂在图形正下方，会吃掉"接着画下一个"的那一下。
        // 出图范围因此刻意**往下多留一段**：不往下留的话，"有没有条"这件事看不出来。
        if (autoSel)
        {
            SetToolFromUi(Tool.Rectangle);
            var aFrom = new Vector2(cx - 260f, cy - 170f);
            var aTo = new Vector2(cx + 260f, cy + 170f);
            SendMouse((int)aFrom.X, (int)aFrom.Y, 0);                            SettleFrames(60);
            SendMouse((int)aFrom.X, (int)aFrom.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(aFrom.X + (aTo.X - aFrom.X) * i / 4f),
                          (int)(aFrom.Y + (aTo.Y - aFrom.Y) * i / 4f), 0);
                SettleFrames(40);
            }
            SendMouse((int)aTo.X, (int)aTo.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(400);                       // 松手 → 自动选中，等它画完

            Console.WriteLine($"画完自动选中：对象 {Doc.Strokes.Count} 个，"
                            + $"选中 {Doc.Selected.Count} 个，工具还是 {Host.State.Tool}，"
                            + $"操作条画不画 = {SelectionBarShown}");

            var sel = SelectionHandles.FrameOf(Doc.Selected);
            var shot = sel.CanvasAabb;
            // 旋转柄在框上方外侧，单独并进来（它不在 aabb 里）。
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, sel, DpiScale);
            shot.Add(grip.X - 40f, grip.Y - 40f);
            shot.Add(grip.X + 40f, grip.Y + 40f);
            // **往下多留 120 逻辑像素**：那正是"操作条本该出现的地方"
            //（`SelectionHandles.BarOffsetLogical` 起算），留出来才看得出它不在。
            shot.Add(shot.MinX, shot.MaxY + 120f * DpiScale);
            if (!OffscreenFloatingShot(path, shot.Inflate(30f))) Console.WriteLine("出图失败");
            _quit = true;
            return;
        }

        // ---- 第③轮（读数）那几张 ----
        // `--pose`：拖旋转柄 → 姿态角读数（矩形停在吸住的 **0°**、椭圆停在 **90°**）。
        if (poseRead)
        {
            bool isRect = kind != "ellipse";
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 起手都是"歪的"：矩形 8°（差一点点就正）、椭圆 52°（很明显是歪的）。
            float startDeg = isRect ? 8f : 52f;
            var center = new Vector2(cx, cy);
            if (isRect)
            {
                sh.Tool = Tool.Rectangle; sh.Kind = StrokeKind.Rectangle;
                sh.AddPoint(center.X - 320f, center.Y - 200f, 1f, 0);
                sh.AddPoint(center.X + 320f, center.Y + 200f, 1f, 0);
            }
            else
            {
                sh.Tool = Tool.Ellipse; sh.Kind = StrokeKind.Ellipse;
                sh.AddPoint(center.X, center.Y, 1f, 0);              // 中心
                sh.AddPoint(center.X + 330f, center.Y + 150f, 1f, 0); // 外角点（a=330, b=150）
            }
            sh.Transform = SelectionHandles.RotateMatrix(startDeg, center);
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var f0 = SelectionHandles.FrameOf(Doc.Selected);
            var pv = new Vector2((f0.CanvasAabb.MinX + f0.CanvasAabb.MaxX) * 0.5f,
                                 (f0.CanvasAabb.MinY + f0.CanvasAabb.MaxY) * 0.5f);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f0, DpiScale);
            float arm = Vector2.Distance(grip, pv);
            float a0 = MathF.Atan2(grip.Y - pv.Y, grip.X - pv.X);
            // 要转到哪儿：矩形顺时针 8.4°（8 → -0.4，±1° 内 → 吸到 0）；
            //          椭圆逆时针 38.4°（52 → 90.4，±1° 内 → 吸到 90）。
            float turn = isRect ? -8.4f : 38.4f;

            SendMouse((int)grip.X, (int)grip.Y, 0);                          SettleFrames(60);
            SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            var last = grip;
            for (int i = 1; i <= 4; i++)
            {
                // 屏幕坐标 y 朝下：方向角**减小**才是视觉上的逆时针，所以 rad = a0 − 转过的角。
                float rad = a0 - turn * i / 4f * MathF.PI / 180f;
                last = new Vector2(pv.X + arm * MathF.Cos(rad), pv.Y + arm * MathF.Sin(rad));
                SendMouse((int)last.X, (int)last.Y, 0);
                SettleFrames(50);
            }
            SettleFrames(220);

            var live = LiveSelectionFrame;
            var gripNow = SelectionHandles.CanvasPosition(SelHandle.Rotate, live, DpiScale);
            var region = live.CanvasAabb;
            region.Add(gripNow.X - 70f * DpiScale, gripNow.Y - 80f * DpiScale);
            region.Add(gripNow.X + 70f * DpiScale, gripNow.Y + 20f * DpiScale);
            Console.WriteLine($"姿态角那一帧（{(isRect ? "矩形" : "椭圆")}）：读数 {SelRotationPose:F1}°"
                            + $"（吸住={SelRotationSnapped}），起手 {startDeg:F0}°，拖了 {turn:F1}°"
                            + $"，标签文案「{SelectionHandles.FormatPose(SelRotationPose)}」");
            if (!OffscreenFloatingShot(path, region.Inflate(40f))) Console.WriteLine("出图失败");
            SendMouse((int)last.X, (int)last.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(150);
            _quit = true;
            return;
        }

        // `--angles`：三角形和平行四边形**都要拖着一个顶点不松手**才出角度
        //（用户 2026-09-20 定：三角形也改成"拖动时才显示"，和平行四边形一条判据）。
        if (angleRead)
        {
            bool para = kind == "parallelogram";
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            if (para)
            {
                sh.Tool = Tool.Parallelogram; sh.Kind = StrokeKind.Parallelogram;
                sh.AddPoint(cx - 300f, cy + 170f, 1f, 0);      // 底左
                sh.AddPoint(cx + 300f, cy + 170f, 1f, 0);      // 底右
                sh.AddPoint(cx - 110f, cy - 170f, 1f, 0);      // 顶左
            }
            else
            {
                // 刻意**不等边**：三个角三个不同的数，出图时一眼能看出"每个角各自独立"
                sh.Tool = Tool.Triangle; sh.Kind = StrokeKind.Triangle;
                sh.AddPoint(cx - 70f, cy - 250f, 1f, 0);       // 上顶点
                sh.AddPoint(cx - 320f, cy + 170f, 1f, 0);      // 下左
                sh.AddPoint(cx + 320f, cy + 170f, 1f, 0);      // 下右
            }
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            // 拖一个顶点、**停住不松手**：三角形和平行四边形**都只在拖顶点时**才出角度
            // （用户 2026-09-20 定："三角形应该在拖动的时候再显示角度，要不然看起来也乱"），
            // 所以拍这一张必须按住不放——松手拍出来是空的。
            // 三角形拖**上顶点**（Vertex0）、平行四边形拖**底右顶点**（Vertex1）。
            var dragHandle = para ? ShapeHandle.Vertex1 : ShapeHandle.Vertex0;
            var dragBy = para ? new Vector2(70f, 40f) : new Vector2(90f, 70f);
            var v = SelectionHandles.ShapeHandleCanvasPosition(sh, dragHandle);
            var toP = new Vector2(v.X + dragBy.X, v.Y + dragBy.Y);
            SendMouse((int)v.X, (int)v.Y, 0);                            SettleFrames(60);
            SendMouse((int)v.X, (int)v.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(v.X + (toP.X - v.X) * i / 4f),
                          (int)(v.Y + (toP.Y - v.Y) * i / 4f), 0);
                SettleFrames(50);
            }
            SettleFrames(220);

            Span<Vector2> vs = stackalloc Vector2[4];
            Span<float> ds = stackalloc float[3];
            int n = FillAngleReadout(vs, ds);
            // **没有"内角和"那一行**（用户 2026-09-19 定），所以只报那几个角自己。
            Console.WriteLine(para
                ? $"夹角那一帧（平行四边形拖顶点中）：{n} 个角 = "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[0])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[1])}，顶点 {vs[0]} / {vs[1]}"
                : $"内角那一帧（三角形拖顶点中）：{n} 个角 = "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[0])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[1])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[2])}");
            var region = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            region = region.Inflate(220f * DpiScale);            // 角标挂在顶点外侧，框要放宽
            if (!OffscreenFloatingShot(path, region)) Console.WriteLine("出图失败");
            SendMouse((int)toP.X, (int)toP.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(150);
            _quit = true;
            return;
        }

        // ---- 圆 / 椭圆 / 三角形 / 平行四边形 / 抛物线 / 长方体：静止选中态 ----
        //（后两种是"**没有特殊手柄**"的那一类：拍的是通用八手柄 ＋ 旋转柄那根连线）
        if (kind != "line")
        {
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            switch (kind)
            {
                case "circle":
                    sh.Tool = Tool.Circle; sh.Kind = StrokeKind.Circle;
                    sh.AddPoint(cx - 40f, cy - 40f, 1f, 0);        // 圆心
                    sh.AddPoint(cx + 200f, cy - 40f, 1f, 0);       // 圆周点（半径 240）
                    break;
                case "ellipse":
                    sh.Tool = Tool.Ellipse; sh.Kind = StrokeKind.Ellipse;
                    sh.AddPoint(cx - 40f, cy, 1f, 0);              // 中心
                    sh.AddPoint(cx + 260f, cy + 180f, 1f, 0);      // 外角点（a=300, b=180）
                    break;
                case "triangle":
                    sh.Tool = Tool.Triangle; sh.Kind = StrokeKind.Triangle;
                    sh.AddPoint(cx, cy - 220f, 1f, 0);             // 上中
                    sh.AddPoint(cx - 260f, cy + 160f, 1f, 0);      // 下左
                    sh.AddPoint(cx + 260f, cy + 160f, 1f, 0);      // 下右
                    break;
                case "parabola":
                    // 抛物线：顶点在左下、经过点在右上 → 开口向上（四个朝向里最好认的那个）。
                    // 它是"没有特殊手柄"那一类的代表：选中后该看到**通用八手柄**，
                    // 旋转柄那根线该连到**框上边中点**（不是顶点与经过点的中点）。
                    sh.Tool = Tool.Parabola; sh.Kind = StrokeKind.Parabola;
                    sh.CurveAxis = CurveAxis.OpenUp;
                    sh.AddPoint(cx - 240f, cy + 150f, 1f, 0);      // 顶点（按下那个点）
                    sh.SetParabolaVertex(cx - 240f, cy + 150f);
                    sh.SetParabolaThroughPoint(cx + 200f, cy - 170f);
                    break;
                case "cuboid":
                    // 长方体：正面矩形 ＋ 深度 150（照画法：深度往右上退）。
                    sh.Tool = Tool.Cuboid; sh.Kind = StrokeKind.Cuboid;
                    sh.AddPoint(cx - 260f, cy - 40f, 1f, 0);
                    sh.SetCuboidFront(cx - 260f, cy - 40f, cx + 180f, cy + 200f);
                    sh.SetCuboidDepth(cy - 40f, cy - 40f + 150f);  // 深 = |正面上边 − 这个 y| = 150
                    break;
                default: // parallelogram
                    sh.Tool = Tool.Parallelogram; sh.Kind = StrokeKind.Parallelogram;
                    sh.AddPoint(cx - 250f, cy + 160f, 1f, 0);      // 底左
                    sh.AddPoint(cx + 250f, cy + 160f, 1f, 0);      // 底右
                    sh.AddPoint(cx - 125f, cy - 160f, 1f, 0);      // 顶左
                    break;
            }
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            // 吸附那一帧：真拖一次顶点，**停住不松手**再拍（松手就没有胶囊了）。
            // 三角形摆到"等边"的容差里（高 = √3/2 × 底边长 520 ≈ 450.3）。
            if (snap && kind == "triangle")
            {
                var apex = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.Vertex0);
                var apexTo = new Vector2(cx + 1f, cy + 160f - 260f * MathF.Sqrt(3f));
                SendMouse((int)apex.X, (int)apex.Y, 0);                          SettleFrames(60);
                SendMouse((int)apex.X, (int)apex.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
                for (int i = 1; i <= 4; i++)
                {
                    SendMouse((int)(apex.X + (apexTo.X - apex.X) * i / 4f),
                              (int)(apex.Y + (apexTo.Y - apex.Y) * i / 4f), 0);
                    SettleFrames(40);
                }
                SettleFrames(200);
                Console.WriteLine($"吸附帧：吸到={ShapeSnapKind}"
                                + $"（「{SelectionHandles.ShapeSnapLabel(ShapeSnapKind)}」），"
                                + $"被拖的顶点 {apex} → {apexTo}");
            }

            var sFrame = SelectionHandles.FrameOf(Doc.Selected);
            var sAabb = sFrame.CanvasAabb;
            var sBar = SelectionHandles.BarRect(sAabb, DpiScale, ViewportCanvas);
            var region = sAabb;
            region.Add(sBar);
            if (SelectionHandles.RotateHandleVisible(sh))       // 圆没有旋转柄，别白算一块
            {
                var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, sFrame, DpiScale);
                region.Add(grip.X - 40f * DpiScale, grip.Y - 40f * DpiScale);
                region.Add(grip.X + 40f * DpiScale, grip.Y + 40f * DpiScale);
            }
            // 拖元素时的胶囊挂在元素外侧：`--snap` 那张必须把它一起圈进来
            if (snap && VertexDragging)
            {
                region.Add(VertexPreviewCanvasPoint.X - 90f * DpiScale,
                           VertexPreviewCanvasPoint.Y - 60f * DpiScale);
                region.Add(VertexPreviewCanvasPoint.X + 90f * DpiScale,
                           VertexPreviewCanvasPoint.Y + 10f * DpiScale);
            }
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");
            if (snap && VertexDragging)
            {
                SendMouse((int)ShapeSnapAnchor.X, (int)ShapeSnapAnchor.Y, Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(150);
            }
            _quit = true;
            return;
        }

        // ---- 一条"已经转过 60°"的直线、静止选中（紧框改口径的第二个现场）----
        if (rotated)
        {
            var rl = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 和另外几张**同一条线**（α₀ ≈ 27.65°），绕它自己的中点视觉逆时针转 32.35°
            // → 转完 α ≈ 60°，正是 --rotate 松手之后那个状态。
            rl.AddPoint(cx - 420f, cy + 220f, 1f, 0);
            rl.AddPoint(cx + 420f, cy - 220f, 1f, 0);
            rl.Transform = SelectionHandles.RotateMatrix(32.35f, new Vector2(cx, cy));
            Doc.AddStroke(rl);
            Doc.SelectOnly(new[] { rl });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var fr = SelectionHandles.FrameOf(Doc.Selected);
            var aabbR = fr.CanvasAabb;
            var barR = SelectionHandles.BarRect(aabbR, DpiScale, ViewportCanvas);
            var gripR = SelectionHandles.CanvasPosition(SelHandle.Rotate, fr, DpiScale);
            var regionR = aabbR;
            regionR.Add(barR);
            regionR.Add(gripR.X - 40f * DpiScale, gripR.Y - 40f * DpiScale);
            regionR.Add(gripR.X + 40f * DpiScale, gripR.Y + 40f * DpiScale);
            Console.WriteLine($"转过 60° 的线、静止选中：框 {aabbR.MaxX - aabbR.MinX:F0}×{aabbR.MaxY - aabbR.MinY:F0}"
                            + $"，线自己的墨迹 {rl.WorldInkBounds.MaxX - rl.WorldInkBounds.MinX:F0}"
                            + $"×{rl.WorldInkBounds.MaxY - rl.WorldInkBounds.MinY:F0}"
                            + $"，PaddedBounds {rl.PaddedBounds.MaxX - rl.PaddedBounds.MinX:F0}"
                            + $"×{rl.PaddedBounds.MaxY - rl.PaddedBounds.MinY:F0}");
            if (!OffscreenFloatingShot(path, regionR.Inflate(30f))) Console.WriteLine("出图失败");
            _quit = true;
            return;
        }

        // ---- 旋转拖动中：真按真转（用户要看的就是这一帧）----
        if (rotate)
        {
            var spinLine = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 和"静止选中"那张**同一条线**（α ≈ 27.65°），三张图才好对比
            spinLine.AddPoint(cx - 420f, cy + 220f, 1f, 0);
            spinLine.AddPoint(cx + 420f, cy - 220f, 1f, 0);
            Doc.AddStroke(spinLine);
            Doc.SelectOnly(new[] { spinLine });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var f0 = SelectionHandles.FrameOf(Doc.Selected);
            var pv = new Vector2((f0.CanvasAabb.MinX + f0.CanvasAabb.MaxX) * 0.5f,
                                 (f0.CanvasAabb.MinY + f0.CanvasAabb.MaxY) * 0.5f);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f0, DpiScale);
            float arm = Vector2.Distance(grip, pv);
            float a0 = MathF.Atan2(grip.Y - pv.Y, grip.X - pv.X);

            SendMouse((int)grip.X, (int)grip.Y, 0);                          SettleFrames(60);
            SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            // 逆时针 31.5°：这条线 α₀ ≈ 27.65° → 59.15°（±1° 内）→ 吸到 60°，读数好看也吸住了
            const float Turn = 31.5f;
            var last = grip;
            for (int i = 1; i <= 4; i++)
            {
                float rad = a0 - Turn * i / 4f * MathF.PI / 180f;
                last = new Vector2(pv.X + arm * MathF.Cos(rad), pv.Y + arm * MathF.Sin(rad));
                SendMouse((int)last.X, (int)last.Y, 0);
                SettleFrames(40);
            }
            SettleFrames(200);

            // 范围：**实时框**（旋转中框按预览几何算，见 LiveSelectionFrame）∪ 旋转柄 ∪ 读数标签
            var live = LiveSelectionFrame;
            var aabb = live.CanvasAabb;
            var gripNow = SelectionHandles.CanvasPosition(SelHandle.Rotate, live, DpiScale);
            var region = aabb;
            region.Add(gripNow.X - 60f * DpiScale, gripNow.Y - 60f * DpiScale);
            region.Add(gripNow.X + 60f * DpiScale, gripNow.Y + 60f * DpiScale);
            region.Add(gripNow.X - 90f * DpiScale, gripNow.Y - 90f * DpiScale);
            region.Add(gripNow.X + 90f * DpiScale, gripNow.Y);
            // 顺带量一个"线**真实**占多大"（把两个端点过实时矩阵再外扩半笔宽）——
            // 用来和上面那个框比：相等就是贴合，明显小就是框虚胖（只打印，不改行为）。
            var liveM = spinLine.Transform * DragPreviewMatrix;
            var t0 = Vector2.Transform(new Vector2(spinLine.Points[0].X, spinLine.Points[0].Y), liveM);
            var t1 = Vector2.Transform(new Vector2(spinLine.Points[^1].X, spinLine.Points[^1].Y), liveM);
            var tight = RectF.Empty;
            tight.Add(t0.X, t0.Y);
            tight.Add(t1.X, t1.Y);
            tight = tight.Inflate(spinLine.Width * 0.5f + 2f);
            Console.WriteLine($"旋转中：读数 {SelRotationInclination:F1}°（吸住={SelRotationSnapped}），"
                            + $"实时框 {aabb.MaxX - aabb.MinX:F0}×{aabb.MaxY - aabb.MinY:F0}"
                            + $" ({(int)aabb.MinX},{(int)aabb.MinY})-({(int)aabb.MaxX},{(int)aabb.MaxY})，"
                            + $"线自己真实占 {tight.MaxX - tight.MinX:F0}×{tight.MaxY - tight.MinY:F0}"
                            + $" ({(int)tight.MinX},{(int)tight.MinY})-({(int)tight.MaxX},{(int)tight.MaxY})"
                            + $"，模型里（没动）的墨迹范围 "
                            + $"{(int)spinLine.WorldInkBounds.MinX},{(int)spinLine.WorldInkBounds.MinY}"
                            + $"-{(int)spinLine.WorldInkBounds.MaxX},{(int)spinLine.WorldInkBounds.MaxY}");
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");

            SendMouse((int)last.X, (int)last.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);
            // 松手后框会重新按**模型**算（FrameOf）——和拖动中那个"旧框转过去的外接矩形"
            // 差多少，这里给出数字（只打印，不改行为）。
            var afterAabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            Console.WriteLine($"旋转松手后：框 {afterAabb.MaxX - afterAabb.MinX:F0}×{afterAabb.MaxY - afterAabb.MinY:F0}"
                            + $"（拖动中是 {aabb.MaxX - aabb.MinX:F0}×{aabb.MaxY - aabb.MinY:F0}），"
                            + $"线的实时姿态 "
                            + $"{SelectionHandles.InclinationDegrees(SelectionHandles.EndpointCanvasPosition(spinLine, 0), SelectionHandles.EndpointCanvasPosition(spinLine, 1)):F1}°");
            _quit = true;
            return;
        }

        // ---- 正在画一条直线：按住不放，拖到**原始 45.5°**（±1° 容差内 → 吸到 45°）----
        if (drawing)
        {
            SetToolFromUi(Tool.Line);
            var from = new Vector2(cx - 380f, cy + 200f);
            float rad = -45.5f * MathF.PI / 180f;
            var tipTarget = new Vector2(from.X + 620f * MathF.Cos(rad), from.Y + 620f * MathF.Sin(rad));

            SendMouse((int)from.X, (int)from.Y, 0);                            SettleFrames(60);
            SendMouse((int)from.X, (int)from.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(from.X + (tipTarget.X - from.X) * i / 4f),
                          (int)(from.Y + (tipTarget.Y - from.Y) * i / 4f), 0);
                SettleFrames(40);
            }
            SettleFrames(200);

            var tip = ShapeInclinationAnchor;
            // 起手必须是 RectF.Empty（默认构造的 RectF 是 (0,0)-(0,0)，会把画面原点算进来）
            var drawBox = RectF.Empty;
            drawBox.Add(from.X, from.Y);
            drawBox.Add(tip.X, tip.Y);
            drawBox = drawBox.Inflate(30f * DpiScale);
            // 读数标签挂在"正在拖的那一端"上方，高度 30 逻辑 + 一段间距：一起圈进画面
            drawBox.Add(tip.X - 80f * DpiScale, tip.Y - 60f * DpiScale);
            drawBox.Add(tip.X + 80f * DpiScale, tip.Y);
            Console.WriteLine($"画线中：α = {ShapeInclinationDegrees:F1}°（吸住={ShapeInclinationSnapped}）"
                            + $"，起点 {from}，笔尖 {tip}"
                            + $"，出图范围 ({drawBox.MinX:F0},{drawBox.MinY:F0})-({drawBox.MaxX:F0},{drawBox.MaxY:F0})");
            if (!OffscreenFloatingShot(path, drawBox)) Console.WriteLine("出图失败");

            SendMouse((int)tipTarget.X, (int)tipTarget.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(120);
            _quit = true;
            return;
        }

        var line = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
        };
        line.AddPoint(cx - 420f, cy + 220f, 1f, 0);
        line.AddPoint(cx + 420f, cy - 220f, 1f, 0);
        Doc.AddStroke(line);
        Doc.SelectOnly(new[] { line });
        Tool = Tool.Marquee;
        Doc.InvalidateAll();
        SettleFrames(700);

        if (!drag)
        {
            // 范围 = 选中框 ∪ 旋转柄 ∪ 操作条（操作条也由 DrawSelection 画，
            // 不并进来会被裁掉一半），再留一圈给投影。
            var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var bar = SelectionHandles.BarRect(aabb, DpiScale, ViewportCanvas);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate,
                                                       SelectionHandles.FrameOf(Doc.Selected), DpiScale);
            var region = aabb;
            region.Add(bar);
            region.Add(grip.X - 40f, grip.Y - 40f);
            region.Add(grip.X + 40f, grip.Y + 40f);
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");
            _quit = true;
            return;
        }

        // 真拖一次端点：原始方向 30.5°（±1° 容差内）→ 读数该显示 α = 30.0°。
        // 用真输入（而不是直接摆内部状态），出的图才是产品里那一帧。
        var eFixed = SelectionHandles.EndpointCanvasPosition(line, 1);
        var eDrag = SelectionHandles.EndpointCanvasPosition(line, 0);
        float theta = 149.5f * MathF.PI / 180f;
        var to = new Vector2(eFixed.X + 700f * MathF.Cos(theta), eFixed.Y + 700f * MathF.Sin(theta));

        SendMouse((int)eDrag.X, (int)eDrag.Y, 0);                          SettleFrames(60);
        SendMouse((int)eDrag.X, (int)eDrag.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
        for (int i = 1; i <= 4; i++)
        {
            SendMouse((int)(eDrag.X + (to.X - eDrag.X) * i / 4f),
                      (int)(eDrag.Y + (to.Y - eDrag.Y) * i / 4f), 0);
            SettleFrames(40);
        }
        SettleFrames(200);

        var preview = VertexPreviewCanvasPoint;
        // 起手必须是 RectF.Empty：默认构造的 RectF 是 (0,0)-(0,0)，
        // 直接 Add 进去会把画面原点也算进范围（出图一半是空白就是它）。
        var box = RectF.Empty;
        box.Add(eFixed.X, eFixed.Y);
        box.Add(preview.X, preview.Y);
        box = box.Inflate(40f * DpiScale);
        // 读数标签挂在被拖端点上方，高度 30 逻辑 + 一段间距：把它一起圈进画面
        box.Add(preview.X - 70f * DpiScale, preview.Y - 60f * DpiScale);
        box.Add(preview.X + 70f * DpiScale, preview.Y);
        Console.WriteLine($"拖端点中：α = {VertexReadoutValue:F1}°（吸住={VertexInclinationSnapped}），"
                        + $"固定端 {eFixed}，拖动端 {preview}"
                        + $"（模型里这条线还没动：墨迹范围 {(int)line.WorldInkBounds.MinX},{(int)line.WorldInkBounds.MinY}"
                        + $"-{(int)line.WorldInkBounds.MaxX},{(int)line.WorldInkBounds.MaxY}），"
                        + $"出图范围 ({box.MinX:F0},{box.MinY:F0})-({box.MaxX:F0},{box.MaxY:F0})");
        if (!OffscreenFloatingShot(path, box)) Console.WriteLine("出图失败");

        SendMouse((int)to.X, (int)to.Y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(120);
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
    private bool _benchPressure;

    /// <summary>`--benchpts N`：基准里每笔几个点（0 = 老行为：随机 8~31）。
    ///
    /// 为什么要有它：老的"一万笔"每笔平均 19.5 个点，
    /// 而**用户真机写字是 139~211 个点**（raw 补点之后）—— 老基准比真实用法轻了近 9 倍。
    /// 要答"一万笔到底吃多少"，得按真实点密度量。</summary>
    private int _benchPts;

    /// <summary>`--benchspread N`：一万笔**分散到 N 屏**（0 = 老行为：全挤在一屏）。
    /// 真实板书是一节课往下写十几屏，每屏只有那么多字；全挤一屏是最极端的形状，
    /// 量出来的重铺代价会大得多，拿它当"一节课的消耗"会吓人。</summary>
    private int _benchSpread;

    private void Benchmark(int strokeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"=== benchmark: {strokeCount} strokes"
                          + (_benchSpread > 0 ? $" spread over {_benchSpread} screens" : " on one screen")
                          + (_benchPressure ? " (with pressure)" : "")
                          + (_benchPts > 0 ? $" ({_benchPts} pts/stroke)" : "") + " ===");

        var gen = Stopwatch.StartNew();
        // 默认那一屏是**最极端的形状**（一万笔全挤在一屏）；真实板书是一节课分散十几屏。
        // `--benchspread N` 走分散版，量的才是"低配机上一节课"那个形状。
        if (_benchSpread > 0) GenerateStrokesSpread(strokeCount, _benchSpread);
        else GenerateStrokes(strokeCount);
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
            Laser.Begin(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.5f,
                        _clock.Elapsed.TotalMilliseconds, LaserWidthLogical);
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
            Laser.Clear();
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

    private static Native.INPUT_KBD KeyInput(ushort vk, bool up)
    {
        var inp = new Native.INPUT_KBD { type = Native.INPUT_KEYBOARD };
        inp.ki.wVk = vk;
        inp.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0u;
        return inp;
    }

    /// <summary>
    /// 合成键盘：把一串虚拟键**按顺序按下、再倒序抬起**（Ctrl+Alt+O 这种组合必须这样发，
    /// 只按主键是触发不了全局热键的）。
    ///
    /// 为什么要它（而不是像批注内按键那样直接 `PostMessage`）：全局热键是**系统**在
    /// 键盘输入流上判定的，`RegisterHotKey` 收到的是真实的按键；要验"按下去真的切了工具"，
    /// 就得走真实的键盘通路。批注内那些键不用它是因为它们由我们自己的窗口收，
    /// 直接投递窗口消息就能到。
    /// </summary>
    private uint SendKeyChord(params ushort[] vks)
    {
        var seq = new List<Native.INPUT_KBD>();
        foreach (var vk in vks) seq.Add(KeyInput(vk, false));
        for (int i = vks.Length - 1; i >= 0; i--) seq.Add(KeyInput(vks[i], true));
        // 返回值＝**真的塞进系统输入流的事件个数**。自检要把它打出来：
        // 它比"工具没切"多一层信息——分得清"按键没进系统"和"进了但热键没响应"。
        return Native.SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<Native.INPUT_KBD>());
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;        // Alt
    private const ushort VK_SHIFT = 0x10;

    /// <summary>按一次 Ctrl+Alt+&lt;主键&gt;（现在只给"退役图形键不响应"那条测试用；
    /// 2026-10-04 起全局热键都是 `Ctrl+Alt+Shift+…` 的形状）。返回塞进去的事件数。</summary>
    private uint SendCtrlAlt(ushort vk) => SendKeyChord(VK_CONTROL, VK_MENU, vk);

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
    private void CaptureIconShow(string path) => SheetShow(new CaptureIconSheet(), path);

    /// <summary>出图：**激光笔图标候选**（开发期比图用，见 <see cref="LaserIconSheet"/>）。</summary>
    private void IconShow(string path) => SheetShow(new LaserIconSheet(), path);

    /// <summary>出图：图形面板那些自绘图标的对照表（见 <see cref="ShapeIconSheet"/>）。</summary>
    private void ShapeIconShow(string path) => SheetShow(new ShapeIconSheet(), path);

    /// <summary>出图：**白板 / 激光笔的图标候选**（2026-09-26，见 <see cref="ToolIconSheet"/>）。</summary>
    private void ToolIconShow(string path) => SheetShow(new ToolIconSheet(), path);

    /// <summary>
    /// **把一张"图标对照表"的图截下来**——`--iconshow` / `--captureicons` / `--shapeiconshow`
    /// 三处共用这一份（原来只有前面两个、各写了一遍，加第三个时收过来的）。
    /// </summary>
    private void SheetShow(IOverlayUi sheet, string path)
    {
        SetUi(sheet);
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
            ui.SetExpandForTest(1f);                     // 展开（显式跳 1；别用会"翻面"的旧 SnapForTest）
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

    /// <summary>
    /// `--makeicon &lt;out.ico&gt;`：**用界面自己的渲染**生成程序图标。
    ///
    /// 2026-10-01 第五轮定：图标 = 设计稿 v5 的 **B 档「Fluent 笔 ＋ 带笔锋的红笔迹」**
    /// （用户："你笔锋弧线这一版做得挺好的，我想使用这个"；稿子见
    /// design/图标-设计稿v5-线条型的笔.png 与 v5b-笔迹四选.png，画在 <see cref="AppIconUi"/> 里）。
    /// 上一版是"实心的大笔 · 白砖"，再上一版是"收起态那颗球"，都留在 git 历史里；这条命令的机制没变：
    /// 把窗口 DPI **临时放大**再离屏出图 —— 界面是按"逻辑坐标 × DPI/96"画的，
    /// 所以放大 DPI 等于**按矢量重画一张大的**，而不是把 96 的小图拉大（拉大会糊）。
    /// 再把那一张按各档尺寸缩下去、装成一个多尺寸 .ico。
    /// </summary>
    private void MakeIcon(string path)
    {
        SetUi(new AppIconUi());
        SettleFrames(120);
        if (CurrentUi is not AppIconUi ui || _windows.Count == 0)
        {
            Console.WriteLine("  出图标失败：图标界面没挂上 / 没有窗口");
            _quit = true;
            return;
        }

        var box = ui.QueryBounds();
        if (box.IsEmpty) { Console.WriteLine("  出图标失败：占用矩形是空的"); _quit = true; return; }

        // 目标边长 256：图标界面是 64 逻辑像素的方框，DPI 抬到 384 正好出 256×256。
        var win = _windows[0];
        uint want = (uint)Math.Clamp(MathF.Round(96f * 256f / (box.MaxX - box.MinX)), 96, 96 * 12);
        uint dpi0 = win.Dpi;
        win.Dpi = want;
        byte[] px = win.RenderUiToBgraTransparent(this, 0, box, out int bw, out int bh);
        win.Dpi = dpi0;
        if (px == null || bw <= 0)
        {
            Console.WriteLine("  出图标失败：" + (win.LastError ?? "没画出来"));
            _quit = true;
            return;
        }
        Console.WriteLine($"  渲染尺寸 {bw}×{bh}（为它把窗口 DPI 临时抬到 {want}）");

        // **不做圆形蒙版**：形状就是白砖自己的圆角，砖外本来就是透明
        // （旧版那颗球才需要把方形柔光清成圆；这条注释留着，免得下次改回去时忘了）。
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            WriteMultiSizeIco(path, px, bw, bh, sizes);
            // 顺带出一张 PNG 预览：笔对不对、红不红、砖的圆角顺不顺，**只能看图**。
            string png = Path.ChangeExtension(path, ".png");
            using (var b = BgraToBitmap(px, bw, bh)) b.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  已写出 {path}（{sizes.Length} 档：{string.Join('/', sizes)}）");
            Console.WriteLine($"  预览图 {png}（人眼核对用）");
        }
        catch (Exception ex) { Console.WriteLine("  出图标失败：" + ex.Message); }
        _quit = true;
    }

    /// <summary>
    /// 量出"球"的半径（像素）：沿中心行/列找 α≥96 的最远点，取两者的较小值。
    /// 为什么要量而不是算：真实半径由界面渲染（含 DPI 缩放与投影）决定，硬算容易差几像素。
    /// 投影的 α 很小（≤30 上下），进不了 96 这道门，所以量到的是球本身。
    ///
    /// ⚠ 2026-10-01 起**没有调用**（图标换成了方角的「白砖＋大笔」，不再需要圆形蒙版）。
    ///   留着是给"以后真要做圆形图标"的那一刻：连同下面那个 <see cref="MaskIconToCircle"/>，
    ///   两条都在"球"的版本上实测过（踩过的坑写在注释里，比代码值钱）。
    /// </summary>
    private static int IconRadiusPx(byte[] bgra, int w, int h)
    {
        int cx = w / 2, cy = h / 2, rx = 0, ry = 0;
        for (int x = 0; x < w; x++) if (bgra[(cy * w + x) * 4 + 3] >= 96) rx = Math.Max(rx, Math.Abs(x - cx));
        for (int y = 0; y < h; y++) if (bgra[(y * w + cx) * 4 + 3] >= 96) ry = Math.Max(ry, Math.Abs(y - cy));
        return Math.Max(1, Math.Min(rx, ry) - 1);      // 再收 1px：把抗锯齿那圈也收进去
    }

    /// <summary>
    /// 圆形 alpha 蒙版：圆外全透明，边缘 1px 线性过渡（抗锯齿）。
    /// ⚠ 数据是**预乘** BGRA，四个分量要一起乘——只把 α 清零会留下黑边（预乘的经典坑）。
    /// </summary>
    private static void MaskIconToCircle(byte[] bgra, int w, int h, int radius)
    {
        float cx = w / 2f, cy = h / 2f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float k = Math.Clamp(radius - d + 0.5f, 0f, 1f);      // 圆内 1、圆外 0、边缘过渡
                if (k >= 1f) continue;
                int o = (y * w + x) * 4;
                bgra[o]     = (byte)(bgra[o]     * k);
                bgra[o + 1] = (byte)(bgra[o + 1] * k);
                bgra[o + 2] = (byte)(bgra[o + 2] * k);
                bgra[o + 3] = (byte)(bgra[o + 3] * k);
            }
        }
    }

    /// <summary>BGRA（D2D 的**预乘**格式）→ GDI+ 位图。像素行是自上而下的。</summary>
    private static System.Drawing.Bitmap BgraToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                             System.Drawing.Imaging.ImageLockMode.WriteOnly,
                             System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(bgra, y * w * 4, row, 0, w * 4);
                Marshal.Copy(row, 0, IntPtr.Add(d.Scan0, y * d.Stride), w * 4);
            }
        }
        finally { bmp.UnlockBits(d); }
        return bmp;
    }

    /// <summary>
    /// 写一个**多尺寸 ICO**（每档一张 PNG——Vista 以后系统就认这种）。
    ///
    /// 格式很小，手写比引第三方库省事：6 字节目录头 ＋ 每档 16 字节目录项 ＋ 各档 PNG 数据。
    /// 注意目录项里宽高各只占**一个字节**：256 要写成 0（这是 ICO 格式的约定，不是笔误）。
    /// </summary>
    private static void WriteMultiSizeIco(string path, byte[] bgra, int bw, int bh, int[] sizes)
    {
        using var src = BgraToBitmap(bgra, bw, bh);
        var blobs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var one = new System.Drawing.Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(one))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, s, s));
            }
            using var ms = new MemoryStream();
            one.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            blobs.Add(ms.ToArray());
        }

        using var fs = File.Create(path);
        using var wr = new BinaryWriter(fs);
        wr.Write((ushort)0); wr.Write((ushort)1); wr.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte wh = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            wr.Write(wh); wr.Write(wh);
            wr.Write((byte)0);            // 调色板颜色数（真彩不用）
            wr.Write((byte)0);            // 保留
            wr.Write((ushort)1);          // 平面数
            wr.Write((ushort)32);         // 位深
            wr.Write(blobs[i].Length);
            wr.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (var b in blobs) wr.Write(b);
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
        _panelShowMore = Environment.GetCommandLineArgs().Contains("--more");
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
        // --select：把工具切成选择（看"选择那一格的图标"）；配 --lasso 就是套索那一档
        //（2026-09-26 加：选择那格现在"选中什么显示什么"，两种框选各一个图标）。
        if (Environment.GetCommandLineArgs().Contains("--select")) Tool = Tool.Marquee;
        // --board-green / --board-black：换板色再出图（看"白板那格填的是哪块板的颜色"）
        {
            var argvB = Environment.GetCommandLineArgs();
            if (argvB.Contains("--board-green")) Host.Commands.SetBoardColor(InkPalette.BoardPresets[1].Color);
            if (argvB.Contains("--board-black")) Host.Commands.SetBoardColor(InkPalette.BoardPresets[2].Color);
        }
        // --pattern N / --step N：把白板底纹摆成某一档再出图（看色带上那两格文字和档位点）。
        // 和 `--cell` / `--zoom` / `--dashline` 同一类：产品里没有这个入口，只服务"图上看细节"。
        {
            var argvP = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(argvP, "--pattern");
            if (pi >= 0 && pi + 1 < argvP.Length && int.TryParse(argvP[pi + 1], out int patArg))
            {
                int si = Array.IndexOf(argvP, "--step");
                float stepArg = si >= 0 && si + 1 < argvP.Length
                                && float.TryParse(argvP[si + 1], out float sv) ? sv : 40f;
                Host.Commands.SetBoardPattern(patArg, stepArg);
            }
        }
        // --board-on：把白板真的打开再出图。**只影响图**：`BoardOn = true` 那句设的是 App
        // 自己的字段，引擎那边是关着的——而"白板色带最右端那个 ✕"在板关着时是压暗的
        //（见 FullUi.DrawBandAction），所以要看它正常的样子必须真开一下板。
        if (Environment.GetCommandLineArgs().Contains("--board-on")) Host.Commands.SetBoard(true);
        // --shape <名字>：把工具切成某一种图形再出图（看"主条那一格的图标跟着种类变"）。
        // 名字用命令行里那套小写写法，和 --shapetoolshow 的 --circle/--triangle 一致。
        {
            var argv = Environment.GetCommandLineArgs();
            int si = Array.IndexOf(argv, "--shape");
            if (si >= 0 && si + 1 < argv.Length)
            {
                Tool = argv[si + 1].ToLowerInvariant() switch
                {
                    "line" => Tool.Line,
                    "rect" or "rectangle" => Tool.Rectangle,
                    "ellipse" => Tool.Ellipse,
                    "circle" => Tool.Circle,
                    "triangle" => Tool.Triangle,
                    "parallelogram" => Tool.Parallelogram,
                    "arrow" => Tool.Arrow,
                    // 2026-09-20：四种曲线（出图看"图形格两行 + 新图标"时要用）
                    "parabola" => Tool.Parabola,
                    "hyperbola" => Tool.Hyperbola,
                    "sine" => Tool.Sine,
                    "cosine" => Tool.Cosine,
                    "wave" => Tool.Wave,
                    "tangent" => Tool.Tangent,
                    "cylinder" => Tool.Cylinder,
                    "cone" => Tool.Cone,
                    "conefrustum" => Tool.ConeFrustum,
                    "sphere" => Tool.Sphere,
                    // 立体那一族（出图看"图形格第二行 + 档位点"时要用）：三个的名字就是
                    // 它们各自的工具名小写（棱柱 / 棱锥 / 棱台）。
                    "prism" => Tool.Prism,
                    "pyramid" => Tool.Pyramid,
                    "frustum" => Tool.Frustum,
                    _ => Tool.Line,
                };
            }
        }
        // 上面那几行改的是 **App 自己的 Tool 字段**，界面不会因此重算"当前是哪一格"
        // （`Tool` 是个裸字段，没有通知）。而出图要看的恰恰是"图形那一格的两行"，
        // 所以这里**走一次真的换工具**（引擎 → 通知界面 → 上带跟着切到图形格）。
        Host.Commands.SetTool(Tool);
        // 套索那一档要单独设（它和"选的是哪个工具"是两回事，见 SetSelectMode）
        if (Environment.GetCommandLineArgs().Contains("--lasso"))
            Host.Commands.SetSelectMode(SelectMode.Lasso);
        SettleFrames(400);

        // --hide：贴边隐藏**要先写进偏好再挂界面**（界面是在 Attach 里读偏好的），
        // 挂完再把露头一把按到底。配合 --ball 出"收起态露头"，不配就是"展开态露头"。
        bool wantHide = Environment.GetCommandLineArgs().Contains("--hide");
        if (wantHide)
        {
            SetUiPref("hide", "1");
            SetUiFactory(() => new InkUi.FullUi());
        }

        if (CurrentUi is InkUi.FullUi ui)
        {
            // --ball：**出收起态（那个球）**——启动默认就是展开态，所以要显式跳 0；
            // 不传就保持展开（收起/贴边这一类毛病只在球上看得见，所以要能单拍它）。
            // ⚠ 2026-10-03 修：原来写的是 `if (!wantBall) ui.SnapForTest()`，而那一刻的
            //   `SnapForTest` 是**跳到另一端**的开关、不是"展开"——启动时本来就展开，
            //   那一跳反而把默认态收成了球：**不带 --ball 出球、带 --ball 出展开**，
            //   和注释正好相反（出 README 主图时实测到）。现在显式跳 0 / 跳 1。
            bool wantBall = Environment.GetCommandLineArgs().Contains("--ball");
            ui.SetExpandForTest(wantBall ? 0f : 1f);
            if (wantHide) ui.ForcePeekForTest(0f);
            // --expand <0..1>：把"球 → 带子"的展开进度钉在中间某一帧（核对动画用）
            {
                var argv = Environment.GetCommandLineArgs();
                int ei = Array.IndexOf(argv, "--expand");
                if (ei >= 0 && ei + 1 < argv.Length && float.TryParse(argv[ei + 1], out float ex))
                    ui.SetExpandForTest(ex);
            }
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
            // --dashline N：把「直线」那一格的档位往后切 N 下（看"图标跟着线型换"那三档：
            // N=0 实线、1 虚线、2 点线；配 `--shape line` 用）。产品里没有这个入口，
            // 它只服务"图上看细节"（和 --zoom / --cell 同一类）。
            {
                int dli = Array.IndexOf(cli, "--dashline");
                if (dli >= 0 && dli + 1 < cli.Length && int.TryParse(cli[dli + 1], out int dashArg))
                {
                    for (int k = 0; k < dashArg; k++) Host.Commands.CycleLineDash();
                    SettleFrames(120);
                }
            }
            // --paraaxis N：把「抛物线」那一格的档位往后切 N 下（N=1 就是"左右抛物"那一档）。
            // 和 --dashline 同一类，只服务"图上看细节"。
            {
                int pai = Array.IndexOf(cli, "--paraaxis");
                if (pai >= 0 && pai + 1 < cli.Length && int.TryParse(cli[pai + 1], out int paArg))
                {
                    for (int k = 0; k < paArg; k++) Host.Commands.CycleParabolaAxis();
                    SettleFrames(120);
                }
            }
            // --dark：出图前直接切深色主题（**不落盘**；和点「深色主题」那一行同一条路）
            if (Environment.GetCommandLineArgs().Contains("--dark")) ui.SetDarkForTest(true);
            // --more [--page N]：出**中央「更多」面板**（0 课堂 / 1 墨迹 / 2 设置）。
            // 面板打开时 QueryBounds 是整块屏幕，截图范围在下面单独裁（见 shot 那一段）。
            if (_panelShowMore)
            {
                ui.OpenMoreForTest();
                var argvMore = Environment.GetCommandLineArgs();
                int pi2 = Array.IndexOf(argvMore, "--page");
                if (pi2 >= 0 && pi2 + 1 < argvMore.Length && int.TryParse(argvMore[pi2 + 1], out int pageArg))
                    ui.SetMorePageForTest(pageArg);
            }
            SettleFrames(500);

            // --timerwin [--run|--edit|--min|--full] / --rollwin [--n 3] [--names]：
            // 课堂窗画在**引擎浮层**里，离屏那条路（只画界面层）拍不到它，
            // 所以等它真画出来，直接截屏幕上的窗口矩形（1:1）。
            if (Environment.GetCommandLineArgs().Contains("--timerwin")
                || Environment.GetCommandLineArgs().Contains("--rollwin"))
            {
                var argvW = Environment.GetCommandLineArgs();
                if (argvW.Contains("--timerwin"))
                {
                    OpenTimerCardFromUi();
                    SettleFrames(200);
                    var tw = TimerCardRect();
                    var cin = TimerWin.CardRect(tw, DpiScale, false);
                    float lu = TimerWin.Layout(cin, DpiScale);
                    void ClickAxis(in RectF r)
                    {
                        TimerPointerDownForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                        TimerPointerUpForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                    }
                    if (argvW.Contains("--stopwatch")) { StartTimerFromUi(TimerMode.Stopwatch, 0f); SettleFrames(900); }
                    else if (argvW.Contains("--run")) StartTimerFromUi(TimerMode.Countdown, 300f);
                    int tbi = Array.IndexOf(argvW, "--tab");
                    if (tbi >= 0 && tbi + 1 < argvW.Length && int.TryParse(argvW[tbi + 1], out int tabArg))
                    {
                        var tcin = TimerWin.CardRect(TimerCardRect(), DpiScale, false);
                        float tlu = TimerWin.Layout(tcin, DpiScale);
                        var tr = TimerWin.TabRect(tcin, tlu, Math.Clamp(tabArg, 0, 2));
                        TimerPointerDownForTest((tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f);
                        TimerPointerUpForTest((tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f);
                    }
                    if (argvW.Contains("--full")) ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Fullscreen));
                    else if (argvW.Contains("--edit")) ClickAxis(TimerWin.ValueRect(cin, lu));
                    else if (argvW.Contains("--min")) ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Minimize));
                }
                if (argvW.Contains("--rollwin"))
                {
                    if (argvW.Contains("--names"))
                    {
                        string tmpNames = Path.Combine(Path.GetTempPath(), "inkteach-shot-names.txt");
                        File.WriteAllLines(tmpNames, new[] { "张伟", "李娜", "王强", "刘洋", "陈晨" });
                        InkEngine.InkEngine.NamesPathOverride = tmpNames;
                        ReloadNamesFromUi();
                    }
                    OpenRollCardFromUi();
                    int ri = Array.IndexOf(argvW, "--n");
                    if (ri >= 0 && ri + 1 < argvW.Length && int.TryParse(argvW[ri + 1], out int nArg) && nArg > 1)
                    {
                        var rw = RollCardRect();
                        float ru = RollUnit();
                        var pr = RollWin.PlusRect(rw, ru);
                        for (int k = 1; k < Math.Min(nArg, 60); k++)
                        {
                            RollPointerDownForTest((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
                            RollPointerUpForTest((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
                        }
                    }
                    StartRollFromUi();
                    double rt0 = NowMs;
                    while (RollingNow && NowMs - rt0 < 3000) { DrainMessages(); Thread.Sleep(10); RollTickForTest(); }
                }
                SettleFrames(400);
                // --board [--boardblack]：出图前铺一层白板，让背景是干净的。
                // 课堂窗是**截屏**出来的（离屏那条路只画界面层，拍不到它），不加这层的话
                // 图里会混着桌面图标和当时的窗口——拿去当宣传图很难看。
                if (argvW.Contains("--board"))
                {
                    Host.Commands.SetBoard(true);
                    if (argvW.Contains("--boardblack"))
                        Host.Commands.SetBoardColor(new Color4(0.13f, 0.15f, 0.17f, 1f));
                    SettleFrames(300);
                }
                if (argvW.Contains("--keepboard")) SetPassThroughFromUi(false);
                SettleFrames(300);
                var wr = argvW.Contains("--timerwin") ? TimerCardRect() : RollCardRect();
                bool okW = ScreenProbe.SaveBmp(path,
                    (int)MathF.Floor(wr.MinX), (int)MathF.Floor(wr.MinY),
                    Math.Max(1, (int)MathF.Ceiling(wr.MaxX - wr.MinX)),
                    Math.Max(1, (int)MathF.Ceiling(wr.MaxY - wr.MinY)));
                Console.WriteLine(okW
                    ? $"已出图 {path}（截屏 {wr.MaxX - wr.MinX:F0}×{wr.MaxY - wr.MinY:F0}）"
                    : "出图失败");
                ExitCode = okW ? 0 : 1;
                _quit = true;
                return;
            }

            // 优先离屏出图（锁屏 / 远程也能出，图里不混桌面）。
            // **范围要连"画到外面那一圈"一起给**（投影，见 IOverlayUi.PaintMargin）：
            // 这一条的边上留白只有 24 **物理**像素，200% 缩放下就是 12 逻辑像素，
            // 而投影最远胀出去 16+8 逻辑像素——不给就会被切掉一半，图看着像"没有投影"。
            var b = ui.QueryBounds();
            // --more：面板打开时占用 = 整块屏幕，照它出图会得到一整屏（20MB 级）。
            // 设计验收要的是那块卡片 ＋ 周围一圈遮罩，所以裁到"面板 ＋ 64"。
            if (_panelShowMore)
            {
                var mr = ui.MoreRectForTest;
                b = new RectF
                {
                    MinX = mr.MinX - 64f, MinY = mr.MinY - 64f,
                    MaxX = mr.MaxX + 64f, MaxY = mr.MaxY + 64f,
                };
            }
            var shot = new RectF
            {
                MinX = b.MinX - ui.PaintMargin, MinY = b.MinY - ui.PaintMargin,
                MaxX = b.MaxX + ui.PaintMargin, MaxY = b.MaxY + ui.PaintMargin,
            };
            // --zoom N：只出**第 N 格的近景**（2026-09-19 加，看"图形那一格的图标"用）。
            // 裁剪框就是那一格的矩形再松 8 逻辑像素——多给一圈是为了别把按钮的圆角
            // 和选中底色切掉（切掉之后图上看着像"图标缺了一块"）。产品里没有这个入口，
            // 它只服务"图上看细节"。
            {
                int zi = Array.IndexOf(cli, "--zoom");
                if (zi >= 0 && zi + 1 < cli.Length && int.TryParse(cli[zi + 1], out int zoomCell))
                {
                    var cr = ui.CellRectForTest(zoomCell);
                    if (!cr.IsEmpty)
                        shot = new RectF
                        {
                            MinX = cr.MinX - 8f, MinY = cr.MinY - 8f,
                            MaxX = cr.MaxX + 8f, MaxY = cr.MaxY + 8f,
                        };
                }
            }
            if (OffscreenShot(path, shot)) return;
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
    private void RadialShow(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 呼出盘摆样（Ctrl+Alt+Shift+Q）===");

        // --dark：深色那档也出一张（呼出盘跟着 FloatingTheme 走，这里顺手验观感）。
        // 自检模式用临时配置（`_selfCheckMode`），不会碰用户的 settings.json。
        if (Environment.GetCommandLineArgs().Contains("--dark")) SetUiPref("dark", "1");

        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ShowHud = false;
        RadialTestHold = true;

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // ① 打开（还没划）
        RadialOpenForTest(cx, cy);
        SettleFrames(400);
        bool ok1 = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ② 划向「红」（正东）
        RadialMoveForTest(cx + 120f, cy);
        SettleFrames(300);
        string path2 = System.IO.Path.ChangeExtension(path, null) + "-red.bmp";
        bool ok2 = ScreenProbe.SaveBmp(path2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        RadialCancelForTest("摆样结束");
        RadialTestHold = false;
        Console.WriteLine(ok1 && ok2 ? $"出图：{path} / {path2}" : "抓屏失败（看上一行）");
        _quit = true;
    }
    private static byte[] MakeLegacyFile(InkDocument doc, int version, int droppedBytes)
    {
        byte[] cur = InkSerializer.Save(doc);
        byte[] old = droppedBytes > 0 ? new byte[cur.Length - droppedBytes] : cur;
        if (droppedBytes > 0) Array.Copy(cur, old, old.Length);
        // 魔数（8 字节）后面那个小端 int32 就是版本号。
        old[4] = (byte)version; old[5] = 0; old[6] = 0; old[7] = 0;
        return old;
    }
    private void AxisShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        float x0 = _virtualX + 460f, y0 = _virtualY + 400f;
        // 外框（两张坐标系共用）：x0..x0+760 × y0..y0+620，所以半宽半高 = (380, 310)。
        // **画法改成了"按下 = 原点、拖出去 = 展开"**（见 Stroke.SetAxisBox），
        // 所以这里的第一参数是原点、第二参数是"往右下拖多远"，不再是外框的两个角。
        const float hw = 380f, hh = 310f;

        // ① 坐标系 · 带网格，原点摊在框的左下（老师最常用的"只留第一象限"）
        var g = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = true,
        };
        for (int i = 0; i < 3; i++) g.AddPoint(x0 + 130f, y0 + 470f, 1f, 0);
        g.SetAxisBox(x0 + 130f, y0 + 470f, x0 + 130f + hw, y0 + 470f + hh);
        Doc.AddStroke(g);

        // ② 坐标系 · 不带网格，原点在框正中间（和左边对照）
        var c = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = false,
        };
        for (int i = 0; i < 3; i++) c.AddPoint(x0 + 900f + hw, y0 + hh, 1f, 0);
        c.SetAxisBox(x0 + 900f + hw, y0 + hh, x0 + 900f + hw + hw, y0 + hh + hh);
        Doc.AddStroke(c);

        // ③ 数轴：一条水平线 + 右端箭头（没有刻度）
        var n = new Stroke
        {
            Tool = Tool.NumberLine, Kind = StrokeKind.NumberLine,
            Color = ink, Width = 4f * DpiScale,
        };
        for (int i = 0; i < 2; i++) n.AddPoint(x0, y0 + 760f, 1f, 0);
        n.SetAxisBox(x0, y0 + 760f, x0 + 1660f, y0 + 760f);
        Doc.AddStroke(n);

        // ④ 一条虚线直线（顺带把线型也看进去：虚线在粗笔上是不是像样）
        var d = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = ink, Width = 7f * DpiScale, Dash = StrokeDash.Dashed,
        };
        d.AddPoint(x0, y0 + 960f, 1f, 0);
        d.AddPoint(x0 + 1660f, y0 + 960f, 1f, 0);
        Doc.AddStroke(d);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)(x0 - 60f), (int)(y0 - 60f), 1900, 1150);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 出图：函数曲线各来一份——**抛物线四种开口、双曲线两个方向、正弦 / 余弦各一个周期、
    /// 波浪线三个周期、正切一支**。
    /// 用法 `--curveshow [路径]`，默认 `reports/四种曲线.bmp`。
    ///
    /// 自检盯的是"数值对不对"，这一张是给**眼睛**看的（这条教训见 计划-图形工具.md 10.7）：
    ///   · 曲线胖不胖、够不够顺（段数按尺寸定，太少了看着是折线）；
    ///   · 双曲线**截断到哪**（`Stroke.HyperbolaTMaxOf`：出框就停笔、最多画到 `2a`：
    ///     太短看着像两根短线、太长就把包围盒撑得很大）；
    ///   · 四种开口是不是真的四个方向（左右开口是"换自变量"，最容易写反）；
    ///   · 正弦 / 余弦的**起点**对不对（正弦从轴起、余弦从峰起），而且**都是一个周期**；
    ///   · **波浪线**是不是"很多个周期"（和正弦那一张摆在一起看，一眼就该分得开）；
    ///   · 正切**两侧的渐近线**在不在、曲线有没有贴着它上去（这条靠 `TangentMinAspect` 兜着，
    ///     所以这一张**故意只拖 60 高**：画出来仍该是贴着渐近线的那一支）。
    /// 摆放：4 列 × 3 行，第一行四种开口、第二行"双曲线两个方向 ＋ 正弦 / 余弦 / 波浪线 / 正切"、
    /// 第三行四种立体。第二行挤了 6 个图形，所以位置是按"不碰上邻居"逐个摆的（见各段注释）。
    /// </summary>
    /// <summary>
    /// 出图（`--smoothshow [路径]`）：**曲线化开/关的对照**（2026-09-28）。
    ///
    /// 同一组"手写样本"画两遍——先按现在的折线出 `-off`，再把曲线化打开、整层重画出 `-on`：
    ///   ① 快弧：采样故意稀（模拟"关掉 Windows Ink 的笔 / 鼠标"）；
    ///   ② 直角：必须原样保留（用户唯一的硬约束）；
    ///   ③ 抛物线：手画曲线不许被改形（上次砍"保形平滑"就是因为这个）；
    ///   ④ 锯齿：连续尖角，一个都不许被磨圆。
    /// 好不好看只能靠眼睛，所以单开一条出图命令（判据交给下一轮真人对比）。
    /// </summary>
    private void SmoothShowcase(string path)
    {
        Console.WriteLine($"=== 出图：曲线化对照（{path}）===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);
        var dotInk = new Color4(0.62f, 0.66f, 0.72f, 1f);   // 采样点的小灰点
        float penW = 6f * DpiScale;
        float ox = _virtualX + 120f, oy = _virtualY + 160f;

        void Add(IEnumerable<Vector2> pts, float dx, float dy, bool dots = true)
        {
            var list = new List<Vector2>();
            foreach (var p in pts) list.Add(new Vector2(p.X + dx, p.Y + dy));

            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = ink, Width = penW };
            foreach (var p in list) s.AddPoint(p.X, p.Y, 0.5f, 0);
            Doc.AddStroke(s);

            if (!dots) return;
            // 每个采样点打一个小灰点：曲线过不过点、直角动没动，看这个最直观
            foreach (var p in list)
            {
                var d = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = dotInk, Width = 2.4f * DpiScale,
                };
                d.AddPoint(p.X, p.Y, 0.5f, 0);
                Doc.AddStroke(d);
            }
        }

        // ① 快弧：中心 (ox+300, oy+280)、半径 280、每 13° 一个点（≈65px 一段，折角肉眼可见）
        var arc = new List<Vector2>();
        for (int i = 0; i <= 14; i++)
        {
            float a = MathF.PI * i / 14f;
            arc.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(arc, ox + 300f, oy + 280f);

        // ② 直角：两条臂各 6 个点（步长 56）
        var corner = new List<Vector2>();
        for (int i = 0; i <= 6; i++) corner.Add(new Vector2(i * 56f, 0f));
        for (int i = 1; i <= 6; i++) corner.Add(new Vector2(336f, i * 56f));
        Add(corner, ox + 760f, oy + 100f);

        // ③ 抛物线：y = x²/500，x ∈ [−260, 260]（手画曲线不许被改形）
        var parab = new List<Vector2>();
        for (int i = 0; i <= 13; i++)
        {
            float x = -260f + i * 40f;
            parab.Add(new Vector2(x, x * x / 500f));
        }
        Add(parab, ox + 1400f, oy + 120f);

        // ④ 锯齿：周期 2 的连续尖角（每个峰谷都必须留住）
        var zig = new List<Vector2>();
        for (int i = 0; i <= 12; i++) zig.Add(new Vector2(i * 55f, (i % 2 == 0) ? 0f : 110f));
        Add(zig, ox + 1860f, oy + 100f);

        // ⑤ 慢画直角：90° 摊在 5 小段上、**每段只有 20°**（比角点阈值 35° 小）——
        //    只有 ±2 点窗口判据兜得住；局部判据看到每段 20° 是判不出来的。
        //
        //    ⚠ 2026-10-06 修正：原来这条用的是 **120px 一段**（"90° 摊在三小段上"）。
        //    那个点距对应的速度是 130Hz × 120px ≈ 15600px/s，是"飞笔"不是"慢画"；
        //    而且"90° 摊在 3 个 120px 点上"与"半径 ~229px 的圆弧"在数学上无法区分
        //    （只有 4 个点），逼着窗口判据去误伤圆弧——正是"快速画圆变折线"的根因。
        //    改成 11px 一段（真·慢画的采样密度）之后，它才真的在测它声称要测的东西。
        var slow = new List<Vector2>();
        {
            const float stepPx = 11f;            // 点距（慢画：11px/点）
            int nPer = 6;                        // 先直走 6 段
            float ang5 = 0f;
            var p = new Vector2(0f, 0f);
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            for (int i = 0; i < 5; i++)          // 转 5 段 × 20° = 100°
            {
                ang5 += 20f * MathF.PI / 180f;
                slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx;
            }
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            slow.Add(p);
        }
        Add(slow, ox + 760f, oy + 520f);

        // ⑥ 密采样弧：**和真笔（Windows Ink 开）一个量级**（每 1° 一个点 ≈ 4.9px 一段）。
        // 它回答的是"拿手写板试为什么看不出区别"：点密到一定程度，折线和曲线就是同一根线
        //（弦长 c、半径 R 时中点偏差 ≈ c²/8R，c=5、R=280 → 0.011px，比一个像素小两个数量级）。
        var dense = new List<Vector2>();
        for (int i = 0; i <= 180; i++)
        {
            float a = MathF.PI * i / 180f;
            dense.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(dense, ox + 1560f, oy + 700f, dots: false);

        // ⑦ **稀疏快弧**（用户 2026-10-06 报的那个）：半径 150、每 22.5° 一个点
        //（弦长 = 2×150×sin(11.25°) ≈ 58.5px 一段）。这是"快速画圆 / 手写板关掉
        //  Windows Ink"的采样密度。
        //
        // 为什么单列这一条：窗口判据量到的转角 ≈ 2×(点距/半径)，
        // 这里 = 2×58.5/150 = 44.7° > 阈值 35° → **每个点都被判成角点** →
        // 半圆退化成 8 边形。修前修后一眼能看出来（对照用 `--smoothcornerpx 0`）。
        var sparse = new List<Vector2>();
        for (int i = 0; i <= 8; i++)
        {
            float a = MathF.PI * i / 8f;
            sparse.Add(new Vector2(MathF.Cos(a) * 150f, -MathF.Sin(a) * 150f));
        }
        Add(sparse, ox + 1120f, oy + 380f);

        // ⑧ **真机复现**（用户 2026-10-07 的实测数据）：「ink 关、快速画圆」那一档
        //（`压感=False 活笔=False 点 11 均距 38.9px 总长 389px`）。
        //
        // 关键算术：**圆上每个顶点的转角 = 360°/段数**，与半径无关。
        //   11 点 → 10 段 → **36°/顶点**，刚好越过 35° 阈值；
        //   ink 开的 36 点 → 35 段 → 10.3°/顶点，差得远。
        // 所以这一条要复现的是"**局部判据①在稀采样下把光滑圆弧判成角点**"，
        // 和用例⑦（那条是窗口判据③越界）是**两个不同的毛病**。
        var realcase = new List<Vector2>();
        {
            const int nSeg = 10;                 // 11 个点 = 10 段
            float R = 389f / (2f * MathF.PI);    // 总长 389px → 半径 ≈ 61.9px
            // **必须绕成接近闭合的一圈**：11 点绕 355° → 每段 35.5°，刚好越过
            // 35° 阈值（局部判据①）。绕得少一点（比如半圈）每段只有 17°，
            // 复现不出来——第一版就是这么做的，出图是光滑的，白测一轮。
            for (int i = 0; i <= nSeg; i++)
            {
                float a = 355f * MathF.PI / 180f * i / nSeg - 355f * MathF.PI / 180f * 0.5f;
                realcase.Add(new Vector2(MathF.Cos(a) * R, -MathF.Sin(a) * R));
            }
        }
        Add(realcase, ox + 420f, oy + 760f);

        // ⑨ **真机复现·椭圆**（用户 2026-10-07 第二轮数据）：他的形状不是圆，是
        //    周长 515~564px 的**椭圆**（≈200×150），10~14 个点、点距 40~60px。
        //
        //    为什么必须单列：椭圆上**转角是变化的**——端部曲率半径 = b²/a ≈ 56px、
        //    侧面 = a²/b ≈ 133px，所以端部每顶点转角 ≈48°、侧面只有 ≈20°，
        //    中间连续过渡。任何"相邻差"式的判据如果容差取小了，端部照样被切成角
        //    ——第一版连续性容差 12° 就是这么栽的（圆的转角处处相等，能过；
        //    椭圆过不了）。
        var ellipse = new List<Vector2>();
        {
            const float a = 100f, b = 75f;       // ≈ 周长 553px，和用户实测一致
            const int n = 12;                    // 12 个点
            // 按**弧长均匀**取样：等弧长行走（手画时点的分布接近这个，
            // 不是按参数角均匀——按角均匀会让端部点变稀，测不出真实分布）
            float perimeter = 0f;
            for (int k = 0; k < 720; k++)
            {
                float t0 = k * MathF.PI / 360f, t1 = (k + 1) * MathF.PI / 360f;
                float s0 = MathF.Sqrt(a * a * MathF.Sin(t0) * MathF.Sin(t0) + b * b * MathF.Cos(t0) * MathF.Cos(t0));
                float s1 = MathF.Sqrt(a * a * MathF.Sin(t1) * MathF.Sin(t1) + b * b * MathF.Cos(t1) * MathF.Cos(t1));
                perimeter += (s0 + s1) * 0.5f * (MathF.PI / 360f);
            }
            float step = perimeter / n;
            ellipse.Add(new Vector2(a, 0f));
            float theta = 0f;
            for (int i = 1; i <= n; i++)
            {
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    float d = 0.0005f;
                    acc += s * d;
                    theta += d;
                }
                float ang = theta - MathF.PI * 0.5f;   // 让起笔落在端部
                ellipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(ellipse, ox + 800f, oy + 760f);

        // ⑩ **真机复现·不均匀点距**（用户 2026-10-07 第三轮数据）：
        //    他实测的顶点转角序列是 `51 18 17 24 30 31 37 22 41 35 27` /
        //    `37 42 35 70 27 13 23 69 60 16` —— **同一个光滑形状上，转角在 13°~70° 之间跳**。
        //
        //    为什么：转角 ≈ **点距 / 局部曲率半径**。手快速画时点距不均匀
        //    （60Hz 采样 + 手抖），于是转角也跟着跳。**这不代表形状有角。**
        //    任何"转角 ≥ 固定绝对值就算角"的判据都会被它骗到 —— 这就是前几版反复栽的地方。
        //
        //    这条用例就是用"椭圆 + 均匀弧长点 + 沿路径抖动 ±50% 点距"复现那个分布。
        var jitterEllipse = new List<Vector2>();
        {
            const float a = 110f, b = 78f;
            const int n = 12;
            var rnd = new Random(20261007);
            float theta = 0f;
            jitterEllipse.Add(new Vector2(a, 0f));
            for (int i = 1; i <= n; i++)
            {
                // 基准步长按周长均分，再乘 0.5~1.6 的抖动（复现 60Hz 快速手画的点距分布）
                float baseStep = 553f / n;
                float step = (float)(baseStep * (0.5 + rnd.NextDouble() * 1.1));
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    acc += s * 0.0005f;
                    theta += 0.0005f;
                }
                float ang = theta - MathF.PI * 0.5f;
                jitterEllipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(jitterEllipse, ox + 1300f, oy + 760f);

        // ⑪ **点密度对照**（用户 2026-10-07 问"raw input 值不值"）：
        //    同一个椭圆、同一条弧长路径，按两种点距各画一条：
        //      · 左边 13 点 = 关 ink 时的真实点距（~68Hz × 2900px/s ≈ 43px 一点）
        //      · 右边 33 点 = 接上 raw input 之后的点距（~170Hz ≈ 17px 一点）
        //    左边是"我们现在能拿到的"，右边是"raw input 能拿到的"。
        //    两条都过同一套曲线代码，差别**只在采样密度**——所以这张图直接回答
        //    "多出来的那 60% 输入到底买到了什么"。
        void DensePair(float dx, int n, bool dots)
        {
            var pts = new List<Vector2>();
            const float a2 = 120f, b2 = 88f;
            for (int i = 0; i <= n; i++)
            {
                float ang = MathF.PI * 1.9f * i / n - MathF.PI * 0.95f;
                pts.Add(new Vector2(MathF.Cos(ang) * a2, -MathF.Sin(ang) * b2));
            }
            Add(pts, dx, oy + 1180f, dots);
        }
        DensePair(ox + 260f, 13, dots: true);    // 现在（关 ink 的真实密度）
        DensePair(ox + 760f, 33, dots: true);    // raw input 之后

        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string off = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-off" + ext);
        string on = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-on" + ext);

        // 折线那张：先关掉开关，整层重画（版本号会顶掉每条笔的几何缓存）
        bool wasEnabled = StrokeSmoothing.Enabled;
        StrokeSmoothing.SetEnabled(false);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok1 = ScreenProbe.SaveBmp(off, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);

        // 曲线那张：打开开关，同一批笔重画一遍
        StrokeSmoothing.SetEnabled(true);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok2 = ScreenProbe.SaveBmp(on, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);
        StrokeSmoothing.SetEnabled(wasEnabled);     // 出完图恢复原状态，别把后面的路径带偏

        Console.WriteLine(ok1 ? $"  折线：{off}" : "  折线图保存失败");
        Console.WriteLine(ok2 ? $"  曲线：{on}" : "  曲线图保存失败");
        Console.WriteLine();
        _quit = true;
    }
    private static (int count, float maxDist, float x, float y) DiffFarFromTip(
        byte[] a, byte[] b, int w, int h, int ox, int oy, float tipX, float tipY, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = MathF.Sqrt((px - tipX) * (px - tipX) + (py - tipY) * (py - tipY));
                if (dist <= minDist) continue;      // 笔尖附近本来就是新墨，允许变
                count++;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy);
    }

    /// <summary>
    /// 滚动比对：两张同尺寸图按"内容整体上移 <paramref name="shift"/> 像素"逐像素对
    /// （a[y] 应等于 b[y+shift]）。返回不匹配的像素数（亮度差 &gt; 24）。
    /// 用来找"滚动时某条横带没更新"——那会表现成一整行不匹配。
    /// </summary>
    private static int CountShiftMismatch(byte[] a, byte[] b, int w, int h, int shift)
    {
        if (shift <= 0 || shift >= h) return int.MaxValue;
        int bad = 0;
        for (int y = 0; y + shift < h; y++)
        {
            int ra = y * w * 4, rb = (y + shift) * w * 4;
            for (int x = 0; x < w; x++)
            {
                int oa = ra + x * 4, ob = rb + x * 4;
                int d = Math.Abs(a[oa] - b[ob]) + Math.Abs(a[oa + 1] - b[ob + 1]) + Math.Abs(a[oa + 2] - b[ob + 2]);
                if (d > 24) bad++;
            }
        }
        return bad;
    }
    private static int FirstLineCenter(byte[] cap, int w, int h, int x)
    {
        if (cap == null) return -1;
        int run = -1;
        for (int y = 0; y < h; y++)
        {
            int o = (y * w + x) * 4;
            bool dark = cap[o] < 225 && cap[o + 1] < 225 && cap[o + 2] < 225;
            if (dark) { if (run < 0) run = y; }
            else if (run >= 0) return (run + y - 1) / 2;
        }
        return -1;
    }

    /// <summary>两张同尺寸 BGRA 图的差异像素数（亮度差 &gt; 24）。</summary>
    private static int DiffCount(byte[] a, byte[] b, int w, int h)
    {
        int n = 0;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
            if (d > 24) n++;
        }
        return n;
    }

    /// <summary>
    /// 两张同尺寸 BGRA 图的差异，只看**离折线 <paramref name="path"/> 的前
    /// <paramref name="pathCount"/> 个点 &gt; minDist 的那些像素**。
    /// 返回（这样的像素数、其中最远那个到路径的距离、坐标）。
    /// </summary>
    private static (int count, float maxDist, float x, float y,
                    int minX, int minY, int maxX, int maxY) DiffFarFromPath(
        byte[] a, byte[] b, int w, int h, int ox, int oy,
        List<Vector2> path, int pathCount, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = DistToPath(path, pathCount, px, py);
                if (dist <= minDist) continue;      // B 自己画的 / 盖住的，允许变
                count++;
                if (i < minX) minX = i;
                if (i > maxX) maxX = i;
                if (j < minY) minY = j;
                if (j > maxY) maxY = j;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy, minX, minY, maxX, maxY);
    }

    /// <summary>点到折线（前 count 个点）的最短距离。</summary>
    private static float DistToPath(List<Vector2> path, int count, float x, float y)
    {
        int n = Math.Min(count, path.Count);
        if (n <= 0) return float.MaxValue;
        if (n == 1) return Vector2.Distance(path[0], new Vector2(x, y));
        float best = float.MaxValue;
        for (int i = 1; i < n; i++)
        {
            float d = DistToSegment(path[i - 1].X, path[i - 1].Y, path[i].X, path[i].Y, x, y);
            if (d < best) best = d;
        }
        return best;
    }

    private static float DistToSegment(float ax, float ay, float bx, float by, float px, float py)
    {
        float vx = bx - ax, vy = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - vx * t, dy = wy - vy * t;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
    private static (float[] width, float[] center) InkProfile(byte[] bgra, int w, int h)
    {
        var width = new float[w];
        var center = new float[w];
        for (int i = 0; i < w; i++)
        {
            int min = int.MaxValue, max = -1;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                if (bgra[o] + bgra[o + 1] + bgra[o + 2] < 600)
                {
                    if (j < min) min = j;
                    if (j > max) max = j;
                }
            }
            width[i] = max >= 0 ? max - min + 1 : -1f;
            center[i] = max >= 0 ? (min + max) * 0.5f : -1f;
        }
        return (width, center);
    }

    /// <summary>
    /// `--tailjumptest`：**预测尾"突突突往外跳"的专项检测**（2026-09-28 用户实测：
    /// "鼠标模式下手写板写字，末端总会突突突往外跳，Ink 开了就没有了"）。
    ///
    /// 症状的机理：鼠标模式下没有委托轨迹，预测尾由我们自己画；而鼠标/兼容模式的输入
    /// 是**突发**的（报几个点、停一下、再报几个点）。来一阵好数据 → 末速度算得大 →
    /// 尾巴甩到前面；间隔一超 `MaxGapMs`(20ms) → 速度清零 → 尾巴没了；再来数据又甩出来。
    ///
    /// 判据是**笔尖的逐帧轨迹**（品红墨的最右列）：
    ///   · `往前跳的最大幅度`：一帧里笔尖突然前进多少；
    ///   · `往后退的次数 / 最大后退`：尾巴消失时笔尖会**缩回去**——这就是"突突"的正身。
    /// 对照组 `--nopredict` 应当明显更稳（这就是这条测试的自证）。
    /// ⚠ 2026-09-29 起预测**默认关**（用户拍板），所以这条测试要显式 `--predict` 才有对照。
    /// </summary>
    /* [删除 2026-10-05] 预测尾"突突跳"检测 + MaxInkColumn 辅助：随老预测系统移除
       （原文备份见 `.revert/2026-10-05-渲染减法/`；恢复见 `已停用-渲染实验.md`）。
    private void TailJumpTest(bool noisy = false, bool predictOff = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 预测尾“突突跳”检测（合成鼠标，突发节奏）===");
        // 测试自己摆状态（不依赖启动默认值）：默认测"开预测"，`--off` 测对照
        PredictEnabled = !predictOff;
        Console.WriteLine($"  预测={(PredictEnabled ? "开" : "关（对照）")}；节奏={(noisy ? "忽大忽小 + 间隔不齐（像真鼠标）" : "固定步长 + 每 4 点一停")}");

        if (SkipIfNoSyntheticInput("预测尾跳跃检测（需要合成鼠标）")) { _quit = true; return; }

        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        PassThrough = false;
        SetColorFromUi(new Color4(1f, 0f, 1f, 1f));      // 品红：和光标/白底分得开
        Doc.InvalidateAll();
        SettleFrames(200);

        float y0 = _virtualY + _virtualH * 0.5f;
        float x0 = _virtualX + 400f;
        const int N = 90;

        // 先把这一步的"步长 + 间隔"定下来（确定性）：忽大忽小的步长是**鼠标加速**的样子，
        // 不齐的间隔是**报点节奏**的样子，两者一起才逼得出"尾巴一出一进"。
        var rnd = new Random(20260928);
        var steps = new float[N];
        var gaps = new int[N];
        float total = 0f;
        for (int i = 0; i < N; i++)
        {
            steps[i] = noisy ? 2f + (float)rnd.NextDouble() * 32f : 14f;
            gaps[i] = noisy ? 6 + rnd.Next(36) : (i % 4 == 3 ? 60 : 8);
            total += steps[i];
        }

        int bandW = (int)(total + 220f), bandH = 120;
        int bandX = (int)(x0 - 60f), bandY = (int)(y0 - bandH * 0.5f);
        var buf = new byte[bandW * bandH * 4];

        SendMouse((int)x0, (int)y0, 0);
        SettleFrames(120);
        SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        var tip = new List<(double t, float x)>();
        int sent = 0;
        float cx = x0;
        double nextMove = NowMs;
        while (!_quit && sent < N)
        {
            if (NowMs >= nextMove)
            {
                cx += steps[sent];
                sent++;
                SendMouse((int)cx, (int)y0, 0);
                nextMove = NowMs + gaps[sent - 1];
            }
            SettleFrames(16);                    // 一次采样 = 一帧
            if (ScreenProbe.CaptureRegionInto(buf, bandX, bandY, bandW, bandH))
            {
                int col = MaxInkColumn(buf, bandW, bandH, minRun: 5);
                tip.Add((NowMs, col >= 0 ? bandX + col : float.NaN));
            }
        }
        SendMouse((int)cx, (int)y0, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        float maxFwd = 0f, maxBack = 0f;
        int backCount = 0, measured = 0;
        for (int i = 1; i < tip.Count; i++)
        {
            if (float.IsNaN(tip[i].x) || float.IsNaN(tip[i - 1].x)) continue;
            float d = tip[i].x - tip[i - 1].x;
            measured++;
            if (d > maxFwd) maxFwd = d;
            if (d < 0f) { backCount++; if (-d > maxBack) maxBack = -d; }
        }
        float lastSent = cx;
        float lastTip = 0f;
        for (int i = tip.Count - 1; i >= 0; i--) if (!float.IsNaN(tip[i].x)) { lastTip = tip[i].x; break; }

        Console.WriteLine($"  采到 {tip.Count} 帧（有效 {measured} 对），画到 x={lastSent:F0}，"
                          + $"最后笔尖 x={lastTip:F0}（落后 {lastSent - lastTip:F0}px）");
        Console.WriteLine($"  往前跳：最大 {maxFwd:F0}px/帧");
        Console.WriteLine($"  往后退：{backCount} 次，最大 {maxBack:F0}px  ← “突突”的正身");
        bool ok = backCount <= 2 && maxFwd <= 30f;
        Console.WriteLine(ok
            ? "  PASS: 笔尖轨迹是稳的（没有反复回缩）"
            : $"  FAIL: 笔尖在往回缩（{backCount} 次）——尾巴在一出一进地弹");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 一块 BGRA 图里**最右边**那一列"够粗的品红墨"（≥ <paramref name="minRun"/> 个连续像素）。
    /// 要求"够粗"是为了把落点光标环排除掉（那是细线，竖着切只有两三个像素）。
    /// </summary>
    private static int MaxInkColumn(byte[] bgra, int w, int h, int minRun)
    {
        for (int i = w - 1; i >= 0; i--)
        {
            int run = 0;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                bool magenta = bgra[o] > 200 && bgra[o + 1] < 90 && bgra[o + 2] > 200;
                if (magenta) { if (++run >= minRun) return i; }
                else run = 0;
            }
        }
        return -1;
    }

    */

    private void CurveShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        float x0 = _virtualX + 200f, y0 = _virtualY + 180f;
        const float pitch = 620f;                            // 格子间距（横竖一样，看着是网格）

        // 造一条曲线：只有 BeginShapeAt 铺的那一个占位点，之后交给 Set*Box
        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }
        // 每格的左上角
        float CellX(int col) => x0 + col * pitch;
        float CellY(int row) => y0 + row * pitch;

        // ---- 第一行：抛物线的四种开口（**先选朝向，再用"顶点 + 经过点"两点画**） ----
        // 这就是用户 2026-09-20 定的口径：方向是"选"出来的（CurveAxis），大小由"经过那个点"反解。
        var dirs = new[] { CurveAxis.OpenUp, CurveAxis.OpenDown, CurveAxis.OpenRight, CurveAxis.OpenLeft };
        for (int col = 0; col < dirs.Length; col++)
        {
            float cx = CellX(col), cy = CellY(0);
            var v = new Vector2(cx + 280f, cy + 280f);
            var s = Make(Tool.Parabola, StrokeKind.Parabola, v.X, v.Y);
            s.CurveAxis = dirs[col];                              // ① 先选开口方向
            s.SetParabolaVertex(v.X, v.Y);                        // ② 定顶点
            // ③ 定"曲线经过的点"：取 `u = 1` 处的曲线点（`s = p/2、t = p`），
            //    反解出来正好是 p = 140（见 Stroke.ParabolaPThroughPoint），一个格子装得下。
            var (dir, perp) = Stroke.ParabolaBasis(dirs[col]);
            var q = v + perp * 140f + dir * 70f;
            s.SetParabolaThroughPoint(q.X, q.Y);
            Doc.AddStroke(s);
        }

        // ---- 第二行：双曲线两个方向 ＋ 正弦 / 余弦各一个周期 ----
        // 双曲线按真实的**两步**出图（第一步拖出**渐近线框**、第二步定"曲线过哪个点"），
        // 这样图上这两条跟老师真画出来的完全一致（朝向也是那一步定的）。
        // 渐近线框是**拖到哪就是哪**：`A = |dx|、B = |dy|`（2026-09-20 改成不再打对折）。
        var hX = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(0) + 280f, CellY(1) + 280f);
        hX.SetHyperbolaFromAsymptote(CellX(0) + 280f, CellY(1) + 280f,
                                     CellX(0) + 280f + 300f, CellY(1) + 280f + 120f, 8f);   // 斜率 0.4
        hX.SetHyperbolaThroughPoint(CellX(0) + 280f + 300f, CellY(1) + 280f + 104f);
        Doc.AddStroke(hX);          // 点更横 → 焦点在 x 轴（左右双曲线）

        var hY = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(1) + 280f, CellY(1) + 280f);
        hY.SetHyperbolaFromAsymptote(CellX(1) + 280f, CellY(1) + 280f,
                                     CellX(1) + 280f + 120f, CellY(1) + 280f + 300f, 8f);   // 斜率 2.5
        hY.SetHyperbolaThroughPoint(CellX(1) + 280f + 104f, CellY(1) + 280f + 300f);
        Doc.AddStroke(hY);          // 点更竖 → 焦点在 y 轴（上下双曲线）

        // 正弦：起点在轴上（第一个零点），**往上拖 = 先上后下**（就是课本的 y = sin x）。
        // ⚠ 2026-09-20 第十六批：**框宽就是一个周期**（用户："正弦和余弦用一个周期的图"）——
        // 所以这一张拖出来的是**一个周期**（300 宽、A = 75）。多周期那件事在下一格「波浪线」。
        // ⚠ 这一行的**纵向位置都写成 `CellY(1) + 本行内偏移`**：格子间距是 620，
        // 第三行（立体）从 `CellY(1) + 600` 就开始了 —— 偏移超过 600 就压在立体上了
        //（2026-09-20 第十六批把波浪线摆到 +760、当场压住圆台和球，出图才发现）。
        float sinX = x0 + 1250f;
        var sin = Make(Tool.Sine, StrokeKind.Sine, sinX, CellY(1) + 300f);
        sin.SetWaveBox(sinX, CellY(1) + 300f, sinX + 300f, CellY(1) + 225f, 8f);   // dy = −75 → A = 75
        Doc.AddStroke(sin);

        // 余弦：起点在**峰顶**，**往下拖 = 从峰顶往下**（就是课本的 y = cos x）。
        // 同样是**一个周期**（纵拖 = 峰 → 谷 = 2A，所以同样 75 的振幅要拖 150 高）。
        float cosX = x0 + 1600f;
        var cos = Make(Tool.Cosine, StrokeKind.Cosine, cosX, CellY(1) + 225f);
        cos.SetWaveBox(cosX, CellY(1) + 225f, cosX + 300f, CellY(1) + 375f, 8f);
        Doc.AddStroke(cos);

        // 波浪线（第十六批）：**多周期的正弦波**——横向拖的是"要画多长"、周期由振幅定
        //（`T = 1 × A`，用户定的"振幅 = 一个周期"）。这里 A = 70、画 210 宽 → **3 个整周期**，
        // 正是用户要的"很多个周期的波浪线"。
        // 摆在正弦 / 余弦下面一点（同一行里错开，三张并排看"一个周期 vs 多周期"最直观）。
        float wavX = x0 + 1950f;
        var wav = Make(Tool.Wave, StrokeKind.Wave, wavX, CellY(1) + 400f);
        wav.SetWaveBox(wavX, CellY(1) + 400f, wavX + 210f, CellY(1) + 330f, 8f);
        Doc.AddStroke(wav);

        // 正切（2026-09-20 第十五批）：**一支**，按下 = 原点、拖出以它为中心的框
        //（横向 = 半支长、纵向 = 可视半高，两条渐近线就落在框的左右两边）。
        // ⚠ 视觉半高有**下限比例 3:1**（见 Stroke.TangentMinAspect）：这里故意只拖 60 高，
        // 画出来会被顶到 240 —— 图上要看的正是"随手一拖就贴着渐近线"这件事。
        float tanX = x0 + 2560f, tanY = CellY(1) + 330f;
        var tan = Make(Tool.Tangent, StrokeKind.Tangent, tanX, tanY);
        tan.SetTangentBox(tanX, tanY, tanX + 80f, tanY + 60f, 8f);
        Doc.AddStroke(tan);

        // ---- 第三行：立体图形（2026-09-20 第五批，照 InkClass 的 case 6/7）----
        // **旋转体那一族**：一次拖出**外接矩形**就成（椭圆由矩形派生，
        // 扁率 = `Stroke.SolidEllipseRatio`），被挡住的那半圈是**细虚线**（辅助几何槽）。
        // 位置贴着第三行的格线上沿：再往上抬就会和第二行（双曲线 / 正弦）叠在一起，
        // 再往下压出图（1800 高）就会被裁掉——两头都试过，这个位置刚好。
        //
        // ⚠ 这一行原来放的是**圆柱 / 圆锥 / 长方体 / 四面体**——后两个 2026-09-20 第十二批
        // 撤了面板入口（见 ShapeRows），所以这里换成**用户现在真能拖出来的四个**：
        // 圆柱 / 圆锥 / 圆台 / 球。（长方体 / 四面体的画法没删，`--curvetest` 里那些
        // 几何断言照旧在管着它们，旧板书也照样打开。）
        float sy0 = CellY(2) - 20f;
        var cyl = Make(Tool.Cylinder, StrokeKind.Cylinder, CellX(0) + 130f, sy0);
        cyl.SetSolidBox(CellX(0) + 130f, sy0, CellX(0) + 430f, sy0 + 350f);
        Doc.AddStroke(cyl);

        var cone = Make(Tool.Cone, StrokeKind.Cone, CellX(1) + 130f, sy0);
        cone.SetSolidBox(CellX(1) + 130f, sy0, CellX(1) + 430f, sy0 + 350f);
        Doc.AddStroke(cone);

        // 圆台（2026-09-20 第十三批）：同一个外接矩形，上底是一圈**小一圈**的椭圆。
        var cfz = Make(Tool.ConeFrustum, StrokeKind.ConeFrustum, CellX(2) + 130f, sy0);
        cfz.SetSolidBox(CellX(2) + 130f, sy0, CellX(2) + 430f, sy0 + 350f);
        Doc.AddStroke(cfz);

        // 球（第十四批）：半径 = min(半宽, 半高) 的内切正圆 ＋ 赤道椭圆（近侧实线）。
        // 这里**故意拖一个正方**（350×350）——球本来就该是个圆。
        var sph = Make(Tool.Sphere, StrokeKind.Sphere, CellX(3) + 130f, sy0);
        sph.SetSolidBox(CellX(3) + 130f, sy0, CellX(3) + 480f, sy0 + 350f);
        Doc.AddStroke(sph);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 出图（2026-09-22）：**双曲线两档 ＋ 椭圆（带焦点）两档**摆成一张四格的图。
    /// 用法 `--conicshow [路径]`，默认 `reports/圆锥曲线-两档.bmp`。
    ///
    /// 为什么单开一张、不塞进 `--curveshow`：那一张的三行已经满了（第二行末尾离立体
    /// 只有 600 像素），塞进去会叠在一起（第十六批就这么出过一次错）。而这一批要看的
    /// 正是"**两档之间的差别**"——四格并排、同一套尺寸，一眼就能对比：
    ///   上排：双曲线**有 / 无**渐近线；下排：椭圆**有焦点三角形 / 只有焦点**。
    /// 椭圆的 P 用**默认位置**（椭圆左上方那个点，见 `Stroke.DefaultFocusPointU`）——
    /// 图上要看的正是"顶点**在椭圆上**、三角形在上半边"，和面板图标同一个位置。
    /// </summary>
    private void ConicShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        const float colPitch = 1440f, rowPitch = 720f;
        float x0 = _virtualX + 720f, y0 = _virtualY + 300f;

        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }

        // ---- 上排：双曲线两档（**同一个几何**，只差那两条虚线在不在）----
        // 画法按真实的两步来：第一步拖出**渐近线框**（+300, +120，斜率 0.4），
        // 第二步定"曲线经过哪个点"（更横 → 焦点在 x 轴）。
        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0;
            var h = Make(Tool.Hyperbola, StrokeKind.Hyperbola, cx, cy);
            h.SetHyperbolaFromAsymptote(cx, cy, cx + 300f, cy + 120f, 8f);
            h.SetHyperbolaThroughPoint(cx + 300f, cy + 104f);
            h.ShowAsymptotes = col == 0;                 // 左：有渐近线；右：无渐近线
            Doc.AddStroke(h);
        }

        // ---- 下排：椭圆（带焦点）两档（**同一个椭圆**，只差那两条边在不在）----
        // 中心 ＋ 外角点拖 300 × 160 → 宽椭圆，焦点在横轴上、离中心 ±254。
        // 左：有焦点三角形（**P 用默认位置**，也就是"顶点在椭圆上"的那个样子）；
        // 右：只有两个焦点。
        // ⚠ P 的默认角是**左上方**（120°，见 `Stroke.DefaultFocusPointU`）——图上看的就是
        // "三角形的顶点贴着椭圆那条线、在上半边"，和面板图标上画的是同一个位置。
        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0 + rowPitch;
            var e = Make(Tool.ConicEllipse, StrokeKind.ConicEllipse, cx, cy);
            // ⚠ **不要用 `SetShapeBox`**：那是"三角形 / 平行四边形"的外框写法（会写三个顶点），
            // 而椭圆的定义是"**中心 ＋ 外角点**"两个点 —— 用错的话画出来是个又细又高的怪东西
            //（第一版出图时就是这么翻的：a 和 b 全错了）。正确写法是"起手点当中心、再拖出外角点"。
            e.SetEnd(cx + 300f, cy + 160f);
            e.FocusTriangle = col == 0;
            Doc.AddStroke(e);
        }

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }
    private void PrismShowcase(string path)
    {
        BoardOn = true;                       // 白底：虚线压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);

        void Put(Tool tool, StrokeKind kind, int sides, float bx, float by, float dx, float dy)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink,
                Width = 6f * DpiScale, PrismSides = sides,
            };
            s.AddPoint(bx, by, 1f, 0);
            s.SetPrismBase(bx, by, bx + 340f, by + 140f);        // 底面外接框 340×140（压扁比 ≈ 0.41）
            s.SetPrismApex(bx + 170f + dx, by + 70f + dy);       // 顶上那个中心（dx/dy 决定直还是斜）
            Doc.AddStroke(s);
        }

        // 三行：棱柱 / 棱锥 / 棱台，每行都是**三/四/五/六**、都是直的（顶上那个中心正对底心上方 240）
        var rows = new (Tool tool, StrokeKind kind)[]
        {
            (Tool.Prism, StrokeKind.Prism),
            (Tool.Pyramid, StrokeKind.Pyramid),
            (Tool.Frustum, StrokeKind.Frustum),
        };
        float[] colX = { 200f, 820f, 1440f, 2060f };
        int[] colSides = { 3, 4, 5, 6 };
        for (int r = 0; r < rows.Length; r++)
        {
            float by = _virtualY + 420f + r * 400f;
            for (int c = 0; c < colX.Length; c++)
                Put(rows[r].tool, rows[r].kind, colSides[c], _virtualX + colX[c], by, 0f, -240f);
        }

        // 最后一行：**直 vs 斜**，三族各一对（同一个四棱，斜的那个顶心往右挪 200）
        float lastY = _virtualY + 420f + rows.Length * 400f;
        var pairs = new (Tool tool, StrokeKind kind, float x, bool slant)[]
        {
            (Tool.Prism, StrokeKind.Prism, 200f, false),
            (Tool.Prism, StrokeKind.Prism, 760f, true),
            (Tool.Pyramid, StrokeKind.Pyramid, 1320f, false),
            (Tool.Pyramid, StrokeKind.Pyramid, 1880f, true),
            (Tool.Frustum, StrokeKind.Frustum, 2440f, false),
        };
        foreach (var (tool, kind, x, slant) in pairs)
            Put(tool, kind, 4, _virtualX + x, lastY,
                slant ? 200f : 0f, slant ? -200f : -240f);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok
            ? $"  已保存 {path}（上三行：三/四/五/六 的 棱柱 / 棱锥 / 棱台，都是直的；末行：直 vs 斜）"
            : "  保存失败");
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
    private IntPtr _syntheticPen;

    // ---- 合成触摸（自检注入用）------------------------------------------
    //
    // 为什么触摸需要单独一份：**POINTER_TYPE_INFO 真实定义里中间是一个 union，
    // 最大成员是 POINTER_TOUCH_INFO（144 字节）**，而我们那个结构体只声明了 pen 分支
    // （120 字节）加 24 字节填充 → 152 字节，与真实步长（4 + 4 对齐 + 144）一致；
    // 而且两个分支的**开头都是同一份 POINTER_INFO**，
    // 所以按 pen 的字段名填、把 type 设成 PT_TOUCH 就能造出一个触摸触点。
    private IntPtr _syntheticTouch;

    private bool EnsureSyntheticTouch()
    {
        if (_syntheticTouch != IntPtr.Zero) return true;
        // maxCount = 10：触摸**可以同时有多个触点**——"第二根手指守卫"、≥3 指擦、
        // 双指手势的自检都要一次注入 2~5 个触点。
        _syntheticTouch = Native.CreateSyntheticPointerDevice(
            Native.PT_TOUCH, 10, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticTouch != IntPtr.Zero;
    }

    /// <summary>
    /// 注入合成触摸（**不带面积**）。`points` 是"这一刻**所有**按在屏上的触点"，
    /// 下标就是触点序号（系统按 pointerId 跟踪，所以每次调用都要把还按着的触点一起带上）。
    /// `contact=false` 表示全部抬起。
    /// </summary>
    private void SendTouches(bool contact, params (float x, float y)[] points)
    {
        var sized = new (float x, float y, float size)[points.Length];
        for (int i = 0; i < points.Length; i++) sized[i] = (points[i].x, points[i].y, 0f);
        SendTouchesSized(contact, sized);
    }

    /// <summary>
    /// 注入合成触摸（**可带接触面积**）：`size` = 接触矩形边长（物理像素），0 = 不报面积。
    /// 手掌擦的判据靠它（见 Touch.cs 的自适应基线），所以"手掌"用例必须能造出大面积。
    ///
    /// ⚠ 步长 = **托管结构体大小**（= 原生步长 152，见 Native 的注释）；
    /// `rcContact` 等偏移**运行时用 `Marshal.OffsetOf` 算**——不写魔法数字。
    /// （历史坑：漏掉 `touchFlags/touchMask` 时按 144 排、第二个触点整体错位丢失。）
    /// </summary>
    private unsafe void SendTouchesSized(bool contact, params (float x, float y, float size)[] points)
    {
        int stride = Marshal.SizeOf<Native.POINTER_TYPE_INFO>();
        int unionOff = (int)Marshal.OffsetOf<Native.POINTER_TYPE_INFO>(nameof(Native.POINTER_TYPE_INFO.pen));
        int maskOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.touchMask));
        int rcOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContact));
        int rcRawOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContactRaw));

        int n = Math.Max(1, points.Length);
        IntPtr buf = Marshal.AllocHGlobal(stride * n + 16);
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                var one = new Native.POINTER_TYPE_INFO();
                one.type = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerType = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerId = (uint)(i + 1);
                one.pen.pointerInfo.pointerFlags =
                    Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE
                    | (contact ? Native.POINTER_FLAG_INCONTACT : 0u)
                    | (i == 0 ? Native.POINTER_FLAG_PRIMARY : 0u);
                one.pen.pointerInfo.ptPixelLocationX = (int)points[i].x;
                one.pen.pointerInfo.ptPixelLocationY = (int)points[i].y;
                one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

                IntPtr dst = buf + i * stride;
                Marshal.StructureToPtr(one, dst, false);
                if (points[i].size > 0f)
                {
                    // 报面积：touchMask 置位 + rcContact / rcContactRaw 都填矩形。
                    float half = points[i].size * 0.5f;
                    int l = (int)(points[i].x - half), t = (int)(points[i].y - half);
                    int r = (int)(points[i].x + half), b = (int)(points[i].y + half);
                    Marshal.WriteInt32(dst + maskOff, (int)Native.TOUCH_MASK_CONTACTAREA);
                    Marshal.WriteInt32(dst + rcOff + 0, l);
                    Marshal.WriteInt32(dst + rcOff + 4, t);
                    Marshal.WriteInt32(dst + rcOff + 8, r);
                    Marshal.WriteInt32(dst + rcOff + 12, b);
                    Marshal.WriteInt32(dst + rcRawOff + 0, l);
                    Marshal.WriteInt32(dst + rcRawOff + 4, t);
                    Marshal.WriteInt32(dst + rcRawOff + 8, r);
                    Marshal.WriteInt32(dst + rcRawOff + 12, b);
                }
            }
            Native.InjectSyntheticPointerInput(_syntheticTouch, buf, (uint)points.Length);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool EnsureSyntheticPen()
    {
        if (_syntheticPen != IntPtr.Zero) return true;
        _syntheticPen = Native.CreateSyntheticPointerDevice(Native.PT_PEN, 1, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticPen != IntPtr.Zero;
    }

    /// <summary>注入一个合成笔采样点，走的是和真笔同一条 WM_POINTER 路径。</summary>
    private unsafe void SendPenPoint(float x, float y, uint pressure, bool contact, bool first)
    {
        uint flags = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE;
        if (contact) flags |= Native.POINTER_FLAG_INCONTACT;
        if (first) flags |= Native.POINTER_FLAG_NEW | Native.POINTER_FLAG_PRIMARY | Native.POINTER_FLAG_FIRSTBUTTON;

        var one = new Native.POINTER_TYPE_INFO();
        one.type = Native.PT_PEN;
        one.pen.pointerInfo.pointerType = Native.PT_PEN;
        one.pen.pointerInfo.pointerFlags = flags;
        one.pen.pointerInfo.ptPixelLocationX = (int)x;
        one.pen.pointerInfo.ptPixelLocationY = (int)y;
        one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        one.pen.penFlags = 0;
        one.pen.penMask = Native.PEN_MASK_PRESSURE;
        one.pen.pressure = pressure;

        // 单点：按**托管结构体步长**排（= 原生步长 152；单点其实无所谓，但别再写死旧数字）。
        IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.POINTER_TYPE_INFO>() + 16);
        try
        {
            Marshal.StructureToPtr(one, buf, false);
            Native.InjectSyntheticPointerInput(_syntheticPen, buf, 1);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
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
            Console.WriteLine($"  书写期间：{StrokesMeasured} 笔，每笔分配 平均 {AllocKbSum / StrokesMeasured:F1} KB"
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

    /// <summary>
    /// 一块区域的**平均亮度**（0..255，BT.601 权重）。给"遮罩压暗没压暗"这类判据用——
    /// 数像素（Count*）分不清"整块都暗了一点"和"少数像素很暗"。
    /// </summary>
    public static double AvgLuma(int x, int y, int w, int h)
    {
        var buf = CaptureRegion(x, y, w, h);
        if (buf.Length < 4) return 0;
        double sum = 0; int n = 0;
        for (int i = 0; i + 3 < buf.Length; i += 4)
        { sum += 0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2]; n++; }
        return n == 0 ? 0 : sum / n;
    }

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
        var buf = new byte[w * h * 4];
        return CaptureRegionInto(buf, x, y, w, h) ? buf : Array.Empty<byte>();
    }

    /// <summary>把一份 BGRA 缓冲原样写成 32 位 BMP（**不经屏幕**：抓过的那一帧要能留档）。</summary>
    public static bool SaveBuffer(string path, byte[] bgra, int w, int h) => WriteBmp(path, bgra, w, h);

    /// <summary>同 <see cref="CaptureRegion"/>，但把像素写进调用方给的缓冲。**逐帧抓屏的测量必须用它**：
    /// 每帧 new 一个 1400×360×4 ≈ 2MB 的数组会直接进 LOH，实测能把**一次笔画**刷出
    /// 64 次第 2 代 GC / 378 MB 分配——那量到的是测量工具自己，不是产品。
    /// （2026-09-28 拿 `--wetdrytest` 踩的，记在这里免得再犯。）
    /// </summary>
    public static bool CaptureRegionInto(byte[] dst, int x, int y, int w, int h)
    {
        if (dst == null || w <= 0 || h <= 0 || dst.Length < w * h * 4) return false;
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
            return false;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);
        Marshal.Copy(bits, dst, 0, w * h * 4);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return true;
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
    /// <summary>
    /// 单实例用的命名互斥体。**必须是静态字段**：句柄一释放，"已经在跑"这个判断就没了。
    /// 进程退出时由系统自动放弃（不需要手动 ReleaseMutex）。
    /// </summary>
    private static System.Threading.Mutex s_single;

    [STAThread]
    private static int Main(string[] args)
    {
        // **WinExe 默认不带控制台**（双击时不该弹黑框），但自检 / 出图几乎全是从命令行跑的，
        // 那些 `Console.WriteLine` 不能一起丢掉。两边兼顾：**有参数时**试着接上父进程的控制台
        //（接不上说明是从资源管理器双击出来的，那正好——安静启动）。
        if (args.Length > 0) TryAttachParentConsole();

        // **单实例**：这个程序是一层全屏置顶的覆盖层。开两个的后果不是"多一个窗口"，
        // 而是：两层叠在一起（写字落到哪一层看运气）、第二个实例的热键注册不上、
        // **PPT 联动两边都在做**（互相抢着翻页、抢着往同一个文件写批注）。
        //
        // ⚠ 自检里的 `--clicktarget` 是**故意再起一个进程**当"下层窗口"用的
        //   （`--passtest` / `--uitest` 都靠它），那不是产品实例、也不能被拦——
        //   拦了那两条用例就没法跑了。所以它单独放行。
        //
        // **整段都放进 try 里**：单实例判断曾经写在 try 外面，它一旦抛异常（见
        // AcquireSingleInstance 里 abandoned 那段）就绕过了下面的 ReportFatal，
        // GUI 版（没有控制台）直接静默消失。
        try
        {
            bool isHelper = Array.IndexOf(args, "--clicktarget") >= 0;
            if (!isHelper && !AcquireSingleInstance()) return 0;
            return new App().Run(args);
        }
        catch (Exception ex)
        {
            // 没有控制台时，"FATAL: ..."这句话等于没写——落到盘上 + 弹一句，
            // 至少让用户知道"它出错了、详情在哪"（从命令行跑时控制台也照旧有）。
            Console.WriteLine("FATAL: " + ex);
            ReportFatal(ex);
            return 1;
        }
    }

    /// <summary>接上父进程的控制台并重开 stdout（见 Main 里那一段）。</summary>
    private static void TryAttachParentConsole()
    {
        try
        {
            if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { /* 接不上就算了：不影响功能，只是看不到日志 */ }
    }

    /// <summary>
    /// 抢"唯一实例"。已经在跑 → 把那个实例的覆盖层窗口提到前面，返回 false。
    /// 用 `Local\` 前缀：**每个登录会话一个**（同一台机器两个用户各开一个互不打扰）。
    /// </summary>
    private static bool AcquireSingleInstance()
    {
        System.Threading.Mutex mutex;
        try
        {
            mutex = new System.Threading.Mutex(true, @"Local\InkTeach.SingleInstance", out bool first);
            if (first) { s_single = mutex; return true; }
        }
        catch (Exception ex)
        {
            // 互斥体本身建不出来（极少见）不该把软件拦死：按"没有别的实例"继续。
            Console.WriteLine("单实例互斥体创建失败，按“没有别的实例”继续：" + ex.Message);
            return true;
        }

        // 抢不到时**先等一小会儿再判**——"界面上的重启"那条路是"先起新进程、
        // 老进程随后才退"（见 Engine.RestartSelf：它 `_quit = true` 之后才走 Shutdown）。
        // 不等的话新进程一上来就看见互斥体还在老进程手里，直接退出——
        // 表现是"点了重启，软件没了"，而这是这类单实例保护最常见的坑。
        // 2 秒（每 50ms 试一次）对"老进程正在退"足够了；真的是"用户又双击了一次"，
        // 也就晚 2 秒看到那句提示，不影响什么。
        for (int i = 0; i < 40; i++)
        {
            try
            {
                if (mutex.WaitOne(50)) { s_single = mutex; return true; }
            }
            catch (System.Threading.AbandonedMutexException)
            {
                // **这一条就是"点了重启不回来"的真凶**（2026-09-28 修）：
                // 老进程退出时还攥着这个互斥体，内核把互斥体标记成 "abandoned"，
                // 于是这里的 WaitOne 不是返回 false，而是**抛 AbandonedMutexException**。
                // 抛异常 ≠ 没抢到：按 MSDN，异常抛出时等待已经满足、所有权已经转过来了
                // （"the wait is satisfied"）——所以当成"抢到了"继续启动才是对的。
                //
                // 之前没接这个异常：它一路冒到 Main 外面（那时单实例判断写在 try 之外），
                // GUI 版没有控制台、也没有 crash 日志，表现就是"老窗口关了、新窗口再也不出来"。
                s_single = mutex;
                return true;
            }
            catch (Exception ex)
            {
                // 其它意外也别把软件拦死（比如权限/句柄问题）。
                Console.WriteLine("单实例判断出错，按“没有别的实例”继续：" + ex.Message);
                return true;
            }
        }

        mutex.Dispose();
        Console.WriteLine("已经有 InkTeach 在运行了（单实例保护）：把它的窗口提到前面，这一个退出。");
        BringExistingToFront();
        return false;
    }

    /// <summary>
    /// 找到已经在跑的那个实例的窗口，把它激活。找不到就提示一句
    /// （覆盖层是透明的、还可能被贴边隐藏收成底部一条，用户多半以为"双击没反应"）。
    ///
    /// **认窗口靠"窗口类名前缀"，不靠标题**：实测从别的进程读这几个窗口的标题，
    /// 拿到的是 `"I"`（建窗口时那个名字被截成了一个字符，原因没深究——
    /// 那几个窗口都是 WS_EX_TOOLWINDOW，不进 Alt+Tab，标题本来也没人看），
    /// 而**类名读出来是完整的** `InkTeachOverlay_<guid>`（见 OverlayWindow 注册窗口类）。
    /// 同一批里还有两个"接输入小窗"（`Engine` 里建的界面面板 / PPT 条），
    /// 它们不算"实例的窗口"，所以按**面积挑最大的那张**——覆盖层是整块屏幕那块。
    /// </summary>
    private static void BringExistingToFront()
    {
        IntPtr found = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            var sb = new System.Text.StringBuilder(128);
            if (Native.GetClassNameW(h, sb, sb.Capacity) <= 0) return true;
            if (!sb.ToString().StartsWith("InkTeachOverlay_", StringComparison.Ordinal)) return true;

            if (!Native.GetWindowRect(h, out var r)) return true;
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; found = h; }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            Native.SetForegroundWindow(found);
            return;
        }
        Native.MessageBoxW(IntPtr.Zero,
            "InkTeach 已经在运行了。\n\n它是一层透明的覆盖层，看屏幕底部中间那条工具条。",
            "InkTeach", Native.MB_OK);
    }

    /// <summary>崩溃时把详情写到盘上并弹一句（WinExe 双击启动时没有控制台可看）。</summary>
    private static void ReportFatal(Exception ex)
    {
        string path = "";
        try
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InkTeach", "last-crash.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n");
        }
        catch { }

        Native.MessageBoxW(IntPtr.Zero,
            "InkTeach 启动失败，已经退出。\n\n详情：" + (path.Length > 0 ? path : "（写日志也失败了）"),
            "InkTeach", Native.MB_OK | Native.MB_ICONERROR);
    }
}
