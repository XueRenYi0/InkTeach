// ---------------------------------------------------------------------------
//  InkTeach (native) - a deliberately minimal twin of the C# prototype.
//
//  It exists to answer one question with a real number instead of a guess:
//  how much memory does the same transparent, GPU-composited annotation
//  overlay cost when there is no .NET runtime underneath it?
//
//  Everything below mirrors src/InkTeach so the two are directly comparable:
//  same window styles, same D3D11 + Direct2D + DirectComposition pipeline,
//  same full-screen offscreen content layer, same synthetic stroke workload.
// ---------------------------------------------------------------------------

#include <initguid.h>
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include <d2d1_1.h>
#include <d2d1helper.h>
#include <dcomp.h>
#include <dwrite.h>
#include <psapi.h>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <vector>

#pragma comment(lib, "d3d11")
#pragma comment(lib, "d2d1")
#pragma comment(lib, "dcomp")
#pragma comment(lib, "dxgi")
#pragma comment(lib, "dwrite")
#pragma comment(lib, "psapi")

// MinGW-w64's dcomp.h declares the interfaces but not their IIDs, so supply them.
DEFINE_GUID(IID_IDCompositionDevice,
            0xC37EA93A, 0xE7AA, 0x450D, 0xB1, 0x6F, 0x97, 0x46, 0xCB, 0x04, 0x07, 0xF3);
DEFINE_GUID(IID_IDCompositionDesktopDevice,
            0x5F4633FE, 0x1E08, 0x4CB8, 0x8C, 0x75, 0xCE, 0x24, 0x33, 0x3F, 0x56, 0x02);

namespace {

struct Stage { const char* label; };

double CommitMb()
{
    PROCESS_MEMORY_COUNTERS pmc{};
    GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc));
    return pmc.PagefileUsage / 1048576.0;
}

double WorkingSetMb()
{
    PROCESS_MEMORY_COUNTERS pmc{};
    GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc));
    return pmc.WorkingSetSize / 1048576.0;
}

