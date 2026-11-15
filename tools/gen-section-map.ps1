<#
从大文件的 banner 注释生成 docs/section-map.md（给 AI 按需读取用）。

用法：
    powershell -ExecutionPolicy Bypass -File tools/gen-section-map.ps1

为什么要有它：
    Engine.cs / FullUi.cs 这类文件一次读不完（Read 上限 2000 行），
    但它们的 banner 注释已经把功能分段了。这个脚本把 banner 抽成
    “标题 → 行号”表，AI 先看表、再只读那一段，不用整文件通读。

为什么是自动生成：
    行号一定会漂移。手写行号表第二次拆文件就废；脚本重跑一次就同步。
    只扫 >= 600 行的 src 文件——小文件直接读，不需要地图。

注意：
    本脚本只认两种 banner（仓库现有风格）：
      // ---- 标题 ----------------------------------------
      // ====================================================
      //  标题
      // ====================================================
    标题里可能有中文；输出统一 UTF-8（无 BOM）。
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src'
$outFile = Join-Path $repoRoot 'docs\section-map.md'
$minLines = 600

$files = Get-ChildItem -Path $srcRoot -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    Sort-Object FullName

$sb = New-Object System.Text.StringBuilder
function Add-Line([string]$text) { [void]$sb.AppendLine($text) }

$today = Get-Date -Format 'yyyy-MM-dd'
Add-Line '# 段落地图（自动生成，勿手改）'
Add-Line ''
Add-Line "> 生成：$today ／ 脚本：``tools/gen-section-map.ps1`` ／ 只列 >= $minLines 行的文件。"
Add-Line '> 用法：``Read`` 大文件前先在这里找段落标题，再只读那一段（行号漂移后重跑脚本即可）。'
Add-Line ''

$total = 0
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    $n = $lines.Length
    if ($n -lt $minLines) { continue }
    $rel = $f.FullName.Substring($repoRoot.Length).TrimStart('\').Replace('\', '/')
    $sections = New-Object System.Collections.ArrayList
    $types = New-Object System.Collections.ArrayList

    for ($i = 0; $i -lt $n; $i++) {
        $l = $lines[$i]

        # 顶层类型 / 枚举（只认行首或一个缩进，避免方法内局部类）
        if ($l -match '^(public|internal|private|sealed|static|abstract|partial|\s)*(class|struct|enum|interface|record)\s+([A-Za-z_][A-Za-z0-9_]*)') {
            if ($l -notmatch '^\s{8,}') {
                [void]$types.Add([pscustomobject]@{ Line = $i + 1; Text = $l.Trim() })
            }
        }

        # banner 行
        if ($l -match '^\s*//\s*([=\-#]{4,})\s*$') {
            $title = ''
            for ($j = $i + 1; $j -lt [Math]::Min($i + 5, $n); $j++) {
                $t = $lines[$j].Trim()
                if ($t -eq '') { continue }
                if ($t -match '^//(?!/)\s*(.*?)\s*[=\-#]*\s*$') {
                    $cand = $Matches[1].Trim()
                    if ($cand -match '^/?\s*<summary>') { continue }
                    if ($cand.StartsWith('/')) { continue }
                    if ($cand.Length -lt 2) { continue }
                    $title = $cand
                }
                break
            }
            if ($title -ne '') {
                [void]$sections.Add([pscustomobject]@{ Line = $i + 1; Text = $title })
            }
        }
        elseif ($l -match '^\s*//\s*[=\-#]{2,}\s+(.+?)\s*[=\-#]*\s*$') {
            $title = $Matches[1].Trim()
            if ($title.Length -gt 2) {
                [void]$sections.Add([pscustomobject]@{ Line = $i + 1; Text = $title })
            }
        }
    }

    if ($sections.Count -eq 0 -and $types.Count -eq 0) { continue }
    $total++
    Add-Line "## $rel（$("{0:N0}" -f $n) 行）"
    Add-Line ''
    if ($types.Count -gt 0) {
        Add-Line '| 行 | 顶层类型 |'
        Add-Line '| --- | --- |'
        foreach ($t in $types) {
            $txt = $t.Text -replace '\|', '\|'
            Add-Line "| $($t.Line) | ``$txt`` |"
        }
        Add-Line ''
    }
    if ($sections.Count -gt 0) {
        Add-Line '| 行 | 段落 |'
        Add-Line '| --- | --- |'
        foreach ($s in $sections) {
            $txt = $s.Text -replace '\|', '\|'
            Add-Line "| $($s.Line) | $txt |"
        }
        Add-Line ''
    }
}

Add-Line '---'
Add-Line ''
Add-Line "共 $total 个文件。重跑：``powershell -ExecutionPolicy Bypass -File tools/gen-section-map.ps1``"

[System.IO.File]::WriteAllText($outFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
"wrote $outFile ($total files)"
