#!/usr/bin/env python3
"""Fails unless a row of a frame has at least so many pixels that stand out (WCAG contrast) from its first pixel, as drawn.

Usage: edges-in-row.py <frame.png> <y> <from x> <to x> <least contrast> <least pixels>   (ImageMagick's convert reads the frame)
The first pixel is the card beside the chart's first bar.
"""
import subprocess
import sys


def light(color):
    parts = [v / 255 / 12.92 if v / 255 <= 0.03928 else ((v / 255 + 0.055) / 1.055) ** 2.4 for v in color]
    return 0.2126 * parts[0] + 0.7152 * parts[1] + 0.0722 * parts[2]


def main():
    frame, y, start, end, least, count = sys.argv[1], *(int(v) for v in sys.argv[2:5]), float(sys.argv[5]), int(sys.argv[6])
    ppm = subprocess.run(
        ["convert", frame, "-alpha", "off", "-depth", "8", "-crop", f"{end - start}x1+{start}+{y}", "+repage", "ppm:-"],
        check=True, capture_output=True).stdout
    body = ppm[-3 * (end - start):]
    lights = [light(tuple(body[i:i + 3])) for i in range(0, len(body), 3)]
    card = lights[0]
    seen = sum(1 for v in lights if (max(v, card) + 0.05) / (min(v, card) + 0.05) >= least)
    print(f"{frame} row {y}: {seen} pixels of {least} or more")
    return 0 if seen >= count else 1


if __name__ == "__main__":
    sys.exit(main())
