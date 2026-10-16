"""osu-ai-rhythm predict <model> <analysis.json> <stars> <out.json> [jumps|auto|streams]

Writes the probability that a human mapper places a hit object at each 1/4 grid position, for the
given star target. The generator turns these into clicks; this step makes no placement decisions.
The style applies to Pulso bundles conditioned on one (pulso-features-3); others ignore it.
"""

from __future__ import annotations

import hashlib
import json
import sys

import joblib
import numpy as np

from .features import FEATURES_VERSION, features

SCHEMA_VERSION = "rhythm-poc-0.1"


def main() -> int:
    if len(sys.argv) not in (6, 7) or sys.argv[1] != "predict":
        print(__doc__, file=sys.stderr)
        return 2

    model_path, analysis_path, stars, out_path = sys.argv[2:6]
    style = sys.argv[6] if len(sys.argv) == 7 else "auto"
    bundle = joblib.load(model_path)
    if bundle["features"] in ("pulso-features-1", "pulso-features-2", "pulso-features-3", "pulso-features-4", "pulso-features-5"):
        with open(analysis_path, encoding="utf-8") as f:
            analysis = json.load(f)
        from .aligned import with_stems
        analysis = with_stems(analysis, analysis_path)
        if bundle["features"] == "pulso-features-1":
            from .pulso import predict
            result = predict(bundle, analysis, float(stars))
        else:
            from .pulso_v2 import predict
            result = predict(bundle, analysis, float(stars), style)
        with open(model_path, "rb") as f:
            result["model"]["sha256"] = hashlib.sha256(f.read()).hexdigest()
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(result, f, separators=(",", ":"))
        print(f"Pulso: {len(result['indices'])} heads", file=sys.stderr)
        return 0
    if bundle["features"] != FEATURES_VERSION:
        print(f"model was trained on {bundle['features']}, this code computes {FEATURES_VERSION}", file=sys.stderr)
        return 1

    with open(analysis_path, encoding="utf-8") as f:
        analysis = json.load(f)
    indices, x = features(analysis, float(stars))
    p = bundle["model"].predict_proba(x)[:, 1]

    # The generator applies one threshold. For expected-count decoding it is the probability of the
    # k-th most likely position, k being the number of clicks the model expects for this song.
    kind, value = bundle.get("decoding", ["threshold", bundle["threshold"]])
    if kind == "count" and len(p):
        k = max(1, int(round(value * float(p.sum()))))
        threshold = float(np.partition(p, len(p) - k)[len(p) - k])
    else:
        threshold = float(value)

    with open(model_path, "rb") as f:
        model_sha = hashlib.sha256(f.read()).hexdigest()

    result = {
        "schemaVersion": SCHEMA_VERSION,
        "model": {"sha256": model_sha, "features": FEATURES_VERSION, "threshold": threshold, "decoding": [kind, value]},
        "stars": float(stars),
        "indices": [int(i) for i in indices],
        "probabilities": [round(float(v), 4) for v in p],
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"))
    print(f"{int((p >= threshold).sum())} clicks ({kind} {value}, threshold {threshold:.3f})", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
