# Compile the installer sources as a LIBRARY so a test can drive the real WPF code.
#
# Why a library and not the shipped exe: the wizard's rules (which buttons close the window, which page
# has no text on it, what the failure page shows) are behaviour, and behaviour in a winexe can only be
# checked by installing. Tests load this DLL by reflection and call the same methods the buttons call.
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$OutDir = 'build\tmp\setup-check',
    [switch]$Reuse
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc not found: $csc" }
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'

$coreDll = Join-Path $root 'build\out\core\QuadVitals.Core.dll'
$uiDll = Join-Path $root 'build\out\ui\QuadVitals.UI.dll'
foreach ($dll in @($coreDll, $uiDll)) {
    if (-not (Test-Path -LiteralPath $dll)) { throw "missing $dll - run scripts\build-ui.ps1 first" }
}

$out = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
New-Item -ItemType Directory -Path $out -Force | Out-Null

# The test loads these next to the DLL, so copy them instead of relying on the build\out layout.
foreach ($dll in @($coreDll, $uiDll)) {
    Copy-Item -LiteralPath $dll -Destination (Join-Path $out (Split-Path -Leaf $dll)) -Force
}

$dllOut = Join-Path $out 'QuadVitals.Setup.dll'
if (Test-Path -LiteralPath $dllOut) { Remove-Item -LiteralPath $dllOut -Force }

$sources = Get-ChildItem -Path (Join-Path $root 'src\QuadVitals.Setup') -Filter '*.cs' |
           ForEach-Object { $_.FullName }

& $csc /nologo /target:library /langversion:7.3 /codepage:65001 /optimize+ `
    /out:"$dllOut" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:"$wpf\PresentationFramework.dll" /reference:"$wpf\PresentationCore.dll" `
    /reference:"$wpf\WindowsBase.dll" ("/reference:" + (Join-Path $framework 'System.Xaml.dll')) `
    /reference:"$coreDll" /reference:"$uiDll" `
    $sources `
    (Join-Path $root 'src\SharedAssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

"setup library -> $dllOut"
