# SCUT-onHCCTestDB（.pot）→ 简易文本语料（预测质量"大数据"管线的第一环）
#
# 格式（2026-10-08 逆向 + 验证：20000/20000 个样本与 label 文件逐条对上、零断点）：
#   record := [size u16][code u16][payload（size-4 字节）]
#     size    = 本记录总字节数（含这 4 字节头）
#     code    = 汉字的 GBK 码，**小端存放**（还原：hi=(v>>8), lo=v&0xFF，再按 GBK 解码）
#     payload = 点对序列 (x u16, y u16)*，其中：
#                 (0xFFFF, 0x0000) = 抬笔（笔段分隔）
#                 (0xFFFF, 0xFFFF) = 样本结束（payload 末尾）
#   ⚠ 没有时间戳（纯几何 + 笔段）——时间相关的评测走 UCI 数据集或真机黑匣子。
#
# 用法（pwsh 7）：
#   .\Export-SCUT-onHCC.ps1 -Pot "<...>\pot\onHCCTestDB-SimpleChar 1.pot" `
#                            -Label "<...>\label\onHCCTestDB-SimpleChar 1.txt" `
#                            -Out "输出.txt"
#
# 输出（UTF-8，无 BOM）：
#   #SCUT-onHCC v1
#   #sample <idx> <汉字> strokes=<n> points=<n>
#   x,y x,y ...     ← 一行一个笔段
#   （空行）
param(
    [Parameter(Mandatory = $true)][string]$Pot,
    [Parameter(Mandatory = $true)][string]$Label,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
$gbk = [Text.Encoding]::GetEncoding(936)
$bytes = [System.IO.File]::ReadAllBytes($Pot)
$labels = ([Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($Label)) -split "`n") |
          Where-Object { $_.Trim().Length -gt 0 }

$sw = New-Object System.IO.StreamWriter($Out, $false, (New-Object System.Text.UTF8Encoding($false)))
$sw.WriteLine('#SCUT-onHCC v1')
$off = 0; $idx = 0; $mismatch = 0
while ($off + 4 -le $bytes.Length) {
    $size = [BitConverter]::ToUInt16($bytes, $off)
    if ($size -lt 8 -or $off + $size -gt $bytes.Length) { break }
    $code = [BitConverter]::ToUInt16($bytes, $off + 2)
    if ($code -eq 0) { break }
    $ch = $gbk.GetString([byte[]]@([byte](($code -shr 8) -band 0xFF), [byte]($code -band 0xFF)))
    if ($idx -lt $labels.Count -and $ch -ne $labels[$idx]) { $mismatch++ }

    $p = $off + 4; $end = $off + $size
    $strokeLines = New-Object System.Collections.Generic.List[string]
    $pts = New-Object System.Collections.Generic.List[string]
    $strokes = 0; $npts = 0
    while ($p + 4 -le $end) {
        $x = [BitConverter]::ToUInt16($bytes, $p)
        $y = [BitConverter]::ToUInt16($bytes, $p + 2)
        $p += 4
        if ($x -eq 65535) {                          # 抬笔 / 样本结束
            if ($pts.Count -gt 0) { $strokeLines.Add(($pts -join ' ')); $pts.Clear(); $strokes++ }
            continue
        }
        $pts.Add("$x,$y"); $npts++
    }
    if ($pts.Count -gt 0) { $strokeLines.Add(($pts -join ' ')); $strokes++ }

    $sw.WriteLine("#sample $idx $ch strokes=$strokes points=$npts")
    foreach ($l in $strokeLines) { $sw.WriteLine($l) }
    $sw.WriteLine('')

    $off = $end; $idx++
}
$sw.Close()
"导出 $idx 个样本（标签不匹配 $mismatch 个），输出：$Out"
