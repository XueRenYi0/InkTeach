using System.IO.Compression;
using System.Text;
using System.Threading;

namespace InkEngine;

/// <summary>
/// **全局备份 / 恢复**（2026-10-25）。只打包"老师自己攒下来的东西"：
///
///   · 收藏（`%APPDATA%\InkTeach\Library`）
///   · 设置 / 键位（`settings.json`）、点名名单
///   · 历史板书（`%LOCALAPPDATA%\InkTeach\History`）
///   · 课件墨迹批注（`Ppt` / `Doc`）
///
/// **不含 PDF / PPT 原文件**——那些是老师的课件本体，备份包里只放 InkTeach 的批注。
///
/// 恢复采用"先完整解到临时目录、校验通过再逐项换入"：坏包不会先把现有数据删掉；
/// 被换下来的旧目录留在 `<名字>.before-restore`，万一恢复错了还能捞回来。
/// </summary>
internal static class AppBackup
{
    public const string ManifestName = "InkTeachBackup.txt";
    public const string FormatVersion = "1";

    /// <summary>自检用：把两个根指到临时目录，**别动用户真实数据**。</summary>
    public static string RoamingRootOverride;
    public static string LocalRootOverride;

    public static string RoamingRoot => RoamingRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkTeach");
    public static string LocalRoot => LocalRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkTeach");

    private static readonly string[] RoamingItems = { "Library", "settings.json", "Names.txt" };
    private static readonly string[] LocalItems = { "History", "Ppt", "Doc" };

    /// <summary>备份到 <paramref name="zipPath"/>。返回 false 时 <paramref name="error"/> 是原因。</summary>
    public static bool Create(string zipPath, out int files, out long bytes, out int skipped, out string error,
                              Action<int> onProgress = null)
    {
        files = 0;
        skipped = 0;
        bytes = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(zipPath)) { error = "没有选择保存位置"; return false; }

