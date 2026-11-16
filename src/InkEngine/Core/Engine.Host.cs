// [职责] 启动、消息循环、窗口过程（WndProc）。
// [测试] --startupcheck

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;

public partial class InkEngine
{
    // =====================================================================
    //  Startup
    // =====================================================================

    internal int Run(string[] args)
    {
        // 设置控制台输出编码。
        // 【为什么必须包 try】：发布版是 GUI 子系统（无控制台），双击启动时进程手里
        // 根本没有控制台句柄，这个 setter 会走 SetConsoleOutputEncoding 抛
        // IOException“句柄无效”，把整个启动流程打断（踩过：双击发布版启动失败）。
        // 有控制台（命令行/带参数自检）时才真正生效，没控制台就跳过——
        // 反正没控制台时所有 Console.WriteLine 本来也是空操作，不影响任何功能。
        //
        // 2026-11-03：**输出被重定向**（自检跑批 / CI）时没有控制台句柄，setter 会抛，
        // 于是 stdout 保持系统 ANSI（936），而 `Run-FuncSuite.ps1` 按 UTF-8 读 →
        // 中文判据（"通过 / 合计"）全变成乱码，用例被误判成"没有判据"（一批 DATA）。
        // 重定向这条路直接把标准流换成 UTF-8，控制台那条保持原样。
        if (Console.IsOutputRedirected)
        {
            try
            {
                var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            }
            catch { /* 有意忽略：日志失败不能再抛（ADR-005 D5） */ }
        }
        else
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 有意忽略：日志失败不能再抛（ADR-005 D5） */ }
        }
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Native.EnableMouseInPointer(true);

        Gfx.Init(args.Contains("--warp"));
        NowMs = _clock.Elapsed.TotalMilliseconds;

        _hInstance = Native.GetModuleHandle(null);
        _className = "InkTeachOverlay_" + Guid.NewGuid().ToString("N");
        if (!RegisterWindowClass())
            return 2;

        string mode = args.Length > 0 ? args[0] : "";
        // ⚠ **命令行开关不能写在第一个位置**这个坑（2026-10-07 修）：
        // 上面这行会让 `InkTeach.exe --norawinput` 把**开关**当成"自检 mode"，
        // 于是 `SelfCheckMode = true` —— 换临时设置路径、关掉导出对话框…
        // 用户想对照一下手感，结果程序进了半个自检状态，**而且他看不出来**。
        // 这里把"不是 mode 的开关"排除掉：单独敲一个开关时，走的还是正常模式。
        // （判据是正向的"已知开关"清单 —— 只列**不是 mode 的那些**；
        //   真敲错了 mode 名的行为不变，仍旧进自检模式。）
        if (IsSwitchNotMode(mode)) mode = "";
        SelfCheckMode = mode.Length > 0;

        if (mode == "--langaudit")
        {
            Loc.AuditAll();
            return 0;
        }
        // 设置页后台自检：**不建覆盖层、不接 PPT、不注册热键、不装键盘看门狗**。
        // 它只摆几何/命中/逐行执行，用户在用电脑时也能跑。
        bool headlessSettingsAudit = mode == "--settingsaudit";
        // `--startupcheck` 是自检模式，但也要走一遍真实的后台待命装配。
        StartupMode = args.Contains("--startup") || args.Contains("--startupcheck");
        _startupPassive = StartupMode;
        // 后台待命不抢键盘/前台；等用户点小球或选工具时再恢复。
        KeyboardMode = !StartupMode;
        if (StartupMode)
            Console.WriteLine("启动模式：开机自启后台待命（穿透 + 工具条收起；点小球或再次双击 exe 激活）");
        _modeName = mode;
        // 这一行**不在这里打** —— 见下面 update 里那句注释。
        // （这里打的话，`--clean 3` 这种"开关写在第一个位置"会被误报成自检模式，
        //   而界面那一层要等到 `RunModeDispatch` 才知道它到底算不算 mode。
        //   2026-10-07 这个误报让我白查了好几轮，所以挪到判定之后。）
        // 产品模式：启动 1.2 秒后（窗口已经露面）做一次历史清理；自检一律不扫。
        if (mode.Length == 0)
        {
            _historySwept = false;
            _historySweepAtMs = NowMs + 1200;
        }

        // 用户偏好（深色主题/贴边隐藏/档位/钉住，以及"启动要不要接上上次的板书"）。
        //
        // **必须读在恢复板书之前**：接不接上次的板书由 `ui.restoreInk` 决定
        // （默认不接，见 RestoreAutoSaveIfAny）。
        // 自检/基准模式**不读**——判据要确定，而且不该拿用户的设置去跑自检。
        if (InkSettings.PathOverride == null && args.Contains("--nosettings"))
            InkSettings.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                              "inkteach-nosettings.json");
        if (!SelfCheckMode)
            foreach (var w in InkSettings.LoadUiPrefs(UiPrefs))
                Console.WriteLine("settings: " + w);

        // 界面语言：命令行 --lang > 用户设置 ui.lang > 安装器写入的语言 > 系统 UI 语言。
        // 必须在创建任何 UI / 文字格式之前初始化。
        string langArg = null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--lang") { langArg = args[i + 1]; break; }
        string langPref = langArg
                          ?? (SelfCheckMode ? null : GetUiPref("lang"))
                          ?? (SelfCheckMode ? null : InkSettings.LoadInstallLanguage());
        Loc.Initialize(langPref);
        if (args.Contains("--lang"))
            Console.WriteLine($"界面语言: {Loc.CurrentCode}（{Loc.CurrentDisplayName}）");

        // 隐私同意单独存一份（不进备份包），恢复设置也不会再弹一次（2026-11-13）。
        // 老版本写在 settings.json 里的记录仍然认；文件优先。
        if (!SelfCheckMode)
        {
            var pv = PrivacyStore.Load();
            if (pv != null)
            {
                UiPrefs["privacy.ver"] = pv.Value.Ver.ToString();
                UiPrefs["privacy.consent"] = pv.Value.Consent;
                UiPrefs["privacy.at"] = pv.Value.At ?? "";
            }
        }

        // 开机自启：Run 值是唯一真源（不另存 settings.json）。
        // 已启用但路径变了（换目录 / 升级）就在这里修回当前 exe。
        if (!SelfCheckMode)
        {
            // 用户点过“开”的意愿记在 ui.autostart；升级/换目录后如果 Run 被安装器
            // 清掉，下一次启动自动补写。注意：Run 值还在、只是被任务管理器禁用时
            // 不能补写——那是用户明确的“不要自启”，必须尊重。
            // 用户可能在任务管理器里把它禁用了。Run 值还在、状态是关，
            // 这时把“想要自启”的旧意愿清掉，免得以后安装器清掉 Run 后
            // 下一次启动又把它擅自打开。
            if (GetUiPref("autostart") == "1" && AutoStart.HasEntry() && !AutoStart.IsEnabled())
            {
                SetUiPref("autostart", null);
                Console.WriteLine("开机自启：任务管理器已禁用，按“关”处理");
            }

            bool wantAutoStart = GetUiPref("autostart") == "1";
            string autoEnableErr = null;
            if (wantAutoStart && !AutoStart.HasEntry()
                && AutoStart.TryEnable(Environment.ProcessPath, out autoEnableErr))
                Console.WriteLine("开机自启：按上次设置补写 Run 值");
            else if (wantAutoStart && !string.IsNullOrEmpty(autoEnableErr))
                Console.WriteLine("开机自启：补写 Run 值失败（不影响本次启动）：" + autoEnableErr);

            AutoStartOn = AutoStart.IsEnabled();
            string autoStartErr = null;
            if (AutoStartOn && AutoStart.EnsureCurrentPath(Environment.ProcessPath, out autoStartErr))
                Console.WriteLine("开机自启：已把记录路径修成当前程序");
            else if (!string.IsNullOrEmpty(autoStartErr))
                Console.WriteLine("开机自启：修路径失败（不影响本次启动）：" + autoStartErr);
        }

        // 点名名单（`%APPDATA%\InkTeach\Names.txt`）：产品模式启动读一次；
        // 自检不读用户的名单（判据要确定），要用就临时设 NamesPathOverride + ReloadNames。
        if (mode.Length == 0) LoadClassroomPrefs();

        // 上次因为界面出问题重启过？把板书读回来（读走就删，只恢复一次）。
        // 自检/基准模式不掺和：那些模式不该被"上次留下的板书"影响判据。
        // 开机自启是“后台待命”，不能一登录就把旧板书铺到桌面上：
        // 恢复留到用户手动启动时做；这里只把自动存档的指纹对上，避免 15 秒后
        // 用空板覆盖掉用户上次的板书。
        if (mode.Length == 0 && !StartupMode)
        {
            RestoreSessionIfAny();
            RestoreAutoSaveIfAny();
        }
        else if (mode.Length == 0)
        {
            PrimeAutoSaveForStartup();
        }

        // 宿主自己的启动分支（开发期的点击目标、截图工具等）。返回 true
        // 表示这条命令行已经由宿主处理完，引擎不再往下走。产品界面不需要覆写。
        if (PrepareHostStartup(mode, args, out int hostExit))
            return hostExit;

        // PPT 放映联动：**只在产品模式下真的接**（自检/基准模式不接——那要碰 COM，
        // 判据会变得不确定；而且探针 `--pptprobe` 自己 new 一个源去问，
        // 不走这条路）。没装 Office / 没开 PPT 时它什么都不做。
        // 开机自启先不接 PPT：等用户真正激活（退出穿透）时再接，
        // 避免一登录就因为放映中的 PPT 自动展开页条/抢前台。
        if (!StartupMode && !headlessSettingsAudit) StartPptLink();

        // --hud：显式打开调试性能面板（默认关，交互里已没有开它的快捷键）。
        // 测"写一笔的内存/延时"就靠它：面板上并列 提交 / 工作集 / 显存 / 笔画数。
        // 放成**显式开关**而不是改默认：普通用户不该看到这个黑框。
        if (args.Contains("--hud")) ShowHud = true;
        // --nohud：关掉调试性能面板。它是给开发看的，每帧要花约 1.9 ms
        // （文字排版 + 进程计数），测底层性能时必须排除掉，否则量到的是
        // 测量工具本身而不是渲染引擎。**两条同时给时以 --nohud 为准。**
        if (args.Contains("--nohud")) ShowHud = false;
        // --fullpresent：每次整屏上屏（不走 Present1 脏矩形）。诊断"固定横线闪"用：
        // 如果加上它就不闪了，说明问题在"部分上屏 + DWM 合成"这条路上（见 Overlay 的说明）。
        if (args.Contains("--fullpresent")) OverlayWindow.FullPresent = true;
        // PDF 内存档案（2026-10-17）：默认按内存自动；这两条是诊断/对照开关。
        if (args.Contains("--pdflowmem")) DocMemory.Force = DocMemoryMode.Low;
        else if (args.Contains("--pdfhighmem")) DocMemory.Force = DocMemoryMode.High;
        // --noprefetch：关掉"视口外一圈"的空闲预取，回到纯同步光栅（对照用）。
        if (args.Contains("--noprefetch")) CanvasTileCache.PrefetchEnabled = false;
        // --traceframes：把每帧的脏区/上屏/分块/预取日志打到控制台（OverlayWindow.Trace）。
        if (args.Contains("--traceframes")) OverlayWindow.Trace = true;
        // 触点诊断（8.3.3）：`--touchhud` 直接开着启动。
        if (args.Contains("--touchhud")) TouchHud = true;
        // 动态橡皮的后门（8.3.4）：关掉"速度→尺寸"，擦除尺寸恒定（不进界面）。
        if (args.Contains("--eraserfixed")) DynamicEraser = false;
        // 橡皮诊断读数（8.3.7，调参用）：左下角浮层实时显示 速度 / 目标 / 当前系数 / 尺寸。
        if (args.Contains("--eraserhud")) { EraserHud = true; RebuildEraserHud(); }

        // 对照实验用：--nohist 关掉脏区的多帧回溯，应当立刻出现残影，
        // 用来证明残影测试本身是有效的（而不是永远通过）。
        if (args.Contains("--nohist")) OverlayWindow.TransientHistoryFrames = 0;
        // ---- 委托墨迹轨迹（笔 / 鼠标 / 触摸都喂）-------------------------------
        //
        // 2026-10-11 起**默认总是开**（除非 `--noinktrail`）：本机它已经真的在画了
        //（以前不画的根因有两处：轨迹对象没挂进视觉树、每帧没把"已上屏的点"结账删掉；
        //  见 Overlay.cs 的 TrimInkTrailAsDrawn / 试验C）。
        // 以前的门槛是"系统里有笔设备才开"——现在鼠标/触摸也喂点，那个门槛没意义了
        //（手写板关 ink 时就是 PT_MOUSE；触摸屏只有 PT_TOUCH）。
        // 实测（2026-10-11，逐帧采样）：鼠标**也画**（拖动中峰值 163 像素的"尾巴"）；
        // 先前"0 像素"是采样太稀漏掉了它（尾巴只存活到我们下一次上屏）。
        // 笔/鼠标/触摸都喂（每消息几次 COM 调用，代价可忽略）。
        // ---- 湿墨档位（0..1）---------------------------------------------------
        // 顺序：**先读设置里记着的档**（没有 = 0 原路径），再看 `--wetmode N`
        //（覆盖 + 记住），最后让老的开发/诊断开关覆盖（`--inktrail` / `--noinktrail`
        // 的判据逐字不变，自检与对照全照旧）。
        WetMode = int.TryParse(GetUiPref(WetModePrefKey), out int wmPref) ? Math.Clamp(wmPref, 0, 1) : 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--wetmode" && int.TryParse(args[i + 1], out int wmArg))
            {
                WetMode = Math.Clamp(wmArg, 0, 1);
                // ⚠ 这里**不能**用 `SelfCheckMode` 把关：这一步跑在 RunModeDispatch 之前，
                // 那个标志还不可信（和 wintab 偏好同一个坑，见下面 _pendingWintabPref 的注释）。
                // 写不写盘交给 Loop() 开头那一段判（那时标志已经定下来了）。
                _pendingWetModePref = WetMode.ToString();
            }
        ApplyWetMode();   // 此时窗口还没建（_windows 为空），这一下只落两个静态开关。
        if (args.Contains("--inktrail")) OverlayWindow.InkTrailEnabled = true;
        if (args.Contains("--noinktrail")) OverlayWindow.InkTrailEnabled = false;

        // `--trailonly`（手动诊断，2026-10-11）：**我们自己不画"正在写的这一笔"**——
        // 于是按住期间屏幕上出现的任何墨就只能是系统轨迹画的。笔/鼠标/触摸都能用，
        // 用来手动回答"系统到底给哪种输入画这条轨迹"（配合 `[笔画]` 行的 `[轨迹 喂N 结账M]`）。
        if (args.Contains("--trailonly")) SuppressActiveStroke = true;
        // `--trailcommit`（A/B，2026-10-11 试验D）：每帧提交一次 DComp（Chromium 的做法）。
        if (args.Contains("--trailcommit")) OverlayWindow.TrailCommitEveryFrame = true;

        // `--syswet`：真笔 + 实线笔时，**湿墨只让系统轨迹画**（我们不再自绘当前笔画）。
        // ⚠ 2026-10-05 真笔实测（判据修好后）：**本机的委托轨迹不渲染**——COM 调用全部
        //    成功、点也喂进去了，但写字期间屏幕上没有任何新增墨（用户亲眼确认：
        //    "写的时候看不见墨迹，抬笔才会出现"）。所以 `--syswet` 在本机只会让湿墨消失，
        //    **默认关、勿启用**；保留它用于换机器/换平板的验证。见 `延时-实测与优化.md` §八。
        // `--ownwet` 强制回老路（A/B 用）。
        OverlayWindow.SystemWetPreferred = args.Contains("--syswet");
        if (args.Contains("--ownwet")) OverlayWindow.SystemWetPreferred = false;

        // PPT 连接诊断（--pptdebug）：把 ROT 扫描每一步打出来（排查用，平时关）。
        PptComSource.Debug = args.Contains("--pptdebug");
        ComLate.Debug = PptComSource.Debug;

        // ---- 笔迹预测：三档收口（2026-10-15）----------------------------------------
        // [删除 2026-10-05] `--predict/--predictms/--predictlead`（老预测系统）与
        // `--predicttip`（预测点并入 mean2）的入口、喂点与渲染尾接线已全部移除。
        // [2026-10-15] 经典 B4 / Google / DL（`--predict3`）退役、不进编译；唯一实现 = v6。
        // 备份 `.revert/2026-10-15-预测收口/`，说明 `调研/已停用-预测算法收口.md`。

        // ---- 书写期间的 GC 低延迟档 -------------------------------------------
        //
        // 默认**开**：书写期间用 SustainedLowLatency 抑制"前台第 2 代回收"
        //（那是最重的一种回收，一次几十毫秒，落在书写中间就是"笔突然卡一下"）。
        // 详见 GcLatency.cs 的说明与两个坑。
        //
        // `--nogclatency` 关掉它，专门用来做"开 / 不开"的对照测量——
        // 低配机上到底是不是它对卡顿有用，得靠这个开关量，不能凭感觉。
        GcLatency.Enabled = !args.Contains("--nogclatency");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--gchold" && double.TryParse(args[i + 1], out double gh))
                GcLatency.HoldMs = Math.Clamp(gh, 0, 60000);

        // ---- 压感 → 粗细 ------------------------------------------------------
        //
        // 默认**开**：设备报压力就按压力改粗细——和系统墨迹（Tablet PC / OneNote 那一档）
        // 同一个口径：最小压力 50%、最大 150%（见 PressureWidth）。
        //
        // 两个开关都是**给真机调手感用的**，不是给用户平时按的：
        //   · `--nopressure` 关掉，用来做"有/无"对照（差异要当场看得出来才算数）；
        //   · `--pressrange min,max[,gamma]` 现调动态范围与曲线，不用重编。
        _noPressureArg = args.Contains("--nopressure");
        PressureWidth.Enabled = !_noPressureArg;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--pressrange") continue;
            var parts = args[i + 1].Split(',');
            if (parts.Length >= 2
                && float.TryParse(parts[0], out float pmin)
                && float.TryParse(parts[1], out float pmax)
                && pmax > pmin)
            {
                PressureWidth.Min = Math.Clamp(pmin, 0.05f, 2f);
                PressureWidth.Max = Math.Clamp(pmax, 0.10f, 3f);
                if (parts.Length >= 3 && float.TryParse(parts[2], out float pg) && pg > 0.05f)
                    PressureWidth.Gamma = Math.Clamp(pg, 0.1f, 4f);
            }
        }

        // ---- 用户开关：压感粗细（2026-10-01，「更多 → 设置 → 书写」）----------
        //
        // 默认**开**；偏好只写"关过"的那一份（`ui.pressure = "0"`）。
        // **命令行优先**：`--nopressure` 是给"有/无"对照实验用的，它存在时不听偏好——
        // 否则自检/实验机器上读到的用户偏好会把对照条件悄悄改掉。
        // 自检模式下 `UiPrefs` 根本没从盘上读（见上面 LoadUiPrefs 那一段），
        // 所以这里拿到的永远是空 → Enabled 就是命令行/默认值，判据稳定。
        if (!_noPressureArg && GetUiPref(PressurePrefKey) == "0")
            PressureWidth.Enabled = false;

        // ---- 精细笔迹（2026-10-07 加开关；**2026-10-09 晚用户定：开关撤掉、常开**）------
        //
        // 它管的是「**原始输入补点**」：系统会把来不及投递的移动合并成一条消息，
        // 只取最新那一个等于把采样率砍半（真机实测：指针消息 61Hz，而设备实际报了 190Hz）。
        // 补点就是按原始报率把中间点捞回来。用户真机用了一整天："比较稳定了"——
        // 不再给用户关的开关（设置页那一行已撤）。只剩开发对照：`--norawinput` 仍可关。
        if (_noRawInputArg)
            RawInputCapture = false;

        // ---- 模拟压力与笔锋：**已全部停用**（2026-10-05，代码保留）----------------
        // [停用] `--simpressure/--simpressdepth`（Xournal++ 速度压力）、
        // `--pfpressure/--pfthinning/--pfstreamline`（perfect-freehand 速度压力）、
        // `--simtaper`（固定两端锥）、`--flicktip`（末尾甩速收尖，入口已删除）。
        // 恢复方法见 `已停用-渲染实验.md`；停用前完整源码在 `.revert/2026-10-05-渲染减法/`。
        PressureSim.Enabled = false;
        PressureSim.UsePf = false;
        // （这里原来还有各开关的解析与 flicktip 速度门控块；停用与删除的原文见登记文档。）
        PressureSim.BumpVersion();

        // ---- 中心线曲线化（过点 Catmull-Rom ＋ 角点保护）------------------------
        //
        // **默认开**（2026-09-28 用户拍板："默认开也没关系"）：把"逐点直线段"换成
        // "过每一个采样点的三次贝塞尔"，急转处由角点保护切断切线。
        // **不改存档里的点、不改形状语义**——曲线严格过点，直角/顿笔/尖角原样保留；
        // 正在写的那一笔走折线（`Stroke.RawWhileLive`），落笔那一刻才换成曲线。
        //
        //   --nosmooth          退回折线（做"开 / 关"对照用）
        //   --smoothcorner N    角点阈值（度）。默认 35：转得比它急就保留尖角。
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--smoothcorner" && float.TryParse(args[i + 1], out float sc))
            {
                StrokeSmoothing.CornerAngleDeg = Math.Clamp(sc, 5f, 90f);
                StrokeSmoothing.BumpVersion();
            }
            // [停用 2026-10-05] `--smoothmacropx`（宏观角点窗）：当天实测变"脏"，已回退默认 0。
            // 恢复见 `已停用-渲染实验.md`。
            // else if (args[i] == "--smoothmacropx" && float.TryParse(args[i + 1], out float mp) && mp >= 0)
            // {
            //     StrokeSmoothing.CornerMacroPx = Math.Clamp(mp, 0f, 200f);
            //     StrokeSmoothing.BumpVersion();
            // }
        }

        // ---- 笔迹运动模型（2026-10-03 对照台 → 2026-10-05 收敛）------------------
        // 保留两个模式：**mean2（默认，M6）** 与 **catmull（M1 老路径，对照/兜底）**。
        // [停用 2026-10-05] raw / sliding / spring / oneeuro / mean / gauss，
        // 及 `--nosmooth`、`--inkmodel`、`--inkm*`、`--slidewin`、`--motionwin` 等参数：
        // 代码保留（StrokeMotion 内对应分支未动），恢复见 `已停用-渲染实验.md`。
        // 仍然**只做渲染期加工**：存档里的点、命中、撤销、橡皮一概不动。
        {
            var motionMode = StrokeMotionMode.Mean2;   // 2026-10-04 默认档（用户定稿）
            // **过点曲线化总开关**（`--smooth` / `--nosmooth`）：2026-10-06 接回来的。
            // 它之前被注释掉、而 `StrokeMotion.SetMode` 又把它按模式强制关掉，
            // 结果是"文档说默认开、`--nosmooth` 是对照开关"，实际**永远关着**：
            // `--nosmooth` 没有任何效果，`--smoothshow` 出的 off/on 两张图逐像素相同
            // （实测：不同点数 = 0）。现在它只管它该管的——曲线化开不开。
            if (args.Contains("--nosmooth")) StrokeSmoothing.SetEnabled(false);
            if (args.Contains("--smooth")) StrokeSmoothing.SetEnabled(true);
            // 自绘预测尾（2026-10-08 用户 90Hz 真机验收 → 2026-10-09 改默认关 →
            // 2026-10-14 发布测试版起默认开 → **2026-10-16 用户定：以后默认关**）。
            // 2026-10-15：接入线性预测（Chromium 同族）、v6 退役（见 `调研/已停用-预测算法收口.md`）。
            // 设置页「墨迹预测」开关，偏好落盘 `ui.predict2`（**默认关，只写"开过"的那一份**）；
            // `--nopredict2` 强关、`--predict2` 强开，两者都给对照实验用、优先于偏好。
            // **时长不可调**（用户定）：地平线按刷新率自动缩放，见 InkPredictorLinear。
            _noPredictArg = args.Contains("--nopredict2");
            _forcePredictArg = args.Contains("--predict2");
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--wetpred" && float.TryParse(args[i + 1], out float wp))
                    _wetPredScale = Math.Clamp(wp, 0f, 1f);
            ApplyPredictTailSetting();
            // 光标风格（2026-10-16 用户拍板：**三支笔统一系统斜笔＝默认**）：
            // `--nosyscursor` 退回"自绘落点"（笔尖环 / 圆盘 / 实心点）做对照；
            // `--syscursor` 是默认行为，保留写法（写不写一样）。
            if (args.Contains("--nosyscursor")) SysCursorAll = false;
            else if (args.Contains("--syscursor")) SysCursorAll = true;
            // [停用] if (args.Contains("--inkmodel")) motionMode = StrokeMotionMode.Spring;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--motion") continue;
                motionMode = args[i + 1].ToLowerInvariant() switch
                {
                    // [停用] "raw" or "m0" => StrokeMotionMode.Raw,
                    "catmull" or "m1" => StrokeMotionMode.Catmull,
                    // [停用] "sliding" or "m2" => StrokeMotionMode.Sliding,
                    // [停用] "spring" or "m3" => StrokeMotionMode.Spring,
                    // [停用] "oneeuro" or "m4" => StrokeMotionMode.OneEuro,
                    // [停用] "mean" or "m5" => StrokeMotionMode.Mean,
                    "mean2" or "m6" => StrokeMotionMode.Mean2,
                    // [停用] "gauss" or "m7" => StrokeMotionMode.Gauss,
                    _ => motionMode,
                };
            }
            // InkModel.ApplyParamsFromArgs(args);      // M3 参数（--inkm*）[停用]
            StrokeMotion.ApplyParamsFromArgs(args);  // mean2 参数（--mean2win 等）
            // 窗口判据的臂长上限：`--smoothcornerpx N`（0 = 关护栏，用于做"修前/修后"对照）
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--smoothcornerpx" && float.TryParse(args[i + 1], out float scp))
                {
                    StrokeSmoothing.CornerWindowMaxPx = Math.Clamp(scp, 0f, 400f);
                    StrokeSmoothing.BumpVersion();
                }
            }
            // 几何路径诊断（`--geomtrace`）：每一笔报一次走了哪条岔路
            if (args.Contains("--geomtrace")) GeomTraceOn = true;
            // 转角连续性阈值（`--smoothcornerdeg N`，0 = 关，做"修前/修后"对照）
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--smoothcornerdeg" && float.TryParse(args[i + 1], out float scd))
                {
                    StrokeSmoothing.CornerSmoothDeg = Math.Clamp(scd, 0f, 90f);
                    StrokeSmoothing.BumpVersion();
                }
            }
            if (args.Contains("--cornerdump")) StrokeSmoothing.DumpTurns = true;
            if (args.Contains("--notrend")) StrokeSmoothing.CornerUseTrend = false;
            // 原始输入默认**开**（2026-10-07 用户定："开不开 ink 要有一样的手写体验"）。
            // `--norawinput` 关掉做对照；`--rawprobe` 单独打开"只数条数"的诊断。
            _noRawInputArg = args.Contains("--norawinput");
            RawInputCapture = !_noRawInputArg;
            RawProbeEnabled = RawInputCapture || args.Contains("--rawprobe");
            // Wintab 默认**开**（2026-10-07）：关 ink 时笔被当鼠标报、那条路上没有压力，
            // Wintab 是驱动自己的通道，能补回压力（实测 0~16383，比 Windows Ink 的 1024 细 16 倍）。
            // `--nowintab` 关掉做对照。**只在有厂商驱动的板子上有效**，没有就自动不启用。
            // Wintab **默认关**（2026-10-07 用户定的）。
            //
            // 沿革：先默认开（想让"关 ink 也有压感"），后来用户实测"关 ink 快速画圆时很脏"
            // —— Wintab 只借压力、不借坐标，所以形状没问题，**脏在"宽度一跳一跳"**。
            // 权衡之后用户拍板：**默认关，不留界面，以后有需要再开**。
            // 这符合这个项目一贯的规矩：**保守优先**。
            //
            // 要开的话：`--wintab`（**会记住**，见下面 WriteUiPref —— 不用每次敲）。
            // ⚠ Wintab 的开关**在上面那一处统一决定**（别在这里再写一遍 ——
            // 之前就是两处各写一半，"记住"那条链子当场被盖掉）。
            // `--clean N`：入笔前对位置与压力做 N 点滑动平均（0 = 关闭，默认）。
            // 见 AddPointCleaned 那段注释：治"细毛刺"和"压力台阶"，滞后比加大距离窗小得多。
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--clean" && int.TryParse(args[i + 1], out int cw))
                    CleanWindow = Math.Clamp(cw, 0, 15);
            // `--strokefile`：把每一笔的 [笔画] 报告**追加到文件**（默认关，零开销）。
            // 给"调手感"用：那一行里的"压力 a~b/上限"是唯一依据，
            // 而截图/转述都会丢信息（今天因此栽过三次）。
            // 不给路径就用工作目录下的 `笔画日志.txt`。
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "--strokefile") continue;
                string p = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "";
                _strokeFilePath = p.Length > 0 ? p : System.IO.Path.Combine(
                    Environment.CurrentDirectory, "笔画日志.txt");
                Console.WriteLine($"  笔画日志 → {_strokeFilePath}");
            }
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--rawmindist" && float.TryParse(args[i + 1], out float rmd))
                    RawMinDistPx = Math.Clamp(rmd, 0f, 50f);
                if (args[i] == "--rawmax" && int.TryParse(args[i + 1], out int rmx))
                    RawMaxPerStroke = Math.Clamp(rmx, 0, 100000);
            }
            StrokeMotion.SetMode(motionMode);
            InkModel.SetEnabled(false);              // M3 弹簧 [停用 2026-10-05]

            string modeDesc = StrokeMotion.Mode switch
            {
                StrokeMotionMode.Catmull => $"catmull（M1：过点曲线＋角点保护，角点阈值 {StrokeSmoothing.CornerAngleDeg}°；活笔走折线）",
                _ => $"mean2（M6：距离窗 {StrokeMotion.Mean2WindowPx:F0}px ＋ 过点曲线 ＋ 收笔追赶"
                     + (StrokeMotion.Mean2Gauss
                         ? $" ＋ 高斯权σ {StrokeMotion.Mean2SigmaSlow:F1}→{StrokeMotion.Mean2SigmaFast:F1}px"
                           + (StrokeMotion.Mean2TipBlendMax > 0f ? $" ＋ 混笔尖{StrokeMotion.Mean2TipBlendMax:F2}" : "")
                         : "")
                     + "）",
            };
            Console.WriteLine($"笔迹运动模型: {modeDesc}");
        }

        // ---- D1：亚像素输入（默认开；`--nohimetric` 退回 D0 对照）--------------
        // 2026-10-08：真机验收（用户手写板 himetric 映射 682/851 点、0 退回）→ 默认开。
        // 拿不到 himetric 的设备（多数鼠标/触摸）逐点自动退回整数像素，行为与 D0 一致。
        InputPrecision.Reset();
        InputPrecision.UseHimetric = !args.Contains("--nohimetric");
        Console.WriteLine(InputPrecision.UseHimetric
            ? "输入精度: himetric 亚像素（默认；逐点拿不到时自动退回整数像素；--nohimetric 对照）"
            : "输入精度: 整数像素（D0；--nohimetric 对照档）");

        // ---- 呈现节奏 ---------------------------------------------------------
        // 默认改成"等到合成边界再抽输入、立刻 Present(0)"。实测这一项把
        // "Present 返回 → 像素亮"从 3.5 个刷新周期压到 1 个（见
        // reports/延时-实测与优化.md）。--nopace 退回老的 Present(1) 阻塞行为。
        OverlayWindow.VBlankPaced = !args.Contains("--nopace");
        if (OverlayWindow.VBlankPaced) OverlayWindow.LatencyWaitEnabled = true;

        _virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        _virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        _virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        _virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        // 主屏尺寸：`--rawinput` 里换算"绝对原始报"的归一化基准（0..65535 → 主屏像素）。
        _primaryW = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        _primaryH = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        if (_primaryW <= 0) _primaryW = _virtualW;
        if (_primaryH <= 0) _primaryH = _virtualH;

        Console.WriteLine($"virtual desktop: {_virtualW}x{_virtualH} at ({_virtualX},{_virtualY})"
                          + $"；主屏 {_primaryW}x{_primaryH}");

        if (!headlessSettingsAudit && !CreateOverlays())
            return 3;

        // 键位：先读用户配置的覆盖项，再注册全局热键。读坏了不阻塞启动——
        // 用默认键位跑起来，但把问题逐条打出来（静默回退最坑人）。
        foreach (var w in InkSettings.Load(Keys))
            Console.WriteLine("settings: " + w);

        // 自动更新的来源：settings.json 覆盖默认值（默认是空的 = 不检查）。
        // 放这里读，是为了"用户改配置文件不用重新编译"。
        UpdateFeed.Url = InkSettings.LoadUpdateUrl() ?? "";

        // **有更新源 → 初始状态就是"检查更新"**（而不是"未配置源"）。
        // `UpdateText` 的默认值是"未配置更新源"，那是给"真的一个源都没有"准备的；
        // 以前不管有没有源，一打开就显示"检查更新（未配置源）"，看着像坏了
        // （用户 2026-09-29 反馈）。界面那边 `UpdateStage.Idle` 显示的就是"检查更新"。
        if (UpdateFeed.HasAnySource)
        {
            UpdateState = UpdateStage.Idle;
            UpdateText = "";
        }

        // **笔的 8 个色位**：默认 = PenBand；老配置的 penCustom 迁到最后一格。
        // 每个色位都能被用户换色，所以不再只存一个“自定义色”。
        for (int i = 0; i < PenSwatchColors.Length; i++)
            PenSwatchColors[i] = InkPalette.PenBand[i].Color;
        PenSwatchSelected = -1;
        var savedSwatches = GetUiPref("penSwatches");
        if (!string.IsNullOrEmpty(savedSwatches))
        {
            var parts = savedSwatches.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < PenSwatchColors.Length; i++)
            {
                var c = ParseColorPref(parts[i], default);
                if (c.A > 0.5f) PenSwatchColors[i] = c;
            }
        }
        else
        {
            // 老版本只有一个自定义槽：把它搬到最后一位。
            var oldCustom = ParseColorPref(GetUiPref("penCustom"), default);
            if (oldCustom.A > 0.5f) PenSwatchColors[^1] = oldCustom;
        }

        if (int.TryParse(GetUiPref("penSwatchSelected"), out int savedSel)
            && savedSel >= -1 && savedSel < PenSwatchColors.Length)
            PenSwatchSelected = savedSel;
        else if (GetUiPref("penCustomSelected") == "1")
            PenSwatchSelected = PenSwatchColors.Length - 1;
        else if (int.TryParse(GetUiPref("penColor"), out int oldPenIdx)
                 && oldPenIdx >= 0 && oldPenIdx < PenSwatchColors.Length)
            PenSwatchSelected = oldPenIdx;

        if (PenSwatchSelected >= 0)
            CurrentColor = PenSwatchColors[PenSwatchSelected];
        else
        {
            // 外部改色（选中面板等）没有“第几个色位”，单独存 RGB。
            var savedColor = ParseColorPref(GetUiPref("penColorValue"), default);
            if (savedColor.A > 0.5f) CurrentColor = savedColor;
            else if (int.TryParse(GetUiPref("penColor"), out int penIdx)
                     && penIdx >= 0 && penIdx < PenSwatchColors.Length)
                CurrentColor = PenSwatchColors[penIdx];
        }

        // 旧字段只给“恢复上一版”用；新代码不再读它。

        _recentPenColors.Clear();
        foreach (var s in (GetUiPref("penRecent") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = ParseColorPref(s, default);
            if (c.A > 0.5f && _recentPenColors.Count < RecentPenColorMax) _recentPenColors.Add(c);
        }

        // ---- 荧光笔的 5 个色位 + 5 档浓度 ----
        for (int i = 0; i < HighlighterSwatchColors.Length; i++)
        {
            var c = InkPalette.HighlighterBand[i].Color;
            HighlighterSwatchColors[i] = new Color4(c.R, c.G, c.B, 1f);
            HighlighterSwatchOpacity[i] = 2;
        }
        var savedHlSwatches = GetUiPref("hlSwatches");
        if (!string.IsNullOrEmpty(savedHlSwatches))
        {
            var parts = savedHlSwatches.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < HighlighterSwatchColors.Length; i++)
            {
                var c = ParseColorPref(parts[i], default);
                if (c.A > 0.5f)
                    HighlighterSwatchColors[i] = new Color4(c.R, c.G, c.B, 1f);
            }
        }
        var savedHlOps = GetUiPref("hlOpacities");
        if (!string.IsNullOrEmpty(savedHlOps))
        {
            var parts = savedHlOps.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < HighlighterSwatchOpacity.Length; i++)
            {
                if (int.TryParse(parts[i], out int lv))
                    HighlighterSwatchOpacity[i] = Math.Clamp(lv, 0, InkPalette.HighlighterOpacityLevels.Length - 1);
            }
        }

        if (int.TryParse(GetUiPref("hlSwatchSelected"), out int savedHlSel)
            && savedHlSel >= -1 && savedHlSel < HighlighterSwatchColors.Length)
            HighlighterSwatchSelected = savedHlSel;
        else if (int.TryParse(GetUiPref("hlColor"), out int hlIdx)
                 && hlIdx >= 0 && hlIdx < HighlighterSwatchColors.Length)
            HighlighterSwatchSelected = hlIdx;
        else
            HighlighterSwatchSelected = 0;

        if (HighlighterSwatchSelected >= 0)
        {
            int si = HighlighterSwatchSelected;
            HighlighterCurrent = InkPalette.ToHighlighter(
                HighlighterSwatchColors[si], InkPalette.HighlighterOpacityLevels[HighlighterSwatchOpacity[si]]);
        }
        else
        {
            var savedHlColor = ParseColorPref(GetUiPref("hlColorValue"), default);
            float savedHlAlpha = 0.32f;
            if (float.TryParse(GetUiPref("hlColorOpacity"), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float a))
                savedHlAlpha = Math.Clamp(a, 0.02f, 1f);
            if (savedHlColor.A > 0.5f)
                HighlighterCurrent = InkPalette.ToHighlighter(savedHlColor, savedHlAlpha);
        }

        _recentHighlighterColors.Clear();
        foreach (var s in (GetUiPref("hlRecent") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = ParseColorPref(s, default);
            if (c.A > 0.5f && _recentHighlighterColors.Count < RecentHighlighterColorMax)
                _recentHighlighterColors.Add(new Color4(c.R, c.G, c.B, 1f));
        }

        // **粗细档**（每个工具分开记，用户 2026-09-30 定）：存的是"第几档"。
        // **换表就升键名**：老下标按新表读会错位。2026-10-15 笔（15→20 档，`w4.pen`）；
        // 2026-10-16 荧光笔（5→21 档，`w3.hl`）/ 激光（7→12 档，`w3.laser`）；
        // 橡皮的表没动，仍 `w2.*`。读不到就按"老下标 → 老表取值 → **吸附**进新表"迁移。
        if (int.TryParse(GetUiPref("w4.pen"), out int wPen4) && wPen4 >= 0 && wPen4 < WidthPresets.Length)
        { WidthPresetIndex = wPen4; PenWidthLogical = WidthPresets[wPen4]; }
        else if (MigrateWidthPref("w3.pen", Legacy15WidthPresets, WidthPresets, ref WidthPresetIndex, ref PenWidthLogical)) { }
        else if (MigrateWidthPref("w2.pen", Legacy9WidthPresets, WidthPresets, ref WidthPresetIndex, ref PenWidthLogical)) { }
        else if (MigrateWidthPref("w.pen", LegacyWidthPresets, WidthPresets, ref WidthPresetIndex, ref PenWidthLogical)) { }
        // **2026-10-16 荧光笔 / 激光换表**（8–88 步 4 / 2–24 步 2）：键名同样升一代，
        // 老下标按老表取值、**吸附**进新表。
        if (int.TryParse(GetUiPref("w3.hl"), out int wHl3) && wHl3 >= 0 && wHl3 < HighlighterWidthPresets.Length)
        { HighlighterWidthIndex = wHl3; HighlighterWidthLogical = HighlighterWidthPresets[wHl3]; }
        else if (MigrateWidthPref("w2.hl", new[] { 8f, 13f, 18f, 25f, 32f }, HighlighterWidthPresets, ref HighlighterWidthIndex, ref HighlighterWidthLogical)) { }
        else if (MigrateWidthPref("w.hl", new[] { 8f, 18f, 32f }, HighlighterWidthPresets, ref HighlighterWidthIndex, ref HighlighterWidthLogical)) { }
        if (int.TryParse(GetUiPref("w3.laser"), out int wLaser3) && wLaser3 >= 0 && wLaser3 < LaserWidthPresets.Length)
        { LaserWidthIndex = wLaser3; LaserWidthLogical = LaserWidthPresets[wLaser3]; }
        else if (MigrateWidthPref("w2.laser", new[] { 4f, 6f, 8f, 11f, 14f, 18f, 22f }, LaserWidthPresets, ref LaserWidthIndex, ref LaserWidthLogical)) { }
        else if (MigrateWidthPref("w.laser", new[] { 4f, 8f, 14f, 22f }, LaserWidthPresets, ref LaserWidthIndex, ref LaserWidthLogical)) { }
        // 2026-11-03：前面加了一档 14，旧下标会整体错位 —— 新 key 存新下标，
        // 旧的 w2.pixel 按**旧档表的值**翻译过来（不能直接当新下标用）。
        if (int.TryParse(GetUiPref("w3.pixel"), out int wPixel3) && wPixel3 >= 0 && wPixel3 < PixelEraserWidthPresets.Length)
        { PixelEraserWidthIndex = wPixel3; PixelEraserWidthLogical = PixelEraserWidthPresets[wPixel3]; }
        else if (MigrateWidthPref("w2.pixel", new[] { 46f, 70f, 93f, 122f, 150f },
                                  PixelEraserWidthPresets, ref PixelEraserWidthIndex, ref PixelEraserWidthLogical)) { }
        else if (MigrateWidthPref("w.pixel", new[] { 46f, 93f, 150f }, PixelEraserWidthPresets, ref PixelEraserWidthIndex, ref PixelEraserWidthLogical)) { }
        // 上次用的橡皮形态（面积擦 / 整笔擦）。新用户默认面积擦（2026-10-25 用户定），
        // 老用户仍按上次记忆——两种 pref 都要认，不能只认 "pixel"。
        string eraserPref = GetUiPref("eraserKind");
        if (eraserPref == "whole") _eraserKind = Tool.Eraser;
        else if (eraserPref == "pixel") _eraserKind = Tool.PixelEraser;
        // 上次用的线型（实线 / 虚线 / 点线）
        if (int.TryParse(GetUiPref("lineDash"), out int dash) && dash >= 0 && dash <= 2)
            LineDash = (StrokeDash)dash;
        // 「箭头清屏」（2026-11-12）：只在桌面批注生效。
        ClearOnArrow = GetUiPref("clearOnArrow") == "1";
        // **粗细的"数值"版**：滑条拖动现在**吸附在档上**，所以这份数值正常就等于某档；
        // 早期版本存过任意值（如 2.37）——读回来时吸附到最近的档，界面不会显示一个"不存在"的档。
        if (float.TryParse(GetUiPref("wv.pen"), out float vPen))
        { PenWidthLogical = Nearest(WidthPresets, vPen); WidthPresetIndex = NearestIndex(WidthPresets, PenWidthLogical); }
        if (float.TryParse(GetUiPref("wv.hl"), out float vHl))
        { HighlighterWidthLogical = Nearest(HighlighterWidthPresets, vHl); HighlighterWidthIndex = NearestIndex(HighlighterWidthPresets, HighlighterWidthLogical); }
        if (float.TryParse(GetUiPref("wv.laser"), out float vLaser))
        { LaserWidthLogical = Nearest(LaserWidthPresets, vLaser); LaserWidthIndex = NearestIndex(LaserWidthPresets, LaserWidthLogical); }
        if (float.TryParse(GetUiPref("wv.pixel"), out float vPixel))
        { PixelEraserWidthLogical = Nearest(PixelEraserWidthPresets, vPixel); PixelEraserWidthIndex = NearestIndex(PixelEraserWidthPresets, PixelEraserWidthLogical); }
        // **界面缩放**（2026-10-10，档表见 UiScalePresets）：配置里存数值（"1.3"），读回先吸附。
        // `--uiscale` 只覆盖**本次运行**、不落盘——自检出图与换机对照用。
        if (float.TryParse(GetUiPref("uiscale"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float usPref))
            UiScale = UiScalePresets.Snap(usPref);
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--uiscale" && float.TryParse(args[i + 1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float usArg))
                UiScale = UiScalePresets.Snap(usArg);
        // 上次用的选择方式（矩形 / 套索）
        if (GetUiPref("selMode") == "lasso") SelMode = SelectMode.Lasso;
        // 触摸手势的旋钮（8.4.0）：双指总闸 / 手掌擦 / 三指擦 / 长按选择 / 两指点选 / 单指漫游。
        LoadTouchPrefs();
        // --notouch：触摸手势总开关**临时关掉**（保险丝 / A-B 对照；不写偏好）。
        // 优先于设置：命令行给的这一份盖过 "touch.gestures"。
        if (args.Contains("--notouch")) _touch.Enabled = false;

        // **上一次是自动更新装上来的吗**：换壳脚本会在更新目录里留一个 done.txt。
        // 看到它 = 本次启动就是"更新完的第一次启动"，在界面上明说一句
        // （「更多」抽屉那一行会显示"已更新到 x.y.z"），然后把标记删掉——只说一次。
        // 为什么要这一步：换壳重启之后界面原本什么提示都没有，老师根本不知道成没成。
        try
        {
            string done = Path.Combine(UpdateFeed.UpdateDirFor(UpdateFeed.CurrentVersion), "done.txt");
            if (File.Exists(done))
            {
                File.Delete(done);
                UpdateState = UpdateStage.UpToDate;
                UpdateText = $"已更新到 {UpdateFeed.CurrentVersion}";
                Console.WriteLine($"自动更新：本次启动是换壳更新上来的（{UpdateFeed.CurrentVersion}）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("自动更新：检查 done.txt 失败：" + ex.Message);
        }

        // 设置页后台自检是无窗口模式：**不注册全局热键**（否则用户正在用的键会被抢），
        // 也不启动键盘取消看门狗。
        if (!headlessSettingsAudit) RegisterHotkeys();

        // 自检取消看门狗（2026-10-18）：独立线程读键，不依赖 UI 的 WM_HOTKEY。
        // --help / --clicktarget 是一次性/辅助进程，不需要。
        if (SelfCheckMode && !headlessSettingsAudit && mode != "--help" && mode != "--clicktarget")
            SelfTestCancel.Start();

        // 自检/基准模式下面板默认关掉，除非显式 --hud。
        //
        // 为什么：面板是个大黑框，画在屏幕左上角**压在一切之上**。而很多自检的
        // 判据恰恰是"数屏幕上的墨"（--coordtest / --inputtest / --erasertest /
        // --ghosttest / --tiletest / --appendtest / --passtest / --rotatetest…），
        // 面板往上一盖，那些用例会**假失败**——面板越大盖得越多，这次把面板
        // 放大到 700×258 逻辑像素就当场踩到了。交互模式仍然默认开着。
        if (mode.Length > 0 && !args.Contains("--hud")) ShowHud = false;

        // 界面是在 RunModeDispatch 里 Attach 的：先把“后台待命”的穿透态摆好，
        // FullUi 首次 Attach 才能据此收成小球而不是展开工具条。
        if (StartupMode) SetPassThrough(true, restoreBoard: false);

        int dispatch = RunModeDispatch(mode, args);
        if (dispatch >= 0)
            return dispatch;

        // 走到这里说明是**交互模式**（2026-10-17 用户："这里好多命令，能不能精简一些"）。
        // 原来每次启动都把**完整参数表 + 键位表**（三四十行）打到控制台：老师双击时
        // 根本看不见，而从命令行起的时候反而把启动日志（和日志文件）淹了。
        // 现在：控制台只留一行指路；**键位表仍然写进日志**（排查要用，见 DiagLog.LogOnly）；
        // 完整参数表挪到 `InkTeach.exe --help`（见 App.RunModeDispatch）。
        Console.WriteLine($"InkTeach {ProductInfo.Version} 已启动（参数与快捷键：InkTeach.exe --help）");
        if (Keys.GlobalFailures.Count > 0)
        {
            Console.WriteLine("  ！这些全局键没注册上（被别的程序占了？换一个键）：");
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    " + f);
        }
        DiagLog.LogOnly("键位表：\n" + Keys.ToText());

        Loop();
        Shutdown();
        return ExitCode;
    }

    /// <summary>
    /// 启动阶段、窗口创建之前的宿主分支。引擎本身没有这样的模式，
    /// 开发期的截图/点击目标工具挂在宿主（InkTeach）里。
    /// </summary>
    protected virtual bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }

    /// <summary>
    /// 读回"因为界面出问题而重启"之前暂存的板书。**读走就删**（只恢复一次），
    /// 太旧的由 <see cref="Recovery.TakeSession"/> 自己丢掉。
    /// </summary>
    private void RestoreSessionIfAny()
    {
        var blob = Recovery.TakeSession();
        if (blob == null) return;

        try
        {
            // 暂存的也是**白板槽**（2026-10-16）：装回白板并直接进白板给它看。
            var strokes = InkSerializer.LoadStrokes(blob, out int maxId);
            // **空暂存不打开白板**（2026-11-13）：换语言重启时如果当时在桌面批注，
            // 旧路径会恢复一份 0 笔的会话并把 BoardOn 打开，看着像"莫名弹白板"。
            if (strokes.Count == 0)
            {
                Console.WriteLine("会话暂存是空的：不切白板");
                return;
            }
            Doc.LoadPageContent(InkDocument.BoardPageKey, strokes, maxId);
            _boardReturnKey = 0;
            Doc.SwitchPage(InkDocument.BoardPageKey);
            BoardOn = true;
            Doc.InvalidateAll();
            Console.WriteLine($"已恢复上次界面出问题时暂存的板书（白板）：{strokes.Count} 笔");
        }
        catch (Exception ex)
        {
            Console.WriteLine("会话恢复失败（那份暂存已丢弃）：" + ex.Message);
        }
    }

    /// <summary>
    /// 启动时接上上次的板书（自动存档）。
    /// **读走不删**：它就是这份板书本身，下次打开还要接着用。
    /// 读坏了只提示、从空白开始——自动存档不该成为"起不来"的理由。
    /// </summary>
    private void RestoreAutoSaveIfAny()
    {
        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;

        // **默认不接上一次的板书**（用户 2026-09-17："退出以后再打开不用恢复墨迹吧……
        // 我觉得默认不恢复墨迹"）。
        //
        // 为什么这个默认是对的：教室的机器是**公用**的，上一节课（甚至上一个班）的板书
        // 一开机又铺满整个屏幕，老师第一件事就得先清空；而且"打开就有别人的东西"
        // 本身就不合适。课与课之间要的是一块干净的白板。
        //
        // 注意和图省事的临时暂存（<see cref="RestoreSessionIfAny"/>）分开：那条只在
        // **软件自己主动重启**（界面上点"重启"、界面出问题自动重建）时才走，
        // 那是"重启不该丢东西"，和"下次打开要不要接上"是两件事，仍然保留。
        //
        // 开关放在界面偏好里（`ui.restoreInk = "1"`），以后在"更多"抽屉里给一行就能改；
        // 现在没有这一项 = 不恢复。
        if (!RestoreInkOnStartup)
        {
            Console.WriteLine("启动：不接上次的板书（默认；要接上就在设置里打开 restoreInk）");
            return;
        }

        var blob = Recovery.LoadAuto();
        if (blob == null) return;

        try
        {
            // 自动存档 = **白板槽**（2026-10-16）：装回白板、直接进白板给它看
            //（以前是"打开就铺满屏幕"；接上的东西从"桌面板书"换成了"白板"——同一份数据，
            //  语义从"恢复桌面批注"改成"恢复上课的板书"）。
            var strokes = InkSerializer.LoadStrokes(blob, out int maxId);
            Doc.LoadPageContent(InkDocument.BoardPageKey, strokes, maxId);
            _boardReturnKey = 0;
            Doc.SwitchPage(InkDocument.BoardPageKey);
            BoardOn = true;
            _autoSavedBoardFp = BoardFingerprint(Doc.StrokesOf(InkDocument.BoardPageKey));
            Doc.InvalidateAll();
            Console.WriteLine($"已接上上次的板书（白板）：{strokes.Count} 笔");
        }
        catch (Exception ex)
        {
            Console.WriteLine("自动存档读不出来（已忽略，从空白开始）：" + ex.Message);
        }
    }

    /// <summary>
    /// 开机自启的“不展开旧板书，但别把旧板书覆盖掉”处理。
    ///
    /// 正常启动时 <see cref="RestoreAutoSaveIfAny"/> 会把旧板书读进文档；自启刻意
    /// 不读。可是自动存档每 15 秒会看指纹，如果这里不先把指纹对齐，空文档会
    /// 被判成“变了”，转头把用户上一节课的板书覆盖成空。
    /// </summary>
    private void PrimeAutoSaveForStartup()
    {
        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;

        // 自启不把旧板书读进文档，但**也不能让空文档把旧自动存档写掉**：
        // 当前文档就是空板，指纹按 0 对齐。15 秒后的自动存档和退出时的兜底
        // 都会认为“没变”，旧文件原样保留；用户一旦真写了/打开了内容，
        // 指纹变化，才走正常保存。
        _autoSavedBoardFp = 0;
        if (RestoreInkOnStartup)
            Console.WriteLine("开机自启：不展开上次板书；旧自动存档保留到用户开始写");
    }

    /// <summary>
    /// 窗口建好、快捷键注册完之后的分支。返回 -1 表示"没有特殊模式，
    /// 正常跑消息循环"；返回 &gt;= 0 表示直接以该值退出。
    /// 产品界面只会拿到 -1；测试模式由 InkTeach 覆写。
    /// </summary>
    protected virtual int RunModeDispatch(string mode, string[] args) => -1;

    /// <summary>
    /// `--geomtrace`（2026-10-06 排查"快速画圆变折线"时加）。
    ///
    /// 打开后，每一笔的几何**走哪条路**会打一行：通道（A 等宽描边 / B 压感墨迹）+
    /// 数据源（模型输出 / 原始采样点）+ 曲线还是折线，外加点数、平均点距、压感有无、
    /// 活笔标志、曲线开关。排查"到底哪条岔路没接上曲线"靠读代码猜不够——
    /// 这个开关让程序自己报（前两轮就是靠读代码猜，两次都猜错了）。
    /// </summary>
    internal static bool GeomTraceOn;

    /// <summary>
    /// `--rawprobe`（2026-10-07）：注册原始输入、数"设备到底报了多少条"。
    ///
    /// 用户的疑问是"手写板标称 300Hz，为什么关掉 ink 只有 65Hz"。
    /// 硬件上报率 ≠ 能到应用手里的条数——中间隔着 Windows 的输入管线
    /// （鼠标消息会被合并）。**Raw Input（WM_INPUT）是绕过合并的官方通道**，
    /// 它给出的数才回答得了"是我们收得少，还是系统本来就没给"。
    ///
    /// 只数数，不参与落笔（产品路径一个字不改）。
    /// </summary>
    internal static bool RawProbeEnabled;

    /// <summary>原始输入注册是否成功（诊断）。</summary>
    internal static bool RawProbeOk;

    /// <summary>本笔期间收到的原始输入条数。</summary>
    private int _rawReportsThisStroke;

    /// <summary>
    /// `--rawinput`：把原始输入（Raw Input）**补充**成这一笔的采样点。
    ///
    /// 为什么需要（用户 2026-10-07 真机实测）：
    ///   关掉 Windows Ink 时手写板走鼠标通道，Windows 的鼠标消息管线**会合并**，
    ///   而且合并掉的点在 `GetPointerInfoHistory` 里**拿不回来**（实测 1:1）。
    ///   同一台机器同一笔：指针消息只给 60~79 Hz，而**原始输入有 154~194 Hz** ——
    ///   也就是说六成的输入被白扔了。
    ///
    /// 设计（**只增不改**，产品路径一个字不动）：
    ///   · 指针消息仍是**唯一的主路径**：路由 / 命中 / 穿透 / 多窗口全归它；
    ///   · 原始输入只在**正在写一笔**时，往这一笔上**追加采样点**；
    ///   · 鼠标原始报是**相对位移**，所以用"**指针消息当绝对锚点 + raw 报累加填缝**"：
    ///     每个指针消息把锚点设成它的绝对位置、累计清零 → **误差被夹在一条消息之内，不漂**；
    ///   · 零位移的报要丢掉（原始输入在不动时也按轮询率发空报）。
    /// </summary>
    internal static bool RawInputCapture;

    /// <summary>
    /// raw 补点的**最小距离**（画布像素）。低于它的报直接丢掉。
    ///
    /// 为什么必须有：鼠标的原始报可以到 1000Hz（游戏鼠标），快速移动时相邻报只差不到
    /// 1 像素——**往笔迹里塞零长段**。而几何那边明确讲过零长段会多出一个退化段
    /// （见 `BuildCenterlineCore` 里"再补一次就给几何多出一个零长段"那段注释）。
    /// 3px 这个数远细于观感所需（12px 笔宽下，点距 4px 时弦高误差只剩 0.04px），
    /// 同时把"每秒最多塞多少个点"限制在 速度/3 以内。
    /// </summary>
    internal static float RawMinDistPx = 3f;

    /// <summary>raw 补点的**每笔上限**（第二道保险，防病态设备）。</summary>
    internal static int RawMaxPerStroke = 3000;

    /// <summary>`--rawinput` 期间从原始输入补进来的点数（诊断）。</summary>
    private int _rawPointsAdded;

    // ---- 原始输入"被哪道门挡住的"分项计数（诊断，2026-10-07 加）------------
    // 起因：用户真机实测 `[原始输入 42 条 → 160 Hz]` 但 `[raw 补点 +0]` ——
    // 抓得到却一条没补。光看总数定位不了是哪道门挡的，所以每个 return 前记一笔。
    private int _rawSkipNoStroke, _rawSkipApi, _rawSkipType, _rawSkipAbs;
    private int _rawSkipZero, _rawSkipKind, _rawSkipGate, _rawSkipAnchor;
    private int _rawSkipCap, _rawSkipNear, _rawSkipNoInter;

    /// <summary>主屏像素尺寸（绝对原始报的归一化基准）。</summary>
    private float _primaryW = 1920f, _primaryH = 1080f;


    /// <summary>上一条绝对原始报的原始值（取差值用，见 HandleRawInput）。</summary>
    private int _rawAbsX, _rawAbsY;
    private bool _rawAbsValid;

    // ---- "到底有没有中间点可捞"的自适应判据（2026-10-07 加）------------------
    // 为什么需要：**1:1 的设备上原始报和指针消息是同一批位置**（本机合成输入实测
    // 61 报 / 61 消息），补进去的全是**重复点**（零长段）——纯噪声，不是信息。
    // 只有像用户手写板那样"指针消息被系统合并"（实测 13 条消息 / 42 条原始报 = 2.3:1）
    // 才有中间点可捞。
    // 所以：**只有当原始报条数明显多于指针消息条数时才补点**。
    // 好处是 1:1 的设备上这个功能等于不存在（零风险），有合并的设备上自动生效。
    private int _rawSinceLastPtr;      // 自上一条指针消息以来收到的原始报数
    private int _rawTotalSincePtr;     // 累计的原始报数
    private int _ptrMsgsThisStroke;    // 累计的指针消息数
    private bool _rawHasIntermediates; // 判定：有中间点可捞

    /// <summary>最近一次指针消息给的**绝对屏幕坐标**（raw 填缝的锚点）。</summary>
    private float _rawAnchorX, _rawAnchorY;
    /// <summary>自锚点以来 raw 报累计的位移。</summary>
    private float _rawAccumX, _rawAccumY;
    /// <summary>锚点是否有效（没锚点就不敢用 raw 填缝）。</summary>
    private bool _rawAnchorValid;

    /// <summary>
    /// 收尾时给进程的退出码。默认 0；自检发现有 FAIL 时置 1，
    /// 这样脚本/CI 不用去解析控制台文字也能判红绿（和 tools/ui-mock 的自检一个规矩）。
    /// 产品路径永远是 0。
    /// </summary>
    protected int ExitCode;

    private bool RegisterWindowClass()
    {
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
            // 兜底光标。**这一句是"永远转圈"的解药**：hCursor 留空（NULL）时
            // 系统会给一个默认的等待类光标，用户看到的就是一直转圈。
            // 具体形状由 WM_SETCURSOR 按状态覆盖（见 ApplyCursor）。
            hCursor = Cursors.System(Native.IDC_ARROW),
        };
        ushort atom = Native.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private bool CreateOverlays()
    {
        bool ok = true;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var w = new OverlayWindow();
            var err = w.Create(_hInstance, _className, r.Left, r.Top, r.Width, r.Height);
            if (err != null)
            {
                Console.WriteLine($"overlay create failed on monitor {hMon}: {err}");
                ok = false;
                return false;
            }
            s_map[w.Hwnd] = w;
            _windows.Add(w);
            Console.WriteLine($"overlay on monitor {hMon}: {r.Width}x{r.Height} at ({r.Left},{r.Top}) dpi={w.Dpi}");
            Console.WriteLine($"湿墨档位: {WetMode} {WetModeName(WetMode)}"
                              + "（设置里记着；Ctrl+Alt+Shift+W 循环切换，--wetmode 0..1 指定）");
            Console.WriteLine($"委托墨迹轨迹(InkTrail): {(OverlayWindow.InkTrailEnabled ? "开" : "关")}"
                              + $"（接口{OverlayWindow.InkTrailNote}）");
            Console.WriteLine($"湿墨呈现: {(OverlayWindow.SystemWetPreferred ? "系统轨迹独占（--syswet）" : "自己画（系统轨迹照喂）")}");
            // 调参时"我到底调上了没有"必须一眼看得见：这里印的是**生效值**，不是"可用/不可用"。
            // （2026-09-22 用户碰到的两个坑：--predictms 100 被静默夹到 15；--noinktrail 生效了没有
            //   只能靠猜。这两件事都不该靠猜。）
            // 2026-10-15：只剩线性预测（Chromium 同族）——算法行不再需要"档"的口径。
            // 2026-10-16：地平线**按刷新率缩放**（用户定：60Hz=10ms，其余按比例 600/Hz、收在 4~10ms）
            // ——取第一块屏（和显示层限速的帧预算同一个口径），放在下面这一行之前算好。
            if (_windows.Count == 1 && w.RefreshPeriodMs > 1.0)
                _predictor.HorizonMs = Math.Clamp(0.6 * w.RefreshPeriodMs, 4.0, 10.0);
            Console.WriteLine(PredictTailEnabled
                ? $"笔迹预测: 开（自绘尾；线性预测（Chromium 同族）；地平线 {_predictor.HorizonMs:F1} ms"
                  + $"（按 {(w.RefreshPeriodMs > 1.0 ? 1000.0 / w.RefreshPeriodMs : 60):F0}Hz 缩放）；"
                  + "设置「墨迹预测」可关 / --nopredict2）"
                : "笔迹预测: 关（设置 → 书写 → 墨迹预测；--predict2 可再开）");
            // 书写期间的 GC 低延迟档：低配上"偶发卡一下"的第一嫌疑就是它没生效。
            // 这里印的是**读回来的实际状态**（见 GcLatency.Describe），不是"我们想让它开"。
            Console.WriteLine($"书写期间 GC 低延迟档: {GcLatency.Describe()}"
                              + $"（SustainedLowLatency，最后一笔后 {GcLatency.HoldMs / 1000:F0} 秒退回）");

            // 系统笔设置：**"长按当右键"最容易在板书时添乱**——笔尖停住不动会被判成长按，
            // 而板书时停顿是常态。这里只**如实打印读到的数值**，不解释哪个取值代表开还是关
            //（那套取值我们没有可靠出处，不能猜；本项目的规矩是不许把猜的写成结论）。
            // 想改就去：设置 → 蓝牙和设备 → 笔和 Windows Ink → 其他笔设置。
            {
                const string penKey = @"Software\Microsoft\Wisp\Pen\SysEventParameters";
                bool hasMode = Native.ReadDword(penKey, "HoldMode", out uint hm, out _);
                bool hasWait = Native.ReadDword(penKey, "WaitTime", out uint wt, out _);
                bool hasHold = Native.ReadDword(penKey, "HoldTime", out uint ht, out _);
                if (hasMode || hasWait || hasHold)
                {
                    var parts = new List<string>();
                    if (hasMode) parts.Add($"HoldMode={hm}");
                    if (hasWait) parts.Add($"长按判定等待 {wt} ms");
                    if (hasHold) parts.Add($"长按保持 {ht} ms");
                    Console.WriteLine("系统笔设置: " + string.Join("、", parts)
                                      + "（这一份是**系统全局**的设置，只列出来看清楚——"
                                      + "我们自己的窗口已经单独关掉了这条手势，见下一行）");
                }
                else
                {
                    Console.WriteLine(@"系统笔设置: 读不到 HKCU\Software\Microsoft\Wisp\Pen\SysEventParameters"
                                      + "（这台机器没有这套笔设置；真笔设备可能由驱动自己管）");
                }
            }
            // **这条是"无论系统怎么设，我们这边都不认它"的验收口**（用户 2026-09-23 要的）：
            // 三条 Window 级关法见 Native.DisableSystemPressAndHold。为什么必须印出来：
            // 它失效时的现象是"笔尖停住弹右键环"，看起来像"停顿成型没识别出来"，
            // 排查方向会从一开始就错。
            Console.WriteLine("长按=右键手势: " + (OverlayWindow.PressAndHoldDisabled
                ? "已在本窗口内关掉（系统里开着也不影响我们）"
                : "⚠ 没关掉（真笔上笔尖停住可能弹右键环，会和停顿成型抢）"));
            Console.WriteLine($"压感→粗细: {(PressureWidth.Enabled
                ? $"开（{PressureWidth.Min:F2}~{PressureWidth.Max:F2} 倍，曲线 gamma {PressureWidth.Gamma:F2}）"
                : "关（--nopressure）")}；变宽通道: {OverlayWindow.InkNote}");
            // [停用 2026-10-05] 模拟压力 / 笔锋 / 收尖：全部停用（代码与备份见 已停用-渲染实验.md）。
            Console.WriteLine("无压感笔迹增强: 已停用（模拟压力 / 笔锋 / 收尖）");
            // **输入路径总览**（2026-10-07 用户要求）：一次说清这台机器上会走哪几条路。
            // 每条笔的 `[笔画]` 行也会用大白话报"这一笔实际走了哪条"（见 DescribeInputPath）。
            Console.WriteLine("输入路径: 开 ink → Windows Ink 笔（压力 1024 级）"
                              + "；关 ink → raw 补点（等宽，无压力）");
            return true;
        }, IntPtr.Zero);

        if (_windows.Count == 0)
        {
            Console.WriteLine("no monitors found");
            return false;
        }

        // Refresh the HUD ~4x a second so the numbers move even when idle.
        //
        // 顺便每隔 250 ms 重新"置顶"一次（WM_TIMER 里调 ReassertTopmost）。
        // 这个必须够快：**别的置顶窗口会把我们盖住**——实测输入法状态条
        // （wetype 的 StatusBarWnd）、桌面小组件（RocketView）都是 TOPMOST，
        // 谁最后调 SetWindowPos 谁在上面。被盖住的后果不只是"看不见"：
        // 合成/真实点击都会落到那个窗口上，批注一个字都画不出来。
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)1, 250, IntPtr.Zero);
        DpiScale = _windows[0].Dpi / 96f;
        PressureSim.DpiScale = DpiScale;    // 模拟压力按 72dpi 口径换算距离（见 PressureSim）
        PressureSim.BumpVersion();

        Host?.UpdateScreen(UiLogicalScreen);
        Host?.UpdateWorkArea(UiLogicalWorkArea);
        Console.WriteLine($"DPI 缩放 {DpiScale:F2}（逻辑 {_windows[0].Width / DpiScale:F0}x{_windows[0].Height / DpiScale:F0}），"
                          + $"界面缩放 {UiScale:F2}（{UiScalePresets.Name(UiScale)}）");

        // 环境摘要进日志（2026-10-17）：同一版本一台 2ms/帧、一台 14ms/帧，先看这行。
        int envHz = _windows[0].RefreshPeriodMs > 1.0
            ? (int)Math.Round(1000.0 / _windows[0].RefreshPeriodMs) : 60;
        DiagLog.SetEnv(Gfx.AdapterName, Gfx.IsSoftware, Gfx.AdapterVramMb,
                       envHz, _windows[0].Dpi, UiScale, $"{_virtualW}x{_virtualH}");
        if (Gfx.IsSoftware)
            Console.WriteLine("[env] ⚠ 没有可用的硬件显卡，正在用 WARP 软件渲染（能用，但会明显慢；4K 尤其）");
        if (Gfx.HardwareError.Length > 0)
        {
            if (Gfx.HardwareError.StartsWith("强制", StringComparison.Ordinal))
                Console.WriteLine("[env] " + Gfx.HardwareError);
            else
                Console.WriteLine("[env] 硬件 D3D 失败原因：" + Gfx.HardwareError);
        }

        // 把键盘模式落到窗口样式上。字段默认是开的，但样式要等窗口建好才能改——
        // 不调这一句，默认值和实际样式就对不上（得按一次 Ctrl+Alt+K 才生效）。
        SetKeyboardMode(KeyboardMode);
        return ok;
    }

    private void RegisterHotkeys()
    {
        // 全局热键**不再是一串写死的 (id, key)**：动作与键位由 Keys 这张表决定，
        // id 只是"注册顺序"，HandleHotkey 拿它反查动作。这样键位可配置、可查冲突，
        // 而引擎里不认识"Ctrl+Alt+P"这种东西。
        IntPtr h = _windows[0].Hwnd;
        _hotkeyActions.Clear();
        Keys.GlobalFailures.Clear();

        foreach (var b in Keys.For(KeyScope.Global))
        {
            int id = _hotkeyActions.Count + 1;
            uint mod = Native.MOD_NOREPEAT;
            if ((b.Chord.Modifiers & KeyChord.ModCtrl) != 0) mod |= Native.MOD_CONTROL;
            if ((b.Chord.Modifiers & KeyChord.ModAlt) != 0) mod |= Native.MOD_ALT;
            if ((b.Chord.Modifiers & KeyChord.ModShift) != 0) mod |= Native.MOD_SHIFT;

            _hotkeyActions.Add(b.Action);
            if (!Native.RegisterHotKey(h, id, mod, b.Chord.Vk))
            {
                // **注册失败要留痕**：教室机上微信 / QQ / 输入法 / 教学软件都会抢键
                // （实测 Ctrl+Alt+W 就被微信截图占了）。以前这里只打一行控制台，
                // 用户根本不知道，只觉得"这个键没反应"。
                int err = Marshal.GetLastWin32Error();
                string msg = $"{b.Chord}（{KeyMap.Describe(b.Action)}）注册失败，错误 {err}"
                           + (err == 1409 ? "：已被别的程序占用" : "");
                Keys.GlobalFailures.Add(msg);
                Console.WriteLine("hotkey " + msg);
            }
            else _hotkeysRegistered++;
        }
        Console.WriteLine($"registered {_hotkeysRegistered}/{Keys.Bindings.Count(b => b.Scope == KeyScope.Global)} global hotkeys");
    }

    // ---- 放映期间的"临时全局热键"（2026-09-30 用户定方案①）-----------------
    //
    // 背景：放映时**前台是 WPS/PPT**，我们的覆盖层收不到键盘（键盘按"焦点"投递；
    // 鼠标才是按"位置"，所以滚轮/点击在放映中照样有效）。而我们的工具键是"应用内
    // 快捷键"，靠焦点 → 放映时全失效 ✗。
    //
    // 办法：**一进放映批注模式，就把这几个键临时注册成全局热键**——系统直接投给我们、
    // 不看焦点；而且键被我们吞掉、**不会传给 WPS**，所以不会有"我们切了工具、PPT 又翻
    // 一页"的双发副作用（隔壁 InkClass 那种"打架"就是它用全局钩子但不吞键造成的）。
    // 退出放映立刻注销：平时一个键都不多占。
    //
    // **例外：穿透开着时整体让路**（用户 2026-09-30 定："穿透模式下，PPT 的键起作用、
    // 我们的键不起作用"）——穿透 = "这一段键盘归下层程序"，所以这几个键临时注销，
    // PPT/WPS 自己的 Ctrl+P/E/L/Z、←→ 等恢复可用；退出穿透时若还在放映，立刻收回
    // （统一走 SyncPptHotkeys，见 SetPassThrough）。
    private const int PptHotkeyBase = 81;          // 一小段专用 id（常规热键是 1..N，别撞）
    private static readonly (uint Mod, uint Vk, KeyAction Act)[] PptHotkeys =
    {
        (Native.MOD_CONTROL, 0x50 /*P*/, KeyAction.ToolPen),
        (Native.MOD_CONTROL, 0x49 /*I*/, KeyAction.ToolHighlighter),
        (Native.MOD_CONTROL, 0x4C /*L*/, KeyAction.ToolLaser),
        (Native.MOD_CONTROL, 0x45 /*E*/, KeyAction.ToolEraser),
        (Native.MOD_CONTROL, 0x5A /*Z*/, KeyAction.Undo),
        // ⚠ 2026-10-04：呼出盘**不再进这张表**——它已升为常驻全局键 `Ctrl+Alt+Shift+Q`
        // （见 KeyBindings.Default），放映时前台是 PPT/WPS 也照样生效；临时表里再挂一遍
        // 就等于"同一个动作两把全局键"，正是这次改键要消掉的东西。
        // 放映时方向键也归我们：**有选中 → 微调；没选中 → ←→ 代 WPS 翻页**
        // （键盘在我们手里，不拦的话 WPS 收不到 ←→，什么都不发生——用户 2026-09-30 实测）。
        (0u, 0x25 /*←*/, KeyAction.PptPrev),
        (0u, 0x27 /*→*/, KeyAction.PptNext),
        (0u, 0x26 /*↑*/, KeyAction.PanUp),
        (0u, 0x28 /*↓*/, KeyAction.PanDown),
    };
    private bool _pptHotkeysOn;

    /// <summary>现在该不该挂放映临时全局键：放映中 **且不穿透**。
    /// 穿透 = "这一段键盘归下层程序"（用户 2026-09-30 定："穿透模式下，PPT 的键起作用、
    /// 我们的键不起作用"），所以穿透期间让路，退出穿透立刻收回。</summary>
    private bool PptHotkeysWanted => PptMode && !PassThrough;

    /// <summary>按当前状态挂/摘放映临时全局键。进/退放映（Ppt.cs）与开/关穿透
    /// （SetPassThrough）都调它；`RegisterPptHotkeys` 幂等，重复调不做事。</summary>
    private void SyncPptHotkeys() => RegisterPptHotkeys(PptHotkeysWanted);

    /// <summary>挂/摘那 8 个放映临时全局键（真正碰系统的那一层）。</summary>
    private void RegisterPptHotkeys(bool on)
    {
        if (_pptHotkeysOn == on || _windows.Count == 0) return;
        IntPtr h = _windows[0].Hwnd;
        for (int i = 0; i < PptHotkeys.Length; i++)
        {
            int id = PptHotkeyBase + i;
            var (mod, vk, _) = PptHotkeys[i];
            if (on)
            {
                if (!Native.RegisterHotKey(h, id, mod | Native.MOD_NOREPEAT, vk))
                    Console.WriteLine($"hotkey 放映临时键 #{i} 注册失败，错误 {Marshal.GetLastWin32Error()}");
            }
            else Native.UnregisterHotKey(h, id);
        }
        _pptHotkeysOn = on;
        Console.WriteLine(on ? "放映批注模式：工具键（Ctrl+P/I/L/E/Z）与方向键已临时升级为全局热键"
                            : "放映临时全局热键已注销（退出放映或开着穿透）");
    }

    /// <summary>自检用：放映临时全局键现在挂着没有（穿透期间会让给下层）。</summary>
    internal bool PptHotkeysOnForTest => _pptHotkeysOn;

    /// <summary>注册顺序 → 动作。按这个顺序 RegisterHotKey，WM_HOTKEY 的 id 就是它。</summary>
    private readonly List<KeyAction> _hotkeyActions = new();

    internal KeyAction ActionForHotkeyId(int id)
        => id >= 1 && id <= _hotkeyActions.Count ? _hotkeyActions[id - 1]
         : id >= PptHotkeyBase && id < PptHotkeyBase + PptHotkeys.Length ? PptHotkeys[id - PptHotkeyBase].Act
         : KeyAction.None;

    /// <summary>自检用：这批全局热键实际注册成功了几个。</summary>
    internal long HotkeysRegistered => _hotkeysRegistered;

    // =====================================================================
    //  Message loop
    // =====================================================================

    internal void Loop()
    {
        // 主循环线程 id：后台线程（文档页渲染 / PPT 轮询）用它 PostThreadMessage 叫醒我们。
        _mainThreadId = Native.GetCurrentThreadId();

        // 旧版换壳脚本只按“当前目录名.old-*”清备份；如果用户从旧备份启动过，
        // 目录会变成 X.old-A.old-B.old-C，老脚本就再也匹配不到。新版本启动后
        // 自己清一遍；等 5 秒确认这次启动不是“刚起来就崩”，再全删，后台做。
        if (!_oldBackupCleanupStarted)
        {
            _oldBackupCleanupStarted = true;
            Task.Run(() =>
            {
                Thread.Sleep(5000);
                UpdateFeed.CleanupOldBackups();
            });
        }

        // **自检模式那行诊断**（2026-10-07）：打在这里，而不是构造函数里。
        //
        // 构造函数跑在 `RunModeDispatch` **之前**，那时还不知道 `args[0]` 到底算不算 mode ——
        // 提前打会把 `--clean 3` 这种"开关写在第一个位置"误报成自检模式（我今天就被自己的
        // 日志骗过一轮）。而 `Loop()` 是 `RunModeDispatch` 判完之后所有模式必经的第一站：
        // 兜底分支已经把这个标记清干净了，所以这里打出来的一定是对的。
        //
        // `[mode]` 是**给脚本认的 ASCII 标记**：中文那串重定向到文件时会被编码搞乱，
        // 靠字形判"是不是自检模式"判不准。
        if (SelfCheckMode && !_selfCheckLogged)
        {
            _selfCheckLogged = true;
            Console.WriteLine($"  [mode] 自检：{_modeName}（设置/存档/PPT 缓存都走临时路径）");
        }
        // 命令行要求的湿墨档位，**到这里才写盘**：此刻 `RunModeDispatch` 刚判完
        // （兜底分支会把开关的 SelfCheckMode 清掉），敲一次是真的记得住。
        if (_pendingWetModePref.Length > 0 && !SelfCheckMode)
        {
            try
            {
                SetUiPref(WetModePrefKey, _pendingWetModePref);
                FlushSettings();
                Console.WriteLine($"  [湿墨] 已记住档位 {_pendingWetModePref}：{WetModeName(WetMode)}"
                                  + "（下次启动照这个档跑；Ctrl+Alt+Shift+W 可随时切）");
            }
            catch (Exception ex) { Console.WriteLine($"  [湿墨] 记档位失败（不影响使用）：{ex.Message}"); }
            _pendingWetModePref = "";
        }

        while (!_quit)
        {
            DrainMessages();
            DrainUiTasks();               // 后台算完的美化像素，回 UI 线程换图
            PumpUpdate();                 // 自动更新：把后台结果搬过来，该换壳就换壳
            if (_quit) break;

            // 原始输入的**注销兜底**：只要没在写一笔就注销掉。
            // 放在主循环里而不是逐个抬笔分支上——抬笔路径有十几条（正常抬手、丢捕获、
            // 截图、图形工具、多笔式…），漏一条就会退回"空闲也收鼠标空报"，
            // 表现就是空闲 46fps 空转、单核 26%（实测）。
            if (_rawRegistered && ActiveStroke == null) SetRawCapture(false);

            NowMs = _clock.Elapsed.TotalMilliseconds;
            MaybeAutoCheckUpdate();       // 静默查更新：气泡 / 更多上的红点靠它出现
            PumpKeyGestures();            // 工具键的手势：长按判定 + 连按换色的延迟结算
            PumpRadialPalette();          // 呼出盘：出盘延迟 / 松手轮询 / 超时
            StepMicroAdjust();            // 连续微调会话：停手 400ms 就"封口"（下一次另起一条撤销）

            if (NowMs >= _autoExitAt) break;

            // 跑满一分钟还没出事 → 把"因为界面出问题而重启"的计数清掉。
            // 不这么做的话，上午两次、下午两次，第四次就会莫名其妙不许重启。
            if (!_restartCountCleared && NowMs > 60_000)
            {
                Recovery.ClearRestartCount();
                _restartCountCleared = true;
            }

            Laser.Prune(NowMs);
            StepCameraAnim();                 // 翻页动画（167ms）
            StepPpt();                        // PPT 放映联动（没变化时只读一个 bool，不碰 COM）
            StepDocPageReady();               // 文档页后台渲染完一页：置脏（这一拍会采纳它）
            StepDocAutoSave();                // 文档批注的节流保存（自动保存开着才写）
            StepDocResume();                  // 文档阅读位置的节流保存（跨会话"继续"用）
            StepPptBar();                     // 底部那条：引导过期 / "再点确认"过期（没有长按了）
            StepToast();                      // 轻提示到点自动消失（2s，不吞输入）
            StepSelectionMenu();              // 选中被清空时弹层跟着关
            StepEngineTooltip();              // 引擎侧悬停提示的 500ms 延迟（到点点亮）
            StepTimerCard();                  // 课堂计时卡片：推进秒数 / 到点 / 同步接输入小窗
            StepRollCard();                   // 课堂点名卡片：滚动推进 / 同步接输入小窗
            // 设备卡（2026-10-17）：和 StepPpt 一样**两处都调**——Loop 这条负责"空闲也在转"，
            // RenderAll 那条负责自检驱动；函数自己幂等（每天最多传一次）。
            MaybeDeviceCardUpload();
            StepLogUpload();                  // 日志上传结果轮询（后台线程传，界面不卡）
            StepHandwritingOcr(); StepAiPlot();             // 手写优化：把后台识别结果搬回文档
            StepImageCard(); StepCardAnswers();             // 图片→题卡 / 卡上生成答案
            StepAppDataJob();                 // 全局备份/恢复结果轮询（后台线程，界面不卡）
            if (NeedsFrame())
            {
                // VBlankPaced：先等到合成边界，**再抽一次消息**，然后画、提交。
                //
                // 顺序是有讲究的。等到边界之后抽，把"等边界期间到达的输入"
                // 也算进这一帧，笔尖就是最新的；反过来（先抽、再等一个周期）
                // 等于让最新的那一笔在队列里多躺一个刷新周期。
                if (OverlayWindow.VBlankPaced)
                {
                    Native.DwmFlush();
                    DrainMessages();
                    _dirty = true;
                }
                UpdatePredictTail();   // 预测尾（每帧重建；默认关（2026-10-16 起）；`--predict2` 可开）
                // 渲染期间界面可能又提出"我还要一帧"（在 Render 里调 InvalidateUi）。
                // 用序号认出来，别让这一句 _dirty = false 把它抹掉——
                // 抹掉的表现就是"动画或一次性外观变化卡在第一帧"。
                long seqBefore = _uiInvalidateSeq;
                long docVerBefore = Doc.Version;
                RenderAll();
                // 渲染期间**改了文档**（"按住清空"就是在界面的 Render 里够时间的）
                // 也要再要一帧：这一帧贴出去的是改之前的像素。
                // 少了后面这半句，清空之后屏幕上那层墨会一直留着——见 RenderAll 里的注释。
                _dirty = _uiInvalidateSeq != seqBefore || Doc.Version != docVerBefore;

                // 空闲预取：正常帧走完，如果"没在写、没在取景"且视口外一圈还有欠着的块，
                // 排几拍只烘块、不合成不上屏的空闲拍（上游 Xournal++/Rnote 的预渲染思路）。
                if (!_quit && !CaptureActive && ActiveStroke == null && AnyPrefetchNeeded())
                    _prefetchStepsLeft = Math.Max(_prefetchStepsLeft, 64);
            }
            else if (_prefetchStepsLeft > 0)
            {
                // 空闲预取一拍：只烘"视口外一圈"里欠着的块，不合成、不上屏。
                // 一拍之后等最多 4ms（有输入立刻醒）：既不打满空闲 CPU，也不拖输入。
                _prefetchStepsLeft--;
                bool more = false;
                foreach (var w in _windows) more |= w.PrefetchStep(this);
                if (!more) _prefetchStepsLeft = 0;
                Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 4, Native.QS_ALLINPUT, 0);
            }
            else
            {
                Native.WaitMessage();
            }
        }
    }

    /// <summary>空闲预取的剩余拍数（见主循环；每拍 <see cref="CanvasTileCache.PrefetchPerFrame"/> 块）。</summary>
    private int _prefetchStepsLeft;

    /// <summary>视口外一圈还有没有欠着的块（任一窗口）。</summary>
    private bool AnyPrefetchNeeded()
    {
        if (!CanvasTileCache.PrefetchEnabled) return false;
        foreach (var w in _windows)
            if (w.PrefetchNeeded()) return true;
        return false;
    }

    // ======================================================================
    //  文档高清补丁：每帧同步一次（由引擎挑驱动窗口）
    // ======================================================================

    /// <summary>
    /// 每帧一次的高清补丁同步。**排在相机推给各窗口之后、`RenderFrame` 之前**：
    /// 那时每个窗口的 `VisibleCanvasRect` 才是这一帧真正要画的那块。
    /// 关着文档 / 页让位（穿透、不透明白板）时不动——页让位的收尾由窗口那拍的
    /// <see cref="DocPatchCache.ReleaseAll"/> 负责（补丁也一起收）。
    /// </summary>
    private void SyncDocPatch()
    {
        var patch = DocPatch;
        if (patch == null || !DocView.IsOpen) return;
        if (PassThrough || (BoardOn && BoardOpacity >= 0.999f)) return;

        int n = _windows.Count;
        if (n == 0) return;

        // 两个小数组（每帧 ≤ 8 个窗口，走栈）——不为此加字段，也不每帧分配 List。
        Span<RectF> canvases = n <= 8 ? stackalloc RectF[n] : new RectF[n];
        Span<RectF> screens = n <= 8 ? stackalloc RectF[n] : new RectF[n];
        for (int i = 0; i < n; i++)
        {
            var w = _windows[i];
            canvases[i] = w.VisibleCanvasRect;
            screens[i] = new RectF
            {
                MinX = w.OriginX, MinY = w.OriginY,
                MaxX = w.OriginX + w.Width, MaxY = w.OriginY + w.Height,
            };
        }

        int driver = ChooseDocPatchDriver(canvases, screens);
        if (driver < 0) return;

        // 冻结页（半透明白板）只画 1× 底图 → 整层不建补丁。
        patch.Sync(canvases[driver], NowMs, BoardFrozenDocActive ? BoardFrozenDocPage : -1,
                   busy: _drawing || _textBoxEditing || _camAnimating);
    }

    /// <summary>
    /// 这一帧由**哪个窗口**驱动高清补丁（-1 = 没有候选）。
    ///
    /// 为什么不是"枚举到的第一个窗口"（2026-11-16 评审 P0-2）：`_windows` 的顺序来自
    /// EnumDisplayMonitors，投影仪 / 副屏很可能排在第一位。它要是不压在页上
    /// （页锚在**主屏中心**），按它算出来的可见矩形根本碰不到页 → 补丁永远排不出来，
    /// 主屏就是一整片 1× 拉伸的糊图。
    ///
    /// 判据 = **与可见页重叠面积最大**；面积相同（两块屏看到的是同一页）时取
    /// **包含主屏中心**的那一块——页锚在那里，那块才是"老师正看着的屏"。
    /// 纯几何、不碰窗口对象，所以 `--zoomtest` 能伪造一块副屏来验它。
    /// </summary>
    internal int ChooseDocPatchDriver(ReadOnlySpan<RectF> canvasRects, ReadOnlySpan<RectF> screenRects)
    {
        int count = Math.Min(canvasRects.Length, screenRects.Length);
        if (count == 0) return -1;

        float px = (_primaryW > 0 ? _primaryW : _virtualW) * 0.5f;
        float py = (_primaryH > 0 ? _primaryH : _virtualH) * 0.5f;

        int best = -1;
        float bestArea = -1f;
        bool bestPrimary = false;
        for (int i = 0; i < count; i++)
        {
            float area = DocPagesOverlapArea(canvasRects[i]);
            var s = screenRects[i];
            bool primary = s.MinX <= px && px < s.MaxX && s.MinY <= py && py < s.MaxY;
            bool better = area > bestArea + 0.5f
                          || (MathF.Abs(area - bestArea) <= 0.5f && primary && !bestPrimary);
            if (!better) continue;
            best = i;
            bestArea = area;
            bestPrimary = primary;
        }
        return best;
    }

    /// <summary>这块画布矩形压住了多少"打开着的页"（画布单位面积；0 = 完全没压在页上）。</summary>
    private float DocPagesOverlapArea(in RectF canvas)
    {
        float sum = 0f;
        int n = DocView.Count;
        for (int i = 0; i < n; i++)
        {
            var page = DocView.At(i)?.Rect;
            if (page is not { } pr) continue;
            float w = MathF.Min(canvas.MaxX, pr.MaxX) - MathF.Max(canvas.MinX, pr.MinX);
            float h = MathF.Min(canvas.MaxY, pr.MaxY) - MathF.Max(canvas.MinY, pr.MinY);
            if (w > 0f && h > 0f) sum += w * h;
        }
        return sum;
    }

    /// <summary>把消息队列里现有的消息全部处理掉，不阻塞。测试模式复用同一份，
    /// 免得"测量用的循环"和"真正的循环"因为顺序不同而量出两个结论。</summary>
    internal void DrainMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) { _quit = true; break; }
            // PPT 轮询线程的唤醒消息：**不派发**（它没有对应的窗口）——
            // 主循环紧接着的 StepPpt() 会把新快照处理掉。
            if (msg.message == PptWatcher.WakeMessage) continue;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        NowMs = _clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>后台线程算完后，把“换图片像素”这类必须回 UI 线程的活排进来。</summary>
    private void PostUi(Action action)
    {
        if (action == null) return;
        _uiTasks.Enqueue(action);
        // 主循环空闲时停在 WaitMessage()：必须叫醒它，否则要等下一次鼠标移动才换图。
        if (_mainThreadId != 0)
            Native.PostThreadMessage(_mainThreadId, PptWatcher.WakeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void DrainUiTasks()
    {
        while (_uiTasks.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Console.WriteLine("[UI任务] " + ex.Message); }
        }
    }

    internal void RenderAll()
    {
        FrameCounter++;
        double frameStart = NowMs;
        double wall0 = _clock.Elapsed.TotalMilliseconds;

        // 自动存档：**放在这里**（每帧都过）而不是主循环里——主循环那条路
        // 只在"真有帧"时才走到，而自检是用"抽消息＋渲染"驱动的，挂在主循环里
        // 自检就永远验不到它（第一版就是这么漏的：改了板书也不写）。
        // 自检模式走临时路径（Recovery.AutoSavePathOverride），不会碰用户的板书。
        MaybeAutoSave();
        // 板书历史（0 号页快照）：只在**手停下来**之后写（见 MaybeHistorySnapshot 的注释）。
        MaybeHistorySnapshot();
        // 运行日志的设备卡（2026-10-17）：每天最多传一次（台数 / 型号统计；自检不传）。
        MaybeDeviceCardUpload();
        StepLogUpload();
        StepHandwritingOcr(); StepAiPlot();
        StepImageCard(); StepCardAnswers();
        StepAppDataJob();

        // PPT 状态机。**为什么两处都调**（这里 + 主循环 Loop 里）：主循环那条路
        // 只在"真有帧"时才走到，而自检是用"抽消息＋渲染"驱动的（同 MaybeAutoSave
        // 的理由）；反过来，空闲无帧时也要能响应翻页，所以 Loop 里那一句不能省。
        // 两边都是幂等的（TakeDirty 取走就清、SameAs 挡重复）。
        StepPpt();
        StepPptBar();     // 底部那条：引导过期 / "再点确认"过期（理由同上，自检那条路也走它）
        StepDocResume();  // 阅读位置节流保存（自检用"抽消息＋渲染"驱动，也走它）
        StepToast();      // 轻提示到点消失（2s；自检也用"抽消息＋渲染"驱动）
        StepSelectionMenu(); // 选中被清空时弹层跟着关（自检也走这一条）
        StepTouchFling(); // 双指纵滑松手后的惯性（每帧衰减；自检也走这一条）
        StepTimerCard();  // 课堂计时卡片同理：自检用"抽消息＋渲染"驱动，不走主循环
        StepRollCard();   // 课堂点名卡片同理
        PumpRadialPalette();   // 呼出盘同理：自检用"抽消息＋渲染"驱动，不走主循环

        // 书写期间的 GC 低延迟档：超时退回。放在这里**和自动存档同一个理由**——
        // 挂主循环里的话，自检那条路永远验不到"超时能退回"（见 GcLatency.cs）。
        // `_drawing` 传进去：手势还在进行就一直保持，别在一笔的中途退出来。
        GcLatency.Tick(_drawing);

        // The HUD text and the process counters cost an order of magnitude more
        // than drawing the ink does, so they refresh a few times a second rather
        // than every frame. Measured before this change: 2.7 ms + 3.5 ms per
        // frame, against 0.7 ms for the actual rendering.
        if (wall0 >= _nextStatsAt)
        {
            // 面板显示时才需要 4 Hz 刷新；只打日志的话 1 Hz 就够。
            // 进程计数一次要 ~3 ms，没必要每秒跑 4 次。
            _nextStatsAt = wall0 + (ShowHud ? 250 : 1000);
            UpdateStats();
            // 只在真的要显示面板时才拼字符串。BuildHudText 会去查进程内存，
            // 那是整条路径里最贵的一步；面板关掉还在跑就纯属浪费——
            // 实测它让空闲 CPU 从 ~0.3% 涨到 2~3.6%（单核）。
            if (ShowHud) HudText = BuildHudText();
        }

        // 相机写给各覆盖窗口：渲染的每一处变换都用它（见 OverlayWindow.CanvasToWindow）。
        //
        // ⚠ 渲染用的偏移**取整到整像素**（Excalidraw 的 `snapScrollToDevicePixels` 同款）：
        // 分块贴图走 NearestNeighbor，真实小数偏移会被驱动吸附，而活笔和命中测试走全精度——
        // 在"滚到底被 ClampOffset 夹出小数"或"翻页动画的中间帧"上，两者最多差半个像素
        // （表现是落笔提交瞬间墨可能轻轻跳一下、或缓存内容轻微脉动）。
        // 取整只作用于渲染层：引擎自己的 `ViewOffsetY` 保持全精度（输入映射/夹紧/可见区计算），
        // 差值 <1px，命中和观感都对得上。
        float renderCamY = MathF.Round(ViewOffsetY);
        float renderCamX = MathF.Round(ViewOffsetX);
        foreach (var w in _windows)
        {
            w.ViewOffsetX = renderCamX;
            w.ViewOffsetY = renderCamY;
            w.ViewScale = ViewScale;      // 阶段 1 = 1：渲染层先接上，不改观感
        }

        // 高清补丁：**每帧一次、由引擎挑驱动窗口**（相机已经推给各窗口，所以这里算出来的
        // 可见矩形就是这一帧真正要画的那块）。判据见 ChooseDocPatchDriver。
        SyncDocPatch();

        // 面板的接输入小窗跟着界面这一刻占的地方走（方案 B）。
        // 放在渲染之前：这一帧界面画在哪，输入就该收在哪，两件事同源。
        UpdateUiInputWindow();

        // 记下渲染前的文档版本：**界面在这一帧之前可能改文档**（"按住清空"就是在
        // `PrepareFrame` / `UpdateBandAction` 里够时间的——界面没有别的每帧回调可用）。
        // 见下面的判断。
        //
        // ⚠ 盯的是 **RenderDoc**（平时=真文档；回放中=影子文档）：脏区/增量清单是
        // 内容层照着渲染的那一份留下的。以前这里只清真文档，结果回放里影子文档的
        // `Dirty.Full` 永远清不掉——**每条笔迹写完都触发一次全屏整层重铺**（看着闪一下、
        // 白花一大笔重绘）。2026-10-01 用户报"写完一个字会闪一下"就是这个。
        var renderDoc = RenderDoc;
        long docVerBeforeRender = renderDoc.Version;

        foreach (var w in _windows)
            w.RenderFrame(this);

        // 补丁脏区是**广播**的：每个窗口都要把这块分块重铺一遍，所以**所有**窗口都渲染完之后
        // 才由引擎清（哪个窗口先清，后面的窗口就漏铺 → 副屏留着上一张高清图）。
        // 见 `OverlayWindow.SyncDocPages` 里那段"只标记、不清单"。
        DocPatch?.DirtyRects.Clear();

        foreach (var w in _windows)
            w.Present();

        // 文本框编辑：透明背景要等这一拍把“正在编辑的框”隐藏并 Present 后，
        // 才能从屏幕截到它下面的画布。
        CapturePendingTextBoxSnapshot();

        // 这一拍已经把"激光淡完后的空画面"贴上去了，欠账可以销掉。
        // 必须放在 Present 之后：提前销掉的话，NeedsFrame 会在下一拍放行，
        // 而后缓冲里仍然留着最后一帧激光——表现就是那道红影忽然定住不消失。
        Laser.ClearFramePainted();

        if (LatencyRecording) RecordLatencySample();

        // 界面自己要求的重画（InvalidateUi）已经在这一帧贴完，可以清掉了。
        UiInvalidatePending = false;

        // Every window has now applied this round of changes, so the stale
        // regions can be dropped. Doing it here (rather than inside a window)
        // is what keeps multi-monitor setups correct.
        //
        // **但渲染期间改了文档就不能清**：这一帧贴出去的是改之前的像素，
        // 脏区得留给下一帧。这里踩过一个真 bug（用户报的"橡皮清空没有用、
        // 还把墨迹卡住、连常规橡皮都擦不掉"）：清空是在界面的 Render 里触发的，
        // 引擎渲染完无条件 Reset()，于是**文档已经空了、屏幕上那层墨还留着**——
        // 看着像清空失效，而橡皮也擦不掉（文档里已经没有东西可擦了）。
        if (renderDoc.Version == docVerBeforeRender)
        {
            renderDoc.Dirty.Reset();
            renderDoc.AppendedSinceRender.Clear();
            renderDoc.StructureChangedSinceRender = false;
        }

        var w0 = _windows[0];
        _lastRebuildMs = w0.LastRebuildMs;
        _lastRecordMs = w0.LastRecordMs;
        _lastPresentMs = w0.LastPresentMs;
        _lastPresentWaitMs = w0.LastPresentWaitMs;
        _tilesUsed = w0.LastTileCount;
        _tilesVisible = w0.LastTileVisible;
        _tilesBudget = w0.LastTileBudget;
        _tilesRasterized = w0.LastPatchCount;
        _prefetchLast = w0.LastPrefetch;

        // How long the newest input took to reach the screen. This is our own
        // contribution; the display pipeline adds up to one more scan-out.
        // 只在"这一帧确实处理了新输入"时采样，而且采完就作废。
        //
        // 这里原来有个 bug：_lastInputMs 用完不清零，于是空闲帧（激光动画、
        // 或只是等垂直同步的那一帧）会把同一条旧输入再算一遍，而且越算越大，
        // 把 EMA 一路往上带。面板上"输入到上屏"那个数因此偏大且不收敛。
        if (_lastInputMs > 0)
        {
            double sample = _clock.Elapsed.TotalMilliseconds - _lastInputMs;
            if (sample >= 0 && sample < 200)
                _inputToPresentMs = _inputToPresentMs <= 0
                    ? sample
                    : _inputToPresentMs * 0.85 + sample * 0.15;
            _lastInputMs = -1;
        }

        double wall = _clock.Elapsed.TotalMilliseconds;
        _lastFrameMs = wall - frameStart;
        // 手测台：把这一帧的"重画 + 整帧"耗时喂给采集器（它只在拖拽中累计）。
        if (EraserTelemetry != null)
        {
            EraserTelemetry.Frame(_windows.Count > 0 ? _windows[0].LastPatchMs : 0,
                                  _lastFrameMs, _inputToPresentMs);
            EraserTelemetry.Beat(NowMs, _fps, Doc);
        }

        _frames++;
        if (wall - _fpsWindowStart >= 500)
        {
            _fps = _frames * 1000.0 / (wall - _fpsWindowStart);
            _frames = 0;
            _fpsWindowStart = wall;
        }

        _accRecord += _lastRecordMs;
        _accPresent += _lastPresentMs;
        _accRaster += _lastRebuildMs;
        _accFrame += _lastFrameMs;
        // 面板自己的代价从窗口那边取（重排 + 贴图），不是这里的"统计"耗时——
        // 混在一起会让"面板很贵"这件事被统计代码盖住。
        _lastHudMs = _windows.Count > 0 ? _windows[0].LastHudMs : 0;
        _accPanel += _lastHudMs;
        if (_windows.Count > 0)
        {
            double redraw = _windows[0].LastHudRedrawMs;
            if (redraw > 0) { _hudRedrawSum += redraw; _hudRedrawCount++; }
            _hudRedrawTotal = _windows[0].HudRedraws;
        }
        _accFrames++;

        // 运行日志的性能快照（2026-10-17）：每 5 分钟一条窗口快照 + 慢帧（≥150ms）立刻记。
        // 代价 = 每帧几次加法；写盘走日志的队列（见 DiagLog.NoteFrame）。
        DiagLog.DocOpenNote = DocView.IsOpen ? "文档开着" : BoardOn ? "白板" : "桌面";
        bool logTick = NowMs >= _nextLogAt;
        // 动因只在"要写日志"的那一帧拼字符串（1Hz / 慢帧），不在 60fps 热路径里分配。
        string drive = (_lastFrameMs >= 50 || logTick) ? FrameDriveText() : "";
        DiagLog.NoteFrame(NowMs, _fps, _lastFrameMs, _lastPresentMs, _workingSetMb, drive);
        MaybeWarnIdleFrames();

        if (logTick)
        {
            _nextLogAt = NowMs + 1000;
            int n = Math.Max(1, _accFrames);
            Console.WriteLine(
                $"[{NowMs / 1000,6:F1}s] fps={_fps,5:F1}"
                + $" 记录={_accRecord / n,5:F2} 上屏={_accPresent / n,5:F2}"
                  + $" 光栅={_accRaster / n,5:F2}"
                  + $" 面板={_hudRedrawMs,4:F2}ms×{_hudRedrawPerSec:F0}/s 统计={_lastStatsMs,5:F2}"
                  + $" 总帧={_accFrame / n,6:F2}ms"
                  + $" 置顶={_lastTopmostMs,5:F2}ms"
                  + $" 脏区={_windows[0].LastPresentRectCount}块/{_windows[0].LastPresentAreaPercent,5:F1}%"
                  + (_windows[0].LastPresentFull ? "（整屏）" : "")
                  + $" 动因={drive}"
                  + (DocView.IsOpen ? $" 页={DocView.ResidentPages}/{DocView.ResidentBytes / 1048576.0:F1}MB" : "")
                  + $" ws={_workingSetMb,6:F1}MB");
            _accRecord = _accPresent = 0;
            _accFrame = _accRaster = 0;
            _hudAvgMs = _accPanel / n;
            _accPanel = 0;
            double secs = Math.Max(0.001, (NowMs - _hudRateWall) / 1000.0);
            _hudRedrawPerSec = (_hudRedrawTotal - _hudRedrawAtWall) / secs;
            _hudRedrawAtWall = _hudRedrawTotal;
            _hudRateWall = NowMs;
            _hudRedrawMs = _hudRedrawCount > 0 ? _hudRedrawSum / _hudRedrawCount : 0;
            _hudRedrawSum = 0; _hudRedrawCount = 0;
            _accFrames = 0;
        }
    }

    /// <summary>
    /// 显存已用量（MB）。
    ///
    /// 为什么要显示它：**核显的显存是从系统内存里分的**，而它**不计入进程工作集**。
    /// 任务管理器那一列在核显机器上会把 GPU 共享显存算进去，于是出现
    /// "任务管理器 1000MB、面板只有 100MB"——不是面板不准，是只报了一个口径，
    /// 恰好漏掉了最大的一块。
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
    /// <summary>触点诊断：指针事件顺手更新（只有开关打开时才走，平时零开销）。</summary>
    private void TouchDiagFeed(uint id, uint ptype, bool down)
    {
        if (!TouchHud) return;
        if (ptype == Native.PT_TOUCH)
        {
            if (down)
            {
                _touchDiagIds.Add(id);
                if (_touchDiagIds.Count > TouchHudMax) TouchHudMax = _touchDiagIds.Count;
            }
            var (w, h) = ReadTouchSizePx(id);
            if (w > 0f || h > 0f)
            {
                TouchHudSawArea = true;
                if (w > _touchDiagMaxW) _touchDiagMaxW = w;
                if (h > _touchDiagMaxH) _touchDiagMaxH = h;
            }
        }
        _touchDiagLast = ptype switch
        {
            Native.PT_TOUCH => "触摸",
            Native.PT_PEN => "笔",
            Native.PT_MOUSE => "鼠标",
            _ => _touchDiagLast,
        };
        // 限频重建（触点数量变化时立刻重建；面积那种连续变化按 200ms 收口）
        if (down || NowMs >= _touchDiagNextMs)
        {
            _touchDiagNextMs = NowMs + 200;
            RebuildTouchHud();
            _dirty = true;
        }
    }

    private void RebuildTouchHud()
    {
        string area = TouchHudSawArea
            ? $"接触面积：见过最大 {_touchDiagMaxW:F0} × {_touchDiagMaxH:F0} 物理像素"
            : "接触面积：没见过非零值 → 这块屏不上报面积（手掌擦不可用，三指擦照常）";
        TouchHudText = $"最近输入：{_touchDiagLast}　当前 {_touchDiagIds.Count} 指　最多 {TouchHudMax} 指\n"
                     + area + "\n"
                     + "试：1 / 2 / 3 根手指各按一下，再用手掌压一下";
    }

    /// <summary>读一个触摸触点的接触尺寸（物理像素）。用显式缓冲区（见 Native 那边的说明）。</summary>
    private static (float w, float h) ReadTouchSizePx(uint id)
    {
        IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(160);
        try
        {
            if (!Native.GetPointerTouchInfo(id, buf)) return (0f, 0f);
            var ti = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.POINTER_TOUCH_INFO>(buf);
            // ⚠ 设备不上报面积时，rcContact 规范默认是"以指针为中心的 0×0"——
            // 所以先看 touchMask 说没说它有效（2026-10-05 修：此前结构体漏了
            // touchFlags/touchMask，整体偏移 8 字节，读到的"面积"其实是坐标）。
            if ((ti.touchMask & Native.TOUCH_MASK_CONTACTAREA) == 0) return (0f, 0f);
            int w = ti.rcContact.Width, h = ti.rcContact.Height;
            if (w <= 0 && h <= 0) { w = ti.rcContactRaw.Width; h = ti.rcContactRaw.Height; }
            return (w, h);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
    }

    private string BuildHudText()
    {
        // 面板的排版原则：**一行一类事**，数字对齐，单位统一。
        // 内存那一行是重点：三个口径并列，并且把"任务管理器里叫哪一列"写出来
        // ——用户拿它跟任务管理器对照时最需要的就是这句话。
        return
            $"帧率 {_fps,6:F1} fps（空闲不重绘）   帧耗时 {_lastFrameMs,5:F2} ms   输入→Present {_inputToPresentMs,5:F1} ms\n" +
            $"绘制 {_lastRecordMs,5:F2} ms      上屏 {_lastPresentMs,5:F2} ms（配速等待 {_lastPresentWaitMs,5:F2}）      分块光栅 {_lastRebuildMs,5:F2} ms\n" +
            $"面板 {_hudRedrawMs,4:F2} ms/次 ×{_hudRedrawPerSec,3:F0}/秒（摊到每帧 {_hudAvgMs,4:F2} ms）   统计 {_lastStatsMs,5:F2} ms\n" +
            $"内存 {_privateWsMb,6:F1} MB（专用工作集 = 任务管理器的“内存”）\n" +
            $"已提交 {_privateMb,6:F1} MB（=“提交大小”）   工作集 {_workingSetMb,6:F1} MB（含共享）\n" +
            $"共享提交 {_sharedCommitMb,6:F1} MB   显存 {_gpuMb,6:F1} MB   CPU（全机） {_cpuPercent,4:F1} %\n" +
            $"笔画 {Doc.Strokes.Count}      点数 {Doc.TotalPoints}\n" +
            $"选中 {Doc.Selected.Count}      工具 {ToolName(Tool)}{SelectModeTag()}{(PassThrough ? "（穿透中）" : "")}      粗细 {CurrentToolWidthLogical,4:F1}      撤销栈 {Doc.UndoDepth}\n" +
            $"分块 {_tilesUsed}/{_tilesBudget}（可见 {_tilesVisible}，本帧光栅 {_tilesRasterized}，预取 {_prefetchLast}）      网格 {Doc.GridCells}\n" +
            $"Ctrl+Alt：1笔 2荧光 3激光 4橡皮 7像素橡皮 5框选 6粗细 Z撤销 C清除全部\n" +
            (EraserTelemetry != null
                ? $"橡皮手测台：记录中 · 已记 {EraserTelemetry.DragCount} 条拖拽（退出时写汇总）\n"
                : "") +
            $"湿墨档位 {WetMode} {WetModeName(WetMode)}（Ctrl+Alt+Shift+W 切下一档）\n" +
            $"其它 Ctrl+Alt：I面板 P穿透 K键盘 Y穿透方式 X退出";
    }

    /// <summary>面板上跟着"工具"显示的当前选择方式（只有框选工具用得上）。</summary>
    private string SelectModeTag()
        => Tool == Tool.Marquee ? (SelMode == SelectMode.Lasso ? "·套索" : "·矩形") : "";

    /// <summary>工具名（遥测 / HUD / 日志用）。名字表在公开的 <see cref="ToolNames"/>——
    /// 界面层的悬停提示也读同一份（2026-10-02），避免两处各写一份。</summary>
    private static string ToolName(Tool t) => ToolNames.Of(t);

    // =====================================================================
    //  Window procedure
    // =====================================================================

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // **系统来问"要不要那条长按手势"**：一律回"不要"。
        //
        // 这是关掉"按住不动 = 右键"的**主路**（另两条在 Native.DisableSystemPressAndHold 里）：
        // 官方文档对这一位的原话就是 "disables press and hold (right-click) gesture"，
        // 也就是它同时干掉手势和右键消息。回在这里而不是某个窗口分支里，
        // 是因为系统可能拿**任意一个**我们的窗口来问（覆盖层、接输入小窗都算）。
        //
        // 顺带一条官方说明：关掉之后左键消息**不再需要等那段"区分长按与单击"的延迟**，
        // 落笔更跟手。
        if (msg == Native.WM_TABLET_QUERYSYSTEMGESTURESTATUS)
            return new IntPtr(Native.TABLET_DISABLE_PRESSANDHOLD);

        // 第二个进程双击启动：叫醒已有实例（退出穿透 + 展开工具条）。
        if (msg == Native.WM_INKTEACH_ACTIVATE)
        {
            ActivateFromExternal();
            return IntPtr.Zero;
        }

        // 面板的"接输入小窗"（方案 B）：它只收输入，什么都不画。
        if (_uiInputHwnd != IntPtr.Zero && hWnd == _uiInputHwnd)
            return UiInputWndProc(hWnd, msg, wParam, lParam);

        // PPT 条那一块的"接输入小窗"（同一套方案，只在放映 + 穿透时存在）。
        // 为什么条需要它、而覆盖层那条 NCHITTEST 豁免不够用，见 Ppt.cs 里
        // `_pptInputHwnd` 那一段注释（一句话：穿透用的 WS_EX_TRANSPARENT 让
        // 系统跳过命中测试，豁免根本执行不到）。
        if (_pptInputHwnd != IntPtr.Zero && hWnd == _pptInputHwnd)
            return PptInputWndProc(hWnd, msg, wParam, lParam);

        // 课堂计时卡片的"接输入小窗"（同一套方案；卡片和 PPT 条不相邻，所以单开一块）。
        if (_timerInputHwnd != IntPtr.Zero && hWnd == _timerInputHwnd)
            return TimerInputWndProc(hWnd, msg, wParam, lParam);

        // 点名卡片的"接输入小窗"（同上）。
        if (_rollInputHwnd != IntPtr.Zero && hWnd == _rollInputHwnd)
            return RollInputWndProc(hWnd, msg, wParam, lParam);

        // 宿主自己的窗口（开发期的点击目标）先处理。产品界面不会用到这一层。
        if (HandleHostWindowMessage(hWnd, msg, wParam, lParam, out var hostResult))
            return hostResult;

        // 滚轮：滚动画布（只改相机偏移，不动对象数据）。见 HandleWheel。
        if (msg == 0x020A /*WM_MOUSEWHEEL*/) return HandleWheel(wParam);
        // 批注键盘模式下的按键。只有这个模式收得到——见 SetKeyboardMode。
        // 工具键（Ctrl+P/I/L/E/M）走手势状态机，需要"松键"和"是不是自动重复"两件事：
        // 自动重复 = lParam bit30（见 ToolKeyDown 的说明）。
        if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
        {
            // 截图流程里的按键（8.3.1）：**取景期间是模态的**——
            //   · Esc = 取消（什么都不落，回到进来之前的工具）；
            //   · Enter = 完成（调整阶段）；方向键 = 微调框（调整阶段，Shift = 改大小）；
            //   · Ctrl+Shift+A = 换另一种截法（重新冻结一次）；
            //   · 其它键一律吞掉（取景中按 Ctrl+P 换工具会把状态搅乱）。
            if (ColorPickActive)
            {
                if (wParam.ToInt32() == 0x1B /*VK_ESCAPE*/) { CancelColorPick(); return IntPtr.Zero; }
                if (wParam.ToInt32() == 0x43 /*C*/
                    && (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0)
                {
                    CopyColorPickHex();
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;                 // 吸管期间吞掉其它按键
            }
            if (CaptureActive)
            {
                int vk = wParam.ToInt32();
                if (vk == 0x1B /*VK_ESCAPE*/) { CancelCapture(); return IntPtr.Zero; }
                if (CaptureAdjusting && (vk == 0x0D || vk == 0x0A) /*VK_RETURN*/)
                { ConfirmCapture(); return IntPtr.Zero; }
                if (CaptureAdjusting && (vk is 0x25 or 0x26 or 0x27 or 0x28 /*方向键*/))
                { CaptureNudge(vk); return IntPtr.Zero; }
                if (vk == 0x53 /*S*/ && (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0)
                { ToggleCaptureMode(); return IntPtr.Zero; }
                return IntPtr.Zero;
            }
            // 只点了「截屏」格、还没进取景：Esc = 把工具还给进来之前那个（和取景里同一个口径）
            if (Tool == Tool.Capture && wParam.ToInt32() == 0x1B)
            {
                var back = _toolBeforeCapture;
                if (back == Tool.Capture) back = Tool.Pen;
                SwitchTool(back);
                _dirty = true;
                ApplyCursor();
                NotifyUiStateChanged();
                return IntPtr.Zero;
            }
            bool repeat = (lParam.ToInt64() & 0x40000000) != 0;
            if (HandleKeyDown(wParam, repeat)) return IntPtr.Zero;
        }
        if ((msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) && CaptureActive)
            return IntPtr.Zero;                     // 取景期间连同抬手一起吞（键位表别残留半套状态）
        if ((msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP) && HandleKeyUp(wParam))
            return IntPtr.Zero;

        switch (msg)
        {
            case Native.WM_NCHITTEST:
                // 这两个计数只给自检用：它回答"穿透时系统到底还问不问我"，
                // 这是判定"能不能只靠命中测试做区域穿透"的唯一硬证据。
                _cntNcHitTest++;
                // 截图 / 吸管是**整屏模态**：冻结帧铺满整个屏幕，这一下必须归我们。
                // 穿透模式也照此处理——否则取色点落在画布上时会漏给下层程序，
                // 吸管看着有预览、却点不下去。
                if (ColorPickActive || CaptureActive)
                {
                    _cntNcHitClient++;
                    return new IntPtr(Native.HTCLIENT);
                }
                if (!PassThrough)
                {
                    _cntNcHitClient++;
                    return new IntPtr(Native.HTCLIENT);
                }
                // 穿透模式下，**界面自己那一块仍然归界面**。
                //
                // 以前这里一律回 HTTRANSPARENT，于是"开着穿透还想用工具条"这个
                // 最常见的组合直接失效：命中测试在系统那一层就把我们排除了，
                // WM_POINTERDOWN 根本轮不到引擎，悬浮球变成了一个画出来的装饰。
                HitTestPoint(lParam, out float hitX, out float hitY);
                // PPT 条（放映时底部那两条）也算"我的地盘"：**开着穿透时老师照样得能
                // 点翻页 / 拖进度条**——不然那一下会落到下层 PPT 上，被它当成翻页点击。
                bool mine = UiContains(hitX, hitY) || PptBarContains(hitX, hitY)
                         || ReplayBarContains(hitX, hitY)
                         || TimerCardContains(hitX, hitY) || RollCardContains(hitX, hitY);
                if (mine) _cntNcHitClient++;
                return new IntPtr(mine ? Native.HTCLIENT : Native.HTTRANSPARENT);

            // 指针形状。**必须显式回答**：不处理时系统会拿窗口类的光标兜底，
            // 而类光标为 NULL 的表现就是"永远转圈"（这次修的就是它）。
            //
            // 只在"移动且输入未被捕获"时才会收到这条消息——所以拖拽中的光标
            // 得在按下那一刻自己设（见 OnPointerDown），不能等这里。
            case Native.WM_SETCURSOR:
                // 非客户区（边框、缩放角）由系统按自己的规矩来。
                if ((lParam.ToInt64() & 0xFFFF) != Native.HTCLIENT) break;
                // 穿透：面板之外让下层窗口决定；**面板自己那一块要给箭头**
                // （否则"工具条上顶着一个笔尖圈"或者一个转圈光标）。
                if (PassThrough && !_uiHover) break;
                ApplyCursor(force: true);
                return new IntPtr(1);                   // TRUE = 已处理，别再兜底

            case Native.WM_POINTERENTER:
                PointerInside = true;
                TrackPointerLeave(hWnd);
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                // 指针离开窗口：落点反馈（橡皮圆环、笔尖环）必须跟着消失，
                // 否则手一移开，屏幕上就留下一个圈。
                PointerInside = false;
                ClearEngineTooltip();          // 悬停提示同理：人走了，提示不能留在屏幕上
                _dirty = true;
                ApplyCursor();
                // 注意：**这里不能叫 Ui.PointerLeave()**。覆盖层的"离开"在面板接管输入时
                // 恰恰是"指针进了面板"的那一刻（人从画布移到工具条上），
                // 界面收到的会是反的。界面那条离开由接输入小窗发（见 UiInputWndProc）。
                return IntPtr.Zero;

            case Native.WM_MOUSELEAVE:
                PointerInside = false;
                ClearEngineTooltip();
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
                OnPointerDown(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUPDATE:
                OnPointerMove(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUP:
                OnPointerUp(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_INPUT:                     // `--rawprobe` 只数数；`--rawinput` 顺带补采样点
                HandleRawInput(lParam);
                return IntPtr.Zero;
            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                CancelLibraryPanelDrag();
                CancelLibraryTouchScroll();
                EndStroke();
                return IntPtr.Zero;

            // 右键：截图流程里 = 取消（和 Esc 一条口径，8.3.0）。
            // 鼠标和笔的桶状键都会合成 WM_RBUTTONDOWN，所以这一条两种设备都覆盖。
            case Native.WM_RBUTTONDOWN:
                if (LibraryPlacementMode)
                {
                    LibraryUseSelected = null;
                    _libraryPending = null;
                    _libraryPendingTitle = null;
                    NotifyUiStateChanged();
                    _dirty = true;
                    return IntPtr.Zero;
                }
                if (ColorPickActive) { CancelColorPick(); return IntPtr.Zero; }
                if (CaptureActive) { CancelCapture(); return IntPtr.Zero; }
                break;

            case Native.WM_HOTKEY:
                HandleHotkey(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;

            case Native.WM_TIMER:
                // **停顿成型那颗 40ms 的定时器**（id = 2）只问一件事："笔停了多久"。
                // 顺手做的置顶那一套（id = 1 那颗 250ms 的）在这里**不重复做**——
                // 不分开的话它会跟着 40ms 的节奏跑，一秒凭空多 25 次置顶。
                if (wParam.ToInt32() == DwellTimerId)
                {
                    TickDwellShape();
                    return IntPtr.Zero;
                }
                // 只有要显示性能面板时才需要周期性重绘；面板关掉还每秒重画 4 次
                // 纯属白烧电。
                if (ShowHud) _dirty = true;
                UpdateScrollBarHoverFromCursor();
                var swTop = Stopwatch.StartNew();
                ReassertTopmost();
                swTop.Stop();
                _lastTopmostMs = swTop.Elapsed.TotalMilliseconds;
                MaybeSweepHistory();      // 启动后首个空闲帧做一次历史清理（墨迹 B）
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _quit = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 让宿主处理属于它自己创建的窗口的消息。返回 true 表示消息已被消费。
    /// </summary>
    protected virtual bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    private void ReassertTopmost()
    {
        // **弹着系统对话框的时候一律不抬**（用户 2026-09-17 报的"点开一下就收回去、
        // 选不到 jpg"，2026-09-17 探针定位）。
        //
        // 这一抬是每秒一次的定时器干的（WM_TIMER），平时是必要的：全屏覆盖层被别的
        // 程序抢到后面去就"看不见也画不上"。但**弹着"另存为"的时候它是有害的**：
        // 覆盖层被重新抬到置顶，就去跟对话框抢那一层，对话框自己的**下拉列表**
        // （普通弹窗，不在置顶层）随即被盖住 / 被关掉——探针连着拍三张看得很清楚：
        // 点下拉 150 毫秒时列表好好地开着（PNG / JPEG 两条），900 毫秒时已经没了。
        // 那 900 毫秒正好压着一次定时器。
        //
        // 对话框期间由 ExportFileDialog 自己保证"它在最上面、而且一直置顶"
        // （见 CenterAndBringUp），关掉之后 ReturnFocusAfterDialog 再把覆盖层拾回来。
        if (ExportDialogOpen) return;

        // **让路一拍**：前台是别的"置顶层"窗口（微信截图、Win+Shift+S 的截图条等）时不抬——
        // 每秒一次的抬举会把我们重新压到它上面，截图框在屏幕上就永远看不见
        // （用户 2026-10-03 报"系统截图被批注层盖住"）。等前台回到我们或普通窗口，
        // 下一拍定时器自然抬回来。
        if (ShouldYieldTopmost()) return;

        foreach (var w in _windows)
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        // 接输入小窗**必须排在最后抬**：两块都是置顶窗口，谁最后 SetWindowPos
        // 谁在上面（见 计划-底层对接界面.md 4.1 的"层序"纪律）。只在这一处抬，
        // 别处不许再抬它，否则会出现"面板偶尔被自己的笔迹层盖住"。
        if (_uiInputShown && _uiInputHwnd != IntPtr.Zero)
            Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 前台是不是"别的置顶层窗口"——是就让路一拍（截图工具/置顶播放器等都是这一层）。
    /// 普通窗口不用让：我们抬上去本来就不挡它。
    /// </summary>
    private bool ShouldYieldTopmost()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        long ex = Native.GetWindowLongPtr(fg, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOPMOST) == 0) return false;
        return !IsOwnWindow(fg);
    }

    /// <summary>这个窗口是不是我们自己创建的（覆盖层 / 各接输入小窗）。</summary>
    private bool IsOwnWindow(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        foreach (var w in _windows) if (w.Hwnd == h) return true;
        return h == _uiInputHwnd || h == _pptInputHwnd
            || h == _timerInputHwnd || h == _rollInputHwnd;
    }

    /// <summary>
    /// 这一刻该不该出一帧：脏了，或者有东西还在动。
    ///
    /// 三个"在动"的来源，各有各的理由：
    ///   · 激光轨迹：它**松手后停留 2 秒再整体淡出**（见 `LaserTrail`），不驱动的话
    ///     停留结束那一刻没人去推进淡出，那道光芒会一直挂在屏幕上；
    ///   · 正在书写：笔尖这条线每帧都在变；
    ///   · **界面自己声明的动画**（<see cref="IOverlayUi.IsAnimating"/>）：
    ///     展开/收起、悬停展开、贴边吸附全靠它，缺了就是"动画停在第一帧"。
    ///
    /// 自检直接调它来数帧——这样"引擎给不给帧"用的是**同一个判据**，
    /// 不会出现"测的是一套、跑的是另一套"。
    /// </summary>
    internal bool NeedsFrame()
    {
        var d = FrameDrive.None;
        if (_dirty) d |= FrameDrive.Dirty;
        // 最后一帧淡完/清空后还要补一帧空画面，否则后缓冲里会留着上一帧的激光。
        if (Laser.Visible || Laser.NeedsClearFrame) d |= FrameDrive.Laser;
        if (_drawing) d |= FrameDrive.Drawing;
        if (SelFlashing) d |= FrameDrive.Selection;
        if (LibraryFeedbackActive) d |= FrameDrive.Selection;
        if (UiIsAnimatingNow) d |= FrameDrive.UiAnim;
        if (_camAnimating) d |= FrameDrive.Camera;
        if (_replayPlaying) d |= FrameDrive.Replay;
        if (TimerWantsFrame) d |= FrameDrive.Timer;
        if (RollWantsFrame) d |= FrameDrive.Roll;
        if (RadialPaletteActive) d |= FrameDrive.Radial;
        if (TooltipPending) d |= FrameDrive.Tooltip;
        if (_windows.Exists(w => w.TrailEndPending)) d |= FrameDrive.Trail;
        // 缩放停手后的"换分块倍率"是一个**定时待办**（防抖 120ms）：不算进这里，
        // 界面不再置脏时主循环会睡在 WaitMessage 上，倍率永远换不了（拉伸糊图常驻）。
        if (_windows.Exists(w => w.TileScalePending)) d |= FrameDrive.Dirty;
        // 文档高清补丁：>100% 且"还缺补丁 / 等落稳 120ms / 有请求在飞"→ 定时待办。
        // 和分块倍率同一个理由：界面不再置脏时主循环会睡在 WaitMessage 上，补丁排不出来。
        // 缩回 100% 后的一次性空闲清理也由它报（待办期间即使 100% 也返回 true）。
        if (DocPatch is { WantsFrame: true }) d |= FrameDrive.Dirty;
        if (AlignSnapPulseActive) d |= FrameDrive.Align;
        if (_g2FlingActive) d |= FrameDrive.TouchFling;

        _frameDrive = d;
        return d != FrameDrive.None;
    }

    /// <summary>把这一帧的出帧原因翻成一句人能读的话（只在 1Hz/慢帧日志里调，不在热路径里拼）。</summary>
    private string FrameDriveText()
    {
        var d = _frameDrive;
        if (d == FrameDrive.None) return "无";
        var parts = new List<string>(4);
        if ((d & FrameDrive.Dirty) != 0) parts.Add("脏区");
        if ((d & FrameDrive.Drawing) != 0) parts.Add("书写");
        if ((d & FrameDrive.Laser) != 0) parts.Add("激光");
        if ((d & FrameDrive.Selection) != 0) parts.Add("选中闪烁");
        if ((d & FrameDrive.UiAnim) != 0) parts.Add("界面动画");
        if ((d & FrameDrive.Camera) != 0) parts.Add("翻页动画");
        if ((d & FrameDrive.Replay) != 0) parts.Add("回放");
        if ((d & FrameDrive.Timer) != 0) parts.Add("计时器");
        if ((d & FrameDrive.Roll) != 0) parts.Add("点名");
        if ((d & FrameDrive.Radial) != 0) parts.Add("呼出盘");
        if ((d & FrameDrive.Tooltip) != 0) parts.Add("悬停提示");
        if ((d & FrameDrive.Trail) != 0) parts.Add("轨迹收尾");
        if ((d & FrameDrive.Align) != 0) parts.Add("吸附提示");
        if ((d & FrameDrive.TouchFling) != 0) parts.Add("触摸惯性");
        return string.Join("+", parts);
    }

    /// <summary>
    /// "没人操作，却一直在出帧"的告警（2026-10-17 加：线上日志里抓到一台 Win10
    /// 空闲 45fps、记录 13ms/帧，但日志没写是谁在驱动）。
    ///
    /// 只在**确实空闲**（10 秒没有按下/按键）、**没有合理的长动画**（计时器/回放/呼出盘/
    /// 激光淡出）时告警；30 秒最多一条，不会刷屏。正常写字、PPT 翻页、点名都不会触发。
    /// </summary>
    private void MaybeWarnIdleFrames()
    {
        if (_drawing || _quit) return;
        if (_lastActivityMs <= 0) return;
        double idle = NowMs - _lastActivityMs;
        if (idle < 10_000) return;
        if (NowMs < _idleWarnAtMs) return;
        var d = _frameDrive;
        // 这些是"用户/界面明确要求的持续动画"，不算异常。
        if ((d & (FrameDrive.Timer | FrameDrive.Roll | FrameDrive.Radial | FrameDrive.Replay
                  | FrameDrive.Laser | FrameDrive.TouchFling)) != 0) return;
        if ((d & (FrameDrive.UiAnim | FrameDrive.Selection | FrameDrive.Camera
                  | FrameDrive.Tooltip | FrameDrive.Trail | FrameDrive.Align)) == 0) return;
        _idleWarnAtMs = NowMs + 30_000;
        Console.WriteLine($"[perf] 空闲仍在出帧：动因={FrameDriveText()}，已空闲 {idle / 1000:F0}s，"
            + $"fps={_fps:F0}，记录≈{_lastRecordMs:F1}ms，ws={_workingSetMb:F0}MB");
    }

}
