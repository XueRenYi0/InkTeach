// B0 spike: windowless RichEdit (ITextServices + ITextHost), transparent background.
// Build with VS Build Tools (x64), run, screenshot. No product code touched.
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <richedit.h>
#include <initguid.h>
#include <textserv.h>
#include <imm.h>
#include <ole2.h>
#include <d2d1.h>
#include <wincodec.h>
#include <stdio.h>
#include <stdarg.h>
#include <string>

static const wchar_t* kClass = L"B0SpikeWindow";
static bool g_selfTest = false;
static bool g_bench = false;
static bool g_d2dTest = false;
static HMODULE g_msft = nullptr;

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }
// textserv.h only declares IID_ITextHost; define it locally for the spike.
static const GUID kITextHostIID =
    { 0x13e670f4, 0x1a5a, 0x11cf, { 0xab, 0xeb, 0x00, 0xaa, 0x00, 0xb6, 0x5e, 0xa1 } };
static ITextServices* g_svc = nullptr;
static class Host* g_host = nullptr;
static HBITMAP g_bgBitmap = nullptr;

static LRESULT SendSvc(UINT msg, WPARAM wp, LPARAM lp)
{
    LRESULT lr = 0;
    if (g_svc) g_svc->TxSendMessage(msg, wp, lp, &lr);
    return lr;
}

class Host : public ITextHost
{
public:
    HWND hwnd = nullptr;
    LONG ref = 1;
    CHARFORMATW cf = {};
    PARAFORMAT pf = {};

