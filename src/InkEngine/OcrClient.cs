using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        /// <summary>带归一化 bbox 的行（识别到文本框时用；没有 bbox 时为空）。</summary>
        public List<OcrLine> Items = new();
        public double Seconds;
        public int PromptTokens;
        public int CompletionTokens;
    }

    /// <summary>OCR 的一行：文本 + 相对图片的归一化包围盒（0..1）。</summary>
    internal sealed class OcrLine
    {
        public string Text = "";
        public float X0, Y0, X1, Y1;
        public bool HasBox;
    }

    /// <summary>配置文件路径：`%APPDATA%\InkTeach\ai.json`（密钥不进仓库/安装包）。</summary>
    internal static string ConfigPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "InkTeach", "ai.json");

    /// <summary>保存 BYOK 配置（覆盖 ai.json；环境变量仍然优先）。</summary>
    internal static void SaveConfig(OcrConfig cfg)
    {
        if (cfg == null) return;
        string file = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var obj = new
        {
            baseUrl = (cfg.BaseUrl ?? "").Trim(),
            key = (cfg.Key ?? "").Trim(),
            model = (cfg.Model ?? "").Trim(),
            thinking = string.IsNullOrWhiteSpace(cfg.Thinking) ? "auto" : cfg.Thinking.Trim(),
        };
        File.WriteAllText(file, JsonSerializer.Serialize(obj,
            new JsonSerializerOptions { WriteIndented = true }));
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
                                                         string prompt, CancellationToken ct,
                                                         bool withBoxes = false)
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
                ["max_tokens"] = withBoxes ? 4096 : 2400,
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

            if (withBoxes)
            {
                var (pw, ph) = PngSize(png);
                if (TryParseBoxes(content, pw, ph, out var items))
                {
                    res.Items = items;
                    res.Lines = new string[items.Count];
                    for (int i = 0; i < items.Count; i++) res.Lines[i] = items[i].Text;
                    if (HasRepeat(res.Lines))
                    {
                        res.Error = "识别结果连续复读，请重试";
                        res.Lines = Array.Empty<string>();
                        return res;
                    }
                    res.Ok = res.Lines.Length > 0;
                    if (!res.Ok) res.Error = "识别结果为空";
                    ReadUsage(root, res);
                    return res;
                }
            }

            res.Lines = OcrText.FilterLines(SplitLines(content));
            if (HasRepeat(res.Lines))
            {
                res.Error = "识别结果连续复读，请重试";
                res.Lines = Array.Empty<string>();
                return res;
            }
            res.Ok = res.Lines.Length > 0;
            if (!res.Ok) res.Error = "识别结果为空";

            ReadUsage(root, res);
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

    private static void ReadUsage(JsonElement root, OcrResult res)
    {
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt)) res.PromptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ctok)) res.CompletionTokens = ctok.GetInt32();
        }
    }

    /// <summary>PNG 宽高（IHDR 固定在第 16..23 字节，大端）。</summary>
    private static (int W, int H) PngSize(byte[] png)
    {
        if (png == null || png.Length < 24) return (0, 0);
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (Math.Max(0, w), Math.Max(0, h));
    }

    /// <summary>
    /// 解析“每行一个对象”的 bbox 结果。坐标允许 0..1；超出就丢掉 bbox，
    /// 文本仍保留（后面的逐行着色会退回整框主色）。
    ///
    /// **不直接用 JsonDocument 读 text**：模型把 LaTeX 的 `\notin` 原样写进 JSON 时，
    /// `\n` / `\t` / `\b` 会被 JSON 当成换行、制表、退格，公式就悄悄坏了。
    /// 这里按固定字段顺序用正则抓原文，再只还原 `\"` 和 `\\`，其余反斜杠原样保留。
    /// </summary>
    private static bool TryParseBoxes(string content, int pngW, int pngH, out List<OcrLine> items)
    {
        items = new List<OcrLine>();
        if (string.IsNullOrWhiteSpace(content)) return false;

        string last = null;
        foreach (Match m in BoxItemRegex.Matches(content))
        {
            string text = UnescapeJsonText(m.Groups["text"].Value).Trim();
            if (text.Length == 0 || text == last) continue;
            if (OcrText.IsJunkLine(text)) continue;
            last = text;

            var line = new OcrLine { Text = text };
            float x0 = Num(m.Groups["x0"].Value);
            float y0 = Num(m.Groups["y0"].Value);
            float x1 = Num(m.Groups["x1"].Value);
            float y1 = Num(m.Groups["y1"].Value);
            NormalizeBox(line, x0, y0, x1, y1, pngW, pngH);
            items.Add(line);
        }
        if (items.Count > 0) return true;

        // 字段顺序不是我们要求的（或模型换了格式）：退回标准 JSON 解析。
        int start = content.IndexOf('[');
        int end = content.LastIndexOf(']');
        if (start < 0 || end <= start) return false;
        string json = content.Substring(start, end - start + 1);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            try { doc = JsonDocument.Parse(FixJsonEscapes(json)); }
            catch { return false; }
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            last = null;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
                string text = (t.GetString() ?? "").Trim();
                if (text.Length == 0 || text == last) continue;
                if (OcrText.IsJunkLine(text)) continue;
                last = text;

                var line = new OcrLine { Text = text };
                if (TryNum(e, "x0", out float x0) && TryNum(e, "y0", out float y0) &&
                    TryNum(e, "x1", out float x1) && TryNum(e, "y1", out float y1))
                    NormalizeBox(line, x0, y0, x1, y1, pngW, pngH);
                items.Add(line);
            }
        }
        return items.Count > 0;
    }

    private static readonly Regex BoxItemRegex = new(
        @"""text""\s*:\s*""(?<text>(?:\\.|[^""\\])*)""\s*,\s*""x0""\s*:\s*(?<x0>[^,}]+)\s*,\s*""y0""\s*:\s*(?<y0>[^,}]+)\s*,\s*""x1""\s*:\s*(?<x1>[^,}]+)\s*,\s*""y1""\s*:\s*(?<y1>[^,}]+)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>只还原 JSON 的 `\"` / `\\`；`\in`、`\frac` 这类 LaTeX 保持原样。</summary>
    private static string UnescapeJsonText(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '\\'))
            {
                sb.Append(s[i + 1]);
                i++;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static float Num(string raw)
    {
        raw = (raw ?? "").Trim().Trim('"');
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out float v)
            ? v : 0f;
    }

    private static void NormalizeBox(OcrLine line, float x0, float y0, float x1, float y1,
                                     int pngW, int pngH)
    {
        if (x0 > 1.5f || y0 > 1.5f || x1 > 1.5f || y1 > 1.5f)
        {
            // 有些厂商返回像素；能换算就换算，不能就不信这个框。
            if (pngW > 0 && pngH > 0 && x1 <= pngW * 1.1f && y1 <= pngH * 1.1f)
            {
                x0 /= pngW; x1 /= pngW; y0 /= pngH; y1 /= pngH;
            }
        }
        if (x0 >= -0.05f && y0 >= -0.05f && x1 <= 1.05f && y1 <= 1.05f && x1 > x0 && y1 > y0)
        {
            line.X0 = Math.Clamp(x0, 0f, 1f);
            line.Y0 = Math.Clamp(y0, 0f, 1f);
            line.X1 = Math.Clamp(x1, 0f, 1f);
            line.Y1 = Math.Clamp(y1, 0f, 1f);
            line.HasBox = true;
        }
    }

    private static bool TryNum(JsonElement e, string name, out float value)
    {
        value = 0f;
        if (!e.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d))
        {
            value = (float)d;
            return true;
        }
        if (p.ValueKind == JsonValueKind.String &&
            float.TryParse(p.GetString(), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;
        return false;
    }

    /// <summary>模型常把 LaTeX 的 `\in` 直接写进 JSON（非法转义）；补一层反斜杠再试。</summary>
    private static string FixJsonEscapes(string s)
        => Regex.Replace(s, @"\\(?![""\\/bfnrtu])", @"\\");

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
        "- 逐字照抄，保留原标点；特别注意下标 / 上标（如 D₁C₁ 不要写成 DD₁）和集合符号（⊂ / ⊄ / ⊆ 不要混用）；\n" +
        "- 复杂公式用 LaTeX，行内包在 $...$ 里；\n" +
        "- 不描述图形、箭头、圈注，不解释，不加标题，不加编号；\n" +
        "- 认不出的字写 ？，不要猜测。";

    /// <summary>识别到文本框：只保留正文/公式，明确不要图形旁的字母数字标注。</summary>
    internal const string TextBoxPrompt =
        BoardPrompt + "\n" +
        "- 不转录图形、坐标轴、立方体顶点旁的字母 / 数字标注（如 A/B/C/D、0/1/x/y、B′ 等），只转录正文段落和公式。";

    /// <summary>
    /// 识别到文本框 + 逐行 bbox。**JSON 里的反斜杠必须转义**（LaTeX 用 `\\in`），
    /// 解析端还会再容错一次（<see cref="FixJsonEscapes"/>）。
    /// </summary>
    internal const string BoxPrompt =
        TextBoxPrompt + "\n\n" +
        "输出格式：只输出一个 JSON 数组，不要 Markdown 代码块，不要解释。每个元素一行：\n" +
        "{\"text\":\"这一行的文字（公式用 $...$，JSON 里的反斜杠写成 \\\\）\",\"x0\":0.05,\"y0\":0.10,\"x1\":0.95,\"y1\":0.18}\n" +
        "坐标是相对整张图片的 0..1 小数：x0/y0 左上，x1/y1 右下；按从上到下、从左到右的阅读顺序排列。";
}
