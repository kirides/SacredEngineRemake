# Mega world texture

`Sacred.World.Renderer.Terminal --mega` renders the whole outdoor world (terrain, liquids, static objects, 3D statics, particles, portals; no creatures) into PNG tiles whose pixels map exactly to the game's world map.

```
Sacred.World.Renderer.Terminal.exe "<game dir>" --mega --mega-scale 8 --mega-part 0/8 --output <dir>   # run parts 0..7 in parallel
python tools/megamap/pyramid.py <dir>                                                                    # s4, s2, s1 + mega.json
python tools/megamap/preview.py <dir>/s8 <outdir> [game-map.png]                                         # overview + overlay
```

Options: `--mega-scale S` (pixels per game-map pixel, default 8 = 16384 x 22528), `--mega-tile 256`, `--mega-part i/n`, `--mega-rows a-b`, `--mega-ancaria-only`, `--skip-existing`, `--supersampling` (default 2).

## Coordinates

The combined game map is 2048 x 2816 (Underworld rows 0..767 above Ancaria, `UI_WORLDMAP*` 8 x 11 tiles of 256). Mega pixel (u, v) at scale S is map pixel (u / S, v / S):

```
map_x = 1018.7499 + (tx - ty) * 48 * 0.007446
map_y = -186.9474 + (tx + ty) * 24 * 0.014914 + 768        (tx, ty = world units / 53.66563; the +768 for the combined picture)
```

(`WorldMapProjection`, the engine's `worldToMap` 0x6CB3B0.) Tile `s<S>/<row>_<col>.png` holds pixels [col*256, +256) x [row*256, +256). `s1` has the grid of the game's own map tiles (8 x 11). Transparent where no sector exists.

Each tile is rendered in the undistorted isometric view (`IsometricProjection`, 96 x 48 per tile) and area-resampled: the map is the isometric view stretched vertically so that (tx - ty) and (tx + ty) have the same scale.
Tiles are at most 45 tiles from their centre, so the 3 x 3 sector load around the centre always covers them (and tall sprites from neighbours).

## Filling the empty parts

`python tools/megamap/fill_composite.py <dir> <map-underworld.png> <out>` (the map from `--map underworld --map-only`, 2048 x 2816) builds opaque s4/s2/s1 tiles: where no sector exists (and in the nearly black outer ring of sectors) the game's own painted map is used, matched per channel (mean/std) to the render's colours and feathered at the edge.
