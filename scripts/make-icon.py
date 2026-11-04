"""Draws the IDM-Rea icon (a progress ring split into three parts around a small chevron)
into src/IDMRea.App/Assets/AppIcon.ico and AppIcon.png. Standard library only.

Run: python scripts/make-icon.py
"""
import math
import os
import struct
import zlib

TILE = (0x1F, 0x1D, 0x1B)
RING = (0xC4, 0x65, 0x32)
MARK = (0xF4, 0xEF, 0xEA)
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SAMPLES = 4  # per axis, for anti-aliasing


def shape(size):
    """Geometry in a 96-unit square. Small sizes get heavier strokes so the mark stays readable."""
    if size <= 24:
        return dict(radius=25.0, ring=10.0, gap=11.0, chevron=((37, 43), (48, 54), (59, 43)), stroke=8.0)
    if size <= 48:
        return dict(radius=24.5, ring=8.0, gap=10.0, chevron=((39, 44), (48, 53), (57, 44)), stroke=6.0)
    return dict(radius=24.0, ring=7.0, gap=9.27, chevron=((40, 44), (48, 52), (56, 44)), stroke=5.0)


def in_tile(x, y):
    corner, half = 22.0, 48.0
    qx = abs(x - 48) - (half - corner)
    qy = abs(y - 48) - (half - corner)
    outside = math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - corner
    return outside <= 0


def in_ring(x, y, g):
    dx, dy = x - 48, y - 48
    if abs(math.hypot(dx, dy) - g["radius"]) > g["ring"] / 2:
        return False
    # Clockwise on screen from 12 o'clock, three equal dashes.
    turn = (math.atan2(dy, dx) + math.pi / 2) % (2 * math.pi)
    period = 2 * math.pi * g["radius"] / 3
    return (turn * g["radius"]) % period < period - g["gap"]


def segment_distance(x, y, a, b):
    ax, ay = a
    bx, by = b
    vx, vy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * vx + (y - ay) * vy) / (vx * vx + vy * vy)))
    return math.hypot(x - (ax + t * vx), y - (ay + t * vy))


def in_chevron(x, y, g):
    p = g["chevron"]
    return min(segment_distance(x, y, p[0], p[1]), segment_distance(x, y, p[1], p[2])) <= g["stroke"] / 2


def render(size):
    g = shape(size)
    scale = 96.0 / size
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            r = gr = b = a = 0.0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    x = (px + (sx + 0.5) / SAMPLES) * scale
                    y = (py + (sy + 0.5) / SAMPLES) * scale
                    if not in_tile(x, y):
                        continue
                    color = MARK if in_chevron(x, y, g) else RING if in_ring(x, y, g) else TILE
                    r += color[0]
                    gr += color[1]
                    b += color[2]
                    a += 1
            n = SAMPLES * SAMPLES
            if a == 0:
                row += b"\x00\x00\x00\x00"
            else:
                row += bytes((round(r / a), round(gr / a), round(b / a), round(255 * a / n)))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + row for row in rows)
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def ico(images):
    directory = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    body = b""
    for size, data in images:
        side = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        body += data
    return directory + body


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "src", "IDMRea.App", "Assets")
    os.makedirs(assets, exist_ok=True)
    images = [(size, png(size, render(size))) for size in ICO_SIZES]
    with open(os.path.join(assets, "AppIcon.ico"), "wb") as f:
        f.write(ico(images))
    with open(os.path.join(assets, "AppIcon.png"), "wb") as f:
        f.write(dict(images)[256])
    print("Wrote AppIcon.ico (" + ", ".join(str(s) for s in ICO_SIZES) + ") and AppIcon.png (256)")


if __name__ == "__main__":
    main()
