#Requires -Version 5.1
<#
release.ps1 - build the two files a release ships:

  dist\cow-worktrees-<version>.zip         the tool folder, to unzip anywhere (package.ps1)
  dist\CowWorktrees-Setup-<version>.exe    the installer (installer\CowWorktrees.iss)

  .\release.ps1 -Version 1.2.0

It runs gui\build.ps1 -Version <version> (the app, into gui\publish), then package.ps1 (stages the folder
and zips it), then Inno Setup's compiler on that same staged folder, and prints both files with their
SHA-256. Needs the .NET 8 SDK or newer and Inno Setup 6.3 or newer (https://jrsoftware.org/isdl.php).
.github\workflows\release.yml runs this for a v<version> tag.

-AppDir     a publish folder of the app that is already built; gui\build.ps1 is then not run.
-OutDir     where the two files and the staged folder go; the default is dist.
-TestBuild  the installer that installer\test_installer.ps1 installs (ISCC /DTestBuild=1): it leaves no
            trace in Windows and only runs with /DIR=. Never publish one.
#>
param([Parameter(Mandatory = $true)][string]$Version, [string]$AppDir, [string]$OutDir, [switch]$TestBuild)
$ErrorActionPreference = 'Stop'
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ISCC.exe: on PATH, where Inno Setup's own uninstall entry says it is, or in one of the usual folders.
function Find-Iscc {
    $onPath = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Path }
    $dirs = @()
    foreach ($hive in 'HKCU:\Software', 'HKLM:\Software', 'HKLM:\Software\WOW6432Node') {
        $entry = Get-ItemProperty -Path "$hive\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" -ErrorAction SilentlyContinue
        if ($entry -and $entry.InstallLocation) { $dirs += $entry.InstallLocation }
    }
    foreach ($parent in (Join-Path $env:LOCALAPPDATA 'Programs'), ${env:ProgramFiles(x86)}, $env:ProgramFiles) {
        if ($parent) { $dirs += (Join-Path $parent 'Inno Setup 6') }
    }
    foreach ($d in $dirs) {
        $exe = Join-Path $d 'ISCC.exe'
        if (Test-Path -LiteralPath $exe) { return $exe }
    }
    return $null
}

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.]*)?$') { throw "'$Version' is not a version like 1.2.3 or 1.2.3-test" }
# Looked up first, so a PC without Inno Setup hears about it before the long build and not after.
$iscc = Find-Iscc
if (-not $iscc) { throw 'Inno Setup 6 is not installed (ISCC.exe was not found on PATH or in its usual folders). Get it from https://jrsoftware.org/isdl.php, then run release.ps1 again.' }
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults, so the default is set here.
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'dist' }
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)

if ($AppDir) {
    $AppDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AppDir)
    Step "App: using the build in $AppDir"
} else {
    Step "App: gui\build.ps1 -Version $Version"
    & (Join-Path $PSScriptRoot 'gui\build.ps1') -Version $Version
    $AppDir = Join-Path $PSScriptRoot 'gui\publish'
}

Step 'Zip: package.ps1'
& (Join-Path $PSScriptRoot 'package.ps1') -Version $Version -AppDir $AppDir -OutDir $OutDir
# package.ps1 leaves the folder it zipped; the installer is made from that folder and from nothing else.
$stage = Join-Path $OutDir "cow-worktrees-$Version"
$zip = Join-Path $OutDir "cow-worktrees-$Version.zip"
$setup = Join-Path $OutDir "CowWorktrees-Setup-$Version.exe"

Step "Installer: $iscc"
if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force }
$isccArgs = @('/Q', "/DVersion=$Version", "/DStageDir=$stage", "/O$OutDir")
if ($TestBuild) { $isccArgs += '/DTestBuild=1' }
& $iscc @isccArgs (Join-Path $PSScriptRoot 'installer\CowWorktrees.iss')
if ($LASTEXITCODE) { throw "ISCC failed ($LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC did not make $setup" }

Step 'Done'
if ($TestBuild) { Write-Host '    TEST BUILD of the installer: for installer\test_installer.ps1 only, never publish it.' -ForegroundColor Yellow }
foreach ($f in $zip, $setup) {
    '{0}  {1}  ({2:N1} MB)' -f (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant(), $f, ((Get-Item -LiteralPath $f).Length / 1MB)
}
