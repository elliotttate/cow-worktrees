#Requires -Version 5.1
# CowIntegrations.ps1 - what setup.ps1 adds outside the pool and uninstall.ps1 takes away again
# (dot-source it after CowCommon.ps1):
#   Claude Code   this tool's hook entries in <checkout>\.claude\settings.local.json
#   Codex         a marked block in the Codex instructions file (AGENTS.md)
#   daily task    registered by register_refresh_task.ps1; here only the question "at what time?"
# and how those two scripts read and write the files they share with people and other programs.
# setup.ps1 and uninstall.ps1 both use these functions, so adding and removing always agree on what is ours.
# A function that changes something writes one line per change to the pipeline (the caller prints it with
# Note); the Test- functions only look.
#
# The hook entries and the Codex block are the same as 1.0.0 wrote, so a setup made by one version can be
# updated or removed by another. When a later version writes something different, raise this number:
# setup.ps1 records it in cow-config.json (integrationRevision), which tells a pool that needs setup run
# again from one that is current. A config without the entry was written by 1.0.x.
$CowIntegrationRevision = 1

# ---- text files that are not only ours -----------------------------------------------------------------
# settings.local.json (Claude Code writes it), AGENTS.md (Codex and editors), .git\info\exclude (Git), and
# cow-config.json and pools.json (people edit them). Windows PowerShell 5.1 reads a file without a byte
# order mark as ANSI and writes "UTF8" with one: 1.0.x did that, and so turned every character outside
# ASCII in such a file into other characters. Here the bytes are read as they are, and the file goes back
# in the encoding it came in.
#
# Read-CowText returns Exists, Text, and Encoding (hand it to Write-CowText):
#   starts with a UTF-8 or UTF-16 byte order mark   that encoding, and the mark stays
#   no mark, valid UTF-8                             UTF-8 without a mark (what Claude Code, Codex and Git write)
#   no mark, not valid UTF-8                         the system's ANSI code page, as before: such bytes are
#                                                    not UTF-8, and decoding them as UTF-8 would destroy them
#   no file, or an empty one                         UTF-8 without a mark; with -NewWithBom, with one
function Read-CowText([string]$File, [switch]$NewWithBom) {
    # .NET resolves a relative path against the process's folder, not PowerShell's location.
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($File)
    $exists = [IO.File]::Exists($path)
    [byte[]]$bytes = @()
    if ($exists) { $bytes = [IO.File]::ReadAllBytes($path) }
    if (-not $bytes.Length) { return [pscustomobject]@{ Exists = $exists; Text = ''; Encoding = (New-Object Text.UTF8Encoding([bool]$NewWithBom)) } }
    $skip = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $enc = New-Object Text.UTF8Encoding($true); $skip = 3 }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { $enc = New-Object Text.UnicodeEncoding($false, $true); $skip = 2 }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) { $enc = New-Object Text.UnicodeEncoding($true, $true); $skip = 2 }
    else {
        $enc = New-Object Text.UTF8Encoding($false)
        try { [void](New-Object Text.UTF8Encoding($false, $true)).GetString($bytes) } catch { $enc = [Text.Encoding]::Default }
    }
    return [pscustomobject]@{ Exists = $true; Text = $enc.GetString($bytes, $skip, $bytes.Length - $skip); Encoding = $enc }
}

# Writes the whole file in the given encoding (which writes its byte order mark, if it has one).
function Write-CowText([string]$File, [string]$Text, $Encoding) {
    [IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($File), $Text, $Encoding)
}

