; The House Control installer (Inno Setup 6). Built by scripts/build.ps1:
;   ISCC.exe /DMyVersion=1.0.0 /DMyArch=x64 /DSourceDir=<publish folder> /DOutputDir=<dist> installer\HouseControl.iss
;
; It installs for the current user only (no administrator prompt) into
; %LOCALAPPDATA%\Programs\House Control, adds a Start menu entry that carries the app's
; taskbar identity (so pinning it and its jump list work), and can start it with Windows.

#ifndef MyVersion
  #define MyVersion "1.0.0"
#endif
#ifndef MyArch
  #define MyArch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-" + MyArch
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName "House Control"
#define MyAppExe "HouseControl.exe"
#define MyAppId "0xProbability.HouseControl"

[Setup]
AppId={{6B3F1E0A-7C2D-4E59-9A41-5D2C8B7F0E31}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppVerName={#MyAppName} {#MyVersion}
AppPublisher=0xProbability
AppPublisherURL=https://github.com/0xProbabillity/house-control-windows
AppSupportURL=https://github.com/0xProbabillity/house-control-windows/issues
AppUpdatesURL=https://github.com/0xProbabillity/house-control-windows/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=HouseControl-{#MyVersion}-setup-{#MyArch}
SetupIconFile=..\src\HouseControl\Assets\HouseControl.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
WizardSizePercent=100
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
#if MyArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.17763
VersionInfoVersion={#MyVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start House Control with Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The AppUserModelID ties the shortcut, the running app, its pinned taskbar button and its jump list together.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; AppUserModelID: "{#MyAppId}"; Comment: "Your home as a live 3D model"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; AppUserModelID: "{#MyAppId}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; \
  ValueData: """{app}\{#MyAppExe}"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Open House Control"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExe}"; Parameters: "--quit"; Flags: runhidden skipifdoesntexist; RunOnceId: "QuitApp"

[UninstallDelete]
; The app's own files only. Your plan and connections in %APPDATA%\HouseControl stay, in case you reinstall.
Type: filesandordirs; Name: "{localappdata}\HouseControl\WebView2"

[Code]
// A running copy keeps its files open: ask it to quit before files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\{#MyAppExe}')) then
  begin
    Exec(ExpandConstant('{app}\{#MyAppExe}'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
  Result := '';
end;

// Uninstalling also removes Start with Windows when it was switched on from the app itself.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');
end;
