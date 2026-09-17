using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
        Mem.Stage("3. 创建 DirectWrite（文字）之后");
    }

    public static void Shutdown()
    {
        RoundStroke?.Dispose();
        RoundStroke = null;
        WriteFactory?.Dispose();
        D2DDevice?.Dispose();
        D2DFactory?.Dispose();
        Factory?.Dispose();
        Device?.Dispose();
    }

    /// <summary>
    /// 把 D3D/DXGI **内部**的缓存缓冲还给系统（<c>IDXGIDevice3::Trim</c>）。
    ///
    /// 为什么需要它：显卡驱动会把一批内部缓冲留着复用（提速后续绘制），
    /// 这些缓冲算在我们的占用里。实测一万笔之后显存 62 → 202 MB，
    /// 把笔画全删掉、几何全部释放之后**仍然停在 201 MB**——就是这些内部缓冲。
    /// Trim 让运行时和驱动把这些丢掉；文档明确说它"不改变渲染状态、
    /// 不影响绘制结果"，代价是之后第一帧要重新分配（有一次性能回落），
    /// 所以**只在空闲时调**（大删除之后、或者停手一会儿）。
    /// </summary>
    public static bool TrimVideoMemory()
    {
        try
        {
            using var dxgiDevice = Device.QueryInterface<IDXGIDevice3>();
            dxgiDevice.Trim();
            return true;
        }
        catch { return false; }
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
    public static long RebuildCount;

    private ID2D1DeviceContext1 _ctx1;

    /// <summary>帧号（跨窗口共享）：给笔画盖"最近被画过"的时间戳，用来淘汰冷掉的细分缓存。</summary>
    private static long s_frameNo;

    /// <summary>
    /// 画布坐标 → 窗口坐标。**相机在这里生效**：渲染的每一处变换都要用它，
    /// 漏掉任何一处，那一处的东西就不会跟着滚动（典型症状：笔迹滚了、
    /// 性能面板没滚，或者反过来）。
    /// </summary>
    private Matrix3x2 CanvasToWindow =>
        Matrix3x2.CreateTranslation(-OriginX + ViewOffsetX, -OriginY + ViewOffsetY);

    /// <summary>
    /// 当前**看得见的那块画布**（画布坐标）。
    ///
    /// 凡是"拿窗口矩形去筛笔画"的地方都必须用它，而不是裸用 OriginX/OriginY——
    /// 相机偏移为 0 时两者恰好相等，一滚动就不等。
    ///
    /// **这个 bug 实测踩过**：滚到 -1800 之后写的那一笔，画布坐标是 y=2500，
    /// 而筛选用的是 [0,1800]，于是它被当成"不在视野里"跳过了，屏幕上一个像素
    /// 都没有——表现就是"滚下去写的字看不见，滚回顶部又一切正常"。
    /// </summary>
    private RectF VisibleCanvasRect => new()
    {
        MinX = OriginX - ViewOffsetX,
        MinY = OriginY - ViewOffsetY,
        MaxX = OriginX - ViewOffsetX + Width,
        MaxY = OriginY - ViewOffsetY + Height,
    };

    /// <summary>相机偏移（由引擎每帧写进来；见 InkEngine.ViewOffsetY）。</summary>
    internal float ViewOffsetX, ViewOffsetY;
    public IntPtr Hwnd;
    public int OriginX, OriginY, Width, Height;
    public uint Dpi = 96;

    /// <summary>
    /// 滚动条的几何（**屏幕坐标**，和 OriginX/Width 同一套）。
    /// 画和命中判定共用一份，避免"对了绘制、错了拖动"这种一半对一半错的状态。
    /// </summary>
    internal struct ScrollBarLayout
    {
        /// <summary>滑块中心线（那条细线的位置）。</summary>
        public float AxisX;
        /// <summary>轨道上下端。</summary>
        public float Top, Bottom;
        /// <summary>滑块顶边、长度，以及可移动的距离。</summary>
        public float ThumbTop, ThumbLen, MaxTravel;
        /// <summary>画布范围（比例换算要用）。</summary>
        public float ExtentMinY, ExtentH;

        /// <summary>
        /// 指针是否落在"可抓"的范围里。宽度按 grabLogical 放宽（滑块只有 4 像素宽，
        /// 按 4 像素判定等于点不中），上下各留一点余量，方便一次抓住。
        /// </summary>
        public bool HitTest(float screenX, float screenY, float grabLogical, float dpi)
            => MathF.Abs(screenX - AxisX) <= grabLogical * dpi * 0.5f
            && screenY >= Top - 6f * dpi && screenY <= Bottom + 6f * dpi;
    }

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
    /// 保留这段代码（默认关闭、已没有命令行开关），供将来换设备/换驱动时复测。
    /// 2026-09-14：`--latencywait` / `--vblankpace` / `--framestats` / `--bufcount`
    /// 这几个**只用于对照实验**的开关已删除，结论留在 延时-实测与优化.md。
    /// </summary>
    public static bool LatencyWaitEnabled = false;
    private IDCompositionDevice _dcomp;
    private IDCompositionTarget _target;
    private IDCompositionVisual _visual;
    private IDCompositionInkTrailDevice _inkTrailDevice;
    private IDCompositionDelegatedInkTrail _inkTrail;
    private ID2D1DeviceContext _ctx;
    private ID2D1Bitmap1 _backBuffer;

    // ---- 内容层（画布空间分块缓存）---------------------------------------
    // 内容层**不再是一张绑在屏幕上的位图**，而是画布空间里的一格一格小位图。
    // 这是"滚动不重画"和"坐标不会忘换算"的根。见 CanvasTiles.cs 顶部的说明。
    private CanvasTileCache _tiles;
    /// <summary>上一次把文档脏区同步进分块缓存时的文档版本号。</summary>
    private int _tilesVersion = -1;
    /// <summary>上一帧的相机偏移。变了 → 整屏合成位置都变了，后缓冲整块作废。</summary>
    private float _lastCamY = float.NaN;
    private bool _lastBoardOn;
    private Color4 _lastBoardColor = new(0f, 0f, 0f, -1f);
    /// <summary>当前帧的引擎引用：光栅化分块时要用文档和底色。</summary>
    private InkEngine _app;

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

    /// <summary>
    /// 旋转度数标签用的文字格式。**按 DPI 生成**：绘制时的变换只有平移，
    /// 字号写死 15 就等于"15 物理像素"，200% 缩放下会小一半。
    /// </summary>
    private IDWriteTextFormat _readoutFormat;
    private float _readoutFormatPx;

    // 性能面板：一帧一张缓存位图，文字变了才重画。
    private ID2D1Bitmap1 _hudTarget, _hudSource;
    private ID3D11Texture2D _hudBmpTex;
    private IDWriteTextFormat _hudFormat;
    private float _hudFormatPx;
    private int _hudBmpW, _hudBmpH;
    private string _hudCacheText;

    /// <summary>脏区要回溯几帧的临时图元。双缓冲下必须 ≥2，否则会出残影；
    /// 仅用于对照实验，正常运行不要改。</summary>
    public static int TransientHistoryFrames = 2;
    public int LastPresentRectCount;
    public double LastPresentAreaPercent;
    /// <summary>
    /// 性能面板的尺寸（**逻辑**像素，按 DPI 放大成物理像素）。
    ///
    /// 老版本是写死的 1000×262 物理像素 + 15 号字：在 200% 缩放的屏上，
    /// 15 物理像素只有 7.5 逻辑像素高——投影上根本看不清（用户原话：看不清）。
    /// 字号必须跟着 DPI 走，这才是"看得清"的根本原因，不是把框放大就行。
    /// </summary>
    public const float HudWidthLogical = 700f;
    public const float HudHeightLogical = 258f;
    public const float HudFontLogical = 17f;
    public const float HudMarginLogical = 12f;
    public const float HudPadLogical = 10f;

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

    public double LastRebuildMs;
    /// <summary>上一帧性能面板自己的代价（重排 + 贴图）。面板的代价也要能被质疑。</summary>
    public double LastHudMs;
    /// <summary>
    /// 面板**重排一次**的耗时（只有文字变了才重排，其余帧是 0）。
    /// 分开报的原因：摊到每帧的均值会把"4 Hz 重排一次"这件事藏起来，
    /// 而"面板比笔迹还贵"这种印象必须能被解释清楚。
    /// </summary>
    public double LastHudRedrawMs;
    /// <summary>累计重排次数（调用方用它算"每秒重排几次"）。</summary>
    public long HudRedraws;
    /// <summary>上一帧走"只补画"路径的块数（诊断）。</summary>
    public int LastAppendedTiles;
    /// <summary>累计"补画"块数（诊断）。</summary>
    public long TotalAppendedTiles;
    /// <summary>上一帧有多少条新笔画没能走补画路径（诊断）。</summary>
    public int LastAppendMissed;
    public double LastAppendMs;
    public double LastPatchMs;
    public int LastPatchCount;
    public double LastRecordMs;
    public double LastPresentMs;

    // ---- 延时探针 --------------------------------------------------------
    // Present 前后的 QPC 时标，以及 DXGI 报告的上屏时刻。见 Latency.cs 的说明：
    // 前两个是精确值，"上屏时刻"带 ±1 帧不确定度（窗口化合成交换链上，
    // GetFrameStatistics 给的是"最近一次真正上屏的帧"，不一定正好是这一帧）。
    public ulong LastPresentStartQpc;
    public ulong LastPresentEndQpc;
    public ulong LastDisplayQpc;
    public bool LastFrameStatsOk;
    public ulong LastPresentCount;
    /// <summary>刷新周期（毫秒）。来自 GetFrameStatistics 的刷新计数差，拿不到时按 60Hz 估。</summary>
    public double RefreshPeriodMs = 1000.0 / 60.0;

    /// <summary>交换链的后缓冲数量：2 是最低延时的常规选择。</summary>
    public static int BufferCount = 2;

    /// <summary>
    /// 每帧在渲染之前先 DwmFlush，等到合成边界再抽输入、提交。
    /// 见 Native.DwmFlush 的说明，以及 README 里延时那一节。
    /// </summary>
    public static bool VBlankPaced;

    public int LastDrawnStrokes;
    /// <summary>常驻分块数 / 这一帧可见块数 / 分块预算（诊断用）。</summary>
    public int LastTileCount, LastTileVisible, LastTileBudget;
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

    /// <summary>
    /// 把一批真实点 + 一批**预测点**交给系统合成器（都用屏幕坐标）。
    ///
    /// 预测点是"这一帧之后笔尖大概在哪"（见 Prediction/InkPredictor.cs）。系统会把轨迹画到
    /// 预测点上；下一条消息来了、真实点补上，预测段自然被覆盖——所以这条通道**猜错了也只是
    /// 屏幕上短暂的一小截**，不会进文档。微软自己笔迹"跟手"的关键就在这个带预测的重载上，
    /// 而绑定我们本来就有（Vortice.DirectComposition 3.8.3）。
    /// </summary>
    public void AddInkTrailPoints(Vector2[] real, int realCount, Vector2[] predicted, int predictedCount, float radius)
    {
        if (!_trailActive || _inkTrail == null || real == null || realCount <= 0) return;
        try
        {
            float r = MathF.Max(0.5f, radius);
            var realPts = new DCompositionInkTrailPoint[realCount];
            for (int i = 0; i < realCount; i++)
                realPts[i] = new DCompositionInkTrailPoint
                {
                    X = real[i].X - OriginX,
                    Y = real[i].Y - OriginY,
                    Radius = r,
                };

            var predPts = Array.Empty<DCompositionInkTrailPoint>();
            if (predicted != null && predictedCount > 0)
            {
                predPts = new DCompositionInkTrailPoint[predictedCount];
                for (int i = 0; i < predictedCount; i++)
                    predPts[i] = new DCompositionInkTrailPoint
                    {
                        X = predicted[i].X - OriginX,
                        Y = predicted[i].Y - OriginY,
                        Radius = r,
                    };
            }

            _trailGeneration = _inkTrail.AddTrailPointsWithPrediction(
                realPts, (uint)realCount, predPts, (uint)predPts.Length);
            InkTrailDebug = $"AddTrailPointsWithPrediction 真实 {realCount} + 预测 {predPts.Length}，gen={_trailGeneration}";
        }
        catch (Exception ex)
        {
            // 带预测的重载万一不被支持，退回不带预测的老路——宁可少一点跟手，也不能丢湿墨。
            InkTrailDebug = "AddTrailPointsWithPrediction 异常: " + ex.Message;
            try
            {
                var p = new DCompositionInkTrailPoint
                {
                    X = real[realCount - 1].X - OriginX,
                    Y = real[realCount - 1].Y - OriginY,
                    Radius = MathF.Max(0.5f, radius),
                };
                _trailGeneration = _inkTrail.AddTrailPoints(new[] { p }, 1);
            }
            catch { _trailActive = false; }
        }
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

        Hwnd = Native.CreateWindowEx(exStyle, className, "InkTeach",
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
            (uint)Math.Max(2, BufferCount),
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

        // 多线程光栅（官方文档：把路径几何的渲染摊到多个逻辑核上）。
        // 当年做过 A/B（--mtraster），结论是收益落在噪声里，于是删掉开关、保持默认关。
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

        // 内容层：画布空间的分块光栅缓存。**不在这里分配位图**——块按需创建，
        // 总量由 CanvasTileCache.BudgetTiles 封顶（默认 48MB）。
        _tiles = new CanvasTileCache();
        _tiles.Attach(_ctx);

        _scratch = _ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f), null);
        _transparentBrush = _ctx.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0f), null);
        _boardBrush = _ctx.CreateSolidColorBrush(Transparent, null);
        Mem.Stage("6. 创建离屏内容层之后");
    }

    /// <summary>
    /// 白板模式下画"页界线"：一屏一页，界线落在画布坐标的整数屏位置。
    ///
    /// 为什么把它画成**画布内容**而不是每帧叠一层：界线在图上的位置是固定的
    /// （不随相机动），所以能跟白板底色一起烘进分块缓存 —— 滚动和翻页都不额外花钱。
    /// 颜色按板色明暗挑：浅板画淡黑线、深板画淡白线，只求"看得出有个分界"，不抢板书。
    /// </summary>
    private void DrawPageLines(InkEngine app, RectF canvas)
    {
        float h = app.PageHeightCanvas;
        if (h < 50f) return;
        float top = app.PageTopCanvas;

        float first = MathF.Floor((canvas.MinY - top) / h) * h + top;
        var c = app.BoardColor;
        float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        var line = lum > 0.5f
            ? new Color4(0f, 0f, 0f, 0.10f)
            : new Color4(1f, 1f, 1f, 0.14f);

        for (float y = first; y <= canvas.MaxY + 0.5f; y += h)
        {
            if (y < canvas.MinY - 0.5f) continue;
            _ctx.DrawLine(new System.Numerics.Vector2(canvas.MinX, y),
                          new System.Numerics.Vector2(canvas.MaxX, y), Brush(line), 1f);
        }
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

    /// <summary>
    /// 把文档的最新状态同步进分块缓存，并把"这一帧要看的新块"光栅化出来。
    ///
    /// 三步，顺序固定：
    ///   ① 文档变了 → 把脏区碰到的块标脏（全是**画布坐标**，与相机无关）
    ///   ② 相机/底色变了 → 块内容一个像素都不用重画，只是合成位置或底变了
    ///   ③ 同步可见块：新露出来的（或脏的）就地光栅化，然后按预算淘汰旧块
    ///
    /// **滚动只走第 ② 步和第 ③ 步里的"没有新块"分支**，所以滚一格的代价
    /// 与文档总量无关——这是分块相对老做法的根本差别。
    /// </summary>
    private void SyncTiles(InkEngine app)
    {
        var doc = app.Doc;

        if (_tilesVersion != doc.Version)
        {
            if (doc.Dirty.Full)
            {
                _tiles.MarkAllDirty();
                _forceFullFrame = true;
            }
            else if (doc.StructureChangedSinceRender)
            {
                // 结构变了（删 / 移 / 撤销 / 清空）：块里"只差几条新笔画"的前提没了，
                // 挂着的补画清单一律作废，改成整块重画。
                _tiles.FlushAppendsAsDirty();
                foreach (var r in doc.Dirty.Rects) _tiles.MarkDirty(r);
            }
            else
            {
                // 只增不减：交给"补画"路径——把新笔画记到它覆盖的块上，
                // 不清空整块。这是"同一页墨迹很多还在写"不卡的关键。
                // 注意：这时的脏区全部来自新笔画本身，所以不必再 MarkDirty。
                int missed = 0;
                foreach (var s in doc.AppendedSinceRender)
                    if (!_tiles.MarkAppend(s)) missed++;
                LastAppendMissed = missed;
            }
            _tilesVersion = doc.Version;
        }

        // 相机变了：**分块一律不动**，只是贴的位置变了。后缓冲里躺着的是
        // 滚动前的画面，整块都不可信，所以这一帧整屏重合成。
        if (ViewOffsetY != _lastCamY)
        {
            _lastCamY = ViewOffsetY;
            _forceFullFrame = true;
        }

        // 底色是**画进分块里**的（透明批注 = 擦成全透明，白板 = 铺底色），
        // 所以换底色等于所有块都过期。
        if (app.BoardOn != _lastBoardOn || !app.BoardColor.Equals(_lastBoardColor))
        {
            _lastBoardOn = app.BoardOn;
            _lastBoardColor = app.BoardColor;
            _tiles.MarkAllDirty();
            _forceFullFrame = true;
        }

        _tiles.Sync(VisibleCanvasRect, RasterizeTile);

        RebuildCount += _tiles.RasterizedLastFrame;
        LastRebuildMs = _tiles.RasterMsLastFrame;
        LastAppendedTiles = _tiles.AppendedLastFrame;
        TotalAppendedTiles += _tiles.AppendedLastFrame;
        LastPatchMs = _tiles.RasterMsLastFrame;
        LastPatchCount = _tiles.RasterizedLastFrame;
        LastDrawnStrokes = _tiles.StrokesLastFrame;
        LastTileCount = _tiles.Count;
        LastTileVisible = _tiles.VisibleCount;
        LastTileBudget = _tiles.BudgetTiles > 0
            ? Math.Max(_tiles.BudgetTiles, _tiles.VisibleCount + CanvasTileCache.ScrollBackMargin)
            : _tiles.VisibleCount + CanvasTileCache.ScrollBackMargin;
    }

    /// <summary>
    /// 光栅化一块：把与这块画布矩形相交的笔画画进它的纹理。
    ///
    /// **这一层唯一的坐标换算就是"减块原点"**，相机不参与——相机只出现在
    /// <see cref="CompositeTiles"/> 那一步。老做法里"脏区、裁剪、快路径"
    /// 各要记得换算一次，漏一处就出残影；现在想漏也没地方漏。
    ///
    /// 块纹理正好是块的大小，所以画出界的部分会被渲染目标自己裁掉，
    /// 不需要额外的裁剪矩形（这也顺带避免了抗锯齿接缝）。
    /// </summary>
    private int RasterizeTile(ID2D1Bitmap1 target, RectF canvas, List<Stroke> onlyThese)
    {
        var app = _app;
        var doc = app.Doc;

        _ctx.Target = target;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.CreateTranslation(-canvas.MinX, -canvas.MinY);

        // 只补画新增的笔画：不清空、不遍历块内原有的笔画。
        // 内容层"只增不减"（正在写字）时才走这条路，见 SyncTiles 里的判断。
        if (onlyThese != null)
        {
            foreach (var s in onlyThese)
            {
                // 被摘出去的那一批（拖动预览）不画进内容层：它们此刻由浮动层画，
                // 位置是每帧都在变的实时变换。见 InkEngine.DetachForDrag。
                if (app.IsContentDetached(s)) continue;
                DrawStroke(s);
            }
            _ctx.Transform = Matrix3x2.Identity;
            var hrAppend = _ctx.EndDraw();
            _ctx.Target = null;
            if (hrAppend.Failure) LastError = "tile append EndDraw: " + hrAppend.Description;
            return onlyThese.Count;
        }

        // Clear() 无视裁剪，用 Copy 混合的填充来"擦"这一块（老规矩）。
        //
        // **矩形要写画布坐标，不能写 (0,0,TileSize,TileSize)**：当前的变换是
        // "画布 → 块内"，直接给块内坐标会被再减一次块原点，整块擦到画面外去，
        // 结果就是"内容确实重画了，但旧墨没被擦掉"——擦除后屏幕上留着鬼影。
        // （实测踩过：橡皮擦掉了数据，屏幕上三条线还在。）
        var clearRect = new Vortice.RawRectF(
            canvas.MinX, canvas.MinY, canvas.MaxX, canvas.MaxY);
        _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        _ctx.FillRectangle(clearRect, BoardBrush(app));
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

        // 白板模式：画"页界线"（一屏一页）。它是**画布内容**——固定在图上的位置、
        // 不随相机动，所以烘进分块缓存里，滚动与翻页都不额外花钱。
        if (app.BoardOn) DrawPageLines(app, canvas);

        // 空间索引按**带笔宽外扩**的框返回候选，所以跨在块边界上的粗笔画
        // 两边都会被画到，不会出现"贴边被削掉一半"的缺口。
        doc.QueryGrid(canvas, _strokeScratch);
        int drawn = 0;
        foreach (var s in _strokeScratch)
        {
            if (!s.PaddedBounds.Intersects(canvas)) continue;
            if (app.IsContentDetached(s)) continue;      // 同上：拖动预览那一批不进内容层
            DrawStroke(s);
            drawn++;
        }

        _ctx.Transform = Matrix3x2.Identity;
        var hr = _ctx.EndDraw();
        _ctx.Target = null;

        if (hr.Failure) LastError = "tile EndDraw: " + hr.Description;
        return drawn;
    }

    /// <summary>
    /// 把可见分块贴到后缓冲上这一块区域里。
    ///
    /// **相机在整个内容层里只出现在这里**：窗口矩形 → 画布矩形，取每块相交的
    /// 子矩形，贴回窗口上对应的位置。块在画布空间里待着不动，所以"滚动"在
    /// 这里是一堆**子矩形拷贝**——和 Win32 的 ScrollWindowEx、浏览器合成器
    /// 滚动图层是同一个手法：搬的是已经画好的像素，不是重新画。
    ///
    /// 必须是子矩形而不是整块：写字时脏区只有一小块，整块贴就等于每帧多搬
    /// 几十万像素。
    /// </summary>
    private void CompositeTiles(in RectF windowRect)
    {
        // 窗口 → 画布（CanvasToWindow 的逆）：c = w + Origin - ViewOffset
        var canvasRect = new RectF
        {
            MinX = windowRect.MinX + OriginX - ViewOffsetX,
            MinY = windowRect.MinY + OriginY - ViewOffsetY,
            MaxX = windowRect.MaxX + OriginX - ViewOffsetX,
            MaxY = windowRect.MaxY + OriginY - ViewOffsetY,
        };

        foreach (var tile in _tiles.Visible)
        {
            var tr = CanvasTileCache.RectOf(tile.Tx, tile.Ty);

            float l = MathF.Max(tr.MinX, canvasRect.MinX);
            float t = MathF.Max(tr.MinY, canvasRect.MinY);
            float r = MathF.Min(tr.MaxX, canvasRect.MaxX);
            float b = MathF.Min(tr.MaxY, canvasRect.MaxY);
            if (r <= l || b <= t) continue;

            var src = new Vortice.RawRectF(l - tr.MinX, t - tr.MinY, r - tr.MinX, b - tr.MinY);
            var dst = new Vortice.RawRectF(
                l - canvasRect.MinX + windowRect.MinX, t - canvasRect.MinY + windowRect.MinY,
                r - canvasRect.MinX + windowRect.MinX, b - canvasRect.MinY + windowRect.MinY);

            // NearestNeighbor：画布和窗口是 1:1，不需要插值；用线性过滤反而
            // 会在块边界上把邻居的像素混进来（半透明笔迹会糊出淡边）。
            _ctx.DrawBitmap(tile.Source, dst, 1f,
                Vortice.Direct2D1.InterpolationMode.NearestNeighbor, src, null);
        }
    }

    /// <summary>
    /// 画布矩形 → 窗口矩形。
    ///
    /// **凡是来自文档的矩形（脏区、笔画包围盒）在送进 ClipToWindow /
    /// PushAxisAlignedClip 之前都必须过这一步**——那些函数吃的是窗口坐标。
    /// 相机为 0 时两者恰好相等，一滚动就全错，症状是"滚完写不了字"
    /// 或者"写了看不见"。
    ///
    /// 这个坑踩过两次：第一次修的是"整层重建"那条路（RebuildAll），
    /// 但真正写字走的是"追加一笔"的快路径（DrawOnlyPatch）——没修到。
    /// </summary>
    private RectF CanvasRectToWindow(in RectF r) => new()
    {
        MinX = r.MinX + ViewOffsetX - OriginX, MinY = r.MinY + ViewOffsetY - OriginY,
        MaxX = r.MaxX + ViewOffsetX - OriginX, MaxY = r.MaxY + ViewOffsetY - OriginY,
    };
    private RectF ClipToWindow(RectF r)
    {
        float l = MathF.Max(r.MinX, OriginX);
        float t = MathF.Max(r.MinY, OriginY);
        float rr = MathF.Min(r.MaxX, OriginX + Width);
        float b = MathF.Min(r.MaxY, OriginY + Height);
        if (rr < l || b < t) return RectF.Empty;
        return new RectF { MinX = l, MinY = t, MaxX = rr, MaxY = b };
    }

    /// <summary>
    /// 旋转度数标签的矩形（**画布坐标**）。抽成独立函数，是因为有两处必须
    /// 用**同一套几何**：画它，以及把它算进每帧脏区。少算一处，屏幕上就会
    /// 留一块擦不掉的残影（选中框那一套 UI 已经踩过这个坑）。
    ///
    /// 位置贴在旋转手柄外侧（跟着手柄转，和 Figma 一样），并且夹在当前可见
    /// 画布范围内——选区贴到屏幕边上的时候，标签不会跑到屏幕外面去。
    /// </summary>
    /// <summary>
    /// 度数标签的盒子**宽度跟着文案走**：角度不设上限之后会出现 "-1234°" 这种长数字，
    /// 写死 64 逻辑像素会把字裁掉（D2D 画在固定矩形里，超出部分直接不见）。
    /// 用和绘制同一个文字格式量一遍宽度——**量完要留着那点余量**，
    /// 所以宽度 = 文字宽 + 两侧内边距。
    /// </summary>
    private RectF RotationReadoutRect(in SelectionFrame frame, float dpi, string text)
    {
        const float minWidthLogical = 64f, heightLogical = 30f;
        float widthLogical = minWidthLogical;
        if (!string.IsNullOrEmpty(text))
        {
            // 量一次就够了：标签只在拖动中出现，每帧一次测量对帧率没有影响
            // （比"按字数估宽"可靠——中英文、正负号、度数符号宽度都不一样）。
            float textW = MeasureTextWidth(text, ReadoutFormat(dpi));
            widthLogical = MathF.Max(minWidthLogical, textW / dpi + 24f);
        }
        var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
        float w = widthLogical * dpi, h = heightLogical * dpi;
        float gap = (SelectionHandles.RotateGripLogical * 0.5f + 9f) * dpi;

        float cx = rot.X, cy = rot.Y - gap - h * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };

        var vis = VisibleCanvasRect;
        float pad = 4f * dpi;
        if (r.MinX < vis.MinX + pad) { r.MaxX += vis.MinX + pad - r.MinX; r.MinX = vis.MinX + pad; }
        if (r.MaxX > vis.MaxX - pad) { r.MinX -= r.MaxX - (vis.MaxX - pad); r.MaxX = vis.MaxX - pad; }
        if (r.MinY < vis.MinY + pad) { r.MaxY += vis.MinY + pad - r.MinY; r.MinY = vis.MinY + pad; }
        if (r.MaxY > vis.MaxY - pad) { r.MinY -= r.MaxY - (vis.MaxY - pad); r.MaxY = vis.MaxY - pad; }
        return r;
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
        if (!app.UiVisibleNow)
        {
            _uiLogicalBounds = RectF.Empty;
            _uiBounds = RectF.Empty;
            return false;
        }

        float dpiScale = Dpi / 96f;

        // 界面看到的"屏幕"是**整块虚拟桌面**，和 `IUiHost.Screen` 是同一个东西。
        //
        // 以前这里给的是**本窗口那一块显示器**的矩形（OriginX/Width 是覆盖窗口自己的）：
        // 同一个概念两个答案，界面按哪个算都会有一个是错的——副屏上"贴右下角"
        // 立刻偏一块。多显示器下每个覆盖窗口都会用这个值调一次 Layout，
        // 现在它们传的是同一个矩形，界面那边就是幂等的。
        //
        // 注：这里的逻辑尺寸仍用全局 DpiScale（第一块屏）。副屏 DPI 不同的完整支持
        // 是二期（难点 4），届时改成按窗口取。
        var uiScreen = app.LogicalVirtualScreen;

        // 界面只在布局会变的时候调（尺寸/DPI 变化）。正常每帧都走缓存。
        if (_uiLayoutBounds.IsEmpty || _uiLayoutDpi != dpiScale
            || !_uiLayoutScreen.Equals(uiScreen))
        {
            _uiLayoutBounds = app.UiLayoutNow(uiScreen, dpiScale);
        _uiLayoutScreen = uiScreen;
        _uiLayoutDpi = dpiScale;
        }

        // 占用矩形每帧都问一次：悬浮条被拖动、展开调色板、折叠收起、暂时消失，
        // 都靠这个方法告诉引擎；否则命中测试和脏区会一直停在旧位置。
        var live = app.UiQueryBoundsNow();
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
    /// 换界面时必须把它清掉：不然新界面**永远不会被调 Layout**（屏幕和 DPI 都没变，
    /// 缓存看起来还有效），它的 QueryBounds 会一直返回空——
    /// 表现就是"换上去的界面看不见、也点不到"。
    /// </summary>
    internal void InvalidateUiLayout()
    {
        _uiLayoutScreen = RectF.Empty;
        _uiLayoutDpi = -1f;
        _uiLayoutBounds = RectF.Empty;
    }

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
        //
        // **这里绝对不能带相机**：界面是贴在屏幕上的工具条，画布滚它不动。
        // 以前这里用的是 CanvasToWindow（含 ViewOffsetY），相机为 0 时看不出
        // 问题，一滚动整个界面就会跟着内容往上跑——正是"坐标换算漏一处"
        // 那一类 bug 的又一例。
        _ctx.SetDpi(96f, 96f);
        _ctx.Transform = Matrix3x2.CreateScale(dpiScale)
                       * Matrix3x2.CreateTranslation(-OriginX, -OriginY);
        // 裁剪矩形同样用逻辑坐标（会被上面的变换一起作用）。
        var clip = new Vortice.RawRectF(_uiLogicalBounds.MinX, _uiLogicalBounds.MinY,
                                        _uiLogicalBounds.MaxX, _uiLogicalBounds.MaxY);
        _ctx.PushAxisAlignedClip(clip, AntialiasMode.Aliased);
        // 防弹入口在引擎那边（`UiRenderNow`）：界面连抛三次就整体停用，笔迹照常。
        app.UiRenderNow(_ctx, UiTheme.Default);
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.PopAxisAlignedClip();
    }

    /// <summary>供分辨率实测复用同一套绘制路径。</summary>
    public void DrawStrokeForTest(Stroke s) => DrawStroke(s);

    /// <summary>
    /// 把一批对象画到**离屏位图**上并读回 BGRA（复制到剪贴板时给外部程序那张图用）。
    ///
    /// 背景是**全透明**——粘到别处时不该带上我们的白底或桌面。
    /// <paramref name="region"/> 是这批对象在画布坐标里的范围（调用方算好，含一点留白），
    /// 位图尺寸 = 它的像素尺寸（画布坐标就是物理像素，1:1）。
    /// 返回 null = 建位图/绘制失败（调用方就当"这次没图"，对象格式照放）。
    /// </summary>
    public byte[] RenderStrokesToBgra(IReadOnlyList<Stroke> strokes, RectF region, int w, int h)
    {
        if (_ctx == null || strokes == null || strokes.Count == 0 || w <= 0 || h <= 0) return null;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        ID2D1Bitmap1 target = null, cpu = null;
        try
        {
            target = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            cpu = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            _ctx.Target = target;
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0f, 0f, 0f, 0f));
            _ctx.Transform = Matrix3x2.CreateTranslation(-region.MinX, -region.MinY);
            foreach (var s in strokes) DrawStroke(s);
            _ctx.Transform = Matrix3x2.Identity;
            var hr = _ctx.EndDraw();
            _ctx.Target = null;
            if (hr.Failure) { LastError = "复制到剪贴板的离屏绘制失败: " + hr.Description; return null; }

            // D2D 位图 → CPU 可读位图 → 拷进内存
            cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
            var m = cpu.Map(MapOptions.Read);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
            cpu.Unmap();
            return buf;
        }
        catch (Exception ex)
        {
            LastError = "复制到剪贴板失败: " + ex.Message;
            return null;
        }
        finally
        {
            target?.Dispose();
            cpu?.Dispose();
        }
    }

    /// <summary>
    /// 把**界面**离屏画进一张位图并读回 BGRA（开发期出图用）。
    ///
    /// 为什么单开这条路：平时的出图是"截屏"，那要求屏幕亮着、会话没锁，
    /// 图里还会混进桌面上的东西。这条路把界面画到自己的位图上——
    /// **锁屏 / 远程 / 这台机器上没人看着**的时候照样能出图，以后也便于接 CI。
    ///
    /// 背景用一层浅灰（不是透明）：半透明白面板压在透明底上看不清边界。
    /// </summary>
    public byte[] RenderUiToBgra(InkEngine app, int padPx, out int w, out int h)
    {
        w = h = 0;
        if (_ctx == null || app.Ui == null || !app.UiVisibleNow) return null;

        var bounds = app.UiQueryBoundsNow();
        if (bounds.IsEmpty) return null;

        float dpi = Dpi / 96f;
        w = (int)MathF.Ceiling((bounds.MaxX - bounds.MinX) * dpi) + padPx * 2;
        h = (int)MathF.Ceiling((bounds.MaxY - bounds.MinY) * dpi) + padPx * 2;
        if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return null;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        ID2D1Bitmap1 target = null, cpu = null;
        try
        {
            target = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            cpu = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            _ctx.Target = target;
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0.93f, 0.94f, 0.96f, 1f));
            _ctx.SetDpi(96f, 96f);
            // 和 DrawUi 同一套坐标：先乘 DPI，再把界面左上角挪到留白处
            _ctx.Transform = Matrix3x2.CreateScale(dpi)
                           * Matrix3x2.CreateTranslation(-bounds.MinX * dpi + padPx,
                                                         -bounds.MinY * dpi + padPx);
            app.UiRenderNow(_ctx, UiTheme.Default);
            _ctx.Transform = Matrix3x2.Identity;
            var hr = _ctx.EndDraw();
            _ctx.Target = null;
            if (hr.Failure) { LastError = "界面离屏绘制失败: " + hr.Description; return null; }

            cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
            var m = cpu.Map(MapOptions.Read);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
            cpu.Unmap();
            return buf;
        }
        catch (Exception ex)
        {
            LastError = "界面离屏绘制失败: " + ex.Message;
            return null;
        }
        finally
        {
            target?.Dispose();
            cpu?.Dispose();
        }
    }

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
    private void DrawStroke(Stroke s)
    {
        if (s.Transform.IsIdentity)
        {
            DrawStrokeCore(s);
        }
        else
        {
            // 局部 → 画布（s.Transform），再 画布 → 窗口（调用方设的）。
            // 乘法顺序按 System.Numerics 的约定：先作用左边的。
            var canvasToWindow = _ctx.Transform;
            _ctx.Transform = s.Transform * canvasToWindow;
            DrawStrokeCore(s);
            _ctx.Transform = canvasToWindow;
        }

    }

    private void DrawStrokeCore(Stroke s)
    {
        // 图像对象：画的是位图，不是几何。**必须放在最前面**——它和图形一样
        // 属于"非自由笔迹"，走到下面那条 DrawGeometry 分支就会被描一个矩形边框。
        if (s.IsImage)
        {
            var bmp = s.Image?.GetBitmap(_ctx);
            if (bmp == null) return;
            var a = new Vector2(s.Points[0].X, s.Points[0].Y);
            var b = s.Points.Count > 1
                ? new Vector2(s.Points[^1].X, s.Points[^1].Y)
                : a;
            var dst = new Vortice.RawRectF(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                                           MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
            // 缩放时用线性插值：最近邻在投影上会让文字边缘全是锯齿（放大看更明显）。
            _ctx.DrawBitmap(bmp, dst, 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            return;
        }

        var geo = s.BuildGeometry(Gfx.D2DFactory);
        if (geo == null) return;
        // 两种画法：
        //   · 单点笔迹 → 几何本身就是一个圆，填充它（零长度的线描边什么都画不出来）；
        //   · 其余（笔迹的中心线、直线/矩形/椭圆/箭头）→ 统一交给 D2D 描边：
        //     宽度、圆头端帽、拐角全由它算（2026-09-14 起，我们自己的轮廓代码已删除）。
        if (s.IsSinglePoint) _ctx.FillGeometry(geo, Brush(s.Color));
        else _ctx.DrawGeometry(geo, Brush(s.Color), MathF.Max(1f, s.Width), Gfx.Round);

    }

    // ------------------------------------------------------------------
    //  Frame
    // ------------------------------------------------------------------

    public void RenderFrame(InkEngine app)
    {
        _app = app;
        s_frameNo++;

        // 界面：先布局、该重画就重画一次，拿到这一帧的矩形。
        bool uiVisible = PrepareUi(app);

        // The content layer must be rebuilt *before* the swap-chain target is
        // bound: switching targets in the middle of BeginDraw/EndDraw puts
        // Direct2D into an error state.
        if (!app.NoContentCache)
            SyncTiles(app);

        // 性能面板先画进自己的缓存位图（必须在绑后缓冲、BeginDraw 之前做）。
        var swHud = Stopwatch.StartNew();
        PrepareHud(app);
        LastHudMs = swHud.Elapsed.TotalMilliseconds;
        LastHudRedrawMs = _hudRedrewThisFrame ? LastHudMs : 0;

        _transientNow = ComputeTransientBounds(app);
        UpdateFrameDirty(app, uiVisible);

        var sw = Stopwatch.StartNew();
        _ctx.Target = _backBuffer;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;

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
                _ctx.Transform = CanvasToWindow;
                // 注意：这里要比的是**画布矩形**。以前直接拿窗口矩形去比，
                // 相机为 0 时看不出问题，一滚动就全错（这也是要收进"类型化
                // 坐标"的那一类坑）。
                var cv = new RectF
                {
                    MinX = c.MinX + OriginX - ViewOffsetX,
                    MinY = c.MinY + OriginY - ViewOffsetY,
                    MaxX = c.MaxX + OriginX - ViewOffsetX,
                    MaxY = c.MaxY + OriginY - ViewOffsetY,
                };
                foreach (var s in app.Doc.Strokes)
                {
                    if (!s.PaddedBounds.Intersects(cv)) continue;
                    if (app.IsContentDetached(s)) continue;   // 拖动预览那一批由浮动层画
                    DrawStroke(s);
                }
            }
            else
            {
                // 内容层：把可见分块按相机贴在窗口上（子矩形拷贝，不重画笔迹）
                CompositeTiles(c);
            }

            _ctx.Transform = CanvasToWindow;

            // 调试用：--trailonly 时不画自己那一笔，用来验证委托墨迹轨迹
            // 是不是真的由系统合成器画出来了。
            if (app.ActiveStroke != null && !app.SuppressActiveStroke)
            // 正在写的那一笔几何每帧都在变，用实现缓存只会不停重建，反而更慢
            DrawStroke(app.ActiveStroke);

            DrawDragPreview(app);
            DrawSelection(app);
            DrawCaptureRect(app);
            DrawLaser(app);
            DrawToolCursor(app);
            DrawMarquee(app);

            _ctx.Transform = Matrix3x2.Identity;

        if (app.ShowHud)
        {
            var swBlit = Stopwatch.StartNew();
            DrawHud();
            LastHudMs += swBlit.Elapsed.TotalMilliseconds;
        }

        // 滚动条（样式 B：一根细线）。画在浮动层，不进内容层。
        DrawScrollBar(app);

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
            r.Add(CanvasRectToWindow(app.ActiveStroke.PaddedBounds));

        var laser = app.Laser.Points;
        if (app.Laser.Visible && laser.Count > 0)
        {
            var b = RectF.Empty;
            foreach (var p in laser) b.Add(p.X, p.Y);
            r.Add(CanvasRectToWindow(b.Inflate(32f)));      // 轨迹有宽度和发光，往外留一点
        }

        // 自绘的落点反馈（橡皮圆环 / 笔尖环 / 荧光笔圆盘）。它比图形本身大一圈
        // （描边 + 刻度），脏区要跟着放大，否则快速划过会留下残影。
        //
        // 画布坐标 → 窗口坐标必须过 CanvasRectToWindow：圆环跟着笔迹走，
        // 滚动之后两者才会一致（漏了这一步，滚下去之后圆环就擦不干净）。
        if (app.DrawnCursor != InkEngine.ToolCursorShape.None)
        {
            float rad = app.DrawnCursorRadius;
            var c = RectF.Empty;
            c.Add(app.PointerX - rad, app.PointerY - rad);
            c.Add(app.PointerX + rad, app.PointerY + rad);
            r.Add(CanvasRectToWindow(c));
        }

        if (app.MarqueeActive)
        {
            var m = RectF.Empty;
            m.Add(app.MqMinX, app.MqMinY);
            m.Add(app.MqMaxX, app.MqMaxY);
            m = CanvasRectToWindow(m);       // 框选矩形是画布坐标，脏区要窗口坐标
            r.Add(m.Inflate(3f));
        }

        // 截图取景框：角标比线宽出去一截，多留 4 像素。
        if (app.CaptureActive)
        {
            var m = RectF.Empty;
            m.Add(app.CapMinX, app.CapMinY);
            m.Add(app.CapMaxX, app.CapMaxY);
            r.Add(CanvasRectToWindow(m).Inflate(4f));
        }

        // 选中高亮画在浮动层上、不进内容层，所以它的区域必须每帧算进脏区。
        //
        // 注意**不能只算对象自己的包围盒**：选中框这一套 UI 比对象大——
        // 外侧的光晕、跨在边线上的手柄、以及伸到上边外侧的旋转手柄。
        // 漏掉哪一块，哪一块就会在屏幕上留下擦不掉的残影（实测踩过：
        // 旋转手柄旁边留了一小块红色的前帧残留）。
        if (app.Doc.Selected.Count > 0)
        {
            // 拖动预览期间框是"实时"的（模型还没动，见 LiveSelectionFrame）：
            // 脏区必须跟着它走，否则框和手柄会在新位置上留下擦不掉的残影。
            var frame = app.LiveSelectionFrame;
            var sb = frame.CanvasAabb;
            float dpi = app.DpiScale;
            float margin = SelectionHandles.VisualSizeLogical * 0.5f * dpi + 6f;
            var ui = sb.Inflate(margin);

            var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
            float grip = SelectionHandles.RotateGripLogical * 0.5f * dpi + 3f;
            ui.Add(rot.X - grip, rot.Y - grip);
            ui.Add(rot.X + grip, rot.Y + grip);
            r.Add(CanvasRectToWindow(ui));   // 选中框也是画布坐标（它跟着墨迹走）

            // 操作条在选中框下方，也必须算进来，否则它自己会留下残影。
            r.Add(CanvasRectToWindow(SelectionHandles.BarRect(sb, dpi, app.ViewportCanvas).Inflate(4f)));

            // 收起态的圆钮 / 打开的面板 / 提示条：都是"每帧都在变"的浮动层，
            // 漏一块就在屏幕上留一块擦不掉的残影（这条踩过好几次了）。
            r.Add(CanvasRectToWindow(SelectionHandles.BarCollapsedRect(sb, dpi, app.ViewportCanvas).Inflate(4f)));
            if (app.SelPanelOpen == SelPanel.Ink)
                r.Add(CanvasRectToWindow(SelectionHandles
                    .PanelRect(sb, dpi, app.ViewportCanvas, SelectionHandles.SwatchCount).Inflate(6f)));
            else if (app.SelPanelOpen == SelPanel.Layer)
                r.Add(CanvasRectToWindow(SelectionHandles.LayerPanelRect(sb, dpi, app.ViewportCanvas).Inflate(6f)));

            // 旋转度数标签贴在旋转手柄外侧，比选中框本身还高出去一截，
            // 同样必须进脏区；拖动中它每帧都在动，靠 _transientHistory 回溯两帧。
            if (app.SelRotating)
                r.Add(CanvasRectToWindow(RotationReadoutRect(frame, dpi, RotationLabel(app)).Inflate(3f)));
        }

        if (app.ShowHud)
        {
            // 面板区域每帧都要进脏区：后缓冲里躺着的是两帧前的画面，不重贴就会闪。
            float m = HudMarginLogical * HudScale;
            var h = RectF.Empty;
            h.Add(OriginX + m - 2, OriginY + m - 2);
            h.Add(OriginX + m + HudWidthPx + 2, OriginY + m + HudHeightPx + 2);
            r.Add(h);
        }

        // 滚动条画在右边缘，而且要每帧淡出，所以必须算进脏区，
        // 否则它消失之后会在屏幕上留一条擦不掉的线。
        r.Add(new RectF
        {
            MinX = OriginX + Width - 30f * app.DpiScale,
            MinY = OriginY,
            MaxX = OriginX + Width,
            MaxY = OriginY + Height,
        });

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
            foreach (var raw in doc.Dirty.Rects) _contentDirtyNow.Add(CanvasRectToWindow(raw));

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
    /// 拖动预览：手势中把被"摘出内容层"的那一批按**实时变换**画在最上层。
    ///
    /// 为什么要有这一层：拖动期间模型一个字都不改（所以内容层那些块全部有效、
    /// 一帧都不用重画，最坏帧不再随"走过的面积"涨），实时位移只活在
    /// <see cref="InkEngine.DragPreviewMatrix"/> 里；旧位置则是从内容层贴回来的，
    /// 像素级还原、不会闪。
    ///
    /// 得到的两个副作用，都是想要的：
    ///   · 被拖的那块**自动浮起来**（它画在整层内容之上）——不用去压暗周围；
    ///   · 拖动期间 z 序暂时在最上层（松手回到正确层次，这是"拖动预览"的常规取舍）。
    ///
    /// 画法必须与内容层**像素一致**，否则松手那一下会"跳"：这里左乘的矩阵正是
    /// 内容层会把对象变换成的那个（对象自己的变换 × 实时矩阵），
    /// 自检里有一条"松手前后同一段墨的墨量一致"盯着它。
    /// </summary>
    private void DrawDragPreview(InkEngine app)
    {
        var strokes = app.DragPreviewStrokes;
        if (strokes.Count == 0) return;

        var canvasToWindow = _ctx.Transform;                 // 此刻是 CanvasToWindow
        _ctx.Transform = app.DragPreviewMatrix * canvasToWindow;
        foreach (var s in strokes) DrawStroke(s);
        _ctx.Transform = canvasToWindow;
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

        // 选区坐标系：**一律轴对齐**——不管选了一条还是多条，框都是屏幕上那个正矩形
        // （见 SelectionFrame 的注释）。拖动预览期间取"实时框"：模型还没动，
        // 但框必须跟着内容走，而且是每帧重新贴合当前内容（见 LiveSelectionFrame）。
        var frame = app.LiveSelectionFrame;
        if (frame.IsEmpty) return;
        var b = frame.CanvasAabb;      // 操作条和脏区用它的轴对齐范围

        float dpi = app.DpiScale;
        var accent = new Color4(0f, 0.47f, 0.83f, 1f);      // #0078D4

        // 拖动 / 旋转期间的**装饰收敛**（方案 A）：手柄与操作条此刻点不中
        // （指针已被拖拽接管），留着就是"看得见、点不到"，还跟着内容一起晃。
        //
        // 注意三件事：
        //   · 缩放手势不收（拖手柄时别的柄是参照，见 SelChromeCollapsed）；
        //   · 旋转时**留**旋转柄与度数标签：柄标着"我抓的是这个"，度数是读数；
        //   · 不画不等于不算：这些矩形的脏区照旧由 ComputeTransientBounds 给出，
        //     靠两帧回溯把刚消失的那一块擦干净（这里踩过坑，见那段注释）。
        bool collapsed = app.SelChromeCollapsed;

        // 框的四个角。**按四个角连成四边形画**，而不是 DrawRectangle：
        // 现在框一律轴对齐（ToCanvas 是单位阵），两种画法结果一样，
        // 但这样将来真要加"整组的朝向"时，这里一行都不用改。
        var c0 = SelectionHandles.CanvasPosition(SelHandle.TopLeft, frame, dpi);
        var c1 = SelectionHandles.CanvasPosition(SelHandle.TopRight, frame, dpi);
        var c2 = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, dpi);
        var c3 = SelectionHandles.CanvasPosition(SelHandle.BottomLeft, frame, dpi);

        // 1) 光晕
        _scratch.Color = new Color4(accent.R, accent.G, accent.B, 0.16f);
        DrawQuad(c0, c1, c2, c3, 6.5f);

        // 2) 描边。两种特殊状态在这里体现：
        //    · **复制拖拽模式**：框画成虚线（"按着拖 = 拖出副本"的通用语言）；
        //    · **复制成功那 0.25 秒**：框加粗变亮，闪一下（用户反馈"复制以后看不出来"）。
        if (app.SelFlashing)
        {
            _scratch.Color = new Color4(accent.R, accent.G, accent.B, 1f);
            DrawQuad(c0, c1, c2, c3, 5.5f);
        }
        else
        {
            _scratch.Color = accent;
            if (app.CopyDragArmed) DrawQuadDashed(c0, c1, c2, c3, 2.5f);
            else DrawQuad(c0, c1, c2, c3, 2.5f);
        }

        // 3) 旋转手柄（在上边中点外侧，先画连线再画圆）
        //    拖动中收起来，但**旋转中要留**（此刻它就是"正在抓的那个东西"）。
        var white = Brush(new Color4(1f, 1f, 1f, 1f));
        if (!collapsed || app.SelRotating)
        {
            float rotR = SelectionHandles.RotateGripLogical * 0.5f * dpi;
            var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
            var topCenter = SelectionHandles.CanvasPosition(SelHandle.Top, frame, dpi);
            _ctx.DrawLine(topCenter, rot, _scratch, 1.4f);
            _ctx.FillEllipse(new Ellipse(rot, rotR, rotR), white);
            _ctx.DrawEllipse(new Ellipse(rot, rotR, rotR), _scratch, 1.6f);
            // 圆里放**官方图标**，不是手画一段弧。
            // 手画那版在投影上看像个"©"——旋转图标的识别特征就是那个箭头，
            // 少一笔就不成形。这是"图标别自己画"的又一个实例。
            float glyph = SelectionHandles.RotateGlyphLogical * dpi;
            DrawIcon(IconPaths.rotate, rot.X - glyph * 0.5f, rot.Y - glyph * 0.5f, glyph, _scratch);
        }

        // 4) 八个手柄。白底 + 蓝边：深色背景上是白方块显眼，
        //    浅色背景上靠蓝边立住，一套画法两边都成立。
        //    拖动 / 旋转中收起来（此刻点不中，而且是最"晃眼"的一圈家具）。
        if (!collapsed)
        {
            float hs = SelectionHandles.VisualSizeLogical * dpi;
            float radius = hs * 0.28f;
            Span<SelHandle> all = stackalloc SelHandle[]
            {
                SelHandle.TopLeft, SelHandle.Top, SelHandle.TopRight, SelHandle.Right,
                SelHandle.BottomRight, SelHandle.Bottom, SelHandle.BottomLeft, SelHandle.Left,
            };
            foreach (var h in all)
            {
                var p = SelectionHandles.CanvasPosition(h, frame, dpi);
                var box = new Vortice.RawRectF(p.X - hs * 0.5f, p.Y - hs * 0.5f,
                                               p.X + hs * 0.5f, p.Y + hs * 0.5f);
                var rr = new RoundedRectangle(box, radius, radius);
                _ctx.FillRoundedRectangle(rr, white);
                _ctx.DrawRoundedRectangle(rr, _scratch, 1.8f);
            }
        }

        // 5) 操作条（九格）/ 收起态的圆钮。放在下方，理由见 DrawSelectionBar 的注释。
        //    拖动 / 旋转中收起来：此刻点不中；而且贴着屏幕下边时它会**停在原地**，
        //    内容继续走、条不跟——那是最像卡死的一幕（见 SelectionHandles.BarRect）。
        if (!collapsed)
        {
            if (app.SelBarCollapsed) DrawCollapsedDot(app, b);
            else DrawSelectionBar(app, b);
        }

        // 5.5) 浮动面板（颜色/粗细、层级）。画在条之后，盖在内容之上。
        if (!collapsed && !app.SelBarCollapsed)
        {
            if (app.SelPanelOpen == SelPanel.Ink) DrawInkPanel(app, b);
            else if (app.SelPanelOpen == SelPanel.Layer) DrawLayerPanel(app, b);
        }

        // 6) 旋转度数标签：只在拖旋转手柄的过程中出现。
        //
        // 两件事必须同时说清楚："转了多少度"和"这个角度是不是吸出来的"。
        // 后者靠颜色：吸住时整块变强调色（Figma / Office 也是这个语言）。
        // 没有这层提示，用户分不清"我自己转到了 90°"和"它替我吸到了 90°"。
        if (app.SelRotating)
        {
            // 同一个文案：盒子按它量宽，字也画它。**改一处就得改两处**的地方收成一个函数。
            string readout = RotationLabel(app);
            var label = RotationReadoutRect(frame, dpi, readout);
            var box = new Vortice.RawRectF(label.MinX, label.MinY, label.MaxX, label.MaxY);
            float pill = (label.MaxY - label.MinY) * 0.5f;
            var shape = new RoundedRectangle(box, pill, pill);
            bool snapped = app.SelRotationSnapped;

            _ctx.FillRoundedRectangle(shape, Brush(snapped
                ? new Color4(accent.R, accent.G, accent.B, 0.96f)
                : new Color4(1f, 1f, 1f, 0.94f)));
            _scratch.Color = snapped
                ? new Color4(1f, 1f, 1f, 0.85f)
                : new Color4(accent.R, accent.G, accent.B, 0.85f);
            _ctx.DrawRoundedRectangle(shape, _scratch, 1.5f);

            _scratch.Color = snapped
                ? new Color4(1f, 1f, 1f, 1f)
                : new Color4(0.10f, 0.12f, 0.16f, 1f);
            _ctx.DrawText(readout, ReadoutFormat(dpi),
                          // 注意：Vortice 的 Rect(x, y, width, height) 是"位置 + 尺寸"，
                          // 不是 (left, top, right, bottom)。写错的话文字会被排到很远的
                          // 地方去（居中排版时直接跑到屏幕外），看起来就像"字没画出来"。
                          new Rect(label.MinX, label.MinY,
                                   label.MaxX - label.MinX, label.MaxY - label.MinY),
                          _scratch);
        }
    }

    /// <summary>
    /// 度数标签的文字格式（按 DPI 生成并缓存）。
    ///
    /// 绘制时的变换只有平移、没有缩放，所以字号写死就等于"物理像素"——
    /// 15 物理像素在 200% 缩放下只有 7.5 逻辑像素，投影上根本看不清。
    /// </summary>
    /// <summary>
    /// 用 DirectWrite 量一段文字在给定格式下的宽度。给"盒子要跟着文案变长"的地方用
    /// （度数标签、面板底色）。用同一个格式量，量出来的才和画出来的一致。
    /// </summary>
    /// <summary>度数标签的文案（画它、按它量盒子宽度，都走这一个函数）。</summary>
    private static string RotationLabel(InkEngine app) => SelectionHandles.FormatDegrees(app.SelRotationDegrees);

    private static float MeasureTextWidth(string text, IDWriteTextFormat format)
    {
        if (string.IsNullOrEmpty(text) || format == null) return 0f;
        try
        {
            using var layout = Gfx.WriteFactory.CreateTextLayout(text, format, 4096f, 1024f);
            return layout.Metrics.Width;
        }
        catch { return 0f; }        // 量不出来就退回最小宽度，绝不能因为量个宽度把渲染搞挂
    }

    private IDWriteTextFormat ReadoutFormat(float dpi)
    {
        float px = MathF.Max(11f, MathF.Round(15f * dpi));
        if (_readoutFormat == null || _readoutFormatPx != px)
        {
            _readoutFormat?.Dispose();
            _readoutFormat = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _readoutFormat.TextAlignment = TextAlignment.Center;
            _readoutFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutFormatPx = px;
        }
        return _readoutFormat;
    }

    /// <summary>
    /// 操作条（九格）：收起 / 颜色 / 锁定 / 层级 / 导出 / 复制 / 左右翻转 / 上下翻转 / 删除。
    /// 顺序的语义见 <see cref="SelBarButton"/>；这一层只负责画。
    ///
    /// **为什么在下方而不是上方**：选中内容靠屏幕上边时，上方的操作条要么被顶出
    /// 屏幕、要么盖住正在讲的内容。下方永远有位置，个子矮的老师也够得着。
    ///
    /// 图标不是自己画的：路径数据来自微软 Fluent UI System Icons（MIT），
    /// 由 tools/gen-icons.ps1 抓取生成。手画的圆角和光学比例总是差一口气。
    ///
    /// 三种状态都要画出来（2026-09-16 补的，起因是用户说"复制以后看不出来"）：
    ///   · **悬停**：浅灰圆角底（`SelBarHover`，以前这个字段是个死字段，没人赋值也没人画）；
    ///   · **激活**：浅蓝底 + 图标加深（颜色/层级面板开着、复制模式开着）；
    ///   · **禁用**：图标减到 35%（导出还没接、锁定对图像无意义……）。
    /// </summary>
    private void DrawSelectionBar(InkEngine app, in RectF sel)
    {
        var visible = app.ViewportCanvas;
        float dpi = Dpi / 96f;
        // 布局从 SelectionHandles 取，与命中判定同源：分开写迟早差几个像素，
        // 表现就是"看得见按钮却点不中"。
        var rect = SelectionHandles.BarRect(sel, dpi, visible);
        var box = new Vortice.RawRectF(rect.MinX, rect.MinY, rect.MaxX, rect.MaxY);
        // 圆角**用界面那套令牌**（UiTheme.CornerRadius = 10），不自己发明一个胶囊：
        // 工具条、操作条、面板三样东西的圆角必须是同一个数，看着才是一家的。
        float radius = MathF.Min(UiTheme.Default.CornerRadius * dpi, (rect.MaxY - rect.MinY) * 0.5f);
        var rounded = new RoundedRectangle(box, radius, radius);

        // 配色**取界面那套主题**（UiTheme.Default）：操作条和工具条必须是同一套颜色，
        // 各写一份迟早会花（用户："纯白色也不美观……看看怎么和那个界面搭配起来"）。
        var theme = UiTheme.Default;
        _scratch.Color = theme.Panel;
        _ctx.FillRoundedRectangle(rounded, _scratch);
        _scratch.Color = theme.PanelBorder;
        _ctx.DrawRoundedRectangle(rounded, _scratch, 1f * dpi);

        const int n = SelectionHandles.BarButtonCount;
        float glyphBox = SelectionHandles.BarIconBoxLogical * dpi;   // 图标框比字形大一点

        for (int i = 0; i < n; i++)
        {
            var btn = SelectionHandles.BarButtonRect(i, sel, dpi, visible);
            var kind = (SelBarButton)i;
            bool hot = app.SelBarHover == i;
            bool active = IsBarButtonActive(app, kind);
            bool disabled = kind == SelBarButton.Export;      // 还没接（见 RunBarAction）

            // 悬停 / 激活的底：参考实现就是这么做的（浅色圆角底 + 图标加深）。
            if (hot || active)
            {
                // 激活底比按钮小一圈（内缩 3），圆角接近半个身位——小按钮上
                // 大圆角会显得"胖"，这一圈内缩就是让它看起来利落的关键。
                float inset = 3f * dpi;
                var bg = new Vortice.RawRectF(btn.MinX + inset, btn.MinY + inset,
                                              btn.MaxX - inset, btn.MaxY - inset);
                float br = MathF.Min(bg.Bottom - bg.Top, bg.Right - bg.Left) * 0.32f;
                _scratch.Color = active
                    ? theme.ActiveBg                            // 激活：主题的实心蓝
                    : theme.Hover;                              // 悬停：主题的浅蓝灰
                _ctx.FillRoundedRectangle(new RoundedRectangle(bg, br, br), _scratch);
            }

            var iconBrush = Brush(disabled ? theme.TextMuted
                                     : active ? theme.ActiveText
                                              : theme.Text);
            float cx = (btn.MinX + btn.MaxX) * 0.5f, cy = (btn.MinY + btn.MaxY) * 0.5f;

            switch (kind)
            {
                case SelBarButton.Color:
                    // 圆环的**半径**按图标框的一半左右给（以前给成 0.62 倍，画出来比
                    // 别的图标还大一圈——用户："那个颜色圈的比例也太大了"）。
                    DrawColorRing(app, cx, cy, glyphBox * 0.44f, disabled);
                    break;
                case SelBarButton.Lock:
                    DrawIcon(IsSelectionLocked(app) ? IconPaths.lockClosed : IconPaths.unlock,
                             cx - glyphBox * 0.5f, cy - glyphBox * 0.5f, glyphBox, iconBrush);
                    break;
                default:
                    DrawIcon(IconForBar(kind), cx - glyphBox * 0.5f, cy - glyphBox * 0.5f,
                             glyphBox, iconBrush);
                    break;
            }
        }
    }

    /// <summary>操作条九格各自的图标（颜色那格是自绘的"当前色环"，不在这里）。</summary>
    private static string IconForBar(SelBarButton b) => b switch
    {
        SelBarButton.Collapse => IconPaths.collapse,
        SelBarButton.Layer => IconPaths.layer,
        SelBarButton.Export => IconPaths.export,
        SelBarButton.Copy => IconPaths.copy,
        SelBarButton.FlipH => IconPaths.flipH,
        SelBarButton.FlipV => IconPaths.flipV,
        SelBarButton.Delete => IconPaths.delete,
        _ => IconPaths.layer,
    };

    /// <summary>这一格是"激活"状态吗（面板开着 / 模式开着）。</summary>
    private static bool IsBarButtonActive(InkEngine app, SelBarButton b) => b switch
    {
        SelBarButton.Color => app.SelPanelOpen == SelPanel.Ink,
        SelBarButton.Layer => app.SelPanelOpen == SelPanel.Layer,
        SelBarButton.Copy => app.CopyDragArmed,
        _ => false,
    };

    /// <summary>选中的对象是不是**全都**锁着（决定锁图标画开锁还是闭锁）。</summary>
    private static bool IsSelectionLocked(InkEngine app)
    {
        if (app.Doc.Selected.Count == 0) return false;
        foreach (var s in app.Doc.Selected) if (!s.Locked) return false;
        return true;
    }

    /// <summary>
    /// "颜色"那一格的图标：**当前色的圆环**（参考实现的画法）。
    ///
    /// 一眼能看出"这一批墨现在是什么颜色"；选中的颜色不一致时（混合选区）
    /// 画成**双色环**——左半边第一种色、右半边第二种色，不假装是纯色。
    /// 选中的全是图像对象时画成禁用态（图像没有"颜色"这个概念）。
    /// </summary>
    private void DrawColorRing(InkEngine app, float cx, float cy, float r, bool disabled)
    {
        Color4 a = default; int distinct = 0; Color4 b2 = default;
        bool any = false, anyImage = false;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) { anyImage = true; continue; }
            if (!any) { a = s.Color; any = true; continue; }
            if (distinct == 0 && !SameRgb(a, s.Color)) { b2 = s.Color; distinct = 1; }
            else if (distinct == 1 && !SameRgb(a, s.Color) && !SameRgb(b2, s.Color)) distinct = 2;
        }
        if (!any)                                   // 全是图像对象：画个灰环（禁用态）
        {
            _scratch.Color = new Color4(0.5f, 0.52f, 0.56f, 0.35f);
            _ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), _scratch, 2.6f * Dpi / 96f);
            return;
        }

        float w = 2.2f * Dpi / 96f;
        var half = new Ellipse(new Vector2(cx, cy), r, r);
        _scratch.Color = disabled ? new Color4(a.R, a.G, a.B, 0.35f) : a;
        // 先整圈画底色，再把"另一种颜色"那一段盖上去（用一小段圆弧当示意，不做精密分色：
        // 这条只需要让人看出来"不止一种颜色"，不需要准确比例）。
        _ctx.DrawEllipse(half, _scratch, w);
        if (distinct >= 1)
        {
            _scratch.Color = disabled ? new Color4(b2.R, b2.G, b2.B, 0.35f) : b2;
            float a0 = -MathF.PI / 2f;
            // 用四条短弧线拼出"另外半圈"（D2D 的 DrawArc 在各版本签名不一致，不值得为它冒险）
            for (int k = 0; k < 12; k++)
            {
                float t0 = a0 + k / 24f * MathF.PI * 2f;
                float t1 = t0 + MathF.PI * 2f / 24f;
                _ctx.DrawLine(new Vector2(cx + MathF.Cos(t0) * r, cy + MathF.Sin(t0) * r),
                              new Vector2(cx + MathF.Cos(t1) * r, cy + MathF.Sin(t1) * r), _scratch, w);
            }
        }
    }

    private static bool SameRgb(in Color4 a, in Color4 b)
        => MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;

    /// <summary>
    /// 收起态的圆钮（参考实现里条首那个 ✕ 点下去之后的样子）。
    /// 画一个圆 + 中间的"＋"：两段线自己画——它不是图标，是个纯几何符号。
    /// </summary>
    private void DrawCollapsedDot(InkEngine app, in RectF sel)
    {
        float dpi = Dpi / 96f;
        var r = SelectionHandles.BarCollapsedRect(sel, dpi, app.ViewportCanvas);
        float d = r.MaxX - r.MinX;
        var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);

        bool hot = app.SelBarHover == (int)SelBarButton.Collapse;
        var th = UiTheme.Default;
        _scratch.Color = hot ? th.Hover : th.Panel;
        _ctx.FillEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch, 1f * dpi);

        _scratch.Color = th.Text;
        float arm = d * 0.19f, w = 1.8f * dpi;
        _ctx.DrawLine(new Vector2(c.X - arm, c.Y), new Vector2(c.X + arm, c.Y), _scratch, w);
        _ctx.DrawLine(new Vector2(c.X, c.Y - arm), new Vector2(c.X, c.Y + arm), _scratch, w);
    }

    /// <summary>浮动面板的卡片底：白底、圆角、浅边、一层很淡的投影（和操作条同一套）。</summary>
    private void DrawPanelCard(in RectF r, float radius)
    {
        float dpi = Dpi / 96f;
        var th = UiTheme.Default;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        // 投影：两层就够（界面上那套四层阴影是给常驻面板用的，这里是临时浮层）
        for (int i = 2; i >= 1; i--)
        {
            _scratch.Color = new Color4(0f, 0f, 0f, 0.045f * i);
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(r.MinX + 0.5f, r.MinY + 0.5f + i * 1.5f * dpi,
                                     r.MaxX - 0.5f, r.MaxY - 0.5f + i * 1.5f * dpi),
                radius, radius), _scratch);
        }
        _scratch.Color = th.Panel;
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch, 1f * dpi);
    }

    /// <summary>
    /// 颜色 / 粗细 / 线型面板（照参考实现）：一条粗细滑条、一行线型、一块 4 列色板。
    ///
    /// 色板的"当前色"用**环**标出来（参考实现的做法）：比整块换色省地方，
    /// 而且混合选区（几种颜色混着选）也能表达——那时环画成双色（见 DrawColorRing）。
    /// </summary>
    private void DrawInkPanel(InkEngine app, in RectF sel)
    {
        float dpi = Dpi / 96f;
        int sc = SelectionHandles.SwatchCount;
        var p = SelectionHandles.PanelRect(sel, dpi, app.ViewportCanvas, sc);
        DrawPanelCard(p, 10f * dpi);

        // ---- ① 粗细滑条 ----
        var slider = SelectionHandles.SliderRect(sel, dpi, app.ViewportCanvas, sc);
        int steps = app.SliderStepCount();
        int cur = app.SliderStepOfSelection(sel);
        float cy = (slider.MinY + slider.MaxY) * 0.5f;
        float x0 = SelectionHandles.SliderStepX(0, steps, sel, dpi, app.ViewportCanvas, sc);
        float x1 = SelectionHandles.SliderStepX(steps - 1, steps, sel, dpi, app.ViewportCanvas, sc);
        var th = UiTheme.Default;
        _scratch.Color = new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.35f);
        _ctx.DrawLine(new Vector2(x0, cy), new Vector2(x1, cy), _scratch, 2.4f * dpi);
        for (int i = 0; i < steps; i++)
        {
            float x = SelectionHandles.SliderStepX(i, steps, sel, dpi, app.ViewportCanvas, sc);
            _scratch.Color = i == cur ? th.ActiveBg : th.TextMuted;
            _ctx.FillEllipse(new Ellipse(new Vector2(x, cy), 1.9f * dpi, 1.9f * dpi), _scratch);
        }
        float kx = SelectionHandles.SliderStepX(cur, steps, sel, dpi, app.ViewportCanvas, sc);
        _ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 7f * dpi, 7f * dpi),
                         Brush(new Color4(1f, 1f, 1f, 1f)));
        _scratch.Color = th.ActiveBg;
        _ctx.DrawEllipse(new Ellipse(new Vector2(kx, cy), 7f * dpi, 7f * dpi), _scratch, 1.8f * dpi);

        // ---- ②③ 线型：实线 / 虚线 ----
        for (int i = 0; i < 2; i++)
        {
            var cell = SelectionHandles.StyleCellRect(i, sel, dpi, app.ViewportCanvas, sc);
            bool solid = i == 0;
            _scratch.Color = solid ? th.Hover : th.Panel;                   // 虚线禁用（底层还没做）
            float cr = 7f * dpi;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY), cr, cr), _scratch);

            float lx0 = cell.MinX + 10f * dpi, lx1 = cell.MaxX - 10f * dpi;
            float ly = (cell.MinY + cell.MaxY) * 0.5f;
            _scratch.Color = solid
                ? th.Text
                : new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.55f);
            if (solid) _ctx.DrawLine(new Vector2(lx0, ly), new Vector2(lx1, ly), _scratch, 2.6f * dpi);
            else DrawDashed(new Vector2(lx0, ly), new Vector2(lx1, ly), 2.6f * dpi);
        }

        // ---- ④ 色板 ----
        var swatches = InkPalette.SelectionSwatches;
        Color4 curColor = default; bool anyColor = false;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
            curColor = s.Color; anyColor = true; break;
        }
        for (int i = 0; i < sc; i++)
        {
            var cell = SelectionHandles.SwatchRect(i, sel, dpi, app.ViewportCanvas, sc);
            var c = new Vector2((cell.MinX + cell.MaxX) * 0.5f, (cell.MinY + cell.MaxY) * 0.5f);
            float rad = (cell.MaxX - cell.MinX) * 0.5f;
            bool custom = i == swatches.Length - 1;      // 末格 = 自定义取色（本轮占位）

            if (custom)
            {
                // 占位不画彩虹（那要引渐变），画一个虚线灰环表示"这里还没接"
                _scratch.Color = new Color4(th.TextMuted.R, th.TextMuted.G, th.TextMuted.B, 0.7f);
                for (int k = 0; k < 8; k++)
                {
                    float t0 = k / 8f * MathF.PI * 2f, t1 = t0 + MathF.PI * 2f / 16f;
                    _ctx.DrawLine(new Vector2(c.X + MathF.Cos(t0) * rad, c.Y + MathF.Sin(t0) * rad),
                                  new Vector2(c.X + MathF.Cos(t1) * rad, c.Y + MathF.Sin(t1) * rad),
                                  _scratch, 2f * dpi);
                }
                continue;
            }

            var col = swatches[i].Color;
            _scratch.Color = col;
            _ctx.FillEllipse(new Ellipse(c, rad, rad), _scratch);
            _scratch.Color = new Color4(0f, 0f, 0f, 0.18f);        // 浅色片压在浅底上要能看见边
            _ctx.DrawEllipse(new Ellipse(c, rad, rad), _scratch, 1f * dpi);

            // 当前色：外面套一圈（参考实现的做法）
            if (anyColor && SameRgb(col, curColor))
            {
                _scratch.Color = th.ActiveBg;
                _ctx.DrawEllipse(new Ellipse(c, rad + 3.5f * dpi, rad + 3.5f * dpi), _scratch, 2.2f * dpi);
            }
        }
    }

    /// <summary>层级小面板：置顶 / 置底两格（图标用 Fluent 的"上/下箭头 + 底托"）。</summary>
    private void DrawLayerPanel(InkEngine app, in RectF sel)
    {
        float dpi = Dpi / 96f;
        var p = SelectionHandles.LayerPanelRect(sel, dpi, app.ViewportCanvas);
        DrawPanelCard(p, 10f * dpi);

        string[] icons = { IconPaths.toFront, IconPaths.toBack };
        var th = UiTheme.Default;
        for (int i = 0; i < 2; i++)
        {
            var cell = SelectionHandles.LayerCellRect(i, sel, dpi, app.ViewportCanvas);
            _scratch.Color = th.Hover;
            float cr = 8f * dpi;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY), cr, cr), _scratch);
            float icon = SelectionHandles.BarIconBoxLogical * dpi;
            DrawIcon(icons[i], (cell.MinX + cell.MaxX) * 0.5f - icon * 0.5f,
                     (cell.MinY + cell.MaxY) * 0.5f - icon * 0.5f, icon,
                     Brush(th.Text));
        }
    }


    /// <summary>
    /// 滚动条（样式 B：Win11 / Figma 那根细线）。
    ///
    /// 默认隐藏，滚动时浮现，**静止 3 秒淡出**（InkClass 实测 1.5 秒太快，
    /// 用户会找不到它）。画在浮动层上——它每帧都在变（淡出），进内容层就等于
    /// 每帧重画内容。
    ///
    /// 比例滑块靠"画布范围"（内容边界 ∪ 一屏，见 InkDocument.Extent）：
    /// 没有总高度就没有比例可算。画布只有一屏时干脆不画。
    ///
    /// 用 <c>_scratch</c> 而不是 Brush(颜色)：淡出时颜色每帧都在变，
    /// Brush() 是按颜色缓存的，会一帧往缓存里塞一个画刷。
    /// </summary>
    private void DrawScrollBar(InkEngine app)
    {
        if (!TryScrollBar(app, out var sb)) return;      // 画布只有一屏：没有滚动条

        // 悬停/拖动中：不淡出，而且加粗——"这根线能拖"靠它自己说，
        // 不靠换光标（手型按规范只能表示链接，四向箭头会被读成缩放）。
        bool hot = app.ScrollBarHover || app.ScrollBarDragging;
        float dpi = Dpi / 96f;
        double idle = (app.NowMs - app.ScrollBarActiveAtMs) / 1000.0;
        float alpha = hot ? 1f
                    : idle <= 3.0 ? 1f
                    : MathF.Max(0f, 1f - (float)((idle - 3.0) / 0.4));
        if (alpha <= 0.01f) return;

        float w = (hot ? 9f : 4f) * dpi;
        float x0 = sb.AxisX - w * 0.5f;
        float r = w * 0.5f;

        // 轨道：极淡，只用来"知道这儿有东西"，不抢视觉
        _scratch.Color = new Color4(0.50f, 0.52f, 0.55f, (hot ? 0.22f : 0.16f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.Top, x0 + w, sb.Bottom), r, r), _scratch);

        // 滑块：中灰带透明度，浅色深色背景上都立得住
        _scratch.Color = new Color4(0.47f, 0.49f, 0.53f, (hot ? 0.86f : 0.75f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.ThumbTop, x0 + w, sb.ThumbTop + sb.ThumbLen), r, r),
            _scratch);
    }

    /// <summary>
    /// 滚动条的几何（**屏幕坐标**，和 OriginX/Width 同一套）。
    /// 画和拖动共用它——两处各算一份，改一处忘一处是这类代码的经典翻车点。
    /// 返回 false = 现在不该有滚动条（画布只有一屏）。
    /// </summary>
    internal bool TryScrollBar(InkEngine app, out ScrollBarLayout l)
    {
        l = default;
        float dpi = Dpi / 96f;
        var extent = app.CanvasExtent;
        float extentH = extent.MaxY - extent.MinY;
        if (extentH <= Height + 1f) return false;

        float top = OriginY + 40f * dpi;
        float bottom = OriginY + Height - 40f * dpi;
        float trackLen = bottom - top;
        if (trackLen < 40f * dpi) return false;

        // 滑块长度 = 一屏 / 画布总高；位置 = 视口顶在画布里的比例
        float thumbLen = MathF.Max(40f * dpi, trackLen * (Height / extentH));
        float maxTravel = trackLen - thumbLen;
        float viewTop = OriginY - app.ViewOffsetY;       // 视口顶部对应的画布 Y
        float frac = extentH - Height > 1f
            ? (viewTop - extent.MinY) / (extentH - Height) : 0f;
        frac = Math.Clamp(frac, 0f, 1f);

        l = new ScrollBarLayout
        {
            AxisX = OriginX + Width - 8f * dpi,
            Top = top,
            Bottom = bottom,
            ThumbTop = top + maxTravel * frac,
            ThumbLen = thumbLen,
            MaxTravel = maxTravel,
            ExtentMinY = extent.MinY,
            ExtentH = extentH,
        };
        return true;
    }

    /// <summary>
    /// 画一个四边形。选中框现在是轴对齐的，但边框仍然用四条线拼：
    /// 这条路径同时要能画"框有自己的朝向"的情形（见 SelectionFrame 的注释），
    /// 换成 DrawRectangle 就等于把那条路堵死了。
    /// </summary>
    private void DrawQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float width)
    {
        _ctx.DrawLine(a, b, _scratch, width);
        _ctx.DrawLine(b, c, _scratch, width);
        _ctx.DrawLine(c, d, _scratch, width);
        _ctx.DrawLine(d, a, _scratch, width);
    }

    /// <summary>
    /// 画一个**虚线**四边形（复制拖拽模式的框用）。
    ///
    /// 为什么不用 D2D 的 dash style：那要给每种线宽各建一个 `ID2D1StrokeStyle`
    /// 并管生命周期，而现在只有两处要虚线（这个框 + 面板里的"虚线"示意）。手算分段
    /// 十来行、结果完全可控，也不用担心各版本 Vortice 的 API 差异。
    /// </summary>
    private void DrawQuadDashed(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float width)
    {
        DrawDashed(a, b, width);
        DrawDashed(b, c, width);
        DrawDashed(c, d, width);
        DrawDashed(d, a, width);
    }

    private void DrawDashed(Vector2 a, Vector2 b, float width)
    {
        float len = Vector2.Distance(a, b);
        if (len < 0.5f) return;
        var dir = (b - a) / len;
        float dash = 7f * Dpi / 96f, gap = 5f * Dpi / 96f;
        for (float t = 0; t < len; t += dash + gap)
        {
            float t1 = MathF.Min(t + dash, len);
            _ctx.DrawLine(a + dir * t, a + dir * t1, _scratch, width);
        }
    }

    /// <summary>
    /// 画一个图标。几何按 24×24 的单位坐标缓存，这里用变换缩放到目标尺寸——
    /// 同一份几何能在不同尺寸和 DPI 下复用，不用重建。
    /// </summary>
    private void DrawIcon(string d, float x, float y, float size, ID2D1SolidColorBrush brush)
    {
        var geo = SvgPath.Get(d);
        if (geo == null) return;

        var saved = _ctx.Transform;
        _ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                       * Matrix3x2.CreateTranslation(x, y) * saved;
        _ctx.FillGeometry(geo, brush);
        _ctx.Transform = saved;
    }

    private void DrawLaser(InkEngine app)
    {
        var pts = app.Laser.Points;
        int n = pts.Count;
        if (n < 2) return;

        double now = app.NowMs;
        // 加法混合：激光要"发光"，叠在深色 PPT 上才有那个感觉。
        _ctx.PrimitiveBlend = PrimitiveBlend.Add;

        // 尾巴做成"填充的渐细带子"（越老越细直到消失），读起来像彗星尾——
        // 这正是激光笔该有的样子。实测：填充一条几何是几十微秒，
        // 而描边一条几何是毫秒级，所以这里不用描边。
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

    /// <summary>
    /// 落点反馈：自己画在浮动层上的"指针"。
    ///
    /// 为什么不直接用系统光标：圆环/圆盘要跟着**笔宽**变，还要在深色 PPT 和
    /// 白色白板上都看得见——系统光标做不到这两点。代价是真光标必须同时藏起来，
    /// 那份逻辑在引擎里（InkEngine.DrawnCursor / CursorKind.Hidden）。
    ///
    /// 画在浮动层还有个好处：它的坐标和笔迹同源，不存在"光标热点偏了一两个
    /// 像素"的问题——写字时那一两个像素是能感觉出来的。
    /// </summary>
    private void DrawToolCursor(InkEngine app)
    {
        var shape = app.DrawnCursor;
        if (shape == InkEngine.ToolCursorShape.None) return;

        var c = new Vector2(app.PointerX, app.PointerY);
        if (shape == InkEngine.ToolCursorShape.Disc)
        {
            DrawHighlighterDisc(app, c);
            return;
        }
        if (shape == InkEngine.ToolCursorShape.Dot)
        {
            DrawLaserDot(app, c);
            return;
        }
        if (shape == InkEngine.ToolCursorShape.Rect)
        {
            DrawEraserRectCursor(app, c);
            return;
        }

        float outer = app.CursorOuterRadius;
        float truth = app.Tool == Tool.Eraser
            ? app.EraserRadius
            : app.CursorRingTrueRadius;
        var fill = app.Tool == Tool.Eraser
            ? new Color4(0.35f, 0.55f, 0.95f, 0.10f)      // 橡皮：淡蓝，"要擦掉这一块"
            : new Color4(0.35f, 0.55f, 0.95f, 0.06f);     // 笔尖：更淡，不挡视线
        DrawRingCursor(c, truth, outer, fill);
    }

    /// <summary>
    /// 圆环：橡皮的"要擦掉多大一块"、笔的"这一笔多粗"。
    ///
    /// 双色描边和四向刻度都是被场景逼出来的：单色圆在白板（浅）和深色 PPT（深）
    /// 上总有一种看不清；刻度标出圆心，投影上写字手是抖的，得知道落点在哪。
    ///
    /// **两个圈**：内圈是**真实笔宽**（"我写出来就这么粗"），外圈是固定尺寸的
    /// 最小可见范围。只有一个圈时无解：按真实宽度画，细笔看不见；按下限画，
    /// 细笔的圈比笔迹粗一倍，反而误判。Photoshop 的笔刷光标就是这个做法。
    /// </summary>
    /// <summary>
    /// 像素橡皮的落点：**竖着的黄金比例矩形**（高 : 宽 = 1.618）。
    ///
    /// 为什么是矩形而不是圆环：它就是"一块橡皮"。从上往下抹一列板书时，用户要知道
    /// 这一抹有多宽、多高；圆环表达不了这个。
    ///
    /// 三层（和圆环同一套色彩逻辑）：
    ///   · **半透明填充**——看得见"这一块会被擦掉"，同时底下的字还看得见（用户要的
    ///     "有点透明度"）。太实会挡住要擦的东西，太淡又看不出边界；
    ///   · 白色外圈 + 深色内圈的双色描边：深色 PPT 和白色白板上都得看得见；
    ///   · 中心一个小十字：投影上写字手会抖，得知道精确落点在哪。
    /// </summary>
    private void DrawEraserRectCursor(InkEngine app, Vector2 c)
    {
        float hw = MathF.Max(1f, app.PixelEraserHalfWidthPx);
        float hh = MathF.Max(1f, app.PixelEraserHalfHeightPx);

        _ctx.FillRectangle(
            new Vortice.RawRectF(c.X - hw, c.Y - hh, c.X + hw, c.Y + hh),
            // 填充色：**0.12 → 0.22**（用户 2026-09-17："面积橡皮擦太透明了"）。
            // 参考点：笔尖那个圆环填充是 0.06，整笔橡皮的圆是 0.10——面积橡皮是
            // 唯一"要看清边界的一块面"，它最实才对；0.22 之后底下的字仍然透得出来。
            Brush(new Color4(0.35f, 0.55f, 0.95f, 0.22f)));

        // 两层描边都比原来略实一档，且线宽 1.5 → 1.8：面积橡皮的边界是"会不会擦掉"
        // 的判据，投影上要一眼看清。
        _scratch.Color = new Color4(1f, 1f, 1f, 0.85f);
        _ctx.DrawRectangle(new Vortice.RawRectF(
            c.X - hw - 0.9f, c.Y - hh - 0.9f, c.X + hw + 0.9f, c.Y + hh + 0.9f), _scratch, 1.8f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.9f);
        _ctx.DrawRectangle(new Vortice.RawRectF(
            c.X - hw + 0.9f, c.Y - hh + 0.9f, c.X + hw - 0.9f, c.Y + hh - 0.9f), _scratch, 1.8f);

        float tick = MathF.Max(5f, MathF.Min(hw, hh) * 0.25f);
        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.85f);
        _ctx.DrawLine(new Vector2(c.X - tick, c.Y), new Vector2(c.X + tick, c.Y), _scratch, 1.8f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - tick), new Vector2(c.X, c.Y + tick), _scratch, 1.8f);
    }

    private void DrawRingCursor(Vector2 c, float truthR, float outerR, Color4 fill)
    {
        float r = MathF.Max(outerR, 1f);
        bool doubleRing = r - truthR > 1.5f;         // 两个圈差得太近就只画外圈

        // 中间一层很淡的填充，让"范围"一眼可见。
        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(fill));

        // 内圈：真实笔宽。它才是"这一笔有多粗"的答案。
        if (doubleRing)
        {
            _ctx.FillEllipse(new Ellipse(c, truthR, truthR),
                             Brush(new Color4(0.35f, 0.55f, 0.95f, 0.16f)));
            _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.55f);
            _ctx.DrawEllipse(new Ellipse(c, truthR, truthR), _scratch, 1f);
        }

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

    /// <summary>
    /// 激光笔的落点：一个实心小圆点 + 白色外圈。
    ///
    /// 为什么不是圆环：激光是"我说的是这里"，不需要表达范围（它连墨都不留）。
    /// 白圈是为了在深色背景上也能看见，并且和激光轨迹的红是同一个色系。
    /// </summary>
    private void DrawLaserDot(InkEngine app, Vector2 c)
    {
        float r = MathF.Max(app.CursorDotRadius, 2f);
        _ctx.FillEllipse(new Ellipse(c, r + 1f, r + 1f), Brush(new Color4(1f, 1f, 1f, 0.85f)));
        // 和激光轨迹同色（DrawLaser 里用的就是这组红）
        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(new Color4(1f, 0.16f, 0.16f, 0.95f)));
    }

    /// <summary>
    /// 荧光笔的落点：**一个直径 = 笔宽的实心圆盘**。
    ///
    /// 为什么是圆，而不是旧版那根横条（胶囊）：荧光笔画出来的墨迹就是"中心线
    /// 两侧各半个笔宽 + 两端圆帽"，落笔处本身是一个直径 = 笔宽的圆；单击一下
    /// 留下的也正是这个圆。横条只在水平方向变宽，竖着画的那一刻和实际墨迹
    /// 对不上，拖动时看上去像拖着一根尺子。
    ///
    /// 填充用荧光笔当前的颜色，落笔前就能看到会画出什么色；描边用和笔/橡皮
    /// 圆环同一套的双色（浅底、深底上都看得见）。
    /// </summary>
    private void DrawHighlighterDisc(InkEngine app, Vector2 c)
    {
        // 半径就是半个笔宽——和真正落到屏幕上的墨迹同一个尺寸，不放大也不缩小。
        float r = MathF.Max(app.HighlighterWidthLogical * app.DpiScale * 0.5f, 2f);
        var hl = app.HighlighterCurrent;

        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(new Color4(hl.R, hl.G, hl.B, 0.22f)));

        _scratch.Color = new Color4(1f, 1f, 1f, 0.75f);
        _ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f), _scratch, 1.5f);

        _scratch.Color = new Color4(0.22f, 0.28f, 0.38f, 0.55f);
        _ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f), _scratch, 1.5f);
    }

    private void DrawMarquee(InkEngine app)
    {
        if (!app.MarqueeActive) return;

        // 套索：画**自由曲线**，不是矩形。手势是什么样，屏幕上就得是什么样——
        // 用户要能看见自己"圈"到哪儿了，否则 80% 这条判据完全不可预期。
        // 收口那条边画淡一点：它提示"松手会自动闭合"，但还不是现在的边界。
        //
        // 线头必须画到**指针此刻的位置**（LassoLive），不能只画抽稀过的路径点：
        // 路径点之间隔着 3 逻辑像素，只画路径的话线头会慢半拍（200% 缩放 = 6 个物理像素，
        // 看得出来）。抽稀是为了判据便宜，不是为了少画这一截。
        if (app.SelMode == SelectMode.Lasso && app.LassoPath.Count >= 1
            && (app.LassoPath.Count >= 2
                || Vector2.Distance(app.LassoPath[0], app.LassoLive) > 0.5f))
        {
            _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.95f);
            using (var geo = BuildFreePolyline(app.LassoPath, app.LassoLive))
                _ctx.DrawGeometry(geo, _scratch, 1.6f, Gfx.Round);

            _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.30f);
            var p0 = app.LassoPath[0];
            _ctx.DrawLine(p0, app.LassoLive, _scratch, 1.2f);
            return;
        }

        var r = new Vortice.RawRectF(app.MqMinX, app.MqMinY, app.MqMaxX, app.MqMaxY);
        _ctx.FillRectangle(r, Brush(new Color4(0.25f, 0.6f, 1f, 0.12f)));
        _scratch.Color = new Color4(0.35f, 0.75f, 1f, 0.95f);
        _ctx.DrawRectangle(r, _scratch, 1.2f);
    }

    /// <summary>
    /// 把一串画布坐标的点连成一条**开放折线**（套索预览用）。
    ///
    /// 为什么不复用手写笔迹那套：那是"中心线 + 原生描边"的墨，要按笔宽、颜色、
    /// 分块缓存走；套索只是一根临时的引导线，每帧现建现扔就够（几十个顶点）。
    /// </summary>
    private static ID2D1PathGeometry BuildFreePolyline(IReadOnlyList<Vector2> pts, Vector2 tail)
    {
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(pts[0], FigureBegin.Hollow);
        for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
        if (Vector2.Distance(tail, pts[pts.Count - 1]) > 0.5f) sink.AddLine(tail);
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 截图时拖出来的那个框。
    ///
    /// 和框选**刻意画得不一样**：框选是蓝色（"我在选东西"），截图是琥珀色 +
    /// 四角短角标（"我在取景"）。同一个手势、两套皮肤，用户一眼就知道
    /// 松手之后会发生什么——这个区分在投影上很值。
    ///
    /// 框里面**不填充**：截图要看见底下的内容才知道该取到哪儿。
    /// </summary>
    private void DrawCaptureRect(InkEngine app)
    {
        if (!app.CaptureActive) return;
        var r = new Vortice.RawRectF(app.CapMinX, app.CapMinY, app.CapMaxX, app.CapMaxY);
        var accent = new Color4(1f, 0.68f, 0.10f, 1f);      // 琥珀

        _scratch.Color = new Color4(accent.R, accent.G, accent.B, 0.9f);
        _ctx.DrawRectangle(r, _scratch, 1.6f);

        // 四角角标：长度取短边的 1/6，但夹在 8~28 之间——
        // 框很小时角标不能糊成一片，很大时也不能细得看不见。
        float shortSide = MathF.Min(r.Right - r.Left, r.Bottom - r.Top);
        float len = Math.Clamp(shortSide / 6f, 8f, 28f);
        float t = 3f;
        DrawCorner(r.Left, r.Top, len, len, t);
        DrawCorner(r.Right, r.Top, -len, len, t);
        DrawCorner(r.Right, r.Bottom, -len, -len, t);
        DrawCorner(r.Left, r.Bottom, len, -len, t);
    }

    private void DrawCorner(float x, float y, float dx, float dy, float t)
    {
        _scratch.Color = new Color4(1f, 0.68f, 0.10f, 1f);
        _ctx.DrawLine(new Vector2(x, y), new Vector2(x + dx, y), _scratch, t);
        _ctx.DrawLine(new Vector2(x, y), new Vector2(x, y + dy), _scratch, t);
    }

    private float HudScale => Dpi / 96f;
    public float HudWidthPx => HudWidthLogical * HudScale;
    public float HudHeightPx => HudHeightLogical * HudScale;

    /// <summary>
    /// 性能面板的文字格式，**按 DPI 生成并缓存**。
    ///
    /// 这是"看不清"的真正原因：渲染时的变换只有平移、没有缩放，字号写死 15
    /// 就等于 15 物理像素——200% 缩放的屏上只有 7.5 逻辑像素高，投影上糊成一团。
    /// </summary>
    private IDWriteTextFormat HudFormat()
    {
        float px = MathF.Max(11f, MathF.Round(HudFontLogical * HudScale));
        if (_hudFormat == null || _hudFormatPx != px)
        {
            _hudFormat?.Dispose();
            _hudFormat = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _hudFormatPx = px;
        }
        return _hudFormat;
    }

    /// <summary>
    /// 面板进缓存位图。**必须在帧的 BeginDraw 之前调用**——Direct2D 不允许在
    /// BeginDraw/EndDraw 中间换渲染目标（换了就把设备搞进错误状态，这条踩过）。
    ///
    /// 为什么要缓存：老实现是每帧直接排 7 行文字，实测面板自己吃掉 4.3 ms/帧，
    /// 比它显示的所有东西加起来还贵（笔迹才 2 ms）。文字是 4 Hz 才变的，
    /// 每帧重排纯属浪费。现在只有文字变了才重排一次，平时只是一次贴图。
    /// </summary>
    private void PrepareHud(InkEngine app)
    {
        if (!app.ShowHud) { _hudCacheText = null; return; }

        float scale = HudScale;
        int w = (int)MathF.Ceiling(HudWidthPx), h = (int)MathF.Ceiling(HudHeightPx);
        EnsureHudBitmap(w, h);

        string text = app.HudText ?? "";
        _hudRedrewThisFrame = false;
        if (_hudCacheText == text) return;

        _ctx.Target = _hudTarget;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;
        var box = new Vortice.RawRectF(0, 0, w, h);
        _ctx.FillRectangle(box, Brush(new Color4(0.05f, 0.06f, 0.09f, 0.86f)));
        _scratch.Color = new Color4(0.35f, 0.8f, 1f, 0.9f);
        _ctx.DrawRectangle(new Vortice.RawRectF(0.5f, 0.5f, w - 0.5f, h - 0.5f), _scratch, 1.5f);
        _scratch.Color = new Color4(0.94f, 0.97f, 1f, 1f);
        // 注意 Rect 是 (x, y, width, height)，不是 (left, top, right, bottom)。
        float pad = HudPadLogical * scale;
        _ctx.DrawText(text, HudFormat(), new Rect(pad, pad * 0.8f, w - pad * 2f, h - pad * 1.6f), _scratch);
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.Target = null;
        var hr = _ctx.EndDraw();
        if (hr.Failure) LastError = "hud EndDraw: " + hr.Description;
        _hudCacheText = text;
        _hudRedrewThisFrame = true;
        HudRedraws++;
    }

    private bool _hudRedrewThisFrame;

    private void EnsureHudBitmap(int w, int h)
    {
        if (_hudTarget != null && _hudBmpW == w && _hudBmpH == h) return;
        _hudSource?.Dispose(); _hudTarget?.Dispose(); _hudBmpTex?.Dispose();
        _hudSource = null; _hudTarget = null; _hudBmpTex = null;

        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, w), Height = (uint)Math.Max(1, h),
            MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _hudBmpTex = Gfx.Device.CreateTexture2D(desc);
        var pf = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                                                 Vortice.DCommon.AlphaMode.Premultiplied);
        using (var surface = _hudBmpTex.QueryInterface<IDXGISurface>())
        {
            // 一张纹理两个视图：当渲染目标的那张不能同时当绘制源（Direct2D 的规定）。
            _hudTarget = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            _hudSource = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.None));
        }
        _hudBmpW = w; _hudBmpH = h;
        _hudCacheText = null;      // 尺寸变了必须重排
    }

    /// <summary>把缓存好的面板贴到后缓冲上（帧内调用，代价就是一次位图拷贝）。</summary>
    private void DrawHud()
    {
        if (_hudSource == null) return;
        float m = HudMarginLogical * HudScale;
        var dst = new Vortice.RawRectF(m, m, m + _hudBmpW, m + _hudBmpH);
        _ctx.DrawBitmap(_hudSource, dst, 1f,
                        Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
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

        LastPresentStartQpc = Qpc.Now;

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
        LastPresentEndQpc = Qpc.Now;
        LastPresentMs = sw.Elapsed.TotalMilliseconds;
        if (hr.Failure) LastError = "Present: " + hr.Description;

    }


    public void Dispose()
    {
        _brushes.Clear();
        _scratch?.Dispose();
        _readoutFormat?.Dispose();
        _hudFormat?.Dispose();
        _hudSource?.Dispose();
        _hudTarget?.Dispose();
        _hudBmpTex?.Dispose();
        _tiles?.Dispose();
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
