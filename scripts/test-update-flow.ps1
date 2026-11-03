# End-to-end probe for the in-app update path: a real HTTP listener serves a staged feed, the
# production transport downloads the installer, and the manifest hash is checked against the bytes
# on disk. The Core suite covers the same logic with in-memory streams; this covers the socket,
# the redirect-free static case and the content-length handling that unit tests cannot.
#
#   powershell -File scripts\test-update-flow.ps1
#
# Needs a built Core (scripts\build-core.ps1) and one installer (scripts\build-installer.ps1).
# No admin rights, no network access beyond 127.0.0.1, nothing is installed.
param(
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc not found: $csc" }

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

$coreDll = Join-Path $root 'build\out\core\QuadVitals.Core.dll'
if (-not (Test-Path -LiteralPath $coreDll)) { throw "missing $coreDll - run scripts\build-core.ps1" }

$releaseRoot = Join-Path $root 'build\out\release'
$setup = Get-ChildItem -LiteralPath $releaseRoot -Filter 'QuadVitals-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "no installer in $releaseRoot - run scripts\build-installer.ps1" }

$work = if ($OutDir) { $OutDir } else { Join-Path $root 'build\tmp\update-flow' }
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

# The advertised version has to be newer than the local build, otherwise the checker correctly
# answers "current" and the download branch never runs.
$versionFile = Join-Path $root 'src\QuadVitals.Core\ProductVersion.cs'
$hit = Select-String -LiteralPath $versionFile -Pattern 'SemVer\s*=\s*"([0-9]+)\.([0-9]+)\.([0-9]+)"'
if (-not $hit) { throw "cannot read SemVer from $versionFile" }
$major = [int]$hit.Matches[0].Groups[1].Value
$minor = [int]$hit.Matches[0].Groups[2].Value
$patch = [int]$hit.Matches[0].Groups[3].Value
$nextVersion = '{0}.{1}.{2}' -f $major, $minor, ($patch + 1)

Step 'stage feed'
$feed = Join-Path $work 'feed'
New-Item -ItemType Directory -Path $feed -Force | Out-Null
$setupName = "QuadVitals-Setup-$nextVersion.exe"
$staged = Join-Path $feed $setupName
Copy-Item -LiteralPath $setup.FullName -Destination $staged
Write-Host ("  advertised version : {0} (local {1}.{2}.{3})" -f $nextVersion, $major, $minor, $patch)
Write-Host ("  staged package     : {0} ({1:N0} B)" -f $setupName, (Get-Item -LiteralPath $staged).Length)

Step 'compile probe'
$probe = Join-Path $work 'update-flow-probe.exe'
# The probe is a private copy: keep the Core it links against beside it instead of relying on the
# process working directory.
Copy-Item -LiteralPath $coreDll -Destination $work -Force
& $csc /nologo /target:exe /langversion:7.3 /codepage:65001 /platform:anycpu /optimize+ /out:$probe `
    /r:$coreDll (Join-Path $PSScriptRoot 'update-flow-probe.cs')
if ($LASTEXITCODE -ne 0) { throw "probe compile failed ($LASTEXITCODE)" }

Step 'run probe'
& $probe --feed $feed --setup $staged --version $nextVersion
$probeExit = $LASTEXITCODE

Write-Host ""
if ($probeExit -ne 0) { throw "update flow probe reported $probeExit failure(s)" }
Write-Host "update flow: PASS" -ForegroundColor Green
exit 0
