"""Compare generated maps with human maps of the same songs.

    uv run --project engines/audio-analysis python tools/map-stats/map_stats.py <dataset> <generated-root> [--split train]

<generated-root> holds one folder per song named after the first 12 hex digits of the audio SHA-256,
each with the beatmap-ir.json written by `osu-ai generate`. Human maps come from the dataset; only
difficulties between 4 and 6.5 stars are used so the comparison is at similar difficulty.

Every statistic is reported for humans and for the generator, with the human spread, so a gap can be
read against how much mappers already differ among themselves.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import statistics
from collections import Counter

import project_paths

STAR_RANGE = (4.0, 6.5)


def load(path: str) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def slider_end(o: dict, sm: float, beat_us: float) -> tuple[float, tuple[float, float]]:
    """End time and end position of an object (slider ends use the authored path end as an approximation)."""
    if o["kind"] == "slider":
        length = o.get("expectedDistancePx") or 0
        duration = o["spanCount"] * length / (sm * 100 * o.get("svMultiplier", 1)) * beat_us
        if "computed" in o:
            end = (o["computed"]["endPosition"]["x"], o["computed"]["endPosition"]["y"])
            duration = o["computed"]["durationUs"]
        else:
            last = o["path"]["controlPoints"][-1]
            end = (o["position"]["x"] + last["x"], o["position"]["y"] + last["y"])
        if o["spanCount"] % 2 == 0:
            end = (o["position"]["x"], o["position"]["y"])
        return o["timeUs"] + duration, end
    if o["kind"] == "spinner":
        return o["endTimeUs"], (o["position"]["x"], o["position"]["y"])
    return o["timeUs"], (o["position"]["x"], o["position"]["y"])


def bucket(beats: float) -> str:
    for label, value in (("1/4", 0.25), ("1/3", 1 / 3), ("1/2", 0.5), ("3/4", 0.75), ("1", 1.0), ("3/2", 1.5), ("2", 2.0)):
        if abs(beats - value) < 0.04:
            return label
    return ">2" if beats > 2 else "other"


def stats(ir: dict) -> dict:
    seg = ir["timing"]["segments"][0]
    beat = seg["beatLengthUs"]
    sm = ir["settings"]["sliderMultiplier"]
    objs = [o for o in ir["objects"] if o["kind"] != "spinner"]
    s: dict = {"objects": len(objs)}

    first, last = objs[0]["timeUs"], objs[-1]["timeUs"]
    s["objects_per_beat"] = len(objs) / max(1, (last - first) / beat)
    s["slider_share"] = sum(o["kind"] == "slider" for o in objs) / len(objs)

    sliders = [o for o in objs if o["kind"] == "slider"]
    durations = [(slider_end(o, sm, beat)[0] - o["timeUs"]) / beat for o in sliders]
    s["slider_beats"] = Counter(bucket(d) for d in durations)
    s["slider_repeat_share"] = sum(o["spanCount"] > 1 for o in sliders) / max(1, len(sliders))
    s["slider_curved_share"] = sum(len(o["path"]["controlPoints"]) > 2 or o["path"]["controlPoints"][0].get("segmentType") not in (None, "linear") for o in sliders) / max(1, len(sliders))
    s["slider_px"] = [o.get("expectedDistancePx") or 0 for o in sliders]

    gaps, distances, by_gap, turns = Counter(), [], {}, []
    heads = []
    run, runs = 0, []
    prev_end_t = prev_end_p = None
    prev_vec = None

    for o in objs:
        head = (o["position"]["x"], o["position"]["y"])
        heads.append(o["timeUs"])
        if prev_end_t is not None:
            g = (o["timeUs"] - prev_end_t) / beat
            b = bucket(g)
            gaps[b] += 1
            d = math.dist(prev_end_p, head)
            by_gap.setdefault(b, []).append(d)
            vec = (head[0] - prev_end_p[0], head[1] - prev_end_p[1])
            if prev_vec is not None and d > 20 and math.hypot(*prev_vec) > 20:
                a1, a2 = math.atan2(*prev_vec[::-1]), math.atan2(*vec[::-1])
                turns.append(abs((math.degrees(a2 - a1) + 180) % 360 - 180))
            prev_vec = vec
            if b == "1/2" and o["kind"] == "circle":
                run += 1
            else:
                if run:
                    runs.append(run)
                run = 0
        end_t, end_p = slider_end(o, sm, beat)
        prev_end_t, prev_end_p = end_t, end_p
    if run:
        runs.append(run)

    s["gaps"] = gaps
    s["distance_by_gap"] = {k: statistics.median(v) for k, v in by_gap.items() if len(v) >= 5}
    s["turns"] = turns
    s["half_runs"] = runs
    combos, n = [], 0
    for o in objs:
        if o["newCombo"] and n:
            combos.append(n)
            n = 0
        n += 1
    combos.append(n)
    s["combo_median"] = statistics.median(combos)
    s["heads"] = heads
    s["stars_px_per_beat_1_2"] = s["distance_by_gap"].get("1/2")
    return s


def rhythm_f1(a: list[float], b: list[float], tol_us: float = 20_000) -> float:
    """One-to-one matching of head times within ±20 ms (greedy on sorted lists)."""
    i = j = matched = 0
    while i < len(a) and j < len(b):
        if abs(a[i] - b[j]) <= tol_us:
            matched += 1
            i += 1
            j += 1
        elif a[i] < b[j]:
            i += 1
        else:
            j += 1
    if not a or not b:
        return 0.0
    p, r = matched / len(a), matched / len(b)
    return 0.0 if p + r == 0 else 2 * p * r / (p + r)


def turn_histogram(turns: list[float]) -> str:
    edges = [0, 30, 60, 90, 120, 150, 181]
    counts = [sum(lo <= t < hi for t in turns) for lo, hi in zip(edges, edges[1:])]
    total = max(1, sum(counts))
    return " ".join(f"{lo}-{hi - 1 if hi == 181 else hi}deg:{c / total:.0%}" for lo, hi, c in zip(edges, edges[1:], counts))


def share(counter: Counter, keys: list[str]) -> str:
    total = max(1, sum(counter.values()))
    return " ".join(f"{k}:{counter.get(k, 0) / total:.0%}" for k in keys)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("dataset")
    parser.add_argument("generated")
    parser.add_argument("--split", default=None, help="only songs in this split (train, validation, test)")
    args = parser.parse_args()

    maps = {}
    with open(os.path.join(args.dataset, "manifests", "maps.jsonl"), encoding="utf-8") as f:
        for line in f:
            m = json.loads(line)
            maps[m["mapSha256"]] = m
    split_of = {}
    split_path = os.path.join(args.dataset, "manifests", "split.json")
    if os.path.exists(split_path):
        for fam in load(split_path)["families"]:
            for m in fam["mapSha256"]:
                split_of[m] = fam["split"]

    rows = []
    for folder in sorted(os.listdir(args.generated)):
        gen_path = project_paths.beatmap_ir(os.path.join(args.generated, folder))
        if gen_path is None:
            continue
        humans = [m for m in maps.values() if m["status"] == "accepted" and m["audioSha256"].startswith(folder)
                  and STAR_RANGE[0] <= (m["starRating"] or 0) <= STAR_RANGE[1]]
        if not humans or (args.split and split_of.get(humans[0]["mapSha256"]) != args.split):
            continue
        gen = stats(load(gen_path))
        hum = [(m, stats(load(os.path.join(args.dataset, m["irRef"])))) for m in humans]
        f1_gen = [rhythm_f1(gen["heads"], h["heads"]) for _, h in hum]
        f1_hum = [rhythm_f1(a["heads"], b["heads"]) for i, (_, a) in enumerate(hum) for (_, b) in hum[i + 1:]]
        rows.append((humans[0]["title"], gen, hum, f1_gen, f1_hum))

    if not rows:
        print("no songs matched")
        return

    def med(values):
        values = [v for v in values if v is not None]
        return statistics.median(values) if values else float("nan")

    print(f"songs: {len(rows)}; human difficulties in {STAR_RANGE[0]}-{STAR_RANGE[1]} stars: {sum(len(r[2]) for r in rows)}\n")
    print(f"{'metric':34s} {'human median (min-max)':>28s} {'generated':>12s}")

    def line(name, getter, fmt="{:.2f}"):
        h = [getter(s) for _, hs, _, _ in [(r[0], [x[1] for x in r[2]], None, None) for r in rows] for s in hs]
        g = [getter(r[1]) for r in rows]
        h = [v for v in h if v is not None]
        if not h:
            return
        print(f"{name:34s} {fmt.format(med(h)):>10s} ({fmt.format(min(h))}-{fmt.format(max(h))}){'':4s} {fmt.format(med(g)):>12s}")

    line("objects per beat", lambda s: s["objects_per_beat"])
    line("slider share", lambda s: s["slider_share"], "{:.0%}")
    line("sliders with repeats", lambda s: s["slider_repeat_share"], "{:.0%}")
    line("curved sliders", lambda s: s["slider_curved_share"], "{:.0%}")
    line("slider length px (median)", lambda s: med(s["slider_px"]) if s["slider_px"] else None, "{:.0f}")
    line("combo length (median)", lambda s: s["combo_median"], "{:.0f}")
    line("longest 1/2 circle run", lambda s: max(s["half_runs"], default=0), "{:.0f}")
    for gap in ("1/2", "1", "3/2", "2"):
        line(f"distance after {gap} gap (px)", lambda s, gap=gap: s["distance_by_gap"].get(gap), "{:.0f}")
    print()
    print(f"{'rhythm F1 vs human maps (±20 ms)':34s} human-vs-human {med([x for r in rows for x in r[4]]):.2f}   generated-vs-human {med([x for r in rows for x in r[3]]):.2f}")
    print()

    def pooled(getter, combine):
        hum = combine(getter(s) for r in rows for _, s in r[2])
        gen = combine(getter(r[1]) for r in rows)
        return hum, gen

    gh, gg = pooled(lambda s: s["gaps"], lambda xs: sum(xs, Counter()))
    keys = ["1/4", "1/3", "1/2", "3/4", "1", "3/2", "2", ">2"]
    print("gap to next object   human:", share(gh, keys))
    print("                 generated:", share(gg, keys))
    sh, sg = pooled(lambda s: s["slider_beats"], lambda xs: sum(xs, Counter()))
    keys = ["1/4", "1/2", "3/4", "1", "3/2", "2", ">2", "other"]
    print("slider duration      human:", share(sh, keys))
    print("                 generated:", share(sg, keys))
    th, tg = pooled(lambda s: s["turns"], lambda xs: [t for x in xs for t in x])
    print("turn angle           human:", turn_histogram(th))
    print("                 generated:", turn_histogram(tg))


if __name__ == "__main__":
    main()
