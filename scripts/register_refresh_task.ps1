#Requires -Version 5.1
<#
register_refresh_task.ps1 - schedule a pool's daily upkeep (Task Scheduler, current user).

Once a day, for one pool:
  1. cow_worktree.ps1 -Prune -OlderThanDays <pruneDays>: removes worktrees idle that long with nothing to
     lose. Claude Code never runs its remove hook for subagent worktrees, and archiving a Claude Desktop
     session did not either, so this keeps them from piling up.
  2. cow_worktree.ps1 -RefreshBase -Fetch [-Build]: fetches the pool's baseRef, moves the base there and,
     when cow-config.json has a build section, rebuilds it so clones start warm.
It starts only after the computer has been idle for 10 minutes and is not stopped when you come back, so
a refresh is never cut off halfway. Output goes to <root>\refresh.log.

  register_refresh_task.ps1 [-Root <root>] [-At 05:00]            install or update
  register_refresh_task.ps1 [-Root <root>] -Unregister             remove
#>
param([string]$Root, [string]$At = '05:00', [switch]$Unregister)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CowCommon.ps1')
$Root = Resolve-CowRoot $Root (Get-Location).Path
$Config = Get-CowConfig $Root
$taskName = Get-CowTaskName $Config.name

if ($Unregister) {
    Unregister-ScheduledTask -TaskPath $CowTaskPath -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    "unregistered $CowTaskPath$taskName"
    return
}

$cow = Join-Path $PSScriptRoot 'cow_worktree.ps1'
$log = Join-Path $Root 'refresh.log'
$build = if ($Config.build) { ' -Build' } else { '' }
$prune = if ([int]$Config.pruneDays -gt 0) { "& '$cow' -Root '$Root' -Prune -OlderThanDays $([int]$Config.pruneDays) *>> '$log'; " } else { '' }
$command = "${prune}& '$cow' -Root '$Root' -RefreshBase -Fetch$build *>> '$log'"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command `"$command`""
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -RunOnlyIfIdle -IdleDuration '00:10:00' -IdleWaitTimeout '06:00:00' -DontStopOnIdleEnd `
    -StartWhenAvailable -ExecutionTimeLimit '03:00:00' -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskPath $CowTaskPath -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Description "cow-worktrees: prune and refresh the base of pool '$($Config.name)' ($Root)" -Force | Out-Null
"registered $CowTaskPath$taskName (daily at $At when idle; log $log)"
