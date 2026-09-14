# 性能测试套件：只回答一个问题——"多快、多省、稳不稳"。
#
# 这里**不判红绿**：基准数字本身没有对错，要拿两次跑的结果比，或者看趋势。
# 功能对不对那是 Run-FuncSuite.ps1 的事，两套分开跑，别把"跑完了"当成"功能好"。
#
# 用法：
#   pwsh -File tools/bench/Run-PerfSuite.ps1                    # 默认档（分钟级）
#   pwsh -File tools/bench/Run-PerfSuite.ps1 -Quick             # 跳过长跑档
#   pwsh -File tools/bench/Run-PerfSuite.ps1 -OutDir reports/perf-0914

param(
    [string]$OutDir = 'reports/perf',
    [string]$AppRoot = 'D:\文件集中\code\批注',
    [switch]$Quick
)

$ErrorActionPreference = 'Continue'
$exe = Join-Path $AppRoot 'src\InkTeach\bin\Release\net8.0-windows\InkTeach.exe'
if (-not (Test-Path $exe)) { throw "找不到可执行文件：$exe（先 dotnet build src/InkTeach -c Release）" }

$OutDir = Join-Path $AppRoot $OutDir
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDirRel = [IO.Path]::GetRelativePath($AppRoot, $OutDir) -replace '\\', '/'

# 名字 / 参数 / 超时秒 / 看什么
$cases = @(
    @{ n = 'selbench';    a = @('--selbench');                     t = 300; d = '选中与变换：修补耗时、每帧记录耗时' }
    @{ n = 'duptest';     a = @('--duptest', '14');                t = 300; d = '指数复制：内存是否线性、有护栏' }
    @{ n = 'memlife';     a = @('--memlife', '10000');             t = 300; d = '一万笔生命周期内存' }
    @{ n = 'restest';     a = @('--restest', '5000');              t = 240; d = '分辨率对照（离屏内容层）' }
    @{ n = 'report';      a = @('--report', (Join-Path $OutDirRel 'report.txt'));   t = 900; d = '全量性能报告（写文件）' }
    @{ n = 'memory';      a = @('--memory', (Join-Path $OutDirRel 'memory.txt'));   t = 600; d = '内存分阶段归因（写文件）' }
)

if (-not $Quick) {
    $cases += @(
        @{ n = 'latbench';  a = @('--latbench', (Join-Path $OutDirRel 'latency.csv')); t = 300; d = '延时分场景实测（写 CSV）' }
        @{ n = 'longrun';   a = @('--longrun', '60');              t = 300; d = '长跑 60 秒：画→擦→撤销→翻页' }
        @{ n = 'writetest'; a = @('--writetest', '2', '6');        t = 600; d = '连续书写 2 分钟（6 笔/秒）' }
    )
}

$results = New-Object System.Collections.Generic.List[object]

foreach ($c in $cases) {
    $out = Join-Path $OutDir ("out-" + $c.n + ".txt")
    $err = Join-Path $OutDir ("err-" + $c.n + ".txt")
    Write-Host ("=== {0} === {1}" -f $c.n, $c.d)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -ArgumentList $c.a -PassThru -WindowStyle Hidden -WorkingDirectory $AppRoot `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    $exited = $p.WaitForExit($c.t * 1000)
    if (-not $exited) {
        try { $p.Kill() } catch { }
        try { $p.WaitForExit(5000) } catch { }
    }
    $sw.Stop()

    $text = (Get-Content $out -Raw -ErrorAction SilentlyContinue)
    # 摘几条带数字的行当摘要（不判红绿，只让人一眼看到量级）
    $nums = ($text -split "`n" | Where-Object { $_ -match 'ms|MB|fps' } |
             Where-Object { $_ -notmatch '^\s*$' } | Select-Object -First 3)
    $key = ($nums -join ' | ') -replace '\s+', ' '
    if ($key.Length -gt 200) { $key = $key.Substring(0, 200) + '…' }

    $verdict = if (-not $exited) { 'TIMEOUT' } else { 'DONE' }
    $results.Add([pscustomobject]@{
        Case = $c.n; Verdict = $verdict; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Key = $key
    })
    Write-Host ("    {0}  {1:N1}s" -f $verdict, $sw.Elapsed.TotalSeconds)
    if ($key) { Write-Host ("      {0}" -f $key) }
}

Write-Host ''
$results | Format-Table -AutoSize Case, Verdict, Seconds
$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir 'summary.json') -Encoding UTF8
Write-Host ("性能测试：{0} 项跑完（这里不判红绿，数字请和上一次对比）" -f $results.Count)
Write-Host ("原始输出：{0}" -f $OutDir)
