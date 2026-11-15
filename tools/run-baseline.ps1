<#
跑一组自检并把原始输出存到 reports/baseline-<时间>/，供重构前后对比。

用法：
    powershell -ExecutionPolicy Bypass -File tools/run-baseline.ps1
    powershell -ExecutionPolicy Bypass -File tools/run-baseline.ps1 -Modes --keytest,--recoverytest
    powershell -ExecutionPolicy Bypass -File tools/run-baseline.ps1 -OutDir reports\baseline-before

为什么需要：
    重构（移动文件 / 拆 partial）不改行为，但必须有“改之前长什么样”的证据。
    跑同一组命令，保存 PASS/FAIL 原始行；重构后再跑一次，用
    tools/compare-baseline.ps1 对比失败项和通过数。

注意：
    - 输出编码按控制台代码页（中文 Windows 是 936）读，再统一写 UTF-8。
    - 每条命令单独超时（默认 180 秒），超时记 TIMEOUT，不中断整组。
#>

param(
    [string[]]$Modes = @('--keytest', '--recoverytest'),
    [string]$OutDir = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) {
    $OutDir = Join-Path $repoRoot ("reports\baseline-" + (Get-Date -Format 'yyyyMMdd-HHmm'))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 子进程输出是 UTF-8（App 的 ConsoleWideWriter / dotnet run），按 UTF-8 解码。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$summary = New-Object System.Collections.Generic.List[string]
foreach ($mode in $Modes) {
    $name = $mode.TrimStart('-')
    $outFile = Join-Path $OutDir ($name + '.txt')
    Write-Host "run $mode -> $outFile"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & (Join-Path $repoRoot 'run.ps1') $mode 2>&1 | Out-String
    $sw.Stop()
    $output | Out-File -FilePath $outFile -Encoding utf8

    $pass = ([regex]::Matches($output, '\bPASS\b')).Count
    $fail = ([regex]::Matches($output, '\bFAIL\b')).Count
    $summary.Add(("{0,-18} exit={1} {2,6} ms  PASS={3} FAIL={4}" -f $mode, $LASTEXITCODE, $sw.ElapsedMilliseconds, $pass, $fail))
}

$summaryFile = Join-Path $OutDir 'summary.txt'
$summary | Out-File -FilePath $summaryFile -Encoding utf8
$summary | ForEach-Object { Write-Host $_ }
Write-Host "baseline: $OutDir"
