#!/usr/bin/env bash
# Runs one Studio scenario under Xvfb. Usage: studio-scenario.sh <lang> <tab> <automation> <name> <output-dir> [watch-dir]
export AUN_LANG=$1 AUN_TAB=$2 AUN_AUTOMATION=$3 AUN_EXIT_AFTER=22 AUN_WATCH_DIR=${6:-}
name=$4 out=$5
export XDG_CONFIG_HOME=$(mktemp -d) AUN_SIMULATE_UPLOADS=1
openbox &
sleep 2
dotnet src/AlUmmahNow.Studio.Linux/bin/Release/net9.0/alummahnow-studio.dll &
app=$!
sleep 17
import -window root "$out/$name.png"
wait $app
