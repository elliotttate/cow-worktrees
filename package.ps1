#Requires -Version 5.1
<#
package.ps1 - build the release zip: dist\cow-worktrees-<version>.zip

  cow-worktrees-<version>\
    setup.ps1, uninstall.ps1, README.md, LICENSE, CHANGELOG.md
    scripts\  templates\  tools\  tests\  docs\
    app\      CowWorktrees.exe and everything it needs (self-contained)

-AppDir is the GUI's publish folder (gui\build.ps1 makes it); the default is gui\publish.
#>
param([Parameter(Mandatory = $true)][string]$Version, [string]$AppDir)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults, so the default is set here.
if (-not $AppDir) { $AppDir = Join-Path $PSScriptRoot 'gui\publish' }
$name = "cow-worktrees-$Version"
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
foreach ($f in 'setup.ps1', 'uninstall.ps1', 'README.md', 'LICENSE', 'CHANGELOG.md') { Copy-Item (Join-Path $PSScriptRoot $f) $stage }
foreach ($d in 'scripts', 'templates', 'tools', 'tests', 'docs') { Copy-Item (Join-Path $PSScriptRoot $d) (Join-Path $stage $d) -Recurse }
if (-not (Test-Path (Join-Path $AppDir 'CowWorktrees.exe'))) { throw "no CowWorktrees.exe in $AppDir; run gui\build.ps1 first" }
Copy-Item $AppDir (Join-Path $stage 'app') -Recurse
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$files = Get-ChildItem $stage -Recurse -File
'{0}: {1} files, {2:N0} MB unpacked, {3:N0} MB zipped' -f $zip, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item $zip).Length / 1MB)
