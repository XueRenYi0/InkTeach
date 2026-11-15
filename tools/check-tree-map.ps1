<#
校验 docs/tree-map.md 是否过期：重新生成一份，和已提交版本对比（忽略“生成日期”行）。

用法：
    powershell -ExecutionPolicy Bypass -File tools/check-tree-map.ps1

什么时候跑：
    - 新增/移动/改名/拆分文件之后
    - 发版前（和 check-module-map / check-section-map 一起）
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mapFile = Join-Path $repoRoot 'docs\tree-map.md'
if (-not (Test-Path $mapFile)) { throw "找不到 $mapFile" }

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('inkteach-tree-map-' + [guid]::NewGuid().ToString('N') + '.md')
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'gen-tree-map.ps1') -Out $tmp | Out-Null
    if (-not (Test-Path $tmp)) { throw 'gen-tree-map 没有生成临时文件' }

    $old = [System.IO.File]::ReadAllLines($mapFile) | Where-Object { $_ -notmatch '^> 生成：' }
    $new = [System.IO.File]::ReadAllLines($tmp) | Where-Object { $_ -notmatch '^> 生成：' }

    if (($old -join "`n") -eq ($new -join "`n")) {
        "OK: tree-map is fresh ($($new.Count) lines)"
        exit 0
    }

    "STALE: docs/tree-map.md 与源码不一致（重跑 tools/gen-tree-map.ps1）"
    $diff = Compare-Object $old $new -SyncWindow 5
    $diff | Select-Object -First 20 | ForEach-Object {
        $side = if ($_.SideIndicator -eq '<=') { 'committed' } else { 'source' }
        "  [$side] $($_.InputObject)"
    }
    exit 1
}
finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}
