# Claude Code

Setup (the app's walkthrough, or `setup.ps1`) merges three hooks into `.claude\settings.local.json` of
every checkout of the repository that is on disk: the main checkout, its other worktrees, the base, and
the worktrees already in the pool. New clones copy the base's file, so they get the hooks too. Other
settings and hooks in those files are kept, and a file that already has the hooks is not written again.
`uninstall.ps1` removes only these entries.

`settings.local.json` is Claude Code's file, so it is written back the way it was found: in the encoding
it has, with or without a byte order mark, and with text outside ASCII intact. A new one is UTF-8 without
a byte order mark. A file that is not a JSON object is left alone, with a warning. (1.0.x read such a
file as ANSI and wrote it back as UTF-8 with a byte order mark, which damaged characters outside ASCII.)

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Bash", "hooks": [ { "type": "command", "if": "Bash(*worktree*)",
          "command": "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
          "args": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                   "C:\\Tools\\cow-worktrees\\scripts\\cc_guard_hook.ps1", "-Root", "E:\\cow\\MyGame"],
          "timeout": 30 } ] },
      { "matcher": "PowerShell", "hooks": [ { "...": "the same, with \"if\": \"PowerShell(*worktree*)\"" } ] }
    ],
    "WorktreeCreate": [ { "hooks": [ { "type": "command", "command": "...powershell.exe",
          "args": ["...", "cc_worktree_hook.ps1", "create", "-Root", "E:\\cow\\MyGame"], "timeout": 1800 } ] } ],
    "WorktreeRemove": [ { "hooks": [ { "type": "command", "command": "...powershell.exe",
          "args": ["...", "cc_worktree_hook.ps1", "remove", "-Root", "E:\\cow\\MyGame"], "timeout": 600 } ] } ]
  }
}
```

The script paths are those of the folder setup ran from: `C:\Tools\cow-worktrees` in the examples,
`%LOCALAPPDATA%\Programs\cow-worktrees` (written out in full) for a copy put there by the installer. The
entries are the same as 1.0.0 wrote, so a setup made by one version can be updated or removed by another.

Claude Code reads settings files when a session starts, so restart open sessions after setup.

## Switching it on and off

`setup.ps1 -NoClaude` means "without the hooks":

- on a first run they are not added;
- on a later run they are also **removed**, from every checkout of the repository, the base and the
  pool's worktrees included. Before 1.1.0 a later run with `-NoClaude` left hooks that were there alone.

Running setup again without the switch puts them back in every checkout, the worktrees that are already
in the pool included. The choice is recorded in `cow-config.json` as `integrations.claude` (see
[configuration.md](configuration.md)).

In the app:

- The walkthrough's "Connect your AI tools" screen has a Claude Code switch. It starts on when Claude Code
  was found on the PC (`claude` on `PATH`, `~\.local\bin\claude.exe`, or a `~\.claude` folder), and "What
  exactly gets added" names the file.
- Repository settings has a Claude Code row that shows what is in the files now: **Connected** (the hooks
  are in every checkout of the repository that is on disk, the base and the pool's worktrees included,
  and they run this copy's scripts), **Not connected**, connected in some of those folders (the ones
  without are named), or **Connected to another copy of the tool** (the hooks run scripts from another
  folder). Turn the switch off or on and press Apply: that runs `setup.ps1` with or without `-NoClaude`.
- **Repair**, on a row that is half there or points elsewhere, runs setup again with the saved choices,
  which writes the hooks into every checkout and points them at this copy.
- A banner in the main window, "This repository's connections need attention", appears when Claude Code
  should be connected and is not, when the hooks are missing from the project's main folder or the base,
  or when they point at another copy. **Fix it** does what Repair does. A checkout made after setup has
  no hooks until setup runs again; the settings page shows that, without a banner.

## WorktreeCreate

A `WorktreeCreate` hook replaces Claude Code's own `git worktree add`. It fires for:

- `claude --worktree`,
- the `EnterWorktree` tool,
- subagents with `isolation: "worktree"`, background sessions and workflow agents,
- Claude Desktop's "worktree" sessions (verified).

The hook:

- makes `<root>\<name>` on branch `<branchPrefix><name>` (default `worktree-<name>`) with
  `cow_worktree.ps1`;
- starts it at the HEAD of the **session's** checkout. Claude Code passes the main checkout as `cwd`
  even when the session runs in a linked worktree, so the hook reads `CLAUDE_PROJECT_DIR` instead;
- prints the path as its last line of output, which Claude Code requires;
- falls back to a plain `git worktree add` if the copy-on-write route is unavailable (no pool, base
  missing or dirty), but only when the drive has `fallbackMinFreeGB` free. Otherwise it fails with a
  message rather than nearly filling the disk.

Claude Desktop passes no branch to the hook, so a desktop worktree session starts at the main
checkout's commit, whatever branch was picked in the app. Switch in the session if you need another one.

## WorktreeRemove

The hook runs `git worktree remove --force`, as Claude Code's own default does. Claude Code asks for
removal only after you chose it, or for a clean worktree. The branch is deleted only if it still
points at the commit the worktree started from.

Claude Code never runs `WorktreeRemove` for subagent worktrees, and archiving a Claude Desktop session
did not run it in testing. Such worktrees stay until the daily task's prune (idle for `pruneDays`, clean,
nothing unpushed), or until you remove them yourself.

## The guard (PreToolUse)

The guard denies a Bash or PowerShell tool call that runs `git ... worktree add` in a repository with a
pool, and tells Claude to use `cow_worktree.ps1`. Details:

- The `if` filter starts it only for commands that mention "worktree", so other commands pay nothing.
- Text inside quotes is ignored, such as a prompt or a `grep` pattern that mentions the command. The
  exception is a quoted script handed to a shell with `-c`, `-Command` or `/c`, which is checked.
- `COW_ALLOW=1` anywhere in the command lets it through.
- Denials are logged to `<root>\hook.log`.

## Logs

Every hook call is written to `<root>\hook.log`: the event, the working directory, the input JSON, and
the names of `CLAUDE*` environment variables (values only for non-secret ones). It is the first place to
look when a worktree turns up somewhere unexpected. The app shows its end on the Base tab.

## Testing the hooks

From inside a checkout of the repository:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Tools\cow-worktrees\tests\hook_selftest.ps1
```

This drives the hook the way Claude Code does. It creates and removes a worktree with and without a
commit, and checks the branch, the start commit, the clean status, and that an unused branch is deleted
while a used one is kept.
