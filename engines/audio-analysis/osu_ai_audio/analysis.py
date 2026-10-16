"""Constant-tempo song analysis.

Produces observations (tracker beats, onsets) separately from the selected timing hypothesis, and
features sampled on the 1/4 grid of that hypothesis. Nothing here decides what to map: density and
object choices belong to the mapping plan.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass

import librosa
import numpy as np

SAMPLE_RATE = 44_100
HOP = 256  # 5.8 ms per frame
GRID_DIVISOR = 4
SCHEMA_VERSION = "poc-0.3"


@dataclass
class Signal:
    y: np.ndarray
    onset: np.ndarray  # normalised onset strength per frame
    onset_low: np.ndarray
    onset_high: np.ndarray
    rms: np.ndarray  # normalised loudness per frame
    onset_timing: np.ndarray  # short-window onset strength, used only to locate onsets in time
    # Light harmonic/percussive separation (HPSS). Not stems: "percussive" is mostly drums and
    # plucks, "harmonic" mostly voice, pads, held guitar and bass notes.
    percussive: np.ndarray  # onset strength of the percussive part
    harmonic: np.ndarray  # onset strength of the harmonic part, 200 Hz to 4 kHz (where melodies sit)
    harmonic_level: np.ndarray  # loudness of the harmonic part in that band

    @property
    def frame_seconds(self) -> float:
        return HOP / SAMPLE_RATE

    def frame_at(self, seconds: float) -> int:
        return int(round(seconds / self.frame_seconds))


# A centred STFT frame "hears" an attack before it happens, by up to half its window. Measured on
# synthetic drums (tests/test_analysis.py): about -15 ms with a 2048-sample window, -2 ms with 512.
# Timing therefore uses the short window, minus the residual measured below.
TIMING_N_FFT = 512
TIMING_LATENCY_S = -0.002


def load(path: str) -> Signal:
    y, _ = librosa.load(path, sr=SAMPLE_RATE, mono=True)
    magnitude = np.abs(librosa.stft(y, n_fft=2048, hop_length=HOP)).astype(np.float32)
    bank = librosa.filters.mel(sr=SAMPLE_RATE, n_fft=2048, n_mels=96, fmax=16_000)
    log_mel = librosa.power_to_db(bank @ magnitude**2, ref=np.max)
    harmonic, percussive = librosa.decompose.hpss(magnitude, kernel_size=31)
    del magnitude
    log_perc = librosa.power_to_db(bank @ percussive**2, ref=np.max)
    harmonic_mel = bank @ harmonic**2
    del harmonic, percussive
    centres = librosa.mel_frequencies(n_mels=96, fmax=16_000)
    melodic = (centres >= 200) & (centres <= 4_000)
    log_harm = librosa.power_to_db(harmonic_mel[melodic], ref=np.max)
    harm_level = np.sqrt(harmonic_mel[melodic].sum(axis=0))
    harm_level = harm_level / (np.percentile(harm_level, 99) + 1e-9)

    def flux(spectrum: np.ndarray) -> np.ndarray:
        diff = np.diff(spectrum, axis=1, prepend=spectrum[:, :1])
        env = np.maximum(diff, 0).mean(axis=0)
        return env / (np.percentile(env, 99.5) + 1e-9)

    fine = librosa.feature.melspectrogram(y=y, sr=SAMPLE_RATE, hop_length=HOP, n_fft=TIMING_N_FFT, n_mels=40, fmax=16_000)
    fine_log = librosa.power_to_db(fine, ref=np.max)

    rms = librosa.feature.rms(y=y, hop_length=HOP)[0]
    rms = rms / (np.percentile(rms, 99) + 1e-9)

    return Signal(
        y=y,
        onset=np.clip(flux(log_mel), 0, 1.5),
        # Below roughly 150 Hz: kick and bass, which mark downbeats more than snares do.
        onset_low=np.clip(flux(log_mel[0:10]), 0, 1.5),
        onset_high=np.clip(flux(log_mel[50:96]), 0, 1.5),
        rms=np.clip(rms, 0, 1.5),
        onset_timing=np.clip(flux(fine_log), 0, 1.5),
        percussive=np.clip(flux(log_perc), 0, 1.5),
        harmonic=np.clip(flux(log_harm), 0, 1.5),
        harmonic_level=np.clip(harm_level, 0, 1.5).astype(np.float32),
    )


def track_beats(signal: Signal) -> tuple[np.ndarray, float]:
    envelope = librosa.onset.onset_strength(y=signal.y, sr=SAMPLE_RATE, hop_length=HOP)
    tempo, frames = librosa.beat.beat_track(onset_envelope=envelope, sr=SAMPLE_RATE, hop_length=HOP, units="frames")
    times = librosa.frames_to_time(frames, sr=SAMPLE_RATE, hop_length=HOP)
    return times, float(np.atleast_1d(tempo)[0])


MIN_BPM, MAX_BPM = 90.0, 260.0
PREFERRED_BPM = (120.0, 240.0)


ATTACK_FRACTION = 0.1
ATTACK_LOOKBACK_S = 0.06


def attack_starts(y: np.ndarray, peaks_s: np.ndarray) -> np.ndarray:
    """Physical start of each attack, at sample resolution.

    A spectral frame marks where energy rises fastest, which on real drums is 10-20 ms after the
    sound begins. Here each detection is refined on a 1 ms energy envelope: the start is where the
    last continuous rise before the local peak crosses 10% of the way from the quiet level to the
    peak. On synthetic drums this lands within 0.5 ms of the true hit.
    """
    win = max(1, SAMPLE_RATE // 1000)
    env = np.sqrt(np.convolve(y * y, np.ones(win) / win, mode="same"))
    look = int(SAMPLE_RATE * ATTACK_LOOKBACK_S)
    ahead = int(SAMPLE_RATE * 0.015)
    starts = np.empty(len(peaks_s))

    for i, t in enumerate(peaks_s):
        centre = int(t * SAMPLE_RATE)
        lo, hi = max(0, centre - look), min(len(env), centre + ahead)
        seg = env[lo:hi]
        if len(seg) < 10:
            starts[i] = t
            continue
        k = int(np.argmax(seg))
        base = np.percentile(seg[: max(1, k)], 10)
        threshold = base + ATTACK_FRACTION * (seg[k] - base)
        j = k
        while j > 0 and seg[j - 1] >= threshold:
            j -= 1
        starts[i] = (lo + j) / SAMPLE_RATE

    return starts


def strong_onsets(signal: Signal) -> tuple[np.ndarray, np.ndarray]:
    """Times (latency-corrected flux peaks) and strengths of the stronger half of onset peaks."""
    env = signal.onset_timing
    peaks = librosa.util.peak_pick(env, pre_max=8, post_max=8, pre_avg=20, post_avg=20, delta=0.1, wait=10)
    if len(peaks) == 0:
        # Silence or a constant tone: no onsets at all (the caller rejects the audio).
        return np.empty(0), np.empty(0)
    strength = env[peaks]
    keep = strength > np.median(strength)
    return peaks[keep] * signal.frame_seconds - TIMING_LATENCY_S, strength[keep]


def attack_lead(signal: Signal, onset_times: np.ndarray) -> float:
    """How long before its flux peak a sound in this song typically starts (seconds, <= 0).

    Per-note attack starts are noisy (a start can run back into the previous sound), so they are
    not used for tempo or phase directly; the median gap is a robust, song-specific correction.
    """
    starts = attack_starts(signal.y, onset_times)
    gaps = starts - onset_times
    gaps = gaps[(gaps <= 0) & (gaps >= -0.04)]
    return float(np.median(gaps)) if len(gaps) else 0.0


def alignment(times: np.ndarray, weights: np.ndarray, step: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Weighted circular resultant of onset times on grids of the given steps.

    Returns (strength in [0, 1], phase in seconds). Onsets on a grid share one angle and push the
    strength towards 1; unrelated grids scatter them and keep it near 1/sqrt(n).
    """
    angles = 2 * np.pi * np.outer(1 / step, times)
    vector = (weights * np.exp(1j * angles)).sum(axis=1) / weights.sum()
    phase = (np.angle(vector) % (2 * np.pi)) / (2 * np.pi) * step
    return np.abs(vector), phase


