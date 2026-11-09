using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// AI 作图：把“画 y=ln(x)”/“画一个椭圆”/“画一个正方体”这类自然语言
/// 解析成解析式或结构化图形；实际求值/采样/画图全在本地，避免让模型给像素。
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
            string prompt =
                "把下面的作图要求解析成一个 JSON 对象。只输出 JSON，不要 markdown 或解释。\n" +
                "kind 只能是 functions / conic / plane / solid。\n" +
                "1) 函数（可含参数）：\n" +
                "{\"kind\":\"functions\",\"functions\":[{\"expr\":\"a*x^2+b*x+c\"}]," +
                "\"params\":{\"a\":1,\"b\":-2,\"c\":1},\"domain\":[-5,5],\"title\":\"含参二次函数\"}\n" +
                "2) 圆锥曲线：\n" +
                "{\"kind\":\"conic\",\"conic\":{\"type\":\"ellipse\",\"h\":0,\"k\":0,\"a\":3,\"b\":2," +
                "\"axis\":\"right\",\"equation\":\"x^2/9+y^2/4=1\"},\"domain\":[-6,6],\"title\":\"椭圆\"}\n" +
                "type 为 ellipse/hyperbola/parabola；抛物线用 axis=right/left/up/down、a=焦准距 p。\n" +
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
                "规则：只支持一个自变量 x；expr 用 * 表示乘法、^ 表示乘方；" +
                "函数支持 sin/cos/tan/sqrt/exp/ln/log/abs。含参函数必须在 params 里给默认值，" +
                "expr 里可以用参数名。定义域要避开 log/sqrt 的非法区间。" +
                "几何点坐标取 0..10 的方便数值，连线要闭合完整。\n" +
                "用户要求：" + (request ?? "");
            string body = "{\"model\":" + J(cfg.Model) +
                          ",\"stream\":false,\"temperature\":0,\"max_tokens\":1200," +
                          "\"messages\":[{\"role\":\"system\",\"content\":" +
                          J("你是数学作图助手。只输出一个 JSON 对象，不要 markdown。") + "}," +
                          "{\"role\":\"user\",\"content\":" + J(prompt) + "}]}";

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
            var spec = ParseSpec(root);
            if (spec == null)
            {
                res.Error = "模型返回的 JSON 缺少可绘制内容";
                return res;
            }
            res.Spec = spec;
            res.Ok = true;
            return res;
        }
        catch (OperationCanceledException) { res.Error = "AI 请求超时"; return res; }
        catch (Exception ex) { res.Error = ex.Message; return res; }
        finally { res.Seconds = sw.Elapsed.TotalSeconds; }
    }

    /// <summary>把模型 JSON 转成 PlotSpec；所有坐标/表达式都在本地校验。</summary>
    private static PlotSpec ParseSpec(JsonElement root)
    {
        string kind = Str(root, "kind").Trim().ToLowerInvariant();
        var spec = new PlotSpec
        {
            Title = Str(root, "title"),
            XMin = -5f,
            XMax = 5f,
        };
        ReadDomain(root, spec);

        // 参数默认值（含参函数）。
        if (root.TryGetProperty("params", out var prm) && prm.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in prm.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number)
                    spec.Params[p.Name.ToLowerInvariant()] = p.Value.GetDouble();
            }
        }

        if (kind is "geometry2d" or "geometry3d")
            kind = kind == "geometry3d" ? "solid" : "plane";
        if (string.IsNullOrEmpty(kind))
        {
            if (root.TryGetProperty("conic", out _)) kind = "conic";
            else if (root.TryGetProperty("figure", out _))
            {
                kind = root.TryGetProperty("figure", out var fg)
                       && Str(fg, "kind") == "geometry3d" ? "solid" : "plane";
            }
            else kind = "functions";
        }

        switch (kind)
        {
            case "conic":
                if (!ParseConic(root, spec)) return null;
                spec.Kind = "conic";
                if (spec.XMin == -5f && spec.XMax == 5f)
                { spec.XMin = -8f; spec.XMax = 8f; }
                if (string.IsNullOrWhiteSpace(spec.Title))
                    spec.Title = spec.Conic?.Label ?? "圆锥曲线";
                return spec;

            case "plane":
            case "solid":
            {
                if (!root.TryGetProperty("figure", out var fig)
                    || fig.ValueKind != JsonValueKind.Object) return null;
                spec.Figure = FigureSpec.Parse(fig.GetRawText());
                if (spec.Figure == null) return null;
                spec.Kind = kind;
                if (string.IsNullOrWhiteSpace(spec.Title))
                    spec.Title = kind == "solid" ? "立体几何" : "平面几何";
                return spec;
            }

            default:
            {
                Color4[] palette =
                {
                    new(0.12f, 0.14f, 0.18f, 1f),
                    new(0.12f, 0.38f, 0.86f, 1f),
                    new(0.82f, 0.16f, 0.16f, 1f),
                    new(0.10f, 0.58f, 0.30f, 1f),
                };
                if (root.TryGetProperty("functions", out var fns)
                    && fns.ValueKind == JsonValueKind.Array)
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
                            // 不信任模型给 label：直接由解析式生成，避免错字。
                            Label = "y=" + expr.Trim(),
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
                            Expr = expr.Trim(), Label = "y=" + expr.Trim(),
                            Color = palette[0],
                        });
                }
                if (spec.Series.Count == 0) return null;
                // 本地验证表达式；有非法项就报错，避免画空图。
                foreach (var s in spec.Series)
                {
                    try { PlotExpression.Parse(s.Expr, spec.Params); }
                    catch (Exception ex)
                    {
                        throw new FormatException(
                            "表达式无法解析：" + s.Expr + "（" + ex.Message + "）");
                    }
                }
                spec.Kind = "functions";
                if (string.IsNullOrWhiteSpace(spec.Title))
                    spec.Title = string.Join("，", spec.Series.ConvertAll(s => s.Label));
                return spec;
            }
        }
    }

    private static bool ParseConic(JsonElement root, PlotSpec spec)
    {
        if (!root.TryGetProperty("conic", out var cr) || cr.ValueKind != JsonValueKind.Object)
            return false;
        string type = Str(cr, "type").Trim().ToLowerInvariant();
        if (type is not ("ellipse" or "hyperbola" or "parabola"))
            type = "ellipse";
        var c = new PlotConic
        {
            Type = type,
            H = Num(cr, "h"),
            K = Num(cr, "k"),
            A = Num(cr, "a", type == "parabola" ? 2f : 3f),
            B = Num(cr, "b", 2f),
            Axis = Str(cr, "axis"),
            Equation = Str(cr, "equation"),
            Label = Str(cr, "label"),
        };
        if (string.IsNullOrWhiteSpace(c.Axis)) c.Axis = "right";
        if (c.A <= 0.01f || c.B <= 0.01f)
            throw new FormatException("圆锥曲线参数 a/b 必须为正数");
        if (!string.IsNullOrWhiteSpace(c.Equation)
            && !string.IsNullOrWhiteSpace(c.Label)
            && !c.Label.Contains(c.Equation, StringComparison.Ordinal))
            c.Label += "  " + c.Equation;
        spec.Conic = c;
        spec.XMin = -8f;
        spec.XMax = 8f;
        ReadDomain(root, spec);
        return true;
    }

    private static void ReadDomain(JsonElement root, PlotSpec spec)
    {
        if (!root.TryGetProperty("domain", out var dom) || dom.ValueKind != JsonValueKind.Array)
            return;
        var nums = new List<float>();
        foreach (var n in dom.EnumerateArray())
            if (n.ValueKind == JsonValueKind.Number) nums.Add((float)n.GetDouble());
        if (nums.Count >= 2 && nums[1] > nums[0])
        { spec.XMin = nums[0]; spec.XMax = nums[1]; }
    }

    private static float Num(JsonElement e, string name, float fallback = 0f)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (float)v.GetDouble() : fallback;

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
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : "";

    private static string Clip(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
}
