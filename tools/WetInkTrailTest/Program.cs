using System.Runtime.InteropServices;
using Vortice.Mathematics;

namespace WetInkTrailTest;

/// <summary>
/// 湿墨迹 → DComp 最小验证程序。技术栈对齐 InkTeach（Vortice + Direct2D + DirectComposition），
/// 但**其余全部是自己的**：文档模型（笔画列表）、自绘湿墨、干墨都自己画，只把"正在写的那一小段"
/// 喂给系统合成器（委托墨迹轨迹）。
///
/// 用法：
///   dotnet run -c Release                 交互模式（鼠标/笔/触摸画线，Esc 退出）
///   dotnet run -c Release -- --selftest   自动验证（直接驱动笔画 + 逐帧截屏数像素，出 PASS/FAIL）
///   dotnet run -c Release -- --selftest --trailonly
///        自检只走系统轨迹（自绘关）——屏幕上出现的任何墨都只可能是系统画的，证据最干净
///
/// 验证判据（照 InkTeach --trailtest 的口径）：拖动中逐帧截屏数墨色像素，
/// 只要有一个采样帧峰值 &gt; 0，就证明系统合成器在画；0 = 通道没通。
/// 采样密度必须 ≤1 帧——这种"只活一两帧"的尾巴隔几步采会量出 0（InkTeach 实测踩过）。
/// </summary>
internal static class Program
{
    private const string ClassName = "WetInkTrailTest.Window";
    private static readonly Native.WndProcDelegate WndProcRef = WndProc;

    private static Scene _scene;
    private static IntPtr _hwnd;
    private static bool _selfTest;
    private static bool _trailOnly;
    private static int _cx, _cy;   // 客户区尺寸（物理像素）

    // 输入诊断（交互模式排障用）
    private static int _ptrMsgs, _mouseMsgs;

