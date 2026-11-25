# Prints a scenario's result. One that wrote an exception has not passed, whatever it says of itself.
param([string]$Result)
$crashed = Test-Path "$Result.crash.txt"
if ($crashed) { Get-Content "$Result.crash.txt" }
if (-not (Test-Path $Result)) { "NO RESULT FILE"; return }
if ($crashed) { (Get-Content $Result -Raw) -replace "RESULT PASS", "RESULT FAIL (an exception, see $(Split-Path -Leaf $Result).crash.txt)" | Set-Content $Result }
Get-Content $Result
