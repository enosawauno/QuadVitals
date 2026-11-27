# Exercises the shared copy list without installing, packaging or loading any driver.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'sensor-runtime.ps1')
$passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
    Write-Host ('PASS ' + $Message)
}
$fixtureDir = Join-Path $root ('build\tmp\sensor-runtime-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null
Check (@(Get-SensorRuntimeFiles $fixtureDir).Count -eq 0) 'absent optional sensors stay optional for development'
$manifest = @(Get-SensorRuntimeManifest)
Check ($manifest.Count -eq 13) 'the complete framework runtime includes transitive dependencies'
Check ($manifest[0].Asset -eq 'runtimes/win-x64/lib/net472/LibreHardwareMonitorLib.dll') 'the runtime uses the x64 implementation rather than a reference assembly'
$fixtureSource = Join-Path $fixtureDir 'Fixture.cs'
[IO.File]::WriteAllText($fixtureSource, '[assembly: System.Reflection.AssemblyFileVersion("0.9.6.0")] public class Fixture {}')
$compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$fixtureDll = Join-Path $fixtureDir 'LibreHardwareMonitorLib.dll'
& $compiler /nologo /target:library /codepage:65001 /out:"$fixtureDll" $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'fixture build failed' }
$partialRejected = $false
try { Get-SensorRuntimeFiles $fixtureDir | Out-Null } catch { $partialRejected = $_.Exception.Message -like 'incomplete sensor runtime:*' }
Check $partialRejected 'a partial runtime is rejected before installation'
foreach ($package in $manifest | Select-Object -Skip 1) {
    Copy-Item -LiteralPath $fixtureDll -Destination (Join-Path $fixtureDir ([IO.Path]::GetFileName($package.Asset)))
}
$driverFile = Join-Path $fixtureDir 'PawnIO_setup.exe'
Copy-Item -LiteralPath (Join-Path $root 'lib\PawnIO_setup.exe') -Destination $driverFile
Assert-PawnIoInstaller $driverFile
Check $true 'the bundled official installer matches the pinned release hash'
[IO.File]::WriteAllText((Join-Path $fixtureDir 'sensor-example-LICENSE.txt'), 'fixture notice')
$files = @(Get-SensorRuntimeFiles $fixtureDir)
Check ($files.Count -eq 15) 'every runtime DLL, driver installer and supplemental notice travels together'
Check (@($files.Name | Select-Object -Unique).Count -eq $files.Count) 'the runtime copy list has no duplicate destination names'
[IO.File]::WriteAllText($fixtureSource, '[assembly: System.Reflection.AssemblyFileVersion("0.9.2.0")] public class Fixture {}')
& $compiler /nologo /target:library /codepage:65001 /out:"$fixtureDll" $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'legacy fixture build failed' }
$legacyRejected = $false
try { Get-SensorRuntimeFiles $fixtureDir | Out-Null } catch { $legacyRejected = $_.Exception.Message -like '*requires LHM 0.9.6*' }
Check $legacyRejected 'a legacy WinRing0 runtime cannot be distributed'
foreach ($name in @('build-agent.ps1','install.ps1','make-release.ps1')) {
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw
    Check ($source.Contains('Get-SensorRuntimeFiles')) ($name + ' consumes the shared runtime list')
}
$badInstaller = Join-Path $fixtureDir 'modified-installer.exe'
[IO.File]::WriteAllText($badInstaller, 'modified')
$badRejected = $false
try { Assert-PawnIoInstaller $badInstaller } catch { $badRejected = $_.Exception.Message -like '*hash mismatch*' }
Check $badRejected 'a changed driver installer is rejected'
$script:driverVersion = [version]'2.2.0.0'
$script:driverStarts = 0
$script:driverExit = 0
function Get-PawnIoVersion { return $script:driverVersion }
function Start-Process {
    [CmdletBinding()]
    param($FilePath, $ArgumentList, $WindowStyle, [switch]$Wait, [switch]$PassThru, $Verb)
    $script:driverStarts++
    Check ($ArgumentList -eq '-install -silent') 'installation uses the official signed edition without an unrestricted flag'
    if ($script:driverExit -eq 0) { $script:driverVersion = [version]'2.2.0.0' }
    [pscustomobject]@{ ExitCode = $script:driverExit }
}
Check ((Install-PawnIoDriver $driverFile) -eq 0 -and $script:driverStarts -eq 0) 'a current shared driver is reused without launching its installer'
$script:driverVersion = [version]'2.1.0.0'
Check ((Install-PawnIoDriver $driverFile) -eq 0 -and $script:driverStarts -eq 1) 'an older driver is upgraded and its installed version is checked'
$script:driverVersion = $null
$script:driverExit = 3010
Check ((Install-PawnIoDriver $driverFile) -eq 3010) 'a required Windows restart is returned without rebooting'
$script:driverExit = 5
$installFailed = $false
try { Install-PawnIoDriver $driverFile | Out-Null } catch { $installFailed = $_.Exception.Message -like '*installation failed: 5' }
Check $installFailed 'a driver installation failure cannot be reported as success'
Write-Host ('sensor runtime: ' + $passed + ' passed / 0 failed')
[IO.File]::WriteAllText((Join-Path $root 'build\tmp\assertion-count-sensor-runtime.txt'), [string]$passed)
