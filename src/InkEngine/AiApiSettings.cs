using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace InkEngine;

/// <summary>
/// 设置里的「AI 识别 API」窗口：服务地址 / Key / 模型。
///
/// 为什么用原生 Win32 控件，而不是 WPF/WebView：
///   · 这是低频设置，不值得再引入一套 UI 运行时；
///   · 中文输入法、密码框、Tab 顺序交给系统 EDIT/BUTTON；
///   · 主画布仍然是 Direct2D，透明/airspace 问题完全不碰。
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

    private IntPtr _apiHwnd, _apiBaseEdit, _apiKeyEdit, _apiModelEdit;
    private Native.WndProcDelegate _apiWndProc;
    private IntPtr _apiOldProc;
    private bool _apiOpen;

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

        var cfg = OcrClient.LoadConfig();
        Native.SetWindowTextW(_apiBaseEdit, cfg.BaseUrl ?? "");
        Native.SetWindowTextW(_apiKeyEdit, cfg.Key ?? "");
        Native.SetWindowTextW(_apiModelEdit, cfg.Model ?? "");

        int w = (int)MathF.Round(560f * UiUnit);
        int h = (int)MathF.Round(330f * UiUnit);
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
            0, 0, 560, 330, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
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
        int labelW = (int)MathF.Round(86f * u);
        int editX = pad + labelW;
        int editW = (int)MathF.Round(420f * u);
        int y0 = (int)MathF.Round(58f * u);
        var font = EnsureLibraryRenameFont(u);

        var title = Native.CreateWindowEx(0, "STATIC", "AI 识别 API",
            0x50000000L, pad, (int)MathF.Round(16f * u),
            (int)MathF.Round(300f * u), (int)MathF.Round(28f * u),
            _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(title, Native.WM_SETFONT, font, new IntPtr(1));

        MakeButton("✕", (int)MathF.Round(514f * u), (int)MathF.Round(12f * u),
                   (int)MathF.Round(28f * u), (int)MathF.Round(26f * u), font, 2002);

        _apiBaseEdit = MakeEdit("baseUrl", editX, y0, editW, rowH, font, password: false);
        _apiKeyEdit = MakeEdit("apiKey", editX, y0 + rowH + gap, editW, rowH, font, password: true);
        _apiModelEdit = MakeEdit("model", editX, y0 + (rowH + gap) * 2, editW, rowH, font, password: false);

        MakeLabel("服务地址", pad, y0, labelW, rowH, font);
        MakeLabel("API Key", pad, y0 + rowH + gap, labelW, rowH, font);
        MakeLabel("模型", pad, y0 + (rowH + gap) * 2, labelW, rowH, font);

        int btnW = (int)MathF.Round(88f * u);
        int btnH = (int)MathF.Round(30f * u);
        int by = (int)MathF.Round(222f * u);
        int bx = pad + editW + labelW - btnW * 2 - (int)MathF.Round(10f * u);
        MakeButton("保存", bx, by, btnW, btnH, font, 2001);
        MakeButton("取消", bx + btnW + (int)MathF.Round(10f * u), by, btnW, btnH, font, 2002);

        string hint = "保存后下次识别生效；Key 保存在 %APPDATA%\\InkTeach\\ai.json";
        var hs = Native.CreateWindowEx(0, "STATIC", hint,
            0x50000000L, pad, by + btnH + (int)MathF.Round(10f * u),
            (int)MathF.Round(500f * u), (int)MathF.Round(30f * u),
            _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(hs, Native.WM_SETFONT, font, new IntPtr(1));
    }

    private IntPtr MakeEdit(string text, int x, int y, int w, int h, IntPtr font, bool password)
    {
        long style = 0x50800000L | 0x0080L; // WS_CHILD|WS_VISIBLE|WS_TABSTOP|WS_BORDER|ES_AUTOHSCROLL
        if (password) style |= 0x0020L;     // ES_PASSWORD
        var hwnd = Native.CreateWindowEx(0, "EDIT", text, style, x, y, w, h,
                                         _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(hwnd, Native.WM_SETFONT, font, new IntPtr(1));
        return hwnd;
    }

    private void MakeLabel(string text, int x, int y, int w, int h, IntPtr font)
    {
        var hwnd = Native.CreateWindowEx(0, "STATIC", text, 0x50000000L, x, y, w, h,
                                         _apiHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Native.SendMessage(hwnd, Native.WM_SETFONT, font, new IntPtr(1));
    }

    private void MakeButton(string text, int x, int y, int w, int h, IntPtr font, int id)
    {
        var hwnd = Native.CreateWindowEx(0, "BUTTON", text, 0x50010000L, x, y, w, h,
                                         _apiHwnd, new IntPtr(id), _hInstance, IntPtr.Zero);
        Native.SendMessage(hwnd, Native.WM_SETFONT, font, new IntPtr(1));
    }

    private static string ReadEdit(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(4096);
        Native.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private IntPtr AiApiWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0111: // WM_COMMAND
            {
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                int code = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (code == 0 && id == 2001) { SaveAiApi(); return IntPtr.Zero; }
                if (code == 0 && id == 2002) { CloseAiApiWindow(); return IntPtr.Zero; }
                break;
            }
            case Native.WM_CLOSE:
                CloseAiApiWindow();
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                _apiHwnd = IntPtr.Zero;
                _apiOpen = false;
                if (_windows.Count > 0)
                {
                    Native.EnableWindow(_windows[0].Hwnd, true);
                    Native.SetFocus(_windows[0].Hwnd);
                }
                return IntPtr.Zero;
        }
        return Native.CallWindowProc(_apiOldProc, hWnd, msg, wParam, lParam);
    }

    private void SaveAiApi()
    {
        string baseUrl = ReadEdit(_apiBaseEdit);
        string key = ReadEdit(_apiKeyEdit);
        string model = ReadEdit(_apiModelEdit);
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || key.Length == 0 || model.Length == 0)
        {
            Native.MessageBoxW(_apiHwnd,
                "服务地址要以 http/https 开头，API Key 和模型不能为空。",
                "AI 识别 API", 0x30);
            return;
        }
        var cfg = OcrClient.LoadConfig();
        cfg.BaseUrl = baseUrl.TrimEnd('/');
        cfg.Key = key;
        cfg.Model = model;
        try { OcrClient.SaveConfig(cfg); }
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

    private void CloseAiApiWindow()
    {
        if (_apiHwnd != IntPtr.Zero) Native.DestroyWindow(_apiHwnd);
        _apiHwnd = IntPtr.Zero;
        _apiOpen = false;
    }
}
