#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "LocalCam"
#define AppPublisher "LocalCam contributors"
#define AppExeName "LocalCam.Desktop.exe"

[Setup]
AppId={{E6D465D9-3D15-4C2E-B8B1-41A0A0D6F8CF}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}.0
DefaultDirName={autopf}\LocalCam
DefaultGroupName=LocalCam
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=..\apps\desktop\LocalCam.Desktop\Assets\LocalCam.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
OutputDir=..\artifacts\release
OutputBaseFilename=LocalCam-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no
AppMutex=Local\LocalCam.Singleton
MinVersion=10.0.22000

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LocalCam"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\LocalCam"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Run]
Filename: "{app}\VirtualCamera\LocalCamVirtualCameraRegistrar.exe"; Parameters: "--install ""{app}\VirtualCamera\LocalCamMediaSource.dll"""; StatusMsg: "正在注册 LocalCam 虚拟摄像头…"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "启动 LocalCam"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\VirtualCamera\LocalCamVirtualCameraRegistrar.exe"; Parameters: "--remove"; RunOnceId: "RemoveLocalCamVirtualCamera"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LocalCam');
end;
