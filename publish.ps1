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
#  用法（2026-10-05 起：**默认 AOT 原生版**；JIT 是附带选项）：
#    .\publish.ps1                 # 默认：NativeAOT 原生版（启动快约 100ms、约 11MB 单 exe、
#                                  #   免装 .NET；需要 VS Build Tools 的 C++ 工作负载）
#    .\publish.ps1 -Jit            # 附带：JIT/自包含版（保稳选项；产物大、启动稍慢）
#    .\publish.ps1 -SingleFile     # JIT 单文件版（AOT 产物本身就是单文件，会被忽略）
#    .\publish.ps1 -NoZip          # 不压 zip
# =====================================================================================
[CmdletBinding()]
param(
    [switch]$SingleFile,
    [switch]$NoZip,
    [switch]$NoSetup,
    [switch]$Jit,
    [switch]$Aot
)
if ($Jit -and $Aot) { throw "-Jit 和 -Aot 只能选一个（默认就是 AOT）" }
$useAot = -not $Jit

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

# 国内镜像（GitCode：源码随发版推送、zip/Setup 挂发行版附件；2026-10-03 起用）。
# 配了它：① GitHub 清单里多写一个 "cn"（App 优先从国内下 zip）；② 生成 dist\update-mirror.json 留档。
# 留空 = 不做国内镜像。
$mirrorOwnerRepo  = "xzx1xzzx/InkTeach"
$mirrorReleaseBase = if ($mirrorOwnerRepo) { "https://gitcode.com/$mirrorOwnerRepo/releases/download" } else { "" }

# ---- 版本号从工程里读，不在这里再写一份（写两份迟早对不上）----------------------------
$ver = (Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $ver) { throw "没能从 $proj 里读到 <Version>" }

$name = "InkTeach-$ver-win-x64" + $(if ($Jit) { "-jit" } else { "" })
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
if ($SingleFile) {
    if ($useAot) { Write-Host "  （AOT 产物本身就是单文件，-SingleFile 忽略）" -ForegroundColor DarkGray }
    else { $pubArgs += @("-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true") }
}

# NativeAOT（2026-10-05 起**默认**）：需要 VS Build Tools 的 C++ 工作负载（MSVC 链接器）。
# 先做一次友好检查，免得报一行看不懂的 ILC 错误。装法与实测数据见
# 调研-启动内存与WPF对比.md 第九节。
if ($useAot) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vcPath = if (Test-Path $vswhere) {
        & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    } else { "" }
    if (-not $vcPath) {
        throw "AOT 发布需要 VS 2022 Build Tools 的 C++ 工作负载（MSVC 链接器 + Windows SDK）。装法见 调研-启动内存与WPF对比.md 第九节。"
    }
    Write-Host "  AOT：使用 VC 工具链 $vcPath" -ForegroundColor Cyan
    $pubArgs += @("-p:PublishAot=true")
} else {
    Write-Host "  JIT 版（-Jit）：自包含发布（附带选项）" -ForegroundColor DarkGray
}

# PowerShell 5.1 会把原生程序写到 stderr 的**警告**当成错误（$ErrorActionPreference=Stop
# 下直接抛 NativeCommandError）——AOT 发布必带一条 SharpGen 的 IL2104 警告，必须放行；
# 真正的失败仍然用退出码判。
$eap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& dotnet @pubArgs
$code = $LASTEXITCODE
$ErrorActionPreference = $eap
if ($code -ne 0) { throw "dotnet publish 失败（退出码 $code）" }

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

# NativeAOT 的**原生 .pdb**（约 41MB）不受 DebugType=None 控制、总是会生成。
# 它是调试符号，发布包里不要（老流程根本没有）：删掉，需要调试时用 bin 下的那份。
Get-ChildItem $outDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# ---- 随手塞一份"怎么用"，省得拷过去之后没人知道怎么退出 ------------------------------
$readme = @"
InkTeach $ver（win-x64）

怎么用：双击**和「使用说明」文件夹并排的 InkTeach.exe**（装了安装包的话，桌面/开始菜单的快捷方式就是它）。
它是一层透明的批注覆盖层，屏幕底部中间那条就是工具条。

三个最常用的：
  · 写一笔       —— 直接画（默认就是画笔）
  · 让它"过手"   —— 点工具条上的「鼠标（穿透）」，鼠标就还给下面的 PPT / 软件
  · 退出         —— 点工具条的「更多」（最右那格）→「退出」

新版本：点「更多」→「检查更新」（会在后台查，按提示点第二下才开始下载，
下载完校验完自己换壳重启；也会自动从国内加速站取，连不上 GitHub 也能用）。

