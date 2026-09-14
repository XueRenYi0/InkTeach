# 端到端验证"滚动会不会把已经写好的墨弄丢"——**走真实用户路径**：
# 合成鼠标画一笔 → 真滚轮事件滚几格 → 从屏幕像素上看墨还在不在、跟在哪儿。
#
# 为什么要从外面量：程序自己的自检里，有的直接给相机赋值、有的走滚轮，两条
# 路径在分块缓存上的处理并不一样；而且"看得见的像素"只有从屏幕外面量才算数。
#
# 默认笔色是正红（InkPalette.PenDefault = 0.95,0.18,0.18），所以这里直接数
# 红像素的包围盒，不依赖桌面背景。
#
#   pwsh -File tools/bench/Check-ScrollKeepsInk.ps1

param(
    [string]$AppRoot = 'D:\文件集中\code\批注',
    [string]$OutDir = 'reports/stress-0914/scrollink',
    [int]$Notches = 3            # 往下滚几格（一格 = 72 逻辑像素 = 144 物理像素）
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace ScrollInk
{
    public static class Win32
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public MOUSEINPUT mi; }

        // 注意常量名别和方法名撞（PowerShell 查成员不区分大小写：
        // 常量 WHEEL 会把方法 Wheel 挡住，报"没有名为 Wheel 的方法"）。
        public const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEELFLAG = 0x0800;
        public const uint ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

        public static void MakeDpiAware() => SetProcessDpiAwarenessContext(new IntPtr(-4));

        private static void Send(int x, int y, uint flags, uint data, int vx, int vy, int vw, int vh)
        {
            var inp = new INPUT[1];
            inp[0].type = 0;
            inp[0].mi.dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1.0, vw - 1));
            inp[0].mi.dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1.0, vh - 1));
            inp[0].mi.mouseData = data;
            inp[0].mi.dwFlags = flags;
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>画一笔：按下 → 沿直线移动 → 抬起。</summary>
        public static void Drag(int x0, int y0, int x1, int y1, int steps,
                                int vx, int vy, int vw, int vh)
        {
            Send(x0, y0, MOVE | ABSOLUTE | VIRTUALDESK, 0, vx, vy, vw, vh);
            System.Threading.Thread.Sleep(80);
            Send(x0, y0, MOVE | ABSOLUTE | VIRTUALDESK | LEFTDOWN, 0, vx, vy, vw, vh);
            for (int i = 1; i <= steps; i++)
            {
                int x = x0 + (x1 - x0) * i / steps;
                int y = y0 + (y1 - y0) * i / steps;
                Send(x, y, MOVE | ABSOLUTE | VIRTUALDESK, 0, vx, vy, vw, vh);
                System.Threading.Thread.Sleep(10);
            }
            Send(x1, y1, MOVE | ABSOLUTE | VIRTUALDESK | LEFTUP, 0, vx, vy, vw, vh);
        }

        /// <summary>把指针放到 (x,y) 再发滚轮。</summary>
        public static void Wheel(int x, int y, int notches, int vx, int vy, int vw, int vh)
        {
            Send(x, y, MOVE | ABSOLUTE | VIRTUALDESK, 0, vx, vy, vw, vh);
            System.Threading.Thread.Sleep(100);
            Send(x, y, WHEELFLAG | ABSOLUTE | VIRTUALDESK, unchecked((uint)(notches * 120)), vx, vy, vw, vh);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KINPUT { public uint type; public INPUTUNION u; }

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_C = 0x43, VK_Z = 0x5A;

        [DllImport("user32.dll", EntryPoint = "SendInput")]
        private static extern uint SendInputKey(uint n, KINPUT[] inputs, int size);

        private static void Key(ushort vk, bool up)
        {
            var inp = new KINPUT[1];
            inp[0].type = 1;
            inp[0].u.ki.wVk = vk;
            inp[0].u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
            SendInputKey(1, inp, Marshal.SizeOf(typeof(KINPUT)));
        }

        /// <summary>按一次全局快捷键 Ctrl+Alt+<vk>。</summary>
        public static void CtrlAlt(ushort vk)
        {
            Key(VK_CONTROL, false); Key(VK_MENU, false); Key(vk, false);
            System.Threading.Thread.Sleep(60);
            Key(vk, true); Key(VK_MENU, true); Key(VK_CONTROL, true);
        }

        /// <summary>把 32 位 BMP 读成自上而下的 BGRA 字节数组。</summary>
        public static byte[] ReadBmp(string path, out int w, out int h)
        {
            byte[] b = System.IO.File.ReadAllBytes(path);
            w = BitConverter.ToInt32(b, 18);
            h = BitConverter.ToInt32(b, 22);
            int off = BitConverter.ToInt32(b, 10);
            byte[] px = new byte[w * h * 4];
            int stride = w * 4;
            for (int row = 0; row < h; row++)
                Buffer.BlockCopy(b, off + (h - 1 - row) * stride, px, row * stride, stride);
            return px;
        }

        /// <summary>正红像素的包围盒：{minX,minY,maxX,maxY,count}。背景不会被算进来。</summary>
        public static int[] RedBox(byte[] px, int w, int h)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, n = 0;
            for (int y = 0; y < h; y++)
            {
                int o = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = o + x * 4;
                    if (!(px[i + 2] > 170 && px[i + 1] < 110 && px[i] < 110)) continue;   // BGRA
                    n++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return new[] { minX, minY, maxX, maxY, n };
        }
    }
}
'@

