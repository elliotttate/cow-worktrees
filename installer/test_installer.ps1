#Requires -Version 5.1
<#
test_installer.ps1 - installs a TEST build of the installer into a temporary folder, installs it again
over itself, uninstalls it, and checks that Windows is left as it was.

  powershell -NoProfile -ExecutionPolicy Bypass -File installer\test_installer.ps1 [-AppDir <folder>] [-CheckApp] [-CheckLaunch] [-CheckDisconnect] [-Keep]

Touches nothing of yours. The test build (release.ps1 -TestBuild, ISCC /DTestBuild=1) has its own AppId and
mutex name, writes no uninstall entry to the registry, makes no shortcut and refuses to run without /DIR=;
the release build is never run here. The pool registry (COW_WORKTREES_HOME), the install folder, Setup's
own temporary files and its logs are all in one folder under %TEMP%, removed at the end.

  1. release.ps1 -Version 0.0.1-test -TestBuild into the temporary folder
  2. Setup /VERYSILENT /SUPPRESSMSGBOXES /NOLAUNCH /NOICONS /DIR=<temp>\install
  3. the installed files are the staged folder (the one the zip is made from) plus installed.marker and
     the uninstaller; the app was not started
  4. -CheckApp: the installed app finds its tool folder (CowWorktrees.exe --hidden --selftest-checks <json>)
  5. Setup again over the first install, while a stand-in holds the app's mutex for a few seconds: a silent
     Setup has to wait for the app to close, as it must when the app updates itself
  6. unins000.exe /VERYSILENT /KEEPINTEGRATIONS: the folder is gone and no pool was disconnected
  7. -CheckLaunch and -CheckDisconnect install once more:
     -CheckLaunch      without /NOLAUNCH: Setup must start the app, also when silent (the in-app updater
                       relies on it). A test build starts it with --hidden, and the test ends it.
     -CheckDisconnect  then uninstalls without /KEEPINTEGRATIONS: the uninstaller must run
                       scripts\disconnect_all.ps1. The pool registry it sees is the temporary one, which
                       names one pool that does not exist, so there is nothing it could disconnect.
  8. nothing named cow-worktrees was added under ...\CurrentVersion\Uninstall, to the Start menu or the
     desktop; the real install folder, the real pool registry and %TEMP% are as before

-AppDir is the app's publish folder; the default is gui\publish (run gui\build.ps1 first). The app is not
built here. -Keep leaves the temporary folder (logs included) for a look. Exit code = number of failed checks.
#>
param([string]$AppDir, [switch]$CheckApp, [switch]$CheckLaunch, [switch]$CheckDisconnect, [switch]$Keep)
$ErrorActionPreference = 'Continue'
$top = Split-Path -Parent $PSScriptRoot
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$version = '0.0.1-test'
# These two are the test build's values in CowWorktrees.iss.
$testAppId = '08F9EE69-BA4B-4CD4-B6C3-9C3B207B22B3'
$testMutex = 'cow-worktrees.app.test'
$failures = 0; $checks = 0
function Check([bool]$ok, [string]$what) { $script:checks++; if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ } }
function Section([string]$s) { "== $s" }
function Wait-Until([scriptblock]$Test, [int]$Seconds) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) { if (& $Test) { return $true }; Start-Sleep -Milliseconds 250 }
    return [bool](& $Test)
}

