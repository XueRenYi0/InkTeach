using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
}
