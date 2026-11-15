using System.Globalization;

namespace InkEngine;

/// <summary>
/// 文档批注的落盘：**一份文档一个目录、一个文件**（`%LOCALAPPDATA%\InkTeach\Doc\{键}\0.inkb`）。
///
/// 与 <see cref="PptStore"/> 的关系：**同构但故意分开**——
///   · 那边"一页一个文件"（每张幻灯片一套墨迹），这边墨迹**整套**，一个文件就够；
///   · 目录分开的理由是生命周期不同（文档 ≠ 演示文稿），将来单独清理/迁移都方便。
/// 原子写（先 .tmp 再换名）、读不出当没有（同 PptStore / Recovery 的规矩）。
///
/// 键：把"打开时那批文件的完整路径"排序拼接后过 FNV-1a，取 8 位十六进制——
/// 同一批文件（无论点选顺序）永远同一个键；目录名就是它（不含文件名，避免
/// 同一份文档因批次细节不同分裂成两个目录）。
/// </summary>
internal static class DocStore
{
    /// <summary>自检用：把根目录指到临时目录，**别动用户真实数据**（同 PptStore 的套路）。</summary>
    public static string RootOverride;

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkTeach", "Doc");

    /// <summary>把"打开的这批文件"算成稳定键（与顺序无关：先排序）。</summary>
    public static string KeyOf(IReadOnlyList<string> paths)
    {
        var list = new List<string>(paths?.Count ?? 0);
        if (paths != null)
            foreach (var p in paths)
            {
                try { list.Add(Path.GetFullPath(p).ToLowerInvariant()); } catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }
            }
        list.Sort(StringComparer.Ordinal);

        uint h = 2166136261;                       // FNV-1a（和 PptComSource 算 _pptKey 同一套思路）
        foreach (var p in list)
            foreach (char c in p)
            {
                h ^= (byte)c; h *= 16777619;
                h ^= (byte)(c >> 8); h *= 16777619;
            }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 文档槽的页键：**int.MinValue 起的一大段负数空间**——
    /// 避开 0（桌面页）、正数（PPT 的 SlideID）和 PPT 的负页码兜底（-1..-9999，见 Ppt.PageKeyOf）。
    /// </summary>
    public static int SlotOf(string key)
    {
        if (string.IsNullOrEmpty(key)) return int.MinValue;
        uint h = uint.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 1u;
        return int.MinValue + (int)(h & 0x3FFFFFFF);
    }

    private static string FileOf(string key) => Path.Combine(Root, key, "0.inkb");

    /// <summary>写。先写 .tmp 再换名——写一半断电不能留下一个坏文件。</summary>
    public static void Save(string key, byte[] blob)
    {
        if (string.IsNullOrEmpty(key)) return;
        var path = FileOf(key);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>读。读不出来返回 null（当作"这份文档还没有批注"）。</summary>
    public static byte[] Load(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            var path = FileOf(key);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    /// <summary>把这份文档的批注从盘上删掉（"清空文档墨迹"用——必须真的删文件，
    /// 不能指望"写一份空内容"那条间接路，理由同 PptStore.DeleteAll）。</summary>
    public static void Delete(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            var dir = Path.Combine(Root, key);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { Console.WriteLine("删文档批注失败：" + ex.Message); }
    }
}

/// <summary>
/// 「最近文档」的一条：**一次成功打开**的一组文件（图片多选算一份）＋ 打开时刻。
///
/// 放这一层而不是界面里：写入点在引擎（打开成功那一刻才知道），界面只负责显示。
/// </summary>
public sealed class RecentDoc
{
    public RecentDoc(long openedAtUnixSeconds, string[] paths)
    {
        OpenedAtUnixSeconds = openedAtUnixSeconds;
        Paths = paths ?? Array.Empty<string>();
    }

    /// <summary>打开时刻（Unix 秒，UTC）。显示时再转本地时间。</summary>
    public long OpenedAtUnixSeconds { get; }

    /// <summary>这次打开的文件（图片多选就是整组；顺序 = 用户选的顺序）。</summary>
    public string[] Paths { get; }

    /// <summary>主文件：列表里显示的名字、去重/分类都看它。</summary>
    public string PrimaryPath => Paths.Length > 0 ? Paths[0] : "";
}

/// <summary>
/// 「最近文档」的线格式（引擎写、界面读，**唯一一份**）。
///
/// 存进 `ui` 偏好的 <see cref="PrefKey"/>：
/// `时刻|转义路径|转义路径;时刻|路径…`（`;` 分条、`|` 分字段）。
///
/// 为什么不用 JSON：`InkSettings.Save` 是手写的平铺 `"键": "值"` 写入器，
/// 值里出现引号 / 反斜杠会把整份 settings.json 写坏；`Uri.EscapeDataString`
/// 顺带把中文变成 %XX，值里只剩 ASCII，永远不会和分隔符撞。
/// </summary>
public static class RecentDocs
{
    /// <summary>偏好键（`ui` 段里的一项）。</summary>
    public const string PrefKey = "recentDocs";

    /// <summary>最多留几条（界面只显示最新 3 条，多留几条是为了删文件时的兜底）。</summary>
    public const int Keep = 8;

    /// <summary>读：坏一条跳一条，绝不因为配置坏了打不开面板。</summary>
    public static List<RecentDoc> Parse(string raw)
    {
        var list = new List<RecentDoc>();
        if (string.IsNullOrEmpty(raw)) return list;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split('|');
            if (fields.Length < 2) continue;
            if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at))
                continue;

            var paths = new List<string>(fields.Length - 1);
            for (int i = 1; i < fields.Length; i++)
            {
                if (fields[i].Length == 0) continue;
                try
                {
                    string p = Uri.UnescapeDataString(fields[i]);
                    if (p.Length > 0) paths.Add(p);
                }
                catch { /* 有意忽略：一个坏路径不影响其它条目（ADR-005 D5） */ }
            }
            if (paths.Count == 0) continue;
            list.Add(new RecentDoc(at, paths.ToArray()));
        }

