<#
  内存曲线采样器：盯住一个进程，按固定间隔记录内存，输出 CSV + 摘要。

  用途：做"同一个动作、不同软件"的横向对比。
  三个程序各跑一次同样的脚本、同样的动作，把三份 CSV 叠在一张图上比。

  用法：
    pwsh tools/bench/Memory-Sweep.ps1 -ProcessName InkTeach -Seconds 60 `
         -Out reports/sweep-inkprobe.csv

  为什么这么写（都是踩过的坑）：

  1. **采私有字节与显存，不采工作集。** 工作集会被系统随时回收，它反映的是
     "系统还有多少压力"，不是"这个程序占了多少"。同一份程序两次跑工作集能差
     7MB 以上，而我们要判断的效应常常就是这个量级。

  2. **显存必须一起看。** 核显的显存是从系统内存里分的——在 4GB 教室机上，
     它和进程内存抢的是同一块。实测一万笔时私有 247MB + 显存 191MB，
     只看进程内存会漏掉将近一半。

  3. **按间隔采样、输出整条曲线。** 单点数字没法比：不同程序"什么时候到达
     稳态"不一样，取哪一刻都能得出不同结论。曲线能看出"涨到哪儿停""什么时候
     开始抖"。

  4. **采样只读，不动被测程序。** 不注入、不挂钩、不需要它们配合，
     所以对闭源程序同样有效。
#>

param(
    [Parameter(Mandatory = $true)][string]$ProcessName,
    [int]$Seconds = 60,
    [int]$IntervalMs = 500,
    [string]$Out = "",
    [string]$Label = ""
)

$ErrorActionPreference = 'Stop'
if (-not $Label) { $Label = $ProcessName }

function Get-GpuMb([int]$procId) {
    # 每个进程的专用显存。计数器名里的实例形如 "pid_1234_luid_..."。
    try {
        $s = (Get-Counter "\GPU Process Memory(*)\Dedicated Usage" -ErrorAction Stop).CounterSamples |
             Where-Object { $_.InstanceName -like "pid_${procId}_*" }
        if ($s) { return [math]::Round((($s | Measure-Object CookedValue -Sum).Sum) / 1MB, 1) }
    } catch { }
    return 0
}

function Get-SharedGpuMb([int]$procId) {
    try {
        $s = (Get-Counter "\GPU Process Memory(*)\Shared Usage" -ErrorAction Stop).CounterSamples |
             Where-Object { $_.InstanceName -like "pid_${procId}_*" }
        if ($s) { return [math]::Round((($s | Measure-Object CookedValue -Sum).Sum) / 1MB, 1) }
    } catch { }
    return 0
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    throw "找不到进程 $ProcessName。请先把它启动起来，再运行本脚本。"
}

Write-Host "采样对象：$Label (pid=$($proc.Id))　时长 ${Seconds}s　间隔 ${IntervalMs}ms"
Write-Host ""
Write-Host "  经过(s) |  私有MB | 工作集MB | 专用显存MB | 共享显存MB | CPU%"
Write-Host "  --------|---------|----------|------------|------------|------"

$rows = @()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$lastCpu = $proc.TotalProcessorTime
$lastWall = 0.0

while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    try { $proc.Refresh() } catch { break }

    $wall = $clock.Elapsed.TotalSeconds
    $cpuDelta = ($proc.TotalProcessorTime - $lastCpu).TotalMilliseconds
    $wallDelta = ($wall - $lastWall) * 1000.0
    $cpuPct = if ($wallDelta > 0) {
        [math]::Round($cpuDelta / $wallDelta / [Environment]::ProcessorCount * 100, 1)
    } else { 0 }
    $lastCpu = $proc.TotalProcessorTime
    $lastWall = $wall

    $priv = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
    $ws = [math]::Round($proc.WorkingSet64 / 1MB, 1)
    $gpuD = Get-GpuMb $proc.Id
    $gpuS = Get-SharedGpuMb $proc.Id

    $row = [pscustomobject]@{
        标签 = $Label; 秒 = [math]::Round($wall, 1); 私有MB = $priv;
        工作集MB = $ws; 专用显存MB = $gpuD; 共享显存MB = $gpuS; CPU百分比 = $cpuPct
    }
    $rows += $row
    Write-Host ("  {0,8:F1} | {1,7:F1} | {2,8:F1} | {3,10:F1} | {4,10:F1} | {5,5:F1}" -f `
                $wall, $priv, $ws, $gpuD, $gpuS, $cpuPct)

    Start-Sleep -Milliseconds $IntervalMs
}

if ($Out) {
    $rows | Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8
    Write-Host ""
    Write-Host "已写入 $Out（$($rows.Count) 个采样点）"
}

$peakPriv = ($rows | Measure-Object 私有MB -Maximum).Maximum
$peakGpu = ($rows | Measure-Object 专用显存MB -Maximum).Maximum
$peakShared = ($rows | Measure-Object 共享显存MB -Maximum).Maximum
$peakCpu = ($rows | Measure-Object CPU百分比 -Maximum).Maximum
$last = $rows[-1]

Write-Host ""
Write-Host "=== 摘要：$Label ==="
Write-Host ("  峰值私有字节  {0,7:F1} MB" -f $peakPriv)
Write-Host ("  峰值专用显存  {0,7:F1} MB" -f $peakGpu)
Write-Host ("  峰值共享显存  {0,7:F1} MB（核显上这部分也是从系统内存分的）" -f $peakShared)
Write-Host ("  峰值 CPU      {0,7:F1} %（单核百分比 ÷ 核数）" -f $peakCpu)
Write-Host ("  结束时私有    {0,7:F1} MB" -f $last.私有MB)
Write-Host ""
Write-Host "  判读要点："
Write-Host "   · 曲线**平不平**比峰值更重要：平说明稳态，一直在涨说明有积累（或泄漏）。"
Write-Host "   · 三个程序要在**同一个动作、同一规模**下比，动作不同比出来的没意义。"
Write-Host "   · 单点数字不可比（什么时候到稳态各不一样），要比就比整条曲线。"
