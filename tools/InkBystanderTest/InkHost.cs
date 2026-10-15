// InkHost.cs —— 系统湿墨托管（CoreInkPresenterHost）+ 三层旁听通道 + 统计
// 通道1：InkPresenter.UnprocessedInput（WinRT 名为 UnprocessedInput，类型 InkUnprocessedInput）
// 通道2：CoreWetStrokeUpdateSource（WetStrokeStarting/Continuing/Stopping/Completed/Canceled）
// 通道3：raw input（在 Program.cs 的 WM_INPUT 里计数）
// 补充：InkStrokeInput（系统已受理为墨的输入的逐点事件，作为旁听参照）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Windows.System;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;

namespace InkBystanderTest
{
    // 一个旁听点：坐标 + 压感 + 时间戳（实测与 QPC 同源、µs 口径；T5 有自动单位核验）
    internal struct PtRec
    {
        public double X, Y;
        public float Pressure;
        public ulong Qpc;
    }

    // 旁听统计（每条轨迹一个实例）
    internal class BystanderStats
    {
        // 通道1 UnprocessedInput 事件计数
        public int UI_Pressed, UI_Moved, UI_Released, UI_Entered, UI_Exited, UI_Hovered, UI_Lost;
        // 通道2 CoreWetStrokeUpdateSource 事件计数
        public int WS_Starting, WS_Continuing, WS_Stopping, WS_Completed, WS_Canceled;
        // 补充通道 InkStrokeInput 事件计数
        public int SI_Started, SI_Continued, SI_Ended, SI_Canceled;
        // 通道3 raw input 计数
        public int Raw_Mouse, Raw_Keyboard, Raw_Hid, Raw_HidPackets;
        public string Raw_HidName = "";
        // 宿主窗口自己收到的消息计数
        public int Wm_PointerDown, Wm_PointerUpdate, Wm_PointerUp, Wm_PointerEnter;
        public int Wm_MouseMove, Wm_LButtonDown, Wm_LButtonUp, Wm_RButtonDown, Wm_RButtonUp;
        // 旁听点（每点：x, y, pressure, QPC）
        public readonly List<PtRec> UI_Points = new List<PtRec>();
        public readonly List<PtRec> WS_Points = new List<PtRec>();
        public readonly List<PtRec> SI_Points = new List<PtRec>();
        // StrokesCollected
        public int StrokesCollectedEvents, StrokesCollectedCount;

        public void Add(List<PtRec> list, double x, double y, float pressure, ulong qpc)
        {
            if (list.Count < 20000) list.Add(new PtRec { X = x, Y = y, Pressure = pressure, Qpc = qpc });
        }

        public static void Bbox(List<PtRec> pts, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            if (pts.Count == 0) { minX = minY = maxX = maxY = double.NaN; }
        }

        public string PointsSummary(List<PtRec> pts, long qpcFreq)
        {
            if (pts.Count == 0) return "点数 0";
            Bbox(pts, out double x0, out double y0, out double x1, out double y1);
            var first = pts[0];
            var last = pts[pts.Count - 1];
            double pmin = double.MaxValue, pmax = double.MinValue;
            foreach (var p in pts) { if (p.Pressure < pmin) pmin = p.Pressure; if (p.Pressure > pmax) pmax = p.Pressure; }
            return string.Format(
                "点数 {0}，坐标范围 ({1:F1},{2:F1})-({3:F1},{4:F1})，压力 {5:F2}~{6:F2}，QPC {7}→{8}（Δ{9:F1}ms）；首点({10:F1},{11:F1},p={12:F2}) 末点({13:F1},{14:F1},p={15:F2})",
                pts.Count, x0, y0, x1, y1, pmin, pmax, first.Qpc, last.Qpc,
                (last.Qpc - first.Qpc) / 1000.0, // Timestamp 实测 µs 口径（T5 单位核验），µs→ms
                first.X, first.Y, first.Pressure, last.X, last.Y, last.Pressure);
        }
    }

