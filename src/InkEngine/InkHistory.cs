namespace InkEngine;

/// <summary>
/// 历史清理（墨迹 B，2026-10-01）：按"保留期"清掉**过期缓存**。
///
/// 三条边界（用户拍板，见 计划-更多面板与课堂工具.md 6.4.2）：
///   · **默认永久**（偏好不写 = 一份都不删）——删老师的板书笔记必须有明确授权；
///   · 只清两类**缓存**：`Ppt\{课件}\`（下次进放映会按需重读的批注）与
///     `Backup\*.inkb`（"打开前备份"，本来就只留最近 5 份）；
///   · **永不动"当前板书"**：`autosave.ink` / `inkteach-session.ink` 根本不在上面
///     两个目录里——"不在扫描范围"就是它们最可靠的保护，不靠判断。
/// </summary>
internal static class InkHistory
{
    /// <summary>保留期偏好（`ui.historyDays`）→ 天数；0 = 永久。</summary>
    public static int RetentionDays(string pref) => pref switch
    {
        "90" => 90,
        "30" => 30,
        "7" => 7,
        _ => 0,
    };

    /// <summary>
    /// 清一次。返回（删掉的文件数、删掉的空目录数）。
    /// 时间基准用**最后写盘时间**（UTC），和用户"什么时候用的"一致。
    /// </summary>
    public static (int Files, int Dirs) Sweep(int days)
    {
        if (days <= 0) return (0, 0);              // 永久：一份都不删
        var cutoff = DateTime.UtcNow.AddDays(-days);
        int files = 0, dirs = 0;

        // ---- PPT 缓存：一份演示文稿一个目录 ----
        try
        {
            if (Directory.Exists(PptStore.Root))
            {
                foreach (var dir in Directory.GetDirectories(PptStore.Root))
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(dir))
                        {
                            if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;
                            File.Delete(f);
                            files++;
                        }
                        // 文件清光的课件目录也删掉（留一堆空目录会让诊断失去意义）
                        if (Directory.GetFiles(dir).Length == 0
                            && Directory.GetDirectories(dir).Length == 0)
                        {
                            Directory.Delete(dir);
                            dirs++;
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"历史清理跳过 {dir}：{ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("历史清理（PPT 缓存）失败：" + ex.Message); }

        // ---- 打开前备份 ----
        try
        {
            if (Directory.Exists(InkFileStore.BackupDir))
            {
                foreach (var f in Directory.GetFiles(InkFileStore.BackupDir, "*.inkb"))
                {
                    if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;
                    File.Delete(f);
                    files++;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("历史清理（备份）失败：" + ex.Message); }

        return (files, dirs);
    }
}
