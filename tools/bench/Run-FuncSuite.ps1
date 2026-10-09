# 功能测试套件：只回答一个问题——"这个功能好不好、有没有 bug"。
#
# 和性能测试（Run-PerfSuite.ps1）**分开**：
#   · 这里每个用例都要给出红绿（PASS / FAIL），没有结论的用例会被点出来；
#   · 那边只看数字（耗时、内存、帧率），不判红绿。
# 分开的理由：混在一起时，"基准跑完了"和"功能是对的"用的是同一行输出，
# 一条永远红的用例（比如判据写错了的）会把真正的失败淹掉。
#
# 用法：
#   pwsh -File tools/bench/Run-FuncSuite.ps1                 # 全部功能用例
#   pwsh -File tools/bench/Run-FuncSuite.ps1 -Only 框选,折角 # 只跑名字含这些字的
#   pwsh -File tools/bench/Run-FuncSuite.ps1 -OutDir reports/func-0914
#
# 每个用例独立进程、独立超时；超时即杀进程并记 TIMEOUT。
# 退出码：有 FAIL 就是 1（方便以后接 CI / 一键回归）。

param(
    [string]$OutDir = 'reports/func',
    [string]$AppRoot = 'D:\文件集中\code\批注',
    [string]$Exe = '',
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Continue'
$exe = if ($Exe) { $Exe } else { Join-Path $AppRoot 'src\InkTeach\bin\Release\net10.0-windows\InkTeach.exe' }
if (-not (Test-Path $exe)) { throw "找不到可执行文件：$exe（先 dotnet build src/InkTeach -c Release）" }

$OutDir = Join-Path $AppRoot $OutDir
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 用例表：功能（给人看的分组）/ 名字 / 参数 / 超时秒 / 说明
$cases = @(
    @{ f = '坐标与相机'; n = 'coordtest';    a = @('--coordtest', '12'); t = 120; d = '相机偏移下：画在哪儿就显示在哪儿' }
    @{ f = '坐标与相机'; n = 'cameratest';   a = @('--cameratest');      t = 120; d = '滚动只改相机一个数，对象数据不动' }
    @{ f = '图形对象';   n = 'shapetest';    a = @('--shapetest');       t = 120; d = '直线/矩形命中：轮廓中间是空的' }
    @{ f = '图形对象';   n = 'imagetest';    a = @('--imagetest');       t = 150; d = '图像对象：上屏/复制翻转/存档/剪贴板' }
    @{ f = '图形对象';   n = 'capturetest';  a = @('--capturetest');     t = 150; d = '截图：拖框 → 左上角 → 剪贴板，不拍进自己' }
    @{ f = '剪贴板';     n = 'clipboardtest'; a = @('--clipboardtest');  t = 150; d = '复制成对象：逐字段回读 / 身份重发 / 同时给外部一张图 / 智能粘贴（会覆盖系统剪贴板）' }
    @{ f = '存档';       n = 'savetest';     a = @('--savetest');        t = 150; d = '保存/加载往返逐字段一致 + 坏数据明确失败' }
    @{ f = '变换与撤销'; n = 'transformtest';a = @('--transformtest');   t = 150; d = '改变换不碰几何、撤销精确回原位' }
    @{ f = '变换与撤销'; n = 'handletest';   a = @('--handletest');      t = 150; d = '选中框手柄位置/命中/拖出来的矩阵' }
    @{ f = '变换与撤销'; n = 'edittest';     a = @('--edittest');        t = 150; d = '复制/删除/翻转/旋转的撤销语义' }
    @{ f = '变换与撤销'; n = 'rotatetest';   a = @('--rotatetest');      t = 180; d = '旋转度数读数 / 三种吸附模式 / 标签上屏' }
    @{ f = '选中与框选'; n = 'seltest';      a = @('--seltest');         t = 120; d = '框到的就该选中（含屏幕外的长线、旋转对象、图像）' }
    @{ f = '选中与框选'; n = 'lassotest';    a = @('--lassotest');       t = 150; d = '套索：80% 判据 / 贴边无限延伸 / 图形按轮廓 / 擦掉的段不算墨 / 加选减选 / 预览上屏' }
    @{ f = '笔迹形状';   n = 'widthtest';    a = @('--widthtest');       t = 150; d = '各档粗细实测墨量对理论值（描边有没有出洞）' }
    @{ f = '笔迹形状';   n = 'captest';      a = @('--captest');         t = 150; d = '两端：单击＝圆点、宽笔＝两头半圆' }
    @{ f = '笔迹形状';   n = 'cornertest';   a = @('--cornertest');      t = 150; d = '折角（45/90/135/170°）不缺墨、不超脏区' }
    @{ f = '笔迹形状';   n = 'selfcross';    a = @('--selfcross');       t = 150; d = '一笔自交处不能变深（荧光笔最敏感）' }
    @{ f = '输入路径';   n = 'inputtest';    a = @('--inputtest');       t = 150; d = '按下→移动→抬起整条路径处处有墨' }
    @{ f = '输入路径';   n = 'appendtest';   a = @('--appendtest');      t = 150; d = '同一笔续写（追加路径）' }
    @{ f = '橡皮擦';     n = 'erasertest';   a = @('--erasertest');      t = 150; d = '稀疏采样快划不遗漏、整段只算一步撤销' }
    @{ f = '橡皮擦';     n = 'pixelerasetest'; a = @('--pixelerasetest'); t = 150; d = '像素橡皮：切段 / 框里无墨 / 图形按轮廓判 / 一步撤销 / 上屏' }
    @{ f = '橡皮擦';     n = 'dynerasertest'; a = @('--dynerasertest'); t = 180; d = '动态橡皮：速度→尺寸曲线 / 后门恒 1 / 快慢扫对比 / 整笔擦不受影响' }
    @{ f = '光标';       n = 'cursortest';   a = @('--cursortest');      t = 180; d = '每种工具 × 设备的落点反馈与系统光标' }
    @{ f = '渲染与缓存'; n = 'tiletest';     a = @('--tiletest');        t = 180; d = '分块缓存：接缝、跨块笔画、滚动复用、内存上界' }
    @{ f = '渲染与缓存'; n = 'selftest';     a = @('--selftest', '8');   t = 180; d = '渲染验证 + 一万笔基准' }
    @{ f = '滚动';       n = 'wheeltest';    a = @('--wheeltest');       t = 150; d = '滚轮滚动' }
    @{ f = '滚动';       n = 'scrollwrite';  a = @('--scrollwrite');     t = 180; d = '滚到哪儿都能写，墨留在画布位置' }
    @{ f = '滚动';       n = 'ghosttest';    a = @('--ghosttest');       t = 180; d = '残影检测（相机为 0）' }
    @{ f = '滚动';       n = 'ghosttest-scroll'; a = @('--ghosttest', 'scroll'); t = 180; d = '残影检测（有滚动偏移）' }
    @{ f = '翻页';       n = 'pagetest';    a = @('--pagetest');        t = 180; d = '整屏翻页（一屏 = 一页）：页高 = 视口高 / 只动相机 / 到顶就停 / 往下无限 / 滚轮仍细粒度' }
    @{ f = '文档导入';   n = 'doctest';     a = @('--doctest');         t = 180; d = '文档页底层（假源）：布局 / 惰性生成（一帧一页）/ 视口窗口回收（驻留不随页数涨）/ 失败当没有' }
    @{ f = 'PPT 模式';   n = 'ppttest';     a = @('--ppttest');         t = 180; d = 'PPT 模式（假页码源）：进放映隔离 / 页内滚动与位置记忆 / 清空只清本页 / 撤销按页 / 退出写盘 / 再进读回 / 按钮走 PPT' }
    @{ f = '导出';       n = 'iotest';      a = @('--iotest');          t = 180; d = '导出选中 → PNG 透明底 / JPEG 白底：签名 / 宽高 / 逐像素颜色与 alpha（预乘还原）/ 空白处全透明 / JPEG 的透明处变**白底而非黑块** / 不许动剪贴板' }
    @{ f = '白板底纹';   n = 'patterntest'; a = @('--patterntest');    t = 180; d = '白板底纹（方格 / 横线）：数屏幕上的线 / 间距生效 / 底纹不算笔迹 / 空闲帧 0 开销 + 整层重铺的代价' }
    @{ f = '实笔轨迹';   n = 'trailtest';    a = @('--trailtest');       t = 150; d = '委托墨迹轨迹（只对真笔生效，鼠标下记跳过）' }
    @{ f = '穿透模式';   n = 'passtest';     a = @('--passtest');        t = 180; d = '穿透：跨进程真实点击落到下层窗口' }
    @{ f = '穿透模式';   n = 'uitest';       a = @('--uitest');          t = 240; d = '界面输入通路：面板内/外 × 穿透开/关 × 界面吃不吃，谁收到（含"穿透下面板可点"）' }
    @{ f = '产品界面';   n = 'paneltest';    a = @('--paneltest');       t = 240; d = '产品界面（src/InkUi）：球→按钮带、点按钮改引擎状态、拖动贴边、空闲 0 帧' }
    @{ f = '产品界面';   n = 'shapebandtest'; a = @('--shapebandtest');  t = 240; d = '图形那格的界面入口：上带七段逐段点 / 三个新热键合成键盘真按 / 主条图标跟着种类变' }
    @{ f = '键位设置';   n = 'keytest';      a = @('--keytest');         t = 180; d = '键位表 / 冲突检测 / 落盘读回 / 真机注册' }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($c in $cases) {
    if ($Only.Count -gt 0) {
        $hit = $false
        foreach ($k in $Only) { if ($c.f -like "*$k*" -or $c.n -like "*$k*") { $hit = $true } }
        if (-not $hit) { continue }
    }

    $out = Join-Path $OutDir ("out-" + $c.n + ".txt")
    $err = Join-Path $OutDir ("err-" + $c.n + ".txt")
    Write-Host ("=== {0} === {1}" -f $c.n, $c.d)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    # ⚠ **passtest 单独用 Normal 启动**（2026-10-09 收编实测）：它是全套里唯一"验证窗真的把
    #   点击让给下层"的用例（Layered+Transparent 那一档）；用 `-WindowStyle Hidden` 或
    #   Minimized 启动时，系统的窗口显示状态会跟着进程走，这一档**必红**——同一份产物
    #   Normal 下必绿、单独复跑与真机也都绿（不是产品问题，是启动方式把测试前提改了）。
    #   其余用例保持 Hidden（屏幕干净）。
    $winStyle = if ($c.n -eq 'passtest') { 'Normal' } else { 'Hidden' }
    $p = Start-Process -FilePath $exe -ArgumentList $c.a -PassThru -WindowStyle $winStyle -WorkingDirectory $AppRoot `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    $exited = $p.WaitForExit($c.t * 1000)
    if (-not $exited) {
        try { $p.Kill() } catch { }
        try { $p.WaitForExit(5000) } catch { }
    }
    $sw.Stop()

    $text = (Get-Content $out -Raw -ErrorAction SilentlyContinue) + "`n" + (Get-Content $err -Raw -ErrorAction SilentlyContinue)

    # 判定规则：光看"失败"两个字会误判（自检里本来就写着"0 项失败"），
    # 所以必须看**数字**和独立的标记。
    #   SKIP  = 环境不适用（例如合成输入被别的程序占着）——不算失败，但要看得见
    #   FAIL  = 独立的 FAIL 字样 / FATAL / "失败 N" / **"N 项失败"（收尾统计）**
    #           ⚠ 2026-10-09 补：以前只认"失败 N"（数字在后），**收尾行"140 项通过, 4 项失败"
    #           一直没被算进 FAIL**——shapebandtest 的 4 个既有红因此被全套当绿放行过。
    #   PASS  = 有 PASS 或"通过"
    #   DATA  = 跑完了但没判据（功能用例里出现就是"这条没在验东西"，要修）
    # ⚠ **行首不能起 `-or`**（2026-10-09 当场踩过）：PowerShell 的续行只认"运算符留在
    # 上一行的行尾"；写成行首 -or 会让**整个脚本解析失败**（上一条"让既有红显性上报"
    # 的补丁因此没能生效，自检直接挂）。所以判定式拆成"每条以 -or 结尾"的样子。
    $failHit = $text -match '\bFAIL\b' -or
               $text -match 'FATAL' -or
               $text -match '失败\s*[1-9]' -or
               $text -match '[1-9]\d*\s*项失败'
    $verdict = if (-not $exited) { 'TIMEOUT' }
               elseif ($text -match 'SKIP:') { 'SKIP' }
               elseif ($failHit) { 'FAIL' }
               elseif ($text -match '\bPASS\b' -or $text -match '通过') { 'PASS' }
               else { 'DATA' }

    # 关键数字：把结论行摘出来，汇报时不用翻原始文件
    $key = ($text -split "`n" | Where-Object { $_ -match 'PASS:|FAIL:|合计|项：' } | Select-Object -Last 1)
    if (-not $key) { $key = ($text -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1) }
    $key = ($key -replace '\s+', ' ').Trim()

    $results.Add([pscustomobject]@{
        Feature = $c.f; Case = $c.n; Verdict = $verdict
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Key = $key
    })
    Write-Host ("    {0}  {1:N1}s  {2}" -f $verdict, $sw.Elapsed.TotalSeconds, $key)
}

Write-Host ''
$results | Format-Table -AutoSize Feature, Case, Verdict, Seconds
$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir 'summary.json') -Encoding UTF8

$pass = ($results | Where-Object Verdict -eq 'PASS').Count
$fail = ($results | Where-Object { $_.Verdict -eq 'FAIL' -or $_.Verdict -eq 'TIMEOUT' }).Count
$skip = ($results | Where-Object Verdict -eq 'SKIP').Count
$noJudge = ($results | Where-Object Verdict -eq 'DATA').Count

Write-Host ("功能测试：{0} 项，通过 {1}，失败 {2}，跳过 {3}，没有判据 {4}" -f $results.Count, $pass, $fail, $skip, $noJudge)
if ($noJudge -gt 0) { Write-Host "  （'没有判据'的用例 = 跑完了但什么都没验，应当补上红绿判据）" }
Write-Host ("原始输出：{0}" -f $OutDir)

if ($fail -gt 0) { exit 1 }
