$repo = "C:\Users\LHL-XZX\Desktop\软件\InkTeach"
Set-Location $repo
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$appPath = "$repo\src\InkTeach\App.cs"
$outDir = "$repo\src\InkTeach\SelfTests"

$groups = [ordered]@{
  'Navigation'   = @('CameraTest','WheelTest','PageTest','CoordTest','ScrollWriteTest','ScrollFlashTest','SmoothFlashTest','PrevFlashTest')
  'BoardAndLaser'= @('PatternTest','LaserTest','CursorTest','TileTest','PrefetchTest','GhostTest','SelfTest')
  'InkShape'     = @('SelfCrossTest','HoleTest','CornerTest','CapTest','WidthTest','DashTest','PrismTest','ResolutionTest')
  'InkMotion'    = @('CurveTest','WetDryTest','InkTrailTest','WetInkTest','WetInkLiveTest')   # TailJump/Predictor/PredictTail 在已停用的块注释里，不是真方法
  'Selection'    = @('SelTest','SelBenchTest','LassoTest','EditTest','HandleTest','RotateTest','TransformTest','ImageTest','CaptureTest')
  'Erasers'      = @('EraserTest','PixelEraserTest','DynEraserTest')
  'PanelAndKeys' = @('PanelTest','ShapeBandTest','UiInputTest','KeyTest','HotkeyTest','PassThroughTest','RadialTest')
  'Touch'        = @('TouchTest','TouchGuardTest')
  'ShapeLibrary' = @('ShapeTest','LibraryTest','ShapeToolTest')
  'Diagnostics'  = @('WriteTest','MemLifeTest','GcLatencyTest','LongRunTest','ProbeHitTest','PressureDiagTest','InputPathTest','ReplayTest','InkFileTest','PressureTest','AxisTest','DupTest','AppendPathTest','RecoveryTest')
  'Helpers'      = @('GenerateStrokes','GenerateStrokesSpread','MakeCoordinateForTest','MakeTestImage','MakeNumberLineForTest')
}
$alreadyDone = @('PptTest','IoTest','ClipboardTest','SaveTest')

$lines = [System.IO.File]::ReadAllLines($appPath)

# ★ 第一步：标出"每一行是否处在 /* */ 块注释里"
$inBlock = New-Object 'bool[]' $lines.Count
$open = $false
for ($i = 0; $i -lt $lines.Count; $i++) {
    $o = ([regex]::Matches($lines[$i], '/\*')).Count
    $c = ([regex]::Matches($lines[$i], '\*/')).Count
    if (-not $open) {
        if ($o -gt $c) { $inBlock[$i] = $true; $open = $true }
    } else {
        $inBlock[$i] = $true
        if ($c -gt 0) { $open = $false }
    }
}
Write-Host ("  App.cs " + $lines.Count + " 行；块注释里 " + (($inBlock | Where-Object { $_ }) | Measure-Object).Count + " 行")

# ★ 第二步：成员索引 —— **跳过块注释里的行**（否则注释里的旧方法会被当成成员，把整块劈开）
$members = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($inBlock[$i]) { continue }
    if ($lines[$i] -match '^    (private|internal|public|protected|static)\s') {
        $name = $null
        if ($lines[$i] -match '([A-Za-z_]\w*)\s*\(') { $name = $Matches[1] }
        elseif ($lines[$i] -match '([A-Za-z_]\w*)\s*[;=]') { $name = $Matches[1] }
        if ($name) { $members += [pscustomobject]@{ Line = $i; Name = $name } }
    }
}
$classEnd = $lines.Count - 1
for ($i = $lines.Count - 1; $i -ge 0; $i--) { if (-not $inBlock[$i] -and $lines[$i] -match '^\}') { $classEnd = $i; break } }
Write-Host ("  成员 " + $members.Count + " 个；类收尾在第 " + ($classEnd+1) + " 行")

# ★ 第三步：识别自检方法（区间 = 本成员 .. 下一个成员前；注释连块一起吞）
$blocks = @()
for ($k = 0; $k -lt $members.Count; $k++) {
    $s = $members[$k].Line
    $e = if ($k + 1 -lt $members.Count) { $members[$k + 1].Line - 1 } else { $classEnd - 1 }
    if ($e -gt $classEnd - 1) { $e = $classEnd - 1 }
    $decl = $s
    while ($decl -le $e -and ($lines[$decl] -match '^\s*(///|//|\s*$)' -or $lines[$decl] -match '^\s*\*')) { $decl++ }
    if ($decl -gt $e) { continue }
    if ($lines[$decl] -notmatch '\b\w*Test\w*\s*\(') { continue }
    $cs = $s
    while ($cs -gt 0 -and ($inBlock[$cs - 1] -or $lines[$cs - 1] -match '^\s*//' -or $lines[$cs - 1].Trim() -eq '')) { $cs-- }
    $blocks += [pscustomobject]@{ Name = $members[$k].Name; Start = $s; End = $e; CommentStart = $cs }
}
Write-Host ("  自检方法 " + $blocks.Count + " 个")

