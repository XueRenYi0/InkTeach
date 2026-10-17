# =====================================================================================
#  Log case ledger (2026-10-17)
#
#  The log repo only stores zips; this script adds the "triage / mark / track" layer:
#    - every uploaded log is either untriaged or attached to a case;
#    - a case has status: new / investigating / fixed / verified / closed / reopened;
#    - a log that has been analyzed is marked here, so the same log is not re-analyzed;
#    - a new log after a fix can be attached to the old case (reopened) or opened
#      as a new case.
#
#  Ledger lives in the private log repo: cases/ledger.json
#  Local cache: reports/log-ledger.json
#
#  Usage:
#    .\tools\log-case.ps1 -Action triage
#    .\tools\log-case.ps1 -Action open -Title "Win10 1080p 45fps" -Logs log-xxx.zip -Symptom "idle 45fps / 13ms"
#    .\tools\log-case.ps1 -Action fixed -Case C-20261017-01 -Version 9.1.9 -Commit abc1234
#    .\tools\log-case.ps1 -Action verified -Case C-20261017-01 -Log log-yyy.zip -Note "60fps, 3ms"
#    .\tools\log-case.ps1 -Action list
#
#  -NoPush: modify only the local cache (dry run).
# =====================================================================================
param(
    [ValidateSet('list','triage','meta','open','attach','investigating','fixed','verified','reopen','close','ignore','prune','note','pull','push')]
    [string]$Action = 'list',
    [string]$Title = '',
    [string]$Case = '',
    [string[]]$Logs = @(),
    [string]$Symptom = '',
    [string]$Device = '',
    [string]$Version = '',
    [string]$Commit = '',
    [string]$Note = '',
    [switch]$NoPush
)

