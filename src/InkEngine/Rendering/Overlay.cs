using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
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
    /// <summary>适配器名（不含显存那串说明；日志/设备卡用）。</summary>
    public static string AdapterName = "unknown";
    /// <summary>专用显存 MB（WARP/核显可能为 0；只作参考）。</summary>
    public static int AdapterVramMb;
    /// <summary>
    /// 当前是不是**软件渲染**（WARP / Microsoft Basic Render Driver）。
    /// 2026-10-17 加：排查"同一版本，一台 2ms/帧、一台 14ms/帧"时，
    /// 第一件要知道的事就是它——软件渲染和真显卡差一个数量级。
    /// </summary>
    public static bool IsSoftware;
    /// <summary>硬件设备建不起来时的原始错误（WARP 兜底成功也保留，日志里能看出为什么降级）。</summary>
    public static string HardwareError = "";

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

    public static void Init(bool forceWarp = false)
    {
        Mem.Stage("0. 进程启动（运行时 + 程序集）");

        var levels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
            Vortice.Direct3D.FeatureLevel.Level_10_0,
        };

        // 硬件优先；建不起来（虚拟机 / 云桌面 / 远程桌面 / 驱动被禁用 / 极老 GPU）
        // 再用 WARP 软件渲染兜底。**这不是提速功能**：1080p 还能用，4K 会很慢；
        // 但没有它，这些机器会直接启动失败、弹一个老师看不懂的错误。
        // `--warp` 是强制走软件渲染的诊断开关（量性能地板用）。
        if (!forceWarp)
        {
            try
            {
                var hr = D3D11.D3D11CreateDevice((IDXGIAdapter)null, DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport, levels, out Device);
                hr.CheckError();
            }
            catch (Exception ex)
            {
                HardwareError = ex.Message;
                Device?.Dispose();
                Device = null;
            }
        }

        if (Device == null)
        {
            var hr = D3D11.D3D11CreateDevice((IDXGIAdapter)null, DriverType.Warp,
                DeviceCreationFlags.BgraSupport, levels, out Device);
            hr.CheckError();
            IsSoftware = true;
            if (HardwareError.Length == 0 && forceWarp) HardwareError = "强制 --warp";
        }

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
            AdapterName = (d.Description ?? "unknown").Trim();
            AdapterVramMb = (int)(dedicated / 1048576UL);
            // DXGI 的软件适配器标志（Basic Render Driver / WARP）。拿不到就按名字兜底。
            bool softFlag = false;
            try
            {
                using var a1 = adapter.QueryInterface<IDXGIAdapter1>();
                var d1 = a1.Description1;
                softFlag = (d1.Flags & Vortice.DXGI.AdapterFlags.Software) != 0;
            }
            catch { }
            if (softFlag
                || AdapterName.Contains("WARP", StringComparison.OrdinalIgnoreCase)
                || AdapterName.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                || AdapterName.Contains("Software Adapter", StringComparison.OrdinalIgnoreCase))
                IsSoftware = true;

            AdapterInfo = $"{AdapterName}  专用显存 {dedicated / 1048576.0:F0} MB, 共享内存 {shared / 1048576.0:F0} MB"
                        + (IsSoftware ? "  → 软件渲染（WARP）"
                           : dedicated < 512UL * 1048576 ? "  → 核显（显存来自系统内存）" : "  → 独显");
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
internal sealed partial class OverlayWindow : IDisposable
{
    public static long RebuildCount;

    private ID2D1DeviceContext1 _ctx1;

