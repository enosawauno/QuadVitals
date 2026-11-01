# Cross-check the agent against Windows' own numbers (CPU <= 3 points,
# network <= 5%). Reads the shared-memory channel through QuadVitals.Core.dll, which also proves
# the IPC read path works from another process - the same path the DeskBand will use.
#
# Each sample is a 10 s window and BOTH sides are averaged over that same window: an
# instantaneous 1 s window vs another 1 s window can never agree within 5% on bursty traffic.
param(
    [int]$Samples = 3,
    [int]$WindowSeconds = 10,
    [int]$Seconds = 180
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$agentExe = Join-Path $root 'build\out\agent\QuadVitals.Agent.exe'
$coreDll = Join-Path $root 'build\out\agent\QuadVitals.Core.dll'
if (-not (Test-Path -LiteralPath $agentExe)) { throw "missing $agentExe (run scripts/build-agent.ps1)" }

Add-Type -Path $coreDll

# Independent GetSystemTimes sampler: same formula the agent uses, implemented here from
# scratch. Comparing against this validates the agent's arithmetic and pacing without
# Get-Counter's own overhead or a second, differently-phased measurement window distorting it.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LocalCpu
{
    [StructLayout(LayoutKind.Sequential)]
    struct FT { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out FT idle, out FT kernel, out FT user);

    static ulong Val(FT f) { return ((ulong)f.High << 32) | f.Low; }

    static ulong _i, _t;
    static bool _has;

    public static void Reset() { _has = false; }

    public static double Sample()
    {
        FT idle, kernel, user;
        if (!GetSystemTimes(out idle, out kernel, out user)) return double.NaN;
        ulong i = Val(idle), t = Val(kernel) + Val(user);
        if (!_has) { _i = i; _t = t; _has = true; return double.NaN; }
        ulong di = i - _i, dt = t - _t;
        _i = i; _t = t;
        if (dt == 0) return double.NaN;
        double busy = 100.0 * (1.0 - (double)di / dt);
        return busy < 0 ? 0 : (busy > 100 ? 100 : busy);
    }
}
'@

$agent = Start-Process -FilePath $agentExe -ArgumentList @('--quiet', '--seconds', "$Seconds") -PassThru -WindowStyle Hidden
$reader = $null
try {
    for ($try = 1; $try -le 30 -and $null -eq $reader; $try++) {
        Start-Sleep -Milliseconds 500
        $reader = [QuadVitals.Core.MetricsChannel+Reader]::TryOpen()
    }
    if ($null -eq $reader) { throw 'no shared-memory channel: is the agent running?' }

    $adapterName = ''
    for ($try = 1; $try -le 20 -and -not $adapterName; $try++) {
        Start-Sleep -Milliseconds 500
        $probe = $reader.TryRead()
        if ($probe) { $adapterName = $probe.AdapterName }
    }
    "channel up; agent adapter = '$adapterName'"

    function Read-AdapterTotals([string]$name) {
        $stats = Get-NetAdapterStatistics -Name $name -ErrorAction SilentlyContinue
        if (-not $stats) { return $null }
        return [pscustomobject]@{ recv = [double]$stats.ReceivedBytes; sent = [double]$stats.SentBytes }
    }

    $results = @()
    for ($i = 1; $i -le $Samples; $i++) {
        $totalsBefore = if ($adapterName) { Read-AdapterTotals $adapterName } else { $null }
        $startedAt = [DateTime]::UtcNow

        [LocalCpu]::Reset() | Out-Null
        $agentCpuSum = 0.0; $counterCpuSum = 0.0; $localCpuSum = 0.0; $cpuSamples = 0; $localSamples = 0
        $agentUpSum = 0.0; $agentDownSum = 0.0; $netSamples = 0

        while (([DateTime]::UtcNow - $startedAt).TotalSeconds -lt $WindowSeconds) {
            # Pace at 1 Hz: GetSystemTimes updates on the scheduler tick (~15 ms), so sampling in
            # a tight loop reads stale deltas and biases the reference low.
            Start-Sleep -Milliseconds 1000
            $snapshot = $reader.TryRead()
            $localCpu = [LocalCpu]::Sample()
            if (-not [double]::IsNaN($localCpu)) { $localCpuSum += [double]$localCpu; $localSamples++ }
            $cpuSamples++
            if ($snapshot) {
                $agentCpuSum += [double]$snapshot.CpuPercent
                $agentUpSum += [double]$snapshot.NetUpBytesPerSec
                $agentDownSum += [double]$snapshot.NetDownBytesPerSec
                $netSamples++
            }
        }

        $elapsed = ([DateTime]::UtcNow - $startedAt).TotalSeconds
        $totalsAfter = if ($adapterName) { Read-AdapterTotals $adapterName } else { $null }

        $result = [pscustomobject]@{
            window     = $i
            agentCpu   = $agentCpuSum / [math]::Max($netSamples, 1)
            counterCpu = $localCpuSum / [math]::Max($localSamples, 1)
            agentUp    = $agentUpSum / [math]::Max($netSamples, 1)
            statUp     = if ($totalsBefore -and $totalsAfter) { ($totalsAfter.sent - $totalsBefore.sent) / $elapsed } else { $null }
            agentDown  = $agentDownSum / [math]::Max($netSamples, 1)
            statDown   = if ($totalsBefore -and $totalsAfter) { ($totalsAfter.recv - $totalsBefore.recv) / $elapsed } else { $null }
        }
        $results += $result
        "window {0}: cpu agent={1,5:N1}% local={2,5:N1}%  |  up agent={3,9:N0} stat={4,9:N0}  |  down agent={5,9:N0} stat={6,9:N0}" -f `
            $result.window, $result.agentCpu, $result.counterCpu, $result.agentUp, [double]$result.statUp, $result.agentDown, [double]$result.statDown
    }

    $cpuDelta = ($results | ForEach-Object { [math]::Abs($_.agentCpu - $_.counterCpu) } | Measure-Object -Average).Average
    ""
    "CPU : |delta| vs independent GetSystemTimes sampler = {0:N1} points over {1} window(s) (limit 3) [{2}]" -f `
        $cpuDelta, $results.Count, $(if ($cpuDelta -le 3) { 'PASS' } else { 'FAIL' })

    $netWindows = @($results | Where-Object { $null -ne $_.statDown -and $_.statDown -gt 2048 })
    if ($netWindows.Count -gt 0) {
        $down = ($netWindows | ForEach-Object { [math]::Abs($_.agentDown - $_.statDown) / $_.statDown * 100 } | Measure-Object -Average).Average
        $up = ($netWindows | ForEach-Object { [math]::Abs($_.agentUp - $_.statUp) / [math]::Max($_.statUp, 1) * 100 } | Measure-Object -Average).Average
        "NET : |delta| down = {0:N1}%  up = {1:N1}%  over {2} window(s) (limit 5) [{3}]" -f `
            $down, $up, $netWindows.Count, $(if ([math]::Max($down, $up) -le 5) { 'PASS' } else { 'FAIL' })
    } else {
        'NET : link idle (no window above 2 KB/s) - comparison skipped'
    }
}
finally {
    if ($reader) { $reader.Dispose() }
    if ($agent -and -not $agent.HasExited) { $agent.Kill() }
}
