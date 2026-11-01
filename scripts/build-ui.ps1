# Build QuadVitals.UI (design system) + QuadVitals.Settings (dashboard window).
# This box has no .NET SDK and no WPF XAML compiler, so the UI is plain C# that references the
# framework's WPF runtime assemblies directly (no BAML, no XAML build step).
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param([switch]$NoRun, [switch]$SkipCore, [string]$OutDir = '')
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc not found: $csc" }
if (-not (Test-Path -LiteralPath $wpf)) { throw "WPF runtime assemblies not found: $wpf (expected on Windows 10)" }

# Core must be current: the settings model lives there.
if (-not $SkipCore) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-core.ps1') -NoRun | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'core build failed' }
}

$coreDll = Join-Path $root 'build\out\core\QuadVitals.Core.dll'
$outDir = if ($OutDir) { if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir } } else { Join-Path $root 'build\out\ui' }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Copy-Item -LiteralPath $coreDll -Destination (Join-Path $outDir 'QuadVitals.Core.dll') -Force

$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpfRefs = @(
    ('/r:' + (Join-Path $wpf 'PresentationFramework.dll')),
    ('/r:' + (Join-Path $wpf 'PresentationCore.dll')),
    ('/r:' + (Join-Path $wpf 'WindowsBase.dll')),
    ('/r:' + (Join-Path $framework 'System.Xaml.dll'))
)

$uiDll = Join-Path $outDir 'QuadVitals.UI.dll'
$uiSources = Get-ChildItem -Path (Join-Path $root 'src\QuadVitals.UI') -Filter '*.cs' | ForEach-Object { $_.FullName }
# Version metadata for every managed binary lives in one file; see QuadVitals.Core.ProductVersion.
$sharedAssemblyInfo = Join-Path $root 'src\SharedAssemblyInfo.cs'
& $csc /nologo /target:library /langversion:7.3 /codepage:65001 /optimize+ /debug- `
    /out:"$uiDll" `
    /r:"$coreDll" `
    $wpfRefs `
    $uiSources `
    $sharedAssemblyInfo
if ($LASTEXITCODE -ne 0) { throw "UI build failed ($LASTEXITCODE)" }
"UI -> $uiDll"

$exe = Join-Path $outDir 'QuadVitals.Settings.exe'
$appSources = Get-ChildItem -Path (Join-Path $root 'src\QuadVitals.Settings') -Filter '*.cs' | ForEach-Object { $_.FullName }
$icon = Join-Path $root 'assets\icon\app.ico'
$iconArg = if (Test-Path -LiteralPath $icon) { '/win32icon:' + $icon } else { $null }

# The title bar draws the mark without its dark tile (a dark tile inside a dark bar is redundant).
# 128 px keeps it crisp at the ~58 px it is drawn at; the exe/tray icons keep the tiled artwork.
$markPng = Join-Path $root 'assets\icon\png\mark-128.png'
if (-not (Test-Path -LiteralPath $markPng)) { $markPng = Join-Path $root 'assets\icon\png\mark-64.png' }
$markArg = if (Test-Path -LiteralPath $markPng) { '/resource:' + $markPng + ',QuadVitals.Settings.mark.png' } else { $null }

& $csc /nologo /target:winexe /langversion:7.3 /codepage:65001 /optimize+ /debug- `
    /out:"$exe" `
    /r:"$coreDll" `
    /r:"$uiDll" `
    /r:System.Net.NetworkInformation.dll `
    /r:System.Management.dll `
    $wpfRefs `
    $iconArg `
    $markArg `
    $appSources `
    $sharedAssemblyInfo
if ($LASTEXITCODE -ne 0) { throw "Settings build failed ($LASTEXITCODE)" }
"Settings -> $exe"

if ($NoRun) { return }
"launching $exe"
Start-Process -FilePath $exe
