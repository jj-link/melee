"""Build John Pork as an additional selectable fighter, preserving the older ISO."""
import argparse
import configparser
import hashlib
import os
import shutil
import subprocess
import sys
from pathlib import Path

from gamecube_disc import extract, extract_system, read_dol
from prepare_mextool import prepare

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parent
TOOLS = ROOT / 'tools'
WORK = ROOT / 'output/custom-smash'
CLEAN_DISC_MD5 = '0e63d4223b01d9aba596259dc155a174'


def run(*args):
    print('+', ' '.join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), cwd=REPO, check=True)


def build_dotnet(project):
    # Explicit source also works on workstations configured with only the VS
    # offline package feed. This does not modify user/global NuGet settings.
    run('dotnet', 'restore', project, '--source', 'https://api.nuget.org/v3/index.json')
    run('dotnet', 'build', project, '-c', 'Release', '--no-restore')


def prepare_profile(root=ROOT):
    config = root / 'output/custom-smash/DolphinUser/Config'
    config.mkdir(parents=True, exist_ok=True)
    dolphin = config / 'Dolphin.ini'
    if not dolphin.exists():
        settings = configparser.ConfigParser(interpolation=None)
        settings.optionxform = str
        settings['Core'] = {'SIDevice0': '6', 'SIDevice1': '6', 'SIDevice2': '0', 'SIDevice3': '0',
                            'EnableCheats': 'False', 'GFXBackend': 'D3D'}
        settings['Interface'] = {'ConfirmStop': 'False'}
        settings['Display'] = {'RenderToMain': 'False', 'RenderWindowWidth': '1280', 'RenderWindowHeight': '960'}
        settings['DSP'] = {'Volume': '75'}
        with dolphin.open('w', encoding='utf-8') as stream:
            settings.write(stream)
    controls = config / 'GCPadNew.ini'
    if not controls.exists():
        shutil.copyfile(ROOT / 'roster/GCPadNew.ini', controls)
    graphics = config / 'GFX.ini'
    prior_graphics = ROOT / 'DolphinUser/Config/GFX.ini'
    if not graphics.exists() and prior_graphics.exists():
        # Retain the working machine's GPU selection, not its save/NAND paths.
        shutil.copyfile(prior_graphics, graphics)


def build_character_assets(source, blender):
    importer = ROOT / 'importer/bin/Release/net10.0-windows/JohnPorkImporter.dll'
    originals = ('PlLgNr.dat', 'PlLg.dat', 'PlLgAJ.dat', 'PlDkNr.dat',
                 'PlDk.dat', 'PlDkAJ.dat', 'EfDkData.dat')
    extract(source, ROOT / 'original', originals)
    (ROOT / 'original/main.dol').write_bytes(read_dol(source)[1])
    run('dotnet', importer, 'export-rig', ROOT / 'original/PlLgNr.dat', ROOT / 'importer/rig.json')
    run(blender, '--background', '--python', TOOLS / 'rig_generated_character_blender.py')
    run(sys.executable, TOOLS / 'build_character.py')
    run('dotnet', importer, 'import-mesh', ROOT / 'original/PlLgNr.dat', ROOT / 'character/john-pork-mesh.json', ROOT / 'character/PlLgNr.dat')
    run('dotnet', importer, 'john-pork-import', ROOT)
    run(sys.executable, TOOLS / 'build_ui.py', source, '--assets-only')


def build(source, blender):
    source = source.resolve()
    if not blender.is_file():
        raise FileNotFoundError(f'Blender is required; pass --blender with its executable path: {blender}')
    with source.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'md5').hexdigest()
    if digest != CLEAN_DISC_MD5:
        raise ValueError(f'Expected a clean, full USA v1.02 disc ({CLEAN_DISC_MD5}); got {digest}. Do not use either modded ISO as input.')
    print('Verified the original USA v1.02 source disc; all source access is read-only.', flush=True)

    prepare()
    build_dotnet(ROOT / 'importer/JohnPorkImporter.csproj')
    build_dotnet(ROOT / 'roster/CustomSmashBuilder.csproj')
    build_character_assets(source, blender)

    # These are owned, generated build trees. Keep them for inspection after a
    # build, but reset them before another; never delete the separate profile.
    for directory in (WORK / 'disc', WORK / 'working-data'):
        if directory.exists():
            shutil.rmtree(directory)
    extract_system(source, WORK / 'disc/sys')
    extract(source, WORK / 'disc/files', [])
    run(ROOT / 'roster/bin/Release/net48/CustomSmashBuilder.exe', ROOT)
    prepare_profile()
    print('\nReady: john-pork/playable/Melee - Custom Smash.iso')
    print('The original source disc and Melee - John Pork.iso were not modified.')
    print('Open this John-Pork-only roster ISO directly in Dolphin.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='Original, unmodified USA v1.02 Melee ISO')
    parser.add_argument('--blender', type=Path,
                        default=Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'Blender Foundation/Blender 5.0/blender.exe')
    args = parser.parse_args()
    build(args.source, args.blender)
