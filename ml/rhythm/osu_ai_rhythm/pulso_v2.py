"""Pulso v2: heads, then type, duration and spacing decided with the context of the chosen sequence.

    python -m osu_ai_rhythm.pulso_v2 DATASET ANALYSES OUTPUT_DIR [--baseline V1_BUNDLE]

Why a v2. Pulso v1 decided circle or slider from the audio at the head alone. In the human maps
of the train split the gap to the next head explains most of that choice (1/2 gap: 6-25% sliders,
1/1 gap: 65-91%, see study.py), and the audio at the head barely moves it. Without that context v1
could only predict the average slider rate, slightly above 0.5 for every head, and a 0.5 cut turned
almost everything into sliders (Self Aware: 424 of 453).

Here the type, duration and spacing models see the sequence: gaps to the previous and next heads,
local head density and position inside 1/4 runs, plus how loud the next beats are. During training
the context comes from human heads; at inference from the heads the model chose itself, and the
end-to-end evaluation measures exactly that path. The type probability is calibrated with isotonic
regression on out-of-fold predictions grouped by song, so no song informs its own calibration.
Duration is a class over 1..16 ticks restricted to what fits before the next head.

Fits on train, reports validation. The test split is not opened.
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path

import joblib
import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier, HistGradientBoostingRegressor
from sklearn.isotonic import IsotonicRegression
from threadpoolctl import threadpool_limits

from .aligned import AlignedMap, aligned_maps, grid_arrays
from .data import Lineage, _sha256
from .features import feature_names
from .pulso import CONTEXT as HPSS_CONTEXT, HPSS, matrix as matrix_v1, selection
from .train import f1

VERSION = "pulso-features-5"
# The mapping style a bundle is conditioned on: the share of heads in 1/4 runs of three or more
# (bursts and streams) of the human map. Popular maps reach their intensity with jumps and few
# streams (Expert, loud passages: 4% streams against 27% in the rest of the dataset); the user picks.
STYLES = {"jumps": 0.15, "auto": 0.5, "streams": 0.85}
SCHEMA = "pulso-poc-0.1"
MAX_GAP = 32
MAX_LENGTH = 16
AHEAD = (2, 4, 8, 16)
QUIET = 0.3  # loudness relative to the song's median under which a tick counts as silent
SEED = 7319


def ahead_names() -> list[str]:
    return [f"loud_next{n}" for n in AHEAD] + ["loud_prev4", "loud_here", "quiet_ticks_next16"]


def ahead_features(analysis: dict, idx: np.ndarray) -> np.ndarray:
    """Loudness of what follows each position, relative to the song: a slider needs sound to hold."""
    _, g = grid_arrays(analysis)
    energy = g["energy"]
    median = max(float(np.median(energy[energy > 0])) if (energy > 0).any() else 0.0, 1e-6)
    rel = energy / median
    padded = np.concatenate([rel, np.zeros(MAX_GAP)])
    cum = np.concatenate([[0], np.cumsum(padded)])
    quiet = np.concatenate([[0], np.cumsum(padded < QUIET)])
    cols = [(cum[idx + 1 + n] - cum[idx + 1]) / n for n in AHEAD]
    lo = np.maximum(idx - 4, 0)
    cols.append((cum[idx] - cum[lo]) / np.maximum(idx - lo, 1))
    cols.append(padded[idx])
    cols.append(quiet[idx + 17] - quiet[idx + 1])
    return np.stack(cols, axis=1).astype(np.float32)


CONTRAST_KEYS = ("onset", "percussive", "harmonic")
CONTRAST_SPAN = 32  # two bars


def contrast_names() -> list[str]:
    names = [f"{k}_rel" for k in CONTRAST_KEYS] + ["harmonic_peak"]
    return names + [f"harmonic_rel@{d:+d}" for d in (-2, -1, 1, 2)]


def contrast_features(analysis: dict, idx: np.ndarray) -> np.ndarray:
    """Each onset track relative to its own median over the surrounding two bars, and whether the
    harmonic onset is a local peak.

    The harmonic track has a high floor (0.25-0.5 almost everywhere), so a sung syllable over quiet
    drums (Beauty And A Beat at 5 s: 0.62-1.01 every half beat, drums 0.06-0.24) did not stand out and
    the head model gave it 4-20%. Relative to its surroundings it does.
    """
    _, g = grid_arrays(analysis)
    size = len(g["energy"])
    out = []
    rel = {}
    for k in CONTRAST_KEYS:
        v = g[k]
        pad = np.pad(v, CONTRAST_SPAN // 2, mode="edge")
        windows = np.lib.stride_tricks.sliding_window_view(pad, CONTRAST_SPAN + 1)[:size]
        rel[k] = v / (np.median(windows, axis=1) + 0.05)
        out.append(rel[k])
    h = g["harmonic"]
    left = np.concatenate([[0], h[:-1]])
    right = np.concatenate([h[1:], [0]])
    out.append(((h >= left) & (h >= right) & (rel["harmonic"] >= 1.5)).astype(np.float32))
    for d in (-2, -1, 1, 2):
        shifted = np.zeros(size, dtype=np.float32)
        if d > 0:
            shifted[:-d] = rel["harmonic"][d:]
        else:
            shifted[-d:] = rel["harmonic"][:d]
        out.append(shifted)
    dense = np.stack(out, axis=1).astype(np.float32)
    return dense[np.clip(idx, 0, size - 1)]


def contrasted(bundle: dict) -> bool:
    return bundle.get("features") in ("pulso-features-4", "pulso-features-5")


STEM_KEYS = ("vocals", "drums", "bass", "other", "vocalLevel", "vocalHeld")


def stemmed(bundle: dict) -> bool:
    return bundle.get("features") == "pulso-features-5"


def stem_names() -> list[str]:
    return list(STEM_KEYS) + ["vocals_rel"] + [f"vocals@{d:+d}" for d in (-2, -1, 1, 2)] + [f"vocalHeld@{d:+d}" for d in (-1, 1)]


def stem_features(analysis: dict, idx: np.ndarray) -> np.ndarray:
    """Separated stems at each tick: attacks of voice, drums, bass and the rest, the voice's level
    and held notes, the voice around the tick and relative to its two-bar median.

    HPSS leaves voice, guitars and synths in one harmonic part; in Beauty And A Beat the attacks at
    4.9-5.5 s that looked like the "Nicki Minaj" syllables were guitar and synth, and the voice,
    alone in its stem, starts at 5.56 s: three short attacks and a held "Naj".
    """
    if "stems" not in analysis:
        raise ValueError("This model needs the separated stems (stems.json next to the analysis; run osu-ai-separate)")
    size = int(max(idx.max() if len(idx) else 0, max(p["index"] for p in analysis["stems"])) + 1)
    dense = {k: np.zeros(size, dtype=np.float32) for k in STEM_KEYS}
    for p in analysis["stems"]:
        if p["index"] >= 0:
            for k in STEM_KEYS:
                dense[k][p["index"]] = p[k]
    v = dense["vocals"]
    pad = np.pad(v, CONTRAST_SPAN // 2, mode="edge")
    rel = v / (np.median(np.lib.stride_tricks.sliding_window_view(pad, CONTRAST_SPAN + 1)[:size], axis=1) + 0.05)

    def shift(a, d):
        out = np.zeros(size, dtype=np.float32)
        if d > 0:
            out[:-d] = a[d:]
        else:
            out[-d:] = a[:d]
        return out

    cols = [dense[k] for k in STEM_KEYS] + [rel] + [shift(v, d) for d in (-2, -1, 1, 2)] + [shift(dense["vocalHeld"], d) for d in (-1, 1)]
    return np.stack(cols, axis=1)[np.clip(idx, 0, size - 1)]


def audio_matrix(analysis: dict, stars: float, hpss: bool, style: float | None = None, contrast: bool = False,
                 stems: bool = False) -> tuple[np.ndarray, np.ndarray]:
    """Audio features; with a style value (pulso-features-3 and later) it is appended as a control
    column, and pulso-features-4 adds local contrast of the onset tracks."""
    idx, x = matrix_v1(analysis, stars, hpss)
    cols = [x, ahead_features(analysis, idx)]
    if contrast:
        cols.append(contrast_features(analysis, idx))
    if stems:
        cols.append(stem_features(analysis, idx))
    if style is not None:
        cols.append(np.full((len(idx), 1), style, dtype=np.float32))
    return idx, np.column_stack(cols)


def style_of(objects) -> float:
    """Share of a map's heads that sit in 1/4 runs of three or more heads."""
    q = np.array([o.q for o in objects])
    if len(q) < 3:
        return 0.0
    quarter = np.diff(q) == 1
    inside = np.zeros(len(q), dtype=bool)
    start = 0
    for i in range(1, len(q) + 1):
        if i == len(q) or not quarter[i - 1]:
            if i - start >= 3:
                inside[start:i] = True
            start = i
    return float(inside.mean())


