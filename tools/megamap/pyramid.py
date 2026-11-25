"""Build the lower pyramid levels (s4, s2, s1) from the s8 tiles. Usage: pyramid.py <root dir containing s8>"""
import sys, os, json
from PIL import Image
root = sys.argv[1]
T = 256
src = os.path.join(root, 's8')
level = 8
while level > 1:
    nxt = level // 2
    dst = os.path.join(root, f's{nxt}')
    os.makedirs(dst, exist_ok=True)
    cur = os.path.join(root, f's{level}')
    rows, cols = {}, {}
    names = [f for f in os.listdir(cur) if f.endswith('.png')]
    groups = {}
    for f in names:
        r, c = int(f[:3]), int(f[4:7])
        groups.setdefault((r // 2, c // 2), []).append((r, c, f))
    for (r2, c2), items in groups.items():
        big = Image.new('RGBA', (2 * T, 2 * T), (0, 0, 0, 0))
        for r, c, f in items:
            big.paste(Image.open(os.path.join(cur, f)).convert('RGBA'), ((c % 2) * T, (r % 2) * T))
        big.resize((T, T), Image.BOX).save(os.path.join(dst, f'{r2:03d}_{c2:03d}.png'), optimize=True)
    print('level', nxt, len(groups), 'tiles')
    level = nxt
json.dump({
    'tile': T, 'base_scale': 8,
    'picture': {'width': 2048, 'height': 2816, 'underworld_rows': 768},
    'note': 'pixel (u,v) at scale S = game-map pixel (u/S, v/S) of the combined 2048x2816 map (Underworld above Ancaria); '
            'map = origin + ((tx-ty)*0.357408, (tx+ty)*0.357936), origin (1018.74994, -186.94739); tx,ty = world units / 53.66563',
    'levels': {str(s): {'cols': -(-2048 * s // T), 'rows': -(-2816 * s // T)} for s in (1, 2, 4, 8)},
}, open(os.path.join(root, 'mega.json'), 'w'), indent=1)
