#Requires -Version 5.1
<#
e2e.ps1 - end-to-end test on a throwaway repository: setup, warm clones, hooks, guard, list, prune,
migrate, refresh and uninstall. Touches nothing of yours: the pool registry, the Codex file and the
Claude settings it writes are all in temporary folders, and the scheduled task it registers is removed.

  powershell -NoProfile -ExecutionPolicy Bypass -File tests\e2e.ps1 [-Drive E]

-Drive is a ReFS volume (a Dev Drive) for the test pool; the first ReFS volume is used by default.
Exit code = number of failed checks.
#>
param([string]$Drive, [switch]$Keep)
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
$top = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $top 'scripts'
$cow = Join-Path $scripts 'cow_worktree.ps1'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$failures = 0; $checks = 0
function Check([bool]$ok, [string]$what) { $script:checks++; if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ } }
function Section([string]$s) { "== $s" }
function Quiet { param([scriptblock]$b) & $b 2>&1 | Out-Null }

if (-not $Drive) { $Drive = (Get-Volume | Where-Object { $_.FileSystem -eq 'ReFS' -and $_.DriveLetter } | Select-Object -First 1).DriveLetter }
if (-not $Drive) { 'no ReFS volume found; pass -Drive'; exit 1 }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP "cow-e2e-$stamp"
$repo = Join-Path $work 'repo'
$root = "${Drive}:\cow-e2e-$stamp"
$env:COW_WORKTREES_HOME = Join-Path $work 'home'
$codexFile = Join-Path $work 'codex\AGENTS.md'
"repo $repo, pool $root"