// Dedicated GPU memory held by our device, in MB (shared on an iGPU).
double GpuUsedMb(IDXGIAdapter3* adapter, double* budgetMb)
{
    if (!adapter) return -1;
    DXGI_QUERY_VIDEO_MEMORY_INFO info{};
    if (FAILED(adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
        return -1;
    *budgetMb = info.Budget / 1048576.0;
    return info.CurrentUsage / 1048576.0;
}

void StageReport(const char* label)
{
    printf("  %-34s 提交 %7.1f MB   工作集 %7.1f MB\n", label, CommitMb(), WorkingSetMb());
}

void Trim()
{
    EmptyWorkingSet(GetCurrentProcess());
}

// ---------------------------------------------------------------------------

ID3D11Device*             g_d3d        = nullptr;
IDXGIFactory2*            g_dxgi       = nullptr;
IDXGIAdapter3*            g_adapter3   = nullptr;
ID2D1Factory1*            g_d2dFactory = nullptr;
ID2D1Device*              g_d2dDevice  = nullptr;
ID2D1DeviceContext*       g_ctx        = nullptr;
IDWriteFactory*           g_write      = nullptr;
IDWriteTextFormat*        g_hudFormat  = nullptr;

IDXGISwapChain1*          g_swapChain  = nullptr;
IDCompositionDevice*      g_dcomp      = nullptr;
IDCompositionTarget*      g_target     = nullptr;
IDCompositionVisual*      g_visual     = nullptr;
ID2D1Bitmap1*             g_backBuffer = nullptr;

ID3D11Texture2D*          g_contentTex = nullptr;
ID2D1Bitmap1*             g_contentTarget = nullptr;
ID2D1Bitmap1*             g_contentSource = nullptr;
ID2D1SolidColorBrush*     g_brush      = nullptr;
ID2D1StrokeStyle1*        g_strokeStyle = nullptr;

HWND  g_hwnd = nullptr;
int   g_width = 0, g_height = 0, g_originX = 0, g_originY = 0;
bool  g_quit = false;

struct Pt { float x, y, p; };
struct Stroke {
    std::vector<Pt> pts;
    float width;
    ID2D1PathGeometry* geo = nullptr;
    float minX = 1e30f, minY = 1e30f, maxX = -1e30f, maxY = -1e30f;
};

std::vector<Stroke*> g_strokes;

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_NCHITTEST:      return HTCLIENT;
    case WM_MOUSEACTIVATE:  return MA_NOACTIVATE;
    case WM_ERASEBKGND:     return 1;
    case WM_CLOSE:          g_quit = true; return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

bool CreateGraphics()
{
    StageReport("0. 进程启动");

    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2,
                                   D3D11_SDK_VERSION, &g_d3d, nullptr, nullptr);
    if (FAILED(hr)) { printf("D3D11CreateDevice failed 0x%08lX\n", (unsigned long)hr); return false; }
    StageReport("1. 创建 Direct3D 11 设备之后");

    IDXGIDevice* dxgiDevice = nullptr;
    g_d3d->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    IDXGIAdapter* adapter = nullptr;
    dxgiDevice->GetAdapter(&adapter);
    adapter->GetParent(IID_PPV_ARGS(&g_dxgi));
    adapter->QueryInterface(IID_PPV_ARGS(&g_adapter3));

    DXGI_ADAPTER_DESC ad{};
    adapter->GetDesc(&ad);
    wprintf(L"  显卡: %s  专用显存 %.0f MB\n", ad.Description, ad.DedicatedVideoMemory / 1048576.0);
    adapter->Release();

    hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1),
                           nullptr, reinterpret_cast<void**>(&g_d2dFactory));
    if (FAILED(hr)) { printf("D2D1CreateFactory failed 0x%08lX\n", (unsigned long)hr); return false; }
    g_d2dFactory->CreateDevice(dxgiDevice, &g_d2dDevice);
    g_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &g_ctx);
    g_ctx->SetDpi(96.f, 96.f);
    g_ctx->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    g_ctx->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    StageReport("2. 创建 Direct2D 设备之后");

    hr = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                             reinterpret_cast<IUnknown**>(&g_write));
    if (SUCCEEDED(hr))
        g_write->CreateTextFormat(L"Microsoft YaHei UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                  DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 13.f,
                                  L"zh-CN", &g_hudFormat);
    StageReport("3. 创建 DirectWrite（文字）之后");

    dxgiDevice->Release();
    return true;
}

