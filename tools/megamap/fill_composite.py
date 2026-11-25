"""Fills the empty (no sector) parts of the render with the game's own map, colour-matched to the render, and rebuilds the pyramid
levels s4/s2/s1 as opaque RGB PNG tiles in <out>/s<S>.  Usage: fill_composite.py <mega root> <combined map png 2048x2816> <out dir>"""
import os, sys
import numpy as np
from PIL import Image, ImageFilter
Image.MAX_IMAGE_PIXELS = None
root, mapfile, out = sys.argv[1:4]
T = 256
S = 4
cols, rows = 8 * S, 11 * S
W, H = cols * T, rows * T
render = np.zeros((H, W, 4), np.uint8)
for r in range(rows):
    for c in range(cols):
        f = os.path.join(root, f's{S}', f'{r:03d}_{c:03d}.png')
        if os.path.exists(f):
            render[r * T:(r + 1) * T, c * T:(c + 1) * T] = np.asarray(Image.open(f).convert('RGBA'))
orig = np.asarray(Image.open(mapfile).convert('RGB').resize((W, H), Image.BICUBIC)).astype(np.float32)
a = render[..., 3].astype(np.float32) / 255
solid = a > 0.9
# the world's outermost ring of sectors is rendered nearly black: count it as empty too (blurred luminance below a threshold)
lum = Image.fromarray((render[..., :3].astype(np.float32) @ np.array([0.3, 0.59, 0.11], np.float32)).astype(np.uint8)).filter(ImageFilter.GaussianBlur(6))
dark = np.asarray(lum) < 26
solid &= ~dark
rgb = render[..., :3].astype(np.float32)
# per-channel mean / std matching over the area both cover (the original is the paper-textured painting, the render is lit terrain)
mo, so = orig[solid].mean(0), orig[solid].std(0)
mr, sr = rgb[solid].mean(0), rgb[solid].std(0)
print('render mean', mr, 'std', sr, '| original mean', mo, 'std', so)
fit = np.clip((orig - mo) / so * sr + mr, 0, 255)
# feathered mask: erode a little so the antialiased rim of the render does not show the black background, then blur
m = Image.fromarray((solid * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(5)).filter(ImageFilter.GaussianBlur(10))
mask = np.asarray(m).astype(np.float32)[..., None] / 255
comp = np.clip(rgb * mask + fit * (1 - mask), 0, 255).astype(np.uint8)
del render, orig, fit, rgb
img = Image.fromarray(comp)
del comp
for s in (4, 2, 1):
    if s != 4:
        img = img.resize((8 * s * T, 11 * s * T), Image.BOX)
    d = os.path.join(out, f's{s}')
    os.makedirs(d, exist_ok=True)
    for r in range(11 * s):
        for c in range(8 * s):
            img.crop((c * T, r * T, (c + 1) * T, (r + 1) * T)).save(os.path.join(d, f'{r:03d}_{c:03d}.png'))
    print('level', s, 'done')
img2 = Image.open(os.path.join(out, 's1', '000_000.png'))
