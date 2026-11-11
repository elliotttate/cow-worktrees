#Requires -Version 5.1
<#
changes_view.ps1 - checks the Changes view: the files it lists, what happened to each, the line counts and
the lines themselves, for uncommitted edits, for all work on a branch, and for one commit.

Builds a throwaway setup with make_test_pool.ps1 (under %TEMP%, removed afterwards), puts every kind of change
into its feature-a worktree, then runs the app hidden with --changes and --changes-dump and compares what the
view holds with what was done. Nothing outside the temp folder is touched; COW_WORKTREES_HOME points into it.

  changes_view.ps1 [-Exe <CowWorktrees.exe>] [-Shots <folder>] [-Keep]

-Shots also saves a picture of the view in the light and in the dark theme. -Keep leaves the temp folder.
Exit 0 when every check passes.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe'),
    [string]$Shots,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$dir = Join-Path $env:TEMP ('cow-changes-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:failed = 0; $script:passed = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; "  ok    $what" } else { $script:failed++; "  FAIL  $what $detail" }
}
function G {
    $ErrorActionPreference = 'Continue'
    $out = & git @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { throw "git $args failed:`n$($out -join "`n")" }
    $out
}
function Commit([string]$w, [string]$message, [string[]]$paths) { G -C $w add -- @paths | Out-Null; G -C $w -c user.name=test -c user.email=test@example.com commit --quiet -m $message | Out-Null }
function Text([string]$path, [string[]]$lines) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, ($lines -join "`n") + "`n")
}
function Bytes([string]$path, [int]$seed) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    $b = New-Object byte[] 2048; (New-Object Random $seed).NextBytes($b); $b[10] = 0; [IO.File]::WriteAllBytes($path, $b)
}

try {
    $cowHome = & (Join-Path $PSScriptRoot 'make_test_pool.ps1') -Dir $dir | Select-Object -Last 1
    $w = Join-Path $dir 'pool\feature-a'

    # ---- two commits on the branch -----------------------------------------------------------------------
    $engine = @('"""The engine."""', 'import math', '', 'class Engine:', '    def __init__(self):', '        self.speed = 10', '        self.gear = 1', '')
    $engine += 1..24 | ForEach-Object { "    def step_$_(self):", "        return self.speed * $_", '' }
    $engine += @('    def stop(self):', '        self.speed = 0', '        print("stopped")')
    Text "$w\src\engine.py" $engine
    Text "$w\src\util.py" (1..10 | ForEach-Object { "def helper_$_(): return $_" })
    Text "$w\docs\guide.md" (1..8 | ForEach-Object { "Guide line $_" })
    Bytes "$w\assets\logo.bin" 1
    G -C $w checkout --quiet -- README.md | Out-Null
    Remove-Item "$w\notes.txt"
    Commit $w 'Add the engine' 'src', 'docs', 'assets'
    $first = (G -C $w rev-parse --short HEAD)
    Text "$w\src\engine.py" ((Get-Content "$w\src\engine.py") -replace 'self\.gear = 1', 'self.gear = 2')
    Commit $w 'Tune the engine' 'src'
    $second = (G -C $w rev-parse --short HEAD)

    # ---- uncommitted: every kind of change ---------------------------------------------------------------
    $lines = [Collections.Generic.List[string]](Get-Content "$w\src\engine.py")
    $at = $lines.IndexOf('        self.speed = 10'); $lines[$at] = '        self.speed = 12'                  # one word of a line
    $lines.Insert($at + 1, '        self.name = "fast"')                                                       # a new line
    $stop = $lines.IndexOf('        print("stopped")'); $lines.RemoveAt($stop)                                  # a line taken out, far below
    $lines.Add('        return True')
    [IO.File]::WriteAllText("$w\src\engine.py", ($lines -join "`n") + "`n")
    G -C $w mv src/util.py src/helpers.py | Out-Null                                                             # renamed, and staged
    Remove-Item "$w\docs\guide.md"                                                                               # deleted
    Bytes "$w\assets\logo.bin" 2                                                                                 # a binary file changed
    Add-Content "$w\README.md" 'edited'
    Text "$w\notes\todo.txt" @('one', "two`twith a tab", 'three')                                                # new, text
    Bytes "$w\notes\picture.bin" 3                                                                               # new, binary

    function Run([string]$select, [string]$scope, [string[]]$more = @()) {
        $out = Join-Path $dir ("dump-" + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.json')
        $env:COW_WORKTREES_HOME = $cowHome; $env:COW_NO_UPDATE_CHECK = '1'
        $argList = @('--hidden', '--pool', 'demo', '--select', $select, '--changes', $scope, '--changes-dump', "`"$out`"", '--size', '1500x950', '--exit') + $more
        $p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
        if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force; throw "the app did not finish (--changes $scope)" }
        if (-not (Test-Path $out)) { throw "the app wrote no dump for --changes $scope (see $cowHome\gui.log)" }
        Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    }
    function File($dump, [string]$path) { $dump.files | Where-Object { $_.path -eq $path } }

    "uncommitted changes"
    $d = Run 'feature-a' 'uncommitted'
    Check 'no problem reported' (-not $d.problem) $d.problem
    Check 'the view chose "Uncommitted changes"' ($d.scope -eq 'uncommitted')
    Check 'eight files' (@($d.files).Count -eq 8) "found $(@($d.files).Count): $(($d.files | ForEach-Object path) -join ', ')"
    $e = File $d 'src/engine.py'
    Check 'engine.py is modified, 3 lines added and 2 removed' ($e.kind -eq 'Modified' -and $e.added -eq 3 -and $e.deleted -eq 2) "kind $($e.kind) +$($e.added) -$($e.deleted)"
    Check 'engine.py has two separate ranges of lines' ($e.hunks -eq 2) "hunks $($e.hunks)"
    Check 'its changed line is read on both sides' ($e.firstRemoved -eq '        self.speed = 10' -and $e.firstAdded -eq '        self.speed = 12') "'$($e.firstRemoved)' / '$($e.firstAdded)'"
    Check 'the changed word is marked in both lines' ($e.highlighted -eq 2) "marked lines: $($e.highlighted)"
    $h = File $d 'src/helpers.py'
    Check 'helpers.py is a rename of util.py, staged' ($h.kind -eq 'Renamed' -and $h.oldPath -eq 'src/util.py' -and $h.staged) "kind $($h.kind) from $($h.oldPath) staged $($h.staged)"
    $g = File $d 'docs/guide.md'
    Check 'guide.md is deleted, 8 lines' ($g.kind -eq 'Deleted' -and $g.deleted -eq 8 -and $g.lines -eq 8) "kind $($g.kind) -$($g.deleted) lines $($g.lines)"
    $l = File $d 'assets/logo.bin'
    Check 'logo.bin is a changed binary file with a note instead of lines' ($l.kind -eq 'Modified' -and $l.binary -and $l.lines -eq 0 -and $l.note) "kind $($l.kind) binary $($l.binary) lines $($l.lines)"
    $t = File $d 'notes/todo.txt'
    Check 'todo.txt is a new file, its 3 lines shown as added' ($t.kind -eq 'Untracked' -and $t.badge -eq 'N' -and $t.added -eq 3 -and $t.lines -eq 3) "kind $($t.kind) +$($t.added) lines $($t.lines)"
    $pic = File $d 'notes/picture.bin'
    Check 'picture.bin is a new binary file' ($pic.kind -eq 'Untracked' -and $pic.binary -and $pic.lines -eq 0)
    Check 'README.md is modified by one line' ((File $d 'README.md').added -eq 1)
    Check 'the totals add up' ($d.added -eq (3 + 1 + 3) -and $d.deleted -eq (2 + 8)) "+$($d.added) -$($d.deleted)"
    Check 'the rows hold every line that was read' ($d.lineRows -eq (@($d.files) | Measure-Object lines -Sum).Sum) "$($d.lineRows) rows"
    Check 'both commits can be chosen' ((@($d.scopes | Where-Object kind -eq 'Commit')).Count -eq 2 -and ($d.scopes | Where-Object key -eq 'all'))

    "everything on the branch"
    $d = Run 'feature-a' 'all'
    Check 'the view chose all work on the branch' ($d.scope -eq 'all' -and $d.scopeTitle -like '*origin/main*') $d.scopeTitle
    $e = File $d 'src/engine.py'
    Check 'engine.py is a new file here, every line added' ($e.kind -eq 'Added' -and $e.deleted -eq 0 -and $e.added -eq $lines.Count) "kind $($e.kind) +$($e.added) of $($lines.Count)"
    Check 'helpers.py is new, util.py is not listed' ((File $d 'src/helpers.py').kind -eq 'Added' -and -not (File $d 'src/util.py'))
    Check 'guide.md, added and deleted again, is not listed' (-not (File $d 'docs/guide.md'))
    Check 'the new files are listed too' ((File $d 'notes/todo.txt').kind -eq 'Untracked')

    "one commit"
    $d = Run 'feature-a' $second
    Check 'the view chose the second commit' ($d.scope -eq $second -and $d.scopeTitle -like '*Tune the engine*') "$($d.scope) $($d.scopeTitle)"
    Check 'it changes one line of engine.py and nothing else' (@($d.files).Count -eq 1 -and $d.added -eq 1 -and $d.deleted -eq 1 -and (File $d 'src/engine.py').firstAdded -eq '        self.gear = 2')
    $d = Run 'feature-a' $first
    Check 'the first commit adds four files' (@($d.files).Count -eq 4 -and @($d.files | Where-Object kind -ne 'Added').Count -eq 0) "$(@($d.files).Count) files"

    "a file that only looks changed"
    $d = Run 'phantom-a' 'uncommitted'
    $raw = File $d 'data.raw'
    Check 'data.raw is marked as only looking changed, and not counted' ($raw.kind -eq 'Phantom' -and $d.added -eq 0 -and $d.deleted -eq 0 -and $d.filesText -eq '0 files') "kind $($raw.kind), $($d.filesText)"

    "a clean worktree"
    $d = Run 'base' 'uncommitted'
    Check 'no change is listed and nothing went wrong' (@($d.files | Where-Object kind -ne 'Phantom').Count -eq 0 -and $d.filesText -eq '0 files' -and -not $d.problem) "$($d.filesText) $($d.problem)"

    if ($Shots) {
        New-Item -ItemType Directory -Force $Shots | Out-Null
        foreach ($theme in 'dark', 'light') {
            $png = Join-Path (Resolve-Path $Shots) "changes-$theme.png"
            Run 'feature-a' 'uncommitted' @('--theme', $theme, '--snapshot', "`"$png`"") | Out-Null
            Check "a picture of the $theme theme was saved" (Test-Path $png)
        }
    }
}
finally {
    if (-not $Keep -and (Test-Path -LiteralPath $dir)) {
        Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { if ($_.IsReadOnly) { $_.IsReadOnly = $false } }
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    } elseif ($Keep) { "kept: $dir" }
}
"$script:passed passed, $script:failed failed"
exit ([int]($script:failed -gt 0))
