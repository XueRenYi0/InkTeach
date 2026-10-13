<#
准备「界面」用的图标路径。默认给设计稿出图工具用，也可以直接出给产品界面层（src/InkUi）。

用法：
    pwsh tools/gen-ui-icons.ps1                      # → tools/design-sheet/IconPaths.g.cs（默认）
    pwsh tools/gen-ui-icons.ps1 -Out src/InkUi/Icons.g.cs -Namespace InkUi -Class PanelIcons

和 tools/gen-fluent-icons.ps1 / gen-icons.ps1 同一套路：只从上游 SVG 里取
path 的 d 数据，不引入任何运行时代码；下载走 jsDelivr 镜像（国内直连
raw.githubusercontent.com 经常超时），并带本地缓存。

和那两支的区别：这张表是**探测式的**——每个位置给一串候选名，谁先下载到
就用谁，并且把「哪些候选名在上游根本不存在」打印出来。原因是 Fluent 的
命名不一定和我们的叫法一致（"荧光笔"是 Highlight 不是 Highlighter），
而这一轮的结论要能直接落到 src 里的图标表上，所以探测结果本身就是要留的证据。
#>

param(
    [string]$Out = 'tools/design-sheet/IconPaths.g.cs',
    [string]$Namespace = 'DesignSheet',
    [string]$Class = 'IconPaths'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$outFile = Join-Path $repoRoot $Out
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outFile) | Out-Null
$cacheDir = Join-Path ([System.IO.Path]::GetTempPath()) 'fluent-icons'
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

$base = 'https://cdn.jsdelivr.net/gh/microsoft/fluentui-system-icons@main/assets'

# 本地名字 → 上游目录候选（按顺序试）。regular 变体，常态用。
$icons = [ordered]@{
    # ⚠ 'mouse' 不在这里：它现在用的是 **MDI 的鼠标设备**（见下面 $foreignIcons）——
    # 2026-09-26 用户："把'穿透'的图标换成鼠标"（原来那个是 Fluent `Cursor` ＝ 一根箭头指针，
    # 和"选择"工具那根箭头容易混；穿透这件事说的是"鼠标点穿到底下那个程序"，画一只鼠标才对）。
    'pen'         = @('Pen')
    'highlighter' = @('Highlight')
    'laser'       = @('Flash')
    'eraser'      = @('Eraser')
    # 「橡皮」那一格**没选中时的固定图标**（用户 2026-09-26："橡皮就是第一个大板擦是没有问题的"）：
    # Fluent 的 Eraser Tool ＝ 白板那块大板擦，而且它**有描边／实心一对**——
    # 正好和这一排的 regular／filled 规矩对上，不用自己配。
    'eraserTool'  = @('Eraser Tool')
    # 「面积擦」那一格用的（2026-09-26 用户挑的）：Fluent 的 Eraser Medium——
    # 橡皮 ＋ 一个大圈（圈在橡皮那儿断开），"擦多大一块"一眼看得出来；
    # 顺便它**不带底下那道横线**，正好也是"格子上的固定橡皮"要的形状。
    'eraserMedium' = @('Eraser Medium')
    'select'      = @('Select Object', 'Lasso')
    'lasso'       = @('Lasso')
    'shapes'      = @('Shapes')
    'capture'     = @('Screenshot', 'Camera')
    'undo'        = @('Arrow Undo')
    'redo'        = @('Arrow Redo')
    'settings'    = @('Settings')
    'lineWeight'  = @('Line Horizontal 1')
    'color'       = @('Color')
    'check'       = @('Checkmark')
    'more'        = @('More Horizontal')
    'dismiss'     = @('Dismiss')
    'board'       = @('Board', 'Whiteboard')
    'passthrough' = @('Eye Off')
    'delete'      = @('Delete')
    'chevronDown' = @('Chevron Down')
    'chevronUp'   = @('Chevron Up')
    'chevronRight'= @('Chevron Right')
    'pin'         = @('Pin')
    'square'      = @('Square')
    'circle'      = @('Circle')
    'triangle'    = @('Triangle')
    'arrowRight'  = @('Arrow Right')
    # 激光笔的几个候选（上游没有"激光笔"这个专名，只能从近义图标里挑）
    'laserFlash'      = @('Flash')
    'laserFlashlight' = @('Flashlight')
    'laserRecord'     = @('Record')
    'laserCircle'     = @('Circle')
    'laserTarget'     = @('Target')
    'laserWand'       = @('Wand')
    'laserSparkle'    = @('Sparkle')
    # 设置条右端那两个"动作"按钮
    'broom'           = @('Broom')
    'selectAll'       = @('Square Multiple')
    # "更多"入口的候选：省略号 vs 宫格（工具箱）
    'apps'            = @('Apps')
    'grid'            = @('Grid')
    # 「更多」抽屉里那几个
    'arrowSync'       = @('Arrow Sync')
    'arrowClockwise'  = @('Arrow Clockwise')
    'power'           = @('Power')
    'darkTheme'       = @('Dark Theme')
    'dockRow'         = @('Dock Row')
    'ruler'           = @('Ruler')
    'mathFormula'     = @('Math Formula')
    # 截屏图标的备选（上游有几枚近义，逐个拉下来比）
    'shotRecord'      = @('Screenshot Record')
    'camera'          = @('Camera')
    'crop'            = @('Crop')
    'scan'            = @('Scan')
    'scanObject'      = @('Scan Object')
    'windowIco'       = @('Window')
    'desktop'         = @('Desktop')
    'maximize'        = @('Full Screen Maximize')
    'image'           = @('Image')
}

