// [职责] .inkb 的保存 / 打开：异步读盘与解析（按代作废）、打开前备份与轮转、界面命令接线。
// [测试] --inkfiletest / --doctest

using InkLocalization;

namespace InkEngine;

/// <summary>
/// 墨迹文件（.inkb）的落盘与"打开前备份"（墨迹 A，2026-10-01）。
///
/// 三条纪律（见 计划-更多面板与课堂工具.md 6.4）：
///   ① **写盘原子化**：先写 `.tmp` 再换名（同 Recovery / PptStore）——写一半断电
///      不能留下一个坏文件，那比"这次没存上"更糟；
///   ② **打开前必须留一份当前板书**：这是"文件级保险"，替代"把整份替换塞进撤销栈"
///      那条危险的路（后果见 6.4.1）；
///   ③ 备份**轮转保留最近 5 份**，文件名按时间可排序。
/// </summary>
internal static class InkFileStore
{
    /// <summary>自检用：把备份目录指到临时目录，**别动用户真实的备份**（同 Recovery 套路）。</summary>
    public static string BackupDirOverride;

    public static string BackupDir => BackupDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkTeach", "Backup");

    /// <summary>保留几份"打开前备份"。5 份 ≈ 最近几次打开操作，够找回一次误点。</summary>
    public const int BackupKeep = 5;

    /// <summary>写字节：先写 `.tmp` 再换名（目录不存在就建）。</summary>
    public static void WriteAtomic(string path, byte[] blob)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>把当前板书写成一份"打开前备份"，返回落盘路径；顺手轮转。</summary>
    public static string WriteBackup(byte[] blob)
    {
        Directory.CreateDirectory(BackupDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        string path = Path.Combine(BackupDir, $"板书-打开前-{stamp}.inkb");
        // 同一毫秒内连点两次：加后缀，别互相覆盖（测试里会连写 6 份）。
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(BackupDir, $"板书-打开前-{stamp}-{n}.inkb");
        WriteAtomic(path, blob);
        Rotate();
        return path;
    }

    /// <summary>
    /// 轮转：只留最近 <see cref="BackupKeep"/> 份。前缀固定、时间戳定长，
    /// 所以**按文件名序数排序就是按时间排序**（不依赖文件系统时间，跨机器可复现）。
    /// </summary>
    public static void Rotate()
    {
        try
        {
            var files = Directory.GetFiles(BackupDir, "*.inkb");
            if (files.Length <= BackupKeep) return;
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - BackupKeep; i++)
            {
                try { File.Delete(files[i]); } catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }
            }
        }
        catch (Exception ex) { Console.WriteLine("备份轮转失败（不影响使用）：" + ex.Message); }
    }

    /// <summary>自检用：现在备份目录里有几份。</summary>
    public static int BackupCount()
    {
        try { return Directory.Exists(BackupDir) ? Directory.GetFiles(BackupDir, "*.inkb").Length : 0; }
        catch { return 0; }
    }
}

/// <summary>
/// 一次 **后台"打开墨迹（.inkb）"任务**（2026-11-16）：读盘 + 校验 + 解析全在工人线程，
/// 主线程只负责"采纳结果"。大文件（几十 MB / 上万条笔迹）走这条路，界面不会再假死。
///
/// 纪律（同 <see cref="BackgroundSaver"/>）：
///   · 工人线程**只写自己的字段**，绝不碰 Doc / 界面 / Toast；
///   · <see cref="Done"/> 是唯一的"可以采纳"信号（volatile，最后一步才置）；
///   · 结果按 <see cref="Gen"/> **整代作废**：换了目标，旧任务连采纳的机会都没有。
/// </summary>
internal sealed class InkOpenJob
{
    public string Path = "";

    /// <summary>发起时领的代号；与闸门当前的 <see cref="InkOpenGate.Gen"/> 不等 = 已作废。</summary>
    public int Gen;

    /// <summary>读到的字节数（只用于日志 / 状态行）。</summary>
    public long Bytes;

    /// <summary>工人线程写完最后一步才置 true；主线程（StepInkOpen）见到它才采纳。</summary>
    public volatile bool Done;

