# Behaviour regression suite for the installer / uninstaller wizard.
#
# Why this file exists: every rule below was broken at least once, and the only check back then was a
# throwaway --preview screenshot under build\tmp - which nothing ever ran. So each rule is now one
# assertion against the real WPF code (the same methods the buttons call), and scripts\verify.ps1 runs
# it on every pre-push. Delete or weaken an assertion only together with the rule it protects.
#
# ASCII only, on purpose: PS 5.1 reads a BOM-less UTF-8 file as GBK, and a misread multi-byte comment can
# even swallow the next line. UI strings are therefore built from code points (see the $S_ table).
#
#   powershell -File tests\QuadVitals.Setup.Tests\setup-regression.ps1 -SetupDir build\tmp\setup-check
param(
    [string]$SetupDir = ''
)
$ErrorActionPreference = 'Stop'
if (-not $SetupDir) {
    # Default to this checkout's output folder, so the suite needs no machine-specific path.
    $SetupDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'build\tmp\setup-check'
}
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$asm = [Reflection.Assembly]::LoadFrom((Join-Path $SetupDir 'QuadVitals.Setup.dll'))
$windowType = $asm.GetType('QuadVitals.Setup.SetupWindow')
$coreType = $asm.GetType('QuadVitals.Setup.InstallerCore')
$flags = [Reflection.BindingFlags]'NonPublic,Instance'

$fail = 0
$pass = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; "  PASS  $what $detail" } else { $script:fail++; "  FAIL  $what $detail" }
}

# UI text without literals.
function Str([int[]]$codes) { return (-join ($codes | ForEach-Object { [char]$_ })) }
$S_ADMIN_GRANT      = Str 0x7BA1,0x7406,0x5458,0x6388,0x6743                     # admin authorisation
$S_WILL_CLOSE       = Str 0x4F1A,0x5148,0x5173,0x95ED                            # "will close first"
$S_DOWNLOADED       = Str 0x7F51,0x7EDC,0x4E0B,0x8F7D                            # "downloaded from the web"
$S_RUN_AS_ADMIN     = Str 0x4EE5,0x7BA1,0x7406,0x5458,0x6743,0x9650,0x8FD0,0x884C  # run as administrator
$S_KEEP_SETTINGS    = Str 0x4FDD,0x7559,0x6211,0x7684,0x8BBE,0x7F6E              # keep my settings
$S_START_AFTER      = Str 0x5B89,0x88C5,0x5B8C,0x6210,0x540E,0x7ACB,0x5373,0x542F,0x52A8
$S_DESKTOP_LNK      = Str 0x5728,0x684C,0x9762,0x521B,0x5EFA,0x5FEB,0x6377,0x65B9,0x5F0F
$S_NO_ADMIN_PICKED  = Str 0x672A,0x52FE,0x9009,0x7BA1,0x7406,0x5458,0x6743,0x9650  # "admin was not ticked"
$S_CANCELLED_INSTALL   = Str 0x5DF2,0x53D6,0x6D88,0x5B89,0x88C5                  # install cancelled
$S_CANCELLED_UNINSTALL = Str 0x5DF2,0x53D6,0x6D88,0x5378,0x8F7D                  # uninstall cancelled
$S_TASKMGR          = Str 0x6253,0x5F00,0x4EFB,0x52A1,0x7BA1,0x7406,0x5668       # open task manager
$S_DONE             = Str 0x5B8C,0x6210                                          # done
$S_OPEN_DASHBOARD   = Str 0x6253,0x5F00,0x4EEA,0x8868,0x76D8                     # open dashboard
$S_REINSTALL        = Str 0x91CD,0x65B0,0x5B89,0x88C5                            # reinstall
$S_REUNINSTALL      = Str 0x91CD,0x65B0,0x5378,0x8F7D                            # re-uninstall

