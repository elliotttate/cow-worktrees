#Requires -Version 5.1
<#
wizard_found.ps1 - checks that the walkthrough finds setups made before and that "Use them" brings one back.

Makes a throwaway repository and two setups under <Drive>:\cow-found-test-<id> with setup.ps1 (no daily task),
then plays "the app was installed again": a new, empty app folder and no Codex file. The app runs hidden with
--wizard welcome, --found-dump and --wizard-adopt. COW_SCAN_ROOTS keeps the search inside the test folder, so
real setups on the drive are neither listed nor touched; COW_WORKTREES_HOME and COW_CODEX_AGENTS_FILE point
into it too. Everything is removed afterwards.

  wizard_found.ps1 [-Exe <CowWorktrees.exe>] [-Drive E] [-Shots <folder>] [-Keep]

Exit 0 when every check passes.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe'),
    [string]$Drive = 'E',
    [string]$Shots,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$tool = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dir = "${Drive}:\cow-found-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
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
function Ps1([string]$script, [string[]]$more) {
    $ErrorActionPreference = 'Continue'
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script @more 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { throw "$script failed:`n$($out -join "`n")" }
}
function NewRepo([string]$path) {
    New-Item -ItemType Directory -Force $path | Out-Null
    G init --quiet -b main $path | Out-Null
    Set-Content "$path\README.md" 'a project'
    G -C $path add README.md | Out-Null
    G -C $path -c user.name=test -c user.email=test@example.com commit --quiet -m 'first commit' | Out-Null
}

$demoRoot = "$dir\pools\demo"; $goneRoot = "$dir\pools\gone"
try {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $env:COW_NO_UPDATE_CHECK = '1'
    $env:COW_SCAN_ROOTS = $dir
    $env:COW_CODEX_AGENTS_FILE = "$dir\codex\AGENTS.md"
    $env:COW_WORKTREES_HOME = "$dir\home-before"

    # ---- before: two projects set up, one of them with a worktree ---------------------------------------
    NewRepo "$dir\repo"; NewRepo "$dir\repo-gone"
    Ps1 "$tool\setup.ps1" @('-Repo', "$dir\repo", '-Root', $demoRoot, '-Name', 'FoundDemo', '-BaseRef', 'main', '-NoSchedule')
    Ps1 "$tool\setup.ps1" @('-Repo', "$dir\repo-gone", '-Root', $goneRoot, '-Name', 'FoundGone', '-BaseRef', 'main', '-NoSchedule', '-NoClaude', '-NoCodex')
    Ps1 "$tool\scripts\cow_worktree.ps1" @('-Root', $demoRoot, '-Name', 'one', '-Branch', 'one')
    Remove-Item -LiteralPath "$dir\repo-gone" -Recurse -Force          # this project was deleted since

    # ---- installed again: the app's own folder is new, the Codex text is gone ---------------------------
    $env:COW_WORKTREES_HOME = "$dir\home-after"
    Remove-Item -LiteralPath "$dir\codex\AGENTS.md" -Force

    function Run([string[]]$more) {
        $out = Join-Path $dir ('found-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.json')
        $p = Start-Process -FilePath $Exe -ArgumentList (@('--hidden', '--wizard', 'welcome', '--found-dump', "`"$out`"", '--size', '1300x900', '--exit') + $more) -PassThru
        if (-not $p.WaitForExit(240000)) { Stop-Process -Id $p.Id -Force; throw 'the app did not finish' }
        if (-not (Test-Path $out)) { throw "the app wrote no dump (see $env:COW_WORKTREES_HOME\gui.log)" }
        Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    }

    "what the walkthrough finds"
    $more = @(); if ($Shots) { New-Item -ItemType Directory -Force $Shots | Out-Null; $more = @('--theme', 'dark', '--snapshot', "`"$(Join-Path (Resolve-Path $Shots) 'wizard-found.png')`"") }
    $d = Run $more
    $demo = $d.found | Where-Object name -eq 'FoundDemo'; $gone = $d.found | Where-Object name -eq 'FoundGone'
    Check 'both setups from before are found, and nothing else' (@($d.found).Count -eq 2 -and $demo -and $gone) "found: $(($d.found | ForEach-Object name) -join ', ')"
    Check 'the first is read from its own settings: folder, project, one worktree' ($demo.root -eq $demoRoot -and $demo.repo -eq "$dir\repo" -and $demo.worktrees -eq 1 -and -not $demo.problem -and $demo.ticked) "$($demo | ConvertTo-Json -Compress)"
    Check 'the one whose project is gone says so and is not ticked' ($gone.problem -like '*not there any more*' -and -not $gone.ticked) "$($gone.problem)"
    Check 'looking changed nothing' (-not (Test-Path "$dir\home-after\pools.json") -and -not (Test-Path "$dir\codex\AGENTS.md") -and @($d.pools).Count -eq 0)

    "using it"
    $d = Run @('--wizard-adopt')
    Check 'the setup was added' ((($d.results | Where-Object name -eq 'FoundDemo').result -eq 'added') -and -not $d.status) "$($d.results | ConvertTo-Json -Compress) $($d.status)"
    Check 'the walkthrough closed and the app lists the repository' (-not $d.wizardOpen -and ($d.pools | Where-Object root -eq $demoRoot))
    $registry = Get-Content "$dir\home-after\pools.json" -Raw | ConvertFrom-Json
    Check 'it is in the new list of repositories, and only it' (@($registry.pools).Count -eq 1 -and $registry.pools[0].root -eq $demoRoot -and $registry.pools[0].name -eq 'FoundDemo')
    $hooks = Get-Content "$dir\repo\.claude\settings.local.json" -Raw
    Check 'Claude Code is connected to this copy of the tool' ($hooks -like '*WorktreeCreate*' -and $hooks.Replace('\\', '\') -like "*$tool\scripts\cc_worktree_hook.ps1*" -and $hooks.Replace('\\', '\') -like "*$demoRoot*")
    Check 'the Codex text is written again' ((Test-Path "$dir\codex\AGENTS.md") -and (Get-Content "$dir\codex\AGENTS.md" -Raw) -like '*cow-worktrees:FoundDemo begin*')
    Check 'no daily task was made, as it was set up without one' (-not (Get-ScheduledTask -TaskPath '\CowWorktrees\' -TaskName 'Refresh-FoundDemo' -ErrorAction SilentlyContinue))
    Check 'its worktree is still there, untouched' ((Test-Path "$demoRoot\one\README.md") -and (G -C "$dir\repo" worktree list) -match 'one')
    Check 'the unusable one was left alone' (-not ($registry.pools | Where-Object root -eq $goneRoot))

    "the next time"
    $d = Run @()
    Check 'the one that was added is not offered again' (@($d.found).Count -eq 1 -and $d.found[0].name -eq 'FoundGone') "found: $(($d.found | ForEach-Object name) -join ', ')"
}
finally {
    # the daily task is never made here; the worktrees and the base are removed with the folder
    Get-ScheduledTask -TaskPath '\CowWorktrees\' -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -in 'Refresh-FoundDemo', 'Refresh-FoundGone' } | Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item Env:COW_SCAN_ROOTS, Env:COW_CODEX_AGENTS_FILE, Env:COW_WORKTREES_HOME -ErrorAction SilentlyContinue
    if (-not $Keep -and (Test-Path -LiteralPath $dir)) {
        Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { if ($_.IsReadOnly) { $_.IsReadOnly = $false } }
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    } elseif ($Keep) { "kept: $dir" }
}
"$script:passed passed, $script:failed failed"
exit ([int]($script:failed -gt 0))
