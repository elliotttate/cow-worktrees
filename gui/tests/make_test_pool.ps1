#Requires -Version 5.1
<#
make_test_pool.ps1 - builds a throwaway setup for trying the GUI without a real pool:

  <Dir>\remote.git        bare "origin"
  <Dir>\repo              clone of it, main pushed, one local commit not pushed
  <Dir>\pool              pool root: cow-config.json, base\ (locked, detached at origin/main),
                          feature-a\ (changed + untracked files), worktree-agent1\ (agent branch, unpushed commit)
  <Dir>\pool-broken       a second registered pool with no cow-config.json (shows the error state)
  <Dir>\home\pools.json   registry naming both pools; toolDir = <Dir>\tool (no scripts there, so actions are off)

Then run the app with COW_WORKTREES_HOME=<Dir>\home. Plain `git worktree add` stands in for block clones; the
GUI only reads Git and the pool's files, so it cannot tell. Nothing outside <Dir> is touched.
#>
param([Parameter(Mandatory)][string]$Dir)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Dir) { throw "$Dir already exists; pick a new folder" }
New-Item -ItemType Directory -Force $Dir | Out-Null
$Dir = (Resolve-Path $Dir).Path
# PowerShell 5.1 turns native stderr (Git's progress and warnings) into errors under 'Stop'; go by the exit code.
function G {
    $ErrorActionPreference = 'Continue'
    $out = & git @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { throw "git $args failed:`n$($out -join "`n")" }
}

G init --quiet --bare -b main "$Dir\remote.git"
G clone --quiet "$Dir\remote.git" "$Dir\repo"
Set-Content "$Dir\repo\README.md" 'test repository for the CoW Worktrees GUI'
G -C "$Dir\repo" add README.md
G -C "$Dir\repo" -c user.name=test -c user.email=test@example.com commit --quiet -m 'first commit'
G -C "$Dir\repo" push --quiet origin main
Add-Content "$Dir\repo\README.md" 'a local change'
G -C "$Dir\repo" -c user.name=test -c user.email=test@example.com commit --quiet -am 'local commit, not pushed'

New-Item -ItemType Directory -Force "$Dir\pool", "$Dir\pool-broken", "$Dir\home", "$Dir\tool" | Out-Null
G -C "$Dir\repo" worktree add --quiet --detach "$Dir\pool\base" origin/main
G -C "$Dir\repo" worktree lock --reason 'base of the test pool' "$Dir\pool\base"
G -C "$Dir\repo" worktree add --quiet -b feature-a "$Dir\pool\feature-a" origin/main
Add-Content "$Dir\pool\feature-a\README.md" 'edited'
Set-Content "$Dir\pool\feature-a\notes.txt" 'untracked'
G -C "$Dir\repo" worktree add --quiet -b worktree-agent1 "$Dir\pool\worktree-agent1" origin/main
Set-Content "$Dir\pool\worktree-agent1\agent.txt" 'agent work'
G -C "$Dir\pool\worktree-agent1" add agent.txt
G -C "$Dir\pool\worktree-agent1" -c user.name=test -c user.email=test@example.com commit --quiet -m 'agent commit, not pushed'

$config = [ordered]@{ version = 1; name = 'demo'; repo = "$Dir\repo"; baseRef = 'origin/main'; branchPrefix = 'worktree-'; fallbackMinFreeGB = 120; pruneDays = 7
    build = [ordered]@{ prepare = @('echo prepare'); command = 'echo build {config}'; configs = @('Debug', 'Release') } }
ConvertTo-Json $config -Depth 5 | Set-Content -LiteralPath "$Dir\pool\cow-config.json" -Encoding UTF8
$pools = [ordered]@{ version = 1; toolDir = "$Dir\tool"; pools = @([ordered]@{ name = 'demo'; root = "$Dir\pool" }, [ordered]@{ name = 'broken'; root = "$Dir\pool-broken" }) }
ConvertTo-Json $pools -Depth 5 | Set-Content -LiteralPath "$Dir\home\pools.json" -Encoding UTF8
Write-Output "$Dir\home"
