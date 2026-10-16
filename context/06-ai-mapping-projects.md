# Other AI mapping projects

## Mapperatorinator (OliBomby), the most complete public one

- **Model**: a modified Whisper encoder-decoder (~219M parameters). The encoder reads mel
  spectrogram frames; the decoder writes a sparse sequence of events (objects, hitsounds, slider
  velocity, new combos, timing points), only where something happens.
- **Tokens**: time in 10 ms steps, positions on a 32 px grid (later versions refine to 2 px).
- **Conditioning before the sequence**: game mode, difficulty, **mapper id**, **year**, circle size,
  slider velocity, descriptor tags (e.g. "jumps", "clean"), with random "unknown" substitution
  during training so each can be left out; classifier-free guidance, including negative descriptors.
- **Long songs**: 16.4 s windows with heavy overlap; the decoder is pre-filled with the previous
  window's tokens to avoid seams.
- **Positions**: a diffusion model (osu-diffusion) refines the coarse positions, re-fitting slider
  ends to their lengths at each step.
- **Timing**: inferred many times and averaged; useful for variable BPM.
- **Training**: ranked beatmaps (public datasets), about 5,700 GPU hours over 261 runs.
- **Extras**: remapping a section (in-painting), guest difficulties in a set's context,
  hitsound generation, an AI modding tool ranking surprising objects.
- **Stated limits**: style and song must agree (a high-SR, high-SV map for a calm song is hard to
  get); seams between windows; specify difficulty and year for consistent output.

## osuT5 (gyataro, archived 2024)

Transformer encoder-decoder after Google Magenta's MT3: spectrogram in, event tokens out. BPM and
offset are given by the user rather than learned. Mapperatorinator grew from it.

## For our project

Use these references to review Mapperatorinator output against the music and human maps.
The active engine supplies rhythm and placement. See
[the mapping principles](../docs/mapping-principles.md) and
[the architecture](../docs/architecture.md).

Sources: [Mapperatorinator](https://github.com/OliBomby/Mapperatorinator),
[osuT5](https://github.com/gyataro/osuT5),
[Mapperatorinator2](https://github.com/Tiger14n/Mapperatorinator2).
