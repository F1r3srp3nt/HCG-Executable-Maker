"""Derive HCG UI assets from the opaque-black logo artwork.

The artwork is light on black, so black is treated as 'no light': alpha = brightest channel,
colour = un-premultiplied. Composited over any dark surface this reproduces the original glow.
"""
import sys
import numpy as np
from PIL import Image, ImageDraw

src, out_dir = sys.argv[1], sys.argv[2]
rgb = np.asarray(Image.open(src).convert("RGB"), dtype=np.float64) / 255.0
m = rgb.max(axis=2)
floor = 0.035                                     # suppress near-black background noise
alpha = np.clip((m - floor) / (1 - floor), 0, 1)
color = np.where(m[..., None] > 1e-3, rgb / np.maximum(m[..., None], 1e-3), 0)
rgba = np.dstack([color, alpha])
img = Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), "RGBA")

# Crop to the letterforms plus a glow margin; the light streak spans the full width, so the
# bounding box is taken from bright pixels and the cut edges are feathered to fade the streak out.
bright = alpha > 0.45
cols = np.where(bright.sum(axis=0) > 25)[0]   # thin flares/streaks don't count as letterform
rows = np.where(bright.sum(axis=1) > 25)[0]
pad = 56
box = (max(cols.min() - pad, 0), max(rows.min() - pad, 0), min(cols.max() + pad + 1, img.width), min(rows.max() + pad + 1, img.height))
img = img.crop(box)
a = np.asarray(img, dtype=np.float64)
h, w = a.shape[:2]
fx = np.clip(np.minimum(np.arange(w), np.arange(w)[::-1]) / pad, 0, 1)
fy = np.clip(np.minimum(np.arange(h), np.arange(h)[::-1]) / pad, 0, 1)
a[..., 3] *= np.outer(fy, fx) ** 1.5
img = Image.fromarray(a.round().astype(np.uint8), "RGBA")

def resize(im, w):
    h = round(im.height * w / im.width)
    return im.convert("RGBa").resize((w, h), Image.LANCZOS).convert("RGBA")

logo = resize(img, 720)
logo.save(f"{out_dir}/hcg-logo.png", optimize=True)

# Square application icon: logo on a dark rounded plate with a subtle cyan rim.
S = 512
plate = Image.new("RGBA", (S, S), (0, 0, 0, 0))
grad = Image.new("RGBA", (S, S))
g = np.zeros((S, S, 4), dtype=np.uint8)
t = np.linspace(0, 1, S)[:, None]
g[..., 0] = (8 + 8 * t).astype(np.uint8)
g[..., 1] = (12 + 11 * t).astype(np.uint8)
g[..., 2] = (18 + 16 * t).astype(np.uint8)
g[..., 3] = 255
grad = Image.fromarray(g, "RGBA")
mask = Image.new("L", (S, S), 0)
r = int(S * 0.2)
ImageDraw.Draw(mask).rounded_rectangle((8, 8, S - 9, S - 9), radius=r, fill=255)
plate.paste(grad, (0, 0), mask)
ImageDraw.Draw(plate).rounded_rectangle((8, 8, S - 9, S - 9), radius=r, outline=(0, 217, 255, 110), width=3)
mark = resize(img, int(S * 0.86))
plate.alpha_composite(mark, ((S - mark.width) // 2, (S - mark.height) // 2))
plate.save(f"{out_dir}/hcg-app-icon.png", optimize=True)
print("logo", logo.size, "icon", plate.size, "crop", box)
