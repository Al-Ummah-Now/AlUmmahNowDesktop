# Capture a validated update scene while Studio holds its window open for us.
param(
  [string]$Exe, [string]$Lang, [string]$Theme, [string]$Size, [string]$Update, [string]$Name, [string]$Out,
  [string]$Tab = "videos", [switch]$HighContrast, [switch]$ReadAutomation, [switch]$ExpectWrong
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
if (-not ("AunUpdateShot.Win" -as [type])) {
  Add-Type -Namespace AunUpdateShot -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
"@
}

$processName = [IO.Path]::GetFileNameWithoutExtension($Exe)
function Stop-Studio {
  $prior = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
  if ($prior.Count -gt 0) {
    Stop-Process -InputObject $prior -Force -ErrorAction SilentlyContinue
    Wait-Process -InputObject $prior -Timeout 10
  }
  if (Get-Process -Name $processName -ErrorAction SilentlyContinue) { throw "Studio is still running after cleanup" }
}

function Assert-Window {
  $running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
  if ($running.Count -ne 1 -or $running[0].Id -ne $p.Id) { throw "Expected only Studio process $($p.Id) for $Name" }
  if ([AunUpdateShot.Win]::GetForegroundWindow() -ne $hwnd) { throw "Studio window for $Name is not in the foreground" }
}

function Focus-Studio {
  if ([AunUpdateShot.Win]::GetForegroundWindow() -eq $hwnd) { return }
  if (-not [AunUpdateShot.Win]::SetForegroundWindow($hwnd)) {
    (New-Object -ComObject WScript.Shell).AppActivate($p.Id) | Out-Null
  }
  Start-Sleep -Milliseconds 300
}

Stop-Studio
$env:AUN_LANG = $Lang; $env:AUN_TAB = $Tab; $env:AUN_AUTOMATION = "shell"; $env:AUN_DEMO = "1"; $env:AUN_STORE_SHOT = "1"
$env:AUN_THEME = $Theme; $env:AUN_WINDOW_SIZE = $Size; $env:AUN_SCENE = $(if ($HighContrast) { "highcontrast" } else { "" })
$env:AUN_DEMO_UPDATE = $Update
$env:AUN_DEMO_UPDATE_ANSWER = $(if ($ExpectWrong) { "bar" } else { "" })
$env:AUN_EXIT_AFTER = "20"
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$env:AUN_END_FILE = "$env:AUN_RESULT.end"
$p = $null
try {
  foreach ($file in $env:AUN_RESULT, "$env:AUN_RESULT.pending", "$env:AUN_RESULT.crash.txt", $env:AUN_END_FILE, (Join-Path $Out "$Name.png"), (Join-Path $Out "$Name.uia.txt")) {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
  }

  $p = Start-Process -FilePath $Exe -PassThru
  $limit = [Diagnostics.Stopwatch]::StartNew()
  do {
    if ($p.HasExited) { throw "Studio exited before opening a window for $Name" }
    $p.Refresh()
    $hwnd = $p.MainWindowHandle
    if ($hwnd -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 200
  } while ($limit.Elapsed.TotalSeconds -lt 25)
  if ($hwnd -eq [IntPtr]::Zero) { throw "No Studio window for $Name" }

  [AunUpdateShot.Win]::ShowWindow($hwnd, 9) | Out-Null
  if (-not [AunUpdateShot.Win]::SetWindowPos($hwnd, [IntPtr]::new(-1), 0, 0, 0, 0, 0x0001)) {
    throw "Could not position the Studio window for $Name"
  }
  Focus-Studio

  while (-not (Test-Path -LiteralPath $env:AUN_RESULT)) {
    if ($p.HasExited -or $limit.Elapsed.TotalSeconds -ge 50) { throw "No Studio validation result for $Name" }
    Start-Sleep -Milliseconds 200
  }

  $result = & "$PSScriptRoot/windows-result.ps1" $env:AUN_RESULT
  $result | Out-Host
  $contrast = $HighContrast.IsPresent
  if ((Test-Path -LiteralPath "$env:AUN_RESULT.crash.txt") -or
      "$result" -cnotmatch " highContrast=$contrast .* pid=$($p.Id) hwnd=$($hwnd.ToInt64()) lang=$Lang updatesScrolled=(True|False) RESULT ") {
    throw "Studio did not validate the window, language and contrast for $Name"
  }
  if ($ExpectWrong) {
    if ("$result" -notmatch " update=${Update}:(?!ok\b).+ RESULT FAIL$") { throw "Wrong update scene passed for $Name" }
  } elseif ("$result" -notmatch " update=${Update}:ok .* RESULT PASS$") {
    throw "Studio did not validate update scene $Update for $Name"
  }

  Focus-Studio
  Assert-Window

  if ($ReadAutomation) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $lines = foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
      $current = $element.Current
      if ($current.Name -or $current.AutomationId) { "{0}`t{1}`t{2}" -f $current.ControlType.ProgrammaticName, $current.AutomationId, $current.Name }
    }
    $lines | Set-Content -Encoding utf8 (Join-Path $Out "$Name.uia.txt")
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  } else {
    $frame = New-Object AunUpdateShot.Win+RECT
    if ([AunUpdateShot.Win]::DwmGetWindowAttribute($hwnd, 9, [ref]$frame, 16) -ne 0) { throw "Could not measure the Studio frame for $Name" }
    # Leave out the translucent one-pixel frame.
    $bounds = [System.Drawing.Rectangle]::FromLTRB($frame.Left + 1, $frame.Top + 1, $frame.Right - 1, $frame.Bottom - 1)
  }

  $bitmap = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
  try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
      Assert-Window
      $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
      Assert-Window
      $bitmap.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
      $graphics.Dispose()
    }
  } finally {
    $bitmap.Dispose()
  }
  Set-Content -LiteralPath $env:AUN_END_FILE -Value ""
  Wait-Process -InputObject $p -Timeout 15
  if (Test-Path -LiteralPath "$env:AUN_RESULT.crash.txt") { throw "Studio crashed after capturing $Name" }
  if ($Update -eq "settingsupdate" -and $Size -eq "1440x900" -and "$result" -match " updatesScrolled=True ") {
    throw "The Updates card needed scrolling at $Size in $Name"
  }
} finally {
  try {
    Stop-Studio
  } finally {
    if (Test-Path -LiteralPath $env:AUN_END_FILE) { Remove-Item -LiteralPath $env:AUN_END_FILE }
    $env:AUN_END_FILE = ""
    $env:AUN_DEMO_UPDATE_ANSWER = ""
  }
}
