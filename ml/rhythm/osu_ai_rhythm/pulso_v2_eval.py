"""Evaluate trained Pulso v2 bundles on the validation split with different head decodings.

    python -m osu_ai_rhythm.pulso_v2_eval DATASET ANALYSES OUT.json BUNDLE [BUNDLE...] [--decodings windows,global,independent]

The same maps and metrics as the trainer's report; nothing is fitted. The test split is not opened.
"""

from __future__ import annotations

import argparse
import json
import sys

import joblib
from threadpoolctl import threadpool_limits

from .aligned import aligned_maps
from .data import _sha256
from .pulso_v2 import evaluate_map, summarise


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dataset"); ap.add_argument("analysis_root"); ap.add_argument("out")
    ap.add_argument("bundles", nargs="+")
    ap.add_argument("--decodings", default="windows,global,independent")
    ap.add_argument("--threads", type=int, default=2)
    args = ap.parse_args()

    maps = aligned_maps(args.dataset, args.analysis_root, splits={"validation"})
    validation = [m for m in maps if m.off_grid <= 0.05 * (len(m.objects) + m.off_grid)]
    report = {"validationMaps": len(validation), "results": {}}
    with threadpool_limits(limits=args.threads):
        for path in args.bundles:
            bundle = joblib.load(path)
            for decoding in args.decodings.split(","):
                variant = {**bundle, "arHead": None} if decoding == "independent" else {**bundle, "arDecoding": decoding}
                rows = [evaluate_map(variant, m, "v2") for m in validation]
                key = f"{path} [{decoding}]"
                report["results"][key] = {"sha256": _sha256(path), "validation": summarise(rows)}
                print(key, json.dumps(report["results"][key]["validation"]["endToEnd"]), file=sys.stderr, flush=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
