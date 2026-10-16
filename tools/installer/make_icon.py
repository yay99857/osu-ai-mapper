"""Draws the app icon (a pink osu! circle with a white ring) and writes it as a multi-size .ico.

    python tools/installer/make_icon.py apps/desktop/Assets/app.ico

Pure standard library: each size is drawn with 4x4 supersampling and stored as a PNG entry.
"""

from __future__ import annotations

import struct
import sys
import zlib

PINK = (0xFF, 0x66, 0xAA)
WHITE = (0xFF, 0xFF, 0xFF)


def pixel(x: float, y: float, size: int) -> tuple[int, int, int, int]:
    c = size / 2
    r = ((x - c) ** 2 + (y - c) ** 2) ** 0.5 / c  # 0 at the centre, 1 at the edge
    if r > 0.96:
        return (0, 0, 0, 0)
    if 0.58 <= r <= 0.74:
        return (*WHITE, 255)
    return (*PINK, 255)


def png(size: int) -> bytes:
    rows = bytearray()
    n = 4
    for y in range(size):
        rows.append(0)
        for x in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(n):
                for sx in range(n):
                    p = pixel(x + (sx + 0.5) / n, y + (sy + 0.5) / n, size)
                    a = p[3]
                    acc[0] += p[0] * a
                    acc[1] += p[1] * a
                    acc[2] += p[2] * a
                    acc[3] += a
            alpha = acc[3] / (n * n)
            if acc[3]:
                rows += bytes((round(acc[0] / acc[3]), round(acc[1] / acc[3]), round(acc[2] / acc[3]), round(alpha)))
            else:
                rows += bytes((0, 0, 0, 0))

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")


def main() -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [png(s) for s in sizes]
    out = bytearray(struct.pack("<HHH", 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for size, data in zip(sizes, images):
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for data in images:
        out += data
    with open(sys.argv[1], "wb") as f:
        f.write(out)


if __name__ == "__main__":
    main()
