; Fullobby — Inno Setup installer (unpackaged WinUI 3, no MSIX)
;
; Build:
;   dotnet publish src/Fullobby.App -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
;   iscc installer\Fullobby.iss [/DAppVersion=x.y.z] [/DPublishDir=path]
;
; Output: installer\Output\Fullobby-Setup-<ver>.exe

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Fullobby"
#define AppPublisher "Cat Tree Gaming LLC"
#define ExeName "Fullobby.exe"
#define Scheme "fullobby"
; Tray-only start, passed by the "Start with Windows" entry. Must match Branding.MinimizedArg.
#define MinimizedArg "--minimized"
#define RepoUrl "https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows"

[Setup]
AppId={{2EEF441B-1E14-4998-837E-826A82642105}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
; Per-user install: the app must stay non-elevated (toasts, HKCU protocol keys,
; self-update without UAC). {autopf} resolves to %LOCALAPPDATA%\Programs here.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=Fullobby-Setup-{#AppVersion}
SetupIconFile=..\icons\icon.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 (19041) is the floor, matching the csproj min platform version.
; Note: Win10 does not ship the WebView2 runtime, so the Admin tab degrades to an
; install-the-runtime message there (AdminPage handles this) unless the installer
; grows a WebView2 bootstrap.
MinVersion=10.0.19041
; Gracefully close a running Fullobby (Restart Manager) before updating
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Opt-out: no "unchecked" flag, so it is pre-ticked on a first install. On a reinstall or update
; InitializeWizard re-seeds it from the HKCU Run value, so an update can never resurrect a
; "Start with Windows" the user turned off in Settings — the self-updater launches setup.exe
; interactively (UpdaterService.LaunchInstaller), so this page is shown on every update.
Name: "startup"; Description: "Start {#AppName} in the tray when I sign in to Windows"; GroupDescription: "Additional options:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Registry]
; fullobby:// deep-link protocol (OAuth callbacks).
; HKA resolves to HKCU under PrivilegesRequired=lowest.
Root: HKA; Subkey: "Software\Classes\{#Scheme}"; ValueType: string; ValueName: ""; ValueData: "URL:Fullobby Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#Scheme}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\{#Scheme}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeName},0"
Root: HKA; Subkey: "Software\Classes\{#Scheme}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""

; "Start with Windows", when the opt-out task is left ticked. Shares one entry with the Settings
; toggle, so the value name and the data must stay byte-identical to what
; Core.Platform.StartupRegistry writes — including the {#MinimizedArg} flag, which starts the app
; in the tray instead of opening a window at sign-in (Branding.MinimizedArg / App.OnLaunched).
; UpdatePathIfNeeded compares the whole string and rewrites anything that differs, so a mismatch
; here means the app quietly overwrites setup's entry on first launch. SettingsPage reads the value
; back to position its toggle. HKCU (not HKA) because StartupRegistry always writes the per-user
; key. Removal when the box is cleared is in [Code] — a [Registry] entry can only add.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#ExeName}"" {#MinimizedArg}"; Tasks: startup

[Run]
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
{ The app self-registers fullobby:// at runtime via the Windows App SDK
  (ActivationRegistrationManager), which mints a ProgId keyed to the exe path under
  HKCU\Software\Classes\App.<hash>.Protocol. Inno's [Registry] uninsdeletekey only removes the
  scheme key, not these ProgIds — so uninstalling would leave dead "Fullobby" handlers behind
  that keep showing in the fullobby:// "open with" picker. Remove every App.*.Protocol whose
  open command launches our exe. }
procedure RemoveStaleProtocolHandlers;
var
  Names: TArrayOfString;
  I: Integer;
  Cmd: String;
begin
  if not RegGetSubkeyNames(HKEY_CURRENT_USER, 'Software\Classes', Names) then
    exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if (Pos('App.', Names[I]) = 1)
       and (Copy(Names[I], Length(Names[I]) - 8, 9) = '.Protocol') then
    begin
      if RegQueryStringValue(HKEY_CURRENT_USER,
           'Software\Classes\' + Names[I] + '\shell\open\command', '', Cmd) then
      begin
        if Pos(Lowercase('{#ExeName}'), Lowercase(Cmd)) > 0 then
          RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Classes\' + Names[I]);
      end;
    end;
  end;
end;

{ The auto-seed daily wake is a Windows Task Scheduler task, created at runtime by
  Core.Scheduling.ScheduledTaskService (schtasks /create /xml) and owned by Task Scheduler, not by
  us — nothing in [Files] or [Registry] can reach it. Left behind, it keeps waking the machine at
  the seed hour to run an exe that no longer exists, and if the user reinstalls it fires
  "--autoseed" at an app that is back at first-run onboarding. Delete both the current task and
  the retired per-region "Fullobby-EU" name from older builds.

  Runs non-elevated, matching how the task was created (RunLevel LeastPrivilege, current user), so
  no UAC prompt. Uninstall-only: an upgrade must keep the task, and a reinstall over a wiped config
  is healed by the app instead (AutoSeedService.RemoveOrphanedTaskAsync). }
procedure DeleteScheduledTask(const TaskName: String);
var
  ResultCode: Integer;
begin
  { ResultCode is ignored — a missing task exits non-zero, which is the normal case. Only a
    failure to launch schtasks.exe at all is worth a log line. }
  if not Exec(ExpandConstant('{sys}\schtasks.exe'),
              '/Delete /TN "' + TaskName + '" /F',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Could not run schtasks.exe to remove scheduled task ' + TaskName);
end;

procedure RemoveScheduledTasks;
var
  ResultCode, I, P: Integer;
  CsvPath, Line, TaskName: String;
  Lines: TArrayOfString;
begin
  { The wake set makes the task names dynamic — one per wake time ('{#AppName}-0600'), derived
    from each network's schedule — so a hardcoded list can't reach them. Sweep instead: list every
    task as CSV and delete root-folder tasks named exactly '{#AppName}' or '{#AppName}-...' (which
    also covers the retired '{#AppName}-EU'). Matching requires the '-' — another vendor's
    '{#AppName}Helper' must survive. Each data row looks like "\Name","Next Run Time","Status". }
  CsvPath := ExpandConstant('{tmp}\fullobby-tasks.csv');
  if Exec(ExpandConstant('{cmd}'), '/C schtasks /Query /FO CSV /NH > "' + CsvPath + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     and (ResultCode = 0) and LoadStringsFromFile(CsvPath, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Line := Lines[I];
      if Copy(Line, 1, 2) <> '"\' then Continue;   { not a data row }
      P := Pos('",', Line);
      if P < 3 then Continue;
      TaskName := Copy(Line, 3, P - 3);
      if Pos('\', TaskName) > 0 then Continue;      { task folder — ours live in the root }
      if SameText(TaskName, '{#AppName}')
         or SameText(Copy(TaskName, 1, Length('{#AppName}') + 1), '{#AppName}-') then
        DeleteScheduledTask(TaskName);
    end;
    DeleteFile(CsvPath);
  end;

  { Historic fixed names — redundant after a successful sweep, load-bearing when the listing
    fails (schtasks quirks, a redirect that couldn't be written). }
  DeleteScheduledTask('{#AppName}');
  DeleteScheduledTask('{#AppName}-EU');
end;

{ "Start with Windows" — HKCU Run value "Fullobby" holding the quoted exe path. Two writers share
  it: the [Registry] entry above (when the setup task is ticked) and Core.Platform.StartupRegistry
  at runtime (the Settings toggle). Deleted unconditionally rather than via uninsdeletevalue, which
  would only reach a value setup itself created and would leave the app-written one behind — every
  logon then tries to launch an exe uninstall just deleted. }
procedure RemoveStartupEntry;
begin
  if RegDeleteValue(HKEY_CURRENT_USER,
       'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}') then
    Log('Removed the "Start with Windows" entry');
end;

function StartupEntryExists: Boolean;
var
  Existing: String;
begin
  Result := RegQueryStringValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}', Existing);
end;

{ Whether Fullobby has been installed under this account before, keyed off setup's own
  fullobby:// class key (uninsdeletekey, so a real uninstall clears it and a later reinstall
  counts as fresh again). }
function PreviousInstallExists: Boolean;
begin
  Result := RegKeyExists(HKEY_CURRENT_USER, 'Software\Classes\{#Scheme}');
end;

procedure InitializeWizard;
begin
  { Pre-ticked on a first install (the opt-out), but on a reinstall or update the user's existing
    choice wins: if there is no Run value, they either never wanted startup or turned it off in
    Settings, and re-offering it ticked would quietly switch it back on at the next update. }
  if PreviousInstallExists and not StartupEntryExists then
    WizardSelectTasks('!startup');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { The [Registry] entry adds the value when the task is ticked; nothing there can remove it when
    it isn't. Without this, clearing the box on a reinstall would silently leave startup enabled. }
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('startup') then
    RemoveStartupEntry;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveStaleProtocolHandlers;
    RemoveScheduledTasks;
    RemoveStartupEntry;
  end;
end;
