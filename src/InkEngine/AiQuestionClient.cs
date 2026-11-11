using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InkEngine;

/// <summary>
/// 真实 AI 出题（OpenAI 兼容 /chat/completions）。
/// 只负责“发 prompt、收 JSON、解析成题干/答案/图形 spec”，
/// 排版、渲染、存档都在卡片层，不在这里重复。
/// </summary>
internal static class AiQuestionClient
{
    internal sealed class Result
    {
        public bool Ok;
        public string Error = "";
        public string Stem = "";
        public string Answer = "";
        public string Analysis = "";
        public FigureSpec Figure;
        public string Raw = "";
        public string Model = "";
        public double Seconds;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private const string FigureSchema =
        "figure 可选，必须是结构化 JSON，不要返回图片、SVG、TikZ 或 LaTeX 代码。\n" +
        "二次函数/抛物线：{\"kind\":\"quadratic\",\"a\":1,\"b\":-4,\"c\":3,\"domain\":[-1,5]}\n" +
        "平面几何：{\"kind\":\"geometry2d\",\"points\":{\"A\":[0,0],\"B\":[4,0],\"C\":[0,3]}," +
        "\"segments\":[[\"A\",\"B\"],[\"B\",\"C\"],[\"C\",\"A\"]],\"labels\":{\"A\":\"A\",\"B\":\"B\",\"C\":\"C\"}}\n" +
        "立体几何：{\"kind\":\"geometry3d\",\"points\":{\"A\":[0,0,0],\"B\":[1,0,0]}," +
        "\"segments\":[[\"A\",\"B\"]],\"labels\":{\"A\":\"A\",\"B\":\"B\"}}\n" +
        "点坐标范围 0..10；没有图就省略 figure。";

    internal static async Task<Result> GenerateAsync(string prompt, OcrClient.OcrConfig cfg,
                                                     CancellationToken ct)
    {
        var res = new Result { Model = cfg?.Model ?? "" };
        if (cfg == null || !cfg.IsConfigured)
        {
            res.Error = "未配置 AI API（Key / BaseUrl / Model）";
            return res;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string J(string s) => "\"" + JsonEncodedText.Encode(s ?? "").ToString() + "\"";
            string body = "{\"model\":" + J(cfg.Model) +
                          ",\"stream\":false,\"temperature\":0.2,\"max_tokens\":1800," +
                          "\"messages\":[{\"role\":\"system\",\"content\":" +
                          J("你是数学出题助手。只输出一个 JSON 对象，不要 markdown 代码块。") + "}," +
                          "{\"role\":\"user\",\"content\":" +
                          J("请生成一道适合课堂投屏的数学题。输出 JSON：\n" +
                            "{\"stem\":\"题干\",\"answer\":\"答案\",\"analysis\":\"解析\",\"figure\":{...}}\n" +
                            FigureSchema + "\n\n用户要求：" + (prompt ?? "")) + "}]}";

            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(85));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead,
                                                  cts.Token).ConfigureAwait(false);
            string raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            res.Raw = raw;
            if (!resp.IsSuccessStatusCode)
            {
                res.Error = $"HTTP {(int)resp.StatusCode}: " + Clip(raw, 200);
                return res;
            }

            using var doc = JsonDocument.Parse(raw);
            string content = null;
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in choices.EnumerateArray())
                {
                    if (c.TryGetProperty("message", out var m)
                        && m.TryGetProperty("content", out var v)
                        && v.ValueKind == JsonValueKind.String)
                    {
                        content = v.GetString();
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                res.Error = "模型没有返回 content";
                return res;
            }

            string json = ExtractJson(content);
            using var q = JsonDocument.Parse(json);
            var root = q.RootElement;
            res.Stem = Str(root, "stem");
            res.Answer = Str(root, "answer");
            res.Analysis = Str(root, "analysis");
            if (root.TryGetProperty("figure", out var fig) && fig.ValueKind == JsonValueKind.Object)
                res.Figure = FigureSpec.Parse(fig.GetRawText());
            res.Ok = !string.IsNullOrWhiteSpace(res.Stem);
            if (!res.Ok) res.Error = "返回 JSON 里没有 stem";
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Error = "AI 请求超时";
            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
        finally
        {
            res.Seconds = sw.Elapsed.TotalSeconds;
        }
    }

