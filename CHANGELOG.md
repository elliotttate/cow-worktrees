# Changelog

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
