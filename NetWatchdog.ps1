#Requires -Version 5.1
<#
.SYNOPSIS
  NetWatchdog: probes the internet reachability of each network adapter in priority order,
  moves traffic to a backup line when the primary line loses internet, and moves it back on recovery.

.DESCRIPTION
  Every round pings the configured targets from each adapter's own IPv4 address, so the watchdog
  keeps checking a line even after traffic has moved away from it. Switching only changes interface
  metrics; adapters are never disabled. A Wi-Fi adapter that is needed but has no working network
  is connected to saved networks one by one until one has internet; a disabled adapter is re-enabled.

  Targets are IP addresses or host names (probed with ping) or http(s):// URLs (probed with an HTTP
  GET from the adapter's address; only a 2xx answer counts, so login-portal redirects count as failures).

  At the end of each round the state is written to status.json for the tray panel. Request files in the
  queue directory let the panel disable lines, pause automatic switching and change probe settings.
  Settings changed from the panel are stored in overrides.json and layered over config.json.

.PARAMETER ConfigPath
  Path of the config file. Defaults to config.json next to this script.

.PARAMETER DryRun
  Probe and log decisions as usual, but do not change metrics, connect Wi-Fi or enable adapters.

.PARAMETER Cycles
  Exit after this many rounds; 0 runs forever.
#>
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'config.json'),
    [switch]$DryRun,
    [int]$Cycles = 0
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'

$DataDir = Join-Path $env:ProgramData 'NetWatchdog'
$Defaults = @{
    interfaces               = @()
    targets                  = @('223.5.5.5', '119.29.29.29', '1.1.1.1', '8.8.8.8')
    minSuccess               = 1
    failTargets              = 0
    intervalSeconds          = 5
    pingTimeoutMs            = 1000
    failThreshold            = 3
    recoverThreshold         = 3
    reconnectCooldownSeconds = 30
    wifiRetryMinutes         = 5
    wifiHunt                 = $true
    demotedMetric            = 9000
    flushDnsOnSwitch         = $true
    statusEveryMinutes       = 60
    historySize              = 120
    language                 = 'auto'
    logPath                  = (Join-Path $DataDir 'watchdog.log')
    statusPath               = (Join-Path $DataDir 'status.json')
    logMaxKB                 = 1024
}

# ---------- Messages ----------

