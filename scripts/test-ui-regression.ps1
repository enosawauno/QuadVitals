# Runs the WPF UI behaviour regression suite against the freshly compiled Settings build.
#
# Why this exists: drag/delete/edit-mode rules in the element list kept regressing because the
# only checks were throwaway probes under build/tmp (gitignored, never run by any check). This script
# is wired into scripts\verify.ps1 (and therefore pre-push) right after the UI compile step.
#
# ASCII only: PowerShell 5.1 reads BOM-less UTF-8 as GBK.
param(
    [string]$OutDir = 'build\tmp\ui-check',
    [switch]$Reuse
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    if (-not $Reuse) {
        Write-Host '[ui-regression] compiling Settings to ' $OutDir
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\build-ui.ps1') `
            -NoRun -SkipCore -OutDir $OutDir
        if ($LASTEXITCODE -ne 0) { throw "UI build failed ($LASTEXITCODE)" }
    }

    $uiDir = (Resolve-Path (Join-Path $root $OutDir)).Path
    # Four suites, four processes: the element-list rules, the general settings sheet, the device
    # status dialog / tile formats, and the overview dynamics (fan card / diagnostics / frame clock).
    # Separate processes because the overlay panel kept answering "not found" after a few hundred
    # windows, and because one file per surface is what keeps each suite inside the size limit.
    $suites = @('ui-regression.ps1', 'general-settings.ps1', 'device-dialog.ps1', 'overview-dynamics.ps1')
    $codes = @()
    foreach ($name in $suites) {
        $suite = Join-Path $root ('tests\QuadVitals.Settings.Tests\' + $name)
        Write-Host ''
        Write-Host ('[ui-regression] ' + $name) -ForegroundColor Cyan
        & powershell -NoProfile -ExecutionPolicy Bypass -File $suite -UiDir $uiDir
        $codes += $LASTEXITCODE
    }

    # The baseline check wants one number for the whole run: sum the per-suite counts.
    $passed = 0
    foreach ($part in @('settings-ui-elements', 'settings-ui-general', 'settings-ui-device', 'settings-ui-overview-dynamics')) {
        $partFile = Join-Path $root ('build\tmp\assertion-count-' + $part + '.txt')
        if (Test-Path -LiteralPath $partFile) { $passed += [int]([IO.File]::ReadAllText($partFile).Trim()) }
    }
    $countFile = Join-Path $root 'build\tmp\assertion-count-settings-ui.txt'
    $countDir = Split-Path -Parent $countFile
    if (-not (Test-Path -LiteralPath $countDir)) { New-Item -ItemType Directory -Path $countDir -Force | Out-Null }
    [IO.File]::WriteAllText($countFile, [string]$passed)

    $code = ($codes | Measure-Object -Sum).Sum
    Write-Host ''
    Write-Host ('' + $passed + ' assertions passed') -ForegroundColor Cyan
    if ($code -eq 0) { Write-Host '[ui-regression] PASS' -ForegroundColor Green }
    else { Write-Host "[ui-regression] FAIL ($code assertion(s))" -ForegroundColor Red }
    exit $code
}
finally {
    Pop-Location
}