# 激活态用的 filled 变体（Windows 11 的惯例：常态 regular、选中 filled）
$filledIcons = [ordered]@{
    'penFilled'         = @('Pen')
    'highlighterFilled' = @('Highlight')
    'laserFilled'       = @('Flash')
    'laserRecordFilled' = @('Record')
    'eraserFilled'      = @('Eraser')
    'eraserToolFilled'  = @('Eraser Tool')
    'selectFilled'      = @('Select Object')
    'shapesFilled'      = @('Shapes')
    'captureFilled'     = @('Screenshot')
    # 截屏工具的候选（用户 2026-09-17："截图图标和选中图标一样的，是不是不大好？"）——
    # Fluent Screenshot 是"方框 + 四角括号"，和选择工具的"虚线方框 + 角点"缩到 24 像素几乎分不出。
    # 相机最不会认错，所以常/选两态都用它。
    'cameraFilled'      = @('Camera')
    'undoFilled'        = @('Arrow Undo')
    'redoFilled'        = @('Arrow Redo')
    'settingsFilled'    = @('Settings')
    'moreFilled'        = @('More Horizontal')
}

# 每张外库图标来自哪一家（写进生成的注释里，便于追许可：MDI/Lucide/Tabler/Radix 都要留版权声明）
$foreignSource = @{
    'mdiSelection'        = 'Material Design Icons (Apache-2.0)'
    'lucideSelectPointer' = 'Lucide (ISC)'
    # 穿透那一格（用户 2026-09-26 挑的 ④）：Tabler 的指针箭头，描边／实心一对
    'cursorArrow'         = 'Tabler Icons (MIT)'
    'cursorArrowFilled'   = 'Tabler Icons (MIT)'
    # 橡皮那一格的休息态（用户 2026-09-26 从候选里挑的 B2）
    'eraserPure'          = 'Radix Icons (MIT)'
    # 激光笔那一格（用户 2026-09-27 挑的）：**还是 Fluent 自家的**，
    # 只是它没出 24 像素版、上游只有 20 的（网格 20，生成出来会带 laserToolBox 常量）
    'laserTool'           = 'Fluent UI System Icons (MIT)'
    'laserToolFilled'     = 'Fluent UI System Icons (MIT)'
}

