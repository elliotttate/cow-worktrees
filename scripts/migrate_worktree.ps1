#Requires -Version 5.1
<#
migrate_worktree.ps1 - turn an existing full worktree into a copy-on-write clone in its repository's pool.

  migrate_worktree.ps1 -From D:\src\MyRepo-feature -Name feature              copy and verify only
  migrate_worktree.ps1 -From D:\src\MyRepo-feature -Name feature -RemoveOld   ...then retire the old one

The pool is the one whose repository the old worktree belongs to (or -Root).

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

# Porcelain v1 with NUL separators; directories of untracked and ignored files collapse to one entry.
function Get-StatusEntries([string]$dir) {
    $raw = (git -C $dir status --porcelain=v1 -z --untracked-files=normal --ignored=matching) -join "`n"
    $items = $raw.Split([char]0) | Where-Object { $_ }
    foreach ($i in $items) { [pscustomobject]@{ X = $i[0]; Y = $i[1]; Path = $i.Substring(3) } }
}
$entries = @(Get-StatusEntries $old)
$staged = @($entries | Where-Object { $_.X -notin ' ', '?', '!' })
if ($staged.Count) { Fail "$old has $($staged.Count) staged changes; commit or unstage them first: $(($staged | Select-Object -First 5 | ForEach-Object Path) -join ', ')" }
Say "old: $old on $(if ($branch) { $branch } else { 'detached' }) at $($head.Substring(0, 9)); $($entries.Count) status entries"

# A second run with -RemoveOld reuses the clone from the first: verify again, then retire.
$reuse = (Test-Path -LiteralPath $new)
if ($reuse -and -not $RemoveOld) { Fail "$new already exists" }
if ($reuse -and (git -C $new rev-parse HEAD) -ne $head) { Fail "$new exists but is not at $($head.Substring(0, 9))" }
if ($reuse) { Say "reusing $new from an earlier run; verifying again before removing anything" }

# 2. clone the commit
if (-not $reuse) {
$made = & (Join-Path $PSScriptRoot 'cow_worktree.ps1') -Name $Name -From $head -Repo $main -Root $Root -NoRelocate
if ($LASTEXITCODE) { Fail 'cow_worktree.ps1 could not make the clone' }

# 3. drop the base's build output, then copy what the old worktree has beyond its tracked files
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
Say "copied $copied files from $old"
}

# 4. verify
$newEntries = @(Get-StatusEntries $new)
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

if (-not $RemoveOld) { Say "copied and verified. Run again with -RemoveOld to retire $old"; Write-Output $new; exit 0 }

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
Say ("retired {0}: {1:N1} GB freed on {2}:, {0} is now a junction to {3}, on {4}" -f $old, $freed, $old.Substring(0, 1), $new, $(if ($branch) { $branch } else { 'detached HEAD' }))
Write-Output $new
