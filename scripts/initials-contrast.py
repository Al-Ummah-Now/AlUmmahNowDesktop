#!/usr/bin/env python3
"""Fails unless white initials can be read on their circles in a frame: 4.5 to the circle's color, as drawn.

Usage: initials-contrast.py <frame.png> <x,y of a circle's middle>...   (ImageMagick's convert reads the frame)
"""
import re
import subprocess
import sys

SIZE = 18  # inside the circle (32), around its initial


def light(color):
    parts = [v / 255 / 12.92 if v / 255 <= 0.03928 else ((v / 255 + 0.055) / 1.055) ** 2.4 for v in color]
    return 0.2126 * parts[0] + 0.7152 * parts[1] + 0.0722 * parts[2]


def main():
    frame, failed = sys.argv[1], False
    for place in sys.argv[2:]:
        x, y = (int(v) for v in place.split(","))
        text = subprocess.run(
            ["convert", frame, "-alpha", "off", "-depth", "8", "-crop", f"{SIZE}x{SIZE}+{x - SIZE // 2}+{y - SIZE // 2}", "+repage", "ppm:-"],
            check=True, capture_output=True).stdout
        head = re.match(rb"P6\s+(\d+)\s+(\d+)\s+255\s", text)
        body = text[head.end():]
        pixels = [tuple(body[i:i + 3]) for i in range(0, len(body), 3)]
        lights = sorted(light(p) for p in pixels)
        initial, circle = lights[-1], lights[len(lights) // 2]
        contrast = (initial + 0.05) / (circle + 0.05)
        found = initial > 0.9
        print(f"{frame} {place}: initial {'found' if found else 'NOT FOUND'}, contrast {contrast:.2f}")
        failed |= not found or contrast < 4.5
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