# 外部图标库的候选（只用来给"上游没有专名"的语义做对比，都是各自许可证下的图形）
#   Material Symbols（Apache-2.0）：有专名 stylus_laser_pointer —— 就是"笔 + 激光"
#   它们用的是 960 网格、填充式路径，所以生成出来会额外带一个 viewBox 常量
$foreignIcons = [ordered]@{
    # MDI「四角括号 ＋ 断续边」＝ 框选那一格（Apache-2.0）。用户 2026-09-26 挑的：
    # 比 Fluent 那张"四角点 ＋ 断续边"更利落，缩到 24 像素边线不糊。
    'mdiSelection'      = 'https://cdn.jsdelivr.net/npm/@mdi/svg@latest/svg/selection.svg'
    # Lucide「虚线框 ＋ 指针」＝ 选择工具那格子上的固定图标（ISC）。
    # ⚠ 它是**描边型**（fill="none" + stroke），不是填充型：路径要"描"不要"填"，
    # 见 IconAtlas 里那张 StrokedIcons 表；它的圆角是 `a`（圆弧），SvgPath 同日补了圆弧支持。
    'lucideSelectPointer' = 'https://cdn.jsdelivr.net/npm/lucide-static@latest/icons/square-dashed-mouse-pointer.svg'
    # Tabler「指针箭头」＝ 穿透那一格（用户 2026-09-26 从候选里挑的 ④：
    # "就是不知道3号、4号哪个更搭配"→ 结论是 4，理由是它是**描边＋实心一对**
    # （pointer / pointer-filled），和 Fluent 那批 regular／filled 一个规矩；
    # Phosphor 那张尾巴太细，缩到 24 像素尾巴快看不见了）。
    # ⚠ Tabler 是**描边型**（fill="none" + stroke-width 2），实心那张才是填充型：
    # 描边那张要进 IconAtlas 的 StrokedIcons 表。
    'cursorArrow'       = 'https://cdn.jsdelivr.net/npm/@tabler/icons@latest/icons/outline/pointer.svg'
    # ⚠ filled 那个文件夹里的文件名**不带 `-filled` 后缀**（目录已经说明是实心了）：
    # 写 `pointer-filled.svg` 会 404（2026-09-26 第一次就是这么失败、被脚本的"跳过"提示抓到的）。
    'cursorArrowFilled' = 'https://cdn.jsdelivr.net/npm/@tabler/icons@latest/icons/filled/pointer.svg'
    # Radix「橡皮」＝ 橡皮那一格**休息态**的固定图标（用户 2026-09-26 从七个候选里挑的 B2）。
    # 为什么挑它：用户要"纯粹的橡皮、不要那种横线"——这一族里它是**路径中一根横线都没有**
    # 的那张（斜长方块 ＋ 一道斜的内部分界线），而且 15 网格的圆角比别的库细腻。
    # ⚠ 它**不是 24 网格**（viewBox="0 0 15 15"），生成出来会带 eraserPureBox 常量；
    # 缩放由 IconAtlas.Draw 里那段"按 Box 常量缩放"统一处理，别在这里预先放大。
    # 下载用的是 Radix **官方 npm 包里的原图**（比 Iconify 转手过的版本更权威）。
    'eraserPure'        = 'https://cdn.jsdelivr.net/npm/@radix-ui/react-icons@latest/icons/eraser.svg'
    'msStylusLaser'     = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/stylus_laser_pointer.svg'
    'msStylusLaserFill' = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/rounded/stylus_laser_pointer-fill.svg'
    'msPointScan'       = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/point_scan.svg'
    'msFlashlightOn'    = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/flashlight_on.svg'
    'msStylus'          = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/stylus.svg'
    # ---- 激光笔那一格（用户 2026-09-27 从一批候选笔里挑的）----
    # **Fluent 自家的 `Laser Tool`**：它就是"一颗笔头 ＋ 笔尖外一圈短射线"，
    # 和用户给的 ClassIn 参考图是同一个构成，**不用自己加杠**。
    # ⚠ 上游**只出了 20 像素版**（24 那份没有），所以要连 viewBox 一起拿：
    #   画的时候会按 `0 0 20 20` 缩放到目标尺寸（见 IconAtlas.Draw 的 Box 处理）。
    #   顺带一个好处：20 网格的线宽 ≈1.33，放大到 24 像素显示刚好 ≈1.6，
    #   和我们这一排 Fluent regular 的 1.5 是一个量级。
    'laserTool'         = 'https://cdn.jsdelivr.net/gh/microsoft/fluentui-system-icons@main/assets/Laser%20Tool/SVG/ic_fluent_laser_tool_20_regular.svg'
    'laserToolFilled'   = 'https://cdn.jsdelivr.net/gh/microsoft/fluentui-system-icons@main/assets/Laser%20Tool/SVG/ic_fluent_laser_tool_20_filled.svg'
}

$used = @{}
$log = @()

