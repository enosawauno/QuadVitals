# Driver diagnostics use a private window state; no process or driver is started.
param([string]$UiDir = '')
. (Join-Path $PSScriptRoot 'ui-harness.ps1') -UiDir $UiDir
$owner = [Activator]::CreateInstance($windowType, @('overview'))
$dialogType = $asm.GetType('QuadVitals.Settings.AgentStatusDialog')
$stateType = $asm.GetType('QuadVitals.Settings.AgentStateInfo')
$dialog = [Activator]::CreateInstance($dialogType, @($owner))
$state = [Activator]::CreateInstance($stateType)
$stateType.GetField('Alive').SetValue($state, $true)
$stateType.GetField('Flags').SetValue($state, [QuadVitals.Core.MetricFlags]::PawnIoMissing)
$panel = $dialogType.GetMethod('Build').Invoke($dialog, @($state))
Check 'a missing PawnIO driver shows installation guidance in the sensor panel' (@(Find-ByTypeName $panel 'TextBlock' | Where-Object { $_.Text -eq [QuadVitals.Core.AgentDiagnostics]::PawnIoRequired }).Count -eq 1) 'driver installation guidance expected'
$owner.Close()
Complete-Run 'settings-ui-agent-driver'
