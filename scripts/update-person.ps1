# What a person does with a running app's window, from outside it (scripts/update-test.mjs).
#   close: the title bar's close button, or Alt+F4: both send the window this message.
#   press: the button of the update bar or screen, through UI Automation. Prints its words and whether it can be pressed.
param([int]$Id, [string]$Action)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace AunPerson -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
$ofApp = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $Id
$window = $null
for ($i = 0; $i -lt 30 -and -not $window; $i++) {
  $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $ofApp)
  if (-not $window) { Start-Sleep -Milliseconds 500 }
}
if (-not $window) { throw "process $Id has no window" }
if ($Action -eq "close") {
  # WM_SYSCOMMAND, SC_CLOSE
  if (-not [AunPerson.Win]::PostMessage([IntPtr]$window.Current.NativeWindowHandle, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero)) { throw "the window took no message" }
  "closed '$($window.Current.Name)'"
} elseif ($Action -eq "press") {
  $named = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), "UpdateDownload"
  $button = $null
  for ($i = 0; $i -lt 20 -and -not $button; $i++) {
    $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $named)
    if (-not $button) { Start-Sleep -Milliseconds 500 }
  }
  if (-not $button) { throw "there is no update button" }
  $words = $button.Current.Name
  if (-not $button.Current.IsEnabled) { "'$words' cannot be pressed" } else {
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    "pressed '$words'"
  }
} else { throw "no such action: $Action" }
