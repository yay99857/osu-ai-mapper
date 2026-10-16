"""Fetch the beatmaps one mapper made, for local training data.

    python tools/map-fetch/osu_fetch.py list  <mapper> [options]
    python tools/map-fetch/osu_fetch.py fetch <mapper> [options]

Search uses the official osu! API v2 with your own OAuth application (client credentials): create it
under osu! account settings -> OAuth, then put the id and secret in artifacts/fetch/osu-api.json
({"client_id": ..., "client_secret": "..."}). The file stays local (artifacts/ is ignored by git).
The API does not serve .osz files to applications, so packages come from public mirrors
(nerinyan, then catboy), each checked to be a real zip; no video by default.

A set counts only for the difficulties the mapper made, read from each difficulty's owners, not
from the set's host: a "Sotarks" set often holds guest difficulties by others, and the mapper's own
difficulties often live in other people's sets. fetch writes <out>/<set id> <artist> - <title>.osz
and <out>/manifest.json listing, per package, its hash and the mapper's difficulty names, which
osu_ai_sequence.finetune reads to pick exactly those difficulties.

Options:
  --out DIR          default artifacts/sources-<mapper>
  --status LIST      ranked,loved,guest (default) and/or graveyard,pending
  --min-stars N      only sets where one of the mapper's osu! difficulties reaches N (default 0)
  --max-sets N       stop after N packages (default 50)
  --order KIND       stars (hardest first, default) or recent (newest sets first)
  --delay S          seconds between downloads (default 4)
  --with-video       keep videos
  --with-marathons   keep compilations, marathons and medleys (skipped by default: many songs in one map)

Downloading maps does not grant the right to train on them or share what is derived from them; the
user records a rights grant (tools/dataset-builder rights add) before ingesting. Use local only.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

API = "https://osu.ppy.sh/api/v2"
TOKEN = "https://osu.ppy.sh/oauth/token"
CONFIG = os.path.join("artifacts", "fetch", "osu-api.json")
USER_AGENT = "osu-ai-mapper-fetch/0.1 (local research; one request at a time)"
MIRRORS = (
    ("nerinyan", "https://api.nerinyan.moe/d/{id}", "?nv=1"),
    ("osu.direct", "https://osu.direct/api/d/{id}", "?noVideo=1"),
    ("beatconnect", "https://beatconnect.io/b/{id}", ""),
    ("catboy", "https://catboy.best/d/{id}", "n"),
)


def request(url: str, data: dict | None = None, token: str | None = None, timeout: int = 60) -> bytes:
    headers = {"User-Agent": USER_AGENT, "Accept": "application/json"}
    body = None
    if data is not None:
        body = json.dumps(data).encode()
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    with urllib.request.urlopen(urllib.request.Request(url, body, headers), timeout=timeout) as r:
        return r.read()


def token() -> str:
    if not os.path.exists(CONFIG):
        os.makedirs(os.path.dirname(CONFIG), exist_ok=True)
        with open(CONFIG, "w", encoding="utf-8") as f:
            json.dump({"client_id": 0, "client_secret": ""}, f, indent=2)
        raise SystemExit(f"Fill in {CONFIG} with your osu! OAuth application's client id and secret (osu! settings -> OAuth -> New OAuth Application).")
    cfg = json.load(open(CONFIG, encoding="utf-8-sig"))
    if not cfg.get("client_id") or not cfg.get("client_secret"):
        raise SystemExit(f"{CONFIG} has no client id or secret yet.")
    answer = json.loads(request(TOKEN, {"client_id": cfg["client_id"], "client_secret": cfg["client_secret"],
                                        "grant_type": "client_credentials", "scope": "public"}))
    return answer["access_token"]


def api(path: str, tok: str, **params) -> object:
    query = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
    time.sleep(1.0)  # well under the API's 60 requests a minute
    return json.loads(request(f"{API}{path}{'?' + query if query else ''}", token=tok))


def owners(beatmap: dict) -> set[int]:
    ids = {o["id"] for o in beatmap.get("owners") or []}
    return ids or {beatmap.get("user_id")}


MARATHON = re.compile(r"compilation|marathon|medley", re.IGNORECASE)


def mapper_sets(name: str, tok: str, statuses: list[str], min_stars: float, skip_marathons: bool = True):
    user = api(f"/users/{urllib.parse.quote(name)}/osu", tok, key="username")
    uid = user["id"]
    print(f"{user['username']} (id {uid})", file=sys.stderr)
    seen = {}
    for status in statuses:
        offset = 0
        while True:
            page = api(f"/users/{uid}/beatmapsets/{status}", tok, limit=50, offset=offset)
            if not page:
                break
            for s in page:
                if s["id"] in seen:
                    continue
                full = s if "beatmaps" in s else api(f"/beatmapsets/{s['id']}", tok)
                mine = [b for b in full.get("beatmaps", []) if uid in owners(b) and b.get("mode") == "osu"]
                if skip_marathons and MARATHON.search(f"{s['title']} {' '.join(b['version'] for b in mine)}"):
                    continue
                if not mine or max(b["difficulty_rating"] for b in mine) < min_stars:
                    continue
                seen[s["id"]] = {"id": s["id"], "artist": s["artist"], "title": s["title"], "host": s["creator"], "status": s["status"],
                                 "difficulties": sorted({b["version"] for b in mine}),
                                 "stars": [round(b["difficulty_rating"], 2) for b in sorted(mine, key=lambda b: b["difficulty_rating"])]}
            offset += len(page)
            if len(page) < 50:
                break
    return user["username"], uid, list(seen.values())


def safe_name(text: str) -> str:
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", text).strip(" .")[:120]


def download(set_id: int, video: bool, attempts: int = 2) -> tuple[bytes, str]:
    """Each mirror in turn; a second round after a pause, since mirrors answer 404 for a while when a
    set is not in their cache yet."""
    errors = []
    for attempt in range(attempts):
        if attempt:
            time.sleep(20)
        try:
            return _download_once(set_id, video, errors)
        except RuntimeError:
            continue
    raise RuntimeError("; ".join(errors[-len(MIRRORS):]))


def _download_once(set_id: int, video: bool, errors: list) -> tuple[bytes, str]:
    for name, url, no_video in MIRRORS:
        target = url.format(id=set_id) + ("" if video else no_video)
        try:
            data = request(target, timeout=180)
        except (urllib.error.URLError, TimeoutError) as e:
            errors.append(f"{name}: {e}")
            continue
        if data[:2] == b"PK" and zipfile.is_zipfile(__import__("io").BytesIO(data)):
            return data, name
        errors.append(f"{name}: not a package ({len(data)} bytes)")
    raise RuntimeError("no mirror served the set")


def main() -> int:
    args = sys.argv[1:]
    if len(args) < 2 or args[0] not in ("list", "fetch"):
        print(__doc__, file=sys.stderr)
        return 2
    command, mapper = args[0], args[1]
    opts = {"--out": None, "--status": "ranked,loved,guest", "--min-stars": "0", "--max-sets": "50", "--delay": "4", "--order": "stars"}
    video = "--with-video" in args
    for k in opts:
        if k in args:
            opts[k] = args[args.index(k) + 1]
    out = opts["--out"] or os.path.join("artifacts", f"sources-{safe_name(mapper.lower())}")

    tok = token()
    username, uid, sets = mapper_sets(mapper, tok, opts["--status"].split(","), float(opts["--min-stars"]), "--with-marathons" not in args)
    sets.sort(key=lambda s: -s["id"] if opts["--order"] == "recent" else -max(s["stars"]))
    print(f"{len(sets)} sets with osu! difficulties by {username}; {sum(len(s['difficulties']) for s in sets)} difficulties", file=sys.stderr)
    if command == "list":
        for s in sets:
            print(f"{s['id']:>8} {s['status']:7s} {max(s['stars']):4.1f}* host {s['host'][:14]:14s} {s['artist'][:20]} - {s['title'][:30]} {s['difficulties']}")
        return 0

    os.makedirs(out, exist_ok=True)
    manifest_path = os.path.join(out, "manifest.json")
    manifest = json.load(open(manifest_path, encoding="utf-8")) if os.path.exists(manifest_path) else {"mapper": username, "mapperId": uid, "packages": []}
    have = {p["setId"] for p in manifest["packages"]}
    todo = [s for s in sets if s["id"] not in have][: max(0, int(opts["--max-sets"]) - len(have))]
    print(f"{len(have)} already here, fetching {len(todo)} into {out}", file=sys.stderr)
    for k, s in enumerate(todo, 1):
        try:
            data, mirror = download(s["id"], video)
        except RuntimeError as e:
            print(f"[{k}/{len(todo)}] {s['id']} FAILED {e}", file=sys.stderr, flush=True)
            continue
        name = safe_name(f"{s['id']} {s['artist']} - {s['title']}") + ".osz"
        with open(os.path.join(out, name + ".part"), "wb") as f:
            f.write(data)
        os.replace(os.path.join(out, name + ".part"), os.path.join(out, name))
        manifest["packages"].append({"setId": s["id"], "file": name, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
                                     "mirror": mirror, "status": s["status"], "host": s["host"], "mapperDifficulties": s["difficulties"],
                                     "fetchedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
        with open(manifest_path + ".tmp", "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=1, ensure_ascii=False)
        os.replace(manifest_path + ".tmp", manifest_path)
        print(f"[{k}/{len(todo)}] {name} {len(data) / 1e6:.1f} MB via {mirror}", file=sys.stderr, flush=True)
        time.sleep(float(opts["--delay"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
