# UI behaviour regression suite for the 显示项 element list.
#
# Why: the drag / delete / edit-mode rules kept regressing because the only checks were throwaway
# probes under build/tmp (gitignored, run by nothing). This file holds them: every agreed rule
# is one assertion, driven through the real WPF code paths (StartDrag / FollowMouse / MoveElement /
# RemoveElement / SelectElement plus routed mouse events).
#
# ASCII only: PowerShell 5.1 reads BOM-less UTF-8 as GBK and would garble the source.
param(
    [string]$UiDir = ''
)
# Assemblies, counters, tree finders and the settings accessors live in the shared harness;
# the device-dialog assertions live in device-dialog.ps1 and use the same one.
. (Join-Path $PSScriptRoot 'ui-harness.ps1') -UiDir $UiDir

$pageType = $asm.GetType('QuadVitals.Settings.ElementsPage')
$elementType = $coreAsm.GetType('QuadVitals.Core.BandElement')
function Field($ctx, [string]$name) { return $pageType.GetField($name, $flags).GetValue($ctx.Page) }
function Tiles($ctx) { return Field $ctx '_tiles' }
function RowTop($ctx) { return Field $ctx '_rowTop' }
function RowBottom($ctx) { return Field $ctx '_rowBottom' }
function Layout($ctx) {
    $ctx.Root.Measure((New-Object System.Windows.Size(1280, 853)))
    $ctx.Root.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
    $ctx.Root.UpdateLayout()
}
function Ids($ctx) { return ($ctx.Draft.Items | ForEach-Object { $_.Id }) -join ',' }
function Count-AddTiles($panel) {
    $n = 0
    foreach ($child in $panel.Children) { if ((AutoId $child) -eq 'ElementAddTile') { $n++ } }
    return $n
}
function Click-Route($element, $routedEvent) {
    $args = New-Object System.Windows.Input.MouseButtonEventArgs(
        [System.Windows.Input.Mouse]::PrimaryDevice, 0, [System.Windows.Input.MouseButton]::Left)
    $args.RoutedEvent = $routedEvent
    $element.RaiseEvent($args)
}
function Get-Badge($tile) {
    foreach ($child in $tile.Child.Children) {
        if ($child -is [System.Windows.FrameworkElement] -and (AutoId $child) -eq 'ElementDeleteBadge') { return $child }
    }
    return $null
}
function Shot($ctx) {
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(1280, 853, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($ctx.Page)
    return $bmp
}
function Diff-Pixels($a, $b, [int]$x0, [int]$y0, [int]$w, [int]$h) {
    $stride = 1280 * 4
    $bufA = New-Object byte[] ($stride * 853)
    $bufB = New-Object byte[] ($stride * 853)
    $a.CopyPixels($bufA, $stride, 0)
    $b.CopyPixels($bufB, $stride, 0)
    $diff = 0
    for ($y = $y0; $y -lt ($y0 + $h); $y++) {
        for ($x = $x0; $x -lt ($x0 + $w); $x++) {
            $i = $y * $stride + $x * 4
            if ($bufA[$i] -ne $bufB[$i] -or $bufA[$i+1] -ne $bufB[$i+1] -or $bufA[$i+2] -ne $bufB[$i+2]) { $diff++ }
        }
    }
    return $diff
}

# Deterministic page: exactly $count elements, ids e0..eN (index 0 is the shipped CPU element,
# the rest are text). Never depends on the developer's own settings.json.
function New-Case([int]$count, [switch]$EditMode) {
    $window = [Activator]::CreateInstance($windowType, @('elements'))
    $window.Width = 1280
    $window.Height = 853
    $root = $window.Content
    $root.Measure((New-Object System.Windows.Size(1280, 853)))
    $root.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
    $root.UpdateLayout()

    $page = $null
    $queue = New-Object 'System.Collections.Queue'
    $queue.Enqueue($root)
    while ($queue.Count -gt 0 -and $null -eq $page) {
        $e = $queue.Dequeue()
        if ($e.GetType() -eq $pageType) { $page = $e; break }
        $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
        for ($i = 0; $i -lt $n; $i++) { $queue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
    }
    if ($null -eq $page) { throw 'ElementsPage not found' }

    $ctx = [pscustomobject]@{ Window = $window; Root = $root; Page = $page; Draft = $null }
    $ctx.Draft = Field $ctx '_draft'
    while ($ctx.Draft.Items.Count -gt 1) { $ctx.Draft.Items.RemoveAt($ctx.Draft.Items.Count - 1) }
    # Index 0 must be the shipped CPU bar: the protection rules key off "the first CPU element",
    # so inheriting whatever the developer's own settings start with makes those cases lie.
    $first = $ctx.Draft.Items[0]
    $first.Type = 'bar'
    $first.Metric = 'cpu'
    $first.Lines = $null
    $first.HistoryColumns = 16
    # Render once so _topCount is clamped to the trimmed list before AddElement inserts at it.
    $pageType.GetMethod('RefreshList', $flags).Invoke($page, $null) | Out-Null
    Layout $ctx

    $add = $pageType.GetMethod('AddElement', $flags)
    for ($k = 1; $k -lt $count; $k++) { $add.Invoke($page, @($true)) | Out-Null }
    $pageType.GetMethod('RefreshList', $flags).Invoke($page, $null) | Out-Null
    Layout $ctx
    for ($i = 0; $i -lt $ctx.Draft.Items.Count; $i++) { $ctx.Draft.Items[$i].Id = "e$i" }

    if ($EditMode) {
        $pageType.GetMethod('SetEditMode', $flags).Invoke($page, @($true)) | Out-Null
        Layout $ctx
    }
    return $ctx
}

"== element spacing lives in the list card =="
$spacingCase = New-Case 3
Layout $spacingCase
$spacingBox = Find-ById $spacingCase.Page 'ElementsSpacingBox'
Check 'the elements page carries the spacing box' ($null -ne $spacingBox) ''
if ($null -ne $spacingBox) {
    $spacingValue = 0
    $numeric = [int]::TryParse($spacingBox.Text, [ref]$spacingValue)
    Check 'the spacing box holds the current 0-40 px value' ($numeric -and $spacingValue -ge 0 -and $spacingValue -le 20) "text=$($spacingBox.Text)"
    $secondRow = Field $spacingCase '_rowBottom'
    $rowY = $secondRow.TransformToAncestor($spacingCase.Page).Transform((New-Object System.Windows.Point(0, 0))).Y
    $boxY = $spacingBox.TransformToAncestor($spacingCase.Page).Transform((New-Object System.Windows.Point(0, 0))).Y
    Check 'the spacing box sits under the second row' ($boxY -gt $rowY) ("box=" + [math]::Round($boxY) + " row=" + [math]::Round($rowY))
    # The card hugs its content: a fixed height left a dead band between the tiles and the field.
    $gap = $boxY - ($rowY + $secondRow.ActualHeight)
    Check 'the spacing box follows the row without a dead band' ($gap -lt 26) ("gap=" + [math]::Round($gap))
    Check 'the spacing value is centred in its box' ($spacingBox.TextAlignment -eq [System.Windows.TextAlignment]::Center) ("align=" + $spacingBox.TextAlignment)
    # One size for the whole row: the box ships 12.5 by default and stood out next to the labels.
    Check 'the spacing row keeps one font size' ([math]::Abs($spacingBox.FontSize - 12) -lt 0.01) ("size=" + $spacingBox.FontSize)
    # The row keeps one trailing note and no separate unit: a lone "px" between the box and the
    # range read as a second sentence fragment and always looked a size smaller than the note.
    $spacingRow = $spacingBox.Parent
    $spacingHint = $null
    foreach ($child in $spacingRow.Children) {
        if ([System.Windows.Controls.Grid]::GetColumn($child) -eq 2) { $spacingHint = $child; break }
    }
    Check 'the spacing row carries one trailing note' ($null -ne $spacingHint -and $spacingHint -is [System.Windows.Controls.TextBlock]) ''
    if ($null -ne $spacingHint) {
        Check 'no separate unit sits between the box and the note' (-not $spacingHint.Text.StartsWith('px')) "text=$($spacingHint.Text)"
        Check 'the note starts with the allowed range' ($spacingHint.Text.StartsWith('0')) "text=$($spacingHint.Text)"
        Check 'the note keeps the row font size' ([math]::Abs($spacingHint.FontSize - 12) -lt 0.01) ("size=" + $spacingHint.FontSize)
        $hintX = $spacingHint.TransformToAncestor($spacingCase.Page).Transform((New-Object System.Windows.Point(0, 0))).X
        $boxRight = $spacingBox.TransformToAncestor($spacingCase.Page).Transform((New-Object System.Windows.Point($spacingBox.ActualWidth, 0))).X
        Check 'the note follows the box without a gap band' (($hintX - $boxRight) -ge 4 -and ($hintX - $boxRight) -le 20) ("gap=" + [math]::Round($hintX - $boxRight))
    }
    # The field sits on the second tile column (64 px tile + 8 px gap), which is where the
    # dashed add tile lands once the second row holds one element.
    $firstTile = Find-ById $spacingCase.Page 'ElementTile'
    if ($null -ne $firstTile) {
        $tileX = $firstTile.TransformToAncestor($spacingCase.Page).Transform((New-Object System.Windows.Point(0, 0))).X
        $boxX = $spacingBox.TransformToAncestor($spacingCase.Page).Transform((New-Object System.Windows.Point(0, 0))).X
        Check 'the spacing box sits on the second tile column' ([math]::Abs(($boxX - $tileX) - 72) -le 2) ("box=" + [math]::Round($boxX) + " tile=" + [math]::Round($tileX))
    } else { Check 'a tile exists for the column check' $false '' }
}
$spacingCase.Window.Close()

"== click =="
$c = New-Case 3 -EditMode
$tiles = Tiles $c
$pageType.GetMethod('SelectElement', $flags).Invoke($c.Page, @(1)) | Out-Null
Layout $c
$before = Shot $c
Click-Route $tiles[1] ([System.Windows.UIElement]::MouseLeftButtonDownEvent)
Click-Route $tiles[1] ([System.Windows.UIElement]::MouseLeftButtonUpEvent)
Layout $c
$after = Shot $c
$origin = (RowTop $c).TransformToAncestor($c.Page).Transform([System.Windows.Point]::new(0, 0))
$diff = Diff-Pixels $before $after ([int]$origin.X) ([int]$origin.Y) 240 80
Check 'clicking the selected tile changes nothing on screen' ($diff -eq 0) "diff=$diff"
Check 'clicking does not rebuild the tiles' ([object]::ReferenceEquals($tiles[1], (Tiles $c)[1])) ''

"== press without moving =="
$c = New-Case 3 -EditMode
$tiles = Tiles $c
Click-Route $tiles[1] ([System.Windows.UIElement]::MouseLeftButtonDownEvent)
Layout $c
Check 'press does not create a placeholder' ($null -eq (Field $c '_dragPlaceholder')) ''
Check 'press does not move the tile into the drag layer' ((Field $c '_dragLayer').Children.Count -eq 0) ''
Click-Route $tiles[1] ([System.Windows.UIElement]::MouseLeftButtonUpEvent)
Layout $c
Check 'release clears the pending drag' (-not (Field $c '_pendingDrag')) ''

"== drag in row 1 =="
$c = New-Case 6 -EditMode
$tiles = Tiles $c
$rows = Field $c '_rows'
$theOrigin = $rows.TransformToAncestor($c.Page).Transform([System.Windows.Point]::new(0, 0))
$dragArgs = New-Object 'object[]' 2
$dragArgs[0] = [System.Windows.FrameworkElement]$tiles[0]
$dragArgs[1] = 0
$pageType.GetMethod('StartDrag', $flags).Invoke($c.Page, $dragArgs) | Out-Null
Layout $c
Pump 120
$follow = $pageType.GetMethod('FollowMouse', $flags)
foreach ($step in 1..12) {
    $pt = [System.Windows.Point]::new($theOrigin.X + 20 + $step * 6, $theOrigin.Y + 30)
    $follow.Invoke($c.Page, [object[]]@($pt)) | Out-Null
    Layout $c
    Pump 16
}
$reflow = Field $c '_reflowOf'
$maxRow2 = 0.0
foreach ($tile in (Tiles $c)) {
    if ($null -eq $tile) { continue }
    if (-not (RowBottom $c).Children.Contains($tile)) { continue }
    $rt = $reflow[$tile]
    if ($null -eq $rt) { continue }
    foreach ($v in @([Math]::Abs($rt.X), [Math]::Abs($rt.Y))) { if ($v -gt $maxRow2) { $maxRow2 = $v } }
}
Check 'row 2 never moves while dragging row 1' ($maxRow2 -le 0.51) "max=$([Math]::Round($maxRow2,1))"
Check 'the hole sits inside row 1' ((RowTop $c).Children.Contains((Field $c '_dragPlaceholder'))) ''
$pageType.GetMethod('AbortDrag', $flags).Invoke($c.Page, $null) | Out-Null
Layout $c

"== reorder =="
$c = New-Case 4 -EditMode
$pageType.GetMethod('MoveElement', $flags).Invoke($c.Page, [object[]]@(0, 2)) | Out-Null
Layout $c
Check 'MoveElement(0,2) gives e1,e2,e0,e3' ((Ids $c) -eq 'e1,e2,e0,e3') (Ids $c)

"== delete =="
$c = New-Case 6 -EditMode
$topSlots = [int]$pageType.GetField('TopRowSlots', $sflags).GetValue($null)
$removeByRef = $pageType.GetMethod('RemoveElement', $flags, $null, [Type[]]@($elementType), $null)
$removeByRef.Invoke($c.Page, [object[]]@([QuadVitals.Core.BandElement]$c.Draft.Items[3])) | Out-Null
Layout $c
$topCount = Field $c '_topCount'
Check 'deleting the 4th keeps the other five' ((Ids $c) -eq 'e0,e1,e2,e4,e5') (Ids $c)
Check 'row 1 shrinks by one and nothing is pulled up' ($topCount -eq ($topSlots - 1)) "topCount=$topCount topSlots=$topSlots"
Check 'row 1 keeps exactly one + tile' ((Count-AddTiles (RowTop $c)) -eq 1) "$(Count-AddTiles (RowTop $c))"
$row2Elements = 6 - $topSlots
Check 'row 2 keeps its elements plus one + tile' ((RowBottom $c).Children.Count -eq ($row2Elements + 1) -and (Count-AddTiles (RowBottom $c)) -eq 1) "row2=$((RowBottom $c).Children.Count) expected=$($row2Elements + 1)"
Check 'the band loses exactly one element' (($c.Draft.Items | Where-Object { $_.Enabled }).Count -eq ($topSlots - 1)) ''

"== row capacity =="
$maxElements = [int]$pageType.GetField('MaxElements', $sflags).GetValue($null)
$bottomSlots = [int]$pageType.GetField('BottomRowSlots', $sflags).GetValue($null)
Check 'row 1 takes five elements' ($topSlots -eq 5) "topSlots=$topSlots"
Check 'row 2 still takes ten' ($bottomSlots -eq 10) "bottomSlots=$bottomSlots"
Check 'total limit is 15' ($maxElements -eq 15) "maxElements=$maxElements"
$c = New-Case 15
Check 'all fifteen elements exist' ((Tiles $c).Count -eq 15) "$(Tiles $c).Count"
Check 'row 1 has no + tile when full' ((Count-AddTiles (RowTop $c)) -eq 0) "$(Count-AddTiles (RowTop $c))"
Check 'row 2 holds ten elements and no + tile' (((RowBottom $c).Children.Count) -eq 10 -and (Count-AddTiles (RowBottom $c)) -eq 0) "row2=$((RowBottom $c).Children.Count)"
Check 'exactly the first five are enabled' ((($c.Draft.Items | Where-Object { $_.Enabled }).Count) -eq 5) ''

"== delete protection =="
$c = New-Case 1 -EditMode
Check 'the only element (CPU) has no X' ($null -eq (Get-Badge (Tiles $c)[0])) ''
$c = New-Case 2 -EditMode
$tiles = Tiles $c
Check 'the first CPU element has no X' ($null -eq (Get-Badge $tiles[0])) ''
Check 'the second (text) element can be deleted' ($null -ne (Get-Badge $tiles[1])) ''

"== long press =="
$c = New-Case 2
Click-Route (Tiles $c)[0] ([System.Windows.UIElement]::MouseLeftButtonDownEvent)
Pump 500
Check 'half a second is not a long press' (-not (Field $c '_editMode')) ''
Pump 600
Check 'long press on a tile enters edit mode' ((Field $c '_editMode')) ''

"== editor order and metric-driven type =="
function Select-ComboKey($combo, [string]$key) {
    for ($i = 0; $i -lt $combo.Items.Count; $i++) {
        $item = $combo.Items[$i]
        if ($null -ne $item -and $item.Tag -eq $key) { $combo.SelectedIndex = $i; return $true }
    }
    return $false
}
# First ink column of a control's text, in page pixels. Used for the left-edge alignment rule:
# the numeric fields have to start on the same vertical line as the combo text above them.
function Ink-Left($ctx, $control) {
    $bmp = Shot $ctx
    $stride = 1280 * 4
    $buf = New-Object byte[] ($stride * 853)
    $bmp.CopyPixels($buf, $stride, 0)
    $origin = $control.TransformToAncestor($ctx.Page).Transform([System.Windows.Point]::new(0, 0))
    $x0 = [int]$origin.X
    $y0 = [int]$origin.Y
    $x1 = [int]($origin.X + $control.ActualWidth)
    for ($x = $x0; $x -lt $x1; $x++) {
        for ($y = $y0; $y -lt ($y0 + 29); $y++) {
            $i = $y * $stride + $x * 4
            $lum = 0.299 * $buf[$i + 2] + 0.587 * $buf[$i + 1] + 0.114 * $buf[$i]
            if ($lum -gt 110) { return $x }
        }
    }
    return -1
}
function Find-NumberBox($root) {
    foreach ($box in (Find-ByTypeName $root 'DarkTextBox')) {
        if ($box.Text -match '^\s*\d+\s*$' -and $box.ActualWidth -gt 0) { return $box }
    }
    return $null
}
$c = New-Case 2 -EditMode
$pageType.GetMethod('SelectElement', $flags).Invoke($c.Page, @(0)) | Out-Null
Layout $c
$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
$typeCombo = Find-ById $c.Page 'ElementTypeCombo'
Check 'metric combo exists' ($null -ne $metricCombo) ''
Check 'type combo exists' ($null -ne $typeCombo) ''
# The spacer type is gone for good: three shapes only.
Check 'the type list offers exactly bar/pie/text' ($null -ne $typeCombo -and $typeCombo.Items.Count -eq 3) "count=$(if ($null -ne $typeCombo) { $typeCombo.Items.Count } else { -1 })"
if ($null -ne $metricCombo -and $null -ne $typeCombo) {
    $metricY = $metricCombo.TransformToAncestor($c.Page).Transform([System.Windows.Point]::new(0, 0)).Y
    $typeY = $typeCombo.TransformToAncestor($c.Page).Transform([System.Windows.Point]::new(0, 0)).Y
    Check 'metric row sits above the type row' ($metricY -lt $typeY) "metricY=$metricY typeY=$typeY"
}

# The numeric fields must start on the combo's text line, not one padding step to the right:
# the TextBox template applies Padding through both the Border and the content host, so the
# padding value alone says nothing about where the glyphs land.
$numberBox = Find-NumberBox (Field $c '_editor')
Check 'the bar editor has a numeric field' ($null -ne $numberBox) ''
if ($null -ne $metricCombo -and $null -ne $numberBox) {
    $comboInk = Ink-Left $c $metricCombo
    $numberInk = Ink-Left $c $numberBox
    $delta = [math]::Abs($comboInk - $numberInk)
    Check 'numeric field text starts on the combo text line' ($delta -le 1) "combo=$comboInk number=$numberInk delta=$delta"
}

$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
Check 'memory metric is available' (Select-ComboKey $metricCombo 'memory') ''
Layout $c
Check 'picking memory switches the type to pie' ($c.Draft.Items[0].Type -eq 'pie') "type=$($c.Draft.Items[0].Type)"
# The ring diameter is a product constant now: no numeric input may be left in the pie editor, and
# the new style row must be there. Assert those rules, not row counts (a count breaks every time
# a row is added or removed).
$editor = Field $c '_editor'
# Colour rows carry a hex box (#FFFFFFFF). The only plain-number box a pie editor may have is the
# refresh row; a re-added diameter input would make this 2.
$numeric = 0
foreach ($box in (Find-ByTypeName $editor 'DarkTextBox')) {
    if ($box.Text -match '^\s*\d+\s*$') { $numeric++ }
}
Check 'the pie editor has only the refresh number box' ($numeric -eq 1) "numeric=$numeric"
$styleCombo = Find-ById $c.Page 'ElementPieStyleCombo'
Check 'the pie editor has a style row' ($null -ne $styleCombo) ''

$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
Check 'disk I/O metric is available' (Select-ComboKey $metricCombo 'diskIoPair') ''
Layout $c
Check 'picking disk I/O switches to a text element' ($c.Draft.Items[0].Type -eq 'text') "type=$($c.Draft.Items[0].Type)"
$item = $c.Draft.Items[0]
$diskLine0 = if ($item.Lines -and $item.Lines.Count -gt 0) { $item.Lines[0].Metric } else { '' }
$diskLine1 = if ($item.Lines -and $item.Lines.Count -gt 1) { $item.Lines[1].Metric } else { '' }
Check 'disk I/O is one element with a read and a write line' ($diskLine0 -eq 'diskIoRead' -and $diskLine1 -eq 'diskIoWrite') "line0=$diskLine0 line1=$diskLine1"
$comboAfter = Find-ById $c.Page 'ElementMetricCombo'
$selectedTag = if ($null -ne $comboAfter -and $null -ne $comboAfter.SelectedItem) { $comboAfter.SelectedItem.Tag } else { '' }
Check 'the disk I/O dropdown stays on the pair item' ($selectedTag -eq 'diskIoPair') "tag=$selectedTag"
Check 'both disk I/O lines carry their own colour' ($item.Lines[0].Color -ne $item.Lines[1].Color) "line0=$($item.Lines[0].Color) line1=$($item.Lines[1].Color)"

$driveCombo = Find-ById $c.Page 'ElementDiskDriveCombo'
Check 'the disk I/O editor has a drive selector' ($null -ne $driveCombo) ''
if ($null -ne $driveCombo) {
    $driveKeys = @($driveCombo.Items | ForEach-Object { $_.Tag })
    $localKeys = @($driveKeys | Where-Object { $_ -ne '' })
    Check 'the drive selector offers all disks plus a local drive' (($driveKeys -contains '') -and $localKeys.Count -ge 1) ("keys=" + ($driveKeys -join ','))
    $someDrive = $localKeys | Select-Object -First 1
    [void](Select-ComboKey $driveCombo $someDrive)
    Layout $c
    Check 'picking a drive stores it on the element' ($c.Draft.Items[0].Drive -eq $someDrive) "drive=$($c.Draft.Items[0].Drive) expected=$someDrive"
    # A volume whose label is the same as the kind must not read "E: local disk * local disk".
    $diskWord = -join @([char]0x672C, [char]0x5730, [char]0x78C1, [char]0x76D8)
    $duplicateLabels = 0
    foreach ($it in $driveCombo.Items) {
        $text = [string]$it.Content
        $first = $text.IndexOf($diskWord)
        if ($first -ge 0 -and $text.IndexOf($diskWord, $first + 1) -ge 0) { $duplicateLabels++ }
    }
    Check 'drive labels do not repeat the volume kind' ($duplicateLabels -eq 0) "duplicates=$duplicateLabels"
}

$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
Check 'network metric is available' (Select-ComboKey $metricCombo 'network') ''
Layout $c
$item = $c.Draft.Items[0]
$line0 = if ($item.Lines -and $item.Lines.Count -gt 0) { $item.Lines[0].Metric } else { '' }
$line1 = if ($item.Lines -and $item.Lines.Count -gt 1) { $item.Lines[1].Metric } else { '' }
Check 'picking the network metric switches the type to text' ($item.Type -eq 'text') "type=$($item.Type)"
Check 'the network metric is one element with an up and a down line' ($line0 -eq 'netUp' -and $line1 -eq 'netDown') "line0=$line0 line1=$line1"

$typeNames = $pageType.GetField('TypeNames', $sflags).GetValue($null)
$barLabel = $typeNames['bar']
Check 'the bar type label is the short one (no history suffix)' ($barLabel.Length -eq 2) "length=$($barLabel.Length)"

$dot = [char]0x00B7
$memoryWord = U @(0x5185, 0x5B58)
$speedWord = U @(0x7F51, 0x901F)
$barWord = U @(0x67F1, 0x72B6)
$pieWord = U @(0x73AF, 0x5F62)
$textWord = U @(0x6587, 0x672C)
function Get-CaptionText($tile) {
    foreach ($child in $tile.Child.Children) {
        if ($child -is [System.Windows.Controls.TextBlock]) { return $child.Text }
    }
    return ''
}

$c = New-Case 2
$pageType.GetMethod('SelectElement', $flags).Invoke($c.Page, @(0)) | Out-Null
Layout $c
$top = RowTop $c
$caption = Get-CaptionText $top.Children[0]
Check 'the cpu tile caption names the metric' ($caption -eq ("1$dot" + 'CPU')) "caption=$caption"

$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
[void](Select-ComboKey $metricCombo 'memory')
Layout $c
$caption = Get-CaptionText (RowTop $c).Children[0]
Check 'the caption follows the metric when it switches' ($caption -eq ("1$dot" + $memoryWord)) "caption=$caption"

$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
[void](Select-ComboKey $metricCombo 'network')
Layout $c
$caption = Get-CaptionText (RowTop $c).Children[0]
Check 'two network lines collapse to one short word' ($caption -eq ("1$dot" + $speedWord)) "caption=$caption"

$shapeFree = $true
$shapeDetail = ''
foreach ($tile in (RowTop $c).Children) {
    if ((AutoId $tile) -ne 'ElementTile') { continue }
    $text = Get-CaptionText $tile
    if ($text.Contains($barWord) -or $text.Contains($pieWord) -or $text.Contains($textWord)) {
        $shapeFree = $false
        $shapeDetail += "$text "
    }
}
Check 'no caption falls back to a shape word' $shapeFree "captions=$shapeDetail"

# --- the settings card must not paint WPF's dotted focus ring --------------------
$scrollers = New-Object 'System.Collections.Generic.List[object]'
$scan = New-Object 'System.Collections.Queue'
$scan.Enqueue($c.Page)
while ($scan.Count -gt 0) {
    $node = $scan.Dequeue()
    if ($node -is [System.Windows.Controls.ScrollViewer]) { $scrollers.Add($node) }
    $childCount = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($node)
    for ($i = 0; $i -lt $childCount; $i++) { $scan.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($node, $i)) }
}
$ringFree = $false
$editorPanel = Field $c '_editor'
foreach ($s in $scrollers) {
    if ([object]::ReferenceEquals($s.Content, $editorPanel)) {
        $ringFree = ($null -eq $s.FocusVisualStyle)
    }
}
Check 'the settings card draws no focus ring' $ringFree "scrollviewers=$($scrollers.Count)"


# --- temperature: one dropdown item, two pickable rows ------------------------------------------
# The two sensors used to be separate metric items, which forced two elements to show CPU and GPU
# temperatures together. Now one "temperature" item owns two rows and each row picks a sensor.
$tempWord = U @(0x6E29, 0x5EA6)

$c = New-Case 2
$pageType.GetMethod('SelectElement', $flags).Invoke($c.Page, @(0)) | Out-Null
Layout $c
$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
$metricKeys = @($metricCombo.Items | ForEach-Object { $_.Tag })
Check 'the metric list offers one temperature item' ($metricKeys -contains 'temperaturePair') ("keys=" + ($metricKeys -join ','))
Check 'cpu/gpu temperature are not separate metric items' ((-not ($metricKeys -contains 'temperature')) -and (-not ($metricKeys -contains 'gpuTemp'))) ("keys=" + ($metricKeys -join ','))
Check 'disk I/O is one pair item, not three single metrics' (($metricKeys -contains 'diskIoPair') -and (-not ($metricKeys -contains 'diskIo')) -and (-not ($metricKeys -contains 'diskIoRead')) -and (-not ($metricKeys -contains 'diskIoWrite'))) ("keys=" + ($metricKeys -join ','))

[void](Select-ComboKey $metricCombo 'temperaturePair')
Layout $c
$item = $c.Draft.Items[0]
$line0 = if ($item.Lines -and $item.Lines.Count -gt 0) { $item.Lines[0].Metric } else { '' }
$line1 = if ($item.Lines -and $item.Lines.Count -gt 1) { $item.Lines[1].Metric } else { '' }
Check 'temperature is a two-line text element' (($item.Type -eq 'text') -and ($item.Lines.Count -eq 2)) "type=$($item.Type) lines=$($item.Lines.Count)"
Check 'temperature row 1 is CPU and row 2 is GPU' (($line0 -eq 'temperature') -and ($line1 -eq 'gpuTemp')) "line0=$line0 line1=$line1"

$row2 = Find-ById $c.Page 'ElementTempLine1'
$row2Keys = @($row2.Items | ForEach-Object { $_.Tag })
Check 'row 2 offers both temperature sensors' (($row2Keys -contains 'temperature') -and ($row2Keys -contains 'gpuTemp')) ("keys=" + ($row2Keys -join ','))
[void](Select-ComboKey $row2 'temperature')
Layout $c
Check 'picking CPU for row 2 rewrites only that row' (($c.Draft.Items[0].Lines[1].Metric -eq 'temperature') -and ($c.Draft.Items[0].Lines[0].Metric -eq 'temperature')) ''

$caption = Get-CaptionText (RowTop $c).Children[0]
Check 'the temperature tile caption is the merged word' ($caption -eq ("1$dot" + $tempWord)) "caption=$caption"


# --- temperature rows colour themselves, so they must not offer a colour picker ------------------
function Find-AllById($root, [string]$id) {
    $found = New-Object System.Collections.Generic.List[object]
    $scan = New-Object 'System.Collections.Queue'
    $scan.Enqueue($root)
    while ($scan.Count -gt 0) {
        $node = $scan.Dequeue()
        if ($node -is [System.Windows.DependencyObject] -and (AutoId $node) -eq $id) { $found.Add($node) }
        $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($node)
        for ($k = 0; $k -lt $n; $k++) { $scan.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($node, $k)) }
    }
    return $found
}

