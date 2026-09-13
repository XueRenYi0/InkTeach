# Drives an app with a fixed synthetic drawing gesture and reports what it cost.
#
# Everything measured here is observable from outside the process, so the same
# numbers are meaningful for an app we can instrument and one we cannot.

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$ProcessName = '',
    [int]$Strokes = 20,
    [int]$PointsPerStroke = 40,
    [int]$MsPerPoint = 6,
    [int]$SettleSeconds = 6,
    [switch]$Maximize,          # maximise the window before drawing
    [switch]$Topmost,           # pin the window on top so synthetic clicks reach it
    [string]$Label = '',
    [string]$ShotPath = ''      # optional: save a screenshot after the burst
    ,[int]$PreHotkeyId = 0      # optional: PostMessage(WM_HOTKEY, id) before drawing
    ,[string[]]$AppArgs = @()   # optional: arguments for the target exe
    ,[string]$PreClicks = ''    # optional: "x,y;x,y" clicks before drawing (menu setup)
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Bench
{
    public static class Win32
    {
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after,
            int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

        /// 把窗口设为置顶。鼠标消息是按 Z 序派发的，所以置顶之后即使窗口
        /// 没有键盘焦点，合成点击也能落到它身上——而 SetForegroundWindow
        /// 在“调用者不是前台进程”时会被系统静默拒绝，不能依赖它。
        public static void MakeTopmost(IntPtr h)
            => SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        /// PowerShell 默认不是 DPI 感知的，GetSystemMetrics 会返回被缩放过的
        /// 逻辑尺寸（本机 2880x1800 会报成 1440x900），于是按它算出来的
        /// SendInput 坐标只有真实值的一半，手势全部落在左上角。
        /// 必须在读取任何尺寸之前把进程设成 Per-Monitor V2。
        public static void MakeDpiAware() => SetProcessDpiAwarenessContext(new IntPtr(-4));
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

        /// Process.MainWindowHandle is unreliable for WPF apps, so pick the
        /// largest visible top-level window owned by the process instead.
        public static IntPtr FindMainWindow(int pid)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((h, l) =>
            {
                uint owner;
                GetWindowThreadProcessId(h, out owner);
                if (owner != (uint)pid || !IsWindowVisible(h)) return true;
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                long area = (long)(r.R - r.L) * (r.B - r.T);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public MOUSEINPUT mi; }

        public const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
        public const uint ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

        // All-double parameters: PowerShell's overload binding is unreliable
        // with mixed int/uint signatures.
        public static void MouseMove(double x, double y, double vx, double vy,
                                     double vw, double vh, int extra)
        {
            var inp = new INPUT[1];
            inp[0].type = 0;
            inp[0].mi.dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1.0, vw - 1));
            inp[0].mi.dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1.0, vh - 1));
            inp[0].mi.dwFlags = MOVE | ABSOLUTE | VIRTUALDESK | (uint)extra;
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
'@

function Get-GpuAverage([int]$ProcessId, [int]$Seconds) {
    $job = Start-Job -ScriptBlock {
        param($pid_, $secs)
        try {
            Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' `
                        -SampleInterval 1 -MaxSamples $secs -ErrorAction Stop |
                ForEach-Object { $_.CounterSamples } |
                Where-Object { $_.InstanceName -like "pid_${pid_}_*engtype_3d" } |
                ForEach-Object { $_.CookedValue }
        } catch { }
    } -ArgumentList $ProcessId, $Seconds
    return $job
}

function Summarize([double[]]$values) {
    if (-not $values -or $values.Count -eq 0) { return 0 }
    $sum = 0; foreach ($v in $values) { $sum += $v }
    return $sum / $values.Count
}

[Bench.Win32]::MakeDpiAware()
$vx = [Bench.Win32]::GetSystemMetrics(76)
$vy = [Bench.Win32]::GetSystemMetrics(77)
$vw = [Bench.Win32]::GetSystemMetrics(78)
$vh = [Bench.Win32]::GetSystemMetrics(79)

Write-Host "=== $Label ==="
Write-Host "屏幕 $vw x $vh"

