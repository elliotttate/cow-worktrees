# Troubleshooting

Start with `<root>\hook.log` (every Claude Code hook call and guard denial), `<root>\refresh.log` (the
daily task) and `<root>\base-build.log` (base builds). The app shows the ends of all three on its Base
tab. For the app itself: `%LOCALAPPDATA%\cow-worktrees\gui.log` (More > App settings and About > Open the
log folder), and the Activity tab, which keeps the full output of every script it ran.

## Installing and updating

**Windows shows "Windows protected your PC" when the installer starts**
The installer is not code signed, so Windows SmartScreen does not know its publisher. Choose "More info",
then "Run anyway". To check the download yourself first, compare
`Get-FileHash .\CowWorktrees-Setup-<version>.exe -Algorithm SHA256` with the digest shown for the file on
the release page. Where an organisation's policy does not allow unsigned installers, use the zip from the
same page instead: extract it somewhere permanent and run `app\CowWorktrees.exe`. (How SmartScreen
treats the release build has not yet been tried; the two clicks above are what Windows offers for an
unsigned download.)

**The installer shows only a progress bar and asks nothing**
That is all there is to it. It installs for your user only, without administrator rights, into
`%LOCALAPPDATA%\Programs\cow-worktrees`, adds "CoW Worktrees" to the Start menu and starts the app. Its
switches are `/NOLAUNCH` (do not start the app), `/NOICONS` (no shortcut) and `/DIR="<folder>"`. Close the
app before installing over it by hand; the installer checks for a running copy before it replaces files.

**After uninstalling, Claude Code and Codex make full worktrees again**
That is what uninstalling does: before the files go, the uninstaller disconnects every repository (hooks,
Codex section, daily task, the entry in `pools.json`), because they all point at the scripts being
removed. What it did is in `%LOCALAPPDATA%\cow-worktrees\disconnect.log`. Worktrees, bases and each
`cow-config.json` stay, so after installing again, the walkthrough with the same project and the same
folder connects it again without making a new base. To remove the files and leave everything connected,
run the uninstaller with `/KEEPINTEGRATIONS`.

**"The update could not be verified"**
The app compares the SHA-256 of the installer it downloaded with the digest the release publishes for
that file. When the release has no digest, or the two differ, the file is deleted and nothing is
installed. The button then reads "Get the update" and opens the release page: download the installer
there and run it. It installs over the copy you have, in the same folder, so everything that is connected
stays connected. "The update could not be downloaded" means the download itself failed; try again later
or use the release page.

**The app never offers an update, or says "This copy does not update itself"**
- Only a copy put there by the installer updates itself. A copy from the zip gets the banner with "Get
  the update", which opens the release page. Either install with the installer (then see "I moved the
  cow-worktrees folder" below), or close the app and replace the folder's contents with the new zip's.
- The check runs once a day. More > App settings and About > "Check now" checks at once.
- It is off when "Look for a new version once a day" is off there (`CheckForUpdates` in `gui.json`) or
  when the environment variable `COW_NO_UPDATE_CHECK` is `1`.
- "Skip this version" hides that version for good. Empty `SkippedVersion` in
  `%LOCALAPPDATA%\cow-worktrees\gui.json` to see it again; "Check now" also shows it.
- Draft releases and pre-releases are never offered.
- A check that fails (no network) shows nothing and writes one line to `gui.log`.
- "Something is still running": updating replaces the scripts, so it does not start while a job or a
  refresh of a base runs. Try again when they have finished.

Updating from a real GitHub release has not yet been run (1.1.0 is the first release with an installer);
the updater is tested against a stand-in release on the same PC.