function New-Win([bool]$uninstall) {
    $ctor = $windowType.GetConstructor([type[]]@([string], [bool], [string]))
    return $ctor.Invoke(@('0.9.0', $uninstall, $null))
}
function Host($win) { return $windowType.GetField('_host', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($win) }
function Core($win) { return $windowType.GetField('_core', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($win) }

function Walk($root) {
    $items = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)
    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        if ($node -ne $null) { [void]$items.Add($node) }
        $count = 0
        try { $count = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($node) } catch { $count = 0 }
        for ($i = 0; $i -lt $count; $i++) {
            $stack.Push([System.Windows.Media.VisualTreeHelper]::GetChild($node, $i))
        }
    }
    return $items
}

function Texts($root) {
    $out = New-Object System.Collections.ArrayList
    foreach ($node in (Walk $root)) {
        if ($node -is [System.Windows.Controls.TextBlock]) { [void]$out.Add([string]$node.Text) }
        elseif ($node -is [System.Windows.Controls.ContentControl] -and $node.Content -is [string]) {
            [void]$out.Add([string]$node.Content)
        }
    }
    return $out
}

function CheckBoxes($root) { return @(Walk $root | Where-Object { $_.GetType().Name -like '*CheckBox*' }) }
function Buttons($root) { return @(Walk $root | Where-Object { $_ -is [System.Windows.Controls.Button] }) }

function Find-Button($root, [string]$label) {
    foreach ($button in (Buttons $root)) {
        if ($button.Content -is [string] -and $button.Content -eq $label) { return $button }
    }
    return $null
}

function Has-Text($root, [string]$needle) {
    foreach ($text in (Texts $root)) {
        if ($text -ne $null -and $text.Contains($needle)) { return $true }
    }
    return $false
}

function Pump([int]$ms) {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds($ms)
    $handler = { $timer.Stop(); $frame.Continue = $false }
    $timer.Add_Tick($handler)
    $timer.Start()
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
    $timer.Remove_Tick($handler)
}

function Close-Fired($win, [scriptblock]$action) {
    $script:closed = $false
    $handler = { param($s, $e) $script:closed = $true }
    $win.Add_Closing($handler)
    & $action
    $closed = $script:closed
    $win.Remove_Closing($handler)
    return $closed
}

function Click($button) {
    $button.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Primitives.ButtonBase]::ClickEvent)))
}

# ---- welcome page: no prose, no admin option -------------------------------------------------

$win = New-Win $false
$host1 = Host $win
$boxes = CheckBoxes $host1
Check 'install: two check boxes' ($boxes.Count -eq 2) "(got $($boxes.Count))"
Check 'install: start-after-install box' (($boxes | Where-Object { ([string]$_.Content).Contains($S_START_AFTER) }).Count -eq 1) ''
Check 'install: desktop-shortcut box' (($boxes | Where-Object { ([string]$_.Content).Contains($S_DESKTOP_LNK) }).Count -eq 1) ''
Check 'install: no admin check box' (($boxes | Where-Object { ([string]$_.Content).Contains($S_RUN_AS_ADMIN) }).Count -eq 0) ''
Check 'install: no admin-authorisation line' (-not (Has-Text $host1 $S_ADMIN_GRANT)) ''
Check 'install: no will-close line' (-not (Has-Text $host1 $S_WILL_CLOSE)) ''
Check 'install: no download/SmartScreen line' (-not (Has-Text $host1 $S_DOWNLOADED)) ''

$win2 = New-Win $true
$host2 = Host $win2
$boxes2 = CheckBoxes $host2
Check 'uninstall: one check box' ($boxes2.Count -eq 1) "(got $($boxes2.Count))"
Check 'uninstall: keep-settings box' (($boxes2 | Where-Object { ([string]$_.Content).Contains($S_KEEP_SETTINGS) }).Count -eq 1) ''
Check 'uninstall: no admin-authorisation line' (-not (Has-Text $host2 $S_ADMIN_GRANT)) ''

# ---- finish page: both exits close the window ------------------------------------------------

$showResult = $windowType.GetMethod('ShowResult', [Reflection.BindingFlags]'NonPublic,Instance')
[string]$emptyDir = Join-Path $env:TEMP 'qv-suite-no-install'
[string]$installDirArg = $emptyDir

