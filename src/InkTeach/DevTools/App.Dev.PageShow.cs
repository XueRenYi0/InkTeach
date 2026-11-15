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
        bool showHelp = Environment.GetCommandLineArgs().Contains("--helpui");
        _panelShowMini = Environment.GetCommandLineArgs().Contains("--mini");
        _panelShowBand = Environment.GetCommandLineArgs().Contains("--band");
        SetUiFactory(() => new InkUi.FullUi());
        // 出图专用：把“有新版本”状态摆出来，查看红点位置（产品里由静默检查设置）。
        if (Environment.GetCommandLineArgs().Contains("--update"))
        {
            UpdateState = UpdateStage.Available;
            UpdateText = "有新版本 9.2.2";
        }
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
            // --picker：把笔的取色板打开（设计验收用；默认落在第 3 个色位，蓝色好核对）。
            // 顺便塞几条“最近使用”，让截图能看到右栏下半段（产品里由用户实际取色产生）。
            if (Environment.GetCommandLineArgs().Contains("--picker"))
            {
                var seed = new[]
                {
                    new Color4(0.05f, 0.58f, 0.53f, 1f), new Color4(0.88f, 0.11f, 0.28f, 1f),
                    new Color4(0.49f, 0.23f, 0.93f, 1f), new Color4(0.92f, 0.35f, 0.05f, 1f),
                    new Color4(0.40f, 0.64f, 0.05f, 1f), new Color4(0.03f, 0.57f, 0.70f, 1f),
                    new Color4(0.86f, 0.15f, 0.47f, 1f), new Color4(0.29f, 0.33f, 0.39f, 1f),
                };
                foreach (var c in seed) Host.Commands.SetPenSwatchColor(7, c, rememberRecent: true);
                Host.Commands.SetPenSwatchColor(7, InkUi.Tokens.Palette[7].Color, rememberRecent: false);
                Host.Commands.SelectPenSwatch(2);
                ui.OpenPenPickerForTest();
            }

            // --hlpicker：荧光笔取色板（设计验收用；顺便塞几条最近色，看看右栏下半段）。
            if (Environment.GetCommandLineArgs().Contains("--hlpicker"))
            {
                Host.Commands.SetTool(Tool.Highlighter);
                var hlSeed = new[]
                {
                    new Color4(0.05f, 0.58f, 0.53f, 1f), new Color4(0.88f, 0.11f, 0.28f, 1f),
                    new Color4(0.49f, 0.23f, 0.93f, 1f), new Color4(0.92f, 0.35f, 0.05f, 1f),
                    new Color4(0.40f, 0.64f, 0.05f, 1f), new Color4(0.03f, 0.57f, 0.70f, 1f),
                };
                foreach (var c in hlSeed)
                    Host.Commands.SetHighlighterSwatchColor(4, c, rememberRecent: true, normalize: true);
                var orange = InkUi.Tokens.HighlighterPalette[4].Color;
                Host.Commands.SetHighlighterSwatchColor(4, orange, rememberRecent: false, normalize: false);
                Host.Commands.SetHighlighterOpacity(0, 2);
                Host.Commands.SelectHighlighterSwatch(0);
                ui.OpenHighlighterPickerForTest(0);
            }
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
            // --uisize N：出图前切界面缩放档（0.8 / 1.0 / 1.3…；和设置里同一命令）。
            {
                var argvUi = Environment.GetCommandLineArgs();
                int uiAt = Array.IndexOf(argvUi, "--uisize");
                if (uiAt >= 0 && uiAt + 1 < argvUi.Length
                    && float.TryParse(argvUi[uiAt + 1], System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out float uiArg))
                {
                    ui.SetUiScaleForTest(uiArg);
                    Console.WriteLine($"  [panelshow] uisize={uiArg}");
                }
            }
            // --more [--page N]：出**中央「更多」面板**（0 课堂 / 1 墨迹 / 2 设置）。
            // 面板打开时 QueryBounds 是整块屏幕，截图范围在下面单独裁（见 shot 那一段）。
            if (_panelShowMore)
            {
                ui.OpenMoreForTest();
                var argvMore = Environment.GetCommandLineArgs();
                int pi2 = Array.IndexOf(argvMore, "--page");
                if (pi2 >= 0 && pi2 + 1 < argvMore.Length && int.TryParse(argvMore[pi2 + 1], out int pageArg))
                    ui.SetMorePageForTest(pageArg);
                // --settingscat N：先把设置页滚到第 N 个分类（0 工具条 / 1 常规 / 2 书写 …）。
                int si2 = Array.IndexOf(argvMore, "--settingscat");
                if (si2 >= 0 && si2 + 1 < argvMore.Length && int.TryParse(argvMore[si2 + 1], out int catArg))
                {
                    ui.SettingsScrollToCategoryForTest(catArg);
                    Console.WriteLine($"  [panelshow] settingscat={catArg}");
                }
                // --clickrow N：出图前点一下设置页第 N 行（语言下拉这类“点开才出现”的层）。
                int ri2 = Array.IndexOf(argvMore, "--clickrow");
                if (ri2 >= 0 && ri2 + 1 < argvMore.Length && int.TryParse(argvMore[ri2 + 1], out int rowArg))
                {
                    ui.ClickSettingsRowForTest(rowArg);
                    Console.WriteLine($"  [panelshow] clickrow={rowArg} langMenu={ui.LangMenuOpenForTest}");
                }
                int lp2 = Array.IndexOf(argvMore, "--langpick");
                if (lp2 >= 0 && lp2 + 1 < argvMore.Length && int.TryParse(argvMore[lp2 + 1], out int pickArg))
                {
                    ui.PickLangForTest(pickArg);
                    Console.WriteLine($"  [panelshow] langpick={pickArg}");
                }
            }
            // --tip N：把第 N 个悬停提示直接摆出来（橡皮提示等；不用真悬停等 500ms）。
            // 它不依赖 --more：主条工具、色片、设置行都能单独出图。
            {
                var argvTip = Environment.GetCommandLineArgs();
                int ti2 = Array.IndexOf(argvTip, "--tip");
                if (ti2 >= 0 && ti2 + 1 < argvTip.Length && int.TryParse(argvTip[ti2 + 1], out int tipArg))
                {
                    ui.ShowTipForTest(tipArg);
                    Console.WriteLine($"  [panelshow] tip={tipArg}");
                }
            }
            if (showHelp)
            {
                // 走真实入口：先开「更多」，再点「使用说明」格；顺便把“格子真的能打开”
                // 这条路径也测到。找不到格子时才退回直开，出图不静默失败。
                ui.OpenMoreForTest();
                var tile = ui.HubTileRectForTest(8);
                if (!tile.IsEmpty)
                {
                    var ev = new InkEngine.UiPointerEvent(
                        (tile.MinX + tile.MaxX) * 0.5f, (tile.MinY + tile.MaxY) * 0.5f,
                        1f, false, false);
                    ui.PointerDown(ev);
                    ui.PointerUp(ev);
                }
                if (!ui.HelpOpenForTest) ui.OpenHelpForTest();
                var argvHelp = Environment.GetCommandLineArgs();
                int ciHelp = Array.IndexOf(argvHelp, "--clicktab");
                if (ciHelp >= 0 && ciHelp + 1 < argvHelp.Length
                    && int.TryParse(argvHelp[ciHelp + 1], out int clickTab))
                {
                    var tr = ui.HelpTabRectForTest(clickTab);
                    var cev = new InkEngine.UiPointerEvent(
                        (tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f,
                        1f, false, false);
                    ui.PointerDown(cev);
                    ui.PointerUp(cev);
                    Console.WriteLine($"  [helpui] clicktab={clickTab} open={ui.HelpOpenForTest} now={ui.HelpTabForTest}");
                }
                int hi = Array.IndexOf(argvHelp, "--tab");
                if (hi >= 0 && hi + 1 < argvHelp.Length && int.TryParse(argvHelp[hi + 1], out int tabArg))
                    ui.SetHelpTabForTest(tabArg);
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
                    float tu = TimerUnit();
                    var cin = TimerWin.CardRect(tw, tu, false);
                    float lu = TimerWin.Layout(cin, tu);
                    void ClickAxis(in RectF r)
                    {
                        TimerPointerDownForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                        TimerPointerUpForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                    }
                    if (argvW.Contains("--stopwatch")) { StartTimerFromUi(TimerMode.Stopwatch, 0f); SettleFrames(900); }
                    else if (argvW.Contains("--run")) StartTimerFromUi(TimerMode.Countdown, 300f);
                    if (TimerKind == TimerMode.Stopwatch && argvW.Contains("--laps"))
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            SettleFrames(420);
                            ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Lap));
                        }
                        SettleFrames(220);
                    }
                    if (TimerKind == TimerMode.Stopwatch && argvW.Contains("--pause"))
                    {
                        PauseTimerFromUi();
                        SettleFrames(220);
                    }
                    int tbi = Array.IndexOf(argvW, "--tab");
                    if (tbi >= 0 && tbi + 1 < argvW.Length && int.TryParse(argvW[tbi + 1], out int tabArg))
                    {
                        float tcu = TimerUnit();
                        var tcin = TimerWin.CardRect(TimerCardRect(), tcu, false);
                        float tlu = TimerWin.Layout(tcin, tcu);
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
            // --pickshow：直接进吸管冻结帧，截一张全屏看左下角面板/抓屏是否正确。
            if (Environment.GetCommandLineArgs().Contains("--pickshow"))
            {
                Host.Commands.BeginScreenColorPick();
                SettleFrames(900);
                bool okPick = ScreenProbe.SaveBmp(path,
                    (int)MathF.Floor(_virtualX), (int)MathF.Floor(_virtualY),
                    Math.Max(1, (int)MathF.Ceiling(_virtualW)), Math.Max(1, (int)MathF.Ceiling(_virtualH)));
                Console.WriteLine(okPick ? $"已出图 {path}（吸管全屏）" : "出图失败");
                ExitCode = okPick ? 0 : 1;
                _quit = true;
                return;
            }

            // **范围要连"画到外面那一圈"一起给**（投影，见 IOverlayUi.PaintMargin）：
            // 这一条的边上留白只有 24 **物理**像素，200% 缩放下就是 12 逻辑像素，
            // 而投影最远胀出去 16+8 逻辑像素——不给就会被切掉一半，图看着像"没有投影"。
            var b = ui.QueryBounds();
            // 悬停提示画在面板外：无论开不开「更多」，出图都要把它框进来。
            {
                var tr0 = ui.TipRectForTest;
                if (!tr0.IsEmpty)
                    b = new RectF
                    {
                        MinX = MathF.Min(b.MinX, tr0.MinX - 12f),
                        MinY = MathF.Min(b.MinY, tr0.MinY - 12f),
                        MaxX = MathF.Max(b.MaxX, tr0.MaxX + 12f),
                        MaxY = MathF.Max(b.MaxY, tr0.MaxY + 12f),
                    };
            }
            // --more：面板打开时占用 = 整块屏幕，照它出图会得到一整屏（20MB 级）。
            // 设计验收要的是那块卡片 ＋ 周围一圈遮罩，所以裁到"面板 ＋ 64"。
            if (_panelShowMore || showHelp)
            {
                var mr = ui.MoreRectForTest;
                b = new RectF
                {
                    MinX = mr.MinX - 64f, MinY = mr.MinY - 64f,
                    MaxX = mr.MaxX + 64f, MaxY = mr.MaxY + 64f,
                };
                // 语言下拉会伸出设置面板，出图范围要连它一起框进来。
                if (ui.LangMenuOpenForTest)
                {
                    var lr = ui.LangMenuRectForTest;
                    b = new RectF
                    {
                        MinX = MathF.Min(b.MinX, lr.MinX - 16f),
                        MinY = MathF.Min(b.MinY, lr.MinY - 16f),
                        MaxX = MathF.Max(b.MaxX, lr.MaxX + 16f),
                        MaxY = MathF.Max(b.MaxY, lr.MaxY + 16f),
                    };
                }
                // 悬停提示画在面板外，出图也要框进来。
                var tipRect = ui.TipRectForTest;
                if (!tipRect.IsEmpty)
                {
                    b = new RectF
                    {
                        MinX = MathF.Min(b.MinX, tipRect.MinX - 12f),
                        MinY = MathF.Min(b.MinY, tipRect.MinY - 12f),
                        MaxX = MathF.Max(b.MaxX, tipRect.MaxX + 12f),
                        MaxY = MathF.Max(b.MaxY, tipRect.MaxY + 12f),
                    };
                }
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
}
