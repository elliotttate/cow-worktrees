#Requires -Version 5.1
<#
.SYNOPSIS
  Set up copy-on-write worktrees for one Git repository (a "pool"), and wire them into Claude Code and Codex.

.EXAMPLE
  .\setup.ps1 -Repo D:\src\MyGame -Root E:\cow\MyGame

.EXAMPLE
  .\setup.ps1 -Repo D:\src\MyGame -Root E:\cow\MyGame -BaseRef origin/develop `
      -BuildCommand "msbuild MyGame.sln /m /p:Configuration={config} /p:Platform=x64" -BuildConfigs Debug,Release

.DESCRIPTION
  1. Checks Git and that -Root is on a ReFS volume (a Windows Dev Drive), where files can be block-cloned.
  2. Turns on extensions.worktreeConfig in the repository (per-worktree settings; harmless otherwise).
  3. Creates the base worktree at <Root>\base on -BaseRef (a full checkout, once) and locks it.
  4. Writes <Root>\cow-config.json and registers the pool in %LOCALAPPDATA%\cow-worktrees\pools.json.
  5. Claude Code: merges WorktreeCreate/WorktreeRemove hooks and a PreToolUse guard into
     .claude\settings.local.json of every checkout of the repository: the main one, its worktrees, the base
     (so clones get them) and the worktrees already in the pool. A file that has them already is not written.
  6. Codex: writes a marked block into ~\.codex\AGENTS.md telling Codex agents to use the script.
  7. Schedules daily upkeep (prune + refresh the base) in Task Scheduler.
  uninstall.ps1 reverses steps 4-7.

  Running it again updates everything in place, and changes only what is passed: -Name, -BaseRef,
  -BranchPrefix, -PruneDays, -FallbackMinFreeGB and -ScheduleAt keep the value the pool already has when
  they are left out (the defaults are for the first run). The build section works the same way, piece by
  piece: -BuildCommand, -BuildPrepare and -BuildConfigs each replace their own part and leave the other
  two (an empty -BuildPrepare '' or -BuildConfigs '' clears that part); -NoBuild removes the section.
  -NoClaude, -NoCodex and -NoSchedule mean "without this piece": on a first run it is not added, and on a
  later run it is also removed if it is there (the hooks from every checkout of the repository, the base
  and the pool's worktrees included; the Codex block; the task). Leave the switch out to put it back.
  The choices are recorded in cow-config.json:
    "integrations": { "claude": true, "codex": true, "scheduleAt": "05:00" }   (scheduleAt null = no task)
    "integrationRevision": 1                                                  (see scripts\CowIntegrations.ps1)

  -CodexAgentsFile: the Codex instructions file. Default: the COW_CODEX_AGENTS_FILE environment variable
  when set, else ~\.codex\AGENTS.md.

  Files that are not only this tool's (settings.local.json, the Codex file, .git\info\exclude, and
  cow-config.json and pools.json, which can be edited by hand) are read and written in the encoding they
  have, with or without a byte order mark, so text outside ASCII in them stays what it was; see
  scripts\CowIntegrations.ps1. A new settings.local.json or Codex file is UTF-8 without a byte order mark.

.NOTES
  Step titles: the lines that start with "==> ". The app shows them as a checklist while setup runs, so
  they are an interface: do not reword one without changing the app, and print a step only when it does
  something. In this order:
    Checking prerequisites             always
    Base worktree                      always
    Pool config                        always
    Claude Code hooks                  without -NoClaude; with it, only on a re-run that finds hooks to remove
    Codex instructions                 without -NoCodex; with it, only on a re-run that finds the block to remove
    Daily upkeep task                  without -NoSchedule; with it, only on a re-run that finds the task to remove
    Building the base (warm clones)    with -BuildNow, when there is a build section
    Done                               always (not reached when a step fails: the script exits 1 with "setup: <why>")
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$Name,
    [string]$BaseRef,
    [string]$BuildCommand,
    [string[]]$BuildPrepare,
    [string[]]$BuildConfigs,
    [switch]$NoBuild,
    [string]$BranchPrefix = 'worktree-',
    [int]$PruneDays = 7,
    [int]$FallbackMinFreeGB = 0,
    [string]$ScheduleAt = '05:00',
    [switch]$NoClaude,
    [switch]$NoCodex,
    [switch]$NoSchedule,
    [switch]$BuildNow,
    [string]$CodexAgentsFile
)
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
# "Debug,Release" arrives as one string through powershell -File; accept both forms.
$BuildConfigs = @($BuildConfigs | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$scripts = Join-Path $PSScriptRoot 'scripts'
. (Join-Path $scripts 'CowCommon.ps1')
. (Join-Path $scripts 'CowIntegrations.ps1')
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note([string]$msg) { Write-Host "    $msg" }
function Fail([string]$msg) { Write-Host "setup: $msg" -ForegroundColor Red; exit 1 }
$CodexAgentsFile = Resolve-CowCodexFile $CodexAgentsFile
# "Passed" and "left at its default" are different things on a re-run (see the description).
$given = @{}; foreach ($k in 'BranchPrefix', 'PruneDays', 'ScheduleAt', 'BuildPrepare', 'BuildConfigs') { $given[$k] = $PSBoundParameters.ContainsKey($k) }

# ---- 1. checks -----------------------------------------------------------------------------------------
Step 'Checking prerequisites'
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git is not on PATH (install Git for Windows)' }
$v = [regex]::Match((git --version), '(\d+)\.(\d+)')
if ([int]$v.Groups[1].Value -lt 2 -or ([int]$v.Groups[1].Value -eq 2 -and [int]$v.Groups[2].Value -lt 38)) { Fail "Git 2.38 or newer is needed (found $(git --version))" }
$common = Get-CowCommonDir $Repo
if (-not $common) { Fail "$Repo is not inside a Git repository" }
if ($NoBuild -and ($BuildCommand -or $given.BuildPrepare -or $given.BuildConfigs)) { Fail '-NoBuild removes the build section; do not pass -BuildCommand, -BuildPrepare or -BuildConfigs with it' }
$main = git -C $Repo rev-parse --path-format=absolute --git-common-dir | Split-Path -Parent
$main = ConvertTo-CowFullPath $main
$Root = ConvertTo-CowFullPath $Root
# A config that is already there makes this a re-run: what is not passed keeps its value, and a -No* switch
# removes its piece. (The file is read as it is, without Get-CowConfig's defaults.)
$cfgFile = Join-Path $Root 'cow-config.json'
$cfgWas = Read-CowText $cfgFile -NewWithBom   # a new one with a byte order mark: see Read-CowRegistry
$old = if ($cfgWas.Text.Trim()) { $cfgWas.Text | ConvertFrom-Json } else { $null }
if ($old) {
    if (-not $Name -and $old.name) { $Name = [string]$old.name }
    if (-not $BaseRef -and $old.baseRef) { $BaseRef = [string]$old.baseRef }
    if (-not $given.BranchPrefix -and $null -ne $old.branchPrefix) { $BranchPrefix = [string]$old.branchPrefix }
    if (-not $given.PruneDays -and $null -ne $old.pruneDays) { $PruneDays = [int]$old.pruneDays }
}
if (-not $Name) { $Name = Split-Path -Leaf $main }
$Name = ($Name -replace '[^A-Za-z0-9._-]', '-')
$drive = $Root.Substring(0, 1)
$vol = Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue
if (-not $vol) { Fail "drive ${drive}: not found" }
if ($vol.FileSystem -ne 'ReFS') { Fail "${drive}: is $($vol.FileSystem); the pool root must be on a ReFS volume (a Dev Drive) so files can be block-cloned. See README: Creating a Dev Drive." }
if ($Root.StartsWith($main + '\', [StringComparison]::OrdinalIgnoreCase)) { Fail "-Root must not be inside the repository" }
# The daily task's time: what was passed; else, on a re-run, the time the registered task has, or the one
# recorded in the config when the task is gone; else the default. Checked here, before anything is changed.
$taskAt = $null
if (-not $NoSchedule) {
    $at = $ScheduleAt
    if ($old -and -not $given.ScheduleAt) {
        $kept = Get-CowTaskTime $Name
        if (-not $kept -and $old.integrations -and $old.integrations.scheduleAt) { $kept = [string]$old.integrations.scheduleAt }
        if ($kept) { $at = $kept }
    }
    try { $taskAt = ([datetime]$at).ToString('HH:mm', [Globalization.CultureInfo]::InvariantCulture) } catch { Fail "-ScheduleAt '$at' is not a time of day (use HH:mm, for example 05:00)" }
}
Note "repository: $main"
Note "pool root:  $Root (${drive}: ReFS, $([math]::Round($vol.SizeRemaining / 1GB)) GB free)"

# CopyFileExW block-clones on ReFS only on Windows builds that support it: prove it on this volume.
New-Item -ItemType Directory -Force -Path $Root | Out-Null
if (-not ('CowCopy' -as [type])) { Add-Type -Path (Join-Path $scripts 'CowCopy.cs') }
$probeA = Join-Path $Root '.clone-probe-a'; $probeB = Join-Path $Root '.clone-probe-b'
try {
    $bytes = New-Object byte[] (4MB); (New-Object Random).NextBytes($bytes); [IO.File]::WriteAllBytes($probeA, $bytes)
    Remove-Item -LiteralPath $probeB -Force -ErrorAction SilentlyContinue
    [CowCopy]::CopyOne($probeA, $probeB, $true)
    $a = [CowCopy]::FirstCluster($probeA); $b = [CowCopy]::FirstCluster($probeB)
    if ($a -ge 0 -and $a -eq $b) { Note 'block cloning works on this volume' }
    else { Write-Host "    warning: copies on ${drive}: are not block clones on this Windows build (Windows 11 24H2 or later block-clones in CopyFile). Worktrees will work, but each takes full space." -ForegroundColor Yellow }
} catch { Write-Host "    warning: could not test block cloning: $($_.Exception.Message)" -ForegroundColor Yellow }
finally { Remove-Item -LiteralPath $probeA, $probeB -Force -ErrorAction SilentlyContinue }

# ---- base ref ----------------------------------------------------------------------------------------
if (-not $BaseRef) {
    $BaseRef = git -C $main symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>$null
    if (-not $BaseRef) { $BaseRef = git -C $main rev-parse --abbrev-ref '@{u}' 2>$null }
    if (-not $BaseRef -or $LASTEXITCODE) { $BaseRef = git -C $main symbolic-ref --quiet --short HEAD 2>$null }
}
if (-not $BaseRef) { Fail 'could not work out a base ref; pass -BaseRef (for example origin/main)' }
$baseSha = git -C $main rev-parse --verify --quiet "$BaseRef^{commit}" 2>$null
if (-not $baseSha -and $BaseRef -match '^([^/]+)/(.+)$') { git -C $main fetch --quiet $Matches[1] $Matches[2] 2>$null; $baseSha = git -C $main rev-parse --verify --quiet "$BaseRef^{commit}" 2>$null }
if (-not $baseSha) { Fail "$BaseRef is not a commit in $main" }
Note "base ref:   $BaseRef ($($baseSha.Substring(0, 9)))"

# ---- 2. per-worktree config ------------------------------------------------------------------------------
git -C $main config extensions.worktreeConfig true
if ($LASTEXITCODE) { Fail 'could not set extensions.worktreeConfig' }

# ---- 3. base worktree ----------------------------------------------------------------------------------
Step 'Base worktree'
New-Item -ItemType Directory -Force -Path $Root | Out-Null
$base = Join-Path $Root 'base'
$listing = @(git -C $main worktree list --porcelain)
$baseLine = "worktree $($base.Replace('\', '/'))"
$registered = $listing -contains $baseLine
if ($registered) {
    # A base whose first checkout was cut off (the PC went down, setup was stopped) is still registered, and
    # still carries the lock Git itself puts on a worktree while it makes it ("initializing"). All it holds is
    # part of the commit, so it is removed and made again. -f twice: one -f does not remove a locked worktree.
    $block = @(); for ($i = [array]::IndexOf($listing, $baseLine) + 1; $i -lt $listing.Count -and $listing[$i]; $i++) { $block += $listing[$i] }
    if ($block -contains 'locked initializing') {
        Note "an earlier setup stopped while it was making $base; removing the partial copy and making it again"
        git -C $main worktree remove -f -f $base 2>$null
        if ($LASTEXITCODE -or (Test-Path -LiteralPath $base)) { Fail "could not remove the partial base $base (is something using it?)" }
        $registered = $false
    }
}
if ($registered) {
    Note "already there: $base at $(git -C $base rev-parse --short HEAD)"
} else {
    if (Test-Path -LiteralPath $base) { Fail "$base exists but is not a worktree of $main; move it away first" }
    Note "checking out $BaseRef into $base (a full checkout, once; large repositories take a while)"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    git -C $main worktree add --detach $base $baseSha
    if ($LASTEXITCODE) { Fail 'git worktree add for the base failed' }
    Note "done in $([math]::Round($clock.Elapsed.TotalMinutes, 1)) min"
}
git -C $main worktree lock --reason "cow-worktrees base for pool '$Name': keep clean, change only with cow_worktree.ps1 -RefreshBase" $base 2>$null
try { $st = Get-CowStatus -Dir $base } catch { Fail "git status failed in the base $base" }
if ($st.Real.Count) { Fail "the base $base has $($st.Real.Count) changed files; it must be clean:`n$(Format-CowEntries $st.Real)" }
if ($st.Phantom.Count) { Note (Get-CowPhantomNote $st.Phantom.Count) }

