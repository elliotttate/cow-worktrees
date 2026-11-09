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

if ($Keep) { "kept everything: $work and $root"; exit $failures }
Section 'cleanup and uninstall'
foreach ($n in 't1', 't2') { & $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Root $root -Remove -Name $n *> $null }
[IO.Directory]::Delete($plain, $false)
git -C $repo worktree unlock (Join-Path $root 'plain') 2>$null
Remove-Item (Join-Path $root 'plain\untracked-note.txt') -Force
& $ps -NoProfile -ExecutionPolicy Bypass -File $cow -Root $root -Remove -Name plain *> $null
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'uninstall.ps1') -Root $root -RemovePool -CodexAgentsFile $codexFile *> (Join-Path $work 'uninstall.log')
Check ($LASTEXITCODE -eq 0) "uninstall succeeded (log: $work\uninstall.log)"
Check (-not (Test-Path $root)) 'pool root removed'
Check (-not (Get-ScheduledTask -TaskPath '\CowWorktrees\' -TaskName "Refresh-e2e-$stamp" -ErrorAction SilentlyContinue)) 'task removed'
Check (-not ((Get-Content $codexFile -Raw) -match 'cow-worktrees:')) 'Codex block removed'
$after = Get-Content (Join-Path $repo '.claude\settings.local.json') -Raw | ConvertFrom-Json
Check (-not $after.hooks) 'Claude hooks removed'
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
"$checks checks, $failures failed"
exit $failures
