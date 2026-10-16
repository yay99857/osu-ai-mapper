"""Per-grid-position features from a SongAnalysis (poc-0.2). Shared by training and inference."""

from __future__ import annotations

import numpy as np

CONTEXT = (-4, -3, -2, -1, 1, 2, 3, 4)
BASE = ("onset", "low", "high", "energy", "sustain")
FEATURES_VERSION = "rhythm-features-1"


def feature_names() -> list[str]:
    names = list(BASE) + ["strength_rel_bar", "strength_rel_beat"]
    names += [f"{k}@{d:+d}" for d in CONTEXT for k in ("onset", "low", "high")]
    names += ["energy@-4", "energy@+4", "onset_mean_2beats"]
    names += [f"pos16_{k}" for k in range(16)]
    names += [f"intensity_{k}" for k in range(4)]
    names += ["bpm_200", "stars"]
    return names


def grid_matrix(analysis: dict) -> tuple[np.ndarray, np.ndarray]:
    """Returns (indices, base) for grid points with index >= 0: base has columns BASE."""
    points = sorted((p for p in analysis["grid"] if p["index"] >= 0), key=lambda p: p["index"])
    idx = np.array([p["index"] for p in points], dtype=np.int64)
    base = np.array([[p[k] for k in BASE] for p in points], dtype=np.float32)
    return idx, base


def intensity_by_bar(analysis: dict) -> dict[int, int]:
    out = {}
    for s in analysis["sections"]:
        for b in range(s["firstBar"], s["firstBar"] + s["barCount"]):
            out[b] = s["intensity"]
    return out


def features(analysis: dict, stars: float) -> tuple[np.ndarray, np.ndarray]:
    """(grid indices, feature matrix) for one song at one difficulty."""
    idx, base = grid_matrix(analysis)
    n = len(idx)
    if n == 0:
        return idx, np.zeros((0, len(feature_names())), dtype=np.float32)

    # Dense array over 0..max index so context lookups are simple shifts.
    size = int(idx.max()) + 1
    dense = np.zeros((size, len(BASE)), dtype=np.float32)
    dense[idx] = base

    strength = 0.55 * dense[:, 0] + 0.3 * dense[:, 1] + 0.15 * dense[:, 2]
    bar_max = np.zeros(size, dtype=np.float32)
    beat_max = np.zeros(size, dtype=np.float32)
    for start in range(0, size, 16):
        bar_max[start:start + 16] = strength[start:start + 16].max(initial=0)
    for start in range(0, size, 4):
        beat_max[start:start + 4] = strength[start:start + 4].max(initial=0)

    def shifted(col: int, d: int) -> np.ndarray:
        out = np.zeros(size, dtype=np.float32)
        if d > 0:
            out[:-d] = dense[d:, col]
        else:
            out[-d:] = dense[:d, col]
        return out

    onset_mean = np.convolve(dense[:, 0], np.ones(8) / 8, mode="same")
    intensity = intensity_by_bar(analysis)

    cols = [dense[:, k] for k in range(len(BASE))]
    cols += [strength / np.maximum(bar_max, 1e-6), strength / np.maximum(beat_max, 1e-6)]
    for d in CONTEXT:
        for k in (0, 1, 2):
            cols.append(shifted(k, d))
    cols += [shifted(3, -4), shifted(3, 4), onset_mean.astype(np.float32)]
    pos = np.arange(size) % 16
    cols += [(pos == k).astype(np.float32) for k in range(16)]
    bars = np.arange(size) // 16
    level = np.array([intensity.get(int(b), 0) for b in bars])
    cols += [(level == k).astype(np.float32) for k in range(4)]
    cols += [np.full(size, analysis["timing"]["bpm"] / 200, dtype=np.float32), np.full(size, stars, dtype=np.float32)]

    matrix = np.stack(cols, axis=1)
    return idx, matrix[idx]
