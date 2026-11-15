using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 抓屏幕（截图工具的底座）。**只有抓，没有别的**——尺寸换算、落成
/// 图像对象、写剪贴板分别由调用方和 <see cref="ClipboardImage"/> 负责。
///
/// ## 为什么是 GDI BitBlt，不是 Windows.Graphics.Capture
///
/// 这一版要的是"抓一块矩形，快、稳、不弹权限框"：
///
/// | 做法 | 抓到的内容 | 代价 |
/// |---|---|---|
/// | **GDI BitBlt（本实现）** | 屏幕上**实际显示的合成结果** | 一次 5ms 级；DPI / 多屏要自己换算 |
/// | PrintWindow | 指定窗口的内容 | 目标窗口自绘 / 硬件加速时经常抓到空白 |
/// | Windows.Graphics.Capture | 指定窗口或显示器 | 需要拾取器与用户授权，跨进程权限框会打断上课 |
///
/// 老师要的是"把屏幕这一块拿下来"（连自己 PPT 的动画状态一起），所以
/// **合成结果**正是想要的。代价是抓之前必须把自己的覆盖层藏起来，
/// 否则会把批注一起抓进去——见 <see cref="HiddenOverlay"/>。
/// </summary>
internal static class ScreenCapture
{
    /// <summary>抓一块矩形（**物理像素**，虚拟桌面坐标）。失败返回 null。</summary>
    public static byte[] Grab(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bits = IntPtr.Zero;

        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                // **负高度 = 自上而下**。BMP 的默认是自下而上，写图/贴位图时
                // 每行都要倒一次；一次抓一整屏就是几百万次多余的搬运。
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            },
        };

        IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS,
                                             out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        IntPtr old = Native.SelectObject(memDc, dib);
        bool ok = Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY);

        var buf = new byte[(long)w * h * 4 <= int.MaxValue ? w * h * 4 : 0];
        if (ok && buf.Length > 0) Marshal.Copy(bits, buf, 0, buf.Length);

        Native.SelectObject(memDc, old);
        Native.DeleteObject(dib);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return ok && buf.Length > 0 ? buf : null;
    }

    /// <summary>
    /// 抓屏期间把覆盖层藏起来。
    ///
    /// **必须留出时间**：隐藏窗口是异步的（合成器下一帧才撤掉），立刻 BitBlt
    /// 会抓到半透明残留。实测本机等 ~45ms 就干净了；这里给 60ms 并抽两次
    /// 合成边界（DwmFlush），比"睡死 200ms"快且更可靠。
    ///
    /// 用 using 包起来，异常路径也不会把窗口永久藏掉——藏着的窗口用户看不见，
    /// 会以为程序崩了。
    /// </summary>
    public static IDisposable HiddenOverlay(IReadOnlyList<IntPtr> hwnds)
    {
        foreach (var h in hwnds)
            if (h != IntPtr.Zero) Native.ShowWindow(h, Native.SW_HIDE);
        Native.DwmFlush();
        System.Threading.Thread.Sleep(60);
        Native.DwmFlush();
        return new Restore(hwnds);
    }

    private sealed class Restore : IDisposable
    {
        private readonly IReadOnlyList<IntPtr> _hwnds;
        public Restore(IReadOnlyList<IntPtr> hwnds) => _hwnds = hwnds;

        public void Dispose()
        {
            foreach (var h in _hwnds)
                // SW_SHOWNOACTIVATE：批注层回来时**不许抢焦点**，否则老师
                // 刚截完图，键盘焦点就被我们抢走了（正在放的 PPT 就收不到按键）。
                if (h != IntPtr.Zero) Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
            Native.DwmFlush();
        }
    }
}
