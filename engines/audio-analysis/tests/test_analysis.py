import json
from pathlib import Path

import jsonschema
import numpy as np
import pytest

from osu_ai_audio.analysis import ManualTiming, analyze
from osu_ai_audio.transcode import to_mp3

from synth import BASIC_ROCK, TAMBORZAO, render

SCHEMA = Path(__file__).resolve().parents[3] / "packages" / "contracts" / "schemas" / "song-analysis-poc-0.3.schema.json"


@pytest.fixture(scope="module")
def rock(tmp_path_factory) -> tuple[Path, dict]:
    path = tmp_path_factory.mktemp("rock") / "rock.wav"
    render(str(path), bpm=175, first_downbeat=0.412, seconds=40, pattern=BASIC_ROCK)
    return path, analyze(str(path))


def downbeat_error_ms(result: dict, bpm: float, first_downbeat: float, beats: int = 4) -> float:
    """Distance to the true downbeat, modulo `beats` (a half-bar-symmetric pattern cannot tell 1 from 3)."""
    bar_us = beats * 60e6 / bpm
    diff = (result["timing"]["offsetUs"] - first_downbeat * 1e6) % bar_us
    return min(diff, bar_us - diff) / 1000


def test_constant_tempo_and_downbeat(rock):
    _, result = rock
    timing = result["timing"]
    assert timing["bpm"] == pytest.approx(175, abs=0.02)
    assert downbeat_error_ms(result, 175, 0.412) < 3
    assert timing["source"] == "detected"
    assert timing["manualFields"] == []
    assert timing["confidence"] > 0.5


def test_grid_index_zero_is_the_downbeat(rock):
    _, result = rock
    zero = next(p for p in result["grid"] if p["index"] == 0)
    assert zero["timeUs"] == result["timing"]["offsetUs"]
    assert zero["low"] > 0.3, "the kick on the downbeat should show in the low band"


def test_output_conforms_to_contract(rock):
    _, result = rock
    jsonschema.validate(result, json.loads(SCHEMA.read_text(encoding="utf-8")))


def test_three_three_two_is_not_mistaken_for_two_thirds(tmp_path):
    path = tmp_path / "funk.wav"
    render(str(path), bpm=150, first_downbeat=0.25, seconds=40, pattern=TAMBORZAO)
    result = analyze(str(path))
    assert result["timing"]["bpm"] == pytest.approx(150, abs=0.05)
    # The tamborzão repeats every two beats, so beat 3 is as good a "downbeat" as beat 1.
    assert downbeat_error_ms(result, 150, 0.25, beats=2) < 3


def test_manual_bpm_keeps_detected_offset(tmp_path, rock):
    path, detected = rock
    result = analyze(str(path), ManualTiming(bpm=175))
    timing = result["timing"]
    assert timing["source"] == "manual"
    assert timing["manualFields"] == ["bpm"]
    assert timing["bpm"] == 175
    assert abs(timing["offsetUs"] - detected["timing"]["offsetUs"]) < 3000


def test_manual_offset_is_used_verbatim(rock):
    path, _ = rock
    result = analyze(str(path), ManualTiming(bpm=175, offset_ms=-30.5))
    assert result["timing"]["offsetUs"] == -30_500
    assert result["timing"]["manualFields"] == ["bpm", "offset"]
    # Index 0 sits 30.5 ms before the audio starts, so the grid begins at the next 1/4.
    assert min(p["index"] for p in result["grid"]) == 1
    assert all(p["timeUs"] >= 0 for p in result["grid"])


def test_wrong_manual_bpm_shows_low_confidence(rock):
    path, _ = rock
    result = analyze(str(path), ManualTiming(bpm=163))
    assert result["timing"]["confidence"] < 0.3


def test_silence_is_rejected(tmp_path):
    path = tmp_path / "silence.wav"
    render(str(path), bpm=120, first_downbeat=0, seconds=20, pattern={})
    with pytest.raises(ValueError):
        analyze(str(path))


def test_short_audio_is_rejected(tmp_path):
    path = tmp_path / "short.wav"
    render(str(path), bpm=120, first_downbeat=0, seconds=3, pattern=BASIC_ROCK)
    with pytest.raises(ValueError):
        analyze(str(path))


def test_mp3_keeps_the_timeline(tmp_path, rock):
    """The packaged MP3 must line up with the source: the encoder delay has to be compensated."""
    wav, detected = rock
    mp3 = tmp_path / "rock.mp3"
    to_mp3(str(wav), str(mp3))
    result = analyze(str(mp3))
    assert result["timing"]["bpm"] == pytest.approx(175, abs=0.02)
    assert abs(result["timing"]["offsetUs"] - detected["timing"]["offsetUs"]) < 2000
    assert result["audio"]["durationUs"] == pytest.approx(detected["audio"]["durationUs"], abs=30_000)


def test_contract_example_conforms():
    example = SCHEMA.parents[1] / "examples" / "song-analysis-minimal.json"
    jsonschema.validate(json.loads(example.read_text(encoding="utf-8")), json.loads(SCHEMA.read_text(encoding="utf-8")))


def test_harmonic_and_percussive_parts_are_told_apart(tmp_path):
    """Drums light up the percussive track; a held organ chord over bars 4-7 lights up the held-tone track."""
    import soundfile as sf

    path = tmp_path / "drums.wav"
    render(str(path), bpm=150, first_downbeat=0.2, seconds=30, pattern=BASIC_ROCK)
    y, rate = sf.read(str(path))
    beat = 60 / 150
    start, stop = int((0.2 + 16 * beat) * rate), int((0.2 + 32 * beat) * rate)
    t = np.arange(stop - start) / rate
    chord = sum(np.sin(2 * np.pi * f * t) for f in (440, 554.4, 659.3)) * 0.12
    y[start:stop] += chord if y.ndim == 1 else chord[:, None]
    sf.write(str(path), y, rate)

    result = analyze(str(path), ManualTiming(bpm=150, offset_ms=200))
    grid = {p["index"]: p for p in result["grid"]}
    inside = [grid[i]["toneHeld"] for i in range(68, 120)]
    outside = [grid[i]["toneHeld"] for i in range(132, 250)]
    assert np.median(inside) > 3 * max(np.median(outside), 0.02)
    snares = [grid[i]["percussive"] for i in range(4, 250, 16)]
    offbeat = [grid[i]["percussive"] for i in range(3, 250, 16)]
    assert np.median(snares) > 2 * np.median(offbeat)
