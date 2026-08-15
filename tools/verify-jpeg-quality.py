#!/usr/bin/env python3
"""
Checks JpegQuality.cs against real JPEG files of known quality.

Written after the estimator shipped with a bug the original test could not
find: it compared DQT values in stored (zig-zag) order against a reference in
natural order, under-estimating throughout - quality 60 read as 54, quality 40
as 35.

The "verification" was a Python round trip that encoded AND decoded through the
same wrong ordering, so it reported perfect accuracy. A test written against the
implementation proves only that the implementation is self-consistent.

This one uses Pillow to write genuine JPEGs at known qualities and reads the DQT
back out of the actual bytes.

    pip install pillow
    python3 tools/verify-jpeg-quality.py
"""
import argparse
import io
import subprocess
import sys

try:
    from PIL import Image
except ImportError:
    print("needs Pillow: pip install pillow")
    sys.exit(2)

STD_LUMINANCE = [16,11,10,16,24,40,51,61, 12,12,14,19,26,58,60,55,
                 14,13,16,24,40,57,69,56, 14,17,22,29,51,87,80,62,
                 18,22,37,56,68,109,103,77, 24,35,55,64,81,104,113,92,
                 49,64,78,87,103,121,120,101, 72,92,95,98,112,100,103,99]

ZIGZAG_TO_NATURAL = [ 0, 1, 8,16, 9, 2, 3,10, 17,24,32,25,18,11, 4, 5,
                     12,19,26,33,40,48,41,34, 27,20,13, 6, 7,14,21,28,
                     35,42,49,56,57,50,43,36, 29,22,15,23,30,37,44,51,
                     58,59,52,45,38,31,39,46, 53,60,61,54,47,55,62,63]


def read_dqt_table_zero(data: bytes):
    """Pull table id 0 out of a real JPEG, as stored (zig-zag)."""
    if data[0:2] != b"\xff\xd8":
        return None
    i = 2
    while i < len(data) - 1:
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        i += 2
        if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
            continue
        if marker == 0xDA:
            return None
        length = (data[i] << 8) | data[i + 1]
        if marker == 0xDB:
            payload = data[i + 2:i + length]
            off = 0
            while off < len(payload):
                pq, tq = payload[off] >> 4, payload[off] & 0x0F
                off += 1
                table = []
                for _ in range(64):
                    if pq == 0:
                        table.append(payload[off]); off += 1
                    else:
                        table.append((payload[off] << 8) | payload[off + 1]); off += 2
                if tq == 0:
                    return table
            return None
        i += length


def estimate(stored):
    natural = [0] * 64
    for k in range(64):
        natural[ZIGZAG_TO_NATURAL[k]] = stored[k]

    if all(v == 1 for v in natural):
        return 100

    total, counted = 0.0, 0
    for i, v in enumerate(natural):
        if v <= 1 or v >= 255:
            continue
        s = (v * 100.0 - 50.0) / STD_LUMINANCE[i]
        if s > 0:
            total += s
            counted += 1

    if counted < 8:
        return -1
    avg = total / counted
    q = 5000.0 / avg if avg > 100.0 else (200.0 - avg) / 2.0
    return round(q)


parser = argparse.ArgumentParser()
parser.add_argument(
    "--worker",
    help="Path to Jalyro.Convert.Worker.exe. When given, the REAL estimator is "
         "called via --jpeg-quality instead of the reimplementation below. "
         "Prefer this: a reimplemented test can pass while the shipped code "
         "regresses, which is close to how the original zig-zag bug survived.")
args = parser.parse_args()


def estimate_via_worker(path, worker):
    out = subprocess.run([worker, "--jpeg-quality", path],
                         capture_output=True, text=True)
    if out.returncode != 0:
        return -1
    try:
        return int(out.stdout.strip())
    except ValueError:
        return -1


img = Image.new("RGB", (128, 128))
for x in range(128):
    for y in range(128):
        img.putpixel((x, y), ((x * 2) % 256, (y * 2) % 256, ((x + y)) % 256))

import os
import tempfile

worst = 0
mode = "production Worker" if args.worker else "python reimplementation"
print(f"estimator: {mode}\n")
print(f"{'quality':>8} {'estimated':>10} {'error':>6}")

for q in (40, 50, 60, 70, 75, 80, 85, 90, 95, 98):
    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=q)
    data = buf.getvalue()

    if args.worker:
        fd, tmp = tempfile.mkstemp(suffix=".jpg")
        os.write(fd, data)
        os.close(fd)
        try:
            est = estimate_via_worker(tmp, args.worker)
        finally:
            os.unlink(tmp)
    else:
        table = read_dqt_table_zero(data)
        if table is None:
            print(f"{q:>8} {'no DQT':>10}")
            continue
        est = estimate(table)
    err = abs(est - q)
    worst = max(worst, err)
    print(f"{q:>8} {est:>10} {err:>6}")

print(f"\nworst error: {worst}")
sys.exit(0 if worst <= 3 else 1)
