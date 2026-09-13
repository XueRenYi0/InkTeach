using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 穿透模式下留在屏幕上的"找回界面"小圆钮。
///
/// 为什么必须单独一个窗口：穿透的实现是给覆盖窗口加上
/// <c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>，那个样式会让窗口**完全收不到
/// 鼠标消息**——连引擎自己也收不到。所以"点一下切回来"这件事，只能由一个
/// **不带穿透样式**的独立小窗口来做。
///
/// 它的体积和内存在两种模式下都是零：平时隐藏，穿透时才挪到工具条原来的
/// 位置显示出来。
/// </summary>
internal sealed class RestorePill : IDisposable
{
    public IntPtr Hwnd;
    public int OriginX, OriginY, Width, Height;
    public uint Dpi = 96;

    private IDXGISwapChain1 _swapChain;
    private IDCompositionDevice _dcomp;
    private IDCompositionTarget _target;
    private IDCompositionVisual _visual;
    private ID2D1DeviceContext _ctx;
    private ID2D1Bitmap1 _backBuffer;
    private ID2D1SolidColorBrush _bg;
    private ID2D1SolidColorBrush _fg;
    private bool _hovered;
    private bool _visible;
    private int _swapChainWidth;
    private int _swapChainHeight;

    private static readonly Color4 Transparent = new(0f, 0f, 0f, 0f);

    public bool Visible => _visible;

    public string Create(IntPtr hInstance, string className, uint dpi)
    {
        Dpi = dpi == 0 ? 96 : dpi;
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOREDIRECTIONBITMAP;

        // 尺寸先按 1x1 建，真正显示时再挪位置、改大小。
        Hwnd = Native.CreateWindowEx(exStyle, className, "InkProbeRestore",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            return "RestorePill CreateWindowEx failed: "
                 + System.Runtime.InteropServices.Marshal.GetLastWin32Error();

        try
        {
            using var dxgiDevice = Gfx.Device.QueryInterface<IDXGIDevice>();
            _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _dcomp.CreateTargetForHwnd(Hwnd, true, out _target).CheckError();
            _visual = _dcomp.CreateVisual();
            _target.SetRoot(_visual).CheckError();

            _ctx = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
            _ctx.SetDpi(96f, 96f);
            _ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            _bg = _ctx.CreateSolidColorBrush(new Color4(0.10f, 0.11f, 0.14f, 0.34f), null);
            _fg = _ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.42f), null);
        }
        catch (Exception ex)
        {
            return "RestorePill 初始化失败: " + ex.Message;
        }
        return null;
    }

    /// <summary>挪到指定位置并显示。位置和尺寸都是**逻辑像素**，这里按 DPI 放大。</summary>
    public void Show(RectF logicalBounds)
    {
        float s = Dpi / 96f;
        int x = (int)MathF.Round(logicalBounds.MinX * s);
        int y = (int)MathF.Round(logicalBounds.MinY * s);
        int w = Math.Max(1, (int)MathF.Round((logicalBounds.MaxX - logicalBounds.MinX) * s));
        int h = Math.Max(1, (int)MathF.Round((logicalBounds.MaxY - logicalBounds.MinY) * s));

        OriginX = x; OriginY = y; Width = w; Height = h;

        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        // 交换链的尺寸必须和窗口一致。尺寸变了就重建，否则只建一次。
        if (_swapChain == null || _swapChainWidth != w || _swapChainHeight != h)
        {
            RecreateSwapChain();
            if (_swapChain == null) return;
        }

        _visible = true;
        Render();
    }

    public void Hide()
    {
        if (!_visible) return;
        _visible = false;
        Native.ShowWindow(Hwnd, 0 /*SW_HIDE*/);
    }

    private void CreateSwapChain()
    {
        _swapChainWidth = Width;
        _swapChainHeight = Height;
        var desc = new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, Width),
            Height = (uint)Math.Max(1, Height),
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            // FlipSequential 必须给采样描述，否则 CreateSwapChainForComposition
            // 直接返回 DXGI_ERROR_INVALID_CALL（实测踩过）。
            SampleDescription = new SampleDescription(1, 0),
        };
        _swapChain = Gfx.Factory.CreateSwapChainForComposition(Gfx.Device, desc, null);
        _visual.SetContent(_swapChain).CheckError();
        _dcomp.Commit().CheckError();

        var pf = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
            Vortice.DCommon.AlphaMode.Premultiplied);
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var surface = backBuffer.QueryInterface<IDXGISurface>();
        _backBuffer = _ctx.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    private void RecreateSwapChain()
    {
        _backBuffer?.Dispose(); _backBuffer = null;
        _swapChain?.Dispose(); _swapChain = null;
        try
        {
            CreateSwapChain();
        }
        catch (Exception ex)
        {
            // 交换链建不起来（例如驱动限制）不该让整个程序崩掉，
            // 大不了这个恢复入口不显示。
            Console.WriteLine("恢复小圆钮交换链创建失败: " + ex.Message);
            _swapChain = null;
            _backBuffer = null;
        }
    }

    /// <summary>鼠标进来就提亮，离开就变回半透明——不抢眼但也找得到。</summary>
    public void SetHovered(bool on)
    {
        if (_hovered == on) return;
        _hovered = on;
        Render();
    }

    private void Render()
    {
        if (_ctx == null || _backBuffer == null) return;

        var bg = new Color4(0.10f, 0.11f, 0.14f, _hovered ? 0.92f : 0.34f);
        var fg = new Color4(1f, 1f, 1f, _hovered ? 0.96f : 0.42f);
        _bg.Color = bg;
        _fg.Color = fg;

        _ctx.Target = _backBuffer;
        _ctx.BeginDraw();
        _ctx.Clear(Transparent);

        float s = Dpi / 96f;
        float w = Width / s, h = Height / s;
        var box = new Vortice.RawRectF(0, 0, w, h);
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, w * 0.46f, h * 0.46f), _bg);

        // 一支斜放的笔：画一条线加一个笔尖，够表达"批注"了。
        float cx = w * 0.5f, cy = h * 0.5f;
        float r = MathF.Min(w, h) * 0.22f;
        _ctx.DrawLine(new System.Numerics.Vector2(cx - r * 1.15f, cy + r * 1.15f),
                      new System.Numerics.Vector2(cx + r * 0.85f, cy - r * 1.55f), _fg, MathF.Max(1.6f, r * 0.28f));
        _ctx.DrawLine(new System.Numerics.Vector2(cx - r * 1.15f, cy + r * 1.15f),
                      new System.Numerics.Vector2(cx - r * 0.05f, cy + r * 0.72f), _fg, MathF.Max(1.6f, r * 0.28f));

        var hr = _ctx.EndDraw();
        _ctx.Target = null;
        if (!hr.Failure) _swapChain.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        _backBuffer?.Dispose();
        _bg?.Dispose();
        _fg?.Dispose();
        _ctx?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _swapChain?.Dispose();
        if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }
}
