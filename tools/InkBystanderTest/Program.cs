// Program.cs —— 窗口 + 消息泵 + --selftest 自动取证 + --customdry 自定义干燥探针 + 交互模式
// 目的：验证"旁听模式"（湿墨系统画，笔迹点自己收）在 Win10/Win11 的可行性：
//   通道1 InkPresenter.UnprocessedInput、通道2 CoreWetStrokeUpdateSource、通道3 raw input，
//   同时统计宿主自己窗口的 WM_POINTER/WM_MOUSE 是否被吞，并用逐帧截图证明系统真的画了湿墨。
// --customdry（R1 前置探针，2026-10-13）：ActivateCustomDrying 在受理输入前调用，
//   逐笔回答"系统只画湿墨不画干墨？旁听通道还在不在？BeginDry/EndDry 帧时序？"
//   ——托管路线要不要上自定义干燥（干掉 ClearStrokes 交接赛道）全凭这轮数据。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.UI.Input.Inking;

namespace InkBystanderTest
{
    internal class Program
    {
        const int CLIENT_W = 800, CLIENT_H = 600;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static IntPtr _hwnd;
        static InkHost _host;
        static BystanderStats _stats = new BystanderStats();
        static WndProcDelegate _wndProc; // 保活，防 GC 回收函数指针
        static long _qpcFreq;
        static float _dpiScale = 1f;
        static Native.POINT _winOrigin;
        static bool _quit;
        static int _clientW = CLIENT_W, _clientH = CLIENT_H;

        static void Print(string s = "") => Console.WriteLine(s);

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            bool selftest = Array.Exists(args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
            bool lag = Array.Exists(args, a => string.Equals(a, "--lag", StringComparison.OrdinalIgnoreCase));
            // --customdry：宿主创建时就 ActivateCustomDrying（**必须在受理输入前**，官方口径）。
            bool customDry = Array.Exists(args, a => string.Equals(a, "--customdry", StringComparison.OrdinalIgnoreCase));

            // 全程物理像素坐标，截图像素与注入坐标口径一致
            Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            Native.QueryPerformanceFrequency(out _qpcFreq);

            if (!CreateMainWindow(out string winErr))
            {
                Print("窗口创建失败：" + winErr);
                return 2;
            }
            RegisterRawInput();

            _host = InkHost.Create(_hwnd, _clientW, _clientH, _dpiScale, customDry ? 1 : 0, _stats, out Exception fatal);
            Print("== 墨迹宿主搭建日志（InkDesktopHost/CoreInkPresenterHost 一族，探针同款用法）==");
            foreach (var line in _host.SetupLog) Print("  " + line);
            if (fatal != null)
            {
                Print("搭建失败：" + fatal);
                return 3;
            }
            Print($"  → 屏幕墨迹托管就绪；StrokeContainer 初始 {_host.StrokeCount()} 条");

            if (customDry)
            {
                int rc = RunCustomDryingProbe();
                Native.DestroyWindow(_hwnd);
                Pump(80);
                return rc;
            }

            if (selftest)
            {
                int rc = RunSelfTest();
                Native.DestroyWindow(_hwnd);
                Pump(80);
                return rc;
            }

            if (lag) { RunLagInteractive(); return 0; }

            RunInteractive();
            return 0;
        }

        // ---------------- 窗口 ----------------
        static bool CreateMainWindow(out string err)
        {
            err = "";
            _wndProc = WndProc;
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
                style = Native.CS_HREDRAW | Native.CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = Marshal.GetHINSTANCE(typeof(Program).Module),
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
                lpszClassName = "InkBystanderTestWnd"
            };
            if (Native.RegisterClassExW(ref wc) == 0)
            {
                int lastErr = Marshal.GetLastWin32Error();
                if (lastErr != 1410) { err = "RegisterClassExW 失败，err=" + lastErr; return false; } // 1410=ERROR_CLASS_ALREADY_EXISTS（重建窗口时正常）
            }

            _hwnd = Native.CreateWindowExW(0, wc.lpszClassName, "InkBystanderTest — 旁听模式验证",
                Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE, 120, 120, CLIENT_W + 32, CLIENT_H + 64,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { err = "CreateWindowExW 失败"; return false; }

            // 把客户区修到精确 800x600（两步法，免 AdjustWindowRectExForDpi）
            Native.GetClientRect(_hwnd, out Native.RECT rc);
            Native.GetWindowRect(_hwnd, out Native.RECT wr);
            int dw = CLIENT_W - (rc.Right - rc.Left), dh = CLIENT_H - (rc.Bottom - rc.Top);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, (wr.Right - wr.Left) + dw, (wr.Bottom - wr.Top) + dh,
                Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
            Native.GetClientRect(_hwnd, out rc);
            _clientW = rc.Right - rc.Left; _clientH = rc.Bottom - rc.Top;

            Native.ShowWindow(_hwnd, Native.SW_SHOW);
            Native.UpdateWindow(_hwnd);
            Native.SetWindowPos(_hwnd, (IntPtr)Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);

            uint dpi = Native.GetDpiForWindow(_hwnd);
            _dpiScale = dpi / 96f;
            var origin = new Native.POINT { X = 0, Y = 0 };
            Native.ClientToScreen(_hwnd, ref origin);
            _winOrigin = origin;
            return true;
        }

