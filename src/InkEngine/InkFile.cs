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
                try { File.Delete(files[i]); } catch { }
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
/// 「保存墨迹 / 打开墨迹」两条界面命令的实现（墨迹 A）。
///
/// 边界（用户 2026-10-01 拍板，见 6.4.1）：
///   · **只在白板模式开放**；PPT 放映中不响应——`Save(doc)` 只写当前页，
///     手动保存整份 PPT 批注是"批注包导出/导入"（C10）的活；
///   · 打开**不进撤销栈**，但**必须**先写一份"打开前备份"；
///   · 失败一律走 `InkStatus` 状态行 + 控制台，**不弹窗、不动文档**。
/// </summary>
public partial class InkEngine
{
    /// <summary>「墨迹」页显示的上次动作结果（保存/打开成功与失败都写在这儿）。</summary>
    internal string InkStatus { get; private set; } = "";

    private void SetInkStatus(string text)
    {
        InkStatus = text;
        Console.WriteLine("[墨迹] " + text);
        NotifyUiStateChanged();
    }

    internal void SaveInkFileFromUi() => SaveInkFile(null);
    internal void OpenInkFileFromUi() => OpenInkFile(null);

    /// <summary>自检用：不弹对话框，直接读写指定路径（其余流程一模一样）。</summary>
    internal bool SaveInkFileForTest(string path) => SaveInkFile(path);
    internal bool OpenInkFileForTest(string path) => OpenInkFile(path);

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
            SetInkStatus($"已保存 {Path.GetFileName(path)}（{Doc.Strokes.Count} 个对象）");
            return true;
        }
        catch (Exception ex) { SetInkStatus("保存失败：" + ex.Message); return false; }
    }

    private bool OpenInkFile(string path)
    {
        if (PptMode)
        {
            SetInkStatus("放映中由 PPT 自己读盘（退出放映后再用这里）");
            return false;
        }

        if (path == null)
        {
            if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return false; }
            string dir = GetUiPref("inkDir");
            BorrowFocusForDialog();
            ExportDialogOpen = true;
            try { path = ExportFileDialog.AskForInkOpen(OwnerHwnd(), dir); }
            catch (Exception ex) { SetInkStatus("弹打开对话框失败：" + ex.Message); return false; }
            finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
            if (path == null) { SetInkStatus("已取消"); return false; }
            SetUiPref("inkDir", Path.GetDirectoryName(path) ?? "");
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { SetInkStatus("打不开：" + ex.Message); return false; }

        // **先校验、后动文档**：坏文件不许碰到当前板书（连备份都不必写）。
        try { InkSerializer.Validate(bytes); }
        catch (Exception ex) { SetInkStatus("打不开：" + ex.Message); return false; }

        // 打开前留一份当前板书（有内容才留）。
        string backupNote = "当前板书是空的，未备份";
        if (Doc.Strokes.Count > 0)
        {
            try { InkFileStore.WriteBackup(InkSerializer.Save(Doc)); backupNote = "旧板书已备份"; }
            catch (Exception ex)
            {
                Console.WriteLine("打开前备份失败（继续打开）：" + ex.Message);
                backupNote = "备份失败";
            }
        }

        try { InkSerializer.LoadInto(Doc, bytes); }
        catch (Exception ex) { SetInkStatus("打不开：" + ex.Message); return false; }

        // 新文档从"第 0 页 ＋ 相机归零 ＋ 无选中"开始（ReplaceAll 已经重置页槽与历史）。
        Doc.Selected.Clear();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        _pageScroll.Clear();
        Doc.InvalidateAll();
        _dirty = true;
        SetInkStatus($"已打开 {Path.GetFileName(path)}（{Doc.Strokes.Count} 个对象；{backupNote}）");
        return true;
    }
}
