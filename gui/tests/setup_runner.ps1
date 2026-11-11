#Requires -Version 5.1
<#
setup_runner.ps1 - checks that the app sets a repository up, and removes it again, the way the walkthrough does.

  setup_runner.ps1 [-Exe <CowWorktrees.exe>] [-Drive E]

Runs the published app hidden with --run-setup, which goes through the same code as the walkthrough's "Set up"
(SetupRunner and MainViewModel.StartSetup, which run setup.ps1) and writes the outcome to a file. On a throwaway
repository whose path has a space in it and a throwaway folder on the Dev Drive it checks:
  1. setup: the registry, the locked base, the Claude Code hooks, the Codex section and the daily task;
  2. a setup on a drive that is not ReFS fails, and the app explains why in plain words;
  3. a second run with Codex and the daily task switched off removes exactly those;
  4. removal through the app (uninstall.ps1 with -RemovePool) leaves nothing behind.
COW_WORKTREES_HOME and COW_CODEX_AGENTS_FILE point at temporary folders, so nothing of yours is touched; the
scheduled task this test registers is removed again. Exit code = number of failed checks.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe'),
    [string]$Drive
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$failures = 0; $checks = 0
function Check([bool]$ok, [string]$what) { $script:checks++; if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ } }
if (-not $Drive) { $Drive = (Get-Volume | Where-Object { $_.FileSystem -eq 'ReFS' -and $_.DriveLetter } | Select-Object -First 1).DriveLetter }
if (-not $Drive) { 'no ReFS volume found; pass -Drive'; exit 1 }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP "cow-guisetup-$stamp"
$repo = Join-Path $work 'my repo'
$root = "${Drive}:\cow-guisetup-$stamp\my-repo"
$name = "guisetup-$stamp"
$env:COW_WORKTREES_HOME = Join-Path $work 'home'
$env:COW_CODEX_AGENTS_FILE = Join-Path $work 'codex\AGENTS.md'
$env:COW_NO_UPDATE_CHECK = '1'
$taskPath = '\CowWorktrees\'; $taskName = "Refresh-$name"
"app $Exe; repository $repo; folder $root"

function Invoke-App([hashtable]$request) {
    $script:run++
    $file = Join-Path $work "run$script:run.json"
    $request.result = Join-Path $work "run$script:run.result.json"
    $request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $file -Encoding UTF8
    $p = Start-Process -FilePath $Exe -ArgumentList @('--hidden', '--run-setup', $file) -PassThru
    if (-not $p.WaitForExit(600000)) { Stop-Process -Id $p.Id -Force; throw 'the app did not finish within 10 minutes' }
    return (Get-Content -LiteralPath $request.result -Raw | ConvertFrom-Json)
}
function Hooks([string]$checkout) {
    $f = Join-Path $checkout '.claude\settings.local.json'
    return ((Test-Path -LiteralPath $f) -and ((Get-Content -LiteralPath $f -Raw) -match 'cc_worktree_hook\.ps1'))
}
function CodexBlock { return ((Test-Path -LiteralPath $env:COW_CODEX_AGENTS_FILE) -and ((Get-Content -LiteralPath $env:COW_CODEX_AGENTS_FILE -Raw) -match [regex]::Escape("cow-worktrees:$name begin"))) }
function Task { return [bool](Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue) }

