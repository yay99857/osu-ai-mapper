"""How human maps place 1/4 triplets (bursts): how often, stacked or spaced, and the jump into them.

    python -m osu_ai_rhythm.burst_study <dataset> <analysis-root> [<map-hash-file>]

With a file of map hashes (one per line) only those maps are read, e.g. one mapper's.
"""

from __future__ import annotations

import sys
from collections import defaultdict

import numpy as np

from .aligned import aligned_maps, tier


def study(maps):
    rate, spread, into, by_tier = [], defaultdict(list), defaultdict(list), defaultdict(list)
    for m in maps:
        o = m.objects
        tick_ms = m.analysis["timing"]["beatLengthUs"] / 4 / 1000
        minutes = (o[-1].q - o[0].q) * tick_ms / 60000
        i, n = 0, 0
        while i < len(o) - 2:
            j = i
            while j + 1 < len(o) and o[j + 1].q - o[j].q == 1 and o[j].kind == "circle":
                j += 1
            if j - i + 1 == 3:
                n += 1
                s = max(np.hypot(o[i].x - o[k].x, o[i].y - o[k].y) for k in (i + 1, i + 2)) / m.radius
                spread["<100 ms" if tick_ms < 100 else "100-120 ms" if tick_ms < 120 else ">=120 ms"].append(s)
                if i > 0:
                    p = o[i - 1]
                    gap = o[i].q - (p.q + p.length)
                    if gap in (2, 4):
                        into[gap].append(np.hypot(o[i].x - p.end_x, o[i].y - p.end_y) / m.radius)
            i = j + 1
        rate.append(n / max(minutes, 1e-6))
        by_tier[tier(m.stars)].append(n / max(minutes, 1e-6))
    return rate, spread, into, by_tier


def report(name, maps):
    rate, spread, into, by_tier = study(maps)
    print(f"{name}: {len(maps)} maps; triplets per minute median {np.median(rate):.1f}, mean {np.mean(rate):.1f}; "
          + ", ".join(f"{t} {np.mean(v):.1f}" for t, v in sorted(by_tier.items())))
    for k in ("<100 ms", "100-120 ms", ">=120 ms"):
        v = np.array(spread.get(k, []))
        if len(v):
            print(f"  1/4 of {k}: {len(v)} triplets, spread median {np.median(v):.2f} radii, stacked (<0.3 r) {100 * np.mean(v < 0.3):.0f}%, "
                  f"overlapping (0.3-1.2 r) {100 * np.mean((v >= 0.3) & (v <= 1.2)):.0f}%, spaced (>1.2 r) {100 * np.mean(v > 1.2):.0f}%")
    for g, label in ((2, "1/2"), (4, "1/1")):
        v = into.get(g, [])
        if v:
            print(f"  jump into a triplet after a {label} gap: median {np.median(v):.1f} radii, p75 {np.percentile(v, 75):.1f} ({len(v)})")


def main() -> int:
    dataset, root = sys.argv[1], sys.argv[2]
    maps = [m for m in aligned_maps(dataset, root, splits={"train"}) if m.stars >= 4.0]
    if len(sys.argv) > 3:
        wanted = {line.strip() for line in open(sys.argv[3], encoding="utf-8") if line.strip()}
        report("selected maps", [m for m in aligned_maps(dataset, root) if m.map_sha in wanted and m.stars >= 4.0])
    report("all Insane/Expert (train)", maps)
    return 0


if __name__ == "__main__":
    sys.exit(main())
