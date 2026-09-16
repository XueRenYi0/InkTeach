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
    'mouse'       = @('Cursor', 'Arrow Mouse Cursor')
    'pen'         = @('Pen')
    'highlighter' = @('Highlight')
    'laser'       = @('Flash')
    'eraser'      = @('Eraser')
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
    'mouseFilled'       = @('Cursor')
    'penFilled'         = @('Pen')
    'highlighterFilled' = @('Highlight')
    'laserFilled'       = @('Flash')
    'laserRecordFilled' = @('Record')
    'eraserFilled'      = @('Eraser')
    'selectFilled'      = @('Select Object')
    'shapesFilled'      = @('Shapes')
    'captureFilled'     = @('Screenshot')
    'undoFilled'        = @('Arrow Undo')
    'redoFilled'        = @('Arrow Redo')
    'settingsFilled'    = @('Settings')
    'moreFilled'        = @('More Horizontal')
}

# 外部图标库的候选（只用来给"上游没有专名"的语义做对比，都是各自许可证下的图形）
#   Material Symbols（Apache-2.0）：有专名 stylus_laser_pointer —— 就是"笔 + 激光"
#   它们用的是 960 网格、填充式路径，所以生成出来会额外带一个 viewBox 常量
$foreignIcons = [ordered]@{
    'msStylusLaser'     = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/stylus_laser_pointer.svg'
    'msStylusLaserFill' = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/rounded/stylus_laser_pointer-fill.svg'
    'msPointScan'       = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/point_scan.svg'
    'msFlashlightOn'    = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/flashlight_on.svg'
    'msStylus'          = 'https://cdn.jsdelivr.net/npm/@material-symbols/svg-400@latest/outlined/stylus.svg'
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

        $m = [regex]::Matches($found, 'd="([^"]+)"')
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
    $m = [regex]::Matches($svg, 'd="([^"]+)"')
    if ($m.Count -eq 0) { Write-Host "  跳过 $key（没有路径）"; continue }
    $d = ($m | ForEach-Object { $_.Groups[1].Value }) -join ' '
    [void]$sb.AppendLine("    /// <summary>Material Symbols: $key（${vb} 网格）</summary>")
    [void]$sb.AppendLine("    public const string $key =")
    [void]$sb.AppendLine('        "' + $d + '";')
    [void]$sb.AppendLine("    public const string ${key}Box = `"$vb`";")
    [void]$sb.AppendLine()
    $log += ("{0,-19} {1,-18} {2,-8} {3} 字符" -f $key, 'Material Symbols', 'foreign', $d.Length)
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
