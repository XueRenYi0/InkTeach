using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InkEngine;

/// <summary>一条 AI 识别服务配置。预置服务商和自定义服务商共用这一份结构。</summary>
internal sealed class OcrProviderProfile
{
    public string Id = "";
    public string Name = "";
    public string BaseUrl = "";
    public string Key = "";
    public string Model = "";
    public string Thinking = "auto";
    public bool Builtin;
    public string KeyUrl = "";
}

/// <summary>ai.json 的完整内容：当前选中的配置 + 所有配置（含多条自定义）。</summary>
internal sealed class OcrProviderState
{
    public string ActiveId = "";
    public readonly List<OcrProviderProfile> Providers = new();

    public OcrProviderProfile Active
        => Providers.FirstOrDefault(p => p.Id == ActiveId) ?? Providers.FirstOrDefault();
}

/// <summary>
/// 预置服务商 / 多条自定义 / ai.json 迁移。
/// 预置只提供默认值；用户保存过的 key/model 以文件为准。
/// </summary>
internal static class OcrProviders
{
    internal readonly record struct Preset(
        string Id, string Name, string BaseUrl, string DefaultModel,
        string KeyUrl, string[] RecommendedModels);

    public static readonly Preset[] Presets =
    {
        new("deepseek-official", "DeepSeek 官方", "https://api.deepseek.com",
            "deepseek-flash", "https://platform.deepseek.com/api_keys",
            new[] { "deepseek-flash" }),
        new("dashscope", "阿里百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "qwen3.5-omni-flash", "https://bailian.console.aliyun.com/",
            Array.Empty<string>()),
        new("volcengine-ark", "火山方舟", "https://ark.cn-beijing.volces.com/api/v3",
            "doubao-seed-2-1-lite-260915", "https://console.volcengine.com/ark",
            new[] { "doubao-seed-2-1-lite-260915" }),
        new("zhipu-glm", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4",
            "glm-4.5v", "https://open.bigmodel.cn/usercenter/apikeys",
            Array.Empty<string>()),
        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1",
            "Qwen/Qwen3-VL-32B-Instruct", "https://cloud.siliconflow.cn/account/ak",
            Array.Empty<string>()),
    };

    public static OcrProviderState Load()
    {
        var state = new OcrProviderState();
        string file = OcrClient.ConfigPath;
        if (!File.Exists(file))
        {
            AddMissingPresets(state);
            state.ActiveId = Presets[0].Id;
            return state;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.TryGetProperty("providers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                if (root.TryGetProperty("activeId", out var aid) && aid.ValueKind == JsonValueKind.String)
                    state.ActiveId = aid.GetString() ?? "";
                foreach (var e in arr.EnumerateArray())
                {
                    var p = ReadProfile(e);
                    if (p != null) state.Providers.Add(p);
                }
            }
            else
            {
                // 旧版扁平结构：迁移成一条，不立刻改写文件，等用户保存时再落新版。
                var p = new OcrProviderProfile
                {
                    BaseUrl = Str(root, "baseUrl") ?? "",
                    Key = Str(root, "key") ?? "",
                    Model = Str(root, "model") ?? "",
                    Thinking = Str(root, "thinking") ?? "auto",
                };
                var preset = MatchPreset(p.BaseUrl);
                if (preset is { } pr)
                {
                    p.Id = pr.Id; p.Name = pr.Name; p.Builtin = true; p.KeyUrl = pr.KeyUrl;
                    if (string.IsNullOrWhiteSpace(p.Model)) p.Model = pr.DefaultModel;
                }
                else
                {
                    p.Id = NewCustomId();
                    p.Name = DeriveName(p.BaseUrl, state, null);
                    p.Builtin = false;
                }
                state.Providers.Add(p);
                state.ActiveId = p.Id;
            }
        }
        catch
        {
            // 文件坏了：按默认配置起来，不阻塞软件。
        }

        AddMissingPresets(state);
        Normalize(state);
        if (string.IsNullOrWhiteSpace(state.ActiveId) || state.Providers.All(p => p.Id != state.ActiveId))
            state.ActiveId = state.Providers.FirstOrDefault()?.Id ?? Presets[0].Id;
        return state;
    }

    public static void Save(OcrProviderState state)
    {
        if (state == null) return;
        Normalize(state);
        string file = OcrClient.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var obj = new
        {
            activeId = state.ActiveId,
            providers = state.Providers.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                kind = p.Builtin ? "builtin" : "custom",
                baseUrl = p.BaseUrl,
                key = p.Key,
                model = p.Model,
                thinking = p.Thinking,
                keyUrl = p.KeyUrl,
            }).ToArray(),
        };
        File.WriteAllText(file, JsonSerializer.Serialize(obj,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void UpdateActive(OcrClient.OcrConfig cfg)
    {
        var state = Load();
        var p = state.Active;
        if (p == null)
        {
            p = new OcrProviderProfile { Id = NewCustomId(), Name = DeriveName(cfg.BaseUrl, state, null) };
            state.Providers.Add(p);
            state.ActiveId = p.Id;
        }
        p.BaseUrl = cfg.BaseUrl ?? "";
        p.Key = cfg.Key ?? "";
        p.Model = cfg.Model ?? "";
        p.Thinking = string.IsNullOrWhiteSpace(cfg.Thinking) ? "auto" : cfg.Thinking;
        Save(state);
    }

    public static bool IsRecommended(string providerId, string model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        foreach (var p in Presets)
            if (p.Id == providerId && p.RecommendedModels.Contains(model)) return true;
        return false;
    }

    public static string DisplayModel(string providerId, string model)
        => IsRecommended(providerId, model) ? model + "    推荐" : model;

    public static string StripModelTag(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int at = text.IndexOf("    推荐", StringComparison.Ordinal);
        return (at >= 0 ? text[..at] : text).Trim();
    }

    public static string DeriveName(string baseUrl, OcrProviderState state, string exceptId)
    {
        string host = "";
        try { host = new Uri(baseUrl).Host; } catch { }
        if (string.IsNullOrWhiteSpace(host)) host = "自定义服务";
        string name = host;
        int n = 2;
        while (state.Providers.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = host + " " + n++;
        return name;
    }

    public static OcrProviderProfile NewCustom(string baseUrl, OcrProviderState state)
    {
        return new OcrProviderProfile
        {
            Id = NewCustomId(),
            Name = DeriveName(baseUrl, state, null),
            BaseUrl = baseUrl ?? "",
            Model = "",
            Thinking = "auto",
            Builtin = false,
        };
    }

    public static string NewCustomId()
        => "custom-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    public static Preset? MatchPreset(string baseUrl)
    {
        string b = (baseUrl ?? "").Trim().TrimEnd('/');
        foreach (var p in Presets)
            if (string.Equals(p.BaseUrl.TrimEnd('/'), b, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    public static Preset? PresetById(string id)
    {
        foreach (var p in Presets) if (p.Id == id) return p;
        return null;
    }

    public static void AddMissingPresets(OcrProviderState state)
    {
        foreach (var preset in Presets)
        {
            if (state.Providers.Any(p => p.Id == preset.Id)) continue;
            state.Providers.Add(new OcrProviderProfile
            {
                Id = preset.Id,
                Name = preset.Name,
                BaseUrl = preset.BaseUrl,
                Model = preset.DefaultModel,
                Thinking = "auto",
                Builtin = true,
                KeyUrl = preset.KeyUrl,
            });
        }
        // 预置永远排在自定义前面，顺序稳定。
        state.Providers.Sort((a, b) =>
        {
            int ai = Array.FindIndex(Presets, p => p.Id == a.Id);
            int bi = Array.FindIndex(Presets, p => p.Id == b.Id);
            if (ai >= 0 && bi >= 0) return ai.CompareTo(bi);
            if (ai >= 0) return -1;
            if (bi >= 0) return 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static OcrProviderProfile ReadProfile(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string id = Str(e, "id") ?? "";
        string kind = Str(e, "kind") ?? "";
        bool builtin = string.Equals(kind, "builtin", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(id))
            id = builtin ? "" : NewCustomId();
        var p = new OcrProviderProfile
        {
            Id = id,
            Name = Str(e, "name") ?? "",
            BaseUrl = Str(e, "baseUrl") ?? "",
            Key = Str(e, "key") ?? "",
            Model = Str(e, "model") ?? "",
            Thinking = Str(e, "thinking") ?? "auto",
            Builtin = builtin,
            KeyUrl = Str(e, "keyUrl") ?? "",
        };
        if (builtin && PresetById(id) is { } preset)
        {
            p.Name = preset.Name;
            p.KeyUrl = preset.KeyUrl;
            if (string.IsNullOrWhiteSpace(p.BaseUrl)) p.BaseUrl = preset.BaseUrl;
            if (string.IsNullOrWhiteSpace(p.Model)) p.Model = preset.DefaultModel;
        }
        return p;
    }

    private static void Normalize(OcrProviderState state)
    {
        foreach (var p in state.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = NewCustomId();
            if (p.Builtin && PresetById(p.Id) is { } preset)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = preset.Name;
                if (string.IsNullOrWhiteSpace(p.BaseUrl)) p.BaseUrl = preset.BaseUrl;
                if (string.IsNullOrWhiteSpace(p.Model)) p.Model = preset.DefaultModel;
                if (string.IsNullOrWhiteSpace(p.KeyUrl)) p.KeyUrl = preset.KeyUrl;
            }
            else
            {
                p.Builtin = false;
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = DeriveName(p.BaseUrl, state, p.Id);
            }
            if (string.IsNullOrWhiteSpace(p.Thinking)) p.Thinking = "auto";
        }

        // 同 id 去重；同名的自定义补序号。
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in state.Providers)
        {
            while (!ids.Add(p.Id)) p.Id = NewCustomId();
            if (p.Builtin) continue;
            string n = p.Name;
            int suffix = 2;
            while (!names.Add(n)) n = p.Name + " " + suffix++;
            p.Name = n;
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // ---- 图片识别成题卡：挑“能看图”和“能写字”的两份配置 -------------------

    /// <summary>
    /// 这个模型能不能看图、看得好不好（越大越好，0 = 不能看）。
    /// 2026-11-10 实测：`qwen3.5-ocr` 对中文卷面最稳（22/22，1.2–2.0s）；
    /// `qwen3.5-omni-flash` 也能看（1.3s，手写批注会串进去）；`Qwen3-VL-32B` 慢（16s）。
    /// </summary>
    private static int VisionScore(string model)
    {
        string m = (model ?? "").ToLowerInvariant();
        if (m.Length == 0) return 0;
        if (m.Contains("embedding") || m.Contains("reranker") || m.Contains("tts") ||
            m.Contains("whisper") || m.Contains("audio") || m.Contains("realtime") ||
            m.Contains("livetranslate") || m.Contains("rerank"))
            return 0;
        if (m.Contains("ocr")) return 5;
        if (m.Contains("omni")) return 4;
        if (m.Contains("vl") || m.Contains("vision")) return 3;
        if (m.Contains("glm-4.5v") || m.Contains("glm-4v") || m.Contains("-4o")) return 3;
        if (m.Contains("image") || m.Contains("multimodal")) return 2;
        // 2026 年的 Qwen3.5–3.8 全系都能看图（qwen3.8-flash / qwen3.7-plus …）。
        // 分数压到 2：能看图、但实测比专用 OCR 慢很多（8–35s），只做兜底。
        foreach (string p in QwenImageSeries)
            if (m.StartsWith(p, StringComparison.Ordinal)) return 2;
        return 0;
    }

    private static readonly string[] QwenImageSeries =
        { "qwen3.8", "qwen3.7", "qwen3.6", "qwen3.5" };

    /// <summary>这个模型是不是“专门 OCR”，不适合拿来写 JSON。</summary>
    private static bool IsOcrOnly(string model)
        => (model ?? "").ToLowerInvariant().Contains("ocr");

    /// <summary>
    /// 挑一份能看图的配置：**当前选中能看就用当前选中的**（用户选的优先），
    /// 否则在其余有 Key 的配置里按 <see cref="VisionScore"/> 取最高分。
    /// 返回 null = 一个能看图的都没有。
    /// </summary>
    public static OcrProviderProfile PickVision(OcrProviderState state)
    {
        if (state == null) return null;
        var active = state.Active;
        if (active != null && !string.IsNullOrWhiteSpace(active.Key)
            && VisionScore(active.Model) > 0)
            return active;

        OcrProviderProfile best = null;
        int bestScore = 0;
        foreach (var p in state.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.BaseUrl)) continue;
            int score = VisionScore(p.Model);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>
    /// 挑一份“能写字”的配置（把 OCR 文字整理成题卡）：
    /// 当前选中能用就用当前选中；但当前若是 `*-ocr` 这种只认字的模型，就换一条别的。
    /// </summary>
    public static OcrProviderProfile PickText(OcrProviderState state)
    {
        if (state == null) return null;
        var active = state.Active;
        if (active != null && !string.IsNullOrWhiteSpace(active.Key)
            && !IsOcrOnly(active.Model) && active.Model.Length > 0)
            return active;

        foreach (var p in state.Providers)
            if (!string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Model)
                && !IsOcrOnly(p.Model))
                return p;
        return active;
    }

    public static OcrClient.OcrConfig ToConfig(OcrProviderProfile p, string modelOverride = null)
    {
        if (p == null) return null;
        return new OcrClient.OcrConfig
        {
            BaseUrl = p.BaseUrl,
            Key = p.Key,
            Model = string.IsNullOrWhiteSpace(modelOverride) ? p.Model : modelOverride,
            Thinking = p.Thinking,
        };
    }
}
