# Shared harness for the WPF behaviour suites (ui-regression.ps1 / device-dialog.ps1).
#
# Dot-sourced, so the counters and every helper live in the caller's scope. One copy on purpose:
# both suites drive the same real windows, and two copies of a tree walker is how the assertions
# end up disagreeing with each other.
#
# ASCII only: PowerShell 5.1 reads BOM-less UTF-8 as GBK and would garble the source.
param(
    [string]$UiDir = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $UiDir) {
    # Default to this checkout's output folder, so the suite needs no machine-specific path.
    $UiDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'build\tmp\ui-check'
}
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

[void][Reflection.Assembly]::LoadFrom((Join-Path $UiDir 'QuadVitals.Core.dll'))
$uiAsm = [Reflection.Assembly]::LoadFrom((Join-Path $UiDir 'QuadVitals.UI.dll'))
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $UiDir 'QuadVitals.Settings.exe'))
$coreAsm = [Reflection.Assembly]::LoadFrom((Join-Path $UiDir 'QuadVitals.Core.dll'))
$windowType = $asm.GetType('QuadVitals.Settings.SettingsWindow')
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$sflags = [Reflection.BindingFlags]'NonPublic,Static'
$pubFlags = [Reflection.BindingFlags]'Public,Instance'

$fail = 0
$pass = 0

