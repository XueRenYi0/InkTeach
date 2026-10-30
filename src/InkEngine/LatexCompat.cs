using System.Text;

namespace InkEngine;

/// <summary>
/// LaTeX 兼容层：把大模型（DeepSeek / ChatGPT / 豆包）和常见题库里
/// **CSharpMath 不认识的写法**统一翻译成它能渲染的子集。
///
/// 这层的目标不是"支持所有 LaTeX"，而是**只维护一张命令表**：
/// 新遇到一种不支持的命令，就在这里加一条，排版层和文本框都不用改。
/// 翻译仍然失败时 <see cref="MathRenderer"/> 会返回 null，显示"（公式）"，
/// 绝不把 LaTeX 源码露给学生。
/// </summary>
internal static class LatexCompat
{
    public static string Normalize(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex)) return "";
        string s = latex.Trim();

        // 网页复制常见的 HTML 实体
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&")
             .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");

        // 环境：align / gather 在 CSharpMath 里用 aligned 代替
        s = s.Replace("\\begin{align*}", "\\begin{aligned}")
             .Replace("\\end{align*}", "\\end{aligned}")
             .Replace("\\begin{align}", "\\begin{aligned}")
             .Replace("\\end{align}", "\\end{aligned}")
             .Replace("\\begin{gather*}", "\\begin{aligned}")
             .Replace("\\end{gather*}", "\\end{aligned}")
             .Replace("\\begin{gather}", "\\begin{aligned}")
             .Replace("\\end{gather}", "\\end{aligned}")
             .Replace("\\begin{eqnarray*}", "\\begin{aligned}")
             .Replace("\\end{eqnarray*}", "\\end{aligned}")
             .Replace("\\begin{eqnarray}", "\\begin{aligned}")
             .Replace("\\end{eqnarray}", "\\end{aligned}");

        // 整段公式环境：只留内容
        foreach (string env in new[] { "equation*", "equation", "displaymath", "math" })
        {
            s = s.Replace("\\begin{" + env + "}", "");
            s = s.Replace("\\end{" + env + "}", "");
        }

        // 常见别名
        s = s.Replace("\\dfrac", "\\frac").Replace("\\tfrac", "\\frac");
        s = s.Replace("\\overrightarrow", "\\vec")
             .Replace("\\overleftarrow", "\\vec")
             .Replace("\\overleftrightarrow", "\\vec");

        // 带参数、但 CSharpMath 不认的命令：保留参数内容
        s = UnwrapArg(s, "\\boxed");
        s = UnwrapArg(s, "\\fbox");
        s = UnwrapArg(s, "\\cancel");
        s = UnwrapArg(s, "\\bcancel");
        s = UnwrapArg(s, "\\xcancel");
        s = UnwrapArg(s, "\\mathop");
        s = UnwrapTwoArgs(s, "\\textcolor");
        // 上下标注 CSharpMath 不支持：保留底下的主体，丢掉标注，至少公式不报错。
        // 圆弧 AB 用上划线近似（Chinese 几何题里最常见）。
        s = s.Replace("\\overset{\\frown}", "\\overline")
             .Replace("\\overset{\\smile}", "\\underline");
        s = UnwrapSecondArg(s, "\\overset");
        s = UnwrapSecondArg(s, "\\underset");
        s = UnwrapSecondArg(s, "\\stackrel");

        // 箭头上的文字：CSharpMath 只画箭头（后面留空格，防止 \toB 被当成一条命令）
        s = ReplaceCommandArg(s, "\\xrightarrow", "\\to ");
        s = ReplaceCommandArg(s, "\\xleftarrow", "\\leftarrow ");
        s = ReplaceCommandArg(s, "\\xRightarrow", "\\Rightarrow ");
        s = ReplaceCommandArg(s, "\\xLeftarrow", "\\Leftarrow ");

        // 只占位、不显示的命令
        foreach (string cmd in new[]
                 {
                     "\\displaystyle", "\\textstyle", "\\scriptstyle", "\\scriptscriptstyle",
                     "\\limits", "\\nolimits", "\\nonumber", "\\notag",
                 })
        {
            s = s.Replace(cmd, "");
        }
        s = StripArg(s, "\\tag");
        s = StripArg(s, "\\label");
        s = StripArg(s, "\\hspace");
        s = StripArg(s, "\\vspace");
        s = StripArg(s, "\\color");

        // cases / array 里的行距：[2pt]、[1ex] 之类
        s = StripRowSpacing(s);

        return s.Trim();
    }

    /// <summary>`\cmd{...}` → `{...}` 里的内容（支持嵌套花括号）。</summary>
    private static string UnwrapArg(string s, string cmd)
    {
        for (int guard = 0; guard < 40; guard++)
        {
            int at = s.IndexOf(cmd, StringComparison.Ordinal);
            if (at < 0) break;
            int open = s.IndexOf('{', at + cmd.Length);
            if (open < 0) break;
            int close = MatchBrace(s, open);
            if (close < 0) break;
            s = s[..at] + s[(open + 1)..close] + s[(close + 1)..];
        }
        return s;
    }

    /// <summary>`\cmd{第一个参数}{内容}` → 内容；第一个参数丢掉（如 \textcolor{red}{x}）。</summary>
    private static string UnwrapTwoArgs(string s, string cmd)
    {
        for (int guard = 0; guard < 20; guard++)
        {
            int at = s.IndexOf(cmd, StringComparison.Ordinal);
            if (at < 0) break;
            int open1 = s.IndexOf('{', at + cmd.Length);
            if (open1 < 0) break;
            int close1 = MatchBrace(s, open1);
            if (close1 < 0) break;
            int open2 = s.IndexOf('{', close1 + 1);
            if (open2 < 0) break;
            int close2 = MatchBrace(s, open2);
            if (close2 < 0) break;
            s = s[..at] + s[(open2 + 1)..close2] + s[(close2 + 1)..];
        }
        return s;
    }

    /// <summary>`\cmd{标注}{主体}` → 主体；CSharpMath 没有上下标注，先保住主体。</summary>
    private static string UnwrapSecondArg(string s, string cmd)
    {
        for (int guard = 0; guard < 20; guard++)
        {
            int at = s.IndexOf(cmd, StringComparison.Ordinal);
            if (at < 0) break;
            int open1 = s.IndexOf('{', at + cmd.Length);
            if (open1 < 0) break;
            int close1 = MatchBrace(s, open1);
            if (close1 < 0) break;
            int open2 = s.IndexOf('{', close1 + 1);
            if (open2 < 0) break;
            int close2 = MatchBrace(s, open2);
            if (close2 < 0) break;
            s = s[..at] + s[(open2 + 1)..close2] + s[(close2 + 1)..];
        }
        return s;
    }

    private static string ReplaceCommandArg(string s, string cmd, string replacement)
    {
        for (int guard = 0; guard < 20; guard++)
        {
            int at = s.IndexOf(cmd, StringComparison.Ordinal);
            if (at < 0) break;
            int open = s.IndexOf('{', at + cmd.Length);
            if (open < 0) break;
            int close = MatchBrace(s, open);
            if (close < 0) break;
            s = s[..at] + replacement + s[(close + 1)..];
        }
        return s;
    }

    private static string StripArg(string s, string cmd)
    {
        for (int guard = 0; guard < 40; guard++)
        {
            int at = s.IndexOf(cmd, StringComparison.Ordinal);
            if (at < 0) break;
            int open = s.IndexOf('{', at + cmd.Length);
            if (open < 0) break;
            int close = MatchBrace(s, open);
            if (close < 0) break;
            s = s[..at] + s[(close + 1)..];
        }
        return s;
    }

    /// <summary>`\\[2pt]` 这种行距参数在 CSharpMath 里会当成普通方括号，直接去掉。</summary>
    private static string StripRowSpacing(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length || s[i + 1] != '\\')
            {
                sb.Append(s[i]);
                continue;
            }
            sb.Append("\\\\");
            i++;
            int j = i + 1;
            while (j < s.Length && s[j] == ' ') j++;
            if (j < s.Length && s[j] == '[')
            {
                int close = s.IndexOf(']', j + 1);
                if (close > 0 && close - j < 24)
                {
                    i = close;
                }
            }
        }
        return sb.ToString();
    }

    private static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 公式里混了中文（`\text{当 }x>0\text{ 时}`）时，CSharpMath 的数学字体没有汉字，
    /// 会画成一排方框。这里把**含中文的 `\text{...}`** 拆出来当普通文字，
    /// 其余部分仍按公式渲染；调用方按 run 排在同一行。
    /// </summary>
    public static List<(bool IsText, string Value)> SplitTextSegments(string latex)
    {
        var result = new List<(bool IsText, string Value)>();
        var math = new StringBuilder();
        int i = 0;
        while (i < latex.Length)
        {
            int at = FindTextCommand(latex, i, out int argOpen);
            if (at < 0)
            {
                math.Append(latex[i..]);
                break;
            }
            math.Append(latex[i..at]);
            int close = MatchBrace(latex, argOpen);
            if (close < 0)
            {
                math.Append(latex[at..]);
                break;
            }
            string inner = latex[(argOpen + 1)..close];
            if (HasCjk(inner))
            {
                if (math.Length > 0)
                {
                    result.Add((false, math.ToString()));
                    math.Clear();
                }
                result.Add((true, UnescapeText(inner)));
            }
            else
            {
                math.Append(latex[at..(close + 1)]);
            }
            i = close + 1;
        }
        if (math.Length > 0) result.Add((false, math.ToString()));
        return result;
    }

    private static int FindTextCommand(string s, int start, out int argOpen)
    {
        argOpen = -1;
        string[] cmds = { "\\text", "\\textrm", "\\textnormal", "\\mbox", "\\hbox", "\\mathrm" };
        int i = start;
        while (i < s.Length)
        {
            int best = -1, bestOpen = -1;
            foreach (string cmd in cmds)
            {
                int at = s.IndexOf(cmd, i, StringComparison.Ordinal);
                if (at < 0) continue;
                int j = at + cmd.Length;
                while (j < s.Length && s[j] == ' ') j++;
                if (j >= s.Length || s[j] != '{') continue;
                if (best < 0 || at < best)
                {
                    best = at;
                    bestOpen = j;
                }
            }
            if (best < 0) return -1;
            // 只拆最外层的 \text；嵌套在 \frac{...} 里的中文不动，避免把公式拆坏。
            if (BraceDepthAt(s, best) == 0)
            {
                argOpen = bestOpen;
                return best;
            }
            i = best + 1;
        }
        return -1;
    }

    private static int BraceDepthAt(string s, int end)
    {
        int depth = 0;
        for (int i = 0; i < end; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
        }
        return depth;
    }

    private static bool HasCjk(string s)
    {
        foreach (char ch in s)
        {
            if (ch >= 0x2E80 && ch <= 0x9FFF) return true;   // 中日韩统一表意文字 + 部首
            if (ch >= 0xF900 && ch <= 0xFAFF) return true;   // 兼容表意文字
            if (ch >= 0xFF00 && ch <= 0xFFEF) return true;   // 全角标点/字母
        }
        return false;
    }

    private static string UnescapeText(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            if (i + 1 >= s.Length) break;
            char n = s[++i];
            sb.Append(n switch
            {
                '%' => "%", '_' => "_", '&' => "&", '{' => "{", '}' => "}",
                '#' => "#", '$' => "$",
                ',' or ';' or ':' or '!' or ' ' => " ",
                '\\' => " ",
                _ => n.ToString(),
            });
        }
        return sb.ToString().Replace("~", " ");
    }
}
