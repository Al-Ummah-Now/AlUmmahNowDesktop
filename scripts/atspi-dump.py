#!/usr/bin/env python3
"""Prints what a Linux screen reader sees: role and name of every element of one app (AT-SPI).
Usage: atspi-dump.py <app-name> <output-file> [name of a control to press afterwards]
What opens and closes has one more line: expandable, collapsed or expanded, its name.
The app matches by name, also "dotnet" (a framework-dependent launch registers as the host) and
"Al Ummah Now". With no match, every registered app name is written instead, to debug."""
import sys
import time

import pyatspi

app_name, out = sys.argv[1], sys.argv[2]
press = sys.argv[3] if len(sys.argv) > 3 else ""
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
        states = node.getState()
        if states.contains(pyatspi.STATE_EXPANDABLE):
            lines.append("expandable\t" + ("expanded" if states.contains(pyatspi.STATE_EXPANDED) else "collapsed") + f"\t{name}")
        if name == press and "button" in role:
            pressed.append(node)
    for i in range(node.childCount):
        try:
            walk(node.getChildAtIndex(i), depth + 1)
        except Exception:
            pass


names = []
pressed = []
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
if press:
    print(f"atspi: pressed {len(pressed)} named {press!r}:", [node.queryAction().doAction(0) for node in pressed])
print(f"atspi: {len(lines)} named elements for {app_name}")
