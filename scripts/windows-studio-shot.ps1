# One owner-review screenshot of the Studio window only (no desktop, taskbar or other windows):
# demo data without the demo notice (AUN_STORE_SHOT=1), a forced Light/Dark theme and window size.
# Usage: windows-studio-shot.ps1 <exe> <lang> <light|dark> <WIDTHxHEIGHT> <scene> <name> <out folder> [tab]
param([string]$Exe, [string]$Lang, [string]$Theme, [string]$Size, [string]$Scene, [string]$Name, [string]$Out, [string]$Tab = "uploads")
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace AunShot -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
"@
$env:AUN_LANG = $Lang; $env:AUN_TAB = $Tab; $env:AUN_AUTOMATION = "shell"; $env:AUN_DEMO = "1"; $env:AUN_STORE_SHOT = "1"
$env:AUN_THEME = $Theme; $env:AUN_WINDOW_SIZE = $Size; $env:AUN_SCENE = $Scene; $env:AUN_EXIT_AFTER = "30"
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 8
$p.Refresh()
if ($p.MainWindowHandle -eq [IntPtr]::Zero) { Start-Sleep -Seconds 5; $p.Refresh() }
$h = $p.MainWindowHandle
[AunShot.Win]::ShowWindow($h, 9) | Out-Null                                  # SW_RESTORE
[AunShot.Win]::SetWindowPos($h, [IntPtr]::new(-1), 0, 0, 0, 0, 0x0001) | Out-Null   # topmost, keep size (SWP_NOSIZE)
[AunShot.Win]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 12
$r = New-Object AunShot.Win+RECT
[AunShot.Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null           # DWMWA_EXTENDED_FRAME_BOUNDS
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
$bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
if (-not $p.WaitForExit(40000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
if (Test-Path "$env:AUN_RESULT.crash.txt") { Get-Content "$env:AUN_RESULT.crash.txt" }
if (Test-Path $env:AUN_RESULT) { Get-Content $env:AUN_RESULT } else { "NO RESULT FILE" }
