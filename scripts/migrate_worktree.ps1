#Requires -Version 5.1
<#
migrate_worktree.ps1 - turn an existing full worktree into a copy-on-write clone in its repository's pool.

  migrate_worktree.ps1 -From D:\src\MyRepo-feature -Name feature              copy and verify only
  migrate_worktree.ps1 -From D:\src\MyRepo-feature -Name feature -RemoveOld   ...then retire the old one

The pool is the one whose repository the old worktree belongs to (or -Root).

0. Files that only look modified (committed raw under a clean filter, see Get-CowStatus) are not changes: they
   are left out of the copy and every check, and nothing is ever written into the old worktree.
1. Refuses when the old worktree has staged changes, is the main checkout, is a sparse checkout
   (nothing to gain), or has a process running from it.
2. cow_worktree.ps1 clones the old worktree's commit from the base (tracked files: block clones).
3. Removes the base's ignored build output from the new clone (git clean -X, in the new clone only),
   then copies from the old worktree every untracked and ignored file, modified tracked files, and
   deletions, keeping timestamps.
4. Verifies: same Git status in both; every untracked and ignored file present with the same size and
   modified time; SHA-256 equal for every copied file over 64 MB and for modified tracked files.
5. Relocates MSBuild build state from the old path to the new one.
With -RemoveOld, only after all checks pass:
6. Renames the old worktree first (refused while anything has it open, in which case nothing is
   touched), then removes it (git worktree remove --force), checks out its branch in the new clone,
   locks the clone (so -Prune never touches it) and leaves a junction at the old path pointing to the
   new one, so anything that remembers the old path keeps working.

A run that stopped can be repeated: the clone records cow.migrateFrom and cow.migrateState (copying / copied)
in its worktree config. When the target name already exists: a clone of this old worktree marked "copying" has
step 3 redone; one marked "copied" is verified again; a detached clone at the old commit with no marker and no
cow.startCommit (cow_worktree.ps1 stopped after the switch) is finished; one with cow.startCommit and no marker
(made by 1.0.0) is verified and retired when -RemoveOld is given. Anything else is refused. At the end the new
clone's index is settled (Invoke-CowSettle) so git status there is really clean.
#>
param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Root,
    [switch]$RemoveOld
)
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
$clock = [Diagnostics.Stopwatch]::StartNew()
function Say([string]$msg) { [Console]::Error.WriteLine(('{0,6:N1}s  {1}' -f $clock.Elapsed.TotalSeconds, $msg)) }
function Fail([string]$msg) { [Console]::Error.WriteLine("migrate_worktree: $msg"); exit 1 }
. (Join-Path $PSScriptRoot 'CowCommon.ps1')
if (-not ('CowCopy' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'CowCopy.cs') }

