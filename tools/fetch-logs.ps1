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

# 列 logs/ 目录
$null = curl.exe -s --max-time 60 -o $tmpJson "$api/contents/logs`?access_token=$tok"
$list = Read-GcJson $tmpJson
if ($null -eq $list -or $list.error_code) {
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
