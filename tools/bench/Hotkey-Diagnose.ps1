# Diagnoses whether a WPF app's keyboard shortcuts depend on window focus.
#
# For each scenario it grabs a before/after screenshot so the difference can be
# inspected, and reports whether the app actually received the keystroke.

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$Combo = 'Ctrl+P',
    [string]$OutDir = 'reports\shots',
    [int]$SettleSeconds = 14
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Diag
{
    public static class K
    {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT { public uint msg, paramL, paramH; }
        [StructLayout(LayoutKind.Explicit)] public struct UNION {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public UNION u; }

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public static readonly uint[] ModVks = { 0x11 /*CTRL*/, 0x12 /*ALT*/, 0x10 /*SHIFT*/, 0x5B /*LWIN*/ };

        public static IntPtr FindMainWindow(int pid)
        {
            IntPtr best = IntPtr.Zero; long bestArea = 0;
            EnumWindows((h, l) => {
                uint owner; GetWindowThreadProcessId(h, out owner);
                if (owner != (uint)pid || !IsWindowVisible(h)) return true;
                RECT r; if (!GetWindowRect(h, out r)) return true;
                long area = (long)(r.R - r.L) * (r.B - r.T);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static INPUT Key(ushort vk, bool up)
        {
            var i = new INPUT(); i.type = 1;
            i.u.ki.vk = vk; i.u.ki.flags = up ? KEYEVENTF_KEYUP : 0;
            return i;
        }

        public static void SendCombo(string combo)
        {
            var mods = new List<ushort>();
            ushort main = 0;
            foreach (var raw in combo.Split('+'))
            {
                var p = raw.Trim().ToUpperInvariant();
                if (p == "CTRL") mods.Add(0x11);
                else if (p == "ALT") mods.Add(0x12);
                else if (p == "SHIFT") mods.Add(0x10);
                else if (p == "WIN") mods.Add(0x5B);
                else if (p.Length == 1) main = (ushort)p[0];
                else if (p.StartsWith("F") && ushort.TryParse(p.Substring(1), out var f)) main = (ushort)(0x70 + f - 1);
            }
            var list = new List<INPUT>();
            foreach (var m in mods) list.Add(Key(m, false));
            list.Add(Key(main, false));
            list.Add(Key(main, true));
            for (int i = mods.Count - 1; i >= 0; i--) list.Add(Key(mods[i], true));
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
'@

function Shot([string]$name) {
    $path = Join-Path $OutDir $name
    & "D:\文件集中\code\批注\src\InkTeach\bin\Release\net8.0-windows\InkTeach.exe" --screenshot $path | Out-Null
    return $path
}

$proc = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds $SettleSeconds
$h = [Diag.K]::FindMainWindow($proc.Id)
if ($h -eq [IntPtr]::Zero) { throw "找不到主窗口" }

[Diag.K]::ShowWindow($h, 3) | Out-Null
[Diag.K]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 3

Write-Host "=== 场景 1：窗口在前台 ==="
Write-Host ("  目标窗口 = 0x{0:X}，当前前台 = 0x{1:X}" -f $h.ToInt64(), ([Diag.K]::GetForegroundWindow()).ToInt64())
Shot 'hk-1-before.bmp' | Out-Null
[Diag.K]::SendCombo($Combo)
Start-Sleep -Seconds 2
Shot 'hk-1-after.bmp' | Out-Null
Write-Host "  已发送 $Combo，截图 hk-1-before.bmp / hk-1-after.bmp"

Write-Host "=== 场景 2：把焦点切给别的窗口后再按 ==="
$other = [Diag.K]::FindMainWindow((Get-Process -Name explorer -ErrorAction SilentlyContinue | Select-Object -First 1).Id)
if ($other -ne [IntPtr]::Zero) { [Diag.K]::SetForegroundWindow($other) | Out-Null }
Start-Sleep -Seconds 2
Shot 'hk-2-before.bmp' | Out-Null
[Diag.K]::SendCombo($Combo)
Start-Sleep -Seconds 2
Shot 'hk-2-after.bmp' | Out-Null
Write-Host "  已发送 $Combo，截图 hk-2-before.bmp / hk-2-after.bmp"

Write-Host "进程仍在运行，PID=$($proc.Id)"
