"""Makes the Late Game Performance site's surfaces: pit-lane concrete (light theme), asphalt (dark theme and the pit
box panels) and the slatted pit board. Procedural (numpy and Pillow, fixed seeds); no source images and no generative
model. Every texture tiles. Run from this folder: python make_textures.py"""
import numpy as np
from PIL import Image

def tile_noise(rng, n, scale):
    """Smooth noise that wraps at the edges: random values on a coarse grid, upsampled with wrap-around interpolation."""
    g = rng.normal(0, 1, (n // scale, n // scale))
    img = Image.fromarray(((g - g.min()) / (g.max() - g.min()) * 255).astype(np.uint8))
    big = np.tile(np.asarray(img, float), (3, 3))
    up = np.asarray(Image.fromarray(big.astype(np.uint8)).resize((n * 3, n * 3), Image.BICUBIC), float)
    return up[n:2 * n, n:2 * n] / 255 - .5

def aggregate(rng, n, count, rmin, rmax):
    """Scattered stones: soft discs at random places, wrapped so the tile stays seamless."""
    out = np.zeros((n, n)); y, x = np.mgrid[0:n, 0:n]
    for _ in range(count):
        cx, cy, r, v = rng.uniform(0, n), rng.uniform(0, n), rng.uniform(rmin, rmax), rng.uniform(-1, 1)
        dx = np.minimum(abs(x - cx), n - abs(x - cx)); dy = np.minimum(abs(y - cy), n - abs(y - cy))
        out += v * np.clip(1 - np.sqrt(dx * dx + dy * dy) / r, 0, 1) ** .6
    return out

def surface(name, seed, base, spread, stones, stone_amp, grain_amp, mottle_amp):
    rng = np.random.default_rng(seed); n = 512
    v = mottle_amp * (tile_noise(rng, n, 64) + .5 * tile_noise(rng, n, 16))
    v += stone_amp * aggregate(rng, n, stones, .8, 3.2)
    v += grain_amp * rng.normal(0, 1, (n, n))
    rgb = np.array(base, float)[None, None, :] + v[..., None] * np.array(spread, float)[None, None, :]
    Image.fromarray(rgb.clip(0, 255).astype(np.uint8)).save(name, quality=90, method=6)
    print("made", name, rgb.mean(axis=(0, 1)).round(1))

# light: swept pit-lane concrete, pale and fine
surface("concrete.webp", 2101, (220, 217, 210), (1, 1, .97), 1600, 6, 4.2, 11)
# dark: asphalt, near-black with lighter chips of stone
surface("asphalt.webp", 2102, (21, 23, 26), (1, 1.02, 1.06), 3200, 13, 3.6, 6)

# the pit board: black slats that the numbers slide into, 64 px a slat at 2x, with a groove and a lit top edge
rng = np.random.default_rng(2103); w, h = 256, 128
y = np.arange(h)[:, None].repeat(w, 1).astype(float); slat = y % 64
v = np.full((h, w), 24.0)
v += 10 * np.exp(-((slat - 1.5) / 1.2) ** 2)          # lit top edge of each slat
v -= 16 * np.exp(-((slat - 62.5) / 1.6) ** 2)         # groove under it
v += 2.2 * rng.normal(0, 1, (h, w)) + 3 * tile_noise(rng, 256, 32)[:h, :w]
rgb = np.dstack([v, v + 1, v + 3]).clip(0, 255).astype(np.uint8)
Image.fromarray(rgb).save("board.webp", quality=86, method=6)
print("made board.webp")
