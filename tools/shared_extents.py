"""Report whether files in two trees share physical clusters (ReFS block clone).

usage: python -I shared_extents.py <tree-a> <tree-b> <n-largest>
Compares the first extent's starting LCN of the n largest files under tree-a with the same
relative path under tree-b. Equal LCNs = the clone shares storage.
"""
import ctypes
import os
import struct
import sys
from ctypes import wintypes

a, b, n = sys.argv[1], sys.argv[2], int(sys.argv[3])
k = ctypes.WinDLL("kernel32", use_last_error=True)
k.CreateFileW.restype = wintypes.HANDLE


def first_lcn(path):
    h = k.CreateFileW(path, 0x80000000, 7, None, 3, 0x80, None)
    if h in (None, wintypes.HANDLE(-1).value):
        return None
    try:
        out = ctypes.create_string_buffer(4096)
        ret = wintypes.DWORD(0)
        ok = k.DeviceIoControl(h, 0x00090073, struct.pack("<q", 0), 8, out, len(out),
                               ctypes.byref(ret), None)
        if not ok and ctypes.get_last_error() != 234:  # ERROR_MORE_DATA is fine
            return None
        count, = struct.unpack_from("<I", out, 0)
        return struct.unpack_from("<qq", out, 16)[1] if count else None
    finally:
        k.CloseHandle(h)


files = []
for root, dirs, names in os.walk(a):
    dirs[:] = [d for d in dirs if d != ".git"]
    for name in names:
        p = os.path.join(root, name)
        try:
            files.append((os.path.getsize(p), p))
        except OSError:
            pass
files.sort(reverse=True)
shared = 0
for size, p in files[:n]:
    rel = os.path.relpath(p, a)
    la, lb = first_lcn(p), first_lcn(os.path.join(b, rel))
    same = la is not None and la == lb
    shared += same
    print(f"{size/1048576:9.1f} MB  {'shared' if same else 'SEPARATE'}  {rel}")
print(f"{shared}/{min(n, len(files))} largest files share clusters; {len(files)} files, "
      f"{sum(s for s, _ in files)/1073741824:.2f} GB logical under {a}")
