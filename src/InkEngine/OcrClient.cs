using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InkEngine;

/// <summary>
/// 板书识别（BYOK）：OpenAI 兼容的 `/chat/completions`，只负责“图片 → 逐行文字”。
///
/// 设计口径照 `调研/AI-板书识别-基准-2026-11-05.md`：
///   · 统一走 OpenAI 兼容协议，baseUrl / key / model 都可配；
///   · 关思考参数按厂商分支（DeepSeek / 智谱 / 火山用 thinking:{type:disabled}，
///     百炼 qwen3-vl-plus 本身不思考，不发额外字段）；
///   · 密钥不写进设置、不进安装包：优先环境变量，其次 `%APPDATA%\InkTeach\ai.json`；
///   · 超时 + 复读检测留给调用方（这里只做一次请求）。
/// </summary>
internal static class OcrClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>测试用：绕过环境变量 / ai.json。</summary>
    internal static OcrConfig TestOverride;

    internal sealed class OcrConfig
    {
        public string BaseUrl = "https://api.deepseek.com";
        public string Key = "";
        public string Model = "deepseek-flash";
        /// <summary>auto / disabled / low / none。</summary>
        public string Thinking = "auto";

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Key) &&
            !string.IsNullOrWhiteSpace(BaseUrl) &&
            !string.IsNullOrWhiteSpace(Model);

        public string Endpoint
        {
            get
            {
                string b = (BaseUrl ?? "").Trim().TrimEnd('/');
                if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
                return b + "/chat/completions";
            }
        }
    }

    internal sealed class OcrResult
    {
        public bool Ok;
        public string Error = "";
        public string Text = "";
        public string[] Lines = Array.Empty<string>();
        public double Seconds;
        public int PromptTokens;
        public int CompletionTokens;
    }

    /// <summary>读配置：环境变量 &gt; ai.json &gt; DeepSeek 官方默认。</summary>
    internal static OcrConfig LoadConfig()
    {
        if (TestOverride != null) return TestOverride;

        var cfg = new OcrConfig();
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkTeach");
        string file = Path.Combine(dir, "ai.json");
        if (File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                cfg.BaseUrl = Str(r, "baseUrl", "base_url") ?? cfg.BaseUrl;
                cfg.Key = Str(r, "key", "apiKey", "api_key") ?? cfg.Key;
                cfg.Model = Str(r, "model") ?? cfg.Model;
                cfg.Thinking = Str(r, "thinking") ?? cfg.Thinking;
            }
            catch { /* 配置坏了就当没配，走环境变量 */ }
        }

        string envBase = Environment.GetEnvironmentVariable("INKTEACH_AI_BASEURL");
        string envKey = Environment.GetEnvironmentVariable("INKTEACH_AI_KEY");
        string envModel = Environment.GetEnvironmentVariable("INKTEACH_AI_MODEL");
        string envThink = Environment.GetEnvironmentVariable("INKTEACH_AI_THINKING");
        if (!string.IsNullOrWhiteSpace(envBase)) cfg.BaseUrl = envBase;
        if (!string.IsNullOrWhiteSpace(envKey)) cfg.Key = envKey;
        if (!string.IsNullOrWhiteSpace(envModel)) cfg.Model = envModel;
        if (!string.IsNullOrWhiteSpace(envThink)) cfg.Thinking = envThink;

        // 测试期最省事的路径：只设 DEEPSEEK_API_KEY 也能跑。
        if (string.IsNullOrWhiteSpace(cfg.Key))
        {
            string ds = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (!string.IsNullOrWhiteSpace(ds))
            {
                cfg.Key = ds;
                cfg.BaseUrl = "https://api.deepseek.com";
                cfg.Model = "deepseek-flash";
            }
        }
        return cfg;
    }

    private static string Str(JsonElement r, params string[] names)
    {
        foreach (var n in names)
            if (r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    /// <summary>识别一张 PNG。失败时返回 Ok=false + 短错误，不抛异常给界面。</summary>
    internal static async Task<OcrResult> RecognizeAsync(byte[] png, OcrConfig cfg,
                                                         string prompt, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new OcrResult();
        try
        {
            string b64 = Convert.ToBase64String(png);
            var body = new Dictionary<string, object>
            {
                ["model"] = cfg.Model,
                ["stream"] = false,
                ["temperature"] = 0,
                ["max_tokens"] = 2400,
                ["messages"] = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/png;base64," + b64 } },
                        },
                    },
                },
            };

            string think = (cfg.Thinking ?? "auto").Trim().ToLowerInvariant();
            string host = (cfg.BaseUrl ?? "").ToLowerInvariant();
            if (think == "auto")
                think = (host.Contains("deepseek") || host.Contains("bigmodel") ||
                         host.Contains("volces") || host.Contains("ark.cn"))
                        ? "disabled" : "none";
            if (think == "disabled")
                body["thinking"] = new { type = "disabled" };
            else if (think == "low")
                body["reasoning_effort"] = "low";
            // none：百炼 qwen3-vl-plus 这类本来不思考的，什么都不发。

            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                       .ConfigureAwait(false);
            string raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                res.Error = ShortError(raw, (int)resp.StatusCode);
                return res;
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                res.Error = err.TryGetProperty("message", out var m) ? m.GetString() : "接口返回 error";
                return res;
            }
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                res.Error = "接口没有返回 choices";
                return res;
            }

            var msg = choices[0].GetProperty("message");
            string content = ContentText(msg);
            content = StripFence(content);
            res.Text = content;
            res.Lines = SplitLines(content);
            if (HasRepeat(res.Lines))
            {
                res.Error = "识别结果连续复读，请重试";
                res.Lines = Array.Empty<string>();
                return res;
            }
            res.Ok = res.Lines.Length > 0;
            if (!res.Ok) res.Error = "识别结果为空";

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) res.PromptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ctok)) res.CompletionTokens = ctok.GetInt32();
            }
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Error = "识别超时 / 已取消";
            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
        finally
        {
            sw.Stop();
            res.Seconds = sw.Elapsed.TotalSeconds;
        }
    }

    private static string ContentText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in c.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t)) sb.AppendLine(t.GetString());
            }
            return sb.ToString();
        }
        return "";
    }

    private static string StripFence(string s)
    {
        s = (s ?? "").Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        int nl = s.IndexOf('\n');
        if (nl >= 0) s = s.Substring(nl + 1);
        int end = s.LastIndexOf("```", StringComparison.Ordinal);
        if (end >= 0) s = s.Substring(0, end);
        return s.Trim();
    }

    private static string[] SplitLines(string s)
    {
        var lines = new List<string>();
        foreach (var line in (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0 || t == "```") continue;
            lines.Add(t);
        }
        return lines.ToArray();
    }

    private static bool HasRepeat(string[] lines)
    {
        int same = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.Equals(lines[i], lines[i - 1], StringComparison.Ordinal))
            {
                if (++same >= 3) return true;
            }
            else same = 0;
        }
        return false;
    }

    private static string ShortError(string raw, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var m))
                return $"HTTP {status}：{Clip(m.GetString(), 120)}";
        }
        catch { }
        return $"HTTP {status}：{Clip(raw, 120)}";
    }

    private static string Clip(string s, int n)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    /// <summary>板书识别的固定提示词（基准里验证过的那份）。</summary>
    internal const string BoardPrompt =
        "逐行转录图片中的手写板书内容。要求：\n" +
        "- 按从上到下的书写顺序输出，每行一段；\n" +
        "- 汉字照写，数学符号尽量用 Unicode（∈ ∉ ⊂ ⊄ ⊆ ∩ ∪ ∥ ∠ △）；\n" +
        "- 复杂公式用 LaTeX，行内包在 $...$ 里；\n" +
        "- 不描述图形、箭头、圈注，不解释，不加标题，不加编号；\n" +
        "- 认不出的字写 ？，不要猜测。";
}
