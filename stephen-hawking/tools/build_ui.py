"""Build standalone Hawking identity textures from clean Melee USA v1.02 assets."""
import argparse
import importlib.util
import json
from pathlib import Path
import sys

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'interface'
TOOLS = ROOT.parent / 'john-pork/tools'
# Load a private copy so its bitmap reader can use our isolated inspection directory.
# Its importer command, Melee glyph layout, stock outline and roster geometry are reused.
sys.path.insert(0, str(TOOLS))
SPEC = importlib.util.spec_from_file_location('hawking_melee_ui', TOOLS / 'build_ui.py')
MELEE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MELEE)
MELEE.INSPECT = OUT / 'inspection'

# Every glyph comes from a separated letter in the actual GmRst.usd name textures.
# These frame/index pairs work for both the outlined winner and solid player-card font.
GLYPHS = {
    'H': (9, 4),   # MART[H]
    'A': (9, 1),   # M[A]RTH
    'W': (5, 2),   # BO[W]SER
    'K': (4, 0),   # [K]IRBY
    'I': (4, 1),   # K[I]RBY
    'N': (11, 0),  # [N]ESS
    'G': (7, 3),   # LUI[G]I
}
ROSTER_PATH = 'MnSelectChrDataTable/versus/animation/joint17/material1/texture0/frame0'


def head_art():
    # Rendered separately, not cut from the tiny full-chair CSP: hair and glasses survive.
    with Image.open(ROOT / 'character/hawking-head.png') as image:
        head = image.convert('RGBA')
    bounds = head.getchannel('A').getbbox()
    if bounds is None:
        raise ValueError('Hawking head render is completely transparent')
    return head.crop(bounds)


def build(source):
    head = head_art()
    OUT.mkdir(parents=True, exist_ok=True)
    archives = ('MnSlChr.usd', 'GmRst.usd')
    # Extraction reads the source disc and writes only private inputs. No import-ui,
    # patched archives, executable name slots, or replacements for vanilla fighters.
    MELEE.extract(source, ROOT / 'original', archives)
    manifests = {}
    for archive in archives:
        MELEE.command('export-ui', ROOT / 'original' / archive, MELEE.INSPECT / archive)
        manifests[archive] = json.loads((MELEE.INSPECT / archive / 'textures.json').read_text())

    winner = MELEE.lettering(manifests['GmRst.usd'], 10, (256, 28), gap=2, space=12,
                             name='HAWKING', sources=GLYPHS)
    card = MELEE.lettering(manifests['GmRst.usd'], 33, (120, 24), gap=2, space=6,
                           name='HAWKING', sources=GLYPHS)
    # Keep the native intensity masks in RGB with opaque alpha, as in the existing
    # importer pipeline; Melee's winner/card materials interpret their intensity.
    winner.convert('RGBA').save(OUT / 'hawking-winner.png')
    card.convert('RGBA').save(OUT / 'hawking-result-name.png')
    MELEE.stock_icon(head).save(OUT / 'hawking-stock.png')
    entry = MELEE.resource(manifests['MnSlChr.usd'], ROSTER_PATH)
    original = MELEE.bitmap('MnSlChr.usd', entry)
    if original.size != (64, 56):
        raise ValueError(f'Expected a native 64x56 roster tile, found {original.size}')
    MELEE.roster_tile(original, head, card).save(OUT / 'hawking-roster.png')
    print('Standalone Hawking identity images ready under', OUT)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='Clean USA v1.02 Melee ISO; opened read-only')
    args = parser.parse_args()
    build(args.source)
