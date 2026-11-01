# S4 verification: start the agent from its logon task (highest privileges), check that
#   - it really keeps running elevated (temperature sensors come alive), and
#   - stopping it makes the published snapshot go stale -> the band would show "--" within 3 s
#.
param([string]$TaskName = 'QuadVitals Agent', [int]$WaitSeconds = 40)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$coreDll = Join-Path $root 'build\out\agent\QuadVitals.Core.dll'
Add-Type -Path $coreDll

function Get-AgentProcess {
    Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
}

& (Join-Path $PSScriptRoot 'stop-agent.ps1')
Start-Sleep -Milliseconds 500

"starting task '$TaskName' ..."
& schtasks.exe /Run /TN $TaskName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "schtasks /Run failed ($LASTEXITCODE): is the task installed?" }

$reader = $null
for ($i = 0; $i -lt $WaitSeconds * 2 -and $null -eq $reader; $i++) {
    Start-Sleep -Milliseconds 500
    $reader = [QuadVitals.Core.MetricsChannel+Reader]::TryOpen()
}
if ($null -eq $reader) { throw 'no shared-memory channel: agent did not start' }

$snapshot = $null
for ($i = 0; $i -lt $WaitSeconds * 2; $i++) {
    Start-Sleep -Milliseconds 500
    $snapshot = $reader.TryRead()
    if ($snapshot -and $snapshot.Has([QuadVitals.Core.MetricFlags]::TemperatureValid)) { break }
}

$proc = Get-AgentProcess
"process   : $(if ($proc) { "pid=$($proc.Id) session=$($proc.SessionId)" } else { 'not running' })"
"snapshot  : $snapshot"
"flags     : $($snapshot.Flags)"

$temperatureOk = $snapshot -and $snapshot.Has([QuadVitals.Core.MetricFlags]::TemperatureValid)
"TEMP      : $(if ($temperatureOk) { '温度可用 -> 提权生效 PASS' } else { '温度仍不可用 -> 提权未生效 FAIL' })"

$alive = $snapshot -and $snapshot.IsAlive([DateTime]::UtcNow, [TimeSpan]::FromSeconds(3))
"heartbeat : $(if ($alive) { '3 秒内视为在线 PASS' } else { '不在线 FAIL' })"

# Stop it and check the staleness rule the band relies on.
"stopping task ..."
& schtasks.exe /End /TN $TaskName | Out-Null
$stopAt = [DateTime]::UtcNow
$wentStaleAfter = $null
while (([DateTime]::UtcNow - $stopAt).TotalSeconds -lt 12) {
    Start-Sleep -Milliseconds 250
    $after = $reader.TryRead()
    $stillAlive = $after -and $after.IsAlive([DateTime]::UtcNow, [TimeSpan]::FromSeconds(3))
    if (-not $stillAlive) { $wentStaleAfter = ([DateTime]::UtcNow - $stopAt).TotalSeconds; break }
}
if ($null -eq $wentStaleAfter) {
    'STALE     : 12 秒内仍未判离线 FAIL'
} else {
    "STALE     : 停止后 {0:N1} 秒判离线 {1}" -f $wentStaleAfter, $(if ($wentStaleAfter -le 3) { 'PASS (<=3s)' } else { 'FAIL (>3s)' })
}

$reader.Dispose()
& (Join-Path $PSScriptRoot 'stop-agent.ps1')
