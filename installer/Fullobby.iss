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
; Gracefully close a running Fullobby (Restart Manager) before updating
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveStaleProtocolHandlers;
end;