    Host(HWND h)
    {
        hwnd = h;
        cf.cbSize = sizeof(cf);
        cf.dwMask = CFM_FACE | CFM_SIZE | CFM_COLOR | CFM_CHARSET;
        wcscpy_s(cf.szFaceName, L"Microsoft YaHei UI");
        cf.yHeight = 320; // 16pt
        cf.crTextColor = RGB(20, 20, 24);
        pf.cbSize = sizeof(pf);
        pf.dwMask = PFM_ALIGNMENT;
        pf.wAlignment = PFA_LEFT;
    }

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == kITextHostIID)
        {
            *ppv = static_cast<ITextHost*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&ref); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG n = InterlockedDecrement(&ref);
        if (n == 0) delete this;
        return n;
    }

    // ITextHost
    HDC TxGetDC() override { return GetDC(hwnd); }
    INT TxReleaseDC(HDC hdc) override { return ReleaseDC(hwnd, hdc); }
    BOOL TxShowScrollBar(INT, BOOL) override { return FALSE; }
    BOOL TxEnableScrollBar(INT, INT) override { return FALSE; }
    BOOL TxSetScrollRange(INT, LONG, INT, BOOL) override { return FALSE; }
    BOOL TxSetScrollPos(INT, INT, BOOL) override { return FALSE; }
    void TxInvalidateRect(LPCRECT prc, BOOL fMode) override { InvalidateRect(hwnd, prc, fMode); }
    void TxViewChange(BOOL fUpdate) override { if (fUpdate) UpdateWindow(hwnd); }
    BOOL TxCreateCaret(HBITMAP hbmp, INT xWidth, INT yHeight) override
    {
        return CreateCaret(hwnd, hbmp, xWidth, yHeight);
    }
    BOOL TxShowCaret(BOOL fShow) override { return fShow ? ShowCaret(hwnd) : HideCaret(hwnd); }
    BOOL TxSetCaretPos(INT x, INT y) override { return SetCaretPos(x, y); }
    BOOL TxSetTimer(UINT id, UINT ms) override { return SetTimer(hwnd, id, ms, nullptr) != 0; }
    void TxKillTimer(UINT id) override { KillTimer(hwnd, id); }
    void TxScrollWindowEx(INT, INT, LPCRECT, LPCRECT, HRGN, LPRECT, UINT) override { }
    void TxSetCapture(BOOL fCapture) override
    {
        if (fCapture) SetCapture(hwnd); else ReleaseCapture();
    }
    void TxSetFocus() override { SetFocus(hwnd); }
    void TxSetCursor(HCURSOR hcur, BOOL) override { if (hcur) SetCursor(hcur); }
    BOOL TxScreenToClient(LPPOINT pt) override { return ScreenToClient(hwnd, pt); }
    BOOL TxClientToScreen(LPPOINT pt) override { return ClientToScreen(hwnd, pt); }
    HRESULT TxActivate(LONG* pOld) override { if (pOld) *pOld = 0; return S_OK; }
    HRESULT TxDeactivate(LONG) override { return S_OK; }
    HRESULT TxGetClientRect(LPRECT prc) override
    {
        if (!prc) return E_POINTER;
        GetClientRect(hwnd, prc);
        return S_OK;
    }
    HRESULT TxGetViewInset(LPRECT prc) override
    {
        if (!prc) return E_POINTER;
        prc->left = prc->top = prc->right = prc->bottom = 8;
        return S_OK;
    }
    HRESULT TxGetCharFormat(const CHARFORMATW** ppCF) override
    {
        if (!ppCF) return E_POINTER;
        *ppCF = &cf;
        return S_OK;
    }
    HRESULT TxGetParaFormat(const PARAFORMAT** ppPF) override
    {
        if (!ppPF) return E_POINTER;
        *ppPF = &pf;
        return S_OK;
    }
    COLORREF TxGetSysColor(int nIndex) override { return GetSysColor(nIndex); }
    HRESULT TxGetBackStyle(TXTBACKSTYLE* pstyle) override
    {
        if (!pstyle) return E_POINTER;
        *pstyle = TXTBACK_TRANSPARENT;   // <-- the whole point
        return S_OK;
    }
    HRESULT TxGetMaxLength(DWORD* plength) override
    {
        if (!plength) return E_POINTER;
        *plength = 20000;
        return S_OK;
    }
    HRESULT TxGetScrollBars(DWORD* pBars) override
    {
        if (!pBars) return E_POINTER;
        *pBars = 0;
        return S_OK;
    }
    HRESULT TxGetPasswordChar(TCHAR* pch) override
    {
        if (pch) *pch = 0;
        return S_OK;
    }
    HRESULT TxGetAcceleratorPos(LONG* pcp) override
    {
        if (pcp) *pcp = -1;
        return S_OK;
    }
    HRESULT TxGetExtent(LPSIZEL pExtent) override
    {
        if (!pExtent) return E_POINTER;
        RECT rc; GetClientRect(hwnd, &rc);
        pExtent->cx = MulDiv(rc.right - rc.left, 2540, 96);
        pExtent->cy = MulDiv(rc.bottom - rc.top, 2540, 96);
        return S_OK;
    }
    HRESULT OnTxCharFormatChange(const CHARFORMATW* p) override
    {
        if (p) cf = *p;
        return S_OK;
    }
    HRESULT OnTxParaFormatChange(const PARAFORMAT* p) override
    {
        if (p) pf = *p;
        return S_OK;
    }
    HRESULT TxGetPropertyBits(DWORD dwMask, DWORD* pBits) override
    {
        if (!pBits) return E_POINTER;
        *pBits = TXTBIT_MULTILINE | TXTBIT_RICHTEXT | TXTBIT_WORDWRAP;
        return S_OK;
    }
    HRESULT TxNotify(DWORD, void*) override
    {
        // RichEdit already calls TxInvalidateRect/TxViewChange for changes.
        // Invalidating again here can create a repaint loop that starves timers.
        return S_OK;
    }
    HIMC TxImmGetContext() override { return ImmGetContext(hwnd); }
    void TxImmReleaseContext(HIMC himc) override { if (himc) ImmReleaseContext(hwnd, himc); }
    HRESULT TxGetSelectionBarWidth(LONG* p) override
    {
        if (p) *p = 0;
        return S_OK;
    }
};

static void DrawBackground(HDC hdc, int w, int h)
{
    // Deliberately busy background to prove transparency (not a white box).
    RECT r{ 0, 0, w, h };
    HBRUSH b1 = CreateSolidBrush(RGB(245, 230, 200));
    FillRect(hdc, &r, b1);
    DeleteObject(b1);

    for (int y = 0; y < h; y += 64)
    {
        for (int x = 0; x < w; x += 64)
        {
            if (((x / 64) + (y / 64)) % 2 == 0) continue;
            RECT q{ x, y, x + 64, y + 64 };
            HBRUSH b = CreateSolidBrush(RGB(205, 225, 250));
            FillRect(hdc, &q, b);
            DeleteObject(b);
        }
    }

    RECT band{ 0, h - 90, w, h };
    HBRUSH b2 = CreateSolidBrush(RGB(190, 225, 195));
    FillRect(hdc, &band, b2);
    DeleteObject(b2);

    // A bright red vertical marker just behind the text.
    RECT red{ 40, 40, 56, h - 120 };
    HBRUSH b3 = CreateSolidBrush(RGB(230, 80, 70));
    FillRect(hdc, &red, b3);
    DeleteObject(b3);
}

