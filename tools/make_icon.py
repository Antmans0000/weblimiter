"""Generates Assets/weblimiter.ico (gauge logo) with no third-party dependency."""
import math
import struct
import sys
import zlib


def render(size, ss=4):
    px = bytearray()
    n = size * ss
    for y in range(size):
        px.append(0)  # PNG filter byte
        for x in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(ss):
                for sx in range(ss):
                    u = (x * ss + sx + 0.5) / n  # 0..1
                    v = (y * ss + sy + 0.5) / n
                    c = sample(u, v)
                    a = c[3]
                    acc[0] += c[0] * a
                    acc[1] += c[1] * a
                    acc[2] += c[2] * a
                    acc[3] += a
            a = acc[3] / (ss * ss)
            if acc[3] > 0:
                r, g, b = (acc[i] / acc[3] for i in range(3))
            else:
                r = g = b = 0
            px += bytes((int(r), int(g), int(b), int(a * 255)))
    return png(size, px)


def sample(u, v):
    # rounded square background
    m, rad = 0.04, 0.22
    qx = max(abs(u - 0.5) - (0.5 - m - rad), 0)
    qy = max(abs(v - 0.5) - (0.5 - m - rad), 0)
    if math.hypot(qx, qy) > rad:
        return (0, 0, 0, 0)
    t = (u + v) / 2
    bg = (37 + (6 - 37) * t, 99 + (182 - 99) * t, 235 + (212 - 235) * t)

    cx, cy = 0.5, 0.58
    dx, dy = u - cx, v - cy
    d = math.hypot(dx, dy)
    ang = math.degrees(math.atan2(-dy, dx))  # 0 = right, 90 = up
    # gauge arc from 210deg (bottom-left) to -30deg (bottom-right)
    in_arc = ang >= -30 or ang <= -150
    if in_arc and abs(d - 0.30) < 0.045:
        return (255, 255, 255, 1)
    # needle pointing up-right (45deg)
    na = math.radians(40)
    nx, ny = math.cos(na), -math.sin(na)
    proj = dx * nx + dy * ny
    perp = abs(-dx * ny + dy * nx)
    if 0 <= proj <= 0.27 and perp < 0.035 * (1 - proj / 0.32):
        return (255, 255, 255, 1)
    if d < 0.06:
        return (255, 255, 255, 1)
    return (*bg, 1)


def png(size, raw):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def main(out):
    sizes = [16, 24, 32, 48, 64, 256]
    images = [render(s, 4 if s <= 64 else 2) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(out, "wb") as f:
        f.write(header + entries + b"".join(images))


if __name__ == "__main__":
    main(sys.argv[1])
