"""Read and write Godot 4.5 resource packs (format v3, relative file base)."""
import hashlib
import struct

MAGIC = b'GDPC'
HEADER_SIZE = 112  # 40-byte header + 64 reserved bytes, aligned to 16


def list_files(path):
    """Yield (res_path, absolute_offset, size) for each file in the pack."""
    with open(path, 'rb') as f:
        magic, ver, _maj, _min, _pat = struct.unpack('<4sIIII', f.read(20))
        assert magic == MAGIC and ver == 3, f'unsupported pack {magic!r} v{ver}'
        flags, file_base, dir_offset = struct.unpack('<IQQ', f.read(20))
        assert not flags & 1, 'encrypted directory not supported'
        f.seek(dir_offset)
        (count,) = struct.unpack('<I', f.read(4))
        for _ in range(count):
            (n,) = struct.unpack('<I', f.read(4))
            name = f.read(n).rstrip(b'\0').decode('utf-8')
            off, size = struct.unpack('<QQ', f.read(16))
            f.read(16 + 4)  # md5, flags
            yield name, off + file_base, size


def read_file(path, res_path):
    for name, off, size in list_files(path):
        if name == res_path:
            with open(path, 'rb') as f:
                f.seek(off)
                return f.read(size)
    raise KeyError(res_path)


def write(path, files):
    """files: iterable of (res_path, bytes). res_path has no res:// prefix."""
    files = sorted(files)
    data, entries = bytearray(), []
    for name, blob in files:
        entries.append((name, len(data), len(blob), hashlib.md5(blob).digest()))
        data += blob
        data += b'\0' * (-len(data) % 16)
    directory = bytearray(struct.pack('<I', len(entries)))
    for name, off, size, md5 in entries:
        raw = name.encode('utf-8')
        raw += b'\0' * (-len(raw) % 4)
        directory += struct.pack('<I', len(raw)) + raw + struct.pack('<QQ', off, size) + md5 + struct.pack('<I', 0)
    header = struct.pack('<4sIIIIIQQ', MAGIC, 3, 4, 5, 1, 2, HEADER_SIZE, HEADER_SIZE + len(data))
    header += b'\0' * (HEADER_SIZE - len(header))
    with open(path, 'wb') as f:
        f.write(header + data + directory)
