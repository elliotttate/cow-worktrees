#Requires -Version 5.1
<#
cc_worktree_hook.ps1 - Claude Code WorktreeCreate / WorktreeRemove hook (installed by setup.ps1).

  create  stdin {"name": ..., "cwd": ...}  -> stdout: the new worktree's absolute path (last line)
  remove  stdin {"worktree_path": ...}     -> deletes the worktree; exit 0 once it is gone

Used by `claude --worktree`, the EnterWorktree tool, subagents with isolation "worktree", background
sessions, workflow agents and Claude Desktop's worktree sessions.

create: makes <root>\<name> on branch <branchPrefix><name>, starting at the HEAD of the session's
checkout, as a block clone of the base (cow_worktree.ps1). Claude Code passes the main checkout as
"cwd" even for a session in a linked worktree, so the session's checkout is taken from
CLAUDE_PROJECT_DIR. If the copy-on-write route is unavailable it falls back to a plain
`git worktree add`, but only when the drive has fallbackMinFreeGB free.

remove: `git worktree remove --force` (Claude Code asks for removal only after the user chose it, or
for a clean worktree, as its own default does). The <branchPrefix><name> branch is deleted only when it
still points at the commit the worktree started from, so no commit is ever lost.
Every call is logged to <root>\hook.log.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('create', 'remove')][string]$Event,
    [string]$Root
)
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
$Cow = Join-Path $PSScriptRoot 'cow_worktree.ps1'
function Say([string]$msg) { [Console]::Error.WriteLine("cow-worktrees: $msg") }
. (Join-Path $PSScriptRoot 'CowCommon.ps1')
$raw = [Console]::In.ReadToEnd()
$payload = $raw | ConvertFrom-Json

# The session's own checkout (see above), then the pool it belongs to.
$session = if ($env:CLAUDE_PROJECT_DIR -and (Test-Path -LiteralPath $env:CLAUDE_PROJECT_DIR)) { $env:CLAUDE_PROJECT_DIR }
           elseif ($payload.cwd) { $payload.cwd } else { (Get-Location).Path }
$Config = $null
try { $Root = Resolve-CowRoot $Root $session; $Config = Get-CowConfig $Root } catch { $poolError = $_.Exception.Message }

# One line per call, for working out what Claude Code passed when something looks wrong.
$logFile = if ($Root -and (Test-Path -LiteralPath $Root)) { Join-Path $Root 'hook.log' } else { Join-Path $env:TEMP 'cow-worktrees-hook.log' }
try {
    # Values only for variables that cannot be secrets; names for the rest.
    $claudeEnv = (Get-ChildItem Env: | Where-Object Name -like 'CLAUDE*' | ForEach-Object {
        if ($_.Name -match 'TOKEN|KEY|SECRET|AUTH|PASS') { $_.Name } else { "$($_.Name)=$($_.Value)" } }) -join ' '
    Add-Content -LiteralPath $logFile -Value ('{0} {1} pwd={2} env=[{3}] input={4}' -f (Get-Date -Format s), $Event, (Get-Location).Path, $claudeEnv, ($raw -replace '\s+', ' '))
} catch { }

if ($Event -eq 'create') {
    $name = ($payload.name -replace '[^A-Za-z0-9._-]', '-').Trim('-.')
    if (-not $name) { Say 'no worktree name in the hook input'; exit 1 }
    $top = git -C $session rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -or -not $top) { Say "$session is not inside a Git checkout"; exit 1 }
    $from = git -C $top rev-parse HEAD
    $prefix = if ($Config) { [string]$Config.branchPrefix } else { 'worktree-' }
    $branch = "$prefix$name"

    # The copy-on-write route needs this repository's pool and its registered base worktree.
    $baseRegistered = $Config -and (@(git -C $top worktree list --porcelain) -contains "worktree $($Root.Replace('\', '/'))/base")
    if ($baseRegistered) {
        $out = & $Cow -Name $name -Branch $branch -From $from -Repo $top -Root $Root
        $cowExit = $LASTEXITCODE
        $path = @($out | Where-Object { $_ -and "$_".Trim() }) | Select-Object -Last 1
        if ($cowExit -eq 0 -and $path) { Write-Output $path; exit 0 }
        if (Test-Path -LiteralPath (Join-Path $Root $name)) { Say "copy-on-write worktree failed partway; left at $Root\$name for a look"; exit 1 }
        Say 'copy-on-write route failed; falling back to a plain git worktree add'
    } elseif ($poolError) {
        Say "no copy-on-write pool for this repository ($poolError); falling back to a plain git worktree add"
    }

    $parent = if ($Root -and (Test-Path -LiteralPath $Root)) { $Root } else { Join-Path $top '.claude\worktrees' }
    $path = Join-Path $parent $name
    # A plain checkout costs the repository's full size: refuse rather than nearly fill the disk.
    $minFree = if ($Config) { [int]$Config.fallbackMinFreeGB } else { 120 }
    $drive = (Split-Path -Qualifier ([IO.Path]::GetFullPath($parent))).TrimEnd(':')
    $freeGB = [math]::Floor((Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue).SizeRemaining / 1GB)
    if ($freeGB -lt $minFree) {
        Say "refusing a plain checkout at $path`: only $freeGB GB free on ${drive}: (needs $minFree). Fix the copy-on-write route instead: is $Root\base present and clean? See $logFile."
        exit 1
    }
    $out = git -C $top worktree add -b $branch $path $from 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { Say "git worktree add failed:`n$($out -join "`n")"; exit 1 }
    git -C $path config --worktree cow.startCommit $from 2>$null
    Write-Output $path
    exit 0
}

# remove
$path = $payload.worktree_path
if (-not $path -or -not (Test-Path -LiteralPath $path)) { exit 0 }
$common = git -C $path rev-parse --path-format=absolute --git-common-dir 2>$null
if ($LASTEXITCODE -or -not $common) { Say "$path is not a Git worktree; leaving it alone"; exit 1 }
$main = Split-Path -Parent $common
$branch = git -C $path symbolic-ref --quiet --short HEAD 2>$null
$tip = git -C $path rev-parse HEAD 2>$null
$start = git -C $path config --worktree --get cow.startCommit 2>$null
$prefix = if ($Config) { [string]$Config.branchPrefix } else { 'worktree-' }

$out = git -C $main worktree remove --force $path 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -or (Test-Path -LiteralPath $path)) { Say "could not remove $path (files in use?):`n$($out -join "`n")"; exit 1 }

if ($branch -and $branch.StartsWith($prefix)) {
    if ($start -and $tip -eq $start) {
        git -C $main branch -D $branch 2>&1 | Out-Null
        Say "removed $path and its unused branch $branch"
    } else {
        Say "removed $path; kept branch $branch (it has commits)"
    }
} else {
    Say "removed $path"
}
exit 0
