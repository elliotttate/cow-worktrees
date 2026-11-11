# Codex

The Codex app has no hook that can replace how it creates worktrees. Its own managed worktrees are
detached full checkouts under `$CODEX_HOME\worktrees`, and you can change that folder in
Settings > Worktrees. Its "local environment" setup scripts run only after the checkout is made.

So for Codex, cow-worktrees works through instructions. Setup (the app's walkthrough, or `setup.ps1`)
writes this section into `~\.codex\AGENTS.md`, which every Codex session reads:

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
replaces the section in place, and `uninstall.ps1` removes it. The script path in it is that of the
folder setup ran from.

Anything else in the file is left alone, byte for byte: its text, its line breaks and its encoding, with
or without a byte order mark. A new file is UTF-8 without a byte order mark, and the file is written only
when the section changes. (1.0.x read the file as ANSI and wrote it back as UTF-8 with a byte order mark,
which damaged characters outside ASCII in the rest of the file.)

## Which file

1. `-CodexAgentsFile <file>` on `setup.ps1` or `uninstall.ps1`;
2. else the environment variable `COW_CODEX_AGENTS_FILE`, when it is set;
3. else `~\.codex\AGENTS.md`.

The variable exists for the tests, which must never touch the real file, and the app reads it the same
way. Set it for the app and for `setup.ps1` alike if your Codex instructions live somewhere else.

## Switching it on and off

`setup.ps1 -NoCodex` means "without the section": on a first run it is not written, and on a later run it
is also **removed** if it is there. Before 1.1.0 a later run with `-NoCodex` left the section alone.
Running setup again without the switch writes it back. The choice is recorded in `cow-config.json` as
`integrations.codex` (see [configuration.md](configuration.md)).

In the app:

- The walkthrough's "Connect your AI tools" screen has a Codex switch. It starts on when Codex was found
  on the PC (`codex` on `PATH`, `%LOCALAPPDATA%\Programs\OpenAI\Codex`, or a `~\.codex` folder), and "What
  exactly gets added" names the file.
- Repository settings has a Codex row that shows what is in the file now: **Connected**, **Not
  connected** (no file, or no section for this repository), or **Connected to another copy of the tool**
  (the section names scripts in another folder, for example after the tool was moved). Turn the switch
  off or on and press Apply: that runs `setup.ps1` with or without `-NoCodex`. **Repair** rewrites a
  section that points elsewhere.
- When the section should be there and is missing, or points at another copy, the main window shows the
  banner "This repository's connections need attention" with **Fix it**.
- "Using another agent? Copy the instructions", in the walkthrough and in Repository settings, puts the
  same text, filled in for the repository, on the clipboard, to paste into any agent's instruction file.

Notes:

- Codex agents often make worktrees themselves with `git worktree add --no-checkout` plus
  `git sparse-checkout`, keeping only a handful of files. Those are already cheap.
  `migrate_worktree.ps1` refuses sparse checkouts, because moving one gains nothing.
- Codex's command rules (`~\.codex\rules\*.rules`) match a command's first words. On Windows, Codex runs
  each command as one `powershell.exe -Command "..."` string, so a rule cannot forbid `git worktree add`
  inside it. That leaves the instruction as the only lever.
- If you use the Codex app's managed worktrees for this repository, pointing their folder at the Dev
  Drive at least keeps those full checkouts off your main disk. The app offers Move to copy-on-write for
  such a worktree, as for any full worktree outside the pool.
