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

    /// <summary>把一行拆成「普通文字 / 行内公式」两段（$...$、\(...\)、\[...\]）。</summary>
    public static List<(bool IsMath, string Text)> SplitRuns(string raw)
    {
        var runs = new List<(bool, string)>();
        if (string.IsNullOrEmpty(raw)) return runs;
        int i = 0, textStart = 0;
        while (i < raw.Length)
        {
            string open = null, close = null;
            if (raw[i] == '$') { open = "$"; close = "$"; }
            else if (i + 1 < raw.Length && raw[i] == '\\' && raw[i + 1] == '(') { open = "\\("; close = "\\)"; }
            else if (i + 1 < raw.Length && raw[i] == '\\' && raw[i + 1] == '[') { open = "\\["; close = "\\]"; }
            if (open == null) { i++; continue; }

            int end = raw.IndexOf(close, i + open.Length, StringComparison.Ordinal);
            if (end < 0) break;
            if (i > textStart)
            {
                string t = raw.Substring(textStart, i - textStart);
                if (!string.IsNullOrWhiteSpace(t)) runs.Add((false, t));
            }
            string math = raw.Substring(i + open.Length, end - i - open.Length);
            if (!string.IsNullOrWhiteSpace(math)) runs.Add((true, math));
            i = end + close.Length;
            textStart = i;
        }
        if (textStart < raw.Length)
        {
            string t = raw.Substring(textStart);
            if (!string.IsNullOrWhiteSpace(t)) runs.Add((false, t));
        }
        if (runs.Count == 0 && !string.IsNullOrWhiteSpace(raw)) runs.Add((false, raw));
        return runs;
    }

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
            if (IsJunkLine(item)) continue;
            string t = item.Trim();
            if (result.Count > 0 && result[^1] == t) continue;
            result.Add(t);
        }
        return result.ToArray();
    }

    /// <summary>单行是不是“非正文”（顶点字母、短括号批注、裸 LaTeX 命令残留）。</summary>
    public static bool IsJunkLine(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return true;
        string t = item.Trim();
        if (Regex.IsMatch(t, "^[A-Z]$")) return true;
        if (Regex.IsMatch(t, @"^[A-Za-z]'?$") || Regex.IsMatch(t, @"^[0-9]{1,2}$") ||
            Regex.IsMatch(t, @"^[∞%]$") || Regex.IsMatch(t, @"^[∞\s0-9]*[A-Za-z]'?$")) return true;
        if (Regex.IsMatch(t, @"^(frac|sqrt|begin|end|dots|quad|qquad|therefore|vdots|cdots|ldots|left|right)[0-9A-Za-z{}]*$",
                          RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(t, @"^[（(][^）)]{1,10}[）)]$") && !HasMath(t)) return true;
        return false;
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

        // 行内短括号批注（答对）/（包含）不是正文；带数学符号的（点用 ∈ ∉）保留。
        s = Regex.Replace(s, @"[（(][^）)]{1,6}[）)]", m =>
            Regex.IsMatch(m.Value, @"[$\\∈∉⊂⊄⊆⊇∩∪∥]") ? m.Value : "");

        s = Regex.Replace(s, @"\\([a-zA-Z]+)", "$1");
        s = SubDigit.Replace(s, m => Subs[m.Groups[1].Value[0] - '0'].ToString());
        s = SupDigit.Replace(s, m => Sups[m.Groups[1].Value[0] - '0'].ToString());
        s = SubLetter.Replace(s, m => SubLetters["nmsijk".IndexOf(m.Groups[1].Value[0])].ToString());
        s = SupLetter.Replace(s, "ⁿ");
        s = ParallelSlash.Replace(s, "∥");
        return s.Trim();
    }
}