        string part = zipPath + ".part";
        try
        {
            string parent = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            try { if (File.Exists(part)) File.Delete(part); } catch { }

            var dirs = new List<string>();
            var plan = new List<(string Entry, string Path, long Size)>();
            CollectRoot("roaming", RoamingRoot, RoamingItems, dirs, plan);
            CollectRoot("local", LocalRoot, LocalItems, dirs, plan);
            long total = 0;
            foreach (var f in plan) total += Math.Max(0, f.Size);

            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var manifest = zip.CreateEntry(ManifestName, CompressionLevel.NoCompression);
                using (var w = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                {
                    w.WriteLine("format=" + FormatVersion);
                    w.WriteLine("created=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                }

                foreach (string d in dirs)
                    zip.CreateEntry(d, CompressionLevel.NoCompression);

                long done = 0;
                onProgress?.Invoke(0);
                foreach (var item in plan)
                {
                    AddFile(zip, item.Entry, item.Path, ref files, ref skipped);
                    done += Math.Max(0, item.Size);
                    if (total > 0) onProgress?.Invoke((int)Math.Clamp(done * 100 / total, 0, 100));
                }
                onProgress?.Invoke(100);
            }

            File.Move(part, zipPath, overwrite: true);
            bytes = new FileInfo(zipPath).Length;
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            error = ex.Message;
            return false;
        }
    }

    private static void CollectRoot(string prefix, string root, string[] items,
                                    List<string> dirs, List<(string Entry, string Path, long Size)> plan)
    {
        foreach (string item in items)
        {
            string path = Path.Combine(root, item);
            if (Directory.Exists(path))
            {
                // 空目录也要留一条，恢复时才知道"这一项在备份里、应该被清空"。
                dirs.Add(prefix + "/" + item + "/");
                foreach (string file in SafeEnumerate(path))
                {
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = Path.GetRelativePath(path, file).Replace('\\', '/');
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { }
                    plan.Add((prefix + "/" + item + "/" + rel, file, size));
                }
            }
            else if (File.Exists(path))
            {
                if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                plan.Add((prefix + "/" + item, path, size));
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        var list = new List<string>();
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                list.Add(f);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[备份] 遍历失败：" + ex.Message);
        }
        return list;
    }

    private static void AddFile(ZipArchive zip, string entryName, string file, ref int files, ref int skipped)
    {
        try
        {
            // 半成品写盘文件没有备份价值（下次写会被覆盖）。
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            files++;
        }
        catch (Exception ex)
        {
            skipped++;
            Console.WriteLine($"[备份] 跳过 {file}：{ex.Message}");
        }
    }

    /// <summary>
    /// 从备份包恢复。**先校验、再换入**：包不合法时现有数据一个字节都不动。
    /// 返回 false 时 <paramref name="error"/> 是原因。
    /// </summary>
    public static bool Restore(string zipPath, out int files, out string error, Action<int> onProgress = null)
    {
        files = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            error = "备份包不存在";
            return false;
        }

        string temp = Path.Combine(Path.GetTempPath(), "InkTeachRestore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = zip.GetEntry(ManifestName);
                if (manifest == null) { error = "这不是 InkTeach 备份包（缺少 " + ManifestName + "）"; return false; }
                using (var reader = new StreamReader(manifest.Open()))
                {
                    string text = reader.ReadToEnd();
                    if (!text.Contains("format=" + FormatVersion, StringComparison.Ordinal))
                    {
                        error = "备份包版本不认识（需要 format=" + FormatVersion + "）";
                        return false;
                    }
                }

                string tempFull = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                int totalFiles = 0;
                foreach (var e in zip.Entries)
                    if (!e.FullName.EndsWith("/", StringComparison.Ordinal)) totalFiles++;
                int doneFiles = 0;
                onProgress?.Invoke(0);
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name == ManifestName) continue;
                    if (!name.StartsWith("roaming/", StringComparison.Ordinal)
                        && !name.StartsWith("local/", StringComparison.Ordinal))
                    {
                        error = "备份包里有不认识的路径：" + name;
                        return false;
                    }

                    string dest = Path.GetFullPath(Path.Combine(temp, name));
                    if (!dest.StartsWith(tempFull, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "备份包路径非法：" + name;
                        return false;
                    }
                    if (name.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(dest, overwrite: true);
                    doneFiles++;
                    if (totalFiles > 0)
                        onProgress?.Invoke((int)Math.Clamp(doneFiles * 100L / totalFiles, 0, 100));
                }
                onProgress?.Invoke(100);
            }

            int applied = 0;
            var rollback = new List<Action>();
            try
            {
                ApplyRoot(temp, "roaming", RoamingRoot, rollback, ref applied);
                ApplyRoot(temp, "local", LocalRoot, rollback, ref applied);
            }
            catch
            {
                for (int i = rollback.Count - 1; i >= 0; i--)
                {
                    try { rollback[i](); } catch (Exception ex) { Console.WriteLine("[恢复] 回滚失败：" + ex.Message); }
                }
                throw;
            }
            files = applied;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    private static void ApplyRoot(string temp, string prefix, string root,
                                  List<Action> rollback, ref int applied)
    {
        string source = Path.Combine(temp, prefix);
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(root);

        foreach (string dir in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(dir);
            if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal)) continue;
            string target = Path.Combine(root, name);
            string old = target + ".before-restore";

            if (Directory.Exists(old)) Directory.Delete(old, true);
            else if (File.Exists(old)) File.Delete(old);
            if (File.Exists(target))
            {
                // 目标原本是文件（极罕见）：先挪走，别让 Directory.Move 撞上。
                string fileOld = target + ".before-restore-file";
                if (File.Exists(fileOld)) File.Delete(fileOld);
                File.Move(target, fileOld);
            }

            bool hadTarget = Directory.Exists(target);
            if (hadTarget) Directory.Move(target, old);
            try
            {
                Directory.Move(dir, target);
            }
            catch
            {
                if (hadTarget && !Directory.Exists(target) && Directory.Exists(old))
                    Directory.Move(old, target);
                throw;
            }
            applied++;
            rollback.Add(() =>
            {
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    if (Directory.Exists(old)) Directory.Move(old, target);
                }
                catch (Exception ex) { Console.WriteLine("[恢复] 回滚目录失败：" + ex.Message); }
            });
        }

        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            string target = Path.Combine(root, name);
            string old = target + ".before-restore";
            if (File.Exists(old)) File.Delete(old);
            else if (Directory.Exists(old)) Directory.Delete(old, true);

            bool hadTarget = File.Exists(target);
            if (hadTarget) File.Move(target, old);
            try
            {
                File.Copy(file, target, overwrite: true);
            }
            catch
            {
                if (hadTarget && File.Exists(old) && !File.Exists(target))
                    File.Move(old, target);
                throw;
            }
            applied++;
            rollback.Add(() =>
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (File.Exists(old)) File.Move(old, target, overwrite: true);
                }
                catch (Exception ex) { Console.WriteLine("[恢复] 回滚文件失败：" + ex.Message); }
            });
        }
    }
}

public partial class InkEngine
{
    private int _appDataJob;
    private string _appDataJobMessage;
    private bool _appDataJobRestore;
    private int _appDataJobPercent;
    private int _appDataJobShownPercent = -1;
    /// <summary>恢复成功后，这一轮不再写 settings.json（内存里是旧值，写了会盖掉刚恢复的）。</summary>
    private bool _settingsRestored;