# PowerShell -File 传 "a,b,c" 时整个会进 [0]；这里拆开，避免只标记第一条。
if ($Logs.Count -eq 1 -and $Logs[0] -match ',') {
    $Logs = @($Logs[0] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$ErrorActionPreference = 'Stop'
$owner = 'XueRenYI0'
$repo  = 'InkTeach-Logs'
$api   = "https://api.gitcode.com/api/v5/repos/$owner/$repo"

$tokFile = Join-Path $env:USERPROFILE '.gitcode-token'
if (-not (Test-Path $tokFile)) { throw "missing token: $tokFile" }
$tok = (Get-Content $tokFile -Raw).Trim()
if (-not $tok) { throw "empty token: $tokFile" }

$root    = Split-Path $PSScriptRoot -Parent
$cache   = Join-Path $root 'reports\log-ledger.json'
$logDir  = Join-Path $root 'logs-download'
New-Item -ItemType Directory -Force -Path (Split-Path $cache) | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Curl-Json([string]$url) {
    $tmp = [IO.Path]::GetTempFileName()
    try {
        $null = curl.exe -s --max-time 120 -o $tmp $url
        $raw = [IO.File]::ReadAllText($tmp, [Text.Encoding]::UTF8)
        if (-not $raw) { return $null }
        return ($raw | ConvertFrom-Json)
    }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

function Get-RemoteFile([string]$path) {
    $u = "$api/contents/$path" + "?access_token=$tok"
    $r = Curl-Json $u
    if ($r -and ($r -isnot [array]) -and $r.content) { return $r }
    return $null
}

function Get-LogNames() {
    $u = "$api/contents/logs" + "?access_token=$tok"
    $r = Curl-Json $u
    if (-not $r -or ($r -isnot [array] -and $r.error_code)) { return @() }
    return @($r | Where-Object { $_.type -eq 'file' } | ForEach-Object { $_.name })
}

function Download-Log([string]$name) {
    $dest = Join-Path $logDir $name
    if (Test-Path $dest) { return $dest }
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $r = Get-RemoteFile ("logs/" + [uri]::EscapeDataString($name))
    if (-not $r -or -not $r.content) { return $null }
    $bytes = [Convert]::FromBase64String(($r.content -replace '\s', ''))
    [IO.File]::WriteAllBytes($dest, $bytes)
    return $dest
}

function Read-SessionMeta([string]$name) {
    $date = ''; $machine = ''; $ver = '?'; $gpu = ''; $soft = $null
    $hz = $null; $dpi = $null; $fps = $null; $rec = $null; $ws = $null; $slow150 = $null
    if ($name -match '^log-(\d{8})-(\d{6})-(.+)-([0-9a-f]{6})\.zip$') {
        $date = $Matches[1]; $machine = $Matches[3]
    }
    $path = Download-Log $name
    if ($path) {
        try {
            $z = [IO.Compression.ZipFile]::OpenRead($path)
            $hadSession = $false
            $e = $null
            foreach ($en in $z.Entries) { if ($en.FullName -eq 'session.json') { $e = $en; break } }
            if ($e) {
                $hadSession = $true
                $sr = New-Object IO.StreamReader($e.Open(), [Text.Encoding]::UTF8)
                $json = $sr.ReadToEnd(); $sr.Dispose()
                $s = $json | ConvertFrom-Json
                if ($s.ver)     { $ver = $s.ver }
                if ($s.model)   { $machine = $s.model }
                if ($s.gpu)     { $gpu = $s.gpu }
                if ($null -ne $s.soft) { $soft = [bool]$s.soft }
                if ($s.hz)      { $hz = $s.hz }
                if ($s.dpi)     { $dpi = $s.dpi }
                if ($s.perf) {
                    $fps = $s.perf.fps; $rec = $s.perf.avgMs; $ws = $s.perf.wsMb
                    $slow150 = $s.perf.slow150
                }
            }
            if (-not $hadSession) {
                # 兼容 9.1.8 之前的旧日志：旧 zip 里可能没有 session.json，而且
                # inkteach.log 可能是轮转后的续页（没有启动头）。把 inkteach*.log
                # 都扫一遍，再退回 环境.txt。
                $allLog = ''
                foreach ($en in $z.Entries) {
                    if ($en.FullName -like 'inkteach*.log') {
                        $sr = New-Object IO.StreamReader($en.Open(), [Text.Encoding]::UTF8)
                        $allLog += "`n" + $sr.ReadToEnd(); $sr.Dispose()
                    }
                }
                if ($allLog) {
                    $m = [regex]::Match($allLog, 'InkTeach\s+([0-9]+\.[0-9]+\.[0-9]+)')
                    if ($m.Success) { $ver = $m.Groups[1].Value }
                    $m = [regex]::Match($allLog, 'overlay on monitor .*?dpi=(\d+)')
                    if ($m.Success) { $dpi = $m.Groups[1].Value }
                    $m = [regex]::Match($allLog, 'soft=(\d)')
                    if ($m.Success) { $soft = ($m.Groups[1].Value -eq '1') }
                    $perf = [regex]::Matches($allLog, 'fps=\s*([0-9.]+)\s+\S+=\s*([0-9.]+)')
                    if ($perf.Count -gt 0) {
                        $fps = $perf[$perf.Count - 1].Groups[1].Value
                        $rec = $perf[$perf.Count - 1].Groups[2].Value
                    }
                    $m = [regex]::Match($allLog, 'ws=\s*([0-9.]+)MB')
                    if ($m.Success) { $ws = $m.Groups[1].Value }
                }
                if ($ver -eq '?' -or -not $dpi) {
                    $envEntry = $null
                    foreach ($en in $z.Entries) { if ($en.FullName -eq '环境.txt') { $envEntry = $en; break } }
                    if ($envEntry) {
                        $sr = New-Object IO.StreamReader($envEntry.Open(), [Text.Encoding]::UTF8)
                        $envTxt = $sr.ReadToEnd(); $sr.Dispose()
                        if ($ver -eq '?') {
                            $m = [regex]::Match($envTxt, '([0-9]+\.[0-9]+\.[0-9]+)')
                            if ($m.Success) { $ver = $m.Groups[1].Value }
                        }
                        if (-not $dpi) {
                            $m = [regex]::Match($envTxt, '缩放\s*([0-9.]+)')
                            if ($m.Success) {
                                $dpi = [string][int]([double]$m.Groups[1].Value * 96)
                            }
                        }
                    }
                }
            }
            $z.Dispose()
        }
        catch { }
    }
    return [pscustomobject]@{
        name = $name; date = $date; machine = $machine; ver = $ver
        gpu = $gpu; soft = $soft; hz = $hz; dpi = $dpi
        fps = $fps; rec = $rec; ws = $ws; slow150 = $slow150
    }
}

# ---- ledger load -------------------------------------------------------------
$ledgerSha = $null
$ledger = $null
$remote = Get-RemoteFile 'cases/ledger.json'
if ($remote) {
    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($remote.content -replace '\s', '')))
    $ledger = $json | ConvertFrom-Json
    $ledgerSha = $remote.sha
}
elseif (Test-Path $cache) {
    # 还没有在线台账（第一次 / 离线）：用本地缓存继续，免得 push 把刚开的案冲掉。
    try { $ledger = Get-Content $cache -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}
if (-not $ledger) {
    $ledger = [pscustomobject]@{ schema = 1; updated = ''; cases = @(); logs = [pscustomobject]@{} }
}
$cases = @($ledger.cases)
$logMap = @{}
if ($ledger.logs) {
    foreach ($p in $ledger.logs.PSObject.Properties) { $logMap[$p.Name] = $p.Value }
}

function Save-Ledger() {
    $logsObj = [ordered]@{}
    foreach ($k in ($logMap.Keys | Sort-Object)) { $logsObj[$k] = $logMap[$k] }
    $obj = [ordered]@{
        schema = 1
        updated = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
        cases = @($cases)
        logs = $logsObj
    }
    $json = $obj | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($cache, $json, (New-Object Text.UTF8Encoding $false))
    Write-Host "local ledger: $cache" -ForegroundColor DarkGray
    if ($NoPush) { Write-Host "NoPush: not uploaded" -ForegroundColor Yellow; return }
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    $body = @{ message = 'log case ledger'; content = $b64; branch = 'main' }
    if ($ledgerSha) { $body.sha = $ledgerSha }
    $bodyJson = $body | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($bodyJson)
    $method = if ($ledgerSha) { 'Put' } else { 'Post' }
    try {
        $resp = Invoke-RestMethod -Method $method -Uri "$api/contents/cases/ledger.json?access_token=$tok" `
                                  -Body $bytes -ContentType 'application/json'
        if ($resp.sha) { $ledgerSha = $resp.sha }
        Write-Host "ledger uploaded (cases=$($cases.Count), logs=$($logMap.Count))" -ForegroundColor Green
    }
    catch {
        Write-Host "ledger upload failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "local cache kept: $cache" -ForegroundColor Yellow
    }
}

function Find-Case([string]$id) {
    $c = @($cases | Where-Object { $_.id -eq $id })
    if ($c.Count -eq 0) { throw "case not found: $id" }
    return $c[0]
}

function Mark-Log([string]$name, [string]$caseId, [string]$status) {
    $logMap[$name] = [pscustomobject]@{
        case = $caseId; status = $status; date = (Get-Date).ToString('yyyy-MM-dd')
    }
}

function New-CaseId() {
    $day = Get-Date -Format 'yyyyMMdd'
    $prefix = "C-$day-"
    $n = @($cases | Where-Object { $_.id -like "$prefix*" }).Count + 1
    return ("{0}{1:00}" -f $prefix, $n)
}

# ---- actions -----------------------------------------------------------------
switch ($Action) {
    'pull' {
        Write-Host "cases=$($cases.Count) logs=$($logMap.Count) sha=$ledgerSha"
        if ($remote) { Write-Host "ledger exists in repo" -ForegroundColor Green }
        else { Write-Host "no ledger in repo yet (will be created on push)" -ForegroundColor Yellow }
        $NoPush = $true
        Save-Ledger
    }
    'push' {
        Save-Ledger
    }
    'list' {
        $byStatus = @{}
        foreach ($c in $cases) {
            $s = [string]$c.status
            if (-not $byStatus.ContainsKey($s)) { $byStatus[$s] = 0 }
            $byStatus[$s]++
        }
        $all = @(Get-LogNames)
        $untriaged = @($all | Where-Object { -not $logMap.ContainsKey($_) })
        Write-Host "cases=$($cases.Count)  logs=$($all.Count)  untriaged=$($untriaged.Count)"
        foreach ($k in ($byStatus.Keys | Sort-Object)) { Write-Host ("  {0,-14} {1}" -f $k, $byStatus[$k]) }
        Write-Host ""
        if ($cases.Count -eq 0) { Write-Host "(no cases yet - run -Action triage)" -ForegroundColor DarkGray }
        foreach ($c in ($cases | Sort-Object id)) {
            $fv = if ($c.fix -and $c.fix.version) { "v" + $c.fix.version } else { "-" }
            Write-Host ("  {0}  {1,-12}  {2,-22}  {3,-9}  logs={4}  {5}" -f `
                $c.id, $c.status, $c.device, $fv, @($c.logs).Count, $c.title)
        }
    }
    'triage' {
        $all = @(Get-LogNames)
        $untriaged = @($all | Where-Object { -not $logMap.ContainsKey($_) })
        Write-Host "untriaged logs: $($untriaged.Count) / total $($all.Count)"
        foreach ($name in $untriaged) {
            $m = Read-SessionMeta $name
            $soft = if ($m.soft -eq $true) { 'SOFT' } elseif ($m.soft -eq $false) { 'HW' } else { '?' }
            $perf = if ($null -ne $m.fps) { "fps=$($m.fps) rec=$($m.rec)ms slow150=$($m.slow150) ws=$($m.ws)MB" } else { "(no session.json)" }
            Write-Host ("  {0}`n    dev={1} ver={2} hz={3} dpi={4} gpu={5} [{6}]`n    {7}" -f `
                $name, $m.machine, $m.ver, $m.hz, $m.dpi, $m.gpu, $soft, $perf)
            $known = @($cases | Where-Object { $_.device -and $m.machine -and ($m.machine -like ("*" + $_.device + "*")) })
            foreach ($c in $known) { Write-Host ("    existing case: {0} [{1}] {2}" -f $c.id, $c.status, $c.title) -ForegroundColor DarkYellow }
        }
    }
    'meta' {
        if ($Logs.Count -eq 0) { throw "meta: -Logs required" }
        foreach ($l in $Logs) {
            $m = Read-SessionMeta $l
            $m | Format-List
        }
    }
    'open' {
        if (-not $Title) { throw "open: -Title required" }
        if ($Logs.Count -eq 0) { throw "open: -Logs required" }
        $id = New-CaseId
        $first = Read-SessionMeta $Logs[0]
        $dev = if ($Device) { $Device } elseif ($first.machine) { $first.machine } else { '?' }
        $c = [pscustomobject]@{
            id = $id; title = $Title; status = 'new'; opened = (Get-Date -Format 'yyyy-MM-dd')
            device = $dev; symptom = $Symptom; logs = @($Logs)
            fix = [pscustomobject]@{ version = ''; commit = ''; note = ''; date = '' }
            verification = [pscustomobject]@{ log = ''; note = ''; date = '' }
            notes = @(); updated = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
        }
        $cases += $c
        foreach ($l in $Logs) { Mark-Log $l $id 'open' }
        Write-Host "opened $id ($dev) logs=$($Logs.Count)" -ForegroundColor Green
        Save-Ledger
    }
    'attach' {
        $c = Find-Case $Case
        if ($Logs.Count -eq 0) { throw "attach: -Logs required" }
        $c.logs = @(@($c.logs) + @($Logs))
        if ($c.status -in @('fixed','verified','closed')) {
            $c.status = 'reopened'
            $c.notes = @(@($c.notes) + "reopened by new log: $($Logs -join ', ')")
        }
        foreach ($l in $Logs) { Mark-Log $l $Case $c.status }
        Write-Host "attached to $Case -> $($c.status)" -ForegroundColor Green
        Save-Ledger
    }
    'investigating' {
        $c = Find-Case $Case
        $c.status = 'investigating'
        if ($Note) { $c.notes = @(@($c.notes) + $Note) }
        Write-Host "case $Case -> investigating" -ForegroundColor Yellow
        Save-Ledger
    }
    'note' {
        $c = Find-Case $Case
        if (-not $Note) { throw "note: -Note required" }
        $c.notes = @(@($c.notes) + $Note)
        Write-Host "note added to $Case" -ForegroundColor DarkGray
        Save-Ledger
    }
    'fixed' {
        $c = Find-Case $Case
        $c.status = 'fixed'
        $c.fix = [pscustomobject]@{
            version = $Version; commit = $Commit
            note = $Note; date = (Get-Date -Format 'yyyy-MM-dd')
        }
        foreach ($l in @($c.logs)) { Mark-Log $l $Case 'fixed' }
        Write-Host "marked $Case fixed in $Version" -ForegroundColor Green
        Save-Ledger
    }
    'verified' {
        $c = Find-Case $Case
        if (-not $Logs -or $Logs.Count -eq 0) { throw "verified: -Log required" }
        $l = $Logs[0]
        $c.logs = @(@($c.logs) + @($l))
        $c.status = 'verified'
        $c.verification = [pscustomobject]@{
            log = $l; note = $Note; date = (Get-Date -Format 'yyyy-MM-dd')
        }
        Mark-Log $l $Case 'verified'
        Write-Host "verified $Case with $l" -ForegroundColor Green
        Save-Ledger
    }
    'reopen' {
        $c = Find-Case $Case
        $c.status = 'reopened'
        $c.notes = @(@($c.notes) + "reopened: $Note")
        Write-Host "reopened $Case" -ForegroundColor Yellow
        Save-Ledger
    }
    'close' {
        $c = Find-Case $Case
        $c.status = 'closed'
        if ($Note) { $c.notes = @(@($c.notes) + "closed: $Note") }
        foreach ($l in @($c.logs)) {
            if ($logMap.ContainsKey($l)) { $logMap[$l].status = 'closed' }
        }
        Write-Host "closed $Case" -ForegroundColor Green
        Save-Ledger
    }
    'ignore' {
        if ($Logs.Count -eq 0) { throw "ignore: -Logs required" }
        foreach ($l in $Logs) { Mark-Log $l '' 'ignored' }
        Write-Host "ignored $($Logs.Count) log(s)" -ForegroundColor DarkGray
        Save-Ledger
    }
    'prune' {
        # 删掉台账里已经不在 logs/ 的键（比如误把整串逗号当一个文件名写进去）。
        $all = @(Get-LogNames)
        $removed = 0
        foreach ($k in @($logMap.Keys)) {
            if ($all -notcontains $k) { $logMap.Remove($k); $removed++ }
        }
        Write-Host "pruned $removed stale log key(s)" -ForegroundColor DarkGray
        Save-Ledger
    }
}
