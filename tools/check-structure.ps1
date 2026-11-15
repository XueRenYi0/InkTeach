<#
ADR-005 D5：结构基线检查。

用法：
    powershell -ExecutionPolicy Bypass -File tools/check-structure.ps1
    powershell -ExecutionPolicy Bypass -File tools/check-structure.ps1 -UpdateBaseline

只报"新增违规"，存量写进 tools/structure-baseline.json：
    1. 新文件 >800 行（ADR-002 的新文件目标上限）
    2. 单文件 >120 行的方法数比基线多
    3. partial 族文件数比基线多（禁止新增 partial；见 ADR-005 D6）
    4. 新字段被 >5 个同族文件引用（状态爆炸半径；见 ADR-005 D1）
    5. 无注释的空 catch（绝对 0；见 ADR-005 D5）

口径与 gen-state-map.ps1 一致：字段 = 4 格缩进 `_camelCase` 声明；方法长度按大括号配平估算。
#>

param([switch]$UpdateBaseline)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src'
$baselinePath = Join-Path $PSScriptRoot 'structure-baseline.json'
$families = @('Engine', 'App', 'Overlay', 'FullUi', 'Model')

$files = @(Get-ChildItem -Path $srcRoot -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    Sort-Object FullName)

function Get-RelPath([string]$full) {
    $full.Substring($repoRoot.Length).TrimStart('\').Replace('\', '/')
}

$filesOver800 = New-Object System.Collections.Generic.List[string]
$heavyMethods = @{}
$partialCounts = @{}
$wideFields = New-Object System.Collections.Generic.List[string]
$uncommentedCatch = New-Object System.Collections.Generic.List[string]

# ---------- 1/2. 文件行数 + 方法长度 ----------
$sig = [regex]::new('^\s{4,}(?:\[[^\]]+\]\s*)?(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|unsafe|extern|partial|new|readonly)\s+)+(?:[\w<>,\[\]\?\.\s]+?)\s+\w+\s*\([^;]*$')

foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    $rel = Get-RelPath $f.FullName

    if ($lines.Length -gt 800) { [void]$filesOver800.Add($rel) }

    $heavy = 0
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if (-not $sig.IsMatch($lines[$i])) { continue }
        $open = -1
        for ($k = $i; $k -lt [Math]::Min($i + 4, $lines.Length); $k++) {
            if ($lines[$k].Contains('{')) { $open = $k; break }
            if ($lines[$k].TrimEnd().EndsWith(';')) { break }
        }
        if ($open -lt 0) { continue }
        $depth = 0; $end = -1
        for ($k = $open; $k -lt $lines.Length; $k++) {
            foreach ($ch in $lines[$k].ToCharArray()) {
                if ($ch -eq '{') { $depth++ }
                elseif ($ch -eq '}') { $depth-- }
            }
            if ($depth -le 0) { $end = $k; break }
        }
        if ($end -ge 0 -and ($end - $open + 1) -gt 120) { $heavy++ }
    }
    if ($heavy -gt 0) { $heavyMethods[$rel] = $heavy }

    # 5. 空 catch（按行判断；多行形式在 fix 时已补成同行注释）
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match 'catch\s*(\([^)]*\))?\s*\{\s*\}' -and $lines[$i] -notmatch '//|/\*') {
            [void]$uncommentedCatch.Add("${rel}:$($i + 1)")
        }
    }
}

# ---------- 3. partial 族文件数 ----------
foreach ($family in $families) {
    $count = @(Get-ChildItem -Path $srcRoot -Recurse -File -Filter "$family*.cs" |
        Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }).Count
    $partialCounts[$family] = $count
}

# ---------- 4. 字段爆炸半径 ----------
$declRegex = [regex]::new(
    '^[ \t]{4,8}(?:(?:private|internal|protected|public|static|readonly|volatile)\s+)*(?:[\w<>,\[\]\?\.]+)\s+(_\w+)\s*(?:=|;)',
    [System.Text.RegularExpressions.RegexOptions]::Multiline)

foreach ($family in $families) {
    $famFiles = @(Get-ChildItem -Path $srcRoot -Recurse -File -Filter "$family*.cs" |
        Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' })
    if ($famFiles.Count -eq 0) { continue }
    $text = @{}
    foreach ($f in $famFiles) { $text[$f.FullName] = [System.IO.File]::ReadAllText($f.FullName) }
    $declared = @{}
    foreach ($f in $famFiles) {
        foreach ($m in $declRegex.Matches($text[$f.FullName])) {
            $declared[$m.Groups[1].Value] = $true
        }
    }
    foreach ($name in $declared.Keys) {
        $pattern = '\b' + [regex]::Escape($name) + '\b'
        $refs = 0
        foreach ($f in $famFiles) { if ($text[$f.FullName] -match $pattern) { $refs++ } }
        if ($refs -gt 5) { [void]$wideFields.Add("$family.$name") }
    }
}

$current = [ordered]@{
    filesOver800 = @($filesOver800 | Sort-Object)
    heavyMethods = $heavyMethods
    partialCounts = $partialCounts
    wideFields = @($wideFields | Sort-Object)
    uncommentedCatch = @($uncommentedCatch | Sort-Object)
}

if ($UpdateBaseline) {
    [System.IO.File]::WriteAllText($baselinePath,
        ($current | ConvertTo-Json -Depth 6),
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "structure baseline updated: $baselinePath"
    exit 0
}

if (-not (Test-Path $baselinePath)) {
    Write-Output "FAIL: missing $baselinePath (run with -UpdateBaseline)"
    exit 1
}

$baseline = Get-Content -Raw -Encoding utf8 $baselinePath | ConvertFrom-Json
$violations = New-Object System.Collections.Generic.List[string]

foreach ($p in $current.filesOver800) {
    if ($baseline.filesOver800 -notcontains $p) { [void]$violations.Add("new file >800 lines: $p") }
}
foreach ($k in $current.heavyMethods.Keys) {
    $was = 0
    if ($baseline.heavyMethods.PSObject.Properties.Name -contains $k) { $was = [int]$baseline.heavyMethods.$k }
    if ($current.heavyMethods[$k] -gt $was) {
        [void]$violations.Add("methods >120 lines increased: $k ($was -> $($current.heavyMethods[$k]))")
    }
}
foreach ($k in $current.partialCounts.Keys) {
    $was = [int]$baseline.partialCounts.$k
    if ($current.partialCounts[$k] -gt $was) {
        [void]$violations.Add("partial family grew: $k ($was -> $($current.partialCounts[$k]))")
    }
}
foreach ($p in $current.wideFields) {
    if ($baseline.wideFields -notcontains $p) { [void]$violations.Add("new field used by >5 files: $p") }
}
foreach ($p in $current.uncommentedCatch) {
    [void]$violations.Add("empty catch without comment: $p")
}

if ($violations.Count -gt 0) {
    Write-Output "FAIL: $($violations.Count) structure violation(s)"
    $violations | Select-Object -First 10 | ForEach-Object { Write-Output "  $_" }
    exit 1
}

Write-Output "OK: structure baseline holds ($($current.filesOver800.Count) large files, $($current.wideFields.Count) wide fields)"
exit 0
