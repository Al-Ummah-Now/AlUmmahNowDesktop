# Runs one Admin scenario on the demo queue (AUN_DEMO=1: nothing sent anywhere), captures the
# screen and what Narrator would see (UI Automation tree).
param([string]$Exe, [string]$Lang, [string]$Scene, [string]$Name, [string]$Out, [string]$Theme = "light", [string]$Extra = "")
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
$env:AUN_DEMO = "1"; $env:AUN_LANG = $Lang; $env:AUN_SCENE = $Scene; $env:AUN_THEME = $Theme; $env:AUN_EXIT_AFTER = "24"
$env:AUN_DEMO_HELLO = $(if ($Extra -eq "hello") { "ask" } else { "" })
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 14
# The window is excluded from capture: a screenshot must not show the queue.
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save((Join-Path $Out "$Name.capture.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$lines = foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
  foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.Name -or $c.AutomationId) { "{0}`t{1}`t{2}`t{3}" -f $c.ControlType.ProgrammaticName, $c.AutomationId, $c.Name, $c.AcceleratorKey }
  }
}
$lines | Set-Content -Encoding utf8 (Join-Path $Out "$Name.uia.txt")
if (-not $p.WaitForExit(30000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
if (Test-Path "$env:AUN_RESULT.crash.txt") { Get-Content "$env:AUN_RESULT.crash.txt" }
if (Test-Path $env:AUN_RESULT) { Get-Content $env:AUN_RESULT } else { "NO RESULT FILE" }
