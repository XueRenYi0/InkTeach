using System.Diagnostics;

namespace InkEngine;

/// <summary>设置页「AI 服务」用的只读服务商信息（不把 OcrProviders 的内部类型暴露给界面）。</summary>
public sealed class AiApiProviderInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Key { get; init; } = "";
    public string Model { get; init; } = "";
    public string KeyUrl { get; init; } = "";
    public bool Builtin { get; init; }
}

public sealed class AiApiSettingsSnapshot
{
    public string ActiveId { get; init; } = "";
    public List<AiApiProviderInfo> Providers { get; init; } = new();
}

public sealed class AiApiSaveRequest
{
    public string Id { get; init; } = "";
    public bool IsNew { get; init; }
    public string BaseUrl { get; init; } = "";
    public string Key { get; init; } = "";
    public string Model { get; init; } = "";
}

public sealed class AiApiModelsResult
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
    public List<string> Models { get; init; } = new();
}

/// <summary>
/// 设置页「AI 服务」的数据入口。界面只拿 DTO，不直接碰 OcrProviders/OcrClient 内部类型。
/// 一个窗口一个实例；调用都在 UI 线程，只有 FetchModelsAsync 是异步。
/// </summary>
public interface IAiApiUiService
{
    AiApiSettingsSnapshot Load();
    bool Save(AiApiSaveRequest request, out string error, out string activeId);
    bool Delete(string id, out string error);
    Task<AiApiModelsResult> FetchModelsAsync(string baseUrl, string key);
    void OpenKeyUrl(string id);
}

public partial class InkEngine
{
    private AiApiUiService _aiApiUi;

    internal IAiApiUiService AiApi => _aiApiUi ??= new AiApiUiService(this);

    internal sealed class AiApiUiService : IAiApiUiService
    {
        private readonly InkEngine _engine;

        public AiApiUiService(InkEngine engine) => _engine = engine;

        public AiApiSettingsSnapshot Load()
        {
            var state = OcrProviders.Load();
            var snapshot = new AiApiSettingsSnapshot { ActiveId = state.ActiveId };
            foreach (var p in state.Providers)
            {
                snapshot.Providers.Add(new AiApiProviderInfo
                {
                    Id = p.Id,
                    Name = p.Name,
                    BaseUrl = p.BaseUrl,
                    Key = p.Key,
                    Model = p.Model,
                    KeyUrl = p.KeyUrl,
                    Builtin = p.Builtin,
                });
            }
            return snapshot;
        }

        public bool Save(AiApiSaveRequest req, out string error, out string activeId)
        {
            error = "";
            activeId = "";
            string baseUrl = (req?.BaseUrl ?? "").Trim();
            string key = req?.Key ?? "";
            string model = (req?.Model ?? "").Trim();
            if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || key.Length == 0 || model.Length == 0)
            {
                error = "服务地址要以 http/https 开头，API Key 和模型不能为空。";
                return false;
            }

            var state = OcrProviders.Load();
            var p = req.IsNew
                ? null
                : state.Providers.FirstOrDefault(x => x.Id == req.Id);
            if (p == null)
            {
                p = OcrProviders.NewCustom(baseUrl, state);
                state.Providers.Add(p);
            }
            if (!req.IsNew && !p.Builtin)
                p.Name = OcrProviders.DeriveName(baseUrl, state, p.Id);
            p.BaseUrl = baseUrl.TrimEnd('/');
            p.Key = key;
            p.Model = model;
            p.Thinking = "auto";
            state.ActiveId = p.Id;
            activeId = p.Id;

            try { OcrProviders.Save(state); }
            catch (Exception ex)
            {
                error = "保存失败：" + ex.Message;
                return false;
            }
            _engine.RefreshAiApiSummary();
            return true;
        }

        public bool Delete(string id, out string error)
        {
            error = "";
            var state = OcrProviders.Load();
            var p = state.Providers.FirstOrDefault(x => x.Id == id);
            if (p == null || p.Builtin)
            {
                error = "预置服务商不能删除。";
                return false;
            }
            state.Providers.Remove(p);
            if (state.ActiveId == id)
                state.ActiveId = OcrProviders.Presets[0].Id;
            try { OcrProviders.Save(state); }
            catch (Exception ex)
            {
                error = "删除失败：" + ex.Message;
                return false;
            }
            _engine.RefreshAiApiSummary();
            return true;
        }

        public async Task<AiApiModelsResult> FetchModelsAsync(string baseUrl, string key)
        {
            var result = new AiApiModelsResult();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
            {
                result = new AiApiModelsResult { Ok = false, Error = "先填服务地址和 API Key" };
                return result;
            }
            try
            {
                var cfg = new OcrClient.OcrConfig
                {
                    BaseUrl = baseUrl.Trim(),
                    Key = key,
                    Model = "",
                    Thinking = "auto",
                };
                var r = await OcrClient.ListModelsAsync(cfg, CancellationToken.None).ConfigureAwait(false);
                if (!r.Ok)
                    return new AiApiModelsResult { Ok = false, Error = r.Error ?? "获取失败" };
                var models = new List<string>(r.Models);
                models.Sort(StringComparer.OrdinalIgnoreCase);
                return new AiApiModelsResult { Ok = true, Models = models };
            }
            catch (Exception ex)
            {
                return new AiApiModelsResult { Ok = false, Error = ex.Message };
            }
        }

        public void OpenKeyUrl(string id)
        {
            var state = OcrProviders.Load();
            var p = state.Providers.FirstOrDefault(x => x.Id == id);
            string url = p?.KeyUrl ?? "";
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* 打不开就算了，状态行不阻塞 */ }
        }
    }
}
