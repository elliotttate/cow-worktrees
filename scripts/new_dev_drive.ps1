#Requires -Version 5.1
<#
new_dev_drive.ps1 - make a Dev Drive inside a virtual disk file, for a PC that has no ReFS volume yet.

cow-worktrees clones worktrees without copying their files, which only works on ReFS, and on Windows 11 a
ReFS volume for development is a "Dev Drive". This script makes one inside a single file: a .vhdx virtual
disk that starts small and grows as it is used, up to -SizeGB. No existing disk or partition is changed.

  new_dev_drive.ps1 -Path C:\DevDrive\devdrive.vhdx -SizeGB 100 [-Letter V] [-WhatIf]

Run it from a PowerShell window started as administrator. In order:
  1. Checks, changing nothing: administrator rights; -Path does not exist yet and is on a built-in NTFS or
     ReFS disk with room; -SizeGB is 50 or more (the smallest Dev Drive Windows allows); the drive letter
     is free. Without -Letter the last free letter is taken (Z down to D; never A, B or C).
  2. diskpart creates the virtual disk, attaches it, makes one GPT partition and gives it the letter.
     (New-VHD would do the same but belongs to Hyper-V, which Windows Home does not have.)
  3. Format-Volume -DevDrive formats that new partition, and only that one, as a ReFS Dev Drive.
  4. A scheduled task, \CowWorktrees\Attach-DevDrive-<letter>, attaches the disk again whenever Windows
     starts: without it the drive would be gone after a restart (see the comment at that step).
  5. A block-clone test on the new drive, the same probe setup.ps1 uses.
When a step fails, everything made so far is removed again. At the end it prints how to remove the drive.

-WhatIf prints every step (the diskpart script, the format command, the task) and changes nothing: no file
is written and no program is started. It also runs without administrator rights, and says so.

Exit code 0 on success, with the drive root (for example V:\) as the last line of output; 1 on failure.
tests\dev_drive_spike.ps1 tries all of this on a throwaway disk.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][int]$SizeGB,
    [string]$Letter,
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
# Belt and braces: the -WhatIf branch below exits before the first thing is made, and this makes PowerShell's
# own commands refuse to change anything as well.
if ($WhatIf) { $WhatIfPreference = $true }
. (Join-Path $PSScriptRoot 'CowCommon.ps1')

$MinSizeGB = 50          # Windows' own minimum for a Dev Drive
$MaxSizeGB = 64000       # a .vhdx file holds 64 TB at most
$MinHostFreeGB = 10      # room for the new disk (well under 1 GB while empty) and a first repository
$PartitionSlackMB = 64   # GPT bookkeeping and the 16 MB reserved partition, so the volume is not below -SizeGB
$Label = 'DevDrive'

function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note([string]$msg) { Write-Host "    $msg" }
function Warn([string]$msg) { Write-Host "    warning: $msg" -ForegroundColor Yellow }
# Fail is for the checks only: once something has been made, a failure goes through Undo-Everything instead.
function Fail([string]$msg) { Write-Host "new_dev_drive: $msg" -ForegroundColor Red; Write-Host '    Nothing was changed.'; exit 1 }
function Show-Lines([string[]]$Lines) { foreach ($l in $Lines) { Write-Host "        $l" } }
function Quote([string]$s) { return "'" + $s.Replace("'", "''") + "'" }

# Runs diskpart with these script lines (through a temporary script file, deleted afterwards) and returns
# ExitCode and Out. diskpart stops at the first command that fails and then exits with a code other than 0.
# Started through .NET rather than "& diskpart 2>&1": PowerShell 5.1 turns redirected error text into errors.
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

function Write-DiskpartOutput([string]$Text) {
    foreach ($line in ($Text -split "`r?`n")) { if ($line.Trim()) { Note "diskpart: $($line.Trim())" } }
}

