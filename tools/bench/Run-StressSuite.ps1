# 全量压力测试：把宿主里所有自检/基准模式跑一遍，原始输出落到一个目录里。
#
# 用法：
#   pwsh -File tools/bench/Run-StressSuite.ps1                      # 默认全量
#   pwsh -File tools/bench/Run-StressSuite.ps1 -Quick               # 跳过分钟级长跑
#   pwsh -File tools/bench/Run-StressSuite.ps1 -OutDir reports/xxx
#
# 每个用例独立进程、独立超时；超时即杀进程并记 TIMEOUT，后面的用例继续跑。

param(
    [string]$OutDir = 'reports/stress-0914',
    [switch]$Quick,
    [string]$AppRoot = 'D:\文件集中\code\批注'
)

$ErrorActionPreference = 'Continue'
$exe = Join-Path $AppRoot 'src\InkTeach\bin\Release\net8.0-windows\InkTeach.exe'
if (-not (Test-Path $exe)) { throw "找不到可执行文件：$exe（先 dotnet build -c Release）" }

$OutDir = Join-Path $AppRoot $OutDir
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
# 相对程序目录的写法，给 --report/--memory/--latbench 这类自己写文件的模式用
$OutDirRel = [IO.Path]::GetRelativePath($AppRoot, $OutDir) -replace '\\', '/'

# 每个用例：名称 / 参数 / 超时秒数 / 说明
$cases = @(
    @{ n = 'coordtest';    a = @('--coordtest', '12');        t = 120; d = '坐标不变量：相机偏移下画哪儿显示哪儿' }
    @{ n = 'cameratest';   a = @('--cameratest');             t = 120; d = '相机只改一个数，对象数据不动' }
    @{ n = 'shapetest';    a = @('--shapetest');              t = 120; d = '图形精确命中（轮廓中间是空的）' }
    @{ n = 'savetest';     a = @('--savetest');               t = 150; d = '保存/加载往返逐字段一致 + 坏数据明确失败' }
    @{ n = 'transformtest';a = @('--transformtest');          t = 150; d = '改变换不碰几何、撤销精确回原位' }
    @{ n = 'handletest';   a = @('--handletest');             t = 150; d = '手柄位置/命中/拖出来的变换矩阵' }
    @{ n = 'edittest';     a = @('--edittest');               t = 150; d = '复制/删除/翻转/旋转的撤销语义' }
    @{ n = 'selbench';     a = @('--selbench');               t = 180; d = '选中/变换性能（分对象数档位）' }
    @{ n = 'duptest';      a = @('--duptest', '14');          t = 300; d = '指数复制：内存线性 + 护栏' }
    @{ n = 'erasertest';   a = @('--erasertest');             t = 150; d = '橡皮擦不遗漏、整段只算一步撤销' }
    @{ n = 'inputtest';    a = @('--inputtest');              t = 150; d = '按下→移动→抬起整条路径处处有墨' }
    @{ n = 'widthtest';    a = @('--widthtest');              t = 150; d = '各档粗细实测墨量对理论值' }
    @{ n = 'cornertest';   a = @('--cornertest');             t = 150; d = '直角填充自检（拐角掉不掉色）' }
    @{ n = 'ghosttest';    a = @('--ghosttest');              t = 180; d = '残影检测（相机为 0）' }
    @{ n = 'ghosttest-scroll'; a = @('--ghosttest', 'scroll'); t = 180; d = '残影检测（有滚动偏移）' }
    @{ n = 'trailtest';    a = @('--trailtest');              t = 150; d = '委托墨迹轨迹对照' }
    @{ n = 'cursortest';   a = @('--cursortest');             t = 180; d = '光标/橡皮圆环/荧光笔胶囊渲染' }
    @{ n = 'tiletest';     a = @('--tiletest');               t = 180; d = '内容层分块缓存正确性' }
    @{ n = 'scrollwrite';  a = @('--scrollwrite');            t = 180; d = '滚到哪儿都能写，内容留在画布位置' }
    @{ n = 'wheeltest';    a = @('--wheeltest');              t = 150; d = '滚轮滚动' }
    @{ n = 'appendtest';   a = @('--appendtest');             t = 150; d = '追加路径（同一笔续写）' }
    @{ n = 'memlife';      a = @('--memlife', '10000');       t = 300; d = '一万笔生命周期内存' }
    @{ n = 'realizetest';  a = @('--realizetest', '10000');   t = 240; d = '几何实现缓存对照' }
    @{ n = 'restest';      a = @('--restest', '5000');        t = 240; d = '分辨率对照（离屏层）' }
    @{ n = 'beautifytest'; a = @('--beautifytest');           t = 240; d = '手写美化自检（笔锋量化 15 项）' }
    @{ n = 'selftest';     a = @('--selftest', '8');          t = 180; d = '渲染验证 + 一万笔基准' }
    @{ n = 'memab';        a = @('--memab', '10000', '5');    t = 600; d = '内存 A/B：几何缓存值不值' }
    @{ n = 'passtest';     a = @('--passtest');               t = 180; d = '穿透：跨进程真实点击落到下层窗口' }
    @{ n = 'rotatetest';   a = @('--rotatetest');             t = 180; d = '旋转度数读数 / 吸附 / 标签上屏' }
    @{ n = 'keytest';      a = @('--keytest');                t = 180; d = '键位表 / 冲突检测 / 落盘 / 真机注册' }
    @{ n = 'report';       a = @('--report', (Join-Path $OutDirRel 'report.txt')); t = 600; d = '全量性能报告（写文件）' }
    @{ n = 'memory';       a = @('--memory', (Join-Path $OutDirRel 'memory.txt')); t = 600; d = '内存分阶段归因（写文件）' }
    @{ n = 'aaprobe';      a = @('--aaprobe');                t = 180; d = '平滑到底是谁给的（点数分解）' }
)

