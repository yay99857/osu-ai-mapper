"""Where human Insane/Expert maps put spinners: how often, how long, where in the song, over what sound.

    python -m osu_ai_rhythm.spinner_study <dataset> <analysis-root>
"""

from __future__ import annotations

import sys
from collections import Counter

import numpy as np

from .aligned import aligned_maps, grid_arrays


def main() -> int:
    maps = [m for m in aligned_maps(sys.argv[1], sys.argv[2], splits={"train"}) if m.stars >= 4.0]
    with_spinner = [m for m in maps if m.spinner_spans]
    where, length, loud, held, after = Counter(), [], [], [], Counter()
    for m in with_spinner:
        _, g = grid_arrays(m.analysis)
        e = g["energy"]
        med = max(float(np.median(e[e > 0])), 1e-6)
        first, last = m.objects[0].q, m.objects[-1].q
        for start, end in m.spinner_spans:
            beats = (end - start) / 4
            length.append(beats)
            pos = (start - first) / max(1, last - first)
            where["after the last object (end)" if start > last else "before the first object" if end < first
                  else "last 10% of the map" if pos > 0.9 else "first 10%" if pos < 0.1 else "middle"] += 1
            s, t = max(0, start), min(len(e), end)
            if t > s:
                loud.append(e[s:t].mean() / med)
                held.append(g["sustain"][s:t].mean())
            nxt = [o.q for o in m.objects if o.q > end]
            gap = (nxt[0] - end) / 4 if nxt else None
            after["song ends" if gap is None else "objects within a beat" if gap <= 1 else "a pause of 1-4 beats" if gap <= 4 else "a long pause"] += 1
    n = len(maps)
    spins = sum(len(m.spinner_spans) for m in maps)
    print(f"{n} Insane/Expert maps; {100 * len(with_spinner) / n:.0f}% have a spinner; {spins / n:.2f} spinners per map")
    print(f"length in beats: median {np.median(length):.1f}, p10 {np.percentile(length, 10):.1f}, p90 {np.percentile(length, 90):.1f}")
    print("where:", ", ".join(f"{k} {100 * v / spins:.0f}%" for k, v in where.most_common()))
    print("then:", ", ".join(f"{k} {100 * v / spins:.0f}%" for k, v in after.most_common()))
    print(f"loudness during the spinner: median x{np.median(loud):.2f} the song's median; sustain {np.median(held):.2f} (song-wide grid median for comparison below)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
