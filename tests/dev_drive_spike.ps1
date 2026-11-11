#Requires -Version 5.1
<#
dev_drive_spike.ps1 - try scripts\new_dev_drive.ps1 for real, on a throwaway 50 GB virtual disk.

For the owner of the PC to run by hand, from PowerShell started as administrator; nothing runs it
automatically. It answers what could not be answered without administrator rights and a restart: does
Format-Volume -DevDrive give a ReFS Dev Drive on this edition of Windows, does block cloning work on it,
and does the disk come back, with the same letter, after Windows restarts.

  powershell -NoProfile -ExecutionPolicy Bypass -File tests\dev_drive_spike.ps1 [-Path <file.vhdx>] [-Letter T] [-Yes]
      Shows the plan, asks you to type CREATE (-Yes skips the question), makes the test disk with
      new_dev_drive.ps1, checks it, and tries its startup task once (detach, run the task, wait).
  ... -CheckOnly [-Path <file.vhdx>]
      Checks the test disk again and makes nothing: run this after a restart.
  ... -Remove [-Path <file.vhdx>] [-Yes]
      Removes it all: the startup task, the attached disk, the .vhdx file and its two helper files.

-Path is C:\DevDriveTest\cow-test.vhdx unless you give another. The file may grow to 50 GB but starts at
well under 1 GB. Lines marked ok / FAIL are checks; LOOK means "read this yourself, the script could not
tell". Exit code = number of failed checks.
#>
param(
    [string]$Path = 'C:\DevDriveTest\cow-test.vhdx',
    [string]$Letter,
    [switch]$CheckOnly,
    [switch]$Remove,
    [switch]$Yes
)
$ErrorActionPreference = 'Continue'
$SizeGB = 50
$top = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $top 'scripts'
. (Join-Path $scripts 'CowCommon.ps1')
$newDrive = Join-Path $scripts 'new_dev_drive.ps1'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$diskpart = Join-Path $env:SystemRoot 'System32\diskpart.exe'
$failures = 0; $checks = 0; $looks = 0
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note([string]$msg) { Write-Host "    $msg" }
function Fail([string]$msg) { Write-Host "dev_drive_spike: $msg" -ForegroundColor Red; exit 1 }
function Check([bool]$ok, [string]$what) {
    $script:checks++
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}
function Look([string]$what) { $script:looks++; Write-Host "  LOOK  $what" -ForegroundColor Yellow }
function Show-Total {
    Write-Host ''
    Write-Host "$checks checks, $failures failed, $looks to read yourself (LOOK)."
}

