# Dev launcher: build what changed, (re)start the agent, register the band, open the dashboard.
#
#   powershell -File scripts\dev.ps1              # build + run everything
#   powershell -File scripts\dev.ps1 -NoBuild     # just (re)start the processes
#   powershell -File scripts\dev.ps1 -NoUi        # no dashboard window
#
# The DeskBand DLL cannot be replaced while Explorer has it loaded, so when -BuildDeskBand is
# requested the script refuses politely and tells you the two-step dance instead of failing with
# LNK1104 halfway through.
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [switch]$NoBuild,
    [switch]$NoUi,
    [switch]$BuildDeskBand
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$agentExe = Join-Path $root 'build\out\agent\QuadVitals.Agent.exe'
$uiExe = Join-Path $root 'build\out\ui\QuadVitals.Settings.exe'
$bandDll = Join-Path $root 'build\out\deskband\QuadVitalsDeskBand.dll'

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- stop ----
# The agent and dashboard hold QuadVitals.Core.dll open; stop them before any build copies over it.
Step 'stop running processes'
# The agent may be an elevated instance (logon task): --exit through the helper instead of kill.
& (Join-Path $PSScriptRoot 'stop-agent.ps1')
Get-Process -Name 'QuadVitals.Settings' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

# ---------------------------------------------------------------- build ----
if (-not $NoBuild) {
    Step 'build core + tests'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-core.ps1') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'core/tests failed' }

    Step 'build agent'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-agent.ps1') -SkipCore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'agent build failed' }

    Step 'build dashboard'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-ui.ps1') -NoRun -SkipCore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'ui build failed' }
}

if ($BuildDeskBand) {
    Step 'build deskband'
    if (Get-Process -Name explorer -ErrorAction SilentlyContinue) {
        $loaded = $false
        try {
            foreach ($m in (Get-Process explorer).Modules) {
                if ($m.FileName -eq $bandDll) { $loaded = $true; break }
            }
        }
        catch { }
        if ($loaded) {
            Write-Host '  Explorer has the band DLL loaded - it cannot be rebuilt in place.' -ForegroundColor Yellow
            Write-Host '  Two-step: scripts\uninstall-deskband.ps1  ->  restart Explorer  ->  scripts\build-deskband.ps1  ->  scripts\install-deskband.ps1' -ForegroundColor Yellow
            Write-Host '  (skipping the deskband build)'
        }
        else {
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-deskband.ps1') | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'deskband build failed' }
        }
    }
}

# ---------------------------------------------------------------- band ----
Step 'register deskband (idempotent)'
if (Test-Path -LiteralPath $bandDll) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-deskband.ps1') -NoRefresh | Out-Host
}
else {
    Write-Host '  no band DLL yet - run scripts\build-deskband.ps1 once' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- processes ----
Step 'start agent'
if (Test-Path -LiteralPath $agentExe) {
    Start-Process -FilePath $agentExe
    Start-Sleep -Seconds 3
    $agent = Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($agent) { Write-Host ("  agent up (pid {0})" -f $agent.Id) -ForegroundColor Green }
    else { Write-Host '  agent did NOT start (check %APPDATA%\QuadVitals\agent-crash.log)' -ForegroundColor Red }
}
else {
    Write-Host "  missing $agentExe" -ForegroundColor Red
}

if (-not $NoUi) {
    Step 'open dashboard'
    if (Test-Path -LiteralPath $uiExe) {
        Start-Process -FilePath $uiExe
        Start-Sleep -Seconds 2
    }
    else {
        Write-Host "  missing $uiExe" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------- status ----
Step 'status'
try {
    & $agentExe --status | ForEach-Object { "  $_" }
}
catch {
    Write-Host '  --status failed' -ForegroundColor Yellow
}

Write-Host ''
Get-Process -Name 'QuadVitals*' -ErrorAction SilentlyContinue |
    Select-Object Name, Id, @{ n = 'mem(MB)'; e = { [int]($_.WorkingSet64 / 1MB) } } |
    Format-Table -AutoSize | Out-Host

Write-Host 'tip: the band lives in  right-click taskbar -> Toolbars -> QuadVitals' -ForegroundColor DarkGray