$win3 = New-Win $false
$showResult.Invoke($win3, [object[]]@($installDirArg)) | Out-Null
$host3 = Host $win3
Check 'finish: no "admin was not picked" warning' (-not (Has-Text $host3 $S_NO_ADMIN_PICKED)) ''
Check 'finish: no task-manager button' ((Find-Button $host3 $S_TASKMGR) -eq $null) ''

$done = Find-Button $host3 $S_DONE
Check 'finish: done button exists' ($done -ne $null) ''
Check 'finish: done closes the window' (Close-Fired $win3 { Click $done }) ''

$win4 = New-Win $false
$showResult.Invoke($win4, [object[]]@($installDirArg)) | Out-Null
$host4 = Host $win4
$dashboard = Find-Button $host4 $S_OPEN_DASHBOARD
Check 'finish: dashboard button exists' ($dashboard -ne $null) ''
Check 'finish: dashboard closes the window' (Close-Fired $win4 { Click $dashboard }) ''

# ---- failure page: wording per flow, task manager only when the stop step failed -------------

$core = Core (New-Win $true)
$feed = $coreType.GetMethod('FeedPreviewLine', [Reflection.BindingFlags]'NonPublic,Instance')
$describe = $coreType.GetMethod('DescribeFailure', [Reflection.BindingFlags]'NonPublic,Instance')
Check 'core: no stop-failure before any marker' (-not $core.StopFailed) ''
$feed.Invoke($core, @('QV-STOP-FAILED: QuadVitals.Agent (pid 4212)')) | Out-Null
Check 'core: stop-failure marker detected' ($core.StopFailed) ''
$textUninstall = [string]$describe.Invoke($core, @(1, $true))
Check 'core: uninstall failure names the process' ($textUninstall.Contains('QuadVitals.Agent (pid 4212)')) $textUninstall
Check 'core: uninstall failure says re-uninstall' ($textUninstall.Contains($S_REUNINSTALL)) ''
Check 'core: uninstall failure does not say reinstall' (-not $textUninstall.Contains($S_REINSTALL)) ''

$win5 = New-Win $false
$showFailure = $windowType.GetMethod('ShowFailure', [Reflection.BindingFlags]'NonPublic,Instance')
$feed5 = $coreType.GetMethod('FeedPreviewLine', [Reflection.BindingFlags]'NonPublic,Instance')
$feed5.Invoke((Core $win5), @('QV-STOP-FAILED: QuadVitals.Agent (pid 4212)')) | Out-Null
$showFailure.Invoke($win5, [object[]]@([string]'x', [bool]$true)) | Out-Null
$host5 = Host $win5
Check 'failure: task-manager button when stop failed' ((Find-Button $host5 $S_TASKMGR) -ne $null) ''
Check 'failure: retry button present' ((Find-Button $host5 $S_REINSTALL) -ne $null) ''

# ---- cancel page: message per flow, and the window closes itself -----------------------------

$win6 = New-Win $false
$cancel = $windowType.GetMethod('CancelWork', [Reflection.BindingFlags]'NonPublic,Instance')
$host6 = Host $win6
$script:closing = $false
$handler6 = { param($s, $e) $script:closing = $true }
$win6.Add_Closing($handler6)
$cancel.Invoke($win6, @()) | Out-Null
Check 'cancel: says install was cancelled' (Has-Text $host6 $S_CANCELLED_INSTALL) ''
Check 'cancel: exit code 0' ($win6.ExitCode -eq 0) ''
Pump 2000
Check 'cancel: window closes by itself' ($script:closing) ''
$win6.Remove_Closing($handler6)

$win7 = New-Win $true
$cancel.Invoke($win7, @()) | Out-Null
Check 'cancel: says uninstall was cancelled' (Has-Text (Host $win7) $S_CANCELLED_UNINSTALL) ''

# ---- the removed layers must stay removed ----------------------------------------------------

