# UI 审计：录带真光标的视频 + 抽帧
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\audit-ui.ps1 -Check
#   powershell -ExecutionPolicy Bypass -File scripts\audit-ui.ps1 -Seconds 8
#   powershell -ExecutionPolicy Bypass -File scripts\audit-ui.ps1 -Steps scripts\audit-steps.json
#   powershell -ExecutionPolicy Bypass -File scripts\audit-ui.ps1 -Launch -Steps scripts\audit-steps.json
#
# 输出：
#   reports\video\audit-<时间戳>.mp4   30fps，含系统真光标
#   reports\frames\f-*.png             抽帧，给视觉审核
#   reports\audit-last.json            本次结果摘要

param(
    [int]$Seconds = 6,
    [string]$Steps = '',
    [string]$ProcessName = 'InkTeach',
    [int]$ProcessId = 0,
    [string]$OutDir = 'reports',
    [int]$FrameFps = 4,
    [switch]$Check,
    [switch]$Calibrate,
    [switch]$NoFrames,
    [switch]$Launch,
    [int]$LaunchTimeoutSec = 180
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
if ($Steps -and -not [System.IO.Path]::IsPathRooted($Steps)) { $Steps = Join-Path $root $Steps }

# ---------- ffmpeg ----------
function Resolve-Ffmpeg {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $hit = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($hit) { return $hit.FullName }
    throw '找不到 ffmpeg。先运行：winget install Gyan.FFmpeg'
}

$ffmpeg = Resolve-Ffmpeg

# ---------- Win32 ----------
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeUi {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
}
"@
[NativeUi]::SetProcessDPIAware() | Out-Null

function Get-AppWindow {
    param([string]$Name, [int]$Id = 0)
    $procs = if ($Id -gt 0) {
        Get-Process -Id $Id -ErrorAction SilentlyContinue
    } else {
        Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
    }
    $best = $null; $bestArea = -1; $bestRect = $null
    foreach ($p in @($procs)) {
        if (-not $p -or $p.MainWindowHandle -eq 0) { continue }
        $rect = New-Object NativeUi+RECT
        $ok = $false
        try {
            # DWMWA_EXTENDED_FRAME_BOUNDS = 9，比 GetWindowRect 少算 Win10 的隐形边框
            $ok = ([NativeUi]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$rect, 16) -eq 0)
        } catch { $ok = $false }
        if (-not $ok) { [NativeUi]::GetWindowRect($p.MainWindowHandle, [ref]$rect) | Out-Null }
        $area = ($rect.Right - $rect.Left) * ($rect.Bottom - $rect.Top)
        if ($area -gt $bestArea) { $bestArea = $area; $best = $p; $bestRect = $rect }
    }
    if (-not $best) { return $null }
    # 录屏是抓屏幕区域，窗口被别的程序盖住就会录错。先还原、置顶、置前。
    [NativeUi]::ShowWindow($best.MainWindowHandle, 9) | Out-Null
    [NativeUi]::SetWindowPos($best.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
    [NativeUi]::SetForegroundWindow($best.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 400
    return [pscustomobject]@{
        Process = $best
        Left    = $bestRect.Left
        Top     = $bestRect.Top
        Width   = $bestRect.Right - $bestRect.Left
        Height  = $bestRect.Bottom - $bestRect.Top
    }
}

function Wait-AppWindow {
    param([string]$Name, [int]$Id = 0, [int]$TimeoutSec)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $w = Get-AppWindow -Name $Name -Id $Id
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# ---------- 启动 ----------
if ($Launch) {
    $runPs1 = Join-Path $root 'run.ps1'
    if (-not (Test-Path $runPs1)) { throw "找不到 $runPs1" }
    Write-Host "启动 $ProcessName ..."
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runPs1) `
        -WorkingDirectory $root -WindowStyle Hidden | Out-Null
}

$win = Get-AppWindow -Name $ProcessName -Id $ProcessId
if (-not $win -and $Launch) { $win = Wait-AppWindow -Name $ProcessName -Id $ProcessId -TimeoutSec $LaunchTimeoutSec }

if ($win) {
    $w = $win.Width - ($win.Width % 2)
    $h = $win.Height - ($win.Height % 2)
    Write-Host ("窗口: {0}  {1}x{2} @ ({3},{4})" -f $ProcessName, $w, $h, $win.Left, $win.Top)
} else {
    $w = 0; $h = 0
    Write-Host "窗口未找到: $ProcessName"
}

if ($Check) {
    Write-Host "ffmpeg: $ffmpeg"
    & $ffmpeg -version 2>&1 | Select-Object -First 1
    if (-not $win) { exit 2 }
    exit 0
}

if (-not $win) {
    throw "找不到进程 $ProcessName 的主窗口。先启动应用，或加 -Launch。"
}
if ($w -le 0 -or $h -le 0) { throw '窗口尺寸无效。' }

# ---------- 标定：带坐标网格的截图 ----------
# 用途：给 agent 自己校准 audit-steps.json 的坐标。
# 截图上的 x/y 是「窗口内坐标」，和 steps 文件里的 x/y 同一套。
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($Calibrate) {
    Add-Type -AssemblyName System.Drawing
    $calDir = Join-Path $OutDir 'calibrate'
    New-Item -ItemType Directory -Force -Path $calDir | Out-Null
    $png = Join-Path $calDir ("grid-$stamp.png")
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($win.Left, $win.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(110, 255, 60, 60), 1)
    $font = New-Object System.Drawing.Font('Consolas', 9)
    for ($x = 0; $x -lt $w; $x += 100) {
        $g.DrawLine($pen, $x, 0, $x, $h)
        if ($x % 200 -eq 0) { $g.DrawString("$x", $font, [System.Drawing.Brushes]::Yellow, ($x + 2), 2) }
    }
    for ($y = 0; $y -lt $h; $y += 100) {
        $g.DrawLine($pen, 0, $y, $w, $y)
        if ($y % 200 -eq 0) { $g.DrawString("$y", $font, [System.Drawing.Brushes]::Yellow, 2, ($y + 2)) }
    }
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "标定图: $png"
    Write-Host "窗口内坐标范围: x 0..$w, y 0..$h；网格 100px，标注 200px。"
    exit 0
}

# ---------- 录制 ----------
$videoDir = Join-Path $OutDir 'video'
New-Item -ItemType Directory -Force -Path $videoDir | Out-Null
$video = Join-Path $videoDir "audit-$stamp.mp4"

$ffArgs = @(
    '-y', '-loglevel', 'error',
    '-f', 'gdigrab', '-draw_mouse', '1', '-framerate', '30',
    '-offset_x', "$($win.Left)", '-offset_y', "$($win.Top)",
    '-video_size', "${w}x${h}",
    '-i', 'desktop',
    '-t', "$Seconds",
    '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
    "`"$video`""
)

Write-Host "录制 $Seconds 秒（含真光标）..."
$ffProc = Start-Process -FilePath $ffmpeg -ArgumentList $ffArgs -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 600

# ---------- 动作 ----------
function Move-To([int]$x, [int]$y) {
    [NativeUi]::SetCursorPos($win.Left + $x, $win.Top + $y) | Out-Null
}

function Invoke-Steps {
    param([string]$Path)
    if (-not (Test-Path $Path)) { throw "找不到步骤文件 $Path" }
    $list = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($s in $list) {
        if (-not $s.action) { continue }   # 允许 {"_comment": "..."} 这种纯注释行
        switch ($s.action) {
            'move'      { Move-To $s.x $s.y; Start-Sleep -Milliseconds ($(if ($s.ms) { $s.ms } else { 200 })) }
            'hover'     { Move-To $s.x $s.y; Start-Sleep -Milliseconds ($(if ($s.ms) { $s.ms } else { 600 })) }
            'click'     {
                Move-To $s.x $s.y
                Start-Sleep -Milliseconds 80
                [NativeUi]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 60
                [NativeUi]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 250
            }
            'dblclick'  {
                Move-To $s.x $s.y
                foreach ($i in 1..2) {
                    [NativeUi]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
                    Start-Sleep -Milliseconds 40
                    [NativeUi]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                    Start-Sleep -Milliseconds 80
                }
                Start-Sleep -Milliseconds 250
            }
            'rightclick' {
                Move-To $s.x $s.y
                [NativeUi]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 60
                [NativeUi]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 300
            }
            'drag'      {
                $ms = $(if ($s.ms) { $s.ms } else { 600 })
                $steps = 16
                Move-To $s.x $s.y
                Start-Sleep -Milliseconds 80
                [NativeUi]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
                for ($i = 1; $i -le $steps; $i++) {
                    $nx = [int]($s.x + ($s.x2 - $s.x) * $i / $steps)
                    $ny = [int]($s.y + ($s.y2 - $s.y) * $i / $steps)
                    Move-To $nx $ny
                    Start-Sleep -Milliseconds ([int]($ms / $steps))
                }
                [NativeUi]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 300
            }
            'wheel'     { [NativeUi]::mouse_event(0x0800, 0, 0, [int]$s.delta, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400 }
            'key'       {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.SendKeys]::SendWait($s.keys)
                Start-Sleep -Milliseconds 300
            }
            'wait'      { Start-Sleep -Milliseconds ($(if ($s.ms) { $s.ms } else { 500 })) }
            default     { Write-Warning "未知动作: $($s.action)" }
        }
    }
}

if ($Steps) {
    Write-Host "执行动作: $Steps"
    try { Invoke-Steps -Path $Steps }
    catch { Write-Warning "动作执行中断: $($_.Exception.Message)" }
} else {
    Write-Host "$Seconds 秒内可以手动操作窗口，脚本不做动作。"
}

# ---------- 收尾 ----------
$waitSec = [Math]::Max(5, $Seconds + 20)
$deadline = (Get-Date).AddSeconds($waitSec)
while (-not $ffProc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
if (-not $ffProc.HasExited) { Stop-Process -Id $ffProc.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

if (-not (Test-Path $video)) { throw "录制失败，没有生成 $video" }

# ---------- 抽帧 ----------
$frames = @()
if (-not $NoFrames) {
    $framesDir = Join-Path $OutDir 'frames'
    New-Item -ItemType Directory -Force -Path $framesDir | Out-Null
    Get-ChildItem $framesDir -Filter 'f-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
    $pattern = Join-Path $framesDir 'f-%03d.png'
    & $ffmpeg -y -loglevel error -i $video -vf "fps=$FrameFps" $pattern | Out-Null
    $frames = @(Get-ChildItem $framesDir -Filter 'f-*.png' | Sort-Object Name | Select-Object -ExpandProperty FullName)
}

$summary = [pscustomobject]@{
    time       = (Get-Date).ToString('s')
    process    = $ProcessName
    window     = @{ left = $win.Left; top = $win.Top; width = $w; height = $h }
    video      = $video
    frameCount = $frames.Count
    frames     = $frames
    steps      = $(if ($Steps) { $Steps } else { $null })
}
$summaryPath = Join-Path $OutDir 'audit-last.json'
$json = $summary | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($summaryPath, $json, (New-Object Text.UTF8Encoding $false))

Write-Host ''
Write-Host "视频: $video"
Write-Host "帧数: $($frames.Count)"
Write-Host "摘要: $summaryPath"
