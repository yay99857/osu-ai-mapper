"""Fine-tune Pulso-S on one mapper's maps.

    python -m osu_ai_sequence.finetune DATASET ANALYSES BASE.pt OUTPUT_DIR --mapper NAME [--rights ID]

A mapper's difficulties are the ones whose name carries the mapper's name ("Sotarks' Insane") or,
in a set the mapper hosts, the ones without another mapper's name. Training continues from the
general model on the mapper's train-split maps mixed with general maps (so the basics are not
forgotten), stops on the mapper's validation maps, and reports, on the mapper's held-out maps, how
close the general and the fine-tuned model each come to what the mapper did.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path

import numpy as np
import torch

from osu_ai_rhythm.aligned import aligned_maps
from osu_ai_rhythm.pulso_v2 import summarise

from .cli import load as load_bundle
from .data import build, load
from .model import WINDOW
from .train import batch, evaluate_map, loss_of, windows

SEED = 7319
GUEST = re.compile(r"'s |'\s|’s |&")


def mapper_maps(dataset: str, mapper: str, rights: str | None, fetch_manifests: list[str] = ()) -> set[str]:
    """The mapper's difficulties: those named in a tools/map-fetch manifest (exact, from the osu! API's
    difficulty owners), plus, in other packages, those whose name or set host says so."""
    owned = {}
    for path in fetch_manifests:
        for p in json.load(open(path, encoding="utf-8"))["packages"]:
            owned[p["sha256"]] = set(p["mapperDifficulties"])
    out = set()
    for line in open(os.path.join(dataset, "manifests", "maps.jsonl"), encoding="utf-8"):
        m = json.loads(line)
        if m.get("status") != "accepted" or (rights and m.get("rightsRef") != rights):
            continue
        version = m.get("version", "")
        if m.get("packageSha256") in owned:
            if version in owned[m["packageSha256"]]:
                out.add(m["mapSha256"])
        elif mapper.lower() in version.lower() or (m.get("creator", "").lower() == mapper.lower() and not GUEST.search(version)):
            out.add(m["mapSha256"])
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dataset"); ap.add_argument("analysis_root"); ap.add_argument("base"); ap.add_argument("output")
    ap.add_argument("--mapper", required=True)
    ap.add_argument("--rights")
    ap.add_argument("--fetch-manifest", action="append", default=[], help="tools/map-fetch manifest naming the mapper's difficulties")
    ap.add_argument("--epochs", type=int, default=8)
    ap.add_argument("--lr", type=float, default=1e-4)
    ap.add_argument("--general-share", type=float, default=0.3)
    ap.add_argument("--threads", type=int, default=10)
    ap.add_argument("--cache", default="artifacts/sequence/cache")
    args = ap.parse_args()
    out = Path(args.output)
    if out.exists():
        ap.error("Use a new output directory")
    torch.manual_seed(SEED)
    torch.set_num_threads(args.threads)
    rng = np.random.default_rng(SEED)

    style_shas = mapper_maps(args.dataset, args.mapper, args.rights, args.fetch_manifest)
    shas = build(args.dataset, args.analysis_root, args.cache, splits=("train", "validation", "test"))
    seqs = {s: load(args.cache, s) for s in shas}
    own = [seqs[s] for s in shas if s in style_shas]
    own_train = [s for s in own if s.split == "train"]
    own_val = [s for s in own if s.split == "validation"]
    own_held = [s for s in own if s.split != "train"]
    general = [s for s in seqs.values() if s.split == "train" and s.map_sha not in style_shas]
    print(f"{args.mapper}: {len(own_train)} train, {len(own_val)} validation, {len(own_held)} held out; general pool {len(general)}", file=sys.stderr, flush=True)
    if not own_train or not own_val:
        ap.error("Need the mapper's maps in both the train and validation splits")

    bundle, model, stats = load_bundle(args.base)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01)

    def val_loss():
        model.eval()
        items = windows(own_val, np.random.default_rng(1))
        with torch.no_grad():
            return float(np.mean([loss_of(model, *batch(own_val, items[k:k + 16], stats, rng, 0.0)).item() for k in range(0, len(items), 16)]))

    history = [{"epoch": 0, "validation": val_loss()}]
    print(json.dumps(history[-1]), file=sys.stderr, flush=True)
    best, best_state = history[0]["validation"], {k: v.detach().clone() for k, v in model.state_dict().items()}
    for epoch in range(1, args.epochs + 1):
        model.train()
        started = time.time()
        mix_general = list(rng.choice(len(general), size=max(1, int(len(own_train) * args.general_share / (1 - args.general_share))), replace=False))
        pool = own_train + [general[i] for i in mix_general]
        items = windows(pool, rng)
        losses = []
        for k in range(0, len(items), 16):
            loss = loss_of(model, *batch(pool, items[k:k + 16], stats, rng, 0.05))
            opt.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            opt.step()
            losses.append(loss.item())
        vl = val_loss()
        history.append({"epoch": epoch, "train": float(np.mean(losses)), "validation": vl, "seconds": round(time.time() - started)})
        print(json.dumps(history[-1]), file=sys.stderr, flush=True)
        if vl < best:
            best, best_state = vl, {k: v.detach().clone() for k, v in model.state_dict().items()}
    model.load_state_dict(best_state)

    out.mkdir(parents=True)
    tuned = {**bundle, "state": model.state_dict(), "name": f"Pulso-S {args.mapper}", "mapper": args.mapper,
             "styleTable": [[s.stars, s.style] for s in own_train]}
    torch.save(tuned, out / f"pulso-s-{args.mapper.lower()}.pt")

    # The mapper's held-out maps, decoded by the general and by the tuned model.
    by_sha = {m.map_sha: m for m in aligned_maps(args.dataset, args.analysis_root, splits={"validation", "test"})}
    _, general_model, _ = load_bundle(args.base)
    report = {"mapper": args.mapper, "epochs": history, "maps": {"train": len(own_train), "validation": len(own_val), "heldOut": len(own_held)},
              "heldOutMapHashes": [s.map_sha for s in own_held], "results": {}}
    for name, mdl in (("general", general_model), ("tuned", model)):
        rows = [evaluate_map(mdl, stats, s, by_sha[s.map_sha], bundle.get("threshold", 0.5)) for s in own_held if s.map_sha in by_sha]
        report["results"][name] = summarise(rows)
        report["results"][name]["distanceRadii"] = {k: float(np.nanmean([r["distanceRadii"][k] for r in rows])) for k in ("model", "human")}
        e = report["results"][name]
        print(f"{name}: F1 {e['clickF1']} type {e['typeAtHumanHeads']['accuracy']} distance {e['distanceRadii']} | {json.dumps(e['endToEnd'])}", file=sys.stderr, flush=True)
    (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
