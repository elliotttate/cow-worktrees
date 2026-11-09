# Notes from trying Riftri

[Riftri](https://github.com/assistant-ui/riftri) also makes copy-on-write worktrees, by building an
immutable base itself and cloning views from it. These notes come from trying version 0.6.7 on the same
87 GB repository on Windows. They may help if you want to use Riftri instead, or report issues to it.

## Attribute checks

Riftri accepts only a strict set of `.gitattributes`. Three things in a long-lived game repository
tripped it:

- **Exemptions from LFS patterns.** Old binaries committed before an LFS rule (`*.jpg filter=lfs ...`)
  are often exempted with `-filter`. Riftri refuses `filter=unset`, but accepts an unspecified filter
  (`!filter`). For Git both mean "no filter", so `"/path/old.jpg" !filter -text !diff !merge` behaves
  the same and passes. [tools/lfs_scan.py](../tools/lfs_scan.py) lists every LFS-matched path at a commit
  whose blob is not a canonical LFS pointer, and writes those lines.
- **Empty files under LFS patterns.** Git LFS keeps empty files as empty blobs, never as pointers. Riftri
  wants a pointer, so they need the same exemption.
- **System attributes.** Git for Windows' own gitattributes gives `*.doc`, `*.pdf` and similar
  `diff=astextplain`, and Riftri requires effective attributes to equal in-tree ones. Claim `-diff` for
  those extensions in-tree, or run Riftri with `GIT_ATTR_NOSYSTEM=1`.

Riftri also requires `.git\info\attributes` to be empty, and that file is shared by every worktree of a
repository.

## Windows failures in 0.6.7

- **Long paths.** The base is checked out by a private Git setup that blanks system and global config,
  so `core.longpaths` never applies. The base folder alone adds about 120 characters, so any repository
  path longer than about 140 characters fails with "Filename too long".
- **Hooks.** The `#!/bin/sh` Git LFS `post-checkout` hook is started directly, which Windows cannot do.
  The add reports failure (exit 1) after it has succeeded.

[riftri/riftri-0.6.7-windows-fixes.patch](riftri/riftri-0.6.7-windows-fixes.patch) fixes both: it adds
`-c core.longpaths=true` to the private checkout, and runs the hook through `git hook run`. Apply it to
the v0.6.7 source with `git apply`, then build with `cargo build --release -p riftri-cli`.

## Performance on a large LFS repository

With the patch it worked, but each view took about 34 minutes:

- every reuse of the base re-hashed the whole base (17 min);
- `git status` then re-hashed the new view (12 min);
- on Windows, LFS content is copied into the base, not cloned;
- sparse views are refused for repositories with LFS.

That is why cow-worktrees clones the base's index instead: a new worktree takes about 30 seconds.