**I moved the cow-worktrees folder (or installed it after using a zip copy)**
The Claude Code hooks, the Codex section and the daily task hold the full path of the scripts, so after a
move they still name the old folder. Start the app from the new place: for each repository it shows the
banner "This repository's connections need attention" ("Some connections point at another copy of
cow-worktrees"). **Fix it** runs setup again with the repository's saved settings, which rewrites all
three to the new folder; with several repositories, pick each one in turn. Without the app, run setup
again from the new folder; it keeps everything you do not pass:

```powershell
cd <the new folder>
.\setup.ps1 -Repo D:\src\MyGame -Root E:\cow\MyGame
```

Restart open Claude Code sessions afterwards. The app refuses to set up or change settings while its
folder is somewhere that will not last (the temporary files folder, Downloads, inside a zip, a removable
or network drive): install it, or move it somewhere permanent first.

## The walkthrough and the app

**"No Dev Drive yet", although there is one**
The check looks for a ReFS volume on a built-in drive that is ready. A Dev Drive in a virtual disk (VHDX)
that is not attached right now does not count: attach it in Settings > System > Storage > Advanced
storage settings > Disks & volumes. The screen checks again when you come back to the app. "Create one
for me" is not shown unless `ExperimentalDevDrive` is `true` in `gui.json`: the script behind it has not
yet been run.

**"Git is not installed", although it is**
The app looks for `git.exe` on `PATH` (as the registry has it now) and in `Program Files\Git\cmd` and
`%LOCALAPPDATA%\Programs\Git\cmd`. A Git installed somewhere else has to be on `PATH`. Git older than
2.38 is reported as too old; installing the current Git for Windows over it cures that.

**"Windows needs an update"**
A warning, not a stop. Before Windows 11 24H2 (build 26100) a copy on ReFS is a full copy, so worktrees
work but each takes the full size of the project.

**The walkthrough refuses the project folder**

| It says | What to do |
|---|---|
| This folder is not a Git repository | Choose the folder that has the hidden `.git` folder in it: the one you open in your editor. |
| This is a bare repository | Choose a normal checkout of the project, one with files in it. |
| This is an extra worktree of the project at ... | Setup starts from the project's main folder. Press the "Use ..." button under the message. |
| This folder is one of the worktrees of "..." | It lies inside the folder of a repository that is already set up. Choose the project's own folder. |
| This project is already set up as "..." | Nothing to do: leave the walkthrough and pick the repository at the top of the window. Its settings are behind the Repository button. |
| This is the folder of cow-worktrees itself | Choose the project you work on. |
| Git could not be started | Go back to Check this PC. |

**The walkthrough refuses the folder for the worktrees**

| It says | What to do |
|---|---|
| Enter a full path | For example `E:\cow\MyProject`. |
| X: is not a Dev Drive | Choose a folder on one of the drives in the list. On another drive every worktree would be a full copy. |
| Choose a folder on the drive, not the drive itself | Add a folder name, such as `E:\cow\MyProject`. |
| This is inside your project | Worktrees have to live outside the project's own folder. |
| Another repository already uses this folder | Choose another folder. |
| This folder already has files in it | Choose a new or empty folder. A folder that holds a `cow-config.json` from an earlier setup is accepted, and its base is used again. |

**"Setup cannot run from here"**
Either the scripts were not found next to the app (install again, or start `app\CowWorktrees.exe` from
inside the extracted folder), or the folder is somewhere that will not last; see "I moved the
cow-worktrees folder" above.

**Setup failed on the review screen**
The screen gives the cause in plain words and the script's own message under it; "Show details" has the
whole output, and so has the Activity tab. Fix the cause and press Try again: setup can be repeated, and
a base that was already made is not made twice.

**A repository "cannot be used right now"**
The page says why. "The folder ... does not exist": the drive it is on is not connected (a Dev Drive in a
virtual disk that is not attached); attach it and press Look again. "There is no cow-config.json": set
the repository up again with the same project and folder to write a new one. "... is not valid JSON": a
hand edit broke `cow-config.json`.

**"The cow-worktrees scripts were not found"**
The app runs the scripts from the cow-worktrees folder it sits in, or else from the folder the last setup
ran from. Neither is there: install again, or unzip again and start the app from the `app` folder inside
it.

**I stopped a job, or it was cut off**
Stop is offered only where it is safe, and the last line of the run says what was left:

- New worktree: the partly made worktree is removed again.
- Move to copy-on-write: nothing in the old folder has changed. If the copy had begun, the clone is kept;
  choose Move to copy-on-write on the old worktree again and it is picked up where it stopped.
- Refresh base: the base is where it should be, but has no build output until the next build finishes.
  Press Refresh base again, or wait for the daily task.
- Setup: the partly made base copy is removed again. Press Try again when you are ready.

When the clean-up could not remove a folder, the line gives the command that does:
`git -C "<repo>" worktree remove --force "<folder>"`. "A folder was left at ...; Git does not know it" means
it can simply be deleted. Closing the window does not stop a job: the window is hidden and the app exits
when the job has finished. Starting the app again meanwhile brings the window back; afterwards, how each
run ended is one line in `gui.log`.

**A row shows "N files only look changed to Git"**
See "Files show as modified straight after a clone or migrate" below. The app does not count such files
as changes, and they do not stop Remove.

## Worktrees and the base

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

**Worktrees pile up**
Subagent worktrees are never removed by Claude Code. Run `cow_worktree.ps1 -Prune -DryRun` to see what
the daily task would remove, and `-Prune -OlderThanDays 0` to clear clean, idle ones now. In the app that
is the Prune button, which shows the dry run first.

## Claude Code and Codex

**Claude Code still makes worktrees under `.claude\worktrees`**
- The session started before setup: restart it.
- The checkout you started in has no hooks: setup covers the checkouts that existed when it ran, so run
  it again after adding a checkout. In the app, Repository settings shows "Claude Code is connected in N
  of M folders" and has a Repair button for this.
- Claude Code was switched off for the repository, in Repository settings or by a setup run with
  `-NoClaude`. From 1.1.0 that removes the hooks from every checkout; switch it on again, or run setup
  without `-NoClaude`.
- The hook fell back to a plain checkout: `hook.log` says why.

**A Claude Desktop worktree session times out while starting**
Desktop allows 5 minutes. A clone normally takes well under one, but a busy disk (a base rebuild, a big
copy) can stretch it. Try again when the disk is quiet.

**The guard blocks a command that only mentions `git worktree add`**
Quoted text is ignored, but unquoted mentions count. Put `COW_ALLOW=1` in the command.

**A setup run took away my hooks, my Codex section or the daily task**
From 1.1.0, `-NoClaude`, `-NoCodex` and `-NoSchedule` on a later run remove that piece; in 1.0.x they left
it alone. A script that calls `setup.ps1` with one of them to "skip" a step now switches it off. Run setup
again without the switch to put it back.

## Moving worktrees

**`migrate_worktree.ps1` says something still has the folder open**
A terminal, editor, file explorer or Claude/Codex session is inside the old worktree. Close it and run
the same command again (in the app: Move to copy-on-write again): the verified copy is reused and nothing
has been removed.

**A `migrate_worktree.ps1` run stopped halfway**
Run the same command again. A clone it left in the pool is recognised by what it recorded (`cow.migrateFrom`
and `cow.migrateState` in the clone's config) and finished: the copy is redone if it had not completed,
then everything is verified again; the old worktree is only retired with `-RemoveOld`, after the checks. A
clone from a run that stopped right after the switch (no record yet, detached, nothing changed in it) is
finished the same way. Anything else under that name is refused with "already exists"; if it is a leftover
you do not need, `git -C <repo> worktree remove --force <root>\<name>` discards it.

## Scripts

**Scripts are blocked: "running scripts is disabled" or "not digitally signed"**
Unblock the downloaded files (`Get-ChildItem C:\Tools\cow-worktrees -Recurse | Unblock-File`) and run
them with `powershell -ExecutionPolicy Bypass -File ...`, as the hooks do. The app starts every script
that way, so it is not affected.