if (-not $AppDir) { $AppDir = Join-Path $top 'gui\publish' }
if (-not (Test-Path -LiteralPath (Join-Path $AppDir 'CowWorktrees.exe'))) { "no CowWorktrees.exe in $AppDir; run gui\build.ps1 first, or pass -AppDir"; exit 1 }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path ([IO.Path]::GetTempPath().TrimEnd('\')) "cow-installer-test-$stamp"
$out = Join-Path $work 'out'            # release.ps1's output
$install = Join-Path $work 'install'    # /DIR=
$tmp = Join-Path $work 'tmp'            # TEMP for Setup and Uninstall, so their leftovers go with $work
$logs = Join-Path $work 'logs'
$cowHome = Join-Path $work 'home'
New-Item -ItemType Directory -Force -Path $work, $tmp, $logs, $cowHome | Out-Null
$env:COW_WORKTREES_HOME = $cowHome
$env:COW_CODEX_AGENTS_FILE = Join-Path $work 'codex\AGENTS.md'
$env:COW_NO_UPDATE_CHECK = '1'
# One registered pool whose folder does not exist: nothing can be done to it, but it shows whether the
# uninstaller tried. toolDir is empty so that the app (-CheckApp) has to find its folder by itself.
$poolName = "installer-test-$stamp"
$poolsFile = Join-Path $cowHome 'pools.json'
ConvertTo-Json ([ordered]@{ version = 1; toolDir = ''; pools = @([ordered]@{ name = $poolName; root = (Join-Path $work 'no-such-pool') }) }) -Depth 5 | Set-Content -LiteralPath $poolsFile -Encoding UTF8
$poolsBefore = Get-Content -LiteralPath $poolsFile -Raw
$disconnectLog = Join-Path $cowHome 'disconnect.log'
"work folder $work"

# Runs a program without the shell (nothing lands in Explorer's lists of started programs), waits, and
# ends it and its children when it takes too long. -OwnTemp gives it $tmp as its temporary folder.
function Invoke-Exe([string]$Exe, [string]$Arguments, [int]$TimeoutSec = 600, [switch]$OwnTemp) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe; $psi.Arguments = $Arguments; $psi.UseShellExecute = $false; $psi.WorkingDirectory = $work
    if ($OwnTemp) { $psi.EnvironmentVariables['TEMP'] = $tmp; $psi.EnvironmentVariables['TMP'] = $tmp }
    $p = [Diagnostics.Process]::Start($psi)
    $timedOut = -not $p.WaitForExit($TimeoutSec * 1000)
    if ($timedOut) { & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null; [void]$p.WaitForExit(10000) }
    $code = -1; if (-not $timedOut) { $code = $p.ExitCode }
    return [pscustomobject]@{ ExitCode = $code; TimedOut = $timedOut; Ended = (Get-Date) }
}
function Get-OurProcesses { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) }) }
function Get-OurApp { @(Get-OurProcesses | Where-Object { $_.ProcessName -eq 'CowWorktrees' }) }
function Invoke-Setup([string]$Log, [switch]$Launch) {
    $noLaunch = '/NOLAUNCH'; if ($Launch) { $noLaunch = '' }
    return (Invoke-Exe $setup "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART $noLaunch /NOICONS /DIR=`"$install`" /LOG=`"$(Join-Path $logs $Log)`"" -OwnTemp)
}
# unins000.exe runs a copy of itself from the temporary folder and can end before that copy has finished,
# so wait for the folder to go and for the copy to end as well.
function Invoke-Uninstall([string]$Log, [string]$Extra) {
    $r = Invoke-Exe (Join-Path $install 'unins000.exe') "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART $Extra /LOG=`"$(Join-Path $logs $Log)`"" -OwnTemp
    [void](Wait-Until { -not (Test-Path -LiteralPath $install) -and -not (Get-OurProcesses).Count } 60)
    return $r
}
# relative path -> SHA-256 of every file under a folder
function Get-Tree([string]$Dir) {
    $tree = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $Dir -Recurse -File -Force)) { $tree[$f.FullName.Substring($Dir.Length + 1)] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash }
    return $tree
}
$installerOwn = 'installed.marker', 'unins000.exe', 'unins000.dat', 'unins000.msg'
function Check-Layout([string]$When) {
    $now = Get-Tree $install
    $missing = @($staged.Keys | Where-Object { -not $now.ContainsKey($_) })
    $extra = @($now.Keys | Where-Object { -not $staged.ContainsKey($_) -and $installerOwn -notcontains $_ })
    $changed = @($staged.Keys | Where-Object { $now.ContainsKey($_) -and $now[$_] -ne $staged[$_] })
    Check (-not $missing.Count -and -not $extra.Count -and -not $changed.Count) "${When}: the $($staged.Count) staged files are installed, unchanged, and nothing else (missing $($missing.Count), extra $($extra.Count), different $($changed.Count))"
    foreach ($n in @($missing + $extra + $changed | Select-Object -First 5)) { "        $n" }
    foreach ($n in 'setup.ps1', 'uninstall.ps1', 'README.md', 'LICENSE', 'CHANGELOG.md', 'app\CowWorktrees.exe', 'scripts\cow_worktree.ps1', 'scripts\disconnect_all.ps1', 'unins000.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $install $n) -PathType Leaf)) { Check $false "${When}: $n is installed" }
    }
    foreach ($n in 'scripts', 'templates', 'docs', 'tests', 'tools', 'app') {
        if (-not (Test-Path -LiteralPath (Join-Path $install $n) -PathType Container)) { Check $false "${When}: $n\ is installed" }
    }
    $marker = Get-Item -LiteralPath (Join-Path $install 'installed.marker') -ErrorAction SilentlyContinue
    Check ($marker -and $marker.Length -eq 0) "${When}: installed.marker is there and empty"
    Check (-not (Get-OurApp).Count) "${When}: the app was not started (/NOLAUNCH)"
}

# ---- what Windows looks like before ---------------------------------------------------------------------
$named = 'cow-?worktrees|cow ?worktrees'   # cow-worktrees, CowWorktrees, CoW Worktrees
$uninstallKeys = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
# Uninstall entries that mention the tool: by key name or by any value (name, folder, uninstall command).
function Get-UninstallEntries {
    $found = @()
    foreach ($key in $uninstallKeys) {
        foreach ($k in @(Get-ChildItem -Path $key -ErrorAction SilentlyContinue)) {
            $text = $k.PSChildName
            foreach ($v in $k.GetValueNames()) { $text += "`n" + [string]$k.GetValue($v) }
            if ($text -match $named -or $text.IndexOf($testAppId, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $text.IndexOf($work, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $found += $k.Name }
        }
    }
    return $found
}
function Get-UninstallKeyNames { @(Get-ChildItem -Path $uninstallKeys[0] -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName }) }
function Get-ShellEntries {
    $found = @()
    foreach ($d in [Environment]::GetFolderPath('StartMenu'), [Environment]::GetFolderPath('CommonStartMenu')) {
        if ($d) { $found += @(Get-ChildItem -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $named } | ForEach-Object { $_.FullName }) }
    }
    foreach ($d in [Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('CommonDesktopDirectory')) {
        if ($d) { $found += @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $named } | ForEach-Object { $_.FullName }) }
    }
    return $found
}
# name, size and time of everything under a folder, as one string ('' when the folder is not there)
function Get-FolderState([string]$Dir) {
    if (-not (Test-Path -LiteralPath $Dir)) { return '' }
    return ('present;' + ((@(Get-ChildItem -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join ';'))
}
function Get-SetupLeftovers { @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'is-*.tmp' -or $_.Name -like '_iu*.tmp' } | ForEach-Object { $_.Name }) }
$realInstall = Join-Path $env:LOCALAPPDATA 'Programs\cow-worktrees'
$realHome = Join-Path $env:LOCALAPPDATA 'cow-worktrees'
$before = @{ uninstall = @(Get-UninstallEntries); keys = @(Get-UninstallKeyNames); shell = @(Get-ShellEntries); realInstall = (Get-FolderState $realInstall)
    realHome = (Get-FolderState $realHome); leftovers = @(Get-SetupLeftovers) }

