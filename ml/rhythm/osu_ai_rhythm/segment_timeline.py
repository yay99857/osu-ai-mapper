"""A variable-tempo map read stretch by stretch, against analyses made on each stretch's red line.

    python -m osu_ai_rhythm.segment_timeline <dataset> <segments-dir> <title substring> <difficulty> [<difficulty>...]

<segments-dir>/segNN/analysis.json are analyses of the whole song on one stretch's timing, with a
"segment" block (startUs, endUs, bpm). For every four bars of each stretch: density, patterns,
1/2 jump distance, and which part of the sound the heads land on (low band = kick and bass,
percussive = drums, harmonic = synths and melody; each in the stretch's top quarter).
"""

from __future__ import annotations

import glob
import json
import os
import sys
from collections import Counter

import numpy as np

from .data import CLIENT_CONVENTION_US

MATCH_US = 25_000


def objects_of(dataset: str, title: str, version: str):
    for line in open(os.path.join(dataset, "manifests", "maps.jsonl"), encoding="utf-8"):
        m = json.loads(line)
        if title.lower() in line.lower() and m.get("version") == version:
            ir = json.load(open(os.path.join(dataset, m["irRef"]), encoding="utf-8"))
            return m, ir
    raise SystemExit(f"no map {title} [{version}]")


def describe(ir, analysis, radius):
    seg = analysis["segment"]
    step = analysis["timing"]["beatLengthUs"] / 4
    offset = analysis["timing"]["offsetUs"] + CLIENT_CONVENTION_US
    grid = {p["index"]: p for p in analysis["grid"]}
    objs = []
    for o in ir["objects"]:
        if not seg["startUs"] <= o["timeUs"] < seg["endUs"] or o["kind"] == "spinner":
            continue
        q = round((o["timeUs"] - offset) / step)
        if abs(o["timeUs"] - offset - q * step) > MATCH_US or q not in grid:
            continue
        if o["kind"] == "slider":
            c = o["computed"]
            objs.append((q, round(c["durationUs"] / step), o["position"]["x"], o["position"]["y"], c["endPosition"]["x"], c["endPosition"]["y"], o["spanCount"]))
        else:
            objs.append((q, 0, o["position"]["x"], o["position"]["y"], o["position"]["x"], o["position"]["y"], 0))
    if len(objs) < 8:
        return []
    qs = [o[0] for o in objs]
    lo_q, hi_q = qs[0], qs[-1]
    span = [grid[q] for q in range(lo_q, hi_q + 1) if q in grid]
    p75 = {k: np.quantile([p[k] for p in span], 0.75) for k in ("low", "percussive", "harmonic", "high")}
    # Run labels: 1/4 chains of circles.
    label = ["single"] * len(objs)
    i = 0
    while i < len(objs):
        j = i
        while j + 1 < len(objs) and objs[j + 1][0] - objs[j][0] == 1 and objs[j][1] == 0:
            j += 1
        if j - i + 1 >= 3:
            for k in range(i, j + 1):
                label[k] = "stream" if j - i + 1 >= 5 else "burst"
        i = j + 1
    for k, o in enumerate(objs):
        if label[k] == "single":
            label[k] = "slider" if o[1] else "circle"
    rows = []
    for start in range(lo_q - lo_q % 64, hi_q + 1, 64):
        block = [(o, lab, k) for k, (o, lab) in enumerate(zip(objs, label)) if start <= o[0] < start + 64]
        if not block:
            continue
        mix = Counter(lab for _, lab, _ in block)
        dists = []
        for o, lab, k in block:
            if k and o[0] - (objs[k - 1][0] + objs[k - 1][1]) == 2:
                p = objs[k - 1]
                dists.append(np.hypot(o[2] - p[4], o[3] - p[5]) / radius)
        follow = Counter()
        for o, _, _ in block:
            g = grid[o[0]]
            hits = [name for name, key in (("kick/bass", "low"), ("drums", "percussive"), ("synth", "harmonic")) if g[key] >= p75[key]]
            follow[" + ".join(hits) if hits else "none"] += 1
        energy = np.mean([grid[q]["energy"] for q in range(start, start + 64) if q in grid])
        onset = np.mean([grid[q]["onset"] for q in range(start, start + 64) if q in grid])
        t = (offset + start * step) / 1e6
        n = len(block)
        rows.append(f"  {int(t // 60)}:{t % 60:04.1f} {n / 16:4.2f}/beat energy {energy:.2f} onset {onset:.2f} | "
                    + ", ".join(f"{k} {100 * v / n:.0f}%" for k, v in mix.most_common()) + " | "
                    + (f"1/2 jumps {np.median(dists):.1f}r (max {max(dists):.1f})" if dists else "no 1/2 jumps") + " | on: "
                    + ", ".join(f"{k} {100 * v / n:.0f}%" for k, v in follow.most_common(3)))
    return rows


def main() -> int:
    dataset, segdir, title = sys.argv[1:4]
    for version in sys.argv[4:]:
        m, ir = objects_of(dataset, title, version)
        radius = 54.4 - 4.48 * ir["settings"]["cs"]
        print(f"\n### {m['artist']} - {m['title']} [{version}] {m['starRating']:.2f}*  CS {ir['settings']['cs']} AR {ir['settings']['ar']}")
        for path in sorted(glob.glob(os.path.join(segdir, "seg*", "analysis.json"))):
            a = json.load(open(path, encoding="utf-8"))
            s = a["segment"]
            print(f"-- {s['startUs'] / 1e6:.1f}-{s['endUs'] / 1e6:.1f} s, {s['bpm']:.0f} BPM")
            for row in describe(ir, a, radius):
                print(row)
    return 0


if __name__ == "__main__":
    sys.exit(main())
