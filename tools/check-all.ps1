<#
ADR-005 D5：一条命令跑完提交前检查。

用法：
    powershell -ExecutionPolicy Bypass -File tools/check-all.ps1

顺序：build 告警基线 → 目录树 → 段落图 → 语义表 → 文件头 → 结构基线。
输出只保留每个检查的一行结论；FAIL 时再附前 3 条明细（token 经济）。
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$checks = @(
    @{ Name = 'build/warnings'; Script = 'check-warnings.ps1' },
    @{ Name = 'tree-map';       Script = 'check-tree-map.ps1' },
    @{ Name = 'section-map';    Script = 'check-section-map.ps1' },
    @{ Name = 'module-map';     Script = 'check-module-map.ps1' },
    @{ Name = 'headers';        Script = 'check-headers.ps1' },
    @{ Name = 'structure';      Script = 'check-structure.ps1' }
)

$failed = 0
$started = Get-Date

foreach ($c in $checks) {
    $scriptPath = Join-Path $PSScriptRoot $c.Script
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath 2>&1
    $code = $LASTEXITCODE
    $text = @($out | ForEach-Object { $_.ToString() })
    if ($code -eq 0) {
        $okLine = @($text | Where-Object { $_ -match '^(OK|state-map|warning baseline)' } | Select-Object -Last 1)
        if (-not $okLine) { $okLine = @('OK') }
        Write-Output ("[OK]   {0,-14} {1}" -f $c.Name, $okLine[0])
    } else {
        $failed++
        Write-Output ("[FAIL] {0,-14} exit={1}" -f $c.Name, $code)
        $text | Where-Object { $_ -match 'FAIL|error|violation|MISSING|missing' } |
            Select-Object -First 3 | ForEach-Object { Write-Output "       $_" }
    }
}

$elapsed = [int]((Get-Date) - $started).TotalSeconds
if ($failed -eq 0) {
    Write-Output "check-all: PASS ($($checks.Count) checks, ${elapsed}s)"
    exit 0
}
Write-Output "check-all: FAIL ($failed/$($checks.Count), ${elapsed}s)"
exit 1
