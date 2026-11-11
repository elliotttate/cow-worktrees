#Requires -Version 5.1
<#
package.ps1 - build the release zip: dist\cow-worktrees-<version>.zip

  cow-worktrees-<version>\
    setup.ps1, uninstall.ps1, README.md, LICENSE, CHANGELOG.md
    scripts\  templates\  tools\  tests\  docs\
    app\      CowWorktrees.exe and everything it needs (self-contained)

The files are first staged in a folder (dist\cow-worktrees-<version>), and the zip is made from it. The
installer (installer\CowWorktrees.iss, built by release.ps1) packs that same folder, so the zip and the
installer cannot differ. A new kind of file that should ship goes into the two lists below, nowhere else.

-AppDir is the GUI's publish folder (gui\build.ps1 makes it); the default is gui\publish.
-OutDir is where the staged folder and the zip go; the default is dist.
-StageOnly stages the folder, prints its path and stops before the zip is made.
#>
param([Parameter(Mandatory = $true)][string]$Version, [string]$AppDir, [string]$OutDir, [switch]$StageOnly)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults, so the defaults are set here.
if (-not $AppDir) { $AppDir = Join-Path $PSScriptRoot 'gui\publish' }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'dist' }
$dist = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$name = "cow-worktrees-$Version"

# ---- stage: the one place that decides what a release holds --------------------------------------------
$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
foreach ($f in 'setup.ps1', 'uninstall.ps1', 'README.md', 'LICENSE', 'CHANGELOG.md') { Copy-Item (Join-Path $PSScriptRoot $f) $stage }
foreach ($d in 'scripts', 'templates', 'tools', 'tests', 'docs') { Copy-Item (Join-Path $PSScriptRoot $d) (Join-Path $stage $d) -Recurse }
if (-not (Test-Path (Join-Path $AppDir 'CowWorktrees.exe'))) { throw "no CowWorktrees.exe in $AppDir; run gui\build.ps1 first" }
Copy-Item $AppDir (Join-Path $stage 'app') -Recurse
# installed.marker is what tells the app it was put there by the installer; only the installer may add it.
if (Test-Path (Join-Path $stage 'installed.marker')) { throw "installed.marker must not be in a release; remove it from $PSScriptRoot" }
if ($StageOnly) { $stage; return }

# ---- zip ------------------------------------------------------------------------------------------------
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$files = Get-ChildItem $stage -Recurse -File
'{0}: {1} files, {2:N0} MB unpacked, {3:N0} MB zipped' -f $zip, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item $zip).Length / 1MB)