@dataclass(frozen=True)
class ManualTiming:
    """A correction from the user. Offset without BPM is meaningless, so BPM is required."""

    bpm: float
    offset_ms: float | None = None


def detect_bpm(onset_times: np.ndarray, onset_weights: np.ndarray) -> float:
    """Global search of a constant tempo against onsets, independent of any beat tracker.

    Trackers make metrical errors (2/3 on 3-3-2 rhythms, half/double); scoring every tempo in the
    mapping range on the half-beat grid avoids inheriting them.
    """

    def strength(bpms: np.ndarray) -> np.ndarray:
        return alignment(onset_times, onset_weights, 60.0 / bpms / 2)[0]

    coarse = np.arange(MIN_BPM, MAX_BPM, 0.02)
    coarse_strength = np.concatenate([strength(chunk) for chunk in np.array_split(coarse, 40)])
    best = coarse[int(np.argmax(coarse_strength))]

    fine = np.arange(best - 0.05, best + 0.05, 0.0005)
    fine_strength = strength(fine)
    i = int(np.argmax(fine_strength))
    bpm, score = float(fine[i]), float(fine_strength[i])

    # Fold into the preferred range when the octave neighbour explains the onsets almost as well.
    if bpm < PREFERRED_BPM[0] and float(strength(np.array([bpm * 2]))[0]) > 0.85 * score:
        bpm *= 2
    elif bpm >= PREFERRED_BPM[1] and float(strength(np.array([bpm / 2]))[0]) > 0.85 * score:
        bpm /= 2
    return bpm


