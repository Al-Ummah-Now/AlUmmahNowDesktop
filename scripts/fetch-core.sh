#!/usr/bin/env bash
# Downloads the pinned AlUmmahNow.Core package from its public release and verifies it.
set -euo pipefail
version=0.1.0
sha256=2e2e609c050ea3ed6ab5d28d11850e7b995c0e801b4df526e8711d59e94f1ab9
root="$(cd "$(dirname "$0")/.." && pwd)"
file="$root/.nuget-local/AlUmmahNow.Core.$version.nupkg"
if [ ! -f "$file" ]; then
  curl -fsSL --retry 5 --retry-delay 5 --retry-all-errors -o "$file" \
    "https://github.com/Al-Ummah-Now/AlUmmahNowCore/releases/download/v$version/AlUmmahNow.Core.$version.nupkg"
fi
if command -v sha256sum >/dev/null; then actual=$(sha256sum "$file" | cut -d' ' -f1); else actual=$(shasum -a 256 "$file" | cut -d' ' -f1); fi
if [ "$actual" != "$sha256" ]; then echo "checksum mismatch for $file" >&2; rm -f "$file"; exit 1; fi
echo "AlUmmahNow.Core $version verified"
