"""Prepare the pinned official m-ex sources/resources without modifying the submodule."""
import hashlib
import subprocess
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'vendor/mexTool'
RUNTIME = ROOT / 'vendor/mexTool-release'
SOURCE_COMMIT = '30ec86530ec9cecd72ad8ea58016559a188269e2'
ARCHIVE_URL = 'https://github.com/akaneia/mexTool/releases/download/LatestCommit/mexTool.zip'
ARCHIVE_SHA256 = '61224ac216fbfabe1647c99a2dbeae5aba46898748ebeeb13bd24e0f34d8e5b6'


def prepare():
    if not (SOURCE / 'mexTool/Core/MEX.cs').is_file():
        raise FileNotFoundError('Initialize the pinned dependencies with git submodule update --init --recursive')
    revision = subprocess.check_output(['git', '-C', str(SOURCE), 'rev-parse', 'HEAD'], text=True).strip()
    if revision != SOURCE_COMMIT:
        raise ValueError(f'mexTool must be at the pinned source revision {SOURCE_COMMIT}, not {revision}')
    subprocess.run(['git', '-C', str(SOURCE), 'diff', '--exit-code', 'HEAD', '--', '.'], check=True)

    RUNTIME.mkdir(parents=True, exist_ok=True)
    archive = RUNTIME / 'mexTool.zip'
    if not archive.exists():
        with urllib.request.urlopen(ARCHIVE_URL, timeout=60) as response:
            payload = response.read()
        if hashlib.sha256(payload).hexdigest() != ARCHIVE_SHA256:
            raise ValueError('The upstream rolling release changed; refusing unpinned m-ex dependencies')
        archive.write_bytes(payload)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != ARCHIVE_SHA256:
        raise ValueError('Cached mexTool.zip does not match the pinned release checksum')
    with zipfile.ZipFile(archive) as bundle:
        bundle.extractall(RUNTIME)

    original = SOURCE / 'mexTool/Core/FileSystem/TempFileManager.cs'
    text = original.read_text(encoding='utf-8-sig')
    expression = r'Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp\\")'
    if text.count(expression) != 1:
        raise ValueError('Pinned m-ex staging-path expression is not present exactly once')
    # The original API hardcodes a temp-named directory. Compile a generated copy
    # using our explicit project working-data path; leave upstream source intact.
    text = text.replace(expression, 'global::CustomSmash.BuildPaths.Workspace')
    generated = ROOT / 'roster/obj/MEXTempFileManager.cs'
    generated.parent.mkdir(parents=True, exist_ok=True)
    generated.write_text(text, encoding='utf-8')
    print(f'Prepared mexTool {SOURCE_COMMIT} with a project-local working-data path')


if __name__ == '__main__':
    prepare()