# ---- 4. config + registry ------------------------------------------------------------------------------
Step 'Pool config'
$cfg = [ordered]@{
    version           = 1
    name              = $Name
    repo              = $main
    baseRef           = $BaseRef
    branchPrefix      = $BranchPrefix
    fallbackMinFreeGB = if ($FallbackMinFreeGB -gt 0) { $FallbackMinFreeGB } elseif ($old -and $old.fallbackMinFreeGB) { [int]$old.fallbackMinFreeGB } else { 120 }
    pruneDays         = $PruneDays
}
# The build section: each of its three parts comes from its parameter when that was passed, else from the
# section an earlier run wrote. Without a command (passed now or earlier) there is no section.
$oldBuild = if ($old) { $old.build } else { $null }
if ($NoBuild) {
    if ($oldBuild) { Note 'build section removed (-NoBuild): the base is no longer built, clones start cold' }
} elseif ($BuildCommand -or ($oldBuild -and $oldBuild.command -and ($given.BuildPrepare -or $given.BuildConfigs))) {
    $prepare = if ($given.BuildPrepare -or -not $oldBuild) { $BuildPrepare } else { $oldBuild.prepare }
    $configs = if ($given.BuildConfigs -or -not $oldBuild) { $BuildConfigs } else { $oldBuild.configs }
    $cfg.build = [ordered]@{
        prepare = @($prepare | Where-Object { $_ })
        command = if ($BuildCommand) { $BuildCommand } else { [string]$oldBuild.command }
        configs = @($configs | Where-Object { $_ })
    }
} elseif ($oldBuild) {
    $cfg.build = $oldBuild   # keep a build section from an earlier run
}
# What this run was asked to wire up; the app's switches read it back. scheduleAt is null without the task.
$cfg.integrations = [ordered]@{ claude = (-not $NoClaude); codex = (-not $NoCodex); scheduleAt = $taskAt }
$cfg.integrationRevision = $CowIntegrationRevision
$cfgText = (ConvertTo-Json -InputObject $cfg -Depth 5) + "`r`n"
if ($cfgText -cne $cfgWas.Text) { Write-CowText $cfgFile $cfgText $cfgWas.Encoding }
Note $cfgFile
$registry = Read-CowRegistry
$pools = $registry.Pools
$pools.toolDir = $PSScriptRoot
$pools.pools = @(@($pools.pools | Where-Object { (ConvertTo-CowFullPath $_.root) -ne $Root -and $_.name -ne $Name }) + [pscustomobject]@{ name = $Name; root = $Root })
Write-CowRegistry $registry
Note "registered in $(Get-CowPoolsFile)"

