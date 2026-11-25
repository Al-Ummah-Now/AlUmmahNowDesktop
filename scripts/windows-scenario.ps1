# Runs one viewer scenario on a CI desktop session and captures the screen.
param([string]$Exe, [string]$Lang, [string]$Theme, [string]$Automation, [string]$Name, [string]$Out, [int]$ShotAfter = 20, [int]$ExitAfter = 26, [string]$Contrast = "")
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$env:AUN_LANG = $Lang; $env:AUN_THEME = $Theme; $env:AUN_AUTOMATION = $Automation; $env:AUN_EXIT_AFTER = "$ExitAfter"
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$env:AUN_EXPECT_HIGH_CONTRAST = $(if ($Contrast) { "1" } else { "" })
$p = Start-Process -FilePath $Exe -PassThru
# "switch": high contrast is turned on while the viewer runs (the script itself takes 8 seconds).
if ($Contrast -eq "switch") {
  Start-Sleep -Seconds ($ShotAfter - 11)
  & "$PSScriptRoot/high-contrast.ps1" on | Tee-Object (Join-Path $Out "high-contrast-switch.txt") | Out-Host
  Start-Sleep -Seconds 3
} else { Start-Sleep -Seconds $ShotAfter }
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
if (-not $p.WaitForExit(25000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
"exit=$($p.ExitCode)"
& "$PSScriptRoot/windows-result.ps1" $env:AUN_RESULT
