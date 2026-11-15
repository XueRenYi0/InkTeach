<#
从源码目录生成 docs/tree-map.md：目录树 + 每个文件的行数 + 文件头职责。

用法：
    powershell -ExecutionPolicy Bypass -File tools/gen-tree-map.ps1

为什么要有它：
    文件树本身就是一层免费索引：目录 = 功能域，文件名 = 功能块。
    这个脚本把树和行数打出来，AI 不用先 glob 一遍；文件头（如果写了）
    会跟在文件名后面，相当于“每节的摘要”。

和 module-map 的分工：
    tree-map   = 结构（有哪些文件、多大）——自动生成，零维护
    module-map = 语义（“收藏拖拽排序”该看哪 1–2 个文件）——人工，只留跨文件流程

注意：
    只认 `// [职责] ...` 或文件头第一段 `/// <summary>` 的第一行；
    没写文件头的文件只显示行数，不编造描述。
#>

param([string]$Out = "")

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src'
$outFile = if ($Out) { $Out } else { Join-Path $repoRoot 'docs\tree-map.md' }

$files = Get-ChildItem -Path $srcRoot -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    Sort-Object FullName

function Get-Headline([string]$path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $limit = [Math]::Min(40, $lines.Length)
    for ($i = 0; $i -lt $limit; $i++) {
        $t = $lines[$i].Trim()
        if ($t -match '^//\s*\[职责\]\s*(.+)$') { return $Matches[1].Trim() }
    }
    # 不猜 `<summary>`：文件里第一个 summary 往往是某个字段/枚举，不是文件职责。
    return ''
}

$sb = [System.Collections.Generic.List[string]]::new()
$sb.Add('# 目录树（自动生成，勿手改）')
$sb.Add('')
$sb.Add('> 生成：' + (Get-Date -Format 'yyyy-MM-dd') + ' ／ 脚本：`tools/gen-tree-map.ps1`')
$sb.Add('> 用途：目录 = 功能域、文件名 = 功能块；先看树定位目录，再看行数决定读哪一段。')
$sb.Add('> 语义导航（“某个需求该看哪 1–2 个文件”）看 `docs/module-map.md`；段落行号看 `docs/section-map.md`。')
$sb.Add('')

$currentDir = ''
foreach ($f in $files) {
    $rel = $f.FullName.Substring($srcRoot.Length).TrimStart('\').Replace('\', '/')
    $dir = if ($rel.Contains('/')) { $rel.Substring(0, $rel.LastIndexOf('/')) } else { '' }
    if ($dir -ne $currentDir) {
        $currentDir = $dir
        $depth = if ($dir -eq '') { 0 } else { ($dir -split '/').Count }
        $indent = '  ' * $depth
        $sb.Add('')
        $sb.Add($indent + '**' + $dir + '/**')
    }
    $name = [System.IO.Path]::GetFileName($rel)
    $lines = [System.IO.File]::ReadAllLines($f.FullName).Length
    $head = Get-Headline $f.FullName
    $depth = if ($dir -eq '') { 0 } else { ($dir -split '/').Count }
    $indent = '  ' * ($depth + 1)
    if ($head.Length -gt 70) { $head = $head.Substring(0, 70) + '…' }
    $line = $indent + '- `' + $name + '` ' + $lines.ToString('N0') + ' 行'
    if ($head.Length -gt 0) { $line += ' — ' + $head }
    $sb.Add($line)
}

[System.IO.File]::WriteAllLines($outFile, $sb.ToArray(), (New-Object System.Text.UTF8Encoding($false)))
"wrote $outFile ($($files.Count) files)"
