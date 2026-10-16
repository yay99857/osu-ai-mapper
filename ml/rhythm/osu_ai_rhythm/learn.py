"""osu-ai-learn: one short learning round on the installed Pulso model.

    uv run --project ml/rhythm osu-ai-learn [--focus all|recent|mapper:NAME] [--maps 200] [--rounds 30] [--install]

The installed model keeps what it knows: its gradient-boosted heads continue from their trees with
--rounds more, fitted on a fresh sample of human maps (all of them, the most recently added ones,
or one mapper's). Before and after, the same fixed benchmark of validation maps is decoded, so
rounds are comparable with each other. The round reports a score change (+000.54%) and a short
comment on what moved, saves the candidate under artifacts/models/learn/, and with --install
replaces the installed model only if the score went up (the previous one is kept as .bak).
"""

from __future__ import annotations

import argparse
import json
import os
import random
import re
import shutil
import sys
import time
from datetime import datetime

import joblib
import numpy as np
from threadpoolctl import threadpool_limits

from .aligned import aligned_maps
from .pulso_v2 import contrasted, evaluate_map, human_rows, sequence_context, stemmed, style_of, styled, summarise

DATASET = "artifacts/dataset"
ANALYSES = "artifacts/study/analysis"
BENCHMARK = "artifacts/models/learn/benchmark.json"
WEIGHTS = {"clickF1": 0.35, "typeAccuracy": 0.2, "gapFit": 0.2, "sliderFit": 0.15, "contrastFit": 0.1}


class Progress:
    """One bar for the whole round; each step owns a share of it."""

    def __init__(self, steps: dict[str, float]):
        self.steps, self.done, self.start = steps, 0.0, time.time()

    def show(self, step: str, fraction: float = 0.0):
        before = sum(w for k, w in self.steps.items() if list(self.steps).index(k) < list(self.steps).index(step))
        p = min(1.0, (before + self.steps[step] * fraction) / sum(self.steps.values()))
        bar = "#" * int(p * 30) + "-" * (30 - int(p * 30))
        elapsed = time.time() - self.start
        eta = f"~{elapsed / p * (1 - p) / 60:.0f} min" if p > 0.03 else "..."
        sys.stderr.write(f"\r[{bar}] {100 * p:5.1f}%  {step:<38s} faltam {eta:<8s}")
        sys.stderr.flush()

    def finish(self):
        sys.stderr.write(f"\r[{'#' * 30}] 100.0%  {'pronto':<38s} em {(time.time() - self.start) / 60:.1f} min   \n")


def score(summary: dict) -> tuple[float, dict]:
    e = summary["endToEnd"]
    parts = {
        "clickF1": summary["clickF1"] or 0,
        "typeAccuracy": summary["typeAtHumanHeads"]["accuracy"] or 0,
        "gapFit": 1 - (e["gapHistogramL1"] or 1) / 2,
        "sliderFit": 1 - (e["sliderShare"]["meanAbsError"] or 1),
        "contrastFit": 1 - min(1.0, abs((e["densityLoudOverQuiet"]["model"] or 0) - (e["densityLoudOverQuiet"]["human"] or 0)) / 2),
    }
    return sum(WEIGHTS[k] * v for k, v in parts.items()), parts


COMMENTS = {
    "clickF1": ("acerta mais onde os humanos clicam", "erra mais onde os humanos clicam"),
    "typeAccuracy": ("escolhe melhor entre círculo e slider", "escolhe pior entre círculo e slider"),
    "gapFit": ("o ritmo (espaços entre notas) ficou mais humano", "o ritmo (espaços entre notas) ficou menos humano"),
    "sliderFit": ("a quantidade de sliders ficou mais fiel", "a quantidade de sliders ficou menos fiel"),
    "contrastFit": ("o contraste entre trechos calmos e intensos ficou mais humano", "o contraste entre trechos calmos e intensos piorou"),
}


