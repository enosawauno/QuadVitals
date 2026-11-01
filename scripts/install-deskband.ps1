# Register the QuadVitals DeskBand for the current user (HKCU\Software\Classes) - no admin needed,
# proven in S3. Also tells the shell to re-read the toolbar list.
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$DllPath = '',
    [string]$Title = 'QuadVitals',
    [switch]$NoRefresh
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$clsid = '{3F0A6E2C-9B14-4D7A-8E55-1C2D3B4A5F60}'
$catid = '{00021492-0000-0000-C000-000000000046}'


if (-not $DllPath) { $DllPath = Join-Path $root 'build\out\deskband\QuadVitalsDeskBand.dll' }
if (-not (Test-Path -LiteralPath $DllPath)) { throw "missing $DllPath (run scripts\build-deskband.ps1)" }
$DllPath = (Resolve-Path -LiteralPath $DllPath).Path

$classes = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('SOFTWARE\Classes')
$base = $classes.CreateSubKey("CLSID\$clsid")
$base.SetValue('', $Title)

$inproc = $base.CreateSubKey('InprocServer32')
$inproc.SetValue('', $DllPath)
$inproc.SetValue('ThreadingModel', 'Apartment')

$null = $base.CreateSubKey("Implemented Categories\$catid")

"registered (per-user)"
"  clsid  = $clsid"
"  title  = $Title"
"  inproc = $DllPath"

if (-not $NoRefresh) {
    Add-Type -Path (Join-Path $PSScriptRoot 'TrayDeskBandApi.cs')
}

if (-not $NoRefresh) {
    "DeskBandRegistrationChanged = 0x{0:X8}" -f [QuadVitals.Install.TrayDeskBandApi]::Refresh()
}

# Deliberately no ShowDeskBand step: asking the shell to put the band on the taskbar makes Explorer
# pop two of its own dialogs ("是否要在任务栏上显示 QuadVitals 工具栏?" and "工具栏与此版本的 Windows
# 不兼容"), which we can neither restyle nor answer reliably - the answer races the shell. The band is
# registered here and appears once the user ticks it in 右键任务栏 -> 工具栏 (or on the next shell start
# if it was already ticked); a local check keeps that call from coming back.
