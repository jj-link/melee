"""Extract original assets and rebuild a separate GameCube disc with replacements."""
import argparse
import json
import shutil
import struct
from pathlib import Path


def filesystem(source):
    with source.open('rb') as stream:
        header = stream.read(0x440)
        if struct.unpack_from('>I', header, 0x1C)[0] != 0xC2339F3D:
            raise ValueError('Not a GameCube disc image')
        fst_offset, fst_size = struct.unpack_from('>II', header, 0x424)
        stream.seek(fst_offset)
        fst = bytearray(stream.read(fst_size))
    count = struct.unpack_from('>I', fst, 8)[0]
    strings = fst[count * 12:]
    entries = []
    parents = [(count, '')]
    for index in range(1, count):
        while index >= parents[-1][0]:
            parents.pop()
        flags, offset, size = struct.unpack_from('>III', fst, index * 12)
        name_offset = flags & 0xFFFFFF
        name = bytes(strings[name_offset:strings.index(0, name_offset)]).decode('ascii')
        path = parents[-1][1] + name
        directory = bool(flags >> 24)
        entries.append(dict(index=index, path=path, directory=directory, offset=offset, size=size))
        if directory:
            parents.append((size, path + '/'))
    return header, fst_offset, fst, entries


def extract(source, destination, names):
    _, _, _, entries = filesystem(source)
    selected = [item for item in entries if not item['directory'] and (not names or item['path'] in names)]
    missing = set(names) - {item['path'] for item in selected}
    if missing:
        raise ValueError(f'Files not found: {sorted(missing)}')
    with source.open('rb') as stream:
        for item in selected:
            target = destination / item['path']
            target.parent.mkdir(parents=True, exist_ok=True)
            stream.seek(item['offset'])
            target.write_bytes(stream.read(item['size']))
            print(f"Extracted {item['path']}: {item['size']} bytes")

def read_dol(source):
    """Read the executable using its section table, without touching the disc."""
    with source.open('rb') as stream:
        stream.seek(0x420)
        offset = struct.unpack('>I', stream.read(4))[0]
        stream.seek(offset)
        header = stream.read(0x100)
        offsets = struct.unpack_from('>18I', header, 0)
        sizes = struct.unpack_from('>18I', header, 0x90)
        size = max(start + length for start, length in zip(offsets, sizes) if length)
        stream.seek(offset)
        data = stream.read(size)
        if len(data) != size:
            raise EOFError('Truncated original executable')
        return offset, data



def rebuild(source, replacements, target):
    if source.resolve() == target.resolve():
        raise ValueError('Refusing to overwrite the original disc')
    header, fst_offset, fst, entries = filesystem(source)
    files = sorted((item for item in entries if not item['directory']), key=lambda item: item['offset'])
    # sys/main.dol is the conventional extracted-disc path, not an FST entry.
    known = {item['path'] for item in files} | {'sys/main.dol'}
    mapping = json.loads(replacements.read_text(encoding='utf-8'))
    if set(mapping) - known:
        raise ValueError(f'Unknown replacement paths: {set(mapping) - known}')
    resolved = {key: (replacements.parent / value).resolve() for key, value in mapping.items()}
    for path in resolved.values():
        if not path.is_file():
            raise FileNotFoundError(path)
    dol_data = None
    if 'sys/main.dol' in resolved:
        dol_offset, original_dol = read_dol(source)
        dol_data = resolved['sys/main.dol'].read_bytes()
        if len(dol_data) != len(original_dol) or dol_data[:0x100] != original_dol[:0x100]:
            raise ValueError('Executable replacement must preserve the original section layout and size')
    target.parent.mkdir(parents=True, exist_ok=True)
    with source.open('rb') as original, target.open('wb') as output:
        # Preserve boot data and filesystem addresses; optionally replace the same-size DOL below.
        prefix_size = min(item['offset'] for item in files if item['size'])
        if prefix_size < fst_offset + len(fst):
            raise ValueError('Unexpected disc layout: data overlaps filesystem')
        remaining = prefix_size
        while remaining:
            block = original.read(min(1024 * 1024, remaining))
            if not block:
                raise EOFError('Truncated original disc prefix')
            output.write(block)
            remaining -= len(block)
        for item in files:
            aligned = (output.tell() + 0x7FFF) & ~0x7FFF
            output.seek(aligned)
            if item['path'] in resolved:
                replacement = resolved[item['path']]
                size = replacement.stat().st_size
                with replacement.open('rb') as data:
                    shutil.copyfileobj(data, output, 1024 * 1024)
            else:
                size = item['size']
                original.seek(item['offset'])
                remaining = size
                while remaining:
                    block = original.read(min(1024 * 1024, remaining))
                    if not block:
                        raise EOFError(item['path'])
                    output.write(block)
                    remaining -= len(block)
            struct.pack_into('>II', fst, item['index'] * 12 + 4, aligned, size)
        # DVD reads round file tails up to sectors, including the final file.
        payload_size = (output.tell() + 0x7FFF) & ~0x7FFF
        output.truncate(max(source.stat().st_size, payload_size))
        output.seek(fst_offset)
        output.write(fst)
        if dol_data is not None:
            output.seek(dol_offset)
            output.write(dol_data)
        output.seek(0x20)
        title = b'Super Smash Bros. Melee - John Pork'
        output.write(title + bytes(0x60 - len(title)))
    print(json.dumps({'output': str(target), 'size': target.stat().st_size, 'replacements': mapping}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    listing = commands.add_parser('list')
    listing.add_argument('source', type=Path)
    extracting = commands.add_parser('extract')
    extracting.add_argument('source', type=Path)
    extracting.add_argument('destination', type=Path)
    extracting.add_argument('names', nargs='*')
    rebuilding = commands.add_parser('rebuild')
    rebuilding.add_argument('source', type=Path)
    rebuilding.add_argument('replacements', type=Path)
    rebuilding.add_argument('target', type=Path)
    args = parser.parse_args()
    if args.command == 'list':
        print(json.dumps(filesystem(args.source)[3], indent=2))
    elif args.command == 'extract':
        extract(args.source, args.destination, args.names)
    else:
        rebuild(args.source, args.replacements, args.target)


if __name__ == '__main__':
    main()
