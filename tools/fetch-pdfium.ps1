# 取 PDFium 原生库（PDF 渲染用）
#
# PDFium = Chrome 的 PDF 引擎，**BSD-3 许可**（第三方：freetype/harfbuzz/icu 等，全宽松，
# 随包带 licenses\ 目录）。我们直接 P/Invoke 它的 C API，不走托管包装。
#
# 为什么用脚本取、不把 dll 进 git：
#   · dll 7.15 MB，二进制进 git 会永久占仓库历史；
#   · 版本要能一眼说清（下面写死 chromium/8086 = PDFium 157.0.8086.0）。
# 取回来放在 `vendor\pdfium-win-x64\`（已 gitignore）：
#   · 编译时 csproj 会把它拷到输出目录（开发/自检直接用）；
#   · 发布时 publish.ps1 会把它放进 dist（缺了会直接报错，避免漏带）；
#   · 安装包（installer\InkTeach.iss）整目录打包，自动带上。
#
# 用法：
#   .\tools\fetch-pdfium.ps1              # 没有就取，已有就跳过
#   .\tools\fetch-pdfium.ps1 -Force       # 重新取
#
# 版本升级：改下面两行（到 https://github.com/bblanchon/pdfium-binaries/releases 看新版），
# 然后 -Force 重取、跑 `run-selftest.ps1 -Only doctest,pdfprobe` 确认。

[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$Repo = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $Repo 'src\InkTeach\InkTeach.csproj'))) {
    # 脚本可能被从别处调用：再按"自己是仓库根"试一次
    $Repo = $PSScriptRoot
}
if (-not (Test-Path (Join-Path $Repo 'src\InkTeach\InkTeach.csproj'))) {
    throw "找不到仓库（按 $Repo 找了 src\InkTeach\InkTeach.csproj）"
}

# ---- 版本（改这里就是升级）------------------------------------------------------------
$Tag   = 'chromium%2F8086'          # URL 里的 tag（斜杠要编码成 %2F）
$Asset = 'pdfium-win-x64.tgz'       # 不带 V8 的版本（PDF 里的 JS 表单我们用不上，省 8MB）
$VerName = 'PDFium 157.0.8086.0'

$vendor = Join-Path $Repo 'vendor\pdfium-win-x64'
$dll = Join-Path $vendor 'bin\pdfium.dll'

if ((Test-Path $dll) -and -not $Force) {
    $size = [Math]::Round((Get-Item $dll).Length / 1MB, 2)
    Write-Host "pdfium.dll 已就位（$size MB）：$dll" -ForegroundColor Green
    Write-Host "要重取加 -Force。" -ForegroundColor DarkGray
    exit 0
}

$url = "https://github.com/bblanchon/pdfium-binaries/releases/download/$Tag/$Asset"
Write-Host "取 $VerName …" -ForegroundColor Cyan
Write-Host "  $url" -ForegroundColor DarkGray

$ProgressPreference = 'SilentlyContinue'
$tgz = Join-Path $env:TEMP "pdfium-$Asset"
Invoke-WebRequest $url -OutFile $tgz

if (Test-Path $vendor) { Remove-Item $vendor -Recurse -Force }
New-Item -ItemType Directory -Path $vendor -Force | Out-Null

# Windows 自带 tar.exe（bsdtar）
& tar -xzf $tgz -C $vendor
if ($LASTEXITCODE -ne 0) { throw "解包失败（tar 退出码 $LASTEXITCODE）" }
if (-not (Test-Path $dll)) { throw "解包后没找到 $dll —— 包结构可能变了，去 releases 页看一眼" }

$size = [Math]::Round((Get-Item $dll).Length / 1MB, 2)
Write-Host "好了：$dll（$size MB）" -ForegroundColor Green
Write-Host "许可文件同目录：LICENSE（bblanchon 构建脚本 MIT）+ licenses\（PDFium BSD-3 及第三方）" -ForegroundColor DarkGray
Write-Host "下一步：dotnet build 会自动拷到输出目录；发布前 publish.ps1 会校验它存在。" -ForegroundColor DarkGray
