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
     .claude\settings.local.json of every checkout of the repository, and of the base (so clones get them).
  6. Codex: writes a marked block into ~\.codex\AGENTS.md telling Codex agents to use the script.
  7. Schedules daily upkeep (prune + refresh the base) in Task Scheduler.
  Running it again updates everything in place. uninstall.ps1 reverses steps 4-7.
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
    [string]$BranchPrefix = 'worktree-',
    [int]$PruneDays = 7,
    [int]$FallbackMinFreeGB = 0,
    [string]$ScheduleAt = '05:00',
    [switch]$NoClaude,
    [switch]$NoCodex,
    [switch]$NoSchedule,
    [switch]$BuildNow,
    [string]$CodexAgentsFile = (Join-Path $env:USERPROFILE '.codex\AGENTS.md')
)
$ErrorActionPreference = 'Continue'
$env:RIFTRI_BYPASS = '1'
# "Debug,Release" arrives as one string through powershell -File; accept both forms.
$BuildConfigs = @($BuildConfigs | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$scripts = Join-Path $PSScriptRoot 'scripts'
. (Join-Path $scripts 'CowCommon.ps1')
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note([string]$msg) { Write-Host "    $msg" }
function Fail([string]$msg) { Write-Host "setup: $msg" -ForegroundColor Red; exit 1 }

# ---- 1. checks -----------------------------------------------------------------------------------------
Step 'Checking prerequisites'
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git is not on PATH (install Git for Windows)' }
$v = [regex]::Match((git --version), '(\d+)\.(\d+)')
if ([int]$v.Groups[1].Value -lt 2 -or ([int]$v.Groups[1].Value -eq 2 -and [int]$v.Groups[2].Value -lt 38)) { Fail "Git 2.38 or newer is needed (found $(git --version))" }
$common = Get-CowCommonDir $Repo
if (-not $common) { Fail "$Repo is not inside a Git repository" }
$main = git -C $Repo rev-parse --path-format=absolute --git-common-dir | Split-Path -Parent
$main = ConvertTo-CowFullPath $main
$Root = ConvertTo-CowFullPath $Root
if (-not $Name) { $Name = Split-Path -Leaf $main }
$Name = ($Name -replace '[^A-Za-z0-9._-]', '-')
$drive = $Root.Substring(0, 1)
$vol = Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue
if (-not $vol) { Fail "drive ${drive}: not found" }
if ($vol.FileSystem -ne 'ReFS') { Fail "${drive}: is $($vol.FileSystem); the pool root must be on a ReFS volume (a Dev Drive) so files can be block-cloned. See README: Creating a Dev Drive." }
if ($Root.StartsWith($main + '\', [StringComparison]::OrdinalIgnoreCase)) { Fail "-Root must not be inside the repository" }
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
$registered = @(git -C $main worktree list --porcelain) -contains "worktree $($base.Replace('\', '/'))"
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
$cfgFile = Join-Path $Root 'cow-config.json'
$old = if (Test-Path -LiteralPath $cfgFile) { Get-Content -LiteralPath $cfgFile -Raw | ConvertFrom-Json } else { $null }
$cfg = [ordered]@{
    version           = 1
    name              = $Name
    repo              = $main
    baseRef           = $BaseRef
    branchPrefix      = $BranchPrefix
    fallbackMinFreeGB = if ($FallbackMinFreeGB -gt 0) { $FallbackMinFreeGB } elseif ($old -and $old.fallbackMinFreeGB) { [int]$old.fallbackMinFreeGB } else { 120 }
    pruneDays         = $PruneDays
}
if ($BuildCommand) {
    $cfg.build = [ordered]@{ prepare = @($BuildPrepare | Where-Object { $_ }); command = $BuildCommand; configs = @($BuildConfigs | Where-Object { $_ }) }
} elseif ($old -and $old.build) {
    $cfg.build = $old.build   # keep a build section from an earlier run
}
ConvertTo-Json -InputObject $cfg -Depth 5 | Set-Content -LiteralPath $cfgFile -Encoding UTF8
Note $cfgFile
$pools = Read-CowPools
$pools.toolDir = $PSScriptRoot
$pools.pools = @(@($pools.pools | Where-Object { (ConvertTo-CowFullPath $_.root) -ne $Root -and $_.name -ne $Name }) + [pscustomobject]@{ name = $Name; root = $Root })
Write-CowPools $pools
Note "registered in $(Get-CowPoolsFile)"

# ---- 5. Claude Code hooks ------------------------------------------------------------------------------
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
function New-HookCommand([string]$script, [string[]]$extra, [int]$timeout, [string]$status, [string]$if) {
    $h = [ordered]@{ type = 'command' }
    if ($if) { $h.if = $if }
    $h.command = $ps
    $h.args = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $scripts $script)) + $extra
    $h.timeout = $timeout
    if ($status) { $h.statusMessage = $status }
    return [pscustomobject]$h
}
$ourHooks = [ordered]@{
    PreToolUse     = @(
        [pscustomobject]@{ matcher = 'Bash'; hooks = @(New-HookCommand 'cc_guard_hook.ps1' @('-Root', $Root) 30 $null 'Bash(*worktree*)') },
        [pscustomobject]@{ matcher = 'PowerShell'; hooks = @(New-HookCommand 'cc_guard_hook.ps1' @('-Root', $Root) 30 $null 'PowerShell(*worktree*)') })
    WorktreeCreate = @([pscustomobject]@{ hooks = @(New-HookCommand 'cc_worktree_hook.ps1' @('create', '-Root', $Root) 1800 'Cloning a copy-on-write worktree' $null) })
    WorktreeRemove = @([pscustomobject]@{ hooks = @(New-HookCommand 'cc_worktree_hook.ps1' @('remove', '-Root', $Root) 600 'Removing the worktree' $null) })
}
function Test-OurEntry($entry) {
    foreach ($h in @($entry.hooks)) { if ((@($h.args) + @($h.command)) -match 'cc_(worktree|guard)_hook\.ps1') { return $true } }
    return $false
}
function Set-ClaudeHooks([string]$checkout) {
    $dir = Join-Path $checkout '.claude'
    $file = Join-Path $dir 'settings.local.json'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $s = if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $s.hooks) { $s | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) -Force }
    foreach ($evt in $ourHooks.Keys) {
        $kept = @($s.hooks.$evt | Where-Object { $_ -and -not (Test-OurEntry $_) })
        if ($evt -like 'Worktree*' -and $kept.Count) { Note "warning: $file already has another $evt hook; Claude Code runs both" }
        $s.hooks | Add-Member -NotePropertyName $evt -NotePropertyValue (@($kept) + @($ourHooks[$evt])) -Force
    }
    ConvertTo-Json -InputObject $s -Depth 20 | Set-Content -LiteralPath $file -Encoding UTF8
    # settings.local.json must stay out of Git; exclude it locally if the repository does not ignore it.
    git -C $checkout check-ignore -q .claude/settings.local.json 2>$null
    if ($LASTEXITCODE) {
        $exclude = Join-Path (git -C $checkout rev-parse --path-format=absolute --git-common-dir) 'info\exclude'
        Add-Content -LiteralPath $exclude -Value '.claude/settings.local.json'
        Note "added .claude/settings.local.json to $exclude"
    }
    Note $file
}
if (-not $NoClaude) {
    Step 'Claude Code hooks'
    $checkouts = @(git -C $main worktree list --porcelain | Where-Object { $_ -like 'worktree *' } | ForEach-Object { ConvertTo-CowFullPath $_.Substring(9) } |
        Where-Object { $_ -eq $base -or -not $_.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) } | Where-Object { Test-Path -LiteralPath $_ })
    foreach ($c in $checkouts) { Set-ClaudeHooks $c }
    try { $st = Get-CowStatus -Dir $base } catch { Fail "git status failed in the base $base" }
    if ($st.Real.Count) { Fail "writing hooks into the base changed tracked files; check $base`n$(Format-CowEntries $st.Real)" }
    Note 'Claude Code reads new settings files when a session starts: restart open sessions.'
}

