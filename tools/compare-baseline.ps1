<#
对比重构前后两份功能测试报告（Run-FuncSuite.ps1 产出的 summary.json）。

用法：
    powershell -ExecutionPolicy Bypass -File tools/compare-baseline.ps1 `
        -Before reports\baseline-pre\summary.json `
        -After  reports\baseline-post\summary.json

判定（回答“效果和原来是不是一样”）：
    - 新增 FAIL / TIMEOUT        → 不一致（退出码 1）
    - 原来 PASS 的用例不见了      → 不一致（退出码 1）
    - PASS 数减少                → 不一致（退出码 1）
    - 只多出 PASS / 耗时变化      → 一致（退出码 0）
#>

param(
    [Parameter(Mandatory = $true)][string]$Before,
    [Parameter(Mandatory = $true)][string]$After
)

$ErrorActionPreference = 'Stop'

function Load-Report([string]$path) {
    if (-not (Test-Path $path)) { throw "找不到报告：$path" }
    $rows = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $map = @{}
    foreach ($r in $rows) { $map[$r.Case] = $r }
    return $map
}

$b = Load-Report $Before
$a = Load-Report $After

$bad = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

$newFail = @()
$missing = @()
$fixed = @()
$slower = @()

foreach ($case in $b.Keys) {
    if (-not $a.ContainsKey($case)) { $missing += $case; continue }
    $bv = $b[$case].Verdict
    $av = $a[$case].Verdict
    if ($bv -eq 'PASS' -and ($av -eq 'FAIL' -or $av -eq 'TIMEOUT')) { $newFail += $case }
    if ($bv -ne 'PASS' -and $av -eq 'PASS') { $fixed += $case }
    if ($bv -eq 'PASS' -and $av -eq 'PASS') {
        $delta = [double]$a[$case].Seconds - [double]$b[$case].Seconds
        if ($delta -gt [math]::Max(2.0, [double]$b[$case].Seconds * 0.5)) {
            $slower += ("{0}: {1:N1}s -> {2:N1}s" -f $case, $b[$case].Seconds, $a[$case].Seconds)
        }
    }
}
foreach ($case in $a.Keys) {
    if (-not $b.ContainsKey($case)) { $notes.Add("新增用例：$case") }
}

$bPass = @($b.Values | Where-Object { $_.Verdict -eq 'PASS' }).Count
$aPass = @($a.Values | Where-Object { $_.Verdict -eq 'PASS' }).Count

"before: $($b.Count) cases, $bPass PASS"
"after : $($a.Count) cases, $aPass PASS"
""

if ($newFail.Count -gt 0) {
    $bad.Add("新增 FAIL/TIMEOUT：")
    foreach ($x in $newFail) { $bad.Add("  - $x (after=$($a[$x].Verdict): $($a[$x].Key))") }
}
if ($missing.Count -gt 0) {
    $bad.Add("原来有、现在不见了：")
    foreach ($x in $missing) { $bad.Add("  - $x") }
}
if ($aPass -lt $bPass) { $bad.Add("PASS 数减少：$bPass -> $aPass") }

if ($fixed.Count -gt 0) {
    "修复/转绿：$($fixed -join ', ')"
}
if ($slower.Count -gt 0) {
    "明显变慢（> max(2s, 50%)）："
    foreach ($x in $slower) { "  - $x" }
}
foreach ($n in $notes) { $n }

if ($bad.Count -gt 0) {
    ""
    "不一致："
    foreach ($x in $bad) { $x }
    exit 1
}

""
"一致：没有新增失败，PASS 数没有减少。"
exit 0
