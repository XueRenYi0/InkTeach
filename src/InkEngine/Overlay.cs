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

    /// <summary>
    /// 虚线 / 点线的描边样式（2026-09-19 加，见 <see cref="StrokeDash"/>）。
    ///
    /// **节长节距用"自定义图案"，单位是笔宽**（Direct2D 的约定：dashes 里的数字乘笔宽）——
    /// 所以粗笔的虚线自动变长、细笔自动变密，和老师"粗笔配长虚线"的直觉一致；
    /// 写死像素值的话，宽笔上会密得像齿锯。
    ///
    /// 端帽：
    ///   · 虚线用 **平头**——圆头会让每一节两头鼓成半圆，节挨得近时糊成一串珠子；
    ///   · 点线**必须**用 **圆头**，而且节长取一个接近 0 的值：圆头会在这一节两头
    ///     各鼓出半个笔宽，合起来正好是**一个直径 = 笔宽的圆点**。平头 + 0 长度的节
    ///     会**什么都画不出来**（这是 Direct2D 上一个很容易踩空的地方）。
    /// 拐角两边都用圆角（和实线一致，粗折线上不会有尖角）。
    /// </summary>
    public static ID2D1StrokeStyle1 DashedStroke;
    public static ID2D1StrokeStyle1 DottedStroke;

    public static ID2D1StrokeStyle1 Dashed => DashedStroke ??= D2DFactory.CreateStrokeStyle(
        new StrokeStyleProperties1
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Flat,
            LineJoin = LineJoin.Round,
            MiterLimit = 10f,
            DashStyle = DashStyle.Custom,
        },
        new[] { 3f, 2f });                    // 节 3、缝 2（× 笔宽）→ 出墨约六成

    public static ID2D1StrokeStyle1 Dotted => DottedStroke ??= D2DFactory.CreateStrokeStyle(
        new StrokeStyleProperties1
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Round,         // 见上面：点线靠圆头把"零长度的节"鼓成一个圆点
            LineJoin = LineJoin.Round,
            MiterLimit = 10f,
            DashStyle = DashStyle.Custom,
        },
        new[] { 0.01f, 2f });                 // 节≈0、缝 2（× 笔宽）→ 出墨约三成

    /// <summary>
    /// 这个线型该用哪一条描边样式。**渲染只有这一个分派点**
    /// （见 <c>DrawStrokeCore</c>）——线型要加档位就改这里，别在各处自己判断。
    /// </summary>
    public static ID2D1StrokeStyle1 StyleFor(StrokeDash dash) => dash switch
    {
        StrokeDash.Dashed => Dashed,
        StrokeDash.Dotted => Dotted,
        _ => Round,
    };

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
        DashedStroke?.Dispose();          // 线型的两条（见 StyleFor）
        DashedStroke = null;
        DottedStroke?.Dispose();
        DottedStroke = null;
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
    /// <summary>
    /// D2D 1.3 的设备上下文 2：**原生墨迹**（ID2D1Ink）唯一能创建的地方
    /// （<c>CreateInk</c> / <c>DrawInk</c> 都挂在这个接口上）。拿不到就是 null。
    /// </summary>
    private ID2D1DeviceContext2 _ctx2;
    /// <summary>墨迹笔尖样式（圆头）——和"笔＝圆头"这条口径一致。</summary>
    private ID2D1InkStyle _inkStyle;
    /// <summary>每次画一条压感笔迹最多铺多少段（见 DrawPressureInk：压力是慢变量）。</summary>
    private const int InkMaxSegments = 120;
    /// <summary>段缓冲：**预分配、复用**，不在每帧绘制里 new（一条笔最多 121 个采样点）。</summary>
    private readonly InkBezierSegment[] _inkSegs = new InkBezierSegment[InkMaxSegments];
    /// <summary>压力的指数平滑系数（0..1，越小越稳）。见 DrawPressureInk。</summary>
    private const float InkPressureEma = 0.35f;
    /// <summary>最小墨迹半径（画布像素）：轻压时也不至于细到画不出来。</summary>
    private const float InkMinRadius = 0.4f;
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

    /// <summary>
    /// **角标**（三角形的三个内角 / 平行四边形的两个夹角）用的小号文字格式：
    /// 比主读数小一号（11 逻辑像素）。
    ///
    /// 为什么另存一份、不把主读数调小：角标是贴在顶点旁边的，太大就会压住那条边。
    /// （以前三角形中间还印一行"内角和"，那一行才用主读数那一号字；用户 2026-09-19
    /// 把那一行去掉了，于是角标是这一组里唯一的字号。）
    /// </summary>
    private IDWriteTextFormat _readoutFormatSmall;
    private float _readoutFormatSmallPx;

    /// <summary>
    /// 拖端点时的"临时几何"复用的那条 scratch 笔画（见 <see cref="DrawVertexPreview"/>）。
    ///
    /// 为什么复用它、而不是在这里另写一套"画一条线 / 一个箭头"：图形的画法
    /// （几何、描边、箭头头部比例）已经在 Stroke 里了，另写一份迟早会不一样。
    /// 它和"正在书写的那一笔"是同一种东西——每帧重建一次几何，代价一样。
    /// </summary>
    private Stroke _vertexGhost;

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
    /// 这台机器上有没有 **D2D 原生墨迹**（压感笔迹的变宽通道）。见 <see cref="InkNote"/>。
    ///
    /// 它决定"有压感的笔迹"怎么画：可用 → `ID2D1Ink`（每点一个半径，D2D 自己算轮廓）；
    /// 不可用 → 退回等宽描边（也就是 2026-09-14 以来那条路，观感不变、只是不吃压感）。
    /// </summary>
    public static bool InkAvailable;
    /// <summary>启动日志里那一行（可不可用、为什么）。</summary>
    public static string InkNote = "未尝试";
    /// <summary>
    /// 走过**变宽墨迹**那条路的次数（自检用）。
    ///
    /// 为什么要这个计数：这条路"调用成功但屏幕上什么都没有"是一种可能的失败形态
    /// （不像抛异常那样会自己喊出来），而自检的数像素判据只看屏幕——
    /// 有这个计数才能区分"没走这条路"和"走了但没画出来"。
    /// </summary>
    public static int PressureInkDraws;

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
    public void AddInkTrailPoints(Vector2[] real, int realCount, Vector2[] predicted, int predictedCount,
                                  float radius, float[] radii = null)
    {
        if (!_trailActive || _inkTrail == null || real == null || realCount <= 0) return;
        try
        {
            float r = MathF.Max(0.5f, radius);
            float R(int i) => radii != null && i >= 0 && i < radii.Length
                ? MathF.Max(0.5f, radii[i]) : r;
            var realPts = new DCompositionInkTrailPoint[realCount];
            for (int i = 0; i < realCount; i++)
                realPts[i] = new DCompositionInkTrailPoint
                {
                    X = real[i].X - OriginX,
                    Y = real[i].Y - OriginY,
                    // **逐点半径**（有压感时）：湿墨的粗细必须和干墨同一个映射，
                    // 不然"正在写的这一笔"和抬手之后那一条粗细不一样（最扎眼的一种不一致）。
                    Radius = R(i),
                };

            var predPts = Array.Empty<DCompositionInkTrailPoint>();
            if (predicted != null && predictedCount > 0)
            {
                float pr = R(realCount - 1);         // 预测段沿用**最后一点**的粗细
                predPts = new DCompositionInkTrailPoint[predictedCount];
                for (int i = 0; i < predictedCount; i++)
                    predPts[i] = new DCompositionInkTrailPoint
                    {
                        X = predicted[i].X - OriginX,
                        Y = predicted[i].Y - OriginY,
                        Radius = pr,
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
        // **原生墨迹**（压感 → 粗细）：`CreateInk` / `DrawInk` 在 `ID2D1DeviceContext2` 上
        // （D2D 1.3，Win10+）。拿不到就当作这台机器不支持，压感笔迹退回等宽描边
        // （启动日志里会打印一行，和"委托墨迹轨迹: 可用/不可用"一个样子）。
        try
        {
            _ctx2 = _ctx.QueryInterfaceOrNull<ID2D1DeviceContext2>();
            if (_ctx2 != null)
            {
                _inkStyle = _ctx2.CreateInkStyle(new InkStyleProperties
                {
                    NibShape = InkNibShape.Round,
                    // **必须显式给单位阵**：结构体的默认值里 `NibTransform` 是**全零矩阵**
                    // （不是单位阵），而零矩阵 = 笔尖被压成零尺寸 →
                    // 表现是"CreateInk / AddSegments / DrawInk 全部成功，屏幕上一个像素都没有"。
                    // 这个坑 2026-09-20 实测踩过一次（`--pressuretest` 数出全屏 0 像素）。
                    NibTransform = Matrix3x2.Identity,
                });
                InkAvailable = true;
                InkNote = "可用（D2D 原生墨迹 ID2D1Ink：压感按逐点半径改粗细）";
            }
            else
            {
                InkAvailable = false;
                InkNote = "不可用（拿不到 ID2D1DeviceContext2）→ 压感笔迹退回等宽描边";
            }
        }
        catch (Exception ex)
        {
            InkAvailable = false;
            InkNote = "不可用（" + ex.Message + "）→ 压感笔迹退回等宽描边";
        }
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
    /// <summary>
    /// **白板底纹（方格 / 横线）**。参考 InkClass 的 `MW_WhiteboardPattern.cs`：
    /// 三档（无/方格/横线）、线极细、线色随板面明暗自适应、**不是笔迹**
    /// （橡皮擦不掉、撤销不涉及、选择选不中）。
    ///
    /// 我们的实现比它省一档：它是给 `Border` 挂一个平铺画刷（固定屏幕上、不随滚动动），
    /// 我们是**画进分块缓存**——底纹在**画布坐标**里，所以：
    ///   · 滚动时底纹跟着板书一起走（写在横线上的字永远在那条线上，这才是"纸"的语义）；
    ///   · 滚动/翻页/写字都不重画底纹（块里已经烘好了）；
    ///   · 换底纹或换板色才整层重铺一次（和换板色本来就是同一件事）。
    ///
    /// 线宽 1 画布像素 = 200% 屏上的 0.5 逻辑像素，和 InkClass 的 0.5px 是同一个观感。
    /// </summary>
    private void DrawBoardPattern(InkEngine app, RectF canvas)
    {
        int kind = app.BoardPattern;
        if (kind == 0) return;
        float step = app.BoardPatternStepPx;
        if (step < 4f) return;

        var c = app.BoardColor;
        float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        // 浅板：灰 60%（InkClass 的 0x99,0x99,0x99,0x99）；深板：白 20%
        var line = lum > 0.5f
            ? new Color4(0.60f, 0.60f, 0.60f, 0.60f)
            : new Color4(1f, 1f, 1f, 0.20f);
        var brush = Brush(line);

        // 对齐到"第一页的左上角"（= 虚拟桌面左上角）：底纹与页界线共用同一个锚点，
        // 翻页之后横线还在原来的高度上，不会跟页边界错开。
        float left = app.PageLeftCanvas, top = app.PageTopCanvas;

        float firstY = MathF.Floor((canvas.MinY - top) / step) * step + top;
        for (float y = firstY; y <= canvas.MaxY + 0.5f; y += step)
        {
            if (y < canvas.MinY - 0.5f) continue;
            _ctx.DrawLine(new System.Numerics.Vector2(canvas.MinX, y),
                          new System.Numerics.Vector2(canvas.MaxX, y), brush, 1f);
        }

        if (kind != 1) return;                       // 横线只画横的，方格还要画竖的
        float firstX = MathF.Floor((canvas.MinX - left) / step) * step + left;
        for (float x = firstX; x <= canvas.MaxX + 0.5f; x += step)
        {
            if (x < canvas.MinX - 0.5f) continue;
            _ctx.DrawLine(new System.Numerics.Vector2(x, canvas.MinY),
                          new System.Numerics.Vector2(x, canvas.MaxY), brush, 1f);
        }
    }

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
    /// 白板时是（可调透明度的）板色。底色变了才重建画刷，正常每帧不分配。
    /// </summary>
    private ID2D1SolidColorBrush BoardBrush(InkEngine app)
    {
        // 白板可以整体调"不透明度"（用户 2026-09-17）：底色这一层半透明，
        // 下面的题目隐约透出来；**笔迹不受影响**（它画在底色上面、本身不透明）。
        var want = app.BoardOn
            ? new Color4(app.BoardColor.R, app.BoardColor.G, app.BoardColor.B,
                         app.BoardColor.A * app.BoardOpacity)
            : Transparent;
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

        // 白板模式：先画**底纹**（方格/横线），再画"页界线"（一屏一页）。
        // 两个都是**画布内容**——固定在图上的位置、不随相机动，
        // 所以一起烘进分块缓存：滚动、翻页、写字都不额外花钱。
        if (app.BoardOn)
        {
            DrawBoardPattern(app, canvas);
            DrawPageLines(app, canvas);
        }

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

    /// <summary>
    /// 倾斜角标签的盒子：贴在**正在拖的那个端点**外侧（挂在它上方），按文案量宽，
    /// 最后夹进当前可见画布。
    ///
    /// 和旋转标签同一套做法（连"按文案量宽"和夹取都照抄），只有锚点不同：
    /// 旋转标签挂旋转柄，这个挂在**指针此刻所在的那个端点**上——拖到哪儿跟到哪儿，
    /// 眼睛不用在两个地方来回找。
    /// </summary>
    private RectF InclinationReadoutRect(Vector2 anchor, float dpi, string text)
        => ReadoutPillRect(anchor, dpi, text, 30f, 84f, ReadoutFormat(dpi), PillPlace.Above);

    /// <summary>读数胶囊贴在锚点的哪一侧。</summary>
    private enum PillPlace
    {
        /// <summary>锚点正上方（倾斜角 / 顶点读数那一套）。</summary>
        Above,
        /// <summary>锚点左边、竖直方向对齐（角标：贴在那个角的外侧）。</summary>
        LeftOf,
        /// <summary>锚点右边、竖直方向对齐（角标）。</summary>
        RightOf,
    }

    /// <summary>
    /// 一颗读数胶囊的盒子：**按文案量宽**、贴到锚点的一侧、再夹进当前可见画布。
    ///
    /// 全工程只有这一份"量宽 + 夹取"的实现（倾斜角 α、圆的 r/d、椭圆 a/b、旋转 Δ，
    /// 以及第③轮的那些角标都走它）：那边当年就是"量一次、画另一份"出的残影，
    /// 分开写迟早会在盒子的宽窄上再犯一次。
    /// </summary>
    private RectF ReadoutPillRect(Vector2 anchor, float dpi, string text,
                                  float heightLogical, float minWidthLogical,
                                  IDWriteTextFormat fmt, PillPlace place)
    {
        float widthLogical = minWidthLogical;
        if (!string.IsNullOrEmpty(text))
        {
            float textW = MeasureTextWidth(text, fmt);
            widthLogical = MathF.Max(minWidthLogical, textW / dpi + 24f);
        }

        float w = widthLogical * dpi, h = heightLogical * dpi;
        float gap = (SelectionHandles.VisualSizeLogical * 0.5f + 9f) * dpi;
        float cx, cy;
        switch (place)
        {
            case PillPlace.LeftOf: cx = anchor.X - gap - w * 0.5f; cy = anchor.Y; break;
            case PillPlace.RightOf: cx = anchor.X + gap + w * 0.5f; cy = anchor.Y; break;
            default: cx = anchor.X; cy = anchor.Y - gap - h * 0.5f; break;    // Above
        }
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
        // 物理像素的占用矩形。**连"画到外面那一圈"一起算**（投影，见 IOverlayUi.PaintMargin）：
        // 不算进去的话，面板一移动（拖动/展开/收起），旧投影就擦不掉——脏区里没有它，
        // 屏幕上会留下一条越来越脏的印子。命中测试用的是上面那份**没放大的**逻辑矩形。
        float paintPad = app.UiPaintMarginNow * dpiScale;
        _uiBounds = live.IsEmpty ? RectF.Empty : new RectF
        {
            MinX = live.MinX * dpiScale - paintPad,
            MinY = live.MinY * dpiScale - paintPad,
            MaxX = live.MaxX * dpiScale + paintPad,
            MaxY = live.MaxY * dpiScale + paintPad,
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
        // **往外放一圈**：界面会画到占用矩形外面（投影、浮出的预览，见 IOverlayUi.PaintMargin）。
        // 占用矩形本身**不放**——它同时是命中测试与输入小窗的矩形。
        float pad = app.UiPaintMarginNow;
        var clip = new Vortice.RawRectF(_uiLogicalBounds.MinX - pad, _uiLogicalBounds.MinY - pad,
                                        _uiLogicalBounds.MaxX + pad, _uiLogicalBounds.MaxY + pad);
        _ctx.PushAxisAlignedClip(clip, AntialiasMode.Aliased);
        // 防弹入口在引擎那边（`UiRenderNow`）：界面连抛三次就整体停用，笔迹照常。
        // 主题参数给的是"当前浮层主题"（界面推上来的那套）——我们的界面自己带令牌，
        // 不读这个参数；但万一将来有个界面想用引擎给的配色，拿到的应该是同一套。
        app.UiRenderNow(_ctx, app.FloatingTheme);
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
        if (app.Ui == null) { w = h = 0; return null; }
        return RenderUiToBgra(app, padPx, app.UiQueryBoundsNow(), out w, out h);
    }

    /// <summary>
    /// 同上，但**画哪一块自己指定**（逻辑像素）。
    ///
    /// 界面自己的占用区只够拍那颗球和那条带子；像"选中框 + 操作条 + 导出格式面板"
    /// 这类画在浮动层上的东西压根不在里面，得把范围一起给进来。
    /// </summary>
    public byte[] RenderUiToBgra(InkEngine app, int padPx, in RectF bounds, out int w, out int h)
        => RenderToBgra(app, padPx, bounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: false, out w, out h);

    /// <summary>
    /// **浮动层**离屏出图：选中框、八个手柄、操作条、挂在条下面的小面板（颜色/层级/导出格式）。
    ///
    /// 和上面那条的区别只在**坐标系**：界面画在自己的逻辑屏幕坐标里（乘 DPI 就完事），
    /// 浮动层画的是**画布坐标**（相机还没减）。所以这里不乘缩放，直接把
    /// "画布坐标 → 位图"写出来，`bounds` 也要给画布坐标。
    ///
    /// 单开这条的理由和界面那条一样：这些家具全在浮动层上，桌面截图截不到干净的一版，
    /// 而它们的排版（字有没有居中、两条线离多远）**只能靠图看**，几何自检看不出来。
    /// </summary>
    public byte[] RenderFloatingToBgra(InkEngine app, int padPx, in RectF canvasBounds, out int w, out int h)
        => RenderToBgra(app, padPx, canvasBounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: true, out w, out h);

    private byte[] RenderToBgra(InkEngine app, int padPx, in RectF bounds, float dpiScale,
                                bool floatingCanvasSpace, out int w, out int h)
    {
        w = h = 0;
        if (_ctx == null) return null;
        // 浮动层这条路不画界面，所以不要求界面挂着；界面那条必须挂着才有东西可画。
        if (!floatingCanvasSpace && (app.Ui == null || !app.UiVisibleNow)) return null;
        if (bounds.IsEmpty) return null;
        _app = app;                 // 画的过程中有几处要读"当前窗口"的状态

        float dpi = dpiScale;
        // **两条路的单位不一样**（这一条踩过）：界面画在自己的**逻辑**坐标里，要乘 DPI 换成
        // 物理像素；画布坐标**本身就是物理像素**（上下文 DPI 固定 96，`CanvasToWindow`
        // 也不带缩放），所以 1 个单位就是 1 个位图像素。第一版给浮动层也乘了 DPI，
        // 位图开成两倍大、内容却按 1:1 画，整个画面被推到左边去了。
        float unit = floatingCanvasSpace ? 1f : dpi;
        w = (int)MathF.Ceiling((bounds.MaxX - bounds.MinX) * unit) + padPx * 2;
        h = (int)MathF.Ceiling((bounds.MaxY - bounds.MinY) * unit) + padPx * 2;
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

            if (floatingCanvasSpace)
            {
                // 画布坐标就是物理像素：只挪，不平移缩放（见上面 unit 那一段）。
                _ctx.Transform = Matrix3x2.CreateTranslation(padPx - bounds.MinX,
                                                             padPx - bounds.MinY);
                // 拍这一块里的墨（内容层），否则取景框、选中框都浮在空白上，
                // 看不出"框有没有圈住东西"。只画和这一块相交的那几条。
                foreach (var s in app.Doc.Strokes)
                    if (s.PaddedBounds.Intersects(bounds) && !app.IsContentDetached(s)) DrawStroke(s);
                // 拖端点手势中：模型还是旧几何，临时几何只活在浮动层上——
                // 出图这条路不补画它，拍出来的就是"手柄不见了、线也没动"的空镜头。
                // （被摘出去的那一批上面已经跳过，所以不会出现"旧线 + 新线"双影。）
                DrawVertexPreview(app);
                // **拖动预览**（移动/旋转手势中被摘出内容层的那一批）同理：
                // 不补画的话，"旋转中"那张图会只剩一个框、线不见了（2026-09-18 出图核对时踩到）。
                DrawDragPreview(app);
                // **正在书写的那一笔**（画线中的实时几何）也不在 Doc 里，得单独补——
                // 不然"画线过程中的 α 读数"这张图拍出来只有一颗标签、没有线。
                if (app.ActiveStroke != null && !app.SuppressActiveStroke) DrawStroke(app.ActiveStroke);
                DrawSelection(app);
                DrawShapeInclination(app);
                DrawCaptureRect(app);        // 截图取景框（含尺寸读数）——不在截图态就直接返回
            }
            else
            {
                // 和 DrawUi 同一套坐标：先乘 DPI，再把界面左上角挪到留白处
                _ctx.Transform = Matrix3x2.CreateScale(dpi)
                               * Matrix3x2.CreateTranslation(-bounds.MinX * dpi + padPx,
                                                             -bounds.MinY * dpi + padPx);
                app.UiRenderNow(_ctx, app.FloatingTheme);
            }
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
    /// 这一帧"**只画辅助几何、不画主体**"的那一个对象：
    /// 双曲线三步式的第一步（正在晃渐近线）——屏幕上先只出那两条虚线，
    /// 曲线要等第 2 下点击之后才出现（见 <see cref="InkEngine.HyperAsymptotePreviewOnly"/>）。
    ///
    /// 由 <see cref="RenderFrame"/> 每帧开头设一次，**不进数据对象、不进存档、也不影响导出**
    /// （导出走的是另一条渲染路径，根本不设它）。
    /// </summary>
    private Stroke _auxOnlyStroke;

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

        // **只画辅助几何、先不画主体**的那个判据（见 _auxOnlyStroke 那段说明）。
        bool auxOnly = ReferenceEquals(s, _auxOnlyStroke);
        var geo = auxOnly ? null : s.BuildGeometry(Gfx.D2DFactory);
        if (geo == null && !auxOnly) return;
        // **只画辅助几何、不画主体**：双曲线三步式的第一步（正在晃渐近线）——
        // 屏幕上先只出那两条虚线渐近线，曲线要等第 2 下点击之后才跟着指针出现
        //（照 InkClass 的第一笔，见 Engine.HyperAsymptotePreviewOnly）。
        // 主体不画时 `geo` 是 null，下面那两行描边自然跳过，辅助几何照画。
        if (geo != null)
        {
            // 两种画法：
            //   · 单点笔迹 → 几何本身就是一个圆，填充它（零长度的线描边什么都画不出来）；
            //   · 其余（笔迹的中心线、直线/矩形/椭圆/箭头）→ 统一交给 D2D 描边：
            //     宽度、端帽、拐角全由它算（2026-09-14 起，我们自己的轮廓代码已删除）；
            //     **线型（实线/虚线/点线）就在这一步按 <see cref="StrokeDash"/> 选描边样式** ——
            //     全引擎只此一处，任何"能画出图形的路径"（内容层、浮动预览、导出、自检出图）
            //     都经过 DrawStroke，所以线型自动都生效，不用逐处补。
            if (s.IsSinglePoint) _ctx.FillGeometry(geo, Brush(s.Color));
            else if (!DrawPressureInk(s))
                _ctx.DrawGeometry(geo, Brush(s.Color), MathF.Max(1f, s.Width), Gfx.StyleFor(s.Dash));
        }

        // **辅助几何**（双曲线的两条虚线渐近线，见 Stroke.BuildAuxGeometry）：
        // 同一支笔的墨色，但线型恒定是虚线、粗细取细的（辅助线的本分是不抢主线）。
        // 放在主几何之后画：两者重叠的地方（顶点附近）以曲线为准。
        var aux = s.BuildAuxGeometry(Gfx.D2DFactory);
        if (aux != null)
            _ctx.DrawGeometry(aux, Brush(s.Color), MathF.Max(1f, s.Width * 0.6f),
                              Gfx.StyleFor(StrokeDash.Dashed));
    }

    /// <summary>
    /// **有压感的自由笔迹**走 D2D 的原生墨迹（`ID2D1Ink`：每个点自带半径 → 变宽）。
    ///
    /// 为什么不自己拼变宽轮廓：官方对这套图元写得很直白——"比过去应用自己用一串椭圆和
    /// 四边形去管墨迹**更快也更漂亮**"，而我们 2026-09-14 删掉的那一层正是那种自拼轮廓
    /// （折角被削、自交挖洞、起笔毛边都是它带来的）。用它等于把"变宽"这件事交回给 D2D。
    ///
    /// **四个前提，缺一个就返回 false 走回等宽描边**（老那条路一个字没改）：
    ///   · 这台机器拿得到 `ID2D1DeviceContext2`（见 Gfx.InkAvailable）；
    ///   · 这一笔**真的有压感**（设备报的，不是我们猜的，见 Stroke.HasPressure）；
    ///   · **实线**——光栅化墨迹不吃 dash 图案，虚线/点线笔迹必须走老路；
    ///   · **没被像素橡皮擦断**（擦除区间是"按段不画"，而 ink 是整条一次性铺出来的）。
    ///
    /// 两个实现细节：
    ///   · **段数封顶 120**：压力是慢变量，长笔按步长抽稀即可（位置精度由 D2D 在段内插值补）。
    ///     抽稀也让"每条笔每次重画"的代价封住——我们不缓存 ink 对象（它只能从设备上下文建，
    ///     缓存就得按窗口分开管生命周期，不划算）；
    ///   · **压力先做一次指数平滑**（只在这一层做，存档里留的是原始值）：
    ///     10 bit 的原始压力在轻压段抖动明显，直接喂给宽度会"笔墨发毛"。
    /// </summary>
    private bool DrawPressureInk(Stroke s)
    {
        if (_ctx2 == null || _inkStyle == null) return false;
        if (!InkAvailable || !PressureWidth.Enabled) return false;
        if (!s.HasPressure || s.Dash != StrokeDash.Solid) return false;
        if (s.Erased.Count > 0 || s.Kind != StrokeKind.Freehand) return false;

        var pts = s.Points;
        int n = pts.Count;
        if (n < 2) return false;

        // 采样步长：保证段数 ≤ InkMaxSegments，且**最后一点一定画到**。
        int stride = Math.Max(1, (int)MathF.Ceiling((n - 1) / (float)InkMaxSegments));

        float sm = pts[0].P;
        float sx = pts[0].X, sy = pts[0].Y;
        float sr = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, sm));
        float startRadius = sr;

        int nSeg = 0;
        for (int i = 1; i < n && nSeg < _inkSegs.Length; i++)
        {
            sm += (pts[i].P - sm) * InkPressureEma;      // 平滑只作用于压力，不动位置
            if (i % stride != 0 && i != n - 1) continue; // 中间的按步长抽稀（末点必留）

            float ex = pts[i].X, ey = pts[i].Y;
            float er = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, sm));
            // 直线段写成三次贝塞尔：控制点落在两端之间 → 位置是直线，半径沿途线性插值。
            //
            // 注意**必须全限定** `Vortice.Direct2D1.InkPoint`：我们自己也有一个
            // `InkEngine.InkPoint`（笔迹的采样点 X/Y/P/T），写裸名会被它遮住，
            // 报错是"未包含 Radius 的定义"——一个不容易一眼看懂的编译错误。
            _inkSegs[nSeg++] = new InkBezierSegment
            {
                Point1 = new Vortice.Direct2D1.InkPoint
                {
                    X = sx + (ex - sx) / 3f, Y = sy + (ey - sy) / 3f,
                    Radius = sr + (er - sr) / 3f,
                },
                Point2 = new Vortice.Direct2D1.InkPoint
                {
                    X = sx + (ex - sx) * 2f / 3f, Y = sy + (ey - sy) * 2f / 3f,
                    Radius = sr + (er - sr) * 2f / 3f,
                },
                Point3 = new Vortice.Direct2D1.InkPoint { X = ex, Y = ey, Radius = er },
            };
            sx = ex; sy = ey; sr = er;
        }
        if (nSeg == 0) return false;

        try
        {
            var ink = _ctx2.CreateInk(new Vortice.Direct2D1.InkPoint
            {
                X = pts[0].X, Y = pts[0].Y, Radius = startRadius,
            });
            try
            {
                ink.AddSegments(_inkSegs, (uint)nSeg);
                PressureInkDraws++;
                _ctx2.DrawInk(ink, Brush(s.Color), _inkStyle);
            }
            finally { ink.Dispose(); }
            return true;
        }
        catch (Exception ex)
        {
            // **出问题就整体退回等宽描边，绝不静默画成空白**——而且只报一次、
            // 之后不再走这条路（否则每一帧都要抛一次，日志会被刷屏）。
            InkAvailable = false;
            InkNote = "运行中失败（" + ex.Message + "）→ 压感笔迹退回等宽描边";
            Console.WriteLine("墨迹通道（ID2D1Ink）失败：" + ex.Message + " → 压感笔迹退回等宽描边");
            return false;
        }
    }

    // ------------------------------------------------------------------
    //  Frame
    // ------------------------------------------------------------------

    public void RenderFrame(InkEngine app)
    {
        _app = app;
        s_frameNo++;

        // 双曲线三步式的第一步：**只画渐近线、先不画曲线**（见 _auxOnlyStroke 那段说明）。
        // 每帧在这里设一次——**只有真机渲染这条路会设**，导出/出图那条路不设，
        // 免得半成品在导出时被误当成"该隐藏主体"的对象。
        _auxOnlyStroke = app.HyperAsymptotePreviewOnly ? app.ActiveStroke : null;

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
            DrawVertexPreview(app);
            DrawSelection(app);
            // 画线中的 α 读数画在浮动层最上面（它贴着正在拖的那一端，压住什么都不碍事）。
            DrawShapeInclination(app);
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
            var capWin = CanvasRectToWindow(m).Inflate(4f);
            r.Add(capWin);
            // **取景框外面还有东西**：拖动中画在框左下角外侧的"宽 × 高"读数
            // （见 DrawCaptureRect）。它不在上面的矩形里，不单独加一块就会被脏区裁掉——
            // 自检当场量到 0 像素（"尺寸读数一个深色像素都没有"）。
            r.Add(new RectF
            {
                MinX = capWin.MinX, MinY = capWin.MaxY,
                MaxX = capWin.MinX + 100f * Dpi,
                MaxY = capWin.MaxY + 36f * Dpi,
            });
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
            // **余量 = 4 ＋ 投影最远伸出多少**：操作条现在带投影（主题给的），
            // 只放 4 像素的话投影会被脏区切掉、在屏幕上留一条印子。
            float cardPad = 4f + app.FloatingTheme.ShadowReachLogical * dpi;
            r.Add(CanvasRectToWindow(SelectionHandles.BarRect(sb, dpi, app.ViewportCanvas).Inflate(cardPad)));

            // 收起态的圆钮 / 打开的面板 / 提示条：都是"每帧都在变"的浮动层，
            // 漏一块就在屏幕上留一块擦不掉的残影（这条踩过好几次了）。
            r.Add(CanvasRectToWindow(SelectionHandles.BarCollapsedRect(sb, dpi, app.ViewportCanvas).Inflate(cardPad)));
            if (app.SelPanelOpen == SelPanel.Ink)
                r.Add(CanvasRectToWindow(SelectionHandles
                    .PanelRect(sb, dpi, app.ViewportCanvas, SelectionHandles.SwatchCount)
                    .Inflate(cardPad + 2f)));
            else if (app.SelPanelOpen == SelPanel.Layer)
                r.Add(CanvasRectToWindow(SelectionHandles.LayerPanelRect(sb, dpi, app.ViewportCanvas)
                    .Inflate(cardPad + 2f)));

            // 旋转读数标签贴在旋转手柄外侧，比选中框本身还高出去一截，
            // 同样必须进脏区；拖动中它每帧都在动，靠 _transientHistory 回溯两帧。
            // **文案要和绘制用同一个函数**：单选直线时它是 `α = xx.x°`、别的对象是 `Δ`，
            // 两者宽度不同——脏区按哪个文案算，就得画哪个文案。
            if (app.SelRotating)
                r.Add(CanvasRectToWindow(RotationReadoutRect(frame, dpi, CurrentRotationReadout(app)).Inflate(3f)));

            // 拖定义元素 / 拖四角时的读数胶囊：挂在"正在拖的那个元素"上方，每帧都跟着指针走，
            // 而且它在元素外侧、根本不在选中框里——漏了这一块就会在屏幕上留一行残影。
            // **文案与绘制同一个函数**（圆的 `r = / d =`、直线的 `α = `、
            // 吸住时的"等边/正方形"宽度都不一样）。
            if (app.VertexDragging || app.ShapeSnapKind != ShapeSnapKind.None)
            {
                var pill = VertexPillRect(app);
                if (!pill.IsEmpty) r.Add(CanvasRectToWindow(pill.Inflate(3f)));
            }

            // 多边形角标（规格 9.7）：三角形的**选中就显示**，而且每一颗都挂在顶点外侧
            // （横着让到边的外面）、根本不在选中框里——不单独加进来就会被脏区裁掉，
            // 屏幕上留下几行擦不掉的数字。**盒子和绘制走同一个函数**（FillAnglePills）。
            for (int i = 0, np = FillAnglePills(app, _anglePills); i < np; i++)
                r.Add(CanvasRectToWindow(_anglePills[i].Rect.Inflate(3f)));
        }

        // 画线过程中的 α 读数（用户 2026-09-18 四条之一）。
        // **必须在"有选中对象"那块之外**：画线的时候没有选中对象（没做画完自动选中），
        // 挂在里面的话这块脏区永远不会被算进来，标签就会在屏幕上留一行残影。
        if (app.ShapeInclinationActive)
            r.Add(CanvasRectToWindow(InclinationReadoutRect(
                app.ShapeInclinationAnchor, app.DpiScale,
                InclinationLabel(app.ShapeInclinationDegrees)).Inflate(3f)));

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
        // **先问"拖动预览到底活着没有"**（DragPreviewActive），不能只看"摘出去了几条"：
        // 拖端点的手势复用了同一套"摘出去"的机制（DetachForDrag），但那时画在浮动层上的
        // 是**临时几何**（DrawVertexPreview），不是"这批对象按实时矩阵位移"。
        // 少了这一句，拖端点时会用**单位矩阵**把那条旧线再画一遍——屏幕上就是"旧线不动 +
        // 新线跟着指针"两条线，而内容层明明已经跳过它了（2026-09-18 出图核对时抓到）。
        if (!app.DragPreviewActive) return;

        var strokes = app.DragPreviewStrokes;
        if (strokes.Count == 0) return;

        var canvasToWindow = _ctx.Transform;                 // 此刻是 CanvasToWindow
        _ctx.Transform = app.DragPreviewMatrix * canvasToWindow;
        foreach (var s in strokes) DrawStroke(s);
        _ctx.Transform = canvasToWindow;
    }

    /// <summary>
    /// 拖端点时的**临时几何**：手势期模型一个字不改，只在浮动层把
    /// "改过端点的那一条图形"按**实时端点**画出来（见 计划-图形工具.md 8.1①）。
    ///
    /// 和拖动预览是同一套路子（画在浮动层、松手回归内容层），区别只有一个：
    /// 那边改的是**变换矩阵**，这里改的是**几何**——所以画的是同一条对象、换一组点，
    /// 变换仍用它自己那个（局部坐标 → 画布）。
    /// </summary>
    private void DrawVertexPreview(InkEngine app)
    {
        var src = app.VertexPreviewStroke;
        if (src == null) return;
        var pts = app.VertexPreviewPoints;
        if (pts == null || pts.Count == 0) return;

        _vertexGhost ??= new Stroke();
        var g = _vertexGhost;
        g.Tool = src.Tool;
        g.Kind = src.Kind;
        g.Color = src.Color;
        g.Width = src.Width;
        g.Dash = src.Dash;          // 线型也要跟着，不然拖一条虚线时预览会突然变实线
        // **曲线朝向也必须跟着**：抛物线开了哪个口 / 双曲线哪条是实轴，
        // 是这个对象几何的一部分。漏了它，拖动预览会按"默认那一档"画，
        // 屏幕上就是"一个朝上的抛物线预览、松手变成朝右的"。
        g.CurveAxis = src.CurveAxis;
        g.Transform = src.Transform;
        g.SetPoints(pts);           // 局部坐标：变换那一层仍由 g.Transform 负责
        DrawStroke(g);
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

        // 单选一个"有定义元素的图形"（直线 / 箭头 / 圆 / 椭圆）时，手柄按定义元素给
        // （见下面第 3、4 步）：两个或五个，而不是通用 8 个。
        bool shapeLike = SelectionHandles.ShapeEditable(sel, out var shapeStroke);

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

        // 3) 旋转手柄（在图形的上方外侧，先画连线再画圆）
        //    拖动中收起来，但**旋转中要留**（此刻它就是"正在抓的那个东西"）。
        //    **圆不给旋转柄**（用户定："圆转了看不出来"）——画都不画，命中也一样。
        var white = Brush(new Color4(1f, 1f, 1f, 1f));
        if (!collapsed || app.SelRotating)
        {
            if (!shapeLike || SelectionHandles.RotateHandleVisible(shapeStroke))
            {
                float rotR = SelectionHandles.RotateGripLogical * 0.5f * dpi;
                // 旋转柄的位置：直线/箭头/椭圆三种"定义元素图形"也用**通用框**的算法
                // （框上边中点外再抬一段）——它只是个抓手，位置跟着框走最稳，不用另算一套。
                var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
                // 连线的那一头：通用框用它上边中点；图形用**它自己的中心**——
                // 一条斜线的包围盒上边中点根本不在线上，连线看着像连到别的东西上去了。
                var topCenter = shapeLike
                    ? RotationGripAnchor(shapeStroke)
                    : SelectionHandles.CanvasPosition(SelHandle.Top, frame, dpi);
                _ctx.DrawLine(topCenter, rot, _scratch, 1.4f);
                _ctx.FillEllipse(new Ellipse(rot, rotR, rotR), white);
                _ctx.DrawEllipse(new Ellipse(rot, rotR, rotR), _scratch, 1.6f);
                // 圆里放**官方图标**，不是手画一段弧。
                // 手画那版在投影上看像个"©"——旋转图标的识别特征就是那个箭头，
                // 少一笔就不成形。这是"图标别自己画"的又一个实例。
                float glyph = SelectionHandles.RotateGlyphLogical * dpi;
                DrawIcon(IconPaths.rotate, rot.X - glyph * 0.5f, rot.Y - glyph * 0.5f, glyph, _scratch);
            }
        }

        // 4) 手柄。两种给法（见 计划-图形工具.md 9.1）：
        //    · **有定义元素的图形**（单选直线 / 箭头 / 圆 / 椭圆）：按**定义元素**给手柄——
        //      直线/箭头/圆两个、椭圆五个（中心 + 四个轴端点）；椭圆**不给外角点**
        //      （用户定：四个轴端点已经把拉伸给全了）。8 个缩放柄对它们是多余甚至是错的
        //      （左右拉伸一条线会顺手改掉倾斜角；拉一个圆会把它拉成椭圆）。
        //    · 其余（矩形 / 图像 / 自由笔迹 / **多选**）：通用 8 手柄，一个字不改。
        //      矩形保留它们是因为它是唯一"拉了还是矩形"的图形（用户定）。
        //    白底 + 蓝边：深色背景上是白方块显眼，浅色背景上靠蓝边立住，一套画法两边都成立。
        //    拖动 / 旋转 / 拖元素中收起来（此刻点不中，而且是最"晃眼"的一圈家具）。
        if (!collapsed)
        {
            float hs = SelectionHandles.VisualSizeLogical * dpi;
            float radius = hs * 0.28f;
            if (shapeLike)
            {
                Span<ShapeHandle> handles = stackalloc ShapeHandle[5];
                int n = SelectionHandles.ShapeHandlesOf(shapeStroke, handles);
                for (int i = 0; i < n; i++)
                    DrawHandleSquare(SelectionHandles.ShapeHandleCanvasPosition(shapeStroke, handles[i]),
                                     hs, radius, white);
            }
            else
            {
                Span<SelHandle> all = stackalloc SelHandle[]
                {
                    SelHandle.TopLeft, SelHandle.Top, SelHandle.TopRight, SelHandle.Right,
                    SelHandle.BottomRight, SelHandle.Bottom, SelHandle.BottomLeft, SelHandle.Left,
                };
                foreach (var h in all)
                    DrawHandleSquare(SelectionHandles.CanvasPosition(h, frame, dpi), hs, radius, white);
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

        // 6) 浮出的读数标签。**两个数分开显示**（见 调研-图形工具.md 2.3）：
        //    · Δ = 我这一次**转了多少**（逆时针为正、不设上限）→ 拖旋转手柄时出现，
        //      **单选直线/箭头时例外**：那时读的是 `α₀ + Δ`（从这条线原有的倾斜角接着转、
        //      同样不设上限，用户 2026-09-18 澄清），写成纯数字（见 CurrentRotationReadout）；
        //    · α = 这条线**本身**的倾斜角（[0°,180°)，永远非负）→ 画线中、拖端点时出现。
        //    两件事都必须说清楚"这个角度是不是吸出来的"：靠颜色——吸住时整块变强调色
        //    （Figma / Office 也是这个语言）。没有这层提示，用户分不清"我自己拖到的"
        //    和"它替我吸上的"。
        if (app.SelRotating)
        {
            // 同一个文案：盒子按它量宽，字也画它。**改一处就得改两处**的地方收成一个函数
            // （CurrentRotationReadout 也被 ComputeTransientBounds 用来算脏区）。
            string readout = CurrentRotationReadout(app);
            DrawReadoutPill(RotationReadoutRect(frame, dpi, readout), readout, app.SelRotationSnapped);
        }
        else if (app.VertexDragging || app.ShapeSnapKind != ShapeSnapKind.None)
        {
            // 拖的是**定义元素 / 四角**：直线/箭头读 α、圆读 r+d、椭圆读 a 或 b，
            // 吸住特殊形状时改读"吸到了什么"（见 VertexReadoutLabel）。
            // 三角形 / 平行四边形没吸住时**什么都不画**（它们的读数是下面的角标那一组）。
            var pill = VertexPillRect(app);
            if (!pill.IsEmpty)
                DrawReadoutPill(pill, VertexReadoutLabel(app), VertexPillSnapped(app));
        }

        // 7) 多边形读数（规格 9.7）：三角形的**三个内角**、平行四边形的**两个夹角**。
        //    它不在上面那两个分支里，因为三角形的这一组**选中就显示**（用户定），
        //    静止选中时也要画；平行四边形那一组只在拖顶点时出现（由 FillAnglePills 自己判）。
        DrawAnglePills(app);
    }

    /// <summary>
    /// 旋转中该显示哪个数：
    ///   · **单选直线/箭头** → `按下时的倾斜角 α₀ + 转过的角 Δ`，**纯数字、一位小数、
    ///     不折角**（`405.0°` / `-135.0°` / `45.0°`）。写成纯数字而不是 `α = …` 是有意的：
    ///     405° 已经超出倾斜角的定义域（[0°,180°)），标成 α 反而错；而且用户说
    ///     "其他和原来的逻辑一样"，原来那套读数就是纯数字。
    ///   · **单选一个图形**（矩形/椭圆/三角形/平行四边形）→ `姿态 = 0.0°`：这个图形
    ///     **相对水平的姿态角**（折在 [0°,180°)，0° = 正的、90° = 竖的）。用户要的用途是
    ///     "歪的椭圆/矩形，看着读数拖到 0° 就转正了"——所以它和 α 只是名字不同、长相一样。
    ///   · **其它情况** → Δ（`FormatDegrees`，整度）：一行都没改。
    /// **绘制与脏区都走它**，免得两边文案不一致
    /// （文案不一致 → 盒子宽度不一致 → 脏区盖不住标签 → 屏幕上留残影）。
    /// </summary>
    private static string CurrentRotationReadout(InkEngine app)
        => app.SelRotationReadsPose
            ? SelectionHandles.FormatPose(app.SelRotationPose)
            : app.SelRotationReadsInclination
                ? SelectionHandles.FormatSignedDegrees(app.SelRotationInclination)
                : RotationLabel(app);

    /// <summary>
    /// **画线过程中的 α 读数**：用户在拉这条线的时候就要看到它现在是多少度
    /// （2026-09-18 四条之一）。贴在被拖动的那一端外侧，和拖端点时是同一颗胶囊。
    ///
    /// 为什么单独一个方法、不挂在 DrawSelection 里：画线的时候**没有选中对象**
    /// （我们没做"画完自动选中"），DrawSelection 第一行就返回了。
    /// </summary>
    private void DrawShapeInclination(InkEngine app)
    {
        if (!app.ShapeInclinationActive) return;
        float dpi = app.DpiScale;
        string readout = InclinationLabel(app.ShapeInclinationDegrees);
        DrawReadoutPill(InclinationReadoutRect(app.ShapeInclinationAnchor, dpi, readout),
                        readout, app.ShapeInclinationSnapped);
    }

    /// <summary>
    /// 旋转柄那根连线在**图形这一头**挂哪儿：
    ///   · 直线 / 箭头：两个端点的中点（斜线的包围盒上边中点根本不在线上）；
    ///   · 椭圆：它的**中心**（那是它自己的"中心"，框的中心在旋转后不是它）；
    ///   · 三角形 / 平行四边形：三个（四个）顶点的**形心**——"多边形自己的中心"就是它，
    ///     拿某一个顶点当挂点会让那根线看着像挂歪了。
    /// 圆不给旋转柄，走不到这里。
    /// </summary>
    private static Vector2 RotationGripAnchor(Stroke s)
    {
        if (s.Kind == StrokeKind.Ellipse) return s.ShapeCenterLocal is var c
            ? Vector2.Transform(c, s.Transform) : Vector2.Zero;
        if (s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram && s.Points.Count >= 3)
        {
            var sum = new Vector2(s.Points[0].X, s.Points[0].Y)
                    + new Vector2(s.Points[1].X, s.Points[1].Y)
                    + new Vector2(s.Points[2].X, s.Points[2].Y);
            if (s.Kind == StrokeKind.Parallelogram) sum += s.ParallelogramFourthLocal();
            float n = s.Kind == StrokeKind.Parallelogram ? 4f : 3f;
            return Vector2.Transform(sum / n, s.Transform);
        }
        var a = SelectionHandles.ShapeHandleCanvasPosition(s, ShapeHandle.Anchor);
        var r = SelectionHandles.ShapeHandleCanvasPosition(s, ShapeHandle.Rim);
        return (a + r) * 0.5f;
    }

    /// <summary>
    /// 拖定义元素 / 拖四角时那颗胶囊的**文案**。五种量（α / r+d / a / b / 吸到了什么）
    /// 都在这里成型，而且**绘制与脏区都调它**——文案一变宽窄就变，
    /// 脏区按哪个文案算就得画哪个文案。**没有可显示的返回 null**（这一帧不画胶囊）。
    ///
    /// 优先级：**特殊形状吸附**（规格 9.6）> 老读数。理由是"吸到了什么"是**这一刻最有用的
    /// 消息**——它回答"我这一下拖出了个什么形状"，而 α / a / b 只是当下这个数。
    /// </summary>
    private static string VertexReadoutLabel(InkEngine app)
    {
        if (app.ShapeSnapKind != ShapeSnapKind.None)
            return SelectionHandles.ShapeSnapLabel(app.ShapeSnapKind);
        return app.VertexReadout switch
        {
            // 圆：两个数一起给（半径是"拉多大"，直径是老师嘴里常说的那个数）
            InkEngine.VertexReadoutKind.Radius =>
                $"r = {app.VertexReadoutValue:F1}  d = {app.VertexReadoutSecondary:F1}",
            InkEngine.VertexReadoutKind.AxisA => $"a = {app.VertexReadoutValue:F1}",
            InkEngine.VertexReadoutKind.AxisB => $"b = {app.VertexReadoutValue:F1}",
            // 抛物线：报课本里那个 p（焦点、准线都从它来，见 VertexReadoutKind 的注释）。
            InkEngine.VertexReadoutKind.ParabolaP => $"p = {app.VertexReadoutValue:F1}",
            // 双曲线：**说清是实半轴还是虚半轴**——方向一换，这两个名字会互换，
            // 光写个 a / b 老师会在另一个朝向下看错（这是本族唯一一处"名字会漂"的地方）。
            InkEngine.VertexReadoutKind.HyperbolaReal => $"a = {app.VertexReadoutValue:F1}（实半轴）",
            InkEngine.VertexReadoutKind.HyperbolaImag => $"b = {app.VertexReadoutValue:F1}（虚半轴）",
            // 正弦 / 余弦：峰点 / 谷点那两个手柄是**两个量一起动**的
            //（纵向 = 振幅、横向 = 周期），所以两个数都报出来。
            InkEngine.VertexReadoutKind.WavePeriod =>
                $"T = {app.VertexReadoutValue:F1}  A = {app.VertexReadoutSecondary:F1}",
            // 直线/箭头：倾斜角 α（[0°,180°)），和旋转读数 Δ 是两个数
            InkEngine.VertexReadoutKind.Inclination => InclinationLabel(app.VertexReadoutValue),
            // 三角形 / 平行四边形没吸住：这一颗胶囊没有可显示的量——它们要显示的是
            // 内角 / 夹角，那是**另一组**角标（见 DrawAnglePills，第③轮）。
            _ => null,
        };
    }

    /// <summary>
    /// 拖定义元素 / 拖四角时那颗胶囊**占的那块矩形**（空 = 这一帧就没有胶囊）。
    ///
    /// 绘制与脏区都走它：文案、锚点、宽度三处**必须同源**——分开算的话，
    /// 脏区按一个宽度算、画按另一个画，屏幕上就会剩下一条擦不掉的边。
    /// 锚点也在这里定：吸住时贴"吸住的那个点"，否则贴被拖的元素。
    /// </summary>
    private RectF VertexPillRect(InkEngine app)
    {
        string text = VertexReadoutLabel(app);
        if (string.IsNullOrEmpty(text)) return RectF.Empty;
        var at = app.ShapeSnapKind != ShapeSnapKind.None
            ? app.ShapeSnapAnchor
            : app.VertexPreviewCanvasPoint;
        return InclinationReadoutRect(at, app.DpiScale, text);
    }

    /// <summary>这颗胶囊是不是"吸出来的"（吸住时整块变强调色）。</summary>
    private static bool VertexPillSnapped(InkEngine app)
        => app.ShapeSnapKind != ShapeSnapKind.None || app.VertexInclinationSnapped;

    // =====================================================================
    //  多边形读数（规格 9.7）：三角形的三个内角 / 平行四边形的两个夹角
    //
    //  这一组不是"一颗胶囊"，是**好几颗**（最多三颗），所以另有一套"这一帧有哪些胶囊"
    //  的收集函数：FillAnglePills。**画与脏区都只调它一次**——盒子的位置、大小、文案
    //  全在那一份里定死，两边各算一份的话，脏区会按一个大小擦、按另一个大小画，
    //  拖久了屏幕上就留一条边（选中框那一套 UI 踩过这个坑）。
    //
    //  **不再有"内角和"那一行**（用户 2026-09-19 定：显示和太乱）。为它服务的
    //  0.1° 配平也一起撤了（见 SelectionHandles.PolygonAngles）。
    // =====================================================================

    /// <summary>一帧最多几颗角标：三角形三个角 / 平行四边形两个角。</summary>
    private const int MaxAnglePills = 3;

    /// <summary>一颗角标：盒子 + 文案 + 是不是小号（画与脏区共用同一个实例）。</summary>
    private readonly struct AnglePill
    {
        public readonly RectF Rect;
        public readonly string Text;
        public readonly bool Small;
        public AnglePill(in RectF rect, string text, bool small)
        {
            Rect = rect; Text = text; Small = small;
        }
    }

    /// <summary>角标与角度的复用缓冲（每帧重填，不 new，理由同"手柄命中每帧都跑"那条）。</summary>
    private readonly Vector2[] _angleVerts = new Vector2[4];
    private readonly float[] _angleDegs = new float[3];
    private readonly AnglePill[] _anglePills = new AnglePill[MaxAnglePills];

    /// <summary>
    /// 这一帧该画哪些角标（规格 9.7），返回几颗。**绘制与脏区唯一的来源。**
    ///
    /// 摆位规则：每个角标贴在它那个顶点的**外侧**，而且**只往左右放**（不往上下放）。
    /// 两个理由，都是被现场那两样家具逼出来的：
    ///   · 正上方那一块是**旋转柄**的（框上边中点往上 30 逻辑像素，柄 + 连线都在那儿）；
    ///   · 正下方那一块是**操作条**的（框下边往下 14 逻辑像素，而且横跨整个选区宽度）。
    /// 左右放还顺带保证了"不遮住要讲的那条边/那个顶点"：顶点就是图形在那一侧的最外点，
    /// 让到它外面去，两条边和顶点都在视野里。
    ///
    /// 大小：一律小号字（贴在顶点旁边，太大了会压住那条边）。
    /// </summary>
    private int FillAnglePills(InkEngine app, Span<AnglePill> dst)
    {
        int n = app.FillAngleReadout(_angleVerts, _angleDegs);
        if (n <= 0 || dst.Length < n) return 0;

        float dpi = app.DpiScale;
        var small = ReadoutFormatSmall(dpi);

        // 形心：判"这个顶点在图形的哪一侧"，决定角标往左还是往右放。
        // （三角形用三个顶点；平行四边形只报两角、就按那两个顶点的中点分左右，够用。）
        var centroid = Vector2.Zero;
        for (int i = 0; i < n; i++) centroid += _angleVerts[i];
        centroid /= n;

        for (int i = 0; i < n; i++)
        {
            string text = SelectionHandles.FormatAngleDegrees(_angleDegs[i]);
            var place = _angleVerts[i].X >= centroid.X ? PillPlace.RightOf : PillPlace.LeftOf;
            dst[i] = new AnglePill(
                ReadoutPillRect(_angleVerts[i], dpi, text, 22f, 44f, small, place), text, true);
        }
        return n;
    }

    /// <summary>画那一组角标（盒子与文案来自 <see cref="FillAnglePills"/>，和脏区同一份）。</summary>
    private void DrawAnglePills(InkEngine app)
    {
        int n = FillAnglePills(app, _anglePills);
        for (int i = 0; i < n; i++)
            DrawReadoutPill(_anglePills[i].Rect, _anglePills[i].Text, false, _anglePills[i].Small);
    }

    /// <summary>
    /// 倾斜角标签的文案（画它、按它量盒子宽度、进脏区，都走这一个函数）。
    ///
    /// 参数是**那个要显示的 α**（画线中 / 拖直线端点 / 单选直线拖旋转柄三个来源）——
    /// 不在这里读 app 的状态，是因为"该显示哪个来源的 α"由调用点最清楚。
    /// </summary>
    private static string InclinationLabel(float degrees)
        => SelectionHandles.FormatInclination(degrees);

    /// <summary>
    /// 一个方形手柄（白底 + 蓝边）。八个通用手柄和两个端点手柄**共用这一份画法**：
    /// 端点手柄只是位置不同，长得不一样会让用户以为是两种东西。
    /// </summary>
    private void DrawHandleSquare(Vector2 p, float size, float radius, ID2D1SolidColorBrush white)
    {
        var box = new Vortice.RawRectF(p.X - size * 0.5f, p.Y - size * 0.5f,
                                       p.X + size * 0.5f, p.Y + size * 0.5f);
        var rr = new RoundedRectangle(box, radius, radius);
        _ctx.FillRoundedRectangle(rr, white);
        _ctx.DrawRoundedRectangle(rr, _scratch, 1.8f);
    }

    /// <summary>
    /// 画一颗"读数胶囊"（旋转 Δ 与倾斜角 α 共用这一份画法）。
    ///
    /// 抽出来是因为两处必须**长得一模一样**：它们会在同一条直线上先后出现
    /// （先转一下、再拖端点），样式差一点用户就会以为是两种东西。
    /// <paramref name="snapped"/> 为真时整块用强调色——"这个数是吸出来的"。
    /// <paramref name="small"/> 为真时用小一号字（三角形的三个**角标**：
    /// 它们贴在顶点旁边，太大了会压住那条边）。
    /// </summary>
    private void DrawReadoutPill(in RectF label, string text, bool snapped, bool small = false)
    {
        // **这里要的是"缩放倍数"（96 DPI 基准），不是 Dpi 那个原始值**：
        // Dpi 是窗口的物理 DPI（200% 屏上就是 192），拿它去 CreateTextFormat
        // 会造出一个 2880 像素高的字号，字直接糊满整个画面（踩过）。
        float dpi = _app.DpiScale;
        var accent = new Color4(0f, 0.47f, 0.83f, 1f);      // #0078D4
        var box = new Vortice.RawRectF(label.MinX, label.MinY, label.MaxX, label.MaxY);
        float pill = (label.MaxY - label.MinY) * 0.5f;
        var shape = new RoundedRectangle(box, pill, pill);

        _ctx.FillRoundedRectangle(shape, Brush(snapped
            ? new Color4(accent.R, accent.G, accent.B, 0.96f)
            : _app.FloatingTheme.Panel));
        _scratch.Color = snapped
            ? new Color4(1f, 1f, 1f, 0.85f)
            : new Color4(accent.R, accent.G, accent.B, 0.85f);
        _ctx.DrawRoundedRectangle(shape, _scratch, 1.5f);

        _scratch.Color = snapped
            ? new Color4(1f, 1f, 1f, 1f)
            : _app.FloatingTheme.Text;
        _ctx.DrawText(text, small ? ReadoutFormatSmall(dpi) : ReadoutFormat(dpi),
                      // 注意：Vortice 的 Rect(x, y, width, height) 是"位置 + 尺寸"，
                      // 不是 (left, top, right, bottom)。写错的话文字会被排到很远的
                      // 地方去（居中排版时直接跑到屏幕外），看起来就像"字没画出来"。
                      new Rect(label.MinX, label.MinY,
                               label.MaxX - label.MinX, label.MaxY - label.MinY),
                      _scratch);
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
    /// **角标**用的小号文字格式（11 逻辑像素，比主读数小一号）。
    ///
    /// 和 <see cref="ReadoutFormat"/> 同一套按 DPI 生成 + 缓存的写法（理由见那里）：
    /// 量盒子和画字必须用**同一个格式**，量出来的宽度才和画出来的一致。
    /// </summary>
    private IDWriteTextFormat ReadoutFormatSmall(float dpi)
    {
        float px = MathF.Max(9f, MathF.Round(11f * dpi));
        if (_readoutFormatSmall == null || _readoutFormatSmallPx != px)
        {
            _readoutFormatSmall?.Dispose();
            _readoutFormatSmall = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
                FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
            _readoutFormatSmall.TextAlignment = TextAlignment.Center;
            _readoutFormatSmall.ParagraphAlignment = ParagraphAlignment.Center;
            _readoutFormatSmallPx = px;
        }
        return _readoutFormatSmall;
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
        // 圆角**用界面那套令牌**（UiTheme.CornerRadius = 10），不自己发明一个胶囊：
        // 工具条、操作条、面板三样东西的圆角必须是同一个数，看着才是一家的。
        float radius = MathF.Min(app.FloatingTheme.CornerRadius * dpi, (rect.MaxY - rect.MinY) * 0.5f);

        // 配色与投影**全取界面推上来的主题**（UiTheme.Default 只是没界面时的兜底）：
        // 操作条和工具条必须是同一套颜色，各写一份迟早会花
        // （用户 2026-09-18 实测：浅色主题改完之后，工具条有投影、操作条没有，一眼就看得出）。
        DrawPanelCard(app, rect, radius);
        var theme = app.FloatingTheme;

        const int n = SelectionHandles.BarButtonCount;
        float glyphBox = SelectionHandles.BarIconBoxLogical * dpi;   // 图标框比字形大一点

        for (int i = 0; i < n; i++)
        {
            var btn = SelectionHandles.BarButtonRect(i, sel, dpi, visible);
            var kind = (SelBarButton)i;
            bool hot = app.SelBarHover == i;
            bool active = IsBarButtonActive(app, kind);
            // 禁用（图标改用 theme.TextMuted）：九格都是通用操作（收起/颜色/锁定/层级/
            // 导出/复制/翻转×2/删除），对任何选中对象都成立，所以**当前没有一格会灰**。
            // 这一格留着是给"以后真需要禁用"的位置——2026-09-20 加过又撤掉的
            // 「开口方向」就是靠它灰的（现在那格整个没了，朝向改到图形面板里定，
            // 见 Engine.CycleParabolaAxis）。
            bool disabled = false;

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
        bool any = false;
        foreach (var s in app.Doc.Selected)
        {
            if (s.IsImage) continue;
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
        var th = app.FloatingTheme;
        _scratch.Color = hot ? th.Hover : th.Panel;
        _ctx.FillEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawEllipse(new Ellipse(c, d * 0.5f, d * 0.5f), _scratch, 1f * dpi);

        _scratch.Color = th.Text;
        float arm = d * 0.19f, w = 1.8f * dpi;
        _ctx.DrawLine(new Vector2(c.X - arm, c.Y), new Vector2(c.X + arm, c.Y), _scratch, w);
        _ctx.DrawLine(new Vector2(c.X, c.Y - arm), new Vector2(c.X, c.Y + arm), _scratch, w);
    }

    /// <summary>
    /// 浮动面板 / 操作条的卡片底：**主题给的投影 ＋ 底 ＋ 描边 ＋（浅色的）底沿内阴影**。
    ///
    /// 数字全部来自 `app.FloatingTheme`，而那个主题是**界面推上来的**（`InkUi.Tokens`）——
    /// 所以浮层和工具条永远是一套，不会再出现"工具条换了投影、操作条还是老样子"。
    /// 没有界面挂上来时用 `UiTheme.Default` 兜底（自检宿主）。
    /// </summary>
    private void DrawPanelCard(InkEngine app, in RectF r, float radius)
    {
        float dpi = Dpi / 96f;
        var th = app.FloatingTheme;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);

        // **投影按形状大小缩一缩**：这套胀幅（最远 16 逻辑像素）是按 48 高的面板定的，
        // 直接套在一个 28 像素的小圆（收起后那颗球）上会变成一大团光晕。
        // 规则：比 48 矮的按比例缩，不超过 1。
        float shadowK = MathF.Min(1f, (r.MaxY - r.MinY) / 48f);
        for (int i = th.Shadow.Length - 1; i >= 0; i--)
        {
            var layer = th.Shadow[i];
            float inf = layer.Inflate * dpi * shadowK;
            float dy = layer.Dy * dpi * shadowK;
            _scratch.Color = layer.Color;
            _ctx.FillRoundedRectangle(new RoundedRectangle(
                new Vortice.RawRectF(r.MinX - inf, r.MinY - inf + dy, r.MaxX + inf, r.MaxY + inf + dy),
                radius + inf, radius + inf), _scratch);
        }

        _scratch.Color = th.Panel;
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch);
        _scratch.Color = th.PanelBorder;
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, radius, radius), _scratch, 1f * dpi);

        // 底沿那道"卷边"（和工具条同一个做法：裁一条横带、在带子里描一遍圆角矩形，
        // 这样两端顺着圆角收进去，不会戳出轮廓外）。深色主题这个是全透明的，等于不画。
        if (th.EdgeBottom.A > 0.001f)
        {
            float y = r.MaxY - 1f * dpi;
            _ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, y - 0.75f * dpi, r.MaxX, y + 0.75f * dpi),
                                     AntialiasMode.Aliased);
            var edge = new Vortice.RawRectF(r.MinX + 0.5f * dpi, r.MinY + 0.5f * dpi,
                                            r.MaxX - 0.5f * dpi, r.MaxY - 0.5f * dpi);
            _scratch.Color = th.EdgeBottom;
            _ctx.DrawRoundedRectangle(new RoundedRectangle(edge, radius - 0.5f * dpi, radius - 0.5f * dpi),
                                      _scratch, 1f * dpi);
            _ctx.PopAxisAlignedClip();
        }
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
        DrawPanelCard(app, p, 10f * dpi);

        // ---- ① 粗细滑条 ----
        var slider = SelectionHandles.SliderRect(sel, dpi, app.ViewportCanvas, sc);
        int steps = app.SliderStepCount();
        int cur = app.SliderStepOfSelection(sel);
        float cy = (slider.MinY + slider.MaxY) * 0.5f;
        float x0 = SelectionHandles.SliderStepX(0, steps, sel, dpi, app.ViewportCanvas, sc);
        float x1 = SelectionHandles.SliderStepX(steps - 1, steps, sel, dpi, app.ViewportCanvas, sc);
        var th = app.FloatingTheme;
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

        // ---- ②③ 线型：实线 / 虚线 / 点线 ----
        // 当前是哪一档**由 SelectionHandles 统一算**（见 DashOfSelection 的注释：
        // 画和自检必须读同一份，不然"面板认不认得当前档"这件事自己验不了自己）。
        var curDash = SelectionHandles.DashOfSelection(app.Doc.Selected);
        for (int i = 0; i < SelectionHandles.StyleCellCount; i++)
        {
            var cell = SelectionHandles.StyleCellRect(i, sel, dpi, app.ViewportCanvas, sc);
            var dash = (StrokeDash)i;             // 格序 = 线型取值，见 StyleCellRect 的注释
            bool active = dash == curDash;

            // 当前档：浅底 + **强调色描边**。光靠浅底不够显眼——用户 2026-09-19 报的
            // "实线和虚线好像不能选中"，最可能看到的就是"这一行没有哪一格像被选中的样子"。
            // 描边这一招和滑条那个当前档的圆钮、色板那个当前色的环是同一套语言。
            // 不再用 th.Hover：那是**悬停**色，拿它表示"选中"本身就串了。
            _scratch.Color = th.Panel;
            float cr = 7f * dpi;
            var cellRect = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
            _ctx.FillRoundedRectangle(new RoundedRectangle(cellRect, cr, cr), _scratch);
            if (active)
            {
                _scratch.Color = th.ActiveBg;
                _ctx.DrawRoundedRectangle(new RoundedRectangle(cellRect, cr, cr), _scratch, 1.6f * dpi);
            }

            // 格子里画**这一档真实的样子**（一条整线 / 一段一段 / 一小点一小点）：
            // 老师看的是"哪格是虚线"，画个文字标签反而要多认一眼。
            float lx0 = cell.MinX + 9f * dpi, lx1 = cell.MaxX - 9f * dpi;
            float ly = (cell.MinY + cell.MaxY) * 0.5f;
            _scratch.Color = active ? th.Text : th.TextMuted;
            var a = new Vector2(lx0, ly);
            var b = new Vector2(lx1, ly);
            float lw = 2.6f * dpi;
            switch (dash)
            {
                case StrokeDash.Dashed: DrawDashed(a, b, lw); break;
                case StrokeDash.Dotted: DrawDotted(a, b, lw); break;
                default: _ctx.DrawLine(a, b, _scratch, lw); break;
            }
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
        DrawPanelCard(app, p, 10f * dpi);

        string[] icons = { IconPaths.toFront, IconPaths.toBack };
        var th = app.FloatingTheme;
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

    private void DrawDashed(Vector2 a, Vector2 b, float width) => DrawDashes(a, b, width, 7f, 5f);

    /// <summary>
    /// 点线（线型那一行的第 3 格）：节**短**、缝比节长，才看得出是"一点一点"
    /// 而不是"很短的一段段"。
    /// </summary>
    private void DrawDotted(Vector2 a, Vector2 b, float width) => DrawDashes(a, b, width, 2f, 4.5f);

    /// <summary>
    /// 手工画虚线 / 点线，<paramref name="dash"/> 与 <paramref name="gap"/> 是**逻辑**像素。
    ///
    /// 为什么这里手工画、不用 <see cref="Dashed"/> 那条 D2D 描边样式：
    /// 这个函数画的是**界面上的小预览**（选中框四角、线型格子里的那一段），
    /// 用的是"一条直线段"而不是图形几何，预览要的是"一眼看出虚实"，
    /// 固定节长比"按笔宽成比例"更可控。
    /// **画布上的图形走 D2D 原生样式**（见 <see cref="StyleFor"/>），两条路各自最合适。
    /// </summary>
    private void DrawDashes(Vector2 a, Vector2 b, float width, float dash, float gap)
    {
        float len = Vector2.Distance(a, b);
        if (len < 0.5f) return;
        var dir = (b - a) / len;
        float k = Dpi / 96f;
        dash *= k; gap *= k;
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
        if (shape == InkEngine.ToolCursorShape.Frame)
        {
            DrawCaptureFrameCursor(app, c);
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
    /// <summary>
    /// 截图工具的落点：**取景框的四个角括号** ＋ 中心一个小十字。
    ///
    /// 为什么单独给它一个形状：截图、框选、图形原来**共用一个系统十字准星**
    /// （`ToolCursorKind` 里一行 `Marquee or Capture or Line or … => Cross`），
    /// 老师分不出"我现在是要截图还是要选框"——用户 2026-09-17 当场点了这一条。
    /// 角括号和"拖出来一个框"是同一个意思，一眼就分得开；而框选/图形仍然是十字。
    ///
    /// 配色和取景框本身一致（琥珀），下面垫一层白描边——深色 PPT 和白色白板上都得看得见
    /// （和圆环、矩形那两个落点是同一套双色逻辑）。
    /// </summary>
    private void DrawCaptureFrameCursor(InkEngine app, Vector2 c)
    {
        float dpi = app.DpiScale;
        float s = 12f * dpi;          // 半宽：24 逻辑像素见方
        float arm = 6f * dpi;         // 角括号的臂长
        var amber = new Color4(1f, 0.68f, 0.10f, 1f);
        var halo = new Color4(1f, 1f, 1f, 0.85f);

        void Brackets(float w, Color4 col)
        {
            _scratch.Color = col;
            // 左上
            _ctx.DrawLine(new Vector2(c.X - s, c.Y - s + arm), new Vector2(c.X - s, c.Y - s), _scratch, w);
            _ctx.DrawLine(new Vector2(c.X - s, c.Y - s), new Vector2(c.X - s + arm, c.Y - s), _scratch, w);
            // 右上
            _ctx.DrawLine(new Vector2(c.X + s - arm, c.Y - s), new Vector2(c.X + s, c.Y - s), _scratch, w);
            _ctx.DrawLine(new Vector2(c.X + s, c.Y - s), new Vector2(c.X + s, c.Y - s + arm), _scratch, w);
            // 右下
            _ctx.DrawLine(new Vector2(c.X + s, c.Y + s - arm), new Vector2(c.X + s, c.Y + s), _scratch, w);
            _ctx.DrawLine(new Vector2(c.X + s, c.Y + s), new Vector2(c.X + s - arm, c.Y + s), _scratch, w);
            // 左下
            _ctx.DrawLine(new Vector2(c.X - s + arm, c.Y + s), new Vector2(c.X - s, c.Y + s), _scratch, w);
            _ctx.DrawLine(new Vector2(c.X - s, c.Y + s), new Vector2(c.X - s, c.Y + s - arm), _scratch, w);
        }

        Brackets(3.2f, halo);          // 先垫白（深色背景上才看得见）
        Brackets(1.6f, amber);

        // 中心小十字：投影上写字手会抖，"从哪儿开始拖"要看得准
        float t = 4f * dpi;
        _scratch.Color = halo;
        _ctx.DrawLine(new Vector2(c.X - t, c.Y), new Vector2(c.X + t, c.Y), _scratch, 3.2f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - t), new Vector2(c.X, c.Y + t), _scratch, 3.2f);
        _scratch.Color = amber;
        _ctx.DrawLine(new Vector2(c.X - t, c.Y), new Vector2(c.X + t, c.Y), _scratch, 1.6f);
        _ctx.DrawLine(new Vector2(c.X, c.Y - t), new Vector2(c.X, c.Y + t), _scratch, 1.6f);
    }

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
        // 抓屏那一瞬不画框（"直接截取"要把板书留下、把框藏掉）
        if (!app.CaptureActive || app.CaptureFrameHidden) return;
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

        // **尺寸读数**（用户 2026-09-17："这个截图使用不顺手，光标配合也感觉不好"）。
        //
        // 拖动时最想知道的是"我框的这块有多大"——没有读数就得靠眼估，松手才发现多一块少一块。
        // 数字用**逻辑像素**（老师看的坐标系，和"导出 300×200"是一套），
        // 框贴在屏幕顶上时改画在框里面，免得跑到屏幕外看不见。
        // 注意：`Dpi` 是**DPI 本身**（本机 192），不是缩放倍数——缩放倍数是它 ÷ 96。
        // 第一版直接乘了 Dpi，胶囊被算到屏幕外 5000 像素（自检量到 0 像素）。
        float s = Dpi / 96f;
        float w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 24f * s || h < 16f * s) return;                  // 刚开始拖，数字没意义
        string text = $"{w / s:F0} × {h / s:F0}";
        float boxW = 96f * s, boxH = 26f * s, gap = 6f * s;
        bool below = r.Bottom + boxH + gap < Height;
        var box = new Vortice.RawRectF(
            r.Left, below ? r.Bottom + gap : r.Bottom - boxH - gap,
            r.Left + boxW, (below ? r.Bottom + gap : r.Bottom - boxH - gap) + boxH);
        _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.82f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, boxH * 0.5f, boxH * 0.5f), _scratch);
        _ctx.DrawText(text, CaptureInfoFormat(),
                      new Rect(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top),
                      Brush(new Color4(1f, 1f, 1f, 1f)));
    }

    private IDWriteTextFormat _capInfoFmt;

    /// <summary>取景框旁边的尺寸读数格式（14 逻辑像素、居中、按 DPI 生成一次）。</summary>
    private IDWriteTextFormat CaptureInfoFormat()
    {
        if (_capInfoFmt != null) return _capInfoFmt;
        _capInfoFmt = Gfx.WriteFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 14f * (Dpi / 96f), "zh-CN");
        _capInfoFmt.TextAlignment = TextAlignment.Center;
        _capInfoFmt.ParagraphAlignment = ParagraphAlignment.Center;
        return _capInfoFmt;
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
        _readoutFormatSmall?.Dispose();
        _hudFormat?.Dispose();
        _hudSource?.Dispose();
        _hudTarget?.Dispose();
        _hudBmpTex?.Dispose();
        _tiles?.Dispose();
        _backBuffer?.Dispose();
        _inkStyle?.Dispose();          // 墨迹笔尖样式（压感变宽那条路）
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
