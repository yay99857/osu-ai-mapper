"""Pulso-S: a small encoder-decoder transformer over one phrase of 1/4 ticks.

The encoder reads the audio features of the whole window in both directions. The decoder walks the
ticks in order: at tick t it sees the encoder's view of t, what was decided at t-1 (none, circle,
slider head, inside a slider), how many ticks have passed since the last head, and through causal
self-attention every earlier decision of the window. That memory of its own choices is what the
gradient-boosted Pulso lacks: it decided each tick nearly on its own.
"""

from __future__ import annotations

import torch
from torch import nn

from .data import MAX_LENGTH

WINDOW = 256
SINCE_CAP = 32


class PulsoS(nn.Module):
    def __init__(self, n_features: int, d: int = 128, heads: int = 4, encoder_layers: int = 3, decoder_layers: int = 2, dropout: float = 0.1):
        super().__init__()
        self.n_features = n_features
        self.config = {"n_features": n_features, "d": d, "heads": heads, "encoder_layers": encoder_layers, "decoder_layers": decoder_layers, "dropout": dropout}
        self.audio = nn.Sequential(nn.Linear(n_features, d), nn.GELU(), nn.Linear(d, d))
        self.position = nn.Embedding(WINDOW, d)
        self.encoder = nn.TransformerEncoder(nn.TransformerEncoderLayer(d, heads, 2 * d, dropout, batch_first=True, norm_first=True), encoder_layers)
        self.previous = nn.Embedding(5, d)  # none, circle, slider head, body, start of window
        self.since = nn.Embedding(SINCE_CAP + 1, d)
        self.decoder = nn.TransformerDecoder(nn.TransformerDecoderLayer(d, heads, 2 * d, dropout, batch_first=True, norm_first=True), decoder_layers)
        self.norm = nn.LayerNorm(d)
        self.cls = nn.Linear(d, 3)  # none, circle, slider head
        self.length = nn.Linear(d, MAX_LENGTH + 1)
        self.dist = nn.Linear(d, 1)
        self.register_buffer("causal", torch.triu(torch.full((WINDOW, WINDOW), float("-inf")), diagonal=1), persistent=False)

    def encode(self, x: torch.Tensor) -> torch.Tensor:
        n = x.shape[1]
        pos = torch.arange(n, device=x.device)
        return self.encoder(self.audio(x) + self.position(pos)[None])

    def decode(self, memory: torch.Tensor, previous: torch.Tensor, since: torch.Tensor):
        """Decisions for the first len(previous) ticks; cross-attention always sees the whole window."""
        n = previous.shape[1]
        h = memory[:, :n] + self.previous(previous) + self.since(since.clamp(0, SINCE_CAP))
        h = self.norm(self.decoder(h, memory, tgt_mask=self.causal[:n, :n], tgt_is_causal=True))
        return self.cls(h), self.length(h), self.dist(h).squeeze(-1)

    def forward(self, x, previous, since):
        return self.decode(self.encode(x), previous, since)


def shifted_inputs(cls: torch.Tensor):
    """Decoder inputs from a label sequence: the label of the previous tick (4 = start) and ticks
    since the last head, both as known before deciding the current tick."""
    b, n = cls.shape
    previous = torch.full_like(cls, 4)
    previous[:, 1:] = cls[:, :-1]
    since = torch.full_like(cls, SINCE_CAP)
    head = (cls == 1) | (cls == 2)
    last = torch.full((b,), -10_000, dtype=torch.long, device=cls.device)
    for t in range(n):
        since[:, t] = torch.clamp(t - last, 0, SINCE_CAP)
        last = torch.where(head[:, t], torch.full_like(last, t), last)
    return previous, since
