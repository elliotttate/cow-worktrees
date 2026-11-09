# Codex

The Codex app has no hook that can replace how it creates worktrees. Its own managed worktrees are
detached full checkouts under `$CODEX_HOME\worktrees`, and you can change that folder in
Settings > Worktrees. Its "local environment" setup scripts run only after the checkout is made.

So for Codex, cow-worktrees works through instructions. `setup.ps1` writes this section into
`~\.codex\AGENTS.md`, which every Codex session reads:

```markdown
<!-- cow-worktrees:MyGame begin -->
# Worktrees for MyGame (cow-worktrees)
Applies when working in the repository at `D:\src\MyGame` and every worktree of it.
Do not make new worktrees of this repository with `git worktree add` ... make them as copy-on-write
clones of the shared base instead:
    powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Tools\cow-worktrees\scripts\cow_worktree.ps1" -Name <folder-name> -Branch <branch> -From <ref>
...
<!-- cow-worktrees:MyGame end -->
```

The full text is in [templates/codex-AGENTS.md](../templates/codex-AGENTS.md). Running setup again
replaces the section, and `uninstall.ps1` removes it. Anything else in the file is left alone.

Notes:

- Codex agents often make worktrees themselves with `git worktree add --no-checkout` plus
  `git sparse-checkout`, keeping only a handful of files. Those are already cheap.
  `migrate_worktree.ps1` refuses sparse checkouts, because moving one gains nothing.
- Codex's command rules (`~\.codex\rules\*.rules`) match a command's first words. On Windows, Codex runs
  each command as one `powershell.exe -Command "..."` string, so a rule cannot forbid `git worktree add`
  inside it. That leaves the instruction as the only lever.
- If you use the Codex app's managed worktrees for this repository, pointing their folder at the Dev
  Drive at least keeps those full checkouts off your main disk.
