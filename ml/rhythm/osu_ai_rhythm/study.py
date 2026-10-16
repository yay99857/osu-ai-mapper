"""How human mappers distribute objects by rhythm and musical moment.

    uv run --project ml/rhythm python -m osu_ai_rhythm.study <dataset> <analysis-root> <report.md>

Reads the train split only (the held-out songs stay unseen) and writes a Markdown report: density
and patterns by loudness of the bar, what happens at drops and at stops, and when mappers choose a
slider. Figures are derived from training-scope maps and stay local (see docs/data-rights-policy.md).
"""

from __future__ import annotations

import argparse
import statistics
import sys
from collections import Counter, defaultdict

import numpy as np

from .aligned import AlignedMap, aligned_maps, grid_arrays, tier

TIERS = ("hard", "insane", "expert")
QUIET = 0.3  # loudness, relative to the song's median, under which a beat counts as silent


def bar_levels(g: dict[str, np.ndarray]) -> np.ndarray:
    """Per bar, the song-relative loudness quintile (0 quietest .. 4 loudest)."""
    bars = len(g["energy"]) // 16 + 1
    energy = np.array([g["energy"][b * 16:(b + 1) * 16].mean() if b * 16 < len(g["energy"]) else 0 for b in range(bars)])
    ranks = energy.argsort().argsort() / max(1, bars - 1)
    return np.minimum((ranks * 5).astype(int), 4), energy


def classify(m: AlignedMap) -> list[str]:
    """Pattern of each object: stream, burst, slider, jump, flow (1/2 spaced), slow (1/1 or longer)."""
    objs = m.objects
    n = len(objs)
    label = ["slow"] * n
    # Runs of 1/4 gaps, counted in objects.
    i = 0
    while i < n:
        j = i
        while j + 1 < n and objs[j + 1].q - objs[j].q == 1 and objs[j].kind == "circle":
            j += 1
        size = j - i + 1
        if size >= 3:
            for k in range(i, j + 1):
                label[k] = "stream" if size >= 5 else "burst"
        i = j + 1
    for k, o in enumerate(objs):
        if label[k] in ("stream", "burst"):
            continue
        if o.kind == "slider":
            label[k] = "slider"
            continue
        if k == 0:
            continue
        p = objs[k - 1]
        gap = o.q - (p.q + p.length)
        if 0 < gap <= 2 and o.q - p.q <= 4:
            d = np.hypot(o.x - p.end_x, o.y - p.end_y) / m.radius
            label[k] = "jump" if d >= 3.5 else "flow"
    return label


