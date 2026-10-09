using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace InkEngine;

/// <summary>
/// **导出格式表**：四种选择，各写清"适合什么、代价是什么"。**唯一的定义处**——
/// 对话框的类型栏、默认扩展名、编码器选择、自检判据全都读它。
///
/// 为什么把优势**写进类型名**（用户 2026-09-17："干脆再增加几种格式，说清楚优势"）：
/// 那一行是老师做决定时唯一一定会看到的地方。写"PNG"等于没说；
/// 写"透明底 · 贴课件首选"才是在帮他做决定。
///
/// **为什么是这四个**（不是为凑数）：位图之间的差别其实只有三件事——
/// **有没有透明底**、**无损还是有损**、**文件多大**。这四条正好对上四个真实场景：
/// 贴课件（透明）／发微信（小文件）／贴深色模板（白底无损）／老软件打不开新格式（BMP）。
///
/// 真正还缺的是**矢量**（SVG/PDF："放大到投影也不糊、还能在课件里继续编辑"）——
/// 那是另一类东西，要按几何导笔迹、还得在真实课件软件里逐个验，**单开一步做**。
/// </summary>
internal static class ExportFormats
{
    public const int Count = 4;

    /// <summary>给系统对话框的过滤器串（双 `\0` 结尾，两两一组）。</summary>
    public const string Filter =
        "PNG 图片（透明底 · 贴课件首选，无损）\0*.png\0"
      + "JPEG 图片（白底 · 文件最小，好发微信邮件）\0*.jpg;*.jpeg\0"
      + "PNG 图片（白底 · 无损，深色模板上不露底）\0*.png\0"
      + "BMP 图片（白底 · 老软件也能打开，文件最大）\0*.bmp\0\0";

    /// <summary>这种选择默认用什么扩展名（写进建议文件名、也交给对话框补后缀）。</summary>
    public static string ExtensionFor(int filterIndex) => filterIndex switch
    {
        2 => ".jpg",
        4 => ".bmp",
        _ => ".png",
    };

    /// <summary>
    /// 编码。**两件事分开决定**：
    ///   · **编码器看扩展名**——老师自己把名字敲成 `.jpg` 就该存 JPEG；
    ///   · **底色看他在类型栏选的那一条**——"透明底 / 白底"是一个独立的选择。
    ///
    /// 唯一的例外是**有损 / 无透明通道的格式一律白底**：选了 JPEG 却把名字写成 .png
    /// （或者反过来）时，绝不能把透明通道丢给 JPEG——那会变成一堆黑块。
    /// </summary>
    public static byte[] Encode(string path, byte[] bgra, int w, int h, int filterIndex,
                                out string tag)
    {
        string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        bool wantBmp = ext == ".bmp";
        bool wantJpeg = ext == ".jpg" || ext == ".jpeg";
        bool wantTransparent = filterIndex == 1;          // 只有第一条是"透明底"

        if (wantJpeg)
        {
            tag = "JPEG（白底）";
            return JpegWriter.EncodeBgraOverWhite(bgra, w, h);
        }
        if (wantBmp)
        {
            tag = "BMP（白底）";
            return BmpWriter.EncodeBgraOverWhite(bgra, w, h);
        }
        if (wantTransparent)
        {
            tag = "PNG（透明底）";
            return PngWriter.EncodeBgraPremultiplied(bgra, w, h);
        }
        tag = "PNG（白底）";
        return PngWriter.EncodeBgraPremultiplied(ExportImages.OverWhiteBgra(bgra, w, h), w, h);
    }
}

/// <summary>
/// 图片的公共小工具：**把预乘 BGRA 合成到白底**。
///
/// 三种编码器都要这一步（JPEG 没有 alpha、BMP 的 alpha 兼容性差、白底 PNG 就是想要白底），
/// 所以只写一遍。渲染出来的位图是**预乘 alpha**：一个像素的实际颜色 = 存储值，
/// 它在白底上显示出来 = `存储值 + 255 × (1 - α)`。
/// </summary>
internal static class ExportImages
{
    public static byte[] OverWhiteBgra(byte[] bgra, int w, int h)
    {
        if (bgra == null || w <= 0 || h <= 0 || bgra.Length < (long)w * h * 4) return null;
        var outBytes = new byte[(long)w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int p = i * 4;
            float a = bgra[p + 3] / 255f;
            float bg = 255f * (1f - a);
            outBytes[p + 0] = (byte)MathF.Min(255f, bgra[p + 0] + bg);
            outBytes[p + 1] = (byte)MathF.Min(255f, bgra[p + 1] + bg);
            outBytes[p + 2] = (byte)MathF.Min(255f, bgra[p + 2] + bg);
            outBytes[p + 3] = 255;
        }
        return outBytes;
    }
}

