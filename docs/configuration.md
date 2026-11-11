# Configuration

## `<root>\cow-config.json`

Written by `setup.ps1`. Run setup again to change it (the app's Repository settings does exactly that),
or edit it by hand.

```json
{
  "version": 1,
  "name": "MyGame",
  "repo": "D:\\src\\MyGame",
  "baseRef": "origin/main",
  "branchPrefix": "worktree-",
  "fallbackMinFreeGB": 120,
  "pruneDays": 7,
  "build": {
    "prepare": ["powershell -NoProfile -File tools\\generate_projects.ps1"],
    "command": "msbuild MyGame.sln /m /p:Configuration={config} /p:Platform=x64",
    "configs": ["Debug", "Release"]
  },
  "integrations": { "claude": true, "codex": true, "scheduleAt": "05:00" },
  "integrationRevision": 1
}
```

| Key | Meaning |
|---|---|
| `name` | The pool's name: used for the scheduled task (`\CowWorktrees\Refresh-<name>`) and the Codex section. Defaults to the repository folder's name. |
| `repo` | The repository's main checkout. Any checkout works when running commands; refs such as `HEAD` mean the checkout you run in. |
| `baseRef` | What the base follows. `-RefreshBase` moves the base here, and `-Fetch` fetches it first. setup's default is the remote's default branch (`origin/HEAD`), else the current branch's upstream, else the current branch. |
| `branchPrefix` | The prefix of branches the Claude Code hook creates. Only branches with it are ever auto-deleted, and only when they never got a commit. |
| `fallbackMinFreeGB` | The hook's plain-checkout fallback refuses to run when the target drive has less free space than this. |
| `pruneDays` | How long a worktree must be idle before the daily task's prune considers it. `0` turns pruning off in the task. |
| `build` | Optional. Commands that build the base; see [warm-builds.md](warm-builds.md). |
| `integrations` | What the last setup run was asked to connect: `claude` and `codex` are `true` or `false`, and `scheduleAt` is the daily task's time as `HH:mm`, or `null` for no task. New in 1.1.0. |
| `integrationRevision` | The revision of the hook entries and the Codex section that setup wrote: `1` in this release. A later version that writes them differently raises the number, so that a pool whose setup has to be run again can be told from one that is current. Nothing acts on it yet. New in 1.1.0. |

`integrations` is a record, not a switch: editing it changes nothing on disk. To connect or disconnect
something, run setup again with or without `-NoClaude`, `-NoCodex` or `-NoSchedule`, or use the switches
in the app's Repository settings. The app starts its switches from this entry and compares it with what
it finds in the files; that is how it notices a connection that has gone missing.

A `cow-config.json` written by 1.0.x has neither entry and keeps working with every script. The app then
takes what it finds on disk as what is wanted, and the next setup run adds both entries.

A new `cow-config.json`, like a new `pools.json`, is UTF-8 with a byte order mark, as before. One that
exists is written back in the encoding it has, so a file saved by an editor without the mark stays
without it.

## Running setup again

`setup.ps1 -Repo <repo> -Root <root>` on a pool that exists updates it in place and changes only what is
passed. The defaults are for the first run.

| Parameter | First run, when left out | Later run, when left out |
|---|---|---|
| `-Name` | the repository folder's name | kept |
| `-BaseRef` | the remote's default branch, else the current branch's upstream, else the current branch | kept |
| `-BranchPrefix` | `worktree-` | kept |
| `-PruneDays` | `7` | kept |
| `-FallbackMinFreeGB` | `120` | kept |
| `-ScheduleAt` | `05:00` | the time the registered task has; if the task is gone, the time recorded in `integrations`; else `05:00` |
| `-BuildCommand` | no build section | the command stays |
| `-BuildPrepare`, `-BuildConfigs` | none | each stays as it is. Passing one replaces that part only; `''` clears it. Without a build command, now or from before, they do nothing. |
| `-CodexAgentsFile` | `COW_CODEX_AGENTS_FILE` when set, else `~\.codex\AGENTS.md` | the same |

Switches:

| Switch | First run | Later run |
|---|---|---|
| `-BuildNow` | builds the base at the end, when there is a build section | the same |
| `-NoClaude` | the hooks are not added | the hooks are removed from every checkout of the repository, the base and the pool's worktrees included |
| `-NoCodex` | the Codex section is not written | the section is removed |
| `-NoSchedule` | no daily task | the task is removed |
| `-NoBuild` | (nothing to remove) | the build section is removed: the base is no longer built, and clones start cold. Cannot be combined with `-BuildCommand`, `-BuildPrepare` or `-BuildConfigs`. |

The `-No*` switches are not remembered. A later run without `-NoCodex` writes the Codex section again,
even if the run before had removed it; the same goes for the other two. The app passes the switches on
every run, from the positions of its own.

`setup.ps1` prints a step (`==> ...`) only when it does something: a later run with `-NoSchedule` and no
task to remove prints no "Daily upkeep task" step. `uninstall.ps1` does the same. A `-ScheduleAt` that
is not a time of day is refused before anything is changed.

**Changed in 1.1.0.** In 1.0.x a later run with `-NoClaude`, `-NoCodex` or `-NoSchedule` left a piece that
was there alone; it now removes it. In 1.0.x a later run also put the name, the base ref, the branch
prefix, the prune age and the schedule time back to their defaults unless they were passed again, and
`-BuildCommand` replaced the whole build section; now each keeps its value.

## `%LOCALAPPDATA%\cow-worktrees\pools.json`

The pools on this machine, written by `setup.ps1` and `uninstall.ps1`, and read by the scripts and the
app. Set `COW_WORKTREES_HOME` to keep it somewhere else; the tests do this.

```json
{ "version": 1, "toolDir": "C:\\Tools\\cow-worktrees", "pools": [ { "name": "MyGame", "root": "E:\\cow\\MyGame" } ] }
```

`toolDir` is the folder the last setup run was started from. The app runs the scripts of the
cow-worktrees folder it sits in, and falls back to `toolDir` only when it was started from somewhere
else.

The same folder holds the app's own files:

| File | What it is |
|---|---|
| `gui.json` | the app's settings; see below |
| `gui.log` | unexpected errors of the app, and one line per script it ran |
| `updates\` | the installer the app downloaded for an update |
| `disconnect.log` | what the uninstaller's `disconnect_all.ps1 -Log` did |

## `%LOCALAPPDATA%\cow-worktrees\gui.json`

Written by the app. Most keys are set from the window; a few only by editing the file while the app is
closed.

| Key | Default | Meaning |
|---|---|---|
| `SelectedPool` | empty | the repository shown last |
| `RefreshIntervalSeconds` | `45` | how often the list refreshes; `0` = only on Refresh (More > Refresh the list) |
| `MaxParallelGit` | `4` | how many git processes the app runs at once (the file only) |
| `EditorPath` | empty | the editor for "Open in editor"; empty = VS Code if installed, else Cursor (App settings) |
| `CheckForUpdates` | `true` | look for a new version once a day (App settings). The check only reads the latest release. |
| `LastUpdateCheck` | none | when the last daily check ran |
| `SkippedVersion` | empty | the version hidden with "Skip this version"; empty it to be offered that version again |
| `LastRunVersion` | empty | the version that ran last, for the "Updated to" note |
| `ExperimentalDevDrive` | `false` | `true` shows "Create one for me" on Check this PC, which runs `scripts\new_dev_drive.ps1` as administrator. That script has not yet been run, hence the switch (the file only). |
| `WalkthroughOffered` | `false` | set once the walkthrough has opened by itself; set it back to `false` to have it open again on a PC with no repository |
| `ColumnWeights`, `DetailsWidth`, `DetailsVisible`, `Sort`, `SortReversed`, `Filter`, `WindowBounds`, `WindowMaximized` | | the window's remembered state |

## Environment variables

| Variable | Effect |
|---|---|
| `COW_WORKTREES_HOME` | The folder for `pools.json` and the app's files, instead of `%LOCALAPPDATA%\cow-worktrees`. Scripts and app. |
| `COW_ROOT` | The pool a command uses when `-Root` is not given; see below. |
| `COW_CODEX_AGENTS_FILE` | The Codex instructions file, instead of `~\.codex\AGENTS.md`, when `-CodexAgentsFile` is not given. `setup.ps1`, `uninstall.ps1` and the app. New in 1.1.0. |
| `COW_NO_UPDATE_CHECK` | `1` switches the app's update check off. |
| `COW_ALLOW` | `COW_ALLOW=1` in a command lets it past the Claude Code guard; see [claude-code.md](claude-code.md). |

## Which pool a command uses

1. `-Root <root>`;
2. the `COW_ROOT` environment variable;
3. the pool whose repository contains the current directory;
4. the only pool there is.

## Names derived from the root

- Lock mutexes: `Global\cow-worktrees.state.<id>` and `Global\cow-worktrees.build.<id>`. The `<id>` is
  the root's full path in lower case, with every character other than `a-z0-9` turned into `_`
  (`E:\cow\MyGame` becomes `e__cow_mygame`).
- Lock files: `<root>\.locks\readers\*` and `<root>\.locks\refresh`.
- Scheduled task: `\CowWorktrees\Refresh-<name>`.

## The tool folder

The folder with `setup.ps1`, `scripts\` and `app\`: where the zip was extracted, or
`%LOCALAPPDATA%\Programs\cow-worktrees` for a copy put there by the installer. Its full path is written
into the Claude Code hooks, the Codex section and the daily task, so it has to stay where it is; after a
move, run setup again for each pool (the app's "Fix it"). An installed copy has an empty file
`installed.marker` in it. That file is how the app knows it may update itself, and the zip never
contains it.