$assigned = @(); foreach ($g in $groups.Keys) { $assigned += $groups[$g] }
$missing = $blocks.Name | Where-Object { $assigned -notcontains $_ }
$unknown = $assigned | Where-Object { ($alreadyDone -notcontains $_) -and (($blocks.Name) -notcontains $_) }
$dup     = $assigned | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name }
if ($missing) { Write-Host ("  ✗ 没被分到: " + ($missing -join ', ')); exit 1 }
if ($unknown) { Write-Host ("  ✗ 分组表多余: " + ($unknown -join ', ')); exit 1 }
if ($dup)     { Write-Host ("  ✗ 重复分组: " + ($dup -join ', ')); exit 1 }
Write-Host "  ✓ 断言1：全部有归属、无重复"

# 夹紧：注释归下一个方法，前一个不能含它
$ordered = $blocks | Sort-Object Start
for ($i = 0; $i -lt $ordered.Count - 1; $i++) {
    if ($ordered[$i].End -ge $ordered[$i + 1].CommentStart) { $ordered[$i].End = $ordered[$i + 1].CommentStart - 1 }
    while ($ordered[$i].End -gt $ordered[$i].Start -and $lines[$ordered[$i].End].Trim() -eq '') { $ordered[$i].End-- }
}
# 边界绝不能落在块注释中间
foreach ($b in $ordered) {
    if ($inBlock[$b.CommentStart]) { Write-Host ("  ✗ " + $b.Name + " 的起点落在块注释里"); exit 2 }
    if ($inBlock[$b.End])         { Write-Host ("  ✗ " + $b.Name + " 的终点落在块注释里"); exit 2 }
}

$seen = @{}
foreach ($b in $ordered) {
    for ($i = $b.CommentStart; $i -le $b.End; $i++) {
        if ($seen.ContainsKey($i)) { Write-Host ("  ✗ 第 " + ($i+1) + " 行同时属于 " + $seen[$i] + " 和 " + $b.Name); exit 3 }
        $seen[$i] = $b.Name
    }
}
Write-Host "  ✓ 断言2：区间两两不相交"
# 再从源码里单独验一遍：块注释必须整块在"删"里或整块在"留"里
$badBlock = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($inBlock[$i]) {
        $del = $seen.ContainsKey($i)
        if ($i -gt 0 -and $inBlock[$i-1] -and $seen.ContainsKey($i-1) -ne $del) { $badBlock++ }
        if ($i + 1 -lt $lines.Count -and $inBlock[$i+1] -and $seen.ContainsKey($i+1) -ne $del) { $badBlock++ }
    }
}
if ($badBlock -gt 0) { Write-Host ("  ✗ 断言2b：有 $badBlock 处块注释被切断"); exit 4 }
Write-Host "  ✓ 断言2b：没有任何块注释被切断"

$byName = @{}; foreach ($b in $ordered) { $byName[$b.Name] = $b }
$total = 0; $summary = @()
foreach ($g in $groups.Keys) {
    $n = 0; $c = 0
    foreach ($name in $groups[$g]) { $b = $byName[$name]; $c += ($b.End - $b.CommentStart + 1); $n++ }
    $total += $c; $summary += [pscustomobject]@{ 文件 = "$g.cs"; 方法 = $n; 行 = $c }
}
if ($total -ne $seen.Count) { Write-Host ("  ✗ 断言3：合计 $total ≠ 待删 " + $seen.Count); exit 5 }
Write-Host ("  ✓ 断言3：合计 == 待删 == " + $seen.Count + " 行")

# ---- 动手 ----
foreach ($s in $summary) { Write-Host ("    {0,-20} {1,3} 个方法  {2,6} 行" -f $s.文件, $s.方法, $s.行) }
foreach ($g in $groups.Keys) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("// 本文件由 App.cs 拆出（2026-10-07）：$g 这一组。")
    [void]$sb.AppendLine("// **纯搬家，逻辑一字未改** —— 靠 partial class 共享 App 的私有成员。")
    [void]$sb.AppendLine("// 拆开的目的：产品代码与自检代码互不干扰，人和 AI 读代码时不必互相穿插。")
    [void]$sb.AppendLine()
    @('using System.Diagnostics;','using System.Numerics;','using System.Runtime;',
      'using System.Runtime.InteropServices;','using System.Threading;',
      'using Vortice.Direct2D1;','using Vortice.Mathematics;','using InkEngine;') |
        ForEach-Object { [void]$sb.AppendLine($_) }
    [void]$sb.AppendLine(); [void]$sb.AppendLine('namespace InkTeach;'); [void]$sb.AppendLine()
    [void]$sb.AppendLine('internal sealed partial class App'); [void]$sb.AppendLine('{')
    foreach ($name in $groups[$g]) {
        $b = $byName[$name]
        for ($i = $b.CommentStart; $i -le $b.End; $i++) { [void]$sb.AppendLine($lines[$i]) }
        [void]$sb.AppendLine()
    }
    [void]$sb.AppendLine('}')
    [System.IO.File]::WriteAllText("$outDir\$g.cs", $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
}
$keep = @()
for ($i = 0; $i -lt $lines.Count; $i++) { if (-not $seen.ContainsKey($i)) { $keep += $lines[$i] } }
[System.IO.File]::WriteAllLines($appPath, $keep, (New-Object System.Text.UTF8Encoding $false))
Write-Host ("  App.cs: " + $lines.Count + " → " + $keep.Count + " 行")