# The disk number Windows gave the attached virtual disk $File, or -1 when it cannot be found.
function Get-VdiskNumber([string]$File) {
    try {
        $img = Get-DiskImage -ImagePath $File -ErrorAction Stop
        if ($img.Attached) {
            if ($null -ne $img.Number) { return [int]$img.Number }
            if ("$($img.DevicePath)" -match 'PhysicalDrive(\d+)$') { return [int]$Matches[1] }
        }
    } catch { }
    try {
        $hit = @(Get-Disk -ErrorAction Stop | Where-Object { "$($_.Location)" -ieq $File })
        if ($hit.Count -eq 1) { return [int]$hit[0].Number }
    } catch { }
    return -1
}

# The startup task runs the commands in this file as SYSTEM, so only SYSTEM and administrators may change
# it; everybody else may read it. (This cannot help when the folder itself belongs to an ordinary user, who
# can then swap the file for another: keep the .vhdx in a folder that only administrators can change.)
function Protect-HelperFile([string]$File) {
    $acl = Get-Acl -LiteralPath $File
    $acl.SetAccessRuleProtection($true, $false)   # stop inheriting from the folder, keep no copy of its rules
    foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'Read'))) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier -ArgumentList $entry[0]   # SYSTEM, Administrators, Users
        $rights = [System.Security.AccessControl.FileSystemRights]$entry[1]
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList $sid, $rights, $allow))
    }
    Set-Acl -LiteralPath $File -AclObject $acl
}

# The probe setup.ps1 uses: write 4 MB, copy it with CopyFileExW, and see whether both files start on the same
# cluster. $true = block clone, $false = an ordinary copy; throws when the drive cannot be written at all.
# Plain .NET file calls only: PowerShell may not know a drive letter that is seconds old yet.
function Test-BlockClone([string]$Root) {
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

# ---- 1. checks (read only: nothing is changed anywhere above the -WhatIf exit) -------------------------
Step 'Checking'
$elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) {
    if (-not $WhatIf) { Fail 'this must be run as administrator, because it creates and formats a (virtual) disk. Right-click PowerShell or Terminal, choose "Run as administrator", and run the same command again.' }
    Note 'not running as administrator: a real run would stop here. Showing the plan anyway.'
}
$formatVolume = Get-Command Format-Volume -ErrorAction SilentlyContinue
if (-not $formatVolume -or -not $formatVolume.Parameters.ContainsKey('DevDrive')) { Fail 'this version of Windows cannot make a Dev Drive (Format-Volume has no -DevDrive here). Windows 11 build 22621.2338 or newer is needed.' }
$diskpart = Join-Path $env:SystemRoot 'System32\diskpart.exe'
if (-not [IO.File]::Exists($diskpart)) { Fail "diskpart was not found ($diskpart)" }

if ($SizeGB -lt $MinSizeGB) { Fail "-SizeGB $SizeGB is too small: Windows wants at least $MinSizeGB GB for a Dev Drive. The file only takes the space that is really used, so asking for $MinSizeGB costs nothing up front." }
if ($SizeGB -gt $MaxSizeGB) { Fail "-SizeGB $SizeGB is too big: a .vhdx file holds $MaxSizeGB GB at most" }

