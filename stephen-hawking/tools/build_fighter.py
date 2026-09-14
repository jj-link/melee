"""Build the approved Hawking fighter alongside John Pork in a separate Custom Smash ISO."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parent
JOHN = REPO / 'john-pork'
TOOLS = ROOT / 'tools'
WORK = ROOT / 'output/custom-smash'
sys.path.insert(0, str(JOHN / 'tools'))
import build_custom_smash as shared
from gamecube_disc import extract, extract_system, read_dol


def build(source, blender):
    source = source.resolve()
    if source.is_relative_to(WORK.resolve()) or source.is_relative_to((ROOT / 'playable').resolve()):
        raise ValueError('The clean source ISO must be outside the generated Hawking build directories.')
    if not blender.is_file():
        raise FileNotFoundError(f'Blender is required: {blender}')
    with source.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'md5').hexdigest()
    if digest != shared.CLEAN_DISC_MD5:
        raise ValueError(f'Expected clean USA v1.02 ({shared.CLEAN_DISC_MD5}), got {digest}. Never use a modded ISO as input.')
    for asset in ('art/john-pork-generated.glb', 'character/john-pork-portrait.png'):
        if not (JOHN / asset).is_file():
            raise FileNotFoundError(f'Existing John Pork asset required: {JOHN / asset}. Build John Pork first.')
    if not (ROOT / 'prototype/stephen-hawking.blend').is_file():
        raise FileNotFoundError('Build and approve the Hawking prototype before game conversion.')
    print('Verified the clean source; preserving the original ISO and existing John Pork builds.', flush=True)

    shared.prepare()
    shared.build_dotnet(JOHN / 'importer/JohnPorkImporter.csproj')
    shared.build_dotnet(JOHN / 'roster/CustomSmashBuilder.csproj')
    shared.build_character_assets(source, blender)
    importer = JOHN / 'importer/bin/Release/net10.0-windows/JohnPorkImporter.dll'
    inputs = ('PlCo.dat', 'PlZdNr.dat', 'PlZd.dat', 'PlZdAJ.dat', 'PlZdDViWaitAJ.dat',
              'PlSsNr.dat', 'PlSs.dat', 'PlSsAJ.dat', 'PlKbCpSs.dat', 'PlKbNr.dat',
              'GmRstMZd.dat', 'IrAls.dat', 'GmRegEnd.dat')
    extract(source, ROOT / 'original', inputs)
    (ROOT / 'original/main.dol').write_bytes(read_dol(source)[1])
    shared.run('dotnet', importer, 'hawking-export', ROOT)
    shared.run('dotnet', importer, 'hawking-kirby-export', ROOT)
    shared.run(blender, '--background', '--python-exit-code', '1', '--python', TOOLS / 'build_kirby_hat_blender.py')
    shared.run('dotnet', importer, 'import-mesh', ROOT / 'character/kirby-hawking-donor.dat',
               ROOT / 'character/kirby-hawking-mesh.json', ROOT / 'character/kirby-hawking-hat.dat')
    shared.run(blender, '--background', '--python-exit-code', '1', '--python', TOOLS / 'rig_game_model_blender.py')
    shared.run('dotnet', importer, 'hawking-import', ROOT)
    shared.run(blender, '--background', '--python-exit-code', '1', '--python', TOOLS / 'render_interface_blender.py')
    shared.run(sys.executable, TOOLS / 'build_ui.py', source)

    # These two generated trees are rebuilt from the clean disc; the isolated
    # emulator profile and all source/approved character assets remain intact.
    for directory in (WORK / 'disc', WORK / 'working-data'):
        if directory.exists():
            shutil.rmtree(directory)
    extract_system(source, WORK / 'disc/sys')
    extract(source, WORK / 'disc/files', [])
    shared.run(JOHN / 'roster/bin/Release/net48/CustomSmashBuilder.exe', JOHN, ROOT)
    shared.prepare_profile(ROOT)
    print(f'Built: {ROOT / "playable/Melee - Custom Smash.iso"}')
    print('Roster includes Stephen Hawking, John Pork, and every original fighter.')
    print('Hawking: Samus Charge Shot, regular/Super Missiles and Bomb; Zelda melee and directional teleport.')
    print(f'Launch the combined game: {REPO / "play-custom-smash.cmd"}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='Original, unmodified USA v1.02 Melee ISO')
    parser.add_argument('--blender', type=Path,
                        default=Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'Blender Foundation/Blender 5.0/blender.exe')
    arguments = parser.parse_args()
    build(arguments.source, arguments.blender)