    /// <summary>解析出来的对象表（失败时是 null）。</summary>
    public List<Stroke> Strokes;
    public int MaxId;

    /// <summary>失败原因（null = 成功）。</summary>
    public string Error;
}

/// <summary>
/// 「打开墨迹」的**单飞闸门 + 代次**（2026-11-16）。整个引擎只有 <c>_inkOpen</c> 一个字段，
/// 状态都收在这里：同时在跑的只留最后一次，关文档 / 退出时把 <see cref="Gen"/> +1
/// 就让在途结果全部作废（线程不杀，它跑完自己扔掉）。
/// </summary>
internal sealed class InkOpenGate
{
    /// <summary>当前这次（null = 没有在途任务）。只在主线程读写。</summary>
    public InkOpenJob Current;

    /// <summary>代号：每发起一次 / 作废一次都 +1（Interlocked；工人线程也会读）。</summary>
    public int Gen;
}

/// <summary>
/// 「保存墨迹 / 打开墨迹」两条界面命令的实现（墨迹 A）。
///
/// 边界（用户 2026-10-01 拍板，见 6.4.1）：
///   · **只在白板模式开放**；PPT 放映中不响应——`Save(doc)` 只写当前页，
///     手动保存整份 PPT 批注是"批注包导出/导入"（C10）的活；
///   · 打开**不进撤销栈**，但**必须**先写一份"打开前备份"；
///   · 失败一律走 `InkStatus` 状态行 + 控制台，**不弹窗、不动文档**。
///
/// 打开是**异步**的（2026-11-16）：界面入口（<see cref="StartInkOpen"/>）只做对话框 +
/// 起后台任务，读盘 / 校验 / 解析都在工人线程；结果由 <see cref="StepInkOpen"/> 采纳。
/// 自检要"确定性的一拍"，仍可用**同步**的 <see cref="OpenInkFile"/>（`OpenInkFileForTest`）。
/// </summary>
public partial class InkEngine
{
    /// <summary>「打开墨迹」的单飞闸门（状态全在里面；见 <see cref="InkOpenGate"/>）。</summary>
    private readonly InkOpenGate _inkOpen = new();

    /// <summary>「墨迹」页显示的上次动作结果（保存/打开成功与失败都写在这儿）。</summary>
    internal string InkStatus { get; private set; } = "";

    private void SetInkStatus(string text)
    {
        InkStatus = text;
        Console.WriteLine("[墨迹] " + text);
        NotifyUiStateChanged();
    }

    internal void SaveInkFileFromUi() => SaveInkFile(null);

    /// <summary>
    /// 「打开墨迹」入口：对话框在 UI 线程，读盘 + 校验 + 解析在后台
    /// （见 <see cref="StartInkOpen"/>）。大文件不再让界面看起来卡死。
    /// </summary>
    internal void OpenInkFileFromUi() => StartInkOpen(null);

    /// <summary>
    /// 「历史板书」（更多 → 墨迹，2026-10-14）：打开"打开墨迹"那条路，起始目录 = 板书历史目录。
    /// 复用整条老路（先备份当前板书 → 校验 → 替换），所以从历史里载入一次
    /// **不会丢当前这一份**（它先进"打开前备份"）。
    /// </summary>
    internal void OpenInkHistoryFromUi()
    {
        if (BoardHistory.Count == 0)
        {
            SetInkStatus("还没有历史板书（写过字之后停手一会儿就会自动留一份）");
            return;
        }
        StartInkOpen(null, BoardHistory.Dir);
    }

