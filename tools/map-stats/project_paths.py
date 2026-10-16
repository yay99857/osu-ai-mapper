"""Where a generated song folder keeps its files: a project (project.json) or the older flat layout."""

from __future__ import annotations

import json
import os


def _manifest(folder: str) -> dict | None:
    path = os.path.join(folder, "project.json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def beatmap_ir(folder: str) -> str | None:
    """BeatmapIR of the active revision, or the flat beatmap-ir.json; None when there is none."""
    manifest = _manifest(folder)
    if manifest is not None:
        active = next((r for r in manifest.get("revisions", []) if r["id"] == manifest.get("activeRevision")), None)
        return os.path.join(folder, active["beatmapIr"]) if active else None
    flat = os.path.join(folder, "beatmap-ir.json")
    return flat if os.path.exists(flat) else None


def analysis(folder: str) -> str | None:
    """analysis.json in use, or the flat one; None when there is none."""
    manifest = _manifest(folder)
    if manifest is not None:
        ref = manifest.get("analysis")
        return os.path.join(folder, ref["relativePath"], "analysis.json") if ref else None
    flat = os.path.join(folder, "analysis.json")
    return flat if os.path.exists(flat) else None