# ---- 5. Claude Code hooks ------------------------------------------------------------------------------
if (-not $NoClaude) {
    Step 'Claude Code hooks'
    # Every checkout, the pool's worktrees too. A clone gets the base's file when it is made, but one made
    # while the hooks were switched off (or before the tool folder moved) would never get them, and a
    # session started in it would make plain worktrees. Set-CowClaudeHooks writes only where the file changes.
    $checkouts = @(Get-CowCheckouts $main)
    $changed = 0
    foreach ($c in $checkouts) { Set-CowClaudeHooks $c $Root $scripts | ForEach-Object { Note $_; $changed++ } }
    try { $st = Get-CowStatus -Dir $base } catch { Fail "git status failed in the base $base" }
    if ($st.Real.Count) { Fail "writing hooks into the base changed tracked files; check $base`n$(Format-CowEntries $st.Real)" }
    if ($changed) { Note 'Claude Code reads new settings files when a session starts: restart open sessions.' }
    else { Note "already in place in all $($checkouts.Count) checkouts" }
} elseif ($old) {
    # Switched off on a re-run: out of every checkout, the pool's worktrees too (each carries the base's copy).
    $withHooks = @(Get-CowCheckouts $main | Where-Object { Test-CowClaudeHooks $_ })
    if ($withHooks.Count) {
        Step 'Claude Code hooks'
        foreach ($c in $withHooks) { Remove-CowClaudeHooks $c | ForEach-Object { Note $_ } }
        Note 'Claude Code reads settings files when a session starts: restart open sessions.'
    }
}

