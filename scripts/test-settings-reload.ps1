# Settings hot reload without restarting the agent.
#
# Verifies three things against a live agent:
#   1. refreshMs 1000 -> 500 doubles the published tick rate,
#   2. a bogus networkAdapter turns NetworkValid off,
#   3. restoring "auto" brings network data back with an adapter name.
#
# The original settings.json is backed up and restored in a finally block. The agent is
# stopped at the start and left stopped (test-all/dev.ps1 starts a fresh one).
#
#   powershell -File scripts\test-settings-reload.ps1
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param([int]$StartupSeconds = 10)
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

$settingsPath = [QuadVitals.Core.Settings]::DefaultPath
$backup = $null
if (Test-Path -LiteralPath $settingsPath) {
    $backup = [IO.File]::ReadAllBytes($settingsPath)
}

function Load-CurrentSettings {
    $warnings = New-Object 'System.Collections.Generic.List[string]'
    return [QuadVitals.Core.Settings]::Load($settingsPath, [ref]$warnings)
}

function Measure-TickRate([QuadVitals.Core.MetricsChannel+Reader]$reader, [double]$seconds) {
    $before = $reader.TryRead()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds ([int]($seconds * 1000))
    $after = $reader.TryRead()
    $sw.Stop()
    if (-not $before -or -not $after) { return -1.0 }

    # The seqlock bumps Sequence twice per publish (odd while writing, even when stable),
    # so divide by 2 to get publishes per second.
    $delta = [double]$after.Sequence - [double]$before.Sequence
    if ($delta -lt 0) { return -1.0 }   # mapping was recreated by a new agent
    return ($delta / 2.0) / $sw.Elapsed.TotalSeconds
}

function Wait-ForSnapshot([QuadVitals.Core.MetricsChannel+Reader]$reader, [int]$milliseconds) {
    Start-Sleep -Milliseconds $milliseconds
    return $reader.TryRead()
}

try {
    # The agent may be an elevated instance (logon task); --exit instead of kill.
    & (Join-Path $PSScriptRoot 'stop-agent.ps1')
    Start-Sleep -Milliseconds 800

    "=== 1. start agent"
    $proc = Start-Process -FilePath $exe -PassThru
    $reader = $null
    for ($i = 0; $i -lt $StartupSeconds * 2 -and $null -eq $reader; $i++) {
        Start-Sleep -Milliseconds 500
        $reader = [QuadVitals.Core.MetricsChannel+Reader]::TryOpen()
    }
    Check 'agent started' ($null -ne $reader)
    if ($null -eq $reader) { throw 'no shared-memory channel' }

    # Warm up: a mapping left over from an earlier agent can still hold a high sequence
    # number until the new writer has published a few frames.
    Start-Sleep -Seconds 3
    $reader.TryRead() | Out-Null
    $rate1000 = Measure-TickRate $reader 4
    Check 'baseline ~1 Hz' (($rate1000 -ge 0.75) -and ($rate1000 -le 1.35)) ("{0:N2}/s" -f $rate1000)

    "=== 2. refreshMs 1000 -> 500 (hot reload)"
    $settings = Load-CurrentSettings
    $settings.RefreshMs = 500
    foreach ($item in $settings.Items) { $item.RefreshMs = 500 }
    $settings.Save($settingsPath)
    Start-Sleep -Seconds 2

    $rate500 = Measure-TickRate $reader 4
    Check 'tick rate doubled' (($rate500 -ge 1.5) -and ($rate500 -le 2.6)) ("{0:N2}/s" -f $rate500)
    Check 'rate increased vs baseline' ($rate500 -gt $rate1000 * 1.3) ("{0:N2} -> {1:N2}" -f $rate1000, $rate500)

    "=== 3. bogus adapter -> NetworkValid off"
    $settings = Load-CurrentSettings
    $settings.NetworkAdapter = '__quadvitals_no_such_adapter__'
    $settings.Save($settingsPath)
    $snapshot = Wait-ForSnapshot $reader 3000
    $networkOff = $snapshot -and -not $snapshot.Has([QuadVitals.Core.MetricFlags]::NetworkValid)
    Check 'network invalid with bogus adapter' $networkOff

    "=== 4. back to auto -> network returns"
    $settings = Load-CurrentSettings
    $settings.NetworkAdapter = 'auto'
    $settings.Save($settingsPath)
    $snapshot = Wait-ForSnapshot $reader 3500
    $networkOn = $snapshot -and $snapshot.Has([QuadVitals.Core.MetricFlags]::NetworkValid)
    Check 'network valid after auto' $networkOn
    Check 'adapter name present' ($snapshot -and -not [string]::IsNullOrEmpty($snapshot.AdapterName)) ("'{0}'" -f $(if ($snapshot) { $snapshot.AdapterName } else { '' }))

    $reader.Dispose()
}
finally {
    if ($null -ne $backup) {
        [IO.File]::WriteAllBytes($settingsPath, $backup)
        "restored original settings.json"
    }
    & (Join-Path $PSScriptRoot 'stop-agent.ps1')
}

""
if ($fail -eq 0) { "settings reload passed" } else { "$fail check(s) failed" }
exit $fail
