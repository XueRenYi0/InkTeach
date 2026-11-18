<#
  官网自动跟随版本（2026-11-17 加）

  问题：以前每发一版都要让大模型去改 docs\index.html 和 docs\i18n.js 里的
        版本号、下载链接、安装包大小——改漏一处官网就写着旧版本。

  现在：**唯一的事实来源是 docs\release.json**，由这个脚本按“当前版本 + dist 里的
        真实产物”生成；网页自己读它（docs\site-release.js），不需要再改页面文案。

  用法：
    .\tools\sync-site.ps1                       # 版本取 csproj，大小取 dist 里的产物
    .\tools\sync-site.ps1 -Version 9.9.3        # 显式指定（一般不用）
    .\tools\sync-site.ps1 -NoStamp              # 只写 release.json，不动 index.html/sitemap

  做四件事：
    ① 写 docs\release.json（version / date / 四个下载地址 / 两个大小）；
    ② 顺手把 index.html 的 JSON-LD（softwareVersion / downloadUrl）和 sitemap 的
       lastmod 就地改掉——这两处是给搜索引擎看的，不能靠 JS；
    ③ 校验所有 docs\*.html + docs\i18n\*.js 里**没有**再写死版本号（写死就报错，免得又漏）；
    ④ 打印一份对照，方便肉眼确认。
