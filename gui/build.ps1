#Requires -Version 5.1
<#
build.ps1 - Release build of CoW Worktrees into a clean .\publish folder.

The output is unpackaged and fully self-contained (.NET 8 and the Windows App SDK runtime are next to
CowWorktrees.exe), so the folder can be zipped and run on any Windows 10/11 x64 PC with nothing installed.
Prints the exe's path. Needs the .NET 8 SDK or newer.
#>
param([string]$Out = (Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'CowWorktrees\CowWorktrees.csproj'
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
dotnet publish $proj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $Out -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
# The published exe dies in InitializeComponent without the app's compiled XAML; the project adds it, check it did.
foreach ($f in 'CowWorktrees.exe', 'CowWorktrees.pri', 'MainWindow.xbf', 'App.xbf', 'coreclr.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.UI.Xaml.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $Out $f))) { throw "publish is missing $f" }
}
Write-Output (Join-Path $Out 'CowWorktrees.exe')