# Appends one line to a text file without touching a byte that is there: after a line break (added in the
# file's own style when the last line has none), in the file's encoding.
function Add-CowLine([string]$File, [string]$Line) {
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($File)
    $was = Read-CowText $path
    $nl = if ($was.Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lead = if ($was.Text -and -not $was.Text.EndsWith("`n", [StringComparison]::Ordinal)) { $nl } else { '' }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $bytes = $was.Encoding.GetBytes("$lead$Line$nl")   # GetBytes adds no byte order mark
    $stream = [IO.File]::Open($path, [IO.FileMode]::Append)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

# ---- the pool registry ---------------------------------------------------------------------------------
# pools.json for the two scripts that change it (CowCommon.ps1's Read-CowPools is what every script reads
# it with). A new pools.json, like a new cow-config.json, still gets a byte order mark, as 1.0.x wrote it:
# CowCommon.ps1 reads both with Get-Content, which takes a file without one for ANSI.
function Read-CowRegistry {
    $was = Read-CowText (Get-CowPoolsFile) -NewWithBom
    $p = if ($was.Text.Trim()) { $was.Text | ConvertFrom-Json } else { [pscustomobject]@{ version = 1; toolDir = ''; pools = @() } }
    if (-not $p.pools) { $p | Add-Member -NotePropertyName pools -NotePropertyValue @() -Force }
    return [pscustomobject]@{ Was = $was; Pools = $p }
}

function Write-CowRegistry($Registry) {
    New-Item -ItemType Directory -Force -Path (Get-CowHome) | Out-Null
    $Registry.Pools.pools = @($Registry.Pools.pools)
    Write-CowText (Get-CowPoolsFile) ((ConvertTo-Json -InputObject $Registry.Pools -Depth 5) + "`r`n") $Registry.Was.Encoding
}

# ---- Claude Code hooks ---------------------------------------------------------------------------------
function New-CowHookCommand([string]$ScriptsDir, [string]$script, [string[]]$extra, [int]$timeout, [string]$status, [string]$if) {
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $h = [ordered]@{ type = 'command' }
    if ($if) { $h.if = $if }
    $h.command = $ps
    $h.args = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $ScriptsDir $script)) + $extra
    $h.timeout = $timeout
    if ($status) { $h.statusMessage = $status }
    return [pscustomobject]$h
}

# The entries for one pool, by hook event. $ScriptsDir is the tool's scripts folder.
function Get-CowClaudeHooks([string]$Root, [string]$ScriptsDir) {
    return [ordered]@{
        PreToolUse     = @(
            [pscustomobject]@{ matcher = 'Bash'; hooks = @(New-CowHookCommand $ScriptsDir 'cc_guard_hook.ps1' @('-Root', $Root) 30 $null 'Bash(*worktree*)') },
            [pscustomobject]@{ matcher = 'PowerShell'; hooks = @(New-CowHookCommand $ScriptsDir 'cc_guard_hook.ps1' @('-Root', $Root) 30 $null 'PowerShell(*worktree*)') })
        WorktreeCreate = @([pscustomobject]@{ hooks = @(New-CowHookCommand $ScriptsDir 'cc_worktree_hook.ps1' @('create', '-Root', $Root) 1800 'Cloning a copy-on-write worktree' $null) })
        WorktreeRemove = @([pscustomobject]@{ hooks = @(New-CowHookCommand $ScriptsDir 'cc_worktree_hook.ps1' @('remove', '-Root', $Root) 600 'Removing the worktree' $null) })
    }
}

# Is this hook entry one of ours? (Whatever pool or tool folder it names: a checkout has one set of them.)
function Test-CowHookEntry($entry) {
    foreach ($h in @($entry.hooks)) { if ((@($h.args) + @($h.command)) -match 'cc_(worktree|guard)_hook\.ps1') { return $true } }
    return $false
}

# Every checkout of the repository that is on disk: the main one, its linked worktrees, the base and the
# pool's clones.
function Get-CowCheckouts([string]$Repo) {
    return @(git -C $Repo worktree list --porcelain | Where-Object { $_ -like 'worktree *' } | ForEach-Object { ConvertTo-CowFullPath $_.Substring(9) } | Where-Object { Test-Path -LiteralPath $_ })
}

# A settings.local.json as an object: Was (Read-CowText's answer), Settings, and Ok. No file, an empty one
# and one that holds only "null" are an empty object. Ok is false when the file is something else than a
# JSON object: then it is not ours to rewrite, and Settings is $null.
function Read-CowClaudeSettings([string]$File) {
    $was = Read-CowText $File
    $s = $null; $ok = $true
    if ($was.Text.Trim()) { try { $s = $was.Text | ConvertFrom-Json -ErrorAction Stop } catch { $ok = $false } }
    if ($ok -and $null -eq $s) { $s = [pscustomobject]@{} }
    if ($ok -and $s -isnot [Management.Automation.PSCustomObject]) { $ok = $false; $s = $null }
    return [pscustomobject]@{ Was = $was; Settings = $s; Ok = $ok }
}

# Does the checkout's settings.local.json hold any of our entries? A file that cannot be read counts as no.
function Test-CowClaudeHooks([string]$Checkout) {
    $read = Read-CowClaudeSettings (Join-Path $Checkout '.claude\settings.local.json')
    if (-not $read.Ok -or -not $read.Settings.hooks) { return $false }
    foreach ($p in @($read.Settings.hooks.PSObject.Properties)) {
        foreach ($e in @($p.Value)) { if ($e -and (Test-CowHookEntry $e)) { return $true } }
    }
    return $false
}

