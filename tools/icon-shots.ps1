<#
把 .ico 里的每一档**拆出来实拍**成一张核对图（reports/图标-实拍七档.png 那张就是这么来的）。

用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/icon-shots.ps1 `
        -Ico src\InkTeach\assets\InkTeach.ico -Out reports\图标-实拍七档.png

为什么要有这条：
    设计稿上的图标是"按尺寸现画"的，装进程序之后是 makeicon 缩下去的**真产物**。
    两者在 16/24/32 这几档上会不一样（缩放的抗锯齿 vs 矢量重画）。
    任务栏认不认，只能看真产物的实拍 —— 这就是这张图的用处。

⚠ 本机是 Windows PowerShell 5.1，脚本文件要存成 **UTF-8 带 BOM**，否则中文注释会乱码报错。
#>

param(
    [Parameter(Mandatory = $true)][string]$Ico,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$icoPath = if ([System.IO.Path]::IsPathRooted($Ico)) { $Ico } else { Join-Path $repoRoot $Ico }
$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repoRoot $Out }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null

# ---- 拆帧：ICO 目录项 16 字节，Vista 以后每帧就是一张 PNG ----
$bytes = [System.IO.File]::ReadAllBytes($icoPath)
$count = [BitConverter]::ToUInt16($bytes, 4)
$frames = @{}
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + 16 * $i
    $w = [int]$bytes[$o]; if ($w -eq 0) { $w = 256 }
    $len = [BitConverter]::ToUInt32($bytes, $o + 8)
    $off = [BitConverter]::ToUInt32($bytes, $o + 12)
    $ms = New-Object System.IO.MemoryStream
    $ms.Write($bytes, $off, $len)
    $frames[$w] = [System.Drawing.Image]::FromStream($ms)
}
Write-Host ("拆出 " + $frames.Count + " 档：" + (($frames.Keys | Sort-Object) -join '/'))

# ---- 排版：上行真实尺寸（底对齐）＋ 256 单摆；下行三档放大（最近邻，看的才是真像素）----
$W = 760; $H = 560
$bmp = New-Object System.Drawing.Bitmap $W, $H
$bmp.SetResolution(96, 96)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 64, 72))

$baseline = 250
$x = 24
foreach ($s in 16, 24, 32, 48, 64) {
    $g.DrawImage($frames[$s], $x, $baseline - $s, $s, $s)
    $g.DrawString("${s}px", $font, $brush, $x - 4, $baseline + 6)
    $x += [Math]::Max(34, $s + 16)
}
$g.DrawImage($frames[128], 300, $baseline - 128, 128, 128)
$g.DrawString('128px', $font, $brush, 336, $baseline + 6)
$g.DrawImage($frames[256], 460, 24, 256, 256)
$g.DrawString('256px', $font, $brush, 560, 288)

$zy = 340
$zx = 24
foreach ($zoom in @(@(16, 4), @(32, 3), @(64, 2))) {
    $s = $zoom[0]; $k = $zoom[1]
    $w = $s * $k
    $g.DrawImage($frames[$s], $zx, $zy, $w, $w)
    $g.DrawString("${s}px x${k}", $font, $brush, $zx, $zy + $w + 4)
    $zx += $w + 40
}
$g.Dispose()
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "已写出 $outPath"