static void DrawScene(HDC hdc, int w, int h)
{
    DrawBackground(hdc, w, h);
    if (g_svc)
    {
        RECTL rcl{ 0, 0, w, h };
        RECT upd{ 0, 0, w, h };
        g_svc->TxDraw(DVASPECT_CONTENT, -1, nullptr, nullptr, hdc, nullptr,
                      &rcl, nullptr, &upd, nullptr, 0, 0);
    }
}

static void LogLine(const char* fmt, ...)
{
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsprintf_s(buf, fmt, ap);
    va_end(ap);
    HANDLE log = CreateFileW(L"b0-selftest.log", FILE_APPEND_DATA, FILE_SHARE_READ,
                             nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (log != INVALID_HANDLE_VALUE)
    {
        DWORD wrote = 0;
        WriteFile(log, buf, (DWORD)strlen(buf), &wrote, nullptr);
        CloseHandle(log);
    }
}

static void RunBench(HWND hwnd)
{
    const int w = 1200, h = 800;
    HDC screen = GetDC(hwnd);
    HDC mem = CreateCompatibleDC(screen);
    HBITMAP bmp = CreateCompatibleBitmap(screen, w, h);
    HGDIOBJ old = SelectObject(mem, bmp);

    // ~16k chars: 200 lines of 80 ASCII chars.
    std::wstring big;
    big.reserve(200 * 82);
    for (int i = 0; i < 200; ++i)
    {
        for (int j = 0; j < 80; ++j) big.push_back((wchar_t)(L'a' + ((i + j) % 26)));
        big.append(L"\r\n");
    }
    g_svc->TxSetText(big.c_str());

    RECTL rcl{ 0, 0, w, h };
    RECT upd{ 0, 0, w, h };
    for (int i = 0; i < 5; ++i)
        g_svc->TxDraw(DVASPECT_CONTENT, -1, nullptr, nullptr, mem, nullptr,
                      &rcl, nullptr, &upd, nullptr, 0, 0);

    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);

    QueryPerformanceCounter(&t0);
    for (int i = 0; i < 60; ++i)
        g_svc->TxDraw(DVASPECT_CONTENT, -1, nullptr, nullptr, mem, nullptr,
                      &rcl, nullptr, &upd, nullptr, 0, 0);
    QueryPerformanceCounter(&t1);
    double drawMs = (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart / 60.0;

    g_svc->TxSetText(L"");
    SetFocus(hwnd);
    g_svc->OnTxUIActivate();
    LRESULT lr = 0;
    g_svc->TxSendMessage(WM_SETFOCUS, 0, 0, &lr);

    QueryPerformanceCounter(&t0);
    for (int i = 0; i < 500; ++i)
    {
        LRESULT r = 0;
        g_svc->TxSendMessage(WM_CHAR, (WPARAM)L'a', 1, &r);
    }
    QueryPerformanceCounter(&t1);
    double typeMs = (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart;

    g_svc->TxSendMessage(WM_GETTEXTLENGTH, 0, 0, &lr);
    LogLine("bench: 16k chars draw=%.2f ms/frame; 500 WM_CHAR=%.2f ms total; len after=%ld\r\n",
            drawMs, typeMs, (long)lr);

    SelectObject(mem, old);
    DeleteObject(bmp);
    DeleteDC(mem);
    ReleaseDC(hwnd, screen);
}

static HRESULT SaveWicPng(IWICImagingFactory* wic, IWICBitmap* bmp, const wchar_t* path)
{
    IWICStream* stream = nullptr;
    IWICBitmapEncoder* enc = nullptr;
    IWICBitmapFrameEncode* frame = nullptr;
    IPropertyBag2* props = nullptr;
    HRESULT hr = wic->CreateStream(&stream);
    if (SUCCEEDED(hr)) hr = stream->InitializeFromFilename(path, GENERIC_WRITE);
    if (SUCCEEDED(hr)) hr = wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &enc);
    if (SUCCEEDED(hr)) hr = enc->Initialize(stream, WICBitmapEncoderNoCache);
    if (SUCCEEDED(hr)) hr = enc->CreateNewFrame(&frame, &props);
    if (SUCCEEDED(hr)) hr = frame->Initialize(props);
    UINT w = 0, h = 0;
    if (SUCCEEDED(hr)) hr = bmp->GetSize(&w, &h);
    if (SUCCEEDED(hr)) hr = frame->SetSize(w, h);
    WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppPBGRA;
    if (SUCCEEDED(hr)) hr = frame->SetPixelFormat(&fmt);
    if (SUCCEEDED(hr)) hr = frame->WriteSource(bmp, nullptr);
    if (SUCCEEDED(hr)) hr = frame->Commit();
    if (SUCCEEDED(hr)) hr = enc->Commit();
    SafeRelease(frame);
    SafeRelease(props);
    SafeRelease(enc);
    SafeRelease(stream);
    return hr;
}

static void RunD2D(HWND hwnd)
{
    const UINT w = 900, h = 560;
    HRESULT hr;
    IWICImagingFactory* wic = nullptr;
    hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                          IID_PPV_ARGS(&wic));
    IWICBitmap* bmp = nullptr;
    if (SUCCEEDED(hr)) hr = wic->CreateBitmap(w, h, GUID_WICPixelFormat32bppPBGRA,
                                              WICBitmapCacheOnLoad, &bmp);
    ID2D1Factory* fac = nullptr;
    if (SUCCEEDED(hr)) hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &fac);
    ID2D1RenderTarget* rt = nullptr;
    if (SUCCEEDED(hr))
        hr = fac->CreateWicBitmapRenderTarget(bmp, D2D1::RenderTargetProperties(), &rt);

    HRESULT hrDraw = E_FAIL, hrEnd = E_FAIL;
    if (SUCCEEDED(hr))
    {
        rt->BeginDraw();
        rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        rt->Clear(D2D1::ColorF(0.96f, 0.92f, 0.82f, 1.0f));
        ID2D1SolidColorBrush* blue = nullptr;
        ID2D1SolidColorBrush* red = nullptr;
        rt->CreateSolidColorBrush(D2D1::ColorF(0.80f, 0.88f, 0.98f, 1.0f), &blue);
        rt->CreateSolidColorBrush(D2D1::ColorF(0.90f, 0.31f, 0.27f, 1.0f), &red);
        if (blue)
        {
            for (UINT y = 0; y < h; y += 64)
                for (UINT x = 0; x < w; x += 64)
                    if (((x / 64) + (y / 64)) % 2)
                        rt->FillRectangle(D2D1::RectF((float)x, (float)y, (float)(x + 64), (float)(y + 64)), blue);
        }
        if (red) rt->FillRectangle(D2D1::RectF(40, 40, 56, (float)h - 60), red);

        g_svc->TxSetText(L"D2D 透明背景测试\r\n中文 / English / 12345\r\n第三行：选区、光标、IME 由 RichEdit 负责。");

        auto iid2 = g_msft ? (const IID*)GetProcAddress(g_msft, "IID_ITextServices2") : nullptr;
        ITextServices2* svc2 = nullptr;
        if (iid2) g_svc->QueryInterface(*iid2, (void**)&svc2);
        if (svc2)
        {
            RECTL rcl{ 0, 0, (LONG)w, (LONG)h };
            RECT upd{ 0, 0, (LONG)w, (LONG)h };
            hrDraw = svc2->TxDrawD2D(rt, &rcl, &upd, 0);
            svc2->Release();
        }
        hrEnd = rt->EndDraw();
        SafeRelease(blue);
        SafeRelease(red);
    }

    HRESULT hrSave = E_FAIL;
    if (SUCCEEDED(hr) && bmp)
        hrSave = SaveWicPng(wic, bmp, L"b0-d2d.png");

    LogLine("d2d: setup=0x%08lX draw=0x%08lX end=0x%08lX save=0x%08lX\r\n",
            (unsigned long)hr, (unsigned long)hrDraw, (unsigned long)hrEnd, (unsigned long)hrSave);

    SafeRelease(rt);
    SafeRelease(fac);
    SafeRelease(bmp);
    SafeRelease(wic);
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_CREATE:
    {
        HMODULE msft = LoadLibraryW(L"Msftedit.dll");
        if (!msft) { MessageBoxW(hwnd, L"Msftedit.dll load failed", L"B0", MB_ICONERROR); return -1; }
        g_msft = msft;
        auto create = (HRESULT(WINAPI*)(IUnknown*, ITextHost*, IUnknown**))
            GetProcAddress(msft, "CreateTextServices");
        auto iidSvc = (IID*)GetProcAddress(msft, "IID_ITextServices");
        if (!create || !iidSvc) { MessageBoxW(hwnd, L"CreateTextServices/IID missing", L"B0", MB_ICONERROR); return -1; }

        g_host = new Host(hwnd);
        IUnknown* unk = nullptr;
        HRESULT hr = create(nullptr, g_host, &unk);
        if (FAILED(hr) || !unk) { MessageBoxW(hwnd, L"CreateTextServices failed", L"B0", MB_ICONERROR); return -1; }
        hr = unk->QueryInterface(*iidSvc, (void**)&g_svc);
        unk->Release();
        if (FAILED(hr) || !g_svc) { MessageBoxW(hwnd, L"ITextServices QI failed", L"B0", MB_ICONERROR); return -1; }

        g_svc->OnTxInPlaceActivate(nullptr);
        g_svc->OnTxUIActivate();
        g_svc->TxSetText(L"透明背景 B0 测试\r\n中文 / English / 12345\r\n第二行：光标、选区、IME 都应该由 RichEdit 负责。");
        LRESULT lr = 0;
        g_svc->TxSendMessage(EM_SETSEL, 0, 0, &lr);
        g_svc->TxSendMessage(WM_SETFOCUS, 0, 0, &lr);
        SetFocus(hwnd);
        if (g_selfTest)
        {
            HIMC himc = ImmGetContext(hwnd);
            HANDLE log = CreateFileW(L"b0-selftest.log", GENERIC_WRITE, FILE_SHARE_READ,
                                     nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (log != INVALID_HANDLE_VALUE)
            {
                char buf[128];
                int n = sprintf_s(buf, "ImmGetContext=%s\r\n",
                                  himc ? "non-null (IME host wired)" : "null");
                DWORD wrote = 0;
                WriteFile(log, buf, (DWORD)n, &wrote, nullptr);
                CloseHandle(log);
            }
            if (himc) ImmReleaseContext(hwnd, himc);
        }
        return 0;
    }
    case WM_SIZE:
        if (g_svc) SendSvc(WM_SIZE, wp, lp);
        InvalidateRect(hwnd, nullptr, FALSE);
        return 0;
    case WM_SETFOCUS:
        if (g_svc)
        {
            g_svc->OnTxUIActivate();
            SendSvc(WM_SETFOCUS, 0, 0);
        }
        return 0;
    case WM_KILLFOCUS:
        if (g_svc)
        {
            SendSvc(WM_KILLFOCUS, 0, 0);
            g_svc->OnTxUIDeactivate();
        }
        return 0;
    case WM_CHAR:
    case WM_KEYDOWN:
    case WM_KEYUP:
    case WM_IME_STARTCOMPOSITION:
    case WM_IME_COMPOSITION:
    case WM_IME_ENDCOMPOSITION:
    case WM_IME_SETCONTEXT:
    case WM_IME_NOTIFY:
    case WM_IME_REQUEST:
        return SendSvc(msg, wp, lp);
    case WM_LBUTTONDOWN:
    case WM_LBUTTONUP:
    case WM_LBUTTONDBLCLK:
    case WM_MOUSEMOVE:
    case WM_MOUSEWHEEL:
        SetFocus(hwnd);
        return SendSvc(msg, wp, lp);
    case WM_TIMER:
        if (wp == 0xB001 && g_selfTest)
        {
            KillTimer(hwnd, 0xB001);
            SetFocus(hwnd);
            if (g_svc) { g_svc->OnTxUIActivate(); SendSvc(WM_SETFOCUS, 0, 0); }
            LRESULT lr = 0;
            g_svc->TxSendMessage(WM_GETTEXTLENGTH, 0, 0, &lr);
            LONG len = (LONG)lr;
            g_svc->TxSendMessage(EM_SETSEL, (WPARAM)len, (LPARAM)len, &lr);
            const wchar_t* add = L" 自测ABC";
            for (const wchar_t* p = add; *p; ++p)
                SendSvc(WM_CHAR, (WPARAM)*p, 1);
            // Select a range so the screenshot shows RichEdit's selection rendering.
            g_svc->TxSendMessage(EM_SETSEL, 0, 6, &lr);
            InvalidateRect(hwnd, nullptr, FALSE);

            g_svc->TxSendMessage(WM_GETTEXTLENGTH, 0, 0, &lr);
            CHARRANGE cr = {};
            g_svc->TxSendMessage(EM_EXGETSEL, 0, (LPARAM)&cr, &lr);
            HANDLE log = CreateFileW(L"b0-selftest.log", FILE_APPEND_DATA, FILE_SHARE_READ,
                                     nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (log != INVALID_HANDLE_VALUE)
            {
                char buf[256];
                int n = sprintf_s(buf, "focus=%s len=%ld sel=(%ld,%ld)\r\n",
                                  GetFocus() == hwnd ? "yes" : "no", (long)len, cr.cpMin, cr.cpMax);
                DWORD wrote = 0;
                WriteFile(log, buf, (DWORD)n, &wrote, nullptr);
                CloseHandle(log);
            }
            return 0;
        }
        return SendSvc(msg, wp, lp);
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hwnd, &ps);
        RECT rc; GetClientRect(hwnd, &rc);
        int w = rc.right - rc.left, h = rc.bottom - rc.top;

        HDC mem = CreateCompatibleDC(hdc);
        HBITMAP bmp = CreateCompatibleBitmap(hdc, w, h);
        HGDIOBJ old = SelectObject(mem, bmp);
        DrawScene(mem, w, h);
        BitBlt(hdc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_SETCURSOR:
        SetCursor(LoadCursor(nullptr, IDC_IBEAM));
        return TRUE;
    case WM_DESTROY:
        if (g_svc) { g_svc->OnTxUIDeactivate(); g_svc->OnTxInPlaceDeactivate(); g_svc->Release(); g_svc = nullptr; }
        if (g_host) { g_host->Release(); g_host = nullptr; }
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR cmdLine, int)
{
    g_selfTest = cmdLine && wcsstr(cmdLine, L"--selftest") != nullptr;
    g_bench = cmdLine && wcsstr(cmdLine, L"--bench") != nullptr;
    g_d2dTest = cmdLine && wcsstr(cmdLine, L"--d2d") != nullptr;
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursor(nullptr, IDC_IBEAM);
    wc.hbrBackground = nullptr;
    wc.lpszClassName = kClass;
    RegisterClassExW(&wc);

    HWND hwnd = CreateWindowExW(0, kClass, L"B0 windowless RichEdit — transparent background spike",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        120, 120, 900, 560, nullptr, nullptr, hInst, nullptr);
    if (!hwnd) return 1;

    if (g_selfTest)
    {
        SetLastError(0);
        UINT_PTR tid = SetTimer(hwnd, 0xB001, 1200, nullptr);
        DWORD err = GetLastError();
        HANDLE log = CreateFileW(L"b0-selftest.log", FILE_APPEND_DATA, FILE_SHARE_READ,
                                 nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (log != INVALID_HANDLE_VALUE)
        {
            char buf[128];
            int n = sprintf_s(buf, "SetTimer returned=%llu err=%lu\r\n",
                              (unsigned long long)tid, (unsigned long)err);
            DWORD wrote = 0;
            WriteFile(log, buf, (DWORD)n, &wrote, nullptr);
            CloseHandle(log);
        }
    }

    if (g_svc && g_msft)
    {
        auto iid2 = (const IID*)GetProcAddress(g_msft, "IID_ITextServices2");
        ITextServices2* svc2 = nullptr;
        HRESULT hr2 = iid2 ? g_svc->QueryInterface(*iid2, (void**)&svc2) : E_NOINTERFACE;
        LogLine("ITextServices2 QI hr=0x%08lX ptr=%p\r\n", (unsigned long)hr2, (void*)svc2);
        if (svc2) svc2->Release();
    }

    if (g_d2dTest && g_svc)
    {
        RunD2D(hwnd);
        return 0;
    }

    if (g_bench && g_svc)
    {
        RunBench(hwnd);
        return 0;
    }

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}
