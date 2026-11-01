# QuadVitals uninstaller.
#
# Stops the agent/dashboard, removes the logon task (one UAC), unregisters the DeskBand and
# deletes the install directory. User settings are KEPT by default - pass
# -RemoveSettings to delete %APPDATA%\QuadVitals too.
#
#   powershell -File scripts\uninstall.ps1
#   powershell -File scripts\uninstall.ps1 -RemoveSettings
#   powershell -File scripts\uninstall.ps1 -KeepFiles
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'QuadVitals'),
    [string]$TaskName = 'QuadVitals Agent',
    [switch]$KeepFiles,
    [switch]$RemoveSettings,
    [switch]$SkipTask
)
$ErrorActionPreference = 'Continue'

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# Same owner as the installer: the elevated script ends processes, never the window (see the header of
# stop-quadvitals.ps1). A survivor here would fail the file removal below anyway.
# The install folder is a parameter and uninstall deletes it recursively, so a drive root, a system
# folder or a folder that contains one is refused instead of escaped. A valid path is not enough
# either: the folder has to still look like this product's install folder.
function Test-SafeInstallDir([string]$Dir) {
    if ([string]::IsNullOrWhiteSpace($Dir)) { return $false }
    $sep = [IO.Path]::DirectorySeparatorChar
    try { $full = [IO.Path]::GetFullPath($Dir).TrimEnd($sep, [IO.Path]::AltDirectorySeparatorChar) } catch { return $false }
    if ($full.Length -le 2) { return $false }

    $systemDirs = @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData)
    foreach ($dir in $systemDirs) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $dir = [IO.Path]::GetFullPath($dir).TrimEnd($sep, [IO.Path]::AltDirectorySeparatorChar)
        if ($full -eq $dir -or $full.StartsWith($dir + $sep, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ($dir.StartsWith($full + $sep, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }

    # Identity only: the default install folder is a child of LOCALAPPDATA, so being inside those is fine.
    $userDirs = @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, [IO.Path]::GetTempPath())
    foreach ($dir in $userDirs) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $dir = [IO.Path]::GetFullPath($dir).TrimEnd($sep, [IO.Path]::AltDirectorySeparatorChar)
        if ($full -eq $dir) { return $false }
        if ($dir.StartsWith($full + $sep, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }

    foreach ($marker in @('QuadVitals.Agent.exe', 'QuadVitals.UI.dll', 'QuadVitals-Uninstall.exe')) {
        if (Test-Path -LiteralPath (Join-Path $full $marker)) { return $true }
    }
    return $false
}

Step 'stop running QuadVitals processes'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'stop-quadvitals.ps1') -InstallDir $InstallDir
if ($LASTEXITCODE -ne 0) {
    throw "QuadVitals is still running (QV-STOP-FAILED above); close it, then run the uninstaller again"
}

$taskExists = $false
& schtasks.exe /Query /TN $TaskName *> $null
if ($LASTEXITCODE -eq 0) { $taskExists = $true }

if ($SkipTask) {
    Write-Host '  logon task skipped (-SkipTask)'
} elseif (-not $taskExists) {
    Write-Host '  no logon task installed'
} else {
    Step 'remove logon task (one UAC prompt)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'uninstall-agent-task.ps1') `
        -TaskName $TaskName
}

Step 'unregister DeskBand'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'uninstall-deskband.ps1')

if ($KeepFiles) {
    Step 'keep install files (-KeepFiles)'
} elseif (-not (Test-SafeInstallDir $InstallDir)) {
    Step "skip removing $InstallDir"
    Write-Host '  this folder does not look like a QuadVitals install folder; left untouched' -ForegroundColor Yellow
    Write-Host '  delete it by hand if you are sure' -ForegroundColor Yellow
} else {
    Step "remove $InstallDir"
    if (Test-Path -LiteralPath $InstallDir) {
        try {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction Stop
            Write-Host '  removed'
        } catch {
            # LibreHardwareMonitor keeps its kernel driver (<exe>.sys) loaded until reboot.
            Write-Host "  could not remove everything: $($_.Exception.Message)" -ForegroundColor Yellow
            Get-ChildItem -LiteralPath $InstallDir -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Host "    leftover: $($_.Name)" }
            Write-Host '  reboot, then delete the directory (the sensor driver is still loaded)' -ForegroundColor Yellow
        }
    } else {
        Write-Host '  not present'
    }
}

$dataDir = Join-Path $env:APPDATA 'QuadVitals'
if ($RemoveSettings) {
    Step "remove user data $dataDir"
    if (Test-Path -LiteralPath $dataDir) {
        Remove-Item -LiteralPath $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host ''
    Write-Host "  user settings kept: $dataDir" -ForegroundColor DarkGray
    Write-Host '  (use -RemoveSettings to delete settings.json and crash logs)'
}

Write-Host ''
Write-Host 'Uninstalled. The taskbar entry disappears after Explorer re-reads the toolbar list;' -ForegroundColor Green
Write-Host 'if it lingers, restart Explorer or uncheck Toolbars -> QuadVitals.'
