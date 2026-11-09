using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// AI 作图：把“画 y=ln(x)”这类自然语言解析成解析式 + 定义域。
/// 只返回表达式文本，实际求值/采样/画图全在本地，避免让模型给像素。
/// </summary>
internal static class AiPlotClient
{
    internal sealed class Result
    {
        public bool Ok;
        public string Error = "";
        public PlotSpec Spec;
        public string Raw = "";
        public double Seconds;
        public string Model = "";
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    internal static async Task<Result> ParseAsync(string request, OcrClient.OcrConfig cfg,
                                                   CancellationToken ct)
    {
        var res = new Result { Model = cfg?.Model ?? "" };
        if (cfg == null || !cfg.IsConfigured)
        {
            res.Error = "未配置 AI API";
            return res;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string J(string s) => "\"" + JsonEncodedText.Encode(s ?? "").ToString() + "\"";
            string body = "{\"model\":" + J(cfg.Model) +
                          ",\"stream\":false,\"temperature\":0,\"max_tokens\":600," +
                          "\"messages\":[{\"role\":\"system\",\"content\":" +
                          J("你是数学作图助手。只输出一个 JSON 对象，不要 markdown。") + "}," +
                          "{\"role\":\"user\",\"content\":" +
                          J("把下面的作图要求解析成函数表达式。只支持一个自变量 x，输出：\n" +
                            "{\"functions\":[{\"expr\":\"ln(x)\",\"label\":\"y=ln(x)\"}]," +
                            "\"domain\":[0.01,6],\"title\":\"对数函数\"}\n" +
                            "expr 用 * 表示乘法、^ 表示乘方，函数支持 sin/cos/tan/sqrt/exp/ln/log。" +
                            "定义域必须避开 log/sqrt 的非法区间。\n用户要求：" + (request ?? "")) +
                          "}]}";

            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(55));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead,
                                                  cts.Token).ConfigureAwait(false);
            string raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            res.Raw = raw;
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
                    { content = v.GetString(); break; }
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
            var spec = new PlotSpec
            {
                Title = Str(root, "title"),
                XMin = -5f, XMax = 5f,
            };
            if (root.TryGetProperty("domain", out var dom) && dom.ValueKind == JsonValueKind.Array)
            {
                var nums = new List<float>();
                foreach (var n in dom.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.Number) nums.Add((float)n.GetDouble());
                if (nums.Count >= 2 && nums[1] > nums[0])
                { spec.XMin = nums[0]; spec.XMax = nums[1]; }
            }

            Color4[] palette =
            {
                new(0.12f, 0.14f, 0.18f, 1f),
                new(0.12f, 0.38f, 0.86f, 1f),
                new(0.82f, 0.16f, 0.16f, 1f),
                new(0.10f, 0.58f, 0.30f, 1f),
            };
            if (root.TryGetProperty("functions", out var fns) && fns.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var f in fns.EnumerateArray())
                {
                    string expr = f.ValueKind == JsonValueKind.String
                        ? f.GetString()
                        : Str(f, "expr");
                    if (string.IsNullOrWhiteSpace(expr)) continue;
                    spec.Series.Add(new PlotSeries
                    {
                        Expr = expr.Trim(),
                        Label = "y=" + expr.Trim(),   // 不信任模型给 label：直接由解析式生成，避免错字
                        Color = palette[idx % palette.Length],
                    });
                    idx++;
                }
            }
            else
            {
                string expr = Str(root, "expr");
                if (!string.IsNullOrWhiteSpace(expr))
                    spec.Series.Add(new PlotSeries
                    {
                        Expr = expr.Trim(), Label = "y=" + expr.Trim(), Color = palette[0],
                    });
            }

            if (spec.Series.Count == 0)
            {
                res.Error = "模型没有给出函数表达式";
                return res;
            }
            // 本地验证表达式；有非法项就报错，避免画空图。
            foreach (var s in spec.Series)
            {
                try { PlotExpression.Parse(s.Expr); }
                catch (Exception ex)
                {
                    res.Error = "表达式无法解析：" + s.Expr + "（" + ex.Message + "）";
                    return res;
                }
            }
            if (string.IsNullOrWhiteSpace(spec.Title))
                spec.Title = string.Join("，", spec.Series.ConvertAll(s => s.Label));
            res.Spec = spec;
            res.Ok = true;
            return res;
        }
        catch (OperationCanceledException) { res.Error = "AI 请求超时"; return res; }
        catch (Exception ex) { res.Error = ex.Message; return res; }
        finally { res.Seconds = sw.Elapsed.TotalSeconds; }
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
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : s;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : "";

    private static string Clip(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
}