$old = [IO.Path]::GetFullPath($From).TrimEnd('\')
try { $Root = Resolve-CowRoot $Root $old } catch { Fail $_.Exception.Message }
$new = Join-Path $Root $Name
if (-not (Test-Path -LiteralPath (Join-Path $old '.git') -PathType Leaf)) { Fail "$old is not a linked worktree (the main checkout cannot be migrated)" }
$common = git -C $old rev-parse --path-format=absolute --git-common-dir
$main = Split-Path -Parent $common
$head = git -C $old rev-parse HEAD
$branch = git -C $old symbolic-ref --quiet --short HEAD 2>$null
# A sparse checkout holds only a few files: moving it frees nothing and would turn it into a full one.
if ((git -C $old config --get core.sparseCheckout 2>$null) -eq 'true') {
    Fail "$old is a sparse checkout ($(@(git -C $old sparse-checkout list 2>$null).Count) patterns); it takes almost no space, so there is nothing to gain by migrating it"
}
$running = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($old + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count) { Fail "processes are running from $old`: $(($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ')" }

# What the old worktree has beyond its commit: untracked and ignored files (directories of them collapse to
# one entry), modified tracked files, deletions. Files that only look modified (see Get-CowStatus) are not
# changes: the clone already holds them exactly as committed, so they stay out of the copy, the comparison
# and the hashing. Nothing is ever written into the old worktree.
try { $oldStatus = Get-CowStatus -Dir $old -Untracked normal -Ignored } catch { Fail $_.Exception.Message }
$entries = @($oldStatus.Real)
$staged = @($entries | Where-Object { $_.X -notin ' ', '?', '!' })
if ($staged.Count) { Fail "$old has $($staged.Count) staged changes; commit or unstage them first: $(($staged | Select-Object -First 5 | ForEach-Object Path) -join ', ')" }
Say "old: $old on $(if ($branch) { $branch } else { 'detached' }) at $($head.Substring(0, 9)); $($entries.Count) status entries"
if ($oldStatus.Phantom.Count) { Say "old: $(Get-CowPhantomNote $oldStatus.Phantom.Count) They are left out of the copy and the checks." }

# A clone left by an earlier run is picked up where it stopped. Progress is recorded in the clone's own
# config: cow.migrateFrom (the old worktree) and cow.migrateState ('copying' while step 3 runs, 'copied'
# once it has finished).
function Get-NewConfig([string]$key) { return (git -C $new config --worktree --get $key 2>$null) }
$mode = 'fresh'
if (Test-Path -LiteralPath $new) {
    $isWorktree = (Test-Path -LiteralPath (Join-Path $new '.git') -PathType Leaf) -and (Get-CowCommonDir $new) -eq (Get-CowCommonDir $old)
    $newHead = if ($isWorktree) { git -C $new rev-parse HEAD 2>$null } else { $null }
    if (-not $isWorktree) { Fail "$new already exists and is not a worktree of this repository; choose another -Name, or move that folder away" }
    if ($newHead -ne $head) {
        Fail "$new already exists, as a worktree of this repository, but not at $($head.Substring(0, 9)). If it is a leftover you do not need, discard it with: git -C $main worktree remove --force $new"
    }
    $mFrom = Get-NewConfig 'cow.migrateFrom'; $mState = Get-NewConfig 'cow.migrateState'; $startCommit = Get-NewConfig 'cow.startCommit'
    $isMine = $mFrom -and (ConvertTo-CowFullPath $mFrom) -eq $old
    if ($isMine -and $mState -eq 'copied') {
        $mode = 'verify'
        Say "reusing $new`: an earlier run copied it; verifying again$(if ($RemoveOld) { ' before removing anything' })"
    } elseif ($isMine -and $mState -eq 'copying') {
        $mode = 'resume'
        Say "resuming $new`: an earlier run stopped while copying; copying again, then verifying"
    } elseif (-not $mFrom -and -not $mState -and -not $startCommit) {
        # No marker and no cow.startCommit: cow_worktree.ps1 stopped after the switch (for example on the
        # false alarm described in Get-CowStatus). Only a clone that holds nothing of its own is picked up.
        git -C $new symbolic-ref --quiet HEAD 2>$null | Out-Null
        $detached = ($LASTEXITCODE -ne 0)
        try { $newReal = @((Get-CowStatus -Dir $new).Real) } catch { Fail $_.Exception.Message }
        if (-not $detached -or $newReal.Count) { Fail "$new already exists" }
        $mode = 'resume'
        Say "resuming $new`: a clone from an earlier run that stopped after the switch; finishing it"
        git -C $new config --worktree cow.startCommit $head
        if ($LASTEXITCODE) { Fail "could not write cow.startCommit in $new" }
    } elseif (-not $mFrom -and -not $mState -and $RemoveOld) {
        $mode = 'verify'
        Say "reusing $new from an earlier run; verifying again before removing anything"
    } else {
        Fail "$new already exists"
    }
}

# 2. clone the commit
if ($mode -eq 'fresh') {
    $made = & (Join-Path $PSScriptRoot 'cow_worktree.ps1') -Name $Name -From $head -Repo $main -Root $Root -NoRelocate
    if ($LASTEXITCODE) { Fail 'cow_worktree.ps1 could not make the clone' }
}

# 3. drop the base's build output, then copy what the old worktree has beyond its tracked files.
# Safe to repeat: the clean removes whatever an earlier attempt copied, and the copy replaces files.
if ($mode -ne 'verify') {
    git -C $new config --worktree cow.migrateFrom $old
    if ($LASTEXITCODE) { Fail "could not write cow.migrateFrom in $new" }
    git -C $new config --worktree cow.migrateState copying
    $out = git -C $new clean -fdXq 2>&1
    if ($LASTEXITCODE) { Fail "git clean -X in the new clone failed: $out" }
    $copied = 0
    foreach ($e in $entries) {
        $src = Join-Path $old ($e.Path.Replace('/', '\'))
        $dst = Join-Path $new ($e.Path.Replace('/', '\'))
        if ($e.Y -eq 'D') { Remove-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue; continue }
        if (-not (Test-Path -LiteralPath $src)) { continue }
        if (Test-Path -LiteralPath $src -PathType Container) {
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
            $copied += [CowCopy]::Run($src, $dst, '', 8, $true)   # entries can nest; copying twice is harmless
        } else {
            [CowCopy]::CopyOne($src, $dst, $true)   # modified tracked files replace the clone's copy
            $copied++
        }
    }
    git -C $new config --worktree cow.migrateState copied
    Say "copied $copied files from $old"
}
# 4. verify
try { $newStatus = Get-CowStatus -Dir $new -Untracked normal -Ignored } catch { Fail $_.Exception.Message }
$newEntries = @($newStatus.Real)
if ($newStatus.Phantom.Count) { Say "new: $(Get-CowPhantomNote $newStatus.Phantom.Count) They are left out of the checks." }
$a = ($entries | ForEach-Object { "$($_.X)$($_.Y) $($_.Path)" } | Sort-Object) -join "`n"
$b = ($newEntries | ForEach-Object { "$($_.X)$($_.Y) $($_.Path)" } | Sort-Object) -join "`n"
if ($a -ne $b) {
    $diff = Compare-Object ($a -split "`n") ($b -split "`n") | Select-Object -First 10 | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
    Fail "git status differs between old and new:`n$($diff -join "`n")"
}
$problems = 0; $hashed = 0; $total = 0; $bytes = 0
$relocatable = @('.tlog', '.lastbuildstate', '.recipe')   # rewritten by relocation: path and time only
foreach ($e in $entries | Where-Object { $_.Y -ne 'D' }) {
    $src = Join-Path $old ($e.Path.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $files = if (Test-Path -LiteralPath $src -PathType Container) { Get-ChildItem -LiteralPath $src -Recurse -File -Force } else { Get-Item -LiteralPath $src -Force }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($old.Length + 1)
        $g = Get-Item -LiteralPath (Join-Path $new $rel) -Force -ErrorAction SilentlyContinue
        $total++; $bytes += $f.Length
        $sizeMatters = $relocatable -notcontains $f.Extension.ToLowerInvariant()
        if (-not $g -or ($sizeMatters -and $g.Length -ne $f.Length) -or $g.LastWriteTimeUtc -ne $f.LastWriteTimeUtc) {
            if ($problems -lt 10) { Say "MISMATCH $rel" }; $problems++; continue
        }
        if ($f.Length -gt 64MB -or $e.Y -eq 'M') {
            if ((Get-FileHash -LiteralPath $f.FullName).Hash -ne (Get-FileHash -LiteralPath $g.FullName).Hash) {
                if ($problems -lt 10) { Say "HASH MISMATCH $rel" }; $problems++
            }
            $hashed++
        }
    }
}
if ($problems) { Fail "$problems files differ between old and new; nothing removed. Look at $new" }
Say ("verified {0} files ({1:N1} GB): same path, size and time; {2} large or modified files hashed equal" -f $total, ($bytes / 1GB), $hashed)

# 5. build state names the old path (relocating twice is harmless: nothing names the old path any more)
& (Join-Path $PSScriptRoot 'relocate_build.ps1') -From $old -To $new

# Last, make git status in the new clone really clean (see Invoke-CowSettle). Only ever the new clone.
function Invoke-SettleNew {
    try { $s = Get-CowStatus -Dir $new } catch { return }
    if (-not $s.Phantom.Count) { return }
    $after = Invoke-CowSettle -Dir $new -Status $s
    if ($after) { Say "settled the new clone's index: $($after.Phantom.Count) of the phantom changes left" } else { Say 'could not settle the phantom changes in the new clone; they stay' }
}

if (-not $RemoveOld) { Invoke-SettleNew; Say "copied and verified. Run again with -RemoveOld to retire $old"; Write-Output $new; exit 0 }

# 6. retire the old worktree
$freeBefore = (Get-Volume -DriveLetter $old.Substring(0, 1)).SizeRemaining
# Rename first: Windows refuses to rename a folder that a process has open (an editor, a terminal or a
# session sitting in it), and then nothing has been touched. Deleting straight away could stop halfway.
$retiring = "$old.retiring"
# Short-lived holders (the worktree monitor's periodic git, an antivirus scan) let go within seconds;
# a terminal or session sitting in the folder does not.
for ($try = 1; $try -le 6; $try++) {
    $out = git -C $main worktree move $old $retiring 2>&1 | ForEach-Object { "$_" }
    if (-not $LASTEXITCODE -and -not (Test-Path -LiteralPath $old)) { break }
    if ($try -lt 6) { Say "$old is in use; retrying ($try of 5)"; Start-Sleep -Seconds 3 }
}
if ($LASTEXITCODE -or (Test-Path -LiteralPath $old)) {
    Fail "something still has $old open (a terminal, editor or session inside it?); nothing was removed. Close it and run this again. The new clone at $new is complete.`n$($out -join "`n")"
}
$out = git -C $main worktree remove --force $retiring 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -or (Test-Path -LiteralPath $retiring)) { Fail "could not finish removing $retiring; the new clone at $new is complete:`n$($out -join "`n")" }
if ($branch) {
    $out = git -C $new switch --quiet $branch 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { Fail "removed $old, but switching $new to $branch failed:`n$($out -join "`n")" }
}
git -C $main worktree lock --reason "migrated from $old; long-lived, never prune" $new
New-Item -ItemType Junction -Path $old -Target $new | Out-Null
$freed = ((Get-Volume -DriveLetter $old.Substring(0, 1)).SizeRemaining - $freeBefore) / 1GB
Invoke-SettleNew
Say ("retired {0}: {1:N1} GB freed on {2}:, {0} is now a junction to {3}, on {4}" -f $old, $freed, $old.Substring(0, 1), $new, $(if ($branch) { $branch } else { 'detached HEAD' }))
Write-Output $new