# Merges our entries into one checkout's settings.local.json: entries of ours that are already there are
# replaced, every other hook and setting stays. Writes the file's path when it wrote the file.
function Set-CowClaudeHooks([string]$Checkout, [string]$Root, [string]$ScriptsDir) {
    $ourHooks = Get-CowClaudeHooks $Root $ScriptsDir
    $dir = Join-Path $Checkout '.claude'
    $file = Join-Path $dir 'settings.local.json'
    $read = Read-CowClaudeSettings $file
    if (-not $read.Ok) { "warning: $file is not a JSON object; left as it is, without the hooks"; return }
    $s = $read.Settings
    $before = ConvertTo-Json -InputObject $s -Depth 20
    if (-not $s.hooks) { $s | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) -Force }
    foreach ($evt in $ourHooks.Keys) {
        $kept = @($s.hooks.$evt | Where-Object { $_ -and -not (Test-CowHookEntry $_) })
        if ($evt -like 'Worktree*' -and $kept.Count) { "warning: $file already has another $evt hook; Claude Code runs both" }
        $s.hooks | Add-Member -NotePropertyName $evt -NotePropertyValue (@($kept) + @($ourHooks[$evt])) -Force
    }
    $json = ConvertTo-Json -InputObject $s -Depth 20
    # Written only when what the file says would change (compared as JSON, so another program's spacing is
    # no reason either). setup.ps1 goes through every checkout on every run, and most have the hooks
    # already: writing them again would touch files in worktrees nobody is working in. A worktree's idle
    # time, which decides when prune may remove it, is read from its folder's and its index's times; a file
    # rewritten in place moves neither, but a .claude folder created in the worktree does, so one is made
    # only when there is something to put in it.
    $wrote = $false
    if ($json -cne $before) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-CowText $file ($json + "`r`n") $read.Was.Encoding
        $wrote = $true
    }
    # settings.local.json must stay out of Git; exclude it locally if the repository does not ignore it.
    # (Exit code 1 is "not ignored". Anything higher is Git failing in this checkout: nothing to add then.)
    git -C $Checkout check-ignore -q .claude/settings.local.json 2>$null
    if ($LASTEXITCODE -eq 1) {
        $exclude = Join-Path (git -C $Checkout rev-parse --path-format=absolute --git-common-dir) 'info\exclude'
        Add-CowLine $exclude '.claude/settings.local.json'
        "added .claude/settings.local.json to $exclude"
    }
    if ($wrote) { $file }
}

# Takes our entries out of one checkout's settings.local.json and leaves every other hook and setting alone.
# Writes the file only when something of ours was in it.
function Remove-CowClaudeHooks([string]$Checkout) {
    $file = Join-Path $Checkout '.claude\settings.local.json'
    $read = Read-CowClaudeSettings $file
    if (-not $read.Was.Exists -or -not $read.Ok) { return }
    $s = $read.Settings
    if (-not $s.hooks) { return }
    $changed = $false
    foreach ($p in @($s.hooks.PSObject.Properties)) {
        $kept = @($p.Value | Where-Object { -not (Test-CowHookEntry $_) })
        if ($kept.Count -ne @($p.Value).Count) { $changed = $true }
        if ($kept.Count) { $p.Value = $kept } else { $s.hooks.PSObject.Properties.Remove($p.Name) }
    }
    if (-not @($s.hooks.PSObject.Properties).Count) { $s.PSObject.Properties.Remove('hooks') }
    if ($changed) { Write-CowText $file ((ConvertTo-Json -InputObject $s -Depth 20) + "`r`n") $read.Was.Encoding; "cleaned $file" }
}

# ---- Codex ---------------------------------------------------------------------------------------------
# Which instructions file: the one passed, else COW_CODEX_AGENTS_FILE (the tests and the app's tests set it,
# so that they never touch the real file), else the one Codex reads.
function Resolve-CowCodexFile([string]$File) {
    if ($File) { return $File }
    if ($env:COW_CODEX_AGENTS_FILE) { return $env:COW_CODEX_AGENTS_FILE }
    return (Join-Path $env:USERPROFILE '.codex\AGENTS.md')
}

