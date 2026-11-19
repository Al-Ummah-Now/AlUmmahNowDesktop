#!/usr/bin/env bash
# Copies the Fawanees tokens from the AlUmmahNow monorepo's shared native theme registry
# (packages/fanous-tokens) into the shared app layer (Studio and the viewer), and records the source commit:
#   generated/fawanees/{winui,gtk}  colour dictionaries and stylesheets
#   generated/native/csharp/FanousThemes.cs  the native registry (materials, header, nav parts)
#   generated/native/gtk/fawanees{,-dark}.css  its GTK stylesheets (solid page since AlUmmahNow#1596)
# Usage: scripts/sync-fanous.sh [ref]   (needs the gh CLI; ref defaults to main)
set -euo pipefail
ref=${1:-main}
dest=src/AlUmmahNow.App/Theme/fanous
base=packages/fanous-tokens/generated
sha=$(gh api "repos/Al-Ummah-Now/AlUmmahNow/commits/$ref" --jq .sha)
get() { gh api -H "Accept: application/vnd.github.raw" "repos/Al-Ummah-Now/AlUmmahNow/contents/$base/$1?ref=$sha" > "$dest/$2"; }
mkdir -p "$dest/winui" "$dest/gtk" "$dest/native"
for f in winui/Fanous.Colors.xaml winui/Fanous.Tokens.xaml gtk/style.css gtk/style-dark.css; do get "fawanees/$f" "$f"; done
get native/csharp/FanousThemes.cs native/FanousThemes.cs
get native/gtk/fawanees.css native/fawanees.css
get native/gtk/fawanees-dark.css native/fawanees-dark.css
echo "Al-Ummah-Now/AlUmmahNow $sha ($ref) packages/fanous-tokens/generated/{fawanees,native}" > "$dest/SOURCE"
