#Requires -Version 5.1
<#
e2e.ps1 - end-to-end test on a throwaway repository: setup, setup again with other switches, warm clones,
hooks, guard, list, prune, migrate, refresh and uninstall; and a second throwaway repository set up from a
copy of the tool in a folder with a space in its name. Touches nothing of yours: the pool registry, the
Codex files and the Claude settings it writes are all in temporary folders, and the scheduled tasks it
registers (one per test pool; the second pool's is started once, by Task Scheduler) are removed.

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
# Where a run without -CodexAgentsFile writes (the second pool below relies on it), and a safety net:
# with this set, no run of this test can fall back to the real ~\.codex\AGENTS.md.
$codexEnv = Join-Path $work 'codex-env\AGENTS.md'
$env:COW_CODEX_AGENTS_FILE = $codexEnv
"repo $repo, pool $root"

# ---- what setup wires up, read back ----
function Get-Cfg([string]$poolRoot) { return (Get-Content (Join-Path $poolRoot 'cow-config.json') -Raw | ConvertFrom-Json) }
# How many hook entries of this tool a checkout's settings.local.json holds: a full set is 4.
function Get-OurHooks([string]$checkout) {
    $f = Join-Path $checkout '.claude\settings.local.json'
    if (-not (Test-Path $f)) { return 0 }
    $s = [IO.File]::ReadAllText($f) | ConvertFrom-Json
    if (-not $s.hooks) { return 0 }
    return @($s.hooks.PSObject.Properties | ForEach-Object { $_.Value } | ForEach-Object { $_.hooks } | Where-Object { "$($_.args)" -match 'cc_(worktree|guard)_hook\.ps1' }).Count
}
function Test-Block([string]$file, [string]$name) { return ((Test-Path $file) -and "$(Get-Content $file -Raw)" -match "cow-worktrees:$name begin") }
function Get-Task([string]$name) { return (Get-ScheduledTask -TaskPath '\CowWorktrees\' -TaskName "Refresh-$name" -ErrorAction SilentlyContinue) }
# The time of day a pool's task is registered for, as HH:mm; '' when there is no task.
function Get-TaskAt([string]$name) {
    $t = Get-Task $name
    if (-not $t) { return '' }
    return ([DateTimeOffset]::Parse($t.Triggers[0].StartBoundary, [Globalization.CultureInfo]::InvariantCulture)).ToString('HH:mm', [Globalization.CultureInfo]::InvariantCulture)
}
# The "==> " lines of a run, in order.
function Get-Steps($lines) { return (@($lines | Where-Object { $_ -like '==> *' } | ForEach-Object { $_.Substring(4) }) -join ' | ') }
# The hooks object setup writes for a pool, as compressed JSON, spelled out here exactly as 1.0.0 wrote it:
# either version must be able to update and remove what the other one set up.
function Get-WantedHooks([string]$scriptsDir, [string]$poolRoot) {
    $head = '"command":"' + $ps.Replace('\', '\\') + '","args":["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File","' + $scriptsDir.Replace('\', '\\') + '\\'
    $tail = '"-Root","' + $poolRoot.Replace('\', '\\') + '"],'
    return '{"PreToolUse":[' +
        '{"matcher":"Bash","hooks":[{"type":"command","if":"Bash(*worktree*)",' + $head + 'cc_guard_hook.ps1",' + $tail + '"timeout":30}]},' +
        '{"matcher":"PowerShell","hooks":[{"type":"command","if":"PowerShell(*worktree*)",' + $head + 'cc_guard_hook.ps1",' + $tail + '"timeout":30}]}],' +
        '"WorktreeCreate":[{"hooks":[{"type":"command",' + $head + 'cc_worktree_hook.ps1","create",' + $tail + '"timeout":1800,"statusMessage":"Cloning a copy-on-write worktree"}]}],' +
        '"WorktreeRemove":[{"hooks":[{"type":"command",' + $head + 'cc_worktree_hook.ps1","remove",' + $tail + '"timeout":600,"statusMessage":"Removing the worktree"}]}]}'
}

# ---- files that are not only this tool's ----
# Text outside ASCII, built from character codes so that this file stays ASCII: e-acute, a CJK character,
# and an emoji (a surrogate pair). Setup, every run after it and uninstall must leave such text, the file's
# byte order mark (there or not) and its line breaks as they are.
$odd = 'caf' + [char]0xE9 + ' ' + [char]0x4E2D + ' ' + [char]0xD83D + [char]0xDE00
$utf8 = New-Object Text.UTF8Encoding($false); $utf8Bom = New-Object Text.UTF8Encoding($true)
function Test-Bom([string]$file) { $b = [IO.File]::ReadAllBytes($file); return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) }
function Test-Odd([string]$file) { return [IO.File]::ReadAllText($file, $utf8).Contains($odd) }   # UTF-8, with or without the mark
function Get-Bytes([string]$file) { return [Convert]::ToBase64String([IO.File]::ReadAllBytes($file)) }
function Test-StartsWith([string]$file, [string]$bytes64) {
    $b = [IO.File]::ReadAllBytes($file); $want = [Convert]::FromBase64String($bytes64)
    if ($b.Length -lt $want.Length) { return $false }
    for ($i = 0; $i -lt $want.Length; $i++) { if ($b[$i] -ne $want[$i]) { return $false } }
    return $true
}
function New-TextFile([string]$file, [string]$text, $encoding) { [void][IO.Directory]::CreateDirectory((Split-Path -Parent $file)); [IO.File]::WriteAllText($file, $text, $encoding) }

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
# What may be there before setup ever runs, here as Claude Code, Codex and Git write: UTF-8 without a byte
# order mark, LF line breaks. The exclude file's last line has no line break.
$settingsFile = Join-Path $repo '.claude\settings.local.json'
$excludeFile = Join-Path $repo '.git\info\exclude'
New-TextFile $settingsFile ('{ "permissions": { "allow": [ "Read(' + $odd + ')" ] } }' + "`n") $utf8
New-TextFile $codexFile "# My own notes`n`n$odd`n" $utf8
[IO.File]::AppendAllText($excludeFile, "# $odd`nno-line-break-after-this", $utf8)
# A personal ignore file must not do setup's work here (Claude Code puts .claude/settings.local.json into the
# global one): this repository reads an empty one instead, so the line in .git\info\exclude is what counts.
$noIgnore = Join-Path $work 'no-personal-ignore'
[IO.File]::WriteAllText($noIgnore, '')
git -C $repo config core.excludesFile $noIgnore.Replace('\', '/')
$codexOrig = Get-Bytes $codexFile; $excludeOrig = Get-Bytes $excludeFile

Section 'setup'
$buildCmd = 'New-Item -ItemType Directory -Force out | Out-Null; Set-Content out\build-{config}.txt (git rev-parse HEAD)'
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'setup.ps1') -Repo $repo -Root $root -Name "e2e-$stamp" -BuildCommand $buildCmd -BuildConfigs 'Debug,Release' -ScheduleAt '04:40' -CodexAgentsFile $codexFile *> (Join-Path $work 'setup.log')
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
$pool1 = "e2e-$stamp"
$c = Get-Cfg $root
Check ($c.integrations.claude -eq $true -and $c.integrations.codex -eq $true -and $c.integrations.scheduleAt -eq '04:40' -and $c.integrationRevision -eq 1) 'cow-config.json records the choices: integrations (claude, codex, scheduleAt) and integrationRevision 1'
Check ((Get-TaskAt $pool1) -eq '04:40') 'the task is registered for the -ScheduleAt time'
Check (($settings.hooks | ConvertTo-Json -Depth 20 -Compress) -ceq (Get-WantedHooks $scripts $root)) 'the hook entries are exactly what 1.0.0 wrote'
Check (-not (Test-Path $codexEnv)) 'an explicit -CodexAgentsFile wins over COW_CODEX_AGENTS_FILE'
Check ((Test-Odd $settingsFile) -and -not (Test-Bom $settingsFile)) 'settings.local.json that was there (UTF-8, no byte order mark): its text outside ASCII is intact, and it still has no byte order mark'
Check ((Test-Odd $codexFile) -and -not (Test-Bom $codexFile) -and (Test-StartsWith $codexFile $codexOrig)) 'the Codex file that was there: the same, and it starts with exactly the bytes it had'
Check (-not [IO.File]::ReadAllText($codexFile, $utf8).Contains("`r")) 'its line breaks are still all LF, the new block''s too'
Check ((Get-Bytes $excludeFile) -ceq [Convert]::ToBase64String([byte[]]([Convert]::FromBase64String($excludeOrig) + $utf8.GetBytes("`n.claude/settings.local.json`n")))) '.git\info\exclude: every byte that was there, then the new line on a line of its own, ending in LF as the file''s lines do'
git -C $repo check-ignore -q .claude/settings.local.json
Check ($LASTEXITCODE -eq 0) 'Git ignores .claude/settings.local.json through that line'
Check (-not (Test-Bom (Join-Path $base '.claude\settings.local.json'))) 'a new settings.local.json (the base''s) is UTF-8 without a byte order mark'
Check ((Test-Bom (Join-Path $root 'cow-config.json')) -and (Test-Bom (Join-Path $env:COW_WORKTREES_HOME 'pools.json'))) 'a new cow-config.json and pools.json have a byte order mark, as before (the other scripts read them with Get-Content)'

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

Section 'setup again: what is not passed stays; -NoCodex, -NoClaude, -NoSchedule and -NoBuild remove their piece'
# Every run here passes only -Repo, -Root, the Codex file and what is named: no -Name, no -ScheduleAt.
function Invoke-Setup { $o = @(& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'setup.ps1') -Repo $repo -Root $root -CodexAgentsFile $codexFile @args 2>&1 | ForEach-Object { "$_" }); $script:setupExit = $LASTEXITCODE; return $o }
$allSteps = 'Checking prerequisites | Base worktree | Pool config | Claude Code hooks | Codex instructions | Daily upkeep task | Done'
Check ((Get-OurHooks $t1) -eq 4 -and (Get-OurHooks $t2) -eq 4) 'the clones t1 and t2 carry the hooks (they have the base''s settings.local.json)'

$o = Invoke-Setup -NoCodex -BuildPrepare 'echo one, two'
$c = Get-Cfg $root
Check ($setupExit -eq 0 -and -not (Test-Block $codexFile $pool1)) '-NoCodex on a re-run removes the Codex block'
Check ((Get-Bytes $codexFile) -ceq $codexOrig) '... and the Codex file is byte for byte what it was before setup'
Check ((Get-OurHooks $repo) -eq 4 -and (Get-OurHooks $base) -eq 4 -and (Get-TaskAt $pool1) -eq '04:40') '-NoCodex keeps the hooks and the task'
Check ($c.integrations.claude -eq $true -and $c.integrations.codex -eq $false -and $c.integrations.scheduleAt -eq '04:40') 'integrations after -NoCodex: codex false, claude and scheduleAt as they were'
Check ((Get-Steps $o) -eq $allSteps) "removing the block is the step 'Codex instructions'; the titles of that run: $(Get-Steps $o)"
Check ($c.name -eq $pool1) 'a re-run without -Name keeps the name'
Check ($c.build.command -eq $buildCmd -and (@($c.build.configs) -join '|') -eq 'Debug|Release' -and @($c.build.prepare).Count -eq 1 -and @($c.build.prepare)[0] -eq 'echo one, two') '-BuildPrepare without -BuildCommand goes into the build section that is there, as one command (not split at the comma); command and configs stay'

# The task deleted by hand: the config still knows its time.
Unregister-ScheduledTask -TaskPath '\CowWorktrees\' -TaskName "Refresh-$pool1" -Confirm:$false
$o = Invoke-Setup -NoClaude -BuildCommand 'echo {config}'
$c = Get-Cfg $root
Check ($setupExit -eq 0 -and (Get-OurHooks $repo) -eq 0 -and (Get-OurHooks $base) -eq 0) '-NoClaude on a re-run removes the hooks from the repository and the base'
Check ((Get-OurHooks $t1) -eq 0 -and (Get-OurHooks $t2) -eq 0) '... and from the pool''s worktrees'
Check ((Test-Odd $settingsFile) -and -not (Test-Bom $settingsFile) -and (Test-Odd $codexFile) -and -not (Test-Bom $codexFile)) 'after the -NoCodex and -NoClaude runs: text outside ASCII intact in both files, still no byte order mark'
Check (Test-Block $codexFile $pool1) '-NoClaude keeps Codex: the block is back, because this run did not pass -NoCodex'
Check ((Get-TaskAt $pool1) -eq '04:40') 'the task deleted by hand is back at its time, which came from the config'
Check ($c.integrations.claude -eq $false -and $c.integrations.codex -eq $true -and $c.integrations.scheduleAt -eq '04:40') 'integrations after -NoClaude: claude false, codex true, scheduleAt as it was'
Check ((Get-Steps $o) -eq $allSteps) "removing the hooks is the step 'Claude Code hooks'; the titles of that run: $(Get-Steps $o)"
Check ($c.build.command -eq 'echo {config}' -and (@($c.build.configs) -join '|') -eq 'Debug|Release' -and (@($c.build.prepare) -join '|') -eq 'echo one, two') '-BuildCommand alone replaces the command; prepare and configs stay'
Check (@(git -C $base status --porcelain --untracked-files=no).Count -eq 0) 'the base is still clean'

# '""' is how Windows PowerShell hands an empty argument to a program; the app passes "" the same way.
$o = Invoke-Setup -NoSchedule -BuildConfigs 'Release' -BuildPrepare '""'
$c = Get-Cfg $root
Check ($setupExit -eq 0 -and -not (Get-Task $pool1)) '-NoSchedule on a re-run removes the task'
Check ((Get-OurHooks $repo) -eq 4 -and (Get-OurHooks $base) -eq 4 -and (Test-Block $codexFile $pool1)) '-NoSchedule keeps Codex, and the hooks are back in the repository and the base'
Check ((Get-OurHooks $t1) -eq 4 -and (Get-OurHooks $t2) -eq 4) '... and in the pool''s worktrees t1 and t2, which were made before -NoClaude took them out'
Check ($c.integrations.claude -eq $true -and $c.integrations.codex -eq $true -and (Get-Content (Join-Path $root 'cow-config.json') -Raw) -match '"scheduleAt":\s*null') 'integrations after -NoSchedule: scheduleAt is null'
Check ((Get-Steps $o) -eq $allSteps) "removing the task is the step 'Daily upkeep task'; the titles of that run: $(Get-Steps $o)"
Check ($c.build.command -eq 'echo {config}' -and (@($c.build.configs) -join '|') -eq 'Release' -and @($c.build.prepare).Count -eq 0) '-BuildConfigs without -BuildCommand replaces the configs; an empty -BuildPrepare clears the prepare steps; the command stays'

# The next run changes no hook: no settings.local.json may be written, and no worktree folder touched
# (a worktree's folder time is part of what prune reads its idle time from).
function Get-Marks { return (@($repo, $base, $t1, $t2 | ForEach-Object { $f = Join-Path $_ '.claude\settings.local.json'; "$((Get-Item $_).LastWriteTimeUtc.Ticks) $((Get-Item $f).LastWriteTimeUtc.Ticks) $(Get-Bytes $f)" }) -join '|') }
$marks = Get-Marks; $codexMark = "$((Get-Item $codexFile).LastWriteTimeUtc.Ticks) $(Get-Bytes $codexFile)"
$o = Invoke-Setup -NoSchedule -NoBuild
$c = Get-Cfg $root
Check ($marks.Split('|').Count -eq 4 -and (Get-Marks) -ceq $marks) 'a re-run that changes no hook leaves settings.local.json (repository, base, t1, t2) and each folder''s time as they were'
Check (($o -join ' ') -match 'already in place in all \d+ checkouts' -and "$((Get-Item $codexFile).LastWriteTimeUtc.Ticks) $(Get-Bytes $codexFile)" -ceq $codexMark) '... says so, and does not write the Codex file again either'
Check ($setupExit -eq 0 -and -not ($c.PSObject.Properties.Name -contains 'build') -and $c.name -eq $pool1) '-NoBuild removes the build section'
Check ((Get-Steps $o) -eq 'Checking prerequisites | Base worktree | Pool config | Claude Code hooks | Codex instructions | Done') "-NoSchedule with no task left to remove prints no step for it: $(Get-Steps $o)"

$before = Get-Content (Join-Path $root 'cow-config.json') -Raw
$o = Invoke-Setup -ScheduleAt 'banana'
Check ($setupExit -eq 1 -and ($o -join ' ') -match 'not a time of day' -and (Get-Content (Join-Path $root 'cow-config.json') -Raw) -ceq $before -and -not (Get-Task $pool1)) 'a -ScheduleAt that is not a time is refused before anything is changed'
$o = Invoke-Setup -NoBuild -BuildCommand 'echo {config}'
Check ($setupExit -eq 1 -and ($o -join ' ') -match '-NoBuild removes the build section' -and (Get-Content (Join-Path $root 'cow-config.json') -Raw) -ceq $before) '-NoBuild together with -BuildCommand is refused'

$o = Invoke-Setup -BuildCommand $buildCmd -BuildConfigs 'Debug,Release'
$c = Get-Cfg $root
Check ($setupExit -eq 0 -and (Get-OurHooks $repo) -eq 4 -and (Get-OurHooks $base) -eq 4 -and (Test-Block $codexFile $pool1) -and [bool](Get-Task $pool1)) 'a re-run with none of the switches puts all three back'
Check ($c.integrations.claude -eq $true -and $c.integrations.codex -eq $true -and $c.integrations.scheduleAt -eq '05:00' -and (Get-TaskAt $pool1) -eq '05:00') 'with no task and no recorded time left, the task gets the default 05:00'
Check ($c.build.command -eq $buildCmd -and (@($c.build.configs) -join '|') -eq 'Debug|Release' -and @($c.build.prepare).Count -eq 0) 'the build section is as the first setup wrote it (Debug,Release still arrives as one string)'
Check (((Get-Content (Join-Path $repo '.claude\settings.local.json') -Raw | ConvertFrom-Json).hooks | ConvertTo-Json -Depth 20 -Compress) -ceq (Get-WantedHooks $scripts $root)) 'after all that the repository''s hook entries are exactly the first setup''s'
Check (@(git -C $base status --porcelain --untracked-files=no).Count -eq 0) 'the base is clean'
Check ((Test-Odd $settingsFile) -and -not (Test-Bom $settingsFile) -and (Test-Odd $codexFile) -and -not (Test-Bom $codexFile)) 'after all those runs: text outside ASCII intact in settings.local.json and the Codex file, still no byte order mark'

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

Section 'a tool folder with a space in its name, and a second pool set up from it'
$tool2 = Join-Path $env:TEMP "cow tool $stamp"
$scripts2 = Join-Path $tool2 'scripts'
$repo2 = Join-Path $work 'repo2'
$root2 = "${Drive}:\cow-e2e-$stamp-b"
$base2 = Join-Path $root2 'base'
$pool2 = "sp-$stamp"
New-Item -ItemType Directory -Force -Path $tool2, $repo2 | Out-Null
Copy-Item (Join-Path $top 'setup.ps1'), (Join-Path $top 'uninstall.ps1') $tool2
Copy-Item (Join-Path $top 'scripts'), (Join-Path $top 'templates') $tool2 -Recurse
Quiet { git -C $repo2 init -q -b main }
git -C $repo2 config user.email 'e2e@example.invalid'; git -C $repo2 config user.name 'cow-worktrees e2e'
Set-Content (Join-Path $repo2 'README.md') 'second repository'
Quiet { git -C $repo2 add -A }; Quiet { git -C $repo2 commit -q -m 'first' }
Quiet { git -C $repo2 branch stable }
# This pool's files have a byte order mark and CRLF line breaks (the first pool's have neither).
$sf2 = Join-Path $repo2 '.claude\settings.local.json'
New-TextFile $sf2 ('{ "permissions": { "allow": [ "Read(' + $odd + ')" ] } }' + "`r`n") $utf8Bom
New-TextFile $codexEnv "# Notes of another tool`r`n`r`n$odd`r`n" $utf8Bom
$sf2Orig = Get-Bytes $sf2; $codexEnvOrig = Get-Bytes $codexEnv
# No -CodexAgentsFile in this section: the file comes from COW_CODEX_AGENTS_FILE.
function Invoke-Setup2 { $o = @(& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tool2 'setup.ps1') -Repo $repo2 -Root $root2 @args 2>&1 | ForEach-Object { "$_" }); $script:setupExit = $LASTEXITCODE; return $o }

$o = Invoke-Setup2 -Name $pool2 -NoClaude -NoCodex -NoSchedule
$c = Get-Cfg $root2
Check ($setupExit -eq 0 -and (Get-Steps $o) -eq 'Checking prerequisites | Base worktree | Pool config | Done') "a first run with -NoClaude -NoCodex -NoSchedule prints only the steps it does: $(Get-Steps $o)"
Check ((Get-Bytes $sf2) -ceq $sf2Orig -and (Get-Bytes $codexEnv) -ceq $codexEnvOrig -and -not (Test-Path (Join-Path $base2 '.claude')) -and -not (Get-Task $pool2)) '... and adds no hooks, no Codex block and no task: the settings.local.json and the Codex file that were there are untouched'
Check ($c.integrations.claude -eq $false -and $c.integrations.codex -eq $false -and (Get-Content (Join-Path $root2 'cow-config.json') -Raw) -match '"scheduleAt":\s*null' -and $c.integrationRevision -eq 1 -and $c.baseRef -eq 'main') 'integrations: claude false, codex false, scheduleAt null'

$o = Invoke-Setup2 -BaseRef stable -BranchPrefix 'sp-' -PruneDays 3 -FallbackMinFreeGB 50 -ScheduleAt '6:30'
$c = Get-Cfg $root2
$s2 = Get-Content (Join-Path $repo2 '.claude\settings.local.json') -Raw | ConvertFrom-Json
Check ($setupExit -eq 0 -and (Get-Steps $o) -eq $allSteps) "the next run, without the switches, adds all three: $(Get-Steps $o)"
Check (($s2.hooks | ConvertTo-Json -Depth 20 -Compress) -ceq (Get-WantedHooks $scripts2 $root2)) 'the hook entries name the scripts in the folder with the space'
Check ((Test-Block $codexEnv $pool2) -and -not (Test-Block $codexFile $pool2)) 'without -CodexAgentsFile the block goes to the file COW_CODEX_AGENTS_FILE names'
Check ((Test-Odd $sf2) -and (Test-Bom $sf2) -and (Test-Odd $codexEnv) -and (Test-Bom $codexEnv) -and (Test-StartsWith $codexEnv $codexEnvOrig)) 'files with a byte order mark keep it and their text outside ASCII; the Codex file starts with exactly the bytes it had'
Check ($c.name -eq $pool2 -and $c.integrations.scheduleAt -eq '06:30' -and (Get-TaskAt $pool2) -eq '06:30') 'the first run''s name is kept; -ScheduleAt 6:30 is recorded as 06:30, and the task is registered for it'

# The commands exactly as they are stored, run the way Claude Code runs a hook: the program and its argument list.
$env:CLAUDE_PROJECT_DIR = $repo2
$h = $s2.hooks.WorktreeCreate[0].hooks[0]
$hookOut = '{"hook_event_name":"WorktreeCreate","cwd":"' + $repo2.Replace('\', '\\') + '","name":"hooked2"}' | & $h.command @($h.args) 2>$null
$hooked2 = @($hookOut | Where-Object { "$_".Trim() }) | Select-Object -Last 1
Check ($hooked2 -eq (Join-Path $root2 'hooked2') -and (git -C $hooked2 symbolic-ref --short HEAD) -eq 'sp-hooked2') 'the stored WorktreeCreate command runs: a clone on sp-hooked2 (the pool''s own branch prefix)'
$h = $s2.hooks.WorktreeRemove[0].hooks[0]
'{"hook_event_name":"WorktreeRemove","worktree_path":"' + "$hooked2".Replace('\', '\\') + '"}' | & $h.command @($h.args) 2>$null | Out-Null
git -C $repo2 show-ref --verify --quiet refs/heads/sp-hooked2
Check ($hooked2 -and -not (Test-Path $hooked2) -and $LASTEXITCODE -ne 0) 'the stored WorktreeRemove command runs: worktree and unused branch gone'
$h = $s2.hooks.PreToolUse[0].hooks[0]
$deny = '{"tool_name":"Bash","tool_input":{"command":"git worktree add ../x HEAD"}}' | & $h.command @($h.args)
Check (($deny -join '') -match '"deny"' -and ($deny -join '') -match 'cow tool') 'the stored guard command runs, and names the script in that folder'
Remove-Item Env:CLAUDE_PROJECT_DIR

# The task's action, started by Task Scheduler: its own process, not a child of this test. What it runs moves the
# base to the base ref's new commit, which is the proof that the command line with the space in it works.
# /I starts it whatever its conditions say: the task waits for an idle computer, and Start-ScheduledTask only
# queues it until then.
Set-Content (Join-Path $repo2 'second.txt') 'two'; Quiet { git -C $repo2 add -A }; Quiet { git -C $repo2 commit -q -m 'second' }
Quiet { git -C $repo2 branch -f stable main }
$want2 = git -C $repo2 rev-parse stable
Check ((git -C $base2 rev-parse HEAD) -ne $want2 -and -not (Test-Path (Join-Path $root2 'refresh.log'))) 'before the task runs: the base is one commit behind its base ref, and there is no refresh.log'
schtasks.exe /Run /I /TN "\CowWorktrees\Refresh-$pool2" *> $null
$deadline = (Get-Date).AddSeconds(120)
while ((git -C $base2 rev-parse HEAD) -ne $want2 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
while ((Get-Task $pool2).State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Check ((git -C $base2 rev-parse HEAD) -eq $want2) 'the scheduled task ran its script: the base moved to the base ref''s new commit'
Check (Test-Path (Join-Path $root2 'refresh.log')) 'refresh.log is there'

# The time changed outside setup (register_refresh_task.ps1 by hand; Task Scheduler's own window does the same).
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts2 'register_refresh_task.ps1') -Root $root2 -At '07:45' *> $null
# ... and cow-config.json saved by an editor that drops the byte order mark.
$cfg2 = Join-Path $root2 'cow-config.json'
[IO.File]::WriteAllText($cfg2, [IO.File]::ReadAllText($cfg2), $utf8)
$o = Invoke-Setup2 -NoCodex -NoClaude
$c = Get-Cfg $root2
Check ($setupExit -eq 0 -and $c.name -eq $pool2 -and $c.baseRef -eq 'stable' -and $c.branchPrefix -eq 'sp-' -and $c.pruneDays -eq 3 -and $c.fallbackMinFreeGB -eq 50) 'a re-run that passes none of them keeps the name, base ref, branch prefix, prune days and fallback size'
Check ((Get-Bytes $codexEnv) -ceq $codexEnvOrig -and (Get-OurHooks $repo2) -eq 0 -and (Get-OurHooks $base2) -eq 0) 'that run had -NoCodex and -NoClaude: the hooks are out, and the Codex file is byte for byte what it was before setup (byte order mark, CRLF)'
Check ((Test-Odd $sf2) -and (Test-Bom $sf2)) '... and settings.local.json keeps its byte order mark and its text outside ASCII'
Check (-not (Test-Bom $cfg2)) 'a cow-config.json without a byte order mark stays without one'
Check ($c.integrations.scheduleAt -eq '07:45' -and (Get-TaskAt $pool2) -eq '07:45') '... and the time the task was registered for (07:45, set by hand; the config said 06:30)'
Check ("$((Get-Task $pool2).Actions[0].Arguments)" -match '-Prune -OlderThanDays 3 ') 'the task prunes after the kept 3 days'

# A cow-config.json as 1.0.x wrote it has neither of the two new entries.
function Remove-NewEntries { $c = Get-Cfg $root2; 'integrations', 'integrationRevision' | ForEach-Object { $c.PSObject.Properties.Remove($_) }; ConvertTo-Json -InputObject $c -Depth 5 | Set-Content (Join-Path $root2 'cow-config.json') -Encoding UTF8 }
Remove-NewEntries
Check (-not ((Get-Content (Join-Path $root2 'cow-config.json') -Raw) -match 'integration') -and (Get-Cfg $root2).name -eq $pool2) 'a cow-config.json in the 1.0.x shape: no integrations, no integrationRevision'
$old1 = & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts2 'cow_worktree.ps1') -Root $root2 -Name old1 2>$null | Select-Object -Last 1
Check ($LASTEXITCODE -eq 0 -and $old1 -eq (Join-Path $root2 'old1') -and (Get-OurHooks $old1) -eq 0) 'cow_worktree.ps1 makes a clone with that config (without hooks: they are switched off)'
# Someone else's hooks in the same file, next to the setting that is there: they must outlive a re-run and uninstall.
$s = [IO.File]::ReadAllText($sf2) | ConvertFrom-Json
$s | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{
    PreToolUse = @([pscustomobject]@{ matcher = 'Bash'; hooks = @([pscustomobject]@{ type = 'command'; command = 'echo not-ours' }) })
    Stop       = @([pscustomobject]@{ hooks = @([pscustomobject]@{ type = 'command'; command = 'echo not-ours-either' }) }) })
[IO.File]::WriteAllText($sf2, (ConvertTo-Json -InputObject $s -Depth 20), $utf8Bom)
function Test-Foreign {
    $s = [IO.File]::ReadAllText($sf2) | ConvertFrom-Json
    return ($s.permissions.allow -contains "Read($odd)" -and @($s.hooks.PreToolUse | Where-Object { $_.hooks.command -eq 'echo not-ours' }).Count -eq 1 -and @($s.hooks.Stop | Where-Object { $_.hooks.command -eq 'echo not-ours-either' }).Count -eq 1)
}
$o = Invoke-Setup2
$c = Get-Cfg $root2
Check ($setupExit -eq 0 -and $c.integrationRevision -eq 1 -and $c.integrations.claude -eq $true -and $c.integrations.codex -eq $true) 'setup again on the 1.0.x config works, and writes the two entries'
Check ($c.integrations.scheduleAt -eq '07:45' -and (Get-TaskAt $pool2) -eq '07:45' -and $c.branchPrefix -eq 'sp-' -and $c.pruneDays -eq 3) '... with the time taken from the registered task (a 1.0.x config has no record of it), and the other values kept'
Check ((Get-OurHooks $repo2) -eq 4 -and (Test-Foreign) -and (Test-Bom $sf2)) 'the hooks are back next to the other hooks and settings in the file, which stay; so does its byte order mark'
Check ((Get-OurHooks $old1) -eq 4 -and (Get-OurHooks $base2) -eq 4) 'the worktree made while the hooks were off gets them too'
Check ((Test-Bom $cfg2) -and (Test-Block $codexEnv $pool2) -and (Test-Bom $codexEnv) -and (Test-Odd $codexEnv)) 'cow-config.json keeps the byte order mark the 1.0.x file had; the Codex file has its block again and keeps its mark and text'

& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts2 'cow_worktree.ps1') -Root $root2 -Remove -Name old1 *> $null
Remove-NewEntries
$o = @(& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tool2 'uninstall.ps1') -Root $root2 -RemovePool 2>&1 | ForEach-Object { "$_" })
$unExit = $LASTEXITCODE
if ($unExit) { "  note  uninstall of the second pool failed:"; $o | Select-Object -Last 6 }
Check ($unExit -eq 0 -and -not (Test-Path $root2)) 'uninstall.ps1 -RemovePool works on the 1.0.x config: the pool root is gone'
Check ((Get-Steps $o) -eq "Claude Code hooks for pool '$pool2' | Codex instructions | Scheduled task | Pool registry | Base and pool files | Done") "the step titles of uninstall: $(Get-Steps $o)"
Check (-not (Get-Task $pool2)) 'its task is gone'
Check (-not (Test-Block $codexEnv $pool2) -and (Test-Block $codexFile $pool1)) 'its Codex block is gone; the first pool''s block, in the other file, is untouched'
Check ((Get-OurHooks $repo2) -eq 0 -and (Test-Foreign) -and (Test-Bom $sf2)) 'its hooks are gone; the other hooks and settings in the file stay (text outside ASCII included), and its byte order mark'
Check ((Get-Bytes $codexEnv) -ceq $codexEnvOrig) 'the Codex file is byte for byte what it was before setup'
Check ((@((Get-Content (Join-Path $env:COW_WORKTREES_HOME 'pools.json') -Raw | ConvertFrom-Json).pools | ForEach-Object { $_.name }) -join '|') -eq $pool1) 'the pool registry lists only the first pool again'
# Whatever happened above, this section's task and tool folder do not stay.
Unregister-ScheduledTask -TaskPath '\CowWorktrees\' -TaskName "Refresh-$pool2" -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $tool2 -Recurse -Force -ErrorAction SilentlyContinue
Check (-not (Test-Path $tool2)) 'the copied tool folder is removed'

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
Section 'a base whose first checkout was cut off'
# What a stopped or crashed first setup leaves: the base is registered, some of its files are there, and Git's
# own lock from making the worktree ("initializing") was never taken off. Setup must make the base again.
$cutRepo = Join-Path $work 'cut'; $cutRoot = "${Drive}:\cow-e2e-$stamp-cut"; $cutBase = Join-Path $cutRoot 'base'
[IO.Directory]::CreateDirectory($cutRepo) | Out-Null
Quiet { git -C $cutRepo init -q -b main }
git -C $cutRepo config user.email 'e2e@example.invalid'; git -C $cutRepo config user.name 'cow-worktrees e2e'
foreach ($n in 1..5) { Set-Content (Join-Path $cutRepo "f$n.txt") "file $n" }
Quiet { git -C $cutRepo add -A }; Quiet { git -C $cutRepo commit -q -m 'first' }
Quiet { git -C $cutRepo worktree add --detach $cutBase main }
Remove-Item (Join-Path $cutBase 'f4.txt'), (Join-Path $cutBase 'f5.txt') -Force
$cutAdmin = git -C $cutBase rev-parse --path-format=absolute --git-dir
[IO.File]::WriteAllText((Join-Path $cutAdmin 'locked'), 'initializing')
Check (@(git -C $cutRepo worktree list --porcelain) -contains 'locked initializing') 'the cut-off base is registered and still locked by Git ("initializing")'
$o = @(& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'setup.ps1') -Repo $cutRepo -Root $cutRoot -Name "e2e-$stamp-cut" -NoClaude -NoCodex -NoSchedule 2>&1 | ForEach-Object { "$_" })
if ($LASTEXITCODE) { '  note  setup output:'; $o }
Check ($LASTEXITCODE -eq 0 -and ($o -join ' ') -match 'removing the partial copy') 'setup removes the partial base and makes it again'
Check ((Test-Path (Join-Path $cutBase 'f5.txt')) -and @(git -C $cutBase status --porcelain).Count -eq 0) 'the new base is complete and clean'
Check (@(git -C $cutRepo worktree list --porcelain | Where-Object { $_ -like 'locked*' }) -notcontains 'locked initializing') 'and it carries the tool''s own lock now, not Git''s'
& $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'uninstall.ps1') -Root $cutRoot -RemovePool *> $null
Check (-not (Test-Path $cutRoot)) 'that pool is removed again'

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
Check ((Get-Bytes $codexFile) -ceq $codexOrig) 'after uninstall the Codex file is byte for byte what it was before setup (no byte order mark, LF)'
Check ((Test-Odd $settingsFile) -and -not (Test-Bom $settingsFile)) '... and settings.local.json has its text outside ASCII, and still no byte order mark'
$realCodex = Join-Path $env:USERPROFILE '.codex\AGENTS.md'
$realPools = Join-Path $env:LOCALAPPDATA 'cow-worktrees\pools.json'
Check (-not ((Test-Path $realCodex) -and "$(Get-Content $realCodex -Raw)" -match "cow-worktrees:(e2e|sp)-$stamp ")) 'nothing of this run is in the real ~\.codex\AGENTS.md'
Check (-not ((Test-Path $realPools) -and "$(Get-Content $realPools -Raw)" -match "cow-e2e-$stamp")) '... or in the real pools.json'
Check (@(Get-ScheduledTask -TaskPath '\CowWorktrees\' -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "*$stamp" }).Count -eq 0) 'no scheduled task of this run is left'
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
"$checks checks, $failures failed"
exit $failures
