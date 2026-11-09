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
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Root,
    [switch]$RemovePool,
    [string]$CodexAgentsFile = (Join-Path $env:USERPROFILE '.codex\AGENTS.md')
)
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
$scripts = Join-Path $PSScriptRoot 'scripts'
. (Join-Path $scripts 'CowCommon.ps1')
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note([string]$msg) { Write-Host "    $msg" }
function Fail([string]$msg) { Write-Host "uninstall: $msg" -ForegroundColor Red; exit 1 }

try { $Root = Resolve-CowRoot $Root (Get-Location).Path; $Config = Get-CowConfig $Root } catch { Fail $_.Exception.Message }
$main = $Config.repo
$base = Join-Path $Root 'base'

Step "Claude Code hooks for pool '$($Config.name)'"
$checkouts = @(git -C $main worktree list --porcelain | Where-Object { $_ -like 'worktree *' } | ForEach-Object { ConvertTo-CowFullPath $_.Substring(9) } | Where-Object { Test-Path -LiteralPath $_ })
foreach ($c in $checkouts) {
    $file = Join-Path $c '.claude\settings.local.json'
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $s = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if (-not $s.hooks) { continue }
    $changed = $false
    foreach ($p in @($s.hooks.PSObject.Properties)) {
        $kept = @($p.Value | Where-Object { $e = $_; -not (@($e.hooks) | Where-Object { (@($_.args) + @($_.command)) -match 'cc_(worktree|guard)_hook\.ps1' }) })
        if ($kept.Count -ne @($p.Value).Count) { $changed = $true }
        if ($kept.Count) { $p.Value = $kept } else { $s.hooks.PSObject.Properties.Remove($p.Name) }
    }
    if (-not @($s.hooks.PSObject.Properties).Count) { $s.PSObject.Properties.Remove('hooks') }
    if ($changed) { ConvertTo-Json -InputObject $s -Depth 20 | Set-Content -LiteralPath $file -Encoding UTF8; Note "cleaned $file" }
}

Step 'Codex instructions'
$begin = "<!-- cow-worktrees:$($Config.name) begin -->"; $end = "<!-- cow-worktrees:$($Config.name) end -->"
if (Test-Path -LiteralPath $CodexAgentsFile) {
    $text = Get-Content -LiteralPath $CodexAgentsFile -Raw
    $i = $text.IndexOf($begin); $j = $text.IndexOf($end)
    if ($i -ge 0 -and $j -gt $i) {
        $text = ($text.Substring(0, $i).TrimEnd() + "`r`n" + $text.Substring($j + $end.Length).TrimStart()).Trim()
        Set-Content -LiteralPath $CodexAgentsFile -Value $text -Encoding UTF8 -NoNewline
        Note "removed the block from $CodexAgentsFile"
    }
}

Step 'Scheduled task'
& (Join-Path $scripts 'register_refresh_task.ps1') -Root $Root -Unregister | ForEach-Object { Note $_ }

Step 'Pool registry'
$pools = Read-CowPools
$pools.pools = @($pools.pools | Where-Object { (ConvertTo-CowFullPath $_.root) -ne $Root })
Write-CowPools $pools
Note "removed from $(Get-CowPoolsFile)"

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
