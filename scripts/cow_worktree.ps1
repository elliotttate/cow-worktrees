#Requires -Version 5.1
<#
cow_worktree.ps1 - Git worktrees as ReFS block clones of one clean base worktree.

  new      cow_worktree.ps1 -Name my-feature [-Branch feature/x] [-From <ref>]
  remove   cow_worktree.ps1 -Remove -Name my-feature
  list     cow_worktree.ps1 -List [-Json]
  prune    cow_worktree.ps1 -Prune [-OlderThanDays N] [-DryRun]
  refresh  cow_worktree.ps1 -RefreshBase [-From <ref>] [-Fetch] [-Build]

Every command acts on one pool (see setup.ps1): -Root names it; without -Root the pool is found from
COW_ROOT, the repository you run it in, or the only pool there is. The pool's repository, base ref,
branch prefix and build commands come from <root>\cow-config.json.

"new" does:
  1. git worktree add --no-checkout at the base's commit. Plain Git, so .git/info/attributes, Git LFS
     and core.longpaths all apply as usual.
  2. Block-clones every file from the base (CowCopy.cs: CopyFileExW over \\?\ paths, 8 threads), the
     base's ignored build output included when the base has been built.
  3. Clones the base's index and sets core.checkStat=minimal for this worktree only, so Git trusts the
     cloned files without re-hashing them (clones keep size and mtime, but get new creation times).
  4. Relocates MSBuild state (.tlog, .lastbuildstate, .recipe) from the base's path to the new one,
     when the base has a finished build of its commit.
  5. Switches to -Branch / -From. Git rewrites only the paths that differ from the base.
  6. If git status then lists files that only look modified (see below), makes the index trust them again.
The new worktree's path is the last line on stdout; progress goes to stderr. Exit 1 on any failure.

Every "must stay clean" check (the base, the fresh clone) fails only on real changes. A file committed as raw
content while .gitattributes gives it a clean filter (Git LFS "files that should have been pointers", CRLF files
under `text`) can show as " M" although it is identical to the commit; such a "phantom" is recognised by its raw
hash equalling the index's blob (Get-CowStatus in CowCommon.ps1), reported once, and not counted: -List shows
it in the Phantom column of the JSON and leaves it out of Changed. After a clone, and after -RefreshBase moves
the base, the index is settled so git status is really clean (Invoke-CowSettle).

The base lives at <root>\base, is locked, and must stay clean: only -RefreshBase changes it. Creating
worktrees shares the base; a refresh waits for them to finish cloning and holds new ones back.
"remove" uses plain `git worktree remove` (no --force), so a worktree with changes is refused. The one exception:
when Git refuses a worktree whose only "changes" are phantoms, and nothing is untracked or locked, the script says
so and retries with --force (the same in "prune"). "prune"
removes only worktrees idle for -OlderThanDays with nothing to lose: no changed or untracked files, no
commits that exist nowhere else, not locked. Both delete a <branchPrefix><name> branch only when it
never gained a commit.
#>
param(
    [string]$Name,
    [string]$Branch,
    [string]$From,
    [string]$Root,
    [string]$Repo,
    [switch]$Remove,
    [switch]$List,
    [switch]$Prune,
    [int]$OlderThanDays = -1,
    [switch]$DryRun,
    [switch]$RefreshBase,
    [switch]$Fetch,
    [switch]$Build,
    [switch]$NoRelocate,
    [switch]$Json
)
# PowerShell 5.1 turns native stderr (Git progress) into errors under 'Stop'; check $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
$clock = [Diagnostics.Stopwatch]::StartNew()
function Say([string]$msg) { [Console]::Error.WriteLine(('{0,6:N1}s  {1}' -f $clock.Elapsed.TotalSeconds, $msg)) }
function Fail([string]$msg) { [Console]::Error.WriteLine("cow_worktree: $msg"); exit 1 }
# Not named "Git": PowerShell resolves names case-insensitively, so that would shadow git.exe itself.
function GitOut { & git @args 2>&1 | ForEach-Object { "$_" } }  # stderr folded in, as text
. (Join-Path $PSScriptRoot 'CowCommon.ps1')