$run = 0
try {
    New-Item -ItemType Directory -Force -Path $repo, $env:COW_WORKTREES_HOME | Out-Null
    git -C $repo init -q -b main 2>&1 | Out-Null
    git -C $repo config user.email 'guisetup@example.invalid'; git -C $repo config user.name 'gui setup test'
    Set-Content (Join-Path $repo 'README.md') 'test'
    git -C $repo add -A 2>&1 | Out-Null; git -C $repo commit -q -m 'first' 2>&1 | Out-Null

    '== set up through the app'
    $r = Invoke-App @{ setup = @{ repo = $repo; root = $root; name = $name; claude = $true; codex = $true; schedule = $true; scheduleAt = '04:30' } }
    if (-not $r.succeeded) { "  note  output:"; $r.lines | ForEach-Object { "        $_" } }
    Check ($r.succeeded -and $r.exitCode -eq 0) 'setup.ps1 succeeded'
    Check (($r.steps | Where-Object { $_.State -ne 'Done' }).Count -eq 0 -and ($r.steps.Key -contains 'Base worktree') -and ($r.steps.Key -contains 'Claude Code hooks') -and ($r.steps.Key -contains 'Daily upkeep task')) "every step is ticked: $(($r.steps.Label) -join '; ')"
    $pools = Get-Content (Join-Path $env:COW_WORKTREES_HOME 'pools.json') -Raw | ConvertFrom-Json
    Check (@($pools.pools | Where-Object { $_.name -eq $name }).Count -eq 1) 'the repository is in pools.json'
    Check ($r.selectedPool -eq $name) 'the app selected the new repository'
    Check ((Test-Path (Join-Path $root 'base\README.md')) -and (@(git -C $repo worktree list --porcelain) -match '^locked')) 'the base copy exists and is locked'
    Check ((Hooks $repo) -and (Hooks (Join-Path $root 'base'))) 'the Claude Code hooks are in the repository and in the base'
    Check (CodexBlock) 'the Codex section is in the instructions file'
    Check (Task) 'the daily task is registered'
    $at = (Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue).Triggers[0].StartBoundary
    Check ($at -match 'T04:30') "the daily task starts at 04:30 ($at)"
    Check ($r.integrations.claude -eq 'On' -and $r.integrations.codex -eq 'On' -and $r.integrations.schedule -eq 'On' -and -not $r.integrations.drift) "the app sees all three as connected: $($r.integrations | ConvertTo-Json -Compress)"
    Check ($r.integrations.scheduleAt -eq '04:30') 'the app reads the task''s time'

    '== a drive that is not a Dev Drive'
    $ntfs = Join-Path $work 'not-refs\pool'
    $r2 = Invoke-App @{ setup = @{ repo = $repo; root = $ntfs; name = "$name-b"; claude = $false; codex = $false; schedule = $false } }
    Check (-not $r2.succeeded) 'setup fails'
    Check ($r2.friendlyKnown -and $r2.friendlyTitle -match 'copy-on-write') "the app explains it: $($r2.friendlyTitle)"
    Check ($r2.raw -match 'ReFS') 'the script''s own message is kept'
    Check (-not (Test-Path (Join-Path $ntfs 'base'))) 'nothing was made'

    '== switch Codex and the daily task off'
    $r3 = Invoke-App @{ setup = @{ repo = $repo; root = $root; name = $name; claude = $true; codex = $false; schedule = $false } }
    Check ($r3.succeeded) 'the second run succeeded'
    Check ((Hooks $repo) -and -not (CodexBlock) -and -not (Task)) 'Claude Code stays connected; the Codex section and the task are gone'
    Check ($r3.integrations.claude -eq 'On' -and $r3.integrations.codex -eq 'Off' -and $r3.integrations.schedule -eq 'Off' -and -not $r3.integrations.drift) "the app sees it, and nothing counts as drift: $($r3.integrations | ConvertTo-Json -Compress)"

    '== move a full worktree into a copy-on-write clone'
    $plain = Join-Path $work 'plain wt'
    git -C $repo worktree add -q -b plainbr $plain main 2>&1 | Out-Null
    Set-Content (Join-Path $plain 'untracked.txt') 'carry me'
    Add-Content (Join-Path $plain 'README.md') 'edited in the old worktree'
    $r5 = Invoke-App @{ migrate = @{ from = $plain; name = 'moved'; retire = $true } }
    $ops = @($r5.operations)
    if (@($ops | Where-Object { -not $_.Succeeded }).Count -or $ops.Count -ne 2) { "  note  operations: $($ops | ConvertTo-Json -Compress)"; $r5.lines | ForEach-Object { "        $_" } }
    Check ($ops.Count -eq 2 -and $ops[0].Kind -eq 'Migrate' -and $ops[1].Kind -eq 'Retire' -and @($ops | Where-Object { $_.Succeeded }).Count -eq 2) 'the copy ran, then the retiring of the old folder, and both succeeded'
    $moved = Join-Path $root 'moved'
    Check ((Get-Item -LiteralPath $plain -Force).LinkType -eq 'Junction') 'the old path is a junction now'
    Check ((Test-Path (Join-Path $moved 'untracked.txt')) -and ((Get-Content (Join-Path $moved 'untracked.txt')) -eq 'carry me')) 'the untracked file was carried over'
    Check (((Get-Content (Join-Path $moved 'README.md'))[-1]) -eq 'edited in the old worktree') 'the edited file was carried over'
    Check ((git -C $moved symbolic-ref --short HEAD) -eq 'plainbr') 'the clone is on the old worktree''s branch'
    # this test's own throwaway: take it out again so the base copy can be deleted below
    [IO.Directory]::Delete($plain, $false)
    git -C $repo worktree unlock $moved 2>$null; git -C $repo worktree remove --force $moved 2>$null

    '== a create that is stopped leaves nothing behind'
    $r6 = Invoke-App @{ create = @{ name = 'stopped'; stopAfterMs = 900 } }
    $stopped = Join-Path $root 'stopped'
    if ($r6.cancelled) {
        Check ($r6.operations[0].last -match 'Stopped') "the app says what it did: $($r6.operations[0].last)"
        Check (-not (@(git -C $repo worktree list --porcelain) -contains ('worktree ' + $stopped.Replace('\', '/'))) -and -not (Test-Path $stopped)) 'the partly made worktree is gone from Git and from disk'
    } else {
        "  note  the create finished before it could be stopped (not counted): $($r6.operations[0].last)"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\..\scripts\cow_worktree.ps1') -Root $root -Remove -Name stopped *> $null
    }
    Check (@(git -C (Join-Path $root 'base') status --porcelain --untracked-files=no).Count -eq 0) 'the base copy is still clean'

    '== remove through the app'
    $r4 = Invoke-App @{ uninstall = @{ root = $root; removePool = $true } }
    if (-not $r4.succeeded) { "  note  output:"; $r4.lines | ForEach-Object { "        $_" } }
    Check ($r4.succeeded) 'uninstall.ps1 succeeded'
    $pools = Get-Content (Join-Path $env:COW_WORKTREES_HOME 'pools.json') -Raw | ConvertFrom-Json
    Check (@($pools.pools | Where-Object { $_.name -eq $name }).Count -eq 0) 'the repository left pools.json'
    Check (-not (Hooks $repo) -and -not (CodexBlock) -and -not (Task)) 'hooks, Codex section and task are gone'
    Check (-not (Test-Path $root)) 'the base copy and the folder are gone'
    Check (Test-Path (Join-Path $repo 'README.md')) 'the project itself is untouched'
} catch {
    $failures++
    "  FAIL  $($_.Exception.Message)"
} finally {
    Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskPath $taskPath -TaskName "Refresh-$name-b" -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $root 'base')) { git -C $repo worktree unlock (Join-Path $root 'base') 2>$null; git -C $repo worktree remove --force (Join-Path $root 'base') 2>$null }
    foreach ($d in "${Drive}:\cow-guisetup-$stamp", $work) { if (Test-Path -LiteralPath $d) { cmd /c rd /s /q "\\?\$d" 2>$null } }
    Remove-Item Env:\COW_WORKTREES_HOME, Env:\COW_CODEX_AGENTS_FILE, Env:\COW_NO_UPDATE_CHECK -ErrorAction SilentlyContinue
}
"$checks checks, $failures failed"
exit $failures
