# Assert the card in the same window that was captured, using the native UI Automation tree.
param([IntPtr]$Handle, [string]$Lang)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$window = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
$elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$names = foreach ($element in $elements) {
  $current = $element.Current
  if (-not $current.IsOffscreen) { $current.Name }
}
$strings = Get-Content -Raw -Encoding utf8 "$PSScriptRoot/../src/AlUmmahNow.App/I18n/$Lang.json" | ConvertFrom-Json
foreach ($key in "settings.telemetry", "settings.telemetry.off", "settings.telemetry.on", "settings.telemetry.note") {
  if ($strings.$key -cnotin $names) { throw "Diagnostics control is missing from the $Lang window: $key" }
}