/// <summary>
/// **BMP 编码器**（24 位、白底、无损）。
///
/// 为什么还要它：PNG 也是无损的，但**老软件不一定认**——教室机器上常有多年没更新的
/// 课件工具 / 老版 WPS / 投影仪自带的白板程序。BMP 是 Windows 上最古老的位图格式，
/// 那些程序一定能打开。代价是**文件最大**（不压缩，约为 PNG 的三到十倍）。
///
/// 写 24 位而不是 32 位：32 位 BMP 的 alpha 各家实现不一致，很多程序会把它当
/// "不透明"甚至显示成黑块。白底 24 位没有这个歧义。
/// </summary>
internal static class BmpWriter
{
    public static byte[] EncodeBgraOverWhite(byte[] bgra, int w, int h)
    {
        var over = ExportImages.OverWhiteBgra(bgra, w, h);
        if (over == null) return null;

        int stride = (w * 3 + 3) & ~3;                 // BMP 每行按 4 字节对齐
        int imageBytes = stride * h;
        var bytes = new byte[54 + imageBytes];

        void W16(int at, int v) { bytes[at] = (byte)v; bytes[at + 1] = (byte)(v >> 8); }
        void W32(int at, int v)
        {
            bytes[at] = (byte)v; bytes[at + 1] = (byte)(v >> 8);
            bytes[at + 2] = (byte)(v >> 16); bytes[at + 3] = (byte)(v >> 24);
        }

        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        W32(2, bytes.Length); W32(10, 54);
        W32(14, 40); W32(18, w); W32(22, h);           // 高度为正 = 自下而上存
        W16(26, 1); W16(28, 24); W32(34, imageBytes);

        // BMP 是**自下而上**存的：源图第 0 行要写到文件里最后一行
        for (int y = 0; y < h; y++)
        {
            int src = (h - 1 - y) * w * 4;
            int dst = 54 + y * stride;
            for (int x = 0; x < w; x++)
            {
                // 注意 `src + x * 4`：第一版这里忘了随 x 走，每一行都拿"这一行第一个像素"
                // 铺满——整张图变成一片白，`--iotest` 的"笔迹颜色一个不差"当场抓住。
                int s = src + x * 4;
                bytes[dst + x * 3 + 0] = over[s + 0];       // B
                bytes[dst + x * 3 + 1] = over[s + 1];       // G
                bytes[dst + x * 3 + 2] = over[s + 2];       // R
            }
        }
        return bytes;
    }
}

/// <summary>
/// **JPEG 编码器**（走系统的 GDI+）。
///
/// 为什么不像 PNG 那样自己写：JPEG 要 DCT ＋ 量化 ＋ 霍夫曼编码，手写不现实；
/// 系统本来就有编码器（GDI+ / WIC），`System.Drawing.Common` 是微软官方的 Windows 包，
/// 用它最稳（本项目已经引了 5 个 Vortice 图形包，再加这一个不影响任何架构判断）。
///
/// **JPEG 没有透明通道**，所以这里先把图**合成到白底**再编码——
/// 直接丢 BGRA 进去的话，透明处会变成黑块（很多程序都踩过这个）。
/// 这也是"PNG 透明底、JPG 白底"这条差别的由来，所以**在文件类型下拉框里就写清楚**
/// （用户 2026-09-17 问"要不要让用户知道"，答案是：写在用户唯一会看的那一行）。
/// </summary>
internal static class JpegWriter
{
    public static byte[] EncodeBgraOverWhite(byte[] bgra, int w, int h, long quality = 88)
    {
        if (bgra == null || w <= 0 || h <= 0 || bgra.Length < (long)w * h * 4) return null;

        var over = ExportImages.OverWhiteBgra(bgra, w, h);
        if (over == null) return null;

        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                // 已经是"合成到白底、不透明"的 BGRA，这里只丢掉 alpha 通道
                int src = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int o = x * 3;
                    row[o + 0] = over[src + 0]; row[o + 1] = over[src + 1]; row[o + 2] = over[src + 2];
                    src += 4;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, nint.Add(data.Scan0, y * data.Stride), row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }

        var codec = Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(),
                               c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        if (codec == null) return null;
        using var pars = new System.Drawing.Imaging.EncoderParameters(1);
        pars.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, pars);
        return ms.ToArray();
    }
}

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

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder s, int n);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    // ---- 对话框"第一帧就在中间"：WH_CBT 钩子 ----------------------------------
    //
    // 为什么需要（用户 2026-09-27："它出现的时候就直接在中间，现在会先跳出来、
    // 然后再移动到中间"）：只靠看门线程"出现后 50ms 内发现、再搬"总会先闪一下——
    // 系统先按自己的算法摆一次（实测会摆到 (0,0)，见 CenterAndBringUp 的注释），
    // 我们随后才把它挪到中间。WH_CBT 的 **HCBT_ACTIVATE 在窗口激活之前**送达，
    // 此刻改坐标，显示出来的第一帧就在中间；看门线程保留——它管"置顶 + 激活 +
    // 防系统再摆回来"（那两件事仍然需要）。
    //
    // 注：`OPENFILENAME` 自带的 lpfnHook / OFN_ENABLEHOOK 在 Vista 之后不触发
    //（见 StartDialogWatcher 的注释），WH_CBT 是另一套机制，两者无关。
    private const int WH_CBT = 5;
    private const int HCBT_CREATEWND = 3;
    private const int HCBT_ACTIVATE = 5;
    private const int WS_CHILD = unchecked((int)0x40000000);

    /// <summary>
    /// `CREATESTRUCT`（只用到几个字段，但**布局必须完整照抄**——少一个字段偏移就全错，
    /// 写回 x/y 时就是写坏内存）。字段顺序按官方定义：cy, cx, y, x 这个顺序容易记反。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT_W
    {
        public IntPtr lpCreateParams;
        public IntPtr hInstance;
        public IntPtr hMenu;
        public IntPtr hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public IntPtr lpszName;
        public IntPtr lpszClass;
        public int dwExStyle;
    }

    private delegate IntPtr CbtProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, CbtProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr _cbtHook = IntPtr.Zero;
    /// <summary>必须保持引用：委托被 GC 回收之后系统回调会直接崩进程。</summary>
    private static CbtProcDelegate _cbtProc;
    /// <summary>这一次弹框已经摆过位置了（**只摆一次**：用户拖过之后不许再抢）。</summary>
    private static bool _cbtCenteredOnce;
    /// <summary>排查用（跑 --dialogprobe 看日志）：钩子触发那一刻窗口在哪、有没有摆成功。</summary>
    private static int _cbtSeenX = int.MinValue, _cbtSeenY = int.MinValue;
    private static bool _cbtApplied;
    /// <summary>排查用：钩子一共被调用了几次（区分"钩子没挂上"和"没收到 ACTIVATE"）。</summary>
    private static int _cbtCalls;
    /// <summary>排查用：各 nCode 的调用次数（0..9）。</summary>
    private static readonly int[] _cbtHist = new int[10];
    /// <summary>排查用：出生分支看到的窗口信息（**内存记录**——系统回调里不写文件）。</summary>
    private static string _cbtBirthNote = "（出生分支一次都没被走到）";

    /// <summary>
    /// 装"对话框激活前摆位置"的钩子。**只钩本线程**（对话框就在调用线程上创建，
    /// 见 AskForImage）。
    ///
    /// ⚠ `hMod` 必须是**本进程的模块句柄**，不能传 NULL：托管委托的代码地址不在任何
    /// "匿名可执行页"里，系统按 hMod 去校验过程地址时对不上，钩子会**静默不投递**
    /// ——2026-09-27 实测（`--dialogprobe` 日志："CBT 钩子已装"但"没触发"）。
    /// </summary>
    private static void InstallDialogPositionHook()
    {
        try
        {
            _cbtProc ??= OnCbt;
            _cbtCenteredOnce = false;
            _cbtHook = SetWindowsHookEx(WH_CBT, _cbtProc, Native.GetModuleHandle(null),
                                        GetCurrentThreadId());
            Log(_cbtHook != IntPtr.Zero
                ? "CBT 钩子已装（对话框第一帧就居中）"
                : "CBT 钩子装不上（回退：看门线程出现后再居中）");
        }
        catch (Exception ex) { Log("CBT 钩子装出错：" + ex.Message); }
    }

    private static void RemoveDialogPositionHook()
    {
        try
        {
            if (_cbtHook != IntPtr.Zero) { UnhookWindowsHookEx(_cbtHook); _cbtHook = IntPtr.Zero; }
        }
        catch { }
    }

    /// <summary>钩子回调：在对话框**出生/激活**的那一刻把它摆到中间。</summary>
    private static IntPtr OnCbt(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _cbtCalls++;
        if (nCode >= 0 && nCode < _cbtHist.Length) _cbtHist[nCode]++;
        // HCBT_CREATEWND：**窗口刚创建、还没显示**——改 CREATESTRUCT 的 x/y 就是
        // "出生就在中间"，这才是"第一帧就在中间"的正解。
        //（HCBT_ACTIVATE 那条路 2026-09-27 实测**收不到**：钩子在跑（调用 36 次），
        //  但通用对话框第一次显示走的不发 ACTIVATE——所以主路放在 CREATEWND。）
        if (nCode == HCBT_CREATEWND && lParam != IntPtr.Zero && !_cbtCenteredOnce)
        {
            try { TryPlaceAtBirth(wParam, lParam); } catch { }
        }
        // HCBT_ACTIVATE：兜底——若某条路径真的是"先激活"（窗口已创建但没经过上面那条），
        // 在激活前再用"只挪位置"的方式摆一次。异常绝不许外抛（系统回调，抛=crupt）。
        else if (nCode == HCBT_ACTIVATE && wParam != IntPtr.Zero && !_cbtCenteredOnce)
        {
            _cbtCenteredOnce = true;
            try
            {
                GetWindowRect(wParam, out var before);       // 排查用：钩子那一刻它在哪
                _cbtSeenX = before.Left; _cbtSeenY = before.Top;
                _cbtApplied = CenterOnce(wParam, out _, out _, quiet: true);
            }
            catch { }
        }
        return CallNextHookEx(_cbtHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 在窗口"出生"时改它 CREATESTRUCT 里的 x/y，让它显示出来的第一帧就在工作区中间。
    ///
    /// **只认"顶层 + 类名 #32770（系统对话框）"**：弹框期间这条线程还会创建别的窗口
    /// （对话框的每个子控件都会走这里），动错了就是"按钮跑到别处"。
    ///
    /// ⚠ 类名**必须用 `GetClassName` 从窗口句柄反查**，不能读 CREATESTRUCT 里的
    /// `lpszClass`：2026-09-27 实测系统对话框是**用原子创建的**（读到的是 atom 0xC018
    /// 而不是字符串 "#32770"），按字符串比对会全部落空——那版日志里"出生摆位未成"
    /// 就是这个原因。
    /// </summary>
    private static void TryPlaceAtBirth(IntPtr hwnd, IntPtr cbtCreateWndPtr)
    {
        // 拿类名（窗口这时已经建好了，只是还没显示；wParam 就是它的句柄）。
        var sb = new StringBuilder(32);
        Native.GetClassNameW(hwnd, sb, sb.Capacity);
        string className = sb.ToString();
        _cbtBirthNote = $"类名={className}";
        if (className != "#32770") return;                       // 不是系统对话框：跳过

        IntPtr csPtr = Marshal.ReadIntPtr(cbtCreateWndPtr);      // CBT_CREATEWND 第一个字段就是 lpcs
        if (csPtr == IntPtr.Zero) return;
        var cs = Marshal.PtrToStructure<CREATESTRUCT_W>(csPtr);

        if ((cs.style & WS_CHILD) != 0) return;                  // 子控件：跳过（对话框的控件都是子窗口）

        // 居中到**光标所在那块屏**的工作区（多屏时，对话框该出现在老师正在看的那块屏）。
        if (!Native.GetCursorPos(out var pt)) return;
        IntPtr mon = Native.MonitorFromWindow(Native.WindowFromPoint(pt), Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;
        var wa = mi.rcWork;
        int w = cs.cx, h = cs.cy;
        int nx = wa.Left + (wa.Width - w) / 2;
        int ny = wa.Top + (h >= wa.Height ? 0 : (wa.Height - h) / 2);

        // 写回 x / y：偏移**问 OffsetOf，不手算**（手算错一个字节就是写坏内存）。
        Marshal.WriteInt32(csPtr, (int)Marshal.OffsetOf<CREATESTRUCT_W>(nameof(CREATESTRUCT_W.x)), nx);
        Marshal.WriteInt32(csPtr, (int)Marshal.OffsetOf<CREATESTRUCT_W>(nameof(CREATESTRUCT_W.y)), ny);
        _cbtCenteredOnce = true;
        _cbtApplied = true;
        Log($"CBT：对话框 {w}×{h} 出生时即被摆到 ({nx},{ny})（居中于光标所在屏）");
    }

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
            // 10 毫秒一轮（不是 50）：发现得越快，"系统初摆 → 我们纠正"之间留给眼睛的
            // 那一帧越少——对话框出生位置已经由 CBT 钩子管了，这里只是兜底与纠偏。
            for (int i = 0; i < 600; i++)          // 最多等 6 秒
            {
                Thread.Sleep(10);
                IntPtr dlg = FindOurDialog();
                if (dlg == IntPtr.Zero) continue;
                // **等它"长好"再碰**：对话框刚 CreateWindow 时可见但空（子控件还没建、
                // 客户区还没画）。这时 SWP_SHOWWINDOW/激活会把它那张**空白首帧**强行摆到
                // 屏幕上——用户 2026-10-05 报的"点保存图片先闪一下白屏，然后才出对话框"。
                if (!DialogReady(dlg)) continue;
                CenterAndBringUp(dlg);
                Log($"对话框 {dlg} 已居中并顶到最前（看门线程第 {i + 1} 次尝试）");
                return;
            }
            Log("看门线程：6 秒内没等到对话框（可能用户没点开，或者系统换了实现）");
        });
        t.IsBackground = true;
        t.Start();
    }

    /// <summary>
    /// 对话框"长好了"吗：可见 **且已经建出子控件**（客户区画得出来了）。
    ///
    /// 为什么需要：通用对话框是"先创建空壳、再建控件、再画"的三拍。看门线程 10ms 一轮，
    /// 很容易在第二拍之前就抓到它——那一刻碰它（尤其 `SWP_SHOWWINDOW` / 激活），
    /// 就会把它那张空白首帧摆到老师眼前（用户 2026-10-05 报的白屏）。
    /// 子控件出现 = 客户区马上就有内容，这时再置顶/激活就不会看到空白。
    /// </summary>
    private static bool DialogReady(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        bool hasChild = false;
        EnumChildWindows(hwnd, (h, _) => { hasChild = true; return false; }, IntPtr.Zero);
        return hasChild;
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
    /// 把对话框**摆到屏幕中间**、顶到最前并激活。
    ///
    /// **一、为什么必须居中**（用户 2026-09-17："在弹出居中保存框"）：
    /// 实测（`--dialogprobe`）系统把对话框摆在了 **(0,0)**——屏幕左上角，
    /// 而屏幕是 2880×1800。老师在最左边那一小块里找一个"另存为"，很别扭。
    /// 我们按最近那块显示器的**工作区**（`rcWork`，避让任务栏）居中，顺手也把
    /// "两个屏幕时跑到副屏去了"这种意外一并收掉。
    ///
    /// **二、为什么必须置顶，而且是一直置顶**：
    /// 我们的覆盖层是铺满全屏的画布。白板开着的时候它是**不透明的白**，
    /// 只要它在对话框上面，对话框就**整个看不见**——实测截图上就是一大片白底 + 我们的墨，
    /// 连标题栏都看不到（`tmp/dlg-a-1-打开时.bmp`）。
    /// 早先那版"顶完马上取消置顶"是**错的**：取消置顶之后对话框立刻掉回覆盖层下面，
    /// 白板一开就完全看不见它。现在**从头到尾保持置顶**：它在最上面，老师才看得见、
    /// 点得到，它的下拉列表也才在它上面。关闭之后由
    /// `ReturnFocusAfterDialog` 把覆盖层的置顶装回去。
    ///
    /// **三、激活**（这一步和置顶是两件事，缺一不可）：
    ///   ① `SetWindowPos(HWND_TOPMOST)` —— 纯 z 序调整，不受"前台锁"限制；
    ///   ② **附加到当前前台窗口的输入线程**再 `SetForegroundWindow` ——
    ///      Windows 只允许"当前前台进程"抢前台，我们不是，所以直接调必然失败；
    ///      附加线程（AttachThreadInput）是系统文档里给出的标准绕法。
    /// </summary>
    private static void CenterAndBringUp(IntPtr hwnd)
    {
        try
        {
            // 排查记录（--dialogprobe 看日志）：看门线程第一次发现它时在哪 + 钩子干了什么。
            // 这两行回答的是"用户看到的'先出现再移动'发生在哪一步"。
            GetWindowRect(hwnd, out var seen);
            string hist = string.Join(" ", System.Linq.Enumerable.Range(0, _cbtHist.Length)
                                    .Where(i => _cbtHist[i] > 0)
                                    .Select(i => $"code{i}×{_cbtHist[i]}"));
            Log($"对话框 {hwnd}：看门线程发现于 ({seen.Left},{seen.Top})；"
                + $"CBT 钩子调用 {_cbtCalls} 次（{hist}）；出生摆位{(_cbtApplied ? "成功" : "未成")}；"
                + $"出生看到：{_cbtBirthNote}");

            // ① 先只管 z 序：把它提到置顶层，位置先别动。
            //    ⚠ **不带 SWP_SHOWWINDOW**：对话框由系统自己显示；我们提前 Show，
            //    会在它还没画完时把空白首帧推上屏幕（同 DialogReady 那段注释）。
            //    只用 NOACTIVATE 调 z 序，它自己的首帧由系统按正常节奏画。
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            // ② 激活（见下面第三段）。
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

            // ③ **兜底纠偏**（2026-09-27 起改成"先判后动"）：
            //
            // 对话框"出生"的位置已经由 CBT 钩子管住了（实测出生就在 (779,330)，随后
            // 系统/对话框自己按最终尺寸精修到 (720,372)，只差几十像素）——**这种小偏差
            // 我们不插手**：原来的"每轮都重新居中"会把那几十像素再搬一次，用户反而
            // 看见一次小跳。现在只在**偏得离谱**（>200 像素，比如系统把它扔到 (0,0)）
            // 时才拉回居中——那才是这层兜底真正要防的事。
            //
            // 容差 200 是这么定的：正常"出生位置 → 最终位置"的自我精修是几十像素
            //（实测 72px）；要防的故障是"摆到屏幕角上"（700+ 像素）。中间没有别的量级。
            const int WayOffPx = 200;
            int moves = 0, okStreak = 0;
            for (int i = 0; i < 14; i++)
            {
                if (!MeasureCenter(hwnd, out int wantX, out int wantY)) break;
                GetWindowRect(hwnd, out var cur);
                int dx = cur.Left - wantX, dy = cur.Top - wantY;

                if (Math.Abs(dx) > WayOffPx || Math.Abs(dy) > WayOffPx)
                {
                    // 真出事了（被摆到别处）：拉回居中，并记下它偏去了哪（排查用）。
                    Log($"对话框 {hwnd}：偏到 ({cur.Left},{cur.Top})，拉回居中 ({wantX},{wantY})");
                    MoveTo(hwnd, wantX, wantY, quiet: false);
                    moves++;
                    okStreak = 0;
                }
                else
                {
                    // 在中心附近（含正常的几十像素精修）：算"到位"。连看 3 轮都到位即收工。
                    if (++okStreak >= 3)
                    {
                        Log($"对话框 {hwnd}：已在居中位置 ({cur.Left},{cur.Top})"
                            + $"（我们动手 {moves} 次）");
                        break;
                    }
                }
                Thread.Sleep(150);
            }
            Log($"对话框 {hwnd}：顶到最前（原前台 {fg}，线程 {fgTid} / 本线程 {myTid}）");
        }
        catch (Exception ex) { Log("顶对话框出错：" + ex.Message); }
    }

    /// <summary>
    /// 按最近那块显示器的**工作区**把对话框摆到中间（工作区装不下就贴顶，
    /// 免得标题栏被推到屏幕外、拖都拖不动）。返回 false = 没量到显示器，位置没动。
    /// </summary>
    /// <param name="quiet">
    /// true = **只挪位置**：不置顶、不显示、不激活（CBT 钩子里用——那一刻窗口还没
    /// 显示出来，显示与 z 序交给系统自己的流程去做）；false = 看门线程那一套
    /// （顺带置顶 + 确保显示）。
    /// </param>
    private static bool CenterOnce(IntPtr hwnd, out int wantX, out int wantY, bool quiet = false)
    {
        if (!MeasureCenter(hwnd, out wantX, out wantY)) return false;
        MoveTo(hwnd, wantX, wantY, quiet);
        return true;
    }

    /// <summary>
    /// **只算"该在哪"、不动手**（居中到最近显示器的工作区）。false = 没量到显示器。
    /// 拆开是为了让看门线程能"先判后动"——偏得离谱才动手（见那个循环里的注释）。
    /// </summary>
    private static bool MeasureCenter(IntPtr hwnd, out int wantX, out int wantY)
    {
        wantX = wantY = int.MinValue;
        GetWindowRect(hwnd, out var r);
        IntPtr mon = MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return false;

        var wa = mi.rcWork;
        wantX = wa.Left + (wa.Width - r.Width) / 2;
        wantY = wa.Top + (r.Height >= wa.Height ? 0 : (wa.Height - r.Height) / 2);
        return true;
    }

    private static void MoveTo(IntPtr hwnd, int x, int y, bool quiet)
        => SetWindowPos(hwnd, quiet ? IntPtr.Zero : HwndTopmost, x, y, 0, 0,
                        quiet ? (SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)
                              : (SWP_NOSIZE | SWP_SHOWWINDOW));

    // 量尺寸 / 找显示器 / 读工作区：走引擎已有的那一套 interop，不在这里另开一份。
    private static void GetWindowRect(IntPtr hWnd, out Native.RECT r) => Native.GetWindowRect(hWnd, out r);
    private static IntPtr MonitorFromWindow(IntPtr hWnd, uint flags) => Native.MonitorFromWindow(hWnd, flags);
    private static bool GetMonitorInfoW(IntPtr mon, ref Native.MONITORINFO mi) => Native.GetMonitorInfo(mon, ref mi);

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
    /// <summary>多选（"打开文档…"用；单选对话框不带它）。</summary>
    private const int OFN_ALLOWMULTISELECT = 0x00000200;

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

    /// <summary>
    /// 弹"另存为"。返回 null = 用户取消（取消就什么都不做）。
    /// <paramref name="filterIndex"/> 回传用户选的是第几条（见 <see cref="ExportFormats"/>，
    /// 1 = PNG 透明底、2 = JPEG 白底、3 = PNG 白底、4 = BMP 白底）。
    ///
    /// **<paramref name="title"/> 必须由调用方给**：这里以前写死"导出选中的内容"，
    /// 于是「更多 → 保存图片」（整块板书）也顶着"导出选中的内容"的标题弹框
    /// （2026-10-05 用户报"点保存图片怎么先跳出来这个"——功能没错，标题串了门）。
    /// 两个入口分明：选中导出 = 导出选中的内容；保存图片 = 保存板书图片。
    /// </summary>
    public static string AskForImage(IntPtr owner, string suggestedName, int defaultFilterIndex,
                                     string title, out int filterIndex)
    {
        filterIndex = defaultFilterIndex;
        StartDialogWatcher();          // 看门线程：对话框出现后置顶 + 激活 + 防系统再摆
        InstallDialogPositionHook();   // CBT 钩子：**第一帧就出现在中间**（见那个函数）
        var ofn = new OpenFileName
        {
            lStructSize = SizeOfOpenFileName,
            hwndOwner = owner,
            // 过滤器是"双 \0 结尾"的一串；每种类型的**优势和代价**都写在名字里（见 ExportFormats）
            lpstrFilter = ExportFormats.Filter,
            nFilterIndex = Math.Clamp(defaultFilterIndex, 1, ExportFormats.Count),
            // 缓冲要**预分配成 nMaxFile 那么长**，再把建议的文件名写进开头
            lpstrFile = suggestedName + new string('\0', Math.Max(0, 512 - suggestedName.Length)),
            nMaxFile = 512,
            lpstrTitle = title,
            lpstrDefExt = ExportFormats.ExtensionFor(defaultFilterIndex).TrimStart('.').Split(';')[0],
            Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST,
        };
        // 钩子必须在**本线程、调用期间**挂着（对话框就在这个调用里创建）——用完立刻卸。
        bool ok;
        try { ok = GetSaveFileNameW(ofn); }
        finally { RemoveDialogPositionHook(); }
        if (!ok) return null;
        filterIndex = ofn.nFilterIndex;
        var path = (ofn.lpstrFile ?? "").Trim().TrimEnd('\0');
        return string.IsNullOrEmpty(path) ? null : path;
    }

    // ---- 墨迹文件（.inkb）的保存 / 打开（墨迹 A，2026-10-01）------------------
    //
    // 和上面那条走**完全同一套**看门线程 + CBT 居中 + 焦点借用（由调用方包住），
    // 不重新发明；区别只在过滤器、标题、默认扩展名，以及"另存为"换成"打开"。

    private const string InkFilter = "InkTeach 板书 (*.inkb)\0*.inkb\0\0";
    private const int OFN_FILEMUSTEXIST = 0x00001000;

    /// <summary>
    /// "打开文档…"的过滤器。五种：
    /// ① 图片+PDF+PPT 混合（默认——老师不用先想"我这是啥"；图片可多选）；
    /// ② 只要图片；③ 只要 PDF；④ 只要 PPT；⑤ 所有文件（老师自己改过扩展名时兜底）。
    /// 说明：**"分工"不在对话框里做**——对话框只负责把路径拿回来，
    /// 谁负责什么由调用方按扩展名决定（图片/PDF 见 DocPages，PPT 见 PptLaunch）。
    /// </summary>
    internal const string DocumentFilter =
        "图片、PDF 与 PPT\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.pdf;" + PptLaunch.FilterSpec + "\0" +
        "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff\0" +
        "PDF 文档 (*.pdf)\0*.pdf\0" +
        "PPT 演示文稿 (" + PptLaunch.FilterSpec + ")\0" + PptLaunch.FilterSpec + "\0" +
        "所有文件 (*.*)\0*.*\0\0";

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW([In, Out] OpenFileName ofn);

    /// <summary>"保存墨迹"：弹另存为。返回 null = 取消。</summary>
    public static string AskForInkSave(IntPtr owner, string suggestedName, string initialDir)
    {
        StartDialogWatcher();
        InstallDialogPositionHook();
        var ofn = new OpenFileName
        {
            lStructSize = SizeOfOpenFileName,
            hwndOwner = owner,
            lpstrFilter = InkFilter,
            nFilterIndex = 1,
            lpstrFile = suggestedName + new string('\0', Math.Max(0, 512 - suggestedName.Length)),
            nMaxFile = 512,
            lpstrInitialDir = string.IsNullOrEmpty(initialDir) ? null : initialDir,
            lpstrTitle = "保存墨迹",
            lpstrDefExt = "inkb",
            Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST,
        };
        bool ok;
        try { ok = GetSaveFileNameW(ofn); }
        finally { RemoveDialogPositionHook(); }
        if (!ok) return null;
        var path = (ofn.lpstrFile ?? "").Trim().TrimEnd('\0');
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>"打开墨迹"：弹打开对话框（只认 .inkb，必须已存在）。返回 null = 取消。</summary>
    public static string AskForInkOpen(IntPtr owner, string initialDir)
    {
        StartDialogWatcher();
        InstallDialogPositionHook();
        var ofn = new OpenFileName
        {
            lStructSize = SizeOfOpenFileName,
            hwndOwner = owner,
            lpstrFilter = InkFilter,
            nFilterIndex = 1,
            // 缓冲区预分配成 nMaxFile 那么长（同 AskForImage 的理由：字段必须是 string）
            lpstrFile = new string('\0', 512),
            nMaxFile = 512,
            lpstrInitialDir = string.IsNullOrEmpty(initialDir) ? null : initialDir,
            lpstrTitle = "打开墨迹",
            lpstrDefExt = "inkb",
            Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST,
        };
        bool ok;
        try { ok = GetOpenFileNameW(ofn); }
        finally { RemoveDialogPositionHook(); }
        if (!ok) return null;
        var path = (ofn.lpstrFile ?? "").Trim().TrimEnd('\0');
        return string.IsNullOrEmpty(path) ? null : path;
    }

    // ---- "打开文档…"（图片批量 / PDF；2026-10-07，图片与 PDF 导入第一步）------------

    /// <summary>
    /// 弹"打开文档"。**图片可多选**（一次铺一叠页）；PDF 也走这个框，
    /// 混选时由调用方按扩展名分工（PDF 一次只认第一份，规则在 DocPages 里）。
    ///
    /// 看门线程 / CBT 居中 / 焦点借用与上面三条**完全同一套**，不重新发明；
    /// 区别只有：过滤器、标题、多选标志、以及缓冲区要开得足够大。
    ///
    /// 返回 null = 用户取消（或一个都没选中）。返回的每条都是**完整路径字符串**。
    /// </summary>
    public static string[] AskForOpenDocuments(IntPtr owner, string initialDir)
    {
        StartDialogWatcher();
        InstallDialogPositionHook();
        // 多选时缓冲区形状是 "目录\0名字1\0名字2\0…\0\0"；
        // 32K 字符够选几千个文件，也远超 MAX_PATH×N。
        const int cap = 32768;
        var ofn = new OpenFileName
        {
            lStructSize = SizeOfOpenFileName,
            hwndOwner = owner,
            lpstrFilter = DocumentFilter,
            nFilterIndex = 1,
            // 字段只能是 string（见 OpenFileName 里的说明）：预分配一整条空缓冲
            lpstrFile = new string('\0', cap),
            nMaxFile = cap,
            lpstrInitialDir = string.IsNullOrEmpty(initialDir) ? null : initialDir,
            lpstrTitle = "打开文档（图片可多选；PPT 直接放映）",
            lpstrDefExt = "pdf",
            Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_ALLOWMULTISELECT,
        };
        bool ok;
        try { ok = GetOpenFileNameW(ofn); }
        finally { RemoveDialogPositionHook(); }
        if (!ok) return null;
        var files = ParseMultiSelection(ofn.lpstrFile);
        return files == null || files.Length == 0 ? null : files;
    }

    /// <summary>
    /// 解析打开对话框的返回缓冲区。两种形状（Win32 的老规矩）：
    ///   · **单选**（只选了一个文件）：整条就是一份完整路径；
    ///   · **多选**：第一段是目录，后面每一段是一个文件名——都得自己拼回去。
    ///
    /// **单独拆出来是为了能直接自检**：这里有三个经典坑——末尾的空段（双 \0）、
    /// 单选/多选两种形状、目录拼接——不单独测就会在"选了一个"和"选了三个"之间翻车，
    /// 而且要弹真对话框才能复现（自检里弹不了框）。
    /// </summary>
    internal static string[] ParseMultiSelection(string buf)
    {
        if (string.IsNullOrEmpty(buf)) return null;
        var parts = new List<string>();
        foreach (var seg in buf.Split('\0'))
            if (seg.Length > 0) parts.Add(seg.Trim());
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return new[] { parts[0] };

        var dir = parts[0];
        var list = new List<string>(parts.Count - 1);
        for (int i = 1; i < parts.Count; i++)
        {
            var name = parts[i];
            // 防御：万一系统给的就是完整路径（某些 shell 扩展会这么干），别再拼一次目录
            list.Add(System.IO.Path.IsPathRooted(name) ? name : System.IO.Path.Combine(dir, name));
        }
        return list.ToArray();
    }
}