$Messages = @{
    en = @{
        ConfigFailed        = 'Cannot read the config file, exiting: {0}'
        AlreadyRunning      = 'Another watchdog instance is running; this one exits'
        Started             = 'Watchdog started. Line priority: {0}. Probe targets: {1}'
        DryRunNote          = 'Dry run: decisions are logged, nothing on the system is changed'
        PausedAtStart       = 'Automatic switching is paused'
        DisabledAtStart     = 'Disabled lines: {0}'
        ConfigReloaded      = 'Config reloaded'
        ConfigInvalid       = 'The new config is invalid, keeping the previous one: {0}'
        LineRecovered       = '{0}: internet reachable again ({1})'
        LineLinkDown        = '{0}: link down or no address (adapter status {1})'
        LineFailed          = '{0}: {1} probes in a row failed, internet unreachable ({2})'
        LineFailedOne       = '{0}: the probe failed, internet unreachable ({2})'
        ActiveLine          = 'Active line: {0}'
        AllDown             = 'No enabled line has internet; routing handed back to Windows'
        RoundError          = 'Error in this round: {0}'
        QueueError          = 'Error while handling a request: {0}'
        OverridesReadFailed = 'Cannot read overrides.json, using defaults: {0}'
        OverridesSaveFailed = 'Cannot save overrides.json: {0}'
        Paused              = 'Automatic switching paused; routing handed back to Windows'
        Resumed             = 'Automatic switching resumed'
        UnknownLine         = 'Ignored request: there is no line named {0}'
        KeepOneLine         = 'Refused to disable {0}: at least one line must stay enabled'
        LineDisabled        = 'Line {0} disabled'
        LineEnabled         = 'Line {0} enabled'
        UnknownRequest      = 'Ignored unknown request: {0}'
        SettingChanged      = 'Setting {0} changed to {1}'
        SettingRejected     = 'Ignored setting {0}: {1}'
        SettingsReset       = 'Settings changed in the panel were reset; the config file applies again'
        HttpProbeFailed     = 'HTTP probing is unavailable, URL targets count as failed: {0}'
        WifiScanEmpty       = '{0}: the scan found no networks; trying saved Wi-Fi networks in saved order'
        WifiAllRecent       = '{0}: every nearby saved Wi-Fi network failed recently; trying again in {1} min'
        WifiNoneLeft        = '{0}: no other saved Wi-Fi network nearby to try'
        WifiTrying          = '{0}: trying {1} Wi-Fi networks one by one: {2}'
        WifiTryingOne       = '{0}: trying the Wi-Fi network {2}'
        WifiConnectFailed   = '{0}: cannot connect to "{1}": {2}'
        WifiNoAddress       = '{0}: "{1}" did not connect or get an address within 15 seconds'
        WifiOk              = '{0}: connected to "{1}" with internet access, address {2}'
        WifiNoInternet      = '{0}: "{1}" connected but has no internet, trying the next one'
        WifiBackTo          = '{0}: no nearby saved Wi-Fi network has internet; reconnecting to "{1}"'
        WifiAllFailed       = '{0}: no nearby saved Wi-Fi network has internet'
        AdapterEnable       = '{0}: the adapter is disabled, enabling it'
        AdapterEnableFailed = '{0}: cannot enable the adapter: {1}'
        Demote              = 'Lowering the priority of {0} (metric {1})'
        Restore             = 'Restoring the automatic metric of {0}'
        MetricFailed        = '{0} ({1}): cannot change the metric: {2}'
        Summary             = 'Active line: {0}; {1}'
        SummaryItem         = '{0}={1} ({2})'
        Up                  = 'up'
        Down                = 'down'
        NoAddress           = 'no address'
        None                = 'none'
        ListSep             = ', '
    }
    zh = @{
        ConfigFailed        = '读取配置失败，退出：{0}'
        AlreadyRunning      = '已有一个看门狗实例在运行，本实例退出'
        Started             = '看门狗启动，线路优先级：{0}；探测目标：{1}'
        DryRunNote          = '演练模式：只记录决策，不改动系统'
        PausedAtStart       = '自动切换处于暂停状态'
        DisabledAtStart     = '已停用的线路：{0}'
        ConfigReloaded      = '配置已重新加载'
        ConfigInvalid       = '新配置无效，继续使用旧配置：{0}'
        LineRecovered       = '{0}：外网恢复（{1}）'
        LineLinkDown        = '{0}：链路断开或没有地址（状态 {1}）'
        LineFailed          = '{0}：连续 {1} 次探测失败，判定外网不通（{2}）'
        LineFailedOne       = '{0}：探测失败，判定外网不通（{2}）'
        ActiveLine          = '当前出口：{0}'
        AllDown             = '可用的线路都不通，交还系统自动选路'
        RoundError          = '本轮出错：{0}'
        QueueError          = '处理请求出错：{0}'
        OverridesReadFailed = '读取 overrides.json 失败，按默认值处理：{0}'
        OverridesSaveFailed = '保存 overrides.json 失败：{0}'
        Paused              = '自动切换已暂停，所有线路交还系统自动选路'
        Resumed             = '自动切换已恢复'
        UnknownLine         = '忽略请求：没有名为 {0} 的线路'
        KeepOneLine         = '拒绝停用 {0}：至少要保留一条线路'
        LineDisabled        = '已停用线路 {0}'
        LineEnabled         = '已启用线路 {0}'
        UnknownRequest      = '忽略未知请求：{0}'
        SettingChanged      = '设置 {0} 已改为 {1}'
        SettingRejected     = '忽略设置 {0}：{1}'
        SettingsReset       = '面板上改过的设置已清除，恢复使用配置文件'
        HttpProbeFailed     = 'HTTP 探测不可用，网址类目标一律按失败处理：{0}'
        WifiScanEmpty       = '{0}：扫描不到任何网络，按保存顺序尝试已保存的 Wi-Fi'
        WifiAllRecent       = '{0}：附近已保存的 Wi-Fi 刚试过都不能上网，{1} 分钟后再试'
        WifiNoneLeft        = '{0}：附近没有其他已保存的 Wi-Fi 可以尝试'
        WifiTrying          = '{0}：逐个尝试 {1} 个 Wi-Fi：{2}'
        WifiTryingOne       = '{0}：尝试 Wi-Fi：{2}'
        WifiConnectFailed   = '{0}：无法连接「{1}」：{2}'
        WifiNoAddress       = '{0}：「{1}」15 秒内没有连上或没有拿到地址'
        WifiOk              = '{0}：已连上「{1}」并确认可以上网，地址 {2}'
        WifiNoInternet      = '{0}：「{1}」已连上但不能上网，换下一个'
        WifiBackTo          = '{0}：附近已保存的 Wi-Fi 都不能上网，连回「{1}」'
        WifiAllFailed       = '{0}：附近已保存的 Wi-Fi 都不能上网'
        AdapterEnable       = '{0}：网卡处于禁用状态，重新启用'
        AdapterEnableFailed = '{0}：启用失败：{1}'
        Demote              = '降低 {0} 的优先级（跃点数 {1}）'
        Restore             = '恢复 {0} 的自动跃点数'
        MetricFailed        = '{0}（{1}）调整跃点数失败：{2}'
        Summary             = '出口：{0}；{1}'
        SummaryItem         = '{0}={1}({2})'
        Up                  = '可用'
        Down                = '不通'
        NoAddress           = '无地址'
        None                = '无'
        ListSep             = '、'
    }
}

# 'auto' follows the system display language. The watchdog runs as SYSTEM, whose UI culture is the
# system default language.
function Resolve-Language([string]$Setting) {
    if ($Setting -eq 'en' -or $Setting -eq 'zh') { return $Setting }
    if ((Get-UICulture).TwoLetterISOLanguageName -eq 'zh') { return 'zh' }
    return 'en'
}
$Lang = Resolve-Language 'auto'

function T([string]$Key) {
    $fmt = $Messages[$Lang][$Key]
    if ($args.Count -gt 0) { return ($fmt -f $args) }
    return $fmt
}

# Same as T, but picks the <Key>One message when Count is 1 (English needs a different sentence)
function TN([string]$Key, [int]$Count) {
    if ($Count -eq 1 -and $Messages[$Lang].ContainsKey($Key + 'One')) { $Key = $Key + 'One' }
    $fmt = $Messages[$Lang][$Key]
    return ($fmt -f $args)
}

# ---------- HTTP probe ----------

