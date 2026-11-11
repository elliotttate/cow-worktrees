# Warm builds (MSBuild)

Without warm builds, a new worktree has only the files Git tracks, so the first build in it is a full
one. With a `build` section in `cow-config.json`, the base keeps a build, and every clone starts with it.

## Setting it up

```powershell
.\setup.ps1 -Repo D:\src\MyGame -Root E:\cow\MyGame `
    -BuildPrepare 'powershell -NoProfile -File tools\generate_projects.ps1' `
    -BuildCommand 'msbuild MyGame.sln /m /p:Configuration={config} /p:Platform=x64' `
    -BuildConfigs Debug,Release
.\scripts\cow_worktree.ps1 -Root E:\cow\MyGame -RefreshBase -Build     # first build; or pass -BuildNow to setup
```

In the app this is "Faster first builds": a screen of the walkthrough (off unless you turn it on; when
the project folder holds a `.sln` it fills in an MSBuild command for it) and a section of Repository
settings. Set up from the walkthrough, the base is first built by the next daily task, or when you press
Refresh base.

To change it later, run setup again with only the part that changes: `-BuildCommand`, `-BuildPrepare` and
`-BuildConfigs` each replace their own part of the `build` section and leave the other two, an empty
`-BuildPrepare ''` or `-BuildConfigs ''` clears that part, and `-NoBuild` removes the section, after
which clones start cold.

`build_base.ps1` runs each `prepare` line once, then `command` once per configuration with `{config}`
filled in. Each line runs through `powershell.exe -NoProfile -Command`, with the base as the working
directory. On success it writes:

- `base-build.json`: the commit and the configurations built;
- `base-build-state.txt`: the list of MSBuild state files clones relocate.

It runs at below-normal priority, so a rebuild doesn't slow down your work; the build tools inherit the
priority. It fails if the build changes any tracked file in the base, because the base must stay clean,
so build output has to be ignored by Git.

The daily task runs `-RefreshBase -Fetch -Build`. When the base moves to a new commit, or the list of
configurations changes, it rebuilds; otherwise it does nothing.

## Why relocation is needed

MSBuild decides what is up to date from **file-tracking logs** (`*.tlog`), plus `*.lastbuildstate` and
`*.recipe` files. They hold absolute paths: `E:\COW\MYGAME\BASE\SRC\FOO.CPP`. In a clone at
`E:\cow\MyGame\feature`, none of the sources match those paths, so MSBuild rebuilds everything. In one
test, that was all 1,910 sources in 13.4 minutes.

`relocate_build.ps1` rewrites the base's path to the clone's in exactly those files:

- It keeps each file's encoding (tlogs are UTF-16), its letter case (tlogs store paths in upper case),
  and its timestamp.
- It never touches binaries.

After relocation, the same clone's first build took 5 s and compiled nothing. Switching the clone to
another commit rebuilds only what that commit changed.

## Caveats

- **Debug info names the base.** `.obj` and `.pdb` files for unchanged sources still record
  `E:\cow\MyGame\base\...` as the source path. A debugger opening such a file opens the base's copy,
  which has the same content. Make edits in your own worktree. If the base changes, the next command
  refuses to use it until it is clean again.
- **Generated project files** are cloned too, and they describe the base's commit. If your project
  files are generated, run the generator in a new worktree before building (as in any fresh checkout);
  otherwise sources the branch added are missing from the projects, and the link fails.
- **Clones made during a base rebuild** skip relocation, because the build output is incomplete, and
  their first build is a full one. They are still correct.
- **Other build systems** (CMake/Ninja, Cargo, Gradle) keep their own absolute-path state. Their output
  is still cloned, but without relocation they will rebuild. Contributions welcome.