foreach ($name in @('ShowCloseFailedPage', 'BeginWork', 'ShowPreflightPage', 'WillRunElevated', 'ConfirmCloseAndStart')) {
    Check "structure: $name is gone" ($windowType.GetMethod($name, [Reflection.BindingFlags]'NonPublic,Instance') -eq $null) ''
}
foreach ($field in @('_createTask', '_logonTaskChosen', '_internetMarked')) {
    Check "structure: $field is gone" ($windowType.GetField($field, [Reflection.BindingFlags]'NonPublic,Instance') -eq $null) ''
}

# ---- install folder guard ---------------------------------------------------------------------
# The folder is user input, gets spliced into an elevated command line and is deleted recursively on
# uninstall, so the validator has to refuse roots, system folders and folders that contain one.
$validate = $coreType.GetMethod('ValidateInstallDir', [Reflection.BindingFlags]'Public,Static')
$isInstall = $coreType.GetMethod('IsInstallFolder', [Reflection.BindingFlags]'Public,Static')
Check 'the install folder validator exists' (($null -ne $validate) -and ($null -ne $isInstall)) ''

function Try-Dir([string]$path) {
    $args = New-Object 'object[]' 2
    $args[0] = $path
    $args[1] = $null
    $result = $validate.Invoke($null, $args)
    return [pscustomobject]@{ Path = $result; Error = [string]$args[1] }
}

Check 'an empty folder is refused' ((Try-Dir '').Path -eq $null) ''
Check 'a drive root is refused' ((Try-Dir ($env:SystemDrive + '\')).Path -eq $null) ''
Check 'a folder inside Windows is refused' ((Try-Dir (Join-Path $env:SystemRoot 'QuadVitals')).Path -eq $null) ''
Check 'a folder inside Program Files is refused' ((Try-Dir (Join-Path $env:ProgramFiles 'QuadVitals')).Path -eq $null) ''
Check 'the user profile itself is refused' ((Try-Dir $env:USERPROFILE).Path -eq $null) ''
Check 'a folder that contains the user profile is refused' ((Try-Dir (Split-Path -Parent $env:USERPROFILE)).Path -eq $null) ''
Check 'a path with a quote is refused' ((Try-Dir ('D:\Apps\Quad' + [char]0x22 + 'Vitals')).Path -eq $null) ''
$good = Try-Dir (Join-Path $env:LOCALAPPDATA 'QuadVitals')
Check 'the default install folder is accepted' ($good.Path -ne $null) "error=$($good.Error)"
Check 'the accepted folder is normalised without a trailing separator' ((Try-Dir ((Join-Path $env:LOCALAPPDATA 'QuadVitals') + '\')).Path -notmatch '\\$') ''
$custom = Try-Dir ($env:SystemDrive + '\Apps\QuadVitals\')
Check 'a custom folder is accepted and trimmed' (($custom.Path -ne $null) -and (-not $custom.Path.EndsWith('\'))) "path=$($custom.Path)"

# The folder also has to look like this product's: a valid path alone must not be deleted recursively.
$probeDir = Join-Path $env:TEMP ('qv-guard-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
try {
    Check 'an empty folder is not an install folder' ($isInstall.Invoke($null, [object[]]@([string]$probeDir)) -eq $false) ''
    New-Item -ItemType File -Path (Join-Path $probeDir 'QuadVitals.UI.dll') -Force | Out-Null
    Check 'a folder with the product files counts as an install folder' ($isInstall.Invoke($null, [object[]]@([string]$probeDir)) -eq $true) ''
}
finally {
    Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue
}

$win.Close()
$win2.Close()
$win5.Close()
$win7.Close()

Write-Host ''
Write-Host ("setup-regression: {0} passed, {1} failed" -f $pass, $fail)

# The assertion floor is recorded here so a suite cannot quietly lose a case: numbers may go up,
# never down.
$countFile = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'build\tmp\assertion-count-setup.txt'
$countDir = Split-Path -Parent $countFile
if (-not (Test-Path -LiteralPath $countDir)) { New-Item -ItemType Directory -Path $countDir -Force | Out-Null }
[IO.File]::WriteAllText($countFile, [string]$pass)

exit $fail
