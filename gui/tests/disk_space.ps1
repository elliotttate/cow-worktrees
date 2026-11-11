#Requires -Version 5.1
<#
disk_space.ps1 - checks the Disk space view (Base tab) against a pool whose sizes are known.

  disk_space.ps1 [-Exe <CowWorktrees.exe>] [-Drive E] [-CaptureDir <folder>] [-Keep]

Builds a throwaway repository and pool on the Dev Drive with make_test_pool.ps1 (24 MB of random data in the
project), makes real copy-on-write worktrees in it with scripts\cow_worktree.ps1, and runs the published app
hidden with --measure and --dump. It checks:
  1. without a measurement the app shows none, and a refresh never starts one;
  2. a fresh clone has next to no space of its own and shares the project's size with the base;
  3. a worktree made with plain `git worktree add` is called a full copy; a folder Git does not know is called
     that; the project's main folder is not flagged; files saved seconds ago, which ReFS has given no place on
     the drive yet, are counted all the same; a file with holes, in more pieces than one answer of the file
     system holds, counts for what it has on the drive;
  4. the totals add up, and the worktree's row carries the same numbers as the list;
  5. the result is kept: the next start shows it without measuring, with its time;
  6. after 4 MB of a clone are rewritten, its own space is 4 MB larger and its shared space 4 MB smaller;
  7. a worktree made after the measurement is mentioned until the next one;
  8. a measurement that is stopped leaves the numbers of the one before.
-CaptureDir also saves pictures of the view (light and dark) and of a worktree's details pane.
COW_WORKTREES_HOME points into the throwaway folder, so nothing of yours is read or written. Exit code = number
of failed checks.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe'),
    [string]$Drive,
    [string]$CaptureDir,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$cow = (Resolve-Path (Join-Path $PSScriptRoot '..\..\scripts\cow_worktree.ps1')).Path
$failures = 0; $checks = 0
function Check([bool]$ok, [string]$what) { $script:checks++; if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ } }
if (-not $Drive) { $Drive = (Get-Volume | Where-Object { $_.FileSystem -eq 'ReFS' -and $_.DriveLetter } | Select-Object -First 1).DriveLetter }
if (-not $Drive) { 'no ReFS volume found; pass -Drive'; exit 1 }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dir = "${Drive}:\cow-disktest-$stamp"
$pool = Join-Path $dir 'pool'
$payloadMB = 24
"app $Exe; test pool $pool"

