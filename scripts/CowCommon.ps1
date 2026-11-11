#Requires -Version 5.1
# CowCommon.ps1 - shared by the cow-worktrees scripts (dot-source it).
#
# A "pool" is one repository's copy-on-write setup: a folder on a ReFS volume (the pool root) holding
# the base worktree (<root>\base), the worktrees cloned from it, and cow-config.json. The pools on this
# machine are listed in %LOCALAPPDATA%\cow-worktrees\pools.json (COW_WORKTREES_HOME overrides the folder).

function Get-CowHome {
    if ($env:COW_WORKTREES_HOME) { return $env:COW_WORKTREES_HOME }
    return (Join-Path $env:LOCALAPPDATA 'cow-worktrees')
}

function Get-CowPoolsFile { return (Join-Path (Get-CowHome) 'pools.json') }

function Read-CowPools {
    $file = Get-CowPoolsFile
    if (-not (Test-Path -LiteralPath $file)) { return [pscustomobject]@{ version = 1; toolDir = ''; pools = @() } }
    $p = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if (-not $p.pools) { $p | Add-Member -NotePropertyName pools -NotePropertyValue @() -Force }
    return $p
}

function Write-CowPools($pools) {
    New-Item -ItemType Directory -Force -Path (Get-CowHome) | Out-Null
    $pools.pools = @($pools.pools)
    ConvertTo-Json -InputObject $pools -Depth 5 | Set-Content -LiteralPath (Get-CowPoolsFile) -Encoding UTF8
}

