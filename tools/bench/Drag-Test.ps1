# Sends one synthetic stroke to whatever window is under the given point and
# reports whether the target process actually did anything about it.

param(
    [int]$X = 700,
    [int]$Y = 700,
    [int]$Length = 600,
    [int]$Steps = 30,
    [string]$ProcessName = 'InkClass'
)

$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class D
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, IN[] i, int s);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, sc; public uint f, t; public IntPtr e; }
    [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint d, f, t; public IntPtr e; }
    [StructLayout(LayoutKind.Sequential)] public struct HI { public uint m, l, h; }
    [StructLayout(LayoutKind.Explicit)] public struct U {
        [FieldOffset(0)] public KI ki; [FieldOffset(0)] public MI mi; [FieldOffset(0)] public HI hi; }
    [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public U u; }

    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    const uint ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

    public static void Dpi() => SetProcessDpiAwarenessContext(new IntPtr(-4));

    public static void Mouse(double x, double y, int vx, int vy, int vw, int vh, uint extra)
    {
        var i = new IN[1];
        i[0].type = 0;
        i[0].u.mi.dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1.0, vw - 1));
        i[0].u.mi.dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1.0, vh - 1));
        i[0].u.mi.f = MOVE | ABSOLUTE | VIRTUALDESK | extra;
        SendInput(1, i, Marshal.SizeOf(typeof(IN)));
    }

    public static int OwnerPidAt(int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        uint pid; GetWindowThreadProcessId(WindowFromPoint(p), out pid);
        return (int)pid;
    }
}
'@

[D]::Dpi()
$vx = [D]::GetSystemMetrics(76); $vy = [D]::GetSystemMetrics(77)
$vw = [D]::GetSystemMetrics(78); $vh = [D]::GetSystemMetrics(79)

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "目标进程 $ProcessName 没在运行"; exit 1 }

Write-Host ("目标进程 $ProcessName (PID {0})，屏幕 {1}x{2}" -f $proc.Id, $vw, $vh)
Write-Host ("起点 ({0},{1}) 处的窗口属于 PID {2}（应为 {3}）" -f $X, $Y, [D]::OwnerPidAt($X, $Y), $proc.Id)

$ws0 = $proc.WorkingSet64
$c0 = $proc.TotalProcessorTime

[D]::Mouse($X, $Y, $vx, $vy, $vw, $vh, 0)
Start-Sleep -Milliseconds 60
[D]::Mouse($X, $Y, $vx, $vy, $vw, $vh, [uint32]2)   # LEFTDOWN

for ($k = 1; $k -le $Steps; $k++) {
    $t = $k / [double]$Steps
    $px = $X + $t * $Length
    $py = $Y + [Math]::Sin($t * 6.283185) * 80
    [D]::Mouse($px, $py, $vx, $vy, $vw, $vh, 0)
    Start-Sleep -Milliseconds 12
}
[D]::Mouse($X + $Length, $Y, $vx, $vy, $vw, $vh, [uint32]4)  # LEFTUP
Start-Sleep -Seconds 2

$proc.Refresh()
Write-Host ("CPU 增量 {0:N1} ms，工作集 {1:N2} MB → {2:N2} MB（+{3:N2}）" -f `
    ($proc.TotalProcessorTime - $c0).TotalMilliseconds, ($ws0/1MB), ($proc.WorkingSet64/1MB), (($proc.WorkingSet64-$ws0)/1MB))
