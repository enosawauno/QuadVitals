# Build the single-file installer: QuadVitals-Setup-<version>.exe (WPF, no console).
#
# The payload is the zip that make-release.ps1 produces, embedded as a managed resource; the
# QuadVitals.UI design system (plus the Core it needs) is embedded too, so the installer shows the
# product's own palette and controls while staying one file. At run time it extracts the payload to
# %TEMP% and runs the very same scripts\install.ps1 / uninstall.ps1 the repo tests.
#
#   powershell -File scripts\build-installer.ps1              # package (if needed) + compile
#   powershell -File scripts\build-installer.ps1 -SkipPackage # reuse the existing zip
#   powershell -File scripts\build-installer.ps1 -Build       # rebuild agent/ui/band first
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [switch]$SkipPackage,
    [switch]$Build,
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc not found: $csc" }
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- version ----
Step 'read product version'
$versionFile = Join-Path $root 'src\QuadVitals.Core\ProductVersion.cs'
$hit = Select-String -LiteralPath $versionFile -Pattern 'SemVer\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"'
if (-not $hit) { throw "cannot read SemVer from $versionFile" }
$version = $hit.Matches[0].Groups[1].Value
Write-Host "  version: $version"

# ---------------------------------------------------------------- payload ----
$releaseRoot = Join-Path $root 'build\out\release'
$zip = Join-Path $releaseRoot ("QuadVitals-{0}.zip" -f $version)
if (-not $SkipPackage) {
    Step 'package payload'
    $releaseArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'make-release.ps1'))
    if ($Build) { $releaseArgs += '-Build' }
    & powershell @releaseArgs
    if ($LASTEXITCODE -ne 0) { throw "make-release.ps1 failed ($LASTEXITCODE)" }
}
if (-not (Test-Path -LiteralPath $zip)) { throw "missing payload $zip - run scripts\make-release.ps1 first" }
Write-Host ("  payload: {0} ({1:N0} B)" -f $zip, (Get-Item -LiteralPath $zip).Length)

# ---------------------------------------------------------------- payload freshness ----
# A stale payload is invisible: the installer runs the scripts inside the zip, not the ones in this tree,
# so a fix to install.ps1 or install-deskband.ps1 silently does nothing until the zip is rebuilt. That is
# how the two Explorer taskbar dialogs survived four rounds of fixes.
Step 'verify payload scripts match this tree'
$payloadCheck = Join-Path $root ('build\tmp\payload-check-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$packaged = @('install.ps1','uninstall.ps1','install-deskband.ps1','uninstall-deskband.ps1','install-agent-task.ps1','uninstall-agent-task.ps1','stop-agent.ps1','stop-quadvitals.ps1','TrayDeskBandApi.cs')
$drift = @()
try {
    Expand-Archive -LiteralPath $zip -DestinationPath $payloadCheck -Force
    $packedRoot = Get-ChildItem -LiteralPath $payloadCheck -Directory | Select-Object -First 1
    $packedScripts = Join-Path $packedRoot.FullName 'scripts'
    foreach ($name in $packaged) {
        $packedFile = Join-Path $packedScripts $name
        $currentFile = Join-Path $PSScriptRoot $name
        if (-not (Test-Path -LiteralPath $packedFile)) { $drift += ($name + ' missing from the payload'); continue }
        if (-not (Test-Path -LiteralPath $currentFile)) { $drift += ($name + ' missing from scripts'); continue }
        $a = (Get-FileHash -LiteralPath $packedFile -Algorithm SHA256).Hash
        $b = (Get-FileHash -LiteralPath $currentFile -Algorithm SHA256).Hash
        if ($a -ne $b) { $drift += ($name + ' differs from scripts\' + $name) }
    }
}
finally {
    Remove-Item -LiteralPath $payloadCheck -Recurse -Force -ErrorAction SilentlyContinue
}
if ($drift.Count -gt 0) {
    Write-Host '  the payload does not match this tree:' -ForegroundColor Red
    foreach ($item in $drift) { Write-Host ('    - ' + $item) -ForegroundColor Red }
    throw 'stale payload: run scripts\make-release.ps1 first (the installer runs the scripts inside the zip)'
}
Write-Host ('  {0} script(s) match' -f $packaged.Count)


# ---------------------------------------------------------------- inputs ----
Step 'collect embedded assemblies'
$coreDll = Join-Path $root 'build\out\core\QuadVitals.Core.dll'
$uiDll = Join-Path $root 'build\out\ui\QuadVitals.UI.dll'
foreach ($dll in @($coreDll, $uiDll)) {
    if (-not (Test-Path -LiteralPath $dll)) { throw "missing $dll - run scripts\build-ui.ps1 first" }
    Write-Host ("  {0} ({1:N0} B)" -f (Split-Path -Leaf $dll), (Get-Item -LiteralPath $dll).Length)
}

$sources = @(
    (Join-Path $root 'src\QuadVitals.Setup\Program.cs'),
    (Join-Path $root 'src\QuadVitals.Setup\InstallerCore.cs'),
    (Join-Path $root 'src\QuadVitals.Setup\SetupWindow.cs'),
    (Join-Path $root 'src\QuadVitals.Setup\SetupWindow.Shell.cs'),
    (Join-Path $root 'src\QuadVitals.Setup\SetupWindow.Welcome.cs'),
    (Join-Path $root 'src\QuadVitals.Setup\SetupWindow.Cancel.cs'),
    (Join-Path $root 'src\QuadVitals.Setup\SetupWindow.Progress.cs'),
    (Join-Path $root 'src\QuadVitals.Setup\SetupWindow.Result.cs'),
    (Join-Path $root 'src\SharedAssemblyInfo.cs')   # keeps FileVersion/ProductVersion on ProductVersion.SemVer
)
foreach ($source in $sources) {
    if (-not (Test-Path -LiteralPath $source)) { throw "missing source $source" }
}

# ---------------------------------------------------------------- compile ----
Step 'compile installer (winexe)'
$outDir = if ($OutDir) { if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir } } else { $releaseRoot }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$exe = Join-Path $outDir ("QuadVitals-Setup-{0}.exe" -f $version)
if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }

$icon = Join-Path $root 'assets\icon\app.ico'
$manifest = Join-Path $root 'src\QuadVitals.Setup\app.manifest'

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/langversion:7.3',
    '/codepage:65001',
    '/optimize+',
    ('/out:' + $exe),
    ('/reference:' + $coreDll),
    ('/reference:' + $uiDll),
    ('/reference:' + (Join-Path $wpf 'PresentationFramework.dll')),
    ('/reference:' + (Join-Path $wpf 'PresentationCore.dll')),
    ('/reference:' + (Join-Path $wpf 'WindowsBase.dll')),
    ('/reference:' + (Join-Path $framework 'System.Xaml.dll')),
    ('/reference:' + (Join-Path $framework 'System.Windows.Forms.dll')),
    ('/reference:' + (Join-Path $framework 'System.Drawing.dll')),
    ('/reference:' + (Join-Path $framework 'System.IO.Compression.dll')),
    ('/reference:' + (Join-Path $framework 'System.IO.Compression.FileSystem.dll')),
    ('/resource:' + $zip + ',QuadVitals.Payload.zip'),
    ('/resource:' + $uiDll + ',QuadVitals.UI.dll'),
    ('/resource:' + $coreDll + ',QuadVitals.Core.dll'),
    ('/win32manifest:' + $manifest)
)
if (Test-Path -LiteralPath $icon) { $arguments += ('/win32icon:' + $icon) }
$arguments += $sources

