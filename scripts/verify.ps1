# Full local check: Core assertions + UI/Agent compile + behaviour suites + native build check + DPI probe.
# Runtime agent tests are opt-in because they stop an already-running tray agent.
# ASCII-only: PowerShell 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [switch]$Runtime,
    [int]$SoakSeconds = 0,
    [switch]$SkipUi,
    [switch]$SkipAgent,
    [switch]$SkipNative,
    [switch]$SkipProbe
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$failed = 0
$gateBandName = 'QuadVitalsDeskBandGateCheck'

function Invoke-Step([string]$title, [string]$script, [string[]]$arguments) {
    Write-Host ''
    Write-Host "== $title" -ForegroundColor Cyan
    # Out-Host: the caller discards this function's return value, and a child process's stdout would
    # otherwise be captured into it - the step would run with its whole log thrown away.
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root $script) @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[verify] FAILED: $title (exit $LASTEXITCODE)" -ForegroundColor Red
        $script:failed++
        return $false
    }
    return $true
}

Push-Location $root
try {
    [void](Invoke-Step 'Core build + assertions' 'scripts\build-core.ps1' @())

    if (-not $SkipUi) {
        [void](Invoke-Step 'UI/Settings compile (temp output)' 'scripts\build-ui.ps1' @('-NoRun', '-SkipCore', '-OutDir', 'build\tmp\ui-check'))
        # Behaviour tests: drag / delete / edit-mode rules of the element list. Reuses the build above.
        [void](Invoke-Step 'UI behaviour regression (drag/delete/edit mode)' 'scripts\test-ui-regression.ps1' @('-Reuse', '-OutDir', 'build\tmp\ui-check'))
        # Setup wizard: welcome page copy, finish-page exits, cancel, failure wording. Compiles the
        # installer sources as a DLL and drives the same methods the buttons call.
        [void](Invoke-Step 'Setup wizard behaviour regression' 'scripts\test-setup-regression.ps1' @())
    } else {
        Write-Host '[verify] UI compile skipped (-SkipUi)' -ForegroundColor Yellow
    }

    if (-not $SkipAgent) {
        [void](Invoke-Step 'Agent compile (temp output)' 'scripts\build-agent.ps1' @('-SkipCore', '-OutDir', 'build\tmp\agent-check'))
    } else {
        Write-Host '[verify] Agent compile skipped (-SkipAgent)' -ForegroundColor Yellow
    }

    if (-not $SkipNative) {
        [void](Invoke-Step 'native DeskBand compile check' 'scripts\build-deskband.ps1' @('-Name', $gateBandName))
    } else {
        Write-Host '[verify] native compile skipped (-SkipNative)' -ForegroundColor Yellow
    }

    if (-not $SkipProbe) {
        [void](Invoke-Step 'off-screen DPI render probe' 'scripts\probe-deskband-render.ps1' @())
    } else {
        Write-Host '[verify] render probe skipped (-SkipProbe)' -ForegroundColor Yellow
    }


    if ($Runtime) {
        Write-Host '[verify] runtime tests stop/restart the tray agent' -ForegroundColor Yellow
        [void](Invoke-Step 'Agent lifecycle + settings reload' 'scripts\test-all.ps1' @('-SkipSoak'))
    }

    if ($SoakSeconds -gt 0) {
        Write-Host "[verify] soak will run for $SoakSeconds s" -ForegroundColor Yellow
        [void](Invoke-Step 'stability soak' 'scripts\soak-test.ps1' @('-Seconds', "$SoakSeconds"))
    }
}
finally {
    Pop-Location
    $gateFiles = Join-Path $root ("build\out\deskband\{0}.*" -f $gateBandName)
    Remove-Item -Path $gateFiles -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "[verify] FAILED: $failed step(s)" -ForegroundColor Red
    exit 1
}
Write-Host '[verify] PASS' -ForegroundColor Green
exit 0