if (-not $Quick) {
    $cases += @(
        @{ n = 'longrun';   a = @('--longrun', '60');            t = 240; d = '长跑 60 秒：画→擦→撤销→翻页' }
        @{ n = 'writetest'; a = @('--writetest', '2', '6');      t = 600; d = '连续书写 2 分钟（6 笔/秒）' }
    @{ n = 'latbench';  a = @('--latbench', (Join-Path $OutDirRel 'latency.csv')); t = 300; d = '延时分场景实测（写 CSV）' }
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

    $text = (Get-Content $out -Raw -ErrorAction SilentlyContinue) + "`n" + (Get-Content $err -Raw -ErrorAction SilentlyContinue)

    # 判定规则（这里踩过坑：光看"失败"两个字会误判——自检里本来就有一句
    # "0 项失败"、"乱数据抛异常 PASS"，所以必须看**数字**和独立的 FAIL 标记）。
    #   FAIL  = 出现独立的 FAIL 字样 / FATAL / "失败 N"（N 不为 0）
    #   PASS  = 出现 PASS 或"通过"
    #   DATA  = 没崩、也没判据输出（基准/报告类模式，看数字）
    $verdict = if (-not $exited) { 'TIMEOUT' }
               elseif ($text -match '\bFAIL\b' -or $text -match 'FATAL' -or $text -match '失败\s*[1-9]') { 'FAIL' }
               elseif ($text -match '\bPASS\b' -or $text -match '通过') { 'PASS' }
               else { 'DATA' }

    $results.Add([pscustomobject]@{
        Case = $c.n; Desc = $c.d; Verdict = $verdict
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); ExitCode = $p.ExitCode
    })
    Write-Host ("    {0}  {1:N1}s  exit={2}" -f $verdict, $sw.Elapsed.TotalSeconds, $p.ExitCode)
}

$results | Format-Table -AutoSize
$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir 'summary.json') -Encoding UTF8
Write-Host ("原始输出目录：{0}" -f $OutDir)
