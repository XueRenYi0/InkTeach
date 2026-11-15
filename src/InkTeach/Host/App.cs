// [职责] 宿主启动、参数分发、模式路由、用法帮助。
// [测试] --startupcheck

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
            try { if (Directory.Exists(PptStore.RootOverride)) Directory.Delete(PptStore.RootOverride, true); } catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }
        }
        // 文档批注缓存同理：自检每次启动先清空这份临时缓存（和 PptStore 一样，别碰用户真目录）
        if (_selfCheckMode && DocStore.RootOverride == null)
        {
            DocStore.RootOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-doc");
            try { if (Directory.Exists(DocStore.RootOverride)) Directory.Delete(DocStore.RootOverride, true); } catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }
        }
        // 板书历史（0 号页快照）同理：自检绝不写用户的历史目录。
        if (_selfCheckMode && BoardHistory.DirOverride == null)
        {
            BoardHistory.DirOverride = Path.Combine(Path.GetTempPath(), "inkteach-selfcheck-history");
            try { if (Directory.Exists(BoardHistory.DirOverride)) Directory.Delete(BoardHistory.DirOverride, true); } catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }
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

        var modeEntry = FindMode(mode);
        if (!modeEntry.HasValue)
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

        int? modeResult = modeEntry.Value.Run(args);
        if (modeResult.HasValue) return modeResult.Value;

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
            try { File.AppendAllText(_clickLogFile, "clicked\n"); } catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }
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
        // 模式表由注册表生成（ADR-005 D2）；新增模式只改 App.Modes.*.cs，不再手写这份表。
        Console.WriteLine("开发期测试模式（不属于引擎；完整表：InkTeach.exe --help）：");
        foreach (var e in ModeEntries.OrderBy(e => e.Mode, StringComparer.Ordinal))
            Console.WriteLine($"  {e.Mode,-22} {(string.IsNullOrEmpty(e.Help) ? "（待补说明）" : e.Help)}");
        Console.WriteLine("  --list-modes          只输出 mode → 说明（机器可读；AI 用来查改哪跑哪）");
        Console.WriteLine();
        Console.WriteLine("开关（不是模式，和 --xxx 一起用）：");
        Console.WriteLine("  --eraserhud         橡皮读数浮层（左下角实时 速度/目标/当前系数/尺寸；调门槛用，不进界面）");
        Console.WriteLine("  --noprefetch        关掉分块空闲预取（对照；默认开）");
        Console.WriteLine("  --lang <code>       界面语言：system|en|zh-CN|ja|ko|de|fr|es|pt-BR|ru|it（本次运行覆盖设置）");
        Console.WriteLine("  --langaudit         语言覆盖率审计（不建窗口，逐语言报缺失 key / 占位符）");
        Console.WriteLine("  --warp              强制 WARP 软件渲染（无显卡兜底；诊断/性能地板用）");
        Console.WriteLine("  --pdflowmem         强制 PDF 省内存档（4GB 档：渲染宽 1600 / 1 页缓存 / 1s 翻页）");
        Console.WriteLine("  --pdfhighmem        强制 PDF 高清档（尽量按主屏物理宽；单页最多 12MP）");
        Console.WriteLine("  --uiscale <0.8|0.9|1|1.15|1.3>  界面缩放档（工具带/色带/抽屉/设置页；只覆盖本次运行，不落盘）");
        Console.WriteLine("  --motion <名字>     catmull / mean2（**默认 mean2**=距离窗＋过点曲线＋收笔追赶）");
        Console.WriteLine("  --reclive <文件>     输入黑匣子：记录喂进引擎的输入（配 --replayinput 离线复现）");
        Console.WriteLine("  --himetric / --nohimetric  亚像素输入（默认开；--nohimetric 退回 D0 对照）");
        Console.WriteLine("  --notouch           触摸手势总开关**临时关掉**（保险丝 / A-B 对照；不写偏好）");
        Console.WriteLine("  --touchhud          触点诊断浮层（触点数 / 接触面积 / 最近的输入设备）");
        Console.WriteLine("  --wetmode <0..1>    湿墨档位（记住到设置；运行时 Ctrl+Alt+Shift+W 循环切换）：");
        Console.WriteLine("  --syswet            真笔 + 实线笔：湿墨只让系统轨迹画（诊断；⚠ 单条轨迹系统上限 128 点，长笔画会回滚）");
        Console.WriteLine("  --syscursor         三支笔一律系统斜笔（零延迟硬件光标）——**已是默认**，写上等于没写");
        Console.WriteLine("  --nosyscursor       退回自绘落点（笔尖环 / 荧光笔圆盘 / 激光实心点）做对照");
        Console.WriteLine("  --trailonly         自己那一笔不画（湿墨只剩系统轨迹）——手动验证系统到底给哪种输入画");
        // [停用] Console.WriteLine("  --predictdata [路径] 真实笔迹数据上的预测评测（UCI Character Trajectories）");
        Console.WriteLine("  --board             打开白板（不透明底色）：自检截图里只有我们自己的内容");
        Console.WriteLine("  --screenshot <路径> [x y w h]  截屏工具");
        Console.WriteLine("  --clicktarget <日志>           穿透测试用的下层窗口");
        Console.WriteLine("  自检取消：Ctrl+Alt+Shift+C（全局；UI 卡住也能停；终端 Ctrl+C 也行）");
    }

    /// <summary>
    /// 对比实验：同一笔画，开/关 Direct2D 的抗锯齿各画一次。
    /// 用来说明"关掉我们的优化之后，看着仍然平滑"这部分是系统给的。
    /// </summary>
}
