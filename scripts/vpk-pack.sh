#!/usr/bin/env bash
# Packs a published Windows app with Velopack (wiki AUN-APP-UPDATES §5.2): the per-user setup and the update package.
# Usage: vpk-pack.sh <viewer|studio> <version> <publish dir> → out/vpk
# The tool's version is the library's (Directory.Packages.props).
set -euo pipefail
app=$1 version=$2 dir=$3
root="$(cd "$(dirname "$0")/.." && pwd)"
case "$app" in
  viewer) id=AlUmmahNow.Viewer title="Al Ummah Now" exe=AlUmmahNow.exe icon=src/AlUmmahNow.Windows/Assets/app.ico ;;
  studio) id=AlUmmahNow.Studio title="Al Ummah Now Studio" exe=AlUmmahNowStudio.exe icon=src/AlUmmahNow.Studio.Windows/Assets/app.ico ;;
  *) echo "::error::no such app: $app"; exit 2 ;;
esac
vpk=$(sed -n 's/.*Include="Velopack" Version="\([^"]*\)".*/\1/p' "$root/Directory.Packages.props")
[ -n "$vpk" ] || { echo "::error::no Velopack version in Directory.Packages.props"; exit 1; }
rm -rf out/vpk out/vpk-tool; mkdir -p out/vpk
dotnet tool install vpk --version "$vpk" --tool-path out/vpk-tool >/dev/null
# In the Start menu alone, as the MSI; AppStart.cs gives the shortcut its short name
DOTNET_ROLL_FORWARD=Major out/vpk-tool/vpk pack --packId "$id" --packVersion "$version" --packDir "$dir" --mainExe "$exe" \
  --packTitle "$title" --packAuthors "Al Ummah Now" --icon "$root/$icon" --shortcuts StartMenuRoot --channel win-x64 --runtime win-x64 --delta None --noPortable --outputDir out/vpk
ls -l out/vpk
