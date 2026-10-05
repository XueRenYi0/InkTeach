# End-to-end check of the InkClass global-hotkey layer.
# Everything is judged from the app's own log, so no screenshots are needed.

param(
    [string]$ExePath = 'D:\文件集中\code\画布测试\Ink-Canvas-Dev\Ink Canvas\bin\InkClass\InkClass.exe'
)

$ErrorActionPreference = 'Continue'
$log = Join-Path (Split-Path $ExePath) 'Log.txt'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class V
{
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, IN[] i, int s);
    [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);

    public delegate bool P(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, sc; public uint f, t; public IntPtr e; }
    [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint d, f, t; public IntPtr e; }
    [StructLayout(LayoutKind.Sequential)] public struct HI { public uint m, l, h; }
    [StructLayout(LayoutKind.Explicit)] public struct U {
        [FieldOffset(0)] public KI ki; [FieldOffset(0)] public MI mi; [FieldOffset(0)] public HI hi; }
    [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public U u; }

    static IN K(ushort vk, bool up) {
        var i = new IN(); i.type = 1; i.u.ki.vk = vk; i.u.ki.f = up ? 2u : 0u; return i; }

    public static void Combo(params ushort[] vks) {
        var l = new List<IN>();
        foreach (var v in vks) l.Add(K(v, false));
        for (int i = vks.Length - 1; i >= 0; i--) l.Add(K(vks[i], true));
        SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(IN)));
    }

    public static IntPtr Find(int pid) {
        IntPtr best = IntPtr.Zero; long ba = 0;
        EnumWindows((h, l) => {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == (uint)pid && IsWindowVisible(h)) {
                R r; if (GetWindowRect(h, out r)) {
                    long a = (long)(r.Rr - r.L) * (r.B - r.T);
                    if (a > ba) { ba = a; best = h; } } }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@

Get-Process -Name InkClass -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath) -PassThru
Start-Sleep -Seconds 14

$h = [V]::Find($p.Id)
Write-Host ("画板窗口 = 0x{0:X}" -f $h.ToInt64())
if ($h -eq [IntPtr]::Zero) { throw "找不到画板主窗口" }

Write-Host ""
Write-Host "--- 1. 注册结果 ---"
Select-String -Path $log -Pattern 'GlobalHotkey.*注册成功' |
    ForEach-Object { Write-Host ("   " + ($_.Line -replace '^.*\[Event\] ', '')) }

Write-Host ""
Write-Host "--- 2. 最小化画板（确保无键盘焦点）后，真实按键 Ctrl+Alt+Shift+1 ---"
[V]::ShowWindow($h, 6) | Out-Null      # SW_MINIMIZE
Start-Sleep -Seconds 2
[V]::Combo(0x11, 0x12, 0x10, 0x31)     # Ctrl + Alt + Shift + 1
Start-Sleep -Seconds 2

Write-Host ""
Write-Host "--- 3. 外部程序用 PostMessage 依次调用 101..106 ---"
foreach ($id in 101, 102, 103, 104, 105, 106) {
    [V]::PostMessage($h, 0x0312, [IntPtr]$id, [IntPtr]0) | Out-Null
    Start-Sleep -Milliseconds 250
}
Start-Sleep -Seconds 1

Write-Host ""
Write-Host "--- 触发日志 ---"
$hits = Select-String -Path $log -Pattern 'GlobalHotkey.*触发'
if (-not $hits) {
    Write-Host "   没有任何触发记录（失败）"
} else {
    $hits | ForEach-Object { Write-Host ("   " + ($_.Line -replace '^.*\[Event\] ', '')) }
}
Write-Host ""
Write-Host ("触发次数 = " + @($hits).Count + " （期望 7：1 次按键 + 6 次 PostMessage）")

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