$swatches = Find-AllById $c.Page 'ColorSwatch'
Check 'temperature rows have no colour picker' ($swatches.Count -eq 0) "swatches=$($swatches.Count)"
$zone0 = Find-ById $c.Page 'ElementTempZone0'
Check 'temperature rows explain nothing they cannot change' ($null -eq $zone0) ('zone0=' + [bool]($null -ne $zone0))


# --- general settings: a title-bar gear panel, not a sidebar page -------------------------------
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
    Check 'the panel matches the other gear dialogs' (([math]::Abs($generalPanel.ActualWidth - 640) -le 0.5) -and ([math]::Abs($generalPanel.ActualHeight - 427) -le 0.5)) ("size=" + [math]::Round($generalPanel.ActualWidth) + "x" + [math]::Round($generalPanel.ActualHeight))
}
$autoStart = Find-ById $generalRoot 'GeneralAutostart'
Check 'the panel carries the autostart switch' ($null -ne $autoStart) ''
if ($null -ne $autoStart) {
    $saved = [bool]$generalWindow.CurrentSettings.StartAgentWithWindows
    Check 'the switch reflects the saved preference' ($autoStart.IsChecked -eq $saved) ("checked=" + $autoStart.IsChecked + " saved=" + $saved)
}
Check 'the panel no longer carries the spacing box' ($null -eq (Find-ById $generalRoot 'GeneralSpacingBox')) ''
$generalClose = Find-ById $generalRoot 'GeneralClose'
Check 'the panel has a close button' ($null -ne $generalClose) ''
if ($null -ne $generalClose) {
    $generalClose.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Button]::ClickEvent)))
    Pump 120
    Check 'closing the panel removes it' (-not [bool]$windowType.GetProperty('IsOverlayOpen').GetValue($generalWindow)) ''
}
$generalWindow.Close()

