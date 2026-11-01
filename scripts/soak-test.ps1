# Stability / performance soak (performance budget).
#
# Samples the agent's own CPU and memory, the shared-memory tick rate and snapshot age for
# -Seconds, and fails if the agent dies, the snapshot goes stale or the taskbar's Explorer
# process restarts underneath it.
#
#   powershell -File scripts\soak-test.ps1 -Seconds 60
#   powershell -File scripts\soak-test.ps1 -Seconds 7200          # the 2-hour acceptance run
#   powershell -File scripts\soak-test.ps1 -RestartExplorer       # also tests band recovery
#
# -RestartExplorer restarts Explorer; the toolbar entry may need re-checking,
# so the script reports the real result instead of pretending.
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [int]$Seconds = 300,
    [int]$SampleSeconds = 5,
    [double]$MaxAgentCpuPercent = 1.0,
    [int]$MaxAgentMemoryMb = 100,
    [switch]$RestartExplorer,
    [switch]$KeepAgent
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$agentExe = Join-Path $root 'build\out\agent\QuadVitals.Agent.exe'
$coreDll = Join-Path $root 'build\out\agent\QuadVitals.Core.dll'
if (-not (Test-Path -LiteralPath $agentExe)) { throw "missing $agentExe (run scripts\build-agent.ps1)" }

Add-Type -Path $coreDll

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class QuadVitalsWindows
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // The band is a child of the taskbar's ReBarWindow32.
    public static bool BandWindowExists(string className)
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        IntPtr rebar = FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar == IntPtr.Zero) return false;
        return FindWindowEx(rebar, IntPtr.Zero, className, null) != IntPtr.Zero;
    }

    // PID of the process that owns the taskbar. Do not compare all explorer.exe PIDs: Win10
    // can run a separate process per folder window, so opening a folder would look like a
    // shell restart.
    public static uint ShellProcessId()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return 0;
        uint pid;
        GetWindowThreadProcessId(tray, out pid);
        return pid;
    }
}
'@

function Get-AgentProcess {
    Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
}

$startedByUs = $false
$agent = Get-AgentProcess
if (-not $agent) {
    "starting agent ..."
    Start-Process -FilePath $agentExe
    $startedByUs = $true
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        $agent = Get-AgentProcess
        if ($agent) { break }
    }
}
if (-not $agent) { throw 'agent did not start (see %APPDATA%\QuadVitals\agent-crash.log)' }

$reader = $null
for ($i = 0; $i -lt 20 -and $null -eq $reader; $i++) {
    Start-Sleep -Milliseconds 500
    $reader = [QuadVitals.Core.MetricsChannel+Reader]::TryOpen()
}
if ($null -eq $reader) { throw 'shared-memory channel not available' }

# A cold start includes LibreHardwareMonitor driver initialisation, which can take seconds.
# Measuring before the first real frame would report a fake half-rate tick, so wait for a
# snapshot that is actually alive (a leftover mapping in the band is not).
$first = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $candidate = $reader.TryRead()
    if ($candidate -and $candidate.IsAlive([DateTime]::UtcNow, [TimeSpan]::FromSeconds(3))) {
        $first = $candidate
        break
    }
}
if ($null -eq $first) { throw 'agent did not publish a fresh snapshot within 30 s' }

# Expected tick rate comes from settings.json (hot reload means it can change under us; the
# tolerance below is deliberately wide enough for a 500 ms/1 s/2 s configuration).
$warnings = New-Object 'System.Collections.Generic.List[string]'
$settings = [QuadVitals.Core.Settings]::Load([QuadVitals.Core.Settings]::DefaultPath, [ref]$warnings)
$expectedPeriodMs = if ($settings.RefreshMs -ge 200) { $settings.RefreshMs } else { 1000 }

$startProcess = Get-AgentProcess
if (-not $startProcess) { throw 'agent exited before the soak started' }
$cpuStart = $startProcess.TotalProcessorTime
$memStart = $startProcess.WorkingSet64
$memPeak = $memStart
$shellPid = [QuadVitalsWindows]::ShellProcessId()

$firstSeq = [double]$first.Sequence
$lastSeq = $firstSeq
$maxAgeMs = 0
$staleSamples = 0
$processDied = $false
$explorerRestarted = $false
$samples = 0

