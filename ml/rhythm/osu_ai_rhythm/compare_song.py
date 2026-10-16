"""Compare what several rhythm models choose for one analysed song.

    python -m osu_ai_rhythm.compare_song <analysis.json> <stars> <model> [<model>...]

Prints, per model, how many heads it chooses, heads per beat, the gaps between heads and the slider
share. Diagnostic only: it shows the shape of the rhythm, not whether it is good.
"""

from __future__ import annotations

import json
import sys
from collections import Counter

import joblib
import numpy as np

from .features import features


def main() -> int:
    if len(sys.argv) < 4:
        print(__doc__, file=sys.stderr)
        return 2
    analysis = json.load(open(sys.argv[1], encoding="utf-8"))
    stars = float(sys.argv[2])
    beats = analysis["audio"]["durationUs"] / analysis["timing"]["beatLengthUs"]
    for path in sys.argv[3:]:
        bundle = joblib.load(path)
        kind = bundle["features"]
        sliders = None
        if kind in ("pulso-features-2", "pulso-features-3", "pulso-features-4", "pulso-features-5"):
            from .pulso_v2 import predict
            r = predict(bundle, analysis, stars)
            idx = r["indices"]
            room = [b - a - 1 for a, b in zip(idx, idx[1:])] + [16]
            sliders = sum(n["sliderProbability"] >= 0.5 and min(n["durationGrid"], room[i]) >= 1 for i, n in enumerate(r["notes"]))
        elif kind == "pulso-features-1":
            from .pulso import predict
            r = predict(bundle, analysis, stars)
            idx = r["indices"]
            room = [b - a - 1 for a, b in zip(idx, idx[1:])] + [16]
            sliders = sum(n["sliderProbability"] >= 0.5 and min(n["durationGrid"], room[i]) >= 1 for i, n in enumerate(r["notes"]))
        else:
            indices, x = features(analysis, stars)
            p = bundle["model"].predict_proba(x)[:, 1]
            k = int(round(float(p.sum())))
            idx = sorted(int(i) for i in indices[np.argsort(-p, kind="stable")[:k]])
        gaps = Counter(min(b - a, 9) for a, b in zip(idx, idx[1:]))
        n = max(1, len(idx) - 1)
        share = ", ".join(f"{'9+' if g == 9 else g}:{100 * c / n:.0f}%" for g, c in sorted(gaps.items()))
        print(f"{path}\n  {kind}: {len(idx)} heads, {len(idx) / beats:.2f} per beat; gaps in 1/4 ticks {share}"
              + (f"; sliders {100 * sliders / max(1, len(idx)):.0f}%" if sliders is not None else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
