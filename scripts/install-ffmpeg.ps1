# Installs ffmpeg on a Windows CI runner.
#   1. Chocolatey, three tries with backoff (its feed answers 503/504 now and then).
#   2. The same release as a pinned zip, checked against a pinned SHA-256 before anything is unpacked.
# Fails this step with a clear message if neither works, so an outage never looks like a test failure
# later in the job. -DirectOnly skips Chocolatey, to try the fallback by itself.
#
# To move to a newer ffmpeg, change $version and $sha256 together. The hash is on both
# https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-<version>-essentials_build.zip.sha256 and the
# GitHub release page (the asset's digest); the two must agree.
param([switch]$DirectOnly)

$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"  # Invoke-WebRequest is many times slower with its progress bar

$version = "9.1"
$sha256 = "b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd"
$name = "ffmpeg-$version-essentials_build"
$sources = @(
  "https://github.com/GyanD/codexffmpeg/releases/download/$version/$name.zip",
  "https://www.gyan.dev/ffmpeg/builds/packages/$name.zip"
)

function Test-Ffmpeg {
  if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { return $false }
  # All of the output first: Select-Object -First stops the pipeline before $LASTEXITCODE is set
  # (and an ffmpeg.exe that can't be launched throws)
  try { $output = @(ffmpeg -version 2>$null) } catch { Write-Host "ffmpeg can't be launched: $_"; return $false }
  if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) { return $false }
  Write-Host $output[0]
  return $true
}

if (-not $DirectOnly) {
  foreach ($try in 1..3) {
    choco install ffmpeg -y --no-progress
    if (Test-Ffmpeg) { exit 0 }
    if ($try -lt 3) { $wait = 15 * [math]::Pow(2, $try - 1); Write-Host "ffmpeg not installed (try $try); retrying in $wait s"; Start-Sleep -Seconds $wait }
  }
  Write-Host "Chocolatey could not install ffmpeg; downloading the pinned $version release instead."
}

$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$zip = Join-Path $temp "$name.zip"
$root = Join-Path $temp "ffmpeg-direct"

foreach ($try in 1..3) {
  foreach ($url in $sources) {
    try {
      Invoke-WebRequest -Uri $url -OutFile $zip -TimeoutSec 300 -ErrorAction Stop
      $actual = (Get-FileHash -Algorithm SHA256 -Path $zip -ErrorAction Stop).Hash
      if ($actual -ne $sha256) { throw "checksum mismatch: expected $sha256, got $actual" }
      Expand-Archive -Path $zip -DestinationPath $root -Force -ErrorAction Stop
      $bin = Join-Path $root "$name/bin"
      $env:PATH = "$bin;$env:PATH"
      if (-not (Test-Ffmpeg)) { throw "ffmpeg from the zip does not run" }
      if ($env:GITHUB_PATH) { Add-Content -Path $env:GITHUB_PATH -Value $bin -ErrorAction Stop }
      Write-Host "Installed ffmpeg $version from $url (SHA-256 verified)."
      exit 0
    } catch {
      Write-Host "Direct download failed (try $try, $url): $_"
    } finally {
      Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
  }
  if ($try -lt 3) { $wait = 15 * [math]::Pow(2, $try - 1); Write-Host "Retrying the download in $wait s"; Start-Sleep -Seconds $wait }
}

Write-Error "ffmpeg could not be installed, from Chocolatey or from the pinned download: the job stops here, not in a later scenario."
exit 1