if ($Path -notmatch '^[A-Za-z]:\\') { Fail "-Path must be a full path on one of this PC's drives, for example C:\DevDrive\devdrive.vhdx (got: $Path)" }
try { $Path = [IO.Path]::GetFullPath($Path) } catch { Fail "-Path is not a usable file path: $($_.Exception.Message)" }
if ([IO.Path]::GetExtension($Path) -ne '.vhdx') { Fail "-Path must end in .vhdx (got: $Path)" }
# diskpart reads its commands from a text file; anything but plain ASCII in a path is not sure to survive that.
if ($Path -match '[^\x20-\x7E]') { Fail "-Path has a character outside plain English letters, digits and punctuation, which diskpart may not read back correctly. Use a folder such as C:\DevDrive (got: $Path)" }
$attachFile = "$Path.attach.txt"
$detachFile = "$Path.detach.txt"
if ([IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) { Fail "$Path already exists. This script never overwrites or reuses a file: choose another name, or delete that one yourself if it is not needed." }
foreach ($f in $attachFile, $detachFile) {
    if ([IO.File]::Exists($f) -or [IO.Directory]::Exists($f)) { Fail "$f already exists (left from an earlier Dev Drive?). Delete it or choose another -Path." }
}

$hostLetter = $Path.Substring(0, 1).ToUpperInvariant()
$hostVolume = Get-Volume -DriveLetter $hostLetter -ErrorAction SilentlyContinue
if (-not $hostVolume) { Fail "drive ${hostLetter}: was not found among this PC's own disks (a network drive cannot hold the file)" }
if ("$($hostVolume.DriveType)" -ne 'Fixed') { Fail "drive ${hostLetter}: is a $($hostVolume.DriveType) drive. The file must be on a built-in disk: the Dev Drive is gone whenever the disk holding its file is." }
if ('NTFS', 'ReFS' -notcontains "$($hostVolume.FileSystem)") { Fail "drive ${hostLetter}: is $($hostVolume.FileSystem); the file must be on an NTFS or ReFS drive" }
$freeGB = [math]::Floor($hostVolume.SizeRemaining / 1GB)
if ($freeGB -lt $MinHostFreeGB) { Fail "drive ${hostLetter}: has only $freeGB GB free. At least $MinHostFreeGB GB is needed for a Dev Drive that can hold a repository: free some space, or put the file on another drive." }

$folder = [IO.Path]::GetDirectoryName($Path)
$missingFolders = @()   # the folders a real run creates, deepest first (removed again when a later step fails)
$existing = $folder
while ($existing -and -not [IO.Directory]::Exists($existing)) { $missingFolders += $existing; $existing = [IO.Path]::GetDirectoryName($existing) }
if ($existing) {
    $attributes = [IO.File]::GetAttributes($existing)
    if ($attributes -band ([IO.FileAttributes]::Compressed -bor [IO.FileAttributes]::Encrypted)) { Fail "the folder $existing is compressed or encrypted, and Windows cannot keep a virtual disk in such a folder. Choose another folder." }
}

# Letters that are not free: drives of this session, every volume, the signed-in user's network drives (an
# elevated window does not see those, so they are read from the registry), and Dev Drives made by this
# script earlier that are detached right now (their startup task still names the letter).
$taken = @{}
foreach ($d in [IO.DriveInfo]::GetDrives()) { $taken[$d.Name.Substring(0, 1).ToUpperInvariant()] = 'it is in use' }
foreach ($v in @(Get-Volume -ErrorAction SilentlyContinue)) { if ($v.DriveLetter) { $taken[([string]$v.DriveLetter).ToUpperInvariant()] = 'it is in use' } }
foreach ($k in @(Get-ChildItem -LiteralPath 'HKCU:\Network' -ErrorAction SilentlyContinue)) { if ($k.PSChildName -match '^[A-Za-z]$') { $taken[$k.PSChildName.ToUpperInvariant()] = 'a network drive uses it' } }
foreach ($t in @(Get-ScheduledTask -TaskPath $CowTaskPath -ErrorAction SilentlyContinue)) {
    if ($t.TaskName -match '^Attach-DevDrive-([A-Za-z])$') { $taken[$Matches[1].ToUpperInvariant()] = "the task $CowTaskPath$($t.TaskName) keeps it for another Dev Drive" }
}
if ($Letter) {
    $Letter = $Letter.Trim().TrimEnd('\').TrimEnd(':').ToUpperInvariant()
    if ($Letter -notmatch '^[D-Z]$') { Fail "-Letter must be one letter from D to Z (A, B and C are never used); got '$Letter'" }
    if ($taken.ContainsKey($Letter)) { Fail "drive letter ${Letter}: cannot be used: $($taken[$Letter]). Pick another, or leave -Letter out and a free one is chosen." }
} else {
    foreach ($c in [char[]]'ZYXWVUTSRQPONMLKJIHGFED') { if (-not $taken.ContainsKey([string]$c)) { $Letter = [string]$c; break } }
    if (-not $Letter) { Fail 'no drive letter from D to Z is free' }
}
Note "file:         $Path"
Note "size:         up to $SizeGB GB (the file starts small and grows as the drive fills)"
Note "drive letter: ${Letter}:"
Note "space:        drive ${hostLetter}: has $freeGB GB free"
if ($freeGB -lt $SizeGB) { Warn "drive ${hostLetter}: has less free space ($freeGB GB) than the Dev Drive may grow to ($SizeGB GB). That works, but when ${hostLetter}: fills up, writing to the Dev Drive fails: keep an eye on it." }

# ---- 2. the plan: every command and file is built here, once, and used exactly as -WhatIf shows it -----
$maxMB = $SizeGB * 1024 + $PartitionSlackMB
$taskName = "Attach-DevDrive-$Letter"
$quotedPath = '"' + $Path + '"'
$createLines = [string[]]@(
    "create vdisk file=$quotedPath maximum=$maxMB type=expandable",
    "select vdisk file=$quotedPath",
    'attach vdisk',
    'convert gpt',
    'create partition primary',
    "assign letter=$Letter"
)
$attachLines = [string[]]@("select vdisk file=$quotedPath", 'attach vdisk')
$detachLines = [string[]]@("select vdisk file=$quotedPath", 'detach vdisk')
$formatText = "Format-Volume -DriveLetter $Letter -DevDrive -FileSystem ReFS -NewFileSystemLabel '$Label' -Confirm:`$false"
$taskArguments = '/s "' + $attachFile + '"'
$taskDescription = "cow-worktrees: attach the Dev Drive ${Letter}: ($Path) when Windows starts"

if ($WhatIf) {
    Step 'What a real run would do, in this order (-WhatIf: nothing is changed)'
    $n = 1
    if ($missingFolders.Count) { Note "$n. Create the folder $folder"; $n++ }
    Note "$n. Run diskpart with this script (creates the file, attaches it as a disk, one GPT partition, letter ${Letter}:):"; $n++
    Show-Lines $createLines
    Note "$n. Format that new partition, and no other, as a ReFS Dev Drive:"; $n++
    Show-Lines @($formatText)
    Note "$n. Write two small diskpart scripts next to the file (the first one changeable by administrators only):"; $n++
    Note "    $attachFile"
    Show-Lines $attachLines
    Note "    $detachFile"
    Show-Lines $detachLines
    Note "$n. Register the scheduled task $CowTaskPath$taskName (at every start of Windows, as SYSTEM, highest privileges):"; $n++
    Show-Lines @("$diskpart $taskArguments")
    Note "$n. Test block cloning on ${Letter}:\ (write a 4 MB file, copy it, compare where the two are stored, delete both)"; $n++
    Note "$n. Print how to remove the drive again, then ${Letter}:\ as the last line"
    Note 'If any step fails: remove the task, detach the disk, delete the files and folders made.'
    Write-Host 'new_dev_drive: -WhatIf: nothing was changed.'
    exit 0
}

# ---- from here on things are made; $made records them so that a failure can remove them again ----------
# The block-clone probe is compiled first: if that cannot be done, better to stop before making anything.
try { if (-not ('CowCopy' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'CowCopy.cs') } }
catch { Fail "could not load CowCopy.cs for the block-clone test: $($_.Exception.Message)" }

$made = @{ Folders = @(); Disk = $false; Helpers = $false; Task = $false }

# Removes whatever this run made, newest first. Every step stands alone: one that fails is reported and
# the next still runs, and none of them can replace the error that brought us here.
function Undo-Everything {
    if (-not ($made.Task -or $made.Disk -or $made.Helpers -or @($made.Folders).Count)) { Note 'nothing had been made yet'; return }
    Step 'Removing what was made'
    if ($made.Task) {
        try { Unregister-ScheduledTask -TaskPath $CowTaskPath -TaskName $taskName -Confirm:$false -ErrorAction Stop; Note "removed the task $CowTaskPath$taskName" }
        catch { Warn "could not remove the task $CowTaskPath${taskName}: $($_.Exception.Message). Remove it in Task Scheduler." }
    }
    if ($made.Disk) {
        try {
            $run = Invoke-Diskpart $detachLines
            if ($run.ExitCode -eq 0) { Note 'detached the virtual disk' } else { Note "diskpart did not detach the disk (exit code $($run.ExitCode)); that is normal when it never got attached" }
        } catch { Warn "could not run diskpart to detach the disk: $($_.Exception.Message)" }
        try {
            if ([IO.File]::Exists($Path)) {
                # Right after a detach the file can stay locked for a moment: try for a few seconds.
                for ($i = 0; $i -lt 10 -and [IO.File]::Exists($Path); $i++) {
                    try { [IO.File]::Delete($Path) } catch { if ($i -eq 9) { throw }; Start-Sleep -Milliseconds 500 }
                }
                Note "deleted $Path"
            }
        } catch { Warn "could not delete ${Path}: $($_.Exception.Message). In Disk Management, right-click the disk and choose Detach VHD, then delete the file." }
    }
    if ($made.Helpers) {
        foreach ($f in $attachFile, $detachFile) {
            try { if ([IO.File]::Exists($f)) { [IO.File]::Delete($f); Note "deleted $f" } }
            catch { Warn "could not delete ${f}: $($_.Exception.Message)" }
        }
    }
    foreach ($d in @($made.Folders)) {
        try { if ([IO.Directory]::Exists($d) -and -not @([IO.Directory]::GetFileSystemEntries($d)).Count) { [IO.Directory]::Delete($d); Note "removed the folder $d" } }
        catch { Warn "could not remove the folder ${d}: $($_.Exception.Message)" }
    }
}

$problem = $null
$finished = $false
$cloned = $false
try {
    # ---- 3. the virtual disk ---------------------------------------------------------------------------
    Step 'Creating the virtual disk'
    if ($missingFolders.Count) { $made.Folders = $missingFolders; [void][IO.Directory]::CreateDirectory($folder) }
    if ([IO.File]::Exists($Path)) { throw "$Path appeared while this script was starting; it was left alone" }
    Show-Lines $createLines
    Note 'If Windows asks whether to format the new drive, choose Cancel: the next step formats it.'
    $made.Disk = $true
    $run = Invoke-Diskpart $createLines
    Write-DiskpartOutput $run.Out
    if ($run.ExitCode -ne 0) { throw "diskpart could not make the disk (exit code $($run.ExitCode); its own messages are above)" }

    # Formatting is the one step that could hurt if it hit the wrong volume, so before it: drive X: must be
    # a partition on the disk that Windows says is our file. If Windows will not say which disk the file is
    # (Get-DiskImage missing or silent), X: is accepted only on a virtual disk. Either way the disk must
    # have exactly the size asked for and must not be the disk Windows runs from.
    $diskNumber = -1; $partition = $null; $byLetter = $false
    for ($i = 0; $i -lt 40 -and -not $partition; $i++) {
        if ($i) { Start-Sleep -Milliseconds 500 }
        $byLetter = $false
        $diskNumber = Get-VdiskNumber $Path
        if ($diskNumber -lt 0) {
            $candidate = Get-Partition -DriveLetter $Letter -ErrorAction SilentlyContinue
            if ($candidate) { $diskNumber = [int]$candidate.DiskNumber; $byLetter = $true }
        }
        if ($diskNumber -ge 0) {
            $partition = @(Get-Partition -DiskNumber $diskNumber -ErrorAction SilentlyContinue | Where-Object { [string]$_.DriveLetter -eq $Letter }) | Select-Object -First 1
        }
    }
    if (-not $partition) { throw "the new disk did not show up as drive ${Letter}: (nothing was formatted)" }
    $disk = Get-Disk -Number $diskNumber -ErrorAction Stop
    $sizeOff = [math]::Abs([double]$disk.Size - [double]$maxMB * 1MB)
    if ($disk.IsBoot -or $disk.IsSystem -or $sizeOff -gt 16MB -or ($byLetter -and "$($disk.BusType)" -notmatch 'Virtual')) {
        throw "drive ${Letter}: is on disk $diskNumber ($($disk.FriendlyName), $([math]::Round($disk.Size / 1GB, 1)) GB), which does not look like the virtual disk made just now. Nothing was formatted."
    }
    Note "the new disk is disk $diskNumber, with one partition as ${Letter}:"

    # ---- 4. format -------------------------------------------------------------------------------------
    Step "Formatting ${Letter}: as a Dev Drive"
    Show-Lines @($formatText)
    Format-Volume -DriveLetter $Letter -DevDrive -FileSystem ReFS -NewFileSystemLabel $Label -Confirm:$false -ErrorAction Stop | Out-Null
    $volume = Get-Volume -DriveLetter $Letter -ErrorAction Stop
    if ("$($volume.FileSystem)" -ne 'ReFS') { throw "after formatting, ${Letter}: says its file system is '$($volume.FileSystem)', not ReFS" }
    Note "${Letter}: is ReFS, $([math]::Round($volume.Size / 1GB, 1)) GB, named $Label"

    # ---- 5. come back after a restart ------------------------------------------------------------------
    # Why a scheduled task: a virtual disk attached with diskpart (or Disk Management) stays attached only
    # until Windows shuts down, and nothing attaches it again at the next start. diskpart has no option for
    # that. Windows has an attach-at-boot flag in its virtual disk API (virtdisk.h), but using it means calling
    # that API from compiled code, and how it behaves on Windows Home has not been verified; Mount-VHD is
    # part of Hyper-V, which Home does not have. A task that runs "diskpart /s <attach script>" as SYSTEM
    # when Windows starts needs nothing extra, runs before anybody signs in, can be seen and switched off in
    # Task Scheduler, and is undone by deleting it. Windows remembers the letter it gave a volume, so the
    # disk is expected to come back as the same drive; tests\dev_drive_spike.ps1 checks exactly that.
    # The task may start on battery (the default would leave a laptop without its Dev Drive).
    Step 'Making the drive come back after a restart'
    $made.Helpers = $true
    [IO.File]::WriteAllLines($attachFile, $attachLines, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllLines($detachFile, $detachLines, [Text.Encoding]::ASCII)
    Protect-HelperFile $attachFile
    Note "wrote $attachFile and $detachFile"
    $action = New-ScheduledTaskAction -Execute $diskpart -Argument $taskArguments
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest   # SYSTEM, by its language-neutral id
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -ExecutionTimeLimit '00:10:00' -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskPath $CowTaskPath -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description $taskDescription -ErrorAction Stop | Out-Null
    $made.Task = $true
    Note "registered the task $CowTaskPath$taskName (at every start of Windows, as SYSTEM): $diskpart $taskArguments"

    # ---- 6. block-clone test ---------------------------------------------------------------------------
    Step 'Testing block cloning on the new drive'
    $cloned = Test-BlockClone "${Letter}:\"
    if ($cloned) { Note 'block cloning works on this volume' }
    else { Warn "copies on ${Letter}: are not block clones on this Windows build (Windows 11 24H2 or later block-clones in CopyFile). The drive works, but each worktree will take full space." }
    $finished = $true
} catch {
    $problem = $_.Exception.Message
    Write-Host "new_dev_drive: $problem" -ForegroundColor Red
} finally {
    # Also reached when the script is interrupted (Ctrl+C) halfway.
    if (-not $finished) { try { Undo-Everything } catch { Warn "removing what was made did not finish: $($_.Exception.Message)" } }
}
if ($problem) {
    Write-Host "new_dev_drive: failed: $problem" -ForegroundColor Red
    Write-Host '    What had been made was removed, except where a warning above says otherwise.'
    exit 1
}

# ---- 7. result -----------------------------------------------------------------------------------------
Step 'Done'
Note "Dev Drive ${Letter}:\ is ready (ReFS, up to $SizeGB GB). Its contents live in $Path."
Note "It is attached again at every start of Windows by the scheduled task $CowTaskPath$taskName."
Note "Use a folder on it as the pool root, for example:  setup.ps1 -Repo <your repository> -Root ${Letter}:\cow\<name>"
Step 'To remove this Dev Drive later'
Note 'In PowerShell started as administrator, in this order (the last line deletes everything stored on the drive):'
Show-Lines @(
    "Unregister-ScheduledTask -TaskPath $(Quote $CowTaskPath) -TaskName $(Quote $taskName) -Confirm:`$false",
    "diskpart /s `"$detachFile`"",
    "Remove-Item -LiteralPath $(Quote $Path), $(Quote $attachFile), $(Quote $detachFile)"
)
"${Letter}:\"
exit 0
