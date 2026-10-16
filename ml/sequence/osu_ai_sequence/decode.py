"""Turning Pulso-S outputs into a map: left to right, window by window, using its own decisions."""

from __future__ import annotations

import numpy as np
import torch

from .data import BODY, CIRCLE, NONE, SLIDER
from .model import SINCE_CAP, WINDOW, PulsoS

STRIDE = 192  # windows overlap by 64 ticks; the overlap is carried over as already decided


def normalise(x: np.ndarray, stats: dict) -> torch.Tensor:
    z = (x.astype(np.float32) - stats["mean"]) / stats["std"]
    return torch.from_numpy(np.clip(z, -8, 8).astype(np.float32))


def _inputs(cls: np.ndarray, start: int, n: int):
    """previous label and ticks since the last head for ticks start..start+n-1 of the decided labels."""
    previous = np.full(n, 4, dtype=np.int64)
    since = np.full(n, SINCE_CAP, dtype=np.int64)
    last = -10_000
    for t in range(max(0, start - SINCE_CAP), start):
        if cls[t] in (CIRCLE, SLIDER):
            last = t
    for k in range(n):
        t = start + k
        if t > 0:
            previous[k] = cls[t - 1]
        since[k] = min(t - last, SINCE_CAP)
        if cls[t] in (CIRCLE, SLIDER):
            last = t
    return previous, since


QUIET_AFTER_S = 1.5  # silence while the music plays before the head threshold starts to drop
FLOOR = 0.03


def stuck_threshold(threshold: float, silent_s: float) -> float:
    """The head threshold after this many seconds without a head while the music plays.

    Pulso-S feeds on its own decisions: after a few beats without a head, "ticks since the last head"
    looks like the inside of a break, so it keeps choosing nothing. The Fate of Ophelia came out
    with 62 s of silence while the song played at its usual loudness. The model still chooses where
    to click; it is only not allowed to fall asleep.
    """
    if silent_s <= QUIET_AFTER_S:
        return threshold
    return max(FLOOR, threshold * float(np.exp(-(silent_s - QUIET_AFTER_S) / 0.75)))


@torch.no_grad()
def generate(model: PulsoS, x: torch.Tensor, gate: np.ndarray, threshold: float = 0.5,
             playing: np.ndarray | None = None, tick_s: float | None = None):
    """Decides every tick of a whole song. Returns labels, head probability, slider probability given
    a head, slider length and distance in radii (the last three at heads). With `playing` and the
    tick length, the threshold drops after a silence the music does not have (stuck_threshold)."""
    model.eval()
    n = len(x)
    cls = np.full(n, -1, dtype=np.int64)
    p_head = np.zeros(n, dtype=np.float32)
    p_slider = np.zeros(n, dtype=np.float32)
    length = np.zeros(n, dtype=np.int64)
    dist = np.zeros(n, dtype=np.float32)
    last_head = -10 ** 9
    start = 0
    while start < n:
        end = min(n, start + WINDOW)
        memory = model.encode(x[start:end][None])
        t0 = start if start == 0 else start + (WINDOW - STRIDE)
        for t in range(t0, end):
            if cls[t] == BODY:
                continue
            k = t - start
            known = np.where(cls[start:t + 1] < 0, NONE, cls[start:t + 1])
            previous, since = _inputs(np.concatenate([np.where(cls[:start] < 0, NONE, cls[:start]), known]), start, k + 1)
            logits, length_logits, d = model.decode(memory, torch.from_numpy(previous)[None], torch.from_numpy(since)[None])
            prob = torch.softmax(logits[0, k], -1).numpy()
            head = float(prob[1] + prob[2]) if gate[t] else 0.0
            p_head[t] = head
            limit = threshold
            if playing is not None and tick_s and playing[t]:
                limit = stuck_threshold(threshold, (t - last_head) * tick_s)
            if head >= limit:
                last_head = t
                p_slider[t] = float(prob[2] / max(head, 1e-9))
                if p_slider[t] >= 0.5:
                    lp = torch.softmax(length_logits[0, k, 1:], -1).numpy()
                    L = int(np.argmax(lp)) + 1
                    room = n - 1 - t
                    L = max(1, min(L, room))
                    cls[t] = SLIDER
                    length[t] = L
                    cls[t + 1:t + L + 1] = BODY
                else:
                    cls[t] = CIRCLE
                dist[t] = float(np.expm1(d[0, k].item()))
            else:
                cls[t] = NONE
        if end == n:
            break
        start += STRIDE
    cls = np.where(cls < 0, NONE, cls)
    return cls, p_head, p_slider, length, dist


@torch.no_grad()
def teacher_forced(model: PulsoS, x: torch.Tensor, cls: np.ndarray):
    """Probabilities with the human labels as the decoder's history (for type metrics at human heads)."""
    model.eval()
    n = len(x)
    p_slider = np.zeros(n, dtype=np.float32)
    for start in range(0, n, WINDOW):
        end = min(n, start + WINDOW)
        previous, since = _inputs(cls, start, end - start)
        logits, _, _ = model(x[start:end][None], torch.from_numpy(previous)[None], torch.from_numpy(since)[None])
        prob = torch.softmax(logits[0], -1).numpy()
        p_slider[start:end] = prob[:, 2] / np.maximum(prob[:, 1] + prob[:, 2], 1e-9)
    return p_slider
