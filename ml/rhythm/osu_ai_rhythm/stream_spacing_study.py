"""Spacing inside human streams (1/4 runs of five or more), by loudness of the passage and tier.

    python -m osu_ai_rhythm.stream_spacing_study <dataset> <analysis-root>
"""

from __future__ import annotations

import sys
from collections import defaultdict

import numpy as np

from .aligned import aligned_maps, grid_arrays, tier
from .study import bar_levels


def main() -> int:
    by = defaultdict(list)
    for m in aligned_maps(sys.argv[1], sys.argv[2], splits={"train"}):
        if m.stars < 4.0:
            continue
        _, g = grid_arrays(m.analysis)
        quint, _ = bar_levels(g)
        o, t = m.objects, tier(m.stars)
        i = 0
        while i < len(o):
            j = i
            while j + 1 < len(o) and o[j + 1].q - o[j].q == 1 and o[j].kind == "circle":
                j += 1
            if j - i + 1 >= 5:
                steps = [np.hypot(o[k + 1].x - o[k].x, o[k + 1].y - o[k].y) / m.radius for k in range(i, j)]
                q = int(quint[min(o[i].q // 16, len(quint) - 1)])
                by[(t, q)].append(np.median(steps))
            i = j + 1
    for t in ("insane", "expert"):
        print(t)
        for q in range(5):
            v = np.array(by.get((t, q), []))
            if len(v) > 20:
                print(f"  q{q}: {len(v):5d} streams, step median {np.median(v):.2f} radii (p25 {np.percentile(v, 25):.2f}, p75 {np.percentile(v, 75):.2f}), spaced (>1 r) {100 * np.mean(v > 1):.0f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
