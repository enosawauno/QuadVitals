# Device-status dialog and tile-formatting suite (overview page, the device-status card).
#
# Why it is its own file: these assertions drive an overlay panel whose content is rebuilt on every
# show, and in the element-list suite that panel kept answering "not found" for the first walk of a
# fresh subtree. Running them in their own process keeps the element-list suite from inheriting it,
# and the shared harness means both suites check the same way.
#
# ASCII only: PowerShell 5.1 reads BOM-less UTF-8 as GBK and would garble the source.
param(
    [string]$UiDir = ''
)
. (Join-Path $PSScriptRoot 'ui-harness.ps1') -UiDir $UiDir

# The overlay panel is what a button just put into the overlay host: read it from there instead of
# walking the window, which intermittently reports nothing about freshly shown content.
function Read-OverlayPanel($window, [string]$typeName) {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $presenter = Read-Field $window '_overlayContent' $flags
        $content = if ($null -eq $presenter) { $null } else { $presenter.Content }
        if (($null -ne $content) -and ($content.GetType().Name -eq $typeName)) { return $content }
        Pump 50
    }
    return $null
}
# Same retry, for the finders: the first passes over a freshly shown panel come back empty.
function Find-InPanel($root, [string]$id) {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $hit = Find-ById $root $id
        if ($null -ne $hit) { return $hit }
        Pump 50
    }
    return $null
}
function Read-Settings($window) {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $hit = $window.CurrentSettings
        if ($null -ne $hit) { return $hit }
        Pump 50
    }
    return $null
}

$ovType = $asm.GetType('QuadVitals.Settings.OverviewPage')
$ovWindow = [Activator]::CreateInstance($windowType, @('overview'))
$ovWindow.Width = 1280
$ovWindow.Height = 853
$ovRoot = $ovWindow.Content
$ovRoot.Measure((New-Object System.Windows.Size(1280, 853)))
$ovRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$ovRoot.UpdateLayout()
$ovPage = $null
$ovQueue = New-Object 'System.Collections.Queue'
$ovQueue.Enqueue($ovRoot)
while ($ovQueue.Count -gt 0 -and $null -eq $ovPage) {
    $e = $ovQueue.Dequeue()
    if ($e.GetType() -eq $ovType) { $ovPage = $e; break }
    if ($e -is [System.Windows.Media.Visual]) {
        $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
        for ($i = 0; $i -lt $n; $i++) { $ovQueue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
    }
}
Check 'the overview page is reachable' ($null -ne $ovPage) ''
if ($null -eq $ovPage) { Complete-Run 'settings-ui-device' }

"== device status dialog =="
# The device card carries the same gear as the network card: one entry point per card, same look.
$deviceGear = Find-ById $ovRoot 'DeviceSettings'
Check 'the device card has a settings gear' (($null -ne $deviceGear) -and ($deviceGear.Visibility -eq [System.Windows.Visibility]::Visible)) ''
Call $ovWindow 'ShowDeviceSettings' @() | Out-Null
Pump 350
$devicePanel = Read-OverlayPanel $ovWindow 'Border'
Check 'the gear opens a device settings panel' ($null -ne $devicePanel) ''
if ($null -ne $devicePanel) {
    $panelTitle = $null
    foreach ($block in (Find-ByTypeName $devicePanel 'TextBlock')) {
        if ($block.Text -eq (U @(0x8BBE, 0x5907, 0x72B6, 0x6001, 0x8BBE, 0x7F6E))) { $panelTitle = $block }
    }
    Check 'the panel is titled device settings' ($null -ne $panelTitle) ''

    # First field: the indicator style dropdown. Its name is four characters everywhere (dialog,
    # assertion, docs, screenshot).
    $indicator = Find-InPanel $devicePanel 'DeviceSettingsIndicator'
    Check 'the panel has the indicator style dropdown' ($null -ne $indicator) ''
    if ($null -ne $indicator) {
        $label = [System.Windows.Automation.AutomationProperties]::GetName($indicator)
        $expectedLabel = U @(0x8FDB, 0x5EA6, 0x6837, 0x5F0F)
        Check 'the dropdown is named with four characters' (($label -eq $expectedLabel) -and ($label.Length -eq 4)) "name=$label"
        Check 'the dropdown offers two indicator styles' ($indicator.Items.Count -eq 2) "items=$($indicator.Items.Count)"
        $segWord = U @(0x5206, 0x6BB5, 0x6761)
        $capsuleWord = U @(0x80F6, 0x56CA, 0x6761)
        $options = @($indicator.Items | ForEach-Object { [string]$_ })
        Check 'the options are segmented and capsule' (($options -contains $segWord) -and ($options -contains $capsuleWord)) "options=$($options -join ',')"

        # Same dropdown width as the network dialog: no fixed width, the field column stretches it.
        Call $ovWindow 'CloseOverlay' @() | Out-Null
        Pump 250
        Call $ovWindow 'ShowNetworkSettings' @() | Out-Null
        Pump 350
        $networkPanel = Read-OverlayPanel $ovWindow 'Border'
        $networkCombo = Find-InPanel $networkPanel 'NetworkAdapterSelect'
        Check 'the network dialog still has its adapter dropdown' ($null -ne $networkCombo) ''
        if (($null -ne $networkCombo) -and ($null -ne $indicator)) {
            Check 'both dialogs use the same dropdown width' ([math]::Abs($networkCombo.ActualWidth - $indicator.ActualWidth) -le 1.0) "network=$([math]::Round($networkCombo.ActualWidth,1)) device=$([math]::Round($indicator.ActualWidth,1))"
        }
        Call $ovWindow 'CloseOverlay' @() | Out-Null
        Pump 250
        Call $ovWindow 'ShowDeviceSettings' @() | Out-Null
        Pump 350
    }

    "== indicator style switch =="
    # Switching only changes the settings object and repaints; the page must not be rebuilt (a
    # rebuild would tear the open dialog down with it).
    $settings = Read-Settings $ovWindow
    $metrics = Read-Property $settings 'DeviceMetrics'
    $styleBefore = Read-Property $settings 'IndicatorStyle'
    Set-Setting $settings 'IndicatorStyle' 'capsule'
    $applied = Call $ovPage 'ApplyIndicatorStyle' @()
    $diskTile = Read-Field $ovPage '_diskTile' $flags
    $diskBar = Read-Field $diskTile '_bar' $flags
    $barStyle = Read-Property $diskBar 'BarStyle'
    Check 'capsule maps to the capsule bar style' (($applied -eq 0) -and ($barStyle -eq 0)) "applied=$applied bar=$barStyle"
    Set-Setting $settings 'IndicatorStyle' 'segments'
    $applied = Call $ovPage 'ApplyIndicatorStyle' @()
    $cpuTile = Read-Field $ovPage '_cpuTile' $flags
    $cpuBar = Read-Field $cpuTile '_bar' $flags
    $barStyle = Read-Property $cpuBar 'BarStyle'
    Check 'segments maps to the segmented bar style' (($applied -eq 1) -and ($barStyle -eq 1)) "applied=$applied bar=$barStyle"

    # Choosing a style only takes effect after the page cache is cleared and the visible page is
    # still the old instance: the switch has to land on the page on screen, not on a fresh one.
    $cache = Read-Field $ovWindow '_cache' $flags
    if ($null -ne $cache) { $cache.Clear() }
    Set-Setting $settings 'IndicatorStyle' 'capsule'
    Call $ovWindow 'ApplyIndicatorStyle' @() | Out-Null
    $shown = (Read-Field $ovWindow '_host' $flags).Content
    $shownTile = Read-Field $shown '_cpuTile' $flags
    $shownBar = Read-Field $shownTile '_bar' $flags
    $shownStyle = Read-Property $shownBar 'BarStyle'
    Check 'the switch still applies after the page cache is cleared' ($shownStyle -eq 0) "bar=$shownStyle"

    "== metric pickers =="
    # One dropdown per hardware tile: option count and "pick it and the tile follows".
    $metricIds = @{ 'cpu' = 4; 'memory' = 2; 'gpu' = 5; 'disk' = 3 }
    foreach ($hardware in $metricIds.Keys) {
        $picker = Find-InPanel $ovRoot ('DeviceMetric_' + $hardware)
        Check ("the $hardware metric picker exists") ($null -ne $picker) ''
        if ($null -ne $picker) {
            Check ("the $hardware picker lists its metrics") ($picker.Items.Count -eq $metricIds[$hardware]) "items=$($picker.Items.Count)"
        }
    }

    $tileProperties = @{ 'cpu' = 'Cpu'; 'memory' = 'MemoryUsed'; 'gpu' = 'Gpu'; 'disk' = 'Disk' }
    $tileFields = @{ 'cpu' = '_cpuTile'; 'memory' = '_memoryTile'; 'gpu' = '_gpuTile'; 'disk' = '_diskTile' }
    $renderCalls = @{ 'cpu' = 'RenderCpuTile'; 'memory' = 'RenderMemoryTile'; 'gpu' = 'RenderGpuTile'; 'disk' = 'RenderDiskTile' }
    $snapshotType = $coreAsm.GetType('QuadVitals.Core.MetricsSnapshot')
    $probeSnapshot = [Activator]::CreateInstance($snapshotType)
    $probeSnapshot.Flags = [QuadVitals.Core.MetricFlags](1 + 2 + 16 + 128 + 32)   # Cpu/Memory/Agent/Disk/Gpu valid
    $probeSnapshot.MemoryTotalBytes = 68719476736
    $probeSnapshot.MemoryUsedBytes = 34359738368
    $temperatureWord = U @(0x6E29, 0x5EA6)
    foreach ($hardware in $metricIds.Keys) {
        Set-Property $metrics $tileProperties[$hardware] 'temperature'
        Call-Private $ovPage $renderCalls[$hardware] @($probeSnapshot) | Out-Null
        $tile = Read-Field $ovPage $tileFields[$hardware] $flags
        $isTemperature = ($hardware -eq 'cpu') -or ($hardware -eq 'gpu')
        if ($isTemperature) {
            Check ("the $hardware tile follows the selected metric") ($tile.Label.EndsWith($temperatureWord)) "label=$($tile.Label)"
        }
    }
    Set-Setting $metrics 'Cpu' 'usage'
    Set-Setting $metrics 'MemoryUsed' 'usage'
    Set-Setting $metrics 'Gpu' 'usage'
    Set-Setting $metrics 'Disk' 'usage'

    "== tile formats =="
    # CPU package power: the watt figure in the snapshot (v3 reserved slot) renders as W.
    $powerSnapshot = [Activator]::CreateInstance($snapshotType)
    $powerSnapshot.Flags = [QuadVitals.Core.MetricFlags]512   # CpuPowerValid
    $powerSnapshot.CpuPowerWatts = [single]253.4
    Set-Setting $metrics 'Cpu' 'power'
    Call-Private $ovPage 'RenderCpuTile' @($powerSnapshot) | Out-Null
    $cpuTile = Read-Field $ovPage '_cpuTile' $flags
    $cpuValue = (Read-Field $cpuTile '_value' $flags).Text
    $cpuUnit = (Read-Field $cpuTile '_unit' $flags).Text
    $powerWord = U @(0x529F, 0x8017)
    Check 'the CPU tile can show package power' ($cpuTile.Label.EndsWith($powerWord)) "label=$($cpuTile.Label)"
    Check 'the power tile prints watts' (($cpuValue -eq '253.4') -and ($cpuUnit -eq 'W')) "value=$cpuValue$cpuUnit"

    # The GPU clock is printed in megahertz, in the same small unit block.
    $clockSnapshot = [Activator]::CreateInstance($snapshotType)
    $clockSnapshot.Flags = [QuadVitals.Core.MetricFlags](32 + 2048)   # GpuValid | GpuClockValid
    $clockSnapshot.GpuClockMhz = [single]2505
    Set-Setting $metrics 'Gpu' 'frequency'
    Call-Private $ovPage 'RenderGpuTile' @($clockSnapshot) | Out-Null
    $gpuTile = Read-Field $ovPage '_gpuTile' $flags
    $gpuValue = (Read-Field $gpuTile '_value' $flags).Text
    $gpuUnit = (Read-Field $gpuTile '_unit' $flags).Text
    Check 'the GPU clock is printed in megahertz' (($gpuValue -eq '2505') -and ($gpuUnit -eq 'MHz')) "value=$gpuValue$gpuUnit"
    Set-Setting $metrics 'Gpu' 'usage'

    # Units belong in the small unit block: temperature and throughput used to glue them to the
    # number, which then grew to the big font.
    $temperatureSnapshot = [Activator]::CreateInstance($snapshotType)
    $temperatureSnapshot.Flags = [QuadVitals.Core.MetricFlags]4   # TemperatureValid
    $temperatureSnapshot.TemperatureC = [single]56
    Set-Setting $metrics 'Cpu' 'temperature'
    Call-Private $ovPage 'RenderCpuTile' @($temperatureSnapshot) | Out-Null
    $cpuTile = Read-Field $ovPage '_cpuTile' $flags
    $cpuValue = (Read-Field $cpuTile '_value' $flags).Text
    $cpuUnit = (Read-Field $cpuTile '_unit' $flags).Text
    Check 'the temperature value carries no unit' ($cpuValue -match '^[0-9.]+$') "value=$cpuValue"
    Check 'the temperature unit is drawn by the unit block' ($cpuUnit -eq (U @(0x2103))) "unit=$cpuUnit"

    $ioSnapshot = [Activator]::CreateInstance($snapshotType)
    $ioSnapshot.Flags = [QuadVitals.Core.MetricFlags]128   # DiskValid
    $ioSnapshot.DiskPercent = [single]53
    $ioSnapshot.DiskIoReadBytesPerSec = [single]2097152
    $ioSnapshot.DiskIoWriteBytesPerSec = [single]1048576
    Set-Setting $metrics 'Disk' 'io'
    Call-Private $ovPage 'RenderDiskTile' @($ioSnapshot) | Out-Null
    $diskTile = Read-Field $ovPage '_diskTile' $flags
    $diskValue = (Read-Field $diskTile '_value' $flags).Text
    $diskUnit = (Read-Field $diskTile '_unit' $flags).Text
    Check 'the io value carries no unit' ($diskValue -match '^[0-9.]+$') "value=$diskValue"
    Check 'the io unit is drawn by the unit block' ($diskUnit -eq 'MB/s') "unit=$diskUnit"

    # Used/free must agree everywhere in the tile: big number, caption and bar colour cannot be
    # half "used" and half "free".
    $diskCaption = Read-Field $diskTile '_caption' $flags
    $diskBar = Read-Field $diskTile '_bar' $flags
    $diskSnapshot = [Activator]::CreateInstance($snapshotType)
    $diskSnapshot.Flags = [QuadVitals.Core.MetricFlags]128   # DiskValid
    $diskSnapshot.DiskPercent = [single]85
    $diskSnapshot.DiskUsedGb = 850
    $diskSnapshot.DiskTotalGb = 1000
    $usedWord = U @(0x5DF2, 0x7528)
    $freeWord = U @(0x53EF, 0x7528)
    Set-Setting $metrics 'Disk' 'usage'
    Call-Private $ovPage 'RenderDiskTile' @($diskSnapshot) | Out-Null
    $usedCaption = $diskCaption.Text
    $diskOnColor = Read-Field $diskBar '_onColor' $flags
    Check 'the usage caption says used' ($usedCaption.Contains($usedWord + ' 850 / 1000')) "caption=$usedCaption"
    Check 'the usage tone follows the used percentage' ($diskOnColor -eq [QuadVitals.UI.Theme]::AccentOrange) "color=$diskOnColor"

    Set-Setting $metrics 'Disk' 'free'
    Call-Private $ovPage 'RenderDiskTile' @($diskSnapshot) | Out-Null
    $freeCaption = $diskCaption.Text
    $freeValue = (Read-Field $diskTile '_value' $flags).Text
    $diskOnColor = Read-Field $diskBar '_onColor' $flags
    Check 'the free tile label says free' ($diskTile.Label.EndsWith($freeWord)) "label=$($diskTile.Label)"
    Check 'the free value is the free percentage' ($freeValue -eq '15') "value=$freeValue"
    Check 'the free caption says free' ($freeCaption.Contains($freeWord + ' 150 / 1000')) "caption=$freeCaption"
    Check 'the free tone follows the free percentage' ($diskOnColor -eq [QuadVitals.UI.Theme]::AccentGreen) "color=$diskOnColor"
    Set-Setting $metrics 'Disk' 'usage'
    Set-Setting $metrics 'Cpu' 'usage'

    Set-Setting $settings 'IndicatorStyle' $styleBefore
    Call $ovWindow 'ApplyIndicatorStyle' @() | Out-Null

    "== segmented geometry =="
    # A segment is a cylinder, not an ellipse: it fills the 22 px capsule height, and the sixteenth
    # segment ends exactly on the control width.
    $barType = $uiAsm.GetType('QuadVitals.UI.MetricBar')
    $segmentRect = Reflect-Method $barType 'SegmentRect' $sflags
    Check 'the segment geometry helper exists' ($null -ne $segmentRect) ''
    if ($null -ne $segmentRect) {
        $rectArgs = New-Object 'object[]' 3
        $rectArgs[0] = 0
        $rectArgs[1] = [double]220
        $rectArgs[2] = [double]22
        $rect = $segmentRect.Invoke($null, $rectArgs)
        Check 'a segment is as tall as the capsule bar' ([math]::Abs($rect.Height - 22) -le 0.01) "height=$($rect.Height)"
        Check 'a segment is a tall cylinder, not a squashed ellipse' ($rect.Width -lt $rect.Height) "width=$([math]::Round($rect.Width,1))"
        $lastArgs = New-Object 'object[]' 3
        $lastArgs[0] = 15
        $lastArgs[1] = [double]220
        $lastArgs[2] = [double]22
        $last = $segmentRect.Invoke($null, $lastArgs)
        $right = $last.X + $last.Width
        Check 'sixteen segments fill the width exactly' ([math]::Abs($right - 220) -le 0.6) "right=$([math]::Round($right,1))"
    }

    "== draft, save and cancel =="
    # Same contract as the network dialog: the dropdown only edits a draft, save writes it, cancel
    # throws it away.
    $devicePanelNow = Read-OverlayPanel $ovWindow 'Border'
    $saveButton = Find-InPanel $devicePanelNow 'DeviceSettingsSave'
    $cancelButton = Find-InPanel $devicePanelNow 'DeviceSettingsCancel'
    Check 'the device panel has cancel and save' (($null -ne $saveButton) -and ($null -ne $cancelButton)) ''
    Check 'the old close-only button is gone' ($null -eq (Find-ById $devicePanelNow 'DeviceSettingsClose')) ''
    $indicatorNow = Find-InPanel $devicePanelNow 'DeviceSettingsIndicator'
    $styleBeforeSave = Read-Property (Read-Settings $ovWindow) 'IndicatorStyle'
    if ($null -ne $indicatorNow) {
        $indicatorNow.SelectedIndex = if ($styleBeforeSave -eq 'segments') { 1 } else { 0 }
        $styleNow = Read-Property (Read-Settings $ovWindow) 'IndicatorStyle'
        Check 'changing the dropdown is only a draft' ($styleNow -eq $styleBeforeSave) "style=$styleNow"
    }
    if ($null -ne $cancelButton) {
        $cancelButton.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Button]::ClickEvent)))
        Pump 250
        $styleAfterCancel = Read-Property (Read-Settings $ovWindow) 'IndicatorStyle'
        Check 'cancel discards the draft' (($null -eq (Find-ById $ovRoot 'DeviceSettingsDialog')) -and ($styleAfterCancel -eq $styleBeforeSave)) ''
    }

    Call $ovWindow 'CloseOverlay' @() | Out-Null
    Pump 350
    Check 'closing the panel removes it' ($null -eq (Find-ById $ovRoot 'DeviceSettingsDialog')) ''
}
$ovWindow.Close()

Complete-Run 'settings-ui-device'
