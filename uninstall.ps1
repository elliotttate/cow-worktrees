#Requires -Version 5.1
<#
.SYNOPSIS
  Undo setup.ps1 for one pool: Claude Code hooks, Codex block, scheduled task and registry entry.

.EXAMPLE
  .\uninstall.ps1 -Root E:\cow\MyGame
  .\uninstall.ps1 -Root E:\cow\MyGame -RemovePool

.DESCRIPTION
  Removes only what setup.ps1 added: this tool's entries in .claude\settings.local.json of every checkout
  (other hooks and settings stay), the marked block in ~\.codex\AGENTS.md, the task
  \CowWorktrees\Refresh-<name>, and the pool's line in pools.json.
  -RemovePool also removes the base worktree and the pool's own files (cow-config.json, logs, locks),
  but only when no other worktrees are left under the root: remove or migrate those first.
  Worktrees, branches and extensions.worktreeConfig are left alone.

  -CodexAgentsFile: the Codex instructions file. Default: the COW_CODEX_AGENTS_FILE environment variable
  when set, else ~\.codex\AGENTS.md.

  The files it changes keep their encoding and everything in them that is not this tool's (see
  scripts\CowIntegrations.ps1).

.NOTES
  Step titles: the lines that start with "==> ". The app shows them as a checklist, so they are an
  interface: do not reword one without changing the app, and print a step only when it does something.
  In this order:
    Claude Code hooks for pool '<name>'   when a checkout has hooks of this tool
    Codex instructions                    when the pool's block is in the file
    Scheduled task                        when the pool's task is registered
    Pool registry                         when the pool is in pools.json
    Base and pool files                   with -RemovePool
    Done                                  always (not reached on a failure: the script exits 1 with "uninstall: <why>")
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Root,
    [switch]$RemovePool,
    [string]$CodexAgentsFile
)
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
$scripts = Join-Path $PSScriptRoot 'scripts'
. (Join-Path $scripts 'CowCommon.ps1')
. (Join-Path $scripts 'CowIntegrations.ps1')
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note([string]$msg) { Write-Host "    $msg" }
function Fail([string]$msg) { Write-Host "uninstall: $msg" -ForegroundColor Red; exit 1 }
$CodexAgentsFile = Resolve-CowCodexFile $CodexAgentsFile

try { $Root = Resolve-CowRoot $Root (Get-Location).Path; $Config = Get-CowConfig $Root } catch { Fail $_.Exception.Message }
$main = $Config.repo
$base = Join-Path $Root 'base'

# Each piece only when it is there (a pool set up with -NoClaude, -NoCodex or -NoSchedule has none of it).
$withHooks = @(Get-CowCheckouts $main | Where-Object { Test-CowClaudeHooks $_ })
if ($withHooks.Count) {
    Step "Claude Code hooks for pool '$($Config.name)'"
    foreach ($c in $withHooks) { Remove-CowClaudeHooks $c | ForEach-Object { Note $_ } }
}

if (Test-CowCodexBlock $CodexAgentsFile $Config.name) {
    Step 'Codex instructions'
    Remove-CowCodexBlock $CodexAgentsFile $Config.name | ForEach-Object { Note $_ }
}

if (Get-ScheduledTask -TaskPath $CowTaskPath -TaskName (Get-CowTaskName $Config.name) -ErrorAction SilentlyContinue) {
    Step 'Scheduled task'
    & (Join-Path $scripts 'register_refresh_task.ps1') -Root $Root -Unregister | ForEach-Object { Note $_ }
}

$registry = Read-CowRegistry
if (@($registry.Pools.pools | Where-Object { (ConvertTo-CowFullPath $_.root) -eq $Root }).Count) {
    Step 'Pool registry'
    $registry.Pools.pools = @($registry.Pools.pools | Where-Object { (ConvertTo-CowFullPath $_.root) -ne $Root })
    Write-CowRegistry $registry
    Note "removed from $(Get-CowPoolsFile)"
}

if ($RemovePool) {
    Step 'Base and pool files'
    $others = @(git -C $main worktree list --porcelain | Where-Object { $_ -like 'worktree *' } | ForEach-Object { ConvertTo-CowFullPath $_.Substring(9) } |
        Where-Object { $_.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -and $_ -ne $base })
    if ($others.Count) { Fail "these worktrees are still in the pool; remove or migrate them first:`n  $($others -join "`n  ")" }
    git -C $main worktree unlock $base 2>$null
    git -C $main worktree remove --force $base
    if ($LASTEXITCODE -or (Test-Path -LiteralPath $base)) { Fail "could not remove $base (in use?)" }
    foreach ($f in 'cow-config.json', 'base-build.json', 'base-build-state.txt', 'hook.log', 'refresh.log', 'base-build.log') { Remove-Item -LiteralPath (Join-Path $Root $f) -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath (Join-Path $Root '.locks') -Recurse -Force -ErrorAction SilentlyContinue
    if (-not @(Get-ChildItem -LiteralPath $Root -Force).Count) { Remove-Item -LiteralPath $Root -Force }
    Note "removed $base and the pool's files"
}
Step 'Done'