try {
    $Root = Resolve-CowRoot $Root (Get-Location).Path
    $Config = Get-CowConfig $Root
} catch { Fail $_.Exception.Message }
$Base = Join-Path $Root 'base'
$LockDir = Join-Path $Root '.locks'
if ($OlderThanDays -lt 0) { $OlderThanDays = [int]$Config.pruneDays }
# Run from inside any checkout of the pool's repository, refs such as HEAD mean that checkout's.
if (-not $Repo) {
    $Repo = $Config.repo
    $here = git rev-parse --show-toplevel 2>$null
    if (-not $LASTEXITCODE -and $here -and (Get-CowCommonDir $here) -eq (Get-CowCommonDir $Config.repo)) { $Repo = $here }
}

# Says the phantom note (see Get-CowStatus) the first time it applies in this run, and only then.
$script:PhantomNoted = $false
function Write-PhantomNote($status, [string]$where) {
    if (-not $status.Phantom.Count -or $script:PhantomNoted) { return }
    $script:PhantomNoted = $true
    Say "$where`: $(Get-CowPhantomNote $status.Phantom.Count)"
}

function Assert-BaseClean {
    if (-not (Test-Path -LiteralPath (Join-Path $Base '.git'))) { Fail "no base worktree at $Base (run setup.ps1)" }
    # status also refreshes the base's index, which every new worktree clones.
    try { $st = Get-CowStatus -Dir $Base } catch { Fail "git status failed in the base $Base" }
    if ($st.Real.Count) { Fail "the base $Base has $($st.Real.Count) changed paths; it must stay clean:`n$(Format-CowEntries $st.Real)" }
    Write-PhantomNote $st "the base"
    $script:BaseStatus = $st
}

function Resolve-Commit([string]$ref) {
    $sha = git -C $Repo rev-parse --verify --quiet "$ref^{commit}" 2>$null
    if ($LASTEXITCODE -or -not $sha) { Fail "'$ref' is not a commit in $Repo (fetch it first?)" }
    return $sha
}

# ---- base lock: many cloners at once, or one refresher -------------------------------------------
# A named mutex guards the lock files; each holder writes "<pid> <process start ticks>", so a holder
# that died is recognised and ignored.
function Get-Holder { $p = Get-Process -Id $PID; return "$PID $($p.StartTime.Ticks)" }
function Test-HolderAlive([string]$file) {
    $text = (Get-Content -LiteralPath $file -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not $text) { return $false }
    $parts = $text.Split(' ')
    $p = Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue
    return ($p -and $p.StartTime.Ticks -eq [long]$parts[1])
}
function Use-StateMutex([scriptblock]$body) {
    $mutex = New-Object System.Threading.Mutex($false, (Get-CowMutexName $Root 'state'))
    try {
        try { [void]$mutex.WaitOne() } catch [System.Threading.AbandonedMutexException] { }  # owner died: ours now
        & $body
    } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
}
function Enter-BaseShared {
    New-Item -ItemType Directory -Force (Join-Path $LockDir 'readers') | Out-Null
    $refreshFile = Join-Path $LockDir 'refresh'
    $deadline = (Get-Date).AddMinutes(90)
    while ($true) {
        $mine = Use-StateMutex {
            if ((Test-Path -LiteralPath $refreshFile) -and (Test-HolderAlive $refreshFile)) { return $null }
            $file = Join-Path $LockDir ("readers\{0}-{1}" -f $PID, [guid]::NewGuid().ToString('N'))
            Set-Content -LiteralPath $file -Value (Get-Holder)
            return $file
        }
        if ($mine) { return $mine }
        if ((Get-Date) -gt $deadline) { Fail 'the base has been refreshing for 90 minutes; giving up' }
        Say 'the base is being refreshed; waiting'
        Start-Sleep -Seconds 15
    }
}
function Exit-BaseShared([string]$file) { if ($file) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue } }
function Enter-BaseExclusive {
    New-Item -ItemType Directory -Force (Join-Path $LockDir 'readers') | Out-Null
    $refreshFile = Join-Path $LockDir 'refresh'
    $taken = Use-StateMutex {
        if ((Test-Path -LiteralPath $refreshFile) -and (Test-HolderAlive $refreshFile)) { return $false }
        Set-Content -LiteralPath $refreshFile -Value (Get-Holder)
        return $true
    }
    if (-not $taken) { Fail 'another refresh of the base is running' }
    $deadline = (Get-Date).AddMinutes(30)
    while ($true) {
        $live = @(Get-ChildItem -LiteralPath (Join-Path $LockDir 'readers') -File | Where-Object {
            if (Test-HolderAlive $_.FullName) { $true } else { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue; $false } })
        if (-not $live.Count) { return $refreshFile }
        if ((Get-Date) -gt $deadline) { Exit-BaseExclusive $refreshFile; Fail "$($live.Count) worktree creations still hold the base after 30 minutes" }
        Say "waiting for $($live.Count) worktree creation(s) to finish cloning"
        Start-Sleep -Seconds 5
    }
}
function Exit-BaseExclusive([string]$file) { if ($file) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue } }

# ---- worktrees under the root ------------------------------------------------------------------------
function Get-CowWorktrees {
    $rootSlash = ($Root.TrimEnd('\') + '\').Replace('\', '/').ToLowerInvariant()
    $baseSlash = $Base.Replace('\', '/').ToLowerInvariant()
    $entries = @(); $current = $null
    foreach ($line in @(git -C $Repo worktree list --porcelain 2>$null) + '') {
        if ($line -like 'worktree *') { $current = @{ path = $line.Substring(9) } }
        elseif ($line -like 'branch refs/heads/*') { $current.branch = $line.Substring(18) }
        elseif ($line -like 'locked*') { $current.locked = $true }
        elseif ($line -eq '' -and $current) { $entries += $current; $current = $null }
    }
    foreach ($e in $entries) {
        $p = $e.path.ToLowerInvariant()
        if (-not $p.StartsWith($rootSlash) -or $p -eq $baseSlash) { continue }
        $path = $e.path.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $path)) { continue }
        # Phantoms (see Get-CowStatus) are not changes. A status that cannot be read counts as a change: unknown is not clean.
        try { $st = Get-CowStatus -Dir $path -Untracked normal } catch { $st = $null }
        $start = git -C $path config --worktree --get cow.startCommit 2>$null
        $own = if ($start) { [int](git -C $path rev-list --count "$start..HEAD" 2>$null) } else { $null }
        $unpushed = [int](git -C $path rev-list --count HEAD --not --remotes 2>$null)
        $index = git -C $path rev-parse --path-format=absolute --git-path index 2>$null
        $used = @((Get-Item -LiteralPath $path).LastWriteTime)
        if ($index -and (Test-Path -LiteralPath $index)) { $used += (Get-Item -LiteralPath $index).LastWriteTime }
        $lastUsed = ($used | Measure-Object -Maximum).Maximum
        [pscustomobject]@{
            Name      = Split-Path -Leaf $path
            Branch    = if ($e.branch) { $e.branch } else { '(detached)' }
            IdleDays  = [math]::Round(((Get-Date) - $lastUsed).TotalDays, 1)
            Changed   = if ($st) { @($st.Real | Where-Object { $_.X -ne '?' }).Count } else { 1 }
            Phantom   = if ($st) { $st.Phantom.Count } else { 0 }
            Untracked = if ($st) { @($st.Real | Where-Object { $_.X -eq '?' }).Count } else { 0 }
            Commits   = $own
            Unpushed  = $unpushed
            Locked    = [bool]$e.locked
            Path      = $path
            Start     = $start
        }
    }
}

function Remove-UnusedBranch([string]$path, [string]$branch, [string]$start, [string]$tip) {
    if ($branch -and $branch.StartsWith([string]$Config.branchPrefix) -and $start -and $tip -eq $start) {
        git -C $Repo branch -D -q $branch 2>$null
        return " and its unused branch $branch"
    }
    return ''
}

# `git worktree remove` refuses a worktree whose only "changes" are phantoms (see Get-CowStatus). When it
# refuses for that reason, look at the worktree ourselves: no real change, no untracked file and not locked
# means nothing of the user's is in it, and only then is --force used. This is the one place that is.
function Test-WorktreeLocked([string]$path) {
    $want = $path.Replace('\', '/').ToLowerInvariant(); $current = ''
    foreach ($line in @(git -C $Repo worktree list --porcelain 2>$null)) {
        if ($line -like 'worktree *') { $current = $line.Substring(9).ToLowerInvariant() }
        elseif ($line -like 'locked*' -and $current -eq $want) { return $true }
    }
    return $false
}
function Remove-CowWorktree([string]$path) {
    $out = GitOut -C $Repo worktree remove $path
    $ok = ($LASTEXITCODE -eq 0)
    if (-not $ok -and ($out -join ' ') -match 'modified or untracked') {
        try { $st = Get-CowStatus -Dir $path -Untracked normal } catch { $st = $null }
        if ($st -and $st.Phantom.Count -and -not $st.Real.Count -and -not (Test-WorktreeLocked $path)) {
            Say "git refused $path because of $($st.Phantom.Count) phantom changes; nothing real is changed or untracked there, so removing it with --force"
            Write-PhantomNote $st (Split-Path -Leaf $path)
            $out = GitOut -C $Repo worktree remove --force $path
            $ok = ($LASTEXITCODE -eq 0)
        }
    }
    return [pscustomobject]@{ Ok = $ok; Out = $out }
}

if ($List) {
    $rows = @(Get-CowWorktrees)
    if ($Json) {
        # For tools: every field, including Path and Start, as a JSON array (empty array when none).
        ConvertTo-Json -InputObject @($rows) -Depth 3
    } else {
        $rows | Select-Object Name, Branch, IdleDays, Changed, Untracked, Commits, Unpushed, Locked | Format-Table -AutoSize | Out-String -Width 200
    }
    exit 0
}

if ($Prune) {
    $all = @(Get-CowWorktrees)
    foreach ($w in $all) {
        $why = @()
        if ($w.IdleDays -lt $OlderThanDays) { $why += "used $($w.IdleDays) days ago" }
        if ($w.Locked) { $why += 'locked' }
        if ($w.Changed) { $why += "$($w.Changed) changed files" }
        if ($w.Untracked) { $why += "$($w.Untracked) untracked files" }
        if ($w.Unpushed -and ($w.Commits -ne 0)) { $why += "$($w.Unpushed) commits not on any remote" }
        if ($why.Count) { Say "keep  $($w.Name): $($why -join ', ')"; continue }
        if ($DryRun) { Say "would remove $($w.Name)"; continue }
        $tip = git -C $w.Path rev-parse HEAD 2>$null
        $branch = if ($w.Branch -ne '(detached)') { $w.Branch } else { '' }
        $r = Remove-CowWorktree $w.Path
        if (-not $r.Ok) { Say "could not remove $($w.Name): $($r.Out -join ' ')"; continue }
        Say "removed $($w.Name)$(Remove-UnusedBranch $w.Path $branch $w.Start $tip)"
    }
    if (-not $all.Count) { Say "no worktrees under $Root" }
    exit 0
}

if ($RefreshBase) {
    $target = if ($From) { $From } else { [string]$Config.baseRef }
    if ($Fetch -and $target -match '^([^/]+)/(.+)$' -and (git -C $Repo remote) -contains $Matches[1]) {
        $out = GitOut -C $Repo fetch --quiet $Matches[1] $Matches[2]
        if ($LASTEXITCODE) { Fail "git fetch $($Matches[1]) $($Matches[2]) failed`n$($out -join "`n")" }
    }
    $sha = Resolve-Commit $target
    $buildState = Join-Path $Root 'base-build.json'
    # Only moving the base's commit needs it to itself: that takes seconds, so worktree creation (which
    # Claude Desktop gives 5 minutes) is never held up for a whole rebuild.
    $lock = Enter-BaseExclusive
    try {
        Assert-BaseClean
        $before = git -C $Base rev-parse HEAD
        if ($before -ne $sha) {
            Remove-Item -LiteralPath $buildState -Force -ErrorAction SilentlyContinue   # build output is now stale
            $out = GitOut -C $Base switch --quiet --detach $sha
            if ($LASTEXITCODE) { Fail "could not move the base to $target`n$($out -join "`n")" }
            Assert-BaseClean
            if ($script:BaseStatus.Phantom.Count) {
                # The base moved, so its index was just rewritten: settle the false alarms (see Invoke-CowSettle).
                $after = Invoke-CowSettle -Dir $Base -Status $script:BaseStatus
                if ($after) { Say "settled the base's index: $($after.Phantom.Count) of the phantom changes left" } else { Say 'could not settle the base''s phantom changes; they stay' }
            }
            Say "base moved from $($before.Substring(0, 9)) to $(git -C $Base log -1 --format='%h %s')"
        } else {
            Say "base already at $target ($($sha.Substring(0, 9)))"
        }
    } finally { Exit-BaseExclusive $lock }

    if ($Build) {
        if (-not $Config.build) { Fail "cow-config.json has no build section, so there is nothing to build" }
        # Clones made during the build skip relocation (base-build.json is absent until it succeeds) and
        # so build in full: slower, still correct. A second refresh cannot build at the same time.
        $wanted = @($Config.build.configs | Where-Object { $_ })
        $state = $null
        if (Test-Path -LiteralPath $buildState) { $state = Get-Content -LiteralPath $buildState -Raw | ConvertFrom-Json }
        $current = $state -and $state.commit -eq $sha -and ((@($state.configs) -join '|') -eq ($wanted -join '|'))
        if ($current) { Say "base build is already current ($($sha.Substring(0, 9)), $($wanted -join ' + '))" }
        else {
            $buildMutex = New-Object System.Threading.Mutex($false, (Get-CowMutexName $Root 'build'))
            $owned = $false
            try { $owned = $buildMutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
            if (-not $owned) { Fail 'another base build is running' }
            try {
                & (Join-Path $PSScriptRoot 'build_base.ps1') -Root $Root
                if ($LASTEXITCODE) { Fail "building the base failed (see $Root\base-build.log)" }
            } finally { $buildMutex.ReleaseMutex(); $buildMutex.Dispose() }
        }
    }
    Write-Output $Base
    exit 0
}

if (-not $Name) { Fail 'give -Name (folder name under the pool root), or -List, -Prune or -RefreshBase' }
$Name = ($Name -replace '[^A-Za-z0-9._-]', '-').Trim('-.')
if (-not $Name -or $Name -eq 'base') { Fail "invalid worktree name '$Name'" }
$Path = Join-Path $Root $Name

if ($Remove) {
    $tip = git -C $Path rev-parse HEAD 2>$null
    $start = git -C $Path config --worktree --get cow.startCommit 2>$null
    $branch = git -C $Path symbolic-ref --quiet --short HEAD 2>$null
    $r = Remove-CowWorktree $Path
    if (-not $r.Ok) { Fail "git worktree remove refused $Path (uncommitted changes, untracked files, or locked?)`n$($r.Out -join "`n")" }
    Say "removed $Path$(Remove-UnusedBranch $Path $branch $start $tip)"
    exit 0
}

if (Test-Path -LiteralPath $Path) { Fail "$Path already exists" }
$volume = Get-Volume -DriveLetter $Root.Substring(0, 1) -ErrorAction SilentlyContinue
if (-not $volume -or $volume.FileSystem -ne 'ReFS') { Fail "$Root is not on a ReFS volume, so files cannot be block-cloned" }

# What to end up on: an existing branch as it is, else a new branch / detached HEAD at -From (default: base).
$existingBranch = $false
if ($Branch) {
    git -C $Repo show-ref --verify --quiet "refs/heads/$Branch" 2>$null
    $existingBranch = ($LASTEXITCODE -eq 0)
}
if ($existingBranch -and $From) { Say "branch $Branch exists; using it as it is and ignoring -From $From" }
$targetSha = if ($existingBranch) { Resolve-Commit "refs/heads/$Branch" } elseif ($From) { Resolve-Commit $From } else { $null }

function Get-BuildStamp([string]$commit) {
    # The base's build output counts only when a build of this very commit finished; returns its time.
    $file = Join-Path $Root 'base-build.json'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { $s = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { return $null }
    if ($s.commit -eq $commit) { return $s.builtAt } else { return $null }
}

$shared = Enter-BaseShared
try {
    Assert-BaseClean
    $baseHead = git -C $Base rev-parse HEAD
    if (-not $targetSha) { $targetSha = $baseHead }
    $buildBefore = Get-BuildStamp $baseHead

    $out = GitOut -C $Repo worktree add --no-checkout --detach $Path $baseHead
    if ($LASTEXITCODE) { Fail "git worktree add failed`n$($out -join "`n")" }
    Say "registered $Path at the base commit $($baseHead.Substring(0, 9))"

    function Undo([string]$why) {
        GitOut -C $Repo worktree remove --force $Path | Out-Null   # nothing of the user's is in it yet
        Fail $why
    }

    # Copy-Item cannot create paths past 260 characters under PowerShell 5.1. CowCopy uses \\?\ paths
    # and CopyFileExW, which block-clones on ReFS just as Copy-Item does.
    try {
        if (-not ('CowCopy' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'CowCopy.cs') -ErrorAction Stop }
        $copied = [CowCopy]::Run($Base, $Path, '.git', 8)
    } catch {
        Undo "block clone from the base failed: $($_.Exception.GetBaseException().Message)"
    }
    $busy = [CowCopy]::SkippedBusy; $gone = [CowCopy]::SkippedGone
    $notes = @(); if ($busy) { $notes += "$busy held open" }; if ($gone) { $notes += "$gone deleted meanwhile" }
    Say "block-cloned $copied files from the base$(if ($notes) { " (a build was running there: $($notes -join ', '))" })"

    $baseIndex = git -C $Base rev-parse --path-format=absolute --git-path index
    $newIndex = git -C $Path rev-parse --path-format=absolute --git-path index
    try { Copy-Item -LiteralPath $baseIndex -Destination $newIndex -Force -ErrorAction Stop } catch { Undo "could not clone the base's index: $($_.Exception.Message)" }
} finally { Exit-BaseShared $shared }

git -C $Path config --worktree core.checkStat minimal
if ($LASTEXITCODE) { Undo 'could not set core.checkStat for the new worktree (is extensions.worktreeConfig on? setup.ps1 sets it)' }
try { $st = Get-CowStatus -Dir $Path } catch { Undo "git status failed in the clone: $($_.Exception.Message)" }
if ($st.Real.Count) { Undo "the clone does not match the base commit ($($st.Real.Count) changed paths); is the base clean?`n$(Format-CowEntries $st.Real)" }
Write-PhantomNote $st 'the clone'
Say "cloned the index; git status shows no $(if ($st.Phantom.Count) { 'real ' })changes"

# Build state written in the base names the base's path; point it at this worktree. Only when the base's
# build of this commit was complete before and after the copy: a clone taken mid-build builds in full.
$buildAfter = Get-BuildStamp $baseHead
if ($NoRelocate) {
    Say 'left the cloned build state as it is (-NoRelocate)'
} elseif ($buildBefore -and $buildBefore -eq $buildAfter) {
    & (Join-Path $PSScriptRoot 'relocate_build.ps1') -From $Base -To $Path -StateList (Join-Path $Root 'base-build-state.txt')
    if ($LASTEXITCODE) { Say 'relocating the cloned build state failed; the first build will be a full one' }
} elseif ($Config.build) {
    Say 'the base has no finished build of its commit; the first build here will be a full one'
}

# From here on the worktree is valid; failures leave it in place for a look.
if ($Branch -and $existingBranch) { $out = GitOut -C $Path switch --quiet $Branch }
elseif ($Branch) { $out = GitOut -C $Path switch --quiet -c $Branch $targetSha }
elseif ($targetSha -ne $baseHead) { $out = GitOut -C $Path switch --quiet --detach $targetSha }
else { $out = @() }
if ($LASTEXITCODE) { Fail "worktree $Path made, but switching to $(if ($Branch) { $Branch } else { $targetSha }) failed`n$($out -join "`n")" }

try { $st = Get-CowStatus -Dir $Path } catch { Fail "worktree $Path made, but git status failed in it: $($_.Exception.Message)" }
if ($st.Real.Count) { Fail "worktree $Path made, but it shows $($st.Real.Count) changed paths after the switch`n$(Format-CowEntries $st.Real)" }
Write-PhantomNote $st 'the new worktree'
# Where this worktree started, so a cleanup can tell whether its branch gained commits.
git -C $Path config --worktree cow.startCommit (git -C $Path rev-parse HEAD)
if ($st.Phantom.Count) {
    $after = Invoke-CowSettle -Dir $Path -Status $st
    if ($after) { $st = $after; Say "settled the index: $($after.Phantom.Count) of the phantom changes left" } else { Say 'could not settle the phantom changes; they stay' }
}
$changed = @(git -C $Path diff --name-only $baseHead HEAD).Count
Say "on $(git -C $Path log -1 --format='%h %s' | ForEach-Object { $_.Substring(0, [Math]::Min(70, $_.Length)) }) ($changed paths written past the base); $(if ($st.Phantom.Count) { 'no real changes' } else { 'clean' })"
Write-Output $Path
