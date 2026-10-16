"""Slider velocity and shape in human Insane/Expert maps, by loudness of the passage.

    python -m osu_ai_rhythm.slider_study <dataset> <analysis-root>
"""

from __future__ import annotations

import json
import os
import sys
from collections import Counter, defaultdict

import numpy as np

from .aligned import aligned_maps, grid_arrays
from .study import bar_levels


def main() -> int:
    dataset, root = sys.argv[1], sys.argv[2]
    maps = [m for m in aligned_maps(dataset, root, splits={"train"}) if m.stars >= 4.0]
    irs = {}
    for line in open(os.path.join(dataset, "manifests", "maps.jsonl"), encoding="utf-8"):
        r = json.loads(line)
        irs[r["mapSha256"]] = r.get("irRef")
    sv_by_q, shape, points, sv_maps = defaultdict(list), Counter(), Counter(), []
    slow_on_held, fast_on_loud = [], []
    for m in maps[:600]:
        ir = json.load(open(os.path.join(dataset, irs[m.map_sha]), encoding="utf-8"))
        _, g = grid_arrays(m.analysis)
        quint, _ = bar_levels(g)
        beat = m.analysis["timing"]["beatLengthUs"]
        offset = m.analysis["timing"]["offsetUs"] - 15_000
        svs = []
        for o in ir["objects"]:
            if o["kind"] != "slider":
                continue
            q = round((o["timeUs"] - offset) / (beat / 4))
            sv = o["svMultiplier"]
            svs.append(sv)
            if 0 <= q < len(g["energy"]):
                sv_by_q[int(quint[min(q // 16, len(quint) - 1)])].append(sv)
            cps = o["path"]["controlPoints"]
            kinds = {c.get("segmentType") for c in cps if c.get("segmentType")}
            red = sum(1 for c in cps[1:-1] if c.get("segmentType"))  # explicit segment starts inside the path
            shape["linear" if kinds == {"linear"} else "perfect arc" if kinds == {"perfectCurve"} else "bezier" if kinds <= {"bezier"} else "mixed"] += 1
            points["2 points" if len(cps) <= 2 else "3 points" if len(cps) == 3 else "4-5 points" if len(cps) <= 5 else "6+ points (waves, S, custom)"] += 1
        if svs:
            sv_maps.append(np.std(svs) > 0.02)
    n = sum(shape.values())
    print(f"{n} sliders in {min(600, len(maps))} maps; maps that change slider velocity: {100 * np.mean(sv_maps):.0f}%")
    print("velocity multiplier by loudness quintile (median, p10-p90, share <0.9, share >1.1):")
    for q in range(5):
        v = np.array(sv_by_q[q])
        print(f"  q{q}: {np.median(v):.2f} ({np.percentile(v, 10):.2f}-{np.percentile(v, 90):.2f}), slow {100 * np.mean(v < 0.9):.0f}%, fast {100 * np.mean(v > 1.1):.0f}%")
    print("path type:", ", ".join(f"{k} {100 * c / n:.0f}%" for k, c in shape.most_common()))
    print("control points:", ", ".join(f"{k} {100 * c / n:.0f}%" for k, c in points.most_common()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
