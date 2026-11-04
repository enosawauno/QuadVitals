# QuadVitals release package (0.9.0): stages the exact file set install.ps1 copies and zips it.
#
# The stage deliberately mirrors the repo layout (scripts\ + build\out\<flavour>\). install.ps1
# resolves $root from its own script directory and reads build\out\agent|ui|deskband plus the
# licence files at $root, so shipping those folders unchanged means the delivered installer is
# byte-identical to the one tested in the repo - no second code path to keep in sync.
#
#   powershell -File scripts\make-release.ps1            # package current build outputs
#   powershell -File scripts\make-release.ps1 -Build     # rebuild agent/ui/band first
#
# Version comes from src\QuadVitals.Core\ProductVersion.cs (single source of truth).
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [switch]$Build,
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build\out'
$agentOut = Join-Path $out 'agent'
$uiOut = Join-Path $out 'ui'
$bandOut = Join-Path $out 'deskband'

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- version ----
Step 'read product version'
$versionFile = Join-Path $root 'src\QuadVitals.Core\ProductVersion.cs'
$hit = Select-String -LiteralPath $versionFile -Pattern 'SemVer\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"'
if (-not $hit) { throw "cannot read SemVer from $versionFile" }
$version = $hit.Matches[0].Groups[1].Value
Write-Host "  version: $version"

# ---------------------------------------------------------------- build ----
if ($Build) {
    Step 'build agent'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-agent.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build-agent.ps1 failed ($LASTEXITCODE)" }

    Step 'build ui'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-ui.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build-ui.ps1 failed ($LASTEXITCODE)" }

    Step 'build deskband'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-deskband.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build-deskband.ps1 failed ($LASTEXITCODE)" }
}

# A -DevTools dashboard can be told to stop recovering on its own; shipping that switch would let a
# stray environment variable disable auto recovery on a user's machine.
$devToolsMarker = Join-Path $out 'ui\.devtools'
if (Test-Path -LiteralPath $devToolsMarker) {
    throw 'build\out\ui is a -DevTools build (recording switch compiled in): rebuild with scripts\build-ui.ps1 first'
}

# ---------------------------------------------------------------- stage ----
Step 'stage package tree'
$releaseRoot = if ($OutDir) { $OutDir } else { Join-Path $out 'release' }
$stage = Join-Path $releaseRoot "QuadVitals-$version"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Paths are relative to the stage root and must stay in the folders install.ps1 reads.
$payload = @(
    @{ Src = (Join-Path $agentOut 'QuadVitals.Agent.exe');           Dst = 'build\out\agent\QuadVitals.Agent.exe' },
    @{ Src = (Join-Path $agentOut 'QuadVitals.Core.dll');            Dst = 'build\out\agent\QuadVitals.Core.dll' },
    @{ Src = (Join-Path $agentOut 'tray.ico');                       Dst = 'build\out\agent\tray.ico' },
    @{ Src = (Join-Path $uiOut 'QuadVitals.Settings.exe');           Dst = 'build\out\ui\QuadVitals.Settings.exe' },
    @{ Src = (Join-Path $uiOut 'QuadVitals.UI.dll');                 Dst = 'build\out\ui\QuadVitals.UI.dll' },
    @{ Src = (Join-Path $bandOut 'QuadVitalsDeskBand.dll');          Dst = 'build\out\deskband\QuadVitalsDeskBand.dll' },
    @{ Src = (Join-Path $agentOut 'LibreHardwareMonitorLib.dll');    Dst = 'build\out\agent\LibreHardwareMonitorLib.dll' },
    @{ Src = (Join-Path $agentOut 'HidSharp.dll');                   Dst = 'build\out\agent\HidSharp.dll' },
    @{ Src = (Join-Path $root 'LICENSE');                            Dst = 'LICENSE' },
    @{ Src = (Join-Path $root 'LICENSE.zh.md');                      Dst = 'LICENSE.zh.md' },
    @{ Src = (Join-Path $root 'THIRD-PARTY-NOTICES.txt');            Dst = 'THIRD-PARTY-NOTICES.txt' }
)

# install.ps1 calls these; TrayDeskBandApi.cs is Add-Type'd by the deskband scripts.
$scripts = @(
    'install.ps1',
    'uninstall.ps1',
    'install-deskband.ps1',
    'uninstall-deskband.ps1',
    'install-agent-task.ps1',
    'uninstall-agent-task.ps1',
    'stop-agent.ps1',
    'stop-quadvitals.ps1',
    'TrayDeskBandApi.cs'
)

$missing = @()
foreach ($item in $payload) {
    if (-not (Test-Path -LiteralPath $item.Src)) { $missing += $item.Src; continue }
    $target = Join-Path $stage $item.Dst
    $folder = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    Copy-Item -LiteralPath $item.Src -Destination $target -Force
    Write-Host ("  + {0}" -f $item.Dst)
}
foreach ($name in $scripts) {
    $src = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $src)) { $missing += $src; continue }
    $target = Join-Path (Join-Path $stage 'scripts') $name
    $folder = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    Copy-Item -LiteralPath $src -Destination $target -Force
    Write-Host ("  + scripts\{0}" -f $name)
}

if ($missing.Count -gt 0) {
    throw ("missing inputs: " + ($missing -join '; ') + " - run scripts\build-agent.ps1, build-ui.ps1 and build-deskband.ps1 first")
}

# ---------------------------------------------------------------- zip ----
Step 'zip'
$zip = Join-Path $releaseRoot ("QuadVitals-{0}.zip" -f $version)
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)

# ---------------------------------------------------------------- verify ----
Step 'verify package'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    # Compress-Archive (PS 5.1) writes entries with backslashes; compare on a normalised name.
    $entries = $archive.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
    $expected = @()
    foreach ($item in $payload) { $expected += ("QuadVitals-{0}/{1}" -f $version, ($item.Dst -replace '\\', '/')) }
    foreach ($name in $scripts) { $expected += ("QuadVitals-{0}/scripts/{1}" -f $version, $name) }
    $absent = @()
    foreach ($needle in $expected) {
        if ($entries -notcontains $needle) { $absent += $needle }
    }
    if ($absent.Count -gt 0) { throw ("package is missing entries: " + ($absent -join '; ')) }
    Write-Host ("  entries: {0} (expected {1} files + folders)" -f $entries.Count, $expected.Count)
} finally {
    $archive.Dispose()
}

$exe = Join-Path $stage 'build\out\agent\QuadVitals.Agent.exe'
$fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
Write-Host ("  agent file version: {0}" -f $fileVersion)
if ($fileVersion -notlike "$version*") {
    Write-Host ("  warning: binary version {0} does not match ProductVersion.SemVer {1}" -f $fileVersion, $version) -ForegroundColor Yellow
}

Step 'done'
Write-Host ("  package : {0}" -f $zip)
Write-Host ("  size    : {0} MB" -f $sizeMb)
Write-Host ("  sha256  : {0}" -f $hash)
Write-Host ""
Write-Host "  install from the extracted folder:"
Write-Host ("    powershell -File scripts\install.ps1        # or -Task / -NoTask / -InstallDir")
