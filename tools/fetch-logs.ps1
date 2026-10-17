<#
  把日志收集仓（InkTeach-Logs）里的日志列出来（可选全部下载到本机）。
  这是「设置 → 上传日志…」的另一头：老师一键上传，我们在这边取。

  存储：日志以**仓库文件**形式放在 `logs/` 目录（不是发行版附件——私有仓的
  附件下载接口回 403，走不通；见 调研\日志上传-方案.md 的"踩坑记录"）。

  用法：
    .\tools\fetch-logs.ps1                 # 只列
    .\tools\fetch-logs.ps1 -Download       # 全部下载到 .\logs-download\
    .\tools\fetch-logs.ps1 -Download -OutDir D:\logs

  前置：%USERPROFILE%\.gitcode-token（GitCode 私人令牌，需有日志仓的读权限）。
#>
param(
    [switch]$Download,
    [switch]$Devices,
    [string]$OutDir = "logs-download"
)

$ErrorActionPreference = 'Stop'
$owner = 'XueRenYI0'
$repo  = 'InkTeach-Logs'
$api   = "https://api.gitcode.com/api/v5/repos/$owner/$repo"

$tokFile = Join-Path $env:USERPROFILE '.gitcode-token'
if (-not (Test-Path $tokFile)) { throw "找不到 $tokFile（把 GitCode 私人令牌存进去，一行、不要有多余字符）" }
$tok = (Get-Content $tokFile -Raw).Trim()
if (-not $tok) { throw "$tokFile 是空的" }

$root = Split-Path $PSScriptRoot -Parent
$tmpJson = Join-Path $env:TEMP "inkteach-fetch-logs.json"

function Read-GcJson([string]$file) {
    $raw = ''
    try { $raw = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8) } catch { }
    if (-not $raw) { return $null }
    try { return ($raw | ConvertFrom-Json) } catch { return $null }
}

# -Devices：列 devices/（设备卡：台数 / 型号 / 最近活跃）；默认：列 logs/（日志）
if ($Devices)
{
    $null = curl.exe -s --max-time 60 -o $tmpJson "$api/contents/devices`?access_token=$tok"
    $dl = Read-GcJson $tmpJson
    # 接口返回**数组**时，PowerShell 会把元素的 `.error_code` 变成「$null 数组」，
    # 而 `if (@($null,$null))` 为真 → 有设备卡也会误报“还没有”。2026-10-17 修。
    if ($null -eq $dl -or ($dl -isnot [array] -and $dl.error_code)) {
        Write-Host "还没有设备卡（每台机器第一次运行后会自动传一张，之后每天更新）。" -ForegroundColor Yellow
        Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue
        return
    }
    $files = @($dl | Where-Object { $_.type -eq 'file' })
    $cards = @()
    foreach ($f in $files) {
        $null = curl.exe -s --max-time 60 -o $tmpJson "$api/contents/devices/$($f.name)?access_token=$tok"
        $one = Read-GcJson $tmpJson
        if ($one -and $one.content) {
            try {
                $json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($one.content -replace '\s', '')))
                $cards += ($json | ConvertFrom-Json)
            } catch { }
        }
    }
    Write-Host "设备卡：$($cards.Count) 台（每台一份，每日更新）" -ForegroundColor Green
    foreach ($g in ($cards | Group-Object model | Sort-Object Count -Descending)) {
        Write-Host ("  {0,3} 台  {1}" -f $g.Count, $g.Name)
    }
    $today = Get-Date -Format 'yyyy-MM-dd'
    $recent = @($cards | Where-Object { $_.last -eq $today }).Count
    Write-Host "  最近一天有活动：$recent 台" -ForegroundColor DarkGray
    Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue
    return
}

# 列 logs/ 目录
$null = curl.exe -s --max-time 60 -o $tmpJson "$api/contents/logs`?access_token=$tok"
$list = Read-GcJson $tmpJson
if ($null -eq $list -or ($list -isnot [array] -and $list.error_code)) {
    $msg = if ($list -and $list.error_message) { $list.error_message } else { "（读不出来）" }
    throw "列日志失败：$msg —— 若第一次用：得先有一条日志传上去（logs/ 目录不存在时就这样）"
}
$files = @($list | Where-Object { $_.type -eq 'file' })
Write-Host "日志收集仓：$($files.Count) 份日志（logs/ 目录）" -ForegroundColor Green
$i = 0
foreach ($f in $files)
{
    $i++
    Write-Host ("  {0,3}. {1}" -f $i, $f.name)
}

if ($Download -and $files.Count -gt 0)
{
    $dir = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($f in $files)
    {
        $dest = Join-Path $dir $f.name
        if (Test-Path $dest) { Write-Host "  已在：$($f.name)" -ForegroundColor DarkGray; continue }
        $null = curl.exe -s --max-time 300 -o $tmpJson "$api/contents/logs/$([uri]::EscapeDataString($f.name))?access_token=$tok"
        $one = Read-GcJson $tmpJson
        if ($null -eq $one -or -not $one.content) { Write-Host "  下载失败：$($f.name)" -ForegroundColor Red; continue }
        $bytes = [Convert]::FromBase64String(($one.content -replace '\s', ''))
        [System.IO.File]::WriteAllBytes($dest, $bytes)
        Write-Host ("  下载 {0}（{1:N0} KB）" -f $f.name, ($bytes.Length / 1KB))
    }
    Write-Host "完成：$dir" -ForegroundColor Green
}

Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue
