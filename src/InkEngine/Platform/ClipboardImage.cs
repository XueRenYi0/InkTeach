using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 图片的剪贴板读写（走 Windows 的 CF_DIB / CF_DIBV5）。
///
/// ## 只做 DIB，不做 PNG
///
/// 剪贴板上常见的图片格式有 CF_BITMAP（GDI 句柄）、CF_DIB（原始像素）、
/// CF_DIBV5（带 alpha）、以及各程序自己注册的 "PNG"。这里只写 CF_DIB、
/// 只读 CF_DIB/CF_DIBV5，理由：
///
///   · **写**：CF_DIB 的兼容面最广，而且我们的像素本来就是 BGRA，一个字节
///     都不用转；写 PNG 要现编一个 PNG 编码器（核心不引依赖，见 ImageData）。
///   · **读**：截图工具、浏览器、Office、微信贴图**都会**同时给 CF_DIB，
///     只给 PNG 的极少。真遇到"只给 PNG"的源头，会在下面返回 false，
///     由调用方明确告诉用户"剪贴板里没有可用的图像"——而不是静默粘贴出一张空白。
///
/// ## 两个经典坑
///
///   ① **DIB 默认自下而上**（biHeight 为正数）。不翻行的话，粘进来的图是
///      倒的——而且只有内容不对称时才看得出来，很容易漏测。
///   ② **剪贴板是全局独占资源**。别的程序正占用时 OpenClipboard 会失败，
///      这不是错误，等几毫秒重试就行（重试 5 次，共约 50ms）。
/// </summary>
internal static class ClipboardImage
{
    /// <summary>
    /// 把一张 BGRA 图放到剪贴板（截图之后自动调用）。
    /// <paramref name="bgra"/> 是**自上而下**的原始像素，会按 DIB 的规矩翻行。
    /// </summary>
    public static bool SetImage(byte[] bgra, int w, int h)
    {
        if (bgra == null || w <= 0 || h <= 0) return false;
        if (bgra.Length < (long)w * h * 4) return false;

        if (!OpenWithRetry(IntPtr.Zero)) return false;
        try
        {
            if (!Native.EmptyClipboard()) return false;

            int headerSize = 40;                        // BITMAPINFOHEADER
            int pixelBytes = w * h * 4;
            int total = headerSize + pixelBytes;

            IntPtr hMem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)total);
            if (hMem == IntPtr.Zero) return false;

            IntPtr p = Native.GlobalLock(hMem);
            if (p == IntPtr.Zero) { Native.GlobalFree(hMem); return false; }
            try
            {
                var header = new Native.BITMAPINFOHEADER
                {
                    biSize = headerSize,
                    biWidth = w,
                    biHeight = h,                        // 正数 = 自下而上
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = (int)Native.BI_RGB,
                    biSizeImage = pixelBytes,
                };
                Marshal.StructureToPtr(header, p, false);

                // 逐行倒着写：内存里是自上而下，DIB 要自下而上。
                for (int row = h - 1; row >= 0; row--)
                    Marshal.Copy(bgra, row * w * 4, p + headerSize + (h - 1 - row) * w * 4, w * 4);
            }
            finally { Native.GlobalUnlock(hMem); }

            if (Native.SetClipboardData(Native.CF_DIB, hMem) == IntPtr.Zero)
            {
                // 交出去失败，内存还归我们，必须自己释放（成功的话所有权归系统）。
                Native.GlobalFree(hMem);
                return false;
            }
            return true;
        }
        finally { Native.CloseClipboard(); }
    }

    /// <summary>
    /// 从剪贴板读一张图。返回的像素是**自上而下**的 BGRA；
    /// <paramref name="hasAlpha"/> 表示这份数据真的带 alpha（CF_DIBV5）。
    /// </summary>
    public static bool TryGetImage(out byte[] bgra, out int w, out int h, out bool hasAlpha)
    {
        bgra = null; w = 0; h = 0; hasAlpha = false;
        if (!OpenWithRetry(IntPtr.Zero)) return false;
        try
        {
            uint fmt = Native.CF_DIB;
            if (Native.IsClipboardFormatAvailable(Native.CF_DIBV5)) { fmt = Native.CF_DIBV5; hasAlpha = true; }
            else if (!Native.IsClipboardFormatAvailable(Native.CF_DIB)) return false;

            IntPtr hMem = Native.GetClipboardData(fmt);
            if (hMem == IntPtr.Zero) return false;

            IntPtr p = Native.GlobalLock(hMem);
            if (p == IntPtr.Zero) return false;
            try
            {
                return Parse(p, Native.GlobalSize(hMem), out bgra, out w, out h);
            }
            finally { Native.GlobalUnlock(hMem); }
        }
        finally { Native.CloseClipboard(); }
    }

    /// <summary>
    /// 解析 DIB。只支持**未压缩的 32 位与 24 位**——这两者覆盖了截屏工具、
    /// Office、浏览器、微信的实际输出；调色板格式（8 位及以下）和 RLE 压缩
    /// 早就不用于屏幕截图，遇到了直接拒绝，比"猜着解"安全。
    ///
    /// 注：24 位 DIB 的每行按 4 字节对齐，多出来的填充字节要跳过。
    /// </summary>
    private static bool Parse(IntPtr p, UIntPtr size, out byte[] bgra, out int w, out int h)
    {
        bgra = null; w = 0; h = 0;
        long total = (long)size;
        if (total < 40) return false;

        int headerSize = Marshal.ReadInt32(p, 0);
        int width = Marshal.ReadInt32(p, 4);
        int height = Marshal.ReadInt32(p, 8);
        int bitCount = Marshal.ReadInt16(p, 14);
        int compression = Marshal.ReadInt32(p, 16);

        if (width <= 0 || height == 0) return false;
        if (compression != 0) return false;                       // BI_RGB
        if (bitCount != 32 && bitCount != 24) return false;
        if (headerSize < 40 || headerSize > total) return false;

        bool topDown = height < 0;
        int hh = Math.Abs(height);
        if ((long)width * hh > 100_000_000) return false;         // 防呆：一亿像素以上直接拒

        int srcStride = ((width * bitCount / 8) + 3) / 4 * 4;
        long need = headerSize + (long)srcStride * hh;
        if (need > total) return false;

        var dst = new byte[(long)width * hh * 4];
        var row = new byte[srcStride];
        for (int y = 0; y < hh; y++)
        {
            int srcRow = topDown ? y : hh - 1 - y;                // DIB 默认自下而上
            Marshal.Copy(p + headerSize + srcRow * srcStride, row, 0, srcStride);
            int dstOff = y * width * 4;
            if (bitCount == 32)
            {
                Buffer.BlockCopy(row, 0, dst, dstOff, width * 4);
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    dst[dstOff + x * 4 + 0] = row[x * 3 + 0];
                    dst[dstOff + x * 4 + 1] = row[x * 3 + 1];
                    dst[dstOff + x * 4 + 2] = row[x * 3 + 2];
                    dst[dstOff + x * 4 + 3] = 255;
                }
            }
        }
        bgra = dst; w = width; h = hh;
        return true;
    }

    /// <summary>剪贴板可能被别的程序占着（这是常态，不是异常）。重试几次。</summary>
    private static bool OpenWithRetry(IntPtr owner)
    {
        for (int i = 0; i < 5; i++)
        {
            if (Native.OpenClipboard(owner)) return true;
            System.Threading.Thread.Sleep(10);
        }
        return false;
    }
}
