import json
import os

import pytest

from osu_ai_audio.bench_timing import classify, run, summarise, wrap

from synth import BASIC_ROCK, render


def make_dataset(root, bpm_audio, bpm_human, offset_human_ms, spread=0.0):
    """A one-song dataset in the builder's layout, with the human timing chosen by the test."""
    os.makedirs(os.path.join(root, "manifests"))
    os.makedirs(os.path.join(root, "audio"))
    os.makedirs(os.path.join(root, "beatmaps"))
    render(os.path.join(root, "audio", "a.wav"), bpm=bpm_audio, first_downbeat=0.3, seconds=30, pattern=BASIC_ROCK)

    segments = [{"beatLengthUs": 60e6 / bpm_human, "startTimeUs": int(offset_human_ms * 1000)}]
    if spread:
        segments.append({"beatLengthUs": 60e6 / (bpm_human * (1 + spread / 100)), "startTimeUs": 15_000_000})
    ir = {"timing": {"segments": segments}}
    with open(os.path.join(root, "beatmaps", "m.json"), "w") as f:
        json.dump(ir, f)

    record = {"mapSha256": "m", "status": "accepted", "audioSha256": "a" * 64, "audioRef": "audio/a.wav", "irRef": "beatmaps/m.json",
              "artist": "Synth", "title": "Rock", "firstObjectUs": 300_000, "lastObjectUs": 28_000_000}
    with open(os.path.join(root, "manifests", "maps.jsonl"), "w") as f:
        f.write(json.dumps(record) + "\n")


def test_correct_timing_scores_near_zero(tmp_path):
    make_dataset(str(tmp_path), 160, 160, 300)
    results, skipped = run(str(tmp_path))
    r = results[0]
    assert skipped == []
    assert r.tempo == "exact"
    assert abs(r.mid_error_ms) < 3
    assert r.downbeat_ok


def test_human_offset_shift_is_measured(tmp_path):
    # The human grid is 20 ms later than the audio: detected minus human is about -20 ms.
    make_dataset(str(tmp_path), 160, 160, 320)
    r = run(str(tmp_path))[0][0]
    assert r.mid_error_ms == pytest.approx(-20, abs=3)


def test_metrical_mismatch_is_classified(tmp_path):
    make_dataset(str(tmp_path), 160, 80, 300)
    assert run(str(tmp_path))[0][0].tempo == "2"


def test_multi_tempo_maps_are_not_scored(tmp_path):
    make_dataset(str(tmp_path), 160, 160, 300, spread=0.2)
    results, skipped = run(str(tmp_path))
    assert results == [] and skipped == ["Synth - Rock"]
    assert "not scored" in summarise(results, skipped).lower()


def test_helpers():
    assert wrap(370, 375) == -5
    assert classify(199.1, 132.733) == "3/2"
    assert classify(150, 150.05) == "exact"