# ---- 6. Codex ------------------------------------------------------------------------------------------
if (-not $NoCodex) {
    Step 'Codex instructions'
    Set-CowCodexBlock $CodexAgentsFile $Name $main $Root $PSScriptRoot | ForEach-Object { Note $_ }
} elseif ($old -and (Test-CowCodexBlock $CodexAgentsFile $Name)) {
    Step 'Codex instructions'
    Remove-CowCodexBlock $CodexAgentsFile $Name | ForEach-Object { Note $_ }
}

# ---- 7. schedule ---------------------------------------------------------------------------------------
if (-not $NoSchedule) {
    Step 'Daily upkeep task'
    & (Join-Path $scripts 'register_refresh_task.ps1') -Root $Root -At $taskAt | ForEach-Object { Note $_ }
} elseif ($old -and (Get-ScheduledTask -TaskPath $CowTaskPath -TaskName (Get-CowTaskName $Name) -ErrorAction SilentlyContinue)) {
    Step 'Daily upkeep task'
    & (Join-Path $scripts 'register_refresh_task.ps1') -Root $Root -Unregister | ForEach-Object { Note $_ }
}

if ($BuildNow -and $cfg.build) {
    Step 'Building the base (warm clones)'
    & (Join-Path $scripts 'cow_worktree.ps1') -Root $Root -RefreshBase -Build
}

Step 'Done'
Note "New worktree:   & '$(Join-Path $scripts 'cow_worktree.ps1')' -Name my-feature -Branch my-feature -From HEAD"
Note "List / prune:   ... -List   |   ... -Prune -DryRun"
if ($cfg.build -and -not $BuildNow) { Note "Warm builds:    run '$(Join-Path $scripts 'cow_worktree.ps1') -Root $Root -RefreshBase -Build' once (or wait for the nightly task)" }
