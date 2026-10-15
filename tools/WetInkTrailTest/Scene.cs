using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WetInkTrailTest;

/// <summary>一笔（自绘用）：点列 + 逐点半径。</summary>
internal sealed class Stroke
{
    public readonly List<Vector2> Points = new();
    public readonly List<float> Radii = new();
    public Color4 Color;
}

/// <summary>
/// 渲染层：D3D11 交换链（合成用）+ Direct2D 自绘 + DirectComposition 视觉树。
///
/// 视觉树结构照 Chromium / InkTeach 的接法：
///   根视觉（纯容器）
///     ├ 交换链视觉（我们自己画的所有内容：背景、干墨、自绘湿墨）
///     └ 轨迹视觉（内容 = 委托墨迹轨迹，叠在上面；见 DelegatedInkTrail.Attach）
/// 根视觉必须是纯容器——直接往根视觉装内容，轨迹就没地方叠了。
/// </summary>
internal sealed class Scene : IDisposable
{
    private ID3D11Device _device;
    private IDXGIFactory2 _factory;
    private ID2D1Factory1 _d2dFactory;
    private ID2D1Device _d2dDevice;
    private ID2D1DeviceContext _ctx;
    private IDXGISwapChain1 _swapChain;
    private ID2D1Bitmap1 _backBuffer;
    private IDCompositionDevice _dcomp;
    private IDCompositionTarget _target;
    private IDCompositionVisual _rootVisual;
    private IDCompositionVisual _swapVisual;
    private ID2D1SolidColorBrush _brush;
    private ID2D1StrokeStyle _roundStyle;

    public readonly DelegatedInkTrail Trail = new();

    /// <summary>干墨（已收笔）+ 自绘湿墨（进行中）。这"其余都是你自己的"。</summary>
    public readonly List<Stroke> DryStrokes = new();
    public Stroke WetStroke;

    /// <summary>开关：自绘湿墨画不画（画 = 双通道并集；不画 = 只看系统轨迹那一小段）。</summary>
    public bool SelfDrawWet = true;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string Init(IntPtr hwnd, int width, int height)
    {
        Width = width;
        Height = height;

        var levels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
            Vortice.Direct3D.FeatureLevel.Level_10_0,
        };
        D3D11.D3D11CreateDevice((IDXGIAdapter)null, Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, levels, out _device).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _ctx = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        var desc = new SwapChainDescription1(
            (uint)width, (uint)height,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            Vortice.DXGI.AlphaMode.Premultiplied,
            SwapChainFlags.None)
        {
            SampleDescription = new SampleDescription(1, 0),
        };
        _swapChain = _factory.CreateSwapChainForComposition(_device, desc, null);

        // DirectComposition：目标 = 窗口，根视觉纯容器。
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _rootVisual = _dcomp.CreateVisual();
        _swapVisual = _dcomp.CreateVisual();
        _swapVisual.SetContent(_swapChain).CheckError();
        _rootVisual.AddVisual(_swapVisual, true, null).CheckError();
        _target.SetRoot(_rootVisual).CheckError();
        _dcomp.Commit().CheckError();

        // 湿墨迹通道：挂到根视觉上，显式叠在交换链视觉之上。
        Trail.Attach(_dcomp, _swapChain, _rootVisual, _swapVisual);

        // 后缓冲绑给 D2D 当绘制目标。
        var pf = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
        using (var surface = backBuffer.QueryInterface<IDXGISurface>())
        {
            _backBuffer = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        }
        _ctx.Target = _backBuffer;
        _ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

        _brush = _ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f), null);
        _roundStyle = _d2dFactory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });

        return "DirectComposition 已接入: " + Trail.Note;
    }

    /// <summary>起笔。</summary>
    public void BeginStroke(float x, float y, float radius, Color4 color)
    {
        WetStroke = new Stroke { Color = color };
        Trail.Begin(color, radius);       // 先开轨迹，第一个点才喂得进去
        AddPoint(x, y, radius);
    }

    /// <summary>追加一个采样点：自绘缓冲 + 喂系统轨迹（两条通道喂同一批点）。</summary>
    public void AddPoint(float x, float y, float radius)
    {
        if (WetStroke == null) return;
        WetStroke.Points.Add(new Vector2(x, y));
        WetStroke.Radii.Add(radius);
        Trail.AddPoint(x, y, radius);
    }

    /// <summary>收笔：湿墨转干墨，撤轨迹。</summary>
    public void EndStroke()
    {
        if (WetStroke == null) return;
        DryStrokes.Add(WetStroke);
        WetStroke = null;
        Trail.EndNow();
    }

    /// <summary>画一帧 + Present，Present 后给轨迹"结账"。</summary>
    public void RenderFrame()
    {
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0.10f, 0.11f, 0.13f, 1f));   // 深底，墨迹是浅色，截屏数像素好分辨
        foreach (var s in DryStrokes) DrawStroke(s);
        if (SelfDrawWet && WetStroke != null) DrawStroke(WetStroke);
        _ctx.EndDraw();

        var hr = _swapChain.Present(1, PresentFlags.None);

        // 结账：这一帧自绘的点已经上屏，从轨迹里删掉（只在自绘时删）。
        if (!hr.Failure && SelfDrawWet && WetStroke != null) Trail.SettleAsDrawn();
    }

    private void DrawStroke(Stroke s)
    {
        if (s.Points.Count == 0) return;
        _brush.Color = s.Color;
        if (s.Points.Count == 1)
        {
            // 单点：画个小圆点（一条零长的圆头线）。
            var p = s.Points[0];
            _ctx.DrawLine(p, p, _brush, s.Radii[0] * 2f, _roundStyle);
            return;
        }
        for (int i = 1; i < s.Points.Count; i++)
        {
            float w = (s.Radii[i - 1] + s.Radii[i]);      // 直径 = 两点半径之和
            _ctx.DrawLine(s.Points[i - 1], s.Points[i], _brush, MathF.Max(1f, w), _roundStyle);
        }
    }

    public void Clear()
    {
        DryStrokes.Clear();
        WetStroke = null;
    }

    public void Dispose()
    {
        _roundStyle?.Dispose();
        _brush?.Dispose();
        _backBuffer?.Dispose();
        _target?.Dispose();
        _swapVisual?.Dispose();
        _rootVisual?.Dispose();
        _dcomp?.Dispose();
        _swapChain?.Dispose();
        _ctx?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _factory?.Dispose();
        _device?.Dispose();
    }
}
