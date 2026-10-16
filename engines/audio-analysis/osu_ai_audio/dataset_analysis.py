"""Analyse every song of a dataset on its human timing, for studying and training rhythm models.

    osu-ai-analyze-dataset <dataset> <out-root> [--workers N]

Each distinct audio file is transcoded and analysed like a product song, except that the grid is
the human red line instead of a detected one: BPM from the map and phase from its first timing
point, moved by the client convention (human lines sit ~15 ms before the acoustic attack, ADR-025)
and folded into the first bar. Then every human object lands on our 1/4 grid, so training labels do
not inherit our timing errors. Songs whose maps change tempo are skipped.

Writes <out-root>/<first 12 hex of the audio hash>/analysis.json, the flat layout ml/rhythm reads.
Existing analyses of the current schema are kept, so the command can be resumed.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
from collections import Counter
from concurrent.futures import ProcessPoolExecutor, as_completed

from .analysis import SCHEMA_VERSION, ManualTiming, analyze
from .transcode import to_mp3

CLIENT_CONVENTION_US = -15_000


def human_timing(dataset: str, maps: list[dict]) -> tuple[float, float] | None:
    """(bpm, offset ms) shared by most single-tempo maps of one song, or None."""
    votes = Counter()
    for m in maps:
        with open(os.path.join(dataset, m["irRef"]), encoding="utf-8") as f:
            segments = json.load(f)["timing"]["segments"]
        lengths = [s["beatLengthUs"] for s in segments]
        if max(lengths) / min(lengths) > 1.0001:
            continue
        beat = segments[0]["beatLengthUs"]
        offset = (segments[0]["startTimeUs"] - CLIENT_CONVENTION_US) % (4 * beat)
        votes[(round(beat, 1), round(offset / 1000, 1))] += 1
    if not votes:
        return None
    (beat, offset_ms), _ = votes.most_common(1)[0]
    return 60e6 / beat, offset_ms


def analyse_one(audio: str, out_dir: str, bpm: float, offset_ms: float) -> str:
    os.makedirs(out_dir, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        mp3 = os.path.join(tmp, "audio.mp3")
        to_mp3(audio, mp3)
        result = analyze(mp3, ManualTiming(bpm, offset_ms))
    target = os.path.join(out_dir, "analysis.json")
    with open(target + ".tmp", "w", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"))
    os.replace(target + ".tmp", target)
    return f"{bpm:.2f} BPM, {result['audio']['durationUs'] / 1e6:.0f} s"


def current(path: str) -> bool:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f).get("schemaVersion") == SCHEMA_VERSION
    except (OSError, ValueError):
        return False


def main() -> int:
    ap = argparse.ArgumentParser(prog="osu-ai-analyze-dataset", description=__doc__.splitlines()[0])
    ap.add_argument("dataset")
    ap.add_argument("out_root")
    ap.add_argument("--workers", type=int, default=max(1, (os.cpu_count() or 2) // 4))
    args = ap.parse_args()

    by_audio: dict[str, list[dict]] = {}
    with open(os.path.join(args.dataset, "manifests", "maps.jsonl"), encoding="utf-8") as f:
        for line in f:
            m = json.loads(line)
            if m["status"] == "accepted":
                by_audio.setdefault(m["audioSha256"], []).append(m)

    jobs, skipped = [], Counter()
    for sha, maps in by_audio.items():
        out_dir = os.path.join(args.out_root, sha[:12])
        if current(os.path.join(out_dir, "analysis.json")):
            skipped["already analysed"] += 1
            continue
        timing = human_timing(args.dataset, maps)
        if timing is None:
            skipped["tempo changes"] += 1
            continue
        jobs.append((os.path.join(args.dataset, maps[0]["audioRef"]), out_dir, *timing, maps[0]["title"]))

    print(f"{len(jobs)} songs to analyse with {args.workers} workers; skipped {dict(skipped)}", file=sys.stderr, flush=True)
    failed = 0
    with ProcessPoolExecutor(args.workers) as pool:
        futures = {pool.submit(analyse_one, *job[:4]): job for job in jobs}
        for done, future in enumerate(as_completed(futures), 1):
            title = futures[future][4][:40]
            try:
                print(f"[{done}/{len(jobs)}] {title}: {future.result()}", file=sys.stderr, flush=True)
            except Exception as e:  # one bad file must not stop a long batch
                failed += 1
                print(f"[{done}/{len(jobs)}] {title}: FAILED {type(e).__name__}: {e}", file=sys.stderr, flush=True)
    print(f"done, {failed} failed", file=sys.stderr)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
