using System;
using System.Collections.Generic;
using InkLocalization;

namespace InkEngine;

/// <summary>
/// 文本框可选字体。
///
/// **不内嵌任何字体**（2026-11-16 删掉 4MB 的钟齐志莽行书）：全部对接系统字体，
/// 雅黑 / 楷体 / 宋体 / 黑体 一定有；行书用系统“华文行楷”（Office 自带），
/// 这台机器没装就**不显示这一格**。
///
/// `ZhiMang = 4` 只保留存档兼容（旧文档里有 4 这个编号）：不再显示「手写」格，
/// 绘制按当前语言回退到系统字体——中文楷体、日文 Yu Gothic/Meiryo、韩文
/// Malgun Gothic、其它语言 Segoe UI。旧自定义家族名 “Zhi Mang Xing” / 缺字体的
/// 家族名走同一条回退，不会变成空白/豆腐块。
///
/// 参考：Word 的字体下拉 / 华为笔记、小米笔记的字体档位（用户 2026-11-11 点名要
/// 楷体、宋体、行书、手写）；不照搬完整字体列表，板书场景这几档够用。
/// </summary>
internal enum TextBoxFace
{
    YaHei = 0,
    KaiTi = 1,
    SimSun = 2,
    SimHei = 3,
    ZhiMang = 4,     // 旧「手写」档：仅存档兼容，绘制走当前语言系统字体
    XingShu = 5,     // 行书（系统“华文行楷”，没装就不显示这一档）
    Custom = 6,      // 系统字体下拉里选的任意字体（家族名在 TextBoxData.FaceName）
}

internal static class TextBoxFaces
{
    internal readonly record struct Info(TextBoxFace Face, string Name, string DirectWrite,
                                         string Alias = null);

    /// <summary>顺序 = 面板里的格序，也是存档里的编号，**不要重排**；新档只往后加。</summary>
    public static readonly Info[] All =
    {
        new(TextBoxFace.YaHei, "雅黑", "Microsoft YaHei UI", "微软雅黑"),
        new(TextBoxFace.KaiTi, "楷体", "KaiTi", "楷体"),
        new(TextBoxFace.SimSun, "宋体", "SimSun", "宋体"),
        new(TextBoxFace.SimHei, "黑体", "SimHei", "黑体"),
        // 旧「手写」档：不再显示、不再内嵌字体；仅保留编号兼容与旧家族名匹配。
        new(TextBoxFace.ZhiMang, "手写", "Zhi Mang Xing", "钟齐志莽行书"),
        new(TextBoxFace.XingShu, "行书", "华文行楷", "华文行楷"),
    };

    private static Info[] _visible;

    /// <summary>
    /// 面板里**实际显示**的快捷格：4 个系统字体；其余全部进「更多…」下拉
    /// （2026-11-11 用户定）。**2026-11-16 起不再有内嵌「手写」格**：旧文档的
    /// `ZhiMang` 编号仍能读，绘制自动回退到当前语言的系统字体（中文=楷体）。
    /// </summary>
    public static bool IsChineseUi => Loc.CurrentCode == "zh-CN";
    public static bool IsCjkUi => Loc.CurrentCode is "zh-CN" or "ja" or "ko";

    public static Info[] Visible
    {
        get
        {
            if (_visible != null) return _visible;
            var list = new List<Info>(4);
            if (IsChineseUi)
            {
                foreach (var info in All)
                    if (info.Face is TextBoxFace.YaHei or TextBoxFace.KaiTi
                        or TextBoxFace.SimSun or TextBoxFace.SimHei)
                        list.Add(info);
            }
            else if (Loc.CurrentCode == "ja")
            {
                // 日文名比中文长，快捷格只放三个短名；其余进「その他…」。
                AddPresetAny(list, "ゴシック", "Yu Gothic UI", "Yu Gothic");
                AddPresetAny(list, "MS", "MS PGothic", "MS Gothic");
                AddPresetAny(list, "メイリオ", "Meiryo UI", "Meiryo");
                AddPresetAny(list, "明朝", "Yu Mincho");
            }
            else if (Loc.CurrentCode == "ko")
            {
                AddPreset(list, "고딕", "Malgun Gothic");
                AddPreset(list, "바탕", "Batang");
                AddPreset(list, "돋움", "Dotum");
            }
            else
            {
                AddPreset(list, "Segoe", "Segoe UI");
                AddPreset(list, "Arial", "Arial");
                AddPreset(list, "Times", "Times New Roman");
                AddPreset(list, "Georgia", "Georgia");
            }
            if (list.Count == 0)
                list.Add(new Info(TextBoxFace.Custom, "Segoe UI", "Segoe UI"));
            _visible = list.ToArray();
            return _visible;
        }
    }

