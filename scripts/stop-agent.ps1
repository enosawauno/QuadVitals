# Stop the running Agent without needing elevation.
#
# Why: the delivery form is the logon task, which starts the agent with highest privileges, and a
# normal shell cannot Stop-Process an elevated process ("Access is denied"). The agent already has
# a graceful exit channel (--exit signals a named event and the process cleans up after itself), so
# try that first and only fall back to Stop-Process when the channel does not answer.
#
#   powershell -File scripts\stop-agent.ps1
#   powershell -File scripts\stop-agent.ps1 -Exe '<install dir>\QuadVitals.Agent.exe'
#   powershell -File scripts\stop-agent.ps1 -TimeoutSeconds 10
#
# Exit code 0 always: callers keep their own "did it die" checks.
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$Exe = '',
    [int]$TimeoutSeconds = 6
)
$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'build\out\agent\QuadVitals.Agent.exe' }

function Get-AgentProcess {
    Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
}

if (-not (Get-AgentProcess)) { return }

# 1. graceful first: an elevated instance can only be asked, not killed
if (Test-Path -LiteralPath $Exe) {
    try { & $Exe --exit 2>$null | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        if (-not (Get-AgentProcess)) { return }
    }
}

# 2. fallback for a process this shell is allowed to terminate (non-elevated agent)
$left = Get-AgentProcess
if ($left) {
    try {
        $left | Stop-Process -Force -ErrorAction Stop
        Start-Sleep -Milliseconds 500
    }
    catch {
        "stop-agent: pid $($left.Id) still running and cannot be killed from this shell" +
        " (elevated instance) - it ignored --exit; stop it from an elevated prompt"
    }
}
return