    /// <summary>「设置 → 墨迹 → 备份全部数据…」。</summary>
    internal void BackupAllDataFromUi()
    {
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return; }
        if (Interlocked.CompareExchange(ref _appDataJob, 1, 0) != 0)
        {
            SetInkStatus("正在备份/恢复，请稍等");
            return;
        }

        string path;
        string dir = GetUiPref("backupDir");
        string suggested = $"InkTeach备份-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        BorrowFocusForDialog();
        ExportDialogOpen = true;
        try { path = ExportFileDialog.AskForBackupSave(OwnerHwnd(), suggested, dir); }
        catch (Exception ex) { SetInkStatus("弹保存对话框失败：" + ex.Message); _appDataJob = 0; return; }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
        if (path == null) { SetInkStatus("已取消"); _appDataJob = 0; return; }

        SetUiPref("backupDir", Path.GetDirectoryName(path) ?? "");
        _appDataJobRestore = false;
        _appDataJobMessage = null;
        _appDataJobPercent = 0;
        _appDataJobShownPercent = -1;
        SetInkStatus("正在备份…（数据多时要等一会儿，界面不卡）");
        new Thread(() =>
        {
            bool ok = AppBackup.Create(path, out int n, out long bytes, out int skipped, out string err,
                                       p => Volatile.Write(ref _appDataJobPercent, p));
            _appDataJobMessage = ok
                ? $"已备份 {n} 个文件（{bytes / 1024.0 / 1024.0:F1} MB）"
                  + (skipped > 0 ? $"，{skipped} 个文件被占用已跳过" : "")
                  + "：" + path
                : "备份失败：" + err;
            Volatile.Write(ref _appDataJob, ok ? 2 : 3);
        }) { IsBackground = true, Name = "InkTeach 备份" }.Start();
    }

    /// <summary>「设置 → 墨迹 → 从备份恢复…」。</summary>
    internal void RestoreAllDataFromUi()
    {
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return; }
        if (Volatile.Read(ref _appDataJob) != 0)
        {
            SetInkStatus("正在备份/恢复，请稍等");
            return;
        }

        string path;
        string dir = GetUiPref("backupDir");
        BorrowFocusForDialog();
        ExportDialogOpen = true;
        try { path = ExportFileDialog.AskForBackupOpen(OwnerHwnd(), dir); }
        catch (Exception ex) { SetInkStatus("弹打开对话框失败：" + ex.Message); return; }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
        if (path == null) { SetInkStatus("已取消"); return; }

        SetUiPref("backupDir", Path.GetDirectoryName(path) ?? "");
        _appDataJobRestore = true;
        _appDataJobMessage = null;
        _appDataJobPercent = 0;
        _appDataJobShownPercent = -1;
        Volatile.Write(ref _appDataJob, 1);
        SetInkStatus("正在恢复…（校验通过后才换入，旧数据会留 .before-restore）");
        new Thread(() =>
        {
            bool ok = AppBackup.Restore(path, out int n, out string err,
                                        p => Volatile.Write(ref _appDataJobPercent, p));
            _appDataJobMessage = ok
                ? $"恢复完成（{n} 项）。请重启软件后使用；旧数据在 .before-restore"
                : "恢复失败：" + err + "（现有数据没动）";
            Volatile.Write(ref _appDataJob, ok ? 2 : 3);
        }) { IsBackground = true, Name = "InkTeach 恢复" }.Start();
    }

    /// <summary>后台备份/恢复的结果轮询（主循环里调，界面不卡）。</summary>
    internal void StepAppDataJob()
    {
        int st = Volatile.Read(ref _appDataJob);
        if (st == 1)
        {
            int p = Volatile.Read(ref _appDataJobPercent);
            int shown = _appDataJobShownPercent;
            if (shown < 0 || p - shown >= 5 || p >= 100)
            {
                _appDataJobShownPercent = p;
                SetInkStatus((_appDataJobRestore ? "正在恢复… " : "正在备份… ") + p + "%");
            }
            return;
        }
        if (st < 2) return;
        Volatile.Write(ref _appDataJob, 0);
        _appDataJobShownPercent = -1;
        string msg = _appDataJobMessage ?? (st == 2 ? "完成" : "失败");
        bool restore = _appDataJobRestore && st == 2;
        SetInkStatus(msg);
        Console.WriteLine("[备份/恢复] " + msg);
        if (restore)
        {
            // 收藏目录整个换过：内存里的列表要重读；设置/批注提示重启。
            _settingsRestored = true;
            ReloadLibrary();
            LibrarySelected.Clear();
            NotifyUiStateChanged();
            _dirty = true;
        }
        NotifyUiStateChanged();
    }
}