    private static void AddPreset(List<Info> list, string name, string family)
        => AddPresetAny(list, name, family);

    private static void AddPresetAny(List<Info> list, string name, params string[] families)
    {
        var available = FamiliesForCurrentLanguage;
        foreach (string family in families)
            foreach (var f in available)
                if (string.Equals(f, family, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new Info(TextBoxFace.Custom, name, family));
                    return;
                }
    }

    /// <summary>快捷字体格当前是否选中（内置档看枚举，语言预设看家族名）。</summary>
    public static bool IsVisibleFace(TextBoxData box, Info info)
        => info.Face == TextBoxFace.Custom
            ? IsFamily(box, info.DirectWrite)
            : IsFace(box, info.Face);

    private static bool HasSystemFamily(string name)
    {
        try
        {
            foreach (string family in SkiaSharp.SKFontManager.Default.FontFamilies)
                if (string.Equals(family, name, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
        return false;
    }

    // ---- 旧「手写」档 / 缺字体家族名的系统回退 ---------------------------

    private static readonly object KnownFamiliesLock = new();
    private static HashSet<string> _knownFamilies;
    private static string _fallbackCode;
    private static string _fallbackFamily;

    /// <summary>旧内嵌字体名（存档里可能是中文别名或英文家族名）。</summary>
    private static bool IsLegacyHandwriting(string family)
        => string.Equals(family, "Zhi Mang Xing", StringComparison.OrdinalIgnoreCase)
           || string.Equals(family, "钟齐志莽行书", StringComparison.OrdinalIgnoreCase)
           || string.Equals(family, "LiyuShoushu", StringComparison.OrdinalIgnoreCase)
           || string.Equals(family, "漓雨手书", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 当前界面的系统回退字体（旧「手写」档 / 本机没有的家族名都落在这里）。
    /// 中文楷体、日文 Yu Gothic（退 Meiryo）、韩文 Malgun Gothic、其它 Segoe UI；
    /// 候选都不在时再用界面字体，保证永远能画出来。
    /// </summary>
    public static string FallbackFamily
    {
        get
        {
            string code = Loc.CurrentCode;
            if (_fallbackFamily != null && _fallbackCode == code) return _fallbackFamily;
            string[] candidates = code switch
            {
                "zh-CN" => new[] { "KaiTi", "楷体", "SimKai" },
                "ja" => new[] { "Yu Gothic", "Meiryo", "MS Gothic" },
                "ko" => new[] { "Malgun Gothic", "Gulim" },
                _ => new[] { "Segoe UI" },
            };
            string picked = null;
            foreach (string c in candidates)
                if (HasFamily(c)) { picked = c; break; }
            picked ??= Loc.FontFamily;
            _fallbackCode = code;
            _fallbackFamily = picked;
            return picked;
        }
    }

    /// <summary>家族名是否真的在本机（DirectWrite 显示名 / Skia 英文名都算）。</summary>
    public static bool HasFamily(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return KnownFamilies.Contains(name.Trim());
    }

    private static HashSet<string> KnownFamilies
    {
        get
        {
            if (_knownFamilies != null) return _knownFamilies;
            lock (KnownFamiliesLock)
            {
                if (_knownFamilies != null) return _knownFamilies;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in SystemFamilies) set.Add(f);
                try
                {
                    foreach (string f in SkiaSharp.SKFontManager.Default.FontFamilies)
                        if (!string.IsNullOrWhiteSpace(f)) set.Add(f);
                }
                catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
                _knownFamilies = set;
                return set;
            }
        }
    }

    /// <summary>
    /// 存档里的字体家族名 → 真正能画的系统家族名。
    /// 旧内嵌名、本机没装的家族名都回退到 <see cref="FallbackFamily"/>。
    /// </summary>
    public static string ResolveFamily(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return FallbackFamily;
        family = family.Trim();
        if (IsLegacyHandwriting(family)) return FallbackFamily;
        return HasFamily(family) ? family : FallbackFamily;
    }

    // ---- 任意系统字体（字体面板的“更多…”下拉） --------------------------

    private static string[] _systemFamilies;
    private static string[] _cjkFamilies;

    /// <summary>
    /// 系统字体里**含中文字形**的家族（默认给字体下拉用；2026-11-11 用户定“只列中文字体”）。
    /// 判据是 DirectWrite 的 HasCharacter('中')——比按名字猜可靠。
    /// </summary>
    public static string[] CjkFamilies
    {
        get
        {
            if (_cjkFamilies != null) return _cjkFamilies;
            _ = SystemFamilies;                     // 先跑一次完整枚举（顺带缓存）
            _cjkFamilies = _cjkFamilyList.ToArray();
            return _cjkFamilies;
        }
    }

    private static readonly List<string> _cjkFamilyList = new();
    private static readonly Dictionary<char, string[]> _scriptFamilyCache = new();
    private static readonly Dictionary<char, string[]> _latinFamilyCache = new();

    // 日/韩不做"含假名/谚文"全量过滤：中文字体也常含假名，会把「微软雅黑」混进日文列表。
    // 只列系统里实际存在的常见日/韩字体；不够就少几项，不硬凑。
    private static readonly string[] JaCandidates =
    {
        "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS Gothic", "MS PGothic",
        "Yu Mincho", "MS Mincho", "MS PMincho", "BIZ UDGothic", "BIZ UDMincho",
    };

    private static readonly string[] KoCandidates =
    {
        "Malgun Gothic", "Batang", "Dotum", "Gulim",
        "BatangChe", "DotumChe", "GulimChe", "Gungsuh",
    };

    private static string[] Existing(string[] candidates)
    {
        var list = new List<string>(candidates.Length);
        foreach (string f in candidates)
            if (HasSystemFamily(f)) list.Add(f);
        return list.ToArray();
    }

    /// <summary>当前界面语言该列哪些字体（中文=含汉字，日/韩=常见系统字体，俄/其他=拉丁）。</summary>
    public static string[] FamiliesForCurrentLanguage
    {
        get
        {
            if (Loc.CurrentCode == "zh-CN") return CjkFamilies;
            if (Loc.CurrentCode == "ja") return Existing(JaCandidates);
            if (Loc.CurrentCode == "ko") return Existing(KoCandidates);
            // 拉丁/俄文界面不把中文字体混进来（它们也含 A，但不是老师想要的“西文字体”）。
            char probe = Loc.CurrentCode == "ru" ? 'Я' : 'A';
            return FamiliesSupporting(probe, excludeCjk: true);
        }
    }

    /// <summary>系统字体里真的含有这个字符的家族（DirectWrite HasCharacter）。</summary>
    private static string[] FamiliesSupporting(char probe, bool excludeCjk = false)
    {
        var cache = excludeCjk ? _latinFamilyCache : _scriptFamilyCache;
        if (cache.TryGetValue(probe, out var cached)) return cached;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        try
        {
            using var coll = Gfx.WriteFactory.GetSystemFontCollection(false);
            uint n = coll.FontFamilyCount;
            for (uint i = 0; i < n; i++)
            {
                string name = null;
                bool has = false;
                try
                {
                    using var fam = coll.GetFontFamily(i);
                    has = FamilyHasCharacter(fam, probe);
                    if (!has) continue;
                    if (excludeCjk && FamilyHasCharacter(fam, '\u4E2D')) continue;
                    name = DisplayName(fam);
                }
                catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) result.Add(name);
            }
        }
        catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
        if (result.Count == 0)
            return SystemFamilies;
        result.Sort(StringComparer.OrdinalIgnoreCase);
        var arr = result.ToArray();
        cache[probe] = arr;
        return arr;
    }

    /// <summary>
    /// 系统已安装的字体家族（去重、中文名优先）。第一次访问时枚举一次，之后走缓存。
    /// **用 DirectWrite 自己的集合枚举**：拿到的名字再传回 CreateTextFormat 一定能解析
    /// （Skia 的名字有时是另一套写法，比如 “Microsoft YaHei” vs “Microsoft YaHei UI”）。
    /// </summary>
    public static string[] SystemFamilies
    {
        get
        {
            if (_systemFamilies != null) return _systemFamilies;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cn = new List<string>();
            var other = new List<string>();
            _cjkFamilyList.Clear();
            try
            {
                using var coll = Gfx.WriteFactory.GetSystemFontCollection(false);
                uint n = coll.FontFamilyCount;
                for (uint i = 0; i < n; i++)
                {
                    string name = null;
                    bool hasCjk = false;
                    try
                    {
                        using var fam = coll.GetFontFamily(i);
                        hasCjk = FamilyHasCharacter(fam, '\u4E2D');      // “中”
                        name = DisplayName(fam);
                    }
                    catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    (IsCjkName(name) ? cn : other).Add(name);
                    if (hasCjk) _cjkFamilyList.Add(name);
                }
            }
            catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
            if (cn.Count == 0 && other.Count == 0)
            {
                // DirectWrite 枚举失败时退回 Skia（只是少见的兜底）。
                try
                {
                    foreach (string family in SkiaSharp.SKFontManager.Default.FontFamilies)
                        if (!string.IsNullOrWhiteSpace(family) && seen.Add(family))
                        {
                            (IsCjkName(family) ? cn : other).Add(family);
                            _cjkFamilyList.Add(family);     // 兜底时按名字粗略当中文
                        }
                }
                catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
            }
            var zh = StringComparer.Create(new System.Globalization.CultureInfo("zh-CN"), false);
            cn.Sort(zh);                                     // 中文按拼音序
            other.Sort(StringComparer.OrdinalIgnoreCase);
            _cjkFamilyList.Sort(zh);
            var all = new List<string>(cn.Count + other.Count);
            all.AddRange(cn);
            all.AddRange(other);
            _systemFamilies = all.ToArray();
            return _systemFamilies;
        }
    }

    /// <summary>家族里任意一个字面含有这个字符（只查 font 0 会漏掉日文字体）。</summary>
    private static bool FamilyHasCharacter(Vortice.DirectWrite.IDWriteFontFamily fam, char ch)
    {
        try
        {
            uint count = fam.FontCount;
            for (uint i = 0; i < count && i < 12; i++)
            {
                using var font = fam.GetFont(i);
                if (font.HasCharacter(ch)) return true;
            }
        }
        catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
        return false;
    }

    /// <summary>字体家族显示名：先当前界面语言，再 en，再 zh；非中文界面不再冒出“微软雅黑”。</summary>
    private static string DisplayName(Vortice.DirectWrite.IDWriteFontFamily fam)
    {
        using var names = fam.FamilyNames;
        uint idx;
        string loc = Loc.LocaleName;
        if (names.FindLocaleName(loc, out idx)) return names.GetString(idx);
        string lang = loc.Split('-')[0];
        if (names.FindLocaleName(lang, out idx)) return names.GetString(idx);
        if (names.FindLocaleName("en-us", out idx)) return names.GetString(idx);
        if (names.FindLocaleName("zh-CN", out idx)) return names.GetString(idx);
        return names.Count > 0 ? names.GetString(0) : null;
    }

    private static bool IsCjkName(string s)
    {
        foreach (char ch in s)
            if (ch >= 0x2E80) return true;
        return false;
    }

    /// <summary>文本框当前实际该用的字体家族名（自定义优先，其次枚举档）。</summary>
    public static string FamilyOf(TextBoxData box)
        => box != null && !string.IsNullOrWhiteSpace(box.FaceName)
            ? ResolveFamily(box.FaceName)
            : DirectWrite(box?.Face ?? TextBoxFace.YaHei);

    /// <summary>这个框现在是不是这一档（自定义字体时，枚举档一律不算选中）。</summary>
    public static bool IsFace(TextBoxData box, TextBoxFace face)
        => box != null && string.IsNullOrWhiteSpace(box.FaceName) && box.Face == face;

    public static bool IsFamily(TextBoxData box, string family)
    {
        if (box == null) return false;
        string name = string.IsNullOrWhiteSpace(box.FaceName)
            ? ListNameOf(box.Face)
            : box.FaceName;
        return string.Equals(name, family, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>行距档位（倍数）。1.5 是现有默认，前后各留常用档。</summary>
    public static readonly float[] LineSpacings = { 1.0f, 1.2f, 1.5f, 1.8f, 2.0f };

    public const float DefaultLineSpacing = 1.55f;

    public static Info Get(TextBoxFace face)
    {
        foreach (var info in All)
            if (info.Face == face) return info;
        return All[0];
    }

    public static string DirectWrite(TextBoxFace face)
        => face == TextBoxFace.ZhiMang ? FallbackFamily : Get(face).DirectWrite;

    public static string Name(TextBoxFace face) => Get(face).Name;

    /// <summary>字体列表里这一行是不是就是这一档（英文家族名或本地化名都算）。</summary>
    public static bool SameFace(Info info, string family)
        => string.Equals(info.DirectWrite, family, StringComparison.OrdinalIgnoreCase)
           || (!string.IsNullOrEmpty(info.Alias)
               && string.Equals(info.Alias, family, StringComparison.OrdinalIgnoreCase))
           || (info.Face == TextBoxFace.ZhiMang && IsLegacyHandwriting(family));

    /// <summary>这一档在「更多…」列表里出现的名字（有本地化名就按它找）。</summary>
    public static string ListNameOf(TextBoxFace face)
    {
        // 旧「手写」档在列表里已经没有对应行：按回退后的系统字体去找（找不到就从头开始）。
        if (face == TextBoxFace.ZhiMang) return FallbackFamily;
        var info = Get(face);
        return string.IsNullOrEmpty(info.Alias) ? info.DirectWrite : info.Alias;
    }

    public static TextBoxFace Clamp(int index)
        => index >= 0 && index < All.Length ? All[index].Face : TextBoxFace.YaHei;

    public static float ClampSpacing(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0.9f, 2.5f) : DefaultLineSpacing;
}
