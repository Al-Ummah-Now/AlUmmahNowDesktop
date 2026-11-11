#!/usr/bin/env python3
"""Prints what a Linux screen reader sees: role and name of every element of one app (AT-SPI).
Usage: atspi-dump.py <app-name> <output-file>"""
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


for _ in range(10):
    desktop = pyatspi.Registry.getDesktop(0)
    apps = [desktop.getChildAtIndex(i) for i in range(desktop.childCount)]
    target = [a for a in apps if a is not None and app_name in (a.name or "")]
    if target:
        for a in target:
            walk(a, 0)
        break
    time.sleep(1)

with open(out, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
print(f"atspi: {len(lines)} named elements for {app_name}")
