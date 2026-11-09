# cow-worktrees

**Git worktrees in seconds, at almost no disk cost, on Windows.** Each new worktree is a copy-on-write
clone of one prepared "base" worktree on a ReFS Dev Drive. Claude Code and Codex use it automatically,
and a small app shows all your worktrees and which ones have uncommitted work.

Built for big repositories, where every agent wanting its own worktree means another full checkout.
Measured on an 87 GB game repository (119k files, 56k of them in Git LFS):

| | `git worktree add` | cow-worktrees |
|---|---|---|
| New worktree | 9-10 min | ~30 s |
| Disk per worktree | ~87 GB | only the files you change |
| First build (MSBuild, at the base's commit) | 13-15 min, everything | 5 s, nothing to compile |

## How it works

1. A **pool** belongs to one repository. It lives in a folder on a ReFS volume and holds a **base**
   worktree: one normal, full checkout, made once with plain Git and locked.
2. A new worktree is registered with `git worktree add --no-checkout`. Every file is then **block-cloned**
   from the base. On ReFS, a block clone shares the base's physical storage until either copy changes.
3. The base's **index is cloned too**, so Git trusts the cloned files without re-reading them
   (`core.checkStat=minimal` for that worktree only). Then Git switches to the branch you asked for and
   rewrites only the files that differ.
4. If the base has been **built**, the clone carries its build output. MSBuild's incremental-build state
   is rewritten to the new path, so the first build compiles nothing ("warm" clones).

Git does all the Git work, so `.gitattributes`, Git LFS, sparse rules and long paths behave exactly as in
any checkout. More detail: [docs/how-it-works.md](docs/how-it-works.md).

## Requirements

- **Windows 11 24H2 or later.** From 24H2 onward, the Windows file copy function block-clones on ReFS.
  `setup.ps1` checks this on your volume.
- **A ReFS volume**, normally a [Dev Drive](https://learn.microsoft.com/windows/dev-drive/), with room
  for one full checkout plus build output.
- **Git for Windows 2.38+.** Git LFS works if your repository uses it.
- **Windows PowerShell 5.1**, which comes with Windows; PowerShell 7 is not required.
- Optional: Claude Code and/or Codex, and an MSBuild build if you want warm clones.

## Quick start

1. **Make a Dev Drive.** Go to Settings > System > Storage > Advanced storage settings > Disks & volumes
   > Create Dev Drive. A new partition or a VHDX both work; give it room for at least one checkout of your
   repository plus its build. In the examples it is `E:`.
2. **Get the tool.** Download `cow-worktrees-<version>.zip` from
   [Releases](https://github.com/elliotttate/cow-worktrees/releases) and extract it somewhere permanent,
   for example `C:\Tools\cow-worktrees`. The hooks point at this folder, so don't move it later. Then
   unblock the downloaded files:
   ```powershell
   Get-ChildItem C:\Tools\cow-worktrees -Recurse | Unblock-File
   ```
3. **Set up a pool for your repository.** Run this in Windows PowerShell:
   ```powershell
   cd C:\Tools\cow-worktrees
   .\setup.ps1 -Repo D:\src\MyGame -Root E:\cow\MyGame
   ```
   This makes the base, a full checkout once, which takes as long as a normal clone checkout. It then
   wires up Claude Code and Codex and schedules a daily cleanup. For warm builds, add a build command;
   see [Warm builds](#warm-builds-msbuild).
4. **Make worktrees.** From any checkout of the repository:
   ```powershell
   C:\Tools\cow-worktrees\scripts\cow_worktree.ps1 -Name my-feature -Branch my-feature -From HEAD
   ```
   The worktree appears at `E:\cow\MyGame\my-feature`.
5. **Watch them.** Run `app\CowWorktrees.exe` from the extracted folder. It needs no install.

To check a machine before using it on a real repository, run `tests\e2e.ps1`. It sets up a throwaway
repository and pool, runs every feature, and removes it all again; 31 checks, under a minute.

## Using it from Claude Code

`setup.ps1` adds hooks to `.claude\settings.local.json` in every checkout of the repository and in the
base, so clones inherit them. These files are local and never committed; if the repository doesn't
already ignore them, setup excludes them locally. **Restart open Claude Code sessions** to load the hooks.

- **New worktrees become clones.** The `WorktreeCreate` hook takes over every worktree Claude Code makes:
  `claude --worktree`, the `EnterWorktree` tool, subagents with `isolation: "worktree"`, background
  sessions, workflow agents, and Claude Desktop's "worktree" sessions. Each one appears at
  `<root>\<name>` on branch `worktree-<name>`, starting at the commit of the session's checkout.
- **Removing.** The `WorktreeRemove` hook removes the worktree. It deletes the `worktree-<name>` branch
  only if it never got a commit.
- **Guard.** A `PreToolUse` hook stops agents from running `git worktree add` in the repository, and
  tells them to use the script instead. Text inside quotes doesn't count, and `COW_ALLOW=1` in a command
  lets it through.

Things to know:

- Claude Desktop doesn't tell the hook which branch you picked, so its worktree sessions start at the
  main checkout's commit.
- Claude Code never runs the remove hook for subagent worktrees, and archiving a desktop session may not
  either. The daily task prunes idle, clean ones after `pruneDays` (default 7).
- Claude Desktop gives a worktree session 5 minutes to start; a clone normally takes well under one.

More: [docs/claude-code.md](docs/claude-code.md).

## Using it from Codex

The Codex app has no hook for making worktrees. `setup.ps1` writes a marked section into
`~\.codex\AGENTS.md` that tells Codex agents to use `cow_worktree.ps1` for this repository instead of
`git worktree add`. Worktrees that the Codex app creates itself are still full checkouts; you can point
its worktree folder at the Dev Drive under Settings > Worktrees. More: [docs/codex.md](docs/codex.md).

## Commands

All commands live in `scripts\`. Each one acts on one pool: `-Root` names it, or it is found from the
repository you run it in.

| Command | What it does |
|---|---|
| `cow_worktree.ps1 -Name x [-Branch b] [-From ref]` | New worktree at `<root>\x`. `-Branch` creates `b` at `-From`, or checks out an existing `b` as it is. The path is the last line of output. |
| `cow_worktree.ps1 -List [-Json]` | Worktrees in the pool, with idle days, changed and untracked files, own commits, unpushed commits |
| `cow_worktree.ps1 -Remove -Name x` | Plain `git worktree remove`: refuses if there are changes |
| `cow_worktree.ps1 -Prune [-OlderThanDays N] [-DryRun]` | Removes idle worktrees with nothing to lose: clean, not locked, no commits that exist nowhere else |
| `cow_worktree.ps1 -RefreshBase [-Fetch] [-Build]` | Moves the base to `baseRef` (fetching first) and rebuilds it |
| `migrate_worktree.ps1 -From <old> -Name x [-RemoveOld]` | Turns an existing full worktree into a clone, carrying every untracked and ignored file; see below |
| `register_refresh_task.ps1 [-At 05:00] [-Unregister]` | The daily prune-and-refresh task (setup does this) |

## Warm builds (MSBuild)

Give setup the build command, with `{config}` where the configuration goes:

```powershell
.\setup.ps1 -Repo D:\src\MyGame -Root E:\cow\MyGame `
    -BuildPrepare 'powershell -NoProfile -File tools\generate_projects.ps1' `
    -BuildCommand 'msbuild MyGame.sln /m /p:Configuration={config} /p:Platform=x64' `
    -BuildConfigs Debug,Release -BuildNow
```

The base is built at below-normal priority, and the daily task rebuilds it whenever the base moves.
Clones then build incrementally from the base's state. If your project files are generated, run the
generator in a new worktree before building, as in any fresh checkout. Details and caveats:
[docs/warm-builds.md](docs/warm-builds.md).

## Moving existing worktrees onto the Dev Drive

```powershell
scripts\migrate_worktree.ps1 -From D:\src\MyGame-feature -Name feature             # copy + verify
scripts\migrate_worktree.ps1 -From D:\src\MyGame-feature -Name feature -RemoveOld  # verify again, retire the old one
```

`migrate_worktree.ps1` copies every untracked and ignored file (build output, local tools) plus modified
tracked files, then verifies Git status and every file's size, time and hash. Only then, with
`-RemoveOld`, does it remove the old worktree. If anything still has the folder open, it stops and
removes nothing. The old path becomes a junction to the new clone, so tools and notes that use it keep
working. Staged changes and sparse checkouts are refused.

## The app

`app\CowWorktrees.exe` lists every worktree of each pool's repository, including ones outside the pool.
It shows each worktree's branch, uncommitted changes, unpushed commits and idle time, and it highlights
dirty ones. It also shows the base's health: commit against `baseRef`, build state, locks, the daily
task and the logs. From the app you can create, remove, prune and refresh. It is self-contained and runs
on a fresh Windows 10/11 x64 PC with nothing else installed. Source: [gui/](gui/).

## Configuration

`setup.ps1` writes `<root>\cow-config.json` (repository, `baseRef`, branch prefix, build commands, prune
age) and lists the pool in `%LOCALAPPDATA%\cow-worktrees\pools.json`. Run setup again to change anything.
Reference: [docs/configuration.md](docs/configuration.md).

## Removing it

```powershell
.\uninstall.ps1 -Root E:\cow\MyGame               # hooks, Codex section, task, registry entry
.\uninstall.ps1 -Root E:\cow\MyGame -RemovePool   # ...and the base, once no other worktrees are left
```

## Safety

- The base is locked and must stay clean. Only `-RefreshBase` changes it, and every command checks it.
- Nothing ever runs `git worktree remove --force` on your work. The exceptions are the Claude Code remove
  hook, which removes only after Claude Code has decided to (it asks you when there are changes), and a
  clone that failed halfway while it was being made.
- Branches are only ever deleted when they never got a commit.
- Every hook call and guard denial is logged to `<root>\hook.log`.

Problems: [docs/troubleshooting.md](docs/troubleshooting.md).

## Credits

- The base-plus-block-clone idea comes from [worktwin](https://github.com/rhagee/worktwin) and
  [git-cow-worktree](https://github.com/josharian/git-cow-worktree). This project adds the cloned index,
  so files are never rewritten or re-hashed, plus warm builds, the Claude Code and Codex wiring, and
  Windows long-path support.
- Notes from trying [Riftri](https://github.com/assistant-ui/riftri) on the same repository:
  [docs/riftri-notes.md](docs/riftri-notes.md).

## License

MIT. See [LICENSE](LICENSE).