function Get-UpstreamSvg([string]$folder, [string]$slug, [string]$variant) {
    $fileName = "ic_fluent_${slug}_24_${variant}.svg"
    $cache = Join-Path $cacheDir ($folder + '_' + $fileName)
    if (Test-Path $cache) { return Get-Content $cache -Raw }

    $url = "$base/" + [uri]::EscapeDataString($folder) + '/SVG/' + [uri]::EscapeDataString($fileName)
    try {
        $text = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20).Content
    } catch {
        return $null
    }
    Set-Content -Path $cache -Value $text -NoNewline
    return $text
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('// <auto-generated>')
[void]$sb.AppendLine('// 由 tools/gen-ui-icons.ps1 从微软官方图标库生成，请勿手改。')
[void]$sb.AppendLine('// 来源：https://github.com/microsoft/fluentui-system-icons （MIT License，')
[void]$sb.AppendLine('// Copyright (c) 2020 Microsoft Corporation）')
[void]$sb.AppendLine('// 两套变体都有：regular（常态）与 filled（激活态），viewBox 都是 0 0 24 24。')
[void]$sb.AppendLine('//')
[void]$sb.AppendLine('// 另有几张**外部图标库**的图（$foreignIcons 那张表），各自带 viewBox：')
[void]$sb.AppendLine('//   · Material Symbols（Google，Apache-2.0）—— stylus_laser_pointer 等；')
[void]$sb.AppendLine('//     （这几张是早前做"激光笔候选图"时拉下来的，界面现在没有引用它们）');
[void]$sb.AppendLine('//   · Material Design Icons（Pictogrammers，Apache-2.0）—— 框选那张 mdiSelection；')
[void]$sb.AppendLine('//   · Lucide（ISC）—— 选择工具那张 lucideSelectPointer（**描边型**，见 IconAtlas 的 StrokedIcons）；')
[void]$sb.AppendLine('//   · Tabler（MIT）—— 穿透那张 cursorArrow（描边）／cursorArrowFilled（实心）；')
[void]$sb.AppendLine('//   · Radix（MIT）—— 橡皮休息态那张 eraserPure（15 网格）。')
[void]$sb.AppendLine('//   · Fluent 自家但**不是 24 网格**的：激光笔那张 laserTool / laserToolFilled（20 网格，')
[void]$sb.AppendLine('//     上游只出了 20 像素版）。')
[void]$sb.AppendLine('// 由 -Out/-Namespace/-Class 决定落到哪个工程：设计稿出图工具，或产品界面层 src/InkUi。')
[void]$sb.AppendLine('// </auto-generated>')
[void]$sb.AppendLine()
[void]$sb.AppendLine("namespace $Namespace;")
[void]$sb.AppendLine()
[void]$sb.AppendLine("internal static partial class $Class")
[void]$sb.AppendLine('{')

$allKeys = @()
foreach ($table in @(@($icons, 'regular'), @($filledIcons, 'filled'))) {
    $map = $table[0]; $variant = $table[1]
    foreach ($key in $map.Keys) {
        $found = $null; $foundName = $null
        foreach ($name in $map[$key]) {
            $slug = ($name -replace ' ', '_').ToLowerInvariant()
            $svg = Get-UpstreamSvg $name $slug $variant
            if ($svg) { $found = $svg; $foundName = $name; break }
        }
        if (-not $found) { throw "$key 的所有候选名都下载失败：$($map[$key] -join ', ')" }

        # ⚠ 前面那个 (?<=\s) 是必须的：光写 d="..." 的话，`id="mdi-selection"` 里
    # 尾巴上那截 `d="mdi-selection"` 也会被当成路径 —— 结果生成出来的 d 前面
    # 多出 "mdi-selection " 一截（2026-09-26 加 MDI 那张图时踩到，出图时字形全乱）。
    $m = [regex]::Matches($found, '(?<=\s)d="([^"]+)"')
        if ($m.Count -eq 0) { throw "$foundName 里没有找到路径数据" }
        $d = ($m | ForEach-Object { $_.Groups[1].Value }) -join ' '

        [void]$sb.AppendLine("    /// <summary>Fluent: $foundName (24 $variant)</summary>")
        [void]$sb.AppendLine("    public const string $key =")
        [void]$sb.AppendLine('        "' + $d + '";')
        [void]$sb.AppendLine()

        foreach ($ch in [regex]::Matches($d, '[A-Za-z]')) { $used[$ch.Value] = $true }
        $log += ("{0,-19} {1,-18} {2,-8} {3} 字符" -f $key, $foundName, $variant, $d.Length)
        $allKeys += $key
    }
}