快捷键（默认键盘归批注层，直接按就行）：
  笔 Ctrl+P ／ 荧光笔 Ctrl+I ／ 激光笔 Ctrl+L ／ 橡皮 Ctrl+E ／ 选中 Ctrl+M
  ★ 已经是那个工具时，再按一次就是"换一个"：笔/荧光笔换颜色（转圈）、
    橡皮在"整笔擦 ⇄ 面积擦"之间换、选中在"矩形框 ⇄ 自由套索"之间换。
  ★ 穿透开着时这五个工具键不响应（先退出穿透再画）；面板上的工具格照常——
    点一格会顺手把穿透关掉。退出穿透后快捷键立刻恢复。
  ★ 呼出盘 Ctrl+Alt+Shift+Q（全局）：按住不放 → 光标处出八扇面 → 划向要的工具/颜色 → 松手切换
    （正北是笔；上下左右＝笔/红/橡皮/荧光笔，四个角＝黑/蓝/框选/激光）。
    开着穿透也能按：选一个扇区就退出穿透并切到它；想继续用下面的程序就把鼠标松在盘心。
  撤销 Ctrl+Z ／ 重做 Ctrl+Y ／ 截图 Ctrl+S ／ 清空 Ctrl+Shift+C ／ 换粗细 Ctrl+6
  白板翻上一屏/下一屏 PageUp / PageDown ／ 微调选中对象 方向键（Shift+方向 = 10 像素）
全局热键（任何程序在前台都生效）：穿透 Ctrl+Alt+Shift+T ／ 退出 Ctrl+Alt+Shift+X ／
  呼出盘 Ctrl+Alt+Shift+Q
放映 PPT/WPS 时：Ctrl+P/I/L/E 同样有效（程序会自动把工具键临时升级为全局热键）；
  ←→ 翻页、↑↓ 滚画布（有选中对象时改成微调）。开穿透时这 8 个键让给 PPT/WPS 自己
  （原生翻页、Ctrl+P/E/L/Z 恢复），退出穿透立刻收回。

系统要求：Windows 10 / 11，64 位。**不需要装 .NET**（运行时已经打进来了）。

数据在哪：设置和自动存档都在 %LOCALAPPDATA%\InkTeach\ 与 %APPDATA%\InkTeach\，
删掉这个软件目录本身不影响它们（想彻底清干净就把这两个目录也删了）。

PPT 批注：需要这台机器装了 PowerPoint 或 WPS，放映时工具条会跟着翻页。
（没装也能正常批注，只是不联动。）
"@
# 使用说明放进子文件夹：绿色版解压后**根目录只有一个 InkTeach.exe**——
# 不用在文档堆里找哪个才是能运行的软件（用户 2026-10-05 反馈）。
$docDir = Join-Path $outDir "使用说明"
New-Item -ItemType Directory -Path $docDir -Force | Out-Null
Set-Content -Path (Join-Path $docDir "使用说明.txt") -Value $readme -Encoding UTF8

# ---- 压 zip ---------------------------------------------------------------------------
$zip = Join-Path $root "dist\$name.zip"
if (-not $NoZip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # 刚写出来的 dll 可能正被杀软/索引器扫着——2026-09-29 真踩过：Compress-Archive
    # 报 PermissionDenied，整个发布卡在"压包"这一步（前面的目录已经重发过了）。
    # 退一步重试三次；还不行就抛出去（那时的报错信息才是有用的）。
    $zipped = $false
    for ($try = 1; $try -le 3 -and -not $zipped; $try++) {
        try {
            Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zip -ErrorAction Stop
            $zipped = $true
        } catch {
            if ($try -ge 3) { throw }
            Write-Host ("  压包被占用（第 $try 次失败），3 秒后重试…") -ForegroundColor Yellow
            Start-Sleep -Seconds 3
        }
    }
}

# ---- 安装包（Inno Setup；没装 Inno 就跳过——发布绿色版不受影响）---------------------------
#
#  为什么是"每用户安装"（PrivilegesRequired=lowest）：不弹 UAC，而且 App 能写自己的
#  目录 → "下载 → 换壳 → 重启"的自动更新在安装版上**照样能用**（装到 Program Files
#  就得每次更新弹 UAC）。细节见 installer\InkTeach.iss 的注释。
$setup = Join-Path $root "dist\InkTeach-Setup-$ver.exe"
if ($NoSetup) {
    Write-Host "  （-NoSetup：跳过安装包）" -ForegroundColor DarkGray
} else {
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Host "  （没找到 Inno Setup 的 ISCC.exe：跳过安装包。装 Inno Setup 6 即可，见 README「安装」）" -ForegroundColor DarkGray
    } else {
        $iss = Join-Path $root 'installer\InkTeach.iss'
        & $iscc "/DAppVersion=$ver" "/DPayloadDir=$outDir" "/DOutDir=$(Join-Path $root 'dist')" $iss | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setup)) { throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }
        Write-Host ("  安装包 dist\{0}（{1:N1} MB）" -f (Split-Path $setup -Leaf), ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
    }
}

