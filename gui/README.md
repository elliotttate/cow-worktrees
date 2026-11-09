# CoW Worktrees (GUI)

A native Windows app (WinUI 3, Windows App SDK 1.8, C# / .NET 8) for watching and managing the Git
worktrees that **cow-worktrees** makes: copy-on-write clones of a built base worktree on a ReFS Dev Drive.
It shows every worktree of a pool's repository, which ones are dirty or hold commits that exist nowhere
else, which ones `-Prune` would clear, and whether the base new worktrees are cloned from is current; and it
creates, removes and prunes worktrees and refreshes the base through `cow_worktree.ps1`.

![Worktrees tab](docs/worktrees.png)

## Pools

A *pool* is one repository's copy-on-write setup: a folder on a ReFS volume holding the base worktree
(`<root>\base`), the worktrees cloned from it, and `cow-config.json`. `setup.ps1` creates pools and lists
them in `%LOCALAPPDATA%\cow-worktrees\pools.json` (the folder can be moved with `COW_WORKTREES_HOME`):

```json
{ "version": 1, "toolDir": "C:\\tools\\cow-worktrees", "pools": [ { "name": "myrepo", "root": "E:\\pools\\myrepo" } ] }
```

The app reads that file and each pool's `<root>\cow-config.json` (repository, `baseRef`, `branchPrefix`,
`fallbackMinFreeGB`, `pruneDays`, optional `build`). It never writes either. With several pools there is a
pool picker; with none, or a pool whose config is missing or broken, it says what to do (run `setup.ps1`).
Both files are read again at every refresh, so a pool you add shows up without restarting.

## What it shows

**Worktrees tab**: every entry of `git worktree list --porcelain` for the pool's repository (the main
checkout, other checkouts, Codex and Claude Code worktrees, the pool's worktrees and its base), one row each:

| Column | Source |
|---|---|
| Worktree | folder name; kind (main checkout / CoW / CoW · agent for `branchPrefix` branches / CoW base / Codex / Claude / checkout); lock icon; `prunable` when `-Prune` would remove it |
| Branch · upstream | branch or `detached HEAD`; `↑ahead ↓behind upstream`, `no upstream`, `upstream gone` |
| HEAD | short commit and subject, author and age |
| Working tree | changed and untracked counts (`git status --porcelain`), or `clean` |
| Unpushed | commits of HEAD on no remote (`git rev-list --count HEAD --not --remotes`); `no branch!` when HEAD is detached, since removing it would leave those commits only in the reflog |
| Last activity | latest of the folder, the worktree's index, HEAD and HEAD reflog, and every changed or untracked file |

Dirty rows are tinted with an orange bar; red = broken or commits at risk, blue = unpushed commits.

- **Columns**: drag a header edge to resize (the two columns beside it trade width, each keeps a minimum,
  the list always fills its width); double-click an edge to reset that pair; right-click the header to reset
  all. Click a header to sort by it, again to reverse; or use the Sort box ("needs attention" puts broken,
  at-risk, dirty and unpushed first).
- **Filtering**: the count chips (`3 dirty`, `1 unpushed`, `2 prunable`, `5 worktrees`) filter the list when
  clicked; the Filter box has the same choices plus copy-on-write / other checkouts; Ctrl+F searches by name,
  branch or path.
- **Details pane** (Ctrl+D hides it; drag its edge to resize): full commit, upstream, the unpushed commits,
  the pool data from `cow_worktree.ps1 -List -Json` (commits since the worktree was made, start commit, why
  `-Prune` keeps it), and its `git status` lines.

**Base tab**: the base's commit against the pool's `baseRef` (behind / diverged, as of the last fetch);
whether the base is clean and locked; `base-build.json` against the base's commit (current, stale, missing,
or building now while `Global\cow-worktrees.build.<id>` is held); the lock files in `<root>\.locks` (a live
`refresh` holder means a refresh is moving the base); the scheduled task `\CowWorktrees\Refresh-<name>`;
free space on the pool's drive (warned below `fallbackMinFreeGB`, and when it is not ReFS); and the tails of
`hook.log`, `refresh.log` and `base-build.log`.

![Base tab](docs/base.png)

**Activity tab**: every script run with its command line and output streamed live. A banner reports how
each run ended; after a create or remove you were watching, the app returns to the list.

The window's position, size and monitor, the column widths, sort, filter and details pane are remembered.

## What it does