bool CreateOverlay(HINSTANCE inst)
{
    g_originX = 0; g_originY = 0;
    g_width  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    g_height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

    WNDCLASSEXW wc{};
    wc.cbSize        = sizeof(wc);
    wc.lpfnWndProc   = WndProc;
    wc.hInstance     = inst;
    wc.lpszClassName = L"InkTeachNativeOverlay";
    RegisterClassExW(&wc);

    g_hwnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
        wc.lpszClassName, L"InkTeachNative", WS_POPUP,
        g_originX, g_originY, g_width, g_height,
        nullptr, nullptr, inst, nullptr);
    if (!g_hwnd) { printf("CreateWindowEx failed %lu\n", GetLastError()); return false; }

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width       = (UINT)g_width;
    desc.Height      = (UINT)g_height;
    desc.Format      = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc  = { 1, 0 };
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.Scaling     = DXGI_SCALING_STRETCH;
    desc.SwapEffect  = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.AlphaMode   = DXGI_ALPHA_MODE_PREMULTIPLIED;

    HRESULT hr = g_dxgi->CreateSwapChainForComposition(g_d3d, &desc, nullptr, &g_swapChain);
    if (FAILED(hr)) { printf("CreateSwapChainForComposition failed 0x%08lX\n", (unsigned long)hr); return false; }
    StageReport("4. 创建交换链之后");

    IDXGIDevice* dxgiDevice = nullptr;
    g_d3d->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    hr = DCompositionCreateDevice(dxgiDevice, IID_IDCompositionDevice,
                                  reinterpret_cast<void**>(&g_dcomp));
    dxgiDevice->Release();
    if (FAILED(hr)) { printf("DCompositionCreateDevice failed 0x%08lX\n", (unsigned long)hr); return false; }

    g_dcomp->CreateTargetForHwnd(g_hwnd, TRUE, &g_target);
    g_dcomp->CreateVisual(&g_visual);
    g_visual->SetContent(g_swapChain);
    g_target->SetRoot(g_visual);
    g_dcomp->Commit();
    StageReport("5. 接入 DirectComposition 之后");

    ID3D11Texture2D* backBuffer = nullptr;
    g_swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
    IDXGISurface* surface = nullptr;
    backBuffer->QueryInterface(IID_PPV_ARGS(&surface));

    D2D1_BITMAP_PROPERTIES1 props{};
    props.pixelFormat.format    = DXGI_FORMAT_B8G8R8A8_UNORM;
    props.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
    props.dpiX = props.dpiY = 96.f;
    props.bitmapOptions = D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
    g_ctx->CreateBitmapFromDxgiSurface(surface, &props, &g_backBuffer);
    surface->Release();
    backBuffer->Release();

    // Offscreen content layer - two Direct2D views over one Direct3D texture,
    // exactly as in the managed version.
    D3D11_TEXTURE2D_DESC td{};
    td.Width = (UINT)g_width; td.Height = (UINT)g_height;
    td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc = { 1, 0 };
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    hr = g_d3d->CreateTexture2D(&td, nullptr, &g_contentTex);
    if (FAILED(hr)) { printf("CreateTexture2D failed 0x%08lX\n", (unsigned long)hr); return false; }

    IDXGISurface* contentSurface = nullptr;
    g_contentTex->QueryInterface(IID_PPV_ARGS(&contentSurface));
    g_ctx->CreateBitmapFromDxgiSurface(contentSurface, &props, &g_contentTarget);
    props.bitmapOptions = D2D1_BITMAP_OPTIONS_NONE;
    g_ctx->CreateBitmapFromDxgiSurface(contentSurface, &props, &g_contentSource);
    contentSurface->Release();

    const D2D1_COLOR_F strokeColor = D2D1::ColorF(0.95f, 0.18f, 0.18f, 1.f);
    g_ctx->CreateSolidColorBrush(strokeColor, &g_brush);

    D2D1_STROKE_STYLE_PROPERTIES1 sp{};
    sp.startCap = sp.endCap = sp.dashCap = D2D1_CAP_STYLE_ROUND;
    sp.lineJoin = D2D1_LINE_JOIN_ROUND;
    sp.miterLimit = 10.f;
    g_d2dFactory->CreateStrokeStyle(sp, nullptr, 0, &g_strokeStyle);

    ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
    SetWindowPos(g_hwnd, HWND_TOPMOST, g_originX, g_originY, g_width, g_height,
                 SWP_NOACTIVATE | SWP_SHOWWINDOW);
    StageReport("6. 创建离屏内容层之后");
    return true;
}

