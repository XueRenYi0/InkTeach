<#
  把一次发布同步到 Gitee 国内镜像仓库。

  ⚠ 2026-10-03：Gitee 当前账号被"RAW 外链滥用"限制、仓库被强制私有（公开仓库需实名），
  本脚本暂时不可用，留作以后解除限制/实名后使用。国内镜像现用 tools\gitcode-release.ps1。

  前置：
    · %USERPROFILE%\.gitee-token 里放着 Gitee 私人令牌（勾了 projects 权限）
    · 先跑过 publish.ps1（dist\ 下有 zip / Setup / update-gitee.json）

  用法：
    .\tools\gitee-release.ps1 -Version 8.6.3 -Notes "本次更新说明…"

  做三件事（幂等，可重复跑）：
    ① 建发行版 v<版本>（已存在就用它）
    ② 上传 dist\InkTeach-<版本>-win-x64.zip 和 dist\InkTeach-Setup-<版本>.exe
    ③ 把 dist\update-gitee.json 提交为仓库根 update.json（App 从 raw 读它）
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ""
)

$ErrorActionPreference = 'Stop'
$owner = 'the-spirit-of-the-snowman'
$repo  = 'InkTeach'
$tag   = "v$Version"
$api   = "https://gitee.com/api/v5/repos/$owner/$repo"

$tokFile = Join-Path $env:USERPROFILE '.gitee-token'
if (-not (Test-Path $tokFile)) { throw "找不到 $tokFile（把 Gitee 私人令牌存进去，一行、不要有多余字符）" }
$tok = (Get-Content $tokFile -Raw).Trim()
if (-not $tok) { throw "$tokFile 是空的" }

$root  = Split-Path $PSScriptRoot -Parent
$zip   = Join-Path $root "dist\InkTeach-$Version-win-x64.zip"
$setup = Join-Path $root "dist\InkTeach-Setup-$Version.exe"
$man   = Join-Path $root "dist\update-gitee.json"
foreach ($f in @($zip, $setup, $man)) {
    if (-not (Test-Path $f)) { throw "缺文件：$f（先跑 .\publish.ps1）" }
}

# ① 建发行版（已存在则取回）
$rel = $null
try {
    $rel = curl.exe -s --max-time 60 "$api/releases/tags/$tag`?access_token=$tok" | ConvertFrom-Json
} catch { }
if (-not $rel -or -not $rel.id) {
    $rel = curl.exe -s --max-time 60 -X POST "$api/releases" `
        --data-urlencode "access_token=$tok" `
        --data-urlencode "tag_name=$tag" `
        --data-urlencode "name=$tag InkTeach" `
        --data-urlencode "body=$Notes" `
        --data-urlencode "target_commitish=master" | ConvertFrom-Json
    if (-not $rel.id) {
        # 建失败（比如 tag 已被占用）：回退去列表里找
        $all = curl.exe -s --max-time 60 "$api/releases?access_token=$tok&per_page=100" | ConvertFrom-Json
        $rel = $all | Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
    }
    if (-not $rel -or -not $rel.id) { throw "建/查 Gitee 发行版失败（$tag）" }
}
Write-Host "  发行版 $tag (id=$($rel.id))" -ForegroundColor Green

# ② 上传附件（同名已存在就跳过）
$existing = @()
try {
    $existing = curl.exe -s --max-time 60 "$api/releases/$($rel.id)/attach_files?access_token=$tok&per_page=100" | ConvertFrom-Json
} catch { }
foreach ($f in @($zip, $setup)) {
    $leaf = Split-Path $f -Leaf
    $hit = $existing | Where-Object { $_.name -eq $leaf } | Select-Object -First 1
    if ($hit) {
        Write-Host "  附件已在：$leaf -> $($hit.browser_download_url)" -ForegroundColor DarkGray
        continue
    }
    $r = curl.exe -s --max-time 1800 -X POST "$api/releases/$($rel.id)/attach_files?access_token=$tok" -F "file=@$f" | ConvertFrom-Json
    if ($r.browser_download_url) {
        Write-Host "  上传 $leaf -> $($r.browser_download_url)" -ForegroundColor Green
    } else {
        throw "上传附件失败：$leaf"
    }
}

# ③ 提交国内清单（有就 PUT + sha，没有就 POST）
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Content $man -Raw)))
$cur = $null
try { $cur = curl.exe -s --max-time 40 "$api/contents/update.json?access_token=$tok" | ConvertFrom-Json } catch { }
if ($cur -and $cur.sha) {
    $code = curl.exe -s -o NUL -w "%{http_code}" --max-time 60 -X PUT "$api/contents/update.json" `
        --data-urlencode "access_token=$tok" --data-urlencode "content=$b64" `
        --data-urlencode "message=发布 $tag：国内更新清单" --data-urlencode "sha=$($cur.sha)"
} else {
    $code = curl.exe -s -o NUL -w "%{http_code}" --max-time 60 -X POST "$api/contents/update.json" `
        --data-urlencode "access_token=$tok" --data-urlencode "content=$b64" `
        --data-urlencode "message=发布 $tag：国内更新清单"
}
Write-Host "  update.json 提交：http=$code" -ForegroundColor Green
Write-Host "  完成：https://gitee.com/$owner/$repo/releases/tag/$tag" -ForegroundColor Green
