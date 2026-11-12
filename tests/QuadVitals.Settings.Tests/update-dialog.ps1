# Update dialog (found -> download -> verify -> hand over to the installer).
#
# Own process: this suite drops the dialog into the real overlay layer, and keeping it apart from
# the element-list and device-dialog scenarios means neither inherits the other's freshly built
# windows. Assertions cover structure and wiring only (version, note count, buttons, progress
# hidden, skip closes and remembers); Present draws the dialog without touching the network.
#
# ASCII only: PowerShell 5.1 reads BOM-less UTF-8 as GBK and would garble the source.
param(
    [string]$UiDir = ''
)
. (Join-Path $PSScriptRoot 'ui-harness.ps1') -UiDir $UiDir

$titleWord = U @(0x53D1, 0x73B0, 0x65B0, 0x7248, 0x672C)          # found new version
$dateWord = U @(0x66F4, 0x65B0, 0x65F6, 0x95F4, 0xFF1A)           # updated at:
$noteOne = U @(0x7B2C, 0x4E00, 0x6761, 0x66F4, 0x65B0, 0x8BF4, 0x660E)
$noteTwo = U @(0x7B2C, 0x4E8C, 0x6761, 0x66F4, 0x65B0, 0x8BF4, 0x660E)
$noteThree = U @(0x7B2C, 0x4E09, 0x6761, 0x66F4, 0x65B0, 0x8BF4, 0x660E)

function New-Manifest([string]$version) {
    $type = $coreAsm.GetType('QuadVitals.Core.UpdateManifest')
    $manifest = [Activator]::CreateInstance($type)
    $manifest.Version = $version
    $manifest.PublishedAtUtc = [DateTime]::UtcNow
    $manifest.Notes = [string[]]@($noteOne, $noteTwo, $noteThree)
    $manifest.SetupName = 'QuadVitals-Setup-' + $version + '.exe'
    $manifest.SetupSize = 1700000
    $manifest.SetupSha256 = ('a' * 64)
    return $manifest
}

$window = [Activator]::CreateInstance($windowType, @('overview'))
$window.Width = 1280
$window.Height = 853
$root = $window.Content
$root.Measure((New-Object System.Windows.Size(1280, 853)))
$root.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$root.UpdateLayout()

$dialogType = $asm.GetType('QuadVitals.Settings.UpdateDialog')
$present = $dialogType.GetMethod('Present', [Reflection.BindingFlags]'Public,Static')
Check 'the update dialog is reachable' ($null -ne $present) ''

$script:skipped = $false
$onSkipped = [Action]{ $script:skipped = $true }
$present.Invoke($null, @($window, (New-Manifest '1.0.2'), $onSkipped)) | Out-Null
Pump 350

Check 'opening the dialog fills the overlay layer' ($window.IsOverlayOpen) ''
$panel = Get-Overlay $window 'Border'
Check 'the overlay holds the dialog panel' ($null -ne $panel) ''

$title = Find-ById $panel 'UpdateDialogTitle'
Check 'the dialog names the new version' (($null -ne $title) -and ($title.Text -eq ($titleWord + ' v1.0.2'))) ''
$date = Find-ById $panel 'UpdateDialogDate'
Check 'the dialog shows the publish date' (($null -ne $date) -and ($date.Text.StartsWith($dateWord))) ''

$notes = Find-ById $panel 'UpdateDialogNotes'
Check 'the notes area exists' ($null -ne $notes) ''
$noteTexts = @()
foreach ($block in (Find-ByTypeName $panel 'TextBlock')) {
    if (($block.Text -eq $noteOne) -or ($block.Text -eq $noteTwo) -or ($block.Text -eq $noteThree)) {
        $noteTexts += $block.Text
    }
}
Check 'every release note is listed' ($noteTexts.Count -eq 3) "count=$($noteTexts.Count)"

$install = Find-ById $panel 'UpdateDialogInstall'
$later = Find-ById $panel 'UpdateDialogLater'
$close = Find-ById $panel 'UpdateDialogClose'
Check 'the primary action is offered' (($null -ne $install) -and ($install.Visibility -eq [System.Windows.Visibility]::Visible)) ''
Check 'the skip action is offered' (($null -ne $later) -and ($later.Visibility -eq [System.Windows.Visibility]::Visible)) ''
Check 'the close affordance sits in the header' ($null -ne $close) ''
$actions = $later.Parent
Check 'the action row sits at the bottom right' (($null -ne $actions) -and ($actions.HorizontalAlignment -eq [System.Windows.HorizontalAlignment]::Right)) ''
Check 'skip comes before install' (($null -ne $actions) -and
    ($actions.Children.IndexOf($later) -lt $actions.Children.IndexOf($install))) ''
$progress = Find-ById $panel 'UpdateDialogProgress'
Check 'the progress bar stays hidden before downloading' (($null -ne $progress) -and ($progress.Visibility -eq [System.Windows.Visibility]::Collapsed)) ''

# Skip must close the dialog AND remember the version: closing alone would prompt again next open.
if ($null -ne $later) {
    $later.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Primitives.ButtonBase]::ClickEvent)))
    Pump 250
}
Check 'skip closes the dialog' (-not $window.IsOverlayOpen) ''
Check 'skip reports the version to remember' ($script:skipped) ''

# Reopen once: a closed dialog must be usable again, not left in a finished state.
$script:skipped = $false
$present.Invoke($null, @($window, (New-Manifest '1.0.3'), $onSkipped)) | Out-Null
Pump 350
$title = Find-ById (Get-Overlay $window 'Border') 'UpdateDialogTitle'
Check 'the dialog can be shown again after closing' (($null -ne $title) -and ($title.Text -eq ($titleWord + ' v1.0.3'))) ''
$window.CloseOverlay()

Complete-Run 'settings-ui-update'
