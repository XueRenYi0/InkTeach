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

        string body = SectionBody(text, "keys");
        if (body == null) { warnings.Add("配置文件里没有 \"keys\" 段，按默认键位运行"); return warnings; }

        foreach (var (key, value, bad) in Pairs(body))
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

    /// <summary>
    /// 读界面自己的偏好（`ui` 段）。引擎**不解释**这些值：它只是"界面说存什么就存什么"，
    /// 语义（深色主题、贴边隐藏、档位、钉住）归界面层。所以这里是一张平铺的字符串表，
    /// 没有类型、没有枚举——引擎不该知道"极简档"是什么东西。
    ///
    /// 和键位一样：文件坏了、段缺了、值读不出来，都只是**警告**，不许影响启动。
    /// </summary>
    public static List<string> LoadUiPrefs(Dictionary<string, string> prefs)
    {
        var warnings = new List<string>();
        string path = FilePath;
        if (!File.Exists(path)) return warnings;

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { warnings.Add($"读不了配置文件 {path}：{ex.Message}"); return warnings; }

        string body = SectionBody(text, "ui");
        if (body == null) return warnings;          // 没有 ui 段很正常（老文件都没有）

        foreach (var (key, value, bad) in Pairs(body))
        {
            if (bad != null) { warnings.Add("界面偏好：" + bad); continue; }
            prefs[key] = value;
        }
        return warnings;
    }

    /// <summary>
    /// 读 `update` 段（目前只有 `url`）。**没有更新源是完全正常的状态**（默认就是没有），
    /// 所以这里读不到就返回 null，不产生任何警告。
    /// </summary>
    public static string LoadUpdateUrl()
    {
        string path = FilePath;
        if (!File.Exists(path)) return null;
        try
        {
            string body = SectionBody(File.ReadAllText(path), "update");
            if (body == null) return null;
            foreach (var (key, value, bad) in Pairs(body))
                if (bad == null && string.Equals(key, "url", StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch { }
        return null;
    }

    /// <summary>取出某个段的正文（不含最外层大括号）。段不存在或大括号不配对就返回 null。</summary>
    private static string SectionBody(string text, string name)
    {
        int i = text.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        int open = text.IndexOf('{', i);
        if (open < 0) return null;

        int depth = 0;
        for (int k = open; k < text.Length; k++)
        {
            if (text[k] == '{') depth++;
            else if (text[k] == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(open + 1, k - open - 1);
            }
        }
        return null;
    }

    /// <summary>
    /// 把"和默认不一样"的键位、以及界面偏好写进配置文件。
    /// 键位只写差异（理由见类注释）；界面偏好是引擎原样存下来的，界面那边只放"和默认不一样"的项。
    /// </summary>
    public static void Save(KeyMap map, IReadOnlyDictionary<string, string> uiPrefs = null,
                            string updateUrl = null)
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

            if (uiPrefs != null && uiPrefs.Count > 0)
            {
                sb.AppendLine(",");
                sb.AppendLine("  \"ui\": {");
                int n = 0;
                foreach (var kv in uiPrefs)
                {
                    sb.Append($"    \"{kv.Key}\": \"{kv.Value}\"");
                    sb.AppendLine(++n == uiPrefs.Count ? "" : ",");
                }
                sb.AppendLine("  }");
            }

            // 自动更新的来源。**留空就不写这一段**（没配更新源是正常状态）。
            // ⚠ 注意要"原样带回去"：这个函数是整文件重写，不写这一段就会把用户填的
            //   更新源抹掉（键位那一段有同样的坑，所以那边只写差异、这边是整段透传）。
            if (!string.IsNullOrWhiteSpace(updateUrl))
            {
                sb.AppendLine(",");
                sb.AppendLine("  \"update\": {");
                sb.AppendLine($"    \"url\": \"{updateUrl.Trim()}\"");
                sb.AppendLine("  }");
            }
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