# Where the pool's marked block is in a text: Begin and End (the markers), and At and Past (the block's
# first character and the one after its end marker; At is -1 when the block is not there).
function Find-CowCodexBlock([string]$Text, [string]$Name) {
    $begin = "<!-- cow-worktrees:$Name begin -->"; $end = "<!-- cow-worktrees:$Name end -->"
    $i = $Text.IndexOf($begin, [StringComparison]::Ordinal); $j = $Text.IndexOf($end, [StringComparison]::Ordinal)
    if ($i -ge 0 -and $j -gt $i) { return [pscustomobject]@{ Begin = $begin; End = $end; At = $i; Past = $j + $end.Length } }
    return [pscustomobject]@{ Begin = $begin; End = $end; At = -1; Past = -1 }
}

# Is the pool's marked block in the file?
function Test-CowCodexBlock([string]$File, [string]$Name) {
    return ((Find-CowCodexBlock (Read-CowText $File).Text $Name).At -ge 0)
}

# Writes the pool's block (templates\codex-AGENTS.md of the tool folder, filled in) between its markers:
# in place when the block is there, else at the end after one empty line. Everything else in the file
# stays byte for byte: its text, its line breaks, its encoding. The file is written only when it changes.
function Set-CowCodexBlock([string]$File, [string]$Name, [string]$Repo, [string]$Root, [string]$ToolDir) {
    $scripts = Join-Path $ToolDir 'scripts'
    $template = (Read-CowText (Join-Path $ToolDir 'templates\codex-AGENTS.md')).Text
    $block = $template.Replace('{name}', $Name).Replace('{repo}', $Repo).Replace('{root}', $Root).Replace('{script}', (Join-Path $scripts 'cow_worktree.ps1')).Replace('{migrate}', (Join-Path $scripts 'migrate_worktree.ps1'))
    $was = Read-CowText $File
    $text = $was.Text
    $at = Find-CowCodexBlock $text $Name
    # Line breaks as the file has them; a new file gets what 1.0.x gave it.
    $nl = if ($text.Contains("`n") -and -not $text.Contains("`r`n")) { "`n" } else { "`r`n" }
    $section = "$($at.Begin)$nl$($block.Trim())$nl$($at.End)"
    if ($at.At -ge 0) { $new = $text.Substring(0, $at.At) + $section + $text.Substring($at.Past) }
    else {
        $lead = if (-not $text) { '' } elseif ($text.EndsWith("`n", [StringComparison]::Ordinal)) { $nl } else { $nl + $nl }
        $new = $text + $lead + $section + $nl
    }
    if (-not $was.Exists -or $new -cne $text) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($File)) | Out-Null
        Write-CowText $File $new $was.Encoding
    }
    $File
}

# Takes the pool's block out of the file, with the line break after it and the empty line setup put in
# front of it. Everything else stays byte for byte.
function Remove-CowCodexBlock([string]$File, [string]$Name) {
    $was = Read-CowText $File
    $at = Find-CowCodexBlock $was.Text $Name
    if ($at.At -lt 0) { return }
    $before = $was.Text.Substring(0, $at.At); $after = $was.Text.Substring($at.Past)
    if ($after.StartsWith("`r`n", [StringComparison]::Ordinal)) { $after = $after.Substring(2) } elseif ($after.StartsWith("`n", [StringComparison]::Ordinal)) { $after = $after.Substring(1) }
    $empty = [regex]::Match($before, '\n(\r?\n)\z')
    if ($empty.Success) { $before = $before.Substring(0, $before.Length - $empty.Groups[1].Length) }
    Write-CowText $File ($before + $after) $was.Encoding
    "removed the block from $File"
}

# ---- daily task ----------------------------------------------------------------------------------------
# The time of day the pool's daily task is registered for, as HH:mm; $null when there is no task (or its
# trigger has no start time).
function Get-CowTaskTime([string]$Name) {
    $task = Get-ScheduledTask -TaskPath $CowTaskPath -TaskName (Get-CowTaskName $Name) -ErrorAction SilentlyContinue
    if (-not $task) { return $null }
    $start = @($task.Triggers | Where-Object { $_.StartBoundary } | ForEach-Object { $_.StartBoundary }) | Select-Object -First 1
    if (-not $start) { return $null }
    # The boundary is a date and time, with or without a UTC offset; the clock time as written is the answer.
    try { return ([DateTimeOffset]::Parse($start, [Globalization.CultureInfo]::InvariantCulture)).ToString('HH:mm', [Globalization.CultureInfo]::InvariantCulture) } catch { return $null }
}