# ---- every mode: administrator rights, and where things are --------------------------------------------
if ($CheckOnly -and $Remove) { Fail 'use -CheckOnly or -Remove, not both' }
$elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) { Fail 'this test must be run as administrator (it creates, formats and removes a virtual disk). Right-click PowerShell or Terminal, choose "Run as administrator", and run the same command again. Nothing was changed.' }
if ($Path -notmatch '^[A-Za-z]:\\') { Fail "-Path must be a full path such as C:\DevDriveTest\cow-test.vhdx (got: $Path)" }
$Path = [IO.Path]::GetFullPath($Path)
$attachFile = "$Path.attach.txt"
$detachFile = "$Path.detach.txt"
$again = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
$pathArgument = "-Path `"$Path`""

# Runs diskpart with these script lines (a temporary script file, deleted afterwards); returns ExitCode, Out.
function Invoke-Diskpart([string[]]$Lines) {
    $file = Join-Path ([IO.Path]::GetTempPath()) ('cow-diskpart-' + [Guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllLines($file, $Lines, [Text.Encoding]::ASCII)
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $diskpart
        $psi.Arguments = '/s "' + $file + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        try {
            $err = $p.StandardError.ReadToEndAsync()
            $out = $p.StandardOutput.ReadToEnd()
            $p.WaitForExit()
            [void]$err.Wait()
            return [pscustomobject]@{ ExitCode = $p.ExitCode; Out = ($out + $err.Result) }
        } finally { $p.Dispose() }
    } finally {
        try { [IO.File]::Delete($file) } catch { }
    }
}

# The disk number Windows gave the attached virtual disk, or -1 when it is not attached or cannot be found.
function Get-VdiskNumber {
    try {
        $img = Get-DiskImage -ImagePath $Path -ErrorAction Stop
        if ($img.Attached) {
            if ($null -ne $img.Number) { return [int]$img.Number }
            if ("$($img.DevicePath)" -match 'PhysicalDrive(\d+)$') { return [int]$Matches[1] }
        }
    } catch { }
    try {
        $hit = @(Get-Disk -ErrorAction Stop | Where-Object { "$($_.Location)" -ieq $Path })
        if ($hit.Count -eq 1) { return [int]$hit[0].Number }
    } catch { }
    return -1
}

# The letter the virtual disk has now: 'V', '' when attached without a letter, $null when not found attached.
function Get-VdiskLetter {
    $n = Get-VdiskNumber
    if ($n -lt 0) { return $null }
    $p = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter }) | Select-Object -First 1
    if ($p) { return ([string]$p.DriveLetter).ToUpperInvariant() }
    return ''
}

function Test-DriveThere([string]$L) { return [bool](Get-Partition -DriveLetter $L -ErrorAction SilentlyContinue) }

# The startup task new_dev_drive.ps1 registered for this .vhdx (the one whose action names its attach script).
function Get-AttachTask {
    foreach ($t in @(Get-ScheduledTask -TaskPath $CowTaskPath -ErrorAction SilentlyContinue)) {
        if ($t.TaskName -notlike 'Attach-DevDrive-*') { continue }
        foreach ($a in @($t.Actions)) {
            if ("$($a.Arguments)".IndexOf($attachFile, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $t }
        }
    }
    return $null
}

# The probe setup.ps1 uses: $true when a copy on this drive shares its storage with the original.
function Test-BlockClone([string]$Root) {
    if (-not ('CowCopy' -as [type])) { Add-Type -Path (Join-Path $scripts 'CowCopy.cs') }
    $a = $Root + '.clone-probe-a'; $b = $Root + '.clone-probe-b'
    try {
        $bytes = New-Object byte[] (4MB); (New-Object Random).NextBytes($bytes); [IO.File]::WriteAllBytes($a, $bytes)
        [CowCopy]::CopyOne($a, $b, $true)
        $ca = [CowCopy]::FirstCluster($a); $cb = [CowCopy]::FirstCluster($b)
        return ($ca -ge 0 -and $ca -eq $cb)
    } finally {
        foreach ($f in $a, $b) { try { [IO.File]::Delete($f) } catch { } }
    }
}

function Confirm-Typed([string]$Word) {
    if ($Yes) { return }
    $answer = $null
    try { $answer = Read-Host "Type $Word and press Enter to go on (anything else stops)" }
    catch { Fail 'cannot ask the question here (not an interactive window). Run it again with -Yes if you are sure. Nothing was changed.' }
    if ("$answer" -cne $Word) { Write-Host 'Stopped. Nothing was changed.'; exit 1 }
}

function Show-TaskRun($Task) {
    try {
        $info = Get-ScheduledTaskInfo -TaskPath $Task.TaskPath -TaskName $Task.TaskName -ErrorAction Stop
        $boot = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
        Note "the task last ran: $($info.LastRunTime), result $($info.LastTaskResult) (0 = it worked). Windows started: $boot"
    } catch { Note "could not read when the task last ran: $($_.Exception.Message)" }
}

# ReFS, Dev Drive, block cloning: the three things the drive is for.
function Test-Drive([string]$L) {
    Step "Checking drive ${L}:"
    $volume = Get-Volume -DriveLetter $L -ErrorAction SilentlyContinue
    Check ([bool]$volume) "drive ${L}: exists"
    if (-not $volume) { return }
    Check ("$($volume.FileSystem)" -eq 'ReFS') "its file system is ReFS (Windows says: $($volume.FileSystem))"
    Note "size $([math]::Round($volume.Size / 1GB, 2)) GB, free $([math]::Round($volume.SizeRemaining / 1GB, 2)) GB, name '$($volume.FileSystemLabel)'"
    if ([IO.File]::Exists($Path)) { Note "the file $Path takes $([math]::Round((New-Object IO.FileInfo -ArgumentList $Path).Length / 1MB)) MB now" }

    Note "fsutil devdrv query ${L}: says:"
    $text = @(& fsutil.exe devdrv query "${L}:")
    $code = $LASTEXITCODE
    foreach ($line in $text) { if ("$line".Trim()) { Write-Host "      | $line" } }
    $joined = $text -join "`n"
    if ($code -ne 0) { Check $false "fsutil devdrv query ${L}: worked (exit code $code)" }
    elseif ($joined -match '(?i)not a developer volume') { Check $false "${L}: is a Dev Drive" }
    elseif ($joined -match '(?i)untrusted developer volume') { Check $false "${L}: is a trusted Dev Drive (it is a Dev Drive but not trusted; 'fsutil devdrv trust ${L}:' would trust it)" }
    elseif ($joined -match '(?i)trusted developer volume') { Check $true "${L}: is a trusted Dev Drive" }
    else { Look "the words above did not say, in a form this script knows, whether ${L}: is a Dev Drive (Windows in another language?)" }

    try {
        $cloned = Test-BlockClone "${L}:\"
        Check ([bool]$cloned) "a copy on ${L}: is a block clone (original and copy start on the same cluster)"
    } catch { Check $false "the block-clone test ran on ${L}: ($($_.Exception.Message))" }
}

