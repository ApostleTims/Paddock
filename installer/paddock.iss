; Paddock installer. Per-user: no administrator rights, installs to %LocalAppData%\Programs\Paddock.
#define AppName "Paddock"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6C1E3F52-7A4B-4D0E-9B1A-5A7D2C9E0F11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Collins
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Paddock-Setup-{#AppVersion}
SetupIconFile=..\paddock.ico
UninstallDisplayIcon={app}\Paddock.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Paddock when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\publish\Paddock.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Paddock.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Paddock.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Paddock"; ValueData: """{app}\Paddock.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Paddock.exe"; Description: "Launch Paddock"; Flags: nowait postinstall skipifsilent