// Builds the same "ribbon" outline the managed version uses: two offset
// polylines joined into one closed figure.
ID2D1PathGeometry* BuildGeometry(Stroke* s)
{
    if (s->geo) return s->geo;
    const int n = (int)s->pts.size();
    if (n == 0) return nullptr;

    ID2D1PathGeometry* geo = nullptr;
    g_d2dFactory->CreatePathGeometry(&geo);
    ID2D1GeometrySink* sink = nullptr;
    geo->Open(&sink);
    sink->SetFillMode(D2D1_FILL_MODE_WINDING);

    if (n == 1)
    {
        sink->BeginFigure(D2D1::Point2F(s->pts[0].x, s->pts[0].y), D2D1_FIGURE_BEGIN_FILLED);
        sink->AddLine(D2D1::Point2F(s->pts[0].x + 0.01f, s->pts[0].y));
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
        sink->Close();
        sink->Release();
        s->geo = geo;
        return geo;
    }

    std::vector<D2D1_POINT_2F> left(n), right(n);
    for (int i = 0; i < n; i++)
    {
        const int a = i > 0 ? i - 1 : i;
        const int b = i < n - 1 ? i + 1 : i;
        float dx = s->pts[b].x - s->pts[a].x;
        float dy = s->pts[b].y - s->pts[a].y;
        float len = std::sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        const float nx = -dy, ny = dx;
        const float hw = s->width * (0.40f + 0.60f * s->pts[i].p) * 0.5f;
        left[i]  = D2D1::Point2F(s->pts[i].x + nx * hw, s->pts[i].y + ny * hw);
        right[i] = D2D1::Point2F(s->pts[i].x - nx * hw, s->pts[i].y - ny * hw);
    }

    sink->BeginFigure(left[0], D2D1_FIGURE_BEGIN_FILLED);
    for (int i = 1; i < n; i++) sink->AddLine(left[i]);
    for (int i = n - 1; i >= 0; i--) sink->AddLine(right[i]);
    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    sink->Close();
    sink->Release();

    s->geo = geo;
    return geo;
}

void DrawStroke(Stroke* s)
{
    ID2D1PathGeometry* geo = BuildGeometry(s);
    if (geo) g_ctx->FillGeometry(geo, g_brush);
}

void RebuildContent()
{
    LARGE_INTEGER freq{}, t0{}, t1{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);
    g_ctx->SetTarget(g_contentTarget);
    g_ctx->BeginDraw();
    g_ctx->Clear(D2D1::ColorF(0, 0.f));
    g_ctx->SetTransform(D2D1::Matrix3x2F::Translation((float)-g_originX, (float)-g_originY));
    for (Stroke* s : g_strokes) DrawStroke(s);
    g_ctx->SetTransform(D2D1::Matrix3x2F::Identity());
    g_ctx->EndDraw();
    g_ctx->Flush(nullptr, nullptr);
    g_ctx->SetTarget(nullptr);
    QueryPerformanceCounter(&t1);
    if (!g_strokes.empty() && g_strokes.size() >= 1000)
        printf("     [重建 %zu 笔耗时 %.1f ms]\n", g_strokes.size(),
               (t1.QuadPart - t0.QuadPart) * 1000.0 / freq.QuadPart);
}

void RenderFrame()
{
    g_ctx->SetTarget(g_backBuffer);
    g_ctx->BeginDraw();
    g_ctx->Clear(D2D1::ColorF(0, 0.f));
    g_ctx->DrawBitmap(g_contentSource, nullptr, 1.f, D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR);

    if (g_hudFormat)
    {
        double budget = 0;
        const double used = GpuUsedMb(g_adapter3, &budget);
        wchar_t text[160];
        swprintf(text, 160, L"C++ 原生原语   笔画 %zu   提交 %.1f MB   显存 %.0f MB",
                 g_strokes.size(), CommitMb(), used);
        g_brush->SetColor(D2D1::ColorF(1, 1, 1, 1));
        g_ctx->DrawText(text, (UINT32)wcslen(text), g_hudFormat,
                        D2D1::RectF(24, 20, 700, 140), g_brush);
    }

    g_ctx->EndDraw();
    g_ctx->SetTarget(nullptr);
    g_swapChain->Present(1, 0);
}