<# Both apps are launched the same way: no console window, output to a file. #>
$outFile = Join-Path $env:TEMP ("bench_out_" + [Guid]::NewGuid().ToString('N') + ".txt")
$errFile = Join-Path $env:TEMP ("bench_err_" + [Guid]::NewGuid().ToString('N') + ".txt")
$proc = Start-Process -FilePath $ExePath -ArgumentList $AppArgs -PassThru -WindowStyle Hidden `
                      -RedirectStandardOutput $outFile -RedirectStandardError $errFile
if ($ProcessName -eq '') { $ProcessName = [IO.Path]::GetFileNameWithoutExtension($ExePath) }

Start-Sleep -Seconds $SettleSeconds
$proc.Refresh()
if ($proc.HasExited) { throw "进程已退出" }

$p = Get-Process -Id $proc.Id
$idleWs = $p.WorkingSet64 / 1MB
$idlePriv = $p.PrivateMemorySize64 / 1MB

Write-Host ("  空闲：工作集 {0:N1} MB，提交 {1:N1} MB" -f $idleWs, $idlePriv)

if ($Maximize) {
    $h = [Bench.Win32]::FindMainWindow($proc.Id)
    if ($h -eq [IntPtr]::Zero) { Start-Sleep -Seconds 5; $h = [Bench.Win32]::FindMainWindow($proc.Id) }
    if ($h -eq [IntPtr]::Zero) { throw "找不到主窗口" }
    [Bench.Win32]::ShowWindow($h, 3) | Out-Null      # SW_MAXIMIZE
    [Bench.Win32]::SetForegroundWindow($h) | Out-Null
    if ($Topmost) { [Bench.Win32]::MakeTopmost($h) | Out-Null }
    Start-Sleep -Seconds 2
}

# ---- idle CPU over 3 s -----------------------------------------------------
$c0 = $p.TotalProcessorTime
Start-Sleep -Seconds 3
$p.Refresh()
$idleCpu = ($p.TotalProcessorTime - $c0).TotalMilliseconds / 3000.0 * 100.0
Write-Host ("  空闲 CPU：{0:F1} %" -f $idleCpu)

$job = Get-GpuAverage $proc.Id 3
Start-Sleep -Seconds 4
$gpuIdle = Receive-Job $job; Remove-Job $job -Force
Write-Host ("  空闲 GPU(3D)：{0:F1} %" -f (Summarize $gpuIdle))

# ---- drawing burst ---------------------------------------------------------
$moves = $Strokes * $PointsPerStroke
$expectedMs = $moves * $MsPerPoint
$desc = "  开始绘制：{0} 笔 x {1} 点，共 {2} 次移动，约 {3:N1} 秒" -f $Strokes, $PointsPerStroke, $moves, ($expectedMs / 1000.0)
Write-Host $desc

$cpuBefore = $p.TotalProcessorTime
$wsBefore = $p.WorkingSet64
$sw = [Diagnostics.Stopwatch]::StartNew()

$job = Get-GpuAverage $proc.Id ([int][Math]::Ceiling($expectedMs / 1000.0) + 3)

# Raise the timer resolution so Start-Sleep can actually hit 6 ms. At the
# default ~15.6 ms tick the gesture runs three times slower than intended.
[Bench.Win32]::timeBeginPeriod(1) | Out-Null

# Some apps need UI steps before they accept ink (Inkeys: open the floating
# toolbar, then pick the pen). Coordinates are absolute screen pixels.
if ($PreClicks -ne '') {
    foreach ($pair in $PreClicks.Split(';')) {
        $xy = $pair.Split(',')
        if ($xy.Count -lt 2) { continue }
        $cx = [int]$xy[0]; $cy = [int]$xy[1]
        [Bench.Win32]::MouseMove($cx, $cy, $vx, $vy, $vw, $vh, 0)
        Start-Sleep -Milliseconds 200
        [Bench.Win32]::MouseMove($cx, $cy, $vx, $vy, $vw, $vh, 2)   # LEFTDOWN
        Start-Sleep -Milliseconds 150
        [Bench.Win32]::MouseMove($cx, $cy, $vx, $vy, $vw, $vh, 4)   # LEFTUP
        Start-Sleep -Milliseconds 500
        Write-Host "  预点击 ($cx,$cy)"
    }
}

# Some apps start with their canvas hidden (InkClass ships isAutoHideCanvas=true),
# so the gesture would land on nothing. Its own hotkey can reveal the canvas.
if ($PreHotkeyId -ne 0) {
    $board = [Bench.Win32]::FindMainWindow($proc.Id)
    if ($board -ne [IntPtr]::Zero) {
        [Bench.Win32]::PostMessage($board, 0x0312, [IntPtr]$PreHotkeyId, [IntPtr]0) | Out-Null
        Start-Sleep -Seconds 2
        Write-Host "  已通过 WM_HOTKEY($PreHotkeyId) 让目标进入可书写状态"
    }
}

$marginX = 120
$marginY = 160
$usableW = $vw - $marginX * 2
$usableH = $vh - $marginY * 2 - 200
$cols = 4
$rows = [Math]::Ceiling($Strokes / $cols)
$cellW = [int]($usableW / $cols)
$cellH = [int]($usableH / [Math]::Max(1, $rows))

for ($s = 0; $s -lt $Strokes; $s++) {
    $cx = $vx + $marginX + ($s % $cols) * $cellW + 40
    $cy = $vy + $marginY + [int][Math]::Floor($s / $cols) * $cellH + 40

    [Bench.Win32]::MouseMove($cx, $cy, $vx, $vy, $vw, $vh, 0)
    Start-Sleep -Milliseconds 30
    [Bench.Win32]::MouseMove($cx, $cy, $vx, $vy, $vw, $vh, [Bench.Win32]::LEFTDOWN)

    for ($k = 1; $k -le $PointsPerStroke; $k++) {
        $t = $k / [double]$PointsPerStroke
        $px = $cx + $t * ($cellW - 90)
        $py = $cy + [Math]::Sin($t * 6.283185) * 55
        [Bench.Win32]::MouseMove([int]$px, [int]$py, $vx, $vy, $vw, $vh, 0)
        Start-Sleep -Milliseconds $MsPerPoint
    }
    [Bench.Win32]::MouseMove([int]($cx + ($cellW - 90)), [int]$cy, $vx, $vy, $vw, $vh, [Bench.Win32]::LEFTUP)
      Start-Sleep -Milliseconds 60
  }

[Bench.Win32]::timeEndPeriod(1) | Out-Null
$sw.Stop()
$p.Refresh()
$cpuDelta = ($p.TotalProcessorTime - $cpuBefore).TotalMilliseconds
$wall = $sw.Elapsed.TotalMilliseconds

$gpuDraw = Receive-Job $job; Remove-Job $job -Force

# If the app could not keep up, it keeps burning CPU after input stops while it
# works through the backlog. That is the clearest "it fell behind" signal.
$cpuAfterStart = $p.TotalProcessorTime
Start-Sleep -Seconds 2
$p.Refresh()
$cpuAfter = ($p.TotalProcessorTime - $cpuAfterStart).TotalMilliseconds / 2000.0 * 100.0

$cores = [Environment]::ProcessorCount
Write-Host ("  绘制耗时：{0:N1} 秒，实际输入速率 {1:F0} 点/秒" -f ($wall / 1000), ($moves / ($wall / 1000.0)))
$cpuText = "  绘制 CPU：{0:N0} ms 总处理器时间 = {1:F1} % 单核 = {2:F2} % 全机" -f $cpuDelta, ($cpuDelta / $wall * 100), ($cpuDelta / $wall * 100 / $cores)
Write-Host $cpuText
Write-Host ("  绘制 GPU(3D)：{0:F1} %" -f (Summarize $gpuDraw))
Write-Host ("  输入停止后 2 秒的 CPU：{0:F1} % 单核（接近空闲说明没积压，明显偏高说明没跟上）" -f $cpuAfter)

Start-Sleep -Seconds 3
$p.Refresh()
$memText = "  绘制后：工作集 {0:N1} MB（+{1:N1}），提交 {2:N1} MB（+{3:N1}）" -f ($p.WorkingSet64 / 1MB), (($p.WorkingSet64 - $wsBefore) / 1MB), ($p.PrivateMemorySize64 / 1MB), (($p.PrivateMemorySize64 - $idlePriv) / 1MB)
Write-Host $memText

# 截图存证：确认这一轮到底有没有真的画上去（内存和 CPU 都可能骗人）
if ($ShotPath) {
    $abs = [IO.Path]::GetFullPath($ShotPath)
    New-Item -ItemType Directory -Force -Path (Split-Path $abs) | Out-Null
    & "D:\文件集中\code\批注\src\InkProbe\bin\Release\net8.0-windows\InkProbe.exe" --screenshot $abs | Out-Null
    Write-Host ("  截图存证：$abs")
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

# If the app writes a log, its own view of what happened is worth seeing.
try {
    $lines = Get-Content $outFile -ErrorAction SilentlyContinue
    if ($lines -and $lines.Count -gt 0) {
        Write-Host "  --- 程序自身日志（最后 6 行）---"
        $lines | Select-Object -Last 6 | ForEach-Object { Write-Host ("    " + $_) }
    }
} catch { }

Write-Host ""

