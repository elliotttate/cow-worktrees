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
