; CowWorktrees.iss - the installer for cow-worktrees (Inno Setup 6.3 or newer).
;
; Do not compile this by hand: release.ps1 does it and passes
;   /DVersion=<x.y.z>       the release's version
;   /DStageDir=<folder>     the folder package.ps1 staged; the zip is made from the same folder, so the
;                           installer and the zip hold the same files
;   /DTestBuild=1           only for installer\test_installer.ps1, see below
;
; What it does: copies the staged folder to %LOCALAPPDATA%\Programs\cow-worktrees (per user, no
; administrator rights, no wizard pages), adds installed.marker (how the app knows it was installed and may
; update itself), makes one Start-menu shortcut and starts the app.
;   /NOLAUNCH            do not start the app at the end
;   /NOICONS             no Start-menu shortcut
;   /DIR="<folder>"      install somewhere else
; The in-app updater runs it with /VERYSILENT /SUPPRESSMSGBOXES; the app is started again afterwards.
;
; Uninstalling first disconnects every registered repository (scripts\disconnect_all.ps1: Claude Code
; hooks, the Codex block, the daily task, the entry in pools.json), then removes the installed files.
; Worktrees, bases and %LOCALAPPDATA%\cow-worktrees stay.
;   /KEEPINTEGRATIONS    (uninstaller) remove the files only, leave every repository connected
;
; A test build (/DTestBuild=1) must leave no trace in Windows: it has its own AppId and mutex name, writes
; no uninstall entry to the registry, makes no shortcut, and refuses to run without /DIR=. Never publish one.

#ifndef Version
  #error Pass /DVersion=x.y.z (release.ps1 does this)
#endif
#ifndef StageDir
  #error Pass /DStageDir=<the folder package.ps1 staged> (release.ps1 does this)
#endif
#if !FileExists(AddBackslash(StageDir) + "app\CowWorktrees.exe")
  #error There is no app\CowWorktrees.exe in StageDir
#endif
#if FileExists(AddBackslash(StageDir) + "installed.marker")
  #error installed.marker must not be in the staged folder: the zip is made from it too
#endif

; The AppId values are fixed for good: a changed AppId is a different product to Windows, and an update
; would then install next to the old copy instead of over it.
#ifdef TestBuild
  #define AppName "CoW Worktrees (test build)"
  #define AppId "{{08F9EE69-BA4B-4CD4-B6C3-9C3B207B22B3}"
  #define AppMutexName "cow-worktrees.app.test"
  ; Not the real folder, and Setup refuses to run without /DIR= anyway (see InitializeSetup).
  #define DefaultDir "{localappdata}\Temp\cow-worktrees-test-install"
  ; A test build never shows the app's window; test_installer.ps1 ends the app it started.
  #define LaunchParameters "--hidden"
#else
  #define AppName "CoW Worktrees"
  #define AppId "{{7BF03EFD-A75C-49BC-BB93-4AEB08BF0750}"
  ; The app holds a mutex with exactly this name for as long as it runs.
  #define AppMutexName "cow-worktrees.app"
  #define DefaultDir "{localappdata}\Programs\cow-worktrees"
  #define LaunchParameters ""
