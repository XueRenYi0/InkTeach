# 一键跑 Wintab 探针，并把结果存成文件（助手会直接去读这个文件）
#
# 用法：右键本文件 → "使用 PowerShell 运行"
#       或者在 PowerShell 里敲：  & "$env:USERPROFILE\Desktop\软件\跑-Wintab探针.ps1"
#
# 跑起来之后：**请用手写笔在板子上划 12 秒**，
#             先悬停划几下，再真的压着笔尖由轻到重划。
# 屏幕上会有一行"第 n/12 秒"的计数——看着它在动就说明在测。

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (Test-Path (Join-Path $root 'InkTeach\src\InkTeach\InkTeach.csproj')) {
    $repo = Join-Path $root 'InkTeach'
} else {
    $repo = $root          # 脚本被放在仓库里也能用
}

$exe = Join-Path $repo 'src\InkTeach\bin\Release\net10.0-windows\InkTeach.exe'
if (-not (Test-Path $exe)) {
    Write-Host "找不到 InkTeach.exe：" -ForegroundColor Red
    Write-Host "  $exe"
    Write-Host "先编译一次再试。"
    Read-Host "`n按回车退出"
    exit 1
}

# exe 需要 DOTNET_ROOT（这台机器上 dotnet 是装在用户目录的）
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet" }

$outDir = Join-Path $repo 'reports\perf-ink'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$out = Join-Path $outDir 'wintab-用户实测.txt'

Write-Host ""
Write-Host "===== Wintab 探针 =====" -ForegroundColor Cyan
Write-Host "跑起来后请**用手写笔在板子上划 12 秒**（先悬停、再压着笔尖由轻到重划）。" -ForegroundColor Yellow
Write-Host ""

# Tee-Object：既能在这儿看到，也存进文件
& $exe '--wintabprobe' 2>&1 | Tee-Object -FilePath $out

Write-Host ""
Write-Host "===== 跑完了 =====" -ForegroundColor Cyan
Write-Host "结果已存到："
Write-Host "  $out" -ForegroundColor Green
Write-Host ""
Write-Host "回去跟助手说一声「跑完了」就行，它会自己去读这个文件。" -ForegroundColor Yellow
Read-Host "`n按回车关闭"
