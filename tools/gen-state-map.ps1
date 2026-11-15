<#
从 partial 族的私有字段生成 docs/state-map.md（ADR-005 D4）。

用法：
    powershell -ExecutionPolicy Bypass -File tools/gen-state-map.ps1

回答的问题：
    "改这个字段之前，还必须读哪些文件？"
    只列被 >=2 个同族文件引用的字段；按引用文件数排序，每族取前 20。

口径：
    - 字段 = 4 格缩进、`_camelCase`、带 `=` 或 `;` 的声明；
    - 引用 = 该字段名在另一文件的任意位置出现（正则 \b）；
    - 写入点 = 字段名后跟 = / += / -= / ++ / -- 的行数，只作灰线索。
    它降低定位成本，不降低验证成本；拆不拆类看 ADR-005 D4 的触发条件。

红线：只读源码、只写 docs/state-map.md，不改任何 .cs。
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src'
$outFile = Join-Path $repoRoot 'docs\state-map.md'
$families = @('Engine', 'App', 'Overlay', 'FullUi', 'Model')
$topPerFamily = 20
$minRefs = 2

$declRegex = [regex]::new(
    '^[ \t]{4,8}(?:(?:private|internal|protected|public|static|readonly|volatile)\s+)*(?:[\w<>,\[\]\?\.]+)\s+(_\w+)\s*(?:=|;)',
    [System.Text.RegularExpressions.RegexOptions]::Multiline)

function Get-FamilyFiles([string]$family) {
    Get-ChildItem -Path $srcRoot -Recurse -File -Filter "$family*.cs" |
        Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
        Sort-Object FullName
}

$out = New-Object System.Text.StringBuilder
function Add-Line([string]$text) { [void]$out.AppendLine($text) }

Add-Line '# 状态地图（自动生成，勿手改）'
Add-Line ''
Add-Line "> 生成：$(Get-Date -Format 'yyyy-MM-dd HH:mm') ／ 脚本：``tools/gen-state-map.ps1`` ／ ADR-005 D4"
Add-Line '> 用法：改一个私有字段前，先在这里查它被哪些同族文件读写；只列被 >=2 个文件引用的字段。'
Add-Line '> 它降低定位成本，不降低验证成本；拆不拆类看 ADR-005 D4 的触发条件，不按行数。'
Add-Line ''

$totalRows = 0
foreach ($family in $families) {
    $files = @(Get-FamilyFiles $family)
    if ($files.Count -eq 0) { continue }

    $text = @{}
    foreach ($f in $files) {
        $body = [System.IO.File]::ReadAllText($f.FullName)
        # 近似：先剥注释，避免注释里的字段名被算成引用。
        $body = $body -replace '(?m)//.*$', '' -replace '(?s)/\*.*?\*/', ''
        $text[$f.FullName] = $body
    }

    $declared = @{}
    foreach ($f in $files) {
        foreach ($m in $declRegex.Matches($text[$f.FullName])) {
            $name = $m.Groups[1].Value
            if (-not $declared.ContainsKey($name)) { $declared[$name] = @() }
            if ($declared[$name] -notcontains $f.Name) { $declared[$name] += $f.Name }
        }
    }

    $rows = New-Object System.Collections.ArrayList
    foreach ($name in $declared.Keys) {
        $pattern = '\b' + [regex]::Escape($name) + '\b'
        $writePattern = '\b' + [regex]::Escape($name) + '\s*(=|\+=|-=|\+\+|[-]{2})'
        $refFiles = New-Object System.Collections.ArrayList
        $writeSites = 0
        foreach ($f in $files) {
            $body = $text[$f.FullName]
            if ($body -match $pattern) {
                [void]$refFiles.Add($f.Name)
                $writeSites += ([regex]::Matches($body, $writePattern)).Count
            }
        }
        if ($refFiles.Count -ge $minRefs) {
            [void]$rows.Add([pscustomobject]@{
                Name = $name
                Refs = $refFiles.Count
                Files = ($refFiles | Select-Object -First 6) -join ', '
                More = [Math]::Max(0, $refFiles.Count - 6)
                Writes = $writeSites
            })
        }
    }

    $ordered = @($rows | Sort-Object -Property @{Expression='Refs';Descending=$true}, @{Expression='Writes';Descending=$true} |
        Select-Object -First $topPerFamily)

    Add-Line "## $family（$($files.Count) 文件 / $($declared.Count) 字段 / $($rows.Count) 个跨文件字段）"
    Add-Line ''
    Add-Line '| 字段 | 引用文件 | 主要文件 | 写入点 |'
    Add-Line '| --- | --- | --- | --- |'
    foreach ($r in $ordered) {
        $filesText = $r.Files
        if ($r.More -gt 0) { $filesText += " …+$($r.More)" }
        Add-Line "| ``$($r.Name)`` | $($r.Refs) | $filesText | $($r.Writes) |"
    }
    Add-Line ''
    $totalRows += $ordered.Count
}

Add-Line "共 $totalRows 行；完整字段清单用 ``tools/gen-state-map.ps1`` 重跑（本文件不手工维护）。"

[System.IO.File]::WriteAllText($outFile, $out.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "state-map: $totalRows rows -> $outFile"