"== disk I/O editor: rows =="
$c = New-Case 1
$pageType.GetMethod('SelectElement', $flags).Invoke($c.Page, @(0)) | Out-Null
Layout $c
$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
Check 'disk I/O metric is available for the row check' (Select-ComboKey $metricCombo 'diskIoPair') ''
Layout $c
$editor = Field $c '_editor'
$numericBoxes = 0
foreach ($box in (Find-ByTypeName $editor 'DarkTextBox')) {
    if ($box.Text -match '^\s*\d+\s*$') { $numericBoxes++ }
}
Check 'the disk I/O editor has only the refresh number box' ($numericBoxes -eq 1) "numeric=$numericBoxes"
$editorCombos = @(Find-ByTypeName $editor 'DarkComboBox')
Check 'the disk I/O editor has metric, type and drive combos' ($editorCombos.Count -eq 3) "combos=$($editorCombos.Count)"

"== about page: developer card =="
$aboutWindow = [Activator]::CreateInstance($windowType, @('about'))
$aboutWindow.Width = 1280
$aboutWindow.Height = 853
# Off-screen but shown: WPF hit testing skips elements whose IsVisible is false, and a window that
# was never shown makes every child invisible. ShowActivated=false keeps the suite from stealing
# focus, and -5000 puts it outside the desktop so nothing flashes on screen.
$aboutWindow.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
$aboutWindow.ShowActivated = $false
$aboutWindow.ShowInTaskbar = $false
$aboutWindow.Left = -5000
$aboutWindow.Top = -5000
$aboutWindow.Show()
Pump 150
$aboutRoot = $aboutWindow.Content
$aboutRoot.UpdateLayout()
$fileOopsRow = $null
foreach ($block in (Find-ByTypeName $aboutRoot 'TextBlock')) {
    if ($block.Text -eq 'FileOops') { $fileOopsRow = $block }
}
Check 'the developer card has a FileOops link row' ($null -ne $fileOopsRow) ''
# Renamed by feedback: the card holds versions, paths, memory channel and licence, so the title
# must not claim it is only about versions and files.
$logsTitleWord = U @(0x65E5, 0x5FD7, 0x548C, 0x6587, 0x4EF6)
$logsTitle = $null
foreach ($block in (Find-ByTypeName $aboutRoot 'TextBlock')) {
    if ($block.Text -eq $logsTitleWord) { $logsTitle = $block }
}
Check 'the about page titles the file card logs and files' ($null -ne $logsTitle) ''