try {
    Section 'build the test installer'
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $top 'release.ps1') -Version $version -AppDir $AppDir -OutDir $out -TestBuild *> (Join-Path $logs 'release.log')
    Check ($LASTEXITCODE -eq 0) "release.ps1 -TestBuild succeeded (log: $logs\release.log)"
    if ($LASTEXITCODE) { Get-Content (Join-Path $logs 'release.log') -Tail 8 }
    $setup = Join-Path $out "CowWorktrees-Setup-$version.exe"
    $zip = Join-Path $out "cow-worktrees-$version.zip"
    $stage = Join-Path $out "cow-worktrees-$version"
    Check ((Test-Path -LiteralPath $setup) -and (Test-Path -LiteralPath $zip)) "both files were made: $(Split-Path -Leaf $zip), $(Split-Path -Leaf $setup)"
    $text = Get-Content -LiteralPath (Join-Path $logs 'release.log') -Raw
    Check ((Test-Path -LiteralPath $setup) -and $text -match [regex]::Escape((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash) -and $text -match [regex]::Escape((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)) 'release.ps1 printed the SHA-256 of both'

    # Only a test build may be run here. A release build would add itself to Windows' list of installed
    # apps and to the Start menu, so stop instead of running anything that is not marked as a test build.
    $isTest = (Test-Path -LiteralPath $setup) -and ((Get-Item -LiteralPath $setup).VersionInfo.ProductName -like '*(test build)*')
    Check $isTest 'the installer is a test build (leaves no trace in Windows)'
    if (-not $isTest) { throw 'STOP: this is not a test build of the installer; it was not run' }

    Section 'the zip and the installer hold the same files'
    $staged = Get-Tree $stage
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    $zipped = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName -replace '/', '\' })
    $archive.Dispose()
    $expected = @($staged.Keys | ForEach-Object { "cow-worktrees-$version\$_" })
    Check ((($zipped | Sort-Object) -join '|') -eq (($expected | Sort-Object) -join '|')) "the zip holds the $($staged.Count) staged files and nothing else"
    Check (-not $staged.ContainsKey('installed.marker')) 'installed.marker is not in the staged folder (so not in the zip)'

    Section 'install'
    $r = Invoke-Setup 'install-1.log'
    Check (-not $r.TimedOut -and $r.ExitCode -eq 0) "Setup /VERYSILENT /SUPPRESSMSGBOXES /NOLAUNCH /NOICONS /DIR=... ended with exit code 0 (got $($r.ExitCode); log: $logs\install-1.log)"
    Check-Layout 'first install'

    if ($CheckApp) {
        Section 'the installed app finds its tool folder'
        $json = Join-Path $work 'selftest.json'
        $r = Invoke-Exe (Join-Path $install 'app\CowWorktrees.exe') "--hidden --selftest-checks `"$json`"" 120
        Check (-not $r.TimedOut) 'CowWorktrees.exe --hidden --selftest-checks ended by itself'
        $toolFolder = $null
        try { $toolFolder = (Get-Content -LiteralPath $json -Raw -ErrorAction Stop | ConvertFrom-Json).toolFolder } catch { }
        Check ($toolFolder -and ([IO.Path]::GetFullPath($toolFolder).TrimEnd('\') -ieq $install)) "toolFolder is the install folder (got '$toolFolder')"
        Get-OurApp | Stop-Process -Force -ErrorAction SilentlyContinue
    }

    Section 'install again over it, while the app is still closing'
    # The stand-in for the app: holds the mutex Setup looks for, for six seconds.
    $holderScript = Join-Path $work 'hold_mutex.ps1'
    Set-Content -LiteralPath $holderScript -Encoding ASCII -Value "`$m = New-Object Threading.Mutex(`$false, '$testMutex'); Set-Content -LiteralPath '$work\mutex.held' -Value 1; Start-Sleep -Seconds 6; `$m.Dispose()"
    $holder = Start-Process -FilePath $ps -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$holderScript`"" -WindowStyle Hidden -PassThru
    $held = Wait-Until { Test-Path -LiteralPath (Join-Path $work 'mutex.held') } 20
    $r = Invoke-Setup 'install-2.log'
    [void]$holder.WaitForExit(20000)
    Check (-not $r.TimedOut -and $r.ExitCode -eq 0) "Setup over the first install ended with exit code 0 (got $($r.ExitCode); log: $logs\install-2.log)"
    $waited = 0
    if ((Get-Content -LiteralPath (Join-Path $logs 'install-2.log') -Raw -ErrorAction SilentlyContinue) -match 'Waited (\d+) ms for the app to close') { $waited = [int]$Matches[1] }
    Check ($held -and $holder.HasExited -and $waited -ge 3000 -and $r.Ended -ge $holder.ExitTime) "Setup waited for the app (a stand-in holding its mutex) to close (its log says $waited ms)"
    Check-Layout 'second install'

    Section 'uninstall, keeping the integrations'
    $r = Invoke-Uninstall 'uninstall-1.log' '/KEEPINTEGRATIONS'
    Check (-not $r.TimedOut -and $r.ExitCode -eq 0) "unins000.exe /VERYSILENT /KEEPINTEGRATIONS ended with exit code 0 (got $($r.ExitCode); log: $logs\uninstall-1.log)"
    Check (-not (Test-Path -LiteralPath $install)) 'the install folder is gone'
    Check ((Get-Content -LiteralPath $poolsFile -Raw) -eq $poolsBefore -and -not (Test-Path -LiteralPath $disconnectLog)) '/KEEPINTEGRATIONS: pools.json is untouched and disconnect_all.ps1 was not run'

    if ($CheckLaunch -or $CheckDisconnect) {
        Section 'install once more'
        $r = Invoke-Setup 'install-3.log' -Launch:$CheckLaunch
        Check (-not $r.TimedOut -and $r.ExitCode -eq 0) "Setup ended with exit code 0 (got $($r.ExitCode); log: $logs\install-3.log)"
        if ($CheckLaunch) {
            # Setup does not wait for the app (nowait), so give the process a moment to appear.
            $started = Wait-Until { (Get-OurApp).Count -gt 0 } 30
            Check $started 'a silent Setup without /NOLAUNCH started the app from the install folder'
            Get-OurApp | Stop-Process -Force -ErrorAction SilentlyContinue
            Check (Wait-Until { -not (Get-OurApp).Count } 30) 'the app the test build started (hidden) is ended again'
        }
        if ($CheckDisconnect) {
            Section 'uninstall without /KEEPINTEGRATIONS runs disconnect_all.ps1'
            $r = Invoke-Uninstall 'uninstall-2.log' ''
            Check (-not $r.TimedOut -and $r.ExitCode -eq 0) "unins000.exe /VERYSILENT ended with exit code 0 (got $($r.ExitCode); log: $logs\uninstall-2.log)"
            Check (-not (Test-Path -LiteralPath $install)) 'the install folder is gone'
            $text = ''; if (Test-Path -LiteralPath $disconnectLog) { $text = Get-Content -LiteralPath $disconnectLog -Raw }
            Check ($text -match [regex]::Escape($poolName)) "disconnect_all.ps1 was run for the registered pool (disconnect.log names $poolName)"
            Check ($text -match '0 of 1 pools disconnected' -and (Get-Content -LiteralPath $poolsFile -Raw) -match [regex]::Escape($poolName)) 'a pool that cannot be disconnected is reported and stays registered; the uninstall goes on'
            if ($text) { $text.TrimEnd() -split "`r?`n" | ForEach-Object { "        $_" } }
        }
        else {
            $r = Invoke-Uninstall 'uninstall-2.log' '/KEEPINTEGRATIONS'
            Check (-not $r.TimedOut -and $r.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $install)) "uninstalled again (exit code $($r.ExitCode))"
        }
    }
}
catch { Check $false "the test ran to its end ($($_.Exception.Message))" }
finally {
    # Whatever happened above: nothing of the test may stay installed or running.
    Get-OurApp | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($holder -and -not $holder.HasExited) { $holder | Stop-Process -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath (Join-Path $install 'unins000.exe')) { [void](Invoke-Uninstall 'uninstall-cleanup.log' '/KEEPINTEGRATIONS') }
    [void](Wait-Until { -not (Get-OurProcesses).Count } 30)
    Get-OurProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
}

Section 'Windows is as it was'
$new = @(Get-UninstallEntries | Where-Object { $before.uninstall -notcontains $_ })
Check (-not $new.Count) "no uninstall entry for cow-worktrees was added to the registry $($new -join ', ')"
$new = @(Get-UninstallKeyNames | Where-Object { $before.keys -notcontains $_ })
Check (-not $new.Count) "no key at all was added under HKCU ...\CurrentVersion\Uninstall $($new -join ', ')"
$new = @(Get-ShellEntries | Where-Object { $before.shell -notcontains $_ })
Check (-not $new.Count) "nothing named cow-worktrees was added to the Start menu or the desktop $($new -join ', ')"
Check ((Get-FolderState $realInstall) -eq $before.realInstall) "$realInstall is as it was ($(if ($before.realInstall) { 'present' } else { 'not there' }))"
Check ((Get-FolderState $realHome) -eq $before.realHome) "$realHome is as it was"
$new = @(Get-SetupLeftovers | Where-Object { $before.leftovers -notcontains $_ })
Check (-not $new.Count) "Setup left nothing in $([IO.Path]::GetTempPath()) $($new -join ', ')"
Check (-not (Get-OurProcesses).Count) 'nothing the test started is still running'

if ($Keep) { "kept $work" }
else {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Check (-not (Test-Path -LiteralPath $work)) "the work folder is removed ($work)"
}
"$checks checks, $failures failed"
exit $failures
