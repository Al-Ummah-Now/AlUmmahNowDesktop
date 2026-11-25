# One Microsoft Store screenshot: starts the app (environment set by the caller, AUN_DEMO=1), maximises
# its window and captures the work area (the app only, no desktop or taskbar), then waits for the
# app's own scenario result. Usage: store-shot.ps1 <exe> <name> <out folder> [seconds before capture]
param([string]$Exe, [string]$Name, [string]$Out, [int]$Wait = 18)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace AunShot -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
"@
$env:AUN_STORE_SHOT = "1"; $env:AUN_EXIT_AFTER = "$($Wait + 12)"
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 8
$p.Refresh()
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { Start-Sleep -Seconds 5; $p.Refresh() }
[AunShot.Win]::ShowWindow($p.MainWindowHandle, 3) | Out-Null   # SW_MAXIMIZE
[AunShot.Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Seconds ([Math]::Max(2, $Wait - 8))
$a = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$bmp = New-Object System.Drawing.Bitmap $a.Width, $a.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($a.Location, [System.Drawing.Point]::Empty, $a.Size)
$bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
if (-not $p.WaitForExit(40000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
& "$PSScriptRoot/windows-result.ps1" $env:AUN_RESULT
