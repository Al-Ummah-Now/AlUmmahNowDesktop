# What a person (or Windows) does with a running app's window, from outside it (scripts/update-test.mjs).
#   close: the title bar's close button, or Alt+F4: both send the window this message.
#   logout: the session ends.
#   press: the button of the update bar or screen, through UI Automation. Prints its words and whether it can be pressed.
param([int]$Id, [string]$Action)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace AunPerson -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
$ofApp = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty), $Id
$window = $null
for ($i = 0; $i -lt 30 -and -not $window; $i++) {
  # The app's window, not one without a name that the framework keeps
  $window = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $ofApp)) | Where-Object { $_.Current.Name } | Select-Object -First 1
  if (-not $window) { Start-Sleep -Milliseconds 500 }
}
if (-not $window) { throw "process $Id has no window" }
if ($Action -eq "close") {
  $title = $window.Current.Name
  # WM_SYSCOMMAND, SC_CLOSE
  if (-not [AunPerson.Win]::PostMessage([IntPtr]$window.Current.NativeWindowHandle, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero)) { throw "the window took no message" }
  "closed '$title'"
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
} elseif ($Action -eq "logout") {
  # What Windows sends every window of a session that ends, also those that are not seen
  Add-Type -Namespace AunPerson -Name Session -MemberDefinition @'
public delegate bool Each(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(Each each, System.IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint p);
[DllImport("user32.dll")] public static extern System.IntPtr SendMessageTimeout(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l, uint f, uint t, out System.IntPtr r);
public static int End(uint of) {
  var all = new System.Collections.Generic.List<System.IntPtr>();
  EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == of) all.Add(h); return true; }, System.IntPtr.Zero);
  System.IntPtr r;
  foreach (var h in all) SendMessageTimeout(h, 0x0011, System.IntPtr.Zero, System.IntPtr.Zero, 2, 5000, out r);
  foreach (var h in all) SendMessageTimeout(h, 0x0016, new System.IntPtr(1), System.IntPtr.Zero, 2, 5000, out r);
  return all.Count;
}
'@
  "told $([AunPerson.Session]::End($Id)) windows"
} else { throw "no such action: $Action" }
