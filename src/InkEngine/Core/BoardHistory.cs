using System.Globalization;

namespace InkEngine;

/// <summary>
/// **板书历史**（2026-10-14 加、2026-10-16 改槽）：把**白板槽**按时段留一份快照到本地。
/// 默认**不显示、不自动恢复**；老师自己去「更多 → 墨迹 → 历史板书」找回来。
///
/// 三条硬规矩（用户拍板）：
///   · **只记白板槽**（2026-10-16 起白板独立；桌面批注不保存、不进历史）——PPT 每页、
///     文档每份都有自己的按文件缓存
///     （PptStore / DocStore，重开同一份课件就回来），重复存没意义，还会跟它们
///     "清除全部墨迹连盘删"的语义打架；
///   · **图片一律不要**（截图那种对象整条跳过）——所以一份很小：纯手写一节课 ≈ 0.1~3 MB；
///   · 上限 **总量 1 GB**（**份数不限**，超了从最老的删）；设置里「历史清理」的保留天数
///     照旧可以叠加（启动时扫，见 <see cref="InkHistory"/>）。
///
/// 一次运行 = 一份：同一个文件覆盖写（不是每 15 秒新建一份），文件名带首次写盘的时间。
/// </summary>
internal static class BoardHistory
{
    /// <summary>自检用：把目录指到临时目录（同 Recovery / InkFileStore 的套路）。</summary>
    public static string DirOverride;

    public static string Dir => DirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkTeach", "History");

    /// <summary>文件名前缀（老师自己进目录看时一眼认得出这是板书快照）。</summary>
    public const string FilePrefix = "板书-";

    /// <summary>
    /// 总量上限：**1 GB**（2026-10-14 用户定："把总量调为 1G，份数不限制"）。
    /// 不是 const：自检要临时调小它来验轮转（不然得真写 1 GB）。
    /// </summary>
    public static long MaxBytes = 1024L * 1024 * 1024;

    private static volatile string _sessionPath;   // 这一轮快照的文件（懒建；volatile：后台线程也会读）

    /// <summary>这一轮已经写过快照吗（退出时判断"空板书要不要留一份空的"）。</summary>
    public static bool HasSession => _sessionPath != null;

    /// <summary>懒建这一轮的文件名（主线程建；失败抛异常由调用方接）。</summary>
    private static string EnsureSessionPath()
    {
        Directory.CreateDirectory(Dir);
        _sessionPath ??= Path.Combine(Dir,
            FilePrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".inkb");
        return _sessionPath;
    }

    /// <summary>现在有几份（界面置灰 / 自检用）。</summary>
    public static int Count
    {
        get
        {
            try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.inkb").Length : 0; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// 写一次快照（**同步**：写完才返回）。同一个运行周期覆盖同一个文件
    /// （文件名 = 首次写盘那一刻的时间）。写成功后轮转（总量超上限就从最老的删）。
    /// 2026-10-15：写盘统一走 `BackgroundSaver` 的阻塞模式——和"周期写"排同一条队列，
    /// 保证顺序（旧内容不会盖掉后写的新内容）。返回落盘路径；失败返回 null。
    /// </summary>
    public static string Save(byte[] blob)
    {
        if (blob == null || blob.Length == 0) return null;
        try
        {
            string path = EnsureSessionPath();
            if (!BackgroundSaver.WriteBlocking("板书历史", path, blob)) return null;
            Enforce();
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine("板书历史写盘失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 后台写一份快照（2026-10-15 加；**周期快照走这条**）：不阻塞主线程——
    /// 慢盘/杀软扫描下的一次落盘顿挫就此消掉。写成功后由后台线程顺手轮转（Enforce）。
    /// 返回落盘路径（**排队成功即返回**，不代表已写完）；失败（目录建不出来）返回 null。
    /// </summary>
    public static string SaveDeferred(byte[] blob)
    {
        if (blob == null || blob.Length == 0) return null;
        try
        {
            string path = EnsureSessionPath();
            BackgroundSaver.Post("板书历史", path, blob, Enforce);
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine("板书历史写盘失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>下次运行另起一份（退出时调；自检也用它复位）。</summary>
    public static void CloseSession() => _sessionPath = null;

    /// <summary>
    /// 轮转：总量超过 <see cref="MaxBytes"/> 就从**最老的**开始删（份数不限）。
    /// 只动历史目录里的 `*.inkb`，**绝不碰别的目录**（"不在扫描范围"就是最可靠的保护）。
    /// </summary>
    public static void Enforce()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new List<FileInfo>();
            long total = 0;
            foreach (var f in Directory.GetFiles(Dir, "*.inkb"))
            {
                try { var fi = new FileInfo(f); files.Add(fi); total += fi.Length; }
                catch { }
            }
            if (total <= MaxBytes) return;

            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));   // 最老的在前
            foreach (var f in files)
            {
                if (total <= MaxBytes) break;
                if (_sessionPath != null
                    && string.Equals(f.FullName, _sessionPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { total -= f.Length; f.Delete(); } catch { }
            }
        }
        catch (Exception ex) { Console.WriteLine("板书历史轮转失败（不影响使用）：" + ex.Message); }
    }

    /// <summary>列出现有的快照（最新的在前；界面 / 自检用）。</summary>
    public static List<(string Path, DateTime Time, long Bytes)> List()
    {
        var list = new List<(string Path, DateTime Time, long Bytes)>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.GetFiles(Dir, "*.inkb"))
            {
                try { var fi = new FileInfo(f); list.Add((fi.FullName, fi.LastWriteTime, fi.Length)); }
                catch { }
            }
            list.Sort((a, b) => b.Time.CompareTo(a.Time));
        }
        catch { }
        return list;
    }
}
