"""Timing benchmark (blueprint experiment E02, gate G1): detected timing against human red lines.

Reads a dataset built by tools/dataset-builder, takes each distinct audio file once, and compares
the analyzer's constant timing with the timing human mappers chose for it. The audio goes through
the same path as generation (transcode to MP3, then analyse) so the measurement covers the product.

Only single-tempo maps are in the G1 domain; the others are counted and listed, not scored.
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import sys
import tempfile
from collections import Counter
from dataclasses import dataclass

from .analysis import analyze
from .transcode import to_mp3

SINGLE_TEMPO_SPREAD_PERCENT = 0.01

# osu!lazer adds WINDOWS_BASE_AUDIO_OFFSET = 15 ms (FramedBeatmapClock) so that maps timed in stable
# play in sync; human red lines therefore sit about that much before the acoustic attack. Generated
# maps follow the same convention. Public constant, so the default does not depend on any dataset.
CLIENT_CONVENTION_MS = -15.0
METRICAL_RATIOS = {"1/2": 0.5, "2": 2.0, "2/3": 2 / 3, "3/2": 1.5, "3/4": 0.75, "4/3": 4 / 3}


@dataclass
class Result:
    audio: str
    title: str
    human_bpm: float
    detected_bpm: float
    tempo: str  # exact | 1/2 | 2 | 2/3 | ... | other
    phase_error_ms: float | None  # signed, detected minus human, wrapped to ±half a beat; exact tempo only
    mid_error_ms: float | None  # same, measured at the middle of the mapped part (captures drift)
    downbeat_ok: bool | None
    confidence: float
    split: str = "unknown"


def latest_maps(dataset: str) -> list[dict]:
    latest: dict[str, dict] = {}
    with open(os.path.join(dataset, "manifests", "maps.jsonl"), encoding="utf-8") as f:
        for line in f:
            if line.strip():
                record = json.loads(line)
                latest[record["mapSha256"]] = record
    return [m for m in latest.values() if m["status"] == "accepted"]


def tempo_spread_percent(dataset: str, m: dict) -> float:
    """(max - min) / min of the red-line tempos, read from the IR rather than from the manifest."""
    with open(os.path.join(dataset, m["irRef"]), encoding="utf-8") as f:
        lengths = [s["beatLengthUs"] for s in json.load(f)["timing"]["segments"]]
    return (max(lengths) / min(lengths) - 1) * 100


def human_timing(dataset: str, maps: list[dict]) -> tuple[float, float, int, int]:
    """Most common (beat length, first red line) among the difficulties on one audio file."""
    votes: Counter = Counter()
    span: tuple[int, int] = (0, 0)
    for m in maps:
        with open(os.path.join(dataset, m["irRef"]), encoding="utf-8") as f:
            ir = json.load(f)
        first = ir["timing"]["segments"][0]
        votes[(round(first["beatLengthUs"], 3), first["startTimeUs"])] += 1
        span = (m["firstObjectUs"], m["lastObjectUs"])
    (beat_us, offset_us), _ = votes.most_common(1)[0]
    return beat_us, offset_us, span[0], span[1]


def wrap(value: float, period: float) -> float:
    v = value % period
    return v - period if v > period / 2 else v


def classify(detected: float, human: float) -> str:
    ratio = detected / human
    if abs(ratio - 1) < 0.001:
        return "exact"
    for name, r in METRICAL_RATIOS.items():
        if abs(ratio / r - 1) < 0.002:
            return name
    return "other"


def evaluate(dataset: str, audio_sha: str, maps: list[dict], workdir: str, convention_ms: float = 0.0) -> Result:
    beat_us, offset_us, first_us, last_us = human_timing(dataset, maps)
    mp3 = os.path.join(workdir, f"{audio_sha[:16]}.mp3")
    to_mp3(os.path.join(dataset, maps[0]["audioRef"]), mp3)
    timing = analyze(mp3)["timing"]

    human_bpm = 60e6 / beat_us
    tempo = classify(timing["bpm"], human_bpm)
    phase = mid = None
    downbeat = None

    if tempo == "exact":
        det_beat = timing["beatLengthUs"]
        det_offset = timing["offsetUs"] + convention_ms * 1000
        # Compare the detected beat nearest to a reference time with the human grid there.
        def error_at(t_us: float) -> float:
            n = round((t_us - det_offset) / det_beat)
            detected_time = det_offset + n * det_beat
            return wrap(detected_time - offset_us, beat_us) / 1000

        phase = error_at(first_us)
        mid = error_at((first_us + last_us) / 2)
        bar_error = wrap(det_offset - offset_us, 4 * beat_us) / 1000
        downbeat = abs(bar_error) < 25

    return Result(
        audio=audio_sha,
        title=f"{maps[0].get('artist')} - {maps[0].get('title')}",
        human_bpm=human_bpm,
        detected_bpm=timing["bpm"],
        tempo=tempo,
        phase_error_ms=phase,
        mid_error_ms=mid,
        downbeat_ok=downbeat,
        confidence=timing["confidence"],
    )


def percentile(values: list[float], p: float) -> float:
    ordered = sorted(values)
    k = (len(ordered) - 1) * p
    lo, hi = int(k), min(int(k) + 1, len(ordered) - 1)
    return ordered[lo] + (ordered[hi] - ordered[lo]) * (k - lo)


def summarise(results: list[Result], out_of_domain: list[str], convention_ms: float = 0.0) -> str:
    lines = ["# Timing benchmark (E02)", "", f"Client convention applied to detected offsets: {convention_ms:+.1f} ms", ""]
    lines.append(f"Audio files scored: {len(results)}; outside the constant-tempo domain (not scored): {len(out_of_domain)}")
    lines.append("")

    if results:
        tempos = Counter(r.tempo for r in results)
        lines.append("Tempo: " + ", ".join(f"{k} {v}" for k, v in tempos.most_common()))
        exact = [r for r in results if r.tempo == "exact"]
        if exact:
            errors = [abs(r.mid_error_ms) for r in exact]
            lines.append(f"Phase |error| at mid-song, exact tempo (n={len(exact)}): median {statistics.median(errors):.1f} ms, "
                         f"P95 {percentile(errors, 0.95):.1f} ms (G1 target: median ≤ 10, P95 ≤ 25)")
            signed = [r.mid_error_ms for r in exact]
            lines.append(f"Signed median {statistics.median(signed):+.1f} ms (positive: detected later than human)")
            lines.append(f"Downbeat matches the human bar: {sum(r.downbeat_ok for r in exact)}/{len(exact)}")
            for split in ("train", "validation", "test"):
                part = [r.mid_error_ms for r in exact if r.split == split]
                if part:
                    lines.append(f"- {split}: n={len(part)}, signed median {statistics.median(part):+.1f} ms, |error| max {max(abs(e) for e in part):.1f} ms")
        lines.append("")
        lines.append("| Song | Split | Human BPM | Detected | Tempo | Phase (first / mid) ms | Downbeat | Confidence |")
        lines.append("|---|---|---:|---:|---|---:|---|---:|")
        for r in sorted(results, key=lambda r: r.title):
            phase = "" if r.phase_error_ms is None else f"{r.phase_error_ms:+.1f} / {r.mid_error_ms:+.1f}"
            down = "" if r.downbeat_ok is None else ("yes" if r.downbeat_ok else "no")
            lines.append(f"| {r.title} | {r.split} | {r.human_bpm:.3f} | {r.detected_bpm:.3f} | {r.tempo} | {phase} | {down} | {r.confidence:.2f} |")

    if out_of_domain:
        lines += ["", "Not scored (several tempi):", ""] + [f"- {t}" for t in sorted(out_of_domain)]

    lines += ["", "Human timing is one interpretation, and mappers use offsets that may differ from the acoustic onset by a few ms.",
              "Small sets say little: treat this as a smoke check until the corpus has tens of families."]
    return "\n".join(lines) + "\n"


def split_of_maps(dataset: str) -> dict[str, str]:
    path = os.path.join(dataset, "manifests", "split.json")
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        families = json.load(f)["families"]
    return {m: fam["split"] for fam in families for m in fam["mapSha256"]}


def run(dataset: str, convention_ms: float = 0.0) -> tuple[list[Result], list[str]]:
    splits = split_of_maps(dataset)
    by_audio: dict[str, list[dict]] = {}
    for m in latest_maps(dataset):
        by_audio.setdefault(m["audioSha256"], []).append(m)

    results, out_of_domain = [], []
    with tempfile.TemporaryDirectory() as work:
        for audio, maps in sorted(by_audio.items()):
            title = f"{maps[0].get('artist')} - {maps[0].get('title')}"
            if any(tempo_spread_percent(dataset, m) > SINGLE_TEMPO_SPREAD_PERCENT for m in maps):
                out_of_domain.append(title)
                continue
            result = evaluate(dataset, audio, maps, work, convention_ms)
            result.split = splits.get(maps[0]["mapSha256"], "unknown")
            results.append(result)
            print(f"scored {title}", file=sys.stderr)
    return results, out_of_domain


def main() -> int:
    parser = argparse.ArgumentParser(prog="osu-ai-bench-timing", description=__doc__.splitlines()[0])
    parser.add_argument("dataset")
    parser.add_argument("--out", help="also write the markdown report here")
    parser.add_argument("--convention-ms", type=float, default=CLIENT_CONVENTION_MS,
                        help="shift applied to detected offsets, as generation does (default: lazer's 15 ms, i.e. -15)")
    args = parser.parse_args()

    results, out_of_domain = run(args.dataset, args.convention_ms)
    report = summarise(results, out_of_domain, args.convention_ms)
    print(report)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
