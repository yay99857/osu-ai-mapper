import numpy as np
import pytest

from osu_ai_rhythm.aligned import aligned_maps
from osu_ai_rhythm.pulso_v2 import VERSION, ahead_features, context_names, decode, evaluate_map, fit, predict, sequence_context, summarise
from test_pulso import fixture


def test_sequence_context_sees_gaps_density_and_runs():
    idx = np.arange(20)
    heads = np.zeros(20, dtype=bool)
    heads[[2, 6, 7, 8, 16]] = True
    ctx = sequence_context(idx, heads)
    col = {n: k for k, n in enumerate(context_names())}
    assert ctx[6, col["gap_prev"]] == 4 and ctx[6, col["gap_next"]] == 1
    assert ctx[8, col["gap_next"]] == 8 and ctx[16, col["is_last"]] == 1
    assert ctx[7, col["run_length"]] == 3 and ctx[8, col["run_position"]] == 2
    assert ctx[2, col["gap_prev"]] == 32  # nothing before: capped
    assert ctx[7, col["heads_pm4"]] == 2  # 6 and 8 within four ticks, itself not counted
    assert not ctx[~heads].any()


def test_ahead_features_see_a_coming_silence():
    grid = [{"index": i, "energy": 0.8 if i < 40 else 0.0} for i in range(80)]
    x = ahead_features({"grid": grid}, np.array([10, 38]))
    assert x[0, 1] == 1 and x[1, 1] < 0.3  # loudness over the next four ticks, relative to the median
    assert x[1, -1] >= 14  # quiet ticks among the next sixteen


def test_fit_decode_and_product_contract(tmp_path):
    fixture(tmp_path)
    maps = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    bundle = fit(maps, True, iterations=3, head_budget=1000, folds=2)
    row = evaluate_map(bundle, maps[0], "v2")
    assert 0 <= row["clickF1"] <= 1 and "sliderShare" in row["human"]
    assert summarise([row])["maps"] == 1
    r = predict(bundle, maps[0].analysis, 5)
    assert r["schemaVersion"] == "pulso-poc-0.1"
    assert len(r["indices"]) == len(r["notes"]) == len(r["probabilities"])
    for i, n in enumerate(r["notes"]):
        assert 0 <= n["sliderProbability"] <= 1 and 1 <= n["durationGrid"] <= 16 and 0 <= n["importance"] <= 1
        assert 0 <= n["distanceRadii"] <= 20


def test_decoded_sliders_always_fit_before_the_next_head(tmp_path):
    fixture(tmp_path)
    maps = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    bundle = fit(maps, True, iterations=3, head_budget=1000, folds=2)
    from osu_ai_rhythm.pulso_v2 import audio_matrix, energy_gate
    idx, x = audio_matrix(maps[0].analysis, 5, True, 0.5, contrast=True, stems=True)
    d = decode(bundle, idx, x, energy_gate(maps[0].analysis, idx), int(idx.max()) + 1)
    pos = idx[d["heads"]]
    for k in range(len(pos) - 1):
        assert d["length"][k] <= pos[k + 1] - pos[k] - 1


def test_history_features_and_state_table_agree():
    from osu_ai_rhythm.pulso_v2 import STATES, history_features, ar_walk
    heads = np.zeros(30, dtype=bool)
    heads[[3, 4, 10]] = True
    h = history_features(heads)
    assert h[5, :2].tolist() == [1, 1] and h[5, 8] == 1  # heads one and two ticks back
    assert h[11, 0] == 1 and h[25, 8] == 15
    # A walk that clicks wherever the table says "a head two ticks after a head" alternates.
    table = np.zeros((12, len(STATES)), dtype=np.float32)
    for code in range(len(STATES)):
        table[:, code] = 1.0 if STATES[code, 1] == 1 or (STATES[code, :8].sum() == 0) else 0.0
    walked = ar_walk(table, np.ones(12, dtype=bool), 0.5)
    assert np.flatnonzero(walked).tolist() == [0, 2, 4, 6, 8, 10]


def test_ar_select_hits_the_target_count():
    from osu_ai_rhythm.pulso_v2 import STATES, ar_select
    rng = np.random.default_rng(1)
    table = rng.random((200, len(STATES))).astype(np.float32)
    heads = ar_select(table, np.ones(200, dtype=bool), 60)
    assert abs(int(heads.sum()) - 60) <= 3