function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; "  PASS  $what $detail" } else { $script:fail++; "  FAIL  $what $detail" }
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
# A fixed Pump is not the same as "wait until the animation loop ran": on a busy machine the frame
# arrives later, so wait for the condition and fail only on timeout.
function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 3000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return $true }
        Pump 50
    }
    return [bool](& $Condition)
}
# CSS-free way to write CJK into an ASCII-only file: literal Chinese would be decoded as GBK.
function U([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }
function AutoId($element) { return [System.Windows.Automation.AutomationProperties]::GetAutomationId($element) }

# Walks the visual tree, like Find-ByTypeName: a control's template content (the dropdown list, the
# popup hosts, the overlay presenter) only exists there, and a logical walk misses controls that are
# definitely on screen.
function Find-ById($root, [string]$id) {
    if ($null -eq $root) { return $null }
    $queue = New-Object 'System.Collections.Queue'
    $queue.Enqueue($root)
    while ($queue.Count -gt 0) {
        $e = $queue.Dequeue()
        if ($e -is [System.Windows.DependencyObject] -and
            [System.Windows.Automation.AutomationProperties]::GetAutomationId($e) -eq $id) { return $e }
        if ($e -is [System.Windows.Media.Visual] -or $e -is [System.Windows.Media.Media3D.Visual3D]) {
            $kids = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
            for ($i = 0; $i -lt $kids; $i++) { $queue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
        }
        else {
            foreach ($child in [System.Windows.LogicalTreeHelper]::GetChildren($e)) { $queue.Enqueue($child) }
        }
    }
    return $null
}
function Find-ByTypeName($root, [string]$typeName) {
    $found = @()
    $queue = New-Object 'System.Collections.Queue'
    $queue.Enqueue($root)
    while ($queue.Count -gt 0) {
        $e = $queue.Dequeue()
        if ($e.GetType().Name -eq $typeName) { $found += $e }
        if ($e -is [System.Windows.Media.Visual]) {
            $kids = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
            for ($i = 0; $i -lt $kids; $i++) { $queue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
        }
    }
    return $found
}

# Same walk as Find-ById, but every match: used where the assertion is about how many copies of a
# control are on screen (palette chips) rather than where a single control ended up.
function Find-All-ById($root, [string]$id) {
    $found = @()
    if ($null -eq $root) { return $found }
    $queue = New-Object 'System.Collections.Queue'
    $queue.Enqueue($root)
    while ($queue.Count -gt 0) {
        $e = $queue.Dequeue()
        if ($e -is [System.Windows.DependencyObject] -and
            [System.Windows.Automation.AutomationProperties]::GetAutomationId($e) -eq $id) { $found += $e }
        if ($e -is [System.Windows.Media.Visual] -or $e -is [System.Windows.Media.Media3D.Visual3D]) {
            $kids = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
            for ($i = 0; $i -lt $kids; $i++) { $queue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
        }
        else {
            foreach ($child in [System.Windows.LogicalTreeHelper]::GetChildren($e)) { $queue.Enqueue($child) }
        }
    }
    return $found
}

# A single pass over a freshly built subtree sometimes reports nothing and the next pass finds the
# element, so lookups retry instead of asserting on one attempt. Retrying matters: a pumped frame
# re-renders the overlay, so pumping is only added once plain passes have failed.
function Wait-ById($root, [string]$id) {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $hit = Find-ById $root $id
        if ($null -ne $hit) { return $hit }
        if ($attempt -ge 3) { Pump 50 }
    }
    return $null
}
# Same symptom on plain member reads of the freshly loaded Core types: one evaluation of a pure
# getter comes back empty and the next one is fine. The assertion still fails if the object is
# really missing, it just is not asserted on a single read.
function Wait-Settings($window) {
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $hit = $window.CurrentSettings
        if ($null -ne $hit) { return $hit }
        Pump 50
    }
    return $null
}
# Properties go through reflection, the way every field in the suites already is: dynamic member
# access is the part that came back empty.
function Get-Setting($settings, [string]$name) {
    if ($null -eq $settings) { return $null }
    return $settings.GetType().GetProperty($name).GetValue($settings, $null)
}
function Set-Setting($settings, [string]$name, $value) {
    $settings.GetType().GetProperty($name).SetValue($settings, $value, $null)
}
# The overlay panel is what ShowOverlay just put into the host: read it from there instead of
# walking the whole window, which intermittently reports nothing about freshly shown content.
function Get-Overlay($window, [string]$typeName) {
    $presenter = $window.GetType().GetField('_overlayContent', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($window)
    if ($null -eq $presenter) { return $null }
    $content = $presenter.Content
    if (($null -eq $content) -or ($content.GetType().Name -ne $typeName)) { return $null }
    return $content
}

# Every suite ends here: one total line for the log, one count file per suite so the wrapper can
# hand the caller a single number for the whole run.
function Complete-Run([string]$suiteName) {
    ""
    "===== $pass passed, $fail failed ====="
    $countFile = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) ('build\tmp\assertion-count-' + $suiteName + '.txt')
    $countDir = Split-Path -Parent $countFile
    if (-not (Test-Path -LiteralPath $countDir)) { New-Item -ItemType Directory -Path $countDir -Force | Out-Null }
    [IO.File]::WriteAllText($countFile, [string]$pass)
    exit $fail
}

# ---- reflection accessors with a bounded retry ------------------------------------------------
# In this environment the same reflective read occasionally comes back empty and answers on the
# next attempt (same object, same arguments, no state change in between). Assertions must not fail
# on that, so every field/method/property read goes through these instead of a bare GetField().
function Reflect-Field($type, [string]$name, $flags) {
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $field = $type.GetField($name, $flags)
        if ($null -ne $field) { return $field }
    }
    return $null
}
function Reflect-Method($type, [string]$name, $flags) {
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $method = $type.GetMethod($name, $flags)
        if ($null -ne $method) { return $method }
    }
    return $null
}
function Read-Field($object, [string]$name, $flags) {
    if ($null -eq $object) { return $null }
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $field = $object.GetType().GetField($name, $flags)
        if ($null -ne $field) { return $field.GetValue($object) }
    }
    return $null
}
function Read-Property($object, [string]$name) {
    if ($null -eq $object) { return $null }
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $property = $object.GetType().GetProperty($name)
        if ($null -ne $property) { return $property.GetValue($object, $null) }
    }
    return $null
}
function Call($object, [string]$name, [object[]]$arguments) {
    $method = Reflect-Method $object.GetType() $name ([Reflection.BindingFlags]'Public,Instance')
    if ($null -eq $method) { return $null }
    return $method.Invoke($object, $arguments)
}
function Call-Private($object, [string]$name, [object[]]$arguments) {
    $method = Reflect-Method $object.GetType() $name ([Reflection.BindingFlags]'NonPublic,Instance')
    if ($null -eq $method) { return $null }
    return $method.Invoke($object, $arguments)
}
function Set-Property($object, [string]$name, $value) {
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $property = $object.GetType().GetProperty($name)
        if ($null -ne $property) { $property.SetValue($object, $value, $null); return }
    }
}