# The About row must read the single version source, not the assembly default (0.0.0.0) that
# appeared when no AssemblyVersion attribute was compiled in.
$semVer = $coreAsm.GetType('QuadVitals.Core.ProductVersion').GetField('SemVer').GetValue($null)
$versionShown = $false
foreach ($block in (Find-ByTypeName $aboutRoot 'TextBlock')) {
    if ($block.Text -eq $semVer) { $versionShown = $true }
}
Check 'the about page shows the product version' $versionShown "version=$semVer"

# The link is the value block on the right, not the whole row. A container with a null Background
# is not hit-testable in WPF, so without the transparent brush only the glyphs reacted: the gap
# before the arrow, or the space beside a short value, ignored the click and never showed the hand.
function Has-HandAncestor($node) {
    while ($null -ne $node) {
        if ($node -is [System.Windows.FrameworkElement] -and $node.Cursor -eq [System.Windows.Input.Cursors]::Hand) { return $true }
        if ($node -is [System.Windows.Media.Visual]) { $node = [System.Windows.Media.VisualTreeHelper]::GetParent($node) }
        else { return $false }
    }
    return $false
}
$linkBlocks = @()
foreach ($element in (Find-ByTypeName $aboutRoot 'Border')) {
    if ($element.Cursor -eq [System.Windows.Input.Cursors]::Hand) { $linkBlocks += $element }
}
Check 'the about page has four clickable link blocks' ($linkBlocks.Count -eq 4) "blocks=$($linkBlocks.Count)"
$paintedBlocks = 0
foreach ($block in $linkBlocks) {
    if ($null -ne $block.Background) { $paintedBlocks++ }
}
Check 'every link block paints a hit-testable background' ($paintedBlocks -eq $linkBlocks.Count) "painted=$paintedBlocks"

