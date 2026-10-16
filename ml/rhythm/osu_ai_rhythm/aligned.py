"""Human maps aligned to our analysis grid, object by object, with what each object is.

Shared by the study (study.py) and the B3 trainer. Applies the same admission rules as data.py:
accepted maps, rights that allow training, an analysis whose tempo is the map's, one tempo only.
"""

from __future__ import annotations

import json
import os
from collections import Counter
from dataclasses import dataclass, field

import numpy as np

from .data import CLIENT_CONVENTION_US, MATCH_US, TRAINING_SCOPES, Lineage, _load, _sha256, analysis_path


@dataclass
class AlignedObject:
    q: int  # 1/4 grid index of the head
    kind: str  # "circle" or "slider"
    length: int  # slider duration in 1/4 grid units (all spans); 0 for circles
    spans: int
    x: float
    y: float
    end_x: float
    end_y: float
    new_combo: bool


@dataclass
class AlignedMap:
    map_sha: str
    song: str
    audio_sha: str
    split: str
    stars: float
    cs: float
    analysis: dict
    objects: list[AlignedObject]
    off_grid: int
    spinners: int
    # Grid ranges [start, end] covered by spinners: not mapped with clicks, so neither "a head" nor
    # "no head" there teaches anything about rhythm.
    spinner_spans: list[tuple[int, int]] = field(default_factory=list)

    @property
    def radius(self) -> float:
        return 54.4 - 4.48 * self.cs


def aligned_maps(dataset: str, analysis_root: str, lineage: Lineage | None = None, splits: set[str] | None = None) -> list[AlignedMap]:
    manifests = os.path.join(dataset, "manifests")
    with open(os.path.join(manifests, "maps.jsonl"), encoding="utf-8") as f:
        # Manifests are append-only: the latest admission decision wins.
        maps = {m["mapSha256"]: m for line in f if line.strip() for m in [json.loads(line)]}
    split_of = {m: f["split"] for f in _load(os.path.join(manifests, "split.json"))["families"] for m in f["mapSha256"]}
    grants = {}
    for line in open(os.path.join(manifests, "rights.jsonl"), encoding="utf-8"):
        if line.strip():
            g = json.loads(line)
            grants[g["id"]] = g

    if lineage is not None:
        lineage.manifests = {name: _sha256(os.path.join(manifests, name)) for name in ("maps.jsonl", "split.json", "rights.jsonl", "packages.jsonl")
                             if os.path.exists(os.path.join(manifests, name))}

    skipped = lineage.skipped if lineage is not None else Counter()
    analyses: dict[str, dict] = {}
    used_grants = set()
    out = []
    for m in maps.values():
        split = split_of.get(m["mapSha256"], "unknown")
        if split not in {"train", "validation", "test"}:
            skipped["not assigned to a split"] += 1
            continue
        if splits is not None and split not in splits:
            continue
        if m["status"] != "accepted":
            skipped["not accepted"] += 1
            continue
        grant = grants.get(m.get("rightsRef"))
        if grant is None or grant.get("scope") not in TRAINING_SCOPES:
            skipped["rights do not allow training"] += 1
            continue
        ir = _load(os.path.join(dataset, m["irRef"]))
        segs = ir["timing"]["segments"]
        if not segs or any(s["beatLengthUs"] <= 0 for s in segs):
            skipped["invalid timing"] += 1
            continue
        if max(s["beatLengthUs"] for s in segs) / min(s["beatLengthUs"] for s in segs) > 1.0001:
            skipped["several tempi"] += 1
            continue
        path = analysis_path(analysis_root, m["audioSha256"])
        if path is None:
            skipped["no audio analysis"] += 1
            continue
        if path not in analyses:
            analyses[path] = with_stems(_load(path), path)
        analysis = analyses[path]
        beat = analysis["timing"]["beatLengthUs"]
        if abs(segs[0]["beatLengthUs"] / beat - 1) > 0.001:
            skipped["tempo differs from analysis"] += 1
            continue

        used_grants.add(grant["id"])
        if lineage is not None:
            lineage.analyses[m["audioSha256"][:12]] = _sha256(path)

        offset = analysis["timing"]["offsetUs"] + CLIENT_CONVENTION_US
        step = beat / 4
        available = {p["index"] for p in analysis["grid"] if p["index"] >= 0}
        objects, off_grid, spinners, spans = [], 0, 0, []
        for o in sorted(ir["objects"], key=lambda obj: obj["timeUs"]):
            if o["kind"] == "spinner":
                spinners += 1
                spans.append((int(np.floor((o["timeUs"] - offset) / step)), int(np.ceil((o.get("endTimeUs", o["timeUs"]) - offset) / step))))
                continue
            q = round((o["timeUs"] - offset) / step)
            if abs(o["timeUs"] - offset - q * step) > MATCH_US or q not in available:
                off_grid += 1
                continue
            x, y = o["position"]["x"], o["position"]["y"]
            if o["kind"] == "slider":
                c = o["computed"]
                objects.append(AlignedObject(q, "slider", max(1, round(c["durationUs"] / step)), o["spanCount"], x, y,
                                             c["endPosition"]["x"], c["endPosition"]["y"], o.get("newCombo", False)))
            else:
                objects.append(AlignedObject(q, "circle", 0, 0, x, y, x, y, o.get("newCombo", False)))

        # Two heads on one grid position (rare, usually 1/6 or 1/8 snaps rounded) keep the first.
        seen, unique = set(), []
        for o in objects:
            if o.q not in seen:
                seen.add(o.q)
                unique.append(o)
        if len(unique) < 20:
            skipped["too few objects on the grid"] += 1
            continue
        out.append(AlignedMap(m["mapSha256"], f"{m['artist']} - {m['title']}", m["audioSha256"], split, m["starRating"],
                              ir["settings"]["cs"], analysis, unique, off_grid, spinners, spans))

    if lineage is not None:
        lineage.rights = [{"id": g["id"], "scope": g["scope"]} for g in grants.values() if g["id"] in used_grants]
    return out


def with_stems(analysis: dict, analysis_path: str) -> dict:
    """Attach the Demucs stems (engines/source-separation) written next to an analysis, if any."""
    stems = os.path.join(os.path.dirname(analysis_path), "stems.json")
    if os.path.exists(stems):
        data = _load(stems)
        if data.get("analysisAudioSha256") in (None, analysis.get("audio", {}).get("sha256")):
            analysis = {**analysis, "stems": data["grid"]}
    return analysis


def tier(stars: float) -> str:
    """The same boundaries as the generator's DifficultyTier."""
    if stars < 4.0:
        return "hard"
    if stars < 5.5:
        return "insane"
    return "expert"


def grid_arrays(analysis: dict) -> tuple[np.ndarray, dict[str, np.ndarray]]:
    """Dense arrays over grid index 0..max of every numeric grid field (missing indices are 0)."""
    points = [p for p in analysis["grid"] if p["index"] >= 0]
    if not points:
        return np.zeros(0, dtype=np.int64), {}
    size = max(p["index"] for p in points) + 1
    keys = [k for k in points[0] if k not in ("index", "timeUs")]
    dense = {k: np.zeros(size, dtype=np.float32) for k in keys}
    for p in points:
        for k in keys:
            dense[k][p["index"]] = p[k]
    return np.arange(size), dense