[ScrollInk.Win32]::MakeDpiAware()
$vx = [ScrollInk.Win32]::GetSystemMetrics(76)
$vy = [ScrollInk.Win32]::GetSystemMetrics(77)
$vw = [ScrollInk.Win32]::GetSystemMetrics(78)
$vh = [ScrollInk.Win32]::GetSystemMetrics(79)

$OutDir = Join-Path $AppRoot $OutDir
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $AppRoot 'src\InkTeach\bin\Release\net8.0-windows\InkTeach.exe'

$winX = $vx + 400; $winY = $vy + 100; $winW = 1200; $winH = 1500
$shot = Join-Path $OutDir 'region.bmp'

# 注意函数名别叫 Measure：那是 Measure-Object 的别名，别名优先于函数。
function Shot([string]$tag) {
    & $exe --screenshot $shot $winX $winY $winW $winH | Out-Null
    $w = 0; $h = 0
    $px = [ScrollInk.Win32]::ReadBmp($shot, [ref]$w, [ref]$h)
    $r = [ScrollInk.Win32]::RedBox($px, $w, $h)
    if ($r[4] -eq 0) {
        Write-Host ("  [{0}] 屏幕上没有墨" -f $tag)
        return $null
    }
    $b = [pscustomobject]@{
        Tag = $tag; Count = $r[4]
        MinX = $r[0] + $winX; MaxX = $r[2] + $winX
        MinY = $r[1] + $winY; MaxY = $r[3] + $winY
        Width = $r[2] - $r[0] + 1; Height = $r[3] - $r[1] + 1
    }
    Write-Host ("  [{0}] x {1}..{2}（宽 {3}）  y {4}..{5}（高 {6}）  共 {7} 像素" -f `
        $b.Tag, $b.MinX, $b.MaxX, $b.Width, $b.MinY, $b.MaxY, $b.Height, $b.Count)
    return $b
}

Write-Host '启动 InkTeach（交互模式）…'
$proc = Start-Process -FilePath $exe -ArgumentList '--nohud' -PassThru -WindowStyle Hidden -WorkingDirectory $AppRoot `
                     -RedirectStandardOutput (Join-Path $OutDir 'app.txt') `
                     -RedirectStandardError (Join-Path $OutDir 'app.err')
Start-Sleep -Seconds 4

$sx = $vx + 500; $sy = $vy + 1000; $ex = $vx + 1300
Write-Host ("画一笔：({0},{1}) → ({2},{1})" -f $sx, $sy, $ex)
[ScrollInk.Win32]::Drag($sx, $sy, $ex, $sy, 60, $vx, $vy, $vw, $vh)
Start-Sleep -Milliseconds 900
$before = Shot '画完'

$step = 144 * $Notches           # 一格 = 72 逻辑像素 = 144 物理像素
Write-Host ("滚轮往下滚 {0} 格（墨应上移 {1} 物理像素）" -f $Notches, $step)
[ScrollInk.Win32]::Wheel($vx + 900, $vy + 900, -$Notches, $vx, $vy, $vw, $vh)
Start-Sleep -Milliseconds 1000
$down = Shot '往下滚'
if ($down -and $before) {
    Write-Host ("  实际上移 {0} 像素（期望 {1}）；宽度 {2} → {3}" -f `
        ($before.MinY - $down.MinY), $step, $before.Width, $down.Width)
}

Write-Host ("滚轮往上滚回 {0} 格" -f $Notches)
[ScrollInk.Win32]::Wheel($vx + 900, $vy + 900, $Notches, $vx, $vy, $vw, $vh)
Start-Sleep -Milliseconds 1000
$up = Shot '滚回顶部'
if ($up -and $before) {
    Write-Host ("  与原始位置差 {0} 像素；宽度 {1} → {2}" -f `
        ($up.MinY - $before.MinY), $before.Width, $up.Width)
}

# 真实课堂动作：滚下去 → 清空 → 在滚动后的位置再写一笔。
# 这一串会走到"相机不为 0 时整层作废 + 重新光栅化"那条路。
Write-Host '清空（Ctrl+Alt+C）后滚到同一位置再写一笔'
[ScrollInk.Win32]::Wheel($vx + 900, $vy + 900, -$Notches, $vx, $vy, $vw, $vh)
Start-Sleep -Milliseconds 500
[ScrollInk.Win32]::CtrlAlt([ScrollInk.Win32]::VK_C)
Start-Sleep -Milliseconds 800
$cleared = Shot '清空后'
[ScrollInk.Win32]::Drag($sx, $sy, $ex, $sy, 60, $vx, $vy, $vw, $vh)
Start-Sleep -Milliseconds 900
$again = Shot '滚动位置再写一笔'
if ($again) {
    Write-Host ("  新笔宽度 {0}（画的时候是 800），y {1}..{2}" -f $again.Width, $again.MinY, $again.MaxY)
}

Start-Sleep -Milliseconds 200
try { $proc.Kill() } catch { }
Write-Host "截图目录：$OutDir"
