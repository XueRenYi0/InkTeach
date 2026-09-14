using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

internal enum PassThroughMode
{
    /// <summary>WM_NCHITTEST -> HTTRANSPARENT only (safe, but the documented
    /// hand-off only reaches windows owned by the same thread).</summary>
    HitTest = 0,

    /// <summary>Adds WS_EX_TRANSPARENT to the window style on top of HitTest.</summary>
    ExTransparent = 1,

    /// <summary>Adds WS_EX_LAYERED | WS_EX_TRANSPARENT (the classic click-through
    /// recipe). Kept as a probe because it may disturb DirectComposition.</summary>
    LayeredTransparent = 2,
}

public class InkEngine
{
    // ---- document / interaction state ------------------------------------
    internal readonly InkDocument Doc = new();
    internal readonly LaserTrail Laser = new();
    internal Stroke ActiveStroke;
    internal Tool Tool = Tool.Pen;
    /// <summary>Tool sizes are authored in logical pixels and scaled by the
    /// monitor DPI at use. Without this everything looks half-size on a 150%
    /// display, which is exactly how a 18px eraser turns into an unusable dot.</summary>
    internal float DpiScale = 1f;

    /// <summary>
    /// **笔记橡皮擦**的笔尖半径（逻辑像素）。
    ///
    /// 比以前小得多（原来是 22，那是"面积擦"的量级）：这一个擦的是**整笔**，
    /// 笔尖一大，一个字里被压到的笔画全没了，"擦掉其中一横"就做不到。
    /// 现在默认 10 逻辑像素 ≈ 一支笔的笔尖宽度，投影上也还看得见落点。
    /// </summary>
    internal float EraserRadiusLogical = 10f;

    /// <summary>
    /// **面积橡皮擦**的矩形**高度**（逻辑像素）。宽度由它除以黄金比得到，
    /// 所以"大小可调"只需记一个数（Ctrl+Alt+6 换档）。
    ///
    /// **竖着的**（高 &gt; 宽）：这是实测之后改的。第一版做成横的（宽 90 × 高 56），
    /// 理由是"压着一行字横着扫"，用起来才发现老师多数是**从上往下抹一段**
    /// （一个字的竖排、一列板书、一块写错的内容），竖矩形才顺手。
    /// </summary>
    internal float AreaEraserHeightLogical = 150f;

    /// <summary>
    /// 黄金分割比。面积橡皮的矩形取它做长宽比（不取圆形，也不取正方形）。
    ///
    /// 为什么是它而不是随手一个 3:2：老师的用法是"压着一行字横着扫一下"，
    /// 需要"横向够宽、纵向别误伤上下行"。黄金比正好给出这个手感，
    /// 而且**比例一固定，改大小就只有一个自由度**——界面上只需一个"大小"，
    /// 不用让用户分别拖长和宽（少一个自由度就少一类误操作）。
    /// </summary>
    internal const float GoldenRatio = 1.618f;

    /// <summary>面积橡皮的矩形宽度：高度 ÷ 黄金比（竖矩形，所以宽 &lt; 高）。</summary>
    internal float AreaEraserWidthLogical => AreaEraserHeightLogical / GoldenRatio;

    /// <summary>面积橡皮的大小档位（逻辑像素，指的是**高度**）。</summary>
    internal static readonly float[] AreaEraserHeightPresets = { 60f, 100f, 150f, 220f, 320f };
    internal int AreaEraserHeightIndex = 2;

    /// <summary>笔记橡皮的笔尖档位（逻辑像素，半径）。</summary>
    internal static readonly float[] EraserRadiusPresets = { 5f, 10f, 18f, 28f };
    internal int EraserRadiusIndex = 1;

    /// <summary>
    /// 面积橡皮当前压在画布上的矩形（**画布坐标**，轴对齐）。
    ///
    /// 输入、擦除范围、光标绘制三处**必须**用这一个函数——分开算的话，
    /// "看到的框"和"擦掉的范围"会差几像素，那是投影上能看出来的错位。
    /// </summary>
    internal RectF AreaEraserRect(float x, float y)
    {
        float h = AreaEraserHeightLogical * DpiScale;
        float w = h / GoldenRatio;
        return new RectF
        {
            MinX = x - w * 0.5f, MinY = y - h * 0.5f,
            MaxX = x + w * 0.5f, MaxY = y + h * 0.5f,
        };
    }
    internal float PenWidthLogical = 3f;
    internal float HighlighterWidthLogical = 18f;
    /// <summary>
    /// 激光笔的粗细。以前激光借的是**笔宽**（`Engine.cs` 里那条
    /// `tool == Highlighter ? Highlighter : Pen`），于是"切粗细"对激光没意义、
    /// 落点的点大小也没法跟笔迹对上。现在四种工具各记各的。
    /// </summary>
    internal float LaserWidthLogical = 4f;
    internal float EraserRadius => EraserRadiusLogical * DpiScale;
    private float _lastEraseX, _lastEraseY;

    // ---- 顶点编辑（双击图形进入，见 VertexHandles 的注释）---------------

    /// <summary>正在顶点编辑模式：手柄换成顶点，缩放手柄收起。</summary>
    internal bool VertexEdit;

    /// <summary>
    /// 这一刻**显示顶点手柄、收起缩放手柄**吗。
    ///
    /// 两种情形：① 用户进了顶点编辑模式；② 选中的是"只认顶点"的图形
    /// （直线/箭头/三角形/平行四边形，见 VertexHandles.PrefersVertices）——
    /// 后者不需要切模式，选中即顶点。
    ///
    /// 抽成一个属性是因为**三处必须一致**：命中判定、绘制、脏区。少一处，
    /// 就会出现"看得见手柄却点不中"或者"手柄消失后留一块残影"。
    /// </summary>
    internal bool ShowsVertexHandles
        => VertexHandles.CanEdit(Doc.Selected)
           && (VertexEdit || VertexHandles.PrefersVertices(Doc.Selected[0]));
    /// <summary>正在拖第几个顶点（-1 = 没在拖）。</summary>
    internal int _vertexDragIndex = -1;
    private Stroke _vertexDragTarget;

    /// <summary>截图工具的拖动矩形（窗口坐标，物理像素）。</summary>
    internal bool CaptureActive;
    internal float CapMinX, CapMinY, CapMaxX, CapMaxY;

    /// <summary>
    /// 上一次按下（时间 + 位置 + 对象），用来识别双击。
    ///
    /// 为什么不接系统的 WM_LBUTTONDBLCLK：覆盖层收到的指针消息走的是
    /// WM_POINTER 这一套（笔、触摸、鼠标统一），双击消息是鼠标专有的老通道，
    /// 笔上根本没有。自己按"400ms 内、8 像素内、同一个对象"判定，
    /// 三种设备的手感就一致了。
    /// </summary>
    private double _lastDownMs = double.MinValue;
    private float _lastDownX, _lastDownY;
    private const double DoubleClickMs = 400;
    private const float DoubleClickSlopLogical = 8f;

    /// <summary>Pen width presets, in logical pixels. Cycled with Ctrl+Alt+W
    /// until there is a proper on-screen control for it.</summary>
    internal static readonly float[] WidthPresets = { 1.5f, 3f, 6f, 10f, 16f, 24f };
    internal int WidthPresetIndex = 1;

    /// <summary>
    /// 每种工具各自的粗细档位。**笔和荧光笔的档位不是一回事**：荧光笔是"涂一大条"，
    /// 1.5 像素这种档位对它没意义；激光更小。共用一张表的结果就是
    /// "选了荧光笔按 Ctrl+Alt+6 没反应"（它改的是笔宽）——实测就是这个 bug。
    /// </summary>
    internal static readonly float[] HighlighterWidthPresets = { 8f, 18f, 32f };
    internal static readonly float[] LaserWidthPresets = { 4f, 8f, 14f };
    internal int HighlighterWidthIndex = 1;
    internal int LaserWidthIndex = 0;

    /// <summary>当前工具的粗细（逻辑像素）。落点反馈、界面状态都读它。</summary>
    internal float CurrentToolWidthLogical => Tool switch
    {
        Tool.Highlighter => HighlighterWidthLogical,
        Tool.Laser => LaserWidthLogical,
        _ => PenWidthLogical,
    };

    private double _lastSampleMs = -1;
    internal bool PointerInside;
    internal float PointerX, PointerY;
    /// <summary>
    /// 最后一次指针消息来自什么设备（鼠标 / 笔 / 触摸）。**悬停的移动也要更新**——
    /// 指针形状直接取决于它，见 <see cref="ComputeCursorKind"/>。
    /// </summary>
    internal uint LastPointerType = Native.PT_MOUSE;
    internal bool ShowHud = true;
    internal string HudText = "";
    internal bool MarqueeActive;

    // ---- 选择手势（框选工具 = 选择工具）---------------------------------
    // 四种情形在这里分流：点操作条按钮 / 拖手柄 / 在选中范围里整体拖 / 空白处重新框选。
    internal bool SelDragging;
    internal int SelBarHover = -1;          // 悬停的按钮，-1 = 无（给渲染用）
    /// <summary>
    /// 批注键盘模式。开 = 键盘归批注层（编辑快捷键生效）；
    /// 关 = 覆盖层永不抢焦点，键盘还给下层程序。
    ///
    /// 这是个**明确的取舍**：开着的时候放映中的 PPT 收不到键盘，因为覆盖层
    /// 拿着前台焦点。用户拍板"先不考虑 PPT，先把底层做好"，所以默认开，
    /// 用 Ctrl+Alt+K 切换。
    /// </summary>
    internal bool KeyboardMode = true;

    /// <summary>
    /// 相机：画布坐标 → 屏幕坐标的纵向偏移。**滚动只改这一个数**，
    /// 不动任何对象数据——这是滚动能做到 O(1) 的前提。
    ///
    /// 往下滚 = 内容上移 = 这个值变负。只做纵向：板书是纵向累积的，
    /// 横向没有用武之地，还省掉双指横扫与"画横线"的手势冲突。
    /// </summary>
    internal float ViewOffsetY;

    /// <summary>
    /// 最后一次"滚动条该露面"的时刻（滚动或悬停）。滚动条按这个时间淡出——
    /// 静止 3 秒后消失（InkClass 实测 1.5 秒太快，用户会找不到它）。
    /// </summary>
    internal double ScrollBarActiveAtMs = double.MinValue;

    /// <summary>指针正停在滚动条上：滑块加粗、不淡出。拖动中也算（滑块不能半路淡掉）。</summary>
    internal bool ScrollBarHover;
    internal bool ScrollBarDragging;
    /// <summary>按下时指针相对滑块顶边的偏移，拖动中保持它不变，滑块才不"跳"。</summary>
    private float _scrollGrabDy;
    private OverlayWindow _scrollWindow;

    /// <summary>屏幕坐标 → 画布坐标（相机）。输入进来第一件事就是过这个。</summary>
    internal void ScreenToCanvas(ref float x, ref float y) => y -= ViewOffsetY;

    /// <summary>当前视口在**画布坐标**里的范围（相机之后）。</summary>
    internal RectF ViewportCanvas => new()
    {
        MinX = _virtualX,
        MinY = _virtualY - ViewOffsetY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY - ViewOffsetY + _virtualH,
    };

    /// <summary>
    /// 画布范围 = 内容边界 ∪ 当前视口 ∪ **视口下方一屏空白**。
    ///
    /// 最后那一屏是必须的：只取"内容 ∪ 视口"的话，**空文档的画布恰好一屏**，
    /// 下边界就是 0，往下滚立刻被夹回去——而滚不动就写不到下面去，写不到下面
    /// 内容就不长，内容不长范围就不长……**死锁**。
    /// （实测踩过：用户一打开软件，滚轮完全没反应，就是这个。）
    ///
    /// 留一屏空白等于"永远有一张白纸在下面"，像记事本一样。
    /// </summary>
    internal RectF CanvasExtent
    {
        get
        {
            var vp = ViewportCanvas;
            var r = Doc.Extent(vp);
            r.Add(new RectF
            {
                MinX = vp.MinX, MinY = vp.MaxY,
                MaxX = vp.MaxX, MaxY = vp.MaxY + _virtualH,
            });
            return r;
        }
    }
    // 这两个给自检看（--cursortest 要构造"拖拽中"的状态）。
    internal SelHandle _dragHandle = SelHandle.None;
    internal bool _dragIsMove;
    private SelectionFrame _dragFrame;

    /// <summary>正在拖旋转手柄——度数标签靠它决定显不显示。</summary>
    internal bool SelRotating;
    /// <summary>当前这一拖转了多（度，正 = 顺时针；相对按下那一刻）。</summary>
    internal float SelRotationDegrees;
    /// <summary>这个角度是"吸"出来的（90° 或 Shift 15° 网格），标签要变色提示。</summary>
    internal bool SelRotationSnapped;

    private Vector2 _dragStartPoint;
    /// <summary>按下旋转手柄那一刻，选区的绝对朝向（度，[0,360)）。</summary>
    private float _selRotBaseDeg;
    private Matrix3x2[] _dragStartXform;
    private Stroke[] _dragTargets;
    private Matrix3x2 _selDragMatrix = Matrix3x2.Identity;
    /// <summary>调试开关：不画自己那一笔，只留系统合成器画出来的委托轨迹。</summary>
    internal bool SuppressActiveStroke;
    internal float MqMinX, MqMinY, MqMaxX, MqMaxY;
    internal bool PassThrough;
    /// <summary>
    /// Windows only honours click-through for a *layered* window, so the
    /// default has to include WS_EX_LAYERED. The other modes are kept as
    /// controls for the automated pass-through test.
    /// </summary>
    internal PassThroughMode PassMode = PassThroughMode.LayeredTransparent;
    internal bool NoContentCache;
    internal double NowMs;

    internal static readonly Color4 PenColor = InkPalette.PenDefault;
    internal static readonly Color4 HighlighterColor = InkPalette.HighlighterDefault;

    // 下面这些标成 internal 而不是 private，是给开发期测试宿主看的：
    // InkTeach 通过 InternalsVisibleTo 继承引擎、直接读内部状态来做自动化
    // 测量。产品界面走 IOverlayUi，不碰这些。
    internal readonly List<OverlayWindow> _windows = new();
    /// <summary>穿透时用来"找回界面"的小圆钮（独立窗口，见 RestorePill）。</summary>
    internal RestorePill Pill;
    internal IntPtr _hInstance;
    internal string _className;
    private Native.WndProcDelegate _wndProc;
    private static readonly Dictionary<IntPtr, OverlayWindow> s_map = new();
    /// <summary>属于"恢复小圆钮"的窗口句柄。它们的消息要单独处理。</summary>
    private static readonly HashSet<IntPtr> s_pillHwnds = new();

    internal bool _quit;
    internal bool _dirty = true;
    private bool _animating;
    internal bool _drawing;
    private uint _activePointer;
    private uint _activePointerType;
    internal int _cntDown, _cntMove, _cntUp, _cntCaptureLost;
    /// <summary>诊断用：窗口总共收到多少消息、其中鼠标消息/指针消息各多少。</summary>
    internal int _cntMsgTotal, _cntWmMouseMove, _cntWmLButtonDown, _cntWmPointerDown, _cntWmPointerUpdate;
    internal string _lastStrokeReport;
    private string _lastSimplifyInfo;
    internal bool argsContainActivate;
    /// <summary>调试用：每帧故意睡这么多毫秒，模拟低配机器上渲染跟不上。</summary>
    internal int _artificialLagMs;
    private long _hotkeysRegistered;

    /// <summary>
    /// 键位表：动作 → 键 + 作用域。**可配置、可查冲突、可落盘**（见 KeyBindings.cs）。
    /// 启动时先读用户配置的覆盖项，退出时把改动写回去。
    /// </summary>
    internal KeyMap Keys = KeyMap.Default();

    /// <summary>
    /// 记住的工具尺寸（逻辑像素）。启动时读、退出时写；改过才写。
    /// 一个文件里两段："keys" 和 "tools"（见 InkSettings）。
    /// </summary>
    internal InkSettings.ToolSettings Sizes = new();
    internal int _virtualX, _virtualY, _virtualW, _virtualH;
    internal double _autoExitAt = double.MaxValue;

