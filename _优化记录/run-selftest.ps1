# InkTeach 一键自检（本仓库内副本）
#
# 用法（在仓库根目录执行，或从任意位置用绝对路径调用）：
#   pwsh -File _优化记录/run-selftest.ps1                 # 全套功能自检
#   pwsh -File _优化记录/run-selftest.ps1 -Only 框选,橡皮  # 只跑名字含这些字的
#   pwsh -File _优化记录/run-selftest.ps1 -Build          # 先重新编译再跑
#   pwsh -File _优化记录/run-selftest.ps1 -Perf           # 性能套件（不判红绿）
#
# ⚠ 两条硬规矩（原委见同目录 baseline.md）：
#   1. **必须用 pwsh（PowerShell 7）**，不能用 powershell.exe（5.1）。
#      套件脚本里 `Get-Content $out -Raw` 没写 -Encoding，而本机 ANSI 代码页是
#      936(GBK)、程序输出却是 UTF-8 → 5.1 下中文判据永远匹配不上，
#      通过的用例会被误判成 DATA（=跑完了但没判据）。
#   2. **编译必须带 -m:1 -nodeReuse:false**。本机默认的多节点 MSBuild 会稳定
#      失败在 NuGet.targets(782,5) Value cannot be null (Parameter 'path1')。

[CmdletBinding()]
param(
    [string[]]$Only = @(),
    [string]$OutDir = '',
    [switch]$Build,
    [switch]$Perf
)

$ErrorActionPreference = 'Stop'

# 仓库根 = 本脚本所在目录的上一级
$Repo = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not (Test-Path (Join-Path $Repo 'src\InkTeach\InkTeach.csproj'))) {
    throw "找不到仓库：$Repo（本脚本应放在仓库内的 _优化记录/ 下）"
}

$exe = Join-Path $Repo 'src\InkTeach\bin\Release\net10.0-windows\InkTeach.exe'

if ($Build -or -not (Test-Path $exe)) {
    Write-Host '==> 编译 -c Release' -ForegroundColor Cyan
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') build (Join-Path $Repo 'src\InkTeach\InkTeach.csproj') `
        -c Release -m:1 -nodeReuse:false -v minimal
    if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
}
if (-not (Test-Path $exe)) { throw "找不到可执行文件：$exe" }

$stamp = Get-Date -Format 'MMdd-HHmmss'
if (-not $OutDir) { $OutDir = if ($Perf) { "reports/perf-$stamp" } else { "reports/func-$stamp" } }

$script = if ($Perf) { 'tools\bench\Run-PerfSuite.ps1' } else { 'tools\bench\Run-FuncSuite.ps1' }
$full   = Join-Path $Repo $script
Write-Host "==> $($PSVersionTable.PSVersion) / $script / out=$OutDir" -ForegroundColor Cyan

# 用 -Command 而不是 -File：`pwsh -File` 传 [string[]] 参数会退化成**一个**字符串，
# 逗号也不拆 → 下游 -like 匹配不上 → 跑 0 项。所以先拆逗号，再用数组语义传下去。
function Q([string]$s) { "'" + $s.Replace("'", "''") + "'" }
$onlyList = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() })
$cmd = "& $(Q $full) -AppRoot $(Q $Repo) -OutDir $(Q $OutDir)"
if ($onlyList.Count -gt 0) { $cmd += " -Only @($(($onlyList | ForEach-Object { Q $_ }) -join ','))" }

& (Get-Command pwsh).Source -NoProfile -ExecutionPolicy Bypass -Command $cmd
$code = $LASTEXITCODE

Write-Host ''
Write-Host "原始输出：$(Join-Path $Repo $OutDir)" -ForegroundColor DarkGray
if ($code -eq 0) { Write-Host '结果：全绿' -ForegroundColor Green }
else { Write-Host "结果：有失败（退出码 $code）。对照 baseline.md 的基线 37/1/1 看是不是本来就红的。" -ForegroundColor Yellow }
exit $code
