using System.Diagnostics;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>Process memory sampling, used to attribute the footprint to each
/// stage of start-up rather than quoting a single number.</summary>
internal static class Mem
{
    public static readonly List<string> Log = new();

    public static double Ws()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64 / 1048576.0;
    }

    public static double Priv()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 / 1048576.0;
    }

    public static void Stage(string label)
        => Log.Add($"{label,-38} 工作集 {Ws(),7:F1} MB   私有 {Priv(),7:F1} MB");

    /// <summary>Asks Windows to trim this process's working set. Freeing memory
    /// does not by itself shrink the working set - resident pages are only
    /// dropped under pressure - so this separates "still held" from
    /// "free but still resident".</summary>
    public static void TrimWorkingSet()
    {
        try { Native.EmptyWorkingSet(Native.GetCurrentProcess()); } catch { }
    }
}

/// <summary>Device-level objects shared by every overlay window.</summary>
internal static class Gfx
{
    public static ID3D11Device Device;
    public static IDXGIFactory2 Factory;
    public static ID2D1Factory1 D2DFactory;
    public static ID2D1Device D2DDevice;
    public static IDWriteFactory WriteFactory;
    public static IDWriteTextFormat HudFormat;
    public static IDXGIAdapter3 Adapter3;
    public static string AdapterInfo = "unknown";

    /// <summary>
    /// 圆头圆角的描边样式。**渲染和命中测试必须共用同一个对象**——
    /// 命中测试要判断"这个点算不算落在这条线上"，用的样式必须和画出来的
    /// 一模一样，否则判定结果和肉眼看到的会对不上（箭头、细长图形尤其明显）。
    /// </summary>
    public static ID2D1StrokeStyle1 RoundStroke;

    public static ID2D1StrokeStyle1 Round => RoundStroke ??= D2DFactory.CreateStrokeStyle(
        new StrokeStyleProperties1
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 10f,
        });

    public static void Init()
    {
        Mem.Stage("0. 进程启动（运行时 + 程序集）");

        var levels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
            Vortice.Direct3D.FeatureLevel.Level_10_0,
        };
        var hr = D3D11.D3D11CreateDevice((IDXGIAdapter)null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, levels, out Device);
        hr.CheckError();
        Mem.Stage("1. 创建 Direct3D 11 设备之后");

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        Factory = adapter.GetParent<IDXGIFactory2>();
        try { Adapter3 = adapter.QueryInterface<IDXGIAdapter3>(); } catch { Adapter3 = null; }

        try
        {
            var d = adapter.Description;
            ulong dedicated = (ulong)d.DedicatedVideoMemory;
            ulong shared = (ulong)d.SharedSystemMemory;
            AdapterInfo = $"{d.Description}  专用显存 {dedicated / 1048576.0:F0} MB, 共享内存 {shared / 1048576.0:F0} MB"
                        + (dedicated < 512UL * 1048576 ? "  → 核显（显存来自系统内存）" : "  → 独显");
        }
        catch { }

        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(
            Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        D2DDevice = D2DFactory.CreateDevice(dxgiDevice);
        Mem.Stage("2. 创建 Direct2D 设备之后");

        WriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        HudFormat = WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13f, "zh-CN");
        Mem.Stage("3. 创建 DirectWrite（文字）之后");
    }

    public static void Shutdown()
    {
        RoundStroke?.Dispose();
        RoundStroke = null;
        WriteFactory?.Dispose();
        HudFormat?.Dispose();
        D2DDevice?.Dispose();
        D2DFactory?.Dispose();
        Factory?.Dispose();
        Device?.Dispose();
    }
}

