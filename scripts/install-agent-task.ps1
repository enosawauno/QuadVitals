# Install the "QuadVitals Agent" logon task: runs the agent with the HIGHEST privileges at logon,
# so LibreHardwareMonitor's sensor driver can load without a UAC prompt every time.
#
# Creating a Highest-privilege task needs admin: this script re-launches itself elevated, so you
# get exactly one UAC prompt. Run it without -Elevated from a normal shell.
param(
    [string]$AgentArguments = '--quiet',
    [string]$TaskName = 'QuadVitals Agent',
    [string]$AgentExe = '',
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $AgentExe) { $AgentExe = Join-Path $root 'build\out\agent\QuadVitals.Agent.exe' }
if (-not (Test-Path -LiteralPath $AgentExe)) { throw "missing $AgentExe (run scripts\build-agent.ps1 or pass -AgentExe)" }
$agentExe = (Resolve-Path -LiteralPath $AgentExe).Path

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not $Elevated -and -not (Test-Elevated)) {
    # Re-launch through cmd so the elevated child's output lands in a log we can read back:
    # Start-Process -Verb RunAs cannot redirect an elevated process' streams.
    $log = Join-Path $root 'build\out\install-agent-task.log'
    New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
    "requesting elevation (one UAC prompt)..."
    $inner = 'chcp 65001 >nul & powershell -NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"' +
             ' -Elevated -AgentArguments "' + $AgentArguments + '" -TaskName "' + $TaskName + '"' +
             ' -AgentExe "' + $AgentExe + '"' +
             ' > "' + $log + '" 2>&1'
    $proc = $null
    try {
        $proc = Start-Process cmd -ArgumentList '/c', $inner -Verb RunAs -PassThru -Wait -WindowStyle Hidden
    } catch {
        # The user declined the UAC prompt. Not an install failure: report it as skipped so callers
        # can continue (install.ps1 turns this into a warning, not an abort).
        Write-Host ''
        Write-Host '  elevation cancelled: the logon task was not created' -ForegroundColor Yellow
        Write-Host '  everything else in the install is unaffected' -ForegroundColor Yellow
        exit 0
    }
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding UTF8 | ForEach-Object { "  | $_" } }
    if ($proc.ExitCode -ne 0) { throw "elevated install failed with exit code $($proc.ExitCode) (see $log)" }
    "elevated install finished"
    exit 0
}

# /RL HIGHEST = highest privileges available, /SC ONLOGON = only after logon.
# /TR takes one string; quote the exe only when its path needs it.
$command = if ($agentExe -match '\s') { '"' + $agentExe + '" ' + $AgentArguments } else { $agentExe + ' ' + $AgentArguments }
"creating task: $TaskName"
"  command: $command"
& schtasks.exe /Create /TN $TaskName /TR $command /SC ONLOGON /RL HIGHEST /F
if ($LASTEXITCODE -ne 0) { throw "schtasks failed with $LASTEXITCODE" }

"task installed"
# Deliberately no `schtasks /Query /FO LIST /V` here: that output is in the console OEM code page and
# turned into boxes in the installer's log pane (which reads UTF-8). Echo what we just created instead -
# it is the same information, in a form that survives any code page.
"  task    = $TaskName"
"  run     = $AgentExe $AgentArguments"
"  trigger = at logon"
"  level   = highest privileges"

# Existence check by exit code only (no text output).
& schtasks.exe /Query /TN $TaskName *> $null
if ($LASTEXITCODE -ne 0) { throw "task $TaskName is not queryable after creation" }
