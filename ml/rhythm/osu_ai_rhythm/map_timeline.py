"""One human map, phrase by phrase: how its rhythm, patterns and distances follow the music.

    python -m osu_ai_rhythm.map_timeline <dataset> <analysis-root> <title substring> <difficulty name>
"""

from __future__ import annotations

import sys
from collections import Counter

import numpy as np

from .aligned import aligned_maps, grid_arrays
from .study import bar_levels, classify


def main() -> int:
    dataset, root, title, version = sys.argv[1:5]
    import json
    import os
    wanted = {json.loads(l)["mapSha256"] for l in open(os.path.join(dataset, "manifests", "maps.jsonl"), encoding="utf-8")
              if title.lower() in l.lower() and json.loads(l)["version"] == version}
    m = next(m for m in aligned_maps(dataset, root) if m.map_sha in wanted)
    _, g = grid_arrays(m.analysis)
    quint, energy = bar_levels(g)
    labels = classify(m)
    beat_s = m.analysis["timing"]["beatLengthUs"] / 1e6
    offset_s = m.analysis["timing"]["offsetUs"] / 1e6
    print(f"{m.song} [{version}] {m.stars:.2f}*  {m.analysis['timing']['bpm']:.0f} BPM  CS {m.cs}  {len(m.objects)} objects, {m.spinners} spinners")
    print("time   bars   loud  heads/beat  sliders  pattern mix                          1/2 dist med/max (radii)  sound at heads (drum/melody)")
    objs = m.objects
    first, last = objs[0].q // 16, objs[-1].q // 16
    p75p = np.quantile(g["percussive"], 0.75)
    p75h = np.quantile(g["harmonic"], 0.75)
    for start in range(first - first % 4, last + 1, 4):
        block = [(o, lab, k) for k, (o, lab) in enumerate(zip(objs, labels)) if start <= o.q // 16 < start + 4]
        if not block:
            continue
        loud = int(round(np.mean([quint[min(b, len(quint) - 1)] for b in range(start, start + 4)])))
        n = len(block)
        mix = Counter(lab for _, lab, _ in block)
        dists = []
        for o, lab, k in block:
            if k and lab in ("jump", "flow"):
                p = objs[k - 1]
                dists.append(np.hypot(o.x - p.end_x, o.y - p.end_y) / m.radius)
        drum = sum(g["percussive"][o.q] >= p75p for o, _, _ in block if o.q < len(g["percussive"]))
        mel = sum(g["harmonic"][o.q] >= p75h for o, _, _ in block if o.q < len(g["harmonic"]))
        t = offset_s + start * 4 * beat_s
        d = f"{np.median(dists):.1f}/{max(dists):.1f}" if dists else "–"
        print(f"{int(t // 60)}:{int(t % 60):02d}  {start:>3}-{start + 3:<3} q{loud}   {n / 16:>5.2f}     {100 * sum(o.kind == 'slider' for o, _, _ in block) / n:>4.0f}%   "
              + f"{', '.join(f'{k} {v}' for k, v in mix.most_common(4)):<36} {d:<24} {100 * drum / n:.0f}% / {100 * mel / n:.0f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
