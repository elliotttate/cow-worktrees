# Changelog

## 1.0.1

### Fixed

- Files committed as raw content while `.gitattributes` gives them a clean filter (Git LFS "files that
  should have been pointers", or CRLF files under `text`) showed as modified straight after a clone, and
  made `cow_worktree.ps1`, `migrate_worktree.ps1` and `claude --worktree` fail with "shows N changed
  paths after the switch". Git re-hashes such a file through the filter when its cached time is "racily
  clean", gets other bytes, and reports ` M` for a file that is identical to the commit. The scripts now
  tell these "phantom" entries from real changes (the file's raw hash equals the blob in the index) at
  every "must be clean" check, in `-List` (new `Phantom` count), `-Remove` and `-Prune`, and in
  `migrate_worktree.ps1`; a new worktree, a moved base and a migrated clone are then settled, so
  `git status` is really clean.
- `migrate_worktree.ps1` can pick up where it stopped: a clone left behind by a stopped run (including one
  that failed on the false alarm above) is finished instead of refused with "already exists".
- Paths with characters outside ASCII are read correctly by the status checks and by migrate, whatever the
  console's code page.

## 1.0.0

First public release.

- `cow_worktree.ps1`: worktrees as ReFS block clones of a locked base, with the base's index cloned
  (no rewrite, no re-hash) and `\\?\` paths for long paths. Also `-List [-Json]`, `-Remove`, `-Prune`,
  and `-RefreshBase -Fetch -Build`.
- Warm builds: the base is built from `cow-config.json`, and MSBuild state is relocated into each clone.
- A reader-writer base lock, with stale holders detected.
- `migrate_worktree.ps1`: turns existing full worktrees into clones, with full verification, a rename
  check for open folders, and a junction at the old path.
- Claude Code: WorktreeCreate/WorktreeRemove hooks (CLI, subagents, background sessions and Claude
  Desktop) and a PreToolUse guard against `git worktree add`.
- Codex: a marked `AGENTS.md` section.
- `setup.ps1` / `uninstall.ps1`; a daily prune-and-refresh task.
- CoW Worktrees app (WinUI 3, self-contained).
- `tests\e2e.ps1` (31 checks) and `tests\hook_selftest.ps1`.
