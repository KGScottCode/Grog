# Builds Grog's macOS icon art from scratch at 1024 px:
#  - grog-mac-1024.png : Apple's pre-26 grid (824 px rounded square on a 1024 canvas, soft shadow) -> grog.icns
#  - Grog.icon/        : macOS 26 layered icon (full-bleed background + "G" layer), compiled by actool in CI
#  Usage: python3 make-icon.py <Inter-Bold.ttf> <out-dir>   (Inter ships in the Avalonia.Fonts.Inter package)
import json, math, os, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter
S = 1024
TOP, BOT = (229, 181, 93), (212, 157, 45)      # sampled from grog-amber.png (light top-left -> dark bottom-right)
INK = (26, 20, 8)
FONT = sys.argv[1] if len(sys.argv) > 1 else "InterBold.ttf"

def squircle_mask(size, n=5.0, ss=4):
    big = size * ss; m = Image.new("L", (big, big), 0); px = m.load(); r = big / 2
    for y in range(big):
        dy = abs((y + 0.5 - r) / r)
        if dy >= 1: continue
        dx = (1 - dy ** n) ** (1 / n); x0 = int(r - dx * r); x1 = int(r + dx * r)
        for x in range(x0, x1): px[x, y] = 255
    return m.resize((size, size), Image.LANCZOS)

def amber(size):
    g = Image.new("RGB", (size, size)); px = g.load()
    for y in range(size):
        for x in range(size):
            t = min(1.0, math.hypot(x - size * 0.27, y - size * 0.18) / (size * 1.05))
            px[x, y] = tuple(int(TOP[i] + (BOT[i] - TOP[i]) * t) for i in range(3))
    return g

def glyph(size, height_frac):
    # "G" in Inter Bold, its ink box scaled to height_frac of size and centred like the original art.
    lay = Image.new("RGBA", (size, size), (0, 0, 0, 0)); d = ImageDraw.Draw(lay)
    fs = 100
    while True:
        f = ImageFont.truetype(FONT, fs); b = d.textbbox((0, 0), "G", font=f)
        if b[3] - b[1] >= size * height_frac: break
        fs += 4
    b = d.textbbox((0, 0), "G", font=f); w, h = b[2] - b[0], b[3] - b[1]
    cx, cy = size * 0.5 + size * 0.0, size * 0.5 + size * 0.002
    d.text((cx - w / 2 - b[0], cy - h / 2 - b[1]), "G", font=f, fill=INK + (255,))
    return lay

out = sys.argv[2] if len(sys.argv) > 2 else "."
# 1) pre-26 art on Apple's grid
tile = 824; off = (S - tile) // 2
mask = squircle_mask(tile)
body = amber(tile).convert("RGBA"); body.alpha_composite(glyph(tile, 0.549)); body.putalpha(mask)
canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0)); sh = Image.new("RGBA", (tile, tile), (0, 0, 0, 90)); sh.putalpha(mask.point(lambda a: a * 90 // 255))
shadow.paste(sh, (off, off + 12), sh); shadow = shadow.filter(ImageFilter.GaussianBlur(14))
canvas.alpha_composite(shadow); canvas.alpha_composite(body, (off, off))
canvas.save(os.path.join(out, "grog-mac-1024.png"))
# 2) macOS 26 layered icon: the system draws the shape, shadow and glass; we give it full-bleed layers.
ic = os.path.join(out, "Grog.icon"); os.makedirs(os.path.join(ic, "Assets"), exist_ok=True)
amber(S).save(os.path.join(ic, "Assets", "background.png"))
glyph(S, 0.549).save(os.path.join(ic, "Assets", "G.png"))
json.dump({
    "groups": [
        {"layers": [{"image-name": "G.png", "name": "G"}],
         "shadow": {"kind": "neutral", "opacity": 0.5},
         "translucency": {"enabled": False, "value": 0.5}},
        {"layers": [{"image-name": "background.png", "name": "background"}],
         "shadow": {"kind": "none", "opacity": 0.5},
         "translucency": {"enabled": False, "value": 0.5}},
    ],
    "supported-platforms": {"squares": "shared"},
}, open(os.path.join(ic, "icon.json"), "w"), indent=2)
print("ok")
