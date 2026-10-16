"""Do human maps jump further at musical peaks? Drops (a bar much louder than the four before it)
and strong hits (a head whose onset is the loudest of its bar), against the map's typical jump.

    python -m osu_ai_rhythm.peak_study <dataset> <analysis-root> [<project-dir>...]

Distances are 1/2-gap jumps from the previous object's end, divided by the map's median 1/2 jump,
so maps of every difficulty can be pooled. Projects (generated maps) are measured the same way.
"""

from __future__ import annotations

import json
import os
import sys
from collections import defaultdict

import numpy as np

from .aligned import aligned_maps, grid_arrays
from .study import bar_levels


def drop_bars(energy: np.ndarray) -> set[int]:
    bars = len(energy) // 16
    e = np.array([energy[b * 16:(b + 1) * 16].mean() for b in range(bars)])
    out = set()
    for b in range(4, bars):
        before = e[b - 4:b].mean()
        if before > 0.05 and e[b] > 1.6 * before and e[b] >= np.quantile(e, 0.6):
            out.add(b)
    return out


def measure(objs, radius, analysis, out):
    """objs: (q, end_q, x, y, end_x, end_y)."""
    _, g = grid_arrays(analysis)
    size = len(g["energy"])
    drops = drop_bars(g["energy"])
    strength = 0.55 * g["onset"] + 0.3 * g["low"] + 0.15 * g["high"]
    bar_max = np.array([strength[(q // 16) * 16:(q // 16 + 1) * 16].max() for q in range(size)])
    jumps = []
    for a, b in zip(objs, objs[1:]):
        if b[0] - a[1] == 2 and b[0] < size:
            jumps.append((b[0], np.hypot(b[2] - a[4], b[3] - a[5]) / radius))
    if len(jumps) < 20:
        return
    med = np.median([d for _, d in jumps])
    for q, d in jumps:
        r = d / med
        bar = q // 16
        if bar in drops:
            out["drop bar 1 (first bar of a drop)"].append(r)
        elif bar - 1 in drops:
            out["drop bar 2"].append(r)
        if q % 16 == 0 and bar in drops:
            out["downbeat that starts a drop"].append(r)
        if strength[q] >= 0.999 * bar_max[q]:
            out["strongest hit of its bar"].append(r)
        else:
            out["other jumps"].append(r)


def summary(name, out):
    print(name)
    for k in ("downbeat that starts a drop", "drop bar 1 (first bar of a drop)", "drop bar 2", "strongest hit of its bar", "other jumps"):
        v = np.array(out.get(k, []))
        if len(v):
            print(f"  {k:36s} {len(v):6d} jumps: x{np.median(v):.2f} the map's median (mean x{v.mean():.2f})")


def main() -> int:
    dataset, root = sys.argv[1], sys.argv[2]
    human = defaultdict(list)
    for m in aligned_maps(dataset, root, splits={"train"}):
        if m.stars >= 4.0:
            measure([(o.q, o.q + o.length, o.x, o.y, o.end_x, o.end_y) for o in m.objects], m.radius, m.analysis, human)
    summary("human Insane/Expert (train)", human)
    for project in sys.argv[3:]:
        proj = json.load(open(os.path.join(project, "project.json"), encoding="utf-8"))
        rev = proj["revisions"][-1]
        ir = json.load(open(os.path.join(project, rev["beatmapIr"]), encoding="utf-8"))
        a = json.load(open(os.path.join(project, proj["analysis"]["relativePath"], "analysis.json"), encoding="utf-8"))
        step = a["timing"]["beatLengthUs"] / 4
        off = a["timing"]["offsetUs"] - 15_000
        objs = []
        for o in ir["objects"]:
            if o["kind"] == "spinner":
                continue
            q = round((o["timeUs"] - off) / step)
            if o["kind"] == "slider":
                beats = o["expectedDistancePx"] * o["spanCount"] / (ir["settings"]["sliderMultiplier"] * 100 * o["svMultiplier"])
                last = o["path"]["controlPoints"][-1]
                end = (o["position"]["x"] + last["x"], o["position"]["y"] + last["y"]) if o["spanCount"] % 2 else (o["position"]["x"], o["position"]["y"])
                objs.append((q, q + round(beats * 4), o["position"]["x"], o["position"]["y"], *end))
            else:
                objs.append((q, q, o["position"]["x"], o["position"]["y"], o["position"]["x"], o["position"]["y"]))
        out = defaultdict(list)
        measure(objs, 54.4 - 4.48 * ir["settings"]["cs"], a, out)
        summary(os.path.basename(project.rstrip("/\\")), out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
