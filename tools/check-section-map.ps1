<#
校验 docs/section-map.md 是否过期：每节标题里的“N 行”必须等于文件当前行数。

用法：
    powershell -ExecutionPolicy Bypass -File tools/check-section-map.ps1

什么时候跑：
    - 拆完文件 / 大改之后
    - 发版前（和 check-module-map 一起）
    - 怀疑“地图行号不对”时

为什么需要：
    section-map 是自动生成的，但生成后代码还会改。行数一变，行号就可能漂，
    AI 按地图跳过去会读错段。这个脚本只回答一件事：**地图是不是旧的**。
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mapFile = Join-Path $repoRoot 'docs\section-map.md'
if (-not (Test-Path $mapFile)) { throw "找不到 $mapFile" }

$lines = [System.IO.File]::ReadAllLines($mapFile)
$stale = @()
$missing = @()
$checked = 0

foreach ($l in $lines) {
    if ($l -match '^##\s+(.+?)（([\d,]+)\s*行）\s*$') {
        $rel = $Matches[1].Trim()
        $claimed = [int]($Matches[2] -replace ',', '')
        $full = Join-Path $repoRoot ($rel -replace '/', '\')
        $checked++
        if (-not (Test-Path $full)) { $missing += $rel; continue }
        $actual = [System.IO.File]::ReadAllLines($full).Length
        if ($actual -ne $claimed) {
            $stale += ("{0}: map={1} actual={2}" -f $rel, $claimed, $actual)
        }
    }
}

"section-map entries: $checked"
if ($missing.Count -eq 0 -and $stale.Count -eq 0) {
    "OK: section-map is fresh"
    exit 0
}
if ($missing.Count -gt 0) {
    "MISSING files:"
    $missing | ForEach-Object { "  - $_" }
}
if ($stale.Count -gt 0) {
    "STALE entries (rerun tools/gen-section-map.ps1):"
    $stale | ForEach-Object { "  - $_" }
}
exit 1