$sw = [Diagnostics.Stopwatch]::StartNew()
"soaking for $Seconds s (sample every $SampleSeconds s, expected period ${expectedPeriodMs} ms) ..."
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    Start-Sleep -Seconds $SampleSeconds
    $samples++

    $now = [DateTime]::UtcNow
    $current = Get-AgentProcess
    if (-not $current) { $processDied = $true; break }

    $snapshot = $reader.TryRead()
    if ($snapshot) {
        $ageMs = [int]$snapshot.Age($now).TotalMilliseconds
        if ($ageMs -gt $maxAgeMs) { $maxAgeMs = $ageMs }
        if ($ageMs -gt 3000) { $staleSamples++ }
        if ($snapshot.Sequence -gt 0) {
            $lastSeq = $snapshot.Sequence
            $lastTime = $now
        }
    }

    $mem = $current.WorkingSet64
    if ($mem -gt $memPeak) { $memPeak = $mem }

    $currentShellPid = [QuadVitalsWindows]::ShellProcessId()
    if ($currentShellPid -ne $shellPid) { $explorerRestarted = $true; $shellPid = $currentShellPid }
}
$sw.Stop()

$endProcess = Get-AgentProcess
$cpuEnd = if ($endProcess) { $endProcess.TotalProcessorTime } else { $cpuStart }
$memEnd = if ($endProcess) { $endProcess.WorkingSet64 } else { $memStart }

$elapsed = [Math]::Max(1.0, $sw.Elapsed.TotalSeconds)
$cpuSeconds = ($cpuEnd - $cpuStart).TotalSeconds
$cpuPercent = 100.0 * $cpuSeconds / $elapsed / [Environment]::ProcessorCount
$seqDelta = [Math]::Max(0, [double]$lastSeq - [double]$firstSeq)
# Sequence advances twice per publish (seqlock: odd while writing, even when stable).
$tickRate = ($seqDelta / 2.0) / $elapsed
$expectedRate = 1000.0 / $expectedPeriodMs

$fail = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { "  PASS  $what $detail" }
    else { "  FAIL  $what $detail"; $script:fail++ }
}

""
"duration      : {0:N1} s / {1} samples" -f $elapsed, $samples
"agent CPU     : avg {0:N3} % (limit {1:N2} %)" -f $cpuPercent, $MaxAgentCpuPercent
"agent memory  : start {0:N1} MB, end {1:N1} MB, peak {2:N1} MB (limit {3} MB)" -f `
    ($memStart / 1MB), ($memEnd / 1MB), ($memPeak / 1MB), $MaxAgentMemoryMb
"tick rate     : {0:N2} /s (expected ~{1:N2} /s)" -f $tickRate, $expectedRate
"snapshot age  : max {0} ms, stale samples {1}" -f $maxAgeMs, $staleSamples
""
Check 'agent stayed alive' (-not $processDied)
Check 'no explorer restart' (-not $explorerRestarted)
Check 'snapshot never stale (>3 s)' ($staleSamples -eq 0) "stale=$staleSamples"
Check 'agent CPU under limit' ($cpuPercent -le $MaxAgentCpuPercent) ("{0:N3}%" -f $cpuPercent)
Check 'agent memory under limit' (($memPeak / 1MB) -le $MaxAgentMemoryMb) ("{0:N1} MB" -f ($memPeak / 1MB))
Check 'tick rate within 30% of settings' `
    (($tickRate -ge $expectedRate * 0.7) -and ($tickRate -le $expectedRate * 1.3)) `
    ("{0:N2} vs {1:N2}" -f $tickRate, $expectedRate)

if ($RestartExplorer) {
    ""
    "restarting Explorer to test band recovery ..."
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Start-Process explorer.exe
    Start-Sleep -Seconds 10

    $bandUp = [QuadVitalsWindows]::BandWindowExists('QuadVitalsBandWindow')
    Check 'band window after Explorer restart' $bandUp 'class QuadVitalsBandWindow (re-check Toolbars if false)'
}

$reader.Dispose()
if (-not $KeepAgent -and $startedByUs) {
    Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force
}

""
if ($fail -eq 0) { "soak passed" } else { "$fail check(s) failed" }
exit $fail
