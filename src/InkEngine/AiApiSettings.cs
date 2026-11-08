namespace InkEngine;

/// <summary>
/// AI 识别 API 的摘要（配置是否完整 / 当前模型 / 地址）。
///
/// 2026-11-08（A2.2）：设置页「AI 服务」已改成内嵌页面（<see cref="AiApiUiService"/>），
/// 原独立 Win32 配置窗已删除；这里只保留引擎内部要用的摘要缓存。
/// </summary>
public partial class InkEngine
{
    private bool? _aiApiConfigured;
    private string _aiApiModel = "";
    private string _aiApiBase = "";

    internal bool AiApiConfigured => EnsureAiApiSummary().Configured;
    internal string AiApiModel => EnsureAiApiSummary().Model;
    internal string AiApiBase => EnsureAiApiSummary().BaseUrl;

    private (bool Configured, string Model, string BaseUrl) EnsureAiApiSummary()
    {
        if (_aiApiConfigured is null)
        {
            var c = OcrClient.LoadConfig();
            _aiApiConfigured = c.IsConfigured;
            _aiApiModel = c.Model ?? "";
            _aiApiBase = c.BaseUrl ?? "";
        }
        return (_aiApiConfigured == true, _aiApiModel, _aiApiBase);
    }

    internal void RefreshAiApiSummary()
    {
        _aiApiConfigured = null;
        EnsureAiApiSummary();
        NotifyUiStateChanged();
    }
}
