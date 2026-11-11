# Runs one Studio scenario on a CI desktop session, captures the screen and the UI Automation tree.
param([string]$Exe, [string]$Lang, [string]$Tab, [string]$Automation, [string]$Name, [string]$Out, [string]$WatchDir = "", [string]$Scene = "", [string]$Demo = "1")
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$env:AUN_LANG = $Lang; $env:AUN_TAB = $Tab; $env:AUN_AUTOMATION = $Automation; $env:AUN_EXIT_AFTER = "26"; $env:AUN_SIMULATE_UPLOADS = "1"; $env:AUN_WATCH_DIR = $WatchDir
$env:AUN_SCENE = $Scene; $env:AUN_DEMO = $Demo
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 18
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
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
