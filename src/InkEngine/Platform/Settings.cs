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
    /// <summary>版本 2 起多了 "tools" 段（工具尺寸）；版本 1 的文件照样能读。</summary>
    public const int Version = 2;

    /// <summary>
    /// 会被记住的工具尺寸（逻辑像素）。**-1 = 没存过，用引擎默认值**——
    /// 用哨兵值而不是"存 0 也算"，是因为 0 宽度的笔没有意义，
    /// 拿它当"没设过"会和"真的设成 0"混淆。
    ///
    /// 记这些的理由：老师在投影上试了几次才挑中的粗细，第二天上课还要再挑一遍
    /// 会很烦。这也是主流批注软件的通行做法（尺寸属于"我的习惯"）。
    /// </summary>
    internal sealed class ToolSettings
    {
        public float PenWidth = -1f;
        public float HighlighterWidth = -1f;
        public float LaserWidth = -1f;
        public float EraserRadius = -1f;
        /// <summary>面积橡皮的矩形**高度**（逻辑像素）。竖矩形，宽度由黄金比算出来。</summary>
        public float AreaEraserHeight = -1f;

        /// <summary>改过没有（退出时据此决定要不要落盘）。</summary>
        public bool Dirty;
    }

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
    public static List<string> Load(KeyMap map, ToolSettings tools = null)
    {
        var warnings = new List<string>();
        string path = FilePath;
        if (!File.Exists(path)) return warnings;

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { warnings.Add($"读不了配置文件 {path}：{ex.Message}"); return warnings; }

        string keysBody = SectionBody(text, "keys");
        if (keysBody == null) warnings.Add("配置文件里没有 \"keys\" 段，按默认键位运行");

        foreach (var (key, value, bad) in Pairs(keysBody ?? ""))
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

        if (tools != null) LoadTools(SectionBody(text, "tools"), tools, warnings);
        return warnings;
    }

    /// <summary>
    /// 读 "tools" 段。认不出的键名要报警告（用户手改过、或者版本对不上），
    /// 数值读不出来就退回"没设过"——**坏配置不许阻塞启动**，这是这个文件一贯的规矩。
    /// </summary>
    private static void LoadTools(string body, ToolSettings t, List<string> warnings)
    {
        if (body == null) return;
        foreach (var (key, value, bad) in Pairs(body))
        {
            if (bad != null) { warnings.Add(bad); continue; }
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v)
                || !(v > 0f) || v > 4000f)
            {
                warnings.Add($"“{key}”的数值“{value}”不合理（要在 0~4000 之间），这一项用默认值");
                continue;
            }
            switch (key.ToLowerInvariant())
            {
                case "penwidth": t.PenWidth = v; break;
                case "highlighterwidth": t.HighlighterWidth = v; break;
                case "laserwidth": t.LaserWidth = v; break;
                case "eraserradius": t.EraserRadius = v; break;
                case "areaeraserheight": t.AreaEraserHeight = v; break;
                // 兼容旧文件里写过的 "AreaEraserWidth"：那时候矩形是横的，
                // 值按宽度存的，直接当高度用会大一圈；这里按"旧宽度 × 1.618"
                // 折算成新高度，用户上一版调好的手感能接着用。
                case "areaeraserwidth": t.AreaEraserHeight = v * 1.618f; break;
                default: warnings.Add($"认不出这一项：“{key}”（工具尺寸段只认工具名，例如 AreaEraserHeight）"); break;
            }
        }
    }

    /// <summary>
    /// 取出某个段的大括号内容。**必须按段取**：整个文件里有两段，
    /// 不分段的话 "keys" 的解析会把 "tools" 的键名也读进来（然后报一堆
    /// "认不出这一项"的假警告）。
    /// </summary>
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

    /// <summary>把"和默认不一样"的键位写进配置文件。只写差异，理由见类注释。</summary>
    public static void Save(KeyMap map, ToolSettings tools = null)
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
            sb.AppendLine("  },");

            // ---- 工具尺寸 ----
            // 只写"和引擎默认不一样"的那些，理由和键位一样：以后默认值一改，
            // 老配置文件不会把新默认顶掉。
            sb.AppendLine("  \"tools\": {");
            var lines = new List<string>();
            void One(string name, float value, float engineDefault)
            {
                if (tools == null || value <= 0f) return;
                if (Math.Abs(value - engineDefault) < 0.001f) return;
                lines.Add($"    \"{name}\": {value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            One("PenWidth", tools?.PenWidth ?? -1f, 3f);
            One("HighlighterWidth", tools?.HighlighterWidth ?? -1f, 18f);
            One("LaserWidth", tools?.LaserWidth ?? -1f, 4f);
            One("EraserRadius", tools?.EraserRadius ?? -1f, 10f);
            One("AreaEraserHeight", tools?.AreaEraserHeight ?? -1f, 150f);
            for (int i = 0; i < lines.Count; i++)
                sb.AppendLine(lines[i] + (i == lines.Count - 1 ? "" : ","));
            sb.AppendLine("  }");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
            map.Dirty = false;
            if (tools != null) tools.Dirty = false;
        }
        catch (Exception ex)
        {
            // 配置写不进去不该影响使用，但必须让用户看得见
            Console.WriteLine($"设置写盘失败（{path}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 极简的 "名字": 值 扫描器。只认平铺的一层，够用且不会因为格式复杂而炸。
    /// 坏片段通过 <c>bad</c> 返回，由调用方变成一条用户能看懂的警告。
    ///
    /// 值支持两种写法：**带引号的字符串**（键位就是这种）和**不带引号的数字**
    /// （工具尺寸是这种，"AreaEraserWidth": 140 比 "140" 更像正常配置文件，
    /// 用户手改的时候也不容易把引号弄丢）。
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

            int v1 = colon + 1;
            while (v1 < body.Length && char.IsWhiteSpace(body[v1])) v1++;
            if (v1 >= body.Length) { yield return (null, null, $"“{key}”后面缺值"); yield break; }

            if (body[v1] == '"')
            {
                int v2 = body.IndexOf('"', v1 + 1);
                if (v2 < 0) { yield return (null, null, $"“{key}”的值没有收尾引号"); yield break; }
                yield return (key, body.Substring(v1 + 1, v2 - v1 - 1), null);
                i = v2 + 1;
            }
            else
            {
                int v2 = v1;
                while (v2 < body.Length && body[v2] != ',' && body[v2] != '}'
                       && body[v2] != '\n' && body[v2] != '\r') v2++;
                string raw = body.Substring(v1, v2 - v1).Trim();
                if (raw.Length == 0) { yield return (null, null, $"“{key}”后面缺值"); yield break; }
                yield return (key, raw, null);
                i = v2;
            }
        }
    }
}
