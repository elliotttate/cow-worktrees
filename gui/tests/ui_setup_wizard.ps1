#Requires -Version 5.1
<#
ui_setup_wizard.ps1 - walks the get-started walkthrough, Repository settings and removal through UI Automation.

  ui_setup_wizard.ps1 [-Exe <CowWorktrees.exe>] [-Drive E] [-CaptureDir <folder>]

Starts the published app hidden (off-screen, never activated) with an empty COW_WORKTREES_HOME, then, on a
throwaway repository and a throwaway folder on the Dev Drive:
  1. presses its way through every screen of the walkthrough to "Set up" and waits for "You're all set";
     checks pools.json, the locked base, the Claude Code hooks, the Codex section and the daily task, and that
     the main window shows the new repository;
  2. opens Repository settings, switches Codex off, presses Apply: the Codex section must be gone and the
     hooks still there;
  3. deletes the hooks file by hand, refreshes: the "needs attention" banner must show; presses Fix it: the
     hooks must be back;
  4. makes a full worktree, and moves it into a copy-on-write clone through the Move dialog: the old folder
     must become a junction, and its untracked file and branch must be in the clone;
  5. opens App settings and About from the More menu;
  6. removes the repository from Repository settings: registry entry, hooks, Codex section and task must be
     gone, and the base copy still on disk (the box for deleting it is left unticked).
Nothing of yours is touched: the registry, the Codex file and the settings are in temporary folders, and the
scheduled task the walkthrough registers is removed again. Exit 0 = pass.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe'),
    [string]$Drive,
    [string]$CaptureDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [Windows.Automation.AutomationElement]
$Exe = (Resolve-Path $Exe).Path
$clock = [Diagnostics.Stopwatch]::StartNew()
function Say([string]$m) { Write-Output ('{0,6:N1}s  {1}' -f $clock.Elapsed.TotalSeconds, $m) }
if (-not $Drive) { $Drive = (Get-Volume | Where-Object { $_.FileSystem -eq 'ReFS' -and $_.DriveLetter } | Select-Object -First 1).DriveLetter }
if (-not $Drive) { 'no ReFS volume found; pass -Drive'; exit 1 }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP "cow-wizard-$stamp"
$repoName = "wizard-$stamp"
$repo = Join-Path $work $repoName
$top = "${Drive}:\cow-wizard-$stamp"
$root = Join-Path $top $repoName
$base = Join-Path $root 'base'
$env:COW_WORKTREES_HOME = Join-Path $work 'home'
$env:COW_CODEX_AGENTS_FILE = Join-Path $work 'codex\AGENTS.md'
$env:COW_NO_UPDATE_CHECK = '1'
$poolsFile = Join-Path $env:COW_WORKTREES_HOME 'pools.json'
$taskPath = '\CowWorktrees\'; $taskName = "Refresh-$repoName"
$hooksFile = Join-Path $repo '.claude\settings.local.json'

New-Item -ItemType Directory -Force -Path $repo, $env:COW_WORKTREES_HOME | Out-Null
git -C $repo init -q -b main 2>&1 | Out-Null
git -C $repo config user.email 'wizard@example.invalid'; git -C $repo config user.name 'wizard test'
Set-Content (Join-Path $repo 'README.md') 'test'
git -C $repo add -A 2>&1 | Out-Null; git -C $repo commit -q -m 'first' 2>&1 | Out-Null

$app = Start-Process -FilePath $Exe -ArgumentList '--hidden', '--wizard', 'welcome' -PassThru
Say "started the app hidden (pid $($app.Id)); repository $repo, folder $root"

