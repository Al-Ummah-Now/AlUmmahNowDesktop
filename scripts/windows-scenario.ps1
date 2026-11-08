# Runs one viewer scenario on a CI desktop session and captures the screen.
param([string]$Exe, [string]$Lang, [string]$Theme, [string]$Automation, [string]$Name, [string]$Out)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$env:AUN_LANG = $Lang; $env:AUN_THEME = $Theme; $env:AUN_AUTOMATION = $Automation; $env:AUN_EXIT_AFTER = "26"
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 20
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
if (-not $p.WaitForExit(25000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
"exit=$($p.ExitCode)"
if (Test-Path "$env:AUN_RESULT.crash.txt") { Get-Content "$env:AUN_RESULT.crash.txt" }
if (Test-Path $env:AUN_RESULT) { Get-Content $env:AUN_RESULT } else { "NO RESULT FILE" }
