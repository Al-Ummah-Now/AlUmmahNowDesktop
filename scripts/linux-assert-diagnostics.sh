#!/usr/bin/env bash
# Check the native accessibility dump produced alongside the settings frame.
set -euo pipefail
strings="$(dirname "$0")/../src/AlUmmahNow.App/I18n/$1.json"
test -s "$2.png"
keys=(settings.telemetry settings.telemetry.off settings.telemetry.on settings.telemetry.note)
if [[ ${3:-} == studio ]]; then keys+=(updates.title); fi
for key in "${keys[@]}"; do
  label=$(jq -er --arg key "$key" '.[$key]' "$strings")
  if ! cut -f2 "$2.atspi.txt" | grep -Fx -- "$label" >/dev/null; then
    echo "Diagnostics control is missing from the $1 window: $key" >&2
    exit 1
  fi
done
