#!/usr/bin/env bash
# Runs one Studio scenario under Xvfb and captures the screen.
# Usage: studio-scenario.sh <lang> <tab> <automation> <name> <output-dir> [watch-dir] [scene] [demo: 1|0]
# demo=1 (default) shows canned accounts and channels; demo=0 starts signed out.
export AUN_LANG=$1 AUN_TAB=$2 AUN_AUTOMATION=$3 AUN_EXIT_AFTER=${AUN_EXIT_AFTER:-22} AUN_WATCH_DIR=${6:-} AUN_SCENE=${7:-} AUN_DEMO=${8:-1}
name=$4 out=$5
export XDG_CONFIG_HOME=$(mktemp -d) XDG_STATE_HOME=$(mktemp -d) AUN_SIMULATE_UPLOADS=1
openbox &
sleep 2
dotnet src/AlUmmahNow.Studio.Linux/bin/Release/net9.0/alummahnow-studio.dll &
app=$!
sleep 15
if command -v python3 >/dev/null && python3 -c "import pyatspi" 2>/dev/null; then
  python3 scripts/atspi-dump.py alummahnow-studio "$out/$name.atspi.txt" || true
fi
sleep 2
import -window root "$out/$name.png"
wait $app
