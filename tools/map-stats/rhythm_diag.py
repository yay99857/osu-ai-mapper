"""Where generated rhythm disagrees with human rhythm.

    uv run --project engines/audio-analysis python tools/map-stats/rhythm_diag.py <dataset> <generated-root> [--split train] [--stars 4 6.5]

For every song with a generated map and human difficulties in the star range, each head is matched
one to one within ±20 ms. Unmatched generated heads are false positives (sounds we click that this
mapper did not), unmatched human heads are misses. Both are broken down by metrical position
(downbeat, beat, half beat, quarter) and by intensity of the generated phrase, so the gap can be
traced to a rule. Human-vs-human figures are given as the ceiling to compare against.
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
from collections import Counter

import project_paths

TOL_US = 20_000


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def heads(ir):
    return [o["timeUs"] for o in ir["objects"] if o["kind"] != "spinner"]


def match(a, b):
    """Greedy one-to-one matching of sorted times; returns matched flags for a and b."""
    ma, mb = [False] * len(a), [False] * len(b)
    i = j = 0
    while i < len(a) and j < len(b):
        if abs(a[i] - b[j]) <= TOL_US:
            ma[i] = mb[j] = True
            i += 1
            j += 1
        elif a[i] < b[j]:
            i += 1
        else:
            j += 1
    return ma, mb


def position_class(t, offset, beat):
    q = round((t - offset) / (beat / 4))
    if abs((t - offset) - q * beat / 4) > TOL_US:
        return "off-grid"
    q %= 16
    return "downbeat" if q == 0 else "beat" if q % 4 == 0 else "half" if q % 2 == 0 else "quarter"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dataset")
    ap.add_argument("generated")
    ap.add_argument("--split")
    ap.add_argument("--stars", nargs=2, type=float, default=(4.0, 6.5))
    args = ap.parse_args()

    maps = {}
    for line in open(os.path.join(args.dataset, "manifests", "maps.jsonl"), encoding="utf-8"):
        m = json.loads(line)
        maps[m["mapSha256"]] = m
    split_of = {m: f["split"] for f in load(os.path.join(args.dataset, "manifests", "split.json"))["families"] for m in f["mapSha256"]}

    fp, miss, gen_total, hum_total = Counter(), Counter(), Counter(), Counter()
    f1s, f1h, prec, rec = [], [], [], []

    for folder in sorted(os.listdir(args.generated)):
        path = project_paths.beatmap_ir(os.path.join(args.generated, folder))
        if path is None:
            continue
        humans = [m for m in maps.values() if m["status"] == "accepted" and m["audioSha256"].startswith(folder)
                  and args.stars[0] <= (m["starRating"] or 0) <= args.stars[1]]
        if not humans or (args.split and split_of.get(humans[0]["mapSha256"]) != args.split):
            continue

        gen = load(path)
        seg = gen["timing"]["segments"][0]
        offset, beat = seg["startTimeUs"], seg["beatLengthUs"]
        g = heads(gen)
        human_heads = [heads(load(os.path.join(args.dataset, m["irRef"]))) for m in humans]

        for h in human_heads:
            mg, mh = match(g, h)
            p, r = sum(mg) / len(g), sum(mh) / len(h)
            prec.append(p)
            rec.append(r)
            f1s.append(0 if p + r == 0 else 2 * p * r / (p + r))
            for t, ok in zip(g, mg):
                c = position_class(t, offset, beat)
                gen_total[c] += 1
                if not ok:
                    fp[c] += 1
            for t, ok in zip(h, mh):
                c = position_class(t, offset, beat)
                hum_total[c] += 1
                if not ok:
                    miss[c] += 1

        for i, a in enumerate(human_heads):
            for b in human_heads[i + 1:]:
                ma, mb = match(a, b)
                p, r = sum(ma) / len(a), sum(mb) / len(b)
                f1h.append(0 if p + r == 0 else 2 * p * r / (p + r))

    print(f"pairs: {len(f1s)}  generated-vs-human F1 {statistics.median(f1s):.2f} (precision {statistics.median(prec):.2f}, recall {statistics.median(rec):.2f})"
          f"   human-vs-human F1 {statistics.median(f1h):.2f}\n")
    print(f"{'position':10s} {'human heads':>12s} {'missed':>8s} {'gen heads':>10s} {'unmatched':>10s}   (shares within each row)")
    for c in ("downbeat", "beat", "half", "quarter", "off-grid"):
        ht, gt = hum_total[c], gen_total[c]
        print(f"{c:10s} {ht:12d} {miss[c] / max(1, ht):8.0%} {gt:10d} {fp[c] / max(1, gt):10.0%}")
    total_h, total_g = sum(hum_total.values()), sum(gen_total.values())
    print(f"\nshare of heads by position   human: " + " ".join(f"{c}:{hum_total[c] / total_h:.0%}" for c in ("downbeat", "beat", "half", "quarter", "off-grid")))
    print(f"                         generated: " + " ".join(f"{c}:{gen_total[c] / total_g:.0%}" for c in ("downbeat", "beat", "half", "quarter", "off-grid")))


if __name__ == "__main__":
    main()
