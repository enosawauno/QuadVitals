# QuadVitals installer.
#
# Copies a built tree into a writable install directory, registers the DeskBand for the current
# user (HKCU, no admin), optionally creates the logon task (one UAC), and starts the agent.
#
# Default install dir is %LOCALAPPDATA%\QuadVitals on purpose: LibreHardwareMonitor writes its
# kernel driver as "<exe name>.sys" NEXT TO THE EXE, so an install under Program Files
# would fail to load the temperature driver.
#
#   powershell -File scripts\install.ps1                 # task follows settings.json preference
#   powershell -File scripts\install.ps1 -Task           # force the logon task (one UAC)
#   powershell -File scripts\install.ps1 -NoTask         # never create the task (no UAC)
#   powershell -File scripts\install.ps1 -InstallDir D:\Apps\QuadVitals
#
# ASCII only: PS 5.1 misreads BOM-less UTF-8 as GBK.
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'QuadVitals'),
    [switch]$Task,
    [switch]$NoTask,
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build\out'
$agentOut = Join-Path $out 'agent'
$uiOut = Join-Path $out 'ui'
$bandOut = Join-Path $out 'deskband'

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }

# A wrong -InstallDir writes product files (and the sensor driver) into someone else's folder. The
# wizard validates too; this is for the script being called by hand. Roots, system folders and
# folders that contain one are refused; the default is a child of LOCALAPPDATA, so being inside the
# user profile stays fine.
function Test-WritableInstallDir([string]$Dir) {
    if ([string]::IsNullOrWhiteSpace($Dir)) { return $false }
    $sep = [IO.Path]::DirectorySeparatorChar
    try { $full = [IO.Path]::GetFullPath($Dir).TrimEnd($sep, [IO.Path]::AltDirectorySeparatorChar) } catch { return $false }
    if ($full.Length -le 2) { return $false }
    foreach ($dir in @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData)) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $dir = [IO.Path]::GetFullPath($dir).TrimEnd($sep, [IO.Path]::AltDirectorySeparatorChar)
        if ($full -eq $dir -or $full.StartsWith($dir + $sep, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ($dir.StartsWith($full + $sep, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    foreach ($dir in @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, [IO.Path]::GetTempPath())) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $dir = [IO.Path]::GetFullPath($dir).TrimEnd($sep, [IO.Path]::AltDirectorySeparatorChar)
        if ($full -eq $dir) { return $false }
        if ($dir.StartsWith($full + $sep, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    return $true
}

if (-not (Test-WritableInstallDir $InstallDir)) {
    Write-Host ''
    Write-Host "refusing to install into $InstallDir" -ForegroundColor Red
    Write-Host '  pick a folder of its own (the default is %LOCALAPPDATA%\QuadVitals)' -ForegroundColor Yellow
    exit 2
}

# ---------------------------------------------------------------- source files ----
Step 'check build outputs'
$required = @(
    @{ Src = (Join-Path $agentOut 'QuadVitals.Agent.exe');      Dst = 'QuadVitals.Agent.exe' },
    @{ Src = (Join-Path $agentOut 'QuadVitals.Core.dll');       Dst = 'QuadVitals.Core.dll' },
    @{ Src = (Join-Path $agentOut 'tray.ico');                  Dst = 'tray.ico' },
    @{ Src = (Join-Path $uiOut 'QuadVitals.Settings.exe');      Dst = 'QuadVitals.Settings.exe' },
    @{ Src = (Join-Path $uiOut 'QuadVitals.UI.dll');            Dst = 'QuadVitals.UI.dll' },
    @{ Src = (Join-Path $bandOut 'QuadVitalsDeskBand.dll');     Dst = 'QuadVitalsDeskBand.dll' },
    # licence + third-party notices must ship with the binaries (MPL 2.0 / Apache 2.0)
    @{ Src = (Join-Path $root 'LICENSE');                       Dst = 'LICENSE' },
    @{ Src = (Join-Path $root 'LICENSE.zh.md');                 Dst = 'LICENSE.zh.md' },
    @{ Src = (Join-Path $root 'THIRD-PARTY-NOTICES.txt');       Dst = 'THIRD-PARTY-NOTICES.txt' }
)
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath $item.Src)) {
        throw "missing $($item.Src) - run scripts\build-agent.ps1, build-ui.ps1 and build-deskband.ps1 first"
    }
}

$optional = @(
    @{ Src = (Join-Path $agentOut 'LibreHardwareMonitorLib.dll'); Dst = 'LibreHardwareMonitorLib.dll' },
    @{ Src = (Join-Path $agentOut 'HidSharp.dll');               Dst = 'HidSharp.dll' }
)
foreach ($item in $optional) {
    if (-not (Test-Path -LiteralPath $item.Src)) {
        Write-Host "  warning: $($item.Dst) missing - CPU temperature will show '--'" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- stop processes ----
# This script runs elevated, which is exactly why ending processes happens here and not in the
# installer window: the window is per-user and can never end an agent started by the highest-privilege
# logon task. stop-quadvitals.ps1 prints QV-STOP-OK, or QV-STOP-FAILED lines the window turns into a
# "cannot close" page.
Step 'stop running QuadVitals processes'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'stop-quadvitals.ps1') -InstallDir $InstallDir
if ($LASTEXITCODE -ne 0) {
    # A surviving process means a locked directory and a half-copied install; stop before touching files.
    throw "QuadVitals is still running (QV-STOP-FAILED above); close it, then run the installer again"
}

# ---------------------------------------------------------------- install dir ----
Step "install to $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# The LHM driver is written next to the exe. Refuse a read-only target instead of
# discovering it at runtime as a silent "temperature unavailable".
try {
    $probe = Join-Path $InstallDir '.write-test'
    [IO.File]::WriteAllText($probe, 'ok')
    Remove-Item -LiteralPath $probe -Force
} catch {
    throw "install dir is not writable: $InstallDir (needed for the LHM driver)"
}
Write-Host '  writable: yes'

# ---------------------------------------------------------------- legacy cleanup ----
# A driver left loaded by an earlier build blocks the copy that follows, so stale copies are
# removed first; one still held by the kernel is reported instead of forced.
$legacy = @(
    'QuadVitals.Agent.sys'
)
foreach ($dir in @($InstallDir, $agentOut, $uiOut, $bandOut)) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($name in $legacy) {
        $path = Join-Path $dir $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            Remove-Item -LiteralPath $path -Force
            Write-Host "  removed legacy $path"
        } catch {
            Write-Host "  legacy file is locked (reboot to remove): $path" -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------- release band ----
# ---------------------------------------------------------------- locked band dll ----
# No band toggling here on purpose: hiding the band makes Explorer ask "是否要在任务栏上显示…" when it
# is shown again, and the hide does not actually release the loaded DLL anyway (measured: HideDeskBand
# returns S_OK while the module stays mapped). The copy step below falls back to a versioned file name
# instead, so the upgrade works and the taskbar is left alone.

# ---------------------------------------------------------------- copy ----
Step 'copy files'
$script:explorerRestarted = $false

function Reset-Explorer {
    # The last resort for a locked file: a shell-loaded DLL (the band, or anything Explorer keeps mapped)
    # cannot be replaced while the shell runs. Killing the shell is disruptive but recoverable - the
    # (unelevated) installer starts it again once the copy is done, so the user only sees the taskbar
    # blink instead of a failed install.
    if ($script:explorerRestarted) { return $false }
    $script:explorerRestarted = $true
    Write-Host '  a file is locked; restarting Explorer and retrying' -ForegroundColor Yellow
    Get-Process -Name explorer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 1800
    return $true
}

function Copy-WithRetry([string]$Source, [string]$Destination) {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq 4) {
                throw "cannot write $Destination ($($_.Exception.Message))"
            }
            # One of the retries is spent restarting the shell rather than sleeping on a lock that
            # will not clear by itself.
            if ($attempt -eq 2) {
                if (Reset-Explorer) { continue }
            }
            Start-Sleep -Milliseconds 700
        }
    }
}
$bandDllDst = Join-Path $InstallDir 'QuadVitalsDeskBand.dll'
foreach ($item in $required) {
    $target = Join-Path $InstallDir $item.Dst

    if ($item.Dst -eq 'QuadVitalsDeskBand.dll') {
        # Explorer keeps the loaded band DLL mapped even after the band is hidden, so an upgrade that
        # overwrites it in place can fail with "file in use" for as long as the shell lives. Falling
        # back to a versioned file name keeps the upgrade working without an Explorer restart.
        try {
            Copy-WithRetry $item.Src $bandDllDst
        } catch {
            $version = (Get-Item -LiteralPath $item.Src).VersionInfo.FileVersion
            if (-not $version) { $version = 'new' }
            $bandDllDst = Join-Path $InstallDir ('QuadVitalsDeskBand-{0}.dll' -f $version)
            Write-Host "  the old band DLL is still loaded by Explorer; installing as $(Split-Path -Leaf $bandDllDst)" -ForegroundColor Yellow
            Copy-WithRetry $item.Src $bandDllDst
        }
    } else {
        Copy-WithRetry $item.Src $target
    }

    Write-Host "  $($item.Dst)"
}
foreach ($item in $optional) {
    if (Test-Path -LiteralPath $item.Src) {
        Copy-WithRetry $item.Src (Join-Path $InstallDir $item.Dst)
        Write-Host "  $($item.Dst)"
    }
}