    /// <summary>图形卡的真实墨迹小样：每个档位造一次，之后只改颜色/线宽再复用。</summary>
    private readonly Dictionary<ShapePreset, Stroke> _shapePreviewCache = new();

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
    private IDXGISwapChain3 _swapChain3;      // 诊断：读"当前后缓冲号"（见 Trace）
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
    private IDCompositionDevice2 _dcomp2;
    private IDCompositionTarget _target;
    /// <summary>根视觉：纯容器（交换链挂在子视觉里，墨迹才有地方叠上去）。</summary>
    private IDCompositionVisual _visual;
    /// <summary>交换链内容视觉（每帧画的内容都在这）。2026-10-10 试验C 从"根视觉直接装内容"改成子视觉。</summary>
    private IDCompositionVisual _swapVisual;
    /// <summary>委托墨迹轨迹视觉（内容＝_inkTrail，叠在交换链之上）。2026-10-10 试验C 新增。</summary>
    private IDCompositionVisual _inkVisual;
    private IDCompositionInkTrailDevice _inkTrailDevice;
    private IDCompositionDelegatedInkTrail _inkTrail;
    private bool _inkTrailRuntimeFailed;
    private ID2D1DeviceContext _ctx;
    /// <summary>
    /// D2D 1.3 的设备上下文 2：**原生墨迹**（ID2D1Ink）唯一能创建的地方
    /// （<c>CreateInk</c> / <c>DrawInk</c> 都挂在这个接口上）。拿不到就是 null。
    /// </summary>
    private ID2D1DeviceContext2 _ctx2;
    /// <summary>墨迹笔尖样式（圆头）——和"笔＝圆头"这条口径一致。</summary>
    private ID2D1InkStyle _inkStyle;
    /// <summary>每次画一条压感笔迹最多铺多少段（**只在曲线化关着时**生效，见 DrawPressureInk）。</summary>
    private const int InkMaxSegments = 120;
    /// <summary>`--inkmodel` 时压感墨迹的段数上限：建模输出 ≥180Hz、点很密，按 120 抽会把弯拉直。</summary>
    private const int InkMaxSegmentsModeled = 4096;
    /// <summary>
    /// 段缓冲：**预分配、复用**，不在每帧绘制里 new。
    /// 曲线化打开时会按点数增长（`--smooth`）：折线段少是有意抽稀的，曲线段不能抽——
    /// 一抽就把刚算出来的弯又拉直了。
    /// </summary>
    private InkBezierSegment[] _inkSegs = new InkBezierSegment[InkMaxSegments];
    /// <summary>`--mean2fit` 用：拟合输入的复用点/压力缓冲（静态数组，不每帧分配）。</summary>
    // [删除 2026-10-05] `_fitPts/_fitP`（WPF 拟合输入缓冲）：随拟合档清理移除。
    /// <summary>压力的指数平滑系数（0..1，越小越稳）。见 DrawPressureInk。</summary>
    private const float InkPressureEma = 0.35f;      // 上升（加压）方向的平滑
    /// <summary>下降（减压）方向的平滑——**比上升慢**（2026-10-08）：
    /// 真实笔"按住的点"压力爬到 ~0.3、一开写压力掉回 0.03，等速平滑会让宽度瞬间塌成发丝线
    /// （"圆消失变尖笔锋"）。降慢一点，让宽度有个缓冲，不跟着压力跳。</summary>
    private const float InkPressureEmaDown = 0.14f;
    /// <summary>最小墨迹半径（画布像素）：轻压时也不至于细到画不出来。</summary>
    private const float InkMinRadius = 0.4f;
    private ID2D1Bitmap1 _backBuffer;

    // ---- 内容层（画布空间分块缓存）---------------------------------------
    // 内容层**不再是一张绑在屏幕上的位图**，而是画布空间里的一格一格小位图。
    // 这是"滚动不重画"和"坐标不会忘换算"的根。见 CanvasTiles.cs 顶部的说明。
    private CanvasTileCache _tiles;
    /// <summary>上一次把文档脏区同步进分块缓存时的文档版本号。</summary>
    private int _tilesVersion = -1;
    /// <summary>z 序缓存（2026-10-17）：`Stroke → Doc.Strokes 下标`，版本号变了才重建。</summary>
    private readonly Dictionary<Stroke, int> _zCache = new();
    private int _zCacheVersion = -1;
    /// <summary>上一帧的相机偏移。变了 → 整屏合成位置都变了，后缓冲整块作废。</summary>
    private float _lastCamY = float.NaN;
    private bool _lastBoardOn;
    private Color4 _lastBoardColor = new(0f, 0f, 0f, -1f);
    /// <summary>上一帧"文档垫白"在不在（进/出会整层重铺，和板色同一套）。</summary>
    private bool _lastPaper;
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
    /// <summary>上一帧图库面板占的矩形（窗口坐标）。面板关掉时要靠它把那张卡片擦掉。</summary>
    private RectF _libraryRectPrev = RectF.Empty;
    /// <summary>上一帧选中条弹层（AI/`⋯`）占的矩形（窗口坐标）。</summary>
    private RectF _selMenuRectPrev = RectF.Empty;
    /// <summary>上一帧字体下拉占的矩形（窗口坐标）。关掉/换位置时靠它擦旧画面。</summary>
    private RectF _fontListRectPrev = RectF.Empty;
    /// <summary>上一帧 α 读数胶囊（画直线/拖端点）占的矩形。</summary>
    private RectF _inclPillRectPrev = RectF.Empty;
    /// <summary>上一帧多笔吸附胶囊（棱柱）占的矩形。</summary>
    private RectF _stepPillRectPrev = RectF.Empty;
    /// <summary>上一帧拖顶点胶囊占的矩形。</summary>
    private RectF _vertexPillRectPrev = RectF.Empty;
    /// <summary>上一帧旋转读数占的矩形。</summary>
    private RectF _rotReadoutRectPrev = RectF.Empty;