def detect_offset(signal: Signal, onset_times: np.ndarray, onset_weights: np.ndarray, beat_length: float, duration: float) -> float:
    """First downbeat (seconds, within the first bar) for a given beat length.

    The phase is the circular mean of onsets on the 1/4 grid, where syncopated hits (a 3-3-2 kick)
    still line up; on the whole-beat grid they would pull the mean off the beat. Which of the four
    quarter positions is the beat is then decided by onset strength, and the downbeat is the beat
    of four with the strongest low-band onsets and loudness.
    """
    quarter = beat_length / 4
    quarter_phase = float(alignment(onset_times, onset_weights, np.array([quarter]))[1][0])

    def on_beats(phase: float) -> float:
        times = np.arange(phase, duration, beat_length)
        frames = np.clip(np.round((times + TIMING_LATENCY_S) / signal.frame_seconds).astype(int), 0, len(signal.onset_timing) - 1)
        return float(signal.onset_timing[frames].mean())

    phase = max((quarter_phase + k * quarter for k in range(4)), key=on_beats)
    bar_scores = []
    for k in range(4):
        times = np.arange(phase + k * beat_length, duration, 4 * beat_length)
        frames = np.clip(np.round(times / signal.frame_seconds).astype(int), 0, len(signal.onset) - 1)
        bar_scores.append(float(signal.onset_low[frames].mean() + 0.5 * signal.rms[frames].mean()))
    downbeat = phase + int(np.argmax(bar_scores)) * beat_length
    return downbeat % (4 * beat_length)


def fit_timing(signal: Signal, beats: np.ndarray, tracker_bpm: float, duration: float, manual: ManualTiming | None = None) -> dict:
    """Constant timing: detected, or corrected by the user (BPM, or BPM and offset).

    Detection quality is reported either way, so a manual value that disagrees with the audio is
    visible rather than silently trusted.
    """
    onset_times, onset_weights = strong_onsets(signal)
    if len(onset_times) < 16:
        raise ValueError("Too few onsets to fit a constant tempo.")

    def strength_at(b: float) -> float:
        return float(alignment(onset_times, onset_weights, np.array([60.0 / b / 2]))[0][0])

    if manual is not None and not (MIN_BPM / 4 <= manual.bpm <= MAX_BPM * 2):
        raise ValueError(f"Manual BPM {manual.bpm} is outside the supported range.")

    bpm = manual.bpm if manual is not None else detect_bpm(onset_times, onset_weights)
    beat_length = 60.0 / bpm

    if manual is not None and manual.offset_ms is not None:
        offset = manual.offset_ms / 1000
        manual_fields = ["bpm", "offset"]
    else:
        # Phase from the stable flux peaks, moved back to where this song's attacks begin.
        lead = attack_lead(signal, onset_times)
        offset = (detect_offset(signal, onset_times, onset_weights, beat_length, duration) + lead) % (4 * beat_length)
        manual_fields = ["bpm"] if manual is not None else []

    # Agreement of tracker beats with the chosen grid, as a cross-check rather than an input.
    tracker_error = ((beats - offset) / beat_length) % 1
    tracker_error = np.minimum(tracker_error, 1 - tracker_error)
    inlier_ratio = float(np.mean(tracker_error < 0.1)) if len(beats) else 0.0

    score = strength_at(bpm)
    chance = 1 / np.sqrt(len(onset_times))
    alternatives = [bpm / 2, bpm * 2 / 3, bpm * 3 / 4, bpm * 4 / 3, bpm * 3 / 2, bpm * 2]

    return {
        "bpm": bpm,
        "beatLengthUs": beat_length * 1e6,
        "offsetUs": int(round(offset * 1e6)),
        "source": "manual" if manual_fields else "detected",
        "manualFields": manual_fields,
        "trackerBpm": tracker_bpm,
        "inlierRatio": inlier_ratio,
        "gridScore": score,
        "chanceScore": float(chance),
        "alternatives": [{"bpm": b, "gridScore": strength_at(b)} for b in alternatives if MIN_BPM / 2 <= b <= MAX_BPM * 2],
        # Heuristic, not calibrated: how far the half-beat alignment rises above chance. It is reported
        # for manual timing too; a low value there means the correction disagrees with the audio.
        "confidence": round(float(np.clip((score - chance) / (1 - chance), 0, 1)), 3),
        "confidenceKind": "heuristic",
    }


