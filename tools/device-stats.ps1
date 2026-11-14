# 设备使用量统计（2026-11-04）
#
# 数据来源：`XueRenYI0/InkTeach-Logs` 私有仓的 `devices/<installId>.json`。
#   · 每台机器**首次启动**生成一张卡，之后每次启动覆盖 `last` 日期；
#   · 用户在「设置 → 隐私」关掉设备统计后，卡片会被删除（所以是“愿意上报”的下限）。
#
# 用法：
#   .\tools\device-stats.ps1
#
# 前置：%USERPROFILE%\.gitcode-token 里有能读该仓的 GitCode 令牌（和发版同一个）。
# 只读：浅克隆 + sparse-checkout 只取 devices/，跑完即删，不在本地留设备数据。

[CmdletBinding()]
param(
    [string]$Repo = "XueRenYI0/InkTeach-Logs"
)

$ErrorActionPreference = "Stop"
$tokFile = Join-Path $env:USERPROFILE ".gitcode-token"
if (-not (Test-Path $tokFile)) { throw "找不到 $tokFile（GitCode 私人令牌）" }
$tok = (Get-Content $tokFile -Raw).Trim()
if (-not $tok) { throw "$tokFile 是空的" }

$dst = Join-Path $env:LOCALAPPDATA ("Temp\opencode\inkteach-logs-" + [guid]::NewGuid().ToString("N").Substring(0, 6))
$env:GIT_TERMINAL_PROMPT = "0"
$url = "https://XueRenYI0:$tok@gitcode.com/$Repo.git"
$out = & git clone --depth 1 --filter=blob:none --sparse $url $dst 2>&1
if ($LASTEXITCODE -ne 0) {
    $out | Select-Object -Last 2 | ForEach-Object { $_ -replace [regex]::Escape($tok), "***" }
    throw "克隆失败（令牌没权限读 $Repo？）"
}

try {
    Push-Location $dst
    & git sparse-checkout set devices 2>&1 | Out-Null
    Pop-Location

    $dir = Join-Path $dst "devices"
    $files = @(Get-ChildItem $dir -Filter *.json -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { throw "devices/ 里没有设备卡" }

    $today = Get-Date
    $rows = @(foreach ($f in $files) {
        try {
            $j = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $last = $null
            if ($j.last -match "^\d{4}-\d{2}-\d{2}$") {
                $last = [datetime]::ParseExact($j.last, "yyyy-MM-dd", $null)
            }
            [pscustomobject]@{
                Ver     = [string]$j.ver
                Last    = $last
                First   = [string]$j.first
                Model   = [string]$j.model
                Lang    = [string]$j.lang
                Region  = [string]$j.region
                Fps     = if ($null -ne $j.fps) { [double]$j.fps } else { 0 }
                Slow150 = if ($null -ne $j.slow150) { [int]$j.slow150 } else { 0 }
                WsMb    = if ($null -ne $j.wsMb) { [double]$j.wsMb } else { 0 }
            }
        } catch { }
    })

    Write-Host ""
    Write-Host ("设备卡总数（愿意上报的安装量）: " + $files.Count) -ForegroundColor Cyan
    Write-Host ("成功解析: " + $rows.Count)
    foreach ($d in 0, 1, 7, 14, 30, 60) {
        $n = @($rows | Where-Object { $_.Last -and ($today.Date - $_.Last.Date).TotalDays -le $d }).Count
        $pct = if ($rows.Count -gt 0) { [math]::Round(100.0 * $n / $rows.Count) } else { 0 }
        Write-Host ("  最近 {0,2} 天启动过: {1,4} 台（{2}%）" -f $d, $n, $pct)
    }

    Write-Host ""
    Write-Host "版本分布:" -ForegroundColor Cyan
    $rows | Group-Object Ver | Sort-Object Count -Descending | Select-Object -First 12 |
        ForEach-Object { Write-Host ("  {0,-8} {1}" -f $_.Name, $_.Count) }

    Write-Host ""
    Write-Host "首次安装月份:" -ForegroundColor Cyan
    $rows | Where-Object { $_.First -match "^\d{4}-\d{2}" } |
        Group-Object { $_.First.Substring(0, 7) } | Sort-Object Name |
        ForEach-Object { Write-Host ("  {0}  {1}" -f $_.Name, $_.Count) }

    Write-Host ""
    Write-Host "国家/地区（Windows 区域，不是 IP）:" -ForegroundColor Cyan
    $rows | Group-Object { if ($_.Region) { $_.Region } else { "未知" } } |
        Sort-Object Count -Descending | Select-Object -First 12 |
        ForEach-Object { Write-Host ("  {0,-8} {1}" -f $_.Name, $_.Count) }

    Write-Host ""
    Write-Host "语言 Top 8:" -ForegroundColor Cyan
    $rows | Group-Object { if ($_.Lang) { $_.Lang } else { "未知" } } |
        Sort-Object Count -Descending | Select-Object -First 8 |
        ForEach-Object { Write-Host ("  {0,-10} {1}" -f $_.Name, $_.Count) }

    Write-Host ""
    Write-Host "机型 Top 8:" -ForegroundColor Cyan
    $rows | Where-Object { $_.Model } | Group-Object Model | Sort-Object Count -Descending |
        Select-Object -First 8 | ForEach-Object { Write-Host ("  {0}  {1}" -f $_.Name, $_.Count) }

    Write-Host ""
    Write-Host "慢帧 Top 10（slow150 多 = 卡；fps 仅参考，空闲时可能很低）:" -ForegroundColor Cyan
    $rows | Sort-Object Slow150 -Descending | Select-Object -First 10 |
        ForEach-Object {
            Write-Host ("  slow150={0,4}  fps={1,6:0.0}  ws={2,6:0}MB  {3} / {4} / {5}" -f
                $_.Slow150, $_.Fps, $_.WsMb, $_.Ver, $(if ($_.Region) { $_.Region } else { "?" }), $_.Model)
        }
} finally {
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force -ErrorAction SilentlyContinue }
}
