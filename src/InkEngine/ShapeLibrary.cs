using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InkEngine;

/// <summary>
/// **收藏库**（2026-10-25 从"图形库"升级；用户定的名字是「收藏」，内部类名先不动）。
///
/// 一条收藏 = 一个 `.ink`（对象存档）＋ 一个 `.meta`（标题 / 分类 / 指纹 / 时间）。
/// 两样都在 `%APPDATA%\InkTeach\Library\` 根下：
///   · 教室机上程序目录往往只读，所以**绝不放程序目录**；
///   · 分类是**一条目一个分类**（meta 里的 `category`），不是文件夹树；
///     这样同一张图不可能同时出现在两个分类里，和用户 2026-10-25 的口径一致；
///   · 全局备份只要打包这个目录即可（PDF/PPT 不是我们的，不进这里）。
///
/// 去重靠 <see cref="CollectionFingerprint"/>：同一张图 / 同一个图标 / 同一段墨迹
/// 再收时**不写新文件**，直接返回已有的那条。旧 `.ink` 没有 meta 时会在第一次
/// 扫描时补一份（分类=未分类），不移动、不删除用户原来的文件。
/// </summary>
internal static class ShapeLibrary
{
    /// <summary>自检用：把图库目录指到临时目录，别动用户真正的收藏（同 <see cref="InkSettings.PathOverride"/>）。</summary>
    public static string DirOverride;

    /// <summary>图库目录。没有就建（存的时候建；列的时候没有＝空库）。</summary>
    public static string Dir => DirOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InkTeach", "Library");

    /// <summary>条目文件后缀。挑一个和"页面存档"能区分开的，方便用户自己进目录看/备份。</summary>
    public const string FileExt = ".ink";
    /// <summary>条目边车（标题 / 分类 / 指纹）。和 `.ink` 同名，只换后缀。</summary>
    public const string MetaExt = ".meta";

    /// <summary>系统分类：未分类。不能删、不能改名。</summary>
    public const string Uncategorized = "uncategorized";
    public const string UncategorizedName = "未分类";

    /// <summary>回收站：不是普通分类，不出现在分类表里；条目在这里只是"暂存"。</summary>
    public const string TrashCategory = "__trash";
    public const string TrashName = "回收站";

    private const string CategoriesFile = "categories.txt";

    // ---- 分类 ---------------------------------------------------------------

    public sealed class Category
    {
        public string Id;
        public string Name;
        public int Order;
        /// <summary>常用分类：页签排在最前、名字前带星；用户可自己标记。</summary>
        public bool Pinned;
    }