    // 系统墨迹宿主：Compositor + DesktopWindowTarget + CoreInkPresenterHost
    internal class InkHost
    {
        public Compositor Compositor;
        public DesktopWindowTarget Target;
        public ContainerVisual RootVisual, InkLayer;
        public SpriteVisual BgVisual;
        public CoreInkPresenterHost Host;
        public InkPresenter Presenter;
        public CoreWetStrokeUpdateSource WetSource;
        public InkSynchronizer Synchronizer; // 自定义干燥时非空
        public DispatcherQueueController Dispatcher; // 保活
        public float DpiScale = 1f; // px→DIP 换算（自绘干墨坐标用）
        public ShapeVisual SelfDryVisual; // 干墨自绘层（合成树内）
        // R1：dispatcher 改静态——调用位置矩阵要重建多次宿主，全进程一个就够
        // （否则每次 Create 都新开 1~4 个 dispatcher 线程，矩阵跑完几十个）。
        static readonly List<DispatcherQueueController> s_controllers = new();
        static bool s_dispatcherReady;
        static DispatcherQueueController s_dispatcher;
        public BystanderStats Stats;
        public readonly List<string> SetupLog = new List<string>();

        /// <summary>
        /// 建宿主。<paramref name="dryOrder"/> = ActivateCustomDrying 的调用位置（R1 矩阵，2026-10-13）：
        ///   0=不调用；1=RootVisual 之后（旧行为，本机 Win11 26200 实测 E_INVALIDSTATE）；
        ///   2=拿到 presenter 立刻（RootVisual 之前，宿主还没进合成树）；
        ///   3=InputDeviceTypes=None 之后、RootVisual 之前；
        ///   4=绘制属性设好 + InputDeviceTypes=None 之后；5=输入全开之后（基线，预期失败）。
        /// 官方只说"受理输入之前"，没定义 CoreInkPresenterHost 的"前"具体在哪一步——逐个试，HResult 打出来。
        /// </summary>
        public static InkHost Create(IntPtr hwnd, float clientW, float clientH, float dpiScale,
            int dryOrder, BystanderStats stats, out Exception fatal)
        {
            var h = new InkHost { Stats = stats, DpiScale = dpiScale };
            fatal = null;
            try
            {
                // 0) 线程消息泵/DispatcherQueue：WinRT 合成与墨迹对象需要所在线程有 dispatcher
                h.EnsureDispatcher();

                // 1) Compositor + 桌面窗口目标（vtable 调 ICompositorDesktopInterop）
                h.Compositor = new Compositor();
                h.SetupLog.Add("new Compositor() ok");
                h.Target = ComposeInterop.CreateDesktopWindowTarget(h.Compositor, hwnd, true);
                h.SetupLog.Add("CreateDesktopWindowTarget（挂到 HWND）ok, IsTopmost=" + h.Target.IsTopmost);

                // 2) 视觉树：白色背景 + 墨迹层（尺寸用 DIP）
                Vector2 size = new Vector2(clientW / dpiScale, clientH / dpiScale);
                h.RootVisual = h.Compositor.CreateContainerVisual();
                h.Target.Root = h.RootVisual;
                h.BgVisual = h.Compositor.CreateSpriteVisual();
                h.BgVisual.Brush = h.Compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
                h.BgVisual.Size = size;
                h.RootVisual.Children.InsertAtTop(h.BgVisual);
                h.InkLayer = h.Compositor.CreateContainerVisual();
                h.InkLayer.Size = size;
                h.RootVisual.Children.InsertAtTop(h.InkLayer);
                h.SetupLog.Add($"视觉树 ok（DIP {size.X:F0}x{size.Y:F0}，dpiScale={dpiScale:F2}）");

                // 3) CoreInkPresenterHost（1709+；与 IInkDesktopHost 同族，走 WinRT 面）
                h.Host = new CoreInkPresenterHost();
                h.Presenter = h.Host.InkPresenter;
                h.SetupLog.Add($"CoreInkPresenterHost + InkPresenter ok（InputDeviceTypes 默认={h.Presenter.InputDeviceTypes}）");

                // 3.5) 自定义干燥：调用位置由 dryOrder 决定（见 Create 的注释）。
                // 2/3 在 RootVisual 之前——宿主还没进合成树，合成器没机会提交过帧。
                if (dryOrder == 2) h.TryActivateCustomDrying();
                if (dryOrder == 3)
                {
                    h.Presenter.InputDeviceTypes = Windows.UI.Core.CoreInputDeviceTypes.None;
                    h.TryActivateCustomDrying();
                }
                h.Host.RootVisual = h.InkLayer;
                if (dryOrder == 1) h.TryActivateCustomDrying();

                // 4) 绘制属性：纯红、粗 4（便于红像素取证）
                var attrs = h.Presenter.CopyDefaultDrawingAttributes();
                attrs.Color = Color.FromArgb(255, 255, 0, 0);
                attrs.Size = new Windows.Foundation.Size(4, 4);
                attrs.FitToCurve = false;
                h.Presenter.UpdateDefaultDrawingAttributes(attrs);
                if (dryOrder == 4)
                {
                    h.Presenter.InputDeviceTypes = Windows.UI.Core.CoreInputDeviceTypes.None;
                    h.TryActivateCustomDrying();
                }

                // 5) 输入受理：笔|鼠标|触摸，系统默认模式（系统画湿墨）
                h.Presenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse | CoreInputDeviceTypes.Touch;
                if (dryOrder == 5) h.TryActivateCustomDrying();

                // 6) 订阅
                h.Presenter.StrokesCollected += (s, e) =>
                {
                    h.Stats.StrokesCollectedEvents++;
                    h.Stats.StrokesCollectedCount += e.Strokes.Count;
                };

                // 通道1：UnprocessedInput
                var ui = h.Presenter.UnprocessedInput;
                ui.PointerPressed += (s, e) => { h.Stats.UI_Pressed++; h.AddUi(e); };
                ui.PointerMoved += (s, e) => { h.Stats.UI_Moved++; h.AddUi(e); };
                ui.PointerReleased += (s, e) => { h.Stats.UI_Released++; h.AddUi(e); };
                ui.PointerEntered += (s, e) => { h.Stats.UI_Entered++; h.AddUi(e); };
                ui.PointerExited += (s, e) => { h.Stats.UI_Exited++; h.AddUi(e); };
                ui.PointerHovered += (s, e) => { h.Stats.UI_Hovered++; h.AddUi(e); };
                ui.PointerLost += (s, e) => { h.Stats.UI_Lost++; h.AddUi(e); };

                // 通道2：CoreWetStrokeUpdateSource（逐点旁听）
                h.WetSource = CoreWetStrokeUpdateSource.Create(h.Presenter);
                h.WetSource.WetStrokeStarting += (s, e) => { h.Stats.WS_Starting++; h.AddWs(e); };
                h.WetSource.WetStrokeContinuing += (s, e) => { h.Stats.WS_Continuing++; h.AddWs(e); };
                h.WetSource.WetStrokeStopping += (s, e) => { h.Stats.WS_Stopping++; h.AddWs(e); };
                h.WetSource.WetStrokeCompleted += (s, e) => { h.Stats.WS_Completed++; h.AddWs(e); };
                h.WetSource.WetStrokeCanceled += (s, e) => { h.Stats.WS_Canceled++; h.AddWs(e); };

                // 补充通道：InkStrokeInput（系统受理为墨的输入逐点事件）
                var si = h.Presenter.StrokeInput;
                si.StrokeStarted += (s, e) => { h.Stats.SI_Started++; h.AddSi(e); };
                si.StrokeContinued += (s, e) => { h.Stats.SI_Continued++; h.AddSi(e); };
                si.StrokeEnded += (s, e) => { h.Stats.SI_Ended++; h.AddSi(e); };
                si.StrokeCanceled += (s, e) => { h.Stats.SI_Canceled++; h.AddSi(e); };

                h.SetupLog.Add("三层旁听 + 补充通道订阅 ok");
            }
            catch (Exception ex)
            {
                fatal = ex;
                h.SetupLog.Add("异常：" + ex.GetType().Name + "：" + ex.Message);
            }
            return h;
        }