# Probe two pixels inside the block padding and once over the grey label: the first has to be a
# link, the second must not be one (the row itself stays inert).
$padHits = 0
$labelHits = 0
foreach ($block in $linkBlocks) {
    $row = [System.Windows.Media.VisualTreeHelper]::GetParent($block)
    $rowOrigin = $row.TransformToAncestor($aboutRoot).Transform((New-Object System.Windows.Point(0, 0)))
    $blockOrigin = $block.TransformToAncestor($aboutRoot).Transform((New-Object System.Windows.Point(0, 0)))
    $padProbe = New-Object System.Windows.Point(($blockOrigin.X + 2), ($blockOrigin.Y + $block.ActualHeight / 2))
    if (Has-HandAncestor ($aboutRoot.InputHitTest($padProbe))) { $padHits++ }
    # 20 px in is label territory for both row styles: left-aligned values start at 96 px,
    # right-aligned ones sit at the far edge.
    $labelProbe = New-Object System.Windows.Point(($rowOrigin.X + 20), ($rowOrigin.Y + $row.ActualHeight / 2))
    if (Has-HandAncestor ($aboutRoot.InputHitTest($labelProbe))) { $labelHits++ }
}
Check 'a click in the value block padding lands on the link' ($padHits -eq $linkBlocks.Count) "pads=$padHits"
Check 'a click over the grey label is not a link' ($labelHits -eq 0) "labels=$labelHits"
$updateWord = U @(0x66F4, 0x65B0, 0x548C, 0x6821, 0x9A8C)
$updateTitle = $null
foreach ($block in (Find-ByTypeName $aboutRoot 'TextBlock')) {
    if ($block.Text -eq $updateWord) { $updateTitle = $block }
}
Check 'the about page has an update card' ($null -ne $updateTitle) ''
$updateCheck = Find-ById $aboutRoot 'AboutUpdateCheck'
$updateCancel = Find-ById $aboutRoot 'AboutUpdateCancel'
# The changelog row at the end of the update card: label on the left, clickable value on the
# right, the same row shape the developer card uses.
# The card is built lazily inside a scroll host: force one more layout pass before looking for the
# row, otherwise the first traversal runs before the card's content is realized.
$aboutRoot.UpdateLayout()
$changelogRow = Wait-ById $aboutRoot 'AboutUpdateChangelog'
Check 'the update card carries a changelog row' ($null -ne $changelogRow) ''
if ($null -ne $changelogRow) {
    $changelogLabel = $null
    $changelogTarget = $null
    foreach ($child in $changelogRow.Children) {
        if ($child -is [System.Windows.Controls.TextBlock] -and $child.Text -eq (U @(0x65E5, 0x5FD7, 0x660E, 0x7EC6))) { $changelogLabel = $child }
        if ($child -is [System.Windows.Controls.Border]) { $changelogTarget = $child }
    }
    Check 'the changelog row is labelled for the changelog' ($null -ne $changelogLabel) ''
    Check 'the changelog value block is the clickable part' (($null -ne $changelogTarget) -and ($changelogTarget.Cursor -eq [System.Windows.Input.Cursors]::Hand)) ''
    Check 'the changelog label stays inert' (($null -eq $changelogLabel) -or ($changelogLabel.Cursor -ne [System.Windows.Input.Cursors]::Hand)) ''
    $changelogUrl = $asm.GetType('QuadVitals.Settings.AboutPage').GetField('ChangelogUrl', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
    Check 'the changelog link points at the repository changelog' ($changelogUrl -eq 'https://github.com/enosawauno/QuadVitals/blob/main/CHANGELOG.md') "url=$changelogUrl"
}

Check 'the update action is usable without a preview flag' (($null -ne $updateCheck) -and $updateCheck.IsEnabled) ''
Check 'the cancel action stays hidden until a transfer starts' (($null -ne $updateCancel) -and ($updateCancel.Visibility -eq [System.Windows.Visibility]::Collapsed)) ''
$progressTrack = Find-ById $aboutRoot 'AboutUpdateProgress'
Check 'the progress bar starts hidden' (($null -ne $progressTrack) -and ($progressTrack.Visibility -eq [System.Windows.Visibility]::Collapsed)) ''
$aboutPageType = $asm.GetType('QuadVitals.Settings.AboutPage')
$aboutPage = $null
$aboutQueue = New-Object 'System.Collections.Queue'
$aboutQueue.Enqueue($aboutRoot)
while ($aboutQueue.Count -gt 0 -and $null -eq $aboutPage) {
    $e = $aboutQueue.Dequeue()
    if ($e.GetType() -eq $aboutPageType) { $aboutPage = $e; break }
    if ($e -is [System.Windows.Media.Visual]) {
        $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
        for ($i = 0; $i -lt $n; $i++) { $aboutQueue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
    }
}
Check 'the update card is hosted by the about page' ($null -ne $aboutPage) ''
if ($null -ne $aboutPage) {
    # The status no longer has its own line: it is printed on the action button and drops back to
    # the idle label five seconds later.
    Check 'the status line is gone from the card' ($null -eq (Find-ById $aboutRoot 'AboutUpdateStatus')) ''
    # The idle card must not carry placeholder rows: 最新版本 / 安装包校验 and the empty status line
    # were pure noise. They must not come back, and the card keeps exactly one labelled row.
    Check 'the placeholder rows are gone' (($null -eq $aboutPageType.GetField('_packageRow', $flags)) -and ($null -eq $aboutPageType.GetField('_latestVersionRow', $flags))) ''
    $updateCard = $null
    $cardQueue = New-Object 'System.Collections.Queue'
    $cardQueue.Enqueue($aboutRoot)
    while ($cardQueue.Count -gt 0 -and $null -eq $updateCard) {
        $e = $cardQueue.Dequeue()
        if (($e.GetType().Name -eq 'Card') -and ($e.Title -eq $updateWord)) { $updateCard = $e; break }
        if ($e -is [System.Windows.Media.Visual]) {
            $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
            for ($i = 0; $i -lt $n; $i++) { $cardQueue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
        }
    }
    Check 'the update card is found for row counting' ($null -ne $updateCard) ''
    if ($null -ne $updateCard) {
        # Counted through the tree: the version row now shares its line with the action button.
        $rowCount = 0
        $rowQueue = New-Object 'System.Collections.Queue'
        $rowQueue.Enqueue($updateCard)
        while ($rowQueue.Count -gt 0) {
            $node = $rowQueue.Dequeue()
            if ($node.GetType().Name -eq 'KeyValueRow') { $rowCount++ }
            if ($node -is [System.Windows.Media.Visual] -or $node -is [System.Windows.Controls.ContentControl]) {
                $kids = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($node)
                for ($i = 0; $i -lt $kids; $i++) { $rowQueue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($node, $i)) }
            }
        }
        Check 'the update card shows only the current version row' ($rowCount -eq 1) "rows=$rowCount"
        # The card follows the page's pattern: data rows first, then the action at the bottom-left,
        # on the same left edge as the labels.
        $versionRow = Find-ById $updateCard 'AboutUpdateCurrent'
        $actionButton = Find-ById $updateCard 'AboutUpdateCheck'
        if (($null -ne $versionRow) -and ($null -ne $actionButton)) {
            $rowOrigin = $versionRow.TransformToAncestor($updateCard).Transform((New-Object System.Windows.Point(0, 0)))
            $buttonOrigin = $actionButton.TransformToAncestor($updateCard).Transform((New-Object System.Windows.Point(0, 0)))
            Check 'the update action sits at the card bottom-left' ([math]::Abs($buttonOrigin.X - $rowOrigin.X) -le 2) ("button=" + [math]::Round($buttonOrigin.X) + " row=" + [math]::Round($rowOrigin.X))
            Check 'the update action sits below the version row' ($buttonOrigin.Y -gt ($rowOrigin.Y + 20)) ("button=" + [math]::Round($buttonOrigin.Y) + " row=" + [math]::Round($rowOrigin.Y))
            $cardWidth = $updateCard.ActualWidth
            Check 'the update action spans the card' ($actionButton.ActualWidth -gt ($cardWidth * 0.8)) ("button=" + [math]::Round($actionButton.ActualWidth) + " card=" + [math]::Round($cardWidth))
            # A full-width button must not scale on hover: 5% of 940 px pushes it over the card edge.
            # The spring attaches a ScaleTransform that grows the control; identity means the
            # full-width button cannot creep over the card edge on hover.
            Check 'the full-width action does not scale on hover' (-not ($actionButton.RenderTransform -is [System.Windows.Media.ScaleTransform])) ("transform=" + $actionButton.RenderTransform)
        } else { Check 'the update action is inside the card' $false '' }
    }
    $ink = [System.Windows.Media.Color]::FromRgb(0x8F, 0x8F, 0x8F)
    $checkBtn = Find-ById $updateCard 'AboutUpdateCheck'
    $idleLabel = [string]$checkBtn.Content
    $aboutPageType.GetMethod('SetStatus', $flags).Invoke($aboutPage, @('checking', $ink)) | Out-Null
    Check 'a real message is printed on the action button' (([string]$checkBtn.Content) -eq 'checking') ("content=" + $checkBtn.Content)
    Check 'a status reset timer is armed' ($null -ne $aboutPageType.GetField('_statusReset', $flags).GetValue($aboutPage)) ''
    $fillField = $aboutPageType.GetField('_progressFill', $flags)
    $trackField = $aboutPageType.GetField('_progressTrack', $flags)
    $fill = $fillField.GetValue($aboutPage)
    $track = $trackField.GetValue($aboutPage)
    $aboutPageType.GetMethod('SetProgressVisible', $flags).Invoke($aboutPage, @($true)) | Out-Null
    $aboutPageType.GetMethod('SetProgress', $flags).Invoke($aboutPage, @(0.65)) | Out-Null
    $scale = ($fill.RenderTransform).ScaleX
    Check 'progress maps onto the track width' ([math]::Abs($scale - 0.65) -le 0.001) "scale=$scale"
    Check 'the progress bar is visible during a transfer' ($track.Visibility -eq [System.Windows.Visibility]::Visible) ''
    $aboutPageType.GetMethod('SetProgress', $flags).Invoke($aboutPage, @(1.7)) | Out-Null
    $scale = ($fill.RenderTransform).ScaleX
    Check 'progress beyond the end is clamped' ([math]::Abs($scale - 1.0) -le 0.001) "scale=$scale"

    # Cleared status returns the button to its own label.
    $aboutPageType.GetMethod('SetStatus', $flags).Invoke($aboutPage, @('', $ink)) | Out-Null
    Check 'clearing the status restores the button label' (([string]$checkBtn.Content) -eq $idleLabel) ("content=" + $checkBtn.Content + " idle=" + $idleLabel)

    # Title-bar buttons were shrunk on request; the invariant that must survive is the ✕ right
    # edge sitting on the card right edge (it moved once when the glyph geometry changed).
    $closeButton = Find-ById $aboutRoot 'CaptionClose'
    $minButton = Find-ById $aboutRoot 'CaptionMinimize'
    $captionOk = ($null -ne $closeButton) -and ($null -ne $minButton) -and
        ($closeButton.ActualWidth -le 37) -and ($closeButton.ActualHeight -le 34) -and
        ($minButton.ActualWidth -le 37) -and ($minButton.ActualHeight -le 34)
    Check 'the caption buttons are the smaller size' $captionOk "close=$($closeButton.ActualWidth)x$($closeButton.ActualHeight)"
    if (($null -ne $closeButton) -and ($null -ne $updateCard)) {
        $glyphRight = 0
        foreach ($child in $closeButton.Content.Children) {
            if ($child -is [System.Windows.Shapes.Line]) {
                $points = @(
                    (New-Object System.Windows.Point($child.X1, $child.Y1)),
                    (New-Object System.Windows.Point($child.X2, $child.Y2)))
                foreach ($pt in $points) {
                    $gx = $child.TransformToAncestor($aboutRoot).Transform($pt).X
                    if ($gx -gt $glyphRight) { $glyphRight = $gx }
                }
            }
        }
        $cardRight = $updateCard.TransformToAncestor($aboutRoot).Transform(
            (New-Object System.Windows.Point($updateCard.ActualWidth, 0))).X
        Check 'the close glyph still sits on the card right edge' ([math]::Abs($glyphRight - $cardRight) -le 2.5) "glyph=$([math]::Round($glyphRight,1)) card=$([math]::Round($cardRight,1))"
    }
}

"== sparkline: smoothing and no overshoot =="
$sparkType = $uiAsm.GetType('QuadVitals.UI.Sparkline')
Check 'the sparkline control exists' ($null -ne $sparkType) ''
if ($null -ne $sparkType) {
    $smoothMethod = $sparkType.GetMethod('SmoothSeries', $sflags)
    Check 'the smoothing helper exists' ($null -ne $smoothMethod) ''
    if ($null -ne $smoothMethod) {
        # A single-sample spike must be rounded off but not erased: the chart has to stay honest
        # about real bursts while stopping 1 Hz jitter from looking like a saw blade.
        $spiked = $smoothMethod.Invoke($null, @(,[double[]]@(0, 0, 10, 0, 0)))
        Check 'a one-sample spike is rounded but kept' (($spiked[2] -lt 6) -and ($spiked[2] -gt 1.5)) "peak=$([math]::Round($spiked[2],2))"
        $flat = $smoothMethod.Invoke($null, @(,[double[]]@(7, 7, 7, 7, 7)))
        Check 'a flat series stays flat' (($flat | Measure-Object -Maximum).Maximum -eq 7) ''
    }

    # The cubic control points are clamped to their segment, otherwise the curve bulges past the
    # samples at every turn and the eye reads that as data noise.
    $buildGeometry = $sparkType.GetMethod('BuildGeometry', $sflags)
    Check 'the geometry builder exists' ($null -ne $buildGeometry) ''
    if ($null -ne $buildGeometry) {
        $points = New-Object 'System.Collections.Generic.List[System.Windows.Point]'
        foreach ($pair in @(@(0, 50), @(10, 50), @(20, 10), @(30, 10), @(40, 50))) {
            $points.Add((New-Object System.Windows.Point($pair[0], $pair[1])))
        }
        $pointList = $points.PSObject.BaseObject   # PowerShell wraps generic lists; reflection needs the raw object
        $geometry = $buildGeometry.Invoke($null, @($pointList, [double]::NaN, $true))
        # StreamGeometry does not flatten reliably outside a render pass; convert it to a PathGeometry
        # instead, then read the control points (they are what used to bulge past the samples).
        $flattened = [System.Windows.Media.PathGeometry]::CreateFromGeometry($geometry)
        $minY = 10000.0
        $maxY = -10000.0
        foreach ($figure in $flattened.Figures) {
            if ($figure.StartPoint.Y -lt $minY) { $minY = $figure.StartPoint.Y }
            if ($figure.StartPoint.Y -gt $maxY) { $maxY = $figure.StartPoint.Y }
            foreach ($segment in $figure.Segments) {
                if ($null -eq $segment.PSObject.Properties['Points']) { continue }
                foreach ($pt in $segment.Points) {
                    if ($pt.Y -lt $minY) { $minY = $pt.Y }
                    if ($pt.Y -gt $maxY) { $maxY = $pt.Y }
                }
            }
        }
        Check 'the smoothed curve stays inside the sample band' (($minY -ge 9.5) -and ($maxY -le 50.5)) "min=$([math]::Round($minY,1)) max=$([math]::Round($maxY,1))"
    }

    # Scale floor: an idle window must not be blown up to full height (that is what turned 1 KB/s
    # jitter into a saw blade), while a real burst still has to reach the top of the chart.
    $buildPoints = $sparkType.GetMethod('BuildPoints', $flags)
    Check 'the point builder exists' ($null -ne $buildPoints) ''
    if ($null -ne $buildPoints) {
        $sample = New-Object QuadVitals.UI.Sparkline ([System.Windows.Media.Color]::FromRgb(0x41, 0xD4, 0x84)), 330, 58
        $sample.SlotCount = 60
        $idle = New-Object 'double[]' 60
        for ($i = 0; $i -lt 60; $i++) { $idle[$i] = 1300 + (($i % 5) * 120) }
        $idle[30] = 3174
        $idleArgs = New-Object 'object[]' 3
        $idleArgs[0] = [double[]]$idle
        $idleArgs[1] = [double]330
        $idleArgs[2] = [double]58
        $idlePoints = $buildPoints.Invoke($sample, $idleArgs)
        $idleTop = 1000.0
        foreach ($pt in $idlePoints) { if ($pt.Y -lt $idleTop) { $idleTop = $pt.Y } }
        Check 'an idle window is not stretched to full height' ($idleTop -ge 18) "topY=$([math]::Round($idleTop,1))"

        $burst = New-Object 'double[]' 60
        for ($i = 0; $i -lt 60; $i++) { $burst[$i] = 1300 }
        $burst[30] = 613000
        $burstArgs = New-Object 'object[]' 3
        $burstArgs[0] = [double[]]$burst
        $burstArgs[1] = [double]330
        $burstArgs[2] = [double]58
        $burstPoints = $buildPoints.Invoke($sample, $burstArgs)
        $burstTop = 1000.0
        foreach ($pt in $burstPoints) { if ($pt.Y -lt $burstTop) { $burstTop = $pt.Y } }
        Check 'a real burst still reaches the top' ($burstTop -le 12) "topY=$([math]::Round($burstTop,1))"

        # One outlier spike must not flatten the other 59 samples into a horizontal line: the
        # axis follows P95 when max is more than 4× P95, and the spike clips at the top.
        $spiky = New-Object 'double[]' 60
        for ($i = 0; $i -lt 60; $i++) { $spiky[$i] = 50000 + (($i % 5) * 5000) }
        foreach ($spikeIndex in 20, 30, 40, 50) { $spiky[$spikeIndex] = 20000000 }
        $spikyArgs = New-Object 'object[]' 3
        $spikyArgs[0] = [double[]]$spiky
        $spikyArgs[1] = [double]330
        $spikyArgs[2] = [double]58
        $spikyPoints = $buildPoints.Invoke($sample, $spikyArgs)
        $spikyLowTop = 1000.0
        for ($i = 0; $i -lt $spikyPoints.Count; $i++) {
            if (@(20, 30, 40, 50) -contains $i) { continue }
            if ($spikyPoints[$i].Y -lt $spikyLowTop) { $spikyLowTop = $spikyPoints[$i].Y }
        }
        Check 'a few spikes do not flatten the rest of the curve' ($spikyLowTop -lt 30) "topY=$([math]::Round($spikyLowTop,1))"

        # A restored session can have fewer than 60 points: it must continue at the right edge
        # (the live samples append after it) instead of stretching over the whole chart.
        $short = New-Object 'double[]' 8
        for ($i = 0; $i -lt 8; $i++) { $short[$i] = 2048 * ($i + 1) }
        $shortArgs = New-Object 'object[]' 3
        $shortArgs[0] = [double[]]$short
        $shortArgs[1] = [double]330
        $shortArgs[2] = [double]58
        $shortPoints = $buildPoints.Invoke($sample, $shortArgs)
        Check 'a short restored curve stays on the right edge' ($shortPoints[0].X -gt 280) "firstX=$([math]::Round($shortPoints[0].X,1))"
        Check 'the newest restored point ends at the right edge' ([math]::Abs($shortPoints[7].X - 324) -le 1) "lastX=$([math]::Round($shortPoints[7].X,1))"
    }

    # A sharp V must come out as a rounded turn: at an extremum the monotone-cubic tangent is
    # zero, so the curve stays flat for a few pixels around the vertex instead of meeting at an
    # angle. A polyline (or a clamped Catmull-Rom) drops ~12 px over that span.
    if ($null -ne $buildGeometry) {
        $vee = New-Object 'System.Collections.Generic.List[System.Windows.Point]'
        foreach ($pair in @(@(0, 50), @(10, 10), @(20, 50))) {
            $vee.Add((New-Object System.Windows.Point($pair[0], $pair[1])))
        }
        $veeArgs = New-Object 'object[]' 3
        $veeArgs[0] = $vee.PSObject.BaseObject
        $veeArgs[1] = [double]::NaN
        $veeArgs[2] = $true
        $veeGeometry = $buildGeometry.Invoke($null, $veeArgs)
        $veePath = [System.Windows.Media.PathGeometry]::CreateFromGeometry($veeGeometry)
        $veeFlat = $veePath.GetFlattenedPathGeometry(0.05, [System.Windows.Media.ToleranceType]::Absolute)
        $worst = 0.0
        foreach ($figure in $veeFlat.Figures) {
            $samples = @($figure.StartPoint)
            foreach ($segment in $figure.Segments) {
                if ($null -eq $segment.PSObject.Properties['Points']) { continue }
                $samples += $segment.Points
            }
            foreach ($pt in $samples) {
                if ([math]::Abs($pt.X - 10) -le 3) {
                    $gap = [math]::Abs($pt.Y - 10)
                    if ($gap -gt $worst) { $worst = $gap }
                }
            }
        }
        Check 'a sharp corner is drawn as a rounded turn' ($worst -le 2.0) "gap=$([math]::Round($worst,2))px"
    }
}
$aboutWindow.Close()

"== feedback panel: title-bar entry, leaving resets the result card =="
$feedbackWindow = [Activator]::CreateInstance($windowType, @('overview'))
$feedbackWindow.Width = 1280
$feedbackWindow.Height = 853
$feedbackRoot = $feedbackWindow.Content
$feedbackRoot.Measure((New-Object System.Windows.Size(1280, 853)))
$feedbackRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$feedbackRoot.UpdateLayout()

# The entry is an icon between the sensor chip and the minimise button, not a sidebar row.
function Screen-Y($element) {
    return $element.TransformToAncestor($feedbackRoot).Transform((New-Object System.Windows.Point(0, 0))).Y
}
function Screen-X($element) {
    return $element.TransformToAncestor($feedbackRoot).Transform((New-Object System.Windows.Point(0, 0))).X
}
$feedbackBubble = Find-ById $feedbackRoot 'FeedbackPill'
$agentChip = Find-ById $feedbackRoot 'AgentPill'
$captionMin = Find-ById $feedbackRoot 'CaptionMinimize'
Check 'the title bar carries a feedback bubble' ($null -ne $feedbackBubble) ''
if (($null -ne $feedbackBubble) -and ($null -ne $agentChip) -and ($null -ne $captionMin)) {
    Check 'the bubble sits between the sensor chip and minimise' ((Screen-X $feedbackBubble) -gt (Screen-X $agentChip) -and (Screen-X $feedbackBubble) -lt (Screen-X $captionMin)) ("x=" + [math]::Round((Screen-X $feedbackBubble)) + " chip=" + [math]::Round((Screen-X $agentChip)) + " min=" + [math]::Round((Screen-X $captionMin)))
}
$overlay = $windowType.GetField('_overlay', $flags).GetValue($feedbackWindow)
Check 'the panel starts closed' ($overlay.Visibility -ne [System.Windows.Visibility]::Visible) ''
$windowType.GetMethod('ShowFeedback').Invoke($feedbackWindow, $null) | Out-Null
Pump 60
$feedbackRoot.UpdateLayout()
Check 'the bubble opens the feedback panel' ($overlay.Visibility -eq [System.Windows.Visibility]::Visible) ''
$feedbackPageType = $asm.GetType('QuadVitals.Settings.FeedbackPage')
$feedbackPage = $null
$pageQueue = New-Object 'System.Collections.Queue'
$pageQueue.Enqueue($feedbackWindow.Content)
while ($pageQueue.Count -gt 0 -and $null -eq $feedbackPage) {
    $e = $pageQueue.Dequeue()
    if ($e.GetType() -eq $feedbackPageType) { $feedbackPage = $e; break }
    if ($e -is [System.Windows.Media.Visual]) {
        $n = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($e)
        for ($i = 0; $i -lt $n; $i++) { $pageQueue.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($e, $i)) }
    }
}
Check 'the feedback page is reachable' ($null -ne $feedbackPage) ''
if ($null -ne $feedbackPage) {
    $previewBox = $feedbackPageType.GetField('_previewBox', $flags).GetValue($feedbackPage)
    Check 'the preview box retints the scrollbar corner' ($previewBox.Resources.Contains([System.Windows.SystemColors]::ControlBrushKey)) ''
    $resultBody = $feedbackPageType.GetField('_resultBody', $flags).GetValue($feedbackPage)
    $feedbackCard = $feedbackPageType.GetField('_cardTitle', $flags).GetValue($feedbackPage)
    $resultBody.Visibility = [System.Windows.Visibility]::Visible
    $feedbackCard.Text = 'sent'
    $feedbackPageType.GetMethod('ResetResultOnLeave', $flags).Invoke($feedbackPage, $null) | Out-Null
    $collapsed = ($resultBody.Visibility -eq [System.Windows.Visibility]::Collapsed)
    $restored = ($feedbackCard.Text -eq (U @(0x53CD, 0x9988, 0x5185, 0x5BB9)))
    Check 'leaving the feedback page collapses the result card' ($collapsed -and $restored) "collapsed=$collapsed restored=$restored"

    # The hint is an overlay above the input. It has to start on the same character cell as
    # typed text, otherwise it reads as a separate line sitting above and to the left.
    $hintBox = $feedbackPageType.GetField('_placeholder', $flags).GetValue($feedbackPage)
    $textBox = $feedbackPageType.GetField('_text', $flags).GetValue($feedbackPage)
    # The large writing area drops the accent focus edge: the template keeps only the hover trigger.
    Check 'the feedback box has no accent focus edge' ($textBox.HasFocusEdge -eq $false) ''
    Check 'its template keeps just the hover edge' ($textBox.Template.Triggers.Count -eq 1) "triggers=$($textBox.Template.Triggers.Count)"

    $feedbackRoot.UpdateLayout()
    $textOrigin = $textBox.GetRectFromCharacterIndex(0)
    Check 'the hint starts on the typed text column' ([math]::Abs($hintBox.Margin.Left - $textOrigin.X) -le 1) "hint=$($hintBox.Margin.Left) text=$($textOrigin.X)"
    Check 'the hint starts on the typed text row' ([math]::Abs($hintBox.Margin.Top - $textOrigin.Y) -le 1) "hint=$($hintBox.Margin.Top) text=$($textOrigin.Y)"
    $hintWrap = $textBox.ActualWidth - $hintBox.Margin.Left - $hintBox.Margin.Right
    $textWrap = $textBox.ActualWidth - $textOrigin.X - ($textBox.BorderThickness.Right + $textBox.Padding.Right * 2)
    Check 'the hint wraps at the typed text width' ([math]::Abs($hintWrap - $textWrap) -le 1) "hint=$([math]::Round($hintWrap,1)) text=$([math]::Round($textWrap,1))"
}

# The three actions are right-aligned, in the order the owner asked for: clear / attach / submit.
$clearButton = Find-ById $feedbackRoot 'FeedbackClear'
$attachButton = Find-ById $feedbackRoot 'FeedbackAddFiles'
$submitButton = Find-ById $feedbackRoot 'FeedbackSubmit'
if (($null -ne $clearButton) -and ($null -ne $attachButton) -and ($null -ne $submitButton)) {
    $cx = Screen-X $clearButton
    $ax = Screen-X $attachButton
    $sx = Screen-X $submitButton
    Check 'the three actions sit right of the diagnostics row' ($cx -gt 500) ("clear x=" + [math]::Round($cx))
    Check 'the actions run clear / attach / submit' (($cx -lt $ax) -and ($ax -lt $sx)) ("x=" + [math]::Round($cx) + "/" + [math]::Round($ax) + "/" + [math]::Round($sx))
} else { Check 'the three actions are present' $false '' }

# Submitted state: the form actions step aside for a single close, and the panel fits its content
# instead of framing a mostly empty 580 px card.
# FeedbackResult lives in QuadVitals.Core.dll. Resolve it by assembly-qualified name so the object
# stays a plain CLR instance - New-Object would wrap it in a PSObject and Invoke refuses that.
$resultType = $coreAsm.GetType('QuadVitals.Core.FeedbackResult')
$result = $resultType.GetConstructor([Type[]]@()).Invoke([object[]]@())
$result.Id = '20261103-test'
$result.PackagePath = 'C:\temp\pkg.zip'
$result.AttachmentCount = 0
$feedbackPageType.GetMethod('ShowSuccess', $flags).Invoke($feedbackPage, @($result)) | Out-Null
Pump 60
$feedbackRoot.UpdateLayout()
$logBlock = $feedbackPageType.GetField('_logBlock', $flags).GetValue($feedbackPage)
$clearBtn = $feedbackPageType.GetField('_clear', $flags).GetValue($feedbackPage)
$closeResult = $feedbackPageType.GetField('_closeResult', $flags).GetValue($feedbackPage)
$addBtn = $feedbackPageType.GetField('_addFiles', $flags).GetValue($feedbackPage)
$submitBtn = $feedbackPageType.GetField('_submit', $flags).GetValue($feedbackPage)
$overlayHost = $windowType.GetField('_overlayContent', $flags).GetValue($feedbackWindow)
$panelBorder = $overlayHost.Content
Check 'submitted: form actions give way to one close' (($clearBtn.Visibility -ne [System.Windows.Visibility]::Visible) -and ($addBtn.Visibility -ne [System.Windows.Visibility]::Visible) -and ($submitBtn.Visibility -ne [System.Windows.Visibility]::Visible) -and ($closeResult.Visibility -eq [System.Windows.Visibility]::Visible)) ''
Check 'submitted: the diagnostics block hides with the form' ($logBlock.Visibility -ne [System.Windows.Visibility]::Visible) ''
Check 'submitted: the panel fits its content' ([double]::IsNaN($panelBorder.Height)) ("height=" + $panelBorder.Height)
Check 'submitted: the card narrows to the result width' ([math]::Abs($panelBorder.Width - 520) -lt 1) ("width=" + $panelBorder.Width)
$visibleButtons = 0
$scan = New-Object 'System.Collections.Queue'
$scan.Enqueue($feedbackPageType.GetField('_resultBody', $flags).GetValue($feedbackPage))
while ($scan.Count -gt 0) {
    $node = $scan.Dequeue()
    if (($node -is [System.Windows.Controls.Button]) -and ($node.Visibility -eq [System.Windows.Visibility]::Visible)) { $visibleButtons++ }
    if ($node -is [System.Windows.Media.Visual]) {
        $kids = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($node)
        for ($i = 0; $i -lt $kids; $i++) { $scan.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($node, $i)) }
    }
}
Check 'submitted: no buttons inside the result block' ($visibleButtons -eq 0) ("buttons=" + $visibleButtons)
$head = $windowType.GetField('_feedbackHead', $flags).GetValue($feedbackWindow)
Check 'submitted: the panel is shorter than the form' (($panelBorder.ActualHeight -gt 150) -and ($panelBorder.ActualHeight -lt 400)) ("actual=" + [math]::Round($panelBorder.ActualHeight))
Check 'submitted: the close button counts down from five' (([string]$closeResult.Content) -eq ((5).ToString() + (U @(0x79D2, 0x540E, 0x81EA, 0x52A8, 0x5173, 0x95ED)))) ("text=" + $closeResult.Content)
Pump 1100
Check 'submitted: the countdown ticks down' (([string]$closeResult.Content) -match '^[0-4]') ("text=" + $closeResult.Content)
# One second left is enough to prove the countdown actually closes the panel.
$feedbackPageType.GetField('_autoCloseSeconds', $flags).SetValue($feedbackPage, 1)
Pump 1300
Check 'submitted: the countdown closes the panel on its own' ($overlay.Visibility -ne [System.Windows.Visibility]::Visible) ''
$windowType.GetMethod('ShowFeedback').Invoke($feedbackWindow, $null) | Out-Null
Pump 60
$feedbackRoot.UpdateLayout()