    /// <summary>上一帧"自绘落点反馈"（橡皮框 / 圆环）占的矩形（窗口坐标）。
    /// **必须并进脏区**：指针一帧里可能移动超过半径（快擦、面积擦的框还会随速度变大变小），
    /// 只算当前位置的话，旧框留在原地的那一条就成了"擦不掉的竖线 / 变暗的带子"——
    /// 用户 2026-09-30 报的"橡皮左侧 / 左上侧靠近墨迹时出现细密竖线、颜色变深"就是它。</summary>
    private RectF _cursorRectPrev = RectF.Empty;
    /// <summary>上一帧多指擦会话的落点反馈包围盒：结束那一帧再擦一次旧位置（"不消失"就出在这）。</summary>
    private RectF _touchEraseRectPrev = RectF.Empty;
    /// <summary>上一帧对象吸附短亮占的矩形（窗口坐标）：淡出结束那一帧要把它擦干净。</summary>
    private RectF _alignPulseRectPrev = RectF.Empty;

    /// <summary>
    /// 右缘滚动条那一条。**单独成一块脏区**，不并进静止图元的联合包围盒
    /// （理由见 <c>ComputeTransientBounds</c> 末尾：并进去会把"笔迹左边"排到脏区外）。
    /// </summary>
    private RectF _scrollbarRect = RectF.Empty;

    /// <summary>上一帧的滚动条矩形：淡出结束那一帧还要把它擦掉（同 _cursorRectPrev 的做法）。</summary>
    private RectF _scrollbarRectPrev = RectF.Empty;

    /// <summary>上一帧"截图取景"开着吗——收场那一帧要整窗重画，把遮罩擦干净（8.3.0）。</summary>
    private bool _captureWasActive;

    /// <summary>
    /// 上一帧"屏幕吸管"开着吗——吸管也是整屏模态：放大镜每帧跟着指针跑，
    /// 退出那一帧还要把镜片擦干净。只算 CaptureActive 不够（用户 2026-10-28 报的
    /// "十字/镜片有残留"就是这里漏了 ColorPickActive）。
    /// </summary>
    private bool _colorPickWasActive;

    /// <summary>PPT 控件条上一帧的矩形（退出放映 / 拖动那一帧要靠它把旧位置擦干净，
    /// 同 `_libraryRectPrev` 的做法）。</summary>
    private RectF _pptBarRectPrev = RectF.Empty;
    /// <summary>回放控制条上一帧的矩形（退出回放那一帧要把旧位置擦干净，同 PPT 条）。</summary>
    private RectF _replayBarRectPrev = RectF.Empty;
    /// <summary>计时卡片上一帧的矩形（停下 / 收起 / 拖动那一帧要把旧位置擦干净）。</summary>
    private RectF _timerRectPrev = RectF.Empty;
    /// <summary>点名卡片上一帧的矩形。</summary>
    private RectF _rollRectPrev = RectF.Empty;
    /// <summary>引擎侧悬停提示上一帧的矩形（换块 / 隐藏那一帧要把旧卡擦干净，同 PPT 条）。</summary>
    private RectF _tipRectPrev = RectF.Empty;
    /// <summary>上一帧轻提示占的矩形：提示到点消失那一帧要把它擦干净。</summary>
    private RectF _toastRectPrev = RectF.Empty;

    /// <summary>页码那一段文字的格式（13 逻辑像素，见 `PptPageFormat`）。</summary>
    private IDWriteTextFormat _pptPageFmt;
    private float _pptPageFmtPx;
    /// <summary>悬停提示的两行格式：第一行 12.5、第二行 11（都左对齐，键位跟名称同一行）。</summary>
    private IDWriteTextFormat _tipTitleFmt, _tipNoteFmt;
    private float _tipTitleFmtPx, _tipNoteFmtPx;
    /// <summary>轻提示的格式：居中、12.5 逻辑像素。</summary>
    private IDWriteTextFormat _toastFmt;
    private float _toastFmtPx;
    /// <summary>选中条弹层的行文字：左对齐/居中两版，**都垂直居中**（图标对齐）。</summary>
    private IDWriteTextFormat _menuLeftFmt, _menuCenterFmt;
    private float _menuLeftPx, _menuCenterPx;

    // 内容层的改动也要记两帧：后缓冲里躺着的是两帧前的画面。
    private readonly List<RectF> _contentDirtyNow = new();
    private readonly List<RectF> _contentDirtyPrev = new();

    // 文档页层（图片/PDF）的脏区同理：页生成/回收既要标块脏，也要进"上屏脏区"，
    // 而且同样要记两帧（两个后缓冲都要覆盖到）。**窗口坐标**。
    private readonly List<RectF> _pageDirtyNow = new();
    private readonly List<RectF> _pageDirtyPrev = new();

