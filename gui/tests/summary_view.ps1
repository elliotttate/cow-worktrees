#Requires -Version 5.1
<#
summary_view.ps1 - checks the details pane's summary (written by Claude or Codex) and its commit and file rows.

Nothing is sent anywhere: COW_CLAUDE_CLI and COW_CODEX_CLI point the app at stand-ins made here, which record
what they were given and answer with a fixed text. Builds a throwaway setup with make_test_pool.ps1 under
%TEMP% (removed afterwards), runs the app hidden with --summarize and --summary-dump, and compares.

  summary_view.ps1 [-Exe <CowWorktrees.exe>] [-Shots <folder>] [-Keep]

-Shots also saves a picture of the main window with the summary, in the light and in the dark theme.
Exit 0 when every check passes.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe'),
    [string]$Shots,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$dir = Join-Path $env:TEMP ('cow-summary-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:failed = 0; $script:passed = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; "  ok    $what" } else { $script:failed++; "  FAIL  $what $detail" }
}

try {
    $cowHome = & (Join-Path $PSScriptRoot 'make_test_pool.ps1') -Dir $dir | Select-Object -Last 1
    $stub = Join-Path $dir 'stub'; New-Item -ItemType Directory -Force $stub | Out-Null
    # The stand-in: keeps the text it was given and its arguments, then answers as the real tool would
    # (Claude on standard output, Codex into the file named after -o).
    @'
$tool = $args[0]; $rest = @($args | Select-Object -Skip 1)
$in = [Console]::In.ReadToEnd()
Add-Content -LiteralPath "$env:COW_STUB_DIR\calls.txt" -Value (([regex]::Match($in, '(?m)^name: (.+)$')).Groups[1].Value.Trim())
[IO.File]::WriteAllText("$env:COW_STUB_DIR\$tool-prompt.txt", $in)
[IO.File]::WriteAllText("$env:COW_STUB_DIR\$tool-args.txt", ($rest -join "`n"))
if ($env:COW_STUB_FAIL) { [Console]::Error.WriteLine('You are not signed in.'); exit 3 }
$reply = "Adds the agent's first piece of work ($tool).`n- Adds agent.txt with the agent's notes`n- Keeps the README as it was`n- **Leaves** the engine alone`nOpen: the commit is not pushed yet."
$o = [Array]::IndexOf($rest, '-o')
if ($o -ge 0) { [IO.File]::WriteAllText($rest[$o + 1], $reply); 'a log line that is not the reply' } else { $reply }
'@ | Set-Content -LiteralPath "$stub\stub.ps1" -Encoding ASCII
    foreach ($t in 'claude', 'codex') {
        "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0stub.ps1`" $t %*" | Set-Content -LiteralPath "$stub\$t-stub.cmd" -Encoding ASCII
    }

    function Run([string]$select, [string]$summarize, [string[]]$more = @()) {
        $out = Join-Path $dir ('dump-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.json')
        $env:COW_WORKTREES_HOME = $cowHome; $env:COW_NO_UPDATE_CHECK = '1'; $env:COW_STUB_DIR = $stub
        $argList = @('--hidden', '--pool', 'demo', '--select', $select, '--summary-dump', "`"$out`"", '--size', '1500x950', '--exit') + $more
        if ($summarize) { $argList += @('--summarize', $summarize) }
        $p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
        if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force; throw "the app did not finish (--summarize $summarize)" }
        if (-not (Test-Path $out)) { throw "the app wrote no dump (see $cowHome\gui.log)" }
        Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    }

    $env:COW_CLAUDE_CLI = "$stub\claude-stub.cmd"; $env:COW_CODEX_CLI = "$stub\codex-stub.cmd"; Remove-Item Env:COW_STUB_FAIL -ErrorAction SilentlyContinue

    "a summary by Claude"
    $d = Run 'worktree-agent1' 'claude'
    Check 'both tools were found' ($d.claude -and $d.codex)
    Check 'no error' (-not $d.error) $d.error
    Check 'it is by Claude' ($d.tool -eq 'Claude' -and $d.meta -like 'Written by Claude*') "$($d.tool) / $($d.meta)"
    Check 'the first line is the headline' ($d.headline -eq "Adds the agent's first piece of work (claude).") $d.headline
    Check 'three points, without Markdown marks' (@($d.points).Count -eq 3 -and $d.points[2] -eq 'Leaves the engine alone') "$(@($d.points) -join ' | ')"
    Check 'the "Open:" line is kept apart' ($d.open -eq 'the commit is not pushed yet.') $d.open
    Check 'it is not marked as out of date' (-not $d.stale)
    $prompt = Get-Content "$stub\claude-prompt.txt" -Raw
    Check 'the tool was given the worktree, its commit and its file' ($prompt -like '*name: worktree-agent1*' -and $prompt -like '*agent commit, not pushed*' -and $prompt -like '*agent.txt*' -and $prompt -like '*+agent work*')
    Check 'the tool was told what to write' ($prompt -like 'Summarize the work in one Git worktree*' -and $prompt -like '*Open: *')
    $toolArgs = Get-Content "$stub\claude-args.txt"
    Check 'Claude runs once, without tools' ($toolArgs -contains '-p' -and $toolArgs -contains '--tools') "$($toolArgs -join ' ')"
    Check 'the commit is a row' (@($d.commits).Count -eq 1 -and $d.commits[0].Subject -eq 'agent commit, not pushed' -and $d.commits[0].Sha.Length -ge 7 -and $d.commits[0].When) "$($d.commits | ConvertTo-Json -Compress)"
    Check 'the summary was saved' ((Test-Path "$cowHome\summaries.json") -and (Get-Content "$cowHome\summaries.json" -Raw) -like '*worktree-agent1*')

    "a summary by Codex"
    $d = Run 'feature-a' 'codex'
    Check 'it is by Codex, read from the file Codex writes' ($d.tool -eq 'Codex' -and $d.headline -eq "Adds the agent's first piece of work (codex)." -and -not $d.error) "$($d.tool): $($d.headline) $($d.error)"
    $toolArgs = Get-Content "$stub\codex-args.txt"
    Check 'Codex runs read-only, outside any repository' ($toolArgs -contains 'exec' -and $toolArgs -contains 'read-only' -and $toolArgs -contains '--skip-git-repo-check') "$($toolArgs -join ' ')"
    Check 'it was given the uncommitted file' ((Get-Content "$stub\codex-prompt.txt" -Raw) -like '*README.md*')
    $readme = $d.changes | Where-Object Path -eq 'README.md'; $notes = $d.changes | Where-Object Path -eq 'notes.txt'
    Check 'the changed and the new file are rows' ($readme.Badge -eq 'M' -and $notes.Badge -eq 'N' -and $notes.kind -eq 'Untracked') "$($d.changes | ConvertTo-Json -Compress)"
    Check 'the numbers are shown' ($d.numbers.changed -eq '1' -and $d.numbers.untracked -eq '1' -and $d.numbers.unpushed -eq '0') "$($d.numbers | ConvertTo-Json -Compress)"

    "the next start"
    $d = Run 'worktree-agent1' ''
    Check 'the saved summary is shown again' ($d.tool -eq 'Claude' -and $d.headline -like "Adds the agent's first*" -and @($d.points).Count -eq 3)
    Check 'the button uses the tool chosen last' ($d.defaultTool -eq 'Codex') $d.defaultTool
    Add-Content "$dir\pool\worktree-agent1\agent.txt" 'more work'
    $d = Run 'worktree-agent1' ''
    Check 'after an edit it says the worktree has changed since' ($d.stale -and $d.meta -like '*has changed since*') $d.meta

    "kept up to date by itself"
    Remove-Item "$stub\calls.txt" -ErrorAction SilentlyContinue
    $d = Run 'base' 'auto'
    $calls = @(Get-Content "$stub\calls.txt" -ErrorAction SilentlyContinue)
    $byName = @{}; foreach ($r in $d.all) { $byName[$r.name] = $r }
    Check 'one pass writes the summaries that are missing or out of date' (($calls | Sort-Object) -join ',' -eq 'repo,worktree-agent1') "written for: $($calls -join ', ')"
    Check 'every worktree with work of its own now has a current summary' ($byName['worktree-agent1'].headline -and -not $byName['worktree-agent1'].stale -and $byName['feature-a'].headline -and -not $byName['feature-a'].stale -and $byName['repo'].headline)
    Check 'the base and a worktree without work get none' (-not $byName['base'].headline -and -not $byName['phantom-a'].headline) "base '$($byName['base'].headline)' phantom-a '$($byName['phantom-a'].headline)'"
    Remove-Item "$stub\calls.txt"
    $d = Run 'base' 'auto'
    Check 'a second pass writes nothing: all are up to date' (-not (Test-Path "$stub\calls.txt"))
    Set-Content "$dir\pool\feature-a\another.txt" 'a new file'
    $d = Run 'base' 'auto'
    $calls = @(Get-Content "$stub\calls.txt" -ErrorAction SilentlyContinue)
    Check 'after a change only that worktree is written again' ($calls.Count -eq 1 -and $calls[0] -eq 'feature-a') "written for: $($calls -join ', ')"
    Check 'the option itself stays off until it is switched on' (-not $d.auto)

    "when it goes wrong"
    $env:COW_STUB_FAIL = '1'
    $d = Run 'phantom-a' 'claude'
    Check 'a failing tool gives its own words, and no summary' ($d.error -like 'Claude could not write the summary:*not signed in*' -and -not $d.tool) $d.error
    Remove-Item Env:COW_STUB_FAIL
    $env:COW_CLAUDE_CLI = "$stub\missing.exe"; $env:COW_CODEX_CLI = "$stub\missing.exe"
    $d = Run 'phantom-a' 'codex'
    Check 'a tool that is not installed is said so' (-not $d.claude -and -not $d.codex -and $d.error -like 'Codex was not found on this PC*') $d.error
    $env:COW_CLAUDE_CLI = "$stub\claude-stub.cmd"; $env:COW_CODEX_CLI = "$stub\codex-stub.cmd"

    if ($Shots) {
        New-Item -ItemType Directory -Force $Shots | Out-Null
        foreach ($theme in 'dark', 'light') {
            $png = Join-Path (Resolve-Path $Shots) "details-$theme.png"
            Run 'worktree-agent1' '' @('--theme', $theme, '--snapshot', "`"$png`"") | Out-Null
            Check "a picture of the $theme theme was saved" (Test-Path $png)
        }
    }
}
finally {
    Remove-Item Env:COW_CLAUDE_CLI, Env:COW_CODEX_CLI, Env:COW_STUB_DIR, Env:COW_STUB_FAIL -ErrorAction SilentlyContinue
    if (-not $Keep -and (Test-Path -LiteralPath $dir)) {
        Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { if ($_.IsReadOnly) { $_.IsReadOnly = $false } }
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    } elseif ($Keep) { "kept: $dir" }
}
"$script:passed passed, $script:failed failed"
exit ([int]($script:failed -gt 0))
