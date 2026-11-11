#Requires -Version 5.1
<#
.SYNOPSIS
  Disconnect every registered pool: runs uninstall.ps1 (without -RemovePool) for each entry of pools.json.

.EXAMPLE
  .\scripts\disconnect_all.ps1

.DESCRIPTION
  The installer's uninstaller runs this before it removes the tool folder: the Claude Code hooks, the Codex
  block and the daily task of every pool point at scripts in that folder and would be left broken.
  For each pool in %LOCALAPPDATA%\cow-worktrees\pools.json (COW_WORKTREES_HOME overrides the folder) it runs
  `uninstall.ps1 -Root <root>`, so the hooks, the block, the task and the pool's line in pools.json go.
  Worktrees, bases, branches and cow-config.json stay: setup.ps1 connects the repository again later.
  A pool that fails (its folder is gone, its drive is not attached) is reported and skipped, and the rest
  are still done.
  -Log also appends the output to disconnect.log next to pools.json, because the uninstaller runs this hidden.
  -CodexAgentsFile is passed on to uninstall.ps1.
  Exit code = number of pools that could not be disconnected.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Log,
    [string]$CodexAgentsFile
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'CowCommon.ps1')
$uninstall = Join-Path (Split-Path -Parent $PSScriptRoot) 'uninstall.ps1'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:lines = New-Object System.Collections.Generic.List[string]
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan; $script:lines.Add("==> $msg") }
function Note([string]$msg) { Write-Host "    $msg"; $script:lines.Add("    $msg") }
# The log goes next to pools.json, and only when that folder is there: nothing is created just to say "nothing to do".
function Save-Log {
    if (-not $Log -or -not (Test-Path -LiteralPath (Get-CowHome))) { return }
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    try { Add-Content -LiteralPath (Join-Path (Get-CowHome) 'disconnect.log') -Value (@("---- $stamp disconnect_all.ps1") + $script:lines) -Encoding UTF8 } catch { }
}

try { $pools = @((Read-CowPools).pools | Where-Object { $_ -and $_.root }) }
catch { Step "Cannot read $(Get-CowPoolsFile)"; Note $_.Exception.Message; Save-Log; exit 1 }
if (-not $pools.Count) { Step "No pools are registered in $(Get-CowPoolsFile); nothing to disconnect"; Save-Log; exit 0 }
if (-not (Test-Path -LiteralPath $uninstall)) { Step "Cannot find $uninstall"; Save-Log; exit $pools.Count }

$failed = @()
foreach ($p in $pools) {
    Step "Pool '$($p.name)' ($($p.root))"
    # A process of its own for each pool: uninstall.ps1 ends with `exit`, and one pool's failure must not stop the rest.
    $uninstallArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $uninstall, '-Root', $p.root)
    if ($CodexAgentsFile) { $uninstallArgs += @('-CodexAgentsFile', $CodexAgentsFile) }
    & $ps @uninstallArgs 2>&1 | ForEach-Object { Note "$_" }
    $code = $LASTEXITCODE
    # uninstall.ps1 takes the pool out of pools.json as its last step, so a pool that is still listed was not finished.
    $still = $true
    try { $still = [bool]@((Read-CowPools).pools | Where-Object { $_.root -and (ConvertTo-CowFullPath $_.root) -eq (ConvertTo-CowFullPath $p.root) }).Count } catch { }
    if ($code -or $still) { $failed += $p.name; Note "not disconnected (uninstall.ps1 ended with exit code $code)" } else { Note 'disconnected' }
}
Step 'Done'
Note "$($pools.Count - $failed.Count) of $($pools.Count) pools disconnected"
if ($failed.Count) { Note "could not be disconnected, and still listed in $(Get-CowPoolsFile): $($failed -join ', ')" }
Save-Log
exit $failed.Count
