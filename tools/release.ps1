<#
  一条命令发一版（2026-11-17 加）

  以前发一版要手工走七八步（publish → 改 update.json → 提交 → tag → 推两个仓库 →
  建 Release → 传附件 → GitCode → 同步旧仓库清单），中间任何一步忘了，用户就收不到更新。
  这个脚本把顺序固定下来，**失败就停在原地并告诉你卡在哪一步**，重跑同一条命令即可续上。

  用法：
    .\tools\release.ps1 -Notes dist\release-notes-9.9.3.md
    .\tools\release.ps1 -Notes dist\release-notes-9.9.3.md -SkipChecks   # 已跑过 check-all
    .\tools\release.ps1 -Notes ... -DryRun                                # 只打包，不提交/不发布

  2026-11-23 起加的两个省时开关（用户拍板，只影响本机流程，不改产物）：
    -ReuseDist   第 2 步发现 dist 里 zip / Setup / update.json 都在（版本号一致）时
                 **跳过 AOT 重建**，直接走 2b / 3。重跑一次失败的发版时最省时间。
    -WithLegacy  第 6 步（往 InkTeach-Releases 推过渡清单）**默认不跑**；
                 老客户端还没升完、确实要补那一份清单时才显式传它。
                 `-NoLegacy` 保留只为兼容旧命令行，语义现在和默认一致（跳第 6 步）。

  顺序：
    ① sync-site  官网 release.json / JSON-LD / sitemap 跟上版本
    ② publish    打包（AOT + zip + Setup + update.json）
    ②b           官网大小回填，再传到阿里云 OSS（没配 tools\oss-site.local.json 就跳过）
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
    # 第 2 步：dist 里这一版的产物都在就跳过 AOT 重建（重跑发版时用）
    [switch]$ReuseDist,
    # 第 6 步：只有显式传它才往 InkTeach-Releases 推过渡清单（2026-11-23 起默认不跑）
    [switch]$WithLegacy,
    # 兼容旧命令行：语义等于默认（跳第 6 步）
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
$distArtifacts = @(
    @{ Path = $zip; Name = 'zip' },
    @{ Path = $setup; Name = 'Setup' },
    @{ Path = $manifest; Name = 'update.json' }
)
$missing = @($distArtifacts | Where-Object { -not (Test-Path $_.Path) } | ForEach-Object { $_.Name })
if ($ReuseDist -and $missing.Count -eq 0) {
    Write-Host "  -ReuseDist：dist 里 $ver 的 zip / Setup / update.json 都在，跳过重建" -ForegroundColor DarkGray
} else {
    if ($ReuseDist) {
        Write-Host "  -ReuseDist：缺 $($missing -join ' / ')，照常重建" -ForegroundColor Yellow
    }
    $pubArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'publish.ps1'))
    if ($SkipChecks) { $pubArgs += '-SkipChecks' }
    Run { & powershell @pubArgs }
}
foreach ($f in @($zip, $setup, $manifest)) {
    if (-not (Test-Path $f)) { throw "打包没产出：$f" }
}

Step '2b' "官网大小回填 + 同步到阿里云 OSS"
# 打包前那次 sync-site 读的是**上一版**的 dist（这一版的 zip/setup 还没生成），
# 所以打完包再跑一次，把这一版的真实大小写进 release.json，然后整站传到 OSS。
# 没配 tools\oss-site.local.json 时 deploy-site 自己跳过，不影响 GitHub Pages 这条线。
Run { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'sync-site.ps1') -Version $ver }
$deployArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'deploy-site.ps1'))
if ($DryRun) { $deployArgs += '-DryRun' }
Run { & powershell @deployArgs }

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
    # `git diff --quiet` 返回 1 = "有改动"，是**正常分支**，不是失败；但它会把
    # $LASTEXITCODE 留成 1，脚本末尾就带着 1 退出（Step 6 被跳过时没人再覆盖它）。
    # 这里显式归零，避免"发版成功却报失败"。
    $global:LASTEXITCODE = 0
    Run { git push upstream main }
    $hasTag = git tag --list $tag
    if (-not $hasTag) { Run { git tag $tag } }
    Run { git push upstream $tag }
}
finally { Pop-Location }

Step 4 "GitHub Release $tag"
$assets = @($zip, $setup) + @($stableZip, $stableSetup, $manifest) | Where-Object { Test-Path $_ }
# 查询发行版是否存在：`gh` 在发行版不存在时会往 stderr 写 "release not found"，
# 在 `$ErrorActionPreference = 'Stop'` 下 PowerShell 5.1 会把 native stderr 当终止异常，
# 结果第 97 行的 `if (-not $view) { gh release create ... }` 根本走不到。
# 所以这段临时放开偏好为 Continue，并显式看 $LASTEXITCODE，查完立刻恢复 Stop。
$view = $null
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $view = gh release view $tag -R "$ghOwner/$ghRepo" --json tagName 2>$null
    if ($LASTEXITCODE -ne 0) { $view = $null }
}
catch { $view = $null }
finally { $ErrorActionPreference = 'Stop' }
if (-not $view) {
    Run { gh release create $tag -R "$ghOwner/$ghRepo" --title "InkTeach $ver" --notes-file $Notes }
} else {
    Write-Host "  发行版 $tag 已存在，只补附件" -ForegroundColor DarkGray
}
Run { gh release upload $tag @assets -R "$ghOwner/$ghRepo" --clobber }
Run { gh release edit $tag -R "$ghOwner/$ghRepo" --title "InkTeach $ver" --notes-file $Notes }

Step 5 "GitCode 国内镜像"
Run { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'gitcode-release.ps1') -Version $ver -Notes "InkTeach $ver" }

if ($WithLegacy -and -not $NoLegacy) {
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
} else {
    Step 6 '跳过（默认不推旧仓库过渡清单；要推请传 -WithLegacy）'
}

$global:LASTEXITCODE = 0
Write-Host "`n完成：$ver" -ForegroundColor Green
Write-Host "  GitHub  https://github.com/$ghOwner/$ghRepo/releases/tag/$tag"
Write-Host "  GitCode https://gitcode.com/XueRenYI0/InkTeach-Releases/releases/tag/$tag"
