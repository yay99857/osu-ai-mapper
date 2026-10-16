"""Distance between consecutive objects, in circle radii, by gap and by loudness of the bar.

    python -m osu_ai_rhythm.spacing_study <dataset> <analysis-root> <stars-lo> <stars-hi> [<project-dir>...]

Human maps of the train split within the star range give the reference; each project's active
revision is measured the same way. Distance runs from the previous object's end to the next head.
"""

from __future__ import annotations

import glob
import json
import os
import sys
from collections import defaultdict

import numpy as np

from .aligned import aligned_maps, grid_arrays
from .study import bar_levels

GAPS = {2: "1/2", 4: "1/1"}


def summarise(samples: dict) -> str:
    out = []
    for gap, name in GAPS.items():
        for q in (0, 2, 4):
            d = np.array(samples.get((gap, q), []))
            if len(d) >= 10:
                out.append(f"{name} q{q}: med {np.median(d):.1f} p90 {np.percentile(d, 90):.1f} >6r {100 * (d > 6).mean():.0f}%")
    return " | ".join(out)


def measure(objects, radius, analysis, beat_us=None, offset_us=None) -> dict:
    """objects: list of (q, end_q, x, y, end_x, end_y)."""
    _, g = grid_arrays(analysis)
    quint, _ = bar_levels(g)
    samples = defaultdict(list)
    for a, b in zip(objects, objects[1:]):
        gap = b[0] - a[1]  # from the end of the previous object
        if gap in GAPS and b[0] - a[0] <= 8:
            q = int(quint[min(b[0] // 16, len(quint) - 1)])
            samples[(gap, q)].append(np.hypot(b[2] - a[4], b[3] - a[5]) / radius)
    return samples


def main() -> int:
    dataset, root, lo, hi = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
    human = defaultdict(list)
    count = 0
    for m in aligned_maps(dataset, root, splits={"train"}):
        if not lo <= m.stars <= hi:
            continue
        count += 1
        objs = [(o.q, o.q + o.length, o.x, o.y, o.end_x, o.end_y) for o in m.objects]
        for k, v in measure(objs, m.radius, m.analysis).items():
            human[k] += v
    print(f"human {count} maps {lo}-{hi}*: {summarise(human)}")

    for project in sys.argv[5:]:
        proj = json.load(open(os.path.join(project, "project.json"), encoding="utf-8"))
        rev = proj["revisions"][-1]
        ir = json.load(open(os.path.join(project, rev["beatmapIr"]), encoding="utf-8"))
        analysis = json.load(open(os.path.join(project, proj["analysis"]["relativePath"], "analysis.json"), encoding="utf-8"))
        step = analysis["timing"]["beatLengthUs"] / 4
        offset = analysis["timing"]["offsetUs"] - 15_000
        objs = []
        for o in ir["objects"]:
            if o["kind"] == "spinner":
                continue
            q = round((o["timeUs"] - offset) / step)
            if o["kind"] == "slider":
                # Generated revisions carry no "computed" block: duration from length and velocity,
                # end from the last control point (exact for the generator's linear and short arcs).
                beats = o["expectedDistancePx"] * o["spanCount"] / (ir["settings"]["sliderMultiplier"] * 100 * o["svMultiplier"])
                last = o["path"]["controlPoints"][-1]
                ex, ey = (o["position"]["x"] + last["x"], o["position"]["y"] + last["y"]) if o["spanCount"] % 2 else (o["position"]["x"], o["position"]["y"])
                objs.append((q, q + round(beats * 4), o["position"]["x"], o["position"]["y"], ex, ey))
            else:
                objs.append((q, q, o["position"]["x"], o["position"]["y"], o["position"]["x"], o["position"]["y"]))
        radius = 54.4 - 4.48 * ir["settings"]["cs"]
        print(f"{os.path.basename(project)}: {summarise(measure(objs, radius, analysis))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
