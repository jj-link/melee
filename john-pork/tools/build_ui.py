"""Build John Pork's menu/HUD/results assets and same-layout v1.02 name patch."""
import argparse
import json
import struct
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

from gamecube_disc import extract, read_dol

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'interface'
INSPECT = OUT / 'inspection'
IMPORTER = ROOT / 'importer/bin/Release/net10.0-windows/JohnPorkImporter.dll'
ARCHIVES = ('MnSlChr.dat', 'MnSlChr.usd', 'IfAll.dat', 'IfAll.usd', 'GmRst.dat', 'GmRst.usd')


def command(*args):
    subprocess.run(['dotnet', str(IMPORTER), *map(str, args)], check=True)


def resource(entries, path):
    matches = [entry for entry in entries if path in entry['paths']]
    if len(matches) != 1:
        raise ValueError(f'Expected exactly one texture for {path}, found {len(matches)}')
    return matches[0]


def bitmap(archive, entry):
    with Image.open(INSPECT / archive / entry['image']) as image:
        return image.convert('RGBA')


def spans(image):
    occupied = np.asarray(image).max(axis=0) > 0
    boundaries = np.diff(np.concatenate(([False], occupied, [False])).astype(np.int8))
    return [(int(a), int(b)) for a, b in zip(np.where(boundaries == 1)[0], np.where(boundaries == -1)[0])]