Section 'a throwaway repository'
New-Item -ItemType Directory -Force -Path $repo | Out-Null
Quiet { git -C $repo init -q -b main }
git -C $repo config user.email 'e2e@example.invalid'; git -C $repo config user.name 'cow-worktrees e2e'
Set-Content (Join-Path $repo '.gitignore') "out/`n"
Set-Content (Join-Path $repo 'README.md') 'test'
$deepRel = ((1..7 | ForEach-Object { "folder-number-$_-padding-xx" }) -join '\') + '\deep-file.txt'
[void][IO.Directory]::CreateDirectory('\\?\' + (Join-Path $repo (Split-Path $deepRel)))
[IO.File]::WriteAllText('\\?\' + (Join-Path $repo $deepRel), 'deep')
git -C $repo config core.longpaths true
Quiet { git -C $repo add -A }; Quiet { git -C $repo commit -q -m 'first' }
Check ((git -C $repo rev-list --count HEAD) -eq '1') 'repository has a commit'

Section 'setup'
$buildCmd = 'New-Item -ItemType Directory -Force out | Out-Null; Set-Content out\build-{config}.txt (git rev-parse HEAD)'
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'setup.ps1') -Repo $repo -Root $root -Name "e2e-$stamp" -BuildCommand $buildCmd -BuildConfigs 'Debug,Release' -CodexAgentsFile $codexFile *> (Join-Path $work 'setup.log')
Check ($LASTEXITCODE -eq 0) "setup.ps1 succeeded (log: $work\setup.log)"
$base = Join-Path $root 'base'
Check (Test-Path (Join-Path $root 'cow-config.json')) 'cow-config.json written'
Check (@(git -C $repo worktree list --porcelain | Where-Object { $_ -like 'locked*' }).Count -ge 1) 'base is a locked worktree'
$settings = Get-Content (Join-Path $repo '.claude\settings.local.json') -Raw | ConvertFrom-Json
Check ($settings.hooks.WorktreeCreate -and $settings.hooks.WorktreeRemove -and $settings.hooks.PreToolUse) 'Claude hooks merged into the repository'
Check (Test-Path (Join-Path $base '.claude\settings.local.json')) 'Claude hooks also in the base (clones inherit them)'
Check ((Get-Content $codexFile -Raw) -match "cow-worktrees:e2e-$stamp begin") 'Codex block written'
Check ([bool](Get-ScheduledTask -TaskPath '\CowWorktrees\' -TaskName "Refresh-e2e-$stamp" -ErrorAction SilentlyContinue)) 'daily task registered'
Check (@(git -C $base status --porcelain --untracked-files=no).Count -eq 0) 'base is clean'

Section 'warm base'
& $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Root $root -RefreshBase -Build *> (Join-Path $work 'build.log')
$bb = Get-Content (Join-Path $root 'base-build.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
Check ($bb -and (@($bb.configs) -join ',') -eq 'Debug,Release') 'base-build.json lists Debug and Release'
Check (Test-Path (Join-Path $base 'out\build-Release.txt')) 'build output in the base'

Section 'clones'
Push-Location $repo
$t1 = & $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Name t1 -Branch t1 -From HEAD 2>$null | Select-Object -Last 1
Check ($LASTEXITCODE -eq 0 -and $t1 -eq (Join-Path $root 't1')) 'cow_worktree made t1 (pool found from the current repository)'
Check ([IO.File]::Exists('\\?\' + (Join-Path $t1 $deepRel))) 'long path carried'
Check (Test-Path (Join-Path $t1 'out\build-Debug.txt')) 'build output carried (warm clone)'
Check ((git -C $t1 symbolic-ref --short HEAD) -eq 't1' -and @(git -C $t1 status --porcelain --untracked-files=no).Count -eq 0) 't1 on branch t1, clean'
Set-Content (Join-Path $repo 'second.txt') 'two'; Quiet { git -C $repo add -A }; Quiet { git -C $repo commit -q -m 'second' }
$t2 = & $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Name t2 -From main 2>$null | Select-Object -Last 1
Check ((Test-Path (Join-Path $t2 'second.txt')) -and (git -C $t2 rev-parse HEAD) -eq (git -C $repo rev-parse main)) 't2 switched past the base to main'
$list = (& $ps -NoProfile -ExecutionPolicy Bypass -File $cow -List -Json 2>$null) -join "`n" | ConvertFrom-Json
Check ((@($list).Name -join ',') -match 't1' -and (@($list).Name -join ',') -match 't2') '-List -Json shows t1 and t2'
$dry = & $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Prune -OlderThanDays 0 -DryRun 2>&1 | ForEach-Object { "$_" }
Check (($dry -join ' ') -match 'would remove t1') '-Prune -DryRun would remove the idle, clean t1'
Pop-Location

Section 'Claude Code hook and guard'
$env:CLAUDE_PROJECT_DIR = $repo
$hookOut = '{"hook_event_name":"WorktreeCreate","cwd":"' + $repo.Replace('\', '\\') + '","name":"hooked"}' | & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'cc_worktree_hook.ps1') create -Root $root 2>$null
$hooked = @($hookOut | Where-Object { "$_".Trim() }) | Select-Object -Last 1
Check ($hooked -eq (Join-Path $root 'hooked') -and (git -C $hooked symbolic-ref --short HEAD) -eq 'worktree-hooked') 'hook create: clone on worktree-hooked'
'{"hook_event_name":"WorktreeRemove","worktree_path":"' + $hooked.Replace('\', '\\') + '"}' | & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'cc_worktree_hook.ps1') remove -Root $root 2>$null | Out-Null
git -C $repo show-ref --verify --quiet refs/heads/worktree-hooked
Check (-not (Test-Path $hooked) -and $LASTEXITCODE -ne 0) 'hook remove: worktree and unused branch gone'
$deny = '{"tool_name":"Bash","tool_input":{"command":"git worktree add ../x HEAD"}}' | & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'cc_guard_hook.ps1') -Root $root
$allow = '{"tool_name":"Bash","tool_input":{"command":"grep -rn \"git worktree add\" docs"}}' | & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'cc_guard_hook.ps1') -Root $root
Check (($deny -join '') -match '"deny"' -and -not (($allow -join '') -match '"deny"')) 'guard denies git worktree add, allows quoted text'
Remove-Item Env:CLAUDE_PROJECT_DIR

Section 'migrate a plain worktree'
$plain = Join-Path $work 'plain'
Quiet { git -C $repo worktree add -q -b plainbr $plain main }
Set-Content (Join-Path $plain 'untracked-note.txt') 'carry me'
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'migrate_worktree.ps1') -From $plain -Name plain -RemoveOld *> (Join-Path $work 'migrate.log')
Check ($LASTEXITCODE -eq 0) "migrate succeeded (log: $work\migrate.log)"
Check ((Get-Item $plain -Force).LinkType -eq 'Junction' -and (Get-Content (Join-Path $plain 'untracked-note.txt')) -eq 'carry me') 'old path is a junction serving the untracked file'
Check ((git -C (Join-Path $root 'plain') symbolic-ref --short HEAD) -eq 'plainbr') 'migrated clone on its branch'

Section 'refresh'
& $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Root $root -RefreshBase -Build *>> (Join-Path $work 'build.log')
Check ((git -C $base rev-parse HEAD) -eq (git -C $repo rev-parse main)) 'base moved to main'
Check ((Get-Content (Join-Path $base 'out\build-Debug.txt')) -eq (git -C $repo rev-parse main)) 'base rebuilt at the new commit'

Section 'phantom changes: files committed raw under a clean filter'
# A stand-in for Git LFS "files that should have been pointers": the clean filter upper-cases, the commit
# holds lower-case content, so Git re-hashes such a file through the filter whenever it does not trust the
# index's cached time (forced here by changing the file's modified time) and reports " M" for a file that is
# identical to the commit. No git-lfs and no race against the clock is needed.
# Git writes paths as UTF-8; a console on code page 437 must not turn them into other characters. The child
# powershell.exe processes inherit the setting. (Set in try/catch: there may be no console. Restored at the end.)
$savedEnc = $null
try { $savedEnc = [Console]::OutputEncoding; [Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437) } catch { "  note  could not set code page 437: $($_.Exception.Message)" }
$uniName = 'n' + [char]0xE4 + [char]0x4E2D + [char]0x6587   # a-umlaut and two CJK characters, built from codes: this file stays ASCII
function Set-Aged([string]$file) { (Get-Item -LiteralPath $file).LastWriteTime = (Get-Date).AddMinutes(-7) }
function Invoke-Cow { $o = @(& $ps -NoProfile -ExecutionPolicy Bypass -File $cow @args 2>&1 | ForEach-Object { "$_" }); $script:cowExit = $LASTEXITCODE; return $o }
function Invoke-Migrate { $o = @(& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'migrate_worktree.ps1') @args 2>&1 | ForEach-Object { "$_" }); $script:migExit = $LASTEXITCODE; return $o }
# stderr dropped (it would break the JSON); ConvertFrom-Json in 5.1 emits an array as one object, so unroll it.
function Get-Rows { return @((& $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Root $root -List -Json 2>$null) -join "`n" | ConvertFrom-Json | ForEach-Object { $_ }) }
git -C $repo config filter.cowtest.clean 'tr a-z A-Z'
git -C $repo config filter.cowtest.smudge cat
# -text: the raw files are never line-ending converted (this PC checks out with autocrlf), so the working file
# really is identical to the commit. core.autocrlf is left alone: it is shared by every worktree of the test.
Set-Content (Join-Path $repo '.gitattributes') '*.raw -text filter=cowtest'
[IO.Directory]::CreateDirectory((Join-Path $repo 'sub')) | Out-Null
foreach ($rel in 'a.raw', 'sub\b.raw') {
    [IO.File]::WriteAllText((Join-Path $repo $rel), "lower case $rel")
    $oid = git -C $repo hash-object -w --no-filters (Join-Path $repo $rel)
    git -C $repo update-index --add --cacheinfo "100644,$oid,$($rel.Replace('\', '/'))"
}
Quiet { git -C $repo add .gitattributes }; Quiet { git -C $repo commit -q -m 'raw content under a clean filter' }
Check ((git -C $repo cat-file -p 'HEAD:a.raw') -ceq 'lower case a.raw') 'the commit holds a.raw as raw lower-case content'

$o = Invoke-Cow -Root $root -Name ph1 -From main
Check ($cowExit -eq 0 -and (Test-Path (Join-Path $root 'ph1\sub\b.raw'))) 'a clone to a commit that adds raw-under-filter files succeeds'
Quiet { Invoke-Cow -Root $root -Remove -Name ph1 }

$o = Invoke-Cow -Root $root -RefreshBase
Check ($cowExit -eq 0 -and (Test-Path (Join-Path $base 'a.raw'))) 'the base moved to the commit with the raw files'
Check (@(git -C $base status --porcelain --untracked-files=no).Count -eq 0) 'after the base moved, git status in it is really clean (the index was settled)'
Set-Aged (Join-Path $base 'a.raw')
Check (((git -C $base status --porcelain --untracked-files=no) -join "`n") -match '(?m)^ M a\.raw') 'git status in the base shows a.raw as modified although it is not'
$o = Invoke-Cow -Root $root -Name ph2
if ($cowExit -ne 0 -or ($o -join ' ') -notmatch 'only looks? changed') { "  note  exit $cowExit; output:"; $o }
Check ($cowExit -eq 0 -and (Test-Path (Join-Path $root 'ph2')) -and ($o -join ' ') -match 'only looks? changed') 'phantom in the base: a clone is still made, and the note is printed'

Add-Content (Join-Path $base 'README.md') 'edited in the base'
$o = Invoke-Cow -Root $root -Name ph3
Check ($cowExit -ne 0 -and ($o -join ' ') -match 'must stay clean' -and ($o -join ' ') -match 'README\.md' -and -not (Test-Path (Join-Path $root 'ph3'))) 'a real edit in the base is still refused, and the path is named'
Quiet { git -C $base checkout -- README.md }
Check (@(git -C $base status --porcelain --untracked-files=no | Where-Object { $_ -notmatch '\.raw$' }).Count -eq 0) 'base restored (only phantoms are left)'

$ph2 = Join-Path $root 'ph2'
Check (@(git -C $ph2 status --porcelain --untracked-files=no).Count -eq 0) 'the clone made next to the base''s phantom is settled: git status is really clean'
Set-Aged (Join-Path $ph2 'a.raw')
Check (((git -C $ph2 status --porcelain --untracked-files=no) -join "`n") -match '(?m)^ M a\.raw') 'git status in ph2 shows the false alarm'
$row = Get-Rows | Where-Object Name -eq 'ph2'
if (-not ($row -and $row.Changed -eq 0 -and $row.Phantom -ge 1)) { "  note  ph2 row: $($row | ConvertTo-Json -Compress); status: $(git -C $ph2 status --porcelain)" }
Check ($row -and $row.Changed -eq 0 -and $row.Phantom -ge 1 -and $row.Untracked -eq 0) '-List -Json: ph2 has Changed 0 and its phantoms counted separately'
$o = Invoke-Cow -Root $root -Prune -OlderThanDays 0 -DryRun
Check (($o -join ' ') -match 'would remove ph2') '-Prune -DryRun would remove the worktree whose only changes are phantoms'
$o = Invoke-Cow -Root $root -Remove -Name ph2
Check ($cowExit -eq 0 -and -not (Test-Path $ph2)) '-Remove removes a worktree whose only changes are phantoms'

$o = Invoke-Cow -Root $root -Name ph4
$ph4 = Join-Path $root 'ph4'
Set-Aged (Join-Path $ph4 'a.raw'); Add-Content (Join-Path $ph4 'README.md') 'a real edit'
$row = Get-Rows | Where-Object Name -eq 'ph4'
if (-not ($row -and $row.Changed -eq 1 -and $row.Phantom -ge 1)) { "  note  ph4 row: $($row | ConvertTo-Json -Compress); status: $(git -C $ph4 status --porcelain)" }
Check ($row -and $row.Changed -eq 1 -and $row.Phantom -ge 1) '-List -Json: ph4 has Changed 1 (the real edit) and phantoms counted separately'
$o = Invoke-Cow -Root $root -Remove -Name ph4
Check ($cowExit -ne 0 -and (Test-Path $ph4)) '-Remove still refuses when there is a real edit as well'
$o = Invoke-Cow -Root $root -Prune -OlderThanDays 0 -DryRun
Check (($o -join ' ') -match 'keep\s+ph4: 1 changed files') '-Prune keeps it'
Quiet { git -C $ph4 checkout -- README.md }
$o = Invoke-Cow -Root $root -Prune -OlderThanDays 0
Check (($o -join ' ') -match 'removed ph4' -and -not (Test-Path $ph4)) '-Prune removes a phantom-only worktree (git refuses it; the script checks and uses --force)'

$plain2 = Join-Path $work 'plain2'
Quiet { git -C $repo worktree add -q -b plain2br $plain2 main }
Set-Aged (Join-Path $plain2 'a.raw'); Set-Content (Join-Path $plain2 'untracked-note2.txt') 'carry me too'
Set-Content (Join-Path $plain2 "untracked-$uniName.txt") 'carry my odd name too'
Check (((git -C $plain2 status --porcelain) -join "`n") -match '(?m)^ M a\.raw') 'the old worktree shows the false alarm'
$o = Invoke-Migrate -From $plain2 -Name plain2
Check ($migExit -eq 0 -and (Get-Content (Join-Path $root 'plain2\untracked-note2.txt')) -eq 'carry me too') 'migrate: a phantom and an untracked file in the old worktree; the file is carried'
Check (($o -join ' ') -match 'only looks? changed') 'migrate says how the phantom was treated'
Check ((Get-Content -LiteralPath (Join-Path $root "plain2\untracked-$uniName.txt") -ErrorAction SilentlyContinue) -eq 'carry my odd name too') 'migrate: an untracked file with a non-ASCII name is carried (console on code page 437)'
# a phantom whose name has non-ASCII characters, in its own small repository
$enc = Join-Path $work 'enc'
[IO.Directory]::CreateDirectory($enc) | Out-Null
Quiet { git -C $enc init -q -b main }
git -C $enc config user.email 'e2e@example.invalid'; git -C $enc config user.name 'cow-worktrees e2e'; git -C $enc config core.autocrlf false
git -C $enc config filter.cowtest.clean 'tr a-z A-Z'; git -C $enc config filter.cowtest.smudge cat
Set-Content (Join-Path $enc '.gitattributes') '*.raw -text filter=cowtest'
[IO.Directory]::CreateDirectory((Join-Path $enc $uniName)) | Out-Null
foreach ($rel in 'plain.raw', "$uniName.raw", "$uniName\$uniName.raw") {
    [IO.File]::WriteAllText((Join-Path $enc $rel), "lower case $rel")
    $oid = git -C $enc hash-object -w --no-filters (Join-Path $enc $rel)
    git -C $enc update-index --add --cacheinfo "100644,$oid,$($rel.Replace('\', '/'))"
}
Quiet { git -C $enc add .gitattributes }; Quiet { git -C $enc commit -q -m x }
foreach ($rel in 'plain.raw', "$uniName.raw", "$uniName\$uniName.raw") { Set-Aged (Join-Path $enc $rel) }
$res = & $ps -NoProfile -ExecutionPolicy Bypass -Command ". '$(Join-Path $scripts 'CowCommon.ps1')'; `$r = Get-CowStatus -Dir '$enc'; 'entries={0} phantom={1}' -f `$r.Entries.Count, `$r.Phantom.Count"
Check ("$res" -eq 'entries=3 phantom=3') "phantoms with non-ASCII names are phantoms too (console on code page 437): $res"
Check (((git -C $plain2 status --porcelain --untracked-files=no) -join "`n") -match '(?m)^ M a\.raw') 'migrate wrote nothing into the old worktree (the phantom is still there)'

$plain3 = Join-Path $work 'plain3'
Quiet { git -C $repo worktree add -q -b plain3br $plain3 main }
Set-Content (Join-Path $plain3 'untracked-note3.txt') 'resume me'
$o = Invoke-Cow -Root $root -Name plain3 -From main
$c3 = Join-Path $root 'plain3'
git -C $c3 config --worktree --unset cow.startCommit
Check ($cowExit -eq 0 -and -not (git -C $c3 config --worktree --get cow.startCommit)) 'a clone at the old commit without cow.startCommit (as a clone that stopped after the switch)'
$o = Invoke-Migrate -From $plain3 -Name plain3
Check ($migExit -eq 0 -and ($o -join ' ') -match 'resuming' -and (Get-Content (Join-Path $c3 'untracked-note3.txt')) -eq 'resume me') 'migrate resumes that clone and carries the file'
Check ((git -C $c3 config --worktree --get cow.migrateState) -eq 'copied' -and (git -C $c3 config --worktree --get cow.startCommit) -eq (git -C $plain3 rev-parse HEAD)) 'the clone is marked copied and has cow.startCommit now'
$o = Invoke-Migrate -From $plain3 -Name plain3 -RemoveOld
Check ($migExit -eq 0 -and (Get-Item $plain3 -Force).LinkType -eq 'Junction' -and (git -C $c3 symbolic-ref --short HEAD) -eq 'plain3br') 'migrate again with -RemoveOld retires the old worktree'

$plain4 = Join-Path $work 'plain4'
Quiet { git -C $repo worktree add -q -b plain4br $plain4 main }
Quiet { Invoke-Cow -Root $root -Name plain4 -From main }
$o = Invoke-Migrate -From $plain4 -Name plain4
Check ($migExit -ne 0 -and ($o -join ' ') -match 'already exists' -and -not (Get-Item $plain4 -Force).LinkType) 'a worktree of that name with cow.startCommit and no marker is refused without -RemoveOld'
if ($Keep) { "kept everything: $work and $root"; exit $failures }
Section 'cleanup and uninstall'
foreach ($n in 't1', 't2') { & $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Root $root -Remove -Name $n *> $null }
[IO.Directory]::Delete($plain, $false)
git -C $repo worktree unlock (Join-Path $root 'plain') 2>$null
Remove-Item (Join-Path $root 'plain\untracked-note.txt') -Force
$o = Invoke-Cow -Root $root -Remove -Name plain
if ($cowExit) { "  note  removing the migrated clone failed:"; $o }
# what the phantom section left: old worktrees, the clones made from them, one junction
[IO.Directory]::Delete($plain3, $false)
foreach ($old in $plain2, $plain4) { git -C $repo worktree remove --force $old 2>$null }
try { if ($savedEnc) { [Console]::OutputEncoding = $savedEnc } } catch { }
foreach ($n in 'plain2', 'plain3', 'plain4') { $c = Join-Path $root $n; git -C $repo worktree unlock $c 2>$null; git -C $repo worktree remove --force $c 2>$null }
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'uninstall.ps1') -Root $root -RemovePool -CodexAgentsFile $codexFile *> (Join-Path $work 'uninstall.log')
Check ($LASTEXITCODE -eq 0) "uninstall succeeded (log: $work\uninstall.log)"
if ($LASTEXITCODE) { Get-Content (Join-Path $work 'uninstall.log') -Tail 6 }
Check (-not (Test-Path $root)) 'pool root removed'
Check (-not (Get-ScheduledTask -TaskPath '\CowWorktrees\' -TaskName "Refresh-e2e-$stamp" -ErrorAction SilentlyContinue)) 'task removed'
Check (-not ((Get-Content $codexFile -Raw) -match 'cow-worktrees:')) 'Codex block removed'
$after = Get-Content (Join-Path $repo '.claude\settings.local.json') -Raw | ConvertFrom-Json
Check (-not $after.hooks) 'Claude hooks removed'
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
"$checks checks, $failures failed"
exit $failures
