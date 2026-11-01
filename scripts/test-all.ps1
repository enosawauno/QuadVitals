# One-shot regression: Core assertions, agent lifecycle, settings hot reload, short soak.
#
#   powershell -File scripts\test-all.ps1
#   powershell -File scripts\test-all.ps1 -SoakSeconds 7200    # full acceptance soak
#
# Stops the agent while running (the tests need a clean channel) and leaves it stopped;
# run scripts\dev.ps1 afterwards to bring the tray agent back.
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [int]$SoakSeconds = 60,
    [switch]$SkipSoak
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Run([string]$script, [string[]]$arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments
    if ($LASTEXITCODE -ne 0) { throw "$script failed ($LASTEXITCODE)" }
}

Step 'build + unit tests'
Run 'build-core.ps1' @()

Step 'agent lifecycle (tray / single instance / --status / --exit)'
Run 'test-agent-app.ps1' @()

Step 'settings hot reload'
Run 'test-settings-reload.ps1' @()

if (-not $SkipSoak) {
    Step "soak ($SoakSeconds s)"
    Run 'soak-test.ps1' @('-Seconds', "$SoakSeconds")
}

Write-Host ''
Write-Host 'all checks passed' -ForegroundColor Green
Write-Host 'run scripts\dev.ps1 to rebuild and start the tray agent again'