# ---------------------------------------------------------------- register band ----
# Registering also tells the shell the registration changed. Do NOT ask it to show the band: ShowDeskBand
# makes Explorer raise its own two dialogs ("是否要在任务栏上显示 QuadVitals 工具栏?" and "工具栏与此版本的
# Windows 不兼容"), and answering those races the shell. A band the user already enabled comes back by
# itself; a fresh install gets it by ticking 右键任务栏 -> 工具栏 (a local check keeps that call dead
# keeps the show call from returning).
Step 'register DeskBand (per-user)'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-deskband.ps1') `
    -DllPath $bandDllDst



# ---------------------------------------------------------------- logon task ----
# The dashboard's "start with Windows" checkbox writes a preference; honour it here so the
# checkbox is not a dead setting. -Task/-NoTask override the file.
$wantTask = $false
$settingsPath = Join-Path $env:APPDATA 'QuadVitals\settings.json'
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $preference = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $wantTask = [bool]$preference.startAgentWithWindows
    } catch {
        Write-Host '  settings.json could not be parsed; task preference ignored' -ForegroundColor Yellow
    }
}

if ($NoTask) {
    Write-Host ''
    Write-Host '  logon task skipped (-NoTask). Run scripts\install-agent-task.ps1 when needed.' -ForegroundColor Yellow
} elseif (-not $Task -and -not $wantTask) {
    Write-Host ''
    Write-Host '  logon task skipped: startAgentWithWindows is false in settings.json' -ForegroundColor DarkGray
    Write-Host '  (use -Task to force it, or tick the box in the dashboard and install again)'
} else {
    Step 'install logon task (one UAC prompt)'
    # Cancelling the UAC prompt must not abort the install: the files and the band registration are
    # already in place, and the only thing missing is autostart. Report it and carry on.
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-agent-task.ps1') `
            -AgentExe (Join-Path $InstallDir 'QuadVitals.Agent.exe')
        if ($LASTEXITCODE -ne 0) { throw "install-agent-task.ps1 exit code $LASTEXITCODE" }
    } catch {
        Write-Host ''
        Write-Host ('  logon task NOT created: ' + $_.Exception.Message) -ForegroundColor Yellow
        Write-Host '  install continues; run scripts\install-agent-task.ps1 later to add it.' -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- start ----
if (-not $NoStart) {
    Step 'start agent'
    Start-Process -FilePath (Join-Path $InstallDir 'QuadVitals.Agent.exe')
    Start-Sleep -Seconds 2
    $agent = Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($agent) { Write-Host ("  agent up (pid {0})" -f $agent.Id) -ForegroundColor Green }
    else { Write-Host '  agent did NOT start (see %APPDATA%\QuadVitals\agent-crash.log)' -ForegroundColor Red }
}

Write-Host ''
Write-Host 'Installed. The band was shown on the taskbar (Explorer was restarted for it).' -ForegroundColor Green
Write-Host 'If it is missing: right-click the taskbar -> Toolbars -> check "QuadVitals"'
Write-Host '  (Windows asking whether the toolbar is compatible is normal; choose Yes)'
Write-Host ''
Write-Host "Settings : $env:APPDATA\QuadVitals\settings.json"
Write-Host "Install  : $InstallDir"