    // ---- perf accounting --------------------------------------------------
    internal readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
    internal double _fps;
    private int _frames;
    private double _fpsWindowStart;
    private TimeSpan _lastCpu;
    private double _lastCpuWall;
    private double _cpuPercent;
    internal double _peakWorkingSetMb;

    // ---- 界面接入口 -------------------------------------------------------
    // 引擎只认 IOverlayUi。界面通过 UiHost 改引擎状态，永远不直接碰文档。
    internal UiHost Host;
    internal IOverlayUi Ui;
    internal bool UiInvalidatePending;
    internal Color4 CurrentColor = InkPalette.PenDefault;
    internal Color4 HighlighterCurrent = InkPalette.HighlighterDefault;
    /// <summary>
    /// 默认**不做手写美化**，就是等宽笔迹。
    ///
    /// 用户实测后的结论：手写美化"不是那么必须"，而且关掉之后观感更干净。
    /// 所以这里默认走 Precise（等宽、不模拟速度、无渐细）。
    /// 需要时用 --preset handwriting 之类打开，算法代码保留。
    /// </summary>
    internal PenPreset PenPresetValue = PenPreset.Precise;
    internal bool UiCapturing;

    /// <summary>
    /// 白板模式：给整块画布铺一层不透明的底色，遮住桌面和别的程序。
    /// 关掉就是原本的"透明批注"——直接写在别人的 PPT、网页上面。
    /// </summary>
    internal bool BoardOn;
    internal Color4 BoardColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal double _lastRebuildMs;
    internal double _lastRecordMs;
    internal double _lastPresentMs;
    // 分块缓存的状态（面板与自检读数）
    internal int _tilesUsed, _tilesVisible, _tilesBudget, _tilesRasterized;
    private double _inputToPresentMs;
    private double _lastInputMs = -1;

    // ---- 延时探针（见 Latency.cs）---------------------------------------
    /// <summary>打开后每画完一帧就记一条延时样本。只有测量模式会打开。</summary>
    internal bool LatencyRecording;
    internal readonly LatencyRecorder Latency = new();
    /// <summary>最近一条指针消息自带的硬件时标（QPC 计数，可能是 0 = 不可信）。</summary>
    private ulong _lastInputPerfQpc;
    /// <summary>最近一条指针消息被我们拿到的时刻（QPC）。</summary>
    private ulong _lastInputMsgQpc;
    /// <summary>已经记过样的那条消息，避免同一帧重复记。</summary>
    private ulong _lastSampledMsgQpc;
    /// <summary>上一条指针消息到达的时刻，用来量实际输入采样间隔。</summary>
    private ulong _prevInputMsgQpc;
    /// <summary>这一帧收进了几个指针采样点。</summary>
    private int _framePoints;
    /// <summary>累计渲染帧数。测量模式用它算真实帧率（样本条数 ≠ 帧数）。</summary>
    internal long FrameCounter;
    internal double _nextLogAt;
    private double _accRecord, _accPresent, _accHud, _accStats;
    private int _accFrames;
    private double _nextStatsAt;
    private double _lastStatsMs;
    /// <summary>上一帧画性能面板花了多久。面板自己的代价也要看得见，否则它会掩盖问题。</summary>
    private double _lastHudMs;
    private double _hudAvgMs;        // 面板代价的窗口均值（单帧会被"重排那一帧"带偏）
    private double _accPanel;
    private double _hudRedrawMs, _hudRedrawSum, _hudRedrawPerSec;
    private double _hudRateWall;
    private long _hudRedrawCount, _hudRedrawTotal, _hudRedrawAtWall;
    private double _workingSetMb;
    private double _privateMb;
    private double _privateWsMb;      // 专用工作集 = 任务管理器"内存"列
    private double _sharedCommitMb;   // 共享提交（工作集里有多少是别的进程也有的）
    private double _gpuMb;
    private double _lastTopmostMs;

