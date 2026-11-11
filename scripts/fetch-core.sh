#!/usr/bin/env bash
# Downloads the pinned AlUmmahNow.Core package from its public release and verifies it.
set -euo pipefail
version=0.3.0-rc.1
sha256=d619fcda838b90cfbb2de6f5c0d3b4ef56532a7a2f90a27605c5107619207128
root="$(cd "$(dirname "$0")/.." && pwd)"
file="$root/.nuget-local/AlUmmahNow.Core.$version.nupkg"
if [ ! -f "$file" ]; then
  curl -fsSL --retry 5 --retry-delay 5 --retry-all-errors -o "$file" \
    "https://github.com/Al-Ummah-Now/AlUmmahNowCore/releases/download/v$version/AlUmmahNow.Core.$version.nupkg"
fi
if command -v sha256sum >/dev/null; then actual=$(sha256sum "$file" | cut -d' ' -f1); else actual=$(shasum -a 256 "$file" | cut -d' ' -f1); fi
if [ "$actual" != "$sha256" ]; then echo "checksum mismatch for $file" >&2; rm -f "$file"; exit 1; fi
echo "AlUmmahNow.Core $version verified"
