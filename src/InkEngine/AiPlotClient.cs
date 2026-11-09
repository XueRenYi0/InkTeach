using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace InkEngine;

/// <summary>
/// AI 作图：把“画 y=ln(x)”/“画一个椭圆”/“画一个正方体”这类自然语言，
/// 或一张题目图片，解析成解析式/结构化图形；实际求值/采样/画图全在本地。
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

    internal static Task<Result> ParseAsync(string request, OcrClient.OcrConfig cfg,
                                            CancellationToken ct)
    {
        var res = new Result { Model = cfg?.Model ?? "" };
        if (cfg == null || !cfg.IsConfigured)
        {
            res.Error = "未配置 AI API";
            return Task.FromResult(res);
        }
        string body = BuildBody(cfg, BuildPrompt(request, false), null);
        return SendAsync(body, cfg, res, request, ct);
    }

    /// <summary>选中题目图片 → 模型直接看图，反推函数/圆锥/几何并输出同一套 JSON。</summary>
    internal static Task<Result> ParseImageAsync(byte[] png, string request,
                                                 OcrClient.OcrConfig cfg,
                                                 CancellationToken ct)
    {
        var res = new Result { Model = cfg?.Model ?? "" };
        if (cfg == null || !cfg.IsConfigured)
        {
            res.Error = "未配置 AI API";
            return Task.FromResult(res);
        }
        if (png == null || png.Length == 0)
        {
            res.Error = "图片为空";
            return Task.FromResult(res);
        }
        string body = BuildBody(cfg, BuildPrompt(request, true), png);
        return SendAsync(body, cfg, res, request, ct);
    }

    private static string BuildPrompt(string request, bool fromImage)
    {
        return
            (fromImage
                ? "先读图中的数学题目/图形，再解析成一个 JSON 对象。只输出 JSON，不要 markdown 或解释。\n"
                : "把下面的作图要求解析成一个 JSON 对象。只输出 JSON，不要 markdown 或解释。\n") +
            "kind 只能是 functions / conic / plane / solid。\n" +
            "1) 函数（可含参数）：\n" +
            "{\"kind\":\"functions\",\"functions\":[{\"expr\":\"a*x^2+b*x+c\"}]," +
            "\"params\":{\"a\":1,\"b\":-2,\"c\":1},\"domain\":[-5,5],\"title\":\"含参二次函数\"}\n" +
            "分段函数用 pieces（每段一个 expr + min/max 边界，min/max 可省略）：\n" +
            "{\"kind\":\"functions\",\"functions\":[{\"pieces\":[" +
            "{\"expr\":\"2^x\",\"min\":0,\"min_inclusive\":true}," +
            "{\"expr\":\"x^2\",\"max\":0,\"max_inclusive\":false}]}]," +
            "\"domain\":[-5,5],\"title\":\"分段函数\"}\n" +
            "2) 圆锥曲线：\n" +
            "{\"kind\":\"conic\",\"conic\":{\"type\":\"ellipse\",\"h\":0,\"k\":0,\"a\":3,\"b\":2," +
            "\"axis\":\"right\",\"equation\":\"x^2/9+y^2/4=1\"},\"domain\":[-6,6],\"title\":\"椭圆\"}\n" +
            "type 为 ellipse/hyperbola/parabola；双曲线用 axis=x 或 y（实轴方向）；" +
            "抛物线用 axis=right/left/up/down、a=焦准距 p。\n" +
            "3) 平面几何：\n" +
            "{\"kind\":\"plane\",\"figure\":{\"kind\":\"geometry2d\"," +
            "\"points\":{\"A\":[0,0],\"B\":[4,0],\"C\":[0,3]}," +
            "\"segments\":[[\"A\",\"B\"],[\"B\",\"C\"],[\"C\",\"A\"]]," +
            "\"labels\":{\"A\":\"A\",\"B\":\"B\",\"C\":\"C\"}},\"title\":\"直角三角形\"}\n" +
            "4) 立体几何：\n" +
            "{\"kind\":\"solid\",\"figure\":{\"kind\":\"geometry3d\"," +
            "\"points\":{\"A\":[0,0,0],\"B\":[2,0,0],\"C\":[2,2,0],\"D\":[0,2,0]," +
            "\"A1\":[0,0,2],\"B1\":[2,0,2],\"C1\":[2,2,2],\"D1\":[0,2,2]}," +
            "\"edges\":[[\"A\",\"B\"],[\"B\",\"C\"],[\"C\",\"D\"],[\"D\",\"A\"]," +
            "[\"A1\",\"B1\"],[\"B1\",\"C1\"],[\"C1\",\"D1\"],[\"D1\",\"A1\"]," +
            "[\"A\",\"A1\"],[\"B\",\"B1\"],[\"C\",\"C1\"],[\"D\",\"D1\"]]," +
            "\"labels\":{\"A\":\"A\",\"B\":\"B\"}},\"title\":\"正方体\"}\n" +
            (fromImage
                ? "如果图里是函数图象，反推出最可能的解析式；有参数就保留 a/b/c 并给默认值。" +
                  "只选一个最贴近的 kind，不要把整张图当成几何点线硬拆。\n"
                : "") +
            "规则：只支持一个自变量 x；expr 用 * 表示乘法、^ 表示乘方；" +
            "函数支持 sin/cos/tan/cot/sec/csc/sqrt/abs/exp/ln/log/log2/lg/cbrt。" +
            "高中写法：lg(x) 是以 10 为底，ln(x) 是自然对数，log2(x) 是以 2 为底；" +
            "含参函数必须在 params 里给默认值，" +
            "expr 里可以用参数名。定义域要避开 log/sqrt 的非法区间。" +
            "分段函数必须用 pieces（每段自己的 min/max），不能只写一个 expr；" +
            "注意：lg 是以10为底，必须写 lg(x)，不能写成 log2；log₂/log2 是以2为底，必须写 log2(x)；" +
            "几何点坐标取 0..10 的方便数值，连线要闭合完整。\n" +
            (fromImage ? "图片：" + (request ?? "") : "用户要求：" + (request ?? ""));
    }

    private static string BuildBody(OcrClient.OcrConfig cfg, string prompt, byte[] png)
    {
        string J(string s) => "\"" + JsonEncodedText.Encode(s ?? "").ToString() + "\"";
        string content = png == null
            ? J(prompt)
            : "[{\"type\":\"text\",\"text\":" + J(prompt) + "}," +
              "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64," +
              Convert.ToBase64String(png) + "\"}}]";
        return "{\"model\":" + J(cfg.Model) +
               ",\"stream\":false,\"temperature\":0,\"max_tokens\":1200," +
               "\"messages\":[{\"role\":\"system\",\"content\":" +
               J("你是数学作图助手。只输出一个 JSON 对象，不要 markdown。") + "}," +
               "{\"role\":\"user\",\"content\":" + content + "}]}";
    }

    private static async Task<Result> SendAsync(string body, OcrClient.OcrConfig cfg,
                                                Result res, string request,
                                                CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
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
            var spec = PlotSpecJson.Read(root);
            if (spec == null)
            {
                res.Error = "模型返回的 JSON 缺少可绘制内容";
                return res;
            }
            NormalizeLogNotation(spec, request);

            if (spec.Kind is "plane" or "solid")
            {
                if (!root.TryGetProperty("figure", out var fig)
                    || fig.ValueKind != JsonValueKind.Object)
                {
                    res.Error = "模型没有给出 figure 点线数据";
                    return res;
                }
                spec.Figure = FigureSpec.Parse(fig.GetRawText());
                if (spec.Figure == null)
                {
                    res.Error = "模型给的 figure 点线无法解析";
                    return res;
                }
                if (string.IsNullOrWhiteSpace(spec.Title))
                    spec.Title = spec.Kind == "solid" ? "立体几何" : "平面几何";
            }
            else if (spec.Kind == "conic")
            {
                if (spec.Conic == null)
                {
                    res.Error = "模型没有给出圆锥曲线参数";
                    return res;
                }
                if (spec.Conic.A <= 0.01f || spec.Conic.B <= 0.01f)
                {
                    res.Error = "圆锥曲线参数 a/b 必须为正数";
                    return res;
                }
                if (string.IsNullOrWhiteSpace(spec.Title))
                    spec.Title = spec.Conic.Label;
            }
            else
            {
                foreach (var s in spec.Series)
                {
                    if (s.Pieces.Count > 0)
                    {
                        foreach (var piece in s.Pieces)
                        {
                            try { PlotExpression.Parse(piece.Expr, spec.Params); }
                            catch (Exception ex)
                            {
                                throw new FormatException(
                                    "分段表达式无法解析：" + piece.Expr + "（" + ex.Message + "）");
                            }
                        }
                    }
                    else
                    {
                        try { PlotExpression.Parse(s.Expr, spec.Params); }
                        catch (Exception ex)
                        {
                            throw new FormatException(
                                "表达式无法解析：" + s.Expr + "（" + ex.Message + "）");
                        }
                    }
                }
                if (spec.Series.Count == 0)
                {
                    res.Error = "模型没有给出函数表达式";
                    return res;
                }
                if (string.IsNullOrWhiteSpace(spec.Title))
                    spec.Title = string.Join("，", spec.Series.ConvertAll(s => s.Label));
            }
            res.Spec = spec;
            res.Ok = true;
            return res;
        }
        catch (OperationCanceledException) { res.Error = "AI 请求超时"; return res; }
        catch (Exception ex) { res.Error = ex.Message; return res; }
        finally { res.Seconds = sw.Elapsed.TotalSeconds; }
    }

    private static void NormalizeLogNotation(PlotSpec spec, string request)
    {
        if (spec == null) return;
        string q = (request ?? "").ToLowerInvariant();
        bool hasLg = q.Contains("lg", StringComparison.Ordinal);
        bool hasLog2 = q.Contains("log2", StringComparison.Ordinal)
                       || q.Contains("log₂", StringComparison.Ordinal)
                       || q.Contains("log_2", StringComparison.Ordinal);
        if (hasLg == hasLog2) return;   // 两种都提到或都没提：不猜

        bool toLg = hasLg;
        foreach (var s in spec.Series)
        {
            if (s.Pieces.Count > 0)
            {
                foreach (var p in s.Pieces) p.Expr = RewriteLog(p.Expr, toLg);
            }
            else s.Expr = RewriteLog(s.Expr, toLg);
        }
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            spec.Title = RewriteLog(spec.Title, toLg);
            spec.Title = PlotSpecJson.HighSchool(spec.Title);
        }
        PlotSpecJson.RefreshLabels(spec);
    }

    private static string RewriteLog(string expr, bool toLg)
    {
        if (string.IsNullOrEmpty(expr)) return expr;
        return toLg
            ? expr.Replace("log2(", "lg(", StringComparison.Ordinal)
                  .Replace("log_2(", "lg(", StringComparison.Ordinal)
            : expr.Replace("log10(", "log2(", StringComparison.Ordinal)
                  .Replace("lg(", "log2(", StringComparison.Ordinal)
                  .Replace("log(", "log2(", StringComparison.Ordinal);
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

    private static string Clip(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
}
