"""What a set of maps does differently from the rest of the dataset, at the same difficulty.

    python -m osu_ai_rhythm.popular_study <dataset> <analysis-root> <rights-id> [<pulso-v2-bundle>]

Compares the maps of one rights grant (for example the user's popular maps) with all other
training-scope maps, tier by tier: density and patterns by loudness, drops, slider choice, distance
between objects, how consistently similar-sounding bars get the same rhythm, and (optionally) how a
Pulso v2 bundle does on those maps, none of which it saw in training.
"""

from __future__ import annotations

import json
import os
import statistics
import sys
from collections import Counter, defaultdict

import numpy as np

from .aligned import aligned_maps, grid_arrays, tier
from .study import bar_levels, classify

TIERS = ("hard", "insane", "expert")


def profile(m) -> dict:
    _, g = grid_arrays(m.analysis)
    quint, bar_energy = bar_levels(g)
    objs = [o for o in m.objects if o.q < len(g["energy"])]
    labels = classify(m)[:len(objs)]
    first, last = objs[0].q // 16, objs[-1].q // 16
    heads = Counter(o.q // 16 for o in objs)
    out = {"density": defaultdict(list), "pattern": defaultdict(Counter), "dist": defaultdict(list), "slider": Counter(), "rest": Counter()}
    for b in range(first, last + 1):
        if b < len(quint):
            out["density"][int(quint[b])].append(heads[b] / 4)
    for k, (o, lab) in enumerate(zip(objs, labels)):
        qn = int(quint[min(o.q // 16, len(quint) - 1)])
        out["pattern"][qn][lab] += 1
        if k:
            p = objs[k - 1]
            if o.q - (p.q + p.length) == 2 and o.q - p.q <= 8:
                out["dist"][qn].append(np.hypot(o.x - p.end_x, o.y - p.end_y) / m.radius)
        if k + 1 < len(objs) and lab not in ("stream", "burst"):
            gap = objs[k + 1].q - o.q
            key = "1/2" if gap <= 2 else "1/1" if gap <= 4 else "longer"
            out["slider"][(key, o.kind)] += 1
            if o.kind == "slider":
                rest = gap - o.length
                out["rest"]["<=1/4" if rest <= 1 else "1/2" if rest == 2 else ">1/2"] += 1

    # Consistency: bars whose sound is alike (onset pattern on the 16 positions) and whose rhythm
    # was mapped alike. Pairs of bars with correlated audio (r > 0.9) in the mapped span.
    on = g["onset"]
    rhythm = {}
    for o in objs:
        rhythm.setdefault(o.q // 16, set()).add(o.q % 16)
    bars = [b for b in range(first, last + 1) if (b + 1) * 16 <= len(on)]
    vec = {b: on[b * 16:(b + 1) * 16] for b in bars}
    same, pairs = 0, 0
    for i, a in enumerate(bars):
        for b in bars[i + 1:i + 33]:
            va, vb = vec[a], vec[b]
            if va.std() < 1e-6 or vb.std() < 1e-6:
                continue
            if np.corrcoef(va, vb)[0, 1] > 0.9:
                pairs += 1
                same += rhythm.get(a, set()) == rhythm.get(b, set())
    out["consistency"] = (same, pairs)
    return out


def merge(profiles):
    total = {"density": defaultdict(list), "pattern": defaultdict(Counter), "dist": defaultdict(list), "slider": Counter(), "rest": Counter(), "consistency": [0, 0]}
    for p in profiles:
        for q, v in p["density"].items():
            total["density"][q] += v
        for q, v in p["pattern"].items():
            total["pattern"][q] += v
        for q, v in p["dist"].items():
            total["dist"][q] += v
        total["slider"] += p["slider"]
        total["rest"] += p["rest"]
        total["consistency"][0] += p["consistency"][0]
        total["consistency"][1] += p["consistency"][1]
    return total


def describe(name, t, n_maps) -> list[str]:
    lines = [f"  {name} ({n_maps} maps)"]
    lines.append("    heads/beat q0..q4: " + " ".join(f"{statistics.mean(t['density'][q]):.2f}" if t["density"][q] else "–" for q in range(5)))
    for q in (0, 4):
        c = t["pattern"][q]
        n = max(1, sum(c.values()))
        lines.append(f"    patterns q{q}: " + ", ".join(f"{k} {100 * c[k] / n:.0f}%" for k in ("stream", "burst", "jump", "flow", "slider", "slow")))
    lines.append("    1/2 distance (radii) median q0/q2/q4: " + " / ".join(f"{np.median(t['dist'][q]):.1f}" if len(t["dist"][q]) > 10 else "–" for q in (0, 2, 4))
                 + "; >6r in q4: " + (f"{100 * np.mean(np.array(t['dist'][4]) > 6):.0f}%" if t["dist"][4] else "–"))
    s = t["slider"]
    lines.append("    slider share by gap to next head: " + ", ".join(
        f"{k} {100 * s[(k, 'slider')] / max(1, s[(k, 'slider')] + s[(k, 'circle')]):.0f}%" for k in ("1/2", "1/1", "longer")))
    r = t["rest"]
    lines.append("    slider tail to next head: " + ", ".join(f"{k} {100 * v / max(1, sum(r.values())):.0f}%" for k, v in sorted(r.items())))
    same, pairs = t["consistency"]
    lines.append(f"    similar-sounding bars mapped with the same rhythm: {100 * same / max(1, pairs):.0f}% of {pairs} pairs")
    return lines


def main() -> int:
    dataset, root, rights = sys.argv[1], sys.argv[2], sys.argv[3]
    chosen = set()
    with open(os.path.join(dataset, "manifests", "maps.jsonl"), encoding="utf-8") as f:
        for line in f:
            m = json.loads(line)
            if m.get("rightsRef") == rights:
                chosen.add(m["mapSha256"])
    maps = aligned_maps(dataset, root)
    target = [m for m in maps if m.map_sha in chosen]
    reference = [m for m in maps if m.map_sha not in chosen and m.split == "train"]
    print(f"{rights}: {len(target)} maps / {len({m.audio_sha for m in target})} songs; reference: {len(reference)} train maps")

    for t in TIERS:
        a = [m for m in target if tier(m.stars) == t]
        b = [m for m in reference if tier(m.stars) == t]
        if not a:
            continue
        print(f"\n== {t}")
        for line in describe(rights, merge(profile(m) for m in a), len(a)) + describe("reference", merge(profile(m) for m in b), len(b)):
            print(line)

    if len(sys.argv) > 4:
        import joblib
        from .pulso_v2 import evaluate_map, summarise
        bundle = joblib.load(sys.argv[4])
        rows = [evaluate_map(bundle, m, "v2") for m in target if m.off_grid <= 0.05 * (len(m.objects) + m.off_grid)]
        s = summarise(rows)
        print(f"\n== Pulso v2 on these maps (never seen in training): {s['maps']} maps / {s['songs']} songs")
        print("  click F1", s["clickF1"], "| type accuracy", s["typeAtHumanHeads"]["accuracy"], "|", json.dumps(s["endToEnd"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
