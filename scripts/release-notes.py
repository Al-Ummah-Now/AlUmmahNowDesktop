#!/usr/bin/env python3
"""Release notes for the desktop packages. Usage: release-notes.py <version> <prerelease: true|false>"""
import sys

version, prerelease = sys.argv[1], sys.argv[2] == "true"
env = "QA (videos.api.qa.alummahnow.net, auth.qa.alummahnow.net)" if prerelease else "production"
print(f"""## Al Ummah Now desktop {version}{' (pre-release)' if prerelease else ''}

> [!WARNING]
> **These packages are not code-signed.** Windows SmartScreen shows "Windows protected your PC": choose
> *More info* then *Run anyway*, and the installer asks for administrator rights. Check the download
> against `SHA256SUMS.txt` before installing. Signed builds follow once the project has a code-signing
> certificate.

{"> [!NOTE]" + chr(10) + "> Pre-release builds connect to **QA**, not production. Sign in with a QA account. To try production, start the app with the environment variable `AUN_ENV=prod`." + chr(10) if prerelease else ""}
Environment: {env}.

### Studio
| Platform | File | Install |
|---|---|---|
| Windows 10 (19041+) / 11, x64 | `AlUmmahNow-Studio-Windows-{version}-x64-unsigned.msi` | Run the MSI; Start menu → Al Ummah Now Studio |
| Windows, no install | `AlUmmahNow-Studio-Windows-{version}-x64-portable-unsigned.zip` | Unzip and run `AlUmmahNowStudio.exe` |
| Ubuntu 24.04+ / Debian-based, x86-64 | `alummahnow-studio_*_amd64.deb` | `sudo apt install ./alummahnow-studio_*_amd64.deb` |
| Any Linux with Flatpak | `AlUmmahNow-Studio-Linux-{version}-x86_64.flatpak` | `flatpak install --user ./AlUmmahNow-Studio-Linux-{version}-x86_64.flatpak` |

Studio: sign in through your browser (several accounts), pick a channel, upload one video, many or a whole
folder with values set for all (title pattern with {{filename}}, {{n}}, {{date}}; description, category,
language, visibility, playlists, tags, staggered schedule). Uploads resume after a restart. Manage videos
with live search, single and bulk edit, Recently deleted with restore, and channel analytics. Arabic and
English, right-to-left, screen readers on both platforms. Logs: `%LOCALAPPDATA%\\AlUmmahNowStudio\\logs`
on Windows, `~/.local/state/alummahnow-studio/logs` on Linux.

### Viewer and TV
The viewer apps (`AlUmmahNow-Windows-*`, `alummahnow_*.deb`, `AlUmmahNow-Linux-*.flatpak`) and the Samsung
TV package (`AlUmmahNow-Tizen-*-unsigned.tpk`, needs Samsung certificates to install) are included too.
""")