# ---- 固定名副本（官网"永不失效"的免登录直链用）--------------------------------------
#
#  官网按钮用的是 **固定文件名** 的直链，这样每次发版网页不用改：
#    https://gitcode.com/xzx1xzzx/InkTeach/releases/download/latest/InkTeach-Setup.exe
#    https://gitcode.com/xzx1xzzx/InkTeach/releases/download/latest/InkTeach-win-x64.zip
#  （`latest` 是 GitCode/GitHub 都支持的"最新发行版"别名；实测匿名 GET 可下、不要登录。）
#  上传发行版附件时把这两个固定名文件也带上：
#    · GitCode：tools\gitcode-release.ps1 已自动带上；
#    · GitHub：`gh release upload <tag> dist\InkTeach-Setup.exe dist\InkTeach-win-x64.zip`
$stableZip = Join-Path $root "dist\InkTeach-win-x64.zip"
$stableSetup = Join-Path $root "dist\InkTeach-Setup.exe"
if (Test-Path $zip)   { Copy-Item $zip   $stableZip   -Force }
if (Test-Path $setup) { Copy-Item $setup $stableSetup -Force }
if (Test-Path $stableZip)   { Write-Host "  固定名副本 dist\InkTeach-win-x64.zip（官网直链用）" -ForegroundColor DarkGray }
if (Test-Path $stableSetup) { Write-Host "  固定名副本 dist\InkTeach-Setup.exe（官网直链用）" -ForegroundColor DarkGray }

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
    # 国内直链（Gitee 发行版附件）：格式与 GitHub 同款，发布时即可推算，无需先上传。
    $cn = if ($mirrorReleaseBase) { "$($mirrorReleaseBase.TrimEnd('/'))/v$ver/$name.zip" } else { "" }
    $body = @"
{
  "version": "$ver",
  "url": "$url",
  "cn": "$cn",
  "sha256": "$hash",
  "notes": "",
  "minVersion": ""
}
"@
    Set-Content -Path $json -Value $body -Encoding UTF8
    if ($cn) {
        # 镜像清单留档（App 实际用的是 GitHub 清单里的 "cn" 字段；这份给人核对/兜底用）。
        $mirrorJson = Join-Path $root "dist\update-mirror.json"
        $mirrorBody = @"
{
  "version": "$ver",
  "url": "$cn",
  "sha256": "$hash",
  "notes": "",
  "minVersion": ""
}
"@
        Set-Content -Path $mirrorJson -Value $mirrorBody -Encoding UTF8
        Write-Host "  镜像清单 dist\update-mirror.json（$cn）" -ForegroundColor Green
    }
    # **再往仓库根目录写一份**：App 的默认更新源取的是
    #   https://raw.githubusercontent.com/<账号>/<仓库>/main/update.json
    # 为什么不用 release 附件当清单：附件走 CDN，刚发新版时"附件已换、取回来还是旧的"
    #（2026-09-29 实测），而更新检查最需要立刻看到新版。zip 仍旧放 release 附件。
    # ⚠ 发新版时**这两件事都要做**：把这份 update.json 提交推送，再把 zip 传成 release 附件。
    $rootJson = Join-Path $root "update.json"
    Set-Content -Path $rootJson -Value $body -Encoding UTF8
    # 让 jsDelivr（国内清单源之一）立刻刷新缓存；失败不影响发布。
    try { curl.exe -s -o NUL --max-time 20 "https://purge.jsdelivr.net/gh/XueRenYi0/InkTeach@main/update.json" | Out-Null } catch { }
    Write-Host "  自动更新清单 dist\update.json ＋ 仓库根 update.json（sha256 $($hash.Substring(0,12))…）" -ForegroundColor Green
    Write-Host "  ⚠ 发新版：先 git add update.json && git commit && git push（App 从 raw 地址取它），再把 zip 和 setup.exe 一起挂到 release 附件" -ForegroundColor Yellow
    if ($mirrorReleaseBase) {
        Write-Host "  ⚠ 国内镜像：跑 tools\gitcode-release.ps1 -Version $ver（推源码/标签、建 GitCode 发行版、传附件）" -ForegroundColor Yellow
    }
}

$files = (Get-ChildItem $outDir -Recurse -File)
$mb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "完成：" -ForegroundColor Green
Write-Host "  目录 $outDir（$($files.Count) 个文件，$mb MB）"
if (-not $NoZip) { Write-Host "  压缩包 $zip（$([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB）" }
if (-not $NoSetup -and (Test-Path $setup)) { Write-Host ("  安装包 $setup（{0:N1} MB）" -f ((Get-Item $setup).Length / 1MB)) }
