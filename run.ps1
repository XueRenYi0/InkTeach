# 开发期启动器：把便携版 .NET 10 SDK 垫进 PATH，再 `dotnet run`。
#
# 为什么需要它：这台机器的全局 dotnet 是 9.x（C:\Program Files\dotnet），
# 直接 `dotnet run` 会报 NETSDK1045（当前 SDK 不支持 net10.0）。
# .NET 10 SDK 装在 %USERPROFILE%\.dotnet10，这里**只给本脚本拉起的进程**垫 PATH，
# 不改系统环境变量；publish.ps1 里也垫了同一层。
#
# 用法：
#   .\run.ps1                 # 启动批注（开发 / 试手感）
#   .\run.ps1 --keytest       # 参数原样透传（各种自检、--penlive 30 都行）
#   .\run.ps1 -- --nohud      # 带 `--` 也行，开头那个 `--` 会被吃掉
$ErrorActionPreference = 'Stop'

$sdkDir = Join-Path $env:USERPROFILE '.dotnet10'
if (Test-Path (Join-Path $sdkDir 'dotnet.exe')) {
    $env:PATH = "$sdkDir;$env:PATH"
    if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = $sdkDir }
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# 没有 param 块：所有参数（包括 `-` 开头的）都进 $args，原样透传给程序。
$appArgs = @($args)
if ($appArgs.Count -ge 1 -and $appArgs[0] -eq '--') {
    $appArgs = @($appArgs | Select-Object -Skip 1)
}

if ($appArgs.Count -gt 0) {
    & dotnet run --project src/InkTeach -c Release -- @appArgs
} else {
    & dotnet run --project src/InkTeach -c Release
}
exit $LASTEXITCODE
