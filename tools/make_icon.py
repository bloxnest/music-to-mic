"""Draws the Music to Mic icons: a violet tile with a microphone and a music note (BloxNest family style:
tilted, with a grey frame). The glyphs come from Segoe Fluent Icons (built into Windows 11), so every
size is smooth, never pixel art.

  assets/icon.ico          the exe and taskbar icon (16-256 px; 24/32 px straight so the edges stay sharp)
  assets/tray-*.ico        the hidden-icons icon: on (violet), off (grey), warning (amber)
  assets/logo-256.png      README image

Run: python tools/make_icon.py   (needs Pillow)
"""
import math
import struct
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ICONS = "C:/Windows/Fonts/SegoeIcons.ttf"
MIC, NOTE = "\uF8B1", "\uE8D6"   # solid microphone, double music note
GRAY, VIOLET, INK = (0x9A, 0x9A, 0xAA), (0xA7, 0x8B, 0xFA), (0x1B, 0x14, 0x30)
TRAY = {"on": VIOLET, "off": (0x8C, 0x8C, 0x99), "warn": (0xF2, 0xC2, 0x6B)}

FRAME = [(28, 38), (182, 38), (182, 66), (56, 66), (56, 204), (28, 204)]
TILE = (76, 90, 230, 214)


def rotate(points, degrees, cx=128, cy=128):
    a = math.radians(degrees)
    return [(cx + (x - cx) * math.cos(a) - (y - cy) * math.sin(a),
             cy + (x - cx) * math.sin(a) + (y - cy) * math.cos(a)) for x, y in points]


def glyph(d, char, box, fill=INK):
    """Draws an icon-font glyph as big as fits in box, centred on its ink."""
    x0, y0, x1, y1 = box
    best = None
    for px in range(4, 600):
        f = ImageFont.truetype(ICONS, px)
        l, t, r, b = d.textbbox((0, 0), char, font=f)
        if r - l > x1 - x0 or b - t > y1 - y0:
            break
        best = (f, l, t, r, b)
    f, l, t, r, b = best
    d.text((x0 + ((x1 - x0) - (r - l)) / 2 - l, y0 + ((y1 - y0) - (b - t)) / 2 - t), char, font=f, fill=fill + (255,))


def tile_art(w, h, scale, small=False):
    """The tile's contents: a microphone, with a music note beside it (left out when tiny)."""
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if small:
        glyph(d, MIC, (w * 0.18, h * 0.14, w * 0.82, h * 0.86))
    else:
        glyph(d, MIC, (w * 0.12, h * 0.16, w * 0.58, h * 0.84))
        glyph(d, NOTE, (w * 0.58, h * 0.22, w * 0.90, h * 0.68))
    return img


def logo(size, background=None, pad=0.0):
    """The full logo: grey frame + tilted violet tile, 4x supersampled."""
    big = size * 4
    img = Image.new("RGBA", (big, big), background + (255,) if background else (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    scale = big * (1 - 2 * pad) / 256
    off = big * pad

    def place(points):
        return [(off + x * scale, off + y * scale) for x, y in rotate(points, -12)]

    x0, y0, x1, y1 = TILE
    d.polygon(place(FRAME), fill=GRAY + (255,))
    # the tile and its art are drawn straight, then turned with the frame
    tw, th = int((x1 - x0) * scale), int((y1 - y0) * scale)
    tile = Image.new("RGBA", (tw, th), VIOLET + (255,))
    art = tile_art(tw, th, scale)
    tile.alpha_composite(art)
    turned = tile.rotate(12, resample=Image.BICUBIC, expand=True)
    cx, cy = place([((x0 + x1) / 2, (y0 + y1) / 2)])[0]
    img.alpha_composite(turned, (int(cx - turned.width / 2), int(cy - turned.height / 2)))
    return img.resize((size, size), Image.LANCZOS)


def straight(size, fill=VIOLET, frame=True):
    """Small sizes (16-32 px): straight, edges on whole pixels. With the frame from 24 px up."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if frame and size >= 24:
        t = 2 if size < 32 else 3
        d.rectangle([0, 2, int(size * 0.62), 2 + t - 1], fill=GRAY + (255,))
        d.rectangle([0, 2, t - 1, int(size * 0.78)], fill=GRAY + (255,))
        box = (t + 2, t + 5, size - 1, size - 2)
        d.rectangle(box, fill=fill + (255,))
        w, h = box[2] - box[0] + 1, box[3] - box[1] + 1
        art = Image.new("RGBA", (w * 4, h * 4), (0, 0, 0, 0))
        glyph(ImageDraw.Draw(art), MIC, (w * 4 * 0.18, h * 4 * 0.12, w * 4 * 0.82, h * 4 * 0.88))
        img.alpha_composite(art.resize((w, h), Image.LANCZOS), (box[0], box[1]))
    else:
        d.rounded_rectangle([0, 0, size - 1, size - 1], radius=max(2, size // 5), fill=fill + (255,))
        art = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
        glyph(ImageDraw.Draw(art), MIC, (size * 4 * 0.2, size * 4 * 0.14, size * 4 * 0.8, size * 4 * 0.86))
        img.alpha_composite(art.resize((size, size), Image.LANCZOS))
    return img


def save_ico(path, frames):
    """Classic 32-bit BMP pictures up to 64 px (every API reads them), PNG for 128/256 px to stay small."""
    frames = sorted((f.convert("RGBA") for f in frames), key=lambda f: f.width)
    pictures = []
    for f in frames:
        w, h = f.size
        if w >= 128:
            buf = BytesIO()
            f.save(buf, "PNG")
            pictures.append(buf.getvalue())
        else:
            header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
            pictures.append(header + f.tobytes("raw", "BGRA", 0, -1) + bytes(((w + 31) // 32) * 4 * h))
    data = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    for f, picture in zip(frames, pictures):
        w, h = f.size
        data += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(picture), offset)
        offset += len(picture)
    Path(path).write_bytes(data + b"".join(pictures))


def main():
    assets = ROOT / "assets"
    assets.mkdir(exist_ok=True)
    frames = [straight(s) for s in (16, 20, 24, 32)] + [logo(s) for s in (40, 48, 64, 128, 256)]
    save_ico(assets / "icon.ico", frames)
    for state, fill in TRAY.items():
        save_ico(assets / f"tray-{state}.ico", [straight(s, fill, frame=False) for s in (16, 20, 24, 32)])
    logo(256).save(assets / "logo-256.png")
    print("icons written to", assets)


if __name__ == "__main__":
    main()
