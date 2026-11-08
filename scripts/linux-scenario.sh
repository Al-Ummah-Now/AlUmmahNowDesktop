#!/usr/bin/env bash
# Runs one viewer scenario under Xvfb and captures a screenshot.
# Usage: linux-scenario.sh <lang> <theme> <automation> <name> <output-dir>
lang=$1 theme=$2 automation=$3 name=$4 out=$5
export AUN_LANG=$lang AUN_THEME=$theme AUN_AUTOMATION=$automation AUN_EXIT_AFTER=24
export XDG_CONFIG_HOME=$(mktemp -d)
openbox &
sleep 2
dotnet src/AlUmmahNow.Linux/bin/Release/net9.0/alummahnow.dll &
app=$!
sleep 18
import -window root "$out/$name.png"
if [[ $automation == watch:* ]]; then
  # Exercise MPRIS as a desktop shell would.
  dest="--session --dest org.mpris.MediaPlayer2.alummahnow --object-path /org/mpris/MediaPlayer2"
  gdbus call $dest --method org.freedesktop.DBus.Properties.Get org.mpris.MediaPlayer2.Player PlaybackStatus | sed 's/^/MPRIS before: /'
  gdbus call $dest --method org.mpris.MediaPlayer2.Player.Pause
  sleep 1
  gdbus call $dest --method org.freedesktop.DBus.Properties.Get org.mpris.MediaPlayer2.Player PlaybackStatus | sed 's/^/MPRIS after: /'
fi
wait $app
