# One owner-review screenshot of the Studio window only (no desktop, taskbar or other windows):
# demo data without the demo notice (AUN_STORE_SHOT=1), a forced Light/Dark theme and window size.
# Usage: windows-studio-shot.ps1 <exe> <lang> <light|dark> <WIDTHxHEIGHT> <scene> <name> <out folder> [tab] [demo: 1|0 (signed out)]
param([string]$Exe, [string]$Lang, [string]$Theme, [string]$Size, [string]$Scene, [string]$Name, [string]$Out, [string]$Tab = "uploads", [string]$Demo = "1")
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace AunShot -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
"@
$env:AUN_LANG = $Lang; $env:AUN_TAB = $Tab; $env:AUN_AUTOMATION = $(if ($Demo -eq "1") { "shell" } else { "" }); $env:AUN_DEMO = $Demo; $env:AUN_STORE_SHOT = "1"
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
Start-Sleep -Seconds 10
$r = New-Object AunShot.Win+RECT
# Scene "searchhover": the pointer over the header search. Moved twice, so the window gets a move inside itself.
if ($Scene -eq "searchhover") {
  [AunShot.Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
  $cx = [int](($r.Left + $r.Right) / 2)
  [AunShot.Win]::SetCursorPos($cx - 20, $r.Top + 60) | Out-Null; Start-Sleep -Milliseconds 300
  [AunShot.Win]::SetCursorPos($cx - 20, $r.Top + 24) | Out-Null
}
Start-Sleep -Seconds 2
[AunShot.Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null           # DWMWA_EXTENDED_FRAME_BOUNDS
# Without the frame's one-pixel border: it is translucent, and the desktop showed through it.
$r.Left += 1; $r.Top += 1; $r.Right -= 1; $r.Bottom -= 1
$w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
$bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
if ($Scene -eq "searchhover") { [AunShot.Win]::SetCursorPos($r.Right + 200, $r.Bottom - 60) | Out-Null }
if (-not $p.WaitForExit(40000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
if (Test-Path "$env:AUN_RESULT.crash.txt") { Get-Content "$env:AUN_RESULT.crash.txt" }
if (Test-Path $env:AUN_RESULT) { Get-Content $env:AUN_RESULT } else { "NO RESULT FILE" }
