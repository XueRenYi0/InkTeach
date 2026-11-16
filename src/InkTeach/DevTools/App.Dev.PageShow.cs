// [职责] 界面出图 / 状态探针：--panelshow 离屏截图、--pageshow 画布截图。
// [测试] --panelshow / --pageshow（本身不判定，出图给人工验收）

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
            // --docpan：**文档模式**出图（2026-11-15：PDF 像放映——主条那格画手掌）。
            // 产品里没有这个入口（和 --recent / --tip 同一类，只服务"图上看细节"）：
            // 铺一叠假页 + 把手掌打开，那一格就会画成手并亮着。页位图给个纯灰生成器，
            // 免得后台页线程拿到 null 生成器还空转。
            if (cli.Contains("--docpan"))
            {
                var demoPages = new List<DocPages.Spec>
                {
                    new() { Kind = 0, Source = "示范讲义.png", SourceIndex = 0, OutW = 900, OutH = 1200 },
                };
                DocView.Generator = sp => FakeDocPageBgra(sp.OutW, sp.OutH, 246, 246, 246);
                DocView.Open(demoPages, "示范讲义.pdf", Native.GetSystemMetrics(0) * 0.5f, 0f, 24f);
                BoardOn = false;                     // 文档模式没有白板（不然白板格也亮着，图上像两个选中）
                Host.Commands.ToggleDocPan();
                SettleFrames(220);
            }
            // --docresume：出「上次看到第 N 页 · 继续 / 从头」那张卡（文档模式）。
            // 走真实的打开流程（读位置 → 卡片可见），再截整屏；产品里没有这个入口。
            if (cli.Contains("--docresume"))
            {
                DocResumeShowcase(path);
                _quit = true;
                return;
            }
            // --pdfclock：出「PDF 打开 + 时间档收起挂钟」整屏图（2026-11-16）。
            // 走真实路径：先选时间档并收起 → 模拟重启（只剩偏好）→ 打开假文档触发自动挂回。
            if (cli.Contains("--pdfclock"))
            {
                PdfClockShowcase(path);
                _quit = true;
                return;
            }
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
            // --recent N：往「最近文档」里塞 N 条假记录（出图 / 验收用）。
            // 文件真的建在临时目录——面板会跳过"文件已经不在了"的条目，不建就什么都看不见。
            // 产品里没有这个入口（和 --picker 的 seed 同一类）。
            {
                var argvR = Environment.GetCommandLineArgs();
                int rci = Array.IndexOf(argvR, "--recent");
                if (rci >= 0 && rci + 1 < argvR.Length && int.TryParse(argvR[rci + 1], out int recentN))
                {
                    string dir = Path.Combine(Path.GetTempPath(), "InkTeach-recent-demo");
                    Directory.CreateDirectory(dir);
                    var demo = new (string Name, string Ext)[]
                    {
                        ("二次函数与图像 第 3 课时 课堂练习（学生版）.pdf", "pdf"),
                        ("期中试卷讲解.pptx", "pptx"),
                        ("板书拍照 2026-11-12.png", "png"),
                        ("第四课 光的折射.pdf", "pdf"),
                        ("作业讲评.jpg", "jpg"),
                    };
                    int take = Math.Clamp(recentN, 0, demo.Length);
                    var seeded = new string[take];
                    for (int i = 0; i < take; i++)
                    {
                        seeded[i] = Path.Combine(dir, demo[i].Name);
                        try
                        {
                            if (!File.Exists(seeded[i])) File.WriteAllBytes(seeded[i], Array.Empty<byte>());
                        }
                        catch { /* 出图用途：建不出来就少一条，不影响其它 */ }
                    }
                    ui.SeedRecentDocsForTest(seeded);
                    Console.WriteLine($"  [panelshow] recent={take}");
                }
            }
            // --recentpage：出「最近文档」页（2 份 PDF + 2 份 PPT + 3 份板书历史）。
            // 产品里没有这个入口；文件真的建在临时目录（列表会跳过不存在的条目）。
            bool recentPageShow = Environment.GetCommandLineArgs().Contains("--recentpage");
            if (recentPageShow) SeedRecentPageShowcase(ui);
            // --more [--page N]：出**中央「更多」面板**（0 课堂 / 1 墨迹 / 2 设置）。
            // 面板打开时 QueryBounds 是整块屏幕，截图范围在下面单独裁（见 shot 那一段）。
            if (_panelShowMore)
            {
                ui.OpenMoreForTest();
                var argvMore = Environment.GetCommandLineArgs();
                int pi2 = Array.IndexOf(argvMore, "--page");
                if (pi2 >= 0 && pi2 + 1 < argvMore.Length && int.TryParse(argvMore[pi2 + 1], out int pageArg))
                    ui.SetMorePageForTest(pageArg);
                else if (recentPageShow)
                    ui.SetMorePageForTest(5);            // 最近文档页（2 PDF / 2 PPT / 3 板书）
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
                        var tr = TimerWin.TabRect(tcin, tlu, Math.Clamp(tabArg, 0, TimerWin.ModeCount - 1));
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
                int wx = (int)MathF.Floor(wr.MinX), wy = (int)MathF.Floor(wr.MinY);
                int ww = Math.Max(1, (int)MathF.Ceiling(wr.MaxX - wr.MinX));
                int wh = Math.Max(1, (int)MathF.Ceiling(wr.MaxY - wr.MinY));
                // 路径写 .png 就转成真 PNG（BMP 字节直接改后缀，看图工具会打不开）；
                // 其余后缀沿用老习惯写 BMP。
                bool okW = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? SaveScreenPng(path, wx, wy, ww, wh)
                    : ScreenProbe.SaveBmp(path, wx, wy, ww, wh);
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

    /// <summary>
    /// `--panelshow &lt;out.png&gt; --docresume`：铺一叠假页 + 种一条"上次会话"的阅读位置，
    /// 走真实打开流程弹出恢复卡，截整屏出图（人工验收：卡片位置 / 文案 / 按钮对齐 /
    /// 不和页码条打架）。产品里没有这个入口。
    /// </summary>
    private void DocResumeShowcase(string path)
    {
        if (DocView.IsOpen) CloseDocument();
        BoardOn = false;
        Tool = Tool.Pen;
        Host.Commands.SetTool(Tool.Pen);

        string key = "resume-showcase";
        DocResumeStore.Delete(key);
        var shownAt = DateTimeOffset.Now.Date.AddDays(-1).AddHours(21).AddMinutes(30);
        DocResumeStore.Save(key, new DocResumePosition
        {
            Page = 7, FractionY = 0.35f, FractionX = 0.5f, Zoom = 0.75f,
            SavedAtUnix = new DateTimeOffset(shownAt).ToUnixTimeSeconds(),   // 昨天 = 跨会话
        });

        var specs = new List<DocPages.Spec>();
        for (int i = 0; i < 12; i++)
            specs.Add(new DocPages.Spec
            {
                Kind = 0, Source = "示范讲义.png", SourceIndex = i,
                OutW = 1400, OutH = 1980,
            });
        DocPageWorker = false;
        DocView.UseWorker = false;
        StartDocViewForTest(specs, "示范讲义.pdf", key);
        DocView.Generator = sp => FakeDocPageBgra(sp.OutW, sp.OutH, 246, 246, 248);
        SettleFrames(900);

        var card = DocResumeCardRect();
        Console.WriteLine($"  [docresume] 卡片逻辑 {card.MaxX - card.MinX:F0}×{card.MaxY - card.MinY:F0}"
                          + $" @ ({card.MinX:F0},{card.MinY:F0}) 可见={DocResumeVisible}");
        bool ok = SaveScreenPng(path, (int)_virtualX, (int)_virtualY,
                                Math.Max(1, (int)_virtualW), Math.Max(1, (int)_virtualH));
        Console.WriteLine(ok ? $"已出图 {path}（文档恢复卡）" : "出图失败");
        ExitCode = ok ? 0 : 1;
        CloseDocument();
        DocResumeStore.Delete(key);
    }

    /// <summary>
    /// `--panelshow &lt;out.png&gt; --pdfclock`：PDF（假页）打开 + 「时间」档收起挂钟 206×48。
    ///
    /// 走的是产品路径，不是摆拍：选「时间」→ 点收起 → `TimerReloadPrefsForTest` 模拟重启
    /// （窗口关着，只剩 timerKind/timerMinimized/timerCardOpen 三条偏好）→ `StartDocViewForTest`
    /// → `StartDocView` 里的 `MaybeAutoShowTimerOnDocEntry` 把挂钟挂回来。
    /// 图里同时能看到翻页条 / 缩放条，验收"206×48 不挡底条"。
    /// </summary>
    private void PdfClockShowcase(string path)
    {
        if (DocView.IsOpen) CloseDocument();
        string key = "clock-showcase";
        DocResumeStore.Delete(key);
        BoardOn = false;
        Tool = Tool.Pen;

        // ① 产品路径：开计时窗 → 选「时间」→ 收起 → 模拟重启（窗口不在了，偏好还在）
        OpenTimerCardFromUi();
        SettleFrames(120);
        var cw = TimerWin.CardRect(TimerCardRect(), TimerUnit(), false);
        float clu = TimerWin.Layout(cw, TimerUnit());
        void ClickAxis(in RectF r)
        {
            TimerPointerDownForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
            TimerPointerUpForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
        }
        ClickAxis(TimerWin.TabRect(cw, clu, 3));
        ClickAxis(TimerWin.BtnRect(cw, clu, TimerZone.Minimize));
        SettleFrames(150);
        TimerReloadPrefsForTest();
        SetPassThroughFromUi(false);      // 模拟新进程：不是"开窗时强制穿透"的那条路

        // ② 打开文档：StartDocView 里的自动挂回把「时间」按收起状态放回来
        var specs = new List<DocPages.Spec>();
        for (int i = 0; i < 8; i++)
            specs.Add(new DocPages.Spec
            {
                Kind = 0, Source = "示范讲义.png", SourceIndex = i,
                OutW = 1400, OutH = 1980,
            });
        DocPageWorker = false;
        StartDocViewForTest(specs, "示范讲义.pdf", key);
        DocView.Generator = sp => FakeDocPageBgra(sp.OutW, sp.OutH, 250, 250, 252);
        SettleFrames(800);

        var tw = TimerCardRect();
        Console.WriteLine($"  [pdfclock] 挂钟 {tw.MaxX - tw.MinX:F0}×{tw.MaxY - tw.MinY:F0}"
                          + $" 开={TimerCardOpen} 收起={TimerMinimized} 显示={TimerDisplayText()}");
        bool ok = SaveScreenPng(path, (int)_virtualX, (int)_virtualY,
                                Math.Max(1, (int)_virtualW), Math.Max(1, (int)_virtualH));
        Console.WriteLine(ok ? $"已出图 {path}（PDF + 时间挂钟）" : "出图失败");
        ExitCode = ok ? 0 : 1;
        CloseDocument();
        DocResumeStore.Delete(key);
    }

    /// <summary>整屏截一张 PNG：先按 BMP 抓（ScreenProbe 现成的路），再交给 GDI+ 转 PNG。</summary>
    private static bool SaveScreenPng(string path, int x, int y, int w, int h)
    {
        string tmp = path + ".tmp.bmp";
        try
        {
            if (!ScreenProbe.SaveBmp(tmp, x, y, w, h)) return false;
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var img = System.Drawing.Image.FromFile(tmp))
                img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  出图转换失败：" + ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 有意忽略：临时文件删不掉不影响出图结果 */ }
        }
    }
}
