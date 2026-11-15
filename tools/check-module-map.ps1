<#
校验 docs/module-map.md 里引用的 .cs 文件是否都还在。

用法：
    powershell -ExecutionPolicy Bypass -File tools/check-module-map.ps1

什么时候跑：
    - 发版前（publish.ps1 之前）
    - 大改 / 拆文件之后
    - 改完 module-map.md 想确认没写错文件名

为什么只查文件、不查符号：
    符号名（方法/类型）用正则从 C# 里提不可靠（多行签名、表达式体、局部函数）。
    这里只保证“地图指向的文件存在”；符号定位交给 grep 和 section-map。
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mapFile = Join-Path $repoRoot 'docs\module-map.md'
if (-not (Test-Path $mapFile)) { throw "找不到 $mapFile" }

$text = [System.IO.File]::ReadAllText($mapFile)
$refs = [regex]::Matches($text, '`([A-Za-z0-9_./\\-]+\.cs)`') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$all = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }
$byLeaf = @{}
foreach ($f in $all) { $byLeaf[$f.Name] = $f.FullName }

$missing = @()
foreach ($r in $refs) {
    $leaf = Split-Path $r -Leaf
    if (-not $byLeaf.ContainsKey($leaf)) { $missing += $r }
}

"module-map refs: $($refs.Count)"
if ($missing.Count -eq 0) {
    "OK: all referenced .cs files exist"
    exit 0
}
"FAIL: missing $($missing.Count) file(s):"
$missing | ForEach-Object { "  - $_" }
exit 1