Check 'submitted: the title row and its cross are gone' ($head.Visibility -ne [System.Windows.Visibility]::Visible) ''
# Failed upload: the green tick and the "sent" headline must not survive - the panel has to read as
# "saved locally, not uploaded", with the reason and the path on their own lines.
$lastResultField = $feedbackPageType.GetField('_lastResult', $flags)
$lastResultField.SetValue($feedbackPage, $result)
$idLine = $feedbackPageType.GetField('_successId', $flags).GetValue($feedbackPage)
Check 'submitted: the local id line is shown' ($idLine.Visibility -eq [System.Windows.Visibility]::Visible) ''
$feedbackPageType.GetMethod('ShowUploadFailure', $flags).Invoke($feedbackPage, @('timeout')) | Out-Null
Pump 60
$reasonLine = $feedbackPageType.GetField('_failureReason', $flags).GetValue($feedbackPage)
$headlineLine = $feedbackPageType.GetField('_resultHeadline', $flags).GetValue($feedbackPage)
$retryBtn = $feedbackPageType.GetField('_retry', $flags).GetValue($feedbackPage)
Check 'failed upload: the headline stops saying sent' (-not ([string]$headlineLine.Text).Contains((U @(0x5DF2, 0x53D1, 0x9001)))) ("text=" + $headlineLine.Text)
Check 'failed upload: the reason gets its own line' ($reasonLine.Visibility -eq [System.Windows.Visibility]::Visible) ("visibility=" + $reasonLine.Visibility)
Check 'failed upload: no file path on the card' ($null -eq $feedbackPageType.GetField('_failurePath', $flags)) ''
Check 'failed upload: the local id line is gone' ($idLine.Visibility -ne [System.Windows.Visibility]::Visible) ("visibility=" + $idLine.Visibility)
Check 'failed upload: retry is offered next to close' (($retryBtn.Visibility -eq [System.Windows.Visibility]::Visible) -and ([string]$closeResult.Content -eq (U @(0x5173, 0x95ED)))) ("retry=" + $retryBtn.Visibility + " close=" + $closeResult.Content)
Check 'failed upload: retry sits right of close' ((Screen-X $retryBtn) -gt (Screen-X $closeResult)) ("retry=" + [math]::Round((Screen-X $retryBtn)) + " close=" + [math]::Round((Screen-X $closeResult)))
Check 'failed upload: retry wears the primary colour' (([string]$retryBtn.Background) -eq ([string]$submitBtn.Background)) ("retry=" + $retryBtn.Background + " submit=" + $submitBtn.Background)
Check 'failed upload: the countdown is stopped' (-not $overlay.Visibility -eq [System.Windows.Visibility]::Collapsed) ''
Pump 1300
Check 'failed upload: the panel stays open for the retry' ($overlay.Visibility -eq [System.Windows.Visibility]::Visible) ''

