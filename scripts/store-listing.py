#!/usr/bin/env python3
"""Turns store/microsoft/<app>/<language>/ into Partner Center baseListing fields.

Usage: store-listing.py viewer|studio [out.json]   (stdout when no file; the file is always UTF-8)

Output: {"en-us": {"title": ..., "description": ..., "shortDescription": ..., "features": [...],
"keywords": [...], "releaseNotes": ..., "privacyPolicy": ...}, "ar-sa": {...}}. The release workflow
merges these into the draft submission (`msstore submission get` / `update`). `privacyPolicy` is
added only when MSSTORE_PRIVACY_POLICY_URL is set; otherwise Partner Center keeps its own value.
The limits are checked by tests/AlUmmahNow.App.Tests/StoreListingTests.cs.
"""
import json
import os
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent / "store" / "microsoft"
TEXT = {"title": "title.txt", "description": "description.txt", "shortDescription": "short-description.txt", "releaseNotes": "release-notes.txt"}
LISTS = {"features": "features.txt", "keywords": "keywords.txt"}


def listing(folder):
    out = {key: (folder / name).read_text(encoding="utf-8").strip() for key, name in TEXT.items()}
    for key, name in LISTS.items():
        out[key] = [line.strip() for line in (folder / name).read_text(encoding="utf-8").splitlines() if line.strip()]
    privacy = os.environ.get("MSSTORE_PRIVACY_POLICY_URL", "").strip()
    if privacy:
        out["privacyPolicy"] = privacy
    return out


def main():
    if len(sys.argv) not in (2, 3) or sys.argv[1] not in ("viewer", "studio"):
        sys.exit("usage: store-listing.py viewer|studio [out.json]")
    app = ROOT / sys.argv[1]
    result = {folder.name: listing(folder) for folder in sorted(app.iterdir()) if folder.is_dir()}
    text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if len(sys.argv) == 3:
        # A file, not a redirect: Windows consoles and PowerShell pipes are not UTF-8.
        pathlib.Path(sys.argv[2]).write_text(text, encoding="utf-8")
    else:
        sys.stdout.buffer.write(text.encode("utf-8"))


if __name__ == "__main__":
    main()