    private static readonly Color4 InkColor = new(0.95f, 0.95f, 0.95f, 1f); // 浅墨迹 / 深背景，截屏好分辨
    private const float InkRadius = 3f;

    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));   // 每监视器 v2：物理像素同口径

        _selfTest = Array.Exists(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        _trailOnly = Array.Exists(args, a => a.Equals("--trailonly", StringComparison.OrdinalIgnoreCase));

        var hInstance = Marshal.GetHINSTANCE(typeof(Program).Module);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = Native.CS_HREDRAW | Native.CS_VREDRAW,
            lpfnWndProc = WndProcRef,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        if (Native.RegisterClassEx(ref wc) == 0)
        {
            Console.WriteLine("RegisterClassEx 失败");
            return 1;
        }

        // 无边框置顶窗（InkTeach 同款窗口样式）；测试窗口给固定 1280x800，截屏好定位。
        _cx = 1280;
        _cy = 800;
        int sx = Math.Max(0, (GetSystemMetrics(0) - _cx) / 2);
        int sy = Math.Max(0, (GetSystemMetrics(1) - _cy) / 2);
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOREDIRECTIONBITMAP;
        _hwnd = Native.CreateWindowEx((int)exStyle, ClassName, "WetInkTrailTest",
            Native.WS_POPUP, sx, sy, _cx, _cy,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Console.WriteLine("CreateWindowEx 失败: " + Marshal.GetLastWin32Error());
            return 1;
        }
        Native.SetWindowPos(_hwnd, new IntPtr(Native.HWND_TOPMOST), sx, sy, _cx, _cy,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        _scene = new Scene();
        string note = _scene.Init(_hwnd, _cx, _cy);
        Console.WriteLine("系统: " + Environment.OSVersion.Version);
        Console.WriteLine(note);
        Console.WriteLine(_selfTest
            ? "模式: 自检" + (_trailOnly ? "（只走系统轨迹，自绘关）" : "（自绘 + 系统轨迹）")
            : "模式: 交互（画线，Esc 退出）");

        if (!_scene.Trail.Available)
        {
            // 通道不存在（Win10 / 老 Win11）也是一个明确结论，不算测试失败。
            Console.WriteLine();
            Console.WriteLine("结论: 本机没有委托墨迹轨迹通道（系统不支持）——湿墨只能自绘。");
            return _selfTest ? 2 : 0;
        }

        int exit = _selfTest ? RunSelfTest() : RunInteractive();
        _scene.Dispose();
        return exit;
    }

    // ---- 交互模式 -------------------------------------------------------

    private static int RunInteractive()
    {
        _scene.SelfDrawWet = !_trailOnly;
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        Console.WriteLine($"[轨迹] 喂 {_scene.Trail.PointsFed} 结账 {_scene.Trail.PointsSettled}");
        Console.WriteLine($"[输入] WM_POINTER {_ptrMsgs} 条 / 鼠标消息 {_mouseMsgs} 条");
        return 0;
    }

    // ---- 自检：逐帧采样 + 基线差分 ---------------------------------------

    /// <summary>
    /// 自检直接调 Scene 起笔/加点/收笔，不走输入消息（输入管线是"其余"里
    /// 最不该成为干扰变量的部分）。全程自绘关（trailonly），屏幕上新出现的墨
    /// 只可能是系统合成器画的。
    ///
    /// ⚠ 采样密度必须 ≤1 帧（InkTeach 实测教训：隔几步采会只赶上"没尾巴"的瞬间，
    /// 量出 0 误判系统没画）。所以这里**边喂边逐帧采样**，统计出墨帧数与峰值，
    /// 并打印时间分布——轨迹若只活一两帧，分布会直接显示出来。
    ///
    /// 两种喂法各跑两遍（看稳定性）：
    ///   A. 喂点期间不 Present —— 轨迹独立存活吗
    ///   B. 喂点期间每 ~16ms Present —— 贴近真实应用节奏
    /// </summary>
    private static int RunSelfTest()
    {
        _scene.SelfDrawWet = !_trailOnly;
        Native.GetWindowRect(_hwnd, out var rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;

        Native.SetCursorPos(rc.Right + 300, rc.Top + 50);   // 光标出窗
        _scene.Clear();
        _scene.RenderFrame();
        Pump(20);

        float x0 = w / 4f, x1 = w * 3f / 4f;
        bool anyInk = false;

        void Run(string name, int rowY, bool presentDuring, int steps)
        {
            // 清掉上一轮的干墨：RenderFrame 会把干墨画进交换链，不清就污染差分。
            _scene.Clear();
            _scene.RenderFrame();
            Pump(10);
            _scene.BeginStroke(x0, rowY, InkRadius, InkColor);
            byte[] baseFrame = CaptureWindow(rc, w, h);

            int framesWithInk = 0, peak = 0, bMinX = int.MaxValue, bMaxX = -1, bMinY = int.MaxValue, bMaxY = -1;
            var timeline = new List<int>();
            for (int i = 1; i <= steps; i++)
            {
                _scene.AddPoint(x0 + (x1 - x0) * i / steps, rowY, InkRadius);
                Pump(2);                                   // ≈2-4ms：≤1 帧
                if (presentDuring && i % 5 == 0) _scene.RenderFrame();
                byte[] now = CaptureWindow(rc, w, h);
                int count = DiffInk(baseFrame, now, w, out int minX, out int maxX, out int minY, out int maxY);
                timeline.Add(count);
                if (count > 0)
                {
                    framesWithInk++;
                    if (minX < bMinX) bMinX = minX;
                    if (maxX > bMaxX) bMaxX = maxX;
                    if (minY < bMinY) bMinY = minY;
                    if (maxY > bMaxY) bMaxY = maxY;
                }
                if (count > peak) peak = count;
            }
            _scene.EndStroke();

            string dist = string.Join(",", timeline);
            string box = framesWithInk > 0
                ? $"bbox x[{bMinX}..{bMaxX}] y[{bMinY}..{bMaxY}]（喂点行 y={rowY}）"
                : "无";
            Console.WriteLine($"{name}: 出墨 {framesWithInk}/{steps} 帧, 峰值 {peak}, {box}");
            Console.WriteLine($"    分布: {dist}");
            if (peak > 0) anyInk = true;
        }

        Console.WriteLine();
        Run("A1 不Present", h / 6, presentDuring: false, steps: 30);
        Run("A2 不Present", h / 3, presentDuring: false, steps: 30);
        Run("B1 期间Present", h / 2, presentDuring: true, steps: 30);
        Run("B2 期间Present", h * 2 / 3, presentDuring: true, steps: 30);

        _scene.Clear();
        _scene.RenderFrame();

        Console.WriteLine();
        Console.WriteLine($"[轨迹] 喂 {_scene.Trail.PointsFed} 结账 {_scene.Trail.PointsSettled}");
        Console.WriteLine();

        // 判定：任一阶段抓到新墨 = 系统在画。
        bool systemDraws = anyInk;
        Console.WriteLine(systemDraws
            ? (_trailOnly
                ? "PASS: 自绘关着，逐帧采样抓到系统合成器画的墨——湿墨迹确实可以给 DComp。"
                : "PASS: 通道通了（自绘开着，A 组不 Present 帧仍出墨 = 系统轨迹独立在画）。加 --trailonly 可做纯系统取证。")
            : "FAIL: 逐帧采样仍全程 0——系统没画。检查：轨迹视觉是否挂进视觉树且在交换链之上；OS 是否 Win11 24H2+。");
        return systemDraws ? 0 : 1;
    }

    /// <summary>抓窗口一帧（BGRA，顶向下，行连续）。</summary>
    private static byte[] CaptureWindow(Native.RECT rc, int w, int h)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = Native.SelectObject(memDc, bmp);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, rc.Left, rc.Top, Native.SRCCOPY);

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        var bits = new byte[w * h * 4];
        Native.GetDIBits(memDc, bmp, 0, (uint)h, bits, ref bmi, Native.DIB_RGB_COLORS);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(bmp);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return bits;
    }

    /// <summary>差分两帧：数"由非墨转墨"的像素，并给出包围盒。</summary>
    private static int DiffInk(byte[] before, byte[] after, int w, out int minX, out int maxX, out int minY, out int maxY)
    {
        minX = int.MaxValue; maxX = -1; minY = int.MaxValue; maxY = -1;
        int count = 0;
        for (int i = 0; i < after.Length; i += 4)
        {
            bool inkNow = after[i + 2] > 150 && after[i + 1] > 150 && after[i] > 150;
            bool inkBefore = before[i + 2] > 150 && before[i + 1] > 150 && before[i] > 150;
            if (inkNow && !inkBefore)
            {
                count++;
                int px = (i / 4) % w, py = (i / 4) / w;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }
        }
        return count;
    }

    // ---- 输入（笔走 WM_POINTER；鼠标兜底走经典消息） ---------------------

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_POINTERDOWN:
            {
                _ptrMsgs++;
                var id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (Native.GetPointerInfo(id, out var pi))
                {
                    var pt = new Native.POINT { X = pi.ptPixelLocation.X, Y = pi.ptPixelLocation.Y };
                    Native.ScreenToClient(hWnd, ref pt);
                    Native.SetCapture(hWnd);
                    _scene.BeginStroke(pt.X, pt.Y, InkRadius, InkColor);
                    _scene.RenderFrame();
                }
                return IntPtr.Zero;
            }
            case Native.WM_POINTERUPDATE:
            {
                _ptrMsgs++;
                if (_scene.WetStroke == null) return IntPtr.Zero;
                var id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (Native.GetPointerInfo(id, out var pi))
                {
                    var pt = new Native.POINT { X = pi.ptPixelLocation.X, Y = pi.ptPixelLocation.Y };
                    Native.ScreenToClient(hWnd, ref pt);
                    _scene.AddPoint(pt.X, pt.Y, InkRadius);
                    _scene.RenderFrame();
                }
                return IntPtr.Zero;
            }
            case Native.WM_POINTERUP:
            case Native.WM_POINTERCAPTURECHANGED:
            {
                _ptrMsgs++;
                Native.ReleaseCapture();
                if (_scene.WetStroke != null)
                {
                    _scene.EndStroke();
                    _scene.RenderFrame();
                }
                return IntPtr.Zero;
            }

            // 鼠标兜底（没开鼠标指针消息的环境也能画）
            case Native.WM_LBUTTONDOWN:
            {
                _mouseMsgs++;
                var pt = LParamPoint(lParam);
                Native.SetCapture(hWnd);
                _scene.BeginStroke(pt.X, pt.Y, InkRadius, InkColor);
                _scene.RenderFrame();
                return IntPtr.Zero;
            }
            case Native.WM_MOUSEMOVE:
            {
                _mouseMsgs++;
                if (_scene.WetStroke == null) return IntPtr.Zero;
                var pt = LParamPoint(lParam);
                _scene.AddPoint(pt.X, pt.Y, InkRadius);
                _scene.RenderFrame();
                return IntPtr.Zero;
            }
            case Native.WM_LBUTTONUP:
            {
                _mouseMsgs++;
                Native.ReleaseCapture();
                if (_scene.WetStroke != null)
                {
                    _scene.EndStroke();
                    _scene.RenderFrame();
                }
                return IntPtr.Zero;
            }

            case Native.WM_KEYDOWN:
                if (wParam.ToInt32() == Native.VK_ESCAPE)
                    PostMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static Native.POINT LParamPoint(IntPtr lParam)
    {
        int v = lParam.ToInt32();
        return new Native.POINT { X = (short)(v & 0xFFFF), Y = (short)((v >> 16) & 0xFFFF) };
    }

    // ---- 小工具 ---------------------------------------------------------

    private static void Pump(int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                if (msg.message == Native.WM_QUIT) return;
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Thread.Sleep(1);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);   // 0=SM_CXSCREEN 1=SM_CYSCREEN

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
