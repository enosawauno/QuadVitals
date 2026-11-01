# Resolves the native toolchain for the DeskBand build and the render probe.
#
# Dot-source it:  . (Join-Path $PSScriptRoot 'native-toolchain.ps1')
# It sets $vc, $cl, $sdkRoot, $sdkVer, $inc, $umLib, $ucrtLib and $rc.
#
# Two SDK layouts are supported:
#   * installed Windows Kits  -> C:\Program Files (x86)\Windows Kits\10 (default)
#   * extracted Windows SDK NuGet package -> set QUADVITALS_SDK to the extracted package root
# The MSVC toolset can be overridden with QUADVITALS_VC.
#
# ASCII only: PowerShell 5.1 reads BOM-less UTF-8 as GBK.
$ErrorActionPreference = 'Stop'

$vc = if ($env:QUADVITALS_VC) { $env:QUADVITALS_VC } else {
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
}
$cl = Join-Path $vc 'bin\Hostx64\x64\cl.exe'

$sdkRoot = $env:QUADVITALS_SDK
if ($sdkRoot) {
    # NuGet layout: the package keeps a versioned Include folder next to its lib folders.
    $sdkVer = '10.0.19041.0'
    $inc = Join-Path $sdkRoot "microsoft.windows.sdk.cpp\c\Include\$sdkVer"
    $umLib = Join-Path $sdkRoot 'microsoft.windows.sdk.cpp.x64\c\um\x64'
    $ucrtLib = Join-Path $sdkRoot 'microsoft.windows.sdk.cpp.x64\c\ucrt\x64'
    $rc = Join-Path $sdkRoot "microsoft.windows.sdk.cpp\c\bin\$sdkVer\x64\rc.exe"
}
else {
    # Installed layout: pick the newest SDK version present.
    $sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
    $includeRoot = Join-Path $sdkRoot 'Include'
    $sdkVer = ''
    if (Test-Path -LiteralPath $includeRoot) {
        $sdkVer = Get-ChildItem -LiteralPath $includeRoot -Directory |
            Where-Object { $_.Name -match '^10\.' } |
            Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
            Select-Object -First 1 -ExpandProperty Name
    }
    if (-not $sdkVer) {
        throw "Windows SDK not found under $includeRoot; install the Windows SDK or set QUADVITALS_SDK"
    }
    $inc = Join-Path $sdkRoot "Include\$sdkVer"
    $umLib = Join-Path $sdkRoot "Lib\$sdkVer\um\x64"
    $ucrtLib = Join-Path $sdkRoot "Lib\$sdkVer\ucrt\x64"
    $rc = Join-Path $sdkRoot "bin\$sdkVer\x64\rc.exe"
}