#endif
; The exe's version resource takes numbers only: 1.2.3-test becomes 1.2.3.
#define NumericVersion Copy(Version, 1, Pos("-", Version + "-") - 1)
#define RepoUrl "https://github.com/elliotttate/cow-worktrees"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher=elliotttate
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
; Per user: no administrator rights and no Windows prompt, which is also what lets the app update itself.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DefaultDirName={#DefaultDir}
; No wizard pages: Setup opens on the progress bar and closes when it is done.
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
ShowLanguageDialog=no
; A running app is closed before its files are replaced: Setup and Uninstall check the app's mutex, and
; Restart Manager closes whatever else still holds one of the files. The app is started by [Run] below,
; so Restart Manager must not start it a second time.
AppMutex={#AppMutexName}
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\app\CowWorktrees.exe
SetupIconFile={#StageDir}\app\Assets\AppIcon.ico
WizardStyle=modern
OutputBaseFilename=CowWorktrees-Setup-{#Version}
; release.ps1 overrides this with /O.
OutputDir=..\dist
SolidCompression=yes
#ifdef TestBuild
; Nothing in Windows may know about a test build: no entry under ...\CurrentVersion\Uninstall, which is
; also where Setup would look up the folder of an earlier install.
CreateUninstallRegKey=no
UsePreviousAppDir=no
Compression=lzma2/fast
#else
Compression=lzma2/max
#endif

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; An empty file. It is a [Files] entry (not written by [Code]) so that it is there before [Run] starts the
; app, and so that the uninstaller removes it. Keep it last: it then exists only when everything else does.
Source: "installed.marker"; DestDir: "{app}"; Flags: ignoreversion

#ifndef TestBuild
[Icons]
Name: "{autoprograms}\CoW Worktrees"; Filename: "{app}\app\CowWorktrees.exe"; WorkingDir: "{app}\app"; Comment: "Watch and manage copy-on-write Git worktrees"; Check: WantShortcut
#endif

[Run]
; Not a "postinstall" entry: there is no last page to tick a box on, and the app must also come back after
; the silent install the in-app updater runs.
Filename: "{app}\app\CowWorktrees.exe"; Parameters: "{#LaunchParameters}"; WorkingDir: "{app}\app"; Flags: nowait; Check: WantLaunch

[Code]
const
  AppMutexName = '{#AppMutexName}';
  { How long a silent Setup waits for a running app to end, in milliseconds. }
  AppExitWait = 30000;

{ True when the command line holds the switch (for example /NOLAUNCH). Works in Setup and in Uninstall. }
function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function WantLaunch: Boolean;
begin
  Result := not HasSwitch('/NOLAUNCH');
end;

function WantShortcut: Boolean;
begin
  Result := not HasSwitch('/NOICONS');
end;

function InitializeSetup: Boolean;
var
  Waited: Integer;
begin
  Result := True;
#ifdef TestBuild
  { A test build without /DIR= would land in a folder nobody cleans up. }
  if ExpandConstant('{param:DIR}') = '' then
  begin
    Log('Test build: refusing to run without /DIR=.');
    if not WizardSilent then
      MsgBox('This is a test build of the installer. It only runs from installer\test_installer.ps1 (it needs /DIR=).', mbError, MB_OK);
    Result := False;
    Exit;
  end;
#endif
  { The in-app updater starts Setup silently and then closes the app. Setup's own check of the app's mutex
    comes right after this function and, with /SUPPRESSMSGBOXES, gives up at once when the app is still
    there. So wait here for the app to finish closing. }
  if WizardSilent then
  begin
    Waited := 0;
    while CheckForMutexes(AppMutexName) and (Waited < AppExitWait) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
    if Waited > 0 then
      Log(Format('Waited %d ms for the app to close.', [Waited]));
  end;
end;

{ Uninstall: disconnect every registered repository before the scripts are removed. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Script, PowerShell: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  if HasSwitch('/KEEPINTEGRATIONS') then
  begin
    Log('/KEEPINTEGRATIONS: leaving every repository connected.');
    Exit;
  end;
#ifdef TestBuild
  { A test build must never reach the real registry of repositories (%LOCALAPPDATA%\cow-worktrees). }
  if GetEnv('COW_WORKTREES_HOME') = '' then
  begin
    Log('Test build: COW_WORKTREES_HOME is not set, not disconnecting anything.');
    Exit;
  end;
#endif
  Script := ExpandConstant('{app}\scripts\disconnect_all.ps1');
  if not FileExists(Script) then
  begin
    Log('Not found, nothing disconnected: ' + Script);
    Exit;
  end;
  PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script + '" -Log', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('disconnect_all.ps1 ended with exit code %d.', [ResultCode]))
  else
    Log('Could not start PowerShell: ' + SysErrorMessage(ResultCode));
end;
