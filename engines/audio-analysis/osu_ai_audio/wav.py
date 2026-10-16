"""osu-ai-to-wav <audio> <out.wav>

Decodes any audio the analyzer can read to 16-bit stereo WAV at 44.1 kHz on the same timeline, for
tools that cannot decode MP3 without FFmpeg on the PATH (Mapperatorinator reads WAV natively).
"""

from __future__ import annotations

import os
import sys

import librosa
import soundfile as sf


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    source, target = sys.argv[1:]
    y, sr = librosa.load(source, sr=44_100, mono=False)
    if y.ndim == 1:
        y = y[None, :].repeat(2, axis=0)
    tmp = target + ".tmp.wav"
    sf.write(tmp, y.T, sr, subtype="PCM_16")
    os.replace(tmp, target)
    return 0


if __name__ == "__main__":
    sys.exit(main())
