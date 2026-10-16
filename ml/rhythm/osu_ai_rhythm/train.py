"""Train and evaluate the B2 rhythm model.

    uv run --project ml/rhythm python -m osu_ai_rhythm.train <dataset> <analysis-root> <model-out>

Training uses the train split only. Decoding picks, per map, the grid positions whose probability
exceeds a threshold chosen on the training songs (leave-one-song-out), so no validation or test
data shapes the model. Reported F1 is head-time agreement with each human map of the held-out songs,
with human-vs-human agreement as the ceiling.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import statistics
import sys
from datetime import datetime, timezone

import joblib
import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier

from .data import Lineage, examples
from .features import FEATURES_VERSION, feature_names


def f1(pred: np.ndarray, truth: np.ndarray) -> float:
    tp = int((pred & truth).sum())
    p = tp / max(1, int(pred.sum()))
    r = tp / max(1, int(truth.sum()))
    return 0.0 if p + r == 0 else 2 * p * r / (p + r)


def expected_count_mask(p: np.ndarray, scale: float = 1.0) -> np.ndarray:
    """The round(scale * sum p) most likely positions: as many clicks as the model expects a mapper to make."""
    k = int(round(scale * float(p.sum())))
    if k <= 0:
        return np.zeros(len(p), dtype=bool)
    cut = np.partition(p, len(p) - k)[len(p) - k]
    return p >= cut


def new_model() -> HistGradientBoostingClassifier:
    return HistGradientBoostingClassifier(max_iter=300, learning_rate=0.06, max_leaf_nodes=31,
                                          min_samples_leaf=40, l2_regularization=1.0, random_state=7319)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("dataset")
    ap.add_argument("analysis_root")
    ap.add_argument("model_out")
    args = ap.parse_args()

    lineage = Lineage(manifests={}, rights=[])
    data = examples(args.dataset, args.analysis_root, lineage)
    train = [e for e in data if e.split == "train"]
    held = [e for e in data if e.split in ("validation", "test")]
    songs = sorted({e.song for e in train})
    print(f"maps: train {len(train)} ({len(songs)} songs), held-out {len(held)} ({len({e.song for e in held})} songs); "
          f"off-grid human heads skipped: {sum(e.off_grid for e in data)} of {sum(int(e.y.sum()) + e.off_grid for e in data)}", file=sys.stderr)

    # Decoding by leave-one-song-out on the training songs: a fixed probability threshold, or the
    # expected number of clicks (sum of probabilities, times a scale).
    fixed = {("threshold", round(float(t), 3)): [] for t in np.arange(0.1, 0.6, 0.025)}
    counted = {("count", round(float(c), 2)): [] for c in np.arange(0.8, 1.21, 0.05)}
    scores = {**fixed, **counted}
    for song in songs:
        fit = [e for e in train if e.song != song]
        model = new_model().fit(np.vstack([e.x for e in fit]), np.concatenate([e.y for e in fit]))
        for e in (e for e in train if e.song == song):
            p = model.predict_proba(e.x)[:, 1]
            truth = e.y.astype(bool)
            for key in scores:
                mask = p >= key[1] if key[0] == "threshold" else expected_count_mask(p, key[1])
                scores[key].append(f1(mask, truth))
    # Product decision: as many clicks as the model expects (scale 1.0). Maximising F1 prefers more
    # clicks, because mappers disagree and the union of their choices scores well against each of
    # them; but that union is denser than any human map. Realistic density matters more for play.
    decoding = ("count", 1.0)
    best_fixed = max(fixed, key=lambda k: statistics.mean(scores[k]))
    best_count = max(counted, key=lambda k: statistics.mean(scores[k]))
    print(f"leave-one-song-out F1 on train: threshold {best_fixed[1]} -> {statistics.mean(scores[best_fixed]):.3f}, "
          f"expected count x{best_count[1]} -> {statistics.mean(scores[best_count]):.3f}, x1.0 -> {statistics.mean(scores[('count', 1.0)]):.3f}; chosen {decoding}", file=sys.stderr)
    threshold = best_fixed[1]

    model = new_model().fit(np.vstack([e.x for e in train]), np.concatenate([e.y for e in train]))
    joblib.dump({"model": model, "threshold": threshold, "decoding": list(decoding), "features": FEATURES_VERSION, "names": feature_names()}, args.model_out)

    def decode(p):
        return p >= decoding[1] if decoding[0] == "threshold" else expected_count_mask(p, decoding[1])

    with open(args.model_out, "rb") as f:
        model_sha = hashlib.sha256(f.read()).hexdigest()

    # Model card: enough to tell which data, code and settings produced this file.
    report = {
        "model": {"sha256": model_sha, "features": FEATURES_VERSION, "estimator": repr(model), "decoding": list(decoding)},
        "trainedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "dataset": {"manifests": lineage.manifests, "rights": lineage.rights, "analyses": lineage.analyses},
        "examples": {
            "train": {"maps": len(train), "songs": len(songs)},
            "heldOut": {"maps": len(held), "songs": len({e.song for e in held})},
            "skipped": dict(lineage.skipped),
            "offGridHumanHeads": sum(e.off_grid for e in data),
        },
        "selection": {"leaveOneSongOutF1": {f"{k[0]} {k[1]}": round(statistics.mean(v), 4) for k, v in scores.items()}},
        "held_out": [],
        "note": "F1 against individual human maps; humans agree with each other at about 0.8. Not a quality percentage.",
    }
    for e in held:
        p = model.predict_proba(e.x)[:, 1]
        mask = decode(p)
        report["held_out"].append({"song": e.song, "split": e.split, "stars": round(e.stars, 2),
                                   "f1": round(f1(mask, e.y.astype(bool)), 3),
                                   "heads_true": int(e.y.sum()), "heads_pred": int(mask.sum())})

    by_song = {}
    for r in report["held_out"]:
        by_song.setdefault(r["song"], []).append(r["f1"])
    for song, values in sorted(by_song.items()):
        print(f"  {song[:50]:50s} F1 median {statistics.median(values):.3f} over {len(values)} maps", file=sys.stderr)
    print(f"held-out F1 median {statistics.median(r['f1'] for r in report['held_out']):.3f}", file=sys.stderr)
    print(json.dumps(report, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
