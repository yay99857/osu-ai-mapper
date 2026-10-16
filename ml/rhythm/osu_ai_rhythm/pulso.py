"""Experimental B3 training; inference uses an explicit Pulso contract, never B2 heuristics.

python -m osu_ai_rhythm.pulso DATASET ANALYSES OUTPUT_DIR [--allow-partial]
Fits train only, reports validation only. Test remains unopened by this command.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

import joblib
import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier, HistGradientBoostingRegressor
from threadpoolctl import threadpool_limits

from .aligned import AlignedMap, aligned_maps
from .data import Lineage, _sha256
from .features import features, feature_names
from .train import f1

VERSION = "pulso-features-1"
HPSS = ("percussive", "harmonic", "tone", "toneHeld")
CONTEXT = (-4, -2, -1, 0, 1, 2, 4)


def matrix(analysis: dict, stars: float, hpss: bool = True):
    idx, x = features(analysis, stars)
    if not hpss:
        return idx, x
    if analysis.get("schemaVersion") != "poc-0.3":
        raise ValueError("Pulso requires poc-0.3 audio analysis with HPSS")
    points = {p["index"]: p for p in analysis["grid"] if p["index"] >= 0}
    extra = np.array([[points.get(int(i + d), {}).get(k, 0) for d in CONTEXT for k in HPSS]
                      for i in idx], dtype=np.float32).reshape(len(idx), len(CONTEXT) * len(HPSS))
    if any(k not in p for p in points.values() for k in HPSS):
        raise ValueError("Incomplete HPSS grid")
    return idx, np.column_stack((x, extra))


def labels(m: AlignedMap, idx: np.ndarray):
    """Click, type, total slider duration, incoming distance in circle radii.

    NaN means not supervised, never a circle or a zero-duration slider. Spacing is measured
    from the previous object's official end; the first object has no spacing label.
    """
    y = np.full((len(idx), 4), np.nan, dtype=np.float32)
    y[:, 0] = 0
    rows = {int(q): n for n, q in enumerate(idx)}
    previous = None
    for o in m.objects:
        if o.q in rows:
            r = rows[o.q]
            y[r, :2] = (1, o.kind == "slider")
            if o.kind == "slider":
                y[r, 2] = np.log2(o.length)
            if previous is not None:
                distance = np.hypot(o.x - previous.end_x, o.y - previous.end_y)
                y[r, 3] = np.log1p(distance / m.radius)
        previous = o
    return y


def selection(probability):
    """Exactly the expected count, deterministic on ties (including all-zero input)."""
    k = int(np.clip(round(float(probability.sum())), 0, len(probability)))
    mask = np.zeros(len(probability), dtype=bool)
    mask[np.argsort(-probability, kind="stable")[:k]] = True
    return mask


def samples(m: AlignedMap, hpss: bool):
    idx, x = matrix(m.analysis, m.stars, hpss)
    keep = (idx >= m.objects[0].q) & (idx <= m.objects[-1].q)
    return x[keep], labels(m, idx[keep])


def predict(bundle, analysis, stars):
    """Experimental product adapter. Geometry and playability constraints remain procedural."""
    idx, x = matrix(analysis, stars, bundle["hpss"])
    models = bundle["models"]
    with threadpool_limits(limits=2):
        p = models[0].predict_proba(x)[:, 1]
        energy = {g["index"]: g["energy"] for g in analysis["grid"]}
        p = np.where([energy[int(i)] >= 0.05 for i in idx], p, 0)
        chosen = selection(p)
        chosen_x = x[chosen]
        if len(chosen_x):
            slider = models[1].predict_proba(chosen_x)[:, 1]
            length = np.clip(np.rint(np.exp2(models[2].predict(chosen_x))), 1, 16).astype(int)
            spacing = np.clip(np.expm1(models[3].predict(chosen_x)) / 8, 0, 1)
        else:
            slider, length, spacing = [], [], []
    return {"schemaVersion": "pulso-poc-0.1", "stars": stars,
            "model": {"features": VERSION, "threshold": 0.0},
            "indices": idx[chosen].tolist(), "probabilities": p[chosen].tolist(),
            "notes": [{"sliderProbability": float(s), "durationGrid": int(d), "importance": float(e)}
                      for s, d, e in zip(slider, length, spacing)]}


def fit(maps, hpss, iterations, max_rows):
    # Limit each map before concatenation: memory stays bounded on a 16 GB host.
    rng = np.random.default_rng(7319)
    limit = max(1, max_rows // len(maps))
    xs, ys = [], []
    for m in maps:
        x, y = samples(m, hpss)
        chosen = rng.choice(len(x), min(limit, len(x)), replace=False)
        xs.append(x[chosen]); ys.append(y[chosen])
    x, y = np.vstack(xs), np.vstack(ys)
    models = []
    for col in range(4):
        valid = np.isfinite(y[:, col])
        if not valid.any() or (col < 2 and len(np.unique(y[valid, col])) < 2):
            raise ValueError(f"Insufficient training labels for head {col}")
        cls = HistGradientBoostingClassifier if col < 2 else HistGradientBoostingRegressor
        model = cls(max_iter=iterations, max_leaf_nodes=15, min_samples_leaf=40,
                    learning_rate=0.08, l2_regularization=1, early_stopping=False, random_state=7319)
        model.fit(x[valid], y[valid, col])
        models.append(model)
    return models, len(x)


def evaluate(models, maps, hpss):
    out = []
    for m in maps:
        x, y = samples(m, hpss)
        predicted = selection(models[0].predict_proba(x)[:, 1])
        heads = np.isfinite(y[:, 1])
        sliders = np.isfinite(y[:, 2])
        spacing = np.isfinite(y[:, 3])
        out.append({"mapSha256": m.map_sha, "audioSha256": m.audio_sha, "stars": m.stars,
                    "clickF1": f1(predicted, y[:, 0].astype(bool)),
                    "predictedHeads": int(predicted.sum()), "humanHeads": int(heads.sum()),
                    "typeAccuracyAtHumanHeads": float(np.mean(models[1].predict(x[heads]) == y[heads, 1])),
                    "sliderDurationMaeGridAtHumanSliders": float(np.mean(np.abs(
                        np.exp2(models[2].predict(x[sliders])) - np.exp2(y[sliders, 2])))) if sliders.any() else None,
                    "spacingMaeRadiiAtHumanHeads": float(np.mean(np.abs(
                        np.expm1(models[3].predict(x[spacing])) - np.expm1(y[spacing, 3])))) if spacing.any() else None})
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("dataset"); ap.add_argument("analysis_root"); ap.add_argument("output")
    ap.add_argument("--allow-partial", action="store_true", help="Explicit smoke experiment while analysis runs")
    ap.add_argument("--iterations", type=int, default=120)
    ap.add_argument("--max-rows", type=int, default=400_000)
    args = ap.parse_args()
    if args.iterations < 1 or args.max_rows < 1:
        ap.error("iterations and max-rows must be positive")
    output = Path(args.output)
    if output.exists():
        ap.error("Use a new output directory to preserve previous experiments")
    lineage = Lineage({}, [])
    maps = aligned_maps(args.dataset, args.analysis_root, lineage, {"train", "validation"})
    # Missing analysis is an incomplete experiment, never silently a full-dataset model.
    if lineage.skipped["no audio analysis"] and not args.allow_partial:
        ap.error("Audio analyses are missing; wait for the batch or explicitly use --allow-partial")
    # Spinner heads/occupancy cannot currently be represented by this model.
    usable = [m for m in maps if not m.spinners and m.off_grid <= 0.05 * (len(m.objects) + m.off_grid)]
    train = [m for m in usable if m.split == "train"]
    validation = [m for m in usable if m.split == "validation"]
    if not train or not validation:
        ap.error("Need usable train and validation maps")
    if args.max_rows < len(train):
        ap.error("max-rows must allow at least one row per training map")
    report = {"features": VERSION, "partial": bool(args.allow_partial),
              "lineage": {"manifests": lineage.manifests, "rights": lineage.rights,
                          "analyses": lineage.analyses, "skipped": dict(lineage.skipped)},
              "maps": dict(Counter(m.split for m in usable)), "excludedSpinnerOrOffGridMaps": len(maps) - len(usable),
              "trainingMapHashes": [m.map_sha for m in train], "validationMapHashes": [m.map_sha for m in validation],
              "configuration": {"iterations": args.iterations, "maxRows": args.max_rows, "seed": 7319},
              "variants": {}, "limits": "Human timing and mapped span supplied. Type/duration/spacing metrics are conditional on human heads, not end-to-end quality. Test not evaluated. Older test songs were exposed during earlier development. HPSS is not instrument separation. Coordinates and playability constraints remain procedural."}
    output.mkdir(parents=True)
    with threadpool_limits(limits=2):
        for hpss in (False, True):
            name = "mixture-hpss" if hpss else "mixture"
            print(f"Fitting {name}: {len(train)} train, {len(validation)} validation maps", flush=True)
            models, rows = fit(train, hpss, args.iterations, args.max_rows)
            target = output / f"{name}.joblib"
            joblib.dump({"features": VERSION, "hpss": hpss, "models": models,
                         "names": feature_names() + ([f"{k}@{d:+d}" for d in CONTEXT for k in HPSS] if hpss else [])}, target)
            report["variants"][name] = {"sha256": _sha256(target), "rows": rows,
                                       "validation": evaluate(models, validation, hpss)}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(output / "report.json", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
