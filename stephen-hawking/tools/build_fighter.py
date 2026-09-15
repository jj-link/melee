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
WORK = REPO / 'output/custom-smash'
OUTPUT = REPO / 'Melee - Custom Smash.iso'
sys.path.insert(0, str(JOHN / 'tools'))
import build_custom_smash as shared
from gamecube_disc import extract, extract_system, read_dol


def build(source, blender):
    source = source.resolve()
    if source == OUTPUT.resolve() or source.is_relative_to(WORK.resolve()):
        raise ValueError('The clean source ISO must not be the combined output ISO or inside its generated build directories.')
    if not blender.is_file():
        raise FileNotFoundError(f'Blender is required: {blender}')
    with source.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'md5').hexdigest()
    if digest != shared.CLEAN_DISC_MD5:
        raise ValueError(f'Expected clean USA v1.02 ({shared.CLEAN_DISC_MD5}), got {digest}. Never use a modded ISO as input.')
    for asset in ('art/john-pork-generated.glb', 'character/john-pork-portrait.png'):
        if not (JOHN / asset).is_file():
            raise FileNotFoundError(f'Missing checked-in John Pork asset: {JOHN / asset}')
    if not (ROOT / 'prototype/stephen-hawking.blend').is_file():
        print('Generating the missing Hawking prototype from checked-in models and textures.', flush=True)
        shared.run(blender, '--background', '--python-exit-code', '1', '--python',
                   TOOLS / 'build_prototype_blender.py', '--', '--no-preview')
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
    # Move existing emulator settings with the combined game, without replacing
    # a profile already configured at the new root-level location.
    previous_profile = ROOT / 'output/custom-smash/DolphinUser'
    profile = WORK / 'DolphinUser'
    if previous_profile.exists() and not profile.exists():
        shutil.move(str(previous_profile), str(profile))
    shared.prepare_profile(REPO)
    print(f'Built: {OUTPUT}')
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
