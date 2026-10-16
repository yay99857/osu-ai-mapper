import numpy as np
import torch

from osu_ai_sequence.data import BODY, CIRCLE, NONE, SLIDER
from osu_ai_sequence.decode import _inputs, generate
from osu_ai_sequence.model import WINDOW, PulsoS, shifted_inputs


def test_shifted_inputs_see_only_the_past():
    cls = torch.tensor([[1, 0, 0, 2, 3, 3, 0, 1]])
    previous, since = shifted_inputs(cls)
    assert previous.tolist() == [[4, 1, 0, 0, 2, 3, 3, 0]]
    assert since[0, 0].item() == 32 and since[0, 1].item() == 1 and since[0, 4].item() == 1 and since[0, 7].item() == 4
    p2, s2 = _inputs(cls[0].numpy(), 0, 8)
    assert p2.tolist() == previous[0].tolist() and s2.tolist() == since[0].tolist()


def test_the_decoder_does_not_peek_at_future_decisions():
    torch.manual_seed(0)
    model = PulsoS(6, d=32, heads=2, encoder_layers=1, decoder_layers=1).eval()
    x = torch.randn(1, 40, 6)
    a = torch.zeros(1, 40, dtype=torch.long)
    b = a.clone()
    b[0, 30:] = 1
    pa, sa = shifted_inputs(a)
    pb, sb = shifted_inputs(b)
    la = model(x, pa, sa)[0]
    lb = model(x, pb, sb)[0]
    assert torch.allclose(la[0, :31], lb[0, :31], atol=1e-5)
    assert not torch.allclose(la[0, 32:], lb[0, 32:], atol=1e-5)


def test_generation_keeps_slider_bodies_free_and_spans_windows():
    torch.manual_seed(0)
    model = PulsoS(6, d=32, heads=2, encoder_layers=1, decoder_layers=1).eval()
    with torch.no_grad():
        model.cls.bias.copy_(torch.tensor([-5.0, -5.0, 5.0]))  # always a slider
        model.length.bias.fill_(-5.0)
        model.length.bias[3] = 5.0  # three ticks
    n = WINDOW + 100
    cls, p_head, p_slider, length, dist = generate(model, torch.randn(n, 6), np.ones(n, dtype=bool))
    heads = np.flatnonzero(cls == SLIDER)
    assert len(heads) > 50 and set(np.diff(heads)) == {4}
    assert all((cls[h + 1:h + 4] == BODY).all() for h in heads[:-1])
    assert not ((cls == CIRCLE).any())
    assert set(np.unique(cls)) <= {NONE, SLIDER, BODY}


def test_a_long_silence_over_playing_music_lowers_the_head_threshold():
    from osu_ai_sequence.decode import FLOOR, stuck_threshold
    assert stuck_threshold(0.5, 1.0) == 0.5
    assert stuck_threshold(0.5, 2.5) < 0.2
    assert stuck_threshold(0.5, 30.0) == FLOOR


def test_a_model_that_never_clicks_still_follows_music_that_plays():
    torch.manual_seed(0)
    model = PulsoS(6, d=32, heads=2, encoder_layers=1, decoder_layers=1).eval()
    with torch.no_grad():
        model.cls.bias.copy_(torch.tensor([3.0, 0.0, -6.0]))  # a head about 5% of the time, below the 0.5 threshold
    n = 300
    tick = 0.1
    silent, _, _, _, _ = generate(model, torch.randn(n, 6), np.ones(n, dtype=bool), 0.5)
    heard, _, _, _, _ = generate(model, torch.randn(n, 6), np.ones(n, dtype=bool), 0.5, np.ones(n, dtype=bool), tick)
    assert (silent == CIRCLE).sum() == 0
    gaps = np.diff(np.flatnonzero(heard == CIRCLE)) * tick
    assert len(gaps) > 5 and gaps.max() < 3.5
