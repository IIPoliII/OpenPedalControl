; Inno Setup script for OpenPedalControl. Build it with build-installer.ps1, which stages the files below.
; Expects in installer\staging: OpenPedalControl.exe, LICENSE, README.md

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "OpenPedalControl"
#define AppExe "OpenPedalControl.exe"

[Setup]
AppId={{4F0E5B0C-9C1A-4B57-9E0B-2D7A61C0B4A1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=OpenPedalControl contributors
AppPublisherURL=https://github.com/IIPoliII/OpenPedalControl
AppSupportURL=https://github.com/IIPoliII/OpenPedalControl/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=staging\LICENSE
OutputDir=output
OutputBaseFilename=OpenPedalControl-Setup-{#AppVersion}
SetupIconFile=..\src\OpenPedalControl\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
; Installing in Program Files needs administrator rights.
#ifdef Preview
; Screenshot builds of the wizard pages only; never released.
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Never replace the exe while the app runs (it may be sitting in the tray).
AppMutex=OpenPedalControl.Instance
CloseApplications=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Types]
Name: "full"; Description: "Recommended"

[Tasks]
Name: "startup"; Description: "Start OpenPedalControl with Windows (hidden in the notification area)"; GroupDescription: "Startup (optional; saved settings work with the app closed):"; Flags: unchecked; Check: not IsUpgrade
Name: "startmenuicon"; Description: "Create a Start menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "staging\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "staging\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "staging\README.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Remove obsolete bundled files from older app installations.
Type: files; Name: "{app}\vJoySetup.exe"
Type: files; Name: "{app}\vJoy-LICENSE.txt"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; "Start with Windows" belongs to the signed-in user, not the administrator account used for setup.
Filename: "{app}\{#AppExe}"; Parameters: "--startup on"; Tasks: startup; Flags: runasoriginaluser waituntilterminated
; After an update from inside the app (silent), start it again.
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Flags: nowait runasoriginaluser; Check: WizardSilent
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent runasoriginaluser

; The uninstaller removes only the files listed above. The folder goes only when it is empty, so a folder
; shared with other files (for example C:\Tools) is never wiped.

[Code]
function IsUpgrade: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1');
end;

{ "Start with Windows" is a per-user Run value. The uninstaller runs elevated, possibly as another
  (administrator) account, so look in every loaded user hive rather than only the current one, and remove
  only entries that point at this installation. }
procedure RemoveStartupEntries;
var
  Users: TArrayOfString;
  I: Integer;
  Key, Command, App: String;
begin
  App := Lowercase(ExpandConstant('{app}\{#AppExe}'));
  if not RegGetSubkeyNames(HKU, '', Users) then Exit;
  for I := 0 to GetArrayLength(Users) - 1 do
  begin
    Key := Users[I] + '\Software\Microsoft\Windows\CurrentVersion\Run';
    if RegQueryStringValue(HKU, Key, '{#AppName}', Command) and (Pos(App, Lowercase(Command)) > 0) then
      RegDeleteValue(HKU, Key, '{#AppName}');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveStartupEntries;
end;
