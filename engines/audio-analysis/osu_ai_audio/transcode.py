"""Turn any input FFmpeg can read into the MP3 that both the analysis and the package use.

Analysing the delivered file, rather than the source, keeps one timeline: the encoder delay is
recorded in the LAME header, which mpg123 (analysis) and BASS (the game) both honour.
"""

from __future__ import annotations

import av

MAX_SECONDS = 10 * 60  # product limit from the system architecture


def to_mp3(source: str, destination: str, sample_rate: int = 44_100, bitrate: int = 320_000) -> float:
    """Returns the decoded duration in seconds."""
    with av.open(source) as src:
        streams = [s for s in src.streams if s.type == "audio"]
        if not streams:
            raise ValueError("Input has no audio stream.")
        stream = streams[0]

        with av.open(destination, "w", format="mp3") as dst:
            out = dst.add_stream("libmp3lame", rate=sample_rate, layout="stereo")
            out.bit_rate = bitrate
            resampler = av.AudioResampler(format="s16p", layout="stereo", rate=sample_rate)
            samples = 0

            for frame in src.decode(stream):
                for converted in resampler.resample(frame):
                    samples += converted.samples
                    if samples > MAX_SECONDS * sample_rate:
                        raise ValueError("Audio is longer than the supported 10 minutes.")
                    for packet in out.encode(converted):
                        dst.mux(packet)

            for converted in resampler.resample(None):
                samples += converted.samples
                for packet in out.encode(converted):
                    dst.mux(packet)

            for packet in out.encode(None):
                dst.mux(packet)

    return samples / sample_rate