function Window {
    $cond = New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
    for ($i = 0; $i -lt 100; $i++) {
        $w = $AE::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 200
    }
    throw 'app window not found'
}
function Find([string]$what, [int]$seconds = 20, [switch]$ByName, [switch]$Optional) {
    $prop = if ($ByName) { $AE::NameProperty } else { $AE::AutomationIdProperty }
    $c = New-Object Windows.Automation.PropertyCondition($prop, $what)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $e = (Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 300
    }
    if ($Optional) { return $null }
    throw "UI element '$what' not found within $seconds s"
}
function Click($el) {
    # UI Automation now and then refuses one call ("Could not open the process token"); the same call works a moment later.
    for ($try = 1; ; $try++) {
        try {
            $pat = $null
            if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return }
            if ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return }
            throw "element '$($el.Current.Name)' cannot be clicked"
        } catch {
            if ($try -ge 3 -or "$_" -match 'cannot be clicked') { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}
function SetText($el, [string]$v) { $el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Text([string]$id, [int]$seconds = 20) { return (Find $id $seconds).Current.Name }
function WaitText([string]$id, [string]$pattern, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds); $last = ''
    while ((Get-Date) -lt $deadline) {
        $e = Find $id 5 -Optional
        if ($e) { $last = $e.Current.Name; if ($last -match $pattern) { return $last } }
        Start-Sleep -Milliseconds 300
    }
    throw "'$id' did not show /$pattern/ within $seconds s (it says: $last)"
}
function Next([int]$seconds = 30) {
    # Next is disabled until the screen is complete
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $b = Find 'Wizard.Next' 10
        if ($b.Current.IsEnabled) { Click $b; Start-Sleep -Milliseconds 600; return }
        Start-Sleep -Milliseconds 300
    }
    $why = (Find 'Wizard.Blocked' 2 -Optional); throw "Next stayed disabled: $(if ($why) { $why.Current.Name })"
}
function SetToggle($el, [bool]$on) {
    $t = $el.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if (($t.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -ne $on) { $t.Toggle(); Start-Sleep -Milliseconds 300 }
}
function FindToggle([string]$name, [int]$seconds = 20) {
    # a switch is the element with that name that can be toggled (texts carry the same name)
    $c = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($e in (Window).FindAll([Windows.Automation.TreeScope]::Descendants, $c)) {
            $p = $null
            if ($e.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { return $e }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "no switch named '$name'"
}
function Expand([string]$name, [int]$seconds = 15) {
    # the first element with that name that can be expanded and is collapsed
    $c = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($e in (Window).FindAll([Windows.Automation.TreeScope]::Descendants, $c)) {
            $p = $null
            if ($e.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) {
                if ($p.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { $p.Expand(); Start-Sleep -Milliseconds 400 }
                return
            }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "nothing named '$name' can be expanded"
}
function Capture([string]$name) {
    if ($CaptureDir) { & (Join-Path $PSScriptRoot 'capture_window.ps1') -ProcessId $app.Id -Out (Join-Path $CaptureDir "$name.png") | Out-Null }
}
function WaitUntil([scriptblock]$test, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $test) { return }; Start-Sleep -Milliseconds 500 }
    throw "$what (waited $seconds s)"
}
function Hooks([string]$checkout) {
    $f = Join-Path $checkout '.claude\settings.local.json'
    return ((Test-Path -LiteralPath $f) -and ((Get-Content -LiteralPath $f -Raw) -match 'cc_worktree_hook\.ps1'))
}
function CodexBlock { return ((Test-Path -LiteralPath $env:COW_CODEX_AGENTS_FILE) -and ((Get-Content -LiteralPath $env:COW_CODEX_AGENTS_FILE -Raw) -match [regex]::Escape("cow-worktrees:$repoName begin"))) }
function Task { return [bool](Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue) }
function Registered { return ((Test-Path $poolsFile) -and (@((Get-Content $poolsFile -Raw | ConvertFrom-Json).pools | Where-Object { $_.name -eq $repoName }).Count -eq 1)) }

$failed = $false
try {
    # ---- 1. the walkthrough ------------------------------------------------------------------------
    $null = Find 'Wizard.Next' 60
    Say 'the walkthrough is open on Welcome'
    # Welcome -> Check this PC. The window is seconds old: press again if the first press came too early.
    for ($try = 1; $try -le 3; $try++) {
        Next
        if (Find 'Wizard.CheckAgain' 6 -Optional) { break }
        if ($try -eq 3) { throw 'pressing Get started did not leave the Welcome screen' }
    }
    $head = WaitText 'Wizard.ChecksHeadline' 'ready|can be used' 60
    Say "Check this PC: $head"
    Next                                              # -> Choose your project
    SetText (Find 'Wizard.RepoPath') $repo
    $status = WaitText 'Wizard.RepoStatus' 'Git repository' 30
    Say "Choose your project: $status"
    Next                                              # -> Where worktrees live
    SetText (Find 'Wizard.RootPath') $root
    $status = WaitText 'Wizard.RootStatus' 'will be created|can be used' 30
    Say "Where worktrees live: $status"
    Next                                              # -> Connect your AI tools
    SetToggle (Find 'Wizard.Claude') $true
    SetToggle (Find 'Wizard.Codex') $true
    SetToggle (Find 'Wizard.Schedule') $true
    Say 'Connect your AI tools: all three switched on'
    Next                                              # -> Faster first builds
    Next                                              # -> Review
    Expand 'Show details'                             # the command line is behind it
    $cmd = Text 'Wizard.CommandLine' 20
    if ($cmd -notmatch 'setup\.ps1' -or $cmd -notmatch [regex]::Escape($root)) { throw "the command line on the review screen is not the setup command: $cmd" }
    Capture 'review'
    Next                                              # Set up
    Say 'Set up pressed; waiting for setup.ps1'
    $null = Find 'Wizard.AllSet' 300
    Capture 'all-set'
    Say "the walkthrough says: $((Find 'Wizard.AllSet').Current.Name)"

    if (-not (Registered)) { throw 'the repository is not in pools.json' }
    if (-not (Test-Path (Join-Path $base 'README.md'))) { throw "no base copy at $base" }
    if (-not (@(git -C $repo worktree list --porcelain) -match '^locked')) { throw 'the base is not locked' }
    if (-not (Hooks $repo) -or -not (Hooks $base)) { throw 'the Claude Code hooks are missing in the repository or the base' }
    if (-not (CodexBlock)) { throw 'the Codex section is missing' }
    if (-not (Task)) { throw 'the daily task is not registered' }
    Say 'set up: registry, locked base, hooks, Codex section and daily task are all there'

    Next                                              # Finish
    $row = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)),
        (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, 'base')))
    WaitUntil { [bool](Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $row) } 60 'the main window does not show the base of the new repository'
    Say 'the main window shows the new repository'

    # ---- 2. Repository settings: Codex off ---------------------------------------------------------
    Click (Find 'Main.RepositorySettings')
    $title = Text 'Settings.Title' 20
    if ($title -notmatch [regex]::Escape($repoName)) { throw "Repository settings opened for something else: $title" }
    Start-Sleep -Milliseconds 800
    SetToggle (FindToggle 'Codex') $false
    $apply = Find 'Settings.Apply'
    WaitUntil { (Find 'Settings.Apply').Current.IsEnabled } 10 'Apply stayed disabled after a change'
    Capture 'settings'
    Click (Find 'Settings.Apply')
    $result = WaitText 'Settings.Result' 'Saved|not work|.+' 180
    if ($result -ne 'Saved') { throw "Apply did not save: $result" }
    if (CodexBlock) { throw 'the Codex section is still there after switching Codex off' }
    if (-not (Hooks $repo) -or -not (Task)) { throw 'switching Codex off removed something else too' }
    Say 'Repository settings: Codex switched off; hooks and task untouched'
    Click (Find 'Settings.Back')
    Start-Sleep -Milliseconds 600

    # ---- 3. drift and Fix it -------------------------------------------------------------------------
    Remove-Item -LiteralPath $hooksFile -Force
    Click (Find 'Refresh' 20 -ByName)
    $fix = Find 'Main.FixDrift' 90
    Capture 'drift'
    Say 'the hooks file was deleted by hand; the banner shows'
    Click $fix
    WaitUntil { Hooks $repo } 180 'Fix it did not bring the hooks back'
    Say 'Fix it: the hooks are back'
    WaitUntil { -not @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id) AND Name='powershell.exe'").Count } 120 'the repair did not finish'
    Start-Sleep -Seconds 2

    # ---- 4. move a full worktree into a copy-on-write clone --------------------------------------------
    $plain = Join-Path $work 'plain'
    git -C $repo worktree add -q -b movebr $plain main 2>&1 | Out-Null
    Set-Content (Join-Path $plain 'untracked.txt') 'carry me'
    Click (Find 'Refresh' 20 -ByName)
    $plainRow = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)),
        (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, 'plain')))
    WaitUntil { [bool](Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $plainRow) } 60 'the app does not show the plain worktree'
    Click ((Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $plainRow))
    Start-Sleep -Milliseconds 600
    Click (Find 'Move to copy-on-write' 20 -ByName)
    $move = Find 'Move' 20 -ByName
    Start-Sleep -Milliseconds 800                       # the dialog looks at the target folder before it enables Move
    Capture 'move-dialog'
    if (-not $move.Current.IsEnabled) { throw 'Move stayed disabled in the dialog' }
    Click $move
    Say 'Move confirmed (with "remove the old folder" ticked); waiting for migrate_worktree.ps1'
    WaitUntil { (Test-Path -LiteralPath $plain) -and (Get-Item -LiteralPath $plain -Force).LinkType -eq 'Junction' } 300 'the old folder did not become a junction'
    $moved = Join-Path $root 'plain'
    if ((Get-Content (Join-Path $moved 'untracked.txt') -ErrorAction SilentlyContinue) -ne 'carry me') { throw 'the untracked file was not carried into the clone' }
    if ((git -C $moved symbolic-ref --short HEAD) -ne 'movebr') { throw 'the clone is not on the old worktree''s branch' }
    Say "moved: $plain is a junction to $moved, the file was carried, and the clone is on its branch"
    WaitUntil { -not @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id) AND Name='powershell.exe'").Count } 120 'the move did not finish'
    Start-Sleep -Seconds 2

    # ---- 5. App settings and About opens ---------------------------------------------------------------
    Click (Find 'Main.More')
    Click (Find 'Main.AppSettings' 10)
    $null = Find 'Check now' 15 -ByName
    $version = (Find 'Version ' 5 -ByName -Optional)
    Capture 'app-settings'
    Click (Find 'CloseButton' 10)                      # the dialog's own button (the window's is called Close too)
    Start-Sleep -Milliseconds 600
    Say 'App settings and About opened and closed'

    # ---- 6. remove -------------------------------------------------------------------------------------
    Click (Find 'Main.RepositorySettings')
    $null = Find 'Settings.Title' 20
    Start-Sleep -Milliseconds 800
    Click (Find 'Settings.Remove')
    Click (Find 'Remove' 15 -ByName)
    WaitUntil { -not (Registered) } 180 'the repository is still in pools.json after Remove'
    if ((Hooks $repo) -or (CodexBlock) -or (Task)) { throw 'Remove left hooks, the Codex section or the task behind' }
    if (-not (Test-Path (Join-Path $base 'README.md'))) { throw 'Remove deleted the base copy although the box was not ticked' }
    Say 'removed: registry entry, hooks, Codex section and task are gone; the base copy is still on disk'
    Say 'PASS'
} catch {
    $failed = $true
    Say "FAIL: $($_.Exception.Message)"
    try { Capture 'failure' } catch { }
    $log = Join-Path $env:COW_WORKTREES_HOME 'gui.log'
    if (Test-Path -LiteralPath $log) { Say 'the end of the app''s log:'; Get-Content -LiteralPath $log -Tail 12 | ForEach-Object { "          $_" } }
} finally {
    try { ((Window).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close() } catch { }
    if (-not $app.WaitForExit(15000)) { Stop-Process -Id $app.Id -Force }
    Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    $junction = Join-Path $work 'plain'
    if ((Test-Path -LiteralPath $junction) -and (Get-Item -LiteralPath $junction -Force).LinkType -eq 'Junction') { [IO.Directory]::Delete($junction, $false) }
    $movedClone = Join-Path $root 'plain'
    if (Test-Path -LiteralPath $movedClone) { git -C $repo worktree unlock $movedClone 2>$null; git -C $repo worktree remove --force $movedClone 2>$null }
    if (Test-Path -LiteralPath $base) { git -C $repo worktree unlock $base 2>$null; git -C $repo worktree remove --force $base 2>$null }
    foreach ($d in $top, $work) { if (Test-Path -LiteralPath $d) { cmd /c rd /s /q "\\?\$d" 2>$null } }
    Remove-Item Env:\COW_WORKTREES_HOME, Env:\COW_CODEX_AGENTS_FILE, Env:\COW_NO_UPDATE_CHECK -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 }
exit 0
