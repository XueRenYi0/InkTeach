using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InkEngine;

/// <summary>
/// OCR 回来的行内 LaTeX → 手写字体能直接画的 Unicode。
///
/// 这一版只处理板书里最常见的符号 / 上下标；复杂分式、根式仍是二期。
/// 关键点：**先去掉 $ / \( \) 这些标记**，否则手写字体把 `\in` 原样画出来。
/// </summary>
internal static class OcrText
{
    private static readonly Regex SubDigit = new(@"_([0-9])", RegexOptions.Compiled);
    private static readonly Regex SupDigit = new(@"\^([0-9])", RegexOptions.Compiled);
    private static readonly Regex SubLetter = new(@"_([nmsijk])", RegexOptions.Compiled);
    private static readonly Regex SupLetter = new(@"\^([n])", RegexOptions.Compiled);
    private static readonly Regex ParallelSlash = new(@"(?<=\s|^)//(?=\s|[，,。；;]|$)", RegexOptions.Compiled);

    private const string Subs = "₀₁₂₃₄₅₆₇₈₉";
    private const string Sups = "⁰¹²³⁴⁵⁶⁷⁸⁹";
    private const string SubLetters = "ₙₘₛᵢⱼₖ";

    /// <summary>
    /// 去掉 OCR 常带出来的“非正文行”：
    ///   · 单个大写字母（立方体顶点 A/B/C/D 被单独认成一行）；
    ///   · 不含数学符号的短括号批注（“（包含）”“（答对）”）；
    ///   · 连续重复行。
    /// 这批行不参与“行框配对”，原笔迹保持不动，避免把后面的行整体错位。
    /// </summary>
    public static string[] FilterLines(IEnumerable<string> raw)
    {
        var result = new List<string>();
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            string t = item.Trim();
            if (Regex.IsMatch(t, "^[A-Z]$")) continue;
            if (Regex.IsMatch(t, @"^[（(][^）)]{1,10}[）)]$") && !HasMath(t)) continue;
            if (result.Count > 0 && result[^1] == t) continue;
            result.Add(t);
        }
        return result.ToArray();
    }

    /// <summary>这一行是不是带公式 / 数学符号（用来决定默认要不要保留原笔迹）。</summary>
    public static bool HasMath(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        if (raw.Contains("$") || raw.Contains("\\(") || raw.Contains("\\)") ||
            raw.Contains("\\[") || raw.Contains("\\]")) return true;

        string s = Normalize(raw);
        foreach (char c in "∈∉⊂⊄⊆⊇∩∪∥∠△√≤≥≠±∞→←⊥")
            if (s.IndexOf(c) >= 0) return true;
        if (Regex.IsMatch(s, "[₀-₉⁰-⁹]")) return true;
        return false;
    }

    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Replace("\\(", "").Replace("\\)", "")
             .Replace("\\[", "").Replace("\\]", "")
             .Replace("$", "");

        // 否定形式必须先换，否则 `\subset` 会先吃掉 `\not\subset` 的后半截。
        s = s.Replace("\\not\\subset", "⊄").Replace("\\not\\in", "∉")
             .Replace("\\notin", "∉")
             .Replace("\\subseteq", "⊆").Replace("\\subset", "⊂")
             .Replace("\\supseteq", "⊇").Replace("\\supset", "⊃")
             .Replace("\\parallel", "∥").Replace("\\perp", "⊥")
             .Replace("\\cap", "∩").Replace("\\cup", "∪")
             .Replace("\\pi", "π").Replace("\\alpha", "α").Replace("\\beta", "β")
             .Replace("\\gamma", "γ").Replace("\\Delta", "Δ").Replace("\\Omega", "Ω")
             .Replace("\\theta", "θ").Replace("\\lambda", "λ").Replace("\\mu", "μ")
             .Replace("\\angle", "∠").Replace("\\triangle", "△")
             .Replace("\\in", "∈")
             .Replace("\\times", "×").Replace("\\cdot", "·").Replace("\\div", "÷")
             .Replace("\\leq", "≤").Replace("\\le", "≤")
             .Replace("\\geq", "≥").Replace("\\ge", "≥")
             .Replace("\\neq", "≠").Replace("\\ne", "≠")
             .Replace("\\pm", "±").Replace("\\infty", "∞")
             .Replace("\\rightarrow", "→").Replace("\\leftarrow", "←")
             .Replace("\\ ", " ").Replace("\\,", "").Replace("\\;", "")
             .Replace("{", "").Replace("}", "");

        s = Regex.Replace(s, @"\\([a-zA-Z]+)", "$1");
        s = SubDigit.Replace(s, m => Subs[m.Groups[1].Value[0] - '0'].ToString());
        s = SupDigit.Replace(s, m => Sups[m.Groups[1].Value[0] - '0'].ToString());
        s = SubLetter.Replace(s, m => SubLetters["nmsijk".IndexOf(m.Groups[1].Value[0])].ToString());
        s = SupLetter.Replace(s, "ⁿ");
        s = ParallelSlash.Replace(s, "∥");
        return s.Trim();
    }
}