function Invoke-App([string[]]$appArgs) {
    $script:run++
    $dump = Join-Path $dir "dump$script:run.json"
    $p = Start-Process -FilePath $Exe -ArgumentList (@('--hidden', '--pool', 'demo', '--dump', $dump, '--exit') + $appArgs) -PassThru
    if (-not $p.WaitForExit(300000)) { Stop-Process -Id $p.Id -Force; throw 'the app did not finish within 5 minutes' }
    return (Get-Content -LiteralPath $dump -Raw | ConvertFrom-Json)
}
function New-Clone([string]$name) {
    $ErrorActionPreference = 'Continue'   # the script reports progress on stderr
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $cow -Root $pool -Name $name 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { throw "cow_worktree.ps1 -Name $name failed:`n$($out -join "`n")" }
}
function Folder($dump, [string]$name) { return @($dump.diskSpace.Folders | Where-Object { $_.Name -eq $name })[0] }
function Row($dump, [string]$name) { return @($dump.worktrees | Where-Object { $_.Name -eq $name })[0] }
function MB([long]$bytes) { return ('{0:N2} MB' -f ($bytes / 1MB)) }

$run = 0
try {
    $env:COW_WORKTREES_HOME = (& (Join-Path $PSScriptRoot 'make_test_pool.ps1') -Dir $dir -PayloadMB $payloadMB | Select-Object -Last 1)
    $env:COW_CODEX_AGENTS_FILE = Join-Path $dir 'codex\AGENTS.md'
    $env:COW_NO_UPDATE_CHECK = '1'
    New-Clone 'clone-a'; New-Clone 'clone-b'
    New-Item -ItemType Directory -Force (Join-Path $pool 'leftover') | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $pool 'leftover\junk.bin'), (New-Object byte[] (2MB)))
    # A file with holes and more pieces than the file system reports in one answer: 1,200 blocks, 64 KB apart.
    $sparse = Join-Path $pool 'leftover\sparse.bin'
    [IO.File]::WriteAllBytes($sparse, (New-Object byte[] 0)); fsutil sparse setflag $sparse | Out-Null
    $block = New-Object byte[] 4096; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($block)
    $fs = [IO.File]::Open($sparse, 'Open'); foreach ($i in 0..1199) { $fs.Position = [long]$i * 64KB; $fs.Write($block, 0, $block.Length) }; $fs.Flush($true); $fs.Dispose()
    $ranges = @(fsutil sparse queryrange $sparse | Where-Object { $_ -match 'Length:\s*0x([0-9a-fA-F]+)' } | ForEach-Object { [Convert]::ToInt64($Matches[1], 16) })
    $sparseOnDisk = ($ranges | Measure-Object -Sum).Sum
    $resultFile = @(Get-ChildItem -LiteralPath (Join-Path $env:COW_WORKTREES_HOME 'disk-space') -Filter *.json -ErrorAction SilentlyContinue)

    '== before any measurement'
    $d0 = Invoke-App @()
    Check ($null -eq $d0.diskSpace -and $resultFile.Count -eq 0) 'nothing is shown, and the refresh measured nothing'
    Check ((Row $d0 'clone-a').DiskDetail -eq 'not measured yet' -and (Row $d0 'clone-a').OnPoolDrive) 'a worktree''s details say "not measured yet"'

    '== the first measurement'
    $d1 = Invoke-App @('--measure')
    $s = $d1.diskSpace
    if (-not $s) { throw "no measurement in the dump; problem: $($d1.problem)" }
    "  note  $($s.Headline); $($s.Measured)"
    foreach ($f in $s.Folders) { "  note  {0,-16} {1,-14} own {2,10}  shared {3,10}  folder {4,10}  {5}{6}" -f $f.Name, $f.Kind, $f.Own, $f.Shared, $f.Size, $f.Flag, $(if ($f.PendingBytes) { " ($(MB $f.PendingBytes) not written out yet)" }) }
    $names = @($s.Folders | ForEach-Object { $_.Name })
    $want = 'base', 'clone-a', 'clone-b', 'feature-a', 'worktree-agent1', 'phantom-a', 'repo', 'leftover'
    Check (@($want | Where-Object { $names -notcontains $_ }).Count -eq 0 -and $names.Count -eq $want.Count -and $names[0] -eq 'base') "the base first, then every checkout on the drive and the other folder in the pool: $($names -join ', ')"
    Check ($s.FileSystem -eq 'ReFS' -and $s.HasBase -and $s.Unreadable -eq 0 -and $s.ClusterSize -gt 0) "drive $($s.Drive) is ReFS, clusters of $($s.ClusterSize) bytes, every file was read"
    $base = Folder $d1 'base'; $a1 = Folder $d1 'clone-a'; $b1 = Folder $d1 'clone-b'; $full = Folder $d1 'feature-a'; $left = Folder $d1 'leftover'; $main = Folder $d1 'repo'
    Check ($base.SizeBytes -ge $payloadMB * 1MB -and $base.SizeBytes -lt ($payloadMB + 1) * 1MB) "the base is as large as the project: $(MB $base.SizeBytes)"
    Check ($base.OwnBytes -lt 1MB -and $base.SharedBytes -ge $payloadMB * 1MB) "the base shares it with the clones: own $(MB $base.OwnBytes), shared $(MB $base.SharedBytes)"
    Check ($a1.OwnBytes -lt 1MB -and $b1.OwnBytes -lt 1MB) "a fresh clone has next to nothing of its own: $(MB $a1.OwnBytes) and $(MB $b1.OwnBytes)"
    Check ($a1.SharedBytes -ge $payloadMB * 1MB -and $a1.SharedWithBaseBytes -eq $a1.SharedBytes -and -not $a1.Flag) "and shares the project with the base: $(MB $a1.SharedBytes)"
    Check ($a1.SizeBytes -eq $a1.OwnBytes + $a1.SharedBytes) 'its folder size is its own plus its shared space'
    Check ($full.Flag -eq 'full copy' -and $full.SharedWithBaseBytes -eq 0 -and $full.OwnBytes -ge $payloadMB * 1MB -and $full.Caption -match 'full copy' -and $full.Caption -match 'git worktree add') "a plain `git worktree add` is called a full copy: $($full.Caption)"
    Check ((Folder $d1 'worktree-agent1').Flag -eq 'full copy' -and (Folder $d1 'phantom-a').Flag -eq 'full copy') 'so are the other two plain worktrees'
    Check ($left.Flag -eq 'not a worktree' -and -not $left.IsWorktree -and $left.Kind -eq 'not a worktree' -and $left.OwnBytes -ge 2MB -and $left.Caption -match 'Git does not know') "a folder Git does not know is called that: $($left.Caption)"
    Check ($ranges.Count -ge 1200 -and $left.OwnBytes -eq 2MB + $sparseOnDisk -and $left.LogicalBytes -gt 70MB) "a file with holes counts for what is on the drive: $(MB $sparseOnDisk) in $($ranges.Count) pieces, of $(MB ((Get-Item -LiteralPath $sparse).Length))"
    Check ($main.IsMain -and $main.Kind -eq 'main checkout' -and -not $main.Flag -and $main.OwnBytes -ge 2 * $payloadMB * 1MB) "the project's main folder (files and Git's history, $(MB $main.OwnBytes)) is not flagged"
    $sumSize = ($s.Folders | Measure-Object SizeBytes -Sum).Sum; $sumOwn = ($s.Folders | Measure-Object OwnBytes -Sum).Sum; $sumShared = ($s.Folders | Measure-Object SharedBytes -Sum).Sum
    Check ($s.FolderBytes -eq $sumSize) "the total is the folders added up: $(MB $s.FolderBytes)"
    # base, clone-a and clone-b share the project three ways: held = everything own + the shared part once
    Check ($s.HeldBytes -eq $sumOwn + $base.SharedBytes -and $s.HeldBytes -lt $s.FolderBytes) "held on the drive: $(MB $s.HeldBytes) = all own space ($(MB $sumOwn)) + the shared part once ($(MB $base.SharedBytes))"
    Check ($s.Headline -match '^\d.* of folders, held in \d' -and $s.Explain -match 'Explorer counts every folder in full') "the headline: $($s.Headline)"
    Check ($s.DriveTotalBytes -gt $s.DriveFreeBytes -and $s.DriveFreeBytes -gt 0) "the drive's free space: $([math]::Round($s.DriveFreeBytes / 1GB)) GB of $([math]::Round($s.DriveTotalBytes / 1GB)) GB"
    $ra = Row $d1 'clone-a'
    Check ($ra.DiskOwnBytes -eq $a1.OwnBytes -and $ra.DiskSharedBytes -eq $a1.SharedBytes -and $ra.DiskDetail -match 'of its own, .* shared' -and $ra.DiskNote -match 'removing it would free') "the worktree's details: $($ra.DiskDetail). $($ra.DiskNote)"
    Check ((Row $d1 'feature-a').DiskNote -match '^A full copy') "the full copy's details: $((Row $d1 'feature-a').DiskNote)"
    $resultFile = @(Get-ChildItem -LiteralPath (Join-Path $env:COW_WORKTREES_HOME 'disk-space') -Filter *.json -ErrorAction SilentlyContinue)
    Check ($resultFile.Count -eq 1) "the result is kept in $($resultFile.FullName)"

    '== the next start'
    $d2 = Invoke-App @()
    Check ($d2.diskSpace -and $d2.diskSpace.MeasuredAt -eq $s.MeasuredAt -and (Folder $d2 'clone-a').OwnBytes -eq $a1.OwnBytes) "shows the measurement of $($s.MeasuredAt) without measuring again"
    Check (-not $d2.diskSpace.Stale) 'and nothing has changed since'

    '== 4 MB of clone-a are rewritten, and a worktree is made'
    $bytes = New-Object byte[] (4MB); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $fs = [IO.File]::Open((Join-Path $pool 'clone-a\assets\part-01.bin'), 'Create'); $fs.Write($bytes, 0, $bytes.Length); $fs.Flush($true); $fs.Dispose()
    New-Clone 'clone-c'
    $d3 = Invoke-App @()
    Check ($d3.diskSpace.MeasuredAt -eq $s.MeasuredAt -and $d3.diskSpace.Stale -match '1 worktree was made') "the old numbers stay, with a note: $($d3.diskSpace.Stale)"
    Check ((Row $d3 'clone-c').DiskDetail -eq 'not measured yet') 'the new worktree is "not measured yet"'
    $d4 = Invoke-App @('--measure')
    $a4 = Folder $d4 'clone-a'
    "  note  clone-a: own $(MB $a1.OwnBytes) -> $(MB $a4.OwnBytes), shared $(MB $a1.SharedBytes) -> $(MB $a4.SharedBytes)"
    Check ($a4.OwnBytes - $a1.OwnBytes -ge 4MB -and $a4.OwnBytes - $a1.OwnBytes -lt 4MB + 64KB) "clone-a's own space grew by the 4 MB: $(MB ($a4.OwnBytes - $a1.OwnBytes))"
    Check ($a1.SharedBytes - $a4.SharedBytes -eq 4MB -and $a4.SizeBytes -eq $a1.SizeBytes) 'its shared space shrank by 4 MB, and the folder is as large as before'
    Check ($d4.diskSpace.HeldBytes - $s.HeldBytes -ge 4MB -and $d4.diskSpace.HeldBytes - $s.HeldBytes -lt 5MB) "the drive holds about 4 MB more: $(MB ($d4.diskSpace.HeldBytes - $s.HeldBytes))"
    Check ((Folder $d4 'clone-c') -and (Folder $d4 'clone-c').OwnBytes -lt 1MB -and -not $d4.diskSpace.Stale -and $d4.diskSpace.MeasuredAt -ne $s.MeasuredAt) 'the new worktree is in the new measurement, and the note is gone'
    Check ((Folder $d4 'base').SharedBytes -eq $base.SharedBytes) 'the base still shares all of the project (with the other clones)'

    '== a measurement that is stopped'
    $d5 = Invoke-App @('--measure', 'stop')
    Check ($d5.diskSpace.Problem -match '^Stopped' -and $d5.diskSpace.MeasuredAt -eq $d4.diskSpace.MeasuredAt -and (Folder $d5 'clone-a').OwnBytes -eq $a4.OwnBytes) "keeps the numbers of the one before: $($d5.diskSpace.Problem)"

    if ($CaptureDir) {
        New-Item -ItemType Directory -Force $CaptureDir | Out-Null
        $CaptureDir = (Resolve-Path $CaptureDir).Path
        foreach ($shot in @(
                @('disk-space-light.png', '--tab', 'base', '--theme', 'light'),
                @('disk-space-dark.png', '--tab', 'base', '--theme', 'dark'),
                @('disk-space-narrow.png', '--tab', 'base', '--theme', 'dark', '--size', '1100x700'),
                @('disk-space-details.png', '--tab', 'worktrees', '--select', 'clone-a', '--theme', 'light'),
                @('disk-space-full-copy.png', '--tab', 'worktrees', '--select', 'feature-a', '--theme', 'dark'))) {
            $png = Join-Path $CaptureDir $shot[0]
            $p = Start-Process -FilePath $Exe -ArgumentList (@('--hidden', '--pool', 'demo', '--snapshot', $png, '--exit') + $shot[1..($shot.Count - 1)]) -PassThru
            if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force }
            Check (Test-Path -LiteralPath $png) "picture saved: $png"
        }
    }
} catch {
    $failures++
    "  FAIL  $($_.Exception.Message)"
} finally {
    Remove-Item Env:\COW_WORKTREES_HOME, Env:\COW_CODEX_AGENTS_FILE, Env:\COW_NO_UPDATE_CHECK -ErrorAction SilentlyContinue
    if ($Keep) { "kept: $dir (COW_WORKTREES_HOME=$dir\home)" }
    elseif (Test-Path -LiteralPath $dir) { cmd /c rd /s /q "\\?\$dir" 2>$null }
}
"$checks checks, $failures failed"
exit $failures
