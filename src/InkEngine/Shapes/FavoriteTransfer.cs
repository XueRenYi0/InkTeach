using System.IO.Compression;
using System.Text;

namespace InkEngine;

/// <summary>
/// **收藏的导入 / 导出**（只动 `%APPDATA%\InkTeach\Library`，和全局备份分开）。
///
/// 为什么还要单独一条：老师之间传"我攒的图形"不该连设置、历史板书一起传。
/// 导出包里带 `categories.txt`，导入时按**分类名字**合并；条目按内容指纹去重，
/// 不会因为同一张图在对方机器上再存一份。
/// </summary>
internal static class FavoriteTransfer
{
    public const string ManifestName = "InkTeachFavorites.txt";
    public const string FormatVersion = "1";

    public static bool Export(string zipPath, out int files, out long bytes, out string error)
    {
        files = 0;
        bytes = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(zipPath)) { error = "没有选择保存位置"; return false; }
        string source = ShapeLibrary.Dir;
        if (!Directory.Exists(source)) { error = "收藏还是空的"; return false; }

        string part = zipPath + ".part";
        try
        {
            string parent = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            try { if (File.Exists(part)) File.Delete(part); } catch { }

            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var manifest = zip.CreateEntry(ManifestName, CompressionLevel.NoCompression);
                using (var w = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                {
                    w.WriteLine("format=" + FormatVersion);
                    w.WriteLine("created=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                }

                zip.CreateEntry("library/", CompressionLevel.NoCompression);
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                        string rel = Path.GetRelativePath(source, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, "library/" + rel, CompressionLevel.Optimal);
                        files++;
                    }
                    catch (Exception ex) { Console.WriteLine("[收藏] 导出跳过：" + ex.Message); }
                }
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

    public static bool Import(string zipPath, out ShapeLibrary.ImportResult result, out string error)
    {
        result = new ShapeLibrary.ImportResult();
        error = null;
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            error = "收藏包不存在";
            return false;
        }

        string temp = Path.Combine(Path.GetTempPath(), "InkTeachFavorites-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            string libraryDir = Path.Combine(temp, "library");
            Directory.CreateDirectory(libraryDir);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = zip.GetEntry(ManifestName);
                if (manifest == null) { error = "这不是收藏包（缺少 " + ManifestName + "）"; return false; }
                using (var reader = new StreamReader(manifest.Open()))
                {
                    if (!reader.ReadToEnd().Contains("format=" + FormatVersion, StringComparison.Ordinal))
                    {
                        error = "收藏包版本不认识（需要 format=" + FormatVersion + "）";
                        return false;
                    }
                }

                string tempFull = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name == ManifestName) continue;
                    if (!name.StartsWith("library/", StringComparison.Ordinal))
                    {
                        error = "收藏包里有不认识的路径：" + name;
                        return false;
                    }
                    string dest = Path.GetFullPath(Path.Combine(temp, name));
                    if (!dest.StartsWith(tempFull, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "收藏包路径非法：" + name;
                        return false;
                    }
                    if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                    string parent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            string catFile = Path.Combine(libraryDir, "categories.txt");
            if (File.Exists(catFile))
            {
                foreach (string raw in File.ReadAllLines(catFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    string[] p = line.Split('|');
                    if (p.Length >= 2) categories[p[0].Trim()] = p[1].Trim();
                }
            }

            result = ShapeLibrary.ImportFrom(libraryDir, categories);
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
}
