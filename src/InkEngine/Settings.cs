using System.Text;

namespace InkEngine;

/// <summary>
/// 用户设置的落盘。目前只有键位，将来加别的（默认工具、默认粗细、吸附开关）也走这里。
///
/// 三个刻意的决定：
///
///   1. **只写"改过的"项**。整张表都写进去的话，以后默认键位一改，老配置文件
///      就会把新默认值顶掉——用户没动过的键反而永远停在旧版本上。
///   2. **手写解析，不引 System.Text.Json**。引擎现在的原则是"不引依赖、不靠反射"
///      （见 `InkSerializer.cs` 开头那段），配置这么小的东西不值得破例；
///      而且自己写才能做到"文件被改坏了也照常用默认值跑起来，并且告诉用户哪儿坏了"。
///   3. **放 %APPDATA%**，不是程序目录。教室机上程序目录往往是只读的
///      （装在 Program Files 或者投影仪的共享盘上），写配置会失败。
/// </summary>
internal static class InkSettings
{
    public const int Version = 1;

    /// <summary>自检用：指向临时文件，别动用户真正的配置。</summary>
    public static string PathOverride;

    public static string FilePath => PathOverride
        ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InkTeach", "settings.json");

    /// <summary>
    /// 把配置文件里的键位覆盖读进 <paramref name="map"/>。
    /// 返回的每一行都是"用户该知道的问题"（键名不认识、组合非法、文件坏了），
    /// **不是异常**：配置坏了也必须能启动，只是用默认值并提示。
    /// </summary>
    public static List<string> Load(KeyMap map)
    {
        var warnings = new List<string>();
        string path = FilePath;
        if (!File.Exists(path)) return warnings;

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { warnings.Add($"读不了配置文件 {path}：{ex.Message}"); return warnings; }

        int i = text.IndexOf("\"keys\"", StringComparison.Ordinal);
        if (i < 0) { warnings.Add("配置文件里没有 \"keys\" 段，按默认键位运行"); return warnings; }
        int open = text.IndexOf('{', i);
        if (open < 0) { warnings.Add("\"keys\" 段没有起始大括号，按默认键位运行"); return warnings; }

        int depth = 0, end = text.Length;
        for (int k = open; k < text.Length; k++)
        {
            if (text[k] == '{') depth++;
            else if (text[k] == '}')
            {
                depth--;
                if (depth == 0) { end = k; break; }
            }
        }

        foreach (var (key, value, bad) in Pairs(text.Substring(open + 1, Math.Max(0, end - open - 1))))
        {
            if (bad != null) { warnings.Add(bad); continue; }
            var parts = key.Split('.');
            if (parts.Length != 2
                || !Enum.TryParse<KeyScope>(parts[0], ignoreCase: true, out var scope)
                || !Enum.TryParse<KeyAction>(parts[1], ignoreCase: true, out var action))
            {
                warnings.Add($"认不出这一项：“{key}”（应该是 Scope.Action，例如 Annotation.Undo）");
                continue;
            }
            var b = map.Find(scope, action);
            if (b == null) { warnings.Add($"“{key}”这个动作已经不存在了，忽略"); continue; }
            if (!KeyChord.TryParse(value, out var chord, out string err))
            {
                warnings.Add($"“{key}”的按键“{value}”读不懂：{err}（这一项用默认值）");
                continue;
            }
            if (!map.TrySet(scope, action, chord, out string conflict))
            {
                warnings.Add($"“{key}”没设上：{conflict}");
                continue;
            }
            map.Dirty = false;      // 从文件读进来的不算"改动"
        }
        return warnings;
    }

    /// <summary>把"和默认不一样"的键位写进配置文件。只写差异，理由见类注释。</summary>
    public static void Save(KeyMap map)
    {
        string path = FilePath;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"version\": {Version},");
            sb.AppendLine("  \"note\": \"只写改过的键位；删掉某一项 = 恢复默认。改坏了也能启动。\",");
            sb.AppendLine("  \"keys\": {");
            var changed = map.Bindings.Where(b => !b.IsDefault).ToList();
            for (int i = 0; i < changed.Count; i++)
            {
                var b = changed[i];
                sb.Append($"    \"{b.Scope}.{b.Action}\": \"{b.Chord}\"");
                sb.AppendLine(i == changed.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
            map.Dirty = false;
        }
        catch (Exception ex)
        {
            // 配置写不进去不该影响使用，但必须让用户看得见
            Console.WriteLine($"设置写盘失败（{path}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 极简的 "名字": "值" 扫描器。只认平铺的一层，够用且不会因为格式复杂而炸。
    /// 坏片段通过 <c>bad</c> 返回，由调用方变成一条用户能看懂的警告。
    /// </summary>
    private static IEnumerable<(string key, string value, string bad)> Pairs(string body)
    {
        // 用 string 而不是 ReadOnlySpan：迭代器里不能跨 yield 保留 span。
        int i = 0;
        while (i < body.Length)
        {
            int q1 = body.IndexOf('"', i);
            if (q1 < 0) yield break;
            int q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) { yield return (null, null, "配置里有个引号没配对"); yield break; }
            string key = body.Substring(q1 + 1, q2 - q1 - 1);

            int colon = body.IndexOf(':', q2 + 1);
            if (colon < 0) { yield return (null, null, $"“{key}”后面缺冒号"); yield break; }

            int v1 = body.IndexOf('"', colon + 1);
            if (v1 < 0) { yield return (null, null, $"“{key}”后面缺值"); yield break; }
            int v2 = body.IndexOf('"', v1 + 1);
            if (v2 < 0) { yield return (null, null, $"“{key}”的值没有收尾引号"); yield break; }

            yield return (key, body.Substring(v1 + 1, v2 - v1 - 1), null);
            i = v2 + 1;
        }
    }
}
