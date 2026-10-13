# InkTeach 一键自检
#
# ⚠ **平时不要跑全套**（用户 2026-10-07 定）：39 个用例背靠背、其中几个是故意的
#    重负载基准，整整 7 分钟满速跑、风扇会响。**发布前跑一次就够。**
#    平时改完只想验一小块，用 -Only：
#        .\run-selftest.ps1 -Only widthtest,inputtest     # 只跑这两条（几秒）
#        .\run-selftest.ps1 -Only 橡皮,框选                # 按分组名也行
#
# 用法：
#   .\run-selftest.ps1                     # 全套功能自检（发布前）
#   .\run-selftest.ps1 -Only a,b           # 只跑名字含 a 或 b 的用例
#   .\run-selftest.ps1 -Build              # 先重新编译再跑
#   .\run-selftest.ps1 -Perf               # 性能套件（不判红绿，只看数字）
#
# **跑的过程中按任意键 = 取消**（会把正在跑的那一条连同它的子进程一起杀掉）。
#
# 两条硬规矩（原委见 InkTeach\_优化记录\baseline.md）：
#   1. 必须用 pwsh（PowerShell 7）：Windows PowerShell 5.1 下 Get-Content 按 GBK 解码，
#      而程序输出是 UTF-8 → 中文判据永远匹配不上 → 通过的用例被误判成"没判据"。
#   2. 编译必须带 -m:1 -nodeReuse:false：默认的多节点 MSBuild 会稳定失败在
#      NuGet.targets(782,5) Value cannot be null (Parameter 'path1')。

[CmdletBinding()]
param(
    [string[]]$Only = @(),
    [string]$OutDir = '',
    [switch]$Build,
    [switch]$Perf
)

$ErrorActionPreference = 'Stop'

# 仓库探测：按可能性依次找（脚本可能被放在不同位置）
$Repo = Join-Path $PSScriptRoot 'InkTeach'                     # 桌面\软件\run-selftest.ps1
if (-not (Test-Path (Join-Path $Repo 'src\InkTeach\InkTeach.csproj'))) {
    $Repo = Split-Path $PSScriptRoot -Parent                    # <仓库>\tools\run-selftest.ps1  ← 2026-10-07 搬进 tools\ 后要这一条
}
if (-not (Test-Path (Join-Path $Repo 'src\InkTeach\InkTeach.csproj'))) {
    $Repo = $PSScriptRoot                                       # 直接放在仓库根
}
if (-not (Test-Path (Join-Path $Repo 'src\InkTeach\InkTeach.csproj'))) {
    throw "找不到仓库：$Repo"
}

$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

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

# 用 -Command 而不是 -File：`pwsh -File` 传 [string[]] 参数会退化成一个字符串、
# 逗号也不拆 → -Only 变成 "a,b,c" 一个整体 → -like 匹配不上 → 跑 0 项。
function Q([string]$s) { "'" + $s.Replace("'", "''") + "'" }
$onlyList = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() })
$cmd = "& $(Q $full) -AppRoot $(Q $Repo) -OutDir $(Q $OutDir)"
if ($onlyList.Count -gt 0) { $cmd += " -Only @($(($onlyList | ForEach-Object { Q $_ }) -join ','))" }

$count = if ($onlyList.Count -gt 0) { "只跑：$($onlyList -join ', ')" } else { '全套（发布前再跑）' }
Write-Host "==> $($PSVersionTable.PSVersion) / $script / $count / out=$OutDir" -ForegroundColor Cyan
Write-Host '    **跑的过程中按任意键 = 取消**' -ForegroundColor DarkGray

# 子进程继承本控制台（输出实时可见），同时我们轮询键盘——按键就整棵进程树杀掉。
# 为什么不用 Ctrl+C：PowerShell 的 Ctrl+C 会把子进程留在后台，InkTeach 的窗口还在，
# 下一次跑会因为 DLL 被锁而编译失败（踩过）。
$proc = Start-Process -FilePath (Get-Command pwsh).Source `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd) `
    -PassThru -NoNewWindow

$cancelled = $false
while (-not $proc.HasExited) {
    try {
        if ([Console]::KeyAvailable) {
            [void][Console]::ReadKey($true)
            $cancelled = $true
            break
        }
    } catch { }                       # 控制台不可读（重定向）时忽略，仍可用 Ctrl+C
    Start-Sleep -Milliseconds 120
}

if ($cancelled) {
    Write-Host ''
    Write-Host '==> 收到按键，正在取消（连子进程一起杀）…' -ForegroundColor Yellow
    & taskkill.exe /PID $proc.Id /T /F 2>&1 | Out-Null
    # 兜底：InkTeach 如果还活着，它的窗口会挡住下一次运行、还会锁住 DLL
    Get-Process -Name InkTeach -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host '    已取消。' -ForegroundColor Yellow
    exit 130
}

$code = $proc.ExitCode
Write-Host ''
Write-Host "原始输出：$(Join-Path $Repo $OutDir)" -ForegroundColor DarkGray
if ($code -eq 0) { Write-Host '结果：全绿' -ForegroundColor Green }
else { Write-Host "结果：有失败（退出码 $code）。对照 baseline.md 的基线 37/1/1 看是不是本来就红的。" -ForegroundColor Yellow }
exit $code