    /// <summary>Everything that talks to the OS for reporting: process times,
    /// working set, CPU share. Runs a few times a second, never per frame.</summary>
    private void UpdateStats()
    {
        var sw = Stopwatch.StartNew();

        double wall = _clock.Elapsed.TotalMilliseconds;
        // 一次调用拿齐内存三个口径；CPU 用 GetProcessTimes。
        // 以前走 Process.GetCurrentProcess()+Refresh()，那条路要几百微秒到几毫秒
        // （统计那一栏 3~4 ms 的大头就是它）——面板自己的开销不该跟被观测的东西一个量级。
        var c = new Native.PROCESS_MEMORY_COUNTERS_EX2();
        c.cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>();
        bool memOk = Native.GetProcessMemoryInfo(Native.GetCurrentProcess(), ref c, c.cb);
        if (memOk)
        {
            _workingSetMb = c.WorkingSetSize.ToUInt64() / 1048576.0;
            _privateMb = c.PrivateUsage.ToUInt64() / 1048576.0;
            double pws = c.PrivateWorkingSetSize.ToUInt64() / 1048576.0;
            // 老系统上 EX2 的后两个字段是 0：那时退回"工作集"，不编一个假数出来。
            _privateWsMb = pws > 0 ? pws : _workingSetMb;
            _sharedCommitMb = c.SharedCommitUsage / 1048576.0;
        }

        if (Native.GetProcessTimes(Native.GetCurrentProcess(), out _, out _, out long kernel, out long user))
        {
            var cpu = TimeSpan.FromTicks(kernel + user);
            double cpuDelta = (cpu - _lastCpu).TotalMilliseconds;
            double wallDelta = wall - _lastCpuWall;
            if (wallDelta > 250)
            {
                _cpuPercent = cpuDelta / wallDelta / Environment.ProcessorCount * 100.0;
                _lastCpu = cpu;
                _lastCpuWall = wall;
            }
        }

        _gpuMb = GpuUsedMb();
        if (_workingSetMb > _peakWorkingSetMb) _peakWorkingSetMb = _workingSetMb;
        sw.Stop();
        _lastStatsMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Stamped whenever a pointer message is handled, so the loop can
    /// report how long input took to reach the screen.</summary>
    private void StampInput() => _lastInputMs = _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// 把"这一帧覆盖的那条最新输入"记成一条延时样本。调用点在每个窗口都
    /// Present 之后——只有到那时候才知道这一帧实际什么时候被交出去。
    ///
    /// 只记**新**输入：同一帧里收到多条指针消息时以最后一条为准（它才是
    /// 屏幕上那一笔的笔尖位置），已经记过的就不再记。
    /// </summary>
    private void RecordLatencySample()
    {
        if (_windows.Count == 0) return;
        if (_lastInputMsgQpc == 0 || _lastInputMsgQpc == _lastSampledMsgQpc) return;
        var w0 = _windows[0];
        if (w0.LastPresentEndQpc < _lastInputMsgQpc) return;   // 这一帧还没把输入交出去

        _lastSampledMsgQpc = _lastInputMsgQpc;

        // 计时起点优先用**采样点自己的时标**，而不是"我们读到它的时刻"。
        //
        // 原因是消息会被系统合并：注入 140 Hz 的输入，应用实际只收到约 60 条
        // WM_POINTERUPDATE（其余被合并进 historyCount）。用"读到时刻"当起点，
        // 会把"系统什么时候肯把消息给我们"算进应用延时里，量出来一半的帧
        // 是 1.5 ms、另一半是 16 ms——那是投递节奏，不是渲染慢。
        // 用采样点时标当起点，才是"笔尖动 → 这一帧交出去"的干净口径。
        bool perfOk = HardwareStampUsable(_lastInputPerfQpc, _lastInputMsgQpc);
        ulong origin = perfOk ? _lastInputPerfQpc : _lastInputMsgQpc;

        var s = new LatencySample
        {
            InputToMsgMs = perfOk ? Qpc.Ms(_lastInputPerfQpc, _lastInputMsgQpc) : double.NaN,
            MsgToPresentMs = Qpc.Ms(origin, w0.LastPresentStartQpc),
            PresentBlockMs = Qpc.Ms(w0.LastPresentStartQpc, w0.LastPresentEndQpc),
            PointsInFrame = _framePoints,
            PresentCount = w0.LastPresentCount,
        };
        // 合计从"采样点产生"算起（拿不到采样点时标就退回"读到消息"那一刻）。
        s.TotalMs = s.MsgToPresentMs + s.PresentBlockMs;

        // 上屏时刻：窗口化合成交换链上，GetFrameStatistics 给的是"最近一次真正
        // 上屏的帧"。它可能比我们这一帧早（负值）——那种情况不算，标记为不可得。
        if (w0.LastFrameStatsOk && w0.LastDisplayQpc > w0.LastPresentEndQpc)
        {
            s.PresentToDisplayMs = Qpc.Ms(w0.LastPresentEndQpc, w0.LastDisplayQpc);
            s.HasDisplay = s.PresentToDisplayMs < w0.RefreshPeriodMs * 4;
        }

        Latency.Add(s);
        _framePoints = 0;
    }

    /// <summary>测量模式在每个场景开始前调用：把探针的游标清干净，避免上一场景的
    /// 残留时标把第一条样本算成"等了三百毫秒"。</summary>
    internal void ResetLatencyProbe()
    {
        _lastInputMsgQpc = 0;
        _lastSampledMsgQpc = 0;
        _prevInputMsgQpc = 0;
        _framePoints = 0;
    }

    /// <summary>
    /// 判断 POINTER_INFO.PerformanceCount 能不能当 QPC 用。
    ///
    /// 真实手写笔上它是硬件的 QPC 时标，值得采信；但**合成输入（SendInput /
    /// 注入）上它可能是 0 或别的计数器**，直接减会得到荒谬的数。所以先做
    /// 两条检查：非零、且不晚于我们收到它的时刻、且差值在 2 秒以内。
    /// 不通过就写 NaN，报告里那一列会是"不可得"，不会拿假数当结论。
    /// </summary>
    private static bool HardwareStampUsable(ulong stamp, ulong msgQpc)
        => stamp != 0 && stamp <= msgQpc && (msgQpc - stamp) * Qpc.TicksToMs < 2000.0;

    internal string LastCaptureReport = "";

    // =====================================================================
    //  Startup
    // =====================================================================

    internal int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Native.EnableMouseInPointer(true);

        Gfx.Init();
        NowMs = _clock.Elapsed.TotalMilliseconds;

        _hInstance = Native.GetModuleHandle(null);
        _className = "InkTeachOverlay_" + Guid.NewGuid().ToString("N");
        if (!RegisterWindowClass())
            return 2;

        string mode = args.Length > 0 ? args[0] : "";

        // 宿主自己的启动分支（开发期的点击目标、截图工具等）。返回 true
        // 表示这条命令行已经由宿主处理完，引擎不再往下走。产品界面不需要覆写。
        if (PrepareHostStartup(mode, args, out int hostExit))
            return hostExit;

        // --nohud：关掉调试性能面板。它是给开发看的，每帧要花约 1.9 ms
        // （文字排版 + 进程计数），测底层性能时必须排除掉，否则量到的是
        // 测量工具本身而不是渲染引擎。
        if (args.Contains("--nohud")) ShowHud = false;

        // 对照实验用：--nohist 关掉脏区的多帧回溯，应当立刻出现残影，
        // 用来证明残影测试本身是有效的（而不是永远通过）。
        if (args.Contains("--nohist")) OverlayWindow.TransientHistoryFrames = 0;
        // ---- 委托墨迹轨迹（湿墨交给系统合成器画）-----------------------------
        //
        // 默认策略（2026-09-13 实测之后定）：**系统里真有笔设备就默认开**，
        // 没有就关。理由：这条通道对鼠标输入没有意义（实测合成鼠标下它不画），
        // 而对手写笔它是"笔尖跟手"的唯一正解——湿墨由 DWM 画，我们自己的
        // 渲染延时（敲定前实测约 1~3 帧）就不再压在笔尖上。
        //
        // 以前默认关是因为"本机没有笔、验证不了"。这台机器有笔数字化器，
        // 而且引擎只对 PT_PEN 调用它，鼠标路径完全不受影响。
        bool penDevicePresent =
            (Native.GetSystemMetrics(Native.SM_DIGITIZER)
             & (Native.NID_INTEGRATED_PEN | Native.NID_EXTERNAL_PEN)) != 0;
        OverlayWindow.InkTrailEnabled = penDevicePresent && !args.Contains("--noinktrail");
        if (args.Contains("--inktrail")) OverlayWindow.InkTrailEnabled = true;
        if (args.Contains("--noinktrail")) OverlayWindow.InkTrailEnabled = false;

        // ---- 呈现节奏 ---------------------------------------------------------
        // 默认改成"等到合成边界再抽输入、立刻 Present(0)"。实测这一项把
        // "Present 返回 → 像素亮"从 3.5 个刷新周期压到 1 个（见
        // reports/延时-实测与优化.md）。--nopace 退回老的 Present(1) 阻塞行为。
        OverlayWindow.VBlankPaced = !args.Contains("--nopace");
        if (OverlayWindow.VBlankPaced) OverlayWindow.LatencyWaitEnabled = true;
        if (args.Contains("--norealize")) OverlayWindow.RealizationEnabled = false;
        if (args.Contains("--mtraster")) OverlayWindow.MultithreadedRaster = true;
        if (args.Contains("--latencywait")) OverlayWindow.LatencyWaitEnabled = true;
        if (args.Contains("--vblankpace"))
        {
            // 低延时配速：等到合成边界再抽输入、Present(0)，不再把线程卡在
            // Present(1) 的垂直同步里。需要等待对象在，否则 Present 仍是 syncInterval=1，
            // 配速反而变成"多等一次"。
            OverlayWindow.LatencyWaitEnabled = true;
            OverlayWindow.VBlankPaced = true;
        }
        int bufIdx = Array.IndexOf(args, "--bufcount");
        if (bufIdx >= 0 && bufIdx + 1 < args.Length && int.TryParse(args[bufIdx + 1], out int bc) && bc >= 2 && bc <= 4)
            OverlayWindow.BufferCount = bc;
        // 笔迹优化不由引擎决定：核心不认识"平滑""笔锋"这些概念，装不装优化器
        // 由宿主说了算（见 InkOptimizer.cs，以及 InkTeach 里的 --smooth 开关）。
        argsContainActivate = args.Contains("--activate");
        int lagIdx = Array.IndexOf(args, "--laggy");
        if (lagIdx >= 0 && lagIdx + 1 < args.Length && int.TryParse(args[lagIdx + 1], out int lagMs))
            _artificialLagMs = lagMs;

        _virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        _virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        _virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        _virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        Console.WriteLine($"virtual desktop: {_virtualW}x{_virtualH} at ({_virtualX},{_virtualY})");

        if (!CreateOverlays())
            return 3;

        // 键位：先读用户配置的覆盖项，再注册全局热键。读坏了不阻塞启动——
        // 用默认键位跑起来，但把问题逐条打出来（静默回退最坑人）。
        if (InkSettings.PathOverride == null && args.Contains("--nosettings"))
            InkSettings.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                              "inkteach-nosettings.json");
        // **自检模式一律不碰用户的配置文件。**
        //
        // 这一条是踩出来的：`--cursortest` 会按几次"换粗细"，而换粗细现在会
        // 记进 settings.json（那是给老师用的：挑好的粗细下次还在）。于是跑一次
        // 自检，老师的笔宽就被改成自检里的值了——自检改用户设置，绝对不行。
        // 给了模式名（--xxx）就说明是自检/摆样，一律走临时文件；
        // 交互模式（无参数）才读写真正的配置。
        if (InkSettings.PathOverride == null && mode.Length > 0)
        {
            InkSettings.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                              "inkteach-selftest-settings.json");
            // 删掉上次自检留下的内容：不然上一次自检改的粗细会变成这一次的"默认值"，
            // 自检之间互相污染（换粗细那条用例就是这么失败过一次）。
            try { System.IO.File.Delete(InkSettings.PathOverride); } catch { }
        }
        foreach (var w in InkSettings.Load(Keys, Sizes))
            Console.WriteLine("settings: " + w);
        ApplySavedToolSizes();

        RegisterHotkeys();

        // 自检/基准模式下面板默认关掉，除非显式 --hud。
        //
        // 为什么：面板是个大黑框，画在屏幕左上角**压在一切之上**。而很多自检的
        // 判据恰恰是"数屏幕上的墨"（--coordtest / --inputtest / --erasertest /
        // --ghosttest / --tiletest / --appendtest / --passtest / --rotatetest…），
        // 面板往上一盖，那些用例会**假失败**——面板越大盖得越多，这次把面板
        // 放大到 700×258 逻辑像素就当场踩到了。交互模式仍然默认开着。
        if (mode.Length > 0 && !args.Contains("--hud")) ShowHud = false;

        int dispatch = RunModeDispatch(mode, args);
        if (dispatch >= 0)
            return dispatch;

        // 走到这里说明是**交互模式**：把键位表打出来（连同注册失败的条目）。
        // 测试模式不打印——三十多行会把每条自检的日志淹掉。
        // 以前这个方法**根本没人调用**，所以键位一直只活在 README 里。
        PrintUsageHelp();

        Loop();
        Shutdown();
        return 0;
    }

    /// <summary>
    /// 启动阶段、窗口创建之前的宿主分支。引擎本身没有这样的模式，
    /// 开发期的截图/点击目标工具挂在宿主（InkTeach）里。
    /// </summary>
    protected virtual bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }

    /// <summary>
    /// 窗口建好、快捷键注册完之后的分支。返回 -1 表示"没有特殊模式，
    /// 正常跑消息循环"；返回 &gt;= 0 表示直接以该值退出。
    /// 产品界面只会拿到 -1；测试模式由 InkTeach 覆写。
    /// </summary>
    protected virtual int RunModeDispatch(string mode, string[] args) => -1;

    private bool RegisterWindowClass()
    {
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
            // 兜底光标。**这一句是"永远转圈"的解药**：hCursor 留空（NULL）时
            // 系统会给一个默认的等待类光标，用户看到的就是一直转圈。
            // 具体形状由 WM_SETCURSOR 按状态覆盖（见 ApplyCursor）。
            hCursor = Cursors.System(Native.IDC_ARROW),
        };
        ushort atom = Native.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private bool CreateOverlays()
    {
        bool ok = true;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var w = new OverlayWindow();
            var err = w.Create(_hInstance, _className, r.Left, r.Top, r.Width, r.Height);
            if (err != null)
            {
                Console.WriteLine($"overlay create failed on monitor {hMon}: {err}");
                ok = false;
                return false;
            }
            s_map[w.Hwnd] = w;
            _windows.Add(w);
            Console.WriteLine($"overlay on monitor {hMon}: {r.Width}x{r.Height} at ({r.Left},{r.Top}) dpi={w.Dpi}");
            Console.WriteLine($"委托墨迹轨迹(InkTrail): {OverlayWindow.InkTrailNote}");
            return true;
        }, IntPtr.Zero);

        if (_windows.Count == 0)
        {
            Console.WriteLine("no monitors found");
            return false;
        }

        // Refresh the HUD ~4x a second so the numbers move even when idle.
        //
        // 顺便每隔 250 ms 重新"置顶"一次（WM_TIMER 里调 ReassertTopmost）。
        // 这个必须够快：**别的置顶窗口会把我们盖住**——实测输入法状态条
        // （wetype 的 StatusBarWnd）、桌面小组件（RocketView）都是 TOPMOST，
        // 谁最后调 SetWindowPos 谁在上面。被盖住的后果不只是"看不见"：
        // 合成/真实点击都会落到那个窗口上，批注一个字都画不出来。
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)1, 250, IntPtr.Zero);
        DpiScale = _windows[0].Dpi / 96f;

        // 找回界面用的小圆钮：单独一个窗口，因为它不能带穿透样式
        // （带了就收不到点击，也就没法"点回来"）。平时隐藏，不占地方。
        Pill = new RestorePill();
        var pillErr = Pill.Create(_hInstance, _className, _windows[0].Dpi);
        if (pillErr != null)
        {
            Console.WriteLine("恢复小圆钮创建失败: " + pillErr);
            Pill = null;
        }
        else
        {
            s_pillHwnds.Add(Pill.Hwnd);   // 它的消息由 WndProc 单独分支处理
        }

        Host?.UpdateScreen(LogicalVirtualScreen);
        Console.WriteLine($"DPI 缩放 {DpiScale:F2}（逻辑 {_windows[0].Width / DpiScale:F0}x{_windows[0].Height / DpiScale:F0}）");

        // 把键盘模式落到窗口样式上。字段默认是开的，但样式要等窗口建好才能改——
        // 不调这一句，默认值和实际样式就对不上（得按一次 Ctrl+Alt+K 才生效）。
        SetKeyboardMode(KeyboardMode);
        return ok;
    }

    private void RegisterHotkeys()
    {
        // 全局热键**不再是一串写死的 (id, key)**：动作与键位由 Keys 这张表决定，
        // id 只是"注册顺序"，HandleHotkey 拿它反查动作。这样键位可配置、可查冲突，
        // 而引擎里不认识"Ctrl+Alt+P"这种东西。
        IntPtr h = _windows[0].Hwnd;
        _hotkeyActions.Clear();
        Keys.GlobalFailures.Clear();

        foreach (var b in Keys.For(KeyScope.Global))
        {
            int id = _hotkeyActions.Count + 1;
            uint mod = Native.MOD_NOREPEAT;
            if ((b.Chord.Modifiers & KeyChord.ModCtrl) != 0) mod |= Native.MOD_CONTROL;
            if ((b.Chord.Modifiers & KeyChord.ModAlt) != 0) mod |= Native.MOD_ALT;
            if ((b.Chord.Modifiers & KeyChord.ModShift) != 0) mod |= Native.MOD_SHIFT;

            _hotkeyActions.Add(b.Action);
            if (!Native.RegisterHotKey(h, id, mod, b.Chord.Vk))
            {
                // **注册失败要留痕**：教室机上微信 / QQ / 输入法 / 教学软件都会抢键
                // （实测 Ctrl+Alt+W 就被微信截图占了）。以前这里只打一行控制台，
                // 用户根本不知道，只觉得"这个键没反应"。
                int err = Marshal.GetLastWin32Error();
                string msg = $"{b.Chord}（{KeyMap.Describe(b.Action)}）注册失败，错误 {err}"
                           + (err == 1409 ? "：已被别的程序占用" : "");
                Keys.GlobalFailures.Add(msg);
                Console.WriteLine("hotkey " + msg);
            }
            else _hotkeysRegistered++;
        }
        Console.WriteLine($"registered {_hotkeysRegistered}/{Keys.Bindings.Count(b => b.Scope == KeyScope.Global)} global hotkeys");
    }

    /// <summary>注册顺序 → 动作。按这个顺序 RegisterHotKey，WM_HOTKEY 的 id 就是它。</summary>
    private readonly List<KeyAction> _hotkeyActions = new();

    internal KeyAction ActionForHotkeyId(int id)
        => id >= 1 && id <= _hotkeyActions.Count ? _hotkeyActions[id - 1] : KeyAction.None;

    /// <summary>自检用：这批全局热键实际注册成功了几个。</summary>
    internal long HotkeysRegistered => _hotkeysRegistered;

    // =====================================================================
    //  Message loop
    // =====================================================================

    internal void Loop()
    {
        while (!_quit)
        {
            DrainMessages();
            if (_quit) break;

            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= _autoExitAt) break;

            Laser.Prune(NowMs);
            _animating = Laser.ActiveAt(NowMs) || _drawing;

            if (_dirty || _animating)
            {
                // VBlankPaced：先等到合成边界，**再抽一次消息**，然后画、提交。
                //
                // 顺序是有讲究的。等到边界之后抽，把"等边界期间到达的输入"
                // 也算进这一帧，笔尖就是最新的；反过来（先抽、再等一个周期）
                // 等于让最新的那一笔在队列里多躺一个刷新周期。
                if (OverlayWindow.VBlankPaced)
                {
                    Native.DwmFlush();
                    DrainMessages();
                    _dirty = true;
                }
                RenderAll();
                _dirty = false;
            }
            else
            {
                Native.WaitMessage();
            }
        }
    }

    /// <summary>把消息队列里现有的消息全部处理掉，不阻塞。测试模式复用同一份，
    /// 免得"测量用的循环"和"真正的循环"因为顺序不同而量出两个结论。</summary>
    internal void DrainMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) { _quit = true; break; }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        NowMs = _clock.Elapsed.TotalMilliseconds;
    }

    internal void RenderAll()
    {
        FrameCounter++;
        double frameStart = NowMs;
        double wall0 = _clock.Elapsed.TotalMilliseconds;

        // The HUD text and the process counters cost an order of magnitude more
        // than drawing the ink does, so they refresh a few times a second rather
        // than every frame. Measured before this change: 2.7 ms + 3.5 ms per
        // frame, against 0.7 ms for the actual rendering.
        var swHud = Stopwatch.StartNew();
        bool statsRan = false;
        if (wall0 >= _nextStatsAt)
        {
            // 面板显示时才需要 4 Hz 刷新；只打日志的话 1 Hz 就够。
            // 进程计数一次要 ~3 ms，没必要每秒跑 4 次。
            _nextStatsAt = wall0 + (ShowHud ? 250 : 1000);
            UpdateStats();
            // 只在真的要显示面板时才拼字符串。BuildHudText 会去查进程内存，
            // 那是整条路径里最贵的一步；面板关掉还在跑就纯属浪费——
            // 实测它让空闲 CPU 从 ~0.3% 涨到 2~3.6%（单核）。
            if (ShowHud) HudText = BuildHudText();
            statsRan = true;
        }
        swHud.Stop();

        // 相机写给各覆盖窗口：渲染的每一处变换都用它（见 OverlayWindow.CanvasToWindow）。
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }

        foreach (var w in _windows)
            w.RenderFrame(this);
        foreach (var w in _windows)
            w.Present();

        if (LatencyRecording) RecordLatencySample();

        // 界面自己要求的重画（InvalidateUi）已经在这一帧贴完，可以清掉了。
        UiInvalidatePending = false;

        // 穿透中小圆钮要跟着界面报的位置走（界面可能还在调整自己的占位）。
        if (PassThrough) UpdatePassThroughPill();

        // 调试：模拟"渲染跟不上"的低配机器，用来验证委托墨迹轨迹会不会补位。
        if (_artificialLagMs > 0) System.Threading.Thread.Sleep(_artificialLagMs);

        // Every window has now applied this round of changes, so the stale
        // regions can be dropped. Doing it here (rather than inside a window)
        // is what keeps multi-monitor setups correct.
        Doc.Dirty.Reset();
        Doc.AppendedSinceRender.Clear();
        Doc.StructureChangedSinceRender = false;

        var w0 = _windows[0];
        _lastRebuildMs = w0.LastRebuildMs;
        _lastRecordMs = w0.LastRecordMs;
        _lastPresentMs = w0.LastPresentMs;
        _tilesUsed = w0.LastTileCount;
        _tilesVisible = w0.LastTileVisible;
        _tilesBudget = w0.LastTileBudget;
        _tilesRasterized = w0.LastPatchCount;

        // How long the newest input took to reach the screen. This is our own
        // contribution; the display pipeline adds up to one more scan-out.
        // 只在"这一帧确实处理了新输入"时采样，而且采完就作废。
        //
        // 这里原来有个 bug：_lastInputMs 用完不清零，于是空闲帧（激光动画、
        // 或只是等垂直同步的那一帧）会把同一条旧输入再算一遍，而且越算越大，
        // 把 EMA 一路往上带。面板上"输入到上屏"那个数因此偏大且不收敛。
        if (_lastInputMs > 0)
        {
            double sample = _clock.Elapsed.TotalMilliseconds - _lastInputMs;
            if (sample >= 0 && sample < 200)
                _inputToPresentMs = _inputToPresentMs <= 0
                    ? sample
                    : _inputToPresentMs * 0.85 + sample * 0.15;
            _lastInputMs = -1;
        }

        double wall = _clock.Elapsed.TotalMilliseconds;
        _lastFrameMs = wall - frameStart;

        _frames++;
        if (wall - _fpsWindowStart >= 500)
        {
            _fps = _frames * 1000.0 / (wall - _fpsWindowStart);
            _frames = 0;
            _fpsWindowStart = wall;
        }

        _accRecord += _lastRecordMs;
        _accPresent += _lastPresentMs;
        // 面板自己的代价从窗口那边取（重排 + 贴图），不是这里的"统计"耗时——
        // 混在一起会让"面板很贵"这件事被统计代码盖住。
        _lastHudMs = _windows.Count > 0 ? _windows[0].LastHudMs : 0;
        _accHud += swHud.Elapsed.TotalMilliseconds;
        _accPanel += _lastHudMs;
        if (_windows.Count > 0)
        {
            double redraw = _windows[0].LastHudRedrawMs;
            if (redraw > 0) { _hudRedrawSum += redraw; _hudRedrawCount++; }
            _hudRedrawTotal = _windows[0].HudRedraws;
        }
        if (statsRan) _accStats += _lastStatsMs;
        _accFrames++;

        if (NowMs >= _nextLogAt)
        {
            _nextLogAt = NowMs + 1000;
            int n = Math.Max(1, _accFrames);
            Console.WriteLine(
                $"[{NowMs / 1000,6:F1}s] fps={_fps,5:F1}"
                + $" 记录={_accRecord / n,5:F2} 上屏={_accPresent / n,5:F2}"
                  + $" 面板={_hudRedrawMs,4:F2}ms×{_hudRedrawPerSec:F0}/s 统计={_lastStatsMs,5:F2}"
                  + $" 合计={(_accRecord + _accPresent + _accHud + _accStats) / n,6:F2}ms"
                  + $" 置顶={_lastTopmostMs,5:F2}ms"
                  + $" 脏区={_windows[0].LastPresentRectCount}块/{_windows[0].LastPresentAreaPercent,5:F1}%"
                  + $" ws={_workingSetMb,6:F1}MB");
            _accRecord = _accPresent = _accHud = _accStats = 0;
            _hudAvgMs = _accPanel / n;
            _accPanel = 0;
            double secs = Math.Max(0.001, (NowMs - _hudRateWall) / 1000.0);
            _hudRedrawPerSec = (_hudRedrawTotal - _hudRedrawAtWall) / secs;
            _hudRedrawAtWall = _hudRedrawTotal;
            _hudRateWall = NowMs;
            _hudRedrawMs = _hudRedrawCount > 0 ? _hudRedrawSum / _hudRedrawCount : 0;
            _hudRedrawSum = 0; _hudRedrawCount = 0;
            _accFrames = 0;
        }
    }

    /// <summary>
    /// 显存已用量（MB）。
    ///
    /// 为什么要显示它：**核显的显存是从系统内存里分的**，而它**不计入进程工作集**。
    /// 任务管理器那一列在核显机器上会把 GPU 共享显存算进去，于是出现
    /// "任务管理器 1000MB、面板只有 100MB"——不是面板不准，是只报了一个口径，
    /// 恰好漏掉了最大的一块。
    /// </summary>
    private static double GpuUsedMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return 0;
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return info.CurrentUsage / 1048576.0;
        }
        catch { return 0; }
    }
    private string BuildHudText()
    {
        // 面板的排版原则：**一行一类事**，数字对齐，单位统一。
        // 内存那一行是重点：三个口径并列，并且把"任务管理器里叫哪一列"写出来
        // ——用户拿它跟任务管理器对照时最需要的就是这句话。
        return
            $"帧率 {_fps,6:F1} fps（空闲不重绘）   帧耗时 {_lastFrameMs,5:F2} ms   输入→上屏 {_inputToPresentMs,5:F1} ms\n" +
            $"绘制 {_lastRecordMs,5:F2} ms      上屏 {_lastPresentMs,5:F2} ms      分块光栅 {_lastRebuildMs,5:F2} ms\n" +
            $"面板 {_hudRedrawMs,4:F2} ms/次 ×{_hudRedrawPerSec,3:F0}/秒（摊到每帧 {_hudAvgMs,4:F2} ms）   统计 {_lastStatsMs,5:F2} ms\n" +
            $"内存 {_privateWsMb,6:F1} MB（专用工作集 = 任务管理器的“内存”）\n" +
            $"已提交 {_privateMb,6:F1} MB（=“提交大小”）   工作集 {_workingSetMb,6:F1} MB（含共享）\n" +
            $"共享提交 {_sharedCommitMb,6:F1} MB   显存 {_gpuMb,6:F1} MB   CPU {_cpuPercent,4:F1} %\n" +
            $"笔画 {Doc.Strokes.Count}      点数 {Doc.TotalPoints}\n" +
            $"选中 {Doc.Selected.Count}      工具 {ToolName(Tool)}{(PassThrough ? "（穿透中）" : "")}      粗细 {CurrentToolWidthLogical,4:F1}      撤销栈 {Doc.UndoDepth}\n" +
            $"分块 {_tilesUsed}/{_tilesBudget}（可见 {_tilesVisible}，本帧光栅 {_tilesRasterized}）      网格 {Doc.GridCells}\n" +
            $"Ctrl+Alt：1笔 2荧光 3激光 4橡皮 5框选 6粗细 Z撤销 C清空\n" +
            $"其它 Ctrl+Alt：I面板 P穿透 K键盘 Y穿透方式 X退出";
    }

    private static string ToolName(Tool t) => t switch
    {
        Tool.Pen => "笔",
        Tool.Highlighter => "荧光笔",
        Tool.Laser => "激光笔",
        Tool.Eraser => "笔记橡皮",
        Tool.AreaEraser => "面积橡皮",
        Tool.Capture => "截图",
        Tool.Marquee => "框选",
        Tool.Line => "直线",
        Tool.Rectangle => "矩形",
        Tool.Ellipse => "椭圆",
        Tool.Circle => "圆",
        Tool.Triangle => "三角形",
        Tool.Parallelogram => "平行四边形",
        Tool.Arrow => "箭头",
        _ => "?",
    };

    // =====================================================================
    //  Window procedure
    // =====================================================================

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 诊断计数器：排查"输入到底有没有进到窗口"（鼠标消息还是指针消息）。
        // 这两个数在 --shapedrawtest 的调试行里打出来。
        _cntMsgTotal++;
        if (msg == Native.WM_MOUSEMOVE) _cntWmMouseMove++;
        else if (msg == 0x0201 /*WM_LBUTTONDOWN*/) _cntWmLButtonDown++;
        else if (msg == Native.WM_POINTERDOWN) _cntWmPointerDown++;
        else if (msg == Native.WM_POINTERUPDATE) _cntWmPointerUpdate++;

        // 宿主自己的窗口（开发期的点击目标）先处理。产品界面不会用到这一层。
        if (HandleHostWindowMessage(hWnd, msg, wParam, lParam, out var hostResult))
            return hostResult;

        // 滚轮：滚动画布（只改相机偏移，不动对象数据）。见 HandleWheel。
        if (msg == 0x020A /*WM_MOUSEWHEEL*/) return HandleWheel(wParam);
        // 批注键盘模式下的按键。只有这个模式收得到——见 SetKeyboardMode。
        if ((msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN) && HandleKeyDown(wParam))
            return IntPtr.Zero;

        // 恢复小圆钮：它独立于覆盖层，是穿透时唯一还能接收点击的东西。
        if (s_pillHwnds.Contains(hWnd))
        {
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                case Native.WM_POINTERDOWN:
                    // 点它就退出穿透，把工具条还回来。
                    SetPassThrough(false);
                    return IntPtr.Zero;

                case Native.WM_MOUSEMOVE:
                    Pill?.SetHovered(true);
                    return IntPtr.Zero;

                case Native.WM_MOUSELEAVE:
                    Pill?.SetHovered(false);
                    return IntPtr.Zero;

                case Native.WM_NCHITTEST:
                    return new IntPtr(Native.HTCLIENT);

                case Native.WM_MOUSEACTIVATE:
                    return new IntPtr(Native.MA_NOACTIVATE);

                default:
                    return Native.DefWindowProc(hWnd, msg, wParam, lParam);
            }
        }

        switch (msg)
        {
            case Native.WM_NCHITTEST:
                if (PassThrough)
                    return new IntPtr(Native.HTTRANSPARENT);
                return new IntPtr(Native.HTCLIENT);

            // 指针形状。**必须显式回答**：不处理时系统会拿窗口类的光标兜底，
            // 而类光标为 NULL 的表现就是"永远转圈"（这次修的就是它）。
            //
            // 只在"移动且输入未被捕获"时才会收到这条消息——所以拖拽中的光标
            // 得在按下那一刻自己设（见 OnPointerDown），不能等这里。
            case Native.WM_SETCURSOR:
                // 非客户区（边框、缩放角）由系统按自己的规矩来。
                if ((lParam.ToInt64() & 0xFFFF) != Native.HTCLIENT) break;
                if (PassThrough) break;                 // 穿透：让下层窗口决定
                ApplyCursor(force: true);
                return new IntPtr(1);                   // TRUE = 已处理，别再兜底

            case Native.WM_POINTERENTER:
                PointerInside = true;
                TrackPointerLeave(hWnd);
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                // 指针离开窗口：落点反馈（橡皮圆环、笔尖环）必须跟着消失，
                // 否则手一移开，屏幕上就留下一个圈。
                PointerInside = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_MOUSELEAVE:
                PointerInside = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
                OnPointerDown(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUPDATE:
                OnPointerMove(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUP:
                OnPointerUp(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                EndStroke();
                return IntPtr.Zero;

            case Native.WM_HOTKEY:
                HandleHotkey(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;

            case Native.WM_TIMER:
                // 只有要显示性能面板时才需要周期性重绘；面板关掉还每秒重画 4 次
                // 纯属白烧电。
                if (ShowHud) _dirty = true;
                UpdatePillHover();
                UpdateScrollBarHoverFromCursor();
                var swTop = Stopwatch.StartNew();
                ReassertTopmost();
                swTop.Stop();
                _lastTopmostMs = swTop.Elapsed.TotalMilliseconds;
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _quit = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 让宿主处理属于它自己创建的窗口的消息。返回 true 表示消息已被消费。
    /// </summary>
    protected virtual bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    private void ReassertTopmost()
    {
        foreach (var w in _windows)
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // =====================================================================
    //  Pointer input
    // =====================================================================

    private void OnPointerDown(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntDown++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (PassThrough) return;
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 相机：屏幕 → 画布

        _activePointer = id;
        _activePointerType = ptype;
        PointerX = x; PointerY = y; PointerInside = true;

        // 界面优先：点在悬浮条上就是操作界面，不是画一笔。
        if (UiPointerDown(x, y, pressure, ptype == Native.PT_PEN, inverted))
        {
            _drawing = false;
            // 界面也要捕获指针：拖出悬浮条、在按钮上滑开都需要继续收到消息。
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 滚动条次之：它只占右边缘一条窄带，但"拖滑块"比"在那儿画一笔"更特殊。
        if (TryBeginScrollBarDrag(screenX, screenY))
        {
            _drawing = true;
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        _drawing = true;
        Native.SetCapture(hWnd);
        // 拖拽中系统不再发 WM_SETCURSOR（输入已被捕获），光标必须在按下这一刻定下来。
        ApplyCursor();

        var tool = inverted ? Tool.Eraser : Tool;
        // 双击的判定要在**这一下**之前问（问完再记录这一下）。
        bool dbl = IsDoubleClick(x, y);
        NoteDown(x, y);
        switch (tool)
        {
            case Tool.Eraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginErase();
                // 笔尖原地不动也要能擦：先按"点"擦一次，之后才按路径擦。
                Doc.EraseStrokeAt(x, y, EraserRadius);
                break;

            case Tool.AreaEraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginErase();
                Doc.EraseAreaRect(AreaEraserRect(x, y));
                break;

            case Tool.Capture:
                // 和框选同一个手势。**和框选一样存画布坐标**（渲染那一层就
                // 不必为它单独换算一次），抓屏时再换算回屏幕坐标
                // （screen = canvas + 相机偏移，见 EndCapture）。
                CaptureActive = true;
                CapMinX = CapMaxX = x;
                CapMinY = CapMaxY = y;
                break;

            case Tool.Marquee:
                // 双击图形 = 进 / 出**顶点编辑**。
                //
                // 为什么必须有这么一个模式开关：矩形的四个顶点正好压在选中框的
                // 四个角上，两套手柄同时可点的话，"想改形状"会变成"把图形拉大"。
                // PowerPoint / Figma 都是这么分的（编辑顶点 / 矢量编辑模式）。
                if (dbl && VertexEdit) { VertexEdit = false; _dirty = true; break; }
                if (dbl && !VertexEdit && VertexHandles.CanEdit(Doc.Selected)
                    && Doc.Selected[0].HitTestExact(x, y, 6f * DpiScale))
                {
                    VertexEdit = true; _dirty = true; break;
                }

                // 先问选择手势（顶点 / 按钮 / 手柄 / 整体拖动）；都没接才起新的框选。
                if (!TryBeginSelectionGesture(x, y))
                {
                    VertexEdit = false;                 // 点空白 = 退出顶点编辑
                    MarqueeActive = true;
                    MqMinX = MqMaxX = x; MqMinY = MqMaxY = y;
                }
                break;

            case Tool.Laser:
                Laser.Clear();
                Laser.Visible = true;
                Laser.Add(x, y, NowMs);
                break;

            case Tool.Line:
            case Tool.Rectangle:
            case Tool.Ellipse:
            case Tool.Circle:
            case Tool.Triangle:
            case Tool.Parallelogram:
            case Tool.Arrow:
                // 图形：按下点是**定义**的起点，拖动中整条重算（不是往点列里追加）。
                // 追加的话"从左上拖到右下再拖回左上"会留下一串废点，
                // 而且三角形的第三个顶点根本没地方放。
                ActiveStroke = new Stroke
                {
                    Tool = tool,
                    Kind = KindOf(tool),
                    Color = CurrentColor,
                    Width = PenWidthLogical * DpiScale,
                    Preset = PenPresetValue,
                };
                ActiveStroke.AddPoint(x, y, 1f, NowMs);
                break;

            default:
                _lastSampleMs = NowMs;
                // 起笔打底交给优化器；没装优化器就什么都不做。
                InkOptimizers.Current?.BeginStroke(x, y);
                float trailW = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                                         : tool == Tool.Laser ? LaserWidthLogical
                                                         : PenWidthLogical) * DpiScale;
                // 只对真笔（PT_PEN）起轨迹：这条通道是给"笔尖跟手"用的，
                // 鼠标/触摸走它没有意义，而且会平白多一条系统画出来的线。
                if (ptype == Native.PT_PEN)
                    WindowAt(x, y)?.BeginInkTrail(
                        tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor, trailW * 0.5f);
                ActiveStroke = new Stroke
                {
                    Tool = tool,
                    Color = tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
                    Width = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                                      : tool == Tool.Laser ? LaserWidthLogical
                                                      : PenWidthLogical) * DpiScale,
                    Preset = PenPresetValue,
                };
                ActiveStroke.AddPoint(x, y, pressure, NowMs);
                break;
        }
        _dirty = true;
    }

    private void OnPointerMove(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntMove++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 相机：屏幕 → 画布，下游全按画布坐标走
        PointerX = x; PointerY = y; PointerInside = true;

        // 界面捕获了指针（例如按下按钮后滑出去），消息全归界面。
        if (UiCapturing)
        {
            UiPointerMove(x, y, pressure, false, inverted);
            _dirty = true;
            return;
        }

        // 穿透模式：我们不收输入，也不该动光标（那是下层窗口的事）。
        if (PassThrough) { _dirty = true; return; }

        // 拖滚动条：和"画一笔"互斥。
        if (ScrollBarDragging)
        {
            UpdateScrollBarDrag(screenY);
            ApplyCursor();
            _dirty = true;
            return;
        }

        if (!_drawing || id != _activePointer)
        {
            // 悬停路径。以前这里直接 return，"没落笔"时引擎完全不知道指针在哪儿，
            // 于是悬停光标、滚动条悬停、落点预览都无从谈起。
            UpdateScrollBarHover(screenX, screenY);
            ApplyCursor();
            if (DrawnCursor != ToolCursorShape.None) _dirty = true;
            return;
        }

        var tool = inverted ? Tool.Eraser : Tool;
        switch (tool)
        {
            case Tool.Eraser:
                EraseStrokeAlongPath(x, y);
                break;

            case Tool.AreaEraser:
                EraseAreaAlongPath(x, y);
                break;

            case Tool.Capture:
                if (CaptureActive)
                {
                    CapMinX = MathF.Min(CapMinX, x); CapMaxX = MathF.Max(CapMaxX, x);
                    CapMinY = MathF.Min(CapMinY, y); CapMaxY = MathF.Max(CapMaxY, y);
                }
                break;

            case Tool.Marquee:
                if (SelDragging) UpdateSelDrag(x, y);
                else
                {
                    MqMinX = MathF.Min(MqMinX, x); MqMaxX = MathF.Max(MqMaxX, x);
                    MqMinY = MathF.Min(MqMinY, y); MqMaxY = MathF.Max(MqMaxY, y);
                }
                break;

            case Tool.Laser:
                Laser.Add(x, y, NowMs);
                break;

            case Tool.Line:
            case Tool.Rectangle:
            case Tool.Ellipse:
            case Tool.Circle:
            case Tool.Triangle:
            case Tool.Parallelogram:
            case Tool.Arrow:
                if (ActiveStroke != null)
                {
                    bool uniform = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
                    ShapeGeometry.SetFromDrag(ActiveStroke, ActiveStroke.Points[0].X,
                                              ActiveStroke.Points[0].Y, x, y, uniform);
                }
                break;

            default:
                if (ActiveStroke != null)
                {
                    // 平滑和抽稀都是优化器的事。没装优化器时原样收下：
                    // 指针报什么坐标，就存什么坐标。
                    double dt = Math.Max(1e-3, (NowMs - _lastSampleMs) / 1000.0);
                    _lastSampleMs = NowMs;
                    float fx = x, fy = y;
                    var opt = InkOptimizers.Current;
                    opt?.Smooth(ref fx, ref fy, dt);

                    if (ptype == Native.PT_PEN)
                        WindowAt(x, y)?.AddInkTrailPoint(fx, fy, ActiveStroke.Width * 0.5f);

                    var last = ActiveStroke.Points[^1];
                    float dx = fx - last.X, dy = fy - last.Y;
                    float minStep = (opt?.SampleStepLogicalPx ?? 0f) * DpiScale;
                    if (dx * dx + dy * dy >= minStep * minStep)
                        ActiveStroke.AddPoint(fx, fy, pressure, NowMs);
                }
                break;
        }
        // 拖拽中（已捕获）：系统不会再发 WM_SETCURSOR，每帧自己维护。
        ApplyCursor();
        _dirty = true;
    }

    private void OnPointerUp(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntUp++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);

        // 界面优先收尾，否则会留下"按钮一直按着"的状态。
        if (UiCapturing && ReadPointer(id, out float ux, out float uy, out float upressure,
                                       out bool uinverted, out _))
        {
            UiPointerUp(ux, uy, upressure, false, uinverted);
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (ScrollBarDragging)
        {
            EndScrollBarDrag();
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (id != _activePointer) return;
        Native.ReleaseCapture();
        _drawing = false;
        EndStroke();
    }

    /// <summary>
    /// 沿着"指针刚走过的那一段"补几刀，而不是只在最新位置上擦一次。
    /// 少了这一步，快速划过去时两个采样点之间会漏掉一片——
    /// 用户说的"橡皮擦不灵敏"就是它。
    ///
    /// 笔尖橡皮与面积橡皮共用这一套补刀逻辑，区别只在"一刀擦什么形状"。
    /// 步长取**擦除范围里最小的一边的一半**：这样相邻两刀一定重叠，
    /// 不会在中间漏出一条缝。
    /// </summary>
    private void EraseStrokeAlongPath(float x, float y)
    {
        float r = EraserRadius;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, r * 0.5f)), 1, 48);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Doc.EraseStrokeAt(_lastEraseX + dx * t, _lastEraseY + dy * t, r);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    /// <summary>面积橡皮：沿路径补刀，每一刀擦一个黄金分割比的矩形。</summary>
    private void EraseAreaAlongPath(float x, float y)
    {
        float step = MathF.Max(4f, MathF.Min(
            AreaEraserWidthLogical, AreaEraserHeightLogical) * DpiScale * 0.5f);
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / step), 1, 48);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Doc.EraseAreaRect(AreaEraserRect(_lastEraseX + dx * t, _lastEraseY + dy * t));
        }
        // 起点那一刀在按下时已经擦过，这里只补中间和终点。
        _lastEraseX = x;
        _lastEraseY = y;
    }

    /// <summary>工具 → 图形种类。工具的枚举值不能直接当 Kind 用（两套枚举各管一头）。</summary>
    private static StrokeKind KindOf(Tool t) => t switch
    {
        Tool.Line => StrokeKind.Line,
        Tool.Rectangle => StrokeKind.Rectangle,
        Tool.Ellipse => StrokeKind.Ellipse,
        Tool.Circle => StrokeKind.Circle,
        Tool.Triangle => StrokeKind.Triangle,
        Tool.Parallelogram => StrokeKind.Parallelogram,
        Tool.Arrow => StrokeKind.Arrow,
        _ => StrokeKind.Freehand,
    };

    // =====================================================================
    //  双击与顶点拖动
    // =====================================================================

    private bool IsDoubleClick(float x, float y)
    {
        double dt = NowMs - _lastDownMs;
        float slop = DoubleClickSlopLogical * DpiScale;
        return dt >= 0 && dt <= DoubleClickMs
            && MathF.Abs(x - _lastDownX) <= slop && MathF.Abs(y - _lastDownY) <= slop;
    }

    private void NoteDown(float x, float y)
    {
        _lastDownMs = NowMs;
        _lastDownX = x;
        _lastDownY = y;
    }

    /// <summary>
    /// 顶点模式的分流：点到顶点就开拖。
    ///
    /// 拖动过程中**每帧直接改控制点**（不产生撤销记录），松手才提交一条
    /// <see cref="EditShapeAction"/>——和拖动选中框是同一套节奏：一次拖拽 = 一步撤销。
    /// </summary>
    private bool TryBeginVertexDrag(float x, float y)
    {
        if (!ShowsVertexHandles) return false;
        var s = Doc.Selected[0];
        int vi = VertexHandles.HitTest(s, x, y, DpiScale);
        if (vi < 0) return false;

        _vertexDragTarget = s;
        _vertexDragIndex = vi;
        _vertexStartControl = ShapeGeometry.ControlPoints(s);
        Doc.BeginLiveEdit(s);
        return true;
    }

    private void UpdateVertexDrag(float x, float y)
    {
        var s = _vertexDragTarget;
        if (s == null) return;
        Doc.Dirty.Add(s.PaddedBounds);                 // 旧位置要擦
        if (VertexHandles.DragTo(s, _vertexDragIndex, x, y))
            Doc.Dirty.Add(s.PaddedBounds);             // 新位置要画
        _dirty = true;
    }

    private void EndVertexDrag()
    {
        var s = _vertexDragTarget;
        _vertexDragTarget = null;
        _vertexDragIndex = -1;
        if (s == null) return;

        Doc.EndLiveEdit(s);

        // 控制点真的变了才记一步撤销。只是"点了一下顶点"（没拖动）不该进栈。
        bool changed = s.Points.Count != _vertexStartControl.Length;
        if (!changed)
            for (int i = 0; i < s.Points.Count; i++)
                if ((new Vector2(s.Points[i].X, s.Points[i].Y) - _vertexStartControl[i]).LengthSquared()
                    > 0.01f) { changed = true; break; }

        if (changed)
            Doc.CommitShapeEdit(s, _vertexStartControl, ShapeGeometry.ControlPoints(s));

        _vertexStartControl = null;
        _dirty = true;
    }

    private Vector2[] _vertexStartControl;

    // =====================================================================
    //  截图
    //
    //  流程（每一步都有它非这么做不可的理由）：
    //
    //   1. 拖一个框（和框选同一个手势，用户不用学新动作）；
    //   2. **把自己的覆盖层藏起来**再抓屏——不然抓到的就是"批注盖在 PPT 上"
    //      那一坨，等于把老师写的字又贴回去一张；
    //   3. 抓到的是一比一的**物理像素**，按 DPI 折成画布尺寸；
    //   4. 放到**视口左上角**并立刻选中——用户看得见、拖得动、能接着复制/翻转；
    //   5. 同时写进剪贴板：老师要把它贴到别的地方（PPT、微信、Word）是常见动作。
    //  =====================================================================

    /// <summary>
    /// 覆盖层藏起来时用的句柄列表。**多屏也要一起藏**，否则副屏上抓到的还是
    /// 我们自己画的东西（多屏教室很常见）。
    /// </summary>
    private IntPtr[] OverlayHandles()
    {
        var list = new List<IntPtr>(_windows.Count);
        foreach (var w in _windows) list.Add(w.Hwnd);
        return list.ToArray();
    }

    /// <summary>截图落地的位置：**视口左上角**往里缩一点（见 CaptureMargin 的注释）。</summary>
    internal const float CaptureMarginLogical = 24f;

    private void EndCapture()
    {
        CaptureActive = false;
        _dirty = true;

        // 画布坐标 → 屏幕坐标：相机偏移加回去（ScreenToCanvas 的逆运算）。
        // 相机只有纵向偏移（横向没有滚动），所以 x 不用换算。
        int x = (int)MathF.Round(MathF.Min(CapMinX, CapMaxX));
        int y = (int)MathF.Round(MathF.Min(CapMinY, CapMaxY) + ViewOffsetY);
        int w = (int)MathF.Round(MathF.Abs(CapMaxX - CapMinX));
        int h = (int)MathF.Round(MathF.Abs(CapMaxY - CapMinY));

        // 抓 1 像素宽的框没有意义（多半是"点了一下"），直接当没发生过。
        if (w < 4 || h < 4)
        {
            Console.WriteLine("截图取消：框太小");
            return;
        }

        byte[] pixels;
        using (ScreenCapture.HiddenOverlay(OverlayHandles()))
            pixels = ScreenCapture.Grab(x, y, w, h);

        // 窗口藏过又显示，后缓冲里的内容是旧的：整层作废重画，
        // 否则屏幕上会留下一块擦不掉的残影。
        Doc.InvalidateAll();

        if (pixels == null)
        {
            Console.WriteLine("截图失败：抓屏返回空");
            return;
        }

        ClipboardImage.SetImage(pixels, w, h);

        var img = ImageData.Adopt(w, h, pixels);
        if (img == null) return;

        // 画布坐标 = 视口左上角 + 一点边距。**边距是必须的**：贴着 (0,0) 的话
        // 选中框的旋转手柄会跑到屏幕外，用户抓不到它。
        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);

        // 落到视口左上角之后可能超出视口（图比屏幕大），把相机拉回去看全它。
        EnsureVisible(placed);
        if (placed != null)
        {
            Doc.SelectOnly(new[] { placed });
            Tool = Tool.Marquee;             // 刚截完图，最可能的下一件事是摆放它
            NotifyUiStateChanged();
        }
        Console.WriteLine($"截图 {w}×{h} 物理像素 → 画布 {w / DpiScale:F0}×{h / DpiScale:F0}，已放到左上角并选中");
    }

    /// <summary>把相机滚到"这个对象看得见"。只做纵向——横向没有滚动这回事。</summary>
    private void EnsureVisible(Stroke s)
    {
        if (s == null) return;
        var vp = ViewportCanvas;
        var b = s.PaddedBounds;
        if (b.MaxY > vp.MaxY) ViewOffsetY += b.MaxY - vp.MaxY;
        else if (b.MinY < vp.MinY) ViewOffsetY += b.MinY - vp.MinY;
        ClampViewOffset();
    }

    /// <summary>
    /// 从剪贴板粘一张图（Ctrl+V）。位置规则和截图一致：**视口左上角**，
    /// 粘完立刻选中。
    /// </summary>
    internal bool PasteImageFromClipboard()
    {
        if (!ClipboardImage.TryGetImage(out var pixels, out int w, out int h, out bool hasAlpha))
        {
            Console.WriteLine("粘贴：剪贴板里没有可用的图像（只认 CF_DIB / CF_DIBV5）");
            return false;
        }
        var img = ImageData.Adopt(w, h, pixels, hasAlpha);
        if (img == null) return false;

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);
        EnsureVisible(placed);
        Doc.SelectOnly(new[] { placed });
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
        _dirty = true;
        Console.WriteLine($"粘贴图片 {w}×{h}");
        return true;
    }

    private void EndStroke()
    {
        if (ScrollBarDragging) EndScrollBarDrag();
        foreach (var w in _windows) w.EndInkTrail();
        Doc.EndErase();
        if (CaptureActive)
        {
            EndCapture();
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        if (ActiveStroke != null)
        {
            // 图形：拖动距离太小就是"点了一下"，不要留下一个零尺寸的图形
            // （它会挂在文档里、进撤销栈、还得靠撤销才清得掉）。
            bool tooSmall = ActiveStroke.IsShape && ActiveStroke.Points.Count >= 2
                && Vector2.Distance(
                       new Vector2(ActiveStroke.Points[0].X, ActiveStroke.Points[0].Y),
                       new Vector2(ActiveStroke.Points[^1].X, ActiveStroke.Points[^1].Y))
                   < 3f * DpiScale;

            if (ActiveStroke.Points.Count > 0 && !tooSmall)
            {
                // 抬笔后的处理（平滑、抽稀、拟合、笔锋）全部交给优化器。
                // 没装优化器时，存下来的就是原始采样点，引擎直接画它。
                // **图形不走优化器**：它有确切定义，抽稀/平滑只会把矩形抹圆。
                if (!ActiveStroke.IsShape)
                    InkOptimizers.Current?.EndStroke(ActiveStroke, DpiScale);
                _lastSimplifyInfo = InkOptimizers.Current?.LastReport;
                Doc.AddStroke(ActiveStroke);
                _lastStrokeReport =
                    $"采集到 {ActiveStroke.Points.Count} 个点"
                    + $"，收到 按下{_cntDown} 移动{_cntMove} 抬起{_cntUp} 丢失捕获{_cntCaptureLost}"
                    + $"，设备={PointerTypeName(_activePointerType)}"
                    + (string.IsNullOrEmpty(_lastSimplifyInfo) ? "" : $"，抽稀 {_lastSimplifyInfo}");
                Console.WriteLine("[笔画] " + _lastStrokeReport);
                _lastSimplifyInfo = null;
            }
            else if (tooSmall)
            {
                ActiveStroke.Release();
            }
            ActiveStroke = null;
        }
        if (Tool == Tool.Marquee)
        {
            if (SelDragging) EndSelDrag();
            else ApplyMarquee();
        }
        _drawing = false;
        _dirty = true;
        _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
    }

    private static string PointerTypeName(uint t) => t switch
    {
        Native.PT_MOUSE => "鼠标",
        Native.PT_TOUCH => "触摸",
        Native.PT_PEN => "笔",
        _ => "未知",
    };

    // =====================================================================
    //  指针形状（光标）
    //
    //  一条规则：**每次指针移动都要能回答"这个位置、这个工具、这个设备，
    //  用户接下来能做什么"**。回答不了就退到箭头，绝不留空——留空就是
    //  系统兜底，也就是这次修的"永远转圈"。
    //
    //  优先级（上面的压下面的）：
    //    1. 穿透模式   → 不插手，交给下层窗口
    //    2. 触摸       → 不显示指针
    //    3. 拖拽中     → 用按下那一刻的语义（此时系统不再发 WM_SETCURSOR）
    //    4. 滚动条 / 选中框手柄 / 操作条
    //    5. 工具       → 画布上的形状
    // =====================================================================

    internal enum ToolCursorShape
    {
        None,
        /// <summary>圆环：橡皮、手写笔悬停。直接表达"落点范围内会怎样"。</summary>
        Ring,
        /// <summary>胶囊：荧光笔。扁笔用横条比圆圈更贴切，也暗示了半透明。</summary>
        Capsule,
        /// <summary>实心点：激光笔。激光表达的是"我说的是这里"，不是"多宽"。</summary>
        Dot,
        /// <summary>
        /// 矩形：面积橡皮。**它必须是矩形，不能画成圆环**——擦除范围是黄金分割比
        /// 的矩形，落点却画个圆环的话，"看到的范围"和"擦掉的范围"对不上，
        /// 用户会以为橡皮擦漏擦（实测这类错位最难被原谅）。
        /// </summary>
        Rect,
    }

    private IntPtr _cursorApplied;
    private int _cursorSizePx, _cursorSizeDpi;

    /// <summary>
    /// 系统指针的物理像素尺寸（跟随 DPI 与用户的"指针大小"辅助功能设置）。
    /// 本机 200% 缩放下是 64。自绘光标按它生成，才不会在高分屏上糊掉。
    /// </summary>
    internal int CursorSizePx
    {
        get
        {
            int dpi = Math.Max(96, (int)MathF.Round(96f * DpiScale));
            if (_cursorSizePx == 0 || dpi != _cursorSizeDpi)
            {
                int v = Native.GetSystemMetricsForDpi(Native.SM_CXCURSOR, (uint)Math.Min(dpi, 768));
                _cursorSizePx = v > 0 ? v : (int)(32 * DpiScale);
                _cursorSizeDpi = dpi;
            }
            return _cursorSizePx;
        }
    }

    /// <summary>当前该显示什么光标。纯函数（不碰系统），所以能拿来做自检。</summary>
    internal CursorKind ComputeCursorKind()
    {
        if (PassThrough) return CursorKind.Leave;                 // 谁来接管由系统决定
        if (LastPointerType == Native.PT_TOUCH) return CursorKind.Hidden;

        if (_drawing)
        {
            // 拖拽中不重新做命中测试：指针早就离开手柄了，重测会让光标在半路变回去。
            if (Tool == Tool.Marquee && SelDragging)
                return _dragIsMove ? CursorKind.Move : HandleCursor(_dragHandle);
            return ToolCursorKind;
        }

        // 滚动条：不换光标。手型按规范只能表示链接；四向箭头又会被读成"移动/拉伸"。
        // "能拖"这件事交给滑块自己的悬停反馈（变粗、变深）去说。
        if (ScrollBarHover) return CursorKind.Default;

        if (Tool == Tool.Marquee && Doc.Selected.Count > 0)
        {
            var k = SelectionCursor(PointerX, PointerY);
            if (k.HasValue) return k.Value;      // null = 这一带没有特殊语义，交给工具
        }

        return ToolCursorKind;
    }

    /// <summary>画布上的工具光标（不含滚动条、操作条、手柄）。</summary>
    private CursorKind ToolCursorKind => Tool switch
    {
        // 现在四种工具都自己画落点反馈，所以系统光标一律藏起来——
        // 自绘落点 + 系统光标叠在一起是"箭头套圆环"，很难看（见 DrawnCursor 的注释）。
        // 以前笔在鼠标下用的是系统十字（没有宽度信息），那正是这次要补的。
        Tool.Pen => CursorKind.Hidden,
        Tool.Highlighter => CursorKind.Hidden,      // 落点由自绘的宽度胶囊表达
        Tool.Laser => CursorKind.Hidden,            // 落点由自绘的实心点表达
        Tool.Eraser => CursorKind.Hidden,           // 落点由自绘圆环表达
        Tool.AreaEraser => CursorKind.Hidden,       // 落点由自绘矩形表达
        // 截图用手势（拖框），十字准星是"从这儿拖到那儿"的通用语言，和框选一致。
        Tool.Marquee or Tool.Capture or Tool.Line or Tool.Rectangle or Tool.Ellipse
            or Tool.Arrow or Tool.Circle or Tool.Triangle or Tool.Parallelogram
            => CursorKind.Cross,
        _ => CursorKind.Default,
    };

    /// <summary>
    /// 选中框上的光标（含操作条、八个手柄、旋转手柄、框内拖动）。
    /// 返回 null = 指针不在选中框这一带，调用方继续往下问工具。
    ///
    /// 用可空值而不是"返回 Default 表示没有"：Default（箭头）本身也是一个
    /// **明确答案**（比如操作条按钮上就该是箭头），两者混在一起写，
    /// 就会出现"按钮上反而露出手柄光标"这种错。
    /// </summary>
    private CursorKind? SelectionCursor(float canvasX, float canvasY)
    {
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;
        var aabb = frame.CanvasAabb;

        // 操作条按钮：按钮的形状本身就是 affordance，光标保持箭头。
        if (SelectionHandles.BarButtonAt(canvasX, canvasY, aabb, dpi) >= 0)
            return CursorKind.Default;

        var h = SelectionHandles.HitTest(canvasX, canvasY, frame, dpi);
        if (h != SelHandle.None) return HandleCursor(h);

        var lp = frame.ToLocalPoint(new Vector2(canvasX, canvasY));
        bool inside = lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
                   && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY;
        return inside ? CursorKind.Move : null;
    }

    private static CursorKind HandleCursor(SelHandle h) => h switch
    {
        SelHandle.TopLeft or SelHandle.BottomRight => CursorKind.ResizeNWSE,
        SelHandle.TopRight or SelHandle.BottomLeft => CursorKind.ResizeNESW,
        SelHandle.Left or SelHandle.Right => CursorKind.ResizeWE,
        SelHandle.Top or SelHandle.Bottom => CursorKind.ResizeNS,
        SelHandle.Rotate => CursorKind.Rotate,
        _ => CursorKind.Default,
    };

    /// <summary>把当前该有的光标设上。重复调用是安全的（同一个句柄不重复设）。</summary>
    internal void ApplyCursor(bool force = false)
    {
        var kind = ComputeCursorKind();
        if (kind == CursorKind.Leave) return;          // 不插手
        var h = Cursors.HandleFor(kind, CursorSizePx);
        if (h == IntPtr.Zero) return;
        if (!force && h == _cursorApplied) return;
        Native.SetCursor(h);
        _cursorApplied = h;
    }

    /// <summary>
    /// 自己要画落点反馈（橡皮圆环、笔尖环、荧光笔胶囊）时，必须把系统光标藏起来，
    /// 否则就是"箭头 + 圆环"叠在一起。触摸不画：手指没有悬停，画了会留在屏幕上。
    /// </summary>
    internal ToolCursorShape DrawnCursor
    {
        get
        {
            if (PassThrough || !PointerInside || LastPointerType == Native.PT_TOUCH)
                return ToolCursorShape.None;

            // 规则一句话：**鼠标没有笔尖，所以悬停和书写都要画**；
            // **手写笔的笔尖本身就是落点**，一落笔就不该再跟一个圈
            // （会把手写的位置挡住，而且笔尖和圈的中心差一两像素时看着像错位）。
            bool penTip = LastPointerType == Native.PT_PEN;

            switch (Tool)
            {
                case Tool.Eraser:
                    // 橡皮反过来：它表达的是"这一块会被擦掉"，擦除中更要看得到。
                    return ToolCursorShape.Ring;
                case Tool.AreaEraser:
                    // 面积橡皮：矩形落点（形状必须和擦除范围一致，见 Rect 的注释）。
                    return ToolCursorShape.Rect;
                case Tool.Pen:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Ring;
                case Tool.Highlighter:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Capsule;
                case Tool.Laser:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Dot;
                default:
                    return ToolCursorShape.None;
            }
        }
    }

    /// <summary>自绘落点反馈的半径（像素）：脏区按它扩，不然快速移动会留残影。</summary>
    internal float DrawnCursorRadius
    {
        get
        {
            switch (DrawnCursor)
            {
                case ToolCursorShape.Ring:
                    return CursorOuterRadius * 1.35f + 10f;
                case ToolCursorShape.Capsule:
                    // 胶囊是横着的：脏区要按它的**长边**算（宽 = 高的 1.7 倍或是 26 逻辑像素）
                    return MathF.Max(HighlighterWidthLogical * DpiScale * 1.7f,
                                     26f * DpiScale) * 0.5f + 12f;
                case ToolCursorShape.Dot:
                    return CursorDotRadius * 1.5f + 10f;
                case ToolCursorShape.Rect:
                    // 矩形是扁的：脏区要按**对角线**算（半个对角线 = 外接圆半径），
                    // 只按宽度算的话，矩形上下两个角会擦不干净。
                    return 0.5f * MathF.Sqrt(
                        AreaEraserWidthLogical * AreaEraserWidthLogical +
                        AreaEraserHeightLogical * AreaEraserHeightLogical) * DpiScale + 10f;
                default:
                    return 0f;
            }
        }
    }

    /// <summary>
    /// 笔/激光落点反馈的**真实**半径（像素）：就是"这一笔有多粗"的一半。
    /// 细笔（1.5 逻辑像素）不能被"看得见的下限"撑掉——那会让人误判笔宽，
    /// 所以下限由外层那个固定尺寸的圈负责（见 Overlay.DrawRingCursor）。
    /// </summary>
    internal float CursorRingTrueRadius => CurrentToolWidthLogical * 0.5f * DpiScale;

    /// <summary>
    /// 激光轨迹的粗细（像素）。**默认值和以前一模一样**：以前轨迹写死
    /// "芯 5 像素 + 外发光 26 像素"，和任何宽度设置都无关；现在按激光自己的
    /// 粗细缩放（0.625 这个系数就是让默认 4 逻辑像素 × 2 倍屏 = 5 像素，
    /// 外观不变），这样切粗细对激光才是有意义的。
    /// </summary>
    internal float LaserCorePx => MathF.Max(2f, LaserWidthLogical * DpiScale * 0.625f);
    internal float LaserGlowPx => LaserCorePx * 5.2f;

    /// <summary>
    /// 激光落点的半径（像素）。取"轨迹头部的粗细"（细激光有个可见下限），
    /// 这样点和它拖出来的尾巴是同一个尺寸，看起来是一支笔而不是"点上加条线"。
    /// </summary>
    internal float CursorDotRadius
        => MathF.Max(LaserCorePx, Cursors.DotMinRadiusLogical * DpiScale);

    /// <summary>
    /// 落点反馈**外圈**的半径（像素）。细笔的真实半径只有 1.5 像素，
    /// 单画那个圈等于没画，所以外面再套一个固定尺寸的圈：
    ///   内圈 = 真实笔宽（1:1，"我写出来就这么粗"）
    ///   外圈 = 最小可见尺寸（"落点在这儿"）
    /// 橡皮本来就只表达范围，只有一个圈。
    /// </summary>
    internal float CursorOuterRadius => Tool == Tool.Eraser
        ? EraserRadius
        : MathF.Max(CursorRingTrueRadius, Cursors.RingMinRadiusLogical * DpiScale);

    /// <summary>
    /// 订一次"指针离开窗口"的通知。覆盖层以前完全没订过，所以 PointerInside
    /// 永远停在 true——手移开之后落点环会留在屏幕上。
    /// </summary>
    private void TrackPointerLeave(IntPtr hWnd)
    {
        var tme = new Native.TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
            dwFlags = Native.TME_LEAVE,
            hwndTrack = hWnd,
        };
        Native.TrackMouseEvent(ref tme);
    }

    // =====================================================================
    //  滚动条（拖动 + 悬停）
    //
    //  以前只有滚轮能滚：滚动条是**画出来的、拖不动的东西**。光标这一题的
    //  前提是"滑块真的能拖"，所以在这里补上。形状仍然是箭头（理由见调研文档
    //  5.3：手型只能表示链接，四向箭头会被读成缩放/移动）。
    // =====================================================================

    /// <summary>命中宽度（逻辑像素）。滑块只有 4 像素宽，按 4 像素判定等于点不中。</summary>
    private const float ScrollBarGrabLogical = 16f;

    /// <summary>上下夹住相机偏移（滚轮和拖滚动条共用）。</summary>
    internal void ClampViewOffset()
    {
        var extent = CanvasExtent;
        float lowest = _virtualH - extent.MaxY;
        if (ViewOffsetY > 0f) ViewOffsetY = 0f;
        if (ViewOffsetY < lowest) ViewOffsetY = lowest;
    }

    private bool TryBeginScrollBarDrag(float screenX, float screenY)
    {
        var w = WindowAt(screenX, screenY);
        if (w == null || !w.TryScrollBar(this, out var sb)) return false;
        if (!sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f)) return false;

        // 点在轨道上（滑块以外）：先把滑块中心挪到指针处，再当成"滑块内拖动"。
        // macOS / Figma 都是这个手感：点哪儿滚到哪儿，比"翻一页"更符合直觉。
        if (screenY < sb.ThumbTop || screenY > sb.ThumbTop + sb.ThumbLen)
        {
            float want = Math.Clamp(screenY - sb.ThumbLen * 0.5f, sb.Top, sb.Top + sb.MaxTravel);
            ApplyScrollBarThumbTop(w, sb, want);
            w.TryScrollBar(this, out sb);
        }

        ScrollBarDragging = true;
        ScrollBarHover = true;
        ScrollBarActiveAtMs = NowMs;
        _scrollWindow = w;
        _scrollGrabDy = screenY - sb.ThumbTop;
        return true;
    }

    private void UpdateScrollBarDrag(float screenY)
    {
        if (_scrollWindow == null) return;
        if (!_scrollWindow.TryScrollBar(this, out var sb)) return;
        float want = Math.Clamp(screenY - _scrollGrabDy, sb.Top, sb.Top + sb.MaxTravel);
        ApplyScrollBarThumbTop(_scrollWindow, sb, want);
    }

    private void EndScrollBarDrag()
    {
        ScrollBarDragging = false;
        _scrollWindow = null;
    }

    /// <summary>滑块顶边 → 相机偏移。绘制时那段换算的逆运算（见 Overlay.DrawScrollBar）。</summary>
    private void ApplyScrollBarThumbTop(OverlayWindow w, in OverlayWindow.ScrollBarLayout sb, float thumbTop)
    {
        if (sb.MaxTravel <= 0.001f) return;
        float frac = Math.Clamp((thumbTop - sb.Top) / sb.MaxTravel, 0f, 1f);
        float viewTop = sb.ExtentMinY + frac * (sb.ExtentH - w.Height);
        ViewOffsetY = w.OriginY - viewTop;
        ClampViewOffset();
        ScrollBarActiveAtMs = NowMs;
        _dirty = true;
    }

    /// <summary>指针是否停在滚动条上。停在上面就不该淡出，所以每次移动和每次心跳都要问。</summary>
    internal void UpdateScrollBarHover(float screenX, float screenY)
    {
        bool hover = false;
        if (!PassThrough && !ScrollBarDragging)
        {
            var w = WindowAt(screenX, screenY);
            if (w != null && w.TryScrollBar(this, out var sb))
                hover = sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f);
        }

        if (hover != ScrollBarHover)
        {
            ScrollBarHover = hover;
            _dirty = true;                      // 滑块要从 4px 变到 9px
        }
        if (hover || ScrollBarDragging) ScrollBarActiveAtMs = NowMs;
    }

    /// <summary>
    /// 心跳（250ms 一次）里按鼠标位置刷新滚动条悬停。没有这一步，指针停在滑块上
    /// 不动时没人通知，滑块会自己淡出——用户会觉得"这玩意儿点不中"。
    /// </summary>
    private void UpdateScrollBarHoverFromCursor()
    {
        if (PassThrough || ScrollBarDragging) return;
        if (!Native.GetCursorPos(out var p)) return;
        UpdateScrollBarHover(p.X, p.Y);
    }

    /// <summary>找出包含某个屏幕坐标的覆盖窗口（多屏时用）。</summary>
    private OverlayWindow WindowAt(float screenX, float screenY)
    {
        foreach (var w in _windows)
        {
            if (screenX >= w.OriginX && screenX < w.OriginX + w.Width &&
                screenY >= w.OriginY && screenY < w.OriginY + w.Height)
                return w;
        }
        return _windows.Count > 0 ? _windows[0] : null;
    }

    private void ApplyMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;

        float l = MqMinX, t = MqMinY, r = MqMaxX, b = MqMaxY;
        if (r - l < 4 || b - t < 4)
        {
            // 这是一次**点击**，不是拖框。单击空白处就该取消选中。
            // 之前这里直接 return、什么都不做，用户得点两下才取消，很别扭。
            Doc.Selected.Clear();
            _dirty = true;
            return;
        }

            Doc.ApplyMarquee(new RectF { MinX = l, MinY = t, MaxX = r, MaxY = b });
        Console.WriteLine($"marquee selected {Doc.Selected.Count} strokes");
        _dirty = true;
    }

    /// <summary>
    /// 读一条指针消息，顺便把两个时标记下来（延时探针用）：
    ///   pi.PerformanceCount —— 系统给的硬件/驱动时标（QPC 计数）
    ///   现在                —— 我们真正拿到它的时刻
    /// 两者之差 = 输入栈 + 我们自己的消息队列。**这一段里只有队列是我们的责任**，
    /// 而队列拖延的直接原因就是 Present(1) 把线程卡在垂直同步里。
    /// </summary>
    private bool ReadPointer(uint id, out float x, out float y, out float pressure,
                             out bool inverted, out uint pointerType)
    {
        x = y = 0; pressure = 0.5f; inverted = false; pointerType = 0;
        if (!Native.GetPointerInfo(id, out var pi)) return false;
        _lastInputPerfQpc = pi.PerformanceCount;
        _lastInputMsgQpc = Qpc.Now;
        if (LatencyRecording && _prevInputMsgQpc != 0)
        {
            double gap = Qpc.Ms(_prevInputMsgQpc, _lastInputMsgQpc);
            if (gap > 0 && gap < 500) Latency.AddInputGap(gap);
        }
        _prevInputMsgQpc = _lastInputMsgQpc;
        _framePoints++;
        x = pi.ptPixelLocationX;
        y = pi.ptPixelLocationY;
        pointerType = pi.pointerType;
        if (pi.pointerType == Native.PT_PEN && Native.GetPointerPenInfo(id, out var pen))
        {
            pressure = pen.pressure / 1024f;
            if (pressure <= 0.01f) pressure = 0.5f;
            inverted = (pen.penFlags & (Native.PEN_FLAG_INVERTED | Native.PEN_FLAG_ERASER)) != 0;
        }
        return true;
    }

    // =====================================================================
    //  Hotkeys
    // =====================================================================

    private void HandleHotkey(int id)
    {
        // id 是"注册顺序"，动作由键位表决定（见 RegisterHotkeys）。
        var action = ActionForHotkeyId(id);
        if (action == KeyAction.None) return;
        RunAction(action, id);
        // 换工具/换模式之后指针形状立刻要跟着变。
        ApplyCursor();
        _dirty = true;
    }

    /// <summary>快捷键里属于宿主（开发工具）的那几个。产品界面用不到。</summary>
    protected virtual void HandleHostHotkey(int id) { }

    /// <summary>
    /// 执行一个动作。**全局热键和批注内快捷键走同一个入口**——否则同一个动作
    /// 会有两份实现，早晚会不一致（"按 Ctrl+Alt+Z 撤销得好好的，按 Ctrl+Z 却少清了激光"）。
    /// </summary>
    private void RunAction(KeyAction action, int hotkeyId = 0)
    {
        switch (action)
        {
            case KeyAction.TogglePassThrough: SetPassThrough(!PassThrough); break;
            case KeyAction.ToolPen: Tool = Tool.Pen; break;
            case KeyAction.ToolHighlighter: Tool = Tool.Highlighter; break;
            case KeyAction.ToolLaser: Tool = Tool.Laser; break;
            case KeyAction.ToolEraser: Tool = Tool.Eraser; break;
            case KeyAction.ToolAreaEraser: Tool = Tool.AreaEraser; break;
            case KeyAction.ToolCapture: Tool = Tool.Capture; break;
            case KeyAction.ToolMarquee: Tool = Tool.Marquee; break;
            case KeyAction.ToolLine: Tool = Tool.Line; break;
            case KeyAction.ToolRectangle: Tool = Tool.Rectangle; break;
            case KeyAction.ToolEllipse: Tool = Tool.Ellipse; break;
            case KeyAction.ToolCircle: Tool = Tool.Circle; break;
            case KeyAction.ToolTriangle: Tool = Tool.Triangle; break;
            case KeyAction.ToolParallelogram: Tool = Tool.Parallelogram; break;
            case KeyAction.Undo: Doc.Undo(); Laser.Clear(); break;
            case KeyAction.Redo: Doc.Redo(); break;
            case KeyAction.Clear: Doc.Clear(); Laser.Clear(); break;
            case KeyAction.ToggleHud: ShowHud = !ShowHud; break;
            case KeyAction.CycleWidth: CycleWidth(); break;
            case KeyAction.ToggleKeyboardMode: SetKeyboardMode(!KeyboardMode); break;
            case KeyAction.CyclePassThroughMode: CyclePassThroughMode(); break;
            case KeyAction.Quit: _quit = true; break;
            // 开发期的基准与内存探测不属于引擎，交给宿主覆写
            case KeyAction.HostBenchmark:
            case KeyAction.HostMemoryProbe:
                HandleHostHotkey(hotkeyId != 0 ? hotkeyId : (int)action);
                break;

            case KeyAction.SelectAll: SelectAll(); break;
            case KeyAction.Duplicate: Doc.DuplicateSelected(); break;
            case KeyAction.DeleteSelected: Doc.DeleteSelected(); break;
            // Esc：先退顶点编辑，再退选择。两件事分开是因为"我改完形状了"
            // 和"我不要这个选区了"是两种意图，一次按键全干掉太粗暴。
            case KeyAction.CancelSelection:
                if (VertexEdit) { VertexEdit = false; }
                else Doc.Selected.Clear();
                break;
            case KeyAction.ToggleVertexEdit: ToggleVertexEdit(); break;
            case KeyAction.PasteImage: PasteImageFromClipboard(); break;
            case KeyAction.NudgeLeft: Nudge(-1f, 0f); break;
            case KeyAction.NudgeUp: Nudge(0f, -1f); break;
            case KeyAction.NudgeRight: Nudge(1f, 0f); break;
            case KeyAction.NudgeDown: Nudge(0f, 1f); break;
            case KeyAction.NudgeLeftFar: Nudge(-10f, 0f); break;
            case KeyAction.NudgeUpFar: Nudge(0f, -10f); break;
            case KeyAction.NudgeRightFar: Nudge(10f, 0f); break;
            case KeyAction.NudgeDownFar: Nudge(0f, 10f); break;
        }
    }

    /// <summary>
    /// 方向键微调。**已知待改**：按住方向键会重复触发，每次都是一条撤销记录；
    /// 要接"连续微调合并成一步"，得等编辑命令支持合并。
    /// </summary>
    private void Nudge(float dx, float dy)
    {
        Doc.ApplyTransform(Matrix3x2.CreateTranslation(dx, dy));
        _dirty = true;
    }

    /// <summary>
    /// 切换**当前工具**的粗细。以前这里写死改笔宽，于是"选了荧光笔按切粗细没反应"
    /// ——改的不是它。四种工具各有一张档位表（见 WidthPresets 的注释）。
    /// </summary>
    internal void CycleWidth()
    {
        switch (Tool)
        {
            case Tool.Highlighter:
                HighlighterWidthIndex = (HighlighterWidthIndex + 1) % HighlighterWidthPresets.Length;
                HighlighterWidthLogical = HighlighterWidthPresets[HighlighterWidthIndex];
                break;
            case Tool.Laser:
                LaserWidthIndex = (LaserWidthIndex + 1) % LaserWidthPresets.Length;
                LaserWidthLogical = LaserWidthPresets[LaserWidthIndex];
                break;
            // 两种橡皮各有一套档位：笔记橡皮调的是笔尖大小，面积橡皮调的是
            // 那个黄金分割比矩形的**高度**（宽度跟着算，见 AreaEraserHeightLogical）。
            case Tool.Eraser:
                EraserRadiusIndex = (EraserRadiusIndex + 1) % EraserRadiusPresets.Length;
                EraserRadiusLogical = EraserRadiusPresets[EraserRadiusIndex];
                break;
            case Tool.AreaEraser:
                AreaEraserHeightIndex = (AreaEraserHeightIndex + 1) % AreaEraserHeightPresets.Length;
                AreaEraserHeightLogical = AreaEraserHeightPresets[AreaEraserHeightIndex];
                break;
            default:
                WidthPresetIndex = (WidthPresetIndex + 1) % WidthPresets.Length;
                PenWidthLogical = WidthPresets[WidthPresetIndex];
                break;
        }
        Console.WriteLine($"{Tool} 粗细 -> {CurrentToolWidthLogical} 逻辑像素"
                        + $"（本机实际 {CurrentToolWidthLogical * DpiScale:F0} 物理像素）"
                        + (Tool == Tool.AreaEraser
                           ? $"，矩形 {AreaEraserWidthLogical:F0}×{AreaEraserHeightLogical:F0}"
                           : ""));
        CaptureToolSizes();
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 把当前尺寸抄进"要记住"的那份里并标脏。**只抄用户真会调的**：
    /// 笔/荧光笔/激光笔的粗细 + 两种橡皮的大小。
    /// </summary>
    private void CaptureToolSizes()
    {
        Sizes.PenWidth = PenWidthLogical;
        Sizes.HighlighterWidth = HighlighterWidthLogical;
        Sizes.LaserWidth = LaserWidthLogical;
        Sizes.EraserRadius = EraserRadiusLogical;
        Sizes.AreaEraserHeight = AreaEraserHeightLogical;
        Sizes.Dirty = true;
    }

    /// <summary>
    /// 启动时把上次记住的尺寸装回去。**只装有效的**（&gt;0）：
    /// 缺项或坏值一律用引擎默认，配置文件不该把程序带到奇怪的状态里。
    /// </summary>
    private void ApplySavedToolSizes()
    {
        if (Sizes.PenWidth > 0f) PenWidthLogical = Sizes.PenWidth;
        if (Sizes.HighlighterWidth > 0f) HighlighterWidthLogical = Sizes.HighlighterWidth;
        if (Sizes.LaserWidth > 0f) LaserWidthLogical = Sizes.LaserWidth;
        if (Sizes.EraserRadius > 0f) EraserRadiusLogical = Sizes.EraserRadius;
        if (Sizes.AreaEraserHeight > 0f) AreaEraserHeightLogical = Sizes.AreaEraserHeight;

        // 档位下标跟着走，否则下次按"换粗细"会跳回默认档附近
        int idx = Array.IndexOf(WidthPresets, PenWidthLogical);
        if (idx >= 0) WidthPresetIndex = idx;
        idx = Array.IndexOf(HighlighterWidthPresets, HighlighterWidthLogical);
        if (idx >= 0) HighlighterWidthIndex = idx;
        idx = Array.IndexOf(LaserWidthPresets, LaserWidthLogical);
        if (idx >= 0) LaserWidthIndex = idx;
        idx = Array.IndexOf(EraserRadiusPresets, EraserRadiusLogical);
        if (idx >= 0) EraserRadiusIndex = idx;
        idx = Array.IndexOf(AreaEraserHeightPresets, AreaEraserHeightLogical);
        if (idx >= 0) AreaEraserHeightIndex = idx;
    }

    /// <summary>
    /// 把工具尺寸与档位复位到**出厂默认**（含"换粗细"用的下标）。
    ///
    /// 自检用：判据里凡是涉及"换一档会变成多少"的，都必须先复位——
    /// 否则用户配置文件里挑过的粗细会变成自检的输入，出现"同一个测试
    /// 在这台机器上过、那台机器上不过"这种最讨厌的假失败。
    /// </summary>
    internal void ResetToolSizesToDefaults()
    {
        PenWidthLogical = WidthPresets[1];              // 3
        WidthPresetIndex = 1;
        HighlighterWidthLogical = HighlighterWidthPresets[1];   // 18
        HighlighterWidthIndex = 1;
        LaserWidthLogical = LaserWidthPresets[0];       // 4
        LaserWidthIndex = 0;
        EraserRadiusLogical = EraserRadiusPresets[1];   // 10
        EraserRadiusIndex = 1;
        AreaEraserHeightLogical = AreaEraserHeightPresets[2];   // 150
        AreaEraserHeightIndex = 2;
        Sizes = new InkSettings.ToolSettings();
    }

    /// <summary>
    /// 进 / 出顶点编辑（Enter 键，或者双击图形）。
    /// 进入的条件是"正好选中一个图形"——多选时顶点手柄不知道该听谁的。
    /// </summary>
    internal void ToggleVertexEdit()
    {
        if (VertexEdit) { VertexEdit = false; _dirty = true; return; }
        if (!VertexHandles.CanEdit(Doc.Selected))
        {
            Console.WriteLine("顶点编辑需要先选中**一个**图形（直线/箭头/矩形/椭圆/圆/三角形/平行四边形）");
            return;
        }
        VertexEdit = true;
        Tool = Tool.Marquee;              // 顶点手柄要在选择工具下才有意义
        NotifyUiStateChanged();
        _dirty = true;
        Console.WriteLine($"进入顶点编辑：{Doc.Selected[0].Kind}，{VertexHandles.Count(Doc.Selected[0])} 个顶点可拖");
    }

    private void SetPassThrough(bool on)
    {
        PassThrough = on;
        if (!on) Laser.Visible = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        UpdatePassThroughPill();
        // 穿透时把指针交还给下层窗口（ApplyCursor 会在穿透模式下自动放手）；
        // 退出穿透要立刻把属于我们的光标设回来，不必等下一次鼠标移动。
        if (!on) ApplyCursor(force: true);
        Console.WriteLine($"pass-through = {on} (mode {PassMode})");
    }

    /// <summary>
    /// 穿透时把"找回界面"的小圆钮摆出来。位置取界面自己报的占位矩形右端——
    /// 也就是工具条原来的位置，老师一眼就能看到它在那儿。
    /// </summary>
    private void UpdatePassThroughPill()
    {
        if (Pill == null) return;

        if (!PassThrough)
        {
            Pill.Hide();
            _pillShown = RectF.Empty;
            return;
        }

        var bounds = Ui?.QueryBounds() ?? RectF.Empty;
        // IsEmpty 只认"求过并集且为空"的矩形；默认全 0 的矩形要单独排除，
        // 否则会拿它去开一个坐标错乱的窗口。
        if (bounds.IsEmpty || (bounds.MinX == 0 && bounds.MinY == 0
                               && bounds.MaxX == 0 && bounds.MaxY == 0))
        {
            // 界面没有报位置（比如纯引擎模式）：退回到屏幕右下角。
            float size = 44f;
            bounds = new RectF
            {
                MinX = LogicalVirtualScreen.MaxX - size - 16f,
                MinY = LogicalVirtualScreen.MaxY - size - 90f,
                MaxX = LogicalVirtualScreen.MaxX - 16f,
                MaxY = LogicalVirtualScreen.MaxY - 90f,
            };
        }

        // 界面可能把提示条也算进了占位矩形，这里只要右下角那一小块。
        float s = 44f;
        var pill = new RectF
        {
            MinX = bounds.MaxX - s,
            MinY = bounds.MaxY - s,
            MaxX = bounds.MaxX,
            MaxY = bounds.MaxY,
        };

        // 只有位置或可见性真的变了才动窗口。
        // 之前每帧都调一次 Show（里面是 SetWindowPos + SWP_SHOWWINDOW），
        // 每帧重新"显示"一次窗口会不断打断鼠标消息，点击因此迟迟送不到
        // ——实测就是这个原因导致点小圆钮没反应。
        if (Pill.Visible && SameRect(_pillShown, pill)) return;
        _pillShown = pill;
        Pill.Show(pill);
    }

    private static bool SameRect(RectF a, RectF b)
        => MathF.Abs(a.MinX - b.MinX) < 0.5f && MathF.Abs(a.MinY - b.MinY) < 0.5f
        && MathF.Abs(a.MaxX - b.MaxX) < 0.5f && MathF.Abs(a.MaxY - b.MaxY) < 0.5f;

    /// <summary>上一次摆出的圆钮矩形，用来避免每帧重复显示窗口。</summary>
    private RectF _pillShown = RectF.Empty;

    /// <summary>
    /// 光标靠近小圆钮时把它提亮。走的是已有的 250ms 定时器，
    /// 不用再为它单独装一个鼠标钩子——那个钮本来就很小，250ms 足够灵敏。
    /// </summary>
    private void UpdatePillHover()
    {
        if (Pill == null || !Pill.Visible) return;
        if (!Native.GetCursorPos(out var p)) return;

        bool inside = p.X >= Pill.OriginX && p.X < Pill.OriginX + Pill.Width
                   && p.Y >= Pill.OriginY && p.Y < Pill.OriginY + Pill.Height;
        if (inside != _pillHover)
        {
            _pillHover = inside;
            Pill.SetHovered(inside);
        }
    }

    private bool _pillHover;

    private void CyclePassThroughMode()
    {
        PassMode = (PassThroughMode)(((int)PassMode + 1) % 3);
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        Console.WriteLine($"pass-through implementation = {PassMode}");
    }

    internal void ApplyPassThroughStyle(OverlayWindow w)
    {
        long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
        const long clear = Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        ex &= ~clear;

        if (PassThrough)
        {
            if (PassMode == PassThroughMode.ExTransparent)
                ex |= Native.WS_EX_TRANSPARENT;
            else if (PassMode == PassThroughMode.LayeredTransparent)
                ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        }

        Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
    }

    /// <summary>
    /// 关窗、注销快捷键、释放设备。子类若要覆写，务必先调用 base。
    /// </summary>
    protected virtual void Shutdown()
    {
        foreach (var w in _windows)
        {
            Native.KillTimer(w.Hwnd, (IntPtr)1);
            for (int i = 1; i <= _hotkeyActions.Count + 2; i++) Native.UnregisterHotKey(w.Hwnd, i);
            w.Dispose();
        }
        // 只写"改过的"键位；没改过就不碰用户的配置文件。
        if (Keys.Dirty || Sizes.Dirty) InkSettings.Save(Keys, Sizes);
        _windows.Clear();
        s_map.Clear();
        s_pillHwnds.Clear();
        Pill?.Dispose();
        Pill = null;
        Cursors.DisposeAll();
        Gfx.Shutdown();
        Console.WriteLine("shutdown complete");
    }

    // =====================================================================
    //  界面接入（引擎边界）
    //
    //  引擎负责：笔画/文档/输入/渲染/工具状态。
    //  界面负责：画自己、解释点击。
    //  两边唯一的通道就是 IOverlayUi / IUiHost，引擎里没有任何具体界面代码。
    // =====================================================================

    /// <summary>
    /// 换界面。传 null 或 <see cref="NullUi"/> 就是无界面（纯笔迹）。
    /// 界面只在覆盖层建好之后接入；运行中换界面也是安全的。
    /// </summary>
    /// <param name="keepScreen">
    /// 传 true 表示沿用当前这个界面已经算好的屏幕尺寸。自检里"临时换掉再换回来"
    /// 会用到处，避免界面按引擎内部的窗口尺寸重新布局（那把界面摆到别处去了）。
    /// 正式产品代码用默认值即可。
    /// </param>
    public void SetUi(IOverlayUi ui, bool keepScreen = false)
    {
        Ui = ui ?? new NullUi();
        // 界面看到的屏幕坐标是逻辑像素：它按自己的逻辑尺寸布局，引擎负责按 DPI 放大。
        Host = new UiHost(this, LogicalVirtualScreen, DpiScale, keepScreen);
        Ui.Attach(Host);
        InvalidateUi();
        NotifyUiStateChanged();
    }

    /// <summary>当前接入的界面（诊断用）。</summary>
    public IOverlayUi CurrentUi => Ui;

    /// <summary>虚拟桌面范围，界面据此决定悬浮条放在哪。</summary>
    public RectF VirtualScreen => new()
    {
        MinX = _virtualX,
        MinY = _virtualY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY + _virtualH,
    };

    /// <summary>虚拟桌面的**逻辑**范围（除以 DPI 缩放），界面布局用这个。</summary>
    public RectF LogicalVirtualScreen => new()
    {
        MinX = _virtualX / DpiScale,
        MinY = _virtualY / DpiScale,
        MaxX = (_virtualX + _virtualW) / DpiScale,
        MaxY = (_virtualY + _virtualH) / DpiScale,
    };

    /// <summary>
    /// 界面声明"外观变了，请重画我的缓存"。可以由界面的任意线程调用，
    /// 主线程在下一帧消费。**不要每帧调**，那等于每帧重画整个界面。
    /// </summary>
    public void InvalidateUi()
    {
        UiInvalidatePending = true;
        _dirty = true;
    }

    internal UiState SnapshotState() => new(
        Tool,
        Tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
        Tool == Tool.Highlighter
            ? new Color4(HighlighterCurrent.R, HighlighterCurrent.G, HighlighterCurrent.B, 1f)
            : CurrentColor,
        CurrentToolWidthLogical,
        PenPresetValue,
        PassThrough, BoardOn, Doc.UndoDepth, Doc.RedoDepth, Doc.Strokes.Count);

    private void NotifyUiStateChanged()
    {
        if (Ui == null) return;
        Ui.OnStateChanged(SnapshotState());
    }

    internal void SetToolFromUi(Tool tool)
    {
        Tool = tool;
        ApplyCursor();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetColorFromUi(Color4 color)
    {
        // 荧光笔用同一组基色，只是降低不透明度——老师看到的色相是一致的。
        if (Tool == Tool.Highlighter)
            HighlighterCurrent = InkPalette.ToHighlighter(color);
        else
            CurrentColor = color;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetWidthFromUi(float logicalPx)
    {
        float v = Math.Clamp(logicalPx, 0.5f, 64f);
        if (Tool == Tool.Highlighter)
        {
            HighlighterWidthLogical = v;
            int hi = Array.IndexOf(HighlighterWidthPresets, v);
            if (hi >= 0) HighlighterWidthIndex = hi;
        }
        else if (Tool == Tool.Laser)
        {
            LaserWidthLogical = v;
            int li = Array.IndexOf(LaserWidthPresets, v);
            if (li >= 0) LaserWidthIndex = li;
        }
        else
        {
            PenWidthLogical = v;
            int idx = Array.IndexOf(WidthPresets, v);
            if (idx >= 0) WidthPresetIndex = idx;
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetPresetFromUi(PenPreset preset)
    {
        PenPresetValue = preset;
        // 已经画好的笔画保持原样：它们是"当时的笔"写出来的，历史不该被改写。
        // 新的一笔会用新的预设。
        NotifyUiStateChanged();
    }

    internal void UndoFromUi()
    {
        Doc.Undo();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void RedoFromUi()
    {
        Doc.Redo();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void ClearFromUi()
    {
        Doc.Clear();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void QuitFromUi() => _quit = true;

    internal void SetPassThroughFromUi(bool on)
    {
        SetPassThrough(on);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 开关白板。底色一变，整个内容层都要重画——缓存里那张图是按旧底色画的。
    /// </summary>
    internal void SetBoardFromUi(bool on)
    {
        if (BoardOn == on) return;
        BoardOn = on;
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 指针按下的第一站：先问界面。返回 true 表示这次输入归界面（比如按到了
    /// 悬浮条上的按钮），引擎不再把它变成笔画。
    /// </summary>
    private bool UiPointerDown(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (Ui == null || !Ui.Visible) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        if (!Ui.PointerDown(e)) return false;
        UiCapturing = true;
        return true;
    }

    private bool UiPointerMove(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (Ui == null || !Ui.Visible) return false;

        // 只有在界面已经捕获输入或指针落在界面矩形内时才转发，避免没必要的调用。
        if (!UiCapturing && !UiContains(x, y)) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        return Ui.PointerMove(e);
    }

    private bool UiPointerUp(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (Ui == null || !Ui.Visible || !UiCapturing) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        bool consumed = Ui.PointerUp(e);
        UiCapturing = false;
        return consumed;
    }

    private bool UiContains(float x, float y)
    {
        x /= DpiScale; y /= DpiScale;   // 物理 → 逻辑
        foreach (var w in _windows)
            if (w.UiBoundsLogicalContains(x, y)) return true;
        return false;
    }

    /// <summary>控制台用法说明。产品界面不会走这里。</summary>
    protected virtual void PrintUsageHelp()
    {
        Console.WriteLine();
        Console.WriteLine("InkTeach 批注原型已启动。键位（可配置，见 " + InkSettings.FilePath + "）：");
        Console.Write(Keys.ToText());
        if (Keys.GlobalFailures.Count > 0)
        {
            Console.WriteLine("  ！下面这些键没注册上（被别的程序占了？换一个键）：");
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    " + f);
        }
        Console.WriteLine();
    }

    // =====================================================================
    //  选择手势（框选工具 = 选择工具）
    // =====================================================================

    /// <summary>
    /// 框选工具按下时的分流。返回 true 表示这次按下已经被选择手势接掉。
    ///
    /// 顺序有讲究：**先按钮、再手柄、最后才判断"整体拖动"**。反过来写的话，
    /// 按钮和手柄都紧挨着选中框，会被"在框内拖动"抢走，表现就是点不中。
    /// </summary>
    private bool TryBeginSelectionGesture(float x, float y)
    {
        if (Doc.Selected.Count == 0) return false;

        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;

        // 操作条定位用框的**画布轴对齐范围**：框本身可能是斜的，但"它占了屏幕上
        // 哪一块"永远是个正矩形，操作条贴在那个矩形的下面才对。
        var aabb = frame.CanvasAabb;

        // 顶点优先：顶点手柄和四个角手柄在屏幕上基本重合（矩形的顶点就是它的角），
        // 先问顶点才可能点得中（见 VertexHandles 的注释）。
        if (TryBeginVertexDrag(x, y))
        {
            SelDragging = true;
            _dirty = true;
            return true;
        }

        int btn = SelectionHandles.BarButtonAt(x, y, aabb, dpi);
        if (btn >= 0) { RunBarAction(btn, frame, aabb); return true; }

        var h = SelectionHandles.HitTest(x, y, frame, dpi);
        // 顶点模式：收起八个缩放手柄（旋转手柄留着——"改完形状再转一下"是
        // 很自然的下一步），于是"想改形状却把图形拉大了"这类误操作不存在。
        if (ShowsVertexHandles && h != SelHandle.Rotate) h = SelHandle.None;
        bool move = false;
        if (h == SelHandle.None)
        {
            // 没点在手柄上：把指针变回框坐标，看是不是落在框里（整体拖动）。
            var lp = frame.ToLocalPoint(new Vector2(x, y));
            move = lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
                && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY;
            if (!move) return false;                      // 落在空白处：交给框选
        }

        _dragHandle = h;
        _dragIsMove = move;
        _dragFrame = frame;
        _dragStartPoint = new Vector2(x, y);
        _selDragMatrix = Matrix3x2.Identity;
        // 读数用**绝对朝向**（[0,360)，见 SelectionHandles.RotationOf）：
        // 所以要把"按下那一刻对象已经转了多少度"记下来，拖动中加上增量。
        _selRotBaseDeg = SelectionHandles.RotationOf(frame.ToCanvas);

        _dragTargets = Doc.Selected.ToArray();
        _dragStartXform = new Matrix3x2[_dragTargets.Length];
        for (int i = 0; i < _dragTargets.Length; i++)
            _dragStartXform[i] = _dragTargets[i].Transform;

        SelDragging = true;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 拖动中：每帧都从**按下那一刻的变换**重算，而不是在上一帧结果上继续乘。
    /// 后者会累积浮点误差，拖得越久偏得越多，撤销也回不到原样。
    /// </summary>
    private void UpdateSelDrag(float x, float y)
    {
        // 顶点拖动走自己的那条路：改的是形状定义，不是变换矩阵。
        if (_vertexDragIndex >= 0) { UpdateVertexDrag(x, y); return; }

        var cur = new Vector2(x, y);
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        // Alt = 临时关掉吸附。Shift 是"硬网格 15°"，Alt 是"完全自由"，两个修饰键
        // 各管一头，中间那档（默认的 90° 软吸附）不用按键。
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;

        Matrix3x2 m;
        if (_dragIsMove)
        {
            // 整体移动：指针在画布上走多少，对象就走多少。**不能**在框坐标里算
            // 再共轭回来——框是斜的时候那样会走偏方向。
            m = Matrix3x2.CreateTranslation(cur - _dragStartPoint);
        }
        else
        {
            if (_dragHandle == SelHandle.Rotate)
            {
                // **必须在框坐标里量**，和真正施加的那个旋转矩阵用同一个轴心。
                //
                // 以前这里把"框坐标的中心"和"画布坐标的两个指针点"混在一起算角度，
                // 对象一被移动/转过，量出来的角度就不等于实际转过的角度——
                // 表现正是"读数对不上"（用户报的第 1 条）。DragMatrix 内部走的是
                // ToLocalPoint，所以这里也统一用局部点。
                var c = new Vector2((_dragFrame.Local.MinX + _dragFrame.Local.MaxX) * 0.5f,
                                    (_dragFrame.Local.MinY + _dragFrame.Local.MaxY) * 0.5f);
                float delta = SelectionHandles.RotationDeltaDegrees(
                    c, _dragFrame.ToLocalPoint(_dragStartPoint), _dragFrame.ToLocalPoint(cur),
                    shift, alt, out bool snapped);
                // 起始绝对角 + 这一拖的增量，再统一归一到 0~360：
                // 用户看到的是"这个对象现在多少度"，没有负号（和 Office 一致）。
                SelRotationDegrees = SelectionHandles.NormalizeDegrees(_selRotBaseDeg + delta);
                SelRotationSnapped = snapped;
                SelRotating = true;
            }

            // 手柄换算在**框坐标**里做（缩放的锚点是"对角那个手柄"，只有在框坐标里
            // 才是"沿着框的两条边"），再共轭回画布坐标：M = F⁻¹ · M_local · F
            var localM = SelectionHandles.DragMatrix(_dragHandle, _dragFrame,
                                                     _dragStartPoint, cur, DpiScale, shift, shift, alt);
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            var s = _dragTargets[i];
            Doc.Dirty.Add(s.PaddedBounds);                 // 旧位置要擦
            Doc.SetTransformLive(s, _dragStartXform[i] * m);
            Doc.Dirty.Add(s.PaddedBounds);                 // 新位置要画
        }
        _selDragMatrix = m;
        _dirty = true;
    }

    /// <summary>
    /// 松手：先把模型恢复到按下那一刻，再提交**一条**变换命令。
    ///
    /// 这样拖动过程中的实时预览不产生撤销记录，而撤销一步就精确回到拖动前
    /// ——不会出现"拖的时候动了三十次，要按三十次撤销"那种事。
    /// </summary>
    private void EndSelDrag()
    {
        SelDragging = false;
        SelRotating = false;            // 度数标签只在拖动中出现
        if (_vertexDragIndex >= 0) { EndVertexDrag(); _dirty = true; return; }
        if (_dragTargets == null) return;

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            Doc.SetTransformLive(_dragTargets[i], _dragStartXform[i]);
            Doc.Dirty.Add(_dragTargets[i].PaddedBounds);
        }

        Doc.Selected.Clear();
        foreach (var t in _dragTargets) Doc.Selected.Add(t);
        Doc.ApplyTransform(_selDragMatrix);

        _dragTargets = null;
        _dragStartXform = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _selDragMatrix = Matrix3x2.Identity;
        _dirty = true;
    }

    /// <summary>
    /// 操作条按钮。下标与 SelectionHandles.BarButtonAt 的返回值和渲染时的
    /// 图标数组一一对应：0 复制 / 1 删除 / 2 左右翻转 / 3 上下翻转 / 4 旋转。
    /// </summary>
    private void RunBarAction(int index, in SelectionFrame frame, in RectF aabb)
    {
        switch (index)
        {
            case 0: Doc.DuplicateSelected(); break;
            case 1: Doc.DeleteSelected(); break;

            // 翻转绕**框自己的轴**：斜着的对象应该在自己那套坐标里翻，
            // 而不是绕屏幕的竖直线翻——后者看起来像被转到别处去了。
            case 2: Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                        SelectionHandles.MirrorMatrix(frame.Local, horizontal: true))); break;
            case 3: Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                        SelectionHandles.MirrorMatrix(frame.Local, horizontal: false))); break;

        }
        Laser.Clear();
        _dirty = true;
    }

    /// <summary>
    /// 把"框坐标下的变换"翻译成"画布坐标下的变换"：<c>F⁻¹ · M · F</c>。
    ///
    /// 框是斜的时候，同一句"沿框的横轴放大两倍"在画布坐标里是个斜的缩放。
    /// 共轭就是在两套坐标之间翻译这件事——不用为"斜着的情况"另写一套公式。
    /// </summary>
    private static Matrix3x2 Conjugate(in Matrix3x2 frame, in Matrix3x2 localM)
    {
        if (frame.IsIdentity) return localM;
        if (!Matrix3x2.Invert(frame, out var inv)) return localM;
        return inv * localM * frame;
    }
    // =====================================================================
    //  批注键盘模式
    // =====================================================================

    /// <summary>
    /// 开关批注键盘模式。
    ///
    /// 开：去掉 WS_EX_NOACTIVATE 并把窗口提到前台，键盘归批注层，编辑类
    ///     快捷键（Ctrl+Z / Ctrl+D / Delete / 方向键…）才有地方落地。
    /// 关：加回 WS_EX_NOACTIVATE，覆盖层回到"永不抢焦点"，键盘还给下层程序
    ///     ——那时候只有全局热键（Ctrl+Alt+…）可用。
    ///
    /// 取舍说清楚：开着的时候，放映中的 PPT 收不到键盘。
    /// </summary>
    internal void SetKeyboardMode(bool on)
    {
        KeyboardMode = on;
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            if (on) ex &= ~Native.WS_EX_NOACTIVATE;
            else ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            if (on) Native.SetForegroundWindow(w.Hwnd);
        }
        Console.WriteLine($"批注键盘模式 = {on}"
            + (on ? "（键盘归批注层；此时下层程序收不到键盘）" : "（键盘还给下层程序）"));
        _dirty = true;
    }

    /// <summary>
    /// 批注键盘模式下的按键。**只在 <see cref="KeyboardMode"/> 打开时收到。**
    ///
    /// 键位一律照 Windows 的通用习惯，不自己发明：
    ///   Ctrl+Z 撤销 / Ctrl+Y 重做 / Ctrl+A 全选 / Delete 删除 / Esc 取消选择
    ///   Ctrl+D 复制一份（系统剪贴板还没接，先用 D）
    ///   方向键移动 1 像素，按住 Shift 是 10 像素
    ///
    /// 已知待改：方向键按住会重复触发，每次都是一条撤销记录。要接"连续微调
    /// 合并成一步"，得等编辑命令支持合并（撤销栈里相邻同类动作合并）。
    /// </summary>
    private bool HandleKeyDown(IntPtr wParam)
    {
        // 键位表驱动：按"当前修饰键状态 + 主键"拼成一个和弦，去批注内作用域里查。
        // 查不到就**不吞这个键**（返回 false），交给系统/下层程序——吞掉所有按键
        // 会让批注键盘模式下连输入法都用不了。
        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;

        var chord = new KeyChord(mods, (uint)wParam.ToInt32());
        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null) return false;

        RunAction(hit.Action);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 全选。顺便切到选择工具——不切的话手柄和操作条不会出现，
    /// 用户会以为"全选没生效"。
    /// </summary>
    internal void SelectAll()
    {
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 滚轮滚动画布。**只改相机偏移 ViewOffsetY，一个对象的数据都不动**——
    /// 这是滚动能做到 O(1) 的前提。绝不能像 InkClass 那样把平移烘焙进点坐标
    /// （那是 O(对象数)/次，一万笔滚一格要重写十万个点）。
    ///
    /// 只做纵向。往下滚 = 内容上移 = 偏移变负。
    ///
    /// 代价：**这里一个像素都不重画**。内容层是画布空间的分块缓存
    /// （CanvasTiles.cs），滚动只是换个位置把已经画好的块贴上去；只有在
    /// 新露出**没画过**的块时才会光栅化那一小块。所以滚一格的代价与文档
    /// 里有多少笔无关——从"整层重画"（一万笔 82~154ms，滚一格卡一下）
    /// 变成"贴图 + 偶尔补一两块"。这也正是 Win32 ScrollWindowEx 和浏览器
    /// 合成器滚动图层用的手法。
    /// </summary>
    internal IntPtr HandleWheel(IntPtr wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        // 往下滚时 delta = -120，要先变负（内容上移）——所以这里是 +=。
        // 写成 -= 会把符号翻过来：滚轮往下 = 视图往上，正好抵掉，
        // 再被"不许滚过顶部"夹回 0，表现就是**完全滚不动**。
        ViewOffsetY += delta / 120f * 72f * DpiScale;   // 一格 = 72 逻辑像素
        // 上下都夹住画布范围（内容边界 ∪ 一屏）。
        // 不夹的话会滚进无尽的空白，而且比例滚动条拿不到有意义的范围。
        // 下边界是"视口底边贴住内容底边"——接着写，内容长出去，范围自己长出来，
        // 所以不会把人卡在底边。
        ClampViewOffset();

        ScrollBarActiveAtMs = NowMs;                     // 滚动时让滚动条露面
        // 注意：**不调 Doc.InvalidateAll()**。那会把整个文档标脏、让所有分块
        // 重画——正是这次要拔掉的病根。分块缓存自己会发现相机变了，只做重合成。
        _dirty = true;
        return IntPtr.Zero;
    }}
