// [职责] 自检模式注册表：Sys 域（ADR-005 D2）。新增模式只改本文件。
// [测试] --list-modes / --unittest

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
    private ModeEntry[] BuildModeTableSys() => new ModeEntry[]
    {
        new("--list-modes", "列出全部自检模式（mode → 说明；机器可读）", args =>
        {
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            foreach (var e in ModeEntries)
                Console.WriteLine($"{e.Mode}\t{(string.IsNullOrEmpty(e.Help) ? "（待补说明）" : e.Help)}");
            // Program 层模式：在 RunModeDispatch 之前就返回，不在注册表里，但仍是用户可见入口。
            Console.WriteLine("--unittest\tL1 纯逻辑测试（--unittest [过滤词]，不建窗口）");
            return null;
        }),
        new("--aitest", "AI 题卡自检（插入 / 折叠 / 存档 / 删除 / 撤销）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AiTest();
            return null;
        }),
        new("--apiinputshot", "", args =>
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
            return null;
        }),
        new("--appendtest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AppendPathTest();
            return null;
        }),
        new("--autostarttest", "开机自启自检（注册表写入 / 任务管理器禁用位 / 路径修复 / 清理）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AutoStartTest();
            return null;
        }),
        new("--axistest", "坐标系/数轴自检（画法 / 四轴端+原点 / 吸附 / 网格 / 上屏 / 存档往返）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AxisTest();
            return null;
        }),
        new("--backuptest", "全局备份自检（收藏/设置/历史/课件批注打包 → 恢复 → 坏包拒绝；不含 PDF/PPT 原文件）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BackupTest();
            return null;
        }),
        new("--cameratest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CameraTest();
            return null;
        }),
        new("--camlag", "[all|1..3] [--speed N] [--sweeps N] [--live N]   手机 120Hz 录像对照：", args =>
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
            return null;
        }),
        new("--captest", "笔迹两端自检（单击＝圆点，宽笔＝圆头收尾）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CapTest();
            return null;
        }),
        new("--capturetest", "截图（拖框 → 原位落 → 不写剪贴板，且不拍进自己的批注）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureTest();
            return null;
        }),
        new("--clipboardtest", "剪贴板对象通道（复制 → 粘回来仍是对象；会覆盖系统剪贴板）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ClipboardTest();
            return null;
        }),
        new("--cornertest", "折角填充自检（45/90/135° 会不会掉色）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CornerTest();
            return null;
        }),
        new("--curvetest", "四种曲线自检（画法 / 紧框 / 手柄精简 / 朝向 / 存档往返）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CurveTest();
            return null;
        }),
        new("--dashtest", "线型自检（实线/虚线/点线上屏墨量、面板那一行、存档往返）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DashTest();
            return null;
        }),
        new("--demogif", "<目录>    演示连拍（写字→停顿变图形→选中拖动→截图取景，存 NNNN.bmp 帧）", args =>
        {
            // 演示连拍（写字 → 停顿变图形 → 选中拖动 → 截图取景）：帧存成 <目录>\NNNN.bmp，
            // 之后用 tools\make-demo-gif.ps1 拼成 README 头部那张 GIF。只在开发机上跑。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DemoGif(args.Length > 1 ? args[1] : "tmp/demo");
            return null;
        }),
        new("--duptest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 14;
            DupTest(n);
            return null;
        }),
        new("--dwelltest", "停顿成型的手势自检（真入口 + 推时钟：触发 / 定型 / 撤销回手绘 / 不留墨）", args =>
        {
            // 手势那一套**必须真跑**（起笔 / 采样 / 轮询 / 抬手都走引擎里真在用的函数），
            // 所以它和 `--inktest` 不同：这条要在活着的引擎里跑。时钟是推的，不 sleep。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = DwellProbe.Run(this);
            _quit = true;
            return null;
        }),
        new("--edittest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EditTest();
            return null;
        }),
        new("--ghosttest", "残影检测", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 加 "scroll" 再跑一遍：相机不为 0 时，光标的脏区换算只要漏了
            // 相机偏移就会"擦不干净"，而那种残影在没滚动的画面上根本看不出来。
            GhostTest(args.Contains("scroll"));
            return null;
        }),
        new("--help", "打印这份参数表与快捷键表（启动时不再刷屏）", args =>
        {
            // 2026-10-17：完整参数表 + 键位表从"每次启动刷屏"挪到这里
            //（用户："这里好多命令，能不能精简一些"）。打印完就走，不启动界面。
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            PrintUsageHelp();
            return null;
        }),
        new("--historytest", "板书历史自检（快照只含笔迹、不含截图 / 总量轮转 / 从历史找回 / 清除可撤销）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 板书历史自检：快照只含笔迹（截图不进）/ 总量轮转 / 从历史找回 / 清除可撤销。
            BoardHistoryTest();
            return null;
        }),
        new("--holetest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HoleTest();
            return null;
        }),
        new("--imagetest", "图像对象（上屏 / 复制翻转 / 存档 / 剪贴板）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageTest();
            return null;
        }),
        new("--iotest", "[路径]     导出自检（选中 → PNG 透明底 / JPEG 白底；给路径就保留文件）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 给路径时**保留文件**（人工核对 / 拿别的解码器验它）
            IoTest(args.Length > 1 ? args[1] : null);
            return null;
        }),
        new("--lagtest", "湿墨延时对照（合成鼠标：主绘 / 系统轨迹 / 产品组合，", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 湿墨延时对照：主绘 / 系统轨迹 / 产品组合三档，量墨尖落后与跳变响应。
            WetLagTest();
            return null;
        }),
        new("--lasertest", "激光笔自检（拖尾一直写不缩 / 松手停 2 秒再整体淡出 / 多条并存 /", args =>
        {
            // 激光笔自检（2026-09-27）：拖尾寿命（写过整条不缩、松手停 2 秒再淡）、
            // 多条并存、粗细真的管用、不进文档。顺带出一张图（摆样要看，自检判不了好不好看）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LaserTest();
            return null;
        }),
        new("--lassotest", "套索选择（80% 判据 / 贴边无限延伸 / 加选减选）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LassoTest();
            return null;
        }),
        new("--librarysizetest", "面板尺寸记忆专项（图形/收藏的位置·宽·高 + 重启读回）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LibrarySizeMemoryTest();
            _quit = true;
            return null;
        }),
        new("--librarytest", "[图]  收藏自检（存入/去重/面板/插入/回收站/分类/⋯ 菜单/拖动排序/整组/吸附）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 第二个参数给了路径就顺手出一张面板的图（和 --shapetoolshow 一个用法）。
            // 以 `--` 开头的是别的开关（如 `--uiscale 0.8`），不能当文件名。
            string shot = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                ? args[1] : null;
            LibraryTest(shot);
            return null;
        }),
        new("--listmodels", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ListModelsTest();
            _quit = true;
            return null;
        }),
        new("--logtest", "日志系统自检（session.json / 环境 / 性能摘要 / 导出 zip）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LogTest();
            return null;
        }),
        new("--logupload", "开发用：真传一次运行日志（验日志收集仓 / 令牌是否通）", args =>
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
            return null;
        }),
        new("--longrun", "[秒]      长时运行内存/CPU", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = args.Length > 1 && double.TryParse(args[1], out var s2) ? s2 : 180;
            LongRunTest(secs);
            return null;
        }),
        new("--mathcorpus", "[文件] 批量跑公式语料（默认 调研/公式语料/高中数学-公式语料.txt），列出失败项", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string file = args.Length > 1 ? args[1] : "调研/公式语料/高中数学-公式语料.txt";
            int ok = MathRenderer.RenderCorpus(file, out int total);
            ExitCode = ok == total && total > 0 ? 0 : 1;
            _quit = true;
            return null;
        }),
        new("--mathtest", "公式渲染回归：CSharpMath 渲染 50 个典型 LaTeX 到 reports/math-spike/", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int ok = MathRenderer.RenderSamples("reports/math-spike", out int total);
            Console.WriteLine($"公式 spike：{ok}/{total} 张成功，输出 reports/math-spike/");
            ExitCode = ok == total ? 0 : 1;
            _quit = true;
            return null;
        }),
        new("--memlife", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var ml) ? ml : 10000;
            MemLifeTest(n);
            return null;
        }),
        new("--overlayaudit", "浮层管理自检（无窗口；优先级/捕获/Esc/点外/阻断）", args =>
        {
            // A4.0：浮层优先级/捕获/Esc/点外/阻断；无窗口、不抢前台。
            return OverlayAudit.Run();
        }),
        new("--pageaudit", "PageHost 页面栈自检（无窗口；栈/生命周期/尺寸/几何）", args =>
        {
            // A1：PageHost 页面栈/生命周期/尺寸/标题几何；无窗口、不抢前台。
            return PageAudit.Run();
        }),
        new("--pagetest", "整屏翻页自检（一屏 = 一页：页高 = 视口高、只动相机、到顶就停）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PageTest();
            return null;
        }),
        new("--patterntest", "白板底纹自检（方格/横线/间距 + 数屏幕上的线 + 重铺代价）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PatternTest();
            return null;
        }),
        new("--penlive", "[秒]      真笔延时实测（挂上手写笔写一会儿，出报告）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = args.Length > 1 && double.TryParse(args[1], out var pl) ? pl : 20;
            LatencyLive(secs);
            return null;
        }),
        new("--photobeautify", "<输入目录> <输出目录>  图片美化批量出图（去黄/白纸/轻锐化）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PhotoBeautifyShow(args.Length > 1 ? args[1] : "",
                              args.Length > 2 ? args[2] : "reports/photo-beautify");
            return null;
        }),
        new("--predlinetest", "", args =>
        {
            // 线性预测纯算法自检（2026-10-15 加）：不需要真笔/屏幕（见 SelfTests/PredictLinear.cs）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PredictLinearTest();
            return null;
        }),
        new("--prefetchtest", "分块空闲预取自检（预取命中则再滚一格不重画；含关预取对照）", args =>
        {
            // 分块空闲预取自检：滚两格 → 预取把视口外一圈烘好 → 再滚一格不重画；
            // 自带"关预取就重画"的对照（自证有效）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrefetchTest();
            return null;
        }),
        new("--prevflash", "[--pen] [--left]  “写下一笔时，上一笔闪不闪”专项检测（合成鼠标/合成笔；--left=左侧竖写+横线底纹）", args =>
        {
            // 诊断：**"写下一笔时，上一笔闪不闪"**（用户 2026-10-04 报）。
            // --pen = 走合成笔（PT_PEN + 压感，覆盖 ID2D1Ink 那条真实渲染路）；
            // --left = 用户的复现场景：横线底纹白板 + 屏幕左侧竖写。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrevFlashTest(args.Contains("--pen"), args.Contains("--left"),
                          args.Contains("--ui"), args.Contains("--fast"));
            return null;
        }),
        new("--recoverytest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RecoveryTest();
            return null;
        }),
        new("--restest", "[n]       分辨率对照（离屏层）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var s4) ? s4 : 5000;
            ResolutionTest(n);
            return null;
        }),
        new("--rolltest", "课堂点名自检（功能卡：设置↔结果 / 滚动定格 / 去重池子 / 名单 / 穿透小窗）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RollTest();
            return null;
        }),
        new("--savetest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SaveTest();
            return null;
        }),
        new("--settingsaudit", "设置页后台自检（无窗口、不抢前台；逐项命中/执行/出图）", args =>
        {
            // 无窗口自检：不建覆盖层、不注入鼠标、不抢前台；用户在用电脑时也能跑。
            return SettingsAudit.Run();
        }),
        new("--smoothflashtest", "[--off]  “画的时候闪不闪”专项检测（合成鼠标画过去，看已经画过的墨还动不动）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SmoothFlashTest(!args.Contains("--off"), args.Contains("--fast"));
            return null;
        }),
        new("--smoothtest", "中心线曲线化自检（过点 Catmull-Rom：直角不变形 / 圆弧更圆滑 / 形状不跑）", args =>
        {
            // **纯算法，不建窗口**（和 `--inktest` 同一个口径）：曲线器是离线几何，
            // 它该验的是"直角变没变形 / 圆有没有更圆"，不该混进界面/输入那一套。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = SmoothProbe.Run();
            _quit = true;
            return null;
        }),
        new("--spaceaxistest", "空间直角坐标系自检（一笔画法 / 四轴端 / 整组平移 / 存档 / 面板）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SpaceAxisTest();
            return null;
        }),
        new("--startupcheck", "开机自启装配自检（穿透 + 小球收起 → 再启动后展开 + 退出穿透）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            StartupCheck();
            return null;
        }),
        new("--tailjumptest", "", args =>
        {
            // [重启 2026-10-08] B4 预测尾"突突跳"检测（老系统 2026-10-05 停用；见 已停用-渲染实验.md）
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TailJumpTest(args.Contains("--noisy"), args.Contains("--off"));
            return null;
        }),
        new("--tiletest", "分块缓存自检（边界无缝 / 回程复用 / 内存上界）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TileTest();
            return null;
        }),
        new("--timertest", "课堂计时器自检（四模式：倒计时/正计时/秒表/时间 / 改时长 / 到点 / 收起 206×48 / PDF 自动挂钟）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TimerTest();
            return null;
        }),
        new("--tipsweep", "语言审计：把所有悬停提示过一遍 Loc.Tr（配 INKTEACH_LOC_AUDIT=1）", args =>
        {
            // 语言审计：把所有悬停提示过一遍 Loc.Tr（配合 INKTEACH_LOC_AUDIT=1）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            (Ui as InkUi.FullUi)?.AuditTipsForTest();
            AuditEngineTips();
            _quit = true;
            return null;
        }),
        new("--trailtest", "[--touch]  委托墨迹轨迹对照（默认合成鼠标；--touch = 合成触摸）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InkTrailTest(args.Contains("--touch"));   // `--trailtest --touch` = 合成触摸对照
            return null;
        }),
        new("--updatecheck", "[清单地址] [--apply]  自动更新验收（**会真的换壳**：--apply = 查到就装）", args =>
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
            return null;
        }),
        new("--updatetest", "自动更新自检（离线：解析 / 版本比较 / sha256 / 下载候选 / 换壳脚本沙箱真跑）", args =>
        {
            // **纯离线**（解析 / 版本比较 / sha256 / 本地取清单 / 换壳脚本语法），不碰网络
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = UpdateProbe.Run();
            _quit = true;
            return null;
        }),
        new("--wetdrytest", "[--live 20] 湿墨/干墨交接测量（**要真笔**：在中间那条浅灰线间画一笔）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 20;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--live" && double.TryParse(args[i + 1], out var s)) secs = s;
            WetDryTest(args.Contains("--live"), secs);
            return null;
        }),
        new("--widthtest", "笔迹粗细/压力", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            WidthTest();
            return null;
        }),
    };
}