    void EnsureDispatcher()
    {
        // R1：静态化——矩阵要重建多次宿主，dispatcher 全进程一个就够（见 s_controllers 注释）。
        if (s_dispatcherReady) { Dispatcher = s_dispatcher; return; }
        // Compositor 要求"本线程"有 DispatcherQueue。用 CoreMessaging CreateDispatcherQueueController
        // 逐个尝试 threadType/apartmentType 组合，用 GetForCurrentThread() 判定是否真的挂到了本线程
        // （threadType 枚举值各资料不一致，这里不猜，实测哪个值生效就用哪个）。
        foreach (int threadType in new[] { 1, 2, 0 })
        {
            foreach (int apt in new[] { 1, 2, 0 })
            {
                try
                {
                    var opt = new DispatcherQueueOptions { dwSize = 12, threadType = threadType, apartmentType = apt };
                    int hr = CreateDispatcherQueueController(opt, out IntPtr pController);
                    if (hr < 0)
                    {
                        SetupLog.Add($"CreateDispatcherQueueController(threadType={threadType},apt={apt}) hr=0x{hr:X8}");
                        continue;
                    }
                    var controller = DispatcherQueueController.FromAbi(pController);
                    s_controllers.Add(controller);
                    bool onThisThread = DispatcherQueue.GetForCurrentThread() != null;
                    SetupLog.Add($"CreateDispatcherQueueController(threadType={threadType},apt={apt}) hr=0，GetForCurrentThread={(onThisThread ? "非空" : "null")}");
                    if (onThisThread)
                    {
                        Dispatcher = controller;
                        s_dispatcher = controller;
                        s_dispatcherReady = true;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    SetupLog.Add($"CreateDispatcherQueueController(threadType={threadType},apt={apt}) 异常：" + ex.Message);
                }
            }
        }
        // 备用：专用线程 dispatcher（注意：它在别的线程上，满足不了 Compositor 的"本线程"要求，仅留作记录）
        try
        {
            Dispatcher = DispatcherQueueController.CreateOnDedicatedThread();
            s_controllers.Add(Dispatcher);
            SetupLog.Add("DispatcherQueueController.CreateOnDedicatedThread ok（备用，非本线程）");
        }
        catch (Exception ex)
        {
            SetupLog.Add("CreateOnDedicatedThread 异常：" + ex.Message + "（继续，不阻断）");
        }
    }

    /// <summary>
    /// R1：ActivateCustomDrying 的**非致命**调用——失败只记录（HResult + 消息），
    /// 宿主继续搭、探针继续跑。调用位置矩阵见 <see cref="Create"/> 的 dryOrder。
    /// </summary>
    void TryActivateCustomDrying()
    {
        try
        {
            Synchronizer = Presenter.ActivateCustomDrying();
            SetupLog.Add($"ActivateCustomDrying ok（dryOrder，InkSynchronizer 已拿到）");
        }
        catch (Exception ex)
        {
            SetupLog.Add($"ActivateCustomDrying 失败 hr=0x{ex.HResult:X8}：{ex.Message}");
        }
    }

        void AddUi(Windows.UI.Core.PointerEventArgs e) =>
            Stats.Add(Stats.UI_Points, e.CurrentPoint.Position.X, e.CurrentPoint.Position.Y,
                e.CurrentPoint.Properties.Pressure, e.CurrentPoint.Timestamp);

        void AddSi(Windows.UI.Core.PointerEventArgs e) =>
            Stats.Add(Stats.SI_Points, e.CurrentPoint.Position.X, e.CurrentPoint.Position.Y,
                e.CurrentPoint.Properties.Pressure, e.CurrentPoint.Timestamp);

        void AddWs(CoreWetStrokeUpdateEventArgs e)
        {
            foreach (var pt in e.NewInkPoints)
                Stats.Add(Stats.WS_Points, pt.Position.X, pt.Position.Y, pt.Pressure, pt.Timestamp);
        }

        public int StrokeCount()
        {
            try { return Presenter.StrokeContainer.GetStrokes().Count; }
            catch { return -1; }
        }

        public string StrokeRect()
        {
            try
            {
                var r = Presenter.StrokeContainer.BoundingRect;
                return $"({r.X:F1},{r.Y:F1})-({r.X + r.Width:F1},{r.Y + r.Height:F1})";
            }
            catch { return "(查询失败)"; }
        }

        public void ClearStrokes()
        {
            try { Presenter.StrokeContainer.Clear(); }
            catch { }
        }

        // 干墨自绘（合成树载体）：把同样一条红线画进 InkLayer 里的 ShapeVisual。
        // 与 StrokeContainer.Clear() 配对使用，中间不泵消息即为"同一帧交接"。
        public void SelfDryLine(IList<Native.POINT> ptsPx, float thicknessPx)
        {
            ClearSelfDry();
            var sv = Compositor.CreateShapeVisual();
            sv.Size = InkLayer.Size;
            var brush = Compositor.CreateColorBrush(Color.FromArgb(255, 255, 0, 0));
            for (int i = 1; i < ptsPx.Count; i++)
            {
                var sp = Compositor.CreateSpriteShape();
                var lg = Compositor.CreateLineGeometry();
                lg.Start = ToDip(ptsPx[i - 1]);
                lg.End = ToDip(ptsPx[i]);
                sp.Geometry = lg;
                sp.StrokeBrush = brush;
                sp.StrokeThickness = thicknessPx / DpiScale;
                sv.Shapes.Add(sp);
            }
            InkLayer.Children.InsertAtTop(sv);
            SelfDryVisual = sv;
        }

        public void ClearSelfDry()
        {
            if (SelfDryVisual == null) return;
            try { InkLayer.Children.Remove(SelfDryVisual); } catch { }
            SelfDryVisual = null;
        }

        Vector2 ToDip(Native.POINT p) => new Vector2(p.X / DpiScale, p.Y / DpiScale);

        // 自定义干燥观察：收笔时把湿墨撤下（BeginDry）并交还（EndDry），只打印可行性
        public string TryBeginDry()
        {
            if (Synchronizer == null) return "（未启用自定义干燥）";
            try
            {
                var strokes = Synchronizer.BeginDry();
                int n = strokes.Count, pts = 0;
                foreach (var s in strokes) pts += s.GetInkPoints().Count;
                Synchronizer.EndDry();
                return $"BeginDry 返回 {n} 条 Stroke / {pts} 点，EndDry 已调用（干墨接管可行）";
            }
            catch (Exception ex)
            {
                return "BeginDry/EndDry 异常：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        /// <summary>
        /// 自定义干燥探针（--customdry，R1 2026-10-13）：**BeginDry 与 EndDry 分离控制**。
        /// v3：带精确耗时（QPC 只包住调用本身——v2 的 790ms 是测量窗口含截图，不是 EndDry 慢）。
        /// ⚠ 协议（v2 意外验证）：EndDry 必须与 BeginDry 配对，单独调 = E_INVALIDSTATE。
        /// </summary>
        public string BeginDryProbe(out int strokes, out int points, out double ms)
        {
            strokes = 0; points = 0; ms = 0;
            if (Synchronizer == null) return "（未启用自定义干燥）";
            try
            {
                Native.QueryPerformanceCounter(out long t0);
                var list = Synchronizer.BeginDry();
                Native.QueryPerformanceCounter(out long t1);
                ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                strokes = list.Count;
                foreach (var s in list) points += s.GetInkPoints().Count;
                return $"BeginDry 返回 {strokes} 条 / {points} 点（{ms:F3}ms）";
            }
            catch (Exception ex)
            {
                return "BeginDry 异常：" + ex.GetType().Name + "：" + ex.Message;
            }
        }

        /// <summary>自定义干燥探针：EndDry（湿墨应在这一步撤下——探针用逐帧截图验证）。带精确耗时。</summary>
        public string EndDryProbe(out double ms)
        {
            ms = 0;
            if (Synchronizer == null) return "（未启用自定义干燥）";
            try
            {
                Native.QueryPerformanceCounter(out long t0);
                Synchronizer.EndDry();
                Native.QueryPerformanceCounter(out long t1);
                ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                return $"EndDry ok（{ms:F3}ms）";
            }
            catch (Exception ex) { return "EndDry 异常：" + ex.GetType().Name + "：" + ex.Message; }
        }

        /// <summary>v3 诊断：StrokeContainer.GetStrokes() 在自定义干燥的各个阶段能不能读（v2 出过 -1）。</summary>
        public string StrokeCountProbe(out int count)
        {
            try { count = Presenter.StrokeContainer.GetStrokes().Count; return "ok"; }
            catch (Exception ex) { count = -1; return ex.GetType().Name + "：" + ex.Message; }
        }

        /// <summary>v3 诊断：自定义干燥下 ClearStrokes 能不能调（决定 R1 里要不要跳过所有 Clear）。</summary>
        public string ClearStrokesProbe()
        {
            try { Presenter.StrokeContainer.Clear(); return "ok"; }
            catch (Exception ex) { return ex.GetType().Name + "：" + ex.Message; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct DispatcherQueueOptions { public uint dwSize; public int threadType; public int apartmentType; }

        [System.Runtime.InteropServices.DllImport("coremessaging.dll")]
        static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    }
}
