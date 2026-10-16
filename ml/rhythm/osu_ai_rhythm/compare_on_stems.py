"""Compare Pulso bundles on the validation maps whose songs have separated stems.

    python -m osu_ai_rhythm.compare_on_stems <dataset> <analysis-root> <out.json> <name=bundle[@style]>...

Every bundle is decoded on exactly the same maps (those a stems model can read), so a stems model
(pulso-features-5) is compared with the others on equal terms. Test split unopened.
"""

from __future__ import annotations

import json
import sys

import joblib
from threadpoolctl import threadpool_limits

from .aligned import aligned_maps
from .pulso_v2 import evaluate_map, summarise


def main() -> int:
    dataset, root, out = sys.argv[1:4]
    maps = [m for m in aligned_maps(dataset, root, splits={"validation"})
            if "stems" in m.analysis and m.off_grid <= 0.05 * (len(m.objects) + m.off_grid)]
    report = {"maps": len(maps), "songs": len({m.audio_sha for m in maps}), "results": {}}
    print(f"{len(maps)} validation maps with stems, {report['songs']} songs", file=sys.stderr, flush=True)
    with threadpool_limits(limits=4):
        for spec in sys.argv[4:]:
            name, rest = spec.split("=", 1)
            path, _, style = rest.partition("@")
            s = summarise([evaluate_map(joblib.load(path), m, "v2", style or "auto") for m in maps])
            report["results"][name] = {"bundle": path, "style": style or "auto", "validation": s}
            e = s["endToEnd"]
            print(f"{name}: F1 {s['clickF1']} vocal {s['vocalHeadRecall']} type {s['typeAtHumanHeads']['accuracy']} | "
                  f"gapL1 {e['gapHistogramL1']} slider {e['sliderShare']['meanAbsError']} contrast {e['densityLoudOverQuiet']}", file=sys.stderr, flush=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
