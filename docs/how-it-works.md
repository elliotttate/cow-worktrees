# How it works

## The pool

A pool is one repository's setup:

```
E:\cow\MyGame\                 pool root, on a ReFS volume
  base\                        the base worktree: a full checkout, locked, kept clean
  my-feature\                  worktrees cloned from the base
  cow-config.json              repository, base ref, build commands, ...
  base-build.json              which commit (and configurations) the base's build output belongs to
  base-build-state.txt         the MSBuild state files clones relocate
  hook.log, refresh.log, base-build.log
  .locks\                      the base lock (see below)
```

The base is an ordinary linked worktree of your repository, made once by `setup.ps1` with plain
`git worktree add`, so it is exactly what Git would check out: attributes, filters, Git LFS and long paths
included. It is locked (`git worktree lock`) so Git's own pruning never removes it.

## Making a worktree

`cow_worktree.ps1 -Name x -Branch b -From ref`:

1. `git worktree add --no-checkout --detach <root>\x <base commit>` registers the worktree. Nothing is
   written to its folder yet.
2. `CowCopy.Run` copies every file and folder of the base (except `.git`) into it, using
   `CopyFileExW` over `\\?\` paths on 8 threads. On ReFS, from Windows 11 24H2, that call makes a
   **block clone**: the new file points at the same physical clusters as the base's, and diverges only
   where either one is written. `Copy-Item` would also block-clone, but under Windows PowerShell 5.1 it
   cannot create paths over 260 characters.
3. The base's **index** (`.git\worktrees\base\index`) is copied to the new worktree's admin folder, and
   `core.checkStat=minimal` is set in that worktree's `config.worktree`. Cloned files keep their size and
   modified time, but get a new creation time and file ID. With the default `checkStat`, Git would
   re-hash every file on the first `git status`; with `minimal` it trusts size and time, and the cloned
   index already says the files are clean. (`setup.ps1` turns on `extensions.worktreeConfig`, which
   per-worktree config needs.)
4. If the base has a finished build of its commit, `relocate_build.ps1` rewrites the base's path to the
   new one in MSBuild's state files (see [warm-builds.md](warm-builds.md)).
5. `git switch` moves to `-Branch` / `-From`. The index is valid, so Git writes only the files that
   differ between the base's commit and the target.
6. `cow.startCommit` is recorded in the worktree's config, so a later cleanup can tell whether the
   worktree gained commits of its own.

The worktree is now a normal Git worktree. Nothing about it is special except where its files came from.

## Why not `git checkout HEAD -- .` after the copy?

That is what [worktwin](https://github.com/rhagee/worktwin) does to anchor the copied files. With no
index entries yet, Git cannot tell that the files are already right, so it rewrites every one of them.
That throws away the sharing, and with Git LFS it runs the smudge filter on every LFS file. Cloning the
base's index avoids both.

## The base lock

Several worktrees can be cloned from the base at once. Moving the base to a new commit must not happen
while one is being cloned. A small reader-writer lock handles this:

- A named mutex (`Global\cow-worktrees.state.<pool id>`) guards the lock files in `<root>\.locks`.
- Each clone in progress writes `.locks\readers\<pid>-<guid>`.
- `-RefreshBase` writes `.locks\refresh`, waits until no clone holds the base, moves the base's commit,
  and removes the file.
- Each lock file holds the owner's process ID and start time, so a lock left by a crashed process is
  recognised as stale and ignored.

Only the commit move is exclusive. That takes seconds, so worktree creation is never held up by a whole
rebuild (Claude Desktop gives a worktree session 5 minutes to start). A second mutex
(`Global\cow-worktrees.build.<pool id>`) keeps two base builds from running at once. A clone made during
a build skips relocation and builds in full the first time, which is slower but still correct.

## Cleanup

`-Prune` removes a worktree only when all of these are true:

- it has been idle for `-OlderThanDays` (the later of the folder's and the index's modified time),
- it has no changed or untracked files (ignored build output doesn't count),
- it isn't locked,
- it has no commits of its own that exist nowhere else.

It then deletes the `<branchPrefix><name>` branch, but only if that branch never moved from the start
commit.

## Measurements

These numbers come from an 87 GB repository with 119k files (56k in Git LFS), on a Samsung 980 PRO NVMe
with the pool on a ReFS partition:

| Step | Time |
|---|---|
| Base checkout (once) | 8.7-10 min |
| Clone 121,895 files from the base | 20-35 s |
| First `git status` in the clone | 0.3 s |
| Switch to a commit 370 files away | 1-2 s |
| First Debug/Release build in a warm clone | 4-5 s (0 sources compiled) |
| Same clone without relocation | 13.4 min (1,910 sources compiled) |
