# Remove the QuadVitals Agent logon task (and stop it if it is running).
param([string]$TaskName = 'QuadVitals Agent', [switch]$Elevated)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot

# Nothing to do (and no reason to prompt for UAC) when the task was never installed.
& schtasks.exe /Query /TN $TaskName *> $null
if ($LASTEXITCODE -ne 0) {
    "task not present: $TaskName"
    exit 0
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not $Elevated -and -not (Test-Elevated)) {
    $log = Join-Path $root 'build\out\uninstall-agent-task.log'
    New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
    $inner = 'chcp 65001 >nul & powershell -NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"' +
             ' -Elevated -TaskName "' + $TaskName + '" > "' + $log + '" 2>&1'
    $proc = Start-Process cmd -ArgumentList '/c', $inner -Verb RunAs -PassThru -Wait -WindowStyle Hidden
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding UTF8 | ForEach-Object { "  | $_" } }
    exit $proc.ExitCode
}

& schtasks.exe /End /TN $TaskName 2>$null | Out-Null
& schtasks.exe /Delete /TN $TaskName /F
if ($LASTEXITCODE -eq 0) { "task removed: $TaskName" } else { "task not present: $TaskName (exit $LASTEXITCODE)" }
& (Join-Path $PSScriptRoot 'stop-agent.ps1')
