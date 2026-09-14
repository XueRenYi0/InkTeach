// Toolchain smoke test: can we build and link against Direct3D / Direct2D /
// DirectComposition with the bundled toolchain?
#include <windows.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <dcomp.h>
#include <dxgi1_2.h>
#include <psapi.h>
#include <cstdio>

int main()
{
    ID3D11Device* dev = nullptr;
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2,
                                   D3D11_SDK_VERSION, &dev, nullptr, nullptr);
    printf("D3D11CreateDevice hr=0x%08lX dev=%p\n", (unsigned long)hr, (void*)dev);

    ID2D1Factory1* f = nullptr;
    hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1),
                           nullptr, reinterpret_cast<void**>(&f));
    printf("D2D1CreateFactory hr=0x%08lX f=%p\n", (unsigned long)hr, (void*)f);

    PROCESS_MEMORY_COUNTERS pmc{};
    GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc));
    printf("WorkingSetSize=%.1f MB  PagefileUsage=%.1f MB\n",
           pmc.WorkingSetSize / 1048576.0, pmc.PagefileUsage / 1048576.0);

    if (f) f->Release();
    if (dev) dev->Release();
    return 0;
}
