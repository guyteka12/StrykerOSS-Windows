#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

[Setup]
AppId={{8A6B7C2E-52D4-4B7A-9F3E-1D2C3B4A5E6F}
AppName=StrykerOSS
AppVersion={#AppVersion}
AppPublisher=StrykerOSS Project (GPL-3.0)
AppPublisherURL=https://github.com/zalexdev/strykerapp
DefaultDirName={autopf}\StrykerOSS
DefaultGroupName=StrykerOSS
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=StrykerOSS-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\StrykerOSS.exe

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Files]
Source: "..\publish\StrykerOSS.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\StrykerOSS"; Filename: "{app}\StrykerOSS.exe"
Name: "{group}\Uninstall StrykerOSS"; Filename: "{uninstallexe}"
Name: "{autodesktop}\StrykerOSS"; Filename: "{app}\StrykerOSS.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\StrykerOSS.exe"; Description: "Launch StrykerOSS"; Flags: nowait postinstall skipifsilent
