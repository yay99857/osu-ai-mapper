import json

import numpy as np
import pytest

from osu_ai_rhythm.aligned import aligned_maps, tier
from osu_ai_rhythm.data import Lineage
from osu_ai_rhythm.pulso import HPSS, labels, matrix, selection, fit, evaluate, predict
from test_rights import make_dataset


def fixture(root, scope="training"):
    make_dataset(str(root), scope)
    path = root / "beatmaps/m.json"
    ir = json.loads(path.read_text())
    ir["settings"] = {"cs": 4}
    for o in ir["objects"]:
        o["position"] = {"x": 200, "y": 100}
    ir["objects"][0].update(kind="slider", spanCount=2,
                            computed={"durationUs": 240_000, "endPosition": {"x": 100, "y": 100}})
    path.write_text(json.dumps(ir))
    path = root / "analysis/aaaaaaaaaaaa/analysis.json"
    a = json.loads(path.read_text())
    a["schemaVersion"] = "poc-0.3"
    for p in a["grid"]:
        p.update({k: 0.5 for k in HPSS})
    path.write_text(json.dumps(a))
    stems = [{"index": p["index"], "vocals": 0.3, "drums": p.get("onset", 0.2), "bass": 0.1, "other": 0.2, "vocalLevel": 0.4, "vocalHeld": 0.1}
             for p in a["grid"]]
    (path.parent / "stems.json").write_text(json.dumps({"schemaVersion": "stems-poc-0.1", "grid": stems}))
    return ir


def test_alignment_duration_and_spacing_use_official_slider_end(tmp_path):
    fixture(tmp_path)
    m, = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    assert m.objects[0].q == 0  # client convention applied exactly once
    assert m.objects[0].length == 3  # total duration, not multiplied by span count again
    idx, _ = matrix(m.analysis, m.stars)
    y = labels(m, idx)
    assert y[0, 1] == 1 and y[0, 2] == pytest.approx(np.log2(3))
    assert np.isnan(y[0, 3]) and np.isnan(y[4, 2])
    assert np.expm1(y[4, 3]) == pytest.approx(100 / m.radius)


def test_latest_admission_and_rights_and_split_are_respected(tmp_path):
    fixture(tmp_path, "evaluation")
    lineage = Lineage({}, [])
    assert not aligned_maps(str(tmp_path), str(tmp_path / "analysis"), lineage)
    assert lineage.skipped["rights do not allow training"] == 1
    fixture(tmp_path)
    assert not aligned_maps(str(tmp_path), str(tmp_path / "analysis"), splits={"validation"})
    path = tmp_path / "manifests/maps.jsonl"
    record = json.loads(path.read_text())
    path.write_text(path.read_text() + json.dumps(record) + "\n")
    assert len(aligned_maps(str(tmp_path), str(tmp_path / "analysis"))) == 1
    record["status"] = "rejected"
    path.write_text(path.read_text() + json.dumps(record) + "\n")
    assert not aligned_maps(str(tmp_path), str(tmp_path / "analysis"))


def test_out_of_range_heads_are_excluded_and_input_is_sorted(tmp_path):
    ir = fixture(tmp_path)
    ir["objects"] += [{"kind": "circle", "timeUs": -100_000}, {"kind": "circle", "timeUs": 50_000_000}]
    ir["objects"].reverse()
    (tmp_path / "beatmaps/m.json").write_text(json.dumps(ir))
    m, = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    assert m.off_grid == 2 and len(m.objects) == 20
    assert [o.q for o in m.objects] == sorted(o.q for o in m.objects)


def test_pulso_rejects_old_or_incomplete_analysis(tmp_path):
    fixture(tmp_path)
    m, = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    m.analysis["grid"][4].pop("tone")
    with pytest.raises(ValueError, match="Incomplete"):
        matrix(m.analysis, 5)
    m.analysis["schemaVersion"] = "poc-0.2"
    with pytest.raises(ValueError, match="poc-0.3"):
        matrix(m.analysis, 5)


def test_exact_count_handles_ties_and_zero():
    assert selection(np.zeros(8)).sum() == 0
    assert selection(np.full(8, 0.5)).tolist() == [True] * 4 + [False] * 4
    assert tier(5.4) == "insane" and tier(5.5) == "expert"


def test_four_heads_fit_and_report(tmp_path):
    fixture(tmp_path)
    maps = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    models, rows = fit(maps, True, iterations=2, max_rows=1000)
    report, = evaluate(models, maps, True)
    assert rows == 77 and len(models) == 4
    assert 0 <= report["clickF1"] <= 1
    assert report["sliderDurationMaeGridAtHumanSliders"] < 0.001
    prediction = predict({"models": models, "hpss": True}, maps[0].analysis, 5)
    assert prediction["schemaVersion"] == "pulso-poc-0.1"
    assert len(prediction["indices"]) == len(prediction["notes"]) == len(prediction["probabilities"])
    assert all(1 <= n["durationGrid"] <= 16 and 0 <= n["importance"] <= 1 for n in prediction["notes"])