def styled(bundle: dict) -> bool:
    return bundle.get("features") in ("pulso-features-3", "pulso-features-4", "pulso-features-5")


def resolve_style(bundle: dict, stars: float, name: str) -> float:
    """The style value for a name: that quantile of the training maps' styles near these stars."""
    table = np.array(bundle["styleTable"])
    near = table[np.abs(table[:, 0] - stars) <= 0.75, 1]
    values = near if len(near) >= 20 else table[:, 1]
    # "auto" is the typical amount of 1/4 runs, the mean: most maps have few and some have many, so
    # the median would ask for fewer bursts than the average human map has.
    return float(values.mean()) if name == "auto" else float(np.quantile(values, STYLES[name]))


def context_names() -> list[str]:
    return ["gap_prev", "gap_next", "gap_prev2", "gap_next2", "heads_pm4", "heads_pm16", "run_length", "run_position", "is_last"]


def sequence_context(idx: np.ndarray, heads: np.ndarray) -> np.ndarray:
    """Per row of idx: the chosen sequence around it. Only rows where heads is true are used."""
    pos = idx[heads]
    n = len(idx)
    out = np.zeros((n, len(context_names())), dtype=np.float32)
    if len(pos) == 0:
        return out
    rows = np.flatnonzero(heads)
    k = np.arange(len(pos))

    def gap(a, b):
        return np.clip(b - a, 0, MAX_GAP).astype(np.float32)

    big = np.full(len(pos), MAX_GAP, dtype=np.float32)
    prev = np.where(k >= 1, gap(pos[np.maximum(k - 1, 0)], pos), big)
    nxt = np.where(k + 1 < len(pos), gap(pos, pos[np.minimum(k + 1, len(pos) - 1)]), big)
    prev2 = np.where(k >= 2, gap(pos[np.maximum(k - 2, 0)], pos), big)
    nxt2 = np.where(k + 2 < len(pos), gap(pos, pos[np.minimum(k + 2, len(pos) - 1)]), big)
    near4 = np.searchsorted(pos, pos + 4, side="right") - np.searchsorted(pos, pos - 4, side="left") - 1
    near16 = np.searchsorted(pos, pos + 16, side="right") - np.searchsorted(pos, pos - 16, side="left") - 1

    # Runs of consecutive 1/4 heads.
    run_len = np.ones(len(pos), dtype=np.float32)
    run_pos = np.zeros(len(pos), dtype=np.float32)
    start = 0
    for i in range(1, len(pos) + 1):
        if i == len(pos) or pos[i] - pos[i - 1] != 1:
            run_len[start:i] = i - start
            run_pos[start:i] = np.arange(i - start)
            start = i
    out[rows] = np.stack([prev, nxt, prev2, nxt2, near4, near16, run_len, run_pos, (k == len(pos) - 1).astype(np.float32)], axis=1)
    return out


