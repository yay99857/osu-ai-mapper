"""Train and evaluate Pulso-S.

    python -m osu_ai_sequence.train DATASET ANALYSES OUTPUT_DIR [--epochs N] [--threads N]

Fits on the train split, keeps the epoch with the lowest validation loss, then decodes every
validation map from scratch (human timing and mapped span, as for Pulso) and reports the same
end-to-end measures as osu_ai_rhythm.pulso_v2, so the two can be compared directly. Test unopened.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np
import torch
from torch import nn

from osu_ai_rhythm.aligned import aligned_maps, grid_arrays
from osu_ai_rhythm.pulso_v2 import contrast_features, map_profile, playing_mask, summarise, tick_seconds
from osu_ai_rhythm.train import f1

from .data import BODY, SLIDER, CIRCLE, FEATURES, build, load
from .decode import generate, normalise, teacher_forced
from .model import WINDOW, PulsoS, shifted_inputs

SEED = 7319


def windows(seqs, rng, stride=WINDOW):
    out = []
    for s_i, s in enumerate(seqs):
        n = len(s.cls)
        offset = int(rng.integers(0, stride)) if n > WINDOW else 0
        for start in range(offset - stride if offset else 0, max(1, n - WINDOW // 4), stride):
            out.append((s_i, max(0, start)))
    rng.shuffle(out)
    return out


def batch(seqs, items, stats, rng, noise: float):
    b = len(items)
    f = seqs[0].x.shape[1]
    x = np.zeros((b, WINDOW, f), dtype=np.float32)
    cls = np.zeros((b, WINDOW), dtype=np.int64)
    length = np.zeros((b, WINDOW), dtype=np.int64)
    dist = np.full((b, WINDOW), np.nan, dtype=np.float32)
    mask = np.zeros((b, WINDOW), dtype=bool)
    for i, (s_i, start) in enumerate(items):
        s = seqs[s_i]
        part = slice(start, min(len(s.cls), start + WINDOW))
        n = part.stop - part.start
        x[i, :n] = np.clip((s.x[part].astype(np.float32) - stats["mean"]) / stats["std"], -8, 8)
        cls[i, :n] = s.cls[part]
        length[i, :n] = s.length[part]
        dist[i, :n] = s.dist[part]
        mask[i, :n] = s.mask[part]
    cls_t = torch.from_numpy(cls)
    # History noise: the decoder must cope with its own mistakes at inference.
    history = cls_t.clone()
    if noise:
        flip = torch.from_numpy(rng.random(history.shape) < noise)
        history = torch.where(flip & (history == 0), torch.ones_like(history), torch.where(flip & ((history == 1) | (history == 2)), torch.zeros_like(history), history))
    previous, since = shifted_inputs(history)
    return torch.from_numpy(x), cls_t, torch.from_numpy(length), torch.from_numpy(dist), torch.from_numpy(mask), previous, since


def loss_of(model, x, cls, length, dist, mask, previous, since):
    logits, length_logits, d = model(x, previous, since)
    decide = mask & (cls != BODY)
    target = torch.where(cls == BODY, torch.zeros_like(cls), cls)
    loss = nn.functional.cross_entropy(logits[decide], target[decide])
    sl = decide & (cls == SLIDER) & (length > 0)
    if sl.any():
        loss = loss + 0.5 * nn.functional.cross_entropy(length_logits[sl], length[sl])
    has = decide & ((cls == CIRCLE) | (cls == SLIDER)) & torch.isfinite(dist)
    if has.any():
        loss = loss + 0.5 * nn.functional.mse_loss(d[has], dist[has])
    return loss


def evaluate_map(model, stats, seq, m, threshold):
    """The pulso_v2 row format, so summarise() compares Pulso-S with Pulso on the same terms."""
    x = normalise(seq.x, stats)
    idx = seq.idx
    energy = {g["index"]: g["energy"] for g in m.analysis["grid"]}
    gate = np.array([energy.get(int(i), 0) >= 0.05 for i in idx])
    cls, p_head, p_slider, length, dist = generate(model, x, gate, threshold)
    heads = (cls == CIRCLE) | (cls == SLIDER)
    human = (seq.cls == CIRCLE) | (seq.cls == SLIDER)
    truth = seq.cls[human] == SLIDER
    pr = np.clip(teacher_forced(model, x, seq.cls.astype(np.int64))[human], 1e-6, 1 - 1e-6)
    _, g = grid_arrays(m.analysis)
    rel = contrast_features(m.analysis, idx)[:, 2]
    perc = g["percussive"][np.clip(idx, 0, len(g["percussive"]) - 1)]
    vocal = human & (rel >= 1.5) & (perc < np.median(g["percussive"]))
    human_dist = np.expm1(seq.dist[human & np.isfinite(seq.dist)])
    return {"map": m.map_sha, "song": m.audio_sha[:12], "stars": round(m.stars, 2), "clickF1": f1(heads, human),
            "distanceRadii": {"model": float(np.median(dist[heads])) if heads.any() else float("nan"),
                              "human": float(np.median(human_dist)) if len(human_dist) else float("nan")},
            "vocalHeadRecall": float((heads & vocal).sum() / vocal.sum()) if vocal.sum() else float("nan"),
            "typeAtHumanHeads": {"accuracy": float(np.mean((pr >= 0.5) == truth)), "brier": float(np.mean((pr - truth) ** 2)),
                                 "logLoss": float(-np.mean(truth * np.log(pr) + (~truth) * np.log(1 - pr))),
                                 "predictedSliderShare": float(np.mean(pr >= 0.5)), "humanSliderShare": float(truth.mean()),
                                 "probabilities": pr.round(3).tolist(), "truth": truth.astype(int).tolist()},
            "human": map_profile(idx, human, truth, seq.length[human], m.analysis),
            "model": map_profile(idx, heads, cls[heads] == SLIDER, length[heads], m.analysis)}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dataset"); ap.add_argument("analysis_root"); ap.add_argument("output")
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--threads", type=int, default=10)
    ap.add_argument("--batch", type=int, default=24)
    ap.add_argument("--threshold", type=float, default=0.5)
    ap.add_argument("--cache", default="artifacts/sequence/cache")
    ap.add_argument("--max-train", type=int, help="smoke runs: at most this many training maps")
    ap.add_argument("--resume", action="store_true", help="continue from OUTPUT_DIR/checkpoint.pt")
    ap.add_argument("--skip-eval", action="store_true", help="save the model without decoding validation maps")
    ap.add_argument("--eval-per-song", type=int, default=2, help="decoding is slow (~40 s a map); validation maps decoded per song")
    args = ap.parse_args()
    out = Path(args.output)
    if out.exists() and not args.resume:
        ap.error("Use a new output directory to preserve previous experiments, or --resume")
    out.mkdir(parents=True, exist_ok=True)
    torch.manual_seed(SEED)
    torch.set_num_threads(args.threads)
    rng = np.random.default_rng(SEED)

    print("caching sequences", file=sys.stderr, flush=True)
    shas = build(args.dataset, args.analysis_root, args.cache)
    seqs = [load(args.cache, s) for s in shas]
    train = [s for s in seqs if s.split == "train"]
    val = [s for s in seqs if s.split == "validation"]
    if args.max_train:
        train = train[:args.max_train]
    print(f"train {len(train)} maps, validation {len(val)} maps", file=sys.stderr, flush=True)

    allx = np.concatenate([s.x[::7].astype(np.float32) for s in train])
    stats = {"mean": allx.mean(0), "std": allx.std(0) + 1e-3}
    del allx
    model = PulsoS(train[0].x.shape[1])
    opt = torch.optim.AdamW(model.parameters(), lr=3e-4, weight_decay=0.01)
    print(f"parameters {sum(p.numel() for p in model.parameters()):,}", file=sys.stderr, flush=True)

    best, best_state, history, first = float("inf"), None, [], 0
    checkpoint = out / "checkpoint.pt"
    if args.resume and checkpoint.exists():
        saved = torch.load(checkpoint, map_location="cpu", weights_only=False)
        model.load_state_dict(saved["state"])
        opt.load_state_dict(saved["optimizer"])
        best, best_state, history = saved["best"], saved["bestState"], saved["history"]
        stats = {k: np.array(v, dtype=np.float32) for k, v in saved["stats"].items()}
        rng = np.random.default_rng(SEED + len(history))
        first = len(history)
        print(f"resuming after epoch {first} (best validation {best:.4f})", file=sys.stderr, flush=True)

    def save_bundle(state):
        torch.save({"kind": "pulso-s", "features": FEATURES, "config": model.config, "state": state,
                    "stats": {k: v.tolist() for k, v in stats.items()}, "threshold": args.threshold,
                    "styleTable": [[s.stars, s.style] for s in train]}, out / "pulso-s.pt")

    val_items = windows(val, np.random.default_rng(1), stride=WINDOW)
    for epoch in range(first, args.epochs):
        model.train()
        started, losses = time.time(), []
        items = windows(train, rng)
        for k in range(0, len(items), args.batch):
            loss = loss_of(model, *batch(train, items[k:k + args.batch], stats, rng, 0.05))
            opt.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            opt.step()
            losses.append(loss.item())
        model.eval()
        with torch.no_grad():
            vl = float(np.mean([loss_of(model, *batch(val, val_items[k:k + 32], stats, rng, 0.0)).item() for k in range(0, len(val_items), 32)]))
        history.append({"epoch": epoch + 1, "train": float(np.mean(losses)), "validation": vl, "seconds": round(time.time() - started)})
        print(json.dumps(history[-1]), file=sys.stderr, flush=True)
        if vl < best:
            best, best_state = vl, {k: v.detach().clone() for k, v in model.state_dict().items()}
            save_bundle(best_state)  # usable at any time, even if the run is stopped
        torch.save({"state": model.state_dict(), "optimizer": opt.state_dict(), "best": best, "bestState": best_state,
                    "history": history, "stats": {k: v.tolist() for k, v in stats.items()}}, checkpoint)
        if epoch - min(range(len(history)), key=lambda i: history[i]["validation"]) >= 3:
            break
    model.load_state_dict(best_state)
    save_bundle(best_state)
    if args.skip_eval:
        return 0

    print("decoding validation maps", file=sys.stderr, flush=True)
    by_sha = {m.map_sha: m for m in aligned_maps(args.dataset, args.analysis_root, splits={"validation"})}
    # Up to N maps per validation song, nearest the song's median stars; the hashes are reported so
    # other models can be compared on exactly these maps.
    chosen = []
    for song in sorted({s.song for s in val}):
        maps = sorted((s for s in val if s.song == song and s.map_sha in by_sha), key=lambda s: s.stars)
        mid = len(maps) // 2
        chosen += sorted(maps, key=lambda s: abs(maps.index(s) - mid))[:args.eval_per_song]
    rows = []
    for k, s in enumerate(chosen, 1):
        started = time.time()
        rows.append(evaluate_map(model, stats, s, by_sha[s.map_sha], args.threshold))
        print(f"  decoded {k}/{len(chosen)} in {time.time() - started:.0f}s", file=sys.stderr, flush=True)
    report = {"model": "pulso-s", "features": FEATURES, "config": model.config, "epochs": history, "bestValidationLoss": best,
              "maps": {"train": len(train), "validation": len(val), "decoded": len(rows)}, "threshold": args.threshold,
              "decodedMapHashes": [s.map_sha for s in chosen],
              "validation": summarise(rows),
              "limits": "Human timing and mapped span supplied. Validation only; test not opened. Macro over songs."}
    (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    e = report["validation"]
    print(f"validation: F1 {e['clickF1']} vocal {e['vocalHeadRecall']} type {e['typeAtHumanHeads']['accuracy']} | {json.dumps(e['endToEnd'])}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
