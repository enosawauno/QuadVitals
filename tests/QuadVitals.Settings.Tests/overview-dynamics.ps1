# Overview dynamics: fan card, diagnostics window, frame clock and edit-mode jiggle.
#
# Split out of ui-regression.ps1 when that file crossed the 1800-line ceiling. This suite owns its
# own overview window and synthetic fan snapshot, so it does not depend on the element-list setup.

param(
    [string]$UiDir = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot 'ui-harness.ps1') -UiDir $UiDir

$ovType = $asm.GetType('QuadVitals.Settings.OverviewPage')
$pageType = $asm.GetType('QuadVitals.Settings.ElementsPage')
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
if ($null -eq $ovPage) { throw 'OverviewPage not found' }

"== fan card =="
    $fanCard = $ovType.GetField('_fanCard', $flags).GetValue($ovPage)
    Check 'the fan card sits on the overview' ($null -ne $fanCard) ''
    if ($null -ne $fanCard) {
        Check 'the fan card has a value readout' ($null -ne (Find-ById $ovRoot 'FanValue')) ''
        Check 'the fan card has a caption and a bar' (($null -ne (Find-ById $ovRoot 'FanCaption')) -and ($null -ne (Find-ById $ovRoot 'FanBar'))) ''

        # 读数区四行同一条左边缘：条宽固定 200，默认对齐会把它居中、比上下两行缩进一截。
        $fanBar = Find-ById $ovRoot 'FanBar'
        $fanCaption = Find-ById $ovRoot 'FanCaption'
        $fanOthers = Find-ById $ovRoot 'FanOthers'
        if (($null -ne $fanBar) -and ($null -ne $fanCaption) -and ($null -ne $fanOthers)) {
            $fanBarX = $fanBar.TranslatePoint((New-Object System.Windows.Point(0, 0)), $ovRoot).X
            $fanCaptionX = $fanCaption.TranslatePoint((New-Object System.Windows.Point(0, 0)), $ovRoot).X
            $fanOthersX = $fanOthers.TranslatePoint((New-Object System.Windows.Point(0, 0)), $ovRoot).X
            Check 'the fan bar sits on the readout left edge' (($fanBar.HorizontalAlignment -eq [System.Windows.HorizontalAlignment]::Left) -and ([Math]::Abs($fanBarX - $fanCaptionX) -le 1)) ("bar=" + [Math]::Round($fanBarX, 1) + " caption=" + [Math]::Round($fanCaptionX, 1))
            Check 'the fan note shares that edge' ([Math]::Abs($fanOthersX - $fanCaptionX) -le 1) ("others=" + [Math]::Round($fanOthersX, 1) + " caption=" + [Math]::Round($fanCaptionX, 1))
            Check 'the fan rows keep the 8 px rhythm' (([double]$fanBar.Margin.Top -eq 8) -and ([double]$fanOthers.Margin.Top -eq 8)) ("bar=" + $fanBar.Margin.Top + " others=" + $fanOthers.Margin.Top)
        }

        # 标题图标是手绘叶轮，不再是 15 px 的系统字形：那个字形在这个尺寸只剩几根细线。
        $fanCardInner = Read-Field $fanCard '_card' $flags
        $fanTitleGroup = Read-Field $fanCardInner '_titleGroup' $flags
        $fanIcon = if (($null -ne $fanTitleGroup) -and ($fanTitleGroup.Children.Count -gt 1)) { $fanTitleGroup.Children[0] } else { $null }
        Check 'the fan card icon is drawn, not a glyph' (($null -ne $fanIcon) -and ($fanIcon -is [System.Windows.Controls.Viewbox])) ''
        Check 'the fan card icon is 18 px square' (($null -ne $fanIcon) -and ([double]$fanIcon.Width -eq 18) -and ([double]$fanIcon.Height -eq 18)) ''
        Check 'the fan card title still reads the same' ($fanCardInner.Title -eq '风扇') ''

        # 说明行只能一行：换行会把整排格子的对齐拉歪，放不下就用省略号。
        $cpuCaption = Read-Field (Read-Field $ovPage '_cpuTile' $flags) '_caption' $flags
        Check 'the cpu caption never wraps' (($null -ne $cpuCaption) -and ($cpuCaption.TextWrapping -eq [System.Windows.TextWrapping]::NoWrap)) ''
        Check 'the cpu caption ellipsizes instead of clipping' (($null -ne $cpuCaption) -and ($cpuCaption.TextTrimming -eq [System.Windows.TextTrimming]::CharacterEllipsis)) ''

        # Feed the card a synthetic snapshot shaped exactly like the agent's: 1700 RPM is the
        # danger band, and the readout must follow it instead of showing a hard-coded colour.
        $fanType = $coreAsm.GetType('QuadVitals.Core.MetricsSnapshot+FanReading')
        $snapType = $coreAsm.GetType('QuadVitals.Core.MetricsSnapshot')
        $snap = [Activator]::CreateInstance($snapType)
        $snap.TimestampUtc = [DateTime]::UtcNow
        $snap.Flags = [Enum]::Parse([QuadVitals.Core.MetricFlags], 'AgentAlive, FanValid')
        $fan = [Activator]::CreateInstance($fanType)
        $fan.Kind = 1
        $fan.Channel = 2
        $fan.Rpm = 1700
        $fan.Valid = $true
        $pump = [Activator]::CreateInstance($fanType)
        $pump.Kind = 7
        $pump.Channel = 7
        $pump.Rpm = 3333
        $pump.Valid = $true
        $fans = [Array]::CreateInstance($fanType, 2)
        $fans.SetValue($fan, 0)
        $fans.SetValue($pump, 1)
        $snap.Fans = $fans
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        $valueText = (Find-ById $ovRoot 'FanValue').Text
        Check 'the fan value follows the snapshot' ($valueText -eq '1700') "value=$valueText"
        $tone = $fanCard.GetType().GetProperty('Tone').GetValue($fanCard)
        Check '1700 RPM maps to the danger band' ($tone.ToString() -eq 'Danger') "tone=$tone"
        Pump 400
        $accent1700 = $fanCard.GetType().GetProperty('AccentColour').GetValue($fanCard)
        Check '1700 RPM stays in the red family' (($accent1700.R -gt ($accent1700.G + 60)) -and ($accent1700.R -gt ($accent1700.B + 60))) ("r=$($accent1700.R) g=$($accent1700.G) b=$($accent1700.B)")
        $fan.Rpm = 1800
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        $accent1800 = $fanCard.GetType().GetProperty('AccentColour').GetValue($fanCard)
        Check 'the danger colour keeps moving with RPM' ($accent1800.ToString() -ne $accent1700.ToString()) ("1700=$accent1700 1800=$accent1800")
        $captionText = (Find-ById $ovRoot 'FanCaption').Text
        Check 'the fan caption uses the board header name' ($captionText -eq 'CPU_FAN') "caption=$captionText"

        # 临界点抖动不换档：1399 ↔ 1401 必须留在绿色档；真的要变快才进黄色。
        $toneOf = { $fanCard.GetType().GetProperty('Tone').GetValue($fanCard).ToString() }
        $fan.Rpm = 1300
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        Check '1300 RPM is green' ((& $toneOf) -eq 'Normal') ("tone=" + (& $toneOf))
        $accent1300 = $fanCard.GetType().GetProperty('AccentColour').GetValue($fanCard)
        $fan.Rpm = 1000
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        $accent1000 = $fanCard.GetType().GetProperty('AccentColour').GetValue($fanCard)
        Check 'the normal colour keeps moving with RPM' ($accent1000.ToString() -ne $accent1300.ToString()) ("1000=$accent1000 1300=$accent1300")
        $fan.Rpm = 1399
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        Check '1399 RPM stays green' ((& $toneOf) -eq 'Normal') ("tone=" + (& $toneOf))
        $fan.Rpm = 1401
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        Check '1401 RPM does not flip to yellow' ((& $toneOf) -eq 'Normal') ("tone=" + (& $toneOf))
        $fan.Rpm = 1460
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        Check '1460 RPM enters the warning band' ((& $toneOf) -eq 'Warning') ("tone=" + (& $toneOf))
        $fan.Rpm = 1401
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        Check 'falling back to 1401 keeps the warning band' ((& $toneOf) -eq 'Warning') ("tone=" + (& $toneOf))
        $fan.Rpm = 1339
        $fanCard.GetType().GetMethod('Update').Invoke($fanCard, @($snap, $true)) | Out-Null
        Check '1339 RPM returns to green' ((& $toneOf) -eq 'Normal') ("tone=" + (& $toneOf))

        # 颜色是渐变的：值文本的刷子上挂着动画，而不是直接换了一把冻结刷子。
        $valueBrush = (Find-ById $ovRoot 'FanValue').Foreground
        Check 'the readout colour is animated' ([bool]$valueBrush.HasAnimatedProperties) ("animated=" + $valueBrush.HasAnimatedProperties)

        # 齿轮：打开「风扇设置」，下拉里是自动 + 当前两路；选中 AIO_PUMP 并保存后写回 fanChannel。
        $fanGear = Find-ById $ovRoot 'FanSettings'
        Check 'the fan card has a settings gear' ($null -ne $fanGear) ''
        $deviceGear = Find-ById $ovRoot 'DeviceSettings'
        $networkGear = Find-ById $ovRoot 'IpSettings'
        Check 'the device gear starts hidden' (($null -ne $deviceGear) -and ($deviceGear.Opacity -lt 0.01)) ("opacity=" + $(if ($null -eq $deviceGear) { 'none' } else { $deviceGear.Opacity }))
        Check 'the network gear starts hidden' (($null -ne $networkGear) -and ($networkGear.Opacity -lt 0.01)) ("opacity=" + $(if ($null -eq $networkGear) { 'none' } else { $networkGear.Opacity }))
        Check 'the fan gear starts hidden' (($null -ne $fanGear) -and ($fanGear.Opacity -lt 0.01)) ("opacity=" + $(if ($null -eq $fanGear) { 'none' } else { $fanGear.Opacity }))

        # 鼠标进入卡片才淡入，移出即淡出；事件在测试里直接升起（不做真实光标移动）。
        $fanSurface = Find-ById $ovRoot 'FanCard'
        if ($null -ne $fanSurface -and $null -ne $fanGear) {
            $enterArgs = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
            $enterArgs.RoutedEvent = [System.Windows.Input.Mouse]::MouseEnterEvent
            $fanSurface.RaiseEvent($enterArgs)
            Pump 250
            Check 'hovering the card reveals its gear' ($fanGear.Opacity -gt 0.9) ("opacity=" + $fanGear.Opacity)
            $leaveArgs = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
            $leaveArgs.RoutedEvent = [System.Windows.Input.Mouse]::MouseLeaveEvent
            $fanSurface.RaiseEvent($leaveArgs)
            Pump 250
            Check 'leaving the card hides it again' ($fanGear.Opacity -lt 0.1) ("opacity=" + $fanGear.Opacity)

            # 每张卡只管自己的齿轮：悬停设备卡时，另外两张卡的齿轮不能跟着亮。
            $deviceSurface = Find-ById $ovRoot 'DeviceCard'
            if ($null -ne $deviceSurface) {
                $deviceEnter = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
                $deviceEnter.RoutedEvent = [System.Windows.Input.Mouse]::MouseEnterEvent
                $deviceSurface.RaiseEvent($deviceEnter)
                Pump 250
                Check 'hovering the device card reveals only its gear' (($deviceGear.Opacity -gt 0.9) -and ($fanGear.Opacity -lt 0.1) -and ($networkGear.Opacity -lt 0.1)) ("device=" + $deviceGear.Opacity + " fan=" + $fanGear.Opacity + " net=" + $networkGear.Opacity)
                $deviceLeave = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
                $deviceLeave.RoutedEvent = [System.Windows.Input.Mouse]::MouseLeaveEvent
                $deviceSurface.RaiseEvent($deviceLeave)
                Pump 250
                Check 'leaving the device card hides its gear' ($deviceGear.Opacity -lt 0.1) ("opacity=" + $deviceGear.Opacity)
            }
        }
        if ($null -ne $fanGear) {
            $fanGear.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Button]::ClickEvent)))
            Pump 200
            $fanDialog = Find-ById $ovRoot 'FanSettingsDialog'
            Check 'the gear opens the fan settings dialog' ($null -ne $fanDialog) ''
            $channelBox = Find-ById $ovRoot 'FanChannel'
            Check 'the dialog offers auto plus every live channel' (($null -ne $channelBox) -and ($channelBox.Items.Count -eq 3)) ("items=" + $(if ($null -eq $channelBox) { 'none' } else { $channelBox.Items.Count }))
            # 只验弹窗与选项；不点保存——那会写进开发者的真实 settings.json。
            $cancelFan = Find-ById $ovRoot 'FanSettingsCancel'
            Check 'the dialog has a cancel button' ($null -ne $cancelFan) ''
            if ($null -ne $cancelFan) {
                $cancelFan.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Button]::ClickEvent)))
                Pump 120
                Check 'cancelling closes the dialog' (-not [bool]$windowType.GetProperty('IsOverlayOpen').GetValue($ovWindow)) ''
            }
        }

        # 保存会写 settings.json 并重建页面，旧引用作废：从树上重新找当前这张卡。
        $freshCard = $null
        foreach ($candidate in (Find-ByTypeName $ovRoot 'FanCard')) { $freshCard = $candidate; break }
        Check 'the card is still on the page after saving' ($null -ne $freshCard) ''
        if ($null -ne $freshCard) {
            $freshCard.GetType().GetProperty('PreferredChannel').SetValue($freshCard, 7, $null)
            $freshCard.GetType().GetMethod('Update').Invoke($freshCard, @($snap, $true)) | Out-Null
            $pumpCaption = (Find-ById $ovRoot 'FanCaption').Text
            Check 'the card follows the configured channel' ($pumpCaption -eq 'AIO_PUMP') "caption=$pumpCaption"
            $pumpTone = $freshCard.GetType().GetProperty('Tone').GetValue($freshCard)
            Check 'a running pump is not a danger band' ($pumpTone.ToString() -eq 'Normal') "tone=$pumpTone"
            $pumpAccent = $freshCard.GetType().GetProperty('AccentColour').GetValue($freshCard)
            Check 'the running pump uses cyan' ($pumpAccent.ToString() -eq [QuadVitals.UI.Theme]::AccentCyan.ToString()) "colour=$pumpAccent"
            Check 'the running pump shows its halo' ([bool]$freshCard.GetType().GetProperty('PumpGlowVisible').GetValue($freshCard, $null)) ''
            $pumpGlow = $freshCard.GetType().GetField('_pumpGlow', $flags).GetValue($freshCard)
            Check 'the pump halo can breathe' ($pumpGlow.RenderTransform -is [System.Windows.Media.ScaleTransform]) ''
            Check 'the pump halo is translucent' (($pumpGlow.Opacity -gt 0.2) -and ($pumpGlow.Opacity -lt 0.55)) ("opacity=" + $pumpGlow.Opacity)

            $freshCard.GetType().GetMethod('Update').Invoke($freshCard, @($null, $false)) | Out-Null
            Check 'offline shows a dash and a reason' (((Find-ById $ovRoot 'FanValue').Text -eq '--') -and ((Find-ById $ovRoot 'FanEmpty').Text.Length -gt 0)) ''

            # Empty state is grey, not "green but empty": the ring, the blades and the dash all drop
            # to the readable grey, while the reason line takes the warning colour and stays on one
            # line at the same size as the device tiles' captions.
            $emptyRing = $freshCard.GetType().GetField('_ringBrush', $flags).GetValue($freshCard)
            $emptyBlade = $freshCard.GetType().GetField('_bladeBrush', $flags).GetValue($freshCard)
            Check 'an empty fan card turns its ring grey' ($emptyRing.Color -eq [QuadVitals.UI.Theme]::TextSecondary) "ring=$($emptyRing.Color)"
            Check 'an empty fan card turns its blades grey' ($emptyBlade.Color -eq [QuadVitals.UI.Theme]::TextSecondary) "blades=$($emptyBlade.Color)"
            $emptyText = Find-ById $ovRoot 'FanEmpty'
            Check 'the empty reason takes the warning colour' ($emptyText.Foreground -eq [QuadVitals.UI.Theme]::AccentOrangeBrush) "fg=$($emptyText.Foreground)"
            Check 'the empty reason stays on one line' ($emptyText.TextWrapping -eq [System.Windows.TextWrapping]::NoWrap) ''
            Check 'the empty reason uses the tile caption size' ([Math]::Abs($emptyText.FontSize - 11.5) -lt 0.01) "size=$($emptyText.FontSize)"

            # Stopped: the channel reports 0 RPM. That is a fault, not "no reading" -- value 0 in the
            # danger colour with the words, no spinning blades, no airflow.
            $freshCard.GetType().GetMethod('Update').Invoke($freshCard, @($snap, $true)) | Out-Null
            $savedPumpRpm = $pump.Rpm
            $pump.Rpm = 0
            $freshCard.GetType().GetMethod('Update').Invoke($freshCard, @($snap, $true)) | Out-Null
            Check 'a stopped channel reads 0, not a dash' ((Find-ById $ovRoot 'FanValue').Text -eq '0') ("value=" + (Find-ById $ovRoot 'FanValue').Text)
            Check 'a stopped channel says so' ((Find-ById $ovRoot 'FanEmpty').Text.Length -gt 0) ("text=" + (Find-ById $ovRoot 'FanEmpty').Text)
            Check 'a stopped channel is a danger band' ($freshCard.GetType().GetProperty('Tone').GetValue($freshCard, $null).ToString() -eq 'Danger') ''
            Check 'a stopped pump drops the cyan halo' (-not [bool]$freshCard.GetType().GetProperty('PumpGlowVisible').GetValue($freshCard, $null)) ''
            $stoppedEmpty = Find-ById $ovRoot 'FanEmpty'
            Check 'the stopped line is red' ($stoppedEmpty.Foreground -eq [QuadVitals.UI.Theme]::AccentRedBrush) ("fg=" + $stoppedEmpty.Foreground)
            $pump.Rpm = $savedPumpRpm

            # Configured but absent this frame: say it, never swap to another channel.
            $freshCard.GetType().GetProperty('PreferredChannel').SetValue($freshCard, 5, $null)
            $freshCard.GetType().GetMethod('Update').Invoke($freshCard, @($snap, $true)) | Out-Null
            Check 'a missing selected channel does not borrow another one' ((Find-ById $ovRoot 'FanValue').Text -eq '--') ("value=" + (Find-ById $ovRoot 'FanValue').Text)
            Check 'the missing selected channel keeps its number' ((Find-ById $ovRoot 'FanCaption').Text -eq ([string]::Concat([char]0x901A, [char]0x9053, ' 5'))) ("caption=" + (Find-ById $ovRoot 'FanCaption').Text)
            Check 'the card explains the missing selection' ((Find-ById $ovRoot 'FanEmpty').Text.Length -gt 0) ''

            # 在线但 Agent 没提权：温度与风扇同源，卡片要说清“未提权”，而不是“未读取到主板风扇”。
            $unelevated = [Activator]::CreateInstance($snapType)
            $unelevated.TimestampUtc = [DateTime]::UtcNow
            $unelevated.Flags = [Enum]::Parse([QuadVitals.Core.MetricFlags], 'AgentAlive, ElevationKnown')
            $freshCard.GetType().GetMethod('Update').Invoke($freshCard, @($unelevated, $true)) | Out-Null
            $unelevatedText = (Find-ById $ovRoot 'FanEmpty').Text
            Check 'the fan card explains an unelevated agent' ($unelevatedText -like '*未提权*') "text=$unelevatedText"
            $freshCard.GetType().GetMethod('Update').Invoke($freshCard, @($snap, $true)) | Out-Null
            $freshCard.GetType().GetProperty('PreferredChannel').SetValue($freshCard, 7, $null)

            # The dialog must keep a configured channel that is not in this frame's list; otherwise
            # the dropdown shows "auto" and one save wipes the choice.
            $dialogType = $asm.GetType('QuadVitals.Settings.FanSettingsDialog')
            $dialog = [Activator]::CreateInstance($dialogType, @($ovWindow, $freshCard))
            $settingsType = $coreAsm.GetType('QuadVitals.Core.Settings')
            $probeSettings = [Activator]::CreateInstance($settingsType)
            $probeSettings.FanChannel = 5
            $options = $dialogType.GetMethod('BuildOptions', $flags).Invoke($dialog, @($probeSettings))
            $keys = @($options | ForEach-Object { $_.Key })
            Check 'the dialog offers auto first' ($keys[0] -eq -1) ("keys=" + ($keys -join ','))
            Check 'the dialog keeps a configured channel that is not reporting' ($keys -contains 5) ("keys=" + ($keys -join ','))
            Check 'the configured channel is marked as unread' (($options | Where-Object { $_.Key -eq 5 }).Value -like '*5*') ''
            Check 'the live channels are still offered' ($keys -contains 2 -and $keys -contains 7) ("keys=" + ($keys -join ','))
        }

        # The gear must stay reachable from the keyboard: hidden by opacity, never by focusability.
        if ($null -ne $fanGear) {
            Check 'the hidden gear is still focusable' ($fanGear.Focusable) ''
            $fanGear.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.UIElement]::GotFocusEvent)))
            Pump 200
            Check 'focusing the gear reveals it' ($fanGear.Opacity -gt 0.9) ("opacity=" + $fanGear.Opacity)
        }

        # Height must not be bound to another element's layout output (it forbids growing at high DPI).
        $networkCard = Find-ById $ovRoot 'NetworkCard'
        # IP 掩码切换不能改变值列宽度/字号，也不能推动右侧图标。
        $lanValue = Find-ById $ovRoot 'LanIpValue'
        $pubValue = Find-ById $ovRoot 'PublicIpValue'
        $eyeButton = Find-ById $ovRoot 'ToggleIpMask'
        $copyButton = Find-ById $ovRoot 'CopyIps'
        if (($null -ne $lanValue) -and ($null -ne $eyeButton) -and ($null -ne $copyButton)) {
            $ovPage.GetType().GetField('_lanIp', $flags).SetValue($ovPage, 'lan-host')
            $ovPage.GetType().GetField('_publicIp', $flags).SetValue($ovPage, 'wan-host')
            $ovPage.GetType().GetMethod('ToggleIpMask', $flags).Invoke($ovPage, @()) | Out-Null
            Pump 150
            $maskWidth = $lanValue.ActualWidth
            $maskFont = [double]$lanValue.FontSize
            $eyeX = $eyeButton.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0))).X
            $expectedMask = -join (1..10 | ForEach-Object { [char]0x2022 })
            Check 'masking shows ten dots' ($lanValue.Text -eq $expectedMask) "text=$($lanValue.Text)"
            $ovPage.GetType().GetMethod('ToggleIpMask', $flags).Invoke($ovPage, @()) | Out-Null
            Pump 150
            Check 'unmasking restores the value width' ([math]::Abs($lanValue.ActualWidth - $maskWidth) -le 0.5) "mask=$maskWidth ip=$($lanValue.ActualWidth)"
            Check 'unmasking keeps the font size' ([double]$lanValue.FontSize -eq $maskFont) "mask=$maskFont ip=$($lanValue.FontSize)"
            $eyeAfter = $eyeButton.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0))).X
            Check 'the eye icon does not move when toggling' ([math]::Abs($eyeAfter - $eyeX) -le 0.5) "mask=$eyeX ip=$eyeAfter"
            Check 'unmasking restores the IP text' ($lanValue.Text -eq 'lan-host') "text=$($lanValue.Text)"
        }
        # 值列只按自己的 IP 量宽：统一按最长 IPv4 兜底会在每列右侧多留 40 多 px 空白，
        # 整行比卡片内容还宽，最右的齿轮被顶到卡片内边距里。
        # 样例用等长的数字串：字面 IP 会被敏感数据门禁拦下，而列宽只跟字符宽度有关。
        $lanSample = '22222222'        # 15 px SemiBold ≈ 67 px，与一个局域网 IPv4 同宽
        $pubSample = '22222222222'     # ≈ 92 px，与一个公网 IPv4 同宽
        if (($null -ne $lanValue) -and ($null -ne $pubValue) -and ($null -ne $eyeButton) -and ($null -ne $copyButton) -and ($null -ne $networkGear) -and ($null -ne $networkCard)) {
            $ovPage.GetType().GetField('_lanIp', $flags).SetValue($ovPage, $lanSample)
            $ovPage.GetType().GetField('_publicIp', $flags).SetValue($ovPage, $pubSample)
            $ovPage.GetType().GetMethod('ApplyIpDisplay', $flags).Invoke($ovPage, @()) | Out-Null
            Pump 150
            $xLan = $lanValue.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0))).X
            $xPub = $pubValue.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0))).X
            $xCopy = $copyButton.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0))).X
            $xGear = $networkGear.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0))).X
            $xCard = $networkCard.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0))).X
            $contentRight = $xCard + $networkCard.ActualWidth - 20
            Check 'the LAN column is sized to its own IP' ($lanValue.ActualWidth -le 70) "w=$($lanValue.ActualWidth)"
            Check 'the public column is sized to its own IP' (($pubValue.ActualWidth -ge 88) -and ($pubValue.ActualWidth -le 96)) "w=$($pubValue.ActualWidth)"
            Check 'the IP group keeps its gap to the icons' ([math]::Abs(($xCopy - $xPub - $pubValue.ActualWidth) - 16) -le 0.5) ("gap=" + ($xCopy - $xPub - $pubValue.ActualWidth))
            Check 'the network gear stays inside the card' (($xGear + $networkGear.ActualWidth) -le ($contentRight + 0.5)) "gearRight=$([math]::Round($xGear + $networkGear.ActualWidth, 1)) contentRight=$([math]::Round($contentRight, 1))"
            Check 'the value columns do not reserve the longest IPv4' (($lanValue.ActualWidth + $pubValue.ActualWidth) -lt 170) ("sum=" + ($lanValue.ActualWidth + $pubValue.ActualWidth))
            $ovPage.GetType().GetField('_lanIp', $flags).SetValue($ovPage, 'lan-host')
            $ovPage.GetType().GetField('_publicIp', $flags).SetValue($ovPage, 'wan-host')
            $ovPage.GetType().GetMethod('ApplyIpDisplay', $flags).Invoke($ovPage, @()) | Out-Null
            Pump 80
        }
        if ($null -ne $networkCard) {
            Check 'the network card sets a minimum height, not a fixed one' ([double]::IsNaN($networkCard.Height) -and $networkCard.MinHeight -gt 0) ("height=" + $networkCard.Height + " min=" + $networkCard.MinHeight)
        }
        Check 'the fan card sets a minimum height, not a fixed one' ([double]::IsNaN($freshCard.Height) -and $freshCard.MinHeight -gt 0) ("height=" + $freshCard.Height + " min=" + $freshCard.MinHeight)

        "== power card =="
        $powerCard = (Find-ByTypeName $ovRoot 'PowerCard' | Select-Object -First 1)
        Check 'the power card sits on the overview' ($null -ne $powerCard) ''
        if ($null -ne $powerCard) {
            $powerSnap = [Activator]::CreateInstance($snapType)
            $powerSnap.TimestampUtc = [DateTime]::UtcNow
            $powerSnap.Flags = [Enum]::Parse([QuadVitals.Core.MetricFlags], 'AgentAlive, CpuPowerValid, GpuPowerValid')
            $powerSnap.CpuPowerWatts = 112.6
            $powerSnap.GpuPowerWatts = 46.5
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null

            $powerTotalText = Find-ById $ovRoot 'PowerTotal'
            $powerCpuText = Find-ById $ovRoot 'PowerCpu'
            $powerGpuText = Find-ById $ovRoot 'PowerGpu'
            $powerCpuUnit = Find-ById $ovRoot 'PowerCpuUnit'
            $powerGpuUnit = Find-ById $ovRoot 'PowerGpuUnit'
            Check 'the power total adds CPU and GPU' ($powerTotalText.Text -eq '159.1') ("total=" + $powerTotalText.Text)
            Check 'the power total carries its unit' ((Find-ById $ovRoot 'PowerUnit').Text -eq 'W') ''
            Check 'the power total is the hero readout' (([double]$powerTotalText.FontSize -eq 40) -and ($null -ne (Find-ById $ovRoot 'PowerTotalLabel'))) ("size=" + $powerTotalText.FontSize)
            Check 'the CPU power is shown on its own line' ($powerCpuText.Text -eq '112.6') ("cpu=" + $powerCpuText.Text)
            Check 'the GPU power is shown on its own line' ($powerGpuText.Text -eq '46.5') ("gpu=" + $powerGpuText.Text)
            Check 'each wattage carries its own unit' (($powerCpuUnit.Text -eq 'W') -and ($powerGpuUnit.Text -eq 'W')) ''
            Check 'the component wattages step down from the total' (([double]$powerCpuText.FontSize -eq 18) -and ([double]$powerGpuText.FontSize -eq 18) -and ([double]$powerCpuText.FontSize -lt [double]$powerTotalText.FontSize)) ''
            Check 'the power scope says CPU + GPU' ((Find-ById $ovRoot 'PowerScope').Text -eq 'CPU + GPU') ''
            Check 'the GPU row is labelled GPU' ((Find-ById $ovRoot 'PowerGpuLabel').Text -eq 'GPU') ''
            $powerCpuY = $powerCpuText.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).Y
            $powerGpuY = $powerGpuText.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).Y
            Check 'CPU and GPU power rows are stacked vertically' ($powerGpuY -gt ($powerCpuY + 6)) ("cpuY=" + [Math]::Round($powerCpuY, 1) + " gpuY=" + [Math]::Round($powerGpuY, 1))
            $powerCpuBar = Find-ById $ovRoot 'PowerCpuBar'
            $powerGpuBar = Find-ById $ovRoot 'PowerGpuBar'
            $powerCpuX = $powerCpuText.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).X
            $powerGpuX = $powerGpuText.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).X
            $powerCpuBarX = $powerCpuBar.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).X
            $powerGpuBarX = $powerGpuBar.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).X
            Check 'each bar sits to the right of its label and value' (($powerCpuBarX -gt ($powerCpuX + 80)) -and ($powerGpuBarX -gt ($powerGpuX + 80))) ("cpuBar=" + [Math]::Round($powerCpuBarX, 1) + " cpuText=" + [Math]::Round($powerCpuX, 1))
            $powerCpuBarY = $powerCpuBar.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).Y
            $powerGpuBarY = $powerGpuBar.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).Y
            Check 'each bar rides on its value row' (([Math]::Abs(($powerCpuY + $powerCpuText.ActualHeight / 2) - ($powerCpuBarY + $powerCpuBar.ActualHeight / 2)) -lt 6) -and ([Math]::Abs(($powerGpuY + $powerGpuText.ActualHeight / 2) - ($powerGpuBarY + $powerGpuBar.ActualHeight / 2)) -lt 6)) ''
            Check 'the segmented bars keep the device card segment size' (($powerCpuBar.ActualWidth -ge 200) -and ($powerCpuBar.ActualWidth -le 240) -and ($powerGpuBar.ActualWidth -ge 200) -and ($powerGpuBar.ActualWidth -le 240)) ("width=" + [Math]::Round($powerCpuBar.ActualWidth, 1))
            $powerCpuTdpText = (Find-ById $ovRoot 'PowerCpuTdp').Text
            $powerGpuTdpText = (Find-ById $ovRoot 'PowerGpuTdp').Text
            # 默认按最大睿频功耗：13900K 为 253 W，基础功耗（TDP）125 W 会过早顶满。
            Check 'the bar scale is visible as a power caption' (($powerCpuTdpText -eq 'MTP 253 W') -and ($powerGpuTdpText -match '^TDP [0-9]+ W$')) ("cpu=" + $powerCpuTdpText + " gpu=" + $powerGpuTdpText)

            # 切到 TDP 后分母用基础功耗：固件报回的 95 W 优先于型号表，没有报告时才用表。
            $powerCard.CpuPowerMode = [QuadVitals.Core.Settings]::CpuPowerTdp
            $powerSnap.CpuTdpWatts = 95
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Check 'a firmware-reported TDP wins over the model table' ((Find-ById $ovRoot 'PowerCpuTdp').Text -eq 'TDP 95 W') ("tdp=" + (Find-ById $ovRoot 'PowerCpuTdp').Text)
            $powerSnap.CpuTdpWatts = 0
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Check 'without a reported TDP the table still applies' ((Find-ById $ovRoot 'PowerCpuTdp').Text -eq 'TDP 125 W') ("tdp=" + (Find-ById $ovRoot 'PowerCpuTdp').Text)
            $powerCard.CpuPowerMode = [QuadVitals.Core.Settings]::CpuPowerMtp
            # 固件限制（PL1/PL2 取大）优先于基础 TDP：13900K 报 125 W 但实跑 200 W 以上，
            # 固件配置的上限（PL1/PL2）不是 MTP：它只进 tooltip，不能改写尾标。
            $powerSnap.CpuPowerLimitWatts = 215
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Pump 120
            $limitText = Find-ById $ovRoot 'PowerCpuTdp'
            Check 'a firmware power limit never masquerades as MTP' ($limitText.Text -eq 'MTP 253 W') ("tdp=" + $limitText.Text)
            Check 'the tooltip still reports the local limit' (($limitText.ToolTip -as [string]) -like '*本机配置上限 215 W*') ("tip=" + $limitText.ToolTip)

            # 规格表命中时按表里的两个固定值算（厂商定的数，与 BIOS 无关）。
            $tableType = $coreAsm.GetType('QuadVitals.Core.CpuSpecTable')
            $specTable = [Activator]::CreateInstance($tableType)
            $specTable.Add('intel', 'I9-13900K', 125.0, 199.0, $null)
            [QuadVitals.Core.CpuSpecs]::Apply($specTable)
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Pump 120
            $specText = Find-ById $ovRoot 'PowerCpuTdp'
            Check 'the spec table drives the MTP caption' ($specText.Text -eq 'MTP 199 W') ("tdp=" + $specText.Text)
            Check 'the MTP tooltip names the spec table' (($specText.ToolTip -as [string]) -like '*规格表*') ("tip=" + $specText.ToolTip)
            $specBar = Find-ById $ovRoot 'PowerCpuBar'
            $specFraction = [double](Read-Field $specBar '_value' $flags)
            Check 'the CPU bar is scaled to the spec table value' ([Math]::Abs($specFraction - (112.6 / 199.0)) -lt 0.001) ("cpu=" + $specFraction)
            # TDP 口径：固件没报时用表里的基础功耗。
            $powerCard.CpuPowerMode = [QuadVitals.Core.Settings]::CpuPowerTdp
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Pump 120
            Check 'the spec table also supplies the base power' ((Find-ById $ovRoot 'PowerCpuTdp').Text -eq 'TDP 125 W') ("tdp=" + (Find-ById $ovRoot 'PowerCpuTdp').Text)
            $powerCard.CpuPowerMode = [QuadVitals.Core.Settings]::CpuPowerMtp
            [QuadVitals.Core.CpuSpecs]::Apply($null)
            $powerSnap.CpuPowerLimitWatts = 0
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Pump 80
            # 表没命中、但内置型号表认识这个型号：措辞不能写成“未登记”。
            $fallbackText = Find-ById $ovRoot 'PowerCpuTdp'
            Check 'the MTP tooltip names the model table when the spec table misses' (($fallbackText.ToolTip -as [string]) -like '*型号表*') ("tip=" + $fallbackText.ToolTip)
            Check 'a model-table hit does not claim the CPU is unregistered' (-not (($fallbackText.ToolTip -as [string]) -like '*未登记*')) ("tip=" + $fallbackText.ToolTip)
            $powerIcon = Find-ById $ovRoot 'PowerIcon'
            Check 'the power card has a drawn energy icon on the left' (($null -ne $powerIcon) -and ($powerIcon -is [System.Windows.Controls.Canvas]) -and ([double]$powerIcon.Width -eq 96) -and ([double]$powerIcon.Height -eq 128)) ''
            $powerIconX = -1
            if ($null -ne $powerIcon) { $powerIconX = $powerIcon.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).X }
            Check 'the energy icon sits left of the readouts' (($powerIconX -ge 0) -and ($powerIconX -lt $powerCpuX)) ("iconX=" + [Math]::Round($powerIconX, 1) + " valueX=" + [Math]::Round($powerCpuX, 1))
            $iconRings = @($powerIcon.Children | Where-Object { ($_ -is [System.Windows.Shapes.Ellipse]) -and ($null -ne $_.Stroke) })
            $iconSparks = @($powerIcon.Children | Where-Object { $_ -is [System.Windows.Shapes.Path] })
            Check 'the energy icon has no circle around it' ($iconRings.Count -eq 0) ("rings=" + $iconRings.Count)
            Check 'the energy icon carries a bolt and sparks' ($iconSparks.Count -ge 4) ("paths=" + $iconSparks.Count)
            Check 'the scope note no longer takes a bottom line' ((Find-ById $ovRoot 'PowerNote').Text.Length -eq 0) ("text=" + (Find-ById $ovRoot 'PowerNote').Text)
            # 统计范围改成标题旁的提示徽标：徽标要在标题行上，并且把整句话当成 tooltip。
            $scopeHint = Find-ById $ovRoot 'PowerScopeHint'
            Check 'the scope hint sits on the title row' (($null -ne $scopeHint) -and (($scopeHint.ToolTip -as [string]) -like '*主板*')) ("tip=" + $scopeHint.ToolTip)
            if ($null -ne $scopeHint) {
                $hintY = $scopeHint.TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).Y
                $firstRowY = (Find-ById $ovRoot 'PowerCpuLabel').TranslatePoint((New-Object System.Windows.Point(0, 0)), $powerCard).Y
                Check 'the scope hint rides above the first readout' (($hintY -lt $firstRowY) -and ($hintY -ge 0)) ("hintY=" + [Math]::Round($hintY, 1) + " rowY=" + [Math]::Round($firstRowY, 1))
            }

            # 右上角齿轮：与其他卡片一致，悬停才显示；点开是功耗设置（TDP / MTP 两个选项）。
            $powerGear = Find-ById $ovRoot 'PowerSettings'
            Check 'the power card has a hover-revealed gear' (($null -ne $powerGear) -and ($powerGear.Opacity -lt 0.01)) ("opacity=" + $(if ($null -eq $powerGear) { 'none' } else { $powerGear.Opacity }))
            if ($null -ne $powerGear) {
                # 悬停要发生在卡片本体（内层 Card 上挂了 HoverReveal），不是外层的组件 Grid。
                $powerSurface = Find-ById $ovRoot 'PowerCard'
                $powerEnter = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
                $powerEnter.RoutedEvent = [System.Windows.Input.Mouse]::MouseEnterEvent
                $powerSurface.RaiseEvent($powerEnter)
                Pump 250
                Check 'hovering the power card reveals its gear' ($powerGear.Opacity -gt 0.9) ("opacity=" + $powerGear.Opacity)

                # 弹窗读的是当前设置，不是卡片字段：先改内存里的设置再打开。
                $savedMode = $ovWindow.CurrentSettings.CpuPowerMode
                $ovWindow.CurrentSettings.CpuPowerMode = [QuadVitals.Core.Settings]::CpuPowerTdp
                $ovWindow.ShowPowerSettings()
                Pump 200
                $modeBox = Find-ById $ovRoot 'CpuPowerMode'
                Check 'the power settings dialog carries one mode dropdown' (($null -ne $modeBox) -and ($modeBox.Items.Count -eq 2)) ("items=" + $(if ($null -eq $modeBox) { 0 } else { $modeBox.Items.Count }))
                if ($null -ne $modeBox) {
                    Check 'the dropdown offers TDP and MTP' ((($modeBox.Items[0] -as [string]) -like 'TDP*') -and (($modeBox.Items[1] -as [string]) -like 'MTP*')) ("items=" + ($modeBox.Items -join ' / '))
                    Check 'the dropdown preselects the current mode' ($modeBox.SelectedIndex -eq 0) ("index=" + $modeBox.SelectedIndex)
                }
                Check 'the dialog has cancel and save' (($null -ne (Find-ById $ovRoot 'PowerSettingsCancel')) -and ($null -ne (Find-ById $ovRoot 'PowerSettingsSave'))) ''
                $ovWindow.CloseOverlay()
                $ovWindow.CurrentSettings.CpuPowerMode = $savedMode
                Pump 150
                $powerCard.CpuPowerMode = [QuadVitals.Core.Settings]::CpuPowerMtp
            }

            # 随机闪烁：等 1–10 秒来一次，单次 0.30–0.75 秒，里面必须有真正的暗台阶。
            $flickerNext = [double](Read-Field $powerCard '_nextFlickerIn' $flags)
            Check 'the flicker waits a random 1-10 seconds' (($flickerNext -ge 1.0) -and ($flickerNext -le 10.0)) ("next=" + [Math]::Round($flickerNext, 1))
            Call-Private $powerCard 'BeginFlicker' @() | Out-Null
            $flickerLeft = [double](Read-Field $powerCard '_flickerLeft' $flags)
            $flickerLength = [double](Read-Field $powerCard '_flickerLength' $flags)
            $flickerSteps = [int](Read-Field $powerCard '_flickerSteps' $flags)
            $flickerLevels = Read-Field $powerCard '_flickerLevels' $flags
            $dropouts = 0
            for ($fi = 0; $fi -lt $flickerSteps; $fi++) { if ([double]$flickerLevels[$fi] -lt 0.5) { $dropouts++ } }
            Check 'a flicker burst is short and has real dropouts' (($flickerLeft -gt 0) -and ($flickerLength -ge 0.30) -and ($flickerLength -le 0.75) -and ($dropouts -ge 1)) ("left=" + [Math]::Round($flickerLeft, 2) + " length=" + [Math]::Round($flickerLength, 2) + " steps=" + $flickerSteps + " dropouts=" + $dropouts)

            # 分档颜色与动效：高负载转红，空闲回绿；指针停在卡上才逐帧。
            $powerTone = $powerCard.GetType().GetProperty('Tone')
            $highSnap = [Activator]::CreateInstance($snapType)
            $highSnap.TimestampUtc = [DateTime]::UtcNow
            $highSnap.Flags = [Enum]::Parse([QuadVitals.Core.MetricFlags], 'AgentAlive, CpuPowerValid, GpuPowerValid')
            $highSnap.CpuPowerWatts = 120.0
            $highSnap.GpuPowerWatts = 300.0
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($highSnap, $true)) | Out-Null
            Check 'a near-limit frame turns the energy icon red' ($powerTone.GetValue($powerCard, $null).ToString() -eq 'Danger') ("tone=" + $powerTone.GetValue($powerCard, $null))
            $powerSnap.CpuPowerWatts = 30.0
            $powerSnap.GpuPowerWatts = 40.0
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Check 'an idle frame keeps the energy icon green' ($powerTone.GetValue($powerCard, $null).ToString() -eq 'Normal') ("tone=" + $powerTone.GetValue($powerCard, $null))

            # 回到基准帧：后面的条比例与缺失态断言都用这一组数。
            $powerSnap.CpuPowerWatts = 112.6
            $powerSnap.GpuPowerWatts = 46.5
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null

            # 这组断言针对 16 段几何；开发机的 indicatorStyle 可能是胶囊，先钉回分段。
            $ovWindow.CurrentSettings.IndicatorStyle = [QuadVitals.Core.Settings]::IndicatorSegments
            $ovPage.ApplyIndicatorStyle() | Out-Null
            Pump 100
            $cpuBar = Find-ById $ovRoot 'PowerCpuBar'
            $gpuBar = Find-ById $ovRoot 'PowerGpuBar'
            Check 'the CPU and GPU have separate bars' (($null -ne $cpuBar) -and ($null -ne $gpuBar) -and -not [object]::ReferenceEquals($cpuBar, $gpuBar)) ''
            Check 'the CPU bar uses the shared 16-segment style' (($null -ne $cpuBar) -and ($cpuBar.BarStyle.ToString() -eq 'Segments') -and ([double]$cpuBar.Height -eq 22)) ''
            Check 'the GPU bar uses the shared 16-segment style' (($null -ne $gpuBar) -and ($gpuBar.BarStyle.ToString() -eq 'Segments') -and ([double]$gpuBar.Height -eq 22)) ''
            $cpuFraction = [double](Read-Field $cpuBar '_value' $flags)
            $gpuFraction = [double](Read-Field $gpuBar '_value' $flags)
            Check 'the CPU bar is scaled to its MTP limit' ([Math]::Abs($cpuFraction - (112.6 / 253.0)) -lt 0.001) ("cpu=" + $cpuFraction)
            Check 'the GPU bar is scaled to its TDP' ([Math]::Abs($gpuFraction - (46.5 / 320.0)) -lt 0.001) ("gpu=" + $gpuFraction)
            Check 'the bars use the 253 W / 320 W denominators' (($cpuBar.ToolTip -like '*253*') -and ($gpuBar.ToolTip -like '*320*')) ''

            $networkWidth = [double](Find-ById $ovRoot 'NetworkCard').ActualWidth
            $powerWidth = [double]$powerCard.ActualWidth
            Check 'the power card matches the network card width' (($networkWidth -gt 0) -and ([Math]::Abs(($powerWidth / $networkWidth) - 1.0) -lt 0.04)) ("power=" + [Math]::Round($powerWidth, 1) + " network=" + [Math]::Round($networkWidth, 1))
            $networkBottom = (Find-ById $ovRoot 'NetworkCard').TranslatePoint((New-Object System.Windows.Point(0, (Find-ById $ovRoot 'NetworkCard').ActualHeight)), $ovRoot).Y
            $powerTop = $powerCard.TranslatePoint((New-Object System.Windows.Point(0, 0)), $ovRoot).Y
            Check 'the power card sits below the network card' ($powerTop -gt $networkBottom) ("powerTop=" + [Math]::Round($powerTop, 1) + " networkBottom=" + [Math]::Round($networkBottom, 1))

            # One missing feed must not be turned into a half total.
            $powerSnap.Flags = [Enum]::Parse([QuadVitals.Core.MetricFlags], 'AgentAlive, CpuPowerValid')
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            Check 'a missing GPU feed removes the total' ((Find-ById $ovRoot 'PowerTotal').Text -eq '--') ("total=" + (Find-ById $ovRoot 'PowerTotal').Text)
            Check 'the valid CPU feed stays readable' ((Find-ById $ovRoot 'PowerCpu').Text -eq '112.6') ''
            Check 'the missing GPU feed reads --' ((Find-ById $ovRoot 'PowerGpu').Text -eq '--') ''
            Check 'the missing feed is explained' ((Find-ById $ovRoot 'PowerNote').Visibility -eq [System.Windows.Visibility]::Visible) ''
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($null, $false)) | Out-Null
            Check 'an offline power card shows a dash' ((Find-ById $ovRoot 'PowerTotal').Text -eq '--') ''
            Check 'an offline power card explains itself' ((Find-ById $ovRoot 'PowerNote').Text.Length -gt 0) ''

            # 回到一帧完整数据：错误说明行会在卡片里占一行，量高度前得先让它藏起来。
            $powerSnap.Flags = [Enum]::Parse([QuadVitals.Core.MetricFlags], 'AgentAlive, CpuPowerValid, GpuPowerValid')
            $powerCard.GetType().GetMethod('Update').Invoke($powerCard, @($powerSnap, $true)) | Out-Null
            $ovRoot.UpdateLayout()

            # Narrow window: the fan moves to its own row, the power card follows underneath.
            $ovWindow.Width = 980
            $ovRoot.Measure((New-Object System.Windows.Size(980, 620)))
            $ovRoot.Arrange((New-Object System.Windows.Rect(0, 0, 980, 620)))
            $ovRoot.UpdateLayout()
            Pump 120
            $narrowNetwork = Find-ById $ovRoot 'NetworkCard'
            $narrowPower = (Find-ByTypeName $ovRoot 'PowerCard' | Select-Object -First 1)
            $narrowPowerTop = $narrowPower.TranslatePoint((New-Object System.Windows.Point(0, 0)), $ovRoot).Y
            $narrowNetworkBottom = $narrowNetwork.TranslatePoint((New-Object System.Windows.Point(0, $narrowNetwork.ActualHeight)), $ovRoot).Y
            Check 'the narrow layout keeps the power card below the network card' ($narrowPowerTop -gt $narrowNetworkBottom) ("powerTop=" + [Math]::Round($narrowPowerTop, 1) + " networkBottom=" + [Math]::Round($narrowNetworkBottom, 1))
            Check 'the narrow layout keeps the same width relationship' (([double]$narrowNetwork.ActualWidth -gt 0) -and ([Math]::Abs(([double]$narrowPower.ActualWidth / [double]$narrowNetwork.ActualWidth) - 1.0) -lt 0.06)) ("power=" + [Math]::Round($narrowPower.ActualWidth, 1) + " network=" + [Math]::Round($narrowNetwork.ActualWidth, 1))
            $ovWindow.Width = 1280
            $ovRoot.Measure((New-Object System.Windows.Size(1280, 853)))
            $ovRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
            $ovRoot.UpdateLayout()
            Pump 120
        }

        "== metric typography =="
        $metricType = $uiAsm.GetType('QuadVitals.UI.MetricTypography')
        $metricValueSize = if ($null -ne $metricType) { [double]$metricType.GetField('ValueSize').GetValue($null) } else { 0 }
        $metricUnitSize = if ($null -ne $metricType) { [double]$metricType.GetField('UnitSize').GetValue($null) } else { 0 }
        $metricLineHeight = if ($null -ne $metricType) { [double]$metricType.GetField('LineHeight').GetValue($null) } else { 0 }
        $metricDigits = if ($null -ne $metricType) { [System.Windows.Media.FontFamily]$metricType.GetField('Digits').GetValue($null) } else { $null }
        Check 'the shared metric typography matches the FOOps card' (($null -ne $metricType) -and ($metricValueSize -eq 40) -and ($metricUnitSize -eq 17) -and ($metricLineHeight -eq 44) -and ($metricDigits.Source -eq 'Arial Black')) ''
        $isMetricValue = { param($tb) ($null -ne $tb) -and ([double]$tb.FontSize -eq 40) -and ($tb.FontFamily.Source -eq 'Arial Black') -and ($tb.FontWeight -eq [System.Windows.FontWeights]::Black) -and ([double]$tb.LineHeight -eq 44) }
        $isMetricUnit = { param($tb) ($null -ne $tb) -and ([double]$tb.FontSize -eq 17) -and ($tb.FontFamily.Source -eq 'Arial Black') -and ($tb.FontWeight -eq [System.Windows.FontWeights]::Black) }

        $cpuTile = Read-Field $ovPage '_cpuTile' $flags
        $cpuValue = Read-Field $cpuTile '_value' $flags
        $cpuUnit = Read-Field $cpuTile '_unit' $flags
        Check 'the device value uses the shared 40 px Black type' (& $isMetricValue $cpuValue) ''
        Check 'the device unit uses the shared 17 px type' (& $isMetricUnit $cpuUnit) ''
        $fanValue = Read-Field $freshCard '_value' $flags
        $fanUnit = Read-Field $freshCard '_unit' $flags
        Check 'the fan value uses the shared 40 px Black type' (& $isMetricValue $fanValue) ''
        Check 'the fan unit uses the shared 17 px type' (& $isMetricUnit $fanUnit) ''
        # 层级：合计用 40 px 主数值排版，两个分量退到 18 px。
        $powerTotalValue = Find-ById $ovRoot 'PowerTotal'
        $powerUnit = Find-ById $ovRoot 'PowerUnit'
        $powerCpuValue = Find-ById $ovRoot 'PowerCpu'
        $powerGpuValue = Find-ById $ovRoot 'PowerGpu'
        $powerCpuUnit = Find-ById $ovRoot 'PowerCpuUnit'
        Check 'the power total uses the shared 40 px Black type' (& $isMetricValue $powerTotalValue) ''
        Check 'the power total unit uses the shared 17 px type' (& $isMetricUnit $powerUnit) ''
        # 大号数字走 Ideal（分数步进），小号单位继续继承窗口的 Display；窗口用 Auto + ClearTypeHint
        # 让 WPF 选高质量光栅化，分层窗口下仍输出灰度、不产生彩色边缘。
        Check 'the hero digits use ideal glyph metrics' ([System.Windows.Media.TextOptions]::GetTextFormattingMode($powerTotalValue) -eq [System.Windows.Media.TextFormattingMode]::Ideal) ''
        Check 'the unit keeps the display metrics' ([System.Windows.Media.TextOptions]::GetTextFormattingMode($powerUnit) -eq [System.Windows.Media.TextFormattingMode]::Display) ''
        Check 'the window lets WPF pick high-quality text rendering' (([System.Windows.Media.TextOptions]::GetTextRenderingMode($ovWindow) -eq [System.Windows.Media.TextRenderingMode]::Auto) -and ([System.Windows.Media.RenderOptions]::GetClearTypeHint($ovWindow) -eq [System.Windows.Media.ClearTypeHint]::Enabled)) ''
        Check 'the power components use the 18 px semibold readout' (([double]$powerCpuValue.FontSize -eq 18) -and ($powerCpuValue.FontWeight -eq [System.Windows.FontWeights]::SemiBold) -and ([double]$powerGpuValue.FontSize -eq 18)) ''
        Check 'the power component unit is smaller than the total unit' ([double]$powerCpuUnit.FontSize -lt [double]$powerUnit.FontSize) ''

        # 网速上下行是唯一保留旧字号的地方：18 px SemiBold，避免把曲线挤掉。
        $upValue = Read-Field $ovPage '_upValue' $flags
        $downValue = Read-Field $ovPage '_downValue' $flags
        Check 'the network up value keeps 18 px SemiBold' (($null -ne $upValue) -and ([double]$upValue.FontSize -eq 18) -and ($upValue.FontWeight -eq [System.Windows.FontWeights]::SemiBold)) ''
        Check 'the network down value keeps 18 px SemiBold' (($null -ne $downValue) -and ([double]$downValue.FontSize -eq 18) -and ($downValue.FontWeight -eq [System.Windows.FontWeights]::SemiBold)) ''

        $fanBar = Read-Field $freshCard '_bar' $flags
        $powerCpuBar = Find-ById $ovRoot 'PowerCpuBar'
        $powerGpuBar = Find-ById $ovRoot 'PowerGpuBar'
        Check 'the fan bar uses the shared 16-segment style' (($null -ne $fanBar) -and ($fanBar.BarStyle.ToString() -eq 'Segments')) ''
        Check 'the fan bar matches the power bars at 22 px / 200 px' (($null -ne $fanBar) -and ($null -ne $powerCpuBar) -and ($null -ne $powerGpuBar) -and ([double]$fanBar.Height -eq 22) -and ([double]$fanBar.Width -eq 200) -and ([double]$powerCpuBar.Height -eq 22) -and ([double]$powerGpuBar.Height -eq 22)) ("fan=" + $(if($null -ne $fanBar){$fanBar.Height}else{'none'}) + "x" + $(if($null -ne $fanBar){$fanBar.Width}else{'none'}) + " cpu=" + $(if($null -ne $powerCpuBar){$powerCpuBar.Height}else{'none'}) + " gpu=" + $(if($null -ne $powerGpuBar){$powerGpuBar.Height}else{'none'}))
        # 分段/胶囊是全局设置：风扇与功耗卡也必须跟随。
        $ovWindow.CurrentSettings.IndicatorStyle = [QuadVitals.Core.Settings]::IndicatorCapsule
        $ovPage.ApplyIndicatorStyle() | Out-Null
        Check 'capsule applies to the fan bar' ($fanBar.BarStyle.ToString() -eq 'Capsule') "style=$($fanBar.BarStyle)"
        Check 'capsule applies to both power bars' (($powerCpuBar.BarStyle.ToString() -eq 'Capsule') -and ($powerGpuBar.BarStyle.ToString() -eq 'Capsule')) "cpu=$($powerCpuBar.BarStyle) gpu=$($powerGpuBar.BarStyle)"
        $ovWindow.CurrentSettings.IndicatorStyle = [QuadVitals.Core.Settings]::IndicatorSegments
        $ovPage.ApplyIndicatorStyle() | Out-Null
        Check 'segments restores the fan bar' ($fanBar.BarStyle.ToString() -eq 'Segments') "style=$($fanBar.BarStyle)"
        Check 'segments restores both power bars' (($powerCpuBar.BarStyle.ToString() -eq 'Segments') -and ($powerGpuBar.BarStyle.ToString() -eq 'Segments')) "cpu=$($powerCpuBar.BarStyle) gpu=$($powerGpuBar.BarStyle)"

        $deviceCard = Find-ById $ovRoot 'DeviceCard'
        # 卡片左边到导航胶囊右边的距离 = 卡片右边到窗口右边的距离。
        $navGlider = Find-ById $ovRoot 'NavGlider'
        if (($null -ne $navGlider) -and ($null -ne $deviceCard)) {
            $navPoint = $navGlider.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0)))
            $cardPoint = $deviceCard.TransformToAncestor($ovRoot).Transform((New-Object System.Windows.Point(0, 0)))
            $leftGap = $cardPoint.X - ($navPoint.X + $navGlider.ActualWidth)
            $rightGap = $ovRoot.ActualWidth - ($cardPoint.X + $deviceCard.ActualWidth)
            Check 'the page leaves equal gaps to the nav and the window edge' ([math]::Abs($leftGap - $rightGap) -le 1) ("left=" + [math]::Round($leftGap, 1) + " right=" + [math]::Round($rightGap, 1))
        }
        # 功耗卡图标列与读数之间留 16 px。
        $powerCard = Find-ById $ovRoot 'PowerCard'
        $powerBody = $null
        foreach ($candidate in (Find-ByTypeName $powerCard 'Grid')) {
            if (($candidate.ColumnDefinitions.Count -eq 3) -and ([double]$candidate.ColumnDefinitions[1].Width.Value -eq 16)) { $powerBody = $candidate; break }
        }
        Check 'the power icon column leaves a 16 px gap' ($null -ne $powerBody) ''
        # 网速标题行：IP 值与操作图标之间留 16 px，两组不再挤成一串。
        $lanValue = Find-ById $ovRoot 'LanIpValue'
        $ipGrid = if ($null -eq $lanValue) { $null } else { $lanValue.Parent }
        Check 'the IP group and action icons are two groups' (($null -ne $ipGrid) -and ($ipGrid.ColumnDefinitions.Count -ge 6) -and ([double]$ipGrid.ColumnDefinitions[5].Width.Value -eq 16)) ''
        # 设备、网速、风扇三张卡必须同高；功耗卡扛着三行读数，允许高十几像素，但不能变成另一档卡片。
        $coreHeights = @([double]$deviceCard.ActualHeight, [double]$networkCard.ActualHeight, [double]$freshCard.ActualHeight)
        $coreMin = ($coreHeights | Measure-Object -Minimum).Minimum
        $coreMax = ($coreHeights | Measure-Object -Maximum).Maximum
        Check 'device, network and fan cards share one height' (($coreMax - $coreMin) -le 1.0) ("heights=" + (($coreHeights | ForEach-Object { [Math]::Round($_, 1) }) -join ','))
        $powerHeight = [double]$powerCard.ActualHeight
        Check 'the power card stays within a compact band' (($powerHeight -ge 204) -and ($powerHeight -le 240)) ("power=" + [Math]::Round($powerHeight, 1))

        # IP 枚举是后台活儿：把 5 秒缓存打旧后调一次 Refresh，界面线程只应把活儿排出去，
        # 否则每 5 秒就会在 1 Hz 刷新里卡掉一两帧。
        $lanStamp = $ovPage.GetType().GetField('_lanCheckedUtc', $flags)
        if ($null -ne $lanStamp) { $lanStamp.SetValue($ovPage, [DateTime]::MinValue) }
        Call $ovPage 'Refresh' @() | Out-Null
        Check 'the lan ip probe is handed to a worker thread' ((Read-Field $ovPage '_lanProbeRunning' $flags) -eq $true) ''
        Pump 500
        Check 'the lan ip probe clears its in-flight flag' ((Read-Field $ovPage '_lanProbeRunning' $flags) -eq $false) ''

        # 离线自动恢复的退避表：首次立即，之后 3 / 5 / 10 / 30 秒。
        $recoveryType = $asm.GetType('QuadVitals.Settings.AgentAutoRecovery')
        $staticFlags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
        $delays = $recoveryType.GetField('DelaysSeconds', $staticFlags).GetValue($null)
        Check 'offline recovery backs off 0/3/5/10/30' (($delays -join ',') -eq '0,3,5,10,30') ("delays=" + ($delays -join ','))

        "== diagnostics window =="
        $diagType = $asm.GetType('QuadVitals.Settings.DiagnosticsWindow')
        Check 'the tray diagnostics window exists' ($null -ne $diagType) ''
        if ($null -ne $diagType) {
            $diag = [Activator]::CreateInstance($diagType)
            $diagRoot = $diag.Content
            $diagTitle = Find-ById $diagRoot 'DiagnosticsTitle'
            Check 'the diagnostics window has a title' (($null -ne $diagTitle) -and ($diagTitle.Text -eq '诊断信息')) "title=$(if($null -ne $diagTitle){$diagTitle.Text}else{'none'})"
            $diagIntro = Find-ById $diagRoot 'DiagnosticsIntro'
            Check 'the diagnostics window explains itself' (($null -ne $diagIntro) -and $diagIntro.Text.Contains('传感器进程')) ''
            # One line carries both the reading and where the data comes from; the sources used to sit
            # at the bottom of the window, far from the table they explain.
            Check 'the diagnostics hint merges the data sources' (($null -ne $diagIntro) -and $diagIntro.Text.Contains('LHM') -and $diagIntro.Text.Contains('NVML') -and $diagIntro.Text.Contains('--')) "text=$(if($null -ne $diagIntro){$diagIntro.Text}else{'none'})"
            Check 'the separate sources line is gone' ($null -eq (Find-ById $diagRoot 'DiagnosticsNote')) ''
            $diagStatus = Find-ById $diagRoot 'DiagnosticsStatus'
            Check 'the diagnostics window shows the agent state' (($null -ne $diagStatus) -and ($diagStatus.Text.Length -gt 0)) ''
            $diagMissing = Find-ById $diagRoot 'DiagnosticsMissing'
            Check 'the diagnostics window shows the missing-metric line' ($null -ne $diagMissing) ''
            $diagShared = Find-ById $diagRoot 'DiagnosticsSharedMemory'
            Check 'the diagnostics window names the shared memory map' (($null -ne $diagShared) -and ($diagShared.Text -eq 'Local\QuadVitals.Metrics.v1')) ''
            $diagClose = Find-ById $diagRoot 'DiagnosticsClose'
            Check 'the diagnostics window has a close button' ($null -ne $diagClose) ''
            Check 'the diagnostics card uses the app card colour' ($diagRoot.Background -eq [QuadVitals.UI.Theme]::SurfaceCardBrush) "bg=$($diagRoot.Background)"
            $diag.Close()
        }
        $traySource = Get-Content -Encoding UTF8 -Raw (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\QuadVitals.Agent\TrayHost.cs')
        Check 'the tray diagnostics opens the dark window' ($traySource.Contains('OpenDashboard("--diagnostics")')) ''
        Check 'the tray diagnostics no longer builds a MessageBox body' (-not $traySource.Contains('MessageBox.Show(text.ToString()')) ''
        $programSource = Get-Content -Encoding UTF8 -Raw (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\QuadVitals.Settings\Program.cs')
        Check 'the settings app accepts --diagnostics' ($programSource.Contains('--diagnostics')) ''
        $settingsSource = Get-Content -Encoding UTF8 -Raw (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\QuadVitals.Settings\SettingsWindow.cs')
        Check 'the title bar no longer maximizes on double click' (-not $settingsSource.Contains('e.ClickCount == 2')) ''

        # Animation clock. The dashboard is meant to sit still: a fan that is not on screen must not
        # keep a frame loop, and no loop may run faster than the shared cap (a 165 Hz panel used to
        # get 165 callbacks a second for a fan turning once).
        "== animation clock =="
        $loopType = $uiAsm.GetType('QuadVitals.UI.FrameLoop')
        Check 'the UI assembly owns the shared frame clock' ($null -ne $loopType) ''
        # A window that was never shown makes every child invisible, so the loop (by design) refuses
        # to run and this section would pass for the wrong reason. Off-screen but shown, like the
        # about-page case above.
        $animWindow = [Activator]::CreateInstance($windowType, @('overview'))
        $animWindow.Width = 1280
        $animWindow.Height = 853
        $animWindow.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
        $animWindow.ShowActivated = $false
        $animWindow.ShowInTaskbar = $false
        $animWindow.Left = -5000
        $animWindow.Top = -5000
        $animWindow.Show()
        Pump 250
        $animRoot = $animWindow.Content
        $animRoot.UpdateLayout()
        $animPage = $null
        $animQueue = New-Object 'System.Collections.Queue'
        $animQueue.Enqueue($animRoot)
        while ($animQueue.Count -gt 0 -and $null -eq $animPage) {
            $e = $animQueue.Dequeue()
            if ($e.GetType() -eq $ovType) { $animPage = $e; break }
            if ($e -is [System.Windows.Media.Visual]) {
                $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
                for ($i = 0; $i -lt $n; $i++) { $animQueue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
            }
        }
        Check 'the animation window has an overview page' ($null -ne $animPage) ''
        if ($null -ne $animPage) {
            # This window refreshes once a second from the real agent, which on a machine without a
            # fan header reports no fans and stops the loop on purpose. Drive it by hand instead.
            $animPage.GetType().GetField('_timer', $flags).GetValue($animPage).Stop()
            $animPage.GetType().GetField('_ipTimer', $flags).GetValue($animPage).Stop()
            $animCard = $ovType.GetField('_fanCard', $flags).GetValue($animPage)
            $animLoop = $animCard.GetType().GetField('_loop', $flags).GetValue($animCard)
            $isAnimating = $animCard.GetType().GetProperty('IsAnimating')
            # 动画只跟 renderPolicy：没有悬停、常驻这些卡片级开关。
            # 指针离开卡面也必须继续跑（MouseLeave 不再停循环）。
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
            Pump 300
            Check 'the fan card animates without hover' ([bool]$isAnimating.GetValue($animCard, $null)) ''
            $hoverOff = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
            $hoverOff.RoutedEvent = [System.Windows.Input.Mouse]::MouseLeaveEvent
            $animCard.RaiseEvent($hoverOff)
            Pump 200
            Check 'the fan card keeps running after the pointer leaves' ([bool]$isAnimating.GetValue($animCard, $null)) ''

            # 功耗卡的闪电只在看得见的窗口里才跑帧，所以放在这个已显示的窗口里验。
            $animPower = $ovType.GetField('_powerCard', $flags).GetValue($animPage)
            $animPower.GetType().GetMethod('Update').Invoke($animPower, @($powerSnap, $true)) | Out-Null
            $powerIsAnimating = $animPower.GetType().GetProperty('IsAnimating')
            Pump 300
            Check 'the power icon animates without hover' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''
            $powerLeave = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
            $powerLeave.RoutedEvent = [System.Windows.Input.Mouse]::MouseLeaveEvent
            $animPower.RaiseEvent($powerLeave)
            Pump 200
            Check 'the power icon keeps running after the pointer leaves' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''

            $before = [long]$animLoop.FramesDelivered
            Pump 1000
            $frames = [long]$animLoop.FramesDelivered - $before
            Check 'the overview keeps delivering frames without hover' ($frames -gt 0) "frames=$frames"
            # 保存一次静默设置（同内容）不能把当前概览页 Dispose 掉，否则风扇会从这一秒起永久停转。
            $animWindow.SaveSettingsQuietly($animWindow.CurrentSettings.Clone())
            # 保存会重配渲染环境并重启帧循环，给一次完整的 1 Hz 刷新再接断言。
            Pump 300
            Check 'a quiet save keeps the visible overview alive' (Wait-Until { [bool]$isAnimating.GetValue($animCard, $null) }) ''

            $before = [long]$animLoop.FramesDelivered
            Pump 1000
            $frames = [long]$animLoop.FramesDelivered - $before
            Check 'the loop is capped at 30 frames a second, not the monitor refresh' (($frames -ge 8) -and ($frames -le 45)) "frames=$frames"

            $animWindow.WindowState = [System.Windows.WindowState]::Minimized
            Pump 250
            Check 'a minimized dashboard leaves the frame clock' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
            $before = [long]$animLoop.FramesDelivered
            Pump 700
            $frames = [long]$animLoop.FramesDelivered - $before
            Check 'a minimized dashboard delivers no frames at all' ($frames -eq 0) "frames=$frames"

            $animWindow.WindowState = [System.Windows.WindowState]::Normal
            Pump 250
            # Restoring restarts the page's own refresh timer (that is the fix we just made), so stop
            # it again before driving the card by hand; otherwise a real snapshot can empty the card
            # in the middle of the next two checks.
            $animPage.GetType().GetField('_timer', $flags).GetValue($animPage).Stop()
            # Restoring refreshes once from the real agent, which empties the card on this
            # machine (no fan channels), so feed the synthetic snapshot again before asserting.
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
            Check 'restoring the window resumes the loop' (Wait-Until { [bool]$isAnimating.GetValue($animCard, $null) }) ''

            # Focus pause: the window is still visible, but every frame loop must stop. The
            # overview timer keeps reading snapshots and pushing history so the curve has no gap.
            # The shipped default is now "always render", so pin the unfocused policy first; the
            # window's 1 Hz policy tick reads the same settings object and keeps it pinned.
            $envType = $uiAsm.GetType('QuadVitals.UI.RenderEnvironment')
            $configurePolicy = $envType.GetMethod('ConfigureRenderPolicy')
            $configureHover = $envType.GetMethod('ConfigureHoverOnlyCards')
            $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
            # "$ovWindow" is still open and its own 1 Hz policy tick would otherwise push the
            # default back every second: the render policy is process-wide by design.
            $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
            $configurePolicy.Invoke($null, @($true, $false)) | Out-Null
            Pump 150
            $activatedHandler = $loopType.GetField('_activatedHandler', $flags).GetValue($animLoop)
            $deactivatedHandler = $loopType.GetField('_deactivatedHandler', $flags).GetValue($animLoop)
            $activatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
            Pump 200
            Check 'focusing the animation window keeps the loop alive' ([bool]$isAnimating.GetValue($animCard, $null)) ''
            $deactivatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
            Pump 200
            Check 'losing focus stops the fan loop' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
            $before = [long]$animLoop.FramesDelivered
            Pump 700
            $frames = [long]$animLoop.FramesDelivered - $before
            Check 'a deactivated window delivers no frames' ($frames -eq 0) "frames=$frames"

            $renderSuspended = $ovType.GetField('_renderSuspended', $flags)
            $tickField = $ovType.GetField('_tick', $flags)
            $pageTimer = $animPage.GetType().GetField('_timer', $flags).GetValue($animPage)
            $pageTimer.Start()
            $deactivatedPage = $ovType.GetMethod('OnHostDeactivated', $flags)
            $activatedPage = $ovType.GetMethod('OnHostActivated', $flags)
            $deactivatedPage.Invoke($animPage, @($animWindow, [EventArgs]::Empty)) | Out-Null
            $tickBefore = [int]$tickField.GetValue($animPage)
            Pump 1300
            $tickAfter = [int]$tickField.GetValue($animPage)
            Check 'the overview keeps sampling while deactivated' ($tickAfter -gt $tickBefore) "ticks=$tickBefore->$tickAfter"
            Check 'the overview marks itself as not painting' ([bool]$renderSuspended.GetValue($animPage)) ''
            Check 'the overview timer keeps running while deactivated' ($pageTimer.IsEnabled) ''
            $activatedPage.Invoke($animPage, @($animWindow, [EventArgs]::Empty)) | Out-Null
            Pump 200
            Check 'reactivating the overview resumes painting' (-not [bool]$renderSuspended.GetValue($animPage)) ''
            # Stop the 1 Hz page timer for the rest of the loop checks: this off-screen window is
            # never really activated (ShowActivated=false), so a real refresh would re-suspend the
            # frame loop via Start()'s IsActive re-read. A real focused window keeps IsActive true.
            $pageTimer.Stop()
            # The real agent may have no fan channels on this machine, so feed the synthetic frame
            # again before asserting the loop is back. The synthetic activation has to come after
            # Update(): Start() re-reads Window.IsActive, and this off-screen window was never
            # really activated (ShowActivated=false).
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
            $activatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
            Pump 200
            Check 'regaining focus resumes the loop' ([bool]$isAnimating.GetValue($animCard, $null)) ''

            # "全局重绘": losing focus must not stop anything.
            $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
            # The other open settings window shares the same process-wide policy; pin it too.
            $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
            $configurePolicy.Invoke($null, @($false, $false)) | Out-Null
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
            $activatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
            Pump 250
            $deactivatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
            Pump 250
            Check 'global repaint keeps the fan loop after focus loss' (Wait-Until { [bool]$isAnimating.GetValue($animCard, $null) }) ''
            $before = [long]$animLoop.FramesDelivered
            Check 'global repaint delivers frames after focus loss' (Wait-Until { ([long]$animLoop.FramesDelivered - $before) -gt 0 }) ("frames=" + ([long]$animLoop.FramesDelivered - $before))

            # "全屏停止动画": same unfocused state, but a fullscreen foreground stops it.
            $setFullscreen = $envType.GetMethod('SetFullscreenForTests', [Reflection.BindingFlags]'NonPublic,Static')
            try {
                # No fullscreen app yet: losing focus must not stop anything under this policy.
                $setFullscreen.Invoke($null, @($false)) | Out-Null
                $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyFullscreen
                $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyFullscreen
                $configurePolicy.Invoke($null, @($false, $true)) | Out-Null
                Pump 250
                Check 'fullscreen policy keeps the fan loop while unfocused' ([bool]$isAnimating.GetValue($animCard, $null)) ''
                Check 'fullscreen policy keeps the overview painting while unfocused' (-not [bool]$renderSuspended.GetValue($animPage)) ''

                $setFullscreen.Invoke($null, @($true)) | Out-Null
                Pump 300
                Check 'fullscreen policy stops the fan loop' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
                Check 'fullscreen policy marks the overview not painting' ([bool]$renderSuspended.GetValue($animPage)) ''
                $setFullscreen.Invoke($null, @($false)) | Out-Null
                Pump 250
                # The resume path refreshes once from the real agent; this machine may have no fan
                # channels, so feed the synthetic snapshot again before checking the loop.
                $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
                $activatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
                Check 'leaving fullscreen resumes the fan loop' (Wait-Until { [bool]$isAnimating.GetValue($animCard, $null) }) ''
                Check 'leaving fullscreen resumes overview painting' (-not [bool]$renderSuspended.GetValue($animPage)) ''
            }
            finally {
                $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
                $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
                $configurePolicy.Invoke($null, @($true, $false)) | Out-Null
                $setFullscreen.Invoke($null, @($null)) | Out-Null
                Pump 150
            }

            # An empty card used to keep spinning a still fan and rebuilding fourteen invisible
            # trails twenty times a second; the loop has to stop with the data.
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($null, $false)) | Out-Null
            Pump 150
            Check 'a card with no reading stops the loop' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
            $activatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
            Pump 250
            Check 'the next reading starts it again' ([bool]$isAnimating.GetValue($animCard, $null)) ''

            # The 1 Hz overview refresh is the last thing that still wakes a window nobody can see:
            # every refresh ends in a repaint, and a repaint of a layered window is CPU work.
            $animTimer = $animPage.GetType().GetField('_timer', $flags).GetValue($animPage)
            $animTimer.Start()
            $animWindow.WindowState = [System.Windows.WindowState]::Minimized
            Pump 300
            Check 'the overview refresh pauses while minimized' (-not $animTimer.IsEnabled) ''
            $animWindow.WindowState = [System.Windows.WindowState]::Normal
            Pump 300
            Check 'restoring resumes the overview refresh' ($animTimer.IsEnabled) ''

            # RDP / WPF tier 0: stop every frame loop, but keep the 1 Hz data refresh alive.
            Check 'the render environment gate exists' ($null -ne $envType) ''
            $setOverride = $envType.GetMethod('SetOverrideForTests', [Reflection.BindingFlags]'NonPublic,Static')
            try {
                $setOverride.Invoke($null, @($true)) | Out-Null
                Pump 250
                Check 'RDP or tier 0 stops the fan loop' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
                $before = [long]$animLoop.FramesDelivered
                Pump 700
                $frames = [long]$animLoop.FramesDelivered - $before
                Check 'RDP or tier 0 delivers no fan frames' ($frames -eq 0) "frames=$frames"

                $barType = $uiAsm.GetType('QuadVitals.UI.MetricBar')
                $bar = [Activator]::CreateInstance($barType, @([QuadVitals.UI.Theme]::AccentGreen, [QuadVitals.UI.Theme]::TrackGrey))
                $barSet = @($barType.GetMethods() | Where-Object { $_.Name -eq 'SetValue' -and $_.GetParameters().Count -eq 1 })[0]
                $barSet.Invoke($bar, @(0.2)) | Out-Null
                $barSet.Invoke($bar, @(0.8)) | Out-Null
                $barDisplay = [double]$barType.GetField('_displayValue', $flags).GetValue($bar)
                Check 'RDP or tier 0 snaps bar values' ([Math]::Abs($barDisplay - 0.8) -lt 0.001) "display=$barDisplay"
                $barType.GetMethod('SetIoWave').Invoke($bar, @(1.0, 1.0)) | Out-Null
                Check 'RDP or tier 0 starts no bar loop' (-not [bool]$barType.GetProperty('IsRunningForTests').GetValue($bar, $null)) ''
            }
            finally {
                $setOverride.Invoke($null, @($null)) | Out-Null
            }
            Pump 250
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
            $activatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
            Pump 250
            Check 'leaving RDP or tier 0 resumes the fan loop' ([bool]$isAnimating.GetValue($animCard, $null)) ''

            # Battery: pause by default, but the user can turn the policy off and animations return.
            $configureBattery = $envType.GetMethod('ConfigureBatteryPolicy')
            $setPower = $envType.GetMethod('SetPowerLineStatusForTests', [Reflection.BindingFlags]'NonPublic,Static')
            try {
                # Keep the window's own settings in sync: its 1 Hz policy tick would otherwise
                # put the default (pause=true) back within the same second.
                $animWindow.CurrentSettings.PauseAnimationsOnBattery = $true
                $setPower.Invoke($null, @([System.Windows.PowerLineStatus]::Offline)) | Out-Null
                $configureBattery.Invoke($null, @($true)) | Out-Null
                Pump 250
                Check 'battery mode pauses animations' ([bool]$envType.GetProperty('AnimationsDisabled').GetValue($null)) ''
                Check 'battery mode stops the fan loop' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
                $animWindow.CurrentSettings.PauseAnimationsOnBattery = $false
                $configureBattery.Invoke($null, @($false)) | Out-Null
                # 固定 250 ms 不等价于「动画循环跑过一拍」：机器忙时帧还没送到就断言了。
                Check 'turning the battery policy off restores animations' (Wait-Until { -not [bool]$envType.GetProperty('AnimationsDisabled').GetValue($null) }) ''
                $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
                $activatedHandler.Invoke($animLoop, [EventArgs]::Empty) | Out-Null
                Check 'battery override resumes the fan loop' (Wait-Until { [bool]$isAnimating.GetValue($animCard, $null) }) ''
            }
            finally {
                $setPower.Invoke($null, @($null)) | Out-Null
                $configureBattery.Invoke($null, @($true)) | Out-Null
            }
        }
        # 所有逐帧动画只由 renderPolicy 控制：风扇与功耗卡不再有悬停/常驻开关。
        "== render policy drives every animation =="
        $powerLoop = Read-Field $animPower '_loop' $flags
        $fanAct = Read-Field $animLoop '_activatedHandler' $flags
        $fanDeact = Read-Field $animLoop '_deactivatedHandler' $flags
        $powAct = Read-Field $powerLoop '_activatedHandler' $flags
        $powDeact = Read-Field $powerLoop '_deactivatedHandler' $flags
        $rewake = {
            $animCard.GetType().GetMethod('Update').Invoke($animCard, @($snap, $true)) | Out-Null
            $animPower.GetType().GetMethod('Update').Invoke($animPower, @($powerSnap, $true)) | Out-Null
            $fanAct.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
            $powAct.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
            Pump 250
        }

        # 全局重绘：没有悬停也必须跑，失焦也不停。
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
        $configurePolicy.Invoke($null, @($false, $false)) | Out-Null
        & $rewake
        Check 'always runs the fan without hover' ([bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'always runs the power card without hover' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        $fanDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        $powDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        Pump 250
        Check 'always keeps the fan after focus loss' ([bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'always keeps the power card after focus loss' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''

        # 悬停档：只把风扇与功耗卡的门控交给指针；其他动画仍按全局重绘继续。
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyHover
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyHover
        $configurePolicy.Invoke($null, @($false, $false)) | Out-Null
        $configureHover.Invoke($null, @($true)) | Out-Null
        & $rewake
        Check 'hover policy keeps the fan idle without the pointer' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'hover policy keeps the power card idle without the pointer' (-not [bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        $hoverEnter = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
        $hoverEnter.RoutedEvent = [System.Windows.Input.Mouse]::MouseEnterEvent
        $animCard.RaiseEvent($hoverEnter)
        $animPower.RaiseEvent($hoverEnter)
        Pump 250
        Check 'hover policy runs the fan while hovered' ([bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'hover policy runs the power card while hovered' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        $fanDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        $powDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        Pump 250
        Check 'hover policy ignores focus loss while hovered' (([bool]$isAnimating.GetValue($animCard, $null)) -and ([bool]$powerIsAnimating.GetValue($animPower, $null))) ''
        $hoverLeave = New-Object System.Windows.Input.MouseEventArgs([System.Windows.Input.Mouse]::PrimaryDevice, 0)
        $hoverLeave.RoutedEvent = [System.Windows.Input.Mouse]::MouseLeaveEvent
        $animCard.RaiseEvent($hoverLeave)
        $animPower.RaiseEvent($hoverLeave)
        Pump 250
        Check 'hover policy stops the fan after the pointer leaves' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'hover policy stops the power card after the pointer leaves' (-not [bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        # 先把两个窗口的策略写离 hover，再关掉 HoverOnlyCards；否则 1 Hz 策略 tick 会把它设回 true。
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
        $configureHover.Invoke($null, @($false)) | Out-Null

        # 失焦停止：两张卡都停；回前台都恢复，且停着时不再出帧。
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
        $configurePolicy.Invoke($null, @($true, $false)) | Out-Null
        & $rewake
        Check 'unfocused starts both cards while focused' (([bool]$isAnimating.GetValue($animCard, $null)) -and ([bool]$powerIsAnimating.GetValue($animPower, $null))) ''
        $fanDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        $powDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        Pump 250
        Check 'unfocused stops the fan after focus loss' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'unfocused stops the power card after focus loss' (-not [bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        $fanBefore = [long]$animLoop.FramesDelivered
        $powBefore = [long]$powerLoop.FramesDelivered
        Pump 500
        Check 'unfocused delivers no fan frames' (([long]$animLoop.FramesDelivered - $fanBefore) -eq 0) ''
        Check 'unfocused delivers no power frames' (([long]$powerLoop.FramesDelivered - $powBefore) -eq 0) ''
        & $rewake
        Check 're-focus resumes the fan' ([bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 're-focus resumes the power card' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''

        # 全屏停止：全屏时两张卡都停，离开全屏都恢复。
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyFullscreen
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyFullscreen
        $configurePolicy.Invoke($null, @($false, $true)) | Out-Null
        & $rewake
        Check 'fullscreen policy runs both cards while not fullscreen' (([bool]$isAnimating.GetValue($animCard, $null)) -and ([bool]$powerIsAnimating.GetValue($animPower, $null))) ''
        $setFullscreen.Invoke($null, @($true)) | Out-Null
        Pump 300
        Check 'fullscreen stops the fan' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'fullscreen stops the power card' (-not [bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        $setFullscreen.Invoke($null, @($false)) | Out-Null
        & $rewake
        Check 'leaving fullscreen resumes the fan' ([bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'leaving fullscreen resumes the power card' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''

        # 策略在后台切换要立刻生效：先失焦，再分别切到「失焦停止 / 全局重绘」。
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
        $configurePolicy.Invoke($null, @($false, $false)) | Out-Null
        & $rewake
        $fanDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        $powDeact.Invoke($animWindow, [EventArgs]::Empty) | Out-Null
        Pump 200
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyUnfocused
        $configurePolicy.Invoke($null, @($true, $false)) | Out-Null
        Pump 250
        Check 'switching to unfocused stops the fan while backgrounded' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'switching to unfocused stops the power card while backgrounded' (-not [bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        $animWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
        $ovWindow.CurrentSettings.RenderPolicy = [QuadVitals.Core.Settings]::RenderPolicyAlways
        $configurePolicy.Invoke($null, @($false, $false)) | Out-Null
        Pump 250
        Check 'switching back to always resumes the fan while backgrounded' ([bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'switching back to always resumes the power card while backgrounded' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''

        # 最小化：两张卡都停；还原后都恢复。
        $animWindow.WindowState = [System.Windows.WindowState]::Minimized
        Pump 300
        Check 'minimizing stops the fan' (-not [bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'minimizing stops the power card' (-not [bool]$powerIsAnimating.GetValue($animPower, $null)) ''
        $animWindow.WindowState = [System.Windows.WindowState]::Normal
        & $rewake
        Check 'restoring resumes the fan' ([bool]$isAnimating.GetValue($animCard, $null)) ''
        Check 'restoring resumes the power card' ([bool]$powerIsAnimating.GetValue($animPower, $null)) ''

        # 卡片级开关已经删除，不能再有 "常驻动画" 这类独立属性/方法。
        Check 'the fan card has no card-level animation switch' (($null -eq $animCard.GetType().GetProperty('AlwaysAnimate')) -and ($null -eq $animCard.GetType().GetMethod('SetAlwaysAnimate'))) ''
        Check 'the power card has no card-level animation switch' (($null -eq $animPower.GetType().GetProperty('AlwaysAnimate')) -and ($null -eq $animPower.GetType().GetMethod('SetAlwaysAnimate'))) ''

        $setFullscreen.Invoke($null, @($null)) | Out-Null
        $animWindow.Close()

        # Edit-mode jiggle. It used to be two RepeatBehavior.Forever animations per tile: fifteen
        # tiles meant fifteen transforms changing every vertical blank, forever, which repaints the
        # whole window while the page is open. Same motion, now driven by the shared clock.
        $editWindow = [Activator]::CreateInstance($windowType, @('elements'))
        $editWindow.Width = 1280
        $editWindow.Height = 853
        $editWindow.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
        $editWindow.ShowActivated = $false
        $editWindow.ShowInTaskbar = $false
        $editWindow.Left = -5000
        $editWindow.Top = -5000
        $editWindow.Show()
        Pump 250
        $editRoot = $editWindow.Content
        $editRoot.UpdateLayout()
        $editPage = $null
        $editQueue = New-Object 'System.Collections.Queue'
        $editQueue.Enqueue($editRoot)
        while ($editQueue.Count -gt 0 -and $null -eq $editPage) {
            $e = $editQueue.Dequeue()
            if ($e.GetType() -eq $pageType) { $editPage = $e; break }
            if ($e -is [System.Windows.Media.Visual]) {
                $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
                for ($i = 0; $i -lt $n; $i++) { $editQueue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
            }
        }
        Check 'the edit window has the elements page' ($null -ne $editPage) ''
        if ($null -ne $editPage) {
            $isJiggling = $editPage.GetType().GetProperty('IsJiggling')
            Check 'the page starts without a jiggle loop' (-not [bool]$isJiggling.GetValue($editPage, $null)) ''
            $pageType.GetMethod('SetEditMode', $flags).Invoke($editPage, @($true)) | Out-Null
            Pump 300
            Check 'edit mode jiggles on the shared clock' ([bool]$isJiggling.GetValue($editPage, $null)) ''
            $editLoop = $editPage.GetType().GetField('_jiggleLoop', $flags).GetValue($editPage)
            $before = [long]$editLoop.FramesDelivered
            Pump 1000
            $frames = [long]$editLoop.FramesDelivered - $before
            Check 'the jiggle is capped like every other loop' (($frames -ge 8) -and ($frames -le 45)) "frames=$frames"
            $editWindow.WindowState = [System.Windows.WindowState]::Minimized
            Pump 250
            Check 'a minimized window stops the jiggle too' (-not [bool]$isJiggling.GetValue($editPage, $null)) ''
            $editWindow.WindowState = [System.Windows.WindowState]::Normal
            Pump 250
            $pageType.GetMethod('SetEditMode', $flags).Invoke($editPage, @($false)) | Out-Null
            Pump 200
            Check 'leaving edit mode stops the jiggle' (-not [bool]$isJiggling.GetValue($editPage, $null)) ''
        }
        $editWindow.Close()
    }
$ovWindow.Close()

Complete-Run 'settings-ui-overview-dynamics'