    private static string ExtractJson(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            int end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
        }
        int a = s.IndexOf('{');
        int b = s.LastIndexOf('}');
        if (a >= 0 && b > a) return s[a..(b + 1)];
        return s;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : "";

    private static string Clip(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    // ================= 图片识别成题卡：文字 → 多张卡 =================

    /// <summary>一道题的结构化结果（一图多题时会有多条）。</summary>
    internal sealed class CardItem
    {
        public string Stem = "";
        public List<string> Options = new();
        public string Answer = "";
        public string Analysis = "";
        public List<string> Knowledge = new();
        public bool NeedReview;
    }

    internal sealed class StructureResult
    {
        public bool Ok;
        public string Error = "";
        public List<CardItem> Cards = new();
        public double Seconds;
        public string Raw = "";
        /// <summary>“识别内容”由模型自己判断：这段文字不是题目，是普通文本。</summary>
        public bool IsPlainText;
        /// <summary>普通文本时，模型整理过的原文（去页眉页脚/水印，保留公式）。</summary>
        public string PlainText = "";
    }

    /// <summary>
    /// 卷面 OCR 文字 → 一题一条 <see cref="CardItem"/>。
    /// 2026-11-10 实测（22 张国内卷面、每张两轮）：
    ///   · 关思考后 1.4–1.9 秒（只题干）/ 1.8–3.2 秒（带答案解析），22/22 都能解析；
    ///   · 不关思考会把 4096 tokens 全烧在 reasoning 上、content 为空；
    ///   · 一张卷面常有 2–3 道题，模型会连着输出多个 JSON 对象，
    ///     所以这里按**平衡花括号**逐个抠，不假设整个回复只有一个对象。
    /// </summary>
    internal static async Task<StructureResult> StructureAsync(
        string ocrText, OcrClient.OcrConfig cfg, CancellationToken ct, bool withAnswer,
        bool classify = false)
    {
        var res = new StructureResult();
        if (cfg == null || !cfg.IsConfigured)
        {
            res.Error = "AI 未配置（Key / BaseUrl / Model）";
            return res;
        }
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            res.Error = "没有可整理的文字";
            return res;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string J(string s) => "\"" + JsonEncodedText.Encode(s ?? "").ToString() + "\"";
            string sys = "你是数学备课助手。只输出 JSON，不要 markdown，不要解释。";
            string user =
                (classify
                    ? "先判断这段 OCR 文字属于哪一类：\n" +
                      "· 【数学题目】：有成组的题目（题号 + 要解答 / 填空 / 选择 / 计算 / 证明），" +
                      "试卷、练习、作业都算；开头有注意事项、页眉页脚、水印、二维码说明也不影响 —— 仍然按题目处理。\n" +
                      "· 【普通文本】：通知、笔记、说明、聊天记录、一段叙述，没有要学生作答的题。\n" +
                      "是数学题目 → 按下面规则输出题卡 JSON 数组。\n" +
                      "是普通文本 → 只输出一个对象：\n" +
                      "{\"kind\":\"text\",\"text\":\"整理后的原文\"}\n" +
                      "  只做整理：去掉页码 / 水印 / 页眉页脚、修断行；公式保留 LaTeX；" +
                      "不要总结、不要改写、不要加标题。\n\n"
                    : "") +
                "把下面的文本整理成题卡，输出 JSON 数组（即使只有一道题也用数组）：\n" +
                "[{\"stem\":\"题干\",\"options\":[],\"answer\":\"答案\",\"analysis\":\"解析\"," +
                "\"knowledge\":[],\"need_review\":false}]\n" +
                "拆分规则（重要）：只有独立题号（1. 2. 3. 或 一、二、）变化时才分卡；" +
                "同一道题的 (1)(2)(3) 小问必须合并在同一张卡的 stem 里，绝不拆卡。\n" +
                (withAnswer
                    ? "answer 给最终答案，analysis 先思路后步骤、不超过 200 个汉字；" +
                      "算出结果和选项对不上就写“请核对”并 need_review=true。\n"
                    : "**不要解题、不要给答案、不要写解析**：answer 与 analysis 一律留空字符串。\n") +
                "其它：数字 / 条件 / 问法原样保留；选项写完整；无选项给 []；拿不准 need_review=true。\n\n" +
                "文本：\n" + (ocrText ?? "");

            var body = new System.Text.StringBuilder();
            body.Append("{\"model\":").Append(J(cfg.Model))
                .Append(",\"stream\":false,\"temperature\":0,\"max_tokens\":")
                .Append(withAnswer ? 4096 : 2400)
                .Append(",\"messages\":[{\"role\":\"system\",\"content\":").Append(J(sys))
                .Append("},{\"role\":\"user\",\"content\":").Append(J(user)).Append("}]");
            string think = (cfg.Thinking ?? "auto").Trim().ToLowerInvariant();
            string host = (cfg.BaseUrl ?? "").ToLowerInvariant();
            if (think == "auto")
                think = (host.Contains("deepseek") || host.Contains("bigmodel") ||
                         host.Contains("volces") || host.Contains("ark.cn"))
                        ? "disabled" : "none";
            if (think == "disabled")
                body.Append(",\"thinking\":{\"type\":\"disabled\"}");
            else if (think == "low")
                body.Append(",\"reasoning_effort\":\"low\"");
            body.Append('}');

            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                       .ConfigureAwait(false);
            string raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                res.Error = $"HTTP {(int)resp.StatusCode}: " + Clip(raw, 160);
                return res;
            }

            using var doc = JsonDocument.Parse(raw);
            string content = null;
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in choices.EnumerateArray())
                {
                    if (c.TryGetProperty("message", out var m)
                        && m.TryGetProperty("content", out var v)
                        && v.ValueKind == JsonValueKind.String)
                    {
                        content = v.GetString();
                        break;
                    }
                }
            }
            res.Raw = content ?? "";
            if (string.IsNullOrWhiteSpace(content))
            {
                res.Error = "模型没有返回内容";
                return res;
            }

            res.Cards = ParseCards(content);
            if (classify)
            {
                string plain = ParsePlainText(content);
                if (plain != null)
                {
                    res.IsPlainText = true;
                    res.PlainText = plain;
                    res.Ok = true;
                    return res;
                }
            }
            res.Ok = res.Cards.Count > 0;
            if (!res.Ok) res.Error = "没有解析出题目";
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Error = "AI 请求超时";
            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
        finally
        {
            res.Seconds = sw.Elapsed.TotalSeconds;
        }
    }

    /// <summary>
    /// 从模型回复里抠出**所有** JSON 对象（数组 / 连着写多个对象都行）。
    /// 逐字符扫平衡花括号，跳过字符串内部的 `{}` 和转义；单个对象坏了不拖累其它题。
    /// </summary>
    internal static List<CardItem> ParseCards(string content)
    {
        var list = new List<CardItem>();
        string s = StripFence(content ?? "");
        int depth = 0, start = -1;
        bool inStr = false, esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == '}')
            {
                if (depth > 0) depth--;
                if (depth == 0 && start >= 0)
                {
                    var item = ReadCard(s.Substring(start, i - start + 1));
                    if (item != null) list.Add(item);
                    start = -1;
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 普通文本判定：模型按 constraints 输出 `{"kind":"text","text":"..."}`。
    /// 只有 kind 明确是 text 才算；题目对象里没有 kind，不会被误判。
    /// </summary>
    internal static string ParsePlainText(string content)
    {
        string s = StripFence(content ?? "");
        int depth = 0, start = -1;
        bool inStr = false, esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == '}')
            {
                if (depth > 0) depth--;
                if (depth == 0 && start >= 0)
                {
                    string obj = s.Substring(start, i - start + 1);
                    string text = ReadPlainTextObject(obj);
                    if (text != null) return text;
                    start = -1;
                }
            }
        }
        return null;
    }

    private static string ReadPlainTextObject(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch
        {
            try { doc = JsonDocument.Parse(Regex.Replace(json, @"\\(?![""\\/bfnrtu])", @"\\")); }
            catch { return null; }
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String
                || !string.Equals(k.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                return null;
            string text = RecoverLatexEscapes(Str(root, "text")).Trim();
            return text.Length > 0 ? text : "";
        }
    }

    private static CardItem ReadCard(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch
        {
            try { doc = JsonDocument.Parse(Regex.Replace(json, @"\\(?![""\\/bfnrtu])", @"\\")); }
            catch { return null; }
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var item = new CardItem
            {
                Stem = RecoverLatexEscapes(Str(root, "stem")).Trim(),
                Answer = RecoverLatexEscapes(Str(root, "answer")).Trim(),
                Analysis = RecoverLatexEscapes(Str(root, "analysis")).Trim(),
            };
            if (item.Stem.Length == 0) return null;
            if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                foreach (var o in opts.EnumerateArray())
                    if (o.ValueKind == JsonValueKind.String)
                    {
                        string t = (o.GetString() ?? "").Trim();
                        if (t.Length > 0) item.Options.Add(t);
                    }
            if (root.TryGetProperty("knowledge", out var kn) && kn.ValueKind == JsonValueKind.Array)
                foreach (var k in kn.EnumerateArray())
                    if (k.ValueKind == JsonValueKind.String)
                    {
                        string t = (k.GetString() ?? "").Trim();
                        if (t.Length > 0) item.Knowledge.Add(t);
                    }
            if (root.TryGetProperty("need_review", out var nr) &&
                (nr.ValueKind == JsonValueKind.True ||
                 (nr.ValueKind == JsonValueKind.String &&
                  bool.TryParse(nr.GetString(), out bool b) && b)))
                item.NeedReview = true;
            return item;
        }
    }

    private static string StripFence(string s)
    {
        s = (s ?? "").Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        int nl = s.IndexOf('\n');
        if (nl >= 0) s = s[(nl + 1)..];
        int end = s.LastIndexOf("```", StringComparison.Ordinal);
        if (end >= 0) s = s[..end];
        return s.Trim();
    }

    /// <summary>
    /// 救回被 JSON 转义吃掉的 LaTeX 命令。
    /// 模型如果直接把 `\frac` 写进 JSON 字符串，`\f` 是**合法**转义（换页符），
    /// `json.loads` 不报错但内容变成 `\x0Crac`——比直接失败更危险（悄悄改题）。
    /// 这里把“控制字符 + 两个以上字母”还原成 `\frac` / `\beta` / `\theta`。
    /// 只处理 \f \b \t \v 这几种（正文里几乎不会真出现）。
    /// </summary>
    private static string RecoverLatexEscapes(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        bool dirty = false;
        foreach (char ch in s)
            if (ch is '\f' or '\b' or '\t' or '\v') { dirty = true; break; }
        if (!dirty) return s;

        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '\f' or '\b' or '\t' or '\v'
                && i + 2 < s.Length
                && char.IsAsciiLetter(s[i + 1]) && char.IsAsciiLetter(s[i + 2]))
            {
                sb.Append('\\').Append(ch switch
                {
                    '\f' => 'f',
                    '\b' => 'b',
                    '\t' => 't',
                    _ => 'v',
                });
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
