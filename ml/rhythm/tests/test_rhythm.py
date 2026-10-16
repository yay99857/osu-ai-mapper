import json
import subprocess
import sys

import joblib
import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier

from osu_ai_rhythm.features import FEATURES_VERSION, feature_names, features
from osu_ai_rhythm.train import expected_count_mask, f1


def analysis(bars=8):
    grid = []
    for i in range(-3, bars * 16):
        on = 1.0 if i % 4 == 0 else 0.5 if i % 2 == 0 else 0.1
        grid.append({"index": i, "timeUs": 500_000 + i * 80_000, "onset": on, "low": on if i % 8 == 0 else 0,
                     "high": on / 2, "energy": 0.7, "sustain": 0.1})
    return {"schemaVersion": "poc-0.2", "timing": {"bpm": 187.5, "beatLengthUs": 320_000, "offsetUs": 500_000},
            "grid": grid, "sections": [{"firstBar": 0, "barCount": bars, "intensity": 2}]}


def test_features_cover_non_negative_grid_with_named_columns():
    idx, x = features(analysis(), stars=5.0)
    assert idx[0] == 0 and len(idx) == 8 * 16
    assert x.shape == (len(idx), len(feature_names()))
    names = feature_names()
    assert x[0, names.index("pos16_0")] == 1 and x[1, names.index("pos16_1")] == 1
    assert np.all(x[:, names.index("stars")] == 5.0)
    assert x[0, names.index("intensity_2")] == 1


def test_context_columns_look_at_neighbours():
    idx, x = features(analysis(), stars=5.0)
    names = feature_names()
    # Position 4 (a beat) has onset 1.0; position 3 sees it one step ahead.
    assert x[3, names.index("onset@+1")] == 1.0
    assert x[5, names.index("onset@-1")] == 1.0


def test_expected_count_decoding_keeps_the_model_density():
    p = np.array([0.9, 0.1, 0.8, 0.1, 0.6, 0.1, 0.4, 0.0])
    mask = expected_count_mask(p)
    assert mask.sum() == round(p.sum())
    assert mask[0] and mask[2] and mask[4]


def test_f1():
    assert f1(np.array([1, 1, 0], bool), np.array([1, 0, 0], bool)) == 2 * (0.5 * 1) / 1.5


def test_predict_cli_writes_probabilities(tmp_path):
    a = analysis()
    idx, x = features(a, 5.0)
    y = (idx % 4 == 0).astype(int)
    model = HistGradientBoostingClassifier(max_iter=20).fit(x, y)
    model_path = tmp_path / "m.joblib"
    joblib.dump({"model": model, "threshold": 0.5, "decoding": ["count", 1.0], "features": FEATURES_VERSION, "names": feature_names()}, model_path)
    analysis_path = tmp_path / "analysis.json"
    analysis_path.write_text(json.dumps(a))
    out = tmp_path / "rhythm.json"

    result = subprocess.run([sys.executable, "-m", "osu_ai_rhythm.cli", "predict", str(model_path), str(analysis_path), "5", str(out)],
                            capture_output=True, text=True)

    assert result.returncode == 0, result.stderr
    r = json.loads(out.read_text())
    assert r["schemaVersion"] == "rhythm-poc-0.1"
    assert len(r["indices"]) == len(r["probabilities"]) == len(idx)
    assert 0 < r["model"]["threshold"] < 1
    clicks = [i for i, p in zip(r["indices"], r["probabilities"]) if p >= r["model"]["threshold"]]
    assert all(i % 4 == 0 for i in clicks), "a model trained on beats clicks beats"
