[Setup]
#ifdef R09Installer
AppId={{1546F5A8-74AF-4D69-B06A-BEA3E8433DAE}
AppName=Worlds Yet Unseen R09
DefaultDirName={localappdata}\Programs\Worlds Yet Unseen R09
UsePreviousAppDir=no
OutputBaseFilename=WYU-R09-{#AppVersion}-win-x64
#else
AppId={{E7123057-A9B2-46C8-A611-40C1D5504964}
AppName=Worlds Yet Unseen Alpha
DefaultDirName={localappdata}\Programs\Worlds Yet Unseen Alpha
DefaultGroupName=Worlds Yet Unseen Alpha
OutputBaseFilename=WYU-Alpha-{#AppVersion}-win-x64
#endif
AppVersion={#AppVersion}
AppPublisher=Worlds Yet Unseen
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#InstallerOutput}
UninstallDisplayIcon={app}\WorldsYetUnseen.Client.exe
SolidCompression=yes
Compression=lzma2
RestartApplications=no

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifndef R09Installer
[Icons]
Name: "{userprograms}\Worlds Yet Unseen Alpha"; Filename: "{app}\WorldsYetUnseen.Client.exe"; WorkingDir: "{app}"
#endif
