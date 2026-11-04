# One-click dev launcher for QuadVitals (Agent + dashboard).
#
#   scripts\launch-dev.ps1              # restart the dev app; rebuilds only if sources changed
#   powershell -File scripts\launch-dev.ps1 -Build     # force a full rebuild
#   powershell -File scripts\launch-dev.ps1 -NoBuild   # never rebuild (fastest)
#   powershell -File scripts\launch-dev.ps1 -NoAgent   # dashboard only, the agent keeps running
#
# Behaviour:
#   * always restarts: an already open Agent/dashboard is stopped first, so the window you
#     see is the one this run started;
#   * rebuild is skipped when every binary is newer than the newest src/tests file, because
#     a full rebuild is ~15s and most launches change nothing (Core tests alone are ~7s);
#   * builds need Agent/Settings stopped anyway, because they hold QuadVitals.Core.dll open;
#   * output goes to build\tmp\dev-launch.log; on failure a dialog shows the log tail.
#
# ASCII only: PowerShell 5.1 decodes BOM-less .ps1 as GBK and would mangle non-ASCII text.
param(
    [switch]$Build,
    [switch]$NoBuild,
    [switch]$NoAgent,
    [switch]$NoWait
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$agentExe = Join-Path $root 'build\out\agent\QuadVitals.Agent.exe'
$uiExe = Join-Path $root 'build\out\ui\QuadVitals.Settings.exe'
$coreDll = Join-Path $root 'build\out\core\QuadVitals.Core.dll'
$bandDll = Join-Path $root 'build\out\deskband\QuadVitalsDeskBand.dll'
$logDir = Join-Path $root 'build\tmp'
$logFile = Join-Path $logDir 'dev-launch.log'

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
if (Test-Path -LiteralPath $logFile) { Remove-Item -LiteralPath $logFile -Force }

function Write-Log([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    try { Add-Content -LiteralPath $logFile -Value $text -Encoding UTF8 } catch { }
}

if ($PSVersionTable.PSVersion.Major -gt 5) {
    Write-Log 'note: this script targets Windows PowerShell 5.1 (the build scripts use it).' 'DarkYellow'
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QuadVitalsDevWindow {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@

function Get-DashboardProcess {
    Get-Process -Name 'QuadVitals.Settings' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
}

function Show-DashboardWindow([System.Diagnostics.Process]$Process) {
    $rect = New-Object QuadVitalsDevWindow+RECT
    [QuadVitalsDevWindow]::GetWindowRect($Process.MainWindowHandle, [ref]$rect) | Out-Null
    $workArea = [System.Windows.Forms.Screen]::FromHandle($Process.MainWindowHandle).WorkingArea
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $left = $workArea.Left + [Math]::Max(0, [Math]::Floor(($workArea.Width - $width) / 2))
    $top = $workArea.Top + [Math]::Max(0, [Math]::Floor(($workArea.Height - $height) / 2))
    [QuadVitalsDevWindow]::ShowWindowAsync($Process.MainWindowHandle, 9) | Out-Null
    [QuadVitalsDevWindow]::SetWindowPos($Process.MainWindowHandle, [IntPtr]::Zero, $left, $top, 0, 0, 0x0041) | Out-Null
    [QuadVitalsDevWindow]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
}

function Show-Failure([string]$reason) {
    $tail = ''
    if (Test-Path -LiteralPath $logFile) {
        $tail = (Get-Content -LiteralPath $logFile -Tail 15 -ErrorAction SilentlyContinue | Out-String).Trim()
    }
    if ($tail.Length -gt 1200) { $tail = $tail.Substring($tail.Length - 1200) }
    $message = 'QuadVitals dev ' + $reason + '.' + [Environment]::NewLine + [Environment]::NewLine +
        'Log: ' + $logFile + [Environment]::NewLine + [Environment]::NewLine + $tail
    $shell = New-Object -ComObject WScript.Shell
    [void]$shell.Popup($message, 0, 'QuadVitals Dev', 16)
}

function Test-NeedsBuild {
    if (-not (Test-Path -LiteralPath $uiExe) -or -not (Test-Path -LiteralPath $coreDll)) {
        return $true
    }
    if (-not $NoAgent -and -not (Test-Path -LiteralPath $agentExe)) { return $true }
    # oldest binary decides: a source newer than that means at least one output is stale. With
    # -NoAgent the agent is left alone (and not rebuilt), so its timestamp must not be compared.
    $stamps = @(
        (Get-Item -LiteralPath $uiExe).LastWriteTimeUtc,
        (Get-Item -LiteralPath $coreDll).LastWriteTimeUtc
    )
    if (-not $NoAgent) { $stamps += (Get-Item -LiteralPath $agentExe).LastWriteTimeUtc }
    $oldest = $stamps | Measure-Object -Minimum | Select-Object -ExpandProperty Minimum

    $newest = Get-ChildItem -LiteralPath (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.xaml' } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $newest) { return $false }
    return ($newest.LastWriteTimeUtc -gt $oldest)
}

# ---------------------------------------------------------------- stop old ----
# -NoAgent means "do not touch the agent": leaving it running is the whole point of the flag, so
# the stop step must be skipped too (stopping it and not restarting it silently removed the
# temperature reading until someone noticed the dashboard showing "Agent 未运行").
$existing = Get-Process -Name 'QuadVitals.Agent', 'QuadVitals.Settings' -ErrorAction SilentlyContinue
if ($existing) {
    $pids = ($existing | ForEach-Object { $_.Id }) -join ', '
    Write-Log ('== stop running processes (pid ' + $pids + ')') 'Cyan'
    if (-not $NoAgent) {
        # The agent may be an elevated instance (logon task): ask it to exit, do not kill it.
        & (Join-Path $PSScriptRoot 'stop-agent.ps1')
    }
    else {
        Write-Log '  -NoAgent: agent left alone' 'DarkGray'
    }
    Get-Process -Name 'QuadVitals.Settings' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}

# ---------------------------------------------------------------- build ----
$needBuild = $false
if (-not $NoBuild) {
    if ($Build) { $needBuild = $true }
    else {
        # A missing output must force a rebuild: the mtime comparison only looks at files that
        # exist, so a half-written build (Roslyn failed to rename its temp dll) would otherwise
        # count as "up to date" and the app would start without its dependency.
        $requiredOutputs = @(
            $uiExe,
            (Join-Path $root 'build\out\ui\QuadVitals.UI.dll'),
            $coreDll
        )
        # A -NoAgent run cannot rebuild the agent anyway (the running instance holds its own
        # binaries), so a stale/missing agent exe must not force a rebuild it cannot finish.
        if (-not $NoAgent) { $requiredOutputs += $agentExe }
        $missing = @($requiredOutputs | Where-Object { -not (Test-Path -LiteralPath $_) })
        if ($missing.Count -gt 0) {
            Write-Log ('== missing outputs, rebuilding: ' + (($missing | ForEach-Object { Split-Path -Leaf $_ }) -join ', ')) 'Yellow'
            $needBuild = $true
        }
        else { $needBuild = Test-NeedsBuild }
    }
}

if ($needBuild) {
    Write-Log '== build core + assertions' 'Cyan'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-core.ps1') 2>&1 |
        ForEach-Object { Write-Log "  $_" }
    if ($LASTEXITCODE -ne 0) { Show-Failure 'build failed (core/tests)'; exit 1 }

    Write-Log '== build agent' 'Cyan'
    if ($NoAgent) {
        Write-Log '  -NoAgent: skipped (the running agent holds its own binaries)' 'DarkGray'
    }
    else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-agent.ps1') -SkipCore 2>&1 |
            ForEach-Object { Write-Log "  $_" }
        if ($LASTEXITCODE -ne 0) { Show-Failure 'build failed (agent)'; exit 1 }
    }

    Write-Log '== build dashboard' 'Cyan'
    # -DevTools: the dev build carries the recording switch that keeps a dashboard on the offline state.
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-ui.ps1') -NoRun -SkipCore -DevTools 2>&1 |
        ForEach-Object { Write-Log "  $_" }
    if ($LASTEXITCODE -ne 0) { Show-Failure 'build failed (dashboard)'; exit 1 }
}
else {
    Write-Log '== build skipped (binaries newer than sources; -Build to force)' 'DarkGray'
}

# ---------------------------------------------------------------- agent ----
if (-not $NoAgent) {
    Write-Log '== start agent' 'Cyan'
    if (Test-Path -LiteralPath $agentExe) {
        Start-Process -FilePath $agentExe
        # Do not block on --status: LHM init can take seconds and the dashboard already
        # reports agent state. Just confirm the process is alive, then move on.
        Start-Sleep -Milliseconds 500
        $agent = Get-Process -Name 'QuadVitals.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $agent) { Write-Log ('  agent up (pid {0})' -f $agent.Id) 'Green' }
        else { Write-Log '  agent did not start - see %APPDATA%\QuadVitals\agent-crash.log' 'Red' }
    }
    else {
        Write-Log ('  missing ' + $agentExe) 'Red'
    }
}

# ---------------------------------------------------------------- dashboard ----
Write-Log '== open dashboard' 'Cyan'
if (-not (Test-Path -LiteralPath $uiExe)) {
    Show-Failure 'dashboard binary missing'
    exit 1
}
Start-Process -FilePath $uiExe

if ($NoWait) {
    Write-Log '  -NoWait: not waiting for the window' 'DarkGray'
    exit 0
}

$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Milliseconds 150
    $window = Get-DashboardProcess
} until ($null -ne $window -or (Get-Date) -ge $deadline)

if ($null -eq $window) {
    Show-Failure 'waiting for the dashboard window timed out (60s)'
    exit 1
}

Show-DashboardWindow $window
Write-Log ('  dashboard up (pid {0})' -f $window.Id) 'Green'
Write-Log ('log: ' + $logFile) 'DarkGray'
