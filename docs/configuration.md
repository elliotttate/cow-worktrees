# Configuration

## `<root>\cow-config.json`

Written by `setup.ps1`. Run setup again to change it, or edit it by hand.

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
  }
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

## `%LOCALAPPDATA%\cow-worktrees\pools.json`

The pools on this machine, written by `setup.ps1` and `uninstall.ps1`, and read by the scripts and the
app. Set `COW_WORKTREES_HOME` to keep it somewhere else; the tests do this.

```json
{ "version": 1, "toolDir": "C:\\Tools\\cow-worktrees", "pools": [ { "name": "MyGame", "root": "E:\\cow\\MyGame" } ] }
```

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
