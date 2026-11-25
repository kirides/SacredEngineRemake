"""Assemble mega tiles -> overview image (downscaled) and a map-aligned overlay check. Usage: assemble.py <s8 dir> <out dir>"""
import sys, os
from PIL import Image
Image.MAX_IMAGE_PIXELS = None
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
T = 256; S = 8
COLS, ROWS = 64, 88
div = 4  # overview scale: 1/4 of mega => 4096 x 5632
ov = Image.new('RGB', (COLS * T // div, ROWS * T // div), (0, 0, 0))
for f in os.listdir(src):
    if not f.endswith('.png'): continue
    r, c = int(f[:3]), int(f[4:7])
    im = Image.open(os.path.join(src, f)).convert('RGB').resize((T // div, T // div), Image.BOX)
    ov.paste(im, (c * T // div, r * T // div))
ov.save(os.path.join(out, 'overview.png'))
# overlay with the game's map (Ancaria picture occupies rows 768..2815 of the combined picture)
m = Image.open(sys.argv[3]).convert('RGB') if len(sys.argv) > 3 else None
if m:
    uw = 768 * S // div
    anc = ov.crop((0, uw, ov.width, ov.height)).resize(m.size, Image.BOX)
    Image.blend(anc, m, 0.5).save(os.path.join(out, 'overlay.png'))
    anc.save(os.path.join(out, 'ancaria_aligned.png'))
