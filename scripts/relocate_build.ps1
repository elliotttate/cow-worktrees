#Requires -Version 5.1
<#
relocate_build.ps1 - point MSBuild's incremental-build state in a cloned worktree at the clone.

A clone of a built base carries the base's build output. MSBuild decides what is up to date from text
files that name absolute paths: *.tlog (file tracking, UTF-16, upper-cased paths), *.lastbuildstate and
*.recipe. Rewriting the base's path to the clone's in those files lets the clone build incrementally.
Binary outputs (.obj, .pdb, .pch, ...) keep the base's path in their debug info; that only matters to a
debugger opening source files, which then shows the base's copy of unchanged files.
Encoding, letter case and each file's timestamp are preserved. Only ignored (build) paths are searched.
#>
param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$To,
    # Paths of the state files relative to the worktree, recorded when the base was built
    # (build_base.ps1 -> base-build-state.txt). Saves asking Git for ignored paths and walking them.
    [string]$StateList,
    # Only print the relative paths of the state files found under -To, one per line, and change nothing.
    [switch]$ListOnly
)
$ErrorActionPreference = 'Continue'
$fromFull = [IO.Path]::GetFullPath($From).TrimEnd('\')
$toFull = [IO.Path]::GetFullPath($To).TrimEnd('\')
# MSBuild writes the path as given or upper-cased (tlogs). Plain ordinal replacements of each spelling,
# each ending in a separator so "base" cannot match "basement": fast on multi-megabyte tlogs, where a
# regex with a per-match PowerShell callback took most of a minute.
$pairs = @()
foreach ($case in 'as-is', 'upper', 'lower') {
    $f = switch ($case) { 'as-is' { $fromFull } 'upper' { $fromFull.ToUpperInvariant() } 'lower' { $fromFull.ToLowerInvariant() } }
    $t = switch ($case) { 'as-is' { $toFull } 'upper' { $toFull.ToUpperInvariant() } 'lower' { $toFull.ToLowerInvariant() } }
    foreach ($sep in '\', '/') { $pairs += , @(($f.Replace('\', $sep) + $sep), ($t.Replace('\', $sep) + $sep)) }
}

# Filter by extension explicitly: PowerShell 5.1 ignores -Include together with -LiteralPath, and
# rewriting a binary (.pdb, .obj) as text corrupts it. State files are small; refuse anything large.
$stateExtensions = @('.tlog', '.lastbuildstate', '.recipe')
if ($StateList -and (Test-Path -LiteralPath $StateList)) {
    $files = @(Get-Content -LiteralPath $StateList | Where-Object { $_ } |
        ForEach-Object { Join-Path $To $_ } | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-Item -LiteralPath $_ -Force })
} else {
    # Build state lives only in ignored paths; list them instead of walking 119k tracked files.
    $roots = @(git -C $To status --porcelain --ignored=matching 2>$null | Where-Object { $_ -like '!! *' } |
        ForEach-Object { Join-Path $To ($_.Substring(3).Trim('"').Replace('/', '\')) })
    $files = @(foreach ($r in $roots) {
        if (Test-Path -LiteralPath $r -PathType Container) {
            Get-ChildItem -LiteralPath $r -Recurse -File -Force -ErrorAction SilentlyContinue
        } elseif (Test-Path -LiteralPath $r) { Get-Item -LiteralPath $r -Force }
    })
}
$files = @($files | Where-Object { $stateExtensions -contains $_.Extension.ToLowerInvariant() -and $_.Length -lt 16MB })
if ($ListOnly) {
    $prefix = [IO.Path]::GetFullPath($To).TrimEnd('\') + '\'
    $files | ForEach-Object { $_.FullName.Substring($prefix.Length) }
    exit 0
}

$changed = 0
foreach ($f in $files) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { $enc = New-Object Text.UnicodeEncoding($false, $true); $skip = 2 }
    elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $enc = New-Object Text.UTF8Encoding($true); $skip = 3 }
    elseif ($bytes.Length -ge 2 -and $bytes[1] -eq 0) { $enc = New-Object Text.UnicodeEncoding($false, $false); $skip = 0 }
    else { $enc = New-Object Text.UTF8Encoding($false); $skip = 0 }
    $text = $enc.GetString($bytes, $skip, $bytes.Length - $skip)
    $new = $text
    foreach ($p in $pairs) { $new = $new.Replace($p[0], $p[1]) }
    if ($new -ceq $text) { continue }
    $stamp = $f.LastWriteTimeUtc
    # Stream the preamble and body: adding byte arrays with + would box every byte in PowerShell.
    $preamble = $enc.GetPreamble(); $body = $enc.GetBytes($new)
    $stream = [IO.File]::Create($f.FullName)
    try { $stream.Write($preamble, 0, $preamble.Length); $stream.Write($body, 0, $body.Length) } finally { $stream.Dispose() }
    [IO.File]::SetLastWriteTimeUtc($f.FullName, $stamp)
    $changed++
}
[Console]::Error.WriteLine("relocate_build: rewrote $changed of $(@($files).Count) build-state files from $fromFull to $toFull")
exit 0