def pct(a, b) -> str:
    return f"{100 * a / b:.0f}%" if b else "–"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("dataset")
    ap.add_argument("analysis_root")
    ap.add_argument("report")
    args = ap.parse_args()

    maps = aligned_maps(args.dataset, args.analysis_root, splits={"train"})
    print(f"{len(maps)} train maps, {len({m.audio_sha for m in maps})} songs", file=sys.stderr)

    # Accumulators keyed by tier.
    density = defaultdict(lambda: defaultdict(list))  # tier -> quintile -> heads per beat
    patterns = defaultdict(lambda: defaultdict(Counter))  # tier -> quintile -> pattern counts
    jump_d = defaultdict(lambda: defaultdict(list))  # tier -> quintile -> 1/2 distance / radius
    drop = defaultdict(lambda: {"before": [], "after": [], "stream_before": [], "stream_after": []})
    stops = defaultdict(Counter)
    slider_by = defaultdict(lambda: defaultdict(Counter))  # tier -> feature bucket -> circle/slider
    slider_fill = defaultdict(Counter)
    head_source = defaultdict(Counter)
    off_grid, total = 0, 0

    for m in maps:
        t = tier(m.stars)
        _, g = grid_arrays(m.analysis)
        size = len(g["energy"])
        quint, bar_energy = bar_levels(g)
        objs = [o for o in m.objects if 0 <= o.q < size]
        if len(objs) < 20:
            continue
        off_grid += m.off_grid
        total += len(m.objects) + m.off_grid
        labels = classify(m)
        labels = [lab for o, lab in zip(m.objects, labels) if 0 <= o.q < size]
        first_bar, last_bar = objs[0].q // 16, objs[-1].q // 16
        heads_in_bar = Counter(o.q // 16 for o in objs)
        quarter_in_bar = Counter(o.q // 16 for o, lab in zip(objs, labels) if lab in ("stream", "burst"))

        for b in range(first_bar, last_bar + 1):
            if b < len(quint):
                density[t][int(quint[b])].append(heads_in_bar[b] / 4)
        for k, (o, lab) in enumerate(zip(objs, labels)):
            b = o.q // 16
            qn = int(quint[min(b, len(quint) - 1)])
            patterns[t][qn][lab] += 1
            if lab in ("jump", "flow"):
                p = objs[k - 1]
                jump_d[t][qn].append(np.hypot(o.x - p.end_x, o.y - p.end_y) / m.radius)

        # Drops: a bar at least 60% louder than the four before it, inside the mapped span.
        for b in range(max(first_bar + 4, 4), min(last_bar - 3, len(bar_energy) - 4)):
            before = bar_energy[b - 4:b].mean()
            if before > 0.05 and bar_energy[b] > 1.6 * before and bar_energy[b] >= np.quantile(bar_energy, 0.6):
                drop[t]["before"].append(sum(heads_in_bar[c] for c in range(b - 4, b)) / 16)
                drop[t]["after"].append(sum(heads_in_bar[c] for c in range(b, b + 4)) / 16)
                drop[t]["stream_before"].append(sum(quarter_in_bar[c] for c in range(b - 4, b)))
                drop[t]["stream_after"].append(sum(quarter_in_bar[c] for c in range(b, b + 4)))

        # Stops: at least one beat of near silence (relative to the song) after sound, mapped around.
        loud = np.convolve(g["energy"], np.ones(4) / 4, mode="same")
        level = max(float(np.median(g["energy"][objs[0].q:objs[-1].q])), 1e-6)
        heads = {o.q: o for o in objs}
        q = objs[0].q + 4
        while q < objs[-1].q - 8:
            if loud[q - 1] > 0.8 * level and all(loud[q:q + 4] < QUIET * level):
                last = max((o for o in objs if o.q < q), key=lambda o: o.q)
                stops[t]["stops"] += 1
                stops[t]["last object a slider"] += last.kind == "slider"
                stops[t]["slider runs into the quiet"] += last.kind == "slider" and last.q + last.length > q + 1
                stops[t]["object inside the quiet beat"] += any(heads.get(i) for i in range(q, q + 4))
                q += 8
            else:
                q += 1

        # Slider or circle, by what is heard at the head (song-relative terciles) and by the gap to
        # the next head.
        span = slice(objs[0].q, objs[-1].q + 1)

        def tercile(name: str, value: float) -> str:
            lo, hi = np.quantile(g[name][span], [1 / 3, 2 / 3])
            return "high" if value > hi else "mid" if value > lo else "low"

        p75 = {k: float(np.quantile(g[k][span], 0.75)) for k in ("percussive", "harmonic")}
        for k, o in enumerate(objs):
            if labels[k] in ("stream", "burst"):
                continue
            held = g["toneHeld"][o.q]
            sustain = g["sustain"][o.q]
            nxt = objs[k + 1].q - o.q if k + 1 < len(objs) else 99
            kind = o.kind
            slider_by[t][f"held tone {tercile('toneHeld', held)}"][kind] += 1
            slider_by[t][f"sustain {tercile('sustain', sustain)}"][kind] += 1
            if nxt in (3, 4):
                slider_by[t][f"next head in 1/1, held tone {tercile('toneHeld', held)}"][kind] += 1
            slider_by[t][f"next head in {'1/2' if nxt <= 2 else '1/1' if nxt <= 4 else '2/1' if nxt <= 8 else 'more'}"][kind] += 1
            if o.kind == "slider" and nxt < 99:
                rest = nxt - o.length
                slider_fill[t]["ends right before next (<=1/4)" if 0 <= rest <= 1 else "1/2 rest" if rest == 2 else "longer rest" if rest > 2 else "overlaps"] += 1
            # Which part of the sound the head follows.
            perc, harm = g["percussive"][o.q], g["harmonic"][o.q]
            dp, dh = perc >= p75["percussive"], harm >= p75["harmonic"]
            src = "both" if dp and dh else "drums" if dp else "melody/voice" if dh else "neither (rhythm continues)"
            head_source[t][src] += 1

    out = ["# How human mappers distribute objects", "",
           f"Train split: {len(maps)} maps from {len({m.audio_sha for m in maps})} songs, analysed on the human timing. "
           f"Objects off the 1/4 grid (1/3, 1/6...): {pct(off_grid, total)}.", "",
           "Quintile 0 is the quietest fifth of the song's bars, 4 the loudest. Radius = circle radius from CS.", ""]

    out += ["## Density (heads per beat) by loudness of the bar", "", "| tier | q0 | q1 | q2 | q3 | q4 |", "|---|---|---|---|---|---|"]
    for t in TIERS:
        out.append(f"| {t} | " + " | ".join(f"{statistics.mean(density[t][q]):.2f}" if density[t][q] else "–" for q in range(5)) + " |")

    out += ["", "## Patterns by loudness of the bar (share of objects)", ""]
    kinds = ("stream", "burst", "jump", "flow", "slider", "slow")
    for t in TIERS:
        out += [f"**{t}**", "", "| quintile | " + " | ".join(kinds) + " | 1/2 distance (radii, median) |", "|---" * (len(kinds) + 2) + "|"]
        for q in range(5):
            c = patterns[t][q]
            n = sum(c.values())
            d = f"{statistics.median(jump_d[t][q]):.1f}" if jump_d[t][q] else "–"
            out.append(f"| q{q} | " + " | ".join(pct(c[k], n) for k in kinds) + f" | {d} |")
        out.append("")

    out += ["## Drops (a bar 60% louder than the four before it)", "", "| tier | drops | heads/beat before | after | 1/4 objects before | after |", "|---|---|---|---|---|---|"]
    for t in TIERS:
        d = drop[t]
        if d["before"]:
            out.append(f"| {t} | {len(d['before'])} | {statistics.mean(d['before']):.2f} | {statistics.mean(d['after']):.2f} | "
                       f"{statistics.mean(d['stream_before']):.1f} | {statistics.mean(d['stream_after']):.1f} |")

    out += ["", "## Stops (a beat of near silence after sound)", "", "| tier | stops | last object a slider | slider runs into the quiet | object inside the quiet beat |", "|---|---|---|---|---|"]
    for t in TIERS:
        s = stops[t]
        out.append(f"| {t} | {s['stops']} | {pct(s['last object a slider'], s['stops'])} | {pct(s['slider runs into the quiet'], s['stops'])} | {pct(s['object inside the quiet beat'], s['stops'])} |")

    out += ["", "## Slider or circle (objects outside streams and bursts)", ""]
    for t in TIERS:
        out += [f"**{t}**", "", "| context | objects | slider share |", "|---|---|---|"]
        for key in sorted(slider_by[t]):
            c = slider_by[t][key]
            out.append(f"| {key} | {sum(c.values())} | {pct(c['slider'], sum(c.values()))} |")
        f = slider_fill[t]
        out += ["", "Where a slider ends relative to the next head: " + ", ".join(f"{k} {pct(v, sum(f.values()))}" for k, v in f.most_common()), ""]

    out += ["## What the heads follow (HPSS onset at the head in the song's top quarter)", "", "| tier | drums | melody/voice | both | neither |", "|---|---|---|---|---|"]
    for t in TIERS:
        c = head_source[t]
        n = sum(c.values())
        out.append(f"| {t} | {pct(c['drums'], n)} | {pct(c['melody/voice'], n)} | {pct(c['both'], n)} | {pct(c['neither (rhythm continues)'], n)} |")

    with open(args.report, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out) + "\n")
    print("\n".join(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
