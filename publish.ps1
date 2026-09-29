# =====================================================================================
#  发布脚本：把 InkTeach 打成一个**绿色版**目录（外加一个 zip）。
#
#  为什么做成绿色版而不是安装包（2026-09-27 定的）：
#    · 它不需要管理员权限（清单里没有 requireAdministrator，数据只写用户目录），
#      而教室 / 学校的机器往往锁着安装权限——**装不上就等于用不了**；
#    · 它的全部依赖就是系统自带的 Direct2D/DirectComposition（Vortice 那几个库都是
#      托管代码，没有要另装的 VC 运行库），自包含发布之后连 .NET 都不用装；
#    · "拷过去就能用、不想用了删掉"对"先给几个同事试试"这个阶段最省事。
#  等要发给不认识的老师、需要开始菜单入口和卸载时，再考虑 Inno Setup 那类安装包
#  （见 README 里"发布"那一节）。
#
#  用法：
#    .\publish.ps1                 # 默认：目录版（推荐，启动快、杀软误报少）
#    .\publish.ps1 -SingleFile     # 单文件版（就一个 exe，方便拷，启动稍慢）
#    .\publish.ps1 -NoZip          # 不压 zip
# =====================================================================================
[CmdletBinding()]
param(
    [switch]$SingleFile,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "src\InkTeach\InkTeach.csproj"

# ---- 自动更新的来源前缀（GitHub Releases 的恒定重定向）-----------------------------------
#
#   **留空 = 先不做自动更新**（打包照常，只是不发 update.json）。
#   填法（等你把仓库建好之后）：
#       $updateBase = "https://github.com/<账号>/<仓库>/releases/latest/download"
#   填好之后每次打包会顺手生成 `dist\update.json`（版本 + zip 地址 + sha256），
#   把它和 zip 一起挂到 Release 的附件里就行。App 端见 README「自动更新」。
#
#   ⚠ **这台机器实测连不上 github.com**（2026-09-29；gitee.com 正常）。教室机器多半
#     也是同一个网络环境——更稳的三条路（都只改这一行，App 不用重新编译）：
#       Gitee：     $updateBase = "https://gitee.com/<账号>/<仓库>/raw/<分支>"
#       局域网共享：$updateBase = "\\教室服务器\InkTeach"
#       对象存储：   $updateBase = "https://<桶>.oss-cn-….aliyuncs.com"
$updateBase = "https://github.com/XueRenYi0/InkTeach/releases/latest/download"

# ---- 版本号从工程里读，不在这里再写一份（写两份迟早对不上）----------------------------
$ver = (Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $ver) { throw "没能从 $proj 里读到 <Version>" }

$name = "InkTeach-$ver-win-x64"
$outDir = Join-Path $root "dist\$name"
Write-Host "发布 $name" -ForegroundColor Cyan

# ---- 图标必须先在（它是 --makeicon 生成的，跟着仓库走）--------------------------------
$ico = Join-Path $root "src\InkTeach\assets\InkTeach.ico"
if (-not (Test-Path $ico)) {
    throw "缺少程序图标 $ico。先生成：dotnet run --project src/InkTeach -c Release -- --makeicon src\InkTeach\assets\InkTeach.ico"
}

# ---- 清掉上一次的产物（免得旧 dll 混在里面，那种"改了没生效"最难查）------------------
#
# ⚠ **不要写成 `Remove-Item $outDir -Recurse -Force`**（2026-09-28 踩过，代价是整包被清空）：
#   那个命令是"先把子文件全删掉、再删目录本身"。而目录本身只要被任何进程占着
#   （资源管理器正开着那个文件夹、或者 InkTrail 还在跑），最后一步就会失败——
#   于是脚本报错退出，可**里面的文件已经没了**，留下一个空壳目录。
#
# 正确做法：**先整体改名**（原子操作，不动内容），改不动就干干净净地报错退出；
# 改名成功之后那个 `-old-xxx` 目录才慢慢删（删不掉也只是留下一点垃圾，不影响这次发布）。
if (Test-Path $outDir) {
    $leaf = Split-Path $outDir -Leaf
    $trash = Join-Path (Split-Path $outDir -Parent) ("$leaf-old-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    try {
        Rename-Item -LiteralPath $outDir -NewName (Split-Path $trash -Leaf) -ErrorAction Stop
    } catch {
        throw ("发布目录被占用，先关掉它再试：`n  $outDir`n" +
               "（资源管理器开着这个文件夹、或者 InkTeach 还在运行都会占住它。改名这一步没动任何文件，重试即可。）")
    }
    Remove-Item -LiteralPath $trash -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ---- 发布 -----------------------------------------------------------------------------
#  几个参数各自的理由：
#    --self-contained true     目标机器不用装 .NET（这是"绿色版"的一半含义）
#    PublishAsWinExe=true      双击启动**不弹黑框**。用自定义开关而不是直接写
#                              OutputType=WinExe：后者是 MSBuild 的全局属性，会一起传给
#                              InkEngine / InkUi（库，没有 Main）→ 整个构建报 CS5001。
#    DebugType=None            不带 .pdb（少几个文件、也不暴露内部符号）
$pubArgs = @(
    "publish", $proj,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishAsWinExe=true",
    "-p:DebugType=None",
    "-o", $outDir,
    "--nologo", "-v", "q"
)
if ($SingleFile) { $pubArgs += @("-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true") }

& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# ---- 检查：发布出来那个 exe 真的是**GUI 子系统**吗（"不弹黑框"就靠它）----------------
#   PE 头里 Subsystem 字段：2 = GUI、3 = 控制台。它在 e_lfanew + 0x5C 处（2 字节）。
$exe = Join-Path $outDir "InkTeach.exe"
$fs = [System.IO.File]::OpenRead($exe)
try {
    $br = New-Object System.IO.BinaryReader($fs)
    $fs.Position = 0x3C
    $peOff = $br.ReadInt32()
    $fs.Position = $peOff + 0x5C
    $subsystem = $br.ReadUInt16()
} finally { $fs.Dispose() }
if ($subsystem -ne 2) { throw "发布出来的 exe 子系统是 $subsystem（期望 2 = GUI），双击会弹黑框" }
Write-Host "  exe 子系统 = GUI（双击不弹黑框）" -ForegroundColor Green

# ---- 随手塞一份"怎么用"，省得拷过去之后没人知道怎么退出 ------------------------------
$readme = @"
InkTeach $ver（绿色版，win-x64）

怎么用：双击 InkTeach.exe。它是**一层透明的批注覆盖层**，屏幕底部中间那条就是工具条。

三个最常用的：
  · 写一笔       —— 直接画（默认就是画笔）
  · 让它"过手"   —— 点工具条上的「鼠标（穿透）」，鼠标就还给下面的 PPT / 软件
  · 退出         —— 点工具条的「更多」（最右那格）→「退出」

系统要求：Windows 10 / 11，64 位。**不需要装 .NET**（运行时已经打进来了）。

数据在哪：设置和自动存档都在 %LOCALAPPDATA%\InkTeach\ 与 %APPDATA%\InkTeach\，
删掉这个软件目录本身不影响它们（想彻底清干净就把这两个目录也删了）。

PPT 批注：需要这台机器装了 PowerPoint 或 WPS，放映时工具条会跟着翻页。
（没装也能正常批注，只是不联动。）
"@
Set-Content -Path (Join-Path $outDir "使用说明.txt") -Value $readme -Encoding UTF8

# ---- 压 zip ---------------------------------------------------------------------------
$zip = Join-Path $root "dist\$name.zip"
if (-not $NoZip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zip
}

# ---- update.json（自动更新的清单；配了更新源才生成）--------------------------------------
#
# 这一份要**和 zip 一起挂到 GitHub Release 的附件里**。App 端只认一个恒定地址：
#     <更新源>/update.json
# 其中更新源 = `https://github.com/<账号>/<仓库>/releases/latest/download`
# （每个 release 都挂这三个附件：update.json + zip（以后再加 setup.exe））。
#
# 没配就**不发**（并把上一次留下的删掉）——"没配更新源"在 App 里是正常状态。
$json = Join-Path $root "dist\update.json"
if (-not $updateBase) {
    if (Test-Path $json) { Remove-Item $json -Force }
    $why = if ($NoZip) { "用了 -NoZip，没有 zip 可算 sha256" } else { "没配更新源" }
    Write-Host "  （$why：跳过 update.json；配法见 README「自动更新」）" -ForegroundColor DarkGray
}
elseif ($NoZip) {
    Write-Host "  （-NoZip 时无法算 zip 的 sha256：跳过 update.json）" -ForegroundColor DarkGray
}
else {
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    $base = $updateBase.TrimEnd('/')
    # 更新源既可以是网址（https://…），也可以是局域网共享 / 本地目录（\\服务器\共享、D:\…）。
    # 后者是教室环境最稳的一条路（这台机器实测连不上 github.com，见 README「自动更新」）。
    $url = if ($base -match '^[a-zA-Z][a-zA-Z0-9+.-]*://') { "$base/$name.zip" }
           else { Join-Path $base "$name.zip" }
    $body = @"
{
  "version": "$ver",
  "url": "$url",
  "sha256": "$hash",
  "notes": "",
  "minVersion": ""
}
"@
    Set-Content -Path $json -Value $body -Encoding UTF8
    # **再往仓库根目录写一份**：App 的默认更新源取的是
    #   https://raw.githubusercontent.com/<账号>/<仓库>/main/update.json
    # 为什么不用 release 附件当清单：附件走 CDN，刚发新版时"附件已换、取回来还是旧的"
    #（2026-09-29 实测），而更新检查最需要立刻看到新版。zip 仍旧放 release 附件。
    # ⚠ 发新版时**这两件事都要做**：把这份 update.json 提交推送，再把 zip 传成 release 附件。
    $rootJson = Join-Path $root "update.json"
    Set-Content -Path $rootJson -Value $body -Encoding UTF8
    Write-Host "  自动更新清单 dist\update.json ＋ 仓库根 update.json（sha256 $($hash.Substring(0,12))…）" -ForegroundColor Green
    Write-Host "  ⚠ 发新版：先 git add update.json && git commit && git push（App 从 raw 地址取它），再把 zip 传成 release 附件" -ForegroundColor Yellow
}

$files = (Get-ChildItem $outDir -Recurse -File)
$mb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "完成：" -ForegroundColor Green
Write-Host "  目录 $outDir（$($files.Count) 个文件，$mb MB）"
if (-not $NoZip) { Write-Host "  压缩包 $zip（$([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB）" }
