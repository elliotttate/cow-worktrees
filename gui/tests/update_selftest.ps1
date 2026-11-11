#Requires -Version 5.1
<#
update_selftest.ps1 - checks the app's update check and download against a stand-in "latest release".

  update_selftest.ps1 [-Exe <CowWorktrees.exe>]

Serves a made-up release and a small stand-in file from http://127.0.0.1 on a free port, and runs the app
hidden with --update-feed and --update-result for each case. With a test feed the app verifies the download but
never starts it: it writes what it would have done to the result file. Nothing here talks to GitHub, and
COW_WORKTREES_HOME is a temporary folder, so the real settings and the real updates folder are not touched.

Cases: a newer version with the right checksum (verified, "would start"); a wrong checksum (refused, file
deleted); no checksum (refused); the same version, an older version and a skipped version (nothing offered);
an installer with another name (ignored); the server down (nothing offered, nothing shown, one log line);
installers that a finished update left in the updates folder (removed, except a newer one and one in use).
Exit code = number of failed checks.
#>
param([string]$Exe = (Join-Path $PSScriptRoot '..\publish\CowWorktrees.exe'))
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$failures = 0; $checks = 0
function Check([bool]$ok, [string]$what) { $script:checks++; if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ } }

$current = ((Get-Item $Exe).VersionInfo.ProductVersion -split '[+ ]')[0]
$work = Join-Path $env:TEMP ("cow-update-test-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $work | Out-Null
"app $Exe (version $current), work folder $work"

# A free port, then a listener in another runspace that answers from a table this script fills in.
$probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
$base = "http://127.0.0.1:$port"
$payload = [Text.Encoding]::ASCII.GetBytes('this stands in for an installer; it is never started')
$sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($payload) | ForEach-Object { $_.ToString('x2') })
$state = [hashtable]::Synchronized(@{ Json = '{}'; Payload = $payload; Stop = $false; Hits = 0 })

$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("$base/")
$listener.Start()
$rs = [runspacefactory]::CreateRunspace(); $rs.Open()
$rs.SessionStateProxy.SetVariable('listener', $listener); $rs.SessionStateProxy.SetVariable('state', $state)
$ps = [powershell]::Create(); $ps.Runspace = $rs
[void]$ps.AddScript({
    while ($listener.IsListening -and -not $state.Stop) {
        try { $ctx = $listener.GetContext() } catch { break }
        $state.Hits++
        try {
            if ($ctx.Request.Url.AbsolutePath -like '/download/*') {
                $bytes = $state.Payload; $ctx.Response.ContentType = 'application/octet-stream'
            } else {
                $bytes = [Text.Encoding]::UTF8.GetBytes($state.Json); $ctx.Response.ContentType = 'application/json'
            }
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $ctx.Response.Close()
        } catch { }
    }
})
$async = $ps.BeginInvoke()

function Release([string]$version, [string]$assetName, [string]$digest) {
    $asset = [ordered]@{ name = $assetName; browser_download_url = "$base/download/$assetName"; size = $payload.Length }
    if ($digest) { $asset.digest = $digest }
    return ([ordered]@{ tag_name = "v$version"; html_url = "https://github.com/elliotttate/cow-worktrees/releases/tag/v$version"; body = 'notes'; draft = $false; prerelease = $false; assets = @($asset) } | ConvertTo-Json -Depth 5)
}

$n = 0
function Run([string]$feed, [string]$skipped, [scriptblock]$Prepare) {
    $script:n++
    $cowHome = Join-Path $work "home$n"
    New-Item -ItemType Directory -Force $cowHome | Out-Null
    if ($Prepare) { & $Prepare $cowHome }
    if ($skipped) { Set-Content (Join-Path $cowHome 'gui.json') ('{ "SkippedVersion": "' + $skipped + '" }') -Encoding ASCII }
    $out = Join-Path $work "result$n.json"
    $env:COW_WORKTREES_HOME = $cowHome
    $p = Start-Process -FilePath $Exe -ArgumentList @('--hidden', '--update-feed', $feed, '--update-result', $out) -PassThru
    if (-not $p.WaitForExit(90000)) { Stop-Process -Id $p.Id -Force; throw 'the app did not finish within 90 s' }
    $r = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    $r | Add-Member -NotePropertyName Home -NotePropertyValue $cowHome
    return $r
}