def test_window_selection_keeps_local_density():
    from osu_ai_rhythm.pulso_v2 import STATES, WINDOW, ar_select_windows
    rng = np.random.default_rng(2)
    table = rng.random((4 * WINDOW, len(STATES))).astype(np.float32)
    expected = np.concatenate([np.full(WINDOW, 0.5), np.full(WINDOW, 0.1), np.full(WINDOW, 0.5), np.full(WINDOW, 0.1)])
    heads = ar_select_windows(table, np.ones(len(table), dtype=bool), expected)
    counts = [int(heads[k * WINDOW:(k + 1) * WINDOW].sum()) for k in range(4)]
    assert abs(counts[0] - 16) <= 2 and abs(counts[1] - 3) <= 1 and abs(counts[2] - 16) <= 2


def test_style_is_learned_from_the_map_and_chosen_by_name(tmp_path):
    from types import SimpleNamespace
    from osu_ai_rhythm.pulso_v2 import resolve_style, style_of
    objs = [SimpleNamespace(q=q) for q in [0, 4, 8, 9, 10, 11, 12, 20, 22]]
    assert style_of(objs) == pytest.approx(5 / 9)  # 8..12 is a five-note stream
    bundle = {"styleTable": [[5.0, v] for v in np.linspace(0, 1, 41)]}
    assert resolve_style(bundle, 5.0, "jumps") < resolve_style(bundle, 5.0, "auto") < resolve_style(bundle, 5.0, "streams")
    fixture(tmp_path)
    maps = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    fitted = fit(maps, True, iterations=3, head_budget=1000, folds=2, ar=False)
    assert fitted["features"] == VERSION and fitted["arHead"] is None
    for style in ("jumps", "auto", "streams"):
        assert predict(fitted, maps[0].analysis, 5, style)["style"] == style
    with pytest.raises(ValueError):
        predict(fitted, maps[0].analysis, 5, "spaghetti")


def test_stretches_while_the_music_plays_are_filled_and_quiet_ones_kept():
    from osu_ai_rhythm.pulso_v2 import fill_pockets
    tick = 0.1  # seconds
    heads = np.zeros(300, dtype=bool)
    heads[[0, 4, 50, 54, 200, 204]] = True  # 4.6 s pocket (4..50) and 14.6 s stretch (54..200)
    playing = np.ones(300, dtype=bool)
    playing[60:195] = False  # the long stretch is quiet: a break
    p = np.linspace(0.01, 0.02, 300)
    filled = fill_pockets(heads, p, playing, tick)
    pos = np.flatnonzero(filled)
    inner = [b - a for a, b in zip(pos, pos[1:]) if 4 <= a < 50]
    assert max(inner) * tick < 2.5
    assert not filled[60:195].any()  # quiet: a break, left alone
    loud = fill_pockets(heads, p, np.ones(300, dtype=bool), tick)
    pos = np.flatnonzero(loud)
    assert max(b - a for a, b in zip(pos, pos[1:])) * tick < 2.5  # a long stretch with music is filled too
    quiet = playing.copy()
    quiet[5:50] = False
    assert fill_pockets(heads, p, quiet, tick)[5:50].sum() == 0  # silence is not filled


def test_a_sung_attack_over_quiet_drums_stands_out_in_the_contrast_features():
    from osu_ai_rhythm.pulso_v2 import contrast_features, contrast_names
    grid = [{"index": i, "energy": 0.4, "onset": 0.1, "percussive": 0.1, "harmonic": 0.35} for i in range(96)]
    grid[50]["harmonic"] = 1.0  # one syllable
    x = contrast_features({"grid": grid}, np.arange(96))
    col = {n: k for k, n in enumerate(contrast_names())}
    assert x[50, col["harmonic_rel"]] > 2.0 and x[50, col["harmonic_peak"]] == 1
    assert x[30, col["harmonic_rel"]] < 1.1 and x[30, col["harmonic_peak"]] == 0
    assert x[49, col["harmonic_rel@+1"]] == x[50, col["harmonic_rel"]]


def test_stems_are_read_next_to_the_analysis_and_required_by_v5(tmp_path):
    from osu_ai_rhythm.pulso_v2 import stem_features, stem_names
    fixture(tmp_path)
    m, = aligned_maps(str(tmp_path), str(tmp_path / "analysis"))
    assert "stems" in m.analysis
    idx = np.array([p["index"] for p in m.analysis["grid"] if p["index"] >= 0])
    x = stem_features(m.analysis, idx)
    assert x.shape == (len(idx), len(stem_names()))
    without = {k: v for k, v in m.analysis.items() if k != "stems"}
    with pytest.raises(ValueError, match="stems"):
        stem_features(without, idx)
