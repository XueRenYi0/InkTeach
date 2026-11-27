// [职责] 自动更新：检查/下载/换壳。
// [测试] --updatetest

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
    //  自动更新（2026-09-29）
    // =====================================================================
    //
    // 分工：**网络全在后台线程**，主线程只做两件事——把结果变成状态文字、
    // 以及（下完之后）拉起换壳脚本然后退出自己。理由：主循环还扛着渲染和输入，
    // 让它在十几秒的下载里卡住是不能接受的。
    //
    // 状态字段由**主线程**写（界面在读），后台线程只往 `_updResult` / `_updZipPath`
    // 里放结果、再把 `_updPost` / `_updApplyPosted` 立起来。主循环每帧 `PumpUpdate()` 收一次。

    /// <summary>自动更新的状态（界面那一行显示什么、点了做什么，都看它）。</summary>
    internal UpdateStage UpdateState = UpdateStage.NotConfigured;
    /// <summary>自动更新的一行状态文字。</summary>
    internal string UpdateText = "未配置更新源";

    private float UpdateProgress;                       // 0..1（下载中，只给界面看）
    private string _updVersion = "", _updNotes = "", _updSha = "";
    private volatile bool _updPost;                     // 后台：检查结果放好了
    private volatile bool _updApplyPosted;              // 后台：下载结束了
    private volatile bool _updBusy;                     // 有后台任务在跑（别叠加）
    /// <summary>
    /// 自检/验收用：查到新版本就**自动继续下载安装**（不用等人再点一下）。
    /// 产品里永远是 false（用户点两下：一下查、一下装）。
    /// </summary>
    internal bool AutoApplyUpdate;
    /// <summary>
    /// 自检/验收用：`--updatecheck` 只查不装时，**查完就自己退出**
    /// （产品里没有这个开关——用户点一下查、再点一下装，界面当然要留着）。
    /// </summary>
    internal bool AutoCheckOnly;

    /// <summary>
    /// 静默自检：启动后 8 秒查一次、之后每 6 小时一次。查到新版本才改状态；
    /// 已是最新 / 网络失败都还原成 Idle，面板状态行仍然显示版本号。
    /// 这样“更多”上的红点不需要用户先手动点一次检查。
    /// </summary>
    private bool _autoUpdateCheck;
    private double _nextUpdateCheckAtMs = -1;
    private const double UpdateCheckFirstDelayMs = 8_000;
    private const double UpdateCheckRepeatMs = 6 * 60 * 60 * 1000;

    private readonly object _updLock = new();
    private (UpdateFeed.Manifest m, string err, List<(string Url, bool UseProxy)> dl) _updResult;
    private string _updUsedUrl = "";
    private string _updZipPath = "", _updError = "";
    private List<(string Url, bool UseProxy)> _updDownloads = new();   // 下载候选（快的在前，失败自动换下一条）
    private long _updGot, _updTotal;                    // 下载进度（后台写、主线程读）

    /// <summary>「检查更新」被点了一下（见 <see cref="IEngineCommands.CheckUpdate"/>）。</summary>
    internal void CheckUpdateFromUi()
    {
        if (!UpdateFeed.HasAnySource)
        {
            // **没配更新源是正常状态，不是错误**（教室机器可以指到局域网共享；
            // 一条都没有时点「检查更新」只改一行状态文字）
            UpdateState = UpdateStage.NotConfigured;
            UpdateText = "未配置更新源";
            NotifyUiStateChanged();
            Console.WriteLine("自动更新：没有可用的更新源（settings.json 的 update.url，或 UpdateFeed.Sources）");
            return;
        }
        if (_updBusy) return;                           // 已经在查 / 在下，别叠加

        UpdateState = UpdateStage.Checking;
        UpdateText = "检查中…";
        NotifyUiStateChanged();

        Console.WriteLine($"自动更新：检查（当前 {UpdateFeed.CurrentVersion}；"
                          + (UpdateFeed.Url.Length > 0 ? "用户配置源" : $"候选 {UpdateFeed.Sources.Length} 条，并行试")
                          + (UpdateFeed.Url.Length > 0 ? "" : "；加速站直连、GitHub 那条走系统代理") + "）");
        _updBusy = true;
        var th = new System.Threading.Thread(() =>
        {
            var m = UpdateFeed.FetchBest(UpdateFeed.CurrentVersion, out string used, out string err, out var dl);
            lock (_updLock)
            {
                _updResult = (m, err, dl);
                _updUsedUrl = used;
            }
            _updPost = true;
        })
        { IsBackground = true, Name = "InkTeach-Update-Check" };
        th.Start();
    }

    /// <summary>
    /// 主循环里的静默更新检查。自检 / `--updatecheck` 不跑；产品里启动 8 秒后
    /// 查一次，之后每 6 小时一次。没有网络或没有配置源时什么都不显示。
    /// </summary>
    private void MaybeAutoCheckUpdate()
    {
        if (SelfCheckMode || AutoCheckOnly || !UpdateFeed.HasAnySource) return;
        if (_nextUpdateCheckAtMs < 0)
        {
            _nextUpdateCheckAtMs = NowMs + UpdateCheckFirstDelayMs;
            return;
        }
        if (NowMs < _nextUpdateCheckAtMs) return;
        if (UpdateState is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready)
        {
            _nextUpdateCheckAtMs = NowMs + UpdateCheckRepeatMs;   // 已经知道了，不重复查
            return;
        }
        if (_updBusy)
        {
            _nextUpdateCheckAtMs = NowMs + 60_000;   // 正忙：一分钟后再看
            return;
        }
        _nextUpdateCheckAtMs = NowMs + UpdateCheckRepeatMs;
        _autoUpdateCheck = true;
        CheckUpdateFromUi();
    }

    /// <summary>
    /// 已经查到新版本了，再点一下：**下载 → 校验 → 换壳重启**。
    /// 下载按候选列表逐条试（同版本的国内加速站在前）：失败或卡死就换下一条，
    /// 全部试完还不行才报失败（2026-10-02；以前只试一条，失败要用户重点一次）。
    /// </summary>
    internal void ApplyUpdateFromUi()
    {
        if (_updBusy || UpdateState != UpdateStage.Available || _updDownloads.Count == 0) return;

        UpdateState = UpdateStage.Downloading;
        UpdateProgress = 0f;
        UpdateText = "下载中 0%";
        NotifyUiStateChanged();

        string sha = _updSha, ver = _updVersion;
        var urls = _updDownloads;
        Console.WriteLine($"自动更新：开始下载 {ver}（{urls.Count} 条候选源，逐条试）");
        _updBusy = true;
        var th = new System.Threading.Thread(() =>
        {
            string dir = UpdateFeed.UpdateDirFor(ver);
            string zip = Path.Combine(dir, $"InkTeach-{UpdateFeed.SafeVer(ver)}-win-x64.zip");
            bool ok = false;
            string err = "没有可用的下载地址";
            foreach (var (url, useProxy) in urls)
            {
                System.Threading.Interlocked.Exchange(ref _updGot, 0);
                System.Threading.Interlocked.Exchange(ref _updTotal, 0);
                Console.WriteLine($"自动更新：试 {UpdateFeed.HostOf(url)}");
                ok = UpdateFeed.Download(url, zip, sha,
                    (got, total) =>
                    {
                        System.Threading.Interlocked.Exchange(ref _updGot, got);
                        System.Threading.Interlocked.Exchange(ref _updTotal, total);
                    }, out err, useProxy);
                if (ok) break;
                Console.WriteLine("自动更新：这条源没成（" + err + "），换下一条");
            }
            lock (_updLock)
            {
                _updZipPath = ok ? zip : "";
                _updError = err;
            }
            _updApplyPosted = true;
        })
        { IsBackground = true, Name = "InkTeach-Update-Download" };
        th.Start();
    }

    /// <summary>主循环每帧叫一次：把后台结果搬成状态；下完了就拉换壳脚本并退出自己。**只在主线程跑。**</summary>
    private void PumpUpdate()
    {
        if (UpdateState == UpdateStage.Downloading)
        {
            long got = System.Threading.Interlocked.Read(ref _updGot);
            long total = System.Threading.Interlocked.Read(ref _updTotal);
            float p = total > 0 ? Math.Clamp(got / (float)total, 0f, 1f) : 0f;
            if (p - UpdateProgress > 0.01f)
            {
                UpdateProgress = p;
                UpdateText = $"下载中 {p * 100:F0}%";
                NotifyUiStateChanged();
            }
        }

        if (_updPost)
        {
            _updPost = false;
            _updBusy = false;
            UpdateFeed.Manifest m;
            string err;
            List<(string Url, bool UseProxy)> dl;
            lock (_updLock) (m, err, dl) = _updResult;

            bool silent = _autoUpdateCheck;
            _autoUpdateCheck = false;
            bool needApply = false;
            if (m == null)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "检查失败，再点重试";
                Console.WriteLine("自动更新：检查失败：" + err);
            }
            else if (UpdateFeed.CompareVersions(m.Version, UpdateFeed.CurrentVersion) <= 0)
            {
                UpdateState = UpdateStage.UpToDate;
                UpdateText = "已是最新";
                Console.WriteLine($"自动更新：已是最新（{UpdateFeed.CurrentVersion}；源 {UpdateFeed.HostOf(_updUsedUrl)}）");
            }
            else if (m.Url.Length == 0 || m.Sha256.Length == 0)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "清单不完整";
                Console.WriteLine($"自动更新：清单里 {m.Version} 缺 url 或 sha256，拒绝");
            }
            else
            {
                _updVersion = m.Version;
                _updNotes = m.Notes;
                _updDownloads = dl ?? new List<(string Url, bool UseProxy)>();
                _updSha = m.Sha256;
                UpdateState = UpdateStage.Available;
                UpdateText = $"有新版本 {m.Version}";
                Console.WriteLine($"自动更新：发现 {m.Version}（当前 {UpdateFeed.CurrentVersion}；"
                                  + $"源 {UpdateFeed.HostOf(_updUsedUrl)}）"
                                  + (m.Notes.Length > 0 ? "：" + Shorten(m.Notes) : ""));
                needApply = AutoApplyUpdate;
            }

            // 静默检查只负责把「有新版本」变成红点；已是最新 / 失败不占状态行。
            if (silent && UpdateState != UpdateStage.Available)
            {
                UpdateState = UpdateStage.Idle;
                UpdateText = "";
            }
            NotifyUiStateChanged();

            // `--updatecheck`（验收用）：**查完就退**，别把界面挂在屏幕上。
            // 产品里没有这个开关（用户点一下查、再点一下装，界面当然要留着）。
            if (AutoCheckOnly) { _quit = true; return; }
            if (needApply) ApplyUpdateFromUi();
        }

        if (_updApplyPosted)
        {
            _updApplyPosted = false;
            _updBusy = false;
            string zip, err;
            lock (_updLock) { zip = _updZipPath; err = _updError; }

            if (zip.Length == 0)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "下载失败，再点重试";
                Console.WriteLine("自动更新：下载失败：" + err + "（软件保持原样）");
                NotifyUiStateChanged();
                return;
            }

            // 换壳：脚本**等我们退出之后**才动文件（见 UpdateFeed.LaunchSwap）。
            // 拉不起来就留在原地——"更新没装成、软件也没了"是最坏的结果。
            string exe = Environment.ProcessPath;
            string appDir = Path.GetDirectoryName(exe);
            try
            {
                string script = UpdateFeed.WriteSwapScript(Path.GetDirectoryName(zip));
                if (UpdateFeed.LaunchSwap(script, appDir, zip, exe, out string lerr))
                {
                    UpdateState = UpdateStage.Ready;
                    UpdateText = "正在重启…";
                    Console.WriteLine($"自动更新：{_updVersion} 已下载并校验，换壳脚本已拉起，本进程退出");
                    NotifyUiStateChanged();
                    _quit = true;                       // 退出 → 脚本接手：改名旧目录、解压、重启
                }
                else
                {
                    UpdateState = UpdateStage.Failed;
                    UpdateText = "换壳失败（保持原样）";
                    Console.WriteLine("自动更新：换壳启动失败：" + lerr);
                    NotifyUiStateChanged();
                }
            }
            catch (Exception ex)
            {
                UpdateState = UpdateStage.Failed;
                UpdateText = "换壳失败（保持原样）";
                Console.WriteLine("自动更新：换壳失败：" + ex.Message);
                NotifyUiStateChanged();
            }
        }
    }

    /// <summary>状态文字要能塞进面板那一行，长的截掉（换行会让行高乱掉）。</summary>
    private static string Shorten(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= 60 ? s : s[..60] + "…";
    }

    internal void RestartFromUi()
    {
        FlushSettings();             // 语言/界面偏好等先落盘，再起新进程
        Recovery.DiscardSession();   // 保证新进程是空白：不写，而且删掉上次的残留
        AutoSaveNow();               // 盘上留一份（不受"下次打开要不要接上"那个偏好影响）

        if (!RestartSelf("界面上的重启"))
        {
            Console.WriteLine("重启没成功，继续用（板书还在）");
            return;
        }
        RestartRequested = true;
        _quit = true;
    }

    /// <summary>
    /// 切换语言后重启：和“重启软件”不同，**当前板书要接回来**。
    /// 先落设置，再把板书写进会话暂存，新进程启动时由 RestoreSessionIfAny 读回。
    /// </summary>
    internal void RestartForLanguageFromUi()
    {
        FlushSettings();
        // **空板不存**：以前无条件写一份空会话，新进程 RestoreSessionIfAny 会把 BoardOn
        // 打开，用户就"换个语言怎么冒出一块白板"。桌面批注本来就不在 BoardForSave 里。
        var board = BoardForSave();
        try
        {
            if (board.Count > 0) Recovery.SaveSession(InkSerializer.SaveStrokes(board));
            else Recovery.DiscardSession();
        }
        catch (Exception ex) { Console.WriteLine("切换语言：板书暂存失败（继续重启）：" + ex.Message); }

        if (!RestartSelf("切换语言"))
        {
            Recovery.DiscardSession();      // 没拉起来：别把暂存留到下次启动
            Console.WriteLine("切换语言：重启没成功，继续用当前语言");
            return;
        }
        RestartRequested = true;
        _quit = true;
    }

    /// <summary>
    /// 穿透开关（用户入口：主条那格 / 全局热键）。
    ///
    /// <paramref name="force"/> = **内部强制路径**（课堂计时 / 点名：开窗就把指针让给下层）。
    /// 那条路在文档模式里也照旧；**用户入口**被挡（见 <see cref="BlockUserPassThroughInDoc"/>）。
    /// </summary>
    internal void SetPassThroughFromUi(bool on, bool toPen = false, bool force = false)
    {
        // 文档模式 = 像放映（2026-11-15 用户定）：**用户不开放穿透**。
        // 文档页在穿透下是整层让开的，老师看到的会是"PDF 突然没了"；
        // 要穿透就先退出文档（页条菜单「退出文档」）。
        if (on && !force && BlockUserPassThroughInDoc()) return;
        // 开穿透 = 键盘/点击归下层：回放中开它就点不到控制条了，先收掉回放。
        if (on) ExitReplayForEdit("开穿透");
        // 界面那格只发"切换"（`!st.PassThrough`），所以这就是"穿透开关"这条路：
        // 退出时恢复进穿透前的板态（用户 2026-09-30 拍板）。
        // `toPen` 只由**用户主动退出**的路径传（面板穿透格 / 全局热键）：退回来直接是画笔。
        SetPassThrough(on, restoreBoard: true, toPen: toPen);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 用户入口这一刻能不能进穿透：**文档模式不行**（2026-11-15 定，PDF 像放映）。
    /// 主条那一格和全局热键走同一条判据（界面那边也按 DocOpen 先分派到手，这里是兜底）。
    /// 返回 true = 挡住了，原因已经写进状态行。
    /// </summary>
    private bool BlockUserPassThroughInDoc()
    {
        if (!DocView.IsOpen) return false;
        SetInkStatus("文档模式下请先退出文档，再穿透");
        Console.WriteLine("[穿透] 文档模式里不开放穿透（先退出文档）");
        NotifyUiStateChanged();
        return true;
    }

    /// <summary>
    /// 设置页“开机自动启动”。只改当前用户的 Run 值；成功与否都以重新读到的
    /// <see cref="AutoStartOn"/> 为准，界面不会出现“开关开着、实际没登记”。
    /// </summary>
    internal void SetAutoStartFromUi(bool on)
    {
        bool ok;
        string err;
        if (on)
        {
            ok = AutoStart.TryEnable(Environment.ProcessPath, out err);
        }
        else
        {
            ok = AutoStart.TryDisable(out err);
        }

        AutoStartOn = AutoStart.IsEnabled();
        if (ok && AutoStartOn == on)
        {
            // 记下“用户想要什么”，供升级/安装器清掉 Run 后下一次启动补写。
            // 任务管理器禁用不走这里：Run 还在，HasEntry 为真，启动时不会擅自打开。
            SetUiPref("autostart", on ? "1" : null);
            FlushSettings();
        }
        if (!ok)
            Console.WriteLine($"开机自启：{(on ? "打开" : "关闭")}失败：" + err);
        else
            Console.WriteLine($"开机自启：已{(on ? "打开" : "关闭")}（当前 {(AutoStartOn ? "生效" : "未生效")}）");
        NotifyUiStateChanged();
    }

    /// <summary>设置页打开时重读一次；任务管理器可能在运行期间改过。</summary>
    internal void RefreshAutoStartFromUi()
    {
        bool now = AutoStart.IsEnabled();
        // 用户可能在任务管理器里禁用了：清掉“想要自启”的旧意愿，
        // 以后安装器清掉 Run 时不要擅自再打开。
        if (!now && AutoStart.HasEntry() && GetUiPref("autostart") == "1")
        {
            SetUiPref("autostart", null);
            FlushSettings();
        }
        if (now == AutoStartOn) return;
        AutoStartOn = now;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 第二个进程发来的“用户又启动了 InkTeach”：把后台待命态叫醒。
    /// 单实例互斥体挡住了第二个进程，但它不能只会 SetForegroundWindow——
    /// 穿透 + 收起的小球被提到前面仍然点不到桌面上的东西，看起来就像没启动。
    /// </summary>
    internal void ActivateFromExternal()
    {
        Console.WriteLine("启动：收到第二个实例的激活请求");
        if (BarSuppressedNow) BarTempShown = true;
        if (PassThrough) SetPassThroughFromUi(false, toPen: true);
        if (Ui != null) UiGuard("Activate", () => Ui.Activate());
        NotifyUiStateChanged();
    }

    internal void ToggleBarFromTray()
    {
        BarTempShown = !BarTempShown;
        NotifyUiStateChanged();
        if (BarTempShown)
        {
            if (Ui != null) UiGuard("Activate", () => Ui.Activate());
        }
        else if (BarSuppressedNow && !PassThrough && !BoardOn && !DocView.IsOpen)
        {
            SetPassThrough(true);
        }
        _dirty = true;
    }

    internal void ExitPptOnlyFromTray()
    {
        SetBarIdleModeFromUi(BarIdleMode.Off);
        if (Ui != null) UiGuard("Activate", () => Ui.Activate());
    }

    internal void ShowSettingsFromTray()
    {
        if (Ui != null) UiGuard("ShowSettings", () => Ui.ShowSettings());
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>进白板前待在哪一页；关白板回哪（默认 0 = 桌面）。</summary>
    private int _boardReturnKey = 0;

    /// <summary>
    /// 打开白板前冻结当前文档页：白板是独立画布，但半透明时露出的 PDF/图片页
    /// 必须停在**打开那一刻**的那一页，不能跟着白板相机回到第 1 页。
    /// </summary>
    private void CaptureBoardFreeze()
    {
        ClearBoardFreeze();
        if (!DocView.IsOpen) return;

        var vp = ViewportCanvas;
        int idx = DocView.CurrentIndex(vp.MinY, vp.MaxY);
        if (idx < 0) idx = DocView.FirstAtOrAfter(vp.MinY);
        if (idx < 0 || idx >= DocView.Count) return;

        BoardFrozenDocPage = idx;
        BoardFrozenDocViewOffsetY = ViewOffsetY;
        DocView.PinPage(idx);
    }

    private void ClearBoardFreeze()
    {
        BoardFrozenDocPage = -1;
        BoardFrozenDocViewOffsetY = 0f;
        DocView.UnpinPage();
    }

    /// <summary>
    /// 开关白板。**2026-10-16 起白板是独立槽**（用户定）：开 = 切到白板槽
    /// （桌面 / 文档 / PPT 的墨迹都不跟着来），关 = 切回进来之前待的那一页。
    /// 底色 / 底纹跟着 <see cref="BoardOn"/> 走，切槽必然整层重画（缓存是按旧底色画的）。
    ///
    /// 与穿透的关系照旧：开白板顺手退穿透（另一边在 <see cref="SetPassThrough"/> 里，
    /// 两个方向都挡）；**穿透本身不切槽**——它只是把白板层临时让开，退穿透原样恢复。
    /// </summary>
    internal void SetBoardFromUi(bool on)
    {
        // 已经是目标状态就没有事做（"开且人就在白板槽" / "关且人不在白板槽"）。
        if (on == BoardOn
            && (on ? Doc.PageKey == InkDocument.BoardPageKey
                   : Doc.PageKey != InkDocument.BoardPageKey))
            return;

        // 切槽 = 整页内容换了：回放是"当前这一页的只读重演"，先收掉（同 Ppt.GotoPage 的"翻页"）。
        ExitReplayForEdit("切白板");

        // 白板菜单和文档/PPT 菜单共用页码条；无论开还是关，都先把上一套
        // 菜单/页号面板/等确认收掉，避免切换后留下“看不见但吃输入”的层。
        PptMenuOpen = false;
        PptPagePanelOpen = false;
        PptZoomMenuOpen = false;
        PptClearConfirm = false;
        PptBarHover = -1;
        CancelPptPress();

        if (on)
        {
            BarTempShown = false;
            SetPassThrough(false);          // 开白板顺手关穿透（原样）
            CaptureBoardFreeze();           // 先记下 PDF/图片停在哪一页（切槽前！）
            int from = Doc.PageKey;
            if (from != InkDocument.BoardPageKey) _boardReturnKey = from;
            if (Doc.SwitchPage(InkDocument.BoardPageKey))
            {
                // 相机"每页一个数"（同 Ppt.GotoPage）：切回来还停在原来看的位置。
                _pageScroll[from] = ViewOffsetY;
                ViewOffsetY = _pageScroll.TryGetValue(InkDocument.BoardPageKey, out var v) ? v : 0f;
                ClampViewOffset();
                _camAnimating = false;
            }
            BoardOn = true;
        }
        else
        {
            LeaveBoardSlot();
            BoardOn = false;
            if (BarSuppressedNow && !PassThrough && !DocView.IsOpen)
                SetPassThrough(true);
        }
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 相机**直接跳**到某个纵向偏移（不做翻页动画）。
    /// 「贴到白板」翻到新页用它：新贴上来的题目必须立刻在本页顶部，不能跟着 167ms 动画晃。
    /// 目标偏移与 <see cref="FlipPage"/> 白板那条完全一致（`_virtualY - (页-1) × 页高 × 缩放`）。
    /// </summary>
    internal void JumpCameraTo(float y)
    {
        ViewOffsetY = ClampOffset(y);
        _camAnimating = false;
    }

    /// <summary>停掉正在跑的翻页动画（内容原地不动，只是别让相机继续滑）。</summary>
    internal void StopCameraAnimation() => _camAnimating = false;

    /// <summary>
    /// 从白板槽退回来处（<see cref="_boardReturnKey"/>），相机按"每页一个数"切
    /// （和 <see cref="SetBoardFromUi"/> 关板那条路共用同一份逻辑）。两个调用点：
    /// 白板关（SetBoardFromUi）、"退穿透但板不恢复"（<see cref="SetPassThrough"/>）——
    /// 两条路都必须让"人不在板槽"与"板没开"同时成立，别留下"板槽没板"的混合态
    /// （板槽的墨会浮在桌面上；2026-10-16 用户报的就是它）。
    /// </summary>
    private void LeaveBoardSlot()
    {
        ClearBoardFreeze();
        int from = Doc.PageKey;
        if (Doc.SwitchPage(_boardReturnKey))
        {
            _pageScroll[from] = ViewOffsetY;
            ViewOffsetY = _pageScroll.TryGetValue(_boardReturnKey, out var v) ? v : 0f;
            ClampViewOffset();
            _camAnimating = false;
        }
    }

    /// <summary>
    /// 装界面，**并留下"再造一个"的办法**。产品代码用这一条，不要用 <see cref="SetUi"/>：
    /// 界面出问题时引擎要能自己把界面重建回来（教室大屏很可能没有键盘，
    /// 界面是唯一的出口，见 <see cref="Recovery"/> 的注释）。
    ///
    /// 传进来的工厂必须每次都能造一个**全新的**界面（不要复用同一个实例——
    /// 那个实例正是刚被判定"状态坏了"的那一个）。
    /// </summary>
    public void SetUiFactory(Func<IOverlayUi> factory)
    {
        _uiFactory = factory;
        SetUi(factory != null ? factory() : null);
    }

    /// <summary>界面切选择方式（矩形框 / 自由套索）。</summary>
    internal void SetSelectModeFromUi(SelectMode mode)
    {
        if (SelMode == mode) return;
        SetSelectMode(mode);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 界面换板色。底色一变整个内容层都要重画——缓存里那张图是按旧底色画的
    /// （和 <see cref="SetBoardFromUi"/> 同理）。
    /// </summary>
    internal void SetBoardColorFromUi(Color4 color)
    {
        if (BoardColor.Equals(color)) return;
        BoardColor = color;
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>界面上的"全选"。<see cref="SelectAll"/> 自己会把工具切成框选，免得用户以为没生效。</summary>
    internal void SelectAllFromUi()
    {
        ExitReplayForEdit("全选");
        SelectAll();
        _dirty = true;
    }

    /// <summary>界面上的"上一屏 / 下一屏"（整屏翻页）。</summary>
    internal void FlipPageFromUi(bool down)
    {
        ExitReplayForEdit("翻页");
        if (!FlipPage(down)) return;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 指针按下的第一站：先问界面。返回 true 表示这次输入归界面（比如按到了
    /// 悬浮条上的按钮），引擎不再把它变成笔画。
    /// </summary>
    private bool UiPointerDown(float x, float y, float pressure, bool fromPen, bool eraserTip,
                               bool fromTouch = false, uint pointerId = 0)
    {
        // 截图 / 吸管 / 收藏落点期间界面是**藏起来的**（PrepareUi 直接返回 false）。
        // 引擎仍会把每一次按下转给界面；界面如果还按自己的几何去吃点击，
        // 就会把截图确认键（正好压在工具条那一块）吃掉——只能按 Enter。
        if (!UiVisibleNow || CaptureActive || ColorPickActive || LibraryPlacementMode) return false;
        var e = new UiPointerEvent(x / UiUnit, y / UiUnit, pressure, fromPen, eraserTip,
                                   fromTouch, pointerId);
        if (!UiGuard("PointerDown", () => Ui.PointerDown(e), false)) return false;
        _uiPointers.Add(pointerId);
        UiCapturing = true;
        return true;
    }

    private bool UiPointerMove(float x, float y, float pressure, bool fromPen, bool eraserTip,
                               bool fromTouch = false, uint pointerId = 0)
    {
        if (!UiVisibleNow || CaptureActive || ColorPickActive || LibraryPlacementMode) return false;

        // 只有在界面已经捕获输入或指针落在界面矩形内时才转发，避免没必要的调用。
        if (!UiCapturing && !_uiPointers.Contains(pointerId) && !UiContains(x, y)) return false;
        var e = new UiPointerEvent(x / UiUnit, y / UiUnit, pressure, fromPen, eraserTip,
                                   fromTouch, pointerId);
        return UiGuard("PointerMove", () => Ui.PointerMove(e), false);
    }

    private bool UiPointerUp(float x, float y, float pressure, bool fromPen, bool eraserTip,
                             bool fromTouch = false, uint pointerId = 0)
    {
        if (CaptureActive || ColorPickActive || LibraryPlacementMode)
        {
            _uiPointers.Clear();
            UiCapturing = false;
            return false;
        }
        if (!UiVisibleNow)
        {
            _uiPointers.Clear();
            UiCapturing = false;
            return false;
        }
        // 触摸双指：第一根抬手后不能把另一根也丢掉；按“这一次按下的指针”记账。
        if (!UiCapturing && !_uiPointers.Contains(pointerId)) return false;
        var e = new UiPointerEvent(x / UiUnit, y / UiUnit, pressure, fromPen, eraserTip,
                                   fromTouch, pointerId);
        bool consumed = UiGuard("PointerUp", () => Ui.PointerUp(e), false);
        _uiPointers.Remove(pointerId);
        UiCapturing = _uiPointers.Count > 0;
        // **手指 / 笔抬手之后不会再有"指针离开"消息**（2026-11-25 用户报：希沃触屏上贴边隐藏不生效）。
        //
        // 鼠标离开面板时，接输入小窗会收到 WM_POINTERLEAVE 并转成 Ui.PointerLeave()；
        // 手指和笔没有"悬停"这一层——抬手就是最后一条消息。界面靠"指针还按在面板上"
        // 决定贴边隐藏收不收，不清掉的话它永远是"在"，面板拖到屏幕底边也永远不收回。
        // 最后一根**界面接管的**指针抬起 = 等效于指针离开面板。
        if (!UiCapturing && (fromTouch || fromPen))
            UiGuard("PointerLeave", () => Ui.PointerLeave());
        return consumed;
    }

    // 自检钩子：直接走真实 UI 包装（含 FromTouch 标志），避免自检绕过它。
    internal bool UiPointerDownForTest(float logicalX, float logicalY, bool fromTouch, uint id)
        => UiPointerDown(logicalX * UiUnit, logicalY * UiUnit, 0.5f, false, false, fromTouch, id);
    internal bool UiPointerMoveForTest(float logicalX, float logicalY, bool fromTouch, uint id)
        => UiPointerMove(logicalX * UiUnit, logicalY * UiUnit, 0.5f, false, false, fromTouch, id);
    internal bool UiPointerUpForTest(float logicalX, float logicalY, bool fromTouch, uint id)
        => UiPointerUp(logicalX * UiUnit, logicalY * UiUnit, 0.5f, false, false, fromTouch, id);

    private bool UiContains(float x, float y)
    {
        if (LibraryPlacementMode) return false;   // 工具栏已让位，整屏归画布
        x /= UiUnit; y /= UiUnit;   // 物理 → 界面逻辑（DPI × 界面缩放）
        foreach (var w in _windows)
            if (w.UiBoundsLogicalContains(x, y)) return true;
        return false;
    }

    /// <summary>
    /// 取 WM_NCHITTEST 的 lParam 里的屏幕坐标。
    ///
    /// 它是两个**有符号** 16 位拼起来的**物理**像素坐标。必须按有符号取：
    /// 副屏在主屏左边/上边时坐标是负的，按无符号取会得到 6 万多，
    /// 贴边、命中测试全错——而单屏 + 正坐标的机器上完全看不出来。
    /// </summary>
    private static void HitTestPoint(IntPtr lParam, out float x, out float y)
    {
        long v = lParam.ToInt64();
        x = (short)(v & 0xFFFF);
        y = (short)((v >> 16) & 0xFFFF);
    }

    // ---- 面板的"接输入小窗"（方案 B）--------------------------------------

    // ---- 界面回调的统一入口：**都是防弹的** ------------------------------
    //
    // 为什么值得包这一层：界面里的一个 bug 不该让"板书"这个主功能跟着不可用。
    // 以前的保护只有绘制那一段（`Overlay.DrawUi` 里的 try/catch 打印），
    // 而 `PointerDown/Move/Up`、`OnStateChanged`、`QueryBounds`、`IsAnimating`
    // 全是裸调——界面一抛，异常就冒到消息循环里，老师当场什么都画不出来。
    //
    // 规矩：**3 秒内 3 次**异常就把界面停用（换成 NullUi，并藏掉接输入小窗），
    // 笔迹照常。
    //
    // 为什么是"一个时间窗内 3 次"而不是"连续 3 次调用"：界面每帧都会被调好几次
    // （Visible / QueryBounds / Render / PointerMove…），只要别处的调用成功就把
    // 计数清零的话，一条只在 PointerDown 里踩的 bug 永远攒不到 3 次。
    // 反过来"一次就停用"又太狠——偶发一次不该让工具条消失一整节课。
    private int _uiFaults;
    private double _uiLastFaultMs = double.NegativeInfinity;

    /// <summary>界面工厂：有它引擎才能"自己把界面重建一个回来"。</summary>
    private Func<IOverlayUi> _uiFactory;

    /// <summary>最近一分钟里重建过几次界面。重建也失败就得往上走一级（重启软件）。</summary>
    private int _uiRebuilds;
    private double _uiRebuildWindowStartMs = double.NegativeInfinity;

    /// <summary>自检用：这一次"重启软件"有没有真的被发起。</summary>
    internal bool RestartRequested { get; private set; }

    /// <summary>重启计数清过一次就够了（跑满一分钟算"稳住了"）。</summary>
    private bool _restartCountCleared;

    /// <summary>
    /// 界面的异常次数与处置。**三级阶梯**，一级比一级重，但都不留死局：
    ///
    ///   ① 重建界面（引擎自己 new 一个回来）—— 板书不动，工具条重新挂一遍；
    ///   ② 重启软件 —— 重启前把板书暂存下来，重启后读回来，**什么都不丢**；
    ///   ③ 都不行就退回无界面 —— 至少还能写，而且是必现 bug 时的唯一出路
    ///      （否则就是无限重启，屏幕一直在闪，比停用更糟）。
    ///
    /// 为什么必须往"重启"走：教室的大屏 + 手写板机器上很可能没有键盘，
    /// 界面就是唯一的出口；界面没了又没法重启，就是死局（用户 2026-09-16 提的）。
    /// </summary>
    private void NoteUiFault(string what, Exception ex)
    {
        if (NowMs - _uiLastFaultMs > 3000) _uiFaults = 0;   // 隔久了当新的一轮
        _uiLastFaultMs = NowMs;
        _uiFaults++;
        if (_uiFaults < 3)
        {
            Console.WriteLine($"界面异常（{what}，第 {_uiFaults} 次）：{ex.Message}");
            return;
        }

        Console.WriteLine($"界面 3 秒内抛了 {_uiFaults} 次异常（最后一次在 {what}）：{ex.Message}");

        // ① 还能重建就先重建：不丢板书，通常也够用（界面多半是状态被搞坏了）。
        if (_uiFactory != null && UiRebuildAllowed())
        {
            RebuildUi(what);
            return;
        }

        // ② 重建也救不回来 → 重启软件（先把板书存下来）。
        if (TryRestartForUiProblem(what)) return;

        // ③ 最后的兜底：退回无界面。必现的 bug 走到这里，至少还能写。
        DisableUi(what);
    }

    private void RebuildUi(string what)
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) { _uiRebuilds = 0; _uiRebuildWindowStartMs = NowMs; }
        _uiRebuilds++;
        _uiFaults = 0;

        Console.WriteLine($"  → 重建界面（这个界面是第 {_uiRebuilds} 次重建）—— 板书不受影响");
        try
        {
            SetUi(_uiFactory());
        }
        catch (Exception ex)
        {
            Console.WriteLine("界面重建失败：" + ex.Message);
            SetUi(null);
        }
    }

    /// <summary>一分钟内最多重建两次；再多说明重建不管用，该往上走一级。</summary>
    private bool UiRebuildAllowed()
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) return true;
        return _uiRebuilds < 2;
    }

    /// <summary>
    /// 因为界面出问题而重启软件。返回 true = 已经开始重启（调用方别再往下走）。
    /// **先存板书再重启**：重启的前提是不丢东西。
    /// </summary>
    private bool TryRestartForUiProblem(string what)
    {
        int n = Recovery.NoteRestart();
        if (n > Recovery.MaxRestartsInWindow)
        {
            Console.WriteLine($"  → 这个窗口内已经重启过 {n - 1} 次，不再重启（多半是必现的问题）");
            return false;
        }

        try { Recovery.SaveSession(InkSerializer.SaveStrokes(BoardForSave())); } catch (Exception ex) { Console.WriteLine("板书暂存失败：" + ex.Message); }
        Console.WriteLine($"  → 重启软件（第 {n} 次），板书已暂存，重启后自动读回来");

        if (!RestartSelf(what)) return false;
        RestartRequested = true;
        _quit = true;           // 主循环退出 → Shutdown → 进程结束，新进程接手
        return true;
    }

    /// <summary>
    /// 真的把软件重新拉起来。**宿主可以覆写**（自检里只记一笔，不真拉进程）。
    /// 返回 false = 没拉起来，那就别退出——退回无界面继续跑总比"退出后什么都没有"强。
    /// </summary>
    protected virtual bool RestartSelf(string why)
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            var args = Environment.GetCommandLineArgs();
            // 重启后应该回到“用户正在用”的形态，而不是因为原始命令行带着
            // --startup 又变回后台待命。只有当前确实还是穿透待命时才保留它。
            bool keepStartup = StartupMode && PassThrough;
            for (int i = 1; i < args.Length; i++)
                if (args[i] != "--startup") psi.ArgumentList.Add(args[i]);
            if (keepStartup) psi.ArgumentList.Add("--startup");
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("拉起新进程失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>三级都走不通时的兜底：停用界面，笔迹照常。</summary>
    private void DisableUi(string what)
    {
        Console.WriteLine($"  → 界面已停用（{what}）。笔迹不受影响；修好之后重开软件就回来了。");
        Ui = new NullUi();
        _uiPointers.Clear();
        UiCapturing = false;
        Native.ReleaseCapture();
        _uiHover = false;
        if (_uiInputHwnd != IntPtr.Zero && _uiInputShown)
        {
            Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
            _uiInputShown = false;
        }
        _dirty = true;
        // 最后的保险：UI 彻底停用时强制交还鼠标穿透，防止用户被困在无工具栏的画笔态。
        if (!PassThrough) SetPassThrough(true);
    }

    private T UiGuard<T>(string what, Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception ex) { NoteUiFault(what, ex); return fallback; }
    }

    private void UiGuard(string what, Action call)
    {
        try { call(); }
        catch (Exception ex) { NoteUiFault(what, ex); }
    }

    internal bool UiVisibleNow => Ui != null && UiGuard("Visible", () => Ui.Visible, false);
    internal RectF UiQueryBoundsNow() =>
        Ui == null ? RectF.Empty : UiGuard("QueryBounds", () => Ui.QueryBounds(), RectF.Empty);
    /// <summary>主条本身（不含分类下拉/动作菜单这类弹层）。收藏面板定位只认它。</summary>
    internal RectF UiChromeBoundsNow() =>
        Ui == null ? RectF.Empty : UiGuard("ChromeBounds", () => Ui.ChromeBounds, RectF.Empty);
    /// <summary>主条那一条（不含上带）。收藏弹窗默认位置用它的上沿。</summary>
    internal RectF UiBarBoundsNow() =>
        Ui == null ? RectF.Empty : UiGuard("BarBounds", () => Ui.BarBounds, RectF.Empty);

    /// <summary>
    /// 这一点是不是落在"界面临时弹层"（分类下拉 / 动作菜单 / 调色盘）里。
    /// 这类弹层可能压在收藏面板上；它归界面，引擎不能先吃掉（否则看得见点不中）。
    /// </summary>
    private bool InUiPopup(float x, float y)
    {
        if (LibraryPlacementMode) return false;   // 工具栏已让位
        var q = UiQueryBoundsNow();
        var c = UiChromeBoundsNow();
        if (q.IsEmpty) return false;
        float dpi = UiUnit;
        bool inQ = x >= q.MinX * dpi && x <= q.MaxX * dpi
                   && y >= q.MinY * dpi && y <= q.MaxY * dpi;
        bool inC = x >= c.MinX * dpi && x <= c.MaxX * dpi
                   && y >= c.MinY * dpi && y <= c.MaxY * dpi;
        return inQ && !inC;
    }
    /// <summary>界面声明"我会画到占用矩形外面一圈"（投影/浮出预览），见 IOverlayUi.PaintMargin。</summary>
    internal float UiPaintMarginNow =>
        Ui == null ? 0f : UiGuard("PaintMargin", () => Ui.PaintMargin, 0f);
    internal bool UiIsAnimatingNow => Ui != null && UiGuard("IsAnimating", () => Ui.IsAnimating, false);
    internal void UiPrepareFrameNow() =>
        UiGuard("PrepareFrame", () => { if (Ui != null) Ui.PrepareFrame(); });
    internal RectF UiLayoutNow(RectF screen, float dpiScale) =>
        Ui == null ? RectF.Empty : UiGuard("Layout", () => Ui.Layout(screen, dpiScale), RectF.Empty);
    internal void UiRenderNow(ID2D1DeviceContext ctx, UiTheme theme) =>
        UiGuard("Render", () => Ui.Render(ctx, theme));

    /// <summary>
    /// 每次渲染前调用一次：让接输入小窗跟着界面这一刻占的地方走。
    /// 界面隐藏或没占地方时把它藏起来——藏起来就等于"不存在于输入里"。
    /// </summary>
    /// <summary>
    /// 穿透模式下指针离开小窗时，若 WM_POINTERLEAVE 被希沃白板或其它置顶窗口吃掉，
    /// `_uiHover` 会永久卡在 true、贴边隐藏再也不收。按光标实际位置兜底清一次。
    /// </summary>
    private void ClearStalePassThroughUiHover()
    {
        if (PassThrough && _uiHover && !UiCapturing && Ui != null
            && Native.GetCursorPos(out var pt) && !UiContains(pt.X, pt.Y))
        {
            _uiHover = false;
            UiGuard("PointerLeave", () => Ui.PointerLeave());
            _dirty = true;
        }
    }

    private void UpdateUiInputWindow()
    {
        ClearStalePassThroughUiHover();
        // 截图 / 吸管期间界面是收起的（见 PrepareUi 开头那段）：接输入小窗也一起收——
        // 不然"看不见的工具条"那一条还会把点击吃掉（截图拖框、吸管取色正好要用那一片）。
        bool uiActive = UiVisibleNow && !CaptureActive && !ColorPickActive && !LibraryPlacementMode;
        // **先把界面自己的状态推进一帧，再问它占哪儿**：贴边隐藏、色带张开、提示计时
        // 都会改位置/占位。顺序反了就是"按新位置画、按旧位置擦"——见 IOverlayUi.PrepareFrame。
        // 放在这里（而不是 Render 里）还保证接输入小窗和这一帧画出来的界面同源。
        if (uiActive) UiPrepareFrameNow();
        RectF logical = uiActive ? UiQueryBoundsNow() : RectF.Empty;

        if (logical.IsEmpty)
        {
            // **不看 `_uiInputShown`**：HiddenOverlay 恢复窗口时不会同步这个标志，
            // 可能出现"窗口实际显示、标志是 false"的错位；那条透明的接输入小窗
            // 会压在截图确认键/工具条上，点击全被它吃掉。这里无条件藏一次。
            if (_uiInputHwnd != IntPtr.Zero)
            {
                Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
                _uiInputShown = false;
                _uiHover = false;
            }
            return;
        }

        EnsureUiInputWindow();
        if (_uiInputHwnd == IntPtr.Zero) return;

        // 逻辑 → 物理只在这里做一次（界面的坐标系永远只有逻辑那一套）。
        var phys = new RectF
        {
            MinX = logical.MinX * UiUnit, MinY = logical.MinY * UiUnit,
            MaxX = logical.MaxX * UiUnit, MaxY = logical.MaxY * UiUnit,
        };

        if (_uiInputShown && phys.Equals(_uiInputRect)) return;

        Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST,
            (int)MathF.Floor(phys.MinX), (int)MathF.Floor(phys.MinY),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxX - phys.MinX)),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxY - phys.MinY)),
            Native.SWP_NOACTIVATE | (_uiInputShown ? 0 : Native.SWP_SHOWWINDOW));

        _uiInputRect = phys;
        _uiInputShown = true;
    }

    private void EnsureUiInputWindow()
    {
        if (_uiInputHwnd != IntPtr.Zero) return;

        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _uiInputHwnd = Native.CreateWindowEx(exStyle, _className, "InkTeachUiInput",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_uiInputHwnd == IntPtr.Zero)
        {
            Console.WriteLine("界面接输入小窗创建失败: " + Marshal.GetLastWin32Error()
                              + "（面板在穿透模式下会点不动）");
            return;
        }

        // 1/255 的不透明度：肉眼看不见，但对命中测试来说它**实实在在地在这**。
        Native.SetLayeredWindowAttributes(_uiInputHwnd, 0, 1, Native.LWA_ALPHA);

        // 这个小窗也算"我们的窗口"：系统可能拿它来问长按手势（消息那条见 WndProc），
        // 窗口属性的那两条要在这里补一次（手势按窗口算，漏一个窗口就漏一块地方）。
        Native.DisableSystemPressAndHold(_uiInputHwnd);
    }

    /// <summary>
    /// 接输入小窗的消息。它不画任何东西，只负责把指针事件翻成界面事件。
    /// 坐标一律是**物理屏幕坐标**——<c>UiPointer*</c> 那三个包装负责除以 DPI，
    /// 跟覆盖层那条路完全同一套口径。
    /// </summary>
    private IntPtr UiInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // 这块就是面板本身，不需要再判断"落在面板里的哪个位置"。
            case Native.WM_NCHITTEST:
                _cntNcHitTest++; _cntNcHitClient++;
                return new IntPtr(Native.HTCLIENT);

            // 点面板不许把下层程序的焦点抢走（老师点一下按钮，PPT 还是前台）。
            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            // 光标：和覆盖层那条路**问同一份判据**（指针在面板上 = 箭头）。
            // 不答这一条的话系统会拿**类光标**兜底（NULL → 默认箭头）：面板上看着没问题，
            // 但"面板 ↔ 画布"来回走时，我们那个"已设过就不重设"的缓存会被这一次
            // 兜底悄悄作废（见 ApplyCursor 的注释）。
            case Native.WM_SETCURSOR:
                ApplyCursor(force: true);
                return new IntPtr(1);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERENTER:
                _uiHover = true; _dirty = true; ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                _uiHover = false; _dirty = true; ApplyCursor();
                // 界面那条"人走了"由这里发：方案 B 下，指针离开面板＝离开这块接输入小窗。
                UiGuard("PointerLeave", () => Ui.PointerLeave());
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                // 截图/吸管/收藏落点期间这个透明小窗不该收点击；万一它还残留着，
                // 把这一下转给主覆盖层（截图确认键正好压在工具条上时，就是这里吃掉的）。
                if (CaptureActive || ColorPickActive || LibraryPlacementMode)
                {
                    Native.ShowWindow(hWnd, Native.SW_HIDE);
                    _uiInputShown = false;
                    if (_windows.Count > 0) OnPointerDown(_windows[0].Hwnd, wParam);
                    return IntPtr.Zero;
                }
                StampInput(); _cntDown++;
                LastPointerType = ptype;
                _uiHover = true;

                if (UiPointerDown(sx, sy, pressure, ptype == Native.PT_PEN, inverted,
                                  ptype == Native.PT_TOUCH, id))
                {
                    // 界面也要捕获：在按钮上滑开、拖出面板，都要继续收到消息。
                    Native.SetCapture(hWnd);
                }
                // 界面没吃这一口也**不落墨、不透给下层**：QueryBounds 声明的就是
                // 界面的地盘（面板底下的墨看不见，将来面板一挪又冒出来）。
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntMove++;
                LastPointerType = ptype;
                _uiHover = true;
                UiPointerMove(sx, sy, pressure, ptype == Native.PT_PEN, inverted,
                              ptype == Native.PT_TOUCH, id);
                _dirty = true;
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                uint ptype = 0;
                if (ReadPointer(id, out float sx, out float sy, out float pressure,
                                out bool inverted, out ptype))
                {
                    StampInput(); _cntUp++;
                    UiPointerUp(sx, sy, pressure, ptype == Native.PT_PEN, inverted,
                                ptype == Native.PT_TOUCH, id);
                }
                if (!UiCapturing)
                {
                    Native.ReleaseCapture();
                    if (ptype == Native.PT_TOUCH)
                    {
                        _uiHover = false;
                        UiGuard("PointerLeave", () => Ui.PointerLeave());
                    }
                }
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                _uiPointers.Clear();
                UiCapturing = false;
                _uiHover = false;
                UiGuard("PointerLeave", () => Ui.PointerLeave());
                _dirty = true;
                return IntPtr.Zero;

            // 接输入小窗覆盖界面时，滚轮消息会先到这里；不转发的话设置页/说明页滚不动。
            case Native.WM_MOUSEWHEEL:
            {
                if (Ui == null) return IntPtr.Zero;
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                Native.GetCursorPos(out var cursor);
                bool consumed = UiGuard("Wheel",
                    () => Ui.Wheel(cursor.X / UiUnit, cursor.Y / UiUnit, delta), false);
                if (consumed) _dirty = true;
                return IntPtr.Zero;
            }
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>控制台用法说明。产品界面不会走这里；**只在 `--help` 时打印**（2026-10-17）。</summary>
    protected virtual void PrintUsageHelp()
    {
        Console.WriteLine();
        Console.WriteLine("InkTeach 参数与快捷键（键位可配置，见 " + InkSettings.FilePath + "）：");
        Console.Write(Keys.ToText());
        if (Keys.GlobalFailures.Count > 0)
        {
            Console.WriteLine("  ！下面这些键没注册上（被别的程序占了？换一个键）：");
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    " + f);
        }
        Console.WriteLine();
    }

}
