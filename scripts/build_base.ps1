#Requires -Version 5.1
<#
build_base.ps1 - build the base worktree so every clone starts with build output ("warm" clones).

Runs the "build" section of <root>\cow-config.json inside the base:
  "build": {
    "prepare":  ["powershell -NoProfile -File tools/generate_projects.ps1"],   (optional, run in order)
    "command":  "msbuild MyGame.sln /m /p:Configuration={config} /p:Platform=x64",
    "configs":  ["Debug", "Release"]
  }
Each line runs through `powershell.exe -NoProfile -Command` with the base as working directory.
On success writes <root>\base-build.json (commit + configs) and <root>\base-build-state.txt (the MSBuild
state files clones relocate). Fails if the build changed any tracked file in the base.
Normally called by `cow_worktree.ps1 -RefreshBase -Build`, which holds the pool's build lock.
#>
param([string]$Root)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'CowCommon.ps1')
try { $Root = Resolve-CowRoot $Root (Get-Location).Path; $Config = Get-CowConfig $Root } catch { [Console]::Error.WriteLine("build_base: $($_.Exception.Message)"); exit 1 }
# Below normal, so a rebuild never slows down whatever you are doing; the build tools inherit it.
try { [Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal' } catch { }

$Base = Join-Path $Root 'base'
$log = Join-Path $Root 'base-build.log'
$stateFile = Join-Path $Root 'base-build.json'
function Log([string]$msg) { $line = '{0} {1}' -f (Get-Date -Format s), $msg; Add-Content -LiteralPath $log -Value $line; [Console]::Error.WriteLine($line) }

if (-not $Config.build -or -not $Config.build.command) { Log 'cow-config.json has no build.command; nothing to build'; exit 1 }
$configs = @($Config.build.configs | Where-Object { $_ })
if (-not $configs.Count) { $configs = @('') }

$commit = git -C $Base rev-parse HEAD
Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue   # stale until this build succeeds
Push-Location $Base
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    # Output is appended as text: `*>>` would write UTF-16 into a log the rest of this script writes as ASCII.
    foreach ($step in @($Config.build.prepare | Where-Object { $_ })) {
        Log "prepare at $($commit.Substring(0, 9)): $step"
        & powershell.exe -NoProfile -Command $step 2>&1 | ForEach-Object { "$_" } | Add-Content -LiteralPath $log
        if ($LASTEXITCODE) { Log "prepare step failed ($LASTEXITCODE): $step"; exit 1 }
    }
    # Not named $config: PowerShell variable names ignore case, and that would overwrite $Config.
    foreach ($configName in $configs) {
        $command = ([string]$Config.build.command).Replace('{config}', $configName)
        Log "build: $command"
        & powershell.exe -NoProfile -Command $command 2>&1 | ForEach-Object { "$_" } | Add-Content -LiteralPath $log
        if ($LASTEXITCODE) { Log "build failed ($LASTEXITCODE): $command"; exit 1 }
    }
    # Files that only look changed to git status (see Get-CowStatus) are not changes the build made.
    try { $st = Get-CowStatus -Dir $Base } catch { Log "git status failed in the base: $($_.Exception.Message)"; exit 1 }
    if ($st.Real.Count) { Log "the build changed $($st.Real.Count) tracked files in the base:`n$(Format-CowEntries $st.Real)"; exit 1 }
    if ($st.Phantom.Count) { Log (Get-CowPhantomNote $st.Phantom.Count) }
    # The build-state files clones must relocate, so each clone reads exactly these instead of searching.
    & (Join-Path $PSScriptRoot 'relocate_build.ps1') -From $Base -To $Base -ListOnly |
        Set-Content -LiteralPath (Join-Path $Root 'base-build-state.txt')
    [ordered]@{ commit = $commit; configs = @($Config.build.configs | Where-Object { $_ }); builtAt = (Get-Date -Format s); minutes = [math]::Round($clock.Elapsed.TotalMinutes, 1) } |
        ConvertTo-Json | Set-Content -LiteralPath $stateFile
    Log "base built in $([math]::Round($clock.Elapsed.TotalMinutes, 1)) min"
    exit 0
} finally { Pop-Location }