HISTORY = 8
SINCE_CAP = 16


def history_names() -> list[str]:
    return [f"head@-{k}" for k in range(1, HISTORY + 1)] + ["ticks_since_head"]


def history_features(heads: np.ndarray) -> np.ndarray:
    """For each tick: which of the previous eight ticks hold a head, and ticks since the last head."""
    n = len(heads)
    out = np.zeros((n, HISTORY + 1), dtype=np.float32)
    for k in range(1, HISTORY + 1):
        out[k:, k - 1] = heads[:-k] if k < n else 0
    since = np.full(n, SINCE_CAP, dtype=np.float32)
    last = -10 ** 9
    for t in range(n):
        since[t] = min(t - last, SINCE_CAP)
        if heads[t]:
            last = t
    out[:, HISTORY] = since
    return out


def _state_table() -> np.ndarray:
    """Every history the decoder can be in: codes 1..255 are the eight-tick bit patterns (bit k = a
    head k+1 ticks back), codes 256..263 an empty history with 9..16 ticks since the last head."""
    rows = np.zeros((256 + SINCE_CAP - HISTORY, HISTORY + 1), dtype=np.float32)
    for code in range(1, 256):
        bits = [(code >> k) & 1 for k in range(HISTORY)]
        rows[code, :HISTORY] = bits
        rows[code, HISTORY] = bits.index(1) + 1
    for j, since in enumerate(range(HISTORY + 1, SINCE_CAP + 1)):
        rows[256 + j, HISTORY] = since
    return rows


STATES = _state_table()


def ar_table(model, x: np.ndarray, chunk: int = 400) -> np.ndarray:
    """P(head | audio at t, history) for every tick and every possible history, in batches."""
    out = np.zeros((len(x), len(STATES)), dtype=np.float32)
    for start in range(0, len(x), chunk):
        part = x[start:start + chunk]
        rows = np.column_stack((np.repeat(part, len(STATES), axis=0), np.tile(STATES, (len(part), 1))))
        out[start:start + len(part)] = model.predict_proba(rows)[:, 1].reshape(len(part), len(STATES))
    return out


def ar_walk(table: np.ndarray, gate: np.ndarray, threshold: float, state: tuple[int, int] = (0, SINCE_CAP), with_state: bool = False):
    """Left to right: a head wherever the probability given the heads already chosen reaches the threshold."""
    heads = np.zeros(len(table), dtype=bool)
    bits, since = state
    for t in range(len(table)):
        code = bits if bits else 256 + min(max(since, HISTORY + 1), SINCE_CAP) - (HISTORY + 1)
        chosen = bool(gate[t]) and table[t, code] >= threshold
        heads[t] = chosen
        bits = ((bits << 1) | chosen) & 0xFF
        since = 1 if chosen else min(since + 1, SINCE_CAP)
    return (heads, (bits, since)) if with_state else heads


WINDOW = 32  # two bars


def selection_filled(probability: np.ndarray, window: int = WINDOW, floor: float = 0.5) -> np.ndarray:
    """Heads by the song's expected count, then any two-bar window left with less than half of the
    heads the model expects there gets its most likely ticks up to that half.

    Counting over the whole song alone left calm passages with held vocals empty for seconds
    (Beauty And A Beat: seven gaps of 3-7 s, 33 s in three minutes, while human maps average 2.5 s
    per minute); counting every window alone flattened the contrast between calm and loud parts
    (validation loud/quiet density 1.8 against 2.1 in human maps).
    """
    mask = selection(probability)
    for start in range(0, len(probability), window):
        part = probability[start:start + window]
        want = int(np.floor(floor * float(part.sum()) + 0.5))
        have = int(mask[start:start + window].sum())
        if have < want:
            free = [i for i in np.argsort(-part, kind="stable") if not mask[start + i]]
            mask[[start + i for i in free[:want - have]]] = True
    return mask


POCKET_S = (2.5, float("inf"))


def fill_pockets(heads: np.ndarray, probability: np.ndarray, playing: np.ndarray, tick_s: float,
                 pocket: tuple[float, float] = POCKET_S) -> np.ndarray:
    """Heads in stretches of 2.5 s or more left empty while the music plays, at the most likely
    ticks, until no such stretch remains.

    Human Insane and Expert maps leave a stretch of 3 s or more without objects while the music
    plays 0.24 times per minute on average (90% of maps under 0.77; half have none), and when they
    do it is usually a real break of about ten seconds. Beauty And A Beat came out with 2.3 per
    minute, all 3-7 s pockets in passages of held vocals the head model rated low. An 8 s ceiling
    on the fill let a Pulso-S map of The Fate of Ophelia keep 62 s of silence over the song at its
    usual loudness, so stretches of any length are filled while the music plays; breaks stay where
    the music is quiet (less than half of the stretch audible).
    """
    heads = heads.copy()
    lo = round(pocket[0] / tick_s)
    hi = round(pocket[1] / tick_s) if np.isfinite(pocket[1]) else len(heads)
    changed = True
    while changed:
        changed = False
        pos = np.flatnonzero(heads)
        for a, b in zip(pos[:-1], pos[1:]):
            if lo <= b - a <= hi and playing[a + 1:b].mean() >= 0.5:
                inside = np.arange(a + 2, b - 1)  # not glued to either neighbour
                if len(inside):
                    heads[inside[np.argmax(probability[inside])]] = True
                    changed = True
    return heads