# PowerShell cannot bind an HTTP request to a source address by itself, so a small C# helper does it.
# Only a 2xx status counts as success; redirects are not followed because login portals use them.
$HttpProbeSource = @'
using System;
using System.Diagnostics;
using System.Net;
public static class NwProbe
{
    public static int Http(string url, string sourceIp, int timeoutMs)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            IPAddress local = IPAddress.Parse(sourceIp);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.AllowAutoRedirect = false;
            req.KeepAlive = false;
            req.Proxy = null;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "NetWatchdog";
            req.ConnectionGroupName = sourceIp;
            req.ServicePoint.BindIPEndPointDelegate = delegate { return new IPEndPoint(local, 0); };
            Stopwatch sw = Stopwatch.StartNew();
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                return code >= 200 && code < 300 ? (int)sw.ElapsedMilliseconds : -1;
            }
        }
        catch { return -1; }
    }
}
'@
$HttpProbeReady = $false
$HttpProbeError = $null
try {
    if (-not ('NwProbe' -as [type])) { Add-Type -TypeDefinition $HttpProbeSource -Language CSharp -ErrorAction Stop }
    $HttpProbeReady = $true
} catch { $HttpProbeError = $_.Exception.Message }

# ---------- Config, log and state files ----------

function Read-Config([string]$Path) {
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $c = @{}
    foreach ($k in $Defaults.Keys) { $c[$k] = $Defaults[$k] }
    foreach ($p in $raw.PSObject.Properties) { $c[$p.Name] = $p.Value }

    $list = @()
    foreach ($i in @($c.interfaces)) {
        $type = 'wired'
        if ($i.PSObject.Properties['type']) { $type = [string]$i.type }
        $profiles = @()
        if ($i.PSObject.Properties['profiles']) { $profiles = @($i.profiles | ForEach-Object { [string]$_ } | Where-Object { $_ }) }
        $list += , @{ alias = [string]$i.alias; type = $type; profiles = $profiles }
    }
    if ($list.Count -eq 0) { throw 'the config has no interfaces' }
    $c.interfaces = $list
    $c.targets = @($c.targets | ForEach-Object { [string]$_ } | Where-Object { $_ })
    if ($c.targets.Count -eq 0) { throw 'the config has no targets' }
    return $c
}

# Every log line carries a kind tag that the tray panel uses to color events:
#   good (recovered), bad (failure), switch (route change or action), info, status (periodic summary)
function Write-Log([string]$Kind, [string]$Message) {
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Kind, $Message
    Write-Host $line
    if (-not $Cfg.logPath) { return }
    try {
        $dir = Split-Path -Parent $Cfg.logPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $f = Get-Item -LiteralPath $Cfg.logPath -ErrorAction SilentlyContinue
        if ($f -and $f.Length -gt ([int64]$Cfg.logMaxKB * 1KB)) {
            Move-Item -LiteralPath $Cfg.logPath -Destination ($Cfg.logPath + '.1') -Force
        }
        Add-Content -LiteralPath $Cfg.logPath -Value $line -Encoding UTF8
    } catch { }
}

# Write to a temp file and swap it in, so readers always see a complete JSON document
function Write-JsonFile([string]$Path, $Object) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = ConvertTo-Json $Object -Depth 6 -Compress
    $tmp = $Path + '.tmp'
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding($false)))
    # The backup argument must be a real null: PowerShell turns $null into an empty string here
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($tmp, $Path, [NullString]::Value) } else { [IO.File]::Move($tmp, $Path) }
}

function Get-StatePaths {
    $dir = Split-Path -Parent $Cfg.statusPath
    return @{ Queue = (Join-Path $dir 'queue'); Overrides = (Join-Path $dir 'overrides.json') }
}

# Changes made from the panel (disabled lines, pause) live in their own file; config.json is never rewritten
function Read-Overrides {
    $o = @{ paused = $false; disabled = @(); settings = @{} }
    $path = (Get-StatePaths).Overrides
    if (Test-Path -LiteralPath $path) {
        try {
            $j = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.PSObject.Properties['paused']) { $o.paused = [bool]$j.paused }
            if ($j.PSObject.Properties['disabled']) { $o.disabled = @($j.disabled | ForEach-Object { [string]$_ } | Where-Object { $_ }) }
            if ($j.PSObject.Properties['settings'] -and $j.settings) {
                foreach ($p in $j.settings.PSObject.Properties) {
                    try { $o.settings[$p.Name] = ConvertTo-Setting $p.Name $p.Value } catch { }
                }
            }
        } catch {
            Write-Log 'bad' (T 'OverridesReadFailed' $_.Exception.Message)
        }
    }
    return $o
}

function Save-Overrides {
    try { Write-JsonFile (Get-StatePaths).Overrides ([ordered]@{ paused = [bool]$Ov.paused; disabled = @($Ov.disabled); settings = $Ov.settings }) }
    catch { Write-Log 'bad' (T 'OverridesSaveFailed' $_.Exception.Message) }
}

