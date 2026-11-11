# Changelog

## Unreleased

### Added

- **Disk space** on the app's Base tab: what really takes the space on the pool's drive. A worktree
  shares the files it has not changed with the base, so the folder sizes Explorer shows add up to far
  more than the drive holds. **Measure** shows, for the base, every other checkout of the repository on
  that drive and every other folder in the pool root: the folder's size as Explorer shows it, its own
  space (clusters none of the other folders uses, which is what removing it gives back) and its shared
  space, with a bar per folder; and above them the total ("506 GB of folders, held in 147 GB") and the
  drive's free space. A checkout that shares nothing with the base is called a full copy (a worktree
  made with plain `git worktree add` is one), and a folder Git does not know is called that. A
  worktree's details pane shows its own and shared space once it has been measured.
  The measurement only reads, can be stopped, and runs only when asked; the last result is kept in
  `%LOCALAPPDATA%\cow-worktrees\disk-space\` and shown again with its time. Files saved seconds ago,
  which ReFS has not yet given a place on the drive, count as the folder's own.
  Tested on throwaway pools (`gui\tests\disk_space.ps1`), and run once by hand on a real project's
  pool: 637,000 files in six folders took 11 seconds, and the result matched a count made
  independently with a script.
- App command line: `--measure [stop[:ms]]` measures before a `--dump` or `--snapshot`, and the dump has
  the result as `diskSpace`. `--tab base:hook` (or `refresh`, `build`, `disk`) picks the tab on the right
  side of the Base tab.
- `gui\tests\disk_space.ps1`: 36 checks of the view on a throwaway pool with real clones.
  `gui\tests\make_test_pool.ps1 -PayloadMB <n>` commits that many megabytes of random data, and the test
  repository now has `extensions.worktreeConfig`, so `cow_worktree.ps1` can make clones in it.

### Changed

- The right side of the Base tab opens on Disk space; the three logs are the tabs after it.

### Fixed

- The Base tab's drive line counted a gigabyte as 10^9 bytes, where Explorer and the scripts
  (`fallbackMinFreeGB`) count 2^30: a 700 GB drive showed as 752 GB, and a drive with 125 * 10^9 bytes
  free passed the app's check against a `fallbackMinFreeGB` of 120 that the hook's fallback already
  failed. The app now counts as they do.

## 1.1.0

The app becomes the way in: download one file, open it, and it walks you through. Everything can now be
done from the app; setup, removal and every worktree action are still the PowerShell scripts, and the app
shows the command line of each one.

### Added

- **Installer.** `CowWorktrees-Setup-<version>.exe` installs for the current user, without administrator
  rights and without questions, into `%LOCALAPPDATA%\Programs\cow-worktrees`, adds a Start-menu shortcut
  and starts the app. It is not code signed, so Windows SmartScreen shows "Windows protected your PC":
  choose "More info", then "Run anyway". Switches: `/NOLAUNCH`, `/NOICONS`, `/DIR=`. Its uninstaller
  first disconnects every repository (new `scripts\disconnect_all.ps1`); worktrees, bases,
  `cow-config.json` and `%LOCALAPPDATA%\cow-worktrees` stay; `/KEEPINTEGRATIONS` skips the disconnecting.
  The zip stays as the second download for PowerShell-only use; a zip copy does not update itself.
- **Get-started walkthrough** in the app: Welcome, Check this PC (Windows, Git and a Dev Drive, with a
  fix button for each missing item), Choose your project, Where worktrees live, Connect your AI tools,
  Faster first builds, Review then Set up (the steps of `setup.ps1` as a checklist, the raw output under
  "Show details"), All set. It opens by itself on the first start with nothing set up, from "Get started"
  on the empty page, and from More > Add repository.
- **Repository settings** (toolbar button "Repository"): Claude Code, Codex and the daily upkeep with
  their real state on disk, a switch each and Repair; the branch new worktrees start from, the days
  before idle worktrees are removed, the branch prefix and the daily upkeep time; Faster first builds;
  Apply, which runs `setup.ps1` again with every value; and Remove, which runs `uninstall.ps1`, with the
  option to delete the base copy too.
- A banner with **Fix it** when a connection has gone missing or points at another copy of the tool, for
  example after the folder was moved.
- **Move to copy-on-write** for full worktrees outside the pool. It runs `migrate_worktree.ps1`, can
  remove the old folder afterwards and leave a junction in its place, and picks up a move that stopped.
- **Stop** for a running create, move, base build and the base copy of a first setup. Each leaves a
  state that is cleaned up or can be picked up. Closing the window while a job runs hides it until the
  job ends, and the app is now one window per person.
- **Updates.** Once a day the app looks for a newer release and shows a banner (Update / What's new /
  Skip this version). Only an installed copy updates itself, and only after the downloaded installer's
  SHA-256 matched the digest the release publishes. Tested against a stand-in release on the same PC;
  updating from a real GitHub release has not yet been run.
- A More menu: Add repository, how often the list refreshes, Check this PC, App settings and About.
- Script failures the app knows are explained in plain words, always with the script's own message.
- `setup.ps1` records what it was asked to connect in `cow-config.json`: `integrations` (`claude`,
  `codex`, `scheduleAt`) and `integrationRevision`.
- `setup.ps1 -NoBuild` removes the build section. `-BuildPrepare ''` and `-BuildConfigs ''` clear that
  part.
- The environment variable `COW_CODEX_AGENTS_FILE` names the Codex instructions file when
  `-CodexAgentsFile` is not passed (`setup.ps1`, `uninstall.ps1` and the app).
- `release.ps1 -Version x.y.z` builds the zip and the installer from one staged folder and prints their
  SHA-256; it needs Inno Setup 6. `.github\workflows\release.yml` runs it for a pushed `v*` tag and makes
  a draft release with both files; the workflow has not yet been run. `package.ps1` gained `-OutDir` and
  `-StageOnly`, and `gui\build.ps1` gained `-Version`.
- `scripts\new_dev_drive.ps1` makes a Dev Drive inside a virtual disk file. Experimental: it has been
  written but not yet run, and the app shows its button ("Create one for me") only when
  `ExperimentalDevDrive` is `true` in `gui.json`. `tests\dev_drive_spike.ps1` is there to try it by hand.
- Tests: `gui\tests\setup_runner.ps1`, `gui\tests\ui_setup_wizard.ps1`, `gui\tests\update_selftest.ps1`
  and `installer\test_installer.ps1` (a test build of the installer; the release build is built but not
  run by any test). `tests\e2e.ps1` has 150 checks.
- App command-line options for tests and screenshots: `--wizard`, `--wizard-state`, `--settings`,
  `--theme`, `--selftest-checks`, `--run-setup`, `--fake-checks`, `--update-feed`, `--update-result`.

### Changed

- **`setup.ps1 -NoClaude`, `-NoCodex` and `-NoSchedule` now remove that piece on a later run.** In 1.0.x
  they only meant "do not add it", and a later run left hooks, a Codex section or a task that was there
  alone. Anything that calls `setup.ps1` again with one of these switches now switches that piece off.
  A run without the switch puts it back.
- **A later run of `setup.ps1` keeps what is not passed**: `-Name`, `-BaseRef`, `-BranchPrefix`,
  `-PruneDays`, `-ScheduleAt`, and each part of the build section (`-BuildCommand`, `-BuildPrepare`,
  `-BuildConfigs`). In 1.0.x a later run put the name, base ref, branch prefix, prune age and schedule
  time back to their defaults, and `-BuildCommand` replaced the whole build section.
- `setup.ps1` and `uninstall.ps1` print a step only when it does something. `uninstall.ps1` for a pool
  without a Codex section, for example, no longer prints "Codex instructions".
- `-CodexAgentsFile` of `setup.ps1` and `uninstall.ps1` defaults to `COW_CODEX_AGENTS_FILE` when that is
  set.
- The app says "repository" where it said "pool". The scripts and the docs keep "pool".
- The app runs the scripts of the cow-worktrees folder it sits in, and uses `toolDir` from `pools.json`
  only when it was started from somewhere else. In 1.0.x it always used `toolDir`.
- With nothing set up, the app offers the walkthrough instead of telling you to run `setup.ps1`. The
  refresh interval moved from the toolbar into the More menu.
- `gui\tests\compare_status.ps1` runs the published app (`gui\publish`) by default, no longer the Debug
  build.

### Fixed

- `cow_worktree.ps1 -List -Json` wrote `"Start": {}` for a worktree without a start commit, which made
  the app discard the whole list. It now writes `null`, and the app reads either.
- "Refresh base" in the app failed on a repository without a build, because it always passed `-Build`.
  It now asks for a build only when the repository has one.
- The app counted files that only look changed to Git (raw content under a clean filter, see 1.0.1) as
  changes. It now shows them as such, does not count them, and does not warn about them on Remove.
- The app flagged the base as holding commits at risk in a project without a remote, where every commit
  is "on no remote". The base is detached on purpose and no longer counts.
- `setup.ps1` and `uninstall.ps1` damaged text outside ASCII in the two files they rewrite but do not
  own, and gave them a byte order mark: `.claude\settings.local.json` and the Codex `AGENTS.md` were read
  as ANSI and written back as UTF-8 with a mark. Each is now written back in the encoding it has, with or
  without a mark, and only when its content changes; the rest of the Codex file stays byte for byte.
- Setup wrote the Claude Code hooks into the base and the checkouts outside the pool only, so a worktree
  that was already in the pool kept whatever it had been cloned with. It now writes them into every
  checkout, the pool's worktrees included, so switching Claude Code back on, or running setup from a
  folder the tool was moved to, also reaches the worktrees that exist.
- A first setup that was cut off while it made the base (the PC went down, the window was closed) left a
  partial base behind, and every later run failed with "the base has N changed files; it must be clean".
  `setup.ps1` now recognises such a base by the lock Git itself leaves on a worktree it was still making
  ("initializing"), removes it and makes it again.

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
