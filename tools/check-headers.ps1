<#
校验“关键文件必须有文件头”：超过 -MinLines 的 .cs 必须在开头 8 行内带 `// [职责]`。

用法：
    powershell -ExecutionPolicy Bypass -File tools/check-headers.ps1
    powershell -ExecutionPolicy Bypass -File tools/check-headers.ps1 -MinLines 1000

为什么：
    tree-map 只摘 `// [职责]`；没有头的文件在树里只剩行数，AI 进文件前不知道它管什么。
    这条规则只要求“大文件/入口文件”，不要求每个小文件都写。
#>

param([int]$MinLines = 600)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src'

$missing = @()
$checked = 0
$files = Get-ChildItem -Path $srcRoot -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }

foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    if ($lines.Length -lt $MinLines) { continue }
    $checked++
    $head = ($lines | Select-Object -First 8) -join "`n"
    if ($head -notmatch '//\s*\[职责\]') {
        $rel = $f.FullName.Substring($repoRoot.Length).TrimStart('\').Replace('\', '/')
        $missing += ("{0} ({1} 行)" -f $rel, $lines.Length)
    }
}

"checked >= $MinLines lines: $checked"
if ($missing.Count -eq 0) {
    "OK: all key files have // [职责]"
    exit 0
}
"FAIL: missing [职责] header: $($missing.Count)"
$missing | Sort-Object | ForEach-Object { "  - $_" }
exit 1