def lettering(entries, joint, size, gap, space, *, name='JOHN PORK', sources=None):
    # Reuse Melee's actual bitmap glyphs, including its outlined winner lettering.
    sources = sources if sources is not None else {
        'J': (15, 0),   # JIGGLYPUFF
        'O': (2, 1),    # FOX
        'H': (9, 4),    # MARTH
        'N': (11, 0),   # NESS
        'P': (12, 0),   # PEACH
        'R': (4, 2),    # KIRBY
        'K': (4, 0),
    }
    glyphs = {}
    for letter, (frame, index) in sources.items():
        path = f'pnlsce/animation0.0/joint{joint}/material{1 if joint == 10 else 0}/texture0/frame{frame}'
        image = bitmap('GmRst.usd', resource(entries, path)).convert('L')
        # The long winner name has overlapping glow; its leading J occupies x=0..23.
        left, right = (0, 24) if letter == 'J' and joint == 10 else spans(image)[index]
        glyphs[letter] = image.crop((left, 0, right, image.height))
    width = sum(space if c == ' ' else glyphs[c].width for c in name) + gap * (len(name) - 1)
    line = Image.new('L', (width, size[1]), 0)
    x = 0
    for letter in name:
        if letter == ' ':
            x += space
        else:
            line.paste(glyphs[letter], (x, 0))
            x += glyphs[letter].width + gap
    if line.width > size[0] - 8:
        line = line.resize((size[0] - 8, size[1]), Image.Resampling.LANCZOS)
    result = Image.new('L', size, 0)
    result.paste(line, ((size[0] - line.width) // 2, 0))
    return result


def head_art():
    with Image.open(ROOT / 'character/john-pork-portrait.png') as portrait:
        head = portrait.convert('RGBA').crop((28, 0, 108, 76))
    pixels = np.array(head)
    # Trim the shirt/tee corners below the jaw, retaining the portrait's skin shading.
    lower = pixels[62:]
    skin = (lower[:, :, 0].astype(int) > lower[:, :, 1].astype(int) + 12) & (lower[:, :, 1] > 70)
    lower[:, :, 3][~skin] = 0
    return Image.fromarray(pixels)


def stock_icon(head):
    face = head.copy()
    face.thumbnail((21, 21), Image.Resampling.LANCZOS)
    icon = Image.new('RGBA', (24, 24))
    icon.alpha_composite(face, ((24 - face.width) // 2, (24 - face.height) // 2))
    alpha = icon.getchannel('A')
    outline = ImageChops.subtract(alpha.filter(ImageFilter.MaxFilter(3)), alpha)
    background = Image.new('RGBA', icon.size, (29, 12, 17, 0))
    background.putalpha(outline)
    return Image.alpha_composite(background, icon)


def roster_tile(original, head, name):
    tile = original.copy()
    draw = ImageDraw.Draw(tile)
    draw.rectangle((3, 2, 60, 41), fill=(0, 0, 0, 0))
    face = head.copy()
    face.thumbnail((54, 39), Image.Resampling.LANCZOS)
    tile.alpha_composite(face, ((64 - face.width) // 2, 3))
    for y in range(43, 52):
        draw.line((3, y, 60, y), fill=original.getpixel((4, y)))
    label = name.crop(name.getbbox()).resize((54, 8), Image.Resampling.LANCZOS)
    text = Image.new('RGBA', label.size, (255, 255, 255, 0))
    text.putalpha(label)
    tile.alpha_composite(text, (5, 43))
    return tile


def patch_names(source):
    _, original = read_dol(source)
    data = bytearray(original)
    offsets = struct.unpack_from('>18I', data, 0)
    addresses = struct.unpack_from('>18I', data, 0x48)
    sizes = struct.unpack_from('>18I', data, 0x90)
    patches = (
        (0x803D4E54, bytes.fromhex('826b82958289828782890000')),
        (0x803D4C88, bytes.fromhex('838b8343817c835700000000')),
    )
    for address, expected in patches:
        sections = [(offset, base) for offset, base, size in zip(offsets, addresses, sizes)
                    if size and base <= address and address + len(expected) <= base + size]
        if len(sections) != 1:
            raise ValueError(f'Name address {address:08x} is outside the expected executable layout')
        offset, base = sections[0]
        at = offset + address - base
        if data[at:at + len(expected)] != expected:
            raise ValueError(f'Unexpected character-name bytes at {address:08x}; requires original Melee v1.02')
        data[at:at + len(expected)] = b'John Pork\0'.ljust(len(expected), b'\0')
    (ROOT / 'original/main.dol').write_bytes(original)
    (OUT / 'main.dol').write_bytes(data)
    print('Patched only the two v1.02 character-name allocations; executable layout and code are unchanged.')


def build(source, *, assets_only=False):
    OUT.mkdir(parents=True, exist_ok=True)
    archives = ('MnSlChr.usd', 'GmRst.usd') if assets_only else ARCHIVES
    if not assets_only:
        patch_names(source)
    extract(source, ROOT / 'original', [name for name in archives if assets_only or not name.startswith('MnSlChr')])
    manifests = {}
    inputs = {}
    for name in archives:
        directory = 'character' if name.startswith('MnSlChr') and not assets_only else 'original'
        inputs[name] = ROOT / directory / name
        command('export-ui', inputs[name], INSPECT / name)
        manifests[name] = json.loads((INSPECT / name / 'textures.json').read_text())

    winner = lettering(manifests['GmRst.usd'], 10, (256, 28), gap=2, space=12)
    card = lettering(manifests['GmRst.usd'], 33, (120, 24), gap=2, space=6)
    # Menu photography is separate from the model-derived HUD stock icon.
    with Image.open(ROOT / 'art/john-pork-menu-head.png') as photo:
        head = photo.convert('RGBA')
    portrait = head.copy()
    portrait.thumbnail((136, 188), Image.Resampling.LANCZOS)
    canvas = Image.new('RGBA', (136, 188))
    canvas.alpha_composite(portrait, ((136 - portrait.width) // 2, (188 - portrait.height) // 2))
    canvas.save(OUT / 'john-pork-menu-portrait.png')
    winner.convert('RGBA').save(OUT / 'john-pork-winner.png')
    card.convert('RGBA').save(OUT / 'john-pork-result-name.png')
    stock_icon(head_art()).save(OUT / 'john-pork-stock.png')

    if assets_only:
        entry = resource(manifests['MnSlChr.usd'], 'MnSelectChrDataTable/versus/animation/joint17/material1/texture0/frame0')
        roster_tile(bitmap('MnSlChr.usd', entry), head, card).save(OUT / 'john-pork-roster-MnSlChr.usd.png')
        print('Standalone identity images ready; original character archives and DOL were not replaced.')
        return

    stock_ids = {
        resource(manifests['IfAll.usd'], f'Stc_scemdls/animation0.0/joint1/material0/texture0/frame{7 + costume * 30}')['id']
        for costume in range(4)
    }
    for name, entries in manifests.items():
        replacements = {entry['id']: 'john-pork-stock.png' for entry in entries if entry['id'] in stock_ids}
        if len(replacements) != 4:
            raise ValueError(f'{name}: expected all four Luigi costume stock icons')
        if name.startswith('MnSlChr'):
            entry = resource(entries, 'MnSelectChrDataTable/versus/animation/joint17/material1/texture0/frame0')
            filename = f'john-pork-roster-{name}.png'
            roster_tile(bitmap(name, entry), head, card).save(OUT / filename)
            replacements[entry['id']] = filename
        elif name.startswith('GmRst'):
            for joint, material, filename in [(10, 1, 'john-pork-winner.png'), (33, 0, 'john-pork-result-name.png')]:
                entry = resource(entries, f'pnlsce/animation0.0/joint{joint}/material{material}/texture0/frame7')
                replacements[entry['id']] = filename
        manifest = OUT / f'{name}.json'
        manifest.write_text(json.dumps(replacements, indent=2) + '\n')
        command('import-ui', inputs[name], manifest, OUT / name)
    print('Interface assets ready under', OUT)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='Original USA v1.02 Melee ISO; opened read-only')
    parser.add_argument('--assets-only', action='store_true', help='Generate images for the expanded roster without replacing Luigi')
    args = parser.parse_args()
    build(args.source, assets_only=args.assets_only)