        // 顺序以时间戳为准（写的时候本来就从新到旧；手改坏了也不会显示乱序）。
        list.Sort((a, b) => b.OpenedAtUnixSeconds.CompareTo(a.OpenedAtUnixSeconds));
        return list.Count > Keep ? list.GetRange(0, Keep) : list;
    }

    /// <summary>写：只输出能解析回来的条目（空路径、坏字符都丢掉）。</summary>
    public static string Format(IEnumerable<RecentDoc> docs)
    {
        if (docs == null) return "";
        var parts = new List<string>();
        foreach (var d in docs)
        {
            if (d == null || d.Paths.Length == 0) continue;
            var fields = new List<string>(d.Paths.Length + 1)
            {
                d.OpenedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var p in d.Paths)
                if (!string.IsNullOrEmpty(p)) fields.Add(Uri.EscapeDataString(p));
            if (fields.Count < 2) continue;
            parts.Add(string.Join("|", fields));
            if (parts.Count >= Keep) break;
        }
        return string.Join(";", parts);
    }

    /// <summary>
    /// 把"这一次打开"插到最前：路径规范化成完整路径，**同一组文件**（与选择顺序无关）
    /// 只留一条；最多留 <see cref="Keep"/> 条。打开失败不要调它。
    /// </summary>
    public static void Push(List<RecentDoc> docs, IReadOnlyList<string> paths, long nowUnixSeconds)
    {
        if (docs == null || paths == null || paths.Count == 0) return;

        var normalized = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                string full = Path.GetFullPath(p);
                if (full.Length > 0) normalized.Add(full);
            }
            catch { /* 有意忽略：非法路径不记（打不开的那一份本来也不会成功） */ }
        }
        if (normalized.Count == 0) return;

        string key = SetKey(normalized);
        docs.RemoveAll(d => d != null && SetKey(d.Paths) == key);
        docs.Insert(0, new RecentDoc(nowUnixSeconds, normalized.ToArray()));
        if (docs.Count > Keep) docs.RemoveRange(Keep, docs.Count - Keep);
    }

    /// <summary>"同一组文件"的判据：完整路径排序后大小写不敏感（Windows 不分大小写）。</summary>
    private static string SetKey(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return "";
        var list = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            try { list.Add(Path.GetFullPath(p).ToLowerInvariant()); }
            catch { /* 有意忽略：算键失败当空（不会被误判成重复） */ }
        }
        list.Sort(StringComparer.Ordinal);
        return string.Join("\n", list);
    }

    /// <summary>
    /// 把"这一组文件"从列表里删掉（返回有没有删到）。
    /// 用在"点开一条最近文档，但其中一半文件已经没了"：实际打开的那几条会被重新记一条，
    /// 原来那条（含已删文件）删掉，列表里不会同一份文档留两行。
    /// </summary>
    public static bool RemoveSet(List<RecentDoc> docs, IReadOnlyList<string> paths)
    {
        if (docs == null || paths == null || paths.Count == 0) return false;
        string key = SetKey(paths);
        if (key.Length == 0) return false;
        return docs.RemoveAll(d => d != null && SetKey(d.Paths) == key) > 0;
    }
}