function ConvertTo-CowFullPath([string]$path) { return [IO.Path]::GetFullPath($path).TrimEnd('\') }

# Lower-cased full path with everything but [a-z0-9] turned into '_': E:\pools\myrepo -> e__pools_myrepo.
function Get-CowPoolId([string]$Root) { return ((ConvertTo-CowFullPath $Root).ToLowerInvariant() -replace '[^a-z0-9]', '_') }

function Get-CowMutexName([string]$Root, [string]$Kind) { return "Global\cow-worktrees.$Kind.$(Get-CowPoolId $Root)" }

function Get-CowTaskName([string]$Name) { return "Refresh-$Name" }
$CowTaskPath = '\CowWorktrees\'

function Get-CowConfig([string]$Root) {
    $file = Join-Path $Root 'cow-config.json'
    if (-not (Test-Path -LiteralPath $file)) { throw "no cow-config.json in $Root (run setup.ps1 for this repository first)" }
    $c = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $defaults = [ordered]@{ name = (Split-Path -Leaf $Root); baseRef = 'origin/main'; branchPrefix = 'worktree-'; fallbackMinFreeGB = 120; pruneDays = 7 }
    foreach ($k in $defaults.Keys) { if ($null -eq $c.$k) { $c | Add-Member -NotePropertyName $k -NotePropertyValue $defaults[$k] -Force } }
    if (-not $c.repo) { throw "$file has no repo" }
    return $c
}

# The Git common directory of the repository that contains $path, or $null.
function Get-CowCommonDir([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    $d = git -C $path rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -or -not $d) { return $null }
    return (ConvertTo-CowFullPath $d).ToLowerInvariant()
}

# Which pool a command means: -Root, else COW_ROOT, else the pool whose repository contains $Here,
# else the only pool there is.
function Resolve-CowRoot([string]$Root, [string]$Here) {
    if ($Root) { return (ConvertTo-CowFullPath $Root) }
    if ($env:COW_ROOT) { return (ConvertTo-CowFullPath $env:COW_ROOT) }
    $pools = @((Read-CowPools).pools)
    if (-not $pools.Count) { throw "no pools registered in $(Get-CowPoolsFile); run setup.ps1 first" }
    $common = Get-CowCommonDir $Here
    if ($common) {
        foreach ($p in $pools) {
            try { $c = Get-CowConfig $p.root } catch { continue }
            if ((Get-CowCommonDir $c.repo) -eq $common) { return (ConvertTo-CowFullPath $p.root) }
        }
    }
    if ($pools.Count -eq 1) { return (ConvertTo-CowFullPath $pools[0].root) }
    throw "several pools are registered and $Here belongs to none of them; pass -Root"
}

# ---- git status without the false alarms ---------------------------------------------------------------
# A file can be committed as raw content while .gitattributes gives it a clean filter (Git LFS "files that
# should have been pointers"; CRLF files under `text`). The checked-out file is identical to the commit, but
# whenever Git does not trust the index's cached file time for it (a file written in the same second as the
# index, which is what a fast switch leaves behind) it re-hashes the file *through the filter*, gets other
# bytes, and reports " M" - and keeps reporting it. Such an entry is a "phantom": nothing changed.

# Quotes one argument for a Windows command line (.NET Framework has no ArgumentList): wrap in double quotes
# when needed, escape inner quotes, and double the backslashes that come before a quote or the closing quote.
function ConvertTo-CowArgument([string]$Arg) {
    if ($Arg -ne '' -and $Arg -notmatch '[\s"]') { return $Arg }
    $s = [regex]::Replace($Arg, '(\\*)"', '$1$1\"')
    $s = [regex]::Replace($s, '(\\+)$', '$1$1')
    return '"' + $s + '"'
}

# Runs git and returns @{ Out; ExitCode }: stdout decoded explicitly as UTF-8 (which is how Git writes paths).
# PowerShell 5.1 would decode it with the console's code page, which turns every path with a non-ASCII
# character into something else, and it cannot be relied on to be settable: the app starts these scripts
# without a console. stderr is discarded (read in the background so a full pipe cannot block git).
function Invoke-CowGit([string[]]$GitArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    $psi.Arguments = (@($GitArgs) | ForEach-Object { ConvertTo-CowArgument $_ }) -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $err = $p.StandardError.ReadToEndAsync()
        $out = $p.StandardOutput.ReadToEnd()
        $p.WaitForExit()
        [void]$err.Wait()
        return [pscustomobject]@{ Out = $out; ExitCode = $p.ExitCode }
    } finally { $p.Dispose() }
}

# Runs git status in the worktree $Dir (its top folder) and returns an object with
#   Entries  every status entry: X, Y, Path (as Git prints it, slashes forward), OrigPath (renames), Phantom
#   Real     the entries that are not phantoms
#   Phantom  the phantom entries
# An entry is a phantom only when it is " M" (nothing staged, modified in the working tree), the file exists,
# and `git hash-object --no-filters` of it equals the blob the index holds for that path. Anything that
# cannot be looked up or hashed counts as a real change: never excuse what was not verified.
function Get-CowStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Dir,
        [ValidateSet('no', 'normal')][string]$Untracked = 'no',
        [switch]$Ignored
    )
    $Dir = ConvertTo-CowFullPath $Dir
    $gitArgs = @('-C', $Dir, 'status', '--porcelain=v1', '-z', "--untracked-files=$Untracked")
    if ($Ignored) { $gitArgs += '--ignored=matching' }
    $run = Invoke-CowGit $gitArgs
    if ($run.ExitCode) { throw "git status failed in $Dir" }
    # Fields are NUL separated (no newlines to tell lines apart); a rename or copy entry is followed by
    # one more field, the path it came from.
    $fields = $run.Out.Split([char]0)
    $entries = @(); $i = 0
    while ($i -lt $fields.Count) {
        $f = $fields[$i]; $i++
        if ($f.Length -lt 4) { continue }
        $e = [pscustomobject]@{ X = [string]$f[0]; Y = [string]$f[1]; Path = $f.Substring(3); OrigPath = $null; Phantom = $false }
        if (('R', 'C') -contains $e.X -or ('R', 'C') -contains $e.Y) { if ($i -lt $fields.Count) { $e.OrigPath = $fields[$i]; $i++ } }
        $entries += $e
    }

    $candidates = @($entries | Where-Object { $_.X -eq ' ' -and $_.Y -eq 'M' })
    # With core.fileMode on, " M" can also mean a changed mode bit, which a content hash cannot rule out.
    if ($candidates.Count -and (git -C $Dir config --get core.fileMode 2>$null) -ne 'true') {
        $present = @($candidates | Where-Object { [IO.File]::Exists('\\?\' + $Dir + '\' + $_.Path.Replace('/', '\')) })
        $blob = @{}; $hash = @{}
        foreach ($chunk in (Split-CowArguments ($present | ForEach-Object Path))) {
            # --literal-pathspecs: a file called a[1].txt is that file, not a pattern.
            $ls = Invoke-CowGit (@('--literal-pathspecs', '-C', $Dir, 'ls-files', '-s', '-z', '--') + $chunk)
            foreach ($rec in $ls.Out.Split([char]0)) {
                if ($rec -match '^\d+ ([0-9a-f]{40,64}) 0\t(.+)$') { $blob[$Matches[2]] = $Matches[1] }
            }
            $oids = @(git -C $Dir hash-object --no-filters -- @chunk 2>$null)
            if ($LASTEXITCODE -or $oids.Count -ne $chunk.Count) {
                # One unreadable file spoils a whole call: ask again path by path, and keep what works.
                foreach ($p in $chunk) { $one = @(git -C $Dir hash-object --no-filters -- $p 2>$null); if (-not $LASTEXITCODE -and $one.Count -eq 1) { $hash[$p] = $one[0] } }
            } else {
                for ($k = 0; $k -lt $chunk.Count; $k++) { $hash[$chunk[$k]] = $oids[$k] }
            }
        }
        foreach ($e in $present) { if ($blob.ContainsKey($e.Path) -and $hash.ContainsKey($e.Path) -and $blob[$e.Path] -eq $hash[$e.Path]) { $e.Phantom = $true } }
    }
    return [pscustomobject]@{
        Entries = $entries
        Real    = @($entries | Where-Object { -not $_.Phantom })
        Phantom = @($entries | Where-Object { $_.Phantom })
    }
}

# Splits paths into groups short enough for one command line.
function Split-CowArguments([string[]]$Paths, [int]$MaxChars = 16000) {
    $groups = New-Object System.Collections.ArrayList   # a list of arrays: returned as one object so PowerShell does not unroll it
    $group = @(); $size = 0
    foreach ($p in @($Paths)) {
        if ($group.Count -and $size + $p.Length + 1 -gt $MaxChars) { [void]$groups.Add($group); $group = @(); $size = 0 }
        $group += $p; $size += $p.Length + 1
    }
    if ($group.Count) { [void]$groups.Add($group) }
    return , $groups
}

# The one sentence every script says about phantoms, so they all say the same thing.
function Get-CowPhantomNote([int]$Count) {
    $what = if ($Count -eq 1) { '1 file only looks' } else { "$Count files only look" }
    return "$what changed to git status: committed as raw content under a clean filter (for example Git LFS files that should have been pointers) and identical to the commit. 'git add --renormalize' in the repository cures it."
}

# "XY path" lines for an error message, the first $Max of them.
function Format-CowEntries($Entries, [int]$Max = 10) {
    return ((@($Entries) | Select-Object -First $Max | ForEach-Object { "$($_.X)$($_.Y) $($_.Path)" }) -join "`n")
}

# Makes git status really clean again for phantoms (see Get-CowStatus), so that the person using the worktree
# does not meet the false " M" in `git status`, `git switch` or `git stash`. Git re-hashes through the filter
# because the file was written in the same second as the index; once the clock is past that second, one
# `git update-index --refresh` with the filter switched off for that one command compares the raw bytes with
# the blob (they are equal: that is what makes it a phantom), and records the file's real size and time.
# Only the index's cached stat data changes: no file and no commit is touched. Never use it on a worktree
# that is not ours to change (the old worktree of a migrate). Returns the status afterwards, or $null when
# nothing could be done; it never throws.
function Invoke-CowSettle {
    param([Parameter(Mandatory = $true)][string]$Dir, [Parameter(Mandatory = $true)]$Status)
    try {
        $Dir = ConvertTo-CowFullPath $Dir
        $phantoms = @($Status.Phantom)
        if (-not $phantoms.Count) { return $Status }
        # Which filter driver each phantom path is under.
        $drivers = @{}
        $run = Invoke-CowGit (@('-C', $Dir, 'check-attr', '-z', 'filter', '--') + @($phantoms | ForEach-Object Path))
        if ($run.ExitCode) { return $null }
        $f = $run.Out.Split([char]0)
        for ($i = 0; $i + 2 -lt $f.Count; $i += 3) {
            if ($f[$i + 2] -notin 'unspecified', 'unset', 'set') { $drivers[$f[$i + 2]] = $true }
        }
        if (-not $drivers.Count) { return $null }
        # Wait until the clock is clearly past the newest of these files' times: until then the files would be
        # racily clean again as soon as the index is written.
        $newest = ($phantoms | ForEach-Object { [IO.File]::GetLastWriteTimeUtc('\\?\' + $Dir + '\' + $_.Path.Replace('/', '\')) } | Measure-Object -Maximum).Maximum
        $wait = ($newest.AddSeconds(1.2) - [DateTime]::UtcNow).TotalSeconds
        if ($wait -gt 5) { return $null }   # a time in the future: not something to wait for
        if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000) + 50) }
        foreach ($name in $drivers.Keys) {
            [void](Invoke-CowGit @('-c', "filter.$name.clean=", '-c', "filter.$name.process=", '-c', "filter.$name.required=false", '-C', $Dir, 'update-index', '--refresh', '-q'))
        }
        return (Get-CowStatus -Dir $Dir)
    } catch { return $null }
}