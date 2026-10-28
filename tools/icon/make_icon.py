"""Draws the DeadlockScout icon, a pixel-art eye whose iris is a pair of binoculars, and writes
src/DeadlockScout.App/Assets/app.ico (the exe, window and tray icon).
Run: python3 tools/icon/make_icon.py [preview-folder]"""
import struct, sys, zlib
from pathlib import Path

PALETTE = {
    ".": None,
    "K": (22, 25, 32),       # outline
    "W": (242, 244, 246),    # sclera
    "S": (196, 205, 214),    # sclera shadow under the lid
    "B": (44, 49, 60),       # binocular body
    "G": (92, 102, 120),     # body highlight
    "L": (58, 160, 216),     # lens
    "D": (21, 94, 138),      # lens shade
    "H": (232, 251, 255),    # glint
}

# One hand-placed 16 px sprite; every other size is this sprite scaled up without smoothing.
# An almond eye, lashes on the upper lid, and for the iris a pair of binoculars seen from the front:
# narrow eyepieces under the lid, a bridge with the focus wheel, and two wider barrels with their lenses below.
SPRITE = [
    "................",
    "................",
    "..K..........K..",
    "...K..K..K..K...",
    "....KKKKKKKK....",
    "..KKSBBSSBBSKK..",
    "..KWWBBGGBBWWK..",
    ".KWWBBBBBBBBWWK.",
    "KWWWBHLBBHLBWWWK",
    ".KWWBLDBBLDBWWK.",
    "..KWWBBWWBBWWK..",
    "..KKWWWWWWWWKK..",
    "....KKKKKKKK....",
    "................",
    "................",
    "................",
]


def rgba(grid, n):
    """The sprite at n x n, nearest neighbour (20 and 24 px double some rows and columns; that is how pixel art scales)."""
    m = len(grid)
    px = []
    for y in range(n):
        row = grid[y * m // n]
        for x in range(n):
            c = PALETTE[row[x * m // n]]
            px.append((0, 0, 0, 0) if c is None else (*c, 255))
    return n, px


def png(n, px):
    raw = b"".join(b"\x00" + bytes(v for p in px[y * n:(y + 1) * n] for v in p) for y in range(n))
    chunk = lambda t, d: struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def dib(n, px):
    """Classic 32-bit DIB entry (what System.Drawing and the shell read best below 256 px)."""
    header = struct.pack("<IiiHHIIiiII", 40, n, n * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = b"".join(bytes((p[2], p[1], p[0], p[3])) for y in reversed(range(n)) for p in px[y * n:(y + 1) * n])
    stride = ((n + 31) // 32) * 4
    and_rows = []
    for y in reversed(range(n)):
        bits = 0
        row = bytearray(stride)
        for x in range(n):
            if px[y * n + x][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        and_rows.append(bytes(row))
    return header + xor + b"".join(and_rows)


def ico(images):
    head = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for n, data in images:
        entries += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return head + entries + blobs


SIZES = (16, 20, 24, 32, 40, 48, 64, 256)


def build():
    images = []
    for size in SIZES:
        n, px = rgba(SPRITE, size)
        images.append((n, png(n, px) if size == 256 else dib(n, px)))  # 256 px as PNG, as Windows expects
    return images


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[2]
    out = root / "src/DeadlockScout.App/Assets/app.ico"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(ico(build()))
    if len(sys.argv) > 1:  # optional folder for preview PNGs
        for size in SIZES:
            n, px = rgba(SPRITE, size)
            (Path(sys.argv[1]) / f"icon-{size}.png").write_bytes(png(n, px))
    print(f"wrote {out} ({out.stat().st_size} bytes)")
