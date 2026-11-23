<#
  把一次发布同步到 GitCode 国内镜像仓库（XueRenYI0/InkTeach）。

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
$owner = 'XueRenYI0'
$repo = 'InkTeach-Releases'
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

# 固定名副本（官网"永不失效"的免登录直链用，见 publish.ps1 同名区块）：有就一起传。
$stableZip = Join-Path $root "dist\InkTeach-win-x64.zip"
$stableSetup = Join-Path $root "dist\InkTeach-Setup.exe"

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

# ① 闭源发布：**不推源码**（2026-11-11 起）。
# 这里原来会把 main + 标签推到 GitCode，等于公开源码；现在只上传发行版附件。
Write-Host "  闭源发布：跳过源码推送，只上传安装包…" -ForegroundColor DarkGray

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

# ③ 附件：同名已在就跳过，缺的先取 upload_url 再 PUT（含固定名副本，有就传）
$existing = @($rel.assets | Where-Object { $_.type -eq 'attach' } | ForEach-Object { $_.name })
$uploads = @($zip, $setup) + @($stableZip, $stableSetup) |
           Where-Object { Test-Path $_ } | Select-Object -Unique
foreach ($f in $uploads) {
    $leaf = Split-Path $f -Leaf
    if ($existing -contains $leaf) { Write-Host "  附件已在：$leaf" -ForegroundColor DarkGray; continue }
    $up = curl.exe -s --max-time 60 "$api/releases/$tag/upload_url?access_token=$tok&file_name=$leaf" | ConvertFrom-Json
    if (-not $up.url) { throw "取 upload_url 失败：$leaf" }
    # **必须关掉 Expect: 100-continue**（2026-11-23 发 9.9.8 踩到）：curl 对 >1KB 的 PUT 默认先发
    # `Expect: 100-continue` 等服务器放行，GitCode 的 OBS 上传端点会先回一个 `HTTP/1.1 100 Continue`
    # 就断在这——`%{http_code}` 只剩 `100`，脚本报"上传失败（http=100）"，其实一个字节都没传上去。
    # `-H 'Expect:'`（空值）= 直接发 body，实测回 200。
    $cargs = @('-s', '-o', 'NUL', '-w', '%{http_code}', '-H', 'Expect:', '--max-time', '1800', '-X', 'PUT', '--data-binary', "@$f")
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

# ⑤ 镜像清单（2026-11-17 加）：GitCode 仓库 main 分支上的 update.json 也是客户端的一条清单源。
#    以前只靠手工同步，漏一次这条源就一直报旧版本。这里用 contents API 直接改（幂等：内容一样就不提交）。
$localManifest = Join-Path $root 'update.json'
if (Test-Path $localManifest) {
    $contentsApi = "https://api.gitcode.com/api/v5/repos/$owner/$repo/contents/update.json"
    $curFile = $null
    try { $curFile = curl.exe -s --max-time 60 "$contentsApi`?ref=main&access_token=$tok" | ConvertFrom-Json } catch { }
    $b64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($localManifest))
    if ($curFile -and $curFile.content -and ($curFile.content -replace '\s', '') -eq $b64) {
        Write-Host "  镜像清单已是最新（$Version）" -ForegroundColor DarkGray
    } else {
        $putBody = @{
            message = "transitional manifest $Version -> GitHub XueRenYi0/InkTeach"
            content = $b64
            branch  = 'main'
        }
        if ($curFile -and $curFile.sha) { $putBody.sha = $curFile.sha }
        $putFile = Join-Path $env:TEMP "inkteach-gc-put-$Version.json"
        # **不带 BOM**：`Set-Content -Encoding UTF8` 在 Windows PowerShell 5.1 上会写 BOM，
        # 带着 BOM 的 JSON 体 GitCode 直接回 400（"Request body parsing error"），
        # 2026-11-17 发 9.9.3 时踩到过一次。这里显式用无 BOM 的 UTF8Encoding。
        [System.IO.File]::WriteAllText($putFile, ($putBody | ConvertTo-Json -Compress),
            (New-Object System.Text.UTF8Encoding($false)))
        $putOut = curl.exe -s --max-time 60 -X PUT "$contentsApi`?access_token=$tok" -H 'Content-Type: application/json' --data-binary "@$putFile"
        Remove-Item $putFile -Force -ErrorAction SilentlyContinue
        if ($putOut -match '"commit"') { Write-Host "  镜像清单已更新（$Version）" -ForegroundColor Green }
        else { Write-Host "  ⚠ 镜像清单更新失败（不影响附件下载）：$($putOut.Substring(0, [Math]::Min(160, $putOut.Length)))" -ForegroundColor Yellow }
    }
}

Write-Host "  完成：https://gitcode.com/$owner/$repo/releases/tag/$tag" -ForegroundColor Green
Remove-Item $tmpJson -Force -ErrorAction SilentlyContinue
