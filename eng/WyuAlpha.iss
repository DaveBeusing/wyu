[Setup]
AppId={{E7123057-A9B2-46C8-A611-40C1D5504964}
AppName=Worlds Yet Unseen Alpha
AppVersion={#AppVersion}
AppPublisher=Worlds Yet Unseen
DefaultDirName={localappdata}\Programs\Worlds Yet Unseen Alpha
DefaultGroupName=Worlds Yet Unseen Alpha
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#InstallerOutput}
OutputBaseFilename=WYU-Alpha-{#AppVersion}-win-x64
UninstallDisplayIcon={app}\WorldsYetUnseen.Client.exe
SolidCompression=yes
Compression=lzma2
RestartApplications=no

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Worlds Yet Unseen Alpha"; Filename: "{app}\WorldsYetUnseen.Client.exe"; WorkingDir: "{app}"
