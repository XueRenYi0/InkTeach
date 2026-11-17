<#
  官网同步到阿里云 OSS（2026-11-17 加）

  为什么有它：官网源文件在 `docs\`，原来只有 GitHub Pages 一份。国内访问 GitHub Pages 慢，
  改成「国内走 OSS、国外继续走 Pages」。两边**同一份源文件**，所以发版时只需要把 docs\
  里该公开的那几个文件传上去，不要再手工复制一遍（手工复制 = 迟早漏传）。

  白名单：docs\ 里同时躺着开发文档（tree-map.md 等）和 Pages 专用文件（CNAME / _headers）。
  这些不该进 OSS（浪费存储，也把内部文档公开了），所以下面维护明确的 `$files` 清单。

  体积：文本类先 gzip 再上传并打上 `Content-Encoding: gzip`，全站 296 KB → 实际传输 200 KB
  （首屏那 8 个文本 + 封面 + 视频 ≈ 125 KB）。OSS 自己不压缩，这一步只能在上传时做。不想压就加 -NoGzip。

  用法：
    .\tools\deploy-site.ps1                     # 按 tools\oss-site.local.json 上传
    .\tools\deploy-site.ps1 -DryRun             # 只打印要传什么，不传
    .\tools\deploy-site.ps1 -NoGzip             # 不压，原样上传
    .\tools\deploy-site.ps1 -Bucket x -Region cn-hangzhou   # 临时覆盖配置

  首次使用（一次性）：
    ① 装 ossutil 2.x 并配好 AK：
         https://gosspublic.alicdn.com/ossutil/v2/2.4.1/ossutil-2.4.1-windows-amd64.zip
         ossutil config        # 填 AK / Region / Endpoint
    ② 复制 tools\oss-site.local.json.example 为 tools\oss-site.local.json，填 bucket / region。
       那个文件在 .gitignore 里，AK 永远不进库。

  Bucket 侧的一次性设置（静态网站托管 / 默认首页 / 公共读 / 自定义域名 / HTTPS / 防盗链）
  **不在这里**——那些是控制台点一次就好的事，写进脚本只会在出错时更难判断。
  清单见 docs\网站部署-OSS.md。
#>
param(
    # 留空 = 读 tools\oss-site.local.json
    [string]$Bucket = "",
    [string]$Region = "",
    # 想放到 Bucket 的子目录就填，例如 "site"（末尾斜杠会自动补齐）
    [string]$Prefix = "",
    [string]$ConfigFile = "",
    # 文本类不 gzip
    [switch]$NoGzip,
    # 只打印命令，不上传
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$docs = Join-Path $root 'docs'

# ---------- 1. 配置 ----------
if (-not $ConfigFile) { $ConfigFile = Join-Path $PSScriptRoot 'oss-site.local.json' }
if (Test-Path $ConfigFile) {
    $cfg = Get-Content $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $Bucket -and $cfg.bucket) { $Bucket = [string]$cfg.bucket }
    if (-not $Region -and $cfg.region) { $Region = [string]$cfg.region }
    if (-not $Prefix -and $cfg.prefix) { $Prefix = [string]$cfg.prefix }
    # enabled=false：留个开关，不想传的时候不用改发布脚本
    if ($cfg.PSObject.Properties.Name -contains 'enabled' -and $cfg.enabled -eq $false) {
        Write-Host '  跳过 OSS：tools\oss-site.local.json 里 enabled=false' -ForegroundColor DarkGray
        exit 0
    }
} else {
    Write-Host "  跳过 OSS：没找到 $ConfigFile" -ForegroundColor DarkGray
    Write-Host '  想启用就照着 tools\oss-site.local.json.example 建一个（AK 走 ossutil config，不写这里）' -ForegroundColor DarkGray
    exit 0
}

if (-not $Bucket) { throw 'oss-site.local.json 里没有 bucket' }
if (-not $Region) { throw 'oss-site.local.json 里没有 region（例如 cn-hangzhou / cn-hongkong）' }
if ($Prefix -and -not $Prefix.EndsWith('/')) { $Prefix += '/' }

$ossutil = Get-Command ossutil -ErrorAction SilentlyContinue
if (-not $ossutil -and -not $DryRun) {
    throw '没找到 ossutil。装 2.x 版并加进 PATH：https://gosspublic.alicdn.com/ossutil/v2/2.4.1/ossutil-2.4.1-windows-amd64.zip'
}

# ---------- 2. 要传哪些文件（白名单） ----------
# gz = 文本类，可以先 gzip 再传（Content-Encoding: gzip）
$files = @(
    @{ p = 'index.html';             cache = 'html';     type = 'text/html; charset=utf-8';                gz = $true }
    @{ p = 'privacy.html';           cache = 'html';     type = 'text/html; charset=utf-8';                gz = $true }
    @{ p = 'style.css';              cache = 'asset';    type = 'text/css; charset=utf-8';                 gz = $true }
    @{ p = 'i18n.js';                cache = 'asset';    type = 'application/javascript; charset=utf-8';    gz = $true }
    @{ p = 'site-release.js';        cache = 'asset';    type = 'application/javascript; charset=utf-8';    gz = $true }
    @{ p = 'release.json';           cache = 'manifest'; type = 'application/json; charset=utf-8';         gz = $true }
    @{ p = 'robots.txt';             cache = 'day';      type = 'text/plain; charset=utf-8';               gz = $true }
    @{ p = 'sitemap.xml';            cache = 'day';      type = 'application/xml; charset=utf-8';          gz = $true }
    @{ p = 'img/icon.png';           cache = 'img';      type = 'image/png';                               gz = $false }
    @{ p = 'img/demo.mp4';           cache = 'video';    type = 'video/mp4';                               gz = $false }
    @{ p = 'img/demo-poster.jpg';    cache = 'img';      type = 'image/jpeg';                              gz = $false }
    @{ p = 'img/social-preview.png'; cache = 'img';      type = 'image/png';                               gz = $false }
)

# 缓存策略：**清单类必须短**，否则发版后用户还看到上一版的版本号/下载链接
# （GitHub Pages 那份 docs\_headers 里说的是同一件事，两边别改岔）。
$cache = @{
    html     = 'public, max-age=0, must-revalidate'
    manifest = 'public, max-age=300'
    asset    = 'public, max-age=600'
    day      = 'public, max-age=3600'
    img      = 'public, max-age=604800'
    video    = 'public, max-age=2592000'
}

$missing = @()
foreach ($f in $files) {
    if (-not (Test-Path (Join-Path $docs ($f.p -replace '/', '\')))) { $missing += $f.p }
}
if ($missing.Count -gt 0) {
    throw ("这些文件不在 docs\ 里，先确认是不是删错了：`n  " + ($missing -join "`n  "))
}

# ---------- 3. 传 ----------
$base = "oss://$Bucket/"
if ($Prefix) { $base += $Prefix }
Write-Host "`n官网 → $base（region $Region）" -ForegroundColor Cyan