    /// <summary>整块后缓冲内容无效（首帧、重建、尺寸变化）时必须全屏重绘一次。</summary>
    /// <summary>
    /// "接下来还要整屏重画几帧"。
    ///
    /// **为什么是 2 而不是 1**（2026-10-04 修"滚完轮一按就闪/错位、只在左侧"）：
    /// 画面在两个后缓冲之间交替，一次整屏重画只更新**其中一个**。如果只强制一帧，
    /// 另一个缓冲会停在变化前（旧相机位置 / 旧底色 / 旧文档）的画面。平时这个旧画面
    /// 靠"变化的像素要在随后两帧都重画"的脏区历史兜住，但**相机变化没有这份两帧历史**，
    /// 于是滚动停下后第一帧如果只重画局部（笔迹 + 滚动条并成的包围盒），旧缓冲里
    /// 没被覆盖的那一块就会露出来——就是用户看到的"从某条线隔开、一侧错位/闪"。
    /// 强制**连续两帧**整屏，保证两个缓冲都更新到新状态。
    /// </summary>
    private int _fullFramesLeft = 2;

    /// <summary>
    /// 旋转度数标签用的文字格式。**按 DPI 生成**：绘制时的变换只有平移，
    /// 字号写死 15 就等于"15 物理像素"，200% 缩放下会小一半。
    /// </summary>
    private IDWriteTextFormat _readoutFormat;
    private float _readoutFormatPx;

    /// <summary>
    /// **收藏页签**用的文字格式：13 逻辑像素、常规字重、水平居中。
    ///
    /// 为什么不复用上面那两个：主读数是 15 SemiBold（太大，四个页签立刻挤爆 28 高的标题栏），
    /// 小号是 11（投到教室大屏上读不清）。13 是「能读清」和「放得下」的交点——
    /// 1440p 投 100 寸时约 3cm 高，后排能认。
    /// 选中态不靠加粗区分（那要多一份 SemiBold 格式），靠**颜色 + 2px 下划线**，
    /// 投影上这两种差别都比字重明显。
    /// </summary>
    private IDWriteTextFormat _readoutTabFormat;
    private float _readoutTabFormatPx;

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

    /// <summary>计时卡片上的数字（19 逻辑像素）与放大态的大字（92 逻辑像素）。</summary>
    private IDWriteTextFormat _timerValueFormat, _timerBigFormat;
    private float _timerValueFormatPx, _timerBigFormatPx;
    /// <summary>点名结果的字号格式（按结果长度自适应，现建现缓存）。</summary>
    private IDWriteTextFormat _rollResultFormat;
    private float _rollResultFormatPx;

    /// <summary>
    /// AI 题卡的文字格式缓存。键 = (字号, 粗体, 居中)；题卡只在内容层重画时用到，
    /// 量很小，但别每帧重建 DirectWrite 格式。
    /// </summary>
    private readonly Dictionary<(int Px, bool Bold, bool Center, bool Italic, string Family), IDWriteTextFormat> _aiFormats = new();

    /// <summary>题卡配色（纸面始终浅色，保证黑题/红解析在深色主题下也读得清）。</summary>
    private static readonly Color4 AiBlack = new(0.12f, 0.14f, 0.18f, 1f);
    private static readonly Color4 AiRed = new(0.72f, 0.16f, 0.16f, 1f);
    private static readonly Color4 AiMuted = new(0.42f, 0.45f, 0.50f, 1f);
    private static readonly Color4 AiRedSoft = new(0.98f, 0.91f, 0.90f, 1f);
    /// <summary>到点闪烁那一拍的颜色（和主题无关：就是"提醒红"）。</summary>
    private static readonly Color4 TimerFlashCol = new(0.91f, 0.26f, 0.21f, 1f);

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
    /// <summary>这一帧最后是不是整屏 Present（true 时 count=1、area=100%）。</summary>
    public bool LastPresentFull;
    /// <summary>Present 前等“下一帧可提交”的时间（配速/垂直同步等待）；不是绘制时间。</summary>
    public double LastPresentWaitMs;
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
    /// 是否启用委托墨迹轨迹（只对 `PT_PEN` 喂点）。开关策略在 `Engine`：
    /// 有笔数字化器时默认开，`--inktrail` / `--noinktrail` 可强制。
    ///
    /// ⚠ 2026-10-05 真笔实测定案：**本机的系统不渲染这条轨迹**——COM 调用全部成功、
    /// 点也喂了进去（1522/1522），屏幕上没有任何新增墨（判据＝按下期间像素增量；
    /// 用户目视确认"写的时候看不见墨迹，抬笔才会出现"）。所以它在本机**空转但不致害**
    /// （湿墨实况＝自绘，合成边界配速 1.0 帧上屏），保留作换机器/换平板的验证通道；
    /// `--syswet`（只让系统画）本机勿用。见 `延时-实测与优化.md` §八。
    /// </summary>
    public static bool InkTrailEnabled;

    /// <summary>
    /// `--syswet`：真笔写实线笔时，**湿墨只让系统轨迹画**（我们不再自绘当前笔画），
    /// 抬笔后由干墨接管。默认关 = 保持"两条都画"的老行为，方便 A/B 与回退。
    /// ⚠ 2026-10-05 本机真笔实测：系统轨迹不渲染（COM 成功但无可见墨），
    /// 所以本机**勿启用**；保留它用于换机器/换平板的验证。见 `延时-实测与优化.md` §八。
    /// </summary>
    public static bool SystemWetPreferred;

    /// <summary>
    /// "按住不动 = 右键"这条**系统手势**在本窗口内关掉了吗（见
    /// <see cref="Native.DisableSystemPressAndHold"/>）。
    ///
    /// 做成静态字段是为了**印进启动横幅**：它生效没有直接决定"停顿成型"试得出来试不出来，
    /// 而失效时的现象（笔尖停住弹右键环）看起来像"识别不准"，很容易查错方向。
    /// </summary>
    public static bool PressAndHoldDisabled;

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
    /// 诊断开关（`--fullpresent`，2026-10-04 加）：**每次都用整屏 Present，
    /// 不走 `Present1` 的脏矩形**。
    ///
    /// 用来分辨用户报的"屏幕上固定一条横线、滚动/书写时从那里闪"：那一类更像是
    /// 脏矩形部分上屏和 DWM/MPO（硬件平面）合成的相互作用，而不是我们墨迹层的
    /// 脏区漏画（`--prevflash` 已排除后者）。关掉脏矩形上屏如果闪就没了，
    /// 就说明该在"部分上屏"这条路上做产品级兜底。
    /// </summary>
    public static bool FullPresent;

    /// <summary>
    /// 每帧在渲染之前先 DwmFlush，等到合成边界再抽输入、提交。
    /// 见 Native.DwmFlush 的说明，以及 README 里延时那一节。
    /// </summary>
    public static bool VBlankPaced;

    /// <summary>
    /// 诊断开关（`--scrollflash` 等探针用）：把每一帧的脏区/上屏/后缓冲号打到控制台。
    /// 用来追"滚完按下时左列错位"这类只发生在特定帧的问题（2026-10-04）。
    /// </summary>
    public static bool Trace;

    public int LastDrawnStrokes;
    /// <summary>上一拍预取了几块 / 耗时 / 预算口径（诊断与 HUD）。</summary>
    public int LastPrefetch;
    public double LastPrefetchMs;
    public bool LastPrefetchPending;
    /// <summary>常驻分块数 / 这一帧可见块数 / 分块预算（诊断用）。</summary>
    public int LastTileCount, LastTileVisible, LastTileBudget;
    public string LastError;

    public ID2D1DeviceContext Context => _ctx;

    // ---- 委托墨迹轨迹（微软的低延迟湿墨通道）-----------------------------
    private bool _trailActive;
    private uint _trailGeneration;
    private float _trailRadius = 2f;
    /// <summary>这一条轨迹成功喂进去的点数（`--syswet` 抑制判据 + `--wetinktest` 判据都要用）。</summary>
    private int _trailPointsFed;
    /// <summary>收笔后"等这一帧 Present 完再撤轨迹"的挂起标记（防"轨迹先没、干墨未上屏"闪一帧）。</summary>
    private bool _trailEndPending;
    /// <summary>
    /// 本帧**我们自绘**把"正在写的这一笔"画上了屏（Present 后据此把轨迹里同样这段删掉，
    /// 见 <see cref="TrimInkTrailAsDrawn"/>）。`--syswet` / 自检抑制自绘时为 false——
    /// 那时湿墨只有轨迹这一份，删了就没了。
    /// </summary>
    private bool _drewWetStrokeThisFrame;
    /// <summary>轨迹点已经"结账"到哪一代（每起一笔归零）。</summary>
    private uint _trailTrimmedGeneration;
    /// <summary>这一条轨迹已经"结账"删掉的点数（诊断用）。</summary>
    private int _trailPointsRemoved;
    /// <summary>这个窗口探过轨迹接口没有。**按窗口**——多屏时每个窗口各要一条轨迹（2026-10-11 修）。</summary>
    private bool _trailProbed;
    /// <summary>进程级累计喂点（`--wetinktest` 判"系统轨迹到底喂没喂进去"）。</summary>
    public static long InkTrailPointsFed;
    /// <summary>进程级累计"结账"删掉的点数（诊断用：默认模式下应约等于喂点数减去在场点数）。</summary>
    public static long InkTrailPointsRemoved;
    /// <summary>
    /// 试验D（2026-10-11）：**每帧 DComp 提交一次**。Chromium 每帧都 Commit，
    /// 我们只在建窗口时提交过那一次——"闪一下就没"的嫌疑之一就是它。
    /// （`--trailcommit` 开；A/B 用。）
    /// </summary>
    public static bool TrailCommitEveryFrame;

