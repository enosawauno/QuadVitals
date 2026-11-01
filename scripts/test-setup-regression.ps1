# Runs the installer/uninstaller behaviour regression suite.
#
# Why: the wizard's rules were only ever checked by eyeballing --preview screenshots, so a fix came back
# on the next edit (the finish page, the two Explorer dialogs, the window that stayed open).
# tests\QuadVitals.Setup.Tests\setup-regression.ps1 turns each of those rules into an assertion; this
# script compiles the setup sources as a DLL and runs it. Wired into scripts\verify.ps1 (pre-push).
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$OutDir = 'build\tmp\setup-check',
    [switch]$Reuse
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    if (-not $Reuse) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\build-setup-tests.ps1') -OutDir $OutDir
        if ($LASTEXITCODE -ne 0) { throw "setup test build failed ($LASTEXITCODE)" }
    }

    $setupDir = (Resolve-Path (Join-Path $root $OutDir)).Path
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\QuadVitals.Setup.Tests\setup-regression.ps1') -SetupDir $setupDir
    $code = $LASTEXITCODE
    Write-Host ''
    if ($code -eq 0) { Write-Host '[setup-regression] PASS' -ForegroundColor Green }
    else { Write-Host "[setup-regression] FAIL ($code assertion(s))" -ForegroundColor Red }
    exit $code
}
finally {
    Pop-Location
}
