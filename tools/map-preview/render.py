"""Draw combos of a BeatmapIR JSON as an image grid, for reviewing patterns by eye.

    uv run --with matplotlib tools/map-preview/render.py beatmap-ir.json out.png [first-combo] [count]

Each panel is one combo on the 512x384 playfield: numbered circles, slider bodies, the path the
cursor travels, and the previous combo's last object greyed for context.
"""

import json
import sys

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.patches import Circle  # noqa: E402


def bezier_points(head, control_points, n=24):
    pts = [(head[0] + p["x"], head[1] + p["y"]) for p in control_points]
    if len(pts) == 2:
        return pts
    out = []
    for i in range(n + 1):
        t = i / n
        # de Casteljau over all control points (single Bézier segment, as generated)
        level = pts
        while len(level) > 1:
            level = [((1 - t) * a[0] + t * b[0], (1 - t) * a[1] + t * b[1]) for a, b in zip(level, level[1:])]
        out.append(level[0])
    return out


def main():
    path, out = sys.argv[1], sys.argv[2]
    first = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    count = int(sys.argv[4]) if len(sys.argv) > 4 else 12

    ir = json.load(open(path, encoding="utf-8"))
    cs = ir["settings"]["cs"]
    radius = 54.4 - 4.48 * cs
    beat = ir["timing"]["segments"][0]["beatLengthUs"]

    combos, current = [], []
    for o in ir["objects"]:
        if o["newCombo"] and current:
            combos.append(current)
            current = []
        current.append(o)
    combos.append(current)

    selected = combos[first:first + count]
    cols = 4
    rows = (len(selected) + cols - 1) // cols
    fig, axes = plt.subplots(rows, cols, figsize=(cols * 4.2, rows * 3.4))
    axes = axes.flatten() if hasattr(axes, "flatten") else [axes]

    for ax, combo, k in zip(axes, selected, range(first, first + count)):
        ax.set_xlim(0, 512)
        ax.set_ylim(384, 0)
        ax.set_aspect("equal")
        ax.set_xticks([])
        ax.set_yticks([])
        t0 = combo[0]["timeUs"] / 1e6
        ax.set_title(f"combo {k}  t={t0:.1f}s", fontsize=9)

        cursor = []
        for n, o in enumerate(combo, 1):
            head = (o["position"]["x"], o["position"]["y"])
            cursor.append(head)
            if o["kind"] == "slider":
                body = bezier_points(head, o["path"]["controlPoints"])
                xs, ys = zip(*body)
                ax.plot(xs, ys, color="#e8a33d", linewidth=radius * 0.55, alpha=0.35, solid_capstyle="round")
                ax.plot(xs, ys, color="#c07a10", linewidth=1)
                end = body[-1] if o["spanCount"] % 2 == 1 else head
                if o["spanCount"] > 1:
                    ax.annotate(f"x{o['spanCount']}", body[-1], fontsize=7, color="#c07a10")
                cursor.append(end)
            ax.add_patch(Circle(head, radius, fill=False, color="#3d6fe8", linewidth=1.2))
            gap = ""
            if n > 1:
                prev = combo[n - 2]
                prev_end = prev["timeUs"] + (prev["spanCount"] * prev["expectedDistancePx"] / (ir["settings"]["sliderMultiplier"] * 100) * beat if prev["kind"] == "slider" else 0)
                gap = f"{(o['timeUs'] - prev_end) / beat:.2g}"
            ax.text(head[0], head[1], str(n), ha="center", va="center", fontsize=8, color="#1b3a8a")
            if gap:
                ax.text(head[0] + radius * 0.9, head[1] - radius * 0.9, gap, fontsize=6, color="#777")

        xs, ys = zip(*cursor)
        ax.plot(xs, ys, color="#999", linewidth=0.7, linestyle="--")

    for ax in axes[len(selected):]:
        ax.axis("off")

    fig.tight_layout()
    fig.savefig(out, dpi=80)


if __name__ == "__main__":
    main()