try {
    '== newer version, right checksum'
    $state.Json = Release '99.1.0' 'CowWorktrees-Setup-99.1.0.exe' "sha256:$sha"
    $r = Run "$base/latest"
    Check ($r.offered -eq '99.1.0') "version 99.1.0 is offered (current $($r.current))"
    Check ($r.verified -eq $true -and $r.wouldStart -and (Test-Path -LiteralPath $r.wouldStart)) 'the download is verified and is the file that would be started'
    Check ($r.wouldStart -like (Join-Path $r.Home 'updates\*')) 'it was downloaded into the updates folder under COW_WORKTREES_HOME'
    Check (@(Get-Process | Where-Object { $_.Path -and $_.Path -like (Join-Path $work '*') }).Count -eq 0) 'nothing from the work folder was started'

    '== wrong checksum'
    $state.Json = Release '99.1.0' 'CowWorktrees-Setup-99.1.0.exe' ('sha256:' + ('0' * 64))
    $r = Run "$base/latest"
    Check ($r.offered -eq '99.1.0' -and $r.verified -eq $false -and -not $r.wouldStart) 'offered, but the download is refused'
    Check (-not $r.leftover -and -not (Test-Path (Join-Path $r.Home 'updates\CowWorktrees-Setup-99.1.0.exe'))) 'the file that failed the check was deleted'
    Check ($r.message -match 'could not be verified') "the message says it could not be verified: $($r.message)"

    '== no checksum in the release'
    $state.Json = Release '99.1.0' 'CowWorktrees-Setup-99.1.0.exe' ''
    $r = Run "$base/latest"
    Check ($r.offered -eq '99.1.0' -and $r.verified -eq $false -and -not $r.wouldStart -and -not $r.leftover) 'refused without downloading'

    '== same version'
    $state.Json = Release $current "CowWorktrees-Setup-$current.exe" "sha256:$sha"
    $r = Run "$base/latest"
    Check (-not $r.offered) "nothing is offered ($($r.why))"

    '== older version'
    $state.Json = Release '0.9.0' 'CowWorktrees-Setup-0.9.0.exe' "sha256:$sha"
    $r = Run "$base/latest"
    Check (-not $r.offered) "nothing is offered ($($r.why))"

    '== a version that was skipped'
    $state.Json = Release '99.1.0' 'CowWorktrees-Setup-99.1.0.exe' "sha256:$sha"
    $r = Run "$base/latest" '99.1.0'
    Check (-not $r.offered -and $r.why -match 'skipped') "nothing is offered ($($r.why))"

    '== an installer with another name'
    $state.Json = Release '99.1.0' 'Setup.exe' "sha256:$sha"
    $r = Run "$base/latest"
    Check ($r.offered -eq '99.1.0' -and $r.hasInstaller -eq $false -and -not $r.wouldStart) 'the version is offered without an installer; nothing is downloaded'

    '== installers a finished update left behind'
    $state.Json = Release $current "CowWorktrees-Setup-$current.exe" "sha256:$sha"
    $held = $null
    $r = Run "$base/latest" '' {
        param($cowHome)
        $u = Join-Path $cowHome 'updates'; New-Item -ItemType Directory -Force $u | Out-Null
        foreach ($f in "CowWorktrees-Setup-$current.exe", 'CowWorktrees-Setup-0.9.0.exe', 'CowWorktrees-Setup-0.8.0.exe', 'CowWorktrees-Setup-99.2.0.exe', 'notes.txt') {
            Set-Content -LiteralPath (Join-Path $u $f) -Value 'stands in for a file left here'
        }
        # as an installer that is still closing holds its own file
        $script:held = [IO.File]::Open((Join-Path $u 'CowWorktrees-Setup-0.8.0.exe'), 'Open', 'Read', 'None')
    }
    $u = Join-Path $r.Home 'updates'
    Check (-not (Test-Path "$u\CowWorktrees-Setup-$current.exe") -and -not (Test-Path "$u\CowWorktrees-Setup-0.9.0.exe")) 'the installer of the running version and an older one are removed'
    Check ((Test-Path "$u\CowWorktrees-Setup-99.2.0.exe") -and (Test-Path "$u\notes.txt")) 'a newer installer and a file of another kind stay'
    Check ((Test-Path "$u\CowWorktrees-Setup-0.8.0.exe") -and $r.oldInstallersInUse -eq 1) "one that is still in use is left for the next try (counted: $($r.oldInstallersInUse))"
    Check ((Get-Content (Join-Path $r.Home 'gui.log') -Raw) -match 'update: removed CowWorktrees-Setup-0\.9\.0\.exe') 'the removal is written to gui.log'
    if ($held) { $held.Close(); $held = $null }

    '== a feed that is not on this PC is ignored'
    $r = Run 'http://example.invalid/latest'
    Check ($r.error -match 'needs --update-feed') 'a feed outside 127.0.0.1 / localhost is not used'

    '== the server is down'
    $state.Stop = $true; $listener.Stop()
    $r = Run "$base/latest"
    Check (-not $r.offered -and $r.why -match 'failed') "nothing is offered and nothing is shown ($($r.why))"
    $log = Join-Path $r.Home 'gui.log'
    Check ((Test-Path $log) -and ((Get-Content $log -Raw) -match 'update check:')) 'the failure is written to gui.log'
} catch {
    $failures++
    "  FAIL  $($_.Exception.Message)"
} finally {
    $state.Stop = $true
    try { $listener.Stop(); $listener.Close() } catch { }
    try { $ps.Stop(); $ps.Dispose(); $rs.Close() } catch { }
    if ($held) { $held.Close() }
    Remove-Item Env:\COW_WORKTREES_HOME -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
"$checks checks, $failures failed"
exit $failures
