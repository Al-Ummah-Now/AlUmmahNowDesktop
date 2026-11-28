# Assert the card in the same window that was captured, using the native UI Automation tree.
param([IntPtr]$Handle, [string]$Lang, [switch]$Studio, [int]$ExpectedProcessId = 0)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$window = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
if ($ExpectedProcessId -ne 0 -and $window.Current.ProcessId -ne $ExpectedProcessId) {
  throw "Captured window is not owned by process $ExpectedProcessId"
}
$elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$names = foreach ($element in $elements) {
  $current = $element.Current
  if (-not $current.IsOffscreen) { $current.Name }
}
$strings = Get-Content -Raw -Encoding utf8 "$PSScriptRoot/../src/AlUmmahNow.App/I18n/$Lang.json" | ConvertFrom-Json
$keys = @("settings.telemetry", "settings.telemetry.off", "settings.telemetry.on", "settings.telemetry.note")
if ($Studio) { $keys += "updates.title" }
foreach ($key in $keys) {
  if ($strings.$key -cnotin $names) { throw "Diagnostics control is missing from the $Lang window: $key" }
}
if (-not $Studio) {
  # The XAML tree mirrors independently. Check the native frame style on the captured window.
  # DWM's caption bounds can stay in logical coordinates after the frame has mirrored.
  Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AunCaption {
  private const int ExtendedStyle = -20;
  private const int LayoutRtl = 0x00400000;
  [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
  private static extern int GetWindowLong(IntPtr window, int index);
  public static bool IsMirrored(IntPtr window) => (GetWindowLong(window, ExtendedStyle) & LayoutRtl) != 0;
}
'@
  if ([AunCaption]::IsMirrored($Handle) -ne ($Lang -eq 'ar')) { throw "Unexpected native caption direction for $Lang" }
}
