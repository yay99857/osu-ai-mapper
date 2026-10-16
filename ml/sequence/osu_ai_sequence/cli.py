"""osu-ai-sequence predict <model.pt> <analysis.json> <stars> <out.json> [jumps|auto|streams]

Decides a whole song with Pulso-S and writes the same contract as the Pulso rhythm models
("pulso-poc-0.1"): chosen grid indices, head probabilities, and per note the slider probability,
duration, spacing importance and learned distance. The generator places them unchanged.
"""

from __future__ import annotations

import hashlib
import json
import sys

import numpy as np
import torch

from osu_ai_rhythm.pulso_v2 import STYLES, fill_pockets, playing_mask, resolve_style, tick_seconds

from .data import BODY, CIRCLE, FEATURES, SLIDER, features
from .decode import generate, normalise
from .model import PulsoS


def load(path: str):
    bundle = torch.load(path, map_location="cpu", weights_only=False)
    if bundle.get("kind") != "pulso-s" or bundle.get("features") != FEATURES:
        raise ValueError(f"{path} is not a Pulso-S model for {FEATURES}")
    model = PulsoS(**bundle["config"])
    model.load_state_dict(bundle["state"])
    model.eval()
    stats = {k: np.array(v, dtype=np.float32) for k, v in bundle["stats"].items()}
    return bundle, model, stats


def predict(path: str, analysis: dict, stars: float, style: str = "auto", threads: int = 4) -> dict:
    if style not in STYLES:
        raise ValueError(f"Unknown style '{style}'; expected one of {', '.join(STYLES)}")
    torch.set_num_threads(threads)
    bundle, model, stats = load(path)
    idx, x = features(analysis, stars, resolve_style(bundle, stars, style))
    energy = {g["index"]: g["energy"] for g in analysis["grid"]}
    gate = np.array([energy.get(int(i), 0) >= 0.05 for i in idx])
    playing = playing_mask(analysis, idx)
    tick = tick_seconds(analysis)
    cls, p_head, p_slider, length, dist = generate(model, normalise(x, stats), gate, bundle.get("threshold", 0.5), playing, tick)
    # Any stretch still empty while the music plays gets circles at its most likely ticks, as Pulso does.
    occupied = (cls == CIRCLE) | (cls == SLIDER)
    filled = fill_pockets(occupied, np.where(gate & (cls != BODY), np.maximum(p_head, 1e-6), 0), playing, tick)
    added = filled & ~occupied
    if added.any():
        cls[added] = CIRCLE
        dist[added] = float(np.median(dist[occupied])) if occupied.any() else 3.0
    heads = np.flatnonzero((cls == CIRCLE) | (cls == SLIDER))
    notes = [{"sliderProbability": float(np.clip(p_slider[h] if cls[h] == SLIDER else min(p_slider[h], 0.49), 0, 1)),
              "durationGrid": int(max(1, length[h])), "importance": float(np.clip(dist[h] / 8, 0, 1)),
              "distanceRadii": float(np.clip(dist[h], 0, 20))} for h in heads]
    with open(path, "rb") as f:
        sha = hashlib.sha256(f.read()).hexdigest()
    return {"schemaVersion": "pulso-poc-0.1", "stars": stars, "style": style,
            "model": {"features": FEATURES, "threshold": 0.0, "sha256": sha, "kind": "pulso-s"},
            "indices": [int(idx[h]) for h in heads], "probabilities": [float(p_head[h]) for h in heads], "notes": notes}


def main() -> int:
    if len(sys.argv) not in (6, 7) or sys.argv[1] != "predict":
        print(__doc__, file=sys.stderr)
        return 2
    model_path, analysis_path, stars, out_path = sys.argv[2:6]
    style = sys.argv[6] if len(sys.argv) == 7 else "auto"
    with open(analysis_path, encoding="utf-8") as f:
        analysis = json.load(f)
    result = predict(model_path, analysis, float(stars), style)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"))
    sliders = sum(n["sliderProbability"] >= 0.5 for n in result["notes"])
    print(f"Pulso-S: {len(result['indices'])} heads, {sliders} sliders", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