def selection_windows(probability: np.ndarray, window: int = WINDOW) -> np.ndarray:
    """In every two-bar window, as many heads as the model expects there, at its most likely ticks.

    Counting over the whole song let passages with clear drum hits take every head and left calm
    ones with held vocals empty for seconds while the music played (Beauty And A Beat: seven gaps of
    3-7 s, 33 s in all); human mappers follow the voice there with sliders.
    """
    mask = np.zeros(len(probability), dtype=bool)
    for start in range(0, len(probability), window):
        part = probability[start:start + window]
        k = int(np.clip(round(float(part.sum())), 0, len(part)))
        mask[start + np.argsort(-part, kind="stable")[:k]] = True
    return mask


def ar_select_windows(table: np.ndarray, gate: np.ndarray, expected: np.ndarray) -> np.ndarray:
    """Per two-bar window, as many heads as the independent model expects there; where they go is
    decided by the history-aware model. One threshold for the whole song let dense passages feed on
    themselves and emptied the quiet ones (validation: loud/quiet density 3.0 against 2.1 in human maps)."""
    heads = np.zeros(len(table), dtype=bool)
    state = (0, SINCE_CAP)
    for start in range(0, len(table), WINDOW):
        part = slice(start, min(start + WINDOW, len(table)))
        target = int(round(float(expected[part].sum())))
        best, best_state = ar_walk(table[part], gate[part], 1.01, state, True)
        if target > 0:
            lo, hi = 0.0, 1.0
            for _ in range(14):
                mid = (lo + hi) / 2
                h, st = ar_walk(table[part], gate[part], mid, state, True)
                if abs(int(h.sum()) - target) < abs(int(best.sum()) - target) or (int(h.sum()) == int(best.sum()) and mid > 0.5):
                    best, best_state = h, st
                if h.sum() > target:
                    lo = mid
                else:
                    hi = mid
        heads[part] = best
        state = best_state
    return heads


def ar_select(table: np.ndarray, gate: np.ndarray, target: int) -> np.ndarray:
    """The threshold whose walk gives the target number of heads (bisection; the count falls as it rises)."""
    if target <= 0:
        return np.zeros(len(table), dtype=bool)
    lo, hi = 0.0, 1.0
    best = ar_walk(table, gate, 0.5)
    for _ in range(18):
        mid = (lo + hi) / 2
        heads = ar_walk(table, gate, mid)
        if abs(int(heads.sum()) - target) < abs(int(best.sum()) - target):
            best = heads
        if heads.sum() > target:
            lo = mid
        else:
            hi = mid
    return best


def human_rows(m: AlignedMap, hpss: bool, style: float | None = None, contrast: bool = False, stems: bool = False):
    """Audio matrix over the mapped span, human head mask, and per-head human labels."""
    idx, x = audio_matrix(m.analysis, m.stars, hpss, style, contrast, stems)
    keep = (idx >= m.objects[0].q) & (idx <= m.objects[-1].q)
    idx, x = idx[keep], x[keep]
    spun = np.zeros(len(idx), dtype=bool)
    for start, end in m.spinner_spans:
        spun |= (idx >= start) & (idx <= end)
    row = {int(q): r for r, q in enumerate(idx)}
    heads = np.zeros(len(idx), dtype=bool)
    slider = np.zeros(len(idx), dtype=np.int8)
    length = np.zeros(len(idx), dtype=np.int16)
    spacing = np.full(len(idx), np.nan, dtype=np.float32)
    previous = None
    for o in m.objects:
        r = row.get(o.q)
        if r is not None:
            heads[r] = True
            slider[r] = o.kind == "slider"
            length[r] = min(o.length, MAX_LENGTH) if o.kind == "slider" else 0
            if previous is not None:
                spacing[r] = np.log1p(np.hypot(o.x - previous.end_x, o.y - previous.end_y) / m.radius)
        previous = o
    return idx, x, heads, slider, length, spacing, spun


def new_classifier(iterations: int, **extra):
    return HistGradientBoostingClassifier(max_iter=iterations, max_leaf_nodes=31, min_samples_leaf=60, learning_rate=0.08,
                                          l2_regularization=1.0, early_stopping=False, random_state=SEED, **extra)


