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
                try { list.Add(Path.GetFullPath(p).ToLowerInvariant()); } catch { }
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