    /// <summary>分类表（旧的/没有文件时只有「未分类」）。</summary>
    public static List<Category> ListCategories()
    {
        var list = new List<Category>();
        try
        {
            string file = Path.Combine(Dir, CategoriesFile);
            if (!File.Exists(file))
            {
                list.Add(new Category { Id = Uncategorized, Name = UncategorizedName, Order = 0 });
                return list;
            }
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] p = line.Split('|');
                if (p.Length < 3) continue;
                string id = p[0].Trim();
                string name = p[1].Trim();
                if (id.Length == 0 || name.Length == 0) continue;
                _ = int.TryParse(p[2].Trim(), out int order);
                bool pinned = p.Length > 3 && (p[3].Trim() == "1" || p[3].Trim() == "true");
                list.Add(new Category { Id = id, Name = name, Order = order, Pinned = pinned });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 读分类失败：" + ex.Message);
        }
        if (!list.Exists(c => c.Id == Uncategorized))
            list.Insert(0, new Category { Id = Uncategorized, Name = UncategorizedName, Order = 0 });
        list.Sort((a, b) =>
        {
            if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
            return a.Order != b.Order ? a.Order.CompareTo(b.Order)
                                       : string.CompareOrdinal(a.Id, b.Id);
        });
        return list;
    }

    private static void WriteCategories(List<Category> list)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            foreach (var c in list)
                sb.Append(c.Id).Append('|').Append(Sanitize(c.Name)).Append('|')
                  .Append(c.Order.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(c.Pinned ? "1" : "0").Append('\n');
            File.WriteAllText(Path.Combine(Dir, CategoriesFile), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 写分类失败：" + ex.Message);
        }
    }

    /// <summary>新建分类（v1 由设置/收藏面板调用；名字先允许自动"第一类/第二类"）。</summary>
    public static Category CreateCategory(string name)
    {
        var list = ListCategories();
        int order = 1;
        foreach (var c in list) if (c.Order >= order) order = c.Order + 1;
        var cat = new Category
        {
            Id = "cat_" + Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(name) ? $"第{ChineseNumber(order)}类" : Sanitize(name.Trim()),
            Order = order,
            Pinned = false,
        };
        list.Add(cat);
        WriteCategories(list);
        return cat;
    }

    /// <summary>标记 / 取消「常用分类」。常用分类排最前，名字/顺序/内容都不变。</summary>
    public static bool SetCategoryPinned(string id, bool pinned)
    {
        if (string.IsNullOrEmpty(id) || id == Uncategorized || id == TrashCategory) return false;
        var list = ListCategories();
        int idx = list.FindIndex(c => c.Id == id);
        if (idx < 0) return false;
        if (list[idx].Pinned == pinned) return true;
        list[idx].Pinned = pinned;
        WriteCategories(list);
        return true;
    }

    /// <summary>删分类只把条目移到未分类，**绝不删条目**。</summary>
    public static bool DeleteCategory(string id)
    {
        if (string.IsNullOrEmpty(id) || id == Uncategorized) return false;
        var list = ListCategories();
        int idx = list.FindIndex(c => c.Id == id);
        if (idx < 0) return false;
        list.RemoveAt(idx);
        WriteCategories(list);
        foreach (var e in List())
            if (e.CategoryId == id) Move(e, Uncategorized);
        return true;
    }

    /// <summary>改分类名；空名 / 「未分类」「回收站」拒绝。只改名字，不动条目。</summary>
    public static bool RenameCategory(string id, string name)
    {
        if (string.IsNullOrEmpty(id) || id == Uncategorized || id == TrashCategory) return false;
        string clean = Sanitize((name ?? "").Trim());
        if (clean.Length == 0) return false;
        var list = ListCategories();
        int idx = list.FindIndex(c => c.Id == id);
        if (idx < 0) return false;
        if (string.Equals(list[idx].Name, clean, StringComparison.Ordinal)) return true;
        list[idx].Name = clean;
        WriteCategories(list);
        return true;
    }

    /// <summary>拖动页签排序：把分类插到 beforeId 前（null = 末尾）；「未分类」永远第一。</summary>
    public static bool ReorderCategory(string id, string beforeId)
    {
        if (string.IsNullOrEmpty(id) || id == Uncategorized || id == beforeId) return false;
        var list = ListCategories();
        int from = list.FindIndex(c => c.Id == id);
        if (from < 0) return false;
        var cat = list[from];
        // 拖到「常用」区（目标页签带星）= 顺手标记常用；拖到普通区 = 取消。
        bool pin = false;
        if (!string.IsNullOrEmpty(beforeId))
        {
            var before = list.Find(c => c.Id == beforeId);
            if (before != null) pin = before.Pinned;
        }
        cat.Pinned = pin;
        list.RemoveAt(from);
        int idx = string.IsNullOrEmpty(beforeId) ? list.Count : list.FindIndex(c => c.Id == beforeId);
        if (idx < 0) idx = list.Count;
        if (idx < 1) idx = 1;
        list.Insert(idx, cat);
        for (int i = 0; i < list.Count; i++) list[i].Order = i;
        WriteCategories(list);
        return true;
    }

    /// <summary>导入一个"收藏导出目录"（里面是 .ink + .meta）。</summary>
    public sealed class ImportResult
    {
        public int Imported;
        public int Duplicates;
        public int Failed;
    }

    /// <summary>
    /// 从另一个收藏目录导入：**按内容指纹去重**，分类按名字合并（没有就新建）。
    /// 每条都换新 id/文件名，绝不覆盖现有条目；回收站里的东西不导入。
    /// </summary>
    public static ImportResult ImportFrom(string sourceDir, Dictionary<string, string> categoryNames)
    {
        var result = new ImportResult();
        try
        {
            if (!Directory.Exists(sourceDir)) return result;
            Directory.CreateDirectory(Dir);

            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in ListCategories()) byName[c.Name] = c.Id;

            foreach (string ink in Directory.GetFiles(sourceDir, "*" + FileExt))
            {
                try
                {
                    var meta = ReadMeta(MetaPathOf(ink));
                    if (meta != null && meta.InTrash) { result.Failed++; continue; }

                    var strokes = Load(ink);
                    if (strokes == null || strokes.Count == 0) { result.Failed++; continue; }
                    string fp = CollectionFingerprint.Of(strokes);
                    if (FindByFingerprint(fp) != null) { result.Duplicates++; continue; }

                    string catName = UncategorizedName;
                    if (meta != null && categoryNames != null)
                    {
                        if (categoryNames.TryGetValue(meta.CategoryId, out string n)
                            && !string.IsNullOrWhiteSpace(n))
                            catName = n.Trim();
                    }

                    string catId;
                    if (catName == UncategorizedName) catId = Uncategorized;
                    else if (byName.TryGetValue(catName, out string existing)) catId = existing;
                    else
                    {
                        var created = CreateCategory(catName);
                        byName[catName] = created.Id;
                        catId = created.Id;
                    }

                    string id = Guid.NewGuid().ToString("N");
                    string dest = Path.Combine(Dir, id + FileExt);
                    File.Copy(ink, dest);
                    var e = new Entry
                    {
                        Path = dest,
                        Id = id,
                        CategoryId = catId,
                        CategoryName = catName,
                        Fingerprint = fp,
                        Title = string.IsNullOrWhiteSpace(meta?.Title) ? AutoTitle(strokes) : meta.Title,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Order = NextTopOrder(),
                        Objects = strokes.Count,
                        Bytes = new FileInfo(dest).Length,
                    };
                    WriteMeta(e);
                    result.Imported++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    Console.WriteLine("[收藏] 导入失败：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 导入目录失败：" + ex.Message);
        }
        return result;
    }

    /// <summary>把一条收藏移到另一个分类（同一个条目只在一个分类里）。</summary>
    public static bool Move(Entry entry, string categoryId)
    {
        if (entry == null) return false;
        if (categoryId == TrashCategory) return Trash(entry);
        if (ListCategories().FindIndex(c => c.Id == categoryId) < 0) categoryId = Uncategorized;
        entry.CategoryId = categoryId;
        entry.CategoryName = CategoryNameOf(categoryId);
        entry.PreviousCategory = null;
        entry.UpdatedAt = DateTime.UtcNow;
        entry.Order = NextTopOrder();       // 移入分类 = 排最前
        WriteMeta(entry);
        return true;
    }

    /// <summary>下一个“排最前”的 order（当前最小值 −1）。</summary>
    private static double NextTopOrder()
    {
        bool any = false;
        double min = 0;
        foreach (var e in List(includeTrash: true))
        {
            if (!any || e.Order < min) { min = e.Order; any = true; }
        }
        return any ? min - 1 : 0;
    }

    /// <summary>把一组条目插到 <paramref name="before"/> 前面（null = 末尾），并重排 order。</summary>
    public static bool ReorderBefore(IReadOnlyList<Entry> moving, Entry before)
    {
        if (moving == null || moving.Count == 0) return false;
        var all = List(includeTrash: true);
        var byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all) byPath[e.Path] = e;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<Entry>();
        foreach (var e in moving)
            if (e?.Path != null && ids.Add(e.Path) && byPath.TryGetValue(e.Path, out var actual))
                picked.Add(actual);
        if (picked.Count == 0) return false;
        var rest = new List<Entry>();
        foreach (var e in all) if (!ids.Contains(e.Path)) rest.Add(e);
        int idx = before == null ? rest.Count : rest.FindIndex(e => e.Path == before.Path);
        if (idx < 0) idx = rest.Count;
        rest.InsertRange(idx, picked);
        for (int i = 0; i < rest.Count; i++)
            if (Math.Abs(rest[i].Order - i) > 0.0001)
            {
                rest[i].Order = i;
                WriteMeta(rest[i]);
            }
        return true;
    }

    /// <summary>把一组条目排到最前（组内保持调用方给的相对顺序）。</summary>
    public static bool ReorderToTop(IReadOnlyList<Entry> moving)
    {
        if (moving == null || moving.Count == 0) return false;
        var all = List(includeTrash: true);
        var byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all) byPath[e.Path] = e;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<Entry>();
        foreach (var e in moving)
            if (e?.Path != null && ids.Add(e.Path) && byPath.TryGetValue(e.Path, out var actual))
                picked.Add(actual);
        if (picked.Count == 0) return false;
        var rest = new List<Entry>();
        foreach (var e in all) if (!ids.Contains(e.Path)) rest.Add(e);
        rest.InsertRange(0, picked);
        for (int i = 0; i < rest.Count; i++)
            if (Math.Abs(rest[i].Order - i) > 0.0001)
            {
                rest[i].Order = i;
                WriteMeta(rest[i]);
            }
        return true;
    }

    /// <summary>软删除：条目进回收站，文件还在；恢复时回原分类。</summary>
    public static bool Trash(Entry entry)
    {
        if (entry == null || entry.InTrash) return false;
        entry.PreviousCategory = entry.CategoryId;
        entry.CategoryId = TrashCategory;
        entry.CategoryName = TrashName;
        entry.UpdatedAt = DateTime.UtcNow;
        WriteMeta(entry);
        return true;
    }

    /// <summary>从回收站恢复；原分类没了就回未分类。</summary>
    public static bool Restore(Entry entry)
    {
        if (entry == null || !entry.InTrash) return false;
        string back = entry.PreviousCategory;
        if (string.IsNullOrEmpty(back) || ListCategories().FindIndex(c => c.Id == back) < 0)
            back = Uncategorized;
        entry.CategoryId = back;
        entry.CategoryName = CategoryNameOf(back);
        entry.PreviousCategory = null;
        entry.UpdatedAt = DateTime.UtcNow;
        WriteMeta(entry);
        return true;
    }

    /// <summary>清空回收站（永久删除）。返回删了几个。</summary>
    public static int EmptyTrash()
    {
        int n = 0;
        foreach (var e in List(includeTrash: true))
            if (e.InTrash && Delete(e.Path)) n++;
        return n;
    }

    internal static string CategoryNameOf(string id)
    {
        foreach (var c in ListCategories()) if (c.Id == id) return c.Name;
        return UncategorizedName;
    }

    private static bool IsKnownCategory(string id)
        => id == Uncategorized || ListCategories().Exists(c => c.Id == id);

    // ---- 条目 ---------------------------------------------------------------

    /// <summary>图库里的一个条目：一个 `.ink` ＋ 它装的那批对象 ＋ 边车元数据。</summary>
    public sealed class Entry
    {
        public string Path;
        public string Id;
        public string CategoryId = Uncategorized;
        public string CategoryName = UncategorizedName;
        /// <summary>进回收站之前所在的分类（恢复时用；普通条目为空）。</summary>
        public string PreviousCategory;
        public string Fingerprint;
        public string Title;
        public DateTime CreatedAt;
        public DateTime UpdatedAt;
        /// <summary>手动排序用的顺序；越小越靠前。新收藏/拖入分类都取当前最小值 −1。</summary>
        public double Order;
        public int Uses;
        public int Objects;
        public long Bytes;
        /// <summary>内容。去重命中时可能为 null（只拿分类/标题，不读整份对象）。</summary>
        public List<Stroke> Strokes;
        public bool InTrash => CategoryId == TrashCategory;
    }

    public sealed class SaveResult
    {
        public Entry Entry;
        /// <summary>true = 命中已有内容，**没有写新文件**。</summary>
        public bool Duplicate;
        public bool Success => Entry != null;
    }

    /// <summary>「用画布上当前选中的内容更新收藏」的结果。</summary>
    public sealed class ReplaceResult
    {
        public Entry Entry;
        public bool Success;
        /// <summary>true = 新内容和**另一条**收藏重复了（没写）。</summary>
        public bool Duplicate;
        public Entry DuplicateEntry;
        /// <summary>true = 内容一点没变（不写盘）。</summary>
        public bool Unchanged;
        public string Error;
    }

    /// <summary>
    /// 用一批新对象**覆盖**已有收藏的内容（分类、标题不动；内容指纹/对象数/字节数更新）。
    ///
    /// 去重只排除"它自己"：改完以后如果和**另一条**收藏撞内容，宁可拒绝也不制造重复。
    /// 先写 `.tmp` 再换名，写到一半断电不会留下半个 `.ink`。
    /// </summary>
    public static ReplaceResult Replace(Entry entry, IReadOnlyList<Stroke> items)
    {
        var result = new ReplaceResult { Entry = entry };
        if (entry == null || entry.InTrash) { result.Error = "这条收藏不能更新"; return result; }
        if (items == null || items.Count == 0) { result.Error = "画布上没有选中的内容"; return result; }

        try
        {
            string fingerprint = CollectionFingerprint.Of(items);
            if (string.IsNullOrEmpty(fingerprint)) { result.Error = "内容识别失败"; return result; }
            if (fingerprint == entry.Fingerprint) { result.Unchanged = true; return result; }

            var existing = FindByFingerprint(fingerprint);
            if (existing != null && !string.Equals(existing.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                result.Duplicate = true;
                result.DuplicateEntry = existing;
                return result;
            }

            byte[] bytes = ClipboardInk.Serialize(items);
            if (bytes == null || bytes.Length == 0) { result.Error = "内容序列化失败"; return result; }

            string tmp = entry.Path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, entry.Path, overwrite: true);

            entry.Fingerprint = fingerprint;
            entry.Objects = items.Count;
            entry.Bytes = bytes.Length;
            entry.UpdatedAt = DateTime.UtcNow;
            entry.Title = AutoTitle(items);
            entry.Strokes = new List<Stroke>(items);
            WriteMeta(entry);
            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// 旧接口：返回文件路径（去重命中时返回已有条目的路径）。
    /// 新代码应走 <see cref="SaveEx"/> 以便知道"是不是重复"。
    /// </summary>
    public static string Save(IReadOnlyList<Stroke> items) => SaveEx(items).Entry?.Path;

    /// <summary>
    /// 一批对象存成一个收藏条目。成功返回 <see cref="SaveResult"/>；
    /// 重复时返回已有条目且 <see cref="SaveResult.Duplicate"/> = true，不写文件。
    /// </summary>
    public static SaveResult SaveEx(IReadOnlyList<Stroke> items, string categoryId = Uncategorized,
                                    string title = null)
    {
        var result = new SaveResult();
        if (items == null || items.Count == 0) return result;
        try
        {
            string fingerprint = CollectionFingerprint.Of(items);
            if (string.IsNullOrEmpty(fingerprint)) return result;

            EnsureDir();
            if (!IsKnownCategory(categoryId)) categoryId = Uncategorized;

            var existing = FindByFingerprint(fingerprint);
            if (existing != null)
            {
                result.Entry = existing;
                result.Duplicate = true;
                return result;
            }

            byte[] bytes = ClipboardInk.Serialize(items);
            if (bytes == null || bytes.Length == 0) return result;

            string id = Guid.NewGuid().ToString("N");
            string file = Path.Combine(Dir, id + FileExt);
            File.WriteAllBytes(file, bytes);

            var e = new Entry
            {
                Path = file,
                Id = id,
                CategoryId = categoryId,
                CategoryName = CategoryNameOf(categoryId),
                Fingerprint = fingerprint,
                Title = string.IsNullOrWhiteSpace(title) ? AutoTitle(items) : Sanitize(title.Trim()),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Order = NextTopOrder(),
                Uses = 0,
                Objects = items.Count,
                Bytes = bytes.Length,
                Strokes = new List<Stroke>(items),
            };
            WriteMeta(e);
            Console.WriteLine($"收藏：存入 {items.Count} 个对象 → {e.Title}（{file}）");
            result.Entry = e;
            return result;
        }
        catch (Exception ex)
        {
            // 存不进去不是致命错（磁盘满 / 权限），报一行就好，别把正在写的板书打断
            Console.WriteLine("收藏失败：" + ex.Message);
            return result;
        }
    }

    /// <summary>
    /// 列出收藏库里所有条目（**旧的在前面**）。旧 `.ink` 没有边车时补元数据；
    /// 读不出来的文件跳过并报一行。
    /// </summary>
    public static List<Entry> List(bool includeTrash = false)
    {
        var list = new List<Entry>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            EnsureAllMeta();

            var cats = CategoryMap();
            foreach (string meta in Directory.GetFiles(Dir, "*" + MetaExt))
            {
                var e = ReadMeta(meta);
                if (e == null) continue;
                if (e.InTrash && !includeTrash) continue;
                e.Strokes = Load(e.Path);
                if (e.Strokes == null || e.Strokes.Count == 0) continue;
                e.CategoryName = e.InTrash ? TrashName
                               : cats.TryGetValue(e.CategoryId, out string name) ? name : UncategorizedName;
                list.Add(e);
            }
            list.Sort((a, b) =>
            {
                int c = a.Order.CompareTo(b.Order);              // 手动顺序：小的在前
                if (c != 0) return c;
                c = b.CreatedAt.CompareTo(a.CreatedAt);          // 老数据没有 order 时仍新的在前
                if (c != 0) return c;
                return string.CompareOrdinal(a.Id, b.Id);
            });
        }
        catch (Exception ex)
        {
            // 异常不能静默吞掉，不然"收藏空了"分不清是真没存过还是读挂了
            Console.WriteLine("[收藏] 列目录失败：" + ex.Message);
        }
        return list;
    }

    /// <summary>按指纹找已有条目（不读整份对象；命中时 <see cref="Entry.Strokes"/> = null）。</summary>
    public static Entry FindByFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint) || !Directory.Exists(Dir)) return null;
        try
        {
            EnsureAllMeta();
            foreach (string meta in Directory.GetFiles(Dir, "*" + MetaExt))
            {
                var e = ReadMeta(meta);
                if (e != null && !e.InTrash && e.Fingerprint == fingerprint) return e;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 查重失败：" + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// 读一个条目。返回的对象**身份已归零**（Id = 0）：文件里的 Id 是当时那批对象的，
    /// 直接插进文档会和现在的对象撞号，撤销/多选就会指错（同 <see cref="ClipboardInk"/>）。
    /// </summary>
    public static List<Stroke> Load(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tmp = new InkDocument();
            InkSerializer.LoadInto(tmp, bytes);
            var items = tmp.Strokes;
            foreach (var s in items) s.Id = 0;
            return items;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 读不出来（跳过）：" + path + " —— " + ex.Message);
            return null;
        }
    }

    /// <summary>永久删一个条目（回收站里勾选删除走这里）。</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            string meta = MetaPathOf(path);
            if (File.Exists(meta)) File.Delete(meta);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 删除失败：" + ex.Message);
            return false;
        }
    }

    // ---- 边车 / 迁移 --------------------------------------------------------

    private static void EnsureDir()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(Path.Combine(Dir, CategoriesFile)))
            WriteCategories(new List<Category>
            {
                new Category { Id = Uncategorized, Name = UncategorizedName, Order = 0 }
            });
    }

    /// <summary>旧 `.ink` 第一次被看到时补一份 meta；不移动、不删除原文件。</summary>
    private static void EnsureAllMeta()
    {
        foreach (string ink in Directory.GetFiles(Dir, "*" + FileExt))
        {
            string meta = MetaPathOf(ink);
            if (File.Exists(meta)) continue;
            var strokes = Load(ink);
            if (strokes == null || strokes.Count == 0) continue;
            var e = new Entry
            {
                Path = ink,
                Id = Path.GetFileNameWithoutExtension(ink),
                CategoryId = Uncategorized,
                CategoryName = UncategorizedName,
                Fingerprint = CollectionFingerprint.Of(strokes),
                Title = AutoTitle(strokes),
                CreatedAt = File.GetCreationTimeUtc(ink),
                UpdatedAt = File.GetLastWriteTimeUtc(ink),
                Order = -new DateTimeOffset(File.GetCreationTimeUtc(ink)).ToUnixTimeSeconds(),
                Objects = strokes.Count,
                Bytes = new FileInfo(ink).Length,
            };
            WriteMeta(e);
        }
    }

    private static Dictionary<string, string> CategoryMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in ListCategories()) map[c.Id] = c.Name;
        return map;
    }

    private static string MetaPathOf(string inkPath)
        => Path.ChangeExtension(inkPath, MetaExt);

    private static void WriteMeta(Entry e)
    {
        if (e == null || string.IsNullOrEmpty(e.Path)) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append("id=").Append(e.Id ?? Path.GetFileNameWithoutExtension(e.Path)).Append('\n');
            sb.Append("category=").Append(e.CategoryId ?? Uncategorized).Append('\n');
            sb.Append("prev=").Append(e.PreviousCategory ?? "").Append('\n');
            sb.Append("fingerprint=").Append(e.Fingerprint ?? "").Append('\n');
            sb.Append("title=").Append(Sanitize(e.Title ?? "")).Append('\n');
            sb.Append("created=").Append(new DateTimeOffset(e.CreatedAt).ToUnixTimeSeconds()).Append('\n');
            sb.Append("updated=").Append(new DateTimeOffset(e.UpdatedAt).ToUnixTimeSeconds()).Append('\n');
            sb.Append("order=").Append(e.Order.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("uses=").Append(e.Uses).Append('\n');
            sb.Append("objects=").Append(e.Objects).Append('\n');
            sb.Append("bytes=").Append(e.Bytes).Append('\n');
            File.WriteAllText(MetaPathOf(e.Path), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 写元数据失败：" + ex.Message);
        }
    }

    private static Entry ReadMeta(string metaPath)
    {
        try
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(metaPath))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                map[raw[..eq]] = raw[(eq + 1)..];
            }
            string ink = Path.ChangeExtension(metaPath, FileExt);
            if (!File.Exists(ink)) return null;

            var e = new Entry
            {
                Path = ink,
                Id = map.TryGetValue("id", out string id) ? id : Path.GetFileNameWithoutExtension(ink),
                CategoryId = map.TryGetValue("category", out string cat) ? cat : Uncategorized,
                PreviousCategory = map.TryGetValue("prev", out string prev) ? prev : null,
                Fingerprint = map.TryGetValue("fingerprint", out string fp) ? fp : null,
                Title = map.TryGetValue("title", out string title) ? title : null,
                Uses = map.TryGetValue("uses", out string uses) && int.TryParse(uses, out int u) ? u : 0,
                Objects = map.TryGetValue("objects", out string objs) && int.TryParse(objs, out int o) ? o : 0,
                Bytes = map.TryGetValue("bytes", out string bytes) && long.TryParse(bytes, out long b) ? b : 0,
            };
            e.CreatedAt = map.TryGetValue("created", out string created)
                          && long.TryParse(created, out long cs)
                ? DateTimeOffset.FromUnixTimeSeconds(cs).UtcDateTime
                : File.GetCreationTimeUtc(ink);
            e.UpdatedAt = map.TryGetValue("updated", out string updated)
                          && long.TryParse(updated, out long us)
                ? DateTimeOffset.FromUnixTimeSeconds(us).UtcDateTime
                : File.GetLastWriteTimeUtc(ink);
            e.Order = map.TryGetValue("order", out string order)
                      && double.TryParse(order, NumberStyles.Float, CultureInfo.InvariantCulture, out double ov)
                ? ov
                : -new DateTimeOffset(e.CreatedAt).ToUnixTimeSeconds();
            e.CategoryName = e.InTrash ? TrashName : CategoryNameOf(e.CategoryId);
            return e;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[收藏] 读元数据失败（跳过）：" + metaPath + " —— " + ex.Message);
            return null;
        }
    }

    private static string AutoTitle(IReadOnlyList<Stroke> items)
    {
        if (items == null || items.Count == 0) return "收藏";
        if (items.Count > 1) return $"{items.Count} 个对象";
        var s = items[0];
        if (s.IsImage) return "图片";
        if (s.Kind == StrokeKind.Freehand) return "墨迹";
        return ToolNames.Of(s.Tool);
    }

    private static string Sanitize(string text)
        => (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('|', '／').Trim();

    private static string ChineseNumber(int n) => n switch
    {
        1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五",
        6 => "六", 7 => "七", 8 => "八", 9 => "九", 10 => "十",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}