Everything goes through `<toolDir>\scripts\cow_worktree.ps1`, run as
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File cow_worktree.ps1 ... -Root <pool root>` from the
pool's repository:

| Action | Command | Confirmation |
|---|---|---|
| New worktree | `-Name <n> [-Branch <b>] [-From <ref>]` | dialog with branch/ref suggestions; warns when the branch is checked out elsewhere |
| Remove | `-Remove -Name <n>` (plain `git worktree remove`, never `--force`) | dialog listing changes, lock, unpushed / detached commits, recent activity |
| Prune | `-Prune -OlderThanDays N -DryRun` first, then `-Prune -OlderThanDays N` (N defaults to `pruneDays`) | the dry run's output; Prune is enabled only for the previewed day count |
| Refresh base | `-RefreshBase -Fetch -Build` (streamed; the build can take a while) | dialog; refuses to start a second one |
| Open | Explorer, a terminal (Windows Terminal, else PowerShell), or an editor (VS Code, else Cursor) | none |

Remove is offered only for worktrees inside the pool root (not the base, not other checkouts).

| Key | Does |
|---|---|
| F5 | refresh now |
| Ctrl+N | new worktree |
| Ctrl+F / Esc | search the list / clear the search |
| Ctrl+D | show or hide the details pane |
| Ctrl+1 / 2 / 3 | Worktrees / Base / Activity |
| Enter, double-click | open the selected worktree in Explorer |
| Del | remove the selected copy-on-write worktree (asks first) |
| Ctrl+C | copy the selected worktree's path |

## Staying out of the way

- Every git call runs with `--no-optional-locks` (and `GIT_OPTIONAL_LOCKS=0`), so polling never takes
  `index.lock` in a worktree an agent is committing in and never rewrites its index. The same variable is set
  for `cow_worktree.ps1 -List -Json`, so polling does not reset the idle time `-Prune` goes by.
- At most `MaxParallelGit` (default 4) git processes at once, all off the UI thread.
- The list refreshes every 45 s by default (30 s to 5 min, or off) and on F5; rows update in place.

## Requirements

- Windows 10 1809 or later, or Windows 11, x64.
- Git for Windows on `PATH`; Windows PowerShell 5.1 (part of Windows) for the scripts.
- The release build is fully self-contained: the .NET 8 runtime and the Windows App SDK runtime are next to
  `CowWorktrees.exe`, so nothing else needs to be installed. Unzip and run.

## Building

Needs the .NET 8 SDK or newer. No Visual Studio workload is required (the Windows App SDK comes from NuGet);
Visual Studio 2022 opens `CowWorktrees.sln` (pick `x64`).

```powershell
dotnet build CowWorktrees\CowWorktrees.csproj -c Release -p:Platform=x64   # run from bin\x64\Release\...\win-x64
.\build.ps1                                                                 # clean self-contained build in .\publish
```

`build.ps1` deletes `publish\`, runs `dotnet publish` (Release, win-x64, self-contained, no trimming: WinUI
and the Task Scheduler COM calls rely on reflection), checks the output and prints the exe's path. Zip the
whole `publish` folder for a release.

`dotnet publish` of an unpackaged WinUI app normally leaves out the app's own `.pri` and `.xbf` files, and the
published exe then crashes in `InitializeComponent`; the `PublishAppXamlResources` target in the project file
adds them, and `build.ps1` checks they are there. The icon in `CowWorktrees\Assets` is drawn by
`tools\make_icon.ps1`.

## Settings

`gui.json` next to `pools.json` (`%LOCALAPPDATA%\cow-worktrees\gui.json`), written by the app:

| Key | Default |
|---|---|
| `SelectedPool` | the pool shown last |
| `RefreshIntervalSeconds` | `45` (0 = off; also set from the toolbar) |
| `MaxParallelGit` | `4` |
| `EditorPath` | empty = VS Code if installed, else Cursor |
| `ColumnWeights`, `DetailsWidth`, `DetailsVisible`, `Sort`, `SortReversed`, `Filter`, `WindowBounds`, `WindowMaximized` | view state |

Unexpected errors, and one line per script run, go to `gui.log` in the same folder.

## Command line

| Option | Effect |
|---|---|
| `--pool <name or root>` | show that pool first |
| `--hidden` | window off-screen, out of the taskbar, never activated (for unattended checks) |
| `--snapshot <png>` | after the first full refresh, render the window to a PNG |
| `--dump <json>` | after the first full refresh, write what every row and health item shows |
| `--tab worktrees\|base\|activity`, `--select <name>` | what the snapshot shows |
| `--size 1100x700` | window size in layout pixels |
| `--drag 2:-140` | before the snapshot, drag the header edge after column 2 by -140 pixels (same code path as the mouse) |
| `--sort Activity[:2]` | before the snapshot, click that column header (twice reverses) |
| `--exit` | quit afterwards |

Runs with `--snapshot` or `--dump` never write `gui.json`.

## Tests

- `tests\make_test_pool.ps1 -Dir <new folder>`: a throwaway repository, a pool with a locked base, a dirty
  worktree and an agent-branch worktree with an unpushed commit, a second pool with no config, and a
  `pools.json` for them. Run the app with `COW_WORKTREES_HOME=<folder>\home` to try it.
- `tests\compare_status.ps1 [-Pool <p>] [-CowHome <folder>]`: the app's changed / untracked / unpushed counts
  against plain `git status --porcelain` and `git rev-list`, for every worktree of the pool.
- `tests\ui_create_remove.ps1 -Root <pool root> [-CowHome <folder>]`: drives the real dialogs through UI
  Automation against a hidden instance: creates `gui-selftest-<time>`, checks git lists it and the app shows it
  clean, adds an untracked file and checks Remove is refused, deletes the file and removes it. Needs the pool's
  scripts; touches only the worktree it makes.
- `tests\capture_window.ps1 -ProcessId <pid> -Out <png>`: saves a window with `PrintWindow` without
  activating it.
