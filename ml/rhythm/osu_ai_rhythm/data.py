"""Training examples: human head positions on our analysis grid.

Human maps come from the dataset built by tools/dataset-builder; the audio analysis of each song
must exist (generated folders keep audio.mp3 and analysis.json under <analysis-root>/<sha12>/).
Only single-tempo maps whose BPM matches the analysis are used, so every human head can be put on
our 1/4 grid; heads more than 25 ms from a grid position are counted and skipped.
"""

from __future__ import annotations

import hashlib
import json
import os
from collections import Counter
from dataclasses import dataclass, field

import numpy as np

from .features import features

# Human red lines sit ~15 ms before the acoustic attack (osu!lazer's Windows offset); our grid is
# acoustic. See docs/adr ADR-025.
CLIENT_CONVENTION_US = -15_000
MATCH_US = 25_000

# A rights grant must allow training for its maps to be used here; "evaluation" grants never do.
TRAINING_SCOPES = {"training", "distribution"}


@dataclass
class MapExample:
    map_sha: str
    song: str
    split: str
    stars: float
    indices: np.ndarray
    x: np.ndarray
    y: np.ndarray
    off_grid: int


def _load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


@dataclass
class Lineage:
    """What a training run consumed, so a model and its report point at one dataset snapshot."""

    manifests: dict[str, str]
    rights: list[dict]
    skipped: Counter = field(default_factory=Counter)
    analyses: dict[str, str] = field(default_factory=dict)


def analysis_path(analysis_root: str, audio_sha: str) -> str | None:
    """analysis.json for a song folder named by the first 12 hex digits of its audio hash: a project
    (project.json names the analysis in use) or the older flat layout."""
    folder = os.path.join(analysis_root, audio_sha[:12])
    manifest = os.path.join(folder, "project.json")
    if os.path.exists(manifest):
        ref = _load(manifest).get("analysis")
        return os.path.join(folder, ref["relativePath"], "analysis.json") if ref else None
    flat = os.path.join(folder, "analysis.json")
    return flat if os.path.exists(flat) else None


def _sha256(path: str) -> str:
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def examples(dataset: str, analysis_root: str, lineage: Lineage | None = None) -> list[MapExample]:
    manifests = os.path.join(dataset, "manifests")
    maps = {}
    for line in open(os.path.join(manifests, "maps.jsonl"), encoding="utf-8"):
        m = json.loads(line)
        maps[m["mapSha256"]] = m
    split_of = {m: f["split"] for f in _load(os.path.join(manifests, "split.json"))["families"] for m in f["mapSha256"]}
    grants = {}
    for line in open(os.path.join(manifests, "rights.jsonl"), encoding="utf-8"):
        if line.strip():
            g = json.loads(line)
            grants[g["id"]] = g

    if lineage is not None:
        lineage.manifests = {name: _sha256(os.path.join(manifests, name)) for name in ("maps.jsonl", "split.json", "rights.jsonl", "packages.jsonl")
                             if os.path.exists(os.path.join(manifests, name))}

    used_grants = set()
    out = []

    def skip(reason):
        if lineage is not None:
            lineage.skipped[reason] += 1

    for m in maps.values():
        if m["status"] != "accepted":
            skip("not accepted")
            continue
        grant = grants.get(m.get("rightsRef"))
        if grant is None or grant.get("scope") not in TRAINING_SCOPES:
            skip("rights do not allow training")
            continue
        path = analysis_path(analysis_root, m["audioSha256"])
        if path is None:
            skip("no audio analysis")
            continue
        ir = _load(os.path.join(dataset, m["irRef"]))
        segs = ir["timing"]["segments"]
        if max(s["beatLengthUs"] for s in segs) / min(s["beatLengthUs"] for s in segs) > 1.0001:
            skip("several tempi")
            continue

        analysis = _load(path)
        beat = analysis["timing"]["beatLengthUs"]
        if abs(segs[0]["beatLengthUs"] / beat - 1) > 0.001:
            skip("tempo differs from analysis (e.g. doubled)")
            continue

        used_grants.add(grant["id"])
        if lineage is not None:
            lineage.analyses[m["audioSha256"][:12]] = _sha256(path)

        indices, x = features(analysis, m["starRating"])
        offset = analysis["timing"]["offsetUs"] + CLIENT_CONVENTION_US
        y = np.zeros(len(indices), dtype=np.int8)
        position = {int(i): k for k, i in enumerate(indices)}
        off_grid = 0
        times = [o["timeUs"] for o in ir["objects"] if o["kind"] != "spinner"]

        for t in times:
            q = round((t - offset) / (beat / 4))
            if abs(t - offset - q * beat / 4) > MATCH_US or q not in position:
                off_grid += 1
                continue
            y[position[q]] = 1

        # Only the mapped span: before the first and after the last object nothing is mapped on purpose.
        first = round((times[0] - offset) / (beat / 4))
        last = round((times[-1] - offset) / (beat / 4))
        keep = (indices >= first) & (indices <= last)

        out.append(MapExample(m["mapSha256"], f"{m['artist']} - {m['title']}", split_of.get(m["mapSha256"], "unknown"),
                              m["starRating"], indices[keep], x[keep], y[keep], off_grid))

    if lineage is not None:
        lineage.rights = [{"id": g["id"], "scope": g["scope"]} for g in grants.values() if g["id"] in used_grants]
    return out
