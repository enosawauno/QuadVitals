# Isolated LHM fixture: no driver, process replacement, or production metrics channel is used.
param(
    [string]$CoreDll = '',
    [string]$CompilerPath = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $CoreDll) { $CoreDll = Join-Path $root 'build\out\core\QuadVitals.Core.dll' }
$outDir = Join-Path $root 'build\tmp\temperature-recovery'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$fixture = Join-Path $outDir 'LibreHardwareMonitorLib.dll'
$exe = Join-Path $outDir 'TemperatureRecovery.Tests.exe'
$testDir = Join-Path $root 'tests\QuadVitals.Agent.Tests'
& $CompilerPath /nologo /target:library /langversion:7.3 /codepage:65001 /out:"$fixture" (Join-Path $testDir 'FakeHardware.cs')
if ($LASTEXITCODE -ne 0) { throw 'LHM fixture build failed' }
$sources = @('TemperatureSource.cs', 'TemperatureWorker.cs', 'TemperatureLog.cs', 'AgentRestarter.cs', 'CpuPowerLimitSource.cs') |
    ForEach-Object { Join-Path $root "src\QuadVitals.Agent\$_" }
& $CompilerPath /nologo /target:exe /langversion:7.3 /codepage:65001 /out:"$exe" /r:"$CoreDll" /r:"$fixture" `
    $sources (Join-Path $testDir 'TemperatureRecoveryTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'temperature recovery test build failed' }
Copy-Item -LiteralPath $CoreDll -Destination (Join-Path $outDir 'QuadVitals.Core.dll') -Force
$previousAppData = $env:QUADVITALS_APPDATA
try {
    $env:QUADVITALS_APPDATA = Join-Path $outDir ('appdata-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $env:QUADVITALS_APPDATA -Force | Out-Null
    $mainOutput = & $exe
    if ($LASTEXITCODE -ne 0) { throw 'temperature recovery tests failed' }
    $mainOutput | Write-Output
    $missingDir = Join-Path $outDir 'missing-library'
    New-Item -ItemType Directory -Path $missingDir -Force | Out-Null
    Copy-Item -LiteralPath $exe, $CoreDll -Destination $missingDir -Force
    $missingOutput = & (Join-Path $missingDir 'TemperatureRecovery.Tests.exe') --missing-library
    if ($LASTEXITCODE -ne 0) { throw 'missing library tests failed' }
    $missingOutput | Write-Output
    $totals = [regex]::Matches((($mainOutput + $missingOutput) -join "`n"), 'temperature recovery: (\d+) passed / 0 failed')
    if ($totals.Count -ne 2) { throw 'missing assertion totals' }
    $count = [int]$totals[0].Groups[1].Value + [int]$totals[1].Groups[1].Value
    [IO.File]::WriteAllText((Join-Path $root 'build\tmp\assertion-count-temperature-recovery.txt'), [string]$count)
}
finally {
    $env:QUADVITALS_APPDATA = $previousAppData
}
