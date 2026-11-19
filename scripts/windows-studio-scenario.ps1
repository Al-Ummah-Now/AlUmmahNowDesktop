# Runs one Studio scenario on a CI desktop session, captures the app window and the UI Automation tree.
param([string]$Exe, [string]$Lang, [string]$Tab, [string]$Automation, [string]$Name, [string]$Out, [string]$WatchDir = "", [string]$Scene = "", [string]$Demo = "1")
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$env:AUN_LANG = $Lang; $env:AUN_TAB = $Tab; $env:AUN_AUTOMATION = $Automation; $env:AUN_EXIT_AFTER = "26"; $env:AUN_SIMULATE_UPLOADS = "1"; $env:AUN_WATCH_DIR = $WatchDir
$env:AUN_SCENE = $Scene; $env:AUN_DEMO = $Demo
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 18
if (-not ("AunDwm" -as [type])) {
  Add-Type -Namespace "" -Name AunDwm -MemberDefinition @"
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int attribute, out RECT value, int size);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hwnd);
"@
}
# Only the app's main window (its visible frame, without the invisible resize borders), not the runner's desktop.
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$p.Refresh()
$hwnd = $p.MainWindowHandle
if ($hwnd -ne [IntPtr]::Zero) {
  [AunDwm]::SetForegroundWindow($hwnd) | Out-Null
  Start-Sleep -Milliseconds 500
  $r = New-Object AunDwm+RECT
  if ([AunDwm]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) -eq 0 -and ($r.Right - $r.Left) -gt 200) {
    $b = New-Object System.Drawing.Rectangle $r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  }
}
"capture $Name at $b" | Write-Host
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
# What Narrator would see: every named element under the app's windows.
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$lines = foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
  foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.Name -or $c.AutomationId) { "{0}`t{1}`t{2}" -f $c.ControlType.ProgrammaticName, $c.AutomationId, $c.Name }
  }
}
$lines | Set-Content -Encoding utf8 (Join-Path $Out "$Name.uia.txt")
if (-not $p.WaitForExit(30000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
if (Test-Path "$env:AUN_RESULT.crash.txt") { Get-Content "$env:AUN_RESULT.crash.txt" }
if (Test-Path $env:AUN_RESULT) { Get-Content $env:AUN_RESULT } else { "NO RESULT FILE" }
