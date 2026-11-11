#!/usr/bin/env python3
"""Picks a QA video for the viewer's watch scenario: public, ready, at least 10 s, preferably with
subtitles. QA's test library is purged regularly, so a pinned id goes stale.
Prints "<video-id> <1 if it has subtitles else 0>"."""
import json
import sys
import urllib.request

API = "https://videos.api.qa.alummahnow.net"


def get(path):
    req = urllib.request.Request(API + path, headers={"User-Agent": "AlUmmahNowDesktop-CI"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.load(r)


videos = [v for v in get("/api/videos?limit=100&filter=public")["data"]
          if v.get("uploadStatus") == "ready" and (v.get("duration") or 0) >= 10 and v.get("playUrl")]
if not videos:
    sys.exit("no playable public QA video")
for v in videos[:40]:
    tracks = get(f"/api/videos/{v['id']}/subtitles").get("data", {}).get("tracks", [])
    if tracks:
        print(v["id"], 1)
        sys.exit(0)
print(videos[0]["id"], 0)
