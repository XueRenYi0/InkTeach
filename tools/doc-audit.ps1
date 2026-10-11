# =====================================================================================
#  发版前置检查（2026-10-12 新规矩）：**代码 vs 文档对账**
#
#  用法：
#    .\tools\doc-audit.ps1                    # 对比"最近一个 tag → HEAD"，打印对账清单
#    .\tools\doc-audit.ps1 -Since v9.0.5      # 指定起点
#    .\tools\doc-audit.ps1 -Out dist\doc-audit-v9.0.7.md   # 同时落一份清单文件
#    .\tools\doc-audit.ps1 -Strict            # "代码有改动但文档零更新"时退出码 2（自动化用）
#
# 规矩：**发新版前必须跑一次，逐项核对文档**；由 publish.ps1 自动调用。
# 背景：2026-10 连续出现"MD 文档与实际代码对不上"（含一例旧结论写进约 20 份文档）。
# =====================================================================================
[CmdletBinding()]
param(
    [string]$Since = "",
    [switch]$Strict,
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Invoke-Git([string[]]$gitArgs) { & git -C $root @gitArgs 2>$null }

# ---- 区间：起点默认=最近 tag ---------------------------------------------------------
if (-not $Since) {
    $Since = (Invoke-Git @("tag", "--sort=-creatordate") | Select-Object -First 1)
}
if (-not $Since) { $Since = (Invoke-Git @("rev-list", "--max-parents=0", "HEAD") | Select-Object -First 1) }
$head = (Invoke-Git @("rev-parse", "--short", "HEAD"))
$headDate = (Invoke-Git @("log", "-1", "--format=%cs", "HEAD"))

$commits = @(Invoke-Git @("log", "--oneline", "$Since..HEAD"))
$changed = @(Invoke-Git @("diff", "--name-only", "$Since..HEAD"))
$dirty   = @(Invoke-Git @("status", "--porcelain"))

$code = @($changed | Where-Object { $_ -match '^(src/|installer/|publish\.ps1|tools/)' })
$docs = @($changed | Where-Object { $_ -match '\.md$' })
$other = @($changed | Where-Object { $code -notcontains $_ -and $docs -notcontains $_ })

# ---- 代码模块 → 建议核对的文档（前缀匹配，最具体的排前面）-----------------------------
$map = @(
    @{ P = 'src/InkEngine/Prediction'; D = '调研/优化记录/预测-DL集成方案.md；调研/调研-压感数据与墨迹预测.md' },
    @{ P = 'src/InkEngine/Input';      D = '调研/调研-压感数据与墨迹预测.md；调研/调研-输入栈与RTS可行性.md' },
    @{ P = 'src/InkEngine/Overlay';    D = '调研/调研-系统墨迹两条路线-委托轨迹与桌面宿主.md；调研/实测-委托墨迹轨迹-2026-10-11.md' },
    @{ P = 'src/InkEngine';            D = '调研/延时-实测与优化.md；调研/对标-微软墨迹栈与我们的架构.md；调研/优化记录/发版历史.md' },
    @{ P = 'src/InkTeach';             D = 'README.md（使用说明 / 快捷键 / 数据目录）；调研/开发笔记.md' },
    @{ P = 'src/InkUi';                D = 'README.md（界面 / 设置页）；调研/开发笔记.md' },
    @{ P = 'installer';                D = 'README.md（安装）；installer/InkTeach.iss 头部注释' },
    @{ P = 'publish.ps1';              D = '调研/开发笔记.md（发布细节）；调研/优化记录/发版历史.md' },
    @{ P = 'tools/';                   D = '调研/开发笔记.md；tools/ 下对应工具的 README/注释' }
)

$L = New-Object System.Collections.Generic.List[string]
$L.Add("# 发版对账清单（$Since → $head，$headDate）")
$L.Add("")
$L.Add("> 规矩：**发新版前逐项核对**——代码行为变了的，对应 MD 文档必须先同步（见 调研\优化记录\发版历史.md 顶部），对完再打包。")
$L.Add("")
$L.Add("## 区间提交（$($commits.Count) 条）")
$L.Add("")
if ($commits.Count -eq 0) { $L.Add("（无——起点与 HEAD 相同？）") }
else { foreach ($c in ($commits | Select-Object -First 40)) { $L.Add("- $c") } }
$L.Add("")

$L.Add("## 代码改动（$($code.Count) 个文件）→ 建议逐项核对以下文档")
$L.Add("")
if ($code.Count -eq 0) { $L.Add("（无代码改动）") }
else {
    foreach ($m in $map) {
        $hit = @($code | Where-Object { $_ -like "$($m.P)*" })
        if ($hit.Count -gt 0) {
            $L.Add("### $($m.P)*（$($hit.Count) 个文件）")
            foreach ($f in $hit) { $L.Add("- ``$f``") }
            $L.Add("  → **核对**：$($m.D)")
            $L.Add("")
        }
    }
    $unmapped = @($code | Where-Object { $f = $_; -not ($map | Where-Object { $f -like "$($_.P)*" }) })
    if ($unmapped.Count -gt 0) {
        $L.Add("### 其他")
        foreach ($f in $unmapped) { $L.Add("- ``$f``") }
        $L.Add("")
    }
}

$L.Add("## 文档改动（$($docs.Count) 份）")
$L.Add("")
if ($docs.Count -eq 0) { $L.Add("（**零文档更新** —— 如果代码有改动，这通常意味着漏更，请对照上方逐项检查！）") }
else { foreach ($f in $docs) { $L.Add("- ``$f``") } }
$L.Add("")

if ($other.Count -gt 0) {
    $L.Add("## 其他文件改动（$($other.Count) 个）")
    $L.Add("")
    foreach ($f in ($other | Select-Object -First 30)) { $L.Add("- ``$f``") }
    $L.Add("")
}

if ($dirty.Count -gt 0) {
    $L.Add("## ⚠ 未提交改动（$($dirty.Count) 项）—— 发布前确认要不要一并提交")
    $L.Add("")
    foreach ($d in ($dirty | Select-Object -First 20)) { $L.Add("- ``$d``") }
    $L.Add("")
}

$warnCodeNoDocs = ($code.Count -gt 0 -and $docs.Count -eq 0)
if ($warnCodeNoDocs) {
    $L.Add("## ⚠⚠ 警示：本区间**代码有改动、文档零更新**——请确认不是漏更文档（规矩见 发版历史.md 顶部）")
    $L.Add("")
}

# ---- 输出 ---------------------------------------------------------------------------
$text = ($L -join [Environment]::NewLine)
Write-Host $text

if ($Out) {
    $dir = Split-Path $Out -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [IO.File]::WriteAllText($Out, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ""
    Write-Host "已写入：$Out" -ForegroundColor Green
}

if ($Strict -and $warnCodeNoDocs) { exit 2 }
exit 0