def collect(maps, hpss, head_budget, rng, with_style=True):
    """Head-model rows sampled per map; every human head for the other models."""
    per_map = max(1, head_budget // max(1, len(maps)))
    hx, hh, hy, tx, ty, tl, tsp, groups = [], [], [], [], [], [], [], []
    for m in maps:
        idx, x, heads, slider, length, spacing, spun = human_rows(m, hpss, style_of(m.objects) if with_style else None, contrast=True, stems=True)
        free = np.flatnonzero(~spun)
        take = rng.choice(free, min(per_map, len(free)), replace=False)
        hx.append(x[take]); hy.append(heads[take]); hh.append(history_features(heads)[take])
        ctx = sequence_context(idx, heads)
        tx.append(np.column_stack((x[heads], ctx[heads]))); ty.append(slider[heads]); tl.append(length[heads]); tsp.append(spacing[heads])
        groups.append(np.full(int(heads.sum()), hash_song(m.audio_sha)))
    return (np.vstack(hx), np.vstack(hh), np.concatenate(hy), np.vstack(tx), np.concatenate(ty), np.concatenate(tl),
            np.concatenate(tsp), np.concatenate(groups))


def _sha256_text(text: str) -> str:
    import hashlib
    return hashlib.sha256(text.encode()).hexdigest()


def hash_song(audio_sha: str) -> int:
    return int(audio_sha[:8], 16)


def fit(maps, hpss: bool, iterations: int, head_budget: int, folds: int = 3, ar: bool = True) -> dict:
    rng = np.random.default_rng(SEED)
    hx, hh, hy, tx, ty, tl, tsp, groups = collect(maps, hpss, head_budget, rng)
    head_rows = len(hy)
    # Two head models: the first says how many heads a mapper would place (its expected count, as
    # in B2); the second, which also sees the heads already placed, says where, so heads form
    # patterns (pairs, bursts, rests) instead of the most likely positions taken one by one.
    head = new_classifier(iterations).fit(hx, hy)
    ar_head = new_classifier(iterations).fit(np.column_stack((hx, hh)), hy) if ar else None
    del hx, hh, hy

    # Out-of-fold type probabilities, folds by song, for the calibrator.
    songs = np.unique(groups)
    fold_of = {s: i % folds for i, s in enumerate(rng.permutation(songs))}
    fold = np.array([fold_of[g] for g in groups])
    oof = np.zeros(len(ty), dtype=np.float64)
    for f in range(folds):
        inside = fold != f
        if len(np.unique(ty[inside])) < 2 or not (~inside).any():
            oof[~inside] = ty[inside].mean() if inside.any() else 0.5
            continue
        oof[~inside] = new_classifier(iterations).fit(tx[inside], ty[inside]).predict_proba(tx[~inside])[:, 1]
    calibrator = IsotonicRegression(out_of_bounds="clip", y_min=0, y_max=1).fit(oof, ty)
    kind = new_classifier(iterations).fit(tx, ty)

    is_slider = ty == 1
    length = new_classifier(iterations).fit(tx[is_slider], tl[is_slider]) if len(np.unique(tl[is_slider])) > 1 else None
    has = np.isfinite(tsp)
    spacing = HistGradientBoostingRegressor(max_iter=iterations, max_leaf_nodes=31, min_samples_leaf=60, learning_rate=0.08,
                                            l2_regularization=1.0, early_stopping=False, random_state=SEED).fit(tx[has], tsp[has])
    return {"features": VERSION, "hpss": hpss, "head": head, "arHead": ar_head, "type": kind, "calibrator": calibrator, "length": length,
            "lengthFallback": int(np.median(tl[is_slider])) if is_slider.any() else 2, "spacing": spacing,
            "names": feature_names() + ([f"{k}@{d:+d}" for d in HPSS_CONTEXT for k in HPSS] if hpss else []) + ahead_names() + contrast_names() + stem_names() + ["style_quarter_runs"] + context_names(),
            "historyNames": history_names(),
            "styleTable": [[float(m.stars), style_of(m.objects)] for m in maps],
            "rows": {"head": int(head_rows), "type": int(len(ty)), "slider": int(is_slider.sum())}}


def decode(bundle: dict, idx: np.ndarray, x: np.ndarray, energy_gate: np.ndarray, end_index: int,
           playing: np.ndarray | None = None, tick_s: float | None = None) -> dict:
    """Heads by expected count; then type, duration and spacing with the chosen sequence as context.
    With `playing` (music audible per tick) and the tick length, empty pockets are filled."""
    p = bundle["head"].predict_proba(x)[:, 1]
    p = np.where(energy_gate, p, 0)
    if bundle.get("arHead") is not None and len(x):
        table = ar_table(bundle["arHead"], x)
        if bundle.get("arDecoding", "windows") == "windows":
            heads = ar_select_windows(table, energy_gate, p)
        else:
            heads = ar_select(table, energy_gate, int(round(float(p.sum()))))
    elif bundle.get("headDecoding", "fill") == "fill":
        heads = selection_filled(p)
    elif bundle.get("headDecoding") == "windows":
        heads = selection_windows(p)
    else:
        heads = selection(p)
    if playing is not None and tick_s and bundle.get("fillPockets", True) and heads.any():
        heads = fill_pockets(heads, np.where(energy_gate, p, 0), playing, tick_s)
    ctx = sequence_context(idx, heads)
    hx = np.column_stack((x[heads], ctx[heads]))
    if len(hx) == 0:
        return {"heads": heads, "p": p, "slider": np.zeros(0), "isSlider": np.zeros(0, dtype=bool), "length": np.zeros(0, dtype=int), "spacing": np.zeros(0)}
    raw = bundle["type"].predict_proba(hx)[:, 1]
    p_slider = bundle["calibrator"].predict(raw)
    pos = idx[heads]
    room = np.append(np.diff(pos), max(1, end_index - pos[-1])) - 1  # ticks a tail may take, one tick of rest kept
    length = np.zeros(len(pos), dtype=int)
    if bundle["length"] is not None:
        classes = bundle["length"].classes_
        probs = bundle["length"].predict_proba(hx)
        for i in range(len(pos)):
            allowed = classes <= room[i]
            length[i] = int(classes[allowed][np.argmax(probs[i][allowed])]) if allowed.any() else 0
    else:
        length = np.minimum(bundle["lengthFallback"], room)
    is_slider = (p_slider >= 0.5) & (length >= 1)
    spacing = np.expm1(bundle["spacing"].predict(hx))
    return {"heads": heads, "p": p, "slider": p_slider, "isSlider": is_slider, "length": np.where(is_slider, length, 0), "spacing": spacing}


def playing_mask(analysis: dict, idx: np.ndarray) -> np.ndarray:
    """Ticks where the music is audible: loudness at least half the song's median."""
    energy = {g["index"]: g["energy"] for g in analysis["grid"]}
    values = np.array([energy.get(int(i), 0) for i in idx])
    median = float(np.median(values[values > 0])) if (values > 0).any() else 0.0
    return values >= 0.5 * median


def tick_seconds(analysis: dict) -> float:
    return analysis["timing"]["beatLengthUs"] / 4 / 1e6


def energy_gate(analysis: dict, idx: np.ndarray) -> np.ndarray:
    energy = {g["index"]: g["energy"] for g in analysis["grid"]}
    return np.array([energy.get(int(i), 0) >= 0.05 for i in idx])


def predict(bundle: dict, analysis: dict, stars: float, style: str = "auto") -> dict:
    """The product path: the whole song, the generator's contract."""
    if style not in STYLES:
        raise ValueError(f"Unknown style '{style}'; expected one of {', '.join(STYLES)}")
    idx, x = audio_matrix(analysis, stars, bundle["hpss"], resolve_style(bundle, stars, style) if styled(bundle) else None, contrasted(bundle), stemmed(bundle))
    with threadpool_limits(limits=2):
        d = decode(bundle, idx, x, energy_gate(analysis, idx), int(idx.max()) + 1 if len(idx) else 0,
                   playing_mask(analysis, idx), tick_seconds(analysis))
    chosen = d["heads"]
    # The generator makes a slider when the probability is >= 0.5 and the duration fits; a slider
    # chosen with no room (length 0) therefore comes out as a circle there too.
    notes = [{"sliderProbability": float(np.clip(s, 0, 1)), "durationGrid": int(max(1, n)), "importance": float(np.clip(sp / 8, 0, 1)),
              "distanceRadii": float(np.clip(sp, 0, 20))}
             for s, n, sp in zip(d["slider"], d["length"], d["spacing"])]
    return {"schemaVersion": SCHEMA, "stars": stars, "style": style if styled(bundle) else None, "model": {"features": bundle["features"], "threshold": 0.0},
            "indices": idx[chosen].tolist(), "probabilities": d["p"][chosen].tolist(), "notes": notes}


# ---- evaluation -------------------------------------------------------------------------------

def map_profile(idx, heads, is_slider, length, analysis) -> dict:
    """Map-level shape of a decoded or human map, comparable between the two."""
    _, g = grid_arrays(analysis)
    pos = idx[heads]
    n = len(pos)
    if n == 0:
        return {"heads": 0}
    energy = g["energy"]
    median = max(float(np.median(energy[energy > 0])), 1e-6)
    bars = len(energy) // 16 + 1
    bar_energy = np.array([energy[b * 16:(b + 1) * 16].mean() if b * 16 < len(energy) else 0 for b in range(bars)])
    first, last = pos[0] // 16, pos[-1] // 16
    span = np.arange(first, last + 1)
    ranks = bar_energy[span].argsort().argsort() / max(1, len(span) - 1)
    loud_bars, quiet_bars = span[ranks >= 0.8], span[ranks < 0.2]
    per_bar = Counter(int(q) // 16 for q in pos)
    density = lambda bs: statistics.mean(per_bar[int(b)] / 4 for b in bs) if len(bs) else float("nan")
    run = np.diff(pos) == 1
    sliders = int(is_slider.sum())
    into_quiet = 0
    for q, L in zip(pos[is_slider], length[is_slider]):
        body = energy[q + 1:q + L + 1] if q + L + 1 <= len(energy) else energy[q + 1:]
        into_quiet += bool(len(body)) and (body < QUIET * median).mean() >= 0.5
    gaps = np.diff(pos)
    # Seconds without any object while the music plays (the gap's mean loudness at least half the
    # song's median), per minute of mapped span. Slider bodies count as occupied.
    tick_s = analysis["timing"]["beatLengthUs"] / 4 / 1e6
    ends = pos + (length if len(length) == len(pos) else 0)
    idle = 0.0
    pockets = 0
    for a, b in zip(ends[:-1], pos[1:]):
        if (b - a) * tick_s >= 3:
            if energy[a:b].mean() >= 0.5 * median:
                idle += (b - a) * tick_s
                pockets += (b - a) * tick_s <= 8
    minutes = max((pos[-1] - pos[0]) * tick_s / 60, 1e-6)
    buckets = {"1/4": gaps == 1, "1/2": gaps == 2, "3/4": gaps == 3, "1/1": gaps == 4, "5-8": (gaps >= 5) & (gaps <= 8), "9+": gaps >= 9}
    hist = {k: float(v.mean()) if len(gaps) else 0.0 for k, v in buckets.items()}
    return {"heads": n, "idlePlayingSecondsPerMinute": idle / minutes, "pocketsPerMinute": pockets / minutes, "sliderShare": sliders / n, "quarterGapShare": float(run.mean()) if len(run) else 0.0, "gaps": hist,
            "densityLoud": density(loud_bars), "densityQuiet": density(quiet_bars),
            "sliderIntoQuiet": into_quiet / max(1, sliders)}


def evaluate_map(bundle, m: AlignedMap, decoder, style: str = "human") -> dict:
    """style "human" conditions on the human map's own style (how well the model follows a given
    style); a name ("auto", "jumps", "streams") is what the product would ask for."""
    hpss = bundle["hpss"]
    value = None
    if decoder == "v2" and styled(bundle):
        value = style_of(m.objects) if style == "human" else resolve_style(bundle, m.stars, style)
    idx, x, heads, slider, length, spacing, _ = human_rows(m, hpss, value, contrasted(bundle), stemmed(bundle)) if decoder == "v2" else (None,) * 7
    if decoder == "v1":
        idx_all, xv1 = matrix_v1(m.analysis, m.stars, hpss)
        keep = (idx_all >= m.objects[0].q) & (idx_all <= m.objects[-1].q)
        idx, xv1 = idx_all[keep], xv1[keep]
        _, x, heads, slider, length, spacing, _ = human_rows(m, True)
        x = xv1
    end = int(idx.max()) + 1
    gate = energy_gate(m.analysis, idx)
    if decoder == "v2":
        d = decode(bundle, idx, x, gate, end, playing_mask(m.analysis, idx), tick_seconds(m.analysis))
        is_slider, pred_len = d["isSlider"], d["length"]
        raw_at_human = bundle["calibrator"].predict(bundle["type"].predict_proba(np.column_stack((x[heads], sequence_context(idx, heads)[heads])))[:, 1])
    else:
        models = bundle["models"]
        p = np.where(gate, models[0].predict_proba(x)[:, 1], 0)
        chosen = selection(p)
        pos = idx[chosen]
        s = models[1].predict_proba(x[chosen])[:, 1] if chosen.any() else np.zeros(0)
        L = np.clip(np.rint(np.exp2(models[2].predict(x[chosen]))), 1, 16).astype(int) if chosen.any() else np.zeros(0, dtype=int)
        room = np.append(np.diff(pos), max(1, end - pos[-1])) - 1 if len(pos) else np.zeros(0, dtype=int)
        L = np.minimum(L, room)  # the C# planner's clipping
        is_slider = (s >= 0.5) & (L >= 1)
        pred_len = np.where(is_slider, L, 0)
        d = {"heads": chosen}
        raw_at_human = models[1].predict_proba(x[heads])[:, 1]
    truth = slider[heads].astype(bool)
    # Heads a human placed on a sung or melodic attack over quiet drums: harmonic onset at least 1.5
    # times its two-bar median, percussive onset under the song's median.
    _, g = grid_arrays(m.analysis)
    rel = contrast_features(m.analysis, idx)[:, 2]
    perc = g["percussive"][np.clip(idx, 0, len(g["percussive"]) - 1)]
    vocal = heads & (rel >= 1.5) & (perc < np.median(g["percussive"]))
    vocal_recall = float((d["heads"] & vocal).sum() / vocal.sum()) if vocal.sum() else float("nan")
    human = map_profile(idx, heads, truth, length[heads], m.analysis)
    model = map_profile(idx, d["heads"], np.asarray(is_slider, dtype=bool), np.asarray(pred_len), m.analysis)
    pr = np.clip(raw_at_human, 1e-6, 1 - 1e-6)
    return {"map": m.map_sha, "song": m.audio_sha[:12], "stars": round(m.stars, 2), "clickF1": f1(d["heads"], heads),
            "vocalHeadRecall": vocal_recall, "vocalHeads": int(vocal.sum()),
            "typeAtHumanHeads": {"accuracy": float(np.mean((pr >= 0.5) == truth)), "brier": float(np.mean((pr - truth) ** 2)),
                                 "logLoss": float(-np.mean(truth * np.log(pr) + (~truth) * np.log(1 - pr))),
                                 "predictedSliderShare": float(np.mean(pr >= 0.5)), "humanSliderShare": float(truth.mean()),
                                 "probabilities": pr.round(3).tolist(), "truth": truth.astype(int).tolist()},
            "human": human, "model": model}


def summarise(rows: list[dict]) -> dict:
    """Macro over songs: each song weighs the same however many difficulties it has."""
    by_song = defaultdict(list)
    for r in rows:
        by_song[r["song"]].append(r)

    def macro(get):
        values = [statistics.mean(v) for v in ([get(r) for r in rs if get(r) == get(r)] for rs in by_song.values()) if v]
        return round(statistics.mean(values), 4) if values else None

    probs = np.concatenate([r["typeAtHumanHeads"]["probabilities"] for r in rows])
    truth = np.concatenate([r["typeAtHumanHeads"]["truth"] for r in rows])
    bins = np.clip((probs * 10).astype(int), 0, 9)
    reliability = [{"bin": f"{b / 10:.1f}-{(b + 1) / 10:.1f}", "n": int((bins == b).sum()),
                    "meanPredicted": round(float(probs[bins == b].mean()), 3), "observedSliderRate": round(float(truth[bins == b].mean()), 3)}
                   for b in range(10) if (bins == b).any()]
    tp = int(((probs >= 0.5) & (truth == 1)).sum())
    return {
        "maps": len(rows), "songs": len(by_song),
        "clickF1": macro(lambda r: r["clickF1"]),
        "vocalHeadRecall": macro(lambda r: r.get("vocalHeadRecall", float("nan"))),
        "typeAtHumanHeads": {"accuracy": macro(lambda r: r["typeAtHumanHeads"]["accuracy"]), "brier": macro(lambda r: r["typeAtHumanHeads"]["brier"]),
                             "logLoss": macro(lambda r: r["typeAtHumanHeads"]["logLoss"]),
                             "sliderPrecision": round(tp / max(1, int((probs >= 0.5).sum())), 4), "sliderRecall": round(tp / max(1, int(truth.sum())), 4),
                             "reliability": reliability},
        "endToEnd": {
            "sliderShare": {"model": macro(lambda r: r["model"].get("sliderShare", float("nan"))), "human": macro(lambda r: r["human"]["sliderShare"]),
                            "meanAbsError": macro(lambda r: abs(r["model"].get("sliderShare", 0) - r["human"]["sliderShare"])),
                            "mapsAbove80Percent": sum(r["model"].get("sliderShare", 0) > 0.8 for r in rows),
                            "humanMapsAbove80Percent": sum(r["human"]["sliderShare"] > 0.8 for r in rows)},
            "headsRatio": macro(lambda r: r["model"]["heads"] / max(1, r["human"]["heads"])),
            "pocketsPerMinute": {"model": macro(lambda r: r["model"].get("pocketsPerMinute", float("nan"))), "human": macro(lambda r: r["human"]["pocketsPerMinute"])},
            "idlePlayingSecondsPerMinute": {"model": macro(lambda r: r["model"].get("idlePlayingSecondsPerMinute", float("nan"))),
                                            "human": macro(lambda r: r["human"]["idlePlayingSecondsPerMinute"])},
            "gapHistogramL1": macro(lambda r: sum(abs(r["model"]["gaps"][k] - r["human"]["gaps"][k]) for k in r["human"]["gaps"]) if "gaps" in r["model"] else float("nan")),
            "halfGapShare": {"model": macro(lambda r: r["model"].get("gaps", {}).get("1/2", float("nan"))), "human": macro(lambda r: r["human"]["gaps"]["1/2"])},
            "beatGapShare": {"model": macro(lambda r: r["model"].get("gaps", {}).get("1/1", float("nan"))), "human": macro(lambda r: r["human"]["gaps"]["1/1"])},
            "quarterGapShare": {"model": macro(lambda r: r["model"].get("quarterGapShare", float("nan"))), "human": macro(lambda r: r["human"]["quarterGapShare"])},
            "densityLoudOverQuiet": {"model": macro(lambda r: r["model"].get("densityLoud", float("nan")) / max(r["model"].get("densityQuiet", float("nan")), 0.05)),
                                     "human": macro(lambda r: r["human"]["densityLoud"] / max(r["human"]["densityQuiet"], 0.05))},
            "sliderIntoQuiet": {"model": macro(lambda r: r["model"].get("sliderIntoQuiet", float("nan"))), "human": macro(lambda r: r["human"]["sliderIntoQuiet"])},
        },
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dataset"); ap.add_argument("analysis_root"); ap.add_argument("output")
    ap.add_argument("--baseline", help="a Pulso v1 bundle to evaluate on the same maps")
    ap.add_argument("--iterations", type=int, default=200)
    ap.add_argument("--head-rows", type=int, default=1_200_000)
    ap.add_argument("--allow-partial", action="store_true")
    ap.add_argument("--variants", default="mixture,mixture-hpss")
    ap.add_argument("--no-ar", action="store_true", help="evaluate the trained bundle also without the history-aware head model")
    ap.add_argument("--skip-ar", action="store_true", help="do not fit the history-aware head model (not used by the product)")
    ap.add_argument("--max-maps", type=int, help="smoke runs only: at most this many maps per split, whole songs")
    args = ap.parse_args()
    output = Path(args.output)
    if output.exists():
        ap.error("Use a new output directory to preserve previous experiments")

    lineage = Lineage({}, [])
    maps = aligned_maps(args.dataset, args.analysis_root, lineage, {"train", "validation"})
    if lineage.skipped["no audio analysis"] and not args.allow_partial:
        ap.error(f"{lineage.skipped['no audio analysis']} maps lack an analysis; wait for the batch or pass --allow-partial")
    # Spinner spans are left out of the head rows (human_rows); only maps with many off-grid heads
    # (1/3, 1/6 rhythms this grid cannot express) are dropped.
    usable = [m for m in maps if m.off_grid <= 0.05 * (len(m.objects) + m.off_grid)]
    without_stems = [m for m in usable if "stems" not in m.analysis]
    if without_stems:
        if not args.allow_partial:
            ap.error(f"{len(without_stems)} maps lack separated stems; run osu-ai-separate --dataset or pass --allow-partial")
        usable = [m for m in usable if "stems" in m.analysis]
    if args.max_maps:
        order = {s: i for i, s in enumerate(sorted({m.audio_sha for m in usable}, key=lambda a: _sha256_text(a)))}
        usable.sort(key=lambda m: order[m.audio_sha])
        usable = [m for split in ("train", "validation") for m in [x for x in usable if x.split == split][:args.max_maps]]
    train = [m for m in usable if m.split == "train"]
    validation = [m for m in usable if m.split == "validation"]
    print(f"train {len(train)} maps / {len({m.audio_sha for m in train})} songs; validation {len(validation)} / {len({m.audio_sha for m in validation})}", file=sys.stderr, flush=True)

    output.mkdir(parents=True)
    report = {"features": VERSION, "partial": bool(args.allow_partial or args.max_maps),
              "lineage": {"manifests": lineage.manifests, "rights": lineage.rights, "analyses": lineage.analyses, "skipped": dict(lineage.skipped)},
              "maps": dict(Counter(m.split for m in usable)), "excludedOffGridMaps": len(maps) - len(usable),
              "trainingMapHashes": [m.map_sha for m in train], "validationMapHashes": [m.map_sha for m in validation],
              "configuration": {"iterations": args.iterations, "headRows": args.head_rows, "seed": SEED, "typeCalibration": "isotonic, 3 song-grouped folds"},
              "limits": "Human timing and mapped span supplied. Validation only; test not opened. Macro averages over songs. "
                        "Spacing is still adapted to procedural placement. HPSS is not instrument separation.",
              "variants": {}}

    with threadpool_limits(limits=4):
        if args.baseline:
            bundle = joblib.load(args.baseline)
            rows = [evaluate_map(bundle, m, "v1") for m in validation]
            report["variants"]["baseline-v1"] = {"bundle": args.baseline, "sha256": _sha256(args.baseline), "validation": summarise(rows)}
            print("baseline v1:", json.dumps(report["variants"]["baseline-v1"]["validation"]["endToEnd"]), file=sys.stderr, flush=True)
        for name in args.variants.split(","):
            hpss = name == "mixture-hpss"
            print(f"fitting {name}", file=sys.stderr, flush=True)
            bundle = fit(train, hpss, args.iterations, args.head_rows, ar=not args.skip_ar)
            target = output / f"{name}.joblib"
            joblib.dump(bundle, target)
            if args.no_ar:
                plain = {**bundle, "arHead": None}
                report["variants"][name + "-independent-heads"] = {"validation": summarise([evaluate_map(plain, m, "v2") for m in validation])}
                print(f"{name} without history:", json.dumps(report["variants"][name + "-independent-heads"]["validation"]["endToEnd"]), file=sys.stderr, flush=True)
            rows = [evaluate_map(bundle, m, "v2") for m in validation]
            report["variants"][name] = {"sha256": _sha256(target), "rows": bundle["rows"], "validation": summarise(rows)}
            auto = summarise([evaluate_map(bundle, m, "v2", "auto") for m in validation])
            report["variants"][name + "-style-auto"] = {"validation": auto}
            print(f"{name} with style auto:", json.dumps(auto["endToEnd"]), file=sys.stderr, flush=True)
            (output / f"{name}-maps.json").write_text(json.dumps([{k: v for k, v in r.items() if k != "typeAtHumanHeads"} for r in rows]), encoding="utf-8")
            print(f"{name}:", json.dumps(report["variants"][name]["validation"]["endToEnd"]), file=sys.stderr, flush=True)
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(output / "report.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