# Settings the panel may change, with their allowed values. Returns the normalized value or throws.
function ConvertTo-Setting([string]$Key, $Value) {
    $ranges = @{
        failTargets = @(0, 10); pingTimeoutMs = @(200, 10000); intervalSeconds = @(1, 300)
        failThreshold = @(1, 20); recoverThreshold = @(1, 20); wifiRetryMinutes = @(1, 240)
    }
    if ($ranges.ContainsKey($Key)) {
        $n = 0
        if (-not [int]::TryParse([string]$Value, [ref]$n)) { throw "not a number: $Value" }
        if ($n -lt $ranges[$Key][0] -or $n -gt $ranges[$Key][1]) { throw ('out of range {0}-{1}' -f $ranges[$Key][0], $ranges[$Key][1]) }
        return $n
    }
    switch ($Key) {
        'wifiHunt' { return [bool]$Value }
        'language' {
            if (@('auto', 'zh', 'en') -notcontains [string]$Value) { throw "unknown language $Value" }
            return [string]$Value
        }
        'targets' {
            $list = @($Value | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
            if ($list.Count -lt 1 -or $list.Count -gt 10) { throw 'between 1 and 10 targets are allowed' }
            foreach ($t in $list) {
                if ($t.Length -gt 200 -or $t -notmatch '^(https?://[^\s]+|[A-Za-z0-9.:\[\]-]+)$') { throw "invalid target $t" }
            }
            return $list
        }
    }
    throw 'unknown setting'
}

function Get-Config {
    $c = Read-Config $ConfigPath
    foreach ($k in @($Ov.settings.Keys)) { $c[$k] = $Ov.settings[$k] }
    return $c
}

# How many failed targets make a round fail. failTargets wins; otherwise derived from minSuccess.
function Get-FailLimit {
    $n = $Cfg.targets.Count
    $f = [int]$Cfg.failTargets
    if ($f -gt 0) { return [Math]::Min($f, $n) }
    return [Math]::Max(1, $n - [int]$Cfg.minSuccess + 1)
}

function Write-Status {
    if (-not $Cfg.statusPath) { return }
    $ifs = @()
    foreach ($if in $Cfg.interfaces) {
        $s = $State[$if.alias]
        $u = $Info[$if.alias]
        if (-not $s -or -not $u) { continue }
        $ifs += , ([ordered]@{
            alias         = $if.alias
            type          = $if.type
            adapterStatus = $u.Status
            ip            = $u.Ip
            healthy       = [bool]$s.Healthy
            enabled       = ($Ov.disabled -notcontains $if.alias)
            latencyMs     = $s.Latency
            since         = $s.Since.ToString('o')
            history       = @($s.History)
        })
    }
    $obj = [ordered]@{
        version         = 1
        updated         = (Get-Date).ToString('o')
        started         = $StartedAt.ToString('o')
        dryRun          = [bool]$DryRun
        paused          = [bool]$Ov.paused
        active          = $Active
        activeSince     = $ActiveSince.ToString('o')
        routeOwner      = $RouteOwner
        wifiAttempt     = $WifiAttempt
        language        = $Lang
        intervalSeconds = [int]$Cfg.intervalSeconds
        targets         = @($Cfg.targets)
        logPath         = $Cfg.logPath
        configPath      = (Resolve-Path -LiteralPath $ConfigPath).Path
        interfaces      = $ifs
        settings        = [ordered]@{
            targets          = @($Cfg.targets)
            failTargets      = (Get-FailLimit)
            pingTimeoutMs    = [int]$Cfg.pingTimeoutMs
            intervalSeconds  = [int]$Cfg.intervalSeconds
            failThreshold    = [int]$Cfg.failThreshold
            recoverThreshold = [int]$Cfg.recoverThreshold
            wifiRetryMinutes = [int]$Cfg.wifiRetryMinutes
            wifiHunt         = [bool]$Cfg.wifiHunt
            language         = [string]$Cfg.language
            overridden       = @($Ov.settings.Keys)
        }
    }
    try { Write-JsonFile $Cfg.statusPath $obj } catch { }
}

# ---------- Panel request queue ----------

# A request is a *.req file in the queue directory holding one line:
#   probe | pause | resume | enable <adapter> | disable <adapter> | set <setting> <json value> | reset
# Requests older than 2 minutes, files over 4 KB and reparse points are discarded.
function Invoke-Queue {
    $dir = (Get-StatePaths).Queue
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    $handled = $false
    $files = @(Get-ChildItem -LiteralPath $dir -Filter '*.req' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc)
    foreach ($f in $files) {
        $text = $null
        try {
            $fresh = ([DateTime]::UtcNow - $f.LastWriteTimeUtc).TotalMinutes -lt 2
            $plain = -not ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)
            if ($fresh -and $plain -and $f.Length -lt 4096) {
                $text = ([IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)).Trim()
            }
        } catch { }
        Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue
        if ($text) {
            Invoke-Request $text
            $handled = $true
        }
    }
    return $handled
}

function Invoke-Request([string]$Text) {
    $clean = ($Text -replace '[\x00-\x1f]', ' ')
    $safe = $clean
    if ($safe.Length -gt 80) { $safe = $safe.Substring(0, 80) }
    $parts = $clean -split '\s+', 2
    $verb = $parts[0].ToLowerInvariant()
    $arg = ''
    if ($parts.Count -gt 1) { $arg = $parts[1].Trim() }
    $aliases = @($Cfg.interfaces | ForEach-Object { $_.alias })

    switch ($verb) {
        'probe' { return }
        'pause' {
            if (-not $Ov.paused) {
                $Ov.paused = $true
                Save-Overrides
                Write-Log 'switch' (T 'Paused')
            }
        }
        'resume' {
            if ($Ov.paused) {
                $Ov.paused = $false
                Save-Overrides
                Write-Log 'good' (T 'Resumed')
            }
        }
        'disable' {
            if ($aliases -notcontains $arg) { Write-Log 'info' (T 'UnknownLine' $arg); return }
            if ($Ov.disabled -contains $arg) { return }
            $left = @($aliases | Where-Object { $_ -ne $arg -and $Ov.disabled -notcontains $_ })
            if ($left.Count -eq 0) { Write-Log 'bad' (T 'KeepOneLine' $arg); return }
            $Ov.disabled = @($Ov.disabled) + $arg
            Save-Overrides
            Write-Log 'switch' (T 'LineDisabled' $arg)
        }
        'enable' {
            if ($Ov.disabled -notcontains $arg) { return }
            $Ov.disabled = @($Ov.disabled | Where-Object { $_ -ne $arg })
            Save-Overrides
            Write-Log 'good' (T 'LineEnabled' $arg)
        }
        'set' {
            $kv = $arg -split '\s+', 2
            $key = $kv[0]
            try {
                if ($kv.Count -lt 2) { throw 'missing value' }
                $value = ConvertTo-Setting $key ($kv[1] | ConvertFrom-Json)
            } catch {
                Write-Log 'info' (T 'SettingRejected' $key $_.Exception.Message)
                return
            }
            $Ov.settings[$key] = $value
            Save-Overrides
            $script:Cfg = Get-Config
            $script:Lang = Resolve-Language ([string]$Cfg.language)
            $shown = $value
            if ($value -is [array]) { $shown = $value -join (T 'ListSep') }
            Write-Log 'switch' (T 'SettingChanged' $key $shown)
        }
        'reset' {
            if ($Ov.settings.Count -eq 0) { return }
            $Ov.settings = @{}
            Save-Overrides
            $script:Cfg = Get-Config
            $script:Lang = Resolve-Language ([string]$Cfg.language)
            Write-Log 'switch' (T 'SettingsReset')
        }
        default { Write-Log 'info' (T 'UnknownRequest' $safe) }
    }
}

