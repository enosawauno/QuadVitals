# Build QuadVitals.Agent (console probe + publisher) against QuadVitals.Core.
# Roslyn csc only: no .NET SDK is required.
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param([switch]$SkipCore, [string]$OutDir = '')
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc not found: $csc" }

if (-not $SkipCore) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-core.ps1') -NoRun | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "core build failed" }
    "core rebuilt"
}

$coreDll = Join-Path $root 'build\out\core\QuadVitals.Core.dll'
if (-not (Test-Path -LiteralPath $coreDll)) { throw "missing $coreDll" }

$outDir = if ($OutDir) { if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir } } else { Join-Path $root 'build\out\agent' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Copy-Item -LiteralPath $coreDll -Destination (Join-Path $outDir 'QuadVitals.Core.dll') -Force

$exe = Join-Path $outDir 'QuadVitals.Agent.exe'
$sources = Get-ChildItem -Path (Join-Path $root 'src\QuadVitals.Agent') -Filter '*.cs' |
           ForEach-Object { $_.FullName }

# WinExe: a tray agent must not flash a console window. Probe/script modes attach the parent
# console themselves (Program.AttachParentConsole).
# The tray icon is a separate, tight-cropped variant (see scripts\make-app-icon.ps1 -Fill 1.25).
$trayIcon = Join-Path $root 'assets\icon\tray.ico'
if (Test-Path -LiteralPath $trayIcon) {
    Copy-Item -LiteralPath $trayIcon -Destination (Join-Path $outDir 'tray.ico') -Force
    "tray.ico deployed next to the exe"
}

# exe icon: full-bleed app.ico (the source art's macOS padding makes it look small at
# 16/32 px taskbar and Alt-Tab sizes). See scripts\make-app-icon.ps1.
$icon = Join-Path $root 'assets\icon\app.ico'
$iconArg = if (Test-Path -LiteralPath $icon) { '/win32icon:' + $icon } else { $null }

& $csc /nologo /target:winexe /langversion:7.3 /codepage:65001 /platform:anycpu /optimize+ /debug- `
    /out:"$exe" `
    /r:"$coreDll" `
    /r:System.Net.NetworkInformation.dll `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    $iconArg `
    $sources `
    (Join-Path $root 'src\SharedAssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw "agent build failed ($LASTEXITCODE)" }

"agent -> $exe"

# LHM is optional at runtime (temperature degrades); copy it in when the dependency DLLs exist.
# They are not source: drop LibreHardwareMonitorLib 0.9.2 and HidSharp 2.1.0 into lib/, or point
# QUADVITALS_LIB at wherever they live.
$libDir = if ($env:QUADVITALS_LIB) { $env:QUADVITALS_LIB } else { Join-Path $root 'lib' }
$lhm = Join-Path $libDir 'LibreHardwareMonitorLib.dll'
$hid = Join-Path $libDir 'HidSharp.dll'
if (Test-Path -LiteralPath $lhm) {
    Copy-Item -LiteralPath $lhm -Destination $outDir -Force
    if (Test-Path -LiteralPath $hid) { Copy-Item -LiteralPath $hid -Destination $outDir -Force }
    "LHM copied (temperature available when the agent runs elevated)"
} else {
    "LHM not found in $libDir - temperature will report '--' (expected on a clean checkout)"
}

# Licence files travel with the binaries: MPL 2.0 / Apache 2.0 require the notices to be
# redistributed alongside the third-party DLLs, so they must not be left behind in the repo.
foreach ($doc in @('LICENSE', 'LICENSE.zh.md', 'THIRD-PARTY-NOTICES.txt')) {
    $src = Join-Path $root $doc
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $outDir $doc) -Force
    }
}
