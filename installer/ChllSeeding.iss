; CHLL Seeding — Inno Setup installer (unpackaged WinUI 3, no MSIX)
;
; Build:
;   dotnet publish src/ChllSeeding.App -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
;   iscc installer\ChllSeeding.iss [/DAppVersion=x.y.z] [/DPublishDir=path]
;
; Output: installer\Output\CHLL-Seeding-Setup-<ver>.exe

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "CHLL Seeding"
#define AppPublisher "Comp HLL"
#define ExeName "CHLLSeeding.exe"
#define Scheme "chllseeding"
#define RepoUrl "https://github.com/Cat-Tree-Gaming-L-L-C/chll-seeding-windows"

[Setup]
AppId={{8BECA68D-1736-425B-8E80-8DDB63709B4E}
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
OutputBaseFilename=CHLL-Seeding-Setup-{#AppVersion}
SetupIconFile=..\icons\icon.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Gracefully close a running CHLL Seeding (Restart Manager) before updating
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
; chllseeding:// deep-link protocol (OAuth callbacks).
; HKA resolves to HKCU under PrivilegesRequired=lowest.
Root: HKA; Subkey: "Software\Classes\{#Scheme}"; ValueType: string; ValueName: ""; ValueData: "URL:CHLL Seeding Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#Scheme}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\{#Scheme}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeName},0"
Root: HKA; Subkey: "Software\Classes\{#Scheme}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""

[Run]
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
