"""Synthetic drum tracks with known timing. Audio we own, so tests can assert exact answers."""

from __future__ import annotations

import numpy as np
import soundfile as sf

RATE = 44_100


def _hit(kind: str) -> np.ndarray:
    n = int(0.12 * RATE)
    t = np.arange(n) / RATE
    rng = np.random.default_rng(0)
    if kind == "kick":
        freq = 55 + 90 * np.exp(-t * 40)
        return 0.9 * np.sin(2 * np.pi * np.cumsum(freq) / RATE) * np.exp(-t * 25)
    if kind == "snare":
        return 0.5 * rng.standard_normal(n) * np.exp(-t * 35) + 0.3 * np.sin(2 * np.pi * 190 * t) * np.exp(-t * 30)
    if kind == "hat":
        noise = rng.standard_normal(n)
        noise = np.diff(noise, prepend=0)  # crude high-pass
        return 0.15 * noise * np.exp(-t * 90)
    raise ValueError(kind)


def render(path: str, bpm: float, first_downbeat: float, seconds: float, pattern: dict[str, list[float]], pad: float = 0.0) -> None:
    """Writes a mono WAV. Pattern positions are in beats within a 4/4 bar, e.g. {"kick": [0, 2]}."""
    beat = 60.0 / bpm
    total = int((seconds + pad) * RATE)
    out = np.zeros(total)
    bar = 4 * beat
    start = first_downbeat - bar * np.ceil(first_downbeat / bar)  # extend the pattern back to time zero

    t_bar = start
    while t_bar < seconds:
        for kind, positions in pattern.items():
            sound = _hit(kind)
            for p in positions:
                t = t_bar + p * beat
                if t < 0 or t >= seconds:
                    continue
                i = int(round(t * RATE))
                j = min(total, i + len(sound))
                out[i:j] += sound[: j - i]
        t_bar += bar

    out /= max(1.0, np.abs(out).max() / 0.9)
    sf.write(path, out.astype(np.float32), RATE)


# Kick on 1 and the "and" of 3: one unambiguous downbeat per bar.
BASIC_ROCK = {"kick": [0, 2.5], "snare": [1, 3], "hat": [0, 0.5, 1, 1.5, 2, 2.5, 3, 3.5]}

# Funk carioca "tamborzão": kicks in a 3-3-2 sixteenth pattern, the rhythm that led librosa's
# tracker to 2/3 of the real tempo on real music.
TAMBORZAO = {
    "kick": [0, 0.75, 1.5, 2, 2.75, 3.5],
    "snare": [1, 3],
    "hat": [0, 0.5, 1, 1.5, 2, 2.5, 3, 3.5],
}
