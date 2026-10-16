import json
import os

from osu_ai_rhythm.data import Lineage, examples

from test_rhythm import analysis


def make_dataset(root, scope):
    """One map, one song, under a rights grant of the given scope."""
    for d in ("manifests", "beatmaps", "analysis/aaaaaaaaaaaa"):
        os.makedirs(os.path.join(root, d), exist_ok=True)
    a = analysis()
    with open(os.path.join(root, "analysis/aaaaaaaaaaaa/analysis.json"), "w") as f:
        json.dump(a, f)
    ir = {"timing": {"segments": [{"beatLengthUs": 320_000, "startTimeUs": 485_000}]},
          "objects": [{"kind": "circle", "timeUs": 485_000 + k * 320_000} for k in range(20)]}
    with open(os.path.join(root, "beatmaps/m.json"), "w") as f:
        json.dump(ir, f)
    with open(os.path.join(root, "manifests/rights.jsonl"), "w") as f:
        f.write(json.dumps({"id": "g", "scope": scope}) + "\n")
    with open(os.path.join(root, "manifests/maps.jsonl"), "w") as f:
        f.write(json.dumps({"mapSha256": "m", "status": "accepted", "rightsRef": "g", "audioSha256": "a" * 64,
                            "irRef": "beatmaps/m.json", "starRating": 5.0, "artist": "A", "title": "T"}) + "\n")
    with open(os.path.join(root, "manifests/split.json"), "w") as f:
        json.dump({"families": [{"split": "train", "mapSha256": ["m"]}]}, f)


def test_training_grant_is_used(tmp_path):
    make_dataset(str(tmp_path), "training")
    lineage = Lineage(manifests={}, rights=[])
    out = examples(str(tmp_path), os.path.join(str(tmp_path), "analysis"), lineage)
    assert len(out) == 1
    assert out[0].y.sum() == 20
    assert lineage.rights == [{"id": "g", "scope": "training"}]
    assert set(lineage.manifests) >= {"maps.jsonl", "split.json", "rights.jsonl"}


def test_evaluation_only_grant_is_refused(tmp_path):
    make_dataset(str(tmp_path), "evaluation")
    lineage = Lineage(manifests={}, rights=[])
    assert examples(str(tmp_path), os.path.join(str(tmp_path), "analysis"), lineage) == []
    assert lineage.skipped["rights do not allow training"] == 1