$feedbackPageType.GetMethod('RestoreForm', $flags).Invoke($feedbackPage, $null) | Out-Null
Pump 60
Check 'back to the form restores its height' (-not [double]::IsNaN($panelBorder.Height)) ("height=" + $panelBorder.Height)

# The failed-upload retry left the form row: retrying belongs to the result card. What the form gets
# instead is a "resend what never went out" switch, off-screen until something is actually pending.
$historyBox = $feedbackPageType.GetField('_uploadHistory', $flags).GetValue($feedbackPage)
$includeLogsBox = $feedbackPageType.GetField('_includeLogs', $flags).GetValue($feedbackPage)
$retryInForm = $feedbackPageType.GetField('_retry', $flags).GetValue($feedbackPage)
Check 'the form row carries no retry button' ($retryInForm.Visibility -ne [System.Windows.Visibility]::Visible) ("visibility=" + $retryInForm.Visibility)
Check 'the history switch exists and defaults to on' (($null -ne $historyBox) -and ($historyBox.IsChecked -eq $true)) ''
$historyBox.Visibility = [System.Windows.Visibility]::Visible
$feedbackRoot.UpdateLayout()
Check 'the history switch sits above the diagnostics switch' ((Screen-Y $historyBox) -lt (Screen-Y $includeLogsBox)) ("history=" + [math]::Round((Screen-Y $historyBox)) + " logs=" + [math]::Round((Screen-Y $includeLogsBox)))

# The ✕ belongs to the form state only; the result carries its own close button.
Check 'the form keeps a close cross' ($null -ne (Find-ById $feedbackRoot 'FeedbackClose')) ''
$headField = $windowType.GetField('_feedbackHead', $flags)
Check 'the form shows its title row' ($headField.GetValue($feedbackWindow).Visibility -eq [System.Windows.Visibility]::Visible) ''
# The same button still closes immediately when clicked; the countdown is a suggestion, not a lock.
$feedbackPageType.GetMethod('ShowSuccess', $flags).Invoke($feedbackPage, @($result)) | Out-Null
Pump 60
$closeResult2 = $feedbackPageType.GetField('_closeResult', $flags).GetValue($feedbackPage)
$closeResult2.RaiseEvent((New-Object System.Windows.RoutedEventArgs([System.Windows.Controls.Primitives.ButtonBase]::ClickEvent)))
Pump 60
Check 'clicking the close button ends it early' ($overlay.Visibility -ne [System.Windows.Visibility]::Visible) ''
$windowType.GetMethod('ShowFeedback').Invoke($feedbackWindow, $null) | Out-Null
Pump 60
$backdrop = Find-ById $feedbackRoot 'OverlayBackdrop'
Check 'the dim layer exists' ($null -ne $backdrop) ''
$backdropFlag = $windowType.GetField('_overlayClosesOnBackdrop', $flags)
Check 'the feedback panel closes on an outside click' (($null -ne $backdrop) -and $backdropFlag.GetValue($feedbackWindow)) ''
$windowType.GetMethod('CloseOverlay').Invoke($feedbackWindow, $null) | Out-Null
$windowType.GetMethod('ShowNetworkSettings').Invoke($feedbackWindow, $null) | Out-Null
Check 'other dialogs keep their own exit' ($backdropFlag.GetValue($feedbackWindow) -eq $false) ''
$windowType.GetMethod('CloseOverlay').Invoke($feedbackWindow, $null) | Out-Null
$windowType.GetMethod('ShowFeedback').Invoke($feedbackWindow, $null) | Out-Null
Pump 60
$windowType.GetMethod('CloseOverlay').Invoke($feedbackWindow, $null) | Out-Null
# Reopening used to throw "the specified element is already the logical child of another element":
# the frame was rebuilt around the same cached form. Open, close, open again.
$windowType.GetMethod('ShowFeedback').Invoke($feedbackWindow, $null) | Out-Null
Pump 60
$feedbackRoot.UpdateLayout()
Check 'the panel reopens after being closed' ($overlay.Visibility -eq [System.Windows.Visibility]::Visible) ''
$formCount = 0
$scan = New-Object 'System.Collections.Queue'
$scan.Enqueue($feedbackRoot)
while ($scan.Count -gt 0) {
    $node = $scan.Dequeue()
    if ($node.GetType() -eq $feedbackPageType) { $formCount++ }
    if ($node -is [System.Windows.Media.Visual]) {
        $kids = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($node)
        for ($i = 0; $i -lt $kids; $i++) { $scan.Enqueue([System.Windows.Media.VisualTreeHelper]::GetChild($node, $i)) }
    }
}
Check 'reopening keeps a single form instance' ($formCount -eq 1) "count=$formCount"
$windowType.GetMethod('CloseOverlay').Invoke($feedbackWindow, $null) | Out-Null
$feedbackWindow.Close()

"== sidebar: sliding selection pill =="
$navWindow = [Activator]::CreateInstance($windowType, @('overview'))
$navWindow.Width = 1280
$navWindow.Height = 853
$navRoot = $navWindow.Content
$navRoot.Measure((New-Object System.Windows.Size(1280, 853)))
$navRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$navRoot.UpdateLayout()
$navGlider = Find-ById $navRoot 'NavGlider'
Check 'the sidebar has a selection pill' ($null -ne $navGlider) ''
if ($null -ne $navGlider) {
    $navShift = $navGlider.RenderTransform
    Check 'the pill starts on the first item without sliding' ([math]::Abs($navShift.Y) -le 0.5) "y=$($navShift.Y)"
    # Switch to elements (index 1, landing at 48). The position is sampled after the switch to
    # prove the pill travels; sampling a mid-flight value instead is not stable here because the
    # render clock can be starved by background work (WMI, the agent), which lands the animation
    # straight on its final value.
    $windowType.GetMethod('ShowPage').Invoke($navWindow, @('elements')) | Out-Null
    Pump 120
    $mid = $navShift.Y
    Check 'the pill animates instead of snapping to the target' $navShift.HasAnimatedProperties "y=$mid"
    # Switching again while still animating must continue from where the pill is, not snap
    # back to the previous landing position (the old bug: BeginAnimation(null) restored a stale
    # base value and the pill jumped home first).
    $windowType.GetMethod('ShowPage').Invoke($navWindow, @('about')) | Out-Null
    $afterSwitch = $navShift.Y
    Check 'a second switch keeps the current position' ($afterSwitch -ge ($mid - 1.0)) "before=$mid after=$afterSwitch"
    Pump 400
    Check 'the pill slides to the selected item' ([math]::Abs($navShift.Y - 96) -le 1) "y=$($navShift.Y)"
}
$navItems = $windowType.GetField('_navItems', $flags).GetValue($navWindow)
$navOrder = ($navItems | ForEach-Object { [string]$_.Tag }) -join ','
Check 'the sidebar holds three screens, feedback and general are not pages' ($navOrder -eq 'overview,elements,about') "order=$navOrder"
$pageFactories = $windowType.GetField('_pages', $flags).GetValue($navWindow)
Check 'the tools page is no longer registered' (-not $pageFactories.ContainsKey('tools')) ''
Check 'the general settings are no longer a page' (-not $pageFactories.ContainsKey('network')) ''
Check 'the feedback page is no longer registered' (-not $pageFactories.ContainsKey('feedback')) ''
$navWindow.Close()

"== page headings =="
function Page-Title([string]$key) {
    $w = [Activator]::CreateInstance($windowType, @($key))
    $w.Width = 1280
    $w.Height = 853
    $r = $w.Content
    $r.Measure((New-Object System.Windows.Size(1280, 853)))
    $r.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
    $r.UpdateLayout()
    $title = Find-ById $r 'PageTitle'
    $caption = Find-ById $r 'PageSubtitle'
    $text = if ($null -ne $title) { [string]$title.Text } else { '' }
    $sub = if ($null -ne $caption) { [string]$caption.Text } else { '' }
    $w.Close()
    return @($text, $sub)
}
$expectedTitles = @{}
$expectedTitles['overview'] = U @(0x6982, 0x89C8)
$expectedTitles['elements'] = U @(0x663E, 0x793A, 0x9879)
$expectedTitles['about'] = U @(0x5173, 0x4E8E)
foreach ($key in $expectedTitles.Keys) {
    $pair = Page-Title $key
    Check ("page '" + $key + "' has its heading") (($pair[0] -eq $expectedTitles[$key]) -and ($pair[1].Length -gt 0)) ("title=" + $pair[0] + " subtitle=" + $pair[1])
}