& $csc $arguments
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

# ---------------------------------------------------------------- verify ----
Step 'verify installer'
$info = (Get-Item -LiteralPath $exe).VersionInfo
$size = (Get-Item -LiteralPath $exe).Length
Write-Host ("  output          : {0}" -f $exe)
Write-Host ("  subsystem       : winexe (no console window)")
Write-Host ("  FileVersion     : {0}" -f $info.FileVersion)
Write-Host ("  ProductVersion  : {0}" -f $info.ProductVersion)
Write-Host ("  size            : {0:N0} B" -f $size)
if ($info.FileVersion -notlike "$version*") {
    Write-Host ("  warning: FileVersion {0} does not match {1}" -f $info.FileVersion, $version) -ForegroundColor Yellow
}

# Every payload binary must carry the same version. A stale folder (e.g. agent built before the
# version bump) silently ships an install whose Settings says 0.9.2 while its Agent still reports
# 0.9.0 — the zip and the freshness check happily accept it because the bytes are "the current
# build output".
Step 'verify payload binary versions'
$payloadBinaries = @(
    (Join-Path $root 'build\out\agent\QuadVitals.Agent.exe'),
    (Join-Path $root 'build\out\agent\QuadVitals.Core.dll'),
    (Join-Path $root 'build\out\ui\QuadVitals.Settings.exe'),
    (Join-Path $root 'build\out\ui\QuadVitals.UI.dll'),
    (Join-Path $root 'build\out\deskband\QuadVitalsDeskBand.dll')
)
$versionDrift = @()
foreach ($binary in $payloadBinaries) {
    if (-not (Test-Path -LiteralPath $binary)) { throw "missing payload binary $binary" }
    $binaryVersion = (Get-Item -LiteralPath $binary).VersionInfo.FileVersion
    Write-Host ("  {0,-38} {1}" -f (Split-Path -Leaf $binary), $binaryVersion)
    if ($binaryVersion -notlike "$version*") { $versionDrift += ("{0} is {1}" -f (Split-Path -Leaf $binary), $binaryVersion) }
}
if ($versionDrift.Count -gt 0) {
    throw ("payload version mismatch (expected {0}): {1} - rebuild the stale project first" -f $version, ($versionDrift -join '; '))
}

