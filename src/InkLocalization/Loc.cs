using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace InkLocalization;

/// <summary>
/// 一个可用的界面语言。
///
/// `Code` 用 BCP-47（zh-CN / en / ja / pt-BR…）；`InstallerName` 是 Inno Setup
/// `ActiveLanguage` 的值（安装器写进 install-language.txt 的那一份）。
/// </summary>
public sealed class LocaleInfo
{
    public string Code { get; }
    public string EnglishName { get; }
    public string NativeName { get; }
    public string FontFamily { get; }
    public string LocaleName { get; }
    public bool Rtl { get; }
    public string InstallerName { get; }

    public LocaleInfo(string code, string englishName, string nativeName,
                      string fontFamily, string localeName, bool rtl, string installerName)
    {
        Code = code;
        EnglishName = englishName;
        NativeName = nativeName;
        FontFamily = fontFamily;
        LocaleName = localeName;
        Rtl = rtl;
        InstallerName = installerName;
    }
}

/// <summary>
/// 进程级语言服务。**这是唯一的语言入口**：
///   · `Tr(text)`：把中文源文按当前语言翻译（查不到就回退英文，再回退中文）；
///   · `T(text, args)`：带占位符的版本；
///   · `FontFamily` / `LocaleName` / `SampleText`：文字格式必须跟语言走；
///   · `PrefCode` / `CurrentCode`：设置里存的是 PrefCode（可以 wildcard = system）。
///
/// 语言文件按 `Locales/&lt;code&gt;/*.json` 分文件、自动合并：
///   Locales/en/core.json
///   Locales/en/help.json      ← 以后加领域文件不用改代码
///   Locales/ja/core.json
/// 每个 JSON 的结构：
///   { "strings": { "中文源文": "Translation" } }
///
/// 为什么用中文源文当 key（gettext 风格），而不是再造一套 key：
///   · 现有 UI 全是中文硬编码，迁移时只需在绘制入口查表，不需要先给几千条文案起名；
///   · 翻译文件直接看到源文，译者/AI 都不用来回跳代码；
///   · 中文源文改动时，旧翻译自然失效并回退，不会把错译文挂到新文案上。
/// 以后要语义 key，可在 JSON 里同时保留 `keys` 段；`T` 的调用方式不变。
///
/// AOT：不依赖 .resx / satellite assemblies / 反射，资源用 EmbeddedResource 嵌入。
/// </summary>
public static class Loc
{
    public const string SourceCode = "zh-CN";
    public const string SystemCode = "system";

    // 用户 2026-11-13 定：目标语言**冻结在这 10 个**，不再新增。
    // 加语言前先问产品；不要顺手往这里塞（每加一个都要 UI/字体/排版验收）。
    private static readonly LocaleInfo[] All =
    {
        new("en",    "English",              "English",              "Segoe UI",         "en-US", false, "english"),
        new("zh-CN", "Chinese (Simplified)", "简体中文",              "Microsoft YaHei UI", "zh-CN", false, "chinese"),
        new("ja",    "Japanese",             "日本語",                "Yu Gothic UI",     "ja-JP", false, "japanese"),
        new("ko",    "Korean",               "한국어",                "Malgun Gothic",    "ko-KR", false, "korean"),
        new("de",    "German",               "Deutsch",              "Segoe UI",         "de-DE", false, "german"),
        new("fr",    "French",               "Français",             "Segoe UI",         "fr-FR", false, "french"),
        new("es",    "Spanish",              "Español",              "Segoe UI",         "es-ES", false, "spanish"),
        new("pt-BR", "Portuguese (Brazil)",  "Português (Brasil)",   "Segoe UI",         "pt-BR", false, "brazilianportuguese"),
        new("ru",    "Russian",              "Русский",              "Segoe UI",         "ru-RU", false, "russian"),
        new("it",    "Italian",              "Italiano",             "Segoe UI",         "it-IT", false, "italian"),
    };

    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _current = Empty;
    private static Dictionary<string, string> _english = Empty;
    private static readonly HashSet<string> _missing = new(StringComparer.Ordinal);

    /// <summary>设为 true 时记录所有没命中的源文（用于补翻译；不参与正常渲染）。</summary>
    public static bool AuditMissing { get; private set; }
    public static IReadOnlyCollection<string> Missing => _missing;

    /// <summary>设置里保存的值：system / en / zh-CN / ja …</summary>
    public static string PrefCode { get; private set; } = SystemCode;

    /// <summary>实际解析出来的语言（system 已经解析成具体 code）。</summary>
    public static string CurrentCode { get; private set; } = SourceCode;

