# Troubleshooting

Start with `<root>\hook.log` (every Claude Code hook call and guard denial), `<root>\refresh.log` (the
daily task) and `<root>\base-build.log` (base builds).

**"the base ... has N changed paths; it must stay clean"**
Something edited, built into or ran in `<root>\base`, perhaps a debugger opening a source file from it.
Look with `git -C <root>\base status`, then put the files back (`git -C <root>\base checkout -- <files>`)
or re-run setup after moving the base away. Build output is fine as long as Git ignores it.

**Files show as modified straight after a clone or migrate (`git status` lists files you never touched)**
The repository has files committed as raw content that `.gitattributes` sends through a clean filter (Git
LFS "files that should have been pointers", or CRLF files under `text`). Git re-hashes them through the
filter, gets different bytes, and reports ` M`, although each file is identical to the commit; a plain
`git worktree add` does the same when the timing matches. The scripts recognise these entries ("N files only
look changed to git status"), do not count them as changes and settle them after a clone. To cure it in the
repository, commit the files the way the attributes expect: `git add --renormalize .` (then commit), or
`git lfs migrate import --no-rewrite <files>` for LFS files.

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

**A `migrate_worktree.ps1` run stopped halfway**
Run the same command again. A clone it left in the pool is recognised by what it recorded (`cow.migrateFrom`
and `cow.migrateState` in the clone's config) and finished: the copy is redone if it had not completed,
then everything is verified again; the old worktree is only retired with `-RemoveOld`, after the checks. A
clone from a run that stopped right after the switch (no record yet, detached, nothing changed in it) is
finished the same way. Anything else under that name is refused with "already exists"; if it is a leftover
you do not need, `git -C <repo> worktree remove --force <root>\<name>` discards it.

**The guard blocks a command that only mentions `git worktree add`**
Quoted text is ignored, but unquoted mentions count. Put `COW_ALLOW=1` in the command.

**Scripts are blocked: "running scripts is disabled" or "not digitally signed"**
Unblock the downloaded files (`Get-ChildItem C:\Tools\cow-worktrees -Recurse | Unblock-File`) and run
them with `powershell -ExecutionPolicy Bypass -File ...`, as the hooks do.
