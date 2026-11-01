# Stop every QuadVitals process and prove they are gone.
#
# Why this is a script and not code in the installer window: ending a process needs the same rights the
# install has. The window runs per-user (asInvoker) so it can never end the agent, which starts from a
# highest-privilege logon task. Every privileged step therefore lives here, next to the copy/register
# work, and the window only shows pages and starts scripts.
#
# Output contract (parsed by the window, keep it stable):
#   QV-STOP-OK                       every process is gone, exit 0
#   QV-STOP-FAILED: <name> (pid <n>) one line per process that survived, exit 1
#
#   powershell -File scripts\stop-quadvitals.ps1
#   powershell -File scripts\stop-quadvitals.ps1 -InstallDir 'D:\Apps\QuadVitals'
#   powershell -File scripts\stop-quadvitals.ps1 -Names 'QuadVitals.Agent'   # diagnostics
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'QuadVitals'),
    [string[]]$Names = @('QuadVitals.Agent', 'QuadVitals.Settings'),
    [int]$Rounds = 3
)
$ErrorActionPreference = 'Continue'

# Graceful first: the agent owns the shared-memory channel and a sensor driver, and --exit lets it clean
# both up. Forcing it is only the fallback.
$installedAgent = Join-Path $InstallDir 'QuadVitals.Agent.exe'
if (Test-Path -LiteralPath $installedAgent) {
    & (Join-Path $PSScriptRoot 'stop-agent.ps1') -Exe $installedAgent
} else {
    & (Join-Path $PSScriptRoot 'stop-agent.ps1')
}

for ($round = 1; $round -le $Rounds; $round++) {
    $alive = @(Get-Process -Name $Names -ErrorAction SilentlyContinue)
    if ($alive.Count -eq 0) { break }

    foreach ($process in $alive) {
        Write-Host "  stopping $($process.ProcessName) (pid $($process.Id))"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 600
}

Start-Sleep -Milliseconds 400

$left = @(Get-Process -Name $Names -ErrorAction SilentlyContinue)
if ($left.Count -gt 0) {
    foreach ($process in $left) {
        "QV-STOP-FAILED: $($process.ProcessName) (pid $($process.Id))"
    }
    Write-Host '  a process could not be stopped; nothing was changed'
    exit 1
}

'QV-STOP-OK'
exit 0
