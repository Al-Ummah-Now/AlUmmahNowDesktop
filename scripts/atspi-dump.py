#!/usr/bin/env python3
"""Prints what a Linux screen reader sees: role and name of every element of one app (AT-SPI).
Usage: atspi-dump.py <app-name> <output-file>
The app matches by name, also "dotnet" (a framework-dependent launch registers as the host) and
"Al Ummah Now". With no match, every registered app name is written instead, to debug."""
import sys
import time

import pyatspi

app_name, out = sys.argv[1], sys.argv[2]
lines = []


def walk(node, depth):
    if depth > 40 or node is None:
        return
    try:
        name = node.name or ""
        role = node.getRoleName()
    except Exception:
        return
    if name:
        lines.append(f"{role}\t{name}")
    for i in range(node.childCount):
        try:
            walk(node.getChildAtIndex(i), depth + 1)
        except Exception:
            pass


names = []
for _ in range(10):
    desktop = pyatspi.Registry.getDesktop(0)
    apps = [desktop.getChildAtIndex(i) for i in range(desktop.childCount)]
    names = [(a.name or "") for a in apps if a is not None]
    keys = (app_name.lower(), "alummahnow", "dotnet", "al ummah now")
    target = [a for a in apps if a is not None and any(k in (a.name or "").lower() for k in keys)]
    if target:
        for a in target:
            walk(a, 0)
        break
    time.sleep(1)

if not lines:
    lines = ["# no match; registered apps: " + ", ".join(repr(n) for n in names)]
with open(out, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
print(f"atspi: {len(lines)} named elements for {app_name}")
