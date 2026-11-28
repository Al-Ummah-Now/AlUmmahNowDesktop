# Runs one viewer scenario on a CI desktop session and captures the screen.
param([string]$Exe, [string]$Lang, [string]$Theme, [string]$Automation, [string]$Name, [string]$Out, [int]$ShotAfter = 20, [int]$ExitAfter = 26, [string]$Contrast = "")
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$env:AUN_LANG = $Lang; $env:AUN_THEME = $Theme; $env:AUN_AUTOMATION = $Automation; $env:AUN_EXIT_AFTER = "$ExitAfter"
$env:AUN_RESULT = Join-Path $Out "$Name.txt"
$env:AUN_EXPECT_HIGH_CONTRAST = $(if ($Contrast) { "1" } else { "" })
# High contrast can take time to apply. Diagnostics also needs the process alive through its UI Automation check.
$diagnostics = $Automation -eq "diagnostics"
$waitForCapture = $diagnostics -or ($Contrast -eq "switch")
$end = "$env:AUN_RESULT.end"
$env:AUN_END_FILE = $(if ($waitForCapture) { $end } else { "" })
if ($waitForCapture -and (Test-Path -LiteralPath $end)) { Remove-Item -LiteralPath $end }
if ($Contrast -eq "switch") {
  & "$PSScriptRoot/high-contrast.ps1" load
}
$p = Start-Process -FilePath $Exe -PassThru
if ($Contrast -eq "switch") {
  Start-Sleep -Seconds ($ShotAfter - 11)
  & "$PSScriptRoot/high-contrast.ps1" on | Tee-Object (Join-Path $Out "high-contrast-switch.txt") | Out-Host
  Start-Sleep -Seconds 3
} else { Start-Sleep -Seconds $ShotAfter }
$captureError = $null
try {
  if ($diagnostics) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
      $p.Refresh()
      if ($p.HasExited) { throw "Viewer process $($p.Id) exited before capturing $Name" }
      $hwnd = $p.MainWindowHandle
      if ($hwnd -ne [IntPtr]::Zero) { break }
      Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    if ($hwnd -eq [IntPtr]::Zero) { throw "No Viewer window owned by process $($p.Id) for $Name" }
  }

  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $Out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  } finally {
    $g.Dispose()
    $bmp.Dispose()
  }
  if ($diagnostics) {
    & "$PSScriptRoot/windows-assert-diagnostics.ps1" -Handle $hwnd -Lang $Lang -ExpectedProcessId $p.Id
  }
} catch {
  $captureError = $_
} finally {
  if ($waitForCapture) { Set-Content -LiteralPath $end -Value "" }
}
if (-not $p.WaitForExit(25000)) { $p.Kill(); "EXIT HANG" | Add-Content "$env:AUN_RESULT.hang.txt" }
if ($waitForCapture) { Remove-Item -LiteralPath $end; $env:AUN_END_FILE = "" }
"exit=$($p.ExitCode)"
& "$PSScriptRoot/windows-result.ps1" $env:AUN_RESULT
if ($captureError) { throw $captureError }
