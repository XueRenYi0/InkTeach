using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 文本框可选字体。
///
/// **只内嵌一款**：手写 = 钟齐志莽行书（`assets/fonts/ZhiMangXing.ttf`，4MB，OFL，
/// 和“AI 手写替换”共用同一份文件，首次用到才 `AddFontResourceEx(FR_PRIVATE)`
/// 注册给本进程，不装系统、不改系统字体表）。
/// 其余全部对接系统字体：雅黑 / 楷体 / 宋体 / 黑体 一定有；行书用系统
/// “华文行楷”（Office 自带），这台机器没装就**不显示这一格**，不会出现
/// 「行书」和「楷体」长得一样的情况。
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
    ZhiMang = 4,     // 手写（包内，唯一自带的字体：钟齐志莽行书）
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
        new(TextBoxFace.ZhiMang, "手写", "Zhi Mang Xing", "钟齐志莽行书"),
        new(TextBoxFace.XingShu, "行书", "华文行楷", "华文行楷"),
    };

    private static Info[] _visible;

    /// <summary>
    /// 面板里**实际显示**的快捷格：4 个系统字体；其余全部进「更多…」下拉
    /// （2026-11-11 用户定）。包内手写字体只在“AI 手写替换”里用——DirectWrite
    /// 的系统集合看不到进程内注册的字体，放进题卡会静默退回默认字体，所以不摆。
    /// </summary>
    public static Info[] Visible
    {
        get
        {
            if (_visible != null) return _visible;
            var list = new List<Info>(All.Length);
            foreach (var info in All)
                if (info.Face is TextBoxFace.YaHei or TextBoxFace.KaiTi
                    or TextBoxFace.SimSun or TextBoxFace.SimHei)
                    list.Add(info);
            _visible = list.ToArray();
            return _visible;
        }
    }

    private static bool HasSystemFamily(string name)
    {
        try
        {
            foreach (string family in SkiaSharp.SKFontManager.Default.FontFamilies)
                if (string.Equals(family, name, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
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
                        try
                        {
                            using var font = fam.GetFont(0);
                            hasCjk = font.HasCharacter(0x4E2D);      // “中”
                        }
                        catch { }
                        using var names = fam.FamilyNames;
                        uint idx = 0;
                        bool ok = names.FindLocaleName("zh-CN", out idx);
                        if (!ok) ok = names.FindLocaleName("zh-Hans", out idx);
                        if (!ok) ok = names.FindLocaleName("en-us", out idx);
                        if (!ok && names.Count > 0) { idx = 0; ok = true; }
                        if (!ok) continue;
                        name = names.GetString(idx);
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    (IsCjkName(name) ? cn : other).Add(name);
                    if (hasCjk) _cjkFamilyList.Add(name);
                }
            }
            catch { }
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
                catch { }
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

    private static bool IsCjkName(string s)
    {
        foreach (char ch in s)
            if (ch >= 0x2E80) return true;
        return false;
    }

    /// <summary>文本框当前实际该用的字体家族名（自定义优先，其次枚举档）。</summary>
    public static string FamilyOf(TextBoxData box)
        => box != null && !string.IsNullOrWhiteSpace(box.FaceName)
            ? box.FaceName
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

    public static string DirectWrite(TextBoxFace face) => Get(face).DirectWrite;

    public static string Name(TextBoxFace face) => Get(face).Name;

    /// <summary>字体列表里这一行是不是就是这一档（英文家族名或本地化名都算）。</summary>
    public static bool SameFace(Info info, string family)
        => string.Equals(info.DirectWrite, family, StringComparison.OrdinalIgnoreCase)
           || (!string.IsNullOrEmpty(info.Alias)
               && string.Equals(info.Alias, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>这一档在「更多…」列表里出现的名字（有本地化名就按它找）。</summary>
    public static string ListNameOf(TextBoxFace face)
    {
        var info = Get(face);
        return string.IsNullOrEmpty(info.Alias) ? info.DirectWrite : info.Alias;
    }

    public static TextBoxFace Clamp(int index)
        => index >= 0 && index < All.Length ? All[index].Face : TextBoxFace.YaHei;

    public static float ClampSpacing(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0.9f, 2.5f) : DefaultLineSpacing;

    // ---- 包内字体：按需注册给本进程 -------------------------------------

    private const uint FR_PRIVATE = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

    private static readonly HashSet<TextBoxFace> Registered = new();
    private static readonly object RegisterLock = new();

    /// <summary>包内字体第一次用到时才注册；系统字体是空操作。</summary>
    public static void EnsureBundled(TextBoxFace face)
    {
        if (face != TextBoxFace.ZhiMang) return;
        lock (RegisterLock)
        {
            if (!Registered.Add(face)) return;
        }
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory,
                                       "assets", "fonts", "ZhiMangXing.ttf");
            if (File.Exists(path)) AddFontResourceEx(path, FR_PRIVATE, IntPtr.Zero);
        }
        catch
        {
            // 注册失败就当没有这一档：绘制会退回系统默认字体，不影响其它功能。
        }
    }

    /// <summary>面板要预览所有档位，所以先把包内字体注册好。</summary>
    public static void EnsureAllBundled() => EnsureBundled(TextBoxFace.ZhiMang);
}
