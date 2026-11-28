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
  # The XAML tree mirrors independently. Check where Windows actually paints the native caption buttons.
  Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AunCaption {
  public const int ButtonBounds = 5;
  [StructLayout(LayoutKind.Sequential)]
  public struct Rect { public int Left, Top, Right, Bottom; }
  [DllImport("dwmapi.dll")]
  public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect bounds, int size);
}
'@
  $bounds = New-Object AunCaption+Rect
  $result = [AunCaption]::DwmGetWindowAttribute($Handle, [AunCaption]::ButtonBounds, [ref]$bounds, 16)
  if ($result -ne 0 -or $bounds.Right -le $bounds.Left) { throw "Cannot locate the native caption buttons for $Lang ($result)" }
  $buttonsOnLeft = ($bounds.Left + $bounds.Right) -lt $window.Current.BoundingRectangle.Width
  if ($buttonsOnLeft -ne ($Lang -eq 'ar')) { throw "Native caption buttons are on the wrong side for $Lang" }
}
