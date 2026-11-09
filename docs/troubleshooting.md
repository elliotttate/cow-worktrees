# Troubleshooting

Start with `<root>\hook.log` (every Claude Code hook call and guard denial), `<root>\refresh.log` (the
daily task) and `<root>\base-build.log` (base builds).

**"the base ... has N changed paths; it must stay clean"**
Something edited, built into or ran in `<root>\base`, perhaps a debugger opening a source file from it.
Look with `git -C <root>\base status`, then put the files back (`git -C <root>\base checkout -- <files>`)
or re-run setup after moving the base away. Build output is fine as long as Git ignores it.

**Worktrees take full space**
`setup.ps1` warns when copies on the volume are not block clones. That needs ReFS and Windows 11 24H2 or
later. Check a worktree against the base with `python tools\shared_extents.py <root>\base <root>\x 10`.

**"git config --worktree ... failed" / core.checkStat**
The repository needs `extensions.worktreeConfig`. setup sets it; `git config extensions.worktreeConfig
true` in the repository does too.

**A clone fails with "Filename too long" or a path error**
The scripts use `\\?\` paths, and Git needs `core.longpaths` for paths over 260 characters. Run
`git -C <repo> config core.longpaths true`.

**Claude Code still makes worktrees under `.claude\worktrees`**
- The session started before setup: restart it.
- The checkout you started in has no hooks: setup covers the checkouts that existed when it ran, so run
  it again after adding a checkout.
- The hook fell back to a plain checkout: `hook.log` says why.

**A Claude Desktop worktree session times out while starting**
Desktop allows 5 minutes. A clone normally takes well under one, but a busy disk (a base rebuild, a big
copy) can stretch it. Try again when the disk is quiet.

**Worktrees pile up**
Subagent worktrees are never removed by Claude Code. Run `cow_worktree.ps1 -Prune -DryRun` to see what
the daily task would remove, and `-Prune -OlderThanDays 0` to clear clean, idle ones now.

**`migrate_worktree.ps1` says something still has the folder open**
A terminal, editor, file explorer or Claude/Codex session is inside the old worktree. Close it and run
the same command again: the verified copy is reused and nothing has been removed.

**The guard blocks a command that only mentions `git worktree add`**
Quoted text is ignored, but unquoted mentions count. Put `COW_ALLOW=1` in the command.

**Scripts are blocked: "running scripts is disabled" or "not digitally signed"**
Unblock the downloaded files (`Get-ChildItem C:\Tools\cow-worktrees -Recurse | Unblock-File`) and run
them with `powershell -ExecutionPolicy Bypass -File ...`, as the hooks do.
