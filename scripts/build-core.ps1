# Build QuadVitals.Core + its test runner with the Roslyn csc that ships with VS BuildTools.
# There is no .NET SDK on this machine, so this is the whole toolchain: Roslyn csc from VS Build Tools.
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param([switch]$NoRun)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Roslyn csc not found: $csc (system csc is C# 5 and cannot compile this code)"
}

$coreDir = Join-Path $root 'src\QuadVitals.Core'
$testDir = Join-Path $root 'tests\QuadVitals.Core.Tests'
$outCore = Join-Path $root 'build\out\core'
$outTest = Join-Path $root 'build\out\tests'
New-Item -ItemType Directory -Path $outCore, $outTest -Force | Out-Null

$coreDll = Join-Path $outCore 'QuadVitals.Core.dll'
$testExe = Join-Path $outTest 'QuadVitals.Core.Tests.exe'

$coreSources = Get-ChildItem -Path $coreDir -Filter '*.cs' | ForEach-Object { $_.FullName }
# Version metadata for every managed binary lives in one file; see QuadVitals.Core.ProductVersion.
$sharedAssemblyInfo = Join-Path $root 'src\SharedAssemblyInfo.cs'
& $csc /nologo /target:library /langversion:7.3 /codepage:65001 /platform:anycpu /optimize+ /debug- `
    /out:"$coreDll" `
    /r:System.Runtime.Serialization.dll `
    /r:System.IO.Compression.dll `
    /r:System.Core.dll `
    /r:System.dll `
    $coreSources `
    $sharedAssemblyInfo
if ($LASTEXITCODE -ne 0) { throw "core build failed ($LASTEXITCODE)" }
"core -> $coreDll"

$testSources = Get-ChildItem -Path $testDir -Filter '*.cs' | ForEach-Object { $_.FullName }
& $csc /nologo /target:exe /langversion:7.3 /codepage:65001 /platform:anycpu /optimize+ /debug- `
    /out:"$testExe" `
    /r:"$coreDll" `
    /r:System.IO.Compression.dll `
    $testSources
if ($LASTEXITCODE -ne 0) { throw "tests build failed ($LASTEXITCODE)" }
"tests -> $testExe"

# The runner loads the library by name, so it has to sit next to the exe.
Copy-Item -LiteralPath $coreDll -Destination (Join-Path $outTest 'QuadVitals.Core.dll') -Force

if ($NoRun) { return }

& $testExe
$code = $LASTEXITCODE
"tests exit code = $code"
if ($code -ne 0) { throw "tests failed: $code assertion(s)" }