    /// <summary>每次 Initialize 变化时 +1；UI 文字格式缓存可以据此失效。</summary>
    public static int Version { get; private set; }

    public static IReadOnlyList<LocaleInfo> Available => All;
    public static bool IsRtl => Info(CurrentCode)?.Rtl == true;
    public static string FontFamily => Info(CurrentCode)?.FontFamily ?? "Segoe UI";
    public static string LocaleName => Info(CurrentCode)?.LocaleName ?? "en-US";
    public static string SampleText => CurrentCode.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "あ"
                                     : CurrentCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "汉"
                                     : "Ag";

    /// <summary>当前语言的本地名（设置页右侧显示用）。</summary>
    public static string CurrentDisplayName => Info(CurrentCode)?.NativeName ?? CurrentCode;

    /// <summary>
    /// 初始化。`pref` 为空/system 时按 Windows UI 语言选；未知语言回退英文。
    /// 必须在创建 UI 之前调用（引擎 Run 里已经接好）。
    /// </summary>
    public static void Initialize(string pref = null)
    {
        PrefCode = NormalizePref(pref);
        CurrentCode = PrefCode == SystemCode ? DetectSystem() : NormalizeCode(PrefCode);
        if (Info(CurrentCode) == null) CurrentCode = "en";

        // 中文是源文，不需要翻译表；英文是回退语言，必须尽量完整。
        _current = CurrentCode == SourceCode ? Empty : Load(CurrentCode);
        _english = CurrentCode == "en" ? _current : Load("en");
        AuditMissing = Environment.GetEnvironmentVariable("INKTEACH_LOC_AUDIT") == "1";
        _missing.Clear();
        Version++;
    }

    /// <summary>
    /// 按当前语言翻译一段中文源文。查不到时：当前语言 → 英文 → 原中文。
    /// 这是 UI 绘制的**唯一文字入口**（Widgets / 引擎 UI 层会调用它）。
    /// </summary>
    public static string Tr(string text)
    {
        if (string.IsNullOrEmpty(text) || CurrentCode == SourceCode) return text;
        if (_current.TryGetValue(text, out var current)) return current;
        if (AuditMissing && HasCjk(text)) _missing.Add(text);
        if (_english.TryGetValue(text, out var english)) return english;
        return text;
    }

    private static bool HasCjk(string s)
    {
        foreach (char c in s)
            if (c >= '\u3400' && c <= '\u9fff') return true;
        return false;
    }

    /// <summary>带占位符的翻译；例如 T("{0} 笔", n)。</summary>
    public static string T(string source, params object[] args)
    {
        string s = Tr(source);
        return args == null || args.Length == 0 ? s : string.Format(CultureInfo.CurrentCulture, s, args);
    }

    /// <summary>设置页用：system + 所有语言。</summary>
    public static IReadOnlyList<string> PrefCodes
    {
        get
        {
            var list = new List<string>(All.Length + 1) { SystemCode };
            foreach (var l in All) list.Add(l.Code);
            return list;
        }
    }

    /// <summary>设置页显示名；system 本身也要能被翻译。</summary>
    public static string DisplayName(string pref)
    {
        string p = NormalizePref(pref);
        if (p == SystemCode) return "跟随系统";
        return Info(p)?.NativeName ?? p;
    }

    /// <summary>把 Inno 安装器写下的 ActiveLanguage 映射成 BCP-47。</summary>
    public static string FromInstallerLanguage(string installerName)
    {
        if (string.IsNullOrWhiteSpace(installerName)) return null;
        string s = installerName.Trim().ToLowerInvariant();
        foreach (var l in All)
            if (string.Equals(l.InstallerName, s, StringComparison.OrdinalIgnoreCase)) return l.Code;
        return s switch
        {
            "portuguese" => "pt-BR",
            "chinese" => "zh-CN",
            "english" => "en",
            "japanese" => "ja",
            "korean" => "ko",
            "german" => "de",
            "french" => "fr",
            "spanish" => "es",
            "russian" => "ru",
            "italian" => "it",
            _ => null,
        };
    }

    /// <summary>诊断：当前语言是否缺这条翻译（不区分是否回退到英文）。</summary>
    public static bool IsMissing(string source)
        => CurrentCode != SourceCode && !_current.ContainsKey(source);

