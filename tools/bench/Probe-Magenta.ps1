# 外部像素探针：不依赖被测程序自己的截屏代码，从**另一个进程**盯着一块屏幕
# 区域采样，报告洋红（品红）像素落在哪几行。
#
# 用途：当程序自己的自检说"墨没出现在该出现的地方"时，用这个从外面确认——
# 墨是真没画出来，还是画到别的位置去了。
#
#   pwsh -File tools/bench/Probe-Magenta.ps1 -Seconds 12 -X 800 -Y 0 -W 200 -H 1000

param(
    [int]$Seconds = 10,
    [int]$X = 800,
    [int]$Y = 0,
    [int]$W = 200,
    [int]$H = 1000,
    [int]$IntervalMs = 40
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Probe
{
    public static class Gdi
    {
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h,
                                                                  IntPtr s, int sx, int sy, int rop);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter,
                       biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER h; public int c1, c2, c3; }

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, int usage,
                                                     out IntPtr bits, IntPtr section, int offset);

        public const int DIB_RGB_COLORS = 0, SRCCOPY = 0x00CC0020;

        /// <summary>
        /// 抓一块区域，返回洋红（品红）像素的包围盒与总数：
        /// [minX, minY, maxX, maxY, count]（区域坐标系，未命中时全是 -1/0）。
        /// </summary>
        public static int[] MagentaBox(int x, int y, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            var bi = new BITMAPINFO
            {
                h = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
                    biCompression = 0,
                }
            };
            IntPtr bits;
            IntPtr dib = CreateDIBSection(mem, ref bi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            if (dib != IntPtr.Zero && bits != IntPtr.Zero)
            {
                IntPtr old = SelectObject(mem, dib);
                BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY);
                byte[] buf = new byte[w * h * 4];
                Marshal.Copy(bits, buf, 0, buf.Length);
                for (int row = 0; row < h; row++)
                {
                    for (int col = 0; col < w; col++)
                    {
                        int i = (row * w + col) * 4;
                        if (buf[i + 2] > 170 && buf[i + 1] < 90 && buf[i] > 170)
                        {
                            count++;
                            if (col < minX) minX = col;
                            if (col > maxX) maxX = col;
                            if (row < minY) minY = row;
                            if (row > maxY) maxY = row;
                        }
                    }
                }
                SelectObject(mem, old);
                DeleteObject(dib);
            }
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            return new[] { minX, minY, maxX, maxY, count };
        }
    }
}
'@

Write-Host ("采样区域 x={0}..{1}  y={2}..{3}，{4} 秒" -f $X, ($X + $W), $Y, ($Y + $H), $Seconds)
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $b = [Probe.Gdi]::MagentaBox($X, $Y, $W, $H)
    $t = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    if ($b[4] -eq 0) { Write-Host ("  [{0,6:N2}s] 无" -f $t) }
    else {
        Write-Host ("  [{0,6:N2}s] x={1}..{2}  y={3}..{4}  宽 {5}  高 {6}  共 {7} 像素" -f `
            $t, ($X + $b[0]), ($X + $b[2]), ($Y + $b[1]), ($Y + $b[3]),
            ($b[2] - $b[0] + 1), ($b[3] - $b[1] + 1), $b[4])
    }
    Start-Sleep -Milliseconds $IntervalMs
}
