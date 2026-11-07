using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InkEngine;

/// <summary>
/// 设置里的「AI 识别 API」窗口：
///   · 预置 5 家 + 多条自定义（自定义用域名自动命名，可删除）；
///   · 服务地址 / Key / 模型；Key 页面链接；
///   · 调 OpenAI 兼容 `/models` 拉模型，推荐项在显示名后缀「推荐」。
///
/// 原生 Win32 控件，不引入 WPF/WebView；主画布仍是 Direct2D。
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

    private IntPtr _apiHwnd, _apiProviderCombo, _apiBaseEdit, _apiKeyEdit, _apiModelCombo, _apiStatus;
    private IntPtr _apiLinkBtn, _apiFetchBtn, _apiDeleteBtn;
    private Native.WndProcDelegate _apiWndProc;
    private IntPtr _apiOldProc;
    private bool _apiOpen;

    private OcrProviderState _apiState;
    private string _apiSelectedId = "";
    private bool _apiEditingNew;
    private bool _apiFetching;
    private int _apiFetchGen;
    private OcrClient.ModelListResult _apiFetchResult;

    private const int IdProvider = 2101;
    private const int IdModel = 2102;
    private const int IdSave = 2001;
    private const int IdCancel = 2002;
    private const int IdDelete = 2004;
    private const int IdFetch = 2005;
    private const int IdLink = 2006;
    private const int WmAppFetch = 0x8000 + 1;

    internal void OpenAiApiSettingsFromUi()
    {
        if (_apiOpen && _apiHwnd != IntPtr.Zero)
        {
            Native.SetForegroundWindow(_apiHwnd);
            Native.SetFocus(_apiBaseEdit);
            return;
        }
        EnsureAiApiWindow();
        if (_apiHwnd == IntPtr.Zero) return;

        _apiState = OcrProviders.Load();
        _apiSelectedId = _apiState.Active?.Id ?? "";
        _apiEditingNew = false;
        BuildProviderCombo();
        SelectProviderById(_apiSelectedId);
        ApplyProviderSelection(_apiSelectedId, isNew: false);

        int w = (int)MathF.Round(640f * UiUnit);
        int h = (int)MathF.Round(430f * UiUnit);
        int x = 0, y = 0;
        if (_windows.Count > 0)
        {
            if (Native.GetWindowRect(_windows[0].Hwnd, out var r))
            {
                x = (r.Left + r.Right - w) / 2;
                y = (r.Top + r.Bottom - h) / 2;
            }
            Native.EnableWindow(_windows[0].Hwnd, false);
        }
        Native.SetWindowPos(_apiHwnd, Native.HWND_TOPMOST, x, y, w, h,
                            Native.SWP_SHOWWINDOW);
        Native.ShowWindow(_apiHwnd, Native.SW_SHOW);
        Native.SetForegroundWindow(_apiHwnd);
        Native.SetFocus(_apiBaseEdit);
        _apiOpen = true;
    }

    private void EnsureAiApiWindow()
    {
        if (_apiHwnd != IntPtr.Zero) return;
        _apiWndProc = AiApiWndProc;
        _apiHwnd = Native.CreateWindowEx(
            Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW,
            "STATIC", "",
            0x80800000L, // WS_POPUP | WS_BORDER
            0, 0, 640, 430, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_apiHwnd == IntPtr.Zero)
        {
            Console.WriteLine("AI API 窗口创建失败: " + Marshal.GetLastWin32Error());
            return;
        }

        _apiOldProc = Native.SetWindowLongPtr(
            _apiHwnd, Native.GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_apiWndProc));

        float u = UiUnit;
        int pad = (int)MathF.Round(20f * u);
        int rowH = (int)MathF.Round(30f * u);
        int gap = (int)MathF.Round(12f * u);
        int labelW = (int)MathF.Round(82f * u);
        int fieldX = pad + labelW;
        int fieldW = (int)MathF.Round(300f * u);
        int rightX = fieldX + fieldW + (int)MathF.Round(12f * u);
        int rightW = (int)MathF.Round(168f * u);
        var font = EnsureLibraryRenameFont(u);

        var title = Native.CreateWindowEx(0, "STATIC", "AI 识别 API",
            0x50000000L, pad, (int)MathF.Round(16f * u),
            (int)MathF.Round(300f * u), (int)MathF.Round(28f * u),
            _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(title, Native.WM_SETFONT, font, new IntPtr(1));

        MakeButton("✕", (int)MathF.Round(594f * u), (int)MathF.Round(12f * u),
                   (int)MathF.Round(28f * u), (int)MathF.Round(26f * u), font, IdCancel);

        int y0 = (int)MathF.Round(58f * u);
        _apiProviderCombo = MakeCombo(IdProvider, fieldX, y0, fieldW,
                                      (int)MathF.Round(rowH * 8f), font, editable: false);
        _apiBaseEdit = MakeEdit("", fieldX, y0 + rowH + gap, fieldW, rowH, font, password: false);
        _apiKeyEdit = MakeEdit("", fieldX, y0 + (rowH + gap) * 2, fieldW, rowH, font, password: true);
        _apiModelCombo = MakeCombo(IdModel, fieldX, y0 + (rowH + gap) * 3, fieldW,
                                   (int)MathF.Round(rowH * 10f), font, editable: true);

        MakeLabel("服务商", pad, y0, labelW, rowH, font);
        MakeLabel("服务地址", pad, y0 + rowH + gap, labelW, rowH, font);
        MakeLabel("API Key", pad, y0 + (rowH + gap) * 2, labelW, rowH, font);
        MakeLabel("模型", pad, y0 + (rowH + gap) * 3, labelW, rowH, font);

        _apiLinkBtn = MakeButton("获取 API Key ↗", rightX, y0, rightW, rowH, font, IdLink);
        _apiFetchBtn = MakeButton("获取模型", rightX, y0 + (rowH + gap) * 3,
                                  (int)MathF.Round(100f * u), rowH, font, IdFetch);

        int statusY = y0 + (rowH + gap) * 4 + (int)MathF.Round(4f * u);
        _apiStatus = Native.CreateWindowEx(0, "STATIC", "",
            0x50000000L, pad, statusY, (int)MathF.Round(600f * u),
            (int)MathF.Round(26f * u), _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(_apiStatus, Native.WM_SETFONT, font, new IntPtr(1));

        int by = (int)MathF.Round(300f * u);
        int btnW = (int)MathF.Round(88f * u);
        int btnH = (int)MathF.Round(30f * u);
        int bx = (int)MathF.Round((640f - 20f - 88f * 3f - 10f * 2f) * u);
        MakeButton("保存", bx, by, btnW, btnH, font, IdSave);
        MakeButton("取消", bx + btnW + (int)MathF.Round(10f * u), by, btnW, btnH, font, IdCancel);
        _apiDeleteBtn = MakeButton("删除", bx + (btnW + (int)MathF.Round(10f * u)) * 2,
                                   by, btnW, btnH, font, IdDelete);

        string hint = "保存后下次识别生效；Key 保存在 %APPDATA%\\InkTeach\\ai.json";
        var hs = Native.CreateWindowEx(0, "STATIC", hint,
            0x50000000L, pad, (int)MathF.Round(374f * u),
            (int)MathF.Round(600f * u), (int)MathF.Round(30f * u),
            _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(hs, Native.WM_SETFONT, font, new IntPtr(1));
    }

    private IntPtr MakeEdit(string text, int x, int y, int w, int h, IntPtr font, bool password)
    {
        long style = 0x50800000L | 0x0080L;
        if (password) style |= 0x0020L;
        var hwnd = Native.CreateWindowEx(0, "EDIT", text, style, x, y, w, h,
                                         _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(hwnd, Native.WM_SETFONT, font, new IntPtr(1));
        return hwnd;
    }

    private IntPtr MakeCombo(int id, int x, int y, int w, int h, IntPtr font, bool editable)
    {
        long style = 0x50210000L | (editable ? 0x0002L : 0x0003L);
        var hwnd = Native.CreateWindowEx(0, "COMBOBOX", "", style, x, y, w, h,
                                         _apiHwnd, new IntPtr(id), _hInstance, IntPtr.Zero);
        Native.SendMessage(hwnd, Native.WM_SETFONT, font, new IntPtr(1));
        return hwnd;
    }

    private void MakeLabel(string text, int x, int y, int w, int h, IntPtr font)
    {
        var hwnd = Native.CreateWindowEx(0, "STATIC", text, 0x50000000L, x, y, w, h,
                                         _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(hwnd, Native.WM_SETFONT, font, new IntPtr(1));
    }

    private IntPtr MakeButton(string text, int x, int y, int w, int h, IntPtr font, int id)
    {
        var hwnd = Native.CreateWindowEx(0, "BUTTON", text, 0x50010000L, x, y, w, h,
                                         _apiHwnd, new IntPtr(id), _hInstance, IntPtr.Zero);
        Native.SendMessage(hwnd, Native.WM_SETFONT, font, new IntPtr(1));
        return hwnd;
    }

    private static string ReadEdit(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(4096);
        Native.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private void SetStatus(string text) => Native.SetWindowTextW(_apiStatus, text ?? "");

    private void BuildProviderCombo()
    {
        Native.SendMessage(_apiProviderCombo, 0x014B, IntPtr.Zero, IntPtr.Zero); // CB_RESETCONTENT
        foreach (var p in _apiState.Providers)
            Native.SendMessageString(_apiProviderCombo, 0x0143, IntPtr.Zero, p.Name); // CB_ADDSTRING
        Native.SendMessageString(_apiProviderCombo, 0x0143, IntPtr.Zero, "＋ 新建自定义…");
    }

    private void SelectProviderById(string id)
    {
        int idx = _apiState.Providers.FindIndex(p => p.Id == id);
        Native.SendMessage(_apiProviderCombo, 0x014E, new IntPtr(idx >= 0 ? idx : 0),
                           IntPtr.Zero); // CB_SETCURSEL
    }

    private void ApplyProviderSelection(string id, bool isNew)
    {
        _apiEditingNew = isNew || string.IsNullOrEmpty(id);
        var p = _apiEditingNew ? null : _apiState.Providers.FirstOrDefault(x => x.Id == id);
        _apiSelectedId = p?.Id ?? "";

        Native.SetWindowTextW(_apiBaseEdit, p?.BaseUrl ?? "");
        Native.SetWindowTextW(_apiKeyEdit, p?.Key ?? "");
        Native.SendMessage(_apiModelCombo, 0x014B, IntPtr.Zero, IntPtr.Zero); // CB_RESETCONTENT

        if (p != null && !string.IsNullOrWhiteSpace(p.Model))
            Native.SendMessageString(_apiModelCombo, 0x0143, IntPtr.Zero,
                                     OcrProviders.DisplayModel(p.Id, p.Model));
        Native.SetWindowTextW(_apiModelCombo,
            p == null ? "" : OcrProviders.DisplayModel(p.Id, p.Model));

        bool custom = p != null && !p.Builtin;
        Native.EnableWindow(_apiDeleteBtn, custom);
        Native.EnableWindow(_apiLinkBtn, p != null && !string.IsNullOrWhiteSpace(p.KeyUrl));
        SetStatus(p == null
            ? "新建自定义：地址、Key、模型填好后保存"
            : (string.IsNullOrWhiteSpace(p.Key) ? "未配置 Key" : "已选择：" + p.Name));
    }

    private void ApplySelectedProvider()
    {
        int idx = (int)Native.SendMessage(_apiProviderCombo, 0x0147, IntPtr.Zero, IntPtr.Zero);
        if (idx < 0) return;
        if (idx >= _apiState.Providers.Count)
        {
            ApplyProviderSelection("", isNew: true);
            return;
        }
        ApplyProviderSelection(_apiState.Providers[idx].Id, isNew: false);
    }

    private OcrClient.OcrConfig CurrentFields()
    {
        string baseUrl = ReadEdit(_apiBaseEdit);
        return new OcrClient.OcrConfig
        {
            BaseUrl = baseUrl,
            Key = ReadEdit(_apiKeyEdit),
            Model = OcrProviders.StripModelTag(ReadEdit(_apiModelCombo)),
            Thinking = "auto",
        };
    }

    private void FetchModels()
    {
        if (_apiFetching) return;
        var cfg = CurrentFields();
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Key))
        {
            SetStatus("先填服务地址和 API Key");
            return;
        }
        _apiFetching = true;
        int gen = ++_apiFetchGen;
        Native.EnableWindow(_apiFetchBtn, false);
        SetStatus("正在获取模型…");
        Task.Run(async () =>
        {
            var r = await OcrClient.ListModelsAsync(cfg, CancellationToken.None).ConfigureAwait(false);
            if (gen != _apiFetchGen) return;
            _apiFetchResult = r;
            Native.PostMessage(_apiHwnd, WmAppFetch, IntPtr.Zero, IntPtr.Zero);
        });
    }

    private void ApplyFetchedModels()
    {
        _apiFetching = false;
        Native.EnableWindow(_apiFetchBtn, true);
        var r = _apiFetchResult;
        _apiFetchResult = null;
        if (r == null) return;
        if (!r.Ok)
        {
            SetStatus("获取失败：" + r.Error + "；可手动填写模型");
            return;
        }

        string providerId = _apiSelectedId;
        string current = OcrProviders.StripModelTag(ReadEdit(_apiModelCombo));
        var models = new List<string>(r.Models);
        if (!string.IsNullOrWhiteSpace(current) && !models.Contains(current)) models.Add(current);
        models.Sort((a, b) =>
        {
            int ar = OcrProviders.IsRecommended(providerId, a) ? 0 : IsVisionName(a) ? 1 : 2;
            int br = OcrProviders.IsRecommended(providerId, b) ? 0 : IsVisionName(b) ? 1 : 2;
            if (ar != br) return ar.CompareTo(br);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        Native.SendMessage(_apiModelCombo, 0x014B, IntPtr.Zero, IntPtr.Zero);
        foreach (var m in models)
            Native.SendMessageString(_apiModelCombo, 0x0143, IntPtr.Zero,
                                     OcrProviders.DisplayModel(providerId, m));
        Native.SetWindowTextW(_apiModelCombo,
            OcrProviders.DisplayModel(providerId, current));
        SetStatus($"已获取 {models.Count} 个模型");
    }

    private static bool IsVisionName(string m)
    {
        string s = (m ?? "").ToLowerInvariant();
        return s.Contains("vl") || s.Contains("vision") || s.Contains("omni")
               || s.Contains("ocr") || s.Contains("4v") || s.Contains("multimodal");
    }

    private void SaveAiApi()
    {
        string baseUrl = ReadEdit(_apiBaseEdit);
        string key = ReadEdit(_apiKeyEdit);
        string model = OcrProviders.StripModelTag(ReadEdit(_apiModelCombo));
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || key.Length == 0 || model.Length == 0)
        {
            Native.MessageBoxW(_apiHwnd,
                "服务地址要以 http/https 开头，API Key 和模型不能为空。",
                "AI 识别 API", 0x30);
            return;
        }

        var p = _apiEditingNew
            ? OcrProviders.NewCustom(baseUrl, _apiState)
            : _apiState.Providers.FirstOrDefault(x => x.Id == _apiSelectedId);
        if (p == null)
        {
            p = OcrProviders.NewCustom(baseUrl, _apiState);
            _apiState.Providers.Add(p);
        }
        if (!_apiEditingNew && !p.Builtin)
            p.Name = OcrProviders.DeriveName(baseUrl, _apiState, p.Id);
        p.BaseUrl = baseUrl.TrimEnd('/');
        p.Key = key;
        p.Model = model;
        p.Thinking = "auto";
        _apiState.ActiveId = p.Id;
        if (_apiEditingNew) _apiSelectedId = p.Id;

        try { OcrProviders.Save(_apiState); }
        catch (Exception ex)
        {
            Native.MessageBoxW(_apiHwnd, "保存失败：" + ex.Message, "AI 识别 API", 0x10);
            return;
        }
        RefreshAiApiSummary();
        Native.MessageBoxW(_apiHwnd, "已保存。下一次识别会使用新配置。",
                           "AI 识别 API", 0x40);
        CloseAiApiWindow();
    }

    private void DeleteSelectedProvider()
    {
        var p = _apiState.Providers.FirstOrDefault(x => x.Id == _apiSelectedId);
        if (p == null || p.Builtin) return;
        int yes = Native.MessageBoxW(_apiHwnd,
            $"删除自定义服务商「{p.Name}」？\n只删除这条配置，不影响其他服务商。",
            "AI 识别 API", 0x04 | 0x20);
        if (yes != 6) return;
        _apiState.Providers.Remove(p);
        if (_apiState.ActiveId == p.Id)
            _apiState.ActiveId = OcrProviders.Presets[0].Id;
        OcrProviders.Save(_apiState);
        _apiState = OcrProviders.Load();
        _apiSelectedId = _apiState.ActiveId;
        BuildProviderCombo();
        SelectProviderById(_apiSelectedId);
        ApplyProviderSelection(_apiSelectedId, isNew: false);
        RefreshAiApiSummary();
    }

    private void OpenKeyPage()
    {
        var p = _apiState.Providers.FirstOrDefault(x => x.Id == _apiSelectedId);
        string url = p?.KeyUrl ?? "";
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("打开失败：" + ex.Message);
        }
    }

    private IntPtr AiApiWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0111: // WM_COMMAND
            {
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                int code = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (id == IdProvider && code == 1) { ApplySelectedProvider(); return IntPtr.Zero; }
                if (id == IdSave) { SaveAiApi(); return IntPtr.Zero; }
                if (id == IdCancel) { CloseAiApiWindow(); return IntPtr.Zero; }
                if (id == IdDelete) { DeleteSelectedProvider(); return IntPtr.Zero; }
                if (id == IdFetch) { FetchModels(); return IntPtr.Zero; }
                if (id == IdLink) { OpenKeyPage(); return IntPtr.Zero; }
                break;
            }
            case WmAppFetch:
                ApplyFetchedModels();
                return IntPtr.Zero;
            case Native.WM_CLOSE:
                CloseAiApiWindow();
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                _apiHwnd = IntPtr.Zero;
                _apiOpen = false;
                _apiFetchGen++;
                _apiFetching = false;
                if (_windows.Count > 0)
                {
                    Native.EnableWindow(_windows[0].Hwnd, true);
                    Native.SetFocus(_windows[0].Hwnd);
                }
                return IntPtr.Zero;
        }
        return Native.CallWindowProc(_apiOldProc, hWnd, msg, wParam, lParam);
    }

    private void CloseAiApiWindow()
    {
        if (_apiHwnd != IntPtr.Zero) Native.DestroyWindow(_apiHwnd);
        _apiHwnd = IntPtr.Zero;
        _apiOpen = false;
    }
}
