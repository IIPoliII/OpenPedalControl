; Inno Setup script for OpenPedalControl. Build it with build-installer.ps1, which stages the files below.
; Expects in installer\staging: OpenPedalControl.exe, LICENSE, README.md, vJoySetup.exe, vJoy-LICENSE.txt

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "OpenPedalControl"
#define AppExe "OpenPedalControl.exe"
#define VJoyUninstallKey "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1"

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
; vJoy is a driver, and Program Files needs administrator rights anyway.
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
Name: "vjoy"; Description: "Install vJoy (free virtual controller driver)"; GroupDescription: "Custom curves need vJoy. Without it, OpenPedalControl can only store the Windows calibration: a curve with one bend, that works with the app closed. You can also install vJoy later from the app's Settings."; Check: not IsVJoyInstalled
Name: "startup"; Description: "Start OpenPedalControl with Windows (hidden in the notification area)"; GroupDescription: "Startup (only needed for custom curves; can be changed in Settings):"; Flags: unchecked; Check: not IsUpgrade
Name: "startmenuicon"; Description: "Create a Start menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "staging\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "staging\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "staging\README.md"; DestDir: "{app}"; Flags: ignoreversion
; Kept so vJoy can also be installed later from the app's Settings.
Source: "staging\vJoySetup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "staging\vJoy-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\vJoySetup.exe"; Parameters: "/SILENT /NORESTART /SUPPRESSMSGBOXES"; StatusMsg: "Installing the vJoy driver..."; Tasks: vjoy; Flags: waituntilterminated
; "Start with Windows" belongs to the signed-in user, not the administrator account used for setup.
Filename: "{app}\{#AppExe}"; Parameters: "--startup on"; Tasks: startup; Flags: runasoriginaluser waituntilterminated
; After an update from inside the app (silent), start it again.
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Flags: nowait runasoriginaluser; Check: WizardSilent
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--startup off"; RunOnceId: "RemoveStartup"; Flags: waituntilterminated runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function IsUpgrade: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1');
end;

function IsVJoyInstalled: Boolean;
begin
#ifdef PreviewNoVJoy
  Result := False; // Preview of the page a PC without vJoy gets.
#else
  Result := RegKeyExists(HKLM64, '{#VJoyUninstallKey}') or RegKeyExists(HKLM32, '{#VJoyUninstallKey}');
#endif
end;

// When vJoy is already there its task is hidden, so say why instead of leaving the option silently missing.
procedure InitializeWizard;
var
  Note: TNewStaticText;
  Page: TNewNotebookPage;
begin
  if not IsVJoyInstalled then Exit;
  Page := WizardForm.SelectTasksPage;
  Note := TNewStaticText.Create(WizardForm);
  Note.Parent := Page;
  Note.AutoSize := False;
  Note.WordWrap := True;
  Note.Left := WizardForm.TasksList.Left;
  Note.Width := WizardForm.TasksList.Width;
  Note.Height := ScaleY(32);
  Note.Top := Page.Height - Note.Height;
  Note.Caption := 'vJoy is already installed on this PC, so custom curves are ready to use. ' +
                  'Nothing to install, and your existing vJoy setup is left as it is.';
  WizardForm.TasksList.Height := Note.Top - WizardForm.TasksList.Top - ScaleY(8);
end;
