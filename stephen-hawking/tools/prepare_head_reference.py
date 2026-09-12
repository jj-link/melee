"""Run with john-pork/.venv/Scripts/python.exe before image-generation/reconstruct_head.py.

The selected studio portrait is converted to an alpha-matted head for Pixal3D.
The generated head GLB alone is sufficient for rebuilding the visual prototype.
"""
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
selection = json.loads((ROOT / 'art/head-selection.json').read_text())
with Image.open(ROOT / 'art' / selection['source_image']) as reference:
    face = reference.convert('RGB')
pixels = np.asarray(face, dtype=np.int16)
background = ((pixels.min(axis=2) >= selection['background_min_channel'])
              & (pixels.max(axis=2) - pixels.min(axis=2) <= selection['background_max_spread']))
# Flood from the exterior so pale teeth and eye whites remain part of the head.
regions = Image.fromarray(np.where(background, 0, 255).astype(np.uint8))
ImageDraw.floodfill(regions, (0, 0), 128)
matte = Image.fromarray(np.where(np.asarray(regions) == 128, 0, 255).astype(np.uint8))
matte = matte.filter(ImageFilter.GaussianBlur(0.4))
isolated = face.convert('RGBA')
isolated.putalpha(matte)
isolated.save(ROOT / 'art/head-source.png')
print('Prepared isolated reference head:', ROOT / 'art/head-source.png')
