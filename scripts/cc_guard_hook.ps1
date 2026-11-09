#Requires -Version 5.1
<#
cc_guard_hook.ps1 - Claude Code PreToolUse hook for the Bash and PowerShell tools (installed by setup.ps1).

Denies a shell command that runs `git ... worktree add` in a repository that has a copy-on-write pool,
and tells Claude to use cow_worktree.ps1 instead (seconds and almost no disk, instead of a full
checkout). Text inside quotes does not count (a prompt or grep pattern that mentions the command),
except a quoted script handed to a shell with -c, -Command or /c. Put COW_ALLOW=1 anywhere in the
command to run it anyway. The settings run this hook only for commands that mention "worktree", so other
commands pay nothing.
#>
param([string]$Root)
$ErrorActionPreference = 'Continue'
$raw = [Console]::In.ReadToEnd()
try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }
$command = [string]$payload.tool_input.command
if (-not $command) { exit 0 }

# git, then options such as -C <path> or -c k=v, then "worktree add". Stops at a command separator.
$pattern = '(?i)\bgit(\.exe)?\b[^|;&\r\n]*?\bworktree\s+add\b'
$quoted = '"(?:[^"\\]|\\.)*"|''[^'']*'''
$outside = [regex]::Replace($command, $quoted, '""')
$shellScripts = [regex]::Matches($command, '(?i)(?:^|\s)(?:-c|-command|/c)\s+("(?:[^"\\]|\\.)*"|''[^'']*'')') | ForEach-Object { $_.Groups[1].Value }
$runsWorktreeAdd = ($outside -match $pattern) -or @($shellScripts | Where-Object { $_ -match $pattern }).Count -gt 0
if (-not $runsWorktreeAdd -or $command -match '\bCOW_ALLOW=1') { exit 0 }

. (Join-Path $PSScriptRoot 'CowCommon.ps1')
$session = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($payload.cwd) { $payload.cwd } else { (Get-Location).Path }
try { $Root = Resolve-CowRoot $Root $session } catch { exit 0 }   # no pool for this repository: not our business
try { Add-Content -LiteralPath (Join-Path $Root 'hook.log') -Value ('{0} guard denied: {1}' -f (Get-Date -Format s), ($command -replace '\s+', ' ')) } catch { }

$cow = Join-Path $PSScriptRoot 'cow_worktree.ps1'
$reason = @"
This repository makes worktrees with the copy-on-write script, not git worktree add (a plain checkout copies the whole repository; the clone takes seconds and almost no disk):
  powershell -NoProfile -ExecutionPolicy Bypass -File "$cow" -Name <folder> [-Branch <branch>] [-From <ref>]
It lands at $Root\<folder> (the path is the last output line); -From HEAD starts where this checkout is. Remove it with -Remove -Name <folder>.
If a separate plain checkout is really needed, run the command again with COW_ALLOW=1 in it.
"@
@{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } } | ConvertTo-Json -Compress -Depth 4
exit 0
