#Requires -Version 5.1
<#
ui_create_remove.ps1 - end-to-end check of the app's New worktree and Remove dialogs through UI Automation.

  ui_create_remove.ps1 -Root <pool root> [-CowHome <folder with pools.json>] [-Exe <CowWorktrees.exe>]

Starts the app hidden (off-screen, never activated, so nothing takes the keyboard) on the pool at -Root, then:
  1. New worktree dialog -> name gui-selftest-<time>, no branch -> Create; waits for the script to finish and
     checks git lists the worktree and the app shows its row.
  2. Puts an untracked file in it, refreshes, and asks the app to remove it: the script must refuse (the app
     never forces) and the worktree must still be there.
  3. Deletes that file, refreshes, removes again: the worktree must be gone from git and from disk.
Only the throwaway worktree this script makes is touched. Exit 0 = pass.
#>
param(
    [Parameter(Mandatory)][string]$Root,   # the pool to test in (its cow-config.json names the repository)
    [string]$CowHome,                      # folder holding pools.json, when not the default (COW_WORKTREES_HOME)
    [string]$Exe = (Join-Path $PSScriptRoot '..\CowWorktrees\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\CowWorktrees.exe'),
    [string]$CaptureDir                    # optional: save a capture of the window after the create
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [Windows.Automation.AutomationElement]
$clock = [Diagnostics.Stopwatch]::StartNew()
function Say([string]$m) { Write-Output ('{0,6:N1}s  {1}' -f $clock.Elapsed.TotalSeconds, $m) }

$CowRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$Repo = (Get-Content -LiteralPath (Join-Path $CowRoot 'cow-config.json') -Raw | ConvertFrom-Json).repo
if (-not $Repo) { throw "no repo in $CowRoot\cow-config.json" }
$name = 'gui-selftest-' + (Get-Date -Format 'HHmmss')
$path = Join-Path $CowRoot $name
if (Test-Path -LiteralPath $path) { throw "$path already exists" }

if ($CowHome) { $env:COW_WORKTREES_HOME = $CowHome }
$app = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList '--hidden', '--pool', $CowRoot -PassThru
Say "started the app hidden (pid $($app.Id)); pool $CowRoot, repository $Repo, test worktree $name"

function Window {
    $cond = New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
    for ($i = 0; $i -lt 100; $i++) {
        $w = $AE::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 200
    }
    throw 'app window not found'
}
function Find([string]$elName, [int]$seconds = 15, [switch]$Prefix) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $w = Window
        if ($Prefix) {
            $all = $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            foreach ($e in $all) { if ($e.Current.Name -like "$elName*") { return $e } }
        } else {
            $c = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $elName)
            $e = $w.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
            if ($e) { return $e }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "UI element '$elName' not found within $seconds s"
}
function Click($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return }
    if ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return }
    throw "element '$($el.Current.Name)' cannot be clicked"
}
function SetText($el, [string]$v) { $el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Tab([string]$t) { Click (Find $t) ; Start-Sleep -Milliseconds 300 }
function WaitScriptsDone([int]$seconds) {
    # The app runs cow_worktree.ps1 as a child powershell.exe; wait until it has started and exited.
    $deadline = (Get-Date).AddSeconds($seconds); $seen = $false
    while ((Get-Date) -lt $deadline) {
        $kids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id) AND Name='powershell.exe'")
        if ($kids.Count) { $seen = $true } elseif ($seen) { return }
        elseif (-not $seen -and ((Get-Date) -gt $deadline.AddSeconds(-$seconds + 10))) { return }   # finished before we looked
        Start-Sleep -Milliseconds 500
    }
    throw "the script did not finish within $seconds s"
}
function WaitRefreshed { $null = Find 'Updated ' 60 -Prefix; Start-Sleep -Milliseconds 800 }
function RefreshNow {
    # The status bar says "Updated HH:mm:ss ..."; wait for a newer one than before the click.
    $before = (Find 'Updated ' 60 -Prefix).Current.Name
    Click (Find 'Refresh')
    $deadline = (Get-Date).AddSeconds(120)
    while ((Find 'Updated ' 60 -Prefix).Current.Name -eq $before) {
        if ((Get-Date) -gt $deadline) { throw 'refresh did not finish' }
        Start-Sleep -Milliseconds 300
    }
}
function RowHas([string]$text, [int]$seconds = 20) {
    # The row's own subtree must contain an element named $text (other rows may say "clean" too).
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $cond = New-Object Windows.Automation.AndCondition(
            (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)),
            (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
        $row = (Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
        if ($row) {
            $c = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $text)
            if ($row.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)) { return }
        }
        Start-Sleep -Milliseconds 400
    }
    throw "the row $name does not show '$text'"
}
function Listed { @(git -C $Repo worktree list --porcelain) -contains ('worktree ' + $path.Replace('\', '/')) }
function SelectRow {
    Tab 'Worktrees'
    $deadline = (Get-Date).AddSeconds(30)
    $cond = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)),
        (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
    while (-not ($row = (Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $cond))) {
        if ((Get-Date) -gt $deadline) { throw "the app shows no row named $name" }
        Start-Sleep -Milliseconds 400
    }
    Click $row
    Start-Sleep -Milliseconds 500
}

$failed = $false
try {
    WaitRefreshed
    Say 'first refresh done'

    # ---- 1. create ---------------------------------------------------------------------------------
    Click (Find 'New worktree')
    $box = Find "Name (folder under $CowRoot)"
    SetText $box $name
    Start-Sleep -Milliseconds 500
    $create = Find 'Create'
    if (-not $create.Current.IsEnabled) { throw 'Create stayed disabled for a valid name' }
    Click $create
    Say 'Create clicked; waiting for cow_worktree.ps1'
    WaitScriptsDone 600
    if (-not (Listed)) { throw "after Create, git does not list $path" }
    if (-not (Test-Path -LiteralPath (Join-Path $path '.git'))) { throw "after Create, $path has no .git" }
    # After a create you were watching, the app returns to the list and says how it went in a banner.
    $summary = (Find "Create ${name}: done" 15 -Prefix).Current.Name
    Say "created: git lists it; the app's banner says '$summary'"
    if ($CaptureDir) { & (Join-Path $PSScriptRoot 'capture_window.ps1') -ProcessId $app.Id -Out (Join-Path $CaptureDir 'after_create.png') | Out-Null }
    RefreshNow
    SelectRow
    RowHas 'clean'
    Say "the app shows the row $name (clean)"

    # ---- 2. remove refused while dirty ------------------------------------------------------------
    Set-Content -LiteralPath (Join-Path $path 'gui-selftest-untracked.txt') -Value 'left by ui_create_remove.ps1'
    RefreshNow
    SelectRow
    RowHas '1 untracked'
    Say 'the app shows 1 untracked file in the row'
    Click (Find 'Remove worktree')
    Click (Find 'Remove' 10)
    Say 'Remove confirmed on a dirty worktree; expecting a refusal'
    WaitScriptsDone 300
    $refusal = (Find 'failed (exit 1)' 15 -Prefix).Current.Name
    if (-not (Listed) -or -not (Test-Path -LiteralPath $path)) { throw 'the dirty worktree was removed' }
    Say "refused as expected: '$refusal'; the worktree is still there"

    # ---- 3. remove when clean ------------------------------------------------------------------------
    Remove-Item -LiteralPath (Join-Path $path 'gui-selftest-untracked.txt')
    RefreshNow
    SelectRow
    RowHas 'clean'
    Click (Find 'Remove worktree')
    Click (Find 'Remove' 10)
    Say 'Remove confirmed on the clean worktree'
    WaitScriptsDone 600
    if (Listed) { throw "after Remove, git still lists $path" }
    if (Test-Path -LiteralPath $path) { throw "after Remove, $path still exists" }
    $summary = (Find "Remove ${name}: done" 15 -Prefix).Current.Name
    Say "removed: gone from git and disk; the app's banner says '$summary'"
    RefreshNow
    Tab 'Worktrees'
    Start-Sleep -Milliseconds 500
    $c = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    if ((Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $c)) { throw 'the app still shows the removed row' }
    Say 'the row is gone from the app'
    Say 'PASS'
} catch {
    $failed = $true
    Say "FAIL: $($_.Exception.Message)"
} finally {
    try { ((Window).GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close() } catch { }
    if (-not $app.WaitForExit(15000)) { Stop-Process -Id $app.Id -Force }
    if (Listed) { Say "NOTE: $path is still registered; remove it with cow_worktree.ps1 -Remove -Name $name" }
}
if ($failed) { exit 1 }
exit 0
