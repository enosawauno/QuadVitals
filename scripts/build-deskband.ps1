# Build the native DeskBand DLL (x64, static CRT) with MSVC + the Windows SDK.
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param([string]$Name = 'QuadVitalsDeskBand')
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')

if (-not (Test-Path -LiteralPath $cl))  { throw "cl.exe not found: $cl" }
if (-not (Test-Path -LiteralPath $inc)) { throw "Windows SDK headers not found: $inc (see scripts/native-toolchain.ps1)" }

$src = Join-Path $root 'src\QuadVitals.DeskBand'
$outDir = Join-Path $root 'build\out\deskband'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$out = Join-Path $outDir ($Name + '.dll')

$sources = Get-ChildItem -Path $src -Filter '*.cpp' | ForEach-Object { $_.FullName }

# Native file version: link.exe takes major.minor only, and VERSIONINFO wants four numbers, so
# both are derived from the one version source instead of being a second number kept by hand.
$versionSource = Get-Content -LiteralPath (Join-Path $root 'src\QuadVitals.Core\ProductVersion.cs') -Raw
$semVer = [regex]::Match($versionSource, 'SemVer\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"').Groups[1].Value
if (-not $semVer) { throw 'could not read ProductVersion.SemVer' }
$linkVersion = ($semVer -split '\.')[0..1] -join '.'
$fileVersionNumbers = (($semVer -split '\.') + '0') -join ','

# VERSIONINFO resource: rc.exe needs a generated header so the readable version string and the
# four-number field both come from ProductVersion.SemVer.
if (-not (Test-Path -LiteralPath $rc)) { throw "rc.exe not found: $rc (see scripts/native-toolchain.ps1)" }
$genDir = Join-Path $root 'build\tmp'
New-Item -ItemType Directory -Path $genDir -Force | Out-Null
$versionHeader = Join-Path $genDir 'qv-version.h'
$headerText = '#define QV_FILE_VERSION ' + $fileVersionNumbers + "`r`n" +
              '#define QV_VERSION_STRING "' + $semVer + '"' + "`r`n"
[IO.File]::WriteAllText($versionHeader, $headerText, [Text.Encoding]::ASCII)
$resFile = Join-Path $genDir 'QuadVitalsDeskBand.res'
& $rc /nologo /I"$genDir" /I"$inc\um" /I"$inc\shared" /I"$inc\ucrt" /fo"$resFile" (Join-Path $src 'QuadVitalsDeskBand.rc')
if ($LASTEXITCODE -ne 0) { throw "rc.exe failed with $LASTEXITCODE" }

& $cl /nologo /LD /EHsc /O2 /MT /GS /W4 /utf-8 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /D_WIN32_WINNT=0x0601 `
  /I"$vc\include" `
  /I"$inc\ucrt" `
  /I"$inc\um" `
  /I"$inc\shared" `
  /I"$inc\winrt" `
  /I"$src" `
  /Fo:"$outDir\\" `
  /Fe:"$out" `
  $sources `
  $resFile `
  /link /DLL `
  /VERSION:$linkVersion `
  /DEF:"$src\QuadVitalsDeskBand.def" `
  /LIBPATH:"$vc\lib\x64" `
  /LIBPATH:"$umLib" `
  /LIBPATH:"$ucrtLib" `
  ole32.lib oleaut32.lib shell32.lib shlwapi.lib user32.lib gdi32.lib gdiplus.lib advapi32.lib uuid.lib comctl32.lib

if ($LASTEXITCODE -ne 0) { throw "cl.exe failed with $LASTEXITCODE" }
Get-Item -LiteralPath $out | Select-Object FullName, Length, LastWriteTime | Format-List
