"""osu-ai-separate <audio> <analysis.json> <stems.json> [--threads N]
osu-ai-separate --dataset <dataset> <analysis-root> [--threads N] [--shard K/N]

Writes the stems of one song next to its analysis, or of every analysed dataset song that has none
yet (resumable). The first run downloads the Demucs weights (htdemucs, about 80 MB) to the torch cache.
"""

from __future__ import annotations

import json
import os
import sys
import time

from .stems import SCHEMA_VERSION, analyse


def write(result: dict, path: str) -> None:
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        json.dump(result, f, separators=(",", ":"))
    os.replace(path + ".tmp", path)


def current(path: str) -> bool:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f).get("schemaVersion") == SCHEMA_VERSION
    except (OSError, ValueError):
        return False


def main() -> int:
    args = sys.argv[1:]
    threads = 8
    shard, shards = 0, 1
    if "--shard" in args:
        i = args.index("--shard")
        shard, shards = (int(v) for v in args[i + 1].split("/"))
        del args[i:i + 2]
    if "--threads" in args:
        i = args.index("--threads")
        threads = int(args[i + 1])
        del args[i:i + 2]

    if args and args[0] == "--dataset" and len(args) == 3:
        dataset, root = args[1], args[2]
        audio_of = {}
        with open(os.path.join(dataset, "manifests", "maps.jsonl"), encoding="utf-8") as f:
            for line in f:
                m = json.loads(line)
                if m.get("audioSha256") and m.get("audioRef"):  # excluded maps carry no audio
                    audio_of.setdefault(m["audioSha256"][:12], os.path.join(dataset, m["audioRef"]))
        todo = [d for d in sorted(os.listdir(root)) if os.path.exists(os.path.join(root, d, "analysis.json"))
                and not current(os.path.join(root, d, "stems.json")) and d in audio_of]
        # Parallel runs take disjoint shares by position in the full sorted list.
        everything = sorted(os.listdir(root))
        todo = [d for d in todo if everything.index(d) % shards == shard]
        print(f"{len(todo)} songs to separate", file=sys.stderr, flush=True)
        failed = 0
        for k, d in enumerate(todo, 1):
            start = time.time()
            try:
                with open(os.path.join(root, d, "analysis.json"), encoding="utf-8") as f:
                    analysis = json.load(f)
                write(analyse(audio_of[d], analysis, threads), os.path.join(root, d, "stems.json"))
                print(f"[{k}/{len(todo)}] {d} {time.time() - start:.0f}s", file=sys.stderr, flush=True)
            except Exception as e:  # one bad file must not stop the batch
                failed += 1
                print(f"[{k}/{len(todo)}] {d} FAILED {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        return 1 if failed else 0

    if len(args) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    audio, analysis_path, out = args
    with open(analysis_path, encoding="utf-8") as f:
        analysis = json.load(f)
    write(analyse(audio, analysis, threads), out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