#>
param(
    [string]$Version = "",
    # 首页「最近更新」三张卡：传 JSON 文件（[{title,body},{…}]）就写进 release.json；
    # 不传 = 沿用页面里原来那三张静态卡（历史版本说明，不用每版改）。
    [string]$Highlights = "",
    [switch]$NoStamp
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$docs = Join-Path $root 'docs'

if (-not $Version) {
    $proj = Join-Path $root 'src\InkTeach\InkTeach.csproj'
    $Version = (Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
}
if (-not $Version) { throw '没读到版本号' }

# 发布地址：GitHub 原仓库（恒定 latest 重定向）+ GitCode 国内镜像（按 tag）。
$ghOwner = 'XueRenYi0'
$ghRepo  = 'InkTeach'
$gcOwner = 'XueRenYI0'
$gcRepo  = 'InkTeach-Releases'

$setupGh = "https://github.com/$ghOwner/$ghRepo/releases/latest/download/InkTeach-Setup.exe"
$zipGh   = "https://github.com/$ghOwner/$ghRepo/releases/latest/download/InkTeach-win-x64.zip"
$setupCn = "https://gitcode.com/$gcOwner/$gcRepo/releases/download/v$Version/InkTeach-Setup.exe"
$zipCn   = "https://gitcode.com/$gcOwner/$gcRepo/releases/download/v$Version/InkTeach-win-x64.zip"

# 大小：优先用 dist 里这次真打出来的文件；没有就留空（网页会隐藏"多大"那一句）。
function Size-Mb([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    return [math]::Round((Get-Item $path).Length / 1e6, 1)
}
$setupMb = Size-Mb (Join-Path $root "dist\InkTeach-Setup.exe")
$zipMb   = Size-Mb (Join-Path $root "dist\InkTeach-win-x64.zip")

# 首页"最近更新"三张卡：默认沿用页面里原来那三张静态卡（历史版本说明，不用每版改）。
$hl = @()
if ($Highlights) {
    if (-not (Test-Path $Highlights)) { throw "找不到 highlights 文件：$Highlights" }
    $hl = @(Get-Content $Highlights -Raw -Encoding UTF8 | ConvertFrom-Json)
}

$json = [ordered]@{
    version = $Version
    date    = (Get-Date -Format 'yyyy-MM-dd')
    setup   = [ordered]@{ gh = $setupGh; cn = $setupCn; mb = $setupMb }
    zip     = [ordered]@{ gh = $zipGh;   cn = $zipCn;   mb = $zipMb }
    highlights = $hl
}
$jsonPath = Join-Path $docs 'release.json'
# **不带 BOM**：浏览器里 `JSON.parse()` 遇到 BOM 会直接抛（V8 不让），页面就永远读不到版本。
[System.IO.File]::WriteAllText($jsonPath, (($json | ConvertTo-Json -Depth 5) + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "  已写 docs\release.json（$Version，安装包 $setupMb MB / 绿色包 $zipMb MB）" -ForegroundColor Green

if (-not $NoStamp) {
    # ---- JSON-LD / sitemap / 首屏兜底：这三处搜索引擎和首屏只认 HTML 里的字面值 ----
    $indexPath = Join-Path $docs 'index.html'
    $html = [System.IO.File]::ReadAllText($indexPath)
    $before = $html
    $html = [regex]::Replace($html, '"softwareVersion":\s*"[^"]*"', "`"softwareVersion`": `"$Version`"")
    $html = [regex]::Replace($html, '"downloadUrl":\s*"[^"]*"', "`"downloadUrl`": `"$setupGh`"")
    # 首屏兜底对象 window.INKTEACH_RELEASE_FALLBACK = { … }; 整段重写（见 docs\index.html 末尾）
    $mb = if ($null -eq $setupMb) { 'null' } else { $setupMb.ToString([System.Globalization.CultureInfo]::InvariantCulture) }
    $zmb = if ($null -eq $zipMb) { 'null' } else { $zipMb.ToString([System.Globalization.CultureInfo]::InvariantCulture) }
    $fallback = @"
window.INKTEACH_RELEASE_FALLBACK = {
  version: "$Version",
  date: "$($json.date)",
  setup: { gh: "$setupGh",
           cn: "$setupCn",
           mb: $mb },
  zip:   { gh: "$zipGh",
           cn: "$zipCn",
           mb: $zmb }
};
"@
    $html = [regex]::Replace($html,
        'window\.INKTEACH_RELEASE_FALLBACK\s*=\s*\{.*?\n\};',
        [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $fallback.TrimEnd() },
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($html -ne $before) { [System.IO.File]::WriteAllText($indexPath, $html, (New-Object System.Text.UTF8Encoding($false))) }

    $siteMap = Join-Path $docs 'sitemap.xml'
    if (Test-Path $siteMap) {
        $sm = [regex]::Replace([System.IO.File]::ReadAllText($siteMap), '<lastmod>[^<]*</lastmod>', "<lastmod>$($json.date)</lastmod>")
        [System.IO.File]::WriteAllText($siteMap, $sm, (New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Host '  JSON-LD / sitemap / 首屏兜底 已跟上这个版本' -ForegroundColor Green
}

# ---- 自检：页面里不许再出现写死的版本号 ----
# 只认「当前版本号」写死：历史更新卡（9.9.0 / 9.8.0 …）本来就该写死，那是在讲旧版本。
# 2026-11-17 拆页后页面变成六个 HTML + 十个语言文件，这里改成扫整个目录——
# 以前只查 index.html + i18n.js，新页面写死版本号根本没人管。
# 两处豁免（它们只认 HTML 里的字面值，靠 JS 填不了）：index.html 的 JSON-LD、首屏兜底对象。
$targets = @()
$targets += @(Get-ChildItem -Path $docs -Filter '*.html' -File | Sort-Object Name | ForEach-Object { $_.Name })
$targets += @(Get-ChildItem -Path (Join-Path $docs 'i18n') -Filter '*.js' -File -ErrorAction SilentlyContinue |
    Sort-Object Name | ForEach-Object { "i18n/$($_.Name)" })
if (-not $targets.Contains('i18n.js')) { $targets += 'i18n.js' }   # 骨架也不能写死

$hard = @()
foreach ($f in $targets) {
    $p = Join-Path $docs ($f -replace '/', '\')
    if (-not (Test-Path $p)) { continue }
    $lines = [System.IO.File]::ReadAllLines($p)
    $skip = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if (-not $skip -and $line -match '<script\s+type="application/ld\+json">') { $skip = $true; continue }
        if (-not $skip -and $line -match '<script>\s*$') {
            # 兜底对象那个 <script> 块：注释里带 "发版时由"，块里有 INKTEACH_RELEASE_FALLBACK。
            # 往后看 6 行判断，避免把别的内联脚本一起放过。
            $peek = ($lines[$i..([Math]::Min($i + 6, $lines.Count - 1))] -join "`n")
            if ($peek -match 'INKTEACH_RELEASE_FALLBACK') { $skip = $true; continue }
        }
        if ($skip) {
            if ($line -match '</script>') { $skip = $false }
            continue
        }
        if ($line.Contains($Version)) { $hard += "${f}:$($i + 1): $($line.Trim())" }
    }
}
if ($hard.Count -gt 0) {
    Write-Host '  ⚠ 页面里仍有写死的版本号（应该改用 {V} 占位）：' -ForegroundColor Yellow
    $hard | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
} else {
    Write-Host "  页面里已无写死版本号（全部走 release.json；扫了 $($targets.Count) 个文件）" -ForegroundColor Green
}
