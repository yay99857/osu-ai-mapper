"""Do human jumps grow across a phrase whose music rises, and do phrases end on a slider?

    python -m osu_ai_rhythm.phrase_study <dataset> <analysis-root>

Phrases are four bars from a downbeat. Each 1/2 jump's distance is divided by the map's median 1/2
jump; the phrase's first and last bar are compared. "Rising" phrases end at least 15% louder than
they start, "falling" ones 13% quieter.
"""

from __future__ import annotations

import sys
from collections import defaultdict

import numpy as np

from .aligned import aligned_maps, grid_arrays


def main() -> int:
    first, last, slopes = defaultdict(list), defaultdict(list), defaultdict(list)
    end_slider, all_slider = defaultdict(list), []
    for m in aligned_maps(sys.argv[1], sys.argv[2], splits={"train"}):
        if m.stars < 4.0:
            continue
        _, g = grid_arrays(m.analysis)
        e = g["energy"]
        o = m.objects
        jumps = [(b.q, np.hypot(b.x - a.end_x, b.y - a.end_y) / m.radius) for a, b in zip(o, o[1:]) if b.q - (a.q + a.length) == 2]
        if len(jumps) < 30:
            continue
        med = np.median([d for _, d in jumps])
        by_phrase = defaultdict(list)
        for q, d in jumps:
            by_phrase[q // 64].append((q % 64, d / med))
        heads_by_phrase = defaultdict(list)
        for x in o:
            heads_by_phrase[x.q // 64].append(x)
        for ph, items in by_phrase.items():
            start, end = ph * 64, ph * 64 + 64
            if end > len(e):
                continue
            e0, e3 = e[start:start + 16].mean(), e[end - 16:end].mean()
            if e0 <= 0.05:
                continue
            kind = "rising" if e3 > 1.15 * e0 else "falling" if e3 < 0.87 * e0 else "flat"
            a = [d for p, d in items if p < 16]
            z = [d for p, d in items if p >= 48]
            if a and z:
                first[kind].append(np.mean(a))
                last[kind].append(np.mean(z))
            if len(items) >= 8:
                pos = np.array([p for p, _ in items]) / 64
                slopes[kind].append(np.polyfit(pos, [d for _, d in items], 1)[0])
            hs = heads_by_phrase.get(ph, [])
            if hs:
                end_slider[kind].append(hs[-1].kind == "slider")
                all_slider += [h.kind == "slider" for h in hs]
    print("1/2 jumps relative to the map's median: first bar -> last bar of a 4-bar phrase (slope over the phrase)")
    for k in ("rising", "flat", "falling"):
        print(f"  {k:8s} {len(first[k]):6d} phrases: x{np.median(first[k]):.2f} -> x{np.median(last[k]):.2f}; "
              f"mean slope {np.mean(slopes[k]):+.3f}; last object a slider {100 * np.mean(end_slider[k]):.0f}%")
    print(f"  any object a slider: {100 * np.mean(all_slider):.0f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