# ---------- Probing ----------

function Get-Uplink([hashtable]$If) {
    $r = @{ Present = $false; Status = 'Missing'; Ip = $null }
    $ad = Get-NetAdapter -Name $If.alias -ErrorAction SilentlyContinue
    if (-not $ad) { return $r }
    $r.Present = $true
    $r.Status = [string]$ad.Status
    if ($ad.Status -eq 'Up') {
        $addr = Get-NetIPAddress -InterfaceIndex $ad.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.PrefixOrigin -ne 'WellKnown' -and $_.AddressState -eq 'Preferred' } |
            Select-Object -First 1
        if ($addr) { $r.Ip = [string]$addr.IPAddress }
    }
    return $r
}

# Probe one target from the given source address; returns latency in ms, or -1 when it failed.
# For ping, success is judged by "TTL=" in the output: ping.exe also exits with 0 when a gateway answers
# "Destination host unreachable", so the exit code cannot be trusted.
function Test-Target([string]$Target, [string]$SourceIp) {
    if ($Target -match '^https?://') {
        if (-not $HttpProbeReady) { return -1 }
        return [NwProbe]::Http($Target, $SourceIp, [int]$Cfg.pingTimeoutMs)
    }
    $out = & ping.exe -4 -n 1 -w ([int]$Cfg.pingTimeoutMs) -S $SourceIp $Target 2>$null | Out-String
    if ($out -notmatch 'TTL=') { return -1 }
    if ($out -match '[=<]\s*(\d+)\s*ms') { return [int]$Matches[1] }
    return 0
}

# A round fails when at least Get-FailLimit targets fail. Probing stops as soon as the outcome is decided.
function Test-Uplink([string]$SourceIp) {
    $n = $Cfg.targets.Count
    $limit = Get-FailLimit
    $ok = 0
    $fail = 0
    $lat = @()
    foreach ($t in $Cfg.targets) {
        $r = Test-Target $t $SourceIp
        if ($r -ge 0) { $ok++; $lat += $r } else { $fail++ }
        if ($fail -ge $limit -or $ok -gt $n - $limit) { break }
    }
    $res = @{ Ok = ($fail -lt $limit); Latency = $null }
    if ($res.Ok -and $lat.Count -gt 0) { $res.Latency = [int][Math]::Round(($lat | Measure-Object -Average).Average) }
    return $res
}

# The adapter that owns the default route Windows actually uses: lowest route metric + interface metric
function Get-RouteOwner {
    $best = $null
    $bestCost = [int]::MaxValue
    foreach ($r in @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)) {
        $ipif = Get-NetIPInterface -InterfaceIndex $r.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
        if (-not $ipif -or [string]$ipif.ConnectionState -ne 'Connected') { continue }
        $cost = [int]$r.RouteMetric + [int]$ipif.InterfaceMetric
        if ($cost -lt $bestCost) { $bestCost = $cost; $best = [string]$r.InterfaceAlias }
    }
    return $best
}

# ---------- Wi-Fi and adapters ----------

function Get-WlanProfiles([string]$Interface) {
    $names = @()
    foreach ($line in (& netsh.exe wlan show profiles "interface=$Interface" 2>$null)) {
        if ($line -match '^\s{4}\S.*?\s:\s(.+?)\s*$') { $names += $Matches[1] }
    }
    return @($names | Select-Object -Unique)
}

# Scan nearby networks and return SSIDs sorted by signal, strongest first. When an SSID has several
# access points the strongest one counts. The signal label depends on the system language
# (Signal / 信号), so signal lines are recognised by a value of the form "NN%".
function Get-VisibleNetworks([string]$Interface) {
    $signal = @{}
    $order = @()
    $current = $null
    foreach ($line in (& netsh.exe wlan show networks mode=bssid "interface=$Interface" 2>$null)) {
        if ($line -match '^SSID \d+\s*:\s(.*?)\s*$') {
            $current = $Matches[1]
            if ($current -and -not $signal.ContainsKey($current)) { $signal[$current] = 0; $order += $current }
        } elseif ($current -and $line -match ':\s*(\d+)%\s*$') {
            if ([int]$Matches[1] -gt $signal[$current]) { $signal[$current] = [int]$Matches[1] }
        }
    }
    return @($order | Sort-Object { -$signal[$_] } | ForEach-Object { @{ Ssid = $_; Signal = $signal[$_] } })
}

# SSID the adapter is connected to, or $null
function Get-CurrentSsid([string]$Interface) {
    $inBlock = $false
    foreach ($line in (& netsh.exe wlan show interfaces 2>$null)) {
        if ($line -match '^\s*$') { continue }
        if ($line -match '^\s+\S[^:]*?\s:\s(.*?)\s*$' -and $Matches[1] -eq $Interface) { $inBlock = $true; continue }
        if ($inBlock -and $line -match '^\s+SSID\s+:\s(.*?)\s*$') { return $Matches[1] }
        if ($inBlock -and $line -match '^\s+(Name|名称)\s+:') { $inBlock = $false }
    }
    return $null
}

