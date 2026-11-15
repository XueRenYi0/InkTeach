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
}
