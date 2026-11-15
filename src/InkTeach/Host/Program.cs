using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal static class ScreenProbe
{
    /// <summary>Grabs the whole virtual desktop into a 32-bit BMP. Used for
    /// eyeballing what another app actually looks like while it is measured.</summary>
    public static bool SaveBmp(string path, int x, int y, int w, int h)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr bits = IntPtr.Zero;
        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);

        var pixels = new byte[w * h * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return WriteBmp(path, pixels, w, h);
    }

    /// <summary>把 BGRA 像素写成 32 位 BMP 文件。</summary>
    private static bool WriteBmp(string path, byte[] pixels, int w, int h)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            int imageBytes = w * h * 4;
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            // BITMAPFILEHEADER
            bw.Write((ushort)0x4D42);          // 'BM'
            bw.Write(14 + 40 + imageBytes);    // 文件大小
            bw.Write(0);
            bw.Write(14 + 40);                 // 像素数据偏移

            // BITMAPINFOHEADER
            bw.Write(40);
            bw.Write(w);
            bw.Write(h);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(0);                       // BI_RGB
            bw.Write(imageBytes);
            bw.Write(2835);                    // 分辨率
            bw.Write(2835);
            bw.Write(0);
            bw.Write(0);

            // BMP 是自下而上存储的，抓到的像素是自上而下的，逐行倒序写。
            for (int row = h - 1; row >= 0; row--)
                bw.Write(pixels, row * w * 4, w * 4);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int CountNear(int x, int y, int w, int h, int r, int g, int b, int tol)
    {
        var box = new Box(r, g, b, tol);
        return Capture(x, y, w, h, (bb, gg, rr) =>
            Math.Abs(rr - box.R) <= box.Tol
            && Math.Abs(gg - box.G) <= box.Tol
            && Math.Abs(bb - box.B) <= box.Tol);
    }

    private readonly struct Box
    {
        public readonly int R, G, B, Tol;
        public Box(int r, int g, int b, int tol) { R = r; G = g; B = b; Tol = tol; }
    }

    public static int CountMagenta(int x, int y, int w, int h) => Capture(x, y, w, h, IsMagenta);
    public static int CountGreen(int x, int y, int w, int h) => Capture(x, y, w, h, IsGreen);
    public static int CountRed(int x, int y, int w, int h) => Capture(x, y, w, h, IsRed);
    public static int CountCursor(int x, int y, int w, int h) => Capture(x, y, w, h, IsCursor);

    /// <summary>深色像素数（找"深色胶囊"那种东西：截图时的尺寸读数）。</summary>
    public static int CountDark(int x, int y, int w, int h) => Capture(x, y, w, h, IsDark);
    private static bool IsDark(byte b, byte g, byte r) => r < 90 && g < 90 && b < 90;

    /// <summary>
    /// 一块区域的**平均亮度**（0..255，BT.601 权重）。给"遮罩压暗没压暗"这类判据用——
    /// 数像素（Count*）分不清"整块都暗了一点"和"少数像素很暗"。
    /// </summary>
    public static double AvgLuma(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return 0;
        var buf = ArrayPool<byte>.Shared.Rent(w * h * 4);
        try
        {
            if (!CaptureRegionInto(buf, x, y, w, h)) return 0;
            double sum = 0; int n = 0;
            for (int i = 0; i + 3 < w * h * 4; i += 4)
            { sum += 0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2]; n++; }
            return n == 0 ? 0 : sum / n;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // 判定用色。像素是 BGRA 顺序，所以参数名按 (b, g, r)。
    private static bool IsMagenta(byte b, byte g, byte r) => b > 200 && g < 90 && r > 200;
    private static bool IsGreen(byte b, byte g, byte r) => g > 180 && r < 90 && b < 90;
    private static bool IsRed(byte b, byte g, byte r) => r > 170 && g < 110 && b < 110;
    private static bool IsCursor(byte b, byte g, byte r)
        => r > 180 && g > 70 && g < 190 && b > 70 && b < 190;

    /// <summary>抓一块区域的原始 BGRA 像素，用于前后差分。</summary>
    public static byte[] CaptureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return Array.Empty<byte>();
        var buf = new byte[w * h * 4];
        return CaptureRegionInto(buf, x, y, w, h) ? buf : Array.Empty<byte>();
    }

    /// <summary>把一份 BGRA 缓冲原样写成 32 位 BMP（**不经屏幕**：抓过的那一帧要能留档）。</summary>
    public static bool SaveBuffer(string path, byte[] bgra, int w, int h) => WriteBmp(path, bgra, w, h);

    /// <summary>同 <see cref="CaptureRegion"/>，但把像素写进调用方给的缓冲。**逐帧抓屏的测量必须用它**：
    /// 每帧 new 一个 1400×360×4 ≈ 2MB 的数组会直接进 LOH，实测能把**一次笔画**刷出
    /// 64 次第 2 代 GC / 378 MB 分配——那量到的是测量工具自己，不是产品。
    /// （2026-09-28 拿 `--wetdrytest` 踩的，记在这里免得再犯。）
    /// </summary>
    public static bool CaptureRegionInto(byte[] dst, int x, int y, int w, int h)
    {
        if (dst == null || w <= 0 || h <= 0 || dst.Length < w * h * 4) return false;
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bits = IntPtr.Zero;

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            }
        };

        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);
        Marshal.Copy(bits, dst, 0, w * h * 4);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return true;
    }

    /// <summary>两张同尺寸截图的逐像素差异数（容忍轻微抗锯齿抖动）。</summary>
    public static int DiffCount(byte[] a, byte[] b, int tolerance = 12)
    {
        if (a == null || b == null || a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int dr = Math.Abs(a[i] - b[i]);
            int dg = Math.Abs(a[i + 1] - b[i + 1]);
            int db = Math.Abs(a[i + 2] - b[i + 2]);
            if (dr > tolerance || dg > tolerance || db > tolerance) n++;
        }
        return n;
    }

    /// <summary>Grabs a screen region with GDI and counts pixels of one colour.
    /// This is how the prototype proves that what the GPU drew really landed on
    /// the display, instead of trusting that the code "should" work.</summary>
    public static bool TryFindMagenta(int x, int y, int w, int h, out int hits)
    {
        hits = Capture(x, y, w, h, IsMagenta);
        return hits > 500;
    }

    private static int Capture(int x, int y, int w, int h, Func<byte, byte, byte, bool> match)
    {
        int hits = 0;
        if (w <= 0 || h <= 0) return 0;
        int bytes = w * h * 4;
        var buffer = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            if (!CaptureRegionInto(buffer, x, y, w, h)) return 0;
            for (int i = 0; i + 3 < bytes; i += 4)
            {
                // Buffer is BGRA order.
                if (match(buffer[i], buffer[i + 1], buffer[i + 2])) hits++;
            }
            return hits;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

internal static class Program
{
    /// <summary>
    /// 单实例用的命名互斥体。**必须是静态字段**：句柄一释放，"已经在跑"这个判断就没了。
    /// 进程退出时由系统自动放弃（不需要手动 ReleaseMutex）。
    /// </summary>
    private static System.Threading.Mutex s_single;

    [STAThread]
    private static int Main(string[] args)
    {
        // **WinExe 默认不带控制台**（双击时不该弹黑框），但从命令行启动时那些
        // `Console.WriteLine` 不能一起丢掉。**无条件**做一次输出设置（双击时没有父
        // 控制台，这里失败即静默跳过，正好安静启动）。
        // 2026-10-17 用户报"控制台乱码"的真正病根就在这一类：
        // **GUI 程序从控制台启动时"继承了一个控制台句柄、但自己没挂控制台"**——
        // .NET 就把 stdout 当普通输出流按 UTF-8 写，控制台按 936 显示 → 全乱。
        // 第一版修复只在"带参数"时才做（`if (args.Length > 0)`），而用户直接
        // 从 PowerShell 跑无参数的 exe，根本走不到 → 还是乱。见 SetupConsoleOutput。
        SetupConsoleOutput();

        // **L1 纯逻辑测试**（`--unittest [过滤词]`）：不建窗口、不装日志、不抢单实例。
        // 放在 DiagLog.Install 之前是故意的：测试绝不往用户日志目录写东西；
        // 隔离由 UnitTestRunner.ApplyIsolation 负责（临时 settings/板书/历史）。
        if (args.Length > 0 && args[0] == "--unittest")
            return UnitTestRunner.Run(args);

        // **运行日志**（2026-10-16 用户定）：接住全仓的 Console 诊断输出、落
        // `%LOCALAPPDATA%\InkTeach\Logs`（限流 + 轮转，绝不拖慢书写）。
        // ⚠ 装在这里是关键：必须在"接父进程控制台"**之后**——tee 要包住最终的 stdout，
        //   命令行 / 自检输出才会原样照打；装早了会把刚接上的控制台盖掉。
        DiagLog.Install();

        // **单实例**：这个程序是一层全屏置顶的覆盖层。开两个的后果不是"多一个窗口"，
        // 而是：两层叠在一起（写字落到哪一层看运气）、第二个实例的热键注册不上、
        // **PPT 联动两边都在做**（互相抢着翻页、抢着往同一个文件写批注）。
        //
        // ⚠ 自检里的 `--clicktarget` 是**故意再起一个进程**当"下层窗口"用的
        //   （`--passtest` / `--uitest` 都靠它），那不是产品实例、也不能被拦——
        //   拦了那两条用例就没法跑了。所以它单独放行。
        //
        // **整段都放进 try 里**：单实例判断曾经写在 try 外面，它一旦抛异常（见
        // AcquireSingleInstance 里 abandoned 那段）就绕过了下面的 ReportFatal，
        // GUI 版（没有控制台）直接静默消失。
        try
        {
            bool isHelper = Array.IndexOf(args, "--clicktarget") >= 0
                         || Array.IndexOf(args, "--help") >= 0;   // --help 随时能打（不抢单实例）
            if (!isHelper && !AcquireSingleInstance()) return 0;
            return new App().Run(args);
        }
        catch (Exception ex)
        {
            // 没有控制台时，"FATAL: ..."这句话等于没写——落到盘上 + 弹一句，
            // 至少让用户知道"它出错了、详情在哪"（从命令行跑时控制台也照旧有）。
            Console.WriteLine("FATAL: " + ex);
            ReportFatal(ex);
            return 1;
        }
    }

    /// <summary>
    /// 把控制台输出接对编码（见 Main 里那一段）。两条路，按"手里有没有控制台句柄"分：
    ///
    /// ① **stdout 本来就指着控制台**（从 cmd / PowerShell 启动 GUI 程序时，句柄是继承的）：
    ///    直接用 `WriteConsoleW` 写**宽字符**——与控制台代码页（936 / 65001）无关，永远不乱。
    ///    ⚠ 这条路**故意不 AttachConsole**：附着上以后，关掉那个控制台窗口会把 InkTeach
    ///    也一起带走（CTRL_CLOSE 事件）；写宽字符不需要附着，句柄就够。
    ///
    /// ② 没有可用的控制台句柄（例如 `start` 起的、或带参数从别处拉起需要接父控制台）：
    ///    才试 `AttachConsole`，然后按**控制台实际代码页**编码写（936 → GBK、65001 → UTF-8）。
    /// </summary>
    private static void SetupConsoleOutput()
    {
        try
        {
            // ① 输出句柄就是控制台 → 宽字符直写
            IntPtr hOut = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
            if (hOut != IntPtr.Zero && hOut != new IntPtr(-1) && Native.GetConsoleMode(hOut, out _))
            {
                Console.SetOut(new ConsoleWideWriter(hOut));

                IntPtr hErr = Native.GetStdHandle(Native.STD_ERROR_HANDLE);
                if (hErr != IntPtr.Zero && hErr != new IntPtr(-1) && Native.GetConsoleMode(hErr, out _))
                    Console.SetError(new ConsoleWideWriter(hErr));
                return;
            }

            // ② 没有控制台句柄：接父控制台，按它的实际代码页写
            if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return;
            System.Text.Encoding enc = System.Text.Encoding.UTF8;
            try
            {
                uint cp = Native.GetConsoleOutputCP();
                if (cp != 0 && cp != 65001)
                {
                    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                    enc = System.Text.Encoding.GetEncoding((int)cp);
                }
            }
            catch { /* 有意忽略：日志失败不能再抛（ADR-005 D5） */ }

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), enc) { AutoFlush = true });
        }
        catch { /* 接不上就算了：不影响功能，只是看不到日志 */ }
    }

    /// <summary>
    /// 用 `WriteConsoleW` 直写控制台的 TextWriter：**宽字符、与代码页无关**。
    /// 为什么需要它：.NET 在"进程没有挂控制台、但 stdout 句柄恰是控制台"时会把输出
    /// 当普通流按 UTF-8 写 → 中文 Windows 的 936 控制台看到的就是乱码（2026-10-17）。
    /// </summary>
    private sealed class ConsoleWideWriter : TextWriter
    {
        private readonly IntPtr _h;
        private readonly char[] _buf = new char[2048];
        private readonly object _lk = new();
        private int _n;

        public ConsoleWideWriter(IntPtr h) { _h = h; }

        public override System.Text.Encoding Encoding => System.Text.Encoding.Unicode;

        public override void Write(char c)
        {
            lock (_lk)
            {
                if (_n >= _buf.Length) FlushLocked();
                _buf[_n++] = c;
            }
        }

        public override void Write(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            lock (_lk)
            {
                foreach (char c in s)
                {
                    if (_n >= _buf.Length) FlushLocked();
                    _buf[_n++] = c;
                }
                FlushLocked();          // 控制台输出本来就要即时可见，攒着没意义
            }
        }

        public override void WriteLine(string s)
        {
            Write(s);
            Write('\n');
            Flush();
        }

        public override void WriteLine()
        {
            Write('\n');
            Flush();
        }

        public override void Flush()
        {
            lock (_lk) FlushLocked();
        }

        private void FlushLocked()
        {
            if (_n <= 0) return;
            try { Native.WriteConsoleW(_h, _buf, (uint)_n, out _, IntPtr.Zero); } catch { /* 有意忽略：日志失败不能再抛（ADR-005 D5） */ }
            _n = 0;
        }
    }

    /// <summary>
    /// 抢"唯一实例"。已经在跑 → 把那个实例的覆盖层窗口提到前面，返回 false。
    /// 用 `Local\` 前缀：**每个登录会话一个**（同一台机器两个用户各开一个互不打扰）。
    /// </summary>
    private static bool AcquireSingleInstance()
    {
        System.Threading.Mutex mutex;
        try
        {
            // 自检专用：允许开发时用临时互斥体后缀和正在运行的产品实例并存（不设 = 正常单实例）。
            string suffix = Environment.GetEnvironmentVariable("INKTEACH_MUTEX_SUFFIX");
            string mutexName = @"Local\InkTeach.SingleInstance"
                               + (string.IsNullOrEmpty(suffix) ? "" : "." + suffix);
            mutex = new System.Threading.Mutex(true, mutexName, out bool first);
            if (first) { s_single = mutex; return true; }
        }
        catch (Exception ex)
        {
            // 互斥体本身建不出来（极少见）不该把软件拦死：按"没有别的实例"继续。
            Console.WriteLine("单实例互斥体创建失败，按“没有别的实例”继续：" + ex.Message);
            return true;
        }

        // 抢不到时**先等一小会儿再判**——"界面上的重启"那条路是"先起新进程、
        // 老进程随后才退"（见 Engine.RestartSelf：它 `_quit = true` 之后才走 Shutdown）。
        // 不等的话新进程一上来就看见互斥体还在老进程手里，直接退出——
        // 表现是"点了重启，软件没了"，而这是这类单实例保护最常见的坑。
        // 2 秒（每 50ms 试一次）对"老进程正在退"足够了；真的是"用户又双击了一次"，
        // 也就晚 2 秒看到那句提示，不影响什么。
        for (int i = 0; i < 40; i++)
        {
            try
            {
                if (mutex.WaitOne(50)) { s_single = mutex; return true; }
            }
            catch (System.Threading.AbandonedMutexException)
            {
                // **这一条就是"点了重启不回来"的真凶**（2026-09-28 修）：
                // 老进程退出时还攥着这个互斥体，内核把互斥体标记成 "abandoned"，
                // 于是这里的 WaitOne 不是返回 false，而是**抛 AbandonedMutexException**。
                // 抛异常 ≠ 没抢到：按 MSDN，异常抛出时等待已经满足、所有权已经转过来了
                // （"the wait is satisfied"）——所以当成"抢到了"继续启动才是对的。
                //
                // 之前没接这个异常：它一路冒到 Main 外面（那时单实例判断写在 try 之外），
                // GUI 版没有控制台、也没有 crash 日志，表现就是"老窗口关了、新窗口再也不出来"。
                s_single = mutex;
                return true;
            }
            catch (Exception ex)
            {
                // 其它意外也别把软件拦死（比如权限/句柄问题）。
                Console.WriteLine("单实例判断出错，按“没有别的实例”继续：" + ex.Message);
                return true;
            }
        }

        mutex.Dispose();
        Console.WriteLine("已经有 InkTeach 在运行了（单实例保护）：把它的窗口提到前面，这一个退出。");
        BringExistingToFront();
        return false;
    }

    /// <summary>
    /// 找到已经在跑的那个实例的窗口，把它激活。找不到就提示一句
    /// （覆盖层是透明的、还可能被贴边隐藏收成底部一条，用户多半以为"双击没反应"）。
    ///
    /// **认窗口靠"窗口类名前缀"，不靠标题**：实测从别的进程读这几个窗口的标题，
    /// 拿到的是 `"I"`（建窗口时那个名字被截成了一个字符，原因没深究——
    /// 那几个窗口都是 WS_EX_TOOLWINDOW，不进 Alt+Tab，标题本来也没人看），
    /// 而**类名读出来是完整的** `InkTeachOverlay_<guid>`（见 OverlayWindow 注册窗口类）。
    /// 同一批里还有两个"接输入小窗"（`Engine` 里建的界面面板 / PPT 条），
    /// 它们不算"实例的窗口"，所以按**面积挑最大的那张**——覆盖层是整块屏幕那块。
    /// </summary>
    private static void BringExistingToFront()
    {
        IntPtr found = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            var sb = new System.Text.StringBuilder(128);
            if (Native.GetClassNameW(h, sb, sb.Capacity) <= 0) return true;
            if (!sb.ToString().StartsWith("InkTeachOverlay_", StringComparison.Ordinal)) return true;

            if (!Native.GetWindowRect(h, out var r)) return true;
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; found = h; }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            // 先叫醒已有实例：后台待命是“穿透 + 小球收起”，只 SetForegroundWindow
            // 的话用户还是点不到工具条，看起来就像双击没反应。
            Native.GetWindowThreadProcessId(found, out uint foundPid);
            // 同步发：第二个进程马上要退出，PostMessage 留在队列里理论上也能到，
            // 但实测在锁屏/消息泵正忙时不可靠；SendMessage 会等对方 WndProc 处理完。
            Native.SendMessage(found, InkEngine.Native.WM_INKTEACH_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
            Console.WriteLine($"单实例：已叫醒运行中的窗口（pid {foundPid}）");
            Native.SetForegroundWindow(found);
            return;
        }
        Native.MessageBoxW(IntPtr.Zero,
            "InkTeach 已经在运行了。\n\n它是一层透明的覆盖层，看屏幕底部中间那条工具条。",
            "InkTeach", Native.MB_OK);
    }

    /// <summary>崩溃时把详情写到盘上并弹一句（WinExe 双击启动时没有控制台可看）。</summary>
    private static void ReportFatal(Exception ex)
    {
        string path = "";
        try
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InkTeach", "last-crash.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n");
        }
        catch { /* 有意忽略：清理/删除失败不影响主流程（ADR-005 D5） */ }

        Native.MessageBoxW(IntPtr.Zero,
            "InkTeach 启动失败，已经退出。\n\n详情：" + (path.Length > 0 ? path : "（写日志也失败了）"),
            "InkTeach", Native.MB_OK | Native.MB_ICONERROR);
    }
}
