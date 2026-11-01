# Remove the per-user DeskBand registration, hiding the band first so the taskbar drops it.
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param([switch]$NoRefresh)
$ErrorActionPreference = 'Continue'

$clsid = '{3F0A6E2C-9B14-4D7A-8E55-1C2D3B4A5F60}'

Add-Type -Path (Join-Path $PSScriptRoot 'TrayDeskBandApi.cs')
"HideDeskBand = 0x{0:X8}" -f [QuadVitals.Install.TrayDeskBandApi]::Hide($clsid)

[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("SOFTWARE\Classes\CLSID\$clsid", $false)
if (-not $NoRefresh) { "DeskBandRegistrationChanged = 0x{0:X8}" -f [QuadVitals.Install.TrayDeskBandApi]::Refresh() }
"unregistered: $clsid"