    /// <summary>
    /// 开始一条委托墨迹轨迹。此后系统合成器会自己把笔尖后面的这一小段
    /// 画出来，节奏跟着显示器刷新走，不受我们这一帧有没有画完影响。
    /// 默认**我们照旧画自己的笔画**（两条都在，取并集）；`--syswet` 时自绘湿墨
    /// 让位给系统轨迹（见 <see cref="SystemWetPreferred"/>）。
    /// 返回值 = 这条轨迹是否真的起来了（`--syswet` 靠它决定敢不敢不画）。
    /// </summary>
    public bool BeginInkTrail(Color4 color, float radius)
    {
        _inkTrailRuntimeFailed = false;
        if (_inkTrail == null || !InkTrailEnabled) return false;
        // 上一条轨迹还挂起没撤（收笔后等 Present）→ 先撤，别叠上新轨迹。
        if (_trailEndPending) EndInkTrailNow();
        try
        {
            _trailRadius = MathF.Max(0.5f, radius);
            _inkTrail.StartNewTrail(color);
            _trailActive = true;
            _trailGeneration = 0;
            _trailPointsFed = 0;
            _trailPointsRemoved = 0;
            _trailTrimmedGeneration = 0;
            InkTrailDebug = $"StartNewTrail ok, color=({color.R:F2},{color.G:F2},{color.B:F2},{color.A:F2})";
        }
        catch (Exception ex)
        {
            _trailActive = false;
            _inkTrail = null;
            _inkTrailRuntimeFailed = true;
            InkTrailDebug = "StartNewTrail 异常: " + ex.Message;
        }
        return _trailActive;
    }

