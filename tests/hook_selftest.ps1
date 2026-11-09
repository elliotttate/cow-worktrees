#Requires -Version 5.1
<#
hook_selftest.ps1 - drive cc_worktree_hook.ps1 the way Claude Code does, against a real pool.

  1. create + remove with no commits: the worktree and its <branchPrefix><name> branch must both go.
  2. create, commit once, remove: the worktree must go and the branch must stay (then this test
     deletes its own test branch).
Run from inside a checkout of a repository that has a pool:  powershell -File tests\hook_selftest.ps1
#>
param([string]$Repo = (git rev-parse --show-toplevel), [string]$Root)
$ErrorActionPreference = 'Continue'
$scripts = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts'
. (Join-Path $scripts 'CowCommon.ps1')
$Root = Resolve-CowRoot $Root $Repo
$prefix = (Get-CowConfig $Root).branchPrefix
$hook = Join-Path $scripts 'cc_worktree_hook.ps1'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$errFile = [IO.Path]::GetTempFileName()
# As Claude Code does: cwd in the input is the main checkout, CLAUDE_PROJECT_DIR the session's checkout.
$env:CLAUDE_PROJECT_DIR = $Repo
$mainCheckout = Split-Path -Parent (git -C $Repo rev-parse --path-format=absolute --git-common-dir)
$failures = 0
function Check([bool]$ok, [string]$what) { if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ } }

function Invoke-Hook([string]$event, [hashtable]$payload) {
    $json = $payload | ConvertTo-Json -Compress
    $out = $json | & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $hook $event -Root $Root 2> $errFile
    $script:hookExit = $LASTEXITCODE
    return (@($out | Where-Object { "$_".Trim() }) | Select-Object -Last 1)
}

$stamp = Get-Date -Format 'HHmmss'
foreach ($commit in $false, $true) {
    $name = "selftest-$stamp-$(if ($commit) { 'commit' } else { 'plain' })"
    "== $name"
    $path = Invoke-Hook 'create' @{ hook_event_name = 'WorktreeCreate'; cwd = $mainCheckout; name = $name }
    Check ($hookExit -eq 0 -and $path -and (Test-Path -LiteralPath $path)) "create printed an existing path ($path)"
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { Get-Content $errFile; continue }
    $branch = git -C $path symbolic-ref --short HEAD 2>$null
    Check ($branch -eq "$prefix$name") "on branch $prefix$name"
    Check ((git -C $path rev-parse HEAD) -eq (git -C $Repo rev-parse HEAD)) 'starts at the session commit'
    Check (@(git -C $path status --porcelain --untracked-files=no).Count -eq 0) 'git status is clean'
    if ($commit) { git -C $path commit --allow-empty -q -m 'hook self-test commit' }
    Invoke-Hook 'remove' @{ hook_event_name = 'WorktreeRemove'; cwd = $Repo; worktree_path = $path } | Out-Null
    Check ($hookExit -eq 0 -and -not (Test-Path -LiteralPath $path)) 'remove deleted the worktree'
    git -C $Repo show-ref --verify --quiet "refs/heads/$branch"
    $branchKept = ($LASTEXITCODE -eq 0)
    if ($commit) {
        Check $branchKept 'a branch with commits is kept'
        if ($branchKept) { git -C $Repo branch -D -q $branch }
    } else {
        Check (-not $branchKept) 'an unused branch is deleted'
    }
}
Remove-Item $errFile -ErrorAction SilentlyContinue
if ($failures) { "$failures check(s) failed"; exit 1 } else { 'all checks passed' }