function Test-Task($Task, [string]$L) {
    Step 'Checking the startup task'
    Check ([bool]$Task) "a task ${CowTaskPath}Attach-DevDrive-<letter> runs $attachFile"
    if (-not $Task) { return }
    Check ($Task.TaskName -eq "Attach-DevDrive-$L") "it is named for the drive letter: $($Task.TaskName)"
    Check ("$($Task.State)" -ne 'Disabled') "it is switched on (state: $($Task.State))"
    $user = "$($Task.Principal.UserId)"
    if ($user -match '^(SYSTEM|S-1-5-18|NT AUTHORITY\\SYSTEM)$') { Check $true 'it runs as SYSTEM' }
    else { Look "it runs as '$user'; SYSTEM was expected (this may be SYSTEM under a translated name)" }
    Check ("$($Task.Principal.RunLevel)" -eq 'Highest') "with highest privileges (run level: $($Task.Principal.RunLevel))"
    $atStartup = @($Task.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' })
    Check ($atStartup.Count -ge 1) 'its trigger is "at startup"'
    Check ("$($Task.Settings.DisallowStartIfOnBatteries)" -eq 'False') 'it may start on battery'
    Check ([IO.File]::Exists($attachFile)) "the attach script $attachFile exists"
    Show-TaskRun $Task
}

# ---- -Remove -------------------------------------------------------------------------------------------
if ($Remove) {
    $task = Get-AttachTask
    $haveDisk = [IO.File]::Exists($Path)
    $haveHelper = [IO.File]::Exists($attachFile) -or [IO.File]::Exists($detachFile)
    if (-not $haveDisk -and -not $haveHelper -and -not $task) { Write-Host "Nothing to remove: no $Path, no helper files next to it, no startup task for it."; exit 0 }
    # Only a disk that new_dev_drive.ps1 made is removed: it leaves a helper file and a task behind.
    if ($haveDisk -and -not $haveHelper -and -not $task) { Fail "$Path does not look like a disk made by new_dev_drive.ps1 (no .attach.txt file next to it, no startup task for it). It was left alone." }
    Write-Host 'WARNING - this removes the test Dev Drive:' -ForegroundColor Yellow
    if ($task) { Write-Host "  - the scheduled task $($task.TaskPath)$($task.TaskName) is deleted" }
    if ($haveDisk) { Write-Host "  - the virtual disk $Path is detached and the file is DELETED, with everything stored on that drive" }
    Write-Host "  - the helper files $attachFile and $detachFile are deleted"
    Confirm-Typed 'REMOVE'

    Step 'Removing'
    if ($task) {
        try { Unregister-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -Confirm:$false -ErrorAction Stop; Check $true "removed the task $($task.TaskPath)$($task.TaskName)" }
        catch { Check $false "removed the task $($task.TaskPath)$($task.TaskName) ($($_.Exception.Message))" }
    }
    if ($haveDisk) {
        try {
            $run = Invoke-Diskpart @("select vdisk file=`"$Path`"", 'detach vdisk')
            if ($run.ExitCode -eq 0) { Note 'detached the virtual disk' } else { Note "diskpart did not detach it (exit code $($run.ExitCode)); that is normal when it was not attached" }
        } catch { Note "could not run diskpart: $($_.Exception.Message)" }
    }
    foreach ($f in $Path, $attachFile, $detachFile) {
        if (-not [IO.File]::Exists($f)) { continue }
        $gone = $false; $why = ''
        # Right after a detach the file can stay locked for a moment: try for a few seconds.
        for ($i = 0; $i -lt 10 -and -not $gone; $i++) {
            try { [IO.File]::Delete($f); $gone = $true } catch { $why = "($($_.Exception.Message))"; Start-Sleep -Milliseconds 500 }
        }
        if ($gone) { $why = '' }
        Check $gone "deleted $f $why"
    }
    if ([IO.File]::Exists($Path)) { Note 'The .vhdx is still there. In Disk Management, right-click the disk, choose Detach VHD, and run -Remove again.' }
    $folder = [IO.Path]::GetDirectoryName($Path)
    if ($folder -and [IO.Directory]::Exists($folder) -and -not @([IO.Directory]::GetFileSystemEntries($folder)).Count) { Note "The folder $folder is empty now and was left in place; delete it if you do not want it." }
    Show-Total
    exit $failures
}

# ---- -CheckOnly (after a restart) ----------------------------------------------------------------------
if ($CheckOnly) {
    Step "Checking the test disk $Path"
    Check ([IO.File]::Exists($Path)) "the file $Path exists"
    $task = Get-AttachTask
    $expected = ''
    if ($task -and $task.TaskName -match '^Attach-DevDrive-([A-Za-z])$') { $expected = $Matches[1].ToUpperInvariant() }
    $now = Get-VdiskLetter
    if ($null -eq $now -and $expected -and (Test-DriveThere $expected)) {
        Look "Windows did not say where $Path is attached; going by drive ${expected}: (check in Disk Management that it is this file)"
        $now = $expected
    }
    Check ($null -ne $now) 'the virtual disk is attached'
    if ($null -eq $now) {
        Note "To attach it by hand:  diskpart /s `"$attachFile`"   - then run this check again."
    } else {
        Check ([bool]($expected -and $now -eq $expected)) "it has the letter it was given (expected '$expected', now '$now')"
        if ($now) { Test-Drive $now }
    }
    Test-Task $task $expected
    Show-Total
    exit $failures
}

# ---- create: the plan, the question, the real run ------------------------------------------------------
if (-not [IO.File]::Exists($newDrive)) { Fail "$newDrive was not found" }
$childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $newDrive, '-Path', $Path, '-SizeGB', "$SizeGB")
if ($Letter) { $childArgs += @('-Letter', $Letter) }

Step 'The plan, as new_dev_drive.ps1 -WhatIf prints it (nothing is changed yet)'
$planArgs = $childArgs + @('-WhatIf')
& $ps @planArgs | ForEach-Object { Write-Host "    | $_" }
if ($LASTEXITCODE -ne 0) { Fail 'new_dev_drive.ps1 would not go ahead; its reason is above. Nothing was changed.' }

Write-Host ''
Write-Host 'WARNING - what follows is a real run, on this PC:' -ForegroundColor Yellow
Write-Host "  - it creates the file $Path, a virtual disk that may grow to $SizeGB GB (it starts at well under 1 GB)"
Write-Host '  - it attaches that file as a new disk and FORMATS THAT NEW DISK, and no other, as a ReFS Dev Drive'
Write-Host "  - it adds the scheduled task ${CowTaskPath}Attach-DevDrive-<letter>, which attaches the disk at every start of Windows"
Write-Host '  - it then detaches the disk once and lets that task attach it again, to see the task work'
Write-Host 'If Windows asks whether to format a new drive while this runs, choose Cancel.'
Write-Host "Everything is removed again with:  $again -Remove $pathArgument"
Confirm-Typed 'CREATE'

Step 'Running new_dev_drive.ps1'
$lines = New-Object System.Collections.ArrayList
& $ps @childArgs | ForEach-Object { [void]$lines.Add("$_"); Write-Host "    | $_" }
$code = $LASTEXITCODE
$root = [string](@($lines | Where-Object { "$_".Trim() }) | Select-Object -Last 1)
Check ($code -eq 0) "new_dev_drive.ps1 ended with exit code 0 (got $code)"
Check ($root -match '^[D-Z]:\\$') "its last line of output is the drive root (got: $root)"
if ($code -ne 0 -or $root -notmatch '^[D-Z]:\\$') {
    Note 'new_dev_drive.ps1 removes what it made when it fails; its messages are above.'
    Show-Total
    exit $failures
}
$L = $root.Substring(0, 1).ToUpperInvariant()

Test-Drive $L
$task = Get-AttachTask
Test-Task $task $L

if ($task) {
    Step 'Trying the startup task now: detach the disk, start the task, wait for the drive'
    $run = Invoke-Diskpart @("select vdisk file=`"$Path`"", 'detach vdisk')
    Check ($run.ExitCode -eq 0) "the disk detaches (diskpart exit code $($run.ExitCode))"
    if ($run.ExitCode -eq 0) {
        for ($i = 0; $i -lt 20 -and (Test-DriveThere $L); $i++) { Start-Sleep -Milliseconds 500 }
        Check (-not (Test-DriveThere $L)) "drive ${L}: is gone while the disk is detached"
        Start-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName
        for ($i = 0; $i -lt 60 -and -not (Test-DriveThere $L); $i++) { Start-Sleep -Milliseconds 500 }
        $back = Test-DriveThere $L
        Check $back "the task attached the disk again as ${L}:"
        Show-TaskRun $task
        if ($back) {
            $volume = Get-Volume -DriveLetter $L -ErrorAction SilentlyContinue
            Check ([bool]($volume -and "$($volume.FileSystem)" -eq 'ReFS')) "and ${L}: is still ReFS"
        } else {
            $other = Get-VdiskLetter
            if ($other) { Note "The disk is attached, but as ${other}: instead of ${L}:." }
            else { Note "The disk may be detached now. To attach it by hand:  diskpart /s `"$attachFile`"" }
        }
    }
}

Step 'After a restart'
Note 'Restart Windows (Start, Power, Restart). Then open PowerShell as administrator and run:'
Note "  $again -CheckOnly $pathArgument"
Note "Every line must say ok, with the disk attached as ${L}:."
Note 'Then do the same once more after Start, Power, Shut down and switching the PC on again:'
Note 'with Fast Startup, a shut-down is not the same thing as a restart.'
Note 'By hand, the same checks are:'
Note "  Get-Volume -DriveLetter $L                 FileSystem must say ReFS"
Note "  fsutil devdrv query ${L}:                  must say it is a trusted developer volume"
Note "  Get-ScheduledTaskInfo -TaskPath '$CowTaskPath' -TaskName 'Attach-DevDrive-$L'     LastTaskResult 0, LastRunTime at the start of Windows"
Step 'To remove the test disk'
Note "  $again -Remove $pathArgument"
Show-Total
exit $failures
