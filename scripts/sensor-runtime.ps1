# One file set for development, release staging and installation.
function Get-SensorRuntimeManifest {
    @(
        @{ Id='LibreHardwareMonitorLib'; Version='0.9.6'; Asset='runtimes/win-x64/lib/net472/LibreHardwareMonitorLib.dll' },
        @{ Id='HidSharp'; Version='2.6.4'; Asset='lib/net35/HidSharp.dll' },
        @{ Id='BlackSharp.Core'; Version='1.0.7'; Asset='lib/net472/BlackSharp.Core.dll' },
        @{ Id='DiskInfoToolkit'; Version='1.1.2'; Asset='lib/net472/DiskInfoToolkit.dll' },
        @{ Id='RAMSPDToolkit-NDD'; Version='1.4.2'; Asset='lib/net472/RAMSPDToolkit-NDD.dll' },
        @{ Id='System.Buffers'; Version='4.6.1'; Asset='lib/net462/System.Buffers.dll' },
        @{ Id='System.CodeDom'; Version='10.0.2'; Asset='lib/net462/System.CodeDom.dll' },
        @{ Id='System.Memory'; Version='4.6.3'; Asset='lib/net462/System.Memory.dll' },
        @{ Id='System.Numerics.Vectors'; Version='4.6.1'; Asset='lib/net462/System.Numerics.Vectors.dll' },
        @{ Id='System.Runtime.CompilerServices.Unsafe'; Version='6.1.2'; Asset='lib/net462/System.Runtime.CompilerServices.Unsafe.dll' },
        @{ Id='System.Security.AccessControl'; Version='6.0.0'; Asset='runtimes/win/lib/net461/System.Security.AccessControl.dll' },
        @{ Id='System.Security.Principal.Windows'; Version='5.0.0'; Asset='runtimes/win/lib/net461/System.Security.Principal.Windows.dll' },
        @{ Id='System.Threading.AccessControl'; Version='10.0.3'; Asset='lib/net462/System.Threading.AccessControl.dll' }
    )
}

function Get-SensorRuntimeFiles([string]$Directory) {
    $library = Join-Path $Directory 'LibreHardwareMonitorLib.dll'
    if (-not (Test-Path -LiteralPath $library)) { return }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($library)
    if ($version.FileMajorPart -ne 0 -or $version.FileMinorPart -ne 9 -or $version.FileBuildPart -ne 6) {
        throw 'sensor runtime requires LHM 0.9.6; run scripts\restore-sensor-runtime.ps1'
    }
    foreach ($package in (Get-SensorRuntimeManifest)) {
        $file = Join-Path $Directory ([IO.Path]::GetFileName($package.Asset))
        if (-not (Test-Path -LiteralPath $file)) { throw ('incomplete sensor runtime: ' + [IO.Path]::GetFileName($file)) }
        Get-Item -LiteralPath $file
    }
    $installer = Join-Path $Directory 'PawnIO_setup.exe'
    Assert-PawnIoInstaller $installer
    Get-Item -LiteralPath $installer
    Get-ChildItem -LiteralPath $Directory -Filter 'sensor-*.txt' -File
}

function Get-PawnIoInstallerInfo {
    @{
        Version = [version]'2.2.0.0'
        Url = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
        Sha256 = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
    }
}

function Assert-PawnIoInstaller([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw 'missing bundled PawnIO installer' }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne (Get-PawnIoInstallerInfo).Sha256) {
        throw 'bundled PawnIO installer hash mismatch'
    }
}

function Get-PawnIoVersion {
    foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO',
                       'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO')) {
        $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        $version = $null
        if ($entry -and [version]::TryParse([string]$entry.DisplayVersion, [ref]$version)) { return $version }
    }
    return $null
}

function Install-PawnIoDriver([string]$Installer) {
    $minimum = (Get-PawnIoInstallerInfo).Version
    $installed = Get-PawnIoVersion
    if ($installed -and $installed -ge $minimum) { return 0 }
    Assert-PawnIoInstaller $Installer
    $options = @{ FilePath=$Installer; ArgumentList='-install -silent'; Wait=$true; PassThru=$true; WindowStyle='Hidden'; ErrorAction='Stop' }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { $options.Verb = 'RunAs' }
    $process = Start-Process @options
    if ($process.ExitCode -eq 3010) { return 3010 }
    if ($process.ExitCode -ne 0) { throw ('PawnIO installation failed: ' + $process.ExitCode) }
    $installed = Get-PawnIoVersion
    if (-not $installed -or $installed -lt $minimum) { throw 'PawnIO installation did not produce the required driver version' }
    return 0
}