# Wait until the adapter is on the given network and has an address. Right after a switch the old
# address may still be present, so the SSID is checked as well.
function Wait-Wifi([hashtable]$If, [string]$Ssid, [int]$Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        Start-Sleep -Seconds 1
        if ((Get-CurrentSsid $If.alias) -eq $Ssid -and (Get-Uplink $If).Ip) { return $true }
    }
    return $false
}

# Routes and ARP may not be ready the moment an address arrives; try up to 3 times
function Test-NewWifi([hashtable]$If) {
    for ($i = 0; $i -lt 3; $i++) {
        $ip = (Get-Uplink $If).Ip
        if ($ip -and (Test-Uplink $ip).Ok) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

# Try Wi-Fi networks one by one until one really has internet:
#   - with profiles configured, in the configured order; otherwise saved networks seen by the scan,
#     strongest signal first;
#   - the network the adapter is on now (and which has no internet) is skipped;
#   - after each connection the targets are pinged from this adapter; no reply means next network;
#   - networks that failed are not retried for wifiRetryMinutes; if all fail, reconnect to the original one.
$WifiTried = @{}
$WifiIdleNote = $null
$WifiAttempt = $null
function Connect-Wifi([hashtable]$If) {
    $saved = @(Get-WlanProfiles $If.alias)
    $visible = @(Get-VisibleNetworks $If.alias)
    if ($If.profiles.Count -gt 0) {
        $candidates = @($If.profiles)
    } elseif ($visible.Count -gt 0) {
        $candidates = @($visible | Where-Object { $saved -contains $_.Ssid } | ForEach-Object { $_.Ssid })
    } else {
        # An empty scan usually means location permission blocked it; fall back to the first 5 saved networks
        $candidates = @($saved | Select-Object -First 5)
        if ($candidates.Count -gt 0) { Write-Log 'info' (T 'WifiScanEmpty' $If.alias) }
    }

    $original = Get-CurrentSsid $If.alias
    $now = Get-Date
    $retry = [double]$Cfg.wifiRetryMinutes
    $skipped = @($candidates | Where-Object { $WifiTried.ContainsKey($_) -and ($now - $WifiTried[$_]).TotalMinutes -lt $retry })
    $candidates = @($candidates | Where-Object { $_ -ne $original -and $skipped -notcontains $_ })

    # Attempts repeat every reconnectCooldownSeconds; log "nothing to try" once until something changes
    if ($candidates.Count -eq 0) {
        $key = 'WifiNoneLeft'
        if ($skipped.Count -gt 0) { $key = 'WifiAllRecent' }
        $note = $If.alias + '|' + $key
        if ($script:WifiIdleNote -ne $note) {
            if ($key -eq 'WifiAllRecent') { Write-Log 'info' (T $key $If.alias $retry) } else { Write-Log 'info' (T $key $If.alias) }
            $script:WifiIdleNote = $note
        }
        return
    }
    $script:WifiIdleNote = $null
    Write-Log 'switch' (TN 'WifiTrying' $candidates.Count $If.alias $candidates.Count ($candidates -join (T 'ListSep')))
    if ($original) { $WifiTried[$original] = $now }
    if ($DryRun) { return }

    $n = 0
    foreach ($ssid in $candidates) {
        $n++
        $script:WifiAttempt = [ordered]@{ alias = $If.alias; ssid = $ssid; index = $n; total = $candidates.Count }
        Write-Status
        $out = & netsh.exe wlan connect "name=$ssid" "interface=$($If.alias)" 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            Write-Log 'bad' (T 'WifiConnectFailed' $If.alias $ssid $out.Trim())
        } elseif (-not (Wait-Wifi $If $ssid 15)) {
            Write-Log 'bad' (T 'WifiNoAddress' $If.alias $ssid)
        } elseif (Test-NewWifi $If) {
            Write-Log 'good' (T 'WifiOk' $If.alias $ssid (Get-Uplink $If).Ip)
            $WifiTried.Remove($ssid)
            $script:WifiAttempt = $null
            return
        } else {
            Write-Log 'bad' (T 'WifiNoInternet' $If.alias $ssid)
        }
        $WifiTried[$ssid] = Get-Date
    }
    $script:WifiAttempt = $null

    if ($original) {
        Write-Log 'bad' (T 'WifiBackTo' $If.alias $original)
        & netsh.exe wlan connect "name=$original" "interface=$($If.alias)" 2>&1 | Out-Null
    } else {
        Write-Log 'bad' (T 'WifiAllFailed' $If.alias)
    }
}

function Enable-Adapter([hashtable]$If) {
    Write-Log 'switch' (T 'AdapterEnable' $If.alias)
    if ($DryRun) { return }
    try { Enable-NetAdapter -Name $If.alias -Confirm:$false -ErrorAction Stop }
    catch { Write-Log 'bad' (T 'AdapterEnableFailed' $If.alias $_.Exception.Message) }
}

# Bring an adapter to the desired state: demoting sets the metric to demotedMetric; restoring only
# touches interfaces whose metric equals demotedMetric, so metrics the user set by hand are kept.
# The actual state is compared every round, which also corrects re-plugged adapters and outside changes.
$DryRunDemoted = @{}
function Sync-Metric([hashtable]$If, [bool]$Demote) {
    $ipifs = @(Get-NetIPInterface -InterfaceAlias $If.alias -ErrorAction SilentlyContinue)
    if ($ipifs.Count -eq 0) { return }
    $m = [int]$Cfg.demotedMetric

    if ($DryRun) {
        if (-not $DryRunDemoted.ContainsKey($If.alias)) {
            $DryRunDemoted[$If.alias] = [bool](@($ipifs | Where-Object { $_.InterfaceMetric -eq $m }).Count)
        }
        if ($DryRunDemoted[$If.alias] -ne $Demote) {
            $DryRunDemoted[$If.alias] = $Demote
            if ($Demote) { Write-Log 'switch' (T 'Demote' $If.alias $m) } else { Write-Log 'switch' (T 'Restore' $If.alias) }
        }
        return
    }

    if ($Demote) {
        $todo = @($ipifs | Where-Object { $_.InterfaceMetric -ne $m })
    } else {
        $todo = @($ipifs | Where-Object { $_.InterfaceMetric -eq $m -and $_.AutomaticMetric -eq 'Disabled' })
    }
    if ($todo.Count -eq 0) { return }
    if ($Demote) { Write-Log 'switch' (T 'Demote' $If.alias $m) } else { Write-Log 'switch' (T 'Restore' $If.alias) }
    foreach ($x in $todo) {
        try {
            if ($Demote) { Set-NetIPInterface -InputObject $x -InterfaceMetric $m -ErrorAction Stop }
            else { Set-NetIPInterface -InputObject $x -AutomaticMetric Enabled -ErrorAction Stop }
        } catch {
            Write-Log 'bad' (T 'MetricFailed' $If.alias $x.AddressFamily $_.Exception.Message)
        }
    }
}

function Format-Status {
    $parts = foreach ($if in $Cfg.interfaces) {
        $s = $State[$if.alias]
        $u = $Info[$if.alias]
        $h = T 'Down'
        if ($s -and $s.Healthy) { $h = T 'Up' }
        $ip = T 'NoAddress'
        if ($u -and $u.Ip) { $ip = $u.Ip }
        T 'SummaryItem' $if.alias $h $ip
    }
    $a = $Active
    if (-not $a) { $a = T 'None' }
    return (T 'Summary' $a ($parts -join (T 'ListSep')))
}

# ---------- Main loop ----------

try {
    $Cfg = Read-Config $ConfigPath
    $Ov = Read-Overrides
    $Cfg = Get-Config
    $Lang = Resolve-Language ([string]$Cfg.language)
} catch {
    $Cfg = @{ logPath = $Defaults.logPath; logMaxKB = $Defaults.logMaxKB }
    Write-Log 'bad' (T 'ConfigFailed' $_.Exception.Message)
    exit 1
}
$CfgStamp = (Get-Item -LiteralPath $ConfigPath).LastWriteTimeUtc

if (-not $DryRun) {
    $Mutex = New-Object System.Threading.Mutex($false, 'Global\NetWatchdog')
    try { $owned = $Mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) {
        Write-Log 'info' (T 'AlreadyRunning')
        exit 0
    }
}

$State = @{}
$Info = @{}
$Active = $null
$ActiveSince = Get-Date
$RouteOwner = $null
$StartedAt = Get-Date
$Started = $false
$LastStatus = Get-Date
$Round = 0

Write-Log 'info' (T 'Started' (($Cfg.interfaces | ForEach-Object { $_.alias }) -join ' > ') ($Cfg.targets -join ', '))
if ($DryRun) { Write-Log 'info' (T 'DryRunNote') }
if (-not $HttpProbeReady -and @($Cfg.targets | Where-Object { $_ -match '^https?://' }).Count -gt 0) { Write-Log 'bad' (T 'HttpProbeFailed' $HttpProbeError) }
if ($Ov.paused) { Write-Log 'info' (T 'PausedAtStart') }
if ($Ov.disabled.Count -gt 0) { Write-Log 'info' (T 'DisabledAtStart' ($Ov.disabled -join (T 'ListSep'))) }

while ($true) {
    try {
        # Reload the config when it changes; keep the old one if the new one is invalid
        $stamp = (Get-Item -LiteralPath $ConfigPath).LastWriteTimeUtc
        if ($stamp -ne $CfgStamp) {
            $CfgStamp = $stamp
            try {
                $Cfg = Get-Config
                $Lang = Resolve-Language ([string]$Cfg.language)
                $DryRunDemoted.Clear()
                Write-Log 'info' (T 'ConfigReloaded')
            } catch {
                Write-Log 'bad' (T 'ConfigInvalid' $_.Exception.Message)
            }
        }

        $prevActiveIdx = -1
        for ($i = 0; $i -lt $Cfg.interfaces.Count; $i++) {
            if ($Cfg.interfaces[$i].alias -eq $Active) { $prevActiveIdx = $i }
        }

        # 1. Probe every line with debouncing: failThreshold failures in a row mark a line down.
        #    A line ranked above the current active line needs recoverThreshold successes in a row to count
        #    as recovered, which prevents flapping; any other line (a backup, or any line while nothing works)
        #    counts as up after one success, so traffic moves over as soon as possible.
        $Info = @{}
        for ($i = 0; $i -lt $Cfg.interfaces.Count; $i++) {
            $if = $Cfg.interfaces[$i]
            if (-not $State.ContainsKey($if.alias)) {
                $State[$if.alias] = @{
                    Healthy = $null; Fail = 0; Ok = 0; Latency = $null; Since = (Get-Date)
                    LastReconnect = [datetime]::MinValue
                    History = (New-Object 'System.Collections.Generic.List[int]')
                }
            }
            $s = $State[$if.alias]
            $u = Get-Uplink $if
            $Info[$if.alias] = $u

            $probe = @{ Ok = $false; Latency = $null }
            if ($u.Ip) { $probe = Test-Uplink $u.Ip }
            $raw = $probe.Ok
            if ($raw) { $s.Fail = 0; $s.Ok++ } else { $s.Ok = 0; $s.Fail++ }
            $s.Latency = $probe.Latency

            # History for the panel chart: latency in ms; -1 = has an address but no reply; -2 = no link or address
            $sample = -2
            if ($u.Ip) { $sample = -1 }
            if ($raw) { $sample = 0; if ($null -ne $probe.Latency) { $sample = [int]$probe.Latency } }
            $s.History.Add($sample)
            while ($s.History.Count -gt [int]$Cfg.historySize) { $s.History.RemoveAt(0) }

            $need = 1
            if ($prevActiveIdx -ge 0 -and $i -lt $prevActiveIdx) { $need = [int]$Cfg.recoverThreshold }

            $prev = $s.Healthy
            if ($null -eq $prev) { $s.Healthy = $raw }
            elseif ($prev -and -not $u.Ip) { $s.Healthy = $false }
            elseif ($prev -and $s.Fail -ge [int]$Cfg.failThreshold) { $s.Healthy = $false }
            elseif (-not $prev -and $s.Ok -ge $need) { $s.Healthy = $true }

            if ($null -ne $prev -and $s.Healthy -ne $prev) {
                $s.Since = Get-Date
                if ($s.Healthy) { Write-Log 'good' (T 'LineRecovered' $if.alias $u.Ip) }
                elseif (-not $u.Ip) { Write-Log 'bad' (T 'LineLinkDown' $if.alias $u.Status) }
                else { Write-Log 'bad' (TN 'LineFailed' $s.Fail $if.alias $s.Fail $u.Ip) }
            }
        }

        # 2. Pick the active line: the highest-priority enabled line that is up. Lines ranked above it and
        #    disabled lines are demoted; the rest keep automatic metrics. While paused everything is automatic.
        $activeIdx = -1
        for ($i = 0; $i -lt $Cfg.interfaces.Count; $i++) {
            $a = $Cfg.interfaces[$i].alias
            if ($Ov.disabled -notcontains $a -and $State[$a].Healthy) { $activeIdx = $i; break }
        }
        for ($i = 0; $i -lt $Cfg.interfaces.Count; $i++) {
            $off = $Ov.disabled -contains $Cfg.interfaces[$i].alias
            $demote = (-not $Ov.paused) -and $activeIdx -ge 0 -and ($off -or $i -lt $activeIdx)
            Sync-Metric $Cfg.interfaces[$i] $demote
        }

        $newActive = $null
        if ($activeIdx -ge 0 -and -not $Ov.paused) { $newActive = $Cfg.interfaces[$activeIdx].alias }
        if (-not $Started -or $newActive -ne $Active) {
            $Active = $newActive
            $ActiveSince = Get-Date
            $WifiIdleNote = $null
            if ($Active) { Write-Log 'switch' (T 'ActiveLine' $Active) }
            elseif (-not $Ov.paused) { Write-Log 'bad' (T 'AllDown') }
            if ($Started -and $Cfg.flushDnsOnSwitch -and -not $DryRun) { Clear-DnsClientCache -ErrorAction SilentlyContinue }
            $Started = $true
        }

        # 3. Bring lines up. Consider enabled adapters ranked above the active line (all of them when nothing
        #    works) and act when the adapter has no address, or when nothing works and the Wi-Fi adapter is on
        #    a network without internet (then other networks are tried). Wi-Fi tries saved networks one by one;
        #    a disabled adapter is re-enabled. Attempts on one adapter are at least reconnectCooldownSeconds apart.
        if (-not $Ov.paused) {
            $limit = $Cfg.interfaces.Count
            if ($activeIdx -ge 0) { $limit = $activeIdx }
            for ($i = 0; $i -lt $limit; $i++) {
                $if = $Cfg.interfaces[$i]
                if ($Ov.disabled -contains $if.alias) { continue }
                $u = $Info[$if.alias]
                $s = $State[$if.alias]
                if (-not $u.Present) { continue }
                $stuck = $activeIdx -lt 0 -and $if.type -eq 'wifi' -and $u.Ip -and -not $s.Healthy
                if ($u.Ip -and -not $stuck) { continue }
                if (((Get-Date) - $s.LastReconnect).TotalSeconds -lt [int]$Cfg.reconnectCooldownSeconds) { continue }
                $s.LastReconnect = Get-Date
                if ($u.Status -eq 'Disabled') { Enable-Adapter $if }
                elseif ($if.type -eq 'wifi' -and $Cfg.wifiHunt) { Connect-Wifi $if }
            }
        }

        $RouteOwner = Get-RouteOwner
        Write-Status

        if ([int]$Cfg.statusEveryMinutes -gt 0 -and ((Get-Date) - $LastStatus).TotalMinutes -ge [int]$Cfg.statusEveryMinutes) {
            Write-Log 'status' (Format-Status)
            $LastStatus = Get-Date
        }
    } catch {
        Write-Log 'bad' (T 'RoundError' $_.Exception.Message)
    }

    $Round++
    if ($Cycles -gt 0 -and $Round -ge $Cycles) {
        Write-Log 'status' (Format-Status)
        break
    }

    # Wait for the next round, checking the request queue every 250 ms; a request starts a new round at once
    $deadline = (Get-Date).AddSeconds([int]$Cfg.intervalSeconds)
    while ((Get-Date) -lt $deadline) {
        $got = $false
        try { $got = Invoke-Queue } catch { Write-Log 'bad' (T 'QueueError' $_.Exception.Message) }
        if ($got) { break }
        Start-Sleep -Milliseconds 250
    }
}
