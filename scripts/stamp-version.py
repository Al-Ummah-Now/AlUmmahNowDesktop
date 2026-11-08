#!/usr/bin/env python3
"""Writes the release version into the store manifests. Usage: stamp-version.py 1.2.3"""
import re
import sys

version = sys.argv[1]
if not re.fullmatch(r"\d+\.\d+\.\d+", version):
    sys.exit("version must look like 1.2.3")


def stamp(path, pattern, replacement):
    with open(path, encoding="utf-8") as f:
        text = f.read()
    updated, count = re.subn(pattern, replacement, text, count=1)
    if count != 1:
        sys.exit("no version found in " + path)
    with open(path, "w", encoding="utf-8") as f:
        f.write(updated)


for manifest in ("src/AlUmmahNow.Windows/Package.appxmanifest", "src/AlUmmahNow.Studio.Windows/Package.appxmanifest"):
    stamp(manifest, r'(<Identity [^>]*Version=")[^"]+(")', r"\g<1>" + version + r".0\g<2>")
stamp("src/AlUmmahNow.Tizen/tizen-manifest.xml", r'(package="net\.alummahnow\.tv" version=")[^"]+(")', r"\g<1>" + version + r"\g<2>")
