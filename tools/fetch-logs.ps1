<#
  把日志收集仓（InkTeach-Logs）里 "logs" 发行版上的日志列出来（可选全部下载到本机）。
  这是「设置 → 上传日志…」的另一头：老师一键上传，我们在这边取。

  用法：
    .\tools\fetch-logs.ps1                 # 只列（名字 + 大小）
    .\tools\fetch-logs.ps1 -Download       # 全部下载到 .\logs-download\
    .\tools\fetch-logs.ps1 -Download -OutDir D:\logs

  前置：%USERPROFILE%\.gitcode-token 里放着 GitCode 私人令牌
        （和发版脚本共用同一个文件；那个令牌要有日志收集仓的读权限）。
#>
param(
    [switch]$Download,
    [string]$OutDir = "logs-download"
)

$ErrorActionPreference = 'Stop'
$owner = 'XueRenYI0'
$repo  = 'InkTeach-Logs'
$tag   = 'logs'
$api   = "https://api.gitcode.com/api/v5/repos/$owner/$repo"

$tokFile = Join-Path $env:USERPROFILE '.gitcode-token'
if (-not (Test-Path $tokFile)) { throw "找不到 $tokFile（把 GitCode 私人令牌存进去，一行、不要有多余字符）" }
$tok = (Get-Content $tokFile -Raw).Trim()
if (-not $tok) { throw "$tokFile 是空的" }

$root = Split-Path $PSScriptRoot -Parent
$tmpJson = Join-Path $env:TEMP "inkteach-fetch-logs.json"

$null = curl.exe -s --max-time 60 -o $tmpJson "$api/releases/tags/$tag`?access_token=$tok"
$raw = ''
try { $raw = [System.IO.File]::ReadAllText($tmpJson, [System.Text.Encoding]::UTF8) } catch { }
$rel = $null
try { if ($raw) { $rel = $raw | ConvertFrom-Json } } catch { }
if (-not $rel -or -not $rel.tag_name) {
    throw "取发行版失败（检查：仓名 InkTeach-Logs / 发行版标签 logs / 令牌权限；原始响应前 200 字：$($raw.Substring(0, [Math]::Min(200, $raw.Length)))）"
}

$assets = @($rel.assets | Where-Object { $_.type -eq 'attach' })
Write-Host "日志收集仓 · 发行版 v$($rel.tag_name)：$($assets.Count) 份日志" -ForegroundColor Green
$i = 0
foreach ($a in $assets)
{
    $i++
    Write-Host ("  {0,3}. {1}   {2:N0} KB" -f $i, $a.name, ($a.size / 1KB))
}

if ($Download -and $assets.Count -gt 0)
{
    $dir = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($a in $assets)
    {
        $dest = Join-Path $dir $a.name
        if (Test-Path $dest) { Write-Host "  已在：$($a.name)" -ForegroundColor DarkGray; continue }
        $dl = [string]$a.browser_download_url
        if ($dl -notmatch '\?') { $dl += "?access_token=$tok" }   # 私有仓下载要带令牌
        $code = curl.exe -s -L --max-time 300 -o $dest -w "%{http_code}" "$dl"
        Write-Host ("  下载 {0}（http={1}）" -f $a.name, $code)
    }
    Write-Host "完成：$dir" -ForegroundColor Green
}

Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue
