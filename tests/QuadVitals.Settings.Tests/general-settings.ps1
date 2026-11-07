# General settings dialog suite (title-bar gear panel).
#
# Why it is its own file: the element-list suite crossed 1800 lines, and this panel is a self-contained
# surface (a gear overlay with its own rows and groups) that only needs one window. The shared harness
# keeps the assertions identical to the other suites.
#
# ASCII only: PowerShell 5.1 reads BOM-less UTF-8 as GBK and would garble the source.
param(
    [string]$UiDir = ''
)
. (Join-Path $PSScriptRoot 'ui-harness.ps1') -UiDir $UiDir

# --- general settings: a title-bar gear panel, not a sidebar page -------------------------------
function Read-Settings($window) {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $hit = Read-Property $window 'CurrentSettings'
        if ($null -ne $hit) { return $hit }
        Pump 50
    }
    return $null
}

$generalWindow = [Activator]::CreateInstance($windowType, @('overview'))
$generalWindow.Width = 1280
$generalWindow.Height = 853
$generalRoot = $generalWindow.Content
$generalRoot.Measure((New-Object System.Windows.Size(1280, 853)))
$generalRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$generalRoot.UpdateLayout()
$gearPill = Find-ById $generalRoot 'GeneralPill'
Check 'the title bar carries the general gear' ($null -ne $gearPill) ''
$feedbackPill = Find-ById $generalRoot 'FeedbackPill'
if (($null -ne $gearPill) -and ($null -ne $feedbackPill)) {
    $gearX = $gearPill.TransformToAncestor($generalRoot).Transform((New-Object System.Windows.Point(0, 0))).X
    $feedbackX = $feedbackPill.TransformToAncestor($generalRoot).Transform((New-Object System.Windows.Point(0, 0))).X
    Check 'the gear sits left of the feedback entry' ($gearX -lt $feedbackX) ("gear=" + [math]::Round($gearX) + " feedback=" + [math]::Round($feedbackX))
}
else { Check 'the feedback entry is still in the title bar' $false '' }
# The four icons are one group: equal pitch, and their ink centres on the same line (the glyphs are
# drawn in 17 px canvases, so a shape that is not centred in its canvas shows up as an offset here).
$captionIds = @('GeneralPill', 'FeedbackPill', 'CaptionMinimize', 'CaptionClose')
$captionButtons = @()
foreach ($id in $captionIds) { $captionButtons += (Find-ById $generalRoot $id) }
if (($captionButtons | Where-Object { $null -eq $_ }).Count -eq 0) {
    $xs = @()
    foreach ($b in $captionButtons) { $xs += $b.TransformToAncestor($generalRoot).Transform((New-Object System.Windows.Point(0, 0))).X }
    $pitch = @()
    for ($i = 1; $i -lt $xs.Count; $i++) { $pitch += [math]::Round($xs[$i] - $xs[$i - 1]) }
    Check 'the four caption buttons sit on one pitch' (($pitch | Select-Object -Unique).Count -eq 1) ("pitch=" + ($pitch -join ','))
    Check 'the caption buttons are tight' ($pitch[0] -le 32) ("pitch=" + $pitch[0])

    $barShot = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(1280, 853, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $barShot.Render($generalRoot)
    $stride = 1280 * 4
    $buf = New-Object byte[] ($stride * 853)
    $barShot.CopyPixels($buf, $stride, 0)
    $centres = @()
    foreach ($b in $captionButtons) {
        $p = $b.TransformToAncestor($generalRoot).Transform((New-Object System.Windows.Point(0, 0)))
        $y0 = [int][math]::Round($p.Y); $y1 = [int][math]::Round($p.Y + $b.ActualHeight)
        $x0 = [int][math]::Round($p.X); $x1 = [int][math]::Round($p.X + $b.ActualWidth)
        $minY = 999; $maxY = -1
        for ($y = $y0; $y -lt $y1; $y++) {
            for ($x = $x0; $x -lt $x1; $x++) {
                $i = $y * $stride + $x * 4
                # #1A1A1A title bar; anything brighter inside the button box is the glyph.
                if (($buf[$i] + $buf[$i + 1] + $buf[$i + 2]) -gt 108) {
                    if ($y -lt $minY) { $minY = $y }
                    if ($y -gt $maxY) { $maxY = $y }
                    break
                }
            }
        }
        if ($maxY -ge 0) { $centres += [math]::Round(($minY + $maxY) / 2, 2) }
    }
    $spread = ($centres | Measure-Object -Minimum -Maximum)
    Check 'the four caption icons share one ink centre' (($centres.Count -eq 4) -and (($spread.Maximum - $spread.Minimum) -le 1.0)) ("centres=" + ($centres -join ','))
}
$windowType.GetMethod('ShowGeneralSettings', $pubFlags).Invoke($generalWindow, @()) | Out-Null
Pump 200
Check 'the gear opens a panel over the window' ([bool]$windowType.GetProperty('IsOverlayOpen').GetValue($generalWindow)) ''
$generalPanel = Find-ById $generalRoot 'GeneralSettingsPanel'
Check 'the panel is the general settings panel' ($null -ne $generalPanel) ''
if ($null -ne $generalPanel) {
    # Same board as the network / device gear dialogs: more settings are coming here, so the panel
    # is sized once instead of growing a row at a time.
    # A short settings list does not take the 640x427 board the form dialogs use: the height follows
    # the content, and the width matches the sensor panel instead of leaving half a sheet empty.
    Check 'the panel is a content-fitted 460 sheet' (([math]::Abs($generalPanel.ActualWidth - 460) -le 0.5) -and ($generalPanel.ActualHeight -ge 240) -and ($generalPanel.ActualHeight -le 330)) ("size=" + [math]::Round($generalPanel.ActualWidth) + "x" + [math]::Round($generalPanel.ActualHeight))
    # The scheduler hint under the switch was removed on request: the switch only stores a
    # preference, and the install-time script name is not something the dialog should carry.
    $hintLines = 0
    foreach ($tb in (Find-ByTypeName $generalPanel 'TextBlock')) {
        if ($tb.Text -like '*install-agent-task*') { $hintLines++ }
    }
    Check 'the panel carries no scheduler hint line' ($hintLines -eq 0) "hint=$hintLines"
}
$autoStart = Find-ById $generalRoot 'GeneralAutostart'
Check 'the panel carries the autostart switch' ($null -ne $autoStart) ''
if ($null -ne $autoStart) {
    $saved = [bool](Read-Settings $generalWindow).StartAgentWithWindows
    Check 'the switch reflects the saved preference' ($autoStart.IsChecked -eq $saved) ("checked=" + $autoStart.IsChecked + " saved=" + $saved)
}
$batterySwitch = Find-ById $generalRoot 'GeneralBatteryAnimations'
Check 'the panel carries the battery animation switch' ($null -ne $batterySwitch) ''
if ($null -ne $batterySwitch) {
    $savedBattery = (Read-Settings $generalWindow).PauseAnimationsOnBattery -ne $false
    Check 'the battery switch reflects the saved preference' (($batterySwitch.IsChecked -eq $true) -eq $savedBattery) ("checked=" + $batterySwitch.IsChecked + " saved=" + $savedBattery)
}
$renderPolicy = Find-ById $generalRoot 'GeneralRenderPolicy'
Check 'the panel carries the render policy dropdown' ($null -ne $renderPolicy) ''
if ($null -ne $renderPolicy) {
    Check 'the render policy offers three choices' ($renderPolicy.Items.Count -eq 3) ("items=" + $renderPolicy.Items.Count)
    $savedPolicy = [string](Read-Settings $generalWindow).RenderPolicy
    $expectedIndex = 0
    if ($savedPolicy -eq [QuadVitals.Core.Settings]::RenderPolicyUnfocused) { $expectedIndex = 1 }
    elseif ($savedPolicy -eq [QuadVitals.Core.Settings]::RenderPolicyFullscreen) { $expectedIndex = 2 }
    Check 'the render policy reflects the saved choice' ($renderPolicy.SelectedIndex -eq $expectedIndex) ("index=" + $renderPolicy.SelectedIndex + " saved=" + $savedPolicy)
    Check 'the render policy has a label' ($null -ne (Find-ById $generalRoot 'GeneralRenderPolicyLabel')) ''

# Settings are grouped: a four-character group title, then one line per row with the label on the
# left and the control right next to it; the battery switch belongs to the performance group.
$groupStart = Find-ById $generalRoot 'GeneralGroupStart'
$groupPerf = Find-ById $generalRoot 'GeneralGroupPerformance'
$startText = -join [char[]](0x542F, 0x52A8, 0x8BBE, 0x7F6E)
$perfText = -join [char[]](0x6027, 0x80FD, 0x8BBE, 0x7F6E)
Check 'the panel is split into two named groups' (($null -ne $groupStart) -and ($null -ne $groupPerf) -and ($groupStart.Text -eq $startText) -and ($groupPerf.Text -eq $perfText)) ("start=" + $(if ($null -eq $groupStart) { 'none' } else { $groupStart.Text }) + " perf=" + $(if ($null -eq $groupPerf) { 'none' } else { $groupPerf.Text }))
Check 'every group title is four characters' (($null -ne $groupStart) -and ($null -ne $groupPerf) -and ($groupStart.Text.Length -eq 4) -and ($groupPerf.Text.Length -eq 4)) ''

function Row-Centre($element) {
    if ($null -eq $element) { return $null }
    $top = $element.TranslatePoint((New-Object System.Windows.Point(0, 0)), $generalPanel).Y
    return $top + ($element.ActualHeight / 2)
}
$autostartLabel = Find-ById $generalRoot 'GeneralAutostartLabel'
$batteryLabel = Find-ById $generalRoot 'GeneralBatteryAnimationsLabel'
$policyLabel = Find-ById $generalRoot 'GeneralRenderPolicyLabel'
$labelLengths = @($autostartLabel, $batteryLabel, $policyLabel) | ForEach-Object { if ($null -eq $_) { -1 } else { $_.Text.Length } }
Check 'every row label is four characters' (($labelLengths -join ',') -eq '4,4,4') ("lengths=" + ($labelLengths -join ','))
$rowPairs = @(@($autostartLabel, $autoStart), @($batteryLabel, $batterySwitch), @($policyLabel, $renderPolicy))
$oneLine = $true
foreach ($pair in $rowPairs) {
    $c1 = Row-Centre $pair[0]
    $c2 = Row-Centre $pair[1]
    if (($null -eq $c1) -or ($null -eq $c2) -or ([math]::Abs($c1 - $c2) -gt 2)) { $oneLine = $false }
}
Check 'each row keeps its label and control on one line' $oneLine ''
$yStart = Row-Centre $groupStart
$yPerf = Row-Centre $groupPerf
$yBattery = Row-Centre $batteryLabel
$yAuto = Row-Centre $autostartLabel
Check 'the battery row sits under the performance group' (($null -ne $yStart) -and ($null -ne $yPerf) -and ($null -ne $yAuto) -and ($null -ne $yBattery) -and ($yAuto -gt $yStart) -and ($yBattery -gt $yPerf) -and ($yPerf -gt $yAuto)) ("start=$yStart auto=$yAuto perf=$yPerf battery=$yBattery")

function Left-Edge($element) {
    if ($null -eq $element) { return -999 }
    return $element.TranslatePoint((New-Object System.Windows.Point(0, 0)), $generalPanel).X
}
$labelXs = @((Left-Edge $autostartLabel), (Left-Edge $batteryLabel), (Left-Edge $policyLabel))
$labelSpread = ($labelXs | Measure-Object -Minimum -Maximum)
Check 'the row labels share one left edge' (($labelSpread.Maximum - $labelSpread.Minimum) -le 1) ("xs=" + ($labelXs -join ','))
# Checkbox rows put the box first, so the two switches share one left edge and their labels start
# after it; the dropdown row's label starts on the same vertical line as those labels.
$checkXs = @((Left-Edge $autoStart), (Left-Edge $batterySwitch))
$checkSpread = ($checkXs | Measure-Object -Minimum -Maximum)
Check 'the two checkboxes share one left edge' (($checkSpread.Maximum - $checkSpread.Minimum) -le 1) ("xs=" + ($checkXs -join ','))
Check 'each checkbox sits left of its label' (($checkXs[0] -lt (Left-Edge $autostartLabel)) -and ($checkXs[1] -lt (Left-Edge $batteryLabel)) -and ((Left-Edge $autostartLabel) -eq (Left-Edge $policyLabel))) ("box=" + ($checkXs -join ',') + " labels=" + (Left-Edge $autostartLabel))
$autoHint = Find-ById $generalRoot 'GeneralAutostartHint'
$batteryHint = Find-ById $generalRoot 'GeneralBatteryAnimationsHint'
Check 'every checkbox row carries a short hint' (($null -ne $autoHint) -and ($null -ne $batteryHint) -and ($autoHint.Text.Length -gt 0) -and ($batteryHint.Text.Length -gt 0) -and ($autoHint.Text.Length -le 20) -and ($batteryHint.Text.Length -le 20)) ("auto=" + $(if ($null -eq $autoHint) { 'none' } else { $autoHint.Text }) + " battery=" + $(if ($null -eq $batteryHint) { 'none' } else { $batteryHint.Text }))
Check 'the hint follows its label on the same line' (($null -ne $autoHint) -and ((Left-Edge $autoHint) -gt (Left-Edge $autostartLabel))) ''
Check 'each group header carries a separating rule' ((($null -ne (Find-ById $generalRoot 'GeneralGroupStartRule')) -and ($null -ne (Find-ById $generalRoot 'GeneralGroupPerformanceRule')))) ''
Check 'the group header is smaller and heavier than a row label' (($null -ne $groupStart) -and ($null -ne $autostartLabel) -and ($groupStart.FontSize -lt $autostartLabel.FontSize) -and ($groupStart.FontWeight -eq [System.Windows.FontWeights]::SemiBold)) ''
# 短列表不留空白：下拉撑满剩余宽度，右边缘与面板内边距对齐。
$policyRight = -999
if (($null -ne $renderPolicy) -and ($null -ne $generalPanel)) {
    $edge = $renderPolicy.TransformToAncestor($generalPanel).Transform((New-Object System.Windows.Point($renderPolicy.ActualWidth, 0)))
    $policyRight = [math]::Round($edge.X, 2)
}
$panelRight = $generalPanel.ActualWidth - 20
Check 'the dropdown fills the rest of its row' ([math]::Abs($policyRight - $panelRight) -le 1) ("right=$policyRight panel=$panelRight")
Check 'the rows sit close to each other' (([math]::Abs(($yPerf - $yAuto)) -le 44) -and ([math]::Abs(($yBattery - $yPerf)) -le 44)) ("auto=$yAuto perf=$yPerf battery=$yBattery")
}
Check 'the panel no longer carries the spacing box' ($null -eq (Find-ById $generalRoot 'GeneralSpacingBox')) ''
# 面板是“取消 / 保存”：勾选只是草稿，点保存才写盘。
# The first walk over a freshly shown overlay sometimes returns nothing: retry like the other suites.
$generalSave = $null
$generalClose = $null
for ($attempt = 0; $attempt -lt 8; $attempt++) {
    if ($null -eq $generalSave) { $generalSave = Find-ById $generalRoot 'GeneralSave' }
    if ($null -eq $generalClose) { $generalClose = Find-ById $generalRoot 'GeneralClose' }
    if (($null -ne $generalSave) -and ($null -ne $generalClose)) { break }
    Pump 50
}
Check 'the panel has cancel and save buttons' (($null -ne $generalSave) -and ($null -ne $generalClose)) ''
if (($null -ne $generalSave) -and ($null -ne $autoStart) -and ($null -ne $batterySwitch) -and ($null -ne $renderPolicy)) {
    $before = [bool](Read-Settings $generalWindow).StartAgentWithWindows
    $beforeBattery = (Read-Settings $generalWindow).PauseAnimationsOnBattery -ne $false
    $beforePolicy = [string](Read-Settings $generalWindow).RenderPolicy
    $targetIndex = 2
    $targetPolicy = [QuadVitals.Core.Settings]::RenderPolicyFullscreen
    if ($beforePolicy -eq $targetPolicy) {
        $targetIndex = 1
        $targetPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
    }
    $autoStart.IsChecked = -not $before
    $batterySwitch.IsChecked = -not $beforeBattery
    $renderPolicy.SelectedIndex = $targetIndex
    Pump 150
    Check 'toggling the switch alone changes nothing on disk' ([bool](Read-Settings $generalWindow).StartAgentWithWindows -eq $before) ''
    Check 'choosing a render policy alone changes nothing on disk' ([string](Read-Settings $generalWindow).RenderPolicy -eq $beforePolicy) ''
    $generalSave.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Button]::ClickEvent)))
    Pump 200
    Check 'saving applies the draft' ([bool](Read-Settings $generalWindow).StartAgentWithWindows -eq (-not $before)) ''
    Check 'saving applies the battery preference' ((Read-Settings $generalWindow).PauseAnimationsOnBattery -eq (-not $beforeBattery)) ''
    Check 'saving applies the render policy' ([string](Read-Settings $generalWindow).RenderPolicy -eq $targetPolicy) ''
    # Other settings windows in this process have their own 1 Hz policy tick and share the same
    # process-wide environment; apply this window's just-saved value right before reading it.
    $windowType.GetMethod('ApplyRenderPolicy', $flags).Invoke($generalWindow, @()) | Out-Null
    $envType = $uiAsm.GetType('QuadVitals.UI.RenderEnvironment')
    Check 'saving applies the fullscreen gate' (([bool]$envType.GetProperty('PauseInFullscreen').GetValue($null)) -eq ($targetPolicy -eq [QuadVitals.Core.Settings]::RenderPolicyFullscreen)) ''
    Check 'saving applies the unfocused gate' (([bool]$envType.GetProperty('PauseWhenUnfocused').GetValue($null)) -eq ($targetPolicy -eq [QuadVitals.Core.Settings]::RenderPolicyUnfocused)) ''
    Check 'saving closes the panel' (-not [bool]$windowType.GetProperty('IsOverlayOpen').GetValue($generalWindow)) ''
    # 还原这次写入的设置：直接改当前设置对象再写盘，不走 Clone（这一步在门禁机上读到过空对象）。
    $restore = $null
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $restore = Read-Property $generalWindow 'CurrentSettings'
        if ($null -ne $restore) { break }
        Pump 50
    }
    $restore.StartAgentWithWindows = $before
    $restore.PauseAnimationsOnBattery = $beforeBattery
    $restore.RenderPolicy = $beforePolicy
    $restore.Normalize()
    $windowType.GetMethod('SaveSettingsQuietly').Invoke($generalWindow, @($restore)) | Out-Null
}
$generalWindow.Close()

Complete-Run 'settings-ui-general'
