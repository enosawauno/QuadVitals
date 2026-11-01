# Tray-mode checks: tray mode runs, single instance is enforced, --status works from another
# process, and --exit cleans up (no orphan process, shared memory released).
param([int]$StartupSeconds = 8)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'build\out\agent\QuadVitals.Agent.exe'
$coreDll = Join-Path $root 'build\out\agent\QuadVitals.Core.dll'
if (-not (Test-Path -LiteralPath $exe)) { throw "missing $exe (run scripts\build-agent.ps1)" }
Add-Type -Path $coreDll

$fail = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $what $detail" }
    else { "  FAIL  $what $detail"; $script:fail++ }
}

& (Join-Path $PSScriptRoot 'stop-agent.ps1')
Start-Sleep -Milliseconds 500

"=== 1. tray 模式启动"
$agentStartedUtc = [DateTime]::UtcNow
$tray = Start-Process -FilePath $exe -PassThru
$connected = $false
for ($i = 0; $i -lt $StartupSeconds * 2 -and -not $connected; $i++) {
    Start-Sleep -Milliseconds 500
    $reader = [QuadVitals.Core.MetricsChannel+Reader]::TryOpen()
    if ($reader) { $connected = $true }
}
Check '进程存活' (-not $tray.HasExited) "pid=$($tray.Id)"
Check '共享内存已建立' $connected

# Opening the channel is not proof the agent is sampling: the band (and this script) can hold
# a mapping left over from an earlier agent. Wait for a fresh heartbeat before asserting.
$snapshot = $null
$fresh = $false
for ($i = 0; $i -lt 60 -and -not $fresh; $i++) {
    Start-Sleep -Milliseconds 500
    $snapshot = if ($reader) { $reader.TryRead() } else { $null }
    # The band can hold an old mapping (or a Core test may have published into it). Only a
    # frame stamped after this process started proves the new agent is publishing.
    if ($snapshot -and $snapshot.TimestampUtc -ge $agentStartedUtc -and
        $snapshot.IsAlive([DateTime]::UtcNow, [TimeSpan]::FromSeconds(3))) {
        $fresh = $true
    }
}
Check '快照可读' ($null -ne $snapshot)
Check '心跳在线（等待首次发布）' $fresh "$snapshot"

"=== 2. 单实例"
$second = Start-Process -FilePath $exe -PassThru -Wait
Check '第二个实例自行退出' ($second.HasExited) "exit=$($second.ExitCode)"
Check '第二个实例退出码为 0' ($second.ExitCode -eq 0)
Check '第一个实例仍在运行' (-not $tray.HasExited)

"=== 3. --status 从外部进程读取"
& $exe --status | ForEach-Object { "    $_" }
$statusCode = $LASTEXITCODE
Check '--status 退出码 0（在线）' ($statusCode -eq 0) "exit=$statusCode"

"=== 4. --exit 清理"
& $exe --exit | Out-Null
$exited = $false
for ($i = 0; $i -lt 20 -and -not $exited; $i++) {
    Start-Sleep -Milliseconds 500
    $exited = $tray.HasExited
}
Check '进程已退出' $exited
$remaining = @(Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue).Count
Check '无残留进程' ($remaining -eq 0) "count=$remaining"

if ($reader) { $reader.Dispose() }
Start-Sleep -Milliseconds 500
# The band (inside Explorer) legitimately keeps the mapping alive after the agent exits, so
# "TryOpen returns null" is the wrong assertion. What matters is that no live snapshot remains.
$reopened = [QuadVitals.Core.MetricsChannel+Reader]::TryOpen()
$aliveAfter = $false
if ($reopened) {
    # The band shows the last sample for up to 3 s after the agent exits, so wait for
    # the heartbeat to age out instead of asserting immediately.
    for ($i = 0; $i -lt 16; $i++) {
        $after = $reopened.TryRead()
        $aliveAfter = $after -and $after.IsAlive([DateTime]::UtcNow, [TimeSpan]::FromSeconds(3))
        if (-not $aliveAfter) { break }
        Start-Sleep -Milliseconds 500
    }
    $reopened.Dispose()
}
Check '无在线快照残留（3 秒后判离线）' (-not $aliveAfter) "band may still hold the mapping"

$crashLog = Join-Path $env:APPDATA 'QuadVitals\agent-crash.log'
Check '无崩溃日志' (-not (Test-Path -LiteralPath $crashLog))

""
if ($fail -eq 0) { "全部通过" } else { "$fail 项失败" }
exit $fail
