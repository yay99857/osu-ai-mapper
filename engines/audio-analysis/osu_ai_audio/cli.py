import argparse
import json
import os
import sys

from .analysis import ManualTiming, analyze
from .transcode import to_mp3


def main() -> int:
    parser = argparse.ArgumentParser(prog="osu-ai-analyze", description="Analyse a local audio file for mapping.")
    parser.add_argument("audio", help="any format FFmpeg can decode")
    parser.add_argument("output_dir", help="receives audio.mp3 (the file to package) and analysis.json")
    parser.add_argument("--bpm", type=float, help="correct the tempo instead of detecting it")
    parser.add_argument("--offset-ms", type=float, help="first downbeat in ms of the delivered audio; needs --bpm")
    args = parser.parse_args()

    if args.offset_ms is not None and args.bpm is None:
        parser.error("--offset-ms needs --bpm")
    manual = ManualTiming(args.bpm, args.offset_ms) if args.bpm is not None else None

    os.makedirs(args.output_dir, exist_ok=True)
    mp3 = os.path.join(args.output_dir, "audio.mp3")
    to_mp3(args.audio, mp3)

    result = analyze(mp3, manual)
    with open(os.path.join(args.output_dir, "analysis.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"))

    t = result["timing"]
    alternatives = ", ".join(f"{a['bpm']:.1f}: {a['gridScore']:.3f}" for a in t["alternatives"])
    print(f"{t['source']} {t['bpm']:.3f} BPM (grid {t['gridScore']:.3f}; {alternatives}), offset {t['offsetUs'] / 1000:.1f} ms, "
          f"tracker {t['trackerBpm']:.2f} BPM, inliers {t['inlierRatio']:.2f}, "
          f"{result['audio']['durationUs'] / 1e6:.1f} s, {len(result['sections'])} sections", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