def comment(before: dict, after: dict, focus: str, n_maps: int) -> str:
    deltas = sorted(((after[k] - before[k], k) for k in before), reverse=True)
    good = [f"{COMMENTS[k][0]} ({before[k]:.3f} -> {after[k]:.3f})" for d, k in deltas if d > 0.002][:2]
    bad = [f"{COMMENTS[k][1]} ({before[k]:.3f} -> {after[k]:.3f})" for d, k in deltas[::-1] if d < -0.002][:1]
    source = {"all": "mapas variados", "recent": "os mapas adicionados por último"}.get(focus, f"mapas de {focus.split(':', 1)[-1]}")
    text = f"Estudou {n_maps} {source}. "
    if good:
        text += "Aprendeu: " + "; ".join(good) + ". "
    if bad:
        text += "Cuidado: " + "; ".join(bad) + "."
    if not good and not bad:
        text += "Nada mudou de forma mensurável: o modelo já sabia o que esses mapas ensinam."
    return text.strip()


def benchmark_maps(size: int, path: str = BENCHMARK):
    """A fixed list of validation maps, chosen once, one per song nearest its median stars."""
    if os.path.exists(path):
        wanted = set(json.load(open(path, encoding="utf-8"))["maps"])
        return [m for m in aligned_maps(DATASET, ANALYSES, splits={"validation"}) if m.map_sha in wanted]
    maps = [m for m in aligned_maps(DATASET, ANALYSES, splits={"validation"}) if m.off_grid <= 0.05 * (len(m.objects) + m.off_grid)]
    chosen = []
    for song in sorted({m.audio_sha for m in maps}):
        ms = sorted((m for m in maps if m.audio_sha == song), key=lambda m: m.stars)
        chosen.append(ms[len(ms) // 2])
    chosen = sorted(chosen, key=lambda m: m.map_sha)[:size]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    json.dump({"maps": [m.map_sha for m in chosen], "chosen": datetime.now().isoformat(timespec="seconds")}, open(path, "w", encoding="utf-8"))
    return chosen


def sample_maps(focus: str, n: int, seed: int):
    maps = [m for m in aligned_maps(DATASET, ANALYSES, splits={"train"}) if m.off_grid <= 0.05 * (len(m.objects) + m.off_grid)]
    if focus == "recent":
        order = {}
        for line in open(os.path.join(DATASET, "manifests", "maps.jsonl"), encoding="utf-8"):
            r = json.loads(line)
            order[r["mapSha256"]] = r.get("ingestedAt", "")
        maps.sort(key=lambda m: order.get(m.map_sha, ""), reverse=True)
        return maps[:n]
    if focus.startswith("mapper:"):
        name = focus.split(":", 1)[1].lower()
        own = set()
        for line in open(os.path.join(DATASET, "manifests", "maps.jsonl"), encoding="utf-8"):
            r = json.loads(line)
            v = r.get("version", "")
            if name in v.lower() or (r.get("creator", "").lower() == name and not re.search(r"'s |'\s|’s |&", v)):
                own.add(r["mapSha256"])
        maps = [m for m in maps if m.map_sha in own]
    random.Random(seed).shuffle(maps)
    return maps[:n]


def continue_boosting(model, x, y, rounds: int, progress: Progress, step: str):
    """More trees on top of the existing ones, a few at a time so the bar moves."""
    if model is None or len(np.unique(y)) < 2 and hasattr(model, "classes_"):
        return model
    model.set_params(warm_start=True)
    target = model.max_iter + rounds
    while model.max_iter < target:
        model.set_params(max_iter=min(target, model.max_iter + 5))
        model.fit(x, y)
        progress.show(step, 1 - (target - model.max_iter) / rounds)
    return model


def main() -> int:
    ap = argparse.ArgumentParser(prog="osu-ai-learn", description=__doc__.splitlines()[0])
    ap.add_argument("--model", default="artifacts/models/pulso/pulso.joblib")
    ap.add_argument("--focus", default="all", help="all, recent, or mapper:NAME")
    ap.add_argument("--maps", type=int, default=200)
    ap.add_argument("--rounds", type=int, default=30, help="boosting rounds added to each model")
    ap.add_argument("--bench", type=int, default=24)
    ap.add_argument("--benchmark", default=BENCHMARK, help="file with the fixed list of reference maps")
    ap.add_argument("--seed", type=int, default=int(time.time()) % 100_000)
    ap.add_argument("--install", action="store_true", help="replace the installed model if the score went up")
    args = ap.parse_args()
    for stream in (sys.stdout, sys.stderr):  # Portuguese text in a Windows console
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    progress = Progress({"lendo mapas": 1, "medindo o modelo atual": 3, "preparando exemplos": 2,
                         "aprendendo onde clicar": 4, "aprendendo círculo/slider": 2, "aprendendo distâncias": 1,
                         "medindo o modelo novo": 3})
    with threadpool_limits(limits=4):
        progress.show("lendo mapas")
        bundle = joblib.load(args.model)
        bench = benchmark_maps(args.bench, args.benchmark)
        study = sample_maps(args.focus, args.maps, args.seed)
        if not study:
            print("\nNenhum mapa encontrado para esse foco.", file=sys.stderr)
            return 1

        def measure(b, step):
            rows = []
            for i, m in enumerate(bench):
                rows.append(evaluate_map(b, m, "v2", "auto"))
                progress.show(step, (i + 1) / len(bench))
            return summarise(rows)

        before_summary = measure(bundle, "medindo o modelo atual")

        hx, hy, tx, ty, tl, tsp = [], [], [], [], [], []
        rng = np.random.default_rng(args.seed)
        for i, m in enumerate(study):
            style = style_of(m.objects) if styled(bundle) else None
            idx, x, heads, slider, length, spacing, spun = human_rows(m, bundle["hpss"], style, contrasted(bundle), stemmed(bundle))
            free = np.flatnonzero(~spun)
            take = rng.choice(free, min(1500, len(free)), replace=False)
            hx.append(x[take]); hy.append(heads[take])
            ctx = sequence_context(idx, heads)
            tx.append(np.column_stack((x[heads], ctx[heads]))); ty.append(slider[heads]); tl.append(length[heads]); tsp.append(spacing[heads])
            progress.show("preparando exemplos", (i + 1) / len(study))
        hx, hy, tx, ty, tl, tsp = np.vstack(hx), np.concatenate(hy), np.vstack(tx), np.concatenate(ty), np.concatenate(tl), np.concatenate(tsp)

        candidate = dict(bundle)
        candidate["head"] = continue_boosting(bundle["head"], hx, hy, args.rounds, progress, "aprendendo onde clicar")
        candidate["type"] = continue_boosting(bundle["type"], tx, ty, args.rounds, progress, "aprendendo círculo/slider")
        has = np.isfinite(tsp)
        candidate["spacing"] = continue_boosting(bundle["spacing"], tx[has], tsp[has], max(5, args.rounds // 3), progress, "aprendendo distâncias")
        after_summary = measure(candidate, "medindo o modelo novo")
    progress.finish()

    s0, p0 = score(before_summary)
    s1, p1 = score(after_summary)
    change = 100 * (s1 - s0) / s0
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    out = os.path.join("artifacts", "models", "learn", stamp)
    os.makedirs(out, exist_ok=True)
    candidate.setdefault("learning", []).append({"at": stamp, "focus": args.focus, "maps": len(study), "rounds": args.rounds, "change": change})
    joblib.dump(candidate, os.path.join(out, "candidate.joblib"))
    text = comment(p0, p1, args.focus, len(study))
    json.dump({"focus": args.focus, "maps": [m.map_sha for m in study], "before": p0, "after": p1, "score": [s0, s1], "change": change,
               "comment": text, "benchmark": args.benchmark}, open(os.path.join(out, "report.json"), "w", encoding="utf-8"), indent=2, ensure_ascii=False)

    print(f"Melhoria: {change:+07.2f}%   (pontuação {s0:.4f} -> {s1:.4f}, {len(bench)} mapas de referência)")
    print(text)
    if args.install:
        if change > 0:
            shutil.copy2(args.model, args.model + ".bak")
            shutil.copy2(os.path.join(out, "candidate.joblib"), args.model)
            print(f"Instalado em {args.model} (anterior em {os.path.basename(args.model)}.bak).")
        else:
            print("Não instalado: o modelo atual continua melhor nesses mapas.")
    else:
        print(f"Candidato salvo em {out}; use --install para trocar o modelo quando melhorar.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
