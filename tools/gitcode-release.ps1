<#
  把一次发布同步到 GitCode 国内镜像仓库（xzx1xzzx/InkTeach）。

  前置：
    · %USERPROFILE%\.gitcode-token 里放着 GitCode 私人令牌（勾了仓库/项目权限）
    · 先跑过 publish.ps1（dist\ 下有 zip / Setup）

  用法：
    .\tools\gitcode-release.ps1 -Version 8.6.3 -Notes "本次更新说明…"

  做四件事（幂等，可重复跑）：
    ① 把 main 和全部标签推到 GitCode（源码镜像）
    ② 建发行版 v<版本>（已存在就用它）
    ③ 上传 dist\InkTeach-<版本>-win-x64.zip 和 dist\InkTeach-Setup-<版本>.exe
       （先取 upload_url，再 PUT；已存在的同名附件跳过）
    ④ 回读并打印附件直链
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ""
)

$ErrorActionPreference = 'Stop'
$owner = 'xzx1xzzx'
$repo = 'InkTeach'
$tag = "v$Version"
$api = "https://api.gitcode.com/api/v5/repos/$owner/$repo"

$tokFile = Join-Path $env:USERPROFILE '.gitcode-token'
if (-not (Test-Path $tokFile)) { throw "找不到 $tokFile（把 GitCode 私人令牌存进去，一行、不要有多余字符）" }
$tok = (Get-Content $tokFile -Raw).Trim()
if (-not $tok) { throw "$tokFile 是空的" }

$root = Split-Path $PSScriptRoot -Parent
$zip = Join-Path $root "dist\InkTeach-$Version-win-x64.zip"
$setup = Join-Path $root "dist\InkTeach-Setup-$Version.exe"
foreach ($f in @($zip, $setup)) { if (-not (Test-Path $f)) { throw "缺文件：$f（先跑 .\publish.ps1）" } }

# JSON 一律**先落文件、再按 UTF-8 读**：PowerShell 5.1 的 `curl | ConvertFrom-Json`
# 管道会把 UTF-8 中文/长 JSON 解错（2026-10-04 发 8.6.4 时真踩过：发行版都建出来了，
# 解析那一步抛异常，附件没传成）。落文件读回来就稳了。
$tmpJson = Join-Path $env:TEMP "inkteach-gc-rel-$Version.json"
function Read-GcJson([string]$file) {
    if (-not (Test-Path $file)) { return $null }
    $raw = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
    if (-not $raw) { return $null }
    return ($raw | ConvertFrom-Json)
}

# ① 源码镜像（输出里把令牌打码；git 往 stderr 打进度，别被 $ErrorActionPreference=Stop 当成异常）
Write-Host "  推送源码（main + 标签）到 GitCode…" -ForegroundColor DarkGray
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$out = & git -C $root push "https://${owner}:${tok}@gitcode.com/$owner/$repo.git" main --tags 2>&1
$gitExit = $LASTEXITCODE
$ErrorActionPreference = $prevEap
$out | Select-Object -Last 2 | ForEach-Object { "    " + ($_.ToString() -replace [regex]::Escape($tok), '***') }
if ($gitExit -ne 0) { throw "推送 GitCode 失败（git exit=$gitExit）" }

# ② 发行版
$rel = $null
try {
    $null = curl.exe -s --max-time 60 -o $tmpJson "$api/releases/tags/$tag`?access_token=$tok"
    $rel = Read-GcJson $tmpJson
} catch { }
if (-not $rel -or -not $rel.tag_name) {
    $null = curl.exe -s --max-time 60 -o $tmpJson -X POST "$api/releases?access_token=$tok" `
        --data-urlencode "tag_name=$tag" `
        --data-urlencode "name=$tag InkTeach" `
        --data-urlencode "body=$Notes" `
        --data-urlencode "target_commitish=main"
    $rel = Read-GcJson $tmpJson
    if (-not $rel -or -not $rel.tag_name) { throw "建 GitCode 发行版失败（$tag）" }
    Write-Host "  已建发行版 $tag" -ForegroundColor Green
}
else {
    Write-Host "  发行版 $tag 已存在" -ForegroundColor DarkGray
}

# ③ 附件：同名已在就跳过，缺的先取 upload_url 再 PUT
$existing = @($rel.assets | Where-Object { $_.type -eq 'attach' } | ForEach-Object { $_.name })
foreach ($f in @($zip, $setup)) {
    $leaf = Split-Path $f -Leaf
    if ($existing -contains $leaf) { Write-Host "  附件已在：$leaf" -ForegroundColor DarkGray; continue }
    $up = curl.exe -s --max-time 60 "$api/releases/$tag/upload_url?access_token=$tok&file_name=$leaf" | ConvertFrom-Json
    if (-not $up.url) { throw "取 upload_url 失败：$leaf" }
    $cargs = @('-s', '-o', 'NUL', '-w', '%{http_code}', '--max-time', '1800', '-X', 'PUT', '--data-binary', "@$f")
    if ($up.headers) { foreach ($p in $up.headers.PSObject.Properties) { $cargs += @('-H', ("{0}: {1}" -f $p.Name, $p.Value)) } }
    $cargs += $up.url
    $code = & curl.exe @cargs
    if ($code -notmatch '^20') { throw "上传失败（$leaf，http=$code）" }
    Write-Host "  上传 $leaf（http=$code）" -ForegroundColor Green
}

# ④ 回读
$null = curl.exe -s --max-time 60 -o $tmpJson "$api/releases/tags/$tag"
$rel2 = Read-GcJson $tmpJson
Write-Host "  附件直链：" -ForegroundColor DarkGray
@($rel2.assets | Where-Object { $_.type -eq 'attach' }) | ForEach-Object { "    $($_.browser_download_url)" }
Write-Host "  完成：https://gitcode.com/$owner/$repo/releases/tag/$tag" -ForegroundColor Green
Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue
