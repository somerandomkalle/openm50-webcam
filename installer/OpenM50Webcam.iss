; Inno Setup script for OpenM50 Webcam. Built by build.ps1 (needs Inno Setup 6).
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#define AppName "OpenM50 Webcam"
#define AppExe "OpenM50Webcam.exe"

[Setup]
AppId={{6F1E2C3A-8B4D-4E5F-9A0B-1C2D3E4F5A6B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=OpenM50 Webcam contributors
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\OpenM50 Webcam
DefaultGroupName=OpenM50 Webcam
DisableProgramGroupPage=yes
LicenseFile=license.txt
OutputDir=..\dist
OutputBaseFilename=OpenM50Webcam-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=..\assets\wizard-large.bmp,..\assets\wizard-large@2x.bmp
WizardSmallImageFile=..\assets\wizard-small.bmp,..\assets\wizard-small@2x.bmp
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
LaunchApp=Start OpenM50 Webcam now
Win10Note=Note: the webcam device "OpenM50 Webcam" requires Windows 11. On Windows 10 you can still use the camera in OBS via the built-in stream (see README).

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\OpenM50VCam.dll"; DestDir: "{app}"; Flags: ignoreversion regserver restartreplace uninsrestartdelete
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; shortcut created by the old script installer (v0.1)
Type: files; Name: "{commonprograms}\OpenM50 Webcam.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopEverything();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  { Windows Frame Server keeps the virtual camera DLL loaded; stop it so the file can be replaced.
    It starts again automatically the next time an app opens a camera. }
  Exec(ExpandConstant('{sys}\net.exe'), 'stop FrameServer /y', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopEverything();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and
     (GetWindowsVersion < $0A0055F0) then { build 22000 = Windows 11 }
    MsgBox(CustomMessage('Win10Note'), mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopEverything();
end;
