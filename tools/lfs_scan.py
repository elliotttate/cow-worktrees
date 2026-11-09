"""List LFS-classified paths at a commit whose blob is not a canonical Git LFS pointer.

Mirrors Riftri 0.6.7's checks: in-tree attributes only (system/global off), a path is LFS when
filter, diff or merge resolves to "lfs"; the blob must be the canonical three-line v1 pointer and
the object must exist under <git-dir>/lfs/objects.

usage: python -I lfs_scan.py <repo> <commit> <out.tsv>
"""
import os
import re
import subprocess
import sys
import tempfile

repo, commit, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
VERSION = b"version https://git-lfs.github.com/spec/v1"
OID = re.compile(rb"^[0-9a-f]{64}$")


def git(args, env=None, inp=None):
    return subprocess.run(["git", "-C", repo, *args], env=env, input=inp,
                          stdout=subprocess.PIPE, check=True).stdout


entries = {}
for rec in git(["ls-tree", "-r", "-z", "--full-tree", commit]).split(b"\0"):
    if not rec:
        continue
    meta, path = rec.split(b"\t", 1)
    mode, kind, oid = meta.split(b" ")
    entries[path] = (mode, kind, oid)

with tempfile.TemporaryDirectory() as tmp:
    env = dict(os.environ)
    env["GIT_INDEX_FILE"] = os.path.join(tmp, "index")
    git(["read-tree", commit], env=env)
    env.update(GIT_ATTR_NOSYSTEM="1", GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL="NUL")
    raw = git(["-c", "core.attributesFile=NUL", "check-attr", "--all", "-z", "--stdin"],
              env=env, inp=b"\0".join(entries) + b"\0")

attrs = {}
parts = raw.split(b"\0")
for i in range(0, len(parts) - 2, 3):
    attrs.setdefault(parts[i], {})[parts[i + 1]] = parts[i + 2]

lfs = [p for p, a in attrs.items()
       if any(a.get(k) == b"lfs" for k in (b"filter", b"diff", b"merge"))]

# Read blob sizes, then bodies of the small ones.
proc = subprocess.run(["git", "-C", repo, "cat-file", "--batch-check=%(objectname) %(objectsize)"],
                      input=b"\n".join(entries[p][2] for p in lfs) + b"\n",
                      stdout=subprocess.PIPE, check=True)
sizes = dict(line.split(b" ") for line in proc.stdout.splitlines())
small = sorted({entries[p][2] for p in lfs if int(sizes[entries[p][2]]) <= 1024})
proc = subprocess.run(["git", "-C", repo, "cat-file", "--batch"],
                      input=b"\n".join(small) + b"\n", stdout=subprocess.PIPE, check=True)
bodies, buf = {}, proc.stdout
pos = 0
while pos < len(buf):
    nl = buf.index(b"\n", pos)
    oid, _, size = buf[pos:nl].split(b" ")
    size = int(size)
    bodies[oid] = buf[nl + 1:nl + 1 + size]
    pos = nl + 1 + size + 1

lfs_dir = os.path.join(git(["rev-parse", "--git-common-dir"]).strip().decode(), "lfs", "objects")
if not os.path.isabs(lfs_dir):
    lfs_dir = os.path.join(repo, lfs_dir)

bad = []
for p in sorted(lfs):
    mode, kind, oid = entries[p]
    size = int(sizes[oid])
    if kind != b"blob" or mode not in (b"100644", b"100755"):
        bad.append((p, "not-regular-file", size))
        continue
    body = bodies.get(oid)
    if body is None:
        bad.append((p, "raw-blob", size))
        continue
    lines = body.split(b"\n")
    if size == 0:
        bad.append((p, "empty-blob", 0))
        continue
    if len(lines) != 4 or lines[0] != VERSION or lines[3] != b"" \
            or not lines[1].startswith(b"oid sha256:") or not OID.match(lines[1][11:]) \
            or not re.match(rb"^(0|[1-9][0-9]*)$", lines[2][5:] if lines[2].startswith(b"size ") else b"x"):
        bad.append((p, "noncanonical-small-blob", size))
        continue
    o = lines[1][11:].decode()
    if not os.path.isfile(os.path.join(lfs_dir, o[:2], o[2:4], o)):
        bad.append((p, "lfs-object-missing", size))

with open(out_path, "w", encoding="utf-8", newline="\n") as f:
    for p, why, size in bad:
        f.write(f"{why}\t{size}\t{p.decode('utf-8', 'surrogateescape')}\n")

# Ready-to-append .gitattributes lines (CRLF) for the raw and empty blobs.
# "!filter" leaves filter unspecified; "-filter" would be filter=unset, which Riftri refuses.
with open(out_path + ".gitattributes", "w", encoding="utf-8", newline="\r\n") as f:
    for p, why, size in bad:
        if why in ("raw-blob", "noncanonical-small-blob", "empty-blob"):
            f.write(f"\"/{p.decode('utf-8', 'surrogateescape')}\" !filter -text !diff !merge\n")

from collections import Counter
print(f"tree entries {len(entries)}, LFS-classified {len(lfs)}, not canonical {len(bad)}")
for why, n in Counter(w for _, w, _ in bad).most_common():
    print(f"  {why}: {n}")
