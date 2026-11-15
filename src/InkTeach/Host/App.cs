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

        // **输入黑匣子**（`--reclive <文件>`）：逐条记录喂进引擎的输入（起笔/采样/收笔 + 引擎时标）。
        // 配合 `--replayinput` 离线 1:1 复现——"偶尔跳一下"这类说不清的问题靠它留证据。
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--reclive")
            {
                var bb = System.IO.Path.GetFullPath(args[i + 1]);
                // ⚠ 表头在**参数解析前**生成，别读引擎的静态值（那时还是默认）——按 args 自己算：
                bool pred = args.Contains("--predict2") && !args.Contains("--nopredict2");   // 默认关（2026-10-16 起）
                InputBlackBox.Open(bb,
                    $"version {UpdateFeed.CurrentVersion} pred {(pred ? 1 : 0)}");
                Console.WriteLine($"[黑匣子] 输入日志：{bb}");
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
        // 板书历史（0 号页快照）同理：自检绝不写用户的历史目录。
        if (_selfCheckMode && BoardHistory.DirOverride == null)
        {
            BoardHistory.DirOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-history");
            try { if (Directory.Exists(BoardHistory.DirOverride)) Directory.Delete(BoardHistory.DirOverride, true); } catch { }
            BoardHistory.CloseSession();
        }
        // 运行日志同理（2026-10-16）：自检**不往用户的日志目录灌**——一轮自检的日志
        // 全落临时目录（也给"日志"那几条用例一个干净起点）。
        if (_selfCheckMode && DiagLog.DirOverride == null)
            DiagLog.Retarget(Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-logs"));

        // 「导出日志…」的环境摘要（屏幕 / 缩放这些建了窗口才算得准）：只挂**惰性取值**，
        // 打包那一刻才调用。
        DiagLog.EnvExtra = () => $"屏幕 {_virtualW}×{_virtualH} · 缩放 {DpiScale:0.00} · 窗口 {_windows.Count} 块"
            + $" · GPU {DiagLog.GpuName} · {DiagLog.RefreshHz}Hz · 软件渲染 {(DiagLog.GpuSoftware ? "是" : "否")}";
        // 自检里**不弹"另存为"对话框**：它会阻塞等消息，而自检是自己抽消息推进的，
        // 一弹就卡到超时（`--selftest` 会逐个点操作条上的按钮，点到"导出"就中招）。
        // 导出那条链由 `--iotest` 走"不弹框、直接写指定路径"验，见 ExportSelectionToPathForTest。
        if (_selfCheckMode) ExportDialogEnabled = false;

        // 设备卡（台数 / 型号 / 活跃统计；2026-10-17 用户定）：安装 ID 首次生成、之后不变。
        // 匿名 GUID，不含任何个人信息；只在产品模式上传（自检/测试模式 ExportDialogEnabled=false，不传）。
        {
            string lid = GetUiPref("log.id");
            if (string.IsNullOrEmpty(lid)) { lid = Guid.NewGuid().ToString("N"); SetUiPref("log.id", lid); }
            DiagLog.DeviceId = lid;
            string firstDay = GetUiPref("log.first");
            if (string.IsNullOrEmpty(firstDay))
            {
                firstDay = DateTime.Now.ToString("yyyy-MM-dd");
                SetUiPref("log.first", firstDay);
            }
            DiagLog.InstallFirstDay = firstDay;
            Console.WriteLine($"[设备] {DiagLog.MachineModel} · {DiagLog.CpuName} · RAM {DiagLog.RamGb}G · id {(lid.Length >= 8 ? lid[..8] : lid)}");
        }

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

        if (mode == "--help")
        {
            // 2026-10-17：完整参数表 + 键位表从"每次启动刷屏"挪到这里
            //（用户："这里好多命令，能不能精简一些"）。打印完就走，不启动界面。
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            PrintUsageHelp();
        }
        else if (mode == "--selftest")
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
        else if (mode == "--shapepaneltest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapePanelTest();
            _quit = true;
        }
        else if (mode == "--bandactiontest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BandActionTest();
            _quit = true;
        }
        else if (mode == "--qrtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            QrTest();
            _quit = true;
        }
        else if (mode == "--shapeaudittest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeAuditTest();
            _quit = true;
        }
        else if (mode == "--librarysizetest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LibrarySizeMemoryTest();
            _quit = true;
        }
        else if (mode == "--aitest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AiTest();
        }
        else if (mode == "--mathcorpus")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string file = args.Length > 1 ? args[1] : "调研/公式语料/高中数学-公式语料.txt";
            int ok = MathRenderer.RenderCorpus(file, out int total);
            ExitCode = ok == total && total > 0 ? 0 : 1;
            _quit = true;
        }
        else if (mode == "--b0textbox")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            Doc.Clear();
            Doc.ClearHistory();
            SetBoardFromUi(true);
            SetBoardColorFromUi(new Vortice.Mathematics.Color4(0.10f, 0.18f, 0.22f, 1f));
            // 在文本框下面铺一条亮绿粗线：编辑背景真透明时，线应从文字后面透出来。
            var band = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Vortice.Mathematics.Color4(0.20f, 0.85f, 0.55f, 1f),
                Width = 44f,
            };
            float midY = _virtualY + _virtualH * 0.5f;
            for (int i = 0; i <= 20; i++)
                band.AddPoint(_virtualX + _virtualW * 0.30f + i * (_virtualW * 0.40f / 20f),
                              midY, 0.5f, i * 8);
            Doc.AddStroke(band);
            Doc.InvalidateAll();
            SettleFrames(180);

            InsertPlainTextBoxForTest("");
            var tr = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (tr?.TextBox != null)
            {
                tr.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                tr.TextBox.Ink = tr.Color;
                tr.TextBox.Paper = false;
                BeginTextBoxEdit(tr);
                Console.WriteLine("B0 IME context=" + TextBoxEditHasImmContextForTest);
                SetTextBoxEditTextForTest("透明文本框：白字压在深色板上\n第二行 ABC 123\n第三行：光标、选区、IME 都由 RichEdit 负责。");
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/b0-textbox-dark.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                SetTextBoxEditSelectionForTest(0, 6);
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-selection.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) TypeTextBoxEditCharForTest('字');
                sw.Stop();
                Console.WriteLine($"B0 输入 100 字耗时：{sw.ElapsedMilliseconds} ms");
                EndTextBoxEditFromUi(true);
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            }

            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("纸面文本框：Paper=true\n第二行：圆角、底色和画布一致");
            var paper = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (paper?.TextBox != null)
            {
                paper.TextBox.Paper = true;
                paper.TextBox.Fill = TextBoxBackground.Fill(1, BoardColor, card: false);
                paper.TextBox.Border = TextBoxBackground.Border(1, BoardColor, card: false);
                paper.Color = new Vortice.Mathematics.Color4(0.1f, 0.1f, 0.12f, 1f);
                paper.TextBox.Ink = paper.Color;
                BeginTextBoxEdit(paper);
                SetTextBoxEditTextForTest("纸面文本框：Paper=true\n第二行：圆角、底色和画布一致");
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/b0-textbox-paper.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
            }

            // 真实逐字输入（不是 SetText）：验证光标位置、字号和字是否对得上。
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("");
            var typed = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (typed?.TextBox != null)
            {
                typed.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                typed.TextBox.Ink = typed.Color;
                typed.TextBox.Paper = false;
                typed.TextBox.AutoWidth = false;
                typed.TextBox.Width = 600f;
                typed.TextBox.InvalidateLayout();
                BeginTextBoxEdit(typed);
                SettleFrames(220);          // 等快照/宿主显示
                TypeTextBoxEditCharForTest('你');
                TypeTextBoxEditCharForTest('怎');
                TypeTextBoxEditCharForTest('么');
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/b0-textbox-realtype.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                Doc.SelectOnly(Array.Empty<Stroke>());
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-realtype-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                // 小字号（18px）：验证顶部留白按字号缩放，提交不跳。
                typed.TextBox.FontSize = 9f;
                typed.TextBox.AutoHeight = true;
                typed.TextBox.Width = 300f;
                typed.TextBox.InvalidateLayout();
                BeginTextBoxEdit(typed);
                SettleFrames(200);
                TypeTextBoxEditCharForTest('你');
                TypeTextBoxEditCharForTest('怎');
                SettleFrames(200);
                ScreenProbe.SaveBmp("reports/b0-textbox-smallfont.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                Doc.SelectOnly(Array.Empty<Stroke>());
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-smallfont-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                // 27px：第三点校准 RichEdit / DirectWrite 的首行留白差。
                typed.TextBox.FontSize = 13.5f;
                typed.TextBox.AutoHeight = true;
                typed.TextBox.Width = 300f;
                typed.TextBox.InvalidateLayout();
                BeginTextBoxEdit(typed);
                SettleFrames(200);
                TypeTextBoxEditCharForTest('你');
                SettleFrames(200);
                ScreenProbe.SaveBmp("reports/b0-textbox-midfont.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                Doc.SelectOnly(Array.Empty<Stroke>());
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-midfont-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            }

            // 点击文字中间：caret 应落在点击处附近，而不是偏一截。
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("你怎么又来了 ABC");
            var clickBox = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (clickBox?.TextBox != null)
            {
                clickBox.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                clickBox.TextBox.Ink = clickBox.Color;
                clickBox.TextBox.Paper = false;
                clickBox.TextBox.AutoWidth = false;
                clickBox.TextBox.Width = 600f;
                BeginTextBoxEdit(clickBox);
                SettleFrames(220);
                var cb = clickBox.PaddedBounds;
                float cpad = 18f * MathF.Max(0.5f, clickBox.TextBox.Unit);
                int clickX = (int)MathF.Round(cb.MinX + 2f + cpad + 90f);
                int clickY = (int)MathF.Round(cb.MinY + 2f + ViewOffsetY + cpad + 24f);
                SendMouse(clickX, clickY, 0);
                SendMouse(clickX, clickY, Native.MOUSEEVENTF_LEFTDOWN);
                SendMouse(clickX, clickY, Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(180);
                var cp = TextBoxEditCaretPointForTest;
                Console.WriteLine($"B0 点击 ({clickX},{clickY}) caret={TextBoxEditCaretForTest} point={cp.X},{cp.Y} host={clickX - cp.X},{clickY - cp.Y}");
                ScreenProbe.SaveBmp("reports/b0-textbox-click.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
            }

            // 已有文字的重新编辑：快照里不能残留旧字（否则会出现双份/重影）。
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("旧文字：重新编辑不应有残影\n第二行 ABC");
            var again = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (again?.TextBox != null)
            {
                again.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                again.TextBox.Ink = again.Color;
                again.TextBox.Paper = false;
                BeginTextBoxEdit(again);
                SetTextBoxEditTextForTest("旧文字：重新编辑不应有残影\n第二行 ABC\n第三行：快照里不能有旧字");
                SettleFrames(240);
                ScreenProbe.SaveBmp("reports/b0-textbox-reedit.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
            }
            Console.WriteLine("B0 出图：reports/b0-textbox-*.bmp");
            _quit = true;
        }
        else if (mode == "--figure")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FigureTest();
            _quit = true;
        }
        else if (mode == "--aiplot")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AiPlotTest();
            _quit = true;
        }
        else if (mode == "--plottest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PlotSuiteTest(args.Length > 1 ? args[1] : null);
            _quit = true;
        }
        else if (mode == "--imgcardtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageCardTest();
            _quit = true;
        }
        else if (mode == "--plotfromcard")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PlotFromCardTest();
            _quit = true;
        }
        else if (mode == "--plotlive")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PlotLiveTest(args.Length > 1 ? args[1] : null);
            _quit = true;
        }
        else if (mode == "--facesprobe")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FacesProbe();
            _quit = true;
        }
        else if (mode == "--arrowcleartest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ArrowClearTest();
            _quit = true;
        }
        else if (mode == "--imgcardlive")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageCardLiveTest(args.Length > 1 ? args[1] : null);
            _quit = true;
        }
        else if (mode == "--imgcardreplay")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageCardReplayTest(args.Length > 1 ? args[1] : null,
                                args.Length > 2 ? args[2] : null);
            _quit = true;
        }
        else if (mode == "--figurelive")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FigureLiveTest();
            _quit = true;
        }
        else if (mode == "--figurequad")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FigureQuadTest();
            _quit = true;
        }
        else if (mode == "--textbg")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TextBackgroundTest();
            _quit = true;
        }
        else if (mode == "--newtext")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            NewTextModelTest();
            _quit = true;
        }
        else if (mode == "--textboxsim")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TextBoxSimTest();
            _quit = true;
        }
        else if (mode == "--textboxedit")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("文本框编辑对齐检查\n第二行：中文混排，没有公式。");
            var st = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (st != null)
            {
                BeginTextBoxEdit(st);
                SetTextBoxEditTextForTest("文本框编辑对齐检查\n第二行：中文混排，没有公式。\n第三行：看看光标和文字是否对齐。");
                SettleFrames(300);
                ScreenProbe.SaveBmp("reports/textbox-edit.bmp", (int)_virtualX, (int)_virtualY,
                                    (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(false);
            }
            Console.WriteLine("出图：reports/textbox-edit.bmp");

            // 文本工具专项：在屏幕右侧贴近边缘处逐字输入，框必须向左长/换行，
            // 不能把前面的汉字挤出可视区；然后点画布空白，应当同一下出新输入框。
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Text);
            float edgeX = _virtualX + _virtualW - 40f * DpiScale;
            float edgeY = _virtualY + _virtualH * 0.35f;
            if (TextToolPointerDown(edgeX, edgeY))
            {
                // 逐字追加，模拟真实键盘：每次插入都会触发"边打边长"和 RichEdit 重排。
                for (int n = 1; n <= 12; n++)
                {
                    TypeTextBoxEditCharForTest('你');
                    SettleFrames(160);
                    var s = Doc.Selected.Count > 0 ? Doc.Selected[0] : null;
                    var b = s?.PaddedBounds ?? default;
                    Console.WriteLine($"右侧输入 {n} 字：x={b.MinX:F0}..{b.MaxX:F0} "
                                    + $"宽={s?.TextBox?.Width:F0} 屏右={_virtualX + _virtualW:F0}");
                    if (n is 2 or 6 or 12)
                        ScreenProbe.SaveBmp($"reports/textbox-edit-right-{n}.bmp",
                            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                }
                int beforeClick = TextBoxCountForTest;
                var old = Doc.Selected.Count > 0 ? Doc.Selected[0] : null;
                int cxp = (int)(_virtualX + 40f), cyp = (int)(_virtualY + 40f);
                SendMouse(cxp, cyp, 0); SettleFrames(60);
                SendMouse(cxp, cyp, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(80);
                SendMouse(cxp, cyp, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
                Console.WriteLine($"点空白：editing={TextBoxEditing} 文本框 {beforeClick}"
                                + $"→{TextBoxCountForTest} 旧框仍选中="
                                + (old != null && Doc.Selected.Contains(old)));
                ScreenProbe.SaveBmp("reports/textbox-edit-after-click.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                SettleFrames(160);

                // 旧框：未选中时悬停应是 I 字；单击选中后应是移动十字；按住拖要真的能移动。
                if (old?.TextBox is { Role: TextBoxRole.Normal } && Doc.Strokes.Contains(old))
                {
                    var ob = old.PaddedBounds;
                    int ocx = (int)((ob.MinX + ob.MaxX) * 0.5f);
                    int ocy = (int)((ob.MinY + ob.MaxY) * 0.5f + ViewOffsetY);
                    SendMouse(ocx, ocy, 0); SettleFrames(140);
                    var hoverCursor = ComputeCursorKind();
                    SendMouse(ocx, ocy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(70);
                    SendMouse(ocx, ocy, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
                    bool selected = Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], old);
                    SendMouse(ocx, ocy, 0); SettleFrames(140);
                    var selCursor = ComputeCursorKind();
                    var p0 = old.Points.Count > 0 ? old.Points[0] : default;
                    float beforeX = p0.X, beforeY = p0.Y;
                    SendMouse(ocx, ocy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(70);
                    SendMouse(ocx - 140, ocy + 90, 0); SettleFrames(160);
                    SendMouse(ocx - 140, ocy + 90, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
                    var p1 = old.Points.Count > 0 ? old.Points[0] : default;
                    Console.WriteLine($"旧框悬停：光标={hoverCursor} 选中={selected} "
                                    + $"选中后光标={selCursor} 移动 Δ=({p1.X - beforeX:F0},{p1.Y - beforeY:F0})");
                    ScreenProbe.SaveBmp("reports/textbox-edit-moved.bmp",
                        (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                    // 双击进入编辑：真双击（松手才兑现）；然后继续打字，宽度仍应自动长。
                    if (selected)
                    {
                        var ob2 = old.PaddedBounds;
                        int ox2 = (int)((ob2.MinX + ob2.MaxX) * 0.5f);
                        int oy2 = (int)((ob2.MinY + ob2.MaxY) * 0.5f + ViewOffsetY);
                        for (int k = 0; k < 2; k++)
                        {
                            SendMouse(ox2, oy2, 0); SettleFrames(60);
                            SendMouse(ox2, oy2, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(70);
                            SendMouse(ox2, oy2, Native.MOUSEEVENTF_LEFTUP); SettleFrames(160);
                        }
                        bool dblOk = TextBoxEditing;
                        if (!dblOk) BeginTextBoxEdit(old);   // 兜底，保证后面的宽高检查仍能跑
                        float wBefore = old.TextBox?.Width ?? 0f;
                        TypeTextBoxEditCharForTest('你');
                        TypeTextBoxEditCharForTest('好');
                        SettleFrames(220);
                        float wAfter = old.TextBox?.Width ?? 0f;
                        Console.WriteLine($"双击={dblOk} 重新编辑追加 2 字：宽 {wBefore:F0}→{wAfter:F0} "
                                        + $"autoW={old.TextBox?.AutoWidth}");
                        ScreenProbe.SaveBmp("reports/textbox-edit-reedit.bmp",
                            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                        EndTextBoxEditFromUi(true);

                        // 文本专项：存读一圈，确认 v27 的 AutoWidth 字节和文字都没丢。
                        var bytes = InkSerializer.SaveStrokes(new[] { old });
                        var loaded = InkSerializer.LoadStrokes(bytes, out _);
                        var lb = loaded.Count > 0 ? loaded[0].TextBox : null;
                        Console.WriteLine($"存读：文本={lb?.Text?.Length ?? 0} 字 "
                                        + $"autoW={lb?.AutoWidth} 宽={lb?.Width:F0}");
                    }

                    // 靠近底部主状态栏的文本框：确认选中后从上半部分也能往上拖。
                    Doc.Clear();
                    Doc.ClearHistory();
                    SetToolFromUi(Tool.Text);
                    float bottomX = _virtualX + _virtualW * 0.45f;
                    float bottomY = _virtualY + _virtualH - 210f * DpiScale;
                    if (TextToolPointerDown(bottomX, bottomY))
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            AppendTextBoxEditTextForTest("底部文字测试\n");
                        }
                        SettleFrames(200);
                        EndTextBoxEditFromUi(true);
                        if (Doc.Strokes.Count > 0)
                        {
                            var bottom = Doc.Strokes[^1];
                            Doc.SelectOnly(new[] { bottom });
                            var bb = bottom.PaddedBounds;
                            int bcx = (int)((bb.MinX + bb.MaxX) * 0.5f);
                            int bcy = (int)(bb.MinY + 24f + ViewOffsetY);
                            var q0 = bottom.Points.Count > 0 ? bottom.Points[0] : default;
                            float qx = q0.X, qy = q0.Y;
                            SendMouse(bcx, bcy, 0); SettleFrames(80);
                            SendMouse(bcx, bcy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(80);
                            SendMouse(bcx, bcy - 120, 0); SettleFrames(160);
                            SendMouse(bcx, bcy - 120, Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);
                            var q1 = bottom.Points.Count > 0 ? bottom.Points[0] : default;
                            Console.WriteLine($"底部文本框：框={bb.MinY:F0}..{bb.MaxY:F0} "
                                            + $"拖动 Δ=({q1.X - qx:F0},{q1.Y - qy:F0})");
                            ScreenProbe.SaveBmp("reports/textbox-edit-bottom.bmp",
                                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                        }
                    }
                }
            }
            _quit = true;
        }
        else if (mode == "--mathtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int ok = MathRenderer.RenderSamples("reports/math-spike", out int total);
            Console.WriteLine($"公式 spike：{ok}/{total} 张成功，输出 reports/math-spike/");
            ExitCode = ok == total ? 0 : 1;
            _quit = true;
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
        else if (mode == "--aishow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AiShow(args.Length > 1 ? args[1] : "reports/ai-card.bmp");
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
        else if (mode == "--inkboardpng")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            InkBoardPng(args.Length > 1 ? args[1] : "",
                        args.Length > 2 ? args[2] : "reports/board.png");
            _quit = true;
        }
        else if (mode == "--glyphsave")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphSaveTest(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                          args.Length > 2 ? args[2] : "函数向量",
                          args.Length > 3 ? args[3] : "reports/glyphsave.png",
                          args.Length > 4 && float.TryParse(args[4], out float gs2) ? gs2 : 120f);
            _quit = true;
        }
        else if (mode == "--handtextbox")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandTextBoxPng(args.Length > 1 ? args[1] : "",
                           args.Length > 2 ? args[2] : "reports/handtextbox.png",
                           args.Length > 3 && float.TryParse(args[3], out float htl) ? htl : 480f);
            _quit = true;
        }
        else if (mode == "--handbbox")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandBboxPng(args.Length > 1 ? args[1] : "",
                        args.Length > 2 ? args[2] : "",
                        args.Length > 3 ? args[3] : "reports/handbbox.png",
                        !args.Contains("--safe"));
            _quit = true;
        }
        else if (mode == "--handblocks")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HandBlocksDump(args.Length > 1 ? args[1] : "");
            _quit = true;
        }
        else if (mode == "--glypherase")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphErasePng(args.Length > 1 ? args[1] : "reports/glypherase-before.png",
                          args.Length > 2 ? args[2] : "reports/glypherase-after.png");
            _quit = true;
        }
        else if (mode == "--selaishow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelAiShow(args.Length > 1 ? args[1] : "reports/selai.bmp");
            _quit = true;
        }
        else if (mode == "--toastshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ToastShow(args.Length > 1 ? args[1] : "reports/toast.bmp");
            _quit = true;
        }
        else if (mode == "--selmenutest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelMenuTest();
            _quit = true;
        }
        else if (mode == "--selbeautifyshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelBeautifyShow(args.Length > 1 ? args[1] : "reports/sel-beautify.bmp");
        }
        else if (mode == "--textstyle")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            TextStyleShow(args.Length > 1 ? args[1] : "reports/textstyle.bmp");
            _quit = true;
        }
        else if (mode == "--textcolor")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            TextColorShow(args.Length > 1 ? args[1] : "reports/textcolor.bmp");
            _quit = true;
        }
        else if (mode == "--fontlistshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FontListShow(args.Length > 1 ? args[1] : "reports/fontlist.bmp");
            _quit = true;
        }
        else if (mode == "--tipsweep")
        {
            // 语言审计：把所有悬停提示过一遍 Loc.Tr（配合 INKTEACH_LOC_AUDIT=1）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            (Ui as InkUi.FullUi)?.AuditTipsForTest();
            AuditEngineTips();
            _quit = true;
        }
        else if (mode == "--settingsaudit")
        {
            // 无窗口自检：不建覆盖层、不注入鼠标、不抢前台；用户在用电脑时也能跑。
            return SettingsAudit.Run();
        }
        else if (mode == "--pageaudit")
        {
            // A1：PageHost 页面栈/生命周期/尺寸/标题几何；无窗口、不抢前台。
            return PageAudit.Run();
        }
        else if (mode == "--textinputaudit")
        {
            // A2：文字宿主 Begin/Commit/Cancel/校验/多行；隐藏窗口、不抢前台。
            return TextInputAudit.Run();
        }
        else if (mode == "--overlayaudit")
        {
            // A4.0：浮层优先级/捕获/Esc/点外/阻断；无窗口、不抢前台。
            return OverlayAudit.Run();
        }
        else if (mode == "--apiinputshot")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            if (CurrentUi is not InkUi.FullUi ui)
            {
                Console.WriteLine("FAIL：UI 不是 FullUi");
                ExitCode = 1;
                _quit = true;
                return ExitCode;
            }
            ui.OpenApiPageForTest();
            SettleFrames(300);
            int hit = 401;
            if (args.Length > 2 && int.TryParse(args[2], out int h)) hit = h;
            ui.ApiClickForTest(hit);      // 默认服务地址输入框：进入编辑态，截原生 EDIT
            SettleFrames(300);
            string path = args.Length > 1 ? args[1] : "reports/api-input-align.bmp";
            bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY,
                                          (int)_virtualW, (int)_virtualH);
            Console.WriteLine((ok ? "已写出 " : "写出失败 ") + path);
            ExitCode = ok ? 0 : 1;
            _quit = true;
        }
        else if (mode == "--apimenushot")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            if (CurrentUi is not InkUi.FullUi ui)
            {
                Console.WriteLine("FAIL：UI 不是 FullUi");
                ExitCode = 1;
                _quit = true;
                return ExitCode;
            }
            ui.OpenApiPageForTest();
            SettleFrames(300);
            ui.ApiClickForTest(402);      // 先进入 Key 编辑
            SettleFrames(200);
            ui.ApiClickForTest(400);      // 再开服务商下拉：EDIT 必须收起
            SettleFrames(300);
            string path = args.Length > 1 ? args[1] : "reports/api-menu.bmp";
            bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY,
                                          (int)_virtualW, (int)_virtualH);
            bool inputClosed = !ui.ApiInputEditingForTest;
            bool menuOpen = ui.ApiProviderMenuOpenForTest;
            Console.WriteLine($"inputClosed={inputClosed} menuOpen={menuOpen}");
            Console.WriteLine((ok ? "已写出 " : "写出失败 ") + path);
            ExitCode = ok && inputClosed && menuOpen ? 0 : 1;
            _quit = true;
        }
        else if (mode == "--aiapishow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            AiApiShow(args.Length > 1 ? args[1] : "reports/aiapi.bmp");
            _quit = true;
        }
        else if (mode == "--apiconfig")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ApiConfigTest();
            _quit = true;
        }
        else if (mode == "--listmodels")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ListModelsTest();
            _quit = true;
        }
        else if (mode == "--handocr")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandOcrPng(args.Length > 1 ? args[1] : "",
                       args.Length > 2 ? args[2] : "",
                       args.Length > 3 ? args[3] : "reports/handocr.png",
                       args.Length > 4 && float.TryParse(args[4], out float tl2) ? tl2 : 480f,
                       args.Contains("--real"),
                       args.Contains("--allformulas"));
            _quit = true;
        }
        else if (mode == "--handreplace")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandReplacePng(args.Length > 1 ? args[1] : "",
                           args.Length > 2 ? args[2] : "",
                           args.Length > 3 ? args[3] : "reports/handreplace.png",
                           args.Length > 4 ? args[4] : @"C:\Windows\Fonts\simkai.ttf",
                           args.Length > 5 && float.TryParse(args[5], out float tl) ? tl : 0f);
            _quit = true;
        }
        else if (mode == "--glyphline")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphLinePng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                         args.Length > 2 ? args[2] : "同学们，上课！",
                         args.Length > 3 ? args[3] : "reports/glyphline.png",
                         args.Length > 4 && float.TryParse(args[4], out float lh) ? lh : 72f,
                         args.Length > 5 && float.TryParse(args[5], out float lw) ? lw : 1200f);
            _quit = true;
        }
        else if (mode == "--glyphobj")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphObjPng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                        args.Length > 2 ? args[2] : "函数向量",
                        args.Length > 3 ? args[3] : "reports/glyphobj.png",
                        args.Length > 4 && float.TryParse(args[4], out float gs) ? gs : 120f);
            _quit = true;
        }
        else if (mode == "--glyphfill")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphFillPng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                         args.Length > 2 && args[2].Length > 0 ? args[2][0] : '函',
                         args.Length > 3 ? args[3] : "reports/glyphfill.png");
            _quit = true;
        }
        else if (mode == "--glyphpath")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            GlyphPathDump(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                          args.Length > 2 ? args[2] : "函数向量");
            _quit = true;
        }
        else if (mode == "--glyphink")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphInkPng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                        args.Length > 2 ? args[2] : "函数向量",
                        args.Length > 3 ? args[3] : "reports/glyphink.png",
                        args.Length > 4 && float.TryParse(args[4], out float gh) ? gh : 96f);
            _quit = true;
        }
        else if (mode == "--inkbeautify")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            InkBeautify(args.Length > 1 ? args[1] : "",
                        args.Length > 2 ? args[2] : "reports/board-beautified.png",
                        args.Length > 3 && int.TryParse(args[3], out int n) ? n : 2);
            _quit = true;
        }
        else if (mode == "--photobeautify")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PhotoBeautifyShow(args.Length > 1 ? args[1] : "",
                              args.Length > 2 ? args[2] : "reports/photo-beautify");
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
            // 第二个参数给了路径就顺手出一张面板的图（和 --shapetoolshow 一个用法）。
            // 以 `--` 开头的是别的开关（如 `--uiscale 0.8`），不能当文件名。
            string shot = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                ? args[1] : null;
            LibraryTest(shot);
        }
        else if (mode == "--libraryshow")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string shot = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                ? args[1] : "reports/library-show.png";
            LibraryShow(shot);
        }
        else if (mode == "--backuptest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BackupTest();
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
        else if (mode == "--spaceaxistest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SpaceAxisTest();
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
        else if (mode == "--logtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LogTest();
        }
        else if (mode == "--autostarttest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AutoStartTest();
        }
        else if (mode == "--startupcheck")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            StartupCheck();
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
            InkTrailTest(args.Contains("--touch"));   // `--trailtest --touch` = 合成触摸对照
        }
        else if (mode == "--historytest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 板书历史自检：快照只含笔迹（截图不进）/ 总量轮转 / 从历史找回 / 清除可撤销。
            BoardHistoryTest();
        }
        else if (mode == "--lagtest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 湿墨延时对照：主绘 / 系统轨迹 / 产品组合三档，量墨尖落后与跳变响应。
            WetLagTest();
        }
        else if (mode == "--camlag")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 手机 120Hz 录像对照：同一套匀速拖动跑各湿墨档，屏上有档位横条 + 50px 刻度尺，
            // 拍下来量"系统光标尖 ↔ 墨尖"的距离（÷ 速度 = 毫秒）。
            string which = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "all";
            int camSpeed = 1200, camSweeps = 4;
            double camLive = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--speed" && i + 1 < args.Length && int.TryParse(args[i + 1], out var vSpeed))
                    camSpeed = Math.Clamp(vSpeed, 100, 6000);
                if (args[i] == "--sweeps" && i + 1 < args.Length && int.TryParse(args[i + 1], out var vSweeps))
                    camSweeps = Math.Clamp(vSweeps, 1, 20);
                if (args[i] == "--live" && i + 1 < args.Length && double.TryParse(args[i + 1], out var vLive))
                    camLive = Math.Clamp(vLive, 3, 600);
            }
            CamLagTest(which, camSpeed, camSweeps, camLive);
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
        else if (mode == "--snaptest")
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SnapTest();
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
                        : args.Contains("--spacecoordinate") ? "spacecoordinate"
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
        else if (mode == "--pdmetrics")
        {
            // 大数据管线第二环：真实语料（SCUT 中文 / UCI）离线评测预测器——
            // 供给率 / 吃到率 / 角度误差 / 超前（见 PdMetricsProbe 的口径注释）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = PdMetricsProbe.Run(args);
            _quit = true;
        }
        else if (mode == "--replayinput")
        {
            // 黑匣子回放：把 `--reclive` 记录的真实输入 1:1 喂回引擎，算出预测尾的指标。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = InputReplayProbe.Run(this, args);
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
        else if (mode == "--tailjumptest")
        {
            // [重启 2026-10-08] B4 预测尾"突突跳"检测（老系统 2026-10-05 停用；见 已停用-渲染实验.md）
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TailJumpTest(args.Contains("--noisy"), args.Contains("--off"));
        }
        else if (mode == "--predlinetest")
        {
            // 线性预测纯算法自检（2026-10-15 加）：不需要真笔/屏幕（见 SelfTests/PredictLinear.cs）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PredictLinearTest();
        }
        else if (mode == "--tapdotprobe")
        {
            // [2026-10-08] "起笔冒圆"复现器（合成笔）：见 TapDotProbe。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TapDotProbe();
        }
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
        else if (mode == "--logupload")
        {
            // 开发用：走一遍**真实的上传链路**（「更多 → 日志」屁股底下同一条路），
            // 打完就退——发布前 / 换令牌后用它验"仓 + 令牌 + 接口"还通。
            // （自检模式默认拦上传，是护栏；这里显式放行。）
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExportDialogEnabled = true;
            UploadLogsFromUi();
            ExportDialogEnabled = false;      // 失败也别弹"导出"（开发模式）
            // 上传在后台线程：抽消息＋渲染驱动，等状态行落定（最多 90 秒）。
            var swLu = System.Diagnostics.Stopwatch.StartNew();
            while (swLu.ElapsedMilliseconds < 90_000
                   && (swLu.ElapsedMilliseconds < 900 || InkStatus.StartsWith("正在上传")))
            {
                PumpMessages();
                RenderAll();
                Thread.Sleep(30);
            }
            Console.WriteLine("logupload: " + InkStatus);
            _quit = true;
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
            // （包括 `--norawinput` / `--clean 3` / `--recink …` 这些**开关**）
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
        Console.WriteLine("开发期测试模式（不属于引擎；完整表：InkTeach.exe --help）：");
        Console.WriteLine("  --help              打印这份参数表与快捷键表（启动时不再刷屏）");
        Console.WriteLine("  --selftest [秒]     自动画几笔并检查渲染");
        Console.WriteLine("  自检取消：Ctrl+Alt+Shift+C（全局；UI 卡住也能停；终端 Ctrl+C 也行）");
        Console.WriteLine("  --report [文件]     输出完整性能报告");
        Console.WriteLine("  --memory [文件]     输出内存归因报告");
        Console.WriteLine("  --inputtest         指针输入路径自检");
        Console.WriteLine("  --passtest          穿透真机测试（跨进程点击）");
        Console.WriteLine("  --uitest            界面输入通路自检（合成点击，看谁收到）");
        Console.WriteLine("  --paneltest         产品界面自检（球 → 按钮带这条最小闭环）");
        Console.WriteLine("  --settingsaudit     设置页后台自检（无窗口、不抢前台；逐项命中/执行/出图）");
        Console.WriteLine("  --pageaudit         PageHost 页面栈自检（无窗口；栈/生命周期/尺寸/几何）");
        Console.WriteLine("  --textinputaudit    文字输入宿主自检（隐藏窗口；Begin/Commit/Cancel/校验/多行）");
        Console.WriteLine("  --overlayaudit      浮层管理自检（无窗口；优先级/捕获/Esc/点外/阻断）");
        Console.WriteLine("  --shapepaneltest    图形面板专项（入口 / 页签 / 选图形 / 蓝色互斥）");
        Console.WriteLine("  --photobeautify <输入目录> <输出目录>  图片美化批量出图（去黄/白纸/轻锐化）");
        Console.WriteLine("  --selbeautifyshow [图]  出图：图片选中条（美化 / 保存进⋯）");
        Console.WriteLine("  --bandactiontest    选择带上带的三连（全选 / 复制 / 粘贴）几何与提示");
        Console.WriteLine("  --qrtest            反馈群二维码（读图 / 打开 / 点一下关闭）");
        Console.WriteLine("  --shapeaudittest    图形面板 41 张卡逐张仿真（种类 / 档位 / 紧框 / 手柄 / 小样）");
        Console.WriteLine("  --librarysizetest   面板尺寸记忆专项（图形/收藏的位置·宽·高 + 重启读回）");
        Console.WriteLine("  --textboxsim        文本框仿真自检（真实鼠标/键盘 + 截图 reports/textbox-sim/）");
        Console.WriteLine("  --textboxedit       文本框编辑截图（reports/textbox-edit.bmp）");
        Console.WriteLine("  --aitest            AI 题卡自检（插入 / 折叠 / 存档 / 删除 / 撤销）");
        Console.WriteLine("  --mathtest          公式渲染回归：CSharpMath 渲染 50 个典型 LaTeX 到 reports/math-spike/");
        Console.WriteLine("  --mathcorpus [文件] 批量跑公式语料（默认 调研/公式语料/高中数学-公式语料.txt），列出失败项");
        Console.WriteLine("  --radialtest        呼出盘自检（Ctrl+Alt+Shift+Q：开 / 划 / 松 / 取消 / 穿透 / 松键轮询）");
        Console.WriteLine("  --radialshow [图]   呼出盘摆样：定格在屏幕中央出图（默认 reports/radial-palette.bmp）");
        Console.WriteLine("  --aishow [图]       AI 面板 + 题卡 + 文本框摆样：面板 / 折叠 / 选中 / 展开 / 文本框 / 改色六张（默认 reports/ai-card.bmp）");
        Console.WriteLine("  --shapeiconshow [路径] 出图：图形面板图标的对照表（含每一档的变体）");
        Console.WriteLine("  --toolicons [路径]  出图：白板 / 激光笔的图标候选（未选中 / 选中 / 放大三格）");
        Console.WriteLine("  --penshow [路径] [bars]  出图：上游那批「带笔的」图标；带第 2 个参数就每格加一副「三道杠」");
        Console.WriteLine("  --snaptest          对象吸附自检（3×3 线组合 / 软相交 / 抛物线顶点 / 正弦拼缝）");
        Console.WriteLine("  --inktest           **墨迹识别的单独测试**（日常改识别只跑这一条：准确率 / 误报 / 难例 / 写回）");
        Console.WriteLine("  --dwelltest         停顿成型的手势自检（真入口 + 推时钟：触发 / 定型 / 撤销回手绘 / 不留墨）");
        Console.WriteLine("  --dashtest          线型自检（实线/虚线/点线上屏墨量、面板那一行、存档往返）");
        Console.WriteLine("  --axistest          坐标系/数轴自检（画法 / 四轴端+原点 / 吸附 / 网格 / 上屏 / 存档往返）");
        Console.WriteLine("  --spaceaxistest     空间直角坐标系自检（一笔画法 / 四轴端 / 整组平移 / 存档 / 面板）");
        Console.WriteLine("  --axisshow [路径]   出图：坐标系（带网格/不带）+ 数轴 + 一条虚线");
        Console.WriteLine("  --curvetest         四种曲线自检（画法 / 紧框 / 手柄精简 / 朝向 / 存档往返）");
        Console.WriteLine("  --curveshow [路径]  出图：抛物线四种开口 + 双曲线两个方向 + 正弦/余弦各一周期");
        Console.WriteLine("  --conicshow [路径]  出图：双曲线两档（有/无渐近线）+ 椭圆两档（有/无焦点三角形）");
        Console.WriteLine("                      （第三行顺带出**旋转体那一族**：圆柱 / 圆锥 / 圆台 / 球）");
        Console.WriteLine("  --pagetest          整屏翻页自检（一屏 = 一页：页高 = 视口高、只动相机、到顶就停）");
        Console.WriteLine("  --ppttest           PPT 模式自检（假页码源：隔离 / 页内滚动 / 清除全部墨迹=所有页且可撤销 / 退出写盘 / 再进读回）");
        Console.WriteLine("  --pptprobe [秒]     PPT 连接探针（真机跑：打开 PPT 按 F5，看连不连得上）");
        Console.WriteLine("                     加 --pptcmd 会实际按 Next/Prev/GotoSlide 验证命令；--pptdebug 打 ROT 细节");
        Console.WriteLine("  --iotest [路径]     导出自检（选中 → PNG 透明底 / JPEG 白底；给路径就保留文件）");
        Console.WriteLine("  --patterntest       白板底纹自检（方格/横线/间距 + 数屏幕上的线 + 重铺代价）");
        Console.WriteLine("  --lasertest         激光笔自检（拖尾一直写不缩 / 松手停 2 秒再整体淡出 / 多条并存 /");
        Console.WriteLine("                      粗细真的管用 / 不是笔迹）；顺带出图 reports/laser-trail.png");
        Console.WriteLine("  --pageshow <图>     整屏翻页摆样（相机停在两屏之间 / 正好对齐，各出一张）");
        Console.WriteLine("  --panelshow <图> [--band] [--mini] [--more [--page N] [--clickrow N] [--langpick N]] [--cell N] [--shape 名字] [--zoom N] [--update]   界面出图（离屏；--more --page 0=启动器 1=设置；--clickrow 点设置第 N 行，--update 摆出新版本红点）");
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
        Console.WriteLine("  --capturetest       截图（拖框 → 原位落 → 不写剪贴板，且不拍进自己的批注）");
        Console.WriteLine("  --logupload         开发用：真传一次运行日志（验日志收集仓 / 令牌是否通）");
        Console.WriteLine("  --logtest           日志系统自检（session.json / 环境 / 性能摘要 / 导出 zip）");
        Console.WriteLine("  --backuptest        全局备份自检（收藏/设置/历史/课件批注打包 → 恢复 → 坏包拒绝；不含 PDF/PPT 原文件）");
        Console.WriteLine("  --librarytest [图]  收藏自检（存入/去重/面板/插入/回收站/分类/⋯ 菜单/拖动排序/整组/吸附）");
        Console.WriteLine("  --libraryshow [图]  收藏弹窗摆样（横排 / 竖排 / 分类菜单各出一张；不依赖合成鼠标）");
        Console.WriteLine("  --autostarttest     开机自启自检（注册表写入 / 任务管理器禁用位 / 路径修复 / 清理）");
        Console.WriteLine("  --startupcheck     开机自启装配自检（穿透 + 小球收起 → 再启动后展开 + 退出穿透）");
        Console.WriteLine("  --cursorshow <笔|荧光笔|激光笔|橡皮|像素橡皮> [宽]  落点摆样");
        Console.WriteLine("  --widthtest         笔迹粗细/压力");
        Console.WriteLine("  --ghosttest         残影检测");
        Console.WriteLine("  --tiletest          分块缓存自检（边界无缝 / 回程复用 / 内存上界）");
        Console.WriteLine("  --prefetchtest      分块空闲预取自检（预取命中则再滚一格不重画；含关预取对照）");
        Console.WriteLine("  --noprefetch        关掉分块空闲预取（对照；默认开）");
        Console.WriteLine("  --lang <code>       界面语言：system|en|zh-CN|ja|ko|de|fr|es|pt-BR|ru|it（本次运行覆盖设置）");
        Console.WriteLine("  --langaudit         语言覆盖率审计（不建窗口，逐语言报缺失 key / 占位符）");
        Console.WriteLine("  --tipsweep          语言审计：把所有悬停提示过一遍 Loc.Tr（配 INKTEACH_LOC_AUDIT=1）");
        Console.WriteLine("  --warp              强制 WARP 软件渲染（无显卡兜底；诊断/性能地板用）");
        Console.WriteLine("  --pdflowmem         强制 PDF 省内存档（4GB 档：渲染宽 1600 / 1 页缓存 / 1s 翻页）");
        Console.WriteLine("  --pdfhighmem        强制 PDF 高清档（尽量按主屏物理宽；单页最多 12MP）");
        Console.WriteLine("  --trailtest [--touch]  委托墨迹轨迹对照（默认合成鼠标；--touch = 合成触摸）");
        Console.WriteLine("  --uiscale <0.8|0.9|1|1.15|1.3>  界面缩放档（工具带/色带/抽屉/设置页；只覆盖本次运行，不落盘）");
        Console.WriteLine("  --smoothtest        中心线曲线化自检（过点 Catmull-Rom：直角不变形 / 圆弧更圆滑 / 形状不跑）");
        Console.WriteLine("  --smoothshow [图]   出图：曲线化开/关对照（同一组样本各存一张 -off / -on，32 位 BMP）");
        Console.WriteLine("  --smoothflashtest [--off]  “画的时候闪不闪”专项检测（合成鼠标画过去，看已经画过的墨还动不动）");
        Console.WriteLine("  --prevflash [--pen] [--left]  “写下一笔时，上一笔闪不闪”专项检测（合成鼠标/合成笔；--left=左侧竖写+横线底纹）");
        Console.WriteLine("  --motion <名字>     catmull / mean2（**默认 mean2**=距离窗＋过点曲线＋收笔追赶）");
        Console.WriteLine("  --motiontest        运动模型自检（baseline / catmull / mean2 同批语料出表）");
        Console.WriteLine("  --pdmetrics <语料> [--dt 15] [--limit N] [--sweep]  预测器离线评测（SCUT/UCI：供给/吃到/角度误差/超前）");
        Console.WriteLine("  --reclive <文件>     输入黑匣子：记录喂进引擎的输入（配 --replayinput 离线复现）");
        Console.WriteLine("  --replayinput <文件> [--frame 10]  黑匣子回放：供给率/帧间墨尖位移/方向翻转（诊断）");
        Console.WriteLine("  --himetric / --nohimetric  亚像素输入（默认开；--nohimetric 退回 D0 对照）");
        Console.WriteLine("  模型调参：--mean2win 画布像素 / --smoothcorner N 角点阈值");
        Console.WriteLine("  预测尾：--predict2 开（**默认关**；唯一算法 = 线性预测（Chromium 同族），--nopredict2 强关）");
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
        Console.WriteLine("  --historytest       板书历史自检（快照只含笔迹、不含截图 / 总量轮转 / 从历史找回 / 清除可撤销）");
        Console.WriteLine("  --replaytest        墨迹回放自检（时间轴 / 前缀 / 暂停 / 倍速 / 只读 / 控制条 / 翻页退出）");
        Console.WriteLine("  --timertest         课堂计时器自检（功能卡：设置态↔运行态 / ⚙ / ±1分 / 到点超时 / 提示音 / 穿透小窗）");
        Console.WriteLine("  --rolltest          课堂点名自检（功能卡：设置↔结果 / 滚动定格 / 去重池子 / 名单 / 穿透小窗）");
        Console.WriteLine("  --replayshow <图>   墨迹回放摆样（铺几笔 → 播到一半 → 截控制条那一块）");
        Console.WriteLine("  --ballprobe [前缀]  收起球贴边诊断（左/右/四角 × 显示/隐藏，逐个出图）");
        Console.WriteLine("  --lagtest           湿墨延时对照（合成鼠标：主绘 / 系统轨迹 / 产品组合，");
        Console.WriteLine("                      量\"墨尖落后指针\"多少 px 与跳变响应 ms；轨迹档还能看出它到底画没画）");
        Console.WriteLine("  --camlag [all|1..3] [--speed N] [--sweeps N] [--live N]   手机 120Hz 录像对照：");
        Console.WriteLine("                      同一套匀速拖动逐档跑（1 主绘 2 系统轨迹 3 产品组合），");
        Console.WriteLine("                      屏上有档位横条 + 每 50 物理像素的刻度尺；拍下来量\"光标尖↔墨尖\"");
        Console.WriteLine("                      的距离 ÷ 速度 = 毫秒。--live N = 换成你自己用笔写 N 秒。");
        Console.WriteLine("  --wetinktest        湿墨轨迹实测（只让系统画，数上屏像素：这条通道到底画不画）");
        Console.WriteLine("  --wetmode <0..1>    湿墨档位（记住到设置；运行时 Ctrl+Alt+Shift+W 循环切换）：");
        Console.WriteLine("                      0=原路径（默认：不喂轨迹）1=+系统委托轨迹（有就用、没有退回自绘）");
        Console.WriteLine("                      也可以点：「更多 → 设置 → 墨迹 → 湿墨档位」（点一下换一档）");
        Console.WriteLine("  --syswet            真笔 + 实线笔：湿墨只让系统轨迹画（诊断；⚠ 单条轨迹系统上限 128 点，长笔画会回滚）");
        Console.WriteLine("  --syscursor         三支笔一律系统斜笔（零延迟硬件光标）——**已是默认**，写上等于没写");
        Console.WriteLine("  --nosyscursor       退回自绘落点（笔尖环 / 荧光笔圆盘 / 激光实心点）做对照");
        Console.WriteLine("  --trailonly         自己那一笔不画（湿墨只剩系统轨迹）——手动验证系统到底给哪种输入画");
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
        Console.WriteLine("  口径：每格工作 = RenderAll 墙钟 − Present 配速等待（含分块光栅/界面准备/上屏调用）；判掉帧只看工作耗时");
        Console.WriteLine("    文档笔数 | 每格工作 | 最慢工作 | 记录平均 | 光栅平均 | 最慢光栅 | 等待平均 | 超16.7/超刷新 | 新光栅块");
        Console.WriteLine("  -----------|----------|----------|----------|----------|----------|----------|----------------|----------");

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
            double record = 0, wait = 0;
            int newTiles = 0, over60 = 0, overRefresh = 0;
            for (int k = 0; k < steps; k++)
            {
                HandleWheel(Wheel(-120));            // 往下滚一格
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();                         // 只渲染这一格

                var w0 = _windows[0];
                double workMs = LastFrameMs - w0.LastPresentWaitMs;
                total += workMs;
                record += w0.LastRecordMs;
                raster += w0.LastRebuildMs;
                wait += w0.LastPresentWaitMs;
                if (workMs > worst) worst = workMs;
                if (w0.LastRebuildMs > worstRaster) worstRaster = w0.LastRebuildMs;
                if (workMs > 16.7) over60++;
                if (workMs > w0.RefreshPeriodMs) overRefresh++;
                newTiles += w0.LastPatchCount;
            }

            string note = worst < 16.7 ? "全程不掉帧"
                        : over60 == 1 ? "偶掉 1 格"
                        : over60 <= 3 ? "偶掉几格"
                        : "会卡";
            Console.WriteLine($"  {n,9} | {total / steps,6:F2} ms | {worst,6:F2} ms | {record / steps,6:F2} ms | {raster / steps,6:F2} ms | {worstRaster,10:F2} ms | {wait / steps,6:F2} ms | {over60,6}/{overRefresh,-6} | {newTiles,8} | {note}");
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
        // 滚动条在 2026-10-09 已停用（见 Engine.ScrollBarEnabled）；这个对照模式
        // 临时把它打开——出图看的是"当年长什么样"，不是产品行为。
        ScrollBarEnabled = true;
        Console.WriteLine("（注：滚动条已在产品里停用；--scrollshow 为对照出图临时打开）");
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
                SelectionHandles.FrameOf(Doc.Selected).CanvasAabb, UiUnit, FurnitureViewport);
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
                                                  UiUnit, FurnitureViewport, SelectionHandles.SwatchCount);
            float mx = SelectionHandles.SliderXOfT(0.62f, SelectionHandles.FrameOf(Doc.Selected).CanvasAabb,
                                                   UiUnit, FurnitureViewport, SelectionHandles.SwatchCount);
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
        var bar = SelectionHandles.BarRect(aabb, UiUnit, FurnitureViewport);
        var region = new RectF
        {
            MinX = MathF.Min(aabb.MinX, bar.MinX), MinY = MathF.Min(aabb.MinY, bar.MinY),
            MaxX = MathF.Max(aabb.MaxX, bar.MaxX), MaxY = MathF.Max(aabb.MaxY, bar.MaxY),
        };
        if (SelPanelOpen == SelPanel.Ink)
        {
            region.Add(SelectionHandles
                .PanelRect(aabb, UiUnit, FurnitureViewport, SelectionHandles.SwatchCount));
            if (CustomColorOpen)
                region.Add(SelectionHandles.CustomPanelRect(aabb, UiUnit, FurnitureViewport,
                                                           SelectionHandles.SwatchCount));
        }
        else if (SelPanelOpen == SelPanel.Layer)
        {
            region.Add(SelectionHandles.LayerPanelRect(aabb, UiUnit, FurnitureViewport));
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
        float dpi = UiUnit;
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
                case "spacecoordinate":
                    sh.Tool = Tool.SpaceCoordinate; sh.Kind = StrokeKind.SpaceCoordinate;
                    sh.AddPoint(cx - 260f, cy + 180f, 1f, 0);
                    sh.SetSpaceAxisBox(cx - 260f, cy + 180f, cx + 240f, cy - 180f);
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

            // `--drag` + 空间坐标系：真拖 X / Z 轴端缩短，松手后看有没有残留的旧选中框。
            if (drag && kind == "spacecoordinate")
            {
                void DragHandle(ShapeHandle h, float dx, float dy)
                {
                    var from = SelectionHandles.ShapeHandleCanvasPosition(sh, h);
                    var to = new Vector2(from.X + dx, from.Y + dy);
                    VertexDragForTest(sh, h, from.X, from.Y, to.X, to.Y);
                    SettleFrames(250);
                }
                DragHandle(ShapeHandle.SpaceX, -100f * DpiScale, -80f * DpiScale);
                DragHandle(ShapeHandle.SpaceZ, 0f, 120f * DpiScale);
                // 再把 Y 拖过原点：这是最容易留下旧框的一幕（旧框在右上方，新框跑到左下方）。
                {
                    var yh = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.SpaceY);
                    var oo = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.SpaceOrigin);
                    var yTo = oo - (yh - oo) * 0.8f;
                    VertexDragForTest(sh, ShapeHandle.SpaceY, yh.X, yh.Y, yTo.X, yTo.Y);
                    SettleFrames(350);
                }
                {
                    var oo = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.SpaceOrigin);
                    VertexDragForTest(sh, ShapeHandle.SpaceOrigin, oo.X, oo.Y, oo.X + 80f, oo.Y + 40f);
                    SettleFrames(350);
                }
            }

            var sFrame = SelectionHandles.FrameOf(Doc.Selected);
            var sAabb = sFrame.CanvasAabb;
            var sBar = SelectionHandles.BarRect(sAabb, UiUnit, FurnitureViewport);
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
            var barR = SelectionHandles.BarRect(aabbR, UiUnit, FurnitureViewport);
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
            var bar = SelectionHandles.BarRect(aabb, UiUnit, FurnitureViewport);
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
}