# ---- 6. Codex ------------------------------------------------------------------------------------------
if (-not $NoCodex) {
    Step 'Codex instructions'
    $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'templates\codex-AGENTS.md') -Raw
    $block = $template.Replace('{name}', $Name).Replace('{repo}', $main).Replace('{root}', $Root).Replace('{script}', (Join-Path $scripts 'cow_worktree.ps1')).Replace('{migrate}', (Join-Path $scripts 'migrate_worktree.ps1'))
    $begin = "<!-- cow-worktrees:$Name begin -->"; $end = "<!-- cow-worktrees:$Name end -->"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $CodexAgentsFile) | Out-Null
    $text = if (Test-Path -LiteralPath $CodexAgentsFile) { Get-Content -LiteralPath $CodexAgentsFile -Raw } else { '' }
    if ($null -eq $text) { $text = '' }
    $section = "$begin`r`n$($block.Trim())`r`n$end"
    $i = $text.IndexOf($begin); $j = $text.IndexOf($end)
    $text = if ($i -ge 0 -and $j -gt $i) { $text.Substring(0, $i) + $section + $text.Substring($j + $end.Length) }
            else { ($text.TrimEnd() + "`r`n`r`n" + $section + "`r`n").TrimStart() }
    Set-Content -LiteralPath $CodexAgentsFile -Value $text -Encoding UTF8 -NoNewline
    Note $CodexAgentsFile
}

# ---- 7. schedule ---------------------------------------------------------------------------------------
if (-not $NoSchedule) {
    Step 'Daily upkeep task'
    & (Join-Path $scripts 'register_refresh_task.ps1') -Root $Root -At $ScheduleAt | ForEach-Object { Note $_ }
}

if ($BuildNow -and $cfg.build) {
    Step 'Building the base (warm clones)'
    & (Join-Path $scripts 'cow_worktree.ps1') -Root $Root -RefreshBase -Build
}

Step 'Done'
Note "New worktree:   & '$(Join-Path $scripts 'cow_worktree.ps1')' -Name my-feature -Branch my-feature -From HEAD"
Note "List / prune:   ... -List   |   ... -Prune -DryRun"
if ($cfg.build -and -not $BuildNow) { Note "Warm builds:    run '$(Join-Path $scripts 'cow_worktree.ps1') -Root $Root -RefreshBase -Build' once (or wait for the nightly task)" }