"== editor card height =="
$c = New-Case 1
$pageType.GetMethod('SelectElement', $flags).Invoke($c.Page, @(0)) | Out-Null
Layout $c
$metricCombo = Find-ById $c.Page 'ElementMetricCombo'
[void](Select-ComboKey $metricCombo 'diskIoPair')
Layout $c
$editor = Field $c '_editor'
$editorCard = $null
$node = $editor
for ($i = 0; $i -lt 8 -and $null -ne $node; $i++) {
    $node = [System.Windows.Media.VisualTreeHelper]::GetParent($node)
    if ($node -is [System.Windows.Controls.Border]) { $editorCard = $node; break }
}
Check 'the editor card exists' ($null -ne $editorCard) ''
if ($null -ne $editorCard) {
    Check 'the editor card hugs its content' (($editorCard.ActualHeight -gt 120) -and ($editorCard.ActualHeight -lt 404)) "height=$($editorCard.ActualHeight)"
    Check 'the editor card sits at the top of its row' ($editorCard.VerticalAlignment -eq [System.Windows.VerticalAlignment]::Top) "align=$($editorCard.VerticalAlignment)"
}

"== page heading alignment =="
$alignWindow = [Activator]::CreateInstance($windowType, @('overview'))
$alignWindow.Width = 1280
$alignWindow.Height = 853
$alignRoot = $alignWindow.Content
$alignRoot.Measure((New-Object System.Windows.Size(1280, 853)))
$alignRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$alignRoot.UpdateLayout()
$alignTitle = Find-ById $alignRoot 'PageTitle'
$alignPill = Find-ById $alignRoot 'NavGlider'
Check 'the page heading and the nav pill both exist' (($null -ne $alignTitle) -and ($null -ne $alignPill)) ''
if (($null -ne $alignTitle) -and ($null -ne $alignPill)) {
    $pillY = $alignPill.TranslatePoint([System.Windows.Point]::new(0, 0), $alignRoot).Y
    $cardTop = [double]::MaxValue
    foreach ($card in (Find-ByTypeName $alignRoot 'Card')) {
        if (-not ($card -is [System.Windows.FrameworkElement])) { continue }
        $y = $card.TranslatePoint([System.Windows.Point]::new(0, 0), $alignRoot).Y
        if ($y -lt $cardTop) { $cardTop = $y }
    }
    Check 'the nav starts on the first card top edge' ([math]::Abs($pillY - $cardTop) -le 2) "pill=$pillY card=$cardTop"
}
$alignWindow.Close()

"== agent status chip and panel =="
$chipWindow = [Activator]::CreateInstance($windowType, @('overview'))
$chipWindow.Width = 1280
$chipWindow.Height = 853
$chipRoot = $chipWindow.Content
$chipRoot.Measure((New-Object System.Windows.Size(1280, 853)))
$chipRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$chipRoot.UpdateLayout()
$chip = Find-ById $chipRoot 'AgentPill'
Check 'the title bar chip is a clickable button' (($chip -is [System.Windows.Controls.Button]) -and ($chip.Cursor -eq [System.Windows.Input.Cursors]::Hand)) ''
$chipWindow.Close()

$dialogType = $asm.GetType('QuadVitals.Settings.AgentStatusDialog')
$stateType = $asm.GetType('QuadVitals.Settings.AgentStateInfo')
$dialogWindow = [Activator]::CreateInstance($windowType, @('overview'))
$dialog = [Activator]::CreateInstance($dialogType, @($dialogWindow))
$buildPanel = $dialogType.GetMethod('Build')
$sensor = -join [char[]](0x4F20, 0x611F, 0x5668)
$restartSensor = (-join [char[]](0x91CD, 0x542F)) + $sensor
$startSensor = (-join [char[]](0x542F, 0x52A8)) + $sensor
$midDot = [string][char]0x00B7
$normal = -join [char[]](0x6B63, 0x5E38)
$offline = -join [char[]](0x79BB, 0x7EBF)

function New-AgentState([bool]$alive) {
    $s = [Activator]::CreateInstance($stateType)
    $stateType.GetField('Alive').SetValue($s, $alive)
    $stateType.GetField('ChannelOpen').SetValue($s, $alive)
    $stateType.GetField('HasSnapshot').SetValue($s, $alive)
    $stateType.GetField('AgeSeconds').SetValue($s, 1.5)
    $stateType.GetField('LastUpdateLocal').SetValue($s, [DateTime]::Now)
    return $s
}

$livePanel = $buildPanel.Invoke($dialog, @((New-AgentState $true)))
$deadPanel = $buildPanel.Invoke($dialog, @((New-AgentState $false)))
$liveIcon = Find-ById $livePanel 'AgentStateIcon'
$deadIcon = Find-ById $deadPanel 'AgentStateIcon'
$liveAction = Find-ById $livePanel 'AgentAction'
$deadAction = Find-ById $deadPanel 'AgentAction'

# The status light is a 30 px halo ring with a 9 px dot inside it, not a text glyph: a live agent
# breathes (halo scale + opacity animation), a dead one holds the halo at half opacity.
function Status-Shapes($icon) {
    $halo = $null
    $dot = $null
    $width = 0
    if (($null -ne $icon) -and ($icon -is [System.Windows.Controls.Panel])) {
        $width = $icon.Width
        foreach ($child in $icon.Children) {
            if ($child -is [System.Windows.Shapes.Ellipse]) {
                if ($child.Width -ge 20) { $halo = $child } else { $dot = $child }
            }
        }
    }
    return [pscustomobject]@{ Halo = $halo; Dot = $dot; Width = $width }
}
function Dot-Color($icon) {
    $shapes = Status-Shapes $icon
    if ($null -eq $shapes.Dot) { return '<none>' }
    return $shapes.Dot.Fill.Color
}

$liveLight = Status-Shapes $liveIcon
$deadLight = Status-Shapes $deadIcon
Check 'the status light is a halo ring with a dot' (($liveLight.Width -eq 30) -and ($null -ne $liveLight.Halo) -and ($null -ne $liveLight.Dot)) "width=$($liveLight.Width) halo=$($null -ne $liveLight.Halo) dot=$($null -ne $liveLight.Dot)"
Check 'a live agent shows the green dot' ((Dot-Color $liveIcon) -eq [QuadVitals.UI.Theme]::AccentGreen) "dot=$(Dot-Color $liveIcon)"
Check 'a live halo breathes' (($null -ne $liveLight.Halo) -and $liveLight.Halo.HasAnimatedProperties -and ($liveLight.Halo.RenderTransform -is [System.Windows.Media.ScaleTransform])) 'animation expected'
Check 'a dead agent shows the red dot' ((Dot-Color $deadIcon) -eq [QuadVitals.UI.Theme]::AccentRed) "dot=$(Dot-Color $deadIcon)"
Check 'a dead halo holds still at half opacity' (($null -ne $deadLight.Halo) -and (-not $deadLight.Halo.HasAnimatedProperties) -and ($deadLight.Halo.Opacity -eq 0.5)) "opacity=$($deadLight.Halo.Opacity) animated=$($deadLight.Halo.HasAnimatedProperties)"
Check 'the action restarts the sensor while alive' ($liveAction.Content -eq $restartSensor) "text=$($liveAction.Content)"
Check 'the action starts the sensor while offline' ($deadAction.Content -eq $startSensor) "text=$($deadAction.Content)"
Check 'the panel names the shared-memory channel' ((Wait-ById $livePanel 'AgentSubline') -ne $null) ''
Check 'the panel names the shared-memory channel' ((Find-ById $livePanel 'AgentSubline') -ne $null) ''

$statusField = $windowType.GetField('_agentStatus', $flags)
$statusBlock = $statusField.GetValue($dialogWindow)
$dialogWindow.SetAgentStatus($true, 0.4)
Check 'the chip reads sensor normal when alive' ($statusBlock.Text -eq ($sensor + ' ' + $midDot + ' ' + $normal)) "text=$($statusBlock.Text)"
$dialogWindow.SetAgentStatus($false, 0)
Check 'the chip reads sensor offline when dead' ($statusBlock.Text -eq ($sensor + ' ' + $midDot + ' ' + $offline)) "text=$($statusBlock.Text)"

$recoveryType = $asm.GetType('QuadVitals.Settings.AgentAutoRecovery')
$staticFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$should = $recoveryType.GetMethod('ShouldAttempt', $staticFlags)
$nowUtc = [DateTime]::UtcNow
Check 'auto recovery waits one confirming tick' (-not $should.Invoke($null, @($false, 1, 0, $nowUtc, [DateTime]::MinValue))) ''
Check 'auto recovery fires when due' ($should.Invoke($null, @($false, 2, 0, $nowUtc, [DateTime]::MinValue))) ''
Check 'auto recovery stops after five attempts' (-not $should.Invoke($null, @($false, 9, 5, $nowUtc, [DateTime]::MinValue))) ''
Check 'auto recovery does nothing while alive' (-not $should.Invoke($null, @($true, 9, 0, $nowUtc, [DateTime]::MinValue))) ''
$dialogWindow.Close()

"== overview accents =="
$ovWindow = [Activator]::CreateInstance($windowType, @('overview'))
$ovWindow.Width = 1280
$ovWindow.Height = 853
$ovRoot = $ovWindow.Content
$ovRoot.Measure((New-Object System.Windows.Size(1280, 853)))
$ovRoot.Arrange((New-Object System.Windows.Rect(0, 0, 1280, 853)))
$ovRoot.UpdateLayout()
$ovType = $asm.GetType('QuadVitals.Settings.OverviewPage')
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
if ($null -ne $ovPage) {
    # Order is CPU / GPU / memory / disk: the GPU sits in the second slot, so usage and
    # temperature stay next to each other instead of two usage tiles in a row.
    # Compared by on-screen x, not by grid column: the user sees left-to-right order.
    function Tile-Left([string]$field) {
        $tile = $ovType.GetField($field, $flags).GetValue($ovPage)
        if ($null -eq $tile) { return -1 }
        return $tile.TransformToAncestor($ovPage).Transform((New-Object System.Windows.Point(0, 0))).X
    }
    $cpuLeft = Tile-Left '_cpuTile'
    $gpuLeft = Tile-Left '_gpuTile'
    $memoryLeft = Tile-Left '_memoryTile'
    $diskLeft = Tile-Left '_diskTile'
    Check 'the device tiles read cpu, gpu, memory, disk' (($cpuLeft -ge 0) -and ($cpuLeft -lt $gpuLeft) -and ($gpuLeft -lt $memoryLeft) -and ($memoryLeft -lt $diskLeft)) "x=$cpuLeft,$gpuLeft,$memoryLeft,$diskLeft"

}
if ($null -ne $ovPage) {
    $gpuTile = $ovType.GetField('_gpuTile', $flags).GetValue($ovPage)
    $accent = $gpuTile.GetType().GetField('_accent', $flags).GetValue($gpuTile)
    Check 'the GPU gauge shares the CPU/memory green' ($accent -eq [QuadVitals.UI.Theme]::AccentGreen) "accent=$accent"

    # All four gauges share one meaning: green while normal, yellow/red only by the tone thresholds.
    # The disk gauge used to carry the warning colour as its base accent, so a 50% disk looked like
    # an alert permanently.
    foreach ($tileName in '_cpuTile', '_memoryTile', '_gpuTile', '_diskTile') {
        $tile = $ovType.GetField($tileName, $flags).GetValue($ovPage)
        $tileAccent = $tile.GetType().GetField('_accent', $flags).GetValue($tile)
        Check ("$tileName starts from the brand green") ($tileAccent -eq [QuadVitals.UI.Theme]::AccentGreen) "accent=$tileAccent"
    }
}

$ovWindow.Close()

Complete-Run 'settings-ui-elements'
