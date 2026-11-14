# Compiles tests/AlUmmahNow.Platform.Windows.Tests/layout/webauthn_layout.c against the Windows SDK's
# webauthn.h and writes its sizeof/offsetof lines to $Out, for WebAuthnLayoutTests (AUN_WEBAUTHN_LAYOUT).
param([string]$Out)
$ErrorActionPreference = "Stop"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "no Visual C++ tools" }
$src = (Resolve-Path "tests/AlUmmahNow.Platform.Windows.Tests/layout/webauthn_layout.c").Path
$work = (New-Item -ItemType Directory -Force "$env:RUNNER_TEMP/webauthn-layout").FullName
cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cd /d `"$work`" && cl /nologo /W3 `"$src`" /Fe:layout.exe"
if ($LASTEXITCODE -ne 0) { throw "layout program did not compile" }
& "$work/layout.exe" | Set-Content -Encoding ascii $Out
Get-Content $Out | Select-Object -First 1
