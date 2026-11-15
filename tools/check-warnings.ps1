<#
ADR-005 D5：编译告警基线。

用法：
    powershell -ExecutionPolicy Bypass -File tools/check-warnings.ps1
    powershell -ExecutionPolicy Bypass -File tools/check-warnings.ps1 -UpdateBaseline

规则：任何告警码的数量超过基线即 fail；基线当前为空（12 条存量已在 P0 清零）。
MSB3026（输出文件被占用）属环境噪声，不计入门禁，但会在输出里提示。
#>

param([switch]$UpdateBaseline)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$baselinePath = Join-Path $PSScriptRoot 'warnings-baseline.json'
$project = Join-Path $repoRoot 'src\InkTeach\InkTeach.csproj'

$output = & dotnet build $project -c Release -v:q 2>&1
$exit = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })

$warningLines = @($lines | Where-Object { $_ -match 'warning\s+(CS|MSB)\d+' })
$codes = @{}
$msb3026 = 0
foreach ($line in $warningLines) {
    if ($line -match 'warning\s+((?:CS|MSB)\d+)') {
        $code = $Matches[1]
        if ($code -eq 'MSB3026') { $msb3026++; continue }
        if (-not $codes.ContainsKey($code)) { $codes[$code] = 0 }
        $codes[$code]++
    }
}

if ($UpdateBaseline) {
    [System.IO.File]::WriteAllText($baselinePath,
        (@{ note = 'ADR-005 D5 warning baseline'; counts = $codes } | ConvertTo-Json -Depth 4),
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "warning baseline updated: $baselinePath"
    exit 0
}

if ($exit -ne 0) {
    Write-Output "FAIL: build failed"
    $lines | Where-Object { $_ -match 'error ' } | Select-Object -First 5 | ForEach-Object { Write-Output "  $_" }
    exit 1
}

$baseline = @{}
if (Test-Path $baselinePath) {
    $json = Get-Content -Raw -Encoding utf8 $baselinePath | ConvertFrom-Json
    if ($json.counts) {
        foreach ($p in $json.counts.PSObject.Properties) { $baseline[$p.Name] = [int]$p.Value }
    }
}

$new = @()
foreach ($code in $codes.Keys) {
    $was = if ($baseline.ContainsKey($code)) { $baseline[$code] } else { 0 }
    if ($codes[$code] -gt $was) { $new += "$code ($was -> $($codes[$code]))" }
}

if ($new.Count -gt 0) {
    Write-Output "FAIL: new build warnings: $($new -join ', ')"
    exit 1
}

$note = if ($msb3026 -gt 0) { " (MSB3026 x$msb3026 ignored: locked output)" } else { '' }
Write-Output "OK: 0 new warnings$note"
exit 0
