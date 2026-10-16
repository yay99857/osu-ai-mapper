"""How human maps use 1/4 rhythms: bursts and streams, by tempo and difficulty.

    uv run --project engines/audio-analysis python tools/map-stats/stream_stats.py <dataset> [--split train]

A run is a chain of hit circles whose heads are 1/4 beat apart (a slider may start the chain but
not continue it). Runs of 2-5 notes are counted as bursts, 6 or more as streams; the threshold is
the one osu! ranking criteria use for Hard ("avoid streams of more than 5 notes"). These are
working definitions written down here, not universal ones.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import statistics
from collections import Counter, defaultdict

import project_paths


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def runs_of(ir):
    seg = ir["timing"]["segments"][0]
    beat = seg["beatLengthUs"]
    objs = [o for o in ir["objects"] if o["kind"] != "spinner"]
    runs, current = [], [objs[0]] if objs else []

    for prev, o in zip(objs, objs[1:]):
        quarter = abs((o["timeUs"] - prev["timeUs"]) / beat - 0.25) < 0.03
        if quarter and o["kind"] == "circle" and (prev["kind"] == "circle" or prev is current[0]):
            current.append(o)
        else:
            if len(current) >= 2:
                runs.append(current)
            current = [o]
    if len(current) >= 2:
        runs.append(current)
    return runs, beat


def describe(run, radius):
    pts = [(o["position"]["x"], o["position"]["y"]) for o in run]
    steps = [math.dist(a, b) for a, b in zip(pts, pts[1:])]
    turns = []
    for a, b, c in zip(pts, pts[1:], pts[2:]):
        v1, v2 = (b[0] - a[0], b[1] - a[1]), (c[0] - b[0], c[1] - b[1])
        if math.hypot(*v1) > 2 and math.hypot(*v2) > 2:
            ang = math.degrees(math.atan2(v2[1], v2[0]) - math.atan2(v1[1], v1[0]))
            turns.append((ang + 180) % 360 - 180)
    same_sign = sum(1 for t in turns if t > 0) if turns else 0
    curving = max(same_sign, len(turns) - same_sign) / len(turns) if turns else None
    starts_downbeatish = None
    return {"len": len(run), "step": statistics.median(steps), "step_r": statistics.median(steps) / radius,
            "turn": statistics.median([abs(t) for t in turns]) if turns else None, "one_way": curving}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dataset")
    ap.add_argument("--split")
    ap.add_argument("--generated", help="folder of generated maps (one subfolder per song with beatmap-ir.json) to measure instead")
    ap.add_argument("--label", default="generated", help="difficulty label for --generated rows")
    args = ap.parse_args()

    if args.generated:
        rows = defaultdict(lambda: {"maps": 0, "objects": 0, "in_runs": 0, "lens": Counter(), "steps": [], "turns": [], "one_way": []})
        for folder in sorted(os.listdir(args.generated)):
            path = project_paths.beatmap_ir(os.path.join(args.generated, folder))
            if path is not None:
                accumulate(rows, load(path), args.label)
        report(rows)
        return

    maps = {}
    for line in open(os.path.join(args.dataset, "manifests", "maps.jsonl"), encoding="utf-8"):
        m = json.loads(line)
        maps[m["mapSha256"]] = m
    split_of = {m: f["split"] for f in load(os.path.join(args.dataset, "manifests", "split.json"))["families"] for m in f["mapSha256"]}

    table = defaultdict(lambda: {"maps": 0, "objects": 0, "in_runs": 0, "lens": Counter(), "steps": [], "turns": [], "one_way": []})
    for m in maps.values():
        if m["status"] != "accepted" or (args.split and split_of.get(m["mapSha256"]) != args.split):
            continue
        stars = m["starRating"]
        accumulate(table, load(os.path.join(args.dataset, m["irRef"])), "<4" if stars < 4 else "4-5.5" if stars < 5.5 else ">=5.5")
    report(table)


def accumulate(table, ir, diff):
    segs = ir["timing"]["segments"]
    if max(s["beatLengthUs"] for s in segs) / min(s["beatLengthUs"] for s in segs) > 1.0001:
        return
    bpm = 60e6 / segs[0]["beatLengthUs"]
    radius = 54.4 - 4.48 * ir["settings"]["cs"]
    tempo = "<170" if bpm < 170 else "170-210" if bpm < 210 else ">=210"
    row = table[(tempo, diff)]
    runs, _ = runs_of(ir)
    row["maps"] += 1
    row["objects"] += len(ir["objects"])
    for r in runs:
        d = describe(r, radius)
        row["in_runs"] += d["len"]
        row["lens"]["2-3" if d["len"] <= 3 else "4-5" if d["len"] <= 5 else "6-9" if d["len"] <= 9 else "10+"] += 1
        row["steps"].append(d["step_r"])
        if d["turn"] is not None:
            row["turns"].append(d["turn"])
        if d["one_way"] is not None and d["len"] >= 4:
            row["one_way"].append(d["one_way"])


def report(table):
    print(f"{'tempo':8s} {'stars':7s} {'maps':>4s} {'1/4 share':>9s}  {'run lengths (2-3/4-5/6-9/10+)':30s} {'step/radius':>11s} {'turn deg':>8s} {'curves one way':>14s}")
    for (tempo, diff), r in sorted(table.items()):
        lens = r["lens"]
        total = max(1, sum(lens.values()))
        med = lambda xs, f="{:.2f}": f.format(statistics.median(xs)) if xs else "-"
        print(f"{tempo:8s} {diff:7s} {r['maps']:4d} {r['in_runs'] / max(1, r['objects']):9.0%}  "
              f"{' / '.join(f'{lens[k] / total:.0%}' for k in ('2-3', '4-5', '6-9', '10+')):30s} "
              f"{med(r['steps']):>11s} {med(r['turns'], '{:.0f}'):>8s} {med(r['one_way'], '{:.0%}'):>14s}")


if __name__ == "__main__":
    main()
