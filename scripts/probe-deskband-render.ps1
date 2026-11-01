# Build and run the off-screen DeskBand render probe.
#
#   powershell -File scripts\probe-deskband-render.ps1
#
# Writes build\tmp\band-{100,125,150,200}.bmp for visual inspection. The probe fails if any
# pixel lands outside the rectangle GetBandInfo requests at that DPI.
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param([switch]$NoRun)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'native-toolchain.ps1')

if (-not (Test-Path -LiteralPath $cl))  { throw "cl.exe not found: $cl" }
if (-not (Test-Path -LiteralPath $inc)) { throw "Windows SDK headers not found: $inc" }

$outDir = Join-Path $root 'build\tmp'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$exe = Join-Path $outDir 'render-probe.exe'

$src = Join-Path $root 'src\QuadVitals.DeskBand'
$sources = @(
    (Join-Path $PSScriptRoot 'probe-deskband-render.cpp'),
    (Join-Path $src 'Settings.cpp'),
    (Join-Path $src 'Json.cpp'),
    (Join-Path $src 'MetricsClient.cpp'),
    (Join-Path $src 'Renderer.cpp'),
    (Join-Path $src 'Renderer.Tooltip.cpp')
)

& $cl /nologo /EHsc /O2 /MT /GS /W3 /utf-8 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /D_WIN32_WINNT=0x0601 `
  /I"$vc\include" `
  /I"$inc\ucrt" `
  /I"$inc\um" `
  /I"$inc\shared" `
  /I"$inc\winrt" `
  /I"$src" `
  /Fo:"$outDir\\" `
  /Fe:"$exe" `
  $sources `
  /link /LIBPATH:"$vc\lib\x64" /LIBPATH:"$umLib" /LIBPATH:"$ucrtLib" `
  ole32.lib oleaut32.lib shell32.lib shlwapi.lib user32.lib gdi32.lib gdiplus.lib advapi32.lib uuid.lib

if ($LASTEXITCODE -ne 0) { throw "cl.exe failed with $LASTEXITCODE" }
if ($NoRun) { return }

& $exe
exit $LASTEXITCODE
