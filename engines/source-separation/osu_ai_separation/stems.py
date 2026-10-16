"""Demucs stems sampled on the 1/4 grid of an existing analysis.

The analysis (engines/audio-analysis) fixes the grid; this module only adds, per grid point, how
strongly each separated stem attacks there, plus the vocal stem's loudness and whether a sung note is
held through the next beat. HPSS splits sound into percussive and harmonic parts; voice, guitars and
synths stay mixed in the harmonic one, so sung syllables over quiet drums did not stand out (Beauty
And A Beat, "Nicki Minaj" at 5 s: the head model gave each syllable 2-6%).
"""

from __future__ import annotations

import hashlib

import librosa
import numpy as np

SCHEMA_VERSION = "stems-poc-0.1"
MODEL = "htdemucs"
HOP = 256
STEMS = ("vocals", "drums", "bass", "other")

_model = None


def model():
    global _model
    if _model is None:
        from demucs.pretrained import get_model
        _model = get_model(MODEL)
        _model.eval()
    return _model


def separate(path: str, threads: int = 8) -> tuple[dict[str, np.ndarray], int]:
    """Mono signal of each stem at the model's sample rate."""
    import torch
    from demucs.apply import apply_model

    torch.set_num_threads(threads)
    m = model()
    y, _ = librosa.load(path, sr=m.samplerate, mono=False)
    if y.ndim == 1:
        y = np.stack([y, y])
    wav = torch.from_numpy(y[:2].astype(np.float32))
    ref = wav.mean(0)
    mean, std = ref.mean(), ref.std() + 1e-8
    with torch.no_grad():
        out = apply_model(m, ((wav - mean) / std)[None], device="cpu", split=True, overlap=0.25, progress=False)[0]
    out = out * std + mean
    return {name: out[i].mean(0).numpy() for i, name in enumerate(m.sources)}, m.samplerate


def _flux(y: np.ndarray, sr: int) -> np.ndarray:
    mel = librosa.feature.melspectrogram(y=y, sr=sr, hop_length=HOP, n_mels=64, fmax=16_000)
    log = librosa.power_to_db(mel, ref=np.max(mel) + 1e-12)
    env = np.maximum(np.diff(log, axis=1, prepend=log[:, :1]), 0).mean(axis=0)
    return np.clip(env / (np.percentile(env, 99.5) + 1e-9), 0, 1.5)


def _level(y: np.ndarray) -> np.ndarray:
    rms = librosa.feature.rms(y=y, hop_length=HOP)[0]
    return np.clip(rms / (np.percentile(rms, 99) + 1e-9), 0, 1.5)


def grid_stems(stems: dict[str, np.ndarray], sr: int, analysis: dict) -> list[dict]:
    frame_s = HOP / sr
    onsets = {k: _flux(stems[k], sr) for k in STEMS}
    vocal_level = _level(stems["vocals"])
    n = len(vocal_level)
    radius = max(1, int(0.02 / frame_s))
    beat_s = analysis["timing"]["beatLengthUs"] / 1e6

    def peak(track, f):
        lo, hi = max(0, f - radius), min(len(track), f + radius + 1)
        return float(track[lo:hi].max()) if hi > lo else 0.0

    points = []
    for g in analysis["grid"]:
        t = g["timeUs"] / 1e6
        f = int(round(t / frame_s))
        if f >= n:
            break
        span = slice(f + radius, min(n, int(round((t + beat_s) / frame_s))))
        held = float(vocal_level[span].mean()) if span.stop > span.start else 0.0
        steady = 1.0 - float(np.clip(onsets["vocals"][span].mean() * 3, 0, 1)) if span.stop > span.start else 0.0
        point = {"index": g["index"]}
        for k in STEMS:
            point[k] = round(peak(onsets[k], f), 4)
        point["vocalLevel"] = round(float(vocal_level[f]), 4)
        point["vocalHeld"] = round(held * steady, 4)
        points.append(point)
    return points


def analyse(audio_path: str, analysis: dict, threads: int = 8) -> dict:
    with open(audio_path, "rb") as f:
        sha = hashlib.sha256(f.read()).hexdigest()
    stems, sr = separate(audio_path, threads)
    return {"schemaVersion": SCHEMA_VERSION, "model": MODEL, "sourceAudioSha256": sha,
            "analysisAudioSha256": analysis["audio"]["sha256"], "grid": grid_stems(stems, sr, analysis)}
