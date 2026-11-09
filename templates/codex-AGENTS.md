# Worktrees for {name} (cow-worktrees)

Applies when working in the repository at `{repo}` and every worktree of it.

Do not make new worktrees of this repository with `git worktree add`: that copies the whole
repository. Make them as copy-on-write clones of the shared base instead:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "{script}" -Name <folder-name> -Branch <branch> -From <ref>
```

- The worktree lands at `{root}\<folder-name>`, and its path is the last line of output. It takes
  seconds and almost no disk space until files change. When the base has been built, the clone carries
  its build output, so a build at or near the base's commit compiles little or nothing.
- `-Branch` creates the branch at `-From`, or checks out an existing branch as it is. Leave it out for a
  detached HEAD. `-From` is any ref or commit, resolved in the checkout you run the script from, so
  `-From HEAD` starts where you are. Without `-From`, the worktree starts at the base's commit.
- If the project generates build files (project generators, codegen), run that step in the new
  worktree before building, as in any fresh checkout.
- `-List` shows the worktrees; `-Remove -Name <folder-name>` removes one (refuses one with changes);
  `-Prune -DryRun` shows which idle ones are safe to drop.
- Never edit, build or run anything in `{root}\base`. Every worktree is cloned from it, so it must stay
  clean. A debugger may open unchanged source files from the base (debug info names it); edit them in
  your own worktree instead.
- The Codex app's own managed worktrees are full checkouts. For this repository, prefer the script.