# PE subsystem 2 = GUI, 3 = console. A console installer is exactly the bug this rewrite fixes.
$bytes = [IO.File]::ReadAllBytes($exe)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$subsystem = [BitConverter]::ToUInt16($bytes, $peOffset + 0x5C)
if ($subsystem -ne 2) { throw "expected GUI subsystem (2), got $subsystem" }
Write-Host ("  PE subsystem    : {0} (GUI)" -f $subsystem)

Add-Type -AssemblyName System.Reflection
$asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($exe)
$names = $asm.GetManifestResourceNames()
foreach ($needed in @('QuadVitals.Payload.zip', 'QuadVitals.UI.dll', 'QuadVitals.Core.dll')) {
    if ($names -notcontains $needed) { throw "embedded resource missing from ${exe}: $needed" }
}
Write-Host ("  embedded        : {0}" -f ($names -join ', '))

$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
Write-Host ""
Write-Host ("  sha256          : {0}" -f $hash)

# ---------------------------------------------------------------- feed ----
# The in-app updater reads this manifest next to the release assets: version, installer
# name/size/hash. Upload it together with the setup exe (GitHub Releases asset). Written after
# the exe exists because the hash of a file cannot live inside that same file.
Step 'write update manifest'
$zipName = "QuadVitals-$version.zip"
$zipPath = Join-Path $releaseRoot $zipName

Step 'confirm the release notes'
# Packaging is the point of no return: the notes are what the user reads in the update card, so they
# have to be the wording that was agreed, not the wording whoever packaged the build typed. A
# maintainer checkout carries a local check for it; anywhere else the packager has to name the
# version they confirmed, which makes an accidental release of unagreed notes impossible.
$notesCheck = Join-Path $root 'scripts\maintenance\check-changelog-approved.ps1'
if (Test-Path -LiteralPath $notesCheck) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $notesCheck
    if ($LASTEXITCODE -ne 0) {
        throw 'release notes are not confirmed - confirm the wording, save the approved text, then package'
    }
} elseif ($env:QUADVITALS_RELEASE_NOTES_CONFIRMED -ne $version) {
    throw ('release notes are not confirmed - set QUADVITALS_RELEASE_NOTES_CONFIRMED=' + $version + ' once the wording is agreed')
}

Step 'read release notes from CHANGELOG.md'
# The changelog is the single source for what users are told about a version: the in-app update
# card reads these lines from latest.json. A version without an entry is a release with nothing to
# tell the user, which is exactly the drift the changelog exists to prevent.
function U([int[]]$codes) { return (-join ($codes | ForEach-Object { [char]$_ })) }
$changelogPath = Join-Path $root 'CHANGELOG.md'
if (-not (Test-Path -LiteralPath $changelogPath)) { throw "missing $changelogPath" }
$changelogLines = [IO.File]::ReadAllText($changelogPath, [Text.Encoding]::UTF8) -split "`r?`n"
$versionHeading = '^##\s+' + [regex]::Escape($version) + '(?:\s|$)'
$start = -1
for ($i = 0; $i -lt $changelogLines.Count; $i++) {
    if ($changelogLines[$i] -match $versionHeading) { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no '## $version' section - write the release notes first" }

$notes = New-Object System.Collections.Generic.List[string]
$inSection = $false
for ($i = $start + 1; $i -lt $changelogLines.Count; $i++) {
    $line = $changelogLines[$i]
    if ($line -match '^## ') { break }
    # Release notes are the numbered lines of the version section; the changelog keeps them either as
    # "01. text" or as a markdown list item "- 01. text", and both carry the same sentence.
    if ($line -match '^(?:-\s+)?\d+\.\s*(.+)$') { $notes.Add($Matches[1].Trim()) }
}
if ($notes.Count -eq 0) { throw "CHANGELOG.md entry for $version has no user-visible items" }
Write-Host ("  notes           : {0} item(s)" -f $notes.Count)
foreach ($note in $notes) { Write-Host ("    - {0}" -f $note) }

$manifest = [ordered]@{
    version     = $version
    publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    notes       = $notes.ToArray()
    setup       = [ordered]@{
        name   = Split-Path -Leaf $exe
        size   = $size
        sha256 = $hash.ToLowerInvariant()
    }
    zip         = $null
}
if (Test-Path -LiteralPath $zipPath) {
    $manifest.zip = [ordered]@{
        name   = $zipName
        size   = (Get-Item -LiteralPath $zipPath).Length
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifestPath = Join-Path $outDir 'latest.json'
$json = $manifest | ConvertTo-Json -Depth 6 -Compress
[IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object Text.UTF8Encoding($false)))
Write-Host ("  manifest        : {0}" -f $manifestPath)
Write-Host ("  version         : {0}" -f $manifest.version)
Write-Host ("  setup sha256    : {0}" -f $manifest.setup.sha256)
Write-Host ""
Write-Host "  double-click it:  .\QuadVitals-Setup-$version.exe"
Write-Host "  silent modes  :   /S  |  --uninstall  |  --extract-only  |  --help"
