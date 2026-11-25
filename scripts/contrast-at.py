#!/usr/bin/env python3
"""Fails unless two points of a frame have at least this contrast (WCAG), as drawn.

Usage: contrast-at.py <frame.png> <least> <x,y:x,y>...   (ImageMagick's convert reads the frame)
"""
import subprocess
import sys


def light(color):
    parts = [v / 255 / 12.92 if v / 255 <= 0.03928 else ((v / 255 + 0.055) / 1.055) ** 2.4 for v in color]
    return 0.2126 * parts[0] + 0.7152 * parts[1] + 0.0722 * parts[2]


def pixel(frame, place):
    x, y = place.split(",")
    ppm = subprocess.run(["convert", frame, "-alpha", "off", "-depth", "8", "-crop", f"1x1+{x}+{y}", "+repage", "ppm:-"], check=True, capture_output=True).stdout
    return tuple(ppm[-3:])


def main():
    frame, least, failed = sys.argv[1], float(sys.argv[2]), False
    for pair in sys.argv[3:]:
        a, b = (pixel(frame, place) for place in pair.split(":"))
        x, y = light(a), light(b)
        contrast = (max(x, y) + 0.05) / (min(x, y) + 0.05)
        print(f"{frame} {pair}: {a} {b} {contrast:.2f}")
        failed |= contrast < least
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
