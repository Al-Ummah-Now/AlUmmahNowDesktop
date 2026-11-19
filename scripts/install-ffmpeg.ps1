# Installs ffmpeg on a Windows CI runner. The Chocolatey feed answers 503 now and then: retry with
# backoff, and fail this step with a clear message if ffmpeg still isn't on PATH, so an outage never
# looks like a test failure later in the job.
$ErrorActionPreference = "Continue"
foreach ($try in 1..4) {
  choco install ffmpeg -y --no-progress
  if (Get-Command ffmpeg -ErrorAction SilentlyContinue) { ffmpeg -version | Select-Object -First 1; exit 0 }
  if ($try -lt 4) { $wait = 15 * [math]::Pow(2, $try - 1); Write-Host "ffmpeg not installed (try $try); retrying in $wait s"; Start-Sleep -Seconds $wait }
}
Write-Error "ffmpeg could not be installed (Chocolatey feed unavailable?): the job stops here, not in a later scenario."
exit 1