        static void RegisterRawInput()
        {
            var devs = new[]
            {
                new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd }, // 鼠标
                new Native.RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd }, // 笔（Digitizer/Pen）
                new Native.RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x01, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd }, // 数字化仪
            };
            bool ok = Native.RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
            Print($"raw input 注册（鼠标/笔/数字化仪，INPUTSINK）：{(ok ? "ok" : "失败")}");
        }

        // ---------------- 窗口过程：统计宿主自己收到的消息 + raw input 旁听 ----------------
        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_POINTERDOWN: _stats.Wm_PointerDown++; break;
                case Native.WM_POINTERUPDATE: _stats.Wm_PointerUpdate++; break;
                case Native.WM_POINTERUP: _stats.Wm_PointerUp++; break;
                case Native.WM_POINTERENTER: _stats.Wm_PointerEnter++; break;
                case Native.WM_MOUSEMOVE: _stats.Wm_MouseMove++; break;
                case Native.WM_LBUTTONDOWN: _stats.Wm_LButtonDown++; break;
                case Native.WM_LBUTTONUP: _stats.Wm_LButtonUp++; break;
                case Native.WM_RBUTTONDOWN: _stats.Wm_RButtonDown++; break;
                case Native.WM_RBUTTONUP: _stats.Wm_RButtonUp++; break;
                case Native.WM_INPUT: ParseRawInput(lParam); break;
                case Native.WM_KEYDOWN:
                    if (wParam.ToInt32() == 0x1B) { Native.DestroyWindow(hWnd); }      // Esc
                    else if (wParam.ToInt32() == 0x43) { _host?.ClearStrokes(); Print("[C] StrokeContainer 已清"); } // C
                    break;
                case Native.WM_ERASEBKGND: return (IntPtr)1; // 背景由合成器白色视觉画
                case Native.WM_DESTROY:
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        static void ParseRawInput(IntPtr hRawInput)
        {
            uint size = 0;
            uint hdrSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
            Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, hdrSize);
            if (size == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                Native.GetRawInputData(hRawInput, Native.RID_INPUT, buf, ref size, hdrSize);
                var hdr = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
                switch (hdr.dwType)
                {
                    case Native.RIM_TYPEMOUSE: _stats.Raw_Mouse++; break;
                    case Native.RIM_TYPEKEYBOARD: _stats.Raw_Keyboard++; break;
                    case Native.RIM_TYPEHID:
                        _stats.Raw_Hid++;
                        int dwSizeHid = Marshal.ReadInt32(buf, (int)hdrSize);
                        int dwCount = Marshal.ReadInt32(buf, (int)hdrSize + 4);
                        _stats.Raw_HidPackets += dwCount > 0 ? dwCount : 1;
                        if (string.IsNullOrEmpty(_stats.Raw_HidName))
                            _stats.Raw_HidName = Native.GetRawInputDeviceName(hdr.hDevice);
                        break;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---------------- 消息泵（STA 下 WinRT 事件靠消息泵送达）----------------
        static void Pump(int ms)
        {
            long freq = Stopwatch.Frequency;
            long end = Stopwatch.GetTimestamp() + ms * freq / 1000;
            while (Stopwatch.GetTimestamp() < end)
            {
                while (Native.PeekMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
                {
                    if (msg.message == Native.WM_QUIT) { _quit = true; return; }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessageW(ref msg);
                }
                Thread.Sleep(1);
            }
        }

        static void SetStats(BystanderStats s) { _stats = s; if (_host != null) _host.Stats = s; }

        static Native.POINT ClientToScreen(int x, int y)
        {
            var p = new Native.POINT { X = x, Y = y };
            Native.ClientToScreen(_hwnd, ref p);
            return p;
        }

        // 光标移出窗口（白色光标会污染非白像素计数）
        static void MoveCursorOutside()
        {
            Native.GetWindowRect(_hwnd, out Native.RECT wr);
            int x = wr.Right + 200, y = wr.Top + 50;
            if (x > Native.GetSystemMetrics(Native.SM_CXSCREEN) - 10) x = Math.Max(10, wr.Left - 200);
            Native.SetCursorPos(x, y);
        }

        // ---------------- 轨迹定义与执行 ----------------
        enum InjectKind { MouseLeft, MouseRight, Pen }

        class TrajSpec
        {
            public string Label;
            public InjectKind Kind;
            public int X0, Y0, X1, Y1, Steps;
        }

        static void RunTrajectory(TrajSpec t, out List<Native.FrameStats> frames, out List<Native.FrameStats> after)
        {
            frames = new List<Native.FrameStats>();
            after = new List<Native.FrameStats>();
            var s0 = ClientToScreen(t.X0, t.Y0);
            var s1 = ClientToScreen(t.X1, t.Y1);

            MoveCursorOutside();
            Pump(60);

            if (t.Kind == InjectKind.Pen)
            {
                // 合成笔：走系统指针栈的 PT_PEN 通道（探针实测该通道能触发系统湿墨渲染）
                IntPtr hPen = Native.CreatePenDevice();
                Native.PenDown(hPen, _hwnd, s0.X, s0.Y);
                Pump(60);
                for (int i = 1; i <= t.Steps; i++)
                {
                    var p = Lerp(s0, s1, i / (double)t.Steps);
                    Native.PenMove(hPen, _hwnd, p.X, p.Y);
                    Pump(16);
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) frames.Add(fs);
                }
                Native.PenUp(hPen, _hwnd, s1.X, s1.Y);
                Pump(80);
            }
            else
            {
                bool right = t.Kind == InjectKind.MouseRight;
                Native.SetCursorPos(s0.X, s0.Y);
                Native.SendMouseAbsMove(s0.X, s0.Y);
                Pump(120);
                Native.SendMouseButton(true, right);
                Pump(60);
                for (int i = 1; i <= t.Steps; i++)
                {
                    var p = Lerp(s0, s1, i / (double)t.Steps);
                    Native.SendMouseAbsMove(p.X, p.Y);
                    Pump(16);
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) frames.Add(fs);
                }
                Native.SendMouseButton(false, right);
                Pump(80);
            }

            // 收笔后观察：光标先移出窗口，再逐帧截图（看湿墨→干墨变化）
            MoveCursorOutside();
            Pump(80);
            for (int k = 0; k < 10; k++)
            {
                if (Native.CaptureClientFrame(_hwnd, out var fs)) after.Add(fs);
                Pump(45);
            }
        }

        static Native.POINT Lerp(Native.POINT a, Native.POINT b, double t) =>
            new Native.POINT { X = (int)Math.Round(a.X + (b.X - a.X) * t), Y = (int)Math.Round(a.Y + (b.Y - a.Y) * t) };

        // ---------------- 报告 ----------------
        static void Report(TrajSpec t, BystanderStats st, List<Native.FrameStats> frames, List<Native.FrameStats> after)
        {
            // 旁听三通道 + 补充通道
            Print($"  通道1 UnprocessedInput：Pressed {st.UI_Pressed} / Moved {st.UI_Moved} / Released {st.UI_Released} / Entered {st.UI_Entered} / Hovered {st.UI_Hovered} / Exited {st.UI_Exited} / Lost {st.UI_Lost}");
            Print($"       点据：{st.PointsSummary(st.UI_Points, _qpcFreq)}");
            Print($"  通道2 CoreWetStrokeUpdateSource：Starting {st.WS_Starting} / Continuing {st.WS_Continuing} / Stopping {st.WS_Stopping} / Completed {st.WS_Completed} / Canceled {st.WS_Canceled}");
            Print($"       点据：{st.PointsSummary(st.WS_Points, _qpcFreq)}");
            Print($"  补充 InkStrokeInput：Started {st.SI_Started} / Continued {st.SI_Continued} / Ended {st.SI_Ended} / Canceled {st.SI_Canceled}");
            Print($"       点据：{st.PointsSummary(st.SI_Points, _qpcFreq)}");
            Print($"  通道3 raw input：鼠标包 {st.Raw_Mouse} / 键盘包 {st.Raw_Keyboard} / HID 包 {st.Raw_Hid}(共 {st.Raw_HidPackets} 个 report) 设备[{st.Raw_HidName}]");
            Print($"  宿主窗口消息：WM_POINTERDOWN {st.Wm_PointerDown} / UPDATE {st.Wm_PointerUpdate} / UP {st.Wm_PointerUp} / ENTER {st.Wm_PointerEnter}；WM_LBUTTONDOWN {st.Wm_LButtonDown} / WM_LBUTTONUP {st.Wm_LButtonUp} / WM_RBUTTONDOWN {st.Wm_RButtonDown} / WM_MOUSEMOVE {st.Wm_MouseMove}");
            Print($"  系统产出：StrokesCollected 事件 {st.StrokesCollectedEvents} 次 / 共 {st.StrokesCollectedCount} 条；收笔后 StrokeContainer {_host.StrokeCount()} 条，bbox {_host.StrokeRect()}");

            // 逐帧截图取证
            int withInk = 0, peak = 0;
            Native.FrameStats peakFrame = default, union = default;
            bool any = false;
            foreach (var f in frames)
            {
                if (f.RedCount <= 0) continue;
                withInk++;
                if (f.RedCount > peak) { peak = f.RedCount; peakFrame = f; }
                if (!any) { union = f; any = true; }
                else
                {
                    union.MinX = Math.Min(union.MinX, f.MinX);
                    union.MinY = Math.Min(union.MinY, f.MinY);
                    union.MaxX = Math.Max(union.MaxX, f.MaxX);
                    union.MaxY = Math.Max(union.MaxY, f.MaxY);
                }
            }
            Print($"  逐帧截图：{frames.Count} 帧，含墨帧 {withInk}（出墨率 {(frames.Count > 0 ? withInk * 100.0 / frames.Count : 0):F0}%），墨色像素峰值 {peak}（该帧非白 {peakFrame.NonWhiteCount}）");
            if (any)
                Print($"           红像素 bbox（客户区 px）= ({union.MinX},{union.MinY})-({union.MaxX},{union.MaxY})");
            var sbf = new StringBuilder();
            foreach (var f in frames) sbf.Append(f.RedCount).Append(",");
            Print($"           逐帧墨色像素：{sbf}");
            var sb = new StringBuilder();
            foreach (var f in after) sb.Append(f.RedCount).Append(",");
            Print($"  收笔后 10 帧墨色像素：{sb}（湿墨变干墨/消失的证据）");

            // 坐标口径核对
            Print("  坐标口径核对（旁听点 bbox vs 截图像素 bbox，客户区 px）：");
            if (any)
                Print($"           像素 bbox = ({union.MinX},{union.MinY})-({union.MaxX},{union.MaxY})（含笔宽约 4px 膨胀）");
            else
                Print("           像素 bbox = 无墨（无从核对）");
            CoordCheck("通道1 UnprocessedInput", st.UI_Points, any ? union : default, any);
            CoordCheck("通道2 CoreWetStrokeUpdateSource", st.WS_Points, any ? union : default, any);
            CoordCheck("补充 InkStrokeInput", st.SI_Points, any ? union : default, any);
        }

        static void CoordCheck(string label, List<PtRec> pts, Native.FrameStats pix, bool hasPix)
        {
            if (pts.Count == 0) { Print($"           {label}：无旁听点，跳过"); return; }
            BystanderStats.Bbox(pts, out double x0, out double y0, out double x1, out double y1);
            string verdict = "无像素可比";
            if (hasPix)
            {
                // 三个候选口径的残差（取四角最大偏差，px）
                double rClient = Resid(x0, y0, x1, y1, pix.MinX, pix.MinY, pix.MaxX, pix.MaxY);
                double rScreen = Resid(x0 - _winOrigin.X, y0 - _winOrigin.Y, x1 - _winOrigin.X, y1 - _winOrigin.Y, pix.MinX, pix.MinY, pix.MaxX, pix.MaxY);
                double rDip = Resid(x0 * _dpiScale, y0 * _dpiScale, x1 * _dpiScale, y1 * _dpiScale, pix.MinX, pix.MinY, pix.MaxX, pix.MaxY);
                verdict = $"客户区px残差 {rClient:F1} / 屏幕px残差 {rScreen:F1} / DIP(×{_dpiScale:F2})残差 {rDip:F1}"
                    + (Math.Min(rClient, Math.Min(rScreen, rDip)) <= 12
                        ? " → 判定=" + (rClient <= rScreen && rClient <= rDip ? "客户区 px/DIP(1:1)" : rScreen <= rDip ? "屏幕 px" : "DIP 空间")
                        : " → 三者都不吻合（需人工看）");
            }
            Print($"           {label}：点 bbox ({x0:F1},{y0:F1})-({x1:F1},{y1:F1})；{verdict}");
        }

        static double Resid(double x0, double y0, double x1, double y1, int px0, int py0, int px1, int py1) =>
            Math.Max(Math.Max(Math.Abs(x0 - px0), Math.Abs(y0 - py0)), Math.Max(Math.Abs(x1 - px1), Math.Abs(y1 - py1)));

        // ---------------- 自检主流程 ----------------
        static int RunSelfTest()
        {
            Print();
            Print("################ InkBystanderTest --selftest ################");
            Print($"环境：{Environment.OSVersion}，进程 {(Environment.Is64BitProcess ? "x64" : "x86")}，QPC 频率 {_qpcFreq}");
            Print($"窗口：客户区 {_clientW}x{_clientH} px，屏幕原点 ({_winOrigin.X},{_winOrigin.Y})，DPI {Native.GetDpiForWindow(_hwnd)}（scale {_dpiScale:F2}）");

            Native.SetForegroundWindow(_hwnd);
            Native.BringWindowToTop(_hwnd);
            Pump(120);
            Print("前台校验：" + (Native.GetForegroundWindow() == _hwnd ? "窗口已置前" : "警告：窗口未置前，截图可能被遮挡"));

            var trajs = new[]
            {
                new TrajSpec { Label = "T1【SendInput 鼠标左键】直线", Kind = InjectKind.MouseLeft, X0 = 200, Y0 = 150, X1 = 600, Y1 = 150, Steps = 30 },
                new TrajSpec { Label = "T2【SendInput 鼠标左键】斜线", Kind = InjectKind.MouseLeft, X0 = 200, Y0 = 320, X1 = 600, Y1 = 470, Steps = 30 },
                new TrajSpec { Label = "T3【合成笔 PT_PEN 注入】直线", Kind = InjectKind.Pen, X0 = 200, Y0 = 545, X1 = 600, Y1 = 545, Steps = 30 },
            };

            foreach (var t in trajs)
            {
                Print();
                Print(new string('=', 74));
                Print($"轨迹 {t.Label}：({t.X0},{t.Y0}) → ({t.X1},{t.Y1})，{t.Steps} 步");
                var st = new BystanderStats();
                SetStats(st);
                _host.ClearStrokes();
                Pump(80);
                RunTrajectory(t, out var frames, out var after);
                Report(t, st, frames, after);
                int beforeClear = _host.StrokeCount();
                _host.ClearStrokes();
                Print($"  [清场] Clear() 前 {beforeClear} 条 → Clear() 后 {_host.StrokeCount()} 条（防残留污染下一轮）");
                Pump(80);
            }

            // T4：右键拖 + RightDragAction=LeaveUnprocessed —— 通道1 的对照实验
            // （通道1 拿的是"不被 InkPresenter 处理成墨的输入"；用对照实验证明订阅本身是通的）
            Print();
            Print(new string('=', 74));
            Print("轨迹 T4【通道1 对照】右键拖 + RightDragAction=LeaveUnprocessed（预期不出墨，只验通道1）");
            try
            {
                _host.Presenter.InputProcessingConfiguration.RightDragAction = InkInputRightDragAction.LeaveUnprocessed;
                Print("  已设 InputProcessingConfiguration.RightDragAction = LeaveUnprocessed");
            }
            catch (Exception ex) { Print("  设置 RightDragAction 失败：" + ex.Message); }
            {
                var st = new BystanderStats();
                SetStats(st);
                _host.ClearStrokes();
                Pump(80);
                var t = new TrajSpec { Label = "T4", Kind = InjectKind.MouseRight, X0 = 200, Y0 = 240, X1 = 600, Y1 = 240, Steps = 20 };
                RunTrajectory(t, out var frames, out var after);
                Report(t, st, frames, after);
                _host.ClearStrokes();
            }

            // T5：跟手量化（合成快甩，逐点间隙 px/ms + 端到端 ms）
            {
                var st = new BystanderStats();
                SetStats(st);
                _host.ClearStrokes();
                Pump(80);
                RunLagTrajectory(st);
                int bc = _host.StrokeCount();
                _host.ClearStrokes();
                Print($"  [清场] Clear() 前 {bc} 条 → Clear() 后 {_host.StrokeCount()} 条（防残留污染下一轮）");
                Pump(80);
            }

            // T6：干墨无缝衔接（同帧 Clear()+自绘，前后各 20 帧连续性）
            RunDryHandoff();
            Pump(80);

            RunDryingPhase();

            Print();
            Print("################ 自检说明 ################");
            Print("1) 通道3 raw input：SendInput 鼠标产生 raw 鼠标包、PT_PEN 合成注入产生 HID 包（设备为 Microsoft HID RID 虚拟数字化仪）；真笔 HID 报文/压感/倾斜未覆盖，需真机手写补测（交互模式）；");
            Print("2) SendInput 合成鼠标不产生 WM_POINTER（此前实测结论），本次继续记录验证；");
            Print("3) 每条轨迹前 Clear() 清干墨，截图前光标移出窗口，红像素判定对光标免疫；");
            Print("4) T5 跟手量化用合成注入快甩；真笔跟手请跑 --lag 交互模式（端到端 ms 需注入时刻，真笔拿不到，仅合成可测）；");
            Print("5) T6 干墨交接：三种顺序对照（同帧 Clear→自绘 / 同帧 自绘→Clear / 重叠 自绘→泵一帧→Clear）；自绘首调已预热排除 JIT 冷启动；GDI 可见性以预探实测为准，不可见则自绘落合成树。");
            return 0;
        }

        // ---------------- 收笔后湿墨归属 + 干墨接管观察 ----------------
        static void RunDryingPhase()
        {
            Print();
            Print(new string('=', 74));
            Print("收笔后湿墨归属与干墨接管（ActivateCustomDrying + InkSynchronizer）观察");
            Print($"  当前 StrokeContainer={_host.StrokeCount()} 条；系统湿墨收笔后的归属见上各轨迹「收笔后 10 帧」行");

            string actMsg;
            try
            {
                _host.Synchronizer = _host.Presenter.ActivateCustomDrying();
                actMsg = "成功（拿到 InkSynchronizer）";
            }
            catch (Exception ex) { actMsg = "失败：" + ex.GetType().Name + "：" + ex.Message; }
            Print("  ActivateCustomDrying（在输入已被受理过之后调用）：" + actMsg);

            if (_host.Synchronizer == null)
            {
                Print("  → 按官方约束重建窗口 + 宿主（customDrying=true，在受理输入前调用）再试");
                RecreateHostWithCustomDrying();
                if (_host == null || _host.Synchronizer == null)
                {
                    Print("  重建后仍未拿到 InkSynchronizer，接管观察终止");
                    return;
                }
            }

            var st = new BystanderStats();
            SetStats(st);
            _host.ClearStrokes();
            Pump(80);
            var t = new TrajSpec { Label = "T7【自定义干燥观察】", Kind = InjectKind.MouseLeft, X0 = 250, Y0 = 420, X1 = 550, Y1 = 420, Steps = 15 };
            RunTrajectory(t, out var frames, out var after);
            Print($"  自定义干燥下的旁听：通道1 点 {st.UI_Points.Count}，通道2 点 {st.WS_Points.Count}，补充通道点 {st.SI_Points.Count}；StrokesCollected {st.StrokesCollectedEvents} 次/共 {st.StrokesCollectedCount} 条");
            Print($"  收笔后 StrokeContainer={_host.StrokeCount()} 条（自定义干燥下通常为 0，等 BeginDry 接管）");
            Print("  干墨接管：" + _host.TryBeginDry());
            Print($"  BeginDry/EndDry 之后 StrokeContainer={_host.StrokeCount()} 条");
        }

        static void RecreateHostWithCustomDrying()
        {
            try
            {
                Native.DestroyWindow(_hwnd);
                Pump(80);
                _quit = false;
                if (!CreateMainWindow(out string err)) { Print("  重建窗口失败：" + err); _host = null; return; }
                RegisterRawInput();
                _host = InkHost.Create(_hwnd, _clientW, _clientH, _dpiScale, 1, _stats, out Exception fatal);
                foreach (var line in _host.SetupLog) Print("    " + line);
                if (fatal != null) { Print("  重建宿主失败：" + fatal); _host = null; }
            }
            catch (Exception ex)
            {
                Print("  重建异常：" + ex.Message);
                _host = null;
            }
        }

        // ---------------- 跟手量化（T5 合成快甩；与 --lag 共用口径） ----------------
        class LagSample
        {
            public double InjX;     // 注入点 X（客户区 px）
            public long InjQpc;     // 注入时刻（QPC）
            public int EdgeX;       // 当帧墨迹 bbox 前缘 MaxX（无墨 = int.MinValue）
            public int Red;         // 当帧墨像素数
            public long FrameQpc;   // 截帧时刻（QPC）
        }

        static void RunLagTrajectory(BystanderStats st)
        {
            Print();
            Print(new string('=', 74));
            Print("轨迹 T5【跟手量化】SendInput 快甩直线：(200,260) → (600,260)，30 步，泵送 3ms/步");
            Print("  口径：间隙px = 本步注入点X − 当帧墨迹bbox前缘MaxX（左→右快甩取 X 方向，负=系统预测让墨超前）；");
            Print("        间隙ms = 间隙px ÷ 当时速度(px/ms)；端到端ms = 注入时刻→该点首次被墨前缘越过的截帧时刻（含 SendInput 延迟，粒度≈泵送周期）");
            MoveCursorOutside();
            Pump(60);
            var s0 = ClientToScreen(200, 260);
            var s1 = ClientToScreen(600, 260);
            Native.SetCursorPos(s0.X, s0.Y);
            Native.SendMouseAbsMove(s0.X, s0.Y);
            Pump(120);
            Native.SendMouseButton(true, false);
            Pump(30);
            var samples = new List<LagSample>();
            int steps = 30;
            for (int i = 1; i <= steps; i++)
            {
                var p = Lerp(s0, s1, i / (double)steps);
                Native.QueryPerformanceCounter(out long t0);
                Native.SendMouseAbsMove(p.X, p.Y);
                Pump(3); // 快甩：压低泵送周期，逼出跟手滞后
                Native.CaptureClientFrame(_hwnd, out var fs);
                Native.QueryPerformanceCounter(out long t1);
                samples.Add(new LagSample
                {
                    InjX = p.X - _winOrigin.X,
                    InjQpc = t0,
                    EdgeX = fs.RedCount > 0 ? fs.MaxX : int.MinValue,
                    Red = fs.RedCount,
                    FrameQpc = t1,
                });
            }
            Native.SendMouseButton(false, false);
            Pump(80);
            MoveCursorOutside();

            // 逐点报表
            Print("   步#   注入X  墨前缘X   间隙px  速度px/ms   间隙ms   当帧墨px");
            var gaps = new List<double>();
            var gapMsList = new List<double>();
            var speeds = new List<double>();
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                double speed = 0, gap = double.NaN, gapMs = double.NaN;
                if (i > 0)
                {
                    double dtMs = (s.InjQpc - samples[i - 1].InjQpc) / 10000.0;
                    if (dtMs > 0) speed = (s.InjX - samples[i - 1].InjX) / dtMs;
                }
                if (s.EdgeX != int.MinValue)
                {
                    gap = s.InjX - s.EdgeX;
                    if (speed > 0.01) gapMs = gap / speed;
                    gaps.Add(gap);
                    if (!double.IsNaN(gapMs)) gapMsList.Add(gapMs);
                }
                if (speed > 0.01) speeds.Add(speed);
                string edgeStr = s.EdgeX == int.MinValue ? "无墨" : s.EdgeX.ToString();
                Print($"  {i + 1,3}  {s.InjX,6:F1}  {edgeStr,6}  {gap,7:F1}  {speed,9:F2}  {gapMs,7:F2}  {s.Red,7}");
            }

            // 端到端：注入点首次被墨前缘越过的时刻
            var e2e = new List<double>();
            int never = 0;
            for (int j = 0; j < samples.Count; j++)
            {
                double best = -1;
                for (int k = j; k < samples.Count; k++)
                {
                    if (samples[k].EdgeX != int.MinValue && samples[k].EdgeX >= samples[j].InjX)
                    { best = (samples[k].FrameQpc - samples[j].InjQpc) / 10000.0; break; }
                }
                if (best >= 0) e2e.Add(best); else never++;
            }

            // 时间戳口径核验：通道2 Timestamp 的哪种单位解释与注入总时长（QPC）一致
            double injSpanMs = (samples[samples.Count - 1].InjQpc - samples[0].InjQpc) / 10000.0;
            double wsSpanUsMs = 0, wsSpan100nsMs = 0;
            if (st.WS_Points.Count > 1)
            {
                double raw = (double)st.WS_Points[st.WS_Points.Count - 1].Qpc - st.WS_Points[0].Qpc;
                wsSpanUsMs = raw / 1000.0;
                wsSpan100nsMs = raw / 10000.0;
            }
            bool tsUs = Math.Abs(wsSpanUsMs - injSpanMs) <= Math.Abs(wsSpan100nsMs - injSpanMs);
            Print($"  [时间戳口径] 注入总时长 {injSpanMs:F1}ms（QPC）；通道2 总跨：µs口径 {wsSpanUsMs:F1}ms / 100ns口径 {wsSpan100nsMs:F1}ms → 判定 Timestamp = {(tsUs ? "µs" : "100ns")} 口径");

            // 注入→通道2 模型延迟（按 X 就近匹配；时间戳按核验出的口径换算，注入 QPC 固定 100ns）
            double tsDiv = tsUs ? 1000.0 : 10000.0;
            var lat = new List<double>();
            foreach (var ws in st.WS_Points)
            {
                LagSample best = null;
                double bd = double.MaxValue;
                foreach (var s in samples) { double d = Math.Abs(s.InjX - ws.X); if (d < bd) { bd = d; best = s; } }
                if (best != null) lat.Add((double)ws.Qpc / tsDiv - best.InjQpc / 10000.0);
            }

            Stat(gaps, out double gAvg, out double gMed, out double gMin, out double gMax);
            Stat(gapMsList, out double gmAvg, out double gmMed, out double gmMin, out double gmMax);
            Stat(speeds, out double sAvg, out _, out _, out double sMax);
            Stat(e2e, out double eAvg, out double eMed, out double eMin, out double eMax);
            Stat(lat, out double lAvg, out _, out double lMin, out double lMax);
            Print($"  [跟手汇总] 间隙px：均值 {gAvg:F1} / 中位 {gMed:F1} / 最小 {gMin:F1} / 最大 {gMax:F1}（共 {gaps.Count} 采样）");
            Print($"             间隙ms：均值 {gmAvg:F2} / 中位 {gmMed:F2} / 最小 {gmMin:F2} / 最大 {gmMax:F2}");
            Print($"             速度px/ms：均值 {sAvg:F2} / 最大 {sMax:F2}");
            Print($"             端到端ms（可测 {e2e.Count}/{samples.Count} 点，未越到 {never} 点=收笔截断）：均值 {eAvg:F2} / 中位 {eMed:F2} / 最小 {eMin:F2} / 最大 {eMax:F2}");
            Print($"             注入→通道2模型延迟ms（X 就近匹配 {lat.Count} 点）：均值 {lAvg:F2} / 最小 {lMin:F2} / 最大 {lMax:F2}（Timestamp 按上方核验口径换算，注入 QPC 固定 100ns）");
            Print($"  通道2 点数 {st.WS_Points.Count}；StrokesCollected {st.StrokesCollectedEvents} 次/共 {st.StrokesCollectedCount} 条");
        }

        // ---------------- T6 干墨无缝衔接 ----------------
        static void RunDryHandoff()
        {
            Print();
            Print(new string('=', 74));
            Print("轨迹 T6【干墨无缝衔接】收笔后 Clear()+自绘干墨，交接前后各 20 帧连续性检查（三种交接顺序对照）");
            Print("  口径：交接 = StrokeContainer.Clear() + 自绘同样红线；连续性 = 交接前后各 20 帧墨像素序列：无掉零、无大跳；逐帧像素数全量打印；");
            Print("        A【同帧 Clear→自绘】B【同帧 自绘→Clear】两步之间不泵消息；C【重叠交接】自绘→泵一帧→Clear（同位置红线短暂双画）");
            Print("        若 Clear 移除立即上屏、自绘插入等下一帧提交，则 A/B 都会露 1 帧空白，只有 C 能做到任何帧都有墨");

            // 0) GDI 可见性预探：合成树内容是否盖住窗口 GDI 绘制（决定自绘载体）
            var probe = new List<Native.POINT>
            {
                new Native.POINT { X = 100, Y = 580 }, new Native.POINT { X = 200, Y = 580 }, new Native.POINT { X = 300, Y = 580 }
            };
            Native.GdiDrawPolyline(_hwnd, probe, 4, 0x000000FF);
            Pump(32);
            Native.CaptureClientFrame(_hwnd, out var pf);
            Native.GdiFillWhite(_hwnd);
            Pump(32);
            Native.CaptureClientFrame(_hwnd, out var pe);
            bool gdiVisible = pf.RedCount > 50;
            Print($"  GDI 可见性预探：画红线后红像素 {pf.RedCount}，刷白后 {pe.RedCount} → 自绘载体 = {(gdiVisible ? "GDI 窗口 DC" : "合成树 ShapeVisual（GDI 被合成内容覆盖，改等价自绘）")}");

            // 0.5) 预热：SelfDryLine 首次调用 JIT 冷启动实测 ~75ms，会拖长交接窗口、制造假空白帧
            var warm = new List<Native.POINT>
            {
                new Native.POINT { X = 200, Y = 560 }, new Native.POINT { X = 400, Y = 560 }, new Native.POINT { X = 600, Y = 560 }
            };
            Native.QueryPerformanceCounter(out long w0);
            if (gdiVisible) { Native.GdiDrawPolyline(_hwnd, warm, 4, 0x000000FF); Native.GdiFillWhite(_hwnd); }
            _host.SelfDryLine(warm, 4);
            _host.ClearSelfDry();
            Native.QueryPerformanceCounter(out long w1);
            Pump(32);
            Print($"  [预热] 自绘首调（含 JIT）耗时 {(w1 - w0) * 1000.0 / _qpcFreq:F3} ms，已预热，交接测量不再含冷启动");

            // A/B/C 三轮对照（不同 y，防止残留互相污染；每轮末尾清场）
            PrintHandoffCycle("A【同帧 Clear→自绘】", 100, gdiVisible, 0);
            PrintHandoffCycle("B【同帧 自绘→Clear】", 180, gdiVisible, 1);
            PrintHandoffCycle("C【重叠交接：自绘→泵一帧→Clear】", 260, gdiVisible, 2);
        }

        // 一轮完整交接测量：画线生长 → 收笔落定 → 交接前 20 帧 → 交接（顺序由 mode 决定）→ 交接后 20 帧 → 判定 → 清场
        // mode：0=同帧 Clear→自绘；1=同帧 自绘→Clear；2=重叠交接（自绘→泵一帧→Clear）
        static void PrintHandoffCycle(string label, int y, bool gdiVisible, int mode)
        {
            Print();
            Print($"  ---- 轮次 {label}（线 y={y}）----");

            // 1) 画线（30 步鼠标，逐步截图看生长）
            MoveCursorOutside();
            Pump(60);
            var s0 = ClientToScreen(200, y);
            var s1 = ClientToScreen(600, y);
            Native.SetCursorPos(s0.X, s0.Y);
            Native.SendMouseAbsMove(s0.X, s0.Y);
            Pump(120);
            Native.SendMouseButton(true, false);
            Pump(30);
            var grow = new List<int>();
            var line = new List<Native.POINT>();
            for (int i = 1; i <= 30; i++)
            {
                var p = Lerp(s0, s1, i / 30.0);
                Native.SendMouseAbsMove(p.X, p.Y);
                line.Add(new Native.POINT { X = p.X - _winOrigin.X, Y = p.Y - _winOrigin.Y });
                Pump(16);
                if (Native.CaptureClientFrame(_hwnd, out var fs)) grow.Add(fs.RedCount);
            }
            Native.SendMouseButton(false, false);

            // 2) 等收笔异步落定（干墨进 StrokeContainer）
            for (int k = 0; k < 20 && _host.StrokeCount() <= 0; k++) Pump(20);
            Pump(60);
            MoveCursorOutside();
            Print($"  收笔落定：StrokeContainer={_host.StrokeCount()} 条，bbox {_host.StrokeRect()}");

            // 3) 交接前 20 帧（系统干墨稳态）
            var before = new List<int>();
            for (int k = 0; k < 20; k++)
            {
                if (Native.CaptureClientFrame(_hwnd, out var fs)) before.Add(fs.RedCount);
                Pump(16);
            }

            // 4) 交接：Clear 与自绘的顺序/间隔由 mode 决定
            Native.QueryPerformanceCounter(out long t0);
            int overlapPx = -1; // mode 2 的重叠帧像素（-1=无重叠帧）
            if (mode == 0)
            {
                _host.ClearStrokes();
                if (gdiVisible) Native.GdiDrawPolyline(_hwnd, line, 4, 0x000000FF);
                else _host.SelfDryLine(line, 4);
            }
            else
            {
                if (gdiVisible) Native.GdiDrawPolyline(_hwnd, line, 4, 0x000000FF);
                else _host.SelfDryLine(line, 4);
                if (mode == 2)
                {
                    // 重叠交接：泵一帧让自绘先上屏（此帧系统干墨+自绘红线同位置双画），再撤系统干墨
                    Pump(32);
                    if (Native.CaptureClientFrame(_hwnd, out var of)) overlapPx = of.RedCount;
                }
                _host.ClearStrokes();
            }
            Native.QueryPerformanceCounter(out long t1);
            string modeStr = mode == 0 ? "同帧 Clear→自绘" : mode == 1 ? "同帧 自绘→Clear" : "重叠 自绘→泵一帧→Clear";
            Print($"  交接（{modeStr}）：CPU 耗时 {(t1 - t0) * 1000.0 / _qpcFreq:F3} ms"
                + (overlapPx >= 0 ? $"；重叠帧（双画）墨像素 {overlapPx}" : "（两步之间未泵消息）"));

            // 5) 交接后 20 帧
            var after = new List<int>();
            for (int k = 0; k < 20; k++)
            {
                if (Native.CaptureClientFrame(_hwnd, out var fs)) after.Add(fs.RedCount);
                Pump(16);
            }

            Print("  生长 30 帧墨像素：" + JoinInts(grow));
            Print("  交接前 20 帧墨像素：" + JoinInts(before));
            Print("  交接后 20 帧墨像素：" + JoinInts(after));

            // 连续性判定：掉零 + 均值大跳
            int zero = 0;
            foreach (var v in after) if (v <= 0) zero++;
            double bAvg = AvgInts(before), aAvg = AvgInts(after);
            int maxJump = 0;
            var seq = new List<int>(before);
            seq.AddRange(after);
            for (int i = 1; i < seq.Count; i++) maxJump = Math.Max(maxJump, Math.Abs(seq[i] - seq[i - 1]));
            double drift = bAvg > 0 ? (aAvg - bAvg) * 100.0 / bAvg : 100;
            bool pass = zero == 0 && bAvg > 0 && Math.Abs(drift) <= 30;
            Print($"  连续性判定[{label}]：交接后掉零帧 {zero}；像素均值 {bAvg:F0}→{aAvg:F0}（Δ{drift:+0.0;-0.0}%）；相邻帧最大跳变 {maxJump} → {(pass ? "通过（无空白帧）" : "不通过（见序列）")}");

            // 6) 清场（防污染下一轮）
            if (gdiVisible) Native.GdiFillWhite(_hwnd); else _host.ClearSelfDry();
            _host.ClearStrokes();
            Pump(80);
            Print($"  [清场] 自绘已撤、StrokeContainer={_host.StrokeCount()} 条");
        }

        // ---------------- --lag 交互模式（真笔实测跟手） ----------------
        static void RunLagInteractive()
        {
            Print();
            Print("跟手模式（--lag）：在窗口里快速书写，逐采样打印「笔尖 vs 湿墨前缘」间隙；Esc 退出，C 清屏。");
            Print("  口径：间隙px = 最新旁听点（通道2，无则补充通道）到当帧墨迹 bbox 边缘的有符号距离（bbox 内=0）；");
            Print("        间隙ms = 间隙px ÷ 当时笔速(px/ms)，笔速取最近两旁听点 距离÷QPC 差；端到端ms 需注入时刻，真笔拿不到——仅 selftest T5 合成可测。");
            long lastPrint = 0;
            int lastStarting = 0;
            var gGap = new List<double>();
            var gGapMs = new List<double>();
            var gSpeed = new List<double>();
            while (!_quit)
            {
                while (Native.PeekMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
                {
                    if (msg.message == Native.WM_QUIT) { _quit = true; break; }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessageW(ref msg);
                }
                if (_quit) break;

                var st = _stats;
                // 笔画进行中门控：Starting/Started 数 > 结束数（Completed/Canceled），防止收笔后拿冻结末点反复打印
                bool inStroke = (st.WS_Starting + st.SI_Started)
                    > (st.WS_Completed + st.WS_Canceled + st.SI_Ended + st.SI_Canceled);
                if (st.WS_Starting + st.SI_Started > lastStarting)
                {
                    lastStarting = st.WS_Starting + st.SI_Started;
                    gGap.Clear(); gGapMs.Clear(); gSpeed.Clear();
                }
                if (!inStroke)
                {
                    if (gGap.Count > 0) // 收笔落定：打一笔画汇总
                    {
                        Stat(gGap, out double ga, out _, out _, out double gmax);
                        Stat(gGapMs, out double ma, out _, out _, out double mmax);
                        Stat(gSpeed, out double sa, out _, out _, out _);
                        Print($"  [笔画结束] 采样 {gGap.Count}：间隙px 均值 {ga:F1}/最大 {gmax:F1}；间隙ms 均值 {ma:F2}/最大 {mmax:F2}；笔速均值 {sa:F2}px/ms");
                        gGap.Clear(); gGapMs.Clear(); gSpeed.Clear();
                    }
                    Thread.Sleep(8);
                    continue;
                }
                if (Native.CaptureClientFrame(_hwnd, out var fs))
                {
                    var pts = st.WS_Points.Count > 0 ? st.WS_Points : st.SI_Points;
                    if (pts.Count > 0 && fs.HasInk)
                    {
                        var tip = pts[pts.Count - 1];
                        double gap = 0;
                        if (tip.X < fs.MinX) gap = fs.MinX - tip.X;
                        else if (tip.X > fs.MaxX) gap = tip.X - fs.MaxX;
                        if (tip.Y < fs.MinY) gap = Math.Max(gap, fs.MinY - tip.Y);
                        else if (tip.Y > fs.MaxY) gap = Math.Max(gap, tip.Y - fs.MaxY);
                        double speed = 0, gapMs = double.NaN;
                        if (pts.Count >= 2)
                        {
                            var p0 = pts[pts.Count - 2];
                            double dtMs = ((double)tip.Qpc - p0.Qpc) / 10000.0;
                            double dist = Math.Sqrt((tip.X - p0.X) * (tip.X - p0.X) + (tip.Y - p0.Y) * (tip.Y - p0.Y));
                            if (dtMs > 0) speed = dist / dtMs;
                        }
                        if (speed > 0.01) gapMs = gap / speed;
                        gGap.Add(gap);
                        if (speed > 0.01) gSpeed.Add(speed);
                        if (!double.IsNaN(gapMs)) gGapMs.Add(gapMs);

                        long now = Stopwatch.GetTimestamp();
                        if (now - lastPrint > Stopwatch.Frequency / 10 && speed > 0.01)
                        {
                            lastPrint = now;
                            Print($"  [实时] 笔尖({tip.X:F0},{tip.Y:F0}) 墨bbox({fs.MinX},{fs.MinY})-({fs.MaxX},{fs.MaxY}) 间隙 {gap:F1}px / {gapMs:F2}ms（速度 {speed:F2}px/ms）");
                        }
                    }
                }
                Thread.Sleep(8);
            }
        }

        // ---------------- --customdry：自定义干燥（ActivateCustomDrying）逐帧探针 ----------------
        //
        // R1 落地前必须回答的 4 个问题（优化方案 §四 R1），全部用合成笔 + 逐帧截屏取证：
        //   ① ActivateCustomDrying（**受理输入前**调用，官方口径）成不成（Win10 要另机复测）；
        //   ② 旁听通道在自定义干燥下还送不送点（通道1/2/补充 计数）；
        //   ③ 系统是不是"只画湿墨、不画干墨"（收笔后 10 帧像素 + StrokeContainer 累不累积）；
        //   ④ BeginDry/EndDry 帧时序：湿墨在哪一步撤？两种交接顺序哪种不出空白帧（T6 判据）。
        static int RunCustomDryingProbe()
        {
            Print();
            Print("################ 自定义干燥（ActivateCustomDrying）探针 ################");
            Print($"环境：{Environment.OSVersion}，进程 {(Environment.Is64BitProcess ? "x64" : "x86")}，QPC {_qpcFreq}");
            Print($"① dryOrder=1（RootVisual 之后调用＝旧行为）：{(_host.Synchronizer != null ? "成功" : "**失败**（HResult 见上方搭建日志）")}");

            // ①-bis：调用位置矩阵。官方只说"受理输入之前"，没定义 CoreInkPresenterHost 的
            // "前"具体在哪一步（本机 Win11 26200 实测 RootVisual 之后就 E_INVALIDSTATE）。
            // 每个变体一个全新窗口 + 全新宿主；成功就用那个宿主继续后面的取证。
            if (_host.Synchronizer == null)
            {
                Print();
                Print("①-bis 调用位置矩阵（每变体：新窗口＋新宿主）：");
                var variants = new (string Name, int Order)[]
                {
                    ("2=拿到 presenter 立刻（RootVisual 之前，宿主未进合成树）", 2),
                    ("3=InputDeviceTypes=None 之后、RootVisual 之前", 3),
                    ("4=绘制属性设好＋InputDeviceTypes=None 之后", 4),
                    ("5=输入全开 Pen|Mouse|Touch 之后（基线，预期失败）", 5),
                };
                foreach (var (name, order) in variants)
                {
                    bool ok = RecreateHostTryDry(order, out var log);
                    Print($"  dryOrder {name}：{(ok ? "**成功** ★" : "失败")}");
                    foreach (var l in log) Print("      " + l);
                    if (ok) { Print("  → 采用这个调用位置，继续取证"); break; }
                }
            }

            bool dry = _host.Synchronizer != null;
            Print();
            Print(dry
                ? "① 结论：自定义干燥**可用**——R1 具备落地条件，④ 段测帧时序"
                : "① 结论：自定义干燥在此系统**不可用**（所有调用位置均失败）——R1 不落地，维持现有 ClearStrokes 交接；②③ 段数据对现行架构同样有效（旁听/湿墨/系统干墨/迟到入库/容器）");

            Native.SetForegroundWindow(_hwnd);
            Native.BringWindowToTop(_hwnd);
            Pump(120);
            Print("前台校验：" + (Native.GetForegroundWindow() == _hwnd ? "窗口已置前" : "警告：窗口未置前，截图可能被遮挡"));

            // 三笔连写：每笔走"顺序 A（自绘干墨 → 泵一帧 → EndDry）"
            for (int k = 1; k <= 3; k++)
            {
                var st = new BystanderStats();
                SetStats(st);
                _host.ClearStrokes();
                _host.ClearSelfDry();
                Pump(80);
                int y = 120 + k * 90;
                var line = new List<Native.POINT>();
                Print();
                Print(new string('=', 74));
                Print($"第 {k} 笔（y={y}，合成笔 30 步）");
                ProbeStroke(y, 30, line, out var during);
                Print($"② 旁听通道：通道1 {st.UI_Points.Count} 点 / 通道2 {st.WS_Points.Count} 点 / 补充 {st.SI_Points.Count} 点"
                    + $"；宿主 WM_POINTER {st.Wm_PointerDown}/{st.Wm_PointerUpdate}/{st.Wm_PointerUp}（期望 0——输入被宿主收了）");
                Print($"   书写中逐帧墨色像素：{string.Join(",", during)}（湿墨证据）");
                int containerAfter = _host.StrokeCount();

                // ③ 收笔后 ~1.1s 逐帧（45ms×10 + 150ms×6）：系统画不画干墨、第几帧画（迟到入库）
                var after = new List<int>();
                MoveCursorOutside();
                Pump(80);
                for (int i = 0; i < 10; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) after.Add(fs.RedCount);
                    Pump(45);
                }
                for (int i = 0; i < 6; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) after.Add(fs.RedCount);
                    Pump(150);
                }
                Print($"③ 收笔后 16 帧墨色像素（前 10 帧 45ms/帧，后 6 帧 150ms/帧）：{string.Join(",", after)}");
                Print(dry
                    ? "   自定义干燥：**恒定值 = 湿墨挂着等 EndDry**（v2/v3 实测：收笔后湿墨不自动消失）；容器**不碰**（见 ④ 诊断）"
                    : $"   现行架构基线：期望**有墨**（系统画干墨）——看第几帧开始出（迟到入库 10~120ms）；容器 {containerAfter}→{_host.StrokeCount()} 条");

                if (!dry)
                {
                    // 现行交接没有 BeginDry/EndDry 可测：清场进下一笔（②③ 已是有效基线数据）
                    _host.ClearStrokes();
                    Pump(80);
                    continue;
                }

                // ④ BeginDry：湿墨撤不撤（v3：先摸容器状态——v2 在这里读到 -1=异常）
                string sc0 = _host.StrokeCountProbe(out int cnt0);
                string bd = _host.BeginDryProbe(out int nStroke, out int nPts, out double bdMs);
                var postBegin = new List<int>();
                for (int i = 0; i < 2; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) postBegin.Add(fs.RedCount);
                    Pump(16);
                }
                Print($"④ BeginDry：{bd}（第 {k} 笔；条数/点数大于本笔 = 容器在累积/上一笔未清）");
                Print($"   [容器诊断] BeginDry 前 GetStrokes：{(sc0 == "ok" ? $"可读，{cnt0} 条" : "**抛异常** → " + sc0)}");
                Print($"   BeginDry 后 2 帧墨色像素：{string.Join(",", postBegin)}"
                    + $"（掉到 0 = 湿墨在 BeginDry 就撤；仍有值 = 要等 EndDry）");
                // 顺序 A：自绘干墨 → 泵一帧 → EndDry
                _host.SelfDryLine(line, 4);
                Pump(32);
                var preEnd = new List<int>();
                for (int i = 0; i < 3; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) preEnd.Add(fs.RedCount);
                    Pump(16);
                }
                string ed = _host.EndDryProbe(out double edMs);
                var postEnd = new List<int>();
                for (int i = 0; i < 20; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) postEnd.Add(fs.RedCount);
                    Pump(16);
                }
                Print($"   顺序 A【自绘干墨→泵一帧→EndDry】：{ed}（BeginDry {bdMs:F3}ms / EndDry {edMs:F3}ms 为纯调用耗时）");
                Print($"     EndDry 前 3 帧：{string.Join(",", preEnd)}（干墨已上屏 + 湿墨是否还在）");
                Print($"     EndDry 后 20 帧：{string.Join(",", postEnd)}（空白帧=交接闪；恒定为优）");
                // v3：EndDry 之后容器能否读/能否清（决定 R1 里要不要跳过所有 Clear）
                string sc1 = _host.StrokeCountProbe(out int cnt1);
                Print($"   [容器诊断] EndDry 后 GetStrokes：{(sc1 == "ok" ? $"可读，{cnt1} 条" : "**抛异常** → " + sc1)}；Clear：{_host.ClearStrokesProbe()}");
                _host.ClearSelfDry();
                Pump(80);
            }

            // 顺序 B 对照（仅自定义干燥可用时跑）：BeginDry → 泵两帧 → **先 EndDry**（不画自绘干墨）
            // → 看空白窗（证明"EndDry 前必须先画干墨"是必要协议）→ 再自绘补上。
            // 注：v2 的 B 段写错了（EndDry 没配 BeginDry）——那次 E_INVALIDSTATE 恰好证明了
            // "BeginDry/EndDry 必须配对"这条协议，本次按协议重写。
            if (dry)
            {
                var st = new BystanderStats();
                SetStats(st);
                _host.ClearStrokes();
                _host.ClearSelfDry();
                Pump(80);
                int y = 120 + 4 * 90;
                var line = new List<Native.POINT>();
                Print();
                Print(new string('=', 74));
                Print($"第 4 笔（y={y}）——顺序 B【BeginDry → 先 EndDry（不画干墨）→ 再自绘】对照");
                ProbeStroke(y, 30, line, out var during);
                Print($"   书写中逐帧墨色像素：{string.Join(",", during)}");
                MoveCursorOutside();
                Pump(80);
                string bd = _host.BeginDryProbe(out _, out _, out _);
                Pump(32);
                string ed = _host.EndDryProbe(out _);
                var gap = new List<int>();
                for (int i = 0; i < 5; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) gap.Add(fs.RedCount);
                    Pump(16);
                }
                Print($"   {bd} → {ed}；EndDry 后 5 帧（自绘前）：{string.Join(",", gap)}（全 0 = 空白窗——顺序 A 必要性的证据）");
                _host.SelfDryLine(line, 4);
                var late = new List<int>();
                for (int i = 0; i < 15; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) late.Add(fs.RedCount);
                    Pump(16);
                }
                Print($"   自绘后 15 帧：{string.Join(",", late)}");
                _host.ClearSelfDry();
                _host.ClearStrokes();
                Pump(80);
            }

            // 第 5 笔：**隐藏层测试**（Self/Ui 模拟）——湿墨在藏起来的墨迹层上画完，
            // 收尾只做 BeginDry/EndDry，然后把层亮回来：屏幕上不允许有幽灵湿墨。
            // 这决定 R1 里 Self/Ui 收尾是"EndDry 撤湿墨后亮层"还是"需要别的招"。
            if (dry)
            {
                SetStats(new BystanderStats());
                _host.ClearSelfDry();
                Pump(80);
                int y = 560;
                Print();
                Print(new string('=', 74));
                Print($"第 5 笔（y={y}）——隐藏层测试（Self/Ui 模拟：起笔后藏层，收尾 EndDry 后亮层）");
                var s0 = ClientToScreen(200, y);
                var s1 = ClientToScreen(600, y);
                MoveCursorOutside();
                Pump(60);
                if (_probePen == IntPtr.Zero) _probePen = Native.CreatePenDevice();
                Native.PenDown(_probePen, _hwnd, s0.X, s0.Y);
                Pump(60);
                _host.InkLayer.IsVisible = false;    // = Self/Ui 模式的藏层
                for (int i = 1; i <= 30; i++)
                {
                    var p = Lerp(s0, s1, i / 30.0);
                    Native.PenMove(_probePen, _hwnd, p.X, p.Y);
                    Pump(16);
                }
                Native.PenUp(_probePen, _hwnd, s1.X, s1.Y);
                Pump(80);
                string bd5 = _host.BeginDryProbe(out _, out _, out _);
                Pump(32);
                string ed5 = _host.EndDryProbe(out _);
                Pump(50);                            // EndDry 效果落定 2~3 帧
                _host.InkLayer.IsVisible = true;     // 亮层
                var hideFrames = new List<int>();
                for (int i = 0; i < 20; i++)
                {
                    if (Native.CaptureClientFrame(_hwnd, out var fs)) hideFrames.Add(fs.RedCount);
                    Pump(16);
                }
                Print($"   {bd5} → {ed5}（藏层期间不截图）；亮层后 20 帧：{string.Join(",", hideFrames)}");
                Print("   判据：**全 0**（湿墨被 EndDry 撤掉、没有幽灵）；若恒有墨 = EndDry 撤不干净，Self/Ui 要另想办法");
                _host.ClearSelfDry();
                Pump(80);
            }

            Print();
            Print("################ 探针判据 ################");
            Print("① 有一个 dryOrder 成功 = R1 可落地（记住是哪个位置）；全失败 = R1 不落地");
            Print("② 通道2 点数 > 0（旁听在自定义干燥下存活）");
            Print("③ dry：收笔后恒定（湿墨挂到 EndDry；容器不可碰，见 ④ 诊断）；非 dry：有墨＋迟到入库帧位＋容器行为（现行基线）");
            Print("④ dry：顺序 A 的 EndDry 后 20 帧无空白、无掉墨");
            Print("   Win10 教室机必须复跑本探针（一票否决项）");
            return dry ? 0 : 2;
        }

        /// <summary>R1 矩阵：换一个新窗口 + 用指定 dryOrder 建宿主，返回 Synchronizer 拿到没有。</summary>
        static bool RecreateHostTryDry(int dryOrder, out List<string> log)
        {
            log = new List<string>();
            try
            {
                Native.DestroyWindow(_hwnd);
                Pump(80);
                _quit = false;
                if (!CreateMainWindow(out string err)) { log.Add("重建窗口失败：" + err); return false; }
                RegisterRawInput();
                _host = InkHost.Create(_hwnd, _clientW, _clientH, _dpiScale, dryOrder, _stats, out Exception fatal);
                log.AddRange(_host.SetupLog);
                if (fatal != null) { log.Add("重建宿主失败：" + fatal.Message); return false; }
                return _host.Synchronizer != null;
            }
            catch (Exception ex)
            {
                log.Add("重建异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>探针用的**单支**合成笔设备（v3：v2 每笔新建设备、pointerId 都是 101——
        /// 第 2 笔点数翻倍（84/93）疑似多设备撞 id 的注入伪影；复用一支消除变量）。</summary>
        static IntPtr _probePen = IntPtr.Zero;

        /// <summary>探针用：沿 y 注入一条合成笔（steps 步），收集客户区坐标点（供自绘干墨复用）。</summary>
        static void ProbeStroke(int y, int steps, List<Native.POINT> line, out List<int> during)
        {
            during = new List<int>();
            var s0 = ClientToScreen(200, y);
            var s1 = ClientToScreen(600, y);
            MoveCursorOutside();
            Pump(60);
            if (_probePen == IntPtr.Zero) _probePen = Native.CreatePenDevice();
            IntPtr hPen = _probePen;
            Native.PenDown(hPen, _hwnd, s0.X, s0.Y);
            Pump(60);
            for (int i = 1; i <= steps; i++)
            {
                var p = Lerp(s0, s1, i / (double)steps);
                Native.PenMove(hPen, _hwnd, p.X, p.Y);
                line.Add(new Native.POINT { X = p.X - _winOrigin.X, Y = p.Y - _winOrigin.Y });
                Pump(16);
                if (Native.CaptureClientFrame(_hwnd, out var fs)) during.Add(fs.RedCount);
            }
            Native.PenUp(hPen, _hwnd, s1.X, s1.Y);
            Pump(80);
        }

        // ---------------- 小工具 ----------------
        static void Stat(List<double> v, out double avg, out double med, out double min, out double max)
        {
            avg = med = min = max = double.NaN;
            if (v == null || v.Count == 0) return;
            var a = v.ToArray();
            Array.Sort(a);
            double s = 0;
            foreach (var x in a) s += x;
            avg = s / a.Length;
            med = a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
            min = a[0]; max = a[a.Length - 1];
        }

        static string JoinInts(List<int> v)
        {
            var sb = new StringBuilder();
            foreach (var x in v) sb.Append(x).Append(",");
            return sb.ToString();
        }

        static double AvgInts(List<int> v)
        {
            if (v.Count == 0) return 0;
            double s = 0;
            foreach (var x in v) s += x;
            return s / v.Count;
        }

        // ---------------- 交互模式 ----------------
        static void RunInteractive()
        {
            Print();
            Print("交互模式：用真笔/鼠标/触摸在窗口里写（真笔才能验 raw HID 旁听与压感）；Esc 退出，C 清屏。");
            long last = Stopwatch.GetTimestamp();
            while (!_quit)
            {
                while (Native.PeekMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
                {
                    if (msg.message == Native.WM_QUIT) { _quit = true; break; }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessageW(ref msg);
                }
                if (_quit) break;
                long now = Stopwatch.GetTimestamp();
                if (now - last > Stopwatch.Frequency)
                {
                    last = now;
                    var st = _stats;
                    if (st.UI_Pressed + st.UI_Moved + st.WS_Starting + st.WS_Continuing + st.SI_Started + st.Raw_Mouse + st.Raw_Hid
                        + st.Wm_PointerDown + st.Wm_LButtonDown + st.StrokesCollectedEvents > 0)
                    {
                        Print($"[累计] 通道1 {st.UI_Pressed}/{st.UI_Moved}/{st.UI_Released}；通道2 {st.WS_Starting}/{st.WS_Continuing}/{st.WS_Stopping}（点 {st.WS_Points.Count}）；"
                            + $"补充 {st.SI_Started}/{st.SI_Continued}/{st.SI_Ended}；raw 鼠标 {st.Raw_Mouse}/HID {st.Raw_Hid}({st.Raw_HidPackets})；"
                            + $"WM_POINTER {st.Wm_PointerDown}/{st.Wm_PointerUpdate}/{st.Wm_PointerUp}；WM_LBUTTONDOWN {st.Wm_LButtonDown}；笔画 {st.StrokesCollectedEvents} 次");
                    }
                }
                Thread.Sleep(50);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    }
}
