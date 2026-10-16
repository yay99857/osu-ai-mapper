"""Turn between consecutive 1/2 jumps (the angle that makes triangles, squares, stars, back-and-forth).

    python -m osu_ai_rhythm.angle_study <dataset> <analysis-root> [<project-dir>...]

The turn is the change of direction from one jump to the next: 0 deg keeps going straight, 120 deg
closes a triangle, 90 deg a square, 144 deg a star, 180 deg goes straight back.
"""

from __future__ import annotations

import json
import os
import sys
from collections import Counter

import numpy as np

from .aligned import aligned_maps

BINS = [(0, 30, "straight-ish (0-30)"), (30, 75, "wide turn (30-75)"), (75, 105, "square (75-105)"),
        (105, 135, "triangle (105-135)"), (135, 165, "star (135-165)"), (165, 181, "back and forth (165-180)")]


def turns(objs):
    """objs: (q, end_q, x, y, end_x, end_y); turns at circle-circle-circle 1/2 jumps."""
    out = []
    for a, b, c in zip(objs, objs[1:], objs[2:]):
        if b[0] - a[1] == 2 and c[0] - b[1] == 2 and a[1] == a[0] and b[1] == b[0]:
            v1 = np.array([b[2] - a[4], b[3] - a[5]])
            v2 = np.array([c[2] - b[4], c[3] - b[5]])
            if np.linalg.norm(v1) > 40 and np.linalg.norm(v2) > 40:
                cos = v1 @ v2 / (np.linalg.norm(v1) * np.linalg.norm(v2))
                out.append(np.degrees(np.arccos(np.clip(cos, -1, 1))))
    return out


def show(name, values):
    v = np.array(values)
    c = Counter(next(label for lo, hi, label in BINS if lo <= x < hi) for x in v)
    print(f"{name}: {len(v)} turns, median {np.median(v):.0f} deg | " + ", ".join(f"{label} {100 * c[label] / len(v):.0f}%" for _, _, label in BINS))


def main() -> int:
    dataset, root = sys.argv[1], sys.argv[2]
    human = []
    for m in aligned_maps(dataset, root, splits={"train"}):
        if m.stars >= 4.0:
            human += turns([(o.q, o.q + o.length, o.x, o.y, o.end_x, o.end_y) for o in m.objects])
    show("human Insane/Expert", human)
    for project in sys.argv[3:]:
        proj = json.load(open(os.path.join(project, "project.json"), encoding="utf-8"))
        ir = json.load(open(os.path.join(project, proj["revisions"][-1]["beatmapIr"]), encoding="utf-8"))
        a = json.load(open(os.path.join(project, proj["analysis"]["relativePath"], "analysis.json"), encoding="utf-8"))
        step, off = a["timing"]["beatLengthUs"] / 4, a["timing"]["offsetUs"] - 15_000
        objs = []
        for o in ir["objects"]:
            if o["kind"] == "circle":
                q = round((o["timeUs"] - off) / step)
                objs.append((q, q, o["position"]["x"], o["position"]["y"], o["position"]["x"], o["position"]["y"]))
            elif o["kind"] == "slider":
                q = round((o["timeUs"] - off) / step)
                objs.append((q, q + 99, o["position"]["x"], o["position"]["y"], o["position"]["x"], o["position"]["y"]))
        show(os.path.basename(project.rstrip("/\\")), turns(objs))
    return 0


if __name__ == "__main__":
    sys.exit(main())