# 外部库：路径 + viewBox 一起生成
$boxes = @()
foreach ($key in $foreignIcons.Keys) {
    try { $svg = (Invoke-WebRequest -Uri $foreignIcons[$key] -UseBasicParsing -TimeoutSec 25).Content }
    catch { Write-Host "  跳过 $key（下载失败）"; continue }
    $vb = [regex]::Match($svg, 'viewBox="([^"]+)"').Groups[1].Value
    $m = [regex]::Matches($svg, '(?<=\s)d="([^"]+)"')   # 前面那个 (?<=\s) 的理由见上面那处
    if ($m.Count -eq 0) { Write-Host "  跳过 $key（没有路径）"; continue }
    # **剔掉"整格垫片"那一笔**：Tabler 的 SVG 文件里第一段固定是
    # `<path stroke="none" d="M0 0h24v24H0z" fill="none"/>`（一个 24×24 的方框，
    # 本意是撑住画布）。对我们有害无益：**实心那张会被它填成一整块黑方块**，
    # 描边那张会多出一圈方框（2026-09-26 加 Tabler 光标时踩到，diff 里一眼看到
    # 多出个 `M0 0h24v24H0z`）。M 段是"整格方框"就丢掉——按这个形状认，不认具体库。
    $paths = @($m | ForEach-Object { $_.Groups[1].Value } |
               Where-Object { -not ($_.Trim() -match '^M0 0h24v24H0z$') })
    if ($paths.Count -eq 0) { Write-Host "  跳过 $key（只剩垫片那一笔）"; continue }
    $d = $paths -join ' '
    $src = if ($foreignSource.ContainsKey($key)) { $foreignSource[$key] } else { 'Material Symbols' }
    [void]$sb.AppendLine("    /// <summary>${src}: $key（${vb} 网格）</summary>")
    [void]$sb.AppendLine("    public const string $key =")
    [void]$sb.AppendLine('        "' + $d + '";')
    [void]$sb.AppendLine("    public const string ${key}Box = `"$vb`";")
    [void]$sb.AppendLine()
    $log += ("{0,-21} {1,-34} {2,-8} {3} 字符" -f $key, $src, 'foreign', $d.Length)
    # 外库图标也要统计用到的命令：末尾那行"出现过的 SVG 命令"是给 SvgPath 看的
    #（它只支持统计出来的那些命令），漏掉外库的话那行就成了假话——
    # 比如圆弧 a/A 就是这次加外库图标才暴露出来的（SvgPath 当天补上了支持）。
    foreach ($ch in [regex]::Matches($d, '[A-Za-z]')) { $used[$ch.Value] = $true }
    $allKeys += $key
    $boxes += $key
}

[void]$sb.AppendLine('    /// <summary>按本地名字取路径数据（名字拼错时当场抛，别画出一个空图标）。</summary>')
[void]$sb.AppendLine('    public static string Get(string name) => name switch')
[void]$sb.AppendLine('    {')
foreach ($key in $allKeys) {
    [void]$sb.AppendLine('        "' + $key + '" => ' + $key + ',')
}
[void]$sb.AppendLine('        _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "图标表里没有这个名字"),')
[void]$sb.AppendLine('    };')
[void]$sb.AppendLine()
[void]$sb.AppendLine('    /// <summary>外部库的图标带自己的 viewBox（比如 Material 用的是 960 网格）；没有就按 24 网格处理。</summary>')
[void]$sb.AppendLine('    public static bool TryGetBox(string name, out string box)')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        box = name switch')
[void]$sb.AppendLine('        {')
foreach ($key in $boxes) {
    [void]$sb.AppendLine('            "' + $key + '" => ' + $key + 'Box,')
}
[void]$sb.AppendLine('            _ => null,')
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('        return box != null;')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')
[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($true))

Write-Host "已生成 $outFile"
$log | ForEach-Object { Write-Host "  $_" }
Write-Host ("出现过的 SVG 命令: " + (($used.Keys | Sort-Object) -join ' '))
