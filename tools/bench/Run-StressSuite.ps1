# 全量压力测试 = 功能测试 + 性能测试，两套依次跑一遍。
#
# **想单独跑的时候用那两个脚本**（这是日常用法）：
#   pwsh -File tools/bench/Run-FuncSuite.ps1     # 功能：红绿，有没有 bug
#   pwsh -File tools/bench/Run-PerfSuite.ps1     # 性能：数字，快不快
# 这个脚本只是"两个都跑"的入口，原始输出各写各的目录。
#
# 历史：这个脚本原来把两者混在一起跑，用 PASS/FAIL/DATA 区分。问题出在
# "DATA（跑完了但没判据）"既是"基准模式"又是"自检少写了判据"，于是
# 一条判据写错、永远红的用例会被当成正常数字埋在中间（实测：折角自检
# 红了好几轮没人发现）。分开之后，功能那边 DATA 会被单独点出来要求补判据。
#
# 用法：
#   pwsh -File tools/bench/Run-StressSuite.ps1                      # 默认全量
#   pwsh -File tools/bench/Run-StressSuite.ps1 -Quick               # 跳过分钟级长跑
#   pwsh -File tools/bench/Run-StressSuite.ps1 -OutDir reports/xxx

param(
    [string]$OutDir = 'reports/stress',
    [switch]$Quick,
    [string]$AppRoot = 'D:\文件集中\code\批注'
)

$ErrorActionPreference = 'Continue'
$func = Join-Path $AppRoot 'tools\bench\Run-FuncSuite.ps1'
$perf = Join-Path $AppRoot 'tools\bench\Run-PerfSuite.ps1'

Write-Host '########## 一、功能测试（红绿）##########'
& $func -OutDir (Join-Path $OutDir 'func') -AppRoot $AppRoot
$funcExit = $LASTEXITCODE

Write-Host ''
Write-Host '########## 二、性能测试（数字）##########'
if ($Quick) { & $perf -OutDir (Join-Path $OutDir 'perf') -AppRoot $AppRoot -Quick }
else        { & $perf -OutDir (Join-Path $OutDir 'perf') -AppRoot $AppRoot }

Write-Host ''
Write-Host ("功能测试退出码：{0}（0 = 全绿）" -f $funcExit)
Write-Host ("原始输出目录：{0}" -f (Join-Path $AppRoot $OutDir))
if ($funcExit -ne 0) { exit $funcExit }