$tmpDir = Join-Path $env:TEMP ('inkteach-oss-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

function Compress-Gzip([string]$src, [string]$dst) {
    $in = [System.IO.File]::OpenRead($src)
    try {
        $out = [System.IO.File]::Create($dst)
        try {
            $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}

$rawKb = 0
$sentKb = 0
try {
    foreach ($f in $files) {
        $local = Join-Path $docs ($f.p -replace '/', '\')
        $size = (Get-Item $local).Length
        $rawKb += $size / 1KB

        $upload = $local
        $extra = @()
        $gzNote = ''
        if ($f.gz -and -not $NoGzip) {
            $gzPath = Join-Path $tmpDir (($f.p -replace '/', '_') + '.gz')
            Compress-Gzip $local $gzPath
            $upload = $gzPath
            $extra = @('--content-encoding', 'gzip')
            $gzNote = ' gzip'
        }
        $sentSize = (Get-Item $upload).Length
        $sentKb += $sentSize / 1KB

        $dest = $base + $f.p
        # 注意别用 $args（PowerShell 自动变量）
        $cpArgs = @('cp', $upload, $dest, '-f',
            '--cache-control', $cache[$f.cache],
            '--content-type', $f.type) + $extra

        if ($DryRun) {
            Write-Host ("  [dry] {0,-24} {1,7:N1} KB → {2,7:N1} KB{3}" -f $f.p, ($size / 1KB), ($sentSize / 1KB), $gzNote) -ForegroundColor DarkGray
            continue
        }
        $out = & ossutil @cpArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host ($out | Out-String) -ForegroundColor Red
            throw "上传失败：$($f.p)（exit=$LASTEXITCODE）"
        }
        Write-Host ("  OK {0,-24} {1,7:N1} KB → {2,7:N1} KB{3}" -f $f.p, ($size / 1KB), ($sentSize / 1KB), $gzNote) -ForegroundColor Green
    }
}
finally {
    Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ("  合计 {0} 个文件：磁盘 {1:N0} KB，实际传输 {2:N0} KB" -f $files.Count, $rawKb, $sentKb) -ForegroundColor Green
if ($DryRun) { Write-Host '  -DryRun：什么都没传' -ForegroundColor Yellow }