def grid_features(signal: Signal, timing: dict, duration: float) -> list[dict]:
    beat = timing["beatLengthUs"] / 1e6
    step = beat / GRID_DIVISOR
    start = timing["offsetUs"] / 1e6
    first = int(np.ceil(-start / step))
    count = int(np.floor((duration - start) / step))
    # The band envelopes come from a long window and run up to ~15 ms early; search a bit wider.
    radius = max(1, int(0.02 / signal.frame_seconds))
    n = len(signal.onset)

    def peak(track: np.ndarray, frame: int) -> float:
        lo, hi = max(0, frame - radius), min(n, frame + radius + 1)
        return float(track[lo:hi].max()) if hi > lo else 0.0

    points = []
    for i in range(first, count + 1):
        t = start + i * step
        f = signal.frame_at(t)
        if f >= n:
            break
        # Sustain: loudness held through the next beat while onsets stay quiet.
        span = slice(f + radius, min(n, signal.frame_at(t + beat)))
        held = float(signal.rms[span].mean()) if span.stop > span.start else 0.0
        quiet = 1.0 - float(np.clip(signal.onset[span].mean() * 3, 0, 1)) if span.stop > span.start else 0.0
        # Held tone: harmonic loudness through the next beat without new harmonic attacks. A vocal
        # or guitar note that rings on, which mappers follow with a slider more than drums.
        tone = float(signal.harmonic_level[span].mean()) if span.stop > span.start else 0.0
        steady = 1.0 - float(np.clip(signal.harmonic[span].mean() * 3, 0, 1)) if span.stop > span.start else 0.0
        points.append({
            "index": i,
            "timeUs": int(round(t * 1e6)),
            "onset": round(peak(signal.onset, f), 4),
            "low": round(peak(signal.onset_low, f), 4),
            "high": round(peak(signal.onset_high, f), 4),
            "energy": round(float(signal.rms[f]), 4),
            "sustain": round(held * quiet, 4),
            "percussive": round(peak(signal.percussive, f), 4),
            "harmonic": round(peak(signal.harmonic, f), 4),
            "tone": round(float(signal.harmonic_level[f]), 4),
            "toneHeld": round(tone * steady, 4),
        })
    return points


def sections(points: list[dict], timing: dict) -> list[dict]:
    """Bar-level loudness, smoothed, cut where it changes level. Labels are intensities, not names."""
    per_bar = 4 * GRID_DIVISOR
    # Grid index 0 is a downbeat, so bars are index // per_bar (floor, also for the pickup before it).
    by_bar: dict[int, list[dict]] = {}
    for p in points:
        by_bar.setdefault(p["index"] // per_bar, []).append(p)
    bars = [by_bar[k] for k in sorted(by_bar)]
    energy = np.array([np.mean([p["energy"] for p in bar]) for bar in bars])
    density = np.array([np.mean([p["onset"] for p in bar]) for bar in bars])
    level = np.convolve(0.6 * energy + 0.4 * density, np.ones(2) / 2, mode="same")

    q = np.quantile(level, [0.15, 0.45, 0.8]) if len(level) else [0, 0, 0]
    labels = np.digitize(level, q)  # 0 quiet .. 3 peak

    result = []
    start = 0
    for b in range(1, len(bars) + 1):
        if b == len(bars) or labels[b] != labels[start]:
            result.append({
                "startUs": bars[start][0]["timeUs"],
                "endUs": bars[b - 1][-1]["timeUs"],
                "firstBar": bars[start][0]["index"] // per_bar,
                "barCount": b - start,
                "intensity": int(labels[start]),
                "energy": round(float(energy[start:b].mean()), 4),
            })
            start = b
    return result


def analyze(path: str, manual: ManualTiming | None = None) -> dict:
    with open(path, "rb") as f:
        sha = hashlib.sha256(f.read()).hexdigest()

    signal = load(path)
    duration = len(signal.y) / SAMPLE_RATE
    beats, tracker_bpm = track_beats(signal)
    if duration < 5:
        raise ValueError("Audio is shorter than five seconds.")
    timing = fit_timing(signal, beats, tracker_bpm, duration, manual)
    points = grid_features(signal, timing, duration)

    return {
        "schemaVersion": SCHEMA_VERSION,
        "audio": {"sha256": sha, "durationUs": int(round(duration * 1e6)), "sampleRate": SAMPLE_RATE},
        "observations": {"trackerBeatsUs": [int(round(t * 1e6)) for t in beats]},
        "timing": timing,
        "gridDivisor": GRID_DIVISOR,
        "grid": points,
        "sections": sections(points, timing),
        "provenance": {"librosa": librosa.__version__, "hop": HOP, "sampleRate": SAMPLE_RATE},
    }