    /// <summary>委托轨迹本笔运行时失败；Engine 据此切换应用侧湿墨。</summary>
    public bool InkTrailRuntimeFailed => _inkTrailRuntimeFailed;

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
            _trailPointsFed += 1;
            InkTrailPointsFed += 1;
            InkTrailDebug = $"AddTrailPoints ok gen={_trailGeneration} at({p.X:F0},{p.Y:F0}) r={p.Radius:F1}";
        }
        catch (Exception ex)
        {
            _trailActive = false;
            _inkTrail = null;
            _inkTrailRuntimeFailed = true;
            InkTrailNote = "运行时单点失败 → 使用应用侧湿墨替身";
            InkTrailDebug = "AddTrailPoints 异常: " + ex.Message;
        }
    }

    /// <summary>
    /// 把一批真实点 + 一批**预测点**交给系统合成器（都用屏幕坐标）。
    ///
    /// 预测点是"这一帧之后笔尖大概在哪"（见 Prediction/InkPredictorLinear.cs）。系统会把轨迹画到
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
            _trailPointsFed += realCount;
            InkTrailPointsFed += realCount;
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
                _trailPointsFed += 1;
                InkTrailPointsFed += 1;
            }
            catch
            {
                _trailActive = false;
                _inkTrail = null;
                _inkTrailRuntimeFailed = true;
                InkTrailNote = "运行时喂点失败 → 使用应用侧湿墨替身";
            }
        }
    }

    /// <summary>这条轨迹现在真的在画：活跃且至少成功喂进去过一个点。</summary>
    public bool TrailDrawing => _trailActive && _trailPointsFed > 0;

    /// <summary>收笔的轨迹撤除还挂着（等下一帧 Present 后执行）——主循环用它兜底要一帧。</summary>
    public bool TrailEndPending => _trailEndPending;

    /// <summary>收笔：立即撤轨迹（自检/清理路径用；正常收笔走 <see cref="EndInkTrailDeferred"/>）。</summary>
    public void EndInkTrail() => EndInkTrailNow();

    /// <summary>
    /// 收笔（**延后到这一帧 Present 之后**再撤）：`--syswet` 下湿墨只有系统轨迹这一份，
    /// 必须先让"干墨那一帧"提交出去再撤，否则会出现"整笔消失一帧再出现"的闪。
    /// </summary>
    public void EndInkTrailDeferred()
    {
        if (!_trailActive) return;
        _trailEndPending = true;
    }

    private void EndInkTrailNow()
    {
        _trailEndPending = false;
        if (!_trailActive || _inkTrail == null) { _trailActive = false; return; }
        _trailActive = false;
        try { if (_trailGeneration != 0) _inkTrail.RemoveTrailPoints(_trailGeneration); }
        catch { }
        // 收笔撤轨迹同样算"结账"（诊断口径：喂 = 结账 + 在场）。
        int outstanding = _trailPointsFed - _trailPointsRemoved;
        if (outstanding > 0) { InkTrailPointsRemoved += outstanding; _trailPointsRemoved = _trailPointsFed; }
    }

    /// <summary>
    /// 轨迹"结账"：我们**自己已经把这一笔画上了屏**，就把轨迹里同样这一段删掉
    /// （Chromium 的每帧协议：用 RemoveTrailPoints 把"已上屏的点"清出轨迹）。
    ///
    /// 为什么必须删（2026-10-11 真笔实测）：系统的委托轨迹**单条上限 128 个点**
    /// （Chromium `kMaximumNumberOfDelegatedInkPoints = 128`，注释写明"和 OS 合成器
    /// 内部行为一致"）。只喂不删 → 写长笔画超过 128 点后旧点被系统擦掉，症状就是
    /// "先出墨、中途开始变透明、抬笔才补全"。删到"本帧喂到的最后一代"之后，
    /// 轨迹里永远只剩**还没上屏的那一小截**（这正是这条通道的设计用途）。
    /// ⚠ 只在自绘过时调用；`--syswet` / 抑制自绘时不能删（那时湿墨只有轨迹这一份）。
    /// </summary>
    private void TrimInkTrailAsDrawn()
    {
        if (!_trailActive || _inkTrail == null) return;
        if (_trailGeneration == 0 || _trailGeneration == _trailTrimmedGeneration) return;
        try
        {
            _inkTrail.RemoveTrailPoints(_trailGeneration);
            _trailTrimmedGeneration = _trailGeneration;
            int outstanding = _trailPointsFed - _trailPointsRemoved;
            if (outstanding > 0)
            {
                InkTrailPointsRemoved += outstanding;
                _trailPointsRemoved = _trailPointsFed;
            }
        }
        catch { _trailActive = false; }   // 删点失败按"这条轨迹废了"处理（和喂点失败一致）
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

        // **在本窗口内关掉"按住不动 = 右键"这条系统手势**（理由见 Native 里那一段注释）：
        // 我们要用"笔按住不动"当停顿成型的信号，和系统那条同款手势直接撞车。
        // 每个覆盖层窗口都要关一次——手势是按窗口算的，只关主屏那一个，
        // 副屏上写字照样弹右键环。
        PressAndHoldDisabled = Native.DisableSystemPressAndHold(Hwnd);

        Dpi = Native.GetDpiForWindow(Hwnd);
        if (Dpi == 0) Dpi = 96;
        // 真实刷新率：120Hz 屏上延时报告要折合真实周期（拿不到退回 60Hz）。
        // 窗口创建时读一次；改了显示模式重启软件即可（运行中改的极少）。
        int refreshHz = Native.GetCurrentRefreshHz();
        RefreshPeriodMs = refreshHz >= 30 ? 1000.0 / refreshHz : 1000.0 / 60.0;

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

    /// <summary>
    /// 按需注册 / 注销**原始输入**（`--rawinput` / `--rawprobe`）。
    ///
    /// ⚠ **只能在"正在写一笔"期间注册，抬笔必须注销**——这条是踩出来的：
    /// `RIDEV_INPUTSINK` 会让窗口在**空闲时也持续收到鼠标的轮询空报**
    /// （鼠标不动时硬件照样按轮询率发"零位移"的报，每秒上百条）。
    /// 消息一多，主循环就被顶成"每帧都渲染"：实测**空闲 46 fps、单核 26%**
    /// （用 `--norawinput` 对照是 0 fps / 0.4%）。教室机/笔记本上这就是风扇狂转。
    /// 所以按笔注册、抬笔注销，空闲时一条都不收。
    /// </summary>
    public bool SetRawInput(bool on)
    {
        if (Hwnd == IntPtr.Zero) return false;
        var dev = new Native.RAWINPUTDEVICE
        {
            usUsagePage = 0x01,        // Generic Desktop
            usUsage = 0x02,            // Mouse（手写板在鼠标模式下也报这个）
            dwFlags = on ? Native.RIDEV_INPUTSINK : Native.RIDEV_REMOVE,
            hwndTarget = on ? Hwnd : IntPtr.Zero,
        };
        return Native.RegisterRawInputDevices(new[] { dev }, 1,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.RAWINPUTDEVICE>());
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
        try { _swapChain3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>(); } catch { _swapChain3 = null; }

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

        // Create one IDCompositionDevice2 from the D2D device, then obtain the
        // v1 face from the same COM object. Surface content and the existing
        // visual tree must belong to one composition device.
        try
        {
            _dcomp = DComp.DCompositionCreateDevice2<IDCompositionDevice>(Gfx.D2DDevice);
            _dcomp2 = _dcomp.QueryInterfaceOrNull<IDCompositionDevice2>();
        }
        catch
        {
            _dcomp2 = null;
            _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(
                Gfx.Device.QueryInterface<IDXGIDevice>());
        }
        _dcomp.CreateTargetForHwnd(Hwnd, true, out _target).CheckError();
        // 根视觉**不装内容**，交换链放在子视觉里 —— 这样湿墨才有地方叠在它上面。
        // 2026-10-10 试验C 按 Chromium 的结构改；2026-10-13 修正层序（见下一条注释）。
        //
        // ⚠ `AddVisual` 的 insertAbove 语义**和直觉相反**（MSDN 原文：referenceVisual 为
        //   NULL 时，"If insertAbove is TRUE, the new child visual is above no sibling,
        //   therefore it is rendered below all of its siblings"）：
        //   · insertAbove:false ＋ 参照 null → 加到**所有兄弟之上**；
        //   · insertAbove:true  ＋ 参照 null → 加到**所有兄弟之下**。
        //   湿墨视觉（系统轨迹）必须叠在交换链**上面**——以前这里用了
        //   true（=压在最下面），开着不透明白板时按住书写屏幕上一个湿墨像素都没有
        //   （2026-10-13 实测：白板 + 系统轨迹 0 像素，桌面正常），
        //   因为白板的底色把下面的一切都盖住了；松手干墨进文档后才重新可见。
        _visual = _dcomp.CreateVisual();
        _swapVisual = _dcomp.CreateVisual();
        _swapVisual.SetContent(_swapChain).CheckError();
        _visual.AddVisual(_swapVisual, false, null).CheckError();
        _target.SetRoot(_visual).CheckError();
        _dcomp.Commit().CheckError();

        // 微软的"委托墨迹轨迹"：本意是把正在写的那一笔交给系统合成器去画，
        // 应用自己的延迟就不再影响笔尖跟手程度。这一项要单独探测，
        // 因为它是较新的接口，老系统上没有。
        // ⚠ 2026-10-05 真笔实测：本机系统不渲染它（调用全成功、屏幕无墨）——
        // 保留探测与喂点作换机器验证；见 InkTrailEnabled 的注释。
        // 2026-10-10 试验C：照 Chromium 的接法补上"挂视觉树"这一步 → **系统真的画了**（wetinktest PASS）。
        // 2026-10-11：改"按窗口探测"——以前用静态 `InkTrailNote == "未尝试"` 当门槛，
        // 多屏时第二个窗口整个跳过 → 副屏永远没有轨迹。
        if (!_trailProbed)
        {
            _trailProbed = true;
            // ★ **系统湿墨只在 Win11 24H2 以上启用**（用户 2026-10-16 定："把系统的湿墨托管
            //   调整到 24H2 以上，等稳定以后才用；之前是不用的，走（我们）那个软件就行了"）。
            //   依据：21H2~23H2 这条通道有系统已知缺陷（顶三分之一屏闪烁等），Chromium 也是
            //   从 24H2 才启用；低于 24H2 时宁可全走自绘，免得把不稳定的系统通道"有就用"进来。
            //   （.NET 5+ 的 Environment.OSVersion 取的是 RtlGetVersion 的真实版本号。）
            int osBuild = Environment.OSVersion.Version.Build;
            if (osBuild < 26100)
            {
                InkTrailAvailable = false;
                InkTrailNote = $"系统低于 Win11 24H2（build {osBuild}）——不启用系统湿墨，全走软件自绘";
            }
            else
            {
                try
                {
                    _inkTrailDevice = _dcomp.QueryInterfaceOrNull<IDCompositionInkTrailDevice>();
                    if (_inkTrailDevice != null)
                    {
                        _inkTrail = _inkTrailDevice.CreateDelegatedInkTrailForSwapChain(_swapChain);
                        InkTrailAvailable = _inkTrail != null;
                        InkTrailNote = InkTrailAvailable ? "可用" : "接口可用但创建轨迹失败";
                        if (InkTrailAvailable)
                        {
                            // ★ 关键一步（Chromium 的 InkTrail 就是这么挂的，我们以前漏了）：
                            //   轨迹对象必须**作为视觉内容放进视觉树**、叠在交换链上面，系统才会画。
                            //   只创建不挂树 → 调用全成功、gen 递增，但屏幕上永远没有墨。
                            try
                            {
                                _inkVisual = _dcomp.CreateVisual();
                                _inkVisual.SetContent(_inkTrail).CheckError();
                                // insertAbove:false + 参照 null = 加到所有兄弟**之上**
                                //（语义说明见 CreateDeviceResources 里 AddVisual 那一段）。
                                _visual.AddVisual(_inkVisual, false, null).CheckError();
                                _dcomp.Commit().CheckError();
                                InkTrailNote += "（已挂视觉树）";
                            }
                            catch (Exception layEx)
                            {
                                InkTrailAvailable = false;
                                InkTrailNote += "（挂视觉树失败: " + layEx.Message + "）";
                            }
                        }
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
    /// 取当前该用的底色画刷。**三层**（2026-10-09"两张纸"模型）：
    ///   · 白板开着 → 板色（可调不透明度）——白板画在文档**上面**（页图会让位，见 SyncDocPages）；
    ///   · 白板关着、文档开着、不在穿透 → **文档的"纸"**（白底：页缝/页边不露桌面）；
    ///   · 其余（透明批注）→ 全透明。
    /// 底色变了才重建画刷，正常每帧不分配。
    /// </summary>
    private ID2D1SolidColorBrush BoardBrush(InkEngine app)
    {
        // 白板可以整体调"不透明度"（用户 2026-09-17）：底色这一层半透明，
        // 下面的题目隐约透出来；**笔迹不受影响**（它画在底色上面、本身不透明）。
        var want = app.BoardOn
            ? new Color4(app.BoardColor.R, app.BoardColor.G, app.BoardColor.B,
                         app.BoardColor.A * app.BoardOpacity)
            : (app.DocView.IsOpen && !app.PassThrough ? app.DocPaperColor : Transparent);
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

}
