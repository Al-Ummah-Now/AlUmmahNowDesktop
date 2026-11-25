# One owner-review screenshot of the Studio window only (no desktop, taskbar or other windows):
# demo data without the demo notice (AUN_STORE_SHOT=1), a forced Light/Dark theme and window size.
# Usage: windows-studio-shot.ps1 <exe> <lang> <light|dark> <WIDTHxHEIGHT> <scene> <name> <out folder> [tab] [demo: 1|0 (signed out)]
param([string]$Exe, [string]$Lang, [string]$Theme, [string]$Size, [string]$Scene, [string]$Name, [string]$Out, [string]$Tab = "uploads", [string]$Demo = "1")
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace AunShot -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
[DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] input, int size);
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint Type; public MOUSEINPUT Mouse; }
// A real pointer move: Studio gets no input from SetCursorPos on the runner
public static bool MoveTo(int x, int y) {
  var i = new INPUT[1];
  i[0].Mouse.X = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
  i[0].Mouse.Y = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
  i[0].Mouse.Flags = 0x8001;
  return SendInput(1, i, Marshal.SizeOf(typeof(INPUT))) == 1;
}
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
"@
$env:AUN_LANG = $Lang; $env:AUN_TAB = $Tab; $env:AUN_AUTOMATION = $(if ($Demo -eq "1") { "shell" } else { "" }); $env:AUN_DEMO = $Demo; $env:AUN_STORE_SHOT = "1"
$env:AUN_THEME = $Theme; $env:AUN_WINDOW_SIZE = $Size; $env:AUN_SCENE = $Scene; $env:AUN_EXIT_AFTER = $(if ($Scene -eq "highcontrastswitch") { "48" } else { "30" })
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
# Scene "searchhover": the pointer over the header search, from inside the window.
if ($Scene -eq "searchhover") {
  [AunShot.Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
  $cx = [int](($r.Left + $r.Right) / 2)
  foreach ($d in 200, 60, 4, 0) {
    if (-not [AunShot.Win]::MoveTo($cx - 20, $r.Top + 24 + $d)) { throw "the pointer cannot be moved" }
    Start-Sleep -Milliseconds 150
  }
}
# Scene "cardhover": the pointer over the picked card of Analytics, the first one after the sidebar.
if ($Scene -eq "cardhover") {
  [AunShot.Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
  $cx = if ($Lang -eq "ar") { $r.Right - 420 } else { $r.Left + 420 }
  foreach ($d in 200, 60, 4, 0) {
    if (-not [AunShot.Win]::MoveTo($cx, $r.Top + 275 + $d)) { throw "the pointer cannot be moved" }
    Start-Sleep -Milliseconds 150
  }
}
# Scene "highcontrastswitch": turned on while Studio runs.
if ($Scene -eq "highcontrastswitch") { & "$PSScriptRoot/high-contrast.ps1" on | Tee-Object (Join-Path $Out "high-contrast.txt") }
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
if (-not $p.WaitForExit(40000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
& "$PSScriptRoot/windows-result.ps1" $env:AUN_RESULT
# The pointer stays until Studio has said whether it is over the search.
if ($Scene -in "searchhover", "cardhover") { [AunShot.Win]::MoveTo($r.Right + 200, $r.Bottom - 60) | Out-Null }