    /// <summary>把本次运行没命中的源文打印出来（供补翻译）。</summary>
    public static void DumpMissing()
    {
        // 审计文件走 UTF-8，绕开 Windows 控制台代码页（否则中文全成乱码，没法直接补翻译）。
        string auditFile = Environment.GetEnvironmentVariable("INKTEACH_LOC_AUDIT_FILE");
        if (!string.IsNullOrEmpty(auditFile))
        {
            try { File.WriteAllLines(auditFile, _missing, new System.Text.UTF8Encoding(false)); }
            catch { /* 审计写不出来不影响软件 */ }
        }
        if (!AuditMissing || _missing.Count == 0) return;
        Console.WriteLine($"--- LOC-MISSING {CurrentCode} ({_missing.Count}) ---");
        foreach (string s in _missing) Console.WriteLine("LOC-MISSING: " + s.Replace("\n", "\\n"));
        Console.WriteLine("--- end LOC-MISSING ---");
    }

    /// <summary>
    /// 覆盖率审计（不建窗口）：以英文为基准，逐语言报 key 数、缺多少、占位符不一致多少。
    /// 只做“表对不对”，不判断翻译好坏；翻译审校仍要人工/母语者。
    /// </summary>
    public static void AuditAll()
    {
        var english = Load("en");
        Console.WriteLine("=== LOC-COUNT ===");
        Console.WriteLine($"{"code",-7} {"keys",5} {"missing",7} {"placeholder",11}");
        foreach (var info in All)
        {
            if (info.Code == SourceCode) continue;
            var data = info.Code == "en" ? english : Load(info.Code);
            int missing = 0, badPlaceholder = 0;
            foreach (var kv in english)
            {
                if (!data.TryGetValue(kv.Key, out string value))
                {
                    missing++;
                    continue;
                }
                if (PlaceholderSignature(kv.Value) != PlaceholderSignature(value)) badPlaceholder++;
            }
            Console.WriteLine($"{info.Code,-7} {data.Count,5} {missing,7} {badPlaceholder,11}");
            if (missing > 0)
            {
                int shown = 0;
                foreach (var kv in english)
                {
                    if (data.ContainsKey(kv.Key)) continue;
                    Console.WriteLine("  missing: " + kv.Key.Replace("\n", "\\n"));
                    if (++shown >= 8) break;
                }
            }
        }
        Console.WriteLine("=== end LOC-COUNT ===");
    }

    private static string PlaceholderSignature(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var nums = new List<int>();
        for (int i = 0; i + 1 < s.Length; i++)
        {
            if (s[i] != '{') continue;
            int j = i + 1;
            while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
            if (j > i + 1 && j < s.Length && s[j] == '}')
                nums.Add(int.Parse(s.AsSpan(i + 1, j - i - 1)));
        }
        nums.Sort();
        return string.Join(",", nums);
    }

    private static string NormalizePref(string pref)
    {
        if (string.IsNullOrWhiteSpace(pref) || string.Equals(pref, SystemCode, StringComparison.OrdinalIgnoreCase))
            return SystemCode;
        string installer = FromInstallerLanguage(pref);
        if (installer != null && Info(installer) != null) return installer;
        return Info(pref) != null ? Info(pref).Code : SystemCode;
    }

    private static string NormalizeCode(string code)
    {
        string c = FromInstallerLanguage(code) ?? code;
        return Info(c)?.Code ?? "en";
    }

    private static LocaleInfo Info(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (var l in All)
            if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    /// <summary>
    /// Windows UI 语言：用 CurrentUICulture 的 BCP-47；只映射我们支持的主要语言。
    /// 繁中/港台暂时回退 zh-CN；阿拉伯语/希伯来语等 RTL 未支持，回退英文。
    /// </summary>
    private static string DetectSystem()
    {
        string name;
        try { name = CultureInfo.CurrentUICulture.Name; }
        catch { return "en"; }
        if (string.IsNullOrEmpty(name)) return "en";

        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko";
        if (name.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return "de";
        if (name.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "fr";
        if (name.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "es";
        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        if (name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "ru";
        if (name.StartsWith("it", StringComparison.OrdinalIgnoreCase)) return "it";
        return "en";
    }

    /// <summary>读取 Locales/&lt;code&gt; 下所有 JSON 并合并。找不到/坏文件都不影响启动。</summary>
    private static Dictionary<string, string> Load(string code)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            Assembly asm = typeof(Loc).Assembly;
            string prefix = "InkLocalization.Locales." + code.Replace('-', '_') + ".";
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                using Stream stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                using JsonDocument doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("strings", out JsonElement strings)
                    || strings.ValueKind != JsonValueKind.Object) continue;
                foreach (JsonProperty p in strings.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        result[p.Name] = p.Value.GetString();
            }
        }
        catch
        {
            // 本地化坏了也绝不能让软件启动失败：回退源文/英文。
        }
        return result;
    }
}