// Same synthetic workload as the managed prototype.
void GenerateStrokes(int count)
{
    for (Stroke* s : g_strokes) { if (s->geo) s->geo->Release(); delete s; }
    g_strokes.clear();

    unsigned seed = 20260913u;
    // NOTE: (seed >> 8) only spans 24 bits, so dividing by 2^31 collapsed every
    // value into [0, 0.008) and piled all strokes into one corner. Mask off the
    // low bits and normalise properly instead.
    auto rnd = [&seed]() {
        seed = seed * 1103515245u + 12345u;
        return ((seed >> 16) & 0x7FFFu) / 32768.0f;
    };

    for (int i = 0; i < count; i++)
    {
        Stroke* s = new Stroke();
        s->width = 2.5f + rnd() * 4.f;
        float x = g_originX + rnd() * g_width;
        float y = g_originY + rnd() * g_height;
        int   pts = 8 + (int)(rnd() * 24);
        float ang = rnd() * 6.2831853f;
        for (int j = 0; j < pts; j++)
        {
            ang += (rnd() - 0.5f) * 0.7f;
            float d = 5.f + rnd() * 9.f;
            x += std::cos(ang) * d;
            y += std::sin(ang) * d;
            if (x < g_originX + 1) x = g_originX + 1;
            if (y < g_originY + 1) y = g_originY + 1;
            if (x > g_originX + g_width - 1)  x = g_originX + g_width - 1;
            if (y > g_originY + g_height - 1) y = g_originY + g_height - 1;
            s->pts.push_back({ x, y, rnd() });
            if (x < s->minX) s->minX = x;
            if (y < s->minY) s->minY = y;
            if (x > s->maxX) s->maxX = x;
            if (y > s->maxY) s->maxY = y;
        }
        g_strokes.push_back(s);
    }
}

void PumpMessages()
{
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
    {
        if (msg.message == WM_QUIT) { g_quit = true; break; }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

} // namespace

int main()
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    printf("===== InkTeach 原生 C++ 内存实测 =====\n");
    printf("屏幕 %dx%d\n\n", GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    if (!CreateGraphics()) return 1;
    if (!CreateOverlay(GetModuleHandleW(nullptr))) return 1;

    for (int i = 0; i < 40; i++) { PumpMessages(); RenderFrame(); }
    Trim();
    printf("\n【空闲稳态】提交 %.1f MB   工作集 %.1f MB\n\n", CommitMb(), WorkingSetMb());

    printf("【按笔画数】\n");
    printf("    笔画数 | 提交大小 | 增量    | 显存 (已用/预算)\n");
    printf("  ---------|----------|---------|----------------\n");
    const double basePriv = CommitMb();
    const int counts[] = { 0, 20, 100, 500, 2000, 10000 };
    for (int count : counts)
    {
        GenerateStrokes(count);
        RebuildContent();
        RenderFrame();
        for (int i = 0; i < 3; i++) { PumpMessages(); RenderFrame(); }
        Trim();
        double budget = 0, used = GpuUsedMb(g_adapter3, &budget);
        double priv = CommitMb();
        char delta[32];
        if (count == 0) snprintf(delta, sizeof(delta), "    —   ");
        else            snprintf(delta, sizeof(delta), "+%5.1f MB", priv - basePriv);
        printf("  %8d | %6.1f MB | %s | %6.0f/%.0f MB\n", count, priv, delta, used, budget);
    }

    printf("\n【一万笔的内存去向】\n");
    GenerateStrokes(10000);
    RebuildContent();
    RenderFrame();
    Trim();
    printf("  一万笔绘制完成、回收后 : 提交 %.1f MB\n", CommitMb());
    GenerateStrokes(0);
    Trim();
    printf("  再把笔画全部清空后     : 提交 %.1f MB\n", CommitMb());

    g_swapChain->Present(0, 0);

    if (g_brush) g_brush->Release();
    if (g_hudFormat) g_hudFormat->Release();
    if (g_write) g_write->Release();
    if (g_contentSource) g_contentSource->Release();
    if (g_contentTarget) g_contentTarget->Release();
    if (g_contentTex) g_contentTex->Release();
    if (g_backBuffer) g_backBuffer->Release();
    if (g_visual) g_visual->Release();
    if (g_target) g_target->Release();
    if (g_dcomp) g_dcomp->Release();
    if (g_swapChain) g_swapChain->Release();
    if (g_ctx) g_ctx->Release();
    if (g_d2dDevice) g_d2dDevice->Release();
    if (g_d2dFactory) g_d2dFactory->Release();
    if (g_adapter3) g_adapter3->Release();
    if (g_dxgi) g_dxgi->Release();
    if (g_d3d) g_d3d->Release();
    return 0;
}
