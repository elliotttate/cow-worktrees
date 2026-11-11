#Requires -Version 5.1
<#
compare_status.ps1 - checks what the app shows against plain git, for every worktree of a pool.

Runs the app hidden with --dump (on -Pool, or the pool it showed last), then for each worktree in the dump counts `git status --porcelain`
lines (changed = not "??", untracked = "??") and `git rev-list --count HEAD --not --remotes`, and compares.
Files that only look changed (the app's Phantom count: raw content under a clean filter, identical to the commit)
are in git's count and not in the app's, so the comparison is app changed + phantom = git changed.
A worktree an agent is editing right now can differ by the time git runs; rerun if one row is off by a file.
Exit 0 when every row matches.
#>
param(
    [string]$Pool,      # pool name or root; default: the one the app showed last
    [string]$CowHome,   # folder holding pools.json, when not the default
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe')
)
$ErrorActionPreference = 'Stop'
$dumpFile = Join-Path $env:TEMP ("cow-worktrees-dump-{0}.json" -f [guid]::NewGuid().ToString('N'))
if ($CowHome) { $env:COW_WORKTREES_HOME = $CowHome }
$argList = @('--hidden', '--dump', $dumpFile, '--exit'); if ($Pool) { $argList += @('--pool', $Pool) }
$p = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList $argList -PassThru
if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw 'the app did not finish its first refresh in 3 minutes' }
$dump = Get-Content -LiteralPath $dumpFile -Raw | ConvertFrom-Json
Remove-Item -LiteralPath $dumpFile -ErrorAction SilentlyContinue

$env:GIT_OPTIONAL_LOCKS = '0'
$rows = foreach ($w in $dump.worktrees) {
    if ($w.IsMissing) { continue }
    $s = @(git -C $w.Path status --porcelain 2>$null)
    $changed = @($s | Where-Object { $_ -notlike '`?`? *' }).Count
    $untracked = @($s | Where-Object { $_ -like '`?`? *' }).Count
    $unpushed = [int](git -C $w.Path rev-list --count HEAD --not --remotes 2>$null)
    [pscustomobject]@{
        Name         = $w.Name
        AppChanged   = $w.Changed;   AppPhantom = [int]$w.Phantom; GitChanged = $changed
        AppUntracked = $w.Untracked; GitUntracked = $untracked
        AppUnpushed  = $w.Unpushed;  GitUnpushed  = $unpushed
        Match        = (($w.Changed + [int]$w.Phantom) -eq $changed -and $w.Untracked -eq $untracked -and $w.Unpushed -eq $unpushed)
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 200
"app refresh took {0:N1} s" -f $dump.seconds
if (@($rows | Where-Object { -not $_.Match }).Count) { 'MISMATCH'; exit 1 }
"all $(@($rows).Count) worktrees match"
exit 0
