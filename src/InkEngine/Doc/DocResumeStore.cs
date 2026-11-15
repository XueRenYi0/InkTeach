// [职责] 文档「上次看到第几页」的落盘：一文档一目录一小文本文件（跨会话恢复用）。
// [测试] --doctest（F 组）

using System.Globalization;

namespace InkEngine;

/// <summary>
/// 一份文档的阅读位置（跨会话恢复用）。
///
/// 为什么存**页内比例**而不是屏幕偏移：屏幕偏移带着缩放（`ViewOffsetY` 是
/// 相机值），同一位置在 100% / 150% 下差一倍，存下来下次一定对不上。
/// 比例 = 视口左上角落在该页里的相对位置（0..1），和缩放无关。
/// </summary>
internal sealed class DocResumePosition
{
    /// <summary>页码（1 起，指**视口顶边**所在的那一页）。</summary>
    public int Page = 1;

    /// <summary>视口顶边在页内的纵向比例（0..1，0 = 页顶）。</summary>
    public float FractionY;

    /// <summary>视口左边在页内的横向比例（0..1，0 = 页左；100% 时基本恒为 0）。</summary>
    public float FractionX;

    /// <summary>当时的文档缩放（1 = 100%；范围 0.4..2）。</summary>
    public float Zoom = 1f;

    /// <summary>写入时刻（Unix 秒，UTC）。用来区分"这次运行内刚存的"和"上次会话存的"。</summary>
    public long SavedAtUnix;

    /// <summary>
    /// 值不值得提示：不是"第 1 页页顶、100%"。阈值 0.02 ≈ 大半行字，
    /// 更小的偏移属于手抖，弹卡反而烦人。
    /// </summary>
    public bool IsMeaningful =>
        Page > 1 || FractionY > 0.02f || FractionX > 0.02f || MathF.Abs(Zoom - 1f) > 0.005f;
}

/// <summary>
/// 阅读位置的盘上格式（**和批注分开**：清空文档墨迹只删 `Doc\{键}\0.inkb`，
/// 不能把"上次看到第几页"一起删掉）。
///
/// 路径：`%LOCALAPPDATA%\InkTeach\DocResume\{键}\resume.txt`
/// 格式：`v1|page|fy|fx|zoom|at`（一行、invariant culture；坏了当没有）。
/// 原子写：先 `.tmp` 再换名，和 <see cref="DocStore.Save"/> 同一套。
/// </summary>
internal static class DocResumeStore
{
    /// <summary>自检用：把根目录指到临时目录，**别动用户真实数据**（同 DocStore）。</summary>
    public static string RootOverride;

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkTeach", "DocResume");

    /// <summary>
    /// 本进程启动时刻（Unix 秒，UTC）。启动时抓一次：跨会话提示的判据是
    /// "存档时刻**早于**这个值"——同一次运行里刚存的（≥ 它）就不弹。
    /// </summary>
    public static readonly long ProcessStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string FileOf(string key) => Path.Combine(Root, key, "resume.txt");

    /// <summary>写一份位置。先写 .tmp 再换名——写一半断电不能留下坏文件。</summary>
    public static void Save(string key, DocResumePosition pos)
    {
        if (string.IsNullOrEmpty(key) || pos == null) return;
        try
        {
            var path = FileOf(key);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string text = string.Join("|",
                "v1",
                Math.Max(1, pos.Page).ToString(CultureInfo.InvariantCulture),
                pos.FractionY.ToString("F6", CultureInfo.InvariantCulture),
                pos.FractionX.ToString("F6", CultureInfo.InvariantCulture),
                pos.Zoom.ToString("F4", CultureInfo.InvariantCulture),
                pos.SavedAtUnix.ToString(CultureInfo.InvariantCulture));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, System.Text.Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Console.WriteLine("[文档] 阅读位置写盘失败：" + ex.Message); }
    }

    /// <summary>读一份位置。读不出来 / 字段不合法 → null（当作没有）。</summary>
    public static DocResumePosition Load(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            var path = FileOf(key);
            if (!File.Exists(path)) return null;
            var fields = File.ReadAllText(path, System.Text.Encoding.UTF8).Trim().Split('|');
            if (fields.Length != 6 || fields[0] != "v1") return null;
            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)) return null;
            if (!float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float fy)) return null;
            if (!float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float fx)) return null;
            if (!float.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float zoom)) return null;
            if (!long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) return null;
            if (page < 1 || zoom < 0.2f || zoom > 4f || at <= 0) return null;
            return new DocResumePosition
            {
                Page = page,
                FractionY = Math.Clamp(fy, 0f, 1f),
                FractionX = Math.Clamp(fx, 0f, 1f),
                Zoom = zoom,
                SavedAtUnix = at,
            };
        }
        catch { return null; }
    }

    /// <summary>这一份有没有存过（自检判"清墨不删阅读位置"用）。</summary>
    public static bool Exists(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        try { return File.Exists(FileOf(key)); }
        catch { return false; }
    }

    /// <summary>删掉一份（只给自检清理用；产品里没有"删阅读位置"的入口）。</summary>
    public static void Delete(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            var dir = Path.Combine(Root, key);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { Console.WriteLine("[文档] 删阅读位置失败：" + ex.Message); }
    }
}