    /// <summary>
    /// **打开一条板书历史**（2026-11-16「最近文档」页）：不弹对话框，直接走
    /// <see cref="StartInkOpen(string, string)"/> 那条路（先备份当前白板 → 校验 → 替换；
    /// 读盘与解析在后台，大文件不会把界面卡住）。
    /// 文件已经不在了（刚被历史清理删掉 / 目录被挪走）只写状态行，不崩、不动板书。
    /// </summary>
    internal void OpenBoardHistoryEntryFromUi(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetInkStatus("这份历史板书已经不在了（可能被历史清理删掉）");
            return;
        }
        StartInkOpen(path, BoardHistory.Dir);
    }

    /// <summary>「保存图片」入口（更多 → 墨迹 → 保存图片，2026-10-02）。</summary>
    internal void SaveBoardImageFromUi()
    {
        int last = int.TryParse(GetUiPref(BoardImageFormatPref), out int v)
                   && v >= 1 && v <= ExportFormats.Count ? v : 2;   // 默认第 2 条 = JPEG 白底（好发微信）
        SaveBoardImage(null, last);
    }

    /// <summary>自检用：不弹对话框，直接写到指定路径（其余流程一模一样）。</summary>
    internal bool SaveBoardImageForTest(string path, int filterIndex = 2)
        => SaveBoardImage(path, filterIndex);

    /// <summary>自检用：不弹对话框，直接读写指定路径（其余流程一模一样）。</summary>
    internal bool SaveInkFileForTest(string path) => SaveInkFile(path);

    /// <summary>自检用：**同步**那条老路（读盘 + 校验 + 解析当场做完，行为一字未改）。
    /// 老自检（`--inkfiletest` / `--historytest`）靠它；产品界面不走它。</summary>
    internal bool OpenInkFileForTest(string path) => OpenInkFile(path);

    /// <summary>自检用：走产品那条**异步**路（绕开对话框）。返回 true = 已经发起。</summary>
    internal bool StartInkOpenForTest(string path) => StartInkOpen(path);

    /// <summary>自检用：推进一拍（产品里由 `RenderAll` 里的 `StepInkOpen` 采纳结果）。</summary>
    internal void PumpInkOpenForTest() => RenderAll();

    /// <summary>
    /// 自检用：等到在途的"打开墨迹"结束（最多 <paramref name="timeoutMs"/>）。
    /// 等待期间**一直在出帧**——这正是要验的"界面不假死"；返回 false = 超时。
    /// </summary>
    internal bool WaitInkOpenForTest(int timeoutMs = 20000)
    {
        double start = _clock.Elapsed.TotalMilliseconds;
        while (InkOpenBusy && _clock.Elapsed.TotalMilliseconds - start < timeoutMs)
        {
            RenderAll();
            Thread.Sleep(2);
        }
        RenderAll();
        return !InkOpenBusy;
    }

    /// <summary>在途的"打开墨迹"还在跑吗（`NeedsFrame` 靠它继续出帧，界面不会停在旧画面）。</summary>
    internal bool InkOpenBusy => _inkOpen.Current != null;

    private bool SaveInkFile(string path)
    {
        if (PptMode)
        {
            SetInkStatus("放映中由 PPT 自己保存（退出放映后再用这里）");
            return false;
        }
        if (Doc.Strokes.Count == 0)
        {
            SetInkStatus("还没有可保存的墨迹");
            return false;
        }

        if (path == null)
        {
            // 自检模式绝不弹系统对话框（模态调用会在自检的消息泵里卡死，见 ExportSelection）。
            if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return false; }
            string dir = GetUiPref("inkDir");
            string suggested = $"板书-{DateTime.Now:yyyyMMdd-HHmm}" + InkSerializer.FileExtension;
            BorrowFocusForDialog();
            ExportDialogOpen = true;
            try { path = ExportFileDialog.AskForInkSave(OwnerHwnd(), suggested, dir); }
            catch (Exception ex) { SetInkStatus("弹保存对话框失败：" + ex.Message); return false; }
            finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
            if (path == null) { SetInkStatus("已取消"); return false; }
            SetUiPref("inkDir", Path.GetDirectoryName(path) ?? "");
        }

        try
        {
            var blob = InkSerializer.Save(Doc);
            InkFileStore.WriteAtomic(path, blob);
            SetInkStatus(Loc.T("已保存 {0}（{1} 个对象）", Path.GetFileName(path), Doc.Strokes.Count));
            return true;
        }
        catch (Exception ex) { SetInkStatus("保存失败：" + ex.Message); return false; }
    }

    /// <param name="startDir">对话框的起始目录；null = 上一次用过的目录（"打开墨迹"那条路）。
    /// 传了它（历史板书那条路）**不覆盖**用户的 "inkDir" 偏好——老师下次点"打开墨迹"
    /// 还是从自己上次的目录开始。</param>
    /// <remarks>**同步**那条路（自检 / 老调用点）。产品界面走 <see cref="StartInkOpen"/>。</remarks>
    private bool OpenInkFile(string path, string startDir = null)
    {
        if (PptMode)
        {
            SetInkStatus("放映中由 PPT 自己读盘（退出放映后再用这里）");
            return false;
        }
        CancelInkOpen();                       // 同步开一份 = 在途的异步结果一律作废

        if (path == null && !AskInkFilePath(ref path, startDir)) return false;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { SetInkStatus("打不开：" + ex.Message); return false; }

        // **先校验、后动文档**：坏文件不许碰到白板槽（连备份都不必写）。
        try { InkSerializer.Validate(bytes); }
        catch (Exception ex) { SetInkStatus("打不开：" + ex.Message); return false; }

        // **打开落白板槽**（2026-10-16 用户定："打开的板书就是白板"）：备份的是
        // **要被替换的那一份**（白板槽）；桌面 / 文档 / PPT 的槽一概不动。
        string backupNote = BackupBoardBeforeReplace();

        List<Stroke> loaded;
        try
        {
            loaded = InkSerializer.LoadStrokes(bytes, out int maxId);
            Doc.LoadPageContent(InkDocument.BoardPageKey, loaded, maxId);   // 自带清该槽的撤销栈
        }
        catch (Exception ex) { SetInkStatus("打不开：" + ex.Message); return false; }

        EnterBoardForOpenedInk();
        SetInkStatus(InkOpenedText(path, loaded.Count, backupNote));
        return true;
    }

    // =====================================================================
    //  **异步"打开墨迹"**（2026-11-16）：大 .inkb 不再把界面卡死。
    //
    //  状态机（单飞 + 按代作废）：
    //    ① StartInkOpen：对话框（路径为 null 时）→ 领一代新代号 → 挂 toast →
    //       起工人线程（读盘 / 校验 / 解析）；
    //    ② 工人线程：只写 job 的字段，最后置 job.Done（volatile）；代次已变就直接扔；
    //    ③ StepInkOpen（UI 线程，每帧）：Done 了才采纳——**此刻**才备份当前白板
    //       （载入期间用户新写的那几笔也一起留住）→ 换页 → 切白板 → 收 toast → 写状态；
    //    ④ CancelInkOpen：关文档 / 打开别的文档 / 退出时调，代次 +1，旧结果永远采纳不到。
    // =====================================================================

    /// <summary>
    /// 发起一次**异步打开墨迹**。路径为 null 时先弹对话框（这一步仍是同步的——
    /// 系统对话框本来就是模态的）；之后的读盘 / 校验 / 解析都在后台。
    /// 返回 true = 已经发起；false = 被拒 / 取消 / 文件不存在（状态行已写）。
    /// </summary>
    internal bool StartInkOpen(string path, string startDir = null)
    {
        if (PptMode)
        {
            SetInkStatus("放映中由 PPT 自己读盘（退出放映后再用这里）");
            return false;
        }
        if (path == null && !AskInkFilePath(ref path, startDir)) return false;

        // 文件不在了：便宜的一步就在 UI 线程判掉，不用白起一个线程。
        if (!File.Exists(path))
        {
            SetInkStatus(Loc.T("打不开 {0}：文件不存在", Path.GetFileName(path)));
            return false;
        }

        var job = new InkOpenJob
        {
            Path = path,
            Gen = Interlocked.Increment(ref _inkOpen.Gen),
        };
        _inkOpen.Current = job;               // 旧 job 就此脱钩：它的结果没人采

        string name = Path.GetFileName(path);
        string hint = Loc.T("正在打开 {0}…", name);
        // 60 秒：大文件读 + 解析可能要等这么久；结束（成功/失败）会主动收掉它。
        ShowToast(hint, 60000);
        SetInkStatus(hint);
        _dirty = true;

        new Thread(() => RunInkOpenJob(job))
        { IsBackground = true, Name = "InkTeach-打开墨迹" }.Start();
        Console.WriteLine($"[墨迹] 后台开始打开 {name}（{startDir ?? Path.GetDirectoryName(path)}）");
        return true;
    }

    /// <summary>工人线程：读盘 → 校验 → 解析。只写 <paramref name="job"/> 的字段。</summary>
    private void RunInkOpenJob(InkOpenJob job)
    {
        try
        {
            var bytes = File.ReadAllBytes(job.Path);
            job.Bytes = bytes.Length;
            if (InkOpenJobStale(job)) return;          // 已经换了目标：别再花时间解析
            InkSerializer.Validate(bytes);
            var strokes = InkSerializer.LoadStrokes(bytes, out int maxId);
            if (InkOpenJobStale(job)) return;          // 解析期间又被顶掉：结果直接扔
            job.Strokes = strokes;
            job.MaxId = maxId;
        }
        catch (Exception ex) { job.Error = ex.Message; }
        finally { job.Done = true; }                   // volatile：主线程据此采纳
    }

    /// <summary>这次任务是不是已经被后来的打开 / 关闭作废了（工人线程也会读）。</summary>
    private bool InkOpenJobStale(InkOpenJob job) => Volatile.Read(ref _inkOpen.Gen) != job.Gen;

    /// <summary>
    /// 每帧一拍：采纳后台打开的结果。主循环和 `RenderAll` **两处都调**
    /// （理由同 `StepAppDataJob`：自检走的是"抽消息 + 渲染"那条路，只挂主循环会验不到）。
    /// </summary>
    internal void StepInkOpen()
    {
        if (_quit) { CancelInkOpen(); return; }
        var job = _inkOpen.Current;
        if (job == null || !job.Done) return;
        _inkOpen.Current = null;

        if (InkOpenJobStale(job)) return;              // 已被新的一次打开顶掉：静默丢
        if (PptMode)
        {
            ClearToast();
            SetInkStatus("放映中不改白板（这次打开已作废）");
            return;
        }
        if (job.Error != null)
        {
            ClearToast();
            SetInkStatus("打不开：" + job.Error);
            Console.WriteLine("[墨迹] 后台打开失败：" + job.Error);
            return;
        }

        var loaded = job.Strokes ?? new List<Stroke>();
        // ⚠ **备份必须在采纳这一刻做**：载入期间用户还在白板上写字，
        // 备份的必须是"马上要被替换掉的这一份"，早备份就等于丢掉那几笔。
        string backupNote = BackupBoardBeforeReplace();
        try { Doc.LoadPageContent(InkDocument.BoardPageKey, loaded, job.MaxId); }
        catch (Exception ex)
        {
            ClearToast();
            SetInkStatus("打不开：" + ex.Message);
            return;
        }

        EnterBoardForOpenedInk();
        ClearToast();
        SetInkStatus(InkOpenedText(job.Path, loaded.Count, backupNote));
        Console.WriteLine($"[墨迹] 已打开 {Path.GetFileName(job.Path)}：{loaded.Count} 个对象"
                          + $"（{backupNote}；后台读盘 {job.Bytes / 1024} KB）");
    }

    /// <summary>
    /// 作废在途的打开（关文档 / 打开别的文档 / 退出前调）：代次 +1，
    /// 旧线程跑完自己扔掉结果，**不会**覆盖后来的文档或板书。
    /// </summary>
    internal void CancelInkOpen()
    {
        if (_inkOpen.Current == null) return;
        _inkOpen.Current = null;
        Interlocked.Increment(ref _inkOpen.Gen);
        ClearToast();
        Console.WriteLine("[墨迹] 在途的打开已作废（换了目标 / 关掉了）");
    }

    /// <summary>
    /// 弹"打开墨迹"对话框拿路径（自检模式不弹）。返回 true = 拿到；
    /// false = 取消 / 弹框失败（状态行已写）。
    /// </summary>
    private bool AskInkFilePath(ref string path, string startDir)
    {
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return false; }
        string dir = startDir ?? GetUiPref("inkDir");
        BorrowFocusForDialog();
        ExportDialogOpen = true;
        try { path = ExportFileDialog.AskForInkOpen(OwnerHwnd(), dir); }
        catch (Exception ex) { SetInkStatus("弹打开对话框失败：" + ex.Message); return false; }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
        if (path == null) { SetInkStatus("已取消"); return false; }
        if (startDir == null) SetUiPref("inkDir", Path.GetDirectoryName(path) ?? "");
        return true;
    }

    /// <summary>
    /// 把当前白板槽写一份"打开前备份"，返回给状态行用的短语。
    /// 同步 / 异步两条打开路共用；**必须在替换前一刻调**（见 StepInkOpen 的注释）。
    /// </summary>
    private string BackupBoardBeforeReplace()
    {
        var boardNow = Doc.StrokesOf(InkDocument.BoardPageKey);
        if (boardNow == null || boardNow.Count == 0) return "白板是空的，未备份";
        try
        {
            InkFileStore.WriteBackup(InkSerializer.SaveStrokes(boardNow));
            return "旧板书已备份";
        }
        catch (Exception ex)
        {
            Console.WriteLine("打开前备份失败（继续打开）：" + ex.Message);
            return "备份失败";
        }
    }

    /// <summary>打开成功后切到白板展示 + 相机归零 + 无选中（同步 / 异步两条路共用）。</summary>
    private void EnterBoardForOpenedInk()
    {
        // 穿透里打开板书：先退出穿透（穿透下画布是让开的，"打开没反应"同样说不通）。
        if (PassThrough) SetPassThrough(false, restoreBoard: false);
        _boardReturnKey = Doc.PageKey == InkDocument.BoardPageKey ? _boardReturnKey : Doc.PageKey;
        Doc.SwitchPage(InkDocument.BoardPageKey);
        BoardOn = true;
        Doc.Selected.Clear();
        ViewOffsetY = 0f;
        ViewOffsetX = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        _pageScroll.Clear();
        Doc.InvalidateAll();
        _dirty = true;
    }

    /// <summary>打开成功的状态行（两条路同一句文案，自检只认它）。</summary>
    private static string InkOpenedText(string path, int count, string backupNote)
        => Loc.T("已打开 {0}（{1} 个对象；{2}）", Path.GetFileName(path), count, backupNote);

    /// <summary>「保存图片」上次选的格式（和选中导出的 `ui.exportFormat` 分开记）。</summary>
    private const string BoardImageFormatPref = "boardImageFormat";

    /// <summary>
    /// **「保存图片」**（2026-10-02 用户提议）：把**整块板书**（当前页全部对象的
    /// 内容包围盒）渲染成一张图片——老师要把墨迹发学生 / 发微信，`.inkb` 发不了。
    ///
    /// 与选中操作条「导出」的分工（用户点名对照过，两个都保留）：
    ///   · **导出** = 只导**选中的**、紧凑裁切，默认**透明底** —— 贴课件 / 抠图；
    ///   · **这里** = **整块板书**，默认 **JPEG 白底**（格式栏里那条"好发微信邮件"）
    ///     —— 发学生 / 发家长。
    ///
    /// 复用现成管线：`EditRegion.Of` 取包围盒（4×DPI 白边、32M 像素上限）→
    /// `RenderStrokesToBgra` 离屏渲染（图形 / 图片 / 压感都在）→ `AskForImage`
    /// 系统另存为（四种格式照旧）→ `WriteExport` 写盘。
    /// **不碰选区、不写剪贴板**（导出只管落盘）。放映中与空板书按 6.4.1b 置灰/拒绝。
    /// </summary>
    private bool SaveBoardImage(string path, int filterIndex)
    {
        if (PptMode) { SetInkStatus("放映中不存图片（退出放映后再用这里）"); return false; }
        if (Doc.Strokes.Count == 0) { SetInkStatus("还没有可保存的墨迹"); return false; }

        var box = EditRegion.Of(Doc.Strokes);
        if (box.IsEmpty) { SetInkStatus("还没有可保存的墨迹"); return false; }
        box = box.Inflate(4f * DpiScale);
        int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 32_000_000) { SetInkStatus(Loc.T("板书太大（{0}×{1}），存不下", w, h)); return false; }
        if (_windows.Count == 0) { SetInkStatus("没有可用窗口，存不了"); return false; }

        if (path == null)
        {
            // 自检模式绝不弹系统对话框（模态调用会在自检的消息泵里卡死，见 ExportSelection）。
            if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return false; }
            int want = filterIndex >= 1 && filterIndex <= ExportFormats.Count ? filterIndex : 2;
            string suggested = $"板书-{DateTime.Now:yyyyMMdd-HHmm}" + ExportFormats.ExtensionFor(want);
            BorrowFocusForDialog();
            ExportDialogOpen = true;
            try
            {
                path = ExportFileDialog.AskForImage(OwnerHwnd(), suggested, want, "保存板书图片", out int chosen);
                if (chosen >= 1) want = chosen;
            }
            catch (Exception ex) { SetInkStatus("弹保存对话框失败：" + ex.Message); return false; }
            finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
            if (path == null) { SetInkStatus("已取消"); return false; }
            SetUiPref(BoardImageFormatPref, want.ToString());
            filterIndex = want;
        }

        var bgra = _windows[0].RenderStrokesToBgra(Doc.Strokes, box, w, h);
        if (bgra == null) { SetInkStatus("图片渲染失败"); return false; }
        if (!WriteExport(bgra, w, h, path, filterIndex))
        {
            SetInkStatus("保存图片失败（详见控制台）");
            return false;
        }
        SetInkStatus(Loc.T("已保存图片 {0}（{1}×{2}）", Path.GetFileName(path), w, h));
        return true;
    }

    // =====================================================================
    //  「保存…」/「打开…」——2026-10-16 合并入口（原「保存墨迹」＋「保存图片」、
    //  「打开墨迹」＋「打开文档」，用户定 7→5 格）。**按扩展名分派**，内部全走老路：
    //  .inkb → 板书文件（SaveInkFile / OpenInkFile）；图片 → 整块板书出图；
    //  图片/PDF/PPT → 铺页 / 放映（OpenDocuments）。对话框只有一个（各一条）。
    // =====================================================================

    /// <summary>
    /// **「保存…」**（2026-10-16 合并入口：原「保存墨迹」＋「保存图片」）。
    /// 一条"另存为"收三种：图片 JPG（默认）/ 图片 PNG / 板书 .inkb——
    /// **按最终扩展名分派**（.inkb → 整份板书文件；图片 → 整块板书出图，白底）。
    /// 空板书 / 放映中不响应；结果写进 <see cref="UiState.InkStatus"/>。
    /// </summary>
    internal void SaveBoardAnyFromUi()
    {
        if (PptMode) { SetInkStatus("放映中由 PPT 自己保存（退出放映后再用这里）"); return; }
        if (Doc.Strokes.Count == 0) { SetInkStatus("还没有可保存的墨迹"); return; }
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return; }

        string dir = GetUiPref("inkDir");
        string suggested = $"板书-{DateTime.Now:yyyyMMdd-HHmm}.jpg";
        BorrowFocusForDialog();
        ExportDialogOpen = true;
        string path;
        try { path = ExportFileDialog.AskForBoardSave(OwnerHwnd(), suggested, dir); }
        catch (Exception ex) { SetInkStatus("弹保存对话框失败：" + ex.Message); return; }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
        if (path == null) { SetInkStatus("已取消"); return; }
        SetUiPref("inkDir", Path.GetDirectoryName(path) ?? "");
        SaveBoardAnyCore(path);
    }

    /// <summary>「保存…」的分派核心（自检直接调它，绕开对话框）。</summary>
    internal bool SaveBoardAnyCore(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".inkb") return SaveInkFile(path);
        // 图片：白底两条（JPG = 2、PNG 白底 = 3）；手动敲成 .bmp 也认（4）。
        int fmt = ext is ".jpg" or ".jpeg" ? 2 : ext == ".bmp" ? 4 : 3;
        return SaveBoardImage(path, fmt);
    }

    /// <summary>自检用：绕开对话框，直接按扩展名分派。</summary>
    internal bool SaveBoardAnyForTest(string path) => SaveBoardAnyCore(path);
}
