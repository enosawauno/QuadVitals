# Restore pinned Windows x64 / .NET Framework runtime assets without installing a driver.
param([string]$OutDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'sensor-runtime.ps1')
if (-not $OutDir) { $OutDir = Join-Path $root 'lib' }
$cache = Join-Path $root 'build\tmp\sensor-packages'
New-Item -ItemType Directory -Path $OutDir, $cache -Force | Out-Null
foreach ($package in (Get-SensorRuntimeManifest)) {
    $id = $package.Id.ToLowerInvariant()
    $version = $package.Version
    $zip = Join-Path $cache "$id.$version.zip"
    $unpacked = Join-Path $cache "$id.$version"
    if (-not (Test-Path -LiteralPath $zip)) {
        Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile $zip
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $unpacked -Force
    Copy-Item -LiteralPath (Join-Path $unpacked $package.Asset) -Destination $OutDir -Force
    foreach ($notice in (Get-ChildItem -LiteralPath $unpacked -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)\.TXT$' })) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $OutDir ('sensor-' + $id + '-' + $notice.Name)) -Force
    }
}
$driver = Get-PawnIoInstallerInfo
$driverFile = Join-Path $cache 'PawnIO_setup.exe'
if (-not (Test-Path -LiteralPath $driverFile)) {
    Invoke-WebRequest -UseBasicParsing -Uri $driver.Url -OutFile $driverFile
}
Assert-PawnIoInstaller $driverFile
if ((Get-AuthenticodeSignature -LiteralPath $driverFile).Status -ne 'Valid') { throw 'PawnIO installer signature is not trusted' }
Copy-Item -LiteralPath $driverFile -Destination $OutDir -Force
$files = @(Get-SensorRuntimeFiles $OutDir)
Write-Host ('sensor runtime restored: ' + $files.Count + ' files; official PawnIO installer bundled without installing it')
