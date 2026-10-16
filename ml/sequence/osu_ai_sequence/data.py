"""Per-tick sequences of human maps for Pulso-S.

Each map becomes, over its mapped span on the 1/4 grid: the Pulso audio features (with stars and the
map's style as conditioning), and per tick a label (none, circle, slider head, or inside a slider
body), the slider length in ticks, and the distance from the previous object's end in circle radii.
Spinner spans are masked out. Cached as one .npz per map so training reads them fast.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass

import numpy as np

from osu_ai_rhythm.aligned import AlignedMap, aligned_maps
from osu_ai_rhythm.pulso_v2 import audio_matrix, style_of

NONE, CIRCLE, SLIDER, BODY = 0, 1, 2, 3
MAX_LENGTH = 16
FEATURES = "pulso-s-features-1"  # pulso-features-3 layout: HPSS, look-ahead, style


def features(analysis: dict, stars: float, style: float) -> tuple[np.ndarray, np.ndarray]:
    return audio_matrix(analysis, stars, True, style)


def labels(m: AlignedMap, idx: np.ndarray):
    n = len(idx)
    row = {int(q): r for r, q in enumerate(idx)}
    cls = np.zeros(n, dtype=np.int8)
    length = np.zeros(n, dtype=np.int8)
    dist = np.full(n, np.nan, dtype=np.float32)
    mask = np.ones(n, dtype=bool)
    for start, end in m.spinner_spans:
        mask &= ~((idx >= start) & (idx <= end))
    previous = None
    for o in m.objects:
        r = row.get(o.q)
        if r is None:
            previous = o
            continue
        if o.kind == "slider":
            cls[r] = SLIDER
            length[r] = min(o.length, MAX_LENGTH)
            body = slice(r + 1, min(n, r + o.length + 1))  # through the tail's tick
            cls[body] = np.where(cls[body] == NONE, BODY, cls[body])
        else:
            cls[r] = CIRCLE
        if previous is not None:
            dist[r] = np.log1p(np.hypot(o.x - previous.end_x, o.y - previous.end_y) / m.radius)
        previous = o
    return cls, length, dist, mask


@dataclass
class Sequence:
    map_sha: str
    song: str
    split: str
    stars: float
    style: float
    idx: np.ndarray
    x: np.ndarray
    cls: np.ndarray
    length: np.ndarray
    dist: np.ndarray
    mask: np.ndarray


def build(dataset: str, analysis_root: str, cache: str, splits=("train", "validation")) -> list[str]:
    """Writes <cache>/<map>.npz for every usable map not cached yet; returns the cached map hashes."""
    os.makedirs(cache, exist_ok=True)
    done = []
    for m in aligned_maps(dataset, analysis_root, splits=set(splits)):
        if m.off_grid > 0.05 * (len(m.objects) + m.off_grid):
            continue
        path = os.path.join(cache, m.map_sha + ".npz")
        if not os.path.exists(path):
            style = style_of(m.objects)
            idx, x = features(m.analysis, m.stars, style)
            keep = (idx >= m.objects[0].q) & (idx <= m.objects[-1].q)
            idx, x = idx[keep], x[keep]
            cls, length, dist, mask = labels(m, idx)
            np.savez_compressed(path, idx=idx, x=x.astype(np.float16), cls=cls, length=length, dist=dist.astype(np.float16), mask=mask,
                                meta=json.dumps({"song": m.audio_sha, "split": m.split, "stars": m.stars, "style": style, "features": FEATURES}))
        done.append(m.map_sha)
    return done


def load(cache: str, map_sha: str) -> Sequence:
    z = np.load(os.path.join(cache, map_sha + ".npz"))
    meta = json.loads(str(z["meta"]))
    return Sequence(map_sha, meta["song"], meta["split"], meta["stars"], meta["style"], z["idx"], z["x"], z["cls"], z["length"],
                    z["dist"].astype(np.float32), z["mask"])
