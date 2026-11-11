<#
  把最新的 update.json 同步到**旧仓库**（只改这一个文件）。

  为什么需要：
    9.6.0 及更早的客户端只认旧仓库的清单地址：
      raw.githubusercontent.com/XueRenYi0/InkTeach/main/update.json
      gitcode.com/api/v5/repos/XueRenYI0/InkTeach/raw/update.json
    新版本的 zip 放在新的发布仓库，但旧客户端必须先看到“9.7.0”这条清单，
    才会去新地址下载。旧仓库其它文件一律不动。

  用法（旧仓库克隆在 ..\InkTeach-Legacy）：
    .\tools\sync-legacy-manifest.ps1 -RepoDir ..\InkTeach-Legacy
    .\tools\sync-legacy-manifest.ps1 -RepoDir ..\InkTeach-Legacy -NoPush   # 只看 diff

  注意：旧仓库必须保持 **Public**；一旦转私有，9.6 及更早的客户端就收不到更新。
#>
param(
    [Parameter(Mandatory = $true)][string]$RepoDir,
    [string]$Manifest = "",
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Manifest) { $Manifest = Join-Path $root 'update.json' }
if (-not (Test-Path $Manifest)) { throw "找不到清单：$Manifest（先跑 .\publish.ps1）" }
if (-not (Test-Path (Join-Path $RepoDir '.git'))) { throw "$RepoDir 不是 git 仓库" }

$dst = Join-Path $RepoDir 'update.json'
Copy-Item $Manifest $dst -Force
$ver = (Get-Content $dst -Raw | ConvertFrom-Json).version

Push-Location $RepoDir
try {
    git add update.json
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  旧仓库清单已经是 $ver，无需提交" -ForegroundColor DarkGray
        return
    }
    git commit -m "release: update manifest for $ver (source stays frozen)" | Out-Null
    if ($NoPush) {
        Write-Host "  已提交（未推送）：$RepoDir" -ForegroundColor Yellow
        return
    }
    git push
    if ($LASTEXITCODE -ne 0) { throw "推送旧仓库失败（git exit=$LASTEXITCODE）" }
    Write-Host "  已推送旧仓库清单：$ver" -ForegroundColor Green
}
finally {
    Pop-Location
}
