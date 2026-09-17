using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace InkEngine;

/// <summary>
/// **最小 PNG 编码器**（零依赖，约百来行）。
///
/// 为什么自己写：引擎到现在**一个第三方依赖都没引**（`InkSerializer` 那段注释里
/// 写过理由），而 .NET 基础库不带 PNG 编码；测试里那个 `ScreenProbe.SaveBmp` 写的是 BMP，
/// 体积是 PNG 的四五倍——发给家长、贴进微信都不合适。
///
/// PNG 的结构很简单：签名 ＋ IHDR ＋ IDAT（zlib 压缩的逐行像素）＋ IEND。
/// 三个坑（都在实现里处理了）：
///   1. `DeflateStream` 出来的是**裸 deflate**，zlib 的 2 字节头和尾部的 adler32 要自己补；
///   2. 每一行像素前面要加一个 **filter 字节**（用 0 = None，最省事）；
///   3. 我们渲染出来的位图是**预乘 alpha**，PNG 要的是**直通 alpha**——
///      不还原就会发灰（半透明的荧光笔最明显）。
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// 把一张 **BGRA 预乘 alpha** 的位图编成 PNG 字节。
    /// </summary>
    public static byte[] EncodeBgraPremultiplied(byte[] bgra, int w, int h)
    {
        if (bgra == null || w <= 0 || h <= 0 || bgra.Length < (long)w * h * 4) return null;

        // ① 逐行转成 PNG 要的样子：filter 字节 + RGBA 直通 alpha
        var raw = new byte[h * (1 + w * 4)];
        for (int y = 0; y < h; y++)
        {
            int dst = y * (1 + w * 4);
            raw[dst] = 0;                                  // filter = None
            int src = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                byte b = bgra[src + 0], g = bgra[src + 1], r = bgra[src + 2], a = bgra[src + 3];
                if (a != 0 && a != 255)
                {
                    // 预乘 → 直通：除以 alpha（四舍五入），并夹住
                    b = Unpremultiply(b, a); g = Unpremultiply(g, a); r = Unpremultiply(r, a);
                }
                int o = dst + 1 + x * 4;
                raw[o + 0] = r; raw[o + 1] = g; raw[o + 2] = b; raw[o + 3] = a;
                src += 4;
            }
        }

        // ② zlib 包一层：头 2 字节 + 裸 deflate + adler32
        byte[] deflated;
        using (var ms = new MemoryStream())
        {
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            deflated = ms.ToArray();
        }
        var zlib = new byte[2 + deflated.Length + 4];
        zlib[0] = 0x78; zlib[1] = 0x9C;                    // zlib: deflate, 默认窗口
        Buffer.BlockCopy(deflated, 0, zlib, 2, deflated.Length);
        uint adler = Adler32(raw);
        zlib[zlib.Length - 4] = (byte)(adler >> 24);
        zlib[zlib.Length - 3] = (byte)(adler >> 16);
        zlib[zlib.Length - 2] = (byte)(adler >> 8);
        zlib[zlib.Length - 1] = (byte)adler;

        // ③ 拼起来
        using var outMs = new MemoryStream();
        outMs.Write(Signature, 0, Signature.Length);

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w);
        WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;      // 每通道 8 位
        ihdr[9] = 6;      // 颜色类型 6 = RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;         // 压缩 / 滤波 / 隔行
        WriteChunk(outMs, "IHDR", ihdr);
        WriteChunk(outMs, "IDAT", zlib);
        WriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    private static byte Unpremultiply(byte c, byte a)
    {
        int v = (c * 255 + a / 2) / a;
        return (byte)(v > 255 ? 255 : v);
    }

    private static void WriteBE(byte[] buf, int at, uint v)
    {
        buf[at] = (byte)(v >> 24); buf[at + 1] = (byte)(v >> 16);
        buf[at + 2] = (byte)(v >> 8); buf[at + 3] = (byte)v;
    }

    /// <summary>一个 PNG 块：长度 ＋ 类型 ＋ 数据 ＋ CRC32（CRC 算"类型 ＋ 数据"）。</summary>
    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);

        var name = Encoding.ASCII.GetBytes(type);
        s.Write(name, 0, 4);
        s.Write(data, 0, data.Length);

        uint crc = Crc32(name, data);
        var cb = new byte[4];
        WriteBE(cb, 0, crc);
        s.Write(cb, 0, 4);
    }

    private static uint[] _crcTable;

    private static uint Crc32(byte[] a, byte[] b)
    {
        if (_crcTable == null)
        {
            _crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                _crcTable[n] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        foreach (var t in new[] { a, b })
            for (int i = 0; i < t.Length; i++)
                crc = _crcTable[(crc ^ t[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var t in data)
        {
            a = (a + t) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}

/// <summary>
/// 系统"另存为"对话框（`comdlg32.GetSaveFileNameW`）。
///
/// 为什么要临时借焦点：我们的覆盖层是 `WS_EX_NOACTIVATE`（永不抢焦点），而系统对话框
/// 必须有焦点才会出现在前面、才能打字。**只在弹框这一小会儿**把那个样式摘掉，
/// 弹完原样装回去（和"批注键盘模式"用的是同一招）。
///
/// 不引 WinForms / WPF：引擎是个库，这条只要一个 P/Invoke。
/// </summary>
internal static class ExportFileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        /// <summary>
        /// 文件名的缓冲区。**必须是 `string` 而不是 `StringBuilder`**：
        /// .NET 7 起，结构体/类的字段**不允许是 StringBuilder**，一调就抛
        /// `TypeLoadException: Cannot marshal field 'lpstrFile' ... cannot be of type StringBuilder`
        /// ——而且这个异常是在 P/Invoke 调用点抛的，一路冒到 Main，**直接把整个软件打死**
        /// （2026-09-17 实测：点"导出"→ 软件消失）。改成 `string` ＋ 预分配同样长度的
        /// 缓冲区（官方给的办法），返回时由 [In, Out] 拷回来。
        /// </summary>
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW([In, Out] OpenFileName ofn);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder s, int n);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// **看门线程**：对话框是模态调用（`GetSaveFileNameW` 不返回我们就拿不到它的句柄），
    /// 所以起一个短命线程，等它出现就把它顶到最前。
    ///
    /// 为什么不挂对话框钩子（`OFN_ENABLEHOOK`）：**Vista 之后的通用对话框不支持它**——
    /// 2026-09-17 实测，钩子一次都没触发（临时日志一行都没写）。
    /// 官方那条路走不通，就换成"另一个线程去找它、把它提上来"。
    /// </summary>
    private static void StartDialogWatcher()
    {
        var t = new Thread(() =>
        {
            for (int i = 0; i < 120; i++)          // 最多等 6 秒
            {
                Thread.Sleep(50);
                IntPtr dlg = FindOurDialog();
                if (dlg == IntPtr.Zero) continue;
                ForceToFront(dlg);
                Log($"对话框 {dlg} 已顶到最前（看门线程第 {i + 1} 次尝试）");
                return;
            }
            Log("看门线程：6 秒内没等到对话框（可能用户没点开，或者系统换了实现）");
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// 找我们自己进程里那个"另存为"窗口：系统的标准对话框类名是 `#32770`，
    /// 我们的覆盖层是自定义类，按类名一筛就分开了。
    /// </summary>
    private static IntPtr FindOurDialog()
    {
        IntPtr found = IntPtr.Zero;
        uint me = (uint)Environment.ProcessId;
        var sb = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != me) return true;
            GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != "#32770") return true;
            found = h;
            return false;                                 // 找到了，停
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 把窗口顶到最前并激活。
    ///
    /// 两招一起用，缺一不可（2026-09-17 实测踩过）：
    ///   ① `SetWindowPos(HWND_TOPMOST)` —— 纯 z 序调整，不受"前台锁"限制；
    ///   ② **附加到当前前台窗口的输入线程**再 `SetForegroundWindow` ——
    ///      Windows 只允许"当前前台进程"抢前台，我们不是，所以直接调必然失败；
    ///      附加线程（AttachThreadInput）是系统文档里给出的标准绕法。
    /// </summary>
    private static void ForceToFront(IntPtr hwnd)
    {
        try
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            IntPtr fg = GetForegroundWindow();
            uint fgTid = GetWindowThreadProcessId(fg, out _);
            uint myTid = GetCurrentThreadId();
            if (fgTid != 0 && fgTid != myTid)
            {
                AttachThreadInput(myTid, fgTid, true);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                AttachThreadInput(myTid, fgTid, false);
            }
            else
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            Log($"对话框 {hwnd}：顶到最前（原前台 {fg}，线程 {fgTid} / 本线程 {myTid}）");
        }
        catch (Exception ex) { Log("顶对话框出错：" + ex.Message); }
    }

    /// <summary>
    /// 排查用的日志：教室机器上看不到控制台，出问题时看
    /// `%TEMP%\inkteach-export.log`。只在弹框那一下写，不产生任何常态开销。
    /// </summary>
    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "inkteach-export.log"),
                               $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}");
            Console.WriteLine("[导出] " + msg);
        }
        catch { }
    }

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_ENABLEHOOK = 0x00000020;

    /// <summary>
    /// `OPENFILENAMEW` 的字节数（Windows 自己也是按这个长度校验的）。
    ///
    /// **不能写成 `Marshal.SizeOf`**：这个类型里有 `string` / `StringBuilder` 字段，
    /// Marshal 算不出非托管布局，会直接抛
    /// "cannot be marshaled as an unmanaged structure"。
    /// 这个坑是**自检当场抓到的**——`--selftest` 会逐个点操作条上的按钮，
    /// 点到"导出"就炸了（那是套件第二次替我抓 bug）。
    /// </summary>
    private static int SizeOfOpenFileName => IntPtr.Size == 8 ? 152 : 88;

    /// <summary>弹"另存为"。返回 null = 用户取消（取消就什么都不做）。</summary>
    public static string AskForPng(IntPtr owner, string suggestedName)
    {
        StartDialogWatcher();          // 先起看门线程：对话框一出现就把它顶到最前
        var ofn = new OpenFileName
        {
            lStructSize = SizeOfOpenFileName,
            hwndOwner = owner,
            // 过滤器是"双 \0 结尾"的一串
            lpstrFilter = "PNG 图片 (*.png)\0*.png\0\0",
            nFilterIndex = 1,
            // 缓冲要**预分配成 nMaxFile 那么长**，再把建议的文件名写进开头
            lpstrFile = suggestedName + new string('\0', Math.Max(0, 512 - suggestedName.Length)),
            nMaxFile = 512,
            lpstrTitle = "导出选中的内容（透明底 PNG）",
            lpstrDefExt = "png",
            Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST,
        };
        if (!GetSaveFileNameW(ofn)) return null;
        var path = (ofn.lpstrFile ?? "").Trim().TrimEnd('\0');
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
