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
            // ① 先只管 z 序：把它提到置顶层，位置先别动。
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

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

            // ③ **居中要"盯一会儿"，不能指望移一次就位**（这一步踩过）：
            //
            // 系统对话框显示之后**自己还会再摆一次位置**：实测移完读到 (779,330)，
            // 1.2 秒后再问它已经在 (0,0) 了（全屏截图坐实：对话框在最左上角）。
            // 所以这里前两秒盯着——偏了就再移一次；连着三次（约 0.45 秒）都在位才算稳。
            // 这段时间用户刚看见框，不可能已经在拖它了，所以不会跟人抢。
            int moves = 0, okStreak = 0;
            for (int i = 0; i < 14; i++)
            {
                if (!CenterOnce(hwnd, out int wantX, out int wantY)) break;
                Thread.Sleep(150);
                GetWindowRect(hwnd, out var cur);
                if (Math.Abs(cur.Left - wantX) <= 4 && Math.Abs(cur.Top - wantY) <= 4)
                {
                    if (++okStreak >= 3)
                    {
                        Log($"对话框 {hwnd}：已居中到 ({wantX},{wantY})（移了 {moves} 次后稳住）");
                        break;
                    }
                }
                else okStreak = 0;
                moves++;
            }
            Log($"对话框 {hwnd}：顶到最前（原前台 {fg}，线程 {fgTid} / 本线程 {myTid}）");
        }
        catch (Exception ex) { Log("顶对话框出错：" + ex.Message); }
    }

    /// <summary>
    /// 按最近那块显示器的**工作区**把对话框摆到中间（工作区装不下就贴顶，
    /// 免得标题栏被推到屏幕外、拖都拖不动）。返回 false = 没量到显示器，位置没动。
    /// </summary>
    private static bool CenterOnce(IntPtr hwnd, out int wantX, out int wantY)
    {
        wantX = wantY = int.MinValue;
        GetWindowRect(hwnd, out var r);
        IntPtr mon = MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return false;

        var wa = mi.rcWork;
        int w = r.Width, h = r.Height;
        wantX = wa.Left + (wa.Width - w) / 2;
        wantY = wa.Top + (h >= wa.Height ? 0 : (wa.Height - h) / 2);
        SetWindowPos(hwnd, HwndTopmost, wantX, wantY, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
        return true;
    }

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
    /// **格式差别就写在文件类型那一行**（用户 2026-09-17 问"要不要让用户知道 png 是透明底、
    /// jpg 是白底？"）：那是他唯一一定会看的一行，比在别处写提示都管用。
    /// </summary>
    public static string AskForImage(IntPtr owner, string suggestedName, int defaultFilterIndex,
                                     out int filterIndex)
    {
        filterIndex = defaultFilterIndex;
        StartDialogWatcher();          // 先起看门线程：对话框一出现就把它顶到最前
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
            lpstrTitle = "导出选中的内容",
            lpstrDefExt = ExportFormats.ExtensionFor(defaultFilterIndex).TrimStart('.').Split(';')[0],
            Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST,
        };
        if (!GetSaveFileNameW(ofn)) return null;
        filterIndex = ofn.nFilterIndex;
        var path = (ofn.lpstrFile ?? "").Trim().TrimEnd('\0');
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