/// <summary>
/// A borderless, click-through-capable, non-activating overlay window that owns
/// one DirectComposition swap chain. This is the core of the whole product: the
/// annotations you see are drawn directly by the GPU into a transparent layer
/// above the desktop.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    /// <summary>Additive blending gives the laser its glow, but costs GPU time
    /// on a full-screen surface. Toggle here to compare.</summary>
    public static bool LaserAdditive = true;
    /// <summary>Number of age bands the laser trail is batched into (0 = don't draw).</summary>
    public static int LaserBands = 7;

    public static long RebuildCount;

    /// <summary>Render pen strokes as stroked centre-lines instead of filled
    /// pressure ribbons. Cheaper to tessellate, but no width-from-pressure.</summary>
    public static bool CenterlineRendering = false;
    /// <summary>
    /// 是否用 Direct2D 的几何实现缓存替代每次重新细分。默认开启：
    /// 实测整层重画快 7 倍、擦除快 3 倍、撤销快 2.2 倍，代价是每个缓存约
    /// 18 KB（有上限保护，见 Stroke.MaxRealizations）。用 --norealize 关掉。
    /// </summary>
    public static bool RealizationEnabled = true;
    public const float RealizationTolerance = 0.25f;

    private ID2D1DeviceContext1 _ctx1;

    public IntPtr Hwnd;
    public int OriginX, OriginY, Width, Height;
    public uint Dpi = 96;

    /// <summary>When true the window reports HTTRANSPARENT so mouse/pen input
    /// falls through to whatever is underneath.</summary>
    public bool PassThrough;

    private IDXGISwapChain1 _swapChain;
    private IDXGISwapChain2 _swapChain2;
    private IntPtr _latencyWait = IntPtr.Zero;

    /// <summary>
    /// 用 DXGI 的"帧延迟等待对象"来配速，而不是靠 Present(1) 阻塞在垂直同步上。
    ///
    /// **实测结论：对合成交换链不适用，默认关闭。** 开启后帧率从 60 冲到 1726，
    /// 绘制 CPU 从 16.5% 涨到 85.7%（单核）、GPU 从 2.9% 涨到 62.9%。
    /// 原因是合成交换链的帧会被 DWM 立刻取走，等待对象几乎不会阻塞，
    /// 失去了垂直同步这层配速。对这种场景 Present(1) 才是正确的节流手段。
    /// 保留代码与 --latencywait 开关，供将来换设备/换驱动时复测。
    /// </summary>
    public static bool LatencyWaitEnabled = false;
    private IDCompositionDevice _dcomp;
    private IDCompositionTarget _target;
    private IDCompositionVisual _visual;
    private IDCompositionInkTrailDevice _inkTrailDevice;
    private IDCompositionDelegatedInkTrail _inkTrail;
    private ID2D1DeviceContext _ctx;
    private ID2D1Bitmap1 _backBuffer;

    private ID3D11Texture2D _contentTexture;
    private ID2D1Bitmap1 _contentTarget;   // render into
    private ID2D1Bitmap1 _contentSource;   // composite from

    // ---- 界面缓存 --------------------------------------------------------
    // 界面画到自己的位图上，只在它声明"变了"的时候重画一次。这是引擎侧的
    // 硬性规矩：调试面板实测每帧 2.7 ms，而笔迹本身只要 0.7 ms——界面一旦
    // 进每帧路径，就比笔迹本身还贵。
    private RectF _uiBounds;          // 物理像素，屏幕坐标
    private RectF _uiLogicalBounds;   // 逻辑像素，屏幕坐标

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    private ID2D1SolidColorBrush _scratch;
    private ID2D1SolidColorBrush _transparentBrush;
    /// <summary>内容层"擦除"用的底色画刷：透明批注时是全透明，白板时是不透明底。</summary>
    private ID2D1SolidColorBrush _boardBrush;
    private Color4 _boardBrushColor = new(-1f, -1f, -1f, -1f);
    private readonly List<Stroke> _strokeScratch = new();

    // ---- 脏区上屏状态 ----------------------------------------------------
    // 每帧只把真正变化的矩形交给 Present1，DWM 就能跳过没变的区域。
    // 注意双缓冲：当前后缓冲里装的是**两帧前**的画面，所以脏区必须覆盖
    // 最近两帧的临时图元范围，否则快速移动的笔画/激光会留下残影。
    private readonly List<RectF> _frameDirty = new();
    private readonly List<Vortice.RawRect> _presentRects = new();
    private readonly RectF[] _transientHistory = new RectF[2];
    private RectF _transientNow = RectF.Empty;
    /// <summary>上一帧界面占的矩形。界面消失时得靠它把旧画面擦掉。</summary>
    private RectF _uiBoundsPrev = RectF.Empty;

    // 内容层的改动也要记两帧：后缓冲里躺着的是两帧前的画面。
    private readonly List<RectF> _contentDirtyNow = new();
    private readonly List<RectF> _contentDirtyPrev = new();

    /// <summary>整块后缓冲内容无效（首帧、重建、尺寸变化）时必须全屏重绘一次。</summary>
    private bool _forceFullFrame = true;

    /// <summary>脏区要回溯几帧的临时图元。双缓冲下必须 ≥2，否则会出残影；
    /// 仅用于对照实验，正常运行不要改。</summary>
    public static int TransientHistoryFrames = 2;
    public int LastPresentRectCount;
    public double LastPresentAreaPercent;
    public const float HudWidth = 760f;
    public const float HudHeight = 158f;

    /// <summary>是否成功拿到微软的委托墨迹轨迹接口（进程级探测结果）。</summary>
    public static bool InkTrailAvailable;
    public static string InkTrailNote = "未尝试";
    public static string InkTrailDebug = "";

    /// <summary>
    /// 是否启用委托墨迹轨迹。**默认关闭**：我们的接口调用全部返回成功，
    /// 但在本机（合成鼠标输入）看不到任何渲染结果，很可能是系统只对
    /// 真实手写笔输入启用这条通道。没有笔设备就无法验证，而一个验证不了的
    /// 绘制通道有可能在真笔上留下重影，所以先关着，等有压感笔时再打开验证。
    /// 用 --inktrail 打开。
    /// </summary>
    public static bool InkTrailEnabled;

    private int _renderedVersion = -1;

    public double LastRebuildMs;
    public double LastAppendMs;
    public double LastPatchMs;
    public int LastPatchCount;
    public double LastRecordMs;
    public double LastPresentMs;
    public int LastDrawnStrokes;
    public string LastError;

    public ID2D1DeviceContext Context => _ctx;

    // ---- 委托墨迹轨迹（微软的低延迟湿墨通道）-----------------------------
    private bool _trailActive;
    private uint _trailGeneration;
    private float _trailRadius = 2f;

    /// <summary>
    /// 开始一条委托墨迹轨迹。此后系统合成器会自己把笔尖后面的这一小段
    /// 画出来，节奏跟着显示器刷新走，不受我们这一帧有没有画完影响。
    /// 我们照旧画自己的笔画；等应用追上来了再把预测段收掉。
    /// </summary>
    public void BeginInkTrail(Color4 color, float radius)
    {
        if (_inkTrail == null || !InkTrailEnabled) return;
        try
        {
            _trailRadius = MathF.Max(0.5f, radius);
            _inkTrail.StartNewTrail(color);
            _trailActive = true;
            _trailGeneration = 0;
            InkTrailDebug = $"StartNewTrail ok, color=({color.R:F2},{color.G:F2},{color.B:F2},{color.A:F2})";
        }
        catch (Exception ex) { _trailActive = false; _inkTrail = null; InkTrailDebug = "StartNewTrail 异常: " + ex.Message; }
    }

    /// <summary>把一个采样点交给系统合成器（屏幕坐标，内部换算到交换链坐标）。</summary>
    public void AddInkTrailPoint(float screenX, float screenY, float radius)
    {
        if (!_trailActive || _inkTrail == null) return;
        try
        {
            var p = new DCompositionInkTrailPoint
            {
                X = screenX - OriginX,
                Y = screenY - OriginY,
                Radius = MathF.Max(0.5f, radius),
            };
            _trailGeneration = _inkTrail.AddTrailPoints(new[] { p }, 1);
            InkTrailDebug = $"AddTrailPoints ok gen={_trailGeneration} at({p.X:F0},{p.Y:F0}) r={p.Radius:F1}";
        }
        catch (Exception ex) { _trailActive = false; InkTrailDebug = "AddTrailPoints 异常: " + ex.Message; }
    }

    /// <summary>收笔：把预测出来的那一段抹掉，交回给我们自己的笔画。</summary>
    public void EndInkTrail()
    {
        if (!_trailActive || _inkTrail == null) { _trailActive = false; return; }
        _trailActive = false;
        try { if (_trailGeneration != 0) _inkTrail.RemoveTrailPoints(_trailGeneration); }
        catch { }
    }

    private static readonly Color4 Transparent = new(0f, 0f, 0f, 0f);

    public string Create(IntPtr hInstance, string className, int x, int y, int w, int h)
    {
        OriginX = x; OriginY = y; Width = w; Height = h;

        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOREDIRECTIONBITMAP;

        Hwnd = Native.CreateWindowEx(exStyle, className, "InkProbe",
            0x80000000L /*WS_POPUP*/, x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (Hwnd == IntPtr.Zero)
            return $"CreateWindowEx failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";

        Dpi = Native.GetDpiForWindow(Hwnd);
        if (Dpi == 0) Dpi = 96;

        try
        {
            CreateDeviceResources();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        return null;
    }

    private void CreateDeviceResources()
    {
        var desc = new SwapChainDescription1(
            (uint)Width, (uint)Height,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            Vortice.DXGI.AlphaMode.Premultiplied,
            LatencyWaitEnabled ? SwapChainFlags.FrameLatencyWaitableObject : SwapChainFlags.None)
        {
            SampleDescription = new SampleDescription(1, 0),
        };

        _swapChain = Gfx.Factory.CreateSwapChainForComposition(Gfx.Device, desc, null);

        if (LatencyWaitEnabled)
        {
            _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (_swapChain2 != null)
            {
                try
                {
                    _swapChain2.MaximumFrameLatency = 1;
                    _latencyWait = _swapChain2.FrameLatencyWaitableObject;
                }
                catch { _latencyWait = IntPtr.Zero; }
            }
            Console.WriteLine($"帧延迟等待对象: {(_latencyWait != IntPtr.Zero ? "已启用" : "不可用，退回垂直同步阻塞")}");
        }
        Mem.Stage($"4. 创建交换链之后（{Width}x{Height}）");

        using var dxgiDevice = Gfx.Device.QueryInterface<IDXGIDevice>();
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(Hwnd, true, out _target).CheckError();
        _visual = _dcomp.CreateVisual();
        _visual.SetContent(_swapChain).CheckError();
        _target.SetRoot(_visual).CheckError();
        _dcomp.Commit().CheckError();

        // 微软的"委托墨迹轨迹"：把正在写的那一笔交给系统合成器去画，
        // 应用自己的延迟就不再影响笔尖跟手程度。这一项要单独探测，
        // 因为它是较新的接口，老系统上没有。
        if (InkTrailNote == "未尝试")
        {
            try
            {
                _inkTrailDevice = _dcomp.QueryInterfaceOrNull<IDCompositionInkTrailDevice>();
                if (_inkTrailDevice != null)
                {
                    _inkTrail = _inkTrailDevice.CreateDelegatedInkTrailForSwapChain(_swapChain);
                    InkTrailAvailable = _inkTrail != null;
                    InkTrailNote = InkTrailAvailable ? "可用" : "接口可用但创建轨迹失败";
                }
                else
                {
                    InkTrailAvailable = false;
                    InkTrailNote = "合成设备不支持 IDCompositionInkTrailDevice";
                }
            }
            catch (Exception ex)
            {
                InkTrailAvailable = false;
                InkTrailNote = "探测异常: " + ex.Message;
            }
        }
        Mem.Stage("5. 接入 DirectComposition 之后");

        _ctx = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        _ctx1 = _ctx.QueryInterfaceOrNull<ID2D1DeviceContext1>();
        _ctx.SetDpi(96f, 96f);
        _ctx.AntialiasMode = AntialiasMode.PerPrimitive;
        _ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
        using (var surface = backBuffer.QueryInterface<IDXGISurface>())
        {
            _backBuffer = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
        }

        // Offscreen layer holding everything that has already been committed.
        // Two Direct2D views over one Direct3D texture: a target to draw into,
        // and a source to composite from (a target bitmap cannot be read back).
        var texDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _contentTexture = Gfx.Device.CreateTexture2D(texDesc);

        using (var surface = _contentTexture.QueryInterface<IDXGISurface>())
        {
            _contentTarget = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
            _contentSource = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.None));
        }

        _scratch = _ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f), null);
        _transparentBrush = _ctx.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0f), null);
        _boardBrush = _ctx.CreateSolidColorBrush(Transparent, null);
        Mem.Stage("6. 创建离屏内容层之后");
    }

    /// <summary>
    /// 取当前该用的底色画刷。透明批注时就是全透明（等于把这一块擦干净），
    /// 白板时是不透明的底色。底色变了才重建画刷，正常每帧不分配。
    /// </summary>
    private ID2D1SolidColorBrush BoardBrush(InkEngine app)
    {
        var want = app.BoardOn ? app.BoardColor : Transparent;
        if (want.R != _boardBrushColor.R || want.G != _boardBrushColor.G
            || want.B != _boardBrushColor.B || want.A != _boardBrushColor.A)
        {
            _boardBrush.Color = want;
            _boardBrushColor = want;
        }
        return _boardBrush;
    }

    private ID2D1SolidColorBrush Brush(Color4 c)
    {
        uint key = ((uint)(Math.Clamp(c.R, 0, 1) * 255) << 24)
                 | ((uint)(Math.Clamp(c.G, 0, 1) * 255) << 16)
                 | ((uint)(Math.Clamp(c.B, 0, 1) * 255) << 8)
                 | (uint)(Math.Clamp(c.A, 0, 1) * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = _ctx.CreateSolidColorBrush(c, null);
        _brushes[key] = b;
        return b;
    }

    // ------------------------------------------------------------------
    //  Content layer
    // ------------------------------------------------------------------

    private void EnsureContent(InkEngine app)
    {
        var doc = app.Doc;
        if (_renderedVersion == doc.Version) return;

        if (_renderedVersion < 0 || doc.Dirty.Full)
        {
            RebuildAll(doc, app);
        }
        else if (doc.PendingAppend != null && doc.Dirty.Rects.Count == 1)
        {
            var sw = Stopwatch.StartNew();
            DrawOnlyPatch(doc.PendingAppend);
            sw.Stop();
            LastPatchMs = sw.Elapsed.TotalMilliseconds;
            LastPatchCount = 1;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            int patched = PatchRegions(doc, app);
            sw.Stop();
            LastPatchMs = sw.Elapsed.TotalMilliseconds;
            LastPatchCount = patched;
        }

        _renderedVersion = doc.Version;
    }

    private RectF ClipToWindow(RectF r)
    {
        float l = MathF.Max(r.MinX, OriginX);
        float t = MathF.Max(r.MinY, OriginY);
        float rr = MathF.Min(r.MaxX, OriginX + Width);
        float b = MathF.Min(r.MaxY, OriginY + Height);
        if (rr < l || b < t) return RectF.Empty;
        return new RectF { MinX = l, MinY = t, MaxX = rr, MaxY = b };
    }

    // ------------------------------------------------------------------
    //  界面层
    // ------------------------------------------------------------------

    /// <summary>
    /// 界面矩形是否盖住这个**逻辑**屏幕坐标（用于输入的第一次命中测试）。
    /// 传进来的坐标已由引擎除以 DPI 缩放，和界面自己的坐标系一致。
    /// </summary>
    public bool UiBoundsLogicalContains(float screenX, float screenY)
    {
        if (_uiLogicalBounds.IsEmpty) return false;
        return screenX >= _uiLogicalBounds.MinX && screenX < _uiLogicalBounds.MaxX
            && screenY >= _uiLogicalBounds.MinY && screenY < _uiLogicalBounds.MaxY;
    }

    /// <summary>诊断用：界面这一帧占的屏幕矩形。</summary>
    public string UiDebug => _uiBounds.IsEmpty
        ? "无"
        : $"({_uiBounds.MinX:F0},{_uiBounds.MinY:F0})-({_uiBounds.MaxX:F0},{_uiBounds.MaxY:F0})";

    /// <summary>
    /// 布局界面。返回界面这一帧是否可见。
    ///
    /// 坐标约定（这是引擎和界面之间最容易出错的一点，写死在这里）：
    /// 界面拿到的是"自己那块画布"的坐标，永远从 (0,0) 开始，单位是逻辑像素；
    /// 它把悬浮条摆在自己画布里的哪个位置，返回哪个矩形。引擎负责把逻辑矩形
    /// 乘以 dpiScale 变成屏幕上的物理矩形——界面不需要知道物理像素和 DPI。
    /// </summary>
    private bool PrepareUi(InkEngine app)
    {
        var ui = app.Ui;
        if (ui == null || !ui.Visible)
        {
            _uiLogicalBounds = RectF.Empty;
            _uiBounds = RectF.Empty;
            return false;
        }

        float dpiScale = Dpi / 96f;

        // 界面看到的逻辑屏幕：覆盖窗口的屏幕范围除以 DPI。界面用逻辑坐标返回
        // 自己占哪一块，引擎乘 dpiScale 就得到物理矩形——放大只发生这一次。
        var uiScreen = new RectF
        {
            MinX = OriginX / dpiScale, MinY = OriginY / dpiScale,
            MaxX = (OriginX + Width) / dpiScale, MaxY = (OriginY + Height) / dpiScale,
        };

        // 界面只在布局会变的时候调（尺寸/DPI 变化）。正常每帧都走缓存。
        if (_uiLayoutBounds.IsEmpty || _uiLayoutDpi != dpiScale
            || !_uiLayoutScreen.Equals(uiScreen))
        {
            _uiLayoutBounds = ui.Layout(uiScreen, dpiScale);
        _uiLayoutScreen = uiScreen;
        _uiLayoutDpi = dpiScale;
        }

        // 占用矩形每帧都问一次：悬浮条被拖动、展开调色板、折叠收起、暂时消失，
        // 都靠这个方法告诉引擎；否则命中测试和脏区会一直停在旧位置。
        var live = ui.QueryBounds();
        _uiLogicalBounds = live;
        _uiBounds = live.IsEmpty ? RectF.Empty : new RectF
        {
            MinX = live.MinX * dpiScale,
            MinY = live.MinY * dpiScale,
            MaxX = live.MaxX * dpiScale,
            MaxY = live.MaxY * dpiScale,
        };

        return !live.IsEmpty;
    }

    private RectF _uiLayoutScreen = RectF.Empty;
    private float _uiLayoutDpi = -1f;
    /// <summary>Layout 上一次返回的逻辑矩形，用来判断是否需要重新布局。</summary>
    private RectF _uiLayoutBounds = RectF.Empty;

    /// <summary>
    /// 把界面画到后缓冲上。
    ///
    /// 每帧只重画界面自己那一小块矩形（裁剪 + 按其逻辑坐标绘制），整屏的其它
    /// 部分一律不动。实测这块的代价在 1 毫秒以内，和一个脏区补丁同量级。
    ///
    /// 两种"更省"的做法都试过，都不成立，别走回头路：
    ///  1) 把界面画进一张位图再 DrawBitmap 贴上去 —— Direct2D 的位图一旦作为
    ///     渲染目标，就不能再当绘制源，贴上去永远是一张空图（内容层因此才需要
    ///     _contentTarget / _contentSource 两个视图）。
    ///  2) 把界面的绘制录成 ID2D1CommandList 再重放 —— 录下来的命令重放时
    ///     在 2 倍屏上会被放大四次或被推到画布外，本机未能拿到稳定结果。
    /// 等界面真的变得很复杂（大量模糊/阴影）再回来解决，届时要连 4K 一起量。
    /// </summary>
    private void DrawUi(InkEngine app)
    {
        float dpiScale = Dpi / 96f;
        // 界面画在自己的绝对逻辑坐标里（和它 Layout 拿到的逻辑屏幕同一套），
        // 引擎负责换算成物理像素：先乘 dpiScale，再减去窗口原点。
        _ctx.SetDpi(96f, 96f);
        _ctx.Transform = Matrix3x2.CreateScale(dpiScale)
                       * Matrix3x2.CreateTranslation(-OriginX, -OriginY);
        // 裁剪矩形同样用逻辑坐标（会被上面的变换一起作用）。
        var clip = new Vortice.RawRectF(_uiLogicalBounds.MinX, _uiLogicalBounds.MinY,
                                        _uiLogicalBounds.MaxX, _uiLogicalBounds.MaxY);
        _ctx.PushAxisAlignedClip(clip, AntialiasMode.Aliased);
        try
        {
            app.Ui.Render(_ctx, UiTheme.Default);
        }
        catch (Exception ex)
        {
            LastError = "UI render: " + ex;
            Console.WriteLine("UI render 异常: " + ex);
        }
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.PopAxisAlignedClip();
    }

    /// <summary>
    /// Appends a stroke that sits on top of everything already committed, so
    /// the region only needs drawing - no erase pass. This is the hot path: it
    /// runs once per finished stroke.
    /// </summary>
    private void DrawOnlyPatch(Stroke s)
    {
        var r = ClipToWindow(s.PaddedBounds);
        if (r.IsEmpty) return;

        _ctx.Target = _contentTarget;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.CreateTranslation(-OriginX, -OriginY);
        _ctx.PushAxisAlignedClip(
            new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
            AntialiasMode.Aliased);
        DrawStroke(s);
        _ctx.PopAxisAlignedClip();
        _ctx.Transform = Matrix3x2.Identity;
        var hr = _ctx.EndDraw();
        try { _ctx.Flush(out _, out _); } catch { }
        _ctx.Target = null;

        if (hr.Failure) LastError = "append EndDraw: " + hr.Description;
    }

    /// <summary>
    /// Repaints every stale rectangle in one pass: wipe each one, then redraw
    /// only the items that actually cross it.
    ///
    /// Two things make this cheap. All the rectangles share a single
    /// BeginDraw/EndDraw pair and a single Flush, because Flush waits for the
    /// GPU and paying that once per rectangle dominates everything else. And the
    /// candidate strokes come from the spatial index instead of a scan over the
    /// whole document.
    /// </summary>
    private int PatchRegions(InkDocument doc, InkEngine app)
    {
        var rects = doc.Dirty.Rects;
        if (rects.Count == 0) return 0;

        _ctx.Target = _contentTarget;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.CreateTranslation(-OriginX, -OriginY);

        int patched = 0;
        foreach (var raw in rects)
        {
            var r = ClipToWindow(raw);
            if (r.IsEmpty) continue;

            var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
            _ctx.PushAxisAlignedClip(box, AntialiasMode.Aliased);

            // Clear() on a Direct2D target ignores the current clip, so punch
            // the hole with a Copy-blended fill instead.
            _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
            _ctx.FillRectangle(box, BoardBrush(app));
            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

            doc.QueryGrid(r, _strokeScratch);
            foreach (var s in _strokeScratch) DrawStroke(s);

            _ctx.PopAxisAlignedClip();
            patched++;
        }

        _ctx.Transform = Matrix3x2.Identity;
        var hr = _ctx.EndDraw();
        try { _ctx.Flush(out _, out _); } catch { }
        _ctx.Target = null;

        if (hr.Failure) LastError = "patch EndDraw: " + hr.Description;
        return patched;
    }

    private void RebuildAll(InkDocument doc, InkEngine app)
    {
        RebuildCount++;
        // 整层重画意味着后缓冲里那块内容全过时了，这一帧必须全屏重绘一次。
        _forceFullFrame = true;
        var sw = Stopwatch.StartNew();
        _ctx.Target = _contentTarget;
        _ctx.BeginDraw();
        // 白板模式下这一层整体铺底色；透明批注时就是清空。
        _ctx.Clear(app.BoardOn ? app.BoardColor : Transparent);
        _ctx.Transform = Matrix3x2.CreateTranslation(-OriginX, -OriginY);

        int drawn = 0;
        var view = new RectF { MinX = OriginX, MinY = OriginY, MaxX = OriginX + Width, MaxY = OriginY + Height };
        foreach (var s in doc.Strokes)
        {
            if (!s.IntersectsRect(view)) continue;
            DrawStroke(s);
            drawn++;
        }

        _ctx.Transform = Matrix3x2.Identity;
        var hr = _ctx.EndDraw();
        // Flush is required before this surface can be read back as a source.
        try { _ctx.Flush(out _, out _); } catch { }
        _ctx.Target = null;
        sw.Stop();

        LastDrawnStrokes = drawn;
        LastRebuildMs = sw.Elapsed.TotalMilliseconds;
        if (hr.Failure) LastError = "rebuild EndDraw: " + hr.Description;
    }

    /// <summary>供分辨率实测复用同一套绘制路径。</summary>
    public void DrawStrokeForTest(Stroke s) => DrawStroke(s);

    /// <summary>供分辨率实测复用同一套"擦掉一块"逻辑。</summary>
    public void ClearRectForTest(RectF r)
    {
        _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        _ctx.FillRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), _transparentBrush);
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
    }

    /// <summary>
    /// 画一个对象。**调用方负责把 ctx 变换设成"画布坐标 → 窗口坐标"**，
    /// 这里只在对象自己带变换时再左乘一下。
    ///
    /// 单位变换（绝大多数对象）走的是原路，一次多余的取/设变换都不做——
    /// 这条路径每帧要给上万个对象跑，不能为了"以后可能用到"先付成本。
    /// </summary>
    private void DrawStroke(Stroke s, bool allowRealization = true)
    {
        if (s.Transform.IsIdentity)
        {
            DrawStrokeCore(s, allowRealization);
            return;
        }

        // 局部 → 画布（s.Transform），再 画布 → 窗口（调用方设的）。
        // 乘法顺序按 System.Numerics 的约定：先作用左边的。
        var canvasToWindow = _ctx.Transform;
        _ctx.Transform = s.Transform * canvasToWindow;
        DrawStrokeCore(s, allowRealization);
        _ctx.Transform = canvasToWindow;
    }

    private void DrawStrokeCore(Stroke s, bool allowRealization)
    {
        if (RealizationEnabled && allowRealization && !s.IsShape)
        {
            var real = s.GetRealization(_ctx1, RealizationTolerance);
            if (real != null)
            {
                // DrawGeometryRealization 定义在 ID2D1DeviceContext1 上
                //
                // 注意：几何实现是把局部几何**预先三角化**过的，画的时候整个
                // 交给 ctx 变换。所以等比缩放没问题，**非等比拉伸会把笔宽一起
                // 拉扁**。我们选了"拉伸时线宽不变"，所以非等比变换之后必须
                // 走重建几何那条路（见计划文档 7.1）。
                _ctx1.DrawGeometryRealization(real, Brush(s.Color));
                return;
            }
        }

        var geo = s.BuildGeometry(Gfx.D2DFactory);
        if (geo == null) return;
        if (s.IsShape || CenterlineRendering)
        {
            _ctx.DrawGeometry(geo, Brush(s.Color), MathF.Max(1f, s.Width), Gfx.Round);
        }
        else
        {
            _ctx.FillGeometry(geo, Brush(s.Color));
        }
    }

    // ------------------------------------------------------------------
    //  Frame
    // ------------------------------------------------------------------

    public void RenderFrame(InkEngine app)
    {
        // 界面：先布局、该重画就重画一次，拿到这一帧的矩形。
        bool uiVisible = PrepareUi(app);

        // The content layer must be rebuilt *before* the swap-chain target is
        // bound: switching targets in the middle of BeginDraw/EndDraw puts
        // Direct2D into an error state.
        if (!app.NoContentCache)
            EnsureContent(app);

        _transientNow = ComputeTransientBounds(app);
        UpdateFrameDirty(app, uiVisible);

        var sw = Stopwatch.StartNew();
        _ctx.Target = _backBuffer;
        _ctx.BeginDraw();

        // 逐块重绘。以前是"全屏清屏 + 全屏贴图 + 全屏上屏"，一帧要动 5.2M 像素；
        // 实测这才是在核显上占 8~9% GPU 的真正原因（不是上屏，Present1 之后
        // 脏区只有 0.1~0.8%，GPU 却一点没降）。现在整条管线都只动脏区。
        foreach (var raw in _frameDirty)
        {
            var c = ClipToWindow(raw);
            if (c.IsEmpty) continue;

            var box = new Vortice.RawRectF(c.MinX, c.MinY, c.MaxX, c.MaxY);
            _ctx.PushAxisAlignedClip(box, AntialiasMode.Aliased);

            // Clear() 不受裁剪影响，所以用 Copy 混合的填充来"擦"这一块。
            _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
            _ctx.FillRectangle(box, BoardBrush(app));
            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

            if (app.NoContentCache)
            {
                // Naive path: re-rasterise every committed stroke, every frame.
                _ctx.Transform = Matrix3x2.CreateTranslation(-OriginX, -OriginY);
                foreach (var s in app.Doc.Strokes)
                {
                    if (!s.IntersectsRect(c)) continue;
                    DrawStroke(s);
                }
            }
            else
            {
                _ctx.DrawBitmap(_contentSource, 1f, Vortice.Direct2D1.InterpolationMode.NearestNeighbor);
                _ctx.Transform = Matrix3x2.CreateTranslation(-OriginX, -OriginY);
            }

            // 调试用：--trailonly 时不画自己那一笔，用来验证委托墨迹轨迹
            // 是不是真的由系统合成器画出来了。
            if (app.ActiveStroke != null && !app.SuppressActiveStroke)
            // 正在写的那一笔几何每帧都在变，用实现缓存只会不停重建，反而更慢
            DrawStroke(app.ActiveStroke, allowRealization: false);

            DrawSelection(app);
            DrawLaser(app);
            DrawEraserCursor(app);
            DrawMarquee(app);

            _ctx.Transform = Matrix3x2.Identity;

            if (app.ShowHud)
                DrawHud(app.HudText);

            _ctx.PopAxisAlignedClip();
        }

        // 界面画在所有笔迹之上。它每帧只贴一张缓存位图，不用重画内容。
        if (uiVisible)
            DrawUi(app);

        var hr = _ctx.EndDraw();
        _ctx.Target = null;
        sw.Stop();

        LastRecordMs = sw.Elapsed.TotalMilliseconds;
        if (hr.Failure) LastError = "frame EndDraw: " + hr.Description;

        // 记住这一帧界面的位置，下一帧靠它把"刚刚消失"的界面擦干净。
        _uiBoundsPrev = uiVisible ? _uiBounds : RectF.Empty;
    }

    /// <summary>
    /// 每帧都会重绘的元素的包围盒：正在书写的那一笔、激光轨迹、橡皮光标、
    /// 框选矩形、性能面板。它们不进内容层，所以每帧都要重新画，
    /// 也就必须每帧都算进脏区。
    /// </summary>
    private RectF ComputeTransientBounds(InkEngine app)
    {
        var r = RectF.Empty;

        if (app.ActiveStroke != null)
            r.Add(app.ActiveStroke.PaddedBounds);

        var laser = app.Laser.Points;
        if (app.Laser.Visible && laser.Count > 0)
        {
            var b = RectF.Empty;
            foreach (var p in laser) b.Add(p.X, p.Y);
            r.Add(b.Inflate(32f));      // 轨迹有宽度和发光，往外留一点
        }

        if (app.Tool == Tool.Eraser && app.PointerInside)
        {
            // 橡皮光标比橡皮半径大一圈（描边 + 四个方向的刻度），
            // 脏区要跟着放大，否则快速划过会留下光标的残影。
            float rad = app.EraserRadius * 1.3f + 8f;
            var c = RectF.Empty;
            c.Add(app.PointerX - rad, app.PointerY - rad);
            c.Add(app.PointerX + rad, app.PointerY + rad);
            r.Add(c);
        }

        if (app.MarqueeActive)
        {
            var m = RectF.Empty;
            m.Add(app.MqMinX, app.MqMinY);
            m.Add(app.MqMaxX, app.MqMaxY);
            r.Add(m.Inflate(3f));
        }

        // 选中高亮画在浮动层上、不进内容层，所以它的区域必须每帧算进脏区。
        //
        // 注意**不能只算对象自己的包围盒**：选中框这一套 UI 比对象大——
        // 外侧的光晕、跨在边线上的手柄、以及伸到上边外侧的旋转手柄。
        // 漏掉哪一块，哪一块就会在屏幕上留下擦不掉的残影（实测踩过：
        // 旋转手柄旁边留了一小块红色的前帧残留）。
        if (app.Doc.Selected.Count > 0)
        {
            var sb = EditRegion.Of(app.Doc.Selected);
            float dpi = app.DpiScale;
            float margin = SelectionHandles.VisualSizeLogical * 0.5f * dpi + 6f;
            var ui = sb.Inflate(margin);

            var rot = SelectionHandles.Position(SelHandle.Rotate, sb, dpi);
            float grip = SelectionHandles.RotateGripLogical * 0.5f * dpi + 3f;
            ui.Add(rot.X - grip, rot.Y - grip);
            ui.Add(rot.X + grip, rot.Y + grip);
            r.Add(ui);
        }

        if (app.ShowHud)
        {
            var h = RectF.Empty;
            h.Add(OriginX + 10, OriginY + 10);
            h.Add(OriginX + 12 + HudWidth + 4, OriginY + 12 + HudHeight + 4);
            r.Add(h);
        }

        return r;
    }

    /// <summary>汇总这一帧要上屏的矩形交给 Present1。</summary>
    private void UpdateFrameDirty(InkEngine app, bool uiVisible)
    {
        _frameDirty.Clear();

        var full = new RectF
        {
            MinX = OriginX, MinY = OriginY,
            MaxX = OriginX + Width, MaxY = OriginY + Height,
        };

        var doc = app.Doc;
        _contentDirtyNow.Clear();
        if (doc.Dirty.Full)
            _contentDirtyNow.Add(full);
        else
            foreach (var raw in doc.Dirty.Rects) _contentDirtyNow.Add(raw);

        if (_forceFullFrame)
        {
            // 首帧 / 内容层整层重建 / 尺寸变化：整块后缓冲都不可信。
            _frameDirty.Add(full);
            _contentDirtyPrev.Clear();
            _forceFullFrame = false;
        }
        else
        {
            // 这一帧和上一帧的内容改动都要重画（后缓冲里是两帧前的画面）
            AddClipped(_frameDirty, _contentDirtyPrev);
            AddClipped(_frameDirty, _contentDirtyNow);
        }

        // 临时图元：这一帧 + 前两帧（双缓冲里躺着的是两帧前的画面）
        var t = _transientNow;
        for (int i = 0; i < Math.Clamp(TransientHistoryFrames, 0, 2); i++)
            t.Add(_transientHistory[i]);
        if (!t.IsEmpty) AddClipped(_frameDirty, t);

        // 界面每帧都会重新贴到后缓冲上，所以它那块区域每帧都得算进上屏的脏区，
        // 否则双缓冲一交换，界面就会闪一下或干脆不见了。
        // 界面这一帧和上一帧占的地方都要算进来：界面可能是刚刚消失的
        // （比如穿透提示淡出、调色板收起），双缓冲里还留着它的旧画面。
        if (uiVisible) AddClipped(_frameDirty, _uiBounds);
        if (!_uiBoundsPrev.IsEmpty) AddClipped(_frameDirty, _uiBoundsPrev);
        if (!_uiBoundsPrev.IsEmpty) AddClipped(_frameDirty, _uiBoundsPrev);

        // 兜底：真要是一个矩形都没有，就整屏来一次，避免出现没擦干净的画面
        if (_frameDirty.Count == 0) _frameDirty.Add(full);

        _transientHistory[1] = _transientHistory[0];
        _transientHistory[0] = _transientNow;

        _contentDirtyPrev.Clear();
        _contentDirtyPrev.AddRange(_contentDirtyNow);
    }

    private void AddClipped(List<RectF> list, RectF r)
    {
        var c = ClipToWindow(r);
        if (!c.IsEmpty) list.Add(c);
    }

    private void AddClipped(List<RectF> list, List<RectF> rects)
    {
        foreach (var r in rects) AddClipped(list, r);
    }

    /// <summary>
    /// 画选中框和手柄。规格见 design/选中与操作条-设计稿.png（方案 B）。
    ///
    /// 三条设计约束，都跟教室场景有关：
    ///   · **画在浮动层上，不进内容层**。否则每改一次选区就要重画整块内容层，
    ///     而选中是高频操作（每拖一下框选都要变）。
    ///   · **光晕是必须的，不是装饰**。一盏投影打上去，深色 PPT 上一条纯蓝线
    ///     会糊掉；描边外侧那圈半透明蓝让它在浅色和深色背景上都立得住。
    ///   · 手柄的**位置和命中判定都在 SelectionHandles 里算**（那一层只有数学）。
    ///     这里只负责画——换观感改这里，改交互规则改那边，互不牵连。
    ///
    /// 操作条（复制/删除/翻转/旋转那排按钮）还没画，等图标设计定稿。
    /// </summary>
    private void DrawSelection(InkEngine app)
    {
        var sel = app.Doc.Selected;
        if (sel.Count == 0) return;

        // 多选时画一个总框，而不是每个对象一个框——拖动就是整组一起动。
        var b = EditRegion.Of(sel);
        if (b.IsEmpty) return;

        float dpi = app.DpiScale;
        var accent = new Color4(0f, 0.47f, 0.83f, 1f);      // #0078D4

        // 1) 光晕
        _scratch.Color = new Color4(accent.R, accent.G, accent.B, 0.16f);
        _ctx.DrawRectangle(
            new Vortice.RawRectF(b.MinX - 2f, b.MinY - 2f, b.MaxX + 2f, b.MaxY + 2f),
            _scratch, 6.5f);

        // 2) 描边
        _scratch.Color = accent;
        var outline = new Vortice.RawRectF(b.MinX, b.MinY, b.MaxX, b.MaxY);
        _ctx.DrawRectangle(outline, _scratch, 2.5f);

        // 3) 旋转手柄（在上边中点外侧，先画连线再画圆）
        float rotR = SelectionHandles.RotateGripLogical * 0.5f * dpi;
        var rot = SelectionHandles.Position(SelHandle.Rotate, b, dpi);
        var topCenter = new Vector2((b.MinX + b.MaxX) * 0.5f, b.MinY);
        _ctx.DrawLine(topCenter, rot, _scratch, 1.4f);
        var white = Brush(new Color4(1f, 1f, 1f, 1f));
        _ctx.FillEllipse(new Ellipse(rot, rotR, rotR), white);
        _ctx.DrawEllipse(new Ellipse(rot, rotR, rotR), _scratch, 1.6f);
        // 转圈的弧。Direct2D 的上下文没有 DrawArc，得自己拼一条路径——
        // 采样十几个点连成折线就够了：这段弧半径不到 9 像素，看不出折。
        using (var arc = Gfx.D2DFactory.CreatePathGeometry())
        {
            using (var sink = arc.Open())
            {
                sink.BeginFigure(PointOnCircle(rot, rotR * 0.55f, 40f), FigureBegin.Hollow);
                for (int i = 1; i <= 14; i++)
                    sink.AddLine(PointOnCircle(rot, rotR * 0.55f, 40f + 260f * i / 14f));
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            _ctx.DrawGeometry(arc, _scratch, 1.8f);
        }

        // 4) 八个手柄。白底 + 蓝边：深色背景上是白方块显眼，
        //    浅色背景上靠蓝边立住，一套画法两边都成立。
        float hs = SelectionHandles.VisualSizeLogical * dpi;
        float radius = hs * 0.28f;
        Span<SelHandle> all = stackalloc SelHandle[]
        {
            SelHandle.TopLeft, SelHandle.Top, SelHandle.TopRight, SelHandle.Right,
            SelHandle.BottomRight, SelHandle.Bottom, SelHandle.BottomLeft, SelHandle.Left,
        };
        foreach (var h in all)
        {
            var p = SelectionHandles.Position(h, b, dpi);
            var box = new Vortice.RawRectF(p.X - hs * 0.5f, p.Y - hs * 0.5f,
                                           p.X + hs * 0.5f, p.Y + hs * 0.5f);
            var rr = new RoundedRectangle(box, radius, radius);
            _ctx.FillRoundedRectangle(rr, white);
            _ctx.DrawRoundedRectangle(rr, _scratch, 1.8f);
        }
    }

    /// <summary>圆上某个角度上的点，用来拼小圆弧（画旋转手柄的转向标记）。</summary>
    private static Vector2 PointOnCircle(Vector2 c, float r, float deg)
    {
        float a = deg * MathF.PI / 180f;
        return new Vector2(c.X + r * MathF.Cos(a), c.Y + r * MathF.Sin(a));
    }

    private void DrawLaser(InkEngine app)
    {
        var pts = app.Laser.Points;
        int n = pts.Count;
        if (n < 2) return;

        double now = app.NowMs;
        _ctx.PrimitiveBlend = LaserAdditive ? PrimitiveBlend.Add : PrimitiveBlend.SourceOver;

        // Diagnostic modes used by the measurement report.
        if (LaserBands == -1)
        {
            _scratch.Color = new Color4(1f, 0.2f, 0.2f, 0.6f);
            _ctx.DrawLine(new Vector2(pts[0].X, pts[0].Y), new Vector2(pts[n - 1].X, pts[n - 1].Y), _scratch, 26f);
            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
            return;
        }
        if (LaserBands == -2)
        {
            _scratch.Color = new Color4(1f, 0.2f, 0.2f, 0.6f);
            _ctx.FillRectangle(new Vortice.RawRectF(pts[n - 1].X, pts[n - 1].Y, pts[n - 1].X + 20, pts[n - 1].Y + 20), _scratch);
            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
            return;
        }
        if (LaserBands == -3)
        {
            // Build the same trail geometry but never draw it: isolates the cost
            // of building a path geometry from the cost of rasterising it.
            using var g = BuildPolyline(pts, 0, n - 1);
            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
            return;
        }

        if (LaserBands == 7)
        {
            // Measurements say a *stroked* path geometry costs milliseconds per
            // call no matter how short it is, while a *filled* one costs tens of
            // microseconds. So the tail is built as a filled ribbon whose width
            // tapers to nothing at the oldest end - which also reads as a comet
            // trail, exactly what a laser pointer should look like.
            using (var glow = BuildTaperedRibbon(pts, 0, n - 1, 13f, 0f))
            {
                if (glow != null)
                {
                    _scratch.Color = new Color4(1f, 0.16f, 0.16f, 0.45f);
                    _ctx.FillGeometry(glow, _scratch);
                }
            }

            int head = Math.Max(1, n / 3);
            using (var core = BuildTaperedRibbon(pts, n - 1 - head, n - 1, 6f, 0f))
            {
                if (core != null)
                {
                    _scratch.Color = new Color4(1f, 0.96f, 0.92f, 0.95f);
                    _ctx.FillGeometry(core, _scratch);
                }
            }

            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
            return;
        }

        // The trail fades with age. Drawing one line per segment costs one draw
        // call per point; batching the trail into a handful of age bands keeps
        // the fade but cuts the draw calls by an order of magnitude.
        const int bandsMax = 6;
        int bands = Math.Clamp(LaserBands, 0, bandsMax);
        if (bands == 0) { _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver; return; }
        int per = Math.Max(1, (n - 1) / bands);
        for (int b = 0; b < bands; b++)
        {
            int start = b * per;
            int end = Math.Min(n - 1, start + per);
            if (end <= start) break;

            double age = now - pts[(start + end) / 2].T;
            float t = (float)Math.Clamp(1.0 - age / LaserTrail.LifetimeMs, 0, 1);
            if (t <= 0.02f) continue;

            using var geo = BuildPolyline(pts, start, end);
            _scratch.Color = new Color4(1f, 0.18f, 0.18f, 0.12f * t);
            _ctx.DrawGeometry(geo, _scratch, 26f);
            _scratch.Color = new Color4(1f, 0.95f, 0.90f, 0.95f * t);
            _ctx.DrawGeometry(geo, _scratch, 5f);
        }

        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
    }

    private static ID2D1PathGeometry BuildPolyline(IReadOnlyList<InkPoint> pts, int start, int end)
    {
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(new Vector2(pts[start].X, pts[start].Y), FigureBegin.Hollow);
        for (int i = start + 1; i <= end; i++)
            sink.AddLine(new Vector2(pts[i].X, pts[i].Y));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// Filled ribbon along a slice of the trail, tapering from headHalf at the
    /// newest point down to tailHalf at the oldest. Returns null when the slice
    /// is too short to form a polygon.
    /// </summary>
    private static ID2D1PathGeometry BuildTaperedRibbon(
        IReadOnlyList<InkPoint> pts, int start, int end, float headHalf, float tailHalf)
    {
        start = Math.Max(0, start);
        end = Math.Min(pts.Count - 1, end);
        int n = end - start + 1;
        if (n < 2) return null;

        var outline = new Vector2[n * 2];
        for (int i = 0; i < n; i++)
        {
            int idx = start + i;
            int a = idx > start ? idx - 1 : idx;
            int b = idx < end ? idx + 1 : idx;
            float dx = pts[b].X - pts[a].X;
            float dy = pts[b].Y - pts[a].Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            float nx = -dy, ny = dx;

            float t = i / (float)(n - 1);          // 0 = tail, 1 = head
            float hw = tailHalf + (headHalf - tailHalf) * t;

            outline[i] = new Vector2(pts[idx].X + nx * hw, pts[idx].Y + ny * hw);
            outline[n * 2 - 1 - i] = new Vector2(pts[idx].X - nx * hw, pts[idx].Y - ny * hw);
        }

        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.SetFillMode(Vortice.Direct2D1.FillMode.Winding);
            sink.BeginFigure(outline[0], FigureBegin.Filled);
            for (int i = 1; i < outline.Length; i++) sink.AddLine(outline[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

    private void DrawEraserCursor(InkEngine app)
    {
        if (app.Tool != Tool.Eraser || !app.PointerInside) return;

        float r = app.EraserRadius;
        var c = new Vector2(app.PointerX, app.PointerY);

        // 中间一层很淡的填充，让"要擦掉的范围"一眼可见。
        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(new Color4(0.35f, 0.55f, 0.95f, 0.10f)));

        // 双色描边：外圈浅、内圈深。单色圆在深色桌面和白色白板上总有一种看不清，
        // 双层描边两种背景上都成立。
        _ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f),
            Brush(new Color4(1f, 1f, 1f, 0.75f)), 1.5f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.85f);
        _ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f), _scratch, 1.5f);

        // 四个方向的小刻度标出圆心，落点更准。
        float tick = MathF.Max(4f, r * 0.22f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.7f);
        _ctx.DrawLine(new Vector2(c.X - r - tick, c.Y), new Vector2(c.X - r + tick * 0.2f, c.Y), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X + r - tick * 0.2f, c.Y), new Vector2(c.X + r + tick, c.Y), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - r - tick), new Vector2(c.X, c.Y - r + tick * 0.2f), _scratch, 1.5f);
        _ctx.DrawLine(new Vector2(c.X, c.Y + r - tick * 0.2f), new Vector2(c.X, c.Y + r + tick), _scratch, 1.5f);
    }

    private void DrawMarquee(InkEngine app)
    {
        if (!app.MarqueeActive) return;
        var r = new Vortice.RawRectF(app.MqMinX, app.MqMinY, app.MqMaxX, app.MqMaxY);
        _ctx.FillRectangle(r, Brush(new Color4(0.25f, 0.6f, 1f, 0.12f)));
        _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.95f);
        _ctx.DrawRectangle(r, _scratch, 1.2f);
    }

    private void DrawHud(string text)
    {
        float w = HudWidth, h = HudHeight;
        var box = new Vortice.RawRectF(12, 12, 12 + w, 12 + h);
        _ctx.FillRectangle(box, Brush(new Color4(0.05f, 0.06f, 0.09f, 0.78f)));
        _scratch.Color = new Color4(0.35f, 0.8f, 1f, 0.9f);
        _ctx.DrawRectangle(box, _scratch, 1f);
        _scratch.Color = new Color4(0.92f, 0.96f, 1f, 1f);
        _ctx.DrawText(text, Gfx.HudFormat, new Rect(24, 20, w - 24, h - 16), _scratch);
    }

    public void Present()
    {
        _presentRects.Clear();
        double area = 0;
        foreach (var r in _frameDirty)
        {
            int l = (int)MathF.Floor(MathF.Max(0, r.MinX - OriginX));
            int t = (int)MathF.Floor(MathF.Max(0, r.MinY - OriginY));
            int rr = (int)MathF.Ceiling(MathF.Min(Width, r.MaxX - OriginX));
            int b = (int)MathF.Ceiling(MathF.Min(Height, r.MaxY - OriginY));
            if (rr <= l || b <= t) continue;
            _presentRects.Add(new Vortice.RawRect(l, t, rr, b));
            area += (long)(rr - l) * (b - t);
        }

        LastPresentRectCount = _presentRects.Count;
        LastPresentAreaPercent = Width > 0 && Height > 0
            ? area / (Width * (double)Height) * 100.0
            : 100.0;

        var sw = Stopwatch.StartNew();
        SharpGen.Runtime.Result hr;

        // 用等待对象先"排队"：它发信号时才是提交下一帧的正确时机。
        // 这样 Present 用 syncInterval=0 立即返回，不必让驱动在垂直同步里忙等。
        bool useWaitable = _latencyWait != IntPtr.Zero;
        if (useWaitable)
            Native.WaitForSingleObjectEx(_latencyWait, 100, true);

        // 脏区太少或太大都不划算：太大不如直接整屏上屏，太少说明这一帧
        // 没什么变化（例如只等垂直同步）。
        if (_presentRects.Count == 0 || _presentRects.Count > 16 || LastPresentAreaPercent > 80.0)
        {
            hr = _swapChain.Present(useWaitable ? 0u : 1u, PresentFlags.None);
            LastPresentAreaPercent = 100.0;
            LastPresentRectCount = 0;
        }
        else
        {
            hr = _swapChain.Present1(useWaitable ? 0u : 1u, PresentFlags.None,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_presentRects), null, null);
        }

        sw.Stop();
        LastPresentMs = sw.Elapsed.TotalMilliseconds;
        if (hr.Failure) LastError = "Present: " + hr.Description;
    }

    public void Dispose()
    {
        _brushes.Clear();
        _scratch?.Dispose();
        _contentSource?.Dispose();
        _contentTarget?.Dispose();
        _contentTexture?.Dispose();
        _backBuffer?.Dispose();
        _ctx?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _swapChain?.Dispose();
        _swapChain2?.Dispose();
        if (_latencyWait != IntPtr.Zero) { Native.CloseHandle(_latencyWait); _latencyWait = IntPtr.Zero; }
        if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }
}
