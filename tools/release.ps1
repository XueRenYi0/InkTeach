<#
  一条命令发一版（2026-11-17 加）

  以前发一版要手工走七八步（publish → 改 update.json → 提交 → tag → 推两个仓库 →
  建 Release → 传附件 → GitCode → 同步旧仓库清单），中间任何一步忘了，用户就收不到更新。
  这个脚本把顺序固定下来，**失败就停在原地并告诉你卡在哪一步**，重跑同一条命令即可续上。

  用法：
    .\tools\release.ps1 -Notes dist\release-notes-9.9.3.md
    .\tools\release.ps1 -Notes dist\release-notes-9.9.3.md -SkipChecks   # 已跑过 check-all
    .\tools\release.ps1 -Notes ... -DryRun                                # 只打包，不提交/不发布

  顺序：
    ① sync-site  官网 release.json / JSON-LD / sitemap 跟上版本
    ② publish    打包（AOT + zip + Setup + update.json）
    ③ 提交并推 main + tag 到 **原仓库 XueRenYi0/InkTeach**（源码公开）
    ④ GitHub Release：建 tag 发行版，传 zip / Setup / 固定名副本 / update.json
    ⑤ GitCode 镜像：建发行版、传附件（国内直连，不停）
    ⑥ 旧仓库 InkTeach-Releases：只写一条**过渡清单**，指向原仓库这一版
       （给 9.7–9.9.2 的老客户端，让它们能升到新版本；不传附件）

  版本号唯一来源：src\InkTeach\InkTeach.csproj 的 <Version>。
#>
param(
    [Parameter(Mandatory = $true)][string]$Notes,
    [switch]$SkipChecks,
    [switch]$DryRun,
    # 老客户端过渡清单：默认写；确认老用户升完之后可以 -NoLegacy
    [switch]$NoLegacy
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\InkTeach\InkTeach.csproj'
$ver = (Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $ver) { throw '没读到 <Version>' }
if (-not (Test-Path $Notes)) { throw "找不到发布说明：$Notes（先写 dist\release-notes-$ver.md）" }
$Notes = (Resolve-Path $Notes).Path

$ghOwner = 'XueRenYi0'
$ghRepo = 'InkTeach'
$tag = "v$ver"
$zip = Join-Path $root "dist\InkTeach-$ver-win-x64.zip"
$setup = Join-Path $root "dist\InkTeach-Setup-$ver.exe"
$stableZip = Join-Path $root "dist\InkTeach-win-x64.zip"
$stableSetup = Join-Path $root "dist\InkTeach-Setup.exe"
$manifest = Join-Path $root 'update.json'

function Step([string]$n, [string]$what) { Write-Host "`n[$n] $what" -ForegroundColor Cyan }
function Run([scriptblock]$sb) { & $sb; if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "上一步失败（exit=$LASTEXITCODE）" } }

Step 1 "官网跟随版本（docs\release.json + JSON-LD + sitemap）"
Run { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'sync-site.ps1') -Version $ver }

Step 2 "打包（AOT + zip + Setup + update.json）"
$pubArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'publish.ps1'))
if ($SkipChecks) { $pubArgs += '-SkipChecks' }
Run { & powershell @pubArgs }
foreach ($f in @($zip, $setup, $manifest)) {
    if (-not (Test-Path $f)) { throw "打包没产出：$f" }
}

if ($DryRun) {
    Write-Host "`n-DryRun：打包完成，未提交/未发布。产物在 dist\。" -ForegroundColor Yellow
    return
}

Step 3 "提交 + 推 main / tag 到 $ghOwner/$ghRepo"
Push-Location $root
try {
    Run { git add update.json docs }
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) { Write-Host '  没有需要提交的清单/站点改动' -ForegroundColor DarkGray }
    else {
        Run { git commit -m "release: $ver 更新清单与官网" }
    }
    Run { git push upstream main }
    $hasTag = git tag --list $tag
    if (-not $hasTag) { Run { git tag $tag } }
    Run { git push upstream $tag }
}
finally { Pop-Location }

Step 4 "GitHub Release $tag"
$assets = @($zip, $setup) + @($stableZip, $stableSetup, $manifest) | Where-Object { Test-Path $_ }
$view = gh release view $tag -R "$ghOwner/$ghRepo" --json tagName 2>$null
if (-not $view) {
    Run { gh release create $tag -R "$ghOwner/$ghRepo" --title "InkTeach $ver" --notes-file $Notes }
} else {
    Write-Host "  发行版 $tag 已存在，只补附件" -ForegroundColor DarkGray
}
Run { gh release upload $tag @assets -R "$ghOwner/$ghRepo" --clobber }
Run { gh release edit $tag -R "$ghOwner/$ghRepo" --title "InkTeach $ver" --notes-file $Notes }

Step 5 "GitCode 国内镜像"
Run { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'gitcode-release.ps1') -Version $ver -Notes "InkTeach $ver" }

if (-not $NoLegacy) {
    Step 6 "旧仓库过渡清单（给 9.7–9.9.2 老客户端）"
    $legacyDir = Join-Path (Split-Path $root -Parent) 'InkTeach-Releases'
    if (-not (Test-Path (Join-Path $legacyDir '.git'))) {
        Write-Host "  跳过：没找到 $legacyDir（克隆 https://github.com/$ghOwner/InkTeach-Releases 到这里即可）" -ForegroundColor Yellow
    } else {
        $legacyJson = Join-Path $legacyDir 'update.json'
        Copy-Item $manifest $legacyJson -Force
        Push-Location $legacyDir
        try {
            Run { git add update.json }
            git diff --cached --quiet
            if ($LASTEXITCODE -eq 0) { Write-Host '  过渡清单已是最新' -ForegroundColor DarkGray }
            else {
                Run { git commit -m "transitional manifest for $ver -> $ghOwner/$ghRepo" }
                Run { git push }
                Write-Host "  旧客户端会看到 $ver，并从原仓库下载" -ForegroundColor Green
            }
        }
        finally { Pop-Location }
    }
}

Write-Host "`n完成：$ver" -ForegroundColor Green
Write-Host "  GitHub  https://github.com/$ghOwner/$ghRepo/releases/tag/$tag"
Write-Host "  GitCode https://gitcode.com/XueRenYI0/InkTeach-Releases/releases/tag/$tag"